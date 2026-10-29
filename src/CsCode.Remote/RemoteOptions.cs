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

    /// <summary>Server bundles to upload; null = found through <c>remote.ssh.serverBundles</c> / next to the program.</summary>
    public ServerBundleSet? Bundles { get; set; }

    /// <summary>Tag for the servers this client starts (<c>--marker</c>), so tests and leak checks can find exactly them.</summary>
    public string? ServerMarker { get; set; }

    public ReconnectPolicy? Reconnect { get; set; }
    public TimeProvider? Time { get; set; }

    /// <summary>Per-call timeout of remote requests.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary><c>cscode-server.dll</c> for the local process language host; null = next to the program.</summary>
    public string? LocalServerAssembly { get; set; }

    /// <summary>Data directory of the local process language host; null = <c>{GlobalStorageDir}/language-host</c>.</summary>
    public string? LocalServerDataDir { get; set; }

    /// <summary>Overrides <c>cscode.languageHost</c> (tests).</summary>
    public string? LanguageHostMode { get; set; }

    /// <summary>The askpass command of the running program: its own executable (apphost), or <c>dotnet &lt;entry assembly&gt;</c>.</summary>
    public static IReadOnlyList<string> DefaultAskpassCommand()
    {
        var self = Environment.ProcessPath ?? "cscode";
        var name = Path.GetFileNameWithoutExtension(self);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
            return [self, entry];
        return [self];
    }
}
