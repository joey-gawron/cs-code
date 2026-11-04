using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Panels.Git.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Git;

// Merge conflicts in the Source Control window (docs/SOURCE-CONTROL.md, "Merge conflicts"): staging a conflicted file marks it resolved, and a
// file that still has conflict markers asks first, as VS Code's git extension does; "Resolve in Merge Editor" opens the 3-way merge editor
// (IMergeEditorService) over the three index stages of the file.
public sealed partial class GitHost
{
    /// <summary>Resolve in Merge Editor (VS Code's git extension's id): the file (argument: its URI; none: the active file) in the 3-way merge editor.</summary>
    public const string OpenMergeEditor = "git.openMergeEditor";

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

    // ------------------------------------------------------------------------------------- the merge editor

    /// <summary>The shell's merge editor, when there is one.</summary>
    public IMergeEditorService? MergeEditors => _sp.GetService<IMergeEditorService>();

    private void RegisterMergeCommands(ICommandService commands)
    {
        if (commands.Has(OpenMergeEditor)) return;
        _store.Add(commands.Register(OpenMergeEditor, async inv =>
        {
            await OnFiles(inv, async (r, paths) => { foreach (var p in paths.Take(1)) await OpenMergeEditorAsync(r, p).ConfigureAwait(false); }).ConfigureAwait(false);
            return null;
        }, new CommandMetadata(OpenMergeEditor, "Resolve in Merge Editor", "Git")));
        // the lens above a conflicted file's first conflict block (after the editor's own Accept / Compare lenses on that line)
        if (_sp.GetService<ILanguageFeatureRegistry>() is { } features)
            _store.Add(features.Register<ICodeLensProvider>(DocumentSelector.All, new MergeEditorLens(this), "git.mergeEditor"));
    }

    /// <summary>
    /// Opens the merge editor on a conflicted file: base, current and incoming are the index stages 1, 2 and 3 (<c>cat-file --filters :N:path</c>,
    /// as checkout would write them; a missing stage, as for a file added on both sides, is empty), named after the file's conflict markers
    /// (<c>HEAD</c>, the branch merged in). Complete Merge stages the file.
    /// </summary>
    public async Task OpenMergeEditorAsync(GitRepository repo, string path)
    {
        var name = Path.GetFileName(path);
        if (MergeEditors is not { } service) { await Info("The merge editor is not available.").ConfigureAwait(false); return; }
        if (repo.Status.Entries.FirstOrDefault(e => e.Path == path) is not { IsConflict: true })
        {
            await Info($"'{name}' has no merge conflicts to resolve.").ConfigureAwait(false);
            return;
        }
        string? current = null, incoming = null, @base = null;
        if (!await Guard(async () =>
        {
            @base = await repo.ShowAsync(":1", path).ConfigureAwait(false);
            current = await repo.ShowAsync(":2", path).ConfigureAwait(false);
            incoming = await repo.ShowAsync(":3", path).ConfigureAwait(false);
        }).ConfigureAwait(false)) return;
        if (current is null || incoming is null)
        {
            await Info($"'{name}' was deleted on one side (or is binary): keep or delete it, then stage it.").ConfigureAwait(false);
            return;
        }
        var uri = repo.ToUri(path);
        var (currentLabel, incomingLabel) = await MarkerNamesAsync(uri).ConfigureAwait(false);
        var request = new MergeEditorRequest(uri, @base ?? "", current, incoming, $"Merging: {name}")
        {
            CurrentLabel = currentLabel ?? repo.Status.Branch.DisplayName,
            IncomingLabel = incomingLabel,
            Complete = _ => repo.StageAsync([path]),
        };
        await Guard(() => service.OpenMergeEditorAsync(request).AsTask()).ConfigureAwait(false);
    }

    /// <summary>The names on the file's first conflict markers (<c>HEAD</c>, <c>topic</c>), from the open document or the disk.</summary>
    private async Task<(string? Current, string? Incoming)> MarkerNamesAsync(Uri uri)
    {
        var text = _sp.GetService<IDocumentManager>()?.Get(uri)?.GetText();
        if (text is null && _sp.GetService<IWorkspaceService>()?.FileSystem is { } fs)
        {
            try { text = GitRepository.Decode(await fs.ReadFileAsync(uri).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (text is null || MergeConflictParser.Parse(text) is not [var first, ..]) return (null, null);
        return (first.Current.Name.Length > 0 ? first.Current.Name : null, first.Incoming.Name.Length > 0 ? first.Incoming.Name : null);
    }

    /// <summary>
    /// "Resolve in Merge Editor" above the first conflict block of a file the repository lists as conflicted. Asked off the UI thread like any
    /// lens provider; answers only for conflicted files (a status lookup first), from the document's text.
    /// </summary>
    private sealed class MergeEditorLens(GitHost host) : ICodeLensProvider
    {
        public ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct)
        {
            var uri = document.Uri;
            if (!uri.IsFile || host.MergeEditors is null || host.Git.RepositoryFor(uri) is not { } repo || repo.RelativePath(uri) is not { Length: > 0 } rel
                || !repo.Status.Conflicts.Any(e => e.Path == rel))
                return ValueTask.FromResult<IReadOnlyList<CodeLens>?>(null);
            if (MergeConflictParser.Parse(document.GetText(), ct) is not [var first, ..]) return ValueTask.FromResult<IReadOnlyList<CodeLens>?>(null);
            var lens = new CodeLens(TextRange.At(first.StartLine, 0), new Command("Resolve in Merge Editor", OpenMergeEditor, [System.Text.Json.JsonSerializer.SerializeToElement(uri.ToString())]));
            return ValueTask.FromResult<IReadOnlyList<CodeLens>?>([lens]);
        }
    }
}
