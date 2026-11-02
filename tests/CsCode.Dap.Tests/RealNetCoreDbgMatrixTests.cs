using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The rest of the .NET debug matrix of docs/CSHARP.md against the REAL netcoredbg (RealNetCoreDbgTests has line, conditional, hit-count,
/// logpoint and exception breakpoints, stepping and evaluate): async call stacks, Locals for collections, records and a LINQ query, evaluate on
/// them, set variable, threads, and attach to a running program. A sample is built in a temp folder by its physical path (netcoredbg binds a
/// breakpoint to the path in the PDB). Skips when netcoredbg or dotnet is missing. What each step showed is printed as a diagnostic message.
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
public class RealNetCoreDbgMatrixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Matrix;

        public record Point(int X, int Y);

        public static class Program
        {
            static async Task<int> InnerAsync(int n)
            {
                await Task.Delay(10);
                var doubled = n * 2; // @async
                return doubled;
            }

            static async Task<int> OuterAsync(int n) => await InnerAsync(n + 1);

            public static async Task<int> Main(string[] args)
            {
                if (args.Length > 0 && args[0] == "wait")
                {
                    Console.WriteLine($"pid={Environment.ProcessId}");
                    for (var i = 0; i < 600 && !System.Diagnostics.Debugger.IsAttached; i++) Thread.Sleep(100);
                    Attached();
                    return 0;
                }
                var list = new List<int> { 1, 2, 3 };
                var map = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
                var point = new Point(3, 4);
                var query = list.Where(x => x > 1).Select(x => x * 10);
                var counter = 5;
                var worker = new Thread(() => Thread.Sleep(5000)) { Name = "matrix-worker", IsBackground = true };
                worker.Start();
                Console.WriteLine("ready"); // @locals
                Console.WriteLine($"counter={counter}");
                return await OuterAsync(counter);
            }

            static void Attached()
            {
                Console.WriteLine("attached"); // @attached
            }
        }
        """;

    private static int Line(string marker) => Source.Split('\n').ToList().FindIndex(l => l.Contains("// @" + marker, StringComparison.Ordinal));

    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static (string Dll, string SourcePath)? s_built;

    private static async Task<(string Dll, string SourcePath)> BuildAsync()
    {
        await BuildGate.WaitAsync(Ct);
        try
        {
            if (s_built is { } built) return built;
            var temp = new TempDirectory("dapmatrix-");
            var dir = RealAdapterSupport.PhysicalPath(temp.Path);
            File.WriteAllText(Path.Combine(dir, "Matrix.csproj"), RealAdapterSupport.ProjectFile.Replace("<ImplicitUsings>disable</ImplicitUsings>", "<ImplicitUsings>disable</ImplicitUsings>", StringComparison.Ordinal));
            var source = Path.Combine(dir, "Program.cs");
            File.WriteAllText(source, Source);
            var psi = new ProcessStartInfo(RealAdapterSupport.FindDotnet()!) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "build", "Matrix.csproj", "-c", "Debug", "-o", "out", "--nologo", "-v", "q" }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(Ct) + await p.StandardError.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("building the matrix sample failed:\n" + output);
            s_built = (Path.Combine(dir, "out", "Matrix.dll"), source);
            return s_built.Value;
        }
        finally { BuildGate.Release(); }
    }

    private static void Note(string text) => TestContext.Current.SendDiagnosticMessage(text);

    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceHarness H { get; init; }
        public required string Dll { get; init; }
        public required string SourcePath { get; init; }
        public List<string> Output { get; } = [];

        public static async Task<Rig> CreateAsync()
        {
            var exe = RealAdapterSupport.FindNetCoreDbg();
            Assert.SkipUnless(exe is not null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/) or set CSCODE_NETCOREDBG");
            Assert.SkipUnless(RealAdapterSupport.FindDotnet() is not null, "`dotnet` is not on PATH, so the sample project cannot be built");
            var (dll, source) = await BuildAsync();
            var h = new ServiceHarness();
            h.Settings.Set(NetCoreDbgAdapterFactory.SettingPath, exe);
            return new Rig { H = h, Dll = dll, SourcePath = source };
        }

        public async Task<DebugSession> StartAsync(string request, IDictionary<string, object?> extra)
        {
            var d = new Dictionary<string, object?> { ["justMyCode"] = true, ["console"] = "internalConsole" };
            foreach (var (k, v) in extra) d[k] = v;
            var session = (DebugSession)await H.StartAsync(ServiceHarness.Config("matrix", "coreclr", request, d, H.Dir.Uri));
            session.Output.Subscribe(e => { lock (Output) Output.Add(e.Output); });
            return session;
        }

        public Task<DebugSession> LaunchAsync() =>
            StartAsync("launch", new Dictionary<string, object?> { ["program"] = Dll, ["cwd"] = Path.GetDirectoryName(Dll), ["args"] = Array.Empty<string>(), ["stopAtEntry"] = false });

        public async Task<DebugStopLocation> StopAtAsync(string marker, int timeoutMs = 40_000)
        {
            await SessionHarness.Eventually(() => H.Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && l.Line == Line(marker), timeoutMs, "stopped at @" + marker);
            return H.Service.StopLocation!;
        }

        public string OutputText { get { lock (Output) return string.Concat(Output); } }

        public async ValueTask DisposeAsync() => await H.DisposeAsync();
    }

    private static async Task<IReadOnlyList<DebugVariable>> LocalsAsync(DebugSession session, int frameId)
    {
        var scopes = await session.GetScopesAsync(frameId, Ct);
        var locals = scopes.First(s => s.Name.Contains("Local", StringComparison.OrdinalIgnoreCase) || !s.Expensive);
        return await session.GetVariablesAsync(locals.VariablesReference, ct: Ct);
    }

    [Fact]
    public async Task Locals_show_collections_records_and_a_linq_query_which_evaluate_and_set_variable_can_use()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Service.Breakpoints.Toggle(new Uri(rig.SourcePath), Line("locals"));
        var session = await rig.LaunchAsync();
        var stop = await rig.StopAtAsync("locals");
        var locals = await LocalsAsync(session, stop.FrameId);
        Note("locals: " + string.Join(" | ", locals.Select(v => $"{v.Name}: {v.Type} = {v.Value} (children {v.VariablesReference})")));

        // netcoredbg shows a collection's value as its type ({System.Collections.Generic.List<int>}: no [DebuggerDisplay] "Count = 3");
        // its children are the elements
        var list = locals.Single(v => v.Name == "list");
        Assert.True(list.VariablesReference > 0);
        var items = await session.GetVariablesAsync(list.VariablesReference, ct: Ct);
        Note("list children: " + string.Join(" | ", items.Select(v => $"{v.Name} = {v.Value}")));
        // no debugger type proxy: the raw fields (_items, _size, Count), the elements under _items
        Assert.Contains(items, v => v.Name == "Count" && v.Value == "3");
        var array = await session.GetVariablesAsync(items.Single(v => v.Name == "_items").VariablesReference, ct: Ct);
        Note("list._items: " + string.Join(" | ", array.Select(v => $"{v.Name} = {v.Value}")));
        Assert.Contains(array, v => v.Name == "[0]" && v.Value == "1");

        var map = locals.Single(v => v.Name == "map");
        var entries = await session.GetVariablesAsync(map.VariablesReference, ct: Ct);
        Note("map children: " + string.Join(" | ", entries.Select(v => $"{v.Name} = {v.Value}")));
        Assert.NotEmpty(entries);

        var point = locals.Single(v => v.Name == "point");
        var members = await session.GetVariablesAsync(point.VariablesReference, ct: Ct);
        Note($"point = {point.Value}; members: " + string.Join(" | ", members.Select(v => $"{v.Name} = {v.Value}")));
        Assert.Contains(members, v => v.Name == "X" && v.Value == "3");
        Assert.Contains(members, v => v.Name == "Y" && v.Value == "4");

        var query = locals.Single(v => v.Name == "query");
        var queryChildren = query.VariablesReference > 0 ? await session.GetVariablesAsync(query.VariablesReference, ct: Ct) : [];
        Note($"query: {query.Type} = {query.Value}; children: " + string.Join(" | ", queryChildren.Select(v => $"{v.Name} = {v.Value}")));

        // evaluate: members, indexers, a record's property; LINQ with a lambda is beyond netcoredbg's evaluator (recorded, not required)
        var sum = await session.EvaluateAsync("list.Count + point.X", stop.FrameId, "watch", Ct);
        Assert.Equal("6", sum.Result);
        var two = await session.EvaluateAsync("map[\"two\"]", stop.FrameId, "watch", Ct);
        Assert.Equal("2", two.Result);
        try { Note("evaluate list.Where(x => x > 1).Count(): " + (await session.EvaluateAsync("list.Where(x => x > 1).Count()", stop.FrameId, "watch", Ct)).Result); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Note("evaluate with a lambda: " + ex.Message); }

        // set variable: the program prints the new value
        var scopes = await session.GetScopesAsync(stop.FrameId, Ct);
        var localsScope = scopes.First(s => s.Name.Contains("Local", StringComparison.OrdinalIgnoreCase) || !s.Expensive);
        var set = await session.SetVariableAsync(localsScope.VariablesReference, "counter", "42", Ct);
        Assert.Equal("42", set.Value);
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => rig.OutputText.Contains("counter=42", StringComparison.Ordinal), 20_000, "the program printed the value set in Locals");
    }

    [Fact]
    public async Task Threads_are_listed_by_name_and_an_async_method_shows_its_call_stack()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Service.Breakpoints.Toggle(new Uri(rig.SourcePath), Line("locals"));
        rig.H.Service.Breakpoints.Toggle(new Uri(rig.SourcePath), Line("async"));
        var session = await rig.LaunchAsync();
        var stop = await rig.StopAtAsync("locals");
        var threads = await session.GetThreadsAsync(Ct);
        Note("threads at the first stop: " + string.Join(" | ", threads.Select(t => $"{t.Id}:{t.Name}")));
        Assert.Contains(threads, t => t.Name == "Main Thread");
        var worker = await session.EvaluateAsync("worker.ManagedThreadId", stop.FrameId, "watch", Ct);
        Note("worker.ManagedThreadId = " + worker.Result + "; worker.IsAlive = " + (await session.EvaluateAsync("worker.IsAlive", stop.FrameId, "watch", Ct)).Result);

        await session.ContinueAsync(ct: Ct);
        var inner = await rig.StopAtAsync("async");
        Note("threads at the async stop: " + string.Join(" | ", (await session.GetThreadsAsync(Ct)).Select(t => $"{t.Id}:{t.Name}")));
        var frames = await session.GetStackTraceAsync(inner.ThreadId, ct: Ct);
        Note("async stack: " + string.Join(" | ", frames.Select(f => $"{f.Name} @ {Path.GetFileName(f.Source?.Path)}:{f.Line + 1}")));
        Assert.Contains("InnerAsync", frames[0].Name, StringComparison.Ordinal);
        Assert.Equal(new Uri(rig.SourcePath), new Uri(frames[0].Source!.Path!));
    }

    [Fact]
    public async Task Attaching_to_a_running_program_stops_at_its_breakpoint()
    {
        await using var rig = await Rig.CreateAsync();
        var psi = new ProcessStartInfo(RealAdapterSupport.FindDotnet()!) { WorkingDirectory = Path.GetDirectoryName(rig.Dll)!, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(rig.Dll);
        psi.ArgumentList.Add("wait");
        using var program = Process.Start(psi)!;
        try
        {
            var first = await program.StandardOutput.ReadLineAsync(Ct);
            Assert.StartsWith("pid=", first);
            rig.H.Service.Breakpoints.Toggle(new Uri(rig.SourcePath), Line("attached"));
            var session = await rig.StartAsync("attach", new Dictionary<string, object?> { ["processId"] = program.Id });
            var stop = await rig.StopAtAsync("attached", 60_000);
            Note($"attached to {program.Id}: stopped in {Path.GetFileName(stop.Uri?.LocalPath)}:{stop.Line + 1}");
            await session.ContinueAsync(ct: Ct);
            Assert.Equal("attached", await program.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct));
        }
        finally
        {
            if (!program.HasExited) program.Kill(entireProcessTree: true);
        }
    }
}
