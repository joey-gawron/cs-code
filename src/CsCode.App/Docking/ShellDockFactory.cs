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
    public IRootDock? Root { get; private set; }
    public ToolDock LeftTools { get; } = new() { Id = "dock.left", Title = "Left", Alignment = Alignment.Left, Proportion = 0.22, CanCloseLastDockable = true };
    public ToolDock RightTools { get; } = new() { Id = "dock.right", Title = "Right", Alignment = Alignment.Right, Proportion = 0.24, CanCloseLastDockable = true };
    public ToolDock BottomTools { get; } = new() { Id = "dock.bottom", Title = "Bottom", Alignment = Alignment.Bottom, Proportion = 0.28, CanCloseLastDockable = true };
    public DocumentDock PrimaryDocuments { get; } = new() { Id = "docs.primary", Title = "Documents", CanCreateDocument = false, CanCloseLastDockable = true };

    public override IRootDock CreateLayout()
    {
        // The primary document group and the tool wells must survive being emptied: Dock would otherwise collapse an empty dock out
        // of the layout and later tabs would land in a detached dock. (Groups created by "split" stay collapsible.)
        PrimaryDocuments.IsCollapsable = false;
        LeftTools.IsCollapsable = false;
        RightTools.IsCollapsable = false;
        BottomTools.IsCollapsable = false;
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
        var main = new ProportionalDock
        {
            Id = "layout.main",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                LeftTools,
                new ProportionalDockSplitter { Id = "split.left" },
                center,
                new ProportionalDockSplitter { Id = "split.right" },
                RightTools),
        };
        var root = CreateRootDock();
        root.Id = "root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.DefaultDockable = main;
        root.ActiveDockable = main;
        Root = root;
        return root;
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
