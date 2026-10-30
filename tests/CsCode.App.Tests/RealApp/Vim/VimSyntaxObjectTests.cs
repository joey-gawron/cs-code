using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// T33 in the REAL app: Vim's syntax text objects (<c>af if ac ic aa ia</c>) and motions (<c>]m [m ]M</c>) on tree-sitter documents — Kotlin and
/// TOML (tree-sitter by default) and Rust opted in — through real key events, the real adapter and the bundled nvim-treesitter-textobjects
/// queries answered on the syntax lane while the UI thread goes on (keys typed meanwhile run after the answer); on a TextMate document they do
/// nothing and say so.
/// </summary>
public class VimSyntaxObjectTests
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    private const string Kotlin = """
        class Greeter(val name: String) {
            fun hello(greeting: String, times: Int): String {
                return greeting + name
            }

            fun bye() = "bye"
        }

        """;

    private static async Task<VimApp> StartOnTreeSitterAsync(string text, string file, string? settings = null)
    {
        Assert.SkipUnless(TsRuntime.Load(), "libtree-sitter is not available on this machine: " + TsRuntime.FailureReason);
        var app = await VimApp.StartAsync(text, file, settings: settings is null ? Quiet : Quiet + ",\n" + settings);
        if (!await RealAppHost.WaitFor(() => app.View.SyntaxEngine == "tree-sitter", 20_000))
        {
            var engine = app.View.SyntaxEngine;
            await app.DisposeAsync();
            Assert.Skip($"{file} is not on tree-sitter here ({engine}): no bundled grammar (tools/treesitter/build-grammars.sh)");
        }
        await DevScene.SettleAsync(5);
        return app;
    }

    private static async Task WaitForText(VimApp app, string expected)
    {
        if (!await RealAppHost.WaitFor(() => app.Text == expected, 15_000)) Assert.Equal(expected, app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Kotlin_functions_parameters_and_classes_with_operators_counts_Visual_mode_and_dot()
    {
        await using var app = await StartOnTreeSitterAsync(Kotlin, "Greeter.kt");
        app.SetCaret(2, 12);                                   // in "return greeting + name"
        app.Keys("daf");
        await WaitForText(app, "class Greeter(val name: String) {\n\n    fun bye() = \"bye\"\n}\n");
        Assert.False(app.Engine.IsAwaitingSyntax);
        app.Keys("u");
        Assert.Equal(Kotlin, app.Text);

        app.SetCaret(1, 33);                                   // in "times"
        app.Keys("ciacount<Esc>");                             // Kotlin's parameter.inner is the name
        await WaitForText(app, Kotlin.Replace("times: Int", "count: Int", StringComparison.Ordinal));
        app.Keys("u");
        await WaitForText(app, Kotlin);

        app.SetCaret(1, 18);                                   // in "greeting": Kotlin's parameter.outer is the declaration (commas only for call arguments)
        app.Keys("daa");
        await WaitForText(app, Kotlin.Replace("greeting: String", "", StringComparison.Ordinal));
        app.Keys("u");
        await WaitForText(app, Kotlin);

        app.SetCaret(2, 12);
        app.Keys("vaf");                                       // Visual: the function, then the class around it
        Assert.True(await RealAppHost.WaitFor(() => app.Engine.Mode == VimMode.Visual && app.View.Modal.Controller.Selections[0].Start == new TextPosition(1, 4)));
        app.Keys("ac");
        Assert.True(await RealAppHost.WaitFor(() => app.View.Modal.Controller.Selections[0].Start == new TextPosition(0, 0)));
        app.Keys("y");
        Assert.True(await RealAppHost.WaitFor(() => app.Engine.Mode == VimMode.Normal));

        app.SetCaret(0, 0);
        app.Keys("]m");                                        // Kotlin's queries count the primary constructor as a function
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(0, 13)), app.Caret.ToString());
        app.Keys("]m");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(1, 4)), app.Caret.ToString());
        app.Keys("]m");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(5, 4)), app.Caret.ToString());
        app.Keys("[m");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(1, 4)), app.Caret.ToString());
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Keys_typed_while_the_tree_answers_run_after_the_text_object_in_order()
    {
        await using var app = await StartOnTreeSitterAsync(Kotlin, "Greeter.kt");
        app.SetCaret(2, 12);
        app.Keys("dafOx<Esc>");                                // one burst: the O, x and Esc wait for daf's answer
        await WaitForText(app, "class Greeter(val name: String) {\nx\n\n    fun bye() = \"bye\"\n}\n");
        Assert.Equal(VimMode.Normal, app.Engine.Mode);
        app.Keys("uu");
        await WaitForText(app, Kotlin);
        app.SetCaret(5, 10);                                   // in bye, a one-line function: its indentation stays (as in Vim)
        app.Keys("daf");
        await WaitForText(app, "class Greeter(val name: String) {\n    fun hello(greeting: String, times: Int): String {\n        return greeting + name\n    }\n\n    \n}\n");
        app.SetCaret(2, 12);
        app.Keys(".");                                         // dot repeats daf (a request of its own) on hello
        await WaitForText(app, "class Greeter(val name: String) {\n\n    \n}\n");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task TOML_arguments_are_its_pairs_and_array_items()
    {
        const string toml = "[package]\nname = \"demo\"\nkeywords = [\"a\", \"b\"]\n";
        await using var app = await StartOnTreeSitterAsync(toml, "Cargo.toml");
        app.SetCaret(2, 17);                                   // in "b"
        app.Keys("daa");
        await WaitForText(app, "[package]\nname = \"demo\"\nkeywords = [\"a\"]\n");
        app.SetCaret(1, 2);
        app.Keys("dia");                                       // the pair
        await WaitForText(app, "[package]\n\nkeywords = [\"a\"]\n");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Rust_opted_into_tree_sitter_has_its_functions_and_the_end_motion()
    {
        const string rust = "fn add(a: i32, b: i32) -> i32 {\n    a + b\n}\n\nfn main() {\n    let x = add(1, 2);\n}\n";
        await using var app = await StartOnTreeSitterAsync(rust, "lib.rs", "\"[rust]\": { \"editor.syntaxEngine\": \"treesitter\" }");
        app.SetCaret(5, 19);                                   // on the "2" of "add(1, 2)"
        app.Keys("cia9<Esc>");
        await WaitForText(app, rust.Replace("add(1, 2)", "add(1, 9)", StringComparison.Ordinal));
        app.SetCaret(0, 0);
        app.Keys("]M");                                        // the end of add
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(2, 0)), app.Caret.ToString());
        app.SetCaret(1, 6);                                    // in add's body (on its closing brace the next function's would be taken)
        app.Keys("dif");
        Assert.True(await RealAppHost.WaitFor(() => !app.Text.Contains("a + b", StringComparison.Ordinal)), app.Text);
        Assert.Contains("let x = add(1, 9);", app.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task On_a_TextMate_document_they_do_nothing_and_say_so()
    {
        const string cs = "class C\n{\n    void M(int a, int b) { }\n}\n";
        await using var app = await VimApp.StartAsync(cs, "a.cs", settings: Quiet);
        Assert.True(await RealAppHost.WaitFor(() => app.View.SyntaxEngine == "TextMate"));
        app.SetCaret(2, 12);
        await app.KeysAsync("daf");
        Assert.Equal(cs, app.Text);
        Assert.Equal(VimMode.Normal, app.Engine.Mode);
        Assert.Contains("No syntax tree", app.CommandLineText, StringComparison.Ordinal);
        await app.KeysAsync("3]m");
        Assert.Equal(new TextPosition(2, 12), app.Caret);
        Assert.False(app.Engine.IsAwaitingSyntax);
    }
}
