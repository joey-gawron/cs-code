using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Breakpoints;

public enum BreakpointRowKind { Header, Source, Function, Exception }

/// <summary>How the debug adapter treats a breakpoint (from <see cref="IBreakpointManager.GetStatus"/>).</summary>
public enum BreakpointVerification
{
    /// <summary>No session has reported on it (not debugging, or the adapter has not answered yet).</summary>
    Unknown,
    Verified,
    /// <summary>Bound to a different line than requested.</summary>
    Moved,
    /// <summary>The adapter accepted the request but has not bound it yet (hollow glyph).</summary>
    Unverified,
    /// <summary>The adapter rejected it; <see cref="BreakpointRow.StatusMessage"/> says why.</summary>
    Failed,
}

/// <summary>One row of the Breakpoints window: a section header, a source/function breakpoint, or an exception filter.</summary>
public sealed class BreakpointRow : ObservableObject
{
    private readonly Action<BreakpointRow, bool>? _onEnabledChanged;
    private bool _isEnabled;
    private bool _suppress;

    internal BreakpointRow(BreakpointRowKind kind, string id, string label, bool isEnabled, Action<BreakpointRow, bool>? onEnabledChanged)
    {
        Kind = kind;
        Id = id;
        Label = label;
        _isEnabled = isEnabled;
        _onEnabledChanged = onEnabledChanged;
    }

    public BreakpointRowKind Kind { get; }
    public string Id { get; }
    /// <summary><c>Program.cs : 12</c>, a function name, an exception filter label or a section title.</summary>
    public string Label { get; }
    /// <summary>Folder of the file (dimmed after the label); empty for non-source rows.</summary>
    public string Detail { get; internal init; } = "";
    public SourceBreakpoint? Source { get; internal init; }
    public FunctionBreakpoint? Function { get; internal init; }
    public ExceptionFilterSetting? Filter { get; internal init; }
    public string? Condition { get; internal init; }
    public string? HitCondition { get; internal init; }
    public string? LogMessage { get; internal init; }
    public BreakpointVerification Verification { get; internal init; }
    public string? StatusMessage { get; internal init; }
    /// <summary>1-based line the adapter bound the breakpoint to when it moved it.</summary>
    public int? MovedToLine { get; internal init; }

    public bool IsFailed => Verification == BreakpointVerification.Failed;
    public bool IsHeader => Kind == BreakpointRowKind.Header;
    public bool IsBreakpoint => Kind is BreakpointRowKind.Source or BreakpointRowKind.Function;
    public bool HasCheckbox => Kind != BreakpointRowKind.Header;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(GlyphKind));
            if (!_suppress) _onEnabledChanged?.Invoke(this, value);
        }
    }

    public bool HasCondition => !string.IsNullOrEmpty(Condition);
    public bool HasHitCondition => !string.IsNullOrEmpty(HitCondition);
    public bool HasLogMessage => !string.IsNullOrEmpty(LogMessage);
    public string ConditionBadge => $"Condition: {Condition}";
    public string HitBadge => $"Hit count: {HitCondition}";
    public string LogBadge => $"Log: {LogMessage}";
    public bool HasNote => MovedToLine is not null || Verification == BreakpointVerification.Failed || Verification == BreakpointVerification.Unverified && StatusMessage is not null;

    public string Note => Verification switch
    {
        BreakpointVerification.Failed => StatusMessage ?? "The breakpoint could not be set.",
        _ when MovedToLine is { } line => $"moved to line {line}",
        _ => StatusMessage ?? "",
    };

    /// <summary>Codicon of the breakpoint glyph: solid when active, hollow when disabled/unverified, variants for conditional, log and function breakpoints.</summary>
    public string GlyphKind
    {
        get
        {
            if (Kind == BreakpointRowKind.Exception) return _isEnabled ? "circle-large-filled" : "circle";
            if (Verification == BreakpointVerification.Failed) return "debug-breakpoint-unsupported";
            var unverified = Verification == BreakpointVerification.Unverified;
            if (Kind == BreakpointRowKind.Function) return _isEnabled ? "debug-breakpoint-function" : "circle";
            if (!_isEnabled) return "circle";
            if (HasLogMessage) return "debug-breakpoint-log";
            if (HasCondition || HasHitCondition) return unverified ? "debug-breakpoint-conditional-unverified" : "debug-breakpoint-conditional";
            return unverified ? "circle" : "circle-large-filled";
        }
    }

    public string StateText => Verification switch
    {
        BreakpointVerification.Verified => "Verified",
        BreakpointVerification.Moved => "Verified (moved)",
        BreakpointVerification.Unverified => "Not yet bound",
        BreakpointVerification.Failed => "Failed: " + (StatusMessage ?? "rejected by the debug adapter"),
        _ => Kind == BreakpointRowKind.Exception ? "" : _isEnabled ? "Enabled" : "Disabled",
    };

    /// <summary>Function breakpoints cannot be toggled (the manager contract has no update for them).</summary>
    public bool CanToggle => Kind is BreakpointRowKind.Source or BreakpointRowKind.Exception;

    public string Tooltip
    {
        get
        {
            if (Kind == BreakpointRowKind.Header) return Label;
            if (Kind == BreakpointRowKind.Exception) return Filter?.Label ?? Label;
            var lines = new List<string> { Source is { } s ? (s.Uri.IsFile ? s.Uri.LocalPath : s.Uri.ToString()) + " : " + (s.Line + 1) : Label, StateText };
            if (HasCondition) lines.Add(ConditionBadge);
            if (HasHitCondition) lines.Add(HitBadge);
            if (HasLogMessage) lines.Add(LogBadge);
            return string.Join('\n', lines.Where(l => l.Length > 0));
        }
    }

    public Thickness Indent => new(IsHeader ? 0 : 6, 0, 0, 0);

    /// <summary>Sets the checkbox from the model without echoing the change back to it.</summary>
    internal void SetEnabledSilently(bool value)
    {
        _suppress = true;
        IsEnabled = value;
        _suppress = false;
    }

    public override string ToString() => Label;
}

