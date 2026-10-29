using System.Diagnostics;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.TreeSitter.Queries;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// The tree-sitter engine against the REAL runtime and grammars of this machine: loading and ABI checks, query composition (Neovim's
/// <c>inherits</c>), predicates, capture → scope mapping, injections, incremental and sliced parsing, cancellation, native memory.
/// </summary>
public class TreeSitterEngineTests
{
    public static TheoryData<string> PrototypeGrammars => ["c_sharp", "typescript", "tsx", "javascript", "rust", "python", "markdown", "markdown_inline", "cpp", "c", "html", "css"];

    [Fact]
    public void The_runtime_loads_and_exports_the_0_25_api()
    {
        RequireRuntime();
        Assert.True(TsRuntime.IsAvailable);
        Assert.Contains("tree-sitter", TsRuntime.LoadedPath!, StringComparison.Ordinal);
        Assert.Null(TsRuntime.FailureReason);
    }

    [Theory]
    [MemberData(nameof(PrototypeGrammars))]
    public void Every_prototype_grammar_loads_within_the_runtime_ABI_and_its_queries_compile(string grammar)
    {
        var bundle = RequireGrammar(grammar);
        Assert.InRange(bundle.Language.AbiVersion, TsRuntime.MinAbi, TsRuntime.MaxAbi);
        Assert.NotNull(bundle.Highlights);
        Assert.True(bundle.Highlights!.PatternCount > 0);
        Assert.NotEmpty(bundle.QueryFiles);
        // every capture the queries use maps onto a TextMate scope (or is deliberately not a highlight): nothing is silently unstyled
        var unmapped = bundle.Highlights.CaptureNames.Where((n, i) => bundle.CaptureScopeNames[i] is null).ToArray();
        Assert.True(unmapped.Length == 0, $"{grammar}: captures without a scope: {string.Join(", ", unmapped)}");
        // the predicates of the prototype queries are all evaluated (no "treated as true" fallbacks)
        Assert.DoesNotContain(bundle.Diagnostics, d => d.Contains("not evaluated", StringComparison.Ordinal) || d.Contains("could not be translated", StringComparison.Ordinal));
    }

    /// <summary>
    /// Evidence for the hybrid option: the 17 languages CsCode detects but cannot highlight today (docs/LANGUAGES.md) all have a grammar and
    /// Neovim queries on this machine; each must load within the runtime's ABI and its highlight query must compile with this engine.
    /// </summary>
    [Theory]
    [InlineData("toml")]
    [InlineData("kotlin")]
    [InlineData("scala")]
    [InlineData("zig")]
    [InlineData("proto")]
    [InlineData("graphql")]
    [InlineData("haskell")]
    [InlineData("elixir")]
    [InlineData("erlang")]
    [InlineData("ocaml")]
    [InlineData("vue")]
    [InlineData("svelte")]
    [InlineData("terraform")]
    [InlineData("nix")]
    [InlineData("rst")]
    [InlineData("asm")]
    [InlineData("csv")]
    public void Grammars_for_the_languages_TextMate_cannot_highlight_load_and_compile(string grammar)
    {
        var bundle = RequireGrammar(grammar);
        Assert.InRange(bundle.Language.AbiVersion, TsRuntime.MinAbi, TsRuntime.MaxAbi);
        Assert.NotNull(bundle.Highlights);
        Assert.True(bundle.Highlights!.PatternCount > 0);
    }

    [Fact]
    public void TypeScript_highlights_inherit_the_ecma_queries_first()
    {
        var bundle = RequireGrammar("typescript");
        var names = bundle.QueryFiles.Select(f => Path.GetFileName(Path.GetDirectoryName(f))).ToArray();
        Assert.True(Array.IndexOf(names, "ecma") >= 0 && Array.IndexOf(names, "ecma") < Array.IndexOf(names, "typescript"), string.Join(",", names));
    }

