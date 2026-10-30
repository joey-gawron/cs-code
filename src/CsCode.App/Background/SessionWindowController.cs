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
    /// <summary>Backups of remote documents waiting for their file system (the connection) to come up; kept on disk meanwhile (UI thread).</summary>
    private readonly List<PreparedBackup> _pendingRemote = new();
    /// <summary>Pending <c>cscode --wait</c> requests (UI thread): completed when their files are closed, the window is closed or the session quits.</summary>
    private readonly List<TaskCompletionSource> _waits = new();
    private readonly object _writeGate = new();
    private IReadOnlyList<string> _stoppedServers = [];
    private SessionWindow? _normalBounds;
    private Captured? _lastCaptured;
    private long _captureSeq, _writtenSeq, _lastTrimWorkingSet;
    private int _trims, _skippedBackups;
    /// <summary>The quit is certain (the window is closing): the session's last write is done and its lock released; requests get <see cref="IpcMessage.QuittingError"/>.</summary>
    private volatile bool _quitCommitted;
    /// <summary>No more writes of this session (set under <see cref="_writeGate"/> when the quit commits, before the lock is released).</summary>
    private bool _sealed;
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
        if (request.Type == IpcVerbs.Quit) return await OnUiAsync(() => QuitAsync(request.Force == true, evict: request.Evict == true)).ConfigureAwait(false);
        if (_host.IsUnclaimedSpare) return IpcMessage.Failure("the warm spare has no session yet (claim it first)");
        await _activated.Task.WaitAsync(ct).ConfigureAwait(false);
        switch (request.Type)
        {
            case IpcVerbs.Show:
                // A window that is closing for good cannot be shown: the launcher waits for the process to end and opens the session afresh.
                if (_quitCommitted) return IpcMessage.Failure(IpcMessage.QuittingError);
                var targets = Classify(request.Paths);
                var reply = await OnUiAsync(() => ShowAsync(targets, request.Wait == true, ct)).ConfigureAwait(false);
                if (reply.Ok == true) OpenRemoteTargets(request.Remotes);
                return reply;
            case IpcVerbs.FocusFile:
                if (_quitCommitted) return IpcMessage.Failure(IpcMessage.QuittingError);
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

    /// <summary>
    /// <c>cscode ssh://host/path</c> through the launcher: the session connects and opens the folder (<c>remote.openFolder</c>, as a foreground start
    /// does). Not awaited: connecting may wait for an ssh prompt, and the launcher's show must not.
    /// </summary>
    private void OpenRemoteTargets(IReadOnlyList<string>? remotes)
    {
        if (remotes is not { Count: > 0 } || _sp.GetService<ICommandService>() is not { } commands) return;
        foreach (var remote in remotes)
            _ = _ui.InvokeAsync(() => commands.ExecuteAsync(CsCode.Remote.Shell.RemoteShellContributions.OpenFolder, remote).AsTask());
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
            UpdateNeedsPrompt();
            _autosave.MarkDirty(); // the session now exists on disk (WasResident) even if nothing changes
            if (hidden)
            {
                _shell.SetHidden(true);
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
        if (doc?.Folders.FirstOrDefault() is { } remoteFolder && Uri.TryCreate(remoteFolder, UriKind.Absolute, out var remoteUri)
            && CsCode.Remote.Shell.RemoteShellContributions.IsRemoteFolder(remoteUri) && _sp.GetService<ICommandService>() is { } commands)
        {
            // A remote workspace comes back by reconnecting (remote.openFolder connects, then opens the folder). Not awaited: the connection may wait
            // for an ssh prompt; the editors below load as soon as it is up (remote reads wait for a connection in progress).
            _ = commands.ExecuteAsync(CsCode.Remote.Shell.RemoteShellContributions.OpenFolder, remoteUri.ToString()).AsTask();
        }
        else if (doc?.Folders.FirstOrDefault() is { } folder && Uri.TryCreate(folder, UriKind.Absolute, out var folderUri) && _workspace is not null)
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
        if (_pendingRemote.Count > 0) _ = RestoreRemoteBackupsAsync(doc);
    }

    /// <summary>
    /// Puts a backup's unsaved text back into its document (opening it, or a new untitled buffer when it has no file any more). A remote document's
    /// backup waits for its connection instead (<see cref="RestoreRemoteBackupsAsync"/>): the restore never waits for an ssh prompt.
    /// </summary>
    private async Task<EditorTab?> RestoreBackupAsync(PreparedBackup backup)
    {
        var info = backup.Info;
        if (!Uri.TryCreate(info.Uri, UriKind.Absolute, out var uri)) return null;
        var well = _shell.Well;
        EditorTab tab;
        if (IsRemote(uri))
        {
            _pendingRemote.Add(backup);
            return null;
        }
        if (backup.DiskState is BackupDiskState.Untitled or BackupDiskState.MissingOnDisk || !uri.IsFile)
        {
            tab = well.OpenUntitled(info.LanguageId);
            if (backup.DiskState == BackupDiskState.MissingOnDisk)
                Notify(MessageSeverity.Warning, $"{uri.LocalPath} was deleted after your unsaved changes to it were backed up; they were restored into {tab.Title}.");
        }
        else tab = well.Find(uri) ?? await well.OpenAsync(uri, preview: false, preserveFocus: true).ConfigureAwait(true);
        ApplyBackupText(tab, backup.Text);
        if (backup.DiskState == BackupDiskState.ChangedOnDisk) _pendingConflicts.Add((tab, backup));
        return tab;
    }

    /// <summary>A document of another file system than the local one (<c>ssh:</c>, <c>docker:</c>, ...): neither a file nor an untitled buffer.</summary>
    private static bool IsRemote(Uri uri) => !uri.IsFile && !string.Equals(uri.Scheme, "untitled", StringComparison.OrdinalIgnoreCase);

    private static void ApplyBackupText(EditorTab tab, string text)
    {
        var d = tab.Doc;
        if (d.Length != text.Length || d.GetText() != text)
            d.ApplyEdits([new TextEdit(new TextRange(TextPosition.Zero, d.PositionAt(d.Length)), text)]);
    }

    /// <summary>
    /// The unsaved changes of remote documents go back into the remote documents themselves (as VS Code restores a remote window's backups), once
    /// their file system answers: opening waits for the connection the restore started. Until then their backups stay on disk (every autosave
    /// keeps them). When a document cannot be opened (the connection failed, the file is gone), its text goes into an untitled buffer of its
    /// language, and the user is told which remote file it belongs to.
    /// </summary>
    private async Task RestoreRemoteBackupsAsync(SessionDocument? doc)
    {
        _ui.AssertUiThread();
        foreach (var backup in _pendingRemote.ToArray())
        {
            if (_disposed || _quitting) return;
            var uri = new Uri(backup.Info.Uri);
            EditorTab tab;
            string? problem = null;
            try
            {
                tab = _shell.Well.Find(uri) ?? await _shell.Well.OpenAsync(uri, preview: false, preserveFocus: true).ConfigureAwait(true);
                if (_disposed || tab.IsDisposed) return;
                // Edited meanwhile (the tab came back with the layout): the newer text stays, the backup goes into a buffer of its own.
                if (tab.IsDirty) problem = "it was edited before they could be restored";
                else
                {
                    ApplyBackupText(tab, backup.Text);
                    if (doc?.Editors.FirstOrDefault(e => e.Uri == backup.Info.Uri)?.ViewState is { } vs) ApplyViewState(tab, ToEditor(vs));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { problem = ex.Message; }
            if (_disposed) return;
            if (problem is not null)
            {
                tab = _shell.Well.OpenUntitled(backup.Info.LanguageId);
                ApplyBackupText(tab, backup.Text);
                Notify(MessageSeverity.Warning, $"Your unsaved changes to {uri} could not be put back into it ({problem}); they were restored into {tab.Title}.");
            }
            _pendingRemote.Remove(backup);
            _autosave.MarkDirty(); // the document's own backup replaces the restored one now
        }
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
                Notify(MessageSeverity.Error, $"Session '{prepared.Name}' was saved by a newer CsCode ({prepared.Message}); it opens empty and is not saved by this version. " +
                    "Unsaved changes made in this window cannot be backed up: quitting asks to save them, or use Save Session As... to keep working in a session of this version.");
                break;
        }
    }

    // ------------------------------------------------------------------ show / hide

    private async Task<IpcMessage> ShowAsync(IReadOnlyList<(IpcPath Path, bool IsDirectory)> targets, bool wait, CancellationToken ct)
    {
        if (_quitCommitted) return IpcMessage.Failure(IpcMessage.QuittingError);
        await ShowWindowAsync().ConfigureAwait(true);
        var opened = await OpenPathsAsync(targets).ConfigureAwait(true);
        if (opened.Count > 0)
        {
            SessionTrace.Mark("host: paths opened");
            await WaitForPaintAsync().ConfigureAwait(true);
            SessionTrace.Mark("host: painted with the paths");
        }
        if (wait && opened.Count > 0) await WaitForTabsClosedAsync(opened, ct).ConfigureAwait(true);
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
            _shell.SetHidden(false);
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
        EndWaits(); // closing the window ends `cscode --wait` (VS Code: the window closed)
        CaptureBounds();
        _window.Hide();
        _shell.SetHidden(true);
        UpdateNeedsPrompt(); // before the state says hidden: an eviction must know whether quitting would ask anything
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

    /// <summary>
    /// <c>cscode --wait</c> (VS Code's): returns once every one of <paramref name="uris"/> is closed, or the window was closed (hidden) or the
    /// session quits (<see cref="EndWaits"/>), so a git commit never waits for a window that is gone.
    /// </summary>
    private async Task WaitForTabsClosedAsync(IReadOnlyList<Uri> uris, CancellationToken ct)
    {
        var well = _shell.Well;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool AllClosed() => uris.All(u => well.Find(u) is null);
        using var sub = well.TabsChanged.Subscribe(_ => { if (AllClosed()) done.TrySetResult(); });
        if (AllClosed() || _quitting || !_window.IsVisible) done.TrySetResult();
        _waits.Add(done);
        try { await done.Task.WaitAsync(ct).ConfigureAwait(true); }
        finally { _waits.Remove(done); }
    }

    /// <summary>The window closed (hidden) or the session quits: every pending <c>--wait</c> is answered.</summary>
    private void EndWaits()
    {
        foreach (var wait in _waits.ToArray()) wait.TrySetResult();
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
    /// Unsaved changes are never dropped silently: those beyond the backup size limit, and every one in a session written by a newer CsCode (which
    /// this version must not write, backups included), make a forced quit refuse and a normal one prompt. An eviction (<paramref name="evict"/>,
    /// more than <c>maxResidentSessions</c>) never prompts: it is refused instead, and the hidden window stays hidden.
    /// </summary>
    internal async Task<IpcMessage> QuitAsync(bool force, bool windowClose = false, bool evict = false, bool terminate = false)
    {
        _ui.AssertUiThread();
        // A quit already under way (SIGTERM during the save prompts included: the host's watchdog writes the last snapshot and ends the process).
        if (_quitting) return IpcMessage.Reply();
        force |= terminate;
        _quitting = true;
        EndWaits(); // the session quits: `cscode --wait` returns 0 (VS Code: the window closed)
        var previousState = _host.State;
        _host.SetState(ProcessStates.Quitting);
        try
        {
            if (_host.IsUnclaimedSpare || !IsActive)
            {
                await CommitQuitAsync().ConfigureAwait(true);
                _window.CloseWithoutPrompt();
                return IpcMessage.Reply();
            }
            // A quit the user asked for leaves the session out of the next login's restore (restoreOnLogin); SIGTERM (logout) does not.
            _wasResident = terminate;
            var hotExit = _config?.Get(BackgroundSettings.HotExit, BackgroundSettings.HotExitOnExitAndWindowClose) ?? BackgroundSettings.HotExitOnExitAndWindowClose;
            var backUp = force || hotExit == BackgroundSettings.HotExitOnExitAndWindowClose || (hotExit == BackgroundSettings.HotExitOnExit && !windowClose);
            var dirty = _shell.Well.EditorTabs.Any(t => t.IsDirty);
            if (dirty && _readOnly && terminate)
            {
                // Nobody can be asked, and the newer CsCode's session must not be written: its unsaved changes go to a new session of this
                // version (window-N) instead, as Save Session As would put them.
                var copy = await _host.Lane.RunAsync(_host.TakeNewUnnamedSession).ConfigureAwait(true);
                lock (_writeGate) _readOnly = false;
                _backedUp.Clear();
                UpdateSessionIdentity();
                SessionTrace.Mark("host: unsaved changes of a newer session kept as " + copy);
            }
            if (dirty && _readOnly)
            {
                if (force || evict)
                {
                    Undo(previousState);
                    return IpcMessage.Failure($"session '{_host.SessionName}' was saved by a newer CsCode, so this version cannot back up its unsaved changes; not quitting");
                }
                return await QuitWithPromptsAsync(previousState).ConfigureAwait(true);
            }
            if (!backUp && dirty)
            {
                if (evict) return Refuse(previousState, NotEvicted);
                return await QuitWithPromptsAsync(previousState).ConfigureAwait(true);
            }

            // SIGTERM: no bound on the backups (keeping the text beats the disk limit when nobody can be asked to save it).
            var result = await PersistAsync(maxBackupBytes: terminate ? long.MaxValue : null).ConfigureAwait(true);
            if (result.Skipped.Count > 0)
            {
                if (evict) return Refuse(previousState, NotEvicted);
                if (force) return Refuse(previousState, $"session '{_host.SessionName}' has unsaved changes beyond the backup limit ({string.Join(", ", result.Skipped)}); not quitting");
                Notify(MessageSeverity.Warning, "Some unsaved changes exceed the backup limit (cscode.sessions.maxBackupMb): save or discard them.");
                return await QuitWithPromptsAsync(previousState).ConfigureAwait(true);
            }
            await CommitQuitAsync().ConfigureAwait(true);
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

    private const string NotEvicted = "not evicted: quitting would have to ask about unsaved changes";

    /// <summary>The quit is certain and the session's last write done (the host's SIGTERM watchdog leaves the rest to the orderly shutdown).</summary>
    internal bool QuitCommitted => _quitCommitted;

    /// <summary>SIGTERM (any thread): Quit Session with hot exit and no questions (<see cref="QuitAsync"/> with <c>terminate</c>).</summary>
    internal async Task TerminateAsync()
    {
        var reply = await OnUiAsync(() => QuitAsync(force: true, terminate: true)).ConfigureAwait(false);
        if (reply.Ok != true) SessionTrace.Mark("host: SIGTERM quit failed: " + reply.Error);
    }

    private IpcMessage Refuse(string previousState, string reason)
    {
        Undo(previousState);
        return IpcMessage.Failure(reason);
    }

    private async Task<IpcMessage> QuitWithPromptsAsync(string previousState)
    {
        await PersistAsync(includeBackups: false).ConfigureAwait(true);
        _quitting = false; // ShowWindowAsync does nothing while quitting
        await ShowWindowAsync().ConfigureAwait(true);
        _quitting = true;
        if (await _window.RequestExitAsync().ConfigureAwait(true))
        {
            // Every document was saved or its changes discarded: the autosave's backups of them must not bring "Don't Save" text back next time
            // (VS Code discards its backups after the shutdown prompts too). A newer CsCode's backups are never touched.
            await CommitQuitAsync(discardBackups: !_readOnly).ConfigureAwait(true);
            return IpcMessage.Reply();
        }
        Undo(_window.IsVisible ? ProcessStates.Shown : previousState);
        return IpcMessage.Failure("the quit was cancelled");
    }

    /// <summary>
    /// The quit is certain (the window closes next, or has just closed): no write of this session may follow, so it is sealed and its lock released
    /// now, before the language servers and the rest shut down (seconds, with a slow server). A launch of the session meanwhile then starts it
    /// afresh instead of taking this process for a wedged owner and opening a copy. <paramref name="discardBackups"/>: after the save prompts.
    /// </summary>
    private async Task CommitQuitAsync(bool discardBackups = false)
    {
        _quitCommitted = true;
        var name = _host.SessionName;
        try
        {
            // On the lane, after any write queued before: it is the last thing done to the session's files.
            await _host.Lane.RunAsync(() =>
            {
                lock (_writeGate) _sealed = true;
                if (discardBackups && name is not null) _host.Store.Backups.DeleteAll(name);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            lock (_writeGate) _sealed = true;
        }
        finally { _host.ReleaseSessionLock(); }
    }

    private void Undo(string state)
    {
        _quitting = false;
        _wasResident = true;
        _host.SetState(state);
        _autosave.MarkDirty();
        UpdateNeedsPrompt();
    }

    // ------------------------------------------------------------------ hidden-session policy

    private void TrimFromTimer() => _ui.Post(() =>
    {
        if (_disposed || _window.IsVisible) return;
        foreach (var tab in _shell.Well.Tabs) tab.DisposeView(); // view state is captured first; the active tabs come back on show
        var monitor = _sp.GetService<IMemoryMonitor>();
        _ = Task.Run(async () =>
        {
            Interlocked.Exchange(ref _lastTrimWorkingSet, await TrimHiddenProcessAsync(monitor).ConfigureAwait(false));
            Interlocked.Increment(ref _trims);
        });
    });

    /// <summary>
    /// One trim of a hidden session: the memory monitor's aggressive trim runs the trimmables, then compacts the heap (large object heap included)
    /// and returns free pages to the OS (malloc_trim), as <see cref="IMemoryMonitor.TrimNowAsync"/> promises. A second, process-level trim after
    /// it would only pause every thread twice for nothing. Without a monitor, or when it fails, the process-level trim alone. Returns the working set.
    /// </summary>
    internal static async Task<long> TrimHiddenProcessAsync(IMemoryMonitor? monitor, Func<long>? processTrim = null)
    {
        processTrim ??= ProcessMemory.TrimNow;
        if (monitor is null) return processTrim();
        try { await monitor.TrimNowAsync(MemoryTrimLevel.Aggressive).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return processTrim(); } // the process still gives its memory back
        return Environment.WorkingSet;
    }

    private void StopServersFromTimer()
    {
        if (_sp.GetService<ILanguageServerManager>() is not { } servers) return;
        string[] running;
        try
        {
            running = servers.Servers.Where(s => s.State is LanguageServerState.Running or LanguageServerState.Starting or LanguageServerState.Unresponsive)
                .Select(s => s.Id).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; } // shutting down
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
                // An eviction: a session that would have to ask the user anything refuses instead of popping its window up.
                try { await IpcClient.RequestAsync(_host.Runtime.EndpointOf(pid), new IpcMessage { Type = IpcVerbs.Quit, Evict = true }, IpcClient.DefaultConnectTimeout, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
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
        EndWaits();
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
