using System.Runtime.InteropServices;

namespace CsCode.Core.Theming;

/// <summary>
/// Font family lists as settings write them (<c>editor.fontFamily</c>, CSS-like: <c>"Menlo, 'Courier New', monospace"</c>) turned into the
/// list a FontFamily is built from, plus the per-OS defaults. Avalonia takes every comma-separated name literally: a quoted name never matches an
/// installed family, and the generic CSS names mean nothing to the macOS (CoreText) and Windows (DirectWrite) font managers, which answer them
/// with their default proportional face (Helvetica on a Mac). Linux keeps its generic names: fontconfig resolves them itself.
/// </summary>
public static class FontFamilies
{
    /// <summary>The code font CsCode ships (Fira Code, SIL OFL 1.1, assets/fonts/fira-code): first in every OS's code font default.</summary>
    public const string Bundled = "Fira Code";

    /// <summary>
    /// The name of the font collection the app registers the bundled faces under (CsCode.Ui <c>CodeFonts</c>): <see cref="Normalize(string)"/>
    /// turns the bare name <see cref="Bundled"/> into <c>fonts:CsCode#Fira Code</c>, so it is found whether or not the OS has Fira Code installed.
    /// </summary>
    public const string BundledCollection = "fonts:CsCode";

    /// <summary>The code font default on macOS: the bundled Fira Code, then VS Code's (Menlo, with SF Mono where it is installed); a stock Mac has none of Cascadia/Consolas/DejaVu.</summary>
    public const string MacCode = Bundled + ", Menlo, SF Mono, Monaco, monospace";

    /// <summary>The code font default elsewhere: the bundled Fira Code, then the Windows and Linux monospace faces.</summary>
    public const string OtherCode = Bundled + ", Cascadia Code, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace";

    /// <summary>The code font default on this OS (<see cref="MacCode"/> or <see cref="OtherCode"/>).</summary>
    public static string DefaultCode => OperatingSystem.IsMacOS() ? MacCode : OtherCode;

    /// <summary>The UI font default on macOS: the system font (San Francisco), which no family name but the generic one reaches.</summary>
    public const string MacUi = "system-ui, Helvetica Neue, sans-serif";

    /// <summary>
    /// <paramref name="families"/> with each name's quotes stripped, the generic names mapped to a family the OS has and the bundled
    /// <see cref="Bundled"/> pointed at its collection (<see cref="BundledCollection"/>), without repeats.
    /// </summary>
    public static string Normalize(string families) => Normalize(families, Current);

    /// <inheritdoc cref="Normalize(string)"/>
    public static string Normalize(string families, OSPlatform os)
    {
        var names = new List<string>();
        foreach (var raw in families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.Trim('\'', '"').Trim();
            if (name.Length == 0) continue;
            name = name.Equals(Bundled, StringComparison.OrdinalIgnoreCase) ? BundledCollection + "#" + Bundled : Generic(name, os) ?? name;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return string.Join(", ", names);
    }

    private static OSPlatform Current =>
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;

    /// <summary>The concrete family a generic CSS name stands for on <paramref name="os"/> (null: not generic, or left to fontconfig).</summary>
    private static string? Generic(string name, OSPlatform os)
    {
        var generic = name.ToLowerInvariant();
        if (os == OSPlatform.OSX)
            return generic switch
            {
                "monospace" or "ui-monospace" => "Menlo",
                "sans-serif" => "Helvetica Neue",
                "serif" => "Times",
                "system-ui" or "ui-sans-serif" or "-apple-system" or "blinkmacsystemfont" => ".AppleSystemUIFont", // CoreText's name for San Francisco
                _ => null,
            };
        if (os == OSPlatform.Windows)
            return generic switch
            {
                "monospace" or "ui-monospace" => "Consolas",
                "sans-serif" or "system-ui" or "ui-sans-serif" => "Segoe UI",
                "serif" => "Times New Roman",
                _ => null,
            };
        return null;
    }
}
