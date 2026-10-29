using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The real js-debug through the real <see cref="DebugService"/>: a TypeScript sample compiled by the real tsc, the adapter server started by
/// <see cref="JsDebugAdapterFactory"/>, the program running as a child session. Records every session, stop, output and termination.
/// </summary>
internal sealed class RealJsDebugRig : IAsyncDisposable
{
    public required ServiceHarness H { get; init; }
    public required JsDebugSample Sample { get; init; }
    public List<IDebugSession> Started { get; } = new();
    public List<DebugStoppedEventArgs> Stops { get; } = new();
    public List<DebugOutputEventArgs> Output { get; } = new();
    public List<DebugTerminatedEventArgs> Terminated { get; } = new();
    public List<int> ServerPids { get; } = new();
    public List<string> Markers { get; } = new();
    public DebugService Service => H.Service;
    public Uri MainUri => new(Sample.MainTs);
    public Uri UtilUri => new(Sample.UtilTs);

    public static async Task<RealJsDebugRig> CreateAsync(Action<ServiceHarness>? configure = null)
    {
        Assert.SkipWhen(JsDebugToolchain.SkipReason() is not null, JsDebugToolchain.SkipReason() ?? "");
        var sample = await JsDebugSample.CreateAsync("jsdebug-dap-", Ct);
        var h = new ServiceHarness(moreBuiltins: p =>
        {
            var factory = new JsDebugAdapterFactory(p);
            return CsCode.Dap.ServiceCollectionExtensions.JsDebugAdapters().Select(f => f(factory));
        });
        h.Settings.Set(JsDebugAdapterFactory.SettingPath, JsDebugToolchain.FindJsDebug());
        configure?.Invoke(h);
        var rig = new RealJsDebugRig { H = h, Sample = sample };
        h.Service.SessionStarted.Subscribe(s =>
        {
            lock (rig.Started) rig.Started.Add(s);
            if (s is DebugSession ds)
                ds.StateChanged.Subscribe(_ =>
                {
                    // The server exists once the session is connected (SessionStarted fires before that); a restart may start a new one.
                    if (ds.AdapterProcessId is { } pid) lock (rig.ServerPids) if (!rig.ServerPids.Contains(pid)) rig.ServerPids.Add(pid);
                    if (ds.AdapterProcessMarker is { } marker) lock (rig.Markers) if (!rig.Markers.Contains(marker)) rig.Markers.Add(marker);
                });
            s.Stopped.Subscribe(e => { lock (rig.Stops) rig.Stops.Add(e); });
            s.Terminated.Subscribe(e => { lock (rig.Terminated) rig.Terminated.Add(e); });
        });
        h.Service.Output.Subscribe(e => { lock (rig.Output) rig.Output.Add(e); });
        return rig;
    }

    public DebugConfiguration Config(string name = "ts", IDictionary<string, object?>? extra = null, string? program = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["program"] = program ?? Sample.MainJs,
            ["cwd"] = Sample.Dir.Path,
            ["outFiles"] = new[] { Sample.OutDir + "/**/*.js" },
            ["sourceMaps"] = true,
            ["skipFiles"] = new[] { "<node_internals>/**" },
            ["console"] = "internalConsole",
        };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        return ServiceHarness.Config(name, "pwa-node", "launch", d, Sample.Dir.Uri);
    }

    public static DebugConfiguration Raw(object config, Uri folder) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(config), folder);

    public async Task<DebugSession> StartAsync(DebugConfiguration? config = null) => (DebugSession)await H.StartAsync(config ?? Config());

    /// <summary>The child session js-debug asked for (the program), once it exists.</summary>
    public async Task<DebugSession> ChildOfAsync(IDebugSession parent, int index = 0, int timeoutMs = 30_000)
    {
        DebugSession? child = null;
        await SessionHarness.Eventually(() =>
        {
            lock (Started) child = Started.OfType<DebugSession>().Where(s => ReferenceEquals(s.Parent, parent)).Skip(index).FirstOrDefault();
            return child is not null;
        }, timeoutMs, $"child session #{index} of {parent.Name}");
        return child!;
    }

    public int StopCount { get { lock (Stops) return Stops.Count; } }
    public DebugStoppedEventArgs LastStop { get { lock (Stops) return Stops[^1]; } }
    public string OutputText { get { lock (Output) return string.Concat(Output.Select(o => o.Output)); } }
    public IReadOnlyList<DebugOutputEventArgs> OutputSnapshot { get { lock (Output) return [.. Output]; } }

    /// <summary>Waits for stop number <paramref name="count"/> and the service's stop location derived from it.</summary>
    public async Task<DebugStopLocation> WaitForStopAsync(int count, int timeoutMs = 30_000)
    {
        await SessionHarness.Eventually(() => StopCount >= count, timeoutMs, $"stop #{count}");
        await SessionHarness.Eventually(() => Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && ReferenceEquals(l.Session, LastStop.Session), timeoutMs, "stop location");
        return Service.StopLocation!;
    }

    public async Task WaitForEndAsync(int timeoutMs = 30_000) =>
        await SessionHarness.Eventually(() => Service.Sessions.Count == 0, timeoutMs, "every session ended: " + string.Join(", ", Service.Sessions.Select(s => $"{s.Name}={s.State}")));

    /// <summary>Every process the adapter servers of this rig started is gone: the servers, the debuggees, anything re-parented that carries their marker.</summary>
    public async Task AssertNothingLeftRunningAsync(int timeoutMs = 10_000)
    {
        int[] servers;
        string[] markers;
        lock (ServerPids) servers = [.. ServerPids];
        lock (Markers) markers = [.. Markers];
        Assert.NotEmpty(servers);
        foreach (var pid in servers) await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), timeoutMs, $"js-debug server pid {pid} exits");
        foreach (var marker in markers)
            await SessionHarness.Eventually(() => ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Count == 0, timeoutMs,
                "no process carrying the adapter marker: " + string.Join("; ", ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Select(p => $"{p} {ProcessTable.CommandLine(p)}")));
    }

    public async Task<(IReadOnlyList<DebugStackFrame> Frames, DebugSession Session)> TopFramesAsync(int levels = 30)
    {
        var loc = Service.StopLocation ?? throw new InvalidOperationException("not stopped");
        var session = (DebugSession)loc.Session;
        return (await session.GetStackTraceAsync(loc.ThreadId, 0, levels, Ct), session);
    }

    /// <summary>Variables of every non-expensive scope of the frame (js-debug: Block, Local, Closure…), innermost first.</summary>
    public async Task<IReadOnlyList<DebugVariable>> LocalsAsync(int frameId, DebugSession session)
    {
        var scopes = await session.GetScopesAsync(frameId, Ct);
        TestMetrics.Report("[js-debug] scopes: " + string.Join(" | ", scopes.Select(s => $"{s.Name} hint={s.PresentationHint} expensive={s.Expensive}")));
        var all = new List<DebugVariable>();
        foreach (var scope in scopes.Where(s => !s.Expensive && !s.Name.StartsWith("Global", StringComparison.Ordinal)))
            all.AddRange(await session.GetVariablesAsync(scope.VariablesReference, ct: Ct));
        return all;
    }

    public async ValueTask DisposeAsync()
    {
        try { await H.DisposeAsync(); }
        finally { Sample.Dispose(); }
    }
}