    [Theory]
    [InlineData(99u)]
    [InlineData(12u)]
    public void A_grammar_with_an_incompatible_ABI_is_refused_not_parsed(uint abi)
    {
        RequireRuntime();
        Assert.SkipUnless(File.Exists("/usr/bin/gcc") || File.Exists("/usr/bin/cc"), "needs gcc to build a fake grammar");
        using var dir = new TempDirectory("cscode-ts-abi-");
        var source = dir.WriteFile("fake.c", $"static unsigned int fake[64] = {{ {abi}u }};\nconst void *tree_sitter_fake{abi}(void) {{ return fake; }}\n");
        var lib = Path.Combine(dir.Path, $"libfake{abi}.so");
        using (var gcc = Process.Start(new ProcessStartInfo(File.Exists("/usr/bin/gcc") ? "/usr/bin/gcc" : "/usr/bin/cc", ["-shared", "-fPIC", "-o", lib, source]) { RedirectStandardError = true })!)
        {
            gcc.WaitForExit();
            Assert.SkipUnless(gcc.ExitCode == 0 && File.Exists(lib), "gcc could not build the fake grammar: " + gcc.StandardError.ReadToEnd());
        }
        var language = TsLanguage.Load("fake" + abi, lib, out var error);
        Assert.Null(language);
        Assert.Contains($"ABI {abi}", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_grammar_is_reported_not_thrown()
    {
        RequireRuntime();
        using var registry = new GrammarRegistry(new GrammarLocator([], useNixStore: false), _ => null);
        Assert.Null(registry.Get("rust", out var error));
        Assert.Contains("no compiled tree-sitter grammar", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_names_map_to_TextMate_scopes_by_longest_prefix_with_the_language_suffix()
    {
        Assert.Equal("keyword.operator.ternary.rust", CaptureScopes.ScopeFor("keyword.conditional.ternary", "rust", "rust"));
        Assert.Equal("keyword.control.conditional.ts", CaptureScopes.ScopeFor("keyword.conditional", "typescript", "ts"));
        Assert.Equal("keyword.type.cs", CaptureScopes.ScopeFor("type.builtin", "c_sharp", "cs")); // C#'s own TextMate scope for `int`
        Assert.Equal("entity.name.function.decorator.python", CaptureScopes.ScopeFor("attribute", "python", "python"));
        Assert.Equal("markup.heading.1.markdown", CaptureScopes.ScopeFor("markup.heading.1", "markdown", "markdown"));
        Assert.Equal("variable.other.readwrite", CaptureScopes.ScopeFor("variable.something.new", "x", null)); // unknown tail → known prefix
        Assert.Equal(CaptureScopes.Skip, CaptureScopes.ScopeFor("_name", "rust", "rust"));
        Assert.Equal(CaptureScopes.Skip, CaptureScopes.ScopeFor("spell", "rust", "rust"));
        Assert.Equal(CaptureScopes.None, CaptureScopes.ScopeFor("none", "rust", "rust"));
        Assert.Null(CaptureScopes.ScopeFor("totally.unknown", "rust", "rust"));
    }

    [Theory]
    [InlineData("^[A-Z][A-Z%d_]*$", "FOO_1", true)]
    [InlineData("^[A-Z][A-Z%d_]*$", "Foo", false)]
    [InlineData("^_*[A-Z][A-Z%d_]*$", "__BAR", true)]
    [InlineData("^%u", "Upper", true)]
    [InlineData("^%u", "lower", false)]
    [InlineData("^[%l_].*$", "snake_case", true)]
    [InlineData("^/[*][*][^*].*[*]/$", "/** doc */", true)]
    [InlineData("^/[*][*][^*].*[*]/$", "/*** x */", false)]
    [InlineData("%stype%s*=", "<a type =", true)]
    [InlineData("^[-][-]", "--x", true)]
    [InlineData("a.-b", "axxb", true)]
    [InlineData("%${", "$x{", false)]
    [InlineData("%${", "${", true)]
    [InlineData("^#!/", "#!/bin/sh", true)]
    public void Lua_patterns_translate_to_equivalent_regexes(string lua, string input, bool expected)
    {
        var regex = LuaPattern.Compile(lua);
        Assert.NotNull(regex);
        Assert.Equal(expected, regex!.IsMatch(input));
    }

    [Theory]
    [InlineData("%b()")]
    [InlineData("%f[%w]x")]
    [InlineData("[%U]")]
    public void Untranslatable_Lua_patterns_are_reported_not_approximated(string lua) => Assert.Null(LuaPattern.Compile(lua));

    [Fact]
    public void CSharp_tokens_get_the_scopes_the_CSharp_TextMate_grammar_uses()
    {
        var bundle = RequireGrammar("c_sharp");
        const string text = "using System;\nclass Foo\n{\n    // note\n    int M(int a) { var s = \"x\\n\"; return a; }\n}\n";
        var scopes = Highlight(bundle, text);
        Assert.Equal("source.cs", scopes[0][0]);
        Assert.True(Has(ScopesOf(text, scopes, "class"), "storage.type"), string.Join(" ", ScopesOf(text, scopes, "class")));
        Assert.True(Has(ScopesOf(text, scopes, "// note"), "comment"), string.Join(" ", ScopesOf(text, scopes, "// note")));
        Assert.True(Has(ScopesOf(text, scopes, "int"), "keyword.type"), string.Join(" ", ScopesOf(text, scopes, "int")));
        Assert.True(Has(ScopesOf(text, scopes, "M("), "entity.name.function"), string.Join(" ", ScopesOf(text, scopes, "M(")));
        Assert.True(Has(ScopesOf(text, scopes, "return"), "keyword.control"), string.Join(" ", ScopesOf(text, scopes, "return")));
        // an escape sequence nests inside its string, as in TextMate: string.quoted > constant.character.escape
        var escape = ScopesOf(text, scopes, "\\n");
        Assert.True(Has(escape, "string") && Has(escape, "constant.character.escape"), string.Join(" ", escape));
        Assert.True(Array.FindIndex(escape, s => s.StartsWith("string", StringComparison.Ordinal)) < Array.FindIndex(escape, s => s.StartsWith("constant.character.escape", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rust_python_and_typescript_keywords_follow_their_categories()
    {
        var rust = RequireGrammar("rust");
        const string rs = "fn main() { let s = \"x\"; if s.len() > 0 { return; } }\n";
        var r = Highlight(rust, rs);
        Assert.True(Has(ScopesOf(rs, r, "fn"), "storage.type.function"), string.Join(" ", ScopesOf(rs, r, "fn")));
        Assert.True(Has(ScopesOf(rs, r, "if"), "keyword.control.conditional"), string.Join(" ", ScopesOf(rs, r, "if")));
        Assert.True(Has(ScopesOf(rs, r, "\"x\""), "string"));

        var python = RequireGrammar("python");
        const string py = "@dec\ndef f(x):\n    return x  # c\n";
        var p = Highlight(python, py);
        Assert.True(Has(ScopesOf(py, p, "def"), "storage.type.function"), string.Join(" ", ScopesOf(py, p, "def")));
        Assert.True(Has(ScopesOf(py, p, "# c"), "comment"));
        Assert.True(Has(ScopesOf(py, p, "dec"), "entity.name.function.decorator"), string.Join(" ", ScopesOf(py, p, "dec")));

        var ts = RequireGrammar("typescript");
        const string tsx = "const x: number = 1;\nfunction f(a: string) { return `v${a}`; }\n";
        var t = Highlight(ts, tsx);
        Assert.True(Has(ScopesOf(tsx, t, "number"), "support.type.primitive"), string.Join(" ", ScopesOf(tsx, t, "number")));
        Assert.True(Has(ScopesOf(tsx, t, "${"), "punctuation.definition.template-expression"), string.Join(" ", ScopesOf(tsx, t, "${")));
    }

    [Fact]
    public void TSX_tags_and_attributes_are_highlighted()
    {
        var bundle = RequireGrammar("tsx");
        const string text = "const x = <div className=\"a\">{y}</div>;\n";
        var scopes = Highlight(bundle, text);
        Assert.True(Has(ScopesOf(text, scopes, "div"), "entity.name.tag"), string.Join(" ", ScopesOf(text, scopes, "div")));
        Assert.True(Has(ScopesOf(text, scopes, "className"), "entity.other.attribute-name"), string.Join(" ", ScopesOf(text, scopes, "className")));
    }

    [Fact]
    public void Markdown_injects_the_inline_grammar_and_fenced_code_languages()
    {
        var bundle = RequireGrammar("markdown");
        RequireGrammar("markdown_inline");
        RequireGrammar("rust");
        const string text = "# Title\n\nSome *em* and `code` text.\n\n```rust\nfn main() { let x = 1; }\n```\n";
        var scopes = Highlight(bundle, text);
        Assert.True(Has(ScopesOf(text, scopes, "Title"), "markup.heading"), string.Join(" ", ScopesOf(text, scopes, "Title")));
        Assert.True(Has(ScopesOf(text, scopes, "em*"), "markup.italic"), string.Join(" ", ScopesOf(text, scopes, "em*")));
        Assert.True(Has(ScopesOf(text, scopes, "code`"), "markup.inline.raw"), string.Join(" ", ScopesOf(text, scopes, "code`")));
        var fn = ScopesOf(text, scopes, "fn");
        Assert.Contains("source.rust", fn);
        Assert.True(Has(fn, "storage.type.function"), string.Join(" ", fn));
        Assert.Equal("text.html.markdown", fn[0]);
    }

    [Fact]
    public void HTML_injects_JavaScript_into_script_and_CSS_into_style()
    {
        var bundle = RequireGrammar("html");
        RequireGrammar("javascript");
        RequireGrammar("css");
        const string text = "<html><script>let a = 1;</script><style>p { color: red; }</style></html>\n";
        var scopes = Highlight(bundle, text);
        Assert.True(Has(ScopesOf(text, scopes, "html"), "entity.name.tag"), string.Join(" ", ScopesOf(text, scopes, "html")));
        var let = ScopesOf(text, scopes, "let");
        Assert.Contains("source.js", let);
        var color = ScopesOf(text, scopes, "color");
        Assert.Contains("source.css", color);
        Assert.True(Has(color, "support.type.property-name"), string.Join(" ", color));
    }

    [Fact]
    public void An_edit_reparses_incrementally_and_reports_only_the_changed_ranges()
    {
        var bundle = RequireGrammar("c_sharp");
        var before = string.Concat(Enumerable.Range(0, 200).Select(i => $"class C{i} {{ int F{i}() {{ return {i}; }} }}\n"));
        using var registry = NewRegistry();
        using var doc = new SyntaxDocument(registry, bundle);
        Assert.Equal(ParseOutcome.Completed, doc.Parse(new StringInput(before), long.MaxValue));
        // replace "return 100;" by "return 100 + 1;" on line 100
        var at = before.IndexOf("return 100;", StringComparison.Ordinal) + "return 100".Length;
        var after = before.Insert(at, " + 1");
        var lineStart = before.LastIndexOf('\n', at) + 1;
        doc.ApplyEdit(new TextEditInfo(at, at, at + 4, 100, at - lineStart, 100, at - lineStart, 100, at - lineStart + 4));
        Assert.Equal(ParseOutcome.Completed, doc.Parse(new StringInput(after), long.MaxValue));
        Assert.NotEmpty(doc.ChangedRanges);
        Assert.All(doc.ChangedRanges, r => Assert.InRange(r.Start, lineStart - 1, lineStart + 60));
        // the incremental tree is the tree of the new text
        using var fresh = new SyntaxDocument(registry, bundle);
        fresh.Parse(new StringInput(after), long.MaxValue);
        Assert.Equal(fresh.TreeString(), doc.TreeString());
    }

    [Fact]
    public void A_parse_is_sliced_by_its_deadline_and_resumes_to_the_same_tree()
    {
        var bundle = RequireGrammar("c_sharp");
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
        var input = new StringInput(text);
        using var registry = NewRegistry();
        using var sliced = new SyntaxDocument(registry, bundle);
        var slices = 0;
        while (sliced.Parse(input, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000) == ParseOutcome.Halted) // 1 ms slices
        {
            Assert.True(++slices < 100_000);
            Assert.True(sliced.IsParsing);
        }
        Assert.True(slices > 1, "a ~1 MB parse should take more than one 1 ms slice");
        using var whole = new SyntaxDocument(registry, bundle);
        whole.Parse(input, long.MaxValue);
        Assert.Equal(whole.TreeString()!.Length, sliced.TreeString()!.Length);
        Assert.Equal(whole.TreeString(), sliced.TreeString());
    }

    [Fact]
    public async Task A_running_parse_is_cancelled_from_another_thread_and_a_new_text_starts_over()
    {
        var bundle = RequireGrammar("c_sharp");
        var big = string.Concat(Enumerable.Range(0, 120_000).Select(i => $"class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));
        var input = new StringInput(big);
        using var registry = NewRegistry();
        using var doc = new SyntaxDocument(registry, bundle);
        var parse = Task.Factory.StartNew(() =>
        {
            var sw = Stopwatch.StartNew();
            var outcome = doc.Parse(input, long.MaxValue);
            return (outcome, sw.Elapsed);
        }, TaskCreationOptions.LongRunning);
        // Parse clears the flag when it starts (a cancel only stops the parse in flight), so keep asking until it has stopped
        while (!parse.IsCompleted)
        {
            doc.RequestCancel();
            await Task.Delay(1);
        }
        var (result, elapsed) = await parse;
        Assert.Equal(ParseOutcome.Halted, result);
        Assert.True(doc.IsParsing); // halted, resumable
        // a different text abandons the stale halted parse instead of resuming it
        const string small = "class X { }\n";
        Assert.Equal(ParseOutcome.Completed, doc.Parse(new StringInput(small), long.MaxValue));
        Assert.Contains("class_declaration", doc.TreeString()!, StringComparison.Ordinal);
        Assert.False(doc.IsParsing);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"cancel took {elapsed}");
    }

    [Fact]
    public void Disposing_documents_returns_the_native_memory()
    {
        var bundle = RequireGrammar("rust");
        Assert.SkipUnless(TsRuntime.AllocationsTracked, "allocation tracking is off (CSCODE_TREESITTER_TRACK_ALLOC=0)");
        var text = string.Concat(Enumerable.Range(0, 2000).Select(i => $"fn f{i}(a: i32) -> i32 {{ let x = a * {i}; x + 1 }}\n"));
        using var registry = NewRegistry();
        // warm up: the grammar's lazily built tables and the registry's parsers are allocated once
        using (var warm = new SyntaxDocument(registry, bundle)) warm.Parse(new StringInput(text), long.MaxValue);
        var before = TsRuntime.AllocatedBytes;
        var docs = new List<SyntaxDocument>();
        for (var i = 0; i < 20; i++)
        {
            var d = new SyntaxDocument(registry, bundle);
            d.Parse(new StringInput(text), long.MaxValue);
            docs.Add(d);
        }
        var peak = TsRuntime.AllocatedBytes;
        foreach (var d in docs) d.Dispose();
        var after = TsRuntime.AllocatedBytes;
        Assert.True(peak - before > 20 * 100_000, $"20 trees of ~110 KB of Rust should hold well over 2 MB, measured {(peak - before) / 1024} KB");
        // other tests may run tree-sitter concurrently: allow their in-flight allocations, but 20 trees must not stay behind
        Assert.True(after - before < (peak - before) / 10, $"native bytes before {before}, peak {peak}, after {after}");
    }
}
