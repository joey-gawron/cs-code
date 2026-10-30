using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Editor.Documents;
using CsCode.Sessions;
using CsCode.Ui.Threading;

namespace CsCode.App.Background;

public sealed partial class SessionWindowController
{
    /// <summary>An immutable snapshot taken on the UI thread; turned into JSON and files on the lane.</summary>
    internal sealed record Captured(
        long Seq,
        string Name,
        bool IsUnnamed,
        bool WasResident,
        IReadOnlyList<string> Folders,
        IReadOnlyList<SessionEditor> Editors,
        string? ActiveEditor,
        LayoutSnapshot Layout,
        SessionWindow? Window,
        DateTimeOffset LastUsed,
        IReadOnlyList<ChangedText> Changed,
        IReadOnlyList<string> Keep,
        bool IncludeBackups,
        long MaxBackupBytes);

    /// <summary>
    /// A dirty document's text to back up, as captured on the UI thread: the document's immutable snapshot (O(1): it shares the rope), whose text
    /// is materialised on the lane. Only documents that are not editor buffers (none in the app) give their text up front.
    /// </summary>
    internal sealed record ChangedText(string Id, string Uri, DocumentSnapshot? Snapshot, string? Text, string Encoding, string EndOfLine, string? LanguageId, int DocumentVersion)
    {
        /// <summary>A remote document's backup that waits for its connection (no tab): written again only under a new session name.</summary>
        public bool IsPendingRemote { get; init; }

        // GetText(0, Length), not GetText(): the snapshot caches its whole text, and it stays the document's latest one while the user is idle.
        public BackupEntry ToEntry() => new(Id, Uri, Text ?? Snapshot!.GetText(0, Snapshot.Length), Encoding, EndOfLine, LanguageId, DocumentVersion);
    }

    private static readonly BackupWriteResult NothingWritten = new([], [], 0, false);

    /// <summary>
    /// Everything that changes a session marks it for autosave: tabs, the active tab, edits of dirty documents, folders, tool windows (and declines a
    /// quit whose close a module holds: <see cref="OnSessionChanged"/>).
    /// </summary>
    private void SubscribeForAutosave()
    {
        if (_subscribed) return;
        _subscribed = true;
        var well = _shell.Well;
        _subs.Add(well.TabsChanged.Subscribe(_ =>
        {
            TrackTabs();
            OnSessionChanged();
        }));
        _subs.Add(well.ActiveChanged.Subscribe(_ => OnSessionChanged()));
        if (_workspace is not null) _subs.Add(_workspace.FoldersChanged.Subscribe(_ => _ui.Post(OnSessionChanged)));
        _subs.Add(_shell.Tools.VisibilityChanged.Subscribe(_ => OnSessionChanged()));
        TrackTabs();
    }

    private void TrackTabs()
    {
        var live = _shell.Well.EditorTabs.ToHashSet();
        foreach (var gone in _tabSubs.Keys.Where(t => !live.Contains(t)).ToArray())
        {
            _tabSubs[gone].Dispose();
            _tabSubs.Remove(gone);
        }
        foreach (var tab in live)
        {
            if (_tabSubs.ContainsKey(tab) || tab.IsDisposed) continue;
            var store = new DisposableStore();
            var doc = tab.Doc;
            store.Add(tab.DirtyStateChanged.Subscribe(_ =>
            {
                OnSessionChanged();
                UpdateNeedsPrompt();
            }));
            // Typing into a dirty document: one volatile write per keystroke (the saver arms its timer once per save).
            store.Add(doc.Changed.Subscribe(_ => { if (doc.IsDirty) OnSessionChanged(); }));
            _tabSubs[tab] = store;
        }
    }

