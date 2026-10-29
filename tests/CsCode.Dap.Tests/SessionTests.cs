using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

public class SessionHandshakeTests
{
    private static readonly Uri FileA = new("file:///work/a.cs");

    [Fact]
    public async Task Handshake_order_is_initialize_launch_breakpoints_configurationDone()
    {
        var h = new SessionHarness();
        h.Breakpoints.Toggle(FileA, 4);
        h.Create(Cfg.Make());
        await using var _ = h;
        await h.Session.StartAsync(Ct);
        Assert.Equal(["initialize", "launch", "setBreakpoints", "setExceptionBreakpoints", "configurationDone"], h.Adapter.Commands);
        Assert.Equal(DebugSessionState.Running, h.Session.State);
    }

    [Fact]
    public async Task Without_breakpoints_only_initialize_launch_filters_and_configurationDone_are_sent()
    {
        await using var h = await SessionHarness.StartAsync();
        Assert.Equal(["initialize", "launch", "setExceptionBreakpoints", "configurationDone"], h.Adapter.Commands);
    }

    [Fact]
    public async Task State_goes_Initializing_to_Running_with_one_StateChanged()
    {
        await using var h = await SessionHarness.StartAsync();
        Assert.Equal([DebugSessionState.Running], h.Snapshot(h.States));
    }

    [Fact]
    public async Task Initialized_event_after_launch_request_is_handled()
    {
        await using var h = await SessionHarness.StartAsync(a => a.InitializedAfterInitialize = false);
        Assert.Equal(DebugSessionState.Running, h.Session.State);
        Assert.Equal("configurationDone", h.Adapter.Commands[^1]);
    }

    [Fact]
    public async Task Adapters_that_answer_launch_only_after_configurationDone_work()
    {
        await using var h = await SessionHarness.StartAsync(a => a.RespondToLaunchAfterConfigurationDone = true);
        Assert.Equal(DebugSessionState.Running, h.Session.State);
        Assert.Contains("configurationDone", h.Adapter.Commands);
    }

    [Fact]
    public async Task Adapter_without_configurationDone_support_skips_it()
    {
        await using var h = await SessionHarness.StartAsync(a => a.Capabilities["supportsConfigurationDoneRequest"] = false);
        Assert.DoesNotContain("configurationDone", h.Adapter.Commands);
        Assert.Equal(DebugSessionState.Running, h.Session.State);
    }

