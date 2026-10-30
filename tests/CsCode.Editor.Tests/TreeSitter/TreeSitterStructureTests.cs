using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterViewTests;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Structural features from the tree (docs/TREE-SITTER.md, "Structure"): Expand/Shrink Selection from the enclosing nodes, folding from
/// <c>folds.scm</c>, the outline and breadcrumbs from <c>symbols.scm</c>/<c>tags.scm</c>/<c>locals.scm</c> — first on the engine, then through the
/// editor's real commands and features, deferring to a language server whenever one provides the feature.
/// </summary>
public class TreeSitterStructureTests
{
    private const string Rust = """
        use std::fmt;
        use std::io;

        struct Point {
            x: i32,
            y: i32,
        }

        impl Point {
            fn norm(&self) -> i32 {
                let sum = self.x * self.x + self.y * self.y;
                sum
            }
        }

        fn main() {
            let p = Point { x: 1, y: 2 };
            println!("{}", p.norm());
        }
        """;

    private const string Toml = """
        [package]
        name = "demo"
        version = "0.1.0"

        [dependencies]
        serde = { version = "1", features = ["derive"] }
        tokio = "1"

        [[bin]]
        name = "tool"
        """;

    private const string Markdown = """
        # Title

        Intro text.

        ## Install

        Run it.

        ### Linux

        apt install demo

        ## Use

        Done.
        """;

    private static (SyntaxDocument Doc, StringInput Text, GrammarRegistry Registry) Parse(string grammar, string text)
    {
        var bundle = RequireGrammar(grammar);
        var registry = NewRegistry();
        var doc = new SyntaxDocument(registry, bundle);
        var input = new StringInput(text);
        Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
        return (doc, input, registry);
    }

    private static string Text(string text, SyntaxSpan s) => text[s.Start..s.End];

    // ---- the engine ----

    [Fact]
    public void Enclosing_nodes_grow_strictly_from_the_word_to_the_whole_file()
    {
        var (doc, input, registry) = Parse("rust", Rust);
        using var _ = registry;
        using var __ = doc;
        var at = Rust.IndexOf("self.x * self.x", StringComparison.Ordinal) + 5; // inside the first `x`
        var chain = doc.EnclosingNodes(input, at, at);
        Assert.Equal("x", Text(Rust, chain[0]));
        Assert.Equal("self.x", Text(Rust, chain[1]));
        Assert.Contains(chain, s => s.Type == "binary_expression");
        Assert.Contains(chain, s => s.Type == "function_item" && Text(Rust, s).StartsWith("fn norm", StringComparison.Ordinal));
        Assert.Contains(chain, s => s.Type == "impl_item");
        Assert.Equal((0, Rust.Length), (chain[^1].Start, chain[^1].End));
        for (var i = 1; i < chain.Count; i++)
            Assert.True(chain[i].Start <= chain[i - 1].Start && chain[i].End >= chain[i - 1].End && (chain[i].End - chain[i].Start) > (chain[i - 1].End - chain[i - 1].Start));

        // a caret at the END of a word takes the word to its left (as VS Code does)
        var end = Rust.IndexOf("norm(&self)", StringComparison.Ordinal) + 4;
        Assert.Equal("norm", Text(Rust, doc.EnclosingNodes(input, end, end)[0]));
        // a selection is grown from the smallest node that contains all of it
        var sel = Rust.IndexOf("x: 1, y: 2", StringComparison.Ordinal);
        Assert.Equal("{ x: 1, y: 2 }", Text(Rust, doc.EnclosingNodes(input, sel, sel + 10)[0]));
    }

    [Fact]
    public void Fold_nodes_come_from_the_grammar_folds_query_with_quantified_captures_as_one_fold()
    {
        var (doc, input, registry) = Parse("rust", Rust);
        using var _ = registry;
        using var __ = doc;
        var folds = doc.FoldNodes(input)!;
        Assert.Contains(folds, f => f.Type == "impl_item");
        Assert.Contains(folds, f => f.Type == "function_item" && Text(Rust, f).StartsWith("fn main", StringComparison.Ordinal));
        // `(use_declaration)+ @fold`: both use lines are one fold
        Assert.Contains(folds, f => Text(Rust, f) == "use std::fmt;\nuse std::io;");
    }

