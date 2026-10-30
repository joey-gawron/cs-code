using System.Text;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>One byte stream for the differential suite, with the screen size it is rendered at.</summary>
public sealed record CorpusCase(string Name, byte[] Bytes, int Columns = 20, int Rows = 6)
{
    public override string ToString() => Name;
}

/// <summary>
/// Hand-written sequences per feature (the emulator's sequence list, docs/TERMINAL.md). Each case is small, so a disagreement points at one
/// sequence. Cases a reference implements differently on purpose are listed in <see cref="KnownDivergences"/> with the reason.
/// </summary>
public static class Corpus
{
    private static CorpusCase C(string name, string text, int columns = 20, int rows = 6) => new(name, Encoding.UTF8.GetBytes(text), columns, rows);

    public static IEnumerable<CorpusCase> HandWritten()
    {
        yield return C("plain-text", "hello world\r\nsecond line\r\nthird");
        yield return C("autowrap", "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJ");
        yield return C("autowrap-off", "\e[?7labcdefghijklmnopqrstuvwxyz\r\nnext");
        yield return C("pending-wrap-crlf", "abcdefghijklmnopqrst\r\nnext line");
        yield return C("pending-wrap-then-cup", "abcdefghijklmnopqrst\e[3;3Hx");
        yield return C("cursor-moves", "\e[3;5Hx\e[2Ay\e[3Bz\e[4Cw\e[10Dv\e[12Gu\e[5dt\e[2Es\e[3Fr\e[7`q");
        yield return C("cursor-clamps", "\e[99;99Hx\e[0;0Hy\e[99Az\e[99Dw");
        yield return C("erase-display-below", "aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc\r\ndddddddddd\e[2;5H\e[0J");
        yield return C("erase-display-above", "aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc\r\ndddddddddd\e[3;5H\e[1J");
        yield return C("erase-display-all-bce", "aaaaaaaaaa\r\nbbbbbbbbbb\e[45m\e[2J\e[0mx");
        yield return C("erase-line", "0123456789abcdef\e[1;5H\e[K\r\n0123456789abcdef\e[2;5H\e[1K\r\n0123456789abcdef\e[3;5H\e[2K");
        yield return C("erase-line-bce", "\e[42m\e[K\e[0m\r\n\e[103m\e[1;34mxx\e[K\e[0m");
        yield return C("erase-chars", "0123456789abcdef\e[1;3H\e[4X\e[44m\e[2X");
        yield return C("insert-delete-chars", "0123456789abcdef\e[1;3H\e[3@\r\n0123456789abcdef\e[2;3H\e[3P");
        yield return C("insert-delete-lines", "1\r\n2\r\n3\r\n4\r\n5\r\n6\e[2;5r\e[3;1H\e[2L\e[5;1H\e[M");
        yield return C("scroll-region-linefeed", "1\r\n2\r\n3\r\n4\r\n5\r\n6\e[2;4r\e[4;1H\n\n\nX", rows: 6);
        yield return C("reverse-index-top", "1\r\n2\r\n3\r\n4\e[2;4r\e[2;1H\eM\eMY");
        yield return C("scroll-up-down", "1\r\n2\r\n3\r\n4\r\n5\r\n6\e[2S\e[1;1H\e[1T");
        yield return C("index-next-line", "ab\eDcd\eEef");
        yield return C("tabs", "\ta\tb\r\n\e[3g\e[5G\eH\e[12G\eH\r\tc\td\r\n\e[2Ie\e[Zf", columns: 30);
        yield return C("tab-clear-one", "\e[9G\e[g\r\tx\r\n\tyz", columns: 30);
        yield return C("sgr-basic", "\e[1mB\e[0m \e[3mI\e[0m \e[4mU\e[0m \e[7mR\e[0m \e[9mS\e[0m \e[5mK\e[0m \e[8mH\e[0m \e[2mD\e[0m");
        yield return C("sgr-resets", "\e[1;3;4;5;7;8;9mA\e[22mB\e[23mC\e[24mD\e[25mE\e[27mF\e[28mG\e[29mH");
        yield return C("sgr-16-colors", "\e[30mk\e[31mr\e[32mg\e[33my\e[34mb\e[35mm\e[36mc\e[37mw\e[90mK\e[91mR\e[92mG\e[93mY\e[94mB\e[95mM\e[96mC\e[97mW\e[39m\r\n\e[40mk\e[41mr\e[47mw\e[100mK\e[107mW\e[49m");
        yield return C("sgr-256-colors", "\e[38;5;1ma\e[38;5;16mb\e[38;5;196mc\e[38;5;255md\e[48;5;21me\e[48;5;232mf\e[0m");
        yield return C("sgr-truecolor", "\e[38;2;255;128;0ma\e[48;2;0;64;255mb\e[38:2:40:50:60md\e[0m");
        yield return C("sgr-truecolor-colour-space", "\e[38:2::10:20:30mc\e[48:2:0:1:2:3me\e[0m");
        yield return C("sgr-underline-styles", "\e[4:1ma\e[4:2mb\e[4:3mc\e[21md\e[4:0me\e[0m");
        yield return C("dec-special-graphics", "\e(0lqqqqk\r\nx    x\r\nmqqqqj\e(B ok\r\n\e(0`afgjklmnopqrstuvwxyz{|}~\e(B");
        yield return C("shift-in-out", "\e)0a\x0eqx\x0fqx");
        yield return C("wide-cjk", "中文字符 ok\r\nab中\r\n");
        yield return C("wide-at-margin", "abcdefghijklmnopqr中x");
        yield return C("wide-overwrite-halves", "中文中文\e[1;2Hx\e[1;5Hy");
        yield return C("combining", "é ä ñ\r\nx⃝");
        yield return C("alternate-screen-roundtrip", "primary\e[2;3H\e[?1049hALT\e[5;5HZ\e[?1049lback");
        yield return C("alternate-screen-active", "primary\e[?1049h\e[2;2Hin alt");
        yield return C("save-restore-cursor", "\e[2;3H\e[1;31m\e7\e[0m\e[5;10Hfar\e8near");
        yield return C("origin-mode", "\e[2;5r\e[?6h\e[1;1Ha\e[3;3Hb\e[?6l\e[1;1Hc");
        yield return C("insert-mode", "abcdef\e[1;3H\e[4hXY\e[4lZ");
        yield return C("rep", "ab\e[5bc");
        yield return C("decaln", "\e#8");
        yield return C("scroll-off-the-top", string.Concat(Enumerable.Range(0, 30).Select(i => $"line {i}\r\n")) + "end");
        yield return C("progress-bar", string.Concat(Enumerable.Range(0, 21).Select(i => $"\r[{new string('#', i),-20}] {i * 5}%")), columns: 30);
        yield return C("carriage-return-overwrite", "hello world\rHELLO\r\n12345\r67");
        yield return new CorpusCase("invalid-utf8", [0x61, 0xC3, 0x28, 0x62, 0xE2, 0x28, 0xA1, 0x63, 0xF0, 0x28, 0x8C, 0xBC, 0x64]);
        yield return new CorpusCase("invalid-utf8-subparts", [0x80, 0x65, 0xED, 0xA0, 0x80, 0x66, 0xF4, 0x90, 0x80, 0x80, 0x67, 0xC0, 0xAF, 0x68]);
        yield return C("ignored-sequences", "a\e]0;window title\ab\e]2;other\e\\c\eP$q m\e\\d\e[?1000h\e[?2004he\e_apc\e\\f");
        yield return C("soft-reset", "\e[2;4r\e[?6h\e[4h\e[1mbold\e[!pplain\e[1;1Htop");
        yield return C("full-reset", "garbage\e[31m\e[2;3r\e[?1049hx\ecafter");
        yield return C("backspace-at-pending-wrap", "abcdefghijklmnopqrst\bX");
        yield return C("emoji", "ok \U0001F600 go ✅!");
        yield return C("erase-wide-partial", "中文中文\e[1;2H\e[1X\e[1;7H\e[K");
        yield return C("vpa-hpa-cha", "\e[4dA\e[10`B\e[3GC");
        yield return C("cnl-cpl", "\e[3;5Hx\e[Ey\e[2Fz");
        yield return C("lf-below-region", "\e[1;3r\e[5;1Ha\nb\nc\nd");
        yield return C("cup-in-region-then-scroll", "\e[2;3r\e[3;1H1\n2\n3\n4");
        yield return C("long-csi", "\e[" + string.Join(';', Enumerable.Range(1, 40)) + "mtext\e[0m");
    }

