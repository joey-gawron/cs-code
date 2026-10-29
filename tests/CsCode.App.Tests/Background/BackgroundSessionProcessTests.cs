using System.Diagnostics;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// Background mode end to end with REAL processes (docs/SESSIONS.md, "Tests"): the launcher, the warm spare, session processes, hiding on close,
/// the hidden-session trim, hot exit across a kill -9, eviction and <c>--quit --all</c>. Each test runs in its own <c>CSCODE_HOME</c>
/// (<see cref="SessionProcessHarness"/>) and leaves no process behind.
/// </summary>
public class BackgroundSessionProcessTests
{
    private const string Source = """
        namespace Demo;

        public class Widget
        {
            public int Size { get; set; }

            public void Grow()
            {
                Size++;
            }

            public void Shrink()
            {
                Size--;
            }
        }
        // line 17
        // line 18
        // line 19
        // line 20
        """;

    private static async Task<int> SpareAsync(SessionProcessHarness h)
    {
        var background = await h.RunAsync("--background");
        Assert.True(background.ExitCode == 0, background.Error);
        var spare = await h.WaitForAsync(p => p.IsSpare, what: "warm spare");
        // The spare is claimable at once; give it time to finish its off-screen warm-up and trim so the measurement is of a warm spare
        // (with tracing, wait for its "warm" mark; without, a fixed settle time).
        if (h.Trace() != "(no trace)") await SessionProcessHarness.WaitUntilAsync(() => h.Trace().Contains($"pid {spare.Pid} +") && h.Trace().Contains("host: spare warm"), 60_000);
        else await Task.Delay(5000);
        await Task.Delay(1000);
        return spare.Pid;
    }

    private static ResidentProcess Owner(SessionProcessHarness h, string session) =>
        h.Discover().FirstOrDefault(p => p.Session == session) ?? throw new InvalidOperationException($"no process owns '{session}'\n{h.Trace()}");

