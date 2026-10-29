using System.Buffers;
using System.Text;
using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Dap.Protocol;
using CsCode.Dap.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Client;

public sealed record DebugAdapterClientOptions
{
    /// <summary>Per-request timeout (a hung adapter must never block a caller forever).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Frames above this are skipped, not buffered; their request (if identifiable) fails with an error.</summary>
    public int MaxFrameBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>How long Dispose lets a child adapter exit by itself before killing its process tree.</summary>
    public TimeSpan CloseGrace { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Frames processed back-to-back before the read loop yields to the rest of the lane (fairness under floods).</summary>
    public int YieldEveryFrames { get; init; } = 32;
}

/// <summary>The adapter answered a request with <c>success:false</c>.</summary>
public sealed class DapRequestException(string command, string message, DapMessage? error = null) : Exception($"'{command}' failed: {message}")
{
    public string Command { get; } = command;
    public string AdapterMessage { get; } = message;
    public DapMessage? Error { get; } = error;
}

public sealed class DapTransportClosedException(string reason) : IOException(reason);

public sealed class DapRequestTimeoutException(string command, TimeSpan timeout)
    : TimeoutException($"Debug adapter did not answer '{command}' within {timeout.TotalSeconds:0.#}s")
{
    public string Command { get; } = command;
}

public sealed record DapUnknownEvent(string Name, JsonElement Body);

/// <summary>
/// Debug Adapter Protocol client (not JSON-RPC: its own <c>seq</c> numbering and request/response correlation) over any
/// <see cref="IDapTransport"/>. All reading/parsing/dispatch happens on the <see cref="DebugLane"/>; the public members are safe
/// to call from any thread and only enqueue. Event emitters fire on the lane — subscribers must be cheap and marshal themselves.
/// </summary>
public sealed class DebugAdapterClient : IAsyncDisposable
{
    private readonly IDapTransport _transport;
    private readonly DebugLane _lane;
    private readonly DebugAdapterClientOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _reverse = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _readLoop = Task.CompletedTask;
    private int _seq;
    private int _disposed;
    private int _started;
    private string? _closeReason;
    private long _oversized;

    private sealed class Pending(int seq, string command)
    {
        public int Seq { get; } = seq;
        public string Command { get; } = command;
        public TaskCompletionSource<JsonElement> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ---- typed events (fire on the lane)
    private readonly Emitter<DapStoppedEvent> _stopped = new();
    private readonly Emitter<DapContinuedEvent> _continued = new();
    private readonly Emitter<DapExitedEvent> _exited = new();
    private readonly Emitter<DapTerminatedEvent> _terminated = new();
    private readonly Emitter<DapThreadEvent> _thread = new();
    private readonly Emitter<DapOutputEvent> _output = new();
    private readonly Emitter<DapBreakpointEvent> _breakpoint = new();
    private readonly Emitter<DapModuleEvent> _module = new();
    private readonly Emitter<DapLoadedSourceEvent> _loadedSource = new();
    private readonly Emitter<DapProcessEvent> _process = new();
    private readonly Emitter<DapCapabilitiesEvent> _capabilities = new();
    private readonly Emitter<DapProgressStartEvent> _progressStart = new();
    private readonly Emitter<DapProgressUpdateEvent> _progressUpdate = new();
    private readonly Emitter<DapProgressEndEvent> _progressEnd = new();
    private readonly Emitter<DapInvalidatedEvent> _invalidated = new();
    private readonly Emitter<DapMemoryEvent> _memory = new();
    private readonly Emitter<DapUnknownEvent> _unknown = new();
    private readonly Emitter<object?> _initialized = new();
    private readonly Emitter<string> _requestTimedOut = new();
    private readonly Emitter<string> _closedEvent = new();

