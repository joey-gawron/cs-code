using System.Globalization;
using CsCode.Core.Platform;
using CsCode.Platform;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Background;

/// <summary>Command ids of File ▸ Sessions (registered only in session processes; the menu items stay disabled elsewhere).</summary>
public static class SessionCommandIds
{
    public const string SaveSessionAs = "cscode.sessions.saveAs";
    public const string OpenSession = "cscode.sessions.open";
    public const string SwitchSession = "workbench.action.switchSession";
    public const string RenameSession = "cscode.sessions.rename";
    public const string DeleteSession = "cscode.sessions.delete";
    public const string CloseWindow = "cscode.sessions.closeWindow";
    public const string QuitSession = "cscode.sessions.quit";
    public const string ExitAll = "cscode.sessions.exitAll";
    /// <summary>Toggles "start CsCode in the background at login" (asks first; <see cref="AutostartEntry"/>).</summary>
    public const string StartAtLogin = "cscode.sessions.startAtLogin";
}

public sealed partial class SessionWindowController
{
    public const string SwitchSessionCommand = SessionCommandIds.SwitchSession;

    private void RegisterCommands()
    {
        var commands = _sp.GetRequiredService<ICommandService>();
        void Add(string id, string title, Func<Task> run) =>
            _subs.Add(commands.Register(id, async _ => { await run().ConfigureAwait(true); return null; }, new CommandMetadata(id, title, "Session")));

        Add(SessionCommandIds.SaveSessionAs, "Save Session As...", () => NameSessionAsync(rename: false));
        Add(SessionCommandIds.RenameSession, "Rename Session...", () => NameSessionAsync(rename: true));
        Add(SessionCommandIds.OpenSession, "Open Session...", () => PickAndOpenAsync(switching: false));
        Add(SessionCommandIds.SwitchSession, "Switch Session...", () => PickAndOpenAsync(switching: true));
        Add(SessionCommandIds.DeleteSession, "Delete Session...", DeleteSessionAsync);
        Add(SessionCommandIds.CloseWindow, "Close Window (Keep Running)", () => { Hide(); return Task.CompletedTask; });
        Add(SessionCommandIds.QuitSession, "Quit Session", async () => await QuitAsync(force: false).ConfigureAwait(true));
        Add(SessionCommandIds.ExitAll, "Exit All", ExitAllAsync);
        Add(SessionCommandIds.StartAtLogin, "Start CsCode in the Background at Login...", ToggleStartAtLoginAsync);
        // In a session process "Exit" ends CsCode as a whole, like closing the last window of a multi-window editor (with hot exit, nothing is asked).
        Add(KnownCommands.Quit, "Exit", ExitAllAsync);
    }

    private IWindowService Windows => _sp.GetRequiredService<IWindowService>();

    private SessionLauncher NewLauncher(TextWriter errors) => new(_host.Runtime, _host.Store, LaunchSettings.Read(new AppPaths().ConfigDir),
        ProcessHostSpawner.ForCurrentProcess(_host.IsHeadlessTest ? ["--headless-test"] : null), TextWriter.Null, errors);

