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
/// The C++ debugging matrix (docs/C-CPP.md, "C++ debugging") against the REAL adapters through the real <see cref="Service.DebugService"/>: a
/// C++20 program built with the system's clang++ (libc++ on macOS) debugged under CodeLLDB (type <c>lldb</c>, <c>sourceLanguages: ["cpp"]</c>)
/// and lldb-dap, row for row: libc++ values through LLDB's formatters, the dynamic type of a polymorphic object, template and lambda frames,
/// the C++ exception filters (throw, catch), and <c>std::thread</c> / <c>std::jthread</c>. The renderings are recorded as evidence
/// (<c>CSCODE_DAP_TEST_METRICS=&lt;file&gt;</c>); the assertions are what a user reads. Skips when the adapter, clang++ or (macOS) Developer
/// Mode is missing. The Memory / Disassembly rows wait for those windows (stage 2).
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
[Collection(SerialCollection.Name)]
public class RealCppDebuggingTests
{
    private const string Source = """
        #include <cstdio>
        #include <map>
        #include <memory>
        #include <optional>
        #include <stdexcept>
        #include <string>
        #include <thread>
        #include <unordered_map>
        #include <variant>
        #include <vector>

        struct Shape { virtual ~Shape() = default; virtual double area() const = 0; };
        struct Circle : Shape { double radius = 2; double area() const override { return 3 * radius * radius; } };

        template <typename T> T twice(T value) {
          T result = value + value; // @template
          return result;
        }

        static int thrower(int n) {
          if (n > 0) throw std::runtime_error("boom"); // @throw
          return n;
        }

        static int worker_value = 0;

        int main() {
          std::string text = "hello world";
          std::vector<int> numbers{1, 2, 3};
          std::map<std::string, int> ages{{"ann", 31}, {"bob", 42}};
          std::unordered_map<int, std::string> names{{1, "one"}};
          std::optional<int> some = 5;
          std::optional<int> none;
          std::variant<int, std::string> var = std::string("alt");
          auto unique = std::make_unique<int>(7);
          auto shared = std::make_shared<std::string>("shared");
          std::unique_ptr<Shape> shape = std::make_unique<Circle>();
          Shape* raw = shape.get();
          int total = 0; // @values
          auto lambda = [&](int x) { return twice(x) + total; };
          total = lambda(3);
          try { thrower(1); } catch (const std::exception& e) { std::printf("caught %s\n", e.what()); } // @catch
          std::thread plain([] {
            worker_value += 1; // @thread
          });
          plain.join();
          {
            std::jthread joining([] {
              worker_value += 10; // @jthread
            });
          }
          std::printf("done %d %d %zu %d\n", total, worker_value, numbers.size(), (int)raw->area());
          return 0;
        }
        """;

