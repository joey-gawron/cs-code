using CsCode.Core.Text;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>
/// Syntax text objects and motions (<c>af if ac ic aa ia</c>, <c>]m [m ]M [M</c>) through <see cref="IVimHost.RequestSyntaxRanges"/>: the engine
/// picks the object from the host's ranges (enclosing, counted, growing in Visual mode, looking ahead when nothing encloses the cursor), runs
/// the pending operator on it, and when the host answers later it suspends the command without blocking, queues the keys typed meanwhile and
/// resumes on the answer (or gives up after <see cref="VimEngine.SyntaxRequestTimeout"/>).
/// </summary>
public class SyntaxObjectTests
{
    private const string Code = """
        class A {
            void f(int a, int b) {
                g(1, 2);
            }
            void h() {
                return;
            }
        }
        """;

    private static VimSyntaxRange R(int l1, int c1, int l2, int c2) => new(new TextPosition(l1, c1), new TextPosition(l2, c2));

    /// <summary>What a tree-sitter host would answer for <see cref="Code"/> (ends exclusive).</summary>
    private static IReadOnlyList<VimSyntaxRange>? Ranges(VimSyntaxObjectKind kind, bool inner) => (kind, inner) switch
    {
        (VimSyntaxObjectKind.Class, false) => [R(0, 0, 7, 1)],
        (VimSyntaxObjectKind.Class, true) => [R(0, 8, 7, 1)],
        (VimSyntaxObjectKind.Function, false) => [R(1, 4, 3, 5), R(4, 4, 6, 5)],
        (VimSyntaxObjectKind.Function, true) => [R(1, 25, 3, 5), R(4, 13, 6, 5)],
        (VimSyntaxObjectKind.Parameter, false) => [R(1, 11, 1, 18), R(1, 16, 1, 23), R(2, 10, 2, 13), R(2, 11, 2, 14)],
        (VimSyntaxObjectKind.Parameter, true) => [R(1, 11, 1, 16), R(1, 18, 1, 23), R(2, 10, 2, 11), R(2, 13, 2, 14)],
        _ => [],
    };

    private static (VimEngine Engine, InMemoryVimHost Host) Create(string text = Code, bool tree = true, bool deferred = false)
    {
        var host = new InMemoryVimHost(text, new ManualVimTimer())
        {
            SyntaxRanges = tree ? Ranges : null,
            DeferSyntaxAnswers = deferred,
        };
        return (new VimEngine(host, new VimSession()), host);
    }

    private static string Text(IVimHost h) => string.Join("\n", Enumerable.Range(0, h.Buffer.LineCount).Select(h.Buffer.GetLine));

    private static void Keys(VimEngine e, string notation)
    {
        foreach (var k in CsCode.Vim.Keys.ParseNotation(notation, "\\", "\\")) e.HandleKey(k);
    }

    // ---- operators ----

    [Fact]
    public void Daf_deletes_the_function_around_the_cursor_and_u_brings_it_back()
    {
        var (e, h) = Create();
        h.Caret = new TextPosition(2, 8);
        e.TypeKeys("daf");
        // from the indentation to the end of a line over several lines: Vim's delete takes the whole lines (as Neovim does with these objects)
        Assert.Equal("class A {\n    void h() {\n        return;\n    }\n}", Text(h));
        Assert.Equal([(VimSyntaxObjectKind.Function, false)], h.SyntaxRequests);
        Assert.Equal(VimMode.Normal, e.Mode);
        e.TypeKeys("u");
        Assert.Equal(Code, Text(h));
    }

    private static string? Unnamed(VimEngine e) => e.Session.Registers.ForRead('"') is { IsEmpty: false } r ? r.ToText() : null;

    [Fact]
    public void Inner_objects_change_and_yank_their_inner_range()
    {
        var (e, h) = Create();
        h.Caret = new TextPosition(1, 20);                 // in "int b"
        e.TypeKeys("yia");
        Assert.Equal("int b", Unnamed(e));
        h.Caret = new TextPosition(5, 9);
        e.TypeKeys("yic");
        Assert.StartsWith("{\n    void f(int a", Unnamed(e));
        h.Caret = new TextPosition(1, 13);                 // in "int a"
        e.TypeKeys("cianame<Esc>");
        Assert.StartsWith("class A {\n    void f(name, int b) {", Text(h));
    }

