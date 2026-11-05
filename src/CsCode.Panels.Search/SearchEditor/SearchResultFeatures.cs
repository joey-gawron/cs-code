using System.Globalization;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;

namespace CsCode.Panels.Search.SearchEditor;

/// <summary>A result line's target: the file and the position in it, and the part of the line that leads there.</summary>
internal sealed record SearchResultTarget(Uri Uri, TextPosition Position, TextRange Origin);

/// <summary>
/// The <c>search-result</c> language's features (VS Code's <c>search-result</c> extension): go to definition (F12, Ctrl/⌘+click) on a result line
/// opens the file at that line and column, a path line is a document link to its file, the matches are highlighted with the query the results
/// came from, and a double-click on a result opens it (<c>search.searchEditor.doubleClickBehaviour</c>). Everything reads the document's text, so
/// it works the same for a saved <c>.code-search</c> file.
/// </summary>
internal sealed class SearchResultFeatures(SearchEditorService owner) : IDefinitionProvider, IDocumentLinkProvider, IMatchHighlightProvider, IDoubleClickHandler
{
    /// <summary>At most this many highlighted ranges per document (the results are capped by <c>search.maxResults</c> anyway).</summary>
    internal const int MaxHighlights = 50_000;
    internal const int MaxLinks = 20_000;
    private static readonly TimeSpan HighlightRegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Emitter<Uri> _changed = new();

    public IEventSource<Uri> Changed => _changed;

    internal void FireChanged(Uri uri) => _changed.Fire(uri);

    // ------------------------------------------------------------------------------------------- navigation

