using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CsCode.Dap.Protocol;
using CsCode.Dap.Transport;

namespace CsCode.Dap.Tests.Fakes;

/// <summary>One direction of an in-memory pipe (bounded, so a fast writer feels back-pressure like with a real OS pipe).</summary>
public sealed class ByteChannelStream : Stream
{
    private readonly Channel<byte[]> _channel;
    private byte[]? _current;
    private int _position;

    public ByteChannelStream(int capacity = 256) =>
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public void CompleteWriting() => _channel.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _position >= _current.Length)
        {
            try
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            }
            catch (ChannelClosedException) { return 0; }
            if (_channel.Reader.TryRead(out var next)) { _current = next; _position = 0; }
        }
        var n = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsMemory(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { await _channel.Writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new IOException("pipe closed"); }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class InMemoryTransport : IDapTransport
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ByteChannelStream _toAdapter, _fromAdapter;
    private int _disposed;

    public InMemoryTransport(ByteChannelStream fromAdapter, ByteChannelStream toAdapter)
    {
        _fromAdapter = fromAdapter;
        _toAdapter = toAdapter;
    }

    public Stream Input => _fromAdapter;
    public Stream Output => _toAdapter;
    public Task Completion => _completion.Task;
    public int? ProcessId => null;
    public string Description => "in-memory fake adapter";
    public string StandardErrorTail => "";
    public bool Closed => Volatile.Read(ref _disposed) != 0;

    public ValueTask CloseAsync(TimeSpan grace)
    {
        _toAdapter.CompleteWriting(); // adapter sees EOF on its input, like a closed stdin
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return CloseAsync(TimeSpan.Zero);
    }
}

public sealed record FakeRequest(int Seq, string Command, JsonElement Arguments, DateTime ReceivedUtc)
{
    public T Args<T>() where T : class, new() => DapJson.Deserialize<T>(Arguments) ?? new T();
}

public sealed class FakeAdapterError(string message) : Exception(message);

/// <summary>
/// A scriptable debug adapter living in the test process. It implements a plausible default behaviour for the whole
/// session handshake and lets a test override/stall any command, inject events, and send reverse requests.
/// </summary>
public sealed class FakeDebugAdapter : IAsyncDisposable
{
    private readonly ByteChannelStream _toAdapter = new(), _fromAdapter = new();
    private readonly object _gate = new();
    private readonly List<FakeRequest> _received = new();
    private readonly Dictionary<string, Func<FakeRequest, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stalled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Task>> _after = new(StringComparer.Ordinal);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _reverseResponses = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _seq, _nextBreakpointId = 1000;
    private int _pendingResponseDelayMs;
    private bool _crashed;

    public FakeDebugAdapter()
    {
        Transport = new InMemoryTransport(_fromAdapter, _toAdapter);
        _loop = Task.Run(RunAsync);
    }

    public InMemoryTransport Transport { get; }

    // ---- behaviour knobs
    public JsonObject Capabilities { get; } = new()
    {
        ["supportsConfigurationDoneRequest"] = true,
        ["supportsFunctionBreakpoints"] = true,
        ["supportsConditionalBreakpoints"] = true,
        ["supportsHitConditionalBreakpoints"] = true,
        ["supportsLogPoints"] = true,
        ["supportsSetVariable"] = true,
        ["supportsEvaluateForHovers"] = true,
        ["supportsCompletionsRequest"] = true,
        ["supportsExceptionInfoRequest"] = true,
        ["exceptionBreakpointFilters"] = new JsonArray(
            new JsonObject { ["filter"] = "all", ["label"] = "All Exceptions", ["default"] = false },
            new JsonObject { ["filter"] = "user-unhandled", ["label"] = "User-Unhandled Exceptions", ["default"] = true }),
    };

    /// <summary>true: <c>initialized</c> follows the initialize response; false: it follows the launch/attach request.</summary>
    public bool InitializedAfterInitialize { get; set; } = true;
    /// <summary>Real adapters (debugpy, js-debug) answer launch only after configurationDone.</summary>
    public bool RespondToLaunchAfterConfigurationDone { get; set; }
    public bool NeverSendInitialized { get; set; }

    public IReadOnlyList<DapStackFrame> Frames { get; set; } =
    [
        new DapStackFrame { Id = 100, Name = "Main", Source = new DapSource { Name = "Program.cs", Path = "/work/Program.cs" }, Line = 10, Column = 5 },
        new DapStackFrame { Id = 101, Name = "Caller", Source = new DapSource { Name = "Program.cs", Path = "/work/Program.cs" }, Line = 20, Column = 1 },
    ];
    public int? TotalFrames { get; set; }
    public Dictionary<int, List<DapVariable>> VariablesByReference { get; } = new();
    public Dictionary<int, string> SourceContents { get; } = new();

