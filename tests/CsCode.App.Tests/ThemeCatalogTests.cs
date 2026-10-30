using System.Globalization;
using System.Text;
using CsCode.App.Tests.Hosting;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// A3: the 21 colour themes embedded in TextMateSharp.Grammars as complete CsCode themes. Their token colours stay as they are; their workbench
/// colours are mapped onto the VS 2022 chrome (borrowed from related keys, else the nearest VS 2022 base), and every chrome text colour is checked
/// against its background (WCAG AA, 4.5:1) and repaired when it fails. docs/SHELL.md carries the generated contrast table; a test keeps it current.
/// </summary>
public class ThemeCatalogTests
{
    private static readonly IReadOnlyList<ColorTheme> Builtins = BuiltinColorThemes.LoadAll();

    /// <summary>The VS 2022 themes, then the embedded ones in catalogue order.</summary>
    internal static IEnumerable<(ColorTheme Theme, string Origin)> AllThemes() =>
        VsThemes.All.Select(t => (t, "CsCode (Visual Studio 2022 palette)"))
            .Concat(BuiltinColorThemes.All.Zip(Builtins, (d, t) => (t, d.Origin == ThemeOrigin.VsCode ? "VS Code built-in" : "TextMateSharp")));

    [Fact]
    public void The_catalogue_is_the_21_embedded_themes_with_unique_ids_and_labels()
    {
        Assert.Equal(21, BuiltinColorThemes.All.Count);
        Assert.Equal(21, Builtins.Count);
        var all = Builtins.Concat(VsThemes.All).ToList();
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, all.Select(t => t.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count()); // workbench.colorTheme stores labels
        Assert.All(Builtins, t => Assert.True(t.TokenColors.Count > 0, t.Label + " has no token colours"));
        Assert.Equal(2, Builtins.Count(t => t.Kind is ThemeKind.HighContrastDark or ThemeKind.HighContrastLight)); // hc_black, hc_light
    }

    [Theory]
    [InlineData("vscode.monokai", ThemeKind.Dark)]
    [InlineData("vscode.solarized-light", ThemeKind.Light)]
    [InlineData("vscode.quiet-light", ThemeKind.Light)]
    [InlineData("vscode.hc-black", ThemeKind.HighContrastDark)]
    [InlineData("vscode.hc-light", ThemeKind.HighContrastLight)]
    [InlineData("textmatesharp.dracula", ThemeKind.Dark)]
    [InlineData("textmatesharp.atom-one-light", ThemeKind.Light)]
    public void Kinds_come_from_the_contribution_ui_theme(string id, ThemeKind kind) => Assert.Equal(kind, Builtins.Single(t => t.Id == id).Kind);

    [Fact]
    public void Includes_are_resolved_so_light_high_contrast_has_the_colours_of_light_vs()
    {
        var hcLight = Builtins.Single(t => t.Id == "vscode.hc-light");
        var lightVs = Builtins.Single(t => t.Id == "vscode.light-vs");
        Assert.Equal(lightVs.Colors["editor.background"], hcLight.Colors["editor.background"]);       // hc_light includes light_vs.json
        Assert.True(hcLight.TokenColors.Count > lightVs.TokenColors.Count);                            // and adds its own rules after them
        Assert.Equal(Rgba.Parse("#1E1E1E"), Builtins.Single(t => t.Id == "vscode.dark-plus").Colors["editor.background"]); // dark_plus includes dark_vs
    }

