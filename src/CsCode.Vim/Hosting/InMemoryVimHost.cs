using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>
/// Headless <see cref="IVimHost"/> used by every test (and handy for embedding): the buffer is an <see cref="InMemoryTextDocument"/>
/// (or any <see cref="IVimBuffer"/>), and everything the engine tells the host is recorded for assertions. It also simulates a
/// viewport (<see cref="WindowHeight"/>, <see cref="TopLine"/>, optional wrapping) so scrolling and display-line motions are testable.
/// </summary>
public sealed class InMemoryVimHost : IVimHost
{
    private const int MaxLog = 1000;
    private IVimBuffer _buffer;

    public InMemoryVimHost(string text = "", IVimTimer? timer = null) : this(
        new TextDocumentVimBuffer(new InMemoryTextDocument(new Uri("file:///test.txt"), "plaintext", text)), timer)
    { }

    public InMemoryVimHost(IVimBuffer buffer, IVimTimer? timer = null)
    {
        _buffer = buffer;
        Timer = timer ?? new ManualVimTimer();
    }

    public IVimBuffer Buffer => _buffer;
    public IVimTimer Timer { get; }
    public ManualVimTimer? ManualTimer => Timer as ManualVimTimer;

    /// <summary>Test hook: swap the current buffer (as :e / :bn would do).</summary>
    public void SwitchBuffer(IVimBuffer buffer) { _buffer = buffer; Caret = default; }

    // ---- recorded feedback
    public TextPosition Caret { get; set; }
    public bool CaretInsertLike { get; private set; }
    public VimSelection Selection { get; private set; } = VimSelection.None;
    public VimMode Mode { get; private set; } = VimMode.Normal;
    public VimModeInfo ModeInfo { get; private set; }
    public List<VimMessage> Messages { get; } = [];
    public string? LastMessage => Messages.Count > 0 ? Messages[^1].Text : null;
    public string ShowCommand { get; private set; } = "";
    public char? Recording { get; private set; }
    public VimCommandLine CommandLine { get; private set; }
    public VimSearchHighlight? SearchHighlight { get; private set; }
    public (TextPosition Start, TextPosition End, bool Linewise)? LastYankHighlight { get; private set; }
    public List<(VimEditorAction Action, VimActionArgs Args)> Actions { get; } = [];
    public List<(string Id, IReadOnlyList<object?>? Args)> Commands { get; } = [];
    public List<(VimFoldCommand Command, int Start, int End)> FoldCommands { get; } = [];
    public List<VimCompletionKind> CompletionRequests { get; } = [];

    // ---- simulated environment
    public string? Clipboard { get; set; }
    public VimRegisterType ClipboardType { get; set; }
    public int TopLine { get; set; }
    public int WindowHeight { get; set; } = 22;
    public int WrapWidth { get; set; }
    public int WindowWidth { get; set; } = 80;
    public VimCommentTokens CommentTokens { get; set; } = new("//", "/*", "*/");
    public Func<int, int, bool>? Formatter { get; set; }
    public List<(int Start, int End)> ClosedFolds { get; } = [];
    public Func<VimEditorAction, VimActionArgs, bool>? ActionHandler { get; set; }

    public TextPosition GetCaret() => Caret;

    public void SetCaret(TextPosition position, bool insertLike)
    {
        Caret = position;
        CaretInsertLike = insertLike;
    }

    public void SetSelection(in VimSelection selection) => Selection = selection;

    public void OnModeChanged(VimMode mode, in VimModeInfo info)
    {
        Mode = mode;
        ModeInfo = info;
    }

    public void OnMessage(in VimMessage message)
    {
        if (Messages.Count >= MaxLog) Messages.RemoveRange(0, MaxLog / 2);
        Messages.Add(message);
    }

    public void OnShowCommand(string pendingKeys) => ShowCommand = pendingKeys;
    public void OnRecordingChanged(char? register) => Recording = register;
    public void OnCommandLineChanged(in VimCommandLine commandLine) => CommandLine = commandLine;
    public void OnSearchHighlight(VimSearchHighlight? highlight) => SearchHighlight = highlight;
    public void OnYankHighlight(TextPosition start, TextPosition end, bool linewise) => LastYankHighlight = (start, end, linewise);

    public VimViewport GetViewport()
    {
        var last = Math.Max(0, _buffer.LineCount - 1);
        var first = Math.Clamp(TopLine, 0, last);
        // Fill the window with display rows (a wrapped line takes several).
        var rows = 0;
        var l = first;
        while (l <= last && rows + GetDisplayRows(l) <= WindowHeight) { rows += GetDisplayRows(l); l++; }
        return new VimViewport(first, Math.Max(first, Math.Min(last, l - 1)), WindowHeight, WrapWidth);
    }

    public void SetTopLine(int firstLine) => TopLine = Math.Clamp(firstLine, 0, Math.Max(0, _buffer.LineCount - 1));

    public int GetDisplayRows(int line)
    {
        if (WrapWidth <= 0 || line < 0 || line >= _buffer.LineCount) return 1;
        var w = VimText.DisplayWidth(_buffer.GetLine(line), 8);
        return Math.Max(1, (w + WrapWidth - 1) / WrapWidth);
    }

    public bool TryGetClosedFold(int line, out int startLine, out int endLine)
    {
        foreach (var (s, e) in ClosedFolds)
            if (line >= s && line <= e) { startLine = s; endLine = e; return true; }
        startLine = endLine = line;
        return false;
    }

    public void FoldCommand(VimFoldCommand command, int startLine, int endLine)
    {
        FoldCommands.Add((command, startLine, endLine));
        switch (command)
        {
            case VimFoldCommand.Close or VimFoldCommand.CloseRecursive or VimFoldCommand.Create: ClosedFolds.Add((startLine, endLine)); break;
            case VimFoldCommand.Open or VimFoldCommand.OpenRecursive or VimFoldCommand.Delete: ClosedFolds.RemoveAll(f => f.Start <= startLine && f.End >= startLine); break;
            case VimFoldCommand.OpenAll or VimFoldCommand.DeleteAll: ClosedFolds.Clear(); break;
        }
    }

    public string? ReadClipboard() => Clipboard;

    public void WriteClipboard(string text, VimRegisterType type)
    {
        Clipboard = text;
        ClipboardType = type;
    }

    public bool PerformAction(VimEditorAction action, in VimActionArgs args)
    {
        if (Actions.Count >= MaxLog) Actions.RemoveRange(0, MaxLog / 2);
        Actions.Add((action, args));
        return ActionHandler?.Invoke(action, args) ?? true;
    }

    public void ExecuteCommand(string commandId, IReadOnlyList<object?>? args)
    {
        if (Commands.Count >= MaxLog) Commands.RemoveRange(0, MaxLog / 2);
        Commands.Add((commandId, args));
    }

    public VimCommentTokens GetCommentTokens() => CommentTokens;
    public bool FormatLines(int startLine, int endLine) => Formatter?.Invoke(startLine, endLine) ?? false;
    public void RequestCompletion(VimCompletionKind kind) => CompletionRequests.Add(kind);
}
