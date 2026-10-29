using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Engine;

/// <param name="ReplaceText">The replacement. Regex queries expand <c>$1</c>, <c>${name}</c>, <c>$&amp;</c>, and the escapes <c>\n</c> <c>\t</c> <c>\\</c>; literal queries use it verbatim.</param>
/// <param name="PreserveCase">All-upper matches get an upper-case replacement, all-lower a lower-case one, a capitalised match a capitalised one.</param>
public sealed record ReplaceOptions(string ReplaceText, bool PreserveCase = false);

/// <summary>What to replace in one file: the listed ranges (as reported by the search) or, when null, every match.</summary>
public sealed record ReplaceTarget(Uri Uri, IReadOnlyList<TextRange>? Ranges = null);

public sealed record ReplaceItem(Uri Uri, TextRange Range, string OldText, string NewText);

/// <summary>
/// The edits for one file. <see cref="Version"/> is set when the file was an open document (the edit is only valid for that version).
/// <see cref="Fingerprint"/> identifies the text the edits were computed from; <see cref="ReplaceService.ApplyAsync"/> re-reads the file right
/// before editing it and skips it when the text is no longer the same (null: only the version is checked).
/// </summary>
public sealed record FileReplacePlan(Uri Uri, IReadOnlyList<TextEdit> Edits, IReadOnlyList<ReplaceItem> Items, int? Version, string? Fingerprint = null);

public sealed record ReplaceSkip(Uri Uri, string Reason);

public sealed record ReplacePlan(IReadOnlyList<FileReplacePlan> Files, IReadOnlyList<ReplaceSkip> Skipped)
{
    public int FileCount => Files.Count;
    public int ReplacementCount => Files.Sum(f => f.Edits.Count);
    public WorkspaceEdit ToWorkspaceEdit() => new(Files.ToDictionary(f => f.Uri, f => f.Edits));
}

public sealed record ReplaceResult(int FilesChanged, int Replacements, IReadOnlyList<string> Problems, bool Cancelled, IReadOnlyList<Uri>? ChangedFiles = null);

/// <summary>
/// The compiled half of a replace preview: built once per searched query, so a keystroke in the replace box only builds a new closure.
/// The regex is kept only when it runs on the linear-time <c>NonBacktracking</c> engine. Previews run on the UI thread once per realised
/// row, and a pattern that needs the backtracking engine (lookarounds, backreferences) can spend its whole timeout re-matching the clipped
/// matched text (a trailing <c>(?=;)</c> never matches there), so such a pattern's preview shows the template with its escapes expanded
/// but its <c>$</c> groups not substituted. What is written is always computed from the file.
/// </summary>
public sealed class ReplacePreviewPattern
{
    internal ReplacePreviewPattern(SearchQuery query, Regex? regex)
    {
        Query = query;
        Regex = regex;
    }

    public SearchQuery Query { get; }
    internal Regex? Regex { get; }
}

