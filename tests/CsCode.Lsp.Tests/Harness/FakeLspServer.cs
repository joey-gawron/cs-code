using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using CsCode.Lsp.Protocol;
using CsCode.Lsp.Runtime;

namespace CsCode.Lsp.Tests.Harness;

/// <summary>A message the fake server received from the client.</summary>
public sealed record Msg(long Seq, string Method, JsonElement Params, long? Id, bool IsRequest)
{
    public T P<T>() => JsonSerializer.Deserialize<T>(Params, LspJson.Options)!;
}

public sealed class LspError(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class FakeDoc
{
    public string Text = "";
    public int Version;
    public string LanguageId = "";
    public int Changes;
    public bool Open = true;
}

/// <summary>
/// An in-process LSP peer over System.IO.Pipelines (own header framing, no StreamJsonRpc, so the client is tested against an
/// independent implementation). Records every message, mirrors document text (so incremental sync can be verified byte for byte),
/// answers requests from a handler table, and can send notifications/requests to the client or crash on demand.
/// </summary>
public sealed class FakeLspServer : ILspConnection
{
    private readonly Pipe _toServer = new(new PipeOptions(pauseWriterThreshold: 64L * 1024 * 1024, resumeWriterThreshold: 32L * 1024 * 1024));
    private readonly Pipe _toClient = new(new PipeOptions(pauseWriterThreshold: 64L * 1024 * 1024, resumeWriterThreshold: 32L * 1024 * 1024));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<Msg> _log = new();
    private long _seq;
    private long _nextRequestId = 1000;
    private int _crashed;
    private Task? _loop;

    public FakeLspServer()
    {
        Capabilities = new ServerCapabilities { TextDocumentSync = TextDocumentSyncKind.Incremental };
    }

    // ---- configuration (set BEFORE the client connects) ----
    public ServerCapabilities Capabilities { get; set; }
    public string Name { get; set; } = "fake-server";
    /// <summary>The <c>serverInfo.version</c> of the default <c>initialize</c> answer.</summary>
    public string Version { get; set; } = "1.0";
    public Func<JsonElement, Task<object?>>? InitializeHook { get; set; }
    public ConcurrentDictionary<string, Func<JsonElement, Task<object?>>> Requests { get; } = new();
    public ConcurrentDictionary<string, Action<Msg>> NotificationHandlers { get; } = new();
    /// <summary>Delay applied to every request response (simulates a slow server).</summary>
    public TimeSpan ResponseDelay { get; set; }
    public bool NeverRespond { get; set; }
    public bool ExitOnExitNotification { get; set; } = true;

    // ---- observation ----
    public ConcurrentDictionary<string, FakeDoc> Documents { get; } = new();
    public Task ClosedTask => _closed.Task;
    public bool Shutdown { get; private set; }
    public bool ExitReceived { get; private set; }

    public IReadOnlyList<Msg> Received
    {
        get { lock (_log) return _log.ToArray(); }
    }

    public IEnumerable<Msg> Of(string method) => Received.Where(m => m.Method == method);
    public int CountOf(string method) => Received.Count(m => m.Method == method);

    // ---- ILspConnection ----
    public Stream Input => _toServer.Writer.AsStream();
    public Stream Output => _toClient.Reader.AsStream();
    /// <summary>Pretend this server runs as a child process with this pid (for memory-tracking tests).</summary>
    public int? PidOverride { get; set; }
    public int? ProcessId => PidOverride;
    public Task Closed => _closed.Task;
    public string? ExitDescription => _crashed != 0 ? "fake server crashed" : null;
    public ValueTask KillAsync() { Crash(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Crash(); return ValueTask.CompletedTask; }

    public void Start()
    {
        InstallDefaults();
        _loop = Task.Run(RunAsync);
    }

    private void InstallDefaults()
    {
        Requests.TryAdd(LspMethods.Initialize, async p =>
        {
            if (InitializeHook is not null) return await InitializeHook(p).ConfigureAwait(false);
            return new InitializeResult { Capabilities = Capabilities, ServerInfo = new InitializeResultServerInfo { Name = Name, Version = Version } };
        });
        Requests.TryAdd(LspMethods.Shutdown, _ => { Shutdown = true; return Task.FromResult<object?>(null); });
    }

    // =================================================================================================================
    // Read loop
    // =================================================================================================================

    private async Task RunAsync()
    {
        var reader = _toServer.Reader;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync().ConfigureAwait(false);
                var buffer = result.Buffer;
                while (TryReadMessage(ref buffer, out var body)) Dispatch(body);
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception) { /* pipe completed by Crash */ }
        finally { Complete(); }
    }

    private static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out byte[] body)
    {
        body = [];
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> header, "\r\n\r\n"u8)) return false;
        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var length = -1;
        foreach (var line in headerText.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
        if (length < 0 || reader.Remaining < length) return false;
        body = new byte[length];
        reader.TryCopyTo(body);
        reader.Advance(length);
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private void Dispatch(byte[] body)
    {
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var hasMethod = root.TryGetProperty("method", out var m);
        var hasId = root.TryGetProperty("id", out var idEl);
        var paramsEl = root.TryGetProperty("params", out var pe) ? pe : default;

        if (!hasMethod && hasId)
        {
            // response to a request WE sent
            if (idEl.TryGetInt64(out var rid) && _pending.TryRemove(rid, out var tcs))
            {
                if (root.TryGetProperty("error", out var err)) tcs.TrySetException(new LspError(err.TryGetProperty("code", out var c) ? c.GetInt32() : 0, err.TryGetProperty("message", out var msg) ? msg.GetString()! : ""));
                else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            }
            return;
        }
        if (!hasMethod) return;

        var method = m.GetString()!;
        long? id = hasId && idEl.TryGetInt64(out var i) ? i : null;
        var message = new Msg(Interlocked.Increment(ref _seq), method, paramsEl.ValueKind == JsonValueKind.Undefined ? default : paramsEl, id, id is not null);
        lock (_log) _log.Add(message);

        if (id is null)
        {
            MirrorDocuments(message);
            if (method == LspMethods.Exit) { ExitReceived = true; if (ExitOnExitNotification) Complete(); }
            if (NotificationHandlers.TryGetValue(method, out var h)) h(message);
            return;
        }
        _ = Task.Run(() => AnswerAsync(message));
    }

    private async Task AnswerAsync(Msg request)
    {
        try
        {
            if (NeverRespond && request.Method != LspMethods.Initialize) return;
            if (ResponseDelay > TimeSpan.Zero && request.Method != LspMethods.Initialize) await Task.Delay(ResponseDelay).ConfigureAwait(false);
            if (!Requests.TryGetValue(request.Method, out var handler))
            {
                await WriteAsync(new { jsonrpc = "2.0", id = request.Id, error = new { code = -32601, message = "method not found: " + request.Method } }).ConfigureAwait(false);
                return;
            }
            var result = await handler(request.Params).ConfigureAwait(false);
            await WriteResultAsync(request.Id!.Value, result).ConfigureAwait(false);
        }
        catch (LspError e)
        {
            await WriteAsync(new { jsonrpc = "2.0", id = request.Id, error = new { code = e.Code, message = e.Message } }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (Volatile.Read(ref _crashed) == 0)
                await WriteAsync(new { jsonrpc = "2.0", id = request.Id, error = new { code = -32603, message = e.Message } }).ConfigureAwait(false);
        }
    }

    // =================================================================================================================
    // Document mirror (applies didOpen/didChange/didClose with real LSP semantics, UTF-16 positions)
    // =================================================================================================================

    private void MirrorDocuments(Msg m)
    {
        switch (m.Method)
        {
            case LspMethods.TextDocumentDidOpen:
            {
                var td = m.P<DidOpenTextDocumentParams>().TextDocument;
                Documents[td.Uri] = new FakeDoc { Text = td.Text, Version = td.Version, LanguageId = td.LanguageId };
                break;
            }
            case LspMethods.TextDocumentDidChange:
            {
                var p = m.P<DidChangeTextDocumentParams>();
                if (!Documents.TryGetValue(p.TextDocument.Uri, out var d)) break;
                lock (d)
                {
                    foreach (var c in p.ContentChanges)
                    {
                        d.Text = c.Range is null ? c.Text : Splice(d.Text, c.Range, c.Text);
                        d.Changes++;
                    }
                    d.Version = p.TextDocument.Version;
                }
                break;
            }
            case LspMethods.TextDocumentDidClose:
                if (Documents.TryGetValue(m.P<DidCloseTextDocumentParams>().TextDocument.Uri, out var closed)) closed.Open = false;
                break;
        }
    }

    public static string Splice(string text, Protocol.Range r, string replacement)
    {
        var start = OffsetAt(text, r.Start);
        var end = OffsetAt(text, r.End);
        return string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(Math.Max(start, end)));
    }

    public static int OffsetAt(string text, Position p)
    {
        var line = 0;
        var i = 0;
        while (line < p.Line && i < text.Length)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] == '\n' || text[i] == '\r') line++;
            i++;
        }
        var lineEnd = i;
        while (lineEnd < text.Length && text[lineEnd] is not ('\n' or '\r')) lineEnd++;
        return Math.Min(i + p.Character, lineEnd);
    }

    public string TextOf(string uri) { var d = Documents[uri]; lock (d) return d.Text; }

    // =================================================================================================================
    // Writing / server-initiated traffic
    // =================================================================================================================

    private Task WriteAsync(object message) => WriteFramedAsync(JsonSerializer.SerializeToUtf8Bytes(message, LspJson.Options));

    /// <summary>Response envelope written by hand: a null result must still be present on the wire (JSON-RPC requires the member).</summary>
    private Task WriteResultAsync(long id, object? result)
    {
        var body = result is null ? "null"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), LspJson.Options);
        var prefix = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":");
        var all = new byte[prefix.Length + body.Length + 1];
        prefix.CopyTo(all, 0);
        body.CopyTo(all, prefix.Length);
        all[^1] = (byte)'}';
        return WriteFramedAsync(all);
    }

    private async Task WriteFramedAsync(byte[] json)
    {
        if (Volatile.Read(ref _crashed) != 0) return;
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _crashed) != 0) return;
            await _toClient.Writer.WriteAsync(header).ConfigureAwait(false);
            await _toClient.Writer.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception) { /* client went away */ }
        finally { _writeLock.Release(); }
    }

    public Task NotifyAsync(string method, object? @params) => WriteAsync(new { jsonrpc = "2.0", method, @params });

    /// <summary>Sends raw bytes (garbage, partial frames...) to the client.</summary>
    public async Task WriteRawAsync(byte[] bytes)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _toClient.Writer.WriteAsync(bytes).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async Task<JsonElement> RequestAsync(string method, object? @params, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params }).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public async Task<Msg> WaitForAsync(string method, Func<Msg, bool>? predicate = null, TimeSpan? timeout = null, int skip = 0)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var hit = Received.Where(m => m.Method == method && (predicate?.Invoke(m) ?? true)).Skip(skip).FirstOrDefault();
            if (hit is not null) return hit;
            await Task.Delay(5).ConfigureAwait(false);
        }
        throw new TimeoutException($"Timed out waiting for {method}; received: {string.Join(", ", Received.Select(r => r.Method))}");
    }

    /// <summary>Abruptly drops the connection (what a crashed or kill -9'd server looks like to the client).</summary>
    public void Crash()
    {
        if (Interlocked.Exchange(ref _crashed, 1) != 0) return;
        try { _toClient.Writer.Complete(); } catch (InvalidOperationException) { }
        try { _toServer.Reader.Complete(); } catch (InvalidOperationException) { }
        _closed.TrySetResult();
    }

    private void Complete()
    {
        try { _toClient.Writer.Complete(); } catch (InvalidOperationException) { }
        _closed.TrySetResult();
    }
}

