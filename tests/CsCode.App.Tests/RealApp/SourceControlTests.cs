using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Editor.Diff;
using CsCode.Panels.Git;
using CsCode.Panels.Git.Engine;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Source control on the REAL composition (docs/SOURCE-CONTROL.md): a folder that is a git repository gets Explorer decorations, the Source
/// Control window, a status bar branch item and gutter markers, and Open Changes opens a diff editor tab whose Next Change command works.
/// </summary>
public class SourceControlTests
{
    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
    }

    private static bool GitAvailable()
    {
        try { Git(Path.GetTempPath(), "--version"); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_git_folder_gets_decorations_the_window_the_branch_item_markers_and_diff_tabs()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed");
        await using var host = await RealAppHost.Create(h =>
        {
            var root = h.Files.Path;
            Git(root, "init", "-q", "-b", "main");
            Git(root, "config", "user.name", "Test User");
            Git(root, "config", "user.email", "test@example.com");
            Git(root, "config", "commit.gpgsign", "false");
            h.Files.WriteFile("a.txt", string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i)) + "\n");
            h.Files.WriteFile("b.txt", "b\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-q", "-m", "initial");
            h.Files.WriteFile("a.txt", string.Join('\n', Enumerable.Range(1, 30).Select(i => i == 5 ? "line five" : "line " + i)) + "\n");
            h.Files.WriteFile("new.txt", "n\n");
        }).StartAsync([]);
        await host.Get<IWorkspaceService>().OpenFolderAsync(host.Files.Uri);
        var git = host.Get<GitService>();
        Assert.True(await RealAppHost.WaitFor(() => git.Repositories.Count == 1 && git.Repositories[0].Status.Entries.Count >= 2, 20_000), "repository found");

        // status bar
        var status = host.Get<IStatusBarService>();
        Assert.True(await RealAppHost.WaitFor(() => status.Items.Any(i => i.Id == "git.branch" && i.Text == "$(source-control) main*")), "branch item");

        // Explorer decorations (the provider is the git service)
        Assert.Same(git, host.Get<IFileDecorationProvider>());
        host.Shell.Tools.Show("explorer");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get("explorer") is not null));
        ExplorerViewModel? Explorer() => (host.Shell.Tools.Get("explorer")?.LiveView as ExplorerView)?.Model;
        Assert.True(await RealAppHost.WaitFor(() => Explorer()?.Rows.FirstOrDefault(r => r.Name == "a.txt")?.Decoration?.Badge == "M", 20_000), "a.txt decorated M");
        Assert.Equal("U", Explorer()!.Rows.Single(r => r.Name == "new.txt").Decoration?.Badge);
        Assert.Null(Explorer()!.Rows.Single(r => r.Name == "b.txt").Decoration);

        // the Source Control window is registered under VS Code's toggle command
        Assert.NotNull(host.Shell.Tools.Get(GitHost.ViewId));
        await host.RunAsync(GitHost.ShowScm);
        Assert.True(await RealAppHost.WaitFor(() => host.Get<GitHost>().LiveView?.ChangesCount == 2), "window rows");

        // the editor's gutter markers and blame read the git service (the markers themselves: tests/CsCode.Editor.Tests SourceControlViewTests)
        Assert.Same(git, host.Get<IQuickDiffProvider>());
        Assert.Same(git, host.Get<ILineBlameProvider>());
        Assert.StartsWith("line 1\n", (await git.GetOriginalAsync(host.Files.FileUri("a.txt")))!.Text);
        await host.Shell.Well.OpenAsync(host.Files.FileUri("a.txt"));

        // Open Changes: a diff tab; the context key and Next Change follow it
        await host.RunAsync(GitHost.OpenChange, host.Files.FileUri("a.txt").ToString());
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Active?.LiveView is DiffEditorControl { Model: not null }, 20_000), "diff tab");
        var diff = (DiffEditorControl)host.Shell.Well.Active!.LiveView!;
        var change = Assert.Single(diff.Model!.Changes);
        Assert.Equal(4, change.ModifiedStart);
        Assert.Equal("a.txt (Working Tree)", host.Shell.Well.Active!.Title);
        Assert.Equal(true, host.Get<IContextKeyService>().Get(ScmContextKeys.TextCompareEditorVisible));
        Assert.True(host.Get<IContextKeyService>().Evaluate("textCompareEditorVisible"));
        Assert.Equal(0, await host.Get<ICommandService>().ExecuteAsync(DiffEditorService.NextChange));

        // stage from the command: the window's staged group follows, the diff tab can go
        await host.RunAsync(GitHost.Stage, host.Files.FileUri("a.txt").ToString());
        Assert.True(await RealAppHost.WaitFor(() => host.Get<GitHost>().LiveView?.StagedCount == 1, 20_000), "staged");
        await host.Shell.Well.CloseAsync(host.Shell.Well.Active!, prompt: false);
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IContextKeyService>().Get(ScmContextKeys.TextCompareEditorVisible) is false));
    }
}

internal static class TempDirectoryUris
{
    public static Uri FileUri(this CsCode.TestDoubles.TempDirectory dir, string rel) => new(dir.Combine(rel.Split('/')));
}
