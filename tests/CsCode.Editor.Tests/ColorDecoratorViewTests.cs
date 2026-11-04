using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// Colour decorators and the colour picker (docs/EDITOR.md §7g) over a provider that answers like vscode-css-language-server: the CSS
/// colours of the text, and rgb / hsl / hex presentations whose edit replaces the colour's range.
/// </summary>
public class ColorDecoratorViewTests
{
    /// <summary>A colour provider: by default it finds the CSS colours of the text, as the CSS server does.</summary>
    private sealed class FakeColors : IDocumentColorProvider
    {
        private int _calls, _presentationCalls;
        public int Calls => Volatile.Read(ref _calls);
        public int PresentationCalls => Volatile.Read(ref _presentationCalls);
        public Func<ITextDocument, CancellationToken, Task<IReadOnlyList<ColorInformation>?>> Colors { get; set; } = (d, _) => Task.FromResult<IReadOnlyList<ColorInformation>?>(Css(d));
        public Func<ITextDocument, ColorInformation, IReadOnlyList<ColorPresentation>?> Presentations { get; set; } = CssPresentations;

        public async ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return await Colors(document, ct).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<ColorPresentation>?> ProvideColorPresentationsAsync(ITextDocument document, ColorInformation color, CancellationToken ct)
        {
            Interlocked.Increment(ref _presentationCalls);
            return ValueTask.FromResult(Presentations(document, color));
        }
    }

    private static IReadOnlyList<ColorInformation> Css(ITextDocument doc)
    {
        var text = doc.GetText();
        var found = new List<ColorFormats.Found>();
        ColorFormats.Find(text, 100_000, found);
        return [.. found.Select(f => new ColorInformation(new TextRange(doc.PositionAt(f.Start), doc.PositionAt(f.Start + f.Length)), f.R, f.G, f.B, f.A))];
    }

    private static IReadOnlyList<ColorPresentation> CssPresentations(ITextDocument doc, ColorInformation c) =>
        [.. ColorFormats.Presentations(c.Red, c.Green, c.Blue, c.Alpha).Select(l => new ColorPresentation(l, new TextEdit(c.Range, l)))];

    private const string Sheet = ".a { color: #ff0000; background: rgba(0, 0, 255, 0.5); }\n.b { color: hsl(120, 100%, 50%); }\n";

    private static async Task<(ViewHost Host, EditorDocument Doc, CodeEditorControl View, Window Window, ColorDecoratorFeature Feature)> OpenAsync(
        string text = Sheet, string file = "a.css", FakeColors? provider = null, Action<ViewHost>? configure = null, bool waitForColours = true)
    {
        var host = ViewHost.Create();
        configure?.Invoke(host);
        if (provider is not null) host.Factory.Services.Features.Register<IDocumentColorProvider>(DocumentSelector.All, provider);
        var (doc, view, window) = await host.OpenAsync(text, file);
        var feature = view.Feature<ColorDecoratorFeature>()!;
        if (waitForColours) await ViewHost.WaitFor(() => feature.Placements.Count > 0, "colours placed");
        ViewHost.Pump();
        return (host, doc, view, window, feature);
    }

    private static List<ColorDecoratorFeature.SwatchElement> Swatches(CodeEditorControl view, int line = 0)
    {
        ViewHost.Pump();
        return [.. view.TextView.GetOrConstructVisualLine(view.Editor.Document.GetLineByNumber(line + 1)).Elements.OfType<ColorDecoratorFeature.SwatchElement>()];
    }

    private static Point Center(Control control, Window window) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static Point At(Control control, Window window, double fx, double fy) => control.TranslatePoint(new Point(control.Bounds.Width * fx, control.Bounds.Height * fy), window)!.Value;

    private static void Click(Window window, Point p)
    {
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        ViewHost.Pump();
    }

    private static async Task<ColorPickerSession> ClickSwatchAsync(ColorDecoratorFeature feature, CodeEditorControl view, Window window, int index = 0)
    {
        var swatch = Swatches(view)[index].Element;
        Click(window, Center(swatch, window));
        await ViewHost.WaitFor(() => feature.Session is not null, "the picker opened");
        await Settle(feature.Session!);
        return feature.Session!;
    }

    private static async Task Settle(ColorPickerSession session)
    {
        for (var i = 0; i < 3; i++)
        {
            await session.Pending;
            ViewHost.Pump();
        }
    }

