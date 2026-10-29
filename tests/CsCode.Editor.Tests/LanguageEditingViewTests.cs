using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// B4 through the editor: a sample of the new language configurations driving the REAL control (headless): the language is detected from the
/// file name, then toggle comment (the command), auto-closing and indentation on Enter happen exactly as a user typing would see them.
/// </summary>
public class LanguageEditingViewTests
{
    private static string Text(CodeEditorControl v) => v.Document.GetText();

    private static void Type(CodeEditorControl v, string text)
    {
        foreach (var ch in text) v.TextArea.PerformTextInput(ch.ToString());
    }

    private static async Task<CodeEditorControl> OpenAsync(ViewHost host, string fileName, string text, string expectedLanguage)
    {
        var (doc, view, _) = await host.OpenAsync(text, fileName);
        Assert.Equal(expectedLanguage, doc.LanguageId);
        host.Factory.NotifyFocused(view);
        return view;
    }

    private static async Task CommentLineAsync(ViewHost host)
    {
        await host.Commands.ExecuteAsync("editor.action.commentLine");
        ViewHost.Pump();
    }

    [AvaloniaFact]
    public async Task Zig_braces_close_and_open_an_indented_block_and_comments_use_slashes()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "main.zig", "", "zig");
        Type(view, "pub fn main() void {");
        Assert.Equal("pub fn main() void {}", Text(view));
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("pub fn main() void {\n    \n}", Text(view));
        Type(view, "const s = \"");
        Assert.Equal("pub fn main() void {\n    const s = \"\"\n}", Text(view));

        view.Controller.SetSelection(TextRange.At(0, 0));
        await CommentLineAsync(host);
        Assert.StartsWith("// pub fn main() void {", Text(view));
    }

    [AvaloniaFact]
    public async Task Zig_doc_comments_continue_on_Enter()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "lib.zig", "/// Adds two numbers.", "zig");
        view.Controller.SetSelection(TextRange.At(0, 21));
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("/// Adds two numbers.\n/// ", Text(view));
    }

    [AvaloniaFact]
    public async Task Nix_comments_with_a_hash_indents_after_let_and_types_indented_strings_as_typed()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "default.nix", "", "nix");
        Type(view, "let");
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("let\n    ", Text(view));
        Type(view, "src = ''");
        Assert.Equal("let\n    src = ''", Text(view));

        view.Controller.SetSelection(TextRange.At(0, 0));
        await CommentLineAsync(host);
        Assert.StartsWith("# let\n", Text(view));
    }

    [AvaloniaFact]
    public async Task Haskell_comments_with_dashes_and_never_pairs_a_prime()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "Main.hs", "main = do", "haskell");
        view.Controller.SetSelection(TextRange.At(0, 9));
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("main = do\n    ", Text(view));
        Type(view, "let x' = 'a'");
        Assert.Equal("main = do\n    let x' = 'a'", Text(view)); // no doubled quotes anywhere

        view.Controller.SetSelection(TextRange.At(0, 0));
        await CommentLineAsync(host);
        Assert.StartsWith("-- main = do\n", Text(view));
    }

    [AvaloniaFact]
    public async Task OCaml_has_only_block_comments_so_a_line_comment_wraps_the_line()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "a.ml", "let x = 1", "ocaml");
        view.Controller.SetSelection(TextRange.At(0, 0));
        await CommentLineAsync(host);
        Assert.Equal("(* let x = 1 *)", Text(view));
        await CommentLineAsync(host);
        Assert.Equal("let x = 1", Text(view));
    }

    [AvaloniaFact]
    public async Task Elixir_indents_a_do_block_and_end_snaps_back()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "a.ex", "def f(x) do", "elixir");
        view.Controller.SetSelection(TextRange.At(0, 11));
        view.TextArea.PerformTextInput("\n");
        Type(view, "x");
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("def f(x) do\n    x\n    ", Text(view));
        Type(view, "end");
        Assert.Equal("def f(x) do\n    x\nend", Text(view));
    }

    [AvaloniaFact]
    public async Task Erlang_indents_a_clause_and_returns_to_column_zero_after_the_full_stop()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "a.erl", "f(X) ->", "erlang");
        view.Controller.SetSelection(TextRange.At(0, 7));
        view.TextArea.PerformTextInput("\n");
        Type(view, "ok.");
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("f(X) ->\n    ok.\n", Text(view));
    }

    [AvaloniaFact]
    public async Task Kotlin_KDoc_closes_and_continues()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "A.kt", "", "kotlin");
        Type(view, "/**");
        Assert.Equal("/** */", Text(view));

        var cont = await OpenAsync(host, "B.kt", "/** Adds", "kotlin");
        cont.Controller.SetSelection(TextRange.At(0, 8));
        cont.TextArea.PerformTextInput("\n");
        Assert.Equal("/** Adds\n * ", Text(cont));
    }

    [AvaloniaFact]
    public async Task Vue_opens_an_indented_line_between_tags_and_comments_with_html_comments()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "App.vue", "<template></template>", "vue");
        view.Controller.SetSelection(TextRange.At(0, 10));
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("<template>\n    \n</template>", Text(view));

        view.Controller.SetSelection(TextRange.At(0, 0));
        await CommentLineAsync(host);
        Assert.StartsWith("<!-- <template> -->\n", Text(view));
    }

    [AvaloniaFact]
    public async Task Toml_and_terraform_use_hash_comments_and_indent_inside_brackets()
    {
        using var host = ViewHost.Create();
        var toml = await OpenAsync(host, "Cargo.toml", "[dependencies]", "toml");
        await CommentLineAsync(host);
        Assert.Equal("# [dependencies]", Text(toml));

        var tf = await OpenAsync(host, "main.tf", "", "terraform");
        Type(tf, "resource \"a\" \"b\" {");
        tf.TextArea.PerformTextInput("\n");
        Assert.Equal("resource \"a\" \"b\" {\n    \n}", Text(tf));
    }

    // ---- a wider sample: the same behaviours across the new configurations ---------------------------------------------------------------

    /// <summary>Strings typed one character at a time, closers included, end up exactly as typed: auto-closing only ever saves keystrokes.</summary>
    [AvaloniaTheory]
    [InlineData("A.kt", "kotlin", "val s = \"\"\"abc\"\"\"")]  // raw string
    [InlineData("A.kt", "kotlin", "val s = \"a $b\"")]
    [InlineData("A.kt", "kotlin", "// it's a 'quote'")]
    [InlineData("A.scala", "scala", "val s = \"\"\"abc\"\"\"")]
    [InlineData("a.toml", "toml", "s = \"\"\"abc\"\"\"")]        // multi-line basic string
    [InlineData("a.toml", "toml", "s = '''abc'''")]              // multi-line literal string
    [InlineData("a.toml", "toml", "s = \"a b\"")]
    [InlineData("a.toml", "toml", "p = 'C:\\x'")]
    [InlineData("default.nix", "nix", "s = ''abc''")]             // indented string
    [InlineData("default.nix", "nix", "s = \"abc\"")]
    [InlineData("a.ex", "elixir", "@doc \"\"\"abc\"\"\"")]       // heredoc
    [InlineData("schema.graphql", "graphql", "\"\"\"Doc\"\"\" type A")] // block string description
    [InlineData("a.zig", "zig", "const c = 'a';")]
    [InlineData("a.erl", "erlang", "X = 'atom', Y = \"s\".")]
    [InlineData("a.proto", "protobuf", "import \"a.proto\";")]
    public async Task Typing_a_string_character_by_character_gives_exactly_what_was_typed(string file, string lang, string typed)
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, file, "", lang);
        Type(view, typed);
        Assert.Equal(typed, Text(view));
    }

    [AvaloniaFact]
    public async Task Kotlin_and_Scala_doc_comments_type_exactly_like_TypeScript()
    {
        // The /** → " */" pair is VS Code's (Java, TypeScript, C#): whatever the editor does with it there, it does the same for KDoc / Scaladoc.
        using var host = ViewHost.Create();
        var ts = await OpenAsync(host, "a.ts", "", "typescript");
        Type(ts, "/** x */");
        foreach (var (file, lang) in new[] { ("A.kt", "kotlin"), ("A.scala", "scala") })
        {
            var view = await OpenAsync(host, file, "", lang);
            Type(view, "/** x */");
            Assert.Equal(Text(ts), Text(view));
        }
    }

    [AvaloniaTheory]
    [InlineData("A.kt", "kotlin", "(", "(foo)")]
    [InlineData("a.zig", "zig", "[", "[foo]")]
    [InlineData("a.toml", "toml", "\"", "\"foo\"")]
    [InlineData("default.nix", "nix", "{", "{foo}")]
    [InlineData("A.hs", "haskell", "`", "`foo`")] // infix application
    [InlineData("a.ex", "elixir", "[", "[foo]")]
    [InlineData("a.ml", "ocaml", "\"", "\"foo\"")]
    [InlineData("a.rst", "restructuredtext", "*", "*foo*")] // emphasis
    public async Task Typing_an_opener_over_a_selection_surrounds_it(string file, string lang, string typed, string expected)
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, file, "foo", lang);
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 3));
        Type(view, typed);
        Assert.Equal(expected, Text(view));
    }

    [AvaloniaTheory]
    [InlineData("A.kt", "kotlin", "fun f() {\n    val a = 1\n}", "// fun f() {\n//     val a = 1\n// }")]
    [InlineData("a.zig", "zig", "fn f() void {\n    return;\n}", "// fn f() void {\n//     return;\n// }")]
    [InlineData("default.nix", "nix", "{\n  a = 1;\n}", "# {\n#   a = 1;\n# }")]
    [InlineData("A.hs", "haskell", "main = do\n  pure ()", "-- main = do\n--   pure ()")]
    [InlineData("a.erl", "erlang", "f() ->\n    ok.", "% f() ->\n%     ok.")]
    [InlineData("a.proto", "protobuf", "message A {\n  int32 x = 1;\n}", "// message A {\n//   int32 x = 1;\n// }")]
    public async Task Toggle_comment_over_several_lines_round_trips(string file, string lang, string text, string commented)
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, file, text, lang);
        var lines = text.Split('\n');
        view.Controller.SetSelection(TextRange.FromLines(0, 0, lines.Length - 1, lines[^1].Length));
        await CommentLineAsync(host);
        Assert.Equal(commented, Text(view));
        await CommentLineAsync(host);
        Assert.Equal(text, Text(view));
    }

    [AvaloniaTheory]
    [InlineData("A.kt", "kotlin", "fun f() {", "fun f() {\n    \n}")]
    [InlineData("a.toml", "toml", "deps = [", "deps = [\n    \n]")]
    [InlineData("default.nix", "nix", "{", "{\n    \n}")]
    [InlineData("A.hs", "haskell", "f = (", "f = (\n    \n)")]
    [InlineData("a.ex", "elixir", "%{", "%{\n    \n}")]
    [InlineData("a.ml", "ocaml", "let r = {", "let r = {\n    \n}")]
    [InlineData("a.ml", "ocaml", "let f x =", "let f x =\n    ")]
    [InlineData("A.hs", "haskell", "f x = case x of", "f x = case x of\n    ")]
    [InlineData("a.ex", "elixir", "case x do", "case x do\n    ")]
    [InlineData("A.scala", "scala", "def f =", "def f =\n    ")] // Scala 3 braceless body
    public async Task Enter_after_an_opener_indents_the_new_line(string file, string lang, string typed, string afterEnter)
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, file, "", lang);
        Type(view, typed);
        view.TextArea.PerformTextInput("\n");
        Assert.Equal(afterEnter, Text(view));
    }

    [AvaloniaFact]
    public async Task CMake_indents_command_blocks_and_endif_snaps_back()
    {
        using var host = ViewHost.Create();
        var view = await OpenAsync(host, "CMakeLists.txt", "if(WIN32)", "cmake");
        view.Controller.SetSelection(TextRange.At(0, 9));
        view.TextArea.PerformTextInput("\n");
        Type(view, "add_definitions(-DW)");
        view.TextArea.PerformTextInput("\n");
        Type(view, "endif(");
        Assert.Equal("if(WIN32)\n    add_definitions(-DW)\nendif()", Text(view));
    }
}
