using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Will-save and file-operation participants (docs/LSP.md) over the REAL composition: the shell's save paths run <see cref="IWillSaveParticipant"/>
/// (bounded to 1.5 s) and apply its edits before writing; Explorer and workspace-edit renames/creates/deletes run <see cref="IFileOperationParticipant"/>
/// around the file system call. One test drives the real language client against a fake language server end to end.
/// </summary>
public class SaveAndFileOperationTests
{
    // ------------------------------------------------------------------ will-save

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Saving_applies_the_will_save_participants_edits_before_the_file_is_written()
    {
        using var env = new RealAppHome();
        var participant = new RecordingParticipant { SaveEdits = _ => [new TextEdit(TextRange.At(0, 0), "// header\n")] };
        var file = env.Workspace.WriteFile("a.txt", "body\n");
        await using var run = new RealAppRun(env, s => participant.Register(s));
        await run.MainWindowAsync();
        var doc = await OpenDirtyAsync(run, file);

        await run.Services.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Save);

        Assert.Equal([$"willSave {new Uri(file)} Manual"], participant.Calls);
        Assert.Equal("// header\nbody\nx", NormalizedText(file));
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_hung_will_save_participant_delays_the_save_by_at_most_the_budget()
    {
        using var env = new RealAppHome();
        var participant = new RecordingParticipant { HangSave = true };
        var file = env.Workspace.WriteFile("a.txt", "body\n");
        await using var run = new RealAppRun(env, s => participant.Register(s));
        await run.MainWindowAsync();
        var doc = await OpenDirtyAsync(run, file);

        var clock = Stopwatch.StartNew();
        await run.Services.GetRequiredService<DocumentWell>().SaveAsync(run.Services.GetRequiredService<DocumentWell>().ActiveEditor!);
        clock.Stop();

        Assert.InRange(clock.Elapsed, FileParticipants.WillSaveBudget - TimeSpan.FromMilliseconds(100), FileParticipants.WillSaveBudget + TimeSpan.FromSeconds(2));
        Assert.Equal("body\nx", NormalizedText(file)); // saved, without the participant's (never delivered) edits
        Assert.False(doc.IsDirty);
    }

