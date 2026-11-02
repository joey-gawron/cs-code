using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Testing.Engine;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Ui;

public sealed class TestingHostTests
{
    [Fact]
    public void Registers_the_vs_code_testing_commands_settings_and_the_glyph_provider()
    {
        using var rig = new HostRig();
        foreach (var id in new[] { "testing.runAll", "testing.debugAll", "testing.reRunFailTests", "testing.debugFailTests", "testing.reRunLastRun", "testing.debugLastRun",
                     "testing.runAtCursor", "testing.debugAtCursor", "testing.runCurrentFile", "testing.debugCurrentFile", "testing.cancelRun", "testing.refreshTests",
                     "testing.clearTestResults", "testing.showMostRecentOutput", "testing.collapseAll", "workbench.view.testing.focus" })
            Assert.True(rig.Commands.Has(id), id);
        Assert.Equal("Test", rig.Commands.All.Single(c => c.Id == "testing.runAll").Category);
        Assert.Equal("Run All Tests", rig.Commands.All.Single(c => c.Id == "testing.runAll").Title);
        foreach (var key in new[] { TestingSettings.GutterEnabled, TestingSettings.DefaultGutterClickAction, TestingSettings.OpenTesting, TestingSettings.AutoDiscover,
                     TestingSettings.DotNetEnabled, TestingSettings.RustEnabled, TestingSettings.JavaScriptEnabled, TestingSettings.CTestEnabled })
            Assert.Contains(rig.Config.Properties, p => p.Key == key && p.Description is { Length: > 0 });
        Assert.Equal(TestingSettings.CTestEnabled, TestingSettings.EnabledKey("ctest")); // not JavaScript's switch
        var doc = new InMemoryTextDocument(new Uri("file:///w/calc/MathTests.cs"), "csharp", HostRig.MathSource);
        Assert.Same(rig.Host.Gutter, Assert.Single(rig.Features.GetProviders<ITestGutterProvider>(doc)));
        Assert.Empty(rig.Features.GetProviders<ITestGutterProvider>(new InMemoryTextDocument(new Uri("file:///w/x.md"), "markdown")));
    }

    [Fact]
    public async Task Opening_a_folder_discovers_nothing_until_the_test_explorer_is_first_shown()
    {
        using var rig = new HostRig();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Provider.Detections); // a cargo package would have been built here (cargo test --no-run)
        rig.Workspace.Folders = [new Uri("file:///w/"), new Uri("file:///other/")];
        Assert.Equal(0, rig.Provider.Detections);

