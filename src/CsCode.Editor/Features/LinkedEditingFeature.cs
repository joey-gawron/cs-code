using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Linked editing (VS Code's <c>editor.linkedEditing</c>, off by default; <c>editor.action.linkedEditing</c>, Ctrl+Shift+F2): while the caret
/// rests in a range the language server links to others (<c>textDocument/linkedEditingRange</c>: an HTML or XML tag name and its closing tag's),
/// the ranges are outlined and an edit in one of them is mirrored into the others. The mirror joins the edit's undo step (one Ctrl+Z undoes
/// both, and a typed word stays one step), and is applied right after the edit's change event — never inside it, so every listener (the
/// language client's sync above all) sees the edit and the mirror as two ordered changes.
/// <para>
/// The session ends when an edit leaves the ranges (an edit outside them, or one that straddles a boundary), when the edited range no longer
/// matches the server's word pattern (else the language's; a space typed into a tag name, VS Code's rule), or when the caret leaves the ranges
/// (then the server is asked again where it rests). Several carets (multi-cursor) editing every range the same way keep it; an edit at several
/// places that leaves the ranges different ends it. Vim's edits are edits like any other: an Insert session's typing mirrors, and the mirror is
/// part of the Vim change's undo step.
/// </para>
/// </summary>
internal sealed class LinkedEditingFeature : IThemeAware, ISettingsAware, IDisposable
{
    public const string Setting = "editor.linkedEditing";
    public const string StartCommand = "editor.action.linkedEditing";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    internal static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(Setting, JsonSerializer.SerializeToElement(false),
            "Linked editing: editing an HTML or XML tag name (any range the language server links to others) changes the matching tag too. Editor > Start Linked Editing (Ctrl+Shift+F2) starts it once with the setting off.",
            JsonDocument.Parse("""{"type":"boolean"}""").RootElement.Clone()),
    ];

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly RangeRenderer _renderer;
    private List<(int Start, int End)>? _ranges; // document offsets; End exclusive
    private Regex? _wordPattern;
    private bool _manual; // started by the command (lives while the setting is off)
    private bool _applying;
    private int? _pendingReference;
    private bool _syncPosted;
    private CancellationTokenSource? _request;
    private System.Threading.Timer? _debounce;
    private bool _enabled, _disposed;

    public LinkedEditingFeature(CodeEditorControl view)
    {
        _v = view;
        _renderer = new RangeRenderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        view.TextArea.Caret.PositionChanged += OnCaretChanged;
        _d.Add(Disposable.Create(() =>
        {
            view.TextArea.Caret.PositionChanged -= OnCaretChanged;
            view.TextView.BackgroundRenderers.Remove(_renderer);
        }));
        _d.Add(view.Document.Changed.Subscribe(OnChanged));
        _enabled = ReadEnabled();
        OnThemeChanged();
    }

    /// <summary>The linked ranges of the current session (empty when there is none). UI thread.</summary>
    internal IReadOnlyList<TextRange> Ranges => _ranges is { } r ? r.Select(x => FeatureSupport.RangeOf(_v, x.Start, x.End)).ToList() : [];
    internal bool IsActive => _ranges is not null;
    /// <summary>Completes when the request in flight has been applied (tests).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    private bool ReadEnabled() => _v.Services.Config.GetOr(Setting, _v.Document.LanguageId, false);

    public void OnSettingsChanged()
    {
        if (_disposed) return;
        var enabled = ReadEnabled();
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (!enabled && !_manual) End();
        else if (enabled) Schedule();
    }

    public void OnThemeChanged()
    {
        var c = EditorPalette.ToColor(_v.Factory.Themes.GetColor("editor.linkedEditingBackground"));
        _renderer.Brush = new ImmutableSolidColorBrush(c);
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ------------------------------------------------------------------------------------------------ starting and ending

    /// <summary>
    /// Start Linked Editing (Ctrl+Shift+F2): asks the server for the ranges at the caret now, with the setting on or off; pressed while a
    /// session is on, ends it. Says in the status bar when there is nothing to link.
    /// </summary>
    public async Task ToggleAsync()
    {
        if (_disposed) return;
        if (_ranges is not null)
        {
            End();
            return;
        }
        if (!await RequestAsync(manual: true).ConfigureAwait(true) && !_disposed) FeatureSupport.Status(_v, "No linked ranges at the caret");
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_disposed || _applying) return;
        if (_ranges is not null && Contains(_v.Editor.CaretOffset)) return;
        if (_ranges is not null) End();
        if (_enabled && !_v.Document.IsReadOnly) Schedule();
    }

    private void Schedule()
    {
        _debounce ??= new System.Threading.Timer(_ => _v.Post(() => { if (_enabled && _ranges is null) Pending = RequestAsync(manual: false); }));
        _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Asks the first provider that answers for the ranges at the caret; a result for older text or a caret elsewhere is dropped.</summary>
    private async Task<bool> RequestAsync(bool manual)
    {
        var doc = _v.Document;
        if (doc.IsReadOnly || _v.Editor.Document is null) return false;
        var providers = _v.Services.Features.GetProviders<ILinkedEditingRangeProvider>(doc);
        if (providers.Count == 0) return false;
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var caret = _v.Editor.CaretOffset;
        var version = doc.Version;
        var position = doc.PositionAt(caret);
        foreach (var provider in providers)
        {
            var result = await FeatureSupport.CallAsync(_v, c => provider.ProvideLinkedEditingRangesAsync(doc, position, c), cts.Token, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            if (_disposed || cts.IsCancellationRequested || doc.Version != version || _v.Editor.CaretOffset != caret) return false;
            if (result is not { Ranges.Count: > 1 } ranges) continue;
            var offsets = ranges.Ranges.Select(r => (Start: doc.OffsetAt(r.Start), End: doc.OffsetAt(r.End))).Where(r => r.End >= r.Start).OrderBy(r => r.Start).ToList();
            if (offsets.Count < 2 || !offsets.Any(r => caret >= r.Start && caret <= r.End) || Overlap(offsets)) continue;
            Start(offsets, ranges.WordPattern, manual);
            return true;
        }
        return false;
    }

    private static bool Overlap(List<(int Start, int End)> sorted)
    {
        for (var i = 1; i < sorted.Count; i++) if (sorted[i].Start < sorted[i - 1].End) return true;
        return false;
    }

    private void Start(List<(int Start, int End)> ranges, string? wordPattern, bool manual)
    {
        _ranges = ranges;
        _manual = manual;
        _wordPattern = Compile(wordPattern) ?? Compile(_v.LanguageConfiguration.Base.WordPattern);
        _pendingReference = null;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>A server's word pattern is a JavaScript regex; it must match a range's whole text. Unusable patterns check nothing.</summary>
    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Ends the session (the outline goes; the next caret move may start another).</summary>
    internal void End()
    {
        _request?.Cancel();
        if (_ranges is null) return;
        _ranges = null;
        _manual = false;
        _pendingReference = null;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private bool Contains(int offset)
    {
        if (_ranges is null) return false;
        foreach (var r in _ranges) if (offset >= r.Start && offset <= r.End) return true;
        return false;
    }

    // ------------------------------------------------------------------------------------------------ following edits and mirroring

    /// <summary>
    /// Maps the ranges through the buffer's changes (an insertion at a range's edge grows it) and decides: an edit inside one range is mirrored
    /// into the others (after this event); edits that keep every range alike are fine; anything else ends the session.
    /// </summary>
    private void OnChanged(TextDocumentChangedEventArgs e)
    {
        if (_disposed || _ranges is not { } ranges) return;
        var touched = new HashSet<int>();
        foreach (var change in e.Changes)
        {
            var o = change.RangeOffset;
            var removed = change.RangeLength;
            var delta = change.Text.Length - removed;
            var inside = -1;
            for (var i = 0; i < ranges.Count; i++)
            {
                var (s, end) = ranges[i];
                if (o + removed < s || (o + removed == s && removed > 0)) { ranges[i] = (s + delta, end + delta); continue; } // before the range
                if (o > end || (o == end && removed > 0)) continue; // after it
                if (o >= s && o + removed <= end) { ranges[i] = (s, end + delta); inside = i; continue; } // inside it, or at an edge (grows)
                End(); // straddles a boundary: the edit left the ranges
                return;
            }
            if (inside < 0 && !_applying) { End(); return; } // an edit elsewhere ends linked editing (the caret is elsewhere too)
            if (inside >= 0) touched.Add(inside);
        }
        if (_applying || touched.Count == 0) return;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
        int reference;
        if (touched.Count == 1) reference = touched.First();
        else if (AllAlike(ranges)) return; // several carets edited every range the same way
        else
        {
            // edited at several places differently: the range with the primary caret leads, as long as it was edited
            var caret = _v.Editor.CaretOffset;
            reference = touched.FirstOrDefault(i => caret >= ranges[i].Start && caret <= ranges[i].End, -1);
            if (reference < 0) { End(); return; }
        }
        _pendingReference = reference;
        if (_syncPosted) return;
        _syncPosted = true;
        // Not inside the change event: listeners after this one must see the edit before the mirror. Normal priority runs before the next key.
        _v.Post(Sync);
    }

    private bool AllAlike(List<(int Start, int End)> ranges)
    {
        var first = Text(ranges[0]);
        for (var i = 1; i < ranges.Count; i++) if (Text(ranges[i]) != first) return false;
        return true;
    }

    private string Text((int Start, int End) r) => _v.Editor.Document.GetText(r.Start, r.End - r.Start);

    /// <summary>Copies the edited range's text into the other ranges, as part of the edit's undo step.</summary>
    private void Sync()
    {
        _syncPosted = false;
        if (_disposed || _ranges is not { } ranges || _pendingReference is not { } reference || reference >= ranges.Count) return;
        _pendingReference = null;
        var text = Text(ranges[reference]);
        if (_wordPattern is { } pattern && !MatchesWhole(pattern, text)) { End(); return; } // e.g. a space typed into a tag name
        var doc = _v.Document;
        if (doc.IsReadOnly) return;
        var edits = new List<TextEdit>();
        for (var i = 0; i < ranges.Count; i++)
        {
            if (i == reference) continue;
            var other = Text(ranges[i]);
            if (other == text) continue;
            // only the part that differs (VS Code: a common prefix and suffix stay), so anchors and markers in the range move as little as possible
            var prefix = 0;
            while (prefix < other.Length && prefix < text.Length && other[prefix] == text[prefix]) prefix++;
            var suffix = 0;
            while (suffix < other.Length - prefix && suffix < text.Length - prefix && other[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;
            var start = ranges[i].Start + prefix;
            var end = ranges[i].End - suffix;
            edits.Add(new TextEdit(FeatureSupport.RangeOf(_v, start, end), text.Substring(prefix, text.Length - prefix - suffix)));
        }
        if (edits.Count == 0) return;
        var typing = _v.Feature<TypingBehaviors>();
        var versionBefore = doc.Version;
        var caretBefore = _v.Editor.CaretOffset;
        _applying = true;
        doc.BeginUndoGroup(continueLast: true); // joins the edit's step: one undo takes both back
        try { doc.ApplyEdits(edits); }
        finally
        {
            doc.EndUndoGroup();
            _applying = false;
        }
        typing?.FollowEdit(versionBefore, caretBefore); // a typed run goes on across the mirror: a typed word stays one undo step
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private static bool MatchesWhole(Regex pattern, string text)
    {
        if (text.Length == 0) return true;
        try
        {
            var m = pattern.Match(text);
            return m.Success && m.Index == 0 && m.Length == text.Length;
        }
        catch (RegexMatchTimeoutException) { return true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _request?.Cancel();
        _request?.Dispose();
        _debounce?.Dispose();
        _ranges = null;
        _d.Dispose();
    }

    /// <summary>The linked ranges, outlined (VS Code's <c>editor.linkedEditingBackground</c>).</summary>
    private sealed class RangeRenderer(LinkedEditingFeature owner) : IBackgroundRenderer
    {
        public IBrush Brush { get; set; } = Brushes.Transparent;
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner._ranges is not { } ranges || textView.Document is null || !textView.VisualLinesValid) return;
            var length = textView.Document.TextLength;
            foreach (var (s, e) in ranges)
            {
                if (s > length) continue;
                var builder = new BackgroundGeometryBuilder { CornerRadius = 1, AlignToWholePixels = true };
                builder.AddSegment(textView, new TextSegment { StartOffset = s, EndOffset = Math.Min(length, e) });
                if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Brush, null, geometry);
            }
        }
    }
}