    [Fact]
    public void Aa_takes_the_separator_with_the_argument()
    {
        var (e, h) = Create();
        h.Caret = new TextPosition(1, 22);                 // on "b"
        e.TypeKeys("daa");
        Assert.StartsWith("class A {\n    void f(int a) {", Text(h));
        h.Caret = new TextPosition(2, 10);                 // on "1" of g(1, 2)
        e.TypeKeys("daa");
        Assert.Contains("        g(2);", Text(h));
    }

    [Fact]
    public void A_count_takes_the_count_th_enclosing_object_and_fails_when_there_are_not_that_many()
    {
        var (e, h) = Create();
        h.Caret = new TextPosition(2, 8);
        e.TypeKeys("y2af");                                // one function encloses the cursor: nothing happens (Vim beeps)
        Assert.Equal(Code, Text(h));
        Assert.Null(Unnamed(e));
        Assert.Equal(VimMode.Normal, e.Mode);
        var both = new[] { R(0, 0, 7, 1), R(1, 4, 3, 5) }; // a function in a function
        h.SyntaxRanges = (_, _) => both;
        e.TypeKeys("y2af");
        Assert.Equal(Code, Unnamed(e));
    }

    /// <summary>Functions of the current text: a line "    void ..." to the next line "    }" (what a tree would answer after edits).</summary>
    private static IReadOnlyList<VimSyntaxRange>? LiveFunctions(IVimBuffer b)
    {
        var result = new List<VimSyntaxRange>();
        for (var i = 0; i < b.LineCount; i++)
        {
            if (!b.GetLine(i).StartsWith("    void", StringComparison.Ordinal)) continue;
            for (var j = i + 1; j < b.LineCount; j++)
                if (b.GetLine(j) == "    }") { result.Add(R(i, 4, j, 5)); break; }
        }
        return result;
    }

    [Fact]
    public void Dot_repeats_the_syntax_text_object_and_looks_ahead_when_nothing_encloses_the_cursor()
    {
        var (e, h) = Create();
        h.SyntaxRanges = (_, _) => LiveFunctions(h.Buffer);
        h.Caret = new TextPosition(0, 2);                  // on "class": no function encloses it, the next one is taken
        e.TypeKeys("daf");
        Assert.Equal("class A {\n    void h() {\n        return;\n    }\n}", Text(h));
        e.TypeKeys(".");                                   // and again, now that h is the one at the cursor
        Assert.Equal("class A {\n}", Text(h));
        Assert.Equal(2, h.SyntaxRequests.Count);
    }

    // ---- Visual mode ----

    [Fact]
    public void In_Visual_mode_the_object_is_selected_and_repeating_grows_it()
    {
        var (e, h) = Create();
        h.Caret = new TextPosition(2, 10);                 // on "1"
        e.TypeKeys("via");
        Assert.Equal(VimMode.Visual, e.Mode);
        Assert.Equal(new TextPosition(2, 10), h.Selection.Anchor);
        Assert.Equal(new TextPosition(2, 10), h.Selection.Active);
        e.TypeKeys("af");                                  // the function around the argument
        Assert.Equal(new TextPosition(1, 4), h.Selection.Anchor);
        Assert.Equal(new TextPosition(3, 4), h.Selection.Active);
        e.TypeKeys("ac");                                  // and the class around that
        Assert.Equal(new TextPosition(0, 0), h.Selection.Anchor);
        Assert.Equal(new TextPosition(7, 0), h.Selection.Active);
        e.TypeKeys("d");
        Assert.Equal("", Text(h));
    }

    // ---- motions ----

