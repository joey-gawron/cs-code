using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Languages;
using CsCode.Editor.Markdown;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The pure models behind the language-feature UI: fuzzy matching, completion ordering, Markdown, folding.</summary>
public sealed class FeatureModelTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly LanguageService _languages;

    public FeatureModelTests() => _languages = new LanguageService(_host.Services);

    public void Dispose()
    {
        _languages.Dispose();
        _host.Dispose();
    }

    // ---------- fuzzy matcher ----------

    [Theory]
    [InlineData("cn", "Console", true)]
    [InlineData("con", "Console", true)]
    [InlineData("CON", "Console", true)]
    [InlineData("wl", "WriteLine", true)]
    [InlineData("xyz", "Console", false)]
    [InlineData("noc", "Console", false)]       // order matters
    [InlineData("", "anything", true)]
    [InlineData("toolong", "tool", false)]
    public void Fuzzy_matching_requires_an_ordered_subsequence(string pattern, string text, bool matches) =>
        Assert.Equal(matches, FuzzyMatcher.Match(pattern, text) is not null);

    [Fact]
    public void Fuzzy_positions_point_at_the_tightest_match_for_highlighting()
    {
        var m = FuzzyMatcher.Match("wl", "WriteLine")!.Value;
        Assert.Equal([0, 5], m.Positions);                       // W… L: word starts
        var prefix = FuzzyMatcher.Match("Wri", "WriteLine")!.Value;
        Assert.Equal([0, 1, 2], prefix.Positions);
    }

    [Fact]
    public void Prefix_and_word_start_matches_outrank_scattered_ones()
    {
        int S(string p, string t) => FuzzyMatcher.Match(p, t)!.Value.Score;
        Assert.True(S("con", "Console") > S("con", "Reconnect"));       // prefix beats middle-of-word
        Assert.True(S("wl", "WriteLine") > S("wl", "Showell"));         // camel humps beat accidental letters
        Assert.True(S("Con", "Console") > S("con", "Console"));         // exact case is a small bonus
        Assert.True(S("get", "get") > S("get", "getAllTheThingsFromTheServer"));
    }

    // ---------- completion filter/ordering ----------

    private static CompletionEntry E(string label, string? sort = null, bool preselect = false, string? filter = null, int order = 0) =>
        new(new CompletionItem { Label = label, SortText = sort, Preselect = preselect, FilterText = filter }, new NullProvider(), order);

    private sealed class NullProvider : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => [];
        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument d, TextPosition p, CompletionContext c, CancellationToken ct) => ValueTask.FromResult<CompletionList?>(null);
    }

    [Fact]
    public void Empty_prefix_shows_everything_in_sortText_order()
    {
        var f = CompletionFilter.Filter([E("b", "2"), E("a", "3"), E("c", "1")], "");
        Assert.Equal(["c", "b", "a"], f.Select(x => x.Entry.Item.Label));
    }

    [Fact]
    public void Filtering_scores_then_preselect_then_sortText_then_label()
    {
        var entries = new[] { E("Console"), E("Convert"), E("Count"), E("Context", preselect: true), E("xcon") };
        var f = CompletionFilter.Filter(entries, "con").Select(x => x.Entry.Item.Label).ToList();
        Assert.Equal(5, f.Count);                                            // "con" is a subsequence of every label, incl. C-o-u-N-t
        Assert.Equal("Context", f[0]);                                       // equal prefix scores: preselect wins
        Assert.True(f.IndexOf("Console") < f.IndexOf("Count"), "a contiguous prefix beats a scattered match");
        Assert.True(f.IndexOf("Console") < f.IndexOf("xcon"), "a prefix match beats a late match");
    }

    [Fact]
    public void FilterText_is_matched_instead_of_the_label()
    {
        var f = CompletionFilter.Filter([E("Label shown", filter: "internalname")], "intern");
        Assert.Single(f);
        Assert.Empty(CompletionFilter.Filter([E("Label shown", filter: "internalname")], "shown"));
    }

    [Fact]
    public void Filtering_caps_the_shown_list_and_is_cancellable()
    {
        var many = Enumerable.Range(0, 50_000).Select(i => E("item" + i)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = CompletionFilter.Filter(many, "it");
        Assert.Equal(CompletionFilter.MaxShown, f.Count);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"filtering 50k items took {sw.ElapsedMilliseconds} ms");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompletionFilter.Filter(many, "it", cts.Token));
    }

    // ---------- markdown ----------

    [Fact]
    public void Markdown_blocks_paragraphs_headings_code_lists_quotes_rules()
    {
        var blocks = MarkdownParser.Parse("# Title\n\nSome *text*\nwrapped\n\n```csharp\nint x;\n```\n\n- one\n- two\n\n1. a\n2. b\n\n> quoted\n\n---\n");
        Assert.IsType<MdHeading>(blocks[0]);
        Assert.Equal(1, ((MdHeading)blocks[0]).Level);
        var para = (MdParagraph)blocks[1];
        Assert.Equal("Some text wrapped", MarkdownParser.PlainText(para.Inlines));
        var code = (MdCodeBlock)blocks[2];
        Assert.Equal(("csharp", "int x;"), (code.Language, code.Text));
        Assert.Equal(["one", "two"], ((MdList)blocks[3]).Items.Select(i => MarkdownParser.PlainText(i)));
        Assert.True(((MdList)blocks[4]).Ordered);
        Assert.Equal("quoted", MarkdownParser.PlainText(((MdParagraph)((MdQuote)blocks[5]).Blocks[0]).Inlines));
        Assert.IsType<MdRule>(blocks[6]);
    }

    [Fact]
    public void Markdown_inlines_code_bold_italic_links_autolinks_escapes()
    {
        var inl = MarkdownParser.ParseInlines("Use `foo()` for **bold**, *italic* and [docs](https://x.dev/a) or https://y.dev. Cost: 5\\*3 snake_case_name");
        Assert.Contains(inl, i => i is MdCodeSpan { Text: "foo()" });
        Assert.Contains(inl, i => i is MdStrong);
        Assert.Contains(inl, i => i is MdEmphasis);
        var links = inl.OfType<MdLink>().ToList();
        Assert.Equal(["https://x.dev/a", "https://y.dev"], links.Select(l => l.Url));
        var text = MarkdownParser.PlainText(inl);
        Assert.Contains("5*3", text);                       // escaped asterisk is literal
        Assert.Contains("snake_case_name", text);           // underscores inside words are not emphasis
    }

    [Fact]
    public void Markdown_hard_breaks_and_html_br_and_tags()
    {
        var inl = MarkdownParser.ParseInlines("a<br>b <b>x</b>");
        Assert.Equal("a\nb x", MarkdownParser.PlainText(inl));
        var blocks = MarkdownParser.Parse("line one  \nline two");
        Assert.Equal("line one\nline two", MarkdownParser.PlainText(((MdParagraph)blocks[0]).Inlines));
    }

    [Theory]
    [InlineData("")]
    [InlineData("```\nunterminated fence")]
    [InlineData("**unterminated bold")]
    [InlineData("[no close](")]
    [InlineData("`backtick")]
    [InlineData("> > > deep")]
    [InlineData("- \n-\n- ")]
    public void Markdown_never_throws_on_malformed_input(string input) => Assert.NotNull(MarkdownParser.Parse(input));

    [Fact]
    public void Markdown_input_is_capped()
    {
        var blocks = MarkdownParser.Parse(new string('x', MarkdownParser.MaxLength * 2));
        Assert.Equal(MarkdownParser.MaxLength, MarkdownParser.PlainText(((MdParagraph)blocks[0]).Inlines).Length);
    }

    // ---------- folding ----------

    private static InMemoryTextDocument Doc(string text) => new(new Uri("file:///x"), "plaintext", text);

    [Fact]
    public void Indentation_folding_finds_nested_blocks_and_ignores_blank_lines()
    {
        var doc = Doc("a\n  b\n    c\n\n    d\n  e\nf\n");
        var folds = FoldComputer.Compute(doc, new LanguageConfiguration(), 4);
        Assert.Contains(new FoldRange(0, 5), folds);   // a … e
        Assert.Contains(new FoldRange(1, 4), folds);   // b … d (blank line inside, trailing blank excluded)
        Assert.DoesNotContain(folds, f => f.StartLine == 6);
    }

    [Fact]
    public void Marker_folding_pairs_nested_regions_and_wins_over_indentation_on_the_same_start_line()
    {
        var cfg = _languages.GetConfiguration("csharp");
        var doc = Doc("#region A\nint a;\n#region B\nint b;\n#endregion\n#endregion\nint c;\n");
        var folds = FoldComputer.Compute(doc, cfg, 4);
        Assert.Contains(new FoldRange(0, 5, FoldingRangeKind.Region), folds);
        Assert.Contains(new FoldRange(2, 4, FoldingRangeKind.Region), folds);
    }

    [Fact]
    public void Unbalanced_markers_and_bad_patterns_do_not_throw()
    {
        var cfg = new LanguageConfiguration { FoldingMarkersStart = "(", FoldingMarkersEnd = "^end" };
        Assert.NotNull(FoldComputer.Compute(Doc("a\n  b\n"), cfg, 4));
        var cfg2 = new LanguageConfiguration { FoldingMarkersStart = "^start", FoldingMarkersEnd = "^end" };
        Assert.Empty(FoldComputer.Compute(Doc("end\nstart\nend\nend\n"), cfg2, 4).Where(f => f.Kind == FoldingRangeKind.Region).Skip(1));
    }

    [Fact]
    public void Tabs_count_as_tabsize_columns_for_folding()
    {
        var folds = FoldComputer.Compute(Doc("a\n\tb\n    c\nd"), new LanguageConfiguration(), 4);
        Assert.Contains(new FoldRange(0, 2), folds);    // tab == 4 spaces: both deeper lines belong to 'a'
    }

    [Fact]
    public void Folding_is_capped_and_cancellable()
    {
        var text = string.Join("\n", Enumerable.Range(0, 20_000).SelectMany(i => new[] { "x" + i, "  y" }));
        var folds = FoldComputer.Compute(Doc(text), new LanguageConfiguration(), 4);
        Assert.Equal(FoldComputer.MaxRanges, folds.Count);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FoldComputer.Compute(Doc(text), new LanguageConfiguration(), 4, cts.Token));
    }

    [Fact]
    public void Folding_a_python_style_offside_file()
    {
        var src = "def f():\n    x = 1\n    if x:\n        y = 2\n\ndef g():\n    pass\n";
        var folds = FoldComputer.Compute(Doc(src), _languages.GetConfiguration("python"), 4);
        Assert.Contains(new FoldRange(0, 3), folds);
        Assert.Contains(new FoldRange(2, 3), folds);
        Assert.Contains(new FoldRange(5, 6), folds);
    }
}
