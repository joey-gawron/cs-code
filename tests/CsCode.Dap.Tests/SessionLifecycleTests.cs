using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

public class SessionTerminationTests
{
    [Fact]
    public async Task Exited_then_terminated_events_end_the_session_with_the_exit_code_and_a_disconnect()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendOutputAsync("bye\n", "stdout");
        await h.Adapter.SendExitedAsync(3);
        await h.Adapter.SendTerminatedAsync();
        await h.WaitForTerminatedEventAsync();
        var t = Assert.Single(h.Snapshot(h.TerminatedEvents));
        Assert.Equal(3, t.ExitCode);
        Assert.Equal(3, h.Session.ExitCode);
        await h.Adapter.WaitForRequestAsync("disconnect"); // spec: client disconnects after `terminated`
        // output printed before the end is delivered BEFORE the termination
        Assert.Contains(h.Snapshot(h.OutputEvents), o => o.Output == "bye\n");
    }

    [Fact]
    public async Task Terminated_is_reported_exactly_once_even_if_events_repeat()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendTerminatedAsync();
        await h.Adapter.SendTerminatedAsync();
        await h.WaitForTerminatedEventAsync();
        await Task.Delay(100, Ct);
        Assert.Single(h.Snapshot(h.TerminatedEvents));
    }

    [Fact]
    public async Task Adapter_crash_terminates_the_session_with_a_reason()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.Crash();
        await h.WaitForTerminatedEventAsync();
        var t = Assert.Single(h.Snapshot(h.TerminatedEvents));
        Assert.Contains("closed", t.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Requests_after_termination_fail_clearly()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.Crash();
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Terminated);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Session.GetThreadsAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Session.ContinueAsync(ct: Ct));
    }

    [Fact]
    public async Task Pending_requests_fail_when_the_adapter_crashes()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.Stall("threads");
        var call = h.Session.GetThreadsAsync(Ct).AsTask();
        await h.Adapter.WaitForRequestAsync("threads");
        h.Adapter.Crash();
        await Assert.ThrowsAnyAsync<IOException>(() => call);
    }

    [Fact]
    public async Task Stop_prefers_terminate_when_supported_then_disconnects()
    {
        await using var h = await SessionHarness.StartAsync(a => a.Capabilities["supportsTerminateRequest"] = true);
        await h.Session.StopAsync(Ct);
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
        var cmds = h.Adapter.Commands;
        Assert.True(cmds.ToList().IndexOf("terminate") < cmds.ToList().IndexOf("disconnect"), string.Join(",", cmds));
        Assert.Single(h.Snapshot(h.TerminatedEvents));
    }

    [Fact]
    public async Task Stop_without_terminate_support_disconnects_and_terminates_the_debuggee_for_launch()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Session.StopAsync(Ct);
        Assert.Equal(0, h.Adapter.CountOf("terminate"));
        var d = h.Adapter.RequestsOf("disconnect")[0].Args<DapDisconnectArguments>();
        Assert.True(d.TerminateDebuggee);
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
    }

    [Fact]
    public async Task Stop_of_an_attach_session_detaches_instead_of_killing_the_debuggee()
    {
        await using var h = await SessionHarness.StartAsync(a => a.Capabilities["supportsTerminateRequest"] = true,
            config: Cfg.Make("attach", extra: new Dictionary<string, object?> { ["processId"] = 5 }));
        await h.Session.StopAsync(Ct);
        Assert.Equal(0, h.Adapter.CountOf("terminate")); // `terminate` would end the debuggee
        Assert.False(h.Adapter.RequestsOf("disconnect")[0].Args<DapDisconnectArguments>().TerminateDebuggee);
    }

    [Fact]
    public async Task Stop_hard_kills_when_the_adapter_ignores_every_request()
    {
        var settings = new DebugSessionSettings { RequestTimeout = TimeSpan.FromMilliseconds(300), StopTimeout = TimeSpan.FromMilliseconds(300) };
        await using var h = await SessionHarness.StartAsync(a =>
        {
            a.Capabilities["supportsTerminateRequest"] = true;
            a.Stall("terminate");
            a.Stall("disconnect");
        }, settings: settings);
        var sw = Stopwatch.StartNew();
        await h.Session.StopAsync(Ct);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"stop took {sw.Elapsed}");
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
        Assert.True(h.Adapter.Transport.Completion.IsCompleted); // connection torn down
    }

    [Fact]
    public async Task Stop_is_idempotent()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Session.StopAsync(Ct);
        await h.Session.StopAsync(Ct);
        Assert.Single(h.Snapshot(h.TerminatedEvents));
    }

    [Fact]
    public async Task Dispose_ends_a_running_session_and_forbids_further_use()
    {
        var h = await SessionHarness.StartAsync();
        await h.Session.DisposeAsync();
        Assert.Equal(DebugSessionState.Terminated, h.Session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await h.Session.GetThreadsAsync(Ct));
        await h.Session.DisposeAsync(); // idempotent
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Terminated_session_clears_its_breakpoint_statuses()
    {
        var h = new SessionHarness();
        h.Breakpoints.Toggle(new Uri("file:///work/a.cs"), 3);
        h.Create(Cfg.Make());
        await using var _ = h;
        await h.Session.StartAsync(Ct);
        Assert.True(h.Breakpoints.StatusCount > 0);
        h.Adapter.Crash();
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Terminated);
        await SessionHarness.Eventually(() => h.Breakpoints.StatusCount == 0);
    }
}

