using CsCode.Core.Debugging;
using CsCode.Dap.Protocol;

namespace CsCode.Dap.Session;

// IDebugSessionNative (lang-2): the Core face of readMemory / writeMemory / disassemble / dataBreakpointInfo / instruction stepping, for the
// Disassembly, Memory and Registers windows. The DAP-typed methods in DebugSession.Api.cs stay as they were.
public sealed partial class DebugSession
{
    public bool SupportsWriteMemory => _dapCaps.SupportsWriteMemoryRequest == true;
    public bool SupportsInstructionBreakpoints => _dapCaps.SupportsInstructionBreakpoints == true;
    public bool SupportsSteppingGranularity => _dapCaps.SupportsSteppingGranularity == true;

    async ValueTask<DebugMemoryBlock> IDebugSessionNative.ReadMemoryAsync(string memoryReference, long offset, int count, CancellationToken ct)
    {
        var r = await OnLane(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsReadMemory) throw new NotSupportedException("The debug adapter does not support readMemory.");
            return DapJson.Deserialize<DapReadMemoryResponse>(await RequestAsync("readMemory",
                new DapReadMemoryArguments { MemoryReference = memoryReference, Offset = checked((int)offset), Count = Math.Clamp(count, 0, 1 << 20) }, ct)) ?? new DapReadMemoryResponse();
        }, ct).ConfigureAwait(false);
        // decoded off the lane: the caller's continuation (a UI view model hops to its own thread right after)
        var data = string.IsNullOrEmpty(r.Data) ? [] : Convert.FromBase64String(r.Data);
        DebugAddress.TryParse(r.Address, out var address);
        return new DebugMemoryBlock(address, data, r.UnreadableBytes ?? 0);
    }

    ValueTask<int?> IDebugSessionNative.WriteMemoryAsync(string memoryReference, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var base64 = Convert.ToBase64String(data.Span);
        return OnLane<int?>(async () =>
        {
            EnsureLive();
            if (!SupportsWriteMemory) throw new NotSupportedException("The debug adapter does not support writeMemory.");
            var r = DapJson.Deserialize<DapWriteMemoryResponse>(await RequestAsync("writeMemory",
                new DapWriteMemoryArguments { MemoryReference = memoryReference, Offset = checked((int)offset), AllowPartial = true, Data = base64 }, ct));
            return r?.BytesWritten;
        }, ct);
    }

    ValueTask<IReadOnlyList<DebugInstruction>> IDebugSessionNative.DisassembleAsync(string memoryReference, long offset, int instructionOffset, int instructionCount,
        CancellationToken ct) =>
        OnLane<IReadOnlyList<DebugInstruction>>(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsDisassemble) throw new NotSupportedException("The debug adapter does not support disassemble.");
            var r = DapJson.Deserialize<DapDisassembleResponse>(await RequestAsync("disassemble", new DapDisassembleArguments
            {
                MemoryReference = memoryReference,
                Offset = offset == 0 ? null : checked((int)offset),
                InstructionOffset = instructionOffset,
                InstructionCount = Math.Clamp(instructionCount, 1, 10_000),
                ResolveSymbols = true,
            }, ct)) ?? new DapDisassembleResponse();
            var result = new DebugInstruction[r.Instructions.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var d = r.Instructions[i];
                DebugAddress.TryParse(d.Address, out var address);
                result[i] = new DebugInstruction(address, d.Address, d.Instruction, d.InstructionBytes, d.Symbol, DapMapping.ToCore(d.Location),
                    d.Line is { } l ? _positions.ToCoreLine(l) : null, d.EndLine is { } el ? _positions.ToCoreLine(el) : null)
                {
                    IsInvalid = d.PresentationHint == "invalid" || d.Instruction is "??" or "<invalid>",
                };
            }
            return result;
        }, ct);

    ValueTask IDebugSessionNative.StepAsync(DebugStepAction action, int? threadId, bool instruction, CancellationToken ct)
    {
        var kind = action switch { DebugStepAction.Into => DebugStepKind.Into, DebugStepAction.Out => DebugStepKind.Out, _ => DebugStepKind.Over };
        return StepAsync(kind, threadId, instruction && SupportsSteppingGranularity ? new DebugStepOptions("instruction") : null, ct);
    }

    async ValueTask<DebugDataBreakpointInfo> IDebugSessionNative.GetDataBreakpointInfoAsync(int? variablesReference, string name, int? frameId, CancellationToken ct)
    {
        var r = await DataBreakpointInfoAsync(variablesReference, name, frameId, ct).ConfigureAwait(false);
        return new DebugDataBreakpointInfo(r.DataId, r.Description ?? name, r.AccessTypes ?? [], r.CanPersist == true);
    }
}
