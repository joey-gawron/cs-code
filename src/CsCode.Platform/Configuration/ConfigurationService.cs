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

public sealed class ConfigurationFileException(string path, string message, Exception? inner = null)
    : IOException($"{message} ({path})", inner)
{
    public string Path { get; } = path;
}

/// <summary>
/// Layered settings: registered defaults &lt; user <c>settings.json</c> &lt; workspace (<c>.vscode/settings.json</c>, then
/// <c>.cscode/settings.json</c> of the primary folder) &lt; additional workspace folders' <c>.cscode/settings.json</c>.
/// <list type="bullet">
/// <item>JSONC files; <c>"editor.fontSize"</c> and <c>{"editor":{"fontSize":…}}</c> are equivalent. Registered object-valued settings
/// (<c>files.exclude</c>…) stay whole so their member names (globs) are never split on dots; objects merge across layers, arrays/scalars replace.</item>
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

    private sealed class Layer(string name)
    {
        public string Name { get; } = name;
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
    private readonly Layer _user = new("user");
    private readonly Layer _vscode = new("workspace(.vscode)");
    private readonly Layer _workspace = new("workspace");
    private List<Layer> _folderLayers = new();
    private readonly List<Registration> _registrations = new();
    private DisposableStore? _workspaceWatches;
    private Snapshot _snapshot;
    private ConfigurationError? _lastError;
    private Uri? _primaryFolder;
    private bool _disposed;

    public ConfigurationService(IAppPaths paths, IoLane lane, IFileWatcher? watcher = null, PlatformOptions? options = null, ILogger<ConfigurationService>? logger = null)
    {
        _paths = paths;
        _lane = lane;
        _watcher = watcher;
        _options = options ?? new PlatformOptions();
        _log = logger ?? NullLogger<ConfigurationService>.Instance;
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
        var node = ToNode(value);
        return new ValueTask(_lane.RunAsync(token => UpdateCore(path, key, node, token), ct));
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
        lock (_publishGate)
        {
            List<JsoncObject> trees;
            List<ConfigurationProperty> props;
            lock (_gate)
            {
                trees = new List<JsoncObject>();
                foreach (var l in new[] { _vscode, _workspace }.Concat(_folderLayers)) if (l.Tree is not null) trees.Add(l.Tree);
                if (_user.Tree is not null) trees.Insert(0, _user.Tree); // user sits below every workspace layer
                props = _registrations.SelectMany(static r => r.Properties).ToList();
            }

            var next = Snapshot.Build(props, trees);
            var old = Interlocked.Exchange(ref _snapshot, next);
            if (raise)
            {
                var keys = Snapshot.Diff(old, next);
                if (keys.Count > 0) affected = keys;
            }
            lock (_gate) error = ComputeError();
            errorChanged = !Equals(error, _lastError);
            _lastError = error;
        }

        if (affected is not null) _changed.Fire(new ConfigurationChangedEventArgs(affected));
        if (errorChanged && raise) _errorChanged.Fire(error);
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

    private void ArmWorkspaceWatchers(Uri? folder)
    {
        DisposableStore? old;
        lock (_gate) { old = _workspaceWatches; _workspaceWatches = null; }
        old?.Dispose();
        if (_watcher is null || folder is null || _disposed) return;

        var store = new DisposableStore();
        try
        {
            var root = FileUri.ToPath(folder);
            // The folder itself (non-recursive) tells us when .cscode/.vscode appear or vanish; each existing dir is watched for its files.
            store.Add(_watcher.Watch(FileUri.FromDirectoryPath(root), recursive: false, excludeGlobs: [], changes => OnFilesChanged(changes, root)));
            foreach (var sub in new[] { ".cscode", ".vscode" })
            {
                var d = Path.Combine(root, sub);
                if (Directory.Exists(d)) store.Add(_watcher.Watch(FileUri.FromDirectoryPath(d), recursive: false, excludeGlobs: [], changes => OnFilesChanged(changes, null)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogWarning(ex, "Cannot watch workspace settings for {Folder}", folder);
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
        if (rearm) _lane.Post(() => { Uri? f; lock (_gate) f = _primaryFolder; ArmWorkspaceWatchers(f); });
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
                _primaryFolder = primary;
                string? Settings(Uri? f, string sub) => f is null || !f.IsFile ? null : Path.Combine(FileUri.ToPath(f), sub, SettingsFile);
                _vscode.Path = Settings(primary, ".vscode");
                _workspace.Path = Settings(primary, ".cscode");
                _folderLayers = folders.Skip(1).Where(f => f.IsFile).Select(f => new Layer("folder") { Path = Settings(f, ".cscode") }).ToList();
                // Different folder => different files: forget what we had parsed for the old ones.
                foreach (var l in new[] { _vscode, _workspace }) { l.Text = null; l.Tree = null; l.Error = null; }
            }
            Layer[] all;
            lock (_gate) all = new[] { _vscode, _workspace }.Concat(_folderLayers).ToArray();
            foreach (var l in all) LoadLayerFromDisk(l);
            ArmWorkspaceWatchers(folders.Count > 0 ? folders[0] : null);
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
    }

    // ---- snapshot ----------------------------------------------------------------------------------------------------------------

    /// <summary>Immutable merged view + lazily filled read caches (replaced wholesale on every change, so caches can never be stale).</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(new JsonObject(), new HashSet<string>(StringComparer.Ordinal));

        private readonly JsonObject _merged;
        private readonly HashSet<string> _atomic;
        private readonly ConcurrentDictionary<string, JsonElement?> _raw = new(StringComparer.Ordinal);
        public ConcurrentDictionary<(string, Type), object?> Typed { get; } = new();

        private Snapshot(JsonObject merged, HashSet<string> atomic) { _merged = merged; _atomic = atomic; }

        public static Snapshot Build(IReadOnlyList<ConfigurationProperty> properties, IReadOnlyList<JsoncObject> layers)
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

            bool IsAtomic(string path) => atomic.Contains(StripLanguagePrefix(path));
            foreach (var layer in layers)
            {
                var normalized = new JsonObject();
                Expand(layer, normalized, "", IsAtomic);
                DeepMerge(merged, normalized);
            }
            return new Snapshot(merged, atomic);
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

        /// <summary>Normalises one file into a nested tree: dotted keys expand to nested objects; registered object settings are kept whole.</summary>
        private static void Expand(JsoncObject src, JsonObject dst, string prefix, Func<string, bool> isAtomic)
        {
            foreach (var m in src.Members)
            {
                var segments = m.Name.Split('.');
                var target = dst;
                for (var i = 0; i < segments.Length - 1; i++) target = GetOrCreateObject(target, segments[i]);
                var last = segments[^1];
                var path = prefix + m.Name;

                if (m.Value is JsoncObject vo && !isAtomic(path)) Expand(vo, GetOrCreateObject(target, last), path + ".", isAtomic);
                else target[last] = JsoncParser.ToJsonNode(m.Value);
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
