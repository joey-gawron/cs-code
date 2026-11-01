using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Navigation.Tests;

/// <summary>The Hierarchy window in a real (headless) window: selection previews, Enter opens, the keys, the toolbar, the menu, disposal.</summary>
public class HierarchyViewTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, HierarchyView View) Show(Rig rig)
    {
        var view = (HierarchyView)new HierarchyToolWindow(rig.Host).CreateView(rig.Provider);
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        Pump();
        return (window, view);
    }

    private static string Rows(HierarchyModel m) => string.Join("|", m.Rows.Select(r => r.ToString().Trim()));

    [AvaloniaFact]
    public async Task Selecting_a_row_previews_its_call_site_keeping_the_keyboard_and_enter_opens_it()
    {
        using var rig = new Rig();
        rig.CallGraph();
        var (window, view) = Show(rig);
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.OutgoingCalls);
        Pump();
        Assert.Equal(3, view.List.ItemCount);
        Assert.Same(rig.Host.Model.Roots[0], view.List.SelectedItem); // the model's selection, shown without navigating
        Assert.Empty(rig.Navigation.Opened);

        view.List.SelectedIndex = 1; // Step: called twice from Run
        Pump();
        var preview = Assert.Single(rig.Navigation.Opened);
        Assert.Equal((FakeCallGraph.FileA, (TextRange?)TextRange.FromLines(11, 8, 11, 12), true, true), preview);

        ToolWindowFocusRow(view);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Pump();
        Assert.Equal((FakeCallGraph.FileA, (TextRange?)TextRange.FromLines(11, 8, 11, 12), false, false), rig.Navigation.Opened[^1]);
        window.Close();
    }

    private static void ToolWindowFocusRow(HierarchyView view)
    {
        Assert.True(view.FocusPreferred());
        Pump();
    }

    [AvaloniaFact]
    public async Task Right_expands_left_collapses_then_goes_to_the_parent()
    {
        using var rig = new Rig();
        rig.CallGraph();
        var (window, view) = Show(rig);
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.IncomingCalls);
        Pump();
        var m = rig.Host.Model;
        m.Selected = m.Rows[1]; // Main
        Pump();
        ToolWindowFocusRow(view);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await WaitFor(() => m.Rows[1].Children is not null);
        Pump();
        Assert.False(m.Rows[1].HasChildren); // nobody calls Main

        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); // collapsed already: to the parent
        Pump();
        Assert.Same(m.Roots[0], m.Selected);
        Assert.Same(m.Roots[0], view.List.SelectedItem);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Pump();
        Assert.Equal("Run [+]", Rows(m));
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await WaitFor(() => m.Rows.Count == 2);
        Assert.Equal("Run [-]|Main", Rows(m));
        window.Close();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException();
            Pump();
            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public async Task The_direction_button_says_what_it_shows_next_and_flips_the_tree()
    {
        using var rig = new Rig();
        rig.CallGraph();
        var (window, view) = Show(rig);
        Assert.False(view.DirectionButton.IsEnabled); // nothing to flip yet
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.IncomingCalls);
        Pump();
        Assert.True(view.DirectionButton.IsEnabled);
        Assert.Equal("Show Outgoing Calls", ToolTip.GetTip(view.DirectionButton));
        view.DirectionButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => rig.Host.Model.Rows.Count == 3);
        Assert.Equal(HierarchyDirection.OutgoingCalls, rig.Host.Model.Direction);
        Assert.Equal("Show Incoming Calls", ToolTip.GetTip(view.DirectionButton));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_row_menu_goes_to_the_call_site_or_the_definition_and_reroots()
    {
        using var rig = new Rig();
        rig.CallGraph();
        var (window, view) = Show(rig);
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.OutgoingCalls);
        Pump();
        var step = rig.Host.Model.Rows[1];
        var entries = view.MenuEntries(step).Where(e => !e.IsSeparator).ToList();
        Assert.Equal(["Go To Call Site", "Go To Definition", "Show Call Hierarchy", "Expand", "Copy", "Refresh"], entries.Select(e => e.AccessibleName));
        await entries[1].Run!();
        Assert.Equal((FakeCallGraph.FileB, (TextRange?)TextRange.FromLines(3, 4, 3, 8), false, false), rig.Navigation.Opened[^1]);
        await entries[2].Run!();
        Assert.Equal("Calls From 'Step'", rig.Host.Model.Header);
        var root = view.MenuEntries(rig.Host.Model.Roots[0]).Single(e => e.AccessibleName == "Show Call Hierarchy");
        Assert.False(root.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_closed_view_is_collectable_and_the_model_keeps_its_tree_for_the_next_one()
    {
        using var rig = new Rig();
        rig.CallGraph();
        await rig.Host.ShowAtAsync(rig.Doc, new TextPosition(10, 4), HierarchyDirection.IncomingCalls);
        LeakAssert.NotRetained(() =>
        {
            var (window, view) = Show(rig);
            Assert.Equal(2, view.List.ItemCount);
            window.Content = null;
            window.Close();
            view.Dispose();
            Pump();
            return [view];
        });
        var (w2, v2) = Show(rig);
        Assert.Equal(2, v2.List.ItemCount);
        Assert.Equal("Calls To 'Run'", rig.Host.Model.Header);
        w2.Close();
    }
}