    public DebugAdapterClient(IDapTransport transport, DebugLane lane, DebugAdapterClientOptions? options = null, ILogger? logger = null)
    {
        _transport = transport;
        _lane = lane;
        _options = options ?? new DebugAdapterClientOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public IEventSource<object?> Initialized => _initialized;
    public IEventSource<DapStoppedEvent> Stopped => _stopped;
    public IEventSource<DapContinuedEvent> Continued => _continued;
    public IEventSource<DapExitedEvent> Exited => _exited;
    public IEventSource<DapTerminatedEvent> Terminated => _terminated;
    public IEventSource<DapThreadEvent> Thread => _thread;
    public IEventSource<DapOutputEvent> Output => _output;
    public IEventSource<DapBreakpointEvent> Breakpoint => _breakpoint;
    public IEventSource<DapModuleEvent> Module => _module;
    public IEventSource<DapLoadedSourceEvent> LoadedSource => _loadedSource;
    public IEventSource<DapProcessEvent> Process => _process;
    public IEventSource<DapCapabilitiesEvent> CapabilitiesChanged => _capabilities;
    public IEventSource<DapProgressStartEvent> ProgressStart => _progressStart;
    public IEventSource<DapProgressUpdateEvent> ProgressUpdate => _progressUpdate;
    public IEventSource<DapProgressEndEvent> ProgressEnd => _progressEnd;
    public IEventSource<DapInvalidatedEvent> Invalidated => _invalidated;
    public IEventSource<DapMemoryEvent> Memory => _memory;
    public IEventSource<DapUnknownEvent> UnknownEvent => _unknown;
    /// <summary>Fires (command name) when a request timed out: the adapter may be hung.</summary>
    public IEventSource<string> RequestTimedOut => _requestTimedOut;
    /// <summary>Fires once (with the reason) when the connection ended: EOF, crash, malformed stream or disposal.</summary>
    public IEventSource<string> ConnectionClosed => _closedEvent;

    /// <summary>Completes when the read loop has ended and every pending request has been failed.</summary>
    public Task Closed => _closed.Task;
    public bool IsClosed => _closed.Task.IsCompleted;
    public string? CloseReason => Volatile.Read(ref _closeReason);
    public int PendingRequestCount { get { lock (_gate) return _pending.Count; } }
    public long OversizedFrames => Interlocked.Read(ref _oversized);
    public IDapTransport Transport => _transport;
    /// <summary>When true, a caller-cancelled request is followed by a DAP <c>cancel</c> request (adapter advertised supportsCancelRequest).</summary>
    public bool SendCancelRequests { get; set; }

    /// <summary>Registers the handler for an adapter→client request (<c>runInTerminal</c>, <c>startDebugging</c>...).</summary>
    public void SetReverseRequestHandler(string command, Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        lock (_gate) _reverse[command] = handler;
    }

    /// <summary>Starts the read loop on the lane (idempotent).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _readLoop = _lane.RunAsync(ReadLoopAsync);
    }

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// Sends a request and awaits its response body (<c>ValueKind == Undefined</c> when the response has none).
    /// The caller pays only an enqueue; serialisation, the write and correlation happen on the lane.
    /// </summary>
    public Task<JsonElement> SendRequestAsync(string command, object? arguments = null, CancellationToken ct = default, TimeSpan? timeout = null) =>
        _lane.RunAsync(() => SendCoreAsync(command, arguments, ct, timeout ?? _options.RequestTimeout), ct);

    public async Task<TBody> SendRequestAsync<TBody>(string command, object? arguments = null, CancellationToken ct = default, TimeSpan? timeout = null) where TBody : class, new()
    {
        var body = await SendRequestAsync(command, arguments, ct, timeout).ConfigureAwait(false);
        return DapJson.Deserialize<TBody>(body) ?? new TBody();
    }

    private async Task<JsonElement> SendCoreAsync(string command, object? arguments, CancellationToken ct, TimeSpan timeout)
    {
        if (IsClosed || Volatile.Read(ref _disposed) != 0)
            throw new DapTransportClosedException(CloseReason ?? "The debug adapter connection is closed.");
        ct.ThrowIfCancellationRequested();

        var pending = new Pending(Interlocked.Increment(ref _seq), command);
        lock (_gate) _pending[pending.Seq] = pending;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout);
        try
        {
            var payload = Serialize(pending.Seq, command, arguments);
            try
            {
                await _writeLock.WaitAsync(linked.Token);
                try { await DapFrameWriter.WriteAsync(_transport.Output, payload, linked.Token); }
                finally { _writeLock.Release(); }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                throw new DapTransportClosedException(CloseReason ?? $"Writing '{command}' failed: {ex.Message}");
            }

            // Await the response without the wake-up depending on anything but: response | cancel | timeout | close.
            using (linked.Token.Register(static s => ((Pending)s!).Tcs.TrySetCanceled(), pending))
                return await pending.Tcs.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (SendCancelRequests && !IsClosed) _ = SendCancelAsync(pending.Seq);
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw new DapTransportClosedException(CloseReason ?? "The debug adapter connection was disposed.");
        }
        catch (OperationCanceledException)
        {
            // our own timeout fired
            _requestTimedOut.Fire(command);
            throw new DapRequestTimeoutException(command, timeout);
        }
        finally
        {
            lock (_gate) _pending.Remove(pending.Seq);
        }
    }

