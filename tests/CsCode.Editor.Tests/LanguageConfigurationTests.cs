using System.Text.Json;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Languages;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// B4 (themes-languages-treesitter brief): the language configurations CsCode ships for the languages VS Code has none for (the supplement
/// table: TOML, CMake, Kotlin, Scala, Zig, Protobuf, GraphQL, Haskell, Elixir, Erlang, OCaml, Vue, Svelte, Terraform, Nix, reStructuredText,
/// Assembly/GNU as, CSV, JSON Lines), exercised through the real LanguageService and the editing functions the view runs. The same behaviours
/// in the real control are in <see cref="LanguageEditingViewTests"/>.
/// </summary>
public sealed class LanguageConfigurationTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly LanguageService _languages;

    public LanguageConfigurationTests() => _languages = new LanguageService(_host.Services);

    public void Dispose()
    {
        _languages.Dispose();
        _host.Dispose();
    }

    private EditingContext Ctx(string lang, int tab = 4)
    {
        var ext = _languages.GetExtended(lang);
        return new EditingContext(ext.Base, tab, true, "\n", ext.EnterRules);
    }

    private static InMemoryTextDocument Doc(string text) => new(new Uri("file:///x"), "plaintext", text);

    private static string Apply(string text, EditPlan? plan)
    {
        Assert.NotNull(plan);
        var doc = Doc(text);
        Assert.True(doc.ApplyEdits(plan!.Edits));
        return doc.GetText();
    }

    // ---- every supplement language has a configuration of its own ----------------------------------------------------------------------

    /// <summary>(language, line comment, block comment open, block comment close) as each language's own documentation defines them.</summary>
    public static TheoryData<string, string?, string?, string?> CommentTokens => new()
    {
        { "toml", "#", null, null },
        { "cmake", "#", "#[[", "]]" },
        { "kotlin", "//", "/*", "*/" },
        { "scala", "//", "/*", "*/" },
        { "zig", "//", null, null },
        { "protobuf", "//", "/*", "*/" },
        { "graphql", "#", null, null },
        { "haskell", "--", "{-", "-}" },
        { "elixir", "#", null, null },
        { "erlang", "%", null, null },
        { "ocaml", null, "(*", "*)" },
        { "vue", null, "<!--", "-->" },
        { "svelte", null, "<!--", "-->" },
        { "terraform", "#", "/*", "*/" },
        { "terraform-vars", "#", "/*", "*/" },
        { "nix", "#", "/*", "*/" },
        { "restructuredtext", "..", null, null },
        { "assembly", ";", null, null },
        { "gas", "#", "/*", "*/" },
        { "csv", null, null, null },
        { "jsonl", null, null, null },
    };

    [Theory]
    [MemberData(nameof(CommentTokens))]
    public void Every_supplement_language_has_its_own_configuration_with_the_languages_comment_syntax(string lang, string? line, string? blockOpen, string? blockClose)
    {
        var info = Assert.Single(BuiltinLanguageSupport.Languages, l => l.Id == lang);
        Assert.Equal(LanguageConfigurationOrigin.CsCode, info.Configuration);
        var c = _languages.GetConfiguration(lang);
        Assert.Equal(line, c.LineComment);
        Assert.Equal(blockOpen is null ? null : (blockOpen, blockClose!), c.BlockComment);
        Assert.NotEmpty(c.Brackets);
        Assert.NotEmpty(c.AutoClosingPairs);
        Assert.NotEmpty(c.SurroundingPairs);
    }

    [Fact]
    public void Only_plain_text_and_languages_without_a_published_configuration_fall_back_to_the_defaults()
    {
        var none = BuiltinLanguageSupport.Languages.Where(l => l.Configuration == LanguageConfigurationOrigin.None).Select(l => l.Id).Order().ToArray();
        // plain text has nothing to configure; BibTeX, Julia Markdown and Log come from VS Code packages that ship no configuration for them;
        // VS Code's search-result language (the search editor) has none either
        Assert.Equal(["bibtex", "juliamarkdown", "log", "plaintext", "search-result"], none);
    }

    [Fact]
    public void Every_regex_in_cscodes_configurations_compiles_in_dotnet_so_none_is_silently_dropped()
    {
        var checkedFiles = 0;
        foreach (var lang in BuiltinLanguages.Catalog.Languages.Where(l => l.ConfigurationPath?.Contains(BuiltinLanguages.OwnConfigurationPrefix, StringComparison.Ordinal) == true))
        {
            var text = BuiltinLanguages.ReadText(lang.ConfigurationPath![BuiltinLanguages.BuiltinPathPrefix.Length..])!;
            var parsed = LanguageConfigurationParser.ParseExtended(text);
            using var raw = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var root = raw.RootElement;
            var c = parsed.Base;
            if (root.TryGetProperty("wordPattern", out _)) Assert.True(c.WordPattern is not null, lang.Id + " wordPattern");
            if (root.TryGetProperty("folding", out var f) && f.TryGetProperty("markers", out _))
                Assert.True(c.FoldingMarkersStart is not null && c.FoldingMarkersEnd is not null, lang.Id + " folding markers");
            if (root.TryGetProperty("indentationRules", out var ir))
            {
                if (ir.TryGetProperty("increaseIndentPattern", out _)) Assert.True(c.IncreaseIndentPattern is not null, lang.Id + " increaseIndentPattern");
                if (ir.TryGetProperty("decreaseIndentPattern", out _)) Assert.True(c.DecreaseIndentPattern is not null, lang.Id + " decreaseIndentPattern");
            }
            if (root.TryGetProperty("onEnterRules", out var rules)) Assert.Equal(rules.GetArrayLength(), parsed.EnterRules!.Count);
            checkedFiles++;
        }
        Assert.Equal(21, checkedFiles); // 20 files; terraform-vars shares Terraform's
    }

    // ---- detection of the ids the configurations (and the language servers) are keyed by ---------------------------------------------------

    [Theory]
    [InlineData("file:///p/build.zig.zon", "zig")]
    [InlineData("file:///p/main.tf", "terraform")]
    [InlineData("file:///p/prod.tfvars", "terraform-vars")]
    [InlineData("file:///p/start.s", "gas")]
    [InlineData("file:///p/START.S", "gas")]
    [InlineData("file:///p/boot.asm", "assembly")]
    [InlineData("file:///p/boot.nasm", "assembly")]
    [InlineData("file:///p/schema.graphqls", "graphql")]
    [InlineData("file:///p/rebar.config", "erlang")]
    [InlineData("file:///p/build.sbt", "scala")]
    [InlineData("file:///p/settings.gradle.kts", "kotlin")]
    [InlineData("file:///p/flake.nix", "nix")]
    [InlineData("file:///p/App.vue", "vue")]
    public void New_and_split_language_ids_are_detected(string uri, string expected) =>
        Assert.Equal(expected, _languages.DetectLanguageId(new Uri(uri)));

    // ---- toggle comment ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("toml", "a = 1", "# a = 1")]
    [InlineData("cmake", "project(x)", "# project(x)")]
    [InlineData("kotlin", "val a = 1", "// val a = 1")]
    [InlineData("zig", "const a = 1;", "// const a = 1;")]
    [InlineData("graphql", "type A", "# type A")]
    [InlineData("haskell", "main = pure ()", "-- main = pure ()")]
    [InlineData("elixir", "x = 1", "# x = 1")]
    [InlineData("erlang", "f() -> ok.", "% f() -> ok.")]
    [InlineData("ocaml", "let x = 1", "(* let x = 1 *)")]
    [InlineData("vue", "<div></div>", "<!-- <div></div> -->")]
    [InlineData("terraform", "count = 1", "# count = 1")]
    [InlineData("nix", "a = 1;", "# a = 1;")]
    [InlineData("restructuredtext", "Some text", ".. Some text")]
    [InlineData("assembly", "mov eax, 1", "; mov eax, 1")]
    [InlineData("gas", "movl $1, %eax", "# movl $1, %eax")]
    public void Toggle_line_comment_uses_the_languages_marker_and_round_trips(string lang, string line, string commented)
    {
        var text = Apply(line, EditingOperations.ToggleLineComment(Doc(line), TextRange.FromLines(0, 0, 0, 0), Ctx(lang)));
        Assert.Equal(commented, text);
        Assert.Equal(line, Apply(text, EditingOperations.ToggleLineComment(Doc(text), TextRange.FromLines(0, 0, 0, 0), Ctx(lang))));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("jsonl")]
    public void Formats_without_comments_leave_toggle_comment_a_no_op(string lang) =>
        Assert.Null(EditingOperations.ToggleLineComment(Doc("a,b"), TextRange.FromLines(0, 0, 0, 0), Ctx(lang)));

    [Theory]
    [InlineData("haskell", "x = 1", "{- x = 1 -}")]
    [InlineData("cmake", "set(A 1)", "#[[ set(A 1) ]]")]
    [InlineData("nix", "a = 1;", "/* a = 1; */")]
    public void Block_comment_uses_the_languages_block_tokens(string lang, string line, string expected) =>
        Assert.Equal(expected, Apply(line, EditingOperations.ToggleBlockComment(Doc(line), TextRange.FromLines(0, 0, 0, line.Length), Ctx(lang))));

    // ---- indentation on Enter ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("toml", "deps = [", "    ")]
    [InlineData("cmake", "if(WIN32)", "    ")]
    [InlineData("cmake", "  FOREACH(f IN LISTS files)", "      ")]   // CMake commands are case-insensitive
    [InlineData("cmake", "message(STATUS x)", "")]
    [InlineData("kotlin", "fun main() {", "    ")]
    [InlineData("scala", "def f(x: Int) =", "    ")]
    [InlineData("scala", "class A:", "    ")]
    [InlineData("zig", "pub fn main() void {", "    ")]
    [InlineData("protobuf", "message Person {", "    ")]
    [InlineData("graphql", "type Query {", "    ")]
    [InlineData("haskell", "main = do", "    ")]
    [InlineData("haskell", "  where", "      ")]
    [InlineData("elixir", "def f(x) do", "    ")]
    [InlineData("elixir", "  fn ->", "      ")]
    [InlineData("erlang", "f(X) ->", "    ")]
    [InlineData("ocaml", "let f x =", "    ")]
    [InlineData("ocaml", "module M = struct", "    ")]
    [InlineData("terraform", "resource \"aws_s3_bucket\" \"b\" {", "    ")]
    [InlineData("nix", "let", "    ")]
    [InlineData("nix", "  buildInputs = [", "      ")]
    [InlineData("svelte", "{#if user}", "    ")]
    [InlineData("svelte", "{#each items as item}", "    ")]
    public void Enter_indents_after_the_languages_block_openers(string lang, string before, string indent) =>
        Assert.Equal(indent, AutoIndent.ForEnter(before, "", Ctx(lang)).NewLineIndent);

    [Fact]
    public void Erlang_starts_the_next_form_in_column_zero_after_a_full_stop()
    {
        Assert.Equal("", AutoIndent.ForEnter("    ok.", "", Ctx("erlang")).NewLineIndent);
        Assert.Equal("    ", AutoIndent.ForEnter("    X = 1,", "", Ctx("erlang")).NewLineIndent);   // inside a clause: keep the indent
        Assert.Equal("    ", AutoIndent.ForEnter("    % done.", "", Ctx("erlang")).NewLineIndent); // a full stop in a comment is not the end of a form
    }

    [Fact]
    public void Doc_comments_continue_on_Enter()
    {
        var kdoc = AutoIndent.ForEnter("    /** Adds", "", Ctx("kotlin"));
        Assert.Equal((" * ", "    "), (kdoc.AppendText, kdoc.NewLineIndent));
        var between = AutoIndent.ForEnter("/** ", " */", Ctx("scala"));
        Assert.NotNull(between.ClosingLineIndent);
        var zigDoc = AutoIndent.ForEnter("    /// Adds two numbers.", "", Ctx("zig"));
        Assert.Equal(("/// ", "    "), (zigDoc.AppendText, zigDoc.NewLineIndent));
        Assert.Equal("//! ", AutoIndent.ForEnter("//! Module docs", "", Ctx("zig")).AppendText);
        Assert.Equal("", AutoIndent.ForEnter("    // plain comment", "", Ctx("zig")).AppendText);
    }

    [Fact]
    public void Enter_between_html_tags_of_a_component_opens_an_indented_line()
    {
        foreach (var lang in new[] { "vue", "svelte" })
        {
            var r = AutoIndent.ForEnter("<template>", "</template>", Ctx(lang));
            Assert.Equal(("    ", ""), (r.NewLineIndent, r.ClosingLineIndent));
        }
    }

    [Theory]
    [InlineData("elixir", "def f do\n    x\n    end", 2, 7, "end")]
    [InlineData("erlang", "f() ->\n    case X of\n        a -> b\n        end", 3, 11, "end")]
    [InlineData("ocaml", "let f () = begin\n    g ();\n    end", 2, 7, "end")]
    [InlineData("cmake", "if(A)\n    message(x)\n    endif(", 2, 10, "endif(")]
    [InlineData("svelte", "{#if a}\n    <p/>\n    {/", 2, 6, "{/")]
    [InlineData("svelte", "{#if a}\n    <p/>\n    {:", 2, 6, "{:")]
    public void Typing_a_block_closer_pulls_its_line_back(string lang, string text, int line, int col, string closer)
    {
        var doc = Doc(text);
        var plan = AutoIndent.AfterTyping(doc, new TextPosition(line, col), closer[^1..], Ctx(lang));
        var result = Apply(text, plan);
        Assert.EndsWith("\n" + new string(' ', ExpectedIndent(text, line)) + closer, result);
    }

    // the closer goes one level shallower than the line it was typed on
    private static int ExpectedIndent(string text, int line) => Math.Max(0, text.Split('\n')[line].TakeWhile(c => c == ' ').Count() - 4);

    [Fact]
    public void Nix_does_not_dedent_while_typing_inherit()
    {
        // `in` closes a let block, but it is also how `inherit` starts: only brackets pull a Nix line back
        Assert.Null(AutoIndent.AfterTyping(Doc("{\n    in"), new TextPosition(1, 6), "n", Ctx("nix")));
        Assert.NotNull(AutoIndent.AfterTyping(Doc("{\n    x = 1;\n    }"), new TextPosition(2, 5), "}", Ctx("nix")));
    }

    // ---- auto-closing ----------------------------------------------------------------------------------------------------------------

    private AutoCloseDecision Decide(string lang, string typed, string before, string after = "", TokenKind kind = TokenKind.Other)
    {
        var ext = _languages.GetExtended(lang);
        return AutoClosing.Decide(typed, before, after, "", ext.Base, ext.EffectiveAutoCloseBefore, kind, false);
    }

    [Theory]
    [InlineData("toml", "\"", "name = ", "\"")]
    [InlineData("toml", "'", "path = ", "'")]                  // literal string
    [InlineData("toml", "[", "", "]")]
    [InlineData("kotlin", "*", "/*", " */")]                   // KDoc
    [InlineData("scala", "*", "/*", " */")]                    // Scaladoc
    [InlineData("graphql", "\"", "  ", "\"")]
    [InlineData("elixir", "\"", "IO.puts ", "\"")]
    [InlineData("zig", "(", "foo", ")")]
    [InlineData("csv", "\"", "a,", "\"")]
    [InlineData("jsonl", "{", "", "}")]
    [InlineData("vue", "-", "<!-", "-->")]
    public void Auto_closing_pairs_follow_the_languages_string_and_comment_syntax(string lang, string typed, string before, string close)
    {
        var d = Decide(lang, typed, before);
        Assert.Equal(AutoCloseKind.InsertPair, d.Kind);
        Assert.Equal(close, d.Close);
    }

    [Theory]
    [InlineData("haskell", "'", "f ")]   // primes and character literals: never auto-closed
    [InlineData("ocaml", "'", "type ")]  // 'a type variables
    [InlineData("nix", "'", "x = ")]     // a lone ' is not a Nix string
    [InlineData("nix", "'", "src = '")]  // nor is the second ' of an indented string: ''…'' is typed in full (no multi-character closers)
    [InlineData("toml", "\"", "s = \"abc ")]
    public void Quotes_that_are_not_string_openers_in_the_language_are_left_alone(string lang, string typed, string before) =>
        Assert.Equal(AutoCloseKind.None, Decide(lang, typed, before, kind: before.Count(c => c == '"') % 2 == 1 ? TokenKind.String : TokenKind.Other).Kind);

    [Fact]
    public void No_own_configuration_auto_closes_a_multi_character_quote_delimiter()
    {
        // The editor steps over only the single-character closers it inserted: an auto-closed """ (or ''…'') would be doubled when the user
        // types the closing delimiter, so """…""" strings and Nix indented strings are typed in full (LanguageEditingViewTests types them).
        var own = BuiltinLanguageSupport.Languages.Where(l => l.Configuration == LanguageConfigurationOrigin.CsCode).ToArray();
        Assert.True(own.Length >= 20, $"only {own.Length} languages with CsCode's own configuration");
        foreach (var l in own)
            Assert.All(_languages.GetConfiguration(l.Id).AutoClosingPairs, p =>
                Assert.False(p.Open.Length > 1 && p.Open.All(c => c is '"' or '\'' or '`'), $"{l.Id}: auto-closes {p.Open}…{p.Close}"));
    }

    // ---- folding markers ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kotlin", "// region Helpers", "// endregion")]
    [InlineData("kotlin", "//<editor-fold desc=\"x\">", "//</editor-fold>")]
    [InlineData("toml", "# region deps", "# endregion")]
    [InlineData("haskell", "-- #region", "-- #endregion")]
    [InlineData("erlang", "%% region exports", "%% endregion")]
    [InlineData("vue", "<!-- #region -->", "<!-- #endregion -->")]
    [InlineData("assembly", "; region boot", "; endregion")]
    public void Folding_region_markers_match(string lang, string start, string end)
    {
        var c = _languages.GetConfiguration(lang);
        Assert.Matches(c.FoldingMarkersStart!, start);
        Assert.Matches(c.FoldingMarkersEnd!, end);
        Assert.DoesNotMatch(c.FoldingMarkersStart!, end);
    }

    [Theory]
    [InlineData("haskell")]
    [InlineData("restructuredtext")]
    public void Layout_languages_fold_by_indentation_off_side(string lang) => Assert.True(_languages.GetConfiguration(lang).OffsideFolding);

    // ---- highlighting claims of the support matrix --------------------------------------------------------------------------------------

    [Fact]
    public void Every_language_listed_with_a_TextMate_grammar_loads_it_and_tokenises_a_line()
    {
        var grammars = new GrammarService(_host.Services);
        var highlighted = BuiltinLanguageSupport.Languages.Where(l => l.GrammarScope is not null).ToArray();
        Assert.True(highlighted.Length >= 50, $"only {highlighted.Length} highlighted languages");
        foreach (var l in highlighted)
        {
            var grammar = grammars.GrammarForLanguage(l.Id);
            Assert.True(grammar is not null, $"{l.Id}: grammar {l.GrammarScope} does not load");
            var result = grammar!.TokenizeLine("x = 1 // y", null, TimeSpan.FromSeconds(5));
            Assert.NotEmpty(result.Tokens);
        }
        Assert.All(BuiltinLanguageSupport.Languages.Where(l => l.GrammarScope is null), l => Assert.Null(grammars.GrammarForLanguage(l.Id)));
    }
}
