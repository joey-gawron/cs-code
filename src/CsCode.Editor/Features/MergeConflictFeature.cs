using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.View;
using CsCode.Editor.View.Decorations;

namespace CsCode.Editor.Features;

/// <summary>
/// Inline merge conflicts in one view (VS Code's merge-conflict decorations): the lines of each <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> ...
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> block tinted with <c>merge.currentHeaderBackground</c> / <c>currentContentBackground</c>, the common
/// ancestors' (diff3) with <c>merge.commonHeaderBackground</c> / <c>commonContentBackground</c>, the incoming side's with
/// <c>merge.incomingContentBackground</c> / <c>incomingHeaderBackground</c>, "(Current Change)" and "(Incoming Change)" after the marker lines,
/// and marks on the overview ruler (<c>editorOverviewRuler.currentContentForeground</c>, <c>incomingContentForeground</c>,
/// <c>commonContentForeground</c>). The blocks come from <see cref="MergeConflictTracker"/>: asked <see cref="Delay"/> after the text stops
/// changing (on the editor worker, per version; a stale answer is dropped); between answers the shown lines follow line insertions and
/// deletions, so the tint does not jump while typing. <c>merge-conflict.decorators.enabled</c> false turns tint, labels and ruler marks off; the
/// blocks are still known to the commands and the context keys.
/// </summary>
internal sealed class MergeConflictFeature : IThemeAware, IDisposable
{
    public const string DecoratorsSetting = "merge-conflict.decorators.enabled";
    public const string AtCursorKey = "mergeConflictAtCursor";
    public const string CountKey = "mergeConflictsCount";

    internal static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The lines of one block as shown (they move with line insertions and deletions until the next scan).</summary>
    internal struct Block
    {
        public int Start, Splitter, End;
        public int[] Ancestors; // header lines of the common-ancestor sections (diff3), possibly empty

        public readonly int CurrentEnd => Ancestors.Length > 0 ? Ancestors[0] : Splitter; // exclusive

        public readonly MergeConflictPart PartAt(int line)
        {
            if (line < Start || line > End) return MergeConflictPart.None;
            if (line == Start) return MergeConflictPart.CurrentHeader;
            if (line == End) return MergeConflictPart.IncomingFooter;
            if (line == Splitter) return MergeConflictPart.Splitter;
            if (line > Splitter) return MergeConflictPart.Incoming;
            return line >= CurrentEnd ? MergeConflictPart.CommonAncestors : MergeConflictPart.Current;
        }
    }

    private readonly CodeEditorControl _v;
    private readonly MergeConflictTracker _tracker;
    private readonly DisposableStore _d = new();
    private readonly Renderer _renderer;
    private Block[] _blocks = [];
    private MergeConflictSet? _set;
    private Brushes _brushes;
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _enabled, _disposed;