    // ------------------------------------------------------------------ file operations

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Explorer_renames_creates_and_deletes_run_the_file_operation_participants_around_the_file_system()
    {
        using var env = new RealAppHome();
        var participant = new RecordingParticipant();
        var source = env.Workspace.WriteFile("src/old.cs", "class Old { }\n");
        var refs = env.Workspace.WriteFile("src/refs.cs", "Old reference\n");
        participant.RenameEdit = _ => new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [new Uri(refs)] = [new TextEdit(TextRange.FromLines(0, 0, 0, 3), "New")] });
        // The participant checks that the edit lands before the move (the old file still exists then) by recording what it sees.
        participant.OnWillRename = () => File.Exists(source) ? "source still in place" : "source already moved";
        await using var run = new RealAppRun(env, s => participant.Register(s));
        await run.MainWindowAsync();
        var sp = run.Services;
        await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<INavigationService>(),
            sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<FileParticipants>());
        await explorer.RefreshRootsAsync();
        var srcFolder = explorer.Roots[0].Children!.Single(c => c.Name == "src");
        await explorer.ExpandAsync(srcFolder);

        var oldNode = srcFolder.Children!.Single(c => c.Name == "old.cs");
        explorer.BeginRename(oldNode);
        oldNode.EditText = "new.cs";
        Assert.True(await explorer.CommitRenameAsync(oldNode));
        var created = await explorer.CreateFileAsync(srcFolder, "added.cs");
        await explorer.ReloadDirectoryAsync(srcFolder);
        Assert.True(await explorer.DeleteAsync(srcFolder.Children!.Single(c => c.Name == "added.cs")));

        var oldUri = new Uri(source);
        var newUri = new Uri(env.Workspace.Combine("src", "new.cs"));
        Assert.Equal(
        [
            $"willRename {oldUri} -> {newUri} (source still in place)",
            $"didRename {oldUri} -> {newUri}",
            $"didCreate {created}",
            $"didDelete {created}",
        ], participant.Calls);
        Assert.Equal("New reference\n", NormalizedText(refs)); // the will-rename edit (a closed file: written on disk)
        Assert.True(File.Exists(newUri.LocalPath));
        Assert.False(File.Exists(created!.LocalPath));
    }

    // files-1: a move that fails (a case-only folder rename on macOS did; here a file of the same name is in the way) left the tabs it had closed
    // closed. They come back where they were.
    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_failed_explorer_move_reopens_the_tabs_it_closed()
    {
        using var env = new RealAppHome();
        var moving = env.Workspace.WriteFile("a/x.txt", "mine\n");
        var inTheWay = env.Workspace.WriteFile("b/x.txt", "in the way\n");
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var sp = run.Services;
        await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        Assert.NotNull(await sp.GetRequiredService<INavigationService>().OpenAsync(new Uri(moving)));
        var well = sp.GetRequiredService<DocumentWell>();
        using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<INavigationService>(),
            sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, well, sp.GetRequiredService<FileParticipants>());
        await explorer.RefreshRootsAsync();
        var a = explorer.Roots[0].Children!.Single(c => c.Name == "a");
        var b = explorer.Roots[0].Children!.Single(c => c.Name == "b");
        await explorer.ExpandAsync(a);

        explorer.Cut([a.Children!.Single(c => c.Name == "x.txt")]);
        await Assert.ThrowsAnyAsync<IOException>(() => explorer.PasteAsync(b));

        Assert.Contains(well.EditorTabs, t => t.Uri == new Uri(moving));
        Assert.Equal("mine\n", NormalizedText(moving));
        Assert.Equal("in the way\n", NormalizedText(inTheWay));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Workspace_edit_resource_operations_run_the_file_operation_participants()
    {
        using var env = new RealAppHome();
        var participant = new RecordingParticipant();
        var a = env.Workspace.WriteFile("a.cs", "a\n");
        var refs = env.Workspace.WriteFile("refs.cs", "Old\n");
        participant.RenameEdit = _ => new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [new Uri(refs)] = [new TextEdit(TextRange.FromLines(0, 0, 0, 3), "New")] });
        await using var run = new RealAppRun(env, s => participant.Register(s));
        await run.MainWindowAsync();
        var created = new Uri(env.Workspace.Combine("made.cs"));
        var renamed = new Uri(env.Workspace.Combine("b.cs"));

        // One resource operation per edit: a multi-file edit asks the user first.
        var nav = run.Services.GetRequiredService<INavigationService>();
        Assert.True(await nav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>>(), [new CreateFileOperation(created)])));
        Assert.True(await nav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>>(), [new RenameFileOperation(new Uri(a), renamed)])));
        Assert.True(await nav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>>(), [new DeleteFileOperation(created)])));

        Assert.Equal(
        [
            $"didCreate {created}",
            $"willRename {new Uri(a)} -> {renamed} (no check)",
            $"didRename {new Uri(a)} -> {renamed}",
            $"didDelete {created}",
        ], participant.Calls);
        Assert.Equal("New\n", NormalizedText(refs)); // the participant's edit, applied without a second confirmation
    }

    // ------------------------------------------------------------------ the real language client end to end

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_language_server_formats_on_save_and_hears_about_file_operations()
    {
        if (FakeLanguageServer.Python is not { } python) { Assert.Skip("python3 is not on PATH: the fake language server cannot run"); return; }
        using var env = new RealAppHome();
        var log = env.Home.Combine("server.log");
        env.WriteUserSettings(FakeLanguageServer.Settings(env, python, "polite", shutdownTimeoutMs: 3000, logPath: log, willSave: "edit"));
        var file = env.Workspace.WriteFile("main" + FakeLanguageServer.Extension, "body\n");
        var refs = env.Workspace.WriteFile("refs" + FakeLanguageServer.Extension, "old name\n");
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var sp = run.Services;
        await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        var doc = await OpenDirtyAsync(run, file);
        var pid = await FakeLanguageServer.WaitForPidAsync(sp);
        using var cleanup = ProcessProbe.KillOnDispose(pid);

        await sp.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Save);
        Assert.Equal("// formatted on save\nbody\nx", NormalizedText(file));
        Assert.False(doc.IsDirty);

        using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<INavigationService>(),
            sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<FileParticipants>());
        await explorer.RefreshRootsAsync();
        var root = explorer.Roots[0];
        var created = await explorer.CreateFileAsync(root, "added" + FakeLanguageServer.Extension);
        var node = root.Children!.Single(c => c.Name == "main" + FakeLanguageServer.Extension);
        explorer.BeginRename(node);
        node.EditText = "renamed" + FakeLanguageServer.Extension;
        Assert.True(await explorer.CommitRenameAsync(node));

        Assert.Equal("new name\n", NormalizedText(refs)); // the server's workspace/willRenameFiles edit
        string[] expected = ["textDocument/willSaveWaitUntil", "workspace/didCreateFiles", "workspace/willRenameFiles", "workspace/didRenameFiles"];
        Assert.True(await DevHost.WaitFor(() => expected.All(m => ServerLog(log).Contains(m)), 10_000),
            "the server did not receive every expected message; it received: " + string.Join(", ", ServerLog(log)));
        Assert.Contains(created!.AbsoluteUri, File.ReadAllText(log));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Opens <paramref name="path"/> in the real editor and types one character at the end (UI thread), so there is something to save.</summary>
    private static async Task<IEditorDocument> OpenDirtyAsync(RealAppRun run, string path)
    {
        var doc = await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(path)) ?? throw new InvalidOperationException("not opened");
        var end = doc.PositionAt(doc.Length);
        Assert.True(doc.ApplyEdits([new TextEdit(new TextRange(end, end), "x")]));
        Assert.True(doc.IsDirty);
        return doc;
    }

    private static string NormalizedText(string path) => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] ServerLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("method").GetString() ?? "").ToArray();
        }
        catch (FileNotFoundException) { return []; }
    }

    private sealed class RecordingParticipant : IWillSaveParticipant, IFileOperationParticipant
    {
        private readonly ConcurrentQueue<string> _calls = new();
        public string[] Calls => _calls.ToArray();
        public Func<ITextDocument, IReadOnlyList<TextEdit>?>? SaveEdits { get; init; }
        public bool HangSave { get; init; }
        public Func<IReadOnlyList<(Uri OldUri, Uri NewUri)>, WorkspaceEdit?>? RenameEdit { get; set; }
        public Func<string>? OnWillRename { get; set; }

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IWillSaveParticipant>(this);
            services.AddSingleton<IFileOperationParticipant>(this);
        }

        public async ValueTask<IReadOnlyList<TextEdit>?> OnWillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct)
        {
            _calls.Enqueue($"willSave {document.Uri} {reason}");
            if (HangSave) await Task.Delay(Timeout.Infinite, ct);
            return SaveEdits?.Invoke(document);
        }

        public ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
        {
            _calls.Enqueue($"willRename {string.Join(", ", files.Select(f => $"{f.OldUri} -> {f.NewUri}"))} ({OnWillRename?.Invoke() ?? "no check"})");
            return ValueTask.FromResult(RenameEdit?.Invoke(files));
        }

        public ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
        {
            _calls.Enqueue($"didRename {string.Join(", ", files.Select(f => $"{f.OldUri} -> {f.NewUri}"))}");
            return ValueTask.CompletedTask;
        }

        public ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
        {
            _calls.Enqueue($"didCreate {string.Join(", ", files)}");
            return ValueTask.CompletedTask;
        }

        public ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
        {
            _calls.Enqueue($"didDelete {string.Join(", ", files)}");
            return ValueTask.CompletedTask;
        }
    }
}
