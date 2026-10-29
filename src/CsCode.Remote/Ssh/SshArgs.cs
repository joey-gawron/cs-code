using CsCode.Remote.Protocol;

namespace CsCode.Remote.Ssh;

/// <summary>How to run the system <c>ssh</c> client (it honours the user's ssh_config: ProxyJump, IdentityFile, ControlMaster, agent, 2FA…).</summary>
public sealed record SshOptions
{
    /// <summary>The ssh binary (<c>remote.ssh.path</c>); resolved on PATH when not rooted.</summary>
    public string SshPath { get; init; } = "ssh";
    /// <summary><c>-F</c>: a config file used INSTEAD of ~/.ssh/config and /etc/ssh/ssh_config (<c>remote.ssh.configFile</c>; tests point it at a temp file).</summary>
    public string? ConfigFile { get; init; }
    /// <summary>Extra <c>-o Key=Value</c> options (<c>remote.ssh.options</c>).</summary>
    public IReadOnlyList<string> ExtraOptions { get; init; } = [];
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Environment changes for the ssh process (null value = remove; askpass variables, SSH_AUTH_SOCK for tests).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>
/// Builds ssh argument vectors (never a local shell command line). The destination is validated (no leading '-', no whitespace or control
/// characters) and placed after <c>--</c>, so a hostile host string cannot become an ssh option such as <c>-oProxyCommand=…</c>.
/// </summary>
public static class SshArgs
{
    /// <summary>Arguments to run <paramref name="remoteCommand"/> (already quoted by <see cref="PosixShell"/>) on the destination, without a tty.</summary>
    public static IReadOnlyList<string> Command(SshOptions options, RemoteAuthority destination, string remoteCommand)
    {
        var args = Common(options);
        args.AddRange(["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4", "-o", "ExitOnForwardFailure=yes",
            "-o", "ConnectTimeout=" + Math.Max(1, (int)options.ConnectTimeout.TotalSeconds)]);
        if (destination.User is { } user) { ValidateWord(user, "user"); args.AddRange(["-l", user]); }
        if (destination.Port is { } port) args.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.Add("--");
        args.Add(ValidateHost(destination.Host));
        args.Add(remoteCommand);
        return args;
    }

    /// <summary><c>ssh -G</c>: prints the effective configuration for the destination without connecting.</summary>
    public static IReadOnlyList<string> ResolveConfig(SshOptions options, RemoteAuthority destination)
    {
        var args = new List<string> { "-G" };
        args.AddRange(Common(options).Skip(1)); // no -T needed
        if (destination.User is { } user) { ValidateWord(user, "user"); args.AddRange(["-l", user]); }
        if (destination.Port is { } port) args.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.Add("--");
        args.Add(ValidateHost(destination.Host));
        return args;
    }

    private static List<string> Common(SshOptions options)
    {
        var args = new List<string> { "-T" };
        if (options.ConfigFile is { Length: > 0 } cfg) args.AddRange(["-F", cfg]);
        foreach (var o in options.ExtraOptions)
        {
            if (string.IsNullOrWhiteSpace(o)) continue;
            var t = o.Trim();
            if (t.StartsWith('-') || !t.Contains('=') && !t.Contains(' ') || t.Any(char.IsControl))
                throw new ArgumentException($"remote.ssh.options entries must look like 'Key=Value': '{o}'");
            args.AddRange(["-o", t]);
        }
        return args;
    }

    public static string ValidateHost(string host)
    {
        ValidateWord(host, "host");
        return host;
    }

    private static void ValidateWord(string value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith('-') || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"Invalid ssh {what}: '{value}'");
    }
}
