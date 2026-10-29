using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Change highlighting when the variables of ONE frame change between two stops, with fakes shaped like js-debug v1.140.0 on Node.js 24
/// (which no longer lists a <c>const</c> before it is initialised, so a step adds names to the frame): the frame is recognised by its function,
/// not by its variable names. Regression tests for docs/DEBUGGING-RUST-TS.md ("Node 20 vs Node 24"); each fails without the fix.
/// </summary>
public class ChangeHighlightTests
{
    private static readonly DebugSource Util = new("util.ts", "/work/src/util.ts");
    private static readonly DebugSource Main = new("main.ts", "/work/src/main.ts");

    private static DebugVariable V(string name, string value, string type = "number") => new(name, value, type, EvaluateName: name);
    private static DebugVariable This => new("this", "undefined", "undefined");
    private static string Names(VariablesTreeViewModel tree) => string.Join(",", tree.Rows.Select(r => r.Name));
    private static List<string> Changed(VariablesTreeViewModel tree) => [.. tree.Rows.Where(r => r.IsChanged).Select(r => r.Name)];

    /// <summary>Stops in add() at <paramref name="line"/>; js-debug numbers the frames anew at every stop (<paramref name="frameId"/>).</summary>
    private static void StopInAdd(PanelRig rig, int frameId, int line, params DebugVariable[] locals)
    {
        rig.Session.Frames[1] = [new DebugStackFrame(frameId, "add", Util, line, 2), new DebugStackFrame(frameId + 1, "work", Main, 12, 4)];
        rig.Session.Scopes[frameId] = [new DebugScope("Local: add", 1000, false, PresentationHint: "locals")];
        rig.Session.Variables[1000] = [.. locals];
        rig.StopAt(0);
    }

    /// <summary>Stops in main() at <paramref name="line"/> with the given scopes (references 1000 + index).</summary>
    private static void StopInMain(PanelRig rig, int frameId, int line, params (string Scope, DebugVariable[] Variables)[] scopes)
    {
        rig.Session.Frames[1] = [new DebugStackFrame(frameId, "main", Main, line, 4)];
        rig.Session.Scopes[frameId] = [.. scopes.Select((s, i) => new DebugScope(s.Scope, 1000 + i, false, PresentationHint: "locals"))];
        for (var i = 0; i < scopes.Length; i++) rig.Session.Variables[1000 + i] = [.. scopes[i].Variables];
        rig.StopAt(0);
    }

