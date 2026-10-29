using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Configuration;

/// <summary>A provider that can also create companion files (e.g. a tasks.json with the build task a template's preLaunchTask refers to).</summary>
public interface IDebugInitialFilesProvider
{
    /// <summary>Extra files (path relative to the launch.json directory, content) to write only if they do not exist yet.</summary>
    ValueTask<IReadOnlyList<(string RelativePath, string Content)>> ProvideInitialFilesAsync(Uri? folder, CancellationToken ct);
}

internal sealed record PersistedSelection(string Name, string? Folder);

/// <summary>
/// <c>launch.json</c> (JSONC) from <c>.cscode/</c> first, else <c>.vscode/</c>; compounds; live reload; the toolbar's selected configuration
/// (persisted per workspace); VS Code variable substitution; initial-file generation. File I/O goes through the workspace
/// <see cref="IFileSystem"/> (so it also works for remote workspaces) and all work is hopped to the Debug lane first.
/// </summary>
public sealed class DebugConfigurationService : IDebugConfigurationService, IAsyncDisposable, IDisposable
{
    private const string SelectionKey = "debug.selectedConfiguration";
    private static readonly string[] LaunchLocations = [".cscode/launch.json", ".vscode/launch.json"];

    private readonly DebugLane _lane;
    private readonly DapPlatform _p;
    private readonly Func<ITaskService?>? _tasks;
    private readonly ILogger _logger;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<Uri, LaunchFile> _cache = new();
    private readonly List<(string Type, IDebugConfigurationProvider Provider)> _providers = new();
    private readonly List<IDisposable> _watches = new();
    private readonly Emitter<DebugConfiguration?> _selectedChanged = new();
    private readonly Emitter<object?> _configurationsChanged = new();
    private DebugConfiguration? _selected;
    private string? _lastErrorShown;
    private int _disposed;

    public DebugConfigurationService(DebugLane lane, DapPlatform platform, Func<ITaskService?>? tasks = null)
    {
        _lane = lane;
        _p = platform;
        _tasks = tasks;
        _logger = platform.Logger("CsCode.Dap.Configuration");
        if (platform.Workspace is { } ws)
        {
            _subs.Add(ws.FoldersChanged.Subscribe(_ => _lane.Post(() => OnWorkspaceChanged())));
            _lane.Post(() => RewatchFolders());
        }
    }

    public IEventSource<DebugConfiguration?> SelectedChanged => _selectedChanged;
    public IEventSource<object?> ConfigurationsChanged => _configurationsChanged;

    /// <summary>Last launch.json parse problem (null when the file loaded fine).</summary>
    public string? LastError { get; private set; }

    public DebugConfiguration? Selected
    {
        get => Volatile.Read(ref _selected);
        set
        {
            var old = Interlocked.Exchange(ref _selected, value);
            if (Equals(old, value)) return;
            PersistSelection(value);
            _selectedChanged.Fire(value);
        }
    }

    // ------------------------------------------------------------------ providers (used by DebugService.RegisterConfigurationProvider)

    internal IDisposable AddProvider(string type, IDebugConfigurationProvider provider)
    {
        var entry = (type, provider);
        _lane.Post(() => _providers.Add(entry));
        return Disposable.Create(() => _lane.Post(() => _providers.Remove(entry)));
    }

    // ------------------------------------------------------------------ loading

    public ValueTask<IReadOnlyList<DebugConfiguration>> GetLaunchConfigurationsAsync(Uri? folder = null, CancellationToken ct = default) =>
        new(_lane.RunAsync(() => GetCoreAsync(folder, ct), ct));

    private async Task<IReadOnlyList<DebugConfiguration>> GetCoreAsync(Uri? folder, CancellationToken ct)
    {
        var result = new List<DebugConfiguration>();
        foreach (var f in FoldersOrDefault(folder))
        {
            var file = await LoadAsync(f, ct);
            result.AddRange(file.All);
        }
        EnsureSelection(result);
        return result;
    }

    private IEnumerable<Uri?> FoldersOrDefault(Uri? folder)
    {
        if (folder is not null) return [folder];
        var folders = _p.Workspace?.Folders;
        if (folders is { Count: > 0 }) return folders.Select(f => (Uri?)f);
        return new Uri?[] { null };
    }

