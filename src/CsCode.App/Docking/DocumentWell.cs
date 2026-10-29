using Avalonia.Controls;
using Avalonia.Threading;
using CsCode.App.Notifications;
using CsCode.App.Services;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Ui.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Dock.Model.Mvvm.Controls;

namespace CsCode.App.Docking;

/// <summary>
/// The document well's behaviour on top of the Dock model: open / preview / pin / close (with Save-Don't Save-Cancel prompts) /
/// close others / saved / all / reopen closed / split / MRU order, plus the memory policy (only the active tab of each group and the
/// last <c>cscode.shell.liveEditorViews</c> tabs keep a live editor view). The Dock model is the single source of truth for which tabs exist,
/// their order and their groups (so Dock's own drag-reorder, split and float just work); this class adds the semantics Dock does not have.
/// UI-thread object.
/// </summary>
public sealed class DocumentWell : IDisposable
{
    public const string LiveViewsSetting = "cscode.shell.liveEditorViews";
    public const int DefaultLiveViews = 8;
    private const int MaxClosedHistory = 20;

    private readonly ShellDockFactory _factory;
    private readonly IEditorViewFactory _editors;
    private readonly IModalDialogs _dialogs;
    private readonly IWindowService? _windows;
    private readonly IConfigurationService? _config;
    private readonly FileParticipants? _participants;
    private readonly DisposableStore _subs = new();
    private readonly List<ShellDocument> _mru = new();
    private readonly LinkedList<Uri> _closed = new();
    private readonly Dictionary<string, ShellDocument> _singletons = new(StringComparer.Ordinal);
    private readonly Emitter<ShellDocument?> _activeChanged = new();
    private readonly Emitter<object?> _tabsChanged = new();
    private ShellDocument? _active;

    private readonly IUiDispatcher? _ui;

    public DocumentWell(ShellDockFactory factory, IEditorViewFactory editors, IModalDialogs dialogs, IWindowService? windows = null, IConfigurationService? config = null, IUiDispatcher? ui = null,
        FileParticipants? participants = null)
    {
        _ui = ui;
        _participants = participants;
        _factory = factory;
        _editors = editors;
        _dialogs = dialogs;
        _windows = windows;
        _config = config;
        factory.ActiveDockableChanged += OnActiveDockableChanged;
        factory.DockableClosing += OnClosing;
        factory.DockableClosed += OnClosed;
        _subs.Add(Disposable.Create(() =>
        {
            factory.ActiveDockableChanged -= OnActiveDockableChanged;
            factory.DockableClosing -= OnClosing;
            factory.DockableClosed -= OnClosed;
        }));
    }

    /// <summary>Currently active document tab (editor or not), or null.</summary>
    public ShellDocument? Active => _active;
    public EditorTab? ActiveEditor => _active as EditorTab;
    public IEventSource<ShellDocument?> ActiveChanged => _activeChanged;
    /// <summary>Raised after tabs were added, removed or reordered.</summary>
    public IEventSource<object?> TabsChanged => _tabsChanged;

    // A tab that was disposed is closed even if Dock has not finished removing it yet (DockableClosed fires mid-removal).
    public IReadOnlyList<ShellDocument> Tabs => _factory.DocumentGroups().SelectMany(g => g.VisibleDockables!.OfType<ShellDocument>()).Where(t => !t.IsDisposed).ToArray();
    public IReadOnlyList<EditorTab> EditorTabs => Tabs.OfType<EditorTab>().ToArray();
    /// <summary>Most recently used first (Ctrl+Tab order).</summary>
    public IReadOnlyList<ShellDocument> MruOrder => _mru.ToArray();
    public int ClosedHistoryCount => _closed.Count;

    public EditorTab? Find(Uri uri) => EditorTabs.FirstOrDefault(t => t.Uri == uri);

    // ---------------------------------------------------------------- open

