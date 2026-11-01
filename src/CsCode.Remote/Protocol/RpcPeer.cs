using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CsCode.Core.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Protocol;

public sealed class RpcPeerOptions
{
    /// <summary>Thread-name prefix and log label.</summary>
    public string Name { get; init; } = "Remote";
    /// <summary>A message announcing more than this is a protocol violation and closes the connection (bounds memory per message).</summary>
    public int MaxMessageBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Dedicated threads that run request/notification handlers (never the reader, never the shared pool).</summary>
    public int HandlerThreads { get; init; } = 1;
    /// <summary>Incoming messages waiting for a handler thread; when full the reader waits (back-pressure into the transport).</summary>
    public int MaxQueuedIncoming { get; init; } = 4096;
    /// <summary>Outgoing messages waiting for the writer; exceeding it means the link is dead or hopelessly slow: the connection is closed (and the owner reconnects/resyncs) instead of growing memory or blocking a caller.</summary>
    public int MaxQueuedOutgoing { get; init; } = 100_000;
    /// <summary>Default per-call timeout of <see cref="RpcPeer.InvokeAsync{TParams,TResult}"/>.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>When set, input before a line equal to this marker is skipped (login banners / motd that a remote shell printed on stdout).</summary>
    public string? Preamble { get; init; }
    public int MaxPreambleBytes { get; init; } = 1 << 20;
    public ThreadPriority Priority { get; init; } = ThreadPriority.Normal;
    public JsonSerializerOptions Json { get; init; } = RemoteJson.Default;
}

/// <summary>
/// One end of a JSON-RPC 2.0 connection with LSP-style <c>Content-Length</c> framing, on System.Text.Json. Own threads (the "Remote lane",
/// ARCHITECTURE §6): one reader, one writer and <see cref="RpcPeerOptions.HandlerThreads"/> handler threads per connection.
/// <list type="bullet">
/// <item><b>Callers only enqueue.</b> <see cref="InvokeAsync{TParams,TResult}"/> / <see cref="Notify{T}"/> capture the (immutable) params object and return;
/// serialisation and the socket write happen on the writer thread, response deserialisation on the reader thread. A UI-thread caller never
/// touches JSON or the network.</item>
/// <item><b>Cancellable, timed out.</b> A cancelled or timed-out call completes at once (it never waits for the other end) and sends
/// <c>$/cancelRequest</c>; handlers get a token that the other end's cancel or the connection closing triggers.</item>
/// <item><b>Bounded.</b> Message size, incoming queue (reader back-pressure) and outgoing queue (overflow closes the link) are capped.</item>
/// <item><b>Ordered where it matters.</b> Notifications registered as <c>inline</c> run on the reader thread in arrival order, before any later
/// message is looked at (document sync: a request read after a change sees the change). Everything else runs on the handler threads.</item>
/// <item>Closing (EOF, protocol error, <see cref="Dispose"/>) fails every pending call with <see cref="RemoteDisconnectedException"/>.</item>
/// </list>
/// </summary>
public sealed class RpcPeer : IRpcEndpoint, IDisposable
{
    public const string CancelMethod = "$/cancelRequest";
    private static readonly byte[] HeaderPrefix = "Content-Length: "u8.ToArray();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly RpcPeerOptions _options;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Handler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, PendingCall> _pending = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _incoming = new();
    private readonly ConcurrentQueue<Outgoing> _outQueue = new();
    private readonly SemaphoreSlim _outSignal = new(0);
    private readonly BlockingCollection<Action> _work;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Emitter<Exception?> _closed = new();
    private readonly List<Thread> _threads = new();
    private long _nextId;
    private int _outCount;
    private int _started;
    private int _isClosed;

