using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>A buffer that checks every edit the engine makes: positions inside the document, never inside a surrogate pair, ranges ordered and disjoint.</summary>
public sealed class ValidatingVimBuffer(LineVimBuffer inner) : IVimBuffer
{
    public long EditCount { get; private set; }
    public string Name => inner.Name;
    public Uri? Uri => inner.Uri;
    public string LanguageId => inner.LanguageId;
    public int Version => inner.Version;
    public int LineCount => inner.LineCount;
    public string EndOfLine => inner.EndOfLine;
    public bool IsReadOnly => inner.IsReadOnly;
    public string GetLine(int line) => inner.GetLine(line);
    public string GetText() => inner.GetText();

    private void Check(TextPosition p, string what)
    {
        if (p.Line < 0 || p.Line >= inner.LineCount) throw new InvalidOperationException($"{what}: line {p.Line} outside the document ({inner.LineCount} lines)");
        var line = inner.GetLine(p.Line);
        if (p.Character < 0 || p.Character > line.Length) throw new InvalidOperationException($"{what}: column {p.Character} outside line {p.Line} (length {line.Length})");
        if (p.Character > 0 && p.Character < line.Length && char.IsLowSurrogate(line[p.Character]) && char.IsHighSurrogate(line[p.Character - 1]))
            throw new InvalidOperationException($"{what}: position {p.Line}:{p.Character} splits a surrogate pair");
    }

    private void Validate(ReadOnlySpan<VimEdit> edits)
    {
        for (var i = 0; i < edits.Length; i++)
        {
            Check(edits[i].Start, "edit start");
            Check(edits[i].End, "edit end");
            if (edits[i].End < edits[i].Start) throw new InvalidOperationException("edit end before start");
            if (edits[i].Text.Contains("\r\n", StringComparison.Ordinal)) throw new InvalidOperationException("edit text contains a CRLF pair");
        }
        for (var i = 0; i < edits.Length; i++)
            for (var j = i + 1; j < edits.Length; j++)
            {
                var (a, b) = edits[i].Start <= edits[j].Start ? (edits[i], edits[j]) : (edits[j], edits[i]);
                if (b.Start < a.End) throw new InvalidOperationException("overlapping edits");
            }
    }

    public bool ApplyEdit(in VimEdit edit)
    {
        Validate([edit]);
        EditCount++;
        return inner.ApplyEdit(edit);
    }

    public bool ApplyEdits(ReadOnlySpan<VimEdit> edits)
    {
        Validate(edits);
        EditCount += edits.Length;
        return inner.ApplyEdits(edits);
    }

}

public class FuzzTests
{
    private static readonly string[] Atoms =
    [
        "h", "j", "k", "l", "w", "W", "b", "B", "e", "E", "0", "^", "$", "G", "gg", "%", "{", "}", "(", ")", "H", "M", "L", "fa", "Fa", "ta", ";", ",", "*", "#", "n", "N",
        "x", "X", "dd", "dw", "d$", "D", "yy", "yw", "p", "P", "J", "gJ", "~", "rx", "R", "cw", "cc", "C", "s", "S", ">>", "<<", "==", "gUU", "guu", "g~~", "gqq", "gww",
        "i", "a", "I", "A", "o", "O", "gi", "gI", "<Esc>", "u", "<C-r>", "U", ".", "q", "@", "@@", "ma", "'a", "`a", "mb", "'b", "v", "V", "<C-v>", "o", "O", "gv",
        "iw", "aw", "i(", "a(", "i\"", "a\"", "ip", "ap", "it", "at", "i{", "2", "3", "5", "10", "0", "\"a", "\"A", "\"_", "\"+", "<C-a>", "<C-x>", "<C-o>", "<C-i>", "g;", "g,",
        "ab", "foo", " ", "\t", "(", ")", "\"", "{", "}", "<BS>", "<CR>", "<C-w>", "<C-u>", "<C-r>a", "<C-v>u00e9", "<C-n>", "<C-h>", "😀", "é", "中",
        ":s/a/b/g<CR>", ":%s/o/0/<CR>", ":g/a/d<CR>", ":v/a/m0<CR>", ":%norm Ax<CR>", ":2,3j<CR>", ":m0<CR>", ":t.<CR>", ":>><CR>", ":d<CR>", ":undo<CR>", ":redo<CR>",
        ":normal! dd<CR>", ":2<CR>", ":$<CR>", ":'a,'bd<CR>", ":%s/\\n//<CR>", ":%s/\\(.\\)\\(.\\)/\\2\\1/<CR>", ":sort<CR>", ":%j<CR>", ":put<CR>", ":noh<CR>",
        "/a<CR>", "?b<CR>", "/\\v(a|b)+<CR>", "/\\n<CR>", ":", "/", "?", "<C-c>", "<C-[>",
        "ysiw)", "ds)", "cs)]", "gcc", "gcip", "S(",
    ];

