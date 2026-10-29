using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Breakpoints;

// Persisted shape (primitive types only so any IStateStorage serialiser round-trips it).
public sealed record PersistedSourceBreakpoint(string Id, string Uri, int Line, int? Column, string? Condition, string? HitCondition, string? LogMessage, bool Enabled);
public sealed record PersistedFunctionBreakpoint(string Id, string Name, string? Condition, string? HitCondition, bool Enabled);
public sealed record PersistedExceptionFilter(string Filter, string Label, bool Enabled, string? Condition);
public sealed record PersistedBreakpoints(
    List<PersistedSourceBreakpoint>? Source = null,
    List<PersistedFunctionBreakpoint>? Functions = null,
    List<PersistedExceptionFilter>? ExceptionFilters = null);

/// <summary>
/// Source of truth for breakpoints (ARCHITECTURE: DAP sessions only MIRROR it). Thread-safe, all state changes are
/// copy-on-write snapshots so readers (editor gutter on the UI thread) never block on a writer. Persisted per workspace in
/// <see cref="IStateStorage"/> with a debounce (the file is not rewritten once per click).
/// </summary>
public sealed class BreakpointManager : IBreakpointManager, IAsyncDisposable
{
    public const string StateKey = "debug.breakpoints";
    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(400);

    private readonly object _gate = new();
    private readonly Emitter<BreakpointsChangedEventArgs> _changed = new();
    private readonly IStateStorage? _storage;
    private readonly ILogger _logger;
    private readonly DisposableStore _subs = new();
    // breakpoint id → (session id → verdict). Per session, so one session ending never erases another live session's verdict.
    private readonly Dictionary<string, Dictionary<string, (long Seq, DebugBreakpointStatus Status)>> _statuses = new();
    private long _statusSeq;
    private SourceBreakpoint[] _source = [];
    private FunctionBreakpoint[] _functions = [];
    private ExceptionFilterSetting[] _filters = [];
    private Timer? _persistTimer;
    private bool _dirty;
    private int _disposed;

    public BreakpointManager(IStateStorage? storage = null, IWorkspaceService? workspace = null, ILogger? logger = null)
    {
        _storage = storage;
        _logger = logger ?? NullLogger.Instance;
        Load();
        if (workspace is not null)
            _subs.Add(workspace.FoldersChanged.Subscribe(_ => Reload()));
    }

    public IEventSource<BreakpointsChangedEventArgs> Changed => _changed;
    public IReadOnlyList<SourceBreakpoint> SourceBreakpoints => Volatile.Read(ref _source);
    public IReadOnlyList<FunctionBreakpoint> FunctionBreakpoints => Volatile.Read(ref _functions);
    public IReadOnlyList<ExceptionFilterSetting> ExceptionFilters => Volatile.Read(ref _filters);

    /// <summary>True when any breakpoint (source or function) exists; drives the <c>breakpointsExist</c> context key.</summary>
    public bool Any => SourceBreakpoints.Count > 0 || FunctionBreakpoints.Count > 0;

    public IReadOnlyList<SourceBreakpoint> GetForUri(Uri uri) => [.. SourceBreakpoints.Where(b => b.Uri == uri)];

    // ------------------------------------------------------------------ source breakpoints

    public bool Toggle(Uri uri, int line)
    {
        bool now;
        lock (_gate)
        {
            var existing = _source.Where(b => b.Uri == uri && b.Line == line).ToArray();
            if (existing.Length > 0)
            {
                _source = [.. _source.Where(b => !existing.Contains(b))];
                foreach (var b in existing) _statuses.Remove(b.Id);
                now = false;
            }
            else
            {
                _source = [.. _source, new SourceBreakpoint(NewId(), uri, line)];
                now = true;
            }
            _dirty = true;
        }
        Raise([uri], false);
        return now;
    }

    public SourceBreakpoint Add(Uri uri, int line, string? condition = null, string? hitCondition = null, string? logMessage = null)
    {
        SourceBreakpoint result;
        lock (_gate)
        {
            // One breakpoint per (uri, line): adding again updates it (VS Code behaviour) instead of stacking duplicates.
            var existing = _source.FirstOrDefault(b => b.Uri == uri && b.Line == line && b.Column is null);
            if (existing is not null)
            {
                result = existing with { Condition = condition, HitCondition = hitCondition, LogMessage = logMessage, Enabled = true };
                _source = [.. _source.Select(b => b.Id == existing.Id ? result : b)];
            }
            else
            {
                result = new SourceBreakpoint(NewId(), uri, line, null, condition, hitCondition, logMessage);
                _source = [.. _source, result];
            }
            _dirty = true;
        }
        Raise([uri], false);
        return result;
    }

    public void Update(SourceBreakpoint updated)
    {
        Uri? oldUri;
        lock (_gate)
        {
            var current = _source.FirstOrDefault(b => b.Id == updated.Id);
            if (current is null) return;
            oldUri = current.Uri;
            _source = [.. _source.Select(b => b.Id == updated.Id ? updated : b)];
            _dirty = true;
        }
        Raise(oldUri == updated.Uri ? [updated.Uri] : [oldUri, updated.Uri], false);
    }

