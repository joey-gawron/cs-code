using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;

namespace CsCode.Dap.Session;

public enum DebugStepKind { Over, Into, Out, Back, ReverseContinue }

/// <summary>Optional stepping parameters (DAP <c>granularity</c>: statement | line | instruction; <c>singleThread</c>).</summary>
public sealed record DebugStepOptions(string? Granularity = null, bool SingleThread = false);

// Client → adapter API. Each public method is ONE enqueue onto the lane (the caller pays microseconds, never I/O or JSON);
// the *Core methods run on the lane.
public sealed partial class DebugSession
{
    private ValueTask<T> OnLane<T>(Func<Task<T>> work, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        return new ValueTask<T>(_lane.RunAsync(work, ct));
    }

    private ValueTask OnLane(Func<Task> work, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        return new ValueTask(_lane.RunAsync(work, ct));
    }

    private void EnsureLive()
    {
        if (_terminatedFired || _client is null) throw new InvalidOperationException($"The debug session '{Name}' has ended.");
    }

    private static DebugHandleInvalidatedException Stale(string what) =>
        new($"The {what} is no longer valid: the debuggee has resumed since it was obtained.");

    // ------------------------------------------------------------------ threads / stack / scopes

    public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => OnLane(() => GetThreadsCoreAsync(ct), ct);

    private async Task<IReadOnlyList<DebugThread>> GetThreadsCoreAsync(CancellationToken ct)
    {
        EnsureLive();
        var resp = DapJson.Deserialize<DapThreadsResponse>(await RequestAsync("threads", null, ct)) ?? new DapThreadsResponse();
        return [.. resp.Threads.Select(DapMapping.ToCore)];
    }

