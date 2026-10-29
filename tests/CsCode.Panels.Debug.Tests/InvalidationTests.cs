using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Regression (debug-ct-3): a DAP <c>invalidated</c> event makes the real session forget every frame id it handed out, but the debug service
/// keeps (and after the grace period the panels reused) the old stop location's frame id, so Locals/Autos showed "Not available" and every
/// watch failed until the next stop. The panels now re-fetch the stack once (which re-registers the ids) and focus a live frame.
/// </summary>
public class InvalidationTests
{
    private static string Names(VariablesTreeViewModel tree) => string.Join(",", tree.Rows.Select(r => r.Name));

    private static PanelRig StoppedRig()
    {
        var rig = new PanelRig();
        rig.Session.TrackFrameIds = true;
        rig.Session.Evaluator = (e, frame, _) => new DebugEvaluateResult(e + "@" + frame, "string");
        rig.Debug.AddWatch("x + 1");
        return rig;
    }

    [AvaloniaFact]
    public async Task After_an_invalidation_locals_and_watches_reload_in_the_same_frame()
    {
        using var rig = StoppedRig();
        using var locals = new LocalsViewModel(rig.Services);
        using var watch = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,s,o", what: "locals");
        await PanelRig.Eventually(() => watch.Tree.Rows[0].FullValue == "x + 1@102", what: "watch");
        var scopeCalls = rig.Session.ScopeCalls;
        var evaluations = rig.Session.EvaluateCalls;

        rig.Session.Invalidate();
        // The reload's rows, not just its status: between the scopes and the variables answer the status is already cleared while the rows
        // are not in yet (waiting for the status alone raced with that, also before the change-highlighting work). A frame the session
        // refuses ("Not available") never gets rows, so this still fails then.
        await PanelRig.Eventually(() => rig.Session.ScopeCalls > scopeCalls && Names(locals.Tree) == "x,s,o", timeoutMs: 3_000, what: "locals reloaded");
        Assert.Equal("x,s,o", Names(locals.Tree));
        Assert.Null(locals.Tree.ErrorText);
        await PanelRig.Eventually(() => rig.Session.EvaluateCalls > evaluations && watch.Tree.Rows[0].HasValue, what: "watch re-evaluated");
        Assert.False(watch.Tree.Rows[0].IsError, watch.Tree.Rows[0].FullValue);
        Assert.Equal("x + 1@102", watch.Tree.Rows[0].FullValue);
    }

    [AvaloniaFact]
    public async Task An_adapter_that_renumbers_its_frames_gets_the_live_frame_focused_even_when_the_service_ignores_it()
    {
        using var rig = StoppedRig();
        using var locals = new LocalsViewModel(rig.Services);
        using var watch = new WatchViewModel(rig.Services);
        using var stack = new CallStackViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,s,o" && watch.Tree.Rows[0].FullValue == "x + 1@102", what: "first stop");
        rig.Debug.FocusableFrameLimit = 0; // like the real service, whose frame cache still holds the old ids
        var scopeCalls = rig.Session.ScopeCalls;

        rig.Session.Invalidate(renumberFrames: true); // Main() is now frame 1102
        await PanelRig.Eventually(() => rig.Session.ScopeCalls > scopeCalls && Names(locals.Tree) == "x,s,o", timeoutMs: 3_000, what: "locals reloaded");
        Assert.Equal("x,s,o", Names(locals.Tree));
        await PanelRig.Eventually(() => watch.Tree.Rows[0].FullValue == "x + 1@1102", what: "watch evaluated in the live frame");
        Assert.Contains(rig.Debug.FocusCalls, c => c.FrameId == 1102); // the service was asked to follow
        await PanelRig.Eventually(() => stack.Rows.Count == 3 && stack.Rows[2].IsCurrent && stack.Rows[2].Frame!.Id == 1102, what: "call stack arrow on the live frame");
    }

    [AvaloniaFact]
    public async Task A_tracker_that_outlives_the_session_does_not_keep_it_reachable_after_its_frames_were_renumbered()
    {
        // Regression (review of debug-ct-3): the live frame that replaces a renumbered stop location was kept, with the location and through
        // it the session, until the next renumbering invalidation. The runtime's exception-key tracker lives as long as the app.
        using var rig = new PanelRig(withSession: false);
        int? frameId = null;
        var hasSession = false;
        using var tracker = new DebugFocusTracker(rig.Services, f => { frameId = f.FrameId; hasSession = f.Session is not null; });
        tracker.Start();

        var session = await StopRenumberAndEndAsync(rig, () => frameId);
        await PanelRig.Eventually(() => !hasSession, what: "the tracker saw the session end");
        for (var i = 0; i < 12 && session.IsAlive; i++)
        {
            await Task.Delay(40);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(session.IsAlive, "the ended session is still reachable after 12 full GCs");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StopRenumberAndEndAsync(PanelRig rig, Func<int?> frameId)
    {
        var session = new FakeDebugSession("renumbering", "Renumbering").WithDefaultScript();
        session.TrackFrameIds = true;
        rig.Debug.AddSession(session);
        rig.Debug.StopAt(session, "breakpoint", 2);
        await PanelRig.Eventually(() => frameId() == 102, what: "first stop");
        rig.Debug.FocusableFrameLimit = 0; // the service ignores the renumbered frame, so the tracker itself has to remember it
        session.Invalidate(renumberFrames: true);
        await PanelRig.Eventually(() => frameId() == 1102, timeoutMs: 3_000, what: "the live frame");
        rig.Debug.RemoveSession(session);
        session.Terminate();
        return new WeakReference(session);
    }

    [AvaloniaFact]
    public async Task The_panels_share_one_stack_request_per_invalidation()
    {
        using var rig = StoppedRig();
        using var locals = new LocalsViewModel(rig.Services);
        using var autos = new AutosViewModel(rig.Services);
        using var watch = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Names(locals.Tree) == "x,s,o" && autos.Tree.Rows.Count > 0 && watch.Tree.Rows[0].HasValue, what: "first stop");
        var stackCalls = rig.Session.StackCalls;
        var scopeCalls = rig.Session.ScopeCalls;

        rig.Session.Invalidate();
        await PanelRig.Eventually(() => rig.Session.ScopeCalls >= scopeCalls + 2 && locals.Tree.Rows.Count == 3 && autos.Tree.Rows.Count > 0, timeoutMs: 3_000, what: "reloaded");
        await Task.Delay(100);
        Assert.Equal(stackCalls + 1, rig.Session.StackCalls);
    }
}
