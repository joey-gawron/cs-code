using System.Globalization;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Regression tests (fakes shaped like js-debug v1.140.0's real answers) for what the adversarial review of the js-debug wave found in the
/// Variables trees (docs/DEBUGGING-RUST-TS.md, "Review fixes"). Each fails without its fix.
/// </summary>
public class JsDebugReviewFixTests
{
    private static string N(int i) => i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Expands <c>huge</c> (an array of <paramref name="count"/> elements plus js-debug's two named children) and loads every page.</summary>
    private static async Task<(VariableNodeViewModel Huge, List<VariableNodeViewModel> Children, LocalsViewModel Vm, PanelRig Rig)> PageEverythingAsync(int count)
    {
        var session = new JsDebugPanelFixTests.JsLikeSession();
        var rig = new PanelRig(session: session);
        session.Variables[1000] = [new DebugVariable("huge", $"({count}) [0, 3, 6, …]", "Array", 4000, "huge", NamedVariables: 2, IndexedVariables: count)];
        session.Variables[4000] = [.. Enumerable.Range(0, count).Select(i => new DebugVariable(N(i), N(i * 3), "number")),
            new DebugVariable("length", N(count), "number"), new DebugVariable("[[Prototype]]", "Array(0)", "Object", 4001)];
        var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "huge"), what: "locals");
        var huge = vm.Tree.Rows.Single(r => r.Name == "huge");
        await vm.Tree.ToggleAsync(huge);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Parent == huge && r.Kind == VariableRowKind.More), what: "first page");
        for (var i = 0; i < 150 && vm.Tree.Rows.FirstOrDefault(r => r.Parent == huge && r.Kind == VariableRowKind.More) is { } more; i++)
            await vm.Tree.LoadMoreAsync(more);
        return (huge, [.. vm.Tree.Rows.Where(r => r.Parent == huge)], vm, rig);
    }

    [AvaloniaFact]
    public async Task An_array_of_ten_thousand_elements_pages_to_its_last_element_and_claims_nothing_was_left_out()
    {
        var (_, children, vm, rig) = await PageEverythingAsync(10_000);
        using (rig)
        using (vm)
        {
            var elements = children.Where(r => r.Kind == VariableRowKind.Variable && int.TryParse(r.Name, out _)).ToList();
            Assert.Equal(10_000, elements.Count); // was 9,998: the named length and [[Prototype]] counted against the 10,000 cap
            Assert.Equal(("9999", "29997"), (elements[^1].Name, elements[^1].DisplayValue));
            Assert.DoesNotContain(children, r => r.Kind == VariableRowKind.Message); // was "Showing the first 10,000 of 10,000 items"
            Assert.Contains(children, r => r.Name == "length");
        }
    }

    [AvaloniaFact]
    public async Task An_array_longer_than_the_cap_shows_its_first_ten_thousand_elements_and_says_how_many_there_are()
    {
        var (_, children, vm, rig) = await PageEverythingAsync(10_050);
        using (rig)
        using (vm)
        {
            var elements = children.Where(r => r.Kind == VariableRowKind.Variable && int.TryParse(r.Name, out _)).ToList();
            Assert.Equal(Enumerable.Range(0, VariablesTreeViewModel.MaxLoadedPerNode).Select(N), elements.Select(r => r.Name));
            Assert.Equal("Showing the first 10,000 of 10,050 items", children.Single(r => r.Kind == VariableRowKind.Message).Name);
        }
    }

    [AvaloniaFact]
    public async Task A_getter_that_throws_says_so_instead_of_showing_the_first_property_of_the_getter_function()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[1000] =
        [
            new DebugVariable("x", "3", "number"),
            new DebugVariable("boom", "ƒ boom() { throw new Error(\"getter failed\"); }", "function", 3000) { IsLazy = true },
        ];
        // js-debug v1.140.0 answers a throwing getter with the getter function's own properties (and no error)
        rig.Session.Variables[3000] =
        [
            new DebugVariable("arguments", "ƒ ()", "function", 3001), new DebugVariable("caller", "ƒ ()", "function", 3002),
            new DebugVariable("length", "0", "number"), new DebugVariable("name", "'get boom'", "string"),
        ];
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "boom"), what: "locals");
        var boom = vm.Tree.Rows.Single(r => r.Name == "boom");
        await vm.Tree.ActivateAsync(boom);
        Assert.True(boom.IsError);
        Assert.Equal(VariablesTreeViewModel.LazyFailedText, boom.DisplayValue); // was "ƒ ()", the value of 'arguments'
        Assert.False(boom.HasChildren);
    }

    [AvaloniaFact]
    public async Task A_getter_that_was_evaluated_is_not_marked_changed_at_the_next_stop_when_nothing_changed()
    {
        using var rig = new PanelRig();
        List<DebugVariable> Locals(string x) =>
        [
            new DebugVariable("x", x, "number"),
            new DebugVariable("length", "ƒ length() { return Math.sqrt(this.x * this.x + this.y * this.y); }", "function", 3000) { IsLazy = true },
        ];
        rig.Session.Variables[1000] = Locals("3");
        rig.Session.Variables[3000] = [new DebugVariable("length", "5", "number")];
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "length"), what: "first stop");
        await vm.Tree.ActivateAsync(vm.Tree.Rows.Single(r => r.Name == "length"));
        Assert.Equal("5", vm.Tree.Rows.Single(r => r.Name == "length").DisplayValue);

        rig.Resume();
        rig.Session.Variables[1000] = Locals("4");
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "x" && r.DisplayValue == "4"), what: "second stop");
        var length = vm.Tree.Rows.Single(r => r.Name == "length");
        Assert.True(length.IsLazy);
        Assert.False(length.IsChanged); // the placeholder is the same as at the first stop (was: compared with the computed "5")
        Assert.True(vm.Tree.Rows.Single(r => r.Name == "x").IsChanged);
        await vm.Tree.ActivateAsync(length);
        Assert.Equal("5", length.DisplayValue);
        Assert.False(length.IsChanged); // evaluating it does not paint it red either
    }

    // ------------------------------------------------------------------ integrator findings (real app on a real display)

    [AvaloniaFact]
    public async Task Node_internals_skipped_through_skipFiles_fold_by_default_and_show_when_their_row_is_clicked()
    {
        using var rig = new PanelRig();
        var internals = new DebugSource("<node_internals>/internal/modules/cjs/loader", "<node_internals>/internal/modules/cjs/loader", 77, "deemphasize");
        var runMain = new DebugSource("<node_internals>/internal/modules/run_main", "<node_internals>/internal/modules/run_main", 78, "deemphasize");
        // the stack of the integrator's demo, stopped in restock() (index.ts:5) under two awaits
        rig.Session.Frames[1] =
        [
            new DebugStackFrame(100, "restock", new DebugSource("src/index.ts", "/demo/src/index.ts"), 4, 4),
            new DebugStackFrame(101, "await", null, 0, 0, PresentationHint: "label"),
            new DebugStackFrame(102, "main", new DebugSource("src/index.ts", "/demo/src/index.ts"), 13, 22),
            new DebugStackFrame(103, "await", null, 0, 0, PresentationHint: "label"),
            new DebugStackFrame(104, "<anonymous>", new DebugSource("src/index.ts", "/demo/src/index.ts"), 20, 0),
            new DebugStackFrame(105, "Module._compile", internals, 1528, 13, PresentationHint: "deemphasize"),
            new DebugStackFrame(106, "Module._extensions..js", internals, 1612, 10, PresentationHint: "deemphasize"),
            new DebugStackFrame(107, "Module.load", internals, 1274, 31, PresentationHint: "deemphasize"),
            new DebugStackFrame(108, "Module._load", internals, 1095, 12, PresentationHint: "deemphasize"),
            new DebugStackFrame(109, "executeUserEntryPoint", runMain, 163, 11, PresentationHint: "deemphasize"),
            new DebugStackFrame(110, "<anonymous>", new DebugSource("<node_internals>/internal/main/run_main_module", null, 79, "deemphasize"), 27, 48, PresentationHint: "deemphasize"),
        ];
        using var vm = new CallStackViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Any(r => r.Text == "restock"), what: "frames");
        Assert.True(vm.ShowExternalCode); // the default
        // was: the six internals listed one by one (greyed)
        Assert.Equal(["restock", "await", "main", "await", "<anonymous>", "[External Code]"], vm.Rows.Select(r => r.Text));
        var folded = vm.Rows[^1];
        Assert.Equal((CallStackRowKind.ExternalCode, 6), (folded.Kind, folded.HiddenFrames));
        Assert.Equal("6 frames are hidden. Click to show them.", folded.Tooltip);

        // a click shows them, greyed, and selects the first; nothing is focused or opened
        var calls = rig.Debug.FocusCalls.Count;
        await vm.ActivateAsync(folded, takeFocus: false);
        Assert.Equal(11, vm.Rows.Count);
        Assert.All(vm.Rows.Skip(5), r => Assert.True(r.IsExternal && r.Kind == CallStackRowKind.Frame));
        Assert.Equal("Module._compile", vm.SelectedRow?.Text);
        Assert.Equal(calls, rig.Debug.FocusCalls.Count);

        // the next stop folds them again; a single skipped frame is not folded (nothing to gain), but greyed
        rig.Resume();
        rig.Session.Frames[1] = [.. rig.Session.Frames[1].Take(5), rig.Session.Frames[1][^1]];
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 6 && vm.Rows[^1].Kind == CallStackRowKind.Frame, what: "second stop");
        Assert.True(vm.Rows[^1].IsExternal);
    }

    [AvaloniaFact]
    public async Task A_lazy_getter_shows_no_type_until_it_is_evaluated_then_the_type_of_its_value()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[1000] =
        [
            new DebugVariable("location", "'Main warehouse'", "string"),
            // js-debug types the placeholder of an accessor property "function" (the getter), not the type of its value
            new DebugVariable("totalUnits", "ƒ totalUnits() {\n        let total = 0;…", "function", 3000) { IsLazy = true },
        ];
        rig.Session.Variables[3000] = [new DebugVariable("totalUnits", "25", "number")];
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "totalUnits"), what: "locals");
        var getter = vm.Tree.Rows.Single(r => r.Name == "totalUnits");
        Assert.Equal(VariableNodeViewModel.LazyText, getter.DisplayValue);
        Assert.Null(getter.Type); // was "function"
        Assert.False(getter.CanViewAsNumber);
        await vm.Tree.ActivateAsync(getter);
        Assert.Equal(("25", "number"), (getter.DisplayValue, getter.Type));
    }
}
