using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The REAL app against an opt-in real ssh host (the Remote.Tests real-host suite's variables: <c>CSCODE_TEST_SSH_HOST</c>, <c>CSCODE_TEST_SSH_CONFIG</c>;
/// docs/REMOTE.md "Testing against a real host"): nothing is overridden but the user settings — the system ssh, the bundles from
/// <c>remote.ssh.serverBundles</c>, the app's own askpass and session registry. <c>remote.openFolder ssh://host/dir</c> as the command line does,
/// the Explorer's folder, an edit saved through the editor onto the host's disk, Open Remote Settings, and the app's exit ending the server there.
/// Everything on the host lives in one temp directory, removed at the end. Skipped (CI) unless the host is named.
/// </summary>
public class RealHostAppTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("CSCODE_TEST_SSH_HOST") is { Length: > 0 } h ? h : null;
    private static readonly string? Config = Environment.GetEnvironmentVariable("CSCODE_TEST_SSH_CONFIG") is { Length: > 0 } c ? c : null;

    private static string? Bundles()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_SERVER_BUNDLES") is { Length: > 0 } env && File.Exists(Path.Combine(env, "manifest.json"))) return env;
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CsCode.slnx"))) dir = Path.GetDirectoryName(dir);
        var found = dir is null ? null : Path.Combine(dir, ".scratch", "server-bundles");
        return found is not null && File.Exists(Path.Combine(found, "manifest.json")) ? found : null;
    }

    private static async Task<(int Exit, string Output)> SshAsync(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("ssh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (Config is not null) { psi.ArgumentList.Add("-F"); psi.ArgumentList.Add(Config); }
        foreach (var a in new[] { "-o", "BatchMode=yes", "-T", "--", Host! }) psi.ArgumentList.Add(a);
        static string Q(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        psi.ArgumentList.Add(string.Join(' ', new[] { "sh", "-c", Q(script), "sh" }.Concat(args.Select(Q))));
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return (p.ExitCode, await output.ConfigureAwait(false));
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task The_real_app_opens_a_folder_on_a_real_ssh_host_edits_a_file_there_and_its_exit_ends_the_server()
    {
        Assert.SkipWhen(Host is null, "no real host: set CSCODE_TEST_SSH_HOST (and CSCODE_TEST_SSH_CONFIG) to run this test (docs/REMOTE.md)");
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "the system ssh of a Unix client");
        var bundles = Bundles();
        Assert.SkipWhen(bundles is null, "no server bundles: run tools/publish-server.sh");
        var dir = "/tmp/cscode-realhost-app-" + Guid.NewGuid().ToString("N")[..8];
        var (made, _) = await SshAsync("mkdir -p \"$1\"/ws/src && printf 'hello\\n' > \"$1\"/ws/src/hello.txt", dir);
        Assert.True(made == 0, $"cannot reach {Host} with ssh");
        try
        {
            using var env = new RealAppHome();
            env.WriteUserSettings(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["remote.ssh.configFile"] = Config ?? "",
                ["remote.ssh.serverBundles"] = bundles!,
                ["remote.ssh.serverInstallPath"] = dir + "/install",
                ["remote.ssh.serverDataPath"] = dir + "/data",
                ["remote.ssh.useAskpass"] = false,
            }));
            await using (var run = new RealAppRun(env))
            {
                await run.MainWindowAsync();
                var sp = run.Services;
                var commands = sp.GetRequiredService<ICommandService>();
                await commands.ExecuteAsync("remote.openFolder", $"ssh://{Host}{dir}/ws/");
                var manager = sp.GetRequiredService<IRemoteConnectionManager>();
                Assert.True(manager.Status.IsConnected, manager.Status.Message);
                var workspace = sp.GetRequiredService<IWorkspaceService>();
                Assert.True(await DevHost.WaitFor(() => workspace.PrimaryFolder?.Scheme == "ssh", 30_000), "the remote folder did not open");

                var file = manager.ToUri(dir + "/ws/src/hello.txt")!;
                var doc = await sp.GetRequiredService<INavigationService>().OpenAsync(file) ?? throw new InvalidOperationException("not opened");
                Assert.Equal("hello\n", doc.GetText().Replace("\r\n", "\n", StringComparison.Ordinal));
                Assert.True(doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), "saved from CsCode\n")]));
                await commands.ExecuteAsync(KnownCommands.Save);
                Assert.False(doc.IsDirty);
                Assert.Equal("hello\nsaved from CsCode\n", (await SshAsync("cat \"$1\"", dir + "/ws/src/hello.txt")).Output); // on the host's disk

                await commands.ExecuteAsync("workbench.action.openRemoteSettings");
                var panels = sp.GetRequiredService<CsCode.Panels.Search.SearchPanelsHost>();
                Assert.True(await DevHost.WaitFor(() => panels.LiveSettings?.SelectedScope == CsCode.Panels.Search.Settings.SettingsScope.Remote, 10_000),
                    "Open Remote Settings did not show the Remote tab");
                Assert.Equal($"Remote [SSH: {Host}]", panels.LiveSettings!.RemoteTabTitle);
                Assert.False(panels.LiveSettings.IsWorkspaceAvailable); // a remote folder's settings are its files on the host: no failing Workspace tab
            }
            // the app's exit ended the server on the host (no daemon left waiting for a client)
            // (polled with awaits: blocking on the ssh call here would block the UI thread its continuations need)
            var gone = false;
            for (var deadline = DateTime.UtcNow.AddSeconds(30); !gone && DateTime.UtcNow < deadline; await Task.Delay(500))
                gone = (await SshAsync("pgrep -f -- \"$1\" >/dev/null && echo alive || echo gone", "[" + dir[0] + "]" + dir[1..] + "/install/")).Output.Trim() == "gone";
            Assert.True(gone, "a server process was left on the host after the app exited");
        }
        finally
        {
            await SshAsync("pkill -f -- \"$1\"; rm -rf -- \"$2\"", "[" + dir[0] + "]" + dir[1..] + "/", dir);
        }
    }
}
