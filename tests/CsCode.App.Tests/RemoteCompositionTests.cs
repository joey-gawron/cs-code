using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Services;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Text;
using CsCode.Remote;
using CsCode.Remote.Bridge;
using CsCode.Remote.Transport;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Remote editing (R1/R2) in the REAL app composition (AppServices with every module, CSCODE_HOME in a temp dir, headless): a remote folder opens in
/// the Explorer, a file is opened, edited and saved through the editor, and the remote indicator follows the connection. The "remote" is a real
/// cscode-server child process (loopback transport instead of ssh, so no sshd is needed here; the ssh path is covered by CsCode.Remote.Tests).
/// </summary>
public class RemoteCompositionTests
{
    private static string ServerAssembly => Path.Combine(AppContext.BaseDirectory, LocalServer.AssemblyFile);

    private static void Remote(IServiceCollection services, TempDirectory serverData, string marker)
    {
        var home = serverData.Combine("home");
        Directory.CreateDirectory(home);
        var launch = LocalServer.Launch(ServerAssembly, serverData.Path, marker, environment: new Dictionary<string, string?>
        {
            ["HOME"] = home, ["XDG_CACHE_HOME"] = Path.Combine(home, ".cache"), ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"), ["SSH_AUTH_SOCK"] = null,
        });
        services.AddSingleton(new RemoteOptions { TransportFactory = (_, _) => new LoopbackTransport(launch), ServerMarker = marker });
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_remote_folder_opens_in_the_Explorer_and_a_file_is_edited_and_saved_through_the_editor()
    {
        Assert.SkipUnless(File.Exists(ServerAssembly), "cscode-server.dll is not next to the tests");
        using var env = new RealAppHome();
        using var serverData = new TempDirectory("cscode-app-remote-");
        var marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        var file = env.Workspace.WriteFile("src/hello.txt", "hello\n");
        await using (var run = new RealAppRun(env, s => Remote(s, serverData, marker)))
        {
            await run.MainWindowAsync();
            var sp = run.Services;
            var manager = sp.GetRequiredService<IRemoteConnectionManager>();
            var status = sp.GetRequiredService<IStatusBarService>();
            Assert.Contains(status.Items, i => i.Id == "remote.indicator" && i.CommandId == "remote.connect"); // shown while disconnected

            await manager.ConnectAsync("tester@box");
            Assert.True(await DevHost.WaitFor(() => status.Items.Any(i => i.Id == "remote.indicator" && i.Text.Contains("SSH: tester@box", StringComparison.Ordinal)), 10_000));

            var folder = manager.ToUri(env.Workspace.Path + "/")!;
            await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(folder);
            using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<INavigationService>(),
                sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<FileParticipants>());
            await explorer.RefreshRootsAsync();
            var src = explorer.Roots[0].Children!.Single(c => c.Name == "src");
            await explorer.ExpandAsync(src);
            var node = src.Children!.Single(c => c.Name == "hello.txt");
            Assert.Equal("ssh", node.Uri.Scheme);

            // open → edit → save, all through the shell's own paths
            var doc = await sp.GetRequiredService<INavigationService>().OpenAsync(node.Uri) ?? throw new InvalidOperationException("not opened");
            Assert.Equal("hello\n", doc.GetText().Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.True(doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), "from the editor\n")]));
            await sp.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Save);
            Assert.False(doc.IsDirty);
            Assert.Equal("hello\nfrom the editor\n", File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal)); // on the "remote" disk
            Assert.Empty(Directory.GetFiles(env.Workspace.Combine("src"), "*.tmp")); // the atomic save left no temp file

            // Explorer file operations go to the remote too
            var created = await explorer.CreateFileAsync(src, "added.txt");
            Assert.NotNull(created);
            Assert.True(File.Exists(env.Workspace.Combine("src", "added.txt")));

            // quick open walks the remote folder on the server
            var found = new List<Uri>();
            await foreach (var u in sp.GetRequiredService<IWorkspaceService>().FindFilesAsync(null, 0)) found.Add(u);
            Assert.Contains(node.Uri, found);
        }
        // the app's exit stopped the server (and nothing else of ours is left)
        Assert.True(await DevHost.WaitFor(() => !ProcessesWithMarker(marker), 15_000), "server processes left after the app exited");
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task With_languageHost_process_the_language_client_runs_in_a_child_and_stops_with_the_app()
    {
        Assert.SkipUnless(File.Exists(ServerAssembly), "cscode-server.dll is not next to the tests");
        using var env = new RealAppHome();
        env.WriteUserSettings("{ \"cscode.languageHost\": \"process\" }");
        var marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        await using (var run = new RealAppRun(env, s => s.AddSingleton(new RemoteOptions { ServerMarker = marker })))
        {
            await run.MainWindowAsync();
            var manager = run.Services.GetRequiredService<ILanguageServerManager>();
            var host = Assert.IsType<ProcessLanguageHost>(manager);
            await host.WaitConnectedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(ProcessesWithMarker(marker));
        }
        Assert.True(await DevHost.WaitFor(() => !ProcessesWithMarker(marker), 15_000), "server processes left after the app exited");
    }

    [Fact]
    public void Ssh_uris_on_the_command_line_become_remote_targets()
    {
        var o = CommandLineOptions.Parse(["ssh://me@box:2222/home/me/src", "--wait"], "/tmp");
        Assert.Equal(["ssh://me@box:2222/home/me/src"], o.RemoteTargets);
        Assert.Empty(o.Targets);
        Assert.Empty(o.UnknownSwitches);
    }

    private static bool ProcessesWithMarker(string marker)
    {
        if (!OperatingSystem.IsLinux()) return false;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(marker, StringComparison.Ordinal)
                    && !File.ReadAllText(Path.Combine(dir, "stat")).Contains(") Z ", StringComparison.Ordinal)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}
