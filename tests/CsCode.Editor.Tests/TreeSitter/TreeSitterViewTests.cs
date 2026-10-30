using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// The engine inside the real editor (<c>AddCsCodeEditor</c>, headless views): chosen by <c>editor.syntaxEngine</c> (and the old
/// <c>editor.experimental.treeSitter</c> alias), on its own syntax lane, incremental while typing, switched live, falling back to TextMate,
/// coloured by the existing theme machinery.
/// </summary>
public class TreeSitterViewTests
{
    internal const string CSharp = """
        using System;

        namespace Demo
        {
            // A small program
            public class Program
            {
                public static int Main(string[] args)
                {
                    var message = "hello, " + args.Length;
                    Console.WriteLine(message);
                    return Compute(1, 2);
                }

                private static int Compute(int first, int second) => first + second;
            }
        }
        """;

    internal static int IdAt(CodeEditorControl view, int line, int character)
    {
        var tokens = view.Tokens!.Store.GetTokens(line);
        if (tokens is null) return -1;
        var id = tokens[1];
        for (var i = 0; i < tokens.Length / 2; i++)
            if (tokens[i * 2] <= character) id = tokens[i * 2 + 1];
        return id;
    }

    internal static string[] ScopesAt(CodeEditorControl view, int line, int character) =>
        IdAt(view, line, character) is var id and >= 0 ? view.Tokens!.Interner.Scopes(id) : [];

    internal static Task WaitForTreeSitter(CodeEditorControl view, int lines = 15) =>
        ViewHost.WaitFor(() => view.Tokens?.TreeSitter is { CompletedParses: > 0 } && view.Tokens.Store.HasTokens(0, lines), "tree-sitter tokens");

