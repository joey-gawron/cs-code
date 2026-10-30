using CsCode.App.Docking;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Text;

namespace CsCode.App.QuickInput;

/// <summary>
/// Everything the palette modes need. All optional services degrade gracefully (a mode simply has nothing to offer):
/// files need a workspace, symbols need language providers, commands need nothing but the command table.
/// </summary>
public sealed record PaletteContext(
    ICommandService Commands,
    INavigationService Navigation,
    DocumentWell Well,
    IContextKeyService Keys,
    IKeybindingService? Bindings = null,
    IWorkspaceService? Workspace = null,
    IStateStorage? State = null,
    ILanguageFeatureRegistry? Languages = null,
    IMemoryMonitor? Memory = null,
    SessionCatalog? Sessions = null);

/// <summary>
/// The command palette / Quick Launch as a quick-pick source. The first character selects the mode: none = go to file,
/// <c>&gt;</c> commands, <c>:</c> go to line, <c>@</c> symbols in the active file, <c>#</c> workspace symbols, <c>session:</c> saved sessions
/// (background mode, docs/SESSIONS.md), <c>?</c> help.
/// Cheap modes answer synchronously (first paint is instant); file search and symbol queries rank on a worker and only the top
/// <see cref="MaxResults"/> immutable entries reach the UI thread. Command mode lists <b>every</b> command, as VS Code does (a few hundred rows; the
/// quick-input list is virtualised): synchronously up to <see cref="SyncCommandLimit"/> commands, on a worker beyond that.
/// </summary>
public sealed class PaletteSource : IQuickPickSource
{
    public const int MaxResults = 100;
    /// <summary>
    /// Command mode answers on the UI thread while the command table has at most this many commands (the threading rule: no sorting or filtering of
    /// more than 1,000 items there); a bigger table is ranked on a worker. Either way every matching command is listed.
    /// </summary>
    public const int SyncCommandLimit = 1_000;
    public const string RecentFilesKey = "shell.recentFiles";
    public const string RecentCommandsKey = "shell.recentCommands";
    /// <summary>Quick Launch's saved-session mode: <c>session:</c> then a filter.</summary>
    public const string SessionPrefix = "session:";
    private const int MaxRecent = 30;

    private readonly PaletteContext _ctx;
    private readonly FileIndex _files;

    public PaletteSource(PaletteContext context, FileIndex? sharedIndex = null)
    {
        _ctx = context;
        _files = sharedIndex ?? new FileIndex(context.Workspace);
    }

    public string? Placeholder => "Type '?' for help, '>' for commands, ':' for line, '@' for symbols, '#' for workspace symbols, 'session:' for sessions";
    public FileIndex Index => _files;

    public IReadOnlyList<QuickPickEntry>? TryQuerySync(string text)
    {
        if (IsSessionQuery(text)) return null; // the saved sessions are read from disk, off the UI thread
        if (text.Length > 0)
        {
            switch (text[0])
            {
                case '>': return _ctx.Commands.All.Count <= SyncCommandLimit ? Commands(text[1..].TrimStart(), _ctx.Commands.All, CancellationToken.None) : null;
                case ':': return GotoLine(text[1..]);
                case '?': return Help();
                case '@': case '#': return null; // symbol providers are asynchronous
            }
        }
        // Plain mode: an empty query shows recent files instantly; a real query needs the (possibly large) index.
        return text.Length == 0 ? Recent() : null;
    }

