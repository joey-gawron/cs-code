using CsCode.Core.Debugging;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

public class BreakpointMirrorTests
{
    private static readonly Uri A = new("file:///work/a.cs"), B = new("file:///work/b.cs");

    private static DapSetBreakpointsArguments Last(FakeDebugAdapter a, string path) =>
        a.RequestsOf("setBreakpoints").Select(r => r.Args<DapSetBreakpointsArguments>()).Last(x => x.Source.Path == path);

    private static async Task Settled(FakeDebugAdapter a, string command, int count)
    {
        await a.WaitForRequestAsync(command, count);
        await Task.Delay(150, Ct); // let a (wrongly) extra request show up
    }

    [Fact]
    public async Task Initial_sync_sends_every_file_with_1_based_lines_and_all_options()
    {
        var h = new SessionHarness();
        h.Breakpoints.Add(A, 4, "x > 1", "3", null);
        h.Breakpoints.Add(A, 9, null, null, "hit {x}");
        h.Breakpoints.Add(B, 0);
        h.Create(Cfg.Make());
        await using var _ = h;
        await h.Session.StartAsync(Ct);

        var a = Last(h.Adapter, "/work/a.cs");
        Assert.Equal([5, 10], a.Breakpoints!.Select(b => b.Line));
        Assert.Equal(("x > 1", "3"), (a.Breakpoints![0].Condition, a.Breakpoints![0].HitCondition));
        Assert.Equal("hit {x}", a.Breakpoints![1].LogMessage);
        Assert.Equal("a.cs", a.Source.Name);
        Assert.Equal([1], Last(h.Adapter, "/work/b.cs").Breakpoints!.Select(b => b.Line));
        Assert.Equal(2, h.Adapter.CountOf("setBreakpoints"));
    }

    [Fact]
    public async Task A_0_based_session_sends_lines_unchanged()
    {
        var h = new SessionHarness();
        h.Breakpoints.Add(A, 4);
        h.Create(Cfg.Make(), new DebugSessionSettings { LinesStartAt1 = false });
        await using var _ = h;
        await h.Session.StartAsync(Ct);
        Assert.Equal([4], Last(h.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line));
    }

    [Fact]
    public async Task Breakpoints_are_sent_after_initialized_and_before_configurationDone()
    {
        var h = new SessionHarness();
        h.Breakpoints.Add(A, 1);
        h.Create(Cfg.Make());
        await using var _ = h;
        await h.Session.StartAsync(Ct);
        var cmds = h.Adapter.Commands.ToList();
        Assert.True(cmds.IndexOf("launch") < cmds.IndexOf("setBreakpoints"));
        Assert.True(cmds.IndexOf("setBreakpoints") < cmds.IndexOf("configurationDone"));
    }

    [Fact]
    public async Task Changing_a_file_resends_the_complete_list_for_that_file_only()
    {
        await using var h = await SessionHarness.StartAsync(beforeStart: x => { x.Breakpoints.Add(A, 1); x.Breakpoints.Add(B, 1); });
        Assert.Equal(2, h.Adapter.CountOf("setBreakpoints"));
        h.Breakpoints.Add(A, 7);
        await Settled(h.Adapter, "setBreakpoints", 3);
        Assert.Equal(3, h.Adapter.CountOf("setBreakpoints"));
        Assert.Equal([2, 8], Last(h.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line)); // replace-all: both lines
    }

    [Fact]
    public async Task Removing_the_last_breakpoint_of_a_file_sends_an_empty_list_to_clear_it()
    {
        await using var h = await SessionHarness.StartAsync(beforeStart: x => x.Breakpoints.Toggle(A, 3));
        h.Breakpoints.Toggle(A, 3);
        await Settled(h.Adapter, "setBreakpoints", 2);
        Assert.Empty(Last(h.Adapter, "/work/a.cs").Breakpoints!);
    }

