using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.App.Docking;
using CsCode.App.Notifications;
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
/// arrive batched from <see cref="IFileWatcher"/> and are diffed against the loaded subtree by name: the tree is never rebuilt.
/// All file operations go through <see cref="IFileSystem"/> (async; delete uses the trash). UI-thread object.
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
    private readonly DisposableStore _subs = new();
    private readonly List<IDisposable> _watches = new();
    private readonly List<ExplorerNode> _roots = new();
    private readonly HashSet<Uri> _expandedUris = new();
    private readonly object _pendingGate = new();
    private readonly HashSet<Uri> _dirtyDirs = new();
    private bool _flushScheduled;
    private ExplorerNode? _selected;
    private string _filter = "";
    private (List<Uri> Uris, bool Cut)? _clipboard;
    private int _loadGeneration;
    private bool _restoring;

    public ExplorerViewModel(IWorkspaceService workspace, IUiDispatcher ui, INavigationService? navigation = null, IConfigurationService? config = null,
        IStateStorage? state = null, IModalDialogs? dialogs = null, DocumentWell? well = null)
    {
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

    /// <summary>The most recently started root refresh (completes when roots and restored expansion are loaded).</summary>
    public Task RefreshCompleted => _refresh;

    public BulkObservableCollection<ExplorerNode> Rows { get; } = new();
    public bool HasFolder => _roots.Count > 0;
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
        _roots.Clear();
        Rows.Clear();

        var restored = _state?.Get<List<string>>(ExpandedStateKey, StorageScope.Workspace) ?? [];
        foreach (var s in restored) if (Uri.TryCreate(s, UriKind.Absolute, out var u)) _expandedUris.Add(u);

        foreach (var folder in _workspace.Folders)
        {
            var name = Path.GetFileName(folder.LocalPath.TrimEnd('/', '\\'));
            var root = new ExplorerNode(folder, name.Length > 0 ? name : folder.LocalPath, true, 0, null, isRoot: true);
            _roots.Add(root);
            _watches.Add(_workspace.Watcher.Watch(folder, true, ExcludeGlobs(), OnWatcherChanges));
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

    private IReadOnlyList<string> ExcludeGlobs()
    {
        if (_config?.GetRaw(SettingKeys.FilesExclude) is not { ValueKind: JsonValueKind.Object } obj) return DefaultExcludes;
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

    /// <summary>Renames/moves on disk; open tabs inside the moved path are closed first (with their save prompts) and reopened at the new location.</summary>
    private async Task MoveUriAsync(Uri from, Uri to)
    {
        var reopen = new List<(Uri Old, Uri New)>();
        if (_well is not null)
            foreach (var tab in _well.EditorTabs.ToArray())
            {
                var rel = RelativeUnder(from, tab.Uri);
                if (rel is null) continue;
                if (!await _well.CloseAsync(tab).ConfigureAwait(true)) return; // user cancelled a save prompt: abort the move
                reopen.Add((tab.Uri, new Uri(to.AbsoluteUri.TrimEnd('/') + (rel.Length > 0 ? "/" + rel : ""))));
            }
        await _fs.RenameAsync(from, to).ConfigureAwait(true);
        if (_navigation is not null) foreach (var (_, n) in reopen) await _navigation.OpenAsync(n).ConfigureAwait(true);
    }

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
        var container = node.Parent!;
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        return true;
    }

    public async Task<Uri?> DuplicateAsync(ExplorerNode node)
    {
        var container = node.Parent ?? ContainerOf(node);
        var target = UniqueName(container, node.Name, node.IsDirectory, " copy");
        await _fs.CopyAsync(node.Uri, Child(container.Uri, target, node.IsDirectory)).ConfigureAwait(true);
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        return Child(container.Uri, target, node.IsDirectory);
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
            else await _fs.CopyAsync(src, Child(container.Uri, UniqueName(container, name, isDir, " copy"), isDir)).ConfigureAwait(true);
        }
        if (clip.Cut) _clipboard = null;
        await ReloadDirectoryAsync(container).ConfigureAwait(true);
        if (clip.Cut) foreach (var root in _roots) await ReloadLoadedAsync(root).ConfigureAwait(true);
    }

    /// <summary>Drag-and-drop move of <paramref name="sources"/> into a folder.</summary>
    public async Task MoveAsync(IEnumerable<ExplorerNode> sources, ExplorerNode targetFolder)
    {
        var container = ContainerOf(targetFolder);
        foreach (var n in sources.ToArray())
        {
            if (n.IsRoot || n == container || IsDescendantOf(container, n)) continue; // cannot move a folder into itself
            if (container.Children?.Any(c => string.Equals(c.Name, n.Name, StringComparison.OrdinalIgnoreCase)) == true) continue;
            await MoveUriAsync(n.Uri, Child(container.Uri, n.Name, n.IsDirectory)).ConfigureAwait(true);
        }
        foreach (var root in _roots) await ReloadLoadedAsync(root).ConfigureAwait(true);
    }

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

    private void OnWatcherChanges(IReadOnlyList<FileChange> changes)
    {
        // Called on the watcher's thread: only record which directories changed, then flush once per debounce window.
        lock (_pendingGate)
        {
            foreach (var c in changes)
            {
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
        lock (_pendingGate)
        {
            dirs = _dirtyDirs.ToArray();
            _dirtyDirs.Clear();
            _flushScheduled = false;
        }
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
        foreach (var f in fresh)
        {
            if (oldByName.TryGetValue(f.Name, out var existing) && existing.IsDirectory == f.IsDirectory) merged.Add(existing);
            else { merged.Add(f); changed = true; }
        }
        if (!changed) return;
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
        _subs.Dispose();
        Rows.Clear();
        _roots.Clear();
    }
}
