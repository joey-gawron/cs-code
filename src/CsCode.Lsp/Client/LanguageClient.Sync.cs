using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

// Document synchronisation: didOpen / didChange / didSave / willSave / didClose.
//
// Threading: Attach/Detach/OnChanged/OnSaved are called from whatever thread edits (usually the UI thread) and only enqueue
// or append to a tiny pending list under SyncedDocument.PendingGate. The Language lane does the real work: reading the text for
// didOpen, coalescing bursts into ONE didChange, converting positions and serialising. A request for a document always flushes that
// document's pending changes first, in the same critical section that initiates the send, so the server never sees a request before
// the text it refers to.
internal sealed partial class LanguageClient
{
    private const int MaxPendingChanges = 20_000;

    // Effective synchronisation options (static capabilities; dynamic registrations may override per selector).
    private bool _openClose = true;
    private P.TextDocumentSyncKind _syncKind = P.TextDocumentSyncKind.None;
    private bool _willSave, _willSaveWaitUntil, _saveNotifications, _saveIncludeText;
    private readonly List<(string Id, DocumentSelector? Selector, P.TextDocumentSyncKind Kind)> _dynamicChange = [];
    private readonly List<(string Id, DocumentSelector? Selector, bool IncludeText)> _dynamicSave = [];

    private void ParseSyncOptions(P.ServerCapabilities caps)
    {
        var sync = caps.TextDocumentSync;
        if (sync is null) { _openClose = true; _syncKind = P.TextDocumentSyncKind.None; return; }
        if (sync.Value.IsT1)
        {
            _openClose = true;
            _syncKind = sync.Value.AsT1;
            return;
        }
        var o = sync.Value.AsT0;
        _openClose = o.OpenClose == true;
        _syncKind = o.Change ?? P.TextDocumentSyncKind.None;
        _willSave = o.WillSave == true;
        _willSaveWaitUntil = o.WillSaveWaitUntil == true;
        if (o.Save is { } save)
        {
            _saveNotifications = save.IsT0 ? save.AsT0 : true;
            _saveIncludeText = save.IsT1 && save.AsT1.IncludeText == true;
        }
    }

    private P.TextDocumentSyncKind SyncKindFor(SyncedDocument sd)
    {
        lock (_dynamicChange)
        {
            for (var i = _dynamicChange.Count - 1; i >= 0; i--)
            {
                var (_, sel, kind) = _dynamicChange[i];
                if (sel is null || sel.Score(sd.Document) > 0) return kind;
            }
        }
        return _syncKind;
    }

    // ---- attach / detach -----------------------------------------------------------------------------------------

    public bool IsAttached(Uri uri) => _docs.ContainsKey(uri);

    public bool TryGetSynced(Uri uri, out SyncedDocument sd) => _docs.TryGetValue(uri, out sd!);

    public void AttachDocument(ITextDocument doc)
    {
        if (Volatile.Read(ref _tornDown) != 0) return;
        // a library or generated document goes to its server under the server's own name for it (csharp:/metadata/…, a decompiled file)
        var uriString = (VirtualDocumentUri.IsVirtual(doc.Uri) ? _ctx.External?.ServerUriOf(doc.Uri) : null) ?? LspMapper.ToUriString(doc.Uri);
        var sd = new SyncedDocument(doc, uriString) { LastSeenVersion = doc.Version, ServerLanguageId = doc.LanguageId };
        if (!_docs.TryAdd(doc.Uri, sd)) return;
        if (_docsMayOpen) PostOpen(sd);
    }

    public void DetachDocument(Uri uri)
    {
        if (!_docs.TryRemove(uri, out var sd)) return;
        TokenCache.Remove(uri);
        _ctx.Queue.Post(LanePriority.Interactive, () => CloseOnLane(sd), exempt: true);
    }

    private void PostOpen(SyncedDocument sd) =>
        _ctx.Queue.Post(LanePriority.Interactive, () => { lock (sd.Gate) EnsureOpenLocked(sd); }, exempt: true);

