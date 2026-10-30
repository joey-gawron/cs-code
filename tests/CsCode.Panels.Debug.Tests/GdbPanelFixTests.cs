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

    /// <summary>
    /// CodeLLDB lists the returned value as the first variable of "Local" (no scope of its own, no presentation hints): Autos lifts it as it lifts
    /// GDB's Return scope, also when the stop line names other variables only (T52). js-debug's "Return value" is lifted the same way.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("[return value]")]
    [InlineData("Return value")]
    public async Task Autos_lift_a_return_value_listed_among_the_locals(string name)
    {
        using var rig = new PanelRig();
        var uri = new Uri("file:///work/src/main.rs");
        rig.Session.Frames[1] = [new DebugStackFrame(0, "sample::main", new DebugSource("main.rs", uri.LocalPath), 3, 0)];
        rig.Session.Scopes[0] = [new DebugScope("Local", 1, false), new DebugScope("Static", 2, false), new DebugScope("Global", 3, false)];
        rig.Session.Variables[1] = [new DebugVariable(name, "52", "int"), new DebugVariable("x", "51", "int"), new DebugVariable("unused", "0", "int")];
        using var registration = rig.Documents.Register(new CsCode.Core.Text.InMemoryTextDocument(uri, "rust", "fn main() {\n    let x = add(1, 51);\n    println!(\"{x}\");\n}\n"));
        using var vm = new AutosViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count > 0, what: "autos");
        Assert.Equal([name, "x"], vm.Tree.Rows.Select(r => r.Name)); // first, though the lines only mention x
        Assert.Equal("52", vm.Tree.Rows[0].DisplayValue);
    }

    /// <summary>Answers like GDB 18: <c>filter</c> is ignored (named or indexed, every child), <c>start</c>/<c>count</c> are honoured.</summary>
    private sealed class GdbLikeSession : FakeDebugSession, IDebugSessionVariablePaging
    {
        public List<string?> Filters { get; } = new();

        public GdbLikeSession() : base("s1", "gdb") => WithDefaultScript();

        public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, string? filter, int? start, int? count, CancellationToken ct = default)
        {
            lock (Filters) Filters.Add(filter);
            return GetVariablesAsync(variablesReference, start, count, ct);
        }
    }

    [AvaloniaFact]
    public async Task A_vector_gdb_announces_only_indexed_children_for_pages_through_its_elements_in_order()
    {
        var session = new GdbLikeSession();
        session.Variables[1000] = [new DebugVariable("big", "Vec(size=250)", "alloc::vec::Vec<u32>", 4000, null, NamedVariables: null, IndexedVariables: 250)];
        session.Variables[4000] = [.. Enumerable.Range(0, 250).Select(i => new DebugVariable($"[{i}]", (i * 2).ToString(System.Globalization.CultureInfo.InvariantCulture), "u32"))];
        using var rig = new PanelRig(session: session);
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "big"), what: "locals");
        var big = vm.Tree.Rows.Single(r => r.Name == "big");
        await vm.Tree.ToggleAsync(big);
        for (var page = 0; page < 5 && vm.Tree.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More); page++)
            await vm.Tree.LoadMoreAsync(vm.Tree.Rows.First(r => r.Parent == big && r.Kind == VariableRowKind.More));
        var elements = vm.Tree.Rows.Where(r => r.Parent == big && r.Kind == VariableRowKind.Variable).Select(r => r.Name).ToList();
        Assert.Equal(Enumerable.Range(0, 250).Select(i => $"[{i}]"), elements);
        Assert.DoesNotContain("named", session.Filters); // GDB would answer it with every element
    }

    [Fact]
    public void Rust_strings_gdb_already_quoted_are_not_quoted_again()
    {
        Assert.Equal("\"hello\"", ValueFormat.Display("\"hello\"", "alloc::string::String", NumberFormat.Decimal));
        Assert.Equal("\"world\"", ValueFormat.Display("\"world\"", "&str", NumberFormat.Decimal));
        Assert.Equal("Some(5)", ValueFormat.Display("Some(5)", "core::option::Option<i32>", NumberFormat.Decimal));
    }
}
