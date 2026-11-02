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
    /// <summary>File &gt; New Window (VS Code's id, Ctrl+Shift+N / ⇧⌘N): a new unnamed session in a window of its own, as <c>cscode -n</c>.</summary>
    public const string NewWindow = "workbench.action.newWindow";
}

public sealed partial class SessionWindowController
{
    public const string SwitchSessionCommand = SessionCommandIds.SwitchSession;

    private void RegisterCommands()
    {
        var commands = _sp.GetRequiredService<ICommandService>();
        void Add(string id, string title, Func<Task> run) =>
            _subs.Add(commands.Register(id, async _ => { await run().ConfigureAwait(true); return null; }, new CommandMetadata(id, title, "Session")));

        // These change or hide this session: run while a quit's close is held by a module's question, they decline it (the user works on), as
        // they could not go ahead in a window that is about to close (Hide does nothing while a quit is under way, a rename writes the session).
        void AddWorkingOn(string id, string title, Func<Task> run) => Add(id, title, () =>
        {
            if (_quitHeld && !_quitCommitted && !_disposed) DeclineHeldQuit();
            return run();
        });

        AddWorkingOn(SessionCommandIds.SaveSessionAs, "Save Session As...", () => NameSessionAsync(rename: false));
        AddWorkingOn(SessionCommandIds.RenameSession, "Rename Session...", () => NameSessionAsync(rename: true));
        // A session name as argument (the Welcome page's Sessions list, Quick Launch's session: mode) opens that session; none shows the picker.
        _subs.Add(commands.Register(SessionCommandIds.OpenSession, async inv =>
        {
            if (inv.Arg<string>(0) is { Length: > 0 } name) await OpenNamedAsync(name).ConfigureAwait(true);
            else await PickAndOpenAsync(switching: false).ConfigureAwait(true);
            return null;
        }, new CommandMetadata(SessionCommandIds.OpenSession, "Open Session...", "Session")));
        AddWorkingOn(SessionCommandIds.SwitchSession, "Switch Session...", () => PickAndOpenAsync(switching: true));
        Add(SessionCommandIds.DeleteSession, "Delete Session...", DeleteSessionAsync);
        // Absolute paths as arguments (the New Project page: the new project's folder, then its main file) open in the new session's window.
        _subs.Add(commands.Register(SessionCommandIds.NewWindow, async inv =>
        {
            await LaunchAsync(new SessionChoice(null, true), switching: false, [.. inv.Args.OfType<string>().Where(Path.IsPathFullyQualified)]).ConfigureAwait(true);
            return null;
        }, new CommandMetadata(SessionCommandIds.NewWindow, "New Window", "Session")));
        AddWorkingOn(SessionCommandIds.CloseWindow, "Close Window (Keep Running)", () => { Hide(); return Task.CompletedTask; });
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
        bool took;
        try { took = await _host.Lane.RunAsync(() => _host.TryTakeSession(chosen, unnamed: false)).ConfigureAwait(true); }
        catch (IOException ex)
        {
            Notify(MessageSeverity.Error, $"Could not take session '{chosen}': {ex.Message}");
            return;
        }
        if (!took)
        {
            Notify(MessageSeverity.Error, $"Session '{chosen}' is open in another CsCode window.");
            return;
        }
        _backedUp.Clear(); // every backup is written again under the new name
        // A session a newer CsCode wrote stays exactly as it is (its file and backups are that version's): the new name is a session of this
        // version, written from now on, and Rename keeps the old one instead of deleting what this version cannot read.
        var keptNewer = _readOnly;
        lock (_writeGate) _readOnly = false;
        UpdateSessionIdentity();
        var written = await PersistAsync().ConfigureAwait(true);
        UpdateNeedsPrompt();
        if (keptNewer)
        {
            if (rename) Notify(MessageSeverity.Info, $"Session '{current}' was saved by a newer CsCode, so it was kept as it is; this window is now session '{chosen}'.");
        }
        else if (rename || wasUnnamed)
        {
            // Its backups go with it only when every unsaved change is backed up under the new name.
            if (written.Skipped.Count == 0) await _host.Lane.RunAsync(() => _host.Store.Delete(current)).ConfigureAwait(true);
            else if (rename) Notify(MessageSeverity.Warning, $"Session '{current}' was kept: some unsaved changes exceed the backup limit and were not backed up under '{chosen}'.");
        }
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
        await LaunchAsync(choice, switching).ConfigureAwait(true);
    }

    /// <summary>Opens the named saved session (its window is shown, started when it is not running); this window's own session is already here.</summary>
    private async Task OpenNamedAsync(string name)
    {
        if (name == _host.SessionName || !SessionNames.IsValid(name)) return;
        await LaunchAsync(new SessionChoice(name, false), switching: false).ConfigureAwait(true);
    }

    private async Task LaunchAsync(SessionChoice choice, bool switching, IReadOnlyList<string>? paths = null)
    {
        var errors = new StringWriter();
        var outcome = await Task.Run(() =>
        {
            var targets = (paths ?? []).Select(p => new LaunchTarget(p, Directory.Exists(p))).ToArray(); // off the UI thread: it stats each path
            var request = new LaunchRequest(LaunchKind.Open) { Session = choice.Name, NewWindow = choice.NewWindow, Targets = targets };
            return NewLauncher(errors).RunAsync(request);
        }).ConfigureAwait(true);
        if (outcome.ExitCode != 0) Notify(MessageSeverity.Error, "Could not open the session: " + errors.ToString().Trim());
        else if (switching) Hide();
    }

    private async Task DeleteSessionAsync()
    {
        var self = _host.SessionName;
        var candidates = await _host.Lane.RunAsync(() => _host.Store.List()
            .Where(s => s.Name != self && !SessionLock.IsHeldElsewhere(_host.Store, _host.Runtime, s.Name)).ToArray()).ConfigureAwait(true);
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
        string? refused;
        try
        {
            refused = await _host.Lane.RunAsync<string?>(() =>
            {
                using var held = SessionLock.TryAcquire(_host.Store, _host.Runtime, name);
                if (held is null) return $"Session '{name}' is running; quit it first."; // it was started meanwhile
                _host.Store.Delete(name);
                return null;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = $"Could not delete session '{name}': {ex.Message}"; }
        if (refused is not null) Notify(MessageSeverity.Error, refused);
    }

    /// <summary>Installs or removes the login autostart entry, after the user confirmed exactly what is written where.</summary>
    private async Task ToggleStartAtLoginAsync()
    {
        if (AutostartEntry.PathFor() is not { } path)
        {
            Notify(MessageSeverity.Info, "On this system, run the script in tools/autostart/ to start CsCode at login (docs/SESSIONS.md).");
            return;
        }
        var (installed, executable, settings) = await _host.Lane.RunAsync(() =>
            (AutostartEntry.IsInstalled(), AutostartEntry.DefaultExecutable(), LaunchSettings.Read(new AppPaths().ConfigDir))).ConfigureAwait(true);
        var question = installed
            ? $"CsCode starts in the background at login ({path}). Stop that?"
            : $"Start CsCode in the background at login? This writes {path}, which runs \"{executable} --background\" when you log in. With the current settings that starts {AutostartEntry.WhatStartsAtLogin(settings)}.";
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