    /// <summary>Editing-thread entry point: O(changes) appends under a tiny lock, one enqueue per burst. Never blocks, never serialises.</summary>
    public void OnChanged(TextDocumentChangedEventArgs args)
    {
        if (!_docs.TryGetValue(args.Document.Uri, out var sd)) return;
        var post = false;
        CancellationTokenSource? stale;
        lock (sd.PendingGate)
        {
            if (args.Version <= sd.LastSeenVersion) return;
            sd.LastSeenVersion = args.Version;
            stale = sd.VersionCts;
            sd.VersionCts = null;
            if (sd.AcceptingChanges)
            {
                var pending = sd.Pending ??= new List<TextContentChange>(Math.Max(4, args.Changes.Count));
                if (pending.Count + args.Changes.Count > MaxPendingChanges)
                {
                    // The lane is far behind (or stuck): drop the incremental history, resync with the full text later.
                    pending.Clear();
                    sd.ForceFull = true;
                }
                else
                    for (var i = 0; i < args.Changes.Count; i++) pending.Add(args.Changes[i]);
                sd.PendingVersion = args.Version;
                if (!sd.FlushPosted) { sd.FlushPosted = true; post = true; }
            }
        }
        // In-flight requests for the previous text are abandoned — cancelled on the lane, never inline on this (UI) thread.
        if (stale is not null) _ctx.Queue.CancelOnLane(stale);
        if (post) _ctx.Queue.Post(LanePriority.Interactive, () => FlushOnLane(sd), exempt: true);
        ScheduleDiagnosticsPull(sd);
    }

    public void OnSaved(ITextDocument doc)
    {
        if (!_docs.TryGetValue(doc.Uri, out var sd) || !SaveNotificationsFor(sd)) return;
        _ctx.Queue.Post(LanePriority.Interactive, () => SendDidSave(sd), exempt: true);
    }

    private bool SaveNotificationsFor(SyncedDocument sd)
    {
        lock (_dynamicSave)
            foreach (var (_, sel, _) in _dynamicSave)
                if (sel is null || sel.Score(sd.Document) > 0) return true;
        return _saveNotifications;
    }

    private bool SaveIncludesText(SyncedDocument sd)
    {
        lock (_dynamicSave)
            foreach (var (_, sel, inc) in _dynamicSave)
                if (sel is null || sel.Score(sd.Document) > 0) return inc;
        return _saveIncludeText;
    }

    private void SendDidSave(SyncedDocument sd)
    {
        lock (sd.Gate)
        {
            if (sd.Detached || !sd.OpenOnServer) return;
            FlushLocked(sd);
            var p = new P.DidSaveTextDocumentParams { TextDocument = new P.TextDocumentIdentifier { Uri = sd.UriString } };
            if (SaveIncludesText(sd)) p = p with { Text = sd.Document.GetText() };
            NotifyRaw(P.LspMethods.TextDocumentDidSave, p);
        }
        ScheduleDiagnosticsPull(sd);
    }

    // ---- lane-side operations (callers hold sd.Gate unless noted) ------------------------------------------------

    private void FlushOnLane(SyncedDocument sd)
    {
        lock (sd.PendingGate) sd.FlushPosted = false;
        lock (sd.Gate) FlushLocked(sd);
    }

    private void CloseOnLane(SyncedDocument sd)
    {
        lock (sd.Gate)
        {
            sd.Detached = true;
            lock (sd.PendingGate) { sd.AcceptingChanges = false; sd.Pending = null; sd.VersionCts?.Cancel(); sd.VersionCts?.Dispose(); sd.VersionCts = null; }
            if (sd.OpenOnServer && _openClose && IsOperational)
                NotifyRaw(P.LspMethods.TextDocumentDidClose, new P.DidCloseTextDocumentParams { TextDocument = new P.TextDocumentIdentifier { Uri = sd.UriString } });
            sd.OpenOnServer = false;
        }
        // Push-only servers never clear a closed file's diagnostics themselves; pull servers report per open document.
        _publishedUris.TryRemove(sd.Uri, out _);
        _ctx.Services.Diagnostics.Clear(sd.Uri, _owner);
    }

