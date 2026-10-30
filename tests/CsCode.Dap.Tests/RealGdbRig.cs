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
/// The real GDB (native DAP, <c>gdb -i=dap</c>) through the real <see cref="DebugService"/>: the Rust sample built by the real cargo, the adapter
/// started by <see cref="GdbAdapterFactory"/> with the GDB quirks. GDB runs with <c>-nx</c> (<c>cscode.debug.gdbArgs</c>): the tests never read the
/// user's gdbinit files. Records every session, stop, output and termination, and every adapter / debuggee process.
/// </summary>
internal sealed class RealGdbRig : IAsyncDisposable
{
    public required ServiceHarness H { get; init; }
    public required RustSample Sample { get; init; }
    public List<IDebugSession> Started { get; } = new();
    public List<DebugStoppedEventArgs> Stops { get; } = new();
    public List<DebugOutputEventArgs> Output { get; } = new();
    public List<DebugTerminatedEventArgs> Terminated { get; } = new();
    public List<int> AdapterPids { get; } = new();
    public List<int> DebuggeePids { get; } = new();
    public List<string> Markers { get; } = new();
    public DebugService Service => H.Service;
    public Uri MainUri => new(Sample.MainRs);
    public Uri UtilUri => new(Sample.UtilRs);

    /// <summary>The debug type the rig's configurations use: <c>gdb</c>, or <c>lldb</c> for the same rig over the real CodeLLDB.</summary>
    public string Type { get; init; } = GdbAdapterFactory.PrimaryType;

