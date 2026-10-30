using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Ui;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace CsCode.App.Docking;

/// <summary>
/// Places every registered <see cref="IToolWindow"/> into the Dock layout, shows/hides/toggles them and keeps the memory contract:
/// a tool's view (and view-model) is created on first show and disposed when the tool is hidden.
/// <see cref="ToolWindowLocation.Document"/> windows (Settings, Keyboard Shortcuts) are not tool windows here: they never enter a tool well and
/// are opened on demand as document tabs (<see cref="DocumentViews"/>, routed by <see cref="ShellViewActivator"/>).
/// </summary>
public sealed class ToolWindowManager : IDisposable
{
    private readonly ShellDockFactory _factory;
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, ToolTab> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IToolWindow> _documentViews = new(StringComparer.Ordinal);
    private readonly Emitter<(string Id, bool Visible)> _visibilityChanged = new();
    private readonly DisposableStore _subs = new();
    private int _focusRequest;

    public ToolWindowManager(ShellDockFactory factory, IEnumerable<IToolWindow> specs, IServiceProvider services)
    {
        _factory = factory;
        _services = services;
        var all = specs.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        foreach (var spec in all.Where(s => s.DefaultLocation == ToolWindowLocation.Document)) _documentViews[spec.Id] = spec;
        var ordered = all.Where(s => s.DefaultLocation != ToolWindowLocation.Document).ToArray();
        if (factory.Root is null) factory.IncludeLeftTools = ordered.Any(s => s.DefaultLocation == ToolWindowLocation.Left);
        factory.EnsureLayout();
        foreach (var spec in ordered)
        {
            var tab = new ToolTab(spec, services);
            _tools[spec.Id] = tab;
            var dock = DockFor(spec.DefaultLocation);
            factory.AddDockable(dock, tab);
            if (dock.ActiveDockable is null) dock.ActiveDockable = tab;
        }
        factory.DockableHidden += OnHidden;
        factory.DockableRestored += OnRestored;
        factory.DockableClosed += OnClosed;
        _subs.Add(Disposable.Create(() =>
        {
            factory.DockableHidden -= OnHidden;
            factory.DockableRestored -= OnRestored;
            factory.DockableClosed -= OnClosed;
        }));
    }

    public IReadOnlyCollection<ToolTab> Tools => _tools.Values;
    public IEventSource<(string Id, bool Visible)> VisibilityChanged => _visibilityChanged;

    /// <summary>Registered windows that open as document tabs instead of docking in a tool well.</summary>
    public IReadOnlyCollection<IToolWindow> DocumentViews => _documentViews.Values;

    public ToolTab? Get(string id) => _tools.GetValueOrDefault(id);

    public IToolWindow? GetDocumentView(string id) => _documentViews.GetValueOrDefault(id);

    private IDock DockFor(ToolWindowLocation location) => location switch
    {
        ToolWindowLocation.Left when _factory.IncludeLeftTools => _factory.LeftTools,
        ToolWindowLocation.Bottom => _factory.BottomTools,
        _ => _factory.RightTools,
    };

    /// <summary>Called once after the layout was initialised: tools that auto-show on a context key start hidden.</summary>
    public void HideInitiallyHidden(Func<IToolWindow, bool> startsHidden)
    {
        foreach (var t in _tools.Values.Where(t => startsHidden(t.Spec)).ToArray()) Hide(t.Spec.Id);
    }

    public bool IsVisible(string id) =>
        _tools.TryGetValue(id, out var t) && !(_factory.Root is IRootDock r && r.HiddenDockables?.Contains(t) == true) && IsInLayout(t);

    // An auto-hidden (pinned) tool sits on an edge strip of the root, in none of the wells: it is still in the layout, and on screen as a strip tab.
    private bool IsInLayout(ToolTab t) => _factory.LeftTools.VisibleDockables!.Contains(t) || _factory.RightTools.VisibleDockables!.Contains(t) || _factory.BottomTools.VisibleDockables!.Contains(t)
        || (t.Owner is IDock owner && owner.VisibleDockables?.Contains(t) == true) || IsPinned(t);

    private bool IsPinned(ToolTab t) => _factory.Root is IRootDock r && _factory.IsDockablePinned(t, r);

