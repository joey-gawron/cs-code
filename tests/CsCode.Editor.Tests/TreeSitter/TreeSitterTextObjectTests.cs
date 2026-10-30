using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Highlighting;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// T33: nvim-treesitter-textobjects' <c>textobjects.scm</c> (bundled, pinned) read on the syntax tree — every bundled query compiles; the
/// captures become ranges the way the plugin reads them (all nodes of one capture in a match are one range, the comma with the argument); and
/// the modal surface hands a modal layer (Vim) the ranges of the document's current tree off the UI thread, or null at once without a tree.
/// </summary>
public class TreeSitterTextObjectTests
{
    private const string Rust = """
        fn add(a: i32, b: i32) -> i32 {
            a + b
        }

        struct Point { x: i32 }

        impl Point {
            fn norm(&self) -> i32 {
                self.x
            }
        }
        """;

    private static (SyntaxDocument Doc, StringInput Text) Parse(string grammar, string text)
    {
        var bundle = RequireGrammar(grammar);
        var registry = NewRegistry();
        var doc = new SyntaxDocument(registry, bundle);
        var input = new StringInput(text);
        Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
        return (doc, input);
    }

    private static string[] Texts(string text, IReadOnlyList<SyntaxSpan>? spans) => spans!.Select(s => text[s.Start..s.End]).ToArray();

    [Fact]
    public void Every_bundled_textobjects_query_compiles_against_its_grammar()
    {
        var bundled = RequireBundle();
        RequireRuntime();
        using var registry = NewRegistry();
        var compiled = new List<string>();
        var problems = new List<string>();
        foreach (var g in bundled.Grammars)
        {
            if (!File.Exists(Path.Combine(bundled.GrammarsRoot, "queries", g.Name, "textobjects.scm"))) continue;
            var bundle = registry.Get(g.Name, out var error);
            if (bundle is null) { problems.Add($"{g.Name}: {error}"); continue; }
            if (bundle.Query(SyntaxDocument.TextObjectsQuery, out var queryError) is null) problems.Add(queryError!);
            else compiled.Add(g.Name);
        }
        Assert.True(compiled.Count >= 30, string.Join(", ", compiled)); // 33 grammars of the bundle have one (and ecma/jsx are inherited)
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Functions_classes_and_parameters_of_a_Rust_file()
    {
        var (doc, text) = Parse("rust", Rust);
        using var _ = doc;
        Assert.Equal(["fn add(a: i32, b: i32) -> i32 {\n    a + b\n}", "fn norm(&self) -> i32 {\n        self.x\n    }"],
            Texts(Rust, doc.TextObjectRanges(text, "function.outer")));
        Assert.Contains("a + b", Texts(Rust, doc.TextObjectRanges(text, "function.inner"))[0], StringComparison.Ordinal);
        var classes = Texts(Rust, doc.TextObjectRanges(text, "class.outer"));
        Assert.Contains("struct Point { x: i32 }", classes);
        Assert.Contains(classes, c => c.StartsWith("impl Point {", StringComparison.Ordinal));
        // the argument with its comma: the plugin's "outer" (a node and the separator captured under one name in one match)
        var outer = Texts(Rust, doc.TextObjectRanges(text, "parameter.outer"));
        Assert.Contains("a: i32,", outer);
        Assert.Contains(", b: i32", outer);
        Assert.Contains("b: i32", Texts(Rust, doc.TextObjectRanges(text, "parameter.inner")));
    }

    [Fact]
    public void TOML_pairs_and_array_items_are_the_parameters_of_its_queries_and_unknown_captures_are_empty()
    {
        const string toml = "[package]\nname = \"demo\"\nkeywords = [\"a\", \"b\"]\n";
        var (doc, text) = Parse("toml", toml);
        using var _ = doc;
        var inner = Texts(toml, doc.TextObjectRanges(text, "parameter.inner"));
        Assert.Contains("name = \"demo\"", inner);
        Assert.Contains("\"b\"", inner);
        Assert.Contains(", \"b\"", Texts(toml, doc.TextObjectRanges(text, "parameter.outer")));
        Assert.Empty(doc.TextObjectRanges(text, "function.outer")!); // TOML has no functions
    }

    [Fact]
    public void A_grammar_without_a_textobjects_query_answers_null()
    {
        var (doc, text) = Parse("csv", "a,b\n1,2\n");
        using var _ = doc;
        Assert.Null(doc.TextObjectRanges(text, "parameter.outer"));
    }

    [AvaloniaFact]
    public async Task The_modal_surface_gives_the_ranges_of_the_current_tree_and_null_at_once_without_one()
    {
        RequireGrammar("toml");
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("[package]\nname = \"demo\"\nversion = \"1\"\n", "Cargo.toml");
        var uiThread = Environment.CurrentManagedThreadId;
        var task = view.Modal.RequestSyntaxObjects("parameter.inner");
        Assert.NotNull(task);
        await ViewHost.WaitFor(() => task!.IsCompleted, "the syntax lane's answer");
        var ranges = await task!;
        Assert.Equal([TextRange.FromLines(1, 0, 1, 13), TextRange.FromLines(2, 0, 2, 13)], ranges);

        // the text changes before the answer (the lane is held back so the edit comes first): null, never stale positions
        using var hold = new ManualResetEventSlim();
        host.Factory.Tokens.TreeSitter.Lane.Post(CsCode.Editor.Threading.WorkPriority.Interactive, null, _ => hold.Wait(TimeSpan.FromSeconds(30)));
        var stale = view.Modal.RequestSyntaxObjects("parameter.inner");
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "# c\n")]);
        hold.Set();
        await ViewHost.WaitFor(() => stale!.IsCompleted, "the answer after an edit");
        Assert.Null(await stale!);
        Assert.Equal(uiThread, Environment.CurrentManagedThreadId);

        // a TextMate document: no tree, null without leaving the UI thread
        var (_, cs, _) = await host.OpenAsync("class C { void M(int a) { } }\n", "a.cs");
        Assert.Null(cs.Modal.RequestSyntaxObjects("function.outer"));
    }
}
