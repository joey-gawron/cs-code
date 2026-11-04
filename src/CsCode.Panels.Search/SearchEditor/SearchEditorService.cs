using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.ViewModels;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.SearchEditor;

/// <summary>The services the search editor wires itself to at startup (the engine and the rest come later, from the lazy search services).</summary>
public sealed record SearchEditorWiring(
    IUiDispatcher Ui,
    IWorkspaceService? Workspace = null,
    IDocumentManager? Documents = null,
    ILanguageFeatureRegistry? Features = null,
    INavigationService? Navigation = null,
    IContextKeyService? ContextKeys = null,
    IConfigurationService? Configuration = null,
    ICommandService? Commands = null,
    IWindowService? Windows = null);

/// <summary>
/// VS Code's Search Editor: search results as an editable text document in the <c>search-result</c> language (VS Code's <c>.code-search</c>
/// format, <see cref="SearchEditorFormat"/>). Every open <c>search-result</c> document (an untitled "Search: query" tab or a saved
/// <c>.code-search</c> file) gets a <see cref="SearchEditorSession"/>; the header is the query, Enter on it or
/// <c>search.action.rerunEditorSearch</c> searches again in place with the Find in Files engine, results stream in, the matches are highlighted and
/// a result line opens its file (F12, Ctrl/⌘+click, double-click). Commands: <c>search.action.openNewEditor</c>, <c>openNewEditorToSide</c>,
/// <c>openInEditor</c> (the Find in Files window's results), <c>rerunEditorSearch</c>. Owned by <see cref="SearchPanelsHost"/>.
/// </summary>
public sealed class SearchEditorService : IDisposable
{
    public const string LanguageId = SearchEditorFormat.LanguageId;

    public const string OpenNewEditor = "search.action.openNewEditor";
    public const string OpenNewEditorToSide = "search.action.openNewEditorToSide";
    public const string OpenInEditor = "search.action.openInEditor";
    public const string RerunEditorSearch = "search.action.rerunEditorSearch";
    /// <summary>VS Code's own id of Search Again (registered too, hidden from the palette, so keybindings.json files written for VS Code work).</summary>
    public const string RerunEditorSearchVsCode = "rerunSearchEditorSearch";

    /// <summary>Context key: the active editor is a search editor (VS Code's).</summary>
    public const string InSearchEditorKey = "inSearchEditor";
    /// <summary>Context key: the caret of the active search editor is on a header line (Enter searches again there).</summary>
    public const string HeaderFocusKey = "searchEditorHeaderFocus";

    public const string ContextLinesKey = "search.searchEditor.defaultNumberOfContextLines";
    public const string DoubleClickKey = "search.searchEditor.doubleClickBehaviour";
    public const string ReuseConfigurationKey = "search.searchEditor.reusePriorSearchConfiguration";
    public const string SearchOnTypeKey = "search.searchOnType";
    public const string SearchOnTypeDebounceKey = "search.searchOnTypeDebouncePeriod";

    public const string DoubleClickGoTo = "goToLocation";
    public const string DoubleClickOpenToSide = "openLocationToSide";
    public const string DoubleClickSelectWord = "selectWord";

    private readonly Func<SearchServices> _services;
    private readonly SearchEditorWiring _w;
    private readonly DisposableStore _store = new();
    private readonly object _gate = new();
    private readonly Dictionary<ITextDocument, SearchEditorSession> _sessions = new(ReferenceEqualityComparer.Instance);
    private readonly SearchResultFeatures _features;
    private DedicatedThreadScheduler? _lane;
    private SearchEditorConfig? _lastConfig;
    private bool _disposed;

    /// <param name="services">The search services (engine, file system...), created on first use.</param>
    /// <param name="seedQuery">The text a new search editor starts with (the selection or the word at the caret), or null.</param>
    public SearchEditorService(Func<SearchServices> services, SearchEditorWiring wiring, Func<string?>? seedQuery = null, Func<FindInFilesViewModel?>? liveFind = null)
    {
        _services = services;
        _w = wiring;
        SeedQuery = seedQuery ?? (() => null);
        LiveFind = liveFind ?? (() => null);
        _features = new SearchResultFeatures(this);

        if (wiring.Features is { } registry)
        {
            var selector = DocumentSelector.ForLanguage(LanguageId);
            _store.Add(registry.Register<IDefinitionProvider>(selector, _features));
            _store.Add(registry.Register<IDocumentLinkProvider>(selector, _features));
            _store.Add(registry.Register<IMatchHighlightProvider>(selector, _features));
            _store.Add(registry.Register<IDoubleClickHandler>(selector, _features));
        }
        if (wiring.Documents is { } documents)
        {
            _store.Add(documents.Opened.Subscribe(e => OnUi(() => Track(e.Document))));
            _store.Add(documents.LanguageChanged.Subscribe(e => OnUi(() => Track(e.Document))));
            // A Save As closes the old name and opens the new one: keep the session (and its run) if the document is still open afterwards.
            _store.Add(documents.Closed.Subscribe(e => wiring.Ui.Post(() => Untrack(e.Document))));
            foreach (var doc in documents.Documents) OnUi(() => Track(doc));
        }
        if (wiring.Navigation is { } nav)
        {
            _store.Add(nav.ActiveDocumentChanged.Subscribe(_ => OnUi(UpdateContextKeys)));
            _store.Add(nav.ActiveCaretChanged.Subscribe(_ => OnUi(UpdateContextKeys)));
        }
        if (wiring.Commands is { } commands) RegisterCommands(commands);
    }

