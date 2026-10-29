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
/// Modules window. The debug contracts carry no module events, so the list is read with the DAP <c>modules</c> request through
/// <c>CustomRequestAsync</c> (when the adapter answers it): on every stop, and on demand. Bounded to <see cref="MaxModules"/> rows.
/// </summary>
public sealed class ModulesViewModel : ObservableObject, IDisposable
{
    public const int MaxModules = 2_000;

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private CancellationTokenSource _cts = new();
    private long _generation;
    private long _loadedEpoch = -1;
    private DebugFocus _focus;
    private string? _status = "Not debugging";
    private ModuleRow? _selected;
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
        if (focus.Session is null || focus.State == DebugSessionState.Terminated)
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

    private void Cancel()
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); }
        catch (AggregateException) { }
        old.Dispose();
    }

    public Task RefreshAsync() => _focus.Session is { } session ? LoadAsync(session) : Task.CompletedTask;

    internal async Task LoadAsync(IDebugSession session)
    {
        Cancel();
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        try
        {
            var args = JsonSerializer.SerializeToElement(new { startModule = 0, moduleCount = MaxModules });
            var body = await _s.Ui.OnUi(session.CustomRequestAsync("modules", args, ct), ct);
            if (generation != Volatile.Read(ref _generation) || IsDisposed) return;
            var rows = body is { ValueKind: JsonValueKind.Object } json ? Parse(json) : [];
            Rows.ReplaceAll(rows);
            StatusText = rows.Count == 0 ? "The debug adapter did not report any modules." : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
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
        try { _cts.Cancel(); }
        catch (AggregateException) { }
        _cts.Dispose();
        _selected = null;
        Rows.Clear();
        _focus = default;
    }
}
