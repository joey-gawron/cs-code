using System.Diagnostics;
using CsCode.App.Background;
using CsCode.App.Startup;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.Background;

public class LauncherFallbackTests
{
    [Fact]
    public void Only_opening_a_window_falls_back_to_running_in_this_process()
    {
        // sessions-8: with the runtime directory unusable, --list-sessions, --quit and the login autostart's --background opened an editor window.
        Assert.Equal(LauncherEntry.RunInProcess, LauncherEntry.ExitCodeFor(new LaunchRequest(LaunchKind.Open), 2));
        foreach (var kind in new[] { LaunchKind.ListSessions, LaunchKind.Quit, LaunchKind.Background })
            Assert.Equal(1, LauncherEntry.ExitCodeFor(new LaunchRequest(kind), 2));
        Assert.Equal(0, LauncherEntry.ExitCodeFor(new LaunchRequest(LaunchKind.Quit), 0));
        Assert.Equal(1, LauncherEntry.ExitCodeFor(new LaunchRequest(LaunchKind.Open), 1));
    }

    [Fact]
    public void A_relative_cscode_home_is_made_absolute_against_the_working_directory()
    {
        // sessions-9: the launcher and the session process it starts (in the user's home) read different directories.
        Assert.Equal("/home/u/proj/portable", AppStartup.AbsoluteHome("portable", "/home/u/proj"));
        Assert.Equal("/home/u/portable", AppStartup.AbsoluteHome("../portable", "/home/u/proj"));
        Assert.Null(AppStartup.AbsoluteHome("/opt/cscode-home", "/home/u/proj"));
        Assert.Null(AppStartup.AbsoluteHome(null, "/home/u/proj"));
        Assert.Null(AppStartup.AbsoluteHome("", "/home/u/proj"));
    }
}

