namespace CsCode.Core.Platform;

// Additive Core contract (Remote, wave 3): text search that runs where the files are. Implemented by CsCode.Remote (the server walks and scans a
// remote workspace in parallel; only the matches cross the network); consumed by the Find in Files engine (CsCode.Panels.Search) for folders of
// that scheme, which then reads no file content itself.

/// <param name="Folders">The folders to search (one scheme: the provider's).</param>
/// <param name="ExcludeGlobs">Globs the walk prunes (<c>files.exclude</c>/<c>search.exclude</c>): an optimisation, the caller still filters.</param>
/// <param name="MaxResults">Stop after this many matches.</param>
/// <param name="MaxFileBytes">Files larger than this are skipped.</param>
public sealed record TextSearchQuery(
    IReadOnlyList<Uri> Folders,
    string Pattern,
    bool IsRegex,
    bool MatchCase,
    bool WholeWord,
    IReadOnlyList<string> ExcludeGlobs,
    bool UseIgnoreFiles,
    int MaxResults,
    long MaxFileBytes);

/// <summary>One match: 0-based line and UTF-16 column/length; <paramref name="LineText"/> is the line (or the part of a long line starting at <paramref name="LineTextStart"/>).</summary>
public sealed record TextSearchHit(Uri Uri, int Line, int Column, int Length, string LineText, int LineTextStart = 0);

/// <summary>Text search over the folders of one URI scheme, run where the files are. Batches stream in as they are found; cancelling stops the work there.</summary>
public interface ITextSearchProvider
{
    string Scheme { get; }

    /// <summary>
    /// Matches in batches (files may interleave: hits of one file can arrive in several batches). An invalid pattern fails the enumeration with an
    /// <see cref="ArgumentException"/>; an unreachable folder with an <see cref="IOException"/>.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<TextSearchHit>> SearchAsync(TextSearchQuery query, CancellationToken ct = default);
}
