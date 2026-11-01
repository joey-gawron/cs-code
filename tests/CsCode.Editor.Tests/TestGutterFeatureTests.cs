using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The Test Explorer's glyphs in the editor (<see cref="TestGutterFeature"/> over <see cref="ITestGutterProvider"/>).</summary>
public sealed class TestGutterFeatureTests
{
    private sealed class FakeTests : ITestGutterProvider
    {
        private readonly Emitter<Uri?> _changed = new();
        public TestGutterState State { get; set; } = TestGutterState.None;
        public List<(string Id, bool Debug)> Runs { get; } = [];
        public List<string> Reveals { get; } = [];

        /// <summary>A test on every line that starts with "[Fact]"'s next line, as the explorer would map them.</summary>
        public ValueTask<IReadOnlyList<TestGutterItem>?> ProvideTestsAsync(ITextDocument document, CancellationToken ct)
        {
            var items = new List<TestGutterItem>();
            for (var i = 0; i + 1 < document.LineCount; i++)
                if (document.GetLineText(i).Trim() == "[Fact]") items.Add(new TestGutterItem(i + 1, "T" + items.Count, "Test" + items.Count, State, State == TestGutterState.Failed ? "boom" : null));
            return ValueTask.FromResult<IReadOnlyList<TestGutterItem>?>(items.Count == 0 ? null : items);
        }

        public IEventSource<Uri?> StatesChanged => _changed;
        public void Fire() => _changed.Fire(null);
        public ValueTask RunAsync(string testId, bool debug, CancellationToken ct = default) { Runs.Add((testId, debug)); return ValueTask.CompletedTask; }
        public ValueTask RevealAsync(string testId, CancellationToken ct = default) { Reveals.Add(testId); return ValueTask.CompletedTask; }
    }

    private const string Source = "class C\n{\n    [Fact]\n    void A() { }\n\n    [Fact]\n    void B() { }\n}\n";

    [AvaloniaFact]
    public async Task Glyphs_mark_the_test_lines_follow_edits_and_show_new_results()
    {
        using var host = ViewHost.Create();
        var tests = new FakeTests();
        host.Factory.Services.Features.Register<ITestGutterProvider>(DocumentSelector.All, tests);
        var (doc, view, _) = await host.OpenAsync(Source, "CalcTests.cs");
        var feature = view.Feature<TestGutterFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "glyphs placed (after the 300 ms settle)");
        Assert.Equal([(3, "T0"), (6, "T1")], feature.Placements.Select(p => (p.Line, p.Item.TestId)));
        ViewHost.Pump();
        Assert.Equal(TestGutterFeature.MarginWidth, feature.Margin.Bounds.Width);

        // a line inserted above: the glyphs move with their lines at once (anchored), before the provider is asked again
        view.Controller.SetSelection(TextRange.At(0, 0));
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "// header\n")]);
        Assert.Equal([4, 7], feature.Placements.Select(p => p.Line));

        // results changed: the provider says so and the glyphs show the new state
        tests.State = TestGutterState.Failed;
        tests.Fire();
        await ViewHost.WaitFor(() => feature.Placements.All(p => p.Item.State == TestGutterState.Failed), "failed state shown");
        Assert.Contains("Test0: failed", feature.Describe(4));
        Assert.Contains("boom", feature.Describe(4));
        Assert.Null(feature.Describe(0));
    }

    [AvaloniaFact]
    public async Task A_click_runs_the_test_or_debugs_it_as_set_and_the_menu_offers_every_action()
    {
        using var host = ViewHost.Create();
        var tests = new FakeTests();
        host.Factory.Services.Features.Register<ITestGutterProvider>(DocumentSelector.All, tests);
        var (_, view, _) = await host.OpenAsync(Source, "CalcTests.cs");
        var feature = view.Feature<TestGutterFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "glyphs placed");

        feature.Click(3, feature.Margin);
        Assert.Equal([("T0", false)], tests.Runs);
        host.Config.Set("testing.defaultGutterClickAction", "debug");
        feature.Click(6, feature.Margin);
        Assert.Equal(("T1", true), tests.Runs[^1]);
        feature.Click(0, feature.Margin); // no test on the line: nothing
        Assert.Equal(2, tests.Runs.Count);

        var menu = feature.BuildContextMenu(3)!;
        var items = menu.Items.OfType<Avalonia.Controls.MenuItem>().ToList();
        Assert.Equal(["Run Test", "Debug Test", "Reveal in Test Explorer"], items.Select(i => i.Header as string));
        items[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        Assert.Equal(["T0"], tests.Reveals);
    }

    [AvaloniaFact]
    public async Task No_tests_no_width_and_the_setting_turns_glyphs_off()
    {
        using var host = ViewHost.Create();
        var tests = new FakeTests();
        host.Factory.Services.Features.Register<ITestGutterProvider>(DocumentSelector.All, tests);
        var (_, plain, _) = await host.OpenAsync("class C { }\n", "Plain.cs");
        var (_, view, _) = await host.OpenAsync(Source, "CalcTests.cs");
        var feature = view.Feature<TestGutterFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "glyphs placed");
        ViewHost.Pump();
        Assert.Equal(0, plain.Feature<TestGutterFeature>()!.Margin.Bounds.Width); // a file without tests keeps its gutter as it was

        host.Config.Set("testing.gutterEnabled", false);
        await ViewHost.WaitFor(() => feature.Placements.Count == 0, "glyphs removed when disabled");
        ViewHost.Pump();
        Assert.Equal(0, feature.Margin.Bounds.Width);
    }
}
