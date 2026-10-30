using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests;

public class PaletteTests
{
    private sealed class Rig : IDisposable
    {
        public WellRig Well { get; } = new();
        public TestCommandService Commands { get; } = new();
        public SimpleContextKeys Keys { get; } = new();
        public FakeKeybindingService Bindings { get; }
        public RecordingNavigationService Nav { get; }
        public FakeWorkspaceService Workspace { get; } = new();
        public InMemoryStateStorage State { get; } = new();
        public LanguageFeatureRegistry Languages { get; } = new();
        public PaletteContext Context { get; }
        public List<string> Ran { get; } = new();

        public Rig()
        {
            Bindings = new FakeKeybindingService(Keys);
            Nav = new RecordingNavigationService(new DocumentManager());
            Context = new PaletteContext(Commands, Nav, Well.Well, Keys, Bindings, Workspace, State, Languages);
        }

        public void Cmd(string id, string title, string? category = null, string? enablement = null) =>
            Commands.Register(id, _ => { Ran.Add(id); return ValueTask.FromResult<object?>(null); }, new CommandMetadata(id, title, category, null, enablement));

        public PaletteSource Source() => new(Context);
        public void Dispose() => Well.Dispose();
    }

    [Fact]
    public void Command_mode_lists_titled_commands_with_category_keybinding_and_filters_by_enablement()
    {
        using var rig = new Rig();
        rig.Cmd("workbench.action.files.save", "Save", "File");
        rig.Cmd("editor.action.formatDocument", "Format Document", "Editor", "editorTextFocus");
        rig.Commands.Register("internal.nothing", _ => ValueTask.FromResult<object?>(null)); // no title: never listed
        rig.Bindings.Add("workbench.action.files.save", "ctrl+s");

        var entries = rig.Source().TryQuerySync(">")!;
        Assert.Equal(["File: Save"], entries.Select(e => e.Label)); // Format Document needs editorTextFocus
        Assert.Equal("Ctrl+S", entries[0].KeybindingLabel);

        rig.Keys.Set("editorTextFocus", true);
        Assert.Equal(2, rig.Source().TryQuerySync(">")!.Count);
    }

    [Fact]
    public async Task Command_mode_ranks_by_fuzzy_match_and_floats_recently_used_commands_first()
    {
        using var rig = new Rig();
        rig.Cmd("a", "Toggle Terminal", "View");
        rig.Cmd("b", "Toggle Word Wrap", "View");
        rig.Cmd("c", "Open Settings", "Preferences");
        var src = rig.Source();
        Assert.Equal("Preferences: Open Settings", src.TryQuerySync(">")![0].Label); // alphabetical by label when nothing was used

        var tt = src.TryQuerySync(">tt")!;
        Assert.Equal("View: Toggle Terminal", tt[0].Label);
        Assert.NotEmpty(tt[0].LabelMatches);

        await src.TryQuerySync(">")!.Single(e => e.Label == "View: Toggle Word Wrap").Accept!();
        Assert.Equal(["b"], rig.Ran);
        Assert.Equal("View: Toggle Word Wrap", rig.Source().TryQuerySync(">")![0].Label); // recently used first
    }

    /// <summary>T13/T18: command mode used to stop at 100 rows, so 'Preferences:' and 'Search:' commands fell off the end. VS Code lists them all.</summary>
    [Fact]
    public void Command_mode_lists_every_command_however_many_there_are()
    {
        using var rig = new Rig();
        for (var i = 0; i < 450; i++) rig.Cmd($"cmd.{i:D3}", $"Command {i:D3}", i % 2 == 0 ? "Macros" : "View");
        rig.Cmd("z.last", "Zebra", "Zoo");
        var all = rig.Source().TryQuerySync(">")!;
        Assert.Equal(451, all.Count);
        Assert.Equal("Zoo: Zebra", all[^1].Label); // alphabetically last, still listed
        Assert.Equal(225, rig.Source().TryQuerySync(">macros")!.Count); // a filter keeps every match too
    }