    [Fact]
    public void Folds_keep_the_first_line_and_a_closing_line_visible_and_drop_trailing_blank_lines()
    {
        RequireRuntime();
        var snapshot = SnapshotOf(Rust);
        var (doc, input, registry) = Parse("rust", Rust);
        using var _ = registry;
        using var __ = doc;
        var folds = TreeSitterLanguageFeatures.Folds(doc.FoldNodes(input)!, snapshot);
        // impl Point { (line 8) ... } (line 13): lines 9-12 fold away, the closing brace stays
        Assert.Contains(new FoldingRange(8, 12), folds);
        Assert.Contains(new FoldingRange(0, 1, FoldingRangeKind.Imports), folds);
        Assert.All(folds, f => Assert.True(f.EndLine > f.StartLine));
        Assert.Equal(folds.Select(f => f.StartLine).Distinct().Count(), folds.Count); // one fold per start line

        var md = SnapshotOf(Markdown);
        var (mdoc, minput, mregistry) = Parse("markdown", Markdown);
        using var ___ = mregistry;
        using var ____ = mdoc;
        var sections = TreeSitterLanguageFeatures.Folds(mdoc.FoldNodes(minput)!, md);
        Assert.Contains(new FoldingRange(4, 10), sections); // "## Install" to "apt install demo", the blank line before "## Use" left out
    }

    private static Documents.DocumentSnapshot SnapshotOf(string text)
    {
        var ui = new ThreadAffinityUiThread();
        var doc = new Documents.EditorDocument(new Uri("file:///tmp/structure-test.txt"), "plaintext", Documents.EditorDocument.CreateBuffer(text, ui), ui);
        return doc.Snapshot();
    }

    [Theory]
    [InlineData("rust", "tags")]
    [InlineData("python", "tags")]
    [InlineData("toml", "symbols")]
    public void Symbols_come_from_own_queries_then_tags_then_locals(string grammar, string expectedSource)
    {
        var bundle = RequireGrammar(grammar);
        Assert.NotNull(bundle.Query(expectedSource, out var error));
        Assert.Null(error);
        foreach (var earlier in SyntaxDocument.SymbolSources.TakeWhile(s => s != expectedSource)) Assert.Null(bundle.Query(earlier, out _));
    }

