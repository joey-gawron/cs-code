using System.Globalization;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Debugging;

// Native debugging (lang-2): memory, disassembly, instruction stepping, data and instruction breakpoints. Additive companions of
// IDebugSession / IBreakpointManager (new interfaces, as in DebugExtras), so existing implementers and fakes are not broken. CsCode.Dap's
// DebugSession and BreakpointManager implement them; the Disassembly, Memory and Registers windows (CsCode.Panels.Debug) use them, and the
// C, C++ and Zig stages reuse them unchanged.

/// <summary>Bytes the adapter read (DAP <c>readMemory</c>): <see cref="Data"/> starts at <see cref="Address"/>; the <see cref="UnreadableBytes"/>
/// after them could not be read (unmapped memory).</summary>
public sealed record DebugMemoryBlock(ulong Address, ReadOnlyMemory<byte> Data, int UnreadableBytes);

/// <summary>One disassembled instruction (DAP <c>DisassembledInstruction</c>).</summary>
/// <param name="AddressText">The address as the adapter wrote it (<c>0x0000000100003f2c</c>): what instruction breakpoints and memory references use.</param>
/// <param name="Location">The source it was compiled from, when the adapter knows it (with <paramref name="Line"/>, 0-based): the window
/// interleaves source lines.</param>
public sealed record DebugInstruction(ulong Address, string AddressText, string Instruction, string? Bytes = null, string? Symbol = null,
    DebugSource? Location = null, int? Line = null, int? EndLine = null)
{
    /// <summary>The adapter's filler for addresses outside any mapped code (DAP: <c>presentationHint: invalid</c>, or "??" instructions).</summary>
    public bool IsInvalid { get; init; }
}

/// <summary>DAP <c>dataBreakpointInfo</c>: what a data breakpoint on a variable or expression would watch.</summary>
/// <param name="DataId">Null when no data breakpoint can be set there (<paramref name="Description"/> says why).</param>
/// <param name="AccessTypes">The access types the adapter offers (<c>read</c>, <c>write</c>, <c>readWrite</c>); empty: its default only.</param>
/// <param name="CanPersist">The data id stays valid in a later session (most native adapters: false).</param>
public sealed record DebugDataBreakpointInfo(string? DataId, string Description, IReadOnlyList<string> AccessTypes, bool CanPersist);

public enum DebugStepAction { Over, Into, Out }

/// <summary>
/// Optional companion of <see cref="IDebugSession"/>: native debugging. The memory and disassembly calls require the matching
/// <see cref="DebugCapabilities"/> flag (<see cref="DebugCapabilities.SupportsReadMemory"/>, <see cref="DebugCapabilities.SupportsDisassemble"/>,
/// <see cref="DebugCapabilities.SupportsDataBreakpoints"/>) or the flags below, and throw <see cref="NotSupportedException"/> without it. Every
/// call is one enqueue onto the debug lane; memory references are the adapter's (a variable's <see cref="DebugVariable.MemoryReference"/>, a
/// frame's <see cref="DebugStackFrame.InstructionPointerReference"/>, or an address such as <c>0x1000</c>).
/// </summary>
public interface IDebugSessionNative
{
    /// <summary>DAP <c>supportsWriteMemoryRequest</c>.</summary>
    bool SupportsWriteMemory { get; }

    /// <summary>DAP <c>supportsInstructionBreakpoints</c>.</summary>
    bool SupportsInstructionBreakpoints { get; }

    /// <summary>DAP <c>supportsSteppingGranularity</c>: <see cref="StepAsync"/> can step by instruction.</summary>
    bool SupportsSteppingGranularity { get; }

    /// <summary>Reads <paramref name="count"/> bytes (at most 1 MiB) at <paramref name="memoryReference"/> + <paramref name="offset"/>.</summary>
    ValueTask<DebugMemoryBlock> ReadMemoryAsync(string memoryReference, long offset, int count, CancellationToken ct = default);

