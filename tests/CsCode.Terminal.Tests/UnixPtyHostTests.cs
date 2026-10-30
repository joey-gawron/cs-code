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
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
