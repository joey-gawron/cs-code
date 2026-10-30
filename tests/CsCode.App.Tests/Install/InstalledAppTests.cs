using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using CsCode.App.Tests.Background;
using CsCode.App.Tests.Infrastructure;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Sessions.Ipc;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// The REAL publish (tools/publish-app.sh, as <see cref="PublishedAppFixture"/> builds it, or <c>CSCODE_PUBLISHED_APP</c>) installed by
/// tools/install.sh into a throwaway home, then started the way the user starts it: the installed launcher under <c>env -i</c> with nothing but
/// HOME, a private X server's DISPLAY and a private XDG_RUNTIME_DIR (no CSCODE_HOME: with HOME a temp directory, the app uses the normal config
/// directories inside it, which is what installing is for). It opens a window, the window is cscode's with its icon, and quitting leaves no
/// process. The bundled runtime pieces pass their gates and the bundled debug adapters run from where they were installed.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class InstalledAppTests(PublishedAppFixture published) : IClassFixture<PublishedAppFixture>
{
    private string Publish()
    {
        if (published.SkipReason is { } why) Assert.Skip(why);
        return published.Directory!;
    }

    private static void Ok(SandboxRun run) => Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.All}");

    /// <summary>The clean environment of a desktop start: only HOME (the sandbox's), plus what the caller adds.</summary>
    private static Dictionary<string, string?> Clean(InstallSandbox box, params (string Name, string Value)[] add)
    {
        var env = box.Environment().ToDictionary(kv => kv.Key, _ => (string?)null);
        env["HOME"] = box.Home;
        foreach (var (name, value) in add) env[name] = value;
        return env;
    }

    [Fact]
    public async Task The_installed_launcher_opens_a_window_from_a_clean_environment_and_quitting_leaves_nothing_behind()
    {
        var publish = Publish();
        using var x = PrivateXServer.TryStart(out var why);
        if (x is null) Assert.Skip(why);
        using var box = new InstallSandbox();
        Ok(await box.InstallAsync("--from", publish, "--no-adapters", "--no-server-bundles"));
        // The desktop session's bus, as KDE has one (without it, D-Bus would autolaunch daemons that outlive the app: PrivateSessionBus).
        var (bus, noBus) = await PrivateSessionBus.TryStartAsync(box);
        if (bus is null) Assert.Skip(noBus);
        using var _ = bus;
        var env = Clean(box, ("DISPLAY", x.Display), ("XDG_RUNTIME_DIR", box.Run), ("DBUS_SESSION_BUS_ADDRESS", bus.Address));

        var version = await box.RunAsync(box.Launcher, ["--version"], Clean(box));
        Ok(version);
        Assert.Matches(@"^\d+\.\d+\.\d+\s*$", version.Output);

        var file = Path.Combine(box.Root, "ws", "hello.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "hello\n");
        // Background mode (the default): the launcher starts the session's process, which shows the window; it returns once that painted.
        var open = await box.RunAsync(box.Launcher, [file], env);
        Assert.True(open.ExitCode == 0, $"exit {open.ExitCode}\n{open.All}\n{CrashLogs(box)}");
        var runtime = RuntimeDirectory.Resolve(name => name == "XDG_RUNTIME_DIR" ? box.Run : null);
        var owner = Assert.Single(ResidentDiscovery.Discover(runtime, TimeSpan.FromSeconds(2)));
        Assert.Equal(ProcessStates.Shown, owner.State);
        try
        {
            // It runs the installed build (not a development one), on the system's libraries, with the launcher's environment.
            Assert.StartsWith(box.CurrentVersionDir + "/", new FileInfo($"/proc/{owner.Pid}/exe").LinkTarget);
            var maps = File.ReadAllText($"/proc/{owner.Pid}/maps");
            Assert.Contains("libX11.so", maps);
            Assert.DoesNotContain("/nix/store/", maps);
            var environ = File.ReadAllText($"/proc/{owner.Pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
            Assert.EndsWith(":" + Path.Combine(box.Home, ".dotnet", "tools"), environ["PATH"]);
            Assert.False(environ.ContainsKey("CSCODE_HOME"));
            Assert.False(environ.ContainsKey("LD_LIBRARY_PATH"));
            // The normal per-user directories, inside the throwaway HOME.
            // (not the session locks' folder: the running session holds its window's lock exclusively, so its content cannot be read now)
            var written = box.Snapshot(".local/share/cscode/sessions/.locks").Keys.Where(k => !k.StartsWith(".local/share/cscode/" + new FileInfo(box.Current).LinkTarget, StringComparison.Ordinal)).ToList();
            Assert.True(written.Any(k => k.StartsWith(".local/share/cscode/sessions", StringComparison.Ordinal)), "the app did not use ~/.local/share/cscode:\n" + string.Join("\n", written));

            // The X11 window: class cscode (what StartupWMClass matches) and the 128 px artwork as _NET_WM_ICON (xprop, when installed).
            if (File.Exists("/usr/bin/xwininfo") && File.Exists("/usr/bin/xprop"))
            {
                var xenv = new Dictionary<string, string?> { ["DISPLAY"] = x.Display };
                string? window = null;
                for (var attempt = 0; attempt < 100 && window is null; attempt++)
                {
                    var tree = (await box.RunAsync("/usr/bin/xwininfo", ["-root", "-tree"], xenv)).Output;
                    window = tree.Split('\n').Where(l => l.Contains("(\"cscode\" \"cscode\")", StringComparison.Ordinal))
                        .Select(l => Regex.Match(l, @"(0x[0-9a-f]+).*\s(\d+)x(\d+)\+")).Where(m => m.Success && int.Parse(m.Groups[2].Value) >= 400)
                        .Select(m => m.Groups[1].Value).FirstOrDefault();
                    if (window is null) await Task.Delay(100);
                }
                Assert.True(window is not null, "no cscode window on the X server");
                var props = await box.RunAsync("/usr/bin/xprop", ["-id", window, "-notype", "WM_CLASS", "_NET_WM_ICON"], xenv);
                Assert.Contains("WM_CLASS = \"cscode\", \"cscode\"", props.Output);
                // xprop prints the icon's size first: "= 128, 128, ..." (older) or "Icon (128 x 128):" (newer, with the picture in ASCII art)
                Assert.True(Regex.IsMatch(props.Output, @"_NET_WM_ICON = (128, 128|\s*Icon \(128 x 128\))"), props.Output[..Math.Min(400, props.Output.Length)]);
            }
        }
        finally
        {
            var quit = await box.RunAsync(box.Launcher, ["--quit", "--all"], env);
            Assert.True(quit.ExitCode == 0, quit.All);
        }
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !ProcessProbe.IsRunning(owner.Pid), 20_000), "the session process did not exit");
        // Nothing the app started is left (services the bus activated for it belong to the session, as on the desktop).
        List<int> Left() => box.Processes().Where(pid => !bus.Owns(pid)).ToList();
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => Left().Count == 0, 10_000),
            "processes of the installed app are left:\n" + string.Join("\n", Left().Select(Describe)));
        Assert.Equal("", CrashLogs(box));
    }

    [Fact]
    public async Task The_installed_app_carries_its_runtime_pieces_and_its_debug_adapters_run_from_where_they_were_installed()
    {
        var publish = Publish();
        var main = MainCheckout();
        string? Source(string variable, string fallback) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v && Path.Exists(v) ? v : Path.Exists(fallback) ? fallback : null;
        var netcoredbg = Source("CSCODE_NETCOREDBG", Path.Combine(main, ".scratch", "tools", "netcoredbg", "netcoredbg"));
        var jsDebug = Source("CSCODE_JSDEBUG", Path.Combine(main, ".scratch", "tools", "js-debug", JsDebugInstaller.Version));
        var codelldb = Source("CSCODE_CODELLDB", Path.Combine(main, ".scratch", "downloads", "codelldb", CodeLldbInstaller.Version));
        if (netcoredbg is null && jsDebug is null && codelldb is null)
            Assert.Skip("no debug adapter on this machine (CSCODE_NETCOREDBG, CSCODE_JSDEBUG, CSCODE_CODELLDB or the repository's .scratch)");

        using var box = new InstallSandbox();
        List<string> args = ["--from", publish, "--no-server-bundles"];
        if (netcoredbg is not null) args.AddRange(["--netcoredbg", netcoredbg]);
        if (jsDebug is not null) args.AddRange(["--js-debug", jsDebug]);
        if (codelldb is not null) args.AddRange(["--codelldb", codelldb]);
        var install = await box.InstallAsync([.. args]);
        Ok(install);
        var app = box.CurrentVersionDir;
        using var paths = new TestAppPaths { InstallDir = app };

        // Tree-sitter: the installed copy passes the bundle's version gate (format, target, ABI, every file's SHA-256 recorded at build time).
        if (File.Exists(Path.Combine(publish, TreeSitterBundle.FolderName, "bundle.json")))
        {
            var bundle = TreeSitterBundle.TryOpen(Path.Combine(app, TreeSitterBundle.FolderName), out var problem);
            Assert.True(bundle is not null, problem);
            Assert.NotEmpty(bundle.Grammars);
        }
        Assert.True(File.Exists(Path.Combine(app, "shellIntegration", "cscode.bash")));
        Assert.True(File.Exists(Path.Combine(app, "Adapters", "Gdb", "cscode_gdb.py")), "the GDB helper ships with the app");

        // Each bundled adapter is where the factories look (BundledAdapters) and starts from there in a clean environment.
        if (netcoredbg is not null)
        {
            var path = BundledAdapters.NetCoreDbg(paths);
            Assert.NotNull(path);
            Assert.StartsWith(app + "/", path);
            var run = await box.RunAsync(path, ["--version"], Clean(box));
            Ok(run);
            Assert.Contains("NET Core debugger", run.Output);
        }
        if (codelldb is not null)
        {
            var path = BundledAdapters.CodeLldb(paths);
            Assert.NotNull(path);
            var run = await box.RunAsync(path, ["--help"], Clean(box));
            Ok(run);
            Assert.Contains("--liblldb", run.Output);
        }
        if (jsDebug is not null)
        {
            var script = BundledAdapters.JsDebug(paths);
            Assert.NotNull(script);
            Assert.StartsWith(app + "/", script);
            if ((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => Path.Combine(d, "node")).FirstOrDefault(File.Exists) is { } node)
            {
                // The adapter server listens on a socket path, as JsDebugAdapterFactory starts it; then it is stopped (by its PID).
                var socket = Path.Combine(box.Run, "dap.sock");
                var psi = new ProcessStartInfo(node) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(socket);
                psi.Environment.Clear();
                psi.Environment["HOME"] = box.Home;
                using var server = Process.Start(psi)!;
                try
                {
                    var line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.Matches(JsDebugAdapterFactory.ListeningPattern, line ?? "");
                }
                finally
                {
                    server.Kill(entireProcessTree: true);
                    await server.WaitForExitAsync();
                }
            }
        }
    }

    private static string MainCheckout()
    {
        // Worktrees share the main checkout's .scratch (downloads and tools): <main>/.git is the common git directory.
        var repo = InstallSandbox.RepoRoot();
        var git = Path.Combine(repo, ".git");
        if (File.Exists(git) && File.ReadAllText(git).Trim() is var text && text.StartsWith("gitdir:", StringComparison.Ordinal))
        {
            var gitdir = text["gitdir:".Length..].Trim();
            var marker = $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}";
            var at = gitdir.IndexOf(marker, StringComparison.Ordinal);
            if (at > 0) return gitdir[..at];
        }
        return repo;
    }

    private static string Describe(int pid)
    {
        try { return $"{pid} (parent {File.ReadAllText($"/proc/{pid}/stat").Split(' ')[3]}): {File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ')}"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return pid + " (gone)"; }
    }

    private static string CrashLogs(InstallSandbox box)
    {
        var logs = Path.Combine(box.Home, ".local", "state", "cscode", "logs");
        return Directory.Exists(logs)
            ? string.Join("\n", Directory.GetFiles(logs, "crash-*").Select(f => f + ":\n" + File.ReadAllText(f)))
            : "";
    }
}