    // ---- decorators ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Each_colour_gets_a_swatch_in_front_of_its_text_that_follows_edits()
    {
        var provider = new FakeColors();
        var (host, doc, view, _, feature) = await OpenAsync(provider: provider);
        using var _h = host;
        await ViewHost.WaitFor(() => feature.Placements.Count == 3, "three colours");
        int[] offsets = [Sheet.IndexOf("#ff0000", StringComparison.Ordinal), Sheet.IndexOf("rgba(", StringComparison.Ordinal), Sheet.IndexOf("hsl(", StringComparison.Ordinal)];
        Assert.Equal(offsets, feature.Placements.Select(p => p.Offset));

        var first = Swatches(view);
        Assert.Equal([offsets[0], offsets[1]], first.Select(s => s.RelativeTextOffset));
        Assert.Equal(Colors.Red, first[0].Color);
        Assert.Equal(Color.FromArgb(128, 0, 0, 255), first[1].Color);
        var second = Swatches(view, 1);
        Assert.Equal(offsets[2] - doc.Buffer.GetLineByNumber(2).Offset, Assert.Single(second).RelativeTextOffset);
        Assert.Equal(Color.FromRgb(0, 255, 0), second[0].Color);
        Assert.Equal(0, first[0].DocumentLength); // no text of its own

        // typing before a colour moves its swatch at once, before any new answer
        view.Controller.SetSelection(TextRange.At(0, 0));
        Type(view, "xy");
        Assert.Equal(offsets.Select(o => o + 2), feature.Placements.Select(p => p.Offset));
        Assert.Equal([offsets[0] + 2, offsets[1] + 2], Swatches(view).Select(s => s.RelativeTextOffset));
        // typing right in front of a colour keeps the swatch in front of it
        var at = doc.PositionAt(offsets[0] + 2);
        view.Controller.SetSelection(new TextRange(at, at));
        Type(view, "z");
        Assert.Equal(offsets[0] + 3, feature.Placements[0].Offset);
        Assert.Equal("#ff0000", doc.GetText().Substring(feature.Placements[0].Offset, 7));
    }

    [AvaloniaFact]
    public async Task The_swatch_is_a_bordered_square_of_its_colour_scaled_with_the_font_and_over_a_checkerboard_when_translucent()
    {
        var (host, _, view, window, _) = await OpenAsync(".a { color: #ff0000; } .b { color: #0000ff80; }\n", provider: new FakeColors());
        using var _h = host;
        var swatches = Swatches(view);
        var red = (ColorSwatch)swatches[0].Element;
        var translucent = (ColorSwatch)swatches[1].Element;
        Assert.Equal(Math.Round(0.8 * view.Editor.FontSize), red.Bounds.Width, 0.5);
        Assert.Equal(red.Bounds.Width, red.Bounds.Height, 0.5);
        // its bottom on the text's baseline, 0.2 em from the text on each side
        var vl = view.TextView.GetOrConstructVisualLine(view.Editor.Document.GetLineByNumber(1));
        var baseline = vl.GetTextLineVisualYPosition(vl.TextLines[0], AvaloniaEdit.Rendering.VisualYPosition.Baseline) - view.TextView.ScrollOffset.Y;
        Assert.Equal(baseline, red.Bounds.Bottom, 1.0);
        var textX = vl.GetVisualPosition(swatches[0].VisualColumn + 1, AvaloniaEdit.Rendering.VisualYPosition.Baseline).X - view.TextView.ScrollOffset.X;
        Assert.Equal(red.Bounds.Right + 0.2 * view.Editor.FontSize, textX, 1.0);

        var frame = window.CaptureRenderedFrame()!;
        var scale = frame.PixelSize.Width / window.Bounds.Width;
        PixelRect Region(Control c) { var p = c.TranslatePoint(default, window)!.Value; return new PixelRect((int)(p.X * scale), (int)(p.Y * scale), (int)Math.Ceiling(c.Bounds.Width * scale), (int)Math.Ceiling(c.Bounds.Height * scale)); }
        var redRegion = Region(red);
        Assert.True(CountPixels(frame, Colors.Red, 10, redRegion) > redRegion.Width * redRegion.Height / 3, "the red square is drawn");
        Assert.True(CountPixels(frame, Color.FromRgb(0xEE, 0xEE, 0xEE), 12, redRegion) > 0, "with a light border in a dark theme");
        // half-transparent blue over white and over light grey: two different blues
        var blueRegion = Region(translucent);
        Assert.True(CountPixels(frame, Color.FromRgb(0x7F, 0x7F, 0xFF), 12, blueRegion) > 0, "blue over the white squares");
        Assert.True(CountPixels(frame, Color.FromRgb(0x66, 0x66, 0xE5), 12, blueRegion) > 0, "blue over the grey squares");

        host.Config.Set("editor.fontSize", 28);
        ViewHost.Pump();
        Assert.Equal(Math.Round(0.8 * 28), ((ColorSwatch)Swatches(view)[0].Element).Bounds.Width, 0.5);
    }

