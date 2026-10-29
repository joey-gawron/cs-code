namespace CsCode.Core.Text;

/// <summary>Zero-based line/character (UTF-16 code units), identical to LSP and the VS Code API.</summary>
public readonly record struct TextPosition(int Line, int Character) : IComparable<TextPosition>
{
    public static readonly TextPosition Zero = new(0, 0);

    public int CompareTo(TextPosition other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Character.CompareTo(other.Character);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
    public override string ToString() => $"{Line}:{Character}";
}

/// <summary>Half-open range [Start, End).</summary>
public readonly record struct TextRange(TextPosition Start, TextPosition End)
{
    public bool IsEmpty => Start == End;
    public bool Contains(TextPosition p) => p >= Start && p < End;
    public bool ContainsInclusive(TextPosition p) => p >= Start && p <= End;
    public static TextRange At(int line, int character) => new(new(line, character), new(line, character));
    public static TextRange FromLines(int startLine, int startChar, int endLine, int endChar) =>
        new(new(startLine, startChar), new(endLine, endChar));
    public override string ToString() => $"{Start}-{End}";
}

public sealed record TextEdit(TextRange Range, string NewText)
{
    public static TextEdit Insert(TextPosition at, string text) => new(new(at, at), text);
    public static TextEdit Delete(TextRange range) => new(range, string.Empty);
}

/// <summary>One content change inside a <see cref="TextDocumentChangedEventArgs"/>.</summary>
/// <param name="Range">Range replaced, expressed against the document state BEFORE this change was applied.</param>
/// <param name="RangeOffset">Absolute UTF-16 offset of the range start (before the change).</param>
/// <param name="RangeLength">Length in UTF-16 code units of the replaced range.</param>
/// <param name="Text">Replacement text.</param>
public readonly record struct TextContentChange(TextRange Range, int RangeOffset, int RangeLength, string Text);

/// <summary>
/// Changes are ordered; apply sequentially, each against the result of the previous (LSP didChange semantics).
/// </summary>
public sealed record TextDocumentChangedEventArgs(
    ITextDocument Document,
    int Version,
    IReadOnlyList<TextContentChange> Changes);
