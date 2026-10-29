using System.Runtime.CompilerServices;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Workspace;

/// <summary>
/// The open folder(s), recents, and "find files".
/// <list type="bullet">
/// <item>Folder changes are serialised. Before <see cref="FoldersChanged"/> fires, workspace-dependent services (workspace settings, workspace-scoped state)
/// are told, on the IO lane, so subscribers already see the new data. Opening a folder never blocks the caller.</item>
/// <item><see cref="RecentFolders"/> is persisted (application state), most recent first, de-duplicated, capped at <see cref="MaxRecents"/>.</item>
/// <item><see cref="FindFilesAsync"/> streams file URIs from a parallel, cancellable walk on the IO lane (see <see cref="FileWalker"/>): honours <c>files.exclude</c>
/// and <c>.gitignore</c> (nested files, negations, <c>.git/info/exclude</c>), never enters VCS directories, is symlink-loop safe, and its memory is
/// proportional to tree depth, not to the number of files. <c>search.useIgnoreFiles</c> / <c>search.followSymlinks</c> control the last two.</item>
/// </list>
/// Only <c>file:</c> folders are walked here; folders of other schemes (remote) are searched by the <see cref="IFileSearchProvider"/> registered
/// for their scheme, next to their files.
/// </summary>
public sealed class WorkspaceService : IWorkspaceService, IDisposable
{
    public const int MaxRecents = 30;
    private const string RecentsKey = "workspace.recentFolders";

    private readonly IFileSystem _fs;
    private readonly IFileWatcher _watcher;
    private readonly IStateStorage _state;
    private readonly IConfigurationService _config;
    private readonly IoLane _lane;
    private readonly IReadOnlyList<IWorkspaceBound> _bound;
    private readonly PlatformOptions _options;
    private readonly ILogger _log;
    private readonly Lazy<IReadOnlyList<IFileSearchProvider>> _searchProviders;
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private readonly object _recentsGate = new();
    private Uri[] _folders = [];
    private Uri[] _recents;

    public WorkspaceService(IFileSystem fileSystem, IFileWatcher watcher, IStateStorage state, IConfigurationService configuration, IoLane lane,
        IEnumerable<IWorkspaceBound>? bound = null, PlatformOptions? options = null, ILogger<WorkspaceService>? logger = null,
        Func<IEnumerable<IFileSearchProvider>>? searchProviders = null)
    {
        // Resolved on the first search of a non-file folder (a remote provider depends on services that depend on this one).
        _searchProviders = new(() => searchProviders?.Invoke().ToArray() ?? [], LazyThreadSafetyMode.ExecutionAndPublication);
        _fs = fileSystem;
        _watcher = watcher;
        _state = state;
        _config = configuration;
        _lane = lane;
        _bound = bound?.ToArray() ?? [];
        _options = options ?? new PlatformOptions();
        _log = logger ?? NullLogger<WorkspaceService>.Instance;
        _recents = LoadRecents();
    }

    public IReadOnlyList<Uri> Folders => Volatile.Read(ref _folders);

    /// <summary>The first folder (the one workspace settings and state belong to), or null.</summary>
    public Uri? PrimaryFolder { get { var f = Volatile.Read(ref _folders); return f.Length > 0 ? f[0] : null; } }

    public string? Name
    {
        get
        {
            var folders = Folders;
            if (folders.Count == 0) return null;
            return string.Join(", ", folders.Select(FolderName));
        }
    }

    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public IFileSystem FileSystem => _fs;
    public IFileWatcher Watcher => _watcher;
    public IReadOnlyList<Uri> RecentFolders { get { lock (_recentsGate) return _recents; } }

    // ---- folders -----------------------------------------------------------------------------------------------------------------

