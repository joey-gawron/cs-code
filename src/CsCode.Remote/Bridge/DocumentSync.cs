using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Bridge;

/// <summary>
/// Open documents sync client → server through <see cref="IDocumentManager"/> events: full text on open and on every (re)connect, then the editor's
/// own incremental changes with its own version numbers. The editor's event handlers only enqueue (the typing path never waits): one FIFO
/// sync thread reads text and hands messages to the connection in order, so a change can never overtake the open it belongs to. Documents larger
/// than <see cref="MaxDocumentChars"/> are not synced (language servers do not get them locally either).
/// </summary>
public sealed class DocumentSync : IRemoteParticipant, IDisposable
{
    public const int MaxDocumentChars = 10 * 1024 * 1024;

    private readonly IDocumentManager _documents;
    private readonly UriMapper _mapper;
    private readonly ILogger _log;
    private readonly DisposableStore _subs = new();
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Dictionary<Uri, int> _sent = new(); // sync-thread only
    private readonly Thread _thread;
    private RemoteSession? _session; // sync-thread only

    public DocumentSync(IDocumentManager documents, UriMapper mapper, ILogger? log = null)
    {
        _documents = documents;
        _mapper = mapper;
        _log = log ?? NullLogger.Instance;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CsCode.Remote-docsync" };
        _thread.Start();
        _subs.Add(documents.Opened.Subscribe(e => { if (_mapper.Handles(e.Document.Uri)) Post(() => Open(e.Document)); }));
        _subs.Add(documents.Changed.Subscribe(e => { if (_mapper.Handles(e.Document.Uri)) Post(() => Change(e)); }));
        _subs.Add(documents.Closed.Subscribe(e => { if (_mapper.Handles(e.Document.Uri)) Post(() => Close(e.Document.Uri)); }));
        _subs.Add(documents.Saved.Subscribe(e => { if (_mapper.Handles(e.Document.Uri)) Post(() => Send(RemoteMethods.DocSaved, new DocUriParams(e.Document.Uri), e.Document.Uri)); }));
        _subs.Add(documents.LanguageChanged.Subscribe(e => { if (_mapper.Handles(e.Document.Uri)) Post(() => Send(RemoteMethods.DocLanguage, new DocLanguageParams(e.Document.Uri, e.Document.LanguageId), e.Document.Uri)); }));
    }

    /// <summary>Documents the server currently has (sync-thread view; tests).</summary>
    public Task<int> SyncedCountAsync() => RunOnSync(() => _sent.Count);

    private void Post(Action work)
    {
        try { _queue.Add(work); } catch (InvalidOperationException) { } // disposed
    }

    private Task<T> RunOnSync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.TrySetResult(work()); } catch (Exception ex) { tcs.TrySetException(ex); } });
        if (_queue.IsAddingCompleted) tcs.TrySetCanceled();
        return tcs.Task;
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { _log.LogWarning(ex, "Document sync step failed"); }
        }
    }

    private void Open(ITextDocument doc)
    {
        if (_session is not { IsOpen: true } s) return;
        if (!ReferenceEquals(_documents.Get(doc.Uri), doc)) return; // closed again before we got here
        // Version and text must belong together: read until both reads see the same version.
        int version;
        string text;
        var tries = 0;
        do
        {
            version = doc.Version;
            text = doc.Length > MaxDocumentChars ? "" : doc.GetText();
        } while (doc.Version != version && ++tries < 5);
        if (text.Length > MaxDocumentChars || doc.Length > MaxDocumentChars) return;
        s.Peer.Notify(RemoteMethods.DocOpen, new DocOpenParams(doc.Uri, doc.LanguageId, version, text));
        _sent[doc.Uri] = version;
    }

    private void Change(TextDocumentChangedEventArgs e)
    {
        if (_session is not { IsOpen: true } s || !_sent.TryGetValue(e.Document.Uri, out var sent)) return;
        if (e.Version <= sent) return; // already part of the text sent at open
        s.Peer.Notify(RemoteMethods.DocChange, new DocChangeParams(e.Document.Uri, e.Version, e.Changes));
        _sent[e.Document.Uri] = e.Version;
    }

    private void Close(Uri uri)
    {
        if (!_sent.Remove(uri)) return;
        _session?.Peer.Notify(RemoteMethods.DocClose, new DocUriParams(uri));
    }

    private void Send<T>(string method, T args, Uri uri)
    {
        if (_session is { IsOpen: true } s && _sent.ContainsKey(uri)) s.Peer.Notify(method, args);
    }

    public void Attach(RpcPeer peer) { }

    /// <summary>Re-sends every matching open document in full on the new connection, and completes when they are queued on it.</summary>
    public Task OnConnectedAsync(RemoteSession session, CancellationToken ct) => RunOnSync(() =>
    {
        _session = session;
        _sent.Clear();
        foreach (var doc in _documents.Documents)
            if (_mapper.Handles(doc.Uri)) Open(doc);
        // A persistent server we reconnected to may still hold documents closed meanwhile: it closes every one not re-sent (ordered after the opens).
        session.Peer.Notify(RemoteMethods.DocKeep, new DocKeepParams(_sent.Keys.ToArray()));
        return true;
    });

    public void OnDisconnected() => Post(() =>
    {
        _session = null;
        _sent.Clear();
    });

    public void Dispose()
    {
        _subs.Dispose();
        _queue.CompleteAdding();
    }
}
