namespace CsCode.Panels.Git.Engine;

/// <summary>
/// How git's network operations (fetch, pull, push) run so that they can neither wait for ever nor prompt on a terminal: ssh gets
/// <c>BatchMode=yes</c> (no prompts at all) when there is no askpass helper, or the GUI askpass helper (<c>SSH_ASKPASS</c> with
/// <c>SSH_ASKPASS_REQUIRE=force</c>, <c>GIT_ASKPASS</c> for HTTPS credentials) when there is; a connect timeout and keep-alives end a dead link
/// (each only when the user's ssh configuration does not set it); and git runs in a session of its own (<c>setsid</c>), so neither ssh nor a
/// credential helper can open the terminal CsCode was started from (<c>/dev/tty</c>). HTTP transfers get no low-speed limit (as in git and VS Code: a
/// server counting or compressing objects, or running a slow hook, sends next to nothing for minutes); the overall limit
/// (<see cref="GitRunner.NetworkTimeout"/>) ends a real hang. The user's own ssh command (<c>GIT_SSH_COMMAND</c>, <c>core.sshCommand</c>) is kept:
/// when it runs ssh the options are appended after the user's (ssh takes the first value of an option, so the user's own choices win); any other
/// program (a wrapper, plink, <c>GIT_SSH</c>) is left exactly as it is.
/// </summary>
public static class GitNetwork
{
    /// <summary>ssh options for every network operation: fail a connection that is not up in 30 s, and a link silent for a minute.</summary>
    public const string SshOptions = "-o ConnectTimeout=30 -o ServerAliveInterval=15 -o ServerAliveCountMax=4";

    /// <summary>Added to <see cref="SshOptions"/> when no askpass helper can answer ssh's prompts.</summary>
    public const string BatchModeOption = "-o BatchMode=yes";

    /// <summary>The git configuration keys read before a network operation (<c>git config --get-regexp</c>; git prints names in lower case).</summary>
    public const string ConfigPattern = @"^core\.sshcommand$";

    private static readonly (string Keyword, string Option)[] _sshOptions =
        [("connecttimeout", "-o ConnectTimeout=30"), ("serveraliveinterval", "-o ServerAliveInterval=15"), ("serveralivecountmax", "-o ServerAliveCountMax=4")];

    /// <summary>
    /// <see cref="SshOptions"/> without the ones whose keyword (lower case) is in <paramref name="configured"/>: a value in the user's ssh
    /// configuration wins over ours (on ssh's command line ours would win, as command-line options come first).
    /// </summary>
    public static string SshOptionsFor(IReadOnlySet<string>? configured)
        => string.Join(' ', _sshOptions.Where(o => configured?.Contains(o.Keyword) != true).Select(o => o.Option));

    /// <summary>
    /// The environment of one network operation.
    /// </summary>
    /// <param name="variable">The process environment (<c>GIT_SSH_COMMAND</c>, <c>GIT_SSH</c>).</param>
    /// <param name="config">The repository's values of <see cref="ConfigPattern"/>'s keys, by lower-case name.</param>
    /// <param name="askpass">The askpass helper of this operation, or null.</param>
    /// <param name="sshConfigured">The keywords the user's ssh configuration sets (<see cref="ConfiguredSshKeywords"/>); those options are not passed.</param>
    public static Dictionary<string, string> Environment(Func<string, string?> variable, IReadOnlyDictionary<string, string> config,
        (string Program, IReadOnlyDictionary<string, string?> Environment)? askpass, IReadOnlySet<string>? sshConfigured = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        // without a helper ssh must not ask; a BatchMode of the user's ssh configuration is the user's choice (ssh has no terminal either way)
        var batch = askpass is null && sshConfigured?.Contains("batchmode") != true;
        var options = string.Join(' ', new[] { SshOptionsFor(sshConfigured), batch ? BatchModeOption : "" }.Where(o => o.Length > 0));
        var userCommand = UserSshCommand(variable, config);
        if (userCommand is not null)
        {
            if (AppendSshOptions(userCommand, options) is { } appended) env["GIT_SSH_COMMAND"] = appended;
        }
        else if (NonEmpty(variable("GIT_SSH")) is null) env["GIT_SSH_COMMAND"] = ("ssh " + options).TrimEnd();
        if (askpass is { } a)
        {
            // replaces the user's GIT_ASKPASS / core.askPass / SSH_ASKPASS for this operation; git.useIntegratedAskPass=false keeps them
            foreach (var (k, v) in a.Environment) if (v is not null) env[k] = v;
            env["GIT_ASKPASS"] = a.Program;
        }
        return env;
    }

    /// <summary>The user's ssh command as git takes it: <c>GIT_SSH_COMMAND</c>, else <c>core.sshCommand</c>; null when neither is set.</summary>
    public static string? UserSshCommand(Func<string, string?> variable, IReadOnlyDictionary<string, string> config)
        => NonEmpty(variable("GIT_SSH_COMMAND")) ?? NonEmpty(config.GetValueOrDefault("core.sshcommand"));

    /// <summary><paramref name="command"/> with <paramref name="options"/> appended when its program is ssh; null for any other program.</summary>
    public static string? AppendSshOptions(string command, string options)
    {
        if (!RunsSsh(command)) return null;
        return options.Length == 0 ? command.TrimEnd() : command.TrimEnd() + " " + options;
    }

