using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Configuration;

/// <summary>A settings file that could not be used (the previous good values stay in effect).</summary>
public sealed record ConfigurationError(string Path, string Message);

/// <summary>A user-only setting (application or machine scope) that the workspace or folder file <paramref name="Path"/> sets and that is ignored.</summary>
/// <param name="Key">The full key as the file sets it, e.g. <c>remote.ssh.path</c>, <c>cscode.languageServers.x.command</c> or <c>[go].cscode.lsp.searchDirs</c>.</param>
public sealed record IgnoredSetting(string Path, string Key, ConfigurationScope Scope);

public sealed class ConfigurationFileException(string path, string message, Exception? inner = null)
    : IOException($"{message} ({path})", inner)
{
    public string Path { get; } = path;
}

/// <summary>
/// Layered settings: registered defaults &lt; user <c>settings.json</c> &lt; workspace (<c>.vscode/settings.json</c>, then
/// <c>.cscode/settings.json</c> of the primary folder) &lt; additional workspace folders' <c>.cscode/settings.json</c>.
/// <list type="bullet">
/// <item>JSONC files; <c>"editor.fontSize"</c> and <c>{"editor":{"fontSize":…}}</c> are equivalent. As in VS Code only the TOP-LEVEL keys of a file
/// (and the keys directly inside a <c>"[lang]"</c> block) are split on dots: member names inside a value are kept as written, so globs
/// (<c>files.associations</c>), URLs (<c>yaml.schemas</c>) and server ids survive. Objects merge across layers, arrays/scalars replace.</item>
/// <item>Scopes (VS Code's): a setting registered as <see cref="ConfigurationScope.Application"/> or <see cref="ConfigurationScope.Machine"/>, and
/// every <see cref="ConfigurationProperty.MachineScopedMembers"/> path, takes its value from the defaults and the user file only. What a workspace
/// or folder file says there is dropped, listed in <see cref="IgnoredSettings"/>, logged and shown once as a notice; writing it there is refused.
/// So a cloned repository cannot choose the programs CsCode starts. Settings nobody registered are window-scoped.</item>
/// <item>Reads (<see cref="Get{T}(string, T)"/>, <see cref="GetRaw"/>) come from an immutable snapshot swapped atomically: no lock and,
/// for primitive types, no allocation after the first read of a key.</item>
/// <item>Files are watched; a debounced reload diffs old/new merged values and raises <see cref="Changed"/> with the exact affected keys
/// (no event for a no-op rewrite or for our own write echo). A malformed file keeps the last good values and sets <see cref="LastError"/>.</item>
/// <item><see cref="UpdateAsync"/> edits the file in place, preserving comments/order/formatting, and writes atomically.</item>
/// </list>
/// Change events are raised on a background (IO lane) thread; UI subscribers must marshal.
/// </summary>
public sealed class ConfigurationService : IConfigurationService, IWorkspaceBound, IDisposable
{
    private const string SettingsFile = "settings.json";
    private const int MaxCachedKeys = 4096;
    private static readonly object Missing = new();

    private sealed class Layer(string name, bool isWorkspace)
    {
        public string Name { get; } = name;
        /// <summary>A workspace or folder file: user-only settings in it are ignored.</summary>
        public bool IsWorkspace { get; } = isWorkspace;
        public string? Path;
        public string? Text;           // last text seen on disk ("" = absent); identical text never triggers work
        public JsoncObject? Tree;      // last GOOD parse
        public string? Error;
    }

    private sealed class Registration(IReadOnlyList<ConfigurationProperty> properties)
    {
        public IReadOnlyList<ConfigurationProperty> Properties { get; } = properties;
    }

    private readonly IAppPaths _paths;
    private readonly IoLane _lane;
    private readonly IFileWatcher? _watcher;
    private readonly PlatformOptions _options;
    private readonly ILogger _log;
    private readonly object _gate = new();          // layers + registrations (never held across I/O)
    private readonly object _publishGate = new();   // serialises snapshot rebuild + swap
    private readonly object _writeGate = new();     // serialises read-modify-write of settings files (lane threads only)
    private readonly Emitter<ConfigurationChangedEventArgs> _changed = new();
    private readonly Emitter<ConfigurationError?> _errorChanged = new();
    private readonly DisposableStore _watches = new();
    private readonly Timer _reloadTimer;
    private readonly Layer _user = new("user", isWorkspace: false);
    private readonly Layer _vscode = new("workspace(.vscode)", isWorkspace: true);
    private readonly Layer _workspace = new("workspace", isWorkspace: true);
    private List<Layer> _folderLayers = new();
    private readonly List<Registration> _registrations = new();
    private DisposableStore? _workspaceWatches;
    private Snapshot _snapshot;
    private ConfigurationError? _lastError;
    private IReadOnlyList<Uri> _folders = [];
    private readonly Emitter<IReadOnlyList<IgnoredSetting>> _ignoredChanged = new();
    private readonly HashSet<string> _noticeShown = new(StringComparer.Ordinal); // (file, key) pairs already announced; guarded by _publishGate
    private readonly Func<IWindowService?>? _windowService;
    private bool _disposed;

