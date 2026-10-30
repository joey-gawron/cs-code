using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// Split terminals in the Terminal view over fake PTYs: Split Terminal shows the group's panes side by side (each sized to its half), the
/// new pane takes the keyboard, focus moves between panes (commands and clicks), keys reach the focused pane's program, panes resize, kill
/// closes one pane and focus moves to its neighbour, and the dropdown lists groups.
/// </summary>
public sealed class SplitPanelTests
{
    private static string Typed(PanelRig rig, int hostIndex) => rig.Hosts[hostIndex].Written;

    [AvaloniaFact]
    public async Task Split_shows_two_panes_side_by_side_and_the_new_one_takes_the_keyboard()
    {
        await using var rig = await new PanelRig(1000, 400).ReadyAsync();
        var first = rig.Session;
        var (fullColumns, _) = rig.Control.GridSize;
        rig.Control.Focus();
        Assert.True(await PanelRig.Pump(() => rig.Control.IsKeyboardFocusWithin));
        Assert.Equal(false, rig.Keys.Get(TerminalPanelRuntime.SplitPaneActiveContextKey));

        await rig.Commands.ExecuteAsync(TerminalCommandSet.Split);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 2 && rig.Hosts.Count == 2), "no second pane");
        var second = rig.Session;
        Assert.NotSame(first, second);
        Assert.Same(second, rig.View.Panes[1].Session);
        Assert.Same(first, rig.View.Panes[0].Session);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes[1].Control.IsKeyboardFocusWithin), "the new pane did not get the keyboard");
        Assert.Equal(true, rig.Keys.Get(TerminalPanelRuntime.SplitPaneActiveContextKey));
        Assert.Single(rig.Service.Groups);

        // each pane's program is told its own (about half) width
        Assert.True(await PanelRig.Pump(() => rig.Hosts[0].Sizes.Count > 0 && rig.Hosts[0].Sizes[^1].Columns < fullColumns * 0.6
            && rig.Hosts[1].Sizes.Count > 0 && rig.Hosts[1].Sizes[^1].Columns < fullColumns * 0.6),
            $"panes not halved: {string.Join(",", rig.Hosts[0].Sizes)} / {string.Join(",", rig.Hosts[1].Sizes)} of {fullColumns}");
        var left = rig.View.Panes[0].Control.Bounds;
        var right = rig.View.Panes[1].Control.Bounds;
        Assert.True(rig.View.Panes[1].TranslatePoint(default, rig.View.Panes[0]) is { X: > 0 } p && p.X >= left.Width, "the panes are not side by side");
        Assert.True(Math.Abs(left.Width - right.Width) < 20, $"{left.Width} vs {right.Width}");

        // typing goes to the focused pane's program only
        rig.Hosts[0].ClearWritten();
        rig.Hosts[1].ClearWritten();
        rig.Window.KeyTextInput("r");
        Assert.True(await PanelRig.Pump(() => Typed(rig, 1) == "r"));
        Assert.Equal("", Typed(rig, 0));
    }

    [AvaloniaFact]
    public async Task Focus_moves_between_panes_by_command_and_by_click_and_keys_follow()
    {
        await using var rig = await new PanelRig(1000, 400).ReadyAsync();
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Split);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 2 && rig.View.Panes[1].Control.IsKeyboardFocusWithin));
        var (first, second) = (rig.View.Panes[0].Session!, rig.View.Panes[1].Session!);

        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusPreviousPane);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes[0].Control.IsKeyboardFocusWithin), "focusPreviousPane did not move the keyboard");
        Assert.Same(first, rig.Service.ActiveSession);
        rig.Hosts[0].ClearWritten();
        rig.Window.KeyTextInput("a");
        Assert.True(await PanelRig.Pump(() => Typed(rig, 0) == "a"));

        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusPreviousPane); // wraps around to the last pane
        Assert.True(await PanelRig.Pump(() => rig.View.Panes[1].Control.IsKeyboardFocusWithin));
        Assert.Same(second, rig.Service.ActiveSession);
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusNextPane);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes[0].Control.IsKeyboardFocusWithin));

        // a click into the right pane makes it the active terminal
        var pane = rig.View.Panes[1].Control;
        var point = pane.TranslatePoint(new Avalonia.Point(30, 10), rig.Window)!.Value;
        rig.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Assert.True(await PanelRig.Pump(() => ReferenceEquals(rig.Service.ActiveSession, second) && pane.IsKeyboardFocusWithin), "a click did not activate the pane");
        Assert.Same(pane, rig.View.Control);
    }

    [AvaloniaFact]
    public async Task Resize_moves_the_divider_next_to_the_active_pane_by_four_cells()
    {
        await using var rig = await new PanelRig(1000, 400).ReadyAsync();
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Split);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 2 && rig.View.Panes[1].Control.Bounds.Width > 0));
        double Width(int i) => rig.View.Panes[i].Bounds.Width;
        var cell = rig.Control.Font.CellWidth;
        var before = Width(0);

        // the active pane is the last one: Resize Left moves its left divider left (it grows)
        await rig.Commands.ExecuteAsync(TerminalCommandSet.ResizePaneLeft);
        Assert.True(await PanelRig.Pump(() => Math.Abs(Width(0) - (before - 4 * cell)) < 2), $"{Width(0)} (was {before}, cell {cell})");
        await rig.Commands.ExecuteAsync(TerminalCommandSet.ResizePaneRight);
        Assert.True(await PanelRig.Pump(() => Math.Abs(Width(0) - before) < 2), $"{Width(0)} (was {before})");

        // the first pane: Resize Right moves its right divider right (it grows); the layout is kept with the group
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusPreviousPane);
        Assert.True(await PanelRig.Pump(() => ReferenceEquals(rig.Service.ActiveSession, rig.View.Panes[0].Session)));
        await rig.Commands.ExecuteAsync(TerminalCommandSet.ResizePaneRight);
        Assert.True(await PanelRig.Pump(() => Math.Abs(Width(0) - (before + 4 * cell)) < 2), $"{Width(0)} (was {before})");
        Assert.NotNull(rig.Service.Groups[0].PaneWeights);
    }

    [AvaloniaFact]
    public async Task Kill_closes_the_active_pane_and_the_keyboard_moves_to_the_remaining_one()
    {
        await using var rig = await new PanelRig(1000, 400).ReadyAsync();
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Split);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 2 && rig.View.Panes[1].Control.IsKeyboardFocusWithin));
        var survivor = rig.View.Panes[0].Session!;
        var killedHost = rig.Hosts[1];

        await rig.Commands.ExecuteAsync(TerminalCommandSet.Kill);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 1 && killedHost.Killed), "the pane did not close");
        Assert.Same(survivor, rig.Service.ActiveSession);
        Assert.True(await PanelRig.Pump(() => rig.View.Control.IsKeyboardFocusWithin && ReferenceEquals(rig.View.Control.Session, survivor)),
            "the keyboard did not move to the remaining pane");
        Assert.Equal(false, rig.Keys.Get(TerminalPanelRuntime.SplitPaneActiveContextKey));
        // the remaining pane takes the whole width again
        Assert.True(await PanelRig.Pump(() => rig.View.Panes[0].Bounds.Width > 900), $"{rig.View.Panes[0].Bounds.Width}");
    }

    [AvaloniaFact]
    public async Task The_dropdown_lists_groups_and_switching_shows_that_groups_panes()
    {
        await using var rig = await new PanelRig(1000, 400).ReadyAsync();
        var box = rig.View.GetVisualDescendantsOfType<Avalonia.Controls.ComboBox>().First();
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Split);
        // the closed dropdown shows the split group's new label at once
        Assert.True(await PanelRig.Pump(() => box.SelectionBoxItem is string s && s.Contains(", ", StringComparison.Ordinal)), $"{box.SelectionBoxItem}");
        await rig.Commands.ExecuteAsync(TerminalCommandSet.New);
        Assert.True(await PanelRig.Pump(() => rig.Service.Groups.Count == 2 && rig.View.Panes.Count == 1 && rig.Hosts.Count == 3));
        var picker = rig.View.GetVisualDescendantsOfType<Avalonia.Controls.ComboBox>().First();
        var labels = picker.Items.OfType<Avalonia.Controls.ComboBoxItem>().Select(i => (string)i.Content!).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Matches(@"^1: [^,]+, [^,]+$", labels[0]); // "1: bash, bash": one entry for the split group
        Assert.Matches(@"^2: [^,]+$", labels[1]);

        // Focus Previous Terminal Group (Ctrl+PgUp) shows the split group again, with its focused pane active
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusPrevious);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 2));
        Assert.Same(rig.Service.Groups[0].Terminals[1], rig.Service.ActiveSession);
        picker.SelectedIndex = 1;
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 1 && ReferenceEquals(rig.Service.ActiveGroup, rig.Service.Groups[1])));
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
