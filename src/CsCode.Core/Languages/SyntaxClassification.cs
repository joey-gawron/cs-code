using CsCode.Core.Lifecycle;

namespace CsCode.Core.Languages;

/// <summary>
/// What a run of a line is, as the editor's own syntax tokens say (TextMate scopes, or tree-sitter captures mapped onto the same scopes), for
/// features that read text by kind instead of colouring it: the spell checker reads comments, strings and prose, never code. Additive (spell).
/// </summary>
public enum SyntaxTextKind : byte
{
    /// <summary>Code: keywords, identifiers, operators, punctuation; also code embedded in prose (a Markdown fence's content).</summary>
    Code = 0,
    /// <summary>A comment (line, block or documentation comment), without the markup inside it.</summary>
    Comment = 1,
    /// <summary>A string literal's text (not its escapes, placeholders or interpolated code).</summary>
    String = 2,
    /// <summary>Prose of a text language (a grammar whose root scope is <c>text.*</c>: Markdown, a commit message, HTML text).</summary>
    Text = 3,
    /// <summary>
    /// Text no checker should read as words: escapes, format placeholders, regular expressions, URLs, inline code and raw spans, markup tags and
    /// attributes inside comments (C# XML documentation), paths of include and import statements.
    /// </summary>
    Ignored = 4,
}

/// <summary>One run of a line: from <paramref name="Start"/> (UTF-16 column) to the next run's start, or the end of the line.</summary>
public readonly record struct SyntaxTextRun(int Start, SyntaxTextKind Kind);

/// <summary>Why a document has (or has no) runs.</summary>
public enum SyntaxClassificationState
{
    /// <summary>The editor has nothing for the document (no view has shown it yet, or it is not an editor document).</summary>
    Unavailable = 0,
    /// <summary>The language has no grammar: the editor cannot tell comments from code.</summary>
    NoGrammar = 1,
    /// <summary>Lines that have tokens have runs; <see cref="SyntaxClassification.TryGetRuns"/> says which.</summary>
    Tokenized = 2,
    /// <summary>The document is open in large-file mode: no language feature looks at it.</summary>
    LargeFile = 3,
}

/// <summary>
/// The classification of one document at one moment: an immutable capture of the editor's token rows (the rows are shared, not copied; the
/// editor replaces a row, it never changes one). Tokens are computed off the UI thread, viewport first, so lines far from anything shown may
/// have none yet, and a line edited a moment ago may still carry its previous tokens until it is tokenised again (the source then raises
/// <see cref="ISyntaxClassificationService.Changed"/> for it). Readable from any thread.
/// </summary>
public abstract class SyntaxClassification
{
    public abstract SyntaxClassificationState State { get; }

    /// <summary>The number of lines the tokens describe (the document's line count when they were captured).</summary>
    public abstract int LineCount { get; }

    /// <summary>
    /// Appends the runs of <paramref name="line"/> to <paramref name="runs"/> (cleared first) and returns true, or returns false when the line has
    /// no tokens (not tokenised yet, or out of range). Adjacent runs of one kind are merged; the first run starts at column 0.
    /// </summary>
    public abstract bool TryGetRuns(int line, List<SyntaxTextRun> runs);

    /// <summary>A classification without runs, in the given state.</summary>
    public static SyntaxClassification Empty(SyntaxClassificationState state, int lineCount = 0) => new EmptyClassification(state, lineCount);

    private sealed class EmptyClassification(SyntaxClassificationState state, int lineCount) : SyntaxClassification
    {
        public override SyntaxClassificationState State => state;
        public override int LineCount => lineCount;

        public override bool TryGetRuns(int line, List<SyntaxTextRun> runs)
        {
            runs.Clear();
            return false;
        }
    }
}

/// <summary>Lines [<paramref name="FirstLine"/>, <paramref name="LastLine"/>] of <paramref name="Uri"/> got new tokens (or lost them).</summary>
public sealed record SyntaxClassificationChange(Uri Uri, int FirstLine, int LastLine);

/// <summary>
/// The editor's syntax tokens of open documents, by kind (implemented by the editor over its token store; registered by <c>AddCsCodeEditor</c>).
/// Consumers capture a document, read the runs off the UI thread, and capture again when <see cref="Changed"/> names the document. Additive (spell).
/// </summary>
public interface ISyntaxClassificationService
{
    /// <summary>The classification of the open document <paramref name="uri"/> now. Any thread; cheap (a copy of the row references).</summary>
    SyntaxClassification Capture(Uri uri);

    /// <summary>Fires (on the UI thread, coalesced per document) when lines of a document got new tokens.</summary>
    IEventSource<SyntaxClassificationChange> Changed { get; }
}
