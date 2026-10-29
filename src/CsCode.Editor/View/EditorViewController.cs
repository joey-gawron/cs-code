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
    private bool _disposed;

    // Hooks the feature components install (so this class does not depend on all of them).
    internal Func<IReadOnlyList<int>>? FoldedLinesProvider { get; set; }
    internal Action<IReadOnlyList<int>>? FoldRestorer { get; set; }
    internal Action? FormatHandler { get; set; }
    internal Action<int?>? StopLineHandler { get; set; }
    internal Func<CancellationToken, ValueTask<IReadOnlyList<string>>>? BreadcrumbsHandler { get; set; }

    public IEditorDocument Document => view.Document;

    public TextPosition Caret => view.PositionOf(view.Editor.CaretOffset);

    public IReadOnlyList<TextRange> Selections
    {
        get
        {
            var editor = view.Editor;
            if (editor.SelectionLength == 0) return [TextRange.At(Caret.Line, Caret.Character)];
            return [new TextRange(view.PositionOf(editor.SelectionStart), view.PositionOf(editor.SelectionStart + editor.SelectionLength))];
        }
    }

    public void SetSelection(TextRange range, bool reveal = true)
    {
        view.SetSelection(range);
        if (reveal) Reveal(range, center: false);
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

    public void Undo() => view.Editor.Undo();
    public void Redo() => view.Editor.Redo();

    public void ShowFind(bool replace)
    {
        var panel = view.Editor.SearchPanel;
        panel.IsReplaceMode = replace;
        panel.Open();
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
            doc.Version);
    }

    public void RestoreViewState(EditorViewState state)
    {
        var doc = view.Document;
        FoldRestorer?.Invoke(state.FoldedStartLines);
        var caret = doc.Validate(state.Caret);
        var a = doc.Validate(state.SelectionStart);
        var b = doc.Validate(state.SelectionEnd);
        view.SetSelection(a == b ? new TextRange(caret, caret) : new TextRange(a, b));
        view.Editor.CaretOffset = doc.OffsetAt(caret);
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

    public ValueTask<IReadOnlyList<string>> GetBreadcrumbsAsync(CancellationToken ct = default) =>
        BreadcrumbsHandler?.Invoke(ct) ?? ValueTask.FromResult<IReadOnlyList<string>>([]);

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
        _disposed = true;
        _caretChanged.Clear();
        _selectionChanged.Clear();
        StopLineChanged.Clear();
        FoldedLinesProvider = null;
        FoldRestorer = null;
        FormatHandler = null;
        StopLineHandler = null;
        BreadcrumbsHandler = null;
    }
}
