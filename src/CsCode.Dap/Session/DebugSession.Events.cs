using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Session;

// Adapter → client: events and reverse requests. Everything here runs on the debug lane.
public sealed partial class DebugSession
{
    private const int MaxModules = 2_000;
    private const int RecentOutputChars = 8 * 1024;
    /// <summary>Several `stopped` events for one stop (one per thread) collapse into one if they follow each other this closely.</summary>
    private static readonly long DuplicateStopWindowTicks = Stopwatch.Frequency / 10; // 100 ms

    private void OnStopped(DapStoppedEvent e)
    {
        MarkAlive();
        var now = Stopwatch.GetTimestamp();
        if (State == DebugSessionState.Stopped && _lastStopAllThreads && _lastStopReason == e.Reason && now - _lastStopTimestamp < DuplicateStopWindowTicks)
        {
            _lastStopTimestamp = now;
            return; // "stopped once": the same all-threads stop announced again for another thread
        }
        _lastStopTimestamp = now;
        _stopCounter++;
        _lastStopReason = e.Reason;
        _lastStopAllThreads = e.AllThreadsStopped == true;

        // New stop = new generation: variablesReferences/frame ids from before are meaningless now.
        _handles.Invalidate();
        _completions.Clear();
        if (e.ThreadId is { } threadId) Volatile.Write(ref _focusedThread, threadId);
        _stopThread = e.ThreadId ?? -1;
        LastException = null;

        // A stop at the breakpoint of a client-side exception filter (GDB: "Rust panics") is reported as the exception it stands for.
        var reason = e.Reason;
        var description = e.Description;
        var text = e.Text;
        // The stop that answers our own pause, reported by the adapter as something else (CodeLLDB: "exception", "signal SIGSTOP").
        if (_pauseRequested)
        {
            _pauseRequested = false;
            if (reason != "pause" && _quirks.IsPauseStop(reason, description)) (reason, description, text) = ("pause", null, null);
        }
        _syntheticStop = e.Reason == "breakpoint" ? _sync?.SyntheticFilterHit(e.HitBreakpointIds) : null;
        if (_syntheticStop is { } filter)
        {
            reason = "exception";
            description = "Paused on exception: " + filter.Label;
            text = _quirks.DescribeSyntheticStop(filter, RecentOutput)?.Description;
        }

        _output.Flush(); // output printed before the stop must reach the console before the stop does
        SetState(DebugSessionState.Stopped);
        _stopped.Fire(new DebugStoppedEventArgs(this, reason, e.ThreadId, e.AllThreadsStopped == true, description, text, e.HitBreakpointIds));

        if (_syntheticStop is { } synthetic) _ = DescribeSyntheticStopAsync(synthetic, _stopCounter);
        else if (reason == "exception" && _caps.SupportsExceptionInfo && e.ThreadId is { } tid)
            _ = FetchExceptionInfoAsync(tid);
    }

