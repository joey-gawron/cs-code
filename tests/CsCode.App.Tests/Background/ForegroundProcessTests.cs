using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// A <c>--foreground</c> window (how the repository's launch.json and Rider/Visual Studio debug CsCode, docs/RUN-AND-DEBUG.md) is not a
/// background process: it opens no session endpoint, so <c>--list-sessions</c> and <c>--quit --all</c> of the same <c>CSCODE_HOME</c> never
/// see it, while they do see and quit that home's background sessions.
/// </summary>
public class ForegroundProcessTests
{
    [Fact]
    public async Task Quit_all_quits_the_homes_background_sessions_and_leaves_a_foreground_window_running()
    {
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": false");
        using var foreground = h.Start("--foreground", h.Workspace);
        try
        {
            var ready = $"pid {foreground.Id} +";
            Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Trace().Split('\n').Any(l => l.Contains(ready) && l.Contains("startup: services ready")) || foreground.HasExited, 60_000),
                "the foreground window never got its services ready\n" + h.Trace());
            if (foreground.HasExited) Assert.Fail($"the foreground window exited ({foreground.ExitCode}): " + await foreground.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));

            var open = await h.RunAsync("--session", "B", h.Workspace); // a background session of the same home
            Assert.True(open.ExitCode == 0, open.Error);
            var background = await h.WaitForAsync(p => p.Session == "B", what: "background session B");
            Assert.DoesNotContain(h.Discover(), p => p.Pid == foreground.Id);

            var quit = await h.RunAsync("--quit", "--all");
            Assert.True(quit.ExitCode == 0, quit.Error);
            Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !Infrastructure.ProcessProbe.IsRunning(background.Pid), 15_000), "the background session should have quit");
            await Task.Delay(1000);
            Assert.False(foreground.HasExited, "--quit --all must not reach a --foreground window: it has no endpoint");
        }
        finally
        {
            if (!foreground.HasExited) foreground.Kill(); // only the PID this test started
            await foreground.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }
}
