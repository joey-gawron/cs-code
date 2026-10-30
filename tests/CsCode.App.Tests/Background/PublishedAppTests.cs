using System.Diagnostics;
using System.Text;
using CsCode.App.Tests.Infrastructure;
using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// The app as the installer will publish it (tools/publish-app.sh: self-contained, ReadyToRun composite, official runtime packs;
/// build/Publish.props), published once for this class into the repository's git-ignored <c>.scratch/publish-test/</c>, or taken from
/// <c>CSCODE_PUBLISHED_APP</c> (a publish directory) when set.
/// </summary>
public sealed class PublishedAppFixture : IAsyncLifetime
{
    public string? Directory { get; private set; }
    public string? SkipReason { get; private set; }
    private bool _owned;

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            SkipReason = "the published-app tests cover linux-x64";
            return;
        }
        if (Environment.GetEnvironmentVariable("CSCODE_PUBLISHED_APP") is { Length: > 0 } given)
        {
            Directory = given;
            return;
        }
        var repo = RepoRoot();
        var output = Path.Combine(repo, ".scratch", "publish-test", "app");
        var psi = new ProcessStartInfo("bash")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repo,
        };
        foreach (var a in new[] { Path.Combine(repo, "tools", "publish-app.sh"), "--out", output, "--artifacts", Path.Combine(repo, ".scratch", "publish-test", "artifacts") })
            psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true); // the publish this fixture started
            throw new TimeoutException("tools/publish-app.sh did not finish within 15 minutes");
        }
        var log = await stdout + await stderr;
        if (p.ExitCode != 0)
        {
            // Offline with an empty NuGet cache, the runtime packs cannot be restored: that is a missing tool, not a failure of the publish settings.
            if (log.Contains("NU1301", StringComparison.Ordinal) || log.Contains("Unable to load the service index", StringComparison.Ordinal))
            {
                SkipReason = "the official runtime packs are not in the NuGet cache and nuget.org is unreachable (tools/fetch-publish-packs.sh)";
                return;
            }
            throw new InvalidOperationException($"tools/publish-app.sh failed ({p.ExitCode}):\n{log}");
        }
        Directory = output;
        _owned = true;
    }

    public ValueTask DisposeAsync()
    {
        // The 200 MB output goes; the intermediate build output stays (the next run is incremental).
        if (_owned && Directory is not null)
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return ValueTask.CompletedTask;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// The published app runs on the system's loader and libraries: no dotnet, no Nix shell, nothing from the environment but what <c>env -i</c> passes
/// (docs/briefs/app-icon-and-install.md: "Verify it starts from a clean environment"; a missing library is fixed in the publish, never with
/// LD_LIBRARY_PATH into the Nix store).
/// </summary>
public sealed class PublishedAppTests(PublishedAppFixture published) : IClassFixture<PublishedAppFixture>
{
    private string App()
    {
        if (published.SkipReason is { } why) Assert.Skip(why);
        return Path.Combine(published.Directory!, "cscode");
    }

    [Fact]
    public void No_file_of_the_publish_refers_to_the_nix_store()
    {
        var dir = Path.GetDirectoryName(App())!;
        var needle = Encoding.ASCII.GetBytes("/nix/store/");
        var offenders = new List<string>();
        var elf = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            // Native files only (ELF): their interpreter, RUNPATH and any embedded path. Managed assemblies may mention the store in strings.
            if (bytes.Length < 4 || bytes[0] != 0x7f || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F') continue;
            elf++;
            if (bytes.AsSpan().IndexOf(needle) >= 0) offenders.Add(Path.GetRelativePath(dir, file));
        }
        Assert.True(elf >= 10, $"only {elf} native files found in {dir}");
        Assert.Empty(offenders);
        Assert.True(File.Exists(Path.Combine(dir, "cscode.r2r.dll")), "the ReadyToRun composite image is missing (build/Publish.props)");
        Assert.True(File.Exists(Path.Combine(dir, "libcoreclr.so")), "not self-contained: the runtime is missing");
    }

    [Fact]
    public async Task The_published_app_starts_headless_with_env_i_and_opens_a_session()
    {
        var app = App();
        using var t = new CleanEnvironment();
        var file = t.WriteFile("notes.cs", "namespace Demo;\npublic class Notes { }\n");

        var version = await t.RunAsync(app, "--version");
        Assert.True(version.ExitCode == 0, version.Error);
        Assert.Matches(@"^\d+\.\d+\.\d+", version.Output.Trim());

        // The launcher starts a session process (background mode, the default), which builds the services and the window on the headless
        // platform, opens the file and paints; the launcher returns when it painted.
        var open = await t.RunAsync(app, "--headless-test", "--session", "P", file + ":2:8");
        Assert.True(open.ExitCode == 0, $"exit {open.ExitCode}\n{open.Error}\n{t.CrashLogs()}");
        var owner = Assert.Single(ResidentDiscovery.Discover(t.Runtime, TimeSpan.FromSeconds(2)), p => p.Session == "P");
        Assert.Equal(ProcessStates.Shown, owner.State);
        var inspect = IpcClient.Request(t.Runtime.EndpointOf(owner.Pid), new IpcMessage { Type = IpcVerbs.Inspect }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        Assert.True(inspect.Ok == true, inspect.Error);
        Assert.Contains(new Uri(file).ToString(), inspect.Data!.Value.GetRawText());
        // Its environment is exactly what env -i passed (and what the launcher added): no LD_LIBRARY_PATH, no DOTNET_ROOT, no DISPLAY.
        var environ = File.ReadAllText($"/proc/{owner.Pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('=')[0]).ToHashSet();
        Assert.DoesNotContain("LD_LIBRARY_PATH", environ);
        Assert.DoesNotContain("DOTNET_ROOT", environ);
        Assert.DoesNotContain("DISPLAY", environ);
        Assert.DoesNotContain("PATH", environ);

        var list = await t.RunAsync(app, "--headless-test", "--list-sessions");
        Assert.Matches($@"P\s+shown\s+{owner.Pid}", list.Output);
        var quit = await t.RunAsync(app, "--headless-test", "--quit", "--all");
        Assert.True(quit.ExitCode == 0, quit.Error);
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !ProcessProbe.IsRunning(owner.Pid), 20_000), "the session process did not exit");
        Assert.Empty(t.CrashLogs());
    }

    [Fact]
    public async Task The_published_app_opens_a_real_x11_window_with_the_system_libraries()
    {
        var app = App();
        using var x = PrivateXServer.TryStart(out var why);
        if (x is null) Assert.Skip(why);
        using var t = new CleanEnvironment(display: x.Display);
        var file = t.WriteFile("a.txt", "hello\n");

        // The real X11 backend: libX11, libXcursor, libXrandr, libGL... come from the system (no Nix library path is set).
        var open = await t.RunAsync(app, "--session", "X", file);
        Assert.True(open.ExitCode == 0, $"exit {open.ExitCode}\n{open.Error}\n{t.CrashLogs()}");
        var owner = Assert.Single(ResidentDiscovery.Discover(t.Runtime, TimeSpan.FromSeconds(2)), p => p.Session == "X");
        Assert.Equal(ProcessStates.Shown, owner.State);
        var maps = File.ReadAllText($"/proc/{owner.Pid}/maps");
        Assert.Contains("libX11.so", maps);
        Assert.DoesNotContain("/nix/store/", maps);

        var quit = await t.RunAsync(app, "--quit", "--all");
        Assert.True(quit.ExitCode == 0, quit.Error);
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !ProcessProbe.IsRunning(owner.Pid), 20_000), "the session process did not exit");
        Assert.Empty(t.CrashLogs());
    }

    /// <summary>
    /// <c>env -i</c> with only HOME, XDG_RUNTIME_DIR and CSCODE_HOME in a private temp directory (plus DISPLAY of a private Xvfb when given).
    /// Dispose quits and kills (by PID) every process of that CSCODE_HOME.
    /// </summary>
    private sealed class CleanEnvironment : IDisposable
    {
        private readonly string? _display;

        public CleanEnvironment(string? display = null)
        {
            _display = display;
            Root = Path.Combine("/tmp", "csp-" + Guid.NewGuid().ToString("N")[..8]); // short: Unix socket paths are limited
            foreach (var d in new[] { "home", "run", "cs/config", "ws" }) System.IO.Directory.CreateDirectory(Path.Combine(Root, d));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(Root, "run"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(Root, "cs", "config", "settings.json"), """{ "workbench.startupEditor": "none" }""");
            Runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? CsCodeHome : null);
        }

        public string Root { get; }
        public string CsCodeHome => Path.Combine(Root, "cs");
        public RuntimeDirectory Runtime { get; }

        public string WriteFile(string name, string text)
        {
            var path = Path.Combine(Root, "ws", name);
            File.WriteAllText(path, text);
            return path;
        }

        public async Task<LauncherRun> RunAsync(string app, params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/env") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("HOME=" + Path.Combine(Root, "home"));
            psi.ArgumentList.Add("XDG_RUNTIME_DIR=" + Path.Combine(Root, "run"));
            psi.ArgumentList.Add("CSCODE_HOME=" + CsCodeHome);
            if (_display is not null) psi.ArgumentList.Add("DISPLAY=" + _display);
            psi.ArgumentList.Add(app);
            foreach (var a in args) psi.ArgumentList.Add(a);
            var clock = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException($"cscode {string.Join(' ', args)} did not exit within 90 s");
            }
            return new LauncherRun(p.ExitCode, await output, await error, clock.Elapsed);
        }

        public string CrashLogs()
        {
            var logs = Path.Combine(CsCodeHome, "logs");
            return System.IO.Directory.Exists(logs)
                ? string.Join("\n", System.IO.Directory.GetFiles(logs, "crash-*").Select(f => f + ":\n" + File.ReadAllText(f)))
                : "";
        }

        /// <summary>Processes whose environment has this CSCODE_HOME (only ours can).</summary>
        private List<int> Processes()
        {
            var needle = Encoding.UTF8.GetBytes("CSCODE_HOME=" + CsCodeHome + "\0");
            var pids = new List<int>();
            foreach (var dir in System.IO.Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
                try { if (File.ReadAllBytes(Path.Combine(dir, "environ")).AsSpan().IndexOf(needle) >= 0 && ProcessProbe.IsRunning(pid)) pids.Add(pid); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return pids;
        }

        public void Dispose()
        {
            foreach (var pid in Processes())
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            try { System.IO.Directory.Delete(Root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