public class SessionRestartTests
{
    [Fact]
    public async Task Restart_uses_the_adapters_restart_request_when_supported()
    {
        await using var h = await SessionHarness.StartAsync(a => a.Capabilities["supportsRestartRequest"] = true);
        await h.StopAtBreakpointAsync();
        await h.Session.RestartAsync(Ct);
        Assert.Equal(1, h.Adapter.CountOf("restart"));
        Assert.Equal(1, h.Adapter.CountOf("initialize")); // no second handshake
        Assert.Equal(DebugSessionState.Running, h.Session.State);
        Assert.Empty(h.Snapshot(h.TerminatedEvents));
    }

    /// <summary>Hands out the harness adapter first, then a fresh fake per call (like spawning the adapter process again).</summary>
    private sealed class AdapterSequence(FakeDebugAdapter first)
    {
        public List<FakeDebugAdapter> All { get; } = new();

        public ValueTask<CsCode.Dap.Transport.IDapTransport> Next(CancellationToken ct)
        {
            var a = All.Count == 0 ? first : new FakeDebugAdapter();
            lock (All) All.Add(a);
            return ValueTask.FromResult<CsCode.Dap.Transport.IDapTransport>(a.Transport);
        }
    }

    [Fact]
    public async Task Restart_without_adapter_support_relaunches_with_the_same_configuration()
    {
        var cfg = Cfg.Make(extra: new Dictionary<string, object?> { ["program"] = "/w/app.dll", ["args"] = new[] { "a", "b" } });
        var h = new SessionHarness();
        var seq = new AdapterSequence(h.Adapter);
        var session = new DebugSession(new DebugSessionOptions("s1", cfg, seq.Next, h.Breakpoints, h.Lane) { Settings = new DebugSessionSettings { RequestTimeout = TimeSpan.FromSeconds(5) } });
        var states = new List<DebugSessionState>();
        var terminated = 0;
        session.StateChanged.Subscribe(s => { lock (states) states.Add(s); });
        session.Terminated.Subscribe(_ => Interlocked.Increment(ref terminated));
        await session.StartAsync(Ct);

        await session.RestartAsync(Ct);

        Assert.Equal(2, seq.All.Count);
        Assert.Equal(DebugSessionState.Running, session.State);
        Assert.Equal(0, terminated); // the restart is NOT the end of the session
        var second = seq.All[1].RequestsOf("launch")[0].Arguments;
        Assert.Equal("/w/app.dll", second.GetProperty("program").GetString());
        Assert.Equal(["a", "b"], second.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        lock (states) Assert.Equal([DebugSessionState.Running, DebugSessionState.Initializing, DebugSessionState.Running], states);
        await session.DisposeAsync();
        foreach (var a in seq.All.Skip(1)) await a.DisposeAsync();
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Terminated_event_asking_for_restart_relaunches_instead_of_ending()
    {
        var h = new SessionHarness();
        var seq = new AdapterSequence(h.Adapter);
        var session = new DebugSession(new DebugSessionOptions("s1", Cfg.Make(), seq.Next, h.Breakpoints, h.Lane) { Settings = new DebugSessionSettings { RequestTimeout = TimeSpan.FromSeconds(5) } });
        var terminated = 0;
        session.Terminated.Subscribe(_ => Interlocked.Increment(ref terminated));
        await session.StartAsync(Ct);

        await h.Adapter.SendEventAsync("terminated", new { restart = new { token = "abc" } });
        await SessionHarness.Eventually(() => { lock (seq.All) return seq.All.Count == 2 && seq.All[1].CountOf("configurationDone") == 1; });
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running);
        Assert.Equal(0, terminated);
        Assert.Equal("abc", seq.All[1].RequestsOf("launch")[0].Arguments.GetProperty("__restart").GetProperty("token").GetString());
        await session.DisposeAsync();
        foreach (var a in seq.All.Skip(1)) await a.DisposeAsync();
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Restart_of_a_finished_session_is_rejected()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.Crash();
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Terminated);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Session.RestartAsync(Ct));
    }
}

