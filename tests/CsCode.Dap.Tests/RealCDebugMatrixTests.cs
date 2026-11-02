using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The C debugging matrix (stage 3, docs/C-CPP.md) against the REAL adapters of this Mac: CodeLLDB (<c>lldb</c>, the pinned copy from
/// tools/fetch-codelldb.sh) and Apple's lldb-dap (<c>lldb-dap</c>), each through the real <see cref="CsCode.Dap.Service.DebugService"/> and the
/// built-in factory, on one C program built with <c>cc -g -O0 -pthread</c>. Every row runs for both adapters; what the matrix decides on is
/// asserted (stops, lines, the values a user reads, exit codes) and how each adapter renders the rest is recorded as evidence through
/// <see cref="TestMetrics"/> (<c>CSCODE_DAP_TEST_METRICS=&lt;file&gt;</c>). Skips with the reason when an adapter, a C compiler or (macOS)
/// Developer Mode is missing. The rows that need the Memory / Disassembly windows and data breakpoints are stage 2's and not here.
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
[Collection(SerialCollection.Name)]
public sealed class RealCDebugMatrixTests
{
    private const string Program = """
        #include <pthread.h>
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>

        struct point { int x; int y; };
        union number { int i; float f; };

        static int square(int n) {
            int result = n * n; /* @square */
            return result;
        }

        static void *worker(void *arg) {
            int id = *(int *)arg;
            printf("worker %d\n", id); /* @worker */
            return NULL;
        }

        int main(int argc, char **argv) {
            struct point p = { 3, 4 };
            int numbers[4] = { 1, 2, 3, 4 };
            int *ptr = &numbers[2];
            union number u;
            u.i = 42;
            const char *name = "cscode";
            int total = 0;
            for (int i = 0; i < 5; i++) {
                total += square(i); /* @loop */
            }
            printf("total %d %d %d %s %d\n", total, p.x, *ptr, name, u.i); /* @values */
            const char *mode = argc > 1 ? argv[1] : "";
            if (strcmp(mode, "threads") == 0) {
                pthread_t threads[2];
                int ids[2] = { 1, 2 };
                for (int k = 0; k < 2; k++) pthread_create(&threads[k], NULL, worker, &ids[k]);
                for (int k = 0; k < 2; k++) pthread_join(threads[k], NULL);
            }
            if (strcmp(mode, "segv") == 0) {
                int *volatile bad = NULL;
                *bad = 1; /* @segv */
            }
            if (strcmp(mode, "abort") == 0) abort(); /* @abort */
            return 7;
        }
        """;

    /// <summary>The 0-based line of a marker: breakpoints, stop locations and frames all count from 0 in Core.</summary>
    private static int Line(string marker) =>
        Program.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains("/* " + marker + " */", StringComparison.Ordinal)).i;

    /// <summary>Breakpoint changes reach the adapter after the sync's debounce (~50 ms): wait as long as a user's next click would.</summary>
    private static Task SettleAsync() => Task.Delay(400, Ct);

    private static void Note(string adapter, string text) => TestMetrics.Report($"[c-matrix {adapter}] {text}");

    /// <summary>One adapter's session over the sample: the harness, the built program, the stops and the output seen so far.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceHarness H { get; init; }
        public required string Type { get; init; }
        public required string Source { get; init; }
        public required string Binary { get; init; }
        public List<DebugStoppedEventArgs> Stops { get; } = [];
        public List<DebugTerminatedEventArgs> Ends { get; } = [];
        public System.Text.StringBuilder Output { get; } = new();
        public Uri SourceUri => new(Source);
        public DebugSession? Session { get; set; }

        public async Task<DebugSession> StartAsync(params string[] args)
        {
            var extra = new Dictionary<string, object?> { ["program"] = Binary, ["args"] = args, ["cwd"] = H.Dir.Path };
            if (Type == CodeLldbAdapterFactory.PrimaryType) extra["sourceLanguages"] = new[] { "c" };
            Session = (DebugSession)await H.StartAsync(ServiceHarness.Config("C matrix", Type, extra: extra, folder: H.Dir.Uri));
            return Session;
        }

        public async Task<DebugStopLocation> WaitForStopAsync(int count)
        {
            await SessionHarness.Eventually(() => { lock (Stops) return Stops.Count >= count; }, 60_000, $"stop #{count}");
            await SessionHarness.Eventually(() => H.Service.StopLocation is { Session.State: DebugSessionState.Stopped }, 30_000, "stop location");
            return H.Service.StopLocation!;
        }

        public DebugStoppedEventArgs LastStop { get { lock (Stops) return Stops[^1]; } }

        public async Task<string> ValueAsync(string expression)
        {
            var loc = H.Service.StopLocation!;
            return (await loc.Session.EvaluateAsync(expression, loc.FrameId, "watch", Ct)).Result;
        }

        public async Task<Dictionary<string, DebugVariable>> LocalsAsync()
        {
            var loc = H.Service.StopLocation!;
            var result = new Dictionary<string, DebugVariable>(StringComparer.Ordinal);
            foreach (var scope in (await loc.Session.GetScopesAsync(loc.FrameId, Ct)).Where(s => !s.Expensive).Take(1))
                foreach (var v in await loc.Session.GetVariablesAsync(scope.VariablesReference, ct: Ct)) result.TryAdd(v.Name, v);
            return result;
        }

        public async Task<IReadOnlyList<DebugVariable>> ChildrenAsync(DebugVariable v) =>
            v.VariablesReference == 0 ? [] : await H.Service.StopLocation!.Session.GetVariablesAsync(v.VariablesReference, ct: Ct);

        public async Task ContinueAsync() => await H.Service.StopLocation!.Session.ContinueAsync(ct: Ct);

        public async Task EndAsync()
        {
            await SessionHarness.Eventually(() => Session!.State == DebugSessionState.Terminated, 60_000, "the session ended");
        }

        public async ValueTask DisposeAsync()
        {
            try { if (Session is { State: not DebugSessionState.Terminated } s) await s.StopAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            await H.DisposeAsync();
        }
    }

    private static async Task<Rig> RigAsync(string type)
    {
        string? adapter;
        if (type == CodeLldbAdapterFactory.PrimaryType)
        {
            adapter = CodeLldbToolchain.FindAdapter();
            Assert.SkipWhen(adapter is null, $"CodeLLDB is not installed: run tools/fetch-codelldb.sh or set {CodeLldbToolchain.EnvironmentVariable}");
        }
        else
        {
            adapter = await new LldbDapAdapterFactory(new DapPlatform()).LocateAsync(Ct);
            Assert.SkipWhen(adapter is null, "lldb-dap is not installed (macOS: Xcode or its Command Line Tools; Linux: LLDB from the distribution or LLVM)");
        }
        var blocked = await RealLldbDapTests.MacDebuggingBlockedReasonAsync();
        Assert.SkipWhen(blocked is not null, blocked ?? "");
        var cc = RustToolchainLocator.FindOnPath(Environment.GetEnvironmentVariable, "cc") ?? RustToolchainLocator.FindOnPath(Environment.GetEnvironmentVariable, "clang");
        Assert.SkipWhen(cc is null, "no C compiler (cc or clang) on PATH");

        var h = new ServiceHarness(moreBuiltins: p => [.. CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p)), .. LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p))]);
        h.Settings.Set(type == CodeLldbAdapterFactory.PrimaryType ? CodeLldbAdapterFactory.SettingPath : LldbDapAdapterFactory.SettingPath, adapter);
        var source = h.Dir.WriteFile("matrix.c", Program);
        var binary = h.Dir.Combine("matrix");
        using (var build = Process.Start(new ProcessStartInfo(cc!, ["-g", "-O0", "-pthread", "-o", binary, source]) { UseShellExecute = false, RedirectStandardError = true })!)
        {
            var errors = await build.StandardError.ReadToEndAsync(Ct);
            await build.WaitForExitAsync(Ct);
            Assert.True(build.ExitCode == 0, errors);
        }
        var rig = new Rig { H = h, Type = type, Source = source, Binary = binary };
        h.Service.SessionStarted.Subscribe(s =>
        {
            s.Stopped.Subscribe(e => { lock (rig.Stops) rig.Stops.Add(e); });
            s.Terminated.Subscribe(e => { lock (rig.Ends) rig.Ends.Add(e); });
            s.Output.Subscribe(e => { lock (rig.Output) rig.Output.Append(e.Output); });
        });
        return rig;
    }

    public static TheoryData<string> Adapters => [CodeLldbAdapterFactory.PrimaryType, LldbDapAdapterFactory.PrimaryType];

    private static void AssertAt(DebugStopLocation loc, int line)
    {
        Assert.Equal("matrix.c", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.Equal(line, loc.Line);
    }

    // ------------------------------------------------------------------ launch and exit

    [Theory, MemberData(nameof(Adapters))]
    public async Task Launch_runs_to_a_clean_exit_with_the_programs_code_and_output(string type)
    {
        await using var rig = await RigAsync(type);
        var started = Stopwatch.GetTimestamp();
        await rig.StartAsync();
        await rig.EndAsync();
        Note(type, $"launch to exit: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms; ends: {string.Join(", ", rig.Ends.Select(e => $"exit={e.ExitCode} reason={e.Reason}"))}");
        await SessionHarness.Eventually(() => { lock (rig.Output) return rig.Output.ToString().Contains("total 30 3 3 cscode 42", StringComparison.Ordinal); }, 10_000, "program output");
        Assert.Contains(rig.Ends, e => e.ExitCode == 7);
    }

    // ------------------------------------------------------------------ breakpoints

    [Theory, MemberData(nameof(Adapters))]
    public async Task Line_conditional_and_hit_count_breakpoints_stop_where_and_when_they_should(string type)
    {
        await using var rig = await RigAsync(type);
        var line = rig.H.Breakpoints.Add(rig.SourceUri, Line("@square"));
        var conditional = rig.H.Breakpoints.Add(rig.SourceUri, Line("@loop"), condition: "i == 3");
        await rig.StartAsync();

        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, Line("@square"));
        Assert.Equal("breakpoint", rig.LastStop.Reason);
        Assert.Equal("0", await rig.ValueAsync("n"));
        rig.H.Breakpoints.Remove(line.Id);
        await SettleAsync();
        await rig.ContinueAsync();

        loc = await rig.WaitForStopAsync(2);
        AssertAt(loc, Line("@loop"));
        Assert.Equal("3", await rig.ValueAsync("i"));
        Note(type, $"conditional breakpoint: stopped with i = 3, verdict {rig.H.Breakpoints.GetStatus(conditional.Id)}");
        rig.H.Breakpoints.Remove(conditional.Id);
        await SettleAsync();

        // hit count: the third time square() is entered from here on (CodeLLDB and lldb-dap read a plain number as "on the Nth hit")
        var hit = rig.H.Breakpoints.Add(rig.SourceUri, Line("@square"), hitCondition: "2");
        await SettleAsync();
        await rig.ContinueAsync();
        loc = await rig.WaitForStopAsync(3);
        AssertAt(loc, Line("@square"));
        var n = await rig.ValueAsync("n");
        Note(type, $"hit condition '2' set while i = 3: stopped in square with n = {n}");
        Assert.Equal("4", n); // square(3) is the first hit, square(4) the second
        rig.H.Breakpoints.Remove(hit.Id);
        await SettleAsync();
        await rig.ContinueAsync();
        await rig.EndAsync();
    }

    [Theory, MemberData(nameof(Adapters))]
    public async Task A_logpoint_writes_its_message_without_stopping(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.SourceUri, Line("@square"), logMessage: "square of {n}");
        await rig.StartAsync();
        await rig.EndAsync();
        string output;
        lock (rig.Output) output = rig.Output.ToString();
        Note(type, "logpoint output: " + string.Join(" | ", output.Split('\n').Where(l => l.Contains("square of", StringComparison.Ordinal)).Take(5)));
        Assert.Contains("square of 4", output, StringComparison.Ordinal);
        Assert.Empty(rig.Stops);
    }

    [Theory, MemberData(nameof(Adapters))]
    public async Task A_function_breakpoint_stops_in_the_function(string type)
    {
        await using var rig = await RigAsync(type);
        var fn = rig.H.Breakpoints.AddFunction("square");
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        Note(type, $"function breakpoint: reason {rig.LastStop.Reason}, line {loc.Line + 1}");
        Assert.Equal("matrix.c", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.InRange(loc.Line, Line("@square") - 1, Line("@square"));
        Assert.Equal("0", await rig.ValueAsync("n"));
        rig.H.Breakpoints.RemoveFunction(fn.Id);
        await SettleAsync();
        await rig.ContinueAsync();
        await rig.EndAsync();
    }

    // ------------------------------------------------------------------ steps

    [Theory, MemberData(nameof(Adapters))]
    public async Task Step_into_out_of_and_over_a_call(string type)
    {
        await using var rig = await RigAsync(type);
        var bp = rig.H.Breakpoints.Add(rig.SourceUri, Line("@loop"));
        var session = await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        rig.H.Breakpoints.Remove(bp.Id);
        await SettleAsync();

        await session.StepIntoAsync(ct: Ct);
        var loc = await rig.WaitForStopAsync(2);
        Assert.Equal("step", rig.LastStop.Reason);
        Assert.InRange(loc.Line, Line("@square") - 1, Line("@square")); // the function's first line or its first statement
        var frames = await session.GetStackTraceAsync(loc.ThreadId, ct: Ct);
        Note(type, "stack in square: " + string.Join(" | ", frames.Take(3).Select(f => $"{f.Name}:{f.Line + 1}")));
        Assert.StartsWith("square", frames[0].Name, StringComparison.Ordinal);

        await session.StepOutAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(3);
        AssertAt(loc, Line("@loop"));

        await session.StepOverAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(4);
        Note(type, $"step over from the loop body: line {loc.Line + 1}");
        Assert.InRange(loc.Line, Line("@loop") - 1, Line("@loop") + 1);
        await rig.ContinueAsync();
        await rig.EndAsync();
    }

    // ------------------------------------------------------------------ values

    [Theory, MemberData(nameof(Adapters))]
    public async Task Structs_arrays_pointers_unions_and_strings_read_as_a_user_expects(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.SourceUri, Line("@values"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var locals = await rig.LocalsAsync();
        Note(type, "locals: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}: {v.Type} = {v.Value} (ref {v.VariablesReference}, mem {v.MemoryReference})")));

        var p = await rig.ChildrenAsync(locals["p"]);
        Assert.Equal([("x", "3"), ("y", "4")], p.Select(c => (c.Name, c.Value)));
        var numbers = await rig.ChildrenAsync(locals["numbers"]);
        Assert.Equal(["1", "2", "3", "4"], numbers.Select(c => c.Value));
        Assert.NotEqual(0, locals["ptr"].VariablesReference); // a pointer expands to its target
        var pointee = await rig.ChildrenAsync(locals["ptr"]);
        Note(type, "ptr expands to: " + string.Join(", ", pointee.Select(c => $"{c.Name} = {c.Value}")));
        Assert.Contains(pointee, c => c.Value == "3");
        var u = await rig.ChildrenAsync(locals["u"]);
        Assert.Contains(u, c => c.Name == "i" && c.Value == "42");
        Assert.Contains(u, c => c.Name == "f");
        Assert.Contains("\"cscode\"", locals["name"].Value, StringComparison.Ordinal);
        Assert.Equal("30", locals["total"].Value);
        Assert.Equal("3", await rig.ValueAsync("*ptr"));
        Assert.Equal("4", await rig.ValueAsync("p.y"));
        await rig.ContinueAsync();
        await rig.EndAsync();
    }

    // ------------------------------------------------------------------ signals

    [Theory, MemberData(nameof(Adapters))]
    public async Task A_segmentation_fault_stops_as_an_exception_at_the_faulting_line(string type)
    {
        await using var rig = await RigAsync(type);
        await rig.StartAsync("segv");
        var loc = await rig.WaitForStopAsync(1);
        var stop = rig.LastStop;
        Note(type, $"SIGSEGV: reason '{stop.Reason}', description '{stop.Description}', text '{stop.Text}', line {loc.Line + 1}");
        Assert.Contains(stop.Reason, new[] { "exception", "signal" });
        AssertAt(loc, Line("@segv"));
        await loc.Session.StopAsync(Ct);
    }

    [Theory, MemberData(nameof(Adapters))]
    public async Task Abort_stops_as_an_exception_with_main_in_the_stack(string type)
    {
        await using var rig = await RigAsync(type);
        var session = await rig.StartAsync("abort");
        var loc = await rig.WaitForStopAsync(1);
        var stop = rig.LastStop;
        var frames = await session.GetStackTraceAsync(loc.ThreadId, ct: Ct);
        Note(type, $"abort: reason '{stop.Reason}', description '{stop.Description}', text '{stop.Text}'; stack {string.Join(" | ", frames.Take(6).Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1}"))}");
        Assert.Contains(stop.Reason, new[] { "exception", "signal" });
        Assert.Contains(frames, f => f.Name.StartsWith("main", StringComparison.Ordinal) && f.Line == Line("@abort"));
        await session.StopAsync(Ct);
    }

    // ------------------------------------------------------------------ threads

    [Theory, MemberData(nameof(Adapters))]
    public async Task Pthreads_are_listed_and_a_breakpoint_in_a_worker_stops_its_thread(string type)
    {
        await using var rig = await RigAsync(type);
        var bp = rig.H.Breakpoints.Add(rig.SourceUri, Line("@worker"));
        var session = await rig.StartAsync("threads");
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, Line("@worker"));
        var threads = await session.GetThreadsAsync(Ct);
        Note(type, $"threads at the worker breakpoint: {string.Join(" | ", threads.Select(t => $"{t.Id} {t.Name}"))}; stopped thread {loc.ThreadId}");
        for (var retry = 0; retry < 10 && threads.All(t => t.Id != loc.ThreadId); retry++)
        {
            await Task.Delay(200, Ct);
            threads = await session.GetThreadsAsync(Ct);
            Note(type, $"threads asked again after {(retry + 1) * 200} ms: {string.Join(" | ", threads.Select(t => $"{t.Id} {t.Name}"))}");
        }
        Assert.True(threads.Count >= 2, "the main thread and at least one worker");
        Assert.Contains(threads, t => t.Id == loc.ThreadId);
        var frames = await session.GetStackTraceAsync(loc.ThreadId, ct: Ct);
        Assert.StartsWith("worker", frames[0].Name, StringComparison.Ordinal);
        Assert.Contains(await rig.ValueAsync("id"), new[] { "1", "2" });
        rig.H.Breakpoints.Remove(bp.Id);
        await SettleAsync();
        await rig.ContinueAsync();
        await rig.EndAsync();
    }
}
