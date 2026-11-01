using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;

namespace CsCode.Editor.View;

/// <summary>Caret shapes a modal editing layer can ask for (Vim: block in Normal and Visual, bar in Insert, underline in Replace).</summary>
public enum EditorCaretShape { Line, Block, Underline, BlockOutline, LineThin, UnderlineThin }

/// <summary>Fold operations on the editor's folding regions, by line.</summary>
public enum EditorFoldAction { Open, Close, Toggle, OpenRecursive, CloseRecursive, OpenAll, CloseAll }

/// <summary>
/// What a modal editing layer (the shell's Vim adapter) needs from one editor view beyond <see cref="IEditorViewController"/>, kept free of any
/// Vim type: the typing path, caret shape, selections (incl. rectangular), viewport queries, folds, the state of the IntelliSense popups and the
/// snippet session, search/flash highlights, the language's comment tokens and range formatting. UI thread only; every member is cheap and
/// never waits for a language service. While a layer is attached (<see cref="Attach"/>) the editor's Undo/Redo commands are offered to the layer
/// first and typing never continues an earlier undo group (the layer groups its own changes); <see cref="Detach"/> restores the plain editor.
/// </summary>
public sealed class EditorModalSurface : IDisposable
{
    private const int MaxPatternMatches = 1000;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(20);

    private readonly CodeEditorControl _v;
    private readonly CaretRenderer _caretRenderer;
    private readonly PatternRenderer _patternRenderer;
    private readonly FlashRenderer _flashRenderer;
    private readonly DisposableStore _d = new();
    private EditorCaretShape _shape = EditorCaretShape.Line;
    private Regex? _pattern;
    private (int Version, int Start, int End, Regex? Pattern) _matchesKey = (-1, -1, -1, null);
    private readonly List<(int Start, int End)> _matches = [];
    private (int Start, int End)? _flash;
    private DispatcherTimer? _flashTimer;
    private IBrush? _caretFill;
    private bool _disposed;

    internal EditorModalSurface(CodeEditorControl view)
    {
        _v = view;
        _caretRenderer = new CaretRenderer(this);
        _patternRenderer = new PatternRenderer(this);
        _flashRenderer = new FlashRenderer(this);
        var area = view.TextArea;
        area.Caret.PositionChanged += OnCaretMoved;
        area.GotFocus += OnFocusChanged;
        area.LostFocus += OnFocusChanged;
        _d.Add(Disposable.Create(() =>
        {
            area.Caret.PositionChanged -= OnCaretMoved;
            area.GotFocus -= OnFocusChanged;
            area.LostFocus -= OnFocusChanged;
        }));
    }

    public CodeEditorControl View => _v;
    public EditorDocument Document => _v.Document;
    public IEditorViewController Controller => _v.Controller;

    /// <summary>True between <see cref="Attach"/> and <see cref="Detach"/>.</summary>
    public bool IsAttached { get; private set; }

    /// <summary>
    /// The layer's undo/redo (argument: true = redo). Returns true when it handled the request; false lets the editor undo itself.
    /// The editor's Undo/Redo commands (menu, toolbar, key bindings) call it first while a layer is attached.
    /// </summary>
    internal Func<bool, bool>? UndoRedoHandler { get; private set; }

    public void Attach(Func<bool, bool>? undoRedo)
    {
        if (_disposed) return;
        UndoRedoHandler = undoRedo;
        if (IsAttached) return;
        IsAttached = true;
        _v.MultiCursor.Collapse(); // multi-cursor is off while a modal layer (Vim) edits
        var tv = _v.TextView;
        tv.BackgroundRenderers.Add(_patternRenderer);
        tv.BackgroundRenderers.Add(_flashRenderer);
        tv.BackgroundRenderers.Add(_caretRenderer);
    }

