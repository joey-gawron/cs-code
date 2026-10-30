using CsCode.Core.Text;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>
/// The clipboard registers through the host (T9/T15): the engine calls <see cref="IVimHost.ReadClipboard(char)"/> /
/// <see cref="IVimHost.WriteClipboard(char, string, VimRegisterType)"/> with the register the command names (<c>+</c> the clipboard, <c>*</c> the
/// selection), takes the type the host knows (<see cref="IVimHost.ReadClipboardType"/>), and a host that implements only the older members keeps
/// working through the defaults.
/// </summary>
public class ClipboardRegisterTests
{
    /// <summary>A host with a clipboard and a selection of its own, recording every write; everything else is the in-memory host.</summary>
    private sealed class TwoClipboardHost(InMemoryVimHost inner) : IVimHost
    {
        public Dictionary<char, (string Text, VimRegisterType? Type)> Held { get; } = new();
        public List<(char Register, string Text, VimRegisterType Type)> Writes { get; } = [];
        public List<char> Reads { get; } = [];
        public int OldMemberCalls { get; private set; }

        public IVimBuffer Buffer => inner.Buffer;
        public IVimTimer Timer => inner.Timer;
        public TextPosition GetCaret() => inner.GetCaret();
        public void SetCaret(TextPosition position, bool insertLike) => inner.SetCaret(position, insertLike);
        public void SetSelection(in VimSelection selection) => inner.SetSelection(selection);
        public void OnModeChanged(VimMode mode, in VimModeInfo info) => inner.OnModeChanged(mode, info);

        public string? ReadClipboard() { OldMemberCalls++; return null; }
        public void WriteClipboard(string text, VimRegisterType type) => OldMemberCalls++;

        public string? ReadClipboard(char register)
        {
            Reads.Add(register);
            return Held.TryGetValue(register, out var held) ? held.Text : null;
        }

        public VimRegisterType? ReadClipboardType(char register) => Held.TryGetValue(register, out var held) ? held.Type : null;

        public void WriteClipboard(char register, string text, VimRegisterType type)
        {
            Writes.Add((register, text, type));
            Held[register] = (text, type);
        }
    }

    private static (VimEngine Engine, TwoClipboardHost Host, InMemoryVimHost Inner) Create(string text, VimSession? session = null)
    {
        var inner = new InMemoryVimHost(text);
        var host = new TwoClipboardHost(inner);
        return (new VimEngine(host, session ?? new VimSession()), host, inner);
    }

    private static string Text(IVimHost h) => string.Join("\n", Enumerable.Range(0, h.Buffer.LineCount).Select(h.Buffer.GetLine));

    [Fact]
    public void Plus_and_star_yanks_reach_the_host_by_register_with_their_type()
    {
        var (e, h, _) = Create("alpha beta\ngamma\ndelta");
        e.TypeKeys("\"+yy");
        e.TypeKeys("w\"*yiw");
        e.TypeKeys("j0<C-v>j$\"+y");
        Assert.Equal([('+', "alpha beta\n", VimRegisterType.Line), ('*', "beta", VimRegisterType.Character), ('+', "gamma\ndelta", VimRegisterType.Block)], h.Writes);
        Assert.Equal(0, h.OldMemberCalls);
    }

    [Fact]
    public void Plus_and_star_deletes_reach_the_host_by_register()
    {
        var (e, h, _) = Create("one\ntwo\nthree");
        e.TypeKeys("\"*dd");
        e.TypeKeys("\"+x");
        Assert.Equal([('*', "one\n", VimRegisterType.Line), ('+', "t", VimRegisterType.Character)], h.Writes);
        Assert.Equal("wo\nthree", Text(h));
    }

    [Fact]
    public void Puts_read_the_named_register_and_take_the_type_the_host_knows()
    {
        var (e, h, _) = Create("ab\ncd\nef");
        h.Held['*'] = ("XY", null);                        // another application's selection: charwise from its text
        e.TypeKeys("\"*p");
        Assert.Equal("aXYb\ncd\nef", Text(h));
        h.Held['+'] = ("1\n2", VimRegisterType.Block);       // a box the host knows was copied as one
        e.TypeKeys("j0\"+P");
        Assert.Equal("aXYb\n1cd\n2ef", Text(h));
        h.Held['+'] = ("whole\n", null);                   // a final newline: linewise
        e.TypeKeys("gg\"+p");
        Assert.Equal("aXYb\nwhole\n1cd\n2ef", Text(h));
        Assert.Equal(['*', '+', '+'], h.Reads);
    }