    public void Remove(string id)
    {
        Uri? uri;
        lock (_gate)
        {
            var current = _source.FirstOrDefault(b => b.Id == id);
            if (current is null) return;
            uri = current.Uri;
            _source = [.. _source.Where(b => b.Id != id)];
            _statuses.Remove(id);
            _dirty = true;
        }
        Raise([uri], false);
    }

    public void RemoveAll()
    {
        Uri[] uris;
        bool hadFunctions;
        lock (_gate)
        {
            uris = [.. _source.Select(b => b.Uri).Distinct()];
            hadFunctions = _functions.Length > 0;
            if (uris.Length == 0 && !hadFunctions) return;
            _source = [];
            _functions = [];
            _statuses.Clear();
            _dirty = true;
        }
        Raise(uris, hadFunctions);
    }

    public void SetEnabledAll(bool enabled)
    {
        Uri[] uris;
        bool functions;
        lock (_gate)
        {
            uris = [.. _source.Where(b => b.Enabled != enabled).Select(b => b.Uri).Distinct()];
            functions = _functions.Any(f => f.Enabled != enabled);
            if (uris.Length == 0 && !functions) return;
            _source = [.. _source.Select(b => b with { Enabled = enabled })];
            _functions = [.. _functions.Select(f => f with { Enabled = enabled })];
            _dirty = true;
        }
        Raise(uris, functions);
    }

    // ------------------------------------------------------------------ function / exception breakpoints

    public FunctionBreakpoint AddFunction(string name, string? condition = null)
    {
        FunctionBreakpoint fb;
        lock (_gate)
        {
            var existing = _functions.FirstOrDefault(f => f.Name == name);
            if (existing is not null)
            {
                fb = existing with { Condition = condition, Enabled = true };
                _functions = [.. _functions.Select(f => f.Id == existing.Id ? fb : f)];
            }
            else
            {
                fb = new FunctionBreakpoint(NewId(), name, condition);
                _functions = [.. _functions, fb];
            }
            _dirty = true;
        }
        Raise([], true);
        return fb;
    }

    public void RemoveFunction(string id)
    {
        lock (_gate)
        {
            if (_functions.All(f => f.Id != id)) return;
            _functions = [.. _functions.Where(f => f.Id != id)];
            _statuses.Remove(id);
            _dirty = true;
        }
        Raise([], true);
    }

    public void SetExceptionFilters(IReadOnlyList<ExceptionFilterSetting> filters)
    {
        lock (_gate)
        {
            if (_filters.SequenceEqual(filters)) return;
            _filters = [.. filters];
            _dirty = true;
        }
        Raise([], true);
    }

    // ------------------------------------------------------------------ adapter verdicts

    public DebugBreakpointStatus? GetStatus(string breakpointId)
    {
        lock (_gate)
            return _statuses.TryGetValue(breakpointId, out var perSession) && perSession.Count > 0
                ? perSession.Values.MaxBy(v => v.Seq).Status
                : null;
    }

    public void ReportStatus(DebugBreakpointStatus status)
    {
        Uri[] uris = [];
        lock (_gate)
        {
            var bp = _source.FirstOrDefault(b => b.Id == status.BreakpointId);
            var isFunction = bp is null && _functions.Any(f => f.Id == status.BreakpointId);
            if (bp is null && !isFunction) return; // the breakpoint was removed while the adapter was answering
            if (!_statuses.TryGetValue(status.BreakpointId, out var perSession)) _statuses[status.BreakpointId] = perSession = new();
            if (perSession.TryGetValue(status.Session.Id, out var old) && Same(old.Status, status)) return;
            perSession[status.Session.Id] = (++_statusSeq, status);
            if (bp is not null) uris = [bp.Uri];
        }
        Raise(uris, uris.Length == 0);
    }

    private static bool Same(DebugBreakpointStatus a, DebugBreakpointStatus b) =>
        ReferenceEquals(a.Session, b.Session) && a.Verified == b.Verified && a.ActualLine == b.ActualLine && a.Message == b.Message;

    /// <summary>
    /// Forgets verdicts of a session (null = all). A session MUST call this when it ends: a status holds the session object, so
    /// leaving it behind would pin a dead session (and its adapter client) in memory.
    /// </summary>
    public void ClearStatuses(string? sessionId = null)
    {
        Uri[] uris;
        lock (_gate)
        {
            var touched = new HashSet<string>();
            foreach (var (bpId, perSession) in _statuses)
            {
                if (sessionId is null) { if (perSession.Count > 0) touched.Add(bpId); perSession.Clear(); }
                else if (perSession.Remove(sessionId)) touched.Add(bpId);
            }
            foreach (var empty in _statuses.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToArray()) _statuses.Remove(empty);
            if (touched.Count == 0) return;
            uris = [.. _source.Where(b => touched.Contains(b.Id)).Select(b => b.Uri).Distinct()];
        }
        Raise(uris, false);
    }

