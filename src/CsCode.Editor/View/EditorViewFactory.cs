using Avalonia.Controls;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Documents;
using CsCode.Editor.Languages;
using CsCode.Editor.Snippets;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// <see cref="IEditorViewFactory"/>: the module's composition root at runtime. Owns the open documents (one <see cref="EditorDocument"/> per URI),
/// the per-document tokenisation sessions, the live views (for "which editor do commands act on"), and wires the editing commands.
/// </summary>
public sealed class EditorViewFactory : IEditorViewFactory, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Uri, Task<EditorDocument>> _opening = new();
    private readonly Dictionary<Uri, EditorDocument> _documents = new();
    private readonly Dictionary<EditorDocument, TokenizationSession> _sessions = new();
    private readonly List<CodeEditorControl> _views = new();
    private readonly DisposableStore _disposables = new();
    private readonly bool _ownsServices;
    private int _untitledCounter;
    private CodeEditorControl? _active;
    private bool _disposed;

    internal EditorViewFactory(
        EditorServices services,
        LanguageService languages,
        ColorThemeService themes,
        TokenizationManager tokens,
        SnippetService snippets,
        EditorWorker worker,
        bool ownsServices = false)
    {
        Services = services;
        Languages = languages;
        Themes = themes;
        Tokens = tokens;
        Snippets = snippets;
        Worker = worker;
        _ownsServices = ownsServices;

        // Snippets are offered through the same registry as language-server completions, so the popup merges them with the same code.
        _disposables.Add(services.Features.Register<ICompletionProvider>(DocumentSelector.All, new SnippetCompletionProvider(snippets), "cscode.snippets"));
        if (services.Commands is { } commands) _disposables.Add(EditorCommands.Register(this, commands));
        // keep context keys current for when-clauses (editorTextFocus, editorLangId, …)
    }

    internal EditorServices Services { get; }
    internal LanguageService Languages { get; }
    internal ColorThemeService Themes { get; }
    internal TokenizationManager Tokens { get; }
    internal SnippetService Snippets { get; }
    internal EditorWorker Worker { get; }

    /// <summary>The view that most recently had keyboard focus (what editor commands act on), or null.</summary>
    internal CodeEditorControl? ActiveView
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    public IReadOnlyList<CodeEditorControl> Views
    {
        get
        {
            lock (_gate) return _views.ToArray();
        }
    }

    // ---- IEditorViewFactory ----

    public async ValueTask<IEditorDocument> OpenDocumentAsync(Uri uri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<EditorDocument> task;
        lock (_gate)
        {
            if (_documents.TryGetValue(uri, out var existing)) return existing;
            if (!_opening.TryGetValue(uri, out task!))
            {
                task = OpenCoreAsync(uri, ct);
                _opening[uri] = task;
            }
        }
        return await task.ConfigureAwait(false);
    }

    private async Task<EditorDocument> OpenCoreAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var doc = await EditorDocument.OpenAsync(Services, uri, (u, first) => Languages.DetectLanguageId(u, first), ct).ConfigureAwait(false);
            lock (_gate) _documents[doc.Uri] = doc;
            doc.UriChanged.Subscribe(change =>
            {
                lock (_gate)
                {
                    _documents.Remove(change.Old);
                    _documents[change.New] = doc;
                }
            });
            return doc;
        }
        finally
        {
            lock (_gate) _opening.Remove(uri);
        }
    }

    public IEditorDocument CreateUntitled(string? languageId = null)
    {
        EditorDocument? doc = null;
        var number = Interlocked.Increment(ref _untitledCounter);
        Services.Ui.Invoke(() =>
        {
            doc = EditorDocument.CreateUntitled(Services, number, languageId ?? "plaintext", (u, first) => Languages.DetectLanguageId(u, first));
            lock (_gate) _documents[doc.Uri] = doc;
            doc.UriChanged.Subscribe(change =>
            {
                lock (_gate)
                {
                    _documents.Remove(change.Old);
                    _documents[change.New] = doc!;
                }
            });
        });
        return doc!;
    }

    public Control CreateView(IEditorDocument document)
    {
        if (document is not EditorDocument doc) throw new ArgumentException("The document was not created by this editor.", nameof(document));
        return new CodeEditorControl(this, doc);
    }

    public IEditorViewController? GetController(Control view) => (view as CodeEditorControl)?.Controller;

    public void DisposeView(Control view)
    {
        if (view is CodeEditorControl v) v.Dispose();
    }

    public async ValueTask CloseDocumentAsync(IEditorDocument document)
    {
        if (document is not EditorDocument doc) return;
        CodeEditorControl[] open;
        lock (_gate) open = _views.Where(v => ReferenceEquals(v.Document, doc)).ToArray();
        // Views that outlive their document would keep it alive: destroy them (the shell should already have).
        await Services.Ui.InvokeAsync(() =>
        {
            foreach (var v in open) v.Dispose();
            lock (_gate)
            {
                if (_sessions.Remove(doc, out var session)) session.Dispose();
                _documents.Remove(doc.Uri);
            }
            doc.Dispose();
        }).ConfigureAwait(false);
    }

    // ---- internals used by views ----

    internal TokenizationSession? GetTokenization(EditorDocument doc)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(doc, out var existing)) return existing;
            if (!Tokens.HasGrammar(doc.LanguageId)) return null;
            return _sessions[doc] = Tokens.CreateSession(doc);
        }
    }

    internal void Register(CodeEditorControl view)
    {
        lock (_gate) _views.Add(view);
    }

    internal void Unregister(CodeEditorControl view)
    {
        lock (_gate)
        {
            _views.Remove(view);
            if (ReferenceEquals(_active, view)) _active = null;
        }
    }

    internal void NotifyFocused(CodeEditorControl view)
    {
        lock (_gate) _active = view;
        UpdateContextKeys(view);
    }

    internal void UpdateContextKeys(CodeEditorControl view)
    {
        if (Services.ContextKeys is not { } keys) return;
        keys.Set("editorTextFocus", true);
        keys.Set("editorLangId", view.Document.LanguageId);
        keys.Set("editorReadonly", view.Document.IsReadOnly);
        keys.Set("resourceExtname", Path.GetExtension(view.Document.Uri.IsFile ? view.Document.Uri.LocalPath : view.Document.Uri.AbsolutePath));
        keys.Set("resourceScheme", view.Document.Uri.Scheme);
    }

    /// <summary>The per-view feature components (completion, hover, diagnostics, folding…), created by the control after its core is up.</summary>
    internal IEnumerable<IDisposable> CreateFeatures(CodeEditorControl view) => EditorFeatures.Create(view);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CodeEditorControl[] views;
        lock (_gate) views = _views.ToArray();
        foreach (var v in views) v.Dispose();
        lock (_gate)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
        _disposables.Dispose();
    }
}
