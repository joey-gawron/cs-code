using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Features;

namespace CsCode.Editor.View;

/// <summary>
/// Clipboard behaviour of one view, replacing AvaloniaEdit's own Cut/Copy/Paste so they behave like Visual Studio (see
/// <see cref="ClipboardOperations"/>): whole-line copy/cut on a caret, line paste above the caret's line, box (column) copy/paste, one line per
/// caret, EOL conversion, auto-indent and format on paste, one undo step per paste, the clipboard ring (Ctrl+Shift+V cycles in place). On Linux
/// the selection is offered as PRIMARY and a middle-click pastes PRIMARY at the click (<c>editor.selectionClipboard</c>). A right-click moves the
/// caret to the click unless it lands inside the selection, as in Visual Studio.
/// <para>
/// Keys: Ctrl+C / Ctrl+Insert copy, Ctrl+X / Shift+Delete cut, Ctrl+V / Shift+Insert paste, Ctrl+Shift+V cycles the ring. The shell's keymap
/// normally runs these as commands (the same methods) before the key reaches the view; the handler here covers a view hosted without a keymap
/// and a user who removed the binding. Clipboard reads are awaited, never blocked on: the UI thread keeps running while the owner answers.
/// </para>
/// </summary>
internal sealed class EditorClipboard : IDisposable
{
    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private (int Index, int Start, int End, int Version)? _ring;
    private bool _primaryScheduled, _disposed;

    public EditorClipboard(CodeEditorControl view)
    {
        _v = view;
        var area = view.TextArea;
        area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        area.SelectionChanged += OnSelectionChanged;
        _d.Add(Disposable.Create(() =>
        {
            area.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            area.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            area.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            area.SelectionChanged -= OnSelectionChanged;
        }));
    }

    private IClipboardService Clipboard => _v.Factory.Clipboard;

    // ------------------------------------------------------------------ selection model

    /// <summary>The current selection as the clipboard operations see it: a stream selection or caret, or a box selection's rows.</summary>
    internal SelectionSet CurrentSelection()
    {
        var area = _v.TextArea;
        if (area.Selection is RectangleSelection rect)
        {
            var rows = rect.Segments.Select(s => new TextRange(_v.PositionOf(s.StartOffset), _v.PositionOf(s.EndOffset))).OrderBy(r => r.Start).ToArray();
            if (rows.Length > 0) return new SelectionSet(rows, IsBlock: true);
        }
        var editor = _v.Editor;
        if (editor.SelectionLength == 0) return SelectionSet.Single(TextRange.At(_v.Controller.Caret.Line, _v.Controller.Caret.Character));
        return SelectionSet.Single(new TextRange(_v.PositionOf(editor.SelectionStart), _v.PositionOf(editor.SelectionStart + editor.SelectionLength)));
    }

    internal bool HasSelection => !_v.TextArea.Selection.IsEmpty;
    internal bool CanCut => !_v.Document.IsReadOnly && (HasSelection || _v.Settings.EmptySelectionClipboard);
    internal bool CanCopy => HasSelection || _v.Settings.EmptySelectionClipboard;
    internal bool CanPaste => !_v.Document.IsReadOnly;

    // ------------------------------------------------------------------ commands

    public Task CopyAsync() => CopyAsync(withFormatting: false);

    /// <summary>
    /// Copy, or Copy with Formatting: the same text, plus an HTML rendition with the theme's syntax colours (<see cref="HtmlClipboard"/>) that the
    /// clipboard service offers as <c>text/html</c>. The UI thread only takes the copied lines and their published token rows (no tokenising: a
    /// line the highlighter has not reached yet is plain); styles are resolved and the HTML written on a worker. Above
    /// <see cref="HtmlClipboard.MaxChars"/> characters only the text is copied.
    /// </summary>
    public async Task CopyAsync(bool withFormatting)
    {
        var selection = CurrentSelection();
        if (_disposed || ClipboardOperations.Copy(_v.Document, selection, _v.GetEditingContext(), _v.Settings.EmptySelectionClipboard) is not { } clip) return;
        if (withFormatting && clip.Text.Length <= HtmlClipboard.MaxChars)
        {
            var capture = CaptureForHtml(selection);
            var html = await Task.Run(capture.Build).ConfigureAwait(true);
            if (_disposed) return;
            clip = clip with { Html = html };
        }
        await Clipboard.WriteAsync(clip).ConfigureAwait(true);
    }

