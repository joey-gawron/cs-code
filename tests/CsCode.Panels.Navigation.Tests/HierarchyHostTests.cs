using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using Avalonia.Headless.XUnit;
using Xunit;

namespace CsCode.Panels.Navigation.Tests;

/// <summary>The commands, the model's lazy tree, directions, refresh and the guards (stale answers, slow or failing providers).</summary>
public class HierarchyHostTests
{
    private static string Rows(HierarchyModel m) => string.Join("|", m.Rows.Select(r => r.ToString().Trim()));

    [Fact]
    public async Task Show_call_hierarchy_prepares_at_the_caret_shows_the_window_and_the_callers_of_the_symbol()
    {
        using var rig = new Rig();
        var g = rig.CallGraph();
        rig.Navigation.ActiveCaret = new TextPosition(10, 6);

        await rig.Commands.ExecuteAsync(HierarchyHost.ShowCallHierarchy);

        Assert.Equal([HierarchyHost.ViewId], rig.Views.Shown);
        Assert.Equal(new TextPosition(10, 6), g.LastPreparePosition);
        var m = rig.Host.Model;
        Assert.Equal(HierarchyDirection.IncomingCalls, m.Direction);
        Assert.Equal("Calls To 'Run'", m.Header);
        Assert.Equal("Run [-]|Main [+]", Rows(m)); // the root is expanded at once; its children are not asked for yet
        Assert.Same(m.Roots[0], m.Selected);
        Assert.Equal(["Run:incoming"], g.Requests.Where(r => r.Direction != "prepare").Select(r => r.Item + ":" + r.Direction));
    }

    [Fact]
    public async Task Children_are_asked_for_once_when_a_node_is_first_expanded_with_the_item_the_server_made()
    {
        using var rig = new Rig();
        var g = rig.CallGraph();
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(20, 0), HierarchyDirection.IncomingCalls); // nothing at line 20 of a.cs
        Assert.Empty(rig.Host.Model.Rows);

        var log = new FakeEditorDocument(new InMemoryTextDocument(FakeCallGraph.FileB, "csharp", string.Join("\n", Enumerable.Range(0, 30).Select(i => "x"))));
        Assert.True(await rig.Host.ShowAtAsync(log, new TextPosition(20, 4), HierarchyDirection.IncomingCalls));
        var m = rig.Host.Model;
        Assert.Equal("Log [-]|Run [+]|Helper [+]", Rows(m));

        var run = m.Rows[1];
        await m.ExpandAsync(run);
        Assert.Equal("Log [-]|Run [-]|Main [+]|Helper [+]", Rows(m));
        m.Collapse(run);
        await m.ExpandAsync(run); // kept: not asked again
        Assert.Equal(1, g.Requests.Count(r => r is { Item: "Run", Direction: "incoming" }));