    public async Task<IReadOnlyList<QuickPickEntry>> QueryAsync(string text, CancellationToken ct)
    {
        if (IsSessionQuery(text)) return await Sessions(text[SessionPrefix.Length..].Trim(), ct).ConfigureAwait(false);
        if (text.Length > 0 && text[0] == '@') return await DocumentSymbols(text[1..].TrimStart(), ct).ConfigureAwait(false);
        if (text.Length > 0 && text[0] == '#') return await WorkspaceSymbols(text[1..].TrimStart(), ct).ConfigureAwait(false);
        if (text.Length > 0 && text[0] == '>')
        {
            // A command table too big for the UI thread: the metadata is taken here, ranked on a worker.
            var all = _ctx.Commands.All;
            return await Task.Run(() => Commands(text[1..].TrimStart(), all, ct), ct).ConfigureAwait(false);
        }
        return await Files(text, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ files

    private async Task<IReadOnlyList<QuickPickEntry>> Files(string query, CancellationToken ct)
    {
        var index = await _files.GetAsync(ct).ConfigureAwait(false);
        var top = new TopK(MaxResults);
        var q = query.Replace('\\', '/');
        for (var i = 0; i < index.Paths.Length; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            var s = FuzzyMatcher.Score(q, index.Paths[i]);
            if (s >= 0) top.Add(s, i);
        }
        var entries = new List<QuickPickEntry>(top.Count);
        foreach (var i in top.OrderedDescending())
        {
            var rel = index.Paths[i];
            var nameStart = rel.LastIndexOf('/') + 1;
            var positions = FuzzyMatcher.Positions(q, rel);
            var uri = index.UriOf(i);
            entries.Add(new QuickPickEntry(rel[nameStart..])
            {
                Description = nameStart > 0 ? rel[..(nameStart - 1)] : null,
                IconKey = "file",
                LabelMatches = positions.Where(p => p >= nameStart).Select(p => p - nameStart).ToArray(),
                Tag = uri,
                Accept = async () => { await _ctx.Navigation.OpenAsync(uri).ConfigureAwait(true); RememberFile(uri); },
            });
        }
        return entries;
    }

    private IReadOnlyList<QuickPickEntry> Recent()
    {
        var recents = _ctx.State?.Get<List<string>>(RecentFilesKey) ?? [];
        var open = _ctx.Well.EditorTabs.Select(t => t.Uri.ToString());
        var result = new List<QuickPickEntry>();
        foreach (var s in open.Concat(recents).Distinct().Take(MaxResults))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme == "untitled") continue;
            var path = uri.IsFile ? uri.LocalPath : uri.ToString();
            result.Add(new QuickPickEntry(Path.GetFileName(path)) { Description = Path.GetDirectoryName(path), IconKey = "file", Tag = uri, Accept = () => OpenAndRemember(uri) });
        }
        return result;
    }

    private async Task OpenAndRemember(Uri uri)
    {
        await _ctx.Navigation.OpenAsync(uri).ConfigureAwait(true);
        RememberFile(uri);
    }

    private void RememberFile(Uri uri) => Remember(RecentFilesKey, uri.ToString());