    // ---- observation
    public IReadOnlyList<FakeRequest> Received { get { lock (_gate) return [.. _received]; } }
    public int CountOf(string command) { lock (_gate) return _received.Count(r => r.Command == command); }
    public IReadOnlyList<FakeRequest> RequestsOf(string command) { lock (_gate) return [.. _received.Where(r => r.Command == command)]; }
    public IReadOnlyList<string> Commands { get { lock (_gate) return [.. _received.Select(r => r.Command)]; } }

    public void On(string command, Func<FakeRequest, object?> handler) => _handlers[command] = r => Task.FromResult(handler(r));
    public void OnAsync(string command, Func<FakeRequest, Task<object?>> handler) => _handlers[command] = handler;
    /// <summary>Runs right after the response to <paramref name="command"/> is on the wire (events that must FOLLOW a response).</summary>
    public void AfterResponse(string command, Func<Task> action) => _after[command] = action;
    public void Stall(string command) { lock (_gate) _stalled.Add(command); }
    public void Unstall(string command) { lock (_gate) _stalled.Remove(command); }
    public void DelayAllResponses(int milliseconds) => Volatile.Write(ref _pendingResponseDelayMs, milliseconds);

    public async Task WaitForRequestAsync(string command, int count = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (CountOf(command) < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fake adapter never received {count}x '{command}' (got {CountOf(command)}; saw: {string.Join(",", Commands)})");
            await Task.Delay(5);
        }
    }

    // ---- sending
    public Task SendEventAsync(string name, object? body = null) =>
        WriteAsync(w =>
        {
            w.WriteNumber("seq", Interlocked.Increment(ref _seq));
            w.WriteString("type", "event");
            w.WriteString("event", name);
            if (body is not null) { w.WritePropertyName("body"); JsonSerializer.Serialize(w, body, body.GetType(), DapJson.Options); }
        });

    public Task SendStoppedAsync(string reason = "breakpoint", int threadId = 1, bool allThreads = true, IEnumerable<int>? hitBreakpointIds = null, string? text = null) =>
        SendEventAsync("stopped", new DapStoppedEvent { Reason = reason, ThreadId = threadId, AllThreadsStopped = allThreads, HitBreakpointIds = hitBreakpointIds?.ToList(), Text = text });

    public Task SendOutputAsync(string text, string category = "console") =>
        SendEventAsync("output", new DapOutputEvent { Category = category, Output = text });

    public Task SendTerminatedAsync() => SendEventAsync("terminated", new DapTerminatedEvent());
    public Task SendExitedAsync(int code) => SendEventAsync("exited", new DapExitedEvent { ExitCode = code });