/// <summary>
/// Breakpoints window: source, function and exception breakpoints with enable checkboxes, condition / hit-count / log-message badges and the
/// adapter's verdict (verified, moved to another line, failed with a message). Edits go straight to <see cref="IBreakpointManager"/>, which
/// persists them and mirrors them to the debug sessions; this model only re-reads its snapshots when <see cref="IBreakpointManager.Changed"/>
/// fires (coalesced), so it never holds state of its own to go stale.
/// </summary>
public sealed class BreakpointsViewModel : ObservableObject, IDisposable
{
    /// <summary>Above this many breakpoints the row list is built on the thread pool instead of the UI thread (the snapshots are immutable).</summary>
    private const int OffThreadThreshold = 200;

    private readonly DebugPanelServices _s;
    private readonly IBreakpointManager _manager;
    private readonly DisposableStore _subs = new();
    private readonly string _refreshKey = "debug.breakpoints.refresh." + Guid.NewGuid().ToString("N");
    private BreakpointRow? _selected;
    private long _refreshVersion;
    private int _disposed;

    public BreakpointsViewModel(DebugPanelServices services)
    {
        _s = services;
        _manager = services.Breakpoints;
        _subs.Add(_manager.Changed.Subscribe(_ => QueueRefresh()));
        // A session starting or ending changes what the adapters say about every breakpoint.
        _subs.Add(services.Debug.SessionStarted.Subscribe(_ => QueueRefresh()));
        _subs.Add(services.Debug.SessionTerminated.Subscribe(_ => QueueRefresh()));
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        _ = RefreshAsync();
        EnableAllCommand = new UiCommand(() => _manager.SetEnabledAll(true));
        DisableAllCommand = new UiCommand(() => _manager.SetEnabledAll(false));
        DeleteAllCommand = new UiCommand(() => { _manager.RemoveAll(); });
        DeleteSelectedCommand = new UiCommand(DeleteSelected);
        AddFunctionCommand = new UiCommand(AddFunctionAsync, onError: ReportError);
    }