    public RpcPeer(Stream input, Stream output, RpcPeerOptions? options = null, ILogger? logger = null)
    {
        _input = input;
        _output = output;
        _options = options ?? new RpcPeerOptions();
        _json = _options.Json;
        _log = logger ?? NullLogger.Instance;
        _work = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), Math.Max(1, _options.MaxQueuedIncoming));
    }

    public JsonSerializerOptions Json => _json;
    public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

    private long _lastReceived = Environment.TickCount64;

    /// <summary><see cref="Environment.TickCount64"/> when the last message arrived (or the peer was made): the client's liveness check.</summary>
    public long LastReceivedTicks => Interlocked.Read(ref _lastReceived);

    /// <summary>Completes when the connection is closed; the result is the reason (null = orderly EOF / dispose).</summary>
    public Task<Exception?> Completion => _completion.Task;

    /// <summary>Fires once when the connection closes (on the thread that noticed).</summary>
    public IEventSource<Exception?> Closed => _closed;

    /// <summary>Calls awaiting an answer (diagnostics/tests).</summary>
    public int PendingCalls => _pending.Count;

    /// <summary>Messages not yet written (diagnostics/tests).</summary>
    public int QueuedOutgoing => Volatile.Read(ref _outCount);

    // ------------------------------------------------------------------------------------------------------------------- handlers

    private sealed record Handler(Func<JsonElement, CancellationToken, ValueTask<Outgoing.Result>>? Request, Action<JsonElement>? Notification, bool Inline);

    /// <summary>Registers a request handler. It runs on a handler thread; params are deserialised there.</summary>
    public void OnRequest<TParams, TResult>(string method, Func<TParams, CancellationToken, ValueTask<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[method] = new Handler(async (el, ct) =>
        {
            var p = el.As<TParams>(_json)!;
            var r = await handler(p, ct).ConfigureAwait(false);
            return new Outgoing.Result(r, typeof(TResult));
        }, null, false);
    }

    /// <summary>Registers a notification handler. <paramref name="inline"/> runs it on the reader thread, in order, before later messages (keep it cheap).</summary>
    public void OnNotification<TParams>(string method, Action<TParams> handler, bool inline = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[method] = new Handler(null, el => handler(el.As<TParams>(_json)!), inline);
    }

    public void RemoveHandler(string method) => _handlers.TryRemove(method, out _);

    // ------------------------------------------------------------------------------------------------------------------- start

    /// <summary>Starts the reader, writer and handler threads. Register handlers first: a message for an unknown method is answered "method not found".</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        StartThread(ReadLoop, "reader");
        StartThread(WriteLoop, "writer");
        for (var i = 0; i < Math.Max(1, _options.HandlerThreads); i++) StartThread(WorkLoop, "handler-" + (i + 1));
    }

    private void StartThread(ThreadStart body, string role)
    {
        var t = new Thread(body) { IsBackground = true, Name = $"CsCode.{_options.Name}-{role}", Priority = _options.Priority };
        lock (_threads) _threads.Add(t);
        t.Start();
    }

    // ------------------------------------------------------------------------------------------------------------------- calls

    /// <summary>
    /// Sends a request and returns its result. Enqueue-only on the calling thread. Cancelling (or the timeout: <paramref name="timeout"/>,
    /// else <see cref="RpcPeerOptions.DefaultTimeout"/>; <see cref="Timeout.InfiniteTimeSpan"/> = none) completes the task at once and tells the other end.
    /// Errors come back as the exception type the other end threw where one is mapped (<see cref="RpcErrorMapping"/>), else <see cref="RemoteException"/>.
    /// </summary>
    public Task<TResult?> InvokeAsync<TParams, TResult>(string method, TParams args, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<TResult?>(ct);
        if (IsClosed) return Task.FromException<TResult?>(new RemoteDisconnectedException($"Not connected ({method})"));
        var id = Interlocked.Increment(ref _nextId);
        var call = new PendingCall<TResult>(this, id, method);
        _pending[id] = call;
        call.Arm(ct, timeout ?? _options.DefaultTimeout);
        if (!Enqueue(new Outgoing(Outgoing.Kind.Request, id, method, args, typeof(TParams), null, 0, null)))
            call.Fail(new RemoteDisconnectedException($"Not connected ({method})"));
        if (IsClosed) call.Fail(new RemoteDisconnectedException($"Connection closed ({method})")); // raced with Close: never leave it pending
        return call.Task;
    }

    /// <summary>Sends a request whose answer nobody waits for (cleanup calls: stop a watch, abort an upload); failures are ignored.</summary>
    public void Fire<TParams>(string method, TParams args)
    {
        if (IsClosed) return;
        _ = InvokeAsync<TParams, JsonElement?>(method, args).ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Sends a notification (enqueue-only). Returns false when the connection is closed.</summary>
    public bool Notify<T>(string method, T args) =>
        !IsClosed && Enqueue(new Outgoing(Outgoing.Kind.Notification, 0, method, args, typeof(T), null, 0, null));

    private bool Enqueue(Outgoing message)
    {
        if (IsClosed) return false;
        if (Interlocked.Increment(ref _outCount) > _options.MaxQueuedOutgoing)
        {
            Interlocked.Decrement(ref _outCount);
            Close(new IOException($"{_options.Name}: more than {_options.MaxQueuedOutgoing} messages waiting to be sent; the connection is not keeping up"));
            return false;
        }
        _outQueue.Enqueue(message);
        _outSignal.Release();
        return true;
    }

    private abstract class PendingCall(RpcPeer peer, long id, string method)
    {
        protected readonly RpcPeer Peer = peer;
        public long Id { get; } = id;
        public string Method { get; } = method;
        public abstract void Complete(JsonElement result);
        public abstract void Fail(Exception error);
    }

    private sealed class PendingCall<T>(RpcPeer peer, long id, string method) : PendingCall(peer, id, method)
    {
        private readonly TaskCompletionSource<T?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _reg;
        private Timer? _timer;

        public Task<T?> Task => _tcs.Task;

        public void Arm(CancellationToken ct, TimeSpan timeout)
        {
            if (ct.CanBeCanceled) _reg = ct.UnsafeRegister(static (s, token) => ((PendingCall<T>)s!).OnCancelled(token), this);
            if (timeout != Timeout.InfiniteTimeSpan && timeout > TimeSpan.Zero)
                _timer = new Timer(static s => ((PendingCall<T>)s!).OnTimeout(), this, timeout, Timeout.InfiniteTimeSpan);
        }

        private void OnCancelled(CancellationToken token)
        {
            if (!Peer._pending.TryRemove(Id, out _)) return;
            Cleanup();
            _tcs.TrySetCanceled(token);
            Peer.Notify(CancelMethod, new CancelParams(Id));
        }

        private void OnTimeout()
        {
            if (!Peer._pending.TryRemove(Id, out _)) return;
            Cleanup();
            _tcs.TrySetException(new TimeoutException($"The remote end did not answer '{Method}' in time"));
            Peer.Notify(CancelMethod, new CancelParams(Id));
        }

        public override void Complete(JsonElement result)
        {
            Cleanup();
            try { _tcs.TrySetResult(result.As<T>(Peer._json)); }
            catch (Exception ex) { _tcs.TrySetException(new RemoteException(RpcErrorCodes.ParseError, $"Unreadable answer to '{Method}': {ex.Message}", ex)); }
        }

        public override void Fail(Exception error)
        {
            Peer._pending.TryRemove(Id, out _);
            Cleanup();
            _tcs.TrySetException(error);
        }

        private void Cleanup()
        {
            _reg.Dispose();
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }
    }

    public sealed record CancelParams(long Id);

    // ------------------------------------------------------------------------------------------------------------------- writer

    private sealed record Outgoing(Outgoing.Kind Type, long Id, string? Method, object? Params, Type? ParamsType, Outgoing.Result? Value, int ErrorCode, string? ErrorMessage)
    {
        public enum Kind { Request, Notification, Response, Error }
        public sealed record Result(object? Value, Type Type);
    }

    private void WriteLoop()
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });
        Span<byte> digits = stackalloc byte[16];
        try
        {
            while (!IsClosed)
            {
                _outSignal.Wait();
                var wrote = false;
                while (_outQueue.TryDequeue(out var message))
                {
                    Interlocked.Decrement(ref _outCount);
                    buffer.ResetWrittenCount();
                    writer.Reset(buffer);
                    if (!TrySerialize(writer, message)) continue;
                    writer.Flush();
                    if (buffer.WrittenCount > _options.MaxMessageBytes)
                    {
                        FailOutgoing(message, new IOException($"'{message.Method}' message of {buffer.WrittenCount} bytes exceeds the {_options.MaxMessageBytes}-byte limit"));
                        continue;
                    }
                    _output.Write(HeaderPrefix);
                    buffer.WrittenCount.TryFormat(digits, out var n, default, CultureInfo.InvariantCulture);
                    _output.Write(digits[..n]);
                    _output.Write("\r\n\r\n"u8);
                    _output.Write(buffer.WrittenSpan);
                    wrote = true;
                    if (buffer.Capacity > 4 * 1024 * 1024) { buffer = new ArrayBufferWriter<byte>(16 * 1024); writer.Reset(buffer); } // do not keep a huge one-off buffer
                }
                if (wrote) _output.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            Close(IsClosed ? null : new IOException($"{_options.Name}: write failed: {ex.Message}", ex));
        }
    }

    private bool TrySerialize(Utf8JsonWriter w, Outgoing m)
    {
        try
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc"u8, "2.0"u8);
            switch (m.Type)
            {
                case Outgoing.Kind.Request:
                    w.WriteNumber("id"u8, m.Id);
                    w.WriteString("method"u8, m.Method);
                    WriteValue(w, "params"u8, m.Params, m.ParamsType!);
                    break;
                case Outgoing.Kind.Notification:
                    w.WriteString("method"u8, m.Method);
                    WriteValue(w, "params"u8, m.Params, m.ParamsType!);
                    break;
                case Outgoing.Kind.Response:
                    w.WriteNumber("id"u8, m.Id);
                    if (m.Value?.Value is null) w.WriteNull("result"u8);
                    else WriteValue(w, "result"u8, m.Value.Value, m.Value.Type);
                    break;
                case Outgoing.Kind.Error:
                    w.WriteNumber("id"u8, m.Id);
                    w.WriteStartObject("error"u8);
                    w.WriteNumber("code"u8, m.ErrorCode);
                    w.WriteString("message"u8, m.ErrorMessage ?? "error");
                    w.WriteEndObject();
                    break;
            }
            w.WriteEndObject();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _log.LogError(ex, "{Peer}: could not serialise '{Method}'", _options.Name, m.Method);
            FailOutgoing(m, ex);
            return false;
        }
    }

    private void FailOutgoing(Outgoing m, Exception ex)
    {
        switch (m.Type)
        {
            case Outgoing.Kind.Request:
                if (_pending.TryRemove(m.Id, out var call)) call.Fail(new RemoteException(RpcErrorCodes.InvalidParams, $"Could not send '{m.Method}': {ex.Message}", ex));
                break;
            case Outgoing.Kind.Response:
                Enqueue(new Outgoing(Outgoing.Kind.Error, m.Id, m.Method, null, null, null, RpcErrorCodes.InternalError, "Could not serialise the result: " + ex.Message));
                break;
        }
    }

    private void WriteValue(Utf8JsonWriter w, ReadOnlySpan<byte> name, object? value, Type type)
    {
        w.WritePropertyName(name);
        if (value is JsonElement el) el.WriteTo(w);
        else JsonSerializer.Serialize(w, value, value is null || type == typeof(object) ? value?.GetType() ?? typeof(object) : type, _json);
    }

    // ------------------------------------------------------------------------------------------------------------------- reader

    private void ReadLoop()
    {
        Exception? reason = null;
        try
        {
            using var input = new BufferedStream(_input, 64 * 1024);
            if (_options.Preamble is { } marker && !SkipPreamble(input, marker)) return;
            while (!IsClosed)
            {
                var length = ReadHeaders(input);
                if (length < 0) break; // orderly EOF between messages
                if (length > _options.MaxMessageBytes)
                    throw new InvalidDataException($"{_options.Name}: incoming message of {length} bytes exceeds the {_options.MaxMessageBytes}-byte limit");
                var body = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                try
                {
                    input.ReadExactly(body, 0, length);
                    Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                    Dispatch(body.AsMemory(0, length));
                }
                finally { ArrayPool<byte>.Shared.Return(body); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or JsonException or EndOfStreamException or InvalidOperationException)
        {
            reason = IsClosed ? null : ex is EndOfStreamException ? new IOException($"{_options.Name}: connection closed in the middle of a message", ex) : ex;
        }
        finally { Close(reason); }
    }

    /// <summary>Skips bytes until a line that equals <paramref name="marker"/>; false at EOF.</summary>
    private bool SkipPreamble(Stream input, string marker)
    {
        var target = Encoding.ASCII.GetBytes(marker);
        var line = new List<byte>(128);
        var skipped = 0;
        var junk = new StringBuilder();
        while (true)
        {
            var b = input.ReadByte();
            if (b < 0) { if (junk.Length > 0) _log.LogWarning("{Peer}: output before the server started: {Junk}", _options.Name, junk.ToString()); return false; }
            if (b == '\n')
            {
                var len = line.Count > 0 && line[^1] == '\r' ? line.Count - 1 : line.Count;
                if (len == target.Length && line.Take(len).SequenceEqual(target))
                {
                    if (junk.Length > 0) _log.LogInformation("{Peer}: skipped output before the server started: {Junk}", _options.Name, junk.ToString());
                    return true;
                }
                if (junk.Length < 4096) junk.Append(Encoding.UTF8.GetString(line.ToArray())).Append('\n');
                line.Clear();
            }
            else line.Add((byte)b);
            if (++skipped > _options.MaxPreambleBytes) throw new InvalidDataException($"{_options.Name}: no server greeting in the first {_options.MaxPreambleBytes} bytes: {junk}");
        }
    }

    /// <summary>Reads the header block; returns the content length, or -1 at a clean EOF before any header byte.</summary>
    private static int ReadHeaders(Stream input)
    {
        var length = -1;
        Span<byte> line = stackalloc byte[256];
        var first = true;
        while (true)
        {
            var n = 0;
            while (true)
            {
                var b = input.ReadByte();
                if (b < 0)
                {
                    if (first && n == 0) return -1;
                    throw new EndOfStreamException();
                }
                first = false;
                if (b == '\n') break;
                if (n == line.Length) throw new InvalidDataException("Header line too long");
                line[n++] = (byte)b;
            }
            if (n > 0 && line[n - 1] == '\r') n--;
            if (n == 0)
            {
                if (length < 0) throw new InvalidDataException("Message without Content-Length");
                return length;
            }
            var text = line[..n];
            if (text.StartsWith("Content-Length:"u8) || text.StartsWith("content-length:"u8))
            {
                var value = Encoding.ASCII.GetString(text[15..]).Trim();
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0)
                    throw new InvalidDataException($"Bad Content-Length '{value}'");
            }
            // other headers (Content-Type) are accepted and ignored
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A JSON-RPC message must be an object");
        var hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number;
        if (root.TryGetProperty("method", out var methodEl) && methodEl.GetString() is { } method)
        {
            var @params = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            if (hasId) OnIncomingRequest(idEl.GetInt64(), method, @params);
            else OnIncomingNotification(method, @params);
            return;
        }
        if (!hasId) return; // malformed: nothing to answer
        var id = idEl.GetInt64();
        if (!_pending.TryRemove(id, out var call)) return; // cancelled or timed out already
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : RpcErrorCodes.InternalError;
            var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error";
            call.Fail(RpcErrorMapping.ToException(code, message, call.Method));
        }
        else call.Complete(root.TryGetProperty("result", out var result) ? result : default);
    }

    private void OnIncomingNotification(string method, JsonElement @params)
    {
        if (method == CancelMethod)
        {
            if (@params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
                && _incoming.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            return;
        }
        if (!_handlers.TryGetValue(method, out var h) || h.Notification is null) { _log.LogDebug("{Peer}: no handler for notification {Method}", _options.Name, method); return; }
        if (h.Inline) RunNotification(method, h, @params);
        else Post(() => RunNotification(method, h, @params));
    }

    private void RunNotification(string method, Handler h, JsonElement @params)
    {
        try { h.Notification!(@params); }
        catch (Exception ex) { _log.LogWarning(ex, "{Peer}: notification handler for {Method} failed", _options.Name, method); }
    }

    private void OnIncomingRequest(long id, string method, JsonElement @params)
    {
        if (!_handlers.TryGetValue(method, out var h) || h.Request is null)
        {
            Enqueue(new Outgoing(Outgoing.Kind.Error, id, method, null, null, null, RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'"));
            return;
        }
        var cts = new CancellationTokenSource();
        _incoming[id] = cts;
        if (IsClosed) cts.Cancel();
        Post(() => RunRequest(id, method, h, @params, cts));
    }

    private void RunRequest(long id, string method, Handler h, JsonElement @params, CancellationTokenSource cts)
    {
        ValueTask<Outgoing.Result> pending;
        try { pending = h.Request!(@params, cts.Token); }
        catch (Exception ex) { Answer(id, method, null, ex, cts); return; }
        if (pending.IsCompleted)
        {
            try { Answer(id, method, pending.Result, null, cts); }
            catch (Exception ex) { Answer(id, method, null, ex, cts); }
            return;
        }
        _ = AwaitAndAnswerAsync(id, method, pending, cts);
    }

    private async Task AwaitAndAnswerAsync(long id, string method, ValueTask<Outgoing.Result> pending, CancellationTokenSource cts)
    {
        try { Answer(id, method, await pending.ConfigureAwait(false), null, cts); }
        catch (Exception ex) { Answer(id, method, null, ex, cts); }
    }

    private void Answer(long id, string method, Outgoing.Result? result, Exception? error, CancellationTokenSource cts)
    {
        _incoming.TryRemove(id, out _);
        cts.Dispose();
        if (error is null)
        {
            Enqueue(new Outgoing(Outgoing.Kind.Response, id, method, null, null, result, 0, null));
            return;
        }
        var code = RpcErrorMapping.CodeOf(error);
        if (code == RpcErrorCodes.InternalError) _log.LogWarning(error, "{Peer}: handler for {Method} failed", _options.Name, method);
        Enqueue(new Outgoing(Outgoing.Kind.Error, id, method, null, null, null, code, error.Message));
    }

    private void Post(Action work)
    {
        try { _work.Add(work); } // bounded: blocks the reader when the handlers are behind (back-pressure into the transport)
        catch (InvalidOperationException) { } // closed
    }

    private void WorkLoop()
    {
        try
        {
            foreach (var item in _work.GetConsumingEnumerable())
            {
                try { item(); }
                catch (Exception ex) { _log.LogWarning(ex, "{Peer}: handler failed", _options.Name); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------------------------------------------------------- close

    /// <summary>Closes the connection now: pending calls fail, handler tokens are cancelled, the streams are disposed. Idempotent.</summary>
    public void Close(Exception? reason = null)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0) return;
        if (reason is not null) _log.LogInformation("{Peer}: connection closed: {Reason}", _options.Name, reason.Message);
        var error = new RemoteDisconnectedException(reason is null ? "The remote connection was closed" : "The remote connection was lost: " + reason.Message, reason);
        foreach (var call in _pending.Values.ToArray()) call.Fail(error);
        foreach (var cts in _incoming.Values.ToArray()) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        _work.CompleteAdding();
        _outSignal.Release();
        while (_outQueue.TryDequeue(out _)) Interlocked.Decrement(ref _outCount);
        // Off this thread: on Unix, disposing a process pipe with a read (or a write) still in flight waits for that call to return, and over a link
        // that went silent (its process alive, no bytes coming) it never does, so Close hung and the reconnect never started (found against a real
        // host). The streams close once the transport process is stopped, which the owner of the connection does next.
        var output = _output;
        var input = _input;
        _ = Task.Run(() =>
        {
            try { output.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { input.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        _completion.TrySetResult(reason);
        _closed.Fire(reason);
        _closed.Clear();
    }

    /// <summary>
    /// Sends everything already queued, then closes. Bounded by <paramref name="timeout"/>; never throws.
    /// </summary>
    public async Task FlushAndCloseAsync(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!IsClosed && Volatile.Read(ref _outCount) > 0 && Environment.TickCount64 < deadline)
            await Task.Delay(5).ConfigureAwait(false);
        Close();
    }

    public void Dispose() => Close();

    /// <summary>Threads of this peer (tests assert they end after close; the reader ends once the transport delivers EOF).</summary>
    internal IReadOnlyList<Thread> Threads { get { lock (_threads) return _threads.ToArray(); } }
}
