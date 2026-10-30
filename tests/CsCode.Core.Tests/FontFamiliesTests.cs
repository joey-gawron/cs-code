using System.Runtime.InteropServices;
using System.Text.Json;
using CsCode.Core.Theming;
using Xunit;

namespace CsCode.Core.Tests;

/// <summary>
/// ui-4 / files-13: a font list as settings write it must reach Avalonia as names it can match. Avalonia takes each name literally, so
/// <c>'Courier New'</c> (quotes and all) never matched, and on a Mac "monospace" and "sans-serif" are Helvetica.
/// </summary>
public sealed class FontFamiliesTests
{
    [Theory]
    [InlineData("'Source Code Pro', \"JetBrains Mono\", monospace", "Source Code Pro, JetBrains Mono, Menlo")]
    [InlineData("Menlo, SF Mono, Monaco, monospace", "Menlo, SF Mono, Monaco")]                       // monospace is Menlo again
    [InlineData("system-ui, Helvetica Neue, sans-serif", ".AppleSystemUIFont, Helvetica Neue")]
    [InlineData("-apple-system, BlinkMacSystemFont, serif", ".AppleSystemUIFont, Times")]
    [InlineData(" Cascadia Code ,, ' ' , MONOSPACE ", "Cascadia Code, Menlo")]
    public void On_macOS_quotes_go_and_generic_names_become_families_the_Mac_has(string families, string expected) =>
        Assert.Equal(expected, FontFamilies.Normalize(families, OSPlatform.OSX));

    [Theory]
    [InlineData("'Courier New', monospace", "Courier New, Consolas")]
    [InlineData("Segoe UI, Inter, Noto Sans, sans-serif", "Segoe UI, Inter, Noto Sans")]
    public void On_Windows_generic_names_become_its_families(string families, string expected) =>
        Assert.Equal(expected, FontFamilies.Normalize(families, OSPlatform.Windows));

    [Theory]
    [InlineData("Cascadia Code, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace", "Cascadia Code, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace")]
    [InlineData("Segoe UI, Inter, Noto Sans, sans-serif", "Segoe UI, Inter, Noto Sans, sans-serif")]
    [InlineData("'Source Code Pro', monospace", "Source Code Pro, monospace")]
    public void On_Linux_only_the_quotes_go_fontconfig_resolves_generic_names(string families, string expected) =>
        Assert.Equal(expected, FontFamilies.Normalize(families, OSPlatform.Linux));

    public static TheoryData<string> Platforms => ["OSX", "WINDOWS", "LINUX"];

    /// <summary>
    /// The bundled Fira Code is reached through its collection (fonts:CsCode, registered by CsCode.Ui's CodeFonts) on every OS, whatever the
    /// quotes and casing, so a user's "Fira Code" setting gets it without installing anything, and a Fira Code the OS has is not consulted.
    /// </summary>
    [Theory]
    [MemberData(nameof(Platforms))]
    public void The_bare_name_Fira_Code_becomes_the_bundled_collection(string platform)
    {
        var os = OSPlatform.Create(platform);
        Assert.Equal("fonts:CsCode#Fira Code", FontFamilies.Normalize("Fira Code", os));
        Assert.Equal("fonts:CsCode#Fira Code, Hack", FontFamilies.Normalize("'fira code', Hack, \"Fira Code\"", os));
        Assert.Equal("Fira Mono, FiraCode Nerd Font", FontFamilies.Normalize("Fira Mono, FiraCode Nerd Font", os)); // other names stay as they are
    }

    /// <summary>Fira Code first in the code font default of every OS, then each OS's own faces (Menlo on a Mac, Cascadia/Consolas/DejaVu elsewhere).</summary>
    [Fact]
    public void The_code_font_default_starts_with_the_bundled_Fira_Code_on_every_OS()
    {
        Assert.StartsWith("Fira Code, Menlo,", FontFamilies.MacCode);
        Assert.StartsWith("Fira Code, Cascadia Code,", FontFamilies.OtherCode);
        Assert.Equal(OperatingSystem.IsMacOS() ? FontFamilies.MacCode : FontFamilies.OtherCode, FontFamilies.DefaultCode);
        Assert.StartsWith("fonts:CsCode#Fira Code, Menlo, SF Mono, Monaco", FontFamilies.Normalize(FontFamilies.MacCode, OSPlatform.OSX));
    }

    /// <summary>editor.fontLigatures as font features: off unless the setting says otherwise (VS Code's default), on with the font's own, or a CSS feature list.</summary>
    [Theory]
    [InlineData(null, "-liga, -calt")]
    [InlineData("false", "-liga, -calt")]
    [InlineData("\"false\"", "-liga, -calt")]
    [InlineData("42", "-liga, -calt")] // a wrongly typed value is ignored
    [InlineData("true", "")]
    [InlineData("\"true\"", "")]
    [InlineData("\"'ss01', 'zero'\"", "ss01=1, zero=1")]
    [InlineData("\"\\\"cv01\\\" 2, 'calt' 0, 'liga' off, 'ss02' on, 'bad tag', 'x=1y'\"", "cv01=2, calt=0, liga=0, ss02=1")]
    public void Ligature_features_follow_editor_fontLigatures(string? json, string expected)
    {
        JsonElement? setting = json is null ? null : JsonDocument.Parse(json).RootElement;
        Assert.Equal(expected, string.Join(", ", FontFamilies.LigatureFeatures(setting)));
    }
}
