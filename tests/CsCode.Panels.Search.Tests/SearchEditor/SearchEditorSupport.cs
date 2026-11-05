using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.SearchEditor;

namespace CsCode.Panels.Search.Tests.SearchEditor;

/// <summary>An editor document over <see cref="InMemoryTextDocument"/> in any language that counts the undo groups held on it.</summary>
public sealed class SearchEditorDocument : IEditorDocument
{
    private readonly InMemoryTextDocument _doc;
    private readonly Emitter<IEditorDocument> _saved = new(), _dirty = new();
    private readonly Emitter<ITextDocument> _language = new();

    public SearchEditorDocument(Uri uri, string languageId, string text)
    {
        _doc = new InMemoryTextDocument(uri, languageId, text);
        _doc.LanguageIdChanged.Subscribe(_ => _language.Fire(this)); // as this document, not the inner one
        _doc.Changed.Subscribe(_ => { if (!IsDirty) { IsDirty = true; _dirty.Fire(this); } });
    }

    public int UndoGroupsOpened { get; private set; }
    public int UndoGroupsOpen { get; private set; }
    public int Edits { get; private set; }

    public IDisposable OpenUndoGroup()
    {
        UndoGroupsOpened++;
        UndoGroupsOpen++;
        var closed = false;
        return Disposable.Create(() => { if (!closed) { closed = true; UndoGroupsOpen--; } });
    }

    public Uri Uri => _doc.Uri;
    public string LanguageId => _doc.LanguageId;
    public int Version => _doc.Version;
    public bool IsDirty { get; private set; }
    public int Length => _doc.Length;
    public int LineCount => _doc.LineCount;
    public string GetText() => _doc.GetText();
    public string GetText(TextRange range) => _doc.GetText(range);
    public string GetLineText(int line) => _doc.GetLineText(line);
    public int OffsetAt(TextPosition position) => _doc.OffsetAt(position);
    public TextPosition PositionAt(int offset) => _doc.PositionAt(offset);
    public TextPosition Validate(TextPosition position) => _doc.Validate(position);
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits) { Edits++; return _doc.ApplyEdits(edits); }
    public IEventSource<TextDocumentChangedEventArgs> Changed => _doc.Changed;
    public IEventSource<ITextDocument> LanguageIdChanged => _language;
    public bool IsReadOnly => false;
    public bool IsUntitled => Uri.Scheme == "untitled";
    public string EncodingName => "utf-8";
    public string EndOfLine => "\n";
    public void SetLanguageId(string languageId) => _doc.SetLanguageId(languageId);
    public Task SaveAsync(CancellationToken ct = default) { IsDirty = false; _saved.Fire(this); return Task.CompletedTask; }
    public Task SaveAsAsync(Uri target, CancellationToken ct = default) => SaveAsync(ct);
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IEventSource<IEditorDocument> Saved => _saved;
    public IEventSource<IEditorDocument> DirtyChanged => _dirty;

    /// <summary>Replaces line <paramref name="line"/> (what typing over a header line does).</summary>
    public void SetLine(int line, string text) =>
        ApplyEdits([new TextEdit(new TextRange(new TextPosition(line, 0), new TextPosition(line, GetLineText(line).Length)), text)]);
}

/// <summary>A shell stand-in: opens untitled documents (registered with the document manager), records navigation, owns the active editor.</summary>
public sealed class SearchEditorNavigation(IDocumentManager documents) : INavigationService
{
    private readonly Emitter<IEditorDocument?> _active = new();
    private readonly Emitter<TextPosition?> _caret = new();
    private readonly Dictionary<IEditorDocument, IDisposable> _registrations = new(ReferenceEqualityComparer.Instance);
    private int _untitled;
    private IEditorDocument? _activeDoc;
    private TextPosition? _activeCaret;

    public List<(Uri Uri, TextRange? Selection)> Opened { get; } = new();
    public List<(Uri Uri, TextRange? Selection)> OpenedToSide { get; } = new();
    public List<UntitledEditorOptions> Untitled { get; } = new();
    public Dictionary<Uri, (string? Title, Uri? SaveUri)> Titles { get; } = new();

