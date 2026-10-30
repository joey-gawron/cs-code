using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using CsCode.Ui.Threading;

namespace CsCode.App.Background;

/// <summary>One entry of the tray menu (a pure model, so the menu's content is unit-tested without a tray host).</summary>
public sealed record TrayMenuEntry(string Header, LaunchRequest? Launch = null, IReadOnlyList<TrayMenuEntry>? Children = null, bool IsSeparator = false, bool Enabled = true)
{
    public static readonly TrayMenuEntry Separator = new("-", IsSeparator: true);
}

public static class TrayMenuModel
{
    /// <summary>Running sessions (state, memory) first, then Open Session ▸ saved ones, New Window, Quit All.</summary>
    public static IReadOnlyList<TrayMenuEntry> Build(IReadOnlyList<ResidentProcess> running, IReadOnlyList<SessionSummary> saved)
    {
        var entries = new List<TrayMenuEntry>();
        foreach (var p in running.Where(p => p.Responsive && p.Session is not null).OrderBy(p => p.Session, StringComparer.Ordinal))
        {
            var memory = p.WorkingSetBytes is { } ws ? $", {(ws / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB" : "";
            entries.Add(new TrayMenuEntry($"{p.Session} ({p.State}{memory})", new LaunchRequest(LaunchKind.Open) { Session = p.Session }));
        }
        if (entries.Count == 0) entries.Add(new TrayMenuEntry("No running sessions", Enabled: false));
        entries.Add(TrayMenuEntry.Separator);
        var savedOnly = saved.Where(s => !running.Any(p => p.Session == s.Name)).Take(20)
            .Select(s => new TrayMenuEntry(s.Name, new LaunchRequest(LaunchKind.Open) { Session = s.Name })).ToArray();
        entries.Add(new TrayMenuEntry("Open Session", Children: savedOnly, Enabled: savedOnly.Length > 0));
        entries.Add(new TrayMenuEntry("New Window", new LaunchRequest(LaunchKind.Open) { NewWindow = true }));
        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new TrayMenuEntry("Quit All", new LaunchRequest(LaunchKind.Quit) { All = true }));
        return entries;
    }
}

/// <summary>
/// The tray icon (<c>cscode.background.showTrayIcon</c>): shown by one CsCode process at a time, the holder of the runtime directory's tray lock
/// (so several sessions do not give several icons; when its process exits, the next process that is shown or hidden takes over). Clicking it shows
/// the last session; its menu lists the running sessions and is rebuilt (off the UI thread) each time it opens. Every action runs the launcher,
/// exactly as the command line would. Without a tray host (or on the headless test platform) there is no icon and everything works through the
/// command line and the desktop entry.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly BackgroundHost _host;
    private readonly IUiDispatcher _ui;
    private TrayIcon? _icon;
    private FileLock? _leadership;
    private int _disposed;

    private TrayController(BackgroundHost host, IUiDispatcher ui)
    {
        _host = host;
        _ui = ui;
    }

    /// <summary>Null when the tray is off (setting), on the headless test platform, or without an application.</summary>
    public static TrayController? TryCreate(BackgroundHost host, IConfigurationService? config, IUiDispatcher ui)
    {
        if (host.IsHeadlessTest || !(config?.Get(BackgroundSettings.ShowTrayIcon, true) ?? true) || Application.Current is null) return null;
        var tray = new TrayController(host, ui);
        _ = tray.TryLeadAsync();
        return tray;
    }

    /// <summary>Takes the tray lock (lane) and, when it got it, creates the icon (UI).</summary>
    internal async Task TryLeadAsync()
    {
        if (_leadership is not null || Volatile.Read(ref _disposed) != 0) return;
        var held = await _host.Lane.RunAsync(() => FileLock.TryAcquire(_host.Runtime.TrayLockPath)).ConfigureAwait(false);
        if (held is null) return;
        _leadership = held;
        _ui.Post(CreateIcon);
    }

    private void CreateIcon()
    {
        if (Volatile.Read(ref _disposed) != 0 || Application.Current is not { } app) return;
        try
        {
            var menu = new NativeMenu();
            menu.Opening += (_, _) => _ = RefreshAsync(menu);
            _icon = new TrayIcon { Icon = AppIcon.Create(32), ToolTipText = "CsCode", Menu = menu, IsVisible = true };
            _icon.Clicked += (_, _) => Launch(new LaunchRequest(LaunchKind.Open));
            TrayIcon.SetIcons(app, [_icon]);
            Fill(menu, TrayMenuModel.Build([], []));
            _ = RefreshAsync(menu);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            _icon = null; // no tray host on this desktop: the CLI and the desktop entry still work
        }
    }

    private async Task RefreshAsync(NativeMenu menu)
    {
        var running = await ResidentDiscovery.DiscoverAsync(_host.Runtime, IpcClient.DefaultConnectTimeout).ConfigureAwait(false);
        var saved = await _host.Lane.RunAsync(_host.Store.List).ConfigureAwait(false);
        var entries = TrayMenuModel.Build(running, saved);
        _ui.Post(() => Fill(menu, entries));
    }

    private void Fill(NativeMenu menu, IReadOnlyList<TrayMenuEntry> entries)
    {
        menu.Items.Clear();
        foreach (var e in entries) menu.Items.Add(ToItem(e));
    }

    private NativeMenuItemBase ToItem(TrayMenuEntry e)
    {
        if (e.IsSeparator) return new NativeMenuItemSeparator();
        var item = new NativeMenuItem(e.Header) { IsEnabled = e.Enabled };
        if (e.Children is { Count: > 0 } children)
        {
            var sub = new NativeMenu();
            foreach (var c in children) sub.Items.Add(ToItem(c));
            item.Menu = sub;
        }
        else if (e.Launch is { } request) item.Click += (_, _) => Launch(request);
        return item;
    }

    private void Launch(LaunchRequest request) => _ = Task.Run(async () =>
    {
        var launcher = new SessionLauncher(_host.Runtime, _host.Store, LaunchSettings.Read(new Platform.AppPaths().ConfigDir),
            ProcessHostSpawner.ForCurrentProcess(), TextWriter.Null, TextWriter.Null);
        try { await launcher.RunAsync(request).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* the tray never takes the process down */ }
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var icon = _icon;
        _icon = null;
        if (icon is not null) _ui.Post(() => { icon.IsVisible = false; icon.Dispose(); });
        _leadership?.Dispose();
    }
}
