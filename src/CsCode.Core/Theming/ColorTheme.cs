using System.Globalization;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Theming;

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Parses #RRGGBB or #RRGGBBAA (throws <see cref="FormatException"/> otherwise).</summary>
    public static Rgba Parse(string hex)
    {
        if (!TryParse(hex, out var c)) throw new FormatException($"Bad color '{hex}'");
        return c;
    }

    /// <summary>Accepts #RGB, #RGBA, #RRGGBB, #RRGGBBAA (leading '#' optional).</summary>
    public static bool TryParse(string? hex, out Rgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.AsSpan().Trim().TrimStart('#');
        static bool Nibble(char c, out int v)
        {
            v = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            return v >= 0;
        }
        static bool Byte(ReadOnlySpan<char> s, int i, int width, out byte b)
        {
            b = 0;
            if (width == 1)
            {
                if (!Nibble(s[i], out var n)) return false;
                b = (byte)(n * 17);
                return true;
            }
            if (!Nibble(s[i], out var hi) || !Nibble(s[i + 1], out var lo)) return false;
            b = (byte)(hi * 16 + lo);
            return true;
        }
        var w = s.Length is 3 or 4 ? 1 : s.Length is 6 or 8 ? 2 : 0;
        if (w == 0) return false;
        if (!Byte(s, 0, w, out var r) || !Byte(s, w, w, out var g) || !Byte(s, 2 * w, w, out var b)) return false;
        byte a = 255;
        if (s.Length is 4 or 8 && !Byte(s, 3 * w, w, out a)) return false;
        color = new Rgba(r, g, b, a);
        return true;
    }

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

public enum ThemeKind { Light, Dark, HighContrastDark, HighContrastLight }

/// <param name="Scopes">TextMate scopes this rule applies to (already split).</param>
/// <param name="FontStyle">Space-separated subset of: italic bold underline strikethrough; empty = reset.</param>
public sealed record TokenColorRule(IReadOnlyList<string> Scopes, string? Foreground, string? Background, string? FontStyle);

/// <summary>
/// One palette namespace for the whole app: VS Code workbench color keys ("editor.background", "sideBar.background",
/// "statusBar.background", "list.activeSelectionBackground", "tab.activeBackground", …). VS Code theme JSON files provide
/// these natively; the shell maps them onto Avalonia resources. Built-in "Visual Studio 2022 Blue/Dark/Light" themes
/// are expressed in the same keys so every theme (built-in or a user's theme file) flows through one pipeline.
/// </summary>
public sealed record ColorTheme(
    string Id,
    string Label,
    ThemeKind Kind,
    IReadOnlyDictionary<string, Rgba> Colors,
    IReadOnlyList<TokenColorRule> TokenColors,
    IReadOnlyDictionary<string, TokenColorRule>? SemanticTokenColors = null)
{
    public bool TryGetColor(string key, out Rgba color) => Colors.TryGetValue(key, out color);
}

public interface IColorThemeService
{
    ColorTheme Current { get; }
    IReadOnlyList<ColorTheme> Available { get; }
    /// <summary>Adds a theme (built-in VS2022 themes are registered by the shell; user theme files by the editor).</summary>
    IDisposable Register(ColorTheme theme);
    ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default);
    /// <summary>Resolved color with the documented VS Code fallback chain (e.g. tab.inactiveBackground → editorGroupHeader.tabsBackground).</summary>
    Rgba GetColor(string key);
    IEventSource<ColorTheme> Changed { get; }
}
