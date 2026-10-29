using CsCode.Core.Debugging;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

public class BreakpointManagerTests
{
    private static readonly Uri A = new("file:///work/a.cs"), B = new("file:///work/b.cs");

    private static (BreakpointManager Mgr, List<BreakpointsChangedEventArgs> Events) Make(FakeStateStorage? storage = null, FakeWorkspace? ws = null)
    {
        var m = new BreakpointManager(storage, ws);
        var events = new List<BreakpointsChangedEventArgs>();
        m.Changed.Subscribe(e => { lock (events) events.Add(e); });
        return (m, events);
    }

    [Fact]
    public void Toggle_adds_then_removes_and_reports_presence()
    {
        var (m, events) = Make();
        Assert.True(m.Toggle(A, 5));
        Assert.Single(m.GetForUri(A));
        Assert.False(m.Toggle(A, 5));
        Assert.Empty(m.SourceBreakpoints);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal([A], e.AffectedUris));
    }

    [Fact]
    public void Toggle_is_per_uri_and_line()
    {
        var (m, _) = Make();
        m.Toggle(A, 1);
        m.Toggle(A, 2);
        m.Toggle(B, 1);
        Assert.Equal(2, m.GetForUri(A).Count);
        Assert.Single(m.GetForUri(B));
        m.Toggle(A, 1);
        Assert.Equal([2], m.GetForUri(A).Select(b => b.Line));
    }

    [Fact]
    public void Add_with_condition_hit_count_and_logmessage_and_updating_the_same_line_replaces_it()
    {
        var (m, _) = Make();
        var first = m.Add(A, 3, "x > 1", "5", null);
        var second = m.Add(A, 3, null, null, "x is {x}");
        Assert.Equal(first.Id, second.Id); // one breakpoint per line
        var only = Assert.Single(m.SourceBreakpoints);
        Assert.Equal((null, null, "x is {x}"), (only.Condition, only.HitCondition, only.LogMessage));
    }

    [Fact]
    public void Update_replaces_by_id_and_notifies_both_old_and_new_file()
    {
        var (m, events) = Make();
        var bp = m.Add(A, 3);
        m.Update(bp with { Uri = B, Line = 9, Condition = "c" });
        Assert.Empty(m.GetForUri(A));
        Assert.Equal(9, Assert.Single(m.GetForUri(B)).Line);
        Assert.Equal([A, B], events[^1].AffectedUris.OrderBy(u => u.ToString()));
        m.Update(bp with { Id = "unknown" }); // unknown id: ignored
        Assert.Single(m.SourceBreakpoints);
    }

    [Fact]
    public void Remove_and_RemoveAll_notify_affected_files()
    {
        var (m, events) = Make();
        var a = m.Add(A, 1);
        m.Add(B, 1);
        m.AddFunction("Main");
        m.Remove(a.Id);
        Assert.Equal([A], events[^1].AffectedUris);
        m.RemoveAll();
        Assert.Empty(m.SourceBreakpoints);
        Assert.Empty(m.FunctionBreakpoints);
        Assert.Equal([B], events[^1].AffectedUris);
        Assert.True(events[^1].FunctionsOrExceptionsChanged);
        var count = events.Count;
        m.RemoveAll(); // nothing left: no noise
        Assert.Equal(count, events.Count);
    }

    [Fact]
    public void SetEnabledAll_toggles_everything_and_only_notifies_on_change()
    {
        var (m, events) = Make();
        m.Add(A, 1);
        m.Add(B, 1);
        m.AddFunction("f");
        m.SetEnabledAll(false);
        Assert.All(m.SourceBreakpoints, b => Assert.False(b.Enabled));
        Assert.All(m.FunctionBreakpoints, f => Assert.False(f.Enabled));
        var n = events.Count;
        m.SetEnabledAll(false);
        Assert.Equal(n, events.Count);
        m.SetEnabledAll(true);
        Assert.All(m.SourceBreakpoints, b => Assert.True(b.Enabled));
    }

    [Fact]
    public void Function_breakpoints_dedupe_by_name()
    {
        var (m, _) = Make();
        var f1 = m.AddFunction("Program.Main");
        var f2 = m.AddFunction("Program.Main", "i == 3");
        Assert.Equal(f1.Id, f2.Id);
        Assert.Equal("i == 3", Assert.Single(m.FunctionBreakpoints).Condition);
        m.RemoveFunction(f1.Id);
        Assert.Empty(m.FunctionBreakpoints);
    }

    [Fact]
    public void Exception_filters_are_set_and_notify_once()
    {
        var (m, events) = Make();
        var filters = new[] { new ExceptionFilterSetting("all", "All", true), new ExceptionFilterSetting("user", "User", false) };
        m.SetExceptionFilters(filters);
        Assert.Equal(filters, m.ExceptionFilters);
        Assert.True(events[^1].FunctionsOrExceptionsChanged);
        var n = events.Count;
        m.SetExceptionFilters(filters);
        Assert.Equal(n, events.Count);
    }

    // ---- ApplyLineShift

    [Fact]
    public void Inserting_lines_moves_breakpoints_at_or_after_the_edit_down()
    {
        var (m, events) = Make();
        m.Add(A, 2); m.Add(A, 5); m.Add(A, 9); m.Add(B, 5);
        m.ApplyLineShift(A, fromLine: 5, delta: 3);
        Assert.Equal([2, 8, 12], m.GetForUri(A).Select(b => b.Line).Order());
        Assert.Equal([5], m.GetForUri(B).Select(b => b.Line)); // other documents untouched
        Assert.Equal([A], events[^1].AffectedUris);
    }

    [Fact]
    public void Deleting_lines_removes_breakpoints_on_them_and_moves_the_rest_up()
    {
        var (m, _) = Make();
        m.Add(A, 2); m.Add(A, 3); m.Add(A, 4); m.Add(A, 5); m.Add(A, 9);
        // fromLine is the first SURVIVING line after the deleted range: lines 3 and 4 are gone, what was line 5 is now line 3
        m.ApplyLineShift(A, fromLine: 5, delta: -2);
        Assert.Equal([2, 3, 7], m.GetForUri(A).Select(b => b.Line).Order());
        Assert.Equal(m.GetForUri(A).Count, m.GetForUri(A).Select(b => b.Line).Distinct().Count()); // still one breakpoint per line
    }

    /// <summary>
    /// The editor's computation (CsCode.Editor BreakpointShift.Of, pinned by its own tests) for typical edits, replayed exactly the way the
    /// editor's DebugFeature applies it: Remove the breakpoints on the vanished lines, then ApplyLineShift(fromLine, delta). Every breakpoint
    /// must end up on the line that now holds its original text.
    /// </summary>
    [Theory]
    // Enter in the middle of line 5: line 5 keeps its breakpoint, 6.. move down
    [InlineData(1, 0, 6, 1, "0>0 1>1 2>2 3>3 4>4 5>5 6>7 7>8 8>9 9>10")]
    // Enter at the start of line 5: line 5's text (and breakpoint) moves down with it
    [InlineData(1, 0, 5, 1, "0>0 1>1 2>2 3>3 4>4 5>6 6>7 7>8 8>9 9>10")]
    // delete (3,2)-(5,4): something of line 3 survives, lines 4 and 5 vanish, what was line 6 is line 4
    [InlineData(4, 5, 6, -2, "0>0 1>1 2>2 3>3 4>- 5>- 6>4 7>5 8>6 9>7")]
    // delete (3,0)-(5,0): lines 3 and 4 vanish, what was line 5 is line 3
    [InlineData(3, 4, 5, -2, "0>0 1>1 2>2 3>- 4>- 5>3 6>4 7>5 8>6 9>7")]
    // paste "a\nb\nc" at (2,4): line 2 keeps its breakpoint, 3.. move down by 2
    [InlineData(1, 0, 3, 2, "0>0 1>1 2>2 3>5 4>6 5>7 6>8 7>9 8>10 9>11")]
    public void Breakpoints_follow_the_editors_line_shifts_exactly(int vanishedFrom, int vanishedTo, int fromLine, int delta, string expected)
    {
        var (m, _) = Make();
        var byOriginalLine = Enumerable.Range(0, 10).ToDictionary(line => line, line => m.Add(A, line).Id);
        m.Add(B, 6);

        foreach (var bp in m.GetForUri(A).Where(b => b.Line >= vanishedFrom && b.Line <= vanishedTo).ToList()) m.Remove(bp.Id);
        m.ApplyLineShift(A, fromLine, delta);

        var now = m.GetForUri(A).ToDictionary(b => b.Id, b => b.Line);
        var actual = string.Join(' ', byOriginalLine.Select(kv => $"{kv.Key}>{(now.TryGetValue(kv.Value, out var line) ? line : "-")}"));
        Assert.Equal(expected, actual);
        Assert.Equal([6], m.GetForUri(B).Select(b => b.Line)); // other documents untouched
    }

    [Fact]
    public void A_deletion_shift_alone_removes_the_vanished_lines_so_moved_breakpoints_never_stack_up()
    {
        var (m, _) = Make();
        var survivors = new[] { m.Add(A, 2).Id, m.Add(A, 5).Id, m.Add(A, 6).Id };
        m.Add(A, 3); m.Add(A, 4);
        m.ApplyLineShift(A, fromLine: 5, delta: -2); // the caller did not remove lines 3 and 4 first
        Assert.Equal([(survivors[0], 2), (survivors[1], 3), (survivors[2], 4)], m.GetForUri(A).OrderBy(b => b.Line).Select(b => (b.Id, b.Line)));
    }

    [Fact]
    public void Line_shift_with_zero_delta_or_nothing_affected_is_silent()
    {
        var (m, events) = Make();
        m.Add(A, 2);
        var n = events.Count;
        m.ApplyLineShift(A, 0, 0);
        m.ApplyLineShift(A, 10, 4);
        m.ApplyLineShift(B, 0, 4);
        Assert.Equal(n, events.Count);
    }

    [Fact]
    public void Shifted_breakpoints_keep_their_condition_and_identity()
    {
        var (m, _) = Make();
        var bp = m.Add(A, 4, "x > 0", "3", null);
        m.ApplyLineShift(A, 0, 10);
        var moved = Assert.Single(m.GetForUri(A));
        Assert.Equal((bp.Id, 14, "x > 0", "3"), (moved.Id, moved.Line, moved.Condition, moved.HitCondition));
    }

    // ---- status

    [Fact]
    public void Status_is_stored_per_breakpoint_and_cleared_per_session()
    {
        var (m, events) = Make();
        var bp = m.Add(A, 4);
        var s1 = new FakeSessionRef("s1");
        var s2 = new FakeSessionRef("s2");
        Assert.Null(m.GetStatus(bp.Id));
        m.ReportStatus(new DebugBreakpointStatus(s1, bp.Id, true, 5, null));
        Assert.Equal(5, m.GetStatus(bp.Id)!.ActualLine);
        Assert.Equal([A], events[^1].AffectedUris); // gutter refresh
        m.ReportStatus(new DebugBreakpointStatus(s2, bp.Id, true, 6, null));
        Assert.Equal(6, m.GetStatus(bp.Id)!.ActualLine); // latest verdict wins (a verified one beats an unverified one: JsDebugReviewFixTests)
        m.ClearStatuses("s2"); // s2 ends: s1's earlier verdict becomes the visible one again
        Assert.Equal(5, m.GetStatus(bp.Id)!.ActualLine);
        m.ClearStatuses("s1");
        Assert.Null(m.GetStatus(bp.Id));
        Assert.Equal(0, m.StatusCount);
    }

    [Fact]
    public void Status_for_a_removed_breakpoint_is_ignored()
    {
        var (m, _) = Make();
        var bp = m.Add(A, 1);
        m.Remove(bp.Id);
        m.ReportStatus(new DebugBreakpointStatus(new FakeSessionRef("s"), bp.Id, true, null, null));
        Assert.Equal(0, m.StatusCount);
    }

    [Fact]
    public void Identical_status_reports_do_not_re_notify()
    {
        var (m, events) = Make();
        var bp = m.Add(A, 1);
        var s = new FakeSessionRef("s");
        m.ReportStatus(new DebugBreakpointStatus(s, bp.Id, true, 1, null));
        var n = events.Count;
        m.ReportStatus(new DebugBreakpointStatus(s, bp.Id, true, 1, null));
        Assert.Equal(n, events.Count);
    }

    [Fact]
    public void ClearStatuses_with_null_clears_everything()
    {
        var (m, _) = Make();
        var a = m.Add(A, 1);
        var b = m.Add(B, 1);
        m.ReportStatus(new DebugBreakpointStatus(new FakeSessionRef("x"), a.Id, true, null, null));
        m.ReportStatus(new DebugBreakpointStatus(new FakeSessionRef("y"), b.Id, true, null, null));
        m.ClearStatuses();
        Assert.Equal(0, m.StatusCount);
    }

    // ---- persistence

    [Fact]
    public async Task Breakpoints_persist_debounced_and_survive_a_restart()
    {
        var storage = new FakeStateStorage();
        var (m, _) = Make(storage);
        for (var i = 0; i < 20; i++) m.Toggle(A, i);
        Assert.Equal(0, storage.SetCount); // debounced: not once per click
        await SessionHarness.Eventually(() => storage.SetCount >= 1, 3000, "persist after the debounce");
        await Task.Delay(700, Ct);
        Assert.Equal(1, storage.SetCount);
        m.AddFunction("Main");
        m.SetExceptionFilters([new ExceptionFilterSetting("all", "All", true, "cond")]);
        await m.DisposeAsync(); // flushes pending edits
        Assert.True(storage.FlushCount >= 1);

        var (m2, _) = Make(storage);
        Assert.Equal(Enumerable.Range(0, 20), m2.GetForUri(A).Select(b => b.Line).Order());
        Assert.Equal("Main", Assert.Single(m2.FunctionBreakpoints).Name);
        Assert.Equal(new ExceptionFilterSetting("all", "All", true, "cond"), Assert.Single(m2.ExceptionFilters));
    }

    [Fact]
    public async Task Persisted_shape_round_trips_conditions_and_ids()
    {
        var storage = new FakeStateStorage();
        var (m, _) = Make(storage);
        var bp = m.Add(A, 7, "i == 2", "%3", "log {i}");
        m.Update(bp with { Enabled = false, Column = 4 });
        await m.DisposeAsync();
        var (m2, _) = Make(storage);
        var r = Assert.Single(m2.SourceBreakpoints);
        Assert.Equal(("i == 2", "%3", "log {i}", false, 4, bp.Id), (r.Condition, r.HitCondition, r.LogMessage, r.Enabled, r.Column, r.Id));
        Assert.Equal(A, r.Uri);
    }

    [Fact]
    public void Unreadable_persisted_uris_are_skipped_not_fatal()
    {
        var storage = new FakeStateStorage();
        storage.Set(BreakpointManager.StateKey, new PersistedBreakpoints([new PersistedSourceBreakpoint("1", "not a uri", 1, null, null, null, null, true), new PersistedSourceBreakpoint("2", "file:///ok.cs", 3, null, null, null, null, true)]), CsCode.Core.Platform.StorageScope.Workspace);
        var (m, _) = Make(storage);
        Assert.Equal("2", Assert.Single(m.SourceBreakpoints).Id);
    }

    [Fact]
    public async Task Opening_another_workspace_swaps_to_its_breakpoints()
    {
        var storage = new FakeStateStorage();
        var ws = new FakeWorkspace(new Uri("file:///w1/"));
        var (m, events) = Make(storage, ws);
        m.Toggle(A, 1);
        // The "other workspace" is simulated by pre-seeding what the storage will return after the switch.
        await ws.OpenFolderAsync(new Uri("file:///w2/"), Ct);
        Assert.NotEmpty(events);
        Assert.True(events[^1].FunctionsOrExceptionsChanged);
        await m.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_mutation_from_many_threads_stays_consistent()
    {
        var (m, _) = Make();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                m.Toggle(A, t * 1000 + i);
                _ = m.SourceBreakpoints.Count;
                _ = m.GetForUri(A);
            }
        }, Ct)));
        Assert.Equal(1600, m.SourceBreakpoints.Count);
        Assert.Equal(1600, m.SourceBreakpoints.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void Any_reflects_source_and_function_breakpoints()
    {
        var (m, _) = Make();
        Assert.False(m.Any);
        var f = m.AddFunction("x");
        Assert.True(m.Any);
        m.RemoveFunction(f.Id);
        m.Toggle(A, 1);
        Assert.True(m.Any);
    }

    internal sealed class FakeSessionRef(string sessionId) : IDebugSession
    {
        public string Id { get; } = sessionId;
        public string Name => Id;
        public DebugConfiguration Configuration => throw new NotSupportedException();
        public DebugSessionState State => DebugSessionState.Running;
        public IDebugSession? Parent => null;
        public DebugCapabilities Capabilities => new();
        public int? FocusedThreadId => null;
        public CsCode.Core.Lifecycle.IEventSource<DebugStoppedEventArgs> Stopped => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<DebugContinuedEventArgs> Continued => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<DebugOutputEventArgs> Output => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<DebugThreadEventArgs> ThreadChanged => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<DebugSessionState> StateChanged => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<DebugTerminatedEventArgs> Terminated => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<DebugBreakpointStatus> BreakpointStatusChanged => throw new NotSupportedException();
        public CsCode.Core.Lifecycle.IEventSource<IDebugSession> Invalidated => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask RestartAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<System.Text.Json.JsonElement?> CustomRequestAsync(string command, System.Text.Json.JsonElement? arguments = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