    [Fact]
    public async Task Launch_arguments_are_the_whole_configuration_untouched()
    {
        var cfg = Cfg.Make(extra: new Dictionary<string, object?>
        {
            ["program"] = "/work/bin/app.dll",
            ["args"] = new[] { "--x", "1" },
            ["justMyCode"] = false,
            ["env"] = new Dictionary<string, string> { ["A"] = "b" },
            ["adapterSpecific"] = new { deep = new { list = new[] { 1, 2, 3 } } },
        });
        await using var h = await SessionHarness.StartAsync(config: cfg);
        var args = h.Adapter.RequestsOf("launch")[0].Arguments;
        Assert.Equal("/work/bin/app.dll", args.GetProperty("program").GetString());
        Assert.Equal(["--x", "1"], args.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        Assert.False(args.GetProperty("justMyCode").GetBoolean());
        Assert.Equal("b", args.GetProperty("env").GetProperty("A").GetString());
        Assert.Equal(3, args.GetProperty("adapterSpecific").GetProperty("deep").GetProperty("list").GetArrayLength());
        Assert.Equal("fake", args.GetProperty("type").GetString());
        Assert.False(args.TryGetProperty("noDebug", out _));
    }

    [Fact]
    public async Task NoDebug_is_passed_to_the_adapter()
    {
        await using var h = await SessionHarness.StartAsync(config: Cfg.Make(noDebug: true));
        Assert.True(h.Adapter.RequestsOf("launch")[0].Arguments.GetProperty("noDebug").GetBoolean());
    }

    [Fact]
    public async Task Attach_configuration_sends_attach_not_launch()
    {
        await using var h = await SessionHarness.StartAsync(config: Cfg.Make("attach", extra: new Dictionary<string, object?> { ["processId"] = 1234 }));
        Assert.Equal(1, h.Adapter.CountOf("attach"));
        Assert.Equal(0, h.Adapter.CountOf("launch"));
        Assert.Equal(1234, h.Adapter.RequestsOf("attach")[0].Arguments.GetProperty("processId").GetInt32());
        Assert.True(h.Session.IsAttach);
    }

    [Fact]
    public async Task Initialize_advertises_client_capabilities_honestly()
    {
        await using var h = await SessionHarness.StartAsync();
        var init = h.Adapter.RequestsOf("initialize")[0].Args<DapInitializeArguments>();
        Assert.Equal("fake", init.AdapterId);
        Assert.Equal("cscode", init.ClientId);
        Assert.True(init.LinesStartAt1);
        Assert.True(init.ColumnsStartAt1);
        Assert.Equal("path", init.PathFormat);
        Assert.True(init.SupportsVariableType);
        Assert.True(init.SupportsVariablePaging);
        Assert.True(init.SupportsRunInTerminalRequest);
        Assert.True(init.SupportsMemoryReferences);
        Assert.True(init.SupportsProgressReporting);
        Assert.True(init.SupportsInvalidatedEvent);
        Assert.False(init.SupportsStartDebuggingRequest); // no child-session starter wired → must not claim it
        Assert.False(init.SupportsMemoryEvent);
    }

    [Fact]
    public async Task StartDebugging_is_advertised_only_when_a_starter_exists()
    {
        await using var h = await SessionHarness.StartAsync(startChild: (_, _, _, _) => Task.FromResult<IDebugSession?>(null));
        Assert.True(h.Adapter.RequestsOf("initialize")[0].Args<DapInitializeArguments>().SupportsStartDebuggingRequest);
    }

    [Fact]
    public async Task Capabilities_are_mapped_to_core()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Capabilities["supportsRestartRequest"] = true;
            a.Capabilities["supportsTerminateRequest"] = true;
            a.Capabilities["supportsStepBack"] = true;
        });
        var c = h.Session.Capabilities;
        Assert.True(c.SupportsConfigurationDoneRequest && c.SupportsFunctionBreakpoints && c.SupportsConditionalBreakpoints);
        Assert.True(c.SupportsLogPoints && c.SupportsRestartRequest && c.SupportsTerminateRequest && c.SupportsStepBack);
        Assert.False(c.SupportsDataBreakpoints);
        Assert.Equal(["all", "user-unhandled"], c.ExceptionBreakpointFilters!.Select(f => f.Filter));
        Assert.True(c.ExceptionBreakpointFilters![1].Default);
    }

    [Fact]
    public async Task Exception_filters_become_the_managers_filter_list_with_adapter_defaults()
    {
        await using var h = await SessionHarness.StartAsync();
        var filters = h.Breakpoints.ExceptionFilters;
        Assert.Equal(["all", "user-unhandled"], filters.Select(f => f.Filter));
        Assert.False(filters[0].Enabled);
        Assert.True(filters[1].Enabled);
        var set = h.Adapter.RequestsOf("setExceptionBreakpoints")[0].Args<DapSetExceptionBreakpointsArguments>();
        Assert.Equal(["user-unhandled"], set.Filters);
    }

    [Fact]
    public async Task Start_fails_when_launch_is_rejected_and_the_session_is_cleaned_up()
    {
        var h = new SessionHarness();
        h.Adapter.On("launch", _ => throw new FakeAdapterError("program not found"));
        h.Create(Cfg.Make());
        await using var _ = h;
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await h.Session.StartAsync(Ct));
        Assert.Contains("program not found", ex.Message);
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
        Assert.Single(h.Snapshot(h.TerminatedEvents));
        Assert.True(h.Adapter.Transport.Closed || h.Adapter.Transport.Completion.IsCompleted);
    }

    [Fact]
    public async Task Start_fails_when_adapter_never_sends_initialized()
    {
        var h = new SessionHarness();
        h.Adapter.NeverSendInitialized = true;
        h.Create(Cfg.Make(), new DebugSessionSettings { InitializedTimeout = TimeSpan.FromMilliseconds(300), RequestTimeout = TimeSpan.FromSeconds(5) });
        await using var _ = h;
        // launch succeeds but initialized never comes: after the short grace we proceed (spec-violating adapters still work)…
        await h.Session.StartAsync(Ct);
        Assert.Equal(DebugSessionState.Running, h.Session.State);
    }

    [Fact]
    public async Task Start_times_out_when_launch_hangs_and_initialized_never_comes()
    {
        var h = new SessionHarness();
        h.Adapter.NeverSendInitialized = true;
        h.Adapter.Stall("launch");
        h.Create(Cfg.Make(), new DebugSessionSettings { InitializedTimeout = TimeSpan.FromMilliseconds(300), RequestTimeout = TimeSpan.FromSeconds(5) });
        await using var _ = h;
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await h.Session.StartAsync(Ct));
        Assert.Contains("initialized", ex.Message);
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
    }

    [Fact]
    public async Task Start_fails_cleanly_when_the_adapter_dies_during_startup()
    {
        var h = new SessionHarness();
        h.Adapter.OnAsync("initialize", async _ => { h.Adapter.Crash(); await Task.Delay(50, Ct); return null; });
        h.Create(Cfg.Make());
        await using var _ = h;
        await Assert.ThrowsAsync<DebugSessionStartException>(async () => await h.Session.StartAsync(Ct));
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
    }

    [Fact]
    public async Task Start_can_be_cancelled()
    {
        var h = new SessionHarness();
        h.Adapter.Stall("initialize");
        h.Create(Cfg.Make());
        await using var _ = h;
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await h.Session.StartAsync(cts.Token));
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
    }
}

