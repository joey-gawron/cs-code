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
    /// <summary><c>inProcess</c> (default) or <c>process</c>: run the language client in a separate local cscode-server process (own heap/GC/CPU scheduling). Applies on restart.</summary>
    public const string LanguageHost = "cscode.languageHost";

    public static IReadOnlyList<ConfigurationProperty> Properties() =>
    [
        P(SshPath, "ssh", "The ssh client to run (resolved on PATH when not a path)."),
        P(ConfigFile, "", "ssh configuration file (ssh -F). Empty: ssh's own defaults (~/.ssh/config). Also the file 'Connect to Host…' lists hosts from."),
        P(Options, Array.Empty<string>(), "Extra ssh options, each 'Key=Value' (passed as -o)."),
        P(ConnectTimeout, 30, "Seconds ssh waits for the TCP connection (ConnectTimeout)."),
        P(ServerInstallPath, "", "Where the server is installed on the remote. Empty: ~/.cscode-server."),
        P(ServerDataPath, "", "The remote server's data directory (its settings mirror, state, logs). Empty: <install path>/data."),
        P(ServerBundles, "", "Directory with the server bundles to upload (manifest.json from tools/publish-server.sh). Empty: next to the program."),
        P(ReconnectAttempts, 8, "Reconnect attempts after a dropped connection (exponential backoff with jitter, up to 30 s apart)."),
        P(UseAskpass, true, "Show ssh's passphrase/password/host-key prompts in CsCode (SSH_ASKPASS)."),
        P(ReconnectGracePeriod, 10800, "Seconds the remote server keeps running after the connection to it was lost, so a reconnect (or a restart after a crash) finds terminals, debug sessions, tasks and language servers as they were. 0: the server ends with the connection."),
        P(Hosts, new Dictionary<string, object>(), "Per-host overrides of the remote.ssh.* settings, keyed by host as typed (e.g. {\"box\": {\"serverInstallPath\": \"/opt/cscode\"}})."),
        P(DockerPath, "docker", "The docker CLI used for docker://container remotes (Remote: Connect to Host… accepts docker://container)."),
        P(WslPath, "wsl.exe", "wsl.exe used for wsl://distro remotes (Windows; WSL distributions run the Linux server)."),
        P(LanguageHost, "inProcess", "Where language servers' clients run: 'inProcess' or 'process' (a separate local cscode-server: own heap, GC and CPU scheduling). Applies on restart."),
    ];

    private static ConfigurationProperty P(string key, object value, string description) =>
        new(key, JsonSerializer.SerializeToElement(value), description);

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
