using CsCode.Core;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.Keybindings;
using CsCode.Platform.Logging;
using CsCode.Platform.State;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public sealed class DiFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-di-");
    public AppPaths Paths { get; }
    public ServiceCollection Services { get; } = new();
    private readonly Lazy<ServiceProvider> _provider;

    public DiFixture()
    {
        // The real AppPaths reads CSCODE_HOME from the process environment; tests must never touch the user's directories or mutate the
        // environment (other tests run in parallel), so a pre-registered IAppPaths wins (the module uses TryAdd).
        Paths = new AppPaths(k => k == "CSCODE_HOME" ? Temp.Combine("home") : null, HostOs.Linux, "/home/test");
        Services.AddSingleton<IAppPaths>(Paths);
        Services.AddSingleton(Paths);
        // Thread-safe on purpose: a test resolves from many threads at once, and two racing first accesses must not build two containers.
        _provider = new Lazy<ServiceProvider>(() => Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true }),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ServiceProvider Provider => _provider.Value;

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public void Dispose() { if (_provider.IsValueCreated) _provider.Value.Dispose(); Temp.Dispose(); }
}

/// <summary>Runs alone: the shutdown test counts process-wide inotify fds, which any parallel test using a real watcher would disturb.</summary>
[Collection("Watchers")]
public class DependencyInjectionTests : IDisposable
{
    private readonly DiFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_contract_the_module_implements_resolves_without_a_dependency_cycle()
    {
        _f.Services.AddCsCodePlatform();
        Assert.NotNull(_f.Get<ICommandService>());
        Assert.NotNull(_f.Get<IContextKeyService>());
        Assert.NotNull(_f.Get<IConfigurationService>());
        Assert.NotNull(_f.Get<IKeybindingService>());
        Assert.NotNull(_f.Get<IWorkspaceService>());
        Assert.NotNull(_f.Get<IFileSystem>());
        Assert.NotNull(_f.Get<IFileWatcher>());
        Assert.NotNull(_f.Get<IStateStorage>());
        Assert.NotNull(_f.Get<IAppPaths>());
        Assert.NotNull(_f.Get<IEditorConfigService>());
        Assert.NotNull(_f.Get<ILoggerFactory>());
        Assert.NotNull(_f.Get<ILogger<WorkspaceService>>());
    }