public class SessionExecutionTests
{
    [Fact]
    public async Task Stopped_event_moves_to_Stopped_focuses_the_thread_and_fires_once()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendStoppedAsync("breakpoint", threadId: 7, allThreads: true, hitBreakpointIds: [1001]);
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Stopped);
        var e = Assert.Single(h.Snapshot(h.StoppedEvents));
        Assert.Equal("breakpoint", e.Reason);
        Assert.Equal(7, e.ThreadId);
        Assert.True(e.AllThreadsStopped);
        Assert.Equal([1001], e.HitBreakpointIds);
        Assert.Equal(7, h.Session.FocusedThreadId);
    }

    [Fact]
    public async Task Repeated_all_threads_stops_for_one_pause_are_coalesced()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendStoppedAsync("pause", 1);
        await h.Adapter.SendStoppedAsync("pause", 2);
        await h.Adapter.SendStoppedAsync("pause", 3);
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Stopped);
        await Task.Delay(100, Ct);
        Assert.Single(h.Snapshot(h.StoppedEvents));
    }

    [Fact]
    public async Task A_later_distinct_stop_after_resume_is_delivered_again()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync();
        await h.Session.ContinueAsync(ct: Ct);
        await h.Adapter.SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Snapshot(h.StoppedEvents).Length == 2);
    }

    [Fact]
    public async Task Continue_sends_continue_for_the_focused_thread_and_fires_Continued()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync(threadId: 5);
        await h.Session.ContinueAsync(ct: Ct);
        Assert.Equal(5, h.Adapter.RequestsOf("continue")[0].Args<DapContinueArguments>().ThreadId);
        Assert.Equal(DebugSessionState.Running, h.Session.State);
        var c = Assert.Single(h.Snapshot(h.ContinuedEvents));
        Assert.Equal(5, c.ThreadId);
        Assert.True(c.AllThreadsContinued);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("stepIn")]
    [InlineData("stepOut")]
    public async Task Step_requests_carry_thread_and_granularity(string command)
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync(threadId: 2);
        var kind = command switch { "next" => DebugStepKind.Over, "stepIn" => DebugStepKind.Into, _ => DebugStepKind.Out };
        await h.Session.StepAsync(kind, null, new DebugStepOptions("instruction", SingleThread: true), Ct);
        var args = h.Adapter.RequestsOf(command)[0].Args<DapStepArguments>();
        Assert.Equal(2, args.ThreadId);
        Assert.Equal("instruction", args.Granularity);
        Assert.True(args.SingleThread);
    }

    [Fact]
    public async Task Interface_step_methods_map_to_next_stepIn_stepOut()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync();
        await h.Session.StepOverAsync(ct: Ct);
        await h.StopAtBreakpointAsync();
        await h.Session.StepIntoAsync(ct: Ct);
        await h.StopAtBreakpointAsync();
        await h.Session.StepOutAsync(ct: Ct);
        Assert.Equal(1, h.Adapter.CountOf("next"));
        Assert.Equal(1, h.Adapter.CountOf("stepIn"));
        Assert.Equal(1, h.Adapter.CountOf("stepOut"));
    }

    [Fact]
    public async Task Resume_is_optimistic_so_a_stop_that_follows_the_response_immediately_wins()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.AfterResponse("next", () => h.Adapter.SendStoppedAsync("step", 1));
        await h.StopAtBreakpointAsync();
        await h.Session.StepOverAsync(ct: Ct);
        await SessionHarness.Eventually(() => h.Snapshot(h.StoppedEvents).Length == 2);
        await Task.Delay(100, Ct); // a late "set Running" must not overwrite the newer Stopped
        Assert.Equal(DebugSessionState.Stopped, h.Session.State);
    }

    [Fact]
    public async Task Failed_resume_rolls_the_state_back_to_Stopped()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.On("continue", _ => throw new FakeAdapterError("cannot continue"));
        await h.StopAtBreakpointAsync();
        await Assert.ThrowsAsync<DapRequestException>(async () => await h.Session.ContinueAsync(ct: Ct));
        Assert.Equal(DebugSessionState.Stopped, h.Session.State);
    }

    [Fact]
    public async Task Pause_without_a_focused_thread_asks_for_threads_first()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Session.PauseAsync(ct: Ct);
        Assert.Equal(1, h.Adapter.CountOf("threads"));
        Assert.Equal(1, h.Adapter.RequestsOf("pause")[0].Args<DapPauseArguments>().ThreadId);
    }

    [Fact]
    public async Task Continued_event_from_the_adapter_resumes_a_stopped_session()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync();
        await h.Adapter.SendEventAsync("continued", new DapContinuedEvent { ThreadId = 1, AllThreadsContinued = true });
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Running);
        Assert.Single(h.Snapshot(h.ContinuedEvents));
    }

    [Fact]
    public async Task Threads_are_mapped()
    {
        await using var h = await SessionHarness.StartAsync();
        var t = await h.Session.GetThreadsAsync(Ct);
        Assert.Equal(new DebugThread(1, "main"), Assert.Single(t));
    }

    [Fact]
    public async Task Thread_events_are_forwarded()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendEventAsync("thread", new DapThreadEvent { Reason = "started", ThreadId = 9 });
        await SessionHarness.Eventually(() => h.Snapshot(h.ThreadEvents).Length == 1);
        Assert.Equal(("started", 9), (h.ThreadEvents[0].Reason, h.ThreadEvents[0].ThreadId));
    }

    [Fact]
    public async Task Exception_stop_fetches_exceptionInfo_and_surfaces_it()
    {
        await using var h = await SessionHarness.StartAsync();
        var seen = new TaskCompletionSource<DebugExceptionInfo?>();
        h.Session.ExceptionInfoChanged.Subscribe(e => seen.TrySetResult(e));
        await h.Adapter.SendStoppedAsync("exception", 1);
        var info = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("System.InvalidOperationException", info!.ExceptionId);
        Assert.Equal("boom", info.Message);
        Assert.Equal("at Main()", info.StackTrace);
        Assert.Equal(info, h.Session.LastException);
    }

    [Fact]
    public async Task Resuming_clears_the_last_exception()
    {
        await using var h = await SessionHarness.StartAsync();
        var seen = new TaskCompletionSource<DebugExceptionInfo?>();
        h.Session.ExceptionInfoChanged.Subscribe(e => seen.TrySetResult(e));
        await h.Adapter.SendStoppedAsync("exception", 1);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await h.Session.ContinueAsync(ct: Ct);
        Assert.Null(h.Session.LastException);
    }

    [Fact]
    public async Task Custom_requests_pass_through_arguments_and_body()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.On("vendorThing", r => new { echoed = r.Arguments.GetProperty("x").GetInt32() * 2 });
        var body = await h.Session.CustomRequestAsync("vendorThing", JsonSerializer.SerializeToElement(new { x = 21 }), Ct);
        Assert.Equal(42, body!.Value.GetProperty("echoed").GetInt32());
    }

    [Fact]
    public async Task Unsupported_request_failure_is_reported_not_swallowed()
    {
        await using var h = await SessionHarness.StartAsync();
        await Assert.ThrowsAsync<DapRequestException>(async () => await h.Session.CustomRequestAsync("nope", null, Ct));
    }

    [Fact]
    public async Task Modules_are_tracked_and_removed()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendEventAsync("module", new DapModuleEvent { Reason = "new", Module = new DapModule { Id = JsonSerializer.SerializeToElement("m1"), Name = "app.dll", Path = "/w/app.dll" } });
        await SessionHarness.Eventually(() => h.Session.Modules.Count == 1);
        Assert.Equal("app.dll", h.Session.Modules[0].Name);
        await h.Adapter.SendEventAsync("module", new DapModuleEvent { Reason = "removed", Module = new DapModule { Id = JsonSerializer.SerializeToElement("m1"), Name = "app.dll" } });
        await SessionHarness.Eventually(() => h.Session.Modules.Count == 0);
    }

    [Fact]
    public async Task Loaded_sources_are_tracked_announced_and_removed()
    {
        await using var h = await SessionHarness.StartAsync();
        var seen = new List<DebugLoadedSourceEventArgs>();
        h.Session.LoadedSourceChanged.Subscribe(e => { lock (seen) seen.Add(e); });
        await h.Adapter.SendEventAsync("loadedSource", new DapLoadedSourceEvent { Reason = "new", Source = new DapSource { Name = "a.cs", Path = "/w/a.cs" } });
        await h.Adapter.SendEventAsync("loadedSource", new DapLoadedSourceEvent { Reason = "new", Source = new DapSource { Name = "gen.cs", SourceReference = 4 } });
        await SessionHarness.Eventually(() => h.Session.LoadedSources.Count == 2);
        Assert.Contains(h.Session.LoadedSources, s => s.Path == "/w/a.cs");
        Assert.Contains(h.Session.LoadedSources, s => s.SourceReference == 4);
        await h.Adapter.SendEventAsync("loadedSource", new DapLoadedSourceEvent { Reason = "removed", Source = new DapSource { Name = "a.cs", Path = "/w/a.cs" } });
        await SessionHarness.Eventually(() => h.Session.LoadedSources.Count == 1);
        lock (seen) Assert.Equal(["new", "new", "removed"], seen.Select(e => e.Reason));
    }

    [Fact]
    public async Task Loaded_sources_and_modules_can_also_be_requested_when_the_adapter_supports_it()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Capabilities["supportsLoadedSourcesRequest"] = true;
            a.Capabilities["supportsModulesRequest"] = true;
        });
        var sources = await h.Session.GetLoadedSourcesAsync(Ct);
        Assert.Equal("/work/Program.cs", Assert.Single(sources).Path);
        var modules = await h.Session.GetModulesAsync(Ct);
        Assert.Equal("app.dll", Assert.Single(modules).Name);

        await using var none = await SessionHarness.StartAsync();
        Assert.Empty(await none.Session.GetLoadedSourcesAsync(Ct)); // not advertised: no request is sent
        Assert.Equal(0, none.Adapter.CountOf("loadedSources"));
    }

    [Fact]
    public async Task Process_event_records_the_debuggee_pid()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendEventAsync("process", new DapProcessEvent { Name = "app", SystemProcessId = 777 });
        await SessionHarness.Eventually(() => h.Session.DebuggeeProcessId == 777);
        Assert.Equal("app", h.Session.DebuggeeProcessName);
    }

    [Fact]
    public async Task Progress_events_are_forwarded()
    {
        await using var h = await SessionHarness.StartAsync();
        var seen = new List<DebugProgressEventArgs>();
        h.Session.Progress.Subscribe(e => { lock (seen) seen.Add(e); });
        await h.Adapter.SendEventAsync("progressStart", new DapProgressStartEvent { ProgressId = "p1", Title = "Loading symbols" });
        await h.Adapter.SendEventAsync("progressUpdate", new DapProgressUpdateEvent { ProgressId = "p1", Message = "50%", Percentage = 50 });
        await h.Adapter.SendEventAsync("progressEnd", new DapProgressEndEvent { ProgressId = "p1" });
        await SessionHarness.Eventually(() => { lock (seen) return seen.Count == 3; });
        Assert.Equal(["start", "update", "end"], seen.Select(e => e.Kind));
    }

    [Fact]
    public async Task Capabilities_event_overlays_changed_fields_only()
    {
        await using var h = await SessionHarness.StartAsync();
        Assert.False(h.Session.Capabilities.SupportsDataBreakpoints);
        await h.Adapter.SendEventAsync("capabilities", new DapCapabilitiesEvent { Capabilities = new JsonObject { ["supportsDataBreakpoints"] = true } });
        await SessionHarness.Eventually(() => h.Session.Capabilities.SupportsDataBreakpoints);
        Assert.True(h.Session.Capabilities.SupportsConditionalBreakpoints); // untouched
    }
}