    private static int Line(string marker) => Source.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains("// " + marker, StringComparison.Ordinal)).i + 1;

    private static void Note(string type, string text) => TestMetrics.Report($"[cpp/{type}] {text}");

    private static string? _binary;
    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    /// <summary>The program, built once per run with clang++ -g -O0 -std=c++20 into a temp folder (kept for the run).</summary>
    private static async Task<string> BinaryAsync()
    {
        await BuildGate.WaitAsync(Ct);
        try
        {
            if (_binary is not null) return _binary;
            var cxx = RustToolchainLocator.FindOnPath(Environment.GetEnvironmentVariable, "clang++") ?? RustToolchainLocator.FindOnPath(Environment.GetEnvironmentVariable, "c++");
            Assert.SkipWhen(cxx is null, "no C++ compiler (clang++ or c++) on PATH");
            var dir = new TempDirectory("cscode-cpp-debug-");
            var source = dir.WriteFile("matrix.cpp", Source);
            var binary = dir.Combine("matrix");
            using var build = Process.Start(new ProcessStartInfo(cxx!, ["-g", "-O0", "-std=c++20", "-o", binary, source]) { UseShellExecute = false, RedirectStandardError = true })!;
            var errors = await build.StandardError.ReadToEndAsync(Ct);
            await build.WaitForExitAsync(Ct);
            Assert.True(build.ExitCode == 0, "clang++ failed: " + errors);
            return _binary = binary;
        }
        finally { BuildGate.Release(); }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceHarness H { get; init; }
        public required string Type { get; init; }
        public required string Binary { get; init; }
        public List<DebugStoppedEventArgs> Stops { get; } = [];
        public List<DebugOutputEventArgs> Output { get; } = [];
        public Uri SourceUri => new(Path.Combine(Path.GetDirectoryName(Binary)!, "matrix.cpp"));
        public DebugSession? Session { get; set; }

        public async Task<DebugStopLocation> WaitForStopAsync(int count)
        {
            await SessionHarness.Eventually(() => { lock (Stops) return Stops.Count >= count; }, 60_000, $"stop #{count}; output: {OutputText}");
            await SessionHarness.Eventually(() => H.Service.StopLocation is { Session.State: DebugSessionState.Stopped }, 30_000, "stop location");
            return H.Service.StopLocation!;
        }

        public DebugStoppedEventArgs LastStop { get { lock (Stops) return Stops[^1]; } }
        public string OutputText { get { lock (Output) return string.Concat(Output.Select(o => o.Output)); } }

        public async Task<Dictionary<string, DebugVariable>> LocalsAsync()
        {
            var loc = H.Service.StopLocation!;
            var all = new Dictionary<string, DebugVariable>(StringComparer.Ordinal);
            foreach (var scope in (await Session!.GetScopesAsync(loc.FrameId, Ct)).Where(s => !s.Expensive || s.Name.StartsWith("Local", StringComparison.Ordinal)))
                foreach (var v in await Session.GetVariablesAsync(scope.VariablesReference, ct: Ct)) all.TryAdd(v.Name, v);
            return all;
        }

        public async Task<IReadOnlyList<DebugVariable>> ChildrenAsync(DebugVariable v) =>
            v.VariablesReference == 0 ? [] : await Session!.GetVariablesAsync(v.VariablesReference, ct: Ct);

        public async Task ContinueAsync() => await H.Service.StopLocation!.Session.ContinueAsync(ct: Ct);

        /// <summary>Continues to the end; a stop on the way (there should be none) is recorded and continued, at most a few times.</summary>
        public async Task RunToEndAsync(string type)
        {
            var count = StopCount;
            await ContinueAsync();
            for (var extra = 0; extra < 5; extra++)
            {
                await SessionHarness.Eventually(() => H.Service.Sessions.Count == 0 || (StopCount > count && H.Service.StopLocation is { Session.State: DebugSessionState.Stopped }),
                    60_000, "the session ended or stopped; output: " + OutputText);
                if (H.Service.Sessions.Count == 0) return;
                count = StopCount;
                var loc = H.Service.StopLocation!;
                var frames = await loc.Session.GetStackTraceAsync(loc.ThreadId, 0, 3, Ct);
                Note(type, $"unexpected stop on the way to the end: {LastStop.Reason} {LastStop.Description} at {string.Join(" | ", frames.Select(f => f.Name))}");
                await ContinueAsync();
            }
            Assert.Fail("the program kept stopping");
        }

        public int StopCount { get { lock (Stops) return Stops.Count; } }

        public async ValueTask DisposeAsync()
        {
            try { await H.Service.StopAllAsync(CancellationToken.None); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            await H.DisposeAsync();
        }
    }

    private static async Task<Rig> RigAsync(string type)
    {
        string? adapter;
        if (type == CodeLldbAdapterFactory.PrimaryType)
        {
            adapter = CodeLldbToolchain.FindAdapter();
            Assert.SkipWhen(adapter is null, $"CodeLLDB {CodeLldbToolchain.PinnedVersion} is not installed: run tools/fetch-codelldb.sh or set {CodeLldbToolchain.EnvironmentVariable}");
        }
        else
        {
            adapter = await new LldbDapAdapterFactory(new DapPlatform()).LocateAsync(Ct);
            Assert.SkipWhen(adapter is null, "lldb-dap is not installed (macOS: Xcode or its Command Line Tools; Linux: LLDB)");
        }
        var blocked = await RealLldbDapTests.MacDebuggingBlockedReasonAsync();
        Assert.SkipWhen(blocked is not null, blocked ?? "");
        var binary = await BinaryAsync();
        var h = new ServiceHarness(moreBuiltins: p => type == CodeLldbAdapterFactory.PrimaryType
            ? CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p))
            : LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p)));
        h.Settings.Set(type == CodeLldbAdapterFactory.PrimaryType ? CodeLldbAdapterFactory.SettingPath : LldbDapAdapterFactory.SettingPath, adapter!);
        // no stop at a C++ throw (CodeLLDB's default) unless a test asks for it
        h.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("cpp_throw", "C++ throw", false), new ExceptionFilterSetting("cpp_catch", "C++ catch", false)]);
        var rig = new Rig { H = h, Type = type, Binary = binary };
        h.Service.SessionStarted.Subscribe(s => s.Stopped.Subscribe(e => { lock (rig.Stops) rig.Stops.Add(e); }));
        h.Service.Output.Subscribe(e => { lock (rig.Output) rig.Output.Add(e); });
        return rig;
    }

    private static async Task StartAsync(Rig rig)
    {
        var extra = new Dictionary<string, object?> { ["program"] = rig.Binary, ["cwd"] = Path.GetDirectoryName(rig.Binary) };
        if (rig.Type == CodeLldbAdapterFactory.PrimaryType) extra["sourceLanguages"] = new[] { "cpp" };
        rig.Session = (DebugSession)await rig.H.StartAsync(ServiceHarness.Config("cpp", rig.Type, extra: extra, folder: new Uri(Path.GetDirectoryName(rig.Binary) + "/")));
    }

    private static string Render(DebugVariable v) => $"{v.Name} = {v.Value} ({v.Type}) children={v.VariablesReference != 0}";

    public static TheoryData<string> Adapters => [CodeLldbAdapterFactory.PrimaryType, LldbDapAdapterFactory.PrimaryType];

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Libcxx_values_and_the_dynamic_type_read_as_a_user_expects(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.SourceUri, Line("@values"));
        await StartAsync(rig);
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal(Line("@values"), loc.Line);
        var locals = await rig.LocalsAsync();
        foreach (var name in new[] { "text", "numbers", "ages", "names", "some", "none", "var", "unique", "shared", "shape", "raw" })
        {
            var v = locals[name];
            var children = await rig.ChildrenAsync(v);
            Note(type, Render(v) + (children.Count > 0 ? " -> " + string.Join(", ", children.Take(6).Select(c => $"{c.Name}={c.Value}")) : ""));
        }

        Assert.Contains("hello world", locals["text"].Value, StringComparison.Ordinal);
        Assert.Contains("size=3", locals["numbers"].Value, StringComparison.Ordinal);
        Assert.Equal(["1", "2", "3"], (await rig.ChildrenAsync(locals["numbers"])).Select(c => c.Value).Take(3));
        Assert.Contains("size=2", locals["ages"].Value, StringComparison.Ordinal);
        Assert.Contains("size=1", locals["names"].Value, StringComparison.Ordinal);
        var someChildren = await rig.ChildrenAsync(locals["some"]);
        Assert.True(locals["some"].Value.Contains('5', StringComparison.Ordinal) || someChildren.Any(c => c.Value == "5"), Render(locals["some"]));
        Assert.Contains("alt", string.Join(" ", new[] { locals["var"].Value }.Concat((await rig.ChildrenAsync(locals["var"])).Select(c => c.Value))), StringComparison.Ordinal);
        var uniqueText = locals["unique"].Value + " " + string.Join(" ", (await rig.ChildrenAsync(locals["unique"])).Select(c => c.Value));
        Assert.Contains("7", uniqueText, StringComparison.Ordinal);
        var sharedText = locals["shared"].Value + " " + string.Join(" ", (await rig.ChildrenAsync(locals["shared"])).Select(c => c.Value));
        Assert.Contains("shared", sharedText, StringComparison.Ordinal);

        // the dynamic type of a Shape* that points at a Circle: its fields are reachable (radius), and the type names Circle somewhere
        var rawText = await DescribeDeepAsync(rig, locals["raw"], 2);
        Note(type, "raw (Shape* to a Circle), two levels: " + rawText);
        Assert.Contains("radius", rawText, StringComparison.Ordinal);
        var evaluated = await rig.Session!.EvaluateAsync("*raw", loc.FrameId, "watch", Ct);
        Note(type, $"evaluate *raw => {evaluated.Result} ({evaluated.Type})");

        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await rig.RunToEndAsync(type);
        Assert.Contains("done 6 11 3 12", rig.OutputText, StringComparison.Ordinal);
    }

    private static async Task<string> DescribeDeepAsync(Rig rig, DebugVariable v, int depth)
    {
        var text = $"{v.Name}={v.Value} ({v.Type})";
        if (depth == 0 || v.VariablesReference == 0) return text;
        var children = await rig.ChildrenAsync(v);
        var inner = new List<string>();
        foreach (var c in children.Take(8)) inner.Add(await DescribeDeepAsync(rig, c, depth - 1));
        return text + " { " + string.Join(", ", inner) + " }";
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Template_and_lambda_frames_are_named_in_the_call_stack(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.SourceUri, Line("@template"));
        await StartAsync(rig);
        var loc = await rig.WaitForStopAsync(1);
        var frames = await rig.Session!.GetStackTraceAsync(loc.ThreadId, 0, 10, Ct);
        Note(type, "call stack in twice<int> from the lambda: " + string.Join(" | ", frames.Take(4).Select(f => $"{f.Name} @{Path.GetFileName(f.Source?.Path)}:{f.Line}")));
        Assert.Contains("twice<int>", frames[0].Name, StringComparison.Ordinal);
        Assert.Contains(frames.Skip(1).Take(2), f => f.Name.Contains("lambda", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("$_", StringComparison.Ordinal) || f.Name.Contains("operator()", StringComparison.Ordinal));
        Assert.Contains(frames, f => f.Name.Contains("main", StringComparison.Ordinal));
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await rig.RunToEndAsync(type);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task The_cpp_throw_and_catch_filters_stop_where_the_exception_is_thrown_and_caught(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("cpp_throw", "C++: on throw", true), new ExceptionFilterSetting("cpp_catch", "C++: on catch", true)]);
        await StartAsync(rig);
        var filters = rig.Session!.Capabilities.ExceptionBreakpointFilters ?? [];
        Note(type, "exception filters: " + string.Join(", ", filters.Select(f => $"{f.Filter}='{f.Label}' default={f.Default}")));
        Assert.Contains(filters, f => f.Filter == "cpp_throw");
        Assert.Contains(filters, f => f.Filter == "cpp_catch");

        var loc = await rig.WaitForStopAsync(1);
        var frames = await rig.Session.GetStackTraceAsync(loc.ThreadId, 0, 10, Ct);
        Note(type, $"throw stop: reason={rig.LastStop.Reason} description={rig.LastStop.Description} text={rig.LastStop.Text}; frames {string.Join(" | ", frames.Take(4).Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line}"))}");
        Assert.Contains(frames.Take(4), f => f.Name.Contains("thrower", StringComparison.Ordinal));
        Assert.Equal("exception", rig.LastStop.Reason);

        await rig.ContinueAsync();
        loc = await rig.WaitForStopAsync(2);
        frames = await rig.Session.GetStackTraceAsync(loc.ThreadId, 0, 10, Ct);
        Note(type, $"catch stop: reason={rig.LastStop.Reason} description={rig.LastStop.Description}; frames {string.Join(" | ", frames.Take(4).Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line}"))}");
        Assert.Equal("exception", rig.LastStop.Reason);

        rig.H.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("cpp_throw", "C++: on throw", false), new ExceptionFilterSetting("cpp_catch", "C++: on catch", false)]);
        await Task.Delay(300, Ct);
        await rig.RunToEndAsync(type);
        Assert.Contains("caught boom", rig.OutputText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Std_thread_and_jthread_bodies_stop_on_their_own_threads(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.SourceUri, Line("@thread"));
        rig.H.Breakpoints.Add(rig.SourceUri, Line("@jthread"));
        await StartAsync(rig);
        var seen = new List<int>();
        for (var stop = 1; stop <= 2; stop++)
        {
            var loc = await rig.WaitForStopAsync(stop);
            // lldb-dap's first threads answer after a stop on a new thread can still be the list from before it (the session asks again
            // since stage 3's fix, 390b376 on the C branch): ask a few times, as the Threads window would on its next refresh
            var threads = await rig.Session!.GetThreadsAsync(Ct);
            for (var retry = 0; retry < 10 && threads.All(t => t.Id != loc.ThreadId); retry++)
            {
                Note(type, $"threads answer without the stopped thread {loc.ThreadId}: {string.Join(", ", threads.Select(t => $"{t.Id}:{t.Name}"))}");
                await Task.Delay(100, Ct);
                threads = await rig.Session.GetThreadsAsync(Ct);
            }
            var frames = await rig.Session.GetStackTraceAsync(loc.ThreadId, 0, 4, Ct);
            Note(type, $"stop {stop} at line {loc.Line} on thread {loc.ThreadId}: {string.Join(" | ", frames.Select(f => f.Name))}; threads {string.Join(", ", threads.Select(t => $"{t.Id}:{t.Name}"))}");
            Assert.True(threads.Count >= 2, "a worker thread besides main");
            Assert.NotEqual(threads[0].Id, loc.ThreadId); // not the main thread
            Assert.Contains("operator()", frames[0].Name, StringComparison.Ordinal);
            seen.Add(loc.Line);
            if (stop == 2) rig.H.Breakpoints.RemoveAll();
            if (stop == 1) await rig.ContinueAsync();
        }
        Assert.Equal([Line("@thread"), Line("@jthread")], seen);
        await Task.Delay(300, Ct);
        await rig.RunToEndAsync(type);
        Assert.Contains("done 6 11 3 12", rig.OutputText, StringComparison.Ordinal);
    }
}
