using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Tests.Hosting;
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
                    var services = app.Services ?? throw new InvalidOperationException("the shell did not build its services");
                    var nav = (Core.Platform.INavigationService)services.GetService(typeof(Core.Platform.INavigationService))!;
                    await host.WorkspaceService.OpenFolderAsync(host.Workspace.Uri);
                    await nav.OpenAsync(new Uri(Path.Combine(host.Workspace.Path, "src/Demo.App/Program.cs")));
                    await Task.Delay(2500);
                    var window = lifetime.MainWindow!;
                    var frame = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    frame.Render(window);
                    frame.Save(shot, new PngBitmapEncoderOptions());
                    if (window is MainWindow main) main.CloseWithoutPrompt();
                }
                catch (Exception ex) { failure = ex; lifetime.Shutdown(1); }
            }, DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
        host.Workspace.Dispose();
        if (failure is not null) throw new InvalidOperationException("real window run failed", failure);
        Assert.Equal(0, exit);
    }
}
