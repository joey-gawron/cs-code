using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// A REAL window on the X server (DISPLAY, e.g. :1), not the headless platform. Avalonia allows one Application per process, so the parent test
/// launches this same test assembly as a child process that runs <see cref="Child"/>: the shell over the dev-host fakes, shown for a few seconds,
/// photographed (RenderTargetBitmap of the live window) and shut down. The parent checks the exit code, the screenshot and that no crash log appeared.
/// </summary>
public class RealWindowSmokeTests
{
    private const string ChildEnv = "CSCODE_REAL_WINDOW_CHILD";

    [Fact]
    public async Task The_real_window_starts_renders_and_exits_cleanly_under_x11()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "an X11 window: elsewhere Avalonia's platform is not X11 (macOS: see The_real_app_starts_on_avalonia_native_and_keeps_running_on_macos)");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) Assert.Skip("DISPLAY is not set: no X server to open a real window on.");
        var dll = typeof(RealWindowSmokeTests).Assembly.Location;
        var home = Path.Combine(Path.GetTempPath(), "cscode-realwin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var shot = Path.Combine(DevHost.ScreenshotDirectory(), "real-window.png");
        if (File.Exists(shot)) File.Delete(shot);

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" -method CsCode.App.Tests.RealWindowSmokeTests.Child")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment[ChildEnv] = "1";
        psi.Environment["CSCODE_HOME"] = home; // never touch the real ~/.cscode
        psi.Environment["CSCODE_SHOT"] = shot;
        using var child = Process.Start(psi)!;
        var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var exited = child.WaitForExit(90_000);
        if (!exited)
        {
            child.Kill(entireProcessTree: true); // only the PID this test started
            Assert.Fail("the real window run did not exit within 90 s");
        }
        var log = await stdout + await stderr;
        try
        {
            Assert.True(child.ExitCode == 0, $"child exit code {child.ExitCode}\n{log}");
            Assert.True(File.Exists(shot), "no screenshot of the live window was produced\n" + log);
            Assert.True(new FileInfo(shot).Length > 20_000);
            var crashLogs = Directory.Exists(Path.Combine(home, "logs")) ? Directory.GetFiles(Path.Combine(home, "logs"), "crash-*.log") : [];
            Assert.True(crashLogs.Length == 0, "the shell reported a crash:\n" + string.Join("\n", crashLogs.Select(File.ReadAllText)));
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    /// <summary>
    /// macOS (pkg-14): the built <c>cscode</c> apphost in the foreground (no background processes) on Avalonia.Native, with a sandboxed
    /// CSCODE_HOME and a folder to open, as a user starts it: the window must come up on the process's real main thread and the app keep running
    /// without a crash report. The test child above cannot do this: xUnit runs it off the main thread, which AppKit refuses. Ended by SIGTERM: the
    /// app has no exit-after-first-frame switch yet, so neither a screenshot nor the orderly shutdown is checked here.
    /// </summary>
    [Fact]
    public async Task The_real_app_starts_on_avalonia_native_and_keeps_running_on_macos()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the Avalonia.Native window is macOS's");
        var configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('/'))!); // bin/<Debug, Release>/net10.0
        var apphost = Path.Combine(Background.PublishedAppFixture.RepoRoot(), "src", "CsCode.App", "bin", configuration, "net10.0", "cscode");
        Assert.SkipUnless(File.Exists(apphost), $"the app is not built ({apphost})");
        using var home = new CsCode.TestDoubles.TempDirectory("cscode-macwin-");
        using var workspace = new CsCode.TestDoubles.TempDirectory("cscode-macwin-ws-");
        workspace.WriteFile("hello.txt", "hello\n");

        var psi = new ProcessStartInfo(apphost) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--foreground");
        psi.ArgumentList.Add(workspace.Path);
        psi.Environment["CSCODE_HOME"] = home.Path; // never touch the real ~/.cscode
        using var app = Process.Start(psi)!;
        var stdout = app.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = app.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var crashed = app.WaitForExit(15_000); // services, the main window, the folder: all up well within this
            var log = crashed ? await stdout + await stderr : "";
            Assert.False(crashed, $"the app exited with {(crashed ? app.ExitCode : 0)} instead of showing its window\n{log}");
        }
        finally
        {
            if (!app.HasExited) app.Kill(); // only the PID this test started (SIGKILL: no orderly shutdown is checked)
            await app.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        var logs = Path.Combine(home.Path, "logs");
        var crashLogs = Directory.Exists(logs) ? Directory.GetFiles(logs, "crash-*.log") : [];
        Assert.True(crashLogs.Length == 0, "the app reported a crash:\n" + string.Join("\n", crashLogs.Select(File.ReadAllText)));
    }

    /// <summary>Runs only in the child process. Starts the real app, waits, captures, exits 0 (or throws).</summary>
    [Fact]
    public void Child()
    {
        if (Environment.GetEnvironmentVariable(ChildEnv) != "1") Assert.Skip("child entry point: only runs when launched by the parent test");
        var host = new DevHost();
        host.SeedWorkspace();
        host.RegisterModuleCommands(Core.Platform.KnownCommands.Find, "undo", "redo", Core.Platform.KnownCommands.DebugStart);
        App.ConfigureServices = host.ApplyTo;
        var shot = Environment.GetEnvironmentVariable("CSCODE_SHOT")!;
        Exception? failure = null;
        var exit = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () =>
            {
                // Runs on the real UI thread once the desktop lifetime is running and the shell has built its main window.
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                try
                {
                    var app = (App)Application.Current!;
                    // The services are built off the UI thread and the main window opens when they are ready.
                    for (var i = 0; i < 600 && lifetime.MainWindow is not MainWindow; i++) await Task.Delay(50);
                    if (lifetime.MainWindow is not MainWindow) throw new InvalidOperationException("the main window did not open");
                    var services = app.Services ?? throw new InvalidOperationException("the shell did not build its services");
                    var nav = (Core.Platform.INavigationService)services.GetService(typeof(Core.Platform.INavigationService))!;
                    await host.WorkspaceService.OpenFolderAsync(host.Workspace.Uri);
                    await nav.OpenAsync(new Uri(Path.Combine(host.Workspace.Path, "src/Demo.App/Program.cs")));
                    await Task.Delay(2500);
                    var window = lifetime.MainWindow!;
                    var frame = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    frame.Render(window);
                    frame.Save(shot, new PngBitmapEncoderOptions());
                    if (window is MainWindow main) main.CloseWithoutPrompt(); // the app's orderly shutdown then ends the lifetime
                }
                catch (Exception ex) { failure = ex; lifetime.Shutdown(1); }
            }, DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
        host.Workspace.Dispose();
        if (failure is not null) throw new InvalidOperationException("real window run failed", failure);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// The real app with EVERY real module on the real desktop lifetime: it starts, opens a folder and a file whose language server (a fake one, if
    /// python3 is available) starts, closes its main window the way the user does, and the process exits 0 with no crash log and the server gone.
    /// </summary>
    [Fact]
    public async Task The_real_app_with_every_module_starts_and_shuts_down_cleanly_under_x11()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "an X11 window: elsewhere Avalonia's platform is not X11 (macOS: see The_real_app_starts_on_avalonia_native_and_keeps_running_on_macos)");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) Assert.Skip("DISPLAY is not set: no X server to open a real window on.");
        using var env = new RealAppHome();
        var python = FakeLanguageServer.Python;
        if (python is not null) env.WriteUserSettings(FakeLanguageServer.Settings(env, python, "polite", shutdownTimeoutMs: 3000));
        var file = env.Workspace.WriteFile("main" + FakeLanguageServer.Extension, "hello\n");
        var pidFile = env.Home.Combine("server.pid");

        var psi = new ProcessStartInfo("dotnet", $"\"{typeof(RealWindowSmokeTests).Assembly.Location}\" -method CsCode.App.Tests.RealWindowSmokeTests.RealAppChild")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment[ChildEnv] = "real";
        psi.Environment["CSCODE_HOME"] = env.Home.Path; // never the user's real config/state
        psi.Environment["XDG_DATA_HOME"] = env.Home.Combine("xdg-data");
        psi.Environment["CSCODE_TEST_WORKSPACE"] = env.Workspace.Path;
        psi.Environment["CSCODE_TEST_OPEN"] = file;
        psi.Environment["CSCODE_TEST_PIDFILE"] = python is null ? "" : pidFile;
        using var child = Process.Start(psi)!;
        var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!child.WaitForExit(120_000))
        {
            child.Kill(entireProcessTree: true); // only the PID this test started
            Assert.Fail("the real app did not exit within 120 s");
        }
        var log = await stdout + await stderr;
        Assert.True(child.ExitCode == 0, $"child exit code {child.ExitCode}\n{log}");
        var logs = env.Home.Combine("logs");
        var crashLogs = Directory.Exists(logs) ? Directory.GetFiles(logs, "crash-*.log") : [];
        Assert.True(crashLogs.Length == 0, "the app reported a crash:\n" + string.Join("\n", crashLogs.Select(File.ReadAllText)));
        if (python is not null)
        {
            var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            using var cleanup = ProcessProbe.KillOnDispose(pid);
            Assert.True(await ProcessProbe.WaitUntilGoneAsync(pid, 3000), $"the language server (pid {pid}) outlived the app");
        }
    }

    /// <summary>Runs only in the child process of <see cref="The_real_app_with_every_module_starts_and_shuts_down_cleanly_under_x11"/>.</summary>
    [Fact]
    public void RealAppChild()
    {
        if (Environment.GetEnvironmentVariable(ChildEnv) != "real") Assert.Skip("child entry point: only runs when launched by the parent test");
        var workspace = Environment.GetEnvironmentVariable("CSCODE_TEST_WORKSPACE")!;
        var file = Environment.GetEnvironmentVariable("CSCODE_TEST_OPEN")!;
        var pidFile = Environment.GetEnvironmentVariable("CSCODE_TEST_PIDFILE");
        Exception? failure = null;
        var exit = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                try
                {
                    for (var i = 0; i < 600 && lifetime.MainWindow is not MainWindow; i++) await Task.Delay(50);
                    if (lifetime.MainWindow is not MainWindow main) throw new InvalidOperationException("the main window did not open: " + lifetime.MainWindow?.GetType().Name);
                    var services = ((App)Application.Current!).Services ?? throw new InvalidOperationException("the app has no services");
                    await services.GetRequiredService<Core.Platform.IWorkspaceService>().OpenFolderAsync(new Uri(Path.TrimEndingDirectorySeparator(workspace) + "/"));
                    await services.GetRequiredService<Core.Platform.INavigationService>().OpenAsync(new Uri(file));
                    if (!string.IsNullOrEmpty(pidFile))
                    {
                        var manager = services.GetRequiredService<Core.Languages.ILanguageServerManager>();
                        int? Pid() => manager.Servers.FirstOrDefault(s => s.Id == FakeLanguageServer.Id && s.State == Core.Languages.LanguageServerState.Running)?.ProcessId;
                        for (var i = 0; i < 400 && Pid() is null; i++) await Task.Delay(50);
                        File.WriteAllText(pidFile, (Pid() ?? throw new InvalidOperationException("the language server did not start")).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    await Task.Delay(1000);
                    main.Close(); // the user's close: prompts (none: nothing is dirty), layout saved, then the app's orderly shutdown ends the lifetime
                }
                catch (Exception ex) { failure = ex; lifetime.Shutdown(1); }
            }, DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([]);
        if (failure is not null) throw new InvalidOperationException("real app run failed", failure);
        Assert.Equal(0, exit);
    }
}