    [Fact]
    public async Task A_command_table_over_the_sync_limit_is_ranked_on_a_worker_and_still_lists_every_command()
    {
        using var rig = new Rig();
        for (var i = 0; i < PaletteSource.SyncCommandLimit + 200; i++) rig.Cmd($"cmd.{i:D4}", $"Command {i:D4}", "Test");
        var src = rig.Source();
        Assert.Null(src.TryQuerySync(">")); // too many to sort on the UI thread
        var all = await src.QueryAsync(">", CancellationToken.None);
        Assert.Equal(PaletteSource.SyncCommandLimit + 200, all.Count);
        Assert.Equal("Test: Command 0000", all[0].Label);
        Assert.Equal(PaletteSource.SyncCommandLimit + 200, (await src.QueryAsync(">command", CancellationToken.None)).Count);
    }

    /// <summary>T12: a failed index build (an unreadable folder) used to stay cached until the folders changed, so go to file kept failing.</summary>
    [Fact]
    public async Task A_failed_file_index_build_is_not_cached()
    {
        using var dir = new TempDirectory("cscode-palette-");
        dir.WriteFile("a.cs", "");
        using var rig = new Rig();
        await rig.Workspace.OpenFolderAsync(dir.Uri);
        var index = new FileIndex(rig.Workspace);
        rig.Workspace.FindFilesFailure = new IOException("permission denied");
        await Assert.ThrowsAsync<IOException>(() => index.GetAsync(CancellationToken.None));
        Assert.Equal(1, index.BuildCount);

        var snapshot = await index.GetAsync(CancellationToken.None); // the next query builds again, and succeeds
        Assert.Equal(2, index.BuildCount);
        Assert.Equal(["a.cs"], snapshot.Paths);
        await index.GetAsync(CancellationToken.None);
        Assert.Equal(2, index.BuildCount); // a good build is kept

        // Through the palette: the failed query reports its error, the next one lists the file.
        var src = new PaletteSource(rig.Context, new FileIndex(rig.Workspace));
        rig.Workspace.FindFilesFailure = new IOException("permission denied");
        await Assert.ThrowsAsync<IOException>(() => src.QueryAsync("a", CancellationToken.None));
        Assert.Equal("a.cs", (await src.QueryAsync("a", CancellationToken.None)).Single().Label);
        index.Dispose();
    }

    [Fact]
    public async Task Go_to_line_parses_line_and_column_clamps_and_moves_the_caret()
    {
        using var rig = new Rig();
        Assert.Equal("Open a file first to go to a line", rig.Source().TryQuerySync(":")![0].Label);
        var tab = await rig.Well.Open("a.cs");
        rig.Well.Editors.Doc(tab.Uri).ApplyEdits([new TextEdit(TextRange.At(0, 0), "l1\nl2\nl3\nl4\nl5\n")]);
        var one = rig.Source().TryQuerySync(":3")!.Single();
        Assert.Equal("Go to line 3", one.Label);
        Assert.Equal(new TextPosition(2, 0), (TextPosition)one.Tag!);
        var withCol = rig.Source().TryQuerySync(":4:2")!.Single();
        Assert.Equal("Go to line 4, column 2", withCol.Label);
        Assert.Equal(new TextPosition(3, 1), (TextPosition)withCol.Tag!);
        Assert.Equal("Go to line 6", rig.Source().TryQuerySync(":99")!.Single().Label); // clamped to the last line (a trailing newline adds one)
        Assert.StartsWith("Type a line number", rig.Source().TryQuerySync(":abc")!.Single().Label);
    }

    [Fact]
    public void Help_mode_lists_every_prefix_and_unknown_prefixes_fall_through_to_files()
    {
        using var rig = new Rig();
        var help = rig.Source().TryQuerySync("?")!;
        Assert.Equal(["[no prefix]", ">", ":", "@", "#", "session:"], help.Select(h => h.Label));
        Assert.Null(rig.Source().TryQuerySync("prog")); // file search is asynchronous
        Assert.Null(rig.Source().TryQuerySync("@"));
        Assert.Null(rig.Source().TryQuerySync("#x"));
    }

