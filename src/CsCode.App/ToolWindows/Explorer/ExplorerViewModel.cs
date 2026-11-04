using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.Services;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Collections;
using CsCode.Ui.Threading;

namespace CsCode.App.ToolWindows.Explorer;

/// <summary>
/// The Explorer model. The tree is stored as nodes with lazily loaded children and <i>displayed</i> as a flat list of visible rows
/// (<see cref="Rows"/>), so the list control virtualises it and 20k siblings cost one bulk collection change. Directory reads, sorting
/// and diffing happen on workers; the UI thread only splices already-built node arrays into <see cref="Rows"/>. File-system changes
/// arrive batched from <see cref="IFileWatcher"/> and are diffed against the loaded subtree by name: the tree is never rebuilt. The watch leaves
/// out what <c>files.watcherExclude</c> or a cache tag excludes (see <see cref="WatchExcludeGlobs"/>), except the expanded folders there, which
/// it is asked for (<see cref="FileWatchWants"/>) and which are read from the disk again whenever they are expanded. The watcher's
/// <c>Changed(root)</c> — events were lost — re-reads every loaded folder of that root.
/// All file operations go through <see cref="IFileSystem"/> (async; delete uses the trash), with the file-operation participants around them
/// (language servers: will-rename edits are applied before a rename or move, did-create/rename/delete follow). UI-thread object.
/// </summary>
public sealed class ExplorerViewModel : ObservableObject, IDisposable
{
    public const string ExpandedStateKey = "explorer.expanded";
    private const int WatcherDebounceMs = 150;

    private readonly IWorkspaceService _workspace;
    private readonly IUiDispatcher _ui;
    private readonly IFileSystem _fs;
    private readonly INavigationService? _navigation;
    private readonly IConfigurationService? _config;
    private readonly IStateStorage? _state;
    private readonly IModalDialogs? _dialogs;
    private readonly DocumentWell? _well;
    private readonly FileParticipants? _participants;
    private readonly DisposableStore _subs = new();
    private readonly List<IDisposable> _watches = new();
    private readonly List<ExplorerNode> _roots = new();
    private readonly HashSet<Uri> _expandedUris = new();
    private readonly object _pendingGate = new();
    private readonly HashSet<Uri> _dirtyDirs = new();
    private bool _flushScheduled;
    private readonly Dictionary<Uri, FileWatchWants> _wants = new();   // per root folder: the folders its watch is asked for
    private readonly HashSet<Uri> _wanted = new();                     // expanded folders in a part of the tree the watch leaves out
    private readonly HashSet<ExplorerNode> _rescanRoots = new();       // roots whose own watch reported Changed(root): every loaded folder is re-read
    private IReadOnlyList<string> _watchExcludes = [];
    private Task _wantsApplied = Task.CompletedTask;
    private ExplorerNode? _selected;
    private string _filter = "";
    private (List<Uri> Uris, bool Cut)? _clipboard;
    private int _loadGeneration;
    private bool _restoring;

    public ExplorerViewModel(IWorkspaceService workspace, IUiDispatcher ui, INavigationService? navigation = null, IConfigurationService? config = null,
        IStateStorage? state = null, IModalDialogs? dialogs = null, DocumentWell? well = null, FileParticipants? participants = null,
        Core.Scm.IFileDecorationProvider? decorations = null)
    {
        _participants = participants;
        _decorations = decorations;
        if (decorations is not null)
        {
            // decorations follow the rows shown (dictionary lookups on the UI thread) and every change of the provider's (coalesced)
            Rows.CollectionChanged += (_, _) => Decorate();
            _subs.Add(decorations.DecorationsChanged.Subscribe(_ => ui.PostCoalesced("explorer.decorations", Decorate)));
        }
        _workspace = workspace;
        _ui = ui;
        _fs = workspace.FileSystem;
        _navigation = navigation;
        _config = config;
        _state = state;
        _dialogs = dialogs;
        _well = well;
        // Folder changes may be raised on any thread; on the UI thread the refresh starts inline (so callers can await RefreshCompleted).
        _subs.Add(workspace.FoldersChanged.Subscribe(change => { _ = change; _ = _ui.InvokeAsync(() => _refresh = RefreshRootsAsync()); }));
    }

    private Task _refresh = Task.CompletedTask;
    private readonly Core.Scm.IFileDecorationProvider? _decorations;

    /// <summary>Puts each shown row's source-control decoration on it (UI thread).</summary>
    private void Decorate()
    {
        if (_decorations is null) return;
        foreach (var node in Rows) node.Decoration = _decorations.GetDecoration(node.Uri, node.IsDirectory);
    }

    /// <summary>The most recently started root refresh (completes when roots and restored expansion are loaded).</summary>
    public Task RefreshCompleted => _refresh;

    /// <summary>The most recent change of the folders the watches are asked for (completes when the OS watches follow it).</summary>
    public Task WantsApplied => _wantsApplied;

    public BulkObservableCollection<ExplorerNode> Rows { get; } = new();
    public bool HasFolder => _roots.Count > 0;
    /// <summary>The workspace has a folder whose root row is not in <see cref="Rows"/> yet: a view just created, before its first load.</summary>
    public bool RootsPending => _roots.Count == 0 && _workspace.Folders.Count > 0;
    public ExplorerNode? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public IReadOnlyList<ExplorerNode> Roots => _roots;

