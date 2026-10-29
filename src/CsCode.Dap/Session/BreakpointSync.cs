using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Session;

/// <summary>What <see cref="BreakpointSync"/> needs from its session (kept narrow so the sync is testable and the session stays readable).</summary>
internal interface IBreakpointSyncHost
{
    IDebugSession Session { get; }
    DapPositions Positions { get; }
    DapCapabilities Capabilities { get; }
    bool IsLive { get; }
    /// <summary>The adapter's quirks (hit-condition translation, client-side exception filters).</summary>
    DebugAdapterQuirks Quirks { get; }
    Task<JsonElement> RequestAsync(string command, object? arguments, CancellationToken ct);
    void BreakpointStatusChanged(DebugBreakpointStatus status);
}

/// <summary>
/// Mirrors <see cref="IBreakpointManager"/> onto ONE adapter. DAP's setBreakpoints is replace-all per source, so a change to a file
/// re-sends that file's complete list (also an empty list, which is how the last breakpoint in a file gets cleared). Changes are
/// debounced (~50 ms), only kinds the adapter supports are sent, and a file whose content did not change since the last send is
/// skipped — which also makes adapter-reported status changes (which re-fire the manager's Changed event) loop-free.
/// Per-session bookkeeping (adapter ids, signatures) lives here and dies with the session.
/// </summary>
internal sealed class BreakpointSync : IDisposable
{
    private readonly IBreakpointSyncHost _host;
    private readonly IBreakpointManager _manager;
    private readonly DebugLane _lane;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly DisposableStore _subs = new();
    private readonly HashSet<Uri> _pendingUris = new();
    private readonly Dictionary<Uri, string> _sentSignatures = new();
    private readonly Dictionary<Uri, int[]> _adapterIdsByFile = new();
    private readonly Dictionary<int, string> _breakpointIdByAdapterId = new();
    // Function breakpoints' adapter ids (in _breakpointIdByAdapterId too, so their `breakpoint` events update the verdict: GDB answers a
    // function breakpoint set before the program is loaded "pending" and verifies it later by event).
    private int[] _functionAdapterIds = [];
    // Adapter ids of the function breakpoints that implement client-side exception filters (DebugAdapterQuirks.SyntheticExceptionFilters).
    private readonly Dictionary<int, SyntheticExceptionFilter> _syntheticByAdapterId = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _pendingFunctionsOrFilters, _flushScheduled, _flushRunning, _active, _disposed;
    private string? _sentFunctions, _sentFilters;
    private int _requestsSent;

    public BreakpointSync(IBreakpointSyncHost host, IBreakpointManager manager, DebugLane lane, ILogger logger, TimeSpan debounce)
    {
        _host = host;
        _manager = manager;
        _lane = lane;
        _logger = logger;
        _debounce = debounce;
    }

    public int RequestsSent => Volatile.Read(ref _requestsSent);
    public int TrackedAdapterIds => _breakpointIdByAdapterId.Count;

    /// <summary>Lane only. The client-side exception filter whose breakpoint the adapter reports among <paramref name="hitBreakpointIds"/>.</summary>
    public SyntheticExceptionFilter? SyntheticFilterHit(IReadOnlyList<int>? hitBreakpointIds)
    {
        if (hitBreakpointIds is null) return null;
        foreach (var id in hitBreakpointIds)
            if (_syntheticByAdapterId.TryGetValue(id, out var filter)) return filter;
        return null;
    }

    // ------------------------------------------------------------------ initial sync (between `initialized` and `configurationDone`)

    public async Task SendAllAsync(CancellationToken ct)
    {
        _active = true;
        _subs.Add(_manager.Changed.Subscribe(OnManagerChanged)); // changes from now on are queued; the full send below reads current state
        var uris = _manager.SourceBreakpoints.Select(b => b.Uri).Distinct().ToArray();
        foreach (var uri in uris) await SendFileAsync(uri, ct);
        await SendFunctionsAsync(ct, force: false);
        await SendExceptionFiltersAsync(ct);
    }

    // ------------------------------------------------------------------ incremental sync

