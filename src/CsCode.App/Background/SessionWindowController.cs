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
    /// <summary>
    /// The session under whose name the <see cref="_pendingRemote"/> backups are on disk: the restored one, until a rename (or a copy seeded from
    /// another session) makes the next write put them under the new name (written on the lane, read on the UI thread).
    /// </summary>
    private string? _pendingRemoteOnDisk;
    private SessionDocument? _restoredDocument;
    private bool _restoringRemote;
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
    /// <summary>
    /// The save prompts of a quit are up (UI thread): the window is shown and nothing is decided, so a show goes ahead (VS Code opens a file into
    /// the window that is asking; if the quit then goes ahead, the file closes with it).
    /// </summary>
    private bool _asking;
    /// <summary>
    /// A module held the quit's window close with a question of its own (the terminal's <c>confirmOnExit</c>), after every save prompt was answered
    /// (UI thread). Nothing is decided: the window is shown and works, hello says shown, shows go ahead, and the session file stays as the quit wrote
    /// it. The module repeats the close when the user agrees (the quit commits then: <see cref="OnCloseProceeding"/>); the user working on in the
    /// window declines it (<see cref="DeclineHeldQuit"/>): the module says nothing when its question is declined.
    /// </summary>
    private bool _quitHeld;
    /// <summary>
    /// What the quit under way does when the window's close goes ahead (UI thread): null while no quit waits for the window, else whether the save
    /// prompts' answers discard the hot-exit backups (<see cref="CommitQuitAsync"/>).
    /// </summary>
    private bool? _commitOnClose;
    /// <summary>The quit's commit, started as the window's close went ahead; the quit that asked for the close awaits it before it answers.</summary>
    private Task? _commit;
    /// <summary>The window's close went ahead (it closes now): from here on the orderly shutdown ends the process.</summary>
    private volatile bool _windowClosing;
    /// <summary>A close of this window was held by a module's question: quitting would ask, so hello says needsPrompt and nothing evicts it.</summary>
    private bool _closeAsks;
    /// <summary>The quit under way closed the window with the save prompts (false: without, hot exit): a held one starts over the same way.</summary>
    private bool _quitPrompted;

    /// <summary>
    /// How long a quit waits for a module that holds its window's close to ask or let it go ahead (<see cref="MainWindow.SettleHeldCloseAsync"/>):
    /// the terminal's "hasChildProcesses" looks for programs in a few milliseconds; past this the close counts as held by a question.
    /// </summary>
    internal static readonly TimeSpan HeldCloseSettleBound = TimeSpan.FromSeconds(3);
    /// <summary>Completed (and replaced) whenever a quit under way starts asking, commits or is undone: what a show that arrived meanwhile waits for.</summary>
    private TaskCompletionSource _quitPhase = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        window.CloseProceeding += OnCloseProceeding;
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
                return await OnUiAsync(async () =>
                {
                    if (await ClosingForGoodAsync().ConfigureAwait(true)) return IpcMessage.Failure(IpcMessage.QuittingError);
                    await OpenPathsAsync(files).ConfigureAwait(true);
                    return IpcMessage.Reply();
                }).ConfigureAwait(false);
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
        _restoredDocument = doc;
        Volatile.Write(ref _pendingRemoteOnDisk, prepared.BackupSource);
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
        if (_pendingRemote.Count > 0) _ = RestoreRemoteBackupsAsync();
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
    /// keeps them, a rename takes them along, and a quit never discards them: nobody was asked about them). When a document cannot be opened (the
    /// connection failed, the file is gone), its text goes into an untitled buffer of its language, and the user is told which remote file it
    /// belongs to. A quit that begins meanwhile stops this (the rest stay on disk for the next start); a quit that is cancelled starts it again.
    /// </summary>
    private async Task RestoreRemoteBackupsAsync()
    {
        _ui.AssertUiThread();
        if (_restoringRemote) return;
        _restoringRemote = true;
        try
        {
            foreach (var backup in _pendingRemote.ToArray())
            {
                if (_disposed || _quitting) return;
                var uri = new Uri(backup.Info.Uri);
                EditorTab tab;
                string? problem = null;
                try
                {
                    tab = _shell.Well.Find(uri) ?? await _shell.Well.OpenAsync(uri, preview: false, preserveFocus: true).ConfigureAwait(true);
                    // A quit began while the connection came up: its save prompts asked only about the tabs it saw, so the text stays on disk.
                    if (_disposed || _quitting || tab.IsDisposed) return;
                    // Edited meanwhile (the tab came back with the layout): the newer text stays, the backup goes into a buffer of its own.
                    if (tab.IsDirty) problem = "it was edited before they could be restored";
                    else
                    {
                        ApplyBackupText(tab, backup.Text);
                        if (_restoredDocument?.Editors.FirstOrDefault(e => e.Uri == backup.Info.Uri)?.ViewState is { } vs) ApplyViewState(tab, ToEditor(vs));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { problem = ex.Message; }
                if (_disposed || _quitting) return;
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
        finally { _restoringRemote = false; }
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
        if (await ClosingForGoodAsync().ConfigureAwait(true)) return IpcMessage.Failure(IpcMessage.QuittingError);
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

    /// <summary>
    /// Whether this window is closing for good, asked by a request that would show it or open paths in it (UI thread). A quit under way that is not
    /// asking the user (its last write: milliseconds, seconds for huge backups) is awaited first: the request must neither open paths into a window
    /// that is about to close and reply OK, nor be turned away by a quit that is then refused. While the save prompts are up, or a module's
    /// question holds the quit's close, nothing is decided and the window is shown: the request goes ahead (<see cref="_asking"/>,
    /// <see cref="_quitHeld"/>; paths it opens are the user working on, which declines a held quit).
    /// </summary>
    private async Task<bool> ClosingForGoodAsync()
    {
        _ui.AssertUiThread();
        while (_quitting && !_asking && !_quitHeld && !_quitCommitted && !_disposed) await _quitPhase.Task.ConfigureAwait(true);
        return _quitCommitted || _disposed;
    }

    private void QuitPhaseChanged()
    {
        var reached = _quitPhase;
        _quitPhase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reached.TrySetResult();
    }

    /// <summary>Makes the window visible and frontmost; completes once a frame with it was painted.</summary>
    internal async Task ShowWindowAsync()
    {
        _ui.AssertUiThread();
        if ((_quitting && !_asking && !_quitHeld) || _disposed) return;
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
        if (AllClosed() || _quitCommitted || _disposed || !_window.IsVisible) done.TrySetResult();
        _waits.Add(done);
        try { await done.Task.WaitAsync(ct).ConfigureAwait(true); }
        finally { _waits.Remove(done); }
    }

    /// <summary>
    /// The window closed (hidden or closed) or the quit is certain: every pending <c>--wait</c> is answered. Never when a quit only began: one the
    /// user cancels at a save prompt, or one that is refused, leaves the window and the files open, and git must go on waiting (VS Code keeps
    /// waiting when the window's close is cancelled).
    /// </summary>
    private void EndWaits()
    {
        foreach (var wait in _waits.ToArray()) wait.TrySetResult();
    }

    // ------------------------------------------------------------------ closing and quitting

    /// <summary>MainWindow's close hook: true when the close was handled here (hidden, or a quit started).</summary>
    private bool OnCloseRequested(WindowCloseReason reason)
    {
        if (_disposed || _host.IsUnclaimedSpare || !IsActive) return false;
        if (reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            if (_quitting) return false;
            // The desktop session or the app is going down: queue a final save (the lane drains before the process exits) and let it close.
            _wasResident = true;
            QueueWrite(Capture(includeBackups: true));
            return false;
        }
        // A quit under way closes the window itself: its save prompts are up (they answer this close too) or its last write is under way; a second
        // close would close the window behind the quit's back. One whose close a module holds asks again (AskAgainAsync: the close starts over as
        // the quit began it), posted: not inside this close.
        if (_quitting)
        {
            if (_quitHeld) _ui.Post(() => _ = QuitAsync(force: false, windowClose: true));
            return true;
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
    /// <para>The quit commits when the window's close goes ahead (<see cref="OnCloseProceeding"/>), not before: a module may hold the close with a
    /// question of its own (the terminal's <c>confirmOnExit</c>), and then the quit is pending (<see cref="QuitHeldAsync"/>).</para>
    /// </summary>
    internal async Task<IpcMessage> QuitAsync(bool force, bool windowClose = false, bool evict = false, bool terminate = false)
    {
        _ui.AssertUiThread();
        if (_quitting)
        {
            // A quit whose window close a module holds (its question is up): a new quit asks again (AskAgainAsync); a forced quit or an eviction
            // would have to ask, so it is refused. Any other quit under way goes on; SIGTERM too, also during the save prompts or a held quit's
            // question: the host's watchdog writes the last snapshot and ends the process (a held quit's decisions are on disk already).
            if (!_quitHeld || terminate) return IpcMessage.Reply();
            if (force || evict) return IpcMessage.Failure(evict ? NotEvictedAsks : QuitHeldMessage);
            return await AskAgainAsync().ConfigureAwait(true);
        }
        force |= terminate;
        _quitting = true;
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
            // The session's last write is done: the window closes, and the quit commits as it does.
            _commitOnClose = false;
            _quitPrompted = false;
            return await AfterQuitCloseAsync(_window.CloseWithoutPrompt(), previousState, terminate, quiet: (force || evict) && !terminate, evict).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Undo(previousState);
            Notify(MessageSeverity.Error, "The session could not be saved, so it keeps running: " + ex.Message);
            return IpcMessage.Failure("could not save the session: " + ex.Message);
        }
    }

    private const string NotEvicted = "not evicted: quitting would have to ask about unsaved changes";
    private const string NotEvictedAsks = "not evicted: its window asks before it closes (a running terminal)";

    /// <summary>What a quit answers while its window's close waits for the user's answer to a module's question: nothing is decided yet.</summary>
    internal const string QuitHeldMessage = "the quit waits for an answer in the session's window (a running terminal asks before it is ended)";

    /// <summary>
    /// The quit is certain, the session's last write done and the window closing (the host's SIGTERM watchdog leaves the rest to the orderly
    /// shutdown). Not while a module holds the window's close: nothing would end the process then.
    /// </summary>
    internal bool QuitCommitted => _quitCommitted && _windowClosing;

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
        // The session as it is before the prompts close its tabs: what the next start restores when the quit goes ahead (the autosave writes nothing
        // while a quit is under way, nor while a module holds its close), with the unsaved changes backed up until the prompts decide them.
        await PersistBeforePromptsAsync().ConfigureAwait(true);
        _quitPrompted = true;
        ShellController.CloseEnd end;
        _asking = true; // the window that asks is shown (hello says shown meanwhile), and shows that arrive go ahead
        QuitPhaseChanged();
        try
        {
            await ShowWindowAsync().ConfigureAwait(true);
            // Once the prompts are answered, every document was saved or its changes discarded: the autosave's backups of them must not bring
            // "Don't Save" text back next time (VS Code discards its backups after the shutdown prompts too). A newer CsCode's backups are never
            // touched, nor those of remote documents still waiting for their connection (nobody was asked about them).
            _commitOnClose = !_readOnly;
            end = await _window.RequestCloseAsync().ConfigureAwait(true);
        }
        finally { _asking = false; }
        return await AfterQuitCloseAsync(end, previousState).ConfigureAwait(true);
    }

    /// <summary>
    /// A new quit, or the user's close of the window, while a module holds a quit's close with its question: the close starts over as the quit began
    /// it, and the module asks again (its earlier question is withdrawn). A hot-exit quit closes again without prompts: everything was backed up as
    /// it began and nothing changed since (any change declines the held quit), as VS Code's hot exit closes again without asking to save. A quit with
    /// save prompts asks them again for anything dirty since (normally nothing: its prompts closed every tab), and their answers decide the backups.
    /// </summary>
    private async Task<IpcMessage> AskAgainAsync()
    {
        _quitHeld = false; // a quit under way again: its prompts, or its close
        _window.ForgetHeldClose();
        ShellController.CloseEnd end;
        if (_quitPrompted)
        {
            _asking = true;
            QuitPhaseChanged();
            try
            {
                _commitOnClose = !_readOnly;
                end = await _window.RequestCloseAsync().ConfigureAwait(true);
            }
            finally { _asking = false; }
        }
        else
        {
            _commitOnClose = false;
            end = _window.CloseWithoutPrompt();
        }
        return await AfterQuitCloseAsync(end, ProcessStates.Shown).ConfigureAwait(true);
    }

    /// <summary>
    /// How the quit's window close ended: the window closed (the quit committed as it went ahead), a save prompt was cancelled (the quit is undone),
    /// or a module holds the close. A held close is settled first (<see cref="MainWindow.SettleHeldCloseAsync"/>): the module may only be finding
    /// out whether to ask, and then lets the close go ahead without a word; the quit is decided once it asks (<see cref="QuitHeldAsync"/>) or the
    /// close goes ahead. Meanwhile it is a quit under way: a user's close is ignored, a show waits, the autosave writes nothing.
    /// <paramref name="quiet"/>: a forced quit or an eviction, which may not ask anyone.
    /// </summary>
    private async Task<IpcMessage> AfterQuitCloseAsync(ShellController.CloseEnd end, string previousState, bool terminate = false, bool quiet = false, bool evict = false)
    {
        var asks = false;
        if (end == ShellController.CloseEnd.Held)
        {
            var settled = await _window.SettleHeldCloseAsync(HeldCloseSettleBound).ConfigureAwait(true);
            if (settled == MainWindow.HeldCloseState.Closed || _windowClosing) end = ShellController.CloseEnd.Closed;
            asks = settled == MainWindow.HeldCloseState.Asking;
        }
        switch (end)
        {
            case ShellController.CloseEnd.Closed:
                if (_commit is { } commit) await commit.ConfigureAwait(true);
                return IpcMessage.Reply();
            case ShellController.CloseEnd.Cancelled:
                Undo(_window.IsVisible ? ProcessStates.Shown : previousState);
                return IpcMessage.Failure("the quit was cancelled");
            default:
                return await QuitHeldAsync(previousState, terminate, quiet, evict, asks).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// A module holds the quit's window close with a question of its own (the terminal's <c>confirmOnExit</c>), asked after the save prompts
    /// (<paramref name="asks"/>; false: the module neither asked nor let the close go ahead in time, which counts the same). It is not a cancel: the
    /// module repeats the close when the user agrees, and the quit commits then (<see cref="OnCloseProceeding"/>). Until then the quit is pending:
    /// the window is shown and works (a hidden one is shown, so the question can be answered), and the session file stays as the quit wrote it, with
    /// every tab (as the shell keeps the layout saved when the close began). The module says nothing when the user declines: working on in the
    /// window declines it (<see cref="OnSessionChanged"/>). A forced quit or an eviction is refused instead (nobody may be asked: the question is
    /// withdrawn and the window stays as it was); SIGTERM leaves the process to the host's watchdog (nobody is there to answer, and everything is
    /// saved).
    /// </summary>
    private async Task<IpcMessage> QuitHeldAsync(string previousState, bool terminate, bool quiet, bool evict, bool asks)
    {
        if (asks) _closeAsks = true; // the next eviction would have to ask too
        if (terminate)
        {
            UpdateNeedsPrompt();
            return IpcMessage.Failure(QuitHeldMessage);
        }
        if (quiet)
            return Refuse(previousState, evict ? NotEvictedAsks : $"session '{_host.SessionName}' asks before its window closes (a running terminal); not quitting");
        _quitHeld = true;
        // Every save prompt was answered and every tab is closed, whatever the answer to the question: the backups of what was saved or discarded
        // go now, so that neither a crash nor SIGTERM meanwhile brings "Don't Save" text back.
        if (_commitOnClose == true && DiscardBackupsJob() is { } discard) _ = DiscardNowAsync(discard);
        UpdateNeedsPrompt();
        if (_window.IsVisible) _host.SetState(ProcessStates.Shown);
        else await ShowWindowAsync().ConfigureAwait(true);
        QuitPhaseChanged();
        return IpcMessage.Failure(QuitHeldMessage);
    }

    /// <summary>
    /// The window closes for good (UI thread, inside its close): the quit that asked for the close commits now, also one whose close a module held
    /// and the user then agreed to. Synchronous up to the lane: the commit's last job is queued before the window is gone.
    /// </summary>
    private void OnCloseProceeding()
    {
        _windowClosing = true;
        if (_quitCommitted || _disposed || _commitOnClose is not { } discardBackups) return;
        _commit = CommitQuitAsync(discardBackups);
    }

    /// <summary>
    /// Something the session saves changed (UI thread): the autosave runs, and a quit whose close a module holds is declined (the user works on in
    /// the window; the module says nothing when its question is declined). Changes the window's own close makes do not count.
    /// </summary>
    private void OnSessionChanged()
    {
        if (_quitHeld && !_quitCommitted && !_disposed && !_shell.IsClosing) DeclineHeldQuit();
        _autosave.MarkDirty();
    }

    /// <summary>The held quit is undone: the session is saved as it is now (the tabs the save prompts closed stay closed, as the window shows).</summary>
    private void DeclineHeldQuit() => Undo(_window.IsVisible ? ProcessStates.Shown : ProcessStates.Hidden);

    /// <summary>
    /// The quit is certain (the window closes now, or a spare's or a still-starting session's next): no write of this session may follow, so it is
    /// sealed and its lock released now, before the language servers and the rest shut down (seconds, with a slow server). Hello says quitting from
    /// here on (a quit with save prompts showed the window, which made it say shown), so a launch of the session meanwhile starts it afresh, instead
    /// of showing a window that is closing or taking this process for a wedged owner and opening a copy. Pending <c>--wait</c>s are answered: the
    /// window closes. <paramref name="discardBackups"/>: after the save prompts. Synchronous until the lane job is queued.
    /// </summary>
    private async Task CommitQuitAsync(bool discardBackups = false)
    {
        _quitCommitted = true;
        _quitHeld = false;
        _commitOnClose = null;
        _host.SetState(ProcessStates.Quitting);
        EndWaits();
        QuitPhaseChanged();
        var discard = discardBackups ? DiscardBackupsJob() : null;
        try
        {
            // On the lane, after any write queued before: it is the last thing done to the session's files. Not back to the UI thread: the window
            // may be gone, and nothing here needs it.
            await _host.Lane.RunAsync(() =>
            {
                lock (_writeGate) _sealed = true;
                discard?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            lock (_writeGate) _sealed = true;
        }
        finally { _host.ReleaseSessionLock(); }
    }

    /// <summary>
    /// The lane job that drops the session's hot-exit backups after the save prompts, prepared on the UI thread (null: nothing to drop). Only the
    /// backups of remote documents that wait for their connection stay (written under this name if they are not yet); a newer CsCode's are never
    /// touched.
    /// </summary>
    private Action? DiscardBackupsJob()
    {
        if (_host.SessionName is not { } name || _readOnly) return null;
        var remote = PendingRemoteBackups(name);
        return () =>
        {
            if (remote.Write.Count == 0 && remote.Keep.Count == 0) _host.Store.Backups.DeleteAll(name);
            else _host.Store.Backups.Write(name, remote.Write.Select(e => e.ToEntry()).ToArray(), remote.Keep, long.MaxValue);
            _backedUp.Clear(); // none of them is on disk any more: a later write backs every dirty document up again
        };
    }

    /// <summary>Drops the backups now (a held quit), unless the session is sealed meanwhile; the commit drops them again if this fails.</summary>
    private async Task DiscardNowAsync(Action discard)
    {
        try
        {
            await _host.Lane.RunAsync(() =>
            {
                lock (_writeGate)
                {
                    if (_sealed) return;
                }
                discard();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { SessionTrace.Mark("host: backups not discarded: " + ex.Message); }
    }

    private void Undo(string state)
    {
        _quitting = false;
        _quitHeld = false;
        _commitOnClose = null;
        _window.ForgetHeldClose(); // a close a module still holds no longer stands for a quit: its question is withdrawn, the next close is a new one
        _wasResident = true;
        _host.SetState(state);
        _autosave.MarkDirty();
        UpdateNeedsPrompt();
        QuitPhaseChanged();
        if (_pendingRemote.Count > 0 && !_disposed) _ = RestoreRemoteBackupsAsync(); // the quit stopped it
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
        _window.CloseProceeding -= OnCloseProceeding;
        _disposed = true;
        EndWaits();
        QuitPhaseChanged();
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
