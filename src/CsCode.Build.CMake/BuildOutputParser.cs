using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Build.CMake;

/// <summary>
/// Turns configure and build output into Error List entries: compiler diagnostics in the gcc / clang form (<c>file:line:col: error: message</c>,
/// what VS Code's <c>$gcc</c> matches; <c>note:</c> lines become information) and cmake's own <c>CMake Error at file:line (command):</c> /
/// <c>CMake Warning at …</c> blocks, whose message is the indented lines that follow. A relative file name is looked up in the build directory
/// (Ninja runs the compiler there), then the source directory. Problems are published per run under one owner (cleared when the run starts),
/// at most <see cref="MaxProblems"/>, in batches: <see cref="Flush"/> after a burst of lines and at the end. Not thread-safe: one run feeds it.
/// </summary>
internal sealed class BuildOutputParser(IDiagnosticService? diagnostics, string owner, string buildDir, string sourceDir)
{
    public const int MaxProblems = 2000;
    private const int MaxMessageLines = 20;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    // file:line[:col]: [fatal ]error|warning|note: message — the file part is lazy and may hold a drive letter
    private static readonly Regex Compiler = new(@"^(?<file>(?:[A-Za-z]:)?[^:]+?):(?<line>\d+)(?::(?<col>\d+))?:\s+(?:fatal\s+)?(?<sev>error|warning|note):\s+(?<msg>.*)$",
        RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex CMakeMessage = new(@"^CMake (?<sev>Error|Warning|Deprecation Warning|Deprecation Error)(?: \(dev\))? at (?<file>.+?):(?<line>\d+) \((?<cmd>[^)]*)\):\s*$",
        RegexOptions.CultureInvariant, Timeout);

    private readonly Dictionary<Uri, List<Diagnostic>> _found = new();
    private readonly HashSet<Uri> _dirty = new();
    private (Uri Uri, Diagnostic Diagnostic, List<string> Lines)? _cmakeBlock;
    private int _count;

    public int Errors { get; private set; }
    public int Warnings { get; private set; }

    public void Start() => diagnostics?.Clear(owner);

    public void Line(string raw)
    {
        var line = StripEscapes(raw);
        if (_cmakeBlock is { } block)
        {
            // the message is the indented lines after the header, up to a blank line followed by a non-indented line
            if (line.StartsWith("  ", StringComparison.Ordinal) && block.Lines.Count < MaxMessageLines) { block.Lines.Add(line.Trim()); return; }
            if (line.Length == 0) return;
            EndBlock();
        }
        try
        {
            if (line.StartsWith("CMake ", StringComparison.Ordinal) && CMakeMessage.Match(line) is { Success: true } cm)
            {
                var severity = cm.Groups["sev"].Value.Contains("Error", StringComparison.Ordinal) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                if (Resolve(cm.Groups["file"].Value, preferSource: true) is { } uri)
                {
                    var l = Math.Max(0, int.Parse(cm.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1);
                    _cmakeBlock = (uri, new Diagnostic(new TextRange(new TextPosition(l, 0), new TextPosition(l, 0)), "", severity, cm.Groups["cmd"].Value, "cmake"), []);
                }
                Count(severity);
                return;
            }
            if (Compiler.Match(line) is not { Success: true } m) return;
            var sev = m.Groups["sev"].Value switch { "error" => DiagnosticSeverity.Error, "warning" => DiagnosticSeverity.Warning, _ => DiagnosticSeverity.Information };
            Count(sev);
            if (Resolve(m.Groups["file"].Value, preferSource: false) is not { } file) return;
            var row = Math.Max(0, int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1);
            var col = m.Groups["col"].Success ? Math.Max(0, int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1) : 0;
            var (message, code) = SplitWarningFlag(m.Groups["msg"].Value);
            Add(file, new Diagnostic(new TextRange(new TextPosition(row, col), new TextPosition(row, col)), message, sev, code, "cmake"));
        }
        catch (RegexMatchTimeoutException) { }
        catch (OverflowException) { }
    }

    /// <summary>The run ended: the last block is closed and everything published.</summary>
    public void Complete()
    {
        EndBlock();
        Flush();
    }

    /// <summary>Publishes the files whose problems changed since the last flush.</summary>
    public void Flush()
    {
        if (diagnostics is null || _dirty.Count == 0) return;
        foreach (var uri in _dirty) diagnostics.Set(uri, owner, _found[uri].ToArray());
        _dirty.Clear();
    }

    private void EndBlock()
    {
        if (_cmakeBlock is not { } b) return;
        _cmakeBlock = null;
        var message = b.Lines.Count == 0 ? $"CMake {b.Diagnostic.Severity.ToString().ToLowerInvariant()} in {b.Diagnostic.Code}" : string.Join(' ', b.Lines);
        Add(b.Uri, b.Diagnostic with { Message = message });
    }

    private void Count(DiagnosticSeverity severity)
    {
        if (severity == DiagnosticSeverity.Error) Errors++;
        else if (severity == DiagnosticSeverity.Warning) Warnings++;
    }

    private void Add(Uri file, Diagnostic d)
    {
        if (_count >= MaxProblems) return;
        if (!_found.TryGetValue(file, out var list)) _found[file] = list = [];
        if (list.Contains(d)) return; // the same header included from several units reports the same problem each time
        list.Add(d);
        _dirty.Add(file);
        _count++;
    }

    /// <summary>clang's trailing <c>[-Wunused-variable]</c> becomes the problem's code.</summary>
    private static (string Message, string? Code) SplitWarningFlag(string message)
    {
        var trimmed = message.TrimEnd();
        if (!trimmed.EndsWith(']')) return (trimmed, null);
        var open = trimmed.LastIndexOf(" [-W", StringComparison.Ordinal);
        if (open < 0) return (trimmed, null);
        return (trimmed[..open], trimmed[(open + 2)..^1]);
    }

    private Uri? Resolve(string file, bool preferSource)
    {
        file = file.Trim();
        if (file.Length == 0) return null;
        string? path = null;
        if (Path.IsPathRooted(file)) path = file;
        else
        {
            foreach (var root in preferSource ? new[] { sourceDir, buildDir } : [buildDir, sourceDir])
            {
                var candidate = Path.GetFullPath(Path.Combine(root, file));
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            path ??= Path.GetFullPath(Path.Combine(preferSource ? sourceDir : buildDir, file));
        }
        try { return new Uri(Path.GetFullPath(path)); }
        catch (UriFormatException) { return null; }
    }

    /// <summary>Removes ANSI escape sequences (colour diagnostics, Ninja's status line).</summary>
    internal static string StripEscapes(string line)
    {
        if (line.IndexOf('\u001b') < 0) return line.TrimEnd('\r');
        var sb = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                i += 2;
                while (i < line.Length && line[i] is not (>= '@' and <= '~')) i++;
                continue;
            }
            sb.Append(line[i]);
        }
        return sb.ToString().TrimEnd('\r');
    }
}