    private void OnManagerChanged(BreakpointsChangedEventArgs e)
    {
        // May fire on the UI thread: only enqueue (a copy of the uris), never touch sync state here.
        var uris = e.AffectedUris.ToArray();
        var functions = e.FunctionsOrExceptionsChanged;
        _lane.Post(() =>
        {
            if (_disposed || !_active) return;
            foreach (var u in uris) _pendingUris.Add(u);
            _pendingFunctionsOrFilters |= functions;
            if (_flushScheduled) return;
            _flushScheduled = true;
            _lane.PostDelayed(_debounce, () => _ = FlushAsync());
        });
    }

    private async Task FlushAsync()
    {
        _flushScheduled = false;
        if (_disposed) return;
        if (_flushRunning) { _flushScheduled = true; _lane.PostDelayed(_debounce, () => _ = FlushAsync()); return; }
        _flushRunning = true;
        try
        {
            var uris = _pendingUris.ToArray();
            var functions = _pendingFunctionsOrFilters;
            _pendingUris.Clear();
            _pendingFunctionsOrFilters = false;
            if (!_host.IsLive) return;
            foreach (var uri in uris) await SendFileAsync(uri, _cts.Token);
            if (functions)
            {
                await SendFunctionsAsync(_cts.Token, force: false);
                await SendExceptionFiltersAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Breakpoint mirroring to session {Session} failed", _host.Session.Id); }
        finally { _flushRunning = false; }
    }

    // ------------------------------------------------------------------ requests

    private async Task SendFileAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsFile) return; // virtual (debug:) and remote sources are not addressable by an adapter's path
        var caps = _host.Capabilities;
        var pos = _host.Positions;
        var sent = new List<(SourceBreakpoint Bp, DapSourceBreakpoint Wire)>();
        foreach (var bp in _manager.GetForUri(uri))
        {
            if (!bp.Enabled) continue;
            var unsupported =
                bp.Condition is { Length: > 0 } && caps.SupportsConditionalBreakpoints != true ? "conditional breakpoints" :
                bp.HitCondition is { Length: > 0 } && caps.SupportsHitConditionalBreakpoints != true ? "hit-count breakpoints" :
                bp.LogMessage is { Length: > 0 } && caps.SupportsLogPoints != true ? "logpoints" : null;
            if (unsupported is not null)
            {
                // Sending it without the condition would make the debuggee stop where the user said it must not.
                Report(new DebugBreakpointStatus(_host.Session, bp.Id, false, null, $"This debug adapter does not support {unsupported}."));
                continue;
            }
            var hit = MapHitCondition(bp.HitCondition);
            if (hit.Unsupported is { } why)
            {
                Report(new DebugBreakpointStatus(_host.Session, bp.Id, false, null, why));
                continue;
            }
            sent.Add((bp, new DapSourceBreakpoint
            {
                Line = pos.ToWireLine(bp.Line),
                Column = bp.Column is { } c ? pos.ToWireColumn(c) : null,
                Condition = bp.Condition is { Length: > 0 } cond ? cond : null,
                HitCondition = hit.Wire,
                LogMessage = bp.LogMessage is { Length: > 0 } log ? log : null,
            }));
        }

        var signature = string.Join('|', sent.Select(s => $"{s.Wire.Line}:{s.Wire.Column}:{s.Wire.Condition}:{s.Wire.HitCondition}:{s.Wire.LogMessage}"));
        if (_sentSignatures.TryGetValue(uri, out var previous) && previous == signature) return;
        if (sent.Count == 0 && previous is null) { _sentSignatures[uri] = signature; return; } // never sent anything for this file: nothing to clear

        var args = new DapSetBreakpointsArguments { Source = DapMapping.ToWire(uri), Breakpoints = [.. sent.Select(s => s.Wire)], SourceModified = false };
        Interlocked.Increment(ref _requestsSent);
        JsonElement body;
        try { body = await _host.RequestAsync("setBreakpoints", args, ct); }
        catch (DapRequestException ex)
        {
            // An adapter rejecting a source (unknown file, unsupported language...) must not abort the debug session: the breakpoints of
            // that file are shown unverified with the adapter's reason (what VS Code does). Not retried until the file's content changes.
            _sentSignatures[uri] = signature;
            ForgetAdapterIds(uri);
            foreach (var (bp, _) in sent) Report(new DebugBreakpointStatus(_host.Session, bp.Id, false, null, ex.AdapterMessage));
            return;
        }
        _sentSignatures[uri] = signature;

        ForgetAdapterIds(uri);
        var response = DapJson.Deserialize<DapBreakpointsResponse>(body)?.Breakpoints ?? [];
        var ids = new List<int>();
        for (var i = 0; i < sent.Count; i++)
        {
            var bp = sent[i].Bp;
            if (i >= response.Count)
            {
                Report(new DebugBreakpointStatus(_host.Session, bp.Id, false, null, "The debug adapter did not report this breakpoint."));
                continue;
            }
            var r = response[i];
            if (r.Id is { } adapterId)
            {
                _breakpointIdByAdapterId[adapterId] = bp.Id;
                ids.Add(adapterId);
            }
            Report(new DebugBreakpointStatus(_host.Session, bp.Id, r.Verified, r.Line is { } l ? pos.ToCoreLine(l) : null, r.Message));
        }
        _adapterIdsByFile[uri] = [.. ids];
    }