    /// <summary>Sends didOpen (full text) if not yet sent. Caller holds sd.Gate.</summary>
    internal void EnsureOpenLocked(SyncedDocument sd)
    {
        if (sd.OpenOnServer || sd.Detached || sd.Ignored || !_docsMayOpen) return;
        var doc = sd.Document;
        if (doc.Length > _ctx.Settings().MaxDocumentBytes)
        {
            sd.Ignored = true;
            _log.Add($"[client] {sd.Uri} is larger than cscode.lsp.maxDocumentBytes; not sent to {Definition.DisplayName}");
            return;
        }
        if (!_openClose) { sd.Ignored = true; return; }

        var (text, version, stable) = ReadConsistent(doc);
        lock (sd.PendingGate)
        {
            sd.ServerVersion = version;
            sd.AcceptingChanges = true;
            // Events at or below `version` are already contained in the text we are about to send.
            if (sd.LastSeenVersion > version || !stable) sd.ForceFull = true;
            else sd.LastSeenVersion = Math.Max(sd.LastSeenVersion, version);
            sd.Pending = null;
        }
        sd.ServerLanguageId = doc.LanguageId;
        sd.OpenOnServer = true;
        NotifyRaw(P.LspMethods.TextDocumentDidOpen, new P.DidOpenTextDocumentParams
        {
            TextDocument = new P.TextDocumentItem { Uri = sd.UriString, LanguageId = doc.LanguageId, Version = version, Text = text },
        });
        FlushLocked(sd); // picks up ForceFull / anything that raced with the read
    }

    private static (string Text, int Version, bool Stable) ReadConsistent(ITextDocument doc)
    {
        for (var i = 0; i < 4; i++)
        {
            var v = doc.Version;
            var text = doc.GetText();
            if (doc.Version == v) return (text, v, true);
        }
        var last = doc.Version;
        return (doc.GetText(), last, false);
    }

    /// <summary>Sends everything that has accumulated for the document as one didChange. Caller holds sd.Gate.</summary>
    internal void FlushLocked(SyncedDocument sd)
    {
        if (!sd.OpenOnServer || sd.Detached) return;
        List<TextContentChange>? changes;
        int version;
        bool forceFull;
        lock (sd.PendingGate)
        {
            changes = sd.Pending;
            sd.Pending = null;
            version = sd.PendingVersion;
            forceFull = sd.ForceFull;
            sd.ForceFull = false;
        }
        if ((changes is null || changes.Count == 0) && !forceFull) return;

        var kind = SyncKindFor(sd);
        if (kind == P.TextDocumentSyncKind.None)
        {
            // The server does not want changes (now). Remember that its copy went stale so that enabling sync later
            // (dynamic registration) starts with the full text instead of ranges against text the server never saw.
            lock (sd.PendingGate) sd.ForceFull = true;
            return;
        }

        P.DidChangeTextDocumentParams p;
        if (kind == P.TextDocumentSyncKind.Full || forceFull || !Mapper.Encoder.IsIdentity)
        {
            // Full sync: also the safe path for non-UTF-16 servers (incremental ranges would need the pre-change line text).
            var (text, v, stable) = ReadConsistent(sd.Document);
            lock (sd.PendingGate)
            {
                // The text we are about to send already contains every edit up to version v, including edits whose change events have
                // not reached OnChanged yet (the document mutates before it notifies). Anything at or below v must NOT be sent again.
                if (v > sd.LastSeenVersion) sd.LastSeenVersion = v;
                if (sd.PendingVersion <= v) sd.Pending = null;      // everything accumulated is inside the text
                else sd.ForceFull = true;                           // mixed old/new: resend the text rather than risk a duplicate
                if (!stable) sd.ForceFull = true;
            }
            if (v <= sd.ServerVersion && !forceFull && kind != P.TextDocumentSyncKind.Full) return;
            sd.ServerVersion = v;
            p = new P.DidChangeTextDocumentParams
            {
                TextDocument = new P.VersionedTextDocumentIdentifier { Uri = sd.UriString, Version = v },
                ContentChanges = [new P.TextDocumentContentChangeEvent { Text = text }],
            };
        }
        else
        {
            var events = BuildIncrementalChanges(changes!, sd.Uri);
            sd.ServerVersion = version;
            p = new P.DidChangeTextDocumentParams
            {
                TextDocument = new P.VersionedTextDocumentIdentifier { Uri = sd.UriString, Version = version },
                ContentChanges = events,
            };
        }
        NotifyRaw(P.LspMethods.TextDocumentDidChange, p);
    }