    [Fact]
    public void Bracket_m_moves_between_function_starts_and_ends_with_counts_and_operators()
    {
        var (e, h) = Create();
        e.TypeKeys("]m");
        Assert.Equal(new TextPosition(1, 4), e.Cursor);
        e.TypeKeys("]m");
        Assert.Equal(new TextPosition(4, 4), e.Cursor);
        e.TypeKeys("[m");
        Assert.Equal(new TextPosition(1, 4), e.Cursor);
        e.TypeKeys("]M");
        Assert.Equal(new TextPosition(3, 4), e.Cursor);
        e.TypeKeys("gg2]m");
        Assert.Equal(new TextPosition(4, 4), e.Cursor);
        e.TypeKeys("[M");
        Assert.Equal(new TextPosition(3, 4), e.Cursor);
        e.TypeKeys("``");                                  // ]m [m are jumps
        Assert.Equal(new TextPosition(4, 4), e.Cursor);
        e.TypeKeys("gg]m]m]m");                            // no third function: stays at the last one
        Assert.Equal(new TextPosition(4, 4), e.Cursor);

        h.Caret = new TextPosition(4, 4);
        e.TypeKeys("d]M");                                 // to the end of the function, inclusive (whole lines, as Vim's delete makes it)
        Assert.Equal("class A {\n    void f(int a, int b) {\n        g(1, 2);\n    }\n}", Text(h));
        h.Caret = new TextPosition(1, 9);
        e.TypeKeys("y]M");                                 // a yank inside a line keeps its characters
        Assert.Equal("f(int a, int b) {\n        g(1, 2);\n    }", Unnamed(e));
    }

    // ---- no tree, asynchronous answers ----

    [Fact]
    public void Without_a_syntax_tree_the_command_says_so_once_and_a_macro_stops_there()
    {
        var (e, h) = Create(tree: false);
        h.Caret = new TextPosition(2, 8);
        e.TypeKeys("daf");
        Assert.Equal(Code, Text(h));
        Assert.Equal(VimMode.Normal, e.Mode);
        Assert.Contains("No syntax tree", e.LastError, StringComparison.Ordinal);
        h.Messages.Clear();
        e.TypeKeys("qqdafjq");
        h.Messages.Clear();
        e.TypeKeys("5@q");                                 // the first failure ends the macro: one message, not five
        Assert.Single(h.Messages, m => m.Kind == VimMessageKind.Error);
        Assert.Equal(Code, Text(h));
        var before = e.Cursor;
        e.TypeKeys("3]m");
        Assert.Equal(before, e.Cursor);
    }

    [Fact]
    public void A_late_answer_resumes_the_command_and_the_keys_typed_meanwhile_run_after_it_in_order()
    {
        var (e, h) = Create(deferred: true);
        var pending = new List<bool>();
        using var sub = e.PendingWorkChanged.Subscribe(pending.Add);
        h.Caret = new TextPosition(2, 8);
        Keys(e, "daf");
        Assert.True(e.IsAwaitingSyntax);
        Assert.False(e.HasPendingWork);                    // nothing for the host to poll: the answer resumes it
        Keys(e, "ix<Esc>");                                // typed while the tree is being asked
        Assert.Equal(Code, Text(h));
        Assert.Equal(1, h.PendingSyntaxAnswers);

        h.AnswerSyntaxRequests();
        Assert.False(e.IsAwaitingSyntax);
        Assert.False(e.HasPendingWork);
        Assert.Equal("class A {\n    xvoid h() {\n        return;\n    }\n}", Text(h));
        Assert.Equal(VimMode.Normal, e.Mode);
        e.TypeKeys("uu");
        Assert.Equal(Code, Text(h));
    }

    [Fact]
    public void No_answer_in_time_ends_the_command_with_a_message_and_a_late_answer_is_ignored()
    {
        var (e, h) = Create(deferred: true);
        h.Caret = new TextPosition(2, 8);
        Keys(e, "dafx");
        Assert.True(e.IsAwaitingSyntax);
        h.ManualTimer!.Advance(VimEngine.SyntaxRequestTimeout + TimeSpan.FromMilliseconds(1));
        Assert.False(e.IsAwaitingSyntax);
        Assert.Contains("did not answer", e.LastError, StringComparison.Ordinal);
        e.Drain();
        Assert.Equal("class A {\n    void f(int a, int b) {\n        (1, 2);\n    }\n    void h() {\n        return;\n    }\n}", Text(h)); // only x ran
        h.AnswerSyntaxRequests();
        Assert.Contains("        (1, 2);", Text(h));
        Assert.Contains("    void f(", Text(h));
    }

