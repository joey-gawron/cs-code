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
    private readonly Emitter<CodeEditorControl> _viewOpened = new();
    private readonly Emitter<CodeEditorControl> _viewClosing = new();
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
        // multi-cursor's settings (editor.multiCursorLimit, editor.multiCursorPaste), so the Settings panel lists them
        _disposables.Add(services.Config.RegisterProperties(MultiCursorController.Properties));
        _disposables.Add(services.Config.RegisterProperties(Features.LinkedEditingFeature.Properties)); // editor.linkedEditing
        _disposables.Add(services.Config.RegisterProperties(LanguageRenderSettings.Properties)); // editor.codeLens*, editor.semanticHighlighting.enabled
        _disposables.Add(services.Config.RegisterProperties(Features.FormatterChoice.Properties)); // editor.defaultFormatter
        _disposables.Add(services.Config.RegisterProperties(Features.CodeActionsOnSaveParticipant.Properties)); // editor.codeActionsOnSave(Timeout)
        // the minimap's and the overview ruler's settings (editor.minimap.side, editor.overviewRulerLanes, ...)
        _disposables.Add(services.Config.RegisterProperties(View.Decorations.ScrollDecorations.Properties));
        _disposables.Add(services.Features.Register<ICompletionProvider>(DocumentSelector.All, new SnippetCompletionProvider(snippets), "cscode.snippets"));
        // inline merge conflicts: the per-version scan, the settings, the code lens, the commands and their right-click items (docs/SOURCE-CONTROL.md)
        MergeConflicts = new Features.MergeConflictTracker(worker);
        _disposables.Add(Features.MergeConflictCommands.Register(this));
        if (services.Commands is { } commands)
        {
            _disposables.Add(EditorCommands.Register(this, commands));
            _disposables.Add(commands.Register("editor.action.toggleMinimap", async _ =>
            {
                await services.Config.UpdateAsync(SettingKeys.EditorMinimapEnabled, !services.Config.GetOr(SettingKeys.EditorMinimapEnabled, true)).ConfigureAwait(false);
                return null;
            }, new CommandMetadata("editor.action.toggleMinimap", "View: Toggle Minimap", "View")));
            // The right-click menu's items, its submenus and the clipboard keys: the shell builds menus from these, it does not know them.
            _disposables.Add(EditorMenus.Contribute(this, services.Contributions));
            // bookmarks.* (docs/EDITOR.md §7g) and the right-click Bookmarks submenu
            if (services.Bookmarks is { } bookmarks)
            {
                _disposables.Add(Bookmarks.BookmarkCommands.Register(this, commands, bookmarks));
                _disposables.Add(Bookmarks.BookmarkCommands.Contribute(services.Contributions));
            }
        }
        // Capability context keys (editorHasRenameProvider, ...) follow servers that come and go.
        _disposables.Add(services.Features.ProvidersChanged.Subscribe(_ => services.Ui.Post(() =>
        {
            if (ActiveView is { IsDisposed: false } view) UpdateCapabilityKeys(view);
        })));
    }

    private Fallbacks.LocalClipboardService? _localClipboard;

    /// <summary>The shell's clipboard service, or (standalone) the clipboard of the window the active view is in.</summary>
    internal IClipboardService Clipboard => Services.Clipboard
        ?? (_localClipboard ??= new Fallbacks.LocalClipboardService(() => ActiveView is { } v ? Avalonia.Controls.TopLevel.GetTopLevel(v) : null));

    internal EditorServices Services { get; }
    internal LanguageService Languages { get; }
    internal ColorThemeService Themes { get; }
    internal TokenizationManager Tokens { get; }
    internal SnippetService Snippets { get; }
    internal EditorWorker Worker { get; }
    /// <summary>The conflict blocks of open documents, per version (decorations, code lens and commands share one scan).</summary>
    internal Features.MergeConflictTracker MergeConflicts { get; }
    internal bool IsDisposed => _disposed;

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

    /// <summary>A view was created (raised at the end of its construction, before it is shown). UI thread.</summary>
    public IEventSource<CodeEditorControl> ViewOpened => _viewOpened;

    /// <summary>A view is being destroyed (raised first thing in its dispose, while it is still intact). UI thread.</summary>
    public IEventSource<CodeEditorControl> ViewClosing => _viewClosing;

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

    /// <summary>The document open for <paramref name="uri"/> (a tab's, so a peek can edit it in place), or null.</summary>
    internal EditorDocument? TryGetOpenDocument(Uri uri)
    {
        lock (_gate) return _documents.TryGetValue(uri, out var doc) ? doc : null;
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

    /// <summary>A diff editor over the request's two sides (<see cref="Diff.DiffEditorControl"/>). UI thread.</summary>
    public Control? CreateDiffView(Core.Scm.DiffEditorRequest request) => new Diff.DiffEditorControl(this, request);

    /// <summary>A 3-way merge editor (<see cref="Diff.MergeEditorControl"/>) whose Result pane edits <paramref name="result"/>. UI thread.</summary>
    public Control? CreateMergeView(Core.Scm.MergeEditorRequest request, IEditorDocument result) =>
        result is EditorDocument doc ? new Diff.MergeEditorControl(this, request, doc) : null;

    /// <summary>The live merge editors by id (their input panes' lenses name them).</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<int, Diff.MergeEditorControl> MergeEditors { get; } = new();

    /// <summary>
    /// A read-only document over fixed text, NOT registered with the document manager (no language server sees it, no tab owns it): one side of
    /// a diff. Coloured like a file of <paramref name="languageId"/>. Release it with <see cref="ReleaseSnapshotDocument"/>. UI thread.
    /// </summary>
    internal EditorDocument CreateSnapshotDocument(Uri uri, string text, string languageId)
    {
        var doc = new EditorDocument(uri, languageId, EditorDocument.CreateBuffer(text, Services.Ui), Services.Ui, undoLimit: 0) { IsReadOnly = true };
        return doc;
    }

    internal void ReleaseSnapshotDocument(EditorDocument doc)
    {
        lock (_gate)
            if (_sessions.Remove(doc, out var session)) session.Dispose();
        doc.Dispose();
    }

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

    /// <summary>Called by the view once it is fully constructed.</summary>
    internal void NotifyOpened(CodeEditorControl view) => _viewOpened.Fire(view);

    internal void Unregister(CodeEditorControl view)
    {
        _viewClosing.Fire(view);
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
        UpdateCapabilityKeys(view);
        EditorMenus.PrepareContextKeys(view);
    }

    /// <summary>
    /// VS Code's provider context keys for the focused view's document (registry lookups only). The definition provider interface serves
    /// definition, declaration, type definition and implementation alike, so those four keys go together.
    /// </summary>
    internal void UpdateCapabilityKeys(CodeEditorControl view)
    {
        if (Services.ContextKeys is not { } keys) return;
        var doc = view.Document;
        var features = Services.Features;
        var large = doc.IsLargeFile;
        bool Has<T>() where T : class, ILanguageProvider => !large && features.GetProviders<T>(doc).Count > 0;
        var definition = Has<IDefinitionProvider>();
        var formatting = Has<IDocumentFormattingProvider>();
        keys.Set("editorHasCodeActionsProvider", Has<ICodeActionProvider>());
        keys.Set("editorHasRenameProvider", Has<IRenameProvider>());
        keys.Set("editorHasDefinitionProvider", definition);
        keys.Set("editorHasDeclarationProvider", definition);
        keys.Set("editorHasTypeDefinitionProvider", definition);
        keys.Set("editorHasImplementationProvider", definition);
        keys.Set("editorHasReferenceProvider", Has<IReferenceProvider>());
        keys.Set("editorHasDocumentFormattingProvider", formatting);
        keys.Set("editorHasDocumentSelectionFormattingProvider", formatting);
        keys.Set("editorHasCallHierarchyProvider", Has<ICallHierarchyProvider>()); // the Hierarchy window's items (docs/NAVIGATION.md)
        keys.Set("editorHasTypeHierarchyProvider", Has<ITypeHierarchyProvider>());
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
        _viewOpened.Clear();
        _viewClosing.Clear();
    }
}
