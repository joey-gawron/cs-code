using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Protocol;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests.Harness;

/// <summary>Workspace double: a mutable folder list with change events and a controllable file watcher.</summary>
public sealed class FakeWorkspace : IWorkspaceService, IFileWatcher
{
    private readonly List<Uri> _folders = new();
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    private readonly List<(Uri Root, Action<IReadOnlyList<FileChange>> Callback)> _watches = new();

    public IReadOnlyList<Uri> Folders { get { lock (_folders) return _folders.ToArray(); } }
    public string? Name => "test";
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public IFileSystem FileSystem => throw new NotSupportedException();
    public IFileWatcher Watcher => this;
    public IReadOnlyList<Uri> RecentFolders => [];
    public int ActiveWatches { get { lock (_watches) return _watches.Count; } }
    public List<(Uri Root, IReadOnlyList<string>? Exclude)> WatchRequests { get; } = new();
    /// <summary>The <see cref="FileWatchWants"/> each watch was given (null: none), in the order of <see cref="WatchRequests"/>.</summary>
    public List<FileWatchWants?> WatchWants { get; } = new();

    public void AddFolder(string path)
    {
        var uri = new Uri(Path.TrimEndingDirectorySeparator(path) + "/");
        lock (_folders) _folders.Add(uri);
        _changed.Fire(new WorkspaceFoldersChangedEventArgs([uri], []));
    }

    public void RemoveFolder(Uri uri)
    {
        lock (_folders) _folders.Remove(uri);
        _changed.Fire(new WorkspaceFoldersChangedEventArgs([], [uri]));
    }

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) =>
        Watch(root, recursive, excludeGlobs, onChanges, null);

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges, FileWatchWants? wants)
    {
        var entry = (root, onChanges);
        lock (_watches) _watches.Add(entry);
        WatchRequests.Add((root, excludeGlobs));
        WatchWants.Add(wants);
        return Disposable.Create(() => { lock (_watches) _watches.Remove(entry); });
    }

    public void Raise(params FileChange[] changes)
    {
        (Uri Root, Action<IReadOnlyList<FileChange>> Callback)[] all;
        lock (_watches) all = _watches.ToArray();
        foreach (var w in all) w.Callback(changes);
    }
}

/// <summary>Delegates to the shared <see cref="RecordingWindowService"/> but also records progress (title, reports, completion).</summary>
public sealed class ProgressRecordingWindow(RecordingWindowService inner) : IWindowService
{
    public List<string> Events { get; } = new();
    public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) => inner.ShowMessageAsync(severity, message, actions, ct);
    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => inner.ShowQuickPickAsync(items, options, ct);
    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => inner.ShowQuickPickManyAsync(items, options, ct);
    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => inner.ShowInputBoxAsync(options, ct);
    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => inner.ShowOpenDialogAsync(options, ct);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => inner.ShowSaveDialogAsync(options, ct);
    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) => inner.SetStatusBarMessage(text, timeout);
    public ValueTask<bool> OpenExternalAsync(Uri uri) { lock (Events) Events.Add("external:" + uri); return inner.OpenExternalAsync(uri); }
    public ValueTask CopyToClipboardAsync(string text) => inner.CopyToClipboardAsync(text);

    public async Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work)
    {
        lock (Events) Events.Add("begin:" + title);
        try { return await work(new Reporter(this), CancellationToken.None); }
        finally { lock (Events) Events.Add("end:" + title); }
    }

    private sealed class Reporter(ProgressRecordingWindow owner) : IProgressReporter
    {
        public void Report(string? message = null, double? percent = null) { lock (owner.Events) owner.Events.Add($"report:{message}:{percent}"); }
    }
}

/// <summary>Sends some definitions to a real child-process factory and the rest to another (usually fake) one.</summary>
public sealed class RoutingConnectionFactory(Func<LspLaunchRequest, bool> useReal, ILspConnectionFactory real, ILspConnectionFactory other) : ILspConnectionFactory
{
    public ValueTask<ILspConnection> ConnectAsync(LspLaunchRequest request, CancellationToken ct) =>
        (useReal(request) ? real : other).ConnectAsync(request, ct);
}

