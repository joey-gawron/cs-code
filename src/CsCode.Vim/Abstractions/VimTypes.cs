namespace CsCode.Vim;

public enum VimMode
{
    Normal,
    Insert,
    Replace,
    Visual,
    VisualLine,
    VisualBlock,
    /// <summary>An operator (d, c, y ...) is waiting for its motion.</summary>
    OperatorPending,
    /// <summary>':' prompt, or '/' '?' search prompt.</summary>
    CommandLine,
    /// <summary>Insert mode, executing a single Normal command after Ctrl-O.</summary>
    InsertNormal,
}

public enum VimKeyResult
{
    /// <summary>The engine consumed the key; the editor must not see it.</summary>
    Handled,
    /// <summary>Not a Vim key in the current state (Vim disabled, `vim.handleKeys` says false, Ctrl-S ...): the editor handles it normally.</summary>
    PassThrough,
}

public enum VimRegisterType { Character, Line, Block }

public enum VimSelectionKind { None, Character, Line, Block }

/// <summary>The Visual selection for the host to draw. Character selections include BOTH end characters.</summary>
public readonly record struct VimSelection(
    VimSelectionKind Kind,
    Core.Text.TextPosition Anchor,
    Core.Text.TextPosition Active,
    int BlockStartVcol = 0,
    int BlockEndVcol = 0,
    bool BlockToLineEnd = false)
{
    public static readonly VimSelection None = new(VimSelectionKind.None, default, default);
}

public enum VimCursorShape { Block, Line, Underline, BlockOutline, LineThin, UnderlineThin }

public readonly record struct VimModeInfo(VimCursorShape Shape, string Label, string SubLabel = "");

public enum VimMessageKind { Info, Warning, Error }

public readonly record struct VimMessage(VimMessageKind Kind, string Text);

/// <summary>State of the ':' / '/' / '?' prompt the host renders. <see cref="Active"/> false = closed.</summary>
public readonly record struct VimCommandLine(bool Active, char Prompt, string Text, int CursorIndex);

/// <summary>What to highlight for 'hlsearch'/'incsearch': a .NET regex pattern (already translated) and its options.</summary>
public readonly record struct VimSearchHighlight(string DotNetPattern, bool IgnoreCase, bool Incremental);

public readonly record struct VimViewport(int FirstLine, int LastLine, int Height, int WrapWidth);

public enum VimCompletionKind { Next, Previous, Line, File, Keyword }

public enum VimFoldCommand { Open, Close, Toggle, OpenAll, CloseAll, OpenRecursive, CloseRecursive, Create, Delete, DeleteAll, MoveNext, MovePrevious, MoveToStart, MoveToEnd, Enable, Disable, ToggleEnable }

/// <summary>Window/buffer/file actions the host performs on behalf of Ex commands and Ctrl-W.</summary>
public enum VimEditorAction
{
    Write, WriteQuit, Quit, QuitAll, WriteAll, WriteQuitAll, Edit, NewBuffer, SaveAs, Reload,
    NextBuffer, PreviousBuffer, DeleteBuffer, ListBuffers, GoToBuffer, AlternateBuffer,
    Split, VSplit, NewSplit, NewVSplit, Only, Close,
    FocusLeft, FocusRight, FocusUp, FocusDown, FocusNext, FocusPrevious, FocusTop, FocusBottom, FocusLast,
    ResizeEqual, ResizeMaximize, ResizeIncreaseHeight, ResizeDecreaseHeight, ResizeIncreaseWidth, ResizeDecreaseWidth,
    MoveWindowRight, MoveWindowLeft, MoveWindowUp, MoveWindowDown, ExchangeWindow,
    Help, NextTab, PreviousTab, NewTab, CloseTab, ShowRegisters, ShowMarks,
}

public readonly record struct VimActionArgs(string? Argument = null, bool Bang = false, int? Count = null);

/// <summary>The syntax objects of <see cref="IVimHost.RequestSyntaxRanges"/>: <c>f</c>unction, <c>c</c>lass and <c>a</c>rgument/parameter.</summary>
public enum VimSyntaxObjectKind { Function, Class, Parameter }

/// <summary>One syntax object of the buffer: [<paramref name="Start"/>, <paramref name="End"/>), the end exclusive.</summary>
public readonly record struct VimSyntaxRange(Core.Text.TextPosition Start, Core.Text.TextPosition End);

/// <summary>Comment delimiters of the buffer's language, for the built-in commentary emulation.</summary>
public readonly record struct VimCommentTokens(string? Line, string? BlockOpen, string? BlockClose);
