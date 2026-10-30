using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Vim;

namespace CsCode.App.Vim;

/// <summary>
/// <see cref="IVimHost"/> over one real editor view (<see cref="EditorModalSurface"/>): caret, selections (block included), viewport, folds,
/// highlights and the typing path go to the view; feedback (mode, showcmd, messages, the command line) and everything that concerns the
/// shell (windows, buffers, files, clipboard, application commands) go to <see cref="VimIntegration"/>. Every call comes from the engine,
/// synchronously, on the UI thread, and returns without waiting for anything.
/// </summary>
internal sealed class EditorVimHost : IVimHost
{
    private readonly VimIntegration _owner;
    private readonly EditorModalSurface _surface;
    private readonly EditorVimBuffer _buffer;

    public EditorVimHost(VimIntegration owner, EditorModalSurface surface, EditorVimBuffer buffer, IVimTimer timer)
    {
        _owner = owner;
        _surface = surface;
        _buffer = buffer;
        Timer = timer;
    }

    /// <summary>Set once the engine exists (the engine's constructor already calls into the host).</summary>
    public VimView? View { get; set; }

    public EditorModalSurface Surface => _surface;

    // ---------------------------------------------------------------- buffer and caret

    public IVimBuffer Buffer => _buffer;
    public IVimTimer Timer { get; }

    /// <summary>
    /// The editor's caret. The engine reads it before every key and adopts it when the mouse (or anything but the engine) moved it, so it is
    /// also where the caret goes back to when Visual mode ends.
    /// </summary>
    public TextPosition GetCaret() => _caret = _surface.Caret;

    private TextPosition _caret;

    public void SetCaret(TextPosition position, bool insertLike)
    {
        // The engine sends the caret before the selection. In Visual mode the selection call places the caret (moving it alone would clear
        // the selection); leaving Visual mode, the selection call clears the selection and then places it.
        _caret = position;
        if (View is { InVisual: true }) return;
        _surface.SetCaret(position);
    }

    /// <summary>The selection the engine last asked for, and the editor range it was drawn as (to tell the engine's selection from the mouse's).</summary>
    public VimSelection LastSelection { get; private set; } = VimSelection.None;
    private TextRange _drawn;

    public bool IsShowing(TextRange editorRange) => LastSelection.Kind != VimSelectionKind.None && editorRange == _drawn;

    public void SetSelection(in VimSelection selection)
    {
        LastSelection = selection;
        var view = View;
        if (view is not null) view.InVisual = selection.Kind != VimSelectionKind.None;
        var doc = _surface.Document;
        try { DrawSelection(selection, doc); }
        finally { _drawn = _surface.Controller.Selections is [var s] ? s : default; }
    }

    private void DrawSelection(in VimSelection selection, Core.Text.IEditorDocument doc)
    {
        switch (selection.Kind)
        {
            case VimSelectionKind.None:
                _surface.SetCaret(_caret);
                break;
            case VimSelectionKind.Character:
            {
                // Vim's character selection includes both end characters; the editor's range is half-open
                var (a, b) = selection.Anchor <= selection.Active ? (selection.Anchor, selection.Active) : (selection.Active, selection.Anchor);
                var endLine = doc.GetLineText(b.Line);
                var end = b.Character < endLine.Length ? new TextPosition(b.Line, b.Character + CharLength(endLine, b.Character))
                    : b.Line + 1 < doc.LineCount ? new TextPosition(b.Line + 1, 0) : new TextPosition(b.Line, endLine.Length);
                _surface.SetSelection(a, end, selection.Active);
                break;
            }
            case VimSelectionKind.Line:
                _surface.SetLineSelection(selection.Anchor.Line, selection.Active.Line, selection.Active);
                break;
            case VimSelectionKind.Block:
            {
                var end = selection.BlockToLineEnd ? LongestVisualLength(selection.Anchor.Line, selection.Active.Line) : selection.BlockEndVcol + 1;
                _surface.SetBlockSelection(selection.Anchor.Line, selection.Active.Line, selection.BlockStartVcol, Math.Max(end, selection.BlockStartVcol + 1), selection.Active);
                break;
            }
        }
    }

