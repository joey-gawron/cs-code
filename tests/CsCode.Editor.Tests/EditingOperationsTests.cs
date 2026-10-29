using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Languages;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Theming;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 7: the editing commands as pure functions, on real VS Code language configurations.</summary>
public sealed class EditingOperationsTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly LanguageService _languages;

    public EditingOperationsTests() => _languages = new LanguageService(_host.Services);

    public void Dispose()
    {
        _languages.Dispose();
        _host.Dispose();
    }

    private EditingContext Ctx(string lang = "csharp", int tab = 4, bool spaces = true)
    {
        var ext = _languages.GetExtended(lang);
        return new EditingContext(ext.Base, tab, spaces, "\n", ext.EnterRules);
    }

    private static TextRange R(int l1, int c1, int l2, int c2) => TextRange.FromLines(l1, c1, l2, c2);

    /// <summary>Applies the plan like the view does (one ApplyEdits) and returns the resulting text and selection.</summary>
    private static (string Text, TextRange Sel) Apply(string text, EditPlan? plan)
    {
        Assert.NotNull(plan);
        var doc = new InMemoryTextDocument(new Uri("file:///x"), "plaintext", text);
        Assert.True(doc.ApplyEdits(plan!.Edits));
        return (doc.GetText(), plan.Selection);
    }

    private static InMemoryTextDocument Doc(string text) => new(new Uri("file:///x"), "plaintext", text);

    // ---------- line comments ----------

    [Fact]
    public void Toggle_line_comment_adds_the_marker_at_the_shallowest_indent_for_all_lines()
    {
        // the shallowest indent is 0 (line "a"), so every marker goes in column 0 and the deeper lines keep their indentation after it
        const string src = "a\n    b\n  c";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 2, 3), Ctx()));
        Assert.Equal("// a\n//     b\n//   c", text);
    }

    [Fact]
    public void Toggle_line_comment_puts_all_markers_in_one_column()
    {
        const string src = "    a\n      b\n    c";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 2, 5), Ctx()));
        Assert.Equal("    // a\n    //   b\n    // c", text);
    }

    [Fact]
    public void Toggle_line_comment_removes_markers_and_one_following_space_when_all_lines_are_commented()
    {
        const string src = "    // a\n    //b\n    //   c";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 2, 9), Ctx()));
        Assert.Equal("    a\n    b\n      c", text);
    }

    [Fact]
    public void Toggle_line_comment_comments_everything_when_only_some_lines_are_commented()
    {
        const string src = "// a\nb";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 1, 1), Ctx()));
        Assert.Equal("// // a\n// b", text);
    }

    [Fact]
    public void Toggle_line_comment_skips_blank_lines()
    {
        const string src = "a\n\nb";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 2, 1), Ctx()));
        Assert.Equal("// a\n\n// b", text);
    }

    [Fact]
    public void A_selection_ending_at_column_zero_does_not_include_that_line()
    {
        const string src = "a\nb\nc";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 2, 0), Ctx()));
        Assert.Equal("// a\n// b\nc", text);
    }

    [Fact]
    public void Toggle_line_comment_twice_restores_the_text_and_tracks_the_caret()
    {
        const string src = "    foo();";
        var first = EditingOperations.ToggleLineComment(Doc(src), R(0, 8, 0, 8), Ctx())!;
        var (t1, s1) = Apply(src, first);
        Assert.Equal("    // foo();", t1);
        Assert.Equal(new TextPosition(0, 11), s1.Start); // caret stayed on the same character
        var (t2, _) = Apply(t1, EditingOperations.ToggleLineComment(Doc(t1), s1, Ctx()));
        Assert.Equal(src, t2);
    }

    [Fact]
    public void Python_uses_hash_comments()
    {
        var (text, _) = Apply("x = 1", EditingOperations.ToggleLineComment(Doc("x = 1"), R(0, 0, 0, 0), Ctx("python")));
        Assert.Equal("# x = 1", text);
    }

    [Fact]
    public void Languages_without_line_comments_use_block_comments_per_line()
    {
        const string src = "a { b: c }\n  d {}";
        var (text, _) = Apply(src, EditingOperations.ToggleLineComment(Doc(src), R(0, 0, 1, 6), Ctx("css")));
        Assert.Equal("/* a { b: c } */\n  /* d {} */", text);
        var (back, _) = Apply(text, EditingOperations.ToggleLineComment(Doc(text), R(0, 0, 1, 12), Ctx("css")));
        Assert.Equal(src, back);
    }

    [Fact]
    public void Languages_with_no_comment_syntax_do_nothing()
    {
        Assert.Null(EditingOperations.ToggleLineComment(Doc("a"), R(0, 0, 0, 0), Ctx("plaintext")));
    }

    // ---------- block comments ----------

    [Fact]
    public void Block_comment_wraps_the_selection_and_unwraps_it_again()
    {
        const string src = "int a = 1;";
        var (wrapped, sel) = Apply(src, EditingOperations.ToggleBlockComment(Doc(src), R(0, 4, 0, 9), Ctx()));
        Assert.Equal("int /* a = 1 */;", wrapped);
        var (back, _) = Apply(wrapped, EditingOperations.ToggleBlockComment(Doc(wrapped), sel, Ctx()));
        Assert.Equal(src, back);
    }

    [Fact]
    public void Block_comment_unwraps_when_the_selection_is_inside_the_markers()
    {
        const string src = "int /* a = 1 */;";
        var (text, _) = Apply(src, EditingOperations.ToggleBlockComment(Doc(src), R(0, 7, 0, 12), Ctx()));
        Assert.Equal("int a = 1;", text);
    }

    [Fact]
    public void Block_comment_without_a_selection_wraps_the_lines_content()
    {
        var (text, _) = Apply("  x = 1;  ", EditingOperations.ToggleBlockComment(Doc("  x = 1;  "), R(0, 4, 0, 4), Ctx()));
        Assert.Equal("  /* x = 1; */  ", text);
    }

    // ---------- moving / copying / deleting ----------

    [Fact]
    public void Move_lines_down_and_up_swap_with_the_neighbour_and_carry_the_selection()
    {
        const string src = "a\nb\nc";
        var (down, sel) = Apply(src, EditingOperations.MoveLines(Doc(src), R(0, 1, 0, 1), Ctx(), down: true));
        Assert.Equal("b\na\nc", down);
        Assert.Equal(new TextPosition(1, 1), sel.Start);
        var (up, sel2) = Apply(src, EditingOperations.MoveLines(Doc(src), R(2, 0, 2, 0), Ctx(), down: false));
        Assert.Equal("a\nc\nb", up);
        Assert.Equal(1, sel2.Start.Line);
    }

    [Fact]
    public void Moving_a_multi_line_block_moves_it_past_one_neighbour()
    {
        const string src = "a\nb\nc\nd";
        var (text, sel) = Apply(src, EditingOperations.MoveLines(Doc(src), R(0, 0, 1, 1), Ctx(), down: true));
        Assert.Equal("c\na\nb\nd", text);
        Assert.Equal((1, 2), (sel.Start.Line, sel.End.Line));
    }

    [Fact]
    public void Moving_past_the_ends_does_nothing()
    {
        Assert.Null(EditingOperations.MoveLines(Doc("a\nb"), R(0, 0, 0, 0), Ctx(), down: false));
        Assert.Null(EditingOperations.MoveLines(Doc("a\nb"), R(1, 0, 1, 0), Ctx(), down: true));
    }

    [Fact]
    public void Move_lines_uses_the_documents_line_ending()
    {
        var (text, _) = Apply("a\r\nb", EditingOperations.MoveLines(Doc("a\r\nb"), R(0, 0, 0, 0), new EditingContext(Ctx().Language, 4, true, "\r\n"), down: true));
        Assert.Equal("b\r\na", text);
    }

    [Fact]
    public void Copy_lines_down_duplicates_below_and_selects_the_copy()
    {
        var (text, sel) = Apply("a\nb", EditingOperations.CopyLines(Doc("a\nb"), R(0, 1, 0, 1), Ctx(), down: true));
        Assert.Equal("a\na\nb", text);
        Assert.Equal(new TextPosition(1, 1), sel.Start);
    }

    [Fact]
    public void Copy_lines_up_duplicates_above_and_keeps_the_selection_on_the_upper_copy()
    {
        var (text, sel) = Apply("a\nb", EditingOperations.CopyLines(Doc("a\nb"), R(1, 0, 1, 0), Ctx(), down: false));
        Assert.Equal("a\nb\nb", text);
        Assert.Equal(1, sel.Start.Line);
    }

    [Fact]
    public void Copying_the_last_line_without_a_final_newline_works()
    {
        var (text, _) = Apply("a\nb", EditingOperations.CopyLines(Doc("a\nb"), R(1, 0, 1, 0), Ctx(), down: true));
        Assert.Equal("a\nb\nb", text);
    }

    [Theory]
    [InlineData("a\nb\nc", 1, "a\nc")]
    [InlineData("a\nb\nc", 0, "b\nc")]
    [InlineData("a\nb\nc", 2, "a\nb")]    // last line: no stray empty line left
    [InlineData("a", 0, "")]
    [InlineData("a\n", 0, "")]
    public void Delete_line(string src, int line, string expected)
    {
        var (text, _) = Apply(src, EditingOperations.DeleteLines(Doc(src), R(line, 0, line, 0), Ctx()));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Delete_lines_covers_a_multi_line_selection()
    {
        var (text, sel) = Apply("a\nb\nc\nd", EditingOperations.DeleteLines(Doc("a\nb\nc\nd"), R(1, 0, 2, 1), Ctx()));
        Assert.Equal("a\nd", text);
        Assert.Equal(1, sel.Start.Line);
    }

    // ---------- indent / outdent ----------

    [Fact]
    public void Indent_lines_adds_one_unit_to_every_non_empty_line()
    {
        var (text, _) = Apply("a\n\nb", EditingOperations.IndentLines(Doc("a\n\nb"), R(0, 0, 2, 1), Ctx(tab: 2)));
        Assert.Equal("  a\n\n  b", text);
        var (tabs, _) = Apply("a", EditingOperations.IndentLines(Doc("a"), R(0, 0, 0, 0), Ctx(spaces: false)));
        Assert.Equal("\ta", tabs);
    }

    [Fact]
    public void Outdent_removes_one_tab_or_up_to_tabsize_spaces()
    {
        var (text, _) = Apply("\ta\n      b\n  c\nd", EditingOperations.OutdentLines(Doc("\ta\n      b\n  c\nd"), R(0, 0, 3, 1), Ctx(tab: 4)));
        Assert.Equal("a\n  b\nc\nd", text);
        Assert.Null(EditingOperations.OutdentLines(Doc("a"), R(0, 0, 0, 0), Ctx()));
    }

    // ---------- join / transpose / trim / sort ----------

    [Theory]
    [InlineData("a\n   b", 0, "a b")]
    [InlineData("a \n b", 0, "a b")]           // no double space when the line already ends with one
    [InlineData("f(\n  x\n)", 0, "f( x")]      // joins the pair selected by the caret line only
    [InlineData("a\n\nb", 0, "a")]             // joining with an empty line leaves the line as it is
    public void Join_lines(string src, int line, string expectedFirstLine)
    {
        var plan = EditingOperations.JoinLines(Doc(src), R(line, 0, line, 0), Ctx());
        var (text, _) = Apply(src, plan);
        Assert.Equal(expectedFirstLine, text.Split('\n')[0]);
    }

    [Fact]
    public void Join_lines_over_a_selection_joins_all_of_them_and_a_closing_paren_attaches_without_space()
    {
        var (text, _) = Apply("f(\n  a,\n  b\n)", EditingOperations.JoinLines(Doc("f(\n  a,\n  b\n)"), R(0, 0, 3, 1), Ctx()));
        Assert.Equal("f( a, b)", text);
    }

    [Fact]
    public void Join_on_the_last_line_does_nothing()
    {
        Assert.Null(EditingOperations.JoinLines(Doc("a\nb"), R(1, 0, 1, 0), Ctx()));
    }

    [Theory]
    [InlineData("abc", 1, "bac", 2)]    // swaps the characters either side of the caret, caret moves right
    [InlineData("abc", 3, "acb", 3)]    // at the end it swaps the last two
    public void Transpose(string src, int col, string expected, int caretCol)
    {
        var (text, sel) = Apply(src, EditingOperations.TransposeCharacters(Doc(src), R(0, col, 0, col), Ctx()));
        Assert.Equal(expected, text);
        Assert.Equal(caretCol, sel.Start.Character);
    }

    [Fact]
    public void Transpose_does_nothing_at_line_start_on_selections_or_short_lines()
    {
        Assert.Null(EditingOperations.TransposeCharacters(Doc("abc"), R(0, 0, 0, 0), Ctx()));
        Assert.Null(EditingOperations.TransposeCharacters(Doc("abc"), R(0, 0, 0, 2), Ctx()));
        Assert.Null(EditingOperations.TransposeCharacters(Doc("a"), R(0, 1, 0, 1), Ctx()));
    }

    [Fact]
    public void Trim_trailing_whitespace_covers_the_whole_document()
    {
        var (text, _) = Apply("a  \n\t\nb \t", EditingOperations.TrimTrailingWhitespace(Doc("a  \n\t\nb \t"), R(0, 0, 0, 0)));
        Assert.Equal("a\n\nb", text);
        Assert.Null(EditingOperations.TrimTrailingWhitespace(Doc("clean"), R(0, 0, 0, 0)));
    }

    [Fact]
    public void Sort_lines_ascending_and_descending_ordinal()
    {
        const string src = "b\nB\na\nA\nc";
        var (asc, _) = Apply(src, EditingOperations.SortLines(Doc(src), R(0, 0, 4, 1), Ctx(), descending: false));
        Assert.Equal("A\nB\na\nb\nc", asc);
        var (desc, _) = Apply(src, EditingOperations.SortLines(Doc(src), R(0, 0, 4, 1), Ctx(), descending: true));
        Assert.Equal("c\nb\na\nB\nA", desc);
    }

    [Fact]
    public void Sort_lines_needs_a_multi_line_selection_and_skips_sorted_input()
    {
        Assert.Null(EditingOperations.SortLines(Doc("b\na"), R(0, 0, 0, 1), Ctx(), false));
        Assert.Null(EditingOperations.SortLines(Doc("a\nb"), R(0, 0, 1, 1), Ctx(), false));
    }

    // ---------- auto-indent ----------

    [Fact]
    public void Enter_between_braces_makes_three_lines_with_the_caret_on_the_indented_one()
    {
        var r = AutoIndent.ForEnter("    if (x) {", "}", Ctx());
        Assert.Equal("        ", r.NewLineIndent);
        Assert.Equal("    ", r.ClosingLineIndent);
    }

    [Theory]
    [InlineData("    foo(", "", "        ")]       // after an opener: indent
    [InlineData("    foo();", "", "    ")]         // plain: keep the indentation
    [InlineData("\tfoo {", "", "\t\t")]
    [InlineData("", "", "")]
    public void Enter_indentation_in_csharp(string before, string after, string expected) =>
        Assert.Equal(expected, AutoIndent.ForEnter(before, after, Ctx(spaces: !before.StartsWith('\t'))).NewLineIndent);

    [Fact]
    public void Enter_after_a_python_block_opener_indents_via_the_onEnter_rule()
    {
        Assert.Equal("        ", AutoIndent.ForEnter("    def f(x):", "", Ctx("python")).NewLineIndent);
        Assert.Equal("    ", AutoIndent.ForEnter("    x = 1", "", Ctx("python")).NewLineIndent);
    }

    [Fact]
    public void Enter_after_a_doc_comment_opener_continues_with_an_asterisk_using_the_real_javascript_rules()
    {
        // /** foo|            →  keep the indent and continue with " * "
        var cont = AutoIndent.ForEnter("    /** foo", "", Ctx("javascript"));
        Assert.Equal("    ", cont.NewLineIndent);
        Assert.Equal(" * ", cont.AppendText);
        Assert.Null(cont.ClosingLineIndent);
        // /** | */  (the closer is already there) →  indentOutdent with the closer on its own line
        var between = AutoIndent.ForEnter("    /** ", " */", Ctx("javascript"));
        Assert.Equal(" * ", between.AppendText);
        Assert.Equal("    ", between.ClosingLineIndent);
        // inside an existing doc comment " * foo|" continues with "* " — guarded by previousLineText (the line above is part of the comment)
        var inside = AutoIndent.ForEnter("     * foo", "", Ctx("javascript"), previousLine: "    /**");
        Assert.Equal("* ", inside.AppendText);
        // ...but not when the line above is ordinary code
        Assert.Equal("", AutoIndent.ForEnter("     * foo", "", Ctx("javascript"), previousLine: "    int x;").AppendText);
        // and a comment's closing line outdents by the one space ("removeText": 1)
        Assert.Equal("    ", AutoIndent.ForEnter("     */", "", Ctx("javascript")).NewLineIndent);
    }

    [Fact]
    public void An_invalid_or_catastrophic_pattern_in_a_configuration_never_throws()
    {
        var cfg = new LanguageConfiguration { IncreaseIndentPattern = "([unclosed", OnEnterRules = [new OnEnterRule("(a+)+$", null, "indent")] };
        var r = AutoIndent.ForEnter(new string('a', 40) + "!", "", new EditingContext(cfg));
        Assert.NotNull(r);
    }

    [Fact]
    public void Typing_a_closing_brace_alone_on_a_line_outdents_to_its_openers_indent()
    {
        const string src = "    if (x) {\n        foo();\n        }";
        var plan = AutoIndent.AfterTyping(Doc(src), new TextPosition(2, 9), "}", Ctx());
        var (text, sel) = Apply(src, plan);
        Assert.Equal("    if (x) {\n        foo();\n    }", text);
        Assert.Equal(5, sel.Start.Character);
    }

    [Fact]
    public void A_decrease_indent_pattern_pulls_the_line_back()
    {
        var cfg = new LanguageConfiguration { DecreaseIndentPattern = @"^\s*end\b", IncreaseIndentPattern = @"^\s*(def|if)\b.*$" };
        var ctx = new EditingContext(cfg, 2);
        const string src = "def f\n  foo\n  end";
        var (text, _) = Apply(src, AutoIndent.AfterTyping(Doc(src), new TextPosition(2, 5), "d", ctx));
        Assert.Equal("def f\n  foo\nend", text);
    }

    [Fact]
    public void Lines_that_are_not_a_lone_closer_are_left_alone_after_typing()
    {
        Assert.Null(AutoIndent.AfterTyping(Doc("    foo();"), new TextPosition(0, 10), ")", Ctx()));
    }

    // ---------- auto-closing ----------

    private AutoCloseDecision Decide(string typed, string before, string after, string selected = "", string lang = "csharp", TokenKind kind = TokenKind.Other, bool autoInserted = false, bool brackets = true, bool quotes = true, bool surround = true)
    {
        var ext = _languages.GetExtended(lang);
        return AutoClosing.Decide(typed, before, after, selected, ext.Base, ext.EffectiveAutoCloseBefore, kind, autoInserted, brackets, quotes, surround);
    }

    [Theory]
    [InlineData("(", "foo", "", ")")]
    [InlineData("{", "if (x) ", "", "}")]
    [InlineData("[", "a", "", "]")]
    [InlineData("(", "foo", " rest", ")")]       // followed by whitespace: close
    [InlineData("(", "foo", ";", ")")]           // followed by a char in autoCloseBefore: close
    [InlineData("\"", "x = ", "", "\"")]
    [InlineData("'", "x = ", "", "'")]
    public void Opening_characters_insert_their_pair(string typed, string before, string after, string close)
    {
        var d = Decide(typed, before, after);
        Assert.Equal(AutoCloseKind.InsertPair, d.Kind);
        Assert.Equal(close, d.Close);
    }

    [Theory]
    [InlineData("(", "foo", "x")]       // directly in front of an identifier: do not close
    [InlineData("\"", "don", "")]       // after a word character: an apostrophe-like use, not an opener
    [InlineData("'", "don", "t")]
    public void No_pair_is_inserted_in_these_contexts(string typed, string before, string after) =>
        Assert.Equal(AutoCloseKind.None, Decide(typed, before, after).Kind);

    [Fact]
    public void Quotes_are_not_paired_inside_strings_or_comments_per_the_notIn_rules()
    {
        Assert.Equal(AutoCloseKind.None, Decide("\"", "\"abc ", "", kind: TokenKind.String).Kind);
        Assert.Equal(AutoCloseKind.None, Decide("'", "// it ", "", kind: TokenKind.Comment).Kind);
        // brackets may still pair inside a string (no notIn on them)
        Assert.Equal(AutoCloseKind.InsertPair, Decide("(", "\"abc ", "", kind: TokenKind.String).Kind);
    }

    [Fact]
    public void Typing_the_closer_over_an_auto_inserted_one_steps_over_it_otherwise_inserts()
    {
        Assert.Equal(AutoCloseKind.OverType, Decide(")", "foo(x", ")", autoInserted: true).Kind);
        Assert.Equal(AutoCloseKind.None, Decide(")", "foo(x", ")", autoInserted: false).Kind);
        Assert.Equal(AutoCloseKind.OverType, Decide("\"", "x = \"abc", "\"", autoInserted: true).Kind);
    }

    [Fact]
    public void A_selection_is_surrounded_by_the_typed_opener_when_it_is_a_surrounding_pair()
    {
        var d = Decide("(", "", "", selected: "word");
        Assert.Equal((AutoCloseKind.Surround, "(", ")"), (d.Kind, d.Open, d.Close));
        Assert.Equal(AutoCloseKind.Surround, Decide("<", "", "", selected: "T").Kind);
        Assert.Equal(AutoCloseKind.None, Decide("x", "", "", selected: "word").Kind);                       // not a surrounding pair: replaces
        Assert.Equal(AutoCloseKind.None, Decide("(", "", "", selected: "word", surround: false).Kind);
    }

    [Fact]
    public void The_settings_switch_brackets_and_quotes_independently()
    {
        Assert.Equal(AutoCloseKind.None, Decide("(", "a", "", brackets: false).Kind);
        Assert.Equal(AutoCloseKind.InsertPair, Decide("\"", "x = ", "", brackets: false).Kind);
        Assert.Equal(AutoCloseKind.None, Decide("\"", "x = ", "", quotes: false).Kind);
    }

    [Fact]
    public void Multi_character_openers_like_the_typescript_doc_comment_close_with_their_suffix()
    {
        var d = Decide("*", "/*", "", lang: "typescript");
        Assert.Equal(AutoCloseKind.InsertPair, d.Kind);
        Assert.Equal("/**", d.Open);
        Assert.Equal(" */", d.Close);
    }

    [Fact]
    public void Python_string_prefix_pairs_apply_but_not_inside_strings()
    {
        Assert.Equal(AutoCloseKind.InsertPair, Decide("\"", "x = f", "", lang: "python").Kind);
        Assert.Equal(AutoCloseKind.None, Decide("\"", "x = \"abc ", "", lang: "python", kind: TokenKind.String).Kind);
    }

    // ---------- expand selection ----------

    [Fact]
    public void Expand_selection_walks_word_line_brackets_and_document()
    {
        const string src = "void M()\n{\n    Foo(bar, baz);\n}\n";
        var doc = Doc(src);
        var cfg = Ctx().Language;
        var caret = R(2, 9, 2, 9); // inside "bar"
        var r1 = EditingOperations.LineSpan(caret);
        Assert.Equal((2, 2), r1);

        var step1 = SelectionExpander.Expand(doc, caret, cfg)!.Value;
        Assert.Equal("bar", doc.GetText(step1));
        var step2 = SelectionExpander.Expand(doc, step1, cfg)!.Value;
        Assert.Equal("bar, baz", doc.GetText(step2));         // inside the call's parentheses
        var step3 = SelectionExpander.Expand(doc, step2, cfg)!.Value;
        Assert.Equal("(bar, baz)", doc.GetText(step3));        // with the parentheses
        var step4 = SelectionExpander.Expand(doc, step3, cfg)!.Value;
        Assert.Equal("Foo(bar, baz);", doc.GetText(step4));    // the trimmed line
        var guard = 0;
        var cur = step4;
        string last = "";
        while (SelectionExpander.Expand(doc, cur, cfg) is { } next && guard++ < 10)
        {
            cur = next;
            last = doc.GetText(cur);
        }
        Assert.Equal(src, last);                               // ends at the whole document
        Assert.Null(SelectionExpander.Expand(doc, cur, cfg));
    }

    [Fact]
    public void Expand_selection_on_whitespace_goes_to_the_line()
    {
        var doc = Doc("a  b");
        var r = SelectionExpander.Expand(doc, R(0, 2, 0, 2), Ctx().Language);
        Assert.NotNull(r);
        Assert.Equal("a  b", doc.GetText(r!.Value));
    }
}