    /// <summary>Back to the plain editor: line caret, no highlights, the editor's own undo.</summary>
    public void Detach()
    {
        if (!IsAttached) return;
        IsAttached = false;
        UndoRedoHandler = null;
        CaretShape = EditorCaretShape.Line;
        SetPatternHighlight(null);
        ClearFlash();
        var tv = _v.TextView;
        tv.BackgroundRenderers.Remove(_patternRenderer);
        tv.BackgroundRenderers.Remove(_flashRenderer);
        tv.BackgroundRenderers.Remove(_caretRenderer);
    }

    // ---------------------------------------------------------------- input

    /// <summary>Whether <paramref name="source"/> (a key or text event's source) is this view's text input element.</summary>
    public bool IsInputElement(object? source) => ReferenceEquals(source, _v.TextArea);

    public bool HasKeyboardFocus => _v.TextArea.IsKeyboardFocusWithin;

    public bool IsReadOnly => _v.Document.IsReadOnly;

    /// <summary>
    /// Inserts <paramref name="text"/> through the editor's own typing path, exactly as if typed: auto-closing pairs and over-typing, completion and
    /// signature-help triggers, snippets, language-aware Enter ("\n") and on-type outdenting all apply. Synchronous.
    /// </summary>
    public void TypeText(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        _v.TextArea.PerformTextInput(text);
    }

    // ---------------------------------------------------------------- caret and selection

    public TextPosition Caret => _v.PositionOf(_v.Editor.CaretOffset);

    /// <summary>Moves the caret (no selection) and scrolls it into view when it left the viewport.</summary>
    public void SetCaret(TextPosition position)
    {
        var area = _v.TextArea;
        var offset = _v.Document.OffsetAt(position);
        if (!area.Selection.IsEmpty) area.ClearSelection();
        if (_v.Editor.CaretOffset != offset) _v.Editor.CaretOffset = offset;
        area.Caret.BringCaretToView();
    }

    /// <summary>A stream selection [<paramref name="start"/>, <paramref name="end"/>) with the caret at <paramref name="caret"/>.</summary>
    public void SetSelection(TextPosition start, TextPosition end, TextPosition caret)
    {
        var doc = _v.Document;
        var a = doc.OffsetAt(start);
        var b = doc.OffsetAt(end);
        var area = _v.TextArea;
        if (a == b) area.ClearSelection();
        else area.Selection = Selection.Create(area, a, b);
        _v.Editor.CaretOffset = doc.OffsetAt(caret);
        area.Caret.BringCaretToView();
    }

    /// <summary>Whole lines <paramref name="first"/>..<paramref name="last"/> (with their line breaks, except the document's last).</summary>
    public void SetLineSelection(int first, int last, TextPosition caret)
    {
        var buffer = _v.Document.Buffer;
        first = Math.Clamp(first, 0, buffer.LineCount - 1);
        last = Math.Clamp(last, 0, buffer.LineCount - 1);
        if (first > last) (first, last) = (last, first);
        var a = buffer.GetLineByNumber(first + 1).Offset;
        var lastLine = buffer.GetLineByNumber(last + 1);
        var b = lastLine.Offset + lastLine.TotalLength;
        var area = _v.TextArea;
        area.Selection = Selection.Create(area, a, Math.Max(a, b));
        _v.Editor.CaretOffset = _v.Document.OffsetAt(caret);
        area.Caret.BringCaretToView();
    }

    /// <summary>
    /// A rectangular selection over lines <paramref name="anchorLine"/>..<paramref name="activeLine"/> covering visual columns
    /// [<paramref name="startVisualColumn"/>, <paramref name="endVisualColumn"/>) (tabs expanded with the editor's tab size); the caret goes to <paramref name="caret"/>.
    /// </summary>
    public void SetBlockSelection(int anchorLine, int activeLine, int startVisualColumn, int endVisualColumn, TextPosition caret)
    {
        var area = _v.TextArea;
        var count = _v.Document.LineCount;
        anchorLine = Math.Clamp(anchorLine, 0, count - 1);
        activeLine = Math.Clamp(activeLine, 0, count - 1);
        var start = new TextViewPosition(anchorLine + 1, ColumnAtVisual(anchorLine, startVisualColumn) + 1, startVisualColumn);
        var end = new TextViewPosition(activeLine + 1, ColumnAtVisual(activeLine, endVisualColumn) + 1, Math.Max(startVisualColumn, endVisualColumn));
        area.Selection = new RectangleSelection(area, start, end);
        _v.Editor.CaretOffset = _v.Document.OffsetAt(caret);
        area.Caret.BringCaretToView();
    }

