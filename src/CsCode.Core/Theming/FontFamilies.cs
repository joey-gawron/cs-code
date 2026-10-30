using System.Runtime.InteropServices;
using System.Text.Json;

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

    /// <summary>
    /// The OpenType features <c>editor.fontLigatures</c> asks for, in the syntax Avalonia's <c>FontFeature.Parse</c> reads. Off (false, unset or
    /// anything else, VS Code's default) turns off the font's ligatures and contextual alternates (<c>-liga</c>, <c>-calt</c>: Fira Code draws
    /// its <c>!=</c> and <c>=&gt;</c> through calt); on (true) leaves the font's defaults; a string is VS Code's CSS font-feature-settings
    /// (<c>"'ss01', 'zero', 'calt' 0"</c>), which turns ligatures on and adds those features.
    /// </summary>
    public static IReadOnlyList<string> LigatureFeatures(JsonElement? setting)
    {
        if (setting is not { } value) return LigaturesOff;
        if (value.ValueKind == JsonValueKind.True) return [];
        if (value.ValueKind != JsonValueKind.String) return LigaturesOff;
        var text = value.GetString() ?? "";
        if (text.Trim() is "" or "true") return [];
        if (text.Trim() == "false") return LigaturesOff;
        var features = new List<string>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 'tag' [on|off|N], as CSS writes it; a tag is four printable ASCII characters
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tag = words[0].Trim('\'', '"');
            if (tag.Length != 4 || tag.Any(c => c is < '!' or > '~' or '=' or '+' or '-' or '[' or ']')) continue;
            var state = words.Length > 1 ? words[1] : "1";
            var n = state switch { "on" => 1, "off" => 0, _ => int.TryParse(state, out var v) && v >= 0 ? v : -1 };
            if (n >= 0) features.Add(tag + "=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return features;
    }

    /// <summary>What <see cref="LigatureFeatures"/> answers when ligatures are off.</summary>
    public static IReadOnlyList<string> LigaturesOff { get; } = ["-liga", "-calt"];

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