public class SessionReverseRequestTests
{
    [Fact]
    public async Task RunInTerminal_spawns_the_debuggee_wires_its_output_and_returns_the_pid()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        await using var h = await SessionHarness.StartAsync();
        var body = await h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments
        {
            Kind = "integrated", Cwd = "/tmp", Args = ["/bin/sh", "-c", "echo out-line; echo err-line 1>&2; echo \"v=$CSCODE_TEST\""],
            Env = new Dictionary<string, string?> { ["CSCODE_TEST"] = "hello" },
        });
        var pid = body.GetProperty("processId").GetInt32();
        Assert.True(pid > 0);
        await SessionHarness.Eventually(() =>
        {
            var all = string.Concat(h.Snapshot(h.OutputEvents).Select(o => o.Category + ":" + o.Output));
            return all.Contains("stdout:out-line") && all.Contains("stderr:err-line") && all.Contains("v=hello");
        });
    }

    [Fact]
    public async Task RunInTerminal_env_null_removes_a_variable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        Environment.SetEnvironmentVariable("CSCODE_TEST_REMOVE_ME", "present");
        try
        {
            await using var h = await SessionHarness.StartAsync();
            await h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments
            {
                Cwd = "/tmp", Args = ["/bin/sh", "-c", "echo \"[$CSCODE_TEST_REMOVE_ME]\""],
                Env = new Dictionary<string, string?> { ["CSCODE_TEST_REMOVE_ME"] = null },
            });
            await SessionHarness.Eventually(() => h.Snapshot(h.OutputEvents).Any(o => o.Output.Contains("[]")));
        }
        finally { Environment.SetEnvironmentVariable("CSCODE_TEST_REMOVE_ME", null); }
    }

    [Fact]
    public async Task RunInTerminal_debuggee_gets_EOF_on_stdin_and_is_killed_with_the_session()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        var h = await SessionHarness.StartAsync();
        var body = await h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments { Cwd = "/tmp", Args = ["/bin/sh", "-c", "sleep 300"] });
        var pid = body.GetProperty("processId").GetInt32();
        Assert.True(ProcessAlive(pid));
        await h.DisposeAsync();
        await SessionHarness.Eventually(() => !ProcessAlive(pid), 5000, "debuggee must not outlive its session");
    }

    [Fact]
    public async Task RunInTerminal_with_a_missing_program_fails_the_reverse_request()
    {
        await using var h = await SessionHarness.StartAsync();
        var ex = await Assert.ThrowsAsync<FakeAdapterError>(() => h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments { Cwd = "/tmp", Args = ["/definitely/not/here"] }));
        Assert.False(string.IsNullOrEmpty(ex.Message));
    }

    [Fact]
    public async Task RunInTerminal_without_a_command_fails()
    {
        await using var h = await SessionHarness.StartAsync();
        await Assert.ThrowsAsync<FakeAdapterError>(() => h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments { Cwd = "/tmp", Args = [] }));
    }

    [Fact]
    public async Task StartDebugging_asks_the_host_for_a_child_session_with_inherited_type()
    {
        var seen = new TaskCompletionSource<(string Request, JsonElementHolder Config)>();
        await using var h = await SessionHarness.StartAsync(startChild: (parent, request, config, ct) =>
        {
            seen.TrySetResult((request, new JsonElementHolder(config.ToJsonString())));
            return Task.FromResult<IDebugSession?>(parent);
        });
        await h.Adapter.RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "attach",
            Configuration = new System.Text.Json.Nodes.JsonObject { ["__pendingTargetId"] = "t1" },
        });
        var (request, cfg) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("attach", request);
        using var doc = JsonDocument.Parse(cfg.Json);
        Assert.Equal("fake", doc.RootElement.GetProperty("type").GetString()); // inherited from the parent
        Assert.Equal("t1", doc.RootElement.GetProperty("__pendingTargetId").GetString());
        Assert.Equal("attach", doc.RootElement.GetProperty("request").GetString());
    }

    [Fact]
    public async Task StartDebugging_failure_is_reported_to_the_adapter()
    {
        await using var h = await SessionHarness.StartAsync(startChild: (_, _, _, _) => throw new InvalidOperationException("no child"));
        var ex = await Assert.ThrowsAsync<FakeAdapterError>(() => h.Adapter.RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments { Configuration = new() }));
        Assert.Equal("no child", ex.Message);
    }

    [Fact]
    public async Task StartDebugging_is_unsupported_without_a_starter()
    {
        await using var h = await SessionHarness.StartAsync();
        await Assert.ThrowsAsync<FakeAdapterError>(() => h.Adapter.RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments { Configuration = new() }));
    }

    private sealed record JsonElementHolder(string Json);

    internal static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            // A killed-but-unreaped child shows as a zombie (state Z): not alive for our purposes.
            var stat = $"/proc/{pid}/stat";
            if (File.Exists(stat))
            {
                var text = File.ReadAllText(stat);
                var close = text.LastIndexOf(')');
                if (close > 0 && close + 2 < text.Length && text[close + 2] == 'Z') return false;
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return true; }
    }
}

