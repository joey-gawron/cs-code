using CsCode.Core;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp;
using CsCode.Platform;
using CsCode.Remote.Bridge;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary><c>cscode.languageHost = process</c>: the real composition with the language client moved into a local cscode-server child process.</summary>
public class ProcessLanguageHostTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    private sealed class Host : IAsyncDisposable
    {
        private readonly TempDirectory _home = new("cscode-plh-client-");
        private readonly TempDirectory _data = new("cscode-plh-server-");
        public readonly TempDirectory Workspace = new("cscode-plh-ws-");
        public readonly string Marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        public readonly ServiceProvider Services;

        public Host(string mode)
        {
            var launchEnv = TestEnv.ServerLaunch(_data.Path, Marker).Environment;
            var sc = new ServiceCollection();
            sc.AddLogging();
            sc.AddCsCodeCore();
            sc.AddSingleton(new AppPaths(name => name == AppPaths.HomeEnvironmentVariable ? _home.Path : Environment.GetEnvironmentVariable(name)));
            sc.AddSingleton<IAppPaths>(sp => sp.GetRequiredService<AppPaths>());
            sc.AddCsCodePlatform();
            sc.AddCsCodeLsp();
            sc.AddSingleton<IWindowService>(new RecordingWindowService());
            sc.AddSingleton<IOutputService>(new InMemoryOutputService());
            sc.AddSingleton<ICommandService, TestCommandService>();
            sc.AddSingleton(new RemoteOptions
            {
                LanguageHostMode = mode, LocalServerAssembly = TestEnv.ServerAssembly, LocalServerDataDir = _data.Path, ServerMarker = Marker,
                LocalServerEnvironment = launchEnv,
            });
            sc.AddCsCodeRemote();
            Services = sc.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await TestEnv.AssertNoProcessesAsync(Marker);
            Workspace.Dispose();
            _data.Dispose();
            _home.Dispose();
        }
    }

    [Fact]
    public async Task In_process_mode_the_shell_gets_the_proxy_and_the_in_process_client_is_never_built()
    {
        await using var host = new Host("process");
        var manager = host.Services.GetRequiredService<ILanguageServerManager>();
        var plh = Assert.IsType<ProcessLanguageHost>(manager);
        Assert.Contains(host.Services.GetServices<IWillSaveParticipant>(), p => ReferenceEquals(p, plh));
        Assert.DoesNotContain(host.Services.GetServices<IWillSaveParticipant>(), p => p.GetType().Name == "LanguageServerManager");
        Assert.DoesNotContain(host.Services.GetServices<IFileOperationParticipant>(), p => p.GetType().Name == "LanguageServerManager");
        await plh.WaitConnectedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.NotEmpty(TestEnv.ProcessesWithMarker(host.Marker)); // the child runs
    }

    [Fact]
    public async Task In_process_mode_off_the_original_manager_is_used()
    {
        await using var host = new Host("inProcess");
        var manager = host.Services.GetRequiredService<ILanguageServerManager>();
        Assert.Equal("LanguageServerManager", manager.GetType().Name);
        Assert.Empty(TestEnv.ProcessesWithMarker(host.Marker));
    }

    [Fact]
    public async Task TypeScript_runs_in_the_child_and_its_features_reach_local_documents()
    {
        if (!LanguageBridgeTests.RequireTypeScript()) return;
        await using var host = new Host("process");
        LanguageBridgeTests.WriteProject(host.Workspace);
        var plh = (ProcessLanguageHost)host.Services.GetRequiredService<ILanguageServerManager>();
        var changes = 0;
        using var sub = plh.ServersChanged.Subscribe(_ => Interlocked.Increment(ref changes));
        await host.Services.GetRequiredService<IWorkspaceService>().OpenFolderAsync(host.Workspace.Uri, Ct);
        await plh.WaitConnectedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var documents = host.Services.GetRequiredService<IDocumentManager>();
        var main = new InMemoryTextDocument(new Uri(host.Workspace.Combine("src", "main.ts")), "typescript", LanguageBridgeTests.MainTs);
        using var reg = documents.Register(main);
        var diagnostics = host.Services.GetRequiredService<IDiagnosticService>();
        await TestEnv.WaitUntilAsync(() => diagnostics.Get(main.Uri).Any(d => d.Code == "2322"), TimeSpan.FromSeconds(120), "TS2322 from the language host process");

        // the server list is mirrored (status bar) and the typescript server is a child of the language host, not of this process
        await TestEnv.WaitUntilAsync(() => plh.Servers.Any(s => s.Id == "typescript" && s.State == LanguageServerState.Running), TimeSpan.FromSeconds(30), "server list");
        Assert.True(changes > 0);
        var tsPid = plh.Servers.First(s => s.Id == "typescript").ProcessId!.Value;
        Assert.NotEqual(Environment.ProcessId, ParentOf(tsPid));
        Assert.Contains(plh.Client.Session!.Server.ProcessId, Ancestors(tsPid));

        var completion = host.Services.GetRequiredService<ILanguageFeatureRegistry>().GetProviders<ICompletionProvider>(main);
        Assert.NotEmpty(completion);
        var tree = TestEnv.Descendants(plh.Client.Session!.Server.ProcessId);
        Assert.Contains(tsPid, tree);
        await plh.StopAllAsync(Ct);
        await TestEnv.WaitUntilAsync(() => plh.Servers.All(s => s.State is LanguageServerState.Stopped), TimeSpan.FromSeconds(30), "stop all through the proxy");
        await TestEnv.WaitUntilAsync(() => !TestEnv.IsAlive(tsPid), TimeSpan.FromSeconds(20), "the stopped server's process to exit");
    }

    private static int ParentOf(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<int> Ancestors(int pid)
    {
        for (var p = pid; p > 1;)
        {
            try { p = ParentOf(p); } catch (IOException) { yield break; }
            yield return p;
        }
    }
}
