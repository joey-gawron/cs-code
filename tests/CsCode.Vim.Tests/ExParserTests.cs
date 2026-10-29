using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>Ex command parsing: ranges, flags, delimiters and the error messages Vim users rely on.</summary>
public class ExParserTests
{
    private const string Five = "one\ntwo\nthree\nfour\nfive";

    private static (VimEngine Engine, InMemoryVimHost Host) Create(string text = Five)
    {
        var host = new InMemoryVimHost(text);
        return (new VimEngine(host, new VimSession()), host);
    }

    private static string Text(InMemoryVimHost h) => string.Join("\n", Enumerable.Range(0, h.Buffer.LineCount).Select(h.Buffer.GetLine));

    private static string Run(string ex, string text = Five, string pre = "")
    {
        var (e, h) = Create(text);
        if (pre.Length > 0) e.TypeKeys(pre);
        e.ExecuteEx(ex);
        return Text(h);
    }

    // ---- ranges

    [Theory]
    [InlineData("2d", "one\nthree\nfour\nfive")]
    [InlineData("2,3d", "one\nfour\nfive")]
    [InlineData("%d", "")]
    [InlineData("$d", "one\ntwo\nthree\nfour")]
    [InlineData(".d", "two\nthree\nfour\nfive")]
    [InlineData(".,+1d", "three\nfour\nfive")]
    [InlineData(".,$d", "")]
    [InlineData("1;+2d", "four\nfive")]
    [InlineData("2,/four/d", "one\nfive")]
    [InlineData("/two/,/four/d", "one\nfive")]
    [InlineData("/three/;+1d", "one\ntwo\nfive")]
    [InlineData("$-1,$d", "one\ntwo\nthree")]
    [InlineData("0d", "two\nthree\nfour\nfive")]
    [InlineData(",+1d", "three\nfour\nfive")]
    [InlineData("d 2", "three\nfour\nfive")]
    [InlineData("2d 2", "one\nfour\nfive")]
    public void Ranges_select_the_expected_lines(string ex, string expected) => Assert.Equal(expected, Run(ex));

    [Fact]
    public void Marks_and_visual_marks_are_addresses()
    {
        var (e, h) = Create();
        e.TypeKeys("jmajjmb");
        e.ExecuteEx("'a,'bd");
        Assert.Equal("one\nfive", Text(h));
        var (e2, h2) = Create();
        e2.TypeKeys("jVj<Esc>");
        e2.ExecuteEx("'<,'>d");
        Assert.Equal("one\nfour\nfive", Text(h2));
        var (e3, h3) = Create();
        e3.TypeKeys("jVj:d<CR>");   // ":" from Visual mode starts with '<,'>
        Assert.Equal("one\nfour\nfive", Text(h3));
    }

    [Fact]
    public void Backward_search_addresses_wrap()
    {
        Assert.Equal("one\ntwo\nthree\nfive", Run("?four?d", pre: "G"));
        Assert.Equal("two\nthree\nfour\nfive", Run("?one?d", pre: "jj"));
    }

    [Theory]
    [InlineData("100d", "E16")]
    [InlineData("2,100d", "E16")]
    [InlineData("'zd", "E20")]
    [InlineData("/nomatch/d", "E486")]
    [InlineData("?nomatch?d", "E486")]
    [InlineData("foo", "E492")]
    [InlineData("2foo", "E492")]
    [InlineData("s/nomatch/x/", "E486")]
    [InlineData("g/nomatch/d", "E486")]
    [InlineData("v/./d", "E538")]
    [InlineData("normal", "E471")]
    [InlineData("pu z", "E353")]
    [InlineData("undo 99", "E830")]
    [InlineData("2,3m2", "E134")]
    [InlineData("set nosuchoption", "E518")]
    [InlineData("set sw=abc", "E521")]
    public void Errors_carry_vims_codes(string ex, string code)
    {
        var (e, h) = Create();
        var ok = e.ExecuteEx(ex);
        Assert.False(ok, ex);
        Assert.StartsWith(code, e.LastError);
        Assert.Equal(Five, Text(h));   // a failing command changes nothing
    }

    // ---- :s flags and delimiters

