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
/// where files are and one or several line patterns (a multi-line matcher's last pattern may <c>loop</c>); for a background (watch) task its
/// <see cref="Background"/> patterns.
/// </summary>
internal sealed record ProblemMatcher(string Owner, string? Source, DiagnosticSeverity Severity, FileLocationKind FileLocation, string? FileBase,
    IReadOnlyList<ProblemPattern> Patterns, ProblemBackground? Background = null);

/// <summary>
/// VS Code's <c>background</c> (formerly <c>watching</c>) of a problem matcher: a line matching <see cref="Begins"/> starts a cycle of work (a
/// watch task's compilation: the task is busy), one matching <see cref="Ends"/> ends it (the task is idle, a preLaunchTask is ready);
/// <see cref="ActiveBegins"/>: the task is busy from its start, before any line.
/// </summary>
internal sealed record ProblemBackground(Regex? Begins, Regex? Ends, bool ActiveBegins);

/// <summary>
/// tasks.json problem matchers: the built-in ones CsCode's languages need (<c>$msCompile</c>, <c>$dotnet-watch</c>, <c>$tsc</c>/<c>$tsc-watch</c>, <c>$gcc</c>,
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

    // file(line[,col[,endLine,endCol]]): [fatal ]error|warning|info CODE: message (MSBuild, csc, the .NET SDK). Indented too: in a terminal the
    // SDK's terminal logger prints each diagnostic four spaces in, under its project ("    /src/Program.cs(5,17): error CS0103: ...").
    private static readonly ProblemPattern MsCompile = P(
        @"^\s*(?:\d+>)?(\S.*?)(?:\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\))?\s*:\s+(?:(\S+)\s+)?((?:fatal +)?error|warning|info)\s+(\w+\d+)?\s*:\s*(.*?)(?:\s+\[[^\]]*\])?$",
        file: 1, location: 2, severity: 4, code: 5, message: 6);

    // $msCompile, plus the form dotnet watch prints for a hot reload that does not compile: "dotnet watch ❌ /src/Program.cs(1,44): error CS1002: ; expected"
    private static readonly ProblemPattern DotnetWatch = P(
        @"^(?:dotnet watch \S+ )?\s*(?:\d+>)?(\S.*?)(?:\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\))?\s*:\s+(?:(\S+)\s+)?((?:fatal +)?error|warning|info)\s+(\w+\d+)?\s*:\s*(.*?)(?:\s+\[[^\]]*\])?$",
        file: 1, location: 2, severity: 4, code: 5, message: 6);

    // dotnet watch (SDK 10, observed 2026-11-01): busy from the start (the first build); a cycle begins when files change or a restart is needed
    // ("dotnet watch ⌚ Files updated: ./Program.cs", "dotnet watch 🔥 Restart is needed to apply the changes.") and ends when the program runs
    // again or cannot ("⌚ Waiting for changes", "🔥 C# and Razor changes applied in 260ms.", "🔥 Unable to apply changes due to compilation
    // errors.", "⌚ Fix the error to continue or press Ctrl+C to exit."). Matched on the words after the emoji, so a terminal without it still
    // matches.
    private static readonly ProblemBackground DotnetWatchBackground = new(
        new Regex(@"^dotnet watch \S+ (?:Files? (?:updated|changed)|Restart is needed|Restarting|Building)", RegexOptions.CultureInvariant, RegexTimeout),
        new Regex(@"^dotnet watch \S+ (?:Waiting for changes|Waiting for a file to change|Fix the error to continue|.*changes applied|Unable to apply changes|Started)",
            RegexOptions.CultureInvariant, RegexTimeout),
        ActiveBegins: true);

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

    // tsc --watch: "[12:00:00 PM] Starting compilation in watch mode..." / "File change detected. Starting incremental compilation..." begin a
    // compilation, "[12:00:01 PM] Found 0 errors. Watching for file changes." ends it (VS Code's $tsc-watch, busy from the start)
    private static readonly ProblemBackground TscWatch = new(
        new Regex(@"^\s*(?:message TS6032:|\[?\D*\d{1,2}[:.]\d{1,2}[:.]\d{1,2}\D*(?:\]| -)) (?:Starting compilation in watch mode|File change detected\. Starting incremental compilation)\.\.\.",
            RegexOptions.CultureInvariant, RegexTimeout),
        new Regex(@"^\s*(?:message TS6042:|\[?\D*\d{1,2}[:.]\d{1,2}[:.]\d{1,2}\D*(?:\]| -)) (?:Compilation complete\.|Found \d+ errors?\.) Watching for file changes\.",
            RegexOptions.CultureInvariant, RegexTimeout),
        ActiveBegins: true);

    private static ProblemMatcher? Builtin(string name) => name.TrimStart('$') switch
    {
        "msCompile" => new("msCompile", "msCompile", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [MsCompile]),
        "dotnet-watch" => new("msCompile", "dotnet watch", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [DotnetWatch], DotnetWatchBackground),
        "tsc" => new("typescript", "ts", DiagnosticSeverity.Error, FileLocationKind.Relative, "${cwd}", [Tsc]),
        "tsc-watch" => new("typescript", "ts", DiagnosticSeverity.Error, FileLocationKind.Relative, "${cwd}", [Tsc], TscWatch),
        "gcc" => new("cpp", "gcc", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [Gcc]),
        "rustc" => new("rustc", "rustc", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", Rustc),
        "eslint-compact" => new("eslint", "eslint", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", [EslintCompact]),
        "eslint-stylish" => new("eslint", "eslint", DiagnosticSeverity.Error, FileLocationKind.AutoDetect, "${workspaceFolder}", EslintStylish),
        _ => null,
    };

    private static IReadOnlyList<ProblemPattern>? BuiltinPatterns(string name) => name.TrimStart('$') switch
    {
        "msCompile" => [MsCompile],
        "dotnet-watch" => [DotnetWatch],
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
        var background = BackgroundOf(obj["background"] ?? obj["watching"]) ?? baseMatcher?.Background;
        return new ProblemMatcher(owner, source, severity, location, fileBase, patterns, background);
    }

    /// <summary>
    /// <c>background</c>: <c>activeBegins</c>, <c>beginsPattern</c> and <c>endsPattern</c> (each an expression, or an object with
    /// <c>regexp</c>). Null when it names none of them or an expression is not valid.
    /// </summary>
    private static ProblemBackground? BackgroundOf(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        static bool? Flag(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        var activeBegins = Flag(o["activeBegins"]) ?? Flag(o["activeBegin"]) ?? false;
        if (!TryRegex(o["beginsPattern"], out var begins) || !TryRegex(o["endsPattern"], out var ends)) return null;
        return begins is null && ends is null && !activeBegins ? null : new ProblemBackground(begins, ends, activeBegins);

        static bool TryRegex(JsonNode? n, out Regex? regex)
        {
            regex = null;
            var text = n switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject p when p["regexp"] is JsonValue r && r.TryGetValue<string>(out var s) => s,
                _ => null,
            };
            if (text is null) return n is null;
            try { regex = new Regex(text, RegexOptions.CultureInvariant, RegexTimeout); return true; }
            catch (ArgumentException) { return false; }
        }
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
    private readonly bool[] _active;
    private int _count;

    public ProblemCollector(IReadOnlyList<ProblemMatcher> matchers, IDiagnosticService diagnostics, string? workspaceFolder, string? cwd)
    {
        _matchers = matchers;
        _diagnostics = diagnostics;
        _workspaceFolder = workspaceFolder;
        _cwd = cwd;
        _state = new (int, Dictionary<string, string>)?[matchers.Count];
        _active = new bool[matchers.Count];
    }

    public int Errors { get; private set; }
    public int Warnings { get; private set; }
    public int Count => _count;

    /// <summary>A matcher has <c>background</c> patterns (VS Code's watching matcher): a background task's readiness can be followed.</summary>
    public bool IsWatching => _matchers.Any(m => m.Background is not null);

    /// <summary>A background matcher is between its begin and its end (the watch task is compiling).</summary>
    public bool IsBusy => _active.Any(a => a);

    /// <summary>A background cycle began (the task went from idle to busy). Runs on the caller's (the lane's) thread.</summary>
    public Action? BackgroundBegan { get; set; }

    /// <summary>Every background cycle ended (the task went idle: a preLaunchTask is ready). Runs on the caller's thread.</summary>
    public Action? BackgroundEnded { get; set; }

    /// <summary>The task starts: the matchers' earlier problems go (they belong to the previous run); <c>activeBegins</c> matchers are busy.</summary>
    public void Start()
    {
        foreach (var owner in _matchers.Select(m => Owner(m)).Distinct()) _diagnostics.Clear(owner);
        for (var i = 0; i < _matchers.Count; i++) _active[i] = _matchers[i].Background is { ActiveBegins: true };
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
        if (Background(line)) return; // (VS Code: a begin or end line is not matched against the problem patterns)
        for (var m = 0; m < _matchers.Count; m++) Match(m, line);
    }

    /// <summary>
    /// VS Code's background tracking: a begins line opens a cycle (that matcher's problems of the last cycle are replaced by this cycle's when it
    /// ends), an ends line closes it. Returns true when the line was one of them.
    /// </summary>
    private bool Background(string line)
    {
        var consumed = false;
        for (var m = 0; m < _matchers.Count; m++)
        {
            if (_matchers[m].Background is not { } background) continue;
            try
            {
                if (background.Begins?.IsMatch(line) == true)
                {
                    consumed = true;
                    if (_active[m]) continue;
                    var wasBusy = IsBusy;
                    _active[m] = true;
                    _state[m] = null;
                    ForgetCycle(Owner(_matchers[m]));
                    if (!wasBusy) BackgroundBegan?.Invoke();
                }
                else if (background.Ends?.IsMatch(line) == true)
                {
                    consumed = true;
                    if (!_active[m]) continue;
                    _active[m] = false;
                    _state[m] = null;
                    var owner = Owner(_matchers[m]);
                    // the cycle's problems replace the last cycle's: files fixed in this cycle lose theirs
                    Publish();
                    _diagnostics.Clear(owner);
                    foreach (var (key, list) in _found)
                        if (key.Owner == owner) _diagnostics.Set(key.File, owner, list.ToArray());
                    if (!IsBusy) BackgroundEnded?.Invoke();
                }
            }
            catch (RegexMatchTimeoutException) { /* a pathological pattern on this line: not a begin or end */ }
        }
        return consumed;
    }

    /// <summary>A new cycle of <paramref name="owner"/>'s matcher begins: what it found so far no longer counts (still shown until the cycle ends).</summary>
    private void ForgetCycle(string owner)
    {
        foreach (var key in _found.Keys.Where(k => k.Owner == owner).ToList())
        {
            _found.Remove(key);
            _dirty.Remove(key);
        }
        _count = Errors = Warnings = 0;
        foreach (var list in _found.Values)
            foreach (var d in list)
            {
                _count++;
                if (d.Severity == DiagnosticSeverity.Error) Errors++;
                else if (d.Severity == DiagnosticSeverity.Warning) Warnings++;
            }
    }

    /// <summary>
    /// The text a terminal would show: escape sequences removed, the last carriage-return overwrite kept. Every trailing CR goes first: a
    /// program's "\r\n" reaches a task's matchers through the PTY as "\r\r\n" (ONLCR), and a CR followed by nothing overwrites nothing. A cursor
    /// movement to another line or column 1, or an erase of the line or screen, also starts the visible text afresh (a progress display redrawn
    /// in place before the line).
    /// </summary>
    internal static string Clean(string raw)
    {
        var text = raw.TrimEnd('\r');
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
                var start = i + 2;
                i += 2;
                while (i < text.Length && !(text[i] >= '@' && text[i] <= '~')) i++;
                if (i < text.Length && StartsLineAfresh(text[i], text.AsSpan(start, i - start))) sb.Clear();
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

    /// <summary>
    /// CSI sequences after which nothing written before them on the line is visible where the next text goes: cursor to the next or previous
    /// line (E, F), to a position (H, f), to column 1 (G), erase the whole line or its start (K 1/2), erase the screen (J 1/2/3).
    /// </summary>
    private static bool StartsLineAfresh(char final, ReadOnlySpan<char> parameters) => final switch
    {
        'E' or 'F' or 'H' or 'f' => true,
        'G' => parameters.Length == 0 || parameters is "1" or "0",
        'K' => parameters is "1" or "2",
        'J' => parameters is "1" or "2" or "3",
        _ => false,
    };

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
