using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Threads;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class CallStackTests
{
    private static async Task<CallStackViewModel> StoppedStack(PanelRig rig)
    {
        var vm = new CallStackViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count > 0, what: "frames");
        return vm;
    }

    [AvaloniaFact]
    public async Task A_single_thread_shows_a_flat_frame_list_with_the_arrow_on_the_stopped_frame()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedStack(rig);
        Assert.Equal(["Sample.Program.Add(int a, int b)", "Sample.Program.Outer(int n)", "Sample.Program.Main()"], vm.Rows.Select(r => r.Text));
        Assert.All(vm.Rows, r => Assert.Equal(CallStackRowKind.Frame, r.Kind));
        Assert.All(vm.Rows, r => Assert.Equal(0, r.Depth));
        await PanelRig.Eventually(() => vm.Rows[0].IsCurrent, what: "arrow on top frame");
        Assert.True(vm.Rows[0].ShowArrow);
        Assert.Equal("debug-stackframe-active", vm.Rows[0].ArrowKind);
        Assert.False(vm.Rows[1].IsCurrent);
        Assert.Equal("Program.cs : 9", vm.Rows[0].Location); // 1-based line shown, Core line is 0-based
    }

    [AvaloniaFact]
    public async Task Picking_a_frame_focuses_it_through_the_frame_focus_service_and_opens_its_source()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedStack(rig);
        await vm.ActivateAsync(vm.Rows[2], takeFocus: false);
        Assert.Equal(("s1", 1, 102), Assert.Single(rig.Debug.FocusCalls));
        var (uri, selection) = Assert.Single(rig.Navigation.Opened);
        Assert.Equal(new Uri("file:///work/Program.cs"), uri);
        Assert.Equal(TextRange.At(24, 0), selection);
        Assert.True(vm.Rows[2].IsCurrent);
        Assert.False(vm.Rows[0].IsCurrent);
        Assert.Equal("debug-stackframe", vm.Rows[2].ArrowKind); // caller frame: outlined arrow
    }

    [AvaloniaFact]
    public async Task The_arrow_follows_the_stop_location_when_another_window_changes_the_focused_frame()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedStack(rig);
        await rig.Debug.SetFocusedFrameAsync("s1", 1, 101);
        await PanelRig.Eventually(() => vm.Rows[1].IsCurrent, what: "arrow moved");
        Assert.False(vm.Rows[0].IsCurrent);
    }

    [AvaloniaFact]
    public async Task External_code_is_greyed_and_can_be_folded_into_one_row()
    {
        using var rig = new PanelRig();
        rig.Session.Frames[1] =
        [
            new DebugStackFrame(100, "Sample.Program.Add()", new DebugSource("Program.cs", "/work/Program.cs"), 8, 0),
            new DebugStackFrame(101, "System.Private.CoreLib.dll!Thread.Start", null, 0, 0),
            new DebugStackFrame(102, "System.Linq!Enumerable.Select", new DebugSource("Enumerable.cs", "/rt/Enumerable.cs"), 3, 0, PresentationHint: "subtle"),
            new DebugStackFrame(103, "Sample.Program.Main()", new DebugSource("Program.cs", "/work/Program.cs"), 24, 0),
        ];
        using var vm = await StoppedStack(rig);
        Assert.Equal([false, true, true, false], vm.Rows.Select(r => r.IsExternal));
        Assert.Equal(4, vm.Rows.Count);

        vm.ShowExternalCode = false;
        Assert.Equal(["Sample.Program.Add()", "[External Code]", "Sample.Program.Main()"], vm.Rows.Select(r => r.Text));
        Assert.Equal(CallStackRowKind.ExternalCode, vm.Rows[1].Kind);
        Assert.Equal(2, vm.Rows[1].HiddenFrames);

        vm.ShowExternalCode = true;
        Assert.Equal(4, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Long_stacks_load_lazily_one_page_at_a_time()
    {
        using var rig = new PanelRig();
        rig.Session.Frames[1] = Enumerable.Range(0, 120).Select(i => new DebugStackFrame(200 + i, "Frame" + i, new DebugSource("a.cs", "/work/a.cs"), i, 0)).ToList();
        using var vm = await StoppedStack(rig);
        Assert.Equal(CallStackViewModel.PageSize + 1, vm.Rows.Count);
        Assert.Equal(CallStackRowKind.More, vm.Rows[^1].Kind);
        Assert.Equal(1, rig.Session.StackCalls); // one page requested up front

        await vm.ActivateAsync(vm.Rows[^1], takeFocus: false);
        Assert.Equal(2 * CallStackViewModel.PageSize + 1, vm.Rows.Count);
        await vm.ActivateAsync(vm.Rows[^1], takeFocus: false);
        Assert.Equal(120, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.Kind == CallStackRowKind.More);
        Assert.Equal("Frame119", vm.Rows[^1].Text);
    }

    [AvaloniaFact]
    public async Task Several_threads_show_thread_rows_and_load_other_stacks_only_when_expanded()
    {
        using var rig = new PanelRig();
        rig.Session.Threads.Add(new DebugThread(2, "Worker"));
        rig.Session.Frames[2] = [new DebugStackFrame(300, "Worker.Run()", new DebugSource("w.cs", "/work/w.cs"), 4, 0)];
        using var vm = await StoppedStack(rig);
        Assert.Equal(["Main Thread (ID 1)", "Sample.Program.Add(int a, int b)", "Sample.Program.Outer(int n)", "Sample.Program.Main()", "Worker (ID 2)"], vm.Rows.Select(r => r.Text));
        Assert.Equal(1, vm.Rows[1].Depth);
        var stackCalls = rig.Session.StackCalls;

        await vm.ActivateAsync(vm.Rows[^1], takeFocus: false); // expand Worker
        Assert.Equal(stackCalls + 1, rig.Session.StackCalls);
        Assert.Equal("Worker.Run()", vm.Rows[^1].Text);
        Assert.Equal(2, vm.Rows[^1].ThreadId);

        await vm.ActivateAsync(vm.Rows[^1], takeFocus: false); // pick the worker frame
        Assert.Equal(("s1", 2, 300), rig.Debug.FocusCalls.Last());
    }

    [AvaloniaFact]
    public async Task The_stack_is_dropped_when_the_debuggee_resumes_and_reloaded_on_the_next_stop()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedStack(rig);
        rig.Resume();
        await PanelRig.Eventually(() => vm.Rows.Count == 0 && vm.StatusText == "Running…", what: "cleared");
        Assert.True(vm.ShowStatus);
        rig.StopAt(1);
        await PanelRig.Eventually(() => vm.Rows.Count == 3);
        await PanelRig.Eventually(() => vm.Rows[1].IsCurrent);
    }

    [AvaloniaFact]
    public async Task Copy_call_stack_copies_one_line_per_frame()
    {
        using var rig = new PanelRig();
        using var vm = await StoppedStack(rig);
        await vm.CopyCallStackAsync();
        var text = Assert.Single(rig.Window.Clipboard);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Contains("Sample.Program.Add(int a, int b)", lines[0]);
        Assert.Contains("Program.cs : 9", lines[0]);
    }

    [AvaloniaFact]
    public async Task A_stack_request_that_never_answers_leaves_the_model_responsive_and_cancelable()
    {
        using var rig = new PanelRig();
        rig.Session.Latency = FakeLatency.Hang;
        var vm = new CallStackViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.StatusText == "Loading…");
        rig.Resume(); // cancels the hung load: nothing waits for the adapter any more
        await PanelRig.Eventually(() => vm.StatusText == "Running…");
        vm.Dispose();
    }
}

