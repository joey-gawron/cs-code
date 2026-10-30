using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Dap.Tasks;

/// <summary>Where a matched file name is looked for (tasks.json <c>fileLocation</c>).</summary>
internal enum FileLocationKind { Absolute, Relative, AutoDetect }

/// <summary>One line pattern of a problem matcher: a regular expression and the groups that hold each part (VS Code's names).</summary>
internal sealed record ProblemPattern(Regex Regex, int? File, int? Location, int? Line, int? Column, int? EndLine, int? EndColumn, int? Severity,
    int? Code, int? Message, bool Loop);

/// <summary>
/// A problem matcher (VS Code's <c>problemMatcher</c>): its owner (the Error List groups and clears problems by it), a default severity,
/// where files are and one or several line patterns (a multi-line matcher's last pattern may <c>loop</c>).
/// </summary>
internal sealed record ProblemMatcher(string Owner, string? Source, DiagnosticSeverity Severity, FileLocationKind FileLocation, string? FileBase,
    IReadOnlyList<ProblemPattern> Patterns);

/// <summary>
/// tasks.json problem matchers: the built-in ones CsCode's languages need (<c>$msCompile</c>, <c>$tsc</c>/<c>$tsc-watch</c>, <c>$gcc</c>,
/// <c>$rustc</c>, <c>$eslint-compact</c>, <c>$eslint-stylish</c>; regular expressions written for CsCode after the compilers' output) and the
/// object form (<c>owner</c>, <c>source</c>, <c>severity</c>, <c>fileLocation</c>, <c>pattern</c> as an object, an array or a "$name",
/// <c>base</c> to extend a built-in). Unknown names are ignored. Patterns run with a time-out, so a pathological expression cannot hang
/// the task runner.
/// </summary>
internal static class ProblemMatchers
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static ProblemPattern P(string regex, int? file = null, int? location = null, int? line = null, int? column = null, int? severity = null,
        int? code = null, int? message = null, int? endLine = null, int? endColumn = null, bool loop = false) =>
        new(new Regex(regex, RegexOptions.CultureInvariant, RegexTimeout), file, location, line, column, endLine, endColumn, severity, code, message, loop);

    // file(line[,col[,endLine,endCol]]): [fatal ]error|warning|info CODE: message (MSBuild, csc, the .NET SDK)
    private static readonly ProblemPattern MsCompile = P(
        @"^(?:\s*\d+>)?(\S.*?)(?:\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\))?\s*:\s+(?:(\S+)\s+)?((?:fatal +)?error|warning|info)\s+(\w+\d+)?\s*:\s*(.*?)(?:\s+\[[^\]]*\])?$",
        file: 1, location: 2, severity: 4, code: 5, message: 6);

    // file(line,col): error TS1234: message, or file:line:col - error TS1234: message (tsc --pretty)
    private static readonly ProblemPattern Tsc = P(@"^([^\s].*)[\(:](\d+)[,:](\d+)(?:\):\s+|\s+-\s+)(error|warning|info)\s+TS(\d+)\s*:\s*(.*)$",
        file: 1, line: 2, column: 3, severity: 4, code: 5, message: 6);

    // file:line:col: [fatal ]error|warning|note: message (gcc, clang)
    private static readonly ProblemPattern Gcc = P(@"^(.*?):(\d+):(\d*):?\s+(?:fatal\s+)?(warning|error|note):\s+(.*)$",
        file: 1, line: 2, column: 3, severity: 4, message: 5);

    // error[E0425]: message, then "  --> src/main.rs:3:5" (rustc, cargo)
    private static readonly ProblemPattern[] Rustc =
    [
        P(@"^(warning|warn|error)(?:\[(.*?)\])?: (.*)$", severity: 1, code: 2, message: 3),
        P(@"^[\s\->=]*(.*?):(\d*):(\d*)\s*$", file: 1, line: 2, column: 3),
    ];

    // file: line 3, col 5, Error - message (rule)
    private static readonly ProblemPattern EslintCompact = P(@"^(.+):\sline\s(\d+),\scol\s(\d+),\s(Error|Warning|Info)\s-\s(.+)\s\((.+)\)$",
        file: 1, line: 2, column: 3, severity: 4, message: 5, code: 6);

    // a file name line, then "  3:5  error  message  rule" lines
    private static readonly ProblemPattern[] EslintStylish =
    [
        P(@"^((?:[a-zA-Z]:)*[./\\]+.*?)$", file: 1),
        P(@"^\s+(\d+):(\d+)\s+(error|warning|info)\s+(.+?)(?:\s\s+(.*))?$", line: 1, column: 2, severity: 3, message: 4, code: 5, loop: true),
    ];

    private static ProblemMatcher? Builtin(string name) => name.TrimStart('$') switch
    {
        "msCompile" => new("msCompile", "msCompile", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [MsCompile]),
        "tsc" or "tsc-watch" => new("typescript", "ts", DiagnosticSeverity.Error, FileLocationKind.Relative, "${cwd}", [Tsc]),
        "gcc" => new("cpp", "gcc", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [Gcc]),
        "rustc" => new("rustc", "rustc", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", Rustc),
        "eslint-compact" => new("eslint", "eslint", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [EslintCompact]),
        "eslint-stylish" => new("eslint", "eslint", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", EslintStylish),
        _ => null,
    };

    private static IReadOnlyList<ProblemPattern>? BuiltinPatterns(string name) => name.TrimStart('$') switch
    {
        "msCompile" => [MsCompile],
        "tsc" or "tsc-watch" => [Tsc],
        "gcc" => [Gcc],
        "rustc" => Rustc,
        "eslint-compact" => [EslintCompact],
        "eslint-stylish" => EslintStylish,
        _ => null,
    };

    /// <summary>The matchers a task's <c>problemMatcher</c> value names or defines (string, object or an array of both).</summary>
    public static IReadOnlyList<ProblemMatcher> Parse(JsonNode? node)
    {
        var result = new List<ProblemMatcher>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) result.AddRange(Parse(item));
                break;
            case JsonValue value when value.TryGetValue<string>(out var name):
                if (Builtin(name) is { } builtin) result.Add(builtin);
                break;
            case JsonObject obj when FromObject(obj) is { } custom:
                result.Add(custom);
                break;
        }
        return result;
    }

    private static ProblemMatcher? FromObject(JsonObject obj)
    {
        var baseMatcher = obj["base"] is JsonValue b && b.TryGetValue<string>(out var baseName) ? Builtin(baseName) : null;
        IReadOnlyList<ProblemPattern>? patterns = obj["pattern"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var named) => BuiltinPatterns(named),
            JsonObject single => Pattern(single) is { } p ? [p] : null,
            JsonArray many => many.OfType<JsonObject>().Select(Pattern).ToList() is var list && list.Count > 0 && list.All(p => p is not null)
                ? list.OfType<ProblemPattern>().ToList() : null,
            _ => null,
        };
        patterns ??= baseMatcher?.Patterns;
        if (patterns is null || patterns.Count == 0) return null;
        var (location, fileBase) = obj["fileLocation"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var kind) => (Location(kind), "${workspaceFolder}"),
            JsonArray a when a.Count > 0 && a[0] is JsonValue k && k.TryGetValue<string>(out var kind) =>
                (Location(kind), a.Count > 1 && a[1] is JsonValue bv && bv.TryGetValue<string>(out var basePath) ? basePath : "${workspaceFolder}"),
            _ => (baseMatcher?.FileLocation ?? FileLocationKind.Relative, baseMatcher?.FileBase ?? "${workspaceFolder}"),
        };
        var owner = obj["owner"] is JsonValue o && o.TryGetValue<string>(out var ownerName) ? ownerName : baseMatcher?.Owner ?? "external";
        var source = obj["source"] is JsonValue s && s.TryGetValue<string>(out var sourceName) ? sourceName : baseMatcher?.Source;
        var severity = obj["severity"] is JsonValue sv && sv.TryGetValue<string>(out var sevName) && SeverityOf(sevName) is { } sevValue
            ? sevValue : baseMatcher?.Severity ?? DiagnosticSeverity.Error;
        return new ProblemMatcher(owner, source, severity, location, fileBase, patterns);
    }

    private static FileLocationKind Location(string kind) => kind switch
    {
        "absolute" => FileLocationKind.Absolute,
        "relative" => FileLocationKind.Relative,
        _ => FileLocationKind.AutoDetect, // "autoDetect", "search"
    };

    private static ProblemPattern? Pattern(JsonObject o)
    {
        if (o["regexp"] is not JsonValue r || !r.TryGetValue<string>(out var regex)) return null;
        int? G(string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        try
        {
            return new ProblemPattern(new Regex(regex, RegexOptions.CultureInvariant, RegexTimeout), G("file"), G("location"), G("line"), G("column"),
                G("endLine"), G("endColumn"), G("severity"), G("code"), G("message"), o["loop"] is JsonValue l && l.TryGetValue<bool>(out var loop) && loop);
        }
        catch (ArgumentException) { return null; } // not a valid .NET expression
    }

    public static DiagnosticSeverity? SeverityOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var t = text.ToLowerInvariant();
        if (t.Contains("error", StringComparison.Ordinal)) return DiagnosticSeverity.Error;
        if (t.StartsWith("warn", StringComparison.Ordinal)) return DiagnosticSeverity.Warning;
        if (t is "info" or "information" or "note") return DiagnosticSeverity.Information;
        if (t == "hint") return DiagnosticSeverity.Hint;
        return null;
    }
}