    public void Show(string id)
    {
        if (!_tools.TryGetValue(id, out var t)) return;
        if (!IsVisible(id))
        {
            Restore(t);
            // A tool hidden while it floated floats again where it was (Visual Studio).
            if (_factory.TakeFloatingMemory(id) is { } bounds) _factory.Float(t, bounds);
        }
        if (_factory.WindowOf(t) is { } floating) floating.SetActive();
        if (IsPinned(t))
            _factory.PreviewPinnedDockable(t); // slides out of its strip and stays auto-hidden, as in Visual Studio
        else
        {
            _factory.SetActiveDockable(t);
            if (t.Owner is IDock owner) owner.ActiveDockable = t;
        }
        t.EnsureHost(); // the view, inside the host Dock presents (Dock may reuse the host it cached for this tool)
        _visibilityChanged.Fire((id, true));
    }

    /// <summary>
    /// Puts the keyboard into a shown tool's view, as Visual Studio's View menu does for the window it shows (typing next goes there, not into the
    /// editor): on the element the view prefers (<see cref="IToolWindowFocus"/>), else on its first content element in tab order (never a button
    /// or splitter, see <see cref="FirstContentElement"/>), else, for a view with nothing else that can take it, on its host
    /// (<see cref="ToolTab.FocusHost"/>). <see cref="Show"/> only creates the view and Dock presents it on a later layout pass, so this runs after
    /// layout and waits (bounded) until the view is on screen. Nothing happens when the tool was hidden meanwhile or a later request superseded
    /// this one.
    /// </summary>
    public void FocusView(string id)
    {
        if (!_tools.TryGetValue(id, out var t)) return;
        var request = ++_focusRequest;
        Dispatcher.UIThread.Post(() => FocusWhenShown(t, request, 0), DispatcherPriority.Loaded);
    }

    private void FocusWhenShown(ToolTab t, int request, int attempt)
    {
        if (request != _focusRequest || t.LiveView is not { } view) return;
        if (view.IsAttachedToVisualTree() && view.IsEffectivelyVisible)
        {
            if (view is IToolWindowFocus own && own.FocusPreferred()) return;
            if (FirstContentElement(view)?.Focus() == true) return;
            t.FocusHost();
            return;
        }
        if (attempt < 20) DispatcherTimer.RunOnce(() => FocusWhenShown(t, request, attempt + 1), TimeSpan.FromMilliseconds(25));
    }

    /// <summary>
    /// The first element of <paramref name="view"/> in tab order that is the window's content, not its chrome: never a button (ToggleButton,
    /// CheckBox and the rest are buttons too) or a GridSplitter, which is what leads the tab order of a window with a toolbar or a column header.
    /// The keyboard left there by the View menu does what the user did not ask for: the next Enter presses Clear Console or Continue, the next
    /// arrow resizes the columns of Locals; Visual Studio puts it in the window's list or input. Null when nothing else in the view can take the
    /// keyboard (the caller then focuses the host).
    /// </summary>
    internal static IInputElement? FirstContentElement(Control view)
    {
        var focus = TopLevel.GetTopLevel(view)?.FocusManager;
        var seen = new HashSet<IInputElement>(); // tab order cycles (at the window, or inside a container with KeyboardNavigationMode.Cycle)
        for (var e = FocusManager.FindFirstFocusableElement(view);
             e is Avalonia.Visual v && (ReferenceEquals(v, view) || view.IsVisualAncestorOf(v)) && seen.Add(e);
             e = focus?.FindNextElement(NavigationDirection.Next, new FindNextElementOptions { FocusedElement = e }))
        {
            if (e is not (Button or GridSplitter)) return e;
        }
        return null;
    }

    /// <summary>Docks an auto-hidden tool back into its well (a no-op for any other tool).</summary>
    public void Unpin(ToolTab t)
    {
        if (IsPinned(t)) _factory.PinDockable(t); // toggles
    }

    /// <summary>Visual Studio's Auto Hide: a docked tool moves onto its well's edge strip (a no-op for one that floats or is already there).</summary>
    public void AutoHide(ToolTab t)
    {
        if (!IsPinned(t) && !_factory.IsFloating(t) && IsVisible(t.Spec.Id)) _factory.PinDockable(t); // toggles
    }