    public static TheoryData<string> ThemeIds()
    {
        var data = new TheoryData<string>();
        foreach (var (t, _) in AllThemes()) data.Add(t.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Completion_defines_every_published_id_keeps_token_colours_and_meets_wcag_aa_in_the_chrome(string id)
    {
        var theme = AllThemes().Single(t => t.Theme.Id == id).Theme;
        var result = ThemeCompletion.Analyze(theme);
        var missing = WorkbenchColors.All.Where(k => !result.Theme.Colors.ContainsKey(k)).ToArray();
        Assert.True(missing.Length == 0, $"{theme.Label} lacks {string.Join(", ", missing)}");
        Assert.Same(theme.TokenColors, result.Theme.TokenColors);
        Assert.Equal(theme.Id, result.Theme.Id);
        Assert.Equal(theme.Label, result.Theme.Label);
        Assert.Equal(theme.Kind, result.Theme.Kind);
        var unresolved = result.Unresolved.Select(c => $"{c.Pair.Element} {c.FinalRatio:F2}").ToArray();
        Assert.True(unresolved.Length == 0, $"{theme.Label}: below AA after repair: {string.Join("; ", unresolved)}");
        // A key the theme defines keeps its value unless the contrast check had to move it (a text colour, or the surface under white text).
        var repairedKeys = result.Checks.Where(c => c.Repaired is not null).Select(c => c.Pair.Foreground)
            .Concat(result.Checks.Where(c => c.RepairedBackground is not null).Select(c => c.Pair.Background)).ToHashSet();
        foreach (var (key, value) in theme.Colors)
            if (!repairedKeys.Contains(key)) Assert.Equal(value, result.Theme.Colors[key]);
        // Completing a completed theme changes nothing (the editor's service and the applier may both run it).
        Assert.Same(result.Theme, ThemeCompletion.Complete(result.Theme));
    }

    [Fact]
    public void The_remote_indicator_badge_is_readable_in_every_theme()
    {
        // The badge is always in the status bar ('$(remote)' while disconnected). Themes that set only its background (One Light: light grey,
        // Dracula: lavender, Solarized Light: olive) kept the base's white text: 1.5, 2.4 and 2.7 to 1.
        foreach (var (theme, _) in AllThemes())
        {
            var check = ThemeCompletion.Analyze(theme).Checks.Single(c => c.Pair.Foreground == "statusBarItem.remoteForeground");
            Assert.Equal("statusBarItem.remoteBackground", check.Pair.Background);
            Assert.True(check.FinalRatio >= Contrast.AaNormalText, $"{theme.Label}: {check.FinalRatio:F2}");
            var completed = ThemeCompletion.Complete(theme).Colors;
            var surface = Contrast.Over(completed["statusBarItem.remoteBackground"], completed["statusBar.background"]);
            var ratio = Contrast.Ratio(Contrast.Over(completed["statusBarItem.remoteForeground"], surface), surface);
            Assert.True(ratio >= Contrast.AaNormalText, $"{theme.Label}: the completed theme's badge is {ratio:F2}:1");
        }
    }

    [Theory]
    [InlineData(VsThemes.DarkId)]
    [InlineData(VsThemes.BlueId)]
    [InlineData(VsThemes.LightId)]
    public void The_vs2022_themes_and_bases_pass_every_contrast_check_without_repair(string id)
    {
        var theme = VsThemes.All.Single(t => t.Id == id);
        var result = ThemeCompletion.Analyze(theme);
        Assert.True(!result.Failures.Any(), $"{theme.Label}: " + string.Join("; ", result.Failures.Select(f => $"{f.Pair.Element} ({f.Pair.Foreground} on {f.Pair.Background}) {f.Ratio:F2}")));
        Assert.Same(theme, result.Theme);
    }

    [Fact]
    public void The_high_contrast_bases_define_every_id_and_pass_without_repair()
    {
        foreach (var b in new[] { VsThemes.HighContrastDark, VsThemes.HighContrastLight })
        {
            Assert.True(ThemeCompletion.DefinesAll(b), b.Label);
            var result = ThemeCompletion.Analyze(b);
            Assert.True(!result.Failures.Any(), $"{b.Label}: " + string.Join("; ", result.Failures.Select(f => $"{f.Pair.Element} {f.Ratio:F2}")));
        }
    }

    [Fact]
    public void Missing_chrome_keys_are_borrowed_from_related_theme_keys_before_the_vs2022_base()
    {
        var monokai = ThemeCompletion.Analyze(Builtins.Single(t => t.Id == "vscode.monokai"));
        // Monokai defines its title bar, not the shell's toolbar: the toolbar is the same band in VS 2022.
        Assert.Equal(ColorSource.Borrowed, monokai.Sources["toolbar.background"]);
        Assert.Equal(monokai.Theme.Colors["titleBar.activeBackground"], monokai.Theme.Colors["toolbar.background"]);
        // The Quick Launch box is an input box.
        Assert.Equal(monokai.Theme.Colors["input.background"], monokai.Theme.Colors["commandCenter.background"]);
        // Dark+ defines almost nothing of the chrome: the VS 2022 Dark base fills it.
        var darkPlus = ThemeCompletion.Analyze(Builtins.Single(t => t.Id == "vscode.dark-plus"));
        Assert.Equal(ColorSource.Base, darkPlus.Sources["statusBar.background"]);
        Assert.Equal(VsThemes.Dark.Colors["statusBar.background"], darkPlus.Theme.Colors["statusBar.background"]);
        // Light themes fill from the Light base, high-contrast ones from the high-contrast bases.
        var hcBlack = ThemeCompletion.Analyze(Builtins.Single(t => t.Id == "vscode.hc-black"));
        Assert.Equal(VsThemes.HighContrastDark.Colors["panel.border"], hcBlack.Theme.Colors["panel.border"]);
        var quiet = ThemeCompletion.Analyze(Builtins.Single(t => t.Id == "vscode.quiet-light"));
        Assert.Equal(Rgba.Parse("#333333"), quiet.Theme.Colors["editor.foreground"]); // its global token colour (the file's "colors" has none)
    }

    [Fact]
    public void A_foreground_follows_its_background_so_a_base_surface_keeps_its_base_text()
    {
        // A light status bar with dark text and no debugging colour: the debugging bar comes from the base (VS orange) and so does its white
        // text. Borrowing the theme's dark status text would put it on orange.
        var theme = new ColorTheme("t", "T", ThemeKind.Light, new Dictionary<string, Rgba>
        {
            ["editor.background"] = Rgba.Parse("#FFFFFF"), ["statusBar.background"] = Rgba.Parse("#E8E8E8"), ["statusBar.foreground"] = Rgba.Parse("#222222"),
        }, []);
        var r = ThemeCompletion.Analyze(theme);
        Assert.Equal(ColorSource.Base, r.Sources["statusBar.debuggingBackground"]);
        Assert.Equal(ColorSource.Base, r.Sources["statusBar.debuggingForeground"]);
        Assert.Equal(VsThemes.Light.Colors["statusBar.debuggingForeground"], r.Theme.Colors["statusBar.debuggingForeground"]);
        Assert.Equal(Rgba.Parse("#222222"), r.Theme.Colors["statusBar.foreground"]);
        // With its own background a foreground is borrowed: One Dark's no-folder bar is its status bar colour, and so is the text.
        var oneDark = ThemeCompletion.Analyze(Builtins.Single(t => t.Id == "textmatesharp.one-dark"));
        Assert.Equal(ColorSource.Borrowed, oneDark.Sources["statusBar.noFolderForeground"]);
        Assert.Equal(oneDark.Theme.Colors["statusBar.foreground"], oneDark.Theme.Colors["statusBar.noFolderForeground"]);
    }

    [Fact]
    public void Failing_pairs_are_repaired_by_the_smallest_move_towards_white_or_black()
    {
        var theme = new ColorTheme("t", "T", ThemeKind.Dark, new Dictionary<string, Rgba>
        {
            ["editor.background"] = Rgba.Parse("#202020"), ["statusBar.background"] = Rgba.Parse("#3060A0"), ["statusBar.foreground"] = Rgba.Parse("#5080C0"),
        }, []);
        var result = ThemeCompletion.Analyze(theme);
        var status = result.Checks.Single(c => c.Pair.Element == "Status bar");
        Assert.True(status.Failed);
        Assert.NotNull(status.Repaired);
        Assert.True(status.FinalRatio >= Contrast.AaNormalText);
        Assert.True(status.FinalRatio < 6, "moved further than needed: " + status.FinalRatio);
        var fixedFg = result.Theme.Colors["statusBar.foreground"];
        Assert.True(fixedFg.B > fixedFg.R, "kept its hue: " + fixedFg.ToHex()); // mixed towards white, still blue
    }

    [Fact]
    public void Wcag_arithmetic_matches_the_reference_values()
    {
        Assert.Equal(21.0, Contrast.Ratio(Rgba.Parse("#000000"), Rgba.Parse("#FFFFFF")), 2);
        Assert.Equal(1.0, Contrast.Ratio(Rgba.Parse("#777777"), Rgba.Parse("#777777")), 3);
        Assert.Equal(4.48, Contrast.Ratio(Rgba.Parse("#777777"), Rgba.Parse("#FFFFFF")), 2); // the classic "#777 on white just fails AA"
        Assert.Equal(new Rgba(128, 128, 128), Contrast.Over(new Rgba(255, 255, 255, 128), Rgba.Parse("#000000")));
    }

    // ------------------------------------------------------------------ the documented contrast table

    internal const string BeginMarker = "<!-- THEME-CONTRAST-BEGIN -->";
    internal const string EndMarker = "<!-- THEME-CONTRAST-END -->";

    /// <summary>The generated section of docs/SHELL.md: per theme, where the chrome colours came from and which pairs failed AA and how they were repaired.</summary>
    internal static string ContrastTable()
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append(inv, $"{ThemeCompletion.ChromeText.Count} chrome text pairs checked per theme (WCAG AA, 4.5:1). Colours: theme / borrowed / VS 2022 base, of {WorkbenchColors.All.Count} published ids.\n\n");
        sb.Append("| Theme | Kind | Origin | Colours | Below AA before repair: element (foreground key) ratio, substitute, ratio after |\n|---|---|---|---|---|\n");
        foreach (var (theme, origin) in AllThemes())
        {
            var r = ThemeCompletion.Analyze(theme);
            var published = WorkbenchColors.All.Select(k => r.Sources[k]).ToArray();
            var counts = $"{published.Count(s => s == ColorSource.Theme)} / {published.Count(s => s == ColorSource.Borrowed)} / {published.Count(s => s == ColorSource.Base)}";
            var failures = r.Failures.ToList();
            var text = failures.Count == 0
                ? "none"
                : string.Join("; ", failures.Select(f => string.Create(inv,
                    $"{f.Pair.Element} (`{f.Pair.Foreground}`, {SourceWord(f.ForegroundSource)}) {f.Ratio:F2}, {Fix(f)} {f.FinalRatio:F2}")));
            sb.Append(inv, $"| {theme.Label} | {Kind(theme.Kind)} | {origin} | {counts} | {text} |\n");
        }
        return sb.ToString();

        static string Fix(ContrastCheck f) =>
            f.Repaired is { } fg ? fg.ToHex() : f.RepairedBackground is { } bg ? "background " + bg.ToHex() : "fixed with an earlier pair";
        static string SourceWord(ColorSource s) => s switch { ColorSource.Theme => "theme", ColorSource.Borrowed => "borrowed", _ => "base" };
        static string Kind(ThemeKind k) => k switch { ThemeKind.Light => "light", ThemeKind.HighContrastDark => "HC dark", ThemeKind.HighContrastLight => "HC light", _ => "dark" };
    }

    [Fact]
    public void Shell_docs_carry_the_current_contrast_table()
    {
        var expected = ContrastTable();
        var shots = DevHost.ScreenshotDirectory();
        File.WriteAllText(Path.Combine(shots, "theme-contrast.md"), expected);
        var doc = File.ReadAllText(Path.Combine(Directory.GetParent(shots)!.Parent!.FullName, "docs", "SHELL.md")).Replace("\r\n", "\n");
        var start = doc.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = doc.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "docs/SHELL.md needs the " + BeginMarker + " section");
        var actual = doc[(start + BeginMarker.Length)..end].Trim('\n') + "\n";
        Assert.True(actual == expected, "docs/SHELL.md's contrast table is stale; the current one is in .scratch/screenshots/theme-contrast.md");
    }
}
