using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Native;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>The Disassembly, Memory and Registers windows and the data-breakpoint menu path (lang-2), against a native-capable fake session.</summary>
public class NativeWindowsTests
{
    private const string Ip = "0x100040";

    private static FakeNativeDebugSession NativeSession()
    {
        var s = new FakeNativeDebugSession();
        s.WithDefaultScript();
        s.Frames[1] =
        [
            new DebugStackFrame(100, "demo::main", new DebugSource("main.rs", "/w/src/main.rs"), 4, 0) { InstructionPointerReference = Ip },
            new DebugStackFrame(101, "std::rt::lang_start", null, 0, 0) { InstructionPointerReference = "0x200000" },
        ];
        s.Scopes[100] =
        [
            new DebugScope("Local", 1000, false),
            new DebugScope("Registers", 3000, true),
        ];
        s.Variables[1000] = [new DebugVariable("v", "size=3", "Vec<i32>", 2000, "v", 3, 0, MemoryReference: "0x10000"), new DebugVariable("x", "41", "int", EvaluateName: "x")];
        s.Variables[3000] = [new DebugVariable("General Purpose Registers", "", null, 3100)];
        s.Variables[3100] = [new DebugVariable("x0", "0x0000000000000001", "unsigned long"), new DebugVariable("pc", "0x0000000000100040", "unsigned long")];
        return s;
    }

    // ------------------------------------------------------------------ Disassembly

