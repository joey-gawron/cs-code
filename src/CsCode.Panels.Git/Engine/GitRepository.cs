using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Git.Engine;

/// <summary>A local branch or a remote-tracking branch (<see cref="IsRemote"/>: <c>origin/main</c>).</summary>
public sealed record GitBranch(string Name, string ShortOid, bool IsRemote, bool IsCurrent, string? Upstream);

/// <summary>
/// One git repository: its status (refreshed by the file watcher with a debounce, on window focus and after every operation, at most one
/// <c>git status</c> at a time and never more than one queued), the content of revisions, and the operations of the Source Control window. All
/// methods are asynchronous and run git on the <see cref="GitRunner"/> lane; mutating operations of one repository run one at a time.
/// </summary>
public sealed class GitRepository : IDisposable
{
    /// <summary>Quiet period after the last file change before the status is read again (VS Code waits about as long).</summary>
    public static TimeSpan RefreshDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    private static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private readonly GitRunner _git;
    private readonly DisposableStore _store = new();
    private readonly Emitter<GitStatusSnapshot> _statusChanged = new();
    private readonly Emitter<object?> _historyChanged = new();
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private GitStatusSnapshot _status = GitStatusSnapshot.Empty;
    private Task? _refreshing;
    private bool _refreshQueued, _historyDirty, _disposed;
    private string? _lastOid;

