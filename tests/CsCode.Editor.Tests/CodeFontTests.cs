using Avalonia.Headless.XUnit;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The editor's default code font is the Fira Code CsCode ships (CsCode.Ui CodeFonts, registered by the test app as by the real one), not
/// whatever monospace face the OS has. Its ligatures (=>, !=, ===) are on unless editor.fontLigatures turns them off; Fira Code draws them
/// with HarfBuzz's default features (calt), one glyph per character at the same advance, so the caret and selection still land on every column.
/// </summary>
public class CodeFontTests
{
    private const string Code = "a->b != c => d === e <= f || g && h :: i |> j";

    private static ShapedTextRun FirstRun(CsCode.Editor.View.CodeEditorControl view) =>
        view.TextView.VisualLines[0].TextLines[0].TextRuns.OfType<ShapedTextRun>().First();

    [AvaloniaFact]
    public async Task With_default_settings_the_editor_draws_code_in_the_bundled_Fira_Code()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Code + "\n", "a.txt");
        ViewHost.Pump();

        var face = FirstRun(view).ShapedBuffer.GlyphTypeface;
        Assert.Equal("Fira Code", face.FamilyName);
        Assert.True(face.Metrics.IsFixedPitch);
    }

    /// <summary>The glyphs of the first run that are not the font's plain glyph for their character (ligature and contextual-alternate glyphs).</summary>
    private static int Substituted(CsCode.Editor.View.CodeEditorControl view)
    {
        var run = FirstRun(view);
        var face = run.ShapedBuffer.GlyphTypeface;
        var glyphs = run.ShapedBuffer.ToList();
        Assert.Equal(Code.Length, glyphs.Count);
        var substituted = 0;
        for (var i = 0; i < Code.Length; i++)
        {
            Assert.Equal(i, glyphs[i].GlyphCluster);
            Assert.Equal(glyphs[0].GlyphAdvance, glyphs[i].GlyphAdvance, 3);
            if (face.CharacterToGlyphMap.TryGetGlyph(Code[i], out var plain) && plain != glyphs[i].GlyphIndex) substituted++;
        }
        return substituted;
    }

    private static void AssertCaretOnEveryColumn(CsCode.Editor.View.CodeEditorControl view)
    {
        var width = view.TextView.WideSpaceWidth;
        for (var column = 0; column <= Code.Length; column++)
        {
            var x = view.TextView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(1, column + 1), VisualYPosition.TextMiddle).X;
            Assert.Equal(column * width, x, 2);
        }
    }

    /// <summary>
    /// editor.fontLigatures is on by default (the owner's choice; VS Code's is off): the bundled Fira Code draws its !=, => and === ligatures,
    /// and they keep one glyph per character, so the caret still lands on every column.
    /// </summary>
    [AvaloniaFact]
    public async Task Ligatures_are_on_by_default_and_keep_one_glyph_per_character_so_the_caret_lands_on_every_column()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Code + "\n", "a.txt");
        ViewHost.Pump();

        var substituted = Substituted(view);
        Assert.True(substituted >= 15, $"the operators are drawn as ligatures ({substituted} glyphs substituted)");
        AssertCaretOnEveryColumn(view);
    }

    [AvaloniaFact]
    public async Task With_editor_fontLigatures_off_operators_are_drawn_as_their_characters_and_live_back_on()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Code + "\n", "a.txt");
        ViewHost.Pump();
        host.Config.Set("editor.fontLigatures", false);
        ViewHost.Pump();

        Assert.Equal(0, Substituted(view));
        AssertCaretOnEveryColumn(view);

        host.Config.Set("editor.fontLigatures", true); // and live back on
        ViewHost.Pump();
        Assert.True(Substituted(view) >= 15);
    }

    [AvaloniaFact]
    public async Task Ligatures_turned_off_stay_off_in_coloured_code_whose_elements_the_colourizer_splits()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.fontLigatures", false);
        var (_, view, _) = await host.OpenAsync("class A { bool F(int a, int b) => a != b && a >= b; }\n", "a.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 1, "tokens");
        view.TextView.Redraw();
        ViewHost.Pump();

        var runs = view.TextView.VisualLines[0].TextLines[0].TextRuns.OfType<ShapedTextRun>().ToList();
        Assert.True(runs.Count > 1, "the colourizer split the line into several runs");
        foreach (var run in runs)
        {
            var face = run.ShapedBuffer.GlyphTypeface;
            var text = run.Text.ToString();
            var glyphs = run.ShapedBuffer.ToList();
            Assert.Equal(text.Length, glyphs.Count);
            for (var i = 0; i < text.Length; i++)
                Assert.True(face.CharacterToGlyphMap.TryGetGlyph(text[i], out var plain) && plain == glyphs[i].GlyphIndex, $"'{text[i]}' in \"{text}\" is drawn with its own glyph");
        }
    }
}
