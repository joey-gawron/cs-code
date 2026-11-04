using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Git.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Git;

// Merge conflicts in the Source Control window (docs/SOURCE-CONTROL.md, "Merge conflicts"): staging a conflicted file marks it resolved, and a
// file that still has conflict markers asks first, as VS Code's git extension does.
public sealed partial class GitHost
{
    /// <summary>The editor's Accept All commands (VS Code's ids); with file URIs as arguments they resolve those files.</summary>
    public const string AcceptAllCurrent = "merge-conflict.accept.all-current";
    public const string AcceptAllIncoming = "merge-conflict.accept.all-incoming";

    /// <summary>
    /// Stages <paramref name="paths"/> (<c>git add</c>, which also marks a conflicted file resolved). Conflicted files whose text still has
    /// conflict markers are staged only after "Are you sure you want to stage ... with merge conflicts?" is answered Yes.
    /// </summary>
    public async Task StageAsync(GitRepository repo, IReadOnlyCollection<string> paths)
    {
        if (!await ConfirmStageConflictsAsync(repo, paths).ConfigureAwait(false)) return;
        await repo.StageAsync(paths).ConfigureAwait(false);
    }

    /// <summary>Stage All Changes, asking first when a conflicted file still has conflict markers.</summary>
    public async Task StageAllAsync(GitRepository repo)
    {
        if (!await ConfirmStageConflictsAsync(repo, [.. repo.Status.Conflicts.Select(e => e.Path)]).ConfigureAwait(false)) return;
        await repo.StageAllAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// VS Code's check before staging: the conflicted files among <paramref name="paths"/> are read from disk (what <c>git add</c> stages) and
    /// those that still have a marker line (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; </c>, <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; </c>) make it
    /// ask. True when staging may go on.
    /// </summary>
    public async Task<bool> ConfirmStageConflictsAsync(GitRepository repo, IReadOnlyCollection<string> paths)
    {
        var conflicted = repo.Status.Conflicts.Select(e => e.Path).Where(paths.Contains).ToList();
        if (conflicted.Count == 0) return true;
        var unresolved = new List<string>();
        foreach (var path in conflicted)
            if (await HasConflictMarkersAsync(repo, path).ConfigureAwait(false)) unresolved.Add(path);
        if (unresolved.Count == 0 || Windows is null) return true;
        var message = unresolved.Count == 1
            ? $"Are you sure you want to stage {Path.GetFileName(unresolved[0])} with merge conflicts?"
            : $"Are you sure you want to stage {unresolved.Count} files with merge conflicts?";
        var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning, message, [new MessageAction("Yes"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(false);
        return answer == "Yes";
    }

    private async Task<bool> HasConflictMarkersAsync(GitRepository repo, string path)
    {
        if (_sp.GetService<IWorkspaceService>()?.FileSystem is not { } fs) return false;
        try
        {
            var text = GitRepository.Decode(await fs.ReadFileAsync(repo.ToUri(path)).ConfigureAwait(false));
            return text is not null && MergeConflictParser.HasConflictMarkers(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // deleted on one side: nothing to check
    }
}