/// <summary>TestAppPaths with the tools directory pointed at the repo's .scratch/tools (where tools/fetch-lsp-tools.sh installs real servers).</summary>
public sealed class ToolsAppPaths(IAppPaths inner, string toolsDir) : IAppPaths
{
    public string UserDataDir => inner.UserDataDir;
    public string LogsDir => inner.LogsDir;
    public string CacheDir => inner.CacheDir;
    public string GlobalStorageDir => inner.GlobalStorageDir;
    public string ToolsDir { get; } = toolsDir;
    public string InstallDir => inner.InstallDir;
    public string? WorkspaceStorageDir(Uri? workspaceFolder) => inner.WorkspaceStorageDir(workspaceFolder);
}

/// <summary>Memory monitor double: records tracked child processes and registered trimmables; lets a test trigger the over-budget callback.</summary>
public sealed class FakeMemoryMonitor : CsCode.Core.Diagnostics.IMemoryMonitor
{
    public List<(string Name, int Pid, long? Limit, Func<ValueTask>? OnOverBudget)> Tracked { get; } = new();
    public List<CsCode.Core.Diagnostics.IMemoryTrimmable> Registered { get; } = new();
    public int DisposedTracking;

    public CsCode.Core.Diagnostics.MemorySnapshot Current => default!;
    public long BudgetBytes => 0;
    public IEventSource<CsCode.Core.Diagnostics.MemorySnapshot> Sampled { get; } = new Emitter<CsCode.Core.Diagnostics.MemorySnapshot>();
    public IReadOnlyList<CsCode.Core.Diagnostics.IMemoryTrimmable> Trimmables { get { lock (Registered) return Registered.ToArray(); } }
    public IDisposable Register(CsCode.Core.Diagnostics.IMemoryTrimmable trimmable) { lock (Registered) Registered.Add(trimmable); return Disposable.Create(() => { lock (Registered) Registered.Remove(trimmable); }); }
    public ValueTask TrimNowAsync(CsCode.Core.Diagnostics.MemoryTrimLevel level, CancellationToken ct = default) => ValueTask.CompletedTask;

    public IDisposable TrackChildProcess(string name, int pid, long? restartAboveBytes = null, Func<ValueTask>? onOverBudget = null)
    {
        lock (Tracked) Tracked.Add((name, pid, restartAboveBytes, onOverBudget));
        return Disposable.Create(() => Interlocked.Increment(ref DisposedTracking));
    }
}

/// <summary>Everything a LSP test needs: Core services, test doubles, a fake connection factory and the real manager.</summary>
public sealed class LspTestEnv : IAsyncDisposable
{
    private readonly List<IDisposable> _registrations = new();
    private readonly List<ITextDocument> _documents = new();