    [AvaloniaFact]
    public async Task Disassembly_loads_around_the_instruction_pointer_and_marks_it()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var vm = new DisassemblyViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Any(r => r.IsCurrent), what: "current instruction");
        var current = vm.Rows.Single(r => r.IsCurrent);
        Assert.Equal(0x100040UL, current.Address);
        Assert.Equal(("0x100040", -DisassemblyViewModel.PageSize, 2 * DisassemblyViewModel.PageSize), session.DisassembleRequests.Single());
        // the function and source lines its instructions come from are interleaved
        Assert.Contains(vm.Rows, r => r.IsSymbol && r.Text == "demo::main:");
        Assert.Contains(vm.Rows, r => r.IsSource && r.Text.StartsWith("main.rs:5", StringComparison.Ordinal));
        Assert.Equal(2 * DisassemblyViewModel.PageSize, vm.Rows.Count(r => r.IsInstruction));
    }

    [AvaloniaFact]
    public async Task Scrolling_loads_one_page_per_edge_and_the_cache_stays_bounded()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var vm = new DisassemblyViewModel(rig.Services);
        var prepended = 0;
        vm.RowsPrepended += n => prepended += n;
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count > 0, what: "first page");

        // three calls while one is on its way still send one request
        session.NativeDelay = TimeSpan.FromMilliseconds(100);
        var t1 = vm.LoadEarlierAsync();
        var t2 = vm.LoadEarlierAsync();
        await Task.WhenAll(t1, t2, vm.LoadEarlierAsync());
        Assert.Equal(2, session.DisassembleRequests.Count);
        Assert.Equal(-2 * DisassemblyViewModel.PageSize, session.DisassembleRequests[^1].InstructionOffset);
        Assert.True(prepended >= DisassemblyViewModel.PageSize, $"prepended {prepended}");

        session.NativeDelay = TimeSpan.Zero;
        await vm.LoadLaterAsync();
        Assert.Equal(DisassemblyViewModel.PageSize, session.DisassembleRequests[^1].InstructionOffset);
        var addresses = vm.Rows.Where(r => r.IsInstruction).Select(r => r.Address).ToList();
        Assert.Equal(addresses.Order(), addresses); // in address order, no duplicates
        Assert.Equal(addresses.Distinct().Count(), addresses.Count);

        for (var i = 0; i < 80; i++) await vm.LoadLaterAsync();
        Assert.True(vm.Rows.Count(r => r.IsInstruction) <= DisassemblyViewModel.MaxInstructions);
        Assert.Equal(83, session.DisassembleRequests.Count); // one request per page, never per row
    }

    [AvaloniaFact]
    public async Task A_new_stop_inside_the_loaded_code_only_moves_the_marker()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var vm = new DisassemblyViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Any(r => r.IsCurrent), what: "first stop");
        session.Frames[1] = [new DebugStackFrame(100, "demo::main", null, 5, 0) { InstructionPointerReference = "0x100044" }, session.Frames[1][1]];
        rig.StopAt(0, "step");
        await PanelRig.Eventually(() => vm.Rows.SingleOrDefault(r => r.IsCurrent)?.Address == 0x100044, what: "marker moved");
        Assert.Single(session.DisassembleRequests);
    }

    [AvaloniaFact]
    public async Task Instruction_breakpoints_toggle_from_a_row_and_show_on_it()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var vm = new DisassemblyViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count > 0, what: "rows");
        var row = vm.Rows.First(r => r.IsInstruction && r.Address == 0x100048);
        vm.ToggleBreakpoint(row);
        var bp = Assert.Single(rig.Debug.FakeBreakpoints.InstructionBreakpoints);
        Assert.Equal(("0x100048", session.Id), (bp.InstructionReference, bp.SessionId));
        Assert.Equal("demo::main (0x100048)", bp.Label);
        await PanelRig.Eventually(() => row.HasBreakpoint, what: "glyph");
        vm.ToggleBreakpoint(row);
        await PanelRig.Eventually(() => !row.HasBreakpoint, what: "glyph gone");
        Assert.Empty(rig.Debug.FakeBreakpoints.InstructionBreakpoints);

        session.SupportsInstructionBreakpoints = false;
        vm.ToggleBreakpoint(row);
        Assert.Empty(rig.Debug.FakeBreakpoints.InstructionBreakpoints);
        Assert.Contains(rig.Window.StatusMessages, m => m.Contains("does not support instruction breakpoints", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_focus_key_is_set_while_the_window_has_the_keyboard_and_cleared_when_it_goes()
    {
        using var rig = new PanelRig(session: NativeSession());
        var vm = new DisassemblyViewModel(rig.Services);
        vm.SetKeyboardFocus(true);
        Assert.Equal(true, rig.ContextKeys.Get(DisassemblyViewModel.FocusKey));
        vm.SetKeyboardFocus(false);
        Assert.Equal(false, rig.ContextKeys.Get(DisassemblyViewModel.FocusKey));
        vm.SetKeyboardFocus(true);
        vm.Dispose(); // a window closed while it had the keyboard does not leave stepping by instruction behind
        Assert.Equal(false, rig.ContextKeys.Get(DisassemblyViewModel.FocusKey));
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Open_Disassembly_View_goes_to_the_requested_address_and_the_address_box_takes_addresses_and_expressions()
    {
        var session = NativeSession();
        session.Evaluator = (e, _, _) => new DebugEvaluateResult("0x200100", "fn()", MemoryReference: e == "f" ? "0x200100" : null);
        using var rig = new PanelRig(session: session);
        using var vm = new DisassemblyViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count > 0, what: "rows");
        await rig.Services.NativeViews.ShowDisassembly(new NativeTarget("0x200000", "lang_start"));
        await PanelRig.Eventually(() => session.DisassembleRequests[^1].Reference == "0x200000", what: "navigated");
        await vm.GoToAsync("0x300000");
        Assert.Equal("0x300000", session.DisassembleRequests[^1].Reference);
        await vm.GoToAsync("f");
        Assert.Equal("0x200100", session.DisassembleRequests[^1].Reference);
    }

    [AvaloniaFact]
    public async Task An_adapter_without_disassembly_says_so()
    {
        var session = NativeSession();
        session.Capabilities = session.Capabilities with { SupportsDisassemble = false };
        using var rig = new PanelRig(session: session);
        using var vm = new DisassemblyViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.StatusText == "The debug adapter does not support disassembly.", what: "status");
        Assert.Empty(session.DisassembleRequests);
    }

    // ------------------------------------------------------------------ Memory

    [AvaloniaFact]
    public async Task View_Binary_Data_shows_the_variables_memory_reading_only_the_visible_pages()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var locals = new LocalsViewModel(rig.Services);
        using var vm = new MemoryViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "v"), what: "locals");
        var v = locals.Tree.Rows.First(r => r.Name == "v");
        Assert.True(locals.Tree.CanViewBinaryData(v));
        locals.Tree.ViewBinaryData(v);
        vm.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 20);
        await PanelRig.Eventually(() => vm.TargetRow.Hex != MemoryRow.Unread, what: "target row read");
        Assert.Equal("0x0000000000010000", vm.TargetRow.AddressText);
        Assert.StartsWith("19 1a 1b", vm.TargetRow.Hex); // 0x10000 % 251 = 25
        Assert.Equal("v at 0x0000000000010000", vm.Title);
        var reads = session.MemoryReads.ToList();
        Assert.All(reads, r => Assert.Equal(MemoryViewModel.PageBytes, r.Count));
        Assert.Single(reads); // 21 visible rows: one 4 KiB page

        // the row above the mapping is unreadable
        vm.SetVisibleRows(MemoryViewModel.RowCount / 2 - 4, MemoryViewModel.RowCount / 2 + 2);
        await PanelRig.Eventually(() => session.MemoryReads.Count == 2, what: "page above");
        Assert.Equal(MemoryRow.Unread, vm.Rows[MemoryViewModel.RowCount / 2 - 1].Hex);
    }

    [AvaloniaFact]
    public async Task A_Memory_window_opened_by_View_Binary_Data_starts_at_the_value()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        rig.StopAt(0);
        await rig.Services.NativeViews.ShowMemory(new NativeTarget("0x40000", "v[0]")); // no Memory window yet: the request waits for one
        using var vm = new MemoryViewModel(rig.Services);
        Assert.Equal("v[0] at 0x0000000000040000", vm.Title);
        Assert.Equal("0x0000000000040000", vm.TargetRow.AddressText);
        vm.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 4);
        await PanelRig.Eventually(() => vm.TargetRow.Hex != MemoryRow.Unread, what: "read");
        Assert.Null(rig.Services.NativeViews.TakeMemory()); // taken: a later window starts empty
    }

    [AvaloniaFact]
    public async Task Memory_refreshes_at_each_stop_and_keeps_a_bounded_number_of_pages()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var vm = new MemoryViewModel(rig.Services);
        rig.StopAt(0);
        await vm.GoToAsync("0x40000");
        vm.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 10);
        await PanelRig.Eventually(() => session.MemoryReads.Count == 1, what: "first read");
        rig.Resume();
        rig.StopAt(0, "step");
        await PanelRig.Eventually(() => session.MemoryReads.Count == 2, what: "read again after the stop");

        for (var row = 0; row < MemoryViewModel.RowCount; row += 256) vm.SetVisibleRows(row, row + 10);
        await PanelRig.Eventually(() => session.MemoryReads.Count >= 2 + 15, what: $"every page visited ({session.MemoryReads.Count} reads)");
        var loaded = vm.Rows.Count(r => r.Hex != MemoryRow.Unread);
        Assert.True(loaded <= MemoryViewModel.MaxPages * (MemoryViewModel.PageBytes / MemoryViewModel.BytesPerRow), $"{loaded} rows loaded");
    }

    [AvaloniaFact]
    public async Task Editing_a_row_writes_memory_and_reads_it_back()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var vm = new MemoryViewModel(rig.Services);
        rig.StopAt(0);
        await vm.GoToAsync("0x40000");
        vm.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 4);
        await PanelRig.Eventually(() => vm.TargetRow.Hex != MemoryRow.Unread, what: "read");
        rig.Window.InputResponses.Enqueue("de ad be ef");
        await vm.EditAsync(vm.TargetRow);
        var write = Assert.Single(session.MemoryWrites);
        Assert.Equal(("0x40000", 0L), (write.Reference, write.Offset));
        Assert.Equal([0xde, 0xad, 0xbe, 0xef], write.Data);
        await PanelRig.Eventually(() => vm.TargetRow.Hex.StartsWith("de ad be ef", StringComparison.Ordinal), what: "read back");
        Assert.Null(MemoryViewModel.ParseBytes("zz"));
        Assert.Null(MemoryViewModel.ParseBytes(string.Join(' ', Enumerable.Repeat("00", 17))));
    }

    // ------------------------------------------------------------------ Registers

    [AvaloniaFact]
    public async Task Registers_shows_the_register_scope_that_Locals_leaves_out()
    {
        using var rig = new PanelRig(session: NativeSession());
        using var registers = new RegistersViewModel(rig.Services);
        using var locals = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => registers.Tree.Rows.Any(r => r.Name == "General Purpose Registers"), what: "register groups");
        await PanelRig.Eventually(() => locals.Tree.Rows.Count > 0, what: "locals");
        Assert.DoesNotContain(locals.Tree.Rows, r => r.Name == "General Purpose Registers");
        var group = registers.Tree.Rows.First();
        await registers.Tree.ToggleAsync(group);
        await PanelRig.Eventually(() => registers.Tree.Rows.Any(r => r.Name == "pc"), what: "pc");
    }

    [Fact]
    public void Register_scopes_are_found_by_hint_or_name()
    {
        var scopes = new[]
        {
            new DebugScope("Locals", 1, false), new DebugScope("Registers", 2, true), new DebugScope("CPU", 3, true, PresentationHint: "registers"),
            new DebugScope("Register group without values", 0, true),
        };
        Assert.Equal([2, 3], RegistersViewModel.ForRegisters(scopes).Select(s => s.VariablesReference));
    }

    // ------------------------------------------------------------------ data breakpoints

    [AvaloniaFact]
    public async Task Break_When_Value_Changes_adds_a_data_breakpoint_for_the_variable_in_its_container()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        using var locals = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "x"), what: "locals");
        var x = locals.Tree.Rows.First(r => r.Name == "x");
        Assert.True(locals.Tree.CanBreakWhenValueChanges(x));
        await locals.Tree.BreakWhenValueChangesAsync(x);
        Assert.Equal((1000, "x", (int?)null), session.DataInfoRequests.Single());
        var bp = Assert.Single(rig.Debug.FakeBreakpoints.DataBreakpoints);
        Assert.Equal(("x@1000", "x (4 bytes)", "write", session.Id), (bp.DataId, bp.Description, bp.AccessType, bp.SessionId));

        session.Variables[1000] = [.. session.Variables[1000], new DebugVariable("unwatchable", "1", "int")];
        rig.Resume();
        rig.StopAt(0, "step");
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "unwatchable"), what: "new row");
        await locals.Tree.BreakWhenValueChangesAsync(locals.Tree.Rows.First(r => r.Name == "unwatchable"));
        Assert.Single(rig.Debug.FakeBreakpoints.DataBreakpoints);
        Assert.Contains(rig.Window.StatusMessages, m => m.Contains("the value is not in memory", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Without_data_breakpoint_support_the_menu_item_is_not_offered()
    {
        var session = NativeSession();
        session.Capabilities = session.Capabilities with { SupportsDataBreakpoints = false, SupportsReadMemory = false };
        using var rig = new PanelRig(session: session);
        using var locals = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "v"), what: "locals");
        var v = locals.Tree.Rows.First(r => r.Name == "v");
        Assert.False(locals.Tree.CanBreakWhenValueChanges(v));
        Assert.False(locals.Tree.CanViewBinaryData(v));
    }

    [AvaloniaFact]
    public async Task Data_and_instruction_breakpoints_are_listed_in_the_Breakpoints_window_and_can_be_disabled_and_deleted()
    {
        using var rig = new PanelRig(session: NativeSession());
        using var vm = new Breakpoints.BreakpointsViewModel(rig.Services);
        var data = rig.Debug.FakeBreakpoints.AddData("x@1000", "x (4 bytes)", "s1", false);
        var instruction = rig.Debug.FakeBreakpoints.ToggleInstruction("0x100048", "demo::main (0x100048)", "s1")!;
        await PanelRig.Eventually(() => vm.Rows.Count(r => r.IsBreakpoint) == 2, what: "rows");
        var dataRow = vm.Rows.Single(r => r.Kind == Breakpoints.BreakpointRowKind.Data);
        Assert.Equal(("x (4 bytes)", "data, break when changed"), (dataRow.Label, dataRow.Detail));
        Assert.Equal("demo::main (0x100048)", vm.Rows.Single(r => r.Kind == Breakpoints.BreakpointRowKind.Instruction).Label);
        Assert.Equal("Breakpoints (2)", vm.Rows[0].Label);
        dataRow.IsEnabled = false;
        await PanelRig.Eventually(() => rig.Debug.FakeBreakpoints.DataBreakpoints.Single().Enabled == false, what: "disabled");
        vm.Delete(vm.Rows.Single(r => r.Id == instruction.Id));
        await PanelRig.Eventually(() => rig.Debug.FakeBreakpoints.InstructionBreakpoints.Count == 0, what: "deleted");
        Assert.NotNull(data);
    }

    // ------------------------------------------------------------------ leaks

    [AvaloniaFact]
    public async Task The_native_windows_view_models_retain_nothing_after_dispose()
    {
        var session = NativeSession();
        using var rig = new PanelRig(session: session);
        var baseline = (rig.Debug.FakeBreakpoints.NativeSubscriberCount, rig.Debug.FakeBreakpoints.SubscriberCount, session.SubscriberCount);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var disassembly = new DisassemblyViewModel(rig.Services);
            var memory = new MemoryViewModel(rig.Services);
            var registers = new RegistersViewModel(rig.Services);
            rig.StopAt(0);
            await PanelRig.Eventually(() => disassembly.Rows.Count > 0, what: "disassembly");
            await memory.GoToAsync("0x40000");
            memory.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 4);
            await PanelRig.Eventually(() => memory.TargetRow.Hex != MemoryRow.Unread, what: "memory");
            await PanelRig.Eventually(() => registers.Tree.Rows.Count > 0, what: "registers");
            rig.Resume();
            disassembly.Dispose();
            memory.Dispose();
            registers.Dispose();
            return [disassembly, memory, registers];
        });
        Assert.Equal(baseline, (rig.Debug.FakeBreakpoints.NativeSubscriberCount, rig.Debug.FakeBreakpoints.SubscriberCount, session.SubscriberCount));
    }
}
