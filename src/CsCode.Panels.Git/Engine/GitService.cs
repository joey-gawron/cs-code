using System.Text;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;

namespace CsCode.Panels.Git.Engine;

/// <summary>
/// The workspace's git repositories and what the rest of the app reads from them: Explorer decorations, the baseline of the editor's change
/// markers (the index), inline blame. Repositories are found for each local workspace folder (the repository containing it, and repositories
/// directly inside it); folders on a remote host get none and <see cref="Unavailable"/> says why. Everything here is thread-safe; events fire on
/// background threads.
/// </summary>
public sealed class GitService : IFileDecorationProvider, IQuickDiffProvider, ILineBlameProvider, IDisposable
{
    public const string EnabledSetting = "git.enabled";
    public const string PathSetting = "git.path";
    public const string AutoRefreshSetting = "git.autorefresh";
    public const string DecorationsSetting = "git.decorations.enabled";
    public const string BlameSetting = "git.blame.editorDecoration.enabled";

    /// <summary>The message shown for a remote (ssh, container) workspace folder.</summary>
    public const string RemoteMessage = "Source control is not available for remote folders yet: git runs on this machine only. Use git in the remote terminal.";

    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly IWorkspaceService _workspace;
    private readonly IConfigurationService? _config;
    private readonly IDocumentManager? _documents;
    private readonly GitRunner _runner;
    private readonly DisposableStore _store = new();
    private readonly Emitter<object?> _repositoriesChanged = new();
    private readonly Emitter<IReadOnlyList<Uri>?> _decorationsChanged = new();
    private readonly Emitter<Uri?> _originalChanged = new();
    private readonly Emitter<object?> _blameChanged = new();
    private readonly object _gate = new();
    private readonly Dictionary<GitRepository, IDisposable> _repoSubscriptions = new();
    private readonly LinkedList<(string Path, Task<QuickDiffOriginal?> Text)> _originals = new();
    private GitRepository[] _repos = [];
    private Decorations _decorations = Decorations.Empty;
    private Task _discovery = Task.CompletedTask;
    private int _discoveryGeneration;
    private bool _disposed;

