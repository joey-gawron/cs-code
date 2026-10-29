using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using CsCode.App.Docking;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Background;

/// <summary>
/// The window side of a session process (UI-thread object): restores a session into the shell, captures it for autosave and hot exit, hides the
/// window instead of closing it (<c>cscode.background.keepRunning</c>), shows it again on request, and applies the hidden-session resource policy
/// (views disposed and memory trimmed after <c>trimAfterSeconds</c>, language servers stopped after <c>idleServerTimeoutMinutes</c>).
/// <para>Threading: capture runs on the UI thread and only takes immutable snapshots (document text only for dirty documents whose version
/// changed since their last backup); JSON and every file write run on the session lane; IPC requests arrive on pool threads and hop here.</para>
/// </summary>
public sealed partial class SessionWindowController : IDisposable
{
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(1.5);

    private readonly BackgroundHost _host;
    private readonly MainWindow _window;
    private readonly IServiceProvider _sp;
    private readonly ShellController _shell;
    private readonly IUiDispatcher _ui;
    private readonly IConfigurationService? _config;
    private readonly IWorkspaceService? _workspace;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<EditorTab, DisposableStore> _tabSubs = new();
    private readonly HiddenSessionPolicy _policy;
    private readonly DebouncedSaver _autosave;
    private readonly ConcurrentDictionary<string, int> _backedUp = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(EditorTab Tab, PreparedBackup Backup)> _pendingConflicts = new();
    private readonly object _writeGate = new();
    private IReadOnlyList<string> _stoppedServers = [];
    private SessionWindow? _normalBounds;
    private Captured? _lastCaptured;
    private long _captureSeq, _writtenSeq, _lastTrimWorkingSet;
    private int _trims;
    private bool _quitting, _disposed, _readOnly, _overLimitWarned, _wasResident = true, _subscribed;
    private IDisposable? _statusItem;
    private readonly TrayController? _tray;

