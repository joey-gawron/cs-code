using CsCode.Core.Lifecycle;

namespace CsCode.Core.Text;

/// <summary>Default <see cref="IDocumentManager"/>. Holds documents strongly only while registered.</summary>
public sealed class DocumentManager : IDocumentManager
{
    private readonly object _gate = new();
    private readonly Dictionary<Uri, Entry> _docs = new();
    private readonly Emitter<DocumentEvent> _opened = new(), _closed = new(), _saved = new(), _lang = new();
    private readonly Emitter<TextDocumentChangedEventArgs> _changed = new();

    private sealed class Entry(ITextDocument doc, IDisposable changeSub, IDisposable langSub)
    {
        public ITextDocument Doc { get; } = doc;
        public IDisposable ChangeSub { get; } = changeSub;
        public IDisposable LangSub { get; } = langSub;
    }

    public IReadOnlyCollection<ITextDocument> Documents
    {
        get { lock (_gate) return _docs.Values.Select(e => e.Doc).ToArray(); }
    }

    public IEventSource<DocumentEvent> Opened => _opened;
    public IEventSource<DocumentEvent> Closed => _closed;
    public IEventSource<DocumentEvent> Saved => _saved;
    public IEventSource<TextDocumentChangedEventArgs> Changed => _changed;
    public IEventSource<DocumentEvent> LanguageChanged => _lang;

    public ITextDocument? Get(Uri uri)
    {
        lock (_gate) return _docs.TryGetValue(uri, out var e) ? e.Doc : null;
    }

    public IDisposable Register(ITextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Entry entry;
        lock (_gate)
        {
            // Validate BEFORE subscribing: a rejected duplicate must not leave a dangling subscription on the document.
            if (_docs.ContainsKey(document.Uri))
                throw new InvalidOperationException($"Document already registered: {document.Uri}");
            entry = new Entry(
                document,
                document.Changed.Subscribe(_changed.Fire),
                document.LanguageIdChanged.Subscribe(d => _lang.Fire(new DocumentEvent(d))));
            _docs[document.Uri] = entry;
        }
        _opened.Fire(new DocumentEvent(document));
        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(document.Uri, out var current) || !ReferenceEquals(current, entry)) return;
                _docs.Remove(document.Uri);
            }
            entry.ChangeSub.Dispose();
            entry.LangSub.Dispose();
            _closed.Fire(new DocumentEvent(document));
        });
    }

    public void NotifySaved(ITextDocument document) => _saved.Fire(new DocumentEvent(document));
}