    [Theory]
    [InlineData("s/o/0/", "0ne\ntwo\nthree\nfour\nfive")]
    [InlineData("s/o/0/g", "0ne\ntwo\nthree\nfour\nfive")]
    [InlineData("%s/o/0/", "0ne\ntw0\nthree\nf0ur\nfive")]
    [InlineData("%s/o/0/g", "0ne\ntw0\nthree\nf0ur\nfive")]
    [InlineData("%s/E/E/gi", "onE\ntwo\nthrEE\nfour\nfivE")]
    [InlineData("%s/e/_/g", "on_\ntwo\nthr__\nfour\nfiv_")]
    [InlineData("%s#o#/#", "/ne\ntw/\nthree\nf/ur\nfive")]
    [InlineData("%s,o,0,", "0ne\ntw0\nthree\nf0ur\nfive")]
    [InlineData("%s!o!0!", "0ne\ntw0\nthree\nf0ur\nfive")]
    [InlineData("%s+o+0+", "0ne\ntw0\nthree\nf0ur\nfive")]
    [InlineData("%s/\\(t\\)\\(.\\)/\\2\\1/", "one\nwto\nhtree\nfour\nfive")]
    [InlineData("%s/\\v(t)(.)/\\2\\1/", "one\nwto\nhtree\nfour\nfive")]
    [InlineData("%s/\\<./\\u&/", "One\nTwo\nThree\nFour\nFive")]
    [InlineData("%s/.*/\\U&/", "ONE\nTWO\nTHREE\nFOUR\nFIVE")]
    [InlineData("%s/o/\\r/", "\nne\ntw\n\nthree\nf\nur\nfive")]
    [InlineData("%s/e\\n/-/", "on-two\nthre-four\nfiv-")]
    [InlineData("2,3s/$/;/", "one\ntwo;\nthree;\nfour\nfive")]
    [InlineData("%s/^/> /", "> one\n> two\n> three\n> four\n> five")]
    [InlineData("s/one/x/ | s/x/y/", "y\ntwo\nthree\nfour\nfive")]
    [InlineData("%s/o/0/n", Five)]
    [InlineData("%s/zzz/0/e", Five)]
    public void Substitute_flags_and_delimiters(string ex, string expected) => Assert.Equal(expected, Run(ex));

    [Fact]
    public void Ampersand_repeats_the_last_substitute_with_its_flags_when_asked()
    {
        var (e, h) = Create("aaa\naaa\naaa");
        e.ExecuteEx("s/a/b/");
        e.TypeKeys("j");
        e.ExecuteEx("&");
        Assert.Equal("baa\nbaa\naaa", Text(h));
        e.TypeKeys("j");
        e.ExecuteEx("&&");
        Assert.Equal("baa\nbaa\nbaa", Text(h));
        e.ExecuteEx("%&g");
        Assert.Equal("bbb\nbbb\nbbb", Text(h));
        e.ExecuteEx("s/b/c/g");
        e.ExecuteEx("~");    // last pattern (b -> c) with the last replacement
        Assert.Contains("c", Text(h));
    }

    [Fact]
    public void Confirmation_is_interactive_and_cooperative()
    {
        var (e, h) = Create("foo foo\nfoo");
        e.TypeKeys(":%s/foo/X/gc<CR>");
        Assert.Equal("r?", e.ModeCode);
        Assert.False(e.HasPendingWork);   // waits for a key, does not spin
        e.TypeKeys("y");
        Assert.Equal("X foo\nfoo", Text(h));
        e.TypeKeys("n");
        Assert.Equal("X foo\nfoo", Text(h));
        e.TypeKeys("a");
        Assert.Equal("X foo\nX", Text(h));
        Assert.Equal("n", e.ModeCode);
        e.TypeKeys("u");
        Assert.Equal("foo foo\nfoo", Text(h));   // one undo step for the whole confirmed command
    }

    [Fact]
    public void Escape_quits_a_confirmation_and_keeps_what_was_substituted()
    {
        var (e, h) = Create("foo foo foo");
        e.TypeKeys(":s/foo/X/gc<CR>yy<Esc>");
        Assert.Equal("X X foo", Text(h));
        Assert.Equal("n", e.ModeCode);
    }

    // ---- :g, :normal, :put and friends

