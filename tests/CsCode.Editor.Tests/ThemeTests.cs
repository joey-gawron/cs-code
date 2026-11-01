using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 6: VS Code theme loading, the colour fallback chain, scope-selector specificity and the theme service.</summary>
public class ThemeTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly ColorThemeService _svc;

    public ThemeTests() => _svc = new ColorThemeService(_host.Services);

    public void Dispose()
    {
        _svc.Dispose();
        _host.Dispose();
    }

    private static ColorTheme LoadBuiltin(string file, string id = "t", string? ui = null)
    {
        var reader = new ColorThemeService.BuiltinThemeReader();
        return VsCodeThemeLoader.LoadSync(id, null, ui, reader.ReadSync(file)!, ColorThemeService.BuiltinPathPrefix + file, (_, rel) =>
        {
            var f = rel.TrimStart('.', '/');
            return reader.ReadSync(f) is { } t ? (ColorThemeService.BuiltinPathPrefix + f, t) : null;
        });
    }

    // ---- real VS Code theme files ----

    [Fact]
    public void Dark_plus_resolves_its_include_chain_colors_and_token_rules()
    {
        var t = LoadBuiltin("dark_plus.json");
        Assert.Equal(ThemeKind.Dark, t.Kind);
        Assert.Equal("Dark+ (default dark)", t.Label);
        Assert.Equal(Rgba.Parse("#1E1E1E"), t.Colors["editor.background"]);            // from the INCLUDED dark_vs.json
        Assert.True(t.Colors.ContainsKey("editor.foreground"));
        // included rules come first, the including theme's rules after (so they win ties)
        var firstOwn = t.TokenColors.ToList().FindIndex(r => r.Scopes.Contains("entity.name.function"));
        var includedComment = t.TokenColors.ToList().FindIndex(r => r.Scopes.Contains("comment"));
        Assert.True(includedComment >= 0 && firstOwn > includedComment, $"comment@{includedComment} function@{firstOwn}");
        Assert.True(t.TokenColors.Count > 40);
    }

    [Fact]
    public void Light_plus_and_high_contrast_have_the_right_kind()
    {
        // light_plus/light_vs carry no "type" (VS Code puts it in the extension manifest): the kind is inferred from editor.background
        Assert.Equal(ThemeKind.Light, LoadBuiltin("light_plus.json").Kind);
        Assert.Equal(ThemeKind.Dark, LoadBuiltin("dark_plus.json").Kind);
        // the contribution's uiTheme is authoritative when given
        Assert.Equal(ThemeKind.HighContrastDark, LoadBuiltin("hc_black.json", ui: "hc-black").Kind);
        Assert.Equal(ThemeKind.HighContrastLight, LoadBuiltin("hc_light.json", ui: "hc-light").Kind);
    }

    [Fact]
    public void Monokai_style_theme_with_scope_arrays_and_font_styles()
    {
        var t = LoadBuiltin("monokai-color-theme.json");
        Assert.Equal(ThemeKind.Dark, t.Kind);                                          // the file has "type": "dark"
        Assert.Contains(t.TokenColors, r => r.Scopes.Count > 1);                       // an array / comma list of scopes
        Assert.Contains(t.TokenColors, r => r.FontStyle is { Length: > 0 } fs && fs.Contains("italic"));
        Assert.True(t.Colors.ContainsKey("editor.background"));
        // every scope in every rule is a clean selector (no stray commas or whitespace)
        Assert.All(t.TokenColors.SelectMany(r => r.Scopes), s => { Assert.DoesNotContain(',', s); Assert.Equal(s.Trim(), s); });
    }

    [Fact]
    public void All_embedded_builtin_themes_load()
    {
        var count = 0;
        foreach (var file in new[]
                 {
                     "abyss-color-theme.json", "atom-one-dark-color-theme.json", "atom-one-light-color-theme.json", "dark_plus.json", "dark_vs.json",
                     "dimmed-monokai-color-theme.json", "dracula-color-theme.json", "hc_black.json", "hc_light.json", "kimbie-dark-color-theme.json",
                     "light_plus.json", "light_vs.json", "monokai-color-theme.json", "onedark-color-theme.json", "quietlight-color-theme.json",
                     "Red-color-theme.json", "solarized-dark-color-theme.json", "solarized-light-color-theme.json", "tomorrow-night-blue-color-theme.json",
                     "visual-studio-dark-theme.json", "visual-studio-light-theme.json",
                 })
        {
            var t = LoadBuiltin(file);
            Assert.True(t.TokenColors.Count > 0 || t.Colors.Count > 0, file);
            count++;
        }
        Assert.Equal(21, count);
    }

    // ---- parser behaviour ----

    [Fact]
    public void Parser_handles_jsonc_scope_strings_with_commas_and_default_rules()
    {
        var t = VsCodeThemeLoader.Parse("x", null, null, """
            {
              // a comment
              "name": "Test", "type": "light",
              "colors": { "editor.background": "#ffffff", "bad": "notacolor", "alpha": "#11223344", },
              "tokenColors": [
                { "settings": { "foreground": "#111111", "background": "#EEEEEE" } },
                { "scope": "comment, string.quoted ,  ", "settings": { "foreground": "#008000", "fontStyle": "italic  bold" } },
                { "scope": ["keyword", "storage.type, storage.modifier"], "settings": { "fontStyle": "" } },
                { "scope": "meta.nothing", "settings": { } },
              ],
            }
            """);
        Assert.Equal("Test", t.Label);
        Assert.Equal(ThemeKind.Light, t.Kind);
        Assert.Equal(Rgba.Parse("#FFFFFF"), t.Colors["editor.background"]);
        Assert.False(t.Colors.ContainsKey("bad"));
        Assert.Equal(new Rgba(0x11, 0x22, 0x33, 0x44), t.Colors["alpha"]);
        Assert.Equal(3, t.TokenColors.Count);                                         // the empty "meta.nothing" rule is dropped
        Assert.Empty(t.TokenColors[0].Scopes);                                        // global default rule
        Assert.Equal(["comment", "string.quoted"], t.TokenColors[1].Scopes);
        Assert.Equal("italic bold", t.TokenColors[1].FontStyle);                      // whitespace normalised
        Assert.Equal(["keyword", "storage.type", "storage.modifier"], t.TokenColors[2].Scopes);
        Assert.Equal(string.Empty, t.TokenColors[2].FontStyle);                       // "" = reset, distinct from "not set"
    }

    [Fact]
    public void Parser_reads_semantic_token_colors_in_string_object_and_flag_forms()
    {
        var t = VsCodeThemeLoader.Parse("x", null, null, """
            { "semanticTokenColors": {
                "enumMember": "#FF0000",
                "variable.readonly:java": { "foreground": "#00FF00", "italic": true, "bold": true },
                "type": { "fontStyle": "underline" },
                "broken": { },
                "worse": 5
            } }
            """);
        Assert.NotNull(t.SemanticTokenColors);
        Assert.Equal("#FF0000", t.SemanticTokenColors!["enumMember"].Foreground);
        Assert.Equal("italic bold", t.SemanticTokenColors["variable.readonly:java"].FontStyle);
        Assert.Equal("underline", t.SemanticTokenColors["type"].FontStyle);
        Assert.False(t.SemanticTokenColors.ContainsKey("broken"));
        Assert.False(t.SemanticTokenColors.ContainsKey("worse"));
    }

    [Fact]
    public void The_themes_semantic_highlighting_flag_is_read_and_inherited_through_includes()
    {
        Assert.True(VsCodeThemeLoader.Parse("x", null, null, """{ "semanticHighlighting": true }""").SemanticHighlighting);
        Assert.False(VsCodeThemeLoader.Parse("x", null, null, """{ "semanticHighlighting": false }""").SemanticHighlighting);
        Assert.False(VsCodeThemeLoader.Parse("x", null, null, "{ }").SemanticHighlighting);
        Assert.True(LoadBuiltin("dark_plus.json").SemanticHighlighting); // VS Code's Dark+ turns it on
        (string, string)? Parent(string from, string rel) => ("parent.json", """{ "semanticHighlighting": true }""");
        Assert.True(VsCodeThemeLoader.LoadSync("c", null, null, """{ "include": "parent.json" }""", "child.json", Parent).SemanticHighlighting);
        Assert.False(VsCodeThemeLoader.LoadSync("c", null, null, """{ "include": "parent.json", "semanticHighlighting": false }""", "child.json", Parent).SemanticHighlighting);
    }

    [Theory]
    [InlineData("dark", null, ThemeKind.Dark)]
    [InlineData("light", null, ThemeKind.Light)]
    [InlineData("hc", null, ThemeKind.HighContrastDark)]
    [InlineData("hc-light", null, ThemeKind.HighContrastLight)]
    [InlineData(null, null, ThemeKind.Dark)]
    [InlineData("dark", "vs", ThemeKind.Light)]            // the contribution's uiTheme wins
    [InlineData(null, "hc-black", ThemeKind.HighContrastDark)]
    public void Theme_kind_from_type_and_ui_theme(string? type, string? ui, ThemeKind expected)
    {
        var json = type is null ? "{}" : $"{{ \"type\": \"{type}\" }}";
        Assert.Equal(expected, VsCodeThemeLoader.Parse("x", null, ui, json).Kind);
    }

    [Fact]
    public void Legacy_top_level_settings_array_is_read_as_token_colors_and_tmTheme_paths_are_ignored()
    {
        var legacy = VsCodeThemeLoader.Parse("x", null, null, """{ "settings": [ { "settings": { "foreground": "#010101" } }, { "scope": "a", "settings": { "foreground": "#020202" } } ] }""");
        Assert.Equal(2, legacy.TokenColors.Count);
        var tm = VsCodeThemeLoader.Parse("x", null, null, """{ "tokenColors": "./old.tmTheme", "colors": { "editor.background": "#000000" } }""");
        Assert.Empty(tm.TokenColors);
        Assert.Single(tm.Colors);
    }

    [Fact]
    public void Include_chain_cycles_missing_files_and_garbage_are_reported_not_hung()
    {
        string? Read(string from, string rel) => null;
        Assert.Throws<FileNotFoundException>(() => VsCodeThemeLoader.LoadSync("x", null, null, """{ "include": "./nope.json" }""", "a.json", (f, r) => Read(f, r) is { } t ? (r, t) : null));
        Assert.Throws<InvalidOperationException>(() => VsCodeThemeLoader.LoadSync("x", null, null, """{ "include": "./a.json" }""", "a.json", (_, r) => ("a.json", """{ "include": "./a.json" }""")));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VsCodeThemeLoader.Parse("x", null, null, "[1,2,3]"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VsCodeThemeLoader.Parse("x", null, null, "{ not json"));
    }

    [Fact]
    public void Child_theme_overrides_included_colors_key_by_key()
    {
        var t = VsCodeThemeLoader.LoadSync("c", null, null, """{ "include": "./p.json", "colors": { "a": "#222222" } }""", "c.json",
            (_, _) => ("p.json", """{ "name": "Parent", "type": "light", "colors": { "a": "#111111", "b": "#333333" } }"""));
        Assert.Equal(Rgba.Parse("#222222"), t.Colors["a"]);
        Assert.Equal(Rgba.Parse("#333333"), t.Colors["b"]);
        Assert.Equal("Parent", t.Label);                // inherited name when the child has none
        Assert.Equal(ThemeKind.Light, t.Kind);          // inherited type
    }

    // ---- colour fallback chain ----

    [Fact]
    public void Default_table_documents_at_least_150_keys()
    {
        Assert.True(DefaultColors.KnownKeys.Count >= 150, $"only {DefaultColors.KnownKeys.Count} keys");
    }

    [Theory]
    [InlineData(ThemeKind.Dark, "editor.background", "#1E1E1E")]
    [InlineData(ThemeKind.Light, "editor.background", "#FFFFFF")]
    [InlineData(ThemeKind.HighContrastDark, "editor.background", "#000000")]
    [InlineData(ThemeKind.Dark, "editorError.foreground", "#F14C4C")]
    [InlineData(ThemeKind.Light, "editorError.foreground", "#E51400")]
    [InlineData(ThemeKind.Dark, "statusBar.background", "#007ACC")]
    [InlineData(ThemeKind.Dark, "statusBar.debuggingBackground", "#CC6633")]
    public void A_theme_that_omits_keys_gets_the_documented_defaults_for_its_kind(ThemeKind kind, string key, string expected)
    {
        var empty = new Dictionary<string, Rgba>();
        Assert.Equal(Rgba.Parse(expected), DefaultColors.Resolve(key, empty, kind));
    }

    [Fact]
    public void Derived_keys_follow_the_theme_not_a_fixed_default()
    {
        var colors = new Dictionary<string, Rgba>
        {
            ["editor.background"] = Rgba.Parse("#101010"),
            ["editorWidget.background"] = Rgba.Parse("#202020"),
            ["editor.selectionBackground"] = Rgba.Parse("#FF000080"),
        };
        Assert.Equal(Rgba.Parse("#101010"), DefaultColors.Resolve("editorGutter.background", colors, ThemeKind.Dark));      // alias of editor.background
        Assert.Equal(Rgba.Parse("#202020"), DefaultColors.Resolve("editorSuggestWidget.background", colors, ThemeKind.Dark)); // alias of editorWidget.background
        Assert.Equal(Rgba.Parse("#101010"), DefaultColors.Resolve("minimap.background", colors, ThemeKind.Dark));
        // inactive selection = the theme's selection colour at half alpha
        var inactive = DefaultColors.Resolve("editor.inactiveSelectionBackground", colors, ThemeKind.Dark)!.Value;
        Assert.Equal((byte)0xFF, inactive.R);
        Assert.Equal((byte)0x40, inactive.A);
    }

    [Fact]
    public void Tab_inactive_background_prefers_the_themes_tab_strip_colour_when_it_has_one()
    {
        var withStrip = new Dictionary<string, Rgba> { ["editorGroupHeader.tabsBackground"] = Rgba.Parse("#ABCDEF") };
        Assert.Equal(Rgba.Parse("#ABCDEF"), DefaultColors.Resolve("tab.inactiveBackground", withStrip, ThemeKind.Dark));
        Assert.Equal(Rgba.Parse("#2D2D2D"), DefaultColors.Resolve("tab.inactiveBackground", new Dictionary<string, Rgba>(), ThemeKind.Dark)); // own default otherwise
        var explicitOne = new Dictionary<string, Rgba> { ["tab.inactiveBackground"] = Rgba.Parse("#123456"), ["editorGroupHeader.tabsBackground"] = Rgba.Parse("#ABCDEF") };
        Assert.Equal(Rgba.Parse("#123456"), DefaultColors.Resolve("tab.inactiveBackground", explicitOne, ThemeKind.Dark));                 // explicit wins
    }

    [Fact]
    public void Unknown_keys_are_null_in_the_table_and_transparent_through_the_service()
    {
        Assert.Null(DefaultColors.Resolve("no.such.key", new Dictionary<string, Rgba>(), ThemeKind.Dark));
        Assert.Equal(new Rgba(0, 0, 0, 0), _svc.GetColor("no.such.key"));
    }

    // ---- scope selector specificity (TextMate semantics) ----

    private static TokenColorResolver Resolver(params (string[] Scopes, string Fg, string? Style)[] rules)
    {
        var tokenColors = new List<TokenColorRule> { new([], "#CCCCCC", null, null) };
        tokenColors.AddRange(rules.Select(r => new TokenColorRule(r.Scopes, r.Fg, null, r.Style)));
        var theme = new ColorTheme("t", "t", ThemeKind.Dark, new Dictionary<string, Rgba>(), tokenColors);
        return new TokenColorResolver(theme, Rgba.Parse("#CCCCCC"), Rgba.Parse("#000000"));
    }

    [Fact]
    public void More_specific_selector_beats_the_general_one()
    {
        var r = Resolver((["comment"], "#00FF00", null), (["comment.line"], "#FF0000", null));
        Assert.Equal(Rgba.Parse("#FF0000"), r.Resolve(["source.cs", "comment.line.double-slash.cs"]).Foreground);
        Assert.Equal(Rgba.Parse("#00FF00"), r.Resolve(["source.cs", "comment.block.cs"]).Foreground);
    }

    [Fact]
    public void Unmatched_scopes_get_the_default_foreground()
    {
        var r = Resolver((["comment"], "#00FF00", null));
        Assert.Equal(Rgba.Parse("#CCCCCC"), r.Resolve(["source.cs", "variable.other"]).Foreground);
    }

    [Fact]
    public void The_deepest_scope_wins_over_an_enclosing_scope()
    {
        var r = Resolver((["string"], "#CE9178", null), (["constant.character.escape"], "#D7BA7D", null));
        // the escape sequence is the innermost scope inside a string
        var style = r.Resolve(["source.cs", "string.quoted.double.cs", "constant.character.escape.cs"]);
        Assert.Equal(Rgba.Parse("#D7BA7D"), style.Foreground);
        Assert.Equal(Rgba.Parse("#CE9178"), r.Resolve(["source.cs", "string.quoted.double.cs"]).Foreground);
    }

    [Fact]
    public void Parent_scope_selectors_apply_only_when_the_ancestor_is_on_the_stack()
    {
        var r = Resolver((["entity.name.function"], "#DCDCAA", null), (["meta.preprocessor entity.name.function"], "#9B9B9B", null));
        Assert.Equal(Rgba.Parse("#9B9B9B"), r.Resolve(["source.cs", "meta.preprocessor.cs", "entity.name.function.cs"]).Foreground);
        Assert.Equal(Rgba.Parse("#DCDCAA"), r.Resolve(["source.cs", "meta.function.cs", "entity.name.function.cs"]).Foreground);
    }

    [Fact]
    public void A_deeper_rule_inherits_the_properties_it_does_not_set_from_the_shallower_one()
    {
        // vscode-textmate trie semantics: comment.line sets only a colour, so italic comes from "comment"
        var r = Resolver((["comment"], "#00FF00", "italic"), (["comment.line"], "#FF0000", null));
        var s = r.Resolve(["source.cs", "comment.line.double-slash.cs"]);
        Assert.Equal(Rgba.Parse("#FF0000"), s.Foreground);
        Assert.True(s.Italic);
    }

    [Fact]
    public void An_empty_font_style_resets_an_inherited_one()
    {
        var r = Resolver((["comment"], "#00FF00", "italic bold"), (["comment.line"], "#FF0000", ""));
        var s = r.Resolve(["source.cs", "comment.line.x"]);
        Assert.False(s.Italic);
        Assert.False(s.Bold);
    }

    [Fact]
    public void A_scope_name_must_match_at_a_dot_boundary()
    {
        var r = Resolver((["string"], "#CE9178", null));
        Assert.Equal(Rgba.Parse("#CCCCCC"), r.Resolve(["source.cs", "stringent.thing"]).Foreground); // "stringent" is not under "string"
    }

    [Fact]
    public void Longer_parent_selectors_beat_shorter_ones_and_the_target_depth_beats_parents()
    {
        var r = Resolver(
            (["meta.a entity.name"], "#111111", null),
            (["meta.a meta.b entity.name"], "#222222", null),
            (["entity.name.function"], "#333333", null));
        Assert.Equal(Rgba.Parse("#222222"), r.Resolve(["source", "meta.a", "meta.b", "entity.name.x"]).Foreground);
        Assert.Equal(Rgba.Parse("#111111"), r.Resolve(["source", "meta.a", "entity.name.x"]).Foreground);
        // a deeper TARGET wins over a parent-qualified shallower one
        Assert.Equal(Rgba.Parse("#333333"), r.Resolve(["source", "meta.a", "meta.b", "entity.name.function.cs"]).Foreground);
    }

    [Theory]
    [InlineData("source.cs|comment.line.double-slash.cs", TokenKind.Comment)]
    [InlineData("source.cs|string.quoted.double.cs", TokenKind.String)]
    [InlineData("source.cs|string.quoted.double.cs|constant.character.escape.cs", TokenKind.String)]   // enclosing string still counts
    [InlineData("source.js|string.regexp.js", TokenKind.String)]
    [InlineData("source.js|string.template.js|meta.embedded.line.js|variable.other.js", TokenKind.Other)] // embedded code inside a string is code
    [InlineData("source.cs|keyword.control.cs", TokenKind.Other)]
    [InlineData("source.cs|stringent.x", TokenKind.Other)]
    public void Token_kind_comes_from_the_innermost_comment_string_or_embedded_scope(string stack, TokenKind expected) =>
        Assert.Equal(expected, TokenColorResolver.KindOf(stack.Split('|')));

    [Fact]
    public void Later_rules_win_ties_and_font_styles_combine_from_different_rules()
    {
        var r = Resolver((["keyword"], "#111111", null), (["keyword"], "#222222", "italic bold"));
        var s = r.Resolve(["source.cs", "keyword.control.cs"]);
        Assert.Equal(Rgba.Parse("#222222"), s.Foreground);
        Assert.True(s.Italic);
        Assert.True(s.Bold);
        Assert.False(s.Underline);
    }

    [Fact]
    public void Real_dark_plus_colours_a_csharp_keyword_and_a_comment()
    {
        var dark = LoadBuiltin("dark_plus.json");
        var r = new TokenColorResolver(dark, dark.Colors["editor.foreground"], dark.Colors["editor.background"]);
        Assert.Equal(Rgba.Parse("#569CD6"), r.Resolve(["source.cs", "keyword.type.int.cs"]).Foreground);
        Assert.Equal(Rgba.Parse("#6A9955"), r.Resolve(["source.cs", "comment.line.double-slash.cs"]).Foreground);
        Assert.Equal(Rgba.Parse("#DCDCAA"), r.Resolve(["source.cs", "entity.name.function.cs"]).Foreground);
    }

    // ---- service ----

    [Fact]
    public void Service_ships_dark_plus_light_plus_and_the_other_builtins_and_defaults_to_dark_plus()
    {
        Assert.Contains(_svc.Available, t => t.Label == "Dark+ (default dark)" && t.Kind == ThemeKind.Dark);
        Assert.Contains(_svc.Available, t => t.Label == "Light+ (default light)" && t.Kind == ThemeKind.Light);
        Assert.True(_svc.Available.Count >= 6);
        Assert.Equal("Dark+ (default dark)", _svc.Current.Label);
    }

    [Fact]
    public async Task SetCurrent_changes_theme_fires_changed_and_persists_the_label_in_workbench_colorTheme()
    {
        var fired = new List<ColorTheme>();
        _svc.Changed.Subscribe(fired.Add);
        await _svc.SetCurrentAsync("vscode.light-plus", TestContext.Current.CancellationToken);
        Assert.Equal("Light+ (default light)", _svc.Current.Label);
        Assert.Single(fired);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal("Light+ (default light)", _host.Config.Get<string>(SettingKeys.WorkbenchColorTheme));
        Assert.Equal(Rgba.Parse("#FFFFFF"), _svc.GetColor("editor.background"));
    }

    [Fact]
    public async Task SetCurrent_accepts_labels_case_insensitively_and_ignores_unknown_themes()
    {
        await _svc.SetCurrentAsync("light+ (DEFAULT light)", TestContext.Current.CancellationToken);
        Assert.Equal("vscode.light-plus", _svc.Current.Id);
        await _svc.SetCurrentAsync("does not exist", TestContext.Current.CancellationToken);
        Assert.Equal("vscode.light-plus", _svc.Current.Id);
    }

    [Fact]
    public void Changing_the_setting_switches_the_theme_live()
    {
        _host.Config.Set(SettingKeys.WorkbenchColorTheme, "Light+ (default light)");
        Assert.Equal("vscode.light-plus", _svc.Current.Id);
    }

    [Fact]
    public void Shell_registered_themes_are_selectable_and_a_persisted_selection_is_adopted_when_it_registers_late()
    {
        _host.Config.Set(SettingKeys.WorkbenchColorTheme, "Visual Studio Dark"); // not registered yet
        Assert.Equal("vscode.dark-plus", _svc.Current.Id);
        var vs = new ColorTheme("vs2022.dark", "Visual Studio Dark", ThemeKind.Dark, new Dictionary<string, Rgba> { ["editor.background"] = Rgba.Parse("#1E1E1E") }, []);
        var fired = 0;
        _svc.Changed.Subscribe(_ => fired++);
        var reg = _svc.Register(vs);
        Assert.Equal("vs2022.dark", _svc.Current.Id);
        Assert.Equal(1, fired);
        Assert.Contains(_svc.Available, t => t.Id == "vs2022.dark");
        reg.Dispose();                                   // removing the current theme falls back to a built-in
        Assert.Equal("vscode.dark-plus", _svc.Current.Id);
        Assert.DoesNotContain(_svc.Available, t => t.Id == "vs2022.dark");
    }

    [Fact]
    public async Task Re_registering_the_current_theme_by_id_replaces_it_and_notifies()
    {
        var a = new ColorTheme("my", "My", ThemeKind.Dark, new Dictionary<string, Rgba> { ["editor.background"] = Rgba.Parse("#111111") }, []);
        _svc.Register(a);
        await _svc.SetCurrentAsync("my", TestContext.Current.CancellationToken);
        var b = a with { Colors = new Dictionary<string, Rgba> { ["editor.background"] = Rgba.Parse("#222222") } };
        var fired = 0;
        _svc.Changed.Subscribe(_ => fired++);
        _svc.Register(b);
        Assert.Equal(1, fired);
        Assert.Equal(Rgba.Parse("#222222"), _svc.GetColor("editor.background"));
        Assert.Single(_svc.Available, t => t.Id == "my");
    }

    [Fact]
    public async Task User_theme_files_are_loaded_from_contributions_with_include_chains_and_unloaded_when_removed()
    {
        _host.Dir.WriteFile("themes/base.json", """{ "type": "dark", "colors": { "editor.background": "#0A0A0A", "editor.foreground": "#EEEEEE" } }""");
        var child = _host.Dir.WriteFile("themes/child.json", """{ "name": "Child", "include": "./base.json", "colors": { "editor.foreground": "#FFFFFF" }, "tokenColors": [ { "scope": "comment", "settings": { "foreground": "#777777" } } ] }""");
        var reg = _host.Services.Contributions.Contribute(ContributionSources.User, _host.Dir.Combine("themes"), new[]
        {
            new ColorThemeContribution("user.child", "My Child Theme", "vs-dark", child),
            new ColorThemeContribution("user.broken", "Broken", "vs-dark", _host.Dir.Combine("themes", "missing.json")),
        });
        await TestHost.WaitUntil(() => _svc.Available.Any(t => t.Id == "user.child"), "user theme load");
        var theme = _svc.Available.Single(t => t.Id == "user.child");
        Assert.Equal("My Child Theme", theme.Label);
        Assert.Equal(Rgba.Parse("#0A0A0A"), theme.Colors["editor.background"]);       // from the include
        Assert.Equal(Rgba.Parse("#FFFFFF"), theme.Colors["editor.foreground"]);       // overridden by the child
        Assert.DoesNotContain(_svc.Available, t => t.Id == "user.broken");             // a bad theme is skipped, never fatal
        reg.Dispose();
        Assert.DoesNotContain(_svc.Available, t => t.Id == "user.child");
    }
}