    public ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct)
    {
        if (Resolve(document, position, owner.Folders, ct) is not { } t) return ValueTask.FromResult<IReadOnlyList<LocationLink>?>(null);
        var context = new TextRange(new TextPosition(Math.Max(0, t.Position.Line - 3), 0), new TextPosition(t.Position.Line + 3, 0));
        return ValueTask.FromResult<IReadOnlyList<LocationLink>?>([new LocationLink(t.Uri, context, new TextRange(t.Position, t.Position), t.Origin)]);
    }

    public ValueTask<IReadOnlyList<DocumentLink>?> ProvideDocumentLinksAsync(ITextDocument document, CancellationToken ct)
    {
        var folders = owner.Folders;
        var count = document.LineCount;
        var first = BodyStart(document);
        var links = new List<DocumentLink>();
        for (var i = first; i < count && links.Count < MaxLinks; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var line = document.GetLineText(i);
            if (line.Length < 2 || char.IsWhiteSpace(line[0]) || line[^1] != ':') continue;
            if (SearchEditorFormat.Classify(line) != SearchResultLineKind.File) continue;
            if (SearchResultPaths.Resolve(line[..^1], folders, document.Uri) is { } uri)
                links.Add(new DocumentLink(new TextRange(new TextPosition(i, 0), new TextPosition(i, line.Length - 1)), uri, "Open " + line[..^1]));
        }
        return ValueTask.FromResult<IReadOnlyList<DocumentLink>?>(links);
    }

    /// <summary>
    /// The target of the result line at <paramref name="position"/> (null for anything else): a path line leads to its file, a result line to its
    /// line, at the column under the position when it is in the text (counting the characters an elision stands for), else the line's start.
    /// </summary>
    internal static SearchResultTarget? Resolve(ITextDocument document, TextPosition position, IReadOnlyList<Uri> folders, CancellationToken ct = default)
    {
        if (position.Line < BodyStart(document) || position.Line >= document.LineCount) return null;
        var line = document.GetLineText(position.Line);
        switch (SearchEditorFormat.Classify(line))
        {
            case SearchResultLineKind.File:
                return SearchResultPaths.Resolve(line[..^1], folders, document.Uri) is { } file
                    ? new SearchResultTarget(file, TextPosition.Zero, new TextRange(new TextPosition(position.Line, 0), new TextPosition(position.Line, line.Length)))
                    : null;
            case SearchResultLineKind.Match or SearchResultLineKind.Context:
                break;
            default:
                return null;
        }
        Match m;
        try { m = SearchEditorFormat.ResultLine.Match(line); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1) return null;
        if (FileOf(document, position.Line, ct) is not { } label || SearchResultPaths.Resolve(label, folders, document.Uri) is not { } uri) return null;

        var targetLine = number - 1;
        var metadata = m.Groups[1].Length + m.Groups[2].Length + m.Groups[3].Length;
        var row = position.Line;
        if (position.Character < metadata)
            return new SearchResultTarget(uri, new TextPosition(targetLine, 0), new TextRange(new TextPosition(row, 0), new TextPosition(row, Math.Max(0, metadata - 1))));

        // segments of shown text between elisions, each with the target column it starts at (VS Code's algorithm)
        var lastEnd = metadata;
        var offset = 0;
        try
        {
            for (var e = SearchEditorFormat.Elision.Match(line, metadata); e.Success; e = e.NextMatch())
            {
                if (position.Character >= lastEnd && position.Character < e.Index)
                    return Target(lastEnd, e.Index, offset);
                offset += e.Index - lastEnd + int.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);
                lastEnd = e.Index + e.Length;
                if (position.Character < lastEnd) // on the elision itself: where the skipped text ends
                    return new SearchResultTarget(uri, new TextPosition(targetLine, offset), new TextRange(new TextPosition(row, e.Index), new TextPosition(row, lastEnd)));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // a pathological line: the column is approximate
        }
        return Target(lastEnd, line.Length, offset);

        SearchResultTarget Target(int from, int to, int targetStart) =>
            new(uri, new TextPosition(targetLine, targetStart + Math.Max(0, Math.Min(position.Character, to) - from)),
                new TextRange(new TextPosition(row, from), new TextPosition(row, Math.Max(from, to))));
    }

    /// <summary>The path of the block that holds <paramref name="line"/>: the nearest path line above it.</summary>
    private static string? FileOf(ITextDocument document, int line, CancellationToken ct)
    {
        var first = BodyStart(document);
        for (var i = line - 1; i >= first; i--)
        {
            if (((line - i) & 4095) == 0) ct.ThrowIfCancellationRequested();
            var text = document.GetLineText(i);
            if (text.Length == 0 || char.IsWhiteSpace(text[0])) continue;
            return SearchEditorFormat.Classify(text) == SearchResultLineKind.File ? text[..^1] : null;
        }
        return null;
    }

    /// <summary>The first line that can hold results: after the header, the empty line and the summary line.</summary>
    internal static int BodyStart(ITextDocument document) =>
        SearchEditorFormat.CountHeaderLines(document.GetLineText, document.LineCount) + 2;

    // ------------------------------------------------------------------------------------------- highlights

    public ValueTask<IReadOnlyList<TextRange>?> ProvideMatchHighlightsAsync(ITextDocument document, CancellationToken ct)
    {
        var config = owner.HighlightConfigFor(document);
        if (config is null || config.Query.Length == 0) return ValueTask.FromResult<IReadOnlyList<TextRange>?>(null);
        TextMatcher matcher;
        try { matcher = TextMatcher.Create(config.ToQuery(), new SearchLimits { RegexTimeout = HighlightRegexTimeout }); }
        catch (SearchPatternException) { return ValueTask.FromResult<IReadOnlyList<TextRange>?>(null); }

        var text = document.GetText(); // one snapshot: every line from the same version
        var ranges = new List<TextRange>();
        var span = text.AsSpan();
        var lineNo = 0;
        var pos = 0;
        var first = -1;
        try
        {
            while (pos <= span.Length && ranges.Count < MaxHighlights)
            {
                var rest = span[pos..];
                var nl = rest.IndexOfAny('\r', '\n');
                var line = nl < 0 ? rest : rest[..nl];
                if (first < 0)
                {
                    if (line.Length == 0 || line[0] != '#') first = lineNo + 2; // the header ends here; the summary follows the empty line
                }
                else if (lineNo >= first && line.Length > 0 && char.IsWhiteSpace(line[0]))
                {
                    HighlightLine(line, lineNo, matcher, ranges);
                }
                if (nl < 0) break;
                pos += nl + 1;
                if (rest[nl] == '\r' && nl + 1 < rest.Length && rest[nl + 1] == '\n') pos++;
                lineNo++;
                if ((lineNo & 1023) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // a pathological pattern: what was found so far
        }
        return ValueTask.FromResult<IReadOnlyList<TextRange>?>(ranges);
    }

    /// <summary>The query's matches in the text of a match line (<c>  N: text</c>); context lines are not highlighted, as in VS Code.</summary>
    private static void HighlightLine(ReadOnlySpan<char> line, int lineNo, TextMatcher matcher, List<TextRange> ranges)
    {
        // "  " + spaces + digits + ": "
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        var digits = i;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == digits || i + 1 >= line.Length || line[i] != ':' || line[i + 1] != ' ') return;
        var start = i + 2;
        var text = line[start..];
        var from = 0;
        int idx;
        while (ranges.Count < MaxHighlights && (idx = matcher.Find(text, from, out var len)) >= 0)
        {
            ranges.Add(new TextRange(new TextPosition(lineNo, start + idx), new TextPosition(lineNo, start + idx + len)));
            from = idx + Math.Max(1, len);
        }
    }

    // ------------------------------------------------------------------------------------------- double-click

    public bool OnDoubleClick(ITextDocument document, TextPosition position)
    {
        var behaviour = owner.DoubleClickBehaviour;
        if (behaviour == SearchEditorService.DoubleClickSelectWord) return false;
        if (position.Line < BodyStart(document) || position.Line >= document.LineCount) return false;
        if (SearchEditorFormat.Classify(document.GetLineText(position.Line)) == SearchResultLineKind.Other) return false;
        _ = owner.NavigateAsync(document, position, toSide: behaviour == SearchEditorService.DoubleClickOpenToSide);
        return true;
    }
}
