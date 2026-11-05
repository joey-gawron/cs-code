using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Diff;
using CsCode.Panels.Git;
using CsCode.Panels.Git.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Merge conflicts on the REAL composition (docs/SOURCE-CONTROL.md): a merge that conflicted in a real repository, the editor's conflict commands,
/// and Resolve in Merge Editor's tab, whose Complete Merge saves, stages and closes it, letting the document go.
/// </summary>
public class MergeConflictTests
{
    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args[0] != "merge") throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
        return output;
    }

    private static bool GitAvailable()
    {
        try { Git(Path.GetTempPath(), "--version"); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static Task<RealAppHost> StartConflictedAsync() => RealAppHost.Create(h =>
    {
        var root = h.Files.Path;
        Git(root, "init", "-q", "-b", "main");
        Git(root, "config", "user.name", "Test User");
        Git(root, "config", "user.email", "test@example.com");
        Git(root, "config", "commit.gpgsign", "false");
        h.Files.WriteFile("c.txt", "one\nbase\ntwo\n");
        h.Files.WriteFile("d.txt", "x\nbase\ny\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "base");
        Git(root, "checkout", "-q", "-b", "topic");
        h.Files.WriteFile("c.txt", "one\ntheirs\ntwo\n");
        h.Files.WriteFile("d.txt", "x\ntheirs\ny\n");
        Git(root, "commit", "-q", "-am", "theirs");
        Git(root, "checkout", "-q", "main");
        h.Files.WriteFile("c.txt", "one\nours\ntwo\n");
        h.Files.WriteFile("d.txt", "x\nours\ny\n");
        Git(root, "commit", "-q", "-am", "ours");
        Git(root, "merge", "topic");
    }).StartAsync([]);

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Resolve_in_merge_editor_opens_a_tab_whose_complete_merge_saves_stages_and_closes_it()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed");
        await using var host = await StartConflictedAsync();
        await host.Get<IWorkspaceService>().OpenFolderAsync(host.Files.Uri);
        var git = host.Get<GitService>();
        Assert.True(await RealAppHost.WaitFor(() => git.Repositories.Count == 1 && git.Repositories[0].Status.Conflicts.Count() == 2, 20_000), "two conflicts");
        Assert.Same(host.Get<MergeEditorService>(), host.Get<IMergeEditorService>());
        var uri = host.Files.FileUri("c.txt");

        await host.RunAsync(GitHost.OpenMergeEditor, uri.ToString());
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Active?.LiveView is MergeEditorControl { Merge: not null }, 20_000), "merge editor tab");
        var tab = host.Shell.Well.Active!;
        Assert.Equal("view:merge:" + uri, tab.Id);
        Assert.Equal("Merging: c.txt", tab.Title);
        Assert.Equal(true, host.Get<IContextKeyService>().Get(MergeEditorService.IsMergeEditorKey));
        var editor = (MergeEditorControl)tab.LiveView!;
        var doc = host.Get<IDocumentManager>().Get(uri)!;
        Assert.Equal("one\nbase\ntwo\n", doc.GetText()); // the base in the conflict, until a side is taken
        editor.Set(0, MergeSides.Incoming);

        await host.RunAsync(MergeEditorService.AcceptMerge);
        Assert.True(await RealAppHost.WaitFor(() => !host.Shell.Well.Tabs.Contains(tab), 20_000), "tab closed");
        Assert.Equal("one\ntheirs\ntwo\n", File.ReadAllText(host.Files.Combine("c.txt")));
        Assert.Equal("one\ntheirs\ntwo\n", Git(host.Files.Path, "show", ":c.txt"));
        Assert.True(await RealAppHost.WaitFor(() => git.Repositories[0].Status.Conflicts.Count() == 1, 20_000), "c.txt resolved");
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IDocumentManager>().Get(uri) is null), "the document was let go (no editor tab showed it)");

        // opened and closed untouched: the file keeps its markers and nothing stays open
        var d = host.Files.FileUri("d.txt");
        await host.RunAsync(GitHost.OpenMergeEditor, d.ToString());
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Active?.LiveView is MergeEditorControl { Merge: not null }, 20_000), "second merge editor");
        await host.Shell.Well.CloseAsync(host.Shell.Well.Active!, prompt: false);
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IDocumentManager>().Get(d) is null), "closed");
        Assert.Contains("<<<<<<< HEAD", File.ReadAllText(host.Files.Combine("d.txt")), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Shell.Well.EditorTabs, t => t.Uri == d);
    }

    internal const string Markers = "one\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> topic\ntwo\n";
    internal static MergeEditorRequest Request(Uri file) => new(file, "one\nbase\ntwo\n", "one\nours\ntwo\n", "one\ntheirs\ntwo\n", "Merging: c.txt");

    /// <summary>The real app with save prompts answered by a fake, and a merge editor over c.txt (markers) that no editor tab shows.</summary>
    private static async Task<(RealAppHost Host, FakeModalDialogs Dialogs, Uri File, ShellDocument Tab, MergeEditorControl Editor)> OpenUnsavedMergeAsync()
    {
        var dialogs = new FakeModalDialogs();
        var host = await RealAppHost.Create(h => h.Files.WriteFile("c.txt", Markers)).StartAsync([], s =>
        {
            s.RemoveAll<IModalDialogs>();
            s.AddSingleton<IModalDialogs>(dialogs);
        });
        var file = host.Files.FileUri("c.txt");
        await host.Get<IMergeEditorService>().OpenMergeEditorAsync(Request(file));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Active?.LiveView is MergeEditorControl { Merge: not null }, 20_000), "merge editor");
        var tab = host.Shell.Well.Active!;
        var editor = (MergeEditorControl)tab.LiveView!;
        // the editor's own starting edit (the base in the conflict) is no change of the user's: nothing to ask about yet
        Assert.False(host.Shell.Well.HasUnsavedChanges);
        Assert.False(host.Window.HasUnsavedChanges);
        editor.Set(0, MergeSides.Incoming);
        Assert.True(host.Shell.Well.HasUnsavedChanges);
        Assert.True(host.Window.HasUnsavedChanges);
        Assert.Equal(file, Assert.Single(host.Shell.Well.UnsavedAttachedDocuments).Document.Uri);
        Assert.Empty(host.Shell.Well.EditorTabs);
        return (host, dialogs, file, tab, editor);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_merge_editors_unsaved_result_no_editor_tab_shows_is_asked_about_at_close_all_like_an_editor_tab()
    {
        var (host, dialogs, file, tab, _) = await OpenUnsavedMergeAsync();
        await using var _ = host;
        var well = host.Shell.Well;
        // Close All (what a quit's save prompts run): Cancel keeps everything
        dialogs.Answers.Enqueue("Cancel");
        Assert.False(await well.CloseAllAsync());
        var prompt = Assert.Single(dialogs.Shown);
        Assert.Contains("c.txt", prompt.Message, StringComparison.Ordinal);
        Assert.Equal(["Save", "Don't Save", "Cancel"], prompt.Buttons);
        Assert.Contains(tab, well.Tabs);
        // Save: the result is written and the document let go with the tab
        dialogs.Answers.Enqueue("Save");
        Assert.True(await well.CloseAllAsync());
        Assert.Equal("one\ntheirs\ntwo\n", File.ReadAllText(host.Files.Combine("c.txt")));
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IDocumentManager>().Get(file) is null), "document closed");
        Assert.Empty(well.EditorTabs);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Closing_a_merge_editors_tab_asks_too_and_dont_save_discards_its_result()
    {
        var (host, dialogs, file, tab, _) = await OpenUnsavedMergeAsync();
        await using var _ = host;
        // the tab's close button (Dock's own close): the well cancels it and asks
        dialogs.Answers.Enqueue("Don't Save");
        host.Get<ShellDockFactory>().CloseDockable(tab);
        Assert.True(await RealAppHost.WaitFor(() => !host.Shell.Well.Tabs.Contains(tab), 20_000), "closed after the prompt");
        Assert.Single(dialogs.Shown);
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IDocumentManager>().Get(file) is null), "document closed");
        Assert.Equal(Markers, File.ReadAllText(host.Files.Combine("c.txt")));
        Assert.Empty(host.Shell.Well.EditorTabs);
        Assert.False(host.Shell.Well.HasUnsavedChanges);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_close_nobody_was_asked_about_keeps_the_unsaved_result_in_an_editor_tab()
    {
        var (host, dialogs, file, tab, _) = await OpenUnsavedMergeAsync();
        await using var _ = host;
        await host.Shell.Well.CloseAsync(tab, prompt: false);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Find(file) is not null, 20_000), "kept in an editor tab");
        var editorTab = host.Shell.Well.Find(file)!;
        Assert.True(editorTab.IsDirty);
        Assert.Equal("one\ntheirs\ntwo\n", editorTab.Doc.GetText());
        Assert.Empty(dialogs.Shown);
        Assert.True(host.Shell.Well.HasUnsavedChanges);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_editor_resolves_a_block_from_its_command_and_the_merge_editor_keeps_an_open_tabs_document()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed");
        await using var host = await StartConflictedAsync();
        await host.Get<IWorkspaceService>().OpenFolderAsync(host.Files.Uri);
        var git = host.Get<GitService>();
        Assert.True(await RealAppHost.WaitFor(() => git.Repositories.Count == 1 && git.Repositories[0].Status.Conflicts.Count() == 2, 20_000), "two conflicts");
        var uri = host.Files.FileUri("c.txt");
        var editorTab = await host.Shell.Well.OpenAsync(uri);
        Assert.True(await RealAppHost.WaitFor(() => editorTab.Controller is not null), "editor view");
        editorTab.Controller!.SetSelection(TextRange.At(2, 0)); // "ours"
        editorTab.Controller.Focus();
        await host.RunAsync("merge-conflict.accept.current");
        Assert.True(await RealAppHost.WaitFor(() => editorTab.Doc.GetText() == "one\nours\ntwo\n"), "accepted current: " + editorTab.Doc.GetText());
        editorTab.Controller.Undo();
        Assert.Contains("<<<<<<< HEAD", editorTab.Doc.GetText(), StringComparison.Ordinal);

        // the merge editor over a document an editor tab shows: closing the merge editor leaves that tab and its document alone
        await host.RunAsync(GitHost.OpenMergeEditor, uri.ToString());
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Active?.LiveView is MergeEditorControl { Merge: not null }, 20_000), "merge editor tab");
        ((MergeEditorControl)host.Shell.Well.Active!.LiveView!).Set(0, MergeSides.Both);
        Assert.Equal("one\nours\ntheirs\ntwo\n", editorTab.Doc.GetText());
        await host.Shell.Well.CloseAsync(host.Shell.Well.Active!, prompt: false);
        Assert.Contains(editorTab, host.Shell.Well.EditorTabs);
        Assert.Same(editorTab.Doc, host.Get<IDocumentManager>().Get(uri));
        Assert.True(editorTab.Doc.IsDirty);
    }
}