    [Fact]
    public void Resolving_in_any_order_and_from_many_threads_at_once_works()
    {
        _f.Services.AddCsCodePlatform();
        Func<object>[] getters =
        [
            () => _f.Get<IWorkspaceService>(), () => _f.Get<IConfigurationService>(), () => _f.Get<IFileWatcher>(), () => _f.Get<IKeybindingService>(),
            () => _f.Get<IContextKeyService>(), () => _f.Get<IStateStorage>(), () => _f.Get<IEditorConfigService>(), () => _f.Get<ICommandService>(), () => _f.Get<IFileSystem>(),
        ];
        var results = new object[getters.Length * 4];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct }, i => results[i] = getters[i % getters.Length]());
        // singletons: every thread got the same instance for the same contract
        for (var g = 0; g < getters.Length; g++)
            Assert.Single(results.Where((_, i) => i % getters.Length == g).Distinct());
    }

    [Fact]
    public void Each_service_is_a_singleton_shared_between_its_interface_and_its_concrete_type()
    {
        _f.Services.AddCsCodePlatform();
        Assert.Same(_f.Get<ICommandService>(), _f.Get<ICommandService>());
        Assert.Same(_f.Get<ICommandService>(), _f.Get<CsCode.Platform.Commands.CommandService>());
        Assert.Same(_f.Get<IConfigurationService>(), _f.Get<ConfigurationService>());
        Assert.Same(_f.Get<IKeybindingService>(), _f.Get<KeybindingService>());
        Assert.Same(_f.Get<IWorkspaceService>(), _f.Get<WorkspaceService>());
        Assert.Same(_f.Get<IStateStorage>(), _f.Get<StateStorage>());
        Assert.Same(_f.Get<IContextKeyService>(), _f.Get<CsCode.Platform.Context.ContextKeyService>());
        Assert.Same(_f.Get<IFileWatcher>(), _f.Get<CsCode.Platform.Watching.FileWatcher>());
        // and the services really share the infrastructure
        Assert.Same(_f.Get<IFileSystem>(), _f.Get<IWorkspaceService>().FileSystem);
        Assert.Same(_f.Get<IFileWatcher>(), _f.Get<IWorkspaceService>().Watcher);
    }

    [Fact]
    public void Calling_AddCsCodePlatform_twice_does_not_duplicate_registrations()
    {
        _f.Services.AddCsCodePlatform();
        _f.Services.AddCsCodePlatform(o => o.IoThreads = 99);
        Assert.Equal(3, _f.Provider.GetServices<IWorkspaceBound>().Count());
        Assert.Single(_f.Provider.GetServices<ILoggerProvider>().OfType<FileLoggerProvider>());
        Assert.NotEqual(99, _f.Get<PlatformOptions>().IoThreads);       // the second call was ignored entirely
    }

    [Fact]
    public void Options_can_be_tuned_and_registered_modules_win_over_the_defaults()
    {
        var custom = new LocalFileSystem(new IoLane("custom", 1));
        _f.Services.AddSingleton<IFileSystem>(custom);                   // an earlier registration (e.g. a routing file system) is respected
        _f.Services.AddCsCodePlatform(o => { o.WatchDebounce = TimeSpan.FromMilliseconds(5); o.IoThreads = 2; });
        Assert.Same(custom, _f.Get<IFileSystem>());
        Assert.Equal(TimeSpan.FromMilliseconds(5), _f.Get<PlatformOptions>().WatchDebounce);
        Assert.Same(_f.Get<PlatformOptions>(), _f.Get<PlatformOptions>());
    }

    [Fact]
    public void Core_and_Platform_compose_and_the_keybinding_service_picks_up_the_contribution_registry()
    {
        _f.Services.AddCsCodeCore();
        _f.Services.AddCsCodePlatform();
        var registry = _f.Get<IContributionRegistry>();
        var keys = _f.Get<IKeybindingService>();
        using var reg = registry.Contribute(ContributionSources.Builtin, "", [new KeybindingContribution("my.contrib", "ctrl+9")]);
        Assert.Equal("my.contrib", keys.Resolve(KeyStrokeParser.ParseStroke("ctrl+9")).Binding!.Command);
    }

    [Fact]
    public void Without_Core_the_keybinding_service_still_works_it_just_has_no_contributions()
    {
        _f.Services.AddCsCodePlatform();
        Assert.Equal("workbench.action.files.save", _f.Get<IKeybindingService>().Resolve(KeyStrokeParser.ParseStroke("ctrl+s")).Binding!.Command);
    }

    // ---- end to end through the container ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_session_through_the_container()
    {
        _f.Services.AddCsCodeCore();
        _f.Services.AddCsCodePlatform();
        var workspace = _f.Get<IWorkspaceService>();
        var config = _f.Get<IConfigurationService>();
        var ctx = _f.Get<IContextKeyService>();
        var commands = _f.Get<ICommandService>();
        var keys = _f.Get<IKeybindingService>();
        var fs = _f.Get<IFileSystem>();

        var root = _f.Temp.Combine("project");
        Directory.CreateDirectory(Path.Combine(root, ".cscode"));
        File.WriteAllText(Path.Combine(root, ".cscode", "settings.json"), """{ "editor.tabSize": 2 }""");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(root, "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(root, "x.log"), "noise");

        Assert.False(ctx.Evaluate("config.editor.tabSize == 2"));
        await workspace.OpenFolderAsync(FileUri.FromDirectoryPath(root), Ct);

        // workspace settings reach config reads and when-clauses
        Assert.Equal(2, config.Get<int>(SettingKeys.EditorTabSize));
        Assert.True(ctx.Evaluate("config.editor.tabSize == 2"));

        // a command, bound to a key by the user, executed through the keybinding pipeline
        var ran = new TaskCompletionSource<string>();
        commands.Register("test.hello", inv => { ran.TrySetResult((string)inv.Args[0]!); return ValueTask.FromResult<object?>("done"); });
        await config.UpdateAsync("cscode.keymap", "visualstudio", ct: Ct);
        File.WriteAllText(((KeybindingService)keys).UserKeybindingsPath, """[ { "key": "ctrl+9", "command": "test.hello", "args": ["hi"] } ]""");
        await ((KeybindingService)keys).ReloadAsync(Ct);
        var hit = keys.Resolve(KeyStrokeParser.ParseStroke("ctrl+9"));
        Assert.Equal(KeyResolution.Matched, hit.Kind);
        Assert.Equal("done", await commands.ExecuteAsync(hit.Binding!.Command, hit.Binding.Args!.Value[0].GetString()));
        Assert.Equal("hi", await ran.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        // file system + search + state
        var found = new List<string>();
        await foreach (var u in workspace.FindFilesAsync(null, 0, Ct)) found.Add(Path.GetFileName(FileUri.ToPath(u)));
        Assert.Equal(["a.cs"], found.Where(n => n is "a.cs" or "x.log").ToArray());   // x.log is gitignored
        await fs.WriteFileAsync(FileUri.FromPath(Path.Combine(root, "b.txt")), "x"u8.ToArray(), ct: Ct);
        Assert.True(File.Exists(Path.Combine(root, "b.txt")));
        _f.Get<IStateStorage>().Set("session", "alive");
        await _f.Get<IStateStorage>().FlushAsync();
        Assert.True(File.Exists(Path.Combine(_f.Paths.GlobalStorageDir, "state.json")));
    }

    // ---- shutdown ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_the_container_flushes_state_and_logs_releases_watches_and_stops_the_lane()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        _f.Services.AddCsCodePlatform();
        var inotifyBefore = WatchFixture.InotifyFds();

        var state = _f.Get<IStateStorage>();
        var workspace = _f.Get<IWorkspaceService>();
        var log = _f.Get<ILoggerFactory>().CreateLogger("shutdown.test");
        var root = _f.Temp.Combine("project");
        Directory.CreateDirectory(root);
        await workspace.OpenFolderAsync(FileUri.FromDirectoryPath(root), Ct);   // arms settings/editorconfig watches
        state.Set("pending", "must-survive");                                     // not yet flushed (debounced)
        log.LogWarning("last words");
        var lane = _f.Get<IoLane>();

        _f.Provider.Dispose();

        Assert.Contains("must-survive", File.ReadAllText(Path.Combine(_f.Paths.GlobalStorageDir, "state.json")));
        Assert.Contains("last words", File.ReadAllText(Path.Combine(_f.Paths.LogsDir, "cscode.log")));
        Assert.Throws<ObjectDisposedException>(() => { _ = lane.RunAsync(_ => 1, Ct); });   // the lane was shut down (and last)
        Assert.True(await WatchFixture.SettledInotifyFds(inotifyBefore, Ct) <= inotifyBefore, "inotify handles leaked at shutdown");
    }

    [Fact]
    public void A_provider_that_is_built_and_disposed_without_resolving_anything_is_fine()
    {
        _f.Services.AddCsCodePlatform();
        _f.Provider.Dispose();
    }

    [Fact]
    public void Repeated_container_lifecycles_do_not_leak_threads_or_throw()
    {
        for (var i = 0; i < 5; i++)
        {
            using var f = new DiFixture();
            f.Services.AddCsCodePlatform();
            f.Get<IWorkspaceService>();
            f.Get<IKeybindingService>();
            f.Provider.Dispose();
        }
    }

    [Fact]
    public async Task The_logger_writes_to_the_logs_directory_through_the_provider()
    {
        _f.Services.AddCsCodePlatform();
        var logger = _f.Get<ILoggerFactory>().CreateLogger("Di.Test");
        logger.LogInformation("hello from di");
        await _f.Provider.GetServices<ILoggerProvider>().OfType<FileLoggerProvider>().Single().FlushAsync();
        Assert.Contains("hello from di", File.ReadAllText(Path.Combine(_f.Paths.LogsDir, "cscode.log")));
    }

    [Fact]
    public void The_legacy_stub_signature_still_works()
    {
        var services = new ServiceCollection();
        Assert.Same(services, ServiceCollectionExtensions.AddCsCodePlatform(services));
    }
}
