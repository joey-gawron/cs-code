using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Services.X11;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Clipboard brief A2/A5 on a REAL window: the real app (every module) runs in a child process on a PRIVATE Xvfb (never the user's display) with
/// Avalonia's X11 backend, and this test process is a second X client (<see cref="X11SelectionClient"/>, its own connection; xclip and xsel are not
/// installed here). Both directions: the editor's Copy and its selection are read from CLIPBOARD and PRIMARY by the other client; text the other
/// client owns (Unicode, CRLF, 2 MB through INCR) is pasted into the editor and read from PRIMARY. The Wayland / XWayland round trip on the user's
/// KDE desktop is the integrator's manual check (docs/SHELL.md).
/// </summary>
public class RealWindowClipboardTests
{
    private const string ChildEnv = "CSCODE_CLIPBOARD_CHILD";

    private static async Task<bool> WaitForFile(string path, Process child, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!File.Exists(path) && !child.HasExited && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        return File.Exists(path);
    }

    [Fact]
    public async Task The_real_app_and_another_X_client_exchange_CLIPBOARD_and_PRIMARY_in_both_directions()
    {
        using var x = PrivateXServer.TryStart(out var why);
        if (x is null) Assert.Skip("no private X server: " + why);
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("doc.txt", "alpha\nbeta 👩‍💻 ünïcode\nomega\n");
        var handshake = env.Home.Combine("handshake");
        Directory.CreateDirectory(handshake);
        var big = string.Concat(Enumerable.Repeat("big line from X ✓\r\n", 110_000)); // ~2 MB of UTF-8, CRLF: INCR into Avalonia

        var psi = new ProcessStartInfo("dotnet", $"\"{typeof(RealWindowClipboardTests).Assembly.Location}\" -method CsCode.App.Tests.RealWindowClipboardTests.Child")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment[ChildEnv] = "1";
        psi.Environment["DISPLAY"] = x!.Display; // the private server only
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.Environment.Remove("SSH_AUTH_SOCK");
        psi.Environment["CSCODE_HOME"] = env.Home.Path;
        psi.Environment["XDG_DATA_HOME"] = env.Home.Combine("xdg-data");
        psi.Environment["CSCODE_TEST_OPEN"] = file;
        psi.Environment["CSCODE_TEST_HANDSHAKE"] = handshake;
        psi.Environment["CSCODE_SHOT"] = Path.Combine(DevHost.ScreenshotDirectory(), "clipboard-real-window.png");
        using var child = Process.Start(psi)!;
        var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var clipboard = X11SelectionClient.TryStart("CLIPBOARD", x.Display)!;
            using var primary = X11SelectionClient.TryStart("PRIMARY", x.Display)!;
            Assert.True(await clipboard.WhenReady && await primary.WhenReady);

            // CsCode -> another application.
            Assert.True(await WaitForFile(Path.Combine(handshake, "copied"), child, 90_000), "the app did not copy\n" + await Log());
            Assert.Equal("beta 👩‍💻 ünïcode", await clipboard.ReadAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("beta 👩‍💻 ünïcode", await primary.ReadAsync(TimeSpan.FromSeconds(5))); // the selection, offered lazily

            // Another application -> CsCode.
            clipboard.SetOwner(() => "pasted\r\nfrom X 🎉" + big);
            primary.SetOwner(() => "primary from X ✓");
            var sw = Stopwatch.StartNew();
            while ((!clipboard.IsOwner || !primary.IsOwner) && sw.ElapsedMilliseconds < 5_000) await Task.Delay(20);
            Assert.True(clipboard.IsOwner && primary.IsOwner);
            await File.WriteAllTextAsync(Path.Combine(handshake, "external"), "", TestContext.Current.CancellationToken);

            Assert.True(await WaitForFile(Path.Combine(handshake, "done"), child, 90_000), "the app did not paste\n" + await Log());
            var text = await File.ReadAllTextAsync(Path.Combine(handshake, "document.txt"), TestContext.Current.CancellationToken);
            var expected = "pasted\nfrom X 🎉" + big.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.True(text.Contains(expected, StringComparison.Ordinal), $"pasted text missing (document is {text.Length} characters)");
            Assert.DoesNotContain('\r', text); // the document's EOL
            Assert.Equal("primary from X ✓", await File.ReadAllTextAsync(Path.Combine(handshake, "primary.txt"), TestContext.Current.CancellationToken));

            Assert.True(child.WaitForExit(60_000), "the app did not exit");
            var log = await Log();
            Assert.True(child.ExitCode == 0, $"child exit code {child.ExitCode}\n{log}");
            var logs = env.Home.Combine("logs");
            Assert.True(!Directory.Exists(logs) || Directory.GetFiles(logs, "crash-*.log").Length == 0, "the app reported a crash\n" + log);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true); // only the process this test started
        }

        async Task<string> Log() => child.HasExited ? await stdout + await stderr : "(still running)";
    }

    /// <summary>Runs only in the child process: the real app on the private X server, driven through its services.</summary>
    [Fact]
    public void Child()
    {
        if (Environment.GetEnvironmentVariable(ChildEnv) != "1") Assert.Skip("child entry point: only runs when launched by the parent test");
        var file = Environment.GetEnvironmentVariable("CSCODE_TEST_OPEN")!;
        var handshake = Environment.GetEnvironmentVariable("CSCODE_TEST_HANDSHAKE")!;
        var shot = Environment.GetEnvironmentVariable("CSCODE_SHOT")!;
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
                    var commands = services.GetRequiredService<ICommandService>();
                    var clipboard = services.GetRequiredService<IClipboardService>();
                    var doc = await services.GetRequiredService<INavigationService>().OpenAsync(new Uri(file), TextRange.FromLines(1, 0, 1, "beta 👩‍💻 ünïcode".Length));
                    var shell = services.GetRequiredService<ShellController>();
                    for (var i = 0; i < 200 && shell.Well.ActiveEditor?.Controller is null; i++) await Task.Delay(50);
                    var editor = shell.Well.ActiveEditor!.Controller!;
                    editor.Focus();
                    editor.SetSelection(TextRange.FromLines(1, 0, 1, "beta 👩‍💻 ünïcode".Length), reveal: true); // offers PRIMARY
                    await Task.Delay(300);
                    await commands.ExecuteAsync("editor.action.clipboardCopyAction");
                    await File.WriteAllTextAsync(Path.Combine(handshake, "copied"), "");

                    for (var i = 0; i < 1200 && !File.Exists(Path.Combine(handshake, "external")); i++) await Task.Delay(50);
                    var lines = doc!.LineCount;
                    editor.SetSelection(TextRange.At(lines - 1, 0), reveal: true);
                    await commands.ExecuteAsync("editor.action.clipboardPasteAction");
                    await File.WriteAllTextAsync(Path.Combine(handshake, "primary.txt"), await clipboard.ReadPrimarySelectionAsync() ?? "(none)");
                    await File.WriteAllTextAsync(Path.Combine(handshake, "document.txt"), doc.GetText());
                    var frame = new RenderTargetBitmap(new PixelSize((int)main.Bounds.Width, (int)main.Bounds.Height));
                    frame.Render(main);
                    frame.Save(shot, new PngBitmapEncoderOptions());
                    await File.WriteAllTextAsync(Path.Combine(handshake, "done"), "");
                    main.CloseWithoutPrompt(); // the document is dirty: no prompt in a test
                }
                catch (Exception ex) { failure = ex; lifetime.Shutdown(1); }
            }, DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([]);
        if (failure is not null) throw new InvalidOperationException("clipboard real window run failed", failure);
        Assert.Equal(0, exit);
    }
}
