using System.Globalization;
using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Timeline;

namespace CsCode.Panels.Git.Engine;

/// <summary>One commit of a file's history: its path in that commit, and its path before it when the commit renamed the file.</summary>
public sealed record GitFileCommit(string Sha, IReadOnlyList<string> Parents, string Author, string Email, DateTimeOffset Committed, string Subject, string Path, string? OldPath);

/// <summary>Parses <c>git log --follow --name-status -z</c> with <see cref="GitTimelineProvider.LogFormat"/>.</summary>
public static class GitLogParser
{
    /// <summary>
    /// Commits newest first. <paramref name="currentPath"/> is the file's path now: a commit without a status line (a merge) keeps the path of
    /// the commit after it, and a rename moves the path of every older commit to the old name.
    /// </summary>
    public static IReadOnlyList<GitFileCommit> Parse(byte[] output, string currentPath)
    {
        var text = Encoding.UTF8.GetString(output);
        var commits = new List<GitFileCommit>();
        var path = currentPath;
        foreach (var record in text.Split('\x1e'))
        {
            var end = record.IndexOf('\0');
            var header = (end < 0 ? record : record[..end]).Split('\x1f');
            if (header.Length < 6 || header[0].Length < 7) continue;
            var parents = header[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var committed = long.TryParse(header[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UnixEpoch;
            string? oldPath = null;
            var thisPath = path;
            if (end >= 0)
            {
                var tokens = record[(end + 1)..].TrimStart('\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && tokens[0].Length > 0)
                {
                    var status = tokens[0][0];
                    if ((status is 'R' or 'C') && tokens.Length >= 3)
                    {
                        oldPath = tokens[1];
                        thisPath = tokens[2];
                    }
                    else thisPath = tokens[1];
                }
            }
            commits.Add(new GitFileCommit(header[0], parents, header[2], header[3], committed, header[5], thisPath, oldPath));
            path = oldPath ?? thisPath; // older commits know the file by its old name
        }
        return commits;
    }
}

/// <summary>What a git item of the Timeline carries (its <see cref="TimelineItem.Data"/>).</summary>
public sealed record GitTimelineData(string Root, GitFileCommit Commit);

/// <summary>
/// Git History in the Timeline (VS Code's git timeline): the commits that changed a file, following renames (<c>git log --follow</c>), paged
/// (<see cref="TimelinePage.Cursor"/> is how many commits came before). git's <c>--skip</c> is wrong together with <c>--follow</c> once the
/// skipped commits pass a rename, so each page asks for the commits up to its end and leaves out the ones already shown; a page is cancellable
/// (the process is killed). Runs on the git lane. Items change when a repository's history moves (commit, checkout).
/// </summary>
public sealed class GitTimelineProvider : ITimelineProvider, IDisposable
{
    public const string ProviderId = "git-history";
    public const string LogFormat = "--format=%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%ct%x1f%s";
    /// <summary>How far back the Timeline pages (each page reads the log from the start).</summary>
    public const int MaxCommits = 5000;

    private readonly GitService _git;
    private readonly Emitter<Uri?> _changed = new();
    private readonly DisposableStore _store = new();
    private readonly object _gate = new();
    private DisposableStore? _repoSubs;

    public GitTimelineProvider(GitService git)
    {
        _git = git;
        _store.Add(git.RepositoriesChanged.Subscribe(_ => Resubscribe()));
        Resubscribe();
    }

    public string Id => ProviderId;
    public string Label => "Git History";
    public IEventSource<Uri?> Changed => _changed;

    private void Resubscribe()
    {
        var subs = new DisposableStore();
        foreach (var repo in _git.Repositories) subs.Add(repo.HistoryChanged.Subscribe(_ => _changed.Fire(null)));
        DisposableStore? old;
        lock (_gate) { old = _repoSubs; _repoSubs = subs; }
        old?.Dispose();
        _changed.Fire(null);
    }

    public bool Handles(Uri resource) => resource.IsFile && _git.IsEnabled && _git.RepositoryFor(resource) is not null;

    public async ValueTask<TimelinePage> GetTimelineAsync(Uri resource, string? cursor, int limit, CancellationToken ct = default)
    {
        if (_git.RepositoryFor(resource) is not { } repo || repo.RelativePath(resource) is not { Length: > 0 } path) return TimelinePage.Empty;
        var skip = int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? Math.Max(0, s) : 0;
        limit = Math.Clamp(limit, 1, 500);
        if (skip >= MaxCommits) return TimelinePage.Empty;
        var r = await repo.Runner.RunAsync(repo.Root, ["log", "--follow", LogFormat, "--name-status", "-z", $"-n{skip + limit + 1}", "--", path],
            throwOnError: false, ct: ct).ConfigureAwait(false);
        if (!r.Ok) return TimelinePage.Empty; // no commits yet, not tracked
        var commits = GitLogParser.Parse(r.Output, path);
        var items = commits.Skip(skip).Take(limit).Select(c => ToItem(repo.Root, resource, c)).ToArray();
        var next = commits.Count > skip + limit && skip + limit < MaxCommits ? (skip + limit).ToString(CultureInfo.InvariantCulture) : null;
        return new TimelinePage(items, next);
    }

    private static TimelineItem ToItem(string root, Uri resource, GitFileCommit c) => new(ProviderId, c.Sha, resource, c.Committed, c.Subject)
    {
        Description = c.Author,
        Tooltip = $"{c.Author} <{c.Email}>, {c.Committed.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)}\n\n{c.Subject}\n\n{c.Sha[..Math.Min(8, c.Sha.Length)]}"
                  + (c.OldPath is { } old ? $" (renamed from {old})" : ""),
        IconKey = "source-control",
        Data = new GitTimelineData(root, c),
    };

    private GitRepository? RepositoryOf(GitTimelineData data) => _git.Repositories.FirstOrDefault(r => string.Equals(r.Root, data.Root, StringComparison.Ordinal));

    public async ValueTask<TimelineContent?> GetContentAsync(TimelineItem item, CancellationToken ct = default)
    {
        if (item.Data is not GitTimelineData data || RepositoryOf(data) is not { } repo) return null;
        var text = await repo.ShowAsync(data.Commit.Sha, data.Commit.Path, ct).ConfigureAwait(false);
        return text is null ? null : new TimelineContent(ContentLabel(data.Commit.Path, data.Commit.Sha), text);
    }

    public async ValueTask<TimelineContent?> GetPreviousContentAsync(TimelineItem item, CancellationToken ct = default)
    {
        if (item.Data is not GitTimelineData data || RepositoryOf(data) is not { } repo || data.Commit.Parents.Count == 0) return null;
        var parent = data.Commit.Parents[0];
        var path = data.Commit.OldPath ?? data.Commit.Path;
        // a commit that added the file: its parent has none, compared as empty
        var text = await repo.ShowAsync(parent, path, ct).ConfigureAwait(false);
        return new TimelineContent(ContentLabel(path, data.Commit.Sha + "^"), text ?? "");
    }

    private static string ContentLabel(string path, string revision)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var hat = revision.EndsWith('^') ? "^" : "";
        return $"{name} ({revision.TrimEnd('^')[..Math.Min(8, revision.TrimEnd('^').Length)]}{hat})";
    }

    public void Dispose()
    {
        _store.Dispose();
        DisposableStore? subs;
        lock (_gate) { subs = _repoSubs; _repoSubs = null; }
        subs?.Dispose();
        _changed.Clear();
    }
}