    [AvaloniaFact]
    public async Task The_caret_passes_a_swatch_with_one_key_each_way()
    {
        var (host, _, view, window, _) = await OpenAsync("a: #fff;\n#000 b\n", provider: new FakeColors());
        using var _h = host;
        Assert.Equal(2, Swatches(view).Count + Swatches(view, 1).Count);
        view.Editor.CaretOffset = 2; // "a:|"
        Key(window, Avalonia.Input.Key.Right); // " " -> before the swatch
        Assert.Equal(3, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Right); // past the swatch and '#'
        Assert.Equal(4, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Left); // back before '#', after the swatch
        Assert.Equal(3, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Left); // past the swatch to before ' '
        Assert.Equal(2, view.Editor.CaretOffset);

        // a colour at a line start: Left from after its swatch goes to the line above, Right from the line's end comes back
        var line2 = "a: #fff;\n".Length;
        view.Editor.CaretOffset = line2 + 1; // after '#'
        Key(window, Avalonia.Input.Key.Left);
        Assert.Equal(line2, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Left);
        Assert.Equal(line2 - 1, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Right);
        Assert.Equal(line2, view.Editor.CaretOffset);
        Key(window, Avalonia.Input.Key.Right);
        Assert.Equal(line2 + 1, view.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task An_answer_for_older_text_is_dropped()
    {
        var gate = new TaskCompletionSource();
        var provider = new FakeColors();
        var first = true;
        provider.Colors = async (d, ct) =>
        {
            var colours = Css(d); // ranges of the text as it is now
            if (first)
            {
                first = false;
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return colours;
        };
        var (host, doc, view, _, feature) = await OpenAsync(".a { color: #ff0000; }\n", provider: provider, waitForColours: false);
        using var _h = host;
        await ViewHost.WaitFor(() => provider.Calls == 1, "the first request");
        view.Controller.SetSelection(TextRange.At(0, 0));
        Type(view, "/* x */"); // the text moves on while the provider works on the old one
        gate.SetResult();
        await feature.Pending;
        ViewHost.Pump();
        Assert.Empty(feature.Placements); // never drawn where the colour USED to be
        await ViewHost.WaitFor(() => feature.Placements.Count == 1, "the answer for the current text");
        Assert.Equal(doc.GetText().IndexOf("#ff0000", StringComparison.Ordinal), feature.Placements[0].Offset);
        Assert.True(provider.Calls >= 2);
    }

    [AvaloniaFact]
    public async Task The_limit_keeps_the_first_colours_and_applies_live()
    {
        var text = string.Concat(Enumerable.Range(0, 20).Select(i => $".c{i} {{ color: #{i:x2}0000; }}\n"));
        var (host, _, _, _, feature) = await OpenAsync(text, provider: new FakeColors(), configure: h => h.Config.Set(ColorDecoratorFeature.LimitKey, 5));
        using var _h = host;
        await ViewHost.WaitFor(() => feature.Placements.Count == 5, "five colours");
        Assert.Equal(Enumerable.Range(0, 5).Select(i => text.IndexOf($"#{i:x2}0000", StringComparison.Ordinal)), feature.Placements.Select(p => p.Offset));
        host.Config.Set(ColorDecoratorFeature.LimitKey, 7);
        await ViewHost.WaitFor(() => feature.Placements.Count == 7, "seven after the limit changed");
        host.Config.Set(ColorDecoratorFeature.LimitKey, 1_000_000); // VS Code's maximum: read as this editor's ceiling
        await ViewHost.WaitFor(() => feature.Placements.Count == 20, "all twenty");
    }

    [AvaloniaFact]
    public async Task The_setting_turns_the_decorators_off_and_on_live()
    {
        var (host, _, view, _, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        host.Config.Set(ColorDecoratorFeature.EnabledKey, false);
        await ViewHost.WaitFor(() => feature.Placements.Count == 0, "no colours");
        Assert.Empty(Swatches(view));
        host.Config.Set(ColorDecoratorFeature.EnabledKey, true);
        await ViewHost.WaitFor(() => feature.Placements.Count == 3, "colours back");
        Assert.Equal(2, Swatches(view).Count);
        // per language, as VS Code's language-overridable setting
        host.Config.Set("[css]", System.Text.Json.JsonDocument.Parse("""{"editor.colorDecorators": false}""").RootElement.Clone());
        await ViewHost.WaitFor(() => feature.Placements.Count == 0, "off for CSS");
    }

    [AvaloniaFact]
    public async Task A_hung_or_throwing_provider_costs_nothing_and_the_others_still_show()
    {
        var hung = new FakeColors { Colors = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); return null; } };
        var throwing = new FakeColors { Colors = (_, _) => throw new InvalidOperationException("provider bug") };
        var (host, _, view, _, feature) = await OpenAsync(waitForColours: false, configure: h =>
        {
            h.Factory.Services.Features.Register<IDocumentColorProvider>(DocumentSelector.All, hung);
            h.Factory.Services.Features.Register<IDocumentColorProvider>(DocumentSelector.All, throwing);
            h.Factory.Services.Features.Register<IDocumentColorProvider>(DocumentSelector.All, new FakeColors());
        });
        using var _h = host;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ViewHost.WaitFor(() => feature.Placements.Count == 3, "the good provider's colours");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"not held up by the hung provider's deadline: {sw.Elapsed}");
        view.Controller.SetSelection(TextRange.At(1, 0));
        var typing = System.Diagnostics.Stopwatch.StartNew();
        Type(view, "abc");
        Assert.True(typing.ElapsedMilliseconds < 500, $"typing waited: {typing.ElapsedMilliseconds} ms");
        Assert.StartsWith("abc.b", view.Document.GetLineText(1));
        await Task.Delay(700);
        ViewHost.Pump();
        Assert.Equal(3, feature.Placements.Count);
    }

    [AvaloniaFact]
    public async Task Large_file_mode_has_no_colour_decorators()
    {
        using var host = ViewHost.Create();
        host.Config.Set(EditorSettingKeys.LargeFileThresholdBytes, 1000);
        host.Factory.Services.Features.Register<IDocumentColorProvider>(DocumentSelector.All, new FakeColors());
        var (doc, view, _) = await host.OpenAsync(string.Concat(Enumerable.Repeat(".a { color: #ff0000; }\n", 200)), "big.css");
        Assert.True(doc.IsLargeFile);
        Assert.Null(view.Feature<ColorDecoratorFeature>());
    }

    // ---- the built-in provider (editor.defaultColorDecorators) --------------------------------------------------------

    [AvaloniaFact]
    public async Task Without_a_colour_provider_the_built_in_one_finds_the_css_colours_of_any_file()
    {
        var text = "const theme = { fg: '#ff0000', bg: \"rgba(0, 0, 255, 0.5)\" }; // #notacolor\n";
        var (host, doc, view, _, feature) = await OpenAsync(text, "theme.ts");
        using var _h = host;
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "two colours");
        Assert.Equal([text.IndexOf("#ff0000", StringComparison.Ordinal), text.IndexOf("rgba(", StringComparison.Ordinal)], feature.Placements.Select(p => p.Offset));

        // its picker writes the colour back in the same form (hex stays hex), off the UI thread
        var session = feature.OpenAt(text.IndexOf("#ff0000", StringComparison.Ordinal))!;
        await Settle(session);
        Assert.Equal(["rgb(255, 0, 0)", "hsl(0, 100%, 50%)", "#ff0000"], session.Presentations.Select(p => p.Label));
        Assert.Equal(2, session.PresentationIndex);
        session.SetColor(new HsvaColor(240, 1, 1, 1));
        await Settle(session);
        Assert.Contains("fg: '#0000ff'", doc.GetText());

        host.Config.Set(ColorDecoratorFeature.DefaultDecoratorsKey, "never");
        await ViewHost.WaitFor(() => feature.Placements.Count == 0, "no built-in colours with never");
        Assert.Empty(Swatches(view));
    }

