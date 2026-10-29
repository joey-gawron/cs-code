namespace CsCode.App.Startup;

/// <summary>One path from the command line, with an optional position (<c>file.cs:12:3</c>).</summary>
public sealed record StartupTarget(string Path, int? Line = null, int? Column = null);

/// <summary>
/// <c>cscode [paths...] [--new-window] [--wait] [--goto file:line:col] [--help] [--version]</c>. Directories open as the workspace folder, files open
/// as editors. <c>--wait</c> is accepted and ignored (the shell never blocks the launcher). Unknown switches are collected, not fatal.
/// </summary>
public sealed record CommandLineOptions(
    IReadOnlyList<StartupTarget> Targets,
    bool NewWindow,
    bool Wait,
    bool ShowHelp,
    bool ShowVersion,
    IReadOnlyList<string> UnknownSwitches)
{
    public static CommandLineOptions Empty { get; } = new([], false, false, false, false, []);

    public static CommandLineOptions Parse(IEnumerable<string> args, string? workingDirectory = null)
    {
        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        var targets = new List<StartupTarget>();
        var unknown = new List<string>();
        bool newWindow = false, wait = false, help = false, version = false, gotoNext = false;
        foreach (var arg in args)
        {
            if (gotoNext) { targets.Add(ParseTarget(arg, cwd)); gotoNext = false; continue; }
            switch (arg)
            {
                case "-n" or "--new-window": newWindow = true; break;
                case "-w" or "--wait": wait = true; break;
                case "-h" or "--help": help = true; break;
                case "-v" or "--version": version = true; break;
                case "-g" or "--goto": gotoNext = true; break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1) unknown.Add(arg);
                    else targets.Add(ParseTarget(arg, cwd));
                    break;
            }
        }
        return new CommandLineOptions(targets, newWindow, wait, help, version, unknown);
    }

    /// <summary>Splits a trailing <c>:line[:column]</c> unless the whole string already names an existing path (Windows drive letters and odd file names).</summary>
    internal static StartupTarget ParseTarget(string arg, string cwd)
    {
        var full = System.IO.Path.IsPathRooted(arg) ? arg : System.IO.Path.GetFullPath(arg, cwd);
        if (File.Exists(full) || Directory.Exists(full)) return new StartupTarget(full);
        var parts = arg.Split(':');
        // "C:\x.cs:12:3" has an extra leading segment for the drive; peel numbers off the end only.
        int? column = null, line = null;
        var n = parts.Length;
        if (n > 1 && int.TryParse(parts[n - 1], out var last))
        {
            if (n > 2 && int.TryParse(parts[n - 2], out var prev)) { line = prev; column = last; n -= 2; }
            else { line = last; n -= 1; }
        }
        var path = string.Join(':', parts.Take(n));
        var resolved = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.GetFullPath(path, cwd);
        return new StartupTarget(resolved, line, column);
    }

    public const string HelpText = """
        Usage: cscode [options] [paths...]

          <path>              A folder (opens as the workspace) or a file (opens in an editor). file:line[:col] jumps to a position.
          -g, --goto <file:line[:col]>  Open a file at a position.
          -n, --new-window    Open in a new window.
          -w, --wait          Accepted for compatibility; ignored.
          -h, --help          Show this help.
          -v, --version       Show the version.
        """;
}
