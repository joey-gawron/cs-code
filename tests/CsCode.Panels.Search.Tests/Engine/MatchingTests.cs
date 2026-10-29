using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

public class MatchingTests : IDisposable
{
    private readonly SearchHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<SearchOutcomeData> Run(string content, SearchQuery q, string name = "a.txt")
    {
        _h.Write(name, content);
        return await _h.RunAsync(q);
    }

    private static SearchMatch Only(SearchOutcomeData r) => Assert.Single(Assert.Single(r.Files).Matches);

    // ----------------------------------------------------------------------------------------- literal

    [Fact]
    public async Task Literal_match_reports_zero_based_line_and_column()
    {
        var r = await Run("alpha\n  beta needle gamma\nomega", new SearchQuery("needle"));
        var m = Only(r);
        Assert.Equal(new TextRange(new(1, 7), new(1, 13)), m.Range);
        Assert.Equal("needle", m.MatchedText);
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
    }

    [Fact]
    public async Task Literal_finds_every_occurrence_on_a_line_in_order()
    {
        var r = await Run("ab ab ab", new SearchQuery("ab"));
        Assert.Equal([0, 3, 6], r.Files[0].Matches.Select(m => m.Column));
    }

    [Fact]
    public async Task Literal_is_case_insensitive_by_default()
    {
        var r = await Run("Needle NEEDLE needle", new SearchQuery("nEeDlE"));
        Assert.Equal(3, r.MatchCount);
    }

    [Fact]
    public async Task Match_case_distinguishes_case()
    {
        var r = await Run("Needle NEEDLE needle", new SearchQuery("needle", MatchCase: true));
        Assert.Equal([14], r.Files[0].Matches.Select(m => m.Column));
    }

    [Fact]
    public async Task Whole_word_rejects_substrings_but_accepts_line_edges()
    {
        var r = await Run("cat\ncatalog\nthe cat sat\nconcat\ncat", new SearchQuery("cat", WholeWord: true));
        Assert.Equal([0, 2, 4], r.Files[0].Matches.Select(m => m.Line));
    }

    [Fact]
    public async Task Whole_word_treats_underscore_and_digits_as_word_characters()
    {
        var r = await Run("foo_bar foo1 foo foo-bar", new SearchQuery("foo", WholeWord: true));
        Assert.Equal([13, 17], r.Files[0].Matches.Select(m => m.Column));
    }

    [Fact]
    public async Task Whole_word_with_a_non_word_edge_only_checks_the_word_edge()
    {
        // ".foo" starts with '.', so only the right edge is a boundary check (like VS Code)
        var r = await Run("x.foo y.foobar", new SearchQuery(".foo", WholeWord: true));
        Assert.Equal([1], r.Files[0].Matches.Select(m => m.Column));
    }

    [Fact]
    public async Task Unicode_case_insensitive_and_whole_word()
    {
        var r = await Run("Éclair éclair ÉCLAIRS", new SearchQuery("éclair", WholeWord: true));
        Assert.Equal([0, 7], r.Files[0].Matches.Select(m => m.Column));
    }

    [Fact]
    public async Task Columns_count_utf16_code_units_so_surrogate_pairs_take_two()
    {
        var r = await Run("a😀b", new SearchQuery("b"));
        Assert.Equal(3, Only(r).Column); // 'a' + two code units of the emoji
    }

