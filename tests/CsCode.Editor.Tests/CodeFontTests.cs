using Avalonia.Headless.XUnit;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The editor's default code font is the Fira Code CsCode ships (CsCode.Ui CodeFonts, registered by the test app as by the real one), not
/// whatever monospace face the OS has. Fira Code draws its ligatures (=>, !=, ===) with HarfBuzz's default features (calt), one glyph per
/// character at the same advance, so the caret and selection still land on every column.
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

    [AvaloniaFact]
    public async Task Ligatures_show_but_keep_one_glyph_per_character_so_the_caret_lands_on_every_column()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Code + "\n", "a.txt");
        ViewHost.Pump();

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
        Assert.True(substituted >= 15, $"the operators are drawn as ligatures ({substituted} glyphs substituted)");

        var width = view.TextView.WideSpaceWidth;
        for (var column = 0; column <= Code.Length; column++)
        {
            var x = view.TextView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(1, column + 1), VisualYPosition.TextMiddle).X;
            Assert.Equal(column * width, x, 2);
        }
    }
}
