using CsCode.Ui;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace CsCode.App.Docking;

/// <summary>Where a floating window was (screen coordinates and size, device-independent pixels).</summary>
public sealed record FloatingBounds(double X, double Y, double Width, double Height);

/// <summary>
/// Builds the VS2022-style workbench layout: documents in the centre, a bottom tool well (Output / Error List / Find Results ...),
/// a left and a right tool well (Explorer docks right by default, like Visual Studio). The tool docks start empty and are filled from
/// the registered <see cref="IToolWindow"/>s by <see cref="ToolWindowManager"/>.
/// <para>
/// <b>Floating windows</b> (Visual Studio's Float / Dock): a tool window or a document tab floats into a native window of its own (Dock.Avalonia's
/// <see cref="HostWindow"/>, owned by the main window), with a layout root of its own in <see cref="IRootDock.Windows"/>. The factory remembers
/// where each floated dockable came from (<see cref="DockBack"/> returns it there), closes a floating window once nothing is left in it, and a
/// floating tool that is hidden (its X, Hide, Close) is docked back hidden and floats again at the same place when it is shown
/// (<see cref="TakeFloatingMemory"/>). <see cref="HostWindowCreated"/> lets the main window give each floating window the shell's keyboard,
/// focus, clipboard and menu handling.
/// </para>
/// </summary>
public sealed class ShellDockFactory : Factory
{
    // Where a floated dockable came from (a tool well, a document group), so Dock returns it there. Weak: a closed tab must not be kept.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDockable, IDock> _homes = new();
    // Floating tools that were hidden while floating: where their window was, so Show floats them again (VS behaviour). Keyed by tool id.
    private readonly Dictionary<string, FloatingBounds> _floatingMemory = new(StringComparer.Ordinal);

    public ShellDockFactory()
    {
        // A tool window's X (and its tab's Close) hides it, like Visual Studio: it stays in the layout model, its view is disposed
        // (ToolWindowManager's hidden handler) and its command, menu entry or key shows it again. Removing it instead (Dock's default) left the
        // view alive and the window unreachable for the rest of the session. Documents are still removed: the document well disposes them.
        HideToolsOnClose = true;
        HideDocumentsOnClose = false;
        // Floating windows are native windows (as in Visual Studio), created here so the shell can wire each one (HostWindowCreated).
        DefaultHostWindowLocator = CreateHostWindow;
    }

    /// <summary>Raised on the UI thread for every floating host window, before it is shown.</summary>
    public event Action<HostWindow>? HostWindowCreated;

    private HostWindow CreateHostWindow()
    {
        var window = new HostWindow();
        HostWindowCreated?.Invoke(window);
        return window;
    }

    public IRootDock? Root { get; private set; }

    /// <summary>Whether the layout has a left tool well. Must be set before <see cref="EnsureLayout"/>.</summary>
    public bool IncludeLeftTools { get; set; } = true;
    public ToolDock LeftTools { get; } = new() { Id = "dock.left", Title = "Left", Alignment = Alignment.Left, Proportion = 0.22, CanCloseLastDockable = true };
    public ToolDock RightTools { get; } = new() { Id = "dock.right", Title = "Right", Alignment = Alignment.Right, Proportion = 0.24, CanCloseLastDockable = true };
    public ToolDock BottomTools { get; } = new() { Id = "dock.bottom", Title = "Bottom", Alignment = Alignment.Bottom, Proportion = 0.28, CanCloseLastDockable = true };
    public DocumentDock PrimaryDocuments { get; } = new() { Id = "docs.primary", Title = "Documents", CanCreateDocument = false, CanCloseLastDockable = true };

    /// <summary>Creates and initialises the layout exactly once (the tool manager and the window both need it; order must not matter).</summary>
    public IRootDock EnsureLayout()
    {
        if (Root is not null) return Root;
        var layout = CreateLayout();
        InitLayout(layout);
        return layout;
    }

