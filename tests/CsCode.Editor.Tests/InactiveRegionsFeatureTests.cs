using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Code the preprocessor skips (stage 3: clangd's inactive regions) drawn faded (<see cref="InactiveRegionsFeature"/>).</summary>
public sealed class InactiveRegionsFeatureTests
{
    private sealed class Regions : IInactiveRegionsProvider
    {
        private readonly Emitter<Uri> _changed = new();
        public IReadOnlyList<TextRange> Current = [];
        public Uri? For;
        public IEventSource<Uri> Changed => _changed;
        public IReadOnlyList<TextRange> GetInactiveRegions(Uri document) => document == For ? Current : [];
        public void Report(Uri document, IReadOnlyList<TextRange> regions)
        {
            For = document;
            Current = regions;
            // as a server's notification arrives: off the UI thread
            Task.Run(() => _changed.Fire(document)).Wait();
        }
    }

    private const string Source = "#if 0\nint dead;\n#endif\nint live;\n";

    private static IBrush? BrushOfLine(CsCode.Editor.View.CodeEditorControl view, int line) =>
        view.TextView.VisualLines.Single(l => l.FirstDocumentLine.LineNumber == line + 1).TextLines[0].TextRuns.OfType<ShapedTextRun>().First().Properties.ForegroundBrush;

    [AvaloniaFact]
    public async Task Reported_regions_are_drawn_faded_follow_edits_and_go_when_reported_empty()
    {
        using var host = ViewHost.Create();
        var regions = new Regions();
        host.Factory.Services.Features.Register<IInactiveRegionsProvider>(DocumentSelector.All, regions);
        var (doc, view, _) = await host.OpenAsync(Source, "main.c");
        var feature = view.Feature<InactiveRegionsFeature>()!;
        Assert.Empty(feature.Regions);

        regions.Report(doc.Uri, [TextRange.FromLines(1, 0, 1, 9)]);
        await ViewHost.WaitFor(() => feature.Regions.Count == 1, "the region placed");
        ViewHost.Pump();
        Assert.Equal(InactiveRegionsFeature.Opacity, BrushOfLine(view, 1)!.Opacity);
        Assert.NotEqual(InactiveRegionsFeature.Opacity, BrushOfLine(view, 3)?.Opacity);

        // a line typed above: the faded text moves with it until the next report
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "// header\n")]);
        var (start, end) = Assert.Single(feature.Regions);
        Assert.Equal("int dead;", doc.GetText()[start..end]);

        regions.Report(doc.Uri, []);
        await ViewHost.WaitFor(() => feature.Regions.Count == 0, "no region after an empty report");
    }

    [AvaloniaFact]
    public async Task Another_documents_report_changes_nothing_here()
    {
        using var host = ViewHost.Create();
        var regions = new Regions();
        host.Factory.Services.Features.Register<IInactiveRegionsProvider>(DocumentSelector.All, regions);
        var (_, view, _) = await host.OpenAsync(Source, "main.c");
        var feature = view.Feature<InactiveRegionsFeature>()!;
        regions.Report(new Uri("file:///elsewhere/other.c"), [TextRange.FromLines(1, 0, 1, 9)]);
        ViewHost.Pump();
        Assert.Empty(feature.Regions);
    }
}