public class HiddenSessionTrimTests
{
    private sealed class CountingMonitor : IMemoryMonitor
    {
        public List<MemoryTrimLevel> Trims { get; } = new();
        public MemorySnapshot Current => throw new NotSupportedException();
        public long BudgetBytes => 0;
        public IEventSource<MemorySnapshot> Sampled => throw new NotSupportedException();
        public IReadOnlyList<IMemoryTrimmable> Trimmables => [];
        public IDisposable Register(IMemoryTrimmable trimmable) => throw new NotSupportedException();
        public IDisposable TrackChildProcess(string name, int pid, long? restartAboveBytes = null, Func<ValueTask>? onOverBudget = null) => throw new NotSupportedException();
        public ValueTask TrimNowAsync(MemoryTrimLevel level, CancellationToken ct = default)
        {
            Trims.Add(level);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_hidden_session_is_trimmed_once_by_the_monitor_not_twice()
    {
        // T40: TrimNowAsync(Aggressive) already compacts and runs malloc_trim; a second process-level trim after it paused every thread again.
        var monitor = new CountingMonitor();
        var processTrims = 0;
        var workingSet = await SessionWindowController.TrimHiddenProcessAsync(monitor, () => { processTrims++; return 1; });
        Assert.Equal([MemoryTrimLevel.Aggressive], monitor.Trims);
        Assert.Equal(0, processTrims);
        Assert.True(workingSet > 0);

        // Without a monitor, the process-level trim alone.
        Assert.Equal(42, await SessionWindowController.TrimHiddenProcessAsync(null, () => 42));
    }

    [Fact]
    public async Task The_shells_stand_in_monitor_compacts_after_the_trimmables_as_the_contract_says()
    {
        using var monitor = new ShellMemoryMonitor(null, false, new ChildProcessTracker());
        var gen2 = GC.CollectionCount(2);
        await monitor.TrimNowAsync(MemoryTrimLevel.Aggressive);
        Assert.True(GC.CollectionCount(2) > gen2, "no full collection ran");
    }
}

public class AutostartUnitTests
{
    [Fact]
    public void The_systemd_unit_keeps_the_processes_the_launcher_starts()
    {
        // sessions-5: a oneshot unit without RemainAfterExit stopped as soon as the launcher exited, and its default KillMode killed every
        // process the launcher had started. And KillMode=process (the first fix) meant stopping the unit at logout signalled only the launcher,
        // long gone: the sessions never got the SIGTERM they save on, and were SIGKILLed with the user manager. RemainAfterExit alone keeps
        // them, and the default KillMode=control-group sends them SIGTERM when the unit stops (systemd.kill(5)). --dry-run prints the unit and
        // changes nothing (the user's systemd is never touched).
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux autostart");
        using var home = new TempDirectory("cscode-unit-");
        var psi = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { Path.Combine(PublishedAppFixture.RepoRoot(), "tools", "autostart", "install-linux.sh"), "--systemd", "--dry-run", "--cscode", "/bin/true" })
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = home.Path;
        psi.Environment["XDG_CONFIG_HOME"] = home.Combine("config");
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("Type=oneshot", output);
        Assert.Contains("RemainAfterExit=yes", output);
        Assert.DoesNotContain("KillMode=", output); // control-group: SIGTERM to the sessions when the unit stops
        Assert.Contains("TimeoutStopSec=30", output); // past the session's own 15 s SIGTERM watchdog
        Assert.Contains("ExecStart=/bin/true --background", output);
        Assert.False(Directory.Exists(home.Combine("config", "systemd")), "a dry run wrote the unit");
    }
}

public class PublishPinTests
{
    private static (int Code, string Output) Run(string file, IEnumerable<string> args, IDictionary<string, string>? env = null, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = PublishedAppFixture.RepoRoot() };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(entireProcessTree: true); // the process this test started
            throw new TimeoutException($"{file} did not finish");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void The_publish_restores_exactly_the_pinned_pack_versions_whatever_the_sdk_bundles()
    {
        // sessions-13: the publish took whatever packs the SDK bundled (a newer SDK: newer, unchecked packs) while the pin check passed on the old ones.
        if (!OperatingSystem.IsLinux()) Assert.Skip("the pins cover linux-x64");
        var pinned = Run("bash", [Path.Combine("tools", "fetch-publish-packs.sh"), "--print-version"]).Output.Trim();
        Assert.Matches(@"^\d+\.\d+\.\d+$", pinned);
        Assert.Contains($"<CsCodePinnedRuntimeVersion Condition=\"'$(CsCodePinnedRuntimeVersion)' == ''\">{pinned}</CsCodePinnedRuntimeVersion>",
            File.ReadAllText(Path.Combine(PublishedAppFixture.RepoRoot(), "build", "PinnedRuntimePacks.props")));

        // Evaluation only (no download): the packs restore would fetch, for the pin and for a version no SDK bundles. The app, and the
        // cscode-server it ships (its publish builds it as a project of its own: it took the SDK's packs before).
        foreach (var project in new[] { Path.Combine("src", "CsCode.App", "CsCode.App.csproj"), Path.Combine("src", "CsCode.Server", "CsCode.Server.csproj") })
        foreach (var version in new[] { pinned, "10.0.99" })
        {
            using var obj = new TempDirectory("cscode-pin-obj-");
            var (code, output) = Run("dotnet", ["msbuild", project, "-nologo", "-t:CollectPackageDownloads", "-getItem:PackageDownload",
                "-p:RuntimeIdentifier=linux-x64", "-p:SelfContained=true", "-p:CsCodeNuGetRuntimePacks=true", "-p:PublishReadyToRun=true", "-p:Configuration=Release",
                $"-p:CsCodePinnedRuntimeVersion={version}", $"-p:BaseIntermediateOutputPath={obj.Path}/"], timeoutMs: 300_000);
            Assert.True(code == 0, output);
            foreach (var pack in new[] { "Microsoft.NETCore.App.Runtime.linux-x64", "Microsoft.NETCore.App.Host.linux-x64", "Microsoft.NETCore.App.Crossgen2.linux-x64", "Microsoft.NETCore.App.Ref" })
                Assert.Matches($"\"Identity\": \"{System.Text.RegularExpressions.Regex.Escape(pack)}\",[^}}]*\"Version\": \"\\[{System.Text.RegularExpressions.Regex.Escape(version)}\\]\"", output);
        }
        // And publish-app.sh checks the pins before it publishes, and the output after.
        var script = File.ReadAllText(Path.Combine(PublishedAppFixture.RepoRoot(), "tools", "publish-app.sh"));
        Assert.Contains("if ! bash \"$repo/tools/fetch-publish-packs.sh\"; then", script);
        Assert.Contains("-p:CsCodePinnedRuntimeVersion=$pinned", script);
        Assert.Contains("runtimepack.Microsoft.NETCore.App.Runtime.$rid/$pinned", script);
        Assert.Contains("deps_files+=(cscode-server.deps.json)", script); // and the cscode-server it ships
    }