    private static bool RunsSsh(string command)
    {
        var program = FirstWord(command.Trim());
        if (program is null) return false;
        var name = program.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name is "ssh" or "ssh.exe";
    }

    /// <summary>
    /// The ssh configuration files ssh reads for <paramref name="userCommand"/> (null: plain <c>ssh</c>): the file of its <c>-F</c> option (ssh then
    /// reads no other), else <c>~/.ssh/config</c> and <c>/etc/ssh/ssh_config</c>.
    /// </summary>
    public static IReadOnlyList<string> SshConfigFiles(string? userCommand, string home)
    {
        if (userCommand is not null && RunsSsh(userCommand))
        {
            var words = userCommand.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < words.Length; i++)
            {
                var file = words[i] == "-F" && i + 1 < words.Length ? words[i + 1] : words[i].StartsWith("-F", StringComparison.Ordinal) && words[i].Length > 2 ? words[i][2..] : null;
                if (file is null) continue;
                file = file.Trim('"', '\'');
                return [file == "~" || file.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, file.Length > 2 ? file[2..] : "") : file];
            }
        }
        return [Path.Combine(home, ".ssh", "config"), "/etc/ssh/ssh_config"];
    }

    /// <summary>
    /// The keywords (lower case) set anywhere in <paramref name="files"/> and the files they <c>Include</c>, whatever <c>Host</c>/<c>Match</c> block
    /// they are in (a value for some hosts is enough to leave that option to the configuration). Unreadable or missing files count as empty.
    /// Reads files: called on the git lane (a dedicated thread), not the UI thread.
    /// </summary>
#pragma warning disable RS0030 // runs on the git lane, not the UI thread
    public static IReadOnlySet<string> ConfiguredSshKeywords(IEnumerable<string> files)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files) Scan(f, 0);
        return set;

        void Scan(string file, int depth)
        {
            if (depth > 8 || !seen.Add(file)) return;
            string[] lines;
            try { lines = File.Exists(file) ? File.ReadAllLines(file) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var end = line.IndexOfAny([' ', '\t', '=']);
                var keyword = (end < 0 ? line : line[..end]).ToLowerInvariant();
                if (keyword != "include") { set.Add(keyword); continue; }
                foreach (var pattern in line[end..].TrimStart(' ', '\t', '=').Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                    foreach (var included in Expand(pattern.Trim('"'), Path.GetDirectoryName(file) ?? "")) Scan(included, depth + 1);
            }
        }

        static IEnumerable<string> Expand(string pattern, string baseDir)
        {
            if (pattern == "~" || pattern.StartsWith("~/", StringComparison.Ordinal))
                pattern = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), pattern.Length > 2 ? pattern[2..] : "");
            else if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(baseDir, pattern); // ssh: relative to ~/.ssh (user) or /etc/ssh (system)
            var dir = Path.GetDirectoryName(pattern);
            var name = Path.GetFileName(pattern);
            if (dir is null || name.Length == 0) return [];
            if (name.IndexOfAny(['*', '?']) < 0) return [pattern];
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, name).Order(StringComparer.Ordinal) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }
#pragma warning restore RS0030

    /// <summary>Parses <c>git config --get-regexp</c> output (<c>name value</c> lines, every scope in order) into a map by name, the last value winning.</summary>
    public static Dictionary<string, string> ParseConfig(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ');
            var (name, value) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
            map[name.Trim().ToLowerInvariant()] = value.TrimEnd('\r'); // the last value wins, as in git (system, global, then the repository's)
        }
        return map;
    }

    /// <summary>
    /// The program (and its leading arguments) that starts git in a session of its own, without a controlling terminal: <c>setsid</c> on Linux
    /// (util-linux's or busybox's: no option, as busybox's takes none but <c>-c</c>), else perl's <c>POSIX::setsid</c> followed by an exec; null when
    /// neither is there (Windows, a minimal system). Both run git in the same process (killing it kills git, its exit code is git's): a child of
    /// .NET is never a process-group leader, so setsid calls <c>setsid()</c> and execs instead of forking (which <c>--wait</c> would be for).
    /// Resolved once, on the git lane.
    /// </summary>
    internal static IReadOnlyList<string>? DetachLauncher => _detach.Value;

    private static readonly Lazy<IReadOnlyList<string>?> _detach = new(() => OperatingSystem.IsWindows() ? null
        : ResolveDetach(OperatingSystem.IsLinux() ? ["/usr/bin/setsid", "/bin/setsid"] : [], ["/usr/bin/perl", "/bin/perl"]));

    /// <summary>The <see cref="DetachLauncher"/> of the first executable of <paramref name="setsid"/>, else of <paramref name="perl"/>.</summary>
    internal static IReadOnlyList<string>? ResolveDetach(IEnumerable<string> setsid, IEnumerable<string> perl)
    {
        foreach (var path in setsid)
            if (GitRunner.IsExecutable(path)) return [path];
        foreach (var path in perl)
            if (GitRunner.IsExecutable(path)) return [path, "-MPOSIX", "-e", "POSIX::setsid(); exec { $ARGV[0] } @ARGV or exit 127;"];
        return null;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? FirstWord(string command)
    {
        if (command.Length == 0) return null;
        if (command[0] is '"' or '\'')
        {
            var end = command.IndexOf(command[0], 1);
            return end < 0 ? command[1..] : command[1..end];
        }
        var space = command.IndexOfAny([' ', '\t']);
        return space < 0 ? command : command[..space];
    }
}
