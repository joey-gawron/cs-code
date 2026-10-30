using System.Diagnostics;
using System.Text;
using CsCode.Terminal.Pty;
using CsCode.Terminal.Pty.Unix;
using CsCode.Terminal.Tests.Support;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// The Unix PTY host against real programs (bash --noprofile --norc, sh, sleep, cat) in a sandboxed environment: spawn, echo, resize seen by
/// <c>stty size</c>, Ctrl+C as SIGINT, exit codes, killing the whole session, a program that stops reading, descriptors and zombies.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class UnixPtyHostTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static void RequireLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The Unix PTY host is tested on Linux.");
    }

    [Fact]
    public async Task Spawns_a_program_reads_its_output_and_reports_the_exit_code()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "echo hello-from-pty; exit 3"]), output);
        Assert.True(host.ProcessId > 0);
        Assert.Equal(3, await output.WaitExitAsync());
        Assert.Contains("hello-from-pty\r\n", output.Text); // the line discipline's ONLCR: a real terminal
        Assert.True(host.HasExited);
        Assert.Equal(3, host.ExitCode);
        Assert.StartsWith("cscode-pty-read-", output.OutputThreadName);
    }

    [Fact]
    public async Task The_child_runs_on_the_pty_as_a_session_leader_with_it_as_controlling_terminal()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        // ps is not needed: /proc/self/stat of the shell has its session id and the tty number; `tty` prints the slave's path.
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "tty; cut -d' ' -f6,7 /proc/$$/stat; echo pid=$$"]), output);
        await output.WaitExitAsync();
        var text = output.Text;
        Assert.Contains(host.SlavePath!, text);
        var pid = host.ProcessId;
        Assert.Contains($"pid={pid}", text);
        // field 6 = session id (== the shell's pid: it leads the session), field 7 = tty_nr (non-zero: it has a controlling terminal)
        var line = text.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith(pid + " ", StringComparison.Ordinal));
        Assert.NotEqual("0", line.Split(' ')[1]);
    }

    [Fact]
    public async Task The_child_inherits_no_ignored_or_blocked_signals_and_no_descriptors()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        // cat is exec'd directly: what it sees is exactly what posix_spawn set up (the runtime itself ignores SIGPIPE).
        host.Start(_sandbox.Command("/bin/cat", ["/proc/self/status"]), output);
        await output.WaitExitAsync();
        Assert.Contains("SigIgn:\t0000000000000000", output.Text);
        Assert.Contains("SigBlk:\t0000000000000000", output.Text);

        using var host2 = new UnixPtyHost();
        var fds = new RecordingPtyListener();
        host2.Start(_sandbox.Command("/bin/ls", ["/proc/self/fd"]), fds);
        await fds.WaitExitAsync();
        var listed = fds.Text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Assert.Equal(["0", "1", "2", "3"], listed.Order()); // 3 is ls's own handle on the directory
    }

    [Fact]
    public async Task An_interactive_bash_echoes_input_and_runs_commands()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.BashInfo(), output);
        Assert.True(host.Write("echo $((6*7))-ok\r"u8));
        Assert.True(await output.WaitForAsync("42-ok\r\n"), output.Text);
        Assert.Contains("echo $((6*7))-ok", output.Text); // the tty echoed what was typed
        host.Write("exit 5\r"u8);
        Assert.Equal(5, await output.WaitExitAsync());
    }

    [Fact]
    public async Task Resize_reaches_the_program_as_the_window_size()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.BashInfo(columns: 80, rows: 24), output);
        host.Write("stty size\r"u8);
        Assert.True(await output.WaitForAsync("24 80\r\n"), output.Text);
        host.Resize(132, 43);
        host.Write("stty size; echo $COLUMNS\r"u8);
        Assert.True(await output.WaitForAsync("43 132\r\n"), output.Text);
        Assert.True(await output.WaitForAsync("132\r\n"), output.Text); // bash got SIGWINCH and updated COLUMNS
        host.Kill();
    }

    [Fact]
    public async Task Ctrl_C_is_delivered_as_SIGINT_to_the_foreground_process_quickly()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Bash, ["--noprofile", "--norc", "-c", "trap 'echo GOT-INT; exit 7' INT; echo ready; while :; do sleep 0.02; done"]), output);
        Assert.True(await output.WaitForAsync("ready"));
        var sw = Stopwatch.StartNew();
        Assert.True(host.Write([0x03], urgent: true));
        Assert.Equal(7, await output.WaitExitAsync());
        Assert.Contains("GOT-INT", output.Text);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"SIGINT took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Ctrl_C_interrupts_a_foreground_job_of_an_interactive_shell()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.BashInfo(), output);
        host.Write("sleep 100; echo after-sleep:$?\r"u8);
        await Task.Delay(300);
        var sw = Stopwatch.StartNew();
        host.Write([0x03]);
        Assert.True(await output.WaitForAsync("$ "), output.Text);
        host.Write("echo still-alive\r"u8);
        Assert.True(await output.WaitForAsync("still-alive\r\n"), output.Text);
        Assert.True(sw.ElapsedMilliseconds < 3000);
        Assert.DoesNotContain("after-sleep:0", output.Text);
        Assert.False(host.HasExited);
        host.Kill();
    }

    [Theory]
    [InlineData("exit 0", 0)]
    [InlineData("exit 42", 42)]
    [InlineData("kill -TERM $$", 128 + 15)]
    [InlineData("kill -KILL $$", 128 + 9)]
    public async Task Exit_codes_are_reported_as_shells_report_them(string script, int expected)
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", script]), output);
        Assert.Equal(expected, await output.WaitExitAsync());
        Assert.Equal(expected, host.ExitCode);
        Assert.True(await ProcessChecks.WaitGoneAsync(host.ProcessId), "the child was not reaped (zombie)");
    }

    [Fact]
    public async Task Kill_ends_the_shell_its_foreground_job_and_its_background_jobs()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.BashInfo(), output);
        host.Write("sleep 1000 & echo BG=$!\r"u8);
        Assert.True(await output.WaitForAsync("BG="));
        await output.WaitForAsync("\r\n$ ");
        host.Write("sleep 1001\r"u8);
        await Task.Delay(300);
        var bg = int.Parse(System.Text.RegularExpressions.Regex.Match(output.Text, @"BG=(\d+)").Groups[1].Value);
        var members = ProcessTable.SessionMembers(host.ProcessId);
        Assert.Contains(bg, members);
        Assert.True(members.Count >= 3, $"session members: {string.Join(",", members)}");

        host.Kill(TimeSpan.FromSeconds(1));
        await output.WaitExitAsync(5_000);
        foreach (var pid in members) Assert.True(await ProcessChecks.WaitGoneAsync(pid, 5_000), $"process {pid} survived the kill");
        Assert.Empty(ProcessTable.SessionMembers(host.ProcessId));
    }

    [Fact]
    public async Task Kill_escalates_to_SIGKILL_when_the_shell_ignores_the_hang_up()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "trap '' HUP; echo armed; sleep 1000 & while :; do sleep 1; done"]), output);
        Assert.True(await output.WaitForAsync("armed"));
        await Task.Delay(200);
        var members = ProcessTable.SessionMembers(host.ProcessId);
        var sw = Stopwatch.StartNew();
        host.Kill(TimeSpan.FromMilliseconds(400));
        var code = await output.WaitExitAsync(5_000);
        Assert.Equal(128 + 9, code);
        Assert.True(sw.ElapsedMilliseconds >= 300, "SIGKILL came before the grace period");
        foreach (var pid in members) Assert.True(await ProcessChecks.WaitGoneAsync(pid), $"process {pid} survived");
    }

    [Fact]
    public async Task A_program_that_stops_reading_never_blocks_a_writer()
    {
        RequireLinux();
        using var host = new UnixPtyHost(maxPendingWriteBytes: 256 * 1024);
        var output = new RecordingPtyListener();
        // sleep never reads its input: the PTY's input buffer fills, then the bounded queue does.
        host.Start(_sandbox.Command("/bin/sh", ["-c", "stty -icanon -echo; exec sleep 30"]), output);
        await Task.Delay(200);
        var chunk = Encoding.ASCII.GetBytes(new string('x', 4096));
        var refused = 0;
        double worst = 0;
        for (var i = 0; i < 200; i++)
        {
            var sw = Stopwatch.StartNew();
            if (!host.Write(chunk)) refused++;
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
        }
        Assert.True(worst < 20, $"a write blocked the caller for {worst:F1} ms");
        Assert.True(refused > 0, "the queue never filled");
        Assert.True(host.PendingWriteBytes <= host.MaxPendingWriteBytes);
        // An urgent write is still accepted while the queue is full, and killing works.
        Assert.True(host.Write([0x03], urgent: true));
        host.Kill(TimeSpan.FromMilliseconds(300));
        await output.WaitExitAsync(5_000);
        Assert.True(await ProcessChecks.WaitGoneAsync(host.ProcessId));
    }

    [Fact]
    public async Task Dispose_kills_a_running_shell_and_reaps_it()
    {
        RequireLinux();
        var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.BashInfo(), output);
        host.Write("sleep 1000\r"u8);
        await Task.Delay(200);
        var members = ProcessTable.SessionMembers(host.ProcessId);
        host.Dispose();
        foreach (var pid in members) Assert.True(await ProcessChecks.WaitGoneAsync(pid, 5_000), $"process {pid} survived dispose");
        Assert.False(host.Write("x"u8));
    }

    [Fact]
    public void A_missing_program_fails_to_start_with_a_message()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var ex = Assert.Throws<PtySpawnException>(() => host.Start(_sandbox.Command("/nonexistent/program", []), new RecordingPtyListener()));
        Assert.Contains("/nonexistent/program", ex.Message);
    }

    [Fact]
    public async Task Environment_and_working_directory_are_the_given_ones()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        var info = _sandbox.Command(ShellSandbox.Sh, ["-c", "pwd; echo FOO=$FOO; echo HOME=$HOME"]) with
        {
            Environment = _sandbox.Environment(new Dictionary<string, string> { ["FOO"] = "bar baz" }),
            WorkingDirectory = _sandbox.Dir.Path,
        };
        host.Start(info, output);
        await output.WaitExitAsync();
        Assert.Contains(_sandbox.Dir.Path + "\r\n", output.Text);
        Assert.Contains("FOO=bar baz", output.Text);
        Assert.Contains("HOME=" + _sandbox.Home, output.Text);
    }

    [Fact]
    public async Task Output_written_just_before_exit_is_not_lost()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "i=0; while [ $i -lt 2000 ]; do echo line-$i; i=$((i+1)); done; echo LAST"]), output);
        await output.WaitExitAsync();
        Assert.Contains("line-1999\r\nLAST\r\n", output.Text);
    }

    [Fact]
    public async Task Twenty_spawned_and_disposed_hosts_leave_no_descriptor_thread_or_process_behind()
    {
        RequireLinux();
        // warm up (first-use allocations of the runtime, and the assemblies the measurements themselves load)
        await RunOnce();
        _ = ProcessChecks.ThreadCount();
        _ = ProcessChecks.DescriptorTargets();
        await Task.Delay(200);
        var fds = ProcessChecks.OpenDescriptors();
        var before = ProcessChecks.DescriptorTargets();
        var threads = ProcessChecks.ThreadCount();
        var pids = new List<int>();
        for (var i = 0; i < 20; i++) pids.Add(await RunOnce());
        foreach (var pid in pids) Assert.True(await ProcessChecks.WaitGoneAsync(pid), $"process {pid} left behind");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && (ProcessChecks.OpenDescriptors() > fds + 2 || ProcessChecks.ThreadCount() > threads + 3)) await Task.Delay(50);
        var added = ProcessChecks.DescriptorTargets().Except(before);
        Assert.True(ProcessChecks.OpenDescriptors() <= fds + 2, $"descriptors {fds} -> {ProcessChecks.OpenDescriptors()}: {string.Join(", ", added)}");
        Assert.True(ProcessChecks.ThreadCount() <= threads + 3, $"threads {threads} -> {ProcessChecks.ThreadCount()}");

        async Task<int> RunOnce()
        {
            var host = new UnixPtyHost();
            var output = new RecordingPtyListener();
            host.Start(_sandbox.BashInfo(), output);
            host.Write("echo hi\r"u8);
            await output.WaitForAsync("hi\r\n");
            var pid = host.ProcessId;
            host.Dispose();
            await output.WaitExitAsync(5_000);
            return pid;
        }
    }

    // ------------------------------------------------------------------ terminal-2: never kill a reaped leader's number

    [Fact]
    public async Task Disposing_an_exited_terminal_never_kills_a_process_that_took_its_number_later()
    {
        RequireLinux();
        using var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "exit 0"]), output);
        await output.WaitExitAsync();
        Assert.True(await ProcessChecks.WaitGoneAsync(host.ProcessId), "not reaped");
        await Task.Delay(50); // a later clock tick than the reap (start times count in 10 ms ticks)

        // An unrelated session leader started after the reap stands in for a process that was given the reaped pid: the kernel reuses a
        // number once nothing holds it. (The real reuse was reproduced in a private pid namespace: docs/TERMINAL.md.)
        using var stranger = new UnixPtyHost();
        var strangerOutput = new RecordingPtyListener();
        stranger.Start(_sandbox.Command("/bin/sh", ["-c", "echo up; exec sleep 30"]), strangerOutput);
        Assert.True(await strangerOutput.WaitForAsync("up"));
        var sid = stranger.ProcessId;
        Assert.Contains(sid, ProcessTable.SessionMembers(sid));

        host.KillSessionForTest(sid); // what the kill timer (or Dispose of the exited terminal) would do if that number were its pid
        await Task.Delay(300);
        Assert.True(ProcessChecks.IsAlive(sid), "a process that took the reaped number later was killed");
        stranger.Kill(TimeSpan.FromMilliseconds(200));
        await strangerOutput.WaitExitAsync(5_000);
    }

    [Fact]
    public async Task Disposing_an_exited_terminal_still_kills_the_jobs_its_program_left_behind_off_the_callers_thread()
    {
        RequireLinux();
        var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        // a background job that outlives the program: it keeps the session (and so the number) in use
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "trap '' HUP; sleep 1000 & echo BG=$!; exit 0"]), output);
        Assert.True(await output.WaitForAsync("BG="));
        await output.WaitExitAsync();
        var bg = int.Parse(System.Text.RegularExpressions.Regex.Match(output.Text, @"BG=(\d+)").Groups[1].Value);
        Assert.True(ProcessChecks.IsAlive(bg));
        Assert.Contains(bg, ProcessTable.SessionMembers(host.ProcessId));

        var sw = Stopwatch.StartNew();
        host.Dispose(); // (the /proc scan runs on the pool)
        Assert.True(sw.ElapsedMilliseconds < 50, $"Dispose of an exited terminal took {sw.ElapsedMilliseconds} ms on the caller's thread");
        Assert.True(await ProcessChecks.WaitGoneAsync(bg, 5_000), "the left-behind job survived");
    }

    /// <summary>
    /// The real reuse, where it can be forced: in a private pid namespace (<c>CSCODE_PIDNS_TEST=1 unshare -Urpfm --mount-proc dotnet test ...
    /// --filter FullyQualifiedName~private_pid_namespace</c>) the next pid can be chosen, so a new session leader gets the reaped terminal
    /// program's pid. Skipped elsewhere (opt-in: it sets the namespace's next pid).
    /// </summary>
    [Fact]
    public async Task In_a_private_pid_namespace_a_reused_pid_survives_disposing_the_exited_terminal()
    {
        RequireLinux();
        const string lastPid = "/proc/sys/kernel/ns_last_pid";
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CSCODE_PIDNS_TEST") == "1" && CanWrite(lastPid),
            "Needs a private pid namespace: CSCODE_PIDNS_TEST=1 unshare -Urpfm --mount-proc dotnet test ...");
        var host = new UnixPtyHost();
        var output = new RecordingPtyListener();
        host.Start(_sandbox.Command(ShellSandbox.Sh, ["-c", "exit 0"]), output);
        await output.WaitExitAsync();
        var pid = host.ProcessId;
        Assert.True(await ProcessChecks.WaitGoneAsync(pid), "not reaped");

        File.WriteAllText(lastPid, (pid - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var stranger = new UnixPtyHost();
        var strangerOutput = new RecordingPtyListener();
        stranger.Start(_sandbox.Command("/bin/sh", ["-c", "echo up; exec sleep 30"]), strangerOutput);
        Assert.SkipUnless(stranger.ProcessId == pid, $"another process took pid {pid} first");
        Assert.True(await strangerOutput.WaitForAsync("up"));

        host.Dispose();
        await Task.Delay(500);
        Assert.True(ProcessChecks.IsAlive(pid), "disposing the exited terminal killed the session leader that reused its pid");
        stranger.Kill(TimeSpan.FromMilliseconds(200));
        await strangerOutput.WaitExitAsync(5_000);

        static bool CanWrite(string path)
        {
            // writing needs CAP_SYS_ADMIN over the namespace (the permission bits allow anyone to open it): write the value it has
            try { File.WriteAllText(path, File.ReadAllText(path).Trim()); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    [Fact]
    public void Only_a_session_that_kept_the_number_in_use_since_the_reap_is_killed()
    {
        const ulong reap = 1_000;
        // a member that started before (or in the tick of) the reap: the number was never free, every member is the terminal's
        Assert.Equal([10, 11], UnixPtyHost.OrphansToKill([(10, 990), (11, 1_500)], reap));
        Assert.Equal([12], UnixPtyHost.OrphansToKill([(12, 1_000)], reap));
        // only processes started after the reap: the number may have been reused (a new session leader and its children)
        Assert.Empty(UnixPtyHost.OrphansToKill([(13, 1_001), (14, 2_000)], reap));
        Assert.Empty(UnixPtyHost.OrphansToKill([], reap));
        // the moment of the reap is unknown: nothing is killed by number
        Assert.Empty(UnixPtyHost.OrphansToKill([(15, 1)], 0));
    }

    [Fact]
    public void Uptime_is_read_in_clock_ticks()
    {
        Assert.Equal(1_234_567UL, ProcessTable.ParseUptimeTicks("12345.67 54321.00\n", 100));
        Assert.Equal(1_234_500UL, ProcessTable.ParseUptimeTicks("12345 1", 100));
        Assert.Equal(12_345_670UL, ProcessTable.ParseUptimeTicks("12345.67 1", 1000));
        Assert.Equal(0UL, ProcessTable.ParseUptimeTicks("garbage", 100));
        if (!OperatingSystem.IsLinux()) return;
        // a process started just now started at or before now, on the same clock
        var now = ProcessTable.BootTicksNow();
        Assert.True(now > 0);
        Assert.True(ProcessTable.Stat(Environment.ProcessId)!.Value.StartTicks <= now);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