    private static readonly string[] Alphabet = ["a", "b", "foo", " ", "  ", "\t", "(", ")", "\"", "{", "}", "é", "中", "😀", "x1", "12", "ab", "O", ".", ",", "-", "_"];

    private static string RandomText(Random r)
    {
        var lines = r.Next(1, 8);
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            var words = r.Next(0, 8);
            for (var w = 0; w < words; w++) sb.Append(Alphabet[r.Next(Alphabet.Length)]);
            if (i < lines - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatSeed(int seed, string text, string keys) => $"seed {seed}\nbuffer: {text.Replace("\n", "\\n").Replace("\t", "\\t")}\nkeys: {keys}";

    private static void CheckEngine(VimEngine engine, ValidatingVimBuffer buf, string context)
    {
        var c = engine.Cursor;
        Assert.True(c.Line >= 0 && c.Line < buf.LineCount, $"cursor line {c.Line} outside the buffer ({buf.LineCount} lines)\n{context}");
        var line = buf.GetLine(c.Line);
        Assert.True(c.Character >= 0 && c.Character <= line.Length, $"cursor column {c.Character} outside line of length {line.Length}\n{context}");
        if (c.Character > 0 && c.Character < line.Length)
            Assert.False(char.IsLowSurrogate(line[c.Character]) && char.IsHighSurrogate(line[c.Character - 1]), $"cursor inside a surrogate pair\n{context}");
        Assert.True(buf.LineCount >= 1, "the buffer lost its last line");
        for (var i = 0; i < buf.LineCount; i++)
            Assert.DoesNotContain('\n', buf.GetLine(i));
    }

    [Fact]
    public void Key_soup_never_throws_or_corrupts_the_buffer()
    {
        const int sequences = 10_000;
        const int keysPerSequence = 50;
        var session = new VimSession();
        for (var seed = 0; seed < sequences; seed++)
        {
            var r = new Random(seed);
            var text = RandomText(r);
            var inner = new LineVimBuffer(text);
            var buf = new ValidatingVimBuffer(inner);
            var host = new InMemoryVimHost(buf);
            using var engine = new VimEngine(host, session);
            var sb = new StringBuilder();
            for (var i = 0; i < keysPerSequence; i++) sb.Append(Atoms[r.Next(Atoms.Length)]);
            var keys = sb.ToString();
            try
            {
                engine.TypeKeys(keys);
                engine.Drain();
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"engine threw {ex.GetType().Name}: {ex.Message}\n{FormatSeed(seed, text, keys)}\n{ex.StackTrace}");
            }
            CheckEngine(engine, buf, FormatSeed(seed, text, keys));
            // leave whatever mode the soup ended in: Esc must always get back to Normal mode
            engine.Reset();
            Assert.Equal(VimMode.Normal, engine.Mode);
            if (seed % 97 == 0) session.Registers.Clear();
        }
    }

    [Fact]
    public void Key_soup_in_a_fresh_session_leaves_registers_and_marks_consistent()
    {
        for (var seed = 20_000; seed < 20_400; seed++)
        {
            var r = new Random(seed);
            var text = RandomText(r);
            var host = new InMemoryVimHost(new ValidatingVimBuffer(new LineVimBuffer(text)));
            using var engine = new VimEngine(host, new VimSession());
            var keys = string.Concat(Enumerable.Range(0, 80).Select(_ => Atoms[r.Next(Atoms.Length)]));
            engine.TypeKeys(keys);
            engine.Drain();
            foreach (var m in "abcdefghijklmnopqrstuvwxyz[]<>.^'")
                if (engine.GetMarkPosition(m) is { } p)
                    Assert.True(p.Line >= 0 && p.Line < host.Buffer.LineCount + 1, $"mark {m} at {p} is outside the buffer\n{FormatSeed(seed, text, keys)}");
            foreach (var reg in "\"0123456789-abcdefghijklmnopqrstuvwxyz")
            {
                var rr = engine.Session.Registers.ForRead(reg);
                if (rr is null) continue;
                Assert.NotNull(rr.Lines);
                Assert.All(rr.Lines, l => Assert.NotNull(l));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // properties
    // ------------------------------------------------------------------------------------------------------------

    private static readonly string[] ChangeCommands =
    [
        "x", "X", "dw", "dd", "D", "J", "~", "rZ", "cwQ<Esc>", "ccQ<Esc>", "C!<Esc>", "s!<Esc>", ">>", "<<", "gUU", "guu", "g~~", "ihi<Esc>", "ahi<Esc>", "Ihi<Esc>", "Ahi<Esc>",
        "ohi<Esc>", "Ohi<Esc>", "p", "P", "yyp", "yyP", "dap", "dip", "ciwQ<Esc>", "daw", "d$", "d0", "dG", "dgg", "dj", "dk", "yiwP", "<C-a>", "<C-x>", "gJ", "&", "5x", "3dd", "2dw", "3~",
        ":s/a/b/g<CR>", ":d<CR>", ":m0<CR>", ":t.<CR>", ":j<CR>", ":>><CR>", ":%s/o/0/g<CR>", ":g/a/d<CR>", ":normal Ax<CR>",
    ];

    private static (string Text, int Line, int Col) RandomBuffer(Random r)
    {
        var text = RandomText(r);
        var lines = text.Split('\n');
        var line = r.Next(lines.Length);
        // a column on a character boundary
        var col = 0;
        if (lines[line].Length > 0)
        {
            col = r.Next(lines[line].Length);
            if (col > 0 && char.IsLowSurrogate(lines[line][col])) col--;
        }
        return (text, line, col);
    }

    private static (string Text, TextPosition Cursor) RunKeys(string text, int line, int col, string keys)
    {
        var host = new InMemoryVimHost(new ValidatingVimBuffer(new LineVimBuffer(text)));
        host.Caret = new TextPosition(line, col);
        using var engine = new VimEngine(host, new VimSession());
        engine.TypeKeys(keys);
        engine.Drain();
        return (((ValidatingVimBuffer)host.Buffer).GetText(), engine.Cursor);
    }

    [Fact]
    public void Undo_restores_the_text_after_any_single_change()
    {
        for (var seed = 0; seed < 3000; seed++)
        {
            var r = new Random(seed);
            var (text, line, col) = RandomBuffer(r);
            var cmd = ChangeCommands[r.Next(ChangeCommands.Length)];
            var (after, _) = RunKeys(text, line, col, cmd + "u");
            Assert.True(after == text, $"u after '{cmd}' did not restore the text\n{FormatSeed(seed, text, cmd)}\nafter: {after.Replace("\n", "\\n")}");
        }
    }

    [Fact]
    public void Undo_restores_the_cursor_after_commands_that_stay_on_their_line()
    {
        string[] sameLine = ["x", "X", "~", "rZ", "ihi<Esc>", "ahi<Esc>", "Ihi<Esc>", "Ahi<Esc>", "<C-a>", "D", "C!<Esc>", ">>", "<<", "gUU", "guu", "g~~"];
        for (var seed = 0; seed < 3000; seed++)
        {
            var r = new Random(seed);
            var (text, line, col) = RandomBuffer(r);
            var cmd = sameLine[r.Next(sameLine.Length)];
            var (_, before) = RunKeys(text, line, col, "");
            var (after, cursor) = RunKeys(text, line, col, cmd + "u");
            Assert.True(after == text, $"text differs after '{cmd}u'\n{FormatSeed(seed, text, cmd)}");
            // the cursor ends on the same line; the column is where the change started
            Assert.True(cursor.Line == before.Line, $"cursor moved to line {cursor.Line} after '{cmd}u' (started on {before.Line})\n{FormatSeed(seed, text, cmd)}");
        }
    }

    [Fact]
    public void Redo_after_undo_gives_the_changed_text_again()
    {
        for (var seed = 0; seed < 2000; seed++)
        {
            var r = new Random(seed);
            var (text, line, col) = RandomBuffer(r);
            var cmd = ChangeCommands[r.Next(ChangeCommands.Length)];
            var (changed, _) = RunKeys(text, line, col, cmd);
            var (redone, _) = RunKeys(text, line, col, cmd + "u<C-r>");
            Assert.True(changed == redone, $"redo after '{cmd}u' differs\n{FormatSeed(seed, text, cmd)}");
        }
    }

    [Fact]
    public void Dot_repeats_a_change_exactly_like_typing_it_again()
    {
        string[] repeatable = ["x", "X", "dw", "dd", "D", "J", "~", "rZ", "cwQ<Esc>", "ccQ<Esc>", ">>", "<<", "gUU", "guu", "ihi<Esc>", "ahi<Esc>", "Ihi<Esc>", "Ahi<Esc>", "ohi<Esc>", "Ohi<Esc>", "daw", "diw", "ciwQ<Esc>", "d$", "gJ", "p", "P", "<C-a>", "<C-x>"];
        for (var seed = 0; seed < 3000; seed++)
        {
            var r = new Random(seed);
            var (text, line, col) = RandomBuffer(r);
            var cmd = repeatable[r.Next(repeatable.Length)];
            // yank something first so p/P have a register
            var prefix = cmd is "p" or "P" ? "yw" : "";
            var (viaDot, cursorDot) = RunKeys(text, line, col, prefix + cmd + ".");
            var (retyped, cursorRetyped) = RunKeys(text, line, col, prefix + cmd + cmd);
            Assert.True(viaDot == retyped, $"'{cmd}.' differs from '{cmd}{cmd}'\n{FormatSeed(seed, text, prefix + cmd)}\ndot:     {viaDot.Replace("\n", "\\n")}\nretyped: {retyped.Replace("\n", "\\n")}");
            Assert.True(cursorDot == cursorRetyped, $"cursor after '{cmd}.' is {cursorDot}, after '{cmd}{cmd}' {cursorRetyped}\n{FormatSeed(seed, text, prefix + cmd)}");
        }
    }

    [Fact]
    public void A_count_equals_the_repeated_command_where_vim_defines_it()
    {
        (string Counted, string Repeated)[] pairs =
        [
            ("3x", "xxx"), ("2X", "XX"), ("4x", "xxxx"), ("3dd", "dddddd"), ("2dw", "dwdw"), ("3~", "~~~"), ("2rZ", "rZlrZ"), ("3ix<Esc>", "ixxx<Esc>"), ("2ax<Esc>", "axx<Esc>"),
            ("3Ax<Esc>", "Axxx<Esc>"), ("2D", "d2$"), ("2<<", "<<j<<"),
        ];
        // long ASCII lines so that every counted form has enough characters to work on
        const string text = "alpha beta gamma delta epsilon\nzeta eta theta iota kappa\nlambda mu nu xi omicron\npi rho sigma tau upsilon\nphi chi psi omega";
        for (var seed = 0; seed < 600; seed++)
        {
            var r = new Random(seed);
            var line = r.Next(0, 3);            // never the last line (2D and 2<< need a next line)
            var col = r.Next(5, 12);
            var (counted, repeated) = pairs[r.Next(pairs.Length)];
            var (a, _) = RunKeys(text, line, col, counted);
            var (b, _) = RunKeys(text, line, col, repeated);
            Assert.True(a == b, $"'{counted}' differs from '{repeated}'\n{FormatSeed(seed, text, counted)} at {line}:{col}\ncounted:  {a.Replace("\n", "\\n")}\nrepeated: {b.Replace("\n", "\\n")}");
        }
    }

    [Fact]
    public void Edits_stay_valid_for_surrogates_crlf_tabs_wide_characters_empty_and_unterminated_buffers()
    {
        string[] texts =
        [
            "", "\n", "\n\n", "a", "a\n", "😀", "😀😀😀\n😀", "中文字符 中文\n\t中\t文", "tab\tin\tmiddle\n\t\t", "e\u0301x\u0301 combining", "no newline at the end", "x\r\ny\r\nz",
            "mixed 😀 and 中 and \t", "\u200b\u200bzero width", "a\u00a0b", new string('é', 300), string.Join("\n", Enumerable.Repeat("😀 中 a", 50)),
        ];
        string[] keys =
        [
            "x", "X", "dw", "dd", "D", "J", "~", "rZ", "r😀", "R😀<Esc>", "cwQ<Esc>", "ihi<Esc>", "A😀<Esc>", "o😀<Esc>", "yyp", "xp", "ddP", ">>", "gUU", "g~~", "gqq", "==", "<C-a>",
            "vjd", "Vjd", "<C-v>jd", "<C-v>jIx<Esc>", "<C-v>j$Ax<Esc>", "<C-v>jc😀<Esc>", "vey$p", "ywP", "yiwP", "daw", "diw", "ci\"x<Esc>", "ds)", "ysiw)", "gcc", ":%s/./😀/g<CR>", ":%s/\\s\\+/\\r/g<CR>",
            ":%s/😀//g<CR>", ":g/./m0<CR>", ":%norm Ax<CR>", ":2,3j<CR>", "gg=G", "dG", "ggdG", "Gdgg", "$x", "0x", "$X", "$D", "$J", "G$p", "$a😀<Esc>$.", "u", "<C-r>", "U",
        ];
        foreach (var text in texts)
            foreach (var key in keys)
                foreach (var startAtEnd in new[] { false, true })
                {
                    var buf = new ValidatingVimBuffer(new LineVimBuffer(text));
                    var host = new InMemoryVimHost(buf);
                    using var engine = new VimEngine(host, new VimSession());
                    engine.TypeKeys(startAtEnd ? "G$" : "");
                    try { engine.TypeKeys(key); engine.Drain(); engine.TypeKeys("u"); engine.Drain(); }
                    catch (Exception ex)
                    {
                        throw new Xunit.Sdk.XunitException($"'{key}' on '{text.Replace("\n", "\\n").Replace("\r", "\\r")}' (startAtEnd {startAtEnd}) threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                    CheckEngine(engine, buf, $"'{key}' on '{text.Replace("\n", "\\n")}'");
                }
    }

    [Fact]
    public void Crlf_buffers_never_see_carriage_returns_in_edits()
    {
        var buf = new ValidatingVimBuffer(new LineVimBuffer("one\r\ntwo\r\nthree"));
        var host = new InMemoryVimHost(buf);
        using var engine = new VimEngine(host, new VimSession());
        engine.TypeKeys("Ax<Esc>jddP:%s/o/0/g<CR>Gof<Esc>");
        Assert.Equal("\r\n", buf.EndOfLine);
        Assert.DoesNotContain('\r', buf.GetText());
    }
}
