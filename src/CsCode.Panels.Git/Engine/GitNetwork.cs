namespace CsCode.Panels.Git.Engine;

/// <summary>
/// How git's network operations (fetch, pull, push) run so that they can neither wait for ever nor prompt on a terminal: ssh gets
/// <c>BatchMode=yes</c> (no prompts at all) when there is no askpass helper, or the GUI askpass helper (<c>SSH_ASKPASS</c> with
/// <c>SSH_ASKPASS_REQUIRE=force</c>, <c>GIT_ASKPASS</c> for HTTPS credentials) when there is; a connect timeout and keep-alives end a dead link; HTTP
/// transfers below 1 KB/s for 60 s are given up; and git runs in a session of its own (<c>setsid</c>), so neither ssh nor a credential helper can open
/// the terminal CsCode was started from (<c>/dev/tty</c>). The user's own ssh command (<c>GIT_SSH_COMMAND</c>, <c>core.sshCommand</c>) is kept: when
/// it runs ssh the options are appended after the user's (ssh takes the first value of an option, so the user's own choices win); any other program
/// (a wrapper, plink, <c>GIT_SSH</c>) is left exactly as it is.
/// </summary>
public static class GitNetwork
{
    /// <summary>ssh options for every network operation: fail a connection that is not up in 30 s, and a link silent for a minute.</summary>
    public const string SshOptions = "-o ConnectTimeout=30 -o ServerAliveInterval=15 -o ServerAliveCountMax=4";

    /// <summary>Added to <see cref="SshOptions"/> when no askpass helper can answer ssh's prompts.</summary>
    public const string BatchModeOption = "-o BatchMode=yes";

    /// <summary>The git configuration keys read before a network operation (<c>git config --get-regexp</c>; git prints names in lower case).</summary>
    public const string ConfigPattern = @"^(core\.sshcommand|http\.lowspeedlimit|http\.lowspeedtime)$";

    /// <summary>
    /// The environment of one network operation.
    /// </summary>
    /// <param name="variable">The process environment (<c>GIT_SSH_COMMAND</c>, <c>GIT_SSH</c>, <c>GIT_HTTP_LOW_SPEED_*</c>).</param>
    /// <param name="config">The repository's values of <see cref="ConfigPattern"/>'s keys, by lower-case name.</param>
    /// <param name="askpass">The askpass helper of this operation, or null.</param>
    public static Dictionary<string, string> Environment(Func<string, string?> variable, IReadOnlyDictionary<string, string> config, (string Program, IReadOnlyDictionary<string, string?> Environment)? askpass)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = askpass is null ? SshOptions + " " + BatchModeOption : SshOptions;
        var userCommand = NonEmpty(variable("GIT_SSH_COMMAND")) ?? NonEmpty(config.GetValueOrDefault("core.sshcommand"));
        if (userCommand is not null)
        {
            if (AppendSshOptions(userCommand, options) is { } appended) env["GIT_SSH_COMMAND"] = appended;
        }
        else if (NonEmpty(variable("GIT_SSH")) is null) env["GIT_SSH_COMMAND"] = "ssh " + options;
        if (askpass is { } a)
        {
            foreach (var (k, v) in a.Environment) if (v is not null) env[k] = v;
            env["GIT_ASKPASS"] = a.Program;
        }
        // a stalled HTTP transfer ends (git's default never gives up), unless the user chose their own limits
        if (NonEmpty(variable("GIT_HTTP_LOW_SPEED_LIMIT")) is null && NonEmpty(variable("GIT_HTTP_LOW_SPEED_TIME")) is null
            && !config.ContainsKey("http.lowspeedlimit") && !config.ContainsKey("http.lowspeedtime"))
        {
            env["GIT_HTTP_LOW_SPEED_LIMIT"] = "1000";
            env["GIT_HTTP_LOW_SPEED_TIME"] = "60";
        }
        return env;
    }

    /// <summary><paramref name="command"/> with <paramref name="options"/> appended when its program is ssh; null for any other program.</summary>
    public static string? AppendSshOptions(string command, string options)
    {
        var program = FirstWord(command.Trim());
        if (program is null) return null;
        var name = program.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name is "ssh" or "ssh.exe" ? command.TrimEnd() + " " + options : null;
    }

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
    /// The program (and its leading arguments) that starts git in a session of its own, without a controlling terminal: util-linux's
    /// <c>setsid --wait</c> (it hands back git's exit code), else perl's <c>POSIX::setsid</c> followed by an exec (same process, so killing it kills
    /// git); null when neither is there (Windows, a minimal system). Resolved once, on the git lane.
    /// </summary>
    internal static IReadOnlyList<string>? DetachLauncher => _detach.Value;

    private static readonly Lazy<IReadOnlyList<string>?> _detach = new(ResolveDetach);

    private static IReadOnlyList<string>? ResolveDetach()
    {
        if (OperatingSystem.IsWindows()) return null;
        if (OperatingSystem.IsLinux())
            foreach (var path in new[] { "/usr/bin/setsid", "/bin/setsid" })
                if (GitRunner.IsExecutable(path)) return [path, "--wait"];
        foreach (var path in new[] { "/usr/bin/perl", "/bin/perl" })
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
