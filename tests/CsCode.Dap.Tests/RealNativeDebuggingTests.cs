using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The native debugging surface (lang-2: what the Disassembly, Memory and Registers windows and data breakpoints use) against the REAL CodeLLDB
/// and lldb-dap, through the real <see cref="CsCode.Dap.Service.DebugService"/>, on the cargo sample: the instruction pointer and the
/// disassembly around it, a step by instruction, a <c>Vec</c>'s buffer read from its element's memory reference, the register scope, and a
/// data breakpoint that stops when the value changes. Skips with its reason when the adapter, cargo or rustc is missing, or when macOS
/// Developer Mode is off (starting a debuggee would wait for an administrator in a dialog).
/// </summary>
[Trait("Category", "RealAdapter")]
[Collection(SerialCollection.Name)]
public class RealNativeDebuggingTests
{
    private static void Note(string text) => TestMetrics.Report("[native] " + text);

    public static TheoryData<string> Adapters => new() { CodeLldbAdapterFactory.PrimaryType, LldbDapAdapterFactory.PrimaryType };

    private static async Task<ServiceHarness> HarnessOrSkipAsync(string type)
    {
        Assert.SkipWhen(GdbToolchain.FindCargo() is null || GdbToolchain.FindRustc() is null, "the Rust toolchain ('cargo', 'rustc') is not on PATH");
        string? adapter;
        if (type == CodeLldbAdapterFactory.PrimaryType)
        {
            adapter = CodeLldbToolchain.FindAdapter();
            Assert.SkipWhen(adapter is null, $"CodeLLDB {CodeLldbToolchain.PinnedVersion} is not installed: run tools/fetch-codelldb.sh");
        }
        else
        {
            adapter = await new LldbDapAdapterFactory(new Configuration.DapPlatform()).LocateAsync(Ct);
            Assert.SkipWhen(adapter is null, "lldb-dap is not installed (macOS: Xcode or its Command Line Tools)");
        }
        var blocked = await RealLldbDapTests.MacDebuggingBlockedReasonAsync();
        Assert.SkipWhen(blocked is not null, blocked ?? "");
        var h = type == CodeLldbAdapterFactory.PrimaryType
            ? new ServiceHarness(moreBuiltins: p => CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p)))
            : new ServiceHarness(moreBuiltins: p => LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p)));
        h.Settings.Set(type == CodeLldbAdapterFactory.PrimaryType ? CodeLldbAdapterFactory.SettingPath : LldbDapAdapterFactory.SettingPath, adapter!);
        return h;
    }

    private static async Task<(ServiceHarness H, RustSample Sample, DebugSession Session, DebugStopLocation Stop)> StopAtLoopAsync(string type)
    {
        var h = await HarnessOrSkipAsync(type);
        var sample = await RustSample.SharedAsync(Ct);
        // at the file's physical path: on a Mac the temp folder (/var/folders/…) is a link into /private, and the debug info has the physical path
        h.Breakpoints.Add(new Uri(TempDirectory.PhysicalPath(sample.MainRs)), RustSample.MainLine("loop"));
        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("rust", type, extra: new Dictionary<string, object?>
        {
            ["program"] = sample.Binary,
            ["cwd"] = sample.Dir.Path,
        }, folder: sample.Dir.Uri));
        await SessionHarness.Eventually(() => h.Service.StopLocation is { Session.State: DebugSessionState.Stopped }, 60_000, "the first stop");
        var stop = h.Service.StopLocation!;
        var frames = await session.GetStackTraceAsync(stop.ThreadId, 0, 4, Ct);
        Note($"{type}: first stop {stop.Reason} at {stop.Uri?.LocalPath}:{stop.Line + 1} (thread {stop.ThreadId}, frame {stop.FrameId}); top frames "
             + string.Join(" | ", frames.Select(f => $"{f.Id} {f.Name} {f.Source?.Name}:{f.Line + 1} ip={f.InstructionPointerReference} hint={f.PresentationHint}")));
        return (h, sample, session, stop);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task The_instruction_pointer_disassembles_with_source_lines_and_an_instruction_step_moves_it(string type)
    {
        var (h, _, session, stop) = await StopAtLoopAsync(type);
        await using var _h = h;
        IDebugSessionNative native = session;
        Assert.True(session.Capabilities.SupportsDisassemble && native.SupportsSteppingGranularity && native.SupportsInstructionBreakpoints);
        var frames = await session.GetStackTraceAsync(stop.ThreadId, 0, 5, Ct);
        var ip = frames[0].InstructionPointerReference;
        Assert.NotNull(ip);
        Assert.True(DebugAddress.TryParse(ip, out var pc), ip);
        var code = await native.DisassembleAsync(ip!, 0, -8, 16, Ct);
        Note($"{type}: ip {ip}; {code.Count} instructions, first {code[0].AddressText} {code[0].Instruction} ({code[0].Symbol}) at {code[0].Location?.Name}:{code[0].Line + 1}");
        Assert.Contains(code, i => i.Address == pc);
        Assert.Contains(code, i => i.Location?.Path?.EndsWith("main.rs", StringComparison.Ordinal) == true && i.Line is not null);
        // the Disassembly window's first page: 64 instructions each side
        var page = await native.DisassembleAsync(ip!, 0, -64, 128, Ct);
        Note($"{type}: window page: {page.Count} instructions {page.FirstOrDefault()?.AddressText}..{page.LastOrDefault()?.AddressText}, pc at index {page.ToList().FindIndex(i => i.Address == pc)}, invalid {page.Count(i => i.IsInvalid)}");
        Assert.Contains(page, i => i.Address == pc);

        await native.StepAsync(DebugStepAction.Over, stop.ThreadId, instruction: true, Ct);
        await SessionHarness.Eventually(() => h.Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && !ReferenceEquals(l, stop), 30_000, "the step's stop");
        var after = await session.GetStackTraceAsync(stop.ThreadId, 0, 1, Ct);
        Assert.True(DebugAddress.TryParse(after[0].InstructionPointerReference, out var next));
        Note($"{type}: one instruction step: {DebugAddress.Format(pc)} -> {DebugAddress.Format(next)}");
        Assert.NotEqual(pc, next);
        Assert.True(next > pc && next - pc <= 16, $"{DebugAddress.Format(pc)} -> {DebugAddress.Format(next)}"); // the next instruction, not the next line's
        await h.Service.StopAllAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task A_Vec_buffer_reads_from_its_elements_memory_reference_and_the_registers_are_a_scope(string type)
    {
        var (h, _, session, stop) = await StopAtLoopAsync(type);
        await using var _h = h;
        IDebugSessionNative native = session;
        var scopes = await session.GetScopesAsync(stop.FrameId, Ct);
        Note($"{type}: scopes {string.Join(", ", scopes.Select(s => $"{s.Name}(expensive={s.Expensive}, hint={s.PresentationHint})"))}");
        string buffer;
        if (type == CodeLldbAdapterFactory.PrimaryType)
        {
            // CodeLLDB's Rust formatters list a Vec's elements, each with its memory reference: View Binary Data on [0] shows the buffer
            var locals = scopes.First(s => !s.Expensive);
            var v = (await session.GetVariablesAsync(locals.VariablesReference, ct: Ct)).First(x => x.Name == "v");
            var elements = await session.GetVariablesAsync(v.VariablesReference, ct: Ct);
            Note($"{type}: v = {v.Value} (memoryReference {v.MemoryReference}); children {string.Join(", ", elements.Take(5).Select(e => $"{e.Name}={e.Value}@{e.MemoryReference}"))}");
            buffer = elements.First(e => e.Value == "1" && e.MemoryReference is not null).MemoryReference!;
        }
        else
        {
            // lldb-dap has no Rust formatters: the Vec is its raw struct, and the buffer is where the pointer inside it points, which the Memory
            // window's address box takes from the evaluated pointer's value (lldb-dap's memory reference of a pointer is the pointer's own storage)
            var pointer = await session.EvaluateAsync("v.buf.inner.ptr.pointer.pointer", stop.FrameId, "watch", Ct);
            Note($"{type}: v.buf.inner.ptr.pointer.pointer = {pointer.Result} (memoryReference {pointer.MemoryReference})");
            buffer = pointer.Result.TrimStart().Split(' ')[0];
        }
        var block = await native.ReadMemoryAsync(buffer, 0, 12, Ct);
        Assert.Equal([1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0], block.Data.ToArray()); // the buffer of vec![1, 2, 3] (i32, little-endian)

        var registers = Native.RegisterScopes(scopes);
        Assert.NotEmpty(registers);
        var groups = await session.GetVariablesAsync(registers[0].VariablesReference, ct: Ct);
        Note($"{type}: register groups {string.Join(", ", groups.Select(g => g.Name))}");
        Assert.NotEmpty(groups);

        // evidence only: how long the adapter takes to list the frame's locals (lldb-dap timed out at 10 s in the first run)
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var count = (await session.GetVariablesAsync(scopes.First(s => !s.Expensive).VariablesReference, ct: Ct)).Count;
            Note($"{type}: {count} locals listed in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Note($"{type}: listing the locals failed after {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms: {ex.Message}");
        }
        await h.Service.StopAllAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task A_data_breakpoint_on_a_local_stops_when_its_value_changes(string type)
    {
        var (h, _, session, stop) = await StopAtLoopAsync(type);
        await using var _h = h;
        IDebugSessionNative native = session;
        h.Breakpoints.RemoveAll(); // only the data breakpoint stops from here on
        var scopes = await session.GetScopesAsync(stop.FrameId, Ct);
        var locals = scopes.First(s => !s.Expensive);
        var info = await native.GetDataBreakpointInfoAsync(locals.VariablesReference, "x", null, Ct);
        Note($"{type}: dataBreakpointInfo x: id {info.DataId}, '{info.Description}', access [{string.Join(", ", info.AccessTypes)}], canPersist {info.CanPersist}");
        Assert.NotNull(info.DataId);
        var bp = h.Breakpoints.AddData(info.DataId!, info.Description, session.Id, info.CanPersist);
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(bp.Id) is { Verified: true }, 20_000, "the data breakpoint verified");
        var before = (await session.EvaluateAsync("x", stop.FrameId, "watch", Ct)).Result;
        var stopsBefore = h.Service.StopLocation;
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => h.Service.StopLocation is { Session.State: DebugSessionState.Stopped } l && !ReferenceEquals(l, stopsBefore), 30_000, "the data breakpoint's stop");
        var now = h.Service.StopLocation!;
        var after = (await session.EvaluateAsync("x", now.FrameId, "watch", Ct)).Result;
        Note($"{type}: stopped ({now.Reason}) at line {now.Line + 1}; x {before} -> {after}");
        Assert.NotEqual(before, after);
        await h.Service.StopAllAsync(Ct);
    }

    /// <summary>The Registers window's rule (CsCode.Panels.Debug's RegistersViewModel.ForRegisters), restated here: the Dap tests do not reference the panels.</summary>
    private static class Native
    {
        public static IReadOnlyList<DebugScope> RegisterScopes(IReadOnlyList<DebugScope> scopes) =>
            [.. scopes.Where(s => s.VariablesReference > 0 && (s.PresentationHint == "registers" || s.Name.Contains("regist", StringComparison.OrdinalIgnoreCase)))];
    }
}