    [AvaloniaFact]
    public async Task With_a_language_server_auto_uses_only_its_colours_and_always_adds_the_built_in_ones_once()
    {
        // the server knows only the hex colour; the built-in provider would also find the rgb() one
        var server = new FakeColors { Colors = (d, _) => Task.FromResult<IReadOnlyList<ColorInformation>?>([.. Css(d).Where(c => d.GetText(c.Range).StartsWith('#'))]) };
        var text = ".a { color: #ff0000; border-color: rgb(0, 128, 0); }\n";
        var (host, _, _, _, feature) = await OpenAsync(text, provider: server);
        using var _h = host;
        await ViewHost.WaitFor(() => feature.Placements.Count == 1, "the server's colour");
        await Task.Delay(300);
        ViewHost.Pump();
        Assert.Single(feature.Placements); // auto: the built-in provider is not asked

        host.Config.Set(ColorDecoratorFeature.DefaultDecoratorsKey, "always");
        await ViewHost.WaitFor(() => feature.Placements.Count == 2, "the built-in colour beside the server's");
        Assert.Equal([text.IndexOf('#'), text.IndexOf("rgb(", StringComparison.Ordinal)], feature.Placements.Select(p => p.Offset)); // #ff0000 once
    }

    [AvaloniaFact]
    public async Task The_built_in_scan_runs_on_the_editor_worker_in_slices_and_is_bounded()
    {
        // 4.5 MB: a colour on every 100th line, the last one near the end
        var text = string.Concat(Enumerable.Range(0, 100_000).Select(i => i % 100 == 99 ? $"rhythm hash = '#{i % 256:x2}{i % 256:x2}{i % 256:x2}' # here and there\n" : "the rhythm of hashes # here and there, rgb hsl\n"));
        using var host = ViewHost.Create();
        var worker = host.Factory.Worker;
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(host.Dir.WriteFile("many.py", text)));
        var provider = new DefaultColorProvider(worker);
        var jobs = worker.CompletedJobs;
        var colours = await Task.Run(async () => await provider.ProvideDocumentColorsAsync(doc, CancellationToken.None));
        Assert.Equal(1000, colours!.Count);
        Assert.Equal(new TextRange(new TextPosition(99_999, 15), new TextPosition(99_999, 22)), colours[^1].Range);
        Assert.True(worker.CompletedJobs - jobs >= 2, $"the scan is sliced (about 6 ms each), got {worker.CompletedJobs - jobs} jobs");
        Assert.True(worker.LongestJob < TimeSpan.FromMilliseconds(100), $"longest worker job {worker.LongestJob.TotalMilliseconds} ms");