    /// <summary>
    /// The exception a client-side filter's stop stands for (a Rust panic's message), from the program's output. The panic prints its message
    /// before it reaches the breakpoint, but the adapter may deliver that output just after the stop: waits for it a little (bounded).
    /// </summary>
    private async Task DescribeSyntheticStopAsync(SyntheticExceptionFilter filter, long stop)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2; // 500 ms
        DebugExceptionInfo? info;
        while ((info = _quirks.DescribeSyntheticStop(filter, RecentOutput)) is null && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(25);
            if (_stopCounter != stop || State != DebugSessionState.Stopped) return;
        }
        if (_stopCounter != stop || State != DebugSessionState.Stopped) return;
        info ??= new DebugExceptionInfo(filter.Filter, filter.Label, "always", null, filter.Label, null);
        LastException = info;
        _syntheticException = info;
        _exceptionInfo.Fire(info);
    }

    /// <summary>The program's latest output (bounded), kept only for adapters with client-side exception filters; cleared on resume.</summary>
    private string RecentOutput
    {
        get
        {
            if (_recentOutput is not { } recent) return "";
            return recent.Length > RecentOutputChars ? recent.ToString(recent.Length - RecentOutputChars, RecentOutputChars) : recent.ToString();
        }
    }

    /// <summary>
    /// Lane only, once per output event (a flood is a million of them): the head is cut only when the buffer reached twice its size, so the
    /// copying stays amortised O(1) per character instead of moving 8 KB per line.
    /// </summary>
    private void RememberOutput(string text)
    {
        if (_recentOutput is not { } recent) return;
        if (text.Length >= RecentOutputChars) { recent.Clear(); recent.Append(text, text.Length - RecentOutputChars, RecentOutputChars); return; }
        recent.Append(text);
        if (recent.Length > 2 * RecentOutputChars) recent.Remove(0, recent.Length - RecentOutputChars);
    }

    private async Task FetchExceptionInfoAsync(int threadId)
    {
        try
        {
            var info = await GetExceptionInfoCoreAsync(threadId, CancellationToken.None);
            if (State != DebugSessionState.Stopped) return;
            LastException = info;
            _exceptionInfo.Fire(info);
        }
        catch (Exception ex) when (ex is DapRequestException or DapRequestTimeoutException or DapTransportClosedException)
        {
            _logger.LogDebug(ex, "exceptionInfo failed");
        }
    }

    private void OnContinued(DapContinuedEvent e)
    {
        MarkAlive();
        // We already moved to Running when WE issued the resume (see ResumeCoreAsync); this covers adapter-initiated resumes
        // (e.g. a debuggee thread was resumed from the adapter's own UI) and is a no-op duplicate otherwise.
        if (State != DebugSessionState.Stopped) return;
        var all = e.AllThreadsContinued ?? true;
        ResumedLocally(e.ThreadId, all);
    }

    /// <summary>Handles die on resume: forget them, then tell the UI.</summary>
    private void ResumedLocally(int threadId, bool allThreads)
    {
        _resumeEpoch++;
        _handles.Invalidate();
        _completions.Clear();
        ResetRunState();
        if (allThreads) SetState(DebugSessionState.Running);
        _continued.Fire(new DebugContinuedEventArgs(this, threadId, allThreads));
    }

    /// <summary>
    /// Lane only. Forgets what belonged to the stop (or run) that is over: its exception, the client-side filter it hit, a pending pause, and
    /// the output a panic message is read from — a panic printed from now on belongs to the next stop. On a resume, and on a restart: a stale
    /// panic left in the output would describe the new run's panic with the old message (<see cref="DescribeSyntheticStopAsync"/> waits only
    /// while nothing describes the stop).
    /// </summary>
    private void ResetRunState()
    {
        LastException = null;
        _syntheticStop = null;
        _syntheticException = null;
        _pauseRequested = false;
        _recentOutput?.Clear();
    }

    private void OnExited(DapExitedEvent e)
    {
        MarkAlive();
        _exitCode = e.ExitCode;
    }

    private void OnThread(DapThreadEvent e)
    {
        MarkAlive();
        _threadChanged.Fire(new DebugThreadEventArgs(this, e.ThreadId, e.Reason));
    }

    private void OnOutput(DapOutputEvent e)
    {
        MarkAlive();
        Volatile.Write(ref _lastOutputTimestamp, Stopwatch.GetTimestamp());
        var line = e.Line is { } l ? _positions.ToCoreLine(l) : (int?)null;
        var category = string.IsNullOrEmpty(e.Category) ? "console" : e.Category;
        var text = e.Output;
        if (!ReferenceEquals(_quirks, DebugAdapterQuirks.None))
        {
            category = _quirks.MapOutputCategory(category, text);
            text = _quirks.MapOutputText(category, text);
            RememberOutput(text);
        }
        _output.Add(category, text, DapMapping.ToCore(e.Source), line);
    }

    private void OnProcess(DapProcessEvent e)
    {
        MarkAlive();
        DebuggeeProcessId = e.SystemProcessId;
        DebuggeeProcessName = e.Name;
    }

    private void OnModule(DapModuleEvent e)
    {
        MarkAlive();
        var id = e.Module.Id.ValueKind == JsonValueKind.String ? e.Module.Id.GetString() ?? "" : e.Module.Id.GetRawText();
        lock (_modules)
        {
            if (e.Reason == "removed") { if (!_modules.Remove(id)) return; }
            else if (_modules.Count >= MaxModules && !_modules.ContainsKey(id)) return; // bounded: a module-heavy app can't grow us without limit
            else _modules[id] = new DebugModuleInfo(id, e.Module.Name, e.Module.Path, e.Module.Version, e.Module.IsUserCode, e.Module.SymbolStatus);
        }
        NotifyModulesChanged();
    }

    private void OnLoadedSource(DapLoadedSourceEvent e)
    {
        MarkAlive();
        var source = DapMapping.ToCore(e.Source);
        if (source is null) return;
        var key = source.Path ?? (source.SourceReference is { } r ? "ref:" + r : source.Name ?? "");
        lock (_loadedSources)
        {
            if (e.Reason == "removed") _loadedSources.Remove(key);
            else if (_loadedSources.Count < MaxModules || _loadedSources.ContainsKey(key)) _loadedSources[key] = source; // bounded
        }
        _loadedSource.Fire(new DebugLoadedSourceEventArgs(e.Reason, source));
    }

    private void OnCapabilitiesEvent(DapCapabilitiesEvent e)
    {
        MarkAlive();
        var fresh = e.Capabilities.Deserialize<DapCapabilities>(DapJson.Options) ?? new DapCapabilities();
        var filtersChanged = e.Capabilities.ContainsKey("exceptionBreakpointFilters");
        ApplyCapabilities(fresh, merge: true);
        if (filtersChanged) MergeExceptionFilters();
    }

    private void OnInvalidatedEvent(DapInvalidatedEvent e)
    {
        MarkAlive();
        var areas = e.Areas;
        if (areas is null || areas.Count == 0 || areas.Any(a => a is "all" or "stacks"))
        {
            _handles.Invalidate(); // frame ids die too: DebugService re-fetches the stack and publishes a live stop location
            _completions.Clear();
        }
        else if (areas.Contains("variables"))
        {
            _handles.InvalidateVariables(); // the frames of this stop stay valid, so the focused frame survives
            _completions.Clear();
        }
        _invalidated.Fire(this);
    }

    // ------------------------------------------------------------------ reverse requests

    private async Task<object?> OnRunInTerminalAsync(JsonElement args, CancellationToken ct)
    {
        var a = DapJson.Deserialize<DapRunInTerminalArguments>(args) ?? throw new ArgumentException("runInTerminal without arguments");
        return await _debuggees.LaunchAsync(a, Name, ct);
    }

    private async Task<object?> OnStartDebuggingAsync(JsonElement args, CancellationToken ct)
    {
        var a = DapJson.Deserialize<DapStartDebuggingArguments>(args) ?? throw new ArgumentException("startDebugging without arguments");
        var starter = _o.StartChild ?? throw new NotSupportedException("startDebugging is not supported here");
        // The child's configuration inherits the debug type (and workspace) from its parent unless the adapter overrides it.
        if (!a.Configuration.ContainsKey("type")) a.Configuration["type"] = _o.Configuration.Type;
        if (!a.Configuration.ContainsKey("name")) a.Configuration["name"] = $"{Name} (child)";
        if (!a.Configuration.ContainsKey("request")) a.Configuration["request"] = a.Request;
        var child = await starter(this, a.Request, a.Configuration, ct);
        if (child is null) throw new InvalidOperationException("The child debug session could not be started.");
        return null;
    }
}
