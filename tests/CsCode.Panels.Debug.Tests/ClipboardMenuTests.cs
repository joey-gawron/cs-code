using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.Panels.Debug.Views;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Clipboard brief A1 in the debug panels: Locals/Autos/Watch copy the value, the name and the expression; the Call Stack copies one frame or
/// the whole stack; the Debug Console copies selected lines (menu and keys); Ctrl+Insert works like Ctrl+C; every view is the Edit menu's
/// clipboard target while it has focus (<see cref="IClipboardTarget"/>).
/// </summary>
public class ClipboardMenuTests
{
    private static async Task Settle(int ms = 60)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Task.Delay(ms);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static Window Host(Control view)
    {
        var window = new Window { Width = 900, Height = 320, Content = view };
        window.Show();
        return window;
    }

    private static string[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToArray();

    [AvaloniaFact]
    public async Task Locals_copy_value_name_and_expression_and_Ctrl_Insert_copies_the_value()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(frame: 2);
        var panel = sc.View("debug.locals");
        var window = Host(panel);
        await Settle();
        var view = panel.GetVisualDescendants().OfType<VariablesTreeView>().First();
        var tree = ScreenshotTests.TreeOf(panel);
        await PanelRig.Eventually(() => tree.Rows.Count >= 8, what: "locals rows");
        var s = tree.Rows.Single(r => r.Name == "s");
        tree.SelectedRow = s;
        await Settle();

        var menu = view.BuildMenu(tree, s);
        Assert.Contains("Copy Value", Headers(menu));
        Assert.Contains("Copy Name", Headers(menu));
        Assert.Contains("Copy Expression", Headers(menu));
        await tree.CopyNameAsync(s);
        Assert.Equal("s", sc.Rig.Window.Clipboard[^1]);

        Assert.True(((IClipboardTarget)view).CanCopy);
        await ((IClipboardTarget)view).CopyAsync();
        Assert.Equal("\"hello world\"", sc.Rig.Window.Clipboard[^1]);

        var rows = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Rows");
        (rows.ContainerFromItem(s) as Control)?.Focus();
        var before = sc.Rig.Window.Clipboard.Count;
        window.KeyPress(Key.Insert, RawInputModifiers.Control, PhysicalKey.Insert, null);
        await PanelRig.Eventually(() => sc.Rig.Window.Clipboard.Count == before + 1, what: "Ctrl+Insert copied");
        Assert.Equal("\"hello world\"", sc.Rig.Window.Clipboard[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_call_stack_copies_the_selected_frame_or_the_whole_stack()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var rig = new PanelRig();
        var vm = new CallStackViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 3, what: "frames");
        var view = new CallStackView { DataContext = vm };
        var window = Host(view);
        await Settle();
        IClipboardTarget target = view;

        vm.SelectedRow = vm.Rows[1];
        await target.CopyAsync();
        Assert.Equal(CallStackView.FrameText(vm.Rows[1]), rig.Window.Clipboard[^1]);
        Assert.Contains("Sample.Program.Outer(int n)", rig.Window.Clipboard[^1]);
        Assert.Contains("[", rig.Window.Clipboard[^1]); // with its location

        vm.SelectedRow = null;
        await target.CopyAsync();
        Assert.Equal(3, rig.Window.Clipboard[^1].Split(Environment.NewLine).Length);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task The_debug_console_copies_selected_lines_from_its_menu_and_keys()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var rig = new PanelRig();
        var vm = new DebugConsoleViewModel(rig.Services);
        var view = new ConsoleView { DataContext = vm };
        var window = Host(view);
        rig.StopAt(0);
        rig.Debug.EmitOutput(new CsCode.Core.Debugging.DebugOutputEventArgs(rig.Session, "stdout", "first output line\nsecond output line\n"));
        await PanelRig.Eventually(() => vm.Lines.Count(l => l.Text.Contains("output line")) == 2, what: "console lines");
        var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Lines");
        foreach (var line in vm.Lines.Where(l => l.Text.Contains("output line"))) list.SelectedItems!.Add(line);
        await Settle();

        var menu = view.BuildLinesMenu();
        Assert.Equal(["Copy", "Copy All", "Clear All"], Headers(menu));
        Assert.True(menu.Items.OfType<MenuItem>().First().IsEnabled);
        await ((IClipboardTarget)view).CopyAsync();
        Assert.Equal("first output line" + Environment.NewLine + "second output line", rig.Window.Clipboard[^1]);

        (list.ContainerFromIndex(list.SelectedIndex) as Control)?.Focus();
        var before = rig.Window.Clipboard.Count;
        window.KeyPress(Key.Insert, RawInputModifiers.Control, PhysicalKey.Insert, null);
        await PanelRig.Eventually(() => rig.Window.Clipboard.Count == before + 1, what: "Ctrl+Insert copied");
        window.Close();
        view.Dispose();
    }
}
