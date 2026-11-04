using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Snippets;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// The snippet session of one view: inserts a resolved snippet as ONE undo unit, anchors every tab stop in the buffer (so they follow edits),
/// walks them with Tab / Shift+Tab, keeps mirrors in sync while the user types into the primary placeholder (applying <c>${1/…/…/}</c>
/// transforms), highlights the active stop, and ends with Esc, after the final stop, or when the caret leaves the snippet.
/// <para>
/// With several carets (multi-cursor) the snippet goes in at every caret as ONE undo step, each copy resolved for its own caret (selected text,
/// line, word, indentation; the clipboard spread one line per caret as a paste is), and the session holds one <i>instance</i> per caret: a tab
/// stop is then that stop in every instance, selected at once as carets, so typing fills them all; each instance keeps its own mirrors.
/// </para>
/// </summary>
internal sealed class SnippetFeature : ISnippetFeature, IThemeAware, IDisposable
{
    private sealed class Stop(int index, IReadOnlyList<Occurrence> occurrences, IReadOnlyList<string>? choices)
    {
        public int Index { get; } = index;
        public IReadOnlyList<Occurrence> Occurrences { get; } = occurrences;
        public IReadOnlyList<string>? Choices { get; } = choices;
        public Occurrence Primary => Occurrences.First(o => o.IsPrimary);

        /// <summary>The primary occurrence of instance <paramref name="instance"/> (one per caret the snippet went in at), if it has one.</summary>
        public Occurrence? PrimaryOf(int instance)
        {
            foreach (var o in Occurrences)
                if (o.IsPrimary && o.Instance == instance) return o;
            return null;
        }
    }

    /// <summary>One place of a tab stop: <see cref="IsPrimary"/> is the one typed into (one per instance), the others mirror it.</summary>
    private sealed record Occurrence(TextAnchor Start, TextAnchor End, bool IsPrimary, SnippetTransform? Transform, int Instance = 0);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly SnippetRenderer _renderer;
    private List<Stop>? _stops;
    private TextAnchor? _spanStart, _spanEnd;
    private List<(TextAnchor Start, TextAnchor End)>? _instanceSpans; // several carets: where each instance is (null: one instance, _spanStart/_spanEnd)
    private int _primaryInstance;
    private int _current;
    private bool _updatingMirrors;
    private bool _mirrorsPending; // typed at several carets: the mirrors follow once the carets' batch is applied
    private bool _disposed;

