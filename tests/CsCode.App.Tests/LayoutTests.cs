using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

public class LayoutSerializerTests
{
    private static LayoutSnapshot Sample() => new(LayoutSnapshot.CurrentVersion,
        [new ToolLayout("explorer", "right", true, 0), new ToolLayout("output", "bottom", true, 0), new ToolLayout("errorlist", "bottom", false, 1)],
        0.2, 0.3, 0.35, [new DocumentLayout("file:///a.cs", true, false, 0), new DocumentLayout("file:///b.cs", false, true, 1)], "file:///b.cs");

    [Fact]
    public void A_snapshot_round_trips_through_json_exactly()
    {
        var s = Sample();
        var back = LayoutSerializer.Deserialize(LayoutSerializer.Serialize(s))!;
        Assert.Equal(s.Version, back.Version);
        Assert.Equal(s.Tools, back.Tools);
        Assert.Equal(s.Documents, back.Documents);
        Assert.Equal((s.LeftProportion, s.RightProportion, s.BottomProportion, s.ActiveDocument), (back.LeftProportion, back.RightProportion, back.BottomProportion, back.ActiveDocument));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":99,\"tools\":[],\"documents\":[]}")]
    [InlineData("{\"version\":0,\"tools\":[],\"documents\":[]}")]
    [InlineData("{\"version\":1}")]
    public void Corrupt_empty_or_future_layouts_are_rejected_so_defaults_apply(string? json) => Assert.Null(LayoutSerializer.Deserialize(json));

    [Fact]
    public void Unknown_extra_properties_from_a_newer_build_are_ignored()
    {
        var json = LayoutSerializer.Serialize(Sample()).TrimEnd('}') + ",\"somethingNew\":{\"x\":1}}";
        Assert.NotNull(LayoutSerializer.Deserialize(json));
    }
}

public class LayoutPersistenceTests
{
    private sealed class FakeTool(string id, ToolWindowLocation loc, string? autoShow = null) : IToolWindow
    {
        public int Created, Disposed;
        public string Id => id;
        public string Title => id;
        public string? IconKey => "files";
        public ToolWindowLocation DefaultLocation => loc;
        public string? ToggleCommandId => "toggle." + id;
        public string? AutoShowWhen => autoShow;
        public Control CreateView(IServiceProvider services)
        {
            Created++;
            return new TrackedView(this);
        }
    }

    private sealed class TrackedView(FakeTool owner) : TextBlock, IDisposable
    {
        public void Dispose() => owner.Disposed++;
    }

    private sealed class Rig : IDisposable
    {
        public ShellDockFactory Factory { get; } = new();
        public FakeEditorViewFactory Editors { get; } = new();
        public FakeModalDialogs Dialogs { get; } = new();
        public InMemoryStateStorage State { get; } = new();
        public FakeTool Explorer { get; } = new("explorer", ToolWindowLocation.Right);
        public FakeTool Output { get; } = new("output", ToolWindowLocation.Bottom);
        public FakeTool Errors { get; } = new("errorlist", ToolWindowLocation.Bottom);
        public FakeTool Debug { get; } = new("callstack", ToolWindowLocation.Bottom, "inDebugMode");
        public ToolWindowManager Tools { get; }
        public DocumentWell Well { get; }
        public LayoutPersistence Layout { get; }

        public Rig()
        {
            Tools = new ToolWindowManager(Factory, [Explorer, Output, Errors, Debug], new System.Collections.Generic.Dictionary<string, object>().ToServiceProvider());
            Well = new DocumentWell(Factory, Editors, Dialogs);
            Layout = new LayoutPersistence(Factory, Tools, Well, new ManualUiDispatcher(), State, ["findresults"]);
        }

        public void Dispose() { Well.Dispose(); Tools.Dispose(); }
    }

    [Fact]
    public void Tools_are_placed_in_their_default_wells_and_there_is_no_left_well_when_nothing_wants_it()
    {
        using var rig = new Rig();
        Assert.False(rig.Factory.IncludeLeftTools);
        Assert.Equal(["explorer"], rig.Factory.RightTools.VisibleDockables!.OfType<ToolTab>().Select(t => t.Spec.Id));
        Assert.Equal(["callstack", "errorlist", "output"], rig.Factory.BottomTools.VisibleDockables!.OfType<ToolTab>().Select(t => t.Spec.Id).OrderBy(x => x));
        var main = rig.Factory.Root!.VisibleDockables![0] as Dock.Model.Controls.IProportionalDock;
        Assert.DoesNotContain(rig.Factory.LeftTools, main!.VisibleDockables!);
    }

    [Fact]
    public void A_tool_that_asks_for_the_left_gets_a_left_well()
    {
        var factory = new ShellDockFactory();
        using var tools = new ToolWindowManager(factory, [new FakeTool("toolbox", ToolWindowLocation.Left)], new Dictionary<string, object>().ToServiceProvider());
        Assert.True(factory.IncludeLeftTools);
        Assert.Single(factory.LeftTools.VisibleDockables!);
    }