    /// <summary>
    /// The session as it is now (UI thread, cheap): tab list, view states, the layout model, bounds, and a snapshot of each dirty document whose
    /// version changed since its last backup (unchanged ones are only listed as "keep"). No document text is built here: a snapshot shares the
    /// buffer's rope, and its text is materialised on the lane.
    /// </summary>
    internal Captured Capture(bool includeBackups, long? maxBackupBytes = null)
    {
        _ui.AssertUiThread();
        var well = _shell.Well;
        var factory = _shell.Factory;
        var groups = factory.DocumentGroups().ToList();
        var editors = new List<SessionEditor>();
        var changed = new List<ChangedText>();
        var keep = new List<string>();
        var order = 0;
        foreach (var tab in well.EditorTabs)
        {
            var doc = tab.Doc;
            var dirty = tab.IsDirty;
            if (doc.IsUntitled && !dirty) continue; // nothing to restore
            var uri = tab.Uri.ToString();
            string? backupId = null;
            if (dirty && includeBackups)
            {
                backupId = HotExitBackupStore.IdFor(uri);
                if (_backedUp.TryGetValue(backupId, out var version) && version == doc.Version) keep.Add(backupId);
                else
                {
                    // The snapshot is the document's latest published one (published before every change event), so it is at doc.Version.
                    var snapshot = doc is EditorDocument editor ? editor.Snapshot() : null;
                    changed.Add(new ChangedText(backupId, uri, snapshot, snapshot is null ? doc.GetText() : null, doc.EncodingName, doc.EndOfLine, doc.LanguageId,
                        snapshot?.Version ?? doc.Version));
                }
            }
            var group = factory.GroupOf(tab) is { } g ? groups.IndexOf(g) : 0;
            var view = tab.Controller?.GetViewState() ?? tab.SavedViewState;
            editors.Add(new SessionEditor
            {
                Uri = uri, Group = Math.Max(0, group), Order = order++, Pinned = tab.IsPinned, Preview = tab.IsPreview,
                LanguageId = doc.IsUntitled ? doc.LanguageId : null, ViewState = view is null ? null : ToSession(view), BackupId = backupId,
            });
        }
        // Backups of remote documents that wait for their connection are kept as they are (written again only under a new name).
        var remote = PendingRemoteBackups(_host.SessionName ?? "");
        keep.AddRange(remote.Keep.Where(id => !keep.Contains(id) && !changed.Any(c => c.Id == id)));
        changed.AddRange(remote.Write.Where(r => !keep.Contains(r.Id) && !changed.Any(c => c.Id == r.Id)));
        var captured = new Captured(Interlocked.Increment(ref _captureSeq), _host.SessionName ?? "", _host.IsUnnamed, _wasResident,
            _workspace?.Folders.Select(f => f.ToString()).ToArray() ?? [], editors, well.ActiveEditor?.Uri.ToString(), _shell.Layout.Capture(), CurrentBounds(),
            _window.IsVisible ? DateTimeOffset.UtcNow : _host.LastShown ?? DateTimeOffset.UtcNow, changed, keep, includeBackups, maxBackupBytes ?? BackgroundSettings.MaxBackupBytes(_config));
        _lastCaptured = captured;
        UpdateNeedsPrompt();
        return captured;
    }

    /// <summary>
    /// The backups of remote documents still waiting for their connection, as a write of session <paramref name="name"/> must leave them (UI thread):
    /// kept when they are on disk under that name already, otherwise to be written there (after a rename, or in a copy seeded from another
    /// session, whose directory they were read from). They have no tab, so this is the only place that keeps them.
    /// </summary>
    private (IReadOnlyList<ChangedText> Write, IReadOnlyList<string> Keep) PendingRemoteBackups(string name)
    {
        if (_pendingRemote.Count == 0) return ([], []);
        if (string.Equals(Volatile.Read(ref _pendingRemoteOnDisk), name, StringComparison.Ordinal)) return ([], _pendingRemote.Select(b => b.Info.Id).ToArray());
        return (_pendingRemote.Select(b => new ChangedText(b.Info.Id, b.Info.Uri, null, b.Text, b.Info.Encoding, b.Info.EndOfLine, b.Info.LanguageId, b.Info.DocumentVersion)
            { IsPendingRemote = true }).ToArray(), []);
    }