    public GitService(IWorkspaceService workspace, IConfigurationService? config = null, IMemoryMonitor? memory = null, IDocumentManager? documents = null, GitRunner? runner = null)
    {
        _workspace = workspace;
        _config = config;
        _documents = documents;
        _runner = runner ?? new GitRunner(() => _config?.Get<string?>(PathSetting, null), memory);
        if (config is not null) _store.Add(config.RegisterProperties(GitSettings.Properties()));
        _store.Add(workspace.FoldersChanged.Subscribe(_ => Discover()));
        if (config is not null)
            _store.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(EnabledSetting) || e.Affects(PathSetting)) Discover();
                if (e.Affects(DecorationsSetting)) RebuildDecorations();
                if (e.Affects(BlameSetting)) _blameChanged.Fire(null);
            }));
        Discover();
    }

    public GitRunner Runner => _runner;

    public IReadOnlyList<GitRepository> Repositories => Volatile.Read(ref _repos);

    /// <summary>Fires when repositories were found or dropped.</summary>
    public IEventSource<object?> RepositoriesChanged => _repositoriesChanged;

    /// <summary>Why there is no source control (git missing, disabled, remote folder); null when every local folder could be looked at.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Completes when the current discovery (workspace folders → repositories) has finished.</summary>
    public Task Initialization => Volatile.Read(ref _discovery);

    public bool IsEnabled => _config?.Get(EnabledSetting, true) ?? true;

    /// <summary>The innermost repository whose working tree contains <paramref name="uri"/>.</summary>
    public GitRepository? RepositoryFor(Uri uri)
    {
        GitRepository? best = null;
        foreach (var r in Repositories)
            if (r.RelativePath(uri) is not null && (best is null || r.Root.Length > best.Root.Length)) best = r;
        return best;
    }

    public Task RefreshAllAsync() => Task.WhenAll(Repositories.Select(r => r.RefreshAsync(historyChanged: true)));

    // ------------------------------------------------------------------------------------- discovery

    private void Discover()
    {
        var generation = Interlocked.Increment(ref _discoveryGeneration);
        Volatile.Write(ref _discovery, DiscoverAsync(generation));
    }

    private async Task DiscoverAsync(int generation)
    {
        var found = new List<(string Root, string GitDir)>();
        string? unavailable = null;
        if (!IsEnabled) unavailable = "Git is disabled (setting git.enabled).";
        else
        {
            var folders = _workspace.Folders;
            if (folders.Any(f => !f.IsFile)) unavailable = RemoteMessage;
            var local = folders.Where(f => f.IsFile).ToList();
            if (local.Count > 0 && await _runner.ResolveAsync().ConfigureAwait(false) is null) unavailable = _runner.Unavailable;
            else
            {
                foreach (var folder in local)
                {
                    var path = Path.TrimEndingDirectorySeparator(folder.LocalPath);
                    if (await ProbeAsync(path).ConfigureAwait(false) is { } repo) Add(repo);
                    // repositories directly inside the folder (a folder of checkouts)
                    IReadOnlyList<DirectoryEntry> children;
                    try { children = await _workspace.FileSystem.ReadDirectoryAsync(folder).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    foreach (var child in children.Where(c => c.Type == FileType.Directory && c.Name != ".git").Take(64))
                    {
                        var childPath = Path.Combine(path, child.Name);
                        FileStat? stat;
                        try { stat = await _workspace.FileSystem.StatAsync(new Uri(Path.Combine(childPath, ".git"))).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                        if (stat is null) continue;
                        if (await ProbeAsync(childPath).ConfigureAwait(false) is { } nested) Add(nested);
                    }
                }
            }
        }
        void Add((string Root, string GitDir) repo)
        {
            if (!found.Any(f => PathComparer.Equals(f.Root, repo.Root))) found.Add(repo);
        }
        if (generation != Volatile.Read(ref _discoveryGeneration)) return;
        ApplyRepositories(found, unavailable);
        await Task.WhenAll(Repositories.Select(r => r.RefreshAsync(historyChanged: true))).ConfigureAwait(false);
    }

    /// <summary>
    /// The working tree containing <paramref name="folder"/>, spelled from the folder's own path (git answers with the realpath: <c>/private/var</c>
    /// for <c>/var</c> on macOS), and its git directory; null when the folder is not in a repository.
    /// </summary>
    private async Task<(string Root, string GitDir)?> ProbeAsync(string folder)
    {
        GitResult r;
        try { r = await _runner.RunAsync(folder, ["rev-parse", "--show-prefix", "--absolute-git-dir"], throwOnError: false, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return null; }
        if (!r.Ok) return null;
        var lines = r.Text.Split('\n');
        if (lines.Length < 2) return null;
        var prefix = lines[0].Trim().TrimEnd('/');
        var gitDir = lines[1].Trim();
        var root = folder;
        if (prefix.Length > 0)
        {
            var depth = prefix.Split('/').Length;
            for (var i = 0; i < depth && root is not null; i++) root = Path.GetDirectoryName(root);
            if (root is null) return null;
        }
        // git's realpath of the git dir under the root: re-spell it from the root when it is the usual <root>/.git
        if (!GitRepository.IsUnder(gitDir, root) && gitDir.EndsWith("/.git", StringComparison.Ordinal)) gitDir = Path.Combine(root, ".git");
        return (root, gitDir);
    }

    private void ApplyRepositories(List<(string Root, string GitDir)> found, string? unavailable)
    {
        List<GitRepository> removed;
        lock (_gate)
        {
            if (_disposed) return;
            var keep = new List<GitRepository>();
            removed = [];
            foreach (var r in _repos)
                if (found.Any(f => PathComparer.Equals(f.Root, r.Root))) keep.Add(r); else removed.Add(r);
            foreach (var f in found.Where(f => !keep.Any(r => PathComparer.Equals(r.Root, f.Root))))
            {
                var repo = new GitRepository(f.Root, f.GitDir, _runner, IsAutoRefresh ? _workspace.Watcher : null);
                _repoSubscriptions[repo] = Disposable.Combine(
                    repo.StatusChanged.Subscribe(_ => RebuildDecorations()),
                    repo.HistoryChanged.Subscribe(_ => OnHistoryChanged(repo)));
                keep.Add(repo);
            }
            _repos = [.. keep.OrderBy(r => r.Root, PathComparer)];
            foreach (var r in removed) if (_repoSubscriptions.Remove(r, out var sub)) sub.Dispose();
            Unavailable = unavailable;
        }
        foreach (var r in removed) r.Dispose();
        RebuildDecorations();
        lock (_gate) _originals.Clear();
        _originalChanged.Fire(null);
        _repositoriesChanged.Fire(null);
    }

    private bool IsAutoRefresh => _config?.Get(AutoRefreshSetting, true) ?? true;

    private void OnHistoryChanged(GitRepository repo)
    {
        lock (_gate)
        {
            for (var n = _originals.First; n is not null;)
            {
                var next = n.Next;
                if (GitRepository.IsUnder(n.Value.Path, repo.Root)) _originals.Remove(n);
                n = next;
            }
        }
        _originalChanged.Fire(null);
        _blameChanged.Fire(null);
    }

    // ------------------------------------------------------------------------------------- decorations

    /// <summary>A snapshot of every repository's decorations, rebuilt (off the UI thread) after each status change.</summary>
    private sealed class Decorations(Dictionary<string, FileDecoration> files, Dictionary<string, FileDecoration> folders, HashSet<string> ignored, string[] roots)
    {
        public static readonly Decorations Empty = new(new(PathComparer), new(PathComparer), new(PathComparer), []);
        public readonly Dictionary<string, FileDecoration> Files = files;
        public readonly Dictionary<string, FileDecoration> Folders = folders;
        public readonly HashSet<string> Ignored = ignored;
        public readonly string[] Roots = roots;
    }

    public IEventSource<IReadOnlyList<Uri>?> DecorationsChanged => _decorationsChanged;

    public FileDecoration? GetDecoration(Uri uri, bool isDirectory)
    {
        if (!uri.IsFile) return null;
        var d = Volatile.Read(ref _decorations);
        if (d.Roots.Length == 0) return null;
        var path = Path.TrimEndingDirectorySeparator(uri.LocalPath);
        if ((isDirectory ? d.Folders : d.Files).TryGetValue(path, out var deco)) return deco;
        if (!isDirectory && d.Folders.ContainsKey(path)) return null;
        // ignored: the path or one of its folders (git lists an ignored folder once, not its contents)
        if (d.Ignored.Count > 0)
            for (var p = path; p is not null && p.Length > 1; p = Path.GetDirectoryName(p))
            {
                if (d.Ignored.Contains(p)) return IgnoredDecoration;
                if (d.Roots.Any(r => PathComparer.Equals(r, p))) break;
            }
        return null;
    }

    private static readonly FileDecoration IgnoredDecoration = new("", "gitDecoration.ignoredResourceForeground", "Ignored") { Faded = true };

    /// <summary>The Explorer decoration of one status entry, VS Code's letters and colours (the working tree wins over the index).</summary>
    public static FileDecoration? DecorationFor(GitStatusEntry e)
    {
        if (e.IsIgnored) return IgnoredDecoration;
        if (e.IsConflict) return new FileDecoration("!", "gitDecoration.conflictingResourceForeground", "Conflict");
        if (e.IsUntracked) return new FileDecoration("U", "gitDecoration.untrackedResourceForeground", "Untracked");
        var code = e.WorkTree != '.' ? e.WorkTree : e.Index;
        var staged = e.WorkTree == '.';
        return code switch
        {
            'M' or 'T' => new FileDecoration("M", "gitDecoration.modifiedResourceForeground", staged ? "Index Modified" : "Modified"),
            'A' => new FileDecoration("A", "gitDecoration.addedResourceForeground", "Index Added"),
            'D' => new FileDecoration("D", "gitDecoration.deletedResourceForeground", staged ? "Index Deleted" : "Deleted") { Strikethrough = true },
            'R' => new FileDecoration("R", "gitDecoration.untrackedResourceForeground", "Index Renamed"),
            'C' => new FileDecoration("C", "gitDecoration.addedResourceForeground", "Index Copied"),
            _ => null,
        };
    }

    private static int Rank(FileDecoration d) => d.ColorKey switch
    {
        "gitDecoration.conflictingResourceForeground" => 5,
        "gitDecoration.deletedResourceForeground" => 4,
        "gitDecoration.modifiedResourceForeground" => 3,
        "gitDecoration.addedResourceForeground" => 2,
        "gitDecoration.untrackedResourceForeground" => 1,
        _ => 0,
    };

    private void RebuildDecorations()
    {
        var enabled = _config?.Get(DecorationsSetting, true) ?? true;
        var files = new Dictionary<string, FileDecoration>(PathComparer);
        var folders = new Dictionary<string, FileDecoration>(PathComparer);
        var ignored = new HashSet<string>(PathComparer);
        var repos = Repositories;
        if (enabled)
            foreach (var repo in repos)
                foreach (var e in repo.Status.Entries)
                {
                    var full = Path.TrimEndingDirectorySeparator(repo.FullPath(e.Path));
                    if (e.IsIgnored) { ignored.Add(full); continue; }
                    if (DecorationFor(e) is not { } deco) continue;
                    files[full] = deco;
                    // folders show the colour of their most important change, with a dot
                    for (var dir = Path.GetDirectoryName(full); dir is not null && GitRepository.IsUnder(dir, repo.Root) && dir.Length > repo.Root.Length; dir = Path.GetDirectoryName(dir))
                    {
                        if (folders.TryGetValue(dir, out var existing) && Rank(existing) >= Rank(deco)) break;
                        folders[dir] = new FileDecoration("●", deco.ColorKey, "Contains changes");
                    }
                }
        Volatile.Write(ref _decorations, new Decorations(files, folders, ignored, enabled ? repos.Select(r => r.Root).ToArray() : []));
        _decorationsChanged.Fire(null);
    }

    // ------------------------------------------------------------------------------------- quick diff

    public IEventSource<Uri?> OriginalChanged => _originalChanged;

    public ValueTask<QuickDiffOriginal?> GetOriginalAsync(Uri uri, CancellationToken ct = default)
    {
        if (RepositoryFor(uri) is not { } repo || repo.RelativePath(uri) is not { Length: > 0 } rel) return ValueTask.FromResult<QuickDiffOriginal?>(null);
        var entry = repo.Status.Entries.FirstOrDefault(e => string.Equals(e.Path, rel, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        if (entry is { IsUntracked: true } or { IsIgnored: true }) return ValueTask.FromResult<QuickDiffOriginal?>(null);
        if (IgnoredByFolder(repo, rel)) return ValueTask.FromResult<QuickDiffOriginal?>(null);
        var path = uri.LocalPath;
        Task<QuickDiffOriginal?> task;
        lock (_gate)
        {
            var hit = _originals.FirstOrDefault(o => PathComparer.Equals(o.Path, path));
            if (hit.Text is not null && !hit.Text.IsFaulted && !hit.Text.IsCanceled)
            {
                _originals.Remove(hit);
                _originals.AddFirst(hit);
                task = hit.Text;
            }
            else
            {
                task = LoadOriginalAsync(repo, rel);
                _originals.AddFirst((path, task));
                while (_originals.Count > 64) _originals.RemoveLast(); // bounded: the open documents' originals
            }
        }
        return new ValueTask<QuickDiffOriginal?>(task.WaitAsync(ct));
    }

    private static bool IgnoredByFolder(GitRepository repo, string rel)
    {
        foreach (var e in repo.Status.Entries)
            if (e.IsIgnored && (e.Path == rel || (e.Path.EndsWith('/') && rel.StartsWith(e.Path, StringComparison.Ordinal)))) return true;
        return false;
    }

    private static async Task<QuickDiffOriginal?> LoadOriginalAsync(GitRepository repo, string rel)
    {
        try
        {
            var text = await repo.ShowAsync("", rel).ConfigureAwait(false);
            return text is null ? null : new QuickDiffOriginal(text, "Index");
        }
        catch (Exception ex) when (ex is InvalidOperationException or GitException or ObjectDisposedException) { return null; }
    }

    public async ValueTask<bool> StageRangeAsync(Uri uri, int startLine, int endLine, CancellationToken ct = default)
    {
        if (RepositoryFor(uri) is not { } repo || repo.RelativePath(uri) is not { Length: > 0 } rel) return false;
        // the open document's text as it would be saved (its encoding), else the file's bytes as they are
        if (_documents?.Get(uri) is { } doc)
            return await repo.StageRangeAsync(rel, doc.GetText(), startLine, endLine, GitRepository.EncodingFor((doc as IEditorDocument)?.EncodingName)).ConfigureAwait(false);
        var bytes = await _workspace.FileSystem.ReadFileAsync(uri, ct).ConfigureAwait(false);
        return await repo.StageRangeAsync(rel, bytes, startLine, endLine).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------- blame

    bool ILineBlameProvider.IsEnabled => _config?.Get(BlameSetting, false) ?? false;

    IEventSource<object?> ILineBlameProvider.Changed => _blameChanged;

    public async ValueTask<LineBlame?> GetLineBlameAsync(Uri uri, int line, string? documentText, CancellationToken ct = default)
    {
        if (RepositoryFor(uri) is not { } repo || repo.RelativePath(uri) is not { Length: > 0 } rel) return null;
        if (repo.Status.Entries.Any(e => e.Path == rel && (e.IsUntracked || e.IsIgnored))) return null;
        IReadOnlyList<BlameLine> lines;
        try { lines = await repo.BlameAsync(rel, line + 1, documentText, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return null; }
        return lines.Count == 0 ? null : Format(lines[0], DateTimeOffset.Now);
    }

    /// <summary>GitLens' current-line annotation: <c>Jane Doe, 3 weeks ago • Fix the parser</c>; "You, Uncommitted changes" for a line not committed yet.</summary>
    public static LineBlame Format(BlameLine b, DateTimeOffset now)
    {
        if (b.IsUncommitted) return new LineBlame("You, Uncommitted changes", "Not committed yet");
        var when = BlameParser.RelativeTime(b.AuthorTime, now);
        var summary = b.Summary.Length > 80 ? b.Summary[..79] + "…" : b.Summary;
        var tooltip = new StringBuilder().Append(b.ShortSha).Append("  ").Append(b.Author);
        if (b.AuthorMail.Length > 0) tooltip.Append(" <").Append(b.AuthorMail).Append('>');
        tooltip.Append('\n').Append(b.AuthorTime.ToString("yyyy-MM-dd HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture)).Append(" (").Append(when).Append(")\n\n").Append(b.Summary);
        return new LineBlame($"{b.Author}, {when} • {summary}", tooltip.ToString());
    }

    public void Dispose()
    {
        GitRepository[] repos;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            repos = _repos;
            _repos = [];
            foreach (var s in _repoSubscriptions.Values) s.Dispose();
            _repoSubscriptions.Clear();
            _originals.Clear();
        }
        _store.Dispose();
        foreach (var r in repos) r.Dispose();
        _runner.Dispose();
        _repositoriesChanged.Clear();
        _decorationsChanged.Clear();
        _originalChanged.Clear();
        _blameChanged.Clear();
    }
}