    /// <summary>The launch.json model of a folder (null folder = no workspace: empty).</summary>
    internal async Task<LaunchFile> LoadAsync(Uri? folder, CancellationToken ct)
    {
        if (folder is null || _p.Workspace is not { } ws) return LaunchFile.Empty("");
        if (_cache.TryGetValue(folder, out var cached)) return cached;

        LaunchFile file = LaunchFile.Empty("");
        foreach (var rel in LaunchLocations)
        {
            var uri = Combine(folder, rel);
            FileStat? stat;
            try { stat = await ws.FileSystem.StatAsync(uri, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stat = null; }
            if (stat is not { Type: FileType.File }) continue;
            try
            {
                var text = Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(uri, ct));
                file = LaunchJson.Parse(text, folder, uri.IsFile ? uri.LocalPath : uri.ToString());
                LastError = null;
                _lastErrorShown = null;
            }
            catch (LaunchJsonException ex)
            {
                LastError = ex.Message;
                _logger.LogWarning("{Message}", ex.Message);
                if (_p.Window is { } w && _lastErrorShown != ex.Message)
                {
                    _lastErrorShown = ex.Message;
                    _ = w.ShowMessageAsync(MessageSeverity.Error, ex.Message, null, CancellationToken.None).AsTask();
                }
                file = LaunchFile.Empty(uri.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = $"Could not read {uri}: {ex.Message}";
            }
            break; // first existing location wins
        }
        _cache[folder] = file;
        return file;
    }

    private void EnsureSelection(IReadOnlyList<DebugConfiguration> configs)
    {
        var current = Volatile.Read(ref _selected);
        if (current is not null && configs.FirstOrDefault(c => c.Name == current.Name && c.WorkspaceFolder == current.WorkspaceFolder) is { } fresh)
        {
            // Same entry, but launch.json may have been edited since it was selected: adopt the new object so a start never uses stale properties.
            if (SameContent(current, fresh)) return;
            Interlocked.Exchange(ref _selected, fresh);
            _selectedChanged.Fire(fresh);
            return;
        }
        DebugConfiguration? next = null;
        if (_p.State?.Get<PersistedSelection>(SelectionKey, StorageScope.Workspace) is { } saved)
            next = configs.FirstOrDefault(c => c.Name == saved.Name && (saved.Folder is null || c.WorkspaceFolder?.ToString() == saved.Folder));
        next ??= configs.FirstOrDefault();
        if (ReferenceEquals(next, current)) return;
        Interlocked.Exchange(ref _selected, next);
        _selectedChanged.Fire(next);
    }

    private static bool SameContent(DebugConfiguration a, DebugConfiguration b) =>
        a.Type == b.Type && a.Request == b.Request && a.Properties.Count == b.Properties.Count &&
        a.Properties.All(kv => b.Properties.TryGetValue(kv.Key, out var other) && other.GetRawText() == kv.Value.GetRawText());

    private void PersistSelection(DebugConfiguration? value)
    {
        if (_p.State is null) return;
        try
        {
            if (value is null) _p.State.Remove(SelectionKey, StorageScope.Workspace);
            else _p.State.Set(SelectionKey, new PersistedSelection(value.Name, value.WorkspaceFolder?.ToString()), StorageScope.Workspace);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not persist the selected launch configuration"); }
    }

    // ------------------------------------------------------------------ watching

    private void OnWorkspaceChanged()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _selected, null);
        RewatchFolders();
        _configurationsChanged.Fire(null);
    }

