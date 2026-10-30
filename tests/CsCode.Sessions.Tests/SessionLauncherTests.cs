using System.Diagnostics;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using Xunit;

namespace CsCode.Sessions.Tests;

/// <summary>The launcher against in-process fake CsCode processes (real IPC, real locks; no processes are started).</summary>
public class SessionLauncherTests
{
    private static SessionLauncher Launcher(TestRuntime rt, FakeSpawner spawner, LaunchSettings? settings = null, StringWriter? output = null, StringWriter? error = null) =>
        new(rt.Runtime, rt.Store, settings ?? new LaunchSettings(), spawner, output ?? new StringWriter(), error ?? new StringWriter(),
            new LauncherTimeouts { Handshake = TimeSpan.FromMilliseconds(300), ShowReply = TimeSpan.FromSeconds(5), StartHello = TimeSpan.FromSeconds(5), QuitWait = TimeSpan.FromSeconds(1) });

    [Fact]
    public async Task A_resident_session_is_shown_with_the_paths()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var work = new FakeHost(rt.Runtime, "work", ProcessStates.Hidden);
        using var spare = new FakeHost(rt.Runtime, null, ProcessStates.Spare);

        var outcome = await Launcher(rt, spawner).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work", Targets = [new LaunchTarget("/p/a.cs", false, 3, 2)] });

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(work.Pid, outcome.TargetPid);
        Assert.Equal(ProcessStates.Shown, work.State);
        var show = Assert.Single(work.Received, m => m.Type == IpcVerbs.Show);
        Assert.Equal(new IpcPath("/p/a.cs", 3, 2), Assert.Single(show.Paths!));
        Assert.Empty(spawner.Spawned);
        Assert.Equal(ProcessStates.Spare, spare.State);
    }

    [Fact]
    public async Task Remote_targets_reach_the_session_in_its_show_request()
    {
        // T5: `cscode ssh://me@box/home/me/src` in background mode: the launcher forwards the remote folder to the session process (which runs
        // remote.openFolder); before, it forwarded only local paths and the remote target was dropped.
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var work = new FakeHost(rt.Runtime, "work", ProcessStates.Hidden);

        var outcome = await Launcher(rt, spawner).RunAsync(new LaunchRequest(LaunchKind.Open)
        {
            Session = "work", RemoteTargets = ["ssh://me@box:2222/home/me/my%20src", "ssh://other/x"],
        });

        Assert.Equal(0, outcome.ExitCode);
        var show = Assert.Single(work.Received, m => m.Type == IpcVerbs.Show);
        Assert.Equal(["ssh://me@box:2222/home/me/my%20src", "ssh://other/x"], show.Remotes);
        Assert.Empty(show.Paths!);
        Assert.False(show.Wait); // nothing local to wait for
    }

    [Fact]
    public async Task A_saved_session_claims_the_spare_and_a_new_spare_is_started()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var spare = new FakeHost(rt.Runtime, null, ProcessStates.Spare);
        rt.Store.Save(new SessionDocument { Name = "work" });

        var outcome = await Launcher(rt, spawner, new LaunchSettings { WarmSpare = true }).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(spare.Pid, outcome.TargetPid);
        Assert.Equal(("work", ProcessStates.Shown), (spare.Session, spare.State));
        Assert.Equal([IpcVerbs.Claim, IpcVerbs.Show], spare.Received.Where(m => m.Type != IpcVerbs.Hello).Select(m => m.Type));
        Assert.Equal(0, spawner.SessionStarts);
        Assert.Equal(1, spawner.SpareStarts);
    }

    [Fact]
    public async Task Cold_start_spawns_a_session_process_waits_for_it_and_starts_a_spare()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);

        var outcome = await Launcher(rt, spawner, new LaunchSettings { WarmSpare = true }).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        var started = Assert.Single(spawner.Spawned, a => !a.Contains("--spare"));
        Assert.Equal(["--host", "--session", "work"], started);
        Assert.Equal(1, spawner.SpareStarts);
        var host = Assert.Single(spawner.Hosts, h => h.Session == "work");
        Assert.Equal(host.Pid, outcome.TargetPid);
        Assert.Contains(host.Received, m => m.Type == IpcVerbs.Show);
    }

    [Fact]
    public async Task A_spare_that_refuses_the_claim_falls_back_to_a_fresh_process()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var spare = new FakeHost(rt.Runtime, null, ProcessStates.Spare) { RefuseClaim = true };
        var error = new StringWriter();

        var outcome = await Launcher(rt, spawner, error: error).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(1, spawner.SessionStarts);
        Assert.Contains("did not take the session", error.ToString());
    }

    [Fact]
    public async Task A_wedged_owner_is_not_waited_for_and_a_copy_opens_instead()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "work" });
        using var held = FileLock.TryAcquire(rt.Store.LockPathOf("work")); // its (wedged) owner holds the lock
        using var wedged = new FakeHost(rt.Runtime, "work", ProcessStates.Shown, pid: Environment.ProcessId) { Wedged = true };
        var error = new StringWriter();
        var clock = Stopwatch.StartNew();

        var outcome = await Launcher(rt, spawner, error: error).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.True(clock.ElapsedMilliseconds < 4000, $"the launcher took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(0, outcome.ExitCode);
        var start = Assert.Single(spawner.Spawned, a => !a.Contains("--spare"));
        Assert.Equal(["--host", "--session", "window-1", "--seed-from", "work", "--unnamed"], start);
        Assert.Contains("does not respond", error.ToString());
    }

    [Fact]
    public async Task Concurrent_launches_of_one_session_end_with_one_owner()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        var settings = new LaunchSettings { WarmSpare = false };

        var runs = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Launcher(rt, spawner, settings).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "x" }))).ToArray();
        var outcomes = await Task.WhenAll(runs);

        Assert.All(outcomes, o => Assert.Equal(0, o.ExitCode));
        Assert.Equal(1, spawner.SessionStarts);
        Assert.Single(outcomes.Select(o => o.TargetPid).Distinct());
    }

    [Fact]
    public async Task A_process_that_dies_during_start_up_is_reported()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime) { FailToStart = true };
        var error = new StringWriter();

        var outcome = await Launcher(rt, spawner, new LaunchSettings { WarmSpare = false }, error: error).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "x" });

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("exited during start-up", error.ToString());
    }

    [Fact]
    public async Task Showing_past_the_maximum_quits_the_least_recently_shown_hidden_session()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var a = new FakeHost(rt.Runtime, "a", ProcessStates.Hidden) { LastShown = DateTimeOffset.UtcNow.AddMinutes(-30) };
        using var b = new FakeHost(rt.Runtime, "b", ProcessStates.Hidden) { LastShown = DateTimeOffset.UtcNow.AddMinutes(-5) };
        using var spare = new FakeHost(rt.Runtime, null, ProcessStates.Spare);

        await Launcher(rt, spawner, new LaunchSettings { MaxResidentSessions = 2 }).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "c" });

        var deadline = Stopwatch.StartNew();
        while (!a.QuitRequested && deadline.ElapsedMilliseconds < 3000) await Task.Delay(10);
        Assert.True(a.QuitRequested, "the least recently shown hidden session was asked to quit");
        Assert.False(b.QuitRequested);
        Assert.Equal("c", spare.Session);
    }

    [Fact]
    public async Task List_prints_saved_and_resident_sessions_and_the_spare()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "saved-only", LastUsedUtc = DateTimeOffset.UtcNow, Folders = ["file:///home/u/p/"] });
        using var work = new FakeHost(rt.Runtime, "work", ProcessStates.Hidden);
        using var spare = new FakeHost(rt.Runtime, null, ProcessStates.Spare);
        var output = new StringWriter();

        var outcome = await Launcher(rt, spawner, output: output).RunAsync(new LaunchRequest(LaunchKind.ListSessions));

        Assert.Equal(0, outcome.ExitCode);
        var text = output.ToString();
        Assert.Contains("SESSION", text);
        Assert.Matches(@"saved-only\s+saved", text);
        Assert.Contains("/home/u/p", text);
        Assert.Matches($@"work\s+hidden\s+{work.Pid}", text);
        Assert.Matches($@"\(warm spare\)\s+spare\s+{spare.Pid}", text);
    }

    [Fact]
    public async Task Quit_all_asks_every_process_to_quit()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var a = new FakeHost(rt.Runtime, "a", ProcessStates.Shown);
        using var s = new FakeHost(rt.Runtime, null, ProcessStates.Spare);

        await Launcher(rt, spawner).RunAsync(new LaunchRequest(LaunchKind.Quit) { All = true });

        Assert.True(a.QuitRequested);
        Assert.True(s.QuitRequested);
        Assert.True(Assert.Single(a.Received, m => m.Type == IpcVerbs.Quit).Force);
    }

    [Fact]
    public async Task Cold_start_with_the_default_settings_starts_no_spare()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);

        var outcome = await Launcher(rt, spawner).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal((1, 0), (spawner.SessionStarts, spawner.SpareStarts));
        Assert.Equal(["--host", "--session", "work"], Assert.Single(spawner.Spawned));
    }

    [Fact]
    public async Task Background_starts_nothing_by_default_and_says_why()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "r", WasResident = true });
        var output = new StringWriter();

        var outcome = await Launcher(rt, spawner, output: output).RunAsync(new LaunchRequest(LaunchKind.Background));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Empty(spawner.Spawned);
        Assert.Contains("nothing to start in the background", output.ToString());
        Assert.Contains(LaunchSettings.WarmSpareKey, output.ToString());
    }

    [Fact]
    public async Task Background_starts_only_the_spare_with_the_warm_spare_on()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "r", WasResident = true });
        var output = new StringWriter();

        var outcome = await Launcher(rt, spawner, new LaunchSettings { WarmSpare = true }, output: output).RunAsync(new LaunchRequest(LaunchKind.Background));
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal((0, 1), (spawner.SessionStarts, spawner.SpareStarts));
        Assert.DoesNotContain("nothing to start", output.ToString());

        using var spawner2 = new FakeSpawner(rt.Runtime);
        await Launcher(rt, spawner2, new LaunchSettings { RestoreOnLogin = true, WarmSpare = false }).RunAsync(new LaunchRequest(LaunchKind.Background));
        Assert.Equal(["--host", "--session", "r", "--hidden"], Assert.Single(spawner2.Spawned));
    }

    [Fact]
    public async Task An_unusable_runtime_directory_reports_exit_code_2()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix permissions");
        using var rt = new TestRuntime();
        var file = Path.Combine(rt.Root, "not-a-dir");
        File.WriteAllText(file, "");
        var broken = new RuntimeDirectory(Path.Combine(file, "run"), "");
        using var spawner = new FakeSpawner(broken);
        var launcher = new SessionLauncher(broken, rt.Store, new LaunchSettings(), spawner, new StringWriter(), new StringWriter());

        var outcome = await launcher.RunAsync(new LaunchRequest(LaunchKind.Open));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Empty(spawner.Spawned);
    }
}
