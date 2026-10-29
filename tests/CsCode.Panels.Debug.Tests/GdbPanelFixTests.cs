using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Regression tests (fakes shaped like GDB 18's native DAP answers) for what the Rust evaluation matrix found in the Debug panels
/// (docs/DEBUGGING-RUST-TS.md, "Rust through GDB"). Each fails without its fix.
/// </summary>
public class GdbPanelFixTests
{
    /// <summary>GDB's scopes right after a step out of <c>add</c>: Locals, Registers, the "Return" scope (hint returnValue), Globals.</summary>
    private static PanelRig AfterStepOut()
    {
        var rig = new PanelRig();
        rig.Session.Frames[1] = [new DebugStackFrame(0, "sample::main", new DebugSource("main.rs", "/work/src/main.rs"), 96, 0)];
        rig.Session.Scopes[0] =
        [
            new DebugScope("Locals", 1, false, 2, PresentationHint: "locals"),
            new DebugScope("Registers", 2, false, 61, PresentationHint: "registers"),
            new DebugScope("Return", 3, false, 1, PresentationHint: "returnValue"),
            new DebugScope("Globals", 4, false, 3, PresentationHint: "globals"),
        ];
        rig.Session.Variables[1] = [new DebugVariable("x", "51", "i32"), new DebugVariable("s", "\"hello\"", "alloc::string::String")];
        rig.Session.Variables[2] = [new DebugVariable("rax", "52", "int64_t")];
        rig.Session.Variables[3] = [new DebugVariable("(return)", "52", "i32")];
        rig.Session.Variables[4] = [new DebugVariable("GLOBAL", "1", "i32")];
        return rig;
    }

    [AvaloniaFact]
    public async Task Locals_show_the_value_the_function_stepped_out_of_returned()
    {
        using var rig = AfterStepOut();
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "(return)"), what: "the Return scope");
        var rows = vm.Tree.Rows.Select(r => $"{new string('.', Math.Max(0, r.Depth))}{r.Name}={r.DisplayValue}").ToList();
        Assert.Equal(["Locals=", ".x=51", ".s=\"hello\"", "Return=", ".(return)=52"], rows);
        Assert.DoesNotContain(rig.Session.VariableRequests, r => r.Reference is 2 or 4); // registers and globals are not fetched
    }

    [AvaloniaFact]
    public async Task Autos_show_the_returned_value_first_whatever_the_stop_line_mentions()
    {
        using var rig = AfterStepOut();
        using var vm = new AutosViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "(return)"), what: "autos");
        Assert.Equal("(return)", vm.Tree.Rows[0].Name);
        Assert.Equal("52", vm.Tree.Rows[0].DisplayValue);
    }

    [Fact]
    public void Rust_strings_gdb_already_quoted_are_not_quoted_again()
    {
        Assert.Equal("\"hello\"", ValueFormat.Display("\"hello\"", "alloc::string::String", NumberFormat.Decimal));
        Assert.Equal("\"world\"", ValueFormat.Display("\"world\"", "&str", NumberFormat.Decimal));
        Assert.Equal("Some(5)", ValueFormat.Display("Some(5)", "core::option::Option<i32>", NumberFormat.Decimal));
    }
}