    [Fact]
    public void An_empty_clipboard_register_puts_the_unnamed_register()
    {
        var (e, h, _) = Create("word\n");
        e.TypeKeys("yiw");
        e.TypeKeys("\"*P");
        Assert.Equal("wordword", h.Buffer.GetLine(0));
        Assert.Contains('*', h.Reads);
    }

    [Theory]
    [InlineData("unnamedplus", '+')]
    [InlineData("unnamed", '*')]
    public void The_clipboard_option_mirrors_unnamed_yanks_to_its_register(string option, char register)
    {
        var (e, h, _) = Create("one two\n");
        e.ExecuteEx("set clipboard=" + option);
        e.TypeKeys("yiw");
        Assert.Equal([(register, "one", VimRegisterType.Character)], h.Writes);
    }

    [Fact]
    public void Vim_useSystemClipboard_means_the_clipboard_not_the_selection()
    {
        var (e, h, _) = Create("one two\nthree\n", new VimSession { UseSystemClipboard = true });
        e.TypeKeys("yy");
        Assert.Equal([('+', "one two\n", VimRegisterType.Line)], h.Writes);
        h.Held['+'] = ("pasted", null);
        e.TypeKeys("jp");
        Assert.Equal("tpastedhree", h.Buffer.GetLine(1));
        Assert.Equal(['+'], h.Reads);
    }

    [Fact]
    public void Insert_mode_ctrl_r_macros_and_the_command_line_read_the_named_register()
    {
        var (e, h, _) = Create("x\n");
        h.Held['+'] = ("plus", null);
        h.Held['*'] = ("star", null);
        e.TypeKeys("A<C-r>+<C-r>*<Esc>");
        Assert.Equal("xplusstar", h.Buffer.GetLine(0));
        e.TypeKeys(":s/<C-r>*/S/<CR>");
        Assert.Equal("xplusS", h.Buffer.GetLine(0));
        Assert.Equal(['+', '*', '*'], h.Reads);
    }

    // ---------------------------------------------------------------- 'clipboard' unnamed / unnamedplus (vim-macros-clipboard-4)

    private static string Reg(VimEngine e, char name) => e.Session.Registers.ForRead(name)?.ToText() ?? "";

    [Theory]
    [InlineData("unnamedplus", '+')]
    [InlineData("unnamed", '*')]
    [InlineData("unnamed,unnamedplus", '+')] // unnamedplus prevails for a put (Vim's adjust_clip_reg)
    public void The_clipboard_option_makes_p_put_its_register(string option, char register)
    {
        var (e, h, _) = Create("one two\n");
        e.ExecuteEx("set clipboard=" + option);
        e.TypeKeys("yiw");                                   // the unnamed register now holds "one"
        h.Held[register] = ("X", null);                      // copied in another application
        e.TypeKeys("$p");
        Assert.Equal("one twoX", h.Buffer.GetLine(0));
        Assert.Equal([register], h.Reads);
    }

    [Fact]
    public void With_unnamedplus_P_gp_Visual_p_and_put_read_the_clipboard()
    {
        var (e, h, _) = Create("ab\ncd\n");
        e.ExecuteEx("set clipboard=unnamedplus");
        h.Held['+'] = ("X", null);
        e.TypeKeys("P");
        Assert.Equal("Xab", h.Buffer.GetLine(0));
        e.TypeKeys("0vlp");                                  // Visual p: "Xa" replaced by the clipboard
        Assert.Equal("Xb", h.Buffer.GetLine(0));
        h.Held['+'] = ("line\n", null);
        e.ExecuteEx("put");
        Assert.Equal("line", h.Buffer.GetLine(1));
        Assert.Equal(['+', '+', '+'], h.Reads);
    }

    [Fact]
    public void With_unnamed_and_unnamedplus_yanks_go_to_both_registers_and_deletes_to_plus_only()
    {
        var (e, h, _) = Create("one two\n");
        e.ExecuteEx("set clipboard=unnamed,unnamedplus");
        e.TypeKeys("yiw");
        Assert.Equal([('*', "one", VimRegisterType.Character), ('+', "one", VimRegisterType.Character)], h.Writes.OrderBy(w => w.Register));
        h.Writes.Clear();
        e.TypeKeys("dw");
        Assert.Equal([('+', "one ", VimRegisterType.Character)], h.Writes);
    }