    [Fact]
    public async Task Rapid_changes_are_debounced_into_one_request_with_the_final_state()
    {
        await using var h = await SessionHarness.StartAsync();
        for (var i = 0; i < 30; i++) h.Breakpoints.Toggle(A, i);
        for (var i = 0; i < 30; i += 2) h.Breakpoints.Toggle(A, i);
        await Settled(h.Adapter, "setBreakpoints", 1);
        Assert.InRange(h.Adapter.CountOf("setBreakpoints"), 1, 2);
        Assert.Equal(Enumerable.Range(0, 30).Where(i => i % 2 == 1).Select(i => i + 1), Last(h.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line));
    }

    [Fact]
    public async Task Toggling_on_and_off_inside_the_debounce_window_sends_nothing_for_a_file_never_sent()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Breakpoints.Toggle(A, 3);
        h.Breakpoints.Toggle(A, 3);
        await Task.Delay(300, Ct);
        Assert.Equal(0, h.Adapter.CountOf("setBreakpoints"));
    }

    [Fact]
    public async Task Disabled_breakpoints_are_not_sent()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Breakpoints.Add(A, 1);
        h.Breakpoints.Add(A, 2);
        await Settled(h.Adapter, "setBreakpoints", 1);
        h.Breakpoints.SetEnabledAll(false);
        await Settled(h.Adapter, "setBreakpoints", 2);
        Assert.Empty(Last(h.Adapter, "/work/a.cs").Breakpoints!);
        h.Breakpoints.SetEnabledAll(true);
        await Settled(h.Adapter, "setBreakpoints", 3);
        Assert.Equal(2, Last(h.Adapter, "/work/a.cs").Breakpoints!.Count);
    }

    [Fact]
    public async Task Unsupported_kinds_are_not_sent_and_reported_unverified_with_a_reason()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Capabilities["supportsConditionalBreakpoints"] = false;
            a.Capabilities["supportsHitConditionalBreakpoints"] = false;
            a.Capabilities["supportsLogPoints"] = false;
        });
        var cond = h.Breakpoints.Add(A, 1, "x > 1");
        var hit = h.Breakpoints.Add(A, 2, null, "3");
        var log = h.Breakpoints.Add(A, 3, null, null, "msg");
        var plain = h.Breakpoints.Add(A, 4);
        await Settled(h.Adapter, "setBreakpoints", 1);
        Assert.Equal([5], Last(h.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line)); // only the plain one
        Assert.False(h.Breakpoints.GetStatus(cond.Id)!.Verified);
        Assert.Contains("conditional", h.Breakpoints.GetStatus(cond.Id)!.Message);
        Assert.Contains("hit-count", h.Breakpoints.GetStatus(hit.Id)!.Message);
        Assert.Contains("logpoints", h.Breakpoints.GetStatus(log.Id)!.Message);
        Assert.True(h.Breakpoints.GetStatus(plain.Id)!.Verified);
    }

    [Fact]
    public async Task Adapter_verdicts_are_mapped_back_by_position_with_moved_lines_converted_to_0_based()
    {
        await using var h = await SessionHarness.StartAsync(a => a.On("setBreakpoints", r => new DapBreakpointsResponse
        {
            Breakpoints =
            [
                new DapBreakpoint { Id = 11, Verified = true, Line = r.Args<DapSetBreakpointsArguments>().Breakpoints![0].Line + 2 }, // adapter moved it down 2
                new DapBreakpoint { Id = 12, Verified = false, Message = "no executable code on this line" },
            ],
        }));
        var one = h.Breakpoints.Add(A, 3);
        var two = h.Breakpoints.Add(A, 20);
        await Settled(h.Adapter, "setBreakpoints", 1);
        var s1 = h.Breakpoints.GetStatus(one.Id)!;
        Assert.True(s1.Verified);
        Assert.Equal(5, s1.ActualLine); // wire 4+2=6 → 0-based 5
        var s2 = h.Breakpoints.GetStatus(two.Id)!;
        Assert.False(s2.Verified);
        Assert.Equal("no executable code on this line", s2.Message);
        Assert.Contains(h.Snapshot(h.StatusEvents), e => e.BreakpointId == one.Id);
    }

    [Fact]
    public async Task A_short_response_marks_the_missing_breakpoints_unverified()
    {
        await using var h = await SessionHarness.StartAsync(a => a.On("setBreakpoints", _ => new DapBreakpointsResponse { Breakpoints = [new DapBreakpoint { Verified = true }] }));
        var one = h.Breakpoints.Add(A, 1);
        var two = h.Breakpoints.Add(A, 2);
        await Settled(h.Adapter, "setBreakpoints", 1);
        Assert.True(h.Breakpoints.GetStatus(one.Id)!.Verified);
        Assert.False(h.Breakpoints.GetStatus(two.Id)!.Verified);
    }

    [Fact]
    public async Task Breakpoint_events_update_the_status_of_the_mapped_breakpoint()
    {
        await using var h = await SessionHarness.StartAsync(a => a.On("setBreakpoints", _ => new DapBreakpointsResponse { Breakpoints = [new DapBreakpoint { Id = 77, Verified = false, Message = "pending" }] }));
        var bp = h.Breakpoints.Add(A, 4);
        await Settled(h.Adapter, "setBreakpoints", 1);
        Assert.False(h.Breakpoints.GetStatus(bp.Id)!.Verified);
        await h.Adapter.SendEventAsync("breakpoint", new DapBreakpointEvent { Reason = "changed", Breakpoint = new DapBreakpoint { Id = 77, Verified = true, Line = 6 } });
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(bp.Id)!.Verified);
        Assert.Equal(5, h.Breakpoints.GetStatus(bp.Id)!.ActualLine);
    }

    [Fact]
    public async Task Breakpoint_events_for_unknown_adapter_ids_are_ignored()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendEventAsync("breakpoint", new DapBreakpointEvent { Reason = "new", Breakpoint = new DapBreakpoint { Id = 9999, Verified = true } });
        await Task.Delay(100, Ct);
        Assert.Equal(0, h.Breakpoints.StatusCount);
    }

    [Fact]
    public async Task Status_updates_do_not_cause_re_sends()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Breakpoints.Add(A, 1);
        await Settled(h.Adapter, "setBreakpoints", 1);
        await Task.Delay(400, Ct);
        Assert.Equal(1, h.Adapter.CountOf("setBreakpoints")); // the verdict re-fired Changed but the content was identical
    }

    [Fact]
    public async Task Function_breakpoints_are_sent_only_to_adapters_that_support_them()
    {
        await using var yes = await SessionHarness.StartAsync();
        yes.Breakpoints.AddFunction("Program.Main", "i == 1");
        await Settled(yes.Adapter, "setFunctionBreakpoints", 1);
        var args = yes.Adapter.RequestsOf("setFunctionBreakpoints")[0].Args<DapSetFunctionBreakpointsArguments>();
        Assert.Equal(("Program.Main", "i == 1"), (args.Breakpoints[0].Name, args.Breakpoints[0].Condition));

        await using var no = await SessionHarness.StartAsync(a => a.Capabilities["supportsFunctionBreakpoints"] = false);
        no.Breakpoints.AddFunction("Program.Main");
        await Task.Delay(300, Ct);
        Assert.Equal(0, no.Adapter.CountOf("setFunctionBreakpoints"));
    }

    [Fact]
    public async Task Exception_filter_changes_resend_setExceptionBreakpoints_with_only_known_enabled_filters()
    {
        await using var h = await SessionHarness.StartAsync();
        Assert.Equal(1, h.Adapter.CountOf("setExceptionBreakpoints"));
        h.Breakpoints.SetExceptionFilters(
        [
            new ExceptionFilterSetting("all", "All", true),
            new ExceptionFilterSetting("user-unhandled", "User", true),
            new ExceptionFilterSetting("from-another-adapter", "Foreign", true),
        ]);
        await Settled(h.Adapter, "setExceptionBreakpoints", 2);
        Assert.Equal(["all", "user-unhandled"], h.Adapter.RequestsOf("setExceptionBreakpoints")[1].Args<DapSetExceptionBreakpointsArguments>().Filters);
    }

    [Fact]
    public async Task Conditional_exception_filters_use_filterOptions_when_the_adapter_supports_them()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Capabilities["supportsExceptionFilterOptions"] = true;
            a.Capabilities["exceptionBreakpointFilters"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["filter"] = "thrown", ["label"] = "Thrown", ["supportsCondition"] = true });
        });
        h.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("thrown", "Thrown", true, "ex is IOException")]);
        await Settled(h.Adapter, "setExceptionBreakpoints", 2);
        var last = h.Adapter.RequestsOf("setExceptionBreakpoints")[^1].Args<DapSetExceptionBreakpointsArguments>();
        Assert.Empty(last.Filters);
        Assert.Equal("ex is IOException", last.FilterOptions![0].Condition);
    }

    [Fact]
    public async Task Non_file_uris_are_not_sent_to_the_adapter()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Breakpoints.Toggle(new Uri("debug:/s1/55/gen.cs"), 1);
        await Task.Delay(300, Ct);
        Assert.Equal(0, h.Adapter.CountOf("setBreakpoints"));
    }

    [Fact]
    public async Task Breakpoints_can_be_changed_while_the_debuggee_is_stopped()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync();
        h.Breakpoints.Toggle(A, 8);
        await Settled(h.Adapter, "setBreakpoints", 1);
        Assert.Equal([9], Last(h.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line));
    }

    [Fact]
    public async Task After_the_session_ends_manager_changes_are_no_longer_mirrored()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Session.StopAsync(Ct);
        var n = h.Adapter.CountOf("setBreakpoints");
        h.Breakpoints.Toggle(A, 1);
        await Task.Delay(300, Ct);
        Assert.Equal(n, h.Adapter.CountOf("setBreakpoints"));
    }

    [Fact]
    public async Task Two_live_sessions_both_mirror_the_shared_manager_and_keep_separate_verdicts()
    {
        var lane = new CsCode.Dap.Client.DebugLane("two-sessions");
        var shared = new CsCode.Dap.Breakpoints.BreakpointManager();
        await using var h1 = await SessionHarness.StartAsync(lane: lane, breakpoints: shared, id: "s1");
        await using var h2 = await SessionHarness.StartAsync(lane: lane, breakpoints: shared, id: "s2",
            configure: a => a.On("setBreakpoints", _ => new DapBreakpointsResponse { Breakpoints = [new DapBreakpoint { Verified = false, Message = "s2 says no" }] }));
        var bp = shared.Add(A, 5);
        await Settled(h1.Adapter, "setBreakpoints", 1);
        await Settled(h2.Adapter, "setBreakpoints", 1);
        Assert.Equal([6], Last(h1.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line));
        Assert.Equal([6], Last(h2.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line));
        Assert.Equal(2, shared.StatusCount);
        await h2.Session.StopAsync(Ct);
        Assert.Equal(1, shared.StatusCount); // only s2's verdict is gone
        Assert.True(shared.GetStatus(bp.Id)!.Verified); // s1's remains
        await h1.DisposeAsync();
        await h2.DisposeAsync();
        await shared.DisposeAsync();
        lane.Dispose();
    }

    [Fact]
    public async Task A_late_joining_session_gets_the_current_breakpoints_in_its_initial_sync()
    {
        var shared = new CsCode.Dap.Breakpoints.BreakpointManager();
        shared.Add(A, 1);
        shared.Add(A, 2);
        var lane = new CsCode.Dap.Client.DebugLane("late");
        await using var h = await SessionHarness.StartAsync(lane: lane, breakpoints: shared);
        Assert.Equal([2, 3], Last(h.Adapter, "/work/a.cs").Breakpoints!.Select(b => b.Line));
        await h.DisposeAsync();
        await shared.DisposeAsync();
        lane.Dispose();
    }
}