    /// <summary>
    /// Whether quitting now would have to ask the user (UI thread; answered by hello without the UI thread): dirty documents that cannot be backed
    /// up because <c>files.hotExit</c> is off, the last backup skipped some (over <c>cscode.sessions.maxBackupMb</c>), or the session was written
    /// by a newer CsCode; or a close of the window was held by a module's question (the terminal's <c>confirmOnExit</c>). Such a session is never
    /// evicted.
    /// </summary>
    private void UpdateNeedsPrompt()
    {
        if (_disposed) return;
        var dirty = _shell.Well.EditorTabs.Any(t => t.IsDirty);
        var hotExitOff = _config?.Get(BackgroundSettings.HotExit, BackgroundSettings.HotExitOnExitAndWindowClose) == BackgroundSettings.HotExitOff;
        _host.NeedsPrompt = _closeAsks || (dirty && (hotExitOff || _readOnly || Volatile.Read(ref _skippedBackups) > 0));
    }

    private void CaptureBounds()
    {
        if (_window.WindowState == WindowState.Normal && _window.IsVisible)
            _normalBounds = new SessionWindow(_window.Position.X, _window.Position.Y, _window.Width, _window.Height, "normal");
    }

    private SessionWindow CurrentBounds()
    {
        CaptureBounds();
        var state = _window.WindowState switch { WindowState.Maximized => "maximized", WindowState.FullScreen => "fullscreen", _ => "normal" };
        return (_normalBounds ?? new SessionWindow(_window.Position.X, _window.Position.Y, _window.Width, _window.Height, "normal")) with { State = state };
    }

    /// <summary>
    /// Writes a snapshot (lane). Snapshots are written in capture order: an older one arriving late is dropped. Nothing is written once the quit
    /// committed (the lock is about to be released), nor for a session a newer CsCode wrote.
    /// </summary>
    private BackupWriteResult Write(Captured c)
    {
        lock (_writeGate)
        {
            if (c.Seq <= _writtenSeq || string.IsNullOrEmpty(c.Name) || _readOnly || _sealed) return NothingWritten;
            _writtenSeq = c.Seq;
        }
        var doc = new SessionDocument
        {
            Name = c.Name, IsUnnamed = c.IsUnnamed, LastUsedUtc = c.LastUsed, Folders = c.Folders, Editors = c.Editors, ActiveEditor = c.ActiveEditor,
            Layout = LayoutSerializer.Serialize(c.Layout), Window = c.Window, WasResident = c.WasResident,
        };
        _host.Store.Save(doc);
        if (!c.IncludeBackups)
        {
            Interlocked.CompareExchange(ref _lastCaptured, null, c);
            return NothingWritten;
        }
        var result = _host.Store.Backups.Write(c.Name, c.Changed.Select(e => e.ToEntry()).ToArray(), c.Keep, c.MaxBackupBytes);
        Volatile.Write(ref _skippedBackups, result.Skipped.Count);
        // The pending remote backups are under this name now (all of them, or they are written again next time).
        var remote = c.Changed.Where(e => e.IsPendingRemote).ToArray();
        if (remote.Length > 0 && remote.All(e => result.Written.Contains(e.Id))) Volatile.Write(ref _pendingRemoteOnDisk, c.Name);
        var live = new HashSet<string>(c.Keep, StringComparer.Ordinal);
        foreach (var entry in c.Changed.Where(e => !e.IsPendingRemote && result.Written.Contains(e.Id)))
        {
            _backedUp[entry.Id] = entry.DocumentVersion;
            live.Add(entry.Id);
        }
        foreach (var stale in _backedUp.Keys.Where(k => !live.Contains(k)).ToArray()) _backedUp.TryRemove(stale, out _);
        Interlocked.CompareExchange(ref _lastCaptured, null, c);
        return result;
    }