    [Fact]
    public void With_unnamedplus_insert_ctrl_r_quote_reads_the_unnamed_register_as_in_Vim()
    {
        // :h 'clipboard': '+' stands in for the unnamed register in yank, delete, change and put operations; CTRL-R " is none of them
        var (e, h, _) = Create("word\n");
        e.ExecuteEx("set clipboard=unnamedplus");
        e.TypeKeys("yiw");
        h.Held['+'] = ("CLIP", null);
        e.TypeKeys("A <C-r>\"<Esc>");
        Assert.Equal("word word", h.Buffer.GetLine(0));
        Assert.Empty(h.Reads);
    }

    // ---------------------------------------------------------------- "0 and "- next to the clipboard registers (vim-macros-clipboard-5)

    [Fact]
    public void Plus_and_star_yanks_and_deletes_leave_register_0_alone()
    {
        var (e, h, _) = Create("alpha beta gamma delta\n");
        e.TypeKeys("yiw");
        e.TypeKeys("w\"+dw");
        Assert.Equal("alpha", Reg(e, '0'));
        e.TypeKeys("\"*yiw");
        Assert.Equal("alpha", Reg(e, '0'));
        e.TypeKeys("\"+yiw");
        Assert.Equal("alpha", Reg(e, '0'));
        e.TypeKeys("\"0P");
        Assert.Equal("alpha alphagamma delta", h.Buffer.GetLine(0));
    }

    [Fact]
    public void After_a_plus_yank_the_unnamed_register_is_that_yank()
    {
        // Vim keeps "+ in a register of its own and points the unnamed register at it (get_yank_register)
        var (e, _, _) = Create("alpha beta\n");
        e.TypeKeys("yiw");
        e.TypeKeys("w\"+yiw");
        Assert.Equal("beta", Reg(e, '"'));
        Assert.Equal("alpha", Reg(e, '0'));
    }

    [Fact]
    public void With_vim_useSystemClipboard_a_delete_fills_the_small_delete_register_and_not_register_0()
    {
        var (e, h, _) = Create("alpha beta\n", new VimSession { UseSystemClipboard = true });
        e.TypeKeys("yiw");
        e.TypeKeys("x");
        Assert.Equal("a", Reg(e, '-'));
        Assert.Equal("alpha", Reg(e, '0'));
        Assert.Equal(('+', "a", VimRegisterType.Character), h.Writes[^1]); // still mirrored to the clipboard
        e.TypeKeys("wdw");
        Assert.Equal("alpha", Reg(e, '0'));
        e.TypeKeys("\"0P");
        Assert.Equal("lphaalpha ", h.Buffer.GetLine(0)); // the yank, not the deleted "beta"
    }

    [Fact]
    public void With_vim_useSystemClipboard_a_multi_line_delete_shifts_the_numbered_registers()
    {
        var (e, h, _) = Create("one\ntwo\nthree\n", new VimSession { UseSystemClipboard = true });
        e.TypeKeys("yy");
        e.TypeKeys("jdd");
        Assert.Equal("two\n", Reg(e, '1'));
        Assert.Equal("one\n", Reg(e, '0'));
        Assert.Equal(('+', "two\n", VimRegisterType.Line), h.Writes[^1]);
    }

    [Fact]
    public void Let_on_a_clipboard_register_writes_the_clipboard()
    {
        var (e, h, _) = Create("x\n");
        e.TypeKeys("yiw");
        e.ExecuteEx("let @+ = 'set'");
        Assert.Equal([('+', "set", VimRegisterType.Character)], h.Writes.Where(w => w.Text == "set"));
        Assert.Equal("x", Reg(e, '0'));
        e.ExecuteEx("let @* = \"line\\n\"");
        Assert.Equal(('*', "line\n", VimRegisterType.Line), h.Writes[^1]);
    }

    // ---------------------------------------------------------------- one read per put (vim-macros-clipboard-9)

    /// <summary>A host whose clipboard changes between calls: the separate text and type reads would disagree.</summary>
    private sealed class SnapshotHost(InMemoryVimHost inner) : IVimHost
    {
        public int SnapshotReads, SeparateReads;
        public IVimBuffer Buffer => inner.Buffer;
        public IVimTimer Timer => inner.Timer;
        public TextPosition GetCaret() => inner.GetCaret();
        public void SetCaret(TextPosition position, bool insertLike) => inner.SetCaret(position, insertLike);
        public void SetSelection(in VimSelection selection) => inner.SetSelection(selection);
        public void OnModeChanged(VimMode mode, in VimModeInfo info) => inner.OnModeChanged(mode, info);
        public string? ReadClipboard(char register) { SeparateReads++; return "a\nb"; }
        public VimRegisterType? ReadClipboardType(char register) { SeparateReads++; return VimRegisterType.Line; }
        public (string? Text, VimRegisterType? Type) ReadClipboardRegister(char register)
        {
            SnapshotReads++;
            return ("a\nb", VimRegisterType.Block);
        }
    }