    /// <param name="windowService">Resolved lazily, when a workspace file sets a user-only setting: the notice is shown through it (null: log only).</param>
    public ConfigurationService(IAppPaths paths, IoLane lane, IFileWatcher? watcher = null, PlatformOptions? options = null, ILogger<ConfigurationService>? logger = null,
        Func<IWindowService?>? windowService = null)
    {
        _paths = paths;
        _lane = lane;
        _watcher = watcher;
        _options = options ?? new PlatformOptions();
        _log = logger ?? NullLogger<ConfigurationService>.Instance;
        _windowService = windowService;
        UserSettingsPath = Path.Combine(AppPaths.ConfigDirOf(paths), SettingsFile);
        _user.Path = UserSettingsPath;
        _registrations.Add(new Registration(BuiltInSettings.All));

        // Startup read is synchronous on purpose (settings must be complete before the first consumer runs); the file is small and
        // DI construction happens at startup. Everything after this (reload, update, workspace folders) runs on the IO lane.
        LoadLayerFromDisk(_user);
        _snapshot = Snapshot.Empty;
        PublishCore(raise: false);
        _lastError = ComputeError();

        _reloadTimer = new Timer(static s => ((ConfigurationService)s!).OnReloadTimer(), this, Timeout.Infinite, Timeout.Infinite);
        ArmUserWatcher();
    }

    public string UserSettingsPath { get; }

    public IEventSource<ConfigurationChangedEventArgs> Changed => _changed;

    /// <summary>The most recent settings-file problem, or null when every file parsed (or is absent). The value in effect is the last good one.</summary>
    public ConfigurationError? LastError => Volatile.Read(ref _lastError);

    /// <summary>Raised once when a file turns bad (and with null when everything is good again) — not on every reload of the same bad file.</summary>
    public IEventSource<ConfigurationError?> ErrorChanged => _errorChanged;

    /// <summary>
    /// The user-only settings (<see cref="ConfigurationScope.Application"/>/<see cref="ConfigurationScope.Machine"/> keys and machine-scoped members)
    /// that a workspace or folder file sets and that are therefore ignored, as of the current snapshot.
    /// </summary>
    public IReadOnlyList<IgnoredSetting> IgnoredSettings => Volatile.Read(ref _snapshot).Ignored;

    /// <summary>Raised (on the IO lane) when <see cref="IgnoredSettings"/> changes.</summary>
    public IEventSource<IReadOnlyList<IgnoredSetting>> IgnoredSettingsChanged => _ignoredChanged;

    public IReadOnlyList<ConfigurationProperty> Properties
    {
        get
        {
            lock (_gate) return _registrations.SelectMany(static r => r.Properties).ToArray();
        }
    }

    // ---- reads (hot path) --------------------------------------------------------------------------------------------------------

    public JsonElement? GetRaw(string key) => Volatile.Read(ref _snapshot).GetRaw(key);

    public T Get<T>(string key, T defaultValue)
    {
        var snap = Volatile.Read(ref _snapshot);
        if (Cacheable<T>.Value)
        {
            if (!snap.Typed.TryGetValue((key, typeof(T)), out var boxed))
            {
                boxed = Convert<T>(snap, key);
                if (snap.Typed.Count < MaxCachedKeys) snap.Typed.TryAdd((key, typeof(T)), boxed);
            }
            return ReferenceEquals(boxed, Missing) ? defaultValue : (T)boxed!;
        }

        var obj = Convert<T>(snap, key);
        return ReferenceEquals(obj, Missing) ? defaultValue : (T)obj!;
    }

    public T? Get<T>(string key) => Get<T>(key, default!);

    // Primitive/immutable results are boxed once per (snapshot, key, type) so repeated reads allocate nothing; anything else is
    // deserialised per call (callers may mutate what they get back).
    private object? Convert<T>(Snapshot snap, string key)
    {
        if (snap.GetRaw(key) is not { } element) return Missing;
        try { return element.Deserialize<T>(CoreJson.Options); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            _log.LogDebug("Setting '{Key}' has a value that cannot be read as {Type}: {Message}", key, typeof(T).Name, ex.Message);
            return Missing;
        }
    }

    private static class Cacheable<T>
    {
        public static readonly bool Value = typeof(T).IsPrimitive || typeof(T) == typeof(string) || typeof(T) == typeof(decimal) || typeof(T).IsEnum;
    }

