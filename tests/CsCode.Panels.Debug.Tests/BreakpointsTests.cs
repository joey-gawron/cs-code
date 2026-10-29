using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.Tests.Fakes;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class BreakpointsTests
{
    private static readonly Uri ProgramUri = new("file:///work/Program.cs");
    private static readonly Uri OtherUri = new("file:///work/Util.cs");

    private static async Task<BreakpointsViewModel> Open(PanelRig rig)
    {
        var vm = new BreakpointsViewModel(rig.Services);
        await vm.RefreshAsync();
        return vm;
    }

    private static BreakpointRow Row(BreakpointsViewModel vm, string label) => vm.Rows.Single(r => r.Label == label);

    [AvaloniaFact]
    public async Task Source_breakpoints_are_listed_by_file_and_line_with_a_header()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.Add(OtherUri, 4);
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 11);
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 3);
        using var vm = await Open(rig);
        Assert.Equal(["Breakpoints (3)", "Program.cs : 4", "Program.cs : 12", "Util.cs : 5"], vm.Rows.Select(r => r.Label));
        Assert.True(vm.Rows[0].IsHeader);
        Assert.Equal("/work", vm.Rows[1].Detail);
        Assert.All(vm.Rows.Skip(1), r => Assert.True(r.IsEnabled));
    }

    [AvaloniaFact]
    public async Task Condition_hit_count_and_log_message_show_as_badges_with_matching_glyphs()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 1, condition: "x > 3");
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 2, hitCondition: ">= 5");
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 3, logMessage: "x is {x}");
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 4);
        using var vm = await Open(rig);
        var cond = Row(vm, "Program.cs : 2");
        Assert.True(cond.HasCondition);
        Assert.Equal("Condition: x > 3", cond.ConditionBadge);
        Assert.Equal("debug-breakpoint-conditional", cond.GlyphKind);
        var hit = Row(vm, "Program.cs : 3");
        Assert.True(hit.HasHitCondition);
        Assert.Equal("Hit count: >= 5", hit.HitBadge);
        var log = Row(vm, "Program.cs : 4");
        Assert.True(log.HasLogMessage);
        Assert.Equal("debug-breakpoint-log", log.GlyphKind);
        Assert.Equal("circle-large-filled", Row(vm, "Program.cs : 5").GlyphKind);
        Assert.Contains("Condition: x > 3", cond.Tooltip);
    }

    [AvaloniaFact]
    public async Task Adapter_verdicts_show_as_verified_unverified_moved_and_failed()
    {
        using var rig = new PanelRig();
        var m = rig.Debug.FakeBreakpoints;
        var verified = m.Add(ProgramUri, 1);
        var unverified = m.Add(ProgramUri, 2);
        var moved = m.Add(ProgramUri, 10);
        var failed = m.Add(ProgramUri, 20);
        var silent = m.Add(ProgramUri, 30);
        m.ReportStatus(new DebugBreakpointStatus(rig.Session, verified.Id, true, 1, null));
        m.ReportStatus(new DebugBreakpointStatus(rig.Session, unverified.Id, false, null, null));
        m.ReportStatus(new DebugBreakpointStatus(rig.Session, moved.Id, true, 12, null));
        m.ReportStatus(new DebugBreakpointStatus(rig.Session, failed.Id, false, null, "The source file could not be found"));
        using var vm = await Open(rig);

        Assert.Equal(BreakpointVerification.Verified, Row(vm, "Program.cs : 2").Verification);
        var u = Row(vm, "Program.cs : 3");
        Assert.Equal(BreakpointVerification.Unverified, u.Verification);
        Assert.Equal("circle", u.GlyphKind); // hollow
        var mv = Row(vm, "Program.cs : 11");
        Assert.Equal(BreakpointVerification.Moved, mv.Verification);
        Assert.Equal(13, mv.MovedToLine);
        Assert.Equal("moved to line 13", mv.Note);
        var f = Row(vm, "Program.cs : 21");
        Assert.Equal(BreakpointVerification.Failed, f.Verification);
        Assert.Equal("The source file could not be found", f.Note);
        Assert.Equal("debug-breakpoint-unsupported", f.GlyphKind);
        Assert.Equal(BreakpointVerification.Unknown, Row(vm, "Program.cs : 31").Verification);
        _ = silent;
    }

    [AvaloniaFact]
    public async Task A_status_arriving_later_updates_the_row_without_the_view_asking()
    {
        using var rig = new PanelRig();
        var bp = rig.Debug.FakeBreakpoints.Add(ProgramUri, 5);
        using var vm = await Open(rig);
        Assert.Equal(BreakpointVerification.Unknown, Row(vm, "Program.cs : 6").Verification);
        rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(rig.Session, bp.Id, true, 5, null));
        await PanelRig.Eventually(() => Row(vm, "Program.cs : 6").Verification == BreakpointVerification.Verified, what: "status applied");
    }

    [AvaloniaFact]
    public async Task The_checkbox_enables_and_disables_through_the_breakpoint_manager()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 1);
        using var vm = await Open(rig);
        var row = Row(vm, "Program.cs : 2");
        row.IsEnabled = false;
        Assert.False(rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().Enabled);
        await PanelRig.Eventually(() => !Row(vm, "Program.cs : 2").IsEnabled);
        Assert.Equal("circle", Row(vm, "Program.cs : 2").GlyphKind); // hollow when disabled
        vm.ToggleEnabled(Row(vm, "Program.cs : 2"));
        Assert.True(rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().Enabled);
    }

    [AvaloniaFact]
    public async Task Delete_delete_all_and_enable_disable_all_go_to_the_manager()
    {
        using var rig = new PanelRig(withSession: false);
        var m = rig.Debug.FakeBreakpoints;
        m.Add(ProgramUri, 1);
        m.Add(ProgramUri, 2);
        m.AddFunction("Sample.Program.Main");
        using var vm = await Open(rig);

        vm.DisableAllCommand.Execute(null);
        Assert.All(m.SourceBreakpoints, b => Assert.False(b.Enabled));
        vm.EnableAllCommand.Execute(null);
        Assert.All(m.SourceBreakpoints, b => Assert.True(b.Enabled));

        vm.SelectedRow = Row(vm, "Program.cs : 2");
        vm.DeleteSelectedCommand.Execute(null);
        await PanelRig.Eventually(() => m.SourceBreakpoints.Count == 1);
        vm.Delete(Row(vm, "Sample.Program.Main"));
        Assert.Empty(m.FunctionBreakpoints);

        vm.DeleteAllCommand.Execute(null);
        await PanelRig.Eventually(() => vm.IsEmpty, what: "everything deleted");
        Assert.Empty(m.SourceBreakpoints);
    }

    [AvaloniaFact]
    public async Task The_condition_is_edited_through_an_input_box_and_empty_text_clears_it()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 1, condition: "x > 1");
        using var vm = await Open(rig);

        rig.Window.InputResponses.Enqueue("x > 3");
        await vm.EditConditionAsync(Row(vm, "Program.cs : 2"));
        Assert.Equal("x > 1", rig.Window.InputPrompts[0]!.Value); // prefilled with the current condition
        Assert.Equal("x > 3", rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().Condition);

        await PanelRig.Eventually(() => Row(vm, "Program.cs : 2").Condition == "x > 3");
        rig.Window.InputResponses.Enqueue(null); // dismissed
        await vm.EditConditionAsync(Row(vm, "Program.cs : 2"));
        Assert.Equal("x > 3", rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().Condition);

        rig.Window.InputResponses.Enqueue("  ");
        await vm.EditConditionAsync(Row(vm, "Program.cs : 2"));
        Assert.Null(rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().Condition);
    }

    [AvaloniaFact]
    public async Task Hit_count_and_log_message_can_be_edited_too()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.Add(ProgramUri, 1);
        using var vm = await Open(rig);
        rig.Window.InputResponses.Enqueue("%3");
        await vm.EditHitCountAsync(Row(vm, "Program.cs : 2"));
        Assert.Equal("%3", rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().HitCondition);
        await PanelRig.Eventually(() => Row(vm, "Program.cs : 2").HitCondition == "%3");
        rig.Window.InputResponses.Enqueue("hit {x}");
        await vm.EditLogMessageAsync(Row(vm, "Program.cs : 2"));
        Assert.Equal("hit {x}", rig.Debug.FakeBreakpoints.SourceBreakpoints.Single().LogMessage);
    }

    [AvaloniaFact]
    public async Task Exception_settings_list_the_adapters_filters_and_toggle_them()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.SetExceptionFilters([new ExceptionFilterSetting("all", "Thrown Exceptions", false), new ExceptionFilterSetting("user-unhandled", "User-Unhandled Exceptions", true)]);
        using var vm = await Open(rig);
        Assert.Equal(["Exception Settings", "Thrown Exceptions", "User-Unhandled Exceptions"], vm.Rows.Select(r => r.Label));
        Assert.All(vm.Rows.Skip(1), r => Assert.Equal(BreakpointRowKind.Exception, r.Kind));

        Row(vm, "Thrown Exceptions").IsEnabled = true;
        Assert.Equal([true, true], rig.Debug.FakeBreakpoints.ExceptionFilters.Select(f => f.Enabled));
        await PanelRig.Eventually(() => Row(vm, "Thrown Exceptions").IsEnabled);

        rig.Window.InputResponses.Enqueue("ex is ArgumentException");
        await vm.EditConditionAsync(Row(vm, "Thrown Exceptions"));
        Assert.Equal("ex is ArgumentException", rig.Debug.FakeBreakpoints.ExceptionFilters[0].Condition);
    }

    [AvaloniaFact]
    public async Task Function_breakpoints_are_listed_and_their_checkbox_is_read_only()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.FakeBreakpoints.AddFunction("Sample.Program.Main", "n > 0");
        using var vm = await Open(rig);
        var row = Row(vm, "Sample.Program.Main");
        Assert.Equal(BreakpointRowKind.Function, row.Kind);
        Assert.False(row.CanToggle);
        Assert.Equal("Condition: n > 0", row.ConditionBadge);
        row.IsEnabled = false; // the manager has no way to disable a function breakpoint: the row refuses
        Assert.True(row.IsEnabled);
        Assert.Equal("debug-breakpoint-function", row.GlyphKind);
    }

    [AvaloniaFact]
    public async Task Double_click_navigates_to_the_line_or_the_line_the_adapter_moved_it_to()
    {
        using var rig = new PanelRig();
        var bp = rig.Debug.FakeBreakpoints.Add(ProgramUri, 9);
        using var vm = await Open(rig);
        await vm.NavigateAsync(Row(vm, "Program.cs : 10"));
        Assert.Equal((ProgramUri, (TextRange?)TextRange.At(9, 0)), rig.Navigation.Opened.Single());

        rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(rig.Session, bp.Id, true, 11, null));
        await PanelRig.Eventually(() => Row(vm, "Program.cs : 10").MovedToLine == 12);
        await vm.NavigateAsync(Row(vm, "Program.cs : 10"));
        Assert.Equal(TextRange.At(11, 0), rig.Navigation.Opened.Last().Selection);
    }

    [AvaloniaFact]
    public async Task Add_function_breakpoint_asks_for_a_name()
    {
        using var rig = new PanelRig(withSession: false);
        using var vm = await Open(rig);
        rig.Window.InputResponses.Enqueue("  Sample.Util.Parse ");
        await vm.AddFunctionCommand.ExecuteAsync();
        Assert.Equal("Sample.Util.Parse", Assert.Single(rig.Debug.FakeBreakpoints.FunctionBreakpoints).Name);
    }

    [AvaloniaFact]
    public async Task A_burst_of_changes_collapses_into_a_few_refreshes_and_selection_survives()
    {
        using var rig = new PanelRig(withSession: false);
        var first = rig.Debug.FakeBreakpoints.Add(ProgramUri, 0);
        using var vm = await Open(rig);
        vm.SelectedRow = Row(vm, "Program.cs : 1");
        var resets = 0;
        vm.Rows.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };

        for (var i = 1; i <= 300; i++) rig.Debug.FakeBreakpoints.Add(ProgramUri, i);
        await PanelRig.Eventually(() => vm.Rows.Count == 302, what: "all rows");
        Assert.True(resets <= 20, $"{resets} refreshes for 300 changes");
        Assert.Equal(first.Id, vm.SelectedRow?.Id);
    }

    [AvaloniaFact]
    public async Task A_thousand_breakpoints_are_built_off_the_ui_thread_and_listed()
    {
        using var rig = new PanelRig(withSession: false);
        for (var i = 0; i < 1000; i++) rig.Debug.FakeBreakpoints.Add(new Uri($"file:///work/f{i % 10}.cs"), i);
        using var vm = await Open(rig);
        Assert.Equal(1001, vm.Rows.Count);
        Assert.Equal("Breakpoints (1000)", vm.Rows[0].Label);
    }

    [AvaloniaFact]
    public async Task Disposing_releases_every_subscription()
    {
        using var rig = new PanelRig();
        var vm = await Open(rig);
        Assert.True(rig.Debug.FakeBreakpoints.SubscriberCount > 0);
        vm.Dispose();
        Assert.Equal(0, rig.Debug.FakeBreakpoints.SubscriberCount);
        Assert.Equal(0, rig.Debug.SessionStartedEmitter.SubscriberCount);
        Assert.Equal(0, rig.Debug.SessionTerminatedEmitter.SubscriberCount);
        Assert.Empty(vm.Rows);
    }
}