    /// <summary>The copied lines (text, start column, the published token row) and the theme, taken on the UI thread; cheap (at most 64K characters).</summary>
    private HtmlCapture CaptureForHtml(SelectionSet selection)
    {
        var doc = _v.Document;
        var store = _v.Tokens?.Store;
        var lines = new List<(string Text, int StartColumn, int[]? Tokens)>();
        void Add(int line, int from, int to)
        {
            var text = doc.GetLineText(line);
            from = Math.Clamp(from, 0, text.Length);
            to = Math.Clamp(to, from, text.Length);
            lines.Add((text[from..to], from, store?.GetTokens(line)));
        }
        if (selection.IsBlock)
            foreach (var row in selection.Ranges) Add(row.Start.Line, row.Start.Character, row.End.Character);
        else if (selection.Ranges.Count > 0 && selection.Ranges[0] is var range)
        {
            if (range.IsEmpty) Add(range.Start.Line, 0, int.MaxValue); // the whole line (editor.emptySelectionClipboard)
            else
                for (var l = range.Start.Line; l <= range.End.Line; l++)
                {
                    if (l == range.End.Line && range.End.Character == 0 && l > range.Start.Line) break; // ends at a line break
                    Add(l, l == range.Start.Line ? range.Start.Character : 0, l == range.End.Line ? range.End.Character : int.MaxValue);
                }
        }
        var themes = _v.Factory.Themes;
        var settings = _v.Settings;
        var style = new HtmlClipboardStyle(themes.GetColor("editor.background"), themes.GetColor("editor.foreground"), _v.Editor.FontFamily.ToString(),
            settings.FontSize, Math.Round(_v.TextArea.TextView.DefaultLineHeight, 2), settings.TabSize);
        return new HtmlCapture(lines, themes.Current, style, _v.Tokens?.Interner);
    }

    private sealed record HtmlCapture(List<(string Text, int StartColumn, int[]? Tokens)> Lines, Core.Theming.ColorTheme Theme, HtmlClipboardStyle Style,
        TextMate.ScopeInterner? Interner)
    {
        /// <summary>Worker: resolves each token's style (cached per scope stack) and writes the fragment.</summary>
        public string Build()
        {
            var resolver = new Theming.TokenColorResolver(Theme, Style.Foreground);
            var styles = new Dictionary<int, Theming.ResolvedTokenStyle>();
            var result = new List<HtmlClipboardLine>(Lines.Count);
            foreach (var (text, startColumn, tokens) in Lines)
            {
                var runs = new List<HtmlClipboardRun>();
                if (tokens is not null && Interner is not null)
                {
                    var count = tokens.Length / 2;
                    for (var i = 0; i < count; i++)
                    {
                        var from = tokens[i * 2] - startColumn;
                        var to = (i + 1 < count ? tokens[(i + 1) * 2] : int.MaxValue / 2) - startColumn;
                        from = Math.Max(0, from);
                        to = Math.Min(text.Length, to);
                        if (to <= from) continue;
                        var scope = tokens[i * 2 + 1];
                        if (!styles.TryGetValue(scope, out var s)) styles[scope] = s = resolver.Resolve(Interner.Scopes(scope));
                        var plain = s.IsPlain || (s.Foreground == Style.Foreground && s.Background is null && !s.Bold && !s.Italic && !s.Underline && !s.Strikethrough);
                        if (!plain) runs.Add(new HtmlClipboardRun(from, to - from, s));
                    }
                }
                result.Add(new HtmlClipboardLine(text, runs));
            }
            return HtmlClipboard.Build(result, Style);
        }
    }

