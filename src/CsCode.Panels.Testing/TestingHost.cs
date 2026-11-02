using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.CTest;
using CsCode.Testing.Providers.DotNet;
using CsCode.Testing.Providers.JavaScript;
using CsCode.Testing.Providers.Rust;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Testing;

/// <summary>
/// The Test Explorer's composition point (one per window): owns the <see cref="TestController"/> and its providers, registers the
/// <c>testing.*</c> commands and settings, keeps the tree in step with the workspace's folders (and a saved file with its project), publishes
/// failures to the Error List (<see cref="IDiagnosticService"/>, owner <c>tests</c>) and tool output to the "Tests" output channel, sets the
/// context keys <c>testing.isRunning</c> / <c>testing.hasFailedTests</c>, and registers the editor's test glyph provider. Resolved at startup
/// (the tool window depends on it). Everything here runs on the UI thread except where noted.
/// <para>Discovery waits until tests are first needed (<see cref="EnsureDiscoveredAsync"/>): the Test Explorer shown, a test command run, or
/// (with <c>cscode.testing.autoDiscover</c>) an editor showing a file in which the glyph provider's text scan finds tests. Opening a folder
/// discovers nothing, so a cargo package is not built (<c>cargo test --no-run</c>) at startup for a user who never looks at its tests.</para>
/// </summary>
public sealed class TestingHost : IDisposable
{
    public const string ViewId = "testing";
    public const string OutputChannelName = "Tests";
    public const string DiagnosticOwner = "tests";
    public const string IsRunningKey = "testing.isRunning";
    public const string HasFailedKey = "testing.hasFailedTests";

    public const string FocusView = "workbench.view.testing.focus";
    public const string RunAll = "testing.runAll";
    public const string DebugAll = "testing.debugAll";
    public const string RunFailed = "testing.reRunFailTests";
    public const string DebugFailed = "testing.debugFailTests";
    public const string RunLast = "testing.reRunLastRun";
    public const string DebugLast = "testing.debugLastRun";
    public const string RunAtCursor = "testing.runAtCursor";
    public const string DebugAtCursor = "testing.debugAtCursor";
    public const string RunCurrentFile = "testing.runCurrentFile";
    public const string DebugCurrentFile = "testing.debugCurrentFile";
    public const string CancelRun = "testing.cancelRun";
    public const string Refresh = "testing.refreshTests";
    public const string ClearResults = "testing.clearTestResults";
    public const string ShowOutput = "testing.showMostRecentOutput";
    public const string CollapseAll = "testing.collapseAll";

    private readonly IServiceProvider _sp;
    private readonly DisposableStore _store = new();
    private readonly IUiDispatcher _ui;
    private readonly IConfigurationService? _config;
    private readonly IContextKeyService? _keys;
    private readonly IDiagnosticService? _diagnostics;
    private readonly IWindowService? _window;
    private readonly IOutputChannel? _channel;
    private readonly HashSet<Uri> _diagnosticUris = [];
    private readonly Dictionary<string, CancellationTokenSource> _pendingRediscovery = new(StringComparer.Ordinal);
    private Task? _discovered;
    private volatile bool _discoveryStarted;
    private bool _disposed;

    public TestingHost(IServiceProvider services) : this(services, null) { }