    public MergeConflictFeature(CodeEditorControl view, MergeConflictTracker tracker)
    {
        _v = view;
        _tracker = tracker;
        _brushes = Brushes.From(view);
        _enabled = view.Services.Config.Get<bool?>(DecoratorsSetting) ?? true;
        _renderer = new Renderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        _d.Add(Disposable.Create(() => view.TextView.BackgroundRenderers.Remove(_renderer)));
        _d.Add(view.Document.BeforeChange.Subscribe(OnBeforeChange));
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule(Delay)));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => { if (IsActive) UpdateContextKeys(); }));
        _d.Add(view.Services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects(DecoratorsSetting)) view.Post(OnDecoratorsSettingChanged);
        }));
        Schedule(TimeSpan.Zero);
    }

    /// <summary>Whether a document gets inline conflict support: not the diff and merge editors' read-only panes, which compare texts.</summary>
    public static bool AppliesTo(EditorDocument doc) => doc.Uri.Scheme is not ("cscode-diff" or "cscode-merge");

    /// <summary>The blocks as last scanned (null before the first scan); the version they belong to is <see cref="MergeConflictSet.Version"/>.</summary>
    internal MergeConflictSet? Set => _set;

    /// <summary>The blocks' lines as shown now.</summary>
    internal IReadOnlyList<Block> Blocks => _blocks;

    internal bool DecorationsEnabled => _enabled;

    /// <summary>Completes when the scan in flight has been applied (tests).</summary>
    internal Task Ready { get; private set; } = Task.CompletedTask;

    private bool IsActive => ReferenceEquals(_v.Factory.ActiveView, _v);

    /// <summary>The block whose lines include <paramref name="line"/> (0-based), as shown now; -1 when none.</summary>
    internal int IndexAt(int line)
    {
        int lo = 0, hi = _blocks.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var b = ref _blocks[mid];
            if (line < b.Start) hi = mid - 1;
            else if (line > b.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>Whether the caret is in a conflict block (context key <c>mergeConflictAtCursor</c>).</summary>
    internal bool AtCursor => IndexAt(_v.TextArea.Caret.Line - 1) >= 0;

    /// <summary>Sets <c>mergeConflictAtCursor</c> and <c>mergeConflictsCount</c> for this view (its focus, its menu, caret moves while it is active).</summary>
    internal void UpdateContextKeys()
    {
        if (_v.Services.ContextKeys is not { } keys) return;
        keys.Set(AtCursorKey, AtCursor);
        keys.Set(CountKey, _blocks.Length);
    }

    public void OnThemeChanged()
    {
        _brushes = Brushes.From(_v);
        _v.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void OnDecoratorsSettingChanged()
    {
        var on = _v.Services.Config.Get<bool?>(DecoratorsSetting) ?? true;
        if (on == _enabled) return;
        _enabled = on;
        _v.TextView.InvalidateLayer(KnownLayer.Background);
        _v.NotifyMarkersChanged(ScrollDecorations.Sources.Merge);
    }

    // ---- scanning ----

    private void Schedule(TimeSpan delay)
    {
        if (_disposed) return;
        if (delay == TimeSpan.Zero) { _v.Post(() => Ready = RefreshAsync()); return; }
        _timer ??= new System.Threading.Timer(_ => _v.Post(() => Ready = RefreshAsync()));
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        MergeConflictSet set;
        try
        {
            set = await _tracker.GetAsync(_v.Document, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        if (_disposed || cts.IsCancellationRequested) return;
        if (set.Version != _v.Document.Version) return; // the text moved on: its own scan is scheduled
        Apply(set);
    }

    /// <summary>Shows the blocks of <paramref name="set"/> (UI thread).</summary>
    internal void Apply(MergeConflictSet set)
    {
        var had = _blocks.Length > 0;
        _set = set;
        var blocks = new Block[set.Conflicts.Count];
        for (var i = 0; i < blocks.Length; i++)
        {
            var c = set.Conflicts[i];
            var ancestors = new int[c.CommonAncestors.Count];
            for (var k = 0; k < ancestors.Length; k++) ancestors[k] = c.CommonAncestors[k].HeaderLine;
            blocks[i] = new Block { Start = c.StartLine, Splitter = c.SplitterLine, End = c.EndLine, Ancestors = ancestors };
        }
        _blocks = blocks;
        if (had || blocks.Length > 0)
        {
            _v.TextView.InvalidateLayer(KnownLayer.Background);
            _v.NotifyMarkersChanged(ScrollDecorations.Sources.Merge);
        }
        if (IsActive) UpdateContextKeys();
    }

    /// <summary>Moves the shown lines with an edit that adds or removes lines (the next scan replaces them).</summary>
    private void OnBeforeChange(TextContentChange change)
    {
        if (_blocks.Length == 0) return;
        var removed = change.Range.End.Line - change.Range.Start.Line;
        var added = 0;
        foreach (var ch in change.Text.AsSpan())
            if (ch == '\n') added++;
        added += CountLoneCr(change.Text);
        var delta = added - removed;
        if (delta == 0) return;
        var at = change.Range.Start.Line;
        // lines after the edit move; so does the edit's own line when lines are inserted at its very start (it is pushed down whole)
        var startOfLine = change.Range.Start.Character == 0 && delta > 0;
        int Shift(int line) => line > at || line == at && startOfLine ? Math.Max(at, line + delta) : line;
        for (var i = 0; i < _blocks.Length; i++)
        {
            ref var b = ref _blocks[i];
            b.Start = Shift(b.Start);
            b.Splitter = Shift(b.Splitter);
            b.End = Shift(b.End);
            for (var k = 0; k < b.Ancestors.Length; k++) b.Ancestors[k] = Shift(b.Ancestors[k]);
        }
    }

    private static int CountLoneCr(string text)
    {
        var n = 0;
        for (var i = text.IndexOf('\r'); i >= 0; i = text.IndexOf('\r', i + 1))
            if (i + 1 >= text.Length || text[i + 1] != '\n') n++;
        return n;
    }

    /// <summary>The ruler's marks: each side's header and content (lowest priority first; the ruler draws them across all lanes, as VS Code does).</summary>
    internal IEnumerable<LineMarker> Marks()
    {
        if (!_enabled) yield break;
        foreach (var b in _blocks)
        {
            yield return new LineMarker(b.Start, Math.Max(b.Start, b.CurrentEnd - 1), MarkerKind.MergeCurrent);
            if (b.Ancestors.Length > 0) yield return new LineMarker(b.Ancestors[0], Math.Max(b.Ancestors[0], b.Splitter - 1), MarkerKind.MergeCommon);
            yield return new LineMarker(Math.Min(b.Splitter + 1, b.End), b.End, MarkerKind.MergeIncoming);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
        _d.Dispose();
        _blocks = [];
        _set = null;
    }

    // ---- drawing ----

    private sealed class Brushes
    {
        public required IBrush CurrentHeader, CurrentContent, IncomingHeader, IncomingContent, CommonHeader, CommonContent, Label;
        public required IPen? Border;

        public static Brushes From(CodeEditorControl v)
        {
            var themes = v.Factory.Themes;
            IBrush B(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key)));
            var border = themes.GetColor("merge.border");
            return new Brushes
            {
                CurrentHeader = B("merge.currentHeaderBackground"),
                CurrentContent = B("merge.currentContentBackground"),
                IncomingHeader = B("merge.incomingHeaderBackground"),
                IncomingContent = B("merge.incomingContentBackground"),
                CommonHeader = B("merge.commonHeaderBackground"),
                CommonContent = B("merge.commonContentBackground"),
                Label = v.Palette.DescriptionForeground,
                // high contrast: VS Code outlines the marker lines in merge.border
                Border = border.A == 0 ? null : new ImmutablePen(new ImmutableSolidColorBrush(EditorPalette.ToColor(border)), 1),
            };
        }
    }

    private sealed class Renderer(MergeConflictFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            var blocks = owner._blocks;
            if (!owner._enabled || blocks.Length == 0 || textView.Document is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            var b = owner._brushes;
            var scroll = textView.ScrollOffset;
            var width = textView.Bounds.Width + scroll.X;
            var first = textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
            // the first block that can be on screen: binary search on the end line
            int lo = 0, hi = blocks.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (blocks[mid].End < first) lo = mid + 1; else hi = mid;
            }
            var index = lo;
            foreach (var vl in textView.VisualLines)
            {
                var line = vl.FirstDocumentLine.LineNumber - 1;
                while (index < blocks.Length && blocks[index].End < line) index++;
                if (index >= blocks.Length) break;
                var part = blocks[index].PartAt(line);
                if (part == MergeConflictPart.None) continue;
                var zone = LineZones.HeightOf(vl); // a code lens above the header line: its zone is not tinted
                var rect = new Rect(0, vl.VisualTop - scroll.Y + zone, width, Math.Max(0, vl.Height - zone));
                var brush = part switch
                {
                    MergeConflictPart.CurrentHeader => b.CurrentHeader,
                    MergeConflictPart.Current => b.CurrentContent,
                    MergeConflictPart.CommonAncestors => blocks[index].Ancestors.Contains(line) ? b.CommonHeader : b.CommonContent,
                    MergeConflictPart.Incoming => b.IncomingContent,
                    MergeConflictPart.IncomingFooter => b.IncomingHeader,
                    _ => null,
                };
                if (brush is not null) context.FillRectangle(brush, rect);
                var isMarker = part is MergeConflictPart.CurrentHeader or MergeConflictPart.IncomingFooter or MergeConflictPart.Splitter
                    || part == MergeConflictPart.CommonAncestors && blocks[index].Ancestors.Contains(line);
                if (isMarker && b.Border is { } pen) context.DrawRectangle(pen, rect.Deflate(0.5));
                if (part is MergeConflictPart.CurrentHeader or MergeConflictPart.IncomingFooter)
                {
                    var label = part == MergeConflictPart.CurrentHeader ? "(Current Change)" : "(Incoming Change)";
                    var end = vl.GetVisualPosition(vl.VisualLengthWithEndOfLineMarker, VisualYPosition.LineTop);
                    var x = end.X - scroll.X + textView.WideSpaceWidth;
                    var y = end.Y + LineZones.ZoneOffset(vl, vl.VisualLengthWithEndOfLineMarker, true) - scroll.Y;
                    var editor = owner._v.Editor;
                    var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(editor.FontFamily), editor.FontSize, b.Label);
                    context.DrawText(ft, new Point(x, y + (textView.DefaultLineHeight - ft.Height) / 2));
                }
            }
        }
    }
}
