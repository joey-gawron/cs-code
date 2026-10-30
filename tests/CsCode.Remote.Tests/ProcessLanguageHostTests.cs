using CsCode.Core;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp;
using CsCode.Platform;
using CsCode.Remote.Bridge;
using CsCode.Remote.Transport;
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

        public Host(string mode, string? serverAssembly = null)
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
                LanguageHostMode = mode, LocalServerAssembly = serverAssembly ?? TestEnv.ServerAssembly, LocalServerDataDir = _data.Path, ServerMarker = Marker,
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
    public async Task A_missing_server_build_degrades_to_no_language_features_instead_of_failing_startup()
    {
        await using var host = new Host("process", serverAssembly: "/nonexistent/cscode-server.dll");
        var plh = Assert.IsType<ProcessLanguageHost>(host.Services.GetRequiredService<ILanguageServerManager>()); // resolving never throws
        await TestEnv.WaitUntilAsync(() => plh.Client.Status.State == Core.Remote.RemoteConnectionState.Failed, TimeSpan.FromSeconds(10), "failed status");
        Assert.Contains("cscode-server.dll was not found", plh.Client.Status.Message);
        Assert.Empty(plh.Servers);
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
    public async Task Resolved_in_in_process_mode_the_process_host_starts_no_child_and_disposes_without_blocking()
    {
        // T29: a composition that resolves every registration (the "resolve everything" tests, a container walk) reaches the process host too
        await using var host = new Host("inProcess");
        var plh = host.Services.GetRequiredService<ProcessLanguageHost>();
        Assert.False(plh.IsStarted);
        await Task.Delay(1500, Ct); // a started host would have its child by now
        Assert.Empty(TestEnv.ProcessesWithMarker(host.Marker));
        Assert.Equal(Core.Remote.RemoteConnectionState.Disconnected, plh.Client.Status.State);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        plh.Dispose(); // the container's synchronous disposal: nothing to stop, nothing to wait for
        Assert.True(sw.ElapsedMilliseconds < 100, $"Dispose blocked for {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_self_contained_server_build_runs_through_its_own_executable_and_a_framework_dependent_one_through_dotnet()
    {
        // T47: in an installed (self-contained) app there may be no dotnet at all; the server next to it is self-contained too
        using var dir = new TempDirectory("cscode-plh-layout-");
        var dll = Path.Combine(dir.Path, LocalServer.AssemblyFile);
        File.WriteAllText(dll, "");
        var apphost = Path.Combine(dir.Path, OperatingSystem.IsWindows() ? "cscode-server.exe" : "cscode-server");
        File.WriteAllText(apphost, "");
        var config = Path.Combine(dir.Path, "cscode-server.runtimeconfig.json");

        File.WriteAllText(config, """{ "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.12" } ] } }""");
        var selfContained = LocalServer.Launch(dll, "/data", "m");
        Assert.Equal(apphost, selfContained.FileName);
        Assert.Equal(["--stdio", "--data-dir", "/data", "--marker", "m"], selfContained.Arguments);

        File.WriteAllText(config, """{ "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" } } }""");
        var frameworkDependent = LocalServer.Launch(dll, "/data", "m");
        Assert.Equal(LocalServer.FindDotnetHost(), frameworkDependent.FileName);
        Assert.Equal([dll, "--stdio", "--data-dir", "/data", "--marker", "m"], frameworkDependent.Arguments);

        // the build next to the tests is framework-dependent: it keeps running through dotnet
        Assert.Null(LocalServer.SelfContainedHost(TestEnv.ServerAssembly));
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