    public async ValueTask<EditorTab> OpenAsync(Uri uri, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        if (Find(uri) is { } existing)
        {
            if (!preview && existing.IsPreview) existing.IsPreview = false; // opening for real promotes a preview tab
            Activate(existing, preserveFocus);
            return existing;
        }
        var doc = await _editors.OpenDocumentAsync(uri, ct);
        // Another open for the same uri may have completed while we awaited the load.
        if (Find(uri) is { } raced)
        {
            Activate(raced, preserveFocus);
            return raced;
        }
        return Add(new EditorTab(doc, _editors), preview, preserveFocus);
    }

    public EditorTab OpenUntitled(string? languageId = null) => Add(new EditorTab(_editors.CreateUntitled(languageId), _editors), preview: false, preserveFocus: false);

    /// <summary>Opens (or activates) a non-file document tab such as the Welcome page. Singletons are keyed by <paramref name="id"/>.</summary>
    public ShellDocument OpenView(string id, string title, Func<Control> factory, string? iconKey = null)
    {
        if (_singletons.TryGetValue(id, out var existing) && Tabs.Contains(existing))
        {
            Activate(existing, preserveFocus: false);
            return existing;
        }
        var tab = new ShellDocument { Id = "view:" + id, Title = title, ViewFactory = factory, CanFloat = true };
        _singletons[id] = tab;
        var group = ActiveGroup();
        _factory.AddDockable(group, tab);
        _factory.SetActiveDockable(tab);
        group.ActiveDockable = tab;
        OnActive(tab);
        _tabsChanged.Fire(null);
        return tab;
    }

    private EditorTab Add(EditorTab tab, bool preview, bool preserveFocus)
    {
        tab.Owned.Add(tab.DirtyStateChanged.Subscribe(_ => _tabsChanged.Fire(null))); // title / status follow the dirty flag
        var group = ActiveGroup();
        tab.IsPreview = preview;
        if (preview)
        {
            // A group has at most one preview tab: the new preview replaces the old one in place.
            var old = group.VisibleDockables!.OfType<EditorTab>().FirstOrDefault(t => t.IsPreview && !t.IsDirty && !t.IsPinned);
            if (old is not null)
            {
                var index = group.VisibleDockables!.IndexOf(old);
                old.CloseConfirmed = true;
                RemoveTab(old, remember: false);
                _factory.InsertDockable(group, tab, Math.Min(index, group.VisibleDockables!.Count));
            }
            else _factory.AddDockable(group, tab);
        }
        else
        {
            _factory.AddDockable(group, tab);
        }
        Activate(tab, preserveFocus);
        _tabsChanged.Fire(null);
        return tab;
    }

    private DocumentDock ActiveGroup()
    {
        if (_active is not null && _factory.GroupOf(_active) is { } g) return g;
        return _factory.DocumentGroups().FirstOrDefault() ?? _factory.PrimaryDocuments;
    }

    public void Activate(ShellDocument tab, bool preserveFocus = false)
    {
        var group = _factory.GroupOf(tab);
        if (group is null) return;
        group.ActiveDockable = tab;
        _factory.SetActiveDockable(tab);
        if (!preserveFocus) _factory.SetFocusedDockable(group, tab);
        OnActive(tab);
    }

    // ---------------------------------------------------------------- close

    public async ValueTask<bool> CloseAsync(ShellDocument tab, bool prompt = true)
    {
        if (!Tabs.Contains(tab)) return true;
        if (tab is EditorTab et)
        {
            if (prompt && et.IsDirty && !await ConfirmCloseAsync(et)) return false;
            et.CloseConfirmed = true;
        }
        RemoveTab(tab, remember: true);
        return true;
    }

    public async ValueTask<bool> CloseOthersAsync(ShellDocument keep)
    {
        foreach (var t in Tabs.Where(t => !ReferenceEquals(t, keep) && !IsPinned(t)).ToArray())
            if (!await CloseAsync(t)) return false;
        return true;
    }

    public async ValueTask<bool> CloseSavedAsync()
    {
        foreach (var t in Tabs.Where(t => t is not EditorTab { IsDirty: true } && !IsPinned(t)).ToArray())
            if (!await CloseAsync(t, prompt: false)) return false;
        return true;
    }