    public DocumentManager Documents { get; } = new();
    public LanguageFeatureRegistry Registry { get; } = new();
    public IDiagnosticService Diagnostics { get; } = new DiagnosticService();
    public TestConfigurationService Config { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public ProgressRecordingWindow ProgressWindow { get; }
    public IOutputService Output { get; } = new InMemoryOutputService();
    public TestCommandService Commands { get; } = new();
    public TestAppPaths Paths { get; } = new();
    public FakeWorkspace Workspace { get; } = new();
    public TempDirectory Dir { get; } = new("cscode-lsp-");
    public RecordingNavigationService Navigation { get; }
    public FakeConnectionFactory Connections { get; } = new();
    public LanguageServerManager Manager { get; }
    public TestStatusBarService StatusBar { get; } = new();
    public CsCode.Lsp.Extensions.ReadOnlyDocuments ReadOnlyDocuments { get; } = new();
    /// <summary>What server extensions see as the debugger and the task terminals (null: none composed).</summary>
    public CsCode.Core.Debugging.IDebugService? Debug { get; set; }
    public CsCode.Core.Tasks.ITaskTerminalHost? TaskTerminals { get; set; }

    public string Root => Dir.Path;

    public FakeMemoryMonitor? Memory { get; }

    /// <summary>The repository root (found by walking up to CsCode.slnx) and its git-ignored tool directory.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string ToolsDir => Path.Combine(RepoRoot, ".scratch", "tools");

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    /// <param name="realServers">Use the real child-process connection factory, the built-in definitions and the repo's tool directory (integration tests).</param>
    public LspTestEnv(Action<TestConfigurationService>? configure = null, bool builtins = false, bool memoryMonitor = false, IDiagnosticService? diagnostics = null, IOutputService? output = null, bool realServers = false, ILspConnectionFactory? connectionsOverride = null,
        IReadOnlyList<CsCode.Lsp.Extensions.LanguageServerExtension>? extensions = null)
    {
        if (realServers) builtins = true;
        Memory = memoryMonitor ? new FakeMemoryMonitor() : null;
        if (diagnostics is not null) Diagnostics = diagnostics;
        if (output is not null) Output = output;
        Navigation = new RecordingNavigationService(Documents);
        ProgressWindow = new ProgressRecordingWindow(Window);
        // Snappy defaults for tests; individual tests override before creating the manager when they need real budgets.
        Config.Set(LspSettingKeys.InteractiveTimeoutMs, 3000);
        Config.Set(LspSettingKeys.BackgroundTimeoutMs, 5000);
        Config.Set(LspSettingKeys.ShutdownTimeoutMs, 1000);
        Config.Set(LspSettingKeys.RestartBackoffMs, new[] { 20, 40, 80, 160, 320 });
        configure?.Invoke(Config);
        if (!builtins) RemoveBuiltins(); // before the manager exists: no race with its first recompute
        Workspace.AddFolder(Dir.Path);

        var lsp = new LspServices(Documents, Registry, Diagnostics)
        {
            Configuration = Config,
            Workspace = Workspace,
            Window = ProgressWindow,
            Output = Output,
            Navigation = Navigation,
            Watcher = Workspace,
            Commands = Commands,
            Paths = realServers ? new ToolsAppPaths(Paths, ToolsDir) : Paths,
            Connections = connectionsOverride ?? (realServers ? null : Connections), // null: the manager uses the real DefaultLspConnectionFactory
            Memory = Memory,
            StatusBar = StatusBar,
            ReadOnlyDocuments = ReadOnlyDocuments,
            Debug = () => Debug,
            TaskTerminals = () => TaskTerminals,
        };
        Manager = extensions is null ? new LanguageServerManager(lsp) : new LanguageServerManager(lsp, extensions);
    }

    private void RemoveBuiltins()
    {
        // The built-in definitions are data only, but most tests want exactly one definition: shadow them as disabled.
        var overrides = BuiltinLanguageServers.All.ToDictionary(d => d.Id, _ => (object)new { enabled = false });
        Config.Set(LspSettingKeys.LanguageServers, overrides);
    }

    public static LanguageServerDefinition FakeDef(string id = "fake", string language = "fake", string[]? rootMarkers = null, bool autoStart = true) =>
        new(id, "Fake " + id, DocumentSelector.ForLanguage(language), "fake-server-" + id, ["--stdio"], RootMarkers: rootMarkers, AutoStart: autoStart);

    public IDisposable AddFake(string id = "fake", string language = "fake", string[]? rootMarkers = null, bool autoStart = true)
    {
        var d = Manager.AddDefinition(FakeDef(id, language, rootMarkers, autoStart));
        _registrations.Add(d);
        return d;
    }

    public InMemoryTextDocument Open(string relativePath, string text = "", string language = "fake")
    {
        var full = Dir.WriteFile(relativePath, text);
        var doc = new InMemoryTextDocument(new Uri(full), language, text);
        _documents.Add(doc);
        _registrations.Add(Documents.Register(doc));
        return doc;
    }

    public (InMemoryTextDocument Doc, IDisposable Registration) OpenWithToken(string relativePath, string text = "", string language = "fake")
    {
        var full = Dir.WriteFile(relativePath, text);
        var doc = new InMemoryTextDocument(new Uri(full), language, text);
        _documents.Add(doc);
        var reg = Documents.Register(doc);
        _registrations.Add(reg);
        return (doc, reg);
    }

    /// <summary>Starts a fake server for a freshly opened document and waits until the server has initialised and received didOpen.</summary>
    public async Task<(FakeLspServer Server, InMemoryTextDocument Doc)> StartAsync(Action<FakeLspServer>? configure = null, string text = "hello world\nsecond line\n", string path = "a.fake", string[]? rootMarkers = null)
    {
        Connections.OnConnect = (s, _) => configure?.Invoke(s);
        AddFake(rootMarkers: rootMarkers);
        var doc = Open(path, text);
        var server = await Connections.WaitForServerAsync();
        await server.WaitForAsync(LspMethods.Initialized);
        await server.WaitForAsync(LspMethods.TextDocumentDidOpen);
        // The client reports Running only after registering features; wait for it so tests can use providers immediately.
        await TestHelpers.EventuallyAsync(() => Manager.Servers.Any(s => s.State == LanguageServerState.Running), "server Running");
        return (server, doc);
    }

    public Task<LanguageServerInfo> WaitForStateAsync(string id, LanguageServerState state, TimeSpan? timeout = null) =>
        WaitForAsync(() => Manager.Servers.FirstOrDefault(s => s.Id == id && s.State == state), $"{id} state {state}", timeout);

    public async Task<T> WaitForAsync<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestHelpers.DefaultTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Timed out waiting for: " + what + "\n" + Dump());
        return null!;
    }