    private Func<string?> SeedQuery { get; }
    private Func<FindInFilesViewModel?> LiveFind { get; }

    internal IUiDispatcher Ui => _w.Ui;
    internal IReadOnlyList<Uri> Folders => _w.Workspace?.Folders ?? [];

    /// <summary>The language features of <c>search-result</c> documents (tests).</summary>
    internal SearchResultFeatures Features => _features;

    // ----------------------------------------------------------------------------------------- settings

    internal bool SearchOnType => SearchLimits.ReadBool(_w.Configuration, SearchOnTypeKey, true);
    internal TimeSpan SearchOnTypeDelay => TimeSpan.FromMilliseconds(_w.Configuration is { } c ? Math.Clamp(SearchLimits.ReadNumber(c, SearchOnTypeDebounceKey, 300), 0, 10_000) : 300);
    internal int DefaultContextLines => _w.Configuration is { } c ? (int)Math.Clamp(SearchLimits.ReadNumber(c, ContextLinesKey, 1), 0, SearchEditorFormat.MaxContextLines) : 1;
    internal bool ReusePriorConfiguration => SearchLimits.ReadBool(_w.Configuration, ReuseConfigurationKey, false);

    internal string DoubleClickBehaviour =>
        _w.Configuration?.GetRaw(DoubleClickKey) is { ValueKind: JsonValueKind.String } e && e.GetString() is DoubleClickOpenToSide or DoubleClickSelectWord ? e.GetString()! : DoubleClickGoTo;

    // ----------------------------------------------------------------------------------------- sessions

    /// <summary>The session of <paramref name="document"/>, or null when it is not a search editor.</summary>
    internal SearchEditorSession? SessionOf(ITextDocument document)
    {
        lock (_gate) return _sessions.GetValueOrDefault(document);
    }

    internal int SessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    private void OnUi(Action action)
    {
        if (_w.Ui.CheckAccess()) action();
        else _w.Ui.Post(action);
    }

    /// <summary>A document that is (now) in the <c>search-result</c> language gets a session; one that left it loses it.</summary>
    private void Track(ITextDocument document)
    {
        if (_disposed) return;
        if (document.LanguageId == LanguageId) EnsureSession(document);
        else DropSession(document);
        if (ReferenceEquals(_w.Navigation?.ActiveDocument, document)) UpdateContextKeys();
    }

    private void Untrack(ITextDocument document)
    {
        if (SessionOf(document) is null) return;
        if (_w.Documents is { } documents && documents.Documents.Contains(document)) return; // renamed (Save As), still open
        DropSession(document);
    }