    private static int CharLength(string line, int index) =>
        index + 1 < line.Length && char.IsHighSurrogate(line[index]) && char.IsLowSurrogate(line[index + 1]) ? 2 : 1;

    private int LongestVisualLength(int a, int b)
    {
        var tab = Math.Max(1, _surface.TabSize);
        var max = 0;
        for (var l = Math.Min(a, b); l <= Math.Max(a, b); l++)
        {
            var text = _buffer.GetLine(l);
            var v = 0;
            foreach (var c in text) v = c == '\t' ? (v / tab + 1) * tab : v + 1;
            max = Math.Max(max, v);
        }
        return max;
    }

    // ---------------------------------------------------------------- feedback

    public void OnModeChanged(VimMode mode, in VimModeInfo info)
    {
        _surface.CaretShape = info.Shape switch
        {
            VimCursorShape.Block => EditorCaretShape.Block,
            VimCursorShape.Underline => EditorCaretShape.Underline,
            VimCursorShape.BlockOutline => EditorCaretShape.BlockOutline,
            VimCursorShape.LineThin => EditorCaretShape.LineThin,
            VimCursorShape.UnderlineThin => EditorCaretShape.UnderlineThin,
            _ => EditorCaretShape.Line,
        };
        if (View is { } v) _owner.OnModeChanged(v, mode, info);
    }

    public void OnMessage(in VimMessage message)
    {
        if (View is { } v) _owner.OnMessage(v, message);
    }

    public void OnShowCommand(string pendingKeys)
    {
        if (View is { } v) _owner.OnShowCommand(v, pendingKeys);
    }

    public void OnRecordingChanged(char? register)
    {
        if (View is { } v) _owner.OnRecordingChanged(v, register);
    }

    public void OnCommandLineChanged(in VimCommandLine commandLine)
    {
        if (View is { } v) _owner.OnCommandLineChanged(v, commandLine);
    }

    public void OnSearchHighlight(VimSearchHighlight? highlight) => _owner.OnSearchHighlight(highlight);

    public void OnYankHighlight(TextPosition start, TextPosition end, bool linewise)
    {
        var doc = _surface.Document;
        if (linewise)
        {
            start = new TextPosition(start.Line, 0);
            end = new TextPosition(end.Line, doc.GetLineText(Math.Clamp(end.Line, 0, doc.LineCount - 1)).Length);
        }
        else
        {
            // the engine's end is inclusive
            var line = doc.GetLineText(Math.Clamp(end.Line, 0, doc.LineCount - 1));
            end = new TextPosition(end.Line, Math.Min(line.Length, end.Character + 1));
        }
        _surface.Flash(start, end, VimIntegration.YankFlashDuration);
    }

    // ---------------------------------------------------------------- viewport

    public VimViewport GetViewport()
    {
        var first = _surface.FirstVisibleLine;
        var last = Math.Max(first, _surface.LastVisibleLine);
        return new VimViewport(first, last, Math.Max(1, _surface.VisibleLineCount), _surface.WrapColumns);
    }

    public void SetTopLine(int firstLine) => _surface.ScrollToTopLine(firstLine);
    public int GetDisplayRows(int line) => _surface.DisplayRowsOf(line);
    public int WrapWidth => _surface.WrapColumns;
    public int WindowWidth => _surface.WindowColumns;

    // ---------------------------------------------------------------- folds

    public bool TryGetClosedFold(int line, out int startLine, out int endLine) => _surface.TryGetClosedFold(line, out startLine, out endLine);