    [Fact]
    public async Task File_mode_ranks_real_files_with_name_highlights_and_opens_the_choice()
    {
        using var dir = new TempDirectory("cscode-palette-");
        dir.WriteFile("src/App/Program.cs", "");
        dir.WriteFile("src/App/Programmer.txt", "");
        dir.WriteFile("src/Lib/Helper.cs", "");
        dir.WriteFile("docs/readme.md", "");
        using var rig = new Rig();
        await rig.Workspace.OpenFolderAsync(dir.Uri);
        var src = rig.Source();

        var results = await src.QueryAsync("prog", CancellationToken.None);
        Assert.Equal("Program.cs", results[0].Label);
        Assert.Equal("src/App", results[0].Description);
        Assert.Equal([0, 1, 2, 3], results[0].LabelMatches);
        Assert.DoesNotContain(results, r => r.Label == "Helper.cs");

        await results[0].Accept!();
        Assert.Equal(new Uri(dir.Combine("src", "App", "Program.cs")), rig.Nav.Opened.Single().Uri);
        // The opened file is remembered and shown first when the palette opens with an empty query.
        Assert.Equal("Program.cs", src.TryQuerySync("")![0].Label);
    }

    [Fact]
    public async Task The_file_index_is_built_once_invalidated_on_folder_change_and_dropped_under_memory_pressure()
    {
        using var dir = new TempDirectory("cscode-palette-");
        dir.WriteFile("a.cs", "");
        using var rig = new Rig();
        await rig.Workspace.OpenFolderAsync(dir.Uri);
        var index = new FileIndex(rig.Workspace);
        await index.GetAsync(CancellationToken.None);
        await index.GetAsync(CancellationToken.None);
        Assert.Equal(1, index.BuildCount);
        Assert.True(index.EstimatedBytes > 0);

        Assert.Equal(0, index.Trim(MemoryTrimLevel.Idle) > 0 ? 1 : 0); // idle housekeeping keeps it
        Assert.True(index.Trim(MemoryTrimLevel.Moderate) > 0);
        Assert.Equal(0, index.EstimatedBytes);
        await index.GetAsync(CancellationToken.None);
        Assert.Equal(2, index.BuildCount); // rebuilt on demand

        await rig.Workspace.CloseFolderAsync(); // FoldersChanged invalidates it
        await index.GetAsync(CancellationToken.None);
        Assert.Equal(3, index.BuildCount);
        index.Dispose();
    }

