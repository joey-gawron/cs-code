using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Client;
using CsCode.Dap.Session;

namespace CsCode.Dap.Tests.Fakes;

public static class Cfg
{
    public static DebugConfiguration Make(string request = "launch", string type = "fake", string name = "Test", bool noDebug = false, IDictionary<string, object?>? extra = null)
    {
        var d = new Dictionary<string, object?> { ["type"] = type, ["request"] = request, ["name"] = name };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(d));
        return noDebug ? cfg with { NoDebug = true } : cfg;
    }
}

/// <summary>Spins up lane + fake adapter + breakpoint manager + a started session, and records every session event.</summary>
public sealed class SessionHarness : IAsyncDisposable
{
    private readonly bool _ownsLane;
    public DebugLane Lane { get; }
    public FakeDebugAdapter Adapter { get; }
    public FakeStateStorage Storage { get; } = new();
    public BreakpointManager Breakpoints { get; }
    public DebugSession Session { get; private set; } = null!;
    public DebugSessionOptions Options { get; private set; } = null!;

    public List<DebugStoppedEventArgs> StoppedEvents { get; } = new();
    public List<DebugContinuedEventArgs> ContinuedEvents { get; } = new();
    public List<DebugOutputEventArgs> OutputEvents { get; } = new();
    public List<DebugSessionState> States { get; } = new();
    public List<DebugTerminatedEventArgs> TerminatedEvents { get; } = new();
    public List<DebugBreakpointStatus> StatusEvents { get; } = new();
    public List<DebugThreadEventArgs> ThreadEvents { get; } = new();
    public int InvalidatedCount;

    public SessionHarness(DebugLane? lane = null, FakeDebugAdapter? adapter = null)
    {
        _ownsLane = lane is null;
        Lane = lane ?? new DebugLane("test-session-lane");
        Adapter = adapter ?? new FakeDebugAdapter();
        Breakpoints = new BreakpointManager(Storage);
    }

    public static async Task<SessionHarness> StartAsync(
        Action<FakeDebugAdapter>? configure = null,
        DebugConfiguration? config = null,
        DebugSessionSettings? settings = null,
        StartChildSession? startChild = null,
        Action<SessionHarness>? beforeStart = null,
        DebugLane? lane = null)
    {
        var h = new SessionHarness(lane);
        configure?.Invoke(h.Adapter);
        h.Create(config ?? Cfg.Make(), settings, startChild);
        beforeStart?.Invoke(h);
        await h.Session.StartAsync(Ct);
        return h;
    }

    public DebugSession Create(DebugConfiguration config, DebugSessionSettings? settings = null, StartChildSession? startChild = null, IDebugSession? parent = null, string id = "s1")
    {
        Options = new DebugSessionOptions(id, config, _ => ValueTask.FromResult<CsCode.Dap.Transport.IDapTransport>(Adapter.Transport), Breakpoints, Lane)
        {
            Settings = settings ?? new DebugSessionSettings { RequestTimeout = TimeSpan.FromSeconds(5), StopTimeout = TimeSpan.FromSeconds(2) },
            StartChild = startChild,
            Parent = parent,
        };
        Session = new DebugSession(Options);
        Session.Stopped.Subscribe(e => { lock (StoppedEvents) StoppedEvents.Add(e); });
        Session.Continued.Subscribe(e => { lock (ContinuedEvents) ContinuedEvents.Add(e); });
        Session.Output.Subscribe(e => { lock (OutputEvents) OutputEvents.Add(e); });
        Session.StateChanged.Subscribe(e => { lock (States) States.Add(e); });
        Session.Terminated.Subscribe(e => { lock (TerminatedEvents) TerminatedEvents.Add(e); });
        Session.BreakpointStatusChanged.Subscribe(e => { lock (StatusEvents) StatusEvents.Add(e); });
        Session.ThreadChanged.Subscribe(e => { lock (ThreadEvents) ThreadEvents.Add(e); });
        Session.Invalidated.Subscribe(_ => Interlocked.Increment(ref InvalidatedCount));
        return Session;
    }

    public async Task StopAtBreakpointAsync(int threadId = 1)
    {
        await Adapter.SendStoppedAsync("breakpoint", threadId);
        await Eventually(() => Session.State == DebugSessionState.Stopped);
    }

    /// <summary>Stops, then loads the stack and scopes so frame/variable handles are valid (like the UI does).</summary>
    public async Task<(IReadOnlyList<DebugStackFrame> Frames, IReadOnlyList<DebugScope> Scopes)> StopAndLoadAsync()
    {
        await StopAtBreakpointAsync();
        var frames = await Session.GetStackTraceAsync(1, 0, 20, Ct);
        var scopes = await Session.GetScopesAsync(frames[0].Id, Ct);
        return (frames, scopes);
    }

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time" + (what is null ? "" : ": " + what));
            await Task.Delay(5, Ct);
        }
    }

    public T[] Snapshot<T>(List<T> list) { lock (list) return [.. list]; }

    public async ValueTask DisposeAsync()
    {
        if (Session is not null) await Session.DisposeAsync();
        await Breakpoints.DisposeAsync();
        await Adapter.DisposeAsync();
        if (_ownsLane) Lane.Dispose();
    }
}