        var main = m.Rows[2];
        await m.ExpandAsync(main); // nobody calls Main: no chevron any more
        Assert.False(main.HasChildren);
        Assert.Equal("", main.Chevron);
        Assert.Equal("Log [-]|Run [-]|Main|Helper [+]", Rows(m));
    }

    [Fact]
    public async Task Incoming_call_sites_are_in_the_caller_and_outgoing_ones_in_the_node_being_expanded()
    {
        using var rig = new Rig();
        rig.CallGraph();
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.OutgoingCalls);
        var m = rig.Host.Model;
        Assert.Equal("Calls From 'Run'", m.Header);
        Assert.Equal("Run [-]|Step [+]|Log [+]", Rows(m));
        var step = m.Rows[1];
        // Run (in a.cs) calls Step twice: both sites are in a.cs, the caller, not in b.cs where Step lives
        Assert.Equal([FakeCallGraph.FileA, FakeCallGraph.FileA], step.Sites.Select(s => s.Uri));
        Assert.Equal(TextRange.FromLines(11, 8, 11, 12), step.Target.Range);
        Assert.Contains("(2 calls)", step.Detail);

        await rig.Host.ToggleDirectionAsync();
        Assert.Equal(HierarchyDirection.IncomingCalls, m.Direction);
        Assert.Equal("Run [-]|Main [+]", Rows(m));
        var main = m.Rows[1];
        Assert.Equal(new Location(FakeCallGraph.FileA, TextRange.FromLines(2, 8, 2, 11)), main.Target); // the call inside Main
    }

    [Fact]
    public async Task The_direction_used_last_is_what_show_call_hierarchy_uses_next()
    {
        using var rig = new Rig();
        rig.CallGraph();
        rig.Navigation.ActiveCaret = new TextPosition(10, 4);
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowOutgoingCalls);
        Assert.Equal(HierarchyDirection.OutgoingCalls, rig.Host.Model.Direction);
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowCallHierarchy);
        Assert.Equal(HierarchyDirection.OutgoingCalls, rig.Host.Model.Direction);
        await rig.Commands.ExecuteAsync(HierarchyHost.ToggleDirection);
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowCallHierarchy);
        Assert.Equal(HierarchyDirection.IncomingCalls, rig.Host.Model.Direction);
    }

    [Fact]
    public async Task Type_hierarchy_shows_subtypes_by_default_and_toggles_to_supertypes()
    {
        using var rig = new Rig();
        var t = new FakeTypeGraph();
        t.Add("Shape", 1, SymbolKind.Interface);
        t.Add("Polygon", 5);
        t.Add("Square", 9);
        t.Add("Circle", 13);
        t.Derives("Polygon", "Shape");
        t.Derives("Circle", "Shape");
        t.Derives("Square", "Polygon");
        rig.Features.Register<ITypeHierarchyProvider>(DocumentSelector.All, t);
        rig.Navigation.ActiveCaret = new TextPosition(1, 8);

        await rig.Commands.ExecuteAsync(HierarchyHost.ShowTypeHierarchy);
        var m = rig.Host.Model;
        Assert.False(m.IsCalls);
        Assert.Equal("Subtypes of 'Shape'", m.Header);
        Assert.Equal("Shape [-]|Polygon [+]|Circle [+]", Rows(m));
        Assert.Equal("symbol-interface", m.Rows[0].Icon);
        await m.ExpandAsync(m.Rows[1]);
        Assert.Equal("Shape [-]|Polygon [-]|Square [+]|Circle [+]", Rows(m));
        Assert.Equal(new Location(FakeCallGraph.FileA, TextRange.FromLines(9, 6, 9, 12)), m.Rows[2].Target); // a type node goes to its name

        rig.Navigation.ActiveCaret = new TextPosition(9, 7);
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowSupertypes);
        Assert.Equal("Supertypes of 'Square'", m.Header);
        Assert.Equal("Square [-]|Polygon [+]", Rows(m));
        await m.ExpandAsync(m.Rows[1]);
        Assert.Equal("Square [-]|Polygon [-]|Shape [+]", Rows(m));
        Assert.Equal("Show Subtypes", m.ToggleTip);
    }

    [Fact]
    public async Task Without_a_provider_or_a_symbol_the_status_bar_says_why_and_the_window_stays_closed()
    {
        using var rig = new Rig();
        rig.Navigation.ActiveCaret = new TextPosition(10, 4);
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowCallHierarchy);
        Assert.Contains(rig.Window.StatusMessages, s => s.Contains("No call hierarchy is available for this file", StringComparison.Ordinal));

        rig.CallGraph();
        rig.Navigation.ActiveCaret = new TextPosition(25, 0);
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowCallHierarchy);
        Assert.Contains(rig.Window.StatusMessages, s => s.Contains("No call hierarchy for the symbol at the caret", StringComparison.Ordinal));

        rig.Navigation.ActiveDocument = null;
        await rig.Commands.ExecuteAsync(HierarchyHost.ShowTypeHierarchy);
        Assert.Contains(rig.Window.StatusMessages, s => s.Contains("Open a file", StringComparison.Ordinal));
        Assert.Empty(rig.Views.Shown);
    }

    [AvaloniaFact]
    public async Task Providers_are_called_off_the_ui_thread()
    {
        using var rig = new Rig();
        var g = rig.CallGraph();
        var ui = Environment.CurrentManagedThreadId;
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.IncomingCalls);
        Assert.NotEmpty(g.Requests);
        Assert.All(g.Requests, r => Assert.NotEqual(ui, r.ThreadId));
    }

    [Fact]
    public async Task An_answer_for_a_tree_that_was_replaced_meanwhile_is_dropped()
    {
        using var rig = new Rig();
        var g = rig.CallGraph();
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.IncomingCalls);
        var m = rig.Host.Model;
        var main = m.Rows[1];
        var hold = new TaskCompletionSource();
        g.Gate = name => name == "Main" ? hold.Task : Task.CompletedTask;
        var slow = m.ExpandAsync(main);
        Assert.True(main.IsLoading);
        Assert.Equal("loading", main.Chevron);

        await rig.Host.ToggleDirectionAsync(); // a new tree while Main's callers are on their way
        hold.SetResult();
        await slow;
        Assert.Equal(HierarchyDirection.OutgoingCalls, m.Direction);
        Assert.Equal("Run [-]|Step [+]|Log [+]", Rows(m));
        Assert.Null(main.Children); // the old node got nothing
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_leaves_the_node_empty_with_the_reason()
    {
        using var rig = new Rig();
        var g = rig.CallGraph();
        var timeout = HierarchyModel.Timeout;
        HierarchyModel.Timeout = TimeSpan.FromMilliseconds(100);
        try
        {
            g.Gate = name => name == "Run" ? new TaskCompletionSource().Task : Task.CompletedTask; // never answers
            await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.IncomingCalls);
            var run = rig.Host.Model.Roots[0];
            Assert.False(run.IsLoading);
            Assert.Equal("timed out", run.Error);
            Assert.Contains("timed out", run.Detail);
            Assert.False(run.HasChildren);
        }
        finally
        {
            HierarchyModel.Timeout = timeout;
        }
    }

    [Fact]
    public async Task Refresh_prepares_again_while_the_document_is_open_and_keeps_expanded_nodes_expanded()
    {
        using var rig = new Rig();
        var g = rig.CallGraph();
        var b = new FakeEditorDocument(new InMemoryTextDocument(FakeCallGraph.FileB, "csharp", string.Join("\n", Enumerable.Range(0, 30).Select(_ => "x"))));
        using var bReg = rig.Documents.Register(b);
        await rig.Host.ShowAtAsync(b, new TextPosition(20, 4), HierarchyDirection.IncomingCalls);
        var m = rig.Host.Model;
        await m.ExpandAsync(m.Rows[1]); // Run
        Assert.Equal("Log [-]|Run [-]|Main [+]|Helper [+]", Rows(m));

        g.Add("Extra", FakeCallGraph.FileA, 35);
        g.Call("Extra", "Log", TextRange.FromLines(36, 0, 36, 3));
        await rig.Commands.ExecuteAsync(HierarchyHost.Refresh);
        Assert.Equal(2, g.PrepareCount);
        Assert.Equal("Log [-]|Run [-]|Main [+]|Helper [+]|Extra [+]", Rows(m));

        bReg.Dispose(); // closed: the same roots are asked again instead
        g.Call("Main", "Log", TextRange.FromLines(3, 0, 3, 3));
        await rig.Host.RefreshAsync();
        Assert.Equal(2, g.PrepareCount);
        Assert.Equal("Log [-]|Run [-]|Main [+]|Helper [+]|Extra [+]|Main [+]", Rows(m));
    }

    [Fact]
    public async Task Collapse_all_clear_and_reroot()
    {
        using var rig = new Rig();
        rig.CallGraph();
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.OutgoingCalls);
        var m = rig.Host.Model;
        var log = m.Rows[2];
        await rig.Host.RerootAsync(log);
        Assert.Equal("Calls From 'Log'", m.Header);
        Assert.Equal("Log", Rows(m));

        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.OutgoingCalls);
        m.CollapseAll();
        Assert.Equal("Run [+]", Rows(m));
        await rig.Commands.ExecuteAsync(HierarchyHost.Clear);
        Assert.Empty(m.Rows);
        Assert.False(m.HasQuery);
        Assert.Equal(HierarchyHost.EmptyHint, m.EmptyText);
    }

    [Fact]
    public void The_commands_and_the_right_click_items_are_registered_and_withdrawn_with_the_host()
    {
        var rig = new Rig();
        foreach (var id in new[] { HierarchyHost.ShowCallHierarchy, HierarchyHost.ShowIncomingCalls, HierarchyHost.ShowOutgoingCalls, HierarchyHost.ShowTypeHierarchy,
                     HierarchyHost.ShowSupertypes, HierarchyHost.ShowSubtypes, HierarchyHost.Refresh, HierarchyHost.ToggleDirection, HierarchyHost.Clear, HierarchyHost.FocusView })
            Assert.True(rig.Commands.Has(id), id);
        var items = rig.Contributions.Of<MenuContribution>().Items.Select(i => i.Value).Where(i => i.MenuId == MenuIds.EditorContext).ToList();
        Assert.Contains(items, i => i is { Command: HierarchyHost.ShowCallHierarchy, Title: "View Call _Hierarchy", When: HierarchyHost.HasCallHierarchyKey });
        Assert.Contains(items, i => i is { Command: HierarchyHost.ShowTypeHierarchy, When: HierarchyHost.HasTypeHierarchyKey });
        Assert.All(items, i => Assert.StartsWith("2_navigation@", i.Group)); // with Visual Studio's navigation items

        rig.Dispose();
        Assert.False(rig.Commands.Has(HierarchyHost.ShowCallHierarchy));
        Assert.Empty(rig.Contributions.Of<MenuContribution>().Items);
    }
}
