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
        // GetText(0, Length), not GetText(): the snapshot caches its whole text, and it stays the document's latest one while the user is idle.
        public BackupEntry ToEntry() => new(Id, Uri, Text ?? Snapshot!.GetText(0, Snapshot.Length), Encoding, EndOfLine, LanguageId, DocumentVersion);
    }

    private static readonly BackupWriteResult NothingWritten = new([], [], 0, false);

    /// <summary>Everything that changes a session marks it for autosave: tabs, the active tab, edits of dirty documents, folders, tool windows.</summary>
    private void SubscribeForAutosave()
    {
        if (_subscribed) return;
        _subscribed = true;
        var well = _shell.Well;
        _subs.Add(well.TabsChanged.Subscribe(_ =>
        {
            TrackTabs();
            _autosave.MarkDirty();
        }));
        _subs.Add(well.ActiveChanged.Subscribe(_ => _autosave.MarkDirty()));
        if (_workspace is not null) _subs.Add(_workspace.FoldersChanged.Subscribe(_ => _autosave.MarkDirty()));
        _subs.Add(_shell.Tools.VisibilityChanged.Subscribe(_ => _autosave.MarkDirty()));
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
                _autosave.MarkDirty();
                UpdateNeedsPrompt();
            }));
            // Typing into a dirty document: one volatile write per keystroke (the saver arms its timer once per save).
            store.Add(doc.Changed.Subscribe(_ => { if (doc.IsDirty) _autosave.MarkDirty(); }));
            _tabSubs[tab] = store;
        }
    }

    /// <summary>
    /// The session as it is now (UI thread, cheap): tab list, view states, the layout model, bounds, and a snapshot of each dirty document whose
    /// version changed since its last backup (unchanged ones are only listed as "keep"). No document text is built here: a snapshot shares the
    /// buffer's rope, and its text is materialised on the lane.
    /// </summary>
    internal Captured Capture(bool includeBackups)
    {
        _ui.AssertUiThread();
        var well = _shell.Well;
        var factory = _shell.Factory;
        var groups = factory.DocumentGroups().ToList();
        var editors = new List<SessionEditor>();
        var changed = new List<ChangedText>();
        // Backups of remote documents that wait for their connection are kept as they are.
        var keep = _pendingRemote.Select(b => b.Info.Id).ToList();
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
        var captured = new Captured(Interlocked.Increment(ref _captureSeq), _host.SessionName ?? "", _host.IsUnnamed, _wasResident,
            _workspace?.Folders.Select(f => f.ToString()).ToArray() ?? [], editors, well.ActiveEditor?.Uri.ToString(), _shell.Layout.Capture(), CurrentBounds(),
            _window.IsVisible ? DateTimeOffset.UtcNow : _host.LastShown ?? DateTimeOffset.UtcNow, changed, keep, includeBackups, BackgroundSettings.MaxBackupBytes(_config));
        _lastCaptured = captured;
        UpdateNeedsPrompt();
        return captured;
    }

    /// <summary>
    /// Whether quitting now would have to ask the user (UI thread; answered by hello without the UI thread): dirty documents that cannot be backed
    /// up because <c>files.hotExit</c> is off, the last backup skipped some (over <c>cscode.sessions.maxBackupMb</c>), or the session was written
    /// by a newer CsCode. Such a session is never evicted.
    /// </summary>
    private void UpdateNeedsPrompt()
    {
        if (_disposed) return;
        var dirty = _shell.Well.EditorTabs.Any(t => t.IsDirty);
        var hotExitOff = _config?.Get(BackgroundSettings.HotExit, BackgroundSettings.HotExitOnExitAndWindowClose) == BackgroundSettings.HotExitOff;
        _host.NeedsPrompt = dirty && (hotExitOff || _readOnly || Volatile.Read(ref _skippedBackups) > 0);
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
        var live = new HashSet<string>(c.Keep, StringComparer.Ordinal);
        foreach (var entry in c.Changed.Where(e => result.Written.Contains(e.Id)))
        {
            _backedUp[entry.Id] = entry.DocumentVersion;
            live.Add(entry.Id);
        }
        foreach (var stale in _backedUp.Keys.Where(k => !live.Contains(k)).ToArray()) _backedUp.TryRemove(stale, out _);
        Interlocked.CompareExchange(ref _lastCaptured, null, c);
        return result;
    }

    /// <summary>The autosave: capture on the UI thread, write on the lane.</summary>
    private async Task SaveSnapshotAsync()
    {
        if (_readOnly || _disposed || _host.SessionName is null) return;
        var captured = await _ui.InvokeAsync(() => Capture(includeBackups: true)).ConfigureAwait(false);
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
    private async Task<BackupWriteResult> PersistAsync(bool includeBackups = true)
    {
        var captured = Capture(includeBackups);
        return await _host.Lane.RunAsync(() => Write(captured)).ConfigureAwait(true);
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
