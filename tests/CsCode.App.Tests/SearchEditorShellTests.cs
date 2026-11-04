using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>What the shell does for the search editor (CsCode.Panels.Search): untitled tabs with text and a title, to the side, Save As proposals.</summary>
public sealed class SearchEditorShellTests
{
    /// <summary>Answers Save As with a fixed file and records what the dialog was asked to propose.</summary>
    private sealed class SaveAsWindows(Uri answer) : IWindowService
    {
        private readonly RecordingWindowService _inner = new();
        public SaveDialogOptions? Asked { get; private set; }
        public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default)
        {
            Asked = options;
            return ValueTask.FromResult<Uri?>(answer);
        }
        public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) => _inner.ShowMessageAsync(severity, message, actions, ct);
        public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => _inner.ShowQuickPickAsync(items, options, ct);
        public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => _inner.ShowQuickPickManyAsync(items, options, ct);
        public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => _inner.ShowInputBoxAsync(options, ct);
        public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => _inner.ShowOpenDialogAsync(options, ct);
        public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => _inner.WithProgressAsync(title, cancellable, work);
        public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) => Disposable.Empty;
        public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(true);
        public ValueTask CopyToClipboardAsync(string text) => ValueTask.CompletedTask;
    }

    [Fact]
    public void An_untitled_editor_opens_with_its_text_title_and_save_proposal_and_can_be_retitled()
    {
        using var rig = new WellRig();
        var save = new Uri("file:///work/foo.code-search");
        var tab = rig.Well.OpenUntitled("search-result", "# Query: foo\n\n", "Search: foo", save, toSide: false);
        Assert.Equal("Search: foo", tab.Title);
        Assert.Equal("# Query: foo\n\n", tab.Doc.GetText());
        Assert.Equal("search-result", tab.Doc.LanguageId);
        Assert.Equal(save, tab.SuggestedSaveUri);
        Assert.Same(tab, rig.Well.Active);

        rig.Well.SetTitle(tab, "Search: bar", null);
        Assert.Equal("Search: bar", tab.Title);
        Assert.Null(tab.SuggestedSaveUri);
        rig.Well.SetTitle(tab, null, null);
        Assert.Equal(tab.Doc.Uri.AbsolutePath, tab.Title); // its own name back: Untitled-N
    }

    [Fact]
    public async Task To_the_side_puts_the_editor_in_the_group_beside_the_active_one()
    {
        using var rig = new WellRig();
        var a = await rig.Open("a.cs");
        var side = rig.Well.OpenUntitled("search-result", "# Query: x\n\n", "Search: x", null, toSide: true);
        Assert.Equal(2, rig.Factory.DocumentGroups().Count);
        Assert.NotSame(rig.Factory.GroupOf(a), rig.Factory.GroupOf(side));

        // a result opened to the side from the search editor lands beside it, not on top of it
        rig.Well.Activate(side);
        var b = await rig.Well.OpenToSideAsync(WellRig.U("b.cs"));
        Assert.NotSame(rig.Factory.GroupOf(side), rig.Factory.GroupOf(b));
        Assert.Same(b, rig.Well.Active);
    }

    [Fact]
    public async Task Save_as_proposes_the_search_editors_file()
    {
        var save = new Uri("file:///work/foo.code-search");
        var windows = new SaveAsWindows(save);
        using var rig = new WellRig();
        var well = new DocumentWell(rig.Factory, rig.Editors, rig.Dialogs, windows);
        try
        {
            var tab = well.OpenUntitled("search-result", "# Query: foo\n\n", "Search: foo", save, toSide: false);
            Assert.True(await well.SaveAsync(tab));
            Assert.Equal(save, windows.Asked!.DefaultUri);
            Assert.Equal(save, ((FakeEditorDocument)tab.Doc).SavedAs);
        }
        finally { well.Dispose(); }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task In_the_real_composition_a_new_search_editor_searches_and_its_results_lead_to_the_file()
    {
        using var env = new RealAppHome();
        env.Workspace.WriteFile("src/a.cs", "using System;\nclass Alpha { }\n");
        env.Workspace.WriteFile("src/b.cs", "class Beta { }\n");
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var sp = run.Services;
        await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        var well = sp.GetRequiredService<DocumentWell>();

        await sp.GetRequiredService<ICommandService>().ExecuteAsync("search.action.openNewEditor", "Alpha");
        Assert.True(await DevHost.WaitFor(() => well.ActiveEditor?.Doc.GetText().Contains("src/a.cs:", StringComparison.Ordinal) == true
                                                && well.ActiveEditor.Doc.GetText().Contains("1 result - 1 file", StringComparison.Ordinal), 30_000),
            well.ActiveEditor?.Doc.GetText());
        var tab = well.ActiveEditor!;
        Assert.Equal("search-result", tab.Doc.LanguageId);
        Assert.True(tab.Doc.IsUntitled);
        Assert.Equal("Search: Alpha", tab.Title);
        Assert.Equal("alpha.code-search", Path.GetFileName(tab.SuggestedSaveUri!.LocalPath).ToLowerInvariant());
        Assert.Contains("  2: class Alpha { }", tab.Doc.GetText());

        // F12 on the result line: the search-result definition provider, registered in the real feature registry
        var line = Enumerable.Range(0, tab.Doc.LineCount).First(l => tab.Doc.GetLineText(l) == "  2: class Alpha { }");
        var provider = Assert.Single(sp.GetRequiredService<ILanguageFeatureRegistry>().GetProviders<IDefinitionProvider>(tab.Doc));
        var target = Assert.Single((await provider.ProvideDefinitionAsync(tab.Doc, new TextPosition(line, 11), DefinitionKind.Definition, TestContext.Current.CancellationToken))!);
        Assert.EndsWith(Path.Combine("src", "a.cs"), target.TargetUri.LocalPath); // the workspace folder may be the temp dir's real path
        Assert.Equal(new TextPosition(1, 6), target.TargetSelectionRange.Start);
    }
}