    [AvaloniaFact]
    public async Task With_the_setting_off_documents_stay_on_TextMate()
    {
        RequireGrammar("c_sharp");
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(CSharp, "Program.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 15, "TextMate tokens");
        Assert.Null(view.Tokens!.TreeSitter);
        Assert.True(Has(ScopesAt(view, 5, 11), "storage.type.class"), string.Join(" ", ScopesAt(view, 5, 11))); // "class" through TextMate's C# grammar
    }

    [AvaloniaFact]
    public async Task With_the_setting_on_the_document_is_coloured_by_tree_sitter_on_the_syntax_lane()
    {
        RequireGrammar("c_sharp");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        var (_, view, _) = await host.OpenAsync(CSharp, "Program.cs");
        await WaitForTreeSitter(view);
        var engine = view.Tokens!.TreeSitter!;
        // its own thread: neither the UI thread nor the editor worker (a grammar that hangs must not stop TextMate, folding, brackets…)
        Assert.Equal(host.Factory.Tokens.TreeSitter.Lane.Thread.ManagedThreadId, engine.LastSliceThreadId);
        Assert.NotEqual(host.Factory.Worker.Thread.ManagedThreadId, engine.LastSliceThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, engine.LastSliceThreadId);
        var classScopes = ScopesAt(view, 5, 11);
        Assert.Equal("source.cs", classScopes[0]);
        Assert.True(Has(classScopes, "storage.type"), string.Join(" ", classScopes));
        Assert.True(Has(ScopesAt(view, 4, 7), "comment"), string.Join(" ", ScopesAt(view, 4, 7)));
        // the existing token consumers read tree-sitter tokens like TextMate ones
        Assert.Equal(TokenKind.String, view.Tokens.KindAt(9, 29)); // inside "hello, "
        Assert.Equal(TokenKind.Comment, view.Tokens.KindAt(4, 10));
        Assert.Equal(TokenKind.Other, view.Tokens.KindAt(9, 17));
        // the setting is registered, so the Settings panel lists it
        Assert.Contains(host.Config.Properties, p => p.Key == TreeSitterService.SettingKey);
    }

    [AvaloniaFact]
    public async Task Typing_reparses_incrementally_and_republishes_only_the_lines_that_changed()
    {
        RequireGrammar("c_sharp");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        var big = CSharp + "\n" + string.Concat(Enumerable.Range(0, 400).Select(i => $"class Extra{i} {{ int F() => {i}; }}\n"));
        var (doc, view, _) = await host.OpenAsync(big, "Big.cs");
        await WaitForTreeSitter(view);
        var engine = view.Tokens!.TreeSitter!;
        await ViewHost.WaitFor(() => view.Tokens.IsQuiet && view.Tokens.Store.HasTokens(0, doc.LineCount - 1), "read-ahead done");
        var parsesBefore = engine.CompletedParses;
        var changed = new List<(int First, int Last)>();
        using var sub = view.Tokens.TokensChanged.Subscribe(r => changed.Add(r));

        // turn line 9 (`var message = ...`) into a comment by typing at its start
        view.Controller.SetSelection(CsCode.Core.Text.TextRange.At(9, 12));
        Type(view, "// ");
        await ViewHost.WaitFor(() => Has(ScopesAt(view, 9, 20), "comment"), "the edited line re-coloured as a comment");
        Assert.True(engine.CompletedParses > parsesBefore);
        Assert.True(engine.Syntax.ChangedRanges.Count < 10);
        // only lines near the edit were republished (no whole-document repaint): a small range around line 9
        Assert.NotEmpty(changed);
        Assert.All(changed, r => Assert.True(r.Last - r.First < 60, $"republished {r.First}-{r.Last}"));
        // still the same engine (incremental), and the tree is the tree of the edited text
        Assert.Same(engine, view.Tokens.TreeSitter);
        Assert.Contains("comment", engine.Syntax.TreeString()!, StringComparison.Ordinal);
        Assert.Equal(doc.Version, doc.Snapshot().Version);
    }

    [AvaloniaFact]
    public async Task The_setting_switches_engines_live_both_ways()
    {
        RequireGrammar("rust");
        using var host = ViewHost.Create();
        const string rust = "fn main() {\n    let s = \"x\"; // c\n}\n";
        var (_, view, _) = await host.OpenAsync(rust, "main.rs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 2, "TextMate tokens");
        Assert.Null(view.Tokens!.TreeSitter);

        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        await ViewHost.WaitFor(() => view.Tokens.TreeSitter is { CompletedParses: > 0 } && view.Tokens.Store.HasTokens(0, 2), "switch to tree-sitter");
        Assert.True(Has(ScopesAt(view, 0, 0), "storage.type.function"), string.Join(" ", ScopesAt(view, 0, 0)));

        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.Auto);
        await ViewHost.WaitFor(() => view.Tokens.TreeSitter is null && view.Tokens.Store.ValidLines >= 2, "back to TextMate");
        Assert.Contains("source.rust", ScopesAt(view, 0, 0));
    }

    [AvaloniaFact]
    public async Task A_language_section_enables_tree_sitter_for_that_language_only()
    {
        RequireGrammar("rust");
        using var host = ViewHost.Create();
        host.Config.Set("[rust]", new Dictionary<string, object> { [TreeSitterService.SettingKey] = TreeSitterService.TreeSitter });
        var (_, rs, _) = await host.OpenAsync("fn main() {}\n", "a.rs");
        var (_, cs, _) = await host.OpenAsync("class A {}\n", "a.cs");
        await ViewHost.WaitFor(() => rs.Tokens?.TreeSitter is { CompletedParses: > 0 }, "rust on tree-sitter");
        await ViewHost.WaitFor(() => cs.Tokens is { } t && t.Store.ValidLines >= 1, "C# tokens");
        Assert.Null(cs.Tokens!.TreeSitter);
    }

    [AvaloniaFact]
    public async Task Without_a_grammar_the_document_falls_back_to_TextMate()
    {
        RequireRuntime();
        using var host = ViewHost.Create();
        host.Factory.Tokens.TreeSitter.LocatorOverride = new GrammarLocator([], useNixStore: false);
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        var (_, view, _) = await host.OpenAsync("fn main() {}\n", "main.rs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 1, "TextMate tokens (fallback)");
        Assert.Null(view.Tokens!.TreeSitter);
        Assert.Contains("source.rust", ScopesAt(view, 0, 0));
    }

    [AvaloniaFact]
    public async Task A_theme_switch_recolours_tree_sitter_tokens_without_reparsing()
    {
        RequireGrammar("c_sharp");
        using var host = ViewHost.Create();
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        await host.Factory.Themes.SetCurrentAsync("vscode.dark-plus");
        var (_, view, _) = await host.OpenAsync(CSharp, "Program.cs");
        await WaitForTreeSitter(view);
        var engine = view.Tokens!.TreeSitter!;
        await ViewHost.WaitFor(() => view.Tokens.IsQuiet && view.Tokens.Store.HasTokens(0, 16), "all lines");
        var parses = engine.CompletedParses;
        var row = view.Tokens.Store.GetTokens(5);
        // the same scope stack resolves to each theme's own colour: `class` is Dark+ blue and Light+ dark blue
        var classId = IdAt(view, 5, 11);
        var dark = new TokenColorResolver(host.Factory.Themes.Current, host.Factory.Themes.GetColor("editor.foreground")).Resolve(view.Tokens.Interner.Scopes(classId));
        await host.Factory.Themes.SetCurrentAsync("vscode.light-plus");
        ViewHost.Pump();
        var light = new TokenColorResolver(host.Factory.Themes.Current, host.Factory.Themes.GetColor("editor.foreground")).Resolve(view.Tokens.Interner.Scopes(classId));
        Assert.NotEqual(dark.Foreground, light.Foreground);
        Assert.Equal(parses, engine.CompletedParses);
        Assert.Same(row, view.Tokens.Store.GetTokens(5));
    }

    [AvaloniaFact]
    public async Task Typing_while_a_big_document_is_still_parsing_cancels_the_stale_parse_and_costs_the_UI_thread_nothing_extra()
    {
        RequireGrammar("c_sharp");
        // ~1.2 MB: below the large-file threshold; the first parse takes many 6 ms slices, and typing lands in the middle of it
        var big = string.Concat(Enumerable.Range(0, 24_000).Select(i => $"class C{i} {{ int F{i}(int a) {{ return a * {i}; }} }}\n"));

        async Task<(List<double> Keystrokes, CodeEditorControl View, ViewHost Host)> TypeBurst(bool treeSitter)
        {
            var host = ViewHost.Create();
            host.Config.Set(TreeSitterService.SettingKey, treeSitter ? TreeSitterService.TreeSitter : TreeSitterService.TextMate);
            var (_, view, _) = await host.OpenAsync(big, treeSitter ? "HugeTs.cs" : "HugeTm.cs", 900, 400);
            var keystrokes = new List<double>();
            for (var i = 0; i < 70; i++)
            {
                var sw = Stopwatch.StartNew();
                Type(view, "x");
                if (i >= 10) keystrokes.Add(sw.Elapsed.TotalMilliseconds); // the first ones pay for JIT and the first layout
                ViewHost.Pump();
                await Task.Delay(2);
            }
            keystrokes.Sort();
            return (keystrokes, view, host);
        }

        var (textMate, _, tmHost) = await TypeBurst(treeSitter: false);
        tmHost.Dispose();
        var (treeSitter, view, host) = await TypeBurst(treeSitter: true);
        using var _ = host;
        await ViewHost.WaitFor(() => view.Tokens?.TreeSitter is { CompletedParses: > 0 } t && !t.Syntax.IsParsing && view.Tokens.Store.HasTokens(0, 10), "final tree", 60_000);
        var engine = view.Tokens!.TreeSitter!;
        // typing during the first parse made it stale: it was abandoned (not finished) and restarted from the new text
        Assert.True(engine.Syntax.AbandonedParses > 0, $"abandoned {engine.Syntax.AbandonedParses}, completed {engine.CompletedParses}");
        Assert.Equal(view.Document.Snapshot().Length, big.Length + 70);
        Assert.True(Has(ScopesAt(view, 1, 0), "storage.type"), string.Join(" ", ScopesAt(view, 1, 0)));
        // the keystroke path only queues the edit for the worker: tree-sitter never runs on the UI thread, and typing is not slower than on TextMate
        Assert.NotEqual(Environment.CurrentManagedThreadId, engine.LastSliceThreadId);
        double P(List<double> l, double q) => l[Math.Min(l.Count - 1, (int)(l.Count * q))];
        var numbers = $"keystroke p50/p95 tree-sitter {P(treeSitter, 0.5):F2}/{P(treeSitter, 0.95):F2} ms vs TextMate {P(textMate, 0.5):F2}/{P(textMate, 0.95):F2} ms";
        TestContext.Current.TestOutputHelper?.WriteLine(numbers + $" (load {TreeSitterBenchmarks.Load1():0.0})"); // T16: the numbers, also when it passes
        Assert.True(P(treeSitter, 0.95) <= P(textMate, 0.95) * 1.5 + 2, numbers);
    }
}