    /// <summary>Adapter→client request; returns the client's response body (throws FakeAdapterError on success:false).</summary>
    public async Task<JsonElement> RequestFromAdapterAsync(string command, object? arguments, int timeoutMs = 5000)
    {
        var seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _reverseResponses[seq] = tcs;
        await WriteAsync(w =>
        {
            w.WriteNumber("seq", seq);
            w.WriteString("type", "request");
            w.WriteString("command", command);
            if (arguments is not null) { w.WritePropertyName("arguments"); JsonSerializer.Serialize(w, arguments, arguments.GetType(), DapJson.Options); }
        });
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    /// <summary>Raw bytes straight onto the wire (for malformed-frame and oversize tests).</summary>
    public async Task WriteRawAsync(byte[] bytes)
    {
        await _writeLock.WaitAsync();
        try { await _fromAdapter.WriteAsync(bytes); }
        finally { _writeLock.Release(); }
    }

    public async Task WriteLargeFrameAsync(int announcedBytes, string prefixJson, Func<int, byte[]> chunkFactory)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {announcedBytes}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _fromAdapter.WriteAsync(header);
            var prefix = Encoding.UTF8.GetBytes(prefixJson);
            await _fromAdapter.WriteAsync(prefix);
            var remaining = announcedBytes - prefix.Length;
            while (remaining > 0)
            {
                var chunk = chunkFactory(Math.Min(remaining, 64 * 1024));
                await _fromAdapter.WriteAsync(chunk);
                remaining -= chunk.Length;
            }
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Hard crash: the adapter's output just ends.</summary>
    public void Crash()
    {
        _crashed = true;
        _fromAdapter.CompleteWriting();
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> body)
    {
        if (_crashed) return;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        await _writeLock.WaitAsync();
        try { await DapFrameWriter.WriteAsync(_fromAdapter, buffer.WrittenMemory, CancellationToken.None); }
        catch (IOException) { }
        finally { _writeLock.Release(); }
    }

    // ---- server loop
    private async Task RunAsync()
    {
        var reader = new DapFrameReader(_toAdapter);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var status = await reader.ReadAsync(_cts.Token);
                if (status != DapFrameStatus.Frame) break;
                using var doc = JsonDocument.Parse(reader.Payload);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "response")
                {
                    var rs = root.GetProperty("request_seq").GetInt32();
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_gate) _reverseResponses.Remove(rs, out tcs);
                    if (tcs is null) continue;
                    if (root.GetProperty("success").GetBoolean())
                        tcs.TrySetResult(root.TryGetProperty("body", out var b) ? b.Clone() : default);
                    else tcs.TrySetException(new FakeAdapterError(root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "failed"));
                    continue;
                }
                if (type != "request") continue;
                var req = new FakeRequest(root.GetProperty("seq").GetInt32(), root.GetProperty("command").GetString()!,
                    root.TryGetProperty("arguments", out var a) ? a.Clone() : default, DateTime.UtcNow);
                lock (_gate) _received.Add(req);
                _ = HandleAsync(req); // concurrently, so one stalled request doesn't wedge the adapter
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task HandleAsync(FakeRequest req)
    {
        bool stalled;
        lock (_gate) stalled = _stalled.Contains(req.Command);
        if (stalled) return;
        var delay = Volatile.Read(ref _pendingResponseDelayMs);
        if (delay > 0) await Task.Delay(delay);

        object? body = null;
        string? error = null;
        try
        {
            if (_handlers.TryGetValue(req.Command, out var custom)) body = await custom(req);
            else body = await DefaultAsync(req);
        }
        catch (FakeAdapterError ex) { error = ex.Message; }
        catch (Exception ex) { error = ex.Message; }
        if (body is DeferResponse) return;

        await RespondAsync(req, body, error);
        await AfterResponseAsync(req);
    }

    private sealed class DeferResponse;
    private TaskCompletionSource? _deferredLaunch;
    private FakeRequest? _launchRequest;

    private Task RespondAsync(FakeRequest req, object? body, string? error = null) =>
        WriteAsync(w =>
        {
            w.WriteNumber("seq", Interlocked.Increment(ref _seq));
            w.WriteString("type", "response");
            w.WriteNumber("request_seq", req.Seq);
            w.WriteBoolean("success", error is null);
            w.WriteString("command", req.Command);
            if (error is not null)
            {
                w.WriteString("message", error);
            }
            else if (body is not null)
            {
                w.WritePropertyName("body");
                if (body is JsonNode node) node.WriteTo(w);
                else JsonSerializer.Serialize(w, body, body.GetType(), DapJson.Options);
            }
        });

    private async Task AfterResponseAsync(FakeRequest req)
    {
        if (_after.TryGetValue(req.Command, out var hook)) await hook();
        switch (req.Command)
        {
            case "initialize" when InitializedAfterInitialize && !NeverSendInitialized:
                await SendEventAsync("initialized");
                break;
            case "launch" or "attach" when !InitializedAfterInitialize && !NeverSendInitialized:
                await SendEventAsync("initialized");
                break;
            case "terminate":
                await SendTerminatedAsync();
                break;
            case "disconnect":
                _fromAdapter.CompleteWriting();
                break;
        }
    }

