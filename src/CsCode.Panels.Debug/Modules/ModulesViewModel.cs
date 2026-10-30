using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui.Collections;

namespace CsCode.Panels.Debug.Modules;

/// <summary>One loaded module (assembly / shared library) of the debuggee.</summary>
public sealed record ModuleRow(string Name, string? Path, string? Version, bool? IsUserCode, string? SymbolStatus)
{
    public string UserCode => IsUserCode switch { true => "Yes", false => "No", _ => "" };
}

/// <summary>
/// Modules window, refreshed on every stop and on demand; bounded to <see cref="MaxModules"/> rows. A session that implements
/// <see cref="IDebugSessionModules"/> (the DAP module's does) supplies the list it keeps from the adapter's <c>module</c> events, sending the
/// <c>modules</c> request only when the adapter advertises it: most adapters, netcoredbg included, do not implement that request. Its list is
/// also re-read (no request) whenever it announces a change, so modules loaded while the debuggee runs show up at once; the window only
/// exists while it is shown (the tool window disposes it when hidden), so a hidden window does no work for them. Any other
/// session is asked with the raw DAP request through <c>CustomRequestAsync</c>; one that refused it is not asked again on every stop (Refresh
/// still retries).
/// </summary>
public sealed class ModulesViewModel : ObservableObject, IDisposable
{
    public const int MaxModules = 2_000;

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private readonly string _key = "debug.modules." + Interlocked.Increment(ref s_ids);
    private static int s_ids;
    // The focused session's ModulesChanged subscription (released at a session change, the end of the session and Dispose).
    private IDebugSessionModules? _watched;
    private IDisposable? _watch;
    private CancellationTokenSource _cts = new();
    private long _generation;
    private long _loadedEpoch = -1;
    private DebugFocus _focus;
    private string? _status = "Not debugging";
    private ModuleRow? _selected;
    // The last session that refused the raw `modules` request (weakly: the window must not keep an ended session alive).
    private WeakReference<IDebugSession>? _refused;
    private int _disposed;

    public ModulesViewModel(DebugPanelServices services)
    {
        _s = services;
        RefreshCommand = new UiCommand(RefreshAsync);
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowStatus));
        _tracker = new DebugFocusTracker(services, OnFocus);
        _tracker.Start();
    }

    public BulkObservableCollection<ModuleRow> Rows { get; } = new();
    public ModuleRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }
    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => Rows.Count == 0 && !string.IsNullOrEmpty(_status);
    public UiCommand RefreshCommand { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void OnFocus(DebugFocus focus)
    {
        if (IsDisposed) return;
        _focus = focus;
        var ended = focus.Session is null || focus.State == DebugSessionState.Terminated;
        Watch(ended ? null : focus.Session as IDebugSessionModules);
        if (focus.Session is null || ended)
        {
            Cancel();
            _loadedEpoch = -1;
            Rows.Clear();
            StatusText = "Not debugging";
            return;
        }
        if (!focus.IsStopped || focus.Epoch == _loadedEpoch) return; // the list stays while running; it refreshes at the next stop
        _loadedEpoch = focus.Epoch;
        _ = LoadAsync(focus.Session);
    }

    private void Watch(IDebugSessionModules? session)
    {
        if (ReferenceEquals(_watched, session)) return;
        _watch?.Dispose();
        _watched = session;
        _watch = session?.ModulesChanged.Subscribe(_ => _s.Ui.PostCoalesced(_key, ReadModules));
    }

    /// <summary>UI thread: shows the list the focused session keeps, after it announced a change (running or stopped).</summary>
    private void ReadModules()
    {
        if (IsDisposed || _watched is not { } modules) return;
        Show(modules.Modules);
    }

    private void Show(IReadOnlyList<DebugModule> modules)
    {
        Rows.ReplaceAll([.. modules.Take(MaxModules).Select(m => new ModuleRow(m.Name is { Length: > 0 } name ? name : "(unnamed)", m.Path, m.Version, m.IsUserCode, m.SymbolStatus))]);
        StatusText = Rows.Count == 0 ? "The debug adapter did not report any modules." : null;
    }

    private void Cancel()
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); }
        catch (AggregateException) { }
        old.Dispose();
    }

    public Task RefreshAsync() => _focus.Session is { } session ? LoadAsync(session, userAsked: true) : Task.CompletedTask;

    internal async Task LoadAsync(IDebugSession session, bool userAsked = false)
    {
        if (session is not IDebugSessionModules && !userAsked && _refused is not null && _refused.TryGetTarget(out var refused) && ReferenceEquals(refused, session))
            return; // it said no at an earlier stop: keep that message instead of sending a request it will fail again on every stop
        Cancel();
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        try
        {
            if (session is IDebugSessionModules modules)
            {
                await _s.Ui.OnUi(modules.GetModulesAsync(ct), ct);
                if (generation != Volatile.Read(ref _generation) || IsDisposed) return;
                Show(modules.Modules); // the list as it is now: a change announced (and shown) meanwhile is not undone by an older copy
                return;
            }
            var args = JsonSerializer.SerializeToElement(new { startModule = 0, moduleCount = MaxModules });
            var body = await _s.Ui.OnUi(session.CustomRequestAsync("modules", args, ct), ct);
            var rows = body is { ValueKind: JsonValueKind.Object } json ? Parse(json) : [];
            _refused = null;
            if (generation != Volatile.Read(ref _generation) || IsDisposed) return;
            Rows.ReplaceAll(rows);
            StatusText = rows.Count == 0 ? "The debug adapter did not report any modules." : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (session is not IDebugSessionModules) _refused = new WeakReference<IDebugSession>(session);
            if (generation == Volatile.Read(ref _generation) && !IsDisposed && Rows.Count == 0)
                StatusText = ex is NotSupportedException ? "The debug adapter does not support the modules request." : "Cannot read the modules: " + ex.Message;
        }
    }

    internal static List<ModuleRow> Parse(JsonElement body)
    {
        var rows = new List<ModuleRow>();
        if (!body.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return rows;
        foreach (var m in modules.EnumerateArray())
        {
            if (rows.Count >= MaxModules) break;
            string? Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool? user = m.TryGetProperty("isUserCode", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False ? u.GetBoolean() : null;
            rows.Add(new ModuleRow(Str("name") ?? "(unnamed)", Str("path"), Str("version"), user, Str("symbolStatus")));
        }
        return rows;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        _watch?.Dispose();
        _watch = null;
        _watched = null;
        try { _cts.Cancel(); }
        catch (AggregateException) { }
        _cts.Dispose();
        _selected = null;
        Rows.Clear();
        _focus = default;
    }
}
