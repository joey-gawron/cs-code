using Avalonia.Media;
using Avalonia.Media.Immutable;
using CsCode.Core.Platform;
using CsCode.Core.Theming;

namespace CsCode.Ui.Theming;

/// <summary>An entry of a theme list: a group header or a theme.</summary>
public abstract record ThemeListItem;

/// <summary>"Dark", "Light", "High contrast": not selectable.</summary>
public sealed record ThemeGroupItem(string Title) : ThemeListItem;

/// <summary>A theme with its swatch: the editor background and three characteristic token colours (keyword, string, function).</summary>
public sealed record ThemeChoiceItem(string Id, string Label, ThemeKind Kind, IBrush Background, IBrush Border, IReadOnlyList<IBrush> Accents) : ThemeListItem
{
    public override string ToString() => Label; // what accessibility tools and the closed box's text search read
}

/// <summary>
/// The one implementation of "choose a colour theme" that the toolbar and Settings dropdown and the palette's Color Theme command share:
/// the grouped list with swatches, preview without persisting, revert, and commit (apply + persist <c>workbench.colorTheme</c>).
/// </summary>
public static class ThemeSelection
{
    public const string DarkGroup = "Dark";
    public const string LightGroup = "Light";
    public const string HighContrastGroup = "High contrast";

    public static string GroupOf(ThemeKind kind) => kind switch
    {
        ThemeKind.Light => LightGroup,
        ThemeKind.HighContrastDark or ThemeKind.HighContrastLight => HighContrastGroup,
        _ => DarkGroup,
    };

    /// <summary>Every theme grouped Dark / Light / High contrast (each group sorted by label), each group preceded by its header.</summary>
    public static IReadOnlyList<ThemeListItem> BuildItems(IReadOnlyList<ColorTheme> themes)
    {
        var items = new List<ThemeListItem>(themes.Count + 3);
        foreach (var group in new[] { DarkGroup, LightGroup, HighContrastGroup })
        {
            var members = themes.Where(t => GroupOf(t.Kind) == group).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (members.Count == 0) continue;
            items.Add(new ThemeGroupItem(group));
            foreach (var t in members) items.Add(Choice(t));
        }
        return items;
    }

    private static ThemeChoiceItem Choice(ColorTheme t)
    {
        var bg = t.TryGetColor("editor.background", out var b) ? b : t.Kind is ThemeKind.Light or ThemeKind.HighContrastLight ? new Rgba(255, 255, 255) : new Rgba(30, 30, 30);
        var fg = t.TryGetColor("editor.foreground", out var f) ? f : Contrast.Luminance(bg) > 0.4 ? new Rgba(0, 0, 0) : new Rgba(255, 255, 255);
        IBrush Brush(Rgba c) => new ImmutableSolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        var accents = new[]
        {
            Brush(TokenColor(t, fg, "keyword", "keyword.control", "storage.type", "storage")),
            Brush(TokenColor(t, fg, "string", "string.quoted")),
            Brush(TokenColor(t, fg, "entity.name.function", "support.function", "entity.name.type", "entity.name.class")),
        };
        // A swatch border that shows on both a dark and a light list background.
        var border = Contrast.Luminance(bg) > 0.4 ? new Rgba(0, 0, 0, 0x66) : new Rgba(255, 255, 255, 0x66);
        return new ThemeChoiceItem(t.Id, t.Label, t.Kind, Brush(bg), Brush(border), accents);
    }

    /// <summary>The foreground of the theme's last rule naming one of <paramref name="scopes"/> exactly (later rules win, as in the theme file).</summary>
    private static Rgba TokenColor(ColorTheme t, Rgba fallback, params string[] scopes)
    {
        foreach (var scope in scopes)
            for (var i = t.TokenColors.Count - 1; i >= 0; i--)
            {
                var rule = t.TokenColors[i];
                if (rule.Foreground is { } hex && rule.Scopes.Contains(scope) && Rgba.TryParse(hex, out var c)) return c;
            }
        return fallback;
    }

    /// <summary>Shows a theme without selecting it (no persistence) when the service can preview; otherwise selects it (test doubles).</summary>
    public static void Preview(IColorThemeService themes, string themeId)
    {
        if (themes is IColorThemePreview preview) preview.Preview(themeId);
        else if (themes.Current.Id != themeId) _ = themes.SetCurrentAsync(themeId);
    }

    /// <summary>Ends a preview: back to the selection (<paramref name="selectedId"/> is what was selected when the picker opened).</summary>
    public static void EndPreview(IColorThemeService themes, string selectedId)
    {
        if (themes is IColorThemePreview preview) preview.EndPreview();
        else if (themes.Current.Id != selectedId) _ = themes.SetCurrentAsync(selectedId);
    }

    /// <summary>
    /// Applies and persists a theme: <see cref="IColorThemeService.SetCurrentAsync"/> (the editor's service persists the label itself) and the
    /// label written to <c>workbench.colorTheme</c>, so a service that does not persist (or another window) gets the choice too. Writing the same
    /// value twice is a no-op in the settings file.
    /// </summary>
    public static async Task CommitAsync(IColorThemeService themes, IConfigurationService? config, string themeId)
    {
        await themes.SetCurrentAsync(themeId).ConfigureAwait(true);
        if (config is null) return;
        var label = themes.Available.FirstOrDefault(t => t.Id == themeId)?.Label ?? themeId;
        await config.UpdateAsync(SettingKeys.WorkbenchColorTheme, label).ConfigureAwait(true);
    }

    /// <summary>True when <paramref name="value"/> (a <c>workbench.colorTheme</c> value: a label, or an id) names <paramref name="theme"/>.</summary>
    public static bool Names(ColorTheme theme, string? value) =>
        value is not null && (string.Equals(theme.Id, value, StringComparison.OrdinalIgnoreCase) || string.Equals(theme.Label, value, StringComparison.OrdinalIgnoreCase));
}