    private async Task SendCancelAsync(int requestId)
    {
        try { await SendRequestAsync("cancel", new DapCancelArguments { RequestId = requestId }, timeout: TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* best effort */ }
    }

    private static ReadOnlyMemory<byte> Serialize(int seq, string command, object? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WriteString("type", "request");
            w.WriteString("command", command);
            if (arguments is not null)
            {
                w.WritePropertyName("arguments");
                JsonSerializer.Serialize(w, arguments, arguments.GetType(), DapJson.Options);
            }
            w.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // ------------------------------------------------------------------ read loop (lane)

    private async Task ReadLoopAsync()
    {
        var reader = new DapFrameReader(_transport.Input, _options.MaxFrameBytes);
        var reason = "The debug adapter closed the connection.";
        var sinceYield = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var status = await reader.ReadAsync(_cts.Token); // resumes on the lane
                if (status == DapFrameStatus.Frame) Dispatch(reader.Payload);
                else if (status == DapFrameStatus.Oversized) OnOversized(reader);
                else
                {
                    if (status == DapFrameStatus.Malformed) reason = "The debug adapter sent a malformed message header.";
                    break;
                }
                // A flooding adapter keeps ReadAsync completing synchronously: yield so queued API calls, timers and other
                // sessions on this lane are not starved.
                if (++sinceYield >= _options.YieldEveryFrames)
                {
                    sinceYield = 0;
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) { reason = "The debug adapter connection was disposed."; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            reason = $"The debug adapter connection failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DAP read loop crashed");
            reason = $"The debug adapter connection failed: {ex.Message}";
        }
        finally
        {
            Close(reason);
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null) return;
        Pending[] pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        var tail = _transport.StandardErrorTail;
        var message = string.IsNullOrWhiteSpace(tail) ? reason : $"{reason} Adapter stderr: {tail.Trim()}";
        foreach (var p in pending) p.Tcs.TrySetException(new DapTransportClosedException(message));
        _closed.TrySetResult();
        _closedEvent.Fire(message);
    }

    private void OnOversized(DapFrameReader reader)
    {
        Interlocked.Increment(ref _oversized);
        _logger.LogWarning("Skipped oversized DAP frame ({Length} bytes, limit {Limit})", reader.SkippedLength, _options.MaxFrameBytes);
        var requestSeq = FindRequestSeq(reader.OversizedPrefix.Span);
        if (requestSeq is not { } seq) return;
        Pending? p;
        lock (_gate) _pending.TryGetValue(seq, out p);
        p?.Tcs.TrySetException(new DapRequestException(p.Command,
            $"the debug adapter's response ({reader.SkippedLength:N0} bytes) exceeds the {_options.MaxFrameBytes:N0}-byte limit; request a smaller page"));
    }

    private static int? FindRequestSeq(ReadOnlySpan<byte> prefix)
    {
        var key = "\"request_seq\""u8;
        var i = prefix.IndexOf(key);
        if (i < 0) return null;
        var j = i + key.Length;
        while (j < prefix.Length && (prefix[j] is (byte)':' or (byte)' ')) j++;
        var start = j;
        while (j < prefix.Length && prefix[j] is >= (byte)'0' and <= (byte)'9') j++;
        return j > start && int.TryParse(Encoding.ASCII.GetString(prefix[start..j]), out var seq) ? seq : null;
    }

    private void Dispatch(ReadOnlyMemory<byte> payload)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Dropping unparseable DAP message");
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl)) return;
            switch (typeEl.GetString())
            {
                case "response": OnResponse(root); break;
                case "event": OnEvent(root); break;
                case "request": OnReverseRequest(root); break;
            }
        }
    }

    private void OnResponse(JsonElement root)
    {
        if (!root.TryGetProperty("request_seq", out var rs) || !rs.TryGetInt32(out var requestSeq)) return;
        Pending? p;
        lock (_gate) _pending.TryGetValue(requestSeq, out p);
        if (p is null) return; // late answer to a cancelled / timed-out request

        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var hasBody = root.TryGetProperty("body", out var body);
        if (success)
        {
            p.Tcs.TrySetResult(hasBody ? body.Clone() : default);
            return;
        }
        var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "request failed";
        DapMessage? error = null;
        if (hasBody && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            error = DapJson.Deserialize<DapMessage>(err);
            if (error is { Format.Length: > 0 }) message = FormatMessage(error);
        }
        p.Tcs.TrySetException(new DapRequestException(p.Command, message, error));
    }

    private static string FormatMessage(DapMessage error)
    {
        var text = error.Format;
        if (error.Variables is { Count: > 0 })
            foreach (var (k, v) in error.Variables) text = text.Replace("{" + k + "}", v, StringComparison.Ordinal);
        return text;
    }

    private void OnEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var nameEl)) return;
        var name = nameEl.GetString() ?? "";
        root.TryGetProperty("body", out var body);
        try
        {
            switch (name)
            {
                case "initialized": _initialized.Fire(null); break;
                case "stopped": Fire(_stopped, body); break;
                case "continued": Fire(_continued, body); break;
                case "exited": Fire(_exited, body); break;
                case "terminated": _terminated.Fire(DapJson.Deserialize<DapTerminatedEvent>(body) ?? new DapTerminatedEvent()); break;
                case "thread": Fire(_thread, body); break;
                case "output": Fire(_output, body); break;
                case "breakpoint": Fire(_breakpoint, body); break;
                case "module": Fire(_module, body); break;
                case "loadedSource": Fire(_loadedSource, body); break;
                case "process": Fire(_process, body); break;
                case "capabilities": Fire(_capabilities, body); break;
                case "progressStart": Fire(_progressStart, body); break;
                case "progressUpdate": Fire(_progressUpdate, body); break;
                case "progressEnd": Fire(_progressEnd, body); break;
                case "invalidated": _invalidated.Fire(DapJson.Deserialize<DapInvalidatedEvent>(body) ?? new DapInvalidatedEvent()); break;
                case "memory": Fire(_memory, body); break;
                default:
                    if (_unknown.SubscriberCount > 0) _unknown.Fire(new DapUnknownEvent(name, body.ValueKind == JsonValueKind.Undefined ? default : body.Clone()));
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring DAP event '{Event}' with an unexpected body", name);
        }
    }

    private static void Fire<T>(Emitter<T> emitter, JsonElement body) where T : class
    {
        if (DapJson.Deserialize<T>(body) is { } value) emitter.Fire(value);
    }

    private void OnReverseRequest(JsonElement root)
    {
        var seq = root.TryGetProperty("seq", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
        var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        var args = root.TryGetProperty("arguments", out var a) ? a.Clone() : default;
        Func<JsonElement, CancellationToken, Task<object?>>? handler;
        lock (_gate) _reverse.TryGetValue(command, out handler);
        _ = AnswerReverseRequestAsync(seq, command, args, handler); // we are on the lane: resumes there
    }

    private async Task AnswerReverseRequestAsync(int requestSeq, string command, JsonElement args, Func<JsonElement, CancellationToken, Task<object?>>? handler)
    {
        object? body = null;
        string? error = null;
        if (handler is null) error = $"Unsupported request '{command}'";
        else
        {
            try { body = await handler(args, _cts.Token); }
            catch (OperationCanceledException) { error = "cancelled"; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reverse request '{Command}' failed", command);
                error = ex.Message;
            }
        }
        if (Volatile.Read(ref _disposed) != 0 || IsClosed) return;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", Interlocked.Increment(ref _seq));
            w.WriteString("type", "response");
            w.WriteNumber("request_seq", requestSeq);
            w.WriteBoolean("success", error is null);
            w.WriteString("command", command);
            if (error is not null) w.WriteString("message", error);
            else if (body is not null)
            {
                w.WritePropertyName("body");
                JsonSerializer.Serialize(w, body, body.GetType(), DapJson.Options);
            }
            w.WriteEndObject();
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(_options.RequestTimeout);
            await _writeLock.WaitAsync(cts.Token);
            try { await DapFrameWriter.WriteAsync(_transport.Output, buffer.WrittenMemory, cts.Token); }
            finally { _writeLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not answer reverse request '{Command}'", command);
        }
    }

    // ------------------------------------------------------------------ teardown

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { await _transport.CloseAsync(_options.CloseGrace).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Closing transport"); }
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        Close("The debug adapter connection was disposed.");
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disposing transport"); }
        ClearEmitters();
        // _cts is deliberately not disposed: a late SendRequestAsync racing with disposal must see "closed", not ObjectDisposedException.
    }

    private void ClearEmitters()
    {
        _stopped.Clear(); _continued.Clear(); _exited.Clear(); _terminated.Clear(); _thread.Clear(); _output.Clear();
        _breakpoint.Clear(); _module.Clear(); _loadedSource.Clear(); _process.Clear(); _capabilities.Clear();
        _progressStart.Clear(); _progressUpdate.Clear(); _progressEnd.Clear(); _invalidated.Clear(); _memory.Clear();
        _unknown.Clear(); _initialized.Clear(); _requestTimedOut.Clear(); _closedEvent.Clear();
        lock (_gate) _reverse.Clear();
    }
}