    private void RewatchFolders()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        foreach (var w in _watches) w.Dispose();
        _watches.Clear();
        if (_p.Workspace is not { } ws) return;
        foreach (var folder in ws.Folders)
        {
            // Non-recursive watches: the folder itself (to notice .cscode/.vscode appearing) and the two config directories.
            foreach (var dir in new[] { folder, Combine(folder, ".cscode/"), Combine(folder, ".vscode/") })
            {
                try { _watches.Add(ws.Watcher.Watch(dir, false, null, changes => OnFilesChanged(folder, changes))); }
                catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or ArgumentException) { /* directory does not exist yet: the root watch will tell us when it appears */ }
            }
        }
    }

    private void OnFilesChanged(Uri folder, IReadOnlyList<FileChange> changes)
    {
        var relevant = changes.Any(c =>
        {
            var name = Path.GetFileName(c.Uri.LocalPath.TrimEnd('/'));
            return name is "launch.json" or ".cscode" or ".vscode";
        });
        if (!relevant) return;
        _lane.Post(() =>
        {
            _cache.Remove(folder);
            RewatchFolders(); // a newly created .cscode/.vscode directory needs its own watch
            _configurationsChanged.Fire(null);
        });
    }

    // ------------------------------------------------------------------ variables

    public ValueTask<DebugConfiguration> ResolveVariablesAsync(DebugConfiguration configuration, CancellationToken ct = default) =>
        new(_lane.RunAsync(() => ResolveCoreAsync(configuration, ct), ct));

    private async Task<DebugConfiguration> ResolveCoreAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        var folder = configuration.WorkspaceFolder ?? _p.PrimaryFolder;
        var inputs = (await LoadAsync(folder, ct)).Inputs;
        var resolver = new VariableResolver(_p.CreateVariableContext(folder, inputs, DefaultBuildTaskAsync(folder)));

        var obj = new JsonObject();
        foreach (var (k, v) in configuration.Properties) obj[k] = VariableResolver.ToNode(v);
        obj = VariableResolver.MergePlatformBlock(obj); // programmatic configs may still carry windows/linux/osx blocks
        var resolved = (JsonObject)(await resolver.ResolveAsync(obj, ct))!;
        var element = JsonSerializer.SerializeToElement(resolved);
        return DebugConfiguration.FromJson(element, configuration.WorkspaceFolder) with { NoDebug = configuration.NoDebug };
    }

    private Func<CancellationToken, ValueTask<string?>>? DefaultBuildTaskAsync(Uri? folder)
    {
        if (_tasks?.Invoke() is not { } tasks) return null;
        return async ct => (await tasks.GetTasksAsync(folder, ct)).FirstOrDefault(t => t.IsDefaultBuild)?.Label;
    }

    // ------------------------------------------------------------------ launch.json creation

    public ValueTask<Uri> CreateLaunchJsonAsync(string debugType, Uri? folder = null, CancellationToken ct = default) =>
        new(_lane.RunAsync(() => CreateCoreAsync(debugType, folder, ct), ct));

    private async Task<Uri> CreateCoreAsync(string debugType, Uri? folder, CancellationToken ct)
    {
        folder ??= _p.PrimaryFolder ?? throw new InvalidOperationException("Open a folder before creating a launch.json.");
        var ws = _p.Workspace ?? throw new InvalidOperationException("No workspace service is available.");
        var target = Combine(folder, ".cscode/launch.json");

        // Never overwrite an existing file (either location).
        foreach (var rel in LaunchLocations)
            if (await ws.FileSystem.StatAsync(Combine(folder, rel), ct) is { Type: FileType.File }) return Combine(folder, rel);

        var configs = new List<DebugConfiguration>();
        var extraFiles = new List<(string Path, string Content)>();
        foreach (var (type, provider) in _providers.Where(p => p.Type == debugType))
        {
            configs.AddRange(await provider.ProvideInitialAsync(folder, ct));
            if (provider is IDebugInitialFilesProvider files) extraFiles.AddRange(await files.ProvideInitialFilesAsync(folder, ct));
        }
        if (configs.Count == 0 && _p.Contributions is { } registry)
            foreach (var c in registry.Of<DebuggerContribution>().Items.Where(i => i.Value.Type == debugType))
                if (c.Value.InitialConfigurations is { ValueKind: JsonValueKind.Array } arr)
                    configs.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => DebugConfiguration.FromJson(e, folder)));
        if (configs.Count == 0) throw new InvalidOperationException($"No initial configurations are available for debug type '{debugType}'.");

        await ws.FileSystem.WriteFileAsync(target, Encoding.UTF8.GetBytes(LaunchJson.Render(configs)), createParents: true, ct);
        foreach (var (rel, content) in extraFiles)
        {
            var uri = Combine(Combine(folder, ".cscode/"), rel);
            if (await ws.FileSystem.StatAsync(uri, ct) is null)
                await ws.FileSystem.WriteFileAsync(uri, Encoding.UTF8.GetBytes(content), createParents: true, ct);
        }
        _cache.Remove(folder);
        _configurationsChanged.Fire(null);
        return target;
    }

    private static Uri Combine(Uri folder, string relative)
    {
        var baseText = folder.ToString();
        if (!baseText.EndsWith('/')) baseText += "/";
        return new Uri(baseText + relative);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subs.Dispose();
        foreach (var w in _watches) w.Dispose();
        _watches.Clear();
        _cache.Clear();
        _providers.Clear();
        _selectedChanged.Clear();
        _configurationsChanged.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