/// <summary>Creates a <see cref="FakeLspServer"/> per connection so restarts get a fresh peer; tests configure each one in <see cref="OnConnect"/>.</summary>
public sealed class FakeConnectionFactory : ILspConnectionFactory
{
    private readonly List<FakeLspServer> _servers = new();
    private int _failures;

    public Action<FakeLspServer, int>? OnConnect { get; set; }
    public TimeSpan ConnectDelay { get; set; }
    public int ConnectCount { get { lock (_servers) return _servers.Count; } }
    public IReadOnlyList<FakeLspServer> Servers { get { lock (_servers) return _servers.ToArray(); } }
    public FakeLspServer Last => Servers[^1];
    public List<LspLaunchRequest> Requests { get; } = new();

    /// <summary>The next <paramref name="count"/> connection attempts throw ServerNotFoundException.</summary>
    public void FailNext(int count = 1) => Interlocked.Exchange(ref _failures, count);

    /// <summary>Blocks the connecting thread synchronously (what Process.Start does), to prove callers never run that code themselves.</summary>
    public TimeSpan SyncBlock { get; set; }
    public List<string?> ConnectThreads { get; } = new();

    public async ValueTask<ILspConnection> ConnectAsync(LspLaunchRequest request, CancellationToken ct)
    {
        lock (ConnectThreads) ConnectThreads.Add(Thread.CurrentThread.Name);
        if (SyncBlock > TimeSpan.Zero) Thread.Sleep(SyncBlock);
        lock (Requests) Requests.Add(request with { OnStderrLine = null }); // keep the record, not the callback (it would retain the client)
        if (ConnectDelay > TimeSpan.Zero) await Task.Delay(ConnectDelay, ct).ConfigureAwait(false);
        if (Interlocked.Decrement(ref _failures) >= 0) throw new ServerNotFoundException(request.Definition.Command);
        Interlocked.Exchange(ref _failures, 0);
        var server = new FakeLspServer();
        int index;
        lock (_servers) { _servers.Add(server); index = _servers.Count - 1; }
        OnConnect?.Invoke(server, index);
        server.Start();
        return server;
    }

    public async Task<FakeLspServer> WaitForServerAsync(int index = 0, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            lock (_servers) if (_servers.Count > index) return _servers[index];
            await Task.Delay(5).ConfigureAwait(false);
        }
        throw new TimeoutException($"server #{index} never connected");
    }
}