    /// <summary>Number of retained verdicts (tests assert this returns to 0 after sessions end).</summary>
    public int StatusCount { get { lock (_gate) return _statuses.Values.Sum(v => v.Count); } }

    // ------------------------------------------------------------------ line tracking

    /// <summary>
    /// The editor reports an edit as "lines at or after <paramref name="fromLine"/> moved by <paramref name="delta"/>".
    /// <paramref name="fromLine"/> is the first line AFTER the edit position. Insertion (delta &gt; 0): breakpoints on lines
    /// &gt;= fromLine move down. Deletion (delta &lt; 0): lines [fromLine, fromLine - delta) vanished, so breakpoints on them are
    /// removed, and breakpoints below move up by |delta|.
    /// </summary>
    public void ApplyLineShift(Uri uri, int fromLine, int delta)
    {
        if (delta == 0) return;
        lock (_gate)
        {
            var changed = false;
            var next = new List<SourceBreakpoint>(_source.Length);
            foreach (var b in _source)
            {
                if (b.Uri != uri || b.Line < fromLine) { next.Add(b); continue; }
                changed = true;
                if (delta < 0 && b.Line < fromLine - delta) { _statuses.Remove(b.Id); continue; } // its line was deleted
                next.Add(b with { Line = b.Line + delta });
            }
            if (!changed) return;
            _source = [.. next];
            _dirty = true;
        }
        Raise([uri], false);
    }

    // ------------------------------------------------------------------ persistence

    private static string NewId() => Guid.NewGuid().ToString("N");

    private void Raise(IReadOnlyCollection<Uri> uris, bool functionsOrExceptions)
    {
        SchedulePersist();
        if (uris.Count == 0 && !functionsOrExceptions) return;
        _changed.Fire(new BreakpointsChangedEventArgs(uris, functionsOrExceptions));
    }

    private void SchedulePersist()
    {
        if (_storage is null || Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (!_dirty) return;
            _persistTimer ??= new Timer(static s => ((BreakpointManager)s!).PersistNow(), this, Timeout.Infinite, Timeout.Infinite);
            _persistTimer.Change(PersistDelay, Timeout.InfiniteTimeSpan); // re-arming = debounce
        }
    }

    private void PersistNow()
    {
        if (_storage is null) return;
        PersistedBreakpoints dto;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            dto = new PersistedBreakpoints(
                [.. _source.Select(b => new PersistedSourceBreakpoint(b.Id, b.Uri.ToString(), b.Line, b.Column, b.Condition, b.HitCondition, b.LogMessage, b.Enabled))],
                [.. _functions.Select(f => new PersistedFunctionBreakpoint(f.Id, f.Name, f.Condition, f.HitCondition, f.Enabled))],
                [.. _filters.Select(f => new PersistedExceptionFilter(f.Filter, f.Label, f.Enabled, f.Condition))]);
        }
        try { _storage.Set(StateKey, dto, StorageScope.Workspace); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist breakpoints"); }
    }

    private void Load()
    {
        if (_storage is null) return;
        PersistedBreakpoints? dto;
        try { dto = _storage.Get<PersistedBreakpoints>(StateKey, StorageScope.Workspace); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read persisted breakpoints");
            return;
        }
        if (dto is null) return;
        lock (_gate)
        {
            _source = [.. (dto.Source ?? []).Select(Restore).Where(b => b is not null)!];
            _functions = [.. (dto.Functions ?? []).Select(f => new FunctionBreakpoint(f.Id, f.Name, f.Condition, f.HitCondition, f.Enabled))];
            _filters = [.. (dto.ExceptionFilters ?? []).Select(f => new ExceptionFilterSetting(f.Filter, f.Label, f.Enabled, f.Condition))];
            _dirty = false;
        }
    }

    private static SourceBreakpoint? Restore(PersistedSourceBreakpoint p) =>
        Uri.TryCreate(p.Uri, UriKind.Absolute, out var uri)
            ? new SourceBreakpoint(p.Id, uri, p.Line, p.Column, p.Condition, p.HitCondition, p.LogMessage, p.Enabled)
            : null;

    /// <summary>The workspace changed: breakpoints are per workspace, so swap to the new workspace's set.</summary>
    public void Reload()
    {
        Uri[] before;
        lock (_gate) { before = [.. _source.Select(b => b.Uri)]; _statuses.Clear(); }
        PersistNow(); // flush the old workspace's pending edits first
        Load();
        var uris = before.Concat(SourceBreakpoints.Select(b => b.Uri)).Distinct().ToArray();
        _changed.Fire(new BreakpointsChangedEventArgs(uris, true));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _persistTimer?.Dispose();
        PersistNow();
        if (_storage is not null)
        {
            try { await _storage.FlushAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Flushing breakpoint state"); }
        }
        _subs.Dispose();
        _changed.Clear();
    }
}
