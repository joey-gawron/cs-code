using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>MUST 9 (diagnostics + hover): squiggles, lookups, versioned updates, hover content, dwell, stale suppression and slow providers — in a real control.</summary>
public class DiagnosticsHoverTests
{
    private static Diagnostic Diag(int line, int from, int to, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error, IReadOnlyList<DiagnosticTag>? tags = null) =>
        new(TextRange.FromLines(line, from, line, to), message, severity, Tags: tags, Source: "test", Code: "T1");

    private static DiagnosticsFeature Diagnostics(CodeEditorControl v) => v.Feature<DiagnosticsFeature>()!;
    private static HoverFeature Hover(CodeEditorControl v) => v.Feature<HoverFeature>()!;

    // ---- diagnostics ----

    [AvaloniaFact]
    public async Task Diagnostics_are_drawn_as_wavy_underlines_in_the_theme_colour_and_looked_up_by_position()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("int x = 1;\nint yy = 2;\n", "a.txt");
        var before = window.CaptureRenderedFrame();
        var errorColor = view.Palette.Error.Brush is Avalonia.Media.ISolidColorBrush b ? b.Color : default;
        var baseline = CountPixels(before!, errorColor, 70);

        view.Services.Diagnostics.Set(doc.Uri, "t", [Diag(1, 4, 6, "yy is wrong")]);
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 1, "diagnostic applied");
        ViewHost.Pump();

        var offset = doc.OffsetAt(new TextPosition(1, 5));
        Assert.Equal("yy is wrong", Diagnostics(view).At(offset).Single().Message);
        Assert.Empty(Diagnostics(view).At(doc.OffsetAt(new TextPosition(0, 1))));
        var after = window.CaptureRenderedFrame();
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        using (var fs = File.Create(Path.Combine(dir, "editor-squiggle.png"))) after!.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Assert.True(CountPixels(after!, errorColor, 70) > baseline + 6, $"a wavy line in the error colour must be on screen (baseline {baseline}, color {errorColor})");
    }

    [AvaloniaFact]
    public async Task A_diagnostic_stays_on_its_text_while_the_user_types_before_it()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("alpha\nbeta gamma\n", "a.txt");
        view.Services.Diagnostics.Set(doc.Uri, "t", [Diag(1, 5, 10, "gamma")]);
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 1, "applied");

        view.Controller.SetSelection(TextRange.At(0, 0));
        Type(view, "// a long comment\n");
        ViewHost.Pump();
        var hit = Diagnostics(view).At(doc.OffsetAt(new TextPosition(2, 7))).SingleOrDefault();
        Assert.NotNull(hit);
        Assert.Equal("gamma", doc.GetText(TextRange.FromLines(2, 5, 2, 10)));
        Assert.Empty(Diagnostics(view).At(doc.OffsetAt(new TextPosition(0, 3)))); // the new comment is not under the squiggle
    }

    [AvaloniaFact]
    public async Task The_latest_set_wins_and_a_flood_is_capped_and_never_wedges_the_view()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(string.Join('\n', Enumerable.Range(0, 3000).Select(i => "line " + i)), "a.txt");
        // a hostile source: 200 publications of ever larger lists in a tight loop, from another thread, while the UI keeps going
        var flood = Task.Run(() =>
        {
            for (var round = 1; round <= 200; round++)
            {
                var list = Enumerable.Range(0, Math.Min(3000, round * 15)).Select(i => Diag(i, 0, 4, "d" + round)).ToList();
                view.Services.Diagnostics.Set(doc.Uri, "flood", list);
            }
            view.Services.Diagnostics.Set(doc.Uri, "flood", Enumerable.Range(0, 3000).Select(i => Diag(i, 0, 4, "final")).ToList());
            view.Services.Diagnostics.Set(doc.Uri, "second", Enumerable.Range(0, 3000).Select(i => Diag(i, 5, 6, "second")).ToList());
        });
        await ViewHost.WaitFor(() => flood.IsCompleted, "flood finished");
        await ViewHost.WaitFor(() => Diagnostics(view).Count == DiagnosticsFeature.MaxDiagnostics, "capped batch applied");
        Assert.Equal(DiagnosticsFeature.MaxDiagnostics, Diagnostics(view).Count);
        Assert.All(Diagnostics(view).At(doc.OffsetAt(new TextPosition(0, 1))), d => Assert.Equal("final", d.Message));
        Type(view, "x"); // still alive
        Assert.StartsWith("x", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Clearing_the_diagnostics_removes_the_squiggles()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("one two", "a.txt");
        view.Services.Diagnostics.Set(doc.Uri, "t", [Diag(0, 0, 3, "x")]);
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 1, "applied");
        view.Services.Diagnostics.Clear("t");
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 0, "cleared");
    }

    [AvaloniaFact]
    public async Task Unnecessary_code_is_faded_and_hints_use_the_hint_colour()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("using System;\nclass A {}\n", "a.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 2, "tokens");
        view.Services.Diagnostics.Set(doc.Uri, "t", [Diag(0, 0, 13, "unused", DiagnosticSeverity.Hint, [DiagnosticTag.Unnecessary])]);
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 1, "applied");
        ViewHost.Pump();
        var d = Diagnostics(view).At(doc.OffsetAt(new TextPosition(0, 2))).Single();
        Assert.Equal(DiagnosticSeverity.Hint, d.Severity);
        Assert.Contains(DiagnosticTag.Unnecessary, d.Tags!);
        Assert.NotNull(window.CaptureRenderedFrame());
    }

    // ---- hover ----

    private static Hover Md(string text) => new([MarkupContent.Markdown(text)]);

    [AvaloniaFact]
    public async Task Hover_shows_markdown_from_every_provider_merged()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider((_, _, _) => Task.FromResult<Hover?>(Md("**Console** class\n\n```csharp\npublic static class Console\n```"))));
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider((_, _, _) => Task.FromResult<Hover?>(Md("Second provider says hi"))));
        var (_, view, _) = await host.OpenAsync("Console.WriteLine();", "a.cs");
        await Hover(view).HoverAtAsync(3);
        ViewHost.Pump();
        Assert.True(Hover(view).IsOpen);
        var text = TextOf(Hover(view).PopupRoot);
        Assert.Contains("Console", text);
        Assert.Contains("public static class Console", text);
        Assert.Contains("Second provider says hi", text);
    }

    [AvaloniaFact]
    public async Task Hover_over_a_squiggle_shows_the_message_and_a_quick_fix_link()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("int yy = 2;\n", "a.txt");
        view.Services.Diagnostics.Set(doc.Uri, "t", [Diag(0, 4, 6, "'yy' is never used", DiagnosticSeverity.Warning)]);
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 1, "applied");
        await Hover(view).HoverAtAsync(5);
        ViewHost.Pump();
        var text = TextOf(Hover(view).PopupRoot);
        Assert.Contains("'yy' is never used", text);
        Assert.Contains("test (T1)", text);
        Assert.Contains("Quick Fix", text);
    }

    [AvaloniaFact]
    public async Task Nothing_to_say_means_no_popup()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider((_, _, _) => Task.FromResult<Hover?>(null)));
        var (_, view, _) = await host.OpenAsync("abc", "a.txt");
        await Hover(view).HoverAtAsync(1);
        Assert.False(Hover(view).IsOpen);
    }

    [AvaloniaFact]
    public async Task A_hover_whose_text_changed_meanwhile_is_dropped_not_shown()
    {
        using var host = ViewHost.Create();
        var gate = new TaskCompletionSource();
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider(async (_, _, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return Md("stale answer");
        }));
        var (doc, view, _) = await host.OpenAsync("abc def", "a.txt");
        var shown = Hover(view).HoverAtAsync(1);
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "x")]); // the text moves on while the provider thinks
        gate.SetResult();
        await shown;
        ViewHost.Pump();
        Assert.False(Hover(view).IsOpen);
    }

    [AvaloniaFact]
    public async Task A_hung_hover_provider_costs_only_the_deadline_and_the_diagnostic_still_shows()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }));
        var (doc, view, _) = await host.OpenAsync("int yy = 2;\n", "a.txt");
        view.Services.Diagnostics.Set(doc.Uri, "t", [Diag(0, 4, 6, "still shown")]);
        await ViewHost.WaitFor(() => Diagnostics(view).Count == 1, "applied");
        var started = Environment.TickCount64;
        var hover = Hover(view).HoverAtAsync(5);
        // the UI keeps pumping while the provider hangs
        while (!hover.IsCompleted)
        {
            ViewHost.Pump();
            await Task.Delay(20);
            Assert.True(Environment.TickCount64 - started < 6000, "the hover must give up on a hung provider");
        }
        Assert.Contains("still shown", TextOf(Hover(view).PopupRoot));
    }

    [AvaloniaFact]
    public async Task Dwelling_on_a_word_opens_the_hover_and_leaving_closes_it()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.hover.delay", 40);
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider((_, _, _) => Task.FromResult<Hover?>(Md("dwell hover"))));
        var (_, view, window) = await host.OpenAsync("hello world", "a.txt");
        ViewHost.Pump();
        window.MouseMove(PointOf(window, view, 2));
        await ViewHost.WaitFor(() => Hover(view).IsOpen, "hover opens after the dwell");
        Assert.Contains("dwell hover", TextOf(Hover(view).PopupRoot));
        window.MouseMove(new Point(800, 580)); // well away from the text and the popup
        await ViewHost.WaitFor(() => !Hover(view).IsOpen, "hover closes after the pointer left");
    }

    [AvaloniaFact]
    public async Task Hover_on_a_collapsed_region_shows_the_hidden_code()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("void F()\n{\n    hidden_line();\n}\n", "a.cs");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount > 0, "fold ranges");
        view.Controller.SetSelection(TextRange.At(1, 0));
        folding.Fold();
        ViewHost.Pump();
        var headerEnd = doc.OffsetAt(new TextPosition(1, 1));
        Assert.True(folding.FoldedStartLines.Count > 0, $"a region must be folded (ranges: {folding.RangeCount})");
        Assert.Contains("hidden_line", folding.FoldedTextAt(headerEnd) ?? $"<null at {headerEnd}; folded lines {string.Join(",", folding.FoldedStartLines)}>");
        await Hover(view).HoverAtAsync(headerEnd);
        ViewHost.Pump(); // let the popup's templates apply
        Assert.True(Hover(view).IsOpen, "the hover must be open");
        Assert.Contains("hidden_line", TextOf(Hover(view).PopupRoot));
    }

    [AvaloniaFact]
    public async Task While_stopped_in_the_debugger_hover_evaluates_the_expression_under_the_pointer()
    {
        var debug = new FakeDebugService();
        using var host = ViewHost.Create(s => s.AddSingleton<CsCode.Core.Debugging.IDebugService>(debug));
        var (doc, view, _) = await host.OpenAsync("var counter = 41 + 1;\n", "a.txt");
        debug.Stop(doc.Uri, 0);
        debug.Session!.Evaluate = (e, _, _) => new CsCode.Core.Debugging.DebugEvaluateResult("42", "int");
        await Hover(view).HoverAtAsync(6);
        ViewHost.Pump();
        var text = TextOf(Hover(view).PopupRoot);
        Assert.Contains("counter = 42", text);
        Assert.Contains("int", text);
        Assert.Equal(("counter", "hover"), debug.Session.Evaluations.Single());
    }

    [AvaloniaFact]
    public async Task Not_stopped_means_no_evaluation()
    {
        var debug = new FakeDebugService();
        using var host = ViewHost.Create(s => s.AddSingleton<CsCode.Core.Debugging.IDebugService>(debug));
        var (_, view, _) = await host.OpenAsync("var counter = 41;\n", "a.txt");
        await Hover(view).HoverAtAsync(6);
        Assert.False(Hover(view).IsOpen);
    }
}
