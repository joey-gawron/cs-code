using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Engine;

/// <summary>What to search for and where. Immutable; one instance per search run.</summary>
/// <param name="Pattern">Literal text or a .NET regular expression (<see cref="IsRegex"/>).</param>
/// <param name="MultiLine">Regex mode only (or a literal containing a line break): the pattern may match across line breaks, which needs the whole file in memory (bounded by the max file size).</param>
/// <param name="Include">Comma separated globs ("*.cs, src/**"); empty = everything. A bare name ("src") means that file or folder at any depth.</param>
/// <param name="Exclude">Comma separated globs, same syntax as <paramref name="Include"/>.</param>
/// <param name="UseExcludeSettings">Honour <c>files.exclude</c> and <c>search.exclude</c>.</param>
/// <param name="UseIgnoreFiles">Honour <c>.gitignore</c>.</param>
/// <param name="OpenEditorsOnly">Search only the documents that are open in editors.</param>
/// <param name="Roots">Restrict the search to these folders (default: every workspace folder).</param>
/// <param name="Selection">Search only inside this range of one document ("search in selection").</param>
public sealed record SearchQuery(
    string Pattern,
    bool IsRegex = false,
    bool MatchCase = false,
    bool WholeWord = false,
    bool MultiLine = false,
    string? Include = null,
    string? Exclude = null,
    bool UseExcludeSettings = true,
    bool UseIgnoreFiles = true,
    bool OpenEditorsOnly = false,
    IReadOnlyList<Uri>? Roots = null,
    SelectionScope? Selection = null);

/// <summary>A range of one document to confine the search to.</summary>
public sealed record SelectionScope(Uri Uri, TextRange Range);

/// <summary>
/// One hit. <see cref="Range"/> is line/character (UTF-16, zero-based) exactly as <c>INavigationService.OpenAsync</c> wants it.
/// The preview is the (leading-whitespace trimmed, control-characters-to-space, clipped to a bounded length) text of the line the match
/// starts on; <see cref="PreviewMatchStart"/>/<see cref="PreviewMatchLength"/> index into it.
/// </summary>
/// <param name="MatchedText">The matched text, clipped to 256 characters (used for replace previews).</param>
public readonly record struct SearchMatch(TextRange Range, string Preview, int PreviewMatchStart, int PreviewMatchLength, string MatchedText)
{
    public int Line => Range.Start.Line;
    public int Column => Range.Start.Character;
}

/// <summary>All matches of one file, in document order. (A consumer should still tolerate the same <see cref="Uri"/> arriving twice and append.)</summary>
/// <param name="FromMemory">The text was searched from an open document, not from disk.</param>
/// <param name="DocumentVersion">Version of the open document that was searched (null when read from disk).</param>
/// <param name="Encoding">Name of the encoding the file was decoded with (null for open documents).</param>
/// <param name="LineEnding">The line terminator the file mainly uses (\n, \r\n or \r), null when it has no line break.</param>
public sealed record FileMatches(Uri Uri, IReadOnlyList<SearchMatch> Matches, bool FromMemory = false, int? DocumentVersion = null, string? Encoding = null, string? LineEnding = null);

/// <summary>A small immutable batch of results, delivered at most about every 16 ms.</summary>
public sealed record SearchBatch(int Sequence, IReadOnlyList<FileMatches> Files);

public sealed record SearchProgress(int FilesScanned, int FilesMatched, int MatchCount, bool Enumerating);

public enum SearchOutcome
{
    /// <summary>Every candidate file was searched.</summary>
    Completed,
    /// <summary>Stopped by <see cref="SearchSession.Cancel"/>; results delivered so far are valid.</summary>
    Cancelled,
    /// <summary>Stopped because <c>search.maxResults</c> was reached.</summary>
    Truncated,
    /// <summary>The query is invalid (bad regex) or the search could not run; see <see cref="SearchStats.Error"/>.</summary>
    Failed,
}

public sealed record SearchStats(
    int FilesScanned,
    int FilesMatched,
    int MatchCount,
    int SkippedBinary,
    int SkippedTooLarge,
    int SkippedTimedOut,
    int SkippedUnreadable,
    bool Truncated,
    TimeSpan Elapsed,
    string? Error = null)
{
    public static readonly SearchStats Empty = new(0, 0, 0, 0, 0, 0, 0, false, TimeSpan.Zero);
}

public sealed record SearchCompletion(SearchOutcome Outcome, SearchStats Stats)
{
    public string? Error => Stats.Error;
}

/// <summary>Tunables, normally read from settings by <see cref="FromConfiguration"/>.</summary>
public sealed record SearchLimits
{
    public const string MaxResultsKey = "search.maxResults";
    public const string MaxFileSizeKey = "search.maxFileSizeBytes";
    public const string RegexTimeoutKey = "search.regexTimeoutMs";
    public const string ExcludeKey = "search.exclude";
    public const string UseIgnoreFilesKey = "search.useIgnoreFiles";

    /// <summary>Hard cap on delivered matches (the search stops and reports truncation).</summary>
    public int MaxResults { get; init; } = 20_000;
    /// <summary>Files larger than this are skipped (counted in <see cref="SearchStats.SkippedTooLarge"/>).</summary>
    public long MaxFileSizeBytes { get; init; } = 5L * 1024 * 1024;
    /// <summary>Per regex match operation. A file whose match times out is abandoned.</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);
    /// <summary>After this many files abandoned by a regex timeout the whole search stops with an error (the regex is too expensive).</summary>
    public int MaxTimedOutFiles { get; init; } = 8;
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    /// <summary>Longest preview text kept per match.</summary>
    public int MaxPreviewChars { get; init; } = 300;
    /// <summary>A line longer than this gets a clipped preview centred on the match.</summary>
    public int LongLineThreshold { get; init; } = 10_000;
    /// <summary>A line longer than this is split (matches that straddle the split are missed). Bounds the per-worker line buffer.</summary>
    public int MaxLineChars { get; init; } = 2_000_000;
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromMilliseconds(16);
    /// <summary>Bytes read per chunk (a file is never read whole unless a multi-line regex needs it).</summary>
    public int ReadChunkBytes { get; init; } = 64 * 1024;
    public int QueueCapacity { get; init; } = 512;

    public static SearchLimits FromConfiguration(IConfigurationService? config)
    {
        var d = new SearchLimits();
        if (config is null) return d;
        return d with
        {
            MaxResults = (int)Math.Clamp(ReadNumber(config, MaxResultsKey, d.MaxResults), 1, int.MaxValue),
            MaxFileSizeBytes = Math.Max(0, ReadNumber(config, MaxFileSizeKey, d.MaxFileSizeBytes)),
            RegexTimeout = TimeSpan.FromMilliseconds(Math.Clamp(ReadNumber(config, RegexTimeoutKey, (long)d.RegexTimeout.TotalMilliseconds), 10, 60_000)),
        };
    }

    /// <summary>
    /// A numeric setting, or <paramref name="fallback"/> when unset or not a number. Goes through <c>GetRaw</c> (null when unset) rather than
    /// <c>Get&lt;long&gt;</c>, whose result for a missing key differs between configuration implementations (default(T) vs the supplied default).
    /// </summary>
    public static long ReadNumber(IConfigurationService config, string key, long fallback) =>
        config.GetRaw(key) is { ValueKind: System.Text.Json.JsonValueKind.Number } e && e.TryGetInt64(out var v) ? v : fallback;

    public static bool ReadBool(IConfigurationService? config, string key, bool fallback) =>
        config?.GetRaw(key) is { } e && e.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? e.GetBoolean() : fallback;
}
