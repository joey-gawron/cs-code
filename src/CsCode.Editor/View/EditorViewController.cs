using AvaloniaEdit;
using AvaloniaEdit.Editing;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Editor.View;

/// <summary>The imperative handle on one <see cref="CodeEditorControl"/> (caret, selection, reveal, undo, find, view state).</summary>
internal sealed class EditorViewController(CodeEditorControl view) : IEditorViewController, IDisposable
{
    private readonly Emitter<TextPosition> _caretChanged = new();
    private readonly Emitter<IReadOnlyList<TextRange>> _selectionChanged = new();
    private readonly Emitter<string> _textInput = new();
    private bool _disposed;

    // Hooks the feature components install (so this class does not depend on all of them).
    internal Func<IReadOnlyList<int>>? FoldedLinesProvider { get; set; }
    internal Action<IReadOnlyList<int>>? FoldRestorer { get; set; }
    internal Action? FormatHandler { get; set; }
    internal Action<int?>? StopLineHandler { get; set; }
    internal Func<CancellationToken, ValueTask<IReadOnlyList<string>>>? BreadcrumbsHandler { get; set; }

    public IEditorDocument Document => view.Document;

    public TextPosition Caret => view.PositionOf(view.Editor.CaretOffset);

    /// <summary>The primary selection first, then the other carets' (multi-cursor) in document order.</summary>
    public IReadOnlyList<TextRange> Selections
    {
        get
        {
            if (view.MultiCursor.IsActive) return view.MultiCursor.Selections();
            var editor = view.Editor;
            if (editor.SelectionLength == 0) return [TextRange.At(Caret.Line, Caret.Character)];
            return [new TextRange(view.PositionOf(editor.SelectionStart), view.PositionOf(editor.SelectionStart + editor.SelectionLength))];
        }
    }

    public void SetSelection(TextRange range, bool reveal = true)
    {
        view.MultiCursor.Collapse();
        view.SetSelection(range);
        if (reveal) Reveal(range, center: false);
    }

    /// <summary>Several selections at once (multi-cursor): the first is the primary. Bounded by <c>editor.multiCursorLimit</c>; one only under a modal layer that does not handle several carets.</summary>
    public void SetSelections(IReadOnlyList<TextRange> selections, bool reveal = true)
    {
        if (_disposed || selections.Count == 0) return;
        if (selections.Count == 1)
        {
            SetSelection(selections[0], reveal);
            return;
        }
        view.MultiCursor.SetSelections(selections, reveal);
    }

    public void Reveal(TextRange range, bool center = true)
    {
        var line = Math.Clamp(range.Start.Line, 0, Math.Max(0, view.Document.LineCount - 1)) + 1;
        var column = range.Start.Character + 1;
        if (center)
            view.Editor.ScrollTo(line, column, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle, view.Editor.ViewportHeight / 2, 0.3);
        else
            view.Editor.ScrollTo(line, column);
    }

    public void Focus() => view.Editor.TextArea.Focus();

    public bool CanUndo => view.Document.CanUndo;
    public bool CanRedo => view.Document.CanRedo;

    // A modal layer (Vim) owns the undo history while it is attached: the editor's Undo/Redo go through it so both always agree.
    public void Undo()
    {
        if (view.ModalUndoRedo?.Invoke(false) == true) return;
        view.Editor.Undo();
    }

    public void Redo()
    {
        if (view.ModalUndoRedo?.Invoke(true) == true) return;
        view.Editor.Redo();
    }

    public void ShowFind(bool replace)
    {
        var panel = view.Editor.SearchPanel;
        view.Feature<FindSearch>()?.Attach(); // bound before the panel's first search
        panel.IsReplaceMode = replace;
        panel.Open();
        // AvaloniaEdit's Open focuses the find box only when the panel's template is already applied, which it is not the first time the panel
        // opens (no layout pass yet; always so on the headless platform): apply it now and put the keyboard in the box, as Ctrl+F does in
        // Visual Studio and VS Code (T20: recording Enter in the find box depends on it).
        panel.ApplyTemplate();
        panel.Reactivate();
    }

    public void FormatDocument() => FormatHandler?.Invoke();

    /// <summary>The line the debugger is stopped at in this view (0-based), or null.</summary>
    internal int? StopLine { get; private set; }

    internal Emitter<int?> StopLineChanged { get; } = new();

    /// <summary>
    /// Sets the stop-line decoration and reveals (centres) the line — only when the line actually changes. The debug feature calls this on every
    /// stop-location and state notification; re-announcing the same line (a variables-only <c>invalidated</c>, a state change of another
    /// session, a second stop at the same line without a resume in between) must not scroll a view the user moved away from the stop line.
    /// A new stop after a resume is a change (the line was cleared in between), so it is revealed.
    /// </summary>
    public void SetDebugStopLine(int? line)
    {
        if (_disposed || StopLine == line) return;
        StopLine = line;
        StopLineHandler?.Invoke(line);
        StopLineChanged.Fire(line);
    }

