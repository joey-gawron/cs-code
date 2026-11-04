using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

/// <summary>
/// A source block (<c>#+BEGIN_SRC lang :header args</c> ... <c>#+END_SRC</c>) and its results block (<c>#+RESULTS:</c> right after it, a blank
/// line between allowed): lines, language, code (its common indentation removed) and header arguments (<c>:results</c>, <c>:dir</c>,
/// <c>:var</c>; a <c>#+HEADER:</c> line above adds more).
/// </summary>
public sealed record OrgSrcBlock(int Begin, int End, string Language, string Code, IReadOnlyList<KeyValuePair<string, string>> Headers, int? ResultsLine, int ResultsEnd)
{
    public string? Header(string name)
    {
        string? value = null;
        foreach (var (k, v) in Headers) if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) value = v; // the last one wins
        return value;
    }

    /// <summary>The <c>:var name=value</c> arguments, in order.</summary>
    public IEnumerable<(string Name, string Value)> Variables() =>
        Headers.Where(h => string.Equals(h.Key, ":var", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(v => v.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .Select(p => (p[0].Trim(), p[1].Trim()));

    /// <summary><c>:results silent</c>: the output is shown but not written into the file.</summary>
    public bool Silent => Header(":results")?.Split(' ').Contains("silent") == true;
}

/// <summary>
/// Org-babel's text side: the source block at a line, all of a document's blocks, and the edit that writes a block's output as its results
/// (<c>: line</c> per line, or an example block past 10 lines, as org writes them; replacing the results already there).
/// </summary>
public static class OrgBabel
{
    private static readonly Regex BeginRe = new(@"^[ \t]*#\+begin_src(?:[ \t]+(\S+))?(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndRe = new(@"^[ \t]*#\+end_src\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeaderLineRe = new(@"^[ \t]*#\+header:(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameLineRe = new(@"^[ \t]*#\+name:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResultsRe = new(@"^[ \t]*#\+results(?:\[[^\]]*\])?:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExampleStartRe = new(@"^[ \t]*#\+begin_example", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExampleEndRe = new(@"^[ \t]*#\+end_example", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DrawerStartRe = new(@"^[ \t]*:results:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DrawerEndRe = new(@"^[ \t]*:end:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Org writes output of more lines than this as an example block (<c>org-babel-min-lines-for-block-output</c>).</summary>
    public const int MinLinesForBlock = 10;

    /// <summary>The block at <paramref name="line"/>: on its lines, its <c>#+NAME:</c> / <c>#+HEADER:</c> lines, or its results.</summary>
    public static OrgSrcBlock? At(OrgDocument doc, int line) =>
        All(doc).FirstOrDefault(b => (line >= b.Begin && line <= b.End) || (b.ResultsLine is { } r && line >= r && line < b.ResultsEnd) || IsAbove(doc, b, line));

    private static bool IsAbove(OrgDocument doc, OrgSrcBlock b, int line)
    {
        for (var i = b.Begin - 1; i >= 0 && (HeaderLineRe.IsMatch(doc.Lines[i]) || NameLineRe.IsMatch(doc.Lines[i])); i--)
            if (i == line) return true;
        return false;
    }

    public static IReadOnlyList<OrgSrcBlock> All(OrgDocument doc)
    {
        var blocks = new List<OrgSrcBlock>();
        for (var i = 0; i < doc.ContentLineCount; i++)
        {
            if (BeginRe.Match(doc.Lines[i]) is not { Success: true } m) continue;
            var end = i + 1;
            while (end < doc.ContentLineCount && !EndRe.IsMatch(doc.Lines[end])) end++;
            if (end >= doc.ContentLineCount) break; // not closed
            var headers = new List<KeyValuePair<string, string>>();
            for (var h = i - 1; h >= 0 && (HeaderLineRe.IsMatch(doc.Lines[h]) || NameLineRe.IsMatch(doc.Lines[h])); h--)
                if (HeaderLineRe.Match(doc.Lines[h]) is { Success: true } hm) headers.InsertRange(0, ParseHeaders(hm.Groups[1].Value));
            headers.AddRange(ParseHeaders(m.Groups[2].Value));
            var code = Dedent(doc.Lines.Skip(i + 1).Take(end - i - 1).ToList());
            var (results, resultsEnd) = FindResults(doc, end);
            blocks.Add(new OrgSrcBlock(i, end, m.Groups[1].Success ? m.Groups[1].Value : "", code, headers, results, resultsEnd));
            i = end;
        }
        return blocks;
    }

    /// <summary><c>:results output :dir ~/x :var a=1</c> as (name, value) pairs.</summary>
    public static List<KeyValuePair<string, string>> ParseHeaders(string text)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (Match m in Regex.Matches(text, @"(:[\w\-]+)((?:[ \t]+(?!:[\w\-])[^ \t]+)*)", RegexOptions.CultureInvariant))
            list.Add(new(m.Groups[1].Value, m.Groups[2].Value.Trim()));
        return list;
    }

    private static string Dedent(List<string> lines)
    {
        var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    // the results after the block's #+END_SRC line: its #+RESULTS: line (one blank line before it allowed) and the line after them
    private static (int? Line, int End) FindResults(OrgDocument doc, int end)
    {
        var at = end + 1;
        if (at < doc.ContentLineCount && string.IsNullOrWhiteSpace(doc.Lines[at])) at++;
        if (at >= doc.ContentLineCount || !ResultsRe.IsMatch(doc.Lines[at])) return (null, end + 1);
        var i = at + 1;
        if (i < doc.ContentLineCount && ExampleStartRe.IsMatch(doc.Lines[i]))
        {
            while (i < doc.ContentLineCount && !ExampleEndRe.IsMatch(doc.Lines[i])) i++;
            return (at, Math.Min(i + 1, doc.ContentLineCount));
        }
        if (i < doc.ContentLineCount && DrawerStartRe.IsMatch(doc.Lines[i]))
        {
            while (i < doc.ContentLineCount && !DrawerEndRe.IsMatch(doc.Lines[i])) i++;
            return (at, Math.Min(i + 1, doc.ContentLineCount));
        }
        while (i < doc.ContentLineCount && (doc.Lines[i].TrimStart().StartsWith(':') || doc.Lines[i].TrimStart().StartsWith('|'))
               && !doc.Lines[i].TrimStart().StartsWith(":END:", StringComparison.OrdinalIgnoreCase)) i++;
        return (at, i);
    }

    /// <summary>The edit that writes <paramref name="output"/> as the block's results, replacing those it has.</summary>
    public static OrgEditResult Results(OrgDocument doc, OrgSrcBlock block, string output)
    {
        var indent = OrgEdits.LeadingBlanks(doc.Lines[block.Begin]);
        var outLines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var lines = new List<string> { indent + "#+RESULTS:" };
        if (output.Trim().Length == 0) { }
        else if (outLines.Length > MinLinesForBlock)
        {
            lines.Add(indent + "#+begin_example");
            lines.AddRange(outLines.Select(l => indent + (l.StartsWith('*') || l.StartsWith("#+", StringComparison.Ordinal) ? "," : "") + l));
            lines.Add(indent + "#+end_example");
        }
        else lines.AddRange(outLines.Select(l => indent + (l.Length == 0 ? ":" : ": " + l)));
        if (block.ResultsLine is { } at) return new OrgEditResult([OrgEdits.ReplaceLines(doc, at, block.ResultsEnd, lines)]);
        return new OrgEditResult([OrgEdits.ReplaceLines(doc, block.End + 1, block.End + 1, ["", .. lines])]);
    }

    /// <summary>What a block's language runs as (<c>org.babel.languages</c> overrides these): the program and the file extension of the code.</summary>
    public static IReadOnlyDictionary<string, (string Program, string Extension)> DefaultLanguages { get; } = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["sh"] = ("sh", ".sh"),
        ["shell"] = ("sh", ".sh"),
        ["bash"] = ("bash", ".sh"),
        ["zsh"] = ("zsh", ".sh"),
        ["fish"] = ("fish", ".fish"),
        ["python"] = ("python3", ".py"),
        ["py"] = ("python3", ".py"),
        ["js"] = ("node", ".js"),
        ["javascript"] = ("node", ".js"),
        ["node"] = ("node", ".js"),
        ["ts"] = ("npx tsx", ".ts"),
        ["typescript"] = ("npx tsx", ".ts"),
        ["ruby"] = ("ruby", ".rb"),
        ["perl"] = ("perl", ".pl"),
        ["lua"] = ("lua", ".lua"),
        ["php"] = ("php", ".php"),
        ["r"] = ("Rscript", ".R"),
        ["powershell"] = ("pwsh", ".ps1"),
        ["pwsh"] = ("pwsh", ".ps1"),
    };

    /// <summary>The code with its <c>:var</c> values set first, in the language's own syntax (numbers as numbers, anything else as a string).</summary>
    public static string WithVariables(OrgSrcBlock block)
    {
        var vars = block.Variables().ToList();
        if (vars.Count == 0) return block.Code;
        var sb = new StringBuilder();
        foreach (var (name, raw) in vars)
        {
            var value = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
            var number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && raw[0] != '"';
            var quoted = "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            sb.Append(block.Language.ToLowerInvariant() switch
            {
                "sh" or "shell" or "bash" or "zsh" => $"{name}='{value.Replace("'", "'\\''", StringComparison.Ordinal)}'",
                "fish" => $"set {name} '{value.Replace("'", "\\'", StringComparison.Ordinal)}'",
                "js" or "javascript" or "node" or "ts" or "typescript" => $"const {name} = {(number ? value : quoted)};",
                "perl" => $"my ${name} = {(number ? value : quoted)};",
                "php" => $"${name} = {(number ? value : quoted)};",
                "powershell" or "pwsh" => $"${name} = {(number ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'")}",
                "r" => $"{name} <- {(number ? value : quoted)}",
                _ => $"{name} = {(number ? value : quoted)}",
            }).Append('\n');
        }
        // PHP needs its opening tag first
        if (block.Language.Equals("php", StringComparison.OrdinalIgnoreCase)) return "<?php\n" + sb + block.Code.Replace("<?php", "", StringComparison.Ordinal);
        return sb + block.Code;
    }
}

