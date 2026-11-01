using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Diagnostics of one view: wavy underlines in four severities (theme colours <c>editorError.foreground</c> …), faded text for
/// <see cref="DiagnosticTag.Unnecessary"/>, and lookup by position for hovers and the lightbulb.
///
/// Flood-proof by construction: the diagnostic service may change 100k times a second, so changes are coalesced (latest wins), converted to
/// offsets on the editor worker from a snapshot, capped at <see cref="MaxDiagnostics"/>, and applied to the UI in slices of
/// <see cref="ApplySlice"/> segments with the document version they were computed for — a stale batch is dropped, never drawn.
/// Once applied, AvaloniaEdit's anchored <see cref="TextSegmentCollection{T}"/> keeps them on the right text while the user types.
/// </summary>
internal sealed class DiagnosticsFeature : IThemeAware, IDisposable
{
    public const int MaxDiagnostics = 5000;
    private const int ApplySlice = 500;
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(60);

    internal sealed class DiagSegment : TextSegment
    {
        public required Diagnostic Diagnostic { get; init; }
    }

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly SquiggleRenderer _renderer;
    private readonly UnnecessaryFade _fade;
    private readonly object _key = new();
    private TextSegmentCollection<DiagSegment> _segments;
    private readonly CancellationTokenSource _lifetime = new();
    private int _scheduled;
    private int _generation;
    private bool _disposed;

