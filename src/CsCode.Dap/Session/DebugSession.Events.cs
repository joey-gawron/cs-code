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
        LastException = null;

        _output.Flush(); // output printed before the stop must reach the console before the stop does
        SetState(DebugSessionState.Stopped);
        _stopped.Fire(new DebugStoppedEventArgs(this, e.Reason, e.ThreadId, e.AllThreadsStopped == true, e.Description, e.Text, e.HitBreakpointIds));

        if (e.Reason == "exception" && _caps.SupportsExceptionInfo && e.ThreadId is { } tid)
            _ = FetchExceptionInfoAsync(tid);
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
        LastException = null;
        if (allThreads) SetState(DebugSessionState.Running);
        _continued.Fire(new DebugContinuedEventArgs(this, threadId, allThreads));
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
        var line = e.Line is { } l ? _positions.ToCoreLine(l) : (int?)null;
        _output.Add(string.IsNullOrEmpty(e.Category) ? "console" : e.Category, e.Output, DapMapping.ToCore(e.Source), line);
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
            if (e.Reason == "removed") { _modules.Remove(id); return; }
            if (_modules.Count >= MaxModules && !_modules.ContainsKey(id)) return; // bounded: a module-heavy app can't grow us without limit
            _modules[id] = new DebugModuleInfo(id, e.Module.Name, e.Module.Path, e.Module.Version, e.Module.IsUserCode, e.Module.SymbolStatus);
        }
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
        if (areas is null || areas.Count == 0 || areas.Any(a => a is "all" or "variables" or "stacks"))
        {
            _handles.Invalidate();
            _completions.Clear();
        }
        _invalidated.Fire(this);
    }

    // ------------------------------------------------------------------ reverse requests

    private async Task<object?> OnRunInTerminalAsync(JsonElement args, CancellationToken ct)
    {
        var a = DapJson.Deserialize<DapRunInTerminalArguments>(args) ?? throw new ArgumentException("runInTerminal without arguments");
        return await _debuggees.LaunchAsync(a);
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