    private async Task<object?> DefaultAsync(FakeRequest req)
    {
        switch (req.Command)
        {
            case "initialize":
                return Capabilities.DeepClone();
            case "launch" or "attach":
                if (RespondToLaunchAfterConfigurationDone)
                {
                    _launchRequest = req;
                    _deferredLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!InitializedAfterInitialize && !NeverSendInitialized) await SendEventAsync("initialized");
                    _ = Task.Run(async () =>
                    {
                        await _deferredLaunch.Task;
                        await RespondAsync(req, null);
                    });
                    return new DeferResponse();
                }
                return null;
            case "configurationDone":
                _deferredLaunch?.TrySetResult();
                return null;
            case "setBreakpoints":
            {
                var a = req.Args<DapSetBreakpointsArguments>();
                return new DapBreakpointsResponse
                {
                    Breakpoints = [.. (a.Breakpoints ?? []).Select(b => new DapBreakpoint { Id = Interlocked.Increment(ref _nextBreakpointId), Verified = true, Line = b.Line })],
                };
            }
            case "setFunctionBreakpoints":
            {
                var a = req.Args<DapSetFunctionBreakpointsArguments>();
                return new DapBreakpointsResponse { Breakpoints = [.. a.Breakpoints.Select(_ => new DapBreakpoint { Id = Interlocked.Increment(ref _nextBreakpointId), Verified = true })] };
            }
            case "setExceptionBreakpoints":
                return null;
            case "threads":
                return new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "main" }] };
            case "stackTrace":
            {
                var a = req.Args<DapStackTraceArguments>();
                var frames = Frames.ToList();
                if (a.StartFrame is { } s && Capabilities["supportsDelayedStackTraceLoading"]?.GetValue<bool>() == true)
                    frames = [.. frames.Skip(s)];
                if (a.Levels is { } l and > 0 && Capabilities["supportsDelayedStackTraceLoading"]?.GetValue<bool>() == true)
                    frames = [.. frames.Take(l)];
                return new DapStackTraceResponse { StackFrames = frames, TotalFrames = TotalFrames ?? Frames.Count };
            }
            case "scopes":
                return new DapScopesResponse
                {
                    Scopes =
                    [
                        new DapScope { Name = "Locals", VariablesReference = 1, Expensive = false },
                        new DapScope { Name = "Globals", VariablesReference = 2, Expensive = true },
                    ],
                };
            case "variables":
            {
                var a = req.Args<DapVariablesArguments>();
                if (!VariablesByReference.TryGetValue(a.VariablesReference, out var list)) list = [];
                var page = list.AsEnumerable();
                if (a.Start is { } st) page = page.Skip(st);
                if (a.Count is { } c and > 0) page = page.Take(c);
                return new DapVariablesResponse { Variables = [.. page] };
            }
            case "evaluate":
            {
                var a = req.Args<DapEvaluateArguments>();
                return new DapEvaluateResponse { Result = $"<{a.Expression}@{a.Context}>", Type = "string" };
            }
            case "setVariable":
            {
                var a = req.Args<DapSetVariableArguments>();
                return new DapSetVariableResponse { Value = a.Value, Type = "int" };
            }
            case "completions":
                return new DapCompletionsResponse { Targets = [new DapCompletionItem { Label = "foo", Text = "foo", Type = "method", Start = 0, Length = 1 }] };
            case "continue":
                return new DapContinueResponse { AllThreadsContinued = true };
            case "next" or "stepIn" or "stepOut" or "pause" or "stepBack" or "reverseContinue" or "setDataBreakpoints" or "setInstructionBreakpoints" or "cancel":
                return null;
            case "source":
            {
                var a = req.Args<DapSourceArguments>();
                var r = a.Source?.SourceReference ?? a.SourceReference;
                return new DapSourceResponse { Content = SourceContents.GetValueOrDefault(r, "// no source") };
            }
            case "exceptionInfo":
                return new DapExceptionInfoResponse { ExceptionId = "System.InvalidOperationException", Description = "boom", BreakMode = "always", Details = new DapExceptionDetails { Message = "boom", TypeName = "InvalidOperationException", StackTrace = "at Main()" } };
            case "terminate" or "disconnect" or "restart":
                return null;
            case "modules":
                return new DapModulesResponse { Modules = [new DapModule { Id = JsonSerializer.SerializeToElement("m1"), Name = "app.dll" }], TotalModules = 1 };
            case "loadedSources":
                return new DapLoadedSourcesResponse { Sources = [new DapSource { Name = "Program.cs", Path = "/work/Program.cs" }] };
            case "readMemory":
                return new DapReadMemoryResponse { Address = "0x1000", Data = Convert.ToBase64String([1, 2, 3, 4]) };
            case "disassemble":
                return new DapDisassembleResponse { Instructions = [new DapDisassembledInstruction { Address = "0x1000", Instruction = "nop" }] };
            case "dataBreakpointInfo":
                return new DapDataBreakpointInfoResponse { DataId = "d1", Description = "data", AccessTypes = ["write"] };
            default:
                throw new FakeAdapterError($"unsupported request '{req.Command}'");
        }
    }

    private int _disposedFlag;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        _cts.Cancel();
        _fromAdapter.CompleteWriting();
        _toAdapter.CompleteWriting();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _cts.Dispose();
    }
}