public class SessionDataTests
{
    [Fact]
    public async Task Lines_and_columns_are_converted_from_1_based_to_0_based()
    {
        await using var h = await SessionHarness.StartAsync();
        var (frames, _) = await h.StopAndLoadAsync();
        Assert.Equal((9, 4), (frames[0].Line, frames[0].Column)); // wire 10:5
        Assert.Equal((19, 0), (frames[1].Line, frames[1].Column)); // wire 20:1
    }

    [Fact]
    public async Task Lines_and_columns_are_passed_through_when_the_session_is_0_based()
    {
        var settings = new DebugSessionSettings { LinesStartAt1 = false, ColumnsStartAt1 = false };
        await using var h = await SessionHarness.StartAsync(settings: settings);
        var init = h.Adapter.RequestsOf("initialize")[0].Args<DapInitializeArguments>();
        Assert.False(init.LinesStartAt1);
        Assert.False(init.ColumnsStartAt1);
        var (frames, _) = await h.StopAndLoadAsync();
        Assert.Equal((10, 5), (frames[0].Line, frames[0].Column));
    }

    [Fact]
    public async Task Mixed_conventions_are_supported_independently()
    {
        var settings = new DebugSessionSettings { LinesStartAt1 = true, ColumnsStartAt1 = false };
        await using var h = await SessionHarness.StartAsync(settings: settings);
        var (frames, _) = await h.StopAndLoadAsync();
        Assert.Equal((9, 5), (frames[0].Line, frames[0].Column));
    }