    [Fact]
    public async Task Searching_200k_files_ranks_on_a_worker_and_returns_a_small_immutable_result()
    {
        using var rig = new Rig();
        rig.Workspace.SyntheticFiles = Enumerable.Range(0, 200_000).Select(i => new Uri($"file:///proj/src/module{i % 500}/sub{i % 37}/File{i}.cs")).ToArray();
        await rig.Workspace.OpenFolderAsync(new Uri("file:///proj/"));
        var src = rig.Source();
        await src.QueryAsync("warm", CancellationToken.None); // index build is a one-off cost
        var sw = Stopwatch.StartNew();
        var results = await src.QueryAsync("m12f9", CancellationToken.None);
        sw.Stop();
        Assert.InRange(results.Count, 1, PaletteSource.MaxResults);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"ranking 200k paths took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_cancelled_file_query_stops_promptly()
    {
        using var rig = new Rig();
        rig.Workspace.SyntheticFiles = Enumerable.Range(0, 200_000).Select(i => new Uri($"file:///proj/d{i % 100}/f{i}.txt")).ToArray();
        await rig.Workspace.OpenFolderAsync(new Uri("file:///proj/"));
        var src = rig.Source();
        await src.QueryAsync("x", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.QueryAsync("f1", cts.Token));
    }

    private sealed class SymbolProvider : IDocumentSymbolProvider, IWorkspaceSymbolProvider
    {
        public ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<DocumentSymbol>?>([
                new DocumentSymbol("Greeter", SymbolKind.Class, TextRange.FromLines(2, 0, 9, 1), TextRange.FromLines(2, 13, 2, 20), Children: [
                    new DocumentSymbol("Greet", SymbolKind.Method, TextRange.FromLines(4, 4, 6, 5), TextRange.FromLines(4, 16, 4, 21)),
                    new DocumentSymbol("Name", SymbolKind.Property, TextRange.FromLines(7, 4, 7, 30), TextRange.FromLines(7, 18, 7, 22))]),
            ]);

        public ValueTask<IReadOnlyList<WorkspaceSymbol>?> ProvideWorkspaceSymbolsAsync(string query, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WorkspaceSymbol>?>([new WorkspaceSymbol("Greeter", SymbolKind.Class, new Location(new Uri("file:///p/Greeter.cs"), TextRange.At(2, 13)), "Demo")]);
    }

    [AvaloniaFact]
    public async Task Document_symbols_are_flattened_with_containers_filtered_and_reveal_the_symbol()
    {
        using var rig = new Rig();
        var provider = new SymbolProvider();
        rig.Languages.Register<IDocumentSymbolProvider>(DocumentSelector.All, provider);
        rig.Languages.Register<IWorkspaceSymbolProvider>(DocumentSelector.All, provider);
        var tab = await rig.Well.Open("a.cs");
        var controller = (FakeController)rig.Well.Editors.GetController(tab.EnsureView())!;
        var src = rig.Source();

        var all = await src.QueryAsync("@", CancellationToken.None);
        Assert.Equal(["Greeter", "Greet", "Name"], all.Select(e => e.Label));
        Assert.Equal("Method in Greeter", all[1].Description);
        var filtered = await src.QueryAsync("@gre", CancellationToken.None);
        Assert.Equal(["Greet", "Greeter"], filtered.Select(e => e.Label).OrderBy(x => x.Length)); // "Name" does not match; the shorter match ranks first

        await all[1].Accept!();
        Assert.Equal(TextRange.FromLines(4, 16, 4, 21), controller.Reveals.Last());

        var ws = await src.QueryAsync("#gree", CancellationToken.None);
        Assert.Equal("Demo - Greeter.cs", ws.Single().Description);
        await ws.Single().Accept!();
        Assert.Equal(new Uri("file:///p/Greeter.cs"), rig.Nav.Opened.Single().Uri);
    }

    [Fact]
    public async Task The_controller_shows_the_palette_on_the_shared_overlay_and_runs_the_accepted_entry_after_it_closes()
    {
        using var rig = new Rig();
        rig.Cmd("x.run", "Run Me", "Test");
        var ui = new ManualUiDispatcher();
        var quick = new QuickInputViewModel(ui);
        using var palette = new PaletteController(quick, rig.Context, ui);

        var show = Task.Run(() => palette.ShowAsync(">run"));
        rig.Well.Dispose(); // (no effect; ensures the rig can be reused below)
        var deadline = Environment.TickCount64 + 3000;
        while (!quick.IsVisible && Environment.TickCount64 < deadline) { ui.RunPending(); Thread.Sleep(2); }
        Assert.True(quick.IsVisible);
        Assert.Equal("Test: Run Me", quick.Items[0].Label); // instant: no dispatcher turn needed for command mode
        quick.Accept();
        await show;
        Assert.Equal(["x.run"], rig.Ran);
        Assert.False(quick.IsVisible);
    }

    [Fact]
    public async Task Escape_in_the_palette_runs_nothing()
    {
        using var rig = new Rig();
        rig.Cmd("x.run", "Run Me");
        var ui = new ManualUiDispatcher();
        var quick = new QuickInputViewModel(ui);
        using var palette = new PaletteController(quick, rig.Context, ui);
        var show = Task.Run(() => palette.ShowAsync(">"));
        var deadline = Environment.TickCount64 + 3000;
        while (!quick.IsVisible && Environment.TickCount64 < deadline) { ui.RunPending(); Thread.Sleep(2); }
        quick.Cancel();
        await show;
        Assert.Empty(rig.Ran);
    }
}