    /// <summary>Every query CsCode writes compiles against its grammar (a renamed node type in a grammar update would show here).</summary>
    [Fact]
    public void Every_own_symbols_query_compiles_against_its_grammar()
    {
        RequireRuntime();
        using var registry = NewRegistry();
        var problems = new List<string>();
        var grammars = OwnQueries.GrammarsWith("symbols").ToArray();
        Assert.True(grammars.Length >= 15, string.Join(", ", grammars));
        foreach (var g in grammars)
        {
            if (registry.Get(g, out var loadError) is not { } bundle)
            {
                problems.Add($"{g}: grammar not available ({loadError})");
                continue;
            }
            if (bundle.Query("symbols", out var error) is null) problems.Add(error!);
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_bundled_tags_query_compiles_or_the_outline_falls_back_to_locals()
    {
        var bundled = RequireBundle();
        RequireRuntime();
        using var registry = NewRegistry();
        var compiled = new List<string>();
        var problems = new List<string>();
        foreach (var g in bundled.Grammars)
        {
            if (!File.Exists(Path.Combine(bundled.GrammarsRoot, "queries", g.Name, "tags.scm"))) continue;
            var bundle = registry.Get(g.Name, out _)!;
            if (bundle.Query("tags", out var error) is null) problems.Add(error!);
            else compiled.Add(g.Name);
        }
        Assert.True(compiled.Count >= 16, string.Join(", ", compiled));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    public static TheoryData<string, string, string, string[]> OutlineSamples => new()
    {
        // grammar, sample, expected path ("a > b" = b nested in a), names that must be top-level
        { "toml", Toml, "dependencies > serde", ["package", "dependencies", "bin"] },
        { "markdown", Markdown, "Title > Install > Linux", ["Title"] },
        { "rust", Rust, "Point", ["Point", "main"] },
        { "python", "class A:\n    def f(self):\n        return 1\n\ndef g():\n    pass\n", "A > f", ["A", "g"] },
        { "kotlin", "class Greeter {\n    fun hello() = println(\"hi\")\n}\nfun main() {}\n", "Greeter > hello", ["Greeter", "main"] },
        { "terraform", "resource \"aws_instance\" \"web\" {\n  ami = \"x\"\n  lifecycle {\n    create_before_destroy = true\n  }\n}\n", "resource \"aws_instance\" \"web\" > lifecycle", ["resource \"aws_instance\" \"web\""] },
        { "yaml", "server:\n  port: 80\n  host: x\nlog: true\n", "server > port", ["server", "log"] },
        { "json", "{\"a\": {\"b\": 1}, \"c\": 2}", "\"a\" > \"b\"", ["\"a\"", "\"c\""] },
        { "graphql", "type Query {\n  user(id: ID!): User\n}\nenum Color { RED }\n", "Query > user", ["Query", "Color"] },
        { "cmake", "function(build_all target)\n  message(${target})\nendfunction()\nmacro(helper)\nendmacro()\n", "build_all", ["build_all", "helper"] },
        { "erlang", "-module(demo).\n-record(point, {x, y}).\nf(0) -> zero;\nf(N) -> N.\n", "f", ["demo", "point", "f"] },
        { "haskell", "module M where\ndata Shape = Circle | Square\nclass Area a where\n  area :: a -> Int\nmain = print 1\n", "Shape", ["Shape", "Area", "main"] },
        { "zig", "const Point = struct {\n    x: i32,\n    fn len(self: Point) i32 { return self.x; }\n};\npub fn main() void {}\ntest \"adds\" {}\n", "Point > len", ["Point", "main"] },
        { "nasm", "section .text\nglobal _start\n_start:\n    mov eax, 1\nloop_top:\n    dec eax\n", "_start", ["_start", "loop_top"] },
        { "bash", "build() {\n  make\n}\nfunction clean {\n  rm -rf out\n}\n", "build", ["build", "clean"] },
        { "css", ".card { color: red; }\n@media (max-width: 600px) {\n  .card { color: blue; }\n}\n", "(max-width: 600px) > .card", [".card"] },
        { "elixir", "defmodule Demo do\n  def hello, do: :world\nend\n", "Demo > hello", ["Demo"] },
        { "ocaml", "let add a b = a + b\nmodule M = struct\n  let x = 1\nend\n", "add", ["add", "M"] },
    };

    [Theory]
    [MemberData(nameof(OutlineSamples))]
    public void The_outline_of_each_language_without_a_server(string grammar, string sample, string path, string[] topLevel)
    {
        var (doc, input, registry) = Parse(grammar, sample);
        using var _ = registry;
        using var __ = doc;
        var symbols = TreeSitterLanguageFeatures.Symbols(doc.SymbolNodes(input)!, SnapshotOf(sample));
        string Dump(IReadOnlyList<DocumentSymbol> list, string indent = "") =>
            string.Concat(list.Select(s => $"{indent}{s.Name} ({s.Kind})\n" + Dump(s.Children ?? [], indent + "  ")));
        var dump = Dump(symbols);
        foreach (var name in topLevel) Assert.True(symbols.Any(s => s.Name == name), $"{grammar}: no top-level '{name}' in\n{dump}");
        IReadOnlyList<DocumentSymbol> level = symbols;
        foreach (var step in path.Split(" > "))
        {
            var found = level.FirstOrDefault(s => s.Name == step);
            Assert.True(found is not null, $"{grammar}: '{step}' of '{path}' not found in\n{dump}");
            level = found!.Children ?? [];
        }
    }

    [Fact]
    public void The_locals_fallback_lists_definitions_an_outline_shows()
    {
        const string sh = "build() {\n  local x=1\n  make\n}\n";
        var (doc, input, registry) = Parse("bash", sh);
        using var _ = registry;
        using var __ = doc;
        var symbols = doc.SymbolNodes(input, ["locals"])!;
        var function = Assert.Single(symbols);
        Assert.Equal("function", function.Kind);
        Assert.Equal("build", sh[function.NameStart..function.NameEnd]);
        Assert.Equal(0, function.Start); // the definition is the name's parent node: the whole function
    }

    // ---- through the editor ----

    private static async Task<(ViewHost Host, CodeEditorControl View)> OpenOnTreeSitterAsync(string text, string file, string? language = null)
    {
        var host = ViewHost.Create();
        if (language is not null) host.Config.Set($"[{language}]", new Dictionary<string, object> { [TreeSitterService.SettingKey] = TreeSitterService.TreeSitter });
        var (_, view, _) = await host.OpenAsync(text, file);
        await WaitForTreeSitter(view, 0);
        return (host, view);
    }

    [AvaloniaFact]
    public async Task Expand_and_shrink_selection_walk_the_syntax_tree_when_no_server_offers_selection_ranges()
    {
        RequireGrammar("toml");
        var (host, view) = await OpenOnTreeSitterAsync(Toml, "Cargo.toml");
        using var _ = host;
        var at = Toml.IndexOf("\"derive\"", StringComparison.Ordinal) + 2;
        var line = Toml[..at].Count(c => c == '\n');
        var col = at - (Toml.LastIndexOf('\n', at) + 1);
        view.Controller.SetSelection(TextRange.At(line, col));

        string Selected() => view.Document.GetText(view.Controller.Selections[0]);
        await host.Commands.ExecuteAsync("editor.action.smartSelect.expand");
        await ViewHost.WaitFor(() => Selected().Length > 0, "first expansion");
        Assert.Equal("derive", Selected().Trim('"')); // the string's content (or the string)
        var steps = new List<string> { Selected() };
        for (var i = 0; i < 6; i++)
        {
            await host.Commands.ExecuteAsync("editor.action.smartSelect.expand");
            ViewHost.Pump();
            steps.Add(Selected());
        }
        Assert.Contains("[\"derive\"]", steps);
        Assert.Contains(steps, s => s.StartsWith("{ version = \"1\"", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.StartsWith("serde = {", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.StartsWith("[dependencies]", StringComparison.Ordinal));
        for (var i = 1; i < steps.Count; i++) Assert.True(steps[i].Contains(steps[i - 1], StringComparison.Ordinal), $"step {i}: '{steps[i]}' does not contain '{steps[i - 1]}'");

        await host.Commands.ExecuteAsync("editor.action.smartSelect.shrink");
        ViewHost.Pump();
        Assert.Equal(steps[^2], Selected());
    }

    [AvaloniaFact]
    public async Task Folding_follows_the_tree_for_a_tree_sitter_document_and_indentation_otherwise()
    {
        RequireGrammar("toml");
        var (host, view) = await OpenOnTreeSitterAsync(Toml + "\n\n[profile.release]\nlto = true\n", "Cargo.toml");
        using var _ = host;
        var folding = view.Feature<FoldingFeature>()!;
        // TOML has no indentation to fold by: every region here comes from the tree (tables, the [[bin]] array element)
        await ViewHost.WaitFor(() => folding.RangeCount >= 3, $"tree folds ({folding.RangeCount})");
        var provider = host.Factory.Services.Features.GetProviders<IFoldingRangeProvider>(view.Document).Single();
        var ranges = await provider.ProvideFoldingRangesAsync(view.Document, CancellationToken.None);
        Assert.Contains(new FoldingRange(0, 2), ranges!); // [package] .. version (the blank line after it left out)
        Assert.Contains(new FoldingRange(4, 6), ranges!); // [dependencies] .. tokio

        // a TextMate document (C#, no server): the provider stays silent and the editor folds by indentation as before
        var (_, cs, _) = await host.OpenAsync(CSharp, "Program.cs");
        Assert.Null(await provider.ProvideFoldingRangesAsync(cs.Document, CancellationToken.None));
        await ViewHost.WaitFor(() => cs.Feature<FoldingFeature>()!.RangeCount >= 3, "indentation folds");
    }

    [AvaloniaFact]
    public async Task Breadcrumbs_and_the_outline_come_from_the_tree_when_no_server_provides_symbols()
    {
        RequireGrammar("toml");
        var (host, view) = await OpenOnTreeSitterAsync(Toml, "Cargo.toml");
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(5, 3)); // in `serde`
        var path = await view.Controller.GetBreadcrumbsAsync();
        Assert.Equal(["dependencies", "serde"], path);

        // a language server's symbols win, and the tree's provider steps aside (no entry twice in Go to Symbol)
        var server = new FakeSymbols();
        using (host.Factory.Services.Features.Register<IDocumentSymbolProvider>(DocumentSelector.ForLanguage("toml"), server))
        {
            var providers = host.Factory.Services.Features.GetProviders<IDocumentSymbolProvider>(view.Document);
            Assert.Same(server, providers[0]);
            Assert.Null(await providers[1].ProvideDocumentSymbolsAsync(view.Document, CancellationToken.None));
            // (the breadcrumbs cache their symbols per document version: an edit makes them ask again)
            view.Controller.SetSelection(TextRange.At(10, 0));
            Type(view, " ");
            view.Controller.SetSelection(TextRange.At(6, 1));
            Assert.Equal(["from the server"], await view.Controller.GetBreadcrumbsAsync());
        }
    }

    [AvaloniaFact]
    public async Task A_markdown_document_opted_into_tree_sitter_gets_its_headings_as_the_outline()
    {
        RequireGrammar("markdown");
        var (host, view) = await OpenOnTreeSitterAsync(Markdown, "README.md", language: "markdown");
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(10, 2)); // "apt install demo"
        Assert.Equal(["Title", "Install", "Linux"], await view.Controller.GetBreadcrumbsAsync());
    }

    private sealed class FakeSymbols : IDocumentSymbolProvider
    {
        public ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<DocumentSymbol>?>(
                [new DocumentSymbol("from the server", SymbolKind.Module, new TextRange(new TextPosition(0, 0), new TextPosition(20, 0)), TextRange.At(0, 0))]);
    }
}