    private HitConditionMapping MapHitCondition(string? hitCondition) =>
        hitCondition is { Length: > 0 } hit ? _host.Quirks.MapHitCondition(hit) : new HitConditionMapping(null, null);

    private async Task SendFunctionsAsync(CancellationToken ct, bool force)
    {
        var all = _manager.FunctionBreakpoints.Where(f => f.Enabled).ToList();
        // Client-side exception filters the user enabled travel as function breakpoints, after the user's own.
        var synthetic = _host.Quirks.SyntheticExceptionFilters is { Count: > 0 } offered
            ? offered.Where(o => _manager.ExceptionFilters.Any(f => f.Filter == o.Filter && f.Enabled)).ToList()
            : [];
        var signature = string.Join('|', all.Select(f => $"{f.Name}:{f.Condition}:{f.HitCondition}")) + "#" + string.Join('|', synthetic.Select(f => f.FunctionName));
        if (!force && (_sentFunctions ?? "#") == signature) return;
        if (_host.Capabilities.SupportsFunctionBreakpoints != true)
        {
            // Nothing is sent (js-debug v1.140.0 advertises no function breakpoints), but the user must see why the breakpoint never hits,
            // as for the source breakpoint kinds an adapter lacks. The signature keeps the verdict's own Changed event from looping back here.
            _sentFunctions = signature;
            foreach (var f in all) Report(new DebugBreakpointStatus(_host.Session, f.Id, false, null, "This debug adapter does not support function breakpoints."));
            return;
        }
        var fns = new List<FunctionBreakpoint>();
        var wire = new List<DapFunctionBreakpoint>();
        foreach (var f in all)
        {
            var hit = _host.Capabilities.SupportsHitConditionalBreakpoints == true ? MapHitCondition(f.HitCondition) : new HitConditionMapping(null, null);
            if (hit.Unsupported is { } why)
            {
                Report(new DebugBreakpointStatus(_host.Session, f.Id, false, null, why));
                continue;
            }
            fns.Add(f);
            wire.Add(new DapFunctionBreakpoint
            {
                Name = f.Name,
                Condition = f.Condition is { Length: > 0 } && _host.Capabilities.SupportsConditionalBreakpoints == true ? f.Condition : null,
                HitCondition = hit.Wire,
            });
        }
        wire.AddRange(synthetic.Select(s => new DapFunctionBreakpoint { Name = s.FunctionName }));
        var args = new DapSetFunctionBreakpointsArguments { Breakpoints = wire };
        Interlocked.Increment(ref _requestsSent);
        JsonElement body;
        try { body = await _host.RequestAsync("setFunctionBreakpoints", args, ct); }
        catch (DapRequestException ex)
        {
            _sentFunctions = signature;
            foreach (var f in fns) Report(new DebugBreakpointStatus(_host.Session, f.Id, false, null, ex.AdapterMessage));
            return;
        }
        _sentFunctions = signature;
        foreach (var id in _functionAdapterIds) _breakpointIdByAdapterId.Remove(id);
        _syntheticByAdapterId.Clear();
        var response = DapJson.Deserialize<DapBreakpointsResponse>(body)?.Breakpoints ?? [];
        var ids = new List<int>();
        for (var i = 0; i < fns.Count && i < response.Count; i++)
        {
            if (response[i].Id is { } adapterId)
            {
                _breakpointIdByAdapterId[adapterId] = fns[i].Id;
                ids.Add(adapterId);
            }
            Report(new DebugBreakpointStatus(_host.Session, fns[i].Id, response[i].Verified, null, response[i].Message));
        }
        for (var i = 0; i < synthetic.Count && fns.Count + i < response.Count; i++)
            if (response[fns.Count + i].Id is { } adapterId) _syntheticByAdapterId[adapterId] = synthetic[i];
        _functionAdapterIds = [.. ids];
    }

