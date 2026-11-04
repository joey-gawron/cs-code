using CsCode.Editor.Features;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The built-in colour detection and presentations (<c>editor.defaultColorDecorators</c>), and the picker's colour model.</summary>
public class ColorFormatsTests
{
    private static List<(string Text, byte R, byte G, byte B, double A)> Find(string text)
    {
        var found = new List<ColorFormats.Found>();
        ColorFormats.Find(text, 1000, found);
        return [.. found.Select(f => (text.Substring(f.Start, f.Length), ColorFormats.Byte(f.R), ColorFormats.Byte(f.G), ColorFormats.Byte(f.B), Math.Round(f.A, 3)))];
    }

    [Fact]
    public void Hex_colours_of_three_four_six_and_eight_digits_are_found_in_either_case()
    {
        var found = Find(".a { color: #f00; background: #FF000080; border-color: #00ff00; outline-color: #0f08; }");
        Assert.Equal([("#f00", 255, 0, 0, 1d), ("#FF000080", 255, 0, 0, 0.502), ("#00ff00", 0, 255, 0, 1d), ("#0f08", 0, 255, 0, 0.533)], found);
    }

    [Theory]
    [InlineData("#ff")] // two digits
    [InlineData("#fffff")] // five
    [InlineData("#abcdefabc")] // nine
    [InlineData("#fffg")] // not hex
    [InlineData("a#fff")] // inside a word
    [InlineData("&#123;")] // an HTML character reference
    [InlineData("#abc-def")] // an id with a dash
    [InlineData("#region")] // a C# directive
    [InlineData("#define")]
    public void Not_colours(string text) => Assert.Empty(Find(text));

    [Fact]
    public void Rgb_and_hsl_functions_with_commas_spaces_slash_alpha_and_percentages()
    {
        var found = Find("""
            rgb(255, 0, 0) rgba(0, 0, 255, 0.5) rgb(0 128 0) rgb(0 128 0 / 25%) RGB(100%, 0%, 0%)
            hsl(120, 100%, 50%) hsla(240, 100%, 50%, 0.5) hsl(0deg 100% 50% / 0.5)
            """);
        Assert.Equal(
        [
            ("rgb(255, 0, 0)", 255, 0, 0, 1d), ("rgba(0, 0, 255, 0.5)", 0, 0, 255, 0.5), ("rgb(0 128 0)", 0, 128, 0, 1d), ("rgb(0 128 0 / 25%)", 0, 128, 0, 0.25),
            ("RGB(100%, 0%, 0%)", 255, 0, 0, 1d), ("hsl(120, 100%, 50%)", 0, 255, 0, 1d), ("hsla(240, 100%, 50%, 0.5)", 0, 0, 255, 0.5), ("hsl(0deg 100% 50% / 0.5)", 255, 0, 0, 0.5),
        ], found);
    }

    [Theory]
    [InlineData("rgb(256, 0, 0)")] // out of range
    [InlineData("rgb(1, 2)")] // too few
    [InlineData("rgb(1, 2, 3, 4, 5)")] // too many
    [InlineData("rgba(1, 2, 3, 2)")] // alpha above 1
    [InlineData("rgb(1 2, 3)")] // mixed separators
    [InlineData("hsl(120, 100, 50)")] // saturation and lightness need %
    [InlineData("torgb(1, 2, 3)")] // a longer name
    [InlineData("rgb(var(--x), 0, 0)")] // not a literal
    public void Not_colour_functions(string text) => Assert.Empty(Find(text));

    [Fact]
    public void Presentations_are_rgb_hsl_and_hex_as_vs_code_writes_them()
    {
        Assert.Equal(["rgb(255, 0, 0)", "hsl(0, 100%, 50%)", "#ff0000"], ColorFormats.Presentations(1, 0, 0, 1));
        Assert.Equal(["rgba(0, 0, 255, 0.5)", "hsla(240, 100%, 50%, 0.5)", "#0000ff80"], ColorFormats.Presentations(0, 0, 1, 0.5));
        Assert.Equal("#FF8000", ColorFormats.FormatHex(1, 128 / 255d, 0, 1, upper: true));
        Assert.Equal("0.33", ColorFormats.FormatAlpha(1 / 3d));
        Assert.Equal("hsl(0, 0%, 50%)", ColorFormats.FormatHsl(0.5, 0.5, 0.5, 1)); // grey: no hue
    }

    [Theory]
    [InlineData("#ff0000", 2)]
    [InlineData("#F00", 2)] // the hex form, whatever its length or case
    [InlineData("rgb(255, 0, 0)", 0)]
    [InlineData("rgba(255, 0, 0, 1)", 0)] // rgba( finds rgb(
    [InlineData("hsl(0, 100%, 50%)", 1)]
    [InlineData("hsla(0, 100%, 50%, 0.5)", 1)]
    [InlineData("red", 0)] // a name: the first
    [InlineData("", 0)]
    public void The_presentation_that_matches_the_text_is_guessed(string text, int expected) =>
        Assert.Equal(expected, ColorFormats.Guess(["rgb(255, 0, 0)", "hsl(0, 100%, 50%)", "#ff0000"], text));

    [Fact]
    public void Hsv_round_trips_rgb_and_a_grey_keeps_the_hue_it_is_given()
    {
        foreach (var (r, g, b) in new[] { (1d, 0d, 0d), (0.2, 0.4, 0.6), (0d, 1d, 0.5), (0.9, 0.9, 0.1), (0d, 0d, 0d), (1d, 1d, 1d) })
        {
            var hsv = HsvaColor.FromRgba(r, g, b, 0.5);
            var back = hsv.ToRgb();
            Assert.Equal(r, back.R, 9);
            Assert.Equal(g, back.G, 9);
            Assert.Equal(b, back.B, 9);
            Assert.Equal(0.5, hsv.A);
        }
        Assert.Equal(200, HsvaColor.FromRgba(0.5, 0.5, 0.5, 1, hueHint: 200).H);
        Assert.Equal((0d, 1d, 1d), (HsvaColor.FromRgba(1, 0, 0, 1).H, HsvaColor.FromRgba(1, 0, 0, 1).S, HsvaColor.FromRgba(1, 0, 0, 1).V));
        var (hr, hg, hb) = ColorFormats.HslToRgb(210, 0.5, 0.4);
        Assert.Equal((51, 102, 153), (ColorFormats.Byte(hr), ColorFormats.Byte(hg), ColorFormats.Byte(hb)));
    }

    [Fact]
    public void Scanning_stops_at_the_maximum()
    {
        var text = string.Join(" ", Enumerable.Repeat("#123456", 50));
        var found = new List<ColorFormats.Found>();
        ColorFormats.Find(text, 20, found);
        Assert.Equal(20, found.Count);
    }
}