    [Fact]
    public async Task Cjk_text_is_matched()
    {
        var r = await Run("你好，世界\n再见", new SearchQuery("世界"));
        Assert.Equal(new TextPosition(0, 3), Only(r).Range.Start);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Line_numbers_follow_every_terminator_style(string eol)
    {
        var r = await Run($"one{eol}two{eol}three{eol}needle{eol}", new SearchQuery("needle"));
        Assert.Equal(3, Only(r).Line);
        Assert.Equal(eol, r.Files[0].LineEnding);
    }

    [Fact]
    public async Task Last_line_without_terminator_is_searched()
    {
        var r = await Run("a\nb\nneedle", new SearchQuery("needle"));
        Assert.Equal(2, Only(r).Line);
    }

    [Fact]
    public async Task No_match_yields_no_file_entry()
    {
        var r = await Run("nothing here", new SearchQuery("needle"));
        Assert.Empty(r.Files);
        Assert.Equal(1, r.Stats.FilesScanned);
    }

    [Fact]
    public async Task Matches_within_a_file_are_in_document_order()
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => i % 3 == 0 ? $"line {i} x x" : $"line {i}"));
        var r = await Run(text, new SearchQuery("x"));
        var pos = r.Files[0].Matches.Select(m => m.Range.Start).ToList();
        Assert.Equal(pos.OrderBy(p => p).ToList(), pos);
        Assert.Equal(134, pos.Count);
    }

    // ------------------------------------------------------------------------------------------ regex

    [Fact]
    public async Task Regex_matches_with_groups_and_reports_the_whole_match_range()
    {
        var r = await Run("key=value; other=thing", new SearchQuery(@"(\w+)=(\w+)", IsRegex: true));
        Assert.Equal(["key=value", "other=thing"], r.Files[0].Matches.Select(m => m.MatchedText));
    }

    [Fact]
    public async Task Regex_is_case_insensitive_unless_match_case()
    {
        var text = "Foo FOO foo";
        Assert.Equal(3, (await Run(text, new SearchQuery("f.o", IsRegex: true))).MatchCount);
        var strict = await Run(text, new SearchQuery("f.o", IsRegex: true, MatchCase: true), "b.txt");
        Assert.Single(strict.File("b.txt").Matches); // (a.txt from the first run is still in the folder)
    }

    [Fact]
    public async Task Regex_whole_word_wraps_the_pattern_in_word_boundaries()
    {
        var r = await Run("cat concat cats cat", new SearchQuery("cat|dog", IsRegex: true, WholeWord: true));
        Assert.Equal([0, 16], r.Files[0].Matches.Select(m => m.Column));
    }

    [Fact]
    public async Task Regex_anchors_apply_per_line_in_line_mode()
    {
        var r = await Run("start of line\n  not at start\nstart again", new SearchQuery("^start", IsRegex: true));
        Assert.Equal([0, 2], r.Files[0].Matches.Select(m => m.Line));
        var e = await Run("a\nb\nc", new SearchQuery("^$", IsRegex: true), "empty.txt");
        Assert.Empty(e.Files); // empty matches are never results
    }

    [Fact]
    public async Task Regex_that_can_match_empty_never_reports_empty_matches()
    {
        var r = await Run("bbb\naab", new SearchQuery("a*", IsRegex: true));
        Assert.Equal(["aa"], r.Files[0].Matches.Select(m => m.MatchedText));
    }

    [Fact]
    public async Task Invalid_regex_fails_with_a_message_and_starts_no_workers()
    {
        var r = await Run("x", new SearchQuery("(unclosed", IsRegex: true));
        Assert.Equal(SearchOutcome.Failed, r.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(r.Completion.Error));
        Assert.Equal(0, _h.Engine.RunningWorkers);
    }

    [Fact]
    public async Task Empty_pattern_fails_instead_of_matching_everything()
    {
        var r = await Run("x", new SearchQuery(""));
        Assert.Equal(SearchOutcome.Failed, r.Outcome);
    }

    [Fact]
    public void Matcher_kinds_pick_the_linear_engine_when_possible()
    {
        var limits = new SearchLimits();
        Assert.Equal("regex-nonbacktracking", TextMatcher.Create(new SearchQuery("(a+)+$", IsRegex: true), limits).Kind);
        Assert.Equal("regex-backtracking", TextMatcher.Create(new SearchQuery("(?=a)a+", IsRegex: true), limits).Kind);
        Assert.Equal("regex-backtracking", TextMatcher.Create(new SearchQuery(@"(\w)\1", IsRegex: true), limits).Kind);
        Assert.Equal("literal", TextMatcher.Create(new SearchQuery("(a+)+$"), limits).Kind);
    }

    // ------------------------------------------------------------------------------------ multi-line

    [Fact]
    public async Task Multiline_regex_spans_lines_and_reports_the_end_position()
    {
        var r = await Run("begin\nmiddle\nend\nrest", new SearchQuery(@"begin\nmiddle\nend", IsRegex: true));
        var m = Only(r);
        Assert.Equal(new TextRange(new(0, 0), new(2, 3)), m.Range);
        Assert.Equal("begin", m.Preview); // the preview is the first line only
    }

    [Fact]
    public async Task Multiline_flag_lets_dot_star_cross_nothing_but_anchors_work_per_line()
    {
        var r = await Run("a1\nb2\nc3", new SearchQuery(@"^b\d$", IsRegex: true, MultiLine: true));
        Assert.Equal(new TextRange(new(1, 0), new(1, 2)), Only(r).Range);
    }

    [Fact]
    public async Task Multiline_works_with_crlf_files_and_gives_the_same_positions_as_lf()
    {
        var lf = await Run("x\nfoo\nbar\ny", new SearchQuery(@"foo\nbar", IsRegex: true), "lf.txt");
        var crlf = await Run("x\r\nfoo\r\nbar\r\ny", new SearchQuery(@"foo\nbar", IsRegex: true), "crlf.txt");
        Assert.Equal(new TextRange(new(1, 0), new(2, 3)), lf.File("lf.txt").Matches.Single().Range);
        Assert.Equal(new TextRange(new(1, 0), new(2, 3)), crlf.File("crlf.txt").Matches.Single().Range);
    }

    [Fact]
    public async Task A_literal_containing_a_line_break_matches_across_lines_in_crlf_files()
    {
        var r = await Run("one\r\ntwo\r\nthree", new SearchQuery("one\ntwo"));
        Assert.Equal(new TextRange(new(0, 0), new(1, 3)), Only(r).Range);
    }

    [Fact]
    public async Task Multiline_match_start_column_is_correct_when_not_at_line_start()
    {
        var r = await Run("12345foo\nbar67", new SearchQuery(@"foo\nbar", IsRegex: true));
        Assert.Equal(new TextRange(new(0, 5), new(1, 3)), Only(r).Range);
    }

    [Fact]
    public async Task Several_multiline_matches_count_lines_incrementally()
    {
        var r = await Run("a\nb\n--\na\nb\n--\na\nb", new SearchQuery(@"a\nb", IsRegex: true));
        Assert.Equal([0, 3, 6], r.Files[0].Matches.Select(m => m.Line));
    }

    // ------------------------------------------------------------------------------------- previews

    [Fact]
    public async Task Preview_trims_leading_whitespace_and_points_at_the_match()
    {
        var r = await Run("\t\t   var needle = 1;   ", new SearchQuery("needle"));
        var m = Only(r);
        Assert.Equal("var needle = 1;", m.Preview);
        Assert.Equal("needle", m.Preview.Substring(m.PreviewMatchStart, m.PreviewMatchLength));
        Assert.Equal(9, m.Column); // the real column is untouched by trimming (5 whitespace chars + "var ")
    }

    [Fact]
    public async Task Preview_turns_tabs_into_spaces_without_shifting_offsets()
    {
        var r = await Run("a\tb\tneedle", new SearchQuery("needle"));
        var m = Only(r);
        Assert.Equal("a b needle", m.Preview);
        Assert.Equal("needle", m.Preview.Substring(m.PreviewMatchStart, m.PreviewMatchLength));
    }

    [Fact]
    public async Task Very_long_lines_get_a_clipped_preview_centred_on_the_match()
    {
        var line = new string('x', 20_000) + "needle" + new string('y', 20_000);
        var r = await Run(line, new SearchQuery("needle"));
        var m = Only(r);
        Assert.Equal(20_000, m.Column);
        Assert.True(m.Preview.Length <= 310, $"preview was {m.Preview.Length} chars");
        Assert.StartsWith("…", m.Preview);
        Assert.EndsWith("…", m.Preview);
        Assert.Equal("needle", m.Preview.Substring(m.PreviewMatchStart, m.PreviewMatchLength));
    }

    [Fact]
    public async Task Matched_text_is_clipped_to_256_characters()
    {
        var r = await Run(new string('a', 1000), new SearchQuery("a+", IsRegex: true));
        var m = Only(r);
        Assert.Equal(256, m.MatchedText.Length);
        Assert.Equal(1000, m.Range.End.Character - m.Range.Start.Character);
    }

    // ---------------------------------------------------------------------------- chunk boundaries

    [Fact]
    public async Task Lines_that_straddle_the_64k_read_boundary_are_matched_exactly_once()
    {
        // lines of 1000 chars: boundaries at 65536 fall in the middle of a line for every alignment
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) sb.Append(new string('.', 990)).Append(" NEEDLE").Append(i).Append('\n');
        var r = await Run(sb.ToString(), new SearchQuery("needle"));
        Assert.Equal(200, r.MatchCount);
        Assert.Equal(Enumerable.Range(0, 200), r.Files[0].Matches.Select(m => m.Line));
    }

    [Fact]
    public async Task A_crlf_split_across_the_read_boundary_counts_as_one_line_break()
    {
        // put "\r" as the LAST byte of the first 64 KB chunk and "\n" as the first of the next
        var chunk = 64 * 1024;
        var bytes = new System.Text.StringBuilder();
        bytes.Append('a', chunk - 1).Append("\r\n").Append("needle\r\nneedle");
        var r = await Run(bytes.ToString(), new SearchQuery("needle"));
        Assert.Equal([1, 2], r.Files[0].Matches.Select(m => m.Line));
        Assert.Equal("\r\n", r.Files[0].LineEnding);
    }

    [Fact]
    public async Task A_line_longer_than_the_line_buffer_is_split_but_matches_before_the_split_are_found()
    {
        var limits = new SearchLimits { MaxLineChars = 1000 };
        using var h = new SearchHarness(limits);
        h.Write("long.txt", "needle" + new string('x', 5000) + "needle\nneedle");
        var r = await h.RunAsync(new SearchQuery("needle"));
        var m = r.Files[0].Matches;
        Assert.Contains(m, x => x.Range.Start == new TextPosition(0, 0));
        Assert.Contains(m, x => x.Range.Start == new TextPosition(0, 5006)); // columns stay absolute across the split
        Assert.Contains(m, x => x.Range.Start == new TextPosition(1, 0));
    }
}