/// <summary>
/// Runs a task's problem matchers over its output and publishes what they find to <see cref="IDiagnosticService"/> (the Error List), owner
/// <c>task.&lt;matcher owner&gt;</c>: each matcher's earlier problems are cleared when the task starts (VS Code clears a matcher's markers per
/// run), lines are split from the raw output with escape sequences removed and carriage-return overwrites resolved, and at most
/// <see cref="MaxProblems"/> problems are kept. Single-threaded: the task runner feeds it on its lane.
/// </summary>
internal sealed class ProblemCollector
{
    public const int MaxProblems = 2000;
    private const int MaxLineLength = 16 * 1024;

    private readonly IReadOnlyList<ProblemMatcher> _matchers;
    private readonly IDiagnosticService _diagnostics;
    private readonly string? _workspaceFolder;
    private readonly string? _cwd;
    private readonly StringBuilder _partial = new();
    private readonly Dictionary<(string Owner, Uri File), List<Diagnostic>> _found = new();
    private readonly (int Index, Dictionary<string, string> Data)?[] _state;
    private readonly HashSet<(string Owner, Uri File)> _dirty = new();
    private int _count;

    public ProblemCollector(IReadOnlyList<ProblemMatcher> matchers, IDiagnosticService diagnostics, string? workspaceFolder, string? cwd)
    {
        _matchers = matchers;
        _diagnostics = diagnostics;
        _workspaceFolder = workspaceFolder;
        _cwd = cwd;
        _state = new (int, Dictionary<string, string>)?[matchers.Count];
    }