    [Theory]
    [InlineData("g/o/d", "three\nfive")]
    [InlineData("v/o/d", "one\ntwo\nfour")]
    [InlineData("g!/o/d", "one\ntwo\nfour")]
    [InlineData("g/o/s/o/0/", "0ne\ntw0\nthree\nf0ur\nfive")]
    [InlineData("g/o/normal Ax", "onex\ntwox\nthree\nfourx\nfive")]
    [InlineData("g/^/m0", "five\nfour\nthree\ntwo\none")]
    [InlineData("g/o/t.", "one\none\ntwo\ntwo\nthree\nfour\nfour\nfive")]
    [InlineData("g/e/j", "one two\nthree four\nfive")]
    [InlineData("2,4g/o/d", "one\nthree\nfive")]
    [InlineData("g/o/.,+1d", "three")]
    [InlineData("g#o#d", "three\nfive")]
    public void Global_commands(string ex, string expected) => Assert.Equal(expected, Run(ex));

    [Fact]
    public void Global_is_one_undo_step()
    {
        var (e, h) = Create();
        e.TypeKeys(":g/o/d<CR>");
        Assert.Equal("three\nfive", Text(h));
        e.TypeKeys("u");
        Assert.Equal(Five, Text(h));
        e.TypeKeys("<C-r>");
        Assert.Equal("three\nfive", Text(h));
    }

    [Theory]
    [InlineData("normal Ax", "onex\ntwo\nthree\nfour\nfive")]
    [InlineData("%normal Ax", "onex\ntwox\nthreex\nfourx\nfivex")]
    [InlineData("2,3normal x", "one\nwo\nhree\nfour\nfive")]
    [InlineData("%normal dd", "")]
    [InlineData("%normal 0x", "ne\nwo\nhree\nour\nive")]
    [InlineData("normal 2dd", "three\nfour\nfive")]
    [InlineData("normal! Ax", "onex\ntwo\nthree\nfour\nfive")]
    [InlineData("%norm A!", "one!\ntwo!\nthree!\nfour!\nfive!")]
    public void Normal_executes_keys(string ex, string expected) => Assert.Equal(expected, Run(ex));

    [Theory]
    [InlineData("2y|pu", "one\ntwo\ntwo\nthree\nfour\nfive")]
    [InlineData("2,3y|$pu", "one\ntwo\nthree\nfour\nfive\ntwo\nthree")]
    [InlineData("2,3y|0pu", "two\nthree\none\ntwo\nthree\nfour\nfive")]
    [InlineData("2,3y|pu!", "two\nthree\none\ntwo\nthree\nfour\nfive")]
    [InlineData("2d|pu", "one\nthree\ntwo\nfour\nfive")]
    [InlineData("2m0", "two\none\nthree\nfour\nfive")]
    [InlineData("2,3m$", "one\nfour\nfive\ntwo\nthree")]
    [InlineData("1m$", "two\nthree\nfour\nfive\none")]
    [InlineData("2t.", "one\ntwo\ntwo\nthree\nfour\nfive")]
    [InlineData("2t0", "two\none\ntwo\nthree\nfour\nfive")]
    [InlineData("2,3t$", "one\ntwo\nthree\nfour\nfive\ntwo\nthree")]
    [InlineData("j", "one two\nthree\nfour\nfive")]
    [InlineData("1,3j", "one two three\nfour\nfive")]
    [InlineData("1,3j!", "onetwothree\nfour\nfive")]
    [InlineData("2>", "one\n\ttwo\nthree\nfour\nfive")]
    [InlineData("2,3>>", "one\n\t\ttwo\n\t\tthree\nfour\nfive")]
    public void Line_commands(string ex, string expected) => Assert.Equal(expected, Run(ex));

    [Fact]
    public void Registers_for_ex_commands()
    {
        var (e, h) = Create();
        e.ExecuteEx("2,3y a");
        e.ExecuteEx("1d b");
        e.ExecuteEx("$pu a");
        Assert.Equal("two\nthree\nfour\nfive\ntwo\nthree", Text(h));
        e.ExecuteEx("2d A");
        Assert.Equal("two\nfour\nfive\ntwo\nthree", Text(h));
        Assert.Equal(VimRegisterType.Line, e.Session.Registers.ForRead('a')!.Type);
        Assert.Equal(["two", "three", "three"], e.Session.Registers.ForRead('a')!.Lines);   // yanked two lines, appended the deleted one
        Assert.Equal(["one"], e.Session.Registers.ForRead('b')!.Lines);
    }