    /// <summary>Whether the tool is auto-hidden (on an edge strip; it counts as visible).</summary>
    public bool IsAutoHidden(ToolTab t) => IsPinned(t);

    /// <summary>Visual Studio's Float: the tool moves into a floating window of its own (shown first when hidden).</summary>
    public void Float(string id)
    {
        if (!_tools.TryGetValue(id, out var t)) return;
        if (!IsVisible(id)) Show(id);
        Unpin(t);
        _factory.Float(t);
        _factory.WindowOf(t)?.SetActive();
        _visibilityChanged.Fire((id, true)); // the layout changed (persistence, menus)
    }

    /// <summary>Visual Studio's Dock: a floating or auto-hidden tool goes back into its well.</summary>
    public void Dock(string id)
    {
        if (!_tools.TryGetValue(id, out var t)) return;
        if (_factory.IsFloating(t)) _factory.DockBack(t, DockFor(t.Spec.DefaultLocation));
        else Unpin(t);
        _visibilityChanged.Fire((id, true));
    }

    /// <summary>Whether the tool is shown in a floating window.</summary>
    public bool IsFloating(string id) => _tools.TryGetValue(id, out var t) && _factory.IsFloating(t);

    /// <summary>The well a tool belongs in: where it is docked, else where it was floated from, else its default well.</summary>
    public IDock HomeWell(ToolTab t) =>
        t.Owner is IDock owner && IsWell(owner) ? owner
        : _factory.HomeOf(t) is { } home && IsWell(home) ? home
        : t.OriginalOwner is IDock original && IsWell(original) ? original
        : DockFor(t.Spec.DefaultLocation);

    /// <summary>
    /// Puts a tool that is not visible back into the layout: a hidden one (Hide, or its X: <see cref="ShellDockFactory"/> hides tools on close)
    /// is restored where it was; one that left the layout altogether (removed by Dock, e.g. while hide-on-close was off) is docked in the well it
    /// was last in, else its default well.
    /// </summary>
    public void Restore(ToolTab t)
    {
        _factory.RestoreDockable(t);
        if (IsInLayout(t)) return;
        IDock well = t.Owner is IDock last && IsWell(last) ? last : DockFor(t.Spec.DefaultLocation);
        _factory.AddDockable(well, t);
    }

    private bool IsWell(IDock dock) =>
        ReferenceEquals(dock, _factory.RightTools) || ReferenceEquals(dock, _factory.BottomTools) || (_factory.IncludeLeftTools && ReferenceEquals(dock, _factory.LeftTools));

    public void Hide(string id)
    {
        if (!_tools.TryGetValue(id, out var t) || !IsVisible(id)) return;
        _factory.HideDockable(t);
    }

    public void Toggle(string id)
    {
        if (IsVisible(id)) Hide(id); else Show(id);
    }

    private void OnHidden(object? sender, Dock.Model.Core.Events.DockableHiddenEventArgs e)
    {
        if (e.Dockable is ToolTab t)
        {
            t.DisposeView(); // memory policy: hidden tool windows keep no live view
            _visibilityChanged.Fire((t.Spec.Id, false));
        }
    }

    // Tools are hidden on close (ShellDockFactory.HideToolsOnClose; Dock then raises Hidden, handled above, and Closed). A tool that Dock
    // removed instead gets the same memory policy as a hidden one, and Show docks it again (Restore).
    private void OnClosed(object? sender, Dock.Model.Core.Events.DockableClosedEventArgs e)
    {
        if (e.Dockable is ToolTab t && _tools.TryGetValue(t.Spec.Id, out var known) && ReferenceEquals(known, t)
            && !(_factory.Root is IRootDock r && r.HiddenDockables?.Contains(t) == true))
        {
            t.DisposeView();
            _visibilityChanged.Fire((t.Spec.Id, false));
        }
    }

    private void OnRestored(object? sender, Dock.Model.Core.Events.DockableRestoredEventArgs e)
    {
        if (e.Dockable is ToolTab t) _visibilityChanged.Fire((t.Spec.Id, true));
    }

    public void Dispose()
    {
        _subs.Dispose();
        foreach (var t in _tools.Values) t.Dispose();
        _tools.Clear();
    }
}