    public int Errors { get; private set; }
    public int Warnings { get; private set; }
    public int Count => _count;

    /// <summary>The task starts: the matchers' earlier problems go (they belong to the previous run).</summary>
    public void Start()
    {
        foreach (var owner in _matchers.Select(m => Owner(m)).Distinct()) _diagnostics.Clear(owner);
    }

    private static string Owner(ProblemMatcher m) => "task." + m.Owner;

    /// <summary>Output text as it arrives (any chunking): complete lines are matched, problems published per chunk.</summary>
    public void Append(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            _partial.Append(text, start, Math.Min(i - start, Math.Max(0, MaxLineLength - _partial.Length)));
            Line(_partial.ToString());
            _partial.Clear();
            start = i + 1;
        }
        if (start < text.Length && _partial.Length < MaxLineLength) _partial.Append(text, start, Math.Min(text.Length - start, MaxLineLength - _partial.Length));
        Publish();
    }

    /// <summary>The output ended: the last line (without a newline) is matched too.</summary>
    public void Complete()
    {
        if (_partial.Length > 0)
        {
            Line(_partial.ToString());
            _partial.Clear();
        }
        Publish();
    }

    private void Line(string raw)
    {
        var line = Clean(raw);
        for (var m = 0; m < _matchers.Count; m++) Match(m, line);
    }

    /// <summary>The text a terminal would show: escape sequences removed, the last carriage-return overwrite kept.</summary>
    internal static string Clean(string raw)
    {
        var text = raw.EndsWith('\r') ? raw[..^1] : raw;
        var cr = text.LastIndexOf('\r');
        if (cr >= 0) text = text[(cr + 1)..];
        if (text.IndexOf('\e') < 0) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\e') { sb.Append(c); continue; }
            if (i + 1 >= text.Length) break;
            var next = text[i + 1];
            if (next == '[')
            {
                // CSI: parameters and intermediates, then a final byte 0x40-0x7E
                i += 2;
                while (i < text.Length && !(text[i] >= '@' && text[i] <= '~')) i++;
            }
            else if (next is ']' or 'P' or '_' or '^')
            {
                // OSC/DCS/APC/PM: up to BEL or ST
                i += 2;
                while (i < text.Length && text[i] != '\a' && !(text[i] == '\e' && i + 1 < text.Length && text[i + 1] == '\\')) i++;
                if (i < text.Length && text[i] == '\e') i++;
            }
            else i++; // a two-character escape
        }
        return sb.ToString();
    }

    private void Match(int index, string line)
    {
        var matcher = _matchers[index];
        var patterns = matcher.Patterns;
        try
        {
            // a multi-line matcher in progress: the next pattern, or (after the last) the looping one again
            if (_state[index] is { } state)
            {
                var next = state.Index;
                var pattern = patterns[next];
                var match = pattern.Regex.Match(line);
                if (match.Success)
                {
                    var data = pattern.Loop ? new Dictionary<string, string>(state.Data) : state.Data;
                    Take(pattern, match, data);
                    if (next == patterns.Count - 1)
                    {
                        Emit(matcher, data);
                        _state[index] = pattern.Loop ? (next, state.Data) : null;
                    }
                    else _state[index] = (next + 1, data);
                    return;
                }
                _state[index] = null; // the sequence broke: start over with this line
            }
            var first = patterns[0].Regex.Match(line);
            if (!first.Success) return;
            var started = new Dictionary<string, string>();
            Take(patterns[0], first, started);
            if (patterns.Count == 1) Emit(matcher, started);
            else _state[index] = (1, started);
        }
        catch (RegexMatchTimeoutException)
        {
            _state[index] = null; // a pathological expression on this line: skip it
        }
    }

    private static void Take(ProblemPattern p, Match m, Dictionary<string, string> data)
    {
        void Set(string key, int? group)
        {
            if (group is { } g && g < m.Groups.Count && m.Groups[g].Success && m.Groups[g].Value.Length > 0) data[key] = m.Groups[g].Value;
        }
        Set("file", p.File);
        Set("location", p.Location);
        Set("line", p.Line);
        Set("column", p.Column);
        Set("endLine", p.EndLine);
        Set("endColumn", p.EndColumn);
        Set("severity", p.Severity);
        Set("code", p.Code);
        Set("message", p.Message);
    }

    private void Emit(ProblemMatcher matcher, Dictionary<string, string> data)
    {
        if (_count >= MaxProblems || !data.TryGetValue("file", out var file) || !data.TryGetValue("message", out var message)) return;
        if (Resolve(matcher, file.Trim()) is not { } uri) return;
        int Int(string key, int fallback) => data.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n > 0 ? n : fallback;
        int line = Int("line", 1), column = Int("column", 1), endLine = Int("endLine", line), endColumn = Int("endColumn", column);
        if (data.TryGetValue("location", out var location))
        {
            var parts = location.Split(',').Select(s => int.TryParse(s, out var n) ? n : 1).ToArray();
            (line, column, endLine, endColumn) = parts.Length switch
            {
                1 => (parts[0], 1, parts[0], 1),
                2 => (parts[0], parts[1], parts[0], parts[1]),
                _ => (parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : parts[1]),
            };
        }
        var severity = ProblemMatchers.SeverityOf(data.GetValueOrDefault("severity")) ?? matcher.Severity;
        var diagnostic = new Diagnostic(TextRange.FromLines(Math.Max(0, line - 1), Math.Max(0, column - 1), Math.Max(0, endLine - 1), Math.Max(0, endColumn - 1)),
            message.Trim(), severity, data.GetValueOrDefault("code"), matcher.Source ?? matcher.Owner);
        var key = (Owner(matcher), uri);
        if (!_found.TryGetValue(key, out var list)) _found[key] = list = new List<Diagnostic>();
        list.Add(diagnostic);
        _dirty.Add(key);
        _count++;
        if (severity == DiagnosticSeverity.Error) Errors++;
        else if (severity == DiagnosticSeverity.Warning) Warnings++;
    }

    private Uri? Resolve(ProblemMatcher matcher, string file)
    {
        try
        {
            var baseDir = (matcher.FileBase ?? "${workspaceFolder}")
                .Replace("${workspaceFolder}", _workspaceFolder ?? _cwd ?? "", StringComparison.Ordinal)
                .Replace("${workspaceRoot}", _workspaceFolder ?? _cwd ?? "", StringComparison.Ordinal)
                .Replace("${cwd}", _cwd ?? _workspaceFolder ?? "", StringComparison.Ordinal);
            if (baseDir.Contains("${", StringComparison.Ordinal) || baseDir.Length == 0) baseDir = _cwd ?? _workspaceFolder ?? Environment.CurrentDirectory;
            string path = matcher.FileLocation switch
            {
                FileLocationKind.Absolute => file,
                FileLocationKind.Relative => Path.Combine(baseDir, file),
                _ => Path.IsPathRooted(file) ? file : Path.Combine(baseDir, file),
            };
            return Path.IsPathRooted(path) ? new Uri(Path.GetFullPath(path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void Publish()
    {
        foreach (var key in _dirty) _diagnostics.Set(key.File, key.Owner, _found[key].ToArray());
        _dirty.Clear();
    }
}