    [Fact]
    public void Undo_and_redo_commands()
    {
        var (e, h) = Create();
        e.TypeKeys("xjxjx");
        e.ExecuteEx("undo");
        Assert.Equal("ne\nwo\nthree\nfour\nfive", Text(h));
        e.ExecuteEx("undo 0");
        Assert.Equal(Five, Text(h));
        e.ExecuteEx("redo");
        Assert.Equal("ne\ntwo\nthree\nfour\nfive", Text(h));
        e.ExecuteEx("undo 3");
        Assert.Equal("ne\nwo\nhree\nfour\nfive", Text(h));
        e.ExecuteEx("earlier 2");
        Assert.Equal("ne\ntwo\nthree\nfour\nfive", Text(h));
        e.ExecuteEx("later 1");
        Assert.Equal("ne\nwo\nthree\nfour\nfive", Text(h));
    }

    [Fact]
    public void Set_command_forms()
    {
        var (e, _) = Create();
        Assert.True(e.ExecuteEx("set sw=4 ts=4 et"));
        Assert.Equal(4, e.Options.ShiftWidth);
        Assert.True(e.Options.ExpandTab);
        Assert.True(e.ExecuteEx("set noet"));
        Assert.False(e.Options.ExpandTab);
        Assert.True(e.ExecuteEx("set invet"));
        Assert.True(e.Options.ExpandTab);
        Assert.True(e.ExecuteEx("set et!"));
        Assert.False(e.Options.ExpandTab);
        Assert.True(e.ExecuteEx("set sw&"));
        Assert.Equal(8, e.Options.ShiftWidth);
        Assert.True(e.ExecuteEx("set ic scs hls"));
        Assert.True(e.Options.IgnoreCase && e.Options.SmartCase && e.Options.HlSearch);
        Assert.True(e.ExecuteEx("setlocal ts=2"));
        Assert.Equal(2, e.Options.TabStop);
        Assert.True(e.ExecuteEx("set sw?"));
        Assert.True(e.ExecuteEx("let &sw=3"));
        Assert.Equal(3, e.Options.ShiftWidth);
        Assert.True(e.ExecuteEx("let &l:sw=5"));
        Assert.Equal(5, e.Options.ShiftWidth);
    }

    [Fact]
    public void Bar_separates_commands_but_not_inside_patterns_or_global()
    {
        Assert.Equal("one\nfour\nfive", Run("2d|2d"));
        Assert.Equal("0ne\ntwo\nthree\nfour\nfive", Run("s/o\\|x/0/"));   // \| inside the pattern is alternation
        Assert.Equal("Y\nY", Run("g/o/s/o/X/|s/X/Y/", "o\no"));      // the bar belongs to the :g command
        Assert.Equal("a|b", Run("s/x/|/", "axb"));                    // and to the replacement of :s
    }

    [Fact]
    public void Lets_registers_and_expressions()
    {
        var (e, h) = Create("x");
        e.ExecuteEx("let @a='hello'");
        e.TypeKeys("\"ap");
        Assert.Equal("xhello", Text(h));
        e.ExecuteEx("let @a=@a . ' world'");
        e.TypeKeys("\"ap");
        Assert.Contains("world", Text(h));
        e.ExecuteEx("let @/='ell'");
        e.TypeKeys("gg0n");
        Assert.Equal(2, e.Cursor.Character);
    }

    [Fact]
    public void Source_reports_errors_per_line_and_continues()
    {
        var session = new VimSession();
        var host = new InMemoryVimHost("abc");
        using var e = new VimEngine(host, session);
        var errors = e.LoadScript("set sw=2\nbogus\n\" comment\nnnoremap Q x\nset nosuch\n  nnoremap R \\\n  \\ dd\n", "test");
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("line 2:", errors[0]);
        Assert.StartsWith("line 5:", errors[1]);
        Assert.Equal(2, session.Options.ShiftWidth);
        Assert.Equal(2, session.Mappings.Items.Count);
    }

    [Fact]
    public void Errors_inside_a_macro_stop_it_but_not_silent_ones()
    {
        var (e, h) = Create("a1\na2\na3");
        e.ExecuteEx("let @q=\":s/zzz/y/\\<CR>x\"");
        e.TypeKeys("@q");
        Assert.Equal("a1\na2\na3", Text(h));    // the failing :s aborted the macro before the x
        e.ExecuteEx("let @q=\":silent! s/zzz/y/\\<CR>x\"");
        e.TypeKeys("@q");
        Assert.Equal("1\na2\na3", Text(h));     // silent! lets it continue
    }
}
