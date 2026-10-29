using System.Text.Json;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

public sealed class ClientHarness : IAsyncDisposable
{
    public DebugLane Lane { get; } = new("test-lane");
    public FakeDebugAdapter Adapter { get; } = new();
    public DebugAdapterClient Client { get; }

    public ClientHarness(DebugAdapterClientOptions? options = null)
    {
        Client = new DebugAdapterClient(Adapter.Transport, Lane, options);
        Client.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Adapter.DisposeAsync();
        Lane.Dispose();
    }
}

public class ClientTests
{
    [Fact]
    public async Task Request_gets_its_own_response_even_with_requests_in_flight_out_of_order()
    {
        await using var h = new ClientHarness();
        var release = new TaskCompletionSource();
        h.Adapter.OnAsync("evaluate", async r =>
        {
            var expr = r.Args<DapEvaluateArguments>().Expression;
            if (expr == "slow") await release.Task;
            return new DapEvaluateResponse { Result = "r:" + expr };
        });
        var slow = h.Client.SendRequestAsync<DapEvaluateResponse>("evaluate", new DapEvaluateArguments { Expression = "slow" }, ct: Ct);
        var fast = await h.Client.SendRequestAsync<DapEvaluateResponse>("evaluate", new DapEvaluateArguments { Expression = "fast" }, ct: Ct);
        Assert.Equal("r:fast", fast.Result);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal("r:slow", (await slow).Result);
    }

    [Fact]
    public async Task Sequence_numbers_increase_monotonically_from_one()
    {
        await using var h = new ClientHarness();
        for (var i = 0; i < 5; i++) await h.Client.SendRequestAsync("threads", ct: Ct);
        Assert.Equal([1, 2, 3, 4, 5], h.Adapter.Received.Select(r => r.Seq));
    }

    [Fact]
    public async Task Failed_response_surfaces_adapter_message()
    {
        await using var h = new ClientHarness();
        h.Adapter.On("evaluate", _ => throw new FakeAdapterError("name 'x' is not defined"));
        var ex = await Assert.ThrowsAsync<DapRequestException>(() => h.Client.SendRequestAsync("evaluate", new DapEvaluateArguments { Expression = "x" }, ct: Ct));
        Assert.Equal("evaluate", ex.Command);
        Assert.Equal("name 'x' is not defined", ex.AdapterMessage);
    }

    [Fact]
    public async Task Failed_response_formats_structured_error_variables()
    {
        await using var h = new ClientHarness();
        // Structured error body: answer the stalled request by hand with a raw frame.
        h.Adapter.Stall("evaluate");
        var call = h.Client.SendRequestAsync("evaluate", new DapEvaluateArguments { Expression = "x" }, ct: Ct);
        await h.Adapter.WaitForRequestAsync("evaluate");
        var seq = h.Adapter.RequestsOf("evaluate")[0].Seq;
        var json = """{"seq":50,"type":"response","request_seq":SEQ,"success":false,"command":"evaluate","message":"short","body":{"error":{"id":2,"format":"Cannot read {name} at {where}","variables":{"name":"foo","where":"line 3"}}}}""".Replace("SEQ", seq.ToString());
        await h.Adapter.WriteRawAsync(System.Text.Encoding.UTF8.GetBytes($"Content-Length: {json.Length}\r\n\r\n{json}"));
        var ex = await Assert.ThrowsAsync<DapRequestException>(() => call);
        Assert.Equal("Cannot read foo at line 3", ex.AdapterMessage);
        Assert.Equal(2, ex.Error!.Id);
    }

    [Fact]
    public async Task Unknown_fields_and_unknown_enum_strings_are_tolerated()
    {
        await using var h = new ClientHarness();
        var seen = new TaskCompletionSource<DapStoppedEvent>();
        h.Client.Stopped.Subscribe(e => seen.TrySetResult(e));
        var json = """{"seq":1,"type":"event","event":"stopped","body":{"reason":"some-new-reason","threadId":3,"brandNewField":{"a":[1,2]},"allThreadsStopped":true}}""";
        await h.Adapter.WriteRawAsync(System.Text.Encoding.UTF8.GetBytes($"Content-Length: {json.Length}\r\n\r\n{json}"));
        var e = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("some-new-reason", e.Reason);
        Assert.Equal(3, e.ThreadId);
    }

