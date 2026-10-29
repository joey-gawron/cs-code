using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Infrastructure;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests;

public sealed class WellRig : IDisposable
{
    public ShellDockFactory Factory { get; } = new();
    public FakeEditorViewFactory Editors { get; } = new();
    public FakeModalDialogs Dialogs { get; } = new();
    public DocumentWell Well { get; }

    public WellRig()
    {
        var layout = Factory.CreateLayout();
        Factory.InitLayout(layout);
        Well = new DocumentWell(Factory, Editors, Dialogs);
    }

    public static Uri U(string name) => new("file:///work/" + name);
    public async Task<EditorTab> Open(string name, bool preview = false) => await Well.OpenAsync(U(name), preview);
    public string[] Titles => Well.Tabs.Select(t => t.Title).ToArray();
    public void Dispose() => Well.Dispose();
}

public class DocumentWellTests
{
    [Fact]
    public async Task Opening_a_file_adds_an_active_tab_and_reopening_the_same_uri_reuses_it()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var again = await rig.Open("a.cs");
        Assert.Same(a, again);
        Assert.Equal(["a.cs"], rig.Titles);
        Assert.Same(a, rig.Well.Active);
        Assert.Equal(1, rig.Editors.OpenCount);
    }

    [Fact]
    public async Task Tabs_are_active_in_open_order_and_mru_puts_the_latest_first()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var b = await rig.Open("b.cs");
        var c = await rig.Open("c.cs");
        Assert.Same(c, rig.Well.Active);
        Assert.Equal([c, b, a], rig.Well.MruOrder);
        rig.Well.Activate(a);
        Assert.Equal([a, c, b], rig.Well.MruOrder);
        Assert.Same(b, rig.Well.PeekMru(2)); // two presses of Ctrl+Tab from a
    }

    [Fact]
    public async Task A_preview_open_replaces_the_previous_preview_tab_but_never_a_permanent_one()
    {
        using var rig = new WellRig();
        var perm = await rig.Open("perm.cs");
        var p1 = await rig.Open("p1.cs", preview: true);
        Assert.True(p1.IsPreview);
        var p2 = await rig.Open("p2.cs", preview: true);
        Assert.Equal(["perm.cs", "p2.cs"], rig.Titles);
        Assert.True(p2.IsPreview);
        Assert.False(perm.IsPreview);
        Assert.Contains(WellRig.U("p1.cs"), rig.Editors.Closed); // the replaced preview's document was released
    }

    [Fact]
    public async Task Opening_a_preview_tab_for_real_or_editing_it_makes_it_permanent()
    {
        using var rig = new WellRig();
        var p = await rig.Open("p.cs", preview: true);
        await rig.Open("p.cs"); // double-click / open without preview
        Assert.False(p.IsPreview);

        var q = await rig.Open("q.cs", preview: true);
        rig.Editors.Doc(q.Uri).ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "x")]);
        Assert.False(q.IsPreview);
        var r = await rig.Open("r.cs", preview: true);
        Assert.Contains(q, rig.Well.Tabs); // dirty ex-preview survives the next preview
        Assert.True(r.IsPreview);
    }

    [Fact]
    public async Task Pinning_moves_a_tab_to_the_front_and_close_others_spares_pinned_tabs()
    {
        using var rig = new WellRig();
        await rig.Open("a.cs");
        await rig.Open("b.cs");
        var c = await rig.Open("c.cs");
        rig.Well.Pin(c);
        Assert.Equal(["c.cs", "a.cs", "b.cs"], rig.Titles);
        var a = rig.Well.Find(WellRig.U("a.cs"))!;
        Assert.True(await rig.Well.CloseOthersAsync(a));
        Assert.Equal(["c.cs", "a.cs"], rig.Titles);
        Assert.True(await rig.Well.CloseAllAsync(includePinned: false));
        Assert.Equal(["c.cs"], rig.Titles);
        rig.Well.Unpin(c);
        Assert.False(c.IsPinned);
    }

    [Fact]
    public async Task Close_saved_keeps_dirty_tabs()
    {
        using var rig = new WellRig();
        await rig.Open("clean.cs");
        var dirty = await rig.Open("dirty.cs");
        rig.Editors.Doc(dirty.Uri).SetDirty(true);
        await rig.Well.CloseSavedAsync();
        Assert.Equal(["dirty.cs"], rig.Titles);
    }

    [Theory]
    [InlineData("Save", true, 1)]
    [InlineData("Don't Save", true, 0)]
    [InlineData("Cancel", false, 0)]
    [InlineData(null, false, 0)]
    public async Task Closing_a_dirty_tab_asks_save_dont_save_cancel(string? answer, bool closes, int saves)
    {
        using var rig = new WellRig();
        var tab = await rig.Open("a.cs");
        var doc = rig.Editors.Doc(tab.Uri);
        doc.SetDirty(true);
        rig.Dialogs.Answers.Enqueue(answer);
        var result = await rig.Well.CloseAsync(tab);
        Assert.Equal(closes, result);
        Assert.Equal(saves, doc.SaveCount);
        Assert.Equal(closes ? 0 : 1, rig.Well.Tabs.Count);
        Assert.Equal(3, rig.Dialogs.Shown.Single().Buttons.Count);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_tab_open()
    {
        using var rig = new WellRig();
        var tab = await rig.Open("a.cs");
        var doc = rig.Editors.Doc(tab.Uri);
        doc.SetDirty(true);
        doc.SaveFailure = new IOException("disk full");
        rig.Dialogs.Answers.Enqueue("Save");
        Assert.False(await rig.Well.CloseAsync(tab));
        Assert.Single(rig.Well.Tabs);
    }

    [Fact]
    public async Task Clicking_the_dock_close_button_on_a_dirty_tab_is_cancelled_and_routed_through_the_prompt()
    {
        using var rig = new WellRig();
        var tab = await rig.Open("a.cs");
        var doc = rig.Editors.Doc(tab.Uri);
        doc.SetDirty(true);
        rig.Dialogs.Answers.Enqueue("Cancel");
        rig.Factory.CloseDockable(tab); // what Dock's X button does
        await Task.Delay(50);
        Assert.Single(rig.Well.Tabs); // Dock's close was cancelled
        Assert.Single(rig.Dialogs.Shown);

        rig.Dialogs.Answers.Enqueue("Don't Save");
        rig.Factory.CloseDockable(tab);
        var deadline = Environment.TickCount64 + 3000;
        while (rig.Well.Tabs.Count > 0 && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.Empty(rig.Well.Tabs);
    }

    [Fact]
    public async Task Clicking_the_dock_close_button_on_a_clean_tab_just_closes_it_and_releases_the_document()
    {
        using var rig = new WellRig();
        var tab = await rig.Open("a.cs");
        rig.Factory.CloseDockable(tab);
        Assert.Empty(rig.Well.Tabs);
        await Task.Delay(20);
        Assert.Contains(WellRig.U("a.cs"), rig.Editors.Closed);
        Assert.Empty(rig.Dialogs.Shown);
    }

    [Fact]
    public async Task Reopen_closed_restores_most_recently_closed_first_and_skips_open_ones()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var b = await rig.Open("b.cs");
        await rig.Well.CloseAsync(a);
        await rig.Well.CloseAsync(b);
        Assert.True(await rig.Well.ReopenClosedAsync());
        Assert.Equal(["b.cs"], rig.Titles);
        Assert.True(await rig.Well.ReopenClosedAsync());
        Assert.Equal(2, rig.Well.Tabs.Count);
        Assert.False(await rig.Well.ReopenClosedAsync());
    }

    [Fact]
    public async Task Closing_the_active_tab_activates_the_most_recently_used_remaining_tab()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var b = await rig.Open("b.cs");
        var c = await rig.Open("c.cs");
        rig.Well.Activate(a);
        rig.Well.Activate(c);
        await rig.Well.CloseAsync(c);
        Assert.Same(a, rig.Well.Active);
        Assert.Equal([a, b], rig.Well.MruOrder);
    }

    [Fact]
    public async Task Split_right_creates_a_second_group_and_tabs_can_move_between_groups()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var b = await rig.Open("b.cs");
        rig.Well.SplitRight(b);
        Assert.Equal(2, rig.Factory.DocumentGroups().Count);
        Assert.NotSame(rig.Factory.GroupOf(a), rig.Factory.GroupOf(b));
        Assert.True(rig.Well.MoveToNextGroup(b));
        Assert.Same(rig.Factory.GroupOf(a), rig.Factory.GroupOf(b));
    }

    [Fact]
    public async Task Untitled_buffers_and_view_tabs_open_and_the_view_singleton_is_reused()
    {
        using var rig = new WellRig();
        var u = rig.Well.OpenUntitled();
        Assert.True(u.Doc.IsUntitled);
        var w1 = rig.Well.OpenView("welcome", "Welcome", () => new Avalonia.Controls.TextBlock());
        var w2 = rig.Well.OpenView("welcome", "Welcome", () => new Avalonia.Controls.TextBlock());
        Assert.Same(w1, w2);
        Assert.Equal(2, rig.Well.Tabs.Count);
        await rig.Well.CloseAsync(w1);
        var w3 = rig.Well.OpenView("welcome", "Welcome", () => new Avalonia.Controls.TextBlock());
        Assert.NotSame(w1, w3);
    }

    [Fact]
    public async Task Open_and_close_300_tabs_retains_nothing()
    {
        var rig = new WellRig();
        var weak = new List<WeakReference>();
        for (var i = 0; i < 300; i++)
        {
            var tab = await rig.Open($"f{i}.cs");
            weak.Add(new WeakReference(tab));
            weak.Add(new WeakReference(tab.Doc));
            await rig.Well.CloseAsync(tab);
        }
        await Task.Delay(50); // let the async document releases finish
        Assert.Empty(rig.Well.Tabs);
        Assert.Equal(0, rig.Editors.OpenCount);
        GcUntilCollected(weak);
        Assert.Equal(0, weak.Count(w => w.IsAlive));
        rig.Dispose();
    }

    private static void GcUntilCollected(List<WeakReference> weak)
    {
        for (var i = 0; i < 10 && weak.Any(w => w.IsAlive); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public async Task A_closed_tab_is_not_kept_alive_by_the_well_or_the_factory()
    {
        var rig = new WellRig();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var tab = await rig.Open("leak.cs");
            var doc = tab.Doc;
            await rig.Well.CloseAsync(tab);
            await Task.Delay(30);
            return [tab, doc];
        });
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Only_the_active_tabs_and_the_lru_budget_keep_a_live_editor_view()
    {
        using var rig = new WellRig();
        var tabs = new List<EditorTab>();
        for (var i = 0; i < 20; i++)
        {
            var t = await rig.Open($"f{i}.cs");
            t.EnsureView(); // what Dock's content presenter does on activation
            tabs.Add(t);
        }
        rig.Well.EnforceViewBudget();
        var live = tabs.Count(t => t.LiveView is not null);
        Assert.InRange(live, 1, DocumentWell.DefaultLiveViews + 1);
        Assert.NotNull(tabs[^1].LiveView); // the active one always survives
        Assert.Null(tabs[0].LiveView);

        // A dropped view is recreated on demand and remembers the caret.
        var first = tabs[0];
        var view = first.EnsureView();
        Assert.NotNull(rig.Editors.GetController(view));
    }

    [AvaloniaFact]
    public async Task A_dropped_editor_view_restores_caret_and_selection_when_recreated()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var view = a.EnsureView();
        var ctl = (FakeController)rig.Editors.GetController(view)!;
        ctl.SetSelection(Core.Text.TextRange.FromLines(2, 1, 2, 5), reveal: false);
        a.DisposeView();
        Assert.Null(a.LiveView);
        Assert.NotNull(a.SavedViewState);
        var again = (FakeController)rig.Editors.GetController(a.EnsureView())!;
        Assert.Equal(new Core.Text.TextPosition(2, 5), again.Caret);
        Assert.Equal(Core.Text.TextRange.FromLines(2, 1, 2, 5), again.Selections.Single());
    }
}