    internal SessionWindowController(BackgroundHost host, MainWindow window, IServiceProvider services)
    {
        _host = host;
        _window = window;
        _sp = services;
        _shell = services.GetRequiredService<ShellController>();
        _ui = services.GetRequiredService<IUiDispatcher>();
        _config = services.GetService<IConfigurationService>();
        _workspace = services.GetService<IWorkspaceService>();
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _policy = new HiddenSessionPolicy(time, () => BackgroundSettings.HiddenPolicy(_config), TrimFromTimer, StopServersFromTimer, IsDebugging);
        _autosave = new DebouncedSaver(time, AutosaveDelay, _ => SaveSnapshotAsync());
        host.IsDebugging = IsDebugging;
        window.CloseInterceptor = OnCloseRequested;
        window.Closed += OnWindowClosed;

        if (_config is not null) _subs.Add(_config.RegisterProperties(BackgroundSettings.Properties));
        if (services.GetService<IContributionRegistry>() is { } contributions)
            _subs.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, [new KeybindingContribution(SwitchSessionCommand, "ctrl+alt+s")]));
        RegisterCommands();
        _tray = TrayController.TryCreate(_host, _config, _ui);
    }

    public bool IsActive => _activated.Task.IsCompleted;
    internal BackgroundHost Host => _host;

    private bool IsDebugging() => _sp.GetService<IDebugService>()?.Sessions.Count > 0;

    // ------------------------------------------------------------------ IPC (pool thread) → UI

    internal async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct)
    {
        if (request.Type == IpcVerbs.Quit) return await OnUiAsync(() => QuitAsync(request.Force == true)).ConfigureAwait(false);
        if (_host.IsUnclaimedSpare) return IpcMessage.Failure("the warm spare has no session yet (claim it first)");
        await _activated.Task.WaitAsync(ct).ConfigureAwait(false);
        switch (request.Type)
        {
            case IpcVerbs.Show:
                var targets = Classify(request.Paths);
                return await OnUiAsync(() => ShowAsync(targets, request.Wait == true, ct)).ConfigureAwait(false);
            case IpcVerbs.FocusFile:
                var files = Classify(request.Paths);
                return await OnUiAsync(async () => { await OpenPathsAsync(files).ConfigureAwait(true); return IpcMessage.Reply(); }).ConfigureAwait(false);
            case IpcVerbs.Hide:
                return await OnUiAsync(() => { Hide(); return Task.FromResult(IpcMessage.Reply()); }).ConfigureAwait(false);
            case IpcVerbs.Inspect:
                return await InspectAsync(request).ConfigureAwait(false);
            case IpcVerbs.TestEdit or IpcVerbs.TestCaret or IpcVerbs.TestFold or IpcVerbs.TestCloseTab or IpcVerbs.TestCloseWindow:
                return await OnUiAsync(() => TestActionAsync(request)).ConfigureAwait(false);
            default:
                return IpcMessage.Failure("unknown request " + request.Type);
        }
    }

    /// <summary>Directory checks are file I/O: done here on the pool thread, never on the UI thread.</summary>
    private static IReadOnlyList<(IpcPath Path, bool IsDirectory)> Classify(IReadOnlyList<IpcPath>? paths) =>
        (paths ?? []).Select(p => (p, Directory.Exists(p.Path))).ToArray();

    private async Task<IpcMessage> OnUiAsync(Func<Task<IpcMessage>> work)
    {
        var inner = await _ui.InvokeAsync(work).ConfigureAwait(false);
        return await inner.ConfigureAwait(false);
    }

    internal async Task ActivateOnUiAsync(PreparedSession prepared, bool hidden)
    {
        var inner = await _ui.InvokeAsync(() => ActivateAsync(prepared, hidden)).ConfigureAwait(false);
        await inner.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ activation (restore)

    /// <summary>Restores <paramref name="prepared"/> into the (still hidden) window, then shows it unless <paramref name="hidden"/>.</summary>
    internal async Task ActivateAsync(PreparedSession prepared, bool hidden)
    {
        _ui.AssertUiThread();
        try
        {
            _host.SetState(ProcessStates.Starting);
            _readOnly = prepared.Status == SessionLoadStatus.TooNew;
            UpdateSessionIdentity();
            if (prepared.Document?.Window is { } bounds) ApplyBounds(bounds);
            _shell.RestoreOverride = () => RestoreAsync(prepared);
            SessionTrace.Mark("host: restore start");
            try { await _shell.StartDeferredAsync().ConfigureAwait(true); }
            finally { _shell.RestoreOverride = null; }
            SessionTrace.Mark("host: restored");
            SubscribeForAutosave();
            _autosave.MarkDirty(); // the session now exists on disk (WasResident) even if nothing changes
            if (hidden)
            {
                _host.SetState(ProcessStates.Hidden);
                _policy.OnHidden();
            }
            else await ShowWindowAsync().ConfigureAwait(true);
            ReportLoadProblem(prepared);
        }
        finally { _activated.TrySetResult(); }
    }

    private async Task RestoreAsync(PreparedSession prepared)
    {
        var well = _shell.Well;
        var doc = prepared.Document;
        if (doc?.Folders.FirstOrDefault() is { } folder && Uri.TryCreate(folder, UriKind.Absolute, out var folderUri) && _workspace is not null)
        {
            try { await _workspace.OpenFolderAsync(folderUri).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Notify(MessageSeverity.Warning, $"Could not reopen the folder {folderUri}: {ex.Message}"); }
        }
        if (prepared.Layout is { } layout) await _shell.Layout.ApplyAsync(layout).ConfigureAwait(true);
        else await _shell.ResetLayoutAsync().ConfigureAwait(true);

        foreach (var e in doc?.Editors ?? [])
        {
            if (e.ViewState is { } vs && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && well.Find(uri) is { } tab) ApplyViewState(tab, ToEditor(vs));
        }

        var restored = new Dictionary<string, EditorTab>(StringComparer.Ordinal);
        foreach (var backup in prepared.Backups)
        {
            try
            {
                if (await RestoreBackupAsync(backup).ConfigureAwait(true) is { } tab)
                {
                    restored[backup.Info.Uri] = tab;
                    if (doc?.Editors.FirstOrDefault(e => e.Uri == backup.Info.Uri)?.ViewState is { } vs) ApplyViewState(tab, ToEditor(vs));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or UriFormatException)
            {
                Notify(MessageSeverity.Error, $"Could not restore the unsaved changes of {backup.Info.Uri}: {ex.Message}");
            }
        }
        if (doc?.ActiveEditor is { } active)
        {
            var tab = restored.GetValueOrDefault(active) ?? (Uri.TryCreate(active, UriKind.Absolute, out var au) ? well.Find(au) : null);
            if (tab is not null) well.Activate(tab, preserveFocus: true);
        }
    }

    /// <summary>Puts a backup's unsaved text back into its document (opening it, or a new untitled buffer when it has no file any more).</summary>
    private async Task<EditorTab?> RestoreBackupAsync(PreparedBackup backup)
    {
        var info = backup.Info;
        if (!Uri.TryCreate(info.Uri, UriKind.Absolute, out var uri)) return null;
        var well = _shell.Well;
        EditorTab tab;
        if (backup.DiskState is BackupDiskState.Untitled or BackupDiskState.MissingOnDisk || !uri.IsFile)
        {
            tab = well.OpenUntitled(info.LanguageId);
            if (backup.DiskState == BackupDiskState.MissingOnDisk)
                Notify(MessageSeverity.Warning, $"{uri.LocalPath} was deleted after your unsaved changes to it were backed up; they were restored into {tab.Title}.");
        }
        else tab = well.Find(uri) ?? await well.OpenAsync(uri, preview: false, preserveFocus: true).ConfigureAwait(true);
        var d = tab.Doc;
        if (d.Length != backup.Text.Length || d.GetText() != backup.Text)
            d.ApplyEdits([new TextEdit(new TextRange(TextPosition.Zero, d.PositionAt(d.Length)), backup.Text)]);
        if (backup.DiskState == BackupDiskState.ChangedOnDisk) _pendingConflicts.Add((tab, backup));
        return tab;
    }

    private static void ApplyViewState(EditorTab tab, EditorViewState state)
    {
        if (tab.Controller is { } controller) controller.RestoreViewState(state);
        else tab.SetViewStateForNextView(state);
    }

    private void ApplyBounds(SessionWindow w)
    {
        if (w.Width >= _window.MinWidth && w.Height >= _window.MinHeight)
        {
            _window.Width = w.Width;
            _window.Height = w.Height;
        }
        var screens = _window.Screens?.All ?? [];
        // Only where a screen still is (the monitor may be gone): otherwise the window manager places it.
        if (screens.Any(s => s.WorkingArea.Contains(new PixelPoint(w.X + 40, w.Y + 15))))
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Position = new PixelPoint(w.X, w.Y);
        }
        _normalBounds = w with { State = "normal" };
        _window.WindowState = w.State switch { "maximized" => WindowState.Maximized, "fullscreen" => WindowState.FullScreen, _ => WindowState.Normal };
    }

    private void ReportLoadProblem(PreparedSession prepared)
    {
        switch (prepared.Status)
        {
            case SessionLoadStatus.RecoveredFromBackup:
                Notify(MessageSeverity.Warning, $"Session '{prepared.Name}': {prepared.Message}.");
                break;
            case SessionLoadStatus.Corrupt:
                Notify(MessageSeverity.Error, $"Session '{prepared.Name}' could not be read ({prepared.Message}); it starts empty. The damaged file was kept.");
                break;
            case SessionLoadStatus.TooNew:
                Notify(MessageSeverity.Error, $"Session '{prepared.Name}' was saved by a newer CsCode ({prepared.Message}); it opens empty and is not saved by this version.");
                break;
        }
    }

    // ------------------------------------------------------------------ show / hide

    private async Task<IpcMessage> ShowAsync(IReadOnlyList<(IpcPath Path, bool IsDirectory)> targets, bool wait, CancellationToken ct)
    {
        await ShowWindowAsync().ConfigureAwait(true);
        var opened = await OpenPathsAsync(targets).ConfigureAwait(true);
        if (opened.Count > 0) await WaitForPaintAsync().ConfigureAwait(true);
        if (wait && opened.Count == 1) await WaitForTabClosedAsync(opened[0], ct).ConfigureAwait(true);
        return IpcMessage.Reply();
    }

    /// <summary>Makes the window visible and frontmost; completes once a frame with it was painted.</summary>
    internal async Task ShowWindowAsync()
    {
        _ui.AssertUiThread();
        if (_quitting) return;
        var (serversWereStopped, _) = _policy.OnShown();
        SessionTrace.Mark("host: show");
        if (!_window.IsVisible)
        {
            RecreateActiveViews(); // the hidden-session trim may have disposed them: the first frame must show the editors, not empty hosts
            _window.Show();
            SessionTrace.Mark("host: shown");
        }
        _window.Activate();
        await WaitForPaintAsync().ConfigureAwait(true);
        SessionTrace.Mark("host: painted");
        _host.MarkShown();
        if (_tray is not null) _ = _tray.TryLeadAsync(); // the process that showed the tray icon may have exited
        if (serversWereStopped) RestartServers();
        ShowPendingConflicts();
    }

    internal void Hide()
    {
        _ui.AssertUiThread();
        if (!_window.IsVisible || _quitting) return;
        CaptureBounds();
        _window.Hide();
        _host.SetState(ProcessStates.Hidden);
        _ = SaveNowAsync();
        _policy.OnHidden();
        if (_tray is not null) _ = _tray.TryLeadAsync();
        _ = Task.Run(EnforceResidentLimitAsync);
    }

    private void RecreateActiveViews()
    {
        foreach (var group in _shell.Factory.DocumentGroups())
            if (group.ActiveDockable is ShellDocument { IsDisposed: false } tab) tab.EnsureHost();
    }

    /// <summary>
    /// Waits until the window was painted: the headless test platform renders on demand (a captured frame is a painted frame); a real platform
    /// paints after the next animation frames (bounded, so a compositor that never ticks cannot hang a launcher).
    /// </summary>
    private async Task WaitForPaintAsync()
    {
        if (_host.IsHeadlessTest)
        {
            using var frame = _window.CaptureRenderedFrame();
            return;
        }
        for (var i = 0; i < 2; i++)
        {
            var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _window.RequestAnimationFrame(_ => painted.TrySetResult());
            try { await painted.Task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(true); }
            catch (TimeoutException) { return; }
        }
    }

    /// <summary>Opens the launcher's paths: a folder becomes the workspace (unless it already is), files open in editors at their positions.</summary>
    private async Task<IReadOnlyList<Uri>> OpenPathsAsync(IReadOnlyList<(IpcPath Path, bool IsDirectory)> targets)
    {
        var opened = new List<Uri>();
        var nav = _sp.GetRequiredService<INavigationService>();
        foreach (var (target, isDirectory) in targets)
        {
            try
            {
                if (isDirectory)
                {
                    var folder = new Uri(Path.TrimEndingDirectorySeparator(target.Path) + "/");
                    if (_workspace is not null && !_workspace.Folders.Any(f => f.AbsolutePath.TrimEnd('/') == folder.AbsolutePath.TrimEnd('/')))
                        await _workspace.OpenFolderAsync(folder).ConfigureAwait(true);
                    continue;
                }
                var uri = new Uri(Path.GetFullPath(target.Path));
                TextRange? at = target.Line is { } l ? TextRange.At(Math.Max(0, l - 1), Math.Max(0, (target.Column ?? 1) - 1)) : null;
                await nav.OpenAsync(uri, at).ConfigureAwait(true);
                opened.Add(uri);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or InvalidOperationException)
            {
                Notify(MessageSeverity.Error, $"Could not open {target.Path}: {ex.Message}");
            }
        }
        return opened;
    }

    private async Task WaitForTabClosedAsync(Uri uri, CancellationToken ct)
    {
        var well = _shell.Well;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = well.TabsChanged.Subscribe(_ => { if (well.Find(uri) is null || _quitting) closed.TrySetResult(); });
        if (well.Find(uri) is null) closed.TrySetResult();
        await closed.Task.WaitAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ closing and quitting

    /// <summary>MainWindow's close hook: true when the close was handled here (hidden, or a quit started).</summary>
    private bool OnCloseRequested(WindowCloseReason reason)
    {
        if (_quitting || _disposed || _host.IsUnclaimedSpare || !IsActive) return false;
        if (reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            // The desktop session or the app is going down: queue a final save (the lane drains before the process exits) and let it close.
            _wasResident = true;
            QueueWrite(Capture(includeBackups: true));
            return false;
        }
        if (_config?.Get(BackgroundSettings.KeepRunning, true) ?? true)
        {
            Hide();
            return true;
        }
        _ = QuitAsync(force: false, windowClose: true);
        return true;
    }

    /// <summary>
    /// Quit Session: persists the session with hot-exit backups of every dirty document (no prompts) and ends the process. With
    /// <c>files.hotExit</c> off (or <c>onExit</c> for a window close) dirty documents are prompted for instead, unless <paramref name="force"/>.
    /// Documents beyond the backup size limit are never dropped silently: a forced quit is refused for them, a normal one prompts.
    /// </summary>
    internal async Task<IpcMessage> QuitAsync(bool force, bool windowClose = false)
    {
        _ui.AssertUiThread();
        if (_quitting) return IpcMessage.Reply();
        _quitting = true;
        var previousState = _host.State;
        _host.SetState(ProcessStates.Quitting);
        try
        {
            if (_host.IsUnclaimedSpare || !IsActive)
            {
                _window.CloseWithoutPrompt();
                return IpcMessage.Reply();
            }
            _wasResident = false;
            var hotExit = _config?.Get(BackgroundSettings.HotExit, BackgroundSettings.HotExitOnExitAndWindowClose) ?? BackgroundSettings.HotExitOnExitAndWindowClose;
            var backUp = force || hotExit == BackgroundSettings.HotExitOnExitAndWindowClose || (hotExit == BackgroundSettings.HotExitOnExit && !windowClose);
            var dirty = _shell.Well.EditorTabs.Any(t => t.IsDirty);
            if (!backUp && dirty) return await QuitWithPromptsAsync(previousState).ConfigureAwait(true);

            var result = await PersistAsync().ConfigureAwait(true);
            if (result.Skipped.Count > 0)
            {
                if (force)
                {
                    Undo(previousState);
                    return IpcMessage.Failure($"session '{_host.SessionName}' has unsaved changes beyond the backup limit ({string.Join(", ", result.Skipped)}); not quitting");
                }
                Notify(MessageSeverity.Warning, "Some unsaved changes exceed the backup limit (cscode.sessions.maxBackupMb): save or discard them.");
                return await QuitWithPromptsAsync(previousState).ConfigureAwait(true);
            }
            _window.CloseWithoutPrompt();
            return IpcMessage.Reply();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Undo(previousState);
            Notify(MessageSeverity.Error, "The session could not be saved, so it keeps running: " + ex.Message);
            return IpcMessage.Failure("could not save the session: " + ex.Message);
        }
    }

    private async Task<IpcMessage> QuitWithPromptsAsync(string previousState)
    {
        await PersistAsync(includeBackups: false).ConfigureAwait(true);
        _quitting = false; // ShowWindowAsync does nothing while quitting
        await ShowWindowAsync().ConfigureAwait(true);
        _quitting = true;
        if (await _window.RequestExitAsync().ConfigureAwait(true)) return IpcMessage.Reply();
        Undo(_window.IsVisible ? ProcessStates.Shown : previousState);
        return IpcMessage.Failure("the quit was cancelled");
    }

    private void Undo(string state)
    {
        _quitting = false;
        _wasResident = true;
        _host.SetState(state);
        _autosave.MarkDirty();
    }

    // ------------------------------------------------------------------ hidden-session policy

    private void TrimFromTimer() => _ui.Post(() =>
    {
        if (_disposed || _window.IsVisible) return;
        foreach (var tab in _shell.Well.Tabs) tab.DisposeView(); // view state is captured first; the active tabs come back on show
        _ = Task.Run(async () =>
        {
            try
            {
                if (_sp.GetService<IMemoryMonitor>() is { } monitor) await monitor.TrimNowAsync(MemoryTrimLevel.Aggressive).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a failing trimmable must not stop the process trim */ }
            Interlocked.Exchange(ref _lastTrimWorkingSet, ProcessMemory.TrimNow());
            Interlocked.Increment(ref _trims);
        });
    });

    private void StopServersFromTimer()
    {
        if (_sp.GetService<ILanguageServerManager>() is not { } servers) return;
        var running = servers.Servers.Where(s => s.State is LanguageServerState.Running or LanguageServerState.Starting or LanguageServerState.Unresponsive)
            .Select(s => s.Id).Distinct(StringComparer.Ordinal).ToArray();
        _stoppedServers = running;
        foreach (var id in running)
            _ = Task.Run(async () =>
            {
                try { await servers.StopAsync(id).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* stopped or not, it is restarted on show */ }
            });
    }

    private void RestartServers()
    {
        if (_sp.GetService<ILanguageServerManager>() is not { } servers) return;
        var ids = Interlocked.Exchange(ref _stoppedServers, []);
        foreach (var id in ids)
            _ = Task.Run(async () =>
            {
                try { await servers.StartAsync(id).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* the language client reports its own start failures */ }
            });
    }

    /// <summary>More than <c>maxResidentSessions</c> resident: the least recently shown hidden ones (never this one) are asked to quit.</summary>
    private async Task EnforceResidentLimitAsync()
    {
        try
        {
            var max = _config?.Get(BackgroundSettings.MaxResidentSessions, 3) ?? 3;
            var others = await ResidentDiscovery.DiscoverAsync(_host.Runtime, IpcClient.DefaultConnectTimeout, excludePid: Environment.ProcessId).ConfigureAwait(false);
            var me = new ResidentProcess(Environment.ProcessId, "", _host.SessionName, ProcessStates.Hidden, _host.LastShown, IsDebugging(), null, true);
            foreach (var pid in ResidentPolicy.SelectEvictions(others.Append(me), max, keepPid: Environment.ProcessId))
            {
                try { await IpcClient.RequestAsync(_host.Runtime.EndpointOf(pid), new IpcMessage { Type = IpcVerbs.Quit }, IpcClient.DefaultConnectTimeout, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (IpcException) { /* it went away or is wedged: nothing to do */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A second spare (two launchers raced): the one with the higher pid leaves.</summary>
    internal async Task QuitIfDuplicateSpareAsync()
    {
        var others = await ResidentDiscovery.DiscoverAsync(_host.Runtime, IpcClient.DefaultConnectTimeout, excludePid: Environment.ProcessId).ConfigureAwait(false);
        if (others.Any(p => p.IsSpare && p.Pid < Environment.ProcessId) && _host.IsUnclaimedSpare)
            await OnUiAsync(() => QuitAsync(force: true)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ conflicts and notifications

    private void ShowPendingConflicts()
    {
        if (_pendingConflicts.Count == 0) return;
        var conflicts = _pendingConflicts.ToArray();
        _pendingConflicts.Clear();
        var windows = _sp.GetRequiredService<IWindowService>();
        foreach (var (tab, backup) in conflicts)
        {
            _ = Ask(tab, backup);
        }

        async Task Ask(EditorTab tab, PreparedBackup backup)
        {
            const string Keep = "Keep My Changes", Load = "Load Disk Version";
            var choice = await windows.ShowMessageAsync(MessageSeverity.Warning,
                $"{tab.Title} changed on disk after your unsaved changes to it were backed up. Your changes were restored.",
                [new MessageAction(Keep), new MessageAction(Load)]).ConfigureAwait(true);
            if (choice == Load && !tab.IsDisposed) await tab.Doc.ReloadAsync().ConfigureAwait(true);
        }
    }

    private void Notify(MessageSeverity severity, string message)
    {
        if (_sp.GetService<IWindowService>() is { } windows) _ = windows.ShowMessageAsync(severity, message);
    }

    // ------------------------------------------------------------------ identity

    private void UpdateSessionIdentity()
    {
        var name = _host.SessionName;
        _window.SessionName = name is null || _host.IsUnnamed ? null : name;
        _statusItem?.Dispose();
        _statusItem = name is null || _sp.GetService<IStatusBarService>() is not { } status ? null
            : status.Set(new StatusBarItemModel("cscode.session", "$(window) " + name, _host.IsUnnamed ? "Unnamed session: Save Session As... to name it" : "Session: click to switch",
                SwitchSessionCommand, StatusBarAlignment.Left, 5));
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _window.Closed -= OnWindowClosed;
        _disposed = true;
        _policy.Dispose();
        _autosave.Dispose();
        _statusItem?.Dispose();
        _tray?.Dispose();
        _subs.Dispose();
        foreach (var store in _tabSubs.Values) store.Dispose();
        _tabSubs.Clear();
        _window.CloseInterceptor = null;
    }
}
