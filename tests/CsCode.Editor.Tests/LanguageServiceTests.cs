using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Editor.Languages;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 5: language detection and language-configuration parsing, on the REAL VS Code built-in files TextMateSharp ships.</summary>
public class LanguageServiceTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly LanguageService _svc;

    public LanguageServiceTests() => _svc = new LanguageService(_host.Services);

    public void Dispose()
    {
        _svc.Dispose();
        _host.Dispose();
    }

    private string Detect(string path, string? first = null) => _svc.DetectLanguageId(new Uri(path), first);

    // ---- detection ----

    [Theory]
    [InlineData("file:///p/Program.cs", "csharp")]
    [InlineData("file:///p/app.ts", "typescript")]
    [InlineData("file:///p/app.tsx", "typescriptreact")]
    [InlineData("file:///p/app.js", "javascript")]
    [InlineData("file:///p/app.mjs", "javascript")]
    [InlineData("file:///p/app.jsx", "javascriptreact")]
    [InlineData("file:///p/main.py", "python")]
    [InlineData("file:///p/main.rs", "rust")]
    [InlineData("file:///p/main.go", "go")]
    [InlineData("file:///p/main.c", "c")]
    [InlineData("file:///p/main.cpp", "cpp")]
    [InlineData("file:///p/main.h", "cpp")]
    [InlineData("file:///p/Main.java", "java")]
    [InlineData("file:///p/a.json", "json")]
    [InlineData("file:///p/a.jsonc", "jsonc")]
    [InlineData("file:///p/README.md", "markdown")]
    [InlineData("file:///p/a.html", "html")]
    [InlineData("file:///p/a.css", "css")]
    [InlineData("file:///p/a.scss", "scss")]
    [InlineData("file:///p/a.xml", "xml")]
    [InlineData("file:///p/App.csproj", "xml")]
    [InlineData("file:///p/a.yml", "yaml")]
    [InlineData("file:///p/a.yaml", "yaml")]
    [InlineData("file:///p/a.sh", "shellscript")]
    [InlineData("file:///p/a.sql", "sql")]
    [InlineData("file:///p/a.rb", "ruby")]
    [InlineData("file:///p/a.php", "php")]
    [InlineData("file:///p/a.lua", "lua")]
    [InlineData("file:///p/a.swift", "swift")]
    [InlineData("file:///p/a.kt", "kotlin")]
    [InlineData("file:///p/a.toml", "toml")]
    [InlineData("file:///p/a.txt", "plaintext")]
    [InlineData("file:///p/unknown.zzz", "plaintext")]
    [InlineData("file:///p/noextension", "plaintext")]
    public void Detects_by_extension(string uri, string expected) => Assert.Equal(expected, Detect(uri));

    [Theory]
    [InlineData("file:///p/Dockerfile", "dockerfile")]
    [InlineData("file:///p/Dockerfile.dev", "dockerfile")]     // filename pattern
    [InlineData("file:///p/Makefile", "makefile")]
    [InlineData("file:///p/GNUmakefile", "makefile")]
    [InlineData("file:///p/tsconfig.json", "jsonc")]            // filename beats the .json extension
    [InlineData("file:///p/tsconfig.build.json", "jsonc")]      // filename pattern beats the .json extension
    [InlineData("file:///p/CMakeLists.txt", "cmake")]           // filename beats the .txt extension
    [InlineData("file:///p/.gitignore", "ignore")]              // dotfile matches as a whole
    [InlineData("file:///p/.bashrc", "shellscript")]
    [InlineData("file:///p/Gemfile", "ruby")]
    [InlineData("file:///p/compose.yaml", "dockercompose")]
    public void Detects_by_filename_and_filename_pattern(string uri, string expected) => Assert.Equal(expected, Detect(uri));

    [Fact]
    public void Longest_extension_wins()
    {
        using var host = TestHost.Create(new ThreadAffinityUiThread());
        host.Services.Contributions.Contribute(ContributionSources.User, "/u", new[]
        {
            new LanguageContribution("typescript-declaration", ["TS Declaration"], [".d.ts"], [], []),
        });
        using var svc = new LanguageService(host.Services);
        Assert.Equal("typescript-declaration", svc.DetectLanguageId(new Uri("file:///p/lib.d.ts")));
        Assert.Equal("typescript", svc.DetectLanguageId(new Uri("file:///p/lib.ts")));
    }

    [Theory]
    [InlineData("#!/usr/bin/env python3", "python")]
    [InlineData("#!/bin/bash", "shellscript")]
    [InlineData("#!/usr/bin/env node", "javascript")]
    [InlineData("<?xml version=\"1.0\"?>", "xml")]
    [InlineData("#!/usr/bin/ruby", "ruby")]
    [InlineData("just text", "plaintext")]
    public void Detects_by_first_line_when_there_is_no_extension(string first, string expected) =>
        Assert.Equal(expected, Detect("file:///p/script", first));

    [Fact]
    public void Extension_beats_first_line()
    {
        Assert.Equal("csharp", Detect("file:///p/a.cs", "#!/usr/bin/env python3"));
    }

    [Fact]
    public void Untitled_documents_have_no_extension_and_stay_plaintext()
    {
        Assert.Equal("plaintext", _svc.DetectLanguageId(new Uri("untitled:Untitled-1")));
        Assert.Equal("python", _svc.DetectLanguageId(new Uri("untitled:Untitled-1"), "#!/usr/bin/python"));
    }

    [Fact]
    public void Detection_is_case_insensitive_for_extensions_and_filenames()
    {
        Assert.Equal("csharp", Detect("file:///p/PROGRAM.CS"));
        Assert.Equal("makefile", Detect("file:///p/makefile"));
    }

    [Fact]
    public void Files_associations_setting_overrides_everything()
    {
        _host.Config.Set("files.associations", new Dictionary<string, string> { ["*.cs"] = "plaintext", ["**/special/*.json"] = "jsonc" });
        Assert.Equal("plaintext", Detect("file:///p/a.cs"));
        Assert.Equal("jsonc", Detect("file:///p/special/x.json"));
        Assert.Equal("json", Detect("file:///p/other/x.json"));
    }

    [Fact]
    public void Contributions_extend_and_override_builtins_live_and_fire_events()
    {
        var changed = 0;
        _svc.LanguagesChanged.Subscribe(_ => changed++);
        Assert.Equal("plaintext", Detect("file:///p/a.foo"));
        var reg = _host.Services.Contributions.Contribute(ContributionSources.User, "/u", new[]
        {
            new LanguageContribution("foolang", ["Foo"], [".foo"], [], []),
            new LanguageContribution("csharp", [], [".cs2"], [], []),   // merges into the built-in csharp
        });
        Assert.Equal("foolang", Detect("file:///p/a.foo"));
        Assert.Equal("csharp", Detect("file:///p/a.cs2"));
        Assert.Equal("csharp", Detect("file:///p/a.cs"));
        Assert.Contains(_svc.Languages, l => l.Id == "foolang" && l.DisplayName == "Foo");
        Assert.True(changed >= 1);
        reg.Dispose();
        Assert.Equal("plaintext", Detect("file:///p/a.foo"));
    }

    [Fact]
    public void A_user_contribution_overrides_a_builtin_extension()
    {
        _host.Services.Contributions.Contribute(ContributionSources.User, "/u", new[] { new LanguageContribution("myjs", ["My JS"], [".js"], [], []) });
        Assert.Equal("myjs", Detect("file:///p/a.js"));
    }

    [Fact]
    public void An_invalid_first_line_regex_is_ignored_not_fatal()
    {
        _host.Services.Contributions.Contribute(ContributionSources.User, "/u", new[] { new LanguageContribution("bad", ["Bad"], [".bad"], [], [], FirstLine: "([unclosed") });
        Assert.Equal("plaintext", Detect("file:///p/x", "anything"));
        Assert.Equal("bad", Detect("file:///p/x.bad"));
    }

    [Fact]
    public void Language_list_has_at_least_forty_named_languages_with_display_names()
    {
        Assert.True(_svc.Languages.Count >= 40, $"only {_svc.Languages.Count} languages");
        Assert.Equal("C#", _svc.Get("csharp")!.DisplayName);
        Assert.Equal("Plain Text", _svc.Get("plaintext")!.DisplayName);
        Assert.Null(_svc.Get("no-such-language"));
        Assert.Equal(_svc.Languages.Select(l => l.DisplayName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), _svc.Languages.Select(l => l.DisplayName));
    }

    // ---- configuration on the real VS Code files ----

    [Fact]
    public void Csharp_configuration_from_the_real_vscode_file()
    {
        var c = _svc.GetConfiguration("csharp");
        Assert.Equal("//", c.LineComment);
        Assert.Equal(("/*", "*/"), c.BlockComment);
        Assert.Equal(3, c.Brackets.Count);
        Assert.Contains(c.AutoClosingPairs, p => p.Open == "\"" && p.Close == "\"" && p.NotIn!.SequenceEqual(new[] { "string", "comment" }));
        Assert.Contains(c.SurroundingPairs, p => p == ("<", ">"));
        Assert.Equal(@"^\s*#region\b", c.FoldingMarkersStart);
        Assert.Equal(@"^\s*#endregion\b", c.FoldingMarkersEnd);
    }

    [Fact]
    public void Python_configuration_has_offside_folding_hash_comments_and_onEnter_rules()
    {
        var c = _svc.GetConfiguration("python");
        Assert.Equal("#", c.LineComment);
        Assert.Equal(("\"\"\"", "\"\"\""), c.BlockComment);
        Assert.True(c.OffsideFolding);
        Assert.Single(c.OnEnterRules);
        Assert.Equal("indent", c.OnEnterRules[0].Action);
        Assert.Contains(c.AutoClosingPairs, p => p.Open == "f\"" && p.NotIn!.Contains("comment"));
    }

    [Fact]
    public void Typescript_configuration_has_autoCloseBefore_and_the_doc_comment_pair()
    {
        var ext = _svc.GetExtended("typescript");
        Assert.Equal(";:.,=}])>` \n\t", ext.AutoCloseBefore);
        Assert.Contains(ext.Base.AutoClosingPairs, p => p.Open == "/**" && p.Close == " */");
        Assert.Equal("//", ext.Base.LineComment);
    }

    [Fact]
    public void Languages_without_a_configuration_get_sane_default_pairs()
    {
        var c = _svc.GetConfiguration("log"); // VS Code's Log package ships a grammar but no configuration
        Assert.Contains(c.AutoClosingPairs, p => p.Open == "(");
        Assert.NotEmpty(c.Brackets);
        Assert.Null(c.LineComment);
    }

    [Fact]
    public void A_user_language_configuration_file_is_loaded_off_thread_and_announced()
    {
        _host.Dir.WriteFile("lang.json", "{ \"comments\": { \"lineComment\": \";;\" }, }");
        _host.Services.Contributions.Contribute(ContributionSources.User, _host.Dir.Path, new[]
        {
            new LanguageContribution("lisp", ["Lisp"], [".lisp"], [], [], ConfigurationPath: _host.Dir.Combine("lang.json")),
        });
        var changed = new List<string>();
        _svc.LanguageConfigurationChanged.Subscribe(changed.Add);
        Assert.Null(_svc.GetConfiguration("lisp").LineComment); // not loaded yet: defaults, and the call did not block on I/O
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!changed.Contains("lisp") && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        Assert.Contains("lisp", changed);
        Assert.Equal(";;", _svc.GetConfiguration("lisp").LineComment);
    }

    // ---- parser ----

    [Fact]
    public void Parser_accepts_jsonc_comments_and_trailing_commas()
    {
        var c = LanguageConfigurationParser.Parse("""
            {
              // line comment
              "comments": { "lineComment": "--", "blockComment": ["{-", "-}"], },
              /* block */ "brackets": [ ["(", ")"], ],
            }
            """);
        Assert.Equal("--", c.LineComment);
        Assert.Equal(("{-", "-}"), c.BlockComment);
        Assert.Single(c.Brackets);
    }

    [Fact]
    public void Parser_reads_wordPattern_as_string_or_pattern_with_flags()
    {
        Assert.Equal(@"[a-z]+", LanguageConfigurationParser.Parse("""{ "wordPattern": "[a-z]+" }""").WordPattern);
        Assert.Equal(@"(?i)[a-z]+", LanguageConfigurationParser.Parse("""{ "wordPattern": { "pattern": "[a-z]+", "flags": "gi" } }""").WordPattern);
    }

    [Fact]
    public void Parser_reads_surrounding_pairs_in_both_array_and_object_form()
    {
        var c = LanguageConfigurationParser.Parse("""{ "surroundingPairs": [ ["(", ")"], { "open": "<", "close": ">" } ] }""");
        Assert.Equal([("(", ")"), ("<", ">")], c.SurroundingPairs);
    }

    [Fact]
    public void Parser_reads_indentation_rules_and_onEnter_rules_including_after_text_and_remove_text()
    {
        var c = LanguageConfigurationParser.Parse("""
            {
              "indentationRules": {
                "increaseIndentPattern": "^.*\\{[^}]*$",
                "decreaseIndentPattern": { "pattern": "^\\s*\\}", "flags": "i" },
                "indentNextLinePattern": "^\\s*if\\b.*$",
                "unIndentedLinePattern": "^#"
              },
              "onEnterRules": [
                { "beforeText": "^\\s*/\\*\\*(?!/)([^\\*]|\\*(?!/))*$", "afterText": "^\\s*\\*/$", "action": { "indent": "indentOutdent", "appendText": " * " } },
                { "beforeText": "x", "action": { "indent": "outdent", "removeText": 2 } },
                { "beforeText": "y", "previousLineText": "z", "action": { "indent": "none" } },
                { "beforeText": "([unclosed", "action": { "indent": "none" } },
                { "beforeText": "w", "action": { "indent": "bogus" } }
              ]
            }
            """);
        Assert.Equal(@"^.*\{[^}]*$", c.IncreaseIndentPattern);
        Assert.Equal(@"(?i)^\s*\}", c.DecreaseIndentPattern);
        Assert.Equal(@"^\s*if\b.*$", c.IndentNextLinePattern);
        Assert.Equal("^#", c.UnIndentedLinePattern);
        Assert.Equal(2, c.OnEnterRules.Count); // previousLineText rule, invalid regex rule and unknown action are dropped
        Assert.Equal("indentOutdent", c.OnEnterRules[0].Action);
        Assert.Equal(" * ", c.OnEnterRules[0].AppendText);
        Assert.NotNull(c.OnEnterRules[0].AfterText);
        Assert.Equal(2, c.OnEnterRules[1].RemoveText);
    }

    [Fact]
    public void Parser_drops_regexes_that_dotnet_cannot_compile_and_tolerates_garbage_shapes()
    {
        var c = LanguageConfigurationParser.Parse("""
            { "wordPattern": "([unclosed", "folding": { "markers": { "start": "(", "end": "^ok$" } },
              "brackets": [ ["only-one"], 5, ["a","b"] ], "comments": 7, "autoClosingPairs": [ { "open": "x" }, ["a","b"] ] }
            """);
        Assert.Null(c.WordPattern);
        Assert.Null(c.FoldingMarkersStart);
        Assert.Equal("^ok$", c.FoldingMarkersEnd);
        Assert.Equal([("a", "b")], c.Brackets);
        Assert.Single(c.AutoClosingPairs);
        Assert.Null(c.LineComment);
    }

    [Fact]
    public void Parser_rejects_invalid_json_via_TryParse_and_returns_an_empty_configuration()
    {
        Assert.False(LanguageConfigurationParser.TryParse("{ nope", out var ext));
        Assert.Empty(ext.Base.Brackets);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => LanguageConfigurationParser.Parse("{ nope"));
    }

    [Fact]
    public void Every_embedded_builtin_language_configuration_parses()
    {
        var checkedCount = 0;
        foreach (var lang in BuiltinLanguages.Catalog.Languages.Where(l => l.ConfigurationPath is not null))
        {
            var text = BuiltinLanguages.ReadText(lang.ConfigurationPath![BuiltinLanguages.BuiltinPathPrefix.Length..]);
            Assert.NotNull(text);
            Assert.True(LanguageConfigurationParser.TryParse(text!, out _), $"{lang.Id} failed to parse");
            checkedCount++;
        }
        Assert.True(checkedCount >= 40, $"only {checkedCount} configurations checked");
    }
}
