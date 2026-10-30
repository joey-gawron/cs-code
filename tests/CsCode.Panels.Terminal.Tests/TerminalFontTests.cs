using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CsCode.Core.Theming;
using CsCode.Panels.Terminal.Rendering;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// ui-4: the terminal's font when none of terminal.integrated.fontFamily is installed. On a stock Mac the last-resort list (DejaVu, Noto,
/// Liberation, then any fixed-pitch "monospace") found nothing, since CoreText answers monospace with Helvetica, and the cell grid was laid out
/// in the proportional default face.
/// </summary>
public sealed class TerminalFontTests
{
    [Fact]
    public void On_macOS_Menlo_and_Monaco_come_after_the_settings_and_before_the_Linux_faces() =>
        Assert.Equal(["Cascadia Code", "Fira Code", "Menlo", "Monaco", "DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono", "monospace"],
            TerminalFont.Candidates("'Cascadia Code', \"Fira Code\"", macOS: true));

    [Fact]
    public void Elsewhere_the_fallbacks_are_as_before() =>
        Assert.Equal(["Cascadia Code", "DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono", "monospace"],
            TerminalFont.Candidates("Cascadia Code", macOS: false));

    [AvaloniaFact]
    public void On_a_Mac_a_list_of_fonts_that_are_not_installed_still_gives_a_fixed_pitch_face()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Menlo ships with macOS");
        var font = TerminalFont.Create("No Such Font Anywhere", 13);
        Assert.True(font.RegularFace.Metrics.IsFixedPitch, $"{font.Family} is proportional");
        Assert.Equal("Menlo", font.Family.Name);
    }

    /// <summary>The terminal's default (the editor's) starts with the bundled Fira Code, which every OS now has: its regular and bold faces are used.</summary>
    [AvaloniaTheory]
    [InlineData(FontFamilies.MacCode)]
    [InlineData(FontFamilies.OtherCode)]
    [InlineData("'Fira Code'")]
    public void The_bundled_Fira_Code_is_the_terminal_font_without_installing_it(string families)
    {
        var font = TerminalFont.Create(families, 13);
        Assert.Equal("Fira Code", font.RegularFace.FamilyName);
        Assert.True(font.RegularFace.Metrics.IsFixedPitch);
        var (bold, glyph) = font.Glyph('M', bold: true, italic: false);
        Assert.NotEqual(0, glyph);
        Assert.Equal("Fira Code", bold.FamilyName);
        Assert.Equal(FontWeight.Bold, bold.Weight);
    }
}