public class ThreadsTests
{
    [AvaloniaFact]
    public async Task Threads_are_listed_with_state_and_location_and_the_current_one_is_marked()
    {
        using var rig = new PanelRig();
        rig.Session.Threads.Add(new DebugThread(2, "Worker"));
        rig.Session.Frames[2] = [new DebugStackFrame(300, "Worker.Run()", new DebugSource("w.cs", "/work/w.cs"), 4, 0)];
        using var vm = new ThreadsViewModel(rig.Services);
        await PanelRig.Eventually(() => vm.Rows.Count == 2, what: "threads while running");
        Assert.All(vm.Rows, r => Assert.Equal("Running", r.State));

        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.All(r => r.State == "Stopped" && r.Location.Length > 0), what: "stopped + locations");
        Assert.Equal("Sample.Program.Add(int a, int b)", vm.Rows[0].Location);
        Assert.Equal("Worker.Run()", vm.Rows[1].Location);
        Assert.True(vm.Rows[0].IsCurrent);
        Assert.False(vm.Rows[1].IsCurrent);

        rig.Resume();
        await PanelRig.Eventually(() => vm.Rows.All(r => r.State == "Running" && r.Location.Length == 0), what: "running again");
    }

    [AvaloniaFact]
    public async Task Switching_threads_focuses_the_threads_top_frame_and_opens_its_source()
    {
        using var rig = new PanelRig();
        rig.Session.Threads.Add(new DebugThread(2, "Worker"));
        rig.Session.Frames[2] = [new DebugStackFrame(300, "Worker.Run()", new DebugSource("w.cs", "/work/w.cs"), 4, 0)];
        using var vm = new ThreadsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 2 && vm.Rows[1].Location.Length > 0);

        await vm.SwitchToAsync(vm.Rows[1]);
        Assert.Equal(("s1", 2, 300), rig.Debug.FocusCalls.Single());
        Assert.True(vm.Rows[1].IsCurrent);
        Assert.Equal(new Uri("file:///work/w.cs"), rig.Navigation.Opened.Single().Uri);
        await PanelRig.Eventually(() => vm.Rows[1].IsCurrent && !vm.Rows[0].IsCurrent);
    }

    [AvaloniaFact]
    public async Task Thread_events_refresh_the_list()
    {
        using var rig = new PanelRig();
        using var vm = new ThreadsViewModel(rig.Services);
        await PanelRig.Eventually(() => vm.Rows.Count == 1);
        rig.Session.Threads.Add(new DebugThread(7, "Pool"));
        rig.Session.ThreadEmitter.Fire(new DebugThreadEventArgs(rig.Session, 7, "started"));
        await PanelRig.Eventually(() => vm.Rows.Count == 2, what: "thread started");
    }

    [AvaloniaFact]
    public async Task No_session_means_an_empty_list_with_a_status()
    {
        using var rig = new PanelRig(withSession: false);
        using var vm = new ThreadsViewModel(rig.Services);
        await PanelRig.Eventually(() => vm.StatusText == "Not debugging");
        Assert.True(vm.ShowStatus);
    }
}
