using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The Zig debugging matrix (docs/ZIG.md) against the REAL debuggers through the real <see cref="CsCode.Dap.Service.DebugService"/>: a Zig
/// program built by the real zig (<c>zig build-exe</c>, Debug), debugged under CodeLLDB (<c>lldb</c>, the default on macOS) and lldb-dap, each
/// started by its built-in factory with the configuration a <c>build.zig</c> folder gets (Zig's and CsCode's formatters loaded through
/// <c>initCommands</c>). Rows: slices, optionals, error unions, tagged unions, a generic function in the call stack, threads, a panic, and stage 2's native
/// windows (disassembly and an instruction step, memory, registers, a data breakpoint). Skips with
/// its reason without zig (tools/fetch-zig.sh), CodeLLDB (tools/fetch-codelldb.sh), lldb-dap or (macOS) Developer Mode. Values a user reads are
/// asserted; the rest is reported as evidence (<c>CSCODE_DAP_TEST_METRICS=&lt;file&gt;</c>).
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
[Collection(SerialCollection.Name)]
public class RealZigDebuggingTests
{
    private const string Source = """
        const std = @import("std");

        const Shape = union(enum) {
            circle: f32,
            rect: struct { w: u32, h: u32 },
            none,
        };

        fn Pair(comptime T: type) type {
            return struct { a: T, b: T };
        }

        fn sum(comptime T: type, items: []const T) T {
            var total: T = 0;
            for (items) |x| {
                total += x; // @generic
            }
            return total;
        }

        fn parse(text: []const u8) !u32 {
            return std.fmt.parseInt(u32, text, 10);
        }

        fn inspect(numbers: []const i32, name: []const u8, some: ?u32, none: ?u32, good: anyerror!u32, bad: anyerror!u32, shape: Shape, pair: Pair(i32)) u32 {
            var n: u32 = @intCast(numbers.len); // @values
            n += @intCast(name.len);
            if (some) |v| n += v;
            if (none) |v| n += v;
            if (good) |v| n += v else |_| {}
            if (bad) |v| n += v else |_| n += 1;
            switch (shape) {
                .rect => |r| n += r.w * r.h,
                else => {},
            }
            n += @intCast(pair.a + pair.b);
            return n;
        }

        fn worker(id: usize, out: *u64) void {
            out.* = id * 10; // @thread
        }

        pub fn main(init: std.process.Init) !void {
            const args = try init.minimal.args.toSlice(init.arena.allocator());
            const numbers = [_]i32{ 1, 2, 3, 4 };
            const start: usize = if (args.len > 5) 0 else 1;
            const slice: []const i32 = numbers[start..];
            const some: ?u32 = if (args.len > 5) null else 42;
            const none: ?u32 = if (args.len > 5) 7 else null;
            const shape: Shape = if (args.len > 5) .none else .{ .rect = .{ .w = 3, .h = 4 } };
            const total = sum(i32, slice);
            const n = inspect(slice, "zig", some, none, parse("12"), parse("x"), shape, .{ .a = 1, .b = 2 });

            var results = [_]u64{ 0, 0 };
            const t1 = try std.Thread.spawn(.{}, worker, .{ 1, &results[0] });
            const t2 = try std.Thread.spawn(.{}, worker, .{ 2, &results[1] });
            t1.join();
            t2.join();
            std.debug.print("done {d} {d} {d}\n", .{ total, n, results[0] + results[1] });
            if (args.len > 1 and std.mem.eql(u8, args[1], "panic")) @panic("boom"); // @panic
        }
        """;

    private static int Line(string marker) =>
        Source.Split('\n').Select((text, i) => (text, i)).Single(l => l.text.Contains("// @" + marker, StringComparison.Ordinal)).i;

    private static void Note(string type, string text) => TestMetrics.Report($"[zig/{type}] {text}");

    /// <summary>The sample, built once per process by the real zig into a folder that also has a build.zig (so it is a Zig folder).</summary>
    private static readonly Lazy<Task<TempDirectory>> Sample = new(BuildSampleAsync);

