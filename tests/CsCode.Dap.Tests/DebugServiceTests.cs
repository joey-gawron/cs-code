using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

public class DebugServiceStartTests
{
    [Fact]
    public async Task Start_runs_the_whole_pipeline_over_real_tcp_and_registers_the_session()
    {
        await using var h = new ServiceHarness();
        var started = new List<IDebugSession>();
        h.Service.SessionStarted.Subscribe(s => { lock (started) started.Add(s); });
        var states = new List<DebugState>();
        h.Service.StateChanged.Subscribe(s => { lock (states) states.Add(s); });

        var session = await h.StartAsync();

        Assert.Same(session, Assert.Single(h.Service.Sessions));
        Assert.Same(session, h.Service.ActiveSession);
        Assert.Equal(DebugState.Running, h.Service.State);
        lock (started) Assert.Same(session, Assert.Single(started));
        lock (states) Assert.Equal([DebugState.Initializing, DebugState.Running], states);
        Assert.Equal(["initialize", "launch", "setExceptionBreakpoints", "configurationDone"], h.AdapterFor(0).Commands);
    }

    [Fact]
    public async Task Variables_in_the_configuration_are_substituted_before_launch()
    {
        await using var h = new ServiceHarness();
        await h.StartAsync(ServiceHarness.Config(extra: new Dictionary<string, object?> { ["program"] = "${workspaceFolder}/bin/app.dll", ["cwd"] = "${workspaceFolder}" }, folder: h.Dir.Uri));
        var root = h.Dir.Path.TrimEnd('/');
        var args = h.AdapterFor(0).RequestsOf("launch")[0].Arguments;
        Assert.Equal($"{root}/bin/app.dll", args.GetProperty("program").GetString());
        Assert.Equal(root, args.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task An_unresolvable_variable_shows_an_error_and_starts_nothing()
    {
        await using var h = new ServiceHarness();
        var cfg = ServiceHarness.Config(extra: new Dictionary<string, object?> { ["program"] = "${file}" }, folder: h.Dir.Uri);
        Assert.Null(await h.Service.StartAsync(cfg, Ct));
        Assert.Empty(h.Service.Sessions);
        Assert.Contains(h.Window.Messages, m => m.Severity == MessageSeverity.Error && m.Message.Contains("no file is open"));
        Assert.Empty(h.Host.Adapters);
    }

    [Fact]
    public async Task A_dismissed_input_prompt_cancels_the_start_quietly()
    {
        await using var h = new ServiceHarness();
        h.Window.InputResponses.Enqueue(null);
        h.Dir.WriteFile(".cscode/launch.json", """{"configurations":[],"inputs":[{"id":"x","type":"promptString"}]}""");
        var cfg = ServiceHarness.Config(extra: new Dictionary<string, object?> { ["a"] = "${input:x}" }, folder: h.Dir.Uri);
        Assert.Null(await h.Service.StartAsync(cfg, Ct));
        Assert.Empty(h.Window.Messages);
    }

    [Fact]
    public async Task No_adapter_for_the_type_shows_a_helpful_error()
    {
        await using var h = new ServiceHarness();
        Assert.Null(await h.Service.StartAsync(ServiceHarness.Config(type: "mystery", folder: h.Dir.Uri), Ct));
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("mystery") && m.Message.Contains("cscode.debuggers"));
    }

    [Fact]
    public async Task A_configuration_provider_can_amend_and_can_abort()
    {
        await using var h = new ServiceHarness();
        using var reg = h.Service.RegisterConfigurationProvider("fake", new AmendingProvider());
        await h.StartAsync();
        Assert.Equal("added-by-provider", h.AdapterFor(0).RequestsOf("launch")[0].Arguments.GetProperty("extra").GetString());

        using var abort = h.Service.RegisterConfigurationProvider("fake", new AbortingProvider());
        Assert.Null(await h.Service.StartAsync(ServiceHarness.Config(folder: h.Dir.Uri), Ct));
        Assert.Single(h.Service.Sessions);
    }

    [Fact]
    public async Task Disposing_a_provider_registration_stops_it_being_consulted()
    {
        await using var h = new ServiceHarness();
        var reg = h.Service.RegisterConfigurationProvider("fake", new AbortingProvider());
        reg.Dispose();
        Assert.NotNull(await h.Service.StartAsync(ServiceHarness.Config(folder: h.Dir.Uri), Ct));
    }

    [Fact]
    public async Task The_latest_adapter_factory_registration_wins_and_disposal_restores_the_previous()
    {
        await using var h = new ServiceHarness();
        var other = new FixedPortAdapterFactory(h.Host.Port);
        var reg = h.Service.RegisterAdapterFactory("fake", other);
        await h.StartAsync();
        Assert.Equal(1, other.Calls);
        Assert.Equal(0, h.FakeFactory.Calls);
        reg.Dispose();
        await h.StartAsync(ServiceHarness.Config("second", folder: h.Dir.Uri));
        Assert.Equal(1, h.FakeFactory.Calls);
    }

    [Fact]
    public async Task A_failing_launch_reports_the_adapters_message_and_leaves_no_session_behind()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.On("launch", _ => throw new FakeAdapterError("program not found: /x")));
        Assert.Null(await h.Service.StartAsync(ServiceHarness.Config(folder: h.Dir.Uri), Ct));
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("program not found: /x"));
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
        Assert.Equal(DebugState.Inactive, h.Service.State);
        Assert.Null(h.Service.ActiveSession);
    }

    [Fact]
    public async Task A_debugServer_port_in_the_configuration_connects_without_any_factory()
    {
        await using var h = new ServiceHarness();
        var cfg = ServiceHarness.Config(type: "no-factory-for-this", extra: new Dictionary<string, object?> { ["debugServer"] = h.Host.Port }, folder: h.Dir.Uri);
        var s = await h.Service.StartAsync(cfg, Ct);
        Assert.NotNull(s);
        Assert.Equal(0, h.FakeFactory.Calls);
    }

    [Fact]
    public async Task A_host_colon_port_debugServer_connects_to_that_host()
    {
        await using var h = new ServiceHarness();
        var cfg = ServiceHarness.Config(type: "no-factory-for-this", extra: new Dictionary<string, object?> { ["debugServer"] = $"127.0.0.1:{h.Host.Port}" }, folder: h.Dir.Uri);
        Assert.NotNull(await h.Service.StartAsync(cfg, Ct));
        var bare = ServiceHarness.Config(type: "no-factory-for-this", extra: new Dictionary<string, object?> { ["debugServer"] = h.Host.Port.ToString() }, folder: h.Dir.Uri);
        Assert.NotNull(await h.Service.StartAsync(bare, Ct));
    }

    [Fact]
    public async Task Start_returns_immediately_to_the_caller()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.DelayAllResponses(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pending = h.Service.StartAsync(ServiceHarness.Config(folder: h.Dir.Uri), Ct);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50), $"StartAsync blocked its caller for {sw.Elapsed}");
        Assert.NotNull(await pending);
    }

    [Fact]
    public async Task Debugger_types_include_the_builtin_dotnet_adapter_and_registered_ones()
    {
        await using var h = new ServiceHarness();
        Assert.Contains(h.Service.DebuggerTypes, t => t.Type == "coreclr" && t.Languages.Contains("csharp"));
        // RegisterAdapterFactory only enqueues: the snapshot is rebuilt on the Debug lane (on a loaded machine, after this line runs).
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "fake"), what: "the registered type is published");
    }

    private sealed class AmendingProvider : IDebugConfigurationProvider
    {
        public ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
        {
            var props = new Dictionary<string, JsonElement>(configuration.Properties) { ["extra"] = JsonSerializer.SerializeToElement("added-by-provider") };
            return ValueTask.FromResult<DebugConfiguration?>(configuration with { Properties = props });
        }
    }

    private sealed class AbortingProvider : IDebugConfigurationProvider
    {
        public ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct) => ValueTask.FromResult<DebugConfiguration?>(null);
    }
}