/// <summary>
/// Replace in files. The search results only remember positions, so the plan is made by searching each file AGAIN (open documents from
/// memory, closed files from disk) and replacing exactly the matches that are still there. A match that moved or vanished since the
/// search is reported as skipped, never replaced blindly. A plan can wait a long time before it is applied (the multi-file confirmation
/// is a toast with no time limit), so each file is re-read once more right before its edit and skipped if its text is no longer the text
/// the plan was made from. The work runs on a dedicated thread; the caller only awaits. Files are applied one at a time through
/// <see cref="INavigationService.ApplyWorkspaceEditAsync"/>, so an open document gets ONE undo step and closed files are edited on disk by
/// the shell (which never sees a multi-file edit it would prompt for again). Limits (file size, regex timeout) are the search's settings.
/// </summary>
public sealed class ReplaceService : IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly IDocumentManager? _documents;
    private readonly IConfigurationService? _config;
    private readonly SearchLimits? _limits;
    private readonly DedicatedThreadScheduler _scheduler = new("cscode-search-replace");

    /// <param name="limits">Fixed limits (tests); when null they are read from <paramref name="configuration"/> for every plan, like the search does.</param>
    public ReplaceService(IWorkspaceService workspace, IDocumentManager? documents = null, SearchLimits? limits = null, IConfigurationService? configuration = null)
    {
        _workspace = workspace;
        _documents = documents;
        _limits = limits;
        _config = configuration;
    }

    private SearchLimits Limits => _limits ?? SearchLimits.FromConfiguration(_config);

    /// <summary>Plans the replacement on the replace thread; the synchronous part on the caller is one enqueue.</summary>
    public Task<ReplacePlan> PlanAsync(SearchQuery query, IReadOnlyList<ReplaceTarget> targets, ReplaceOptions options, CancellationToken ct = default) =>
        Task.Factory.StartNew(() => PlanCoreAsync(query, targets, options, ct), ct, TaskCreationOptions.DenyChildAttach, _scheduler).Unwrap();

    private async Task<ReplacePlan> PlanCoreAsync(SearchQuery query, IReadOnlyList<ReplaceTarget> targets, ReplaceOptions options, CancellationToken ct)
    {
        var limits = Limits;
        TextMatcher matcher;
        try { matcher = TextMatcher.Create(query, limits); }
        catch (SearchPatternException ex) { return new ReplacePlan([], targets.Select(t => new ReplaceSkip(t.Uri, ex.Message)).ToArray()); }

        var files = new List<FileReplacePlan>();
        var skipped = new List<ReplaceSkip>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await ReadContentAsync(target.Uri, limits.MaxFileSizeBytes, ct).ConfigureAwait(true);
                if (content.Problem is not null) { skipped.Add(new ReplaceSkip(target.Uri, content.Problem)); continue; }

                var items = FindReplacements(target.Uri, content.Text!, content.LineEnding, matcher, options);
                if (target.Ranges is { } wanted)
                {
                    var set = new HashSet<TextRange>(wanted);
                    var found = items.Where(i => set.Contains(i.Range)).ToList();
                    var missing = set.Count - found.Count;
                    if (missing > 0) skipped.Add(new ReplaceSkip(target.Uri, missing == 1 ? "1 match changed since the search" : $"{missing} matches changed since the search"));
                    items = found;
                }
                if (items.Count == 0) continue;
                files.Add(new FileReplacePlan(target.Uri, items.Select(i => new TextEdit(i.Range, i.NewText)).ToArray(), items, content.Version, Fingerprint(content.Text!)));
            }
            catch (RegexMatchTimeoutException)
            {
                skipped.Add(new ReplaceSkip(target.Uri, "the regular expression timed out on this file"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bug or an exotic file must cost one file, never the whole replace (the fault would surface on the UI thread).
                skipped.Add(new ReplaceSkip(target.Uri, ex.Message));
            }
        }
        return new ReplacePlan(files, skipped);
    }

    /// <summary>
    /// Applies a plan file by file. Returns what happened; never throws for a single bad file. Right before each file is edited it is read
    /// again (open document or disk, whichever it is NOW) and skipped when its text is no longer the one the plan was made from: line/column
    /// edits applied to other text would replace the wrong characters, irreversibly for a closed file. An open document is also skipped when
    /// its version moves between that check and the edit.
    /// </summary>
    public async Task<ReplaceResult> ApplyAsync(ReplacePlan plan, INavigationService navigation, string? label = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var problems = plan.Skipped.Select(s => $"{DisplayName(s.Uri)}: {s.Reason}").ToList();
        int files = 0, replaced = 0, done = 0;
        var changed = new List<Uri>();
        foreach (var file in plan.Files)
        {
            if (ct.IsCancellationRequested) return new ReplaceResult(files, replaced, problems, true, changed);
            progress?.Report(++done);
            var expectedVersion = file.Version;
            if (file.Fingerprint is not null)
            {
                string? problem;
                try { (problem, expectedVersion) = await VerifyAsync(file, ct).ConfigureAwait(true); }
                catch (OperationCanceledException) { return new ReplaceResult(files, replaced, problems, true, changed); }
                if (problem is not null)
                {
                    problems.Add($"{DisplayName(file.Uri)}: {problem}");
                    continue;
                }
            }
            if (expectedVersion is { } v && _documents?.Get(file.Uri) is { } doc && doc.Version != v)
            {
                problems.Add($"{DisplayName(file.Uri)}: the document changed since the search");
                continue;
            }
            try
            {
                var edit = new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [file.Uri] = file.Edits });
                if (await navigation.ApplyWorkspaceEditAsync(edit, label, ct).ConfigureAwait(true))
                {
                    files++;
                    replaced += file.Edits.Count;
                    changed.Add(file.Uri);
                }
                else problems.Add($"{DisplayName(file.Uri)}: the edit could not be applied");
            }
            catch (OperationCanceledException) { return new ReplaceResult(files, replaced, problems, true, changed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add($"{DisplayName(file.Uri)}: {ex.Message}");
            }
        }
        return new ReplaceResult(files, replaced, problems, false, changed);
    }

    private static string DisplayName(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

    /// <summary>Re-reads the file as it is now (on the replace thread) and compares it with the text the plan was made from.</summary>
    private Task<(string? Problem, int? Version)> VerifyAsync(FileReplacePlan file, CancellationToken ct) =>
        Task.Factory.StartNew(() => VerifyCoreAsync(file, ct), ct, TaskCreationOptions.DenyChildAttach, _scheduler).Unwrap();

    private async Task<(string? Problem, int? Version)> VerifyCoreAsync(FileReplacePlan file, CancellationToken ct)
    {
        try
        {
            var content = await ReadContentAsync(file.Uri, Limits.MaxFileSizeBytes, ct).ConfigureAwait(true);
            if (content.Problem is { } problem) return (problem, null);
            // The text, not the version, decides: a document closed and reopened (or a closed file opened since) restarts its versions.
            return Fingerprint(content.Text!) == file.Fingerprint ? (null, content.Version) : ("the file changed since the search", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ex.Message, null);
        }
    }

    /// <summary>
    /// Identifies a text (SHA-256). Line breaks are normalised first: the edits are line/column based, so text that differs only in its
    /// line-ending style (an editor that normalises them when it opens the file) still gets exactly the right edits.
    /// </summary>
    internal static string Fingerprint(string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (!text.Contains('\r'))
        {
            hash.AppendData(MemoryMarshal.AsBytes(text.AsSpan()));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        Span<char> buffer = stackalloc char[4096];
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                c = '\n';
            }
            buffer[n++] = c;
            if (n == buffer.Length) { hash.AppendData(MemoryMarshal.AsBytes(buffer)); n = 0; }
        }
        hash.AppendData(MemoryMarshal.AsBytes(buffer[..n]));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ------------------------------------------------------------------------------------------------ content

    private readonly record struct Content(string? Text, string? LineEnding, int? Version, string? Problem);

    private async ValueTask<Content> ReadContentAsync(Uri uri, long maxFileSizeBytes, CancellationToken ct)
    {
        if (_documents?.Get(uri) is { } doc)
        {
            var version = doc.Version;
            var text = doc.GetText();
            var eol = (doc as IEditorDocument)?.EndOfLine is { Length: > 0 } e ? e : DetectLineEnding(text);
            return new Content(text, eol, version, null);
        }
        try
        {
            var stat = await _workspace.FileSystem.StatAsync(uri, ct).ConfigureAwait(true);
            if (stat is null) return new Content(null, null, null, "the file no longer exists");
            if (stat.Size > maxFileSizeBytes) return new Content(null, null, null, "the file is too large");
            // A closed file is written to disk by the shell, which refuses read-only files and then awaits its own error toast (about 20 s)
            // before returning; files are applied one by one, so each read-only file would hold up the rest. Skip it here, with a reason.
            if (stat.IsReadOnly) return new Content(null, null, null, "the file is read-only");
            var bytes = await _workspace.FileSystem.ReadFileAsync(uri, ct).ConfigureAwait(true);
            if (!TryDecode(bytes, out var decoded)) return new Content(null, null, null, "the file is not valid UTF-8 or UTF-16 (replace is only supported for those)");
            return new Content(decoded, DetectLineEnding(decoded), null, null);
        }
        catch (NotSupportedException ex)
        {
            // Not a file the file system can read: a result that was searched from memory (an untitled buffer) whose editor has been closed.
            return new Content(null, null, null, uri.IsFile ? ex.Message : "the document is no longer open");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Content(null, null, null, ex.Message);
        }
    }

    /// <summary>BOM-aware decode that refuses invalid UTF-8 (same policy as the shell's edit of closed files, so the two always agree on positions).</summary>
    internal static bool TryDecode(byte[] bytes, out string text)
    {
        text = "";
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static string? DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lf++;
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
        }
        if (crlf + lf + cr == 0) return null;
        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }

    // ------------------------------------------------------------------------------------------------ matching

    /// <summary>Every (non-empty) match of the file with its replacement, in document order. Line mode and whole-text mode mirror the search exactly.</summary>
    internal static List<ReplaceItem> FindReplacements(Uri uri, string text, string? lineEnding, TextMatcher matcher, ReplaceOptions options)
    {
        var items = new List<ReplaceItem>();
        var template = matcher.Regex is not null ? ExpandEscapes(options.ReplaceText) : null;

        string Replacement(string matched, Match? m)
        {
            var result = m is not null ? m.Result(template!) : options.ReplaceText;
            if (options.PreserveCase) result = PreserveCase(matched, result);
            if (lineEnding is { } eol && eol != "\n" && result.Contains('\n')) result = result.Replace("\r\n", "\n").Replace("\n", eol);
            return result;
        }

        if (matcher.RequiresWholeText)
        {
            var normal = TextMatcher.NormaliseBreaks(text);
            var line = 0;
            var lineStart = 0;
            var scanned = 0;
            var from = 0;
            while (true)
            {
                int idx, len;
                Match? m = null;
                if (matcher.Regex is { } rx)
                {
                    m = rx.Match(normal, from);
                    while (m.Success && m.Length == 0) m = m.NextMatch();
                    if (!m.Success) break;
                    idx = m.Index;
                    len = m.Length;
                }
                else
                {
                    idx = matcher.Find(normal, from, out len);
                    if (idx < 0) break;
                }
                while (scanned < idx)
                {
                    var nl = normal.IndexOf('\n', scanned, idx - scanned);
                    if (nl < 0) { scanned = idx; break; }
                    scanned = nl + 1;
                    line++;
                    lineStart = scanned;
                }
                var startCol = idx - lineStart;
                var matched = normal.Substring(idx, len);
                var endLine = line;
                var endCol = startCol + len;
                var lastBreak = matched.LastIndexOf('\n');
                if (lastBreak >= 0)
                {
                    endLine += matched.Count(c => c == '\n');
                    endCol = len - lastBreak - 1;
                }
                items.Add(new ReplaceItem(uri, new TextRange(new TextPosition(line, startCol), new TextPosition(endLine, endCol)), matched, Replacement(matched, m)));
                from = idx + len;
            }
            return items;
        }

        var lineNo = 0;
        var pos = 0;
        while (pos <= text.Length)
        {
            var end = text.AsSpan(pos).IndexOfAny('\n', '\r');
            var lineEnd = end < 0 ? text.Length : pos + end;
            var lineText = text.Substring(pos, lineEnd - pos);
            var from = 0;
            while (true)
            {
                int idx, len;
                Match? m = null;
                if (matcher.Regex is { } rx)
                {
                    m = rx.Match(lineText, from);
                    while (m.Success && m.Length == 0) m = m.NextMatch();
                    if (!m.Success) break;
                    idx = m.Index;
                    len = m.Length;
                }
                else
                {
                    idx = matcher.Find(lineText, from, out len);
                    if (idx < 0) break;
                }
                var matched = lineText.Substring(idx, len);
                items.Add(new ReplaceItem(uri, new TextRange(new TextPosition(lineNo, idx), new TextPosition(lineNo, idx + len)), matched, Replacement(matched, m)));
                from = idx + len;
            }
            if (end < 0) break;
            pos = lineEnd + 1;
            if (text[lineEnd] == '\r' && pos < text.Length && text[pos] == '\n') pos++;
            lineNo++;
        }
        return items;
    }

    // ------------------------------------------------------------------------------------------------ strings

    /// <summary>The replacement as it will be written for one matched text (used for the live preview in the results tree).</summary>
    public static string PreviewReplacement(SearchQuery query, string matchedText, ReplaceOptions options) =>
        CreatePreviewer(query, options)(matchedText);

    /// <summary>Compiles the regex half of a replace preview once per searched query (see <see cref="ReplacePreviewPattern"/>).</summary>
    public static ReplacePreviewPattern CreatePreviewPattern(SearchQuery query)
    {
        Regex? rx = null;
        if (query.IsRegex)
        {
            try
            {
                var matcher = RegexMatcher.Create(query.Pattern, query.MatchCase, query.WholeWord, false, TimeSpan.FromMilliseconds(250));
                if (matcher.Kind == "regex-nonbacktracking") rx = matcher.Regex;
            }
            catch (Exception ex) when (ex is SearchPatternException or ArgumentException) { rx = null; }
        }
        return new ReplacePreviewPattern(query, rx);
    }

    /// <summary>
    /// A reusable "matched text to replacement" function for the results tree (see <see cref="ReplacePreviewPattern"/>). It sees only the clipped
    /// matched text, so for patterns that depend on context (anchors) the preview is an approximation; the real replacement is always
    /// computed from the file when applying.
    /// </summary>
    public static Func<string, string> CreatePreviewer(SearchQuery query, ReplaceOptions options) => CreatePreviewer(CreatePreviewPattern(query), options);

    public static Func<string, string> CreatePreviewer(ReplacePreviewPattern pattern, ReplaceOptions options)
    {
        var expanded = pattern.Query.IsRegex ? ExpandEscapes(options.ReplaceText) : options.ReplaceText;
        var rx = pattern.Regex;
        return matched =>
        {
            var result = expanded;
            if (rx is not null)
            {
                try { if (rx.Match(matched) is { Success: true } m) result = m.Result(expanded); }
                catch (RegexMatchTimeoutException) { /* keep the unexpanded text */ }
            }
            return options.PreserveCase ? PreserveCase(matched, result) : result;
        };
    }

    /// <summary>Turns the replace-box escapes <c>\n</c>, <c>\t</c> and <c>\\</c> into characters; the backslash of anything else is kept.</summary>
    internal static string ExpandEscapes(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case 'n': sb.Append('\n'); i++; continue;
                    case 't': sb.Append('\t'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>VS Code's "preserve case": follow the capitalisation of the matched text.</summary>
    public static string PreserveCase(string matched, string replacement)
    {
        if (matched.Length == 0 || replacement.Length == 0) return replacement;
        if (matched.ToUpperInvariant() == matched && matched.ToLowerInvariant() != matched) return replacement.ToUpperInvariant();
        if (matched.ToLowerInvariant() == matched && matched.ToUpperInvariant() != matched) return replacement.ToLowerInvariant();
        if (char.IsUpper(matched[0])) return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        if (char.IsLower(matched[0])) return char.ToLowerInvariant(replacement[0]) + replacement[1..];
        return replacement;
    }

    public void Dispose() => _scheduler.Dispose();
}