    [Fact]
    public async Task End_positions_and_source_are_mapped()
    {
        await using var h = await SessionHarness.StartAsync(a => a.Frames =
            [new DapStackFrame { Id = 1, Name = "f", Source = new DapSource { Name = "x.cs", Path = "/w/x.cs" }, Line = 3, Column = 2, EndLine = 3, EndColumn = 9, PresentationHint = "subtle" }]);
        var (frames, _) = await h.StopAndLoadAsync();
        Assert.Equal((2, 1, 2, 8), (frames[0].Line, frames[0].Column, frames[0].EndLine!.Value, frames[0].EndColumn!.Value));
        Assert.Equal("/w/x.cs", frames[0].Source!.Path);
        Assert.Equal("subtle", frames[0].PresentationHint);
    }

    [Fact]
    public async Task Stack_paging_uses_startFrame_and_levels_when_the_adapter_supports_delayed_loading()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Capabilities["supportsDelayedStackTraceLoading"] = true;
            a.Frames = [.. Enumerable.Range(0, 30).Select(i => new DapStackFrame { Id = 100 + i, Name = "f" + i, Line = i + 1, Column = 1 })];
        });
        await h.StopAtBreakpointAsync();
        var page = await h.Session.GetStackTraceAsync(1, startFrame: 10, levels: 5, ct: Ct);
        Assert.Equal(["f10", "f11", "f12", "f13", "f14"], page.Select(f => f.Name));
        var args = h.Adapter.RequestsOf("stackTrace")[0].Args<DapStackTraceArguments>();
        Assert.Equal((10, 5), (args.StartFrame!.Value, args.Levels!.Value));
    }

    [Fact]
    public async Task Stack_paging_is_done_client_side_for_adapters_that_return_everything()
    {
        await using var h = await SessionHarness.StartAsync(a =>
            a.Frames = [.. Enumerable.Range(0, 30).Select(i => new DapStackFrame { Id = 100 + i, Name = "f" + i, Line = i + 1, Column = 1 })]);
        await h.StopAtBreakpointAsync();
        var page = await h.Session.GetStackTraceAsync(1, startFrame: 10, levels: 5, ct: Ct);
        Assert.Equal(["f10", "f11", "f12", "f13", "f14"], page.Select(f => f.Name));
        var args = h.Adapter.RequestsOf("stackTrace")[0].Args<DapStackTraceArguments>();
        Assert.Null(args.StartFrame); // not sent: the adapter would ignore it
    }

    [Fact]
    public async Task Source_references_resolve_to_debug_uris_and_fetch_content()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Frames = [new DapStackFrame { Id = 1, Name = "dyn", Source = new DapSource { Name = "gen.cs", SourceReference = 55 }, Line = 1, Column = 1 }];
            a.SourceContents[55] = "// generated";
        });
        var (frames, _) = await h.StopAndLoadAsync();
        Assert.Equal(55, frames[0].Source!.SourceReference);
        var uri = h.Session.ResolveSourceUri(frames[0].Source);
        Assert.Equal("debug", uri!.Scheme);
        Assert.True(DebugSourceUri.TryParse(uri, out var sid, out var reference, out var name));
        Assert.Equal(("s1", 55, "gen.cs"), (sid, reference, name));
        Assert.Equal("// generated", await h.Session.GetSourceContentAsync(55, Ct));
    }

    [Fact]
    public async Task Path_sources_resolve_to_file_uris()
    {
        await using var h = await SessionHarness.StartAsync();
        var uri = h.Session.ResolveSourceUri(new DebugSource("Program.cs", "/work/Program.cs"));
        Assert.Equal(new Uri("file:///work/Program.cs"), uri);
        Assert.Null(h.Session.ResolveSourceUri(new DebugSource("x", null)));
    }

    [Fact]
    public async Task Scopes_and_variables_are_fetched_and_mapped()
    {
        await using var h = await SessionHarness.StartAsync(a =>
            a.VariablesByReference[1] = [new DapVariable { Name = "x", Value = "42", Type = "int", EvaluateName = "x" }, new DapVariable { Name = "s", Value = "\"hi\"", Type = "string", VariablesReference = 9, NamedVariables = 2 }]);
        var (_, scopes) = await h.StopAndLoadAsync();
        Assert.Equal(["Locals", "Globals"], scopes.Select(s => s.Name));
        Assert.True(scopes[1].Expensive);
        var vars = await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal(("x", "42", "int"), (vars[0].Name, vars[0].Value, vars[0].Type));
        Assert.Equal(9, vars[1].VariablesReference);
        Assert.Equal(2, vars[1].NamedVariables);
    }

    [Fact]
    public async Task Variable_paging_passes_start_count_and_filter()
    {
        await using var h = await SessionHarness.StartAsync(a =>
            a.VariablesByReference[1] = [.. Enumerable.Range(0, 100).Select(i => new DapVariable { Name = $"[{i}]", Value = i.ToString() })]);
        var (_, scopes) = await h.StopAndLoadAsync();
        var page = await h.Session.GetVariablesAsync(scopes[0].VariablesReference, "indexed", 20, 10, Ct);
        Assert.Equal(10, page.Count);
        Assert.Equal("[20]", page[0].Name);
        var args = h.Adapter.RequestsOf("variables")[0].Args<DapVariablesArguments>();
        Assert.Equal(("indexed", 20, 10), (args.Filter, args.Start, args.Count));
    }

    [Fact]
    public async Task A_hostile_adapter_ignoring_count_cannot_make_us_return_more_than_the_cap()
    {
        var settings = new DebugSessionSettings { MaxVariablesPerRequest = 500 };
        await using var h = await SessionHarness.StartAsync(a =>
            a.On("variables", _ => new DapVariablesResponse { Variables = [.. Enumerable.Range(0, 5000).Select(i => new DapVariable { Name = "v" + i, Value = "1" })] }), settings: settings);
        var (_, scopes) = await h.StopAndLoadAsync();
        Assert.Equal(500, (await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct)).Count);
        Assert.Equal(50, (await h.Session.GetVariablesAsync(scopes[0].VariablesReference, 0, 50, Ct)).Count);
    }

    [Fact]
    public async Task Variables_of_reference_zero_is_empty_without_asking_the_adapter()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAndLoadAsync();
        Assert.Empty(await h.Session.GetVariablesAsync(0, ct: Ct));
        Assert.Equal(0, h.Adapter.CountOf("variables"));
    }

    [Fact]
    public async Task Variable_pages_are_cached_until_the_debuggee_resumes()
    {
        await using var h = await SessionHarness.StartAsync(a => a.VariablesByReference[1] = [new DapVariable { Name = "x", Value = "1" }]);
        var (_, scopes) = await h.StopAndLoadAsync();
        await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal(1, h.Adapter.CountOf("variables"));
        await h.Session.GetScopesAsync((await h.Session.GetStackTraceAsync(1, ct: Ct))[0].Id, Ct);
        Assert.Equal(1, h.Adapter.CountOf("scopes")); // scopes cached too
    }

    [Fact]
    public async Task Handles_are_invalidated_on_resume_and_stale_use_fails_without_reaching_the_adapter()
    {
        await using var h = await SessionHarness.StartAsync(a => a.VariablesByReference[1] = [new DapVariable { Name = "x", Value = "1" }]);
        var (frames, scopes) = await h.StopAndLoadAsync();
        await h.Session.ContinueAsync(ct: Ct);
        var before = h.Adapter.CountOf("variables");
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct));
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await h.Session.GetScopesAsync(frames[0].Id, Ct));
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await h.Session.EvaluateAsync("x", frames[0].Id, ct: Ct));
        Assert.Equal(before, h.Adapter.CountOf("variables"));
        Assert.Equal(0, h.Session.TrackedVariableHandles);
    }

    [Fact]
    public async Task Handles_are_invalidated_on_step_and_fresh_ones_work_after_the_next_stop()
    {
        await using var h = await SessionHarness.StartAsync(a => a.VariablesByReference[1] = [new DapVariable { Name = "x", Value = "1" }]);
        var (_, scopes) = await h.StopAndLoadAsync();
        await h.Session.StepOverAsync(ct: Ct);
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct));
        var (_, scopes2) = await h.StopAndLoadAsync();
        Assert.Single(await h.Session.GetVariablesAsync(scopes2[0].VariablesReference, ct: Ct));
    }

    [Fact]
    public async Task Invalidated_event_with_variables_area_drops_handles_and_notifies()
    {
        await using var h = await SessionHarness.StartAsync();
        var (_, scopes) = await h.StopAndLoadAsync();
        await h.Adapter.SendEventAsync("invalidated", new DapInvalidatedEvent { Areas = ["variables"] });
        await SessionHarness.Eventually(() => h.InvalidatedCount == 1);
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct));
    }

    [Fact]
    public async Task Invalidated_variables_keep_the_frames_of_the_stop_and_scopes_are_fetched_again()
    {
        await using var h = await SessionHarness.StartAsync();
        var (frames, _) = await h.StopAndLoadAsync();
        await h.Adapter.SendEventAsync("invalidated", new DapInvalidatedEvent { Areas = ["variables"] });
        await SessionHarness.Eventually(() => h.InvalidatedCount == 1);
        var scopes = await h.Session.GetScopesAsync(frames[0].Id, Ct); // the frame is still valid (DAP: only variable data died)
        Assert.NotEmpty(scopes);
        Assert.Equal(2, h.Adapter.CountOf("scopes")); // the cached scope list (old variablesReferences) was not reused
        await h.Session.EvaluateAsync("x", frames[0].Id, ct: Ct);
    }

    [Theory]
    [InlineData("stacks")]
    [InlineData("all")]
    [InlineData(null)]
    public async Task Invalidated_stacks_kill_the_frames_of_the_stop_until_the_stack_is_fetched_again(string? area)
    {
        await using var h = await SessionHarness.StartAsync();
        var (frames, _) = await h.StopAndLoadAsync();
        await h.Adapter.SendEventAsync("invalidated", new DapInvalidatedEvent { Areas = area is null ? null : [area] });
        await SessionHarness.Eventually(() => h.InvalidatedCount == 1);
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await h.Session.GetScopesAsync(frames[0].Id, Ct));
        var again = await h.Session.GetStackTraceAsync(1, 0, 20, Ct);
        Assert.NotEmpty(await h.Session.GetScopesAsync(again[0].Id, Ct));
    }

    [Fact]
    public async Task Invalidated_event_for_unrelated_areas_keeps_handles()
    {
        await using var h = await SessionHarness.StartAsync();
        var (_, scopes) = await h.StopAndLoadAsync();
        await h.Adapter.SendEventAsync("invalidated", new DapInvalidatedEvent { Areas = ["threads"] });
        await SessionHarness.Eventually(() => h.InvalidatedCount == 1);
        await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct); // still valid
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("repl")]
    [InlineData("hover")]
    [InlineData("clipboard")]
    public async Task Evaluate_passes_the_context_through(string context)
    {
        await using var h = await SessionHarness.StartAsync();
        var (frames, _) = await h.StopAndLoadAsync();
        var r = await h.Session.EvaluateAsync("x + 1", frames[0].Id, context, Ct);
        Assert.Equal($"<x + 1@{context}>", r.Result);
        var args = h.Adapter.RequestsOf("evaluate")[0].Args<DapEvaluateArguments>();
        Assert.Equal((context, frames[0].Id), (args.Context, args.FrameId!.Value));
    }

    [Fact]
    public async Task Evaluate_without_a_frame_works_while_running()
    {
        await using var h = await SessionHarness.StartAsync();
        var r = await h.Session.EvaluateAsync("1+1", null, "repl", Ct);
        Assert.NotNull(r.Result);
        Assert.Null(h.Adapter.RequestsOf("evaluate")[0].Args<DapEvaluateArguments>().FrameId);
    }

    [Fact]
    public async Task Evaluate_result_with_children_registers_its_handle()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.On("evaluate", _ => new DapEvaluateResponse { Result = "{...}", VariablesReference = 77, NamedVariables = 1 });
            a.VariablesByReference[77] = [new DapVariable { Name = "inner", Value = "1" }];
        });
        var (frames, _) = await h.StopAndLoadAsync();
        var r = await h.Session.EvaluateAsync("obj", frames[0].Id, "watch", Ct);
        var kids = await h.Session.GetVariablesAsync(r.VariablesReference, ct: Ct);
        Assert.Equal("inner", kids[0].Name);
    }

    [Fact]
    public async Task SetVariable_returns_the_new_value_and_drops_cached_pages()
    {
        await using var h = await SessionHarness.StartAsync(a => a.VariablesByReference[1] = [new DapVariable { Name = "x", Value = "1" }]);
        var (_, scopes) = await h.StopAndLoadAsync();
        await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        var v = await h.Session.SetVariableAsync(scopes[0].VariablesReference, "x", "99", Ct);
        Assert.Equal(("x", "99"), (v.Name, v.Value));
        await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal(2, h.Adapter.CountOf("variables")); // refetched, not served from the stale cache
    }

    [Fact]
    public async Task Completions_convert_columns_and_are_cached()
    {
        await using var h = await SessionHarness.StartAsync();
        var (frames, _) = await h.StopAndLoadAsync();
        var a = await h.Session.CompletionsAsync(frames[0].Id, "Con", 3, Ct);
        var b = await h.Session.CompletionsAsync(frames[0].Id, "Con", 3, Ct);
        Assert.Equal("foo", a[0].Label);
        Assert.Equal(a, b);
        Assert.Equal(1, h.Adapter.CountOf("completions"));
        Assert.Equal(4, h.Adapter.RequestsOf("completions")[0].Args<DapCompletionsArguments>().Column); // 0-based 3 → wire 4
        Assert.Equal(0, a[0].Start); // wire start 0 → 0-based -1 clamped to 0
    }

    [Fact]
    public async Task Completions_return_nothing_when_the_adapter_does_not_support_them()
    {
        await using var h = await SessionHarness.StartAsync(a => a.Capabilities["supportsCompletionsRequest"] = false);
        Assert.Empty(await h.Session.CompletionsAsync(null, "x", 1, Ct));
        Assert.Equal(0, h.Adapter.CountOf("completions"));
    }
}