public class DebugServiceTaskTests
{
    [Fact]
    public async Task preLaunchTask_runs_before_the_adapter_is_contacted()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "needs /bin/sh");
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => { });
        h.Dir.WriteFile(".cscode/tasks.json", """{"tasks":[{"label":"build","command":"echo built > built.txt"}]}""");
        var seenBuiltAtLaunch = false;
        h.Host.Configure = (a, _) => a.On("launch", _ => { seenBuiltAtLaunch = File.Exists(Path.Combine(h.Dir.Path, "built.txt")); return null; });
        await h.StartAsync(ServiceHarness.Config(extra: new Dictionary<string, object?> { ["preLaunchTask"] = "build" }, folder: h.Dir.Uri));
        Assert.True(seenBuiltAtLaunch, "the build task must have finished before launch was sent");
    }

    [Fact]
    public async Task A_failing_preLaunchTask_aborts_by_default_and_tells_the_user()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "needs /bin/sh");
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/tasks.json", """{"tasks":[{"label":"build","command":"exit 2"}]}""");
        var s = await h.Service.StartAsync(ServiceHarness.Config(extra: new Dictionary<string, object?> { ["preLaunchTask"] = "build" }, folder: h.Dir.Uri), Ct);
        Assert.Null(s);
        Assert.Empty(h.Host.Adapters);
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("preLaunchTask 'build'") && m.Message.Contains("exit code 2"));
    }

    [Fact]
    public async Task Debug_Anyway_continues_after_a_failed_preLaunchTask()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "needs /bin/sh");
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/tasks.json", """{"tasks":[{"label":"build","command":"exit 2"}]}""");
        h.Window.MessageActionResponses.Enqueue("Debug Anyway");
        var s = await h.Service.StartAsync(ServiceHarness.Config(extra: new Dictionary<string, object?> { ["preLaunchTask"] = "build" }, folder: h.Dir.Uri), Ct);
        Assert.NotNull(s);
    }

    [Fact]
    public async Task An_unknown_preLaunchTask_is_reported()
    {
        await using var h = new ServiceHarness();
        var s = await h.Service.StartAsync(ServiceHarness.Config(extra: new Dictionary<string, object?> { ["preLaunchTask"] = "ghost" }, folder: h.Dir.Uri), Ct);
        Assert.Null(s);
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("ghost"));
    }

    [Fact]
    public async Task postDebugTask_runs_after_the_session_ends()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "needs /bin/sh");
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/tasks.json", """{"tasks":[{"label":"cleanup","command":"echo done > cleaned.txt"}]}""");
        var s = await h.StartAsync(ServiceHarness.Config(extra: new Dictionary<string, object?> { ["postDebugTask"] = "cleanup" }, folder: h.Dir.Uri));
        await s.StopAsync(Ct);
        await SessionHarness.Eventually(() => File.Exists(Path.Combine(h.Dir.Path, "cleaned.txt")), 8000, "postDebugTask output");
    }
}

public class DebugServiceSessionTests
{
    [Fact]
    public async Task StopAll_ends_every_session_and_returns_to_Inactive()
    {
        await using var h = new ServiceHarness();
        var ended = new List<IDebugSession>();
        h.Service.SessionTerminated.Subscribe(s => { lock (ended) ended.Add(s); });
        await h.StartAsync(ServiceHarness.Config("A", folder: h.Dir.Uri));
        await h.StartAsync(ServiceHarness.Config("B", folder: h.Dir.Uri));
        Assert.Equal(2, h.Service.Sessions.Count);
        await h.Service.StopAllAsync(Ct);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
        Assert.Equal(DebugState.Inactive, h.Service.State);
        Assert.Null(h.Service.ActiveSession);
        lock (ended) Assert.Equal(2, ended.Count);
    }

    [Fact]
    public async Task The_newest_session_is_active_and_the_active_session_can_be_switched()
    {
        await using var h = new ServiceHarness();
        var a = await h.StartAsync(ServiceHarness.Config("A", folder: h.Dir.Uri));
        var b = await h.StartAsync(ServiceHarness.Config("B", folder: h.Dir.Uri));
        Assert.Same(b, h.Service.ActiveSession);
        var changes = new List<IDebugSession?>();
        h.Service.ActiveSessionChanged.Subscribe(s => { lock (changes) changes.Add(s); });
        h.Service.ActiveSession = a;
        Assert.Same(a, h.Service.ActiveSession);
        lock (changes) Assert.Same(a, Assert.Single(changes));
    }

    [Fact]
    public async Task Only_live_sessions_can_be_made_active()
    {
        await using var h = new ServiceHarness();
        var a = await h.StartAsync();
        h.Service.ActiveSession = new FakeForeignSession();
        Assert.Same(a, h.Service.ActiveSession);
    }

    [Fact]
    public async Task Aggregate_state_follows_the_active_session()
    {
        await using var h = new ServiceHarness();
        var a = await h.StartAsync(ServiceHarness.Config("A", folder: h.Dir.Uri));
        var b = await h.StartAsync(ServiceHarness.Config("B", folder: h.Dir.Uri));
        await h.AdapterFor(1).SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Service.State == DebugState.Stopped);
        h.Service.ActiveSession = a; // a is running
        Assert.Equal(DebugState.Running, h.Service.State);
        h.Service.ActiveSession = b;
        Assert.Equal(DebugState.Stopped, h.Service.State);
    }

    [Fact]
    public async Task When_the_active_session_ends_focus_falls_back_to_a_remaining_one()
    {
        await using var h = new ServiceHarness();
        var a = await h.StartAsync(ServiceHarness.Config("A", folder: h.Dir.Uri));
        var b = await h.StartAsync(ServiceHarness.Config("B", folder: h.Dir.Uri));
        await b.StopAsync(Ct);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 1);
        Assert.Same(a, h.Service.ActiveSession);
        Assert.Equal(DebugState.Running, h.Service.State);
    }

    [Fact]
    public async Task A_session_that_ends_on_its_own_is_removed_and_disposed()
    {
        await using var h = new ServiceHarness();
        var s = (DebugSession)await h.StartAsync();
        await h.AdapterFor(0).SendExitedAsync(0);
        await h.AdapterFor(0).SendTerminatedAsync();
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
        Assert.Equal(DebugState.Inactive, h.Service.State);
        await s.Disposed.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the service releases ended sessions in the background
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await s.GetThreadsAsync(Ct));
    }

    [Fact]
    public async Task A_stop_in_a_background_session_brings_it_to_the_front()
    {
        await using var h = new ServiceHarness();
        var a = await h.StartAsync(ServiceHarness.Config("A", folder: h.Dir.Uri));
        await h.StartAsync(ServiceHarness.Config("B", folder: h.Dir.Uri));
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => ReferenceEquals(h.Service.ActiveSession, a));
    }

    [Fact]
    public async Task Compound_configurations_start_all_members_and_stopAll_ends_the_rest()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/launch.json", """
            {"configurations":[{"name":"Server","type":"fake","request":"launch"},{"name":"Client","type":"fake","request":"launch"}],
             "compounds":[{"name":"Both","configurations":["Server","Client"],"stopAll":true}]}
            """);
        var compound = (await h.Configurations.GetLaunchConfigurationsAsync(null, Ct)).First(c => c.Name == "Both");
        var first = await h.Service.StartAsync(compound, Ct);
        Assert.NotNull(first);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2);
        Assert.Equal(["Client", "Server"], h.Service.Sessions.Select(s => s.Name).Order());

        await h.AdapterFor(0).SendTerminatedAsync();
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0, 8000, "stopAll must end the sibling");
    }

    [Fact]
    public async Task Compounds_with_an_unknown_member_fail_with_a_message()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/launch.json", """{"configurations":[],"compounds":[{"name":"Bad","configurations":["Missing"]}]}""");
        var compound = (await h.Configurations.GetLaunchConfigurationsAsync(null, Ct)).Single();
        Assert.Null(await h.Service.StartAsync(compound, Ct));
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("Missing"));
    }

    [Fact]
    public async Task Without_stopAll_the_other_compound_member_keeps_running()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/launch.json", """
            {"configurations":[{"name":"S","type":"fake","request":"launch"},{"name":"C","type":"fake","request":"launch"}],
             "compounds":[{"name":"Both","configurations":["S","C"]}]}
            """);
        var compound = (await h.Configurations.GetLaunchConfigurationsAsync(null, Ct)).First(c => c.Name == "Both");
        await h.Service.StartAsync(compound, Ct);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2);
        await h.AdapterFor(0).SendTerminatedAsync();
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 1);
        await Task.Delay(300, Ct);
        Assert.Single(h.Service.Sessions);
    }

    [Fact]
    public async Task StartDebugging_creates_a_child_session_with_the_parent_set()
    {
        await using var h = new ServiceHarness();
        var parent = await h.StartAsync();
        await h.AdapterFor(0).RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "launch", Configuration = new System.Text.Json.Nodes.JsonObject { ["name"] = "child", ["marker"] = 7 },
        });
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2);
        var child = h.Service.Sessions[1];
        Assert.Same(parent, child.Parent);
        Assert.Equal("fake", child.Configuration.Type); // inherited
        Assert.Equal(7, h.AdapterFor(1).RequestsOf("launch")[0].Arguments.GetProperty("marker").GetInt32());
        // ending the child leaves the parent alone
        await child.StopAsync(Ct);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 1);
        Assert.Same(parent, h.Service.Sessions[0]);
    }
}

public class DebugServiceStopLocationTests
{
    [Fact]
    public async Task A_stop_publishes_the_top_frame_as_a_0_based_stop_location()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Frames =
            [new DapStackFrame { Id = 7, Name = "Main", Source = new DapSource { Name = "Program.cs", Path = "/work/Program.cs" }, Line = 10, Column = 5 }]);
        var locations = new List<DebugStopLocation?>();
        h.Service.StopLocationChanged.Subscribe(l => { lock (locations) locations.Add(l); });
        var session = await h.StartAsync();
        Assert.Null(h.Service.StopLocation);

        await h.AdapterFor(0).SendStoppedAsync("breakpoint", threadId: 3);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);

        var loc = h.Service.StopLocation!;
        Assert.Same(session, loc.Session);
        Assert.Equal((3, 7), (loc.ThreadId, loc.FrameId));
        Assert.Equal(new Uri("file:///work/Program.cs"), loc.Uri);
        Assert.Equal((9, 4), (loc.Line, loc.Column));
        Assert.Equal("breakpoint", loc.Reason);
        Assert.Equal(DebugState.Stopped, h.Service.State);
        lock (locations) Assert.Equal(loc, Assert.Single(locations));
    }

    [Fact]
    public async Task Resuming_clears_the_stop_location_and_notifies()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        var cleared = new TaskCompletionSource();
        h.Service.StopLocationChanged.Subscribe(l => { if (l is null) cleared.TrySetResult(); });
        await session.ContinueAsync(ct: Ct);
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Null(h.Service.StopLocation);
        Assert.Equal(DebugState.Running, h.Service.State);
    }

    [Fact]
    public async Task The_first_frame_with_a_resolvable_source_is_chosen_when_the_top_frame_has_none()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Frames =
        [
            new DapStackFrame { Id = 1, Name = "[Native Frame]", Line = 0, Column = 0 },
            new DapStackFrame { Id = 2, Name = "UserCode", Source = new DapSource { Path = "/work/a.cs" }, Line = 4, Column = 1 },
        ]);
        await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        Assert.Equal(2, h.Service.StopLocation!.FrameId);
    }

    [Fact]
    public async Task Adapter_only_sources_become_debug_uris_whose_content_the_service_can_fetch()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) =>
        {
            a.Frames = [new DapStackFrame { Id = 1, Name = "dyn", Source = new DapSource { Name = "gen.cs", SourceReference = 9 }, Line = 1, Column = 1 }];
            a.SourceContents[9] = "// generated code";
        });
        await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        var uri = h.Service.StopLocation!.Uri!;
        Assert.Equal("debug", uri.Scheme);
        Assert.Equal("// generated code", await h.Service.GetSourceContentAsync(uri, Ct));
        Assert.Null(await h.Service.GetSourceContentAsync(new Uri("file:///x"), Ct));
        Assert.Null(await h.Service.GetSourceContentAsync(new Uri("debug:/nosuchsession/1/x"), Ct));
    }

    [Fact]
    public async Task SetFocusedFrame_moves_the_stop_location_to_the_selected_frame()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        Assert.Equal(100, h.Service.StopLocation!.FrameId);
        Assert.Equal(100, h.Service.FocusedFrameId);

        await ((IDebugFrameFocus)h.Service).SetFocusedFrameAsync(session.Id, 1, 101, Ct);
        Assert.Equal(101, h.Service.StopLocation!.FrameId);
        Assert.Equal((19, 0), (h.Service.StopLocation.Line, h.Service.StopLocation.Column));
        Assert.Equal(101, h.Service.FocusedFrameId);
    }

    [Fact]
    public async Task SetFocusedFrame_fails_for_ended_sessions_unknown_frames_and_after_a_resume_without_moving_the_focus()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        var before = h.Service.StopLocation;
        var focus = (IDebugFrameFocus)h.Service;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await focus.SetFocusedFrameAsync("nope", 1, 101, Ct));
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await focus.SetFocusedFrameAsync(session.Id, 1, 99999, Ct));
        Assert.Equal(before, h.Service.StopLocation);
        Assert.Equal(before!.FrameId, h.Service.FocusedFrameId);

        // A frame of the stop is dead once the debuggee resumed.
        await session.ContinueAsync(ct: Ct);
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await focus.SetFocusedFrameAsync(session.Id, 1, 101, Ct));
        Assert.Null(h.Service.FocusedFrameId);
    }

    private static IReadOnlyList<DapStackFrame> DeepStack(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new DapStackFrame { Id = 1000 + i, Name = "Level" + i, Source = new DapSource { Name = "Deep.cs", Path = "/work/Deep.cs" }, Line = 10 + i, Column = 1 })];

    [Fact]
    public async Task SetFocusedFrame_focuses_a_frame_from_a_later_call_stack_page_of_the_stopped_thread()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Frames = DeepStack(120));
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        Assert.Equal(1000, h.Service.StopLocation!.FrameId);

        // What the Call Stack window does: pages of 50, then "load more".
        await session.GetStackTraceAsync(1, 0, 50, Ct);
        var second = await session.GetStackTraceAsync(1, 50, 50, Ct);
        var deep = second[25]; // level 75, far beyond what the stop itself fetched
        await ((IDebugFrameFocus)h.Service).SetFocusedFrameAsync(session.Id, 1, deep.Id, Ct);

        Assert.Equal(deep.Id, h.Service.FocusedFrameId);
        Assert.Equal((deep.Id, deep.Line, "frame"), (h.Service.StopLocation!.FrameId, h.Service.StopLocation.Line, h.Service.StopLocation.Reason));
        Assert.Equal(new Uri("/work/Deep.cs"), h.Service.StopLocation.Uri);
        Assert.Equal(3, h.AdapterFor(0).CountOf("stackTrace")); // the stop's own fetch + the two pages: focusing did not ask again
    }

    [Fact]
    public async Task SetFocusedFrame_focuses_a_deep_frame_of_another_thread()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) =>
        {
            a.Frames = DeepStack(80);
            a.On("threads", _ => new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "main" }, new DapThread { Id = 2, Name = "worker" }] });
        });
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);

        await session.GetStackTraceAsync(2, 0, 50, Ct);
        var page = await session.GetStackTraceAsync(2, 50, 50, Ct);
        await ((IDebugFrameFocus)h.Service).SetFocusedFrameAsync(session.Id, 2, page[10].Id, Ct);

        Assert.Equal((2, page[10].Id, page[10].Line), (h.Service.StopLocation!.ThreadId, h.Service.StopLocation.FrameId, h.Service.StopLocation.Line));
        Assert.Equal(page[10].Id, h.Service.FocusedFrameId);
    }

    [Fact]
    public async Task A_stale_stack_fetch_after_a_quick_resume_does_not_resurrect_the_stop_location()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        h.AdapterFor(0).DelayAllResponses(200);
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Service.State == DebugState.Stopped);
        await session.ContinueAsync(ct: Ct); // resume while the stackTrace for the stop is still in flight
        await Task.Delay(900, Ct);
        Assert.Null(h.Service.StopLocation);
    }

    // ---- adapter `invalidated` while stopped: the published location must stay usable

    [Fact]
    public async Task After_invalidated_stacks_the_stack_is_fetched_again_and_the_location_points_at_a_live_frame()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        Assert.Equal(100, h.Service.StopLocation!.FrameId);

        // The adapter renumbers its frames (e.g. after a hot reload) and tells the client.
        h.AdapterFor(0).Frames = [new DapStackFrame { Id = 200, Name = "Main", Source = new DapSource { Name = "Program.cs", Path = "/work/Program.cs" }, Line = 12, Column = 5 }];
        await h.AdapterFor(0).SendEventAsync("invalidated", new DapInvalidatedEvent { Areas = ["stacks"] });

        await SessionHarness.Eventually(() => h.Service.StopLocation?.FrameId == 200, what: "a fresh location after the invalidation");
        var loc = h.Service.StopLocation!;
        Assert.Equal((1, 11, "breakpoint"), (loc.ThreadId, loc.Line, loc.Reason));
        Assert.Equal(200, h.Service.FocusedFrameId);
        Assert.NotEmpty(await session.GetScopesAsync(loc.FrameId, Ct)); // what Locals does with it
        Assert.Equal("<x@watch>", (await session.EvaluateAsync("x", loc.FrameId, "watch", Ct)).Result); // what Watch does with it
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await session.GetScopesAsync(100, Ct));
    }

    [Fact]
    public async Task After_an_invalidation_with_stable_frame_ids_the_same_location_is_published_again_and_usable()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        var before = h.Service.StopLocation!;
        var published = new List<DebugStopLocation?>();
        h.Service.StopLocationChanged.Subscribe(l => { lock (published) published.Add(l); });

        await h.AdapterFor(0).SendEventAsync("invalidated", new DapInvalidatedEvent()); // no areas = all (netcoredbg-style stable ids)

        await SessionHarness.Eventually(() => { lock (published) return published.LastOrDefault() is not null; }, what: "location published again");
        Assert.Equal(before, h.Service.StopLocation);
        Assert.NotEmpty(await session.GetScopesAsync(before.FrameId, Ct)); // the frame id is live again, not "Not available"
    }

    [Fact]
    public async Task After_invalidated_variables_the_focused_frame_stays_and_is_announced_after_every_invalidated_subscriber()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        await ((IDebugFrameFocus)h.Service).SetFocusedFrameAsync(session.Id, 1, 101, Ct); // the user picked the caller
        var scopes = await session.GetScopesAsync(101, Ct);
        var order = new List<string>();
        // Subscribed after the service, like the panels' focus trackers: they start a new epoch on `invalidated` and then wait for the location.
        session.Invalidated.Subscribe(_ => { lock (order) order.Add("invalidated"); });
        h.Service.StopLocationChanged.Subscribe(l => { lock (order) order.Add("location " + l?.FrameId); });

        await h.AdapterFor(0).SendEventAsync("invalidated", new DapInvalidatedEvent { Areas = ["variables"] });

        await SessionHarness.Eventually(() => { lock (order) return order.Count == 2; }, what: "location published again");
        lock (order) Assert.Equal(["invalidated", "location 101"], order);
        Assert.Equal(101, h.Service.FocusedFrameId);
        await Assert.ThrowsAsync<DebugHandleInvalidatedException>(async () => await session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct));
        Assert.NotEmpty(await session.GetScopesAsync(101, Ct));
    }

    [Fact]
    public async Task When_the_stopped_session_ends_the_stop_location_is_cleared()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        await session.StopAsync(Ct);
        await SessionHarness.Eventually(() => h.Service.StopLocation is null);
    }
}

public class DebugServiceConsoleTests
{
    [Fact]
    public async Task Output_from_sessions_is_forwarded_with_the_session_attached()
    {
        await using var h = new ServiceHarness();
        var seen = new List<DebugOutputEventArgs>();
        h.Service.Output.Subscribe(e => { lock (seen) seen.Add(e); });
        var session = await h.StartAsync();
        await h.AdapterFor(0).SendOutputAsync("hello\n", "stdout");
        await SessionHarness.Eventually(() => { lock (seen) return seen.Any(e => e.Output == "hello\n"); });
        lock (seen) Assert.Same(session, seen.First(e => e.Output == "hello\n").Session);
    }

    [Fact]
    public async Task History_keeps_late_subscribers_informed_and_is_bounded_to_5000_lines()
    {
        await using var h = new ServiceHarness();
        await h.StartAsync();
        var adapter = h.AdapterFor(0);
        for (var batch = 0; batch < 20; batch++)
        {
            await adapter.SendOutputAsync(string.Concat(Enumerable.Range(0, 1000).Select(i => $"line {batch * 1000 + i}\n")), "stdout");
            await Task.Delay(30, Ct); // distinct 16 ms windows
        }
        await SessionHarness.Eventually(() => string.Concat(h.Service.GetOutputHistory().Select(e => e.Output)).Contains("line 19999\n"));
        var history = h.Service.GetOutputHistory();
        var text = string.Concat(history.Select(e => e.Output));
        var lines = text.Count(c => c == '\n');
        Assert.InRange(lines, 1, DebugServiceLimits.MaxHistoryLines + 1000); // whole batches are evicted, so a batch of slack
        Assert.Contains("line 19999\n", text);
        Assert.DoesNotContain("line 0\n", text);
    }

    [Fact]
    public async Task One_giant_batch_is_trimmed_to_its_tail()
    {
        await using var h = new ServiceHarness();
        await h.StartAsync();
        await h.AdapterFor(0).SendOutputAsync(string.Concat(Enumerable.Range(0, 30_000).Select(i => $"row {i}\n")), "stdout");
        await SessionHarness.Eventually(() => h.Service.GetOutputHistory().Count > 0);
        await Task.Delay(100, Ct);
        var text = string.Concat(h.Service.GetOutputHistory().Select(e => e.Output));
        Assert.InRange(text.Count(c => c == '\n'), 1, 5_001);
        Assert.Contains("row 29999\n", text);
    }

    [Fact]
    public async Task Clearing_history_empties_it_and_notifies()
    {
        await using var h = new ServiceHarness();
        await h.StartAsync();
        await h.AdapterFor(0).SendOutputAsync("x\n");
        await SessionHarness.Eventually(() => h.Service.GetOutputHistory().Count > 0);
        var cleared = 0;
        ((IDebugOutputHistory)h.Service).OutputCleared.Subscribe(_ => Interlocked.Increment(ref cleared));
        ((IDebugOutputHistory)h.Service).ClearOutputHistory();
        Assert.Empty(h.Service.GetOutputHistory());
        Assert.Equal(1, cleared);
    }

    [Fact]
    public async Task Watch_expressions_can_be_added_and_removed_from_any_thread()
    {
        await using var h = new ServiceHarness();
        var a = h.Service.AddWatch("x + 1");
        var b = h.Service.AddWatch("y");
        Assert.Equal(["x + 1", "y"], h.Service.Watches.Select(w => w.Expression));
        h.Service.RemoveWatch(a);
        Assert.Same(b, Assert.Single(h.Service.Watches));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => { for (var k = 0; k < 100; k++) h.Service.RemoveWatch(h.Service.AddWatch($"w{i}-{k}")); }, Ct)));
        Assert.Single(h.Service.Watches);
    }
}

public class DebugServiceContextKeyTests
{
    [Fact]
    public async Task Context_keys_track_the_debug_state()
    {
        await using var h = new ServiceHarness();
        Assert.Equal("inactive", h.ContextKeys.Get("debugState"));
        Assert.Equal(false, h.ContextKeys.Get("inDebugMode"));
        Assert.Equal(true, h.ContextKeys.Get("debuggerAvailable"));
        Assert.Equal(false, h.ContextKeys.Get("breakpointsExist"));

        await h.StartAsync(ServiceHarness.Config(request: "attach", extra: new Dictionary<string, object?> { ["processId"] = 1 }, folder: h.Dir.Uri));
        Assert.Equal("running", h.ContextKeys.Get("debugState"));
        Assert.Equal(true, h.ContextKeys.Get("inDebugMode"));
        Assert.Equal("fake", h.ContextKeys.Get("debugType"));
        Assert.Equal(true, h.ContextKeys.Get("focusedSessionIsAttach"));

        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => (string?)h.ContextKeys.Get("debugState") == "stopped");

        h.Breakpoints.Toggle(new Uri("file:///x.cs"), 1);
        await SessionHarness.Eventually(() => h.ContextKeys.Get("breakpointsExist") is true);

        await h.Service.StopAllAsync(Ct);
        await SessionHarness.Eventually(() => (string?)h.ContextKeys.Get("debugState") == "inactive");
        Assert.Equal(false, h.ContextKeys.Get("inDebugMode"));
        Assert.Equal("", h.ContextKeys.Get("debugType"));
        Assert.Equal(false, h.ContextKeys.Get("focusedSessionIsAttach"));
    }

    [Fact]
    public async Task The_service_works_without_a_context_key_service()
    {
        await using var h = new ServiceHarness(withPlatform: false);
        Assert.NotNull(await h.Service.StartAsync(ServiceHarness.Config(folder: h.Dir.Uri), Ct));
    }
}

/// <summary>Public mirror of the service's limits so tests don't hard-code magic numbers twice.</summary>
internal static class DebugServiceLimits
{
    public const int MaxHistoryLines = CsCode.Dap.Service.DebugService.MaxHistoryLines;
}

internal sealed class FakeForeignSession : IDebugSession
{
    public string Id => "foreign";
    public string Name => "foreign";
    public DebugConfiguration Configuration => throw new NotSupportedException();
    public DebugSessionState State => DebugSessionState.Running;
    public IDebugSession? Parent => null;
    public DebugCapabilities Capabilities => new();
    public int? FocusedThreadId => null;
    public IEventSource<DebugStoppedEventArgs> Stopped => throw new NotSupportedException();
    public IEventSource<DebugContinuedEventArgs> Continued => throw new NotSupportedException();
    public IEventSource<DebugOutputEventArgs> Output => throw new NotSupportedException();
    public IEventSource<DebugThreadEventArgs> ThreadChanged => throw new NotSupportedException();
    public IEventSource<DebugSessionState> StateChanged => throw new NotSupportedException();
    public IEventSource<DebugTerminatedEventArgs> Terminated => throw new NotSupportedException();
    public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged => throw new NotSupportedException();
    public IEventSource<IDebugSession> Invalidated => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask RestartAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