    private static async Task<TempDirectory> BuildSampleAsync()
    {
        var dir = new TempDirectory("zigdebug-");
        dir.WriteFile("main.zig", Source);
        dir.WriteFile("build.zig", "const std = @import(\"std\");\npub fn build(b: *std.Build) void { _ = b; }\n");
        var psi = new ProcessStartInfo(ZigTools.FindZig()!) { WorkingDirectory = dir.Path, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "build-exe", "main.zig", "-femit-bin=sample" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, "zig build-exe failed: " + await stderr);
        return dir;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceHarness H { get; init; }
        public required TempDirectory Dir { get; init; }
        public required string Type { get; init; }
        public List<DebugStoppedEventArgs> Stops { get; } = [];
        public List<DebugOutputEventArgs> Output { get; } = [];
        public List<int> Adapters { get; } = [];
        /// <summary>The folder as zig records it in the debug info: macOS's temp directory is behind the /var → /private/var link.</summary>
        public string Root => TempDirectory.PhysicalPath(Dir.Path);
        public Uri MainUri => new(Path.Combine(Root, "main.zig"));
        public string OutputText { get { lock (Output) return string.Concat(Output.Select(o => o.Output)); } }

        public DebugConfiguration Config(params string[] args) => ServiceHarness.Config("zig", Type, "launch", new Dictionary<string, object?>
        {
            ["program"] = Path.Combine(Root, "sample"),
            ["args"] = args,
            ["cwd"] = Root,
        }, new Uri(Root + "/"));

        public async Task<DebugStopLocation> WaitForStopAsync(int count)
        {
            await SessionHarness.Eventually(() => { lock (Stops) return Stops.Count >= count; }, 90_000, $"stop #{count}; output: {OutputText}");
            await SessionHarness.Eventually(() => H.Service.StopLocation is { Session.State: DebugSessionState.Stopped }, 30_000, "stop location");
            return H.Service.StopLocation!;
        }

        public async Task<Dictionary<string, DebugVariable>> LocalsAsync()
        {
            var loc = H.Service.StopLocation!;
            var all = new Dictionary<string, DebugVariable>(StringComparer.Ordinal);
            foreach (var scope in await loc.Session.GetScopesAsync(loc.FrameId, Ct))
                if (scope.PresentationHint is "locals" or "arguments" || scope.Name is "Local" or "Locals" or "Arguments")
                    foreach (var v in await loc.Session.GetVariablesAsync(scope.VariablesReference, ct: Ct))
                        all.TryAdd(v.Name, v);
            return all;
        }

        public async Task EndAsync()
        {
            await H.Service.StopAllAsync(Ct);
            await SessionHarness.Eventually(() => H.Service.Sessions.Count == 0, 30_000, "sessions ended");
            int[] adapters;
            lock (Adapters) adapters = [.. Adapters];
            foreach (var pid in adapters) await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), 15_000, $"adapter {pid} exits");
        }

        public async ValueTask DisposeAsync() => await H.DisposeAsync();
    }

    private static async Task<Rig> RigAsync(string type)
    {
        var noZig = ZigTools.ZigSkipReason();
        Assert.SkipWhen(noZig is not null, noZig ?? "");
        var blocked = await RealLldbDapTests.MacDebuggingBlockedReasonAsync();
        Assert.SkipWhen(blocked is not null, blocked ?? "");
        string? adapter;
        if (type == CodeLldbAdapterFactory.PrimaryType)
        {
            adapter = CodeLldbToolchain.FindAdapter();
            Assert.SkipWhen(adapter is null, $"CodeLLDB {CodeLldbToolchain.PinnedVersion} is not installed: run tools/fetch-codelldb.sh or set {CodeLldbToolchain.EnvironmentVariable}");
        }
        else
        {
            adapter = await new LldbDapAdapterFactory(new Configuration.DapPlatform()).LocateAsync(Ct);
            Assert.SkipWhen(adapter is null, "lldb-dap is not installed (macOS: Xcode or its Command Line Tools; Linux: LLDB from the distribution or LLVM)");
        }
        var dir = await Sample.Value;
        var h = new ServiceHarness(moreBuiltins: p => type == CodeLldbAdapterFactory.PrimaryType
            ? CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p))
            : LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p)));
        h.Settings.Set(type == CodeLldbAdapterFactory.PrimaryType ? CodeLldbAdapterFactory.SettingPath : LldbDapAdapterFactory.SettingPath, adapter!);
        h.Settings.Set(ZigToolchainLocator.SettingPath, ZigTools.FindZig()!);
        var rig = new Rig { H = h, Dir = dir, Type = type };
        h.Service.SessionStarted.Subscribe(s =>
        {
            if (s is DebugSession ds)
                ds.StateChanged.Subscribe(_ => { if (ds.AdapterProcessId is { } pid) lock (rig.Adapters) if (!rig.Adapters.Contains(pid)) rig.Adapters.Add(pid); });
            s.Stopped.Subscribe(e => { lock (rig.Stops) rig.Stops.Add(e); });
        });
        h.Service.Output.Subscribe(e => { lock (rig.Output) rig.Output.Add(e); });
        return rig;
    }

    public static TheoryData<string> Debuggers => [CodeLldbAdapterFactory.PrimaryType, LldbDapAdapterFactory.PrimaryType];

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task Slices_optionals_error_unions_and_tagged_unions_read_by_value(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.MainUri, Line("values"));
        await rig.H.StartAsync(rig.Config());
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal(Line("values"), loc.Line);
        var locals = await rig.LocalsAsync();
        Note(type, "locals: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}={v.Value} ({v.Type})")));
        Note(type, "console: " + string.Concat(rig.Output.Where(o => o.Category == "console").Select(o => o.Output)).Replace("\n", "\\n"));
        Assert.Equal("len=3", locals["numbers"].Value);
        Assert.Equal("\"zig\"", locals["name"].Value);
        Assert.Equal("42", locals["some"].Value);
        Assert.Equal("null", locals["none"].Value);
        Assert.Equal("12", locals["good"].Value);
        Assert.Equal("error.InvalidCharacter", locals["bad"].Value);
        Assert.Equal(".rect = {w = 3, h = 4}", locals["shape"].Value);
        var elements = await loc.Session.GetVariablesAsync(locals["numbers"].VariablesReference, ct: Ct);
        Note(type, "numbers expanded: " + string.Join(" | ", elements.Select(v => $"{v.Name}={v.Value}")));
        // CodeLLDB adds a "[raw]" child to every synthesised value (the fields underneath)
        Assert.Equal(["[0]=2", "[1]=3", "[2]=4"], elements.Where(e => e.Name != "[raw]").Select(e => $"{e.Name}={e.Value}"));
        var payload = await loc.Session.GetVariablesAsync(locals["shape"].VariablesReference, ct: Ct);
        Note(type, "shape expanded: " + string.Join(" | ", payload.Select(v => $"{v.Name}={v.Value}")));
        Assert.Equal("rect", Assert.Single(payload, v => v.Name != "[raw]").Name);

        var watch = await loc.Session.EvaluateAsync("some", loc.FrameId, "watch", Ct);
        Note(type, $"watch 'some' = {watch.Result}; 'numbers' = {(await loc.Session.EvaluateAsync("numbers", loc.FrameId, "watch", Ct)).Result}");
        Assert.Equal("42", watch.Result);
        await rig.EndAsync();
    }

    // ------------------------------------------------------------------ the native windows (stage 2's Disassembly, Memory, Registers, data breakpoints)

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task The_disassembly_around_the_instruction_pointer_has_zig_source_lines_and_an_instruction_step_moves_it(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.MainUri, Line("values"));
        await rig.H.StartAsync(rig.Config());
        var loc = await rig.WaitForStopAsync(1);
        var session = (DebugSession)loc.Session;
        IDebugSessionNative native = session;
        Assert.True(session.Capabilities.SupportsDisassemble && native.SupportsSteppingGranularity);
        var frames = await session.GetStackTraceAsync(loc.ThreadId, 0, 2, Ct);
        var ip = frames[0].InstructionPointerReference;
        Assert.True(DebugAddress.TryParse(ip, out var pc), ip);
        // the Disassembly window's first page: 64 instructions each side of the instruction pointer
        var page = await native.DisassembleAsync(ip!, 0, -64, 128, Ct);
        var at = page.ToList().FindIndex(i => i.Address == pc);
        Note(type, $"disassembly: ip {ip}; {page.Count} instructions, pc at {at}; " + string.Join(" | ", page.Skip(Math.Max(0, at - 2)).Take(4)
            .Select(i => $"{i.AddressText} {i.Instruction} ({i.Symbol}) {i.Location?.Name}:{i.Line + 1}")));
        Assert.True(at >= 0, "the instruction pointer is on the page");
        Assert.Contains(page, i => i.Location?.Path?.EndsWith("main.zig", StringComparison.Ordinal) == true && i.Line == Line("values"));

        await native.StepAsync(DebugStepAction.Over, loc.ThreadId, instruction: true, Ct);
        await SessionHarness.Eventually(() => rig.H.Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && !ReferenceEquals(l, loc), 30_000, "the step's stop");
        var after = await session.GetStackTraceAsync(loc.ThreadId, 0, 1, Ct);
        Assert.True(DebugAddress.TryParse(after[0].InstructionPointerReference, out var next));
        Note(type, $"one instruction step: {DebugAddress.Format(pc)} -> {DebugAddress.Format(next)} (line {after[0].Line + 1})");
        Assert.True(next > pc && next - pc <= 16, $"{DebugAddress.Format(pc)} -> {DebugAddress.Format(next)}"); // the next instruction, not the next line's
        await rig.EndAsync();
    }

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task A_slices_elements_read_from_memory_and_the_registers_are_a_scope(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.MainUri, Line("values"));
        await rig.H.StartAsync(rig.Config());
        var loc = await rig.WaitForStopAsync(1);
        IDebugSessionNative native = (DebugSession)loc.Session;
        var locals = await rig.LocalsAsync();
        var elements = await loc.Session.GetVariablesAsync(locals["numbers"].VariablesReference, ct: Ct);
        var first = elements.First(e => e.Name == "[0]");
        Note(type, $"numbers[0] = {first.Value} (memoryReference {first.MemoryReference})");
        // View Binary Data on an element opens the Memory window at it (both debuggers give the formatted elements their addresses)
        Assert.NotNull(first.MemoryReference);
        var address = first.MemoryReference!;
        var block = await native.ReadMemoryAsync(address, 0, 12, Ct);
        Assert.Equal([2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0], block.Data.ToArray()); // numbers[1..] of { 1, 2, 3, 4 } (i32, little-endian)

        var scopes = await loc.Session.GetScopesAsync(loc.FrameId, Ct);
        var registers = scopes.Where(s => s.VariablesReference > 0 && (s.PresentationHint == "registers" || s.Name.Contains("regist", StringComparison.OrdinalIgnoreCase))).ToList();
        Note(type, "scopes: " + string.Join(", ", scopes.Select(s => $"{s.Name}({s.PresentationHint})")));
        Assert.NotEmpty(registers); // the Registers window's rule (RegistersViewModel.ForRegisters)
        Assert.NotEmpty(await loc.Session.GetVariablesAsync(registers[0].VariablesReference, ct: Ct));
        await rig.EndAsync();
    }

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task A_data_breakpoint_on_a_local_stops_when_its_value_changes(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.MainUri, Line("values"));
        await rig.H.StartAsync(rig.Config());
        var loc = await rig.WaitForStopAsync(1);
        var session = (DebugSession)loc.Session;
        IDebugSessionNative native = session;
        rig.H.Breakpoints.RemoveAll(); // only the data breakpoint stops from here on
        var scope = (await session.GetScopesAsync(loc.FrameId, Ct)).First(s => s.PresentationHint is "locals" || s.Name is "Local" or "Locals");
        var info = await native.GetDataBreakpointInfoAsync(scope.VariablesReference, "n", null, Ct);
        Note(type, $"dataBreakpointInfo n: id {info.DataId}, '{info.Description}', access [{string.Join(", ", info.AccessTypes)}]");
        Assert.NotNull(info.DataId);
        var bp = rig.H.Breakpoints.AddData(info.DataId!, info.Description, session.Id, info.CanPersist);
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true }, 20_000, "the data breakpoint verified");
        await session.ContinueAsync(ct: Ct);
        var now = await rig.WaitForStopAsync(2);
        var n = (await session.EvaluateAsync("n", now.FrameId, "watch", Ct)).Result;
        Note(type, $"stopped ({now.Reason}) at line {now.Line + 1}; n = {n}");
        Assert.Equal("3", n); // numbers.len, the first value written
        Assert.InRange(now.Line, Line("values"), Line("values") + 1); // on the write, or the line after it
        await rig.EndAsync();
    }

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task A_generic_function_shows_in_the_call_stack_under_its_caller(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.MainUri, Line("generic"));
        await rig.H.StartAsync(rig.Config());
        var loc = await rig.WaitForStopAsync(1);
        var frames = await loc.Session.GetStackTraceAsync(loc.ThreadId, 0, 10, Ct);
        Note(type, "generic frames: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1}")));
        Assert.Contains("sum", frames[0].Name, StringComparison.Ordinal);
        Assert.Equal(Line("generic"), frames[0].Line);
        Assert.Contains("main", frames[1].Name, StringComparison.Ordinal);
        Assert.Equal("main.zig", Path.GetFileName(frames[1].Source?.Path));
        await rig.EndAsync();
    }

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task A_breakpoint_in_a_spawned_thread_stops_there_and_lists_the_threads(string type)
    {
        await using var rig = await RigAsync(type);
        rig.H.Breakpoints.Add(rig.MainUri, Line("thread"));
        await rig.H.StartAsync(rig.Config());
        var loc = await rig.WaitForStopAsync(1);
        var first = await loc.Session.GetThreadsAsync(Ct);
        // Apple's lldb-dap answers the first threads request after a stop with the main thread alone and lists every thread a moment later
        // (docs/ZIG.md, debugging matrix): the settled list is what this row decides on, the first one is evidence
        await Task.Delay(1000, Ct);
        var threads = await loc.Session.GetThreadsAsync(Ct);
        var frames = await loc.Session.GetStackTraceAsync(loc.ThreadId, 0, 6, Ct);
        Note(type, $"threads right after the stop: {string.Join(" | ", first.Select(t => $"{t.Id}:{t.Name}"))}; a second later: {string.Join(" | ", threads.Select(t => $"{t.Id}:{t.Name}"))}; stopped thread {loc.ThreadId}: {string.Join(" | ", frames.Select(f => f.Name))}");
        Assert.True(threads.Count >= 3, "the main thread and both workers");
        Assert.Contains(threads, t => t.Id == loc.ThreadId);
        Assert.Contains("worker", frames[0].Name, StringComparison.Ordinal);
        Assert.Equal(Line("thread"), frames[0].Line);
        await rig.EndAsync();
    }

    [Theory]
    [MemberData(nameof(Debuggers))]
    public async Task A_panic_stops_the_program_with_the_panicking_line_in_the_call_stack(string type)
    {
        await using var rig = await RigAsync(type);
        await rig.H.StartAsync(rig.Config("panic"));
        var loc = await rig.WaitForStopAsync(1);
        DebugStoppedEventArgs stop;
        lock (rig.Stops) stop = rig.Stops[0];
        var frames = await loc.Session.GetStackTraceAsync(loc.ThreadId, 0, 30, Ct);
        Note(type, $"panic stop: reason={stop.Reason} description={stop.Description} text={stop.Text}; frames: {string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1}"))}");
        Assert.Contains("boom", rig.OutputText, StringComparison.Ordinal); // the panic message, printed before the abort
        var user = Assert.Single(frames, f => Path.GetFileName(f.Source?.Path) == "main.zig");
        Assert.Equal(Line("panic"), user.Line);
        await rig.EndAsync();
    }
}
