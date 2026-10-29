using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// ARCHITECTURE §6 / docs/briefs/dap.md "Threading &amp; isolation": the debug adapter (stalled, flooding, hostile) must never be felt by the
/// caller. These tests assert the caller-side synchronous cost of every API, bounded memory and delivery rates under floods, that
/// cancellation works, and that all client work happens on ONE dedicated non-thread-pool lane.
/// </summary>
[Collection(SerialCollection.Name)]
public class ThreadingIsolationTests
{
    private static double Median(IEnumerable<double> xs) { var a = xs.Order().ToArray(); return a[a.Length / 2]; }
    private static double Percentile(IEnumerable<double> xs, double p) { var a = xs.Order().ToArray(); return a[Math.Min(a.Length - 1, (int)(a.Length * p))]; }

    /// <summary>Times ONLY the synchronous portion of a call (until the ValueTask is returned), in milliseconds.</summary>
    private static double SyncMs(Func<ValueTask> call, out Task completion)
    {
        var sw = Stopwatch.StartNew();
        var vt = call();
        sw.Stop();
        completion = vt.AsTask();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task<SessionHarness> StoppedSessionAsync(Action<FakeDebugAdapter>? configure = null, DebugSessionSettings? settings = null)
    {
        var h = await SessionHarness.StartAsync(configure, settings: settings);
        await h.StopAtBreakpointAsync();
        return h;
    }

    [Fact]
    public async Task Every_session_api_returns_to_the_caller_in_well_under_a_millisecond_even_when_the_adapter_never_answers()
    {
        await using var h = await StoppedSessionAsync(settings: new DebugSessionSettings { RequestTimeout = TimeSpan.FromMinutes(5) });
        var frames = await h.Session.GetStackTraceAsync(1, 0, 5, Ct);
        var scopes = await h.Session.GetScopesAsync(frames[0].Id, Ct);
        var scopeRef = scopes[0].VariablesReference;
        // From now on the adapter swallows everything.
        foreach (var c in new[] { "threads", "stackTrace", "scopes", "variables", "evaluate", "setVariable", "completions", "continue", "next", "stepIn", "stepOut", "pause", "source", "restart", "exceptionInfo" })
            h.Adapter.Stall(c);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var calls = new Dictionary<string, Func<ValueTask>>
        {
            ["threads"] = () => new ValueTask(h.Session.GetThreadsAsync(token).AsTask()),
            ["stackTrace"] = () => new ValueTask(h.Session.GetStackTraceAsync(1, 0, 5, token).AsTask()),
            ["scopes-stale-cache-miss"] = () => new ValueTask(h.Session.GetScopesAsync(frames[1].Id, token).AsTask()),
            ["variables"] = () => new ValueTask(h.Session.GetVariablesAsync(scopeRef, 0, 5, token).AsTask()),
            ["evaluate"] = () => new ValueTask(h.Session.EvaluateAsync("x", frames[0].Id, "watch", token).AsTask()),
            ["setVariable"] = () => new ValueTask(h.Session.SetVariableAsync(scopeRef, "x", "1", token).AsTask()),
            ["completions"] = () => new ValueTask(h.Session.CompletionsAsync(frames[0].Id, "x", 1, token).AsTask()),
            ["pause"] = () => h.Session.PauseAsync(1, token),
            ["custom"] = () => new ValueTask(h.Session.CustomRequestAsync("anything", null, token).AsTask()),
            ["source"] = () => new ValueTask(h.Session.GetSourceContentAsync(3, token).AsTask()),
        };
        var pending = new List<Task>();
        var report = new StringBuilder();
        foreach (var (name, call) in calls)
        {
            SyncMs(call, out var warm); pending.Add(warm); // JIT warm-up (first call compiles lambdas/closures)
            var samples = new List<double>();
            for (var i = 0; i < 25; i++) { samples.Add(SyncMs(call, out var t)); pending.Add(t); }
            var median = Median(samples);
            var p95 = Percentile(samples, 0.95);
            report.AppendLine($"{name}: median {median:F3} ms, p95 {p95:F3} ms");
            Assert.True(median < 1.0, $"{name}: median caller-side sync time {median:F3} ms (must be < 1 ms)\n{report}");
            Assert.True(p95 < 10.0, $"{name}: p95 caller-side sync time {p95:F3} ms\n{report}");
        }
        TestMetrics.Report("caller-side sync cost with a stalled adapter:\n" + report);
        cts.Cancel();
        // Every stalled request must unwind on cancellation, none may hang.
        var all = Task.WhenAll(pending.Select(async t => { try { await t; } catch (Exception) { } }));
        await all.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Resume_and_step_calls_are_enqueue_only_too()
    {
        await using var h = await StoppedSessionAsync(settings: new DebugSessionSettings { RequestTimeout = TimeSpan.FromMinutes(5) });
        h.Adapter.Stall("continue");
        h.Adapter.Stall("next");
        using var cts = new CancellationTokenSource();
        var samples = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(SyncMs(() => h.Session.StepOverAsync(1, cts.Token), out var t));
            _ = t.ContinueWith(_ => { }, TaskScheduler.Default);
            // each call after the first is a no-op resume (already Running) – the cost is still enqueue-only
        }
        Assert.True(Median(samples) < 1.0, $"median {Median(samples):F3} ms");
        cts.Cancel();
    }

    [Fact]
    public async Task Calls_from_a_thread_with_a_throwing_SynchronizationContext_never_post_to_it()
    {
        await using var h = await StoppedSessionAsync();
        var ctx = new ThrowingContext();
        Task? pending = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(ctx);
            pending = Task.WhenAll(
                h.Session.GetThreadsAsync(CancellationToken.None).AsTask(),
                h.Session.EvaluateAsync("1", null, "repl", CancellationToken.None).AsTask(),
                h.Session.GetStackTraceAsync(1, 0, 3, CancellationToken.None).AsTask());
        });
        thread.Start();
        thread.Join();
        await pending!.WaitAsync(TimeSpan.FromSeconds(5), Ct); // awaited from the test thread: only the CALLER's await may touch a context
        Assert.Equal(0, ctx.Posts);
        Assert.Equal(0, ctx.Sends);
    }

    private sealed class ThrowingContext : SynchronizationContext
    {
        public int Posts, Sends;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); throw new InvalidOperationException("library code posted to the caller's context"); }
        public override void Send(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Sends); throw new InvalidOperationException("library code sent to the caller's context"); }
    }

    [Fact]
    public async Task All_client_work_and_all_events_run_on_one_dedicated_non_pool_thread()
    {
        await using var h = await SessionHarness.StartAsync();
        var threads = new HashSet<int>();
        var names = new HashSet<string?>();
        var pool = new List<bool>();
        void Record() { lock (threads) { threads.Add(Environment.CurrentManagedThreadId); names.Add(Thread.CurrentThread.Name); pool.Add(Thread.CurrentThread.IsThreadPoolThread); } }
        h.Session.Stopped.Subscribe(_ => Record());
        h.Session.Output.Subscribe(_ => Record());
        h.Session.StateChanged.Subscribe(_ => Record());
        await h.Adapter.SendOutputAsync("x\n");
        await h.Adapter.SendStoppedAsync();
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Stopped);
        await h.Session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => { lock (threads) return threads.Count > 0 && pool.Count >= 3; });
        lock (threads)
        {
            Assert.Single(threads);
            Assert.Equal(h.Lane.ManagedThreadId, threads.Single());
            Assert.All(pool, p => Assert.False(p, "debug work must not run on the shared thread pool"));
            Assert.Equal(["test-session-lane"], names);
        }
    }

    [Fact]
    public async Task A_cpu_heavy_or_flooding_session_does_not_starve_another_session_on_the_same_lane()
    {
        var lane = new DebugLane("fairness");
        await using var flooder = await SessionHarness.StartAsync(lane: lane, id: "flood");
        await using var victim = await SessionHarness.StartAsync(lane: lane, id: "victim");

        // Pre-serialised block of 500 output events; the flooder writes it as fast as the pipe accepts.
        var one = Encoding.UTF8.GetBytes("""{"seq":1,"type":"event","event":"output","body":{"category":"stdout","output":"0123456789012345678901234567890123456789012345678901234567890123456789\n"}}""");
        var block = new MemoryStream();
        for (var i = 0; i < 500; i++)
        {
            var header = Encoding.ASCII.GetBytes($"Content-Length: {one.Length}\r\n\r\n");
            block.Write(header); block.Write(one);
        }
        var bytes = block.ToArray();
        using var stop = new CancellationTokenSource();
        var floodTask = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) await flooder.Adapter.WriteRawAsync(bytes); }
            catch (Exception) { }
        }, Ct);

        var latencies = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            var t0 = Stopwatch.GetTimestamp();
            await victim.Session.GetThreadsAsync(Ct);
            latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        stop.Cancel();
        await floodTask.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var p99 = Percentile(latencies, 0.99);
        TestMetrics.Report($"victim session while another floods the lane: {latencies.Count} round trips in 3 s, median {Median(latencies):F2} ms, p99 {p99:F1} ms");
        Assert.True(latencies.Count > 50, $"victim only completed {latencies.Count} requests in 3 s");
        Assert.True(p99 < 250, $"victim request latency p99 {p99:F1} ms while another session floods the lane (median {Median(latencies):F2} ms)");

        await flooder.DisposeAsync();
        await victim.DisposeAsync();
        lane.Dispose();
    }

    [Fact]
    public async Task A_million_line_output_flood_is_batched_and_bounded_for_the_ui()
    {
        await using var h = await SessionHarness.StartAsync();
        var batches = 0;
        long chars = 0;
        var stamps = new List<long>();
        h.Session.Output.Subscribe(e => { Interlocked.Increment(ref batches); Interlocked.Add(ref chars, e.Output.Length); lock (stamps) stamps.Add(Stopwatch.GetTimestamp()); });

        // 1,000 events x 1,000 lines x ~10 chars = 1M lines, pushed as fast as the transport takes them.
        var text = string.Concat(Enumerable.Repeat("0123456789\n", 1000));
        var body = JsonSerializer.Serialize(new { seq = 1, type = "event", @event = "output", body = new { category = "stdout", output = text } });
        var payload = Encoding.UTF8.GetBytes(body);
        var frame = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray();
        var before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) await h.Adapter.WriteRawAsync(frame);
        await h.Adapter.SendOutputAsync("END-MARKER\n");
        await SessionHarness.Eventually(() => Volatile.Read(ref batches) > 0 && h.Session.DroppedOutputChars >= 0, 30_000);
        await Task.Delay(400, Ct);
        sw.Stop();

        // UI-facing event rate is bounded by the batching window, not by the input rate.
        long first, last;
        lock (stamps) { first = stamps[0]; last = stamps[^1]; }
        var seconds = Math.Max(0.2, Stopwatch.GetElapsedTime(first, last).TotalSeconds);
        var rate = batches / seconds;
        TestMetrics.Report($"1M-line flood: {batches} UI batches ({rate:F0}/s), {chars:N0} chars delivered, {h.Session.DroppedOutputChars:N0} dropped, heap delta {(GC.GetTotalMemory(true) - before) / 1024} KB");
        Assert.True(batches < 1000, $"{batches} UI batches for 1,000 input events"); // coalesced, not 1:1 (and, past the cap, dropped)
        Assert.True(rate < 200, $"{rate:F0} output events/s reached the UI (budget: roughly one per 16 ms frame per session)");
        // Memory stays bounded: pending text is capped (512 KB), delivered text is garbage afterwards.
        var after = GC.GetTotalMemory(true);
        Assert.True(after - before < 64L * 1024 * 1024, $"managed heap grew by {(after - before) / 1024 / 1024} MB across a 1M-line flood");
        Assert.True(h.Session.DroppedOutputChars + chars >= 0);
        // and the session is still perfectly usable
        Assert.Single(await h.Session.GetThreadsAsync(Ct));
    }

    [Fact]
    public async Task The_debug_console_history_stays_bounded_under_a_flood()
    {
        await using var svc = new ServiceHarness();
        await svc.StartAsync();
        var text = string.Concat(Enumerable.Repeat("0123456789\n", 2000));
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { seq = 1, type = "event", @event = "output", body = new { category = "stdout", output = text } }));
        var frame = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray();
        // Pace the writer so distinct 16 ms windows form (a real debuggee prints over time, not in one blast).
        for (var i = 0; i < 100; i++) { await svc.AdapterFor(0).WriteRawAsync(frame); await Task.Delay(2, Ct); }
        await svc.AdapterFor(0).SendOutputAsync("LAST\n");
        await SessionHarness.Eventually(() => string.Concat(svc.Service.GetOutputHistory().Select(e => e.Output)).Contains("LAST"), 30_000);
        var history = svc.Service.GetOutputHistory();
        var lines = history.Sum(e => e.Output.Count(c => c == '\n'));
        Assert.InRange(lines, 1, 12_000); // ≤ 5,000 plus at most one whole batch of slack (64 KB ≈ 6,000 lines)
        Assert.True(history.Sum(e => e.Output.Length) <= 4 * 1024 * 1024 + 70_000);
    }

    [Fact]
    public async Task A_million_element_variables_page_is_refused_by_the_frame_cap_without_buffering_it()
    {
        await using var h = await StoppedSessionAsync(settings: new DebugSessionSettings { MaxFrameBytes = 2 * 1024 * 1024, RequestTimeout = TimeSpan.FromSeconds(20) });
        var frames = await h.Session.GetStackTraceAsync(1, 0, 3, Ct);
        var scopes = await h.Session.GetScopesAsync(frames[0].Id, Ct);

        // The adapter streams a ~60 MB "1M variables" response (valid prefix, filler body) instead of the 5 real ones.
        h.Adapter.OnAsync("variables", async req =>
        {
            var prefix = $$"""{"seq":9,"type":"response","request_seq":{{req.Seq}},"success":true,"command":"variables","body":{"variables":[""";
            var filler = new byte[64 * 1024];
            await h.Adapter.WriteLargeFrameAsync(60 * 1000 * 1000, prefix, n => n == filler.Length ? filler : new byte[n]);
            return new FakeDebugAdapter.DeferResponse();
        });

        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var sync = SyncMs(() => new ValueTask(h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct).AsTask()), out var task);
        Assert.True(sync < 1.0, $"caller-side {sync:F3} ms");
        var ex = await Assert.ThrowsAsync<DapRequestException>(async () => await task);
        Assert.Contains("limit", ex.AdapterMessage);
        var allocated = GC.GetTotalAllocatedBytes(true) - allocBefore;
        TestMetrics.Report($"60 MB / 1M-element variables response: refused in place, caller-side sync {sync:F3} ms, process allocations {allocated / 1024 / 1024} MB (fake's pooled pipe included)");
        // (the fake's bounded pipe legitimately holds up to ~16 MB of pooled chunks; a client that buffered the response would need >= 60 MB, plus parsing)
        Assert.True(allocated < 30L * 1024 * 1024, $"allocated {allocated / 1024 / 1024} MB handling a 60 MB response");
        Assert.Single(await h.Session.GetThreadsAsync(Ct)); // the connection survived (the oversized body was skipped, framing stayed in sync)
    }

    [Fact]
    public async Task A_big_but_within_cap_page_is_truncated_to_the_element_cap()
    {
        var settings = new DebugSessionSettings { MaxVariablesPerRequest = 1_000 };
        await using var h = await StoppedSessionAsync(a => a.On("variables", _ => new DapVariablesResponse
        {
            Variables = [.. Enumerable.Range(0, 30_000).Select(i => new DapVariable { Name = "v" + i, Value = i.ToString() })],
        }), settings);
        var frames = await h.Session.GetStackTraceAsync(1, 0, 3, Ct);
        var scopes = await h.Session.GetScopesAsync(frames[0].Id, Ct);
        var page = await h.Session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal(1_000, page.Count);
        Assert.Equal("v0", page[0].Name);
    }

    [Fact]
    public async Task Cancelling_a_pending_request_unblocks_the_caller_promptly_and_leaves_the_session_usable()
    {
        await using var h = await StoppedSessionAsync(settings: new DebugSessionSettings { RequestTimeout = TimeSpan.FromMinutes(5) });
        h.Adapter.Stall("evaluate");
        using var cts = new CancellationTokenSource();
        var pending = h.Session.EvaluateAsync("slow", null, "repl", cts.Token).AsTask();
        await h.Adapter.WaitForRequestAsync("evaluate");
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(sw.ElapsedMilliseconds < 500, $"cancellation took {sw.ElapsedMilliseconds} ms");
        Assert.Single(await h.Session.GetThreadsAsync(Ct));
    }

    [Fact]
    public async Task A_stalled_adapter_times_out_requests_without_blocking_and_reports_unresponsive()
    {
        await using var h = await StoppedSessionAsync(settings: new DebugSessionSettings { RequestTimeout = TimeSpan.FromMilliseconds(250) });
        h.Adapter.Stall("threads");
        var sync = SyncMs(() => new ValueTask(h.Session.GetThreadsAsync(Ct).AsTask()), out var task);
        Assert.True(sync < 1.0, $"caller-side {sync:F3} ms");
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<DapRequestTimeoutException>(async () => await task);
        Assert.InRange(sw.ElapsedMilliseconds, 200, 2000);
        Assert.True(h.Session.IsUnresponsive);
    }

    [Fact]
    public async Task Many_concurrent_requests_from_many_threads_all_complete_and_stay_correct()
    {
        await using var h = await StoppedSessionAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(async () =>
        {
            var ok = 0;
            for (var i = 0; i < 50; i++)
            {
                var r = await h.Session.EvaluateAsync($"e{t}_{i}", null, "watch", Ct);
                if (r.Result == $"<e{t}_{i}@watch>") ok++;
            }
            return ok;
        }, Ct)));
        Assert.All(results, r => Assert.Equal(50, r));
    }

    [Fact]
    public async Task The_service_start_and_stop_paths_do_not_block_the_caller()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.DelayAllResponses(100));
        // Warm-up: the first call of each generic instantiation pays one-time JIT, which is not what this test is about.
        var warm = await h.Service.StartAsync(ServiceHarness.Config("warm", folder: h.Dir.Uri), Ct);
        await h.Service.StopAllAsync(Ct);
        await h.Configurations.GetLaunchConfigurationsAsync(null, Ct);
        await h.Tasks.GetTasksAsync(null, Ct);
        Assert.NotNull(warm);
        Task<IDebugSession?> startTask = null!;
        var s1 = SyncMs(() => { startTask = h.Service.StartAsync(ServiceHarness.Config(folder: h.Dir.Uri), Ct).AsTask(); return new ValueTask(startTask); }, out _);
        Assert.True(s1 < 1.0, $"StartAsync sync portion {s1:F3} ms");
        var session = await startTask;
        Assert.NotNull(session);
        var s2 = SyncMs(() => h.Service.StopAllAsync(Ct), out var stop);
        Assert.True(s2 < 1.0, $"StopAllAsync sync portion {s2:F3} ms");
        await stop;
        var s3 = SyncMs(() => new ValueTask(h.Configurations.GetLaunchConfigurationsAsync(null, Ct).AsTask()), out var configs);
        Assert.True(s3 < 1.0, $"GetLaunchConfigurationsAsync sync portion {s3:F3} ms");
        await configs;
        var s4 = SyncMs(() => new ValueTask(h.Tasks.GetTasksAsync(null, Ct).AsTask()), out var tasks);
        Assert.True(s4 < 1.0, $"GetTasksAsync sync portion {s4:F3} ms");
        await tasks;
    }

    [Fact]
    public async Task The_lane_survives_a_throwing_work_item_and_a_throwing_event_subscriber()
    {
        var lane = new DebugLane("resilience");
        lane.Post(() => throw new InvalidOperationException("boom"));
        Assert.Equal(5, await lane.Run(() => 5, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lane.RunAsync<int>(() => throw new InvalidOperationException("inner"), Ct));
        Assert.Equal(6, await lane.Run(() => 6, Ct));
        lane.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await lane.Run(() => 1, Ct));
    }

    [Fact]
    public async Task Lane_work_resumes_on_the_lane_after_awaits_and_completes_callers_off_the_lane()
    {
        var lane = new DebugLane("resume");
        var ids = new List<int>();
        var callerThreadAfter = 0;
        await lane.RunAsync(async () =>
        {
            ids.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(20);
            ids.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            ids.Add(Environment.CurrentManagedThreadId);
        }, Ct);
        callerThreadAfter = Environment.CurrentManagedThreadId;
        Assert.Single(ids.Distinct());
        Assert.Equal(lane.ManagedThreadId, ids[0]);
        Assert.NotEqual(lane.ManagedThreadId, callerThreadAfter); // RunContinuationsAsynchronously: the caller never resumes ON the lane
        lane.Dispose();
    }
}