    /// <summary>Save Session As (keeps the old session as it was saved) and Rename (the old one is gone). Both make this window's session the new name.</summary>
    private async Task NameSessionAsync(bool rename)
    {
        if (_host.SessionName is not { } current) return;
        var taken = await _host.Lane.RunAsync(() => _host.Store.List().Select(s => s.Name).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(true);
        var name = await Windows.ShowInputBoxAsync(new InputBoxOptions(
            Title: rename ? "Rename Session" : "Save Session As",
            Prompt: "Session name (letters, digits, spaces and - _ . ( ) +)",
            Value: _host.IsUnnamed ? "" : current,
            Validate: n => SessionNames.Validate(n.Trim()) ?? (n.Trim() != current && taken.Contains(n.Trim()) ? $"A session named '{n.Trim()}' already exists." : null))).ConfigureAwait(true);
        if (name?.Trim() is not { Length: > 0 } chosen) return;
        if (chosen == current && !_host.IsUnnamed)
        {
            await SaveNowAsync().ConfigureAwait(true);
            return;
        }
        var wasUnnamed = _host.IsUnnamed;
        var took = await _host.Lane.RunAsync(() => _host.TryTakeSession(chosen, unnamed: false)).ConfigureAwait(true);
        if (!took)
        {
            Notify(MessageSeverity.Error, $"Session '{chosen}' is open in another CsCode window.");
            return;
        }
        _backedUp.Clear(); // every backup is written again under the new name
        UpdateSessionIdentity();
        await PersistAsync().ConfigureAwait(true);
        if (rename || wasUnnamed) await _host.Lane.RunAsync(() => _host.Store.Delete(current)).ConfigureAwait(true);
        _host.MarkShown();
    }

    private sealed record SessionChoice(string? Name, bool NewWindow);

    private async Task PickAndOpenAsync(bool switching)
    {
        var self = _host.SessionName;
        var saved = await _host.Lane.RunAsync(_host.Store.List).ConfigureAwait(true);
        var running = await ResidentDiscovery.DiscoverAsync(_host.Runtime, IpcClient.DefaultConnectTimeout, excludePid: Environment.ProcessId).ConfigureAwait(true);
        var items = new List<QuickPickItem> { new("$(add) New Window", "a new unnamed session", Tag: new SessionChoice(null, true)) };
        foreach (var s in saved.Where(s => s.Name != self))
        {
            var proc = running.FirstOrDefault(p => p.Session == s.Name);
            var folder = s.Folders.FirstOrDefault() is { } f && Uri.TryCreate(f, UriKind.Absolute, out var u) ? (u.IsFile ? u.LocalPath : u.ToString()) : null;
            var used = s.LastUsedUtc == DateTimeOffset.MinValue ? null : "last used " + s.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            items.Add(new QuickPickItem(s.Name, proc is null ? (s.IsUnnamed ? "unnamed" : null) : "running (" + proc.State + ")",
                string.Join(" · ", new[] { folder, used }.OfType<string>()), Tag: new SessionChoice(s.Name, false)));
        }
        var pick = await Windows.ShowQuickPickAsync(items, new QuickPickOptions(switching ? "Switch Session" : "Open Session", "Session name", MatchOnDetail: true)).ConfigureAwait(true);
        if (pick?.Tag is not SessionChoice choice) return;
        var errors = new StringWriter();
        var request = new LaunchRequest(LaunchKind.Open) { Session = choice.Name, NewWindow = choice.NewWindow };
        var outcome = await Task.Run(() => NewLauncher(errors).RunAsync(request)).ConfigureAwait(true);
        if (outcome.ExitCode != 0) Notify(MessageSeverity.Error, "Could not open the session: " + errors.ToString().Trim());
        else if (switching) Hide();
    }

    private async Task DeleteSessionAsync()
    {
        var self = _host.SessionName;
        var candidates = await _host.Lane.RunAsync(() => _host.Store.List()
            .Where(s => s.Name != self && !FileLock.IsHeldElsewhere(_host.Runtime.SessionLockPath(s.Name))).ToArray()).ConfigureAwait(true);
        if (candidates.Length == 0)
        {
            Notify(MessageSeverity.Info, "There is no saved session to delete (running sessions must be quit first).");
            return;
        }
        var pick = await Windows.ShowQuickPickAsync(candidates.Select(s => new QuickPickItem(s.Name, s.IsUnnamed ? "unnamed" : null, Tag: s.Name)).ToArray(),
            new QuickPickOptions("Delete Session", "Session to delete")).ConfigureAwait(true);
        if (pick?.Tag is not string name) return;
        var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning, $"Delete session '{name}'? Its backups of unsaved changes are deleted too.",
            [new MessageAction("Delete"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
        if (answer != "Delete") return;
        var deleted = await _host.Lane.RunAsync(() =>
        {
            using var held = FileLock.TryAcquire(_host.Runtime.SessionLockPath(name));
            if (held is null) return false; // it was started meanwhile
            _host.Store.Delete(name);
            return true;
        }).ConfigureAwait(true);
        if (!deleted) Notify(MessageSeverity.Error, $"Session '{name}' is running; quit it first.");
    }

    /// <summary>Installs or removes the login autostart entry, after the user confirmed exactly what is written where.</summary>
    private async Task ToggleStartAtLoginAsync()
    {
        if (AutostartEntry.PathFor() is not { } path)
        {
            Notify(MessageSeverity.Info, "On this system, run the script in tools/autostart/ to start CsCode at login (docs/SESSIONS.md).");
            return;
        }
        var (installed, executable) = await _host.Lane.RunAsync(() => (AutostartEntry.IsInstalled(), AutostartEntry.DefaultExecutable())).ConfigureAwait(true);
        var question = installed
            ? $"CsCode starts in the background at login ({path}). Stop that?"
            : $"Start CsCode in the background at login? This writes {path}, which runs \"{executable} --background\" when you log in (the warm spare; no window opens).";
        var yes = installed ? "Stop Starting at Login" : "Start at Login";
        var answer = await Windows.ShowMessageAsync(MessageSeverity.Info, question, [new MessageAction(yes), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
        if (answer != yes) return;
        try
        {
            await _host.Lane.RunAsync(() => { if (installed) AutostartEntry.Uninstall(); else AutostartEntry.Install(executable); }).ConfigureAwait(true);
            Notify(MessageSeverity.Info, installed ? "CsCode no longer starts at login." : "CsCode will start in the background at login.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(MessageSeverity.Error, "Could not change the login autostart entry: " + ex.Message);
        }
    }

    /// <summary>Exit All: every other CsCode process quits (hot exit), then this one.</summary>
    private async Task ExitAllAsync()
    {
        var others = await ResidentDiscovery.DiscoverAsync(_host.Runtime, IpcClient.DefaultConnectTimeout, excludePid: Environment.ProcessId).ConfigureAwait(true);
        var refused = new List<string>();
        await Task.WhenAll(others.Where(p => p.Responsive).Select(async p =>
        {
            try
            {
                var reply = await IpcClient.RequestAsync(p.Endpoint, new IpcMessage { Type = IpcVerbs.Quit, Force = false }, IpcClient.DefaultConnectTimeout, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (reply.Ok != true) lock (refused) refused.Add($"{p.Session ?? "spare"}: {reply.Error}");
            }
            catch (IpcException) { /* gone already */ }
        })).ConfigureAwait(true);
        if (refused.Count > 0) Notify(MessageSeverity.Warning, "Some sessions did not quit: " + string.Join("; ", refused));
        await QuitAsync(force: false).ConfigureAwait(true);
    }
}
