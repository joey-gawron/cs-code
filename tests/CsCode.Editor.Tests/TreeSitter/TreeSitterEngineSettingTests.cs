using Avalonia.Headless.XUnit;
using CsCode.Core.Contributions;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TreeSitter;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterViewTests;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// <c>editor.syntaxEngine</c> (docs/TREE-SITTER.md, "The setting"): <c>auto</c> (the default) gives tree-sitter exactly the languages TextMate
/// cannot colour and leaves every TextMate language (C# included) on TextMate; <c>textmate</c> and <c>treesitter</c> force an engine; each works
/// per language; the prototype's <c>editor.experimental.treeSitter</c> is still read (true = treesitter, false = auto).
/// </summary>
public class TreeSitterEngineSettingTests
{
    private const string Toml = "# a comment\n[package]\nname = \"demo\"\nversion = 1\n";

    [AvaloniaFact]
    public async Task By_default_tree_sitter_colours_the_languages_TextMate_cannot_and_CSharp_stays_on_TextMate()
    {
        RequireGrammar("toml");
        RequireGrammar("kotlin");
        using var host = ViewHost.Create();
        var (_, toml, _) = await host.OpenAsync(Toml, "Cargo.toml");
        var (_, kotlin, _) = await host.OpenAsync("fun main() {\n    val s = \"x\" // c\n}\n", "Main.kt");
        var (_, cs, _) = await host.OpenAsync(CSharp, "Program.cs");
        await WaitForTreeSitter(toml, 3);
        await WaitForTreeSitter(kotlin, 2);
        await ViewHost.WaitFor(() => cs.Tokens is { } t && t.Store.ValidLines >= 15, "C# TextMate tokens");

        Assert.Equal("tree-sitter", toml.SyntaxEngine);
        Assert.Equal("tree-sitter", kotlin.SyntaxEngine);
        Assert.Equal("TextMate", cs.SyntaxEngine);
        Assert.Null(cs.Tokens!.TreeSitter);
        Assert.True(Has(ScopesAt(toml, 0, 2), "comment"), string.Join(" ", ScopesAt(toml, 0, 2)));
        Assert.True(Has(ScopesAt(toml, 2, 8), "string"), string.Join(" ", ScopesAt(toml, 2, 8)));
        Assert.Equal("source.toml", ScopesAt(toml, 2, 8)[0]);
        Assert.True(Has(ScopesAt(kotlin, 0, 0), "keyword") || Has(ScopesAt(kotlin, 0, 0), "storage"), string.Join(" ", ScopesAt(kotlin, 0, 0)));
        // the registered setting is the three-way choice the Settings panel shows
        var property = Assert.Single(host.Config.Properties, p => p.Key == TreeSitterService.SettingKey);
        Assert.Equal(TreeSitterService.Auto, property.Default!.Value.GetString());
        Assert.Equal(3, property.Schema!.Value.GetProperty("enum").GetArrayLength());
    }

    [AvaloniaFact]
    public async Task Textmate_turns_tree_sitter_off_everywhere_and_treesitter_turns_it_on_for_TextMate_languages()
    {
        RequireGrammar("toml");
        RequireGrammar("c_sharp");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TextMate);
        var (_, toml, _) = await host.OpenAsync(Toml, "a.toml");
        var (_, cs, _) = await host.OpenAsync(CSharp, "Program.cs");
        await ViewHost.WaitFor(() => cs.Tokens is { } t && t.Store.ValidLines >= 15, "C# TextMate tokens");
        ViewHost.Pump();
        Assert.Null(toml.Tokens); // TextMate has no TOML grammar: plain text, as the user chose
        Assert.Null(toml.SyntaxEngine);

