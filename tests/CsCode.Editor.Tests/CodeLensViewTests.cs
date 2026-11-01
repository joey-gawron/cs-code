using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.Core.Debugging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>Code lens: the zones above lines, their text, clicks, resolve-when-visible, refreshes and the stable layout.</summary>
public class CodeLensViewTests
{
    internal sealed class FakeLenses(Func<ITextDocument, IReadOnlyList<CodeLens>?> provide) : ICodeLensProvider
    {
        private readonly Emitter<Uri?> _changed = new();
        public int Calls, Resolves;
        public Func<CodeLens, CodeLens>? Resolve { get; set; }
        /// <summary>When set, answers wait for it (a slow server).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return provide(document);
        }

        public ValueTask<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct)
        {
            Interlocked.Increment(ref Resolves);
            return ValueTask.FromResult(Resolve is { } r ? r(lens) : lens);
        }

        public IEventSource<Uri?> CodeLensesChanged => _changed;
        public void FireChanged(Uri? uri) => _changed.Fire(uri);
    }

    private sealed class FakeFolds(params FoldingRange[] ranges) : IFoldingRangeProvider
    {
        public ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<FoldingRange>?>(ranges);
    }

    internal static CodeLens Lens(int line, int ch, string? title, string commandId = "test.lens", params object[] args) =>
        new(TextRange.At(line, ch), title is null ? null : new Command(title, commandId, args.Select(a => JsonSerializer.SerializeToElement(a)).ToArray()));

    private const string Code = "class A\n{\n    void M()\n    {\n    }\n\n    void N() { }\n}\n";

    private static VisualLine Visual(CodeEditorControl view, int line) =>
        view.TextView.GetOrConstructVisualLine(view.Editor.Document.GetLineByNumber(line + 1));

    [AvaloniaFact]
    public async Task Lenses_open_a_zone_above_their_line_with_the_text_below_it_and_the_caret_on_the_text()
    {
        using var host = ViewHost.Create();
        var provider = new FakeLenses(_ => [Lens(2, 9, "2 references"), Lens(2, 9, "Run Test"), Lens(6, 9, "0 references", "")]);
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, provider);
        var (doc, view, window) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 3, "lenses placed");
        Assert.Equal([(2, "2 references"), (2, "Run Test"), (6, "0 references")], feature.Placements.ToArray());
        ViewHost.Pump();

        var lineHeight = view.TextView.DefaultLineHeight;
        var zone = feature.ZoneHeight;
        Assert.True(zone >= lineHeight - 1, $"zone {zone} vs line {lineHeight}"); // VS Code: the editor's line height at the default lens font
        Assert.Equal(0, LineZones.HeightOf(Visual(view, 1)));
        Assert.Equal(zone, LineZones.HeightOf(Visual(view, 2)));
        var plain = Visual(view, 1);
        var lensed = Visual(view, 2);
        Assert.Equal(plain.Height + zone, lensed.Height, 1);
        // the text row starts below the zone: the caret, and the baseline the line numbers use, follow it
        var textTop = lensed.GetTextLineVisualYPosition(lensed.TextLines[0], VisualYPosition.TextTop);
        Assert.Equal(lensed.VisualTop + zone, textTop, 1);
        Assert.Equal(textTop, LineZones.TextTop(lensed), 1);

        view.Editor.CaretOffset = doc.Buffer.GetLineByNumber(3).Offset + 4;
        Key(window, Avalonia.Input.Key.Home);
        Assert.Equal(doc.Buffer.GetLineByNumber(3).Offset, view.Editor.CaretOffset); // smart Home toggles to column 0: the zone is no caret stop
        Key(window, Avalonia.Input.Key.Left);
        Assert.Equal(doc.Buffer.GetLineByNumber(2).EndOffset, view.Editor.CaretOffset); // Left at the text start leaves the line
        Key(window, Avalonia.Input.Key.Right);
        Assert.Equal(doc.Buffer.GetLineByNumber(3).Offset, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Down);
        Key(window, Avalonia.Input.Key.Up);
        Assert.Equal(3, view.TextArea.Caret.Line);
        Key(window, Avalonia.Input.Key.Up);
        Assert.Equal(2, view.TextArea.Caret.Line); // Up from the lens line goes to the line above, not into the zone
    }

    [AvaloniaFact]
    public async Task The_zone_covers_the_selection_current_line_and_caret_and_draws_the_lens_text()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, new FakeLenses(_ => [Lens(2, 4, "2 references")]));
        var (doc, view, window) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 1, "lens placed");
        view.Editor.Select(0, doc.Buffer.GetLineByNumber(4).EndOffset); // a selection across the lens line
        ViewHost.Pump();
        var lensed = Visual(view, 2);
        var top = lensed.VisualTop - view.TextView.ScrollOffset.Y;
        var origin = view.TextView.TranslatePoint(new Point(0, top), window)!.Value;
        var frame = window.CaptureRenderedFrame()!;
        var selection = ((ISolidColorBrush)view.Palette.Selection).Color;
        var zoneRect = new PixelRect((int)origin.X, (int)Math.Ceiling(origin.Y) + 1, (int)view.TextView.Bounds.Width, (int)feature.ZoneHeight - 2);
        Assert.Equal(0, CountPixels(frame, selection, 2, zoneRect)); // the zone hides the selection AvaloniaEdit draws over the tall row
        var textRow = new PixelRect((int)origin.X, (int)(origin.Y + feature.ZoneHeight) + 1, 30, (int)view.TextView.DefaultLineHeight - 2);
        Assert.True(CountPixels(frame, selection, 2, textRow) > 0, "the text row of the lens line is selected");
        var lensColor = ((ISolidColorBrush)view.Palette.CodeLens).Color;
        Assert.True(CountPixels(frame, lensColor, 40, zoneRect) > 5, "the lens title is drawn in editorCodeLens.foreground");

        view.TextArea.ClearSelection();
        view.Editor.CaretOffset = doc.Buffer.GetLineByNumber(3).Offset + 4;
        ViewHost.Pump();
        var caretRect = view.TextArea.Caret.CalculateCaretRectangle();
        Assert.True(caretRect.Top < lensed.VisualTop + 1); // AvaloniaEdit's caret spans the tall row ...
        frame = window.CaptureRenderedFrame()!;
        var caret = ((ISolidColorBrush)view.Palette.Caret).Color;
        var caretX = (int)view.TextView.TranslatePoint(new Point(caretRect.X - view.TextView.ScrollOffset.X, 0), window)!.Value.X;
        Assert.Equal(0, CountPixels(frame, caret, 10, new PixelRect(caretX - 1, (int)Math.Ceiling(origin.Y) + 1, 3, (int)feature.ZoneHeight - 2))); // ... but is only seen on the text
    }

    [AvaloniaFact]
    public async Task A_click_on_a_lens_runs_its_command_with_its_arguments_and_a_click_in_the_zone_never_moves_the_caret()
    {
        using var host = ViewHost.Create();
        var ran = new List<object?[]>();
        host.Commands.Register("test.lens", inv => { ran.Add(inv.Args.ToArray()); return ValueTask.FromResult<object?>(null); });
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, new FakeLenses(_ => [Lens(2, 4, "Run Test", "test.lens", "Tests.A.M", 7)]));
        var (doc, view, window) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 1, "lens placed");
        ViewHost.Pump();
        view.Editor.CaretOffset = 0;

        var lensed = Visual(view, 2);
        var zoneTop = lensed.VisualTop - view.TextView.ScrollOffset.Y;
        var indentX = lensed.GetVisualPosition(lensed.GetVisualColumn(4), VisualYPosition.TextTop).X;
        var onTitle = view.TextView.TranslatePoint(new Point(indentX + 8, zoneTop + feature.ZoneHeight / 2), window)!.Value;
        window.MouseMove(onTitle);
        ViewHost.Pump();
        Assert.Equal(StandardCursorType.Hand.ToString(), CursorName(feature.Zones.Layer.Cursor)); // a link: the hand
        window.MouseDown(onTitle, MouseButton.Left);
        window.MouseUp(onTitle, MouseButton.Left);
        await ViewHost.WaitFor(() => ran.Count == 1, "command ran");
        Assert.Equal("Tests.A.M", ((JsonElement)ran[0][0]!).GetString());
        Assert.Equal(7, ((JsonElement)ran[0][1]!).GetInt32());
        Assert.Equal(0, view.Editor.CaretOffset); // the press went to the lens, not to the text

        var past = view.TextView.TranslatePoint(new Point(view.TextView.Bounds.Width - 20, zoneTop + feature.ZoneHeight / 2), window)!.Value;
        window.MouseMove(past);
        window.MouseDown(past, MouseButton.Left);
        window.MouseUp(past, MouseButton.Left);
        ViewHost.Pump();
        Assert.Single(ran);
        Assert.Equal(0, view.Editor.CaretOffset); // empty zone space is not text either
    }

    private static string CursorName(Cursor? cursor) => cursor is null ? "null" : StandardCursorType.Hand.ToString() is var hand && cursor.ToString()!.Contains("Hand") ? hand : cursor.ToString()!;

    [AvaloniaFact]
    public async Task Reference_lenses_map_onto_the_references_ui_and_unknown_commands_say_so()
    {
        using var host = ViewHost.Create();
        var shown = new List<object?[]>();
        host.Commands.Register(NavigationFeature.ShowReferencesCommand, inv => { shown.Add(inv.Args.ToArray()); return ValueTask.FromResult<object?>(null); });
        var path = host.Dir.WriteFile("b.cs", "class B { }\n");
        var other = new Uri(path).ToString();
        var uri = new Uri(System.IO.Path.Combine(host.Dir.Path, "a.cs")).ToString();
        object Loc(int line) => new { uri = other, range = new { start = new { line, character = 0 }, end = new { line, character = 1 } } };
        var pos = new { line = 2, character = 9 };
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, new FakeLenses(_ =>
        [
            Lens(2, 9, "2 references", "editor.action.showReferences", uri, pos, new[] { Loc(0), Loc(3) }),
            Lens(6, 9, "1 reference", "rust-analyzer.showReferences", uri, pos, new[] { Loc(5) }),
            Lens(6, 9, "0 references", "editor.action.showReferences", uri, pos, Array.Empty<object>()),
            Lens(7, 0, "Do it", "someServer.clientSideThing"),
        ]));
        var (_, view, _) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 4, "lenses placed");

        await feature.ClickAsync(2, 0);
        var refs = Assert.IsAssignableFrom<IReadOnlyList<Location>>(Assert.Single(shown)[0]);
        Assert.Equal([0, 3], refs.Select(r => r.Range.Start.Line));
        Assert.Equal("References to 'M'", shown[0][1]);

        await feature.ClickAsync(6, 0);
        Assert.Equal((new Uri(path), (TextRange?)TextRange.FromLines(5, 0, 5, 1)), Assert.Single(host.Navigation.Opened)); // one: opened

        await feature.ClickAsync(6, 1);
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("No references found for 'M'", StringComparison.Ordinal));

        await feature.ClickAsync(7, 0);
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("'someServer.clientSideThing' is not available", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Unresolved_lenses_are_resolved_only_near_the_viewport_and_once()
    {
        using var host = ViewHost.Create();
        var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"void F{i}() {{ }}\n"));
        var provider = new FakeLenses(_ => [Lens(1, 5, null), Lens(300, 5, null)])
        {
            Resolve = l => l with { Command = new Command($"{l.Range.Start.Line} refs", "test.lens") },
        };
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync(text, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2 && feature.Placements[0].Title is not null, "the visible lens resolved");
        await Task.Delay(300);
        ViewHost.Pump();
        Assert.Null(feature.Placements[1].Title); // far below the viewport: not asked
        Assert.Equal(1, provider.Resolves);
        Assert.True(LineZones.HeightOf(Visual(view, 1)) > 0); // an unresolved lens already holds its zone (no jump when it resolves)

        view.Controller.Reveal(TextRange.At(300, 0), center: true);
        await ViewHost.WaitFor(() => feature.Placements[1].Title == "300 refs", "resolved after scrolling to it");
        view.Controller.Reveal(TextRange.At(0, 0), center: true);
        await Task.Delay(300);
        ViewHost.Pump();
        Assert.Equal(2, provider.Resolves); // each lens once
    }

    [AvaloniaFact]
    public async Task While_typing_the_old_lenses_stay_until_the_new_ones_arrive_so_the_caret_line_does_not_move()
    {
        using var host = ViewHost.Create();
        var provider = new FakeLenses(doc => [Lens(2, 4, "2 references", "test.lens"), Lens(6, 4, null)])
        {
            Resolve = l => l with { Command = new Command($"resolved {l.Range.Start.Line}", "test.lens") },
        };
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2 && feature.Placements[1].Title == "resolved 6", "lenses placed and resolved");
        view.Editor.CaretOffset = doc.Buffer.GetLineByNumber(7).Offset + 8;
        ViewHost.Pump();
        var caretY = view.TextArea.Caret.CalculateCaretRectangle().Bottom;

        provider.Gate = new TaskCompletionSource(); // the server is slow now
        Type(view, "x");
        ViewHost.Pump();
        Assert.Equal(caretY, view.TextArea.Caret.CalculateCaretRectangle().Bottom, 1); // zones kept: nothing jumped
        Assert.Equal(2, feature.Placements.Count);
        await ViewHost.WaitFor(() => provider.Calls >= 2, "the edit asked again");
        await Task.Delay(100);
        ViewHost.Pump();
        Assert.Equal(caretY, view.TextArea.Caret.CalculateCaretRectangle().Bottom, 1);

        provider.Gate.SetResult(); // the new set arrives: line 6's lens needs resolving again, the old title shows meanwhile
        provider.Resolve = l => l with { Command = new Command("new " + l.Range.Start.Line, "test.lens") };
        await ViewHost.WaitFor(() => feature.Placements[1].Title == "new 6", "new lens resolved");
        Assert.Equal(caretY, view.TextArea.Caret.CalculateCaretRectangle().Bottom, 1);
    }

    [AvaloniaFact]
    public async Task A_new_title_waits_on_the_old_one_while_resolving()
    {
        using var host = ViewHost.Create();
        var gate = new TaskCompletionSource();
        var provider = new FakeLenses(doc => [Lens(2, 4, null)]) { Resolve = l => l with { Command = new Command("first", "test.lens") } };
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 1 && feature.Placements[0].Title == "first", "resolved");
        provider.Resolve = l => { gate.Task.Wait(TimeSpan.FromSeconds(5)); return l with { Command = new Command("second", "test.lens") }; };
        Type(view, "x");
        await ViewHost.WaitFor(() => provider.Calls >= 2 && provider.Resolves >= 2, "asked and resolving again");
        Assert.Equal("first", feature.Placements[0].Title); // shown until the new title is known
        gate.SetResult();
        await ViewHost.WaitFor(() => feature.Placements[0].Title == "second", "new title");
    }

    [AvaloniaFact]
    public async Task When_lenses_arrive_the_first_visible_line_keeps_its_place_on_screen()
    {
        using var host = ViewHost.Create();
        var text = string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\n"));
        var provider = new FakeLenses(_ => []);
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync(text, "a.txt");
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Vector(0, view.TextView.DefaultLineHeight * 50.5); // line 51 half scrolled off
        ViewHost.Pump();
        var first = view.TextView.VisualLines[0];
        var line = first.FirstDocumentLine.LineNumber;
        Assert.True(line > 40);
        var screenY = LineZones.TextTop(first) - view.TextView.ScrollOffset.Y;

        var lenses = Enumerable.Range(0, line + 5).Select(i => Lens(i, 0, "lens " + i)).ToArray(); // above it, on it, and below it
        var feature = view.Feature<CodeLensFeature>()!;
        var current = new FakeLenses(_ => lenses);
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, current); // a provider arriving re-queries at once
        await ViewHost.WaitFor(() => feature.Placements.Count == lenses.Length, "lenses placed");
        ViewHost.Pump();
        var again = view.TextView.VisualLines.First(v => v.FirstDocumentLine.LineNumber == line);
        Assert.Equal(screenY, LineZones.TextTop(again) - view.TextView.ScrollOffset.Y, 1);
    }

    [AvaloniaFact]
    public async Task Settings_turn_lenses_off_and_size_their_font_and_refreshes_ask_again()
    {
        using var host = ViewHost.Create();
        var titles = "one";
        var provider = new FakeLenses(_ => [Lens(2, 4, titles)]);
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 1, "lens placed");
        var normal = feature.ZoneHeight;
        var calls = provider.Calls;
        host.Config.Set("editor.tabSize", 2);
        await Task.Delay((int)CodeLensFeature.Delay.TotalMilliseconds + 200);
        Assert.Equal(calls, provider.Calls); // another setting changing does not ask the server again

        host.Config.Set(LanguageRenderSettings.CodeLensFontSizeKey, 30);
        await ViewHost.WaitFor(() => feature.ZoneHeight > normal + 10, "a bigger lens font makes a taller zone");
        ViewHost.Pump();
        Assert.Equal(feature.ZoneHeight, LineZones.HeightOf(Visual(view, 2)));

        titles = "two";
        provider.FireChanged(view.Document.Uri); // the provider says its lenses changed (the Test Explorer after discovery)
        await ViewHost.WaitFor(() => feature.Placements.SingleOrDefault().Title == "two", "re-asked on the provider's event");

        titles = "three";
        var token = host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.ForLanguages(["nothing"]), new FakeLenses(_ => null));
        await ViewHost.WaitFor(() => feature.Placements.SingleOrDefault().Title == "three", "re-asked when providers change (workspace/codeLens/refresh re-registers)");
        token.Dispose();

        host.Config.Set(LanguageRenderSettings.CodeLensKey, false);
        await ViewHost.WaitFor(() => feature.Placements.Count == 0, "editor.codeLens off removes them");
        ViewHost.Pump();
        Assert.Equal(0, LineZones.HeightOf(Visual(view, 2)));
    }

    [AvaloniaFact]
    public async Task A_lens_inside_a_folded_region_shows_no_zone_and_lenses_follow_edits()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, new FakeLenses(_ => [Lens(2, 4, "outer"), Lens(3, 4, "inner")]));
        host.Factory.Services.Features.Register<IFoldingRangeProvider>(DocumentSelector.All, new FakeFolds(new FoldingRange(2, 5)));
        var (doc, view, _) = await host.OpenAsync("class A\n{\n    void M()\n    void Inner()\n    {\n    }\n}\n", "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "lenses placed");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.Ranges.Contains((2, 5)), "the fold from line 3 to 6");
        view.Controller.SetSelection(TextRange.At(2, 6));
        folding.Fold();
        ViewHost.Pump();
        Assert.True(LineZones.HeightOf(Visual(view, 2)) > 0); // the fold's first line keeps its lens
        Assert.DoesNotContain(view.TextView.VisualLines, v => v.FirstDocumentLine.LineNumber == 4); // the hidden line has no visual line, so no zone
        Assert.Equal(view.TextView.DefaultLineHeight + feature.ZoneHeight, Visual(view, 2).Height, 1); // one zone, not two
        folding.Unfold();
        ViewHost.Pump();
        Assert.True(LineZones.HeightOf(Visual(view, 3)) > 0);

        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "// a\n// b\n")]);
        Assert.Equal([(4, "outer"), (5, "inner")], feature.Placements.ToArray()); // anchored: moved with the text at once
    }

    [AvaloniaFact]
    public async Task The_breakpoint_glyph_of_a_lens_line_sits_on_its_text_not_in_the_zone()
    {
        var debug = new FakeDebugService();
        using var host = ViewHost.Create(s => s.AddSingleton<IDebugService>(debug));
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, new FakeLenses(_ => [Lens(2, 4, "2 references")]));
        var (doc, view, window) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => feature.Placements.Count == 1, "lens placed");
        debug.Manager.Add(doc.Uri, 2);
        await ViewHost.WaitFor(() => view.Feature<DebugFeature>()!.BreakpointAt(2) is not null, "breakpoint shown");
        ViewHost.Pump();
        var margin = view.TextArea.LeftMargins[0];
        var lensed = Visual(view, 2);
        var zoneTop = margin.TranslatePoint(new Point(0, lensed.VisualTop - view.TextView.ScrollOffset.Y), window)!.Value;
        var red = ((ISolidColorBrush)view.Palette.BreakpointFill).Color;
        var frame = window.CaptureRenderedFrame()!;
        var inZone = CountPixels(frame, red, 12, new PixelRect((int)zoneTop.X, (int)zoneTop.Y, (int)margin.Bounds.Width, (int)feature.ZoneHeight - 1));
        var onText = CountPixels(frame, red, 12, new PixelRect((int)zoneTop.X, (int)(zoneTop.Y + feature.ZoneHeight), (int)margin.Bounds.Width, (int)view.TextView.DefaultLineHeight));
        Assert.True(onText > 20, $"the glyph is on the text row ({onText})");
        Assert.True(inZone < 3, $"nothing of it in the zone ({inZone})");
    }
}
