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
        _subs.Add(Disposable.Create(() =>
        {
            factory.DockableHidden -= OnHidden;
            factory.DockableRestored -= OnRestored;
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

    private bool IsInLayout(ToolTab t) => _factory.LeftTools.VisibleDockables!.Contains(t) || _factory.RightTools.VisibleDockables!.Contains(t) || _factory.BottomTools.VisibleDockables!.Contains(t)
        || (t.Owner is IDock owner && owner.VisibleDockables?.Contains(t) == true);

    public void Show(string id)
    {
        if (!_tools.TryGetValue(id, out var t)) return;
        if (!IsVisible(id)) _factory.RestoreDockable(t);
        _factory.SetActiveDockable(t);
        if (t.Owner is IDock owner) owner.ActiveDockable = t;
        t.EnsureView();
        _visibilityChanged.Fire((id, true));
    }

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