    [Fact]
    public void A_put_reads_the_clipboard_text_and_type_in_one_call()
    {
        var host = new SnapshotHost(new InMemoryVimHost("12\n34\n"));
        var e = new VimEngine(host, new VimSession());
        e.TypeKeys("\"+P");
        Assert.Equal(["a12", "b34"], [host.Buffer.GetLine(0), host.Buffer.GetLine(1)]); // the block, not the type of another read
        Assert.Equal(1, host.SnapshotReads);
        Assert.Equal(0, host.SeparateReads);
    }

    [Fact]
    public void A_host_with_only_the_older_members_still_gets_both_registers_through_the_defaults()
    {
        var host = new InMemoryVimHost("one\ntwo\n");
        var e = new VimEngine(host, new VimSession());
        e.TypeKeys("\"*yy");
        Assert.Equal("one\n", host.Clipboard);
        Assert.Equal(VimRegisterType.Line, host.ClipboardType);
        host.Clipboard = "THERE";
        e.TypeKeys("j\"+P");
        Assert.Equal("THEREtwo", host.Buffer.GetLine(1));
    }

    // ---------------------------------------------------------------- which key reads a clipboard register (vim-macros-clipboard-1, -4)

    /// <summary>An engine with 'clipboard' set to <paramref name="option"/> (empty: Vim's own unnamed register), both clipboard registers holding text.</summary>
    private static (VimEngine Engine, TwoClipboardHost Host) Probed(string option)
    {
        var (e, h, _) = Create("one two xp\nthree four\n");
        if (option.Length > 0) e.ExecuteEx("set clipboard=" + option);
        h.Held['+'] = ("PLUS", null);
        h.Held['*'] = ("STAR", null);
        return (e, h);
    }

    /// <summary>What a key can change: the text, the mode, the cursor, the keys pending, the unnamed register, mark p.</summary>
    private static string State((VimEngine Engine, TwoClipboardHost Host) p) =>
        $"{Text(p.Host)} | {p.Engine.ModeCode} {p.Engine.Cursor} [{p.Engine.PendingKeys}] \"{Reg(p.Engine, '"')}\" 'p={p.Engine.GetMarkPosition('p')}";

    [Theory]
    [InlineData("", "p", "+")]
    [InlineData("", "P", "+")]
    [InlineData("3", "p", "+")]
    [InlineData("g", "p", "+")]
    [InlineData("g", "P", "+")]
    [InlineData("]", "p", "+")]
    [InlineData("[", "P", "+")]
    [InlineData("v", "p", "+")]
    [InlineData("Vj", "P", "+")]
    [InlineData("<C-v>j", "p", "+")]
    [InlineData("i<C-o>", "p", "+")]       // Insert-mode CTRL-O p puts the unnamed register too
    [InlineData("\"", "p", "")]           // p is the register's name
    [InlineData("\"a", "p", "")]          // a register of Vim's own
    [InlineData("\"+", "p", "")]          // named in the keys: read when "+ was typed
    [InlineData("m", "p", "")]             // a mark
    [InlineData("'", "p", "")]
    [InlineData("f", "p", "")]             // a motion's argument
    [InlineData("t", "p", "")]
    [InlineData("r", "p", "")]
    [InlineData("q", "p", "")]             // recording into register p
    [InlineData("@", "p", "")]
    [InlineData("vi", "p", "")]            // a text object
    [InlineData("va", "p", "")]
    [InlineData("d", "p", "")]             // an operator's motion
    [InlineData("di", "p", "")]
    [InlineData("i", "p", "")]             // typed text
    [InlineData(":", "p", "")]             // the command line
    [InlineData("", "x", "")]
    public void Only_a_put_of_the_unnamed_register_reads_the_clipboard_register_it_stands_for(string before, string key, string expected)
    {
        var probed = Probed("unnamedplus");
        probed.Engine.TypeKeys(before);
        var at = State(probed);
        Assert.Equal(expected, probed.Engine.ClipboardRegisterReadBy(key)?.ToString() ?? "");
        Assert.Equal(at, State(probed));      // the engine's state is as it was
        // and the key then does what it does without the question (the parser's state was put back)
        var plain = Probed("unnamedplus");
        plain.Engine.TypeKeys(before);
        probed.Engine.TypeKeys(key);
        plain.Engine.TypeKeys(key);
        Assert.Equal(State(plain), State(probed));
    }

