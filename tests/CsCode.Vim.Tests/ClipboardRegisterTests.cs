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
}
