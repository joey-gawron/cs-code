using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Shell;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>The commands and the status-bar indicator, driven through the command service like the palette/menu would.</summary>
public class ShellContributionTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task Connect_open_folder_and_disconnect_commands_drive_the_connection_workspace_and_indicator()
    {
        using var cfgDir = new TempDirectory("cscode-shellcfg-");
        var sshConfig = cfgDir.WriteFile("ssh_config", "Host build-box dev-*\n  HostName 10.0.0.1\nHost other\n");
        await using var host = new RemoteTestHost(userSettings: JsonSerializer.Serialize(new Dictionary<string, object> { ["remote.ssh.configFile"] = sshConfig }));
        var shell = host.Services.GetRequiredService<RemoteShellContributions>();
        using var started = shell.Start();
        var commands = host.Services.GetRequiredService<ICommandService>();
        var status = host.Services.GetRequiredService<IStatusBarService>();
        foreach (var id in new[] { RemoteShellContributions.Connect, RemoteShellContributions.OpenFolder, RemoteShellContributions.Disconnect,
                     RemoteShellContributions.Reconnect, RemoteShellContributions.Reinstall, RemoteShellContributions.ShowLog })
            Assert.True(commands.Has(id), id);
        Assert.Contains(status.Items, i => i.Id == RemoteShellContributions.IndicatorId && i.CommandId == RemoteShellContributions.Connect);
        Assert.Equal(["build-box", "other"], shell.ListConfiguredHosts()); // from remote.ssh.configFile, wildcards left out
        Assert.Contains(host.Services.GetRequiredService<IConfigurationService>().Properties, p => p.Key == RemoteSettings.LanguageHost);

        // Connect to Host… with the host given, then the folder prompt (answered with the "remote" workspace path)
        host.Recording.InputResponses.Enqueue(host.Workspace.Path);
        await commands.ExecuteAsync(RemoteShellContributions.Connect, RemoteTestHost.Host);
        Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
        Assert.Equal(host.Remote(), host.WorkspaceService.Folders.Single());
        await TestEnv.WaitUntilAsync(() => status.Items.Any(i => i.Id == RemoteShellContributions.IndicatorId && i.Text.Contains("SSH: " + RemoteTestHost.Host, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5), "indicator");

        // Disconnect closes the remote folder first, then the connection
        await commands.ExecuteAsync(RemoteShellContributions.Disconnect);
        Assert.Empty(host.WorkspaceService.Folders);
        Assert.Equal(RemoteConnectionState.Disconnected, host.Manager.Status.State);
        Assert.Contains(status.Items, i => i.Id == RemoteShellContributions.IndicatorId && i.CommandId == RemoteShellContributions.Connect);

        // `cscode ssh://…/path`: connect and open in one go
        var target = host.Manager.ToUri(host.Workspace.Path + "/")!; // mapper of the last host stays available
        await commands.ExecuteAsync(RemoteShellContributions.OpenFolder, target.AbsoluteUri);
        Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
        Assert.Equal(target, host.WorkspaceService.Folders.Single());
    }

    [Fact]
    public void The_indicator_is_a_badge_in_the_themes_remote_colours_in_every_state()
    {
        var host = Protocol.RemoteAuthority.Parse("me@box");
        foreach (var state in Enum.GetValues<RemoteConnectionState>())
        {
            var item = RemoteShellContributions.Indicator(new RemoteConnectionStatus(state, "me@box", host.Root, "msg"));
            Assert.Equal("statusBarItem.remoteBackground", item.BackgroundColorKey);
            Assert.Equal("statusBarItem.remoteForeground", item.ColorKey);
            Assert.Equal(StatusBarAlignment.Left, item.Alignment);
        }
        Assert.StartsWith("$(remote) SSH: me@box", RemoteShellContributions.Indicator(new RemoteConnectionStatus(RemoteConnectionState.Connected, "me@box", host.Root)).Text);
        var docker = new Uri("docker://c0ffee/");
        Assert.StartsWith("$(remote) Docker: c0ffee", RemoteShellContributions.Indicator(new RemoteConnectionStatus(RemoteConnectionState.Connected, "c0ffee", docker)).Text);
    }

    [Fact]
    public async Task A_failed_connection_shows_an_error_with_retry_and_show_log()
    {
        await using var host = new RemoteTestHost(o => o.TransportFactory = (_, _) => throw new IOException("ssh: connect to host nowhere port 22: Connection refused"));
        using var started = host.Services.GetRequiredService<RemoteShellContributions>().Start();
        host.Recording.MessageActionResponses.Enqueue("Close");
        await host.Services.GetRequiredService<ICommandService>().ExecuteAsync(RemoteShellContributions.Connect, "nowhere");
        Assert.Equal(RemoteConnectionState.Failed, host.Manager.Status.State);
        Assert.Contains(host.Recording.Messages, m => m.Severity == MessageSeverity.Error && m.Message.Contains("Connection refused", StringComparison.Ordinal));
        Assert.Contains(host.Services.GetRequiredService<IStatusBarService>().Items,
            i => i.Id == RemoteShellContributions.IndicatorId && i.CommandId == RemoteShellContributions.Reconnect && i.Text.Contains("(disconnected)", StringComparison.Ordinal));
    }
}