    [AvaloniaFact]
    public async Task Variables_that_appear_or_disappear_in_a_step_keep_the_others_compared_and_a_new_one_is_marked_changed()
    {
        using var rig = new PanelRig();
        using var locals = new LocalsViewModel(rig.Services);
        using var autos = new AutosViewModel(rig.Services);

        // In add(): `sum` is not listed yet (Node.js 24 hides it until `const sum = a + b` ran); `tmp` is still listed
        StopInAdd(rig, 10, 1, V("a", "100"), V("b", "1"), V("tmp", "5"), This);
        await PanelRig.Eventually(() => Names(locals.Tree) == "a,b,tmp,this" && Names(autos.Tree) == "a,b,tmp", what: "first stop");
        Assert.Empty(Changed(locals.Tree));

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0 && autos.Tree.Rows.Count == 0, what: "resumed");
        // One step later in the same function: sum appeared, tmp is gone, b changed, and js-debug added its "Return value" pseudo-variable
        StopInAdd(rig, 20, 2, V("a", "100"), V("b", "2"), V("Return value", "7"), V("sum", "102"), This);
        await PanelRig.Eventually(() => Names(locals.Tree) == "a,b,Return value,sum,this" && Names(autos.Tree) == "a,b,Return value,sum", what: "after the step");
        Assert.Equal(["b", "sum"], Changed(locals.Tree)); // b changed; sum is new (Visual Studio shows a new local in red); a and this did not change
        Assert.Equal(["b", "sum"], Changed(autos.Tree));

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0 && autos.Tree.Rows.Count == 0, what: "resumed again");
        StopInAdd(rig, 30, 3, V("a", "100"), V("b", "2"), V("sum", "102"), This); // nothing changed since the previous stop
        await PanelRig.Eventually(() => Names(locals.Tree) == "a,b,sum,this" && Names(autos.Tree) == "a,b,sum", what: "the next step");
        Assert.Empty(Changed(locals.Tree));
        Assert.Empty(Changed(autos.Tree));
    }

    [AvaloniaFact]
    public async Task A_block_scope_that_comes_and_goes_keeps_the_frame_compared_and_its_variables_count_as_new()
    {
        using var rig = new PanelRig();
        using var locals = new LocalsViewModel(rig.Services);

        StopInMain(rig, 10, 50, ("Local: main", [V("x", "1"), This]));
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,this", what: "before the loop");

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0);
        // Into the loop body: js-debug lists a Block scope first; the frame's own scope (now a group) keeps its variables' paths
        StopInMain(rig, 20, 51, ("Block", [V("i", "0")]), ("Local: main", [V("x", "2"), This]));
        await PanelRig.Eventually(() => Names(locals.Tree) == "Block,i,Local: main,x,this", what: "in the loop");
        Assert.Equal(["i", "x"], Changed(locals.Tree));

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0);
        StopInMain(rig, 30, 53, ("Local: main", [V("x", "2"), This])); // out of the loop: the block is gone, nothing else changed
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,this", what: "after the loop");
        Assert.Empty(Changed(locals.Tree));

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0);
        StopInMain(rig, 40, 51, ("Block", [V("i", "1")]), ("Local: main", [V("x", "2"), This])); // the block appeared again
        await PanelRig.Eventually(() => Names(locals.Tree) == "Block,i,Local: main,x,this", what: "in the loop again");
        Assert.Equal(["i"], Changed(locals.Tree));
    }

    [AvaloniaFact]
    public async Task Locals_and_autos_share_one_stack_request_per_stop_for_the_frames_identity()
    {
        using var rig = new PanelRig();
        using var locals = new LocalsViewModel(rig.Services);
        using var autos = new AutosViewModel(rig.Services);
        StopInAdd(rig, 10, 1, V("a", "1"), This);
        await PanelRig.Eventually(() => Names(locals.Tree) == "a,this" && Names(autos.Tree) == "a", what: "first stop");
        Assert.Equal(1, rig.Session.StackCalls);

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0 && autos.Tree.Rows.Count == 0);
        rig.Session.Variables[1000] = [V("a", "2"), This];
        rig.StopAt(0); // the same place again (a loop): the same function, nothing to ask
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "a" && r.IsChanged) && autos.Tree.Rows.Any(r => r.Name == "a" && r.IsChanged), what: "second stop");
        Assert.Equal(1, rig.Session.StackCalls);

        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0 && autos.Tree.Rows.Count == 0);
        StopInAdd(rig, 20, 2, V("a", "3"), This);
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "a" && r.DisplayValue == "3") && autos.Tree.Rows.Any(r => r.Name == "a" && r.DisplayValue == "3"), what: "third stop");
        Assert.Equal(2, rig.Session.StackCalls);
    }

    [AvaloniaFact]
    public async Task Without_the_frames_identity_its_variable_names_still_key_the_comparison()
    {
        using var rig = new PanelRig();
        using var locals = new LocalsViewModel(rig.Services);
        rig.Session.Frames[1] = []; // the stack cannot tell which function frame 77 is in
        rig.Session.Scopes[77] = [new DebugScope("Locals", 1000, false)];
        void Stop(params DebugVariable[] variables)
        {
            rig.Session.Variables[1000] = [.. variables];
            rig.Session.Stop();
            rig.Debug.SetStopLocation(new DebugStopLocation(rig.Session, 1, 77, new Uri("file:///work/a.cs"), 3, 0, "breakpoint"));
        }

        Stop(V("x", "1"), V("y", "1"));
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,y", what: "first stop");
        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0);
        Stop(V("x", "2"), V("y", "1"));
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "x" && r.DisplayValue == "2"), what: "same names");
        Assert.Equal(["x"], Changed(locals.Tree));
        rig.Resume();
        await PanelRig.Eventually(() => locals.Tree.Rows.Count == 0);
        Stop(V("x", "3"), V("z", "1")); // other names: without the identity this may be another function, so nothing is compared
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,z", what: "other names");
        Assert.Empty(Changed(locals.Tree));
    }
}