    public IEventSource<TextPosition> CaretChanged => _caretChanged;
    public IEventSource<IReadOnlyList<TextRange>> SelectionChanged => _selectionChanged;

    public bool IsFocused => view.Editor.TextArea.IsFocused || view.IsKeyboardFocusWithin;

    public EditorViewState GetViewState()
    {
        var doc = view.Document;
        var start = view.PositionOf(view.Editor.SelectionStart);
        var end = view.PositionOf(view.Editor.SelectionStart + view.Editor.SelectionLength);
        var caret = Caret;
        return new EditorViewState(
            caret,
            view.Editor.SelectionLength == 0 ? caret : start,
            view.Editor.SelectionLength == 0 ? caret : end,
            view.TextView.ScrollOffset.Y,
            view.TextView.ScrollOffset.X,
            FoldedLinesProvider?.Invoke() ?? [],
            doc.Version)
        {
            WordWrap = view.WordWrapOverride,
            Carets = view.MultiCursor.IsActive ? view.MultiCursor.CaretStates() : null, // multi-cursor: every caret, the primary first
        };
    }

    public void RestoreViewState(EditorViewState state)
    {
        var doc = view.Document;
        if (state.WordWrap is { } wrap) view.WordWrapOverride = wrap; // before the folds and the scroll: wrapping changes the line heights
        FoldRestorer?.Invoke(state.FoldedStartLines);
        var caret = doc.Validate(state.Caret);
        var a = doc.Validate(state.SelectionStart);
        var b = doc.Validate(state.SelectionEnd);
        view.SetSelection(a == b ? new TextRange(caret, caret) : new TextRange(a, b));
        view.Editor.CaretOffset = doc.OffsetAt(caret);
        if (state.Carets is { Count: > 1 } carets) view.MultiCursor.RestoreCarets(carets);
        ApplyScroll(state.VerticalOffset, state.HorizontalOffset, attemptsLeft: 8);
    }

    /// <summary>
    /// A freshly created view has no extent yet, so a scroll offset applied immediately is clamped to 0. Re-apply on the next dispatcher turns
    /// until the layout has caught up (bounded: a few attempts, then give up rather than fight the user's own scrolling).
    /// </summary>
    private void ApplyScroll(double vertical, double horizontal, int attemptsLeft)
    {
        void Apply()
        {
            ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Avalonia.Vector(horizontal, vertical); // TextEditor.ScrollToVerticalOffset is a no-op for absolute positions
        }
        bool Done() => Math.Abs(view.TextView.ScrollOffset.Y - vertical) < 1;

        Apply();
        DetachScrollRetry();
        if (Done()) return;
        // Layout has not produced an extent yet: retry on each layout pass (bounded), then stop rather than fight the user's own scrolling.
        var passes = 0;
        _scrollRetry = (_, _) =>
        {
            if (_disposed || view.IsDisposed || ++passes > attemptsLeft * 4)
            {
                DetachScrollRetry();
                return;
            }
            Apply();
            if (Done()) DetachScrollRetry();
        };
        view.Editor.LayoutUpdated += _scrollRetry;
    }

    private EventHandler? _scrollRetry;

    private void DetachScrollRetry()
    {
        if (_scrollRetry is null) return;
        view.Editor.LayoutUpdated -= _scrollRetry;
        _scrollRetry = null;
    }

    public bool CanCut => !_disposed && view.Clipboard.CanCut;
    public bool CanCopy => !_disposed && view.Clipboard.CanCopy;
    public bool CanPaste => !_disposed && view.Clipboard.CanPaste;
    public Task CutAsync() => _disposed ? Task.CompletedTask : view.Clipboard.CutAsync();
    public Task CopyAsync() => _disposed ? Task.CompletedTask : view.Clipboard.CopyAsync();
    public Task CopyWithFormattingAsync() => _disposed ? Task.CompletedTask : view.Clipboard.CopyAsync(withFormatting: true);
    public Task PasteAsync() => _disposed ? Task.CompletedTask : view.Clipboard.PasteAsync();
    public Avalonia.Rect GetCaretBounds() => _disposed ? default : view.CaretBounds();
    public void PrepareContextMenu()
    {
        if (!_disposed) EditorMenus.PrepareContextKeys(view);
    }

    public IEventSource<string> TextInput => _textInput;