/// <summary>What running a block gave: its standard output and error, its exit code (null when it was stopped).</summary>
public sealed record OrgBabelRun(string Output, string Error, int? ExitCode, bool TimedOut);

/// <summary>
/// Runs a source block as a program: the code (with its <c>:var</c> values) in a temporary file, run by the language's program
/// (<see cref="OrgBabel.DefaultLanguages"/>, or <c>org.babel.languages</c>) in the block's <c>:dir</c> (else the org file's folder), its
/// output read, stopped after the timeout or when cancelled (the process tree is killed). The temporary file is deleted afterwards.
/// </summary>
public static class OrgBabelRunner
{
    public static async Task<OrgBabelRun> RunAsync(string program, string extension, string code, string workingDirectory, TimeSpan timeout,
        Action<int>? started = null, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), "cscode-org-babel-" + Guid.NewGuid().ToString("N")[..12] + extension);
        await File.WriteAllTextAsync(file, code, new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            var parts = program.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var psi = new ProcessStartInfo(parts[0])
            {
                WorkingDirectory = workingDirectory, // the caller checked it exists
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var p in parts.Skip(1)) psi.ArgumentList.Add(p);
            psi.ArgumentList.Add(file);
            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new OrgBabelRun("", $"Could not start \"{parts[0]}\": {ex.Message}. Install it, or set the program for this language in org.babel.languages.", null, false);
            }
            started?.Invoke(process.Id);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            var timedOut = false;
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            return new OrgBabelRun(stdout, stderr, process.HasExited && !timedOut && !ct.IsCancellationRequested ? process.ExitCode : null, timedOut);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