        // the open TOML view starts colouring as soon as a grammar is available for it (no reopen)
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        await ViewHost.WaitFor(() => toml.Tokens is not null, "a session for the plain view");
        await WaitForTreeSitter(toml, 3);
        await WaitForTreeSitter(cs);
        Assert.Equal("tree-sitter", cs.SyntaxEngine);
    }

    [AvaloniaFact]
    public async Task A_language_section_overrides_the_global_choice_both_ways()
    {
        RequireGrammar("toml");
        RequireGrammar("rust");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        host.Config.Set("[toml]", new Dictionary<string, object> { [TreeSitterService.SettingKey] = TreeSitterService.TextMate });
        var (_, toml, _) = await host.OpenAsync(Toml, "a.toml");
        var (_, rs, _) = await host.OpenAsync("fn main() {}\n", "a.rs");
        await ViewHost.WaitFor(() => rs.Tokens?.TreeSitter is { CompletedParses: > 0 }, "rust on tree-sitter (global)");
        Assert.Null(toml.Tokens); // its section says textmate, and TextMate has no TOML grammar

        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.Auto);
        host.Config.Set("[rust]", new Dictionary<string, object> { [TreeSitterService.SettingKey] = TreeSitterService.TreeSitter });
        await ViewHost.WaitFor(() => rs.Tokens?.TreeSitter is { CompletedParses: > 0 }, "rust still on tree-sitter (its section)");
        ViewHost.Pump();
        Assert.Null(toml.Tokens); // [toml] still says textmate
        host.Config.Set("[toml]", new Dictionary<string, object>());
        await WaitForTreeSitter(toml, 3); // auto again
    }

    [AvaloniaFact]
    public void The_old_experimental_setting_is_read_as_an_alias()
    {
        using var host = ViewHost.Create();
        var service = host.Factory.Tokens.TreeSitter;
        Assert.Equal(TreeSitterService.Auto, service.SettingFor("rust"));

        host.Config.Set(TreeSitterService.LegacySettingKey, true);
        Assert.Equal(TreeSitterService.TreeSitter, service.SettingFor("rust"));
        Assert.True(service.IsEnabledFor("rust"));
        host.Config.Set(TreeSitterService.LegacySettingKey, false);
        Assert.Equal(TreeSitterService.Auto, service.SettingFor("rust")); // false never meant "leave TOML uncoloured"
        Assert.True(service.IsEnabledFor("toml"));
        Assert.False(service.IsEnabledFor("rust"));

        // per language, and the new key wins over the old one at the same level and over the global value
        host.Config.Set("[rust]", new Dictionary<string, object> { [TreeSitterService.LegacySettingKey] = true });
        Assert.Equal(TreeSitterService.TreeSitter, service.SettingFor("rust"));
        host.Config.Set("[rust]", new Dictionary<string, object> { [TreeSitterService.LegacySettingKey] = true, [TreeSitterService.SettingKey] = TreeSitterService.TextMate });
        Assert.Equal(TreeSitterService.TextMate, service.SettingFor("rust"));
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TextMate);
        host.Config.Set(TreeSitterService.LegacySettingKey, true);
        Assert.Equal(TreeSitterService.TextMate, service.SettingFor("go")); // the alias only speaks while the new setting is at its default
        // an unknown value is ignored (the level below decides)
        host.Config.Set(TreeSitterService.SettingKey, "fancy");
        Assert.Equal(TreeSitterService.TreeSitter, service.SettingFor("go"));
        host.Config.Set(TreeSitterService.SettingKey, "Tree-Sitter");
        Assert.Equal(TreeSitterService.TreeSitter, service.SettingFor("go"));
    }

    [AvaloniaFact]
    public async Task The_old_setting_switches_an_open_editor_live_through_the_alias()
    {
        RequireGrammar("rust");
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("fn main() {}\n", "main.rs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 1, "TextMate tokens");
        host.Config.Set(TreeSitterService.LegacySettingKey, true);
        await ViewHost.WaitFor(() => view.Tokens?.TreeSitter is { CompletedParses: > 0 }, "tree-sitter through the alias");
        host.Config.Set(TreeSitterService.LegacySettingKey, false);
        await ViewHost.WaitFor(() => view.Tokens is { TreeSitter: null } t && t.Store.ValidLines >= 1, "back to TextMate");
    }

    /// <summary><c>auto</c> asks "does TextMate have a grammar?": a user's TextMate grammar for TOML takes TOML back from tree-sitter.</summary>
    [AvaloniaFact]
    public async Task A_user_TextMate_grammar_takes_its_language_back_from_auto()
    {
        RequireGrammar("toml");
        using var host = ViewHost.Create();
        var (_, toml, _) = await host.OpenAsync(Toml, "a.toml");
        await WaitForTreeSitter(toml, 3);

        var grammar = host.Dir.WriteFile("toml.tmLanguage.json",
            """{ "scopeName": "source.toml", "patterns": [ { "match": "#.*$", "name": "comment.line.number-sign.toml" } ] }""");
        using var contribution = host.Factory.Services.Contributions.Contribute("user", host.Dir.Path, [new GrammarContribution("toml", "source.toml", grammar)]);
        await ViewHost.WaitFor(() => toml.Tokens is { TreeSitter: null } t && t.Store.ValidLines >= 3, "TextMate (the user's grammar) takes over");
        Assert.True(Has(ScopesAt(toml, 0, 2), "comment.line.number-sign"), string.Join(" ", ScopesAt(toml, 0, 2)));

        contribution.Dispose();
        await ViewHost.WaitFor(() => toml.Tokens?.TreeSitter is { CompletedParses: > 0 }, "back to tree-sitter when it is gone");
    }

    [AvaloniaFact]
    public async Task OCaml_interfaces_get_the_interface_grammar()
    {
        RequireGrammar("ocaml");
        RequireGrammar("ocaml_interface");
        using var host = ViewHost.Create();
        var (_, ml, _) = await host.OpenAsync("let x = 1\n", "a.ml");
        var (_, mli, _) = await host.OpenAsync("val x : int\n", "a.mli");
        await WaitForTreeSitter(ml, 0);
        await WaitForTreeSitter(mli, 0);
        Assert.Equal("ocaml", ml.Tokens!.TreeSitter!.Syntax.Language.Name);
        Assert.Equal("ocaml_interface", mli.Tokens!.TreeSitter!.Syntax.Language.Name);
        Assert.False(mli.Tokens.TreeSitter.Syntax.RootHasError, mli.Tokens.TreeSitter.Syntax.TreeString());
    }
}
