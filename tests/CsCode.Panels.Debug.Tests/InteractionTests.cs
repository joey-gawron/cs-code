using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>Keyboard- and mouse-driven interaction with the real views in a headless window (focus, key routing, inline editors, drag and drop).</summary>
public class InteractionTests
{
    private static readonly Dictionary<Key, PhysicalKey> Physical = new()
    {
        [Key.Up] = PhysicalKey.ArrowUp, [Key.Down] = PhysicalKey.ArrowDown, [Key.Left] = PhysicalKey.ArrowLeft, [Key.Right] = PhysicalKey.ArrowRight,
        [Key.Enter] = PhysicalKey.Enter, [Key.Escape] = PhysicalKey.Escape, [Key.F2] = PhysicalKey.F2, [Key.Delete] = PhysicalKey.Delete,
        [Key.C] = PhysicalKey.C, [Key.Space] = PhysicalKey.Space, [Key.Tab] = PhysicalKey.Tab, [Key.End] = PhysicalKey.End,
    };

    private static async Task Settle(int ms = 60)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(ms / 3);
        }
    }

    private static async Task Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, Physical.GetValueOrDefault(key), null);
        window.KeyRelease(key, modifiers, Physical.GetValueOrDefault(key), null);
        await Settle();
    }

    private static Window Host(Control view, double width = 900, double height = 320)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        return window;
    }

    private static Point Center(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException("control is not laid out");

    private static async Task Click(Window window, ListBox list, object item, double xFraction = 0.5)
    {
        var container = list.ContainerFromItem(item) as Control ?? throw new InvalidOperationException("item not realised: " + item);
        var p = container.TranslatePoint(new Point(container.Bounds.Width * xFraction, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Settle();
    }

    private static ListBox Rows(Control view, string name = "Rows") => view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == name);

    private static async Task<(Scenario Sc, Control View, Window Window, VariablesTreeViewModel Tree)> LocalsHost()
    {
        TestTheme.Apply(VsThemes.Dark);
        var sc = new Scenario(frame: 2);
        var view = sc.View("debug.locals");
        var window = Host(view);
        var tree = ScreenshotTests.TreeOf(view);
        await PanelRig.Eventually(() => tree.Rows.Count >= 8, what: "locals rows");
        await Settle();
        return (sc, view, window, tree);
    }

    [AvaloniaFact]
    public async Task Arrow_keys_navigate_and_expand_collapse_the_variables_tree()
    {
        var (sc, view, window, tree) = await LocalsHost();
        using var _ = sc;
        var list = Rows(view);
        var item = tree.Rows.Single(r => r.Name == "item");
        await Click(window, list, item);
        Assert.Same(item, tree.SelectedRow);

        await Press(window, Key.Right); // expand
        await PanelRig.Eventually(() => tree.Rows.Any(r => r.Name == "Count"), what: "children loaded");
        Assert.True(item.IsExpanded);
        await Press(window, Key.Right); // already expanded: into the first child
        Assert.Equal("Name", tree.SelectedRow?.Name);
        await Press(window, Key.Down);
        Assert.Equal("Count", tree.SelectedRow?.Name);
        await Press(window, Key.Left); // a leaf: to its parent
        Assert.Same(item, tree.SelectedRow);
        await Press(window, Key.Left); // collapse
        Assert.False(item.IsExpanded);
        Assert.DoesNotContain(tree.Rows, r => r.Name == "Count");
        await Press(window, Key.Up);
        Assert.Equal("nothing", tree.SelectedRow?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_edits_a_value_in_place_and_escape_cancels()
    {
        var (sc, view, window, tree) = await LocalsHost();
        using var _ = sc;
        sc.Session.SetVariableHandler = (_, name, value) => new DebugVariable(name, value, "int");
        var list = Rows(view);
        var x = tree.Rows.Single(r => r.Name == "x");
        await Click(window, list, x);

        await Press(window, Key.Enter);
        Assert.True(x.IsEditingValue);
        await PanelRig.Eventually(() => (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as TextBox)?.Text == "41", what: "editor focused with the value selected");
        await Press(window, Key.Escape);
        Assert.False(x.IsEditing);
        Assert.Equal("41", x.DisplayValue);
        var afterEsc = TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement()?.GetType().Name;

        await Press(window, Key.Enter);
        var afterEnter = $"editing={x.IsEditingValue} focus={TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement()?.GetType().Name}";
        try { await PanelRig.Eventually(() => (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as TextBox)?.Text == "41", 1500, "editor focused again"); }
        catch (TimeoutException) { Assert.Fail($"after Escape focus={afterEsc}; after Enter {afterEnter}"); }
        window.KeyTextInput("99"); // typing replaces the selected text
        await Settle();
        Assert.Equal("99", x.EditText);
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => x.DisplayValue == "99" && !x.IsEditing, what: "committed");
        Assert.Equal(1, sc.Session.SetVariableCalls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_C_copies_the_selected_value()
    {
        var (sc, view, window, tree) = await LocalsHost();
        using var _ = sc;
        await Click(window, Rows(view), tree.Rows.Single(r => r.Name == "s"));
        await Press(window, Key.C, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control); // Cmd+C on macOS (ClipboardKeys)
        await PanelRig.Eventually(() => sc.Rig.Window.Clipboard.Count == 1);
        Assert.Equal("\"hello world\"", sc.Rig.Window.Clipboard[0]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Double_click_toggles_and_the_pager_row_loads_more()
    {
        var (sc, view, window, tree) = await LocalsHost();
        using var _ = sc;
        var list = Rows(view);
        var numbers = tree.Rows.Single(r => r.Name == "numbers");
        await Click(window, list, numbers);
        await Press(window, Key.Right);
        await PanelRig.Eventually(() => tree.Rows.Any(r => r.Kind == VariableRowKind.More), what: "first page");
        var more = tree.Rows.Single(r => r.Kind == VariableRowKind.More);
        Assert.Equal("… 152 more", more.Name); // 2 named + 250 indexed children, 100 shown
        await Press(window, Key.End); // the keyboard walks to the last row, the pager (focus follows the selection)
        Assert.Same(more, tree.SelectedRow);
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => tree.Rows.Count(r => r.Depth == 1) >= 200, what: "second page");
        window.Close();
    }

    // ------------------------------------------------------------------ Watch

    private static async Task<(Scenario Sc, Control View, Window Window, WatchViewModel Vm)> WatchHost(params string[] expressions)
    {
        TestTheme.Apply(VsThemes.Dark);
        var sc = new Scenario(frame: 2);
        foreach (var e in expressions) sc.Rig.Debug.AddWatch(e);
        var view = sc.View("debug.watch");
        var window = Host(view);
        var vm = (WatchViewModel)view.DataContext!;
        await PanelRig.Eventually(() => vm.Tree.Rows.Count(r => r.Kind == VariableRowKind.Watch && r.HasValue()) == expressions.Length, what: "watches evaluated");
        await Settle();
        return (sc, view, window, vm);
    }

    [AvaloniaFact]
    public async Task F2_renames_a_watch_in_place_enter_commits_and_delete_removes()
    {
        var (sc, view, window, vm) = await WatchHost("x + 1", "s.Length");
        using var _ = sc;
        var list = Rows(view);
        var first = vm.Tree.Rows.First(r => r.Kind == VariableRowKind.Watch);
        await Click(window, list, first);

        await Press(window, Key.F2);
        Assert.True(first.IsEditingExpression);
        await PanelRig.Eventually(() => (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as TextBox)?.Text == "x + 1", what: "expression editor focused");
        window.KeyTextInput("numbers.Count * 2");
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => sc.Rig.Debug.Watches.Select(w => w.Expression).SequenceEqual(["numbers.Count * 2", "s.Length"]), what: "renamed");

        var renamed = vm.Tree.Rows.First(r => r.Kind == VariableRowKind.Watch);
        await PanelRig.Eventually(() => renamed.Name == "numbers.Count * 2");
        await Click(window, list, vm.Tree.Rows.First(r => r.Kind == VariableRowKind.Watch));
        await Press(window, Key.Delete);
        await PanelRig.Eventually(() => sc.Rig.Debug.Watches.Count == 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_into_the_empty_last_row_adds_a_watch_and_alt_arrows_reorder()
    {
        var (sc, view, window, vm) = await WatchHost("a", "b");
        using var _ = sc;
        var list = Rows(view);
        await Click(window, list, vm.NewRow);
        await Press(window, Key.Enter); // activates the placeholder row: its editor opens
        Assert.True(vm.NewRow.IsEditingExpression);
        await PanelRig.Eventually(() => TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() is TextBox, what: "editor focused");
        window.KeyTextInput("c");
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => sc.Rig.Debug.Watches.Count == 3, what: "added");

        var c = vm.Tree.Rows.First(r => r.Name == "c");
        await Click(window, list, c);
        await Press(window, Key.Up, RawInputModifiers.Alt);
        await PanelRig.Eventually(() => sc.Rig.Debug.Watches.Select(w => w.Expression).SequenceEqual(["a", "c", "b"]), what: "moved up");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_a_watch_onto_another_row_reorders_the_list()
    {
        var (sc, view, window, vm) = await WatchHost("a", "b", "c");
        using var _ = sc;
        var list = Rows(view);
        Control Container(string name) => (Control)list.ContainerFromItem(vm.Tree.Rows.First(r => r.Name == name))!;
        var from = Center(window, Container("a"));
        var to = Center(window, Container("c"));

        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 6; step++)
        {
            window.MouseMove(new Point(from.X, from.Y + (to.Y - from.Y) * step / 6), RawInputModifiers.LeftMouseButton);
            await Settle(20);
        }
        Assert.True(vm.Tree.Rows.First(r => r.Name == "c").IsDropTarget, "the row under the pointer shows where the watch will land");
        window.MouseUp(to, MouseButton.Left);
        await PanelRig.Eventually(() => sc.Rig.Debug.Watches.Select(w => w.Expression).SequenceEqual(["b", "c", "a"]), what: "dropped");
        Assert.DoesNotContain(vm.Tree.Rows, r => r.IsDropTarget);
        window.Close();
    }

    // ------------------------------------------------------------------ Call Stack, Breakpoints, Console

    [AvaloniaFact]
    public async Task Call_stack_enter_picks_the_frame_and_arrows_expand_threads()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = sc.View("debug.callstack");
        var window = Host(view);
        var model = (CallStackViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Rows.Count > 5, what: "frames");
        await Settle();
        var list = Rows(view);

        var worker = model.Rows.Single(r => r.Kind == CallStackRowKind.Thread && r.ThreadId == 2);
        await Click(window, list, worker, xFraction: 0.5);
        await PanelRig.Eventually(() => worker.IsExpanded || model.Rows.Any(r => r.ThreadId == 2 && r.Kind == CallStackRowKind.Frame), what: "clicking a thread toggles it");
        await Press(window, Key.Left); // collapse again
        await PanelRig.Eventually(() => !model.Rows.Any(r => r.ThreadId == 2 && r.Kind == CallStackRowKind.Frame), what: "collapsed");
        await Press(window, Key.Right);
        await PanelRig.Eventually(() => model.Rows.Any(r => r.ThreadId == 2 && r.Kind == CallStackRowKind.Frame), what: "expanded by the Right key");

        var outer = model.Rows.First(r => r.Kind == CallStackRowKind.Frame && r.Text.Contains("Outer"));
        await Click(window, list, outer);
        await PanelRig.Eventually(() => sc.Rig.Debug.FocusCalls.Any(c => c.FrameId == 101), what: "a click focuses the frame");
        sc.Rig.Navigation.Opened.Clear();
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => sc.Rig.Navigation.Opened.Count == 1, what: "Enter opens the source");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Breakpoints_space_toggles_enter_navigates_and_delete_removes()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = sc.View("debug.breakpoints");
        var window = Host(view, height: 360);
        var model = (BreakpointsViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Rows.Count >= 10, what: "rows");
        await Settle();
        var list = Rows(view);
        var row = model.Rows.First(r => r.Kind == BreakpointRowKind.Source);
        var id = row.Id;
        await Click(window, list, row, xFraction: 0.4);

        await Press(window, Key.Space);
        await PanelRig.Eventually(() => !sc.Rig.Debug.FakeBreakpoints.SourceBreakpoints.Single(b => b.Id == id).Enabled, what: "disabled");
        await Press(window, Key.Space);
        await PanelRig.Eventually(() => sc.Rig.Debug.FakeBreakpoints.SourceBreakpoints.Single(b => b.Id == id).Enabled, what: "enabled again");

        sc.Rig.Navigation.Opened.Clear();
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => sc.Rig.Navigation.Opened.Count == 1);
        Assert.Equal(new Uri("file:///work/Sample/Program.cs"), sc.Rig.Navigation.Opened[0].Uri);

        var before = sc.Rig.Debug.FakeBreakpoints.SourceBreakpoints.Count;
        await Press(window, Key.Delete);
        await PanelRig.Eventually(() => sc.Rig.Debug.FakeBreakpoints.SourceBreakpoints.Count == before - 1, what: "deleted");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Console_input_enter_runs_shift_enter_adds_a_line_and_up_walks_the_history()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = sc.View("debug.console");
        var window = Host(view);
        var model = (DebugConsoleViewModel)view.DataContext!;
        await Settle();
        var input = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "Input");
        window.MouseDown(Center(window, input), MouseButton.Left);
        window.MouseUp(Center(window, input), MouseButton.Left);
        await Settle();

        window.KeyTextInput("item.Count");
        await Press(window, Key.Enter);
        await PanelRig.Eventually(() => model.Lines.Any(l => l.Kind == ConsoleLineKind.Result), what: "result line");
        Assert.Equal("", model.Input);

        window.KeyTextInput("a");
        await Press(window, Key.Enter, RawInputModifiers.Shift);
        window.KeyTextInput("b");
        await Settle();
        Assert.Equal("a" + Environment.NewLine + "b", model.Input.Replace("\n", Environment.NewLine).Replace("\r\n", Environment.NewLine));

        model.Input = "";
        await Press(window, Key.Up);
        Assert.Equal("item.Count", model.Input); // history
        await Press(window, Key.Down);
        Assert.Equal("", model.Input);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Console_completions_are_accepted_with_tab()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = sc.View("debug.console");
        var window = Host(view);
        var model = (DebugConsoleViewModel)view.DataContext!;
        await Settle();
        var input = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "Input");
        input.Focus();
        await Settle();
        window.KeyTextInput("item.");
        await PanelRig.Eventually(() => model.CompletionsOpen, what: "completion list");
        await Press(window, Key.Down);
        Assert.Equal("Count", model.SelectedCompletion?.Label);
        await Press(window, Key.Tab);
        Assert.Equal("item.Count", model.Input);
        Assert.False(model.CompletionsOpen);
        window.Close();
    }
}

internal static class NodeExtensions
{
    public static bool HasValue(this VariableNodeViewModel node) => node.DisplayValue.Length > 0;
}