    /// <summary>Writes <paramref name="data"/> at <paramref name="memoryReference"/> + <paramref name="offset"/>; returns the bytes written (the
    /// adapter may write fewer), null when it did not say.</summary>
    ValueTask<int?> WriteMemoryAsync(string memoryReference, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="instructionCount"/> instructions (at most 10,000) starting <paramref name="instructionOffset"/> instructions after (negative:
    /// before) <paramref name="memoryReference"/> + <paramref name="offset"/> bytes, with symbols. The adapter pads with invalid instructions
    /// where there is no code, so the count is always what was asked (unless it fails).
    /// </summary>
    ValueTask<IReadOnlyList<DebugInstruction>> DisassembleAsync(string memoryReference, long offset, int instructionOffset, int instructionCount, CancellationToken ct = default);

    /// <summary>Steps the thread (default: the focused one) by one instruction when <paramref name="instruction"/> and the adapter supports it,
    /// else by statement, as <see cref="IDebugSession.StepOverAsync"/> and its siblings do.</summary>
    ValueTask StepAsync(DebugStepAction action, int? threadId, bool instruction, CancellationToken ct = default);

    /// <summary>What a data breakpoint on <paramref name="name"/> would watch: a child of <paramref name="variablesReference"/>, or (null) an
    /// expression evaluated in <paramref name="frameId"/>.</summary>
    ValueTask<DebugDataBreakpointInfo> GetDataBreakpointInfoAsync(int? variablesReference, string name, int? frameId, CancellationToken ct = default);
}

/// <summary>A data breakpoint ("Break When Value Changes"): the program stops when the watched memory is accessed.</summary>
/// <param name="AccessType"><c>write</c> (the default), <c>read</c> or <c>readWrite</c>.</param>
public sealed record DataBreakpoint(string Id, string DataId, string Description, string AccessType = "write", bool Enabled = true,
    string? Condition = null, string? HitCondition = null)
{
    /// <summary>The adapter said the data id stays valid across sessions; otherwise the breakpoint ends with <see cref="SessionId"/>.</summary>
    public bool CanPersist { get; init; }
    /// <summary>The session the data id belongs to; it is sent to that session only, and removed when it ends (unless <see cref="CanPersist"/>).</summary>
    public string? SessionId { get; init; }
}

/// <summary>A breakpoint on one instruction (the Disassembly window), sent with DAP <c>setInstructionBreakpoints</c>.</summary>
/// <param name="InstructionReference">The instruction's address as the adapter wrote it.</param>
/// <param name="Label">How the Breakpoints window names it (<c>main+12 (0x100003f2c)</c>).</param>
public sealed record InstructionBreakpoint(string Id, string InstructionReference, int Offset = 0, bool Enabled = true, string? Condition = null,
    string? HitCondition = null, string? Label = null)
{
    /// <summary>The session whose addresses it uses (load addresses change between runs): sent to it only, removed when it ends.</summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// Optional companion of <see cref="IBreakpointManager"/> (CsCode.Dap's BreakpointManager implements it): data and instruction breakpoints.
/// They are tied to a debug session's memory, so they are not persisted; each is sent to its session only, and its verdict arrives through
/// <see cref="IBreakpointManager.GetStatus"/> like any breakpoint's. Every member may be called from any thread.
/// </summary>
public interface INativeBreakpointManager
{
    IReadOnlyList<DataBreakpoint> DataBreakpoints { get; }
    IReadOnlyList<InstructionBreakpoint> InstructionBreakpoints { get; }

    /// <summary>Adds a data breakpoint (an existing one on the same data id and access type is returned instead).</summary>
    DataBreakpoint AddData(string dataId, string description, string? sessionId, bool canPersist, string accessType = "write");

    /// <summary>Adds the instruction breakpoint, or removes the one already at that address; returns the added one, null when it removed one.</summary>
    InstructionBreakpoint? ToggleInstruction(string instructionReference, string? label, string? sessionId);

    /// <summary>Enables or disables a data or instruction breakpoint.</summary>
    void SetNativeEnabled(string id, bool enabled);

    /// <summary>Removes a data or instruction breakpoint.</summary>
    void RemoveNative(string id);

    /// <summary>A session ended: its instruction breakpoints and its data breakpoints that cannot persist go.</summary>
    void RemoveSessionBreakpoints(string sessionId);

    /// <summary>Data or instruction breakpoints were added, removed or changed (any thread).</summary>
    IEventSource<object?> NativeChanged { get; }
}

/// <summary>Memory addresses as DAP writes them (<c>0x…</c> hex, or decimal).</summary>
public static class DebugAddress
{
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
            : ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out address);
    }

    /// <summary><c>0x</c> + 16 hex digits (64-bit) or 8 (when it fits and <paramref name="wide"/> is false).</summary>
    public static string Format(ulong address, bool wide = true) =>
        "0x" + address.ToString(wide || address > uint.MaxValue ? "x16" : "x8", CultureInfo.InvariantCulture);
}
