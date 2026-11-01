using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Theming;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>Semantic highlighting: style resolution (VS Code's rules), the token store following edits, and the overlay in a real view.</summary>
public class SemanticTokenTests
{
    // ---- style resolution ----

    private static ColorTheme Theme(IReadOnlyDictionary<string, TokenColorRule>? semantic = null) => new("t", "t", ThemeKind.Dark,
        new Dictionary<string, Rgba>(),
        [
            new(["entity.name.type"], "#4EC9B0", null, null),
            new(["entity.name.function"], "#DCDCAA", null, null),
            new(["variable"], "#9CDCFE", null, null),
            new(["variable.other.constant"], "#4FC1FF", null, null),
            new(["keyword.control"], "#C586C0", null, "italic"),
        ], semantic);

    private static SemanticTokenStyleResolver Resolver(ColorTheme theme) => new(theme, new TokenColorResolver(theme, Rgba.Parse("#D4D4D4")));

    [Fact]
    public void Standard_types_fall_back_to_the_themes_textmate_rules_through_vs_codes_scope_map()
    {
        var r = Resolver(Theme());
        Assert.Equal(Rgba.Parse("#4EC9B0"), r.Resolve("class", [], "csharp")!.Foreground);   // class → entity.name.type.class ⊂ entity.name.type
        Assert.Equal(Rgba.Parse("#4EC9B0"), r.Resolve("struct", [], "csharp")!.Foreground);
        Assert.Equal(Rgba.Parse("#DCDCAA"), r.Resolve("method", [], "csharp")!.Foreground);  // method → entity.name.function.member
        Assert.Equal(Rgba.Parse("#9CDCFE"), r.Resolve("variable", [], "csharp")!.Foreground);
        Assert.Equal(Rgba.Parse("#9CDCFE"), r.Resolve("parameter", [], "csharp")!.Foreground); // variable.parameter ⊂ variable
        Assert.Equal(Rgba.Parse("#4FC1FF"), r.Resolve("variable", ["readonly"], "csharp")!.Foreground); // the type.modifier default wins (score 200)
        var keyword = r.Resolve("keyword", [], "csharp")!;
        Assert.Equal(true, keyword.Italic);  // a TextMate fontStyle sets all four flags
        Assert.Equal(false, keyword.Bold);
        Assert.Null(r.Resolve("comment", [], "csharp"));   // no rule for "comment" in this theme: the grammar's colour stays
        Assert.Null(r.Resolve("lifetime", [], "rust"));    // an unknown type with no super-type: nothing
        Assert.Equal(Rgba.Parse("#4EC9B0"), r.Resolve("builtinType", [], "rust")!.Foreground); // rust-analyzer's custom type → type
    }

    [Fact]
    public void Theme_semantic_rules_win_by_score_and_on_ties_with_language_and_wildcard_selectors()
    {
        var r = Resolver(Theme(new Dictionary<string, TokenColorRule>
        {
            ["class"] = new(["class"], "#FF0000", null, null),
            ["type"] = new(["type"], "#00FF00", null, null),
            ["*.declaration"] = new(["*.declaration"], null, null, "bold"),
            ["variable.readonly:java"] = new(["variable.readonly:java"], "#123456", null, null),
            ["property"] = new(["property"], null, null, "underline"),
        }));
        Assert.Equal(Rgba.Parse("#FF0000"), r.Resolve("class", [], "csharp")!.Foreground);   // exact type beats the class→type default on a tie: the theme wins
        Assert.Equal(Rgba.Parse("#00FF00"), r.Resolve("builtinType", [], "rust")!.Foreground); // "type" covers its sub-types: a tie with the default for "type", which the theme wins
        Assert.Equal(Rgba.Parse("#4EC9B0"), r.Resolve("struct", [], "csharp")!.Foreground);  // but the default for "struct" itself scores higher (100 > 99), as in VS Code
        var decl = r.Resolve("class", ["declaration"], "csharp")!;
        Assert.Equal(true, decl.Bold);
        Assert.Equal(Rgba.Parse("#FF0000"), decl.Foreground); // property by property: colour from "class", bold from "*.declaration"
        Assert.Equal(Rgba.Parse("#123456"), r.Resolve("variable", ["readonly"], "java")!.Foreground);
        Assert.Equal(Rgba.Parse("#4FC1FF"), r.Resolve("variable", ["readonly"], "csharp")!.Foreground); // the java rule does not apply
        var property = r.Resolve("property", [], "csharp")!;
        Assert.Equal(true, property.Underline);
        Assert.Equal(Rgba.Parse("#9CDCFE"), property.Foreground); // the colour from the default (variable.other.property ⊂ the theme's "variable"), the style from the theme
    }

    [Fact]
    public void Dark_plus_colours_the_usual_types()
    {
        var reader = new ColorThemeService.BuiltinThemeReader();
        var theme = VsCodeThemeLoader.LoadSync("dp", null, "vs-dark", reader.ReadSync("dark_plus.json")!, ColorThemeService.BuiltinPathPrefix + "dark_plus.json",
            (_, rel) => reader.ReadSync(rel.TrimStart('.', '/')) is { } t ? (ColorThemeService.BuiltinPathPrefix + rel.TrimStart('.', '/'), t) : null);
        var r = Resolver(theme);
        Assert.Equal(Rgba.Parse("#4EC9B0"), r.Resolve("class", [], "csharp")!.Foreground);
        Assert.Equal(Rgba.Parse("#9CDCFE"), r.Resolve("variable", [], "csharp")!.Foreground);
        Assert.Equal(Rgba.Parse("#DCDCAA"), r.Resolve("function", [], "csharp")!.Foreground);
        Assert.Equal(Rgba.Parse("#4FC1FF"), r.Resolve("variable", ["readonly"], "csharp")!.Foreground);
    }

    // ---- the store ----

    /// <summary>LSP encoding of absolute (line, start, length, type, modifiers) tokens.</summary>
    internal static uint[] Encode(params (int Line, int Start, int Length, int Type, int Mods)[] tokens)
    {
        var data = new List<uint>();
        int line = 0, start = 0;
        foreach (var t in tokens)
        {
            var dl = t.Line - line;
            data.AddRange([(uint)dl, (uint)(dl == 0 ? t.Start - start : t.Start), (uint)t.Length, (uint)t.Type, (uint)t.Mods]);
            line = t.Line;
            start = t.Start;
        }
        return [.. data];
    }

    private static (int, int)[] Row(SemanticTokenStore s, int line)
    {
        var row = s.Get(line);
        if (row is null) return [];
        return Enumerable.Range(0, row.Length / 4).Select(i => (row[i * 4], row[i * 4 + 1])).ToArray();
    }

    private static TextContentChange Change(int l1, int c1, int l2, int c2, string text) => new(TextRange.FromLines(l1, c1, l2, c2), 0, 0, text);

    [Fact]
    public void Tokens_follow_edits_on_their_line()
    {
        var s = new SemanticTokenStore();
        s.SetFull(Encode((0, 4, 3, 0, 0), (0, 10, 5, 1, 0), (1, 0, 2, 0, 0)), 3); // "    foo   bar__"
        Assert.Equal(3, s.TokenCount);
        s.Apply(Change(0, 0, 0, 0, "  "));                   // typing before: both shift
        Assert.Equal([(6, 3), (12, 5)], Row(s, 0));
        s.Apply(Change(0, 7, 0, 7, "x"));                    // inside "foo": it grows
        Assert.Equal([(6, 4), (13, 5)], Row(s, 0));
        s.Apply(Change(0, 10, 0, 10, "y"));                  // an identifier character at its end: it grows
        Assert.Equal([(6, 5), (14, 5)], Row(s, 0));
        s.Apply(Change(0, 11, 0, 11, "("));                  // punctuation at its end: it does not
        Assert.Equal([(6, 5), (15, 5)], Row(s, 0));
        s.Apply(Change(0, 7, 0, 9, ""));                     // deleting inside: it shrinks
        Assert.Equal([(6, 3), (13, 5)], Row(s, 0));
        s.Apply(Change(0, 8, 0, 14, "zz"));                  // a change that cuts both tokens drops them until the next answer
        Assert.Empty(Row(s, 0));
        Assert.Equal([(0, 2)], Row(s, 1));                   // other lines untouched
        Assert.Equal(1, s.TokenCount);
    }

    [Fact]
    public void Line_breaks_split_join_and_move_lines()
    {
        var s = new SemanticTokenStore();
        s.SetFull(Encode((0, 0, 3, 0, 0), (0, 8, 3, 1, 0), (1, 2, 2, 0, 0), (2, 0, 4, 0, 0)), 3);
        s.Apply(Change(0, 5, 0, 5, "\n  "));                // Enter between the tokens of line 0, with indentation
        Assert.Equal([(0, 3)], Row(s, 0));
        Assert.Equal([(5, 3)], Row(s, 1));                   // 8 - 5 + 2
        Assert.Equal([(2, 2)], Row(s, 2));                   // old line 1 moved down
        Assert.Equal([(0, 4)], Row(s, 3));
        s.Apply(Change(0, 3, 1, 2, ""));                     // join them back (delete "\n  " and the 2 spaces before)
        Assert.Equal([(0, 3), (6, 3)], Row(s, 0));          // 5 - 2 + 3
        Assert.Equal([(2, 2)], Row(s, 1));
        s.Apply(Change(0, 0, 0, 0, "a\r\nb\r\n"));           // CRLF counts once per break: line 0's tokens go two lines down
        Assert.Empty(Row(s, 0));
        Assert.Empty(Row(s, 1));
        Assert.Equal([(0, 3), (6, 3)], Row(s, 2));
        Assert.Equal(4, s.TokenCount);
        s.Apply(Change(1, 1, 3, 1, ""));                     // multi-line deletion: what it cuts goes, the rest of the last line joins
        Assert.Equal([(2, 2)], Row(s, 1));                   // (2, 2) of line 3 lands at 1 + (2 - 1)
        Assert.Equal([(0, 4)], Row(s, 2));                   // the line below moves up by two
        Assert.Equal(2, s.TokenCount);
    }

    [Fact]
    public void Range_answers_replace_their_lines_and_forget_far_ones_and_the_total_is_capped()
    {
        var s = new SemanticTokenStore();
        s.SetFull(Encode((0, 0, 1, 0, 0), (500, 0, 1, 0, 0), (900, 0, 1, 0, 0)), 1000);
        s.SetRange(Encode((450, 0, 2, 0, 0), (460, 1, 2, 0, 0)), 400, 600, 1000, keep: 100);
        Assert.Empty(Row(s, 0));            // more than 100 lines from the range: forgotten
        Assert.Empty(Row(s, 500));          // inside the range: replaced by the answer (which has none there)
        Assert.Empty(Row(s, 900));
        Assert.Equal([(0, 2)], Row(s, 450));
        Assert.Equal(2, s.TokenCount);

        var big = Encode([.. Enumerable.Range(0, SemanticTokenStore.MaxTokens + 10).Select(i => (i / 10, (i % 10) * 3, 2, 0, 0))]);
        s.SetFull(big, SemanticTokenStore.MaxTokens / 10 + 10);
        Assert.Equal(SemanticTokenStore.MaxTokens, s.TokenCount);
    }

    // ---- in a view ----

    internal sealed class FakeSemantic(SemanticTokensLegend legend, Func<ITextDocument, uint[]?> provide) : ISemanticTokensProvider
    {
        public int Calls;
        public TaskCompletionSource? Gate { get; set; }
        public SemanticTokensLegend Legend => legend;

        public async ValueTask<SemanticTokens?> ProvideSemanticTokensAsync(ITextDocument document, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var data = provide(document);
            if (Gate is { } g) await g.Task.WaitAsync(ct).ConfigureAwait(false);
            return data is null ? null : new SemanticTokens(data);
        }
    }

    private sealed class FakeSemanticRange(SemanticTokensLegend legend) : ISemanticTokensRangeProvider
    {
        public readonly List<TextRange> Asked = [];
        public SemanticTokensLegend Legend => legend;

        public ValueTask<SemanticTokens?> ProvideSemanticTokensRangeAsync(ITextDocument document, TextRange range, CancellationToken ct)
        {
            lock (Asked) Asked.Add(range);
            return ValueTask.FromResult<SemanticTokens?>(new SemanticTokens(Encode((range.Start.Line + 1, 0, 4, 0, 0))));
        }
    }

    internal static readonly SemanticTokensLegend Legend = new(["class", "variable", "function", "parameter"], ["declaration", "readonly"]);

    /// <summary>The foreground the view draws the character at <paramref name="offset"/> with.</summary>
    internal static Color? ForegroundAt(CodeEditorControl view, int offset)
    {
        var line = view.Editor.Document.GetLineByOffset(offset);
        var vl = view.TextView.GetOrConstructVisualLine(line);
        var column = vl.GetVisualColumn(offset - line.Offset);
        var element = vl.Elements.FirstOrDefault(e => e.VisualColumn <= column && column < e.VisualColumn + e.VisualLength);
        return (element?.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color;
    }

    private const string Code = "class Widget\n{\n    int count;\n    void Run() { count = 1; }\n}\n";

    [AvaloniaFact]
    public async Task Tokens_colour_over_the_grammar_follow_edits_without_flicker_and_the_setting_and_theme_decide()
    {
        using var host = ViewHost.Create();
        await host.Factory.Themes.SetCurrentAsync("vscode.dark-plus"); // semanticHighlighting: true
        // "count" on line 3 coloured as a readonly variable; "Widget" as a class declaration
        var provider = new FakeSemantic(Legend, _ => Encode((0, 6, 6, 0, 1), (3, 17, 5, 1, 2)));
        host.Factory.Services.Features.Register<ISemanticTokensProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<SemanticTokensFeature>()!;
        await ViewHost.WaitFor(() => feature.TokenCount == 2, "tokens applied");
        ViewHost.Pump();
        var countAt = doc.GetText().IndexOf("count = 1", StringComparison.Ordinal);
        var readonlyVariable = Color.Parse("#4FC1FF");
        Assert.Equal(readonlyVariable, ForegroundAt(view, countAt));
        var token = Assert.Single(feature.TokensOf(3));
        Assert.Equal((17, 5, "variable", "readonly"), (token.Start, token.Length, token.Type, string.Join(",", token.Modifiers)));

        var calls = provider.Calls;
        host.Config.Set("editor.tabSize", 2);
        await Task.Delay((int)SemanticTokensFeature.Delay.TotalMilliseconds + 200);
        Assert.Equal(calls, provider.Calls); // another setting changing restyles, it does not ask the server again
        Assert.Equal(readonlyVariable, ForegroundAt(view, countAt));

        provider.Gate = new TaskCompletionSource(); // the server is slow now
        view.Controller.SetSelection(TextRange.At(3, 4));
        Type(view, "  ");
        ViewHost.Pump();
        Assert.Equal(readonlyVariable, ForegroundAt(view, countAt + 2)); // shifted with the text at once: no flicker back to the grammar's colour
        Assert.Equal(19, feature.TokensOf(3)[0].Start);
        provider.Gate.SetResult();

        host.Config.Set(LanguageRenderSettings.SemanticHighlightingKey, false);
        await ViewHost.WaitFor(() => feature.TokenCount == 0, "turned off");
        ViewHost.Pump();
        Assert.NotEqual(readonlyVariable, ForegroundAt(view, countAt + 2));

        host.Config.Set(LanguageRenderSettings.SemanticHighlightingKey, "configuredByTheme");
        await ViewHost.WaitFor(() => feature.TokenCount == 2, "back on: Dark+ asks for it");
        using var noFlag = host.Factory.Themes.Register(host.Factory.Themes.Current with { Id = "test.no-flag", Label = "No flag", SemanticHighlighting = false });
        await host.Factory.Themes.SetCurrentAsync("test.no-flag"); // a theme without the flag
        await ViewHost.WaitFor(() => feature.TokenCount == 0, "configuredByTheme: off for a theme that does not ask for it");
        host.Config.Set(LanguageRenderSettings.SemanticHighlightingKey, true);
        await ViewHost.WaitFor(() => feature.TokenCount == 2, "true: on whatever the theme");
    }

    [AvaloniaFact]
    public async Task A_stale_answer_is_dropped_and_a_provider_change_asks_again()
    {
        using var host = ViewHost.Create();
        await host.Factory.Themes.SetCurrentAsync("vscode.dark-plus");
        static int LineOf(ITextDocument d, string prefix) => d.GetText().Split('\n').ToList().FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Func<ITextDocument, uint[]> answer = d => Encode((LineOf(d, "class"), 6, 6, 0, 0));
        var provider = new FakeSemantic(Legend, d => answer(d)) { Gate = new TaskCompletionSource() };
        host.Factory.Services.Features.Register<ISemanticTokensProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync(Code, "a.cs");
        var feature = view.Feature<SemanticTokensFeature>()!;
        await ViewHost.WaitFor(() => provider.Calls >= 1, "asked");
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "// x\n")]); // the text moves on while the server works
        provider.Gate.SetResult();
        await Task.Delay(100);
        ViewHost.Pump();
        Assert.Equal(0, feature.TokenCount); // the answer was for the old text: not applied
        await ViewHost.WaitFor(() => feature.TokenCount == 1, "the next request (after the edit) applies");
        Assert.Single(feature.TokensOf(1)); // on the class's new line

        var calls = provider.Calls;
        answer = d => Encode((LineOf(d, "class"), 6, 6, 0, 0), (4, 4, 4, 3, 0));
        using var _ = host.Factory.Services.Features.Register<ISemanticTokensProvider>(DocumentSelector.ForLanguages(["nothing"]), new FakeSemantic(Legend, _ => null));
        await ViewHost.WaitFor(() => provider.Calls > calls && feature.TokenCount == 2, "workspace/semanticTokens/refresh re-registers: asked again");
    }

    [AvaloniaFact]
    public async Task A_big_document_asks_for_the_viewport_through_the_range_provider()
    {
        using var host = ViewHost.Create();
        await host.Factory.Themes.SetCurrentAsync("vscode.dark-plus");
        var full = new FakeSemantic(Legend, _ => Encode((0, 0, 4, 0, 0)));
        var range = new FakeSemanticRange(Legend);
        host.Factory.Services.Features.Register<ISemanticTokensProvider>(DocumentSelector.All, full);
        host.Factory.Services.Features.Register<ISemanticTokensRangeProvider>(DocumentSelector.All, range);
        var text = string.Concat(Enumerable.Range(0, SemanticTokensFeature.RangeThresholdLines + 1000).Select(i => $"var v{i} = {i};\n"));
        var (_, view, _) = await host.OpenAsync(text, "big.cs");
        var feature = view.Feature<SemanticTokensFeature>()!;
        await ViewHost.WaitFor(() => feature.TokenCount > 0, "range tokens applied");
        Assert.Single(feature.TokensOf(1));
        Assert.True(feature.RangeMode);
        Assert.Equal(0, full.Calls);
        TextRange first;
        lock (range.Asked) first = range.Asked[0];
        Assert.Equal(0, first.Start.Line);
        Assert.True(first.End.Line < 300, $"the viewport and a margin, not the document: {first}");

        view.Controller.Reveal(TextRange.At(4000, 0), center: true);
        await ViewHost.WaitFor(() => { lock (range.Asked) return range.Asked.Any(r => r.Start.Line > 3000 && r.End.Line < 5000); }, "scrolling asks for the new viewport");
        await ViewHost.WaitFor(() => feature.TokensOf(1).Count == 0, "the new answer applied");
        Assert.Empty(feature.TokensOf(1)); // the first answer's token, far from the viewport now: forgotten
    }
}