    public IDisposable OpenUndoGroup() => _disposed || view.IsDisposed ? Disposable.Empty : view.Document.OpenUndoGroup();

    public void StartNewUndoStep()
    {
        if (!_disposed && !view.IsDisposed) view.Document.StartNewUndoStep();
    }

    public bool ReplayText(string text)
    {
        if (_disposed || view.IsDisposed || view.Document.IsReadOnly || string.IsNullOrEmpty(text)) return false;
        view.TextArea.PerformTextInput(text);
        return true;
    }

    public bool ReplayKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        if (_disposed || view.IsDisposed) return false;
        var area = view.TextArea;
        // AvaloniaEdit's editing and caret commands act on the focused text area (its RoutedCommand targets the last focused element, not the
        // event's source): a key for this view must find it focused, or it would edit whichever editor had the focus last.
        if (!area.IsFocused && (!area.Focus() || !area.IsFocused)) return false;
        var down = new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = area };
        area.RaiseEvent(down);
        // the release keeps handlers that track pressed keys (Ctrl held for Ctrl+Tab, Ctrl+click) consistent; it is never a command
        if (!view.IsDisposed) area.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers, Source = area });
        return down.Handled;
    }

    /// <summary>Text input that reached the text area (see <see cref="IEditorViewController.TextInput"/>): control characters are not typing.</summary>
    internal void RaiseTextInput(string text)
    {
        if (_disposed || _textInput.SubscriberCount == 0) return;
        foreach (var c in text)
            if (c >= ' ' && c != '\u007f')
            {
                _textInput.Fire(text);
                return;
            }
    }

    public ValueTask<IReadOnlyList<string>> GetBreadcrumbsAsync(CancellationToken ct = default) =>
        BreadcrumbsHandler?.Invoke(ct) ?? ValueTask.FromResult<IReadOnlyList<string>>([]);

    // ---- scroll synchronisation (the Markdown preview)

    private Emitter<double>? _scrolled;

    public double TopLine
    {
        get
        {
            if (_disposed || view.IsDisposed) return 0;
            var tv = view.TextView;
            var y = tv.ScrollOffset.Y;
            if (tv.VisualLinesValid && tv.GetVisualLineFromVisualTop(y) is { } visual)
            {
                var within = visual.Height > 0 ? Math.Clamp((y - visual.VisualTop) / visual.Height, 0, 0.999) : 0;
                return visual.FirstDocumentLine.LineNumber - 1 + within;
            }
            var lineHeight = tv.DefaultLineHeight;
            if (lineHeight <= 0) return 0;
            var top = tv.GetDocumentLineByVisualTop(y);
            return top is null ? 0 : top.LineNumber - 1 + Math.Clamp((y - tv.GetVisualTopByDocumentLine(top.LineNumber)) / lineHeight, 0, 0.999);
        }
    }

    public IEventSource<double> Scrolled
    {
        get
        {
            if (_scrolled is null)
            {
                _scrolled = new Emitter<double>();
                if (!_disposed) view.TextView.ScrollOffsetChanged += OnScrollOffsetChanged; // hooked on first use: most views never have a preview
            }
            return _scrolled;
        }
    }

    private void OnScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _scrolled?.Fire(TopLine);
    }

    public void ScrollToLine(double line)
    {
        if (_disposed || view.IsDisposed) return;
        var doc = view.Editor.Document;
        if (doc is null || doc.LineCount == 0) return;
        var tv = view.TextView;
        line = Math.Clamp(line, 0, doc.LineCount - 1);
        var number = (int)Math.Floor(line) + 1;
        var top = tv.GetVisualTopByDocumentLine(number);
        var height = tv.DefaultLineHeight;
        if (tv.VisualLinesValid && tv.GetVisualLine(number) is { } visual) height = visual.Height;
        var y = top + (line - (number - 1)) * height;
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Avalonia.Vector(tv.ScrollOffset.X, Math.Max(0, y));
    }

    internal void RaiseCaretChanged()
    {
        if (!_disposed) _caretChanged.Fire(Caret);
    }

    internal void RaiseSelectionChanged()
    {
        if (!_disposed) _selectionChanged.Fire(Selections);
    }

    public void Dispose()
    {
        DetachScrollRetry();
        if (_scrolled is not null)
        {
            view.TextView.ScrollOffsetChanged -= OnScrollOffsetChanged;
            _scrolled.Clear();
        }
        _disposed = true;
        _caretChanged.Clear();
        _selectionChanged.Clear();
        _textInput.Clear();
        StopLineChanged.Clear();
        FoldedLinesProvider = null;
        FoldRestorer = null;
        FormatHandler = null;
        StopLineHandler = null;
        BreadcrumbsHandler = null;
    }
}
