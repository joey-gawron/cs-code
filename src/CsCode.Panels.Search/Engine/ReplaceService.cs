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

/// <summary>The edits for one file. <see cref="Version"/> is set when the file is an open document (the edit is only valid for that version).</summary>
public sealed record FileReplacePlan(Uri Uri, IReadOnlyList<TextEdit> Edits, IReadOnlyList<ReplaceItem> Items, int? Version);

public sealed record ReplaceSkip(Uri Uri, string Reason);

public sealed record ReplacePlan(IReadOnlyList<FileReplacePlan> Files, IReadOnlyList<ReplaceSkip> Skipped)
{
    public int FileCount => Files.Count;
    public int ReplacementCount => Files.Sum(f => f.Edits.Count);
    public WorkspaceEdit ToWorkspaceEdit() => new(Files.ToDictionary(f => f.Uri, f => f.Edits));
}

public sealed record ReplaceResult(int FilesChanged, int Replacements, IReadOnlyList<string> Problems, bool Cancelled);

/// <summary>
/// Replace in files. The search results only remember positions, so the plan is made by searching each file AGAIN right before the
/// edit (open documents from memory, closed files from disk) and replacing exactly the matches that are still there. A match that moved
/// or vanished since the search is reported as skipped, never replaced blindly. The work runs on a dedicated thread; the caller only
/// awaits. Files are applied one at a time through <see cref="INavigationService.ApplyWorkspaceEditAsync"/>, so an open document gets
/// ONE undo step and closed files are edited on disk by the shell (which never sees a multi-file edit it would prompt for again).
/// </summary>
public sealed class ReplaceService : IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly IDocumentManager? _documents;
    private readonly SearchLimits _limits;
    private readonly DedicatedThreadScheduler _scheduler = new("cscode-search-replace");

    public ReplaceService(IWorkspaceService workspace, IDocumentManager? documents = null, SearchLimits? limits = null)
    {
        _workspace = workspace;
        _documents = documents;
        _limits = limits ?? new SearchLimits();
    }

    /// <summary>Plans the replacement on the replace thread; the synchronous part on the caller is one enqueue.</summary>
    public Task<ReplacePlan> PlanAsync(SearchQuery query, IReadOnlyList<ReplaceTarget> targets, ReplaceOptions options, CancellationToken ct = default) =>
        Task.Factory.StartNew(() => PlanCoreAsync(query, targets, options, ct), ct, TaskCreationOptions.DenyChildAttach, _scheduler).Unwrap();

    private async Task<ReplacePlan> PlanCoreAsync(SearchQuery query, IReadOnlyList<ReplaceTarget> targets, ReplaceOptions options, CancellationToken ct)
    {
        var matcher = TextMatcher.Create(query, _limits);
        var files = new List<FileReplacePlan>();
        var skipped = new List<ReplaceSkip>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var content = await ReadContentAsync(target.Uri, ct).ConfigureAwait(true);
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
            files.Add(new FileReplacePlan(target.Uri, items.Select(i => new TextEdit(i.Range, i.NewText)).ToArray(), items, content.Version));
        }
        return new ReplacePlan(files, skipped);
    }

    /// <summary>
    /// Applies a plan file by file. Returns what happened; never throws for a single bad file. An open document that changed since the
    /// plan was made is skipped (its version no longer matches).
    /// </summary>
    public async Task<ReplaceResult> ApplyAsync(ReplacePlan plan, INavigationService navigation, string? label = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var problems = plan.Skipped.Select(s => $"{DisplayName(s.Uri)}: {s.Reason}").ToList();
        int files = 0, replaced = 0, done = 0;
        foreach (var file in plan.Files)
        {
            if (ct.IsCancellationRequested) return new ReplaceResult(files, replaced, problems, true);
            progress?.Report(++done);
            if (file.Version is { } v && _documents?.Get(file.Uri) is { } doc && doc.Version != v)
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
                }
                else problems.Add($"{DisplayName(file.Uri)}: the edit could not be applied");
            }
            catch (OperationCanceledException) { return new ReplaceResult(files, replaced, problems, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add($"{DisplayName(file.Uri)}: {ex.Message}");
            }
        }
        return new ReplaceResult(files, replaced, problems, false);
    }

    private static string DisplayName(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

    // ------------------------------------------------------------------------------------------------ content

    private readonly record struct Content(string? Text, string? LineEnding, int? Version, string? Problem);

    private async ValueTask<Content> ReadContentAsync(Uri uri, CancellationToken ct)
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
            if (stat.Size > _limits.MaxFileSizeBytes) return new Content(null, null, null, "the file is too large");
            var bytes = await _workspace.FileSystem.ReadFileAsync(uri, ct).ConfigureAwait(true);
            if (!TryDecode(bytes, out var decoded)) return new Content(null, null, null, "the file is not valid UTF-8 or UTF-16 (replace is only supported for those)");
            return new Content(decoded, DetectLineEnding(decoded), null, null);
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
    public static string PreviewReplacement(SearchQuery query, string matchedText, ReplaceOptions options)
    {
        string result = options.ReplaceText;
        if (query.IsRegex)
        {
            try
            {
                var rx = RegexMatcher.Create(query.Pattern, query.MatchCase, query.WholeWord, false, TimeSpan.FromMilliseconds(250)).Regex!;
                if (rx.Match(matchedText) is { Success: true } m) result = m.Result(ExpandEscapes(options.ReplaceText));
                else result = ExpandEscapes(options.ReplaceText);
            }
            catch (Exception ex) when (ex is SearchPatternException or RegexMatchTimeoutException or ArgumentException)
            {
                result = ExpandEscapes(options.ReplaceText);
            }
        }
        return options.PreserveCase ? PreserveCase(matchedText, result) : result;
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