    /// <summary>
    /// Where a reference terminal deliberately behaves otherwise than xterm/xterm.js (which CsCode follows): oracle name and case name, with
    /// the reason. Everything not listed must match exactly.
    /// </summary>
    public static readonly Dictionary<(string Oracle, string Case), string> KnownDivergences = new()
    {
        [("tmux", "erase-wide-partial")] = "erasing the right half of a wide character: xterm.js erases the whole character, tmux keeps the left half drawn",
        [("libvterm", "erase-wide-partial")] = "erasing the right half of a wide character: xterm.js erases the whole character, libvterm keeps the left half drawn",
        [("libvterm", "wide-overwrite-halves")] = "printing over the right half of a wide character: xterm.js blanks the left half, libvterm keeps it (tmux agrees with CsCode)",
        [("tmux", "backspace-at-pending-wrap")] = "tmux keeps a pending wrap as the column past the margin, so BS lands on the last column; DEC terminals and xterm.js move one left of it (libvterm agrees with CsCode)",
        [("tmux", "long-csi")] = "tmux keeps more than 32 parameters; xterm.js and CsCode keep the first 32",
        [("tmux", "soft-reset")] = "tmux does not implement DECSTR (CSI ! p); libvterm agrees with CsCode",
        [("tmux", "tabs")] = "capture-pane writes a TAB character for the cells a tab skipped, so tab stops cannot be read back from tmux (its cursor agrees); libvterm agrees with CsCode",
        [("tmux", "tab-clear-one")] = "capture-pane writes a TAB character for the cells a tab skipped (see 'tabs'); libvterm agrees with CsCode",
        [("tmux", "full-reset")] = "RIS: xterm.js recreates both screens and shows the primary one; tmux stays on the alternate screen (the text agrees)",
        [("libvterm", "full-reset")] = "RIS: xterm.js recreates both screens and shows the primary one; libvterm stays on the alternate screen (the text agrees)",
        [("libvterm", "dec-special-graphics")] = "libvterm maps 'y'/'z' to U+2A7D/U+2A7E; xterm and xterm.js use U+2264/U+2265 (tmux agrees with CsCode)",
        [("tmux", "invalid-utf8-subparts")] = "CsCode replaces every maximal invalid subpart with U+FFFD (Unicode's recommended practice, as browsers decode); tmux writes one per broken sequence (the valid text agrees: 'invalid-utf8')",
        [("libvterm", "invalid-utf8-subparts")] = "CsCode replaces every maximal invalid subpart with U+FFFD; libvterm drops some of the bytes (the valid text agrees: 'invalid-utf8')",
        [("libvterm", "sgr-truecolor-colour-space")] = "libvterm 0.3 misreads the ITU form 38:2:<space>:r:g:b; xterm.js and tmux read it as CsCode does",
    };

    /// <summary>Inputs a reference cannot survive in-process (they would crash the test host), with the reason.</summary>
    public static readonly Dictionary<(string Oracle, string Case), string> Unsafe = new()
    {
        [("libvterm", "long-csi")] = "libvterm 0.3 overflows its argument array on a CSI with more than 16 parameters (segfault, found by this suite); CsCode keeps 32 as xterm.js does",
    };
}
