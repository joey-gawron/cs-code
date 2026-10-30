using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
            // the indicator is drawn as a badge in the theme's remote colours (VS 2022 Dark: white on #16825D)
            var window = await run.MainWindowAsync();
            Button? badge = null;
            Assert.True(await DevHost.WaitFor(() => (badge = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("badge"))) is not null, 10_000), "no badge in the status bar");
            var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(badge!.Background);
            Assert.Equal(Avalonia.Media.Color.Parse("#16825D"), brush.Color);
            Assert.Same(CsCode.Ui.Theming.ThemeBrushes.Get("statusBarItem.remoteBackground"), badge.Background);

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
    public async Task A_remote_document_changed_on_the_server_reloads_and_its_tab_shows_the_real_file_name()
    {
        // T12: the editor watched file: documents only (a remote file changed on the server was never offered for reload), and tab titles of
        // ssh: documents showed the escaped path ("my%20notes.txt").
        Assert.SkipUnless(File.Exists(ServerAssembly), "cscode-server.dll is not next to the tests");
        using var env = new RealAppHome();
        using var serverData = new TempDirectory("cscode-app-remote-");
        var marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        var file = env.Workspace.WriteFile("my notes.txt", "first\n");
        await using (var run = new RealAppRun(env, s => Remote(s, serverData, marker)))
        {
            await run.MainWindowAsync();
            var sp = run.Services;
            var manager = sp.GetRequiredService<IRemoteConnectionManager>();
            await manager.ConnectAsync("tester@box");
            await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(manager.ToUri(env.Workspace.Path + "/")!);
            var uri = manager.ToUri(file)!;
            Assert.Contains("my%20notes.txt", uri.AbsoluteUri, StringComparison.Ordinal);

            var tab = await sp.GetRequiredService<DocumentWell>().OpenAsync(uri);
            Assert.Equal("my notes.txt", tab.Title);
            var doc = tab.Doc;
            Assert.Equal("first\n", doc.GetText().Replace("\r\n", "\n", StringComparison.Ordinal));

            // changed on the "remote" disk (the server's machine): the clean document reloads, as a local one does
            await Task.Delay(300); // the server's watch is armed when the document opened; give the first event a fresh mtime
            File.WriteAllText(file, "second, from the server\n");
            Assert.True(await DevHost.WaitFor(() => doc.GetText().Replace("\r\n", "\n", StringComparison.Ordinal) == "second, from the server\n", 20_000),
                "the remote document was not reloaded: " + doc.GetText());
            Assert.False(doc.IsDirty);
        }
        Assert.True(await DevHost.WaitFor(() => !ProcessesWithMarker(marker), 15_000), "server processes left after the app exited");
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task In_the_real_app_a_remote_workspace_terminal_runs_on_the_server()
    {
        // R3 in the real composition: the Terminal module asks the Remote module's provider; a remote folder's terminal is the server's child.
        Assert.SkipUnless(File.Exists(ServerAssembly), "cscode-server.dll is not next to the tests");
        using var env = new RealAppHome();
        using var serverData = new TempDirectory("cscode-app-remote-");
        var marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        await using (var run = new RealAppRun(env, s => Remote(s, serverData, marker)))
        {
            await run.MainWindowAsync();
            var sp = run.Services;
            var manager = sp.GetRequiredService<IRemoteConnectionManager>();
            await manager.ConnectAsync("tester@box");
            await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(manager.ToUri(env.Workspace.Path + "/")!);
            var terminals = sp.GetRequiredService<CsCode.Core.Terminal.ITerminalService>();
            var t = await terminals.CreateAsync(new CsCode.Core.Terminal.TerminalCreateOptions
            {
                ShellPath = "/bin/bash", ShellArgs = ["--noprofile", "--norc"], Show = false,
                Env = new Dictionary<string, string?> { ["HISTFILE"] = "/dev/null" },
            });
            var server = ((CsCode.Remote.Client.RemoteConnectionManager)manager).Session!.Server.ProcessId;
            Assert.True(IsDescendant(t.ProcessId!.Value, server), $"terminal pid {t.ProcessId} is not under the server {server}");
            Assert.Equal(env.Workspace.Path, t.CurrentDirectory);
            await terminals.KillAsync(t);
        }
        Assert.True(await DevHost.WaitFor(() => !ProcessesWithMarker(marker), 15_000), "server processes left after the app exited");
    }

    private static bool IsDescendant(int pid, int ancestor)
    {
        for (var p = pid; p > 1;)
        {
            if (p == ancestor) return true;
            try
            {
                var stat = File.ReadAllText($"/proc/{p}/stat");
                p = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException) { return false; }
        }
        return false;
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

    [Fact]
    public void The_app_binary_answers_askpass_without_starting_the_gui()
    {
        var app = Path.Combine(AppContext.BaseDirectory, "cscode.dll");
        Assert.SkipUnless(File.Exists(app), "cscode.dll is not next to the tests");
        var psi = new System.Diagnostics.ProcessStartInfo(LocalServer.FindDotnetHost()) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { app, "--askpass", "Enter passphrase for key '/k':" }) psi.ArgumentList.Add(a);
        psi.Environment.Remove(CsCode.Remote.Askpass.AskpassServer.SocketVariable);
        psi.Environment.Remove(CsCode.Remote.Askpass.AskpassServer.TokenVariable);
        psi.Environment.Remove("DISPLAY");
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        Assert.Equal(1, p.ExitCode); // nobody to ask: ssh then treats it as a refusal
        Assert.Contains("not started by CsCode", stderr);
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
