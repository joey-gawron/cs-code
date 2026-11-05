using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>
    /// An untitled editor holding <paramref name="text"/> (the search editor's "Search: query" tabs): titled <paramref name="title"/> (null: Untitled-N),
    /// Save As proposing <paramref name="suggestedSaveUri"/>, in the group beside the active one when <paramref name="toSide"/>. UI thread.
    /// </summary>
    public EditorTab OpenUntitled(string languageId, string text, string? title, Uri? suggestedSaveUri, bool toSide)
    {
        var tab = Add(new EditorTab(_editors.CreateUntitled(languageId, text), _editors), preview: false, preserveFocus: false);
        tab.SetTitle(title);
        tab.SuggestedSaveUri = suggestedSaveUri;
        if (toSide && !MoveToNextGroup(tab)) SplitRight(tab);
        _tabsChanged.Fire(null);
        return tab;
    }

    /// <summary>Opens <paramref name="uri"/> in the group beside the active tab's (a new group on the right when there is one group): VS Code's "to the side".</summary>
    public async ValueTask<EditorTab> OpenToSideAsync(Uri uri, CancellationToken ct = default)
    {
        var from = _active;
        var tab = await OpenAsync(uri, preview: false, preserveFocus: false, ct);
        if (from is not null && !ReferenceEquals(from, tab) && ReferenceEquals(_factory.GroupOf(from), _factory.GroupOf(tab)))
        {
            if (!MoveToNextGroup(tab)) SplitRight(tab);
            Activate(tab);
        }
        return tab;
    }

    /// <summary>Retitles an editor tab and what its Save As proposes (the search editor's tab follows its query). UI thread.</summary>
    public void SetTitle(EditorTab tab, string? title, Uri? suggestedSaveUri)
    {
        tab.SetTitle(title);
        tab.SuggestedSaveUri = suggestedSaveUri;
        _tabsChanged.Fire(null);
    }

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

    /// <summary>
    /// Makes <paramref name="tab"/> the active tab of its group and of the well. Unless <paramref name="preserveFocus"/>, its editor also takes the
    /// keyboard once Dock presents it, as in VS Code and Visual Studio, when the keyboard was in the editor it replaces or nowhere (the view that had
    /// it left the window, or a closed palette had it): a preview opened from the Explorer leaves the keyboard in the Explorer.
    /// </summary>
    public void Activate(ShellDocument tab, bool preserveFocus = false)
    {
        var group = _factory.GroupOf(tab);
        if (group is null) return;
        var previous = _active;
        group.ActiveDockable = tab;
        _factory.SetActiveDockable(tab);
        if (!preserveFocus)
        {
            _factory.SetFocusedDockable(group, tab);
            _factory.WindowOf(tab)?.SetActive(); // a floating document's window comes to the front
        }
        OnActive(tab);
        if (!preserveFocus) FocusWhenShown(tab, takeFrom: previous);
    }

    /// <summary>
    /// Puts the keyboard into the active editor once it is shown, unless something else has it by then (a palette or prompt that closed leaves it
    /// nowhere; see <see cref="Views.OverlayFocus"/>). <paramref name="mayTakeFrom"/>: what else it may be taken from when the editor is shown (the
    /// palette: where it left the keyboard, while the user has not pressed anything since).
    /// </summary>
    public void FocusActiveEditor(Func<IInputElement, bool>? mayTakeFrom = null)
    {
        if (_active is { } active) FocusWhenShown(active, mayTakeFrom: mayTakeFrom);
    }

    // ---------------------------------------------------------------- floating

    /// <summary>Visual Studio's Float: the tab moves into a floating window of its own (Dock.Avalonia host window, owned by the main window).</summary>
    public void Float(ShellDocument tab)
    {
        if (!Tabs.Contains(tab) || _factory.IsFloating(tab)) return;
        _factory.Float(tab);
        Activate(tab);
        _tabsChanged.Fire(null);
    }

    /// <summary>Visual Studio's Dock as Tabbed Document: a floating tab goes back to the group it came from (else the primary group).</summary>
    public void DockBack(ShellDocument tab)
    {
        if (!_factory.DockBack(tab)) return;
        Activate(tab);
        _tabsChanged.Fire(null);
    }

    /// <summary>Whether the tab is shown in a floating window.</summary>
    public bool IsFloating(ShellDocument tab) => _factory.IsFloating(tab);

    // ---------------------------------------------------------------- close

    public async ValueTask<bool> CloseAsync(ShellDocument tab, bool prompt = true)
    {
        if (!Tabs.Contains(tab)) return true;
        if (tab is EditorTab et)
        {
            // a merge editor still showing the document asks when it closes
            if (prompt && et.IsDirty && !_attached.Values.Any(a => ReferenceEquals(a.Doc, et.Doc)) && !await ConfirmCloseAsync(et)) return false;
            et.CloseConfirmed = true;
        }
        else if (_attached.TryGetValue(tab, out var attached) && prompt && IsUnsavedHere(attached) && !ShownByOtherTab(tab, attached.Doc))
        {
            if (!await ConfirmCloseAsync(attached)) return false;
            attached.Decided = true; // saved, or "Don't Save": the document goes with the tab
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
        foreach (var t in Tabs.Where(t => t is not EditorTab { IsDirty: true } && !IsPinned(t) && !(_attached.TryGetValue(t, out var a) && IsUnsavedHere(a))).ToArray())
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
            target = await _windows.ShowSaveDialogAsync(new SaveDialogOptions(Title: saveAs ? "Save As" : "Save " + tab.Title, DefaultUri: tab.Doc.IsUntitled ? tab.SuggestedSaveUri : null));
            if (target is null) return false;
        }
        var doc = tab.Doc;
        if (_participants is not null) await _participants.WillSaveAsync(doc, DocumentSaveReason.Manual);
        if (target is not null)
        {
            await doc.SaveAsAsync(target);
            // the tab follows the document to its new name (an untitled search editor saved as query.code-search shows that name)
            if (!tab.IsDisposed && doc.Uri != tab.Uri && Find(doc.Uri) is null)
            {
                tab.Retarget(doc.Uri);
                _tabsChanged.Fire(null);
            }
        }
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

    // ---------------------------------------------------------------- files that moved (Explorer rename, move, drag and drop)

    /// <summary>
    /// <paramref name="from"/> (a file, or a folder and everything in it) was moved to <paramref name="to"/>: every editor of a file in it follows
    /// it to the new path in place, with its unsaved changes, undo history, view and tab position (VS Code re-points its editors the same way).
    /// A document that cannot follow (<see cref="IRelocatableDocument"/>) is closed and reopened at the new path with its unsaved text. The
    /// closed-editors history follows too. UI thread; call after the move.
    /// </summary>
    public async ValueTask RelocateAsync(Uri from, Uri to)
    {
        var prefix = from.AbsoluteUri.TrimEnd('/');
        Uri? Moved(Uri uri)
        {
            var text = uri.AbsoluteUri;
            if (text.Equals(prefix, StringComparison.Ordinal)) return to;
            return text.StartsWith(prefix + "/", StringComparison.Ordinal) ? new Uri(to.AbsoluteUri.TrimEnd('/') + text[prefix.Length..]) : null;
        }
        foreach (var group in EditorTabs.Where(t => Moved(t.Uri) is not null).GroupBy(t => t.Doc).ToArray())
        {
            var target = Moved(group.First().Uri)!;
            if (group.Key is IRelocatableDocument relocatable && !group.Key.IsUntitled)
            {
                await relocatable.RelocateAsync(target);
                foreach (var tab in group) tab.Relocate(target);
                continue;
            }
            // The document cannot change its path: closed here and opened there, its unsaved text put back as one edit.
            var first = group.First();
            var (dirty, text, preview, pinned) = (first.IsDirty, first.IsDirty ? first.Doc.GetText() : null, first.IsPreview, first.IsPinned);
            foreach (var tab in group) await CloseAsync(tab, prompt: false);
            var reopened = await OpenAsync(target, preview, preserveFocus: true);
            if (pinned) Pin(reopened);
            if (dirty && text is not null)
            {
                var doc = reopened.Doc;
                doc.ApplyEdits([new TextEdit(new TextRange(doc.PositionAt(0), doc.PositionAt(doc.Length)), text)]);
            }
        }
        for (var node = _closed.First; node is not null; node = node.Next)
            if (Moved(node.Value) is { } moved) node.Value = moved;
        _tabsChanged.Fire(null);
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

    // ---------------------------------------------------------------- moving between editor groups (Ctrl+W h j k l w W p o, Vim's Ctrl-W)

    /// <summary>A new untitled editor, moved into a new group below the current one (<paramref name="right"/>: to its right): Vim's <c>:new</c> / <c>:vnew</c>.</summary>
    public EditorTab OpenUntitledSplit(bool right = false)
    {
        var opened = OpenUntitled();
        if (right) SplitRight(opened); else SplitDown(opened);
        return opened;
    }

    /// <summary>
    /// The tabs in front of the document groups that are shown, each with its window (0: the main window, then the floating windows in group
    /// order) and its on-screen centre in that window.
    /// </summary>
    private List<GroupFront> GroupFronts()
    {
        var list = new List<GroupFront>();
        var floating = new List<TopLevel>();
        foreach (var g in _factory.DocumentGroups())
        {
            if (g.ActiveDockable is not ShellDocument { IsDisposed: false, LiveView: { } v } t || TopLevel.GetTopLevel(v) is not { } top) continue;
            if (v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), top) is not { } centre) continue;
            var window = 0;
            if (_factory.IsFloating(t))
            {
                window = floating.IndexOf(top);
                if (window < 0) { floating.Add(top); window = floating.Count - 1; }
                window++;
            }
            list.Add(new GroupFront(t, window, centre));
        }
        return list;
    }

    private readonly record struct GroupFront(ShellDocument Tab, int Window, Point Centre);

    /// <summary>
    /// Puts the keyboard in the nearest editor group in <paramref name="direction"/> from the group of <paramref name="from"/>, in the same window
    /// (VS Code's Focus Left / Right / Above / Below Editor Group, Vim's Ctrl-W h l k j). <paramref name="from"/> may be a tab that is not an
    /// editor (Welcome, Settings); a group whose front tab is not an editor is not a Vim window and is never the target. False when there is
    /// none that way.
    /// </summary>
    public bool FocusGroup(ShellDocument? from, GroupDirection direction)
    {
        if (from is null) return false;
        var fronts = GroupFronts();
        var origin = fronts.FirstOrDefault(f => ReferenceEquals(f.Tab, from));
        if (origin.Tab is null) return false;
        var best = fronts.Where(f => f.Tab is EditorTab && f.Window == origin.Window && !ReferenceEquals(f.Tab, from)).Select(f =>
        {
            var dx = f.Centre.X - origin.Centre.X;
            var dy = f.Centre.Y - origin.Centre.Y;
            var ok = direction switch
            {
                GroupDirection.Left => dx < -1 && Math.Abs(dy) <= Math.Abs(dx) * 2,
                GroupDirection.Right => dx > 1 && Math.Abs(dy) <= Math.Abs(dx) * 2,
                GroupDirection.Up => dy < -1 && Math.Abs(dx) <= Math.Abs(dy) * 2,
                _ => dy > 1 && Math.Abs(dx) <= Math.Abs(dy) * 2,
            };
            return (Tab: (EditorTab)f.Tab, Distance: ok ? dx * dx + dy * dy : double.MaxValue);
        }).Where(e => e.Distance < double.MaxValue).OrderBy(e => e.Distance).FirstOrDefault();
        if (best.Tab is null) return false;
        FocusEditor(best.Tab);
        return true;
    }

    /// <summary>
    /// Puts the keyboard in another editor group in reading order: the main window's groups top to bottom, left to right, then each floating
    /// window's. <see cref="GroupCycle.Next"/> and <see cref="GroupCycle.Previous"/> step from the group of <paramref name="from"/> and wrap around
    /// (VS Code's Focus Next / Previous Editor Group, Vim's Ctrl-W w W p), so a floating window's last group leads back to the main window; from a
    /// tab that is not an editor (Welcome) they step from where its group is, and from no shown tab Next is the first group and Previous the last.
    /// First and Last are the main window's top-left and bottom-right editors (Vim's Ctrl-W t b; any window's when the main one shows none).
    /// False when there is no editor group.
    /// </summary>
    public bool FocusGroup(ShellDocument? from, GroupCycle step)
    {
        var order = GroupFronts().Where(f => f.Tab is EditorTab || ReferenceEquals(f.Tab, from))
            .OrderBy(f => f.Window).ThenBy(f => f.Centre.Y).ThenBy(f => f.Centre.X).ToList();
        var editors = order.Where(f => f.Tab is EditorTab).ToList();
        if (editors.Count == 0) return false;
        EditorTab? target;
        if (step is GroupCycle.First or GroupCycle.Last)
        {
            var main = editors.Where(f => f.Window == 0).ToList();
            if (main.Count == 0) main = editors;
            target = (EditorTab)(step == GroupCycle.First ? main[0] : main[^1]).Tab;
        }
        else
        {
            var i = order.FindIndex(f => ReferenceEquals(f.Tab, from));
            var delta = step == GroupCycle.Previous ? -1 : 1;
            if (i < 0) target = (EditorTab)(delta > 0 ? editors[0] : editors[^1]).Tab;
            else
            {
                target = from as EditorTab; // the only group: it keeps the keyboard
                for (var k = 1; k < order.Count; k++)
                    if (order[((i + delta * k) % order.Count + order.Count) % order.Count].Tab is EditorTab e) { target = e; break; }
                if (target is null) return false;
            }
        }
        FocusEditor(target);
        return true;
    }

    /// <summary>Makes <paramref name="tab"/> the active tab and puts the keyboard in its editor.</summary>
    public void FocusEditor(EditorTab tab)
    {
        Activate(tab);
        tab.Controller?.Focus();
    }

    /// <summary>
    /// Closes every tab of the other document groups, so the group of <paramref name="keep"/> is the only one left (VS Code's Close Editors in
    /// Other Groups, Vim's <c>:only</c> / Ctrl-W o). A dirty editor asks first; Cancel stops there. False when cancelled.
    /// </summary>
    public async ValueTask<bool> CloseOtherGroupsAsync(ShellDocument? keep)
    {
        var group = keep is null ? null : _factory.GroupOf(keep);
        foreach (var t in Tabs.Where(t => _factory.GroupOf(t) is { } g && !ReferenceEquals(g, group)).ToArray())
            if (!await CloseAsync(t)) return false;
        return true;
    }

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
        // the same for a view tab's unsaved document (the merge editor's result); our own closes (RemoveTab) run with _suppressActive set
        else if (e.Dockable is ShellDocument viewTab && !_suppressActive && !e.Cancel && _attached.TryGetValue(viewTab, out var attached) && !attached.Decided
                 && IsUnsavedHere(attached) && !ShownByOtherTab(viewTab, attached.Doc))
        {
            e.Cancel = true;
            _ = CloseAfterPromptAsync(viewTab);
        }
        else if (e.Dockable is ShellDocument closingNow && !e.Cancel) closingNow.ReleaseFocus(); // before Dock detaches the view (see RemoveTab)
    }

    private async Task CloseAfterPromptAsync(ShellDocument tab)
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
        _attached.Remove(tab, out var attached);
        tab.Dispose(); // (a merge editor closed untouched gives its document its conflict markers back here)
        if (doc is not null && !EditorTabs.Any(t => ReferenceEquals(t.Doc, doc)) && !_attached.Values.Any(a => ReferenceEquals(a.Doc, doc)))
            _ = CloseDocumentAsync(doc);
        if (attached is not null) ReleaseAttached(attached);
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
    /// Visual Studio: closing the focused document puts the keyboard into the document that takes its place (and activating one moves it there).
    /// Dock presents it after this turn, so wait (bounded) until its editor is in the window. Nothing happens if the user focused something else
    /// meanwhile: only when nothing has the keyboard, or the editor of <paramref name="takeFrom"/> (the tab active before) still has it (another
    /// group's editor stays in the window).
    /// </summary>
    private void FocusWhenShown(ShellDocument next, int attempt = 0, ShellDocument? takeFrom = null, Func<IInputElement, bool>? mayTakeFrom = null)
    {
        if (next is not EditorTab editor || editor.IsDisposed || !ReferenceEquals(_active, editor)) return;
        if (editor.LiveView is { } view && TopLevel.GetTopLevel(view) is { } top && editor.Controller is { } controller)
        {
            var focused = top.FocusManager?.GetFocusedElement();
            if (focused is null or TopLevel || InViewOf(takeFrom, focused) || mayTakeFrom?.Invoke(focused) == true) controller.Focus();
            return;
        }
        // (a well without the shell's dispatcher, in unit tests, has no window to wait for)
        if (attempt < 20 && _ui?.CheckAccess() == true) DispatcherTimer.RunOnce(() => FocusWhenShown(next, attempt + 1, takeFrom, mayTakeFrom), TimeSpan.FromMilliseconds(25));
    }

    private static bool InViewOf(ShellDocument? tab, IInputElement focused) =>
        tab is EditorTab { IsDisposed: false, LiveView: { } view } && focused is Visual v && (ReferenceEquals(v, view) || view.IsVisualAncestorOf(v));

    private async Task CloseDocumentAsync(IEditorDocument doc)
    {
        try { await _editors.CloseDocumentAsync(doc); }
        catch (Exception) { /* editor-side cleanup failure is not actionable by the shell */ }
    }

    // ---------------------------------------------------------------- documents that a view tab edits (a merge editor's result)

    /// <summary>A view tab's document: whether its changes count (<see cref="Unsaved"/>), and whether a close of the tab decided them (asked).</summary>
    private sealed class Attached(IEditorDocument doc, Func<bool> unsaved)
    {
        public IEditorDocument Doc { get; } = doc;
        public Func<bool> Unsaved { get; } = unsaved;
        public bool Decided { get; set; }
    }

    private readonly Dictionary<ShellDocument, Attached> _attached = new();

    /// <summary>
    /// <paramref name="tab"/> (a view tab: the merge editor) edits <paramref name="doc"/>, a file's document no editor tab needs to show. Until the
    /// tab closes the document stays open and counts like an editor tab's: its unsaved changes, when no editor tab shows them, are asked about
    /// when the tab closes (and so at quit: <see cref="CloseAllAsync"/>), make <see cref="HasUnsavedChanges"/> true and are backed up by hot exit
    /// (<see cref="UnsavedAttachedDocuments"/>). <paramref name="unsaved"/> says whether the document's dirty state is the user's (a merge editor's
    /// own starting edit is not); default: dirty. Closing the tab without being asked (a programmatic close) keeps unsaved changes in an editor tab
    /// of their own; otherwise the document is closed with the tab when no editor tab shows it. UI thread.
    /// </summary>
    public void AttachDocument(ShellDocument tab, IEditorDocument doc, Func<bool>? unsaved = null)
    {
        _attached[tab] = new Attached(doc, unsaved ?? (() => doc.IsDirty));
        _tabsChanged.Fire(null); // listeners following the tabs' documents (session autosave) pick it up
    }

    /// <summary>The documents view tabs edit (<see cref="AttachDocument"/>), with their tab, in no particular order.</summary>
    public IReadOnlyList<(ShellDocument Tab, IEditorDocument Document)> AttachedDocuments =>
        [.. _attached.Where(kv => !kv.Key.IsDisposed).Select(kv => (kv.Key, kv.Value.Doc))];

    /// <summary>
    /// The view tabs whose document has unsaved changes of the user's that no editor tab shows, in tab order: what a quit asks about and hot exit
    /// backs up besides the editor tabs.
    /// </summary>
    public IReadOnlyList<(ShellDocument Tab, IEditorDocument Document)> UnsavedAttachedDocuments =>
        _attached.Count == 0 ? [] : [.. Tabs.Where(t => _attached.TryGetValue(t, out var a) && IsUnsavedHere(a)).Select(t => (t, _attached[t].Doc))];

    /// <summary>An editor tab, or a view tab's document (<see cref="AttachDocument"/>), has changes not saved yet: what the Save prompts of a close ask about.</summary>
    public bool HasUnsavedChanges => EditorTabs.Any(t => t.IsDirty) || UnsavedAttachedDocuments.Count > 0;

    private bool IsUnsavedHere(Attached a) => a.Doc.IsDirty && a.Unsaved() && !EditorTabs.Any(t => ReferenceEquals(t.Doc, a.Doc));

    private bool ShownByOtherTab(ShellDocument tab, IEditorDocument doc) =>
        EditorTabs.Any(t => !ReferenceEquals(t, tab) && ReferenceEquals(t.Doc, doc)) || _attached.Any(kv => !ReferenceEquals(kv.Key, tab) && ReferenceEquals(kv.Value.Doc, doc));

    /// <summary>Save / Don't Save / Cancel for a view tab's document (it is a file: no Save As). True when the tab may close.</summary>
    private async ValueTask<bool> ConfirmCloseAsync(Attached a)
    {
        var name = EditorTab.TitleFor(a.Doc.Uri);
        var answer = await _dialogs.ShowAsync(
            "Save changes",
            $"Do you want to save the changes you made to {name}?\n\nYour changes will be lost if you don't save them.",
            ["Save", "Don't Save", "Cancel"], defaultIndex: 0);
        switch (answer)
        {
            case "Save":
                try
                {
                    if (_participants is not null) await _participants.WillSaveAsync(a.Doc, DocumentSaveReason.Manual);
                    await a.Doc.SaveAsync();
                    return true;
                }
                catch (Exception ex)
                {
                    if (_windows is not null) await _windows.ShowMessageAsync(MessageSeverity.Error, $"Could not save {name}: {ex.Message}");
                    return false;
                }
            case "Don't Save":
                return true;
            default:
                return false;
        }
    }

    /// <summary>A view tab closed: its document goes too unless another tab shows it; unsaved changes nobody was asked about get an editor tab.</summary>
    private void ReleaseAttached(Attached a)
    {
        if (EditorTabs.Any(t => ReferenceEquals(t.Doc, a.Doc)) || _attached.Values.Any(x => ReferenceEquals(x.Doc, a.Doc))) return;
        if (a.Doc.IsDirty && !a.Decided) _ = KeepInEditorTabAsync(a.Doc);
        else _ = CloseDocumentAsync(a.Doc);
    }

    private async Task KeepInEditorTabAsync(IEditorDocument doc)
    {
        try { await OpenAsync(doc.Uri, preserveFocus: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
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

/// <summary>A direction from one editor group to its neighbour (<see cref="DocumentWell.FocusGroup(ShellDocument?, GroupDirection)"/>).</summary>
public enum GroupDirection { Left, Right, Up, Down }

/// <summary>A step through the editor groups in reading order (<see cref="DocumentWell.FocusGroup(ShellDocument?, GroupCycle)"/>).</summary>
public enum GroupCycle { Next, Previous, First, Last }