    /// <param name="sample">A sample of the test's own (a folder it writes into); null: the shared, already built one.</param>
    /// <param name="type"><c>gdb</c> (GDB's native DAP) or <c>lldb</c> (CodeLLDB, found by <see cref="CodeLldbToolchain"/>; never downloaded).</param>
    public static async Task<RealGdbRig> CreateAsync(RustSample? sample = null, Action<ServiceHarness>? configure = null, string type = GdbAdapterFactory.PrimaryType)
    {
        var codeLldb = type == CodeLldbAdapterFactory.PrimaryType;
        var skip = codeLldb ? CodeLldbToolchain.SkipReason() : GdbToolchain.SkipReason();
        Assert.SkipWhen(skip is not null, skip ?? "");
        sample ??= await RustSample.SharedAsync(Ct);
        var h = new ServiceHarness(moreBuiltins: p => codeLldb ? CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p)) : GdbAdapterFactory.Builtins(new GdbAdapterFactory(p)));
        if (codeLldb) h.Settings.Set(CodeLldbAdapterFactory.SettingPath, CodeLldbToolchain.FindAdapter()!); // the adapter under test: never the download offer
        else h.Settings.Set(GdbAdapterFactory.ArgsSettingPath, new[] { "-nx" });
        configure?.Invoke(h);
        var rig = new RealGdbRig { H = h, Sample = sample, Type = type };
        h.Service.SessionStarted.Subscribe(s =>
        {
            lock (rig.Started) rig.Started.Add(s);
            if (s is DebugSession ds)
                ds.StateChanged.Subscribe(_ =>
                {
                    // The adapter exists once the session is connected (SessionStarted fires before that); a restart starts a new one.
                    if (ds.AdapterProcessId is { } pid) lock (rig.AdapterPids) if (!rig.AdapterPids.Contains(pid)) rig.AdapterPids.Add(pid);
                    if (ds.AdapterProcessMarker is { } marker) lock (rig.Markers) if (!rig.Markers.Contains(marker)) rig.Markers.Add(marker);
                    if (ds.DebuggeeProcessId is { } debuggee && !ds.IsAttach) lock (rig.DebuggeePids) if (!rig.DebuggeePids.Contains((int)debuggee)) rig.DebuggeePids.Add((int)debuggee);
                });
            s.Stopped.Subscribe(e =>
            {
                if (s is DebugSession { DebuggeeProcessId: { } debuggee, IsAttach: false }) lock (rig.DebuggeePids) if (!rig.DebuggeePids.Contains((int)debuggee)) rig.DebuggeePids.Add((int)debuggee);
                lock (rig.Stops) rig.Stops.Add(e);
            });
            s.Terminated.Subscribe(e => { lock (rig.Terminated) rig.Terminated.Add(e); });
        });
        h.Service.Output.Subscribe(e =>
        {
            lock (rig.Output)
            {
                Interlocked.Increment(ref rig._outputEvents);
                rig.Output.Add(e);
                // a flood test keeps only the newest events, so the heap it measures is the debugger's, not this list's
                if (rig.OutputLimit is { } limit && rig.Output.Count > limit) rig.Output.RemoveRange(0, rig.Output.Count - limit);
            }
        });
        return rig;
    }

    private int _outputEvents;
    /// <summary>Output events delivered so far (also those <see cref="OutputLimit"/> dropped from <see cref="Output"/>).</summary>
    public int OutputEvents => Volatile.Read(ref _outputEvents);
    /// <summary>Keep only the newest this many output events (null: all).</summary>
    public int? OutputLimit { get; set; }

    /// <summary>Turns the "Rust panics" exception filter (on by default) off or on before a session merges the filters (the choice is kept).</summary>
    public void PanicStops(bool enabled) =>
        H.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting(GdbAdapterQuirks.RustPanicFilter, "Rust panics", enabled)]);

    public DebugConfiguration Config(string name = "rust", IDictionary<string, object?>? extra = null, string[]? args = null, string? program = null, Uri? folder = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["program"] = program ?? Sample.Binary,
            ["args"] = args ?? [],
            ["cwd"] = Sample.Dir.Path,
        };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        return ServiceHarness.Config(name, Type, "launch", d, folder ?? Sample.Dir.Uri);
    }

    public static DebugConfiguration Raw(object config, Uri folder) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(config), folder);

    public async Task<DebugSession> StartAsync(DebugConfiguration? config = null) => (DebugSession)await H.StartAsync(config ?? Config());

    public int StopCount { get { lock (Stops) return Stops.Count; } }
    public DebugStoppedEventArgs LastStop { get { lock (Stops) return Stops[^1]; } }
    public string OutputText { get { lock (Output) return string.Concat(Output.Select(o => o.Output)); } }
    public string OutputOf(string category) { lock (Output) return string.Concat(Output.Where(o => o.Category == category).Select(o => o.Output)); }
    public IReadOnlyList<DebugOutputEventArgs> OutputSnapshot { get { lock (Output) return [.. Output]; } }

    /// <summary>Waits for stop number <paramref name="count"/> and the service's stop location derived from it.</summary>
    public async Task<DebugStopLocation> WaitForStopAsync(int count, int timeoutMs = 60_000)
    {
        await SessionHarness.Eventually(() => StopCount >= count, timeoutMs, $"stop #{count}; output so far: {OutputText}");
        await SessionHarness.Eventually(() => Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && ReferenceEquals(l.Session, LastStop.Session), timeoutMs, "stop location");
        return Service.StopLocation!;
    }

    public async Task WaitForEndAsync(int timeoutMs = 60_000)
    {
        // the service drops a session when it terminates, possibly before this rig's own Terminated handler ran: wait for both
        bool Ended() { lock (Started) lock (Terminated) return Service.Sessions.Count == 0 && Terminated.Count >= Started.Count; }
        try { await SessionHarness.Eventually(Ended, timeoutMs, "every session ended"); }
        catch (TimeoutException)
        {
            string stops;
            lock (Stops) stops = string.Join(", ", Stops.Select(s => $"{s.Reason}/{s.Description}"));
            var output = OutputText;
            throw new TimeoutException($"Not every session ended: {string.Join(", ", Service.Sessions.Select(s => $"{s.Name}={s.State}"))}; stops: {stops}; output: {(output.Length > 3000 ? output[^3000..] : output)}");
        }
    }

    /// <summary>GDB, the programs it launched and everything carrying its marker are gone.</summary>
    public async Task AssertNothingLeftRunningAsync(int timeoutMs = 10_000)
    {
        int[] adapters, debuggees;
        string[] markers;
        lock (AdapterPids) adapters = [.. AdapterPids];
        lock (DebuggeePids) debuggees = [.. DebuggeePids];
        lock (Markers) markers = [.. Markers];
        Assert.NotEmpty(adapters);
        foreach (var pid in adapters) await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), timeoutMs, $"gdb pid {pid} exits");
        foreach (var pid in debuggees) await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), timeoutMs, $"debuggee pid {pid} exits");
        foreach (var marker in markers)
            await SessionHarness.Eventually(() => ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Count == 0, timeoutMs,
                "no process carrying the adapter marker: " + string.Join("; ", ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Select(p => $"{p} {ProcessTable.CommandLine(p)}")));
    }

    public async Task<(IReadOnlyList<DebugStackFrame> Frames, DebugSession Session)> TopFramesAsync(int levels = 40)
    {
        var loc = Service.StopLocation ?? throw new InvalidOperationException("not stopped");
        var session = (DebugSession)loc.Session;
        return (await session.GetStackTraceAsync(loc.ThreadId, 0, levels, Ct), session);
    }

    /// <summary>The variables of the frame's scopes hinted locals/arguments (what the Locals window shows), by name.</summary>
    public async Task<Dictionary<string, DebugVariable>> LocalsAsync(int frameId, DebugSession session)
    {
        var scopes = await session.GetScopesAsync(frameId, Ct);
        TestMetrics.Report($"[{Type}] scopes: " + string.Join(" | ", scopes.Select(s => $"{s.Name} hint={s.PresentationHint} expensive={s.Expensive} named={s.NamedVariables}")));
        var all = new Dictionary<string, DebugVariable>(StringComparer.Ordinal);
        // GDB hints its scopes locals/arguments; CodeLLDB names one "Local" with no hint (and puts a "[return value]" first in it after a step out)
        foreach (var scope in scopes.Where(s => s.PresentationHint is "locals" or "arguments" || (s.PresentationHint is null && s.Name == "Local")))
            foreach (var v in await session.GetVariablesAsync(scope.VariablesReference, ct: Ct))
                all.TryAdd(v.Name, v);
        return all;
    }

    /// <summary>The nice value of a process (Linux <c>/proc/&lt;pid&gt;/stat</c> field 19).</summary>
    public static int? NiceOf(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return int.Parse(fields[16], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Disposes the service; then, as a safety net for a test that failed before it could stop everything, kills what this rig's adapters left:
    /// their trees and the processes carrying their markers. Says so in the metrics.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try { await H.DisposeAsync(); }
        finally
        {
            int[] adapters, debuggees;
            string[] markers;
            lock (AdapterPids) adapters = [.. AdapterPids];
            lock (DebuggeePids) debuggees = [.. DebuggeePids];
            lock (Markers) markers = [.. Markers];
            List<int> Left() => [.. adapters.Concat(debuggees).Where(ProcessTable.IsAlive).Concat(markers.SelectMany(m => ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", m))).Distinct()];
            var left = Left();
            var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 3 * System.Diagnostics.Stopwatch.Frequency;
            while (left.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(50);
                left = Left();
            }
            if (left.Count > 0) TestMetrics.Report($"[{Type}] rig cleanup killed what the test left running: " + string.Join("; ", left.Select(p => $"{p} {ProcessTable.CommandLine(p)}")));
            foreach (var pid in left)
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