    public bool HasSelection => !_v.TextArea.Selection.IsEmpty;

    public void ClearSelection()
    {
        if (!_v.TextArea.Selection.IsEmpty) _v.TextArea.ClearSelection();
    }

    private int ColumnAtVisual(int line, int visualColumn)
    {
        var text = _v.Document.GetLineText(line);
        var tab = Math.Max(1, _v.Settings.TabSize);
        var v = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (v >= visualColumn) return i;
            v = text[i] == '\t' ? (v / tab + 1) * tab : v + 1;
        }
        return text.Length;
    }

    /// <summary>The caret drawn by the layer; <see cref="EditorCaretShape.Line"/> is the editor's own (blinking) caret.</summary>
    public EditorCaretShape CaretShape
    {
        get => _shape;
        set
        {
            if (_shape == value) return;
            _shape = value;
            ApplyCaretBrush();
            _v.TextView.InvalidateLayer(KnownLayer.Caret);
        }
    }

    /// <summary>The native caret is hidden (transparent) while the layer draws another shape.</summary>
    internal bool HidesNativeCaret => IsAttached && _shape != EditorCaretShape.Line;

    internal void ApplyCaretBrush()
    {
        _caretFill = null;
        _v.TextArea.CaretBrush = HidesNativeCaret ? Brushes.Transparent : _v.Palette.Caret;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (IsAttached && _shape != EditorCaretShape.Line) _v.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    private void OnFocusChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsAttached && _shape != EditorCaretShape.Line) _v.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    // ---------------------------------------------------------------- viewport

    private double LineHeight => Math.Max(1, _v.TextView.DefaultLineHeight);

    /// <summary>First line whose top is inside the viewport (0-based).</summary>
    public int FirstVisibleLine
    {
        get
        {
            var tv = _v.TextView;
            var doc = tv.Document;
            if (doc is null) return 0;
            var y = tv.ScrollOffset.Y;
            var line = tv.GetDocumentLineByVisualTop(y);
            if (line is null) return 0;
            if (tv.GetVisualTopByDocumentLine(line.LineNumber) < y - 0.5 && line.NextLine is { } next) line = next;
            return line.LineNumber - 1;
        }
    }

    /// <summary>Last line whose bottom is inside the viewport (0-based; at least <see cref="FirstVisibleLine"/>).</summary>
    public int LastVisibleLine
    {
        get
        {
            var tv = _v.TextView;
            var doc = tv.Document;
            if (doc is null) return 0;
            var first = FirstVisibleLine;
            var height = tv.Bounds.Height;
            if (height <= 0) return Math.Min(doc.LineCount - 1, first + VisibleLineCount - 1);
            var bottom = tv.ScrollOffset.Y + height;
            var line = tv.GetDocumentLineByVisualTop(Math.Max(0, bottom - 1));
            if (line is null) return doc.LineCount - 1;
            var lineBottom = tv.GetVisualTopByDocumentLine(line.LineNumber) + LineHeight;
            if (lineBottom > bottom + 0.5 && line.PreviousLine is { } prev && prev.LineNumber - 1 >= first) line = prev;
            return Math.Max(first, line.LineNumber - 1);
        }
    }

    /// <summary>Text rows that fit in the viewport (20 before the first layout).</summary>
    public int VisibleLineCount
    {
        get
        {
            var height = _v.TextView.Bounds.Height;
            return height <= 0 ? 20 : Math.Max(1, (int)Math.Floor(height / LineHeight));
        }
    }

    /// <summary>Scrolls so that <paramref name="line"/> is the first visible line (clamped by the editor).</summary>
    public void ScrollToTopLine(int line)
    {
        var tv = _v.TextView;
        if (tv.Document is null) return;
        line = Math.Clamp(line, 0, tv.Document.LineCount - 1);
        var y = tv.GetVisualTopByDocumentLine(line + 1);
        ((IScrollable)_v.TextArea).Offset = new Vector(tv.ScrollOffset.X, y);
    }

    /// <summary>Display rows <paramref name="line"/> takes with word wrap (1 without, or when the line was never laid out).</summary>
    public int DisplayRowsOf(int line)
    {
        if (!_v.Editor.WordWrap) return 1;
        var tv = _v.TextView;
        if (tv.Document is null || line < 0 || line >= tv.Document.LineCount) return 1;
        if (tv.GetVisualLine(line + 1) is { } visual) return Math.Max(1, visual.TextLines.Count);
        var columns = WrapColumns;
        if (columns <= 0) return 1;
        var length = tv.Document.GetLineByNumber(line + 1).Length;
        return Math.Max(1, (length + columns - 1) / columns);
    }

    /// <summary>Cells per display row when word wrap is on, else 0.</summary>
    public int WrapColumns => _v.Editor.WordWrap ? WindowColumns : 0;

    /// <summary>Width of the text area in cells (80 before the first layout).</summary>
    public int WindowColumns
    {
        get
        {
            var tv = _v.TextView;
            var width = tv.Bounds.Width;
            var cell = tv.WideSpaceWidth;
            return width <= 0 || cell <= 0 ? 80 : Math.Max(1, (int)Math.Floor(width / cell));
        }
    }

    // ---------------------------------------------------------------- folds

    /// <summary>When <paramref name="line"/> lies in a collapsed region (its visible first line included), the outermost such region's lines.</summary>
    public bool TryGetClosedFold(int line, out int startLine, out int endLine)
    {
        startLine = endLine = line;
        return _v.Feature<FoldingFeature>() is { } folding && folding.TryGetClosedFold(line, out startLine, out endLine);
    }

    public void Fold(EditorFoldAction action, int startLine, int endLine) => _v.Feature<FoldingFeature>()?.FoldLines(action, startLine, endLine);

    // ---------------------------------------------------------------- popups and sessions

    public bool IsCompletionOpen => _v.Feature<CompletionFeature>()?.IsOpen == true;
    public bool IsSignatureHelpOpen => _v.Feature<SignatureFeature>()?.IsOpen == true;
    public bool IsSnippetActive => _v.Feature<SnippetFeature>()?.IsActive == true;
    public bool IsHoverOpen => _v.Feature<HoverFeature>()?.IsOpen == true;

    public void TriggerCompletion() => _v.Feature<ICompletionFeature>()?.Trigger();

    /// <summary>Moves the completion list's selection (no-op when the list is not open).</summary>
    public void MoveCompletionSelection(int delta)
    {
        if (_v.Feature<CompletionFeature>() is { IsOpen: true } completion) completion.MoveSelection(delta);
    }

    public void CloseCompletion() => _v.Feature<CompletionFeature>()?.Close();

    /// <summary>Ends the snippet tab-stop session, keeping the text.</summary>
    public void EndSnippet() => _v.Feature<SnippetFeature>()?.End();

    /// <summary>Closes the hover and parameter-hint popups (a peek stays: see <see cref="ClosePeek"/>).</summary>
    public void CloseInfoPopups()
    {
        _v.Feature<HoverFeature>()?.Hide();
        _v.Feature<SignatureFeature>()?.Close();
    }

    /// <summary>
    /// Closes the peek this view shows, or the one it is embedded in, and returns the keyboard to the view the peek is in; true when one was
    /// open. A modal layer calls it for its "close" Esc only (Vim: Esc in Normal mode), not for the Esc that leaves Insert or Visual mode.
    /// </summary>
    public bool ClosePeek() => PeekFeature.CloseFor(_v);

    // ---------------------------------------------------------------- decorations

    /// <summary>Highlights every match of <paramref name="pattern"/> in the visible lines (search highlighting); null clears.</summary>
    public void SetPatternHighlight(Regex? pattern)
    {
        if (ReferenceEquals(_pattern, pattern)) return;
        _pattern = pattern;
        _matches.Clear();
        _matchesKey = (-1, -1, -1, null);
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    public Regex? PatternHighlight => _pattern;

    /// <summary>Briefly highlights [<paramref name="start"/>, <paramref name="end"/>) (a yank).</summary>
    public void Flash(TextPosition start, TextPosition end, TimeSpan duration)
    {
        var doc = _v.Document;
        var a = doc.OffsetAt(start);
        var b = doc.OffsetAt(end);
        if (b <= a) return;
        _flash = (a, b);
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => ClearFlash());
        _flashTimer.Start();
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>The range currently flashed (offsets), for tests.</summary>
    public (int Start, int End)? FlashedRange => _flash;

    private void ClearFlash()
    {
        _flashTimer?.Stop();
        _flashTimer = null;
        if (_flash is null) return;
        _flash = null;
        if (!_disposed) _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ---------------------------------------------------------------- language

    /// <summary>The language's line and block comment tokens (from its language configuration).</summary>
    public (string? Line, string? BlockOpen, string? BlockClose) CommentTokens
    {
        get
        {
            var cfg = _v.LanguageConfiguration.Base;
            return (cfg.LineComment, cfg.BlockComment?.Open, cfg.BlockComment?.Close);
        }
    }

    public int TabSize => _v.Settings.TabSize;
    public bool InsertSpaces => _v.Settings.InsertSpaces;

    /// <summary>
    /// The ranges the document's syntax tree captures as <paramref name="capture"/> in nvim-treesitter-textobjects' <c>textobjects.scm</c>
    /// (<c>function.outer</c>, <c>class.inner</c>, <c>parameter.outer</c>…): a modal layer's syntax text objects. Null at once (on the UI
    /// thread, without leaving it) when the document has no tree-sitter tree (TextMate colours it). Otherwise a task computed on the syntax lane
    /// from the tree of the document's version at this call, which completes off the UI thread (the caller hops back itself; nothing waits for
    /// it) with the ranges in document order, or with null when no tree of that version arrived in time, the text changed meanwhile, or the
    /// grammar has no text objects.
    /// </summary>
    public Task<IReadOnlyList<TextRange>?>? RequestSyntaxObjects(string capture) =>
        _disposed ? null : _v.Factory.Tokens.Structure.TextObjectRangesAsync(_v.Document, capture);

    /// <summary>True when a formatter is registered for the document's language.</summary>
    public bool CanFormat => !_v.Document.IsLargeFile && _v.Services.Features.GetProviders<IDocumentFormattingProvider>(_v.Document).Count > 0;

    /// <summary>Formats lines <paramref name="startLine"/>..<paramref name="endLine"/> through the language's formatter. Fire and forget: the edit arrives later.</summary>
    public void FormatLines(int startLine, int endLine)
    {
        if (_v.Feature<FormattingFeature>() is not { } formatting) return;
        var doc = _v.Document;
        var last = Math.Clamp(endLine, 0, doc.LineCount - 1);
        var range = new TextRange(new TextPosition(Math.Clamp(startLine, 0, last), 0), new TextPosition(last, doc.GetLineText(last).Length));
        _ = formatting.FormatLinesAsync(range);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _d.Dispose();
        _flashTimer?.Stop();
        _flashTimer = null;
    }

    // ---------------------------------------------------------------- renderers

    private IBrush CaretFill()
    {
        if (_caretFill is { } cached) return cached;
        var color = _v.Palette.Caret is ISolidColorBrush s ? s.Color : Colors.White;
        return _caretFill = new ImmutableSolidColorBrush(color, 0.55);
    }

    /// <summary>Block / underline / outline carets (the native caret is transparent meanwhile). Steady, drawn only while the view has focus.</summary>
    private sealed class CaretRenderer(EditorModalSurface owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext context)
        {
            var shape = owner._shape;
            if (shape == EditorCaretShape.Line || !owner.IsAttached || !textView.VisualLinesValid || !owner._v.TextArea.IsKeyboardFocusWithin) return;
            var pos = owner._v.TextArea.Caret.Position;
            if (textView.GetVisualLine(pos.Line) is null) return;
            var top = textView.GetVisualPosition(pos, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(pos, VisualYPosition.LineBottom) - textView.ScrollOffset;
            var width = textView.WideSpaceWidth;
            var line = textView.Document.GetLineByNumber(pos.Line);
            if (pos.Column <= line.Length)
            {
                var next = textView.GetVisualPosition(new TextViewPosition(pos.Line, pos.Column + 1), VisualYPosition.LineTop) - textView.ScrollOffset;
                if (Math.Abs(next.Y - top.Y) < 0.5 && next.X - top.X > 0.5) width = next.X - top.X;
            }
            var height = Math.Max(1, bottom.Y - top.Y);
            var fill = owner.CaretFill();
            var solid = owner._v.Palette.Caret;
            switch (shape)
            {
                case EditorCaretShape.Block:
                    context.FillRectangle(fill, new Rect(top.X, top.Y, width, height));
                    break;
                case EditorCaretShape.BlockOutline:
                    context.DrawRectangle(new Pen(solid, 1), new Rect(top.X + 0.5, top.Y + 0.5, Math.Max(1, width - 1), Math.Max(1, height - 1)));
                    break;
                case EditorCaretShape.Underline:
                    context.FillRectangle(solid, new Rect(top.X, top.Y + height - 2, width, 2));
                    break;
                case EditorCaretShape.UnderlineThin:
                    context.FillRectangle(solid, new Rect(top.X, top.Y + height - 1, width, 1));
                    break;
                case EditorCaretShape.LineThin:
                    context.FillRectangle(solid, new Rect(top.X, top.Y, 1, height));
                    break;
            }
        }
    }

    /// <summary>Search matches in the visible lines; computed once per (document version, visible range, pattern), capped and with a regex timeout.</summary>
    private sealed class PatternRenderer(EditorModalSurface owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            var pattern = owner._pattern;
            if (pattern is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            var first = textView.VisualLines[0].FirstDocumentLine;
            var last = textView.VisualLines[^1].LastDocumentLine;
            var key = (owner._v.Document.Version, first.Offset, last.EndOffset, (Regex?)pattern);
            if (owner._matchesKey != key)
            {
                owner._matchesKey = key;
                owner._matches.Clear();
                var doc = textView.Document;
                try
                {
                    for (var l = first; l is not null && l.LineNumber <= last.LineNumber && owner._matches.Count < MaxPatternMatches; l = l.NextLine)
                    {
                        var text = doc.GetText(l.Offset, l.Length);
                        for (var m = pattern.Match(text); m.Success && owner._matches.Count < MaxPatternMatches; m = m.NextMatch())
                            if (m.Length > 0) owner._matches.Add((l.Offset + m.Index, l.Offset + m.Index + m.Length));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // a pathological pattern: show what was found so far rather than stall the UI thread
                }
            }
            var brush = owner._v.Palette.FindMatch;
            foreach (var (start, end) in owner._matches)
            {
                var seg = new TextSegment { StartOffset = start, EndOffset = end };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg)) context.FillRectangle(brush, rect);
            }
        }
    }

    private sealed class FlashRenderer(EditorModalSurface owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (owner._flash is not { } f || !textView.VisualLinesValid) return;
            var length = textView.Document.TextLength;
            var seg = new TextSegment { StartOffset = Math.Min(f.Start, length), EndOffset = Math.Min(f.End, length) };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg)) context.FillRectangle(owner._v.Palette.WordHighlightStrong, rect);
        }
    }

    /// <summary>Compiles a highlight pattern with the timeout the renderer relies on; null when the pattern is not a valid .NET regex.</summary>
    public static Regex? CompilePattern(string pattern, bool ignoreCase)
    {
        try { return new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), PatternTimeout); }
        catch (ArgumentException) { return null; }
    }
}