    // ---- property registration ---------------------------------------------------------------------------------------------------

    public IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties)
    {
        var reg = new Registration(properties.ToArray());
        lock (_gate) _registrations.Add(reg);
        Publish();
        return Disposable.Create(() =>
        {
            lock (_gate) _registrations.Remove(reg);
            if (!_disposed) Publish();
        });
    }

    // ---- updates -----------------------------------------------------------------------------------------------------------------

    public ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var path = target switch
        {
            ConfigurationTarget.User => UserSettingsPath,
            ConfigurationTarget.Workspace or ConfigurationTarget.WorkspaceFolder => WorkspaceSettingsPath()
                ?? throw new InvalidOperationException("No folder is open: workspace settings cannot be updated"),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        // VS Code refuses the same write ("This setting can be written only into User settings").
        if (target != ConfigurationTarget.User && value is not null && UserOnlyScopeOf(key) is { } scope)
            throw new InvalidOperationException(
                $"'{key}' can only be set in user settings ({scope.ToString().ToLowerInvariant()} scope): a workspace or folder cannot choose it.");
        var node = ToNode(value);
        return new ValueTask(_lane.RunAsync(token => UpdateCore(path, key, node, token), ct));
    }

    /// <summary>The user-only scope that covers <paramref name="key"/> (the key itself, a key below it, or a machine-scoped member), or null.</summary>
    private ConfigurationScope? UserOnlyScopeOf(string key)
    {
        List<ConfigurationProperty> props;
        lock (_gate) props = _registrations.SelectMany(static r => r.Properties).ToList();
        var segments = key.Split('.');
        foreach (var p in props)
        {
            if (p.IsUserSettingOnly && (key == p.Key || key.StartsWith(p.Key + ".", StringComparison.Ordinal))) return p.Scope;
            if (p.MachineScopedMembers is not { Count: > 0 } members || !key.StartsWith(p.Key + ".", StringComparison.Ordinal)) continue;
            var relative = segments.AsSpan(p.Key.Split('.').Length);
            foreach (var member in members)
            {
                var pattern = member.Split('.');
                if (relative.Length < pattern.Length) continue;
                var hit = true;
                for (var i = 0; i < pattern.Length && hit; i++) hit = pattern[i] == "*" || pattern[i] == relative[i];
                if (hit) return ConfigurationScope.Machine;
            }
        }
        return null;
    }

    private string? WorkspaceSettingsPath()
    {
        lock (_gate) return _workspace.Path;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        JsonElement e => e.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(e.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, CoreJson.Options),
    };

    private void UpdateCore(string path, string key, JsonNode? value, CancellationToken ct)
    {
        lock (_writeGate)
        {
            ct.ThrowIfCancellationRequested();
            var (current, hadBom) = ReadText(path); // fresh from disk: never clobber an edit the watcher has not delivered yet

            string updated;
            try { updated = JsoncEditor.Set(current, key, value); }
            catch (JsoncException ex)
            {
                throw new ConfigurationFileException(path, $"Cannot update '{key}': the settings file is not valid JSON with comments — {ex.Message}", ex);
            }
            if (updated == current) return;

            var bytes = new UTF8Encoding(hadBom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(updated)).ToArray();
            AtomicFile.Write(path, bytes, createParents: true, ct);

            // Apply to memory immediately and remember the text: the watcher's echo of this very write then finds identical text and is ignored.
            Layer? layer;
            lock (_gate) layer = LayerForPath(path);
            if (layer is not null) ApplyText(layer, updated);
        }
        Publish();
    }

    private Layer? LayerForPath(string path)
    {
        if (string.Equals(_user.Path, path, StringComparison.Ordinal)) return _user;
        if (string.Equals(_workspace.Path, path, StringComparison.Ordinal)) return _workspace;
        if (string.Equals(_vscode.Path, path, StringComparison.Ordinal)) return _vscode;
        return _folderLayers.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.Ordinal));
    }

    // ---- loading -----------------------------------------------------------------------------------------------------------------

    private static (string Text, bool HadBom) ReadText(string path)
    {
        try
        {
            var bytes = AtomicFile.ReadAll(path, CancellationToken.None);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return (Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }
        catch (FileNotFoundException) { return ("", false); }
        catch (DirectoryNotFoundException) { return ("", false); }
    }

    /// <summary>Re-reads one layer. Returns true when its content changed (and was applied or recorded as an error).</summary>
    private bool LoadLayerFromDisk(Layer layer)
    {
        string? path;
        lock (_gate) path = layer.Path;
        string text;
        if (path is null) text = "";
        else
        {
            try { text = ReadText(path).Text; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "Could not read {Path}; keeping the previous settings", path);
                return false;
            }
        }
        lock (_gate)
        {
            if (layer.Text == text && layer.Path == path) return false;
            ApplyText(layer, text);
            return true;
        }
    }

    /// <summary>Parses and stores <paramref name="text"/>. Malformed text records the error and KEEPS the previous good tree.</summary>
    private void ApplyText(Layer layer, string text)
    {
        lock (_gate)
        {
            layer.Text = text;
            try
            {
                var root = JsoncParser.Parse(text);
                if (root is not null and not JsoncObject) throw new JsoncException("The settings file must contain a JSON object at the top level", root.Start);
                layer.Tree = (JsoncObject?)root;
                layer.Error = null;
            }
            catch (JsoncException ex)
            {
                layer.Error = ex.Message;
                _log.LogWarning("Ignoring malformed settings file {Path}: {Message}. Previous values stay in effect.", layer.Path, ex.Message);
            }
        }
    }

    // ---- publish / diff ----------------------------------------------------------------------------------------------------------

    private void Publish() => PublishCore(raise: true);

    private void PublishCore(bool raise)
    {
        IReadOnlyCollection<string>? affected = null;
        ConfigurationError? error;
        bool errorChanged;
        IReadOnlyList<IgnoredSetting>? ignoredChanged = null;
        List<IgnoredSetting>? announce = null;
        lock (_publishGate)
        {
            List<Snapshot.LayerTree> trees;
            List<ConfigurationProperty> props;
            lock (_gate)
            {
                trees = new List<Snapshot.LayerTree>();
                if (_user.Tree is not null) trees.Add(new(_user.Tree, _user.Path ?? _user.Name, IsWorkspace: false)); // user sits below every workspace layer
                foreach (var l in new[] { _vscode, _workspace }.Concat(_folderLayers))
                    if (l.Tree is not null) trees.Add(new(l.Tree, l.Path ?? l.Name, l.IsWorkspace));
                props = _registrations.SelectMany(static r => r.Properties).ToList();
            }

            var next = Snapshot.Build(props, trees);
            var old = Interlocked.Exchange(ref _snapshot, next);
            if (raise)
            {
                var keys = Snapshot.Diff(old, next);
                if (keys.Count > 0) affected = keys;
            }
            if (!old.Ignored.SequenceEqual(next.Ignored))
            {
                ignoredChanged = next.Ignored;
                foreach (var i in next.Ignored)
                    if (_noticeShown.Add(i.Path + "\n" + i.Key)) (announce ??= []).Add(i);
            }
            lock (_gate) error = ComputeError();
            errorChanged = !Equals(error, _lastError);
            _lastError = error;
        }

        if (affected is not null) _changed.Fire(new ConfigurationChangedEventArgs(affected));
        if (errorChanged && raise) _errorChanged.Fire(error);
        if (announce is not null) Announce(announce);
        if (ignoredChanged is not null) _ignoredChanged.Fire(ignoredChanged);
    }

    /// <summary>Logs, and shows once per file and key, the user-only settings a workspace or folder file tried to set.</summary>
    private void Announce(List<IgnoredSetting> ignored)
    {
        foreach (var group in ignored.GroupBy(static i => i.Path))
        {
            var keys = string.Join(", ", group.Select(static i => i.Key));
            _log.LogWarning("Ignoring {Keys} in {Path}: these settings can only be set in user settings (they choose programs CsCode runs)", keys, group.Key);
            if (_disposed || _windowService?.Invoke() is not { } window) continue;
            var message = $"{group.Key} sets {keys}, which only user settings can set: they choose the programs CsCode runs. "
                + "The workspace's value is ignored; copy it into your user settings if you trust this folder.";
            _ = ShowNoticeAsync(window, message);
        }
    }

    private async Task ShowNoticeAsync(IWindowService window, string message)
    {
        try { await window.ShowMessageAsync(MessageSeverity.Warning, message).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogDebug(ex, "Could not show the ignored-settings notice"); }
    }

    private ConfigurationError? ComputeError()
    {
        foreach (var l in new[] { _user, _vscode, _workspace }.Concat(_folderLayers))
            if (l.Error is { } e) return new ConfigurationError(l.Path ?? l.Name, e);
        return null;
    }

    // ---- watching & reload -------------------------------------------------------------------------------------------------------

    private void ArmUserWatcher()
    {
        if (_watcher is null) return;
        try
        {
            var dir = Path.GetDirectoryName(UserSettingsPath)!;
            Directory.CreateDirectory(dir);
            _watches.Add(_watcher.Watch(FileUri.FromDirectoryPath(dir), recursive: false, excludeGlobs: [], changes => OnFilesChanged(changes, null)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Cannot watch the settings directory; changes to settings.json will not be picked up live");
        }
    }

    /// <summary>
    /// Watches every folder that contributes a layer: the primary one's .cscode and .vscode directories, every other folder's .cscode directory
    /// (lsp-core-7: only the primary folder used to be watched, so edits to an added folder's settings were never applied).
    /// </summary>
    private void ArmWorkspaceWatchers(IReadOnlyList<Uri> folders)
    {
        DisposableStore? old;
        lock (_gate) { old = _workspaceWatches; _workspaceWatches = null; }
        old?.Dispose();
        if (_watcher is null || folders.Count == 0 || _disposed) return;

        var store = new DisposableStore();
        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            if (!folder.IsFile) continue;
            try
            {
                var root = FileUri.ToPath(folder);
                // The folder itself (non-recursive) tells us when .cscode/.vscode appear or vanish; each existing dir is watched for its files.
                store.Add(_watcher.Watch(FileUri.FromDirectoryPath(root), recursive: false, excludeGlobs: [], changes => OnFilesChanged(changes, root)));
                foreach (var sub in i == 0 ? new[] { ".cscode", ".vscode" } : [".cscode"])
                {
                    var d = Path.Combine(root, sub);
                    if (Directory.Exists(d)) store.Add(_watcher.Watch(FileUri.FromDirectoryPath(d), recursive: false, excludeGlobs: [], changes => OnFilesChanged(changes, null)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.LogWarning(ex, "Cannot watch workspace settings for {Folder}", folder);
            }
        }
        lock (_gate) _workspaceWatches = store;
        if (_disposed) store.Dispose();
    }

    private void OnFilesChanged(IReadOnlyList<FileChange> changes, string? workspaceRoot)
    {
        var relevant = false;
        var rearm = false;
        foreach (var c in changes)
        {
            string p;
            try { p = FileUri.ToPath(c.Uri); } catch (NotSupportedException) { continue; }
            var name = Path.GetFileName(p);
            if (workspaceRoot is not null)
            {
                // events of the folder-level watch: only the two settings directories matter
                if (name is ".cscode" or ".vscode") { relevant = true; rearm = true; }
            }
            else if (name == SettingsFile) relevant = true;
        }
        if (!relevant) return;
        if (rearm) _lane.Post(() => { IReadOnlyList<Uri> f; lock (_gate) f = _folders; ArmWorkspaceWatchers(f); });
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        if (_disposed) return;
        try { _reloadTimer.Change(_options.ConfigReloadDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // Timer callbacks run on the shared pool: do nothing but hand the work to the IO lane.
    private void OnReloadTimer() { if (!_disposed) _lane.Post(ReloadAll, ex => _log.LogWarning(ex, "Settings reload failed")); }

    /// <summary>Re-reads every settings file now (normally triggered by the file watcher). Completes after values/events are published.</summary>
    public Task ReloadAsync(CancellationToken ct = default) => _lane.RunAsync(_ => ReloadAll(), ct);

    private void ReloadAll()
    {
        if (_disposed) return;
        Layer[] layers;
        lock (_gate) layers = new[] { _user, _vscode, _workspace }.Concat(_folderLayers).ToArray();
        var changed = false;
        foreach (var l in layers) changed |= LoadLayerFromDisk(l);
        if (changed) Publish();
    }

    // ---- workspace binding -------------------------------------------------------------------------------------------------------

    async ValueTask IWorkspaceBound.OnWorkspaceFoldersChangedAsync(IReadOnlyList<Uri> folders, CancellationToken ct)
    {
        await _lane.RunAsync(_ =>
        {
            lock (_gate)
            {
                var primary = folders.Count > 0 ? folders[0] : null;
                _folders = folders.ToArray();
                string? Settings(Uri? f, string sub) => f is null || !f.IsFile ? null : Path.Combine(FileUri.ToPath(f), sub, SettingsFile);
                _vscode.Path = Settings(primary, ".vscode");
                _workspace.Path = Settings(primary, ".cscode");
                _folderLayers = folders.Skip(1).Where(f => f.IsFile).Select(f => new Layer("folder", isWorkspace: true) { Path = Settings(f, ".cscode") }).ToList();
                // Different folder => different files: forget what we had parsed for the old ones.
                foreach (var l in new[] { _vscode, _workspace }) { l.Text = null; l.Tree = null; l.Error = null; }
            }
            Layer[] all;
            lock (_gate) all = new[] { _vscode, _workspace }.Concat(_folderLayers).ToArray();
            foreach (var l in all) LoadLayerFromDisk(l);
            ArmWorkspaceWatchers(folders);
            Publish();
        }, ct).ConfigureAwait(false);
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reloadTimer.Dispose();
        _watches.Dispose();
        DisposableStore? ws;
        lock (_gate) { ws = _workspaceWatches; _workspaceWatches = null; }
        ws?.Dispose();
        _changed.Clear();
        _errorChanged.Clear();
        _ignoredChanged.Clear();
    }

    // ---- snapshot ----------------------------------------------------------------------------------------------------------------

    /// <summary>Immutable merged view + lazily filled read caches (replaced wholesale on every change, so caches can never be stale).</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(new JsonObject(), new HashSet<string>(StringComparer.Ordinal), []);

        /// <summary>One settings file's last good tree; <paramref name="IsWorkspace"/> marks a workspace or folder file.</summary>
        public readonly record struct LayerTree(JsoncObject Tree, string Path, bool IsWorkspace);

        private readonly JsonObject _merged;
        private readonly HashSet<string> _atomic;
        private readonly ConcurrentDictionary<string, JsonElement?> _raw = new(StringComparer.Ordinal);
        public ConcurrentDictionary<(string, Type), object?> Typed { get; } = new();

        /// <summary>User-only settings that workspace or folder files set (and that were therefore ignored), in file order.</summary>
        public IReadOnlyList<IgnoredSetting> Ignored { get; }

        private Snapshot(JsonObject merged, HashSet<string> atomic, IReadOnlyList<IgnoredSetting> ignored) { _merged = merged; _atomic = atomic; Ignored = ignored; }

        public static Snapshot Build(IReadOnlyList<ConfigurationProperty> properties, IReadOnlyList<LayerTree> layers)
        {
            var atomic = new HashSet<string>(StringComparer.Ordinal);
            var merged = new JsonObject();

            // Defaults first, in registration order (a later registration of the same key overrides an earlier one).
            var defaults = new JsonObject();
            foreach (var p in properties)
            {
                if (p.Default is not { } d) continue;
                var node = d.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(d.GetRawText());
                if (node is JsonObject) atomic.Add(p.Key);
                else if (p.Schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "object") atomic.Add(p.Key);
                SetPath(defaults, p.Key, node);
            }
            foreach (var p in properties) // object-typed properties without a default are atomic too
                if (p.Schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "object") atomic.Add(p.Key);
            DeepMerge(merged, defaults);

            var scoped = UserOnly.From(properties);
            List<IgnoredSetting>? ignored = null;
            UserOnly.Saved? userValues = null;
            foreach (var layer in layers)
            {
                var normalized = new JsonObject();
                Expand(layer.Tree, normalized, splitDots: true);
                if (layer.IsWorkspace && !scoped.IsEmpty)
                {
                    // What defaults + user files say, before the first workspace file is merged: restored over whatever the workspace sets.
                    userValues ??= scoped.Save(merged);
                    scoped.Report(normalized, layer.Path, ref ignored);
                    scoped.StripLanguageBlocks(normalized);
                }
                DeepMerge(merged, normalized);
            }
            userValues?.Restore(merged);
            return new Snapshot(merged, atomic, ignored is null ? [] : ignored);
        }

        /// <summary>
        /// The user-only part of the registry: whole keys (<see cref="ConfigurationProperty.IsUserSettingOnly"/>) and machine-scoped members. Their
        /// effective value is taken from the defaults and the user layer: a workspace value is reported, then overwritten (or removed) after the merge,
        /// which also undoes a workspace that replaces a parent object with a scalar (<c>"remote": 1</c>).
        /// </summary>
        private sealed class UserOnly
        {
            private readonly List<(string Key, ConfigurationScope Scope)> _keys = new();
            private readonly List<(string Key, string[] Member)> _members = new();

            public bool IsEmpty => _keys.Count == 0 && _members.Count == 0;

            public static UserOnly From(IReadOnlyList<ConfigurationProperty> properties)
            {
                var u = new UserOnly();
                foreach (var p in properties)
                {
                    if (p.IsUserSettingOnly) u._keys.Add((p.Key, p.Scope));
                    foreach (var m in p.MachineScopedMembers ?? []) u._members.Add((p.Key, m.Split('.')));
                }
                return u;
            }

            public Saved Save(JsonObject merged)
            {
                var keys = _keys.Select(k => (k.Key, Value: NodeAt(merged, k.Key)?.DeepClone(), Present: HasPath(merged, k.Key))).ToList();
                var members = _members.Select(m => (m.Key, m.Member, Container: (NodeAt(merged, m.Key) as JsonObject)?.DeepClone() as JsonObject)).ToList();
                return new Saved(keys, members);
            }

            /// <summary>Lists what <paramref name="layer"/> (one normalized workspace file) sets at a user-only path.</summary>
            public void Report(JsonObject layer, string path, ref List<IgnoredSetting>? into)
            {
                foreach (var block in LanguageBlocks(layer).Prepend((Name: "", Block: layer)))
                {
                    var prefix = block.Name.Length == 0 ? "" : block.Name + ".";
                    foreach (var (key, scope) in _keys)
                        if (HasPath(block.Block, key) || ClobbersParent(block.Block, key)) (into ??= []).Add(new IgnoredSetting(path, prefix + key, scope));
                    foreach (var (key, member) in _members)
                        if (NodeAt(block.Block, key) is JsonObject container)
                            foreach (var hit in Matches(container, member, 0, key))
                                (into ??= []).Add(new IgnoredSetting(path, prefix + hit, ConfigurationScope.Machine));
                }
            }

            /// <summary>A workspace "[lang]" block never sets a user-only key (VS Code: application and machine settings cannot be language-specific).</summary>
            public void StripLanguageBlocks(JsonObject layer)
            {
                foreach (var (_, block) in LanguageBlocks(layer).ToArray())
                {
                    foreach (var (key, _) in _keys) RemovePath(block, key);
                    foreach (var (key, member) in _members)
                        if (NodeAt(block, key) is JsonObject container) RemoveMatches(container, member, 0);
                }
            }

            private static IEnumerable<(string Name, JsonObject Block)> LanguageBlocks(JsonObject layer)
            {
                foreach (var (name, value) in layer)
                    if (name.Length > 2 && name[0] == '[' && name[^1] == ']' && value is JsonObject o) yield return (name, o);
            }

            /// <summary>A workspace value that is not an object at a strict prefix of <paramref name="key"/> (it would hide the user's value).</summary>
            private static bool ClobbersParent(JsonObject root, string key)
            {
                JsonNode? cur = root;
                var segments = key.Split('.');
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (cur is not JsonObject o || !o.TryGetPropertyValue(segments[i], out cur)) return false;
                    if (cur is not JsonObject) return true;
                }
                return false;
            }

            private static IEnumerable<string> Matches(JsonObject container, string[] member, int i, string prefix)
            {
                foreach (var (name, value) in container.ToArray())
                {
                    if (member[i] != "*" && member[i] != name) continue;
                    var path = prefix + "." + name;
                    if (i == member.Length - 1) yield return path;
                    else if (value is JsonObject child)
                        foreach (var hit in Matches(child, member, i + 1, path)) yield return hit;
                }
            }

            private static void RemoveMatches(JsonObject container, string[] member, int i)
            {
                foreach (var (name, value) in container.ToArray())
                {
                    if (member[i] != "*" && member[i] != name) continue;
                    if (i == member.Length - 1) container.Remove(name);
                    else if (value is JsonObject child) RemoveMatches(child, member, i + 1);
                }
            }

            public sealed class Saved(List<(string Key, JsonNode? Value, bool Present)> keys, List<(string Key, string[] Member, JsonObject? Container)> members)
            {
                public void Restore(JsonObject merged)
                {
                    foreach (var (key, value, present) in keys)
                    {
                        if (present) SetPath(merged, key, value?.DeepClone());
                        else RemovePath(merged, key);
                    }
                    foreach (var (key, member, user) in members)
                        if (NodeAt(merged, key) is JsonObject container) RestoreMembers(container, user, member, 0);
                }

                private static void RestoreMembers(JsonObject container, JsonObject? user, string[] member, int i)
                {
                    var names = member[i] == "*" ? container.Select(static kv => kv.Key).ToArray() : [member[i]];
                    foreach (var name in names)
                    {
                        JsonNode? userValue = null;
                        var userHas = user is not null && user.TryGetPropertyValue(name, out userValue);
                        if (i == member.Length - 1)
                        {
                            if (userHas) container[name] = userValue?.DeepClone();
                            else container.Remove(name);
                        }
                        else if (container[name] is JsonObject child) RestoreMembers(child, userValue as JsonObject, member, i + 1);
                    }
                }
            }
        }

        private static JsonNode? NodeAt(JsonObject root, string key)
        {
            JsonNode? cur = root;
            foreach (var seg in key.Split('.'))
                if (cur is not JsonObject o || !o.TryGetPropertyValue(seg, out cur)) return null;
            return cur;
        }

        private static bool HasPath(JsonObject root, string key)
        {
            JsonNode? cur = root;
            var segments = key.Split('.');
            for (var i = 0; i < segments.Length; i++)
            {
                if (cur is not JsonObject o || !o.ContainsKey(segments[i])) return false;
                cur = o[segments[i]];
            }
            return true;
        }

        /// <summary>Removes the value at <paramref name="key"/> and the objects that only existed to hold it.</summary>
        private static void RemovePath(JsonObject root, string key)
        {
            var segments = key.Split('.');
            var chain = new List<JsonObject> { root };
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (chain[^1][segments[i]] is not JsonObject next) return;
                chain.Add(next);
            }
            if (!chain[^1].Remove(segments[^1])) return;
            for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--) chain[i - 1].Remove(segments[i - 1]);
        }

        public JsonElement? GetRaw(string key)
        {
            if (_raw.TryGetValue(key, out var cached)) return cached;
            var value = Compute(key);
            if (_raw.Count < MaxCachedKeys) _raw.TryAdd(key, value);
            return value;
        }

        private JsonElement? Compute(string key)
        {
            JsonNode? cur = _merged;
            foreach (var seg in key.Split('.'))
            {
                if (cur is not JsonObject o || !o.TryGetPropertyValue(seg, out cur)) return null;
            }
            return cur is null ? null : JsonSerializer.SerializeToElement(cur, CoreJson.Options);
        }

        /// <summary>Keys whose effective value differs between two snapshots (leaf paths; registered object settings count as one key).</summary>
        public static IReadOnlyCollection<string> Diff(Snapshot a, Snapshot b)
        {
            var left = new Dictionary<string, string>(StringComparer.Ordinal);
            var right = new Dictionary<string, string>(StringComparer.Ordinal);
            var atomic = new HashSet<string>(a._atomic, StringComparer.Ordinal);
            atomic.UnionWith(b._atomic);
            Flatten(a._merged, "", p => atomic.Contains(StripLanguagePrefix(p)), left);
            Flatten(b._merged, "", p => atomic.Contains(StripLanguagePrefix(p)), right);

            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (k, v) in left) if (!right.TryGetValue(k, out var rv) || rv != v) keys.Add(k);
            foreach (var k in right.Keys) if (!left.ContainsKey(k)) keys.Add(k);
            return keys;
        }

        private static void Flatten(JsonObject node, string prefix, Func<string, bool> isAtomic, Dictionary<string, string> into)
        {
            foreach (var (k, v) in node)
            {
                var path = prefix + k;
                if (v is JsonObject o && !isAtomic(path)) Flatten(o, path + ".", isAtomic, into);
                else into[path] = v?.ToJsonString() ?? "null";
            }
        }

        /// <summary>"[csharp].editor.tabSize" is checked against the registered "editor.tabSize".</summary>
        private static string StripLanguagePrefix(string path)
        {
            if (path.Length > 0 && path[0] == '[')
            {
                var end = path.IndexOf("].", StringComparison.Ordinal);
                if (end > 0) return path[(end + 2)..];
            }
            return path;
        }

        /// <summary>
        /// Normalises one file into a nested tree, as VS Code does: a top-level dotted key expands to nested objects ("editor.tabSize" →
        /// editor/tabSize), and so does a key directly inside a "[lang]" block; every VALUE is copied as written, so member names inside it
        /// (the globs of files.associations, the URLs of yaml.schemas, server ids) are never split (lsp-core-2). A value that lands on an object
        /// created by an earlier dotted key is merged into it ({"editor.tabSize": 2, "editor": {"fontSize": 9}} keeps both).
        /// </summary>
        private static void Expand(JsoncObject src, JsonObject dst, bool splitDots)
        {
            foreach (var m in src.Members)
            {
                var isLanguageBlock = m.Name.Length > 2 && m.Name[0] == '[' && m.Name[^1] == ']';
                var segments = splitDots && !isLanguageBlock ? m.Name.Split('.') : [m.Name];
                var target = dst;
                for (var i = 0; i < segments.Length - 1; i++) target = GetOrCreateObject(target, segments[i]);
                var last = segments[^1];

                if (splitDots && isLanguageBlock && m.Value is JsoncObject block)
                {
                    Expand(block, GetOrCreateObject(target, last), splitDots: true);
                    continue;
                }
                var node = JsoncParser.ToJsonNode(m.Value);
                if (node is JsonObject incoming && target[last] is JsonObject existing) DeepMerge(existing, incoming);
                else target[last] = node;
            }
        }

        private static JsonObject GetOrCreateObject(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject existing) return existing;
            var created = new JsonObject();
            parent[name] = created;
            return created;
        }

        private static void SetPath(JsonObject root, string dottedKey, JsonNode? value)
        {
            var segments = dottedKey.Split('.');
            var target = root;
            for (var i = 0; i < segments.Length - 1; i++) target = GetOrCreateObject(target, segments[i]);
            target[segments[^1]] = value;
        }

        private static void DeepMerge(JsonObject dst, JsonObject src)
        {
            foreach (var (k, v) in src)
            {
                if (v is JsonObject vo && dst[k] is JsonObject existing) DeepMerge(existing, vo);
                else dst[k] = v?.DeepClone();
            }
        }
    }
}
