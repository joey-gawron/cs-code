using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Shell;

/// <summary>
/// The Remote module's shell glue, written against Core services only (commands, status bar, window, workspace): the commands
/// (<c>Remote: Connect to Host…</c>, <c>Open Remote Folder…</c>, disconnect, reconnect, reinstall server, show log), the remote indicator at the
/// left of the status bar (host + state; click to connect) and the connection-progress notification. <see cref="Start"/> is called once by the shell.
/// </summary>
public sealed class RemoteShellContributions(IServiceProvider sp) : IDisposable
{
    public const string Connect = "remote.connect";
    public const string OpenFolder = "remote.openFolder";
    public const string Disconnect = "remote.disconnect";
    public const string Reconnect = "remote.reconnect";
    public const string Reinstall = "remote.reinstallServer";
    public const string ShowLog = "remote.showLog";
    public const string IndicatorId = "remote.indicator";
    private const string AddHostLabel = "+ Add New SSH Host…";

    private readonly DisposableStore _store = new();
    private IDisposable? _indicator;
    private readonly ILogger _log = sp.GetService<ILoggerFactory>()?.CreateLogger("CsCode.Remote.Shell") ?? NullLogger.Instance;

    private RemoteConnectionManager Manager => sp.GetRequiredService<RemoteConnectionManager>();
    private IWindowService? Window => sp.GetService<IWindowService>();

    /// <summary>Registers commands and the status-bar indicator. Returns the registration (dispose to remove everything).</summary>
    public IDisposable Start()
    {
        if (sp.GetService<ICommandService>() is { } commands)
        {
            _store.Add(commands.Register(Connect, inv => Run(ConnectToHostAsync(inv.Arg<string>(0))), new CommandMetadata(Connect, "Connect to Host...", "Remote", "remote")));
            _store.Add(commands.Register(OpenFolder, inv => Run(OpenRemoteFolderAsync(inv.Arg<string>(0))), new CommandMetadata(OpenFolder, "Open Remote Folder...", "Remote", "remote")));
            _store.Add(commands.Register(Disconnect, _ => Run(DisconnectAsync()), new CommandMetadata(Disconnect, "Close Remote Connection", "Remote")));
            _store.Add(commands.Register(Reconnect, _ => Run(Manager.ReconnectAsync().AsTask()), new CommandMetadata(Reconnect, "Reconnect", "Remote")));
            _store.Add(commands.Register(Reinstall, _ => Run(Manager.ReinstallServerAsync().AsTask()), new CommandMetadata(Reinstall, "Reinstall Server and Reconnect", "Remote")));
            _store.Add(commands.Register(ShowLog, _ => Run(ShowLogAsync()), new CommandMetadata(ShowLog, "Show Log", "Remote")));
            if (sp.GetService<IConfigurationService>() is { } config) _store.Add(config.RegisterProperties(RemoteSettings.Properties()));
        }
        // R3: sessions of a remote workspace start their adapter on the server (asked before the built-in, local factories).
        if (sp.GetService<CsCode.Core.Debugging.IDebugService>() is { } debug && sp.GetService<Bridge.RemoteDebugging>() is { } remoteDebug)
            _store.Add(remoteDebug.RegisterWith(debug));
        if (sp.GetService<IStatusBarService>() is { } statusBar)
        {
            // Resolving the manager here is cheap (everything it needs is resolved lazily) and makes the indicator follow it from the start.
            var manager = Manager;
            UpdateIndicator(statusBar, manager.Status);
            _store.Add(manager.StatusChanged.Subscribe(s => UpdateIndicator(statusBar, s)));
            _store.Add(Disposable.Create(() => Interlocked.Exchange(ref _indicator, null)?.Dispose()));
        }
        return this;
    }

    /// <summary>True for the folder URI of a remote workspace (<c>ssh://host/path</c>): the session restore reconnects instead of opening it directly.</summary>
    public static bool IsRemoteFolder(Uri uri) => uri.IsAbsoluteUri && RemoteSchemes.IsRemote(uri.Scheme);

    private static async ValueTask<object?> Run(Task work)
    {
        await work.ConfigureAwait(false);
        return null;
    }

    /// <summary>The badge's theme colours (VS Code's): the indicator is always drawn as a coloured badge at the left of the status bar.</summary>
    public const string BadgeBackground = "statusBarItem.remoteBackground";
    public const string BadgeForeground = "statusBarItem.remoteForeground";

    internal static StatusBarItemModel Indicator(RemoteConnectionStatus s) => (s.State switch
    {
        RemoteConnectionState.Connected => new StatusBarItemModel(IndicatorId, $"$(remote) {Label(s)}", $"Connected to {s.Host}. Click for remote commands.", Connect, StatusBarAlignment.Left, 10_000),
        RemoteConnectionState.Connecting or RemoteConnectionState.Bootstrapping =>
            new(IndicatorId, $"$(sync~spin) {Label(s)}", s.Message ?? "Connecting…", ShowLog, StatusBarAlignment.Left, 10_000),
        RemoteConnectionState.Reconnecting => new(IndicatorId, $"$(sync~spin) {Label(s)} (reconnecting)", s.Message ?? "Reconnecting…", ShowLog, StatusBarAlignment.Left, 10_000),
        RemoteConnectionState.Failed => new(IndicatorId, $"$(error) {Label(s)} (disconnected)", s.Message ?? "Connection failed", Reconnect, StatusBarAlignment.Left, 10_000),
        _ => new(IndicatorId, "$(remote)", "Open a remote window (Connect to Host…)", Connect, StatusBarAlignment.Left, 10_000),
    }) with { ColorKey = BadgeForeground, BackgroundColorKey = BadgeBackground };

