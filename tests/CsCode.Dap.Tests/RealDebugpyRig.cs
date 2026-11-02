using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Python;
using CsCode.Dap.Adapters;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>Where the debugpy suites find their tools (tools/fetch-debugpy.sh): the tool venv (with debugpy and pytest) and the bundled copy.</summary>
internal static class DebugpyToolchain
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string ToolsDir => Path.Combine(RepoRoot, ".scratch", "tools");
    public static string VenvPython => Path.Combine(ToolsDir, "python", "venv", "bin", "python");
    public static string BundledAdapter => DebugpyInstaller.AdapterPath(ToolsDir);

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    public static string? SkipReason()
    {
        if (OperatingSystem.IsWindows()) return "the debugpy suite is written for unix";
        if (!File.Exists(VenvPython)) return "the tool venv is missing: run tools/fetch-debugpy.sh";
        if (!Directory.EnumerateDirectories(Path.Combine(ToolsDir, "python", "venv", "lib")).Any(d => Directory.Exists(Path.Combine(d, "site-packages", "debugpy"))))
            return "debugpy is not in the tool venv: run tools/fetch-debugpy.sh";
        return null;
    }

    /// <summary>A system interpreter WITHOUT debugpy (the bundled adapter's case), or null.</summary>
    public static string? PythonWithoutDebugpy()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3" })
        {
            if (!File.Exists(p)) continue;
            if (OperatingSystem.IsMacOS() && p == "/usr/bin/python3" && !File.Exists("/Library/Developer/CommandLineTools/usr/bin/python3")) continue;
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p, ["-I", "-c", "import debugpy"]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            probe.WaitForExit(10_000);
            if (probe.HasExited && probe.ExitCode != 0) return p;
        }
        return null;
    }
}

/// <summary>The interpreter service the debugger sees: one interpreter; HasModule answers for real (does the interpreter import it?).</summary>
internal sealed class FixedPython(string executable) : IPythonEnvironmentService
{
    private readonly Emitter<Uri?> _changed = new();
    public PythonEnvironment Environment { get; } = new(executable, PythonEnvironmentKind.Venv, "3", Path.GetDirectoryName(Path.GetDirectoryName(executable)), "venv");
    public PythonEnvironment? GetActive(Uri? resource) => Environment;
    public ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default) => ValueTask.FromResult<PythonEnvironment?>(Environment);
    public ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<PythonEnvironment>>([Environment]);
    public ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default) => default;
    public ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default) => default;
    public async ValueTask<bool> HasModuleAsync(string exe, string module, CancellationToken ct = default)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, ["-I", "-c", $"import {module}"]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0;
    }
    public IEventSource<Uri?> ActiveChanged => _changed;
}

/// <summary>
/// The real debugpy through the real <see cref="DebugService"/> and <see cref="DebugpyAdapterFactory"/>: a sample program in a temp folder (markers
/// <c># @name</c> on the lines the tests break on), every session, stop, output and termination recorded.
/// </summary>
internal sealed class RealDebugpyRig : IAsyncDisposable
{
    public const string Program = """
        import asyncio
        import dataclasses
        import multiprocessing
        import os
        import sys
        import threading
        import time


        @dataclasses.dataclass
        class Point:
            x: int
            y: int


        class Box:
            def __init__(self):
                self.items = [1, 2, 3]
                self.name = "box"


        def add(a, b):
            return a + b  # @add


        def gen():
            for i in range(3):
                yield i  # @gen


        async def work(n):
            await asyncio.sleep(0)
            return n * 2  # @async


        def worker(tag):
            print("child", tag, os.getpid())  # @child


        def spin(name):
            value = name.upper()  # @thread
            return value


        def main(mode):
            data = {"a": 1, "b": [1, 2]}
            items = [10, 20, 30]
            letters = {"x", "y"}
            point = Point(1, 2)
            box = Box()
            big = list(range(5000))
            total = 0
            for i in range(5):
                total = add(total, i)  # @loop
            print("total", total)  # @print
            for v in gen():
                pass
            print("async", asyncio.run(work(21)))
            if mode == "caught":
                try:
                    raise KeyError("k")  # @caught
                except KeyError:
                    pass
            if mode == "raise":
                raise ValueError("boom")  # @raise
            if mode == "threads":
                t = threading.Thread(target=spin, args=("worker",), name="spinner")
                t.start()
                t.join()
            if mode == "mp":
                ctx = multiprocessing.get_context("spawn")
                p = ctx.Process(target=worker, args=("mp",))
                p.start()
                p.join()
            if mode == "flood":
                for n in range(1_000_000):
                    print(n)
                print("flood done")
            if mode == "wait":
                for _ in range(600):
                    time.sleep(0.1)  # @wait
            print("done", len(data), len(items), len(letters), point.x, box.name, len(big))  # @done


        if __name__ == "__main__":
            main(sys.argv[1] if len(sys.argv) > 1 else "")
        """;

