using System.Globalization;
using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>
/// A session with native debugging (<see cref="IDebugSessionNative"/>), like CodeLLDB's or lldb-dap's through the DAP module: memory whose
/// byte at address a is <c>a % 251</c> (unreadable below <see cref="MappedFrom"/>), 4-byte instructions anywhere (function <c>demo::main</c> from
/// <see cref="CodeBase"/>, one source line per four instructions in <c>/w/src/main.rs</c>), and recorded requests.
/// </summary>
public sealed class FakeNativeDebugSession : FakeDebugSession, IDebugSessionNative
{
    public const ulong CodeBase = 0x100000;
    public const ulong MappedFrom = 0x10000;

    public FakeNativeDebugSession(string id = "s1") : base(id, "Native")
    {
        Capabilities = Capabilities with { SupportsReadMemory = true, SupportsDisassemble = true, SupportsDataBreakpoints = true };
    }

    public bool SupportsWriteMemory { get; set; } = true;
    public bool SupportsInstructionBreakpoints { get; set; } = true;
    public bool SupportsSteppingGranularity { get; set; } = true;
    /// <summary>How long memory and disassembly requests take (0: answered at once).</summary>
    public TimeSpan NativeDelay { get; set; }

    public List<(string Reference, long Offset, int Count)> MemoryReads { get; } = [];
    public List<(string Reference, long Offset, byte[] Data)> MemoryWrites { get; } = [];
    public List<(string Reference, int InstructionOffset, int Count)> DisassembleRequests { get; } = [];
    public List<(DebugStepAction Action, bool Instruction)> Steps { get; } = [];
    public List<(int? Reference, string Name, int? Frame)> DataInfoRequests { get; } = [];
    public Dictionary<ulong, byte> Written { get; } = new();

    private static ulong Address(string reference) =>
        DebugAddress.TryParse(reference, out var a) ? a : throw new InvalidOperationException("unknown memory reference " + reference);

    public ValueTask<DebugMemoryBlock> ReadMemoryAsync(string memoryReference, long offset, int count, CancellationToken ct = default)
    {
        lock (MemoryReads) MemoryReads.Add((memoryReference, offset, count));
        var start = unchecked(Address(memoryReference) + (ulong)offset);
        var data = new List<byte>(count);
        var unreadable = 0;
        for (var i = 0; i < count; i++)
        {
            var a = start + (ulong)i;
            if (a < MappedFrom) { unreadable = count - i; break; }
            data.Add(Written.TryGetValue(a, out var w) ? w : (byte)(a % 251));
        }
        return ValueTask.FromResult(new DebugMemoryBlock(start, data.ToArray(), unreadable));
    }

    public ValueTask<int?> WriteMemoryAsync(string memoryReference, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        lock (MemoryWrites) MemoryWrites.Add((memoryReference, offset, data.ToArray()));
        var start = unchecked(Address(memoryReference) + (ulong)offset);
        for (var i = 0; i < data.Length; i++) Written[start + (ulong)i] = data.Span[i];
        return ValueTask.FromResult<int?>(data.Length);
    }

    public ValueTask<IReadOnlyList<DebugInstruction>> DisassembleAsync(string memoryReference, long offset, int instructionOffset, int instructionCount, CancellationToken ct = default)
    {
        lock (DisassembleRequests) DisassembleRequests.Add((memoryReference, instructionOffset, instructionCount));
        if (NativeDelay > TimeSpan.Zero) return Delayed(DisassembleAsyncCore(memoryReference, offset, instructionOffset, instructionCount));
        return ValueTask.FromResult(DisassembleAsyncCore(memoryReference, offset, instructionOffset, instructionCount));
    }

    private async ValueTask<T> Delayed<T>(T value)
    {
        await Task.Delay(NativeDelay);
        return value;
    }

    private IReadOnlyList<DebugInstruction> DisassembleAsyncCore(string memoryReference, long offset, int instructionOffset, int instructionCount)
    {
        var anchor = unchecked(Address(memoryReference) + (ulong)offset);
        var list = new List<DebugInstruction>(instructionCount);
        for (var i = 0; i < instructionCount; i++)
        {
            var a = unchecked(anchor + (ulong)((long)(instructionOffset + i) * 4));
            var inMain = a >= CodeBase && a < CodeBase + 0x400;
            list.Add(new DebugInstruction(a, "0x" + a.ToString("x", CultureInfo.InvariantCulture), inMain ? $"add x{i % 8}, x1, #{i}" : "nop", "00 00 00 91",
                inMain ? "demo::main" : null, inMain ? new DebugSource("main.rs", "/w/src/main.rs") : null, inMain ? (int)((a - CodeBase) / 16) : null));
        }
        return list;
    }

    public ValueTask StepAsync(DebugStepAction action, int? threadId, bool instruction, CancellationToken ct = default)
    {
        lock (Steps) Steps.Add((action, instruction));
        return ValueTask.CompletedTask;
    }

    public ValueTask<DebugDataBreakpointInfo> GetDataBreakpointInfoAsync(int? variablesReference, string name, int? frameId, CancellationToken ct = default)
    {
        lock (DataInfoRequests) DataInfoRequests.Add((variablesReference, name, frameId));
        return ValueTask.FromResult(name == "unwatchable"
            ? new DebugDataBreakpointInfo(null, "the value is not in memory", [], false)
            : new DebugDataBreakpointInfo($"{name}@{variablesReference ?? frameId}", $"{name} (4 bytes)", ["write", "read"], false));
    }
}