    public async ValueTask<bool> CloseAllAsync(bool includePinned = true)
    {
        foreach (var t in Tabs.Where(t => includePinned || !IsPinned(t)).ToArray())
            if (!await CloseAsync(t)) return false;
        return true;
    }

    public async ValueTask<bool> CloseToTheRightAsync(ShellDocument of)
    {
        var group = _factory.GroupOf(of);
        if (group is null) return true;
        var tabs = group.VisibleDockables!.OfType<ShellDocument>().ToList();
        foreach (var t in tabs.Skip(tabs.IndexOf(of) + 1).Where(t => !IsPinned(t)).ToArray())
            if (!await CloseAsync(t)) return false;
        return true;
    }

    private static bool IsPinned(ShellDocument t) => t is EditorTab { IsPinned: true };

    /// <summary>
    /// Saves a tab: Save As (a dialog) for untitled buffers or when <paramref name="saveAs"/> is set, then the will-save participants (language servers'
    /// format-on-save edits; bounded, docs/LSP.md), then the editor writes the file. Every save path of the shell goes through here.
    /// Returns false when the user cancelled the Save As dialog. UI thread.
    /// </summary>
    public async ValueTask<bool> SaveAsync(EditorTab tab, bool saveAs = false)
    {
        Uri? target = null;
        if (saveAs || tab.Doc.IsUntitled)
        {
            if (_windows is null) return false;
            target = await _windows.ShowSaveDialogAsync(new SaveDialogOptions(Title: saveAs ? "Save As" : "Save " + tab.Title));
            if (target is null) return false;
        }
        var doc = tab.Doc;
        if (_participants is not null) await _participants.WillSaveAsync(doc, DocumentSaveReason.Manual);
        if (target is not null) await doc.SaveAsAsync(target);
        else await doc.SaveAsync();
        return true;
    }

    /// <summary>Save / Don't Save / Cancel. Returns true if the tab may be closed.</summary>
    public async ValueTask<bool> ConfirmCloseAsync(EditorTab tab)
    {
        var answer = await _dialogs.ShowAsync(
            "Save changes",
            $"Do you want to save the changes you made to {tab.Title}?\n\nYour changes will be lost if you don't save them.",
            ["Save", "Don't Save", "Cancel"], defaultIndex: 0);
        switch (answer)
        {
            case "Save":
                try
                {
                    return await SaveAsync(tab);
                }
                catch (Exception ex)
                {
                    if (_windows is not null) await _windows.ShowMessageAsync(MessageSeverity.Error, $"Could not save {tab.Title}: {ex.Message}");
                    return false;
                }
            case "Don't Save":
                return true;
            default:
                return false;
        }
    }

    private void RemoveTab(ShellDocument tab, bool remember)
    {
        if (remember && tab is EditorTab { Doc.IsUntitled: false } et)
        {
            _closed.Remove(et.Uri);
            _closed.AddFirst(et.Uri);
            while (_closed.Count > MaxClosedHistory) _closed.RemoveLast();
        }
        tab.WasActiveOnClose = ReferenceEquals(_active, tab);
        tab.ReleaseFocus(); // while the view is still in the window: a focused editor that Dock detached stays reachable
        _suppressActive = true; // Dock activates its own neighbour while closing; that must not reorder the MRU list
        try
        {
            _factory.CloseDockable(tab);
            // Dock fires DockableClosed synchronously; if a host variant did not, make sure we still clean up.
            if (Tabs.Contains(tab)) _factory.RemoveDockable(tab, collapse: true);
            CleanUp(tab);
        }
        finally { _suppressActive = false; }
        ReassertMru(); // the user-visible rule is "most recently used", not Dock's neighbour
    }

    private bool _reassertMru;
    private bool _suppressActive;

    private void ReassertMru()
    {
        if (!_reassertMru) return;
        _reassertMru = false;
        var live = Tabs;
        if (_mru.FirstOrDefault(live.Contains) is { } next && !ReferenceEquals(_active, next)) Activate(next, preserveFocus: true);
    }

