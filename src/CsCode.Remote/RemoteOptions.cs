using CsCode.Remote.Client;
using CsCode.Remote.Install;

namespace CsCode.Remote;

/// <summary>
/// Programmatic knobs of the Remote module (not user settings): what the host process provides and what tests replace. Register an instance before
/// <c>AddCsCodeRemote()</c> to override.
/// </summary>
public sealed class RemoteOptions
{
    /// <summary>Environment changes for every ssh process (null value removes a variable; tests remove SSH_AUTH_SOCK).</summary>
    public IReadOnlyDictionary<string, string?>? SshEnvironment { get; set; }

    /// <summary>Program (+ leading arguments) that ssh runs as askpass with <c>--askpass</c>; null = this process's own executable.</summary>
    public IReadOnlyList<string>? AskpassCommand { get; set; }

    /// <summary>Where the per-connection askpass directory (socket + wrapper script) is created; null = $XDG_RUNTIME_DIR, else the temp directory.</summary>
    public string? AskpassDirectory { get; set; }

    /// <summary>Server bundles to upload; null = found through <c>remote.ssh.serverBundles</c> / next to the program.</summary>
    public ServerBundleSet? Bundles { get; set; }

    /// <summary>Tag for the servers this client starts (<c>--marker</c>), so tests and leak checks can find exactly them.</summary>
    public string? ServerMarker { get; set; }

    /// <summary>
    /// Replaces the ssh transport (tests: a loopback server behind an <c>ssh://</c> authority, chaos wrappers). Receives the host as typed and its
    /// authority; called for every connection attempt.
    /// </summary>
    public Func<string, Protocol.RemoteAuthority, Transport.IRemoteTransport>? TransportFactory { get; set; }

    public ReconnectPolicy? Reconnect { get; set; }
    public TimeProvider? Time { get; set; }

    /// <summary>Per-call timeout of remote requests.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a file operation (and a new terminal, debug adapter or task) waits for a connection being made or remade before failing.</summary>
    public TimeSpan ReconnectWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The liveness check of a connected host (<see cref="HeartbeatPolicy"/>); null turns it off. Tests shorten it.</summary>
    public HeartbeatPolicy? Heartbeat { get; set; } = HeartbeatPolicy.Default;

    /// <summary>How long a connection attempt may take to bring up the server (ssh authentication included); null = 3 minutes. Tests shorten it.</summary>
    public TimeSpan? StartTimeout { get; set; }

    /// <summary>Overrides <c>remote.ssh.reconnectGracePeriod</c> (tests); <see cref="TimeSpan.Zero"/> turns persistence off.</summary>
    public TimeSpan? GracePeriod { get; set; }

    /// <summary>The persistent server's session id to use (tests: a "restarted" client in the same process); null = claimed per host.</summary>
    public string? ServerSession { get; set; }

    /// <summary><c>cscode-server.dll</c> for the local process language host; null = next to the program.</summary>
    public string? LocalServerAssembly { get; set; }

    /// <summary>Data directory of the local process language host; null = <c>{GlobalStorageDir}/language-host</c>.</summary>
    public string? LocalServerDataDir { get; set; }

    /// <summary>Environment changes for the local language host process (null value removes a variable).</summary>
    public IReadOnlyDictionary<string, string?>? LocalServerEnvironment { get; set; }

    /// <summary>Overrides <c>cscode.languageHost</c> (tests).</summary>
    public string? LanguageHostMode { get; set; }

    /// <summary>The askpass command of the running program: its own executable (apphost), or <c>dotnet &lt;entry assembly&gt;</c>.</summary>
    public static IReadOnlyList<string> DefaultAskpassCommand()
    {
        var self = Environment.ProcessPath ?? "cscode";
        var name = Path.GetFileNameWithoutExtension(self);
        // under `dotnet app.dll`, argv[0] is the app's dll (Assembly.Location is empty in single-file builds, so it is not used)
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) && Environment.GetCommandLineArgs() is [{ Length: > 0 } entry, ..] && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return [self, entry];
        return [self];
    }
}