    /// <summary>Tests pass their own providers (scripted processes); the app gets the built-in .NET, Rust and JavaScript ones.</summary>
    public TestingHost(IServiceProvider services, IReadOnlyList<ITestProvider>? providers)
    {
        _sp = services;
        _ui = services.GetService<IUiDispatcher>() ?? AvaloniaUiDispatcher.Instance;
        _config = services.GetService<IConfigurationService>();
        _keys = services.GetService<IContextKeyService>();
        _diagnostics = services.GetService<IDiagnosticService>();
        _window = services.GetService<IWindowService>();
        _channel = services.GetService<IOutputService>()?.GetOrCreate(OutputChannelName);
        var context = new TestProviderContext(new ProcessRunner(services.GetService<Core.Diagnostics.IMemoryMonitor>()), key => _config?.Get<string>(key));
        var cmake = services.GetService<Core.Build.ICMakeService>();
        providers ??= [new DotNetTestProvider(context), new RustTestProvider(context), new JavaScriptTestProvider(context), new CTestProvider(context, cmake)];
        Controller = new TestController(providers, a => _ui.Post(a, UiPriority.Background), line => _channel?.AppendLine(line),
            id => _config?.Get<bool?>(TestingSettings.EnabledKey(id)) ?? true);
        if (services.GetService<IDebugService>() is { } debug) Controller.Debugger = new DebugLauncher(debug, _ui);
        Gutter = new TestGutterProvider(this, _ui);
        Lenses = new TestCodeLensProvider(Gutter, () => _config?.Get<bool?>(TestingSettings.CodeLens) ?? true);
        _store.Add(Lenses);

        if (_config is not null) _store.Add(_config.RegisterProperties(TestingSettings.Properties()));
        if (services.GetService<ICommandService>() is { } commands) RegisterCommands(commands);
        if (services.GetService<ILanguageFeatureRegistry>() is { } features)
        {
            var languages = DocumentSelector.ForLanguages([.. providers.SelectMany(p => p.LanguageIds).Distinct()]);
            _store.Add(features.Register<ITestGutterProvider>(languages, Gutter, "testing"));
            _store.Add(features.Register<ICodeLensProvider>(languages, Lenses, "testing")); // "Run Test | Debug Test" above each test
        }
        if (_config is not null) _store.Add(_config.Changed.Subscribe(e => { if (e.Affects(TestingSettings.CodeLens)) Lenses.Invalidate(); }));
        _store.Add(Controller.Changed.Subscribe(OnTreeChanged));
        _store.Add(Controller.Errors.Subscribe(message => { if (_window is not null) _ = _window.ShowMessageAsync(MessageSeverity.Warning, message); }));
        _store.Add(Controller.RunFinished.Subscribe(_ => OnRunFinished()));
        if (services.GetService<IWorkspaceService>() is { } workspace)
        {
            Workspace = workspace;
            _store.Add(workspace.FoldersChanged.Subscribe(e => _ui.Post(() => { if (_discovered is not null) _ = SyncFoldersAsync(); })));
        }
        if (services.GetService<IDocumentManager>() is { } documents) _store.Add(documents.Saved.Subscribe(e => _ui.Post(() => OnSaved(e.Document.Uri))));
        // a CMake project that appears or is configured again (new targets, a changed CMakeLists.txt) changes its CTest tests
        if (cmake is not null) _store.Add(cmake.ProjectsChanged.Subscribe(projects => _ui.Post(() => OnCMakeProjectsChanged(projects))));
    }

    private readonly Dictionary<string, long> _cmakeGenerations = new(StringComparer.Ordinal);

    private void OnCMakeProjectsChanged(IReadOnlyList<Core.Build.CMakeProject> projects)
    {
        if (_disposed || _discovered is null) return;
        var known = Controller.Tree.Projects.Select(r => r.Project).Where(p => p.ProviderId == CTestProvider.ProviderId).ToList();
        if (projects.Any(p => known.All(k => k.Directory != p.SourceDirectory)) || known.Any(k => projects.All(p => p.SourceDirectory != k.Directory)))
        {
            _ = SyncFoldersAsync();
            return;
        }
        foreach (var p in projects)
        {
            var before = _cmakeGenerations.GetValueOrDefault(p.SourceDirectory, -1);
            _cmakeGenerations[p.SourceDirectory] = p.Generation;
            if (before >= 0 && before != p.Generation && known.FirstOrDefault(k => k.Directory == p.SourceDirectory) is { } project) _ = Controller.RediscoverAsync(project);
        }
    }

    public TestController Controller { get; }
    public TestGutterProvider Gutter { get; }
    internal TestCodeLensProvider Lenses { get; }
    internal IWorkspaceService? Workspace { get; }
    internal IServiceProvider Services => _sp;
    internal IUiDispatcher Ui => _ui;