public class SessionOutputTests
{
    [Fact]
    public async Task Output_categories_are_preserved_and_telemetry_is_dropped()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendOutputAsync("a", "console");
        await h.Adapter.SendOutputAsync("b", "stderr");
        await h.Adapter.SendOutputAsync("secret", "telemetry");
        await SessionHarness.Eventually(() => h.Snapshot(h.OutputEvents).Select(o => o.Output).Contains("b"));
        var all = h.Snapshot(h.OutputEvents);
        Assert.Contains(all, o => o.Category == "console" && o.Output == "a");
        Assert.Contains(all, o => o.Category == "stderr" && o.Output == "b");
        Assert.DoesNotContain(all, o => o.Output.Contains("secret"));
    }

    [Fact]
    public async Task Output_without_a_category_is_console()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendEventAsync("output", new { output = "plain" });
        await SessionHarness.Eventually(() => h.Snapshot(h.OutputEvents).Any(o => o.Output == "plain"));
        Assert.Equal("console", h.Snapshot(h.OutputEvents).First(o => o.Output == "plain").Category);
    }

    [Fact]
    public async Task Rapid_output_is_coalesced_into_few_batches_without_losing_text()
    {
        await using var h = await SessionHarness.StartAsync();
        for (var i = 0; i < 500; i++) await h.Adapter.SendOutputAsync($"line {i}\n", "stdout");
        await SessionHarness.Eventually(() => string.Concat(h.Snapshot(h.OutputEvents).Select(o => o.Output)).Contains("line 499\n"));
        var events = h.Snapshot(h.OutputEvents);
        Assert.True(events.Length < 100, $"{events.Length} batches for 500 lines");
        var text = string.Concat(events.Select(o => o.Output));
        for (var i = 0; i < 500; i += 50) Assert.Contains($"line {i}\n", text);
        Assert.True(text.IndexOf("line 0\n", StringComparison.Ordinal) < text.IndexOf("line 499\n", StringComparison.Ordinal)); // order kept
    }

    [Fact]
    public async Task Output_with_a_source_location_is_kept_separate_and_converted_to_0_based()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.Adapter.SendEventAsync("output", new DapOutputEvent { Category = "stdout", Output = "at-loc\n", Source = new DapSource { Path = "/w/a.cs", Name = "a.cs" }, Line = 12 });
        await SessionHarness.Eventually(() => h.Snapshot(h.OutputEvents).Any(o => o.Output == "at-loc\n"));
        var e = h.Snapshot(h.OutputEvents).First(o => o.Output == "at-loc\n");
        Assert.Equal(11, e.Line);
        Assert.Equal("/w/a.cs", e.Source!.Path);
    }

    [Fact]
    public async Task Output_printed_before_a_stop_is_delivered_before_the_stop_event()
    {
        await using var h = await SessionHarness.StartAsync();
        var order = new List<string>();
        h.Session.Output.Subscribe(o => { lock (order) order.Add("out:" + o.Output.Trim()); });
        h.Session.Stopped.Subscribe(_ => { lock (order) order.Add("stopped"); });
        await h.Adapter.SendOutputAsync("before-stop\n", "stdout");
        await h.Adapter.SendStoppedAsync();
        await SessionHarness.Eventually(() => { lock (order) return order.Contains("stopped"); });
        lock (order) Assert.Equal(["out:before-stop", "stopped"], order);
    }
}