    public required ServiceHarness H { get; init; }
    public required string ProgramPath { get; init; }
    public required string Python { get; init; }
    public List<IDebugSession> Started { get; } = new();
    public List<DebugStoppedEventArgs> Stops { get; } = new();
    public List<DebugOutputEventArgs> Output { get; } = new();
    public List<int> AdapterPids { get; } = new();
    public List<long> DebuggeePids { get; } = new();
    public DebugService Service => H.Service;
    public Uri ProgramUri => new(ProgramPath);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="python">The interpreter (default: the tool venv's, which has debugpy).</param>
    public static async Task<RealDebugpyRig> CreateAsync(string? python = null)
    {
        Assert.SkipWhen(DebugpyToolchain.SkipReason() is not null, DebugpyToolchain.SkipReason() ?? "");
        python ??= DebugpyToolchain.VenvPython;
        var h = new ServiceHarness(moreBuiltins: p => DebugpyAdapterFactory.Builtins(new DebugpyAdapterFactory(p)), python: new FixedPython(python));
        h.Settings.Set(DebugpyAdapterFactory.SettingPath, DebugpyToolchain.BundledAdapter); // for an interpreter without debugpy
        var path = Path.Combine(h.Dir.Path, "main.py");
        await File.WriteAllTextAsync(path, Program.ReplaceLineEndings("\n"), Ct);
        var rig = new RealDebugpyRig { H = h, ProgramPath = path, Python = python };
        h.Service.SessionStarted.Subscribe(s =>
        {
            lock (rig.Started) rig.Started.Add(s);
            if (s is DebugSession ds)
                ds.StateChanged.Subscribe(_ =>
                {
                    if (ds.AdapterProcessId is { } pid) lock (rig.AdapterPids) if (!rig.AdapterPids.Contains(pid)) rig.AdapterPids.Add(pid);
                    if (ds.DebuggeeProcessId is { } dp) lock (rig.DebuggeePids) if (!rig.DebuggeePids.Contains(dp)) rig.DebuggeePids.Add(dp);
                });
            s.Stopped.Subscribe(e => { lock (rig.Stops) rig.Stops.Add(e); });
        });
        h.Service.Output.Subscribe(e => { lock (rig.Output) rig.Output.Add(e); });
        return rig;
    }

    /// <summary>1-based line of <c># @marker</c>.</summary>
    public static int Line(string marker)
    {
        var lines = Program.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++) if (lines[i].TrimEnd().EndsWith("# @" + marker, StringComparison.Ordinal)) return i + 1;
        throw new ArgumentException("no marker " + marker);
    }

    public DebugConfiguration Launch(string mode = "", IDictionary<string, object?>? extra = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["program"] = ProgramPath,
            ["args"] = mode.Length > 0 ? new[] { mode } : Array.Empty<string>(),
            ["console"] = "internalConsole",
            ["justMyCode"] = true,
        };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        return ServiceHarness.Config("py", DebugpyAdapterFactory.PrimaryType, "launch", d, H.Dir.Uri);
    }

    public async Task<DebugSession> StartAsync(DebugConfiguration config) => (DebugSession)await H.StartAsync(config);

    public int StopCount { get { lock (Stops) return Stops.Count; } }
    public DebugStoppedEventArgs LastStop { get { lock (Stops) return Stops[^1]; } }
    public string OutputText { get { lock (Output) return string.Concat(Output.Select(o => o.Output)); } }

    public async Task<DebugStopLocation> WaitForStopAsync(int count, int timeoutMs = 30_000)
    {
        await SessionHarness.Eventually(() => StopCount >= count, timeoutMs, $"stop #{count}; output so far:\n{Tail(OutputText)}");
        await SessionHarness.Eventually(() => Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && ReferenceEquals(l.Session, LastStop.Session), timeoutMs, "stop location");
        return Service.StopLocation!;
    }

    private static string Tail(string s) => s.Length > 2000 ? s[^2000..] : s;

    public async Task WaitForEndAsync(int timeoutMs = 30_000) =>
        await SessionHarness.Eventually(() => Service.Sessions.Count == 0, timeoutMs, "every session ended: " + string.Join(", ", Service.Sessions.Select(s => $"{s.Name}={s.State}")));

    /// <summary>The adapters and debuggees of this rig are gone (clean exit: nothing left behind).</summary>
    public async Task AssertNothingLeftRunningAsync(int timeoutMs = 10_000)
    {
        int[] adapters;
        long[] debuggees;
        lock (AdapterPids) adapters = [.. AdapterPids];
        lock (DebuggeePids) debuggees = [.. DebuggeePids];
        foreach (var pid in adapters) await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), timeoutMs, $"adapter pid {pid} exits");
        foreach (var pid in debuggees) await SessionHarness.Eventually(() => !ProcessTable.IsAlive((int)pid), timeoutMs, $"debuggee pid {pid} exits");
    }

    public async Task<(IReadOnlyList<DebugStackFrame> Frames, DebugSession Session)> TopFramesAsync(int levels = 30)
    {
        var loc = Service.StopLocation ?? throw new InvalidOperationException("not stopped");
        var session = (DebugSession)loc.Session;
        return (await session.GetStackTraceAsync(loc.ThreadId, 0, levels, Ct), session);
    }

    /// <summary>The variables of the frame's first scope (debugpy: Locals).</summary>
    public static async Task<IReadOnlyList<DebugVariable>> LocalsAsync(DebugSession session, int frameId)
    {
        var scopes = await session.GetScopesAsync(frameId, Ct);
        return await session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Service.StopAllAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { }
        await H.DisposeAsync();
    }

    public static DebugConfiguration Raw(object config, Uri folder) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(config), folder);
}