    public override IRootDock CreateLayout()
    {
        // The primary document group and the tool wells must survive being emptied: Dock would otherwise collapse an empty dock out
        // of the layout and later tabs would land in a detached dock. (Groups created by "split" stay collapsible.) The tool wells are
        // collapsible all the same, because Dock's theme only gives up the room of an empty dock that is collapsible (an empty well kept its
        // share of the window); CollapseDock below keeps them in the model.
        PrimaryDocuments.IsCollapsable = false;
        LeftTools.IsCollapsable = true;
        RightTools.IsCollapsable = true;
        BottomTools.IsCollapsable = true;
        PrimaryDocuments.VisibleDockables = CreateList<IDockable>();
        LeftTools.VisibleDockables = CreateList<IDockable>();
        RightTools.VisibleDockables = CreateList<IDockable>();
        BottomTools.VisibleDockables = CreateList<IDockable>();

        var center = new ProportionalDock
        {
            Id = "layout.center",
            Orientation = Orientation.Vertical,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                PrimaryDocuments,
                new ProportionalDockSplitter { Id = "split.bottom" },
                BottomTools),
        };
        // An empty well would still reserve its proportion of the width, so the left well only exists when some tool window wants it.
        var children = IncludeLeftTools
            ? CreateList<IDockable>(LeftTools, new ProportionalDockSplitter { Id = "split.left" }, center, new ProportionalDockSplitter { Id = "split.right" }, RightTools)
            : CreateList<IDockable>(center, new ProportionalDockSplitter { Id = "split.right" }, RightTools);
        var main = new ProportionalDock { Id = "layout.main", Orientation = Orientation.Horizontal, VisibleDockables = children };
        var root = CreateRootDock();
        root.Id = "root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.DefaultDockable = main;
        root.ActiveDockable = main;
        Root = root;
        return root;
    }

    /// <summary>An emptied tool well stays in the layout (it only takes no room on screen while empty); other docks collapse as Dock does.</summary>
    public override void CollapseDock(IDock dock)
    {
        if (ReferenceEquals(dock, LeftTools) || ReferenceEquals(dock, RightTools) || ReferenceEquals(dock, BottomTools)) return;
        base.CollapseDock(dock);
    }

    /// <summary>
    /// All document groups currently in the layout (the primary plus any created by split right / down), then those of the floating windows.
    /// </summary>
    public IReadOnlyList<DocumentDock> DocumentGroups()
    {
        var result = new List<DocumentDock>();
        if (Root is null) return result;
        Collect(Root, result);
        foreach (var window in FloatingWindows)
            if (window.Layout is { } layout) Collect(layout, result);
        return result;

        static void Collect(IDockable node, List<DocumentDock> into)
        {
            if (node is DocumentDock dd) into.Add(dd);
            if (node is IDock dock && dock.VisibleDockables is { } kids)
                foreach (var k in kids.ToArray()) Collect(k, into);
        }
    }

    /// <summary>Finds the document group that contains <paramref name="tab"/>.</summary>
    public DocumentDock? GroupOf(IDockable tab) => DocumentGroups().FirstOrDefault(g => g.VisibleDockables?.Contains(tab) == true);

    // ------------------------------------------------------------------ floating windows

    /// <summary>The floating windows (each with its own layout root), in the order they were created.</summary>
    public IReadOnlyList<IDockWindow> FloatingWindows => Root is IRootDock { Windows: { } windows } ? windows.ToArray() : [];

    /// <summary>The floating window that holds <paramref name="dockable"/>, or null when it is in the main window (or nowhere).</summary>
    public IDockWindow? WindowOf(IDockable dockable)
    {
        IDockable? node = dockable;
        while (node is not null && node is not IRootDock) node = node.Owner;
        if (node is not IRootDock root || ReferenceEquals(root, Root)) return null;
        return FloatingWindows.FirstOrDefault(w => ReferenceEquals(w.Layout, root));
    }

    /// <summary>Whether <paramref name="dockable"/> is shown in a floating window.</summary>
    public bool IsFloating(IDockable dockable) => WindowOf(dockable) is not null;

    /// <summary>Where a floating window is now (from its host when it has one, else its model).</summary>
    public static FloatingBounds BoundsOf(IDockWindow window)
    {
        double x = window.X, y = window.Y, w = window.Width, h = window.Height;
        if (window.Host is { } host)
        {
            host.GetPosition(out x, out y);
            host.GetSize(out w, out h);
        }
        return new FloatingBounds(x, y, w, h);
    }

    /// <summary>Moves and sizes a floating window (its model and its host).</summary>
    public static void SetBounds(IDockWindow window, FloatingBounds bounds)
    {
        var w = Math.Max(120, double.IsFinite(bounds.Width) ? bounds.Width : 400);
        var h = Math.Max(80, double.IsFinite(bounds.Height) ? bounds.Height : 300);
        var x = double.IsFinite(bounds.X) ? bounds.X : 0;
        var y = double.IsFinite(bounds.Y) ? bounds.Y : 0;
        window.X = x;
        window.Y = y;
        window.Width = w;
        window.Height = h;
        window.Host?.SetPosition(x, y);
        window.Host?.SetSize(w, h);
    }

    /// <summary>Floats <paramref name="dockable"/> into a new window, at <paramref name="bounds"/> when given. Returns its window (null if it could not float).</summary>
    public IDockWindow? Float(IDockable dockable, FloatingBounds? bounds = null)
    {
        if (WindowOf(dockable) is { } already) return already;
        FloatDockable(dockable);
        var window = WindowOf(dockable);
        if (window is not null && bounds is not null) SetBounds(window, bounds);
        return window;
    }

    public override void FloatDockable(IDockable dockable, DockWindowOptions? options)
    {
        // Remember where it was docked (a floating dockable floated again keeps its original home).
        if (dockable.Owner is IDock home && !IsFloating(dockable))
        {
            _homes.Remove(dockable);
            _homes.Add(dockable, home);
        }
        base.FloatDockable(dockable, options);
    }

    /// <summary>
    /// Visual Studio's Dock (Dock as Tabbed Document for a document): puts a floating dockable back where it was floated from, or, when that group
    /// is gone, into the primary document group (a document) or its default well (a tool). The floating window closes once it is empty.
    /// </summary>
    public bool DockBack(IDockable dockable, IDock? defaultHome = null)
    {
        if (WindowOf(dockable) is null || dockable.Owner is not IDock from) return false;
        var target = _homes.TryGetValue(dockable, out var home) && IsInMainLayout(home) ? home
            : defaultHome ?? (dockable is ShellDocument ? PrimaryDocuments : RightTools);
        MoveDockable(from, target, dockable, null);
        target.ActiveDockable = dockable;
        SetActiveDockable(dockable);
        _homes.Remove(dockable);
        CloseEmptyWindows();
        return true;
    }

    /// <summary>The home a floated dockable returns to (null: docked, or floated by something that did not go through the factory).</summary>
    public IDock? HomeOf(IDockable dockable) => _homes.TryGetValue(dockable, out var home) ? home : null;

    private bool IsInMainLayout(IDock dock)
    {
        IDockable? node = dock;
        while (node is not null && node is not IRootDock) node = node.Owner;
        return ReferenceEquals(node, Root);
    }

    /// <summary>
    /// A tool hidden while it floats (its X, Hide, Close, Zen mode) goes back to its well first, hidden there like any other hidden tool, and
    /// its window's place is remembered: Show floats it again (<see cref="TakeFloatingMemory"/>). Left in the floating root, it would come back
    /// into a window that has closed meanwhile, or leave an empty window on screen.
    /// </summary>
    public override void HideDockable(IDockable dockable)
    {
        if (dockable is ToolTab tool && WindowOf(tool) is { } window)
        {
            _floatingMemory[tool.Spec.Id] = BoundsOf(window);
            DockBack(tool);
        }
        base.HideDockable(dockable);
        CloseEmptyWindows();
    }

    /// <summary>Where a hidden tool floated (and forgets it): the tool manager floats it there again when it is shown.</summary>
    public FloatingBounds? TakeFloatingMemory(string toolId) => _floatingMemory.Remove(toolId, out var b) ? b : null;

    /// <summary>The places of hidden floating tools (layout persistence).</summary>
    public IReadOnlyDictionary<string, FloatingBounds> FloatingMemory => _floatingMemory;

    /// <summary>Remembers that a hidden tool floats when shown (layout restore).</summary>
    public void RememberFloating(string toolId, FloatingBounds bounds) => _floatingMemory[toolId] = bounds;

    public override void CloseDockable(IDockable? dockable)
    {
        base.CloseDockable(dockable);
        CloseEmptyWindows();
    }

    public override void MoveDockable(IDock sourceDock, IDock targetDock, IDockable sourceDockable, IDockable? targetDockable)
    {
        base.MoveDockable(sourceDock, targetDock, sourceDockable, targetDockable);
        CloseEmptyWindows();
    }

    /// <summary>Closes floating windows that hold no tab any more (Dock leaves one behind when its last tool is hidden or its last tab closed).</summary>
    public void CloseEmptyWindows()
    {
        foreach (var window in FloatingWindows)
        {
            if (window.Layout is { } layout && HasDockables(layout)) continue;
            AllowClose = true;
            try { CloseWindow(window); }
            finally { AllowClose = false; }
        }
    }

    private static bool HasDockables(IDockable node) => node switch
    {
        ShellDocument or ToolTab => true,
        IDock { VisibleDockables: { } kids } => kids.Any(HasDockables),
        _ => false,
    };

    /// <summary>
    /// Asked when a floating window is about to close by itself (its title bar's close button, Alt+F4). Returning true lets it close; the shell
    /// returns false and empties it instead (tools hidden, documents closed with their Save prompts), after which it closes as an empty window.
    /// </summary>
    public Func<IDockWindow, bool>? WindowClosingInterceptor { get; set; }

    // Set while the factory itself closes an empty window or the shell closes everything (exit): no interception.
    private bool AllowClose { get; set; }

    /// <summary>Closes every floating window without asking (the main window is closing; the layout was saved before).</summary>
    public void CloseAllWindows()
    {
        AllowClose = true;
        try
        {
            foreach (var window in FloatingWindows) CloseWindow(window);
        }
        finally { AllowClose = false; }
    }

    /// <summary>Set once the main window is closing for good: floating windows then close without being emptied first.</summary>
    public bool IsShuttingDown { get; set; }

    public override bool OnWindowClosing(IDockWindow? window)
    {
        // Only a live floating window that still holds tabs is emptied instead (a window Dock already removed, or an empty one, just closes).
        if (!AllowClose && !IsShuttingDown && window is not null && FloatingWindows.Contains(window) && window.Layout is { } layout && HasDockables(layout)
            && WindowClosingInterceptor is { } intercept && !intercept(window)) return false;
        return base.OnWindowClosing(window);
    }
}