    private async Task SendExceptionFiltersAsync(CancellationToken ct)
    {
        var caps = _host.Capabilities;
        var advertised = caps.ExceptionBreakpointFilters;
        if (advertised is null || advertised.Count == 0) return;
        var known = advertised.ToDictionary(f => f.Filter);
        var enabled = _manager.ExceptionFilters.Where(f => f.Enabled && known.ContainsKey(f.Filter)).ToList();
        var useOptions = caps.SupportsExceptionFilterOptions == true;
        var plain = enabled.Where(f => !(useOptions && f.Condition is { Length: > 0 } && known[f.Filter].SupportsCondition == true)).Select(f => f.Filter).ToList();
        var withCondition = enabled.Where(f => useOptions && f.Condition is { Length: > 0 } && known[f.Filter].SupportsCondition == true)
            .Select(f => new DapExceptionFilterOptions { FilterId = f.Filter, Condition = f.Condition }).ToList();
        var signature = string.Join('|', plain) + "#" + string.Join('|', withCondition.Select(o => o.FilterId + "=" + o.Condition));
        if (_sentFilters == signature) return;
        Interlocked.Increment(ref _requestsSent);
        try
        {
            await _host.RequestAsync("setExceptionBreakpoints", new DapSetExceptionBreakpointsArguments { Filters = plain, FilterOptions = withCondition.Count > 0 ? withCondition : null }, ct);
        }
        catch (DapRequestException ex)
        {
            _logger.LogDebug(ex, "Adapter rejected setExceptionBreakpoints");
        }
        _sentFilters = signature;
    }

    /// <summary>Adapter-initiated <c>breakpoint</c> event (e.g. a pending breakpoint got bound after a module loaded).</summary>
    public void OnAdapterBreakpointEvent(DapBreakpointEvent e)
    {
        if (e.Breakpoint.Id is not { } adapterId || !_breakpointIdByAdapterId.TryGetValue(adapterId, out var ourId)) return;
        if (e.Reason == "removed") { _breakpointIdByAdapterId.Remove(adapterId); return; }
        var pos = _host.Positions;
        // A function breakpoint's "line" is where the function is (GDB reports it): not a moved line of a source breakpoint.
        var line = Array.IndexOf(_functionAdapterIds, adapterId) >= 0 ? null : e.Breakpoint.Line is { } l ? pos.ToCoreLine(l) : (int?)null;
        Report(new DebugBreakpointStatus(_host.Session, ourId, e.Breakpoint.Verified, line, e.Breakpoint.Message));
    }

    private void Report(DebugBreakpointStatus status)
    {
        _manager.ReportStatus(status);
        _host.BreakpointStatusChanged(status);
    }

    private void ForgetAdapterIds(Uri uri)
    {
        if (!_adapterIdsByFile.Remove(uri, out var old)) return;
        foreach (var id in old) _breakpointIdByAdapterId.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _subs.Dispose();
        _pendingUris.Clear();
        _sentSignatures.Clear();
        _adapterIdsByFile.Clear();
        _breakpointIdByAdapterId.Clear();
        _syntheticByAdapterId.Clear();
        _functionAdapterIds = [];
        _manager.ClearStatuses(_host.Session.Id);
    }
}
