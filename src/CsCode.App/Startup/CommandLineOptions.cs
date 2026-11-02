namespace CsCode.App.Startup;

/// <summary>One path from the command line, with an optional position (<c>file.cs:12:3</c>).</summary>
public sealed record StartupTarget(string Path, int? Line = null, int? Column = null);

/// <summary>
/// The <c>cscode</c> command line (docs/SESSIONS.md, "Command line"; a stable interface: the desktop entry, the install wave's
/// <c>~/.local/bin/cscode</c> and scripts depend on it):
/// <c>cscode [paths...] [--session NAME | --new-window] [--wait] [--goto file:line:col] [--list-sessions] [--background] [--quit [NAME | --all]]
/// [--foreground] [--help] [--version]</c>. Directories open as the workspace folder, files open as editors. Unknown switches are collected, not
/// fatal. The internal switches (<c>--host</c>, <c>--spare</c>, <c>--quit-token TOKEN</c>, <c>--hidden</c>, <c>--unnamed</c>, <c>--seed-from NAME</c>,
/// <c>--headless-test</c>)
/// are how the launcher starts CsCode processes; they are not part of the public interface.
/// </summary>
public sealed record CommandLineOptions(
    IReadOnlyList<StartupTarget> Targets,
    bool NewWindow,
    bool Wait,
    bool ShowHelp,
    bool ShowVersion,
    IReadOnlyList<string> UnknownSwitches)
{
    /// <summary><c>ssh://[user@]host[:port]/path</c> (also <c>docker://container/path</c>, <c>wsl://distro/path</c>) arguments: remote folders to open (connecting first).</summary>
    public IReadOnlyList<string> RemoteTargets { get; init; } = [];

    public static CommandLineOptions Empty { get; } = new([], false, false, false, false, []);

    /// <summary><c>--session NAME</c>: the named session (created when it does not exist).</summary>
    public string? Session { get; init; }
    /// <summary><c>--list-sessions</c>.</summary>
    public bool ListSessions { get; init; }
    /// <summary><c>--background</c>: start the warm spare (and the resident sessions, hidden, with <c>cscode.background.restoreOnLogin</c>); show nothing.</summary>
    public bool Background { get; init; }
    /// <summary><c>--quit [NAME]</c>; with <see cref="All"/> every CsCode process.</summary>
    public bool Quit { get; init; }
    public bool All { get; init; }
    /// <summary><c>--foreground</c>: run the window in this process, without background mode (the behaviour before sessions existed).</summary>
    public bool Foreground { get; init; }

    // ---- internal: how the launcher starts CsCode processes ----

    /// <summary><c>--host</c>: this process is a CsCode process (a session host or the spare), not a launcher.</summary>
    public bool Host { get; init; }
    /// <summary><c>--spare</c>: start as the warm spare (no session until claimed).</summary>
    public bool Spare { get; init; }
    /// <summary><c>--quit-token TOKEN</c>: the spare's quit-all token when the launcher started it (<c>RuntimeDirectory.QuitAllToken</c>).</summary>
    public string? QuitToken { get; init; }
    /// <summary><c>--hidden</c>: restore the session without showing its window.</summary>
    public bool Hidden { get; init; }
    /// <summary><c>--unnamed</c>: the session is a new unnamed one (<c>window-N</c>).</summary>
    public bool Unnamed { get; init; }
    /// <summary><c>--seed-from NAME</c>: start the session as a copy of NAME (whose owner does not respond).</summary>
    public string? SeedFrom { get; init; }
    /// <summary><c>--headless-test</c>: the Avalonia headless platform (no display) and the test-only IPC verbs; integration tests only.</summary>
    public bool HeadlessTest { get; init; }

    /// <summary>True when this process is a launcher (client mode): it talks to CsCode processes and exits.</summary>
    public bool IsLauncher => !Host && !Foreground && !ShowHelp && !ShowVersion;

    public static CommandLineOptions Parse(IEnumerable<string> args, string? workingDirectory = null)
    {
        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        var targets = new List<StartupTarget>();
        var unknown = new List<string>();
        var remote = new List<string>();
        bool newWindow = false, wait = false, help = false, version = false;
        bool list = false, background = false, quit = false, all = false, foreground = false;
        bool host = false, spare = false, hidden = false, unnamed = false, headless = false;
        string? session = null, seedFrom = null, quitToken = null;
        string? pending = null; // the switch whose value comes next
        foreach (var arg in args)
        {
            switch (pending)
            {
                case "--goto": targets.Add(ParseTarget(arg, cwd)); pending = null; continue;
                case "--session": session = arg; pending = null; continue;
                case "--seed-from": seedFrom = arg; pending = null; continue;
                case "--quit-token": quitToken = arg; pending = null; continue;
                case "--quit":
                    pending = null;
                    if (!arg.StartsWith('-')) { session = arg; continue; }
                    break; // "--quit --all": handle this switch normally
            }
            switch (arg)
            {
                case "-n" or "--new-window": newWindow = true; break;
                case "-w" or "--wait": wait = true; break;
                case "-h" or "--help": help = true; break;
                case "-v" or "--version": version = true; break;
                case "-g" or "--goto": pending = "--goto"; break;
                case "-s" or "--session": pending = "--session"; break;
                case "--list-sessions": list = true; break;
                case "--background": background = true; break;
                case "--quit": quit = true; pending = "--quit"; break;
                case "--all": all = true; break;
                case "--foreground": foreground = true; break;
                case "--host": host = true; break;
                case "--spare": spare = true; break;
                case "--hidden": hidden = true; break;
                case "--unnamed": unnamed = true; break;
                case "--seed-from": pending = "--seed-from"; break;
                case "--quit-token": pending = "--quit-token"; break;
                case "--headless-test": headless = true; break;
                default:
                    if (arg.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("docker://", StringComparison.OrdinalIgnoreCase)
                        || arg.StartsWith("wsl://", StringComparison.OrdinalIgnoreCase)) remote.Add(arg);
                    else if (arg.StartsWith('-') && arg.Length > 1) unknown.Add(arg);
                    else targets.Add(ParseTarget(arg, cwd));
                    break;
            }
        }
        return new CommandLineOptions(targets, newWindow, wait, help, version, unknown)
        {
            RemoteTargets = remote,
            Session = session, ListSessions = list, Background = background, Quit = quit, All = all, Foreground = foreground,
            Host = host, Spare = spare, QuitToken = quitToken, Hidden = hidden, Unnamed = unnamed, SeedFrom = seedFrom, HeadlessTest = headless,
        };
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

          <path>                 A folder (opens as the workspace) or a file (opens in an editor). file:line[:col] jumps to a position.
          ssh://[user@]host[:port]/path   Connect to the host over ssh and open the remote folder
                                          (docker://container/path and wsl://distro/path likewise).
          -g, --goto <file:line[:col]>  Open a file at a position.
          -s, --session <name>   Open the named session (shown at once when it is already running; created when it does not exist).
          -n, --new-window       Open a new unnamed session (window-N).
          -w, --wait             Wait until the opened file's tab is closed (for use as $EDITOR / the git editor).
          --list-sessions        List saved and running sessions.
          --background           Start CsCode in the background without a window (for login autostart).
          --quit [<name>]        Quit a session's process (default: the last active one; once it has quit, the window shown last,
                                 never a hidden session); --quit --all quits every CsCode process.
          --foreground           Run the window in this process, without background mode.
          -h, --help             Show this help.
          -v, --version          Show the version.

        Without --session, files open in the last active session, a folder in the session that has it (else a new one), and
        no arguments follow the setting cscode.sessions.default (last | new | <name>). The last active session counts while its
        process runs, even one that does not respond (a copy opens, and --quit says so); once it has quit, files go to the session
        shown last. Closing a window keeps its session running in the background (cscode.background.keepRunning); File > Quit
        Session ends it.
        """;
}
