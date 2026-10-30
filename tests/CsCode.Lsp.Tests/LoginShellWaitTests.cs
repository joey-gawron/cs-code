using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// lsp-1: a language server asked for while the login shell's environment is still being read (a Mac app just started from the Dock) is looked up and
/// started only once it is merged, by the client and by the real process factory. The wait is made pending by hand
/// (<see cref="LoginShellEnvironment.PendForTests"/>), so this runs on any OS; one class, so its tests never overlap.
/// </summary>
public class LoginShellWaitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Held = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task The_client_connects_only_after_the_resolution()
    {
        await using var env = new LspTestEnv();
        env.AddFake();
        var resolution = new TaskCompletionSource<LoginShellEnvironment.Resolution?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (LoginShellEnvironment.PendForTests(resolution.Task))
            {
                env.Open("a.fake", "x");
                await Task.Delay(Held, Ct);
                Assert.Equal(0, env.Connections.ConnectCount);
                lock (env.Connections.Requests) Assert.Empty(env.Connections.Requests);
                resolution.SetResult(null);
            }
            await env.Connections.WaitForServerAsync();
        }
        finally { resolution.TrySetResult(null); }
    }

    [Fact]
    public async Task The_process_factory_starts_the_server_with_the_merged_environment()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("a shell script stands in for the server: Unix only"); return; }
        using var dir = new TempDirectory("cscode-lspwait-");
        var name = "CSCODE_TEST_SHELL_ENV_" + Guid.NewGuid().ToString("N");
        var seen = dir.Combine("env.txt");
        var server = dir.Combine("cscode-fake-server");
        File.WriteAllText(server, $"#!/bin/sh\nprintf '%s' \"${name}\" > '{seen}'\nexec sleep 30\n");
        File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var request = new LspLaunchRequest(new LanguageServerDefinition("t", "T", DocumentSelector.ForLanguage("t"), server, []), null, null, ProcessPriority.Normal);
        var resolution = new TaskCompletionSource<LoginShellEnvironment.Resolution?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<ILspConnection> connect;
            using (LoginShellEnvironment.PendForTests(resolution.Task))
            {
                connect = new DefaultLspConnectionFactory().ConnectAsync(request, Ct).AsTask();
                await Task.Delay(Held, Ct);
                Assert.False(connect.IsCompleted);
                Assert.False(File.Exists(seen), "the server started before the environment was read");
                Environment.SetEnvironmentVariable(name, "from-the-login-shell"); // what the merge does
                resolution.SetResult(null);
            }
            await using var connection = await connect;
            await TestHelpers.EventuallyAsync(() => File.Exists(seen) && new FileInfo(seen).Length > 0, "the server writing the variable");
            Assert.Equal("from-the-login-shell", File.ReadAllText(seen));
            await connection.KillAsync();
        }
        finally
        {
            resolution.TrySetResult(null);
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
