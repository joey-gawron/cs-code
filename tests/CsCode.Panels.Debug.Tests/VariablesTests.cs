using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class VariablesTests
{
    private static async Task<LocalsViewModel> StoppedLocals(PanelRig rig, int frameIndex = 2)
    {
        var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(frameIndex);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count > 0, what: "locals rows");
        return vm;
    }

    private static string Names(VariablesTreeViewModel tree) => string.Join(",", tree.Rows.Select(r => r.Name));

    [AvaloniaFact]
    public async Task Locals_show_the_variables_of_the_focused_frame_after_a_stop()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        Assert.Equal("x,s,o", Names(vm.Tree));
        Assert.Equal("41", vm.Tree.Rows[0].DisplayValue);
        Assert.Equal("\"hello\"", vm.Tree.Rows[1].DisplayValue); // already quoted by the adapter
        Assert.Equal("int", vm.Tree.Rows[0].Type);
        Assert.Equal(1, rig.Session.ScopeCalls);
    }

    [AvaloniaFact]
    public async Task Locals_follow_the_frame_picked_in_the_call_stack()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        await rig.Debug.SetFocusedFrameAsync("s1", 1, 100); // user picks Add()
        await PanelRig.Eventually(() => Names(vm.Tree) == "a,b,sum", what: "locals of frame 100");
        Assert.Equal("42", vm.Tree.Rows[2].DisplayValue);
    }

    [AvaloniaFact]
    public async Task Children_are_fetched_lazily_once_per_expansion_and_cached_while_stopped()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        var o = vm.Tree.Rows.Single(r => r.Name == "o");
        Assert.True(o.HasChildren);
        var before = rig.Session.VariableCalls;

        await vm.Tree.ToggleAsync(o);
        Assert.Equal(before + 1, rig.Session.VariableCalls);
        Assert.Equal("x,s,o,Name,Count", Names(vm.Tree));
        Assert.Equal(1, vm.Tree.Rows[3].Depth);

        vm.Tree.Collapse(o);
        Assert.Equal("x,s,o", Names(vm.Tree));
        await vm.Tree.ToggleAsync(o);
        Assert.Equal(before + 1, rig.Session.VariableCalls); // re-expanding while still stopped costs no request
        Assert.Equal("x,s,o,Name,Count", Names(vm.Tree));
    }

    [AvaloniaFact]
    public async Task Large_nodes_are_paged_100_at_a_time_with_a_more_row()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[2000] = Enumerable.Range(0, 250).Select(i => new DebugVariable($"[{i}]", i.ToString(), "int")).ToList();
        rig.Session.Variables[1200][2] = new DebugVariable("arr", "int[250]", "int[]", 2000, "arr", null, 250);
        using var vm = await StoppedLocals(rig);
        var arr = vm.Tree.Rows.Single(r => r.Name == "arr");

        await vm.Tree.ToggleAsync(arr);
        Assert.Equal(3 + 100 + 1, vm.Tree.Rows.Count);
        var more = vm.Tree.Rows[^1];
        Assert.Equal(VariableRowKind.More, more.Kind);
        Assert.Equal("… 150 more", more.Name);
        Assert.Equal((2000, 0, 100), rig.Session.VariableRequests.Last());

        await vm.Tree.ActivateAsync(more);
        Assert.Equal(3 + 200 + 1, vm.Tree.Rows.Count);
        Assert.Equal("… 50 more", vm.Tree.Rows[^1].Name);
        Assert.Equal((2000, 100, 100), rig.Session.VariableRequests.Last());

        await vm.Tree.ActivateAsync(vm.Tree.Rows[^1]);
        Assert.Equal(3 + 250, vm.Tree.Rows.Count);
        Assert.DoesNotContain(vm.Tree.Rows, r => r.Kind == VariableRowKind.More);
        Assert.Equal("[249]", vm.Tree.Rows[^1].Name);
    }

    [AvaloniaFact]
    public async Task A_million_element_array_costs_one_page_of_rows_not_a_million()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[2000] = Enumerable.Range(0, 100_000).Select(i => new DebugVariable($"[{i}]", i.ToString(), "int")).ToList();
        rig.Session.Variables[1200][2] = new DebugVariable("big", "int[1000000]", "int[]", 2000, "big", null, 1_000_000);
        using var vm = await StoppedLocals(rig);
        await vm.Tree.ToggleAsync(vm.Tree.Rows.Single(r => r.Name == "big"));
        Assert.Equal(3 + 100 + 1, vm.Tree.Rows.Count);
        Assert.Equal("… 999,900 more", vm.Tree.Rows[^1].Name);
    }

    [AvaloniaFact]
    public async Task Collapsing_a_node_while_its_children_wait_to_be_inserted_keeps_them_out_and_re_expanding_shows_them_once()
    {
        // Regression (debug-ct-1): a full page (100 + "more" row) is inserted in a Background-priority chunk whose position used to be
        // computed before the yield; a collapse in between left the rows under the collapsed node and re-expanding inserted them again.
        var ui = new GatedUiDispatcher();
        using var rig = new PanelRig(ui: ui);
        rig.Session.Variables[2000] = Enumerable.Range(0, 1000).Select(i => new DebugVariable($"[{i}]", i.ToString(), "int")).ToList();
        rig.Session.Variables[1200][2] = new DebugVariable("arr", "int[1000]", "int[]", 2000, "arr", null, 1000);
        using var vm = await StoppedLocals(rig);
        var arr = vm.Tree.Rows.Single(r => r.Name == "arr");

        ui.HoldBackground = true;
        var expanding = vm.Tree.ExpandAsync(arr);
        await PanelRig.Eventually(() => ui.HeldCount == 1, what: "node creation queued");
        ui.ReleaseAll();
        await PanelRig.Eventually(() => ui.HeldCount == 1 && arr.ChildrenLoaded, what: "row insert queued");
        vm.Tree.Collapse(arr); // the user collapses after the answer arrived, before the rows went in
        ui.HoldBackground = false;
        ui.ReleaseAll();
        await expanding;
        await Task.Delay(50);
        Assert.Equal("x,s,arr", Names(vm.Tree));
        Assert.False(arr.IsExpanded);

        await vm.Tree.ExpandAsync(arr);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count >= 3 + 100 + 1, what: "re-expanded");
        await Task.Delay(50);
        Assert.Equal(3 + 100 + 1, vm.Tree.Rows.Count);
        Assert.Equal(100, vm.Tree.Rows.Count(r => r.Depth == 1 && r.Kind == VariableRowKind.Variable));
        Assert.Equal(VariableRowKind.More, vm.Tree.Rows[^1].Kind);
    }

    [AvaloniaFact]
    public async Task An_adapter_that_ignores_paging_cannot_make_the_tree_grow_beyond_its_cap()
    {
        using var rig = new PanelRig();
        rig.Session.IgnorePaging = true;
        rig.Session.Variables[2000] = Enumerable.Range(0, 60_000).Select(i => new DebugVariable($"[{i}]", i.ToString(), "int")).ToList();
        using var vm = await StoppedLocals(rig);
        await vm.Tree.ToggleAsync(vm.Tree.Rows.Single(r => r.Name == "o"));
        var children = vm.Tree.Rows.Count(r => r.Depth == 1 && r.Kind == VariableRowKind.Variable);
        Assert.Equal(VariablesTreeViewModel.MaxLoadedPerNode, children);
        Assert.Contains(vm.Tree.Rows, r => r.Kind == VariableRowKind.Message && r.Name.StartsWith("Showing the first 10,000", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Values_that_differ_from_the_previous_stop_are_highlighted_and_the_highlight_clears_on_the_next_stop()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        Assert.All(vm.Tree.Rows, r => Assert.False(r.IsChanged)); // first stop: nothing to compare with

        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0, what: "dropped on resume");
        rig.Session.Variables[1200][0] = new DebugVariable("x", "42", "int", EvaluateName: "x");
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 3, what: "second stop");
        Assert.True(vm.Tree.Rows.Single(r => r.Name == "x").IsChanged);
        Assert.False(vm.Tree.Rows.Single(r => r.Name == "s").IsChanged);

        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0);
        rig.StopAt(2); // x is still 42: no longer different from the previous stop
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 3);
        Assert.False(vm.Tree.Rows.Single(r => r.Name == "x").IsChanged);
    }

    [AvaloniaFact]
    public async Task Stepping_into_another_function_does_not_paint_unrelated_locals_red()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0);
        rig.Session.Variables[1200] = [new DebugVariable("x", "7", "int"), new DebugVariable("t", "9", "int")]; // a different frame shape
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 2);
        Assert.All(vm.Tree.Rows, r => Assert.False(r.IsChanged));
    }

    [AvaloniaFact]
    public async Task Resuming_drops_the_tree_cancels_in_flight_loads_and_never_applies_stale_results()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        var o = vm.Tree.Rows.Single(r => r.Name == "o");
        rig.Session.Latency = FakeLatency.Slow;
        rig.Session.Delay = TimeSpan.FromMilliseconds(300);
        var expanding = vm.Tree.ToggleAsync(o); // request in flight …
        await PanelRig.Eventually(() => rig.Session.Outstanding > 0);
        rig.Resume(); // … when the debuggee resumes
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0, what: "tree dropped");
        await expanding;
        await Task.Delay(400);
        Assert.Empty(vm.Tree.Rows); // the late answer was not applied
        Assert.Equal("Running…", vm.Tree.StatusText);
    }

    [AvaloniaFact]
    public async Task Expansion_is_remembered_by_name_and_restored_lazily_after_the_next_stop()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        await vm.Tree.ToggleAsync(vm.Tree.Rows.Single(r => r.Name == "o"));
        Assert.Equal(5, vm.Tree.Rows.Count);

        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0);
        var requestsBefore = rig.Session.VariableRequests.Count;
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 5, what: "o re-expanded");
        Assert.True(vm.Tree.Rows.Single(r => r.Name == "o").IsExpanded);
        Assert.Equal(2, rig.Session.VariableRequests.Count - requestsBefore); // the scope, then o's children
        Assert.All(rig.Session.VariableRequests.Skip(requestsBefore), r => Assert.True(r.Reference is 1200 or 2000));
    }

    [AvaloniaFact]
    public async Task Editing_a_value_calls_set_variable_and_updates_the_row()
    {
        using var rig = new PanelRig();
        rig.Session.SetVariableHandler = (_, name, value) => new DebugVariable(name, value, "int");
        using var vm = await StoppedLocals(rig);
        var x = vm.Tree.Rows.Single(r => r.Name == "x");
        Assert.True(vm.Tree.CanEditValue(x));
        vm.Tree.BeginEditValue(x);
        Assert.True(x.IsEditingValue);
        Assert.Equal("41", x.EditText);
        x.EditText = "99";
        await vm.Tree.CommitEditAsync(x);
        Assert.False(x.IsEditing);
        Assert.Equal("99", x.DisplayValue);
        Assert.True(x.IsChanged);
        Assert.Equal(1, rig.Session.SetVariableCalls);
    }

    [AvaloniaFact]
    public async Task A_failed_edit_stays_in_the_editor_and_shows_the_error_inline()
    {
        using var rig = new PanelRig();
        rig.Session.SetVariableHandler = (_, _, _) => throw new InvalidOperationException("Cannot convert 'abc' to int");
        using var vm = await StoppedLocals(rig);
        var x = vm.Tree.Rows.Single(r => r.Name == "x");
        vm.Tree.BeginEditValue(x);
        x.EditText = "abc";
        await vm.Tree.CommitEditAsync(x);
        Assert.True(x.IsEditing);
        Assert.Equal("Cannot convert 'abc' to int", x.EditError);
        Assert.Equal("41", x.DisplayValue); // unchanged
        vm.Tree.CancelEdit(x);
        Assert.False(x.IsEditing);
    }

    [AvaloniaFact]
    public async Task Values_cannot_be_edited_when_the_adapter_does_not_support_set_variable()
    {
        using var rig = new PanelRig();
        rig.Session.Capabilities = rig.Session.Capabilities with { SupportsSetVariable = false };
        using var vm = await StoppedLocals(rig);
        Assert.False(vm.Tree.CanEditValue(vm.Tree.Rows[0]));
    }

    [AvaloniaFact]
    public async Task Copy_value_copy_expression_and_add_to_watch()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedLocals(rig);
        await vm.Tree.ToggleAsync(vm.Tree.Rows.Single(r => r.Name == "o"));
        var count = vm.Tree.Rows.Single(r => r.Name == "Count");

        await vm.Tree.CopyValueAsync(count);
        await vm.Tree.CopyExpressionAsync(count);
        Assert.Equal(["3", "o.Count"], rig.Window.Clipboard); // evaluateName, not the bare member name

        vm.Tree.AddToWatch(count);
        Assert.Equal("o.Count", Assert.Single(rig.Debug.Watches).Expression);
    }

    [AvaloniaFact]
    public async Task Integers_can_be_shown_as_hexadecimal_client_side()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[1200][0] = new DebugVariable("x", "255", "int");
        rig.Session.Variables[1200][1] = new DebugVariable("neg", "-2", "int");
        using var vm = await StoppedLocals(rig);
        vm.Tree.NumberFormat = NumberFormat.Hexadecimal;
        Assert.Equal("0xFF", vm.Tree.Rows[0].DisplayValue);
        Assert.Equal("0xFFFFFFFE", vm.Tree.Rows[1].DisplayValue); // two's complement of the declared width
        Assert.Equal("255", vm.Tree.Rows[0].FullValue);
        vm.Tree.ViewAs(vm.Tree.Rows[0], NumberFormat.Decimal);
        Assert.Equal("255", vm.Tree.Rows[0].DisplayValue);
        Assert.Equal("{Sample.Item}", vm.Tree.Rows[2].DisplayValue); // not an integer: untouched
    }

    [Fact]
    public void Value_formatting_quotes_strings_truncates_long_values_and_keeps_rows_single_line()
    {
        Assert.Equal("\"hi\"", ValueFormat.Display("hi", "string", NumberFormat.Decimal));
        Assert.Equal("\"hi\"", ValueFormat.Display("\"hi\"", "string", NumberFormat.Decimal)); // not quoted twice
        Assert.Equal("null", ValueFormat.Display("null", "string", NumberFormat.Decimal));
        var longText = new string('a', 500);
        var shown = ValueFormat.Display("\"" + longText + "\"", "string", NumberFormat.Decimal);
        Assert.Equal(ValueFormat.MaxDisplayChars + 1, shown.Length);
        Assert.EndsWith("…", shown);
        Assert.Equal("line1⏎line2", ValueFormat.SingleLine("line1\r\nline2", 200));
        Assert.Equal("0xA", ValueFormat.ToHexadecimal("10", "uint"));
        Assert.Equal("10", ValueFormat.ToDecimal("0xA"));
        Assert.Equal(64, ValueFormat.WidthOf("System.Int64"));
        Assert.Equal(16, ValueFormat.WidthOf("short"));
    }

    [AvaloniaFact]
    public async Task Long_values_show_truncated_with_the_full_text_in_the_tooltip_and_copy()
    {
        using var rig = new PanelRig();
        var full = "\"" + new string('z', 500) + "\"";
        rig.Session.Variables[1200][1] = new DebugVariable("s", full, "string");
        using var vm = await StoppedLocals(rig);
        var s = vm.Tree.Rows.Single(r => r.Name == "s");
        Assert.Equal(201, s.DisplayValue.Length);
        Assert.Contains(full, s.Tooltip);
        await vm.Tree.CopyValueAsync(s);
        Assert.Equal(full, rig.Window.Clipboard.Single());
    }

    [AvaloniaFact]
    public async Task Autos_show_only_the_variables_used_on_the_current_and_previous_line_when_the_source_is_open()
    {
        using var rig = new PanelRig();
        var uri = new Uri("file:///work/Program.cs");
        var doc = new CsCode.Core.Text.InMemoryTextDocument(uri, "csharp", string.Join('\n', Enumerable.Range(0, 30).Select(i => i == 23 ? "var s = \"v\";" : i == 24 ? "Console.WriteLine(s + this.o);" : "// line " + i)));
        using var registration = rig.Documents.Register(doc);
        using var vm = new AutosViewModel(rig.Services);
        rig.StopAt(2); // Main at line 24 (0-based)
        await PanelRig.Eventually(() => vm.Tree.Rows.Count > 0, what: "autos");
        Assert.Equal("s,o", Names(vm.Tree)); // x is not mentioned on lines 23-24
    }

    [AvaloniaFact]
    public async Task Autos_highlight_what_the_last_step_changed_although_the_line_filter_shows_another_subset()
    {
        // Regression (debug-ct-5): the baseline used to be keyed by the names Autos happened to show, which change with the line. Stepping
        // from [x, y] to [x, z] found no baseline (x not red), and revisiting a line compared with an older stop (spurious red).
        using var rig = new PanelRig();
        var uri = new Uri("file:///work/Program.cs");
        var lines = Enumerable.Range(0, 30).Select(i => "// line " + i).ToArray();
        lines[9] = "var y = 5;";
        lines[10] = "x = x + 1;";
        lines[11] = "z = x * 2;";
        using var registration = rig.Documents.Register(new CsCode.Core.Text.InMemoryTextDocument(uri, "csharp", string.Join('\n', lines)));
        void Locals(int x, int y, int z) => rig.Session.Variables[1200] = [new DebugVariable("x", x.ToString(), "int"), new DebugVariable("y", y.ToString(), "int"), new DebugVariable("z", z.ToString(), "int")];
        void At(int line) => rig.Session.Frames[1][2] = rig.Session.Frames[1][2] with { Line = line };
        using var vm = new AutosViewModel(rig.Services);

        Locals(0, 5, 0);
        At(10);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Names(vm.Tree) == "x,y", what: "autos at line 10");

        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0);
        Locals(1, 6, 0); // the step changed x (shown) and y (not shown on the new lines)
        At(11);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Names(vm.Tree) == "x,z", what: "autos at line 11");
        Assert.True(vm.Tree.Rows.Single(r => r.Name == "x").IsChanged);
        Assert.False(vm.Tree.Rows.Single(r => r.Name == "z").IsChanged);

        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 0);
        Locals(2, 6, 0); // back at line 10 (a loop): y did not change since the PREVIOUS stop
        At(10);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Names(vm.Tree) == "x,y", what: "autos at line 10 again");
        Assert.True(vm.Tree.Rows.Single(r => r.Name == "x").IsChanged);
        Assert.False(vm.Tree.Rows.Single(r => r.Name == "y").IsChanged);
    }

    [AvaloniaFact]
    public async Task Autos_without_source_show_the_locals_minus_this_and_statics()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[1200] = [new DebugVariable("this", "{Program}", "Program", 2000), new DebugVariable("x", "1", "int"), new DebugVariable("Static members", "", null, 2000)];
        using var vm = new AutosViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count > 0, what: "autos");
        Assert.Equal("x", Names(vm.Tree));
    }

    [AvaloniaFact]
    public async Task Panels_show_a_status_when_not_debugging_or_running()
    {
        using var rig = new PanelRig(withSession: false);
        using var vm = new LocalsViewModel(rig.Services);
        await PanelRig.Eventually(() => vm.Tree.StatusText == "Not debugging");
        rig.Debug.AddSession(rig.Session);
        await PanelRig.Eventually(() => vm.Tree.StatusText == "Running…");
        Assert.True(vm.Tree.ShowStatus);
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count > 0);
        Assert.False(vm.Tree.ShowStatus);
    }

    [AvaloniaFact]
    public async Task Multiple_scopes_become_expandable_groups()
    {
        using var rig = new PanelRig();
        rig.Session.Scopes[102] = [new DebugScope("Locals", 1200, false), new DebugScope("Arguments", 1300, false, PresentationHint: "arguments"), new DebugScope("Globals", 1400, true)];
        rig.Session.Variables[1300] = [new DebugVariable("args", "string[0]", "string[]")];
        using var vm = await StoppedLocals(rig);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 2 + 3 + 1, what: "both groups expanded"); // Locals(3) + Arguments(1); the expensive Globals scope is never loaded
        Assert.Equal("Locals,x,s,o,Arguments,args", Names(vm.Tree));
        Assert.Equal(VariableRowKind.Scope, vm.Tree.Rows[0].Kind);
    }
}