    [Theory]
    [InlineData("nnoremap p x", "\"")]      // after " the next key is a register name, which no mapping takes
    [InlineData("nnoremap p x", "m")]       // a mark's name, an argument: not mapped either
    [InlineData("nnoremap p x", "f")]
    [InlineData("nnoremap dx x", "d")]      // d waits for the mapping decision: asking parses dp, an operator waiting for its motion
    public void Asking_leaves_the_parser_as_it_was_for_the_keys_that_follow(string mapping, string before)
    {
        var probed = Probed("unnamedplus");
        var plain = Probed("unnamedplus");
        foreach (var e in new[] { probed.Engine, plain.Engine })
        {
            e.ExecuteEx(mapping);
            e.TypeKeys(before);
        }
        var at = State(probed);
        Assert.Null(probed.Engine.ClipboardRegisterReadBy("p"));
        Assert.Equal(at, State(probed));
        probed.Engine.TypeKeys("p");                // a register name, a mark, a character to find; an invalid motion
        plain.Engine.TypeKeys("p");
        Assert.Equal(State(plain), State(probed));
    }

    [Theory]
    [InlineData("unnamed", "*")]
    [InlineData("unnamed,unnamedplus", "+")]
    [InlineData("unnamedplus", "+")]
    [InlineData("", "")]                   // the unnamed register is Vim's own: a put reads no clipboard
    public void The_register_reported_follows_the_clipboard_option(string option, string expected)
    {
        var (e, _) = Probed(option);
        Assert.Equal(expected, e.ClipboardRegisterReadBy("p")?.ToString() ?? "");
        Assert.Equal(expected, e.ClipboardRegisterReadBy("P")?.ToString() ?? "");
    }

    [Fact]
    public void Vim_useSystemClipboard_makes_a_put_read_plus()
    {
        var (e, _, _) = Create("one\n", new VimSession { UseSystemClipboard = true });
        Assert.Equal('+', e.ClipboardRegisterReadBy("p"));
    }

    [Fact]
    public void A_repeat_reads_the_register_the_repeated_put_read()
    {
        var (e, h, _) = Create("one\n");
        e.ExecuteEx("set clipboard=unnamedplus");
        h.Held['+'] = ("X", null);
        h.Held['*'] = ("Y", null);
        Assert.Null(e.ClipboardRegisterReadBy("."));          // nothing to repeat
        e.TypeKeys("p");
        Assert.Equal('+', e.ClipboardRegisterReadBy("."));
        e.TypeKeys("x");
        Assert.Null(e.ClipboardRegisterReadBy("."));          // x puts nothing
        e.ExecuteEx("set clipboard=");
        e.TypeKeys("\"*p");
        Assert.Equal('*', e.ClipboardRegisterReadBy("."));    // the register the put named, whatever 'clipboard' says
        e.TypeKeys("yy\"0p");
        Assert.Null(e.ClipboardRegisterReadBy("."));
    }

    [Fact]
    public void A_key_a_mapping_may_take_reports_nothing_and_a_mapping_prefix_left_alone_does_not_hide_the_put()
    {
        var (e, _, _) = Create("one\n");
        e.ExecuteEx("set clipboard=unnamedplus");
        e.ExecuteEx("nnoremap gD x");                         // like the shell's gD gi g. ]d [d: g waits for the mapping decision
        e.TypeKeys("g");
        Assert.Equal("g", e.PendingKeys);
        Assert.Equal('+', e.ClipboardRegisterReadBy("p"));    // gp: no mapping takes it
        e.TypeKeys("<Esc>");
        e.ExecuteEx("nnoremap gp x");
        e.TypeKeys("g");
        Assert.Null(e.ClipboardRegisterReadBy("p"));          // the mapping runs instead
        e.TypeKeys("<Esc>");
        e.ExecuteEx("nnoremap p P");
        Assert.Null(e.ClipboardRegisterReadBy("p"));
        e.ExecuteEx("nnoremap pq x");                         // p begins a longer mapping: it waits for the next key
        Assert.Null(e.ClipboardRegisterReadBy("p"));
    }
}
