using CsCode.Core.Platform;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// lsp-6 / dbg-4: tasks and debug sessions started while the login shell's environment is still being read (a Mac app just started from the Dock)
/// wait for it, so their children inherit the merged environment. The wait is made pending by hand (<see cref="LoginShellEnvironment.PendForTests"/>),
/// so this runs on any OS; one class, so its tests never overlap.
/// </summary>
public class LoginShellWaitTests
{
    private static readonly TimeSpan Held = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task A_task_started_during_the_resolution_runs_with_the_merged_environment()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        var name = "CSCODE_TEST_SHELL_ENV_" + Guid.NewGuid().ToString("N");
        await using var f = new TaskFixture($$"""{"tasks":[{"label":"env","type":"shell","command":"printf '%s' \"${{{name}}}\" > out.txt"}]}""");
        var resolution = new TaskCompletionSource<LoginShellEnvironment.Resolution?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<CsCode.Core.Tasks.TaskResult> run;
            using (LoginShellEnvironment.PendForTests(resolution.Task))
            {
                run = f.Service.RunAsync("env", null, Ct).AsTask();
                await Task.Delay(Held, Ct);
                Assert.False(run.IsCompleted);
                Assert.False(File.Exists(Path.Combine(f.Dir.Path, "out.txt")), "the task started before the environment was read");
                Environment.SetEnvironmentVariable(name, "from-the-login-shell"); // what the merge does
                resolution.SetResult(null);
            }
            Assert.True((await run).Succeeded);
            Assert.Equal("from-the-login-shell", File.ReadAllText(Path.Combine(f.Dir.Path, "out.txt")));
        }
        finally
        {
            resolution.TrySetResult(null);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task A_debug_session_started_during_the_resolution_contacts_its_adapter_only_after_it()
    {
        await using var h = new ServiceHarness();
        var resolution = new TaskCompletionSource<LoginShellEnvironment.Resolution?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<CsCode.Core.Debugging.IDebugSession> start;
            using (LoginShellEnvironment.PendForTests(resolution.Task))
            {
                start = h.StartAsync();
                await Task.Delay(Held, Ct);
                Assert.False(start.IsCompleted);
                Assert.Equal(0, Volatile.Read(ref h.FakeFactory.Calls));
                resolution.SetResult(null);
            }
            await start;
            Assert.Equal(1, Volatile.Read(ref h.FakeFactory.Calls));
        }
        finally { resolution.TrySetResult(null); }
    }
}