    private void Remember(string key, string value)
    {
        if (_ctx.State is null) return;
        var list = _ctx.State.Get<List<string>>(key) ?? [];
        list.Remove(value);
        list.Insert(0, value);
        if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);
        _ctx.State.Set(key, list);
    }

    // ------------------------------------------------------------------ commands

    private IReadOnlyList<QuickPickEntry> Commands(string query, IReadOnlyList<CommandMetadata> all, CancellationToken ct)
    {
        var recents = _ctx.State?.Get<List<string>>(RecentCommandsKey) ?? [];
        var scored = new List<(CommandMetadata Meta, string Label, int Score)>(all.Count);
        foreach (var meta in all)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(meta.Title)) continue;
            if (!_ctx.Keys.Evaluate(meta.Enablement)) continue;
            var label = string.IsNullOrEmpty(meta.Category) ? meta.Title : meta.Category + ": " + meta.Title;
            var s = query.Length == 0 ? 0 : FuzzyMatcher.Score(query, label);
            if (s < 0) continue;
            var recentRank = recents.IndexOf(meta.Id);
            // Recently used commands float to the top; among the rest, best match then alphabetical.
            scored.Add((meta, label, recentRank >= 0 ? 10_000 - recentRank : s));
        }
        scored.Sort(static (a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        // Every command, no cap (T13/T18: with 100, 'Search: Find in Files' and 'Preferences: ...' fell off the end and could not be scrolled to).
        return scored.Select(x => new QuickPickEntry(x.Label)
        {
            KeybindingLabel = _ctx.Bindings?.GetLabel(x.Meta.Id),
            LabelMatches = query.Length == 0 ? [] : FuzzyMatcher.Positions(query, x.Label),
            Tag = x.Meta.Id,
            Accept = async () =>
            {
                Remember(RecentCommandsKey, x.Meta.Id);
                await _ctx.Commands.ExecuteAsync(x.Meta.Id).ConfigureAwait(true);
            },
        }).ToArray();
    }

    // ------------------------------------------------------------------ saved sessions

    private static bool IsSessionQuery(string text) => text.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<QuickPickEntry>> Sessions(string query, CancellationToken ct)
    {
        if (_ctx.Sessions is not { } catalog || !catalog.CanOpen)
            return [new QuickPickEntry("Sessions are opened by CsCode's background mode") { Description = "this window runs with --foreground" }];
        var sessions = await catalog.ListAsync(ct).ConfigureAwait(false);
        if (sessions.Count == 0) return [new QuickPickEntry("No other saved sessions") { Description = "File > Sessions > Save Session As... names this one" }];
        var scored = new List<(SavedSession Session, int Score)>();
        foreach (var s in sessions)
        {
            var score = query.Length == 0 ? 0 : FuzzyMatcher.Score(query, s.Name);
            // A session is also found by its folder's name (not the whole path: every path would match a short query).
            if (score < 0 && query.Length > 0 && s.Folder is { } folder) score = FuzzyMatcher.Score(query, Path.GetFileName(folder.TrimEnd('/', '\\'))) - 5;
            if (score >= 0) scored.Add((s, score));
        }
        if (query.Length > 0) scored.Sort(static (a, b) => b.Score.CompareTo(a.Score)); // else most recently used first, as listed
        return scored.Select(x => new QuickPickEntry(x.Session.Name)
        {
            Description = x.Session.Detail.Length > 0 ? x.Session.Detail : x.Session.IsUnnamed ? "unnamed" : null,
            IconKey = "window",
            LabelMatches = query.Length == 0 ? [] : FuzzyMatcher.Positions(query, x.Session.Name),
            Tag = x.Session,
            Accept = () => catalog.OpenAsync(x.Session.Name),
        }).ToArray();
    }

    // ------------------------------------------------------------------ go to line

    private IReadOnlyList<QuickPickEntry> GotoLine(string text)
    {
        var editor = _ctx.Well.ActiveEditor;
        if (editor is null) return [new QuickPickEntry("Open a file first to go to a line")];
        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        var lineCount = editor.Doc.LineCount;
        if (parts[0].Length == 0 || !int.TryParse(parts[0], out var line))
            return [new QuickPickEntry($"Type a line number between 1 and {lineCount} to go to")];
        line = Math.Clamp(line, 1, lineCount);
        var col = parts.Length > 1 && int.TryParse(parts[1], out var c) ? Math.Max(1, c) : 1;
        var label = parts.Length > 1 ? $"Go to line {line}, column {col}" : $"Go to line {line}";
        var pos = new TextPosition(line - 1, col - 1);
        return [new QuickPickEntry(label) { Tag = pos, Accept = () => { editor.Controller?.SetSelection(new TextRange(pos, pos), reveal: true); return Task.CompletedTask; } }];
    }

    // ------------------------------------------------------------------ symbols

    private async Task<IReadOnlyList<QuickPickEntry>> DocumentSymbols(string query, CancellationToken ct)
    {
        var editor = _ctx.Well.ActiveEditor;
        if (editor is null || _ctx.Languages is null) return [new QuickPickEntry("No symbol information available")];
        var doc = editor.Doc;
        var providers = _ctx.Languages.GetProviders<IDocumentSymbolProvider>(doc);
        var flat = new List<(DocumentSymbol Symbol, string Container)>();
        foreach (var p in providers)
        {
            var symbols = await p.ProvideDocumentSymbolsAsync(doc, ct).ConfigureAwait(false);
            if (symbols is not null) Flatten(symbols, "", flat);
        }
        if (flat.Count == 0) return [new QuickPickEntry("No symbols found in this file")];
        var scored = new List<(DocumentSymbol S, string C, int Score)>();
        foreach (var (s, c) in flat)
        {
            var score = query.Length == 0 ? 0 : FuzzyMatcher.Score(query, s.Name);
            if (score >= 0) scored.Add((s, c, score));
        }
        if (query.Length > 0) scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(MaxResults).Select(x => new QuickPickEntry(x.S.Name)
        {
            Description = string.IsNullOrEmpty(x.C) ? x.S.Kind.ToString() : $"{x.S.Kind} in {x.C}",
            IconKey = SymbolIcon(x.S.Kind),
            LabelMatches = query.Length == 0 ? [] : FuzzyMatcher.Positions(query, x.S.Name),
            Tag = x.S.SelectionRange,
            Accept = () => { editor.Controller?.SetSelection(x.S.SelectionRange, reveal: true); return Task.CompletedTask; },
        }).ToArray();
    }

    private static void Flatten(IReadOnlyList<DocumentSymbol> symbols, string container, List<(DocumentSymbol, string)> into)
    {
        foreach (var s in symbols)
        {
            into.Add((s, container));
            if (s.Children is { Count: > 0 } kids) Flatten(kids, string.IsNullOrEmpty(container) ? s.Name : container + "." + s.Name, into);
        }
    }

    private async Task<IReadOnlyList<QuickPickEntry>> WorkspaceSymbols(string query, CancellationToken ct)
    {
        if (_ctx.Languages is null || query.Length == 0) return [new QuickPickEntry("Type a name to search workspace symbols")];
        var results = new List<WorkspaceSymbol>();
        foreach (var p in _ctx.Languages.GetAll<IWorkspaceSymbolProvider>())
        {
            var found = await p.ProvideWorkspaceSymbolsAsync(query, ct).ConfigureAwait(false);
            if (found is not null) results.AddRange(found);
        }
        return results.Take(MaxResults).Select(s => new QuickPickEntry(s.Name)
        {
            Description = (s.ContainerName is { Length: > 0 } ? s.ContainerName + " - " : "") + Path.GetFileName(s.Location.Uri.LocalPath),
            IconKey = SymbolIcon(s.Kind),
            LabelMatches = FuzzyMatcher.Positions(query, s.Name),
            Tag = s.Location,
            Accept = async () => { await _ctx.Navigation.OpenAsync(s.Location.Uri, s.Location.Range).ConfigureAwait(true); },
        }).ToArray();
    }

    private static string SymbolIcon(SymbolKind k) => k switch
    {
        SymbolKind.Class => "symbol-class", SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor => "symbol-method",
        SymbolKind.Property => "symbol-property", SymbolKind.Field => "symbol-field", SymbolKind.Variable => "symbol-variable",
        SymbolKind.Interface => "symbol-interface", SymbolKind.Enum => "symbol-enum", SymbolKind.EnumMember => "symbol-enum-member",
        SymbolKind.Namespace or SymbolKind.Module or SymbolKind.Package => "symbol-namespace", SymbolKind.Struct => "symbol-structure",
        SymbolKind.Constant => "symbol-constant", SymbolKind.Event => "symbol-event", SymbolKind.Operator => "symbol-operator",
        SymbolKind.Key => "symbol-key", SymbolKind.String => "symbol-string", SymbolKind.Number => "symbol-numeric", SymbolKind.Boolean => "symbol-boolean",
        SymbolKind.Array => "symbol-array", _ => "symbol-misc",
    };

    // ------------------------------------------------------------------ help

    private IReadOnlyList<QuickPickEntry> Help() =>
    [
        new QuickPickEntry("[no prefix]") { Description = "Go to file" },
        new QuickPickEntry(">") { Description = "Show and run commands" },
        new QuickPickEntry(":") { Description = "Go to line / column in the active editor" },
        new QuickPickEntry("@") { Description = "Go to symbol in the active editor" },
        new QuickPickEntry("#") { Description = "Go to symbol in the workspace" },
        new QuickPickEntry(SessionPrefix) { Description = "Open a saved session" },
    ];

    /// <summary>Bounded top-K by score (insertion into a tiny sorted array): no allocation per candidate, no full sort of 200k hits.</summary>
    private sealed class TopK(int k)
    {
        private readonly (int Score, int Id)[] _items = new (int, int)[k];
        public int Count { get; private set; }

        public void Add(int score, int id)
        {
            if (Count == k && score <= _items[Count - 1].Score) return;
            var i = Count < k ? Count++ : k - 1;
            while (i > 0 && _items[i - 1].Score < score) { _items[i] = _items[i - 1]; i--; }
            _items[i] = (score, id);
        }

        public IEnumerable<int> OrderedDescending() => _items.Take(Count).Select(x => x.Id);
    }
}