public class SessionHealthTests
{
    [Fact]
    public async Task A_hung_adapter_makes_the_session_report_Unresponsive_without_blocking_callers()
    {
        var settings = new DebugSessionSettings { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        await using var h = await SessionHarness.StartAsync(settings: settings);
        var flips = new List<bool>();
        h.Session.UnresponsiveChanged.Subscribe(v => { lock (flips) flips.Add(v); });
        h.Adapter.Stall("threads");

        var sw = Stopwatch.StartNew();
        var pending = h.Session.GetThreadsAsync(Ct).AsTask();
        Assert.True(sw.ElapsedMilliseconds < 50, "the call itself must return immediately"); // no sync wait on the adapter
        await Assert.ThrowsAsync<DapRequestTimeoutException>(() => pending);
        Assert.True(h.Session.IsUnresponsive);

        h.Adapter.Unstall("threads");
        await h.Session.GetThreadsAsync(Ct); // a healthy answer clears the flag
        Assert.False(h.Session.IsUnresponsive);
        lock (flips) Assert.Equal([true, false], flips);
    }

    [Fact]
    public async Task Any_event_from_the_adapter_clears_Unresponsive()
    {
        var settings = new DebugSessionSettings { RequestTimeout = TimeSpan.FromMilliseconds(150) };
        await using var h = await SessionHarness.StartAsync(settings: settings);
        h.Adapter.Stall("threads");
        await Assert.ThrowsAsync<DapRequestTimeoutException>(async () => await h.Session.GetThreadsAsync(Ct));
        Assert.True(h.Session.IsUnresponsive);
        await h.Adapter.SendOutputAsync("alive");
        await SessionHarness.Eventually(() => !h.Session.IsUnresponsive);
    }

    [Fact]
    public async Task Cancellation_of_a_slow_request_returns_promptly()
    {
        await using var h = await SessionHarness.StartAsync();
        h.Adapter.Stall("threads");
        using var cts = new CancellationTokenSource();
        var pending = h.Session.GetThreadsAsync(cts.Token).AsTask();
        await h.Adapter.WaitForRequestAsync("threads");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
