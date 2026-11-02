using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;
using static CsCode.Dap.Tests.TestToken;

namespace CsCode.Dap.Tests;

/// <summary>IDebugSessionNative and the data / instruction breakpoints (lang-2) against the scriptable fake adapter.</summary>
public class NativeDebuggingTests
{
    private static void NativeCapabilities(FakeDebugAdapter a)
    {
        a.Capabilities["supportsReadMemoryRequest"] = true;
        a.Capabilities["supportsWriteMemoryRequest"] = true;
        a.Capabilities["supportsDisassembleRequest"] = true;
        a.Capabilities["supportsDataBreakpoints"] = true;
        a.Capabilities["supportsInstructionBreakpoints"] = true;
        a.Capabilities["supportsSteppingGranularity"] = true;
    }

    [Fact]
    public async Task Memory_is_read_decoded_and_written_back_as_base64()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            NativeCapabilities(a);
            a.On("readMemory", r => new DapReadMemoryResponse { Address = "0x1000", Data = Convert.ToBase64String([1, 2, 3, 0xff]), UnreadableBytes = 4 });
            a.On("writeMemory", r => new DapWriteMemoryResponse { BytesWritten = 2 });
        });
        await h.StopAtBreakpointAsync();
        IDebugSessionNative native = h.Session;
        var block = await native.ReadMemoryAsync("0x1000", 16, 8, Ct);
        Assert.Equal(0x1000UL, block.Address);
        Assert.Equal([1, 2, 3, 0xff], block.Data.ToArray());
        Assert.Equal(4, block.UnreadableBytes);
        var read = h.Adapter.RequestsOf("readMemory")[0].Args<DapReadMemoryArguments>();
        Assert.Equal(("0x1000", 16, 8), (read.MemoryReference, read.Offset, read.Count));

        Assert.True(native.SupportsWriteMemory);
        Assert.Equal(2, await native.WriteMemoryAsync("0x1000", 3, new byte[] { 0xab, 0xcd }, Ct));
        var write = h.Adapter.RequestsOf("writeMemory")[0].Args<DapWriteMemoryArguments>();
        Assert.Equal(("0x1000", 3, Convert.ToBase64String([0xab, 0xcd])), (write.MemoryReference, write.Offset, write.Data));
    }

    [Fact]
    public async Task Without_the_capability_memory_calls_throw_not_supported()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync();
        IDebugSessionNative native = h.Session;
        await Assert.ThrowsAsync<NotSupportedException>(() => native.ReadMemoryAsync("0x1", 0, 4, Ct).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => native.WriteMemoryAsync("0x1", 0, new byte[1], Ct).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => native.DisassembleAsync("0x1", 0, 0, 4, Ct).AsTask());
        Assert.False(native.SupportsInstructionBreakpoints || native.SupportsSteppingGranularity || native.SupportsWriteMemory);
    }

    [Fact]
    public async Task Disassembly_maps_addresses_sources_lines_and_invalid_filler()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            NativeCapabilities(a);
            a.On("disassemble", r => new DapDisassembleResponse
            {
                Instructions =
                [
                    new DapDisassembledInstruction { Address = "0x100003f20", Instruction = "sub sp, sp, #0x20", InstructionBytes = "ff 83 00 d1", Symbol = "demo::main",
                        Location = new DapSource { Name = "main.rs", Path = "/w/src/main.rs" }, Line = 7 },
                    new DapDisassembledInstruction { Address = "0x100003f24", Instruction = "??", PresentationHint = "invalid" },
                ],
            });
        });
        await h.StopAtBreakpointAsync();
        var list = await ((IDebugSessionNative)h.Session).DisassembleAsync("0x100003f20", 0, -10, 2, Ct);
        Assert.Equal(0x100003f20UL, list[0].Address);
        Assert.Equal(("demo::main", "/w/src/main.rs", 6), (list[0].Symbol, list[0].Location?.Path, list[0].Line)); // 0-based
        Assert.False(list[0].IsInvalid);
        Assert.True(list[1].IsInvalid);
        var args = h.Adapter.RequestsOf("disassemble")[0].Args<DapDisassembleArguments>();
        Assert.Equal((-10, 2, true, (int?)null), (args.InstructionOffset!.Value, args.InstructionCount, args.ResolveSymbols == true, args.Offset));
    }

    [Fact]
    public async Task Frames_carry_their_instruction_pointer_reference()
    {
        await using var h = await SessionHarness.StartAsync(a =>
            a.Frames = [new DapStackFrame { Id = 100, Name = "main", Line = 1, Column = 1, InstructionPointerReference = "0x100003f2c" }]);
        await h.StopAtBreakpointAsync();
        var frames = await h.Session.GetStackTraceAsync(1, ct: Ct);
        Assert.Equal("0x100003f2c", frames[0].InstructionPointerReference);
    }

    [Theory]
    [InlineData(DebugStepAction.Over, "next")]
    [InlineData(DebugStepAction.Into, "stepIn")]
    [InlineData(DebugStepAction.Out, "stepOut")]
    public async Task Instruction_steps_send_granularity_only_when_supported(DebugStepAction action, string command)
    {
        await using var h = await SessionHarness.StartAsync(NativeCapabilities);
        await h.StopAtBreakpointAsync();
        await ((IDebugSessionNative)h.Session).StepAsync(action, null, instruction: true, Ct);
        Assert.Equal("instruction", h.Adapter.RequestsOf(command)[0].Args<DapStepArguments>().Granularity);

        await using var plain = await SessionHarness.StartAsync();
        await plain.StopAtBreakpointAsync();
        await ((IDebugSessionNative)plain.Session).StepAsync(action, null, instruction: true, Ct);
        Assert.Null(plain.Adapter.RequestsOf(command)[0].Args<DapStepArguments>().Granularity);
    }

    [Fact]
    public async Task Data_breakpoint_info_maps_the_answer()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            NativeCapabilities(a);
            a.On("dataBreakpointInfo", r => new DapDataBreakpointInfoResponse { DataId = "0x16fdff0c8/4", Description = "x (4 bytes)", AccessTypes = ["write", "read"], CanPersist = false });
        });
        await h.StopAtBreakpointAsync();
        var info = await ((IDebugSessionNative)h.Session).GetDataBreakpointInfoAsync(1000, "x", null, Ct);
        Assert.Equal(("0x16fdff0c8/4", "x (4 bytes)", false), (info.DataId, info.Description, info.CanPersist));
        Assert.Equal(["write", "read"], info.AccessTypes);
        var args = h.Adapter.RequestsOf("dataBreakpointInfo")[0].Args<DapDataBreakpointInfoArguments>();
        Assert.Equal((1000, "x"), (args.VariablesReference!.Value, args.Name));
    }

    [Fact]
    public async Task Data_and_instruction_breakpoints_go_to_their_session_and_report_verdicts()
    {
        await using var h = await SessionHarness.StartAsync(a =>
        {
            NativeCapabilities(a);
            a.On("setDataBreakpoints", r => new DapBreakpointsResponse
            {
                Breakpoints = [.. r.Args<DapSetDataBreakpointsArguments>().Breakpoints.Select((b, i) => new DapBreakpoint { Id = 50 + i, Verified = true })],
            });
            a.On("setInstructionBreakpoints", r => new DapBreakpointsResponse
            {
                Breakpoints = [.. r.Args<DapSetInstructionBreakpointsArguments>().Breakpoints.Select((b, i) => new DapBreakpoint { Id = 60 + i, Verified = i == 0, Message = i == 0 ? null : "no code" })],
            });
        });
        await h.StopAtBreakpointAsync();
        INativeBreakpointManager native = h.Breakpoints;
        var data = native.AddData("0x16fdff0c8/4", "x", h.Session.Id, canPersist: false);
        var other = native.AddData("0xdead/8", "y", "another-session", canPersist: false);
        await h.Adapter.WaitForRequestAsync("setDataBreakpoints");
        var sent = h.Adapter.RequestsOf("setDataBreakpoints")[^1].Args<DapSetDataBreakpointsArguments>();
        Assert.Equal(["0x16fdff0c8/4"], sent.Breakpoints.Select(b => b.DataId)); // not the other session's
        Assert.Equal("write", sent.Breakpoints[0].AccessType);
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(data.Id) is { Verified: true }, what: "data breakpoint verified");
        Assert.Null(h.Breakpoints.GetStatus(other.Id));

        var ib = native.ToggleInstruction("0x100003f2c", "main+12", h.Session.Id)!;
        await h.Adapter.WaitForRequestAsync("setInstructionBreakpoints");
        Assert.Equal("0x100003f2c", h.Adapter.RequestsOf("setInstructionBreakpoints")[^1].Args<DapSetInstructionBreakpointsArguments>().Breakpoints.Single().InstructionReference);
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(ib.Id) is { Verified: true }, what: "instruction breakpoint verified");

        Assert.Null(native.ToggleInstruction("0x0000000100003f2c", null, h.Session.Id)); // the same address written wider: removed
        await h.Adapter.WaitForRequestAsync("setInstructionBreakpoints", 2);
        Assert.Empty(h.Adapter.RequestsOf("setInstructionBreakpoints")[^1].Args<DapSetInstructionBreakpointsArguments>().Breakpoints);

        native.SetNativeEnabled(data.Id, false);
        await h.Adapter.WaitForRequestAsync("setDataBreakpoints", 2);
        Assert.Empty(h.Adapter.RequestsOf("setDataBreakpoints")[^1].Args<DapSetDataBreakpointsArguments>().Breakpoints);
    }

    [Fact]
    public async Task A_data_breakpoint_changed_while_stopped_reaches_the_adapter_before_the_resume()
    {
        // a debounce far longer than the test: only the flush before a resume can send it in time
        await using var h = await SessionHarness.StartAsync(NativeCapabilities,
            settings: new DebugSessionSettings { RequestTimeout = TimeSpan.FromSeconds(5), StopTimeout = TimeSpan.FromSeconds(2), BreakpointDebounce = TimeSpan.FromSeconds(30) });
        await h.StopAtBreakpointAsync();
        ((INativeBreakpointManager)h.Breakpoints).AddData("0x16fdff0c8/4", "x", h.Session.Id, canPersist: false);
        await h.Session.ContinueAsync(ct: Ct);
        var received = h.Adapter.Received;
        var sent = received.ToList().FindIndex(r => r.Command == "setDataBreakpoints"
            && r.Args<DapSetDataBreakpointsArguments>().Breakpoints.Any(b => b.DataId == "0x16fdff0c8/4"));
        var resumed = received.ToList().FindLastIndex(r => r.Command == "continue");
        Assert.True(sent >= 0 && sent < resumed, string.Join(", ", received.Select(r => r.Command)));
    }

    [Fact]
    public async Task An_adapter_without_data_breakpoints_says_so_on_the_breakpoint()
    {
        await using var h = await SessionHarness.StartAsync();
        await h.StopAtBreakpointAsync();
        var data = ((INativeBreakpointManager)h.Breakpoints).AddData("id", "x", h.Session.Id, false);
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(data.Id) is { Verified: false, Message: not null }, what: "unsupported verdict");
        Assert.Contains("does not support data breakpoints", h.Breakpoints.GetStatus(data.Id)!.Message);
        Assert.Equal(0, h.Adapter.CountOf("setDataBreakpoints"));
    }

    [Fact]
    public void Session_breakpoints_end_with_their_session_unless_they_persist_and_are_never_saved()
    {
        var storage = new FakeStateStorage();
        using var manager = new BreakpointManager(storage);
        var fired = 0;
        using var sub = manager.NativeChanged.Subscribe(_ => Interlocked.Increment(ref fired));
        manager.AddData("a", "a", "s1", canPersist: false);
        var kept = manager.AddData("b", "b", "s1", canPersist: true);
        manager.ToggleInstruction("0x10", null, "s1");
        var otherSession = manager.ToggleInstruction("0x20", null, "s2")!;
        Assert.Same(kept, manager.AddData("b", "b", "s1", canPersist: true)); // the same data id is not added twice
        manager.RemoveSessionBreakpoints("s1");
        Assert.Equal([kept], manager.DataBreakpoints);
        Assert.Equal([otherSession], manager.InstructionBreakpoints);
        manager.SetEnabledAll(false);
        Assert.False(manager.DataBreakpoints.Single().Enabled);
        manager.RemoveAll();
        Assert.Empty(manager.DataBreakpoints);
        Assert.Empty(manager.InstructionBreakpoints);
        Assert.True(fired >= 6, $"NativeChanged fired {fired} times");
        Assert.DoesNotContain("\"b\"", JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(manager.SourceBreakpoints))!.ToJsonString());
    }

    [Theory]
    [InlineData("0x1000", true, 0x1000UL)]
    [InlineData("0X00000001000", true, 0x1000UL)]
    [InlineData("4096", true, 4096UL)]
    [InlineData("zz", false, 0UL)]
    [InlineData("", false, 0UL)]
    public void Addresses_parse_in_hex_and_decimal(string text, bool ok, ulong value)
    {
        Assert.Equal(ok, DebugAddress.TryParse(text, out var address));
        Assert.Equal(value, address);
        if (ok) Assert.Equal("0x0000000000001000", DebugAddress.Format(address));
    }
}