    public async Task<T> WaitForAsync<T>(Func<T?> probe, Func<string> describe, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestHelpers.DefaultTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Timed out waiting for: " + describe());
        return null!;
    }

    /// <summary>State of every server plus the Output channels: appended to timeout failures so they explain themselves.</summary>
    public string Dump()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in Manager.Servers) sb.AppendLine($"server {s.Id} state={s.State} err={s.LastError} pid={s.ProcessId} root={s.RootUri}");
        sb.AppendLine($"connections={Connections.ConnectCount} requests={Connections.Requests.Count}");
        if (Output is InMemoryOutputService mem) foreach (var n in mem.ChannelNames) sb.AppendLine($"--- {n}\n{mem.Text(n)}");
        foreach (var m in Window.Messages.ToArray()) sb.AppendLine($"message[{m.Severity}] {m.Message}");
        return sb.ToString();
    }

    public T Provider<T>(ITextDocument doc) where T : class, ILanguageProvider
    {
        var providers = Registry.GetProviders<T>(doc);
        Assert.True(providers.Count > 0, $"no {typeof(T).Name} registered for {doc.Uri}");
        return providers[0];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registrations.AsEnumerable().Reverse()) { try { r.Dispose(); } catch (Exception) { } }
        _registrations.Clear();
        await Manager.DisposeAsync();
        Paths.Dispose();
        Dir.Dispose();
    }
}

/// <summary>A status bar that keeps its items (server extensions' status items).</summary>
public sealed class TestStatusBarService : IStatusBarService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StatusBarItemModel> _items = new();
    private readonly Emitter<IReadOnlyList<StatusBarItemModel>> _changed = new();

    public IReadOnlyList<StatusBarItemModel> Items { get { lock (_gate) return [.. _items.Values]; } }
    public IEventSource<IReadOnlyList<StatusBarItemModel>> Changed => _changed;
    public StatusBarItemModel? Get(string id) { lock (_gate) return _items.GetValueOrDefault(id); }

    public IDisposable Set(StatusBarItemModel item)
    {
        lock (_gate) _items[item.Id] = item;
        return Disposable.Create(() => { lock (_gate) if (_items.TryGetValue(item.Id, out var cur) && ReferenceEquals(cur, item)) _items.Remove(item.Id); });
    }
}