    public async ValueTask<bool> ReopenClosedAsync()
    {
        while (_closed.First is { } node)
        {
            _closed.RemoveFirst();
            if (Find(node.Value) is not null) continue;
            try
            {
                await OpenAsync(node.Value);
                return true;
            }
            catch (IOException)
            {
                // file vanished since it was closed: try the next one
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- pin / split

    public void Pin(EditorTab tab)
    {
        tab.IsPinned = true;
        tab.IsPreview = false;
        MoveToPinnedRegion(tab);
        _tabsChanged.Fire(null);
    }

    public void Unpin(EditorTab tab)
    {
        tab.IsPinned = false;
        _tabsChanged.Fire(null);
    }

    public void Promote(EditorTab tab) => tab.IsPreview = false;

    private void MoveToPinnedRegion(EditorTab tab)
    {
        if (_factory.GroupOf(tab) is not { } group) return;
        var list = group.VisibleDockables!;
        var target = list.OfType<EditorTab>().Count(t => t.IsPinned && !ReferenceEquals(t, tab));
        var current = list.IndexOf(tab);
        if (current == target) return;
        var wasActive = ReferenceEquals(group.ActiveDockable, tab);
        list.RemoveAt(current);
        list.Insert(Math.Min(target, list.Count), tab);
        if (wasActive) group.ActiveDockable = tab;
    }

    public void SplitRight(ShellDocument tab) => _factory.NewHorizontalDocumentDock(tab);
    public void SplitDown(ShellDocument tab) => _factory.NewVerticalDocumentDock(tab);

    /// <summary>Moves a tab into the next document group (wraps), as "Move to next group".</summary>
    public bool MoveToNextGroup(ShellDocument tab)
    {
        var groups = _factory.DocumentGroups();
        if (groups.Count < 2 || _factory.GroupOf(tab) is not { } from) return false;
        var to = groups[(groups.ToList().IndexOf(from) + 1) % groups.Count];
        _factory.MoveDockable(from, to, tab, null);
        _tabsChanged.Fire(null);
        return true;
    }

    // ---------------------------------------------------------------- MRU switcher (Ctrl+Tab)

    /// <summary>The tab Ctrl+Tab would switch to after <paramref name="steps"/> presses (1 = previous). Does not activate.</summary>
    public ShellDocument? PeekMru(int steps)
    {
        if (_mru.Count == 0) return null;
        var n = _mru.Count;
        var i = ((steps % n) + n) % n;
        return _mru[i];
    }

    // ---------------------------------------------------------------- events from Dock

    private void OnActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e)
    {
        if (e.Dockable is ShellDocument d) OnActive(d, fromDock: true);
    }

    private void OnClosing(object? sender, DockableClosingEventArgs e)
    {
        if (e.Dockable is ShellDocument closing && !e.Cancel && !_suppressActive) closing.WasActiveOnClose = ReferenceEquals(_active, closing);
        // The user clicked a tab's close button. A dirty tab needs an async prompt, so cancel Dock's close and run ours.
        if (e.Dockable is EditorTab { CloseConfirmed: false, IsDirty: true } tab)
        {
            e.Cancel = true;
            _ = CloseAfterPromptAsync(tab);
        }
        else if (e.Dockable is ShellDocument closingNow && !e.Cancel) closingNow.ReleaseFocus(); // before Dock detaches the view (see RemoveTab)
    }

    private async Task CloseAfterPromptAsync(EditorTab tab)
    {
        try { await CloseAsync(tab); }
        catch (Exception) { /* prompt failures must not crash the UI; the tab simply stays open */ }
    }

    private void OnClosed(object? sender, DockableClosedEventArgs e)
    {
        if (e.Dockable is not ShellDocument tab) return;
        CleanUp(tab);
        if (_suppressActive) return; // programmatic close: RemoveTab finishes the job
        // Dock-initiated close (the tab's X button): Dock picks the neighbour after this event returns, so keep ignoring its activation
        // until the next UI turn, then re-assert the MRU choice.
        if (_ui is null) { _suppressActive = false; return; }
        _suppressActive = true;
        _ui.Post(() => { _suppressActive = false; ReassertMru(); });
    }

    private void OnActive(ShellDocument tab, bool fromDock = false)
    {
        if (fromDock && _suppressActive) return; // Dock's own neighbour pick while a tab is closing
        _mru.Remove(tab);
        _mru.Insert(0, tab);
        if (ReferenceEquals(_active, tab)) return;
        _active = tab;
        EnforceViewBudget();
        _activeChanged.Fire(tab);
    }

    private void CleanUp(ShellDocument tab)
    {
        if (tab.IsDisposed) return; // Dock's DockableClosed and RemoveTab both end up here
        // Dock may already have activated its own neighbour; the user-visible rule is "most recently used", so we re-pick.
        var wasActive = tab.WasActiveOnClose || ReferenceEquals(_active, tab);
        _mru.Remove(tab);
        var doc = (tab as EditorTab)?.Doc;
        tab.Dispose();
        if (doc is not null && !EditorTabs.Any(t => ReferenceEquals(t.Doc, doc)))
            _ = CloseDocumentAsync(doc);
        if (tab is not EditorTab) _singletons.Where(kv => ReferenceEquals(kv.Value, tab)).Select(kv => kv.Key).ToList().ForEach(k => _singletons.Remove(k));
        if (wasActive)
        {
            var live = Tabs;
            var next = _mru.FirstOrDefault(live.Contains);
            if (next is not null)
            {
                _active = null;
                Activate(next, preserveFocus: true);
                _reassertMru = true;
                if (tab.HadKeyboardFocus) FocusWhenShown(next);
            }
            else { _active = null; _activeChanged.Fire(null); }
        }
        _tabsChanged.Fire(null);
    }

    /// <summary>
    /// Visual Studio: closing the focused document puts the keyboard into the document that takes its place. Dock presents it after this turn, so
    /// wait (bounded) until its editor is in the window. Nothing happens if the user focused something else meanwhile.
    /// </summary>
    private void FocusWhenShown(ShellDocument next, int attempt = 0)
    {
        if (next is not EditorTab editor || editor.IsDisposed || !ReferenceEquals(_active, editor)) return;
        if (editor.LiveView is { } view && TopLevel.GetTopLevel(view) is { } top && editor.Controller is { } controller)
        {
            if (top.FocusManager?.GetFocusedElement() is null) controller.Focus();
            return;
        }
        if (attempt < 20) DispatcherTimer.RunOnce(() => FocusWhenShown(next, attempt + 1), TimeSpan.FromMilliseconds(25));
    }

    private async Task CloseDocumentAsync(IEditorDocument doc)
    {
        try { await _editors.CloseDocumentAsync(doc); }
        catch (Exception) { /* editor-side cleanup failure is not actionable by the shell */ }
    }

    // ---------------------------------------------------------------- memory policy

    /// <summary>Disposes live views of tabs that are neither the active tab of a group nor within the LRU budget.</summary>
    public void EnforceViewBudget()
    {
        var budget = Math.Max(1, _config?.Get(LiveViewsSetting, DefaultLiveViews) ?? DefaultLiveViews);
        var keep = new HashSet<ShellDocument>();
        foreach (var g in _factory.DocumentGroups())
            if (g.ActiveDockable is ShellDocument a) keep.Add(a);
        foreach (var t in _mru)
        {
            if (keep.Count >= budget + _factory.DocumentGroups().Count) break;
            keep.Add(t);
        }
        foreach (var t in Tabs)
            if (!keep.Contains(t) && t.LiveView is not null) t.DisposeView();
    }

    public void Dispose()
    {
        _subs.Dispose();
        foreach (var t in Tabs) t.Dispose();
        _mru.Clear();
        _activeChanged.Clear();
        _tabsChanged.Clear();
    }
}