    public void FoldCommand(VimFoldCommand command, int startLine, int endLine)
    {
        EditorFoldAction? action = command switch
        {
            VimFoldCommand.Open => EditorFoldAction.Open,
            VimFoldCommand.Close => EditorFoldAction.Close,
            VimFoldCommand.Toggle => EditorFoldAction.Toggle,
            VimFoldCommand.OpenRecursive => EditorFoldAction.OpenRecursive,
            VimFoldCommand.CloseRecursive => EditorFoldAction.CloseRecursive,
            VimFoldCommand.OpenAll => EditorFoldAction.OpenAll,
            VimFoldCommand.CloseAll => EditorFoldAction.CloseAll,
            _ => null, // zf zd zE zj zk [z ]z: the editor computes its regions itself
        };
        if (action is { } a) _surface.Fold(a, startLine, endLine);
    }

    // ---------------------------------------------------------------- clipboard, windows, commands

    // + is the clipboard (with the ring, shape metadata and this document's line ends), * the PRIMARY selection where there is one (VimClipboard)
    public string? ReadClipboard(char register) => _owner.Clipboard.Text(register);

    public VimRegisterType? ReadClipboardType(char register) => _owner.Clipboard.Type(register);

    // the engine's read: text and type of one snapshot (a refresh may publish between two separate reads)
    public (string? Text, VimRegisterType? Type) ReadClipboardRegister(char register) =>
        _owner.Clipboard.Read(register) is { } content ? (content.Text, content.Type) : (null, null);

    public void WriteClipboard(char register, string text, VimRegisterType type) => _owner.Clipboard.Write(register, text, type, _surface.Document.EndOfLine);

    public string? ReadClipboard() => ReadClipboard('+');

    public void WriteClipboard(string text, VimRegisterType type) => WriteClipboard('+', text, type);

    public bool PerformAction(VimEditorAction action, in VimActionArgs args) => View is { } v && _owner.Actions.Perform(v, action, args);

    public void ExecuteCommand(string commandId, IReadOnlyList<object?>? args) => _owner.ExecuteCommand(commandId, args);

    // ---------------------------------------------------------------- language

    public VimCommentTokens GetCommentTokens()
    {
        var (line, open, close) = _surface.CommentTokens;
        return new VimCommentTokens(line, open, close);
    }

    public bool FormatLines(int startLine, int endLine)
    {
        // The formatter is a language server: the result arrives later, as a change of its own. Without one the engine re-indents itself.
        if (!_surface.CanFormat) return false;
        _surface.FormatLines(startLine, endLine);
        return true;
    }

    public void RequestCompletion(VimCompletionKind kind)
    {
        if (_surface.IsCompletionOpen) _surface.MoveCompletionSelection(kind == VimCompletionKind.Previous ? -1 : 1);
        else _surface.TriggerCompletion();
    }

    public bool InsertTypedText(string text)
    {
        if (_surface.IsReadOnly) return false;
        using (_buffer.BeginTyping()) _surface.TypeText(text);
        return true;
    }

    /// <summary>
    /// af/if, ac/ic, aa/ia and ]m [m ]M [M: nvim-treesitter-textobjects' captures in the document's tree-sitter tree. The editor computes them on
    /// the syntax lane; the answer comes back as an engine call of its own on the UI thread (the engine has suspended the command and queues
    /// the keys typed meanwhile), so the UI thread never waits for the lane. False at once for a document tree-sitter does not parse.
    /// </summary>
    public bool RequestSyntaxRanges(VimSyntaxObjectKind kind, bool inner, Action<IReadOnlyList<VimSyntaxRange>?> answer)
    {
        var capture = kind switch
        {
            VimSyntaxObjectKind.Function => "function",
            VimSyntaxObjectKind.Class => "class",
            _ => "parameter",
        } + (inner ? ".inner" : ".outer");
        if (View is not { } view || _surface.RequestSyntaxObjects(capture) is not { } task) return false;
        _ = DeliverAsync();
        return true;

        async Task DeliverAsync()
        {
            IReadOnlyList<TextRange>? found = null;
            try { found = await task.ConfigureAwait(false); } // completes off the UI thread; nothing waits for it
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            IReadOnlyList<VimSyntaxRange>? ranges = found?.Select(r => new VimSyntaxRange(r.Start, r.End)).ToArray();
            _owner.PostToEngine(view, () => answer(ranges));
        }
    }
}
