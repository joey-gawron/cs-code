using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Languages;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Clipboard brief A2/A3: Visual Studio's clipboard behaviours as pure functions (whole lines, blocks, carets, EOL, auto-indent).</summary>
public sealed class ClipboardOperationsTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create(new ThreadAffinityUiThread());
    private readonly LanguageService _languages;

    public ClipboardOperationsTests() => _languages = new LanguageService(_host.Services);

    public void Dispose()
    {
        _languages.Dispose();
        _host.Dispose();
    }

    private EditingContext Ctx(string lang = "csharp", int tab = 4, bool spaces = true, string eol = "\n")
    {
        var ext = _languages.GetExtended(lang);
        return new EditingContext(ext.Base, tab, spaces, eol, ext.EnterRules);
    }

    private static InMemoryTextDocument Doc(string text) => new(new Uri("file:///x.cs"), "csharp", text);
    private static SelectionSet Caret(int line, int col) => SelectionSet.Single(TextRange.At(line, col));
    private static SelectionSet Sel(int l1, int c1, int l2, int c2) => SelectionSet.Single(TextRange.FromLines(l1, c1, l2, c2));
    private static SelectionSet Box(params (int Line, int Start, int End)[] rows) => new(rows.Select(r => TextRange.FromLines(r.Line, r.Start, r.Line, r.End)).ToArray(), IsBlock: true);

    private static (string Text, TextRange Selection) Apply(InMemoryTextDocument doc, EditPlan? plan)
    {
        Assert.NotNull(plan);
        Assert.True(doc.ApplyEdits(plan!.Edits));
        return (doc.GetText(), plan.Selection);
    }

    private (string Text, TextRange Selection) Paste(string text, SelectionSet at, ClipboardText clip, EditingContext? ctx = null, bool autoIndent = true)
    {
        var doc = Doc(text);
        var result = ClipboardOperations.Paste(doc, at, clip, ctx ?? Ctx(), autoIndent);
        Assert.NotNull(result);
        return Apply(doc, result!.Plan);
    }

    // ---------------------------------------------------------------- A3: empty selection = whole line

    [Fact]
    public void Copy_with_an_empty_selection_takes_the_whole_line_with_its_line_ending()
    {
        var clip = ClipboardOperations.Copy(Doc("first\n    second\nthird"), Caret(1, 3), Ctx());
        Assert.NotNull(clip);
        Assert.Equal("    second\n", clip!.Text);
        Assert.Equal(ClipboardTextShape.FullLines, clip.Shape);
        Assert.Equal(4, clip.SourceIndent);
    }

    [Fact]
    public void Copy_with_an_empty_selection_copies_nothing_when_the_whole_line_mode_is_off()
    {
        Assert.Null(ClipboardOperations.Copy(Doc("a\nb"), Caret(0, 0), Ctx(), emptySelectionClipboard: false));
        Assert.Null(ClipboardOperations.Cut(Doc("a\nb"), Caret(0, 0), emptySelectionClipboard: false));
    }

    [Fact]
    public void Copy_of_a_selection_is_the_selected_text()
    {
        var clip = ClipboardOperations.Copy(Doc("hello world"), Sel(0, 6, 0, 11), Ctx());
        Assert.Equal(new ClipboardText("world") { SourceIndent = 0 }, clip);
    }

    [Fact]
    public void Cut_with_an_empty_selection_removes_the_line_and_keeps_the_column()
    {
        var doc = Doc("one\ntwo\nthree");
        var (text, sel) = Apply(doc, ClipboardOperations.Cut(doc, Caret(1, 2)));
        Assert.Equal("one\nthree", text);
        Assert.Equal(TextRange.At(1, 2), sel);
    }

    [Fact]
    public void Cut_of_the_last_line_removes_the_line_break_before_it()
    {
        var doc = Doc("one\ntwo");
        var (text, sel) = Apply(doc, ClipboardOperations.Cut(doc, Caret(1, 1)));
        Assert.Equal("one", text);
        Assert.Equal(TextRange.At(0, 1), sel);
    }

    [Fact]
    public void Cut_of_the_only_line_empties_it()
    {
        var doc = Doc("only");
        var (text, _) = Apply(doc, ClipboardOperations.Cut(doc, Caret(0, 2)));
        Assert.Equal("", text);
    }

    [Fact]
    public void Cut_of_a_selection_removes_it_and_puts_the_caret_at_its_start()
    {
        var doc = Doc("hello world");
        var (text, sel) = Apply(doc, ClipboardOperations.Cut(doc, Sel(0, 5, 0, 11)));
        Assert.Equal("hello", text);
        Assert.Equal(TextRange.At(0, 5), sel);
    }

    [Fact]
    public void A_whole_line_pastes_above_the_carets_line_and_the_caret_stays_on_its_line()
    {
        var clip = new ClipboardText("copied\n", ClipboardTextShape.FullLines) { SourceIndent = 0 };
        var (text, sel) = Paste("one\ntwo\n", Caret(1, 2), clip, autoIndent: false);
        Assert.Equal("one\ncopied\ntwo\n", text);
        Assert.Equal(TextRange.At(2, 2), sel);
    }

    [Fact]
    public void A_whole_line_pasted_over_a_selection_replaces_the_selection()
    {
        var clip = new ClipboardText("copied\n", ClipboardTextShape.FullLines);
        var (text, _) = Paste("abc def", Sel(0, 4, 0, 7), clip, autoIndent: false);
        Assert.Equal("abc copied\n", text);
    }

    // ---------------------------------------------------------------- A3: several carets, box selections

    [Fact]
    public void A_column_of_carets_copies_each_carets_line_and_pastes_one_line_per_caret()
    {
        var doc = Doc("a1\nb2\nc3\nd4");
        var carets = Box((0, 1, 1), (1, 1, 1), (2, 1, 1));
        var clip = ClipboardOperations.Copy(doc, carets, Ctx());
        Assert.Equal(ClipboardTextShape.FullLines, clip!.Shape);
        Assert.Equal(["a1\n", "b2\n", "c3\n"], clip.Pieces);

        // Pasted into three carets elsewhere: each caret's line gets one of the lines above it.
        var (text, _) = Paste("x\ny\nz", Box((0, 0, 0), (1, 0, 0), (2, 0, 0)), clip, autoIndent: false);
        Assert.Equal("a1\nx\nb2\ny\nc3\nz", text);
    }

    [Fact]
    public void A_box_selection_copies_as_a_block()
    {
        var doc = Doc("abcd\nefgh\nijkl");
        var clip = ClipboardOperations.Copy(doc, Box((0, 1, 3), (1, 1, 3), (2, 1, 3)), Ctx());
        Assert.Equal(ClipboardTextShape.Block, clip!.Shape);
        Assert.Equal("bc\nfg\njk", clip.Text);
        Assert.Equal(["bc", "fg", "jk"], clip.Pieces);
    }

    [Fact]
    public void A_block_pastes_as_a_block_at_the_carets_visual_column_padding_short_lines_and_adding_missing_ones()
    {
        var clip = new ClipboardText("XY\nZW\nUV", ClipboardTextShape.Block, ["XY", "ZW", "UV"]);
        var (text, sel) = Paste("0123\n0\n", Caret(0, 2), clip);
        Assert.Equal("01XY23\n0 ZW\n  UV", text);
        Assert.Equal(TextRange.At(2, 4), sel);
    }

    [Fact]
    public void A_block_pastes_at_the_visual_column_across_tabs()
    {
        var clip = new ClipboardText("A\nB", ClipboardTextShape.Block, ["A", "B"]);
        var (text, _) = Paste("\tx\n    y", Caret(0, 1), clip, Ctx(tab: 4));
        Assert.Equal("\tAx\n    By", text);
    }

    [Fact]
    public void Several_carets_take_one_line_each_when_the_counts_match()
    {
        var clip = ClipboardText.Plain("one\ntwo\nthree");
        var (text, _) = Paste("a\nb\nc", Box((0, 1, 1), (1, 1, 1), (2, 1, 1)), clip);
        Assert.Equal("aone\nbtwo\ncthree", text);
    }

    [Fact]
    public void Several_carets_each_take_a_single_line_and_the_whole_text_when_the_counts_differ()
    {
        var (single, _) = Paste("a\nb", Box((0, 1, 1), (1, 1, 1)), ClipboardText.Plain("!"));
        Assert.Equal("a!\nb!", single);
        var (whole, _) = Paste("a\nb", Box((0, 1, 1), (1, 1, 1)), ClipboardText.Plain("x\ny\nz"));
        Assert.Equal("ax\ny\nz\nbx\ny\nz", whole);
    }

    [Fact]
    public void A_box_of_text_is_replaced_row_by_row_by_a_block_of_the_same_height()
    {
        var clip = new ClipboardText("1\n2", ClipboardTextShape.Block, ["1", "2"]);
        var (text, _) = Paste("abc\ndef", Box((0, 1, 2), (1, 1, 2)), clip);
        Assert.Equal("a1c\nd2f", text);
    }

    // ---------------------------------------------------------------- A2: line endings and Unicode

    [Theory]
    [InlineData("a\r\nb\r\nc", "\n", "x a\nb\nc")]
    [InlineData("a\nb\rc", "\r\n", "x a\r\nb\r\nc")]
    [InlineData("a\rb", "\n", "x a\nb")]
    public void Paste_converts_line_endings_to_the_documents(string clipboard, string eol, string expected)
    {
        var doc = new InMemoryTextDocument(new Uri("file:///x.txt"), "plaintext", "x ");
        var result = ClipboardOperations.Paste(doc, Caret(0, 2), ClipboardText.Plain(clipboard), Ctx("plaintext", eol: eol), autoIndent: false);
        Apply(doc, result!.Plan);
        Assert.Equal(expected, doc.GetText());
    }

    [Theory]
    [InlineData("a\r\nb", "\n", "a\nb")]
    [InlineData("a\nb", "\r\n", "a\r\nb")]
    [InlineData("a\rb\nc\r\nd", "\n", "a\nb\nc\nd")]
    [InlineData("plain", "\r\n", "plain")]
    [InlineData("a\r\nb", "\r\n", "a\r\nb")]
    [InlineData("", "\n", "")]
    public void Line_ending_normalisation(string input, string eol, string expected) =>
        Assert.Equal(expected, ClipboardTextExtensions.NormalizeLineEndings(input, eol));

    [Fact]
    public void Unicode_text_survives_copy_and_paste_unchanged()
    {
        // Emoji (surrogate pairs, ZWJ sequence), combining marks, right-to-left text.
        const string text = "👩‍💻 café ̧ é שלום עולם مرحبا";
        var doc = Doc(text);
        var clip = ClipboardOperations.Copy(doc, Sel(0, 0, 0, text.Length), Ctx());
        Assert.Equal(text, clip!.Text);
        var (pasted, _) = Paste("[]", Caret(0, 1), clip);
        Assert.Equal("[" + text + "]", pasted);
    }

    // ---------------------------------------------------------------- A3: paste respects auto-indent

    [Fact]
    public void A_multi_line_paste_at_an_indented_caret_keeps_relative_indentation_under_the_caret()
    {
        // Copied from a line indented 8 deep (whole lines with their indentation), pasted on an empty line indented 4.
        var clip = new ClipboardText("        if (x)\n        {\n            y();\n        }") { SourceIndent = 8 };
        var (text, _) = Paste("class C\n{\n    \n}", Caret(2, 4), clip);
        Assert.Equal("class C\n{\n    if (x)\n    {\n        y();\n    }\n}", text);
    }

    [Fact]
    public void A_multi_line_paste_copied_from_the_first_non_blank_keeps_relative_indentation()
    {
        // Selection started at "if" (Home goes to the first non-blank in Visual Studio): line 0 has no indentation in the text.
        var clip = new ClipboardText("if (x)\n        {\n            y();\n        }") { SourceIndent = 8 };
        var (text, _) = Paste("{\n  \n}", Caret(1, 2), clip, Ctx(tab: 2));
        Assert.Equal("{\n  if (x)\n  {\n      y();\n  }\n}", text);
    }

    [Fact]
    public void A_multi_line_paste_from_another_application_infers_the_indentation()
    {
        var clip = ClipboardText.Plain("\t\tfoo();\n\t\tbar();\n\t\t\tbaz();");
        var (text, _) = Paste("x\n    \ny", Caret(1, 4), clip, Ctx(tab: 4, spaces: true));
        Assert.Equal("x\n    foo();\n    bar();\n        baz();\ny", text);
    }

    [Fact]
    public void Whole_lines_pasted_above_a_line_take_the_indentation_Enter_would_give()
    {
        var clip = new ClipboardText("foo();\n", ClipboardTextShape.FullLines) { SourceIndent = 0 };
        var (text, _) = Paste("void M()\n{\n    bar();\n}", Caret(3, 0), clip);
        Assert.Equal("void M()\n{\n    bar();\n    foo();\n}", text);

        var (afterBrace, _) = Paste("void M()\n{\n}", Caret(2, 0), clip);
        Assert.Equal("void M()\n{\n    foo();\n}", afterBrace);
    }

    [Fact]
    public void Auto_indent_off_pastes_the_text_as_it_is()
    {
        var clip = new ClipboardText("        if (x)\n        {\n        }") { SourceIndent = 8 };
        var (text, _) = Paste("{\n    \n}", Caret(1, 4), clip, autoIndent: false);
        Assert.Equal("{\n            if (x)\n        {\n        }\n}", text);
    }

    [Fact]
    public void Indentation_uses_tabs_when_the_document_does()
    {
        var clip = new ClipboardText("a\n    b") { SourceIndent = 0 };
        var (text, _) = Paste("\t\n", Caret(0, 1), clip, Ctx(tab: 4, spaces: false));
        Assert.Equal("\ta\n\t\tb\n", text);
    }

    [Fact]
    public void A_single_line_paste_is_never_reindented()
    {
        var (text, sel) = Paste("    ", Caret(0, 4), new ClipboardText("        x();") { SourceIndent = 8 });
        Assert.Equal("            x();", text);
        Assert.Equal(TextRange.At(0, 16), sel);
    }

    [Fact]
    public void The_paste_result_reports_where_the_text_landed()
    {
        var doc = Doc("ab");
        var result = ClipboardOperations.Paste(doc, Caret(0, 1), ClipboardText.Plain("X\nY"), Ctx("plaintext"), autoIndent: false)!;
        Assert.Equal(TextRange.FromLines(0, 1, 1, 1), result.Inserted);
        Assert.Equal(TextRange.At(1, 1), result.Plan.Selection);
    }
}
