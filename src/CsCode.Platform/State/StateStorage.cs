using System.Text;
using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.State;

/// <summary>
/// Durable JSON key/value state in two scopes: <see cref="StorageScope.Application"/> (<c>{GlobalStorageDir}/state.json</c>) and
/// <see cref="StorageScope.Workspace"/> (<c>{WorkspaceStorageDir(folder)}/state.json</c>, switched when the folder changes).
/// <list type="bullet">
/// <item>Reads/writes are in-memory (short lock). Writes mark the scope dirty and are flushed to disk after a quiet period
/// (<see cref="PlatformOptions.StateFlushDelay"/>, never later than <see cref="PlatformOptions.StateMaxUnsaved"/> after the first unsaved change), on <see cref="FlushAsync"/> and on dispose. Flushing runs on the IO lane.</item>
/// <item>Every flush is atomic (temp + fsync + rename). Before a good file is replaced it is copied to <c>state.json.bak</c>; a corrupt
/// file is recovered from the .bak (and kept as <c>state.json.corrupt</c> for inspection), else state starts empty — it never throws.</item>
/// <item>Values are serialised when <c>Set</c> is called (so later mutation of the object cannot change what is saved). That is a few
/// microseconds for typical values; callers storing large documents should serialise them on their own lane and pass a <see cref="JsonElement"/>.</item>
/// <item>Setting a null value removes the key.</item>
/// </list>
/// The application scope is read synchronously in the constructor (a small file, at startup); workspace scopes are loaded on the lane when the folder opens.
/// </summary>
public sealed class StateStorage : IStateStorage, IWorkspaceBound, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);

    private sealed class Scope(string? path)
    {
        public string? Path { get; } = path;
        public readonly Dictionary<string, JsonElement> Values = new(StringComparer.Ordinal);
        public bool Dirty;
        public bool MainFileKnownGood;
    }

    private readonly IAppPaths _paths;
    private readonly IoLane _lane;
    private readonly PlatformOptions _options;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly Timer _timer;
    private Scope _app;
    private Scope _workspace = new(null);
    private long _firstDirtyTick;
    private bool _disposed;

    public StateStorage(IAppPaths paths, IoLane lane, PlatformOptions? options = null, ILogger<StateStorage>? logger = null)
    {
        _paths = paths;
        _lane = lane;
        _options = options ?? new PlatformOptions();
        _log = logger ?? NullLogger<StateStorage>.Instance;
        _app = LoadScope(Path.Combine(paths.GlobalStorageDir, "state.json"));
        _timer = new Timer(static s => ((StateStorage)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
    }

    // ---- IStateStorage -----------------------------------------------------------------------------------------------------------

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        JsonElement element;
        lock (_gate)
        {
            if (!ScopeOf(scope).Values.TryGetValue(key, out element)) return default;
        }
        try { return element.Deserialize<T>(CoreJson.Options); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            _log.LogWarning("State value '{Key}' cannot be read as {Type}: {Message}", key, typeof(T).Name, ex.Message);
            return default;
        }
    }

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (value is null) { Remove(key, scope); return; }
        var element = value is JsonElement je ? je.Clone() : JsonSerializer.SerializeToElement(value, CoreJson.Options);
        lock (_gate)
        {
            var s = ScopeOf(scope);
            if (s.Values.TryGetValue(key, out var old) && old.GetRawText() == element.GetRawText()) return; // unchanged: no write
            s.Values[key] = element;
            MarkDirty(s);
        }
        ScheduleFlush();
    }

    public void Remove(string key, StorageScope scope = StorageScope.Application)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            var s = ScopeOf(scope);
            if (!s.Values.Remove(key)) return;
            MarkDirty(s);
        }
        ScheduleFlush();
    }

    private Scope ScopeOf(StorageScope scope) => scope == StorageScope.Workspace ? _workspace : _app; // caller holds _gate

    private void MarkDirty(Scope s)
    {
        s.Dirty = true;
        if (_firstDirtyTick == 0) _firstDirtyTick = Environment.TickCount64;
    }

    // ---- flushing ----------------------------------------------------------------------------------------------------------------

    private void ScheduleFlush()
    {
        if (_disposed) return;
        long first;
        lock (_gate) first = _firstDirtyTick == 0 ? Environment.TickCount64 : _firstDirtyTick;
        var waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - first);
        var due = _options.StateFlushDelay;
        var remainingBudget = _options.StateMaxUnsaved - waited;
        if (remainingBudget < due) due = remainingBudget > TimeSpan.Zero ? remainingBudget : TimeSpan.Zero;
        try { _timer.Change(due, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    // Timer callbacks run on the shared pool: do nothing but queue the real work on the IO lane.
    private void OnTimer()
    {
        if (_disposed) return;
        _lane.Post(() => FlushCore(), ex => _log.LogWarning(ex, "State flush failed"));
    }

    /// <summary>Writes every dirty scope to disk now (on the IO lane) and completes when that is done.</summary>
    public Task FlushAsync() => _lane.RunAsync(_ => FlushCore());

    private bool FlushCore()
    {
        lock (_flushGate)
        {
            var ok = true;
            Scope[] scopes;
            lock (_gate) scopes = [_app, _workspace];
            foreach (var scope in scopes) ok &= FlushScope(scope);
            lock (_gate) { if (!scopes.Any(s => s.Dirty)) _firstDirtyTick = 0; }
            if (!ok && !_disposed) { try { _timer.Change(RetryAfterFailure, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
            return ok;
        }
    }

    private bool FlushScope(Scope scope)
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!scope.Dirty || scope.Path is null) { scope.Dirty = false; return true; }
            bytes = Serialize(scope.Values);
            scope.Dirty = false; // changes made while we write set it again
        }

        try
        {
            if (scope.MainFileKnownGood && File.Exists(scope.Path))
            {
                // Keep the last GOOD file as .bak: never refreshed from a file we have not verified.
                try { File.Copy(scope.Path, scope.Path + ".bak", overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogDebug(ex, "Could not refresh {Path}.bak", scope.Path); }
            }
            AtomicFile.Write(scope.Path, bytes, createParents: true, CancellationToken.None);
            scope.MainFileKnownGood = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not save state to {Path}; will retry", scope.Path);
            lock (_gate) scope.Dirty = true;
            return false;
        }
    }

    private static byte[] Serialize(Dictionary<string, JsonElement> values)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var key in values.Keys.Order(StringComparer.Ordinal)) // stable output: diff-friendly, and identical state writes identical bytes
            {
                w.WritePropertyName(key);
                values[key].WriteTo(w);
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    // ---- loading / recovery ------------------------------------------------------------------------------------------------------

    private Scope LoadScope(string? path)
    {
        var scope = new Scope(path);
        if (path is null) return scope;

        var (loaded, existed) = TryRead(path, scope);
        if (loaded) { scope.MainFileKnownGood = true; return scope; }

        if (existed)
        {
            _log.LogWarning("State file {Path} is corrupt; trying {Path}.bak", path, path);
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var (bakLoaded, _) = TryRead(path + ".bak", scope);
        if (bakLoaded)
        {
            _log.LogWarning("Recovered state from {Path}.bak", path);
            scope.Dirty = true; // rewrite a healthy main file at the next flush
            return scope;
        }
        if (existed) _log.LogError("State file {Path} and its backup are unusable; starting with empty state", path);
        return scope;
    }

    private (bool Loaded, bool Existed) TryRead(string file, Scope into)
    {
        byte[] bytes;
        try { bytes = AtomicFile.ReadAll(file, CancellationToken.None); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return (false, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Cannot read {Path}", file); return (false, true); }

        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (false, true);
            into.Values.Clear();
            foreach (var p in doc.RootElement.EnumerateObject()) into.Values[p.Name] = p.Value.Clone();
            return (true, true);
        }
        catch (JsonException)
        {
            return (false, true);
        }
    }

    // ---- workspace scope ---------------------------------------------------------------------------------------------------------

    async ValueTask IWorkspaceBound.OnWorkspaceFoldersChangedAsync(IReadOnlyList<Uri> folders, CancellationToken ct)
    {
        var dir = folders.Count > 0 ? _paths.WorkspaceStorageDir(folders[0]) : null;
        var path = dir is null ? null : Path.Combine(dir, "state.json");

        // Save what the old workspace had, then swap in the new one (loaded off the UI thread) in one step.
        await FlushAsync().ConfigureAwait(false);
        var next = await _lane.RunAsync(_ => LoadScope(path), ct).ConfigureAwait(false);
        lock (_gate) _workspace = next;
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _timer.DisposeAsync().ConfigureAwait(false);
        try { await _lane.RunAsync(_ => FlushCore()).ConfigureAwait(false); }
        catch (ObjectDisposedException) { FlushCore(); } // the lane is already gone (shutdown order): write on this thread
    }

    /// <summary>Synchronous shutdown path: flushes on the calling thread (blocking file I/O, acceptable at process exit). Prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        FlushCore();
    }
}
