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
    /// <summary>The code font default on macOS (VS Code's, with SF Mono where it is installed): a stock Mac has none of Cascadia/Consolas/DejaVu.</summary>
    public const string MacCode = "Menlo, SF Mono, Monaco, monospace";

    /// <summary>The UI font default on macOS: the system font (San Francisco), which no family name but the generic one reaches.</summary>
    public const string MacUi = "system-ui, Helvetica Neue, sans-serif";

    /// <summary><paramref name="families"/> with each name's quotes stripped and the generic names mapped to a family the OS has, without repeats.</summary>
    public static string Normalize(string families) => Normalize(families, Current);

    /// <inheritdoc cref="Normalize(string)"/>
    public static string Normalize(string families, OSPlatform os)
    {
        var names = new List<string>();
        foreach (var raw in families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.Trim('\'', '"').Trim();
            if (name.Length == 0) continue;
            name = Generic(name, os) ?? name;
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