    public async Task CutAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var selection = CurrentSelection();
        var empty = _v.Settings.EmptySelectionClipboard;
        if (ClipboardOperations.Copy(_v.Document, selection, _v.GetEditingContext(), empty) is not { } clip) return;
        if (ClipboardOperations.Cut(_v.Document, selection, empty) is not { } plan) return;
        // The text is put on the clipboard first (it is already in hand), then removed: the edit does not wait for the clipboard.
        var write = Clipboard.WriteAsync(clip);
        _v.ApplyPlan(plan);
        await write.ConfigureAwait(true);
    }

    public async Task PasteAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var clip = await Clipboard.ReadAsync().ConfigureAwait(true);
        if (clip is null || _disposed || _v.IsDisposed) return;
        Paste(clip, formatAfter: true);
        _ring = null;
    }

    /// <summary>Pastes <paramref name="clip"/> over the current selection as one undo unit; returns where it landed (single caret) or null.</summary>
    internal TextRange? Paste(ClipboardText clip, bool formatAfter)
    {
        if (_v.Document.IsReadOnly) return null;
        var result = ClipboardOperations.Paste(_v.Document, CurrentSelection(), clip, _v.GetEditingContext(), _v.Settings.AutoIndentOnPaste && clip.Text.Length < LargePaste);
        if (result is null) return null;
        _v.ApplyPlan(result.Plan);
        if (result.Inserted is { } inserted)
        {
            _v.Controller.Reveal(new TextRange(inserted.End, inserted.End), center: false);
            if (formatAfter) _v.Feature<IFormattingFeature>()?.OnPasted(inserted);
        }
        return result.Inserted;
    }

    /// <summary>Above this size a paste is inserted as it is (line endings converted, no re-indent), so it never runs heuristics over megabytes on the UI thread.</summary>
    internal const int LargePaste = 512 * 1024;

    /// <summary>
    /// Visual Studio's Ctrl+Shift+V: the first press pastes the newest ring entry; pressing again right away (caret still after it, nothing else
    /// edited) replaces it in place with the next older entry, cycling. The whole cycle is one undo step.
    /// </summary>
    public Task CycleRingAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return Task.CompletedTask;
        var ring = Clipboard.Ring;
        if (ring.Count == 0)
        {
            FeatureSupport.Status(_v, "The clipboard ring is empty");
            return Task.CompletedTask;
        }
        var doc = _v.Document;
        if (_ring is { } s && s.Version == doc.Version && _v.Editor.CaretOffset == s.End && _v.Editor.SelectionLength == 0 && s.End <= doc.Length)
        {
            var index = (s.Index + 1) % ring.Count;
            var range = new TextRange(doc.PositionAt(s.Start), doc.PositionAt(s.End));
            var entry = ring[index];
            // Whole lines replaced by an in-line entry keep the line ending they replace, so the next line is not joined.
            var replacingLines = range.End.Character == 0 && range.End.Line > range.Start.Line;
            if (replacingLines && !EndsWithLineBreak(entry.Text)) entry = entry with { Text = entry.Text + doc.EndOfLine };
            // Through the document: the cycle joins the paste's undo step unless a save came between (T50), and a held group sees the step.
            doc.BeginUndoGroup(continueLast: true);
            try
            {
                var result = ClipboardOperations.Paste(doc, SelectionSet.Single(range), entry with { Shape = ClipboardTextShape.Normal }, _v.GetEditingContext(), _v.Settings.AutoIndentOnPaste);
                if (result is null) return Task.CompletedTask;
                _v.ApplyPlan(result.Plan);
                var end = doc.OffsetAt(result.Plan.Selection.End);
                _ring = (index, s.Start, end, doc.Version);
            }
            finally
            {
                doc.EndUndoGroup();
            }
            FeatureSupport.Status(_v, $"Clipboard ring: {index + 1} of {ring.Count}");
            return Task.CompletedTask;
        }
        var inserted = Paste(ring[0], formatAfter: false);
        _ring = inserted is { } r ? (0, doc.OffsetAt(r.Start), doc.OffsetAt(r.End), doc.Version) : null;
        if (inserted is not null) FeatureSupport.Status(_v, $"Clipboard ring: 1 of {ring.Count}");
        return Task.CompletedTask;
    }

    private static bool EndsWithLineBreak(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    // ------------------------------------------------------------------ keys

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var m = e.KeyModifiers;
        if (e.Key == Key.F10 && m == KeyModifiers.Shift)
        {
            // Visual Studio's Shift+F10 opens the menu at the caret. Avalonia's own keyboard context request knows only the Menu key (which
            // reaches the host the same way, as a context request without a pointer position).
            e.Handled = true;
            _v.ShowContextMenu();
            return;
        }
        Func<Task>? run = (e.Key, m) switch
        {
            (Key.C, KeyModifiers.Control) or (Key.Insert, KeyModifiers.Control) => CopyAsync,
            (Key.X, KeyModifiers.Control) or (Key.Delete, KeyModifiers.Shift) => CutAsync,
            (Key.V, KeyModifiers.Control) or (Key.Insert, KeyModifiers.Shift) => PasteAsync,
            (Key.V, KeyModifiers.Control | KeyModifiers.Shift) => CycleRingAsync,
            _ => null,
        };
        if (run is null) return;
        e.Handled = true; // AvaloniaEdit's own clipboard bindings must not run as well
        _ = RunAsync(run);
    }

    // The release of Shift+F10 must not open a second menu where a platform's keyboard context request includes it.
    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) e.Handled = true;
    }

    private async Task RunAsync(Func<Task> run)
    {
        try { await run().ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { FeatureSupport.Status(_v, "Clipboard: " + ex.Message); }
    }

    // ------------------------------------------------------------------ pointer: right-click caret, middle-click PRIMARY paste

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_v.TextArea);
        if (point.Properties.IsRightButtonPressed)
        {
            // Visual Studio: a right-click moves the caret to the click unless it lands inside the selection (the menu then acts on the selection).
            if (OffsetAt(e) is { } offset && !_v.TextArea.Selection.Contains(offset))
            {
                _v.TextArea.ClearSelection();
                _v.Editor.CaretOffset = offset;
            }
            _v.TextArea.Focus();
            return;
        }
        if (point.Properties.IsMiddleButtonPressed && _v.Settings.SelectionClipboard && !_v.Document.IsReadOnly && Clipboard.IsPrimarySelectionSupported)
        {
            if (OffsetAt(e) is not { } at) return;
            e.Handled = true;
            _ = RunAsync(() => PastePrimaryAsync(at));
        }
    }

    private int? OffsetAt(PointerEventArgs e)
    {
        var view = _v.TextView;
        var p = e.GetPosition(view) + view.ScrollOffset;
        var pos = view.GetPosition(p) ?? view.GetPositionFloor(p);
        return pos is { } tp ? _v.Document.Buffer.GetOffset(tp.Location) : null;
    }

    /// <summary>Linux middle-click: the caret goes to the click and PRIMARY is inserted there (as it is, line endings converted), one undo step.</summary>
    internal async Task PastePrimaryAsync(int offset)
    {
        var doc = _v.Document;
        offset = Math.Clamp(offset, 0, doc.Length);
        var anchor = doc.Buffer.CreateAnchor(offset);
        _v.TextArea.ClearSelection();
        _v.Editor.CaretOffset = offset;
        _v.TextArea.Focus();
        var text = await Clipboard.ReadPrimarySelectionAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text) || _disposed || _v.IsDisposed || anchor.IsDeleted || doc.IsReadOnly) return;
        var insert = ClipboardTextExtensions.NormalizeLineEndings(text, doc.EndOfLine);
        var at = doc.PositionAt(anchor.Offset);
        if (!doc.ApplyEdits([TextEdit.Insert(at, insert)])) return;
        _v.Editor.CaretOffset = Math.Min(doc.Length, anchor.Offset + insert.Length);
    }

    // ------------------------------------------------------------------ PRIMARY: the selection is offered while it exists

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_primaryScheduled || _disposed || !_v.Settings.SelectionClipboard) return;
        _primaryScheduled = true; // a mouse drag changes the selection on every move: offer it once per UI turn
        _v.Post(PublishPrimary);
    }

    private void PublishPrimary()
    {
        _primaryScheduled = false;
        if (_disposed || !HasSelection || !Clipboard.IsPrimarySelectionSupported) return;
        var ranges = CurrentSelection().Ranges.Where(r => !r.IsEmpty).ToArray();
        if (ranges.Length == 0) return;
        // Lazy: the text is produced only if someone pastes it, from the immutable snapshot of this version (any thread).
        var snapshot = _v.Document.Snapshot();
        var eol = _v.Document.EndOfLine;
        Clipboard.SetPrimarySelection(() => string.Join(eol, ranges.Select(snapshot.GetText)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ring = null;
        _d.Dispose();
    }
}