    public DiagnosticsFeature(CodeEditorControl view)
    {
        _v = view;
        _segments = new TextSegmentCollection<DiagSegment>(view.Document.Buffer);
        _renderer = new SquiggleRenderer(this);
        _fade = new UnnecessaryFade(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        view.TextView.LineTransformers.Add(_fade);
        _d.Add(view.Services.Diagnostics.Changed.Subscribe(uris =>
        {
            if (uris.Contains(view.Document.Uri)) Schedule();
        }));
        Schedule(immediate: true);
    }

    /// <summary>Segments currently applied (diagnostics, tests).</summary>
    internal int Count => _segments.Count;

    /// <summary>Diagnostics covering <paramref name="offset"/>, most severe first.</summary>
    internal IReadOnlyList<Diagnostic> At(int offset) =>
        _segments.FindSegmentsContaining(offset).Select(s => s.Diagnostic).OrderBy(d => (int)d.Severity).ToList();

    /// <summary>
    /// Start offset of the next (<paramref name="forward"/>) or previous diagnostic after/before <paramref name="offset"/>, wrapping around the
    /// document like VS Code's Next/Previous Problem; null when there is none.
    /// </summary>
    internal int? NextMarker(int offset, bool forward)
    {
        if (_segments.Count == 0) return null;
        int? best = null, wrap = null;
        foreach (var s in _segments)
        {
            var start = s.StartOffset;
            if (forward)
            {
                if (start > offset && (best is null || start < best)) best = start;
                if (wrap is null || start < wrap) wrap = start;
            }
            else
            {
                if (start < offset && (best is null || start > best)) best = start;
                if (wrap is null || start > wrap) wrap = start;
            }
        }
        return best ?? wrap;
    }

    /// <summary>Every applied diagnostic's range and severity, in document order (the overview ruler's marks). UI thread.</summary>
    internal IEnumerable<(int Start, int End, DiagnosticSeverity Severity)> Marks => _segments.Select(s => (s.StartOffset, s.EndOffset, s.Diagnostic.Severity));

    internal IReadOnlyList<Diagnostic> Overlapping(int start, int end) =>
        _segments.FindOverlappingSegments(start, Math.Max(0, end - start)).Select(s => s.Diagnostic).OrderBy(d => (int)d.Severity).ToList();

    // ---- updating ----

    /// <summary>
    /// At most ONE run is ever pending: a source publishing 100k times a second costs an interlocked exchange per event, not a timer, a task and a
    /// token per event. The pending run reads the latest published state when it fires, so nothing is lost by not scheduling again.
    /// </summary>
    private void Schedule(bool immediate = false)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _scheduled, 1) == 1 && !immediate) return;
        _ = RunAsync(immediate ? TimeSpan.Zero : Coalesce, _lifetime.Token);
    }

    private async Task RunAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Volatile.Write(ref _scheduled, 0); // from here on a new change schedules the next run
        if (_disposed) return;
        var doc = _v.Document;
        var service = _v.Services.Diagnostics;
        var generation = Interlocked.Increment(ref _generation);
        _v.Factory.Worker.Post(WorkPriority.Background, _key, _ =>
        {
            var diagnostics = service.Get(doc.Uri);
            var snapshot = doc.Snapshot();
            var prepared = new List<(int Start, int End, Diagnostic D)>(Math.Min(diagnostics.Count, MaxDiagnostics));
            foreach (var d in diagnostics.OrderBy(d => (int)d.Severity).ThenBy(d => d.Range.Start).Take(MaxDiagnostics))
            {
                var start = snapshot.OffsetAt(d.Range.Start);
                var end = snapshot.OffsetAt(d.Range.End);
                if (end <= start) end = Math.Min(snapshot.Length, start + 1); // an empty range still gets a visible mark
                if (end <= start) start = Math.Max(0, end - 1);
                prepared.Add((start, end, d));
            }
            _v.Post(() => BeginApply(generation, snapshot.Version, prepared));
        });
    }

    private void BeginApply(int generation, int version, List<(int Start, int End, Diagnostic D)> prepared)
    {
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        if (_v.Document.Version != version)
        {
            Schedule(immediate: true); // the text moved on: these offsets are stale; recompute against the new version
            return;
        }
        var fresh = new TextSegmentCollection<DiagSegment>(_v.Document.Buffer);
        ApplySliceAt(generation, version, prepared, 0, fresh);
    }

    private void ApplySliceAt(int generation, int version, List<(int Start, int End, Diagnostic D)> prepared, int from, TextSegmentCollection<DiagSegment> into)
    {
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        if (_v.Document.Version != version)
        {
            Schedule(immediate: true);
            return;
        }
        var to = Math.Min(prepared.Count, from + ApplySlice);
        for (var i = from; i < to; i++)
        {
            var (s, e, d) = prepared[i];
            into.Add(new DiagSegment { StartOffset = s, EndOffset = e, Diagnostic = d });
        }
        if (to < prepared.Count)
        {
            // keep each UI step small (≤ 500 anchors): yield to input/render between slices
            UiJobs.Post(() => ApplySliceAt(generation, version, prepared, to, into), Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        _segments = into;
        _v.NotifyMarkersChanged(); // the overview ruler and the minimap
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
        _v.TextView.Redraw();
    }

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Selection);

    // ---- rendering ----

    private sealed class SquiggleRenderer(DiagnosticsFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            var segments = owner._segments;
            if (segments.Count == 0 || !textView.VisualLinesValid) return;
            var palette = owner._v.Palette;
            foreach (var line in textView.VisualLines)
            {
                var first = line.FirstDocumentLine;
                var last = line.LastDocumentLine;
                var start = first.Offset;
                var length = last.EndOffset - start;
                foreach (var seg in segments.FindOverlappingSegments(start, length))
                {
                    if (seg.Length <= 0) continue;
                    var pen = seg.Diagnostic.Severity switch
                    {
                        DiagnosticSeverity.Error => palette.Error,
                        DiagnosticSeverity.Warning => palette.Warning,
                        DiagnosticSeverity.Information => palette.Info,
                        _ => palette.Hint,
                    };
                    var from = Math.Max(seg.StartOffset, start);
                    var to = Math.Min(seg.EndOffset, last.EndOffset);
                    var clipped = new TextSegment { StartOffset = from, EndOffset = to };
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, clipped))
                        Squiggle(context, pen, rect, seg.Diagnostic.Severity == DiagnosticSeverity.Hint);
                }
            }
        }

        private static void Squiggle(DrawingContext context, IPen pen, Rect rect, bool dotted)
        {
            var y = rect.Bottom - 1.5;
            var x = rect.Left;
            var end = rect.Right;
            if (end - x < 2) end = x + 2;
            if (dotted)
            {
                for (var dx = x; dx < end; dx += 3) context.DrawRectangle(pen.Brush, null, new Rect(dx, y, 1.5, 1.5));
                return;
            }
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var up = true;
                g.BeginFigure(new Point(x, y + 1), false);
                for (var dx = x + 2; dx < end + 2; dx += 2, up = !up) g.LineTo(new Point(Math.Min(dx, end), y + (up ? -1 : 1)));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>"Hint"/"Unnecessary" code (unused usings, unreachable code) is drawn faded, like VS Code.</summary>
    private sealed class UnnecessaryFade(DiagnosticsFeature owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var segments = owner._segments;
            if (segments.Count == 0) return;
            foreach (var seg in segments.FindOverlappingSegments(line.Offset, line.Length))
            {
                if (seg.Diagnostic.Tags is not { } tags || !tags.Contains(DiagnosticTag.Unnecessary)) continue;
                var from = Math.Max(seg.StartOffset, line.Offset);
                var to = Math.Min(seg.EndOffset, line.EndOffset);
                if (to > from) ChangeLinePart(from, to, e => e.TextRunProperties.SetForegroundBrush(new SolidColorBrush(owner._v.Palette.DefaultForeground, 0.45)));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _lifetime.Cancel(); // (not disposed: a producer thread may race a last Schedule(); a cancelled source without timers holds nothing)
        _v.TextView.BackgroundRenderers.Remove(_renderer);
        _v.TextView.LineTransformers.Remove(_fade);
        _d.Dispose();
        _segments = new TextSegmentCollection<DiagSegment>(); // drop the anchors
    }
}