    public IEditorDocument? ActiveDocument => _activeDoc;
    public TextPosition? ActiveCaret => _activeCaret;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _active;
    public IEventSource<TextPosition?> ActiveCaretChanged => _caret;
    public IReadOnlyList<IEditorDocument> OpenDocuments => documents.Documents.OfType<IEditorDocument>().ToArray();

    public void Activate(IEditorDocument? doc, TextPosition? caret = null)
    {
        _activeDoc = doc;
        _activeCaret = caret;
        _active.Fire(doc);
        _caret.Fire(caret);
    }

    public void MoveCaret(TextPosition caret)
    {
        _activeCaret = caret;
        _caret.Fire(caret);
    }

    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        lock (Opened) Opened.Add((uri, selection));
        return ValueTask.FromResult(documents.Get(uri) as IEditorDocument);
    }

    public ValueTask<IEditorDocument?> OpenToSideAsync(Uri uri, TextRange? selection = null, CancellationToken ct = default)
    {
        lock (OpenedToSide) OpenedToSide.Add((uri, selection));
        return ValueTask.FromResult(documents.Get(uri) as IEditorDocument);
    }

    public ValueTask<IEditorDocument?> OpenUntitledAsync(UntitledEditorOptions options, CancellationToken ct = default)
    {
        Untitled.Add(options);
        var doc = new SearchEditorDocument(new Uri($"untitled:Untitled-{++_untitled}"), options.LanguageId, options.Text);
        _registrations[doc] = documents.Register(doc);
        Titles[doc.Uri] = (options.Title, options.SuggestedSaveUri);
        Activate(doc, options.Selection?.Start);
        return ValueTask.FromResult<IEditorDocument?>(doc);
    }

    public void SetEditorTitle(Uri document, string? title, Uri? suggestedSaveUri = null) => Titles[document] = (title, suggestedSaveUri);

    public ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default) => ValueTask.FromResult(false);

    /// <summary>Closes an untitled document opened here (its registration goes, as when its tab closes).</summary>
    public void Close(IEditorDocument doc)
    {
        if (_registrations.Remove(doc, out var registration)) registration.Dispose();
        if (ReferenceEquals(_activeDoc, doc)) Activate(null);
    }
}

/// <summary>A search editor service over a temp workspace, the real engine, a manual UI thread and the stand-in shell.</summary>
public sealed class SearchEditorFixture : IDisposable
{
    public VmFixture F { get; }
    public LanguageFeatureRegistry Features { get; } = new();
    public SearchEditorNavigation Nav { get; }
    public SearchEditorService Service { get; }
    public ManualUiDispatcher Ui => F.Ui;

    public SearchEditorFixture(SearchLimits? limits = null, Func<string?>? seed = null)
    {
        F = new VmFixture(limits);
        Nav = new SearchEditorNavigation(F.Harness.Documents);
        var services = F.With(navigation: Nav);
        Service = new SearchEditorService(() => services, new SearchEditorWiring(F.Ui, F.Harness.Workspace, F.Harness.Documents, Features, Nav, F.ContextKeys,
            F.Configuration, F.Commands, F.Windows), seed);
    }

    public string Write(string relative, string content) => F.Harness.Write(relative, content);

    /// <summary>Opens a new search editor for <paramref name="config"/> and waits until its search is in the text.</summary>
    public async Task<SearchEditorDocument> OpenAsync(SearchEditorConfig config, bool run = true)
    {
        var open = Service.OpenNewAsync(config, run: run);
        await Ui.PumpAsync(open);
        var doc = (SearchEditorDocument)(await open)!;
        await WaitAsync(doc);
        return doc;
    }

    /// <summary>Pumps the UI queue until the document's current run is done.</summary>
    public async Task WaitAsync(ITextDocument doc)
    {
        if (Service.SessionOf(doc)?.Run is { } run) await Ui.PumpAsync(run.Completion);
        Ui.RunPending();
    }

    public void Dispose()
    {
        Service.Dispose();
        F.Dispose();
    }
}
