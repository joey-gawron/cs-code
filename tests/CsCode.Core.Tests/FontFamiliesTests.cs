using System.Runtime.InteropServices;
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
    [InlineData("'Fira Code', \"JetBrains Mono\", monospace", "Fira Code, JetBrains Mono, Menlo")]
    [InlineData("Menlo, SF Mono, Monaco, monospace", "Menlo, SF Mono, Monaco")]                       // the default: monospace is Menlo again
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
    [InlineData("'Fira Code', monospace", "Fira Code, monospace")]
    public void On_Linux_only_the_quotes_go_fontconfig_resolves_generic_names(string families, string expected) =>
        Assert.Equal(expected, FontFamilies.Normalize(families, OSPlatform.Linux));
}
