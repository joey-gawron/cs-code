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
                // The Platform watcher takes a directory that does not exist yet (it waits for it to appear); a FileSystemWatcher-based one
                // throws instead (like the Editor's fallback FswFileWatcher, which has no such waiting), and then the folder's own watch reports
                // .cscode/.vscode appearing, and OnFilesChanged watches it.
                catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or ArgumentException) { }
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

        // Never overwrite an existing file (either location).
        if (await FindLaunchJsonCoreAsync(folder, ct) is { } existing) return existing;

        var (configs, extraFiles) = await InitialCoreAsync(debugType, folder, withFiles: true, ct);
        if (configs.Count == 0) throw new InvalidOperationException($"No initial configurations are available for debug type '{debugType}'.");

        // A companion file (the tasks.json a template's preLaunchTask names) never shadows the user's own: the reader takes .cscode/ first, so a
        // new .cscode/tasks.json would hide every task of an existing .vscode/tasks.json. When the folder keeps its tasks.json in .vscode/, the
        // new launch.json goes next to it and the template's task is added to that file (VS Code's C# assets merge their build task the same way).
        var dir = ".cscode/";
        foreach (var (rel, _) in extraFiles)
            if (!await IsFileAsync(Combine(folder, ".cscode/" + rel), ct) && await IsFileAsync(Combine(folder, ".vscode/" + rel), ct)) { dir = ".vscode/"; break; }
        var target = Combine(folder, dir + "launch.json");

        await ws.FileSystem.WriteFileAsync(target, Encoding.UTF8.GetBytes(LaunchJson.Render(configs)), createParents: true, ct);
        foreach (var (rel, content) in extraFiles)
        {
            Uri? present = null;
            foreach (var location in new[] { ".cscode/", ".vscode/" }) // the reader's order: the first that exists is the one in use
                if (await IsFileAsync(Combine(folder, location + rel), ct)) { present = Combine(folder, location + rel); break; }
            if (present is null) await ws.FileSystem.WriteFileAsync(Combine(folder, dir + rel), Encoding.UTF8.GetBytes(content), createParents: true, ct);
            else if (string.Equals(rel, "tasks.json", StringComparison.Ordinal)) await MergeTasksAsync(present, content, ct);
        }
        _cache.Remove(folder);
        _configurationsChanged.Fire(null);
        return target;
    }

    private async Task<bool> IsFileAsync(Uri uri, CancellationToken ct)
    {
        try { return await _p.Workspace!.FileSystem.StatAsync(uri, ct) is { Type: FileType.File }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Adds the template's tasks whose labels <paramref name="existing"/> lacks to its <c>tasks</c> list, keeping the user's text (comments,
    /// layout) as it is (<see cref="AddTasks"/>). A file that does not parse is left alone.
    /// </summary>
    private async Task MergeTasksAsync(Uri existing, string template, CancellationToken ct)
    {
        var ws = _p.Workspace!;
        try
        {
            var text = Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(existing, ct));
            if (JsonNode.Parse(template, documentOptions: Lenient)?["tasks"] is not JsonArray offered) return;
            if (AddTasks(text, offered.OfType<JsonObject>(), out var problem) is not { } edit)
            {
                if (problem is not null) _logger.LogWarning("Could not add the template's task to {File}: {Problem}", existing, problem);
                return;
            }
            await ws.FileSystem.WriteFileAsync(existing, Encoding.UTF8.GetBytes(edit.ApplyTo(text)), createParents: false, ct);
        }
        catch (JsonException ex) { _logger.LogWarning("The template's tasks.json does not parse: {Message}", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Could not add the template's task to {File}", existing); }
    }

    /// <summary>
    /// The edit that appends the tasks of <paramref name="offered"/> whose labels <paramref name="text"/> (a tasks.json) lacks to its <c>tasks</c>
    /// list, keeping the rest of the text as written. A task offered as the default build task is added as a plain build task when the file has
    /// a default build task already (a default TEST task does not count: VS Code keeps one default per group). Null when nothing is missing, or
    /// with <paramref name="problem"/> set when the text does not parse or has no <c>tasks</c> list that can be edited.
    /// </summary>
    internal static JsoncInsertion? AddTasks(string text, IEnumerable<JsonObject> offered, out string? problem)
    {
        problem = null;
        JsonObject? current;
        try { current = JsonNode.Parse(text, documentOptions: Lenient) as JsonObject; }
        catch (JsonException ex) { problem = $"it does not parse ({ex.Message})"; return null; }
        if (current is null) { problem = "it is not a JSON object"; return null; }
        var tasks = current["tasks"] as JsonArray;
        var labels = tasks?.OfType<JsonObject>().Select(t => t["label"]?.GetValueKind() == JsonValueKind.String ? t["label"]!.GetValue<string>() : null).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var hasDefaultBuild = tasks?.OfType<JsonObject>().Any(IsDefaultBuild) ?? false;
        var missing = new List<JsonNode>();
        foreach (var task in offered)
        {
            if (task["label"]?.GetValueKind() != JsonValueKind.String || !labels.Add(task["label"]!.GetValue<string>())) continue;
            var copy = (JsonObject)task.DeepClone();
            if (hasDefaultBuild && IsDefaultBuild(copy)) copy["group"] = ((JsonObject)copy["group"]!)["kind"]!.GetValue<string>();
            hasDefaultBuild |= IsDefaultBuild(copy);
            missing.Add(copy);
        }
        if (missing.Count == 0) return null;
        var edit = JsoncText.AppendToArray(text, "tasks", missing);
        if (edit is null) problem = "its 'tasks' is not a list";
        return edit;

        // what TaskService counts as the default build task (TaskDefinition.IsDefaultBuild): group { "kind": "build", "isDefault": true }
        static bool IsDefaultBuild(JsonObject task) =>
            task["group"] is JsonObject g && g["isDefault"]?.GetValueKind() == JsonValueKind.True
            && g["kind"]?.GetValueKind() == JsonValueKind.String && string.Equals(g["kind"]!.GetValue<string>(), "build", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The initial configurations (and companion files) of a debug type: its providers', else a configured debugger's <c>initialConfigurations</c>.</summary>
    private async Task<(List<DebugConfiguration> Configs, List<(string Path, string Content)> Files)> InitialCoreAsync(string debugType, Uri folder, bool withFiles, CancellationToken ct)
    {
        var configs = new List<DebugConfiguration>();
        var extraFiles = new List<(string Path, string Content)>();
        foreach (var (type, provider) in _providers.Where(p => string.Equals(p.Type, debugType, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            configs.AddRange(await provider.ProvideInitialAsync(folder, ct));
            if (withFiles && provider is IDebugInitialFilesProvider files) extraFiles.AddRange(await files.ProvideInitialFilesAsync(folder, ct));
        }
        if (configs.Count == 0 && _p.Contributions is { } registry)
            foreach (var c in registry.Of<DebuggerContribution>().Items.Where(i => i.Value.Type == debugType))
                if (c.Value.InitialConfigurations is { ValueKind: JsonValueKind.Array } arr)
                    configs.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => DebugConfiguration.FromJson(e, folder)));
        return (configs, extraFiles);
    }

    /// <summary>
    /// What "Add Configuration…" offers for a debug type: the configurations a new launch.json for it would hold, and the companion tasks.json
    /// that Create launch.json would write with them (the tasks their preLaunchTask names; null when the debugger has none).
    /// </summary>
    internal sealed record ConfigurationTemplates(IReadOnlyList<DebugConfiguration> Configurations, string? TasksJson);

    /// <summary>The configurations (and companion tasks.json) "Add Configuration…" offers for a debug type.</summary>
    internal ValueTask<ConfigurationTemplates> GetConfigurationTemplatesAsync(string debugType, Uri? folder = null, CancellationToken ct = default) =>
        new(_lane.RunAsync(async () =>
        {
            var f = folder ?? _p.PrimaryFolder ?? throw new InvalidOperationException("Open a folder first.");
            var (configs, files) = await InitialCoreAsync(debugType, f, withFiles: true, ct);
            return new ConfigurationTemplates(configs, files.Where(x => string.Equals(x.Path, "tasks.json", StringComparison.Ordinal)).Select(x => x.Content).FirstOrDefault());
        }, ct));

    /// <summary>
    /// The tasks of <paramref name="tasksJson"/> (a template's companion tasks.json) that <paramref name="configuration"/> runs: its
    /// <c>preLaunchTask</c> and <c>postDebugTask</c>. Empty when it names none of them, or the text does not parse.
    /// </summary>
    internal static IReadOnlyList<JsonObject> TasksNamedBy(DebugConfiguration configuration, string? tasksJson)
    {
        var names = new[] { configuration.PreLaunchTask, configuration.PostDebugTask }.OfType<string>().Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0 || tasksJson is null) return [];
        try
        {
            return JsonNode.Parse(tasksJson, documentOptions: Lenient)?["tasks"] is JsonArray tasks
                ? [.. tasks.OfType<JsonObject>().Where(t => t["label"]?.GetValueKind() == JsonValueKind.String && names.Contains(t["label"]!.GetValue<string>()))]
                : [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The tasks.json a folder's tasks come from (TaskService's order: <c>.cscode/</c> first, else <c>.vscode/</c>); null when it has none.</summary>
    internal ValueTask<Uri?> FindTasksJsonAsync(Uri folder, CancellationToken ct = default) =>
        new(_lane.RunAsync(async () =>
        {
            foreach (var location in new[] { ".cscode/", ".vscode/" })
                if (_p.Workspace is not null && await IsFileAsync(Combine(folder, location + "tasks.json"), ct)) return Combine(folder, location + "tasks.json");
            return (Uri?)null;
        }, ct));

    /// <summary>The launch.json a folder's configurations come from (<c>.cscode/</c> first, else <c>.vscode/</c>); null when it has none.</summary>
    public ValueTask<Uri?> FindLaunchJsonAsync(Uri? folder = null, CancellationToken ct = default) =>
        new(_lane.RunAsync(() => (folder ?? _p.PrimaryFolder) is { } f ? FindLaunchJsonCoreAsync(f, ct) : Task.FromResult<Uri?>(null), ct));

    private async Task<Uri?> FindLaunchJsonCoreAsync(Uri folder, CancellationToken ct)
    {
        if (_p.Workspace is null) return null;
        foreach (var rel in LaunchLocations)
            if (await IsFileAsync(Combine(folder, rel), ct)) return Combine(folder, rel);
        return null;
    }

    /// <summary>
    /// Appends <paramref name="configuration"/> to the <c>configurations</c> of <paramref name="text"/> (a launch.json's content), keeping the
    /// rest of the text as written. Null when the text has no editable <c>configurations</c> list.
    /// </summary>
    internal static JsoncInsertion? AddConfiguration(string text, DebugConfiguration configuration)
    {
        var node = new JsonObject();
        foreach (var (k, v) in configuration.Properties) node[k] = VariableResolver.ToNode(v);
        return JsoncText.AppendToArray(text, "configurations", [node]);
    }

    /// <summary>A launch.json changed by someone else than the file watcher could see (an edit applied in the editor, then saved): reread it next time.</summary>
    internal void Invalidate(Uri folder) => _lane.Post(() =>
    {
        _cache.Remove(folder);
        _configurationsChanged.Fire(null);
    });

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