    public RangeObservableCollection<BreakpointRow> Rows { get; } = new();
    public BreakpointRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }
    public bool IsEmpty => Rows.Count == 0;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public UiCommand EnableAllCommand { get; }
    public UiCommand DisableAllCommand { get; }
    public UiCommand DeleteAllCommand { get; }
    public UiCommand DeleteSelectedCommand { get; }
    public UiCommand AddFunctionCommand { get; }

    // ------------------------------------------------------------------ rows

    /// <summary>Any thread: schedules one refresh on the UI thread (a burst of changes collapses into one).</summary>
    private void QueueRefresh() => _s.Ui.PostCoalesced(_refreshKey, StartRefresh);

    private void StartRefresh() => _ = RefreshAsync();

    /// <summary>Re-reads the manager's snapshots and replaces the rows (selection kept by id). Completes when the rows are applied.</summary>
    public async Task RefreshAsync()
    {
        if (IsDisposed) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        var source = _manager.SourceBreakpoints;
        var functions = _manager.FunctionBreakpoints;
        var filters = _manager.ExceptionFilters;
        List<BreakpointRow> rows;
        if (source.Count + functions.Count > OffThreadThreshold)
        {
            rows = await Task.Run(() => BuildRows(source, functions, filters));
            await _s.Ui.SwitchToUi();
            if (IsDisposed || version != Interlocked.Read(ref _refreshVersion)) return; // a newer refresh superseded this one
        }
        else rows = BuildRows(source, functions, filters);

        var keep = SelectedRow?.Id;
        Rows.ReplaceAll(rows);
        if (keep is not null) SelectedRow = rows.FirstOrDefault(r => r.Id == keep && !r.IsHeader);
    }

    private List<BreakpointRow> BuildRows(IReadOnlyList<SourceBreakpoint> source, IReadOnlyList<FunctionBreakpoint> functions, IReadOnlyList<ExceptionFilterSetting> filters)
    {
        var rows = new List<BreakpointRow>(source.Count + functions.Count + filters.Count + 2);
        var count = source.Count + functions.Count;
        if (count > 0)
        {
            rows.Add(new BreakpointRow(BreakpointRowKind.Header, "header:breakpoints", $"Breakpoints ({count})", true, null));
            foreach (var bp in source.OrderBy(b => b.Uri.ToString(), StringComparer.Ordinal).ThenBy(b => b.Line)) rows.Add(SourceRow(bp));
            foreach (var fn in functions) rows.Add(FunctionRow(fn));
        }
        if (filters.Count > 0)
        {
            rows.Add(new BreakpointRow(BreakpointRowKind.Header, "header:exceptions", "Exception Settings", true, null));
            foreach (var f in filters) rows.Add(ExceptionRow(f));
        }
        return rows;
    }

    private BreakpointRow SourceRow(SourceBreakpoint bp)
    {
        var path = bp.Uri.IsFile ? bp.Uri.LocalPath : bp.Uri.AbsolutePath;
        var file = System.IO.Path.GetFileName(path);
        var (verification, message, moved) = Verify(bp.Id, bp.Line);
        return new BreakpointRow(BreakpointRowKind.Source, bp.Id, $"{(file.Length > 0 ? file : path)} : {bp.Line + 1}", bp.Enabled, OnEnabledChanged)
        {
            Source = bp, Detail = System.IO.Path.GetDirectoryName(path) ?? "", Condition = bp.Condition, HitCondition = bp.HitCondition, LogMessage = bp.LogMessage,
            Verification = verification, StatusMessage = message, MovedToLine = moved,
        };
    }

    private BreakpointRow FunctionRow(FunctionBreakpoint fn)
    {
        var (verification, message, _) = Verify(fn.Id, null);
        return new BreakpointRow(BreakpointRowKind.Function, fn.Id, fn.Name, fn.Enabled, OnEnabledChanged)
        {
            Function = fn, Detail = "function", Condition = fn.Condition, HitCondition = fn.HitCondition, Verification = verification, StatusMessage = message,
        };
    }

    private BreakpointRow ExceptionRow(ExceptionFilterSetting f) =>
        new(BreakpointRowKind.Exception, "filter:" + f.Filter, f.Label, f.Enabled, OnEnabledChanged) { Filter = f, Condition = f.Condition };

    private (BreakpointVerification Verification, string? Message, int? MovedToLine) Verify(string id, int? requestedLine)
    {
        var status = _manager.GetStatus(id);
        if (status is null) return (BreakpointVerification.Unknown, null, null);
        if (!status.Verified) return (string.IsNullOrEmpty(status.Message) ? BreakpointVerification.Unverified : BreakpointVerification.Failed, status.Message, null);
        if (requestedLine is { } line && status.ActualLine is { } actual && actual != line) return (BreakpointVerification.Moved, status.Message, actual + 1);
        return (BreakpointVerification.Verified, status.Message, null);
    }

    // ------------------------------------------------------------------ edits

    private void OnEnabledChanged(BreakpointRow row, bool enabled)
    {
        switch (row.Kind)
        {
            case BreakpointRowKind.Source when row.Source is { } bp:
                _manager.Update(bp with { Enabled = enabled });
                break;
            case BreakpointRowKind.Function:
                // IBreakpointManager has no way to update a function breakpoint (only add/remove), so its checkbox is read-only.
                row.SetEnabledSilently(!enabled);
                break;
            case BreakpointRowKind.Exception when row.Filter is { } filter:
                _manager.SetExceptionFilters([.. _manager.ExceptionFilters.Select(f => f.Filter == filter.Filter ? f with { Enabled = enabled } : f)]);
                break;
        }
    }

    public void Delete(BreakpointRow row)
    {
        if (row.Kind == BreakpointRowKind.Source) _manager.Remove(row.Id);
        else if (row.Kind == BreakpointRowKind.Function) _manager.RemoveFunction(row.Id);
    }

    private void DeleteSelected()
    {
        if (SelectedRow is { } row) Delete(row);
    }

    public void ToggleEnabled(BreakpointRow row)
    {
        if (row.HasCheckbox) row.IsEnabled = !row.IsEnabled;
    }

    /// <summary>Edits the condition through an input box (empty text clears it). Source and function breakpoints only.</summary>
    public async Task EditConditionAsync(BreakpointRow row)
    {
        if (_s.Window is not { } window) return;
        if (row.Kind == BreakpointRowKind.Source && row.Source is { } bp)
        {
            var text = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Breakpoint condition", Prompt: "Break when this expression is true (leave empty for none)", Value: bp.Condition ?? ""));
            if (text is not null) _manager.Update(bp with { Condition = string.IsNullOrWhiteSpace(text) ? null : text.Trim() });
        }
        else if (row.Kind == BreakpointRowKind.Exception && row.Filter is { } filter)
        {
            var text = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Exception condition", Prompt: "Break only when this expression is true (leave empty for none)", Value: filter.Condition ?? ""));
            if (text is not null) _manager.SetExceptionFilters([.. _manager.ExceptionFilters.Select(f => f.Filter == filter.Filter ? f with { Condition = string.IsNullOrWhiteSpace(text) ? null : text.Trim() } : f)]);
        }
    }

    public async Task EditHitCountAsync(BreakpointRow row)
    {
        if (_s.Window is not { } window || row.Source is not { } bp) return;
        var text = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Hit count", Prompt: "Break when the hit count satisfies this (for example 5, >5, %3); empty for none", Value: bp.HitCondition ?? ""));
        if (text is not null) _manager.Update(bp with { HitCondition = string.IsNullOrWhiteSpace(text) ? null : text.Trim() });
    }

    public async Task EditLogMessageAsync(BreakpointRow row)
    {
        if (_s.Window is not { } window || row.Source is not { } bp) return;
        var text = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Log message", Prompt: "Log this message instead of breaking ({expression} is evaluated); empty for none", Value: bp.LogMessage ?? ""));
        if (text is not null) _manager.Update(bp with { LogMessage = string.IsNullOrWhiteSpace(text) ? null : text });
    }

    private async Task AddFunctionAsync()
    {
        if (_s.Window is not { } window) return;
        var name = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "New function breakpoint", Prompt: "Function name (for example Namespace.Class.Method)"));
        if (!string.IsNullOrWhiteSpace(name)) _manager.AddFunction(name.Trim());
    }

    private void ReportError(Exception ex) => _s.Window?.SetStatusBarMessage("Breakpoints: " + ex.Message, TimeSpan.FromSeconds(4));

    /// <summary>Double-click / Enter: opens the file at the breakpoint's line (the adapter-moved line when it moved).</summary>
    public async Task NavigateAsync(BreakpointRow row)
    {
        if (_s.Navigation is not { } navigation || row.Source is not { } bp) return;
        var line = row.MovedToLine is { } moved ? moved - 1 : bp.Line;
        try { await _s.Ui.OnUi(navigation.OpenAsync(bp.Uri, TextRange.At(Math.Max(0, line), Math.Max(0, bp.Column ?? 0)), preview: false, preserveFocus: false)); }
        catch (Exception ex) { ReportError(ex); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subs.Dispose();
        _selected = null;
        Rows.Reset();
    }
}