    /// <summary>
    /// The autosave: capture on the UI thread, write on the lane. Not while a quit is under way: it wrote the session itself (before its save prompts
    /// close the tabs: with every tab), and that write stands while the prompts wait and while a module holds the quit's close (as the shell keeps the
    /// layout saved when a close began); an undone quit marks the session for the autosave again.
    /// </summary>
    private async Task SaveSnapshotAsync()
    {
        if (_readOnly || _disposed || _host.SessionName is null) return;
        var captured = await _ui.InvokeAsync(() => _quitting || _disposed ? null : Capture(includeBackups: true)).ConfigureAwait(false);
        if (captured is null) return;
        var result = await _host.Lane.RunAsync(() => Write(captured)).ConfigureAwait(false);
        if (result.OverLimit && !_overLimitWarned)
        {
            _overLimitWarned = true;
            _ui.Post(() => Notify(MessageSeverity.Warning,
                $"Unsaved changes of this session exceed {BackgroundSettings.MaxBackupBytes(_config) / (1024 * 1024)} MB (cscode.sessions.maxBackupMb); " +
                $"{string.Join(", ", result.Skipped.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? EditorTab.TitleFor(x) : u))} are not backed up and will ask to be saved when the session quits."));
        }
    }

    private async Task SaveNowAsync()
    {
        try { await _autosave.SaveAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException) { /* recorded by the saver */ }
    }

    /// <summary>Captures now and writes on the lane (quit).</summary>
    private async Task<BackupWriteResult> PersistAsync(bool includeBackups = true, long? maxBackupBytes = null)
    {
        var captured = Capture(includeBackups, maxBackupBytes);
        return await _host.Lane.RunAsync(() => Write(captured)).ConfigureAwait(true);
    }

    /// <summary>
    /// Before a quit's save prompts (UI thread, then the lane): the session with every tab as it is now, its editors naming no backup (the prompts
    /// decide the unsaved changes, and a quit that goes ahead leaves none), and the backups written all the same, so that SIGTERM or a crash while a
    /// prompt waits still finds the latest unsaved text (the autosave writes nothing while a quit is under way; the prompts' answers drop them).
    /// </summary>
    private async Task PersistBeforePromptsAsync()
    {
        var all = Capture(includeBackups: true);
        var captured = all with { Editors = all.Editors.Select(e => e.BackupId is null ? e : e with { BackupId = null }).ToArray() };
        Volatile.Write(ref _lastCaptured, captured);
        await _host.Lane.RunAsync(() => Write(captured)).ConfigureAwait(true);
    }

    private void QueueWrite(Captured captured) => _ = _host.Lane.RunAsync(() => Write(captured));

    /// <summary>Fatal crash (any thread): writes the last snapshot that was captured but not written yet, synchronously.</summary>
    internal void FlushOnCrash()
    {
        try
        {
            if (Volatile.Read(ref _lastCaptured) is { } pending) Write(pending);
        }
        catch (Exception) { /* the process is going down; the previous autosave is on disk */ }
    }

    private static SessionViewState ToSession(EditorViewState s) => new(s.Caret.Line, s.Caret.Character, s.SelectionStart.Line, s.SelectionStart.Character,
        s.SelectionEnd.Line, s.SelectionEnd.Character, s.VerticalOffset, s.HorizontalOffset, s.FoldedStartLines);

    private static EditorViewState ToEditor(SessionViewState s) => new(new TextPosition(s.CaretLine, s.CaretColumn), new TextPosition(s.SelectionStartLine, s.SelectionStartColumn),
        new TextPosition(s.SelectionEndLine, s.SelectionEndColumn), s.VerticalOffset, s.HorizontalOffset, s.FoldedStartLines ?? [], 0);
}
