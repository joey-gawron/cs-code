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
    public async Task A_session_whose_process_is_quitting_is_started_afresh_once_it_lets_go_not_copied()
    {
        // sessions-3: the quitting owner still held the lock (its final save, a slow language server's shutdown): the launcher took it for a
        // wedged one and opened a copy of the session, with a false "does not respond" warning.
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "work" });
        var held = FileLock.TryAcquire(rt.Store.LockPathOf("work"))!;
        using var leaving = new FakeHost(rt.Runtime, "work", ProcessStates.Quitting);
        var error = new StringWriter();
        _ = Task.Run(async () => { await Task.Delay(400); held.Dispose(); }); // its last save is done

        var outcome = await new SessionLauncher(rt.Runtime, rt.Store, new LaunchSettings(), spawner, new StringWriter(), error,
            new LauncherTimeouts { Handshake = TimeSpan.FromMilliseconds(300), ShowReply = TimeSpan.FromSeconds(5), StartHello = TimeSpan.FromSeconds(5), QuitWait = TimeSpan.FromSeconds(10) })
            .RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(["--host", "--session", "work"], Assert.Single(spawner.Spawned));
        Assert.DoesNotContain("does not respond", error.ToString());
        held.Dispose();
    }

    [Fact]
    public async Task A_session_an_older_build_still_holds_in_the_runtime_directory_is_awaited_never_given_a_second_writer()
    {
        // sessions-7: after an upgrade, a process of the older build is in the final save of its quit; it holds only <runtime>/locks/work.lock.
        // The launcher saw the session as free and started a second writer next to it.
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "work" });
        var older = FileLock.TryAcquire(rt.Runtime.LegacySessionLockPath("work"))!;
        using var leaving = new FakeHost(rt.Runtime, "work", ProcessStates.Quitting);
        var error = new StringWriter();
        _ = Task.Run(async () => { await Task.Delay(400); older.Dispose(); }); // its last save is done

        var clock = Stopwatch.StartNew();
        var outcome = await new SessionLauncher(rt.Runtime, rt.Store, new LaunchSettings(), spawner, new StringWriter(), error,
            new LauncherTimeouts { Handshake = TimeSpan.FromMilliseconds(300), ShowReply = TimeSpan.FromSeconds(5), StartHello = TimeSpan.FromSeconds(5), QuitWait = TimeSpan.FromSeconds(10) })
            .RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(clock.ElapsedMilliseconds >= 350, "the session was started while the older process still held it");
        Assert.Equal(["--host", "--session", "work"], Assert.Single(spawner.Spawned));
        Assert.DoesNotContain("does not respond", error.ToString());
        older.Dispose();
    }

    [Fact]
    public async Task A_data_directory_that_cannot_hold_locks_falls_back_at_once_instead_of_waiting()
    {
        // sessions-7: a read-only data directory (a read-only portable CSCODE_HOME) made every launch wait 15 s for the launcher lock, then start
        // a process that refused with "owned by another CsCode process": no window at all. Now the launcher says background mode is unavailable
        // (exit 2: opening a window runs it in the launcher's process, as with an unusable runtime directory).
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        var ro = FileLockTests.ReadOnlyDirectory(rt.Store.SessionsDir);
        if (ro is null) Assert.Skip("this user can write into a mode 0500 directory");
        try
        {
            var error = new StringWriter();
            var clock = Stopwatch.StartNew();
            var outcome = await new SessionLauncher(rt.Runtime, rt.Store, new LaunchSettings(), spawner, new StringWriter(), error)
                .RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" }); // the default 15 s launcher-lock wait

            Assert.Equal(2, outcome.ExitCode);
            Assert.True(clock.ElapsedMilliseconds < 5000, $"the launcher took {clock.ElapsedMilliseconds} ms");
            Assert.Empty(spawner.Spawned);
            Assert.Contains("background mode unavailable", error.ToString());
        }
        finally { FileLockTests.MakeWritable(ro); }
    }

    [Fact]
    public async Task A_quitting_process_that_keeps_the_session_too_long_counts_as_wedged()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "work" });
        using var held = FileLock.TryAcquire(rt.Store.LockPathOf("work"));
        using var leaving = new FakeHost(rt.Runtime, "work", ProcessStates.Quitting);
        var error = new StringWriter();

        var outcome = await Launcher(rt, spawner, error: error).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" }); // QuitWait 1 s

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(["--host", "--session", "window-1", "--seed-from", "work", "--unnamed"], Assert.Single(spawner.Spawned));
        Assert.Contains("does not respond", error.ToString());
    }

    [Fact]
    public async Task A_show_that_finds_the_session_quitting_opens_it_afresh()
    {
        // sessions-3: a show that reached a quitting process replied OK and nothing appeared.
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        using var closing = new FakeHost(rt.Runtime, "work", ProcessStates.Shown) { QuitsBeforeShow = true };

        var outcome = await Launcher(rt, spawner).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "work" });

        Assert.Equal(0, outcome.ExitCode);
        var fresh = Assert.Single(spawner.Hosts, h => h.Session == "work");
        Assert.Equal(fresh.Pid, outcome.TargetPid);
        Assert.Contains(fresh.Received, m => m.Type == IpcVerbs.Show);
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
        Assert.True(Assert.Single(a.Received, m => m.Type == IpcVerbs.Quit).Evict); // sessions-10: an eviction, refused rather than prompting
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
    public async Task Quit_all_leaves_a_new_token_before_it_looks_and_each_spare_is_started_with_the_token_of_its_time()
    {
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        var spareOn = new LaunchSettings { WarmSpare = true };
        string LastSpareToken()
        {
            var args = spawner.Spawned.Last(a => a.Contains("--spare")).ToList();
            return args[args.IndexOf("--quit-token") + 1];
        }

        await Launcher(rt, spawner, spareOn).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "a" });
        Assert.Equal(RuntimeDirectory.NoQuitAllToken, LastSpareToken());

        // A spare still starting has no endpoint for the quit to find: the token it would see must be new before the quit looks for processes.
        var spare = Assert.Single(spawner.Hosts, h => h.Session is null);
        string? tokenWhenAsked = null;
        spare.Seen = _ => tokenWhenAsked ??= rt.Runtime.QuitAllToken();
        await Launcher(rt, spawner).RunAsync(new LaunchRequest(LaunchKind.Quit) { All = true });
        var token = rt.Runtime.QuitAllToken();
        Assert.NotEqual(RuntimeDirectory.NoQuitAllToken, token);
        Assert.Equal(token, tokenWhenAsked);
        Assert.True(spare.QuitRequested);

        await Launcher(rt, spawner, spareOn).RunAsync(new LaunchRequest(LaunchKind.Open) { Session = "b" });
        Assert.Equal(2, spawner.SpareStarts);
        Assert.Equal(token, LastSpareToken());
    }

    [Fact]
    public async Task Quit_without_a_name_after_the_last_active_session_quit_asks_the_shown_window_and_says_which()
    {
        // The last active session has quit (its marker stays behind); a hidden session was shown more recently than the window still open.
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "b" });
        rt.Store.WriteLastActive("b");
        using var window = new FakeHost(rt.Runtime, "a", ProcessStates.Shown) { LastShown = DateTimeOffset.UtcNow.AddMinutes(-5) };
        using var hidden = new FakeHost(rt.Runtime, "h", ProcessStates.Hidden) { LastShown = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var output = new StringWriter();

        var outcome = await Launcher(rt, spawner, output: output).RunAsync(new LaunchRequest(LaunchKind.Quit));

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(window.QuitRequested);
        Assert.False(hidden.QuitRequested);
        Assert.Contains($"cscode: quitting session 'a' (pid {window.Pid})", output.ToString());
    }

    [Fact]
    public async Task Quit_without_a_name_never_quits_another_session_in_place_of_a_last_active_one_that_does_not_answer()
    {
        // Review: the last active session's process missed the hello (paused in a debugger) and counted as quit, so --quit force-quit the
        // other window (and dropped it from restore-on-login) without a word.
        using var rt = new TestRuntime();
        using var spawner = new FakeSpawner(rt.Runtime);
        rt.Store.Save(new SessionDocument { Name = "b" });
        rt.Store.WriteLastActive("b");
        using var held = FileLock.TryAcquire(rt.Store.LockPathOf("b")); // its (paused) owner holds the lock
        using var paused = new FakeHost(rt.Runtime, "b", ProcessStates.Shown, pid: Environment.ProcessId) { Wedged = true };
        using var window = new FakeHost(rt.Runtime, "a", ProcessStates.Shown);
        var output = new StringWriter();
        var error = new StringWriter();

        await Launcher(rt, spawner, output: output, error: error).RunAsync(new LaunchRequest(LaunchKind.Quit));

        Assert.False(window.QuitRequested);
        Assert.Contains("Session 'b' is held by a CsCode process that does not respond", error.ToString());
        Assert.DoesNotContain("quitting session", output.ToString());
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