    /// <summary>
    /// Converts the ordered change list into LSP content changes, merging runs of typing (an insertion exactly where the previous
    /// replacement's new text ends) into one change. Each range is relative to the text after the preceding change — LSP semantics.
    /// </summary>
    private P.TextDocumentContentChangeEvent[] BuildIncrementalChanges(List<TextContentChange> changes, Uri uri)
    {
        var result = new List<P.TextDocumentContentChangeEvent>(changes.Count);
        TextRange curRange = default;
        string? curText = null;
        void Emit()
        {
            if (curText is null) return;
            result.Add(new P.TextDocumentContentChangeEvent { Range = Mapper.ToLsp(curRange, uri), Text = curText });
            curText = null;
        }
        foreach (var c in changes)
        {
            if (curText is not null && c.Range.IsEmpty && c.Range.Start == EndOfInserted(curRange.Start, curText))
            {
                curText += c.Text;
                continue;
            }
            Emit();
            curRange = c.Range;
            curText = c.Text;
        }
        Emit();
        return result.ToArray();
    }

    private static TextPosition EndOfInserted(TextPosition start, string text)
    {
        var lines = 0;
        var lastLineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            else if (text[i] != '\n' && text[i] != '\r') continue;
            lines++;
            lastLineStart = i + 1;
        }
        return lines == 0 ? new TextPosition(start.Line, start.Character + text.Length) : new TextPosition(start.Line + lines, text.Length - lastLineStart);
    }

    // ---- willSave / willSaveWaitUntil (driven by IWillSaveParticipant) ----------------------------------------------

    /// <summary>Sends willSave and, if the server wants it, waits (bounded) for willSaveWaitUntil edits.</summary>
    public async ValueTask<IReadOnlyList<TextEdit>?> WillSaveAsync(ITextDocument doc, DocumentSaveReason reason, CancellationToken ct)
    {
        if (!_docs.TryGetValue(doc.Uri, out var sd) || !(_willSave || _willSaveWaitUntil)) return null;
        var r = (P.TextDocumentSaveReason)(int)reason;
        return await _ctx.Queue.RunAsync<IReadOnlyList<TextEdit>>(LanePriority.Interactive, default, async wc =>
        {
            var p = new P.WillSaveTextDocumentParams { TextDocument = new P.TextDocumentIdentifier { Uri = sd.UriString }, Reason = r };
            if (_willSave)
                lock (sd.Gate)
                {
                    EnsureOpenLocked(sd);
                    FlushLocked(sd);
                    NotifyRaw(P.LspMethods.TextDocumentWillSave, p);
                }
            if (!_willSaveWaitUntil) return null;
            var edits = await RequestAsync(wc, P.LspRequests.TextDocumentWillSaveWaitUntil, p, wc.Token, sd, 0, TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            return edits is null ? null : Mapper.ToCore(edits, sd.Uri);
        }, ct).ConfigureAwait(false);
    }
}