    [Fact]
    public void Ctrl_C_abandons_a_waiting_command_and_text_changed_meanwhile_is_not_cut_by_old_ranges()
    {
        var (e, h) = Create(deferred: true);
        h.Caret = new TextPosition(2, 8);
        Keys(e, "daf");
        e.HandleKey(3);
        Assert.False(e.IsAwaitingSyntax);
        h.AnswerSyntaxRequests();
        Assert.Equal(Code, Text(h));

        Keys(e, "daf");
        h.Buffer.ApplyEdit(new VimEdit(new TextPosition(0, 0), new TextPosition(0, 0), "// x\n")); // someone else edits meanwhile
        h.AnswerSyntaxRequests();
        Assert.Equal("// x\n" + Code, Text(h));
        Assert.Contains("changed", e.LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void After_ctrl_o_insert_mode_resumes_once_the_object_is_done_or_failed(bool deferred)
    {
        var (e, h) = Create(deferred: deferred);
        h.Caret = new TextPosition(1, 13);                 // in "int a"
        Keys(e, "i<C-o>diay");                             // the y is typed while (deferred) the tree is asked
        h.AnswerSyntaxRequests();
        e.Drain();
        Assert.Equal(VimMode.Insert, e.Mode);
        Assert.StartsWith("class A {\n    void f(y, int b) {", Text(h));

        h.Caret = new TextPosition(0, 0);
        Keys(e, "<Esc>i<C-o>3]mz");                        // no third function: the motion fails, Insert mode comes back
        h.AnswerSyntaxRequests();
        e.Drain();
        Assert.Equal(VimMode.Insert, e.Mode);
        Assert.StartsWith("zclass A {", Text(h));
    }

    [Fact]
    public void Inside_normal_and_g_only_an_immediate_answer_is_used_and_a_late_one_ends_the_command_without_hanging()
    {
        var (e, h) = Create();
        h.SyntaxRanges = (_, _) => LiveFunctions(h.Buffer);
        e.TypeKeys(":3normal daf<CR>");                    // a host that answers at once: served inside :normal
        Assert.Equal("class A {\n    void h() {\n        return;\n    }\n}", Text(h));

        var (late, lh) = Create(deferred: true);
        lh.Caret = new TextPosition(2, 8);
        late.TypeKeys(":normal daf<CR>");                  // nothing can wait inside :normal: a message, no hang, nothing deleted
        Assert.Equal(Code, Text(lh));
        Assert.Contains("inside :normal", late.LastError, StringComparison.Ordinal);
        Assert.False(late.IsAwaitingSyntax);
        late.TypeKeys(":g/return/normal daf<CR>");
        Assert.Equal(Code, Text(lh));
        lh.AnswerSyntaxRequests();                         // the late answers are ignored
        Assert.Equal(Code, Text(lh));
        Assert.Equal(0, lh.ManualTimer!.PendingCount);     // and no timeout is left behind
    }

    // ---- the choice, directly ----

    [Fact]
    public void The_enclosing_object_is_the_smallest_one_holding_the_whole_selection()
    {
        IReadOnlyList<VimSyntaxRange> ranges = [R(0, 0, 9, 0), R(1, 0, 4, 1), R(2, 2, 3, 3), R(6, 0, 7, 1)];
        Assert.Equal(R(2, 2, 3, 3), VimEngine.EnclosingSyntaxRange(ranges, new(2, 5), new(2, 5), 1, strictlyLarger: false));
        Assert.Equal(R(1, 0, 4, 1), VimEngine.EnclosingSyntaxRange(ranges, new(2, 5), new(2, 5), 2, strictlyLarger: false));
        Assert.Equal(R(0, 0, 9, 0), VimEngine.EnclosingSyntaxRange(ranges, new(2, 5), new(2, 5), 3, strictlyLarger: false));
        Assert.Null(VimEngine.EnclosingSyntaxRange(ranges, new(2, 5), new(2, 5), 4, strictlyLarger: false));
        Assert.Equal(R(1, 0, 4, 1), VimEngine.EnclosingSyntaxRange(ranges, new(2, 2), new(3, 2), 1, strictlyLarger: true)); // grows past itself
        Assert.Equal(R(6, 0, 7, 1), VimEngine.EnclosingSyntaxRange([R(1, 0, 4, 1), R(6, 0, 7, 1)], new(5, 0), new(5, 0), 1, strictlyLarger: false)); // lookahead
        Assert.Null(VimEngine.EnclosingSyntaxRange([R(1, 0, 4, 1)], new(5, 0), new(5, 0), 1, strictlyLarger: false));
    }
}
