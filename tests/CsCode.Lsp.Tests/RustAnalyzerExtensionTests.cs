using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions.RustAnalyzer;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>rust-analyzer's extension without a server: snippets, the syntax tree text, runnables, lens and hover rewriting.</summary>
public class RustAnalyzerExtensionTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string RunRunnable = """
        {"label":"run app","kind":"cargo","args":{"environment":{"RUSTC_TOOLCHAIN":"/t"},"cwd":"/w","workspaceRoot":"/w",
         "cargoArgs":["run","--package","app","--bin","app"],"executableArgs":[]}}
        """;
    private const string TestRunnable = """
        {"label":"test tests::it","kind":"cargo","args":{"cwd":"/w","workspaceRoot":"/w","cargoArgs":["test","--package","app","--lib"],
         "executableArgs":["tests::it","--exact","--nocapture"]}}
        """;

    [Theory]
    [InlineData("fn f() {$0}", "fn f() {}")]
    [InlineData("let ${1:x} = ${2:value};$0", "let x = value;")]
    [InlineData("match ${1:e} {\n    ${2:_} => ${0:todo!()},\n}", "match e {\n    _ => todo!(),\n}")]
    [InlineData("${1|a,b,c|}", "a")]
    [InlineData("cost: \\$5 and \\} and \\\\", "cost: $5 and } and \\")]
    [InlineData("${1:outer ${2:inner}}", "outer inner")]
    [InlineData("$10x", "x")]
    [InlineData("plain $ text", "plain $ text")]
    public void Snippets_become_the_text_they_insert(string snippet, string plain) => Assert.Equal(plain, Snippets.ToPlainText(snippet));

    [Fact]
    public void Snippet_text_edits_in_a_code_action_answer_are_made_plain_and_others_left_alone()
    {
        var answer = Json("""
            {"title":"Add impl","edit":{"documentChanges":[{"textDocument":{"uri":"file:///a.rs","version":1},"edits":[
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"newText":"impl $0Foo {}","insertTextFormat":2},
              {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":0}},"newText":"keep $0 literal"}]}]}}
            """);
        var plain = Snippets.MakePlain(answer);
        var edits = plain.GetProperty("edit").GetProperty("documentChanges")[0].GetProperty("edits");
        Assert.Equal("impl Foo {}", edits[0].GetProperty("newText").GetString());
        Assert.False(edits[0].TryGetProperty("insertTextFormat", out _));
        Assert.Equal("keep $0 literal", edits[1].GetProperty("newText").GetString());
        var none = Json("""{"title":"x"}""");
        Assert.Equal(none.GetRawText(), Snippets.MakePlain(none).GetRawText());
    }

    [Fact]
    public void The_syntax_tree_json_is_shown_as_indented_text()
    {
        var tree = """
            {"type":"Node","kind":"SOURCE_FILE","start":[0,0,0],"end":[12,1,0],"children":[
              {"type":"Node","kind":"FN","start":[0,0,0],"end":[11,0,11],"children":[{"type":"Token","kind":"FN_KW","start":[0,0,0],"end":[2,0,2]}]}]}
            """;
        Assert.Equal("SOURCE_FILE@0..12\n  FN@0..11\n    FN_KW@0..2\n", SyntaxTreeText.Render(tree));
        Assert.Equal("not json", SyntaxTreeText.Render("not json"));
    }

    [Fact]
    public void Runnables_are_parsed_and_classified()
    {
        var run = Runnable.Parse(Json(RunRunnable))!;
        Assert.True(run.IsRun);
        Assert.Equal("app", run.ValueOf("--bin"));
        Assert.Equal("/w", run.Cwd);
        Assert.Equal("/t", run.Environment["RUSTC_TOOLCHAIN"]);
        Assert.Equal("cargo run --package app --bin app", run.CommandLine);
        var test = Runnable.Parse(Json(TestRunnable))!;
        Assert.True(test.IsTest);
        Assert.Equal("cargo test --package app --lib -- tests::it --exact --nocapture", test.CommandLine);
        Assert.Null(Runnable.Parse(Json("""{"label":"x","kind":"shell","args":{}}""")));
    }

    [Fact]
    public void Run_and_debug_lenses_stay_on_binaries_and_go_from_tests()
    {
        var ext = new RustAnalyzerExtension();
        CodeLens Lens(string command, string runnable) => new(TextRange.FromLines(0, 0, 0, 1), new Command("▶︎ Run", command, [Json(runnable)]));
        var lenses = new[]
        {
            Lens(RustAnalyzerExtension.RunSingleCommand, RunRunnable), Lens(RustAnalyzerExtension.DebugSingleCommand, RunRunnable),
            Lens(RustAnalyzerExtension.RunSingleCommand, TestRunnable), Lens(RustAnalyzerExtension.DebugSingleCommand, TestRunnable),
            new CodeLens(TextRange.FromLines(1, 0, 1, 1), new Command("3 references", "rust-analyzer.showReferences")),
        };
        var kept = ext.AdjustCodeLenses(null!, lenses, new Uri("file:///a.rs"));
        Assert.Equal([lenses[0], lenses[1], lenses[4]], kept);
        var untouched = new[] { lenses[0], lenses[4] };
        Assert.Same(untouched, ext.AdjustCodeLenses(null!, untouched, new Uri("file:///a.rs")));
    }

    [Fact]
    public void Hover_actions_become_one_trusted_block_of_command_links_with_their_arguments()
    {
        var ext = new RustAnalyzerExtension();
        var raw = Json($$"""
            {"contents":{"kind":"markdown","value":"fn main()"},
             "actions":[{"commands":[{"title":"▶︎ Run ","command":"rust-analyzer.runSingle","arguments":[{{RunRunnable}}],"tooltip":"run app"},
                                     {"title":"Go to [Type]","command":"rust-analyzer.gotoLocation","arguments":[]}]}]}
            """);
        var hover = ext.AdjustHover(null!, raw, new Hover([MarkupContent.Markdown("fn main()")]), new Uri("file:///a.rs"))!;
        Assert.Equal(2, hover.Contents.Count);
        Assert.False(hover.Contents[0].IsTrusted);
        var actions = hover.Contents[1];
        Assert.True(actions.IsTrusted && actions.IsMarkdown);
        Assert.StartsWith("[▶︎ Run](command:rust-analyzer.runSingle?", actions.Value);
        Assert.Contains(" · [Go to \\[Type\\]](command:rust-analyzer.gotoLocation?%5B%5D)", actions.Value);
        var encoded = actions.Value[(actions.Value.IndexOf('?') + 1)..actions.Value.IndexOf(')')];
        var args = JsonDocument.Parse(Uri.UnescapeDataString(encoded)).RootElement;
        Assert.Equal("run app", Runnable.Parse(args[0])!.Label);

        var plain = new Hover([MarkupContent.Markdown("x")]);
        Assert.Same(plain, ext.AdjustHover(null!, Json("""{"contents":"x"}"""), plain, new Uri("file:///a.rs")));
    }

    [Fact]
    public void The_experimental_capabilities_are_the_ones_CsCode_honours()
    {
        var experimental = new JsonObject();
        new RustAnalyzerExtension().AddExperimentalCapabilities(experimental);
        Assert.Equal(["hoverActions", "serverStatusNotification", "codeActionGroup", "snippetTextEdit", "commands"], experimental.Select(kv => kv.Key));
        Assert.Equal(
            ["rust-analyzer.runSingle", "rust-analyzer.debugSingle", "rust-analyzer.showReferences", "rust-analyzer.gotoLocation"],
            experimental["commands"]!["commands"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Fact]
    public void Server_status_text_follows_health_and_quiescence()
    {
        Assert.Equal("⟳ rust-analyzer", ServerStatusDisplay.TextOf(new ServerStatus("ok", false, null)));
        Assert.Equal("✓ rust-analyzer", ServerStatusDisplay.TextOf(new ServerStatus("ok", true, null)));
        Assert.Equal("⚠ rust-analyzer", ServerStatusDisplay.TextOf(new ServerStatus("warning", true, "proc macros")));
        Assert.Equal("✖ rust-analyzer", ServerStatusDisplay.TextOf(new ServerStatus("error", false, "no Cargo.toml")));
        Assert.Equal(new ServerStatus("warning", true, "m"), ServerStatus.From(Json("""{"health":"warning","quiescent":true,"message":"m"}""")));
    }
}