/// <summary>
/// Relative-path index of the workspace for the go-to-file mode. Built once per workspace off the UI thread (streamed from
/// <see cref="IWorkspaceService.FindFilesAsync"/>), capped at <see cref="MaxFiles"/>, invalidated when folders change, and
/// registered as an <see cref="IMemoryTrimmable"/>: under pressure it is dropped and rebuilt on the next query.
/// </summary>
public sealed class FileIndex : IMemoryTrimmable, IDisposable
{
    public const int MaxFiles = 300_000;
    private readonly IWorkspaceService? _workspace;
    private readonly object _gate = new();
    private readonly IDisposable? _sub;
    private Task<Snapshot>? _build;

    public FileIndex(IWorkspaceService? workspace)
    {
        _workspace = workspace;
        _sub = workspace?.FoldersChanged.Subscribe(_ => Invalidate());
    }

    public string Name => "shell.fileIndex";
    private long _bytes;
    public long EstimatedBytes => Interlocked.Read(ref _bytes);
    private int _buildCount;
    public int BuildCount => Volatile.Read(ref _buildCount);

    public long Trim(MemoryTrimLevel level)
    {
        var freed = EstimatedBytes;
        if (level >= MemoryTrimLevel.Moderate) Invalidate();
        return level >= MemoryTrimLevel.Moderate ? freed : 0;
    }