    [Fact]
    public async Task Spare_claim_edit_hide_trim_show_and_hot_exit_across_kill_9()
    {
        using var h = new SessionProcessHarness("\"cscode.background.trimAfterSeconds\": 1");
        var file = h.WriteFile("Widget.cs", Source);
        var uri = new Uri(file).ToString();
        var sparePid = await SpareAsync(h);

        // 1. A session that is not running claims the warm spare (and a new spare is started).
        var open = await h.RunAsync("--session", "A", h.Workspace, file + ":10:5");
        Assert.True(open.ExitCode == 0, open.Error);
        var a = Owner(h, "A");
        Assert.Equal(sparePid, a.Pid);
        Assert.Equal(ProcessStates.Shown, a.State);
        var state = h.Inspect(a.Pid);
        Assert.True(state.Visible);
        Assert.Equal(new Uri(h.Workspace + "/").ToString(), Assert.Single(state.Folders));
        var tab = Assert.Single(state.Tabs);
        Assert.Equal((uri, 9, 4), (tab.Uri, tab.CaretLine, tab.CaretColumn));
        Assert.Contains("A — ws", state.Title);
        await h.WaitForAsync(p => p.IsSpare && p.Pid != sparePid, what: "replacement spare");

        // 2. Unsaved edit, a fold, a caret move.
        h.Must(a.Pid, new IpcMessage { Type = IpcVerbs.TestEdit, Uri = uri, Line = 2, Column = 0, Text = "// UNSAVED EDIT\n" });
        // Folding regions are computed asynchronously after the edit: fold the Shrink() body (lines 12-15 now) once its region exists.
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() =>
        {
            h.Must(a.Pid, new IpcMessage { Type = IpcVerbs.TestFold, Uri = uri, Line = 12 });
            h.Must(a.Pid, new IpcMessage { Type = IpcVerbs.TestFold, Uri = uri, Line = 13 });
            return Assert.Single(h.Inspect(a.Pid).Tabs).Folded.Count > 0;
        }, 15_000), "no folding region appeared");
        h.Must(a.Pid, new IpcMessage { Type = IpcVerbs.TestCaret, Uri = uri, Line = 9, Column = 8 });
        var edited = h.Inspect(a.Pid);
        var editedTab = Assert.Single(edited.Tabs);
        Assert.True(editedTab.Dirty);
        Assert.Equal((9, 8), (editedTab.CaretLine, editedTab.CaretColumn));
        var folds = editedTab.Folded;
        Assert.NotEmpty(folds);

        // 3. Closing the window hides it: the process stays, its endpoint answers, nothing was prompted.
        h.Must(a.Pid, new IpcMessage { Type = IpcVerbs.TestCloseWindow });
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Discover().Any(p => p.Pid == a.Pid && p.State == ProcessStates.Hidden), 10_000));
        Assert.False(h.Inspect(a.Pid).Visible);

        // 4. After trimAfterSeconds the hidden session released its editor views and trimmed its memory.
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Inspect(a.Pid).Trims >= 1, 20_000), "the hidden session was not trimmed");
        var trimmed = h.Inspect(a.Pid);
        Assert.Equal(0, trimmed.LiveViews);
        Assert.True(trimmed.LastTrimWorkingSet > 0);

        // 5. cscode --session A shows the same window again: same tab, caret, folds, the unsaved edit, a live editor view.
        var show = await h.RunAsync("--session", "A");
        Assert.True(show.ExitCode == 0, show.Error);
        var back = h.Inspect(a.Pid, withText: true);
        Assert.True(back.Visible);
        Assert.True(back.LiveViews >= 1, "the active editor view was not recreated before the window was shown");
        var backTab = Assert.Single(back.Tabs);
        Assert.True(backTab.Dirty);
        Assert.Contains("// UNSAVED EDIT", backTab.Text);
        Assert.Equal((9, 8), (backTab.CaretLine, backTab.CaretColumn));
        // The recreated view computes its folding regions asynchronously, then re-applies the saved folds.
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => Assert.Single(h.Inspect(a.Pid).Tabs).Folded.SequenceEqual(folds), 15_000),
            $"folds after show: [{string.Join(",", Assert.Single(h.Inspect(a.Pid).Tabs).Folded)}], expected [{string.Join(",", folds)}]");

        // 6. kill -9: the next launch restores the session from its autosave and hot-exit backup (no save prompt, the edit is there).
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Store.Backups.List("A").Count == 1, 10_000), "no hot-exit backup was written");
        Process.GetProcessById(a.Pid).Kill();
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !Infrastructure.ProcessProbe.IsRunning(a.Pid), 10_000));
        var relaunch = await h.RunAsync("--session", "A");
        Assert.True(relaunch.ExitCode == 0, relaunch.Error);
        var restored = Owner(h, "A");
        Assert.NotEqual(a.Pid, restored.Pid);
        var after = h.Inspect(restored.Pid, withText: true);
        var afterTab = Assert.Single(after.Tabs);
        Assert.True(afterTab.Dirty, "the unsaved edit must come back as unsaved");
        Assert.Contains("// UNSAVED EDIT", afterTab.Text);
        Assert.Equal(Source.Split('\n').Length + 1, afterTab.Text!.Split('\n').Length);
        Assert.Equal((9, 8), (afterTab.CaretLine, afterTab.CaretColumn)); // the autosaved view state
        Assert.Equal(new Uri(h.Workspace + "/").ToString(), Assert.Single(after.Folders));
        Assert.DoesNotContain("UNSAVED", File.ReadAllText(file)); // hot exit never writes the user's file

        // 7. --quit --all: no process and no endpoint is left.
        var quit = await h.RunAsync("--quit", "--all");
        Assert.True(quit.ExitCode == 0, quit.Error);
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.ProcessesOfThisHome().Count == 0, 15_000), "processes left: " + string.Join(", ", h.ProcessesOfThisHome()));
        Assert.Empty(Directory.GetFiles(h.Runtime.Path, "*.sock"));
        Assert.False(h.Store.Load("A").Session!.WasResident);
    }

    [Fact]
    public async Task Latency_resident_show_and_warm_spare_claim_are_within_budget()
    {
        using var h = new SessionProcessHarness(trace: false);
        h.WriteFile("Widget.cs", Source);
        await SpareAsync(h);

        var claim = await h.RunAsync("--session", "L", Path.Combine(h.Workspace, "Widget.cs"));
        Assert.True(claim.ExitCode == 0, claim.Error);
        var pid = Owner(h, "L").Pid;
        h.Must(pid, new IpcMessage { Type = IpcVerbs.Hide });
        var resident = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var run = await h.RunAsync("--session", "L");
            Assert.True(run.ExitCode == 0, run.Error);
            resident.Add(run.Elapsed.TotalMilliseconds);
            h.Must(pid, new IpcMessage { Type = IpcVerbs.Hide });
        }
        var median = resident.Order().ElementAt(resident.Count / 2);
        TestContext.Current.SendDiagnosticMessage($"invocation→painted: warm spare {claim.Elapsed.TotalMilliseconds:0} ms; resident {string.Join(", ", resident.Select(r => r.ToString("0")))} ms (median {median:0})");
        Assert.True(median < 150, $"resident hot load median {median:0} ms (budget 150 ms): {string.Join(", ", resident.Select(r => r.ToString("0")))}\n{h.Trace()}");
        Assert.True(claim.Elapsed.TotalMilliseconds < 500, $"warm-spare open took {claim.Elapsed.TotalMilliseconds:0} ms (budget 500 ms)\n{h.Trace()}");
    }

    [Fact]
    public async Task Cold_start_without_a_spare_opens_the_session_and_reports_its_latency()
    {
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": false");
        var file = h.WriteFile("notes.txt", "hello\n");
        var cold = await h.RunAsync("--session", "C", file);
        Assert.True(cold.ExitCode == 0, cold.Error);
        TestContext.Current.SendDiagnosticMessage($"cold start invocation→painted: {cold.Elapsed.TotalMilliseconds:0} ms");
        var c = Owner(h, "C");
        Assert.Equal(new Uri(file).ToString(), Assert.Single(h.Inspect(c.Pid).Tabs).Uri);
        Assert.DoesNotContain(h.Discover(), p => p.IsSpare); // warmSpare: false
    }

    [Fact]
    public async Task Beyond_max_resident_sessions_the_least_recently_shown_hidden_one_quits()
    {
        using var h = new SessionProcessHarness("\"cscode.background.maxResidentSessions\": 2, \"cscode.background.warmSpare\": false");
        foreach (var name in new[] { "A", "B" })
        {
            var run = await h.RunAsync("--session", name);
            Assert.True(run.ExitCode == 0, run.Error);
        }
        var a = Owner(h, "A");
        var b = Owner(h, "B");
        h.Must(a.Pid, new IpcMessage { Type = IpcVerbs.Hide });
        await Task.Delay(50);
        h.Must(b.Pid, new IpcMessage { Type = IpcVerbs.Hide });

        var c = await h.RunAsync("--session", "C");
        Assert.True(c.ExitCode == 0, c.Error);

        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !Infrastructure.ProcessProbe.IsRunning(a.Pid), 20_000), "A (least recently shown, hidden) should have quit");
        Assert.True(Infrastructure.ProcessProbe.IsRunning(b.Pid));
        Assert.Equal(["B", "C"], h.Discover().Select(p => p.Session).OfType<string>().Order());
        Assert.False(h.Store.Load("A").Session!.WasResident); // persisted on its way out
    }

    [Fact]
    public async Task Concurrent_launches_of_one_session_start_one_process()
    {
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": false");
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => h.RunAsync("--session", "X")));
        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Error));
        Assert.Single(h.Discover(), p => p.Session == "X");
        Assert.Single(h.ProcessesOfThisHome());
    }

    [Fact]
    public async Task Wait_blocks_until_the_file_tab_is_closed()
    {
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": false");
        var file = h.WriteFile("COMMIT_EDITMSG", "message\n");
        var first = await h.RunAsync("--session", "W");
        Assert.True(first.ExitCode == 0, first.Error);
        var w = Owner(h, "W");

        using var waiting = h.Start("--session", "W", "--wait", file);
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Inspect(w.Pid).Tabs.Any(t => t.Uri == new Uri(file).ToString()), 20_000));
        await Task.Delay(500);
        Assert.False(waiting.HasExited, "--wait returned before the tab was closed");

        h.Must(w.Pid, new IpcMessage { Type = IpcVerbs.TestCloseTab, Uri = new Uri(file).ToString() });
        Assert.True(waiting.WaitForExit(10_000), "--wait did not return after the tab closed");
        Assert.Equal(0, waiting.ExitCode);
    }

    [Fact]
    public async Task A_hidden_trimmed_session_idles_at_zero_cpu()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("/proc CPU accounting");
        using var h = new SessionProcessHarness("\"cscode.background.trimAfterSeconds\": 1, \"cscode.background.warmSpare\": false");
        var file = h.WriteFile("Widget.cs", Source);
        var open = await h.RunAsync("--session", "Idle", h.Workspace, file);
        Assert.True(open.ExitCode == 0, open.Error);
        var pid = Owner(h, "Idle").Pid;
        h.Must(pid, new IpcMessage { Type = IpcVerbs.Hide });
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Inspect(pid).Trims >= 1, 20_000));
        await Task.Delay(2000); // let the trim's own work finish

        var before = SessionProcessHarness.CpuSeconds(pid);
        await Task.Delay(TimeSpan.FromSeconds(30));
        var used = SessionProcessHarness.CpuSeconds(pid) - before;
        TestContext.Current.SendDiagnosticMessage($"hidden session CPU over 30 s: {used:0.00} s ({used / 30 * 100:0.00} %)");
        Assert.True(used / 30 < 0.01, $"a hidden session used {used:0.00} s of CPU in 30 s ({used / 30 * 100:0.0} %)");
    }

    [Fact]
    public async Task Two_hundred_hide_show_cycles_keep_memory_flat()
    {
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": false");
        var file = h.WriteFile("Widget.cs", Source);
        var open = await h.RunAsync("--session", "Soak", h.Workspace, file);
        Assert.True(open.ExitCode == 0, open.Error);
        var pid = Owner(h, "Soak").Pid;

        void Cycles(int n)
        {
            for (var i = 0; i < n; i++)
            {
                h.Must(pid, new IpcMessage { Type = IpcVerbs.Hide });
                h.Must(pid, new IpcMessage { Type = IpcVerbs.Show });
            }
        }
        Cycles(20);
        var baseline = h.Inspect(pid, collect: true);
        Cycles(180);
        var end = h.Inspect(pid, collect: true);
        var heapGrowthMb = (end.ManagedHeap - baseline.ManagedHeap) / (1024.0 * 1024.0);
        var wsGrowthMb = (end.WorkingSet - baseline.WorkingSet) / (1024.0 * 1024.0);
        TestContext.Current.SendDiagnosticMessage($"200 hide/show cycles: managed heap {baseline.ManagedHeap / 1048576.0:0.0} → {end.ManagedHeap / 1048576.0:0.0} MB, working set {baseline.WorkingSet / 1048576.0:0} → {end.WorkingSet / 1048576.0:0} MB");
        Assert.True(heapGrowthMb < 4, $"managed heap grew {heapGrowthMb:0.0} MB over 180 cycles");
        Assert.True(wsGrowthMb < 40, $"working set grew {wsGrowthMb:0.0} MB over 180 cycles");
        Assert.True(end.Visible);
    }
}
