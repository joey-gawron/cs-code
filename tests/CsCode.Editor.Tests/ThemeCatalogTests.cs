using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Theme dropdown support in the theme service (docs/briefs/themes-languages-treesitter.md A2/A3): the 21 themes embedded in TextMateSharp.Grammars
/// are registered, a picker can preview a theme without selecting or persisting it (<see cref="IColorThemePreview"/>), and the shell's completion
/// post-processes every theme the service hands out.
/// </summary>
public class ThemeCatalogTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly ColorThemeService _svc;

    public ThemeCatalogTests() => _svc = new ColorThemeService(_host.Services);

    public void Dispose()
    {
        _svc.Dispose();
        _host.Dispose();
    }

    private static ColorTheme Theme(string id, string label, string background = "#101010") =>
        new(id, label, ThemeKind.Dark, new Dictionary<string, Rgba> { ["editor.background"] = Rgba.Parse(background) }, []);

    // ---- the catalogue ----

    [Fact]
    public void All_21_embedded_themes_are_registered_with_their_catalogue_ids_labels_and_kinds()
    {
        Assert.Equal(21, BuiltinColorThemes.All.Count);
        foreach (var d in BuiltinColorThemes.All)
        {
            var t = Assert.Single(_svc.Available, x => x.Id == d.Id);
            Assert.Equal(d.Label, t.Label);
            var kind = d.UiTheme switch { "vs" => ThemeKind.Light, "hc-black" => ThemeKind.HighContrastDark, "hc-light" => ThemeKind.HighContrastLight, _ => ThemeKind.Dark };
            Assert.Equal(kind, t.Kind);
            Assert.True(t.TokenColors.Count > 0, d.Label + " has no token colours");
            Assert.True(BuiltinColorThemes.IsBuiltin(d.Id));
        }
        Assert.Equal("vscode.dark-plus", _svc.Current.Id); // the editor's own default, before the shell registers its themes
    }

    [Theory]
    [InlineData("dark_plus.json", "dark_vs.json")]
    [InlineData("light_plus.json", "light_vs.json")]
    [InlineData("hc_light.json", "light_vs.json")]
    public void Includes_are_resolved_from_the_embedded_resources(string file, string included)
    {
        var d = BuiltinColorThemes.All.Single(x => x.File == file);
        var theme = BuiltinColorThemes.Load(d);
        var inner = BuiltinColorThemes.Load(BuiltinColorThemes.All.Single(x => x.File == included));
        // The included theme's token rules come first, then the including theme's own: it has strictly more.
        Assert.True(theme.TokenColors.Count > inner.TokenColors.Count, $"{file}: {theme.TokenColors.Count} rules, {included}: {inner.TokenColors.Count}");
        Assert.Equal(inner.Colors["editor.background"], theme.Colors["editor.background"]);
    }

    [Fact]
    public void Every_embedded_file_exists_and_unknown_files_read_as_null()
    {
        foreach (var d in BuiltinColorThemes.All) Assert.NotNull(BuiltinColorThemes.ReadResource(d.File));
        Assert.Null(BuiltinColorThemes.ReadResource("no-such-theme.json"));
        Assert.False(BuiltinColorThemes.IsBuiltin("vs2022-dark"));
    }

    [Fact]
    public void Embedded_themes_are_selectable_by_label_like_a_settings_json_value()
    {
        _host.Config.Set(SettingKeys.WorkbenchColorTheme, "Solarized Light");
        Assert.Equal("vscode.solarized-light", _svc.Current.Id);
        _host.Config.Set(SettingKeys.WorkbenchColorTheme, "dracula"); // labels match case-insensitively
        Assert.Equal("textmatesharp.dracula", _svc.Current.Id);
    }

    // ---- preview ----

    [Fact]
    public async Task A_preview_shows_a_theme_without_selecting_or_persisting_it_and_ending_it_reverts()
    {
        var fired = new List<string>();
        using var sub = _svc.Changed.Subscribe(t => fired.Add(t.Id));
        var preview = (IColorThemePreview)_svc;

        preview.Preview("vscode.monokai");
        Assert.Equal("vscode.monokai", _svc.Current.Id);
        Assert.Equal("vscode.monokai", preview.PreviewedThemeId);
        Assert.Equal("vscode.dark-plus", _svc.SelectedThemeId);
        Assert.Equal(["vscode.monokai"], fired);
        preview.Preview("Abyss"); // labels work too
        Assert.Equal("vscode.abyss", _svc.Current.Id);
        preview.Preview("vscode.abyss"); // the same theme again: nothing fires
        Assert.Equal(2, fired.Count);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(_host.Config.Get<string>(SettingKeys.WorkbenchColorTheme)); // a preview writes nothing

        preview.EndPreview();
        Assert.Equal("vscode.dark-plus", _svc.Current.Id);
        Assert.Null(preview.PreviewedThemeId);
        Assert.Equal(["vscode.monokai", "vscode.abyss", "vscode.dark-plus"], fired);
        preview.EndPreview(); // no preview: a no-op
        Assert.Equal(3, fired.Count);
    }

    [Fact]
    public void An_unknown_theme_is_not_previewed()
    {
        var preview = (IColorThemePreview)_svc;
        preview.Preview("no such theme");
        Assert.Null(preview.PreviewedThemeId);
        Assert.Equal("vscode.dark-plus", _svc.Current.Id);
    }

    [Fact]
    public async Task Choosing_during_a_preview_commits_ends_the_preview_and_persists_the_label()
    {
        var preview = (IColorThemePreview)_svc;
        preview.Preview("vscode.monokai");
        await _svc.SetCurrentAsync("vscode.quiet-light", TestContext.Current.CancellationToken);
        Assert.Null(preview.PreviewedThemeId);
        Assert.Equal("vscode.quiet-light", _svc.Current.Id);
        Assert.Equal("vscode.quiet-light", _svc.SelectedThemeId);
        await TestHost.WaitUntil(() => _host.Config.Get<string>(SettingKeys.WorkbenchColorTheme) == "Quiet Light", "persisted label");
    }

    [Fact]
    public void A_settings_change_during_a_preview_moves_the_selection_but_leaves_the_preview_on_screen()
    {
        var preview = (IColorThemePreview)_svc;
        preview.Preview("vscode.monokai");
        _host.Config.Set(SettingKeys.WorkbenchColorTheme, "Light+ (default light)"); // another window, or a hand edit of settings.json
        Assert.Equal("vscode.monokai", _svc.Current.Id);                     // the user is still arrowing through a list
        Assert.Equal("vscode.light-plus", _svc.SelectedThemeId);
        preview.EndPreview();                                                 // Escape: back to the selection, which is now Light+
        Assert.Equal("vscode.light-plus", _svc.Current.Id);
    }

    [Fact]
    public void Unregistering_the_previewed_theme_ends_the_preview_and_shows_the_selection()
    {
        var reg = _svc.Register(Theme("user.mine", "Mine"));
        var preview = (IColorThemePreview)_svc;
        preview.Preview("user.mine");
        Assert.Equal("user.mine", _svc.Current.Id);
        reg.Dispose();
        Assert.Null(preview.PreviewedThemeId);
        Assert.Equal("vscode.dark-plus", _svc.Current.Id);
    }

    [Fact]
    public void A_late_registration_of_the_persisted_theme_waits_while_a_preview_is_shown()
    {
        _host.Config.Set(SettingKeys.WorkbenchColorTheme, "Late Theme"); // not registered yet
        var preview = (IColorThemePreview)_svc;
        preview.Preview("vscode.red");
        using var reg = _svc.Register(Theme("late", "Late Theme"));
        Assert.Equal("vscode.red", _svc.Current.Id);
        Assert.Equal("late", _svc.SelectedThemeId);
        preview.EndPreview();
        Assert.Equal("late", _svc.Current.Id);
    }

    // ---- completion ----

    [Fact]
    public void A_completion_post_processes_every_theme_lazily_once_and_from_the_theme_as_registered()
    {
        var calls = new List<string>();
        var marker = (byte)1;
        ColorTheme Complete(ColorTheme t)
        {
            calls.Add(t.Id);
            Assert.False(t.Colors.ContainsKey("test.marker"), "completed a completed theme");
            return t with { Colors = new Dictionary<string, Rgba>(t.Colors) { ["test.marker"] = new Rgba(marker, 0, 0) } };
        }
        var fired = 0;
        using var sub = _svc.Changed.Subscribe(_ => fired++);

        _svc.SetCompletion(Complete);
        Assert.True(_svc.HasCompletion);
        Assert.Equal(1, fired);                                  // the current theme's colours changed
        Assert.Equal(["vscode.dark-plus"], calls);               // only the current one so far
        Assert.Equal(new Rgba(1, 0, 0), _svc.Current.Colors["test.marker"]);
        Assert.Equal(new Rgba(1, 0, 0), _svc.GetColor("test.marker"));
        Assert.Same(_svc.Current, _svc.Current);                 // cached

        Assert.All(_svc.Available, t => Assert.True(t.Colors.ContainsKey("test.marker"), t.Label));
        Assert.Equal(_svc.Available.Count, calls.Count);         // each theme once
        _ = _svc.Available;
        Assert.Equal(_svc.Available.Count, calls.Count);

        using var reg = _svc.Register(Theme("user.mine", "Mine"));
        Assert.True(_svc.Available.Single(t => t.Id == "user.mine").Colors.ContainsKey("test.marker"));

        marker = 2;
        _svc.SetCompletion(Complete);                            // a new completion starts from the registered themes again
        Assert.Equal(new Rgba(2, 0, 0), _svc.Current.Colors["test.marker"]);
        _svc.SetCompletion(null);
        Assert.False(_svc.Current.Colors.ContainsKey("test.marker"));
    }

    [Fact]
    public void A_completion_that_throws_leaves_the_theme_as_registered()
    {
        var raw = _svc.Current;
        _svc.SetCompletion(t => t.Id == "vscode.dark-plus" ? throw new InvalidOperationException("broken") : t);
        Assert.Same(raw, _svc.Current);
        Assert.Equal(BuiltinColorThemes.All.Count, _svc.Available.Count);
    }

    [Fact]
    public void A_preview_hands_out_the_completed_theme()
    {
        _svc.SetCompletion(t => t with { Colors = new Dictionary<string, Rgba>(t.Colors) { ["test.marker"] = new Rgba(9, 9, 9) } });
        ColorTheme? shown = null;
        using var sub = _svc.Changed.Subscribe(t => shown = t);
        ((IColorThemePreview)_svc).Preview("vscode.monokai");
        Assert.NotNull(shown);
        Assert.Equal(new Rgba(9, 9, 9), shown!.Colors["test.marker"]);
        Assert.Same(shown, _svc.Current);
    }
}