    [Fact]
    public async Task Unknown_events_are_surfaced_not_dropped_silently()
    {
        await using var h = new ClientHarness();
        var seen = new TaskCompletionSource<DapUnknownEvent>();
        h.Client.UnknownEvent.Subscribe(e => seen.TrySetResult(e));
        await h.Adapter.SendEventAsync("customThing", new { value = 5 });
        var e = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("customThing", e.Name);
        Assert.Equal(5, e.Body.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Typed_events_are_dispatched_with_their_bodies()
    {
        await using var h = new ClientHarness();
        var output = new TaskCompletionSource<DapOutputEvent>();
        var exited = new TaskCompletionSource<DapExitedEvent>();
        var thread = new TaskCompletionSource<DapThreadEvent>();
        h.Client.Output.Subscribe(e => output.TrySetResult(e));
        h.Client.Exited.Subscribe(e => exited.TrySetResult(e));
        h.Client.Thread.Subscribe(e => thread.TrySetResult(e));
        await h.Adapter.SendOutputAsync("hello\n", "stdout");
        await h.Adapter.SendExitedAsync(7);
        await h.Adapter.SendEventAsync("thread", new DapThreadEvent { Reason = "started", ThreadId = 4 });
        Assert.Equal("hello\n", (await output.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Output);
        Assert.Equal(7, (await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ExitCode);
        Assert.Equal(4, (await thread.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ThreadId);
    }

    [Fact]
    public async Task Request_times_out_when_adapter_never_answers_and_fires_RequestTimedOut()
    {
        await using var h = new ClientHarness(new DebugAdapterClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(150) });
        h.Adapter.Stall("threads");
        var timedOut = new TaskCompletionSource<string>();
        h.Client.RequestTimedOut.Subscribe(c => timedOut.TrySetResult(c));
        var ex = await Assert.ThrowsAsync<DapRequestTimeoutException>(() => h.Client.SendRequestAsync("threads", ct: Ct));
        Assert.Equal("threads", ex.Command);
        Assert.Equal("threads", await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        Assert.Equal(0, h.Client.PendingRequestCount);
    }

    [Fact]
    public async Task Late_response_after_timeout_is_ignored()
    {
        await using var h = new ClientHarness(new DebugAdapterClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });
        h.Adapter.DelayAllResponses(400);
        await Assert.ThrowsAsync<DapRequestTimeoutException>(() => h.Client.SendRequestAsync("threads", ct: Ct));
        h.Adapter.DelayAllResponses(0);
        await Task.Delay(500, Ct); // late answer arrives and must not blow anything up
        var ok = await h.Client.SendRequestAsync<DapThreadsResponse>("threads", ct: Ct);
        Assert.Single(ok.Threads);
    }

    [Fact]
    public async Task Cancellation_completes_the_request_promptly_and_leaves_no_pending_entry()
    {
        await using var h = new ClientHarness();
        h.Adapter.Stall("threads");
        using var cts = new CancellationTokenSource();
        var call = h.Client.SendRequestAsync("threads", ct: cts.Token);
        await h.Adapter.WaitForRequestAsync("threads");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, h.Client.PendingRequestCount);
    }

    [Fact]
    public async Task Cancelling_before_the_lane_runs_it_skips_the_work()
    {
        await using var h = new ClientHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Client.SendRequestAsync("threads", ct: cts.Token));
        Assert.Equal(0, h.Adapter.CountOf("threads"));
    }

    [Fact]
    public async Task Cancel_request_is_sent_when_the_adapter_supports_it()
    {
        await using var h = new ClientHarness();
        h.Client.SendCancelRequests = true;
        h.Adapter.Stall("evaluate");
        using var cts = new CancellationTokenSource();
        var call = h.Client.SendRequestAsync("evaluate", new DapEvaluateArguments { Expression = "x" }, cts.Token);
        await h.Adapter.WaitForRequestAsync("evaluate");
        var seq = h.Adapter.RequestsOf("evaluate")[0].Seq;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await h.Adapter.WaitForRequestAsync("cancel");
        Assert.Equal(seq, h.Adapter.RequestsOf("cancel")[0].Args<DapCancelArguments>().RequestId);
    }

    [Fact]
    public async Task Adapter_crash_fails_all_pending_requests_and_closes_the_client()
    {
        await using var h = new ClientHarness();
        h.Adapter.Stall("threads");
        var closed = new TaskCompletionSource<string>();
        h.Client.ConnectionClosed.Subscribe(r => closed.TrySetResult(r));
        var call = h.Client.SendRequestAsync("threads", ct: Ct);
        await h.Adapter.WaitForRequestAsync("threads");
        h.Adapter.Crash();
        await Assert.ThrowsAsync<DapTransportClosedException>(() => call);
        await h.Client.Closed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(h.Client.IsClosed);
        Assert.NotNull(await closed.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        await Assert.ThrowsAsync<DapTransportClosedException>(() => h.Client.SendRequestAsync("threads", ct: Ct));
    }

    [Fact]
    public async Task Malformed_stream_closes_the_connection()
    {
        await using var h = new ClientHarness();
        await h.Adapter.WriteRawAsync("garbage without any content length header\r\n\r\n"u8.ToArray());
        await h.Client.Closed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Contains("malformed", h.Client.CloseReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oversized_response_fails_only_its_request_and_the_client_keeps_working()
    {
        await using var h = new ClientHarness(new DebugAdapterClientOptions { MaxFrameBytes = 200_000 });
        h.Adapter.Stall("variables");
        var call = h.Client.SendRequestAsync("variables", new DapVariablesArguments { VariablesReference = 1 }, ct: Ct);
        await h.Adapter.WaitForRequestAsync("variables");
        var seq = h.Adapter.RequestsOf("variables")[0].Seq;
        var prefix = """{"seq":9,"type":"response","request_seq":SEQ,"success":true,"command":"variables","body":{"variables":[""".Replace("SEQ", seq.ToString());
        await h.Adapter.WriteLargeFrameAsync(3_000_000, prefix, n => Enumerable.Repeat((byte)' ', n).ToArray());
        var ex = await Assert.ThrowsAsync<DapRequestException>(() => call);
        Assert.Contains("limit", ex.AdapterMessage);
        Assert.Equal(1, h.Client.OversizedFrames);
        var ok = await h.Client.SendRequestAsync<DapThreadsResponse>("threads", ct: Ct);
        Assert.Single(ok.Threads);
    }

    [Fact]
    public async Task Reverse_request_is_answered_with_handler_body()
    {
        await using var h = new ClientHarness();
        h.Client.SetReverseRequestHandler("runInTerminal", (args, ct) =>
            Task.FromResult<object?>(new DapRunInTerminalResponse { ProcessId = 4242 }));
        var body = await h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments { Cwd = "/tmp", Args = ["echo"] });
        Assert.Equal(4242, body.GetProperty("processId").GetInt32());
    }

    [Fact]
    public async Task Unhandled_reverse_request_gets_an_error_response()
    {
        await using var h = new ClientHarness();
        var ex = await Assert.ThrowsAsync<FakeAdapterError>(() => h.Adapter.RequestFromAdapterAsync("nonsense", null));
        Assert.Contains("Unsupported", ex.Message);
    }

    [Fact]
    public async Task Failing_reverse_handler_reports_failure_to_the_adapter()
    {
        await using var h = new ClientHarness();
        h.Client.SetReverseRequestHandler("runInTerminal", (_, _) => throw new InvalidOperationException("no shell"));
        var ex = await Assert.ThrowsAsync<FakeAdapterError>(() => h.Adapter.RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArguments { Cwd = "/" }));
        Assert.Equal("no shell", ex.Message);
    }

    [Fact]
    public async Task Dispose_closes_transport_and_fails_pending()
    {
        var h = new ClientHarness();
        h.Adapter.Stall("threads");
        var call = h.Client.SendRequestAsync("threads", ct: Ct);
        await h.Adapter.WaitForRequestAsync("threads");
        await h.Client.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => call);
        Assert.True(h.Adapter.Transport.Closed);
        await h.Adapter.DisposeAsync();
        h.Lane.Dispose();
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var h = new ClientHarness();
        await h.Client.DisposeAsync();
        await h.Client.DisposeAsync();
        await h.Adapter.DisposeAsync();
        h.Lane.Dispose();
    }

    [Fact]
    public async Task Event_subscribers_that_throw_do_not_break_the_read_loop()
    {
        await using var h = new ClientHarness();
        var got = new TaskCompletionSource<DapOutputEvent>();
        h.Client.Output.Subscribe(_ => throw new InvalidOperationException("bad subscriber"));
        h.Client.Output.Subscribe(e => got.TrySetResult(e));
        await h.Adapter.SendOutputAsync("x");
        Assert.Equal("x", (await got.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Output);
        await h.Client.SendRequestAsync("threads", ct: Ct);
    }

    [Fact]
    public async Task Events_arriving_before_and_after_a_response_keep_their_order()
    {
        await using var h = new ClientHarness();
        var order = new List<string>();
        h.Client.Output.Subscribe(e => { lock (order) order.Add("event:" + e.Output); });
        h.Adapter.OnAsync("threads", async _ =>
        {
            await h.Adapter.SendOutputAsync("before");
            return new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "t" }] };
        });
        await h.Client.SendRequestAsync("threads", ct: Ct);
        lock (order) order.Add("response");
        await h.Adapter.SendOutputAsync("after");
        await Task.Delay(100, Ct);
        lock (order) Assert.Equal(["event:before", "response", "event:after"], order);
    }
}
