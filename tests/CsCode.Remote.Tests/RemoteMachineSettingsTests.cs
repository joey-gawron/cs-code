using System.Text;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Shell;
using CsCode.Server;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// The remote host's machine settings file as the client sees it (the Settings page's Remote tab, Open Remote Settings): its ssh: URI from the
/// server's data dir, writes through <c>settings/updateMachine</c> into the server's own file (comment-preserving), change notifications from the
/// remote watcher, and the <c>remoteName</c> context key the commands' when-clause reads. Real loopback server.
/// </summary>
public sealed class RemoteMachineSettingsTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task The_host_s_machine_settings_file_is_read_written_and_watched_through_the_connection()
    {
        await using var host = new RemoteTestHost(machineSettings: "// this host's own\n{\n  \"editor.tabSize\": 5\n}\n");
        var settings = host.Services.GetRequiredService<IRemoteMachineSettings>();
        Assert.Null(settings.SettingsUri);                                         // nothing connected: no remote settings
        var changes = 0;
        using var sub = settings.Changed.Subscribe(_ => Interlocked.Increment(ref changes));

        await host.ConnectAsync();
        var machine = ServerHost.MachineSettingsPath(host.ServerDataDir);
        Assert.Equal(host.Manager.ToUri(machine), settings.SettingsUri);           // ssh://tester@remote-box/<data dir>/machine/settings.json
        Assert.Equal("SSH: " + RemoteTestHost.Host, settings.HostLabel);
        Assert.True(changes >= 1);
        Assert.Contains("\"editor.tabSize\": 5", RemoteTestHost.Text(await host.FileSystem.ReadFileAsync(settings.SettingsUri!, Ct))); // read like any remote file

        var before = Volatile.Read(ref changes);
        await settings.UpdateAsync("cscode.debug.gdbPath", "/opt/gdb-14/bin/gdb", Ct);
        await settings.UpdateAsync("editor.tabSize", 2, Ct);
        var text = File.ReadAllText(machine);                                      // the server's own file, on its disk
        Assert.Contains("// this host's own", text);
        Assert.Contains("\"cscode.debug.gdbPath\": \"/opt/gdb-14/bin/gdb\"", text);
        Assert.Contains("\"editor.tabSize\": 2", text);
        Assert.True(Volatile.Read(ref changes) >= before + 2);
        await settings.UpdateAsync("editor.tabSize", null, Ct);
        Assert.DoesNotContain("editor.tabSize", File.ReadAllText(machine));

        // refused by the server: an application-scoped setting is never a remote host's
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await settings.UpdateAsync("terminal.integrated.inheritEnv", false, Ct));
        Assert.Contains("application scope", refused.Message);

        // changed on the host (or saved from the editor): the watcher tells the Settings page
        var seen = Volatile.Read(ref changes);
        File.WriteAllText(machine, "{ \"editor.tabSize\": 7 }");
        await TestEnv.WaitUntilAsync(() => Volatile.Read(ref changes) > seen, TimeSpan.FromSeconds(20), "a Changed for an edit made on the host");

        await host.Manager.DisconnectAsync(Ct);
        Assert.Null(settings.SettingsUri);
        Assert.Null(settings.HostLabel);
        await Assert.ThrowsAnyAsync<IOException>(async () => await settings.UpdateAsync("editor.tabSize", 3, Ct));
    }

    [Fact]
    public async Task The_remote_name_context_key_says_whether_and_how_a_remote_host_is_connected()
    {
        var keys = new TestContextKeyService();
        await using var host = new RemoteTestHost(services: s => s.AddSingleton<IContextKeyService>(keys));
        using var shell = host.Services.GetRequiredService<RemoteShellContributions>().Start();
        Assert.Null(keys.Get(RemoteShellContributions.RemoteNameKey));
        Assert.False(keys.Evaluate("remoteName"));

        await host.ConnectAsync();
        await TestEnv.WaitUntilAsync(() => keys.Get(RemoteShellContributions.RemoteNameKey) is "ssh-remote", TimeSpan.FromSeconds(10), "remoteName = ssh-remote");
        Assert.True(keys.Evaluate("remoteName"));

        await host.Manager.DisconnectAsync(Ct);
        await TestEnv.WaitUntilAsync(() => keys.Get(RemoteShellContributions.RemoteNameKey) is null, TimeSpan.FromSeconds(10), "remoteName cleared");
    }

    [Fact]
    public void The_machine_settings_file_is_where_the_server_reads_it()
    {
        Assert.Equal(ServerHost.MachineSettingsPath("/home/me/.cscode-server/data"), RemoteMachineSettings.PathIn("/home/me/.cscode-server/data/"));
        Assert.Equal("attached-container", RemoteShellContributions.RemoteName(new RemoteConnectionStatus(RemoteConnectionState.Connected, "web", new Uri("docker://web/"))));
        Assert.Equal("wsl", RemoteShellContributions.RemoteName(new RemoteConnectionStatus(RemoteConnectionState.Connected, "Ubuntu", new Uri("wsl://ubuntu/"))));
        Assert.Null(RemoteShellContributions.RemoteName(new RemoteConnectionStatus(RemoteConnectionState.Reconnecting, "box", new Uri("ssh://box/"))));
    }
}