    /// <summary>Makes <paramref name="folder"/> the (only) workspace folder.</summary>
    public async ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default)
    {
        var normalized = await ValidateAsync(folder, ct).ConfigureAwait(false);
        await ChangeAsync([normalized], ct).ConfigureAwait(false);
        NoteRecent(normalized);
    }

    /// <summary>Adds a further root (multi-root workspace). The first folder stays the primary one.</summary>
    public async ValueTask AddFolderAsync(Uri folder, CancellationToken ct = default)
    {
        var normalized = await ValidateAsync(folder, ct).ConfigureAwait(false);
        await _changeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Folders;
            if (current.Contains(normalized)) return;
            await ApplyAsync([.. current, normalized], ct).ConfigureAwait(false);
        }
        finally { _changeGate.Release(); }
        NoteRecent(normalized);
    }

    public async ValueTask RemoveFolderAsync(Uri folder, CancellationToken ct = default)
    {
        var target = Normalize(folder);
        await _changeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Folders;
            if (!current.Contains(target)) return;
            await ApplyAsync(current.Where(f => f != target).ToArray(), ct).ConfigureAwait(false);
        }
        finally { _changeGate.Release(); }
    }

    public ValueTask CloseFolderAsync(CancellationToken ct = default) => new(ChangeAsync([], ct).AsTask());

    private async ValueTask ChangeAsync(Uri[] next, CancellationToken ct)
    {
        await _changeGate.WaitAsync(ct).ConfigureAwait(false);
        try { await ApplyAsync(next, ct).ConfigureAwait(false); }
        finally { _changeGate.Release(); }
    }

    private async ValueTask ApplyAsync(Uri[] next, CancellationToken ct)
    {
        var previous = Folders;
        if (previous.SequenceEqual(next)) return;

        // Dependents first (settings, state), then publish: a FoldersChanged subscriber must already see the new workspace's values.
        foreach (var b in _bound)
        {
            try { await b.OnWorkspaceFoldersChangedAsync(next, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One dependent failing (an unreadable settings file, a full disk) must not leave the workspace half-switched or unopenable.
                _log.LogError(ex, "{Service} failed to follow the workspace change; continuing", b.GetType().Name);
            }
        }
        Volatile.Write(ref _folders, next);

        var added = next.Except(previous).ToArray();
        var removed = previous.Except(next).ToArray();
        _changed.Fire(new WorkspaceFoldersChangedEventArgs(added, removed));
    }

    private async ValueTask<Uri> ValidateAsync(Uri folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var normalized = Normalize(folder);
        // Stat the URI as the caller meant it (no trailing slash for local paths): a FILE must be reported as "not a folder", not "missing".
        var statUri = folder.IsFile ? FileUri.FromPath(TrimSeparators(FileUri.ToPath(folder))) : folder;
        var stat = await _fs.StatAsync(statUri, ct).ConfigureAwait(false);
        if (stat is null) throw new DirectoryNotFoundException($"Folder does not exist: {folder}");
        if ((stat.Type & FileType.Directory) == 0) throw new IOException($"Not a folder: {folder}");
        return normalized;
    }

    /// <summary>Canonical form for equality: <c>file:</c> folders become directory URIs (trailing slash, consistently escaped).</summary>
    internal static Uri Normalize(Uri folder)
    {
        if (!folder.IsAbsoluteUri) throw new ArgumentException("A folder URI must be absolute", nameof(folder));
        return folder.IsFile ? FileUri.FromDirectoryPath(FileUri.ToPath(folder)) : folder;
    }

    private static string TrimSeparators(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;

    private static string FolderName(Uri folder)
    {
        var path = Uri.UnescapeDataString(folder.AbsolutePath).TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.Length > 0 ? name : folder.Host.Length > 0 ? folder.Host : path.Length > 0 ? path : "/";
    }

    // ---- recents -----------------------------------------------------------------------------------------------------------------

    /// <summary>Records a folder or file as recently opened (most recent first, de-duplicated, capped at 30) and persists the list.</summary>
    public void NoteRecent(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var normalized = uri.IsFile && uri.AbsolutePath.EndsWith('/') ? Normalize(uri) : uri;
        string[] snapshot;
        lock (_recentsGate)
        {
            _recents = [normalized, .. _recents.Where(r => !SameUri(r, normalized))];
            if (_recents.Length > MaxRecents) _recents = _recents[..MaxRecents];
            snapshot = _recents.Select(r => r.AbsoluteUri).ToArray();
        }
        _state.Set(RecentsKey, snapshot);
    }

    public void ClearRecents()
    {
        lock (_recentsGate) _recents = [];
        _state.Remove(RecentsKey);
    }

    private static bool SameUri(Uri a, Uri b) =>
        string.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    private Uri[] LoadRecents()
    {
        var stored = _state.Get<string[]>(RecentsKey);
        if (stored is null) return [];
        var list = new List<Uri>();
        foreach (var s in stored)
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || list.Any(x => SameUri(x, u))) continue;
            list.Add(u);
            if (list.Count == MaxRecents) break;
        }
        return [.. list];
    }

    // ---- find files --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Streams the files under every workspace folder that match <paramref name="includeGlob"/> (null = all; matched against the folder-relative
    /// path with VS Code glob rules), at most <paramref name="maxResults"/> (≤ 0 = unlimited). Order is unspecified (the walk is parallel).
    /// Cancelling stops the walk promptly; abandoning the enumeration does too.
    /// </summary>
    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var folders = Folders;
        var remaining = maxResults <= 0 ? int.MaxValue : maxResults;
        if (folders.Any(f => f.IsFile))
        {
            await foreach (var uri in FindLocalFilesAsync(includeGlob, remaining, ct).ConfigureAwait(false))
            {
                yield return uri;
                if (--remaining == 0) yield break;
            }
        }

        var others = folders.Where(f => !f.IsFile).ToArray();
        if (others.Length == 0) yield break;
        var options = new FileSearchOptions(
            string.IsNullOrWhiteSpace(includeGlob) ? null : includeGlob,
            (_config.Get<Dictionary<string, bool>>(SettingKeys.FilesExclude) ?? []).Where(kv => kv.Value).Select(kv => kv.Key).ToArray(),
            _config.Get("search.useIgnoreFiles", true),
            _config.Get("search.followSymlinks", true),
            remaining == int.MaxValue ? 0 : remaining);
        foreach (var folder in others)
        {
            var provider = _searchProviders.Value.FirstOrDefault(p => string.Equals(p.Scheme, folder.Scheme, StringComparison.OrdinalIgnoreCase));
            if (provider is null) continue; // nobody serves this scheme: nothing to find
            await foreach (var uri in provider.FindFilesAsync(folder, options with { MaxResults = remaining == int.MaxValue ? 0 : remaining }, ct).ConfigureAwait(false))
            {
                yield return uri;
                if (--remaining == 0) yield break;
            }
        }
    }

    private async IAsyncEnumerable<Uri> FindLocalFilesAsync(string? includeGlob, int maxResults, [EnumeratorCancellation] CancellationToken ct)
    {
        var roots = Folders.Where(f => f.IsFile).Select(FileUri.ToPath).ToArray();
        if (roots.Length == 0) yield break;

        var exclude = (_config.Get<Dictionary<string, bool>>(SettingKeys.FilesExclude) ?? [])
            .Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
        var options = new WalkOptions(
            string.IsNullOrWhiteSpace(includeGlob) ? null : includeGlob,
            exclude,
            _config.Get("search.useIgnoreFiles", true),
            _config.Get("search.followSymlinks", true),
            Degree: Math.Clamp(_options.IoThreads - 1, 1, 3), // leave a lane thread for everyone else
            MaxResults: maxResults <= 0 ? int.MaxValue : maxResults);

        using var walker = new FileWalker(_lane, roots, options, ct);
        var reader = walker.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var uri))
            {
                ct.ThrowIfCancellationRequested(); // surface cancellation at once even while buffered results remain
                yield return uri;
            }
            walker.Resume(); // room again: wake a parked walk
        }
        if (walker.Fault is { } fault) throw new IOException("Searching the workspace failed", fault);
    }

    public void Dispose()
    {
        _changed.Clear();
        _changeGate.Dispose();
    }
}
