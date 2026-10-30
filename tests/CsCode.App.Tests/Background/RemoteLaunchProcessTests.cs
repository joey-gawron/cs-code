using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// T5 end to end with REAL processes: <c>cscode ssh://host/path</c> in background mode (the default) goes through the launcher's IPC to the session
/// process, which connects and opens the remote folder (remote.openFolder). The "ssh" is a stand-in script (no sshd here, never the user's ssh,
/// keys or agent): it answers <c>ssh -G</c> and runs the remote command with this machine's sh, with HOME in the test's temp dir, as sshd would on
/// the remote. The session bootstraps the published server through it (upload, checksum, install, start).
/// </summary>
public class RemoteLaunchProcessTests
{
    private static string? Bundles()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx")))
            {
                var dir = Path.Combine(d.FullName, ".scratch", "server-bundles");
                return File.Exists(Path.Combine(dir, "manifest.json")) ? dir : null;
            }
        return null;
    }

    [Fact]
    public async Task An_ssh_target_on_the_command_line_reaches_the_session_which_connects_and_opens_the_remote_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "needs /bin/sh");
        var bundles = Bundles();
        Assert.SkipWhen(bundles is null, "no server bundles: run tools/publish-server.sh (writes .scratch/server-bundles)");
        var home = Path.Combine("/tmp", "csr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var fakeSsh = Path.Combine(home, "fake-ssh");
        var remoteHome = Path.Combine(home, "remote-home");
        Directory.CreateDirectory(remoteHome);
        File.WriteAllText(fakeSsh, $$"""
            #!/bin/sh
            # Test stand-in for ssh: "-G" prints a resolved configuration; otherwise everything up to "--" (ssh's options) and the destination after
            # it are dropped and the remote command runs through this machine's sh, as sshd would run it on the remote.
            for a in "$@"; do [ "$a" = "-G" ] && { echo "user tester"; echo "hostname fakehost"; echo "port 22"; exit 0; }; done
            while [ $# -gt 0 ] && [ "$1" != "--" ]; do shift; done
            shift 2
            export HOME='{{remoteHome}}'
            unset SSH_AUTH_SOCK
            exec /bin/sh -c "$1"
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fakeSsh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var h = new SessionProcessHarness($$"""
                "cscode.background.warmSpare": false,
                "remote.ssh.path": "{{fakeSsh}}",
                "remote.ssh.serverBundles": "{{bundles}}",
                "remote.ssh.serverInstallPath": "{{Path.Combine(home, "remote-install")}}",
                "remote.ssh.useAskpass": false,
                "remote.ssh.reconnectGracePeriod": 0
                """);
            var folder = $"ssh://tester@fakehost{h.Workspace}";
            var run = await h.RunAsync("--session", "R", folder);
            Assert.True(run.ExitCode == 0, run.Error);
            var owner = h.Discover().First(p => p.Session == "R");
            // the launcher returned once the window was shown; the session connects (installing the server) and opens the folder after that
            var expected = new Uri(folder.TrimEnd('/') + "/").ToString();
            Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Inspect(owner.Pid).Folders.Contains(expected), 120_000),
                $"the remote folder did not open; folders: {string.Join(", ", h.Inspect(owner.Pid).Folders)}\n{h.Trace()}");
            Assert.True(Directory.Exists(Path.Combine(home, "remote-install", "bin")), "the server was installed through the ssh stand-in");

            // R4, persistence across a client restart: the session quits (saved), and starting it again reconnects and reopens the remote folder
            h.Must(owner.Pid, new IpcMessage { Type = IpcVerbs.Quit, Force = true });
            Assert.True(await SessionProcessHarness.WaitUntilAsync(() => !Infrastructure.ProcessProbe.IsRunning(owner.Pid), 30_000), "the session did not quit");
            Assert.Contains(expected, h.Store.Load("R").Session!.Folders);
            var again = await h.RunAsync("--session", "R");
            Assert.True(again.ExitCode == 0, again.Error);
            var restored = h.Discover().First(p => p.Session == "R");
            Assert.NotEqual(owner.Pid, restored.Pid);
            Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.Inspect(restored.Pid).Folders.Contains(expected), 120_000),
                $"the restored session did not reopen the remote folder; folders: {string.Join(", ", h.Inspect(restored.Pid).Folders)}\n{h.Trace()}");
            h.Must(restored.Pid, new IpcMessage { Type = IpcVerbs.Quit, Force = true });
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