    /// <param name="root">The working tree root, spelled as the workspace spells it (not git's realpath).</param>
    /// <param name="gitDir">The repository's git directory (absolute), which may be outside <paramref name="root"/> for a linked worktree.</param>
    public GitRepository(string root, string gitDir, GitRunner git, IFileWatcher? watcher)
    {
        Root = Path.TrimEndingDirectorySeparator(root);
        GitDir = gitDir;
        _git = git;
        RootUri = new Uri(Root.EndsWith('/') ? Root : Root + "/");
        _debounce = new Timer(_ => _ = RefreshAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _store.Add(_debounce);
        if (watcher is not null)
        {
            _store.Add(watcher.Watch(new Uri(Root), recursive: true, excludeGlobs: null, OnFileChanges));
            if (!IsUnder(gitDir, Root)) _store.Add(watcher.Watch(new Uri(gitDir), recursive: false, excludeGlobs: null, OnFileChanges));
        }
    }

    public string Root { get; }
    public Uri RootUri { get; }
    public string GitDir { get; }
    public string Name => Path.GetFileName(Root);
    public GitRunner Runner => _git;

    public GitStatusSnapshot Status => Volatile.Read(ref _status);

    /// <summary>Fires (on the git lane or a timer thread) after each status read that changed something.</summary>
    public IEventSource<GitStatusSnapshot> StatusChanged => _statusChanged;

    /// <summary>Fires when the index or HEAD moved (a stage, a commit, a checkout): the originals of the gutter markers and blame are stale.</summary>
    public IEventSource<object?> HistoryChanged => _historyChanged;

    // ------------------------------------------------------------------------------------------- paths

    /// <summary>The repository-relative path (forward slashes) of <paramref name="uri"/>, or null when it is not inside this working tree.</summary>
    public string? RelativePath(Uri uri)
    {
        if (!uri.IsFile) return null;
        var path = uri.LocalPath;
        if (!IsUnder(path, Root)) return null;
        var rel = path.Length == Root.Length ? "" : path[(Root.Length + 1)..];
        return rel.Replace('\\', '/');
    }

    public string FullPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public Uri ToUri(string relativePath) => new(FullPath(relativePath.TrimEnd('/')));

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it (case-insensitively on macOS and Windows).</summary>
    public static bool IsUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Length == root.Length) return string.Equals(path, root, PathComparison);
        return path.Length > root.Length && path.StartsWith(root, PathComparison) && (path[root.Length] == '/' || path[root.Length] == Path.DirectorySeparatorChar);
    }

    // ------------------------------------------------------------------------------------------- status

    private void OnFileChanges(IReadOnlyList<FileChange> changes)
    {
        var relevant = false;
        foreach (var c in changes)
        {
            if (!c.Uri.IsFile) continue;
            var p = c.Uri.LocalPath;
            if (IsUnder(p, GitDir))
            {
                var rel = p.Length > GitDir.Length ? p[(GitDir.Length + 1)..].Replace('\\', '/') : "";
                if (rel.EndsWith(".lock", StringComparison.Ordinal) || rel.StartsWith("objects/", StringComparison.Ordinal) || rel.StartsWith("logs/", StringComparison.Ordinal)
                    || rel is "FETCH_HEAD" or "fsmonitor--daemon" || rel.StartsWith("fsmonitor", StringComparison.Ordinal)) continue;
                lock (_gate) _historyDirty |= rel is "index" or "HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "packed-refs" || rel.StartsWith("refs/", StringComparison.Ordinal);
                relevant = true;
                continue;
            }
            relevant = true;
        }
        if (relevant) ScheduleRefresh();
    }

    /// <summary>Reads the status again after <see cref="RefreshDebounce"/> of quiet (each call restarts the wait).</summary>
    public void ScheduleRefresh()
    {
        if (_disposed) return;
        try { _debounce.Change(RefreshDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Reads the status now (or, when a read is running, once more after it). Completes when a read that started after this call has finished.</summary>
    public Task RefreshAsync(bool historyChanged = false)
    {
        lock (_gate)
        {
            _historyDirty |= historyChanged;
            if (_disposed) return Task.CompletedTask;
            if (_refreshing is { IsCompleted: false } running)
            {
                _refreshQueued = true;
                return running.ContinueWith(_ => RefreshAsync(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            }
            return _refreshing = RefreshLoopAsync();
        }
    }

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            await RefreshOnceAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (!_refreshQueued || _disposed) return;
                _refreshQueued = false;
            }
        }
    }

    private async Task RefreshOnceAsync()
    {
        GitStatusSnapshot next;
        try
        {
            var r = await _git.RunAsync(Root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--ignored=matching"], throwOnError: false,
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!r.Ok) return; // a transient lock or a repository being rewritten: the next change refreshes again
            next = PorcelainV2.Parse(r.Output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { return; }
        bool history;
        lock (_gate)
        {
            history = _historyDirty || (_lastOid is not null && next.Branch.Oid != _lastOid);
            _historyDirty = false;
            _lastOid = next.Branch.Oid;
        }
        var previous = Interlocked.Exchange(ref _status, next);
        if (!Same(previous, next)) _statusChanged.Fire(next);
        if (history) _historyChanged.Fire(null);
    }

    private static bool Same(GitStatusSnapshot a, GitStatusSnapshot b) => a.Branch == b.Branch && a.Entries.SequenceEqual(b.Entries);

    // ------------------------------------------------------------------------------------------- content

    /// <summary>
    /// The text of <paramref name="relativePath"/> at <paramref name="revision"/> (<c>""</c> = the index, <c>HEAD</c>, a commit); null when the file
    /// is not there (new file, initial repository) or is binary (a NUL in its first 8 KB).
    /// </summary>
    public async Task<string?> ShowAsync(string revision, string relativePath, CancellationToken ct = default)
    {
        // cat-file --filters: the blob as checkout would write it (core.autocrlf, .gitattributes eol), so it compares line by line with the file
        var r = await _git.RunAsync(Root, ["cat-file", "--filters", $"{revision}:{relativePath}"], throwOnError: false, ct: ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        return Decode(r.Output);
    }

    /// <summary>UTF-8 (BOM stripped), or null for binary content.</summary>
    public static string? Decode(byte[] bytes)
    {
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0)) return null;
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    // ------------------------------------------------------------------------------------------- operations

    private async Task<T> MutateAsync<T>(Func<Task<T>> work, bool historyChanged = true)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally
        {
            _mutations.Release();
            await RefreshAsync(historyChanged).ConfigureAwait(false);
        }
    }

    private Task MutateAsync(Func<Task> work, bool historyChanged = true) => MutateAsync(async () => { await work().ConfigureAwait(false); return true; }, historyChanged);

    private static List<string> WithPaths(IEnumerable<string> head, IEnumerable<string> paths) => [.. head, "--", .. paths];

    public Task StageAsync(IReadOnlyCollection<string> paths) => paths.Count == 0 ? Task.CompletedTask
        : MutateAsync(() => _git.RunAsync(Root, WithPaths(["add", "-A"], paths)));

    public Task StageAllAsync() => MutateAsync(() => _git.RunAsync(Root, ["add", "-A"]));

    public Task UnstageAsync(IReadOnlyCollection<string> paths) => paths.Count == 0 ? Task.CompletedTask : MutateAsync(async () =>
    {
        if (Status.Branch.IsInitial) await _git.RunAsync(Root, WithPaths(["rm", "--cached", "-r", "-q"], paths)).ConfigureAwait(false);
        else await _git.RunAsync(Root, WithPaths(["reset", "-q", "HEAD"], paths)).ConfigureAwait(false);
    });

    public Task UnstageAllAsync() => MutateAsync(async () =>
    {
        if (Status.Branch.IsInitial) await _git.RunAsync(Root, ["rm", "--cached", "-r", "-q", "."]).ConfigureAwait(false);
        else await _git.RunAsync(Root, ["reset", "-q"]).ConfigureAwait(false);
    });

    /// <summary>
    /// Throws away working-tree changes: tracked files are restored from the index (<c>git checkout -- path</c>), untracked files are deleted
    /// (<c>git clean -f</c>). The caller confirms first.
    /// </summary>
    public Task DiscardAsync(IReadOnlyCollection<GitStatusEntry> entries) => entries.Count == 0 ? Task.CompletedTask : MutateAsync(async () =>
    {
        var untracked = entries.Where(e => e.IsUntracked).Select(e => e.Path).ToList();
        var tracked = entries.Where(e => !e.IsUntracked && !e.IsIgnored).Select(e => e.Path).ToList();
        // a file added to the index and changed again: discarding its working-tree changes restores the staged version, like VS Code
        if (tracked.Count > 0) await _git.RunAsync(Root, WithPaths(["checkout", "-q"], tracked)).ConfigureAwait(false);
        if (untracked.Count > 0) await _git.RunAsync(Root, WithPaths(["clean", "-f", "-q"], untracked)).ConfigureAwait(false);
    }, historyChanged: false);

    /// <summary>Commits the index (<paramref name="all"/>: stages everything first, untracked files included). The message goes through standard input.</summary>
    public Task CommitAsync(string message, bool amend = false, bool all = false) => MutateAsync(async () =>
    {
        if (all) await _git.RunAsync(Root, ["add", "-A"]).ConfigureAwait(false);
        var args = new List<string> { "commit", "--quiet" };
        if (amend) args.Add("--amend");
        if (string.IsNullOrWhiteSpace(message))
        {
            if (!amend) throw new InvalidOperationException("Please enter a commit message.");
            args.Add("--no-edit");
            await _git.RunAsync(Root, args).ConfigureAwait(false);
            return;
        }
        args.AddRange(["--cleanup=strip", "-F", "-"]);
        await _git.RunAsync(Root, args, Encoding.UTF8.GetBytes(message)).ConfigureAwait(false);
    });

    /// <summary>The last commit's message (for Amend: the box is filled with it).</summary>
    public async Task<string?> LastCommitMessageAsync()
    {
        var r = await _git.RunAsync(Root, ["log", "-1", "--format=%B"], throwOnError: false).ConfigureAwait(false);
        return r.Ok ? r.Text.TrimEnd() : null;
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync()
    {
        var r = await _git.RunAsync(Root, ["for-each-ref", "--format=%(HEAD)%00%(refname)%00%(objectname:short)%00%(upstream:short)", "refs/heads", "refs/remotes"]).ConfigureAwait(false);
        var list = new List<GitBranch>();
        foreach (var line in r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\0');
            if (f.Length < 4) continue;
            var refName = f[1];
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var remote = refName.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = remote ? refName["refs/remotes/".Length..] : refName["refs/heads/".Length..];
            list.Add(new GitBranch(name, f[2], remote, f[0] == "*", f[3].Length > 0 ? f[3] : null));
        }
        return list;
    }

    /// <summary>Checks out a local branch, or a remote-tracking one as a new local branch tracking it (<c>origin/x</c> → <c>x</c>).</summary>
    public Task CheckoutAsync(GitBranch branch) => MutateAsync(async () =>
    {
        if (!branch.IsRemote) { await _git.RunAsync(Root, ["checkout", "-q", branch.Name]).ConfigureAwait(false); return; }
        var local = branch.Name[(branch.Name.IndexOf('/') + 1)..];
        await _git.RunAsync(Root, ["checkout", "-q", "--track", "-b", local, branch.Name]).ConfigureAwait(false);
    });

    public Task CreateBranchAsync(string name, bool checkout = true) => MutateAsync(async () =>
    {
        if (checkout) await _git.RunAsync(Root, ["checkout", "-q", "-b", name]).ConfigureAwait(false);
        else await _git.RunAsync(Root, ["branch", name]).ConfigureAwait(false);
    });

    /// <summary>True when git accepts <paramref name="name"/> as a branch name (<c>git check-ref-format --branch</c>).</summary>
    public async Task<bool> IsValidBranchNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var r = await _git.RunAsync(Root, ["check-ref-format", "--branch", name], throwOnError: false).ConfigureAwait(false);
        return r.Ok;
    }

    public Task FetchAsync(CancellationToken ct = default) =>
        MutateAsync(() => _git.RunAsync(Root, ["fetch", "--prune"], timeout: Timeout.InfiniteTimeSpan, ct: ct));

    public Task PullAsync(CancellationToken ct = default) =>
        MutateAsync(() => _git.RunAsync(Root, ["pull", "--no-edit"], timeout: Timeout.InfiniteTimeSpan, ct: ct));

    /// <summary>Pushes the current branch; a branch without an upstream is published to <paramref name="remote"/> (default <c>origin</c>) and tracks it.</summary>
    public Task PushAsync(CancellationToken ct = default, string? remote = null) => MutateAsync(async () =>
    {
        var branch = Status.Branch;
        if (branch.Upstream is null && branch.Head is { } head)
            await _git.RunAsync(Root, ["push", "-u", remote ?? "origin", head], timeout: Timeout.InfiniteTimeSpan, ct: ct).ConfigureAwait(false);
        else await _git.RunAsync(Root, ["push"], timeout: Timeout.InfiniteTimeSpan, ct: ct).ConfigureAwait(false);
    });

    public async Task<IReadOnlyList<string>> RemotesAsync()
    {
        var r = await _git.RunAsync(Root, ["remote"], throwOnError: false).ConfigureAwait(false);
        return r.Ok ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    /// <summary>
    /// <c>git blame --porcelain</c> of a file, or of one line (<paramref name="line"/>, 1-based). With <paramref name="contents"/> blame runs against
    /// that text instead of the file on disk (the editor's unsaved buffer).
    /// </summary>
    public async Task<IReadOnlyList<BlameLine>> BlameAsync(string relativePath, int? line = null, string? contents = null, CancellationToken ct = default)
    {
        var args = new List<string> { "blame", "--porcelain" };
        if (line is { } l) { args.Add("-L"); args.Add($"{l},{l}"); }
        if (contents is not null) { args.Add("--contents"); args.Add("-"); }
        args.Add("--");
        args.Add(relativePath);
        var r = await _git.RunAsync(Root, args, contents is null ? null : Encoding.UTF8.GetBytes(contents), throwOnError: false, ct: ct).ConfigureAwait(false);
        return r.Ok ? BlameParser.Parse(r.Text) : [];
    }

    /// <summary>
    /// Stages the changes of <paramref name="workingText"/> (the editor's text of the file) that touch its lines <paramref name="startLine"/>..
    /// <paramref name="endLine"/>: the index version with those changes applied is hashed and written to the index. False when nothing was picked.
    /// </summary>
    public Task<bool> StageRangeAsync(string relativePath, string workingText, int startLine, int endLine) => MutateAsync(async () =>
    {
        var index = await ShowAsync("", relativePath).ConfigureAwait(false) ?? "";
        var next = TextSplice.Apply(index, workingText, c => TextSplice.TouchesModifiedLines(c, startLine, endLine));
        if (next is null) return false;
        await WriteIndexAsync(relativePath, next).ConfigureAwait(false);
        return true;
    });

    /// <summary>Unstages the staged changes touching the index lines <paramref name="startLine"/>..<paramref name="endLine"/> (the index gets HEAD's lines back there).</summary>
    public Task<bool> UnstageRangeAsync(string relativePath, int startLine, int endLine) => MutateAsync(async () =>
    {
        var index = await ShowAsync("", relativePath).ConfigureAwait(false) ?? "";
        var head = await ShowAsync("HEAD", relativePath).ConfigureAwait(false) ?? "";
        var next = TextSplice.Apply(index, head, c => TextSplice.TouchesOriginalLines(c, startLine, endLine));
        if (next is null) return false;
        await WriteIndexAsync(relativePath, next).ConfigureAwait(false);
        return true;
    });

    private async Task WriteIndexAsync(string relativePath, string text)
    {
        var mode = Status.Entries.FirstOrDefault(e => e.Path == relativePath)?.IndexMode is { Length: 6 } m && m != "000000" ? m : "100644";
        var hash = await _git.RunAsync(Root, ["hash-object", "-w", "--stdin", "--path", relativePath], Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
        var sha = hash.Text.Trim();
        await _git.RunAsync(Root, ["update-index", "--add", "--cacheinfo", $"{mode},{sha},{relativePath}"]).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _store.Dispose();
        _statusChanged.Clear();
        _historyChanged.Clear();
    }
}