    [AvaloniaFact]
    public void A_tool_view_is_created_on_first_show_and_disposed_when_hidden_then_recreated_on_next_show()
    {
        using var rig = new Rig();
        rig.Tools.Show("explorer");
        Assert.Equal(1, rig.Explorer.Created);
        Assert.NotNull(rig.Tools.Get("explorer")!.LiveView);
        rig.Tools.Show("explorer"); // idempotent: no second view
        Assert.Equal(1, rig.Explorer.Created);

        rig.Tools.Hide("explorer");
        Assert.False(rig.Tools.IsVisible("explorer"));
        Assert.Equal(1, rig.Explorer.Disposed);
        Assert.Null(rig.Tools.Get("explorer")!.LiveView);

        rig.Tools.Show("explorer");
        Assert.True(rig.Tools.IsVisible("explorer"));
        Assert.Equal(2, rig.Explorer.Created);
        rig.Tools.Toggle("explorer");
        Assert.False(rig.Tools.IsVisible("explorer"));
        Assert.Equal(2, rig.Explorer.Disposed);
    }

    [AvaloniaFact]
    public void Showing_and_hiding_each_tool_window_100_times_balances_creation_and_disposal_and_retains_nothing()
    {
        using var rig = new Rig();
        foreach (var tool in new[] { rig.Explorer, rig.Output, rig.Errors })
        {
            for (var i = 0; i < 100; i++)
            {
                rig.Tools.Show(tool.Id);
                rig.Tools.Hide(tool.Id);
            }
            Assert.Equal(tool.Created, tool.Disposed);
            Assert.Equal(100, tool.Created);
        }
        var weak = new List<WeakReference>();
        Rig_Cycle(rig, weak);
        for (var i = 0; i < 10 && weak.Any(w => w.IsAlive); i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
        Assert.Equal(0, weak.Count(w => w.IsAlive));
    }

    private static void Rig_Cycle(Rig rig, List<WeakReference> weak)
    {
        rig.Tools.Show("output");
        weak.Add(new WeakReference(rig.Tools.Get("output")!.LiveView));
        rig.Tools.Hide("output");
    }

    [Fact]
    public async Task Capture_then_apply_restores_visibility_proportions_and_documents()
    {
        using var rig = new Rig();
        var a = await rig.Well.OpenAsync(new Uri("file:///w/a.cs"));
        var b = await rig.Well.OpenAsync(new Uri("file:///w/b.cs"), preview: true);
        rig.Well.Pin(a);
        rig.Tools.Hide("errorlist");
        rig.Factory.RightTools.Proportion = 0.31;
        rig.Factory.BottomTools.Proportion = 0.4;

        var snapshot = rig.Layout.Capture();
        Assert.False(snapshot.Tools.Single(t => t.Id == "errorlist").Visible);
        Assert.Equal(0.31, snapshot.RightProportion, 3);
        Assert.Equal(["file:///w/a.cs", "file:///w/b.cs"], snapshot.Documents.Select(d => d.Uri).OrderBy(x => x));
        Assert.True(snapshot.Documents.Single(d => d.Uri.EndsWith("a.cs")).Pinned);
        Assert.True(snapshot.Documents.Single(d => d.Uri.EndsWith("b.cs")).Preview);

        // A fresh session.
        using var second = new Rig();
        await second.Layout.ApplyAsync(LayoutSerializer.Deserialize(LayoutSerializer.Serialize(snapshot))!);
        Assert.False(second.Tools.IsVisible("errorlist"));
        Assert.True(second.Tools.IsVisible("output"));
        Assert.Equal(0.31, second.Factory.RightTools.Proportion, 3);
        Assert.Equal(0.4, second.Factory.BottomTools.Proportion, 3);
        Assert.Equal(2, second.Well.EditorTabs.Count);
        Assert.True(second.Well.Find(new Uri("file:///w/a.cs"))!.IsPinned);
        Assert.True(second.Well.Find(new Uri("file:///w/b.cs"))!.IsPreview);
        _ = b;
    }

    [Fact]
    public async Task Moving_a_tool_to_another_well_is_persisted()
    {
        using var rig = new Rig();
        var tool = rig.Tools.Get("output")!;
        rig.Factory.MoveDockable(rig.Factory.BottomTools, rig.Factory.RightTools, tool, null);
        var snapshot = rig.Layout.Capture();
        Assert.Equal("right", snapshot.Tools.Single(t => t.Id == "output").Dock);

        using var second = new Rig();
        await second.Layout.ApplyAsync(snapshot);
        Assert.Contains(second.Factory.RightTools.VisibleDockables!.OfType<ToolTab>(), t => t.Spec.Id == "output");
    }

    [Fact]
    public async Task Stale_ids_and_missing_files_are_skipped_without_throwing()
    {
        using var rig = new Rig();
        var stale = new LayoutSnapshot(1, [new ToolLayout("no-such-tool", "bottom", true, 0), new ToolLayout("output", "left", true, 1)], 0.2, 0.2, 0.2,
            [new DocumentLayout("not a uri", false, false, 0), new DocumentLayout("file:///w/ok.cs", false, false, 0)], "file:///never-opened.cs");
        await rig.Layout.ApplyAsync(stale);
        Assert.Equal(["ok.cs"], rig.Well.EditorTabs.Select(t => t.Title));
        // 'left' degrades to the right well because this layout has no left well.
        Assert.Contains(rig.Factory.RightTools.VisibleDockables!.OfType<ToolTab>(), t => t.Spec.Id == "output");
    }

    [Fact]
    public async Task Save_writes_workspace_and_global_state_and_load_prefers_the_workspace_then_the_global_default()
    {
        using var rig = new Rig();
        await rig.Well.OpenAsync(new Uri("file:///w/a.cs"));
        rig.Layout.Save();
        Assert.NotNull(rig.State.Get<string>(LayoutPersistence.StateKey, StorageScope.Workspace));
        Assert.NotNull(rig.State.Get<string>(LayoutPersistence.StateKey, StorageScope.Application));
        Assert.Single(rig.Layout.Load()!.Documents);

        rig.State.Remove(LayoutPersistence.StateKey, StorageScope.Workspace);
        Assert.NotNull(rig.Layout.Load()); // falls back to the global default
        rig.State.Remove(LayoutPersistence.StateKey, StorageScope.Application);
        Assert.Null(rig.Layout.Load());
        rig.State.Set(LayoutPersistence.StateKey, "garbage{", StorageScope.Workspace);
        Assert.Null(rig.Layout.Load()); // corrupt state never throws
    }

    [Fact]
    public async Task Reset_restores_default_placement_hides_auto_show_windows_and_forgets_saved_layouts()
    {
        using var rig = new Rig();
        rig.Tools.Hide("explorer");
        rig.Factory.MoveDockable(rig.Factory.BottomTools, rig.Factory.RightTools, rig.Tools.Get("output")!, null);
        rig.Layout.Save();
        await rig.Layout.ResetAsync();
        Assert.True(rig.Tools.IsVisible("explorer"));
        Assert.Contains(rig.Factory.BottomTools.VisibleDockables!.OfType<ToolTab>(), t => t.Spec.Id == "output");
        Assert.False(rig.Tools.IsVisible("callstack")); // AutoShowWhen windows come back only when their context key says so
        Assert.Null(rig.Layout.Load());
    }

    [Fact]
    public async Task A_tool_that_a_saved_layout_does_not_know_gets_its_default_visibility_not_visible()
    {
        // A layout saved before a module contributed "modules" (hidden by default) must not make that window appear on the next start.
        var factory = new ShellDockFactory();
        var modules = new FakeTool("modules", ToolWindowLocation.Bottom);
        var output = new FakeTool("output", ToolWindowLocation.Bottom);
        using var tools = new ToolWindowManager(factory, [output, modules], new Dictionary<string, object>().ToServiceProvider());
        using var well = new DocumentWell(factory, new FakeEditorViewFactory(), new FakeModalDialogs());
        var layout = new LayoutPersistence(factory, tools, well, new ManualUiDispatcher(), new InMemoryStateStorage(), ["modules"]);
        var older = new LayoutSnapshot(1, [new ToolLayout("output", "bottom", true, 0)], 0.2, 0.2, 0.2, [], null);
        await layout.ApplyAsync(older);
        Assert.True(tools.IsVisible("output"));
        Assert.False(tools.IsVisible("modules"));

        // A layout that does mention it keeps the user's choice.
        await layout.ApplyAsync(older with { Tools = [.. older.Tools, new ToolLayout("modules", "bottom", true, 1)] });
        Assert.True(tools.IsVisible("modules"));
    }

    [Fact]
    public void Document_location_windows_never_enter_a_tool_well()
    {
        var factory = new ShellDockFactory();
        var settings = new FakeTool("settings", ToolWindowLocation.Document);
        using var tools = new ToolWindowManager(factory, [new FakeTool("explorer", ToolWindowLocation.Right), settings], new Dictionary<string, object>().ToServiceProvider());
        Assert.Equal(["explorer"], factory.RightTools.VisibleDockables!.OfType<ToolTab>().Select(t => t.Spec.Id));
        Assert.Null(tools.Get("settings"));
        Assert.Same(settings, tools.GetDocumentView("settings"));
        Assert.Equal(0, settings.Created); // opened on demand only
    }
}

internal static class ServiceProviderExtensions
{
    public static IServiceProvider ToServiceProvider(this Dictionary<string, object> _) => new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
}
