using System.Diagnostics;
using System.Text;
using CsCode.Core.Terminal;
using CsCode.Terminal.Emulator;
using ProcessTable = CsCode.Terminal.Pty.Unix.ProcessTable;
using CsCode.Terminal.Sessions;
using CsCode.Terminal.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// The service and its sessions over real shells (sandboxed): creation off the caller's thread, the one-frame-at-a-time snapshot protocol,
/// sending text, exit codes in the buffer, kill, reuse of idle debug terminals, failures, and disposal leaving no process or thread behind.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class TerminalServiceTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();
    private readonly TestConfigurationService _config = new();
    private readonly TerminalService _service;

    public TerminalServiceTests()
    {
        _service = new TerminalService(_config, environment: () => _sandbox.Environment());
    }

    public void Dispose()
    {
        _service.Dispose();
        _sandbox.Dispose();
    }

    private TerminalCreateOptions Bash(string? name = null) => new()
    {
        Name = name,
        ShellPath = ShellSandbox.Bash,
        ShellArgs = ["--noprofile", "--norc"],
        Cwd = _sandbox.Home,
        Show = false,
    };

    private static async Task<string> WaitScreenAsync(TerminalSession s, string text, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        string screen = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            screen = await s.InvokeAsync(e => e.ScreenText());
            if (screen.Contains(text, StringComparison.Ordinal)) return screen;
            await Task.Delay(20);
        }
        Assert.Fail($"'{text}' did not appear on the screen:\n{screen}");
        return screen;
    }

    [Fact]
    public async Task Creating_a_terminal_runs_the_shell_and_reports_it()
    {
        var created = new List<ITerminal>();
        using var sub = _service.Created.Subscribe(created.Add);
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        Assert.NotNull(t.ProcessId);
        Assert.Same(t, Assert.Single(created));
        Assert.Same(t, _service.ActiveTerminal);
        Assert.Equal(1, t.Id);
        await t.SendTextAsync("echo hello-$((1+2))");
        await WaitScreenAsync(t, "hello-3");
    }

    [Fact]
    public async Task A_terminal_created_while_the_login_shell_environment_is_read_starts_its_shell_only_after_it()
    {
        // lsp-1: a Mac app just started from the Dock; the shell must inherit the merged environment
        var resolution = new TaskCompletionSource<CsCode.Core.Platform.LoginShellEnvironment.Resolution?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<ITerminal> create;
            using (CsCode.Core.Platform.LoginShellEnvironment.PendForTests(resolution.Task))
            {
                create = _service.CreateAsync(Bash()).AsTask();
                await Task.Delay(300, TestContext.Current.CancellationToken);
                Assert.False(create.IsCompleted);
                Assert.Empty(_service.Terminals);
                resolution.SetResult(null);
            }
            Assert.NotNull(((TerminalSession)await create).ProcessId);
        }
        finally { resolution.TrySetResult(null); }
    }

    [Fact]
    public async Task Create_does_not_block_the_calling_thread()
    {
        var sw = Stopwatch.StartNew();
        var pending = _service.CreateAsync(Bash());
        var synchronous = sw.Elapsed.TotalMilliseconds;
        await pending;
        Assert.True(synchronous < 5, $"CreateAsync held the caller for {synchronous:F2} ms before returning");
    }

    [Fact]
    public async Task The_environment_has_the_terminal_identity_and_the_settings_variables()
    {
        _config.Set(TerminalSettingKeys.EnvKey, new Dictionary<string, string?> { ["CSCODE_TEST_A"] = "from settings ${env:HOME}", ["XDG_DATA_HOME"] = null });
        var t = (TerminalSession)await _service.CreateAsync(Bash() with { Env = new Dictionary<string, string?> { ["CSCODE_TEST_B"] = "from caller" }, InitialSize = (240, 24) });
        await t.SendTextAsync("echo \"[$TERM|$COLORTERM|$TERM_PROGRAM|$CSCODE_TEST_A|$CSCODE_TEST_B|${XDG_DATA_HOME-unset}]\"");
        await WaitScreenAsync(t, $"[xterm-256color|truecolor|cscode|from settings {_sandbox.Home}|from caller|unset]");
    }

    /// <summary>
    /// T27: CsCode started by dotnet run/test carries DOTNET_ROOT_&lt;ARCH&gt; for its own apphost; shells and task programs get the language
    /// servers' rule (folded into DOTNET_ROOT when that is unset, dropped when it names the same directory, kept when it names another).
    /// </summary>
    [Fact]
    public void Dotnet_root_for_this_architecture_is_folded_into_DOTNET_ROOT_for_programs_in_the_terminal()
    {
        var arch = Core.Platform.InheritedDotnetRoot.ArchRootVariable;
        var sdk = _sandbox.Dir.Combine("sdk");
        var other = _sandbox.Dir.Combine("other");
        Dictionary<string, string> Resolve(Dictionary<string, string> inherited, IReadOnlyDictionary<string, string?>? own = null) =>
            new TerminalLaunchResolver(_config, null, () => inherited).Environment(new TerminalCreateOptions { Env = own }, null);

        var folded = Resolve(new() { ["PATH"] = "/bin", [arch] = sdk });
        Assert.Equal(sdk, folded["DOTNET_ROOT"]);
        Assert.False(folded.ContainsKey(arch));

        var same = Resolve(new() { ["PATH"] = "/bin", [arch] = sdk, ["DOTNET_ROOT"] = sdk + "/" });
        Assert.Equal(sdk + "/", same["DOTNET_ROOT"]);
        Assert.False(same.ContainsKey(arch));

        var split = Resolve(new() { ["PATH"] = "/bin", [arch] = sdk, ["DOTNET_ROOT"] = other });
        Assert.Equal((other, sdk), (split["DOTNET_ROOT"], split[arch])); // a deliberate split set-up stays

        // the caller's (a task's, a profile's) own value is applied on top, as written
        var own = Resolve(new() { ["PATH"] = "/bin", [arch] = sdk }, new Dictionary<string, string?> { [arch] = other });
        Assert.Equal((sdk, other), (own["DOTNET_ROOT"], own[arch]));
    }

    [Fact]
    public async Task A_shell_started_by_a_cscode_from_dotnet_run_has_no_DOTNET_ROOT_for_this_architecture()
    {
        var arch = Core.Platform.InheritedDotnetRoot.ArchRootVariable;
        var sdk = _sandbox.Dir.Combine("sdk");
        using var service = new TerminalService(_config, environment: () => _sandbox.Environment(new Dictionary<string, string> { [arch] = sdk }));
        // wide enough that the sandbox path (long under macOS's /var/folders temp directory) is not wrapped
        var t = (TerminalSession)await service.CreateAsync(Bash() with { InitialSize = (250, 24) });
        await t.SendTextAsync($"echo \"[${{{arch}-unset}}|$DOTNET_ROOT]\"");
        await WaitScreenAsync(t, $"[unset|{sdk}]");
    }

    [Fact]
    public async Task Exit_keeps_the_buffer_and_reports_the_code()
    {
        var exits = new List<TerminalExitedEventArgs>();
        using var sub = _service.Exited.Subscribe(exits.Add);
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        await t.SendTextAsync("echo before-exit; exit 7");
        var screen = await WaitScreenAsync(t, "[The terminal process exited with code 7.]");
        Assert.Contains("before-exit", screen);
        Assert.True(t.HasExited);
        Assert.Equal(7, t.ExitCode);
        Assert.Equal(7, Assert.Single(exits).ExitCode);
        Assert.Contains(t, _service.Terminals); // still listed until killed
        Assert.False(t.Input("x"u8));
    }

    [Fact]
    public async Task Frames_are_built_only_when_the_ui_asks_for_one()
    {
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        var frames = 0;
        using var sub = t.FrameReady.Subscribe(_ => Interlocked.Increment(ref frames));
        await WaitScreenAsync(t, "$");
        // nobody asked: a flood produces at most the one frame that was pending
        await t.SendTextAsync("for i in $(seq 1 3000); do echo line-$i; done; echo flood-done");
        await WaitScreenAsync(t, "flood-done");
        var before = Volatile.Read(ref frames);
        Assert.True(before <= 1, $"{before} frames without a request");
        var last = t.LatestSnapshot;
        t.RequestFrame();
        var sw = Stopwatch.StartNew();
        while (ReferenceEquals(t.LatestSnapshot, last) && sw.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.NotSame(last, t.LatestSnapshot);
        Assert.Contains("flood-done", string.Join('\n', t.LatestSnapshot!.Rows.Select(r => r.GetText())));
        Assert.Equal(before + 1, Volatile.Read(ref frames));
    }

    [Fact]
    public async Task Resize_reaches_the_shell()
    {
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        t.Resize(100, 30);
        Assert.Equal((100, 30), await t.InvokeAsync(e => (e.Columns, e.Rows)));
        await t.SendTextAsync("stty size");
        await WaitScreenAsync(t, "30 100");
    }

    [Fact]
    public async Task Kill_removes_the_terminal_ends_its_processes_and_activates_a_neighbour()
    {
        var a = (TerminalSession)await _service.CreateAsync(Bash());
        var b = (TerminalSession)await _service.CreateAsync(Bash());
        _service.SetActive(b);
        await b.SendTextAsync("sleep 1000 & sleep 1001");
        await Task.Delay(300);
        var members = ProcessTable.SessionMembers(b.ProcessId!.Value);
        Assert.True(members.Count >= 3);
        var removed = new List<ITerminal>();
        using var sub = _service.Removed.Subscribe(removed.Add);
        await _service.KillAsync(b);
        Assert.Same(b, Assert.Single(removed));
        Assert.Same(a, _service.ActiveTerminal);
        Assert.DoesNotContain(b, _service.Terminals);
        foreach (var pid in members) Assert.True(await ProcessChecks.WaitGoneAsync(pid, 5000), $"process {pid} survived the kill");
    }

    [Fact]
    public async Task A_transient_terminal_is_reused_for_the_next_run_when_idle()
    {
        var first = (TerminalSession)await _service.CreateAsync(new TerminalCreateOptions
        {
            Name = "Launch App", IsTransient = true, ReuseWhenIdle = true, Show = false,
            ShellPath = ShellSandbox.Sh, ShellArgs = ["-c", "echo run-one"], Cwd = _sandbox.Home,
        });
        await WaitScreenAsync(first, "exited with code 0");
        var second = (TerminalSession)await _service.CreateAsync(new TerminalCreateOptions
        {
            Name = "Launch App", IsTransient = true, ReuseWhenIdle = true, Show = false,
            ShellPath = ShellSandbox.Sh, ShellArgs = ["-c", "echo run-two; exit 3"], Cwd = _sandbox.Home,
        });
        Assert.Same(first, second);
        var screen = await WaitScreenAsync(second, "exited with code 3");
        Assert.Contains("run-one", screen);
        Assert.Contains("run-two", screen);
        Assert.Single(_service.Terminals);
        // a running one is not reused
        var running = (TerminalSession)await _service.CreateAsync(Bash("Launch App") with { IsTransient = true, ReuseWhenIdle = true });
        var third = await _service.CreateAsync(Bash("Launch App") with { IsTransient = true, ReuseWhenIdle = true });
        Assert.NotSame(running, third);
    }

    [Fact]
    public async Task A_program_that_cannot_start_shows_why_and_counts_as_exited()
    {
        var t = (TerminalSession)await _service.CreateAsync(new TerminalCreateOptions { ShellPath = "/nonexistent/shell", Show = false });
        await WaitScreenAsync(t, "/nonexistent/shell");
        Assert.True(t.HasExited);
        Assert.Equal(-1, t.ExitCode);
    }

    [Fact]
    public async Task The_title_follows_the_program_then_the_user_rename()
    {
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        Assert.Equal("bash", t.Title);
        await t.SendTextAsync("printf '\\033]0;my-title\\007'");
        var sw = Stopwatch.StartNew();
        while (t.Title != "my-title" && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.Equal("my-title", t.Title);
        t.UserTitle = "renamed";
        Assert.Equal("renamed", t.Title);
    }

    [Fact]
    public async Task Send_text_to_the_service_creates_a_terminal_when_there_is_none()
    {
        _config.Set(TerminalSettingKeys.ProfilesKey, new Dictionary<string, object>
        {
            ["sandboxed"] = new { path = ShellSandbox.Bash, args = new[] { "--noprofile", "--norc" } },
        });
        _config.Set(TerminalSettingKeys.DefaultProfileKey, "sandboxed");
        await _service.SendTextAsync("echo created-for-text");
        var t = Assert.IsType<TerminalSession>(Assert.Single(_service.Terminals));
        await WaitScreenAsync(t, "created-for-text");
    }

    [Fact]
    public async Task A_flood_nobody_draws_stays_bounded_and_ctrl_c_stops_it_quickly()
    {
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        await WaitScreenAsync(t, "$");
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        await t.SendTextAsync("yes flooding-the-terminal");
        await Task.Delay(2000);
        var during = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        Assert.True(t.Input([0x03], urgent: true));
        await t.SendTextAsync("echo stopped-$((40+2))");
        await WaitScreenAsync(t, "stopped-42", 10_000);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Ctrl+C took {sw.ElapsedMilliseconds} ms to get the prompt back");
        Assert.True(during - before < 64L * 1024 * 1024, $"memory grew by {(during - before) / 1024 / 1024} MB during the flood");
    }

    [Fact]
    public async Task Disposing_the_service_kills_every_terminal_and_ends_its_threads()
    {
        var service = new TerminalService(_config, environment: () => _sandbox.Environment());
        var sessions = new List<TerminalSession>();
        var pids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var t = (TerminalSession)await service.CreateAsync(Bash());
            await t.SendTextAsync("sleep 1000 & sleep 1001");
            sessions.Add(t);
        }
        await Task.Delay(300);
        foreach (var t in sessions) pids.AddRange(ProcessTable.SessionMembers(t.ProcessId!.Value));
        service.Dispose();
        foreach (var pid in pids) Assert.True(await ProcessChecks.WaitGoneAsync(pid, 5000), $"process {pid} survived the service");
        foreach (var t in sessions) Assert.True(t.JoinWorker(TimeSpan.FromSeconds(5)), "a terminal worker thread is still running");
    }

    [Fact]
    public async Task Fifty_terminals_created_and_killed_leave_no_process_thread_descriptor_or_object()
    {
        // warm up the runtime's lazy machinery and the measurements themselves
        await _service.KillAsync(await _service.CreateAsync(Bash()));
        _ = ProcessChecks.ThreadCount();
        _ = ProcessChecks.DescriptorTargets();
        await Task.Delay(500);
        var fds = ProcessChecks.OpenDescriptors();
        var threads = ProcessChecks.ThreadCount();
        var pids = new List<int>();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var objects = new List<object>();
            for (var i = 0; i < 50; i++)
            {
                var t = (TerminalSession)await _service.CreateAsync(Bash());
                await t.SendTextAsync("echo hi");
                pids.Add(t.ProcessId!.Value);
                objects.Add(t);
                objects.Add(t.Emulator);
                await _service.KillAsync(t);
            }
            return objects.ToArray();
        });
        foreach (var pid in pids) Assert.True(await ProcessChecks.WaitGoneAsync(pid, 5000), $"process {pid} left behind");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && (ProcessChecks.OpenDescriptors() > fds + 2 || ProcessChecks.ThreadCount() > threads + 3)) await Task.Delay(50);
        Assert.True(ProcessChecks.OpenDescriptors() <= fds + 2, $"descriptors {fds} -> {ProcessChecks.OpenDescriptors()}");
        Assert.True(ProcessChecks.ThreadCount() <= threads + 3, $"threads {threads} -> {ProcessChecks.ThreadCount()}");
        Assert.Empty(_service.Terminals);
    }
}