        var vm = rig.Host.CreateExplorer();
        await rig.DiscoveredAsync();
        Assert.Equal(3, rig.Host.Controller.Tree.TestCount);
        Assert.Equal(2, rig.Provider.Detections); // both folders, once
        rig.Host.ReleaseExplorer(vm);
    }

    [Fact]
    public async Task A_test_command_or_refresh_before_any_discovery_discovers_first()
    {
        using (var rig = new HostRig())
        {
            await rig.Commands.ExecuteAsync("testing.runAll");
            Assert.Equal(3, rig.Host.Controller.Tree.TestCount);
            Assert.Equal(3, Assert.Single(rig.Provider.Runs).TestIds?.Count ?? 3);
            Assert.Equal(TestOutcome.Failed, rig.Host.Controller.Tree.FindTest("Calc.MathTests.Subtracts")!.Outcome);
        }
        using (var rig = new HostRig())
        {
            await rig.Commands.ExecuteAsync("testing.refreshTests");
            Assert.Equal(3, rig.Host.Controller.Tree.TestCount);
        }
    }

    [Fact]
    public async Task An_editor_showing_a_file_with_tests_starts_discovery_unless_auto_discovery_is_off()
    {
        using (var rig = new HostRig())
        {
            var plain = new InMemoryTextDocument(new Uri("file:///w/calc/Plain.cs"), "csharp", "namespace Calc; public class Plain { }");
            Assert.Null(await rig.Host.Gutter.ProvideTestsAsync(plain, TestContext.Current.CancellationToken));
            Assert.Equal(0, rig.Provider.Detections); // no tests in it: nothing wanted yet
            var doc = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource);
            Assert.Null(await rig.Host.Gutter.ProvideTestsAsync(doc, TestContext.Current.CancellationToken)); // the glyphs come after discovery
            await rig.DiscoveredAsync();
            Assert.Equal(1, rig.Provider.Detections);
            Assert.Equal(2, (await rig.Host.Gutter.ProvideTestsAsync(doc, TestContext.Current.CancellationToken))!.Count);
        }
        using (var rig = new HostRig())
        {
            rig.Config.Set(TestingSettings.AutoDiscover, false);
            var doc = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource);
            Assert.Null(await rig.Host.Gutter.ProvideTestsAsync(doc, TestContext.Current.CancellationToken));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(0, rig.Provider.Detections);
        }
    }

    [Fact]
    public async Task Run_all_publishes_failures_to_the_error_list()
    {
        using var rig = new HostRig();
        await rig.DiscoveredAsync();
        Assert.Equal(3, rig.Host.Controller.Tree.TestCount);

        await rig.Commands.ExecuteAsync("testing.runAll");
        Assert.Equal(TestOutcome.Failed, rig.Host.Controller.Tree.FindTest("Calc.MathTests.Subtracts")!.Outcome);
        var diagnostic = Assert.Single(rig.Diagnostics.Get(new Uri(HostRig.MathFile)));
        Assert.Equal(7, diagnostic.Range.Start.Line);
        Assert.Equal("Test Explorer", diagnostic.Source);
        Assert.Contains("Calc.MathTests.Subtracts", diagnostic.Message);
        Assert.Contains("Assert.Equal() Failure", diagnostic.Message);
        Assert.Equal(true, rig.Keys.Get(TestingHost.HasFailedKey));
        Assert.Equal(false, rig.Keys.Get(TestingHost.IsRunningKey));
        Assert.Contains("testing", rig.Views.Shown); // testing.openTesting = openOnTestStart
        Assert.Contains(rig.Output.GetOrCreate(TestingHost.OutputChannelName).GetText().Split('\n'), l => l.Contains("1 failed", StringComparison.Ordinal));

        // fixed: the rerun of the failures clears the Error List
        rig.Provider.Outcome = id => new TestResult(id, TestOutcome.Passed);
        await rig.Commands.ExecuteAsync("testing.reRunFailTests");
        Assert.Equal(["Calc.MathTests.Subtracts"], rig.Provider.Runs.Last().TestIds);
        Assert.Empty(rig.Diagnostics.Get(new Uri(HostRig.MathFile)));
        Assert.Equal(false, rig.Keys.Get(TestingHost.HasFailedKey));
    }

    [Fact]
    public async Task Never_open_keeps_the_explorer_where_it_is()
    {
        using var rig = new HostRig();
        rig.Config.Set(TestingSettings.OpenTesting, "neverOpen");
        await rig.DiscoveredAsync();
        await rig.Commands.ExecuteAsync("testing.runAll");
        Assert.DoesNotContain("testing", rig.Views.Shown);
    }

    [Fact]
    public async Task The_glyph_provider_maps_a_documents_tests_to_their_results_and_runs_them()
    {
        using var rig = new HostRig();
        await rig.DiscoveredAsync();
        var doc = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource);
        var before = await rig.Host.Gutter.ProvideTestsAsync(doc, TestContext.Current.CancellationToken);
        Assert.Equal([(4, "Calc.MathTests.Adds", TestGutterState.None), (7, "Calc.MathTests.Subtracts", TestGutterState.None)],
            before!.Select(i => (i.Line, i.TestId, i.State)));

        var changed = 0;
        using var sub = rig.Host.Gutter.StatesChanged.Subscribe(_ => changed++);
        await rig.Host.Gutter.RunAsync("Calc.MathTests.Subtracts", debug: false, TestContext.Current.CancellationToken);
        Assert.Equal(["Calc.MathTests.Subtracts"], rig.Provider.Runs.Last().TestIds);
        Assert.True(changed > 0);
        var after = await rig.Host.Gutter.ProvideTestsAsync(doc, TestContext.Current.CancellationToken);
        Assert.Equal(TestGutterState.Failed, after!.Single(i => i.TestId == "Calc.MathTests.Subtracts").State);
        Assert.Equal("Assert.Equal() Failure", after!.Single(i => i.TestId == "Calc.MathTests.Subtracts").Message);

        // lines follow the text: a test moved down by an edit is found on its new line
        var edited = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", "// header\n\n" + HostRig.MathSource);
        var moved = await rig.Host.Gutter.ProvideTestsAsync(edited, TestContext.Current.CancellationToken);
        Assert.Equal([6, 9], moved!.Select(i => i.Line));
        // a file the explorer knows nothing about has no glyphs
        Assert.Null(await rig.Host.Gutter.ProvideTestsAsync(new InMemoryTextDocument(new Uri("file:///w/calc/New.cs"), "csharp", HostRig.MathSource), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_known_test_gets_run_and_debug_lenses_that_run_it_and_follow_discovery_and_the_setting()
    {
        using var rig = new HostRig();
        var doc = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource);
        var provider = Assert.Single(rig.Features.GetProviders<ICodeLensProvider>(doc));
        Assert.Empty(rig.Features.GetProviders<ICodeLensProvider>(new InMemoryTextDocument(new Uri("file:///w/x.md"), "markdown")));
        var changed = new List<Uri?>();
        using var sub = provider.CodeLensesChanged.Subscribe(changed.Add);
        await rig.DiscoveredAsync();
        var lenses = await provider.ProvideCodeLensesAsync(doc, TestContext.Current.CancellationToken);
        Assert.Equal([(4, "Run Test"), (4, "Debug Test"), (7, "Run Test"), (7, "Debug Test")], lenses!.Select(l => (l.Range.Start.Line, l.Command!.Title)));

        var subtracts = lenses!.Single(l => l.Range.Start.Line == 7 && l.Command!.Title == "Run Test").Command!;
        Assert.True(rig.Commands.Has(subtracts.CommandId));
        await rig.Commands.ExecuteAsync(subtracts.CommandId, [.. subtracts.Arguments!.Select(a => (object?)a)]);
        Assert.Equal(["Calc.MathTests.Subtracts"], rig.Provider.Runs.Last().TestIds);
        var debug = lenses!.Single(l => l.Range.Start.Line == 4 && l.Command!.Title == "Debug Test").Command!;
        await rig.Commands.ExecuteAsync(debug.CommandId, [.. debug.Arguments!.Select(a => (object?)a)]);
        Assert.Contains(rig.Window.Messages, m => m.Message.Contains("debugger", StringComparison.Ordinal)); // debug went to the launcher (none here)

        changed.Clear();
        rig.Config.Set(TestingSettings.CodeLens, false);
        Assert.Contains(null, changed);
        Assert.Null(await provider.ProvideCodeLensesAsync(doc, TestContext.Current.CancellationToken));
        Assert.Contains(rig.Config.Properties, p => p.Key == TestingSettings.CodeLens && p.Description is { Length: > 0 });
    }

    [Fact]
    public async Task A_group_the_provider_scans_gets_run_all_and_debug_all_lenses_that_run_its_known_tests()
    {
        using var rig = new HostRig();
        // the class line groups its two tests, and a test the explorer does not know is left out
        rig.Provider.Groups = text => [new SourceTestGroup("MathTests", 2,
            [.. rig.Provider.Scan(text), new SourceTest("Calc.MathTests.Unknown", 9, "Unknown")])];
        await rig.DiscoveredAsync();
        var doc = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource);
        var provider = Assert.Single(rig.Features.GetProviders<ICodeLensProvider>(doc));
        var lenses = await provider.ProvideCodeLensesAsync(doc, TestContext.Current.CancellationToken);
        Assert.Equal([(2, "Run All Tests"), (2, "Debug All Tests"), (4, "Run Test"), (4, "Debug Test"), (7, "Run Test"), (7, "Debug Test")],
            lenses!.Select(l => (l.Range.Start.Line, l.Command!.Title)));

        var all = lenses!.Single(l => l.Command!.Title == "Run All Tests").Command!;
        Assert.True(rig.Commands.Has(all.CommandId));
        await rig.Commands.ExecuteAsync(all.CommandId, [.. all.Arguments!.Select(a => (object?)a)]);
        Assert.Equal(["Calc.MathTests.Adds", "Calc.MathTests.Subtracts"], rig.Provider.Runs.Last().TestIds);
        var debugAll = lenses!.Single(l => l.Command!.Title == "Debug All Tests").Command!;
        await rig.Commands.ExecuteAsync(debugAll.CommandId, [.. debugAll.Arguments!.Select(a => (object?)a)]);
        Assert.Contains(rig.Window.Messages, m => m.Message.Contains("debugger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_results_leave_the_lenses_alone_and_a_change_of_the_tests_refreshes_them()
    {
        using var rig = new HostRig();
        await rig.DiscoveredAsync();
        var doc = new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource);
        var provider = Assert.Single(rig.Features.GetProviders<ICodeLensProvider>(doc));
        var lensChanges = new List<Uri?>();
        var stateChanges = 0;
        using var sub = provider.CodeLensesChanged.Subscribe(lensChanges.Add);
        using var states = rig.Host.Gutter.StatesChanged.Subscribe(_ => stateChanges++);

        // a run changes outcomes only: the glyphs refresh, the editors are not asked to re-request every code lens (the server's too)
        await rig.Commands.ExecuteAsync("testing.runAll");
        rig.Host.Gutter.Invalidate();
        Assert.True(stateChanges > 0);
        Assert.Empty(lensChanges);

        // a test moved to another line (rediscovery): the lenses follow
        rig.Provider.Tests[rig.Project.Key][1] = FakeProvider.Test("Calc.MathTests.Subtracts", HostRig.MathFile, 9);
        await rig.Commands.ExecuteAsync(TestingHost.Refresh);
        for (var i = 0; i < 400 && lensChanges.Count == 0; i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.Contains(null, lensChanges);
    }

    [Fact]
    public async Task Run_at_cursor_runs_the_test_above_the_caret_and_current_file_runs_all_of_it()
    {
        using var rig = new HostRig();
        await rig.DiscoveredAsync();
        var doc = new FakeEditorDocument(new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp", HostRig.MathSource));
        rig.Navigation.ActiveDocument = doc;
        rig.Navigation.ActiveCaret = new TextPosition(8, 4); // inside Subtracts' body
        await rig.Commands.ExecuteAsync("testing.runAtCursor");
        Assert.Equal(["Calc.MathTests.Subtracts"], rig.Provider.Runs.Last().TestIds);
        rig.Navigation.ActiveCaret = new TextPosition(1, 0); // above every test: the file's tests
        await rig.Commands.ExecuteAsync("testing.runAtCursor");
        Assert.Equal(["Calc.MathTests.Adds", "Calc.MathTests.Subtracts"], rig.Provider.Runs.Last().TestIds);
        await rig.Commands.ExecuteAsync("testing.debugCurrentFile");
        Assert.Empty(rig.Provider.Debugs); // no debug service: no launcher
        Assert.Contains(rig.Window.Messages, m => m.Message.Contains("debugger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disposing_the_host_withdraws_its_commands_diagnostics_and_provider()
    {
        var rig = new HostRig();
        await rig.DiscoveredAsync();
        await rig.Commands.ExecuteAsync("testing.runAll");
        Assert.NotEmpty(rig.Diagnostics.Get(new Uri(HostRig.MathFile)));
        rig.Dispose();
        Assert.False(rig.Commands.Has("testing.runAll"));
        Assert.Empty(rig.Diagnostics.Get(new Uri(HostRig.MathFile)));
        Assert.Empty(rig.Features.GetProviders<ITestGutterProvider>(new InMemoryTextDocument(new Uri("file://" + HostRig.MathFile), "csharp")));
    }
}