    /// <summary>"SSH: host", "Docker: container", "WSL: distro" — by the connection's URI scheme.</summary>
    private static string Label(RemoteConnectionStatus s) => s.Authority?.Scheme switch
    {
        RemoteSchemes.Docker => "Docker: " + s.Host,
        RemoteSchemes.Wsl => "WSL: " + s.Host,
        _ => "SSH: " + s.Host,
    };

    // Set updates the entry by id; only the newest token is kept (disposing an older one would remove the entry).
    private void UpdateIndicator(IStatusBarService statusBar, RemoteConnectionStatus s) => Interlocked.Exchange(ref _indicator, statusBar.Set(Indicator(s)));

    // ------------------------------------------------------------------------------------------------------------------- commands

    /// <summary>Lists ssh_config hosts (+ "Add New SSH Host…"), connects with a progress notification, then offers to open a remote folder.</summary>
    public async Task ConnectToHostAsync(string? host = null)
    {
        await new PoolHop(); // commands run on the UI thread; listing hosts reads the ssh configuration file
        host ??= await PickHostAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(host)) return;
        if (!await ConnectWithProgressAsync(host).ConfigureAwait(false)) return;
        await OpenRemoteFolderAsync(null).ConfigureAwait(false);
    }

    /// <summary>Opens a remote folder: <paramref name="target"/> is an <c>ssh://</c> URI (command line), or the user is asked (connecting first if needed).</summary>
    public async Task OpenRemoteFolderAsync(string? target)
    {
        await new PoolHop();
        var manager = Manager;
        string? path = null;
        if (target is { Length: > 0 } && target.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(target);
            if (!await ConnectWithProgressAsync(RemoteAuthority.Parse(target).Destination).ConfigureAwait(false)) return;
            path = SshUriMapper.PathOf(uri, false);
        }
        else if (!manager.Status.IsConnected)
        {
            await ConnectToHostAsync().ConfigureAwait(false);
            return;
        }
        if (path is null)
        {
            var home = manager.Session?.Server.Home ?? "/";
            path = Window is { } w
                ? await w.ShowInputBoxAsync(new InputBoxOptions("Open Remote Folder", $"Folder on {manager.HostName}", "/path/to/folder", home)).ConfigureAwait(false)
                : home;
            if (string.IsNullOrWhiteSpace(path)) return;
        }
        var folder = manager.ToUri(path.EndsWith('/') ? path : path + "/");
        if (folder is null || sp.GetService<IWorkspaceService>() is not { } workspace) return;
        try { await workspace.OpenFolderAsync(folder).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Window is { } w) await w.ShowMessageAsync(MessageSeverity.Error, $"Cannot open {path} on {manager.HostName}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task<string?> PickHostAsync()
    {
        if (Window is not { } w) return null;
        var hosts = ListConfiguredHosts();
        var items = hosts.Select(h => new QuickPickItem(h, "ssh_config")).Append(new QuickPickItem(AddHostLabel)).ToArray();
        var picked = await w.ShowQuickPickAsync(items, new QuickPickOptions("Connect to Host", "Select a configured SSH host or enter user@host")).ConfigureAwait(false);
        if (picked is null) return null;
        if (picked.Label != AddHostLabel) return picked.Label;
        return await w.ShowInputBoxAsync(new InputBoxOptions("Add New SSH Host", "user@host[:port]", "me@example.com",
            Validate: v => { try { RemoteAuthority.Parse(v); return null; } catch (FormatException ex) { return ex.Message; } catch (ArgumentException ex) { return ex.Message; } })).ConfigureAwait(false);
    }

    /// <summary>
    /// Hosts of the ssh configuration the connection uses: <c>remote.ssh.configFile</c> when set, else the user's ~/.ssh/config (read only here, when
    /// the user asks to connect; never written).
    /// </summary>
    internal IReadOnlyList<string> ListConfiguredHosts()
    {
        var configured = sp.GetService<IConfigurationService>()?.Get(RemoteSettings.ConfigFile, "");
        var file = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config")
            : configured;
        try { return SshConfigHosts.List(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task<bool> ConnectWithProgressAsync(string host)
    {
        var manager = Manager;
        try
        {
            if (Window is { } w)
                await w.WithProgressAsync($"Connecting to {host}", true, async (progress, ct) =>
                {
                    using var sub = manager.StatusChanged.Subscribe(s => progress.Report(s.Message));
                    await manager.ConnectAsync(host, ct).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            else await manager.ConnectAsync(host).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            _log.LogWarning("Connecting to {Host} failed: {Error}", host, ex.Message);
            if (Window is { } w)
            {
                var choice = await w.ShowMessageAsync(MessageSeverity.Error, $"Could not connect to {host}: {ex.Message}",
                    [new MessageAction("Retry"), new MessageAction("Show Log"), new MessageAction("Close", IsCloseAffordance: true)]).ConfigureAwait(false);
                if (choice == "Retry") return await ConnectWithProgressAsync(host).ConfigureAwait(false);
                if (choice == "Show Log") await ShowLogAsync().ConfigureAwait(false);
            }
            return false;
        }
    }

    private async Task DisconnectAsync()
    {
        var manager = Manager;
        // The remote folder stops being reachable: close it first so nothing keeps calling into a closed connection.
        if (sp.GetService<IWorkspaceService>() is { } ws && ws.Folders.Any(f => f.Scheme == SshUriMapper.Scheme))
            await ws.CloseFolderAsync().ConfigureAwait(false);
        await manager.DisconnectAsync().ConfigureAwait(false);
    }

    private async Task ShowLogAsync()
    {
        sp.GetService<IOutputService>()?.GetOrCreate(RemoteConnectionManager.OutputChannel);
        if (sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync("output").ConfigureAwait(false);
    }

    public void Dispose() => _store.Dispose();
}