    public void Invalidate()
    {
        lock (_gate) _build = null;
        Interlocked.Exchange(ref _bytes, 0);
    }

    /// <summary>
    /// The index, built once and shared. A build that failed (an unreadable folder, a file system error) is not kept: the caller sees its exception
    /// once and the next query builds again (T12; it used to be cached until the folders changed, so go to file stayed broken).
    /// </summary>
    public Task<Snapshot> GetAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_build is { IsFaulted: true } or { IsCanceled: true }) _build = null;
            return _build ??= Task.Run(() => BuildAsync(), CancellationToken.None);
        }
    }

    private async Task<Snapshot> BuildAsync()
    {
        Interlocked.Increment(ref _buildCount);
        var roots = _workspace?.Folders.ToArray() ?? [];
        var paths = new List<string>();
        var rootIndex = new List<byte>();
        if (_workspace is not null)
            await foreach (var uri in _workspace.FindFilesAsync(null, MaxFiles).ConfigureAwait(false))
            {
                var r = Array.FindIndex(roots, root => uri.AbsoluteUri.StartsWith(root.AbsoluteUri.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
                if (r < 0) continue;
                paths.Add(Uri.UnescapeDataString(uri.AbsoluteUri[(roots[r].AbsoluteUri.TrimEnd('/').Length + 1)..]));
                rootIndex.Add((byte)Math.Min(r, 255));
            }
        Interlocked.Exchange(ref _bytes, paths.Count * 160L);
        return new Snapshot(paths.ToArray(), rootIndex.ToArray(), roots);
    }

    public sealed record Snapshot(string[] Paths, byte[] RootOf, Uri[] Roots)
    {
        public Uri UriOf(int i) => new(Roots[RootOf[i]].AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(Paths[i]).Replace("%2F", "/"));
    }

    public void Dispose() => _sub?.Dispose();
}