        // at most MaxColors, whatever the document holds
        var dense = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(host.Dir.WriteFile("dense.css", string.Concat(Enumerable.Repeat("#123456 ", 20_000)))));
        Assert.Equal(DefaultColorProvider.MaxColors, (await Task.Run(async () => await provider.ProvideDocumentColorsAsync(dense, CancellationToken.None)))!.Count);
        doc.Dispose();
        dense.Dispose();
    }

    // ---- the picker ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Clicking_a_swatch_opens_the_picker_and_picking_applies_the_presentation_live_as_one_undo_step()
    {
        var provider = new FakeColors();
        var (host, doc, view, window, feature) = await OpenAsync(provider: provider);
        using var _h = host;
        var caret = view.Editor.CaretOffset;
        var session = await ClickSwatchAsync(feature, view, window);
        Assert.Equal(caret, view.Editor.CaretOffset); // the click went to the swatch, not the text
        Assert.Same(session.Widget, view.WidgetLayer.Children.Single());
        Assert.True(session.Widget.IsKeyboardFocusWithin);
        Assert.Equal("#ff0000", session.Label); // the presentation matching the text is chosen
        Assert.Equal(2, session.PresentationIndex);
        Assert.Equal(Sheet, doc.GetText()); // opening edits nothing

        // pick in the saturation/value square: bottom left is black
        Click(window, At(session.Widget.SaturationBox, window, 0.02, 0.98));
        await Settle(session);
        Assert.StartsWith(".a { color: #0", doc.GetText());
        Assert.Matches(@"^\.a \{ color: #0[0-9a-f]{5}; background", doc.GetText());
        // then the hue strip: the middle is cyan's hue; drag to the top: red
        var hue = session.Widget.HueStrip;
        window.MouseDown(At(hue, window, 0.5, 0.5), MouseButton.Left);
        window.MouseMove(At(hue, window, 0.5, 0.0));
        window.MouseUp(At(hue, window, 0.5, 0.0), MouseButton.Left);
        Click(window, At(session.Widget.SaturationBox, window, 0.999, 0.001)); // full saturation and value
        await Settle(session);
        Assert.StartsWith(".a { color: #ff0000; background", doc.GetText());
        Click(window, At(session.Widget.SaturationBox, window, 0.999, 0.5));
        await Settle(session);
        var picked = doc.GetText();
        Assert.Matches(@"^\.a \{ color: #[78][0-9a-f]0000; background", picked);
        Assert.Equal(session.Range, (Sheet.IndexOf('#'), Sheet.IndexOf('#') + 7));
        // the swatch shows the picked colour at once
        Assert.Equal(session.Widget.PickedColor.ToAvalonia(), Swatches(view)[0].Color);

        // one undo step for the whole session
        session.Commit();
        Assert.Null(feature.Session);
        Assert.Empty(view.WidgetLayer.Children);
        view.Controller.Undo();
        Assert.Equal(Sheet, doc.GetText());
        view.Controller.Redo();
        Assert.Equal(picked, doc.GetText());
    }

    [AvaloniaFact]
    public async Task Escape_undoes_the_picker_s_edits_and_closes_it()
    {
        var (host, doc, view, window, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        var session = await ClickSwatchAsync(feature, view, window, index: 1); // rgba(0, 0, 255, 0.5)
        Assert.Equal("rgba(0, 0, 255, 0.5)", session.Label);
        Click(window, At(session.Widget.AlphaStrip, window, 0.5, 0.001)); // opaque
        await Settle(session);
        Assert.Contains("background: rgb(0, 0, 255);", doc.GetText());
        session.SetColor(new HsvaColor(0, 1, 1, 0.25));
        await Settle(session);
        Assert.Contains("background: rgba(255, 0, 0, 0.25);", doc.GetText());

        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.Null(feature.Session);
        Assert.Equal(Sheet, doc.GetText());
        Assert.False(doc.IsDirty);
        Assert.True(view.TextArea.IsKeyboardFocusWithin); // the keyboard goes back to the text
        Assert.Equal(Color.FromArgb(128, 0, 0, 255), Swatches(view)[1].Color);

        // Enter keeps them
        session = await ClickSwatchAsync(feature, view, window, index: 1);
        session.SetColor(new HsvaColor(120, 1, 1, 1));
        await Settle(session);
        Key(window, Avalonia.Input.Key.Enter);
        ViewHost.Pump();
        Assert.Null(feature.Session);
        Assert.Contains("background: rgb(0, 255, 0);", doc.GetText());
        var kept = doc.GetText();

        // a modal layer's Escape (Vim's: keys in the text go to Vim first) closes it the same way
        session = await ClickSwatchAsync(feature, view, window, index: 1);
        session.SetColor(new HsvaColor(0, 1, 1, 1));
        await Settle(session);
        Assert.Contains("background: rgb(255, 0, 0);", doc.GetText());
        view.Modal.CloseInfoPopups();
        Assert.Null(feature.Session);
        Assert.Equal(kept, doc.GetText());
    }

    [AvaloniaFact]
    public async Task The_header_cycles_presentations_and_its_original_colour_goes_back()
    {
        var (host, doc, view, window, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        var session = await ClickSwatchAsync(feature, view, window);
        Click(window, Center(session.Widget.PresentationCell, window)); // #ff0000 -> rgb(...)
        await Settle(session);
        Assert.StartsWith(".a { color: rgb(255, 0, 0);", doc.GetText());
        Assert.Equal("rgb(255, 0, 0)", session.Label);
        Click(window, Center(session.Widget.PresentationCell, window));
        await Settle(session);
        Assert.StartsWith(".a { color: hsl(0, 100%, 50%);", doc.GetText());
        session.SetColor(new HsvaColor(120, 1, 1, 1));
        await Settle(session);
        Assert.StartsWith(".a { color: hsl(120, 100%, 50%);", doc.GetText());
        Click(window, Center(session.Widget.OriginalCell, window));
        await Settle(session);
        Assert.StartsWith(".a { color: hsl(0, 100%, 50%);", doc.GetText());
        Assert.Equal(new HsvaColor(0, 1, 1, 1), session.Color);
    }

    [AvaloniaFact]
    public async Task Additional_edits_apply_with_the_presentation_and_the_range_follows_them()
    {
        var provider = new FakeColors();
        provider.Presentations = (d, c) => [new ColorPresentation("Color." + ColorFormats.FormatHex(c.Red, c.Green, c.Blue, c.Alpha).TrimStart('#'),
            AdditionalTextEdits: c.Range.Start.Line == 0 && !d.GetText().StartsWith("@use") ? [TextEdit.Insert(TextPosition.Zero, "@use colors;\n")] : null)];
        var (host, doc, view, window, feature) = await OpenAsync(".a { color: #ff0000; }\n", provider: provider);
        using var _h = host;
        var session = await ClickSwatchAsync(feature, view, window);
        session.SetColor(new HsvaColor(240, 1, 1, 1));
        await Settle(session);
        Assert.Equal("@use colors;\n.a { color: Color.0000ff; }\n", doc.GetText());
        session.SetColor(new HsvaColor(120, 1, 1, 1));
        await Settle(session);
        Assert.Equal("@use colors;\n.a { color: Color.00ff00; }\n", doc.GetText()); // the label replaced the colour's new range
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Equal(".a { color: #ff0000; }\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Answers_come_in_latest_wins_and_one_for_older_text_is_never_applied()
    {
        var provider = new FakeColors();
        var slow = new SemaphoreSlim(0);
        var asked = new List<string>();
        var inner = provider.Presentations;
        provider.Presentations = (d, c) =>
        {
            lock (asked) asked.Add(ColorFormats.FormatHex(c.Red, c.Green, c.Blue, c.Alpha));
            slow.Wait(TimeSpan.FromSeconds(10)); // on the pool: the picker never waits on it
            return inner(d, c);
        };
        var (host, doc, view, window, feature) = await OpenAsync(provider: provider);
        using var _h = host;
        Click(window, Center(Swatches(view)[0].Element, window));
        await ViewHost.WaitFor(() => feature.Session is not null, "the picker");
        var session = feature.Session!;
        await ViewHost.WaitFor(() => { lock (asked) return asked.Count == 1; }, "the opening request");
        session.SetColor(new HsvaColor(240, 1, 1, 1));
        session.SetColor(new HsvaColor(120, 1, 1, 1));
        session.SetColor(new HsvaColor(60, 1, 1, 1)); // three picks while the first answer is out
        slow.Release(10);
        await Settle(session);
        lock (asked) Assert.Equal(["#ff0000", "#ffff00"], asked); // the picks in between were never asked for
        Assert.StartsWith(".a { color: #ffff00;", doc.GetText());
    }

    [AvaloniaFact]
    public async Task An_edit_from_elsewhere_ends_the_session_and_keeps_what_it_applied()
    {
        var (host, doc, view, window, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        var session = await ClickSwatchAsync(feature, view, window);
        session.SetColor(new HsvaColor(240, 1, 1, 1));
        await Settle(session);
        doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Buffer.TextLength), "/* end */")]); // a language server's edit, another view's typing
        ViewHost.Pump();
        Assert.Null(feature.Session);
        Assert.StartsWith(".a { color: #0000ff;", doc.GetText());
        Assert.EndsWith("/* end */", doc.GetText());
        // a click in the text closes a picker too
        session = await ClickSwatchAsync(feature, view, window);
        Click(window, PointOf(window, view, doc.GetText().IndexOf(".b", StringComparison.Ordinal)));
        Assert.Null(feature.Session);
        Assert.Empty(view.WidgetLayer.Children);
    }

    [AvaloniaFact]
    public async Task The_command_opens_the_picker_at_the_caret_or_writes_a_new_colour_at_a_plain_caret()
    {
        var (host, doc, view, _, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        Assert.True(host.Commands.Has(ColorDecoratorFeature.ShowPickerCommand));
        var inside = Sheet.IndexOf("hsl(", StringComparison.Ordinal) + 5;
        view.Editor.CaretOffset = inside;
        await host.Commands.ExecuteAsync(ColorDecoratorFeature.ShowPickerCommand);
        ViewHost.Pump();
        var session = feature.Session!;
        Assert.Equal((Sheet.IndexOf("hsl(", StringComparison.Ordinal), Sheet.IndexOf("hsl(", StringComparison.Ordinal) + "hsl(120, 100%, 50%)".Length), session.Range);
        Assert.True(session.Widget.IsKeyboardFocusWithin);
        await Settle(session);
        Assert.Equal("hsl(120, 100%, 50%)", session.Label);
        await host.Commands.ExecuteAsync(ColorDecoratorFeature.ShowPickerCommand); // again: the same picker keeps the focus
        ViewHost.Pump();
        Assert.Same(session, feature.Session);
        session.Commit();

        // standalone: nothing at the caret, the picked colour's presentation is inserted there
        var end = doc.Buffer.TextLength;
        view.Editor.CaretOffset = end;
        await host.Commands.ExecuteAsync(ColorDecoratorFeature.ShowPickerCommand);
        ViewHost.Pump();
        session = feature.Session!;
        await Settle(session);
        Assert.Equal(Sheet, doc.GetText());
        session.SetColor(new HsvaColor(0, 1, 1, 1));
        await Settle(session);
        Assert.Equal(Sheet + "rgb(255, 0, 0)", doc.GetText());
        session.Revert();
        Assert.Equal(Sheet, doc.GetText());
    }

    [AvaloniaFact]
    public async Task Resting_on_a_swatch_opens_the_picker_and_leaving_it_unused_closes_it()
    {
        var (host, doc, view, window, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        var swatch = Swatches(view)[0].Element;
        window.MouseMove(Center(swatch, window));
        await ViewHost.WaitFor(() => feature.Session is not null, "the picker after the hover delay");
        var session = feature.Session!;
        Assert.True(session.IsHoverOpened);
        Assert.False(session.Widget.IsKeyboardFocusWithin); // the hover does not take the keyboard
        window.MouseMove(PointOf(window, view, Sheet.IndexOf(".b", StringComparison.Ordinal)));
        await ViewHost.WaitFor(() => feature.Session is null, "closed after the pointer left");
        Assert.Equal(Sheet, doc.GetText());

        // with editor.colorDecoratorsActivatedOn: click, resting opens nothing; a click still does
        host.Config.Set(ColorDecoratorFeature.ActivatedOnKey, "click");
        ViewHost.Pump();
        window.MouseMove(Center(Swatches(view)[0].Element, window));
        await Task.Delay(600);
        ViewHost.Pump();
        Assert.Null(feature.Session);
        await ClickSwatchAsync(feature, view, window);
        Assert.False(feature.Session!.IsHoverOpened);
    }

    [AvaloniaFact]
    public async Task Resting_on_a_colour_s_text_opens_its_picker_in_place_of_the_hover_and_the_pointer_can_move_into_it()
    {
        var hovers = new FakeHoverProvider((_, _, _) => Task.FromResult<Hover?>(new Hover([MarkupContent.Plain("a CSS colour")])));
        var (host, doc, view, window, feature) = await OpenAsync(provider: new FakeColors(),
            configure: h => h.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, hovers));
        using var _h = host;
        var onText = PointOf(window, view, Sheet.IndexOf("255, 0.5", StringComparison.Ordinal)); // inside rgba(0, 0, 255, 0.5)
        window.MouseMove(onText);
        await ViewHost.WaitFor(() => feature.Session is not null, "the picker after the hover delay");
        var session = feature.Session!;
        Assert.True(session.IsHoverOpened);
        Assert.Equal("rgba(0, 0, 255, 0.5)", doc.GetText().Substring(session.Range.Start, session.Range.End - session.Range.Start));
        await Task.Delay(500); // the editor's hover had its delay too: it stays away while the picker is up
        ViewHost.Pump();
        Assert.False(view.Feature<HoverFeature>()!.IsOpen);

        // the pointer goes over to the picker: it stays, and a press there keeps it for good, with the keyboard
        window.MouseMove(Center(session.Widget, window));
        await Task.Delay(500);
        ViewHost.Pump();
        Assert.Same(session, feature.Session);
        window.MouseDown(At(session.Widget.AlphaStrip, window, 0.5, 0.001), MouseButton.Left);
        window.MouseUp(At(session.Widget.AlphaStrip, window, 0.5, 0.001), MouseButton.Left);
        await Settle(session);
        Assert.False(session.IsHoverOpened);
        Assert.True(session.Widget.IsKeyboardFocusWithin);
        Assert.Contains("background: rgb(0, 0, 255);", doc.GetText());
        window.MouseMove(PointOf(window, view, Sheet.IndexOf(".b", StringComparison.Ordinal)));
        await Task.Delay(500);
        ViewHost.Pump();
        Assert.Same(session, feature.Session); // a used picker does not close with the pointer
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Equal(Sheet, doc.GetText());

        // typing closes a picker the hover opened
        window.MouseMove(onText);
        await ViewHost.WaitFor(() => feature.Session is { IsHoverOpened: true }, "the picker again");
        Type(view, "x");
        Key(window, Avalonia.Input.Key.Right);
        ViewHost.Pump();
        Assert.Null(feature.Session);
    }

    [AvaloniaFact]
    public async Task A_provider_without_presentations_shows_the_colour_and_edits_nothing()
    {
        var provider = new FakeColors { Presentations = (_, _) => null };
        var (host, doc, view, window, feature) = await OpenAsync(provider: provider);
        using var _h = host;
        var session = await ClickSwatchAsync(feature, view, window);
        session.SetColor(new HsvaColor(240, 1, 1, 1));
        await Settle(session);
        Assert.Equal(Sheet, doc.GetText());
        Assert.Equal("#ff0000", session.Label);
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Equal(Sheet, doc.GetText());
    }

    [AvaloniaFact]
    public async Task The_swatches_and_the_picker_are_drawn_in_the_view_screenshot()
    {
        var (host, _, view, window, feature) = await OpenAsync(provider: new FakeColors());
        using var _h = host;
        var session = await ClickSwatchAsync(feature, view, window, index: 1); // rgba(0, 0, 255, 0.5)
        ViewHost.Pump();
        var frame = window.CaptureRenderedFrame()!;
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        using (var fs = File.Create(Path.Combine(dir, "editor-color-picker.png"))) frame.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        PixelRect Corner(Control c, double fx, double fy)
        {
            var p = c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), window)!.Value;
            return new PixelRect((int)p.X - 2, (int)p.Y - 2, 4, 4);
        }
        Assert.True(CountPixels(frame, Color.FromRgb(0, 0, 255), 30, Corner(session.Widget.SaturationBox, 0.98, 0.02)) > 0, "the square's top right is the pure hue");
        Assert.True(CountPixels(frame, Colors.Black, 20, Corner(session.Widget.SaturationBox, 0.5, 0.98)) > 0, "its bottom is black");
        Assert.True(CountPixels(frame, Colors.Red, 30, Corner(session.Widget.HueStrip, 0.5, 0.012)) > 0, "the hue strip starts at red");
        Assert.True(CountPixels(frame, Color.FromRgb(0, 255, 255), 40, Corner(session.Widget.HueStrip, 0.5, 0.5)) > 0, "cyan in its middle");
        Assert.Equal("rgba(0, 0, 255, 0.5)", session.Label);
    }

    // ---- lifetime -----------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_feature_its_swatches_and_its_picker_are_collectable_after_the_view_is_disposed()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IDocumentColorProvider>(DocumentSelector.All, new FakeColors());
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(host.Dir.WriteFile("leak.css", Sheet)));
        var other = new Button { Content = "elsewhere" }; // where the shell moves the keyboard before it destroys a focused view (docs/EDITOR.md §1)
        var panel = new DockPanel();
        DockPanel.SetDock(other, Avalonia.Controls.Dock.Top);
        panel.Children.Add(other);
        var window = new Window { Width = 900, Height = 600, Content = panel };
        window.Show();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var view = (CodeEditorControl)host.Factory.CreateView(doc);
            panel.Children.Add(view);
            ViewHost.Pump();
            var feature = view.Feature<ColorDecoratorFeature>()!;
            await ViewHost.WaitFor(() => feature.Placements.Count == 3, "colours");
            var swatch = Swatches(view)[0].Element;
            var session = feature.OpenAt(Sheet.IndexOf('#'))!;
            await Settle(session);
            session.SetColor(new HsvaColor(240, 1, 1, 1));
            await Settle(session);
            Assert.True(session.Widget.IsKeyboardFocusWithin);
            var widget = session.Widget;
            other.Focus();
            host.Factory.DisposeView(view);
            panel.Children.Remove(view);
            for (var i = 0; i < 3; i++)
            {
                ViewHost.Pump();
                await Task.Delay(10);
            }
            Assert.False(session.IsOpen);
            return [view, feature, session, widget, swatch];
        });
        window.Close();
        Assert.StartsWith(".a { color: #0000ff;", doc.GetText()); // the document keeps what the picker applied
        doc.Dispose();
    }
}
