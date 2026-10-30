using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CsCode.Core.Theming;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The code font CsCode ships: Fira Code's faces are embedded in CsCode.Ui and registered with the font manager (CodeFonts.WithCodeFonts), so
/// "Fira Code" in editor.fontFamily, or the default, needs nothing installed on the OS.
/// </summary>
public class CodeFontTests
{
    private static GlyphTypeface Face(string families, FontWeight weight = FontWeight.Normal)
    {
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(FontFamilies.Normalize(families)), FontStyle.Normal, weight), out var face),
            $"no face for {families}");
        return face;
    }

    [AvaloniaTheory]
    [InlineData("Fira Code")]
    [InlineData("'fira code', Menlo, monospace")]
    [InlineData("Fira Code, Menlo, SF Mono, Monaco, monospace")] // the macOS default
    [InlineData("Fira Code, Cascadia Code, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace")] // elsewhere
    public void The_bundled_Fira_Code_is_found_by_name_and_is_fixed_pitch(string families)
    {
        var face = Face(families);
        Assert.Equal("Fira Code", face.FamilyName);
        Assert.True(face.Metrics.IsFixedPitch);
    }

    [AvaloniaTheory]
    [InlineData(FontWeight.Light)]
    [InlineData(FontWeight.Normal)]
    [InlineData(FontWeight.Medium)]
    [InlineData(FontWeight.SemiBold)]
    [InlineData(FontWeight.Bold)]
    public void Every_bundled_weight_is_there(FontWeight weight)
    {
        var face = Face("Fira Code", weight);
        Assert.Equal("Fira Code", string.IsNullOrEmpty(face.TypographicFamilyName) ? face.FamilyName : face.TypographicFamilyName); // Light's FamilyName is "Fira Code Light"
        Assert.Equal(weight, face.Weight);
    }

    [AvaloniaFact]
    public void A_font_the_user_names_instead_is_still_used()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Menlo ships with macOS");
        Assert.Equal("Menlo", Face("Menlo, Fira Code").FamilyName);
    }
}