    [Fact]
    public void Netcoredbg_is_fetched_only_as_the_pinned_release_and_archives_with_links_are_refused()
    {
        // sessions-13: "latest" was checked only against the digest of the same API answer, and link entries were extracted.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/tar") && !File.Exists("/bin/tar")) Assert.Skip("Linux, tar");
        var script = Path.Combine("tools", "fetch-netcoredbg.sh");
        Assert.DoesNotContain("releases/latest", File.ReadAllText(Path.Combine(PublishedAppFixture.RepoRoot(), script)));

        // Another release needs its digest too.
        var unpinned = Run("bash", [script], new Dictionary<string, string> { ["NETCOREDBG_TAG"] = "1.0.0-1" });
        Assert.Equal(2, unpinned.Code);
        Assert.Contains("not the pinned release", unpinned.Output);

        using var dir = new TempDirectory("cscode-ncdbg-");
        // A local archive that is not the pinned one: refused before anything is extracted.
        var fake = dir.Combine("netcoredbg-linux-amd64.tar.gz");
        File.WriteAllBytes(fake, [0x1f, 0x8b, 0, 0]);
        var mismatch = Run("bash", [script, "--force"], new Dictionary<string, string> { ["NETCOREDBG_ARCHIVE"] = fake });
        Assert.Equal(1, mismatch.Code);
        Assert.Contains("SHA-256 mismatch", mismatch.Output);

        // An archive with a symbolic link (its digest given, as for another release): refused before extraction.
        Directory.CreateDirectory(dir.Combine("x", "netcoredbg"));
        File.WriteAllText(dir.Combine("x", "netcoredbg", "netcoredbg"), "#!/bin/sh\n");
        File.CreateSymbolicLink(dir.Combine("x", "netcoredbg", "link"), "/etc/passwd");
        var evil = dir.Combine("evil.tar.gz");
        Assert.Equal(0, Run("tar", ["-czf", evil, "-C", dir.Combine("x"), "netcoredbg"]).Code);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evil)));
        var linked = Run("bash", [script, "--force"], new Dictionary<string, string> { ["NETCOREDBG_TAG"] = "test", ["NETCOREDBG_SHA256"] = sha, ["NETCOREDBG_ARCHIVE"] = evil });
        Assert.Equal(1, linked.Code);
        Assert.Contains("links or special files", linked.Output);
    }
}

public class UnusableSessionLockTests
{
    [Fact]
    public async Task A_session_process_whose_data_directory_cannot_hold_locks_says_so_and_never_claims_another_owner()
    {
        // sessions-7: with the locks in the data directory, a read-only one made a session process exit with 3, "owned by another CsCode process",
        // and a process without --session (TakeNewUnnamedSession) tried window-1, window-2, ... for ever.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions");
            return;
        }
        using var home = new TempDirectory("csl-");
        var runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? home.Path : null);
        var store = new SessionStore(home.Combine("data"));
        Directory.CreateDirectory(store.SessionsDir);
        File.SetUnixFileMode(store.SessionsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try { File.WriteAllText(Path.Combine(store.SessionsDir, "probe"), ""); Assert.Skip("this user can write into a mode 0500 directory"); }
            catch (UnauthorizedAccessException) { }
            foreach (var args in new[] { new[] { "--host", "--session", "U", "--headless-test" }, new[] { "--host", "--headless-test" } })
            {
                int? refused = null;
                var start = Task.Run(() => BackgroundHost.Start(CommandLineOptions.Parse(args), new CrashHandler(home.Combine("crash")), runtime, store, out refused));
                var host = await start.WaitAsync(TimeSpan.FromSeconds(20)); // it ends (it looped for ever without --session)
                host?.Dispose();
                Assert.Null(host); // background mode is unavailable here (the launcher finds that out first and runs the window in its own process)
                Assert.Null(refused); // not "owned by another CsCode process"
            }
        }
        finally { File.SetUnixFileMode(store.SessionsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
}
