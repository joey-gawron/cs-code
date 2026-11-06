using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Remote;

/// <summary>Settings of the Remote module (registered with their defaults; see docs/REMOTE.md).</summary>
public static class RemoteSettings
{
    public const string SshPath = "remote.ssh.path";
    public const string ConfigFile = "remote.ssh.configFile";
    public const string Options = "remote.ssh.options";
    public const string ConnectTimeout = "remote.ssh.connectTimeout";
    public const string ServerInstallPath = "remote.ssh.serverInstallPath";
    public const string ServerDataPath = "remote.ssh.serverDataPath";
    public const string ServerBundles = "remote.ssh.serverBundles";
    public const string ReconnectAttempts = "remote.ssh.reconnectAttempts";
    public const string UseAskpass = "remote.ssh.useAskpass";
    /// <summary>Seconds the remote server keeps running for a client that lost its connection (terminals, debug adapters, tasks and language servers stay); 0 = the server ends with the connection.</summary>
    public const string ReconnectGracePeriod = "remote.ssh.reconnectGracePeriod";
    /// <summary>Per-host overrides: <c>"remote.ssh.hosts": { "box": { "serverInstallPath": "/opt/cscode", "options": ["Compression=yes"] } }</c>.</summary>
    public const string Hosts = "remote.ssh.hosts";
    /// <summary>The docker CLI for <c>docker://container</c> remotes.</summary>
    public const string DockerPath = "remote.docker.path";
    /// <summary>wsl.exe for <c>wsl://distro</c> remotes (Windows).</summary>
    public const string WslPath = "remote.wsl.path";
    /// <summary>limactl for <c>lima://instance</c> remotes (Lima VMs).</summary>
    public const string LimaPath = "remote.lima.path";
    /// <summary><c>auto</c>: the remote host's terminals, language servers, tasks and debuggers run inside the workspace flake's <c>nix develop</c> environment; <c>off</c> (default).</summary>
    public const string NixDevelop = "remote.nix.develop";
    /// <summary>Which flake's dev shell: empty = the workspace folder, <c>#name</c> = that devShell of it, else a flake reference as <c>nix develop</c> takes it.</summary>
    public const string NixFlake = "remote.nix.flake";
    /// <summary>Seconds to wait for <c>nix develop</c> before using the plain environment.</summary>
    public const string NixTimeout = "remote.nix.timeout";
    /// <summary><c>inProcess</c> (default) or <c>process</c>: run the language client in a separate local cscode-server process (own heap/GC/CPU scheduling). Applies on restart.</summary>
    public const string LanguageHost = "cscode.languageHost";

    // Scopes (remote-5, VS Code's Remote-SSH): every remote.ssh.* setting and the docker/wsl programs are machine-scoped — read from user settings
    // only, so a local folder's .vscode/settings.json cannot pick the ssh binary, a ProxyCommand/LocalCommand option, the server bundle or the
    // docker CLI that "Connect to Host…" runs. The language host is an application setting (one per CsCode, applies on restart).
    public static IReadOnlyList<ConfigurationProperty> Properties() =>
    [
        M(SshPath, "ssh", "The ssh client to run (resolved on PATH when not a path)."),
        M(ConfigFile, "", "ssh configuration file (ssh -F). Empty: ssh's own defaults (~/.ssh/config). Also the file 'Connect to Host…' lists hosts from."),
        M(Options, Array.Empty<string>(), "Extra ssh options, each 'Key=Value' (passed as -o)."),
        M(ConnectTimeout, 30, "Seconds ssh waits for the TCP connection (ConnectTimeout)."),
        M(ServerInstallPath, "", "Where the server is installed on the remote. Empty: ~/.cscode-server."),
        M(ServerDataPath, "", "The remote server's data directory (its settings mirror, state, logs). Empty: <install path>/data."),
        M(ServerBundles, "", "Directory with the server bundles to upload (manifest.json from tools/publish-server.sh). Empty: next to the program."),
        M(ReconnectAttempts, 8, "Reconnect attempts after a dropped connection (exponential backoff with jitter, up to 30 s apart)."),
        M(UseAskpass, true, "Show ssh's passphrase/password/host-key prompts in CsCode (SSH_ASKPASS)."),
        M(ReconnectGracePeriod, 10800, "Seconds the remote server keeps running after the connection to it was lost, so a reconnect (or a restart after a crash) finds terminals, debug sessions, tasks and language servers as they were. 0: the server ends with the connection."),
        M(Hosts, new Dictionary<string, object>(), "Per-host overrides of the remote.ssh.* settings, keyed by host as typed (e.g. {\"box\": {\"serverInstallPath\": \"/opt/cscode\"}})."),
        M(DockerPath, "docker", "The docker CLI used for docker://container remotes (Remote: Connect to Host… accepts docker://container)."),
        M(WslPath, "wsl.exe", "wsl.exe used for wsl://distro remotes (Windows; WSL distributions run the Linux server)."),
        M(LimaPath, "limactl", "The limactl program used for lima://instance remotes (Remote: Connect to Lima VM… lists the instances 'limactl list' knows)."),
        .. NixProperties(),
        P(LanguageHost, "inProcess", "Where language servers' clients run: 'inProcess' or 'process' (a separate local cscode-server: own heap, GC and CPU scheduling). Applies on restart.")
            with { Scope = ConfigurationScope.Application },
    ];

    /// <summary>The <c>remote.nix.*</c> settings (machine-scoped: read on the host the terminals and servers run on; the host registers them too).</summary>
    public static IReadOnlyList<ConfigurationProperty> NixProperties() =>
    [
        M(NixDevelop, "off", "'auto': when the folder has a flake.nix, run terminals, language servers, tasks and debuggers inside its `nix develop` environment (resolved when the folder opens or flake.nix / flake.lock change). 'off': the plain environment."),
        M(NixFlake, "", "The flake whose dev shell to enter: empty is the workspace folder; '#name' picks that devShell of the folder's flake; anything else (e.g. 'github:org/repo#dev') is passed to `nix develop` as it is."),
        M(NixTimeout, 600, "Seconds to wait for `nix develop` (the first evaluation may build or download); then the plain environment is used."),
    ];

    private static ConfigurationProperty P(string key, object value, string description) =>
        new(key, JsonSerializer.SerializeToElement(value), description);

    private static ConfigurationProperty M(string key, object value, string description) =>
        new(key, JsonSerializer.SerializeToElement(value), description + " User settings only.") { Scope = ConfigurationScope.Machine };

    /// <summary>A <c>remote.ssh.*</c> value for <paramref name="host"/>: its <see cref="Hosts"/> override when present, else the plain setting.</summary>
    public static T For<T>(IConfigurationService? config, string host, string key, T fallback)
    {
        if (config is null) return fallback;
        var leaf = key["remote.ssh.".Length..];
        if (config.GetRaw(Hosts) is { ValueKind: JsonValueKind.Object } hosts && hosts.TryGetProperty(host, out var entry)
            && entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(leaf, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            try { return value.Deserialize<T>(CoreJson.Options) ?? fallback; }
            catch (JsonException) { }
        }
        return config.Get(key, fallback);
    }
}