    internal SearchEditorSession EnsureSession(ITextDocument document)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(document, out var existing)) return existing;
            var session = new SearchEditorSession(this, document);
            _sessions[document] = session;
            return session;
        }
    }

    private void DropSession(ITextDocument document)
    {
        SearchEditorSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(document, out session)) return;
        }
        session.Dispose();
    }

    internal SearchEditorRunContext RunContext()
    {
        var s = _services();
        lock (_gate) _lane ??= new DedicatedThreadScheduler("cscode-search-editor");
        return new SearchEditorRunContext(s.Engine, s.Ui, s.Workspace.Folders, s.Documents, s.FileSystem ?? s.Workspace.FileSystem, _lane);
    }

    /// <summary>A session is about to search with <paramref name="config"/>: an untitled search editor's tab follows its query.</summary>
    internal void OnRunStarting(SearchEditorSession session, SearchEditorConfig config)
    {
        _lastConfig = config;
        if (session.Document is IEditorDocument { IsUntitled: true } doc) _w.Navigation?.SetEditorTitle(doc.Uri, TitleFor(config), SaveUriFor(config));
    }

    internal void HighlightsChanged(Uri uri) => _features.FireChanged(uri);

    /// <summary>The query a search editor's matches are highlighted with: its last run's, else its header's (a saved file just opened).</summary>
    internal SearchEditorConfig? HighlightConfigFor(ITextDocument document)
    {
        if (SessionOf(document)?.LastConfig is { } last) return last;
        var header = SearchEditorFormat.CountHeaderLines(document.GetLineText, document.LineCount);
        return header == 0 ? null : SearchEditorFormat.ParseHeader(Enumerable.Range(0, header).Select(document.GetLineText).ToArray());
    }

    // ----------------------------------------------------------------------------------------- context keys

    private static readonly object BoxedTrue = true, BoxedFalse = false;
    private bool? _keyInEditor, _keyOnHeader;

    /// <summary>
    /// Runs on every caret move of the active editor (typing included): outside a search editor it is two comparisons, and a key is written only
    /// when its value changes.
    /// </summary>
    private void UpdateContextKeys()
    {
        if (_w.ContextKeys is not { } keys) return;
        var doc = _w.Navigation?.ActiveDocument;
        var inEditor = doc is not null && doc.LanguageId == LanguageId;
        var onHeader = false;
        if (inEditor && doc is not null && _w.Navigation?.ActiveCaret is { } caret)
            onHeader = caret.Line < SearchEditorFormat.CountHeaderLines(doc.GetLineText, doc.LineCount, max: caret.Line + 1);
        if (_keyInEditor != inEditor) keys.Set(InSearchEditorKey, inEditor ? BoxedTrue : BoxedFalse);
        if (_keyOnHeader != onHeader) keys.Set(HeaderFocusKey, onHeader ? BoxedTrue : BoxedFalse);
        _keyInEditor = inEditor;
        _keyOnHeader = onHeader;
    }

    // ----------------------------------------------------------------------------------------- opening

    /// <summary>The header of a new search editor: the previous one's flags and globs (<c>search.searchEditor.reusePriorSearchConfiguration</c>) or the defaults, and the seed query.</summary>
    internal SearchEditorConfig NewConfig(string? query)
    {
        var baseConfig = ReusePriorConfiguration && _lastConfig is { } last ? last : new SearchEditorConfig { ContextLines = DefaultContextLines };
        return baseConfig with { Query = query ?? "" };
    }

    /// <summary>
    /// Opens a new untitled search editor with <paramref name="config"/>'s header and, when <paramref name="run"/> and the query is not empty,
    /// searches (or shows <paramref name="preset"/> results). Returns the document, or null when the host cannot open untitled editors.
    /// </summary>
    public async Task<IEditorDocument?> OpenNewAsync(SearchEditorConfig config, bool toSide = false, bool run = true, IReadOnlyList<FileMatches>? preset = null)
    {
        var nav = _w.Navigation;
        if (nav is null || _disposed) return null;
        var header = SearchEditorFormat.SerializeHeader(config);
        var caret = new TextPosition(0, "# Query: ".Length + SearchEditorFormat.EscapeQuery(config.Query).Length);
        var doc = await nav.OpenUntitledAsync(new UntitledEditorOptions(LanguageId, header + "\n")
        {
            Title = TitleFor(config),
            SuggestedSaveUri = SaveUriFor(config),
            ToSide = toSide,
            Selection = new TextRange(caret, caret),
        }).ConfigureAwait(true);
        if (doc is null)
        {
            if (_w.Windows is { } windows) _ = windows.ShowMessageAsync(MessageSeverity.Warning, "A search editor cannot be opened here: the window has no untitled editors.");
            return null;
        }
        _lastConfig = config;
        await _w.Ui.InvokeAsync(() =>
        {
            if (_disposed) return;
            var session = EnsureSession(doc);
            if (run && config.Query.Length > 0) session.Rerun(preset);
        }).ConfigureAwait(true);
        return doc;
    }

    /// <summary>
    /// Open Results in Editor: the Find in Files window's query and results (what it shows now, dismissed rows left out, with context lines read
    /// from the files) in a new search editor; a window that has not searched opens one with its query, searching it.
    /// </summary>
    public Task<IEditorDocument?> OpenInEditorAsync(FindInFilesViewModel? find)
    {
        if (find is null) return OpenNewAsync(NewConfig(SeedQuery()));
        var query = find.LastQuery ?? find.BuildQuery();
        var config = SearchEditorConfig.From(query, DefaultContextLines);
        var preset = find.LastQuery is not null && find.HasResults && !find.IsSearching ? find.ResultSnapshot() : null;
        return OpenNewAsync(config, preset: preset);
    }

    /// <summary>Search Again in the active search editor (UI thread). False when the active editor is not a search editor.</summary>
    public bool RerunActive()
    {
        if (_w.Navigation?.ActiveDocument is not { } doc || doc.LanguageId != LanguageId) return false;
        EnsureSession(doc).Rerun();
        return true;
    }

    /// <summary>Opens the target of the result at <paramref name="position"/> (a double-click): the file at that line and column, beside the editor when asked.</summary>
    internal async Task NavigateAsync(ITextDocument document, TextPosition position, bool toSide)
    {
        if (_w.Navigation is not { } nav) return;
        var folders = Folders;
        // Finding the block's path can walk far up a big file: not on the UI thread.
        var target = await Task.Run(() => SearchResultFeatures.Resolve(document, position, folders)).ConfigureAwait(false);
        if (target is null) return;
        var at = new TextRange(target.Position, target.Position);
        if (toSide) await nav.OpenToSideAsync(target.Uri, at).ConfigureAwait(false);
        else await nav.OpenAsync(target.Uri, at).ConfigureAwait(false);
    }

    internal static string TitleFor(SearchEditorConfig config)
    {
        var q = config.Query.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (q.Length == 0) return "Search";
        return "Search: " + (q.Length > 30 ? q[..29] + "…" : q);
    }

    /// <summary>What Save As proposes: <c>&lt;first folder&gt;/&lt;query&gt;.code-search</c>.</summary>
    internal Uri? SaveUriFor(SearchEditorConfig config)
    {
        if (Folders is not [var folder, ..]) return null;
        var name = Regex.Replace(config.Query, @"[^\w \-.]+", "_", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Trim(' ', '.', '_');
        if (name.Length > 60) name = name[..60].TrimEnd();
        if (name.Length == 0) name = "search";
        return SearchResultPaths.Combine(folder, name + SearchEditorFormat.FileExtension);
    }

    // ----------------------------------------------------------------------------------------- commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, string category, Func<CommandInvocation, ValueTask<object?>> handler, string? enablement = null, bool hidden = false)
        {
            if (commands.Has(id)) return;
            _store.Add(hidden ? commands.Register(id, handler) : commands.Register(id, handler, new CommandMetadata(id, title, category, Enablement: enablement)));
        }

        Reg(OpenNewEditor, "New Search Editor", "Search Editor", async inv => await OpenFromArgsAsync(inv, toSide: false).ConfigureAwait(true));
        Reg(OpenNewEditorToSide, "Open New Search Editor to the Side", "Search Editor", async inv => await OpenFromArgsAsync(inv, toSide: true).ConfigureAwait(true));
        Reg(OpenInEditor, "Open Results in Editor", "Search", async _ => await OpenInEditorAsync(LiveFind()).ConfigureAwait(true));
        Reg(RerunEditorSearch, "Search Again", "Search Editor", RerunAsync, InSearchEditorKey);
        Reg(RerunEditorSearchVsCode, "Search Again", "Search Editor", RerunAsync, hidden: true);
    }

    private async ValueTask<object?> RerunAsync(CommandInvocation _)
    {
        await _w.Ui.InvokeAsync(() => RerunActive()).ConfigureAwait(true);
        return null;
    }

    /// <summary>VS Code's <c>OpenSearchEditorArgs</c> (query, filesToInclude, filesToExclude, contextLines, isRegexp, isCaseSensitive, matchWholeWord, useExcludeSettingsAndIgnoreFiles, onlyOpenEditors, triggerSearch) or a query string.</summary>
    private async Task<IEditorDocument?> OpenFromArgsAsync(CommandInvocation inv, bool toSide)
    {
        var config = NewConfig(SeedQuery());
        bool? trigger = null;
        if (inv.Args is [{ } raw, ..])
        {
            if (raw is string s) config = config with { Query = s };
            else
            {
                JsonElement e;
                try { e = raw is JsonElement je ? je : JsonSerializer.SerializeToElement(raw); }
                catch (NotSupportedException) { e = default; }
                if (e.ValueKind == JsonValueKind.String) config = config with { Query = e.GetString() ?? "" };
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    string? Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    bool? Bool(string n) => e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                    int? Int(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                    config = config with
                    {
                        Query = Str("query") ?? config.Query,
                        Include = Str("filesToInclude") ?? config.Include,
                        Exclude = Str("filesToExclude") ?? config.Exclude,
                        ContextLines = Math.Clamp(Int("contextLines") ?? config.ContextLines, 0, SearchEditorFormat.MaxContextLines),
                        IsRegex = Bool("isRegexp") ?? config.IsRegex,
                        MatchCase = Bool("isCaseSensitive") ?? config.MatchCase,
                        WholeWord = Bool("matchWholeWord") ?? config.WholeWord,
                        UseExcludeSettingsAndIgnoreFiles = Bool("useExcludeSettingsAndIgnoreFiles") ?? config.UseExcludeSettingsAndIgnoreFiles,
                        OnlyOpenEditors = Bool("onlyOpenEditors") ?? config.OnlyOpenEditors,
                    };
                    trigger = Bool("triggerSearch");
                }
            }
        }
        return await OpenNewAsync(config, toSide, run: trigger ?? true).ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        SearchEditorSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var s in sessions) s.Dispose();
        _lane?.Dispose();
        _lane = null;
    }
}
