using System.Diagnostics;
using System.Text;
using CsCode.App.Tests.Infrastructure;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>The end-pass session fixes with REAL CsCode processes (each test in its own CSCODE_HOME; nothing is left running).</summary>
public class SessionFixProcessTests
{
    private const string NoSpare = "\"cscode.background.warmSpare\": false";
    private const int SigTerm = 15;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    private static ResidentProcess Owner(SessionProcessHarness h, string session) =>
        h.Discover().FirstOrDefault(p => p.Session == session && p.State != ProcessStates.Quitting) ?? throw new InvalidOperationException($"no process owns '{session}'\n{h.Trace()}");

    [Fact]
    public async Task Sigterm_saves_the_unsaved_changes_before_the_process_ends()
    {
        // sessions-5: logout or `systemctl --user stop` (SIGTERM) ended a session process at once, losing what changed since the last autosave.
        if (!OperatingSystem.IsLinux()) Assert.Skip("POSIX signals");
        using var h = new SessionProcessHarness(NoSpare);
        var file = h.WriteFile("a.txt", "original\n");
        var open = await h.RunAsync("--session", "T", file);
        Assert.True(open.ExitCode == 0, open.Error);
        var t = Owner(h, "T");
        await Task.Delay(2500); // the activation's own autosave is done: what follows is only in memory

        h.Must(t.Pid, new IpcMessage { Type = IpcVerbs.TestEdit, Uri = new Uri(file).ToString(), Line = 0, Column = 0, Text = "UNSAVED " });
        Assert.Equal(0, Kill(t.Pid, SigTerm)); // a process this test started (through the launcher)

        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !ProcessProbe.IsRunning(t.Pid), 30_000), "the session process did not end after SIGTERM\n" + h.Trace());
        var backup = Assert.Single(h.Store.Backups.List("T"));
        Assert.Equal("UNSAVED original\n", h.Store.Backups.ReadText("T", backup.Id));
        Assert.True(h.Store.Load("T").Session!.WasResident, "a session ended by SIGTERM (logout) is restored at the next login");
        Assert.Equal("original\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task An_unusable_runtime_directory_never_opens_a_window_for_list_quit_or_background()
    {
        // sessions-8: these fell back to running the app in-process (an editor window) instead of failing.
        using var h = new SessionProcessHarness(NoSpare);
        File.WriteAllText(h.Runtime.Path, "not a directory");
        try
        {
            foreach (var args in new[] { new[] { "--list-sessions" }, ["--quit", "--all"], ["--background"] })
            {
                var run = await h.RunAsync(TimeSpan.FromSeconds(60), args);
                Assert.True(run.ExitCode == 1, $"{string.Join(' ', args)}: exit {run.ExitCode}\n{run.Error}");
                Assert.Contains("background mode unavailable", run.Error);
            }
            Assert.Empty(h.ProcessesOfThisHome());
        }
        finally { File.Delete(h.Runtime.Path); }
    }

    [Fact]
    public async Task A_relative_cscode_home_means_the_same_directory_to_the_launcher_and_its_session()
    {
        // sessions-9: the session process runs in the user's home directory, so it resolved a relative CSCODE_HOME elsewhere: the launcher waited
        // 30 s for a process it could not see (and the process crashed on the relative path).
        using var h = new SessionProcessHarness(NoSpare);
        var fakeHome = Directory.CreateDirectory(Path.Combine(h.Home, "user-home")).FullName;
        var env = new Dictionary<string, string?> { ["CSCODE_HOME"] = Path.GetFileName(h.Home), ["HOME"] = fakeHome };

        var run = await h.RunWithAsync(Path.GetDirectoryName(h.Home), env, TimeSpan.FromSeconds(90), "--session", "S");

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.True(run.Elapsed < TimeSpan.FromSeconds(25), $"the launcher took {run.Elapsed.TotalSeconds:0.0} s");
        Owner(h, "S");
        Assert.False(Directory.Exists(Path.Combine(fakeHome, Path.GetFileName(h.Home))), "the session process used a second directory");
    }

    [Fact]
    public async Task Reopening_a_session_while_its_process_shuts_down_a_slow_language_server_starts_it_afresh_without_a_copy()
    {
        // sessions-3: the quitting process kept the session's lock through its whole shutdown (a language server that ignores shutdown: seconds),
        // so the launch took it for a wedged owner and opened 'window-1', a copy holding the same unsaved changes.
        if (!OperatingSystem.IsLinux() || FakeLanguageServer.Python is not { } python) { Assert.Skip("needs python3"); return; }
        using var h = new SessionProcessHarness(NoSpare);
        var script = Path.Combine(h.Home, "fake-ls.py");
        File.WriteAllText(script, (string)typeof(FakeLanguageServer).GetField("Script", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!);
        File.WriteAllText(Path.Combine(h.Home, "config", "settings.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cscode.languageServers"] = new Dictionary<string, object>
            {
                [FakeLanguageServer.Id] = new { command = python, args = new[] { script, "stubborn" }, selector = new[] { new { pattern = "**/*" + FakeLanguageServer.Extension } } },
            },
            ["cscode.lsp.shutdownTimeoutMs"] = 4000,
            ["workbench.startupEditor"] = "none",
            ["cscode.background.warmSpare"] = false,
        }));
        var file = h.WriteFile("a" + FakeLanguageServer.Extension, "a\n");
        var first = await h.RunAsync("--session", "work", file);
        Assert.True(first.ExitCode == 0, first.Error);
        var old = Owner(h, "work");
        h.Must(old.Pid, new IpcMessage { Type = IpcVerbs.TestEdit, Uri = new Uri(file).ToString(), Line = 0, Column = 0, Text = "UNSAVED " });
        await Task.Delay(4000); // the server is up, the edit backed up

        var quit = h.Request(old.Pid, new IpcMessage { Type = IpcVerbs.Quit, Force = false });
        Assert.True(quit.Ok == true, quit.Error);
        var again = await h.RunAsync("--session", "work");

        Assert.True(again.ExitCode == 0, again.Error);
        Assert.DoesNotContain("does not respond", again.Error);
        var owner = Owner(h, "work");
        Assert.NotEqual(old.Pid, owner.Pid);
        Assert.DoesNotContain(h.Store.List(), s => s.Name.StartsWith(SessionNames.UnnamedPrefix, StringComparison.Ordinal));
        var tab = Assert.Single(h.Inspect(owner.Pid, withText: true).Tabs);
        Assert.Equal("UNSAVED a\n", tab.Text);
    }

    [Fact]
    public async Task Launches_that_resolve_different_runtime_directories_never_both_own_a_session()
    {
        // sessions-7: the session lock lived in the runtime directory, so a launch without the desktop's XDG_RUNTIME_DIR started a second writer
        // of the same session, and each one's autosave deleted the other's backups.
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux runtime directories"); return; }
        var root = Path.Combine("/tmp", "cs7-" + Guid.NewGuid().ToString("N")[..8]);
        // Long enough that the runtime directory moves under XDG_RUNTIME_DIR (as in a real install), short enough for the test's own paths.
        var home = Path.Combine(root, new string('h', 100));
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var xdgA = Directory.CreateDirectory(Path.Combine(root, "xa"), Private).FullName;
        var xdgB = Directory.CreateDirectory(Path.Combine(root, "xb"), Private).FullName;
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "settings.json"), """{ "cscode.languageServers": { "typescript": {"enabled": false}, "csharp": {"enabled": false}, "python": {"enabled": false} }, "workbench.startupEditor": "none", "cscode.background.warmSpare": false }""");
        var a = Path.Combine(root, "a.txt");
        File.WriteAllText(a, "a\n");
        var runA = RuntimeDirectory.Resolve(n => n switch { "CSCODE_HOME" => home, "XDG_RUNTIME_DIR" => xdgA, _ => null });
        var runB = RuntimeDirectory.Resolve(n => n switch { "CSCODE_HOME" => home, "XDG_RUNTIME_DIR" => xdgB, _ => null });
        Assert.NotEqual(runA.Path, runB.Path);
        Dictionary<string, string?> Env(string xdg) => new() { ["CSCODE_HOME"] = home, ["XDG_RUNTIME_DIR"] = xdg, ["XDG_DATA_HOME"] = Path.Combine(root, "xdg-data") };
        try
        {
            var first = await Launch(root, Env(xdgA), "--session", "work", a);
            Assert.True(first.Code == 0, first.Error);
            var second = await Launch(root, Env(xdgB), "--session", "work");
            Assert.True(second.Code == 0, second.Error);

            var owners = ResidentDiscovery.Discover(runA, TimeSpan.FromSeconds(2)).Concat(ResidentDiscovery.Discover(runB, TimeSpan.FromSeconds(2)))
                .Where(p => p.Session == "work").ToArray();
            Assert.Single(owners);
            Assert.Contains("cannot reach", second.Error); // it opened a copy instead (window-1), and said why
            Assert.Contains(ResidentDiscovery.Discover(runB, TimeSpan.FromSeconds(2)), p => p.Session == "window-1");
        }
        finally
        {
            foreach (var xdg in new[] { xdgA, xdgB }) await Launch(root, Env(xdg), "--quit", "--all");
            foreach (var pid in PidsWith("CSCODE_HOME=" + home))
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(); } // only processes of this test's CSCODE_HOME
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            await SessionProcessHarness.WaitUntilAsync(() => PidsWith("CSCODE_HOME=" + home).Count == 0, 10_000);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<(int Code, string Error)> Launch(string cwd, IReadOnlyDictionary<string, string?> env, params string[] args)
    {
        var psi = new ProcessStartInfo(SessionProcessHarness.ExecutableUnderTest)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = cwd,
        };
        psi.ArgumentList.Add("--headless-test");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "SSH_AUTH_SOCK", "CSCODE_SESSION_TRACE" }) psi.Environment.Remove(name);
        foreach (var (key, value) in env)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true); // the launcher this test started
            throw new TimeoutException($"cscode {string.Join(' ', args)} did not exit");
        }
        await output;
        return (p.ExitCode, await error);
    }

    private static List<int> PidsWith(string needle)
    {
        if (OperatingSystem.IsMacOS()) return TestDoubles.ProcessTable.WithEnvironment(needle[..needle.IndexOf('=')], needle[(needle.IndexOf('=') + 1)..]);
        var result = new List<int>();
        var bytes = Encoding.UTF8.GetBytes(needle + "\0");
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                if (File.ReadAllBytes(Path.Combine(dir, "environ")).AsSpan().IndexOf(bytes) >= 0 && ProcessProbe.IsRunning(pid)) result.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