    /// <summary>The view-model of the open Test Explorer (null while it is hidden).</summary>
    public TestExplorerViewModel? LiveExplorer { get; private set; }

    public TestExplorerViewModel CreateExplorer()
    {
        var vm = new TestExplorerViewModel(this);
        LiveExplorer = vm;
        _ = EnsureDiscoveredAsync();
        return vm;
    }

    /// <summary>True once discovery has been asked for (any thread: the glyph provider reads it on the editor's worker).</summary>
    internal bool DiscoveryStarted => _discoveryStarted;

    /// <summary>Whether a file with tests shown in an editor may start discovery (<c>cscode.testing.autoDiscover</c>).</summary>
    internal bool DiscoverFromEditors => _config?.Get<bool?>(TestingSettings.AutoDiscover) ?? true;

    /// <summary>
    /// UI thread: discovers the workspace folders' tests the first time this is called (later calls wait for that discovery, then return at
    /// once); from then on a change of folders discovers again. Completes once the discovered tree has landed on the UI thread.
    /// </summary>
    internal async Task EnsureDiscoveredAsync()
    {
        if (_disposed || _discovered is { IsCompleted: true }) return;
        _discoveryStarted = true;
        _discovered ??= SyncFoldersAsync();
        await _discovered.ConfigureAwait(true);
        // the controller hands the tree over in background-priority posts: let them land before a command reads it
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() => landed.TrySetResult(), UiPriority.Background);
        await landed.Task.ConfigureAwait(true);
    }

    public void ReleaseExplorer(TestExplorerViewModel vm)
    {
        if (ReferenceEquals(LiveExplorer, vm)) LiveExplorer = null;
        vm.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ workspace

    private async Task SyncFoldersAsync()
    {
        if (_disposed || Workspace is null) return;
        var folders = Workspace.Folders.Where(f => f.IsFile).Select(f => f.LocalPath.TrimEnd('/', '\\') is { Length: > 0 } p ? p : f.LocalPath).ToList();
        await Controller.SetFoldersAsync(folders).ConfigureAwait(true);
    }

    /// <summary>A saved C# or JavaScript file rescans its project (cheap: a text scan); Rust needs a build, so only Refresh does that.</summary>
    private void OnSaved(Uri uri)
    {
        if (_disposed || !uri.IsFile) return;
        var path = uri.LocalPath;
        foreach (var root in Controller.Tree.Projects)
        {
            var project = root.Project;
            if (project.ProviderId == RustTestProvider.ProviderId || !StackTraces.IsUnder(path, project.Directory)) continue;
            var relevant = project.ProviderId == DotNetTestProvider.ProviderId ? path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) : JavaScriptTestProvider.IsTestFile(path);
            if (!relevant) continue;
            if (_pendingRediscovery.Remove(project.Key, out var old)) { old.Cancel(); old.Dispose(); }
            var cts = new CancellationTokenSource();
            _pendingRediscovery[project.Key] = cts;
            _ = Task.Delay(500, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) _ui.Post(() =>
                {
                    if (_pendingRediscovery.TryGetValue(project.Key, out var mine) && ReferenceEquals(mine, cts)) { _pendingRediscovery.Remove(project.Key); cts.Dispose(); }
                    _ = Controller.RediscoverAsync(project);
                });
            }, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------------------------------------ results: Error List, context keys, glyphs

    private void OnTreeChanged(TestTreeChange change)
    {
        if (_disposed) return;
        _keys?.Set(IsRunningKey, Controller.IsRunning);
        _ui.PostCoalesced("testing.results", PublishResults, UiPriority.Background);
        Gutter.Invalidate();
    }

    private void OnRunFinished()
    {
        _keys?.Set(IsRunningKey, false);
        PublishResults();
        if (Controller.Summary().Failed > 0 && OpenMode() == "openOnTestFailure") _ = ShowViewAsync();
    }

    internal string OpenMode() => _config?.Get(TestingSettings.OpenTesting, "openOnTestStart") ?? "openOnTestStart";

    /// <summary>Failed tests as Error List entries at the failure's place (else the test's), and the <c>testing.hasFailedTests</c> key.</summary>
    internal void PublishResults()
    {
        if (_disposed) return;
        var byUri = new Dictionary<Uri, List<Diagnostic>>();
        var failed = 0;
        foreach (var t in Controller.Tree.AllTests())
        {
            if (t.Outcome != TestOutcome.Failed) continue;
            failed++;
            if ((t.FailureLocation ?? t.Location) is not { } loc || !Path.IsPathRooted(loc.FilePath)) continue;
            var uri = new Uri(loc.FilePath);
            if (!byUri.TryGetValue(uri, out var list)) byUri[uri] = list = [];
            if (list.Count >= 500) continue;
            var message = $"Test failed: {t.TestId}" + (t.Message is { Length: > 0 } m ? " — " + FirstLine(m) : "");
            list.Add(new Diagnostic(new TextRange(new TextPosition(loc.Line, 0), new TextPosition(loc.Line, 0)), message, DiagnosticSeverity.Error, Source: "Test Explorer"));
        }
        _keys?.Set(HasFailedKey, failed > 0);
        if (_diagnostics is null) return;
        foreach (var gone in _diagnosticUris.Where(u => !byUri.ContainsKey(u)).ToList())
        {
            _diagnostics.Clear(gone, DiagnosticOwner);
            _diagnosticUris.Remove(gone);
        }
        foreach (var (uri, list) in byUri)
        {
            _diagnostics.Set(uri, DiagnosticOwner, list);
            _diagnosticUris.Add(uri);
        }
    }

    private static string FirstLine(string s)
    {
        var nl = s.IndexOf('\n');
        var line = (nl >= 0 ? s[..nl] : s).Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    // ------------------------------------------------------------------------------------------------ commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, Func<CommandInvocation, ValueTask<object?>> handler)
        {
            if (commands.Has(id)) return;
            _store.Add(commands.Register(id, handler, new CommandMetadata(id, title, "Test")));
        }
        ValueTask<object?> Run(Func<Task<bool>> start) => new(StartAndShow(start));

        Reg(FocusView, "Focus on Test Explorer View", async _ => { await ShowViewAsync(); return null; });
        Reg(RunAll, "Run All Tests", _ => Run(() => Controller.RunAllAsync()));
        Reg(DebugAll, "Debug All Tests", _ => Run(() => Controller.RunAllAsync(debug: true)));
        Reg(RunFailed, "Rerun Failed Tests", _ => Run(() => Controller.RunFailedAsync()));
        Reg(DebugFailed, "Debug Failed Tests", _ => Run(() => Controller.RunFailedAsync(debug: true)));
        Reg(RunLast, "Rerun Last Run", _ => Run(() => Controller.RunLastAsync(debug: false)));
        Reg(DebugLast, "Debug Last Run", _ => Run(() => Controller.RunLastAsync(debug: true)));
        Reg(RunAtCursor, "Run Test at Cursor", _ => Run(() => RunAtCursorAsync(debug: false)));
        Reg(DebugAtCursor, "Debug Test at Cursor", _ => Run(() => RunAtCursorAsync(debug: true)));
        Reg(RunCurrentFile, "Run Tests in Current File", _ => Run(() => RunCurrentFileAsync(debug: false)));
        Reg(DebugCurrentFile, "Debug Tests in Current File", _ => Run(() => RunCurrentFileAsync(debug: true)));
        Reg(CancelRun, "Cancel Test Run", _ => { Controller.Cancel(); return ValueTask.FromResult<object?>(null); });
        Reg(Refresh, "Refresh Tests", _ => new ValueTask<object?>(RefreshAsync()));
        Reg(ClearResults, "Clear All Results", _ => { Controller.ClearResults(); return ValueTask.FromResult<object?>(null); });
        Reg(ShowOutput, "Show Output", async _ =>
        {
            if (_sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync("output");
            if (_sp.GetService<ICommandService>() is { } c && c.Has("workbench.action.output.show")) await c.ExecuteAsync("workbench.action.output.show", OutputChannelName);
            return null;
        });
        Reg(TestCodeLensProvider.RunCommand, "Run Test (CodeLens)", inv => TestCodeLensProvider.RunAsync(Gutter, inv, debug: false));
        Reg(TestCodeLensProvider.DebugCommand, "Debug Test (CodeLens)", inv => TestCodeLensProvider.RunAsync(Gutter, inv, debug: true));
        Reg(CollapseAll, "Collapse All Tests", _ => { LiveExplorer?.CollapseAll(); return ValueTask.FromResult<object?>(null); });
    }

    /// <summary>Refresh: the first discovery when there has been none, else a new one.</summary>
    private async Task<object?> RefreshAsync()
    {
        if (_discovered is null) await EnsureDiscoveredAsync().ConfigureAwait(true);
        else await Controller.RefreshAsync().ConfigureAwait(true);
        return null;
    }

    private async Task<object?> StartAndShow(Func<Task<bool>> start)
    {
        await EnsureDiscoveredAsync().ConfigureAwait(true); // a test command before anything was discovered
        var mode = OpenMode();
        var run = start();
        if (mode is "openOnTestStart" or "openExplorerOnTestStart" && Controller.IsRunning) await ShowViewAsync().ConfigureAwait(true);
        await run.ConfigureAwait(true);
        return null;
    }

    /// <summary>Runs <paramref name="nodes"/>, showing the explorer as <c>testing.openTesting</c> says (the gutter and the explorer's own buttons).</summary>
    internal Task RunNodesAsync(IReadOnlyList<TestNode> nodes, bool debug) => StartAndShow(() => Controller.RunAsync(nodes, debug));

    internal async Task ShowViewAsync()
    {
        if (_sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync(ViewId).ConfigureAwait(true);
    }

    /// <summary>The test declared at or above the caret in the active editor (the nearest one), else every test of the file.</summary>
    private Task<bool> RunAtCursorAsync(bool debug)
    {
        if (_sp.GetService<INavigationService>() is not { ActiveDocument: { } doc } nav) return Task.FromResult(false);
        var tests = Gutter.TestsIn(doc);
        if (tests.Count == 0) return NothingToRun("No test was found in the current file.");
        var line = nav.ActiveCaret?.Line ?? 0;
        var at = tests.Where(t => t.Line <= line).OrderByDescending(t => t.Line).FirstOrDefault();
        var nodes = at is null ? tests.Select(t => t.Node).ToList() : [at.Node];
        return Controller.RunAsync(nodes, debug);
    }

    private Task<bool> RunCurrentFileAsync(bool debug)
    {
        if (_sp.GetService<INavigationService>() is not { ActiveDocument: { } doc }) return Task.FromResult(false);
        var tests = Gutter.TestsIn(doc);
        return tests.Count == 0 ? NothingToRun("No test was found in the current file.") : Controller.RunAsync([.. tests.Select(t => t.Node)], debug);
    }

    private Task<bool> NothingToRun(string message)
    {
        if (_window is not null) _ = _window.ShowMessageAsync(MessageSeverity.Info, message + (Controller.IsDiscovering ? " Tests are still being discovered." : ""));
        return Task.FromResult(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _pendingRediscovery.Values) { cts.Cancel(); cts.Dispose(); }
        _pendingRediscovery.Clear();
        _store.Dispose();
        Controller.Dispose();
        Gutter.Dispose();
        if (_diagnostics is not null) _diagnostics.Clear(DiagnosticOwner);
    }

    /// <summary>Starts debug sessions on the UI thread through <see cref="IDebugService"/>.</summary>
    private sealed class DebugLauncher(IDebugService debug, IUiDispatcher ui) : ITestDebugLauncher
    {
        public async ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct)
        {
            var start = await ui.InvokeAsync(() => debug.StartAsync(configuration, ct).AsTask()).ConfigureAwait(false);
            return await start.ConfigureAwait(false);
        }
    }
}