    /// <summary>Number of directory reads performed (tests assert that watcher updates never re-read the whole tree).</summary>
    public int DirectoryReads { get; private set; }

    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value ?? "")) ApplyFilter(); }
    }

    // ------------------------------------------------------------------ roots

    public async Task RefreshRootsAsync()
    {
        _ui.AssertUiThread();
        var generation = ++_loadGeneration;
        foreach (var w in _watches) w.Dispose();
        _watches.Clear();
        _wants.Clear();
        _wanted.Clear();
        _roots.Clear();
        Rows.Clear();
        _watchExcludes = WatchExcludeGlobs();

        var restored = _state?.Get<List<string>>(ExpandedStateKey, StorageScope.Workspace) ?? [];
        foreach (var s in restored) if (Uri.TryCreate(s, UriKind.Absolute, out var u)) _expandedUris.Add(u);

        foreach (var folder in _workspace.Folders)
        {
            var name = Path.GetFileName(folder.LocalPath.TrimEnd('/', '\\'));
            var root = new ExplorerNode(folder, name.Length > 0 ? name : folder.LocalPath, true, 0, null, isRoot: true);
            _roots.Add(root);
            var wants = new FileWatchWants();
            _wants[folder] = wants;
            var rootKey = folder.AbsoluteUri.TrimEnd('/');
            _watches.Add(_workspace.Watcher.Watch(folder, true, _watchExcludes, changes => OnWatcherChanges(root, rootKey, changes), wants));
        }
        OnPropertyChanged(nameof(HasFolder));
        Rows.AddRange(_roots);
        _restoring = true;
        try
        {
            foreach (var root in _roots.ToArray())
            {
                if (generation != _loadGeneration) return;
                await ExpandAsync(root).ConfigureAwait(true);
                // Restore persisted expansion top-down: a node can only be expanded after its parent was loaded.
                await RestoreExpandedAsync(root, generation).ConfigureAwait(true);
            }
        }
        finally { _restoring = false; }
    }

    private async Task RestoreExpandedAsync(ExplorerNode node, int generation)
    {
        if (node.Children is null) return;
        foreach (var child in node.Children.ToArray())
        {
            if (generation != _loadGeneration) return;
            if (child.IsDirectory && _expandedUris.Contains(child.Uri) && !child.IsExpanded)
            {
                await ExpandAsync(child).ConfigureAwait(true);
                await RestoreExpandedAsync(child, generation).ConfigureAwait(true);
            }
        }
    }

    // ------------------------------------------------------------------ expand / collapse

    public async Task ToggleAsync(ExplorerNode node)
    {
        if (!node.IsDirectory) return;
        if (node.IsExpanded) Collapse(node); else await ExpandAsync(node).ConfigureAwait(true);
    }

    public async Task ExpandAsync(ExplorerNode node)
    {
        _ui.AssertUiThread();
        if (!node.IsDirectory || node.IsExpanded) return;
        // The folder, and the expanded ones it shows again, that lie where the watch leaves out are asked for first; what they hold is then read
        // from the disk, since nothing reported their changes while they were out of sight.
        var outOfWatch = await WantShownAsync(node).ConfigureAwait(true);
        if (node.IsExpanded) return; // expanded meanwhile
        if (node.Children is not null && outOfWatch.Contains(node)) await ReloadDirectoryAsync(node).ConfigureAwait(true);
        if (node.Children is null)
        {
            node.IsLoading = true;
            try
            {
                var children = await LoadChildrenAsync(node).ConfigureAwait(true);
                node.Children = children;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                node.Children = [];
            }
            finally { node.IsLoading = false; }
        }
        foreach (var shown in ExpandedBelow(node))
            if (outOfWatch.Contains(shown) && shown.Children is not null) await ReloadDirectoryAsync(shown).ConfigureAwait(true); // not in Rows yet: only its children change
        node.IsExpanded = true;
        _expandedUris.Add(node.Uri);
        var index = Rows.IndexOf(node);
        if (index >= 0)
        {
            var visible = node.VisibleDescendants().ToList(); // nodes only: no I/O or sorting here
            Rows.InsertRange(index + 1, visible);
        }
        if (!_restoring) PersistExpanded();
    }

    public void Collapse(ExplorerNode node)
    {
        if (!node.IsDirectory || !node.IsExpanded) return;
        var index = Rows.IndexOf(node);
        var count = node.VisibleDescendants().Count();
        node.IsExpanded = false;
        _expandedUris.Remove(node.Uri);
        if (index >= 0 && count > 0) Rows.RemoveRange(index + 1, count);
        if (Selected is { } s && s != node && IsDescendantOf(s, node)) Selected = node;
        PersistExpanded();
        // Out of sight: the watch need not keep the folders below it that it would otherwise leave out (re-read when shown again).
        if (Unwant(node.Uri) && RootOf(node) is { } root) _ = ApplyWantsAsync(root.Uri);
    }

    /// <summary>Stops asking for <paramref name="folder"/> and every folder below it. True when something was asked for.</summary>
    private bool Unwant(Uri folder)
    {
        var prefix = folder.AbsoluteUri.TrimEnd('/') + "/";
        return _wanted.RemoveWhere(u => u.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) || u.AbsoluteUri.TrimEnd('/') == prefix[..^1]) > 0;
    }

    private static ExplorerNode? RootOf(ExplorerNode node)
    {
        var n = node;
        while (n.Parent is not null) n = n.Parent;
        return n.IsRoot ? n : null;
    }

    /// <summary>The expanded folders below <paramref name="node"/> that expanding it shows again (their parents expanded too), top-down.</summary>
    private static List<ExplorerNode> ExpandedBelow(ExplorerNode node)
    {
        var list = new List<ExplorerNode>();
        Add(node);
        return list;

        void Add(ExplorerNode n)
        {
            if (n.Children is null) return;
            foreach (var c in n.Children)
            {
                if (!c.IsDirectory || !c.IsExpanded) continue;
                list.Add(c);
                Add(c);
            }
        }
    }

    /// <summary>
    /// <paramref name="node"/> (unless it is a root) and the expanded folders it shows again, those of them that lie where the folder's watch
    /// leaves things out: asks the watch for them (each one's own entries, not what is below) and returns them — to be read from the disk —
    /// once the OS watch is in place. On a local folder that is <see cref="WatchExclusion.IsInExcludedArea"/> (watcher-excluded, or in a cache
    /// directory). On a remote one (ssh, docker, wsl) the client cannot see the server's cache tags, so every one of them is asked for (the
    /// server watches anew only those it would leave out) and re-read. Empty for the rest — the watch reports their changes anyway.
    /// </summary>
    private async Task<HashSet<ExplorerNode>> WantShownAsync(ExplorerNode node)
    {
        var outOfWatch = new HashSet<ExplorerNode>();
        if (RootOf(node) is not { } root || !_wants.TryGetValue(root.Uri, out var wants)) return outOfWatch;
        List<ExplorerNode> candidates = node.IsRoot ? ExpandedBelow(node) : [node, .. ExpandedBelow(node)];
        if (candidates.Count == 0) return outOfWatch;
        bool[] excluded;
        if (root.Uri.IsFile)
        {
            var rootPath = root.Uri.LocalPath;
            var excludes = _watchExcludes;
            var paths = candidates.Select(static n => n.Uri.LocalPath).ToArray();
            excluded = await Task.Run(() => paths.Select(p => WatchExclusion.IsInExcludedArea(rootPath, p, excludes)).ToArray()).ConfigureAwait(true);
            if (!_wants.TryGetValue(root.Uri, out var current) || !ReferenceEquals(current, wants)) return outOfWatch; // the folders were refreshed meanwhile
        }
        else excluded = [.. candidates.Select(static _ => true)];
        for (var i = 0; i < candidates.Count; i++)
            if (excluded[i] && IsAttached(candidates[i], node)) outOfWatch.Add(candidates[i]); // (collapsed or dropped while the disk was looked at: not asked for)
        if (outOfWatch.Count == 0) return outOfWatch;
        foreach (var n in outOfWatch) _wanted.Add(n.Uri);
        await ApplyWantsAsync(root.Uri).ConfigureAwait(true);
        return outOfWatch;
    }

    /// <summary>
    /// <paramref name="n"/> is still in the tree (each ancestor lists it) and will be shown once <paramref name="expanding"/> is: every folder
    /// between them is expanded, and so is every folder above <paramref name="expanding"/>.
    /// </summary>
    private bool IsAttached(ExplorerNode n, ExplorerNode expanding)
    {
        if (n != expanding && !n.IsExpanded) return false;
        for (var c = n; c.Parent is { } p; c = p)
        {
            if (p.Children is not { } siblings || !siblings.Contains(c)) return false; // dropped from the listing
            if (p != expanding && !p.IsExpanded) return false;                          // collapsed meanwhile
        }
        return RootOf(n) is { } root && _roots.Contains(root);
    }

    private Task ApplyWantsAsync(Uri root)
    {
        if (!_wants.TryGetValue(root, out var wants)) return Task.CompletedTask;
        var prefix = root.AbsoluteUri.TrimEnd('/') + "/";
        var directories = _wanted.Where(u => u.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return _wantsApplied = wants.SetAsync(directories, []);
    }

    private static bool IsDescendantOf(ExplorerNode n, ExplorerNode ancestor)
    {
        for (var p = n.Parent; p is not null; p = p.Parent) if (p == ancestor) return true;
        return false;
    }

    private async Task<List<ExplorerNode>> LoadChildrenAsync(ExplorerNode parent)
    {
        DirectoryReads++;
        var excludes = ExcludeGlobs();
        var entries = await _fs.ReadDirectoryAsync(parent.Uri).ConfigureAwait(true);
        var uri = parent.Uri;
        var depth = parent.Depth + 1;
        // Sorting and node construction for a potentially huge folder happen on a worker.
        return await Task.Run(() => BuildChildren(parent, uri, depth, entries, excludes)).ConfigureAwait(true);
    }

    private static List<ExplorerNode> BuildChildren(ExplorerNode parent, Uri parentUri, int depth, IReadOnlyList<DirectoryEntry> entries, IReadOnlyList<string> excludes)
    {
        var list = new List<ExplorerNode>(entries.Count);
        var baseUri = parentUri.AbsoluteUri.TrimEnd('/') + "/";
        foreach (var e in entries)
        {
            if (IsExcluded(e.Name, excludes)) continue;
            var isDir = e.Type.HasFlag(FileType.Directory);
            var childUri = new Uri(baseUri + Uri.EscapeDataString(e.Name) + (isDir ? "/" : ""));
            list.Add(new ExplorerNode(childUri, e.Name, isDir, depth, parent));
        }
        list.Sort(CompareNodes);
        return list;
    }

    internal static int CompareNodes(ExplorerNode a, ExplorerNode b) =>
        a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> ExcludeGlobs() =>
        _config?.GetRaw(SettingKeys.FilesExclude) is { ValueKind: JsonValueKind.Object } obj ? EnabledGlobs(obj) : DefaultExcludes;

    /// <summary>
    /// What the folder's watch leaves out: <c>files.watcherExclude</c> ∪ <c>files.exclude</c>. Its subscription shares the folder's OS watch with
    /// the language servers' and EditorConfig's (<c>files.watcherExclude</c>), and a directory whose contents every subscription excludes costs no
    /// OS watch; with <c>files.exclude</c> alone it kept <c>node_modules/</c>, <c>bin/</c> and <c>obj/</c> watched for everyone — tens of
    /// thousands of inotify watches on a big tree. What refreshes live: everything outside those folders, the folders themselves appearing,
    /// going or being renamed (an event is excluded by its own path only), and the entries of every expanded folder inside them — the Explorer
    /// asks the watch for those (<see cref="WantShownAsync"/>). Not live: a collapsed folder in there (re-read when expanded). What is shown is
    /// still filtered by <c>files.exclude</c> alone.
    /// </summary>
    private IReadOnlyList<string> WatchExcludeGlobs()
    {
        var watcher = _config?.GetRaw(SettingKeys.FilesWatcherExclude) is { ValueKind: JsonValueKind.Object } obj ? EnabledGlobs(obj) : WatchExclusion.DefaultExcludes;
        return watcher.Union(ExcludeGlobs(), StringComparer.Ordinal).ToArray();
    }

    private static List<string> EnabledGlobs(JsonElement obj)
    {
        var globs = new List<string>();
        foreach (var p in obj.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.True) globs.Add(p.Name);
        return globs;
    }

    private static readonly string[] DefaultExcludes = ["**/.git", "**/.DS_Store", "**/Thumbs.db"];

    private static bool IsExcluded(string name, IReadOnlyList<string> globs)
    {
        foreach (var g in globs)
        {
            // Patterns like **/.git match by name at any depth; Core Glob handles braces and wildcards.
            if (Glob.IsMatch(g, name) || Glob.IsMatch(g, "/" + name)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ persistence

    private void PersistExpanded() => _state?.Set(ExpandedStateKey, _expandedUris.Select(u => u.ToString()).ToList(), StorageScope.Workspace);

    // ------------------------------------------------------------------ reveal / select / type-ahead

    /// <summary>Expands ancestors as needed and selects the node for <paramref name="uri"/> ("reveal active file").</summary>
    public async Task<ExplorerNode?> RevealAsync(Uri uri)
    {
        var root = _roots.FirstOrDefault(r => uri.AbsoluteUri.StartsWith(r.Uri.AbsoluteUri.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        if (root is null) return null;
        var rel = Uri.UnescapeDataString(uri.AbsoluteUri[(root.Uri.AbsoluteUri.TrimEnd('/').Length + 1)..]).TrimEnd('/');
        var node = root;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            await ExpandAsync(node).ConfigureAwait(true);
            node = node.Children?.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        Selected = node;
        return node;
    }

    /// <summary>Keyboard type-ahead: selects the next visible row whose name starts with <paramref name="prefix"/>.</summary>
    public ExplorerNode? TypeAhead(string prefix)
    {
        if (prefix.Length == 0 || Rows.Count == 0) return null;
        var start = Selected is null ? -1 : Rows.IndexOf(Selected);
        for (var k = 1; k <= Rows.Count; k++)
        {
            var node = Rows[(start + k) % Rows.Count];
            if (node.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { Selected = node; return node; }
        }
        return null;
    }

    public async Task OpenAsync(ExplorerNode node, bool preview)
    {
        if (node.IsDirectory) { await ToggleAsync(node).ConfigureAwait(true); return; }
        if (_navigation is not null) await _navigation.OpenAsync(node.Uri, preview: preview).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ filter

    private CancellationTokenSource? _filterCts;

    private void ApplyFilter()
    {
        _filterCts?.Cancel();
        if (_filter.Length == 0)
        {
            Rows.ReplaceAll(_roots.SelectMany(r => new[] { r }.Concat(r.VisibleDescendants())));
            return;
        }
        // Filter applies to the loaded part of the tree; ranking runs on a worker, only the result list is applied on the UI thread.
        var cts = _filterCts = new CancellationTokenSource();
        var snapshot = _roots.SelectMany(r => r.Descendants()).ToArray();
        _ = ApplyFilterAsync(_filter, snapshot, cts.Token);
    }

    private async Task ApplyFilterAsync(string text, ExplorerNode[] snapshot, CancellationToken ct)
    {
        try
        {
            var matches = await Task.Run(() => snapshot.Where(n => n.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(2000).ToArray(), ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested && _filter == text) Rows.ReplaceAll(matches);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer filter text
        }
    }

    // ------------------------------------------------------------------ file operations

    private static Uri Child(Uri dir, string name, bool isDir) => new(dir.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(name) + (isDir ? "/" : ""));

    private ExplorerNode ContainerOf(ExplorerNode node) => node.IsDirectory ? node : node.Parent ?? _roots[0];

    public async Task<Uri?> CreateFileAsync(ExplorerNode parent, string name) => await CreateAsync(parent, name, isDirectory: false).ConfigureAwait(true);
    public async Task<Uri?> CreateFolderAsync(ExplorerNode parent, string name) => await CreateAsync(parent, name, isDirectory: true).ConfigureAwait(true);

    private async Task<Uri?> CreateAsync(ExplorerNode where, string name, bool isDirectory)
    {
        var container = ContainerOf(where);
        if (ValidateName(name, container, null) is { } error) { await ReportAsync(error).ConfigureAwait(true); return null; }
        var uri = Child(container.Uri, name, isDirectory);
        if (isDirectory) await _fs.CreateDirectoryAsync(uri).ConfigureAwait(true);
        else await _fs.WriteFileAsync(uri, ReadOnlyMemory<byte>.Empty, createParents: false).ConfigureAwait(true);
        if (_participants is not null) await _participants.DidCreateAsync([uri]).ConfigureAwait(true);
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        return uri;
    }

    /// <summary>Null if the name is acceptable, else the message to show.</summary>
    public string? ValidateName(string name, ExplorerNode container, ExplorerNode? renaming)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A file or folder name must be provided.";
        if (name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..") return $"The name '{name}' is not valid as a file or folder name.";
        if (container.Children?.Any(c => c != renaming && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
            return $"A file or folder '{name}' already exists at this location. Please choose a different name.";
        return null;
    }

    public void BeginRename(ExplorerNode node)
    {
        node.EditText = node.Name;
        node.IsEditing = true;
    }

    public void CancelRename(ExplorerNode node) => node.IsEditing = false;

    public async Task<bool> CommitRenameAsync(ExplorerNode node)
    {
        var newName = node.EditText.Trim();
        node.IsEditing = false;
        if (newName == node.Name) return true;
        var container = node.Parent ?? ContainerOf(node);
        if (ValidateName(newName, container, node) is { } error) { await ReportAsync(error).ConfigureAwait(true); return false; }
        var target = Child(container.Uri, newName, node.IsDirectory);
        await MoveUriAsync(node.Uri, target).ConfigureAwait(true);
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Renames/moves on disk: the one path every Explorer rename and move takes (F2, cut and paste, drag and drop, undo of a drop). Will-rename
    /// participants (language servers updating imports and references) are asked first and their edit is applied before the move; then the
    /// editors of the files moved follow them to the new path, unsaved changes included (<see cref="DocumentWell.RelocateAsync"/>); did-rename
    /// follows.
    /// </summary>
    private async Task MoveUriAsync(Uri from, Uri to)
    {
        IReadOnlyList<(Uri, Uri)> renamed = [(from, to)];
        IReadOnlyList<(Uri Uri, string Text)> before = [];
        if (_participants is not null && await _participants.WillRenameAsync(renamed).ConfigureAwait(true) is { } edit && _navigation is not null)
        {
            before = await TextsAsync(edit.Changes.Keys).ConfigureAwait(true);
            await _navigation.ApplyWorkspaceEditAsync(edit, $"Rename {NameOf(from)}").ConfigureAwait(true); // declined or failed: the move still happens
        }
        try
        {
            await _fs.RenameAsync(from, to).ConfigureAwait(true);
        }
        catch
        {
            // Nothing moved (a target in the way, a volume that refuses): the imports and references rewritten for the new path are put back; the
            // editors never left (files-1).
            await RevertTextsAsync(before, $"Undo Rename {NameOf(from)}").ConfigureAwait(true);
            throw;
        }
        if (_well is not null) await _well.RelocateAsync(from, to).ConfigureAwait(true);
        if (_participants is not null) await _participants.DidRenameAsync(renamed).ConfigureAwait(true);
    }

    /// <summary>The current text of each file (the open editor's, else the file's on disk); a file that cannot be read is left out.</summary>
    private async Task<IReadOnlyList<(Uri Uri, string Text)>> TextsAsync(IEnumerable<Uri> uris)
    {
        var texts = new List<(Uri, string)>();
        foreach (var uri in uris)
        {
            if (_navigation?.OpenDocuments.FirstOrDefault(d => d.Uri == uri) is { } open) { texts.Add((uri, open.GetText())); continue; }
            try { texts.Add((uri, NavigationService.Decode(await _fs.ReadFileAsync(uri).ConfigureAwait(true)).Text)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
        }
        return texts;
    }

    /// <summary>
    /// Puts back the text of every file that changed since <paramref name="before"/> was taken, as one workspace edit (an undo step in an open
    /// editor). Only the will-rename edit ran in between, so a file it did not reach is equal and untouched.
    /// </summary>
    private async Task RevertTextsAsync(IReadOnlyList<(Uri Uri, string Text)> before, string label)
    {
        if (before.Count == 0 || _navigation is null) return;
        var now = (await TextsAsync(before.Select(b => b.Uri)).ConfigureAwait(true)).ToDictionary(t => t.Uri, t => t.Text);
        var changes = new Dictionary<Uri, IReadOnlyList<TextEdit>>();
        foreach (var (uri, original) in before)
            if (now.TryGetValue(uri, out var current) && current != original) changes[uri] = [Restore(uri, current, original)];
        if (changes.Count > 0) await _navigation.ApplyWorkspaceEditAsync(new WorkspaceEdit(changes), label).ConfigureAwait(true);
    }

    /// <summary>One edit that turns <paramref name="current"/> back into <paramref name="original"/>: the span between their common prefix and suffix.</summary>
    private static TextEdit Restore(Uri uri, string current, string original)
    {
        var prefix = 0;
        while (prefix < current.Length && prefix < original.Length && current[prefix] == original[prefix]) prefix++;
        var suffix = 0;
        while (suffix < current.Length - prefix && suffix < original.Length - prefix && current[^(suffix + 1)] == original[^(suffix + 1)]) suffix++;
        var doc = new InMemoryTextDocument(uri, "plaintext", current);
        return new TextEdit(new TextRange(doc.PositionAt(prefix), doc.PositionAt(current.Length - suffix)), original[prefix..(original.Length - suffix)]);
    }

    private static string NameOf(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);

    private static string? RelativeUnder(Uri root, Uri candidate)
    {
        var r = root.AbsoluteUri.TrimEnd('/');
        var c = candidate.AbsoluteUri;
        if (c.Equals(r, StringComparison.OrdinalIgnoreCase)) return "";
        return c.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ? c[(r.Length + 1)..] : null;
    }

    public async Task<bool> DeleteAsync(ExplorerNode node)
    {
        if (node.IsRoot) return false;
        if (_dialogs is not null)
        {
            var answer = await _dialogs.ShowAsync("Delete", $"Are you sure you want to delete '{node.Name}'{(node.IsDirectory ? " and its contents" : "")}?\n\nYou can restore it from the Trash.", ["Move to Trash", "Cancel"]).ConfigureAwait(true);
            if (answer != "Move to Trash") return false;
        }
        if (_well is not null)
            foreach (var tab in _well.EditorTabs.Where(t => RelativeUnder(node.Uri, t.Uri) is not null).ToArray())
                await _well.CloseAsync(tab, prompt: false).ConfigureAwait(true);
        await _fs.DeleteAsync(node.Uri, recursive: true, useTrash: true).ConfigureAwait(true);
        if (_participants is not null) await _participants.DidDeleteAsync([node.Uri]).ConfigureAwait(true);
        var container = node.Parent!;
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        return true;
    }

    public async Task<Uri?> DuplicateAsync(ExplorerNode node)
    {
        var container = node.Parent ?? ContainerOf(node);
        var target = Child(container.Uri, UniqueName(container, node.Name, node.IsDirectory, " copy"), node.IsDirectory);
        await _fs.CopyAsync(node.Uri, target).ConfigureAwait(true);
        if (_participants is not null) await _participants.DidCreateAsync([target]).ConfigureAwait(true);
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        return target;
    }

    public void Copy(IEnumerable<ExplorerNode> nodes) => SetClipboard(nodes, cut: false);
    public void Cut(IEnumerable<ExplorerNode> nodes) => SetClipboard(nodes, cut: true);

    private void SetClipboard(IEnumerable<ExplorerNode> nodes, bool cut)
    {
        if (_clipboard is { } old) foreach (var r in _roots.SelectMany(r => new[] { r }.Concat(r.Descendants()))) if (old.Uris.Contains(r.Uri)) r.IsCut = false;
        var list = nodes.ToList();
        _clipboard = (list.Select(n => n.Uri).ToList(), cut);
        if (cut) foreach (var n in list) n.IsCut = true;
    }

    public bool CanPaste => _clipboard is { Uris.Count: > 0 };

    public async Task PasteAsync(ExplorerNode target)
    {
        if (_clipboard is not { } clip) return;
        var container = ContainerOf(target);
        foreach (var src in clip.Uris)
        {
            var name = Path.GetFileName(src.LocalPath.TrimEnd('/', '\\'));
            var isDir = (await _fs.StatAsync(src).ConfigureAwait(true))?.Type.HasFlag(FileType.Directory) == true;
            if (clip.Cut)
            {
                var dest = Child(container.Uri, name, isDir);
                if (dest == src) continue;
                await MoveUriAsync(src, dest).ConfigureAwait(true);
            }
            else
            {
                var copy = Child(container.Uri, UniqueName(container, name, isDir, " copy"), isDir);
                await _fs.CopyAsync(src, copy).ConfigureAwait(true);
                if (_participants is not null) await _participants.DidCreateAsync([copy]).ConfigureAwait(true);
            }
        }
        if (clip.Cut) _clipboard = null;
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        if (clip.Cut) foreach (var root in _roots) await ReloadLoadedAsync(root).ConfigureAwait(true);
    }

    /// <summary>Moves <paramref name="sources"/> into a folder (or a file's folder) without asking first: <see cref="DropAsync"/> with no confirmation.</summary>
    public Task<IReadOnlyList<Uri>> MoveAsync(IEnumerable<ExplorerNode> sources, ExplorerNode targetFolder) =>
        DropAsync([.. sources.Select(n => n.Uri)], targetFolder, copy: false, confirm: false);

    // ------------------------------------------------------------------ drag and drop (VS Code's Explorer)

    /// <summary>VS Code's <c>explorer.confirmDragAndDrop</c>: a drag and drop that moves files or folders asks first.</summary>
    public const string ConfirmDragAndDropSetting = "explorer.confirmDragAndDrop";

    /// <summary>VS Code's <c>explorer.enableUndo</c>: ⌘Z (Ctrl+Z) in the Explorer undoes its last drag and drop.</summary>
    public const string EnableUndoSetting = "explorer.enableUndo";

    private const string MoveAnswer = "Move", MoveAndStopAsking = "Move, Don't Ask Again", ReplaceAnswer = "Replace", SkipAnswer = "Skip";
    private enum TransferKind { Move, Copy }
    private sealed record Transfer(Uri Source, Uri Target, bool IsDirectory, bool Taken);
    private (TransferKind Kind, IReadOnlyList<(Uri From, Uri To)> Items)? _lastTransfer;

    /// <summary>The folder a drop on <paramref name="target"/> lands in: a folder row is that folder, a file row its folder, no row (the blank space
    /// under the rows) the first workspace folder. Null without a folder.</summary>
    public ExplorerNode? DropFolder(ExplorerNode? target) => target is null ? _roots.FirstOrDefault() : target.IsDirectory ? target : target.Parent;

    /// <summary>
    /// VS Code's rules for dragging Explorer items onto <paramref name="target"/>: refused when any of them is a workspace folder, the folder
    /// itself or a folder above it (a folder cannot go into itself or below itself) or, for a move, already in that folder (nothing would
    /// happen). A copy into its own folder is allowed (it is named "name copy", as Duplicate does).
    /// </summary>
    public bool CanDrop(IReadOnlyList<Uri> sources, ExplorerNode? target, bool copy)
    {
        if (sources.Count == 0 || DropFolder(target) is not { } folder) return false;
        var into = Key(folder.Uri);
        foreach (var source in sources)
        {
            var s = Key(source);
            if (_roots.Any(r => Key(r.Uri) == s)) return false;
            if (s == into || into.StartsWith(s + "/", StringComparison.Ordinal)) return false;
            if (!copy && ParentKey(s) == into) return false;
        }
        return true;
    }

    /// <summary>
    /// Explorer items dropped on <paramref name="target"/>: moved into its folder or, with <paramref name="copy"/> (⌥ on macOS, Ctrl elsewhere),
    /// copied. A move asks first while <see cref="ConfirmDragAndDropSetting"/> is on (its "Don't Ask Again" answer turns the setting off); a name
    /// already taken in the folder asks to replace or skip. A move takes the rename path (language servers' will-rename edits, editors follow
    /// the files with their unsaved changes, did-rename), a copy tells the participants what was created. All file I/O runs on the file system's
    /// lane. Returns what was created in the folder (empty when refused, cancelled or nothing was left to do).
    /// </summary>
    public async Task<IReadOnlyList<Uri>> DropAsync(IReadOnlyList<Uri> sources, ExplorerNode? target, bool copy, bool confirm = true)
    {
        if (!CanDrop(sources, target, copy) || DropFolder(target) is not { } folder) return [];
        var items = Outermost(sources);
        if (!copy && confirm && !await ConfirmMoveAsync(items, folder).ConfigureAwait(true)) return [];
        return await TransferAsync([.. items.Select(u => (u, u.AbsoluteUri.EndsWith('/')))], folder, copy ? TransferKind.Copy : TransferKind.Move).ConfigureAwait(true);
    }

    /// <summary>Whether files from another application may be dropped on <paramref name="target"/>: there is a folder, and it is not one of them or
    /// inside one of them.</summary>
    public bool CanDropExternal(IReadOnlyList<Uri> files, ExplorerNode? target)
    {
        if (files.Count == 0 || DropFolder(target) is not { } folder) return false;
        var into = Key(folder.Uri);
        return !files.Any(f => Key(f) == into || into.StartsWith(Key(f) + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Files and folders dropped from another application (Finder, a file manager) on <paramref name="target"/>: copied into its folder, as VS Code
    /// does (never moved), with the same replace-or-skip question for a name already taken. A folder dropped into itself or below itself is
    /// refused. Returns what was created.
    /// </summary>
    public async Task<IReadOnlyList<Uri>> DropExternalAsync(IReadOnlyList<Uri> files, ExplorerNode? target)
    {
        if (!CanDropExternal(files, target) || DropFolder(target) is not { } folder) return [];
        var items = new List<(Uri, bool)>();
        foreach (var file in Outermost(files))
            if (await _fs.StatAsync(file).ConfigureAwait(true) is { } stat) items.Add((file, stat.Type.HasFlag(FileType.Directory))); // (gone meanwhile: left out)
        return await TransferAsync(items, folder, TransferKind.Copy).ConfigureAwait(true);
    }

    /// <summary>Whether <see cref="UndoAsync"/> has a drag and drop to undo (and <see cref="EnableUndoSetting"/> is on).</summary>
    public bool CanUndo => _lastTransfer is not null && _config?.GetRaw(EnableUndoSetting) is not { ValueKind: JsonValueKind.False };

    /// <summary>
    /// Undoes the last drag and drop: what it moved goes back where it was (the same rename path), what it copied goes to the Trash after a
    /// question. Something it replaced stays in the Trash. Only the last one: there is no redo, and the record goes with the Explorer view.
    /// </summary>
    public async Task<bool> UndoAsync()
    {
        if (!CanUndo || _lastTransfer is not { } last) return false;
        _lastTransfer = null;
        var failures = new List<string>();
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        if (last.Kind == TransferKind.Copy)
        {
            if (_dialogs is not null)
            {
                var answer = await _dialogs.ShowAsync("Undo Copy", $"Are you sure you want to undo the copy of {Names(last.Items.Select(i => i.To).ToList())}?\n\nThe copies are moved to the Trash.",
                    ["Undo", "Cancel"]).ConfigureAwait(true);
                if (answer != "Undo") { _lastTransfer = last; return false; }
            }
            var deleted = new List<Uri>();
            foreach (var (_, copy) in last.Items)
            {
                try
                {
                    await RemoveAsync(copy).ConfigureAwait(true);
                    deleted.Add(copy);
                    dirs.Add(ParentKey(Key(copy)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { failures.Add($"{NameOf(copy)}: {ex.Message}"); }
            }
            if (deleted.Count > 0 && _participants is not null) await _participants.DidDeleteAsync(deleted).ConfigureAwait(true);
        }
        else
        {
            foreach (var (from, to) in last.Items.Reverse())
            {
                try
                {
                    await MoveUriAsync(to, from).ConfigureAwait(true);
                    dirs.Add(ParentKey(Key(to)));
                    dirs.Add(ParentKey(Key(from)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { failures.Add($"{NameOf(to)}: {ex.Message}"); }
            }
        }
        await ReloadAsync(dirs).ConfigureAwait(true);
        if (failures.Count > 0) await ReportAsync("Could not undo everything:\n\n" + string.Join("\n", failures)).ConfigureAwait(true);
        return true;
    }

    private async Task<IReadOnlyList<Uri>> TransferAsync(IReadOnlyList<(Uri Source, bool IsDirectory)> items, ExplorerNode folder, TransferKind kind)
    {
        // The folder is shown open, as VS Code does after a drop; reading it also gives a copy into its own folder the names to stay clear of.
        await ExpandAsync(folder).ConfigureAwait(true);
        var plan = new List<Transfer>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, isDirectory) in items)
        {
            var name = NameOf(source);
            var target = Child(folder.Uri, name, isDirectory);
            if (Key(target) == Key(source))
            {
                if (kind == TransferKind.Move) continue;
                target = Child(folder.Uri, UniqueName(folder, name, isDirectory, " copy"), isDirectory);
            }
            var taken = !planned.Add(Key(target)) || await _fs.StatAsync(target).ConfigureAwait(true) is not null;
            plan.Add(new Transfer(source, target, isDirectory, taken));
        }
        if (plan.Any(p => p.Taken))
        {
            switch (await AskReplaceAsync([.. plan.Where(p => p.Taken)], folder).ConfigureAwait(true))
            {
                case null: return [];
                case false: plan.RemoveAll(p => p.Taken); break;
            }
        }

        var done = new List<(Uri From, Uri To)>();
        var failures = new List<string>();
        foreach (var p in plan)
        {
            try
            {
                if (p.Taken)
                {
                    await RemoveAsync(p.Target).ConfigureAwait(true); // replaced: what was there goes to the Trash
                    if (_participants is not null) await _participants.DidDeleteAsync([p.Target]).ConfigureAwait(true);
                }
                if (kind == TransferKind.Move) await MoveUriAsync(p.Source, p.Target).ConfigureAwait(true);
                else await CopyAcrossAsync(p.Source, p.Target, p.IsDirectory).ConfigureAwait(true);
                done.Add((p.Source, p.Target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add($"{NameOf(p.Source)}: {ex.Message}");
            }
        }
        if (kind == TransferKind.Copy && done.Count > 0 && _participants is not null) await _participants.DidCreateAsync([.. done.Select(d => d.To)]).ConfigureAwait(true);

        // Only the folders that changed are read again: the target, and the ones the moved items left.
        var dirs = new HashSet<string>(StringComparer.Ordinal) { Key(folder.Uri) };
        if (kind == TransferKind.Move) foreach (var (from, _) in done) dirs.Add(ParentKey(Key(from)));
        await ReloadAsync(dirs).ConfigureAwait(true);
        if (done.Count > 0)
        {
            _lastTransfer = (kind, done);
            if (folder.Children?.FirstOrDefault(c => Key(c.Uri) == Key(done[0].To)) is { } first) Selected = first;
        }
        if (failures.Count > 0)
            await ReportAsync($"Could not {(kind == TransferKind.Move ? "move" : "copy")} everything into '{folder.Name}':\n\n" + string.Join("\n", failures)).ConfigureAwait(true);
        return [.. done.Select(d => d.To)];
    }

    private async Task<bool> ConfirmMoveAsync(IReadOnlyList<Uri> items, ExplorerNode folder)
    {
        if (_dialogs is null || _config?.GetRaw(ConfirmDragAndDropSetting) is { ValueKind: JsonValueKind.False }) return true;
        var message = items.Count == 1
            ? $"Are you sure you want to move '{NameOf(items[0])}' into '{folder.Name}'?"
            : $"Are you sure you want to move the following {items.Count} files and folders into '{folder.Name}'?\n\n{NameList(items)}";
        var answer = await _dialogs.ShowAsync("Move", message, [MoveAnswer, MoveAndStopAsking, "Cancel"]).ConfigureAwait(true);
        if (answer == MoveAndStopAsking && _config is not null) await _config.UpdateAsync(ConfirmDragAndDropSetting, false).ConfigureAwait(true);
        return answer is MoveAnswer or MoveAndStopAsking;
    }

    /// <summary>Replace (true), Skip (false) or Cancel (null) for the items whose name is taken in <paramref name="folder"/>. Without a dialog
    /// service nothing is replaced.</summary>
    private async Task<bool?> AskReplaceAsync(IReadOnlyList<Transfer> taken, ExplorerNode folder)
    {
        if (_dialogs is null) return false;
        var message = taken.Count == 1
            ? $"A file or folder with the name '{NameOf(taken[0].Target)}' already exists in '{folder.Name}'. Do you want to replace it?"
            : $"The following {taken.Count} files and folders already exist in '{folder.Name}':\n\n{NameList([.. taken.Select(t => t.Target)])}\n\nDo you want to replace them?";
        var answer = await _dialogs.ShowAsync("Replace", message + "\n\nWhat is replaced is moved to the Trash. Skip leaves it, and does not move or copy the one dropped.",
            [ReplaceAnswer, SkipAnswer, "Cancel"]).ConfigureAwait(true);
        return answer switch { ReplaceAnswer => true, SkipAnswer => false, _ => null };
    }

    /// <summary>Moves <paramref name="uri"/> to the Trash, closing its editors first (without asking: it is being replaced or undone).</summary>
    private async Task RemoveAsync(Uri uri)
    {
        if (_well is not null)
            foreach (var tab in _well.EditorTabs.Where(t => RelativeUnder(uri, t.Uri) is not null).ToArray())
                await _well.CloseAsync(tab, prompt: false).ConfigureAwait(true);
        await _fs.DeleteAsync(uri, recursive: true, useTrash: true).ConfigureAwait(true);
    }

    /// <summary>A copy, also from another file system (a local file dropped on a remote folder): there its bytes are read and written, folder by folder.</summary>
    private async Task CopyAcrossAsync(Uri source, Uri target, bool isDirectory)
    {
        if (string.Equals(source.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(source.Authority, target.Authority, StringComparison.OrdinalIgnoreCase))
        {
            await _fs.CopyAsync(source, target).ConfigureAwait(true);
            return;
        }
        if (!isDirectory)
        {
            await _fs.WriteFileAsync(target, await _fs.ReadFileAsync(source).ConfigureAwait(true), createParents: false).ConfigureAwait(true);
            return;
        }
        await _fs.CreateDirectoryAsync(target).ConfigureAwait(true);
        foreach (var entry in await _fs.ReadDirectoryAsync(source).ConfigureAwait(true))
        {
            var dir = entry.Type.HasFlag(FileType.Directory);
            await CopyAcrossAsync(Child(source, entry.Name, dir), Child(target, entry.Name, dir), dir).ConfigureAwait(true);
        }
    }

    private async Task ReloadAsync(IEnumerable<string> folderKeys)
    {
        foreach (var key in folderKeys)
            if (FindNode(new Uri(key + "/")) is { IsDirectory: true, Children: not null } node) await ReloadDirectoryAsync(node).ConfigureAwait(true);
    }

    /// <summary>The items not inside another one of them (a folder dragged with something in it carries it).</summary>
    private static List<Uri> Outermost(IReadOnlyList<Uri> uris) =>
        [.. uris.Where(u => !uris.Any(o => !ReferenceEquals(o, u) && Key(u).StartsWith(Key(o) + "/", StringComparison.Ordinal))).DistinctBy(Key)];

    private static string Key(Uri uri) => uri.AbsoluteUri.TrimEnd('/');
    private static string ParentKey(string key) => key[..Math.Max(0, key.LastIndexOf('/'))];

    private static string NameList(IReadOnlyList<Uri> uris) =>
        string.Join("\n", uris.Take(10).Select(NameOf)) + (uris.Count > 10 ? $"\n...and {uris.Count - 10} more" : "");

    private static string Names(IReadOnlyList<Uri> uris) => uris.Count == 1 ? $"'{NameOf(uris[0])}'" : $"{uris.Count} files and folders";

    private static string UniqueName(ExplorerNode container, string name, bool isDir, string suffix)
    {
        var existing = new HashSet<string>(container.Children?.Select(c => c.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem}{suffix}{(i > 1 ? " " + i : "")}{ext}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private async Task ReportAsync(string message)
    {
        if (_dialogs is not null) await _dialogs.ShowAsync("CsCode", message, ["OK"]).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ live refresh (watcher)

    private void OnWatcherChanges(ExplorerNode root, string rootKey, IReadOnlyList<FileChange> changes)
    {
        // Called on the watcher's thread: only record which directories changed, then flush once per debounce window. Changed(root) from the
        // root's own watch is the watcher's rescan marker (events were lost: an overflow, a reconnect): every loaded folder of that root is
        // re-read. (A nested root's Changed from the watch of the root around it is a change of that folder like any other.) An entry that
        // files.exclude hides changes nothing shown (a wanted folder's events include those).
        var hidden = ExcludeGlobs();
        lock (_pendingGate)
        {
            foreach (var c in changes)
            {
                var key = c.Uri.AbsoluteUri.TrimEnd('/');
                if (c.Kind == FileChangeKind.Changed && key == rootKey)
                {
                    _rescanRoots.Add(root);
                    continue;
                }
                var name = Uri.UnescapeDataString(key[(key.LastIndexOf('/') + 1)..]);
                if (name.Length > 0 && IsExcluded(name, hidden)) continue;
                var dir = new Uri(c.Uri, ".");
                _dirtyDirs.Add(dir);
                if (c.Uri.AbsoluteUri.EndsWith('/')) _dirtyDirs.Add(new Uri(c.Uri, ".."));
            }
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = Task.Delay(WatcherDebounceMs).ContinueWith(_ => _ui.Post(() => _ = FlushWatcherAsync()), TaskScheduler.Default);
    }

    /// <summary>Applies all pending watcher notifications: re-reads only the affected, already-loaded directories and splices differences.</summary>
    public async Task FlushWatcherAsync()
    {
        Uri[] dirs;
        ExplorerNode[] rescans;
        lock (_pendingGate)
        {
            dirs = _dirtyDirs.ToArray();
            _dirtyDirs.Clear();
            rescans = _rescanRoots.ToArray();
            _rescanRoots.Clear();
            _flushScheduled = false;
        }
        foreach (var root in rescans)
            if (_roots.Contains(root)) await ReloadLoadedAsync(root).ConfigureAwait(true); // (a root of folders since closed: nothing)
        foreach (var dir in dirs)
        {
            var node = FindNode(dir);
            if (node is { IsDirectory: true, Children: not null }) await ReloadDirectoryAsync(node).ConfigureAwait(true);
        }
    }

    private ExplorerNode? FindNode(Uri dir)
    {
        var key = dir.AbsoluteUri.TrimEnd('/');
        foreach (var root in _roots)
        {
            if (root.Uri.AbsoluteUri.TrimEnd('/').Equals(key, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (var n in root.Descendants())
                if (n.IsDirectory && n.Uri.AbsoluteUri.TrimEnd('/').Equals(key, StringComparison.OrdinalIgnoreCase)) return n;
        }
        return null;
    }

    private async Task ReloadLoadedAsync(ExplorerNode node)
    {
        if (!node.IsDirectory || node.Children is null) return;
        await ReloadDirectoryAsync(node).ConfigureAwait(true);
        foreach (var c in node.Children.ToArray()) await ReloadLoadedAsync(c).ConfigureAwait(true);
    }

    /// <summary>Re-reads one directory and applies the difference by name (adds/removes only, so expanded subtrees keep their state).</summary>
    public async Task ReloadDirectoryAsync(ExplorerNode dir)
    {
        if (!dir.IsDirectory) return;
        List<ExplorerNode> fresh;
        try { fresh = await LoadChildrenAsync(dir).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { fresh = []; }

        var old = dir.Children ?? [];
        var oldByName = old.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var merged = new List<ExplorerNode>(fresh.Count);
        var changed = old.Count != fresh.Count;
        var replaced = new List<ExplorerNode>();
        foreach (var f in fresh)
        {
            if (oldByName.Remove(f.Name, out var existing))
            {
                if (existing.IsDirectory == f.IsDirectory) { merged.Add(existing); continue; }
                replaced.Add(existing); // a folder now a file, or the reverse: the old node goes like one that left
            }
            merged.Add(f);
            changed = true;
        }
        if (!changed) return;
        // Folders that left the listing (deleted, renamed, replaced — by a file too) are no longer asked for, nor anything below them: a folder
        // coming back at the same path is a new node, collapsed.
        var unwanted = false;
        foreach (var gone in oldByName.Values.Concat(replaced)) if (gone.IsDirectory) unwanted |= Unwant(gone.Uri);
        if (unwanted && RootOf(dir) is { } root) _ = ApplyWantsAsync(root.Uri);
        merged.Sort(CompareNodes);
        dir.Children = merged;
        if (!dir.IsExpanded) return;
        var index = Rows.IndexOf(dir);
        if (index < 0) return;
        var oldVisible = old.Count == 0 ? 0 : dir.IsExpanded ? CountVisible(old) : 0;
        var newVisible = dir.VisibleDescendants().ToList();
        Rows.RemoveRange(index + 1, oldVisible);
        Rows.InsertRange(index + 1, newVisible);
    }

    private static int CountVisible(List<ExplorerNode> children)
    {
        var n = 0;
        foreach (var c in children) { n++; n += c.VisibleDescendants().Count(); }
        return n;
    }

    public void Dispose()
    {
        _filterCts?.Cancel();
        foreach (var w in _watches) w.Dispose();
        _watches.Clear();
        _wants.Clear();
        _wanted.Clear();
        _subs.Dispose();
        Rows.Clear();
        _roots.Clear();
    }
}