    public SnippetFeature(CodeEditorControl view)
    {
        _v = view;
        _renderer = new SnippetRenderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _d.Add(view.Document.Changed.Subscribe(_ => OnDocumentChanged()));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => OnCaretChanged()));
        _d.Add(Disposable.Create(() => view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown)));
        _d.Add(view.MultiCursor.BatchApplied.Subscribe(_ => OnCaretBatchApplied()));
        _d.Add(view.Factory.Snippets.Changed.Subscribe(language => view.Post(() => _ = RefreshSurroundAsync())));
        _d.Add(view.Document.LanguageIdChanged.Subscribe(document => view.Post(() => _ = RefreshSurroundAsync())));
        _ = RefreshSurroundAsync();
    }

    // ---- snippet pickers (Insert Snippet, Surround With) ----

    private volatile bool _hasSurround;

    public bool HasSurroundSnippets => _hasSurround;

    private static bool Surrounds(SnippetDefinition s) =>
        s.Body.Contains("TM_SELECTED_TEXT", StringComparison.Ordinal) || s.Body.Contains("$SELECTION", StringComparison.Ordinal) || s.Body.Contains("${SELECTION", StringComparison.Ordinal);

    // The first lookup of a language parses its snippet files: never on the UI thread (the menu reads the cached answer).
    private async Task RefreshSurroundAsync()
    {
        var language = _v.Document.LanguageId;
        var snippets = _v.Factory.Snippets;
        var has = await Task.Run(() => snippets.GetSnippets(language).Any(Surrounds)).ConfigureAwait(true);
        if (!_disposed && language == _v.Document.LanguageId) _hasSurround = has;
    }

    public async Task PickAndInsertAsync(bool surround)
    {
        if (_disposed || _v.Document.IsReadOnly || _v.Services.Window is not { } window) return;
        var language = _v.Document.LanguageId;
        var snippets = _v.Factory.Snippets;
        var all = await Task.Run(() => snippets.GetSnippets(language).Where(s => !surround || Surrounds(s)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray()).ConfigureAwait(true);
        if (_disposed) return;
        if (all.Length == 0)
        {
            FeatureSupport.Status(_v, surround ? "There are no surround snippets for this language" : "There are no snippets for this language");
            return;
        }
        var target = _v.Controller.Selections[0]; // remembered: the picker takes the focus
        var carets = _v.MultiCursor.IsActive ? _v.MultiCursor.Current : null;
        var version = _v.Document.Version;
        var items = all.Select(s => new QuickPickItem(s.Prefixes.FirstOrDefault() ?? s.Name, s.Name, s.Description, Tag: s)).ToArray();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: surround ? "Surround With" : "Insert Snippet", Placeholder: "Select a snippet", MatchOnDescription: true)).ConfigureAwait(true);
        if (_disposed || picked?.Tag is not SnippetDefinition chosen) return;
        _v.Controller.Focus();
        if (carets is not null && _v.Document.Version == version)
        {
            // several carets: the snippet goes in at each (they are put back if anything collapsed them meanwhile)
            if (!_v.MultiCursor.IsActive) _v.MultiCursor.SetCarets(carets, reveal: false);
            await InsertAtCaretsAsync(chosen.Body, null).ConfigureAwait(true);
            return;
        }
        await InsertAsync(chosen.Body, _v.Document.Version == version ? target : null).ConfigureAwait(true);
    }

    public bool IsActive => _stops is not null;

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Selection);

    // ---- insertion ----

    public void Insert(string snippetBody) => _ = InsertAsync(snippetBody, null);

    /// <summary>Replaces <paramref name="replace"/> (default: the selection / caret) with the snippet and starts the tab-stop session.</summary>
    internal async Task InsertAsync(string body, TextRange? replace)
    {
        if (_v.Document.IsReadOnly || _disposed) return;
        if (replace is null && _v.MultiCursor.IsActive)
        {
            await InsertAtCaretsAsync(body, null).ConfigureAwait(true);
            return;
        }
        var doc = _v.Document;
        var range = replace ?? _v.Controller.Selections[0];
        var clipboard = body.Contains("CLIPBOARD", StringComparison.Ordinal) ? await ReadClipboardAsync().ConfigureAwait(true) : null;
        if (_disposed) return;

        var a = doc.OffsetAt(range.Start);
        var b = doc.OffsetAt(range.End);
        var line = doc.PositionAt(a).Line;
        var lineText = doc.GetLineText(line);
        var baseIndent = lineText[..Math.Min(lineText.Length, LeadingWhitespace(lineText))];
        var languageConfig = _v.LanguageConfiguration.Base;
        var workspace = _v.Services.Provider?.GetService(typeof(IWorkspaceService)) as IWorkspaceService;
        var ctx = new SnippetVariableContext
        {
            FilePath = doc.IsUntitled ? doc.Uri.AbsolutePath : doc.Uri.LocalPath,
            WorkspaceName = workspace?.Name,
            WorkspaceFolder = workspace?.PrimaryFolder?.LocalPath,
            SelectedText = doc.GetText(range),
            CurrentLine = lineText,
            LineIndex = line,
            CurrentWord = WordAt(lineText, doc.PositionAt(a).Character),
            Clipboard = () => clipboard,
            LineComment = languageConfig.LineComment,
            BlockCommentStart = languageConfig.BlockComment?.Open,
            BlockCommentEnd = languageConfig.BlockComment?.Close,
        };
        var indentation = new SnippetIndentation(baseIndent, _v.Settings.TabSize, _v.Settings.InsertSpaces, doc.EndOfLine);
        var resolved = SnippetResolver.Resolve(body, ctx, indentation);

        End(); // a new snippet replaces an active one
        if (!doc.ApplyEdits([new TextEdit(range, resolved.Text)])) return;
        _v.Editor.CaretOffset = a + resolved.Text.Length;
        if (!resolved.HasTabstops) return;
        StartSession([(a, resolved)], primaryInstance: 0);
    }

    /// <summary>
    /// The snippet at every caret (multi-cursor), as ONE undo step: each caret's range (its selection, or what <paramref name="rangeOf"/> says:
    /// a completion's typed prefix around it) is replaced by the snippet resolved for that caret, and a session with one instance per caret starts.
    /// </summary>
    internal async Task InsertAtCaretsAsync(string body, Func<CaretRange, (int Start, int End)>? rangeOf)
    {
        if (_v.Document.IsReadOnly || _disposed) return;
        var clipboard = body.Contains("CLIPBOARD", StringComparison.Ordinal) ? await ReadClipboardAsync().ConfigureAwait(true) : null;
        if (_disposed) return;
        var doc = _v.Document;
        var buffer = doc.Buffer;
        var set = _v.MultiCursor.Current;
        // the clipboard spread one line per caret when the counts match, as a paste is (editor.multiCursorPaste "spread"; VS Code does the same)
        string[]? clipLines = null;
        if (clipboard is not null && _v.Settings.MultiCursorPaste != "full")
        {
            var lines = ClipboardTextExtensions.SplitLines(clipboard);
            if (lines.Length == set.Count) clipLines = lines;
        }
        var languageConfig = _v.LanguageConfiguration.Base;
        var workspace = _v.Services.Provider?.GetService(typeof(IWorkspaceService)) as IWorkspaceService;
        var edits = new OffsetEdit[set.Count];
        var resolved = new ResolvedSnippet[set.Count];
        var carets = new CaretRange[set.Count];
        var previousEnd = 0;
        var delta = 0;
        for (var i = 0; i < set.Count; i++)
        {
            var c = set[i];
            var (a, b) = rangeOf?.Invoke(c) ?? (c.Start, c.End);
            a = Math.Clamp(Math.Max(a, previousEnd), 0, buffer.TextLength); // the ranges of neighbouring carets never overlap
            b = Math.Clamp(Math.Max(a, b), a, buffer.TextLength);
            previousEnd = b;
            var line = buffer.GetLineByOffset(a);
            var lineText = buffer.GetText(line.Offset, line.Length);
            var index = i;
            var ctx = new SnippetVariableContext
            {
                FilePath = doc.IsUntitled ? doc.Uri.AbsolutePath : doc.Uri.LocalPath,
                WorkspaceName = workspace?.Name,
                WorkspaceFolder = workspace?.PrimaryFolder?.LocalPath,
                SelectedText = buffer.GetText(a, b - a),
                CurrentLine = lineText,
                LineIndex = line.LineNumber - 1,
                CurrentWord = WordAt(lineText, a - line.Offset),
                Clipboard = () => clipLines is not null ? clipLines[index] : clipboard,
                LineComment = languageConfig.LineComment,
                BlockCommentStart = languageConfig.BlockComment?.Open,
                BlockCommentEnd = languageConfig.BlockComment?.Close,
                CursorIndex = i,
            };
            var indentation = new SnippetIndentation(lineText[..Math.Min(lineText.Length, LeadingWhitespace(lineText))], _v.Settings.TabSize, _v.Settings.InsertSpaces, doc.EndOfLine);
            resolved[i] = SnippetResolver.Resolve(body, ctx, indentation);
            edits[i] = new OffsetEdit(a, b, resolved[i].Text);
            carets[i] = CaretRange.At(a + delta + resolved[i].Text.Length);
            delta += edits[i].Delta;
        }
        End(); // a new snippet replaces an active one
        _v.MultiCursor.ApplyBatch(set, new CaretEditBatch(edits, carets, set.PrimaryIndex));
        if (!resolved[0].HasTabstops) return;
        var starts = new (int Start, ResolvedSnippet Snippet)[set.Count];
        delta = 0;
        for (var i = 0; i < set.Count; i++)
        {
            starts[i] = (edits[i].Start + delta, resolved[i]);
            delta += edits[i].Delta;
        }
        StartSession(starts, set.PrimaryIndex);
    }

    /// <summary>Anchors the tab stops of every instance (a resolved snippet inserted at <c>Start</c>) and activates the first stop.</summary>
    private void StartSession(IReadOnlyList<(int Start, ResolvedSnippet Snippet)> instances, int primaryInstance)
    {
        var buffer = _v.Document.Buffer;
        TextAnchor Anchor(int offset, AnchorMovementType movement)
        {
            var anchor = buffer.CreateAnchor(Math.Clamp(offset, 0, buffer.TextLength));
            anchor.MovementType = movement;
            return anchor;
        }
        // every instance comes from the same snippet: its n-th group is the n-th stop of the session
        var first = instances[0].Snippet;
        var occurrences = new List<Occurrence>[first.Groups.Count];
        for (var g = 0; g < occurrences.Length; g++) occurrences[g] = new List<Occurrence>();
        var spans = new List<(TextAnchor Start, TextAnchor End)>(instances.Count);
        for (var i = 0; i < instances.Count; i++)
        {
            var (a, resolved) = instances[i];
            spans.Add((Anchor(a, AnchorMovementType.BeforeInsertion), Anchor(a + resolved.Text.Length, AnchorMovementType.AfterInsertion)));
            for (var g = 0; g < Math.Min(occurrences.Length, resolved.Groups.Count); g++)
                foreach (var r in resolved.Groups[g].Ranges)
                    occurrences[g].Add(new Occurrence(Anchor(a + r.Start, AnchorMovementType.BeforeInsertion), Anchor(a + r.Start + r.Length, AnchorMovementType.AfterInsertion), r.IsPrimary, r.Transform, i));
        }
        _spanStart = spans[0].Start;
        _spanEnd = spans[^1].End;
        _instanceSpans = instances.Count > 1 ? spans : null;
        _primaryInstance = Math.Clamp(primaryInstance, 0, instances.Count - 1);
        _stops = new List<Stop>(occurrences.Length);
        for (var g = 0; g < occurrences.Length; g++) _stops.Add(new Stop(first.Groups[g].Index, occurrences[g], first.Groups[g].Choices));
        _current = 0;
        Activate();
    }

    // ---- navigation ----

    private void Activate()
    {
        if (_stops is null) return;
        var stop = _stops[_current];
        if (_instanceSpans is { } spans)
        {
            // several instances: the stop in each, selected at once as carets (the instance of the primary caret stays primary)
            var carets = new List<CaretRange>(spans.Count);
            var primary = 0;
            for (var i = 0; i < spans.Count; i++)
            {
                if (stop.PrimaryOf(i) is not { } o || o.Start.IsDeleted || o.End.IsDeleted) continue;
                if (i == _primaryInstance) primary = carets.Count;
                carets.Add(o.Start.Offset >= o.End.Offset ? CaretRange.At(o.End.Offset) : CaretRange.Select(o.Start.Offset, o.End.Offset));
            }
            if (carets.Count > 0) _v.MultiCursor.Move(CaretSet.Create(carets, primary, _v.Settings.MultiCursorLimit));
            _v.TextView.InvalidateLayer(KnownLayer.Selection);
            if (stop.Choices is { Count: > 1 } several) _ = OfferChoicesAsync(stop, several);
            if (stop.Index == 0) End(); // the final stop ($0) ends the session, the carets staying there
            return;
        }
        var p = stop.Primary;
        var area = _v.TextArea;
        if (p.Start.Offset == p.End.Offset)
        {
            area.ClearSelection();
            _v.Editor.CaretOffset = p.End.Offset;
        }
        else
        {
            area.Selection = Selection.Create(area, p.Start.Offset, p.End.Offset);
            _v.Editor.CaretOffset = p.End.Offset;
        }
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
        if (stop.Choices is { Count: > 1 } choices) _ = OfferChoicesAsync(stop, choices);
        // the final stop ($0) ends the session
        if (stop.Index == 0) End();
    }

    private async Task OfferChoicesAsync(Stop stop, IReadOnlyList<string> choices)
    {
        var window = _v.Services.Window;
        if (window is null) return;
        var picked = await window.ShowQuickPickAsync(choices.Select(c => new QuickPickItem(c)).ToList(), new QuickPickOptions(Title: "Choose a value", Placeholder: "")).ConfigureAwait(true);
        if (picked is null || _stops is null || _disposed) return;
        if (_instanceSpans is not null)
        {
            // the choice goes into the stop of every instance, as typing would (the mirrors follow on the change)
            var choiceEdits = stop.Occurrences.Where(o => o.IsPrimary && !o.Start.IsDeleted && !o.End.IsDeleted)
                .Select(o => new TextEdit(new TextRange(_v.Document.PositionAt(o.Start.Offset), _v.Document.PositionAt(o.End.Offset)), picked.Label)).ToArray();
            if (choiceEdits.Length > 0) _v.Document.ApplyEdits(choiceEdits);
            return;
        }
        var p = stop.Primary;
        if (p.Start.IsDeleted || p.End.IsDeleted) return;
        _v.Document.ApplyEdits([new TextEdit(new TextRange(_v.Document.PositionAt(p.Start.Offset), _v.Document.PositionAt(p.End.Offset)), picked.Label)]);
    }

    private bool Move(int delta)
    {
        if (_stops is null) return false;
        var next = _current + delta;
        if (next < 0) return true; // Shift+Tab on the first stop: stay (and swallow the key)
        if (next >= _stops.Count)
        {
            End();
            return false; // past the end: let Tab behave normally
        }
        _current = next;
        Activate();
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_stops is null || _disposed) return;
        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == 0)
        {
            if (Move((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1)) e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            End();
            e.Handled = true;
        }
    }

    // ---- keeping mirrors in sync ----

    private void OnDocumentChanged()
    {
        if (_stops is null || _updatingMirrors || _disposed) return;
        if (_v.MultiCursor.IsApplyingBatch)
        {
            // typed at several carets: their batch must not be edited while it is applied; the mirrors follow right after it, in its undo step
            _mirrorsPending = true;
            return;
        }
        var edits = MirrorEdits();
        if (edits.Count == 0) return;
        var doc = _v.Document;
        _updatingMirrors = true;
        try
        {
            if (_v.MultiCursor.IsActive)
            {
                _v.MultiCursor.ApplyMapped(edits, keepSelections: true, typing: true); // the carets follow the mirrors
                return;
            }
            var caret = _v.Editor.CaretOffset;
            doc.ApplyEdits(edits.Select(e => new TextEdit(new TextRange(doc.PositionAt(e.Start), doc.PositionAt(e.End)), e.Text)).ToArray()); // inside the typing group when this change came from a keystroke: ONE Ctrl+Z
            _v.Editor.CaretOffset = Math.Min(caret, doc.Length);
        }
        finally
        {
            _updatingMirrors = false;
        }
    }

    /// <summary>A batch at several carets was applied: the mirrors it left behind are brought up to date, joined to its undo step.</summary>
    private void OnCaretBatchApplied()
    {
        if (!_mirrorsPending || _updatingMirrors) return;
        _mirrorsPending = false;
        if (_stops is null || _disposed) return;
        var edits = MirrorEdits();
        if (edits.Count == 0) return;
        _updatingMirrors = true;
        try { _v.MultiCursor.ApplyMapped(edits, keepSelections: true, typing: true); }
        finally { _updatingMirrors = false; }
    }

    /// <summary>The edits that make every mirror of the current stop show its instance's primary (transforms applied), in document order.</summary>
    private List<OffsetEdit> MirrorEdits()
    {
        var edits = new List<OffsetEdit>();
        if (_stops is null) return edits;
        var stop = _stops[_current];
        if (stop.Occurrences.Count < 2) return edits;
        var buffer = _v.Document.Buffer;
        var instances = _instanceSpans?.Count ?? 1;
        for (var i = 0; i < instances; i++)
        {
            if (stop.PrimaryOf(i) is not { } primary || primary.Start.IsDeleted || primary.End.IsDeleted || primary.End.Offset < primary.Start.Offset) continue;
            var typed = buffer.GetText(primary.Start.Offset, primary.End.Offset - primary.Start.Offset);
            foreach (var m in stop.Occurrences)
            {
                if (m.IsPrimary || m.Instance != i || m.Start.IsDeleted || m.End.IsDeleted || m.End.Offset < m.Start.Offset) continue;
                var text = m.Transform is null ? typed : SnippetTransforms.Apply(typed, m.Transform);
                if (buffer.GetText(m.Start.Offset, m.End.Offset - m.Start.Offset) != text) edits.Add(new OffsetEdit(m.Start.Offset, m.End.Offset, text));
            }
        }
        edits.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : x.End.CompareTo(y.End));
        return edits;
    }

    private void OnCaretChanged()
    {
        if (_stops is null || _spanStart is null || _spanEnd is null || _updatingMirrors) return;
        var caret = _v.Editor.CaretOffset;
        var inside = _instanceSpans is { } spans ? spans.Any(s => caret >= s.Start.Offset && caret <= s.End.Offset) : caret >= _spanStart.Offset && caret <= _spanEnd.Offset;
        if (!inside) End(); // left the snippet: the session is over
        else _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    internal void End()
    {
        if (_stops is null) return;
        _stops = null;
        _spanStart = _spanEnd = null;
        _instanceSpans = null;
        _mirrorsPending = false;
        if (!_disposed) _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ---- highlight of the active stop ----

    private IEnumerable<(int Start, int End, bool Active)> HighlightRanges()
    {
        if (_stops is null) yield break;
        for (var i = 0; i < _stops.Count; i++)
        {
            var stop = _stops[i];
            if (stop.Index == 0) continue;
            foreach (var o in stop.Occurrences)
                if (!o.Start.IsDeleted && !o.End.IsDeleted && o.End.Offset >= o.Start.Offset) yield return (o.Start.Offset, o.End.Offset, i == _current);
        }
    }

    private sealed class SnippetRenderer(SnippetFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!owner.IsActive) return;
            var fill = owner._v.Palette.SnippetTabstop;
            var pen = new Pen(owner._v.Palette.WidgetBorder, 1);
            foreach (var (start, end, active) in owner.HighlightRanges())
            {
                var segment = new TextSegment { StartOffset = start, EndOffset = end };
                var any = false;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    any = true;
                    context.DrawRectangle(active ? fill : null, active ? null : pen, rect);
                }
                if (!any && start == end)
                {
                    var at = new AvaloniaEdit.TextViewPosition(textView.Document.GetLocation(start));
                    var p = textView.GetVisualPosition(at, VisualYPosition.LineTop) - textView.ScrollOffset;
                    context.DrawRectangle(fill, null, new Rect(p.X, p.Y + LineZones.ZoneOffset(textView, at), 2, textView.DefaultLineHeight));
                }
            }
        }
    }

    private static int LeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        return i;
    }

    private static string WordAt(string line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);
        var a = col;
        var b = col;
        while (a > 0 && (char.IsLetterOrDigit(line[a - 1]) || line[a - 1] == '_')) a--;
        while (b < line.Length && (char.IsLetterOrDigit(line[b]) || line[b] == '_')) b++;
        return line[a..b];
    }

    private async Task<string?> ReadClipboardAsync()
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(_v);
        if (top?.Clipboard is not { } clipboard) return null;
        try
        {
            return await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null; // the clipboard may be unavailable (headless, remote session): an empty variable, not a failed insert
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        End();
        _v.TextView.BackgroundRenderers.Remove(_renderer);
        _d.Dispose();
    }
}
