using System.Reflection;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Features;
using CsCode.Lsp.Runtime;
using StreamJsonRpc;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Extensions;

/// <summary>
/// One running server as its <see cref="LanguageServerExtension"/> sees it: send requests and notifications, receive custom notifications,
/// map positions, and keep per-server state and disposables that die with the server. Created by the client when the server starts.
/// </summary>
public sealed class ServerExtensionSession : IDisposable
{
    private static readonly MethodInfo SinkInvoke = typeof(NotificationSink).GetMethod(nameof(NotificationSink.Invoke))!;

    private readonly LanguageClient _client;
    private JsonRpc? _attaching;
    private int _disposed;
    // The connection may outlive the session (its read loop ends when the stream does): the sinks let go of the session when it ends.
    private readonly List<NotificationSink> _sinks = [];

    internal ServerExtensionSession(LanguageClient client, LanguageServerExtension extension, JsonRpc rpc)
    {
        _client = client;
        Extension = extension;
        _attaching = rpc;
    }

    public LanguageServerExtension Extension { get; }
    public LanguageServerDefinition Definition => _client.Definition;
    /// <summary>The root the server was started for (its workspace), when it has one.</summary>
    public Uri? RootUri => _client.RootUri;
    public IReadOnlyList<Uri> WorkspaceFolders => _client.WorkspaceFolders;
    public bool IsRunning => _client.IsOperational && Volatile.Read(ref _disposed) == 0;
    /// <summary>The server's <c>capabilities.experimental</c> from its <c>initialize</c> answer (null before it, or when it sent none).</summary>
    public JsonElement? ExperimentalServerCapabilities => _client.ServerCapabilities?.Experimental;
    /// <summary>The Core services (window, commands, navigation, documents…), as the LSP module has them.</summary>
    public LspServices Services => _client.Services;
    /// <summary>Released when the server stops.</summary>
    public DisposableStore Disposables { get; } = new();
    /// <summary>The extension's own per-server state (status, caches); dropped with the session.</summary>
    public object? State { get; set; }

    /// <summary>
    /// Handles a server-to-client notification (<c>experimental/serverStatus</c>). Only during <see cref="LanguageServerExtension.Attach"/>: the
    /// connection does not accept new methods once it listens. <paramref name="handler"/> runs on the Language lane with a copy of the params;
    /// <paramref name="latestWins"/> drops a queued one when a newer one arrives (status-like notifications).
    /// </summary>
    public void OnNotification(string method, Action<JsonElement> handler, bool latestWins = false)
    {
        var rpc = _attaching ?? throw new InvalidOperationException($"{Extension.Name}: notification handlers are added in Attach, before the server starts.");
        var sink = new NotificationSink(this, handler, latestWins ? new WorkKey(handler, 0) : default);
        _sinks.Add(sink);
        rpc.AddLocalRpcMethod(SinkInvoke, sink, new JsonRpcMethodAttribute(method) { UseSingleObjectParameterDeserialization = true });
    }

    internal void EndAttach() => _attaching = null;

    /// <summary>A request that is not about one document. Null when the server is not running, answered null, failed or timed out (logged).</summary>
    public ValueTask<JsonElement?> RequestAsync(string method, object? @params, CancellationToken ct, TimeSpan? timeout = null) =>
        _client.PlainRequestAsync<JsonElement?>(LanePriority.Interactive, default, async wc =>
        {
            var raw = await _client.SendAsync(method, @params, LanePriority.Interactive, wc.Token, null, 0, timeout ?? _client.Settings.BackgroundTimeout).ConfigureAwait(false);
            return raw?.Clone();
        }, ct);

    /// <summary>
    /// A request about <paramref name="document"/>: its pending edits reach the server first. Null when the server does not serve the document,
    /// is not running, answered null, failed or timed out.
    /// </summary>
    internal ValueTask<JsonElement?> RequestAsync(string method, ITextDocument document, Func<P.TextDocumentIdentifier, LspMapper, object?> @params, CancellationToken ct, TimeSpan? timeout = null) =>
        _client.ExtensionDocRequestAsync(document, async (dr) =>
        {
            var raw = await _client.SendAsync(method, @params(dr.Id, dr.Mapper), LanePriority.Interactive, dr.Token, dr.Doc, 0, timeout ?? _client.Settings.BackgroundTimeout).ConfigureAwait(false);
            return raw?.Clone();
        }, ct);

    public void Notify(string method, object? @params) => _client.NotifyRaw(method, @params);

    /// <summary>Maps positions and ranges between Core and the server's encoding (UTF-16 unless negotiated otherwise).</summary>
    internal LspMapper Mapper => _client.Mapper;

    /// <summary>Writes to the server's output channel ("Rust (rust-analyzer) (LSP)").</summary>
    public void Log(string line) => _client.Log.Add($"[{Extension.Name}] {line}");

    /// <summary>Runs <paramref name="work"/> on the Language lane (fire and forget).</summary>
    public void Post(Action work) => _client.Context.Queue.Post(LanePriority.Background, work, exempt: true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _attaching = null;
        foreach (var sink in _sinks) sink.Detach();
        _sinks.Clear();
        try { Extension.OnStopped(this); }
        finally
        {
            State = null;
            Disposables.Dispose();
        }
    }

    /// <summary>The target StreamJsonRpc calls on its reader loop: copy and enqueue, nothing else.</summary>
    private sealed class NotificationSink(ServerExtensionSession session, Action<JsonElement> handler, WorkKey key)
    {
        private ServerExtensionSession? _session = session;
        private Action<JsonElement>? _handler = handler;

        public void Detach()
        {
            _session = null;
            _handler = null;
        }

        public void Invoke(JsonElement p)
        {
            if (_session is not { } session || _handler is not { } handler || Volatile.Read(ref session._disposed) != 0) return;
            var copy = p.Clone();
            session._client.Context.Queue.Post(LanePriority.Background, () =>
            {
                if (Volatile.Read(ref session._disposed) != 0) return;
                try { handler(copy); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    session.Log($"notification handler failed: {ex.Message}");
                }
            }, key, exempt: true);
        }
    }
}