    public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) =>
        OnLane(() => GetStackTraceCoreAsync(threadId, startFrame, levels, ct), ct);

    private async Task<IReadOnlyList<DebugStackFrame>> GetStackTraceCoreAsync(int threadId, int startFrame, int levels, CancellationToken ct)
    {
        EnsureLive();
        var take = Math.Min(levels <= 0 ? _settings.MaxStackFrames : levels, _settings.MaxStackFrames);
        startFrame = Math.Max(0, startFrame);
        // Only adapters that advertise delayed loading honour startFrame/levels; for the others we ask for everything and page here.
        var delayed = _dapCaps.SupportsDelayedStackTraceLoading == true;
        var args = delayed
            ? new DapStackTraceArguments { ThreadId = threadId, StartFrame = startFrame, Levels = take }
            : new DapStackTraceArguments { ThreadId = threadId };
        var gen = _handles.FrameGeneration;
        var resp = DapJson.Deserialize<DapStackTraceResponse>(await RequestAsync("stackTrace", args, ct)) ?? new DapStackTraceResponse();
        IEnumerable<DapStackFrame> frames = delayed ? resp.StackFrames : resp.StackFrames.Skip(startFrame);
        var mapped = frames.Take(take).Select(f => _quirks.MapFrame(DapMapping.ToCore(f, _positions))).ToArray();
        if (gen == _handles.FrameGeneration) foreach (var f in mapped) _handles.RegisterFrame(f);
        return mapped;
    }

    /// <summary>
    /// Lane only. A frame this session handed out during the current stop — any thread, any page (the Call Stack window pages 50+ frames) —
    /// so it can be focused without asking the adapter again. False when the id is stale, and also when the bounded registry overflowed
    /// (then <see cref="IsFrameValid"/> still says true and only the adapter can tell).
    /// </summary>
    internal bool TryGetFrame(int frameId, [MaybeNullWhen(false)] out DebugStackFrame frame)
    {
        Debug.Assert(_lane.IsOnLane, "session state is lane-confined");
        return _handles.TryGetFrame(frameId, out frame);
    }

    /// <summary>Lane only. Whether <paramref name="frameId"/> may still be used (handed out during this stop, or tracking overflowed).</summary>
    internal bool IsFrameValid(int frameId)
    {
        Debug.Assert(_lane.IsOnLane, "session state is lane-confined");
        return _handles.IsValidFrame(frameId);
    }

    public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => OnLane(() => GetScopesCoreAsync(frameId, ct), ct);

    private async Task<IReadOnlyList<DebugScope>> GetScopesCoreAsync(int frameId, CancellationToken ct)
    {
        EnsureLive();
        if (!_handles.IsValidFrame(frameId)) throw Stale("stack frame");
        if (_handles.TryGetScopes(frameId, out var cached)) return cached;
        var gen = _handles.Generation;
        var resp = DapJson.Deserialize<DapScopesResponse>(await RequestAsync("scopes", new DapScopesArguments { FrameId = frameId }, ct)) ?? new DapScopesResponse();
        if (gen != _handles.Generation) throw Stale("stack frame");
        var scopes = resp.Scopes.Select(DapMapping.ToCore).ToArray();
        foreach (var s in scopes) _handles.RegisterHandle(s.VariablesReference);
        _handles.SetScopes(frameId, scopes);
        return scopes;
    }

    // ------------------------------------------------------------------ variables / evaluate

    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) =>
        OnLane(() => GetVariablesCoreAsync(variablesReference, null, start, count, ct), ct);

    /// <summary>Paged read of indexed or named children (<paramref name="filter"/> = "indexed" | "named" | null for both).</summary>
    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, string? filter, int? start, int? count, CancellationToken ct = default) =>
        OnLane(() => GetVariablesCoreAsync(variablesReference, filter, start, count, ct), ct);

    private async Task<IReadOnlyList<DebugVariable>> GetVariablesCoreAsync(int reference, string? filter, int? start, int? count, CancellationToken ct)
    {
        EnsureLive();
        if (reference <= 0) return [];
        if (!_handles.IsValidHandle(reference)) throw Stale("variables reference");
        var key = new VariableHandleCache.VariablesKey(reference, filter, start, count);
        if (_handles.TryGetPage(key, out var cached)) return cached;

        var gen = _handles.Generation;
        var args = new DapVariablesArguments { VariablesReference = reference, Filter = filter, Start = start, Count = count is > 0 ? count : null };
        var resp = DapJson.Deserialize<DapVariablesResponse>(await RequestAsync("variables", args, ct)) ?? new DapVariablesResponse();
        if (gen != _handles.Generation) throw Stale("variables reference"); // resumed while the request was in flight: the data is stale
        // A hostile/buggy adapter may ignore `count`: never hand the UI (or keep) more than the cap.
        var cap = Math.Min(count is > 0 ? count.Value : _settings.MaxVariablesPerRequest, _settings.MaxVariablesPerRequest);
        var vars = resp.Variables.Take(cap).Select(DapMapping.ToCore).ToArray();
        foreach (var v in vars) _handles.RegisterHandle(v.VariablesReference);
        _handles.SetPage(key, vars);
        return vars;
    }

    public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default) =>
        OnLane(() => EvaluateCoreAsync(expression, frameId, context, ct), ct);

    private async Task<DebugEvaluateResult> EvaluateCoreAsync(string expression, int? frameId, string context, CancellationToken ct)
    {
        EnsureLive();
        if (frameId is { } f && !_handles.IsValidFrame(f)) throw Stale("stack frame");
        if (context == "repl" && !ReferenceEquals(_quirks, DebugAdapterQuirks.None))
        {
            // The Debug Console's input as this adapter understands it (GDB: an expression, or a GDB command when asked or when it is one).
            var request = _quirks.MapReplInput(expression);
            while (true)
            {
                try
                {
                    var result = await EvaluateOnceAsync(request.Expression, frameId, request.Context, ct);
                    return result with { Result = _quirks.MapReplResult(request, result.Result) };
                }
                catch (DapRequestException ex) when (request.Fallback is { } fallback && (request.FallbackWhen?.Invoke(ex.Message) ?? true))
                {
                    request = fallback;
                }
            }
        }
        return await EvaluateOnceAsync(expression, frameId, context, ct);
    }

    private async Task<DebugEvaluateResult> EvaluateOnceAsync(string expression, int? frameId, string context, CancellationToken ct)
    {
        var gen = _handles.Generation;
        var resp = DapJson.Deserialize<DapEvaluateResponse>(await RequestAsync("evaluate", new DapEvaluateArguments { Expression = expression, FrameId = frameId, Context = context }, ct)) ?? new DapEvaluateResponse();
        if (gen == _handles.Generation) _handles.RegisterHandle(resp.VariablesReference);
        return DapMapping.ToCore(resp);
    }

    public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) =>
        OnLane(() => SetVariableCoreAsync(variablesReference, name, value, ct), ct);

    private async Task<DebugVariable> SetVariableCoreAsync(int reference, string name, string value, CancellationToken ct)
    {
        EnsureLive();
        if (!_handles.IsValidHandle(reference)) throw Stale("variables reference");
        var resp = DapJson.Deserialize<DapSetVariableResponse>(await RequestAsync("setVariable", new DapSetVariableArguments { VariablesReference = reference, Name = name, Value = value }, ct)) ?? new DapSetVariableResponse();
        _handles.InvalidatePages(); // cached pages show the old value (and may show dependents): refetch on next expand
        _handles.RegisterHandle(resp.VariablesReference ?? 0);
        return new DebugVariable(name, resp.Value, resp.Type, resp.VariablesReference ?? 0, null, resp.NamedVariables, resp.IndexedVariables);
    }

    /// <summary>DAP <c>setExpression</c> (assign to an arbitrary l-value, e.g. a watch).</summary>
    public ValueTask<DebugEvaluateResult> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            if (_dapCaps.SupportsSetExpression != true) throw new NotSupportedException("The debug adapter does not support setExpression.");
            var resp = DapJson.Deserialize<DapSetVariableResponse>(await RequestAsync("setExpression", new DapSetExpressionArguments { Expression = expression, Value = value, FrameId = frameId }, ct)) ?? new DapSetVariableResponse();
            _handles.InvalidatePages();
            _handles.RegisterHandle(resp.VariablesReference ?? 0);
            return new DebugEvaluateResult(resp.Value, resp.Type, resp.VariablesReference ?? 0, resp.NamedVariables, resp.IndexedVariables);
        }, ct);

    public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) =>
        OnLane(() => CompletionsCoreAsync(frameId, text, column, ct), ct);

    private async Task<IReadOnlyList<DebugCompletionItem>> CompletionsCoreAsync(int? frameId, string text, int column, CancellationToken ct)
    {
        EnsureLive();
        if (!_caps.SupportsCompletionsRequest) return [];
        var key = (frameId, text, column);
        if (_completions.TryGet(key, out var cached)) return cached; // typing in the debug console re-asks the same prefix a lot
        var gen = _handles.Generation;
        var mapping = _quirks.MapCompletions(text, column); // GDB completes commands: an expression goes as `print <expression>`
        var sent = mapping is null ? (text, column) : (mapping.Text, mapping.Column);
        var resp = DapJson.Deserialize<DapCompletionsResponse>(await RequestAsync("completions", new DapCompletionsArguments { FrameId = frameId, Text = sent.Item1, Column = _positions.ToWireColumn(sent.Item2) }, ct)) ?? new DapCompletionsResponse();
        var items = resp.Targets.Take(500)
            .Select(t => new DebugCompletionItem(t.Label, t.Text, t.Type, t.Start is { } s ? _positions.ToCoreColumn(s) : null, t.Length))
            .Select(i => mapping is null ? i : mapping.Map(i))
            .OfType<DebugCompletionItem>()
            .DistinctBy(i => (i.Label, i.Text))
            .ToArray();
        if (gen == _handles.Generation) _completions.Set(key, items);
        return items;
    }

    // ------------------------------------------------------------------ execution control

    public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => OnLane(() => ResumeCoreAsync("continue", threadId, null, false, ct), ct);
    public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => OnLane(() => ResumeCoreAsync("next", threadId, null, false, ct), ct);
    public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => OnLane(() => ResumeCoreAsync("stepIn", threadId, null, false, ct), ct);
    public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => OnLane(() => ResumeCoreAsync("stepOut", threadId, null, false, ct), ct);

    /// <summary>Continue/step with granularity and/or single-thread control, plus the reverse-debugging plumbing (<c>stepBack</c>, <c>reverseContinue</c>).</summary>
    public ValueTask StepAsync(DebugStepKind kind, int? threadId = null, DebugStepOptions? options = null, CancellationToken ct = default)
    {
        var command = kind switch
        {
            DebugStepKind.Over => "next",
            DebugStepKind.Into => "stepIn",
            DebugStepKind.Out => "stepOut",
            DebugStepKind.Back => "stepBack",
            _ => "reverseContinue",
        };
        return OnLane(() => ResumeCoreAsync(command, threadId, options?.Granularity, options?.SingleThread ?? false, ct), ct);
    }

    public ValueTask ContinueAsync(int? threadId, bool singleThread, CancellationToken ct = default) =>
        OnLane(() => ResumeCoreAsync("continue", threadId, null, singleThread, ct), ct);

    private async Task<int> ResolveThreadAsync(int? threadId, CancellationToken ct)
    {
        if (threadId is { } t) return t;
        if (FocusedThreadId is { } focused) return focused;
        var threads = await GetThreadsCoreAsync(ct);
        return threads.Count > 0 ? threads[0].Id : throw new InvalidOperationException("The debuggee has no threads.");
    }

    private async Task ResumeCoreAsync(string command, int? threadId, string? granularity, bool singleThread, CancellationToken ct)
    {
        EnsureLive();
        var tid = await ResolveThreadAsync(threadId, ct);
        if (command is "stepBack" or "reverseContinue" && !_caps.SupportsStepBack) throw new NotSupportedException("The debug adapter does not support reverse debugging.");

        var wasStopped = State == DebugSessionState.Stopped;
        var stopsBefore = _stopCounter;
        // Optimistic: the adapter's `stopped` may follow its response immediately, so we must already be Running when it lands
        // (and handles die at the moment of resuming, not whenever the response is processed).
        if (wasStopped) ResumedLocally(tid, !singleThread);
        object args = command is "continue" or "reverseContinue"
            ? new DapContinueArguments { ThreadId = tid, SingleThread = singleThread ? true : null }
            : new DapStepArguments { ThreadId = tid, SingleThread = singleThread ? true : null, Granularity = granularity };
        try
        {
            await RequestAsync(command, args, ct);
        }
        catch
        {
            // The adapter refused: nothing resumed. Only roll back if no newer stop arrived meanwhile.
            if (wasStopped && _stopCounter == stopsBefore && State == DebugSessionState.Running) SetState(DebugSessionState.Stopped);
            throw;
        }
    }

    public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            var tid = await ResolveThreadAsync(threadId, ct);
            await RequestAsync("pause", new DapPauseArguments { ThreadId = tid }, ct);
        }, ct);

    // ------------------------------------------------------------------ misc

    public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            // The stop of a client-side exception filter is described by the session (the adapter knows no such exception).
            if (command == "exceptionInfo" && _syntheticStop is not null)
                return (JsonElement?)ToExceptionInfoBody(await SyntheticExceptionInfoAsync(ct));
            var body = await RequestAsync(command, arguments, ct);
            return body.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : body;
        }, ct);

    /// <summary>Lane only. The exception the current client-side filter stop stands for, once known (bounded wait for the panic's output).</summary>
    private async Task<DebugExceptionInfo> SyntheticExceptionInfoAsync(CancellationToken ct)
    {
        var filter = _syntheticStop!;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency; // 1 s
        while (_syntheticException is null && _syntheticStop == filter && Stopwatch.GetTimestamp() < deadline) await Task.Delay(25, ct);
        return _syntheticException ?? new DebugExceptionInfo(filter.Filter, filter.Label, "always", null, filter.Label, null);
    }

    private static JsonElement ToExceptionInfoBody(DebugExceptionInfo info) => JsonSerializer.SerializeToElement(new DapExceptionInfoResponse
    {
        ExceptionId = info.ExceptionId,
        Description = info.Description,
        BreakMode = info.BreakMode,
        Details = new DapExceptionDetails { Message = info.Message, TypeName = info.TypeName, StackTrace = info.StackTrace },
    }, DapJson.Options);

    /// <summary>Maps an adapter source to a document URI: a real path → <c>file:</c>, adapter-only content → <c>debug:/session/ref/name</c>.</summary>
    public Uri? ResolveSourceUri(DebugSource? source)
    {
        if (source is null) return null;
        try
        {
            // A remote workspace's adapter reports paths on the remote host: they map back to the workspace's URIs.
            if (_o.PathMapper is { } mapper && source.Path is { Length: > 0 } path && mapper.ToUri(path, Configuration.WorkspaceFolder) is { } remote) return remote;
            if (source.ToUri() is { } file) return file;
        }
        catch (UriFormatException) { }
        return source.SourceReference is > 0 and var reference ? DebugSourceUri.Create(Id, reference, source.Name) : null;
    }

    /// <summary>Content of a <c>sourceReference</c> source (DAP <c>source</c> request); shown read-only through <c>debug:</c> URIs.</summary>
    public ValueTask<string?> GetSourceContentAsync(int sourceReference, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            var resp = DapJson.Deserialize<DapSourceResponse>(await RequestAsync("source", new DapSourceArguments { Source = new DapSource { SourceReference = sourceReference }, SourceReference = sourceReference }, ct));
            return resp?.Content;
        }, ct);

    public ValueTask<DebugExceptionInfo> GetExceptionInfoAsync(int? threadId = null, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsExceptionInfo) throw new NotSupportedException("The debug adapter does not support exceptionInfo.");
            if (_syntheticStop is not null) return await SyntheticExceptionInfoAsync(ct);
            return await GetExceptionInfoCoreAsync(await ResolveThreadAsync(threadId, ct), ct);
        }, ct);

    private async Task<DebugExceptionInfo> GetExceptionInfoCoreAsync(int threadId, CancellationToken ct)
    {
        var r = DapJson.Deserialize<DapExceptionInfoResponse>(await RequestAsync("exceptionInfo", new DapExceptionInfoArguments { ThreadId = threadId }, ct)) ?? new DapExceptionInfoResponse();
        return new DebugExceptionInfo(r.ExceptionId, r.Description, r.BreakMode, r.Details?.Message, r.Details?.FullTypeName ?? r.Details?.TypeName, r.Details?.StackTrace);
    }

    public ValueTask<IReadOnlyList<DebugSource>> GetLoadedSourcesAsync(CancellationToken ct = default) =>
        OnLane<IReadOnlyList<DebugSource>>(async () =>
        {
            EnsureLive();
            if (_dapCaps.SupportsLoadedSourcesRequest != true) return [];
            var r = DapJson.Deserialize<DapLoadedSourcesResponse>(await RequestAsync("loadedSources", null, ct)) ?? new DapLoadedSourcesResponse();
            return [.. r.Sources.Take(5_000).Select(s => DapMapping.ToCore(s)!)];
        }, ct);

    /// <summary>Refreshes the module list from the adapter (the event stream keeps <see cref="Modules"/> current afterwards).</summary>
    public ValueTask<IReadOnlyList<DebugModuleInfo>> GetModulesAsync(CancellationToken ct = default) =>
        OnLane<IReadOnlyList<DebugModuleInfo>>(async () =>
        {
            EnsureLive();
            if (_dapCaps.SupportsModulesRequest != true) return Modules;
            var r = DapJson.Deserialize<DapModulesResponse>(await RequestAsync("modules", new DapModulesArguments { StartModule = 0, ModuleCount = 500 }, ct)) ?? new DapModulesResponse();
            lock (_modules)
                foreach (var m in r.Modules.Take(500))
                {
                    var id = m.Id.ValueKind == JsonValueKind.String ? m.Id.GetString() ?? "" : m.Id.GetRawText();
                    if (_modules.Count < MaxModules || _modules.ContainsKey(id))
                        _modules[id] = new DebugModuleInfo(id, m.Name, m.Path, m.Version, m.IsUserCode, m.SymbolStatus);
                }
            return Modules;
        }, ct);

    public ValueTask<DapReadMemoryResponse> ReadMemoryAsync(string memoryReference, int count, int offset = 0, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsReadMemory) throw new NotSupportedException("The debug adapter does not support readMemory.");
            return DapJson.Deserialize<DapReadMemoryResponse>(await RequestAsync("readMemory", new DapReadMemoryArguments { MemoryReference = memoryReference, Count = Math.Min(count, 1 << 20), Offset = offset }, ct)) ?? new DapReadMemoryResponse();
        }, ct);

    public ValueTask<IReadOnlyList<DapDisassembledInstruction>> DisassembleAsync(string memoryReference, int instructionCount, int instructionOffset = 0, CancellationToken ct = default) =>
        OnLane<IReadOnlyList<DapDisassembledInstruction>>(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsDisassemble) throw new NotSupportedException("The debug adapter does not support disassemble.");
            var r = DapJson.Deserialize<DapDisassembleResponse>(await RequestAsync("disassemble", new DapDisassembleArguments { MemoryReference = memoryReference, InstructionCount = Math.Min(instructionCount, 10_000), InstructionOffset = instructionOffset, ResolveSymbols = true }, ct)) ?? new DapDisassembleResponse();
            return r.Instructions;
        }, ct);

    public ValueTask<DapDataBreakpointInfoResponse> DataBreakpointInfoAsync(int? variablesReference, string name, int? frameId = null, CancellationToken ct = default) =>
        OnLane(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsDataBreakpoints) throw new NotSupportedException("The debug adapter does not support data breakpoints.");
            return DapJson.Deserialize<DapDataBreakpointInfoResponse>(await RequestAsync("dataBreakpointInfo", new DapDataBreakpointInfoArguments { VariablesReference = variablesReference, Name = name, FrameId = frameId }, ct)) ?? new DapDataBreakpointInfoResponse();
        }, ct);

    /// <summary>Replaces the adapter's data breakpoints (not persisted by the manager: they are tied to a live debuggee's memory).</summary>
    public ValueTask<IReadOnlyList<DapBreakpoint>> SetDataBreakpointsAsync(IReadOnlyList<DapDataBreakpoint> breakpoints, CancellationToken ct = default) =>
        OnLane<IReadOnlyList<DapBreakpoint>>(async () =>
        {
            EnsureLive();
            if (!_caps.SupportsDataBreakpoints) throw new NotSupportedException("The debug adapter does not support data breakpoints.");
            var r = DapJson.Deserialize<DapBreakpointsResponse>(await RequestAsync("setDataBreakpoints", new DapSetDataBreakpointsArguments { Breakpoints = [.. breakpoints] }, ct)) ?? new DapBreakpointsResponse();
            return r.Breakpoints;
        }, ct);

    public ValueTask<IReadOnlyList<DapBreakpoint>> SetInstructionBreakpointsAsync(IReadOnlyList<DapInstructionBreakpoint> breakpoints, CancellationToken ct = default) =>
        OnLane<IReadOnlyList<DapBreakpoint>>(async () =>
        {
            EnsureLive();
            if (_dapCaps.SupportsInstructionBreakpoints != true) throw new NotSupportedException("The debug adapter does not support instruction breakpoints.");
            var r = DapJson.Deserialize<DapBreakpointsResponse>(await RequestAsync("setInstructionBreakpoints", new DapSetInstructionBreakpointsArguments { Breakpoints = [.. breakpoints] }, ct)) ?? new DapBreakpointsResponse();
            return r.Breakpoints;
        }, ct);
}
