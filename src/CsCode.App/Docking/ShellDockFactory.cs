using CsCode.Ui;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace CsCode.App.Docking;

/// <summary>
/// Builds the VS2022-style workbench layout: documents in the centre, a bottom tool well (Output / Error List / Find Results ...),
/// a left and a right tool well (Explorer docks right by default, like Visual Studio). The tool docks start empty and are filled from
/// the registered <see cref="IToolWindow"/>s by <see cref="ToolWindowManager"/>.
/// </summary>
public sealed class ShellDockFactory : Factory
{
    public ShellDockFactory()
    {
        // A tool window's X (and its tab's Close) hides it, like Visual Studio: it stays in the layout model, its view is disposed
        // (ToolWindowManager's hidden handler) and its command, menu entry or key shows it again. Removing it instead (Dock's default) left the
        // view alive and the window unreachable for the rest of the session. Documents are still removed: the document well disposes them.
        HideToolsOnClose = true;
        HideDocumentsOnClose = false;
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

    /// <summary>All document groups currently in the layout (the primary plus any created by split right / down).</summary>
    public IReadOnlyList<DocumentDock> DocumentGroups()
    {
        var result = new List<DocumentDock>();
        if (Root is not null) Collect(Root, result);
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
}
