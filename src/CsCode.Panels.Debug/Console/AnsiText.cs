using System.Text;

namespace CsCode.Panels.Debug.Console;

/// <summary>Text attributes an SGR escape sequence (<c>ESC [ … m</c>) can set.</summary>
[Flags]
public enum ConsoleTextFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strikethrough = 32,
}

/// <summary>
/// The look of a run of console text. A colour is <see cref="Default"/> (the line's own colour, from its category), an index into the xterm
/// 256-colour palette (0–15: the theme's <c>terminal.ansi*</c> colours; 16–255: the 6×6×6 cube and the grey ramp) or a 24-bit colour
/// (<see cref="Rgb"/>).
/// </summary>
public readonly record struct ConsoleTextStyle(int Foreground, int Background, ConsoleTextFlags Flags)
{
    public const int Default = -1;
    private const int RgbFlag = 0x0100_0000;

    public static ConsoleTextStyle Plain { get; } = new(Default, Default, ConsoleTextFlags.None);

    public bool IsPlain => Foreground == Default && Background == Default && Flags == ConsoleTextFlags.None;

    public static int Rgb(int r, int g, int b) => RgbFlag | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);

    public static bool IsRgb(int color) => (color & RgbFlag) != 0;

    /// <summary>The 0xRRGGBB value of a palette index ≥ 16 or of a 24-bit colour (0–15 are the theme's and are resolved by the view).</summary>
    public static uint ToRgb(int color)
    {
        if (IsRgb(color)) return (uint)(color & 0xFF_FFFF);
        if (color is >= 16 and <= 231)
        {
            var i = color - 16;
            static uint Level(int v) => v == 0 ? 0u : (uint)(55 + 40 * v);
            return (Level(i / 36) << 16) | (Level(i / 6 % 6) << 8) | Level(i % 6);
        }
        if (color is >= 232 and <= 255)
        {
            var grey = (uint)(8 + 10 * (color - 232));
            return (grey << 16) | (grey << 8) | grey;
        }
        return 0;
    }
}

/// <summary>A styled run of a <see cref="ConsoleLine"/>'s text: characters <c>[Start, Start + Length)</c>.</summary>
public readonly record struct ConsoleSpan(int Start, int Length, ConsoleTextStyle Style);

/// <summary>
/// Turns output carrying ANSI escape sequences into plain text plus styled spans, as VS Code's Debug Console does: SGR (<c>ESC [ … m</c>)
/// sets colours (16, 256 and 24-bit, foreground and background), bold, dim, italic, underline, inverse and strikethrough; every other control
/// sequence (cursor movement, erase, OSC titles and links, two-character escapes) is removed. The style carries over from line to line and
/// from event to event of one stream (one parser per output category), as it does in a terminal. Runs on the console's pump thread, never on
/// the UI thread; one instance is used by one thread at a time.
/// </summary>
internal sealed class AnsiParser
{
    private const char Esc = '\u001b';
    private readonly StringBuilder _text = new();
    private readonly List<ConsoleSpan> _spans = new();
    private readonly List<int> _params = new();

    /// <summary>The style the next character gets (what the escapes seen so far set).</summary>
    public ConsoleTextStyle Style { get; private set; } = ConsoleTextStyle.Plain;

    /// <summary>Whether <paramref name="text"/> needs this parser: it has an escape, or a style set earlier is still on.</summary>
    public bool Needed(ReadOnlySpan<char> text) => !Style.IsPlain || text.Contains(Esc);

    public void Reset() => Style = ConsoleTextStyle.Plain;

    /// <summary>
    /// One line (no line break) to its plain text, cut to <paramref name="maxChars"/> characters (plus "…"), and its styled spans (null when
    /// all of it is plain).
    /// </summary>
    public (string Text, ConsoleSpan[]? Spans) ParseLine(ReadOnlySpan<char> line, int maxChars)
    {
        _text.Clear();
        _spans.Clear();
        var runStart = 0;
        var runStyle = Style;
        var truncated = false;
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c != Esc)
            {
                if (_text.Length < maxChars) _text.Append(c);
                else truncated = true;
                i++;
                continue;
            }
            i = Escape(line, i);
            if (Style != runStyle)
            {
                Close(runStart, runStyle);
                runStart = _text.Length;
                runStyle = Style;
            }
        }
        Close(runStart, runStyle);
        if (truncated) _text.Append('…');
        return (_text.ToString(), _spans.Count == 0 ? null : [.. _spans]);
    }

    private void Close(int start, ConsoleTextStyle style)
    {
        var length = _text.Length - start;
        if (length > 0 && !style.IsPlain) _spans.Add(new ConsoleSpan(start, length, style));
    }

    /// <summary>Consumes the escape sequence at <paramref name="at"/> (applying it if it is SGR); returns the index after it.</summary>
    private int Escape(ReadOnlySpan<char> s, int at)
    {
        var i = at + 1;
        if (i >= s.Length) return i;
        switch (s[i])
        {
            case '[': // CSI: parameters and intermediates, then a final byte in @..~
                var paramStart = ++i;
                while (i < s.Length && s[i] is < '@' or > '~') i++;
                if (i >= s.Length) return i; // cut off at the end of the line: dropped
                if (s[i] == 'm') Sgr(s[paramStart..i]);
                return i + 1;
            case ']': // OSC: up to BEL or ST (ESC \)
                for (i++; i < s.Length; i++)
                {
                    if (s[i] == '\u0007') return i + 1;
                    if (s[i] == Esc && i + 1 < s.Length && s[i + 1] == '\\') return i + 2;
                }
                return i;
            default: // ESC, intermediates (ESC ( B, ESC # 8), then one final character (ESC 7, ESC =)
                while (i < s.Length && s[i] is >= ' ' and <= '/') i++;
                return Math.Min(i + 1, s.Length);
        }
    }

    private void Sgr(ReadOnlySpan<char> parameters)
    {
        _params.Clear();
        var value = -1;
        foreach (var c in parameters)
        {
            if (c is >= '0' and <= '9') value = (value < 0 ? 0 : Math.Min(value, 100_000) * 10) + (c - '0');
            else if (c is ';' or ':') { _params.Add(Math.Max(value, 0)); value = -1; }
            else return; // private or malformed (ESC [ ? … m): not a colour
        }
        _params.Add(Math.Max(value, 0));

        var (fg, bg, flags) = Style;
        for (var k = 0; k < _params.Count; k++)
        {
            var p = _params[k];
            switch (p)
            {
                case 0: fg = bg = ConsoleTextStyle.Default; flags = ConsoleTextFlags.None; break;
                case 1: flags |= ConsoleTextFlags.Bold; break;
                case 2: flags |= ConsoleTextFlags.Dim; break;
                case 3: flags |= ConsoleTextFlags.Italic; break;
                case 4: flags |= ConsoleTextFlags.Underline; break;
                case 7: flags |= ConsoleTextFlags.Inverse; break;
                case 9: flags |= ConsoleTextFlags.Strikethrough; break;
                case 22: flags &= ~(ConsoleTextFlags.Bold | ConsoleTextFlags.Dim); break;
                case 23: flags &= ~ConsoleTextFlags.Italic; break;
                case 24: flags &= ~ConsoleTextFlags.Underline; break;
                case 27: flags &= ~ConsoleTextFlags.Inverse; break;
                case 29: flags &= ~ConsoleTextFlags.Strikethrough; break;
                case >= 30 and <= 37: fg = p - 30; break;
                case 38: fg = Extended(ref k, fg); break;
                case 39: fg = ConsoleTextStyle.Default; break;
                case >= 40 and <= 47: bg = p - 40; break;
                case 48: bg = Extended(ref k, bg); break;
                case 49: bg = ConsoleTextStyle.Default; break;
                case >= 90 and <= 97: fg = p - 90 + 8; break;
                case >= 100 and <= 107: bg = p - 100 + 8; break;
            }
        }
        Style = new ConsoleTextStyle(fg, bg, flags);
    }

    /// <summary><c>38;5;n</c> / <c>38;2;r;g;b</c> (and 48): the colour, consuming its parameters; malformed ones leave the colour as it was.</summary>
    private int Extended(ref int k, int current)
    {
        if (k + 1 >= _params.Count) return current;
        switch (_params[k + 1])
        {
            case 5 when k + 2 < _params.Count:
                k += 2;
                return _params[k] <= 255 ? _params[k] : current;
            case 2 when k + 4 < _params.Count:
                k += 4;
                return ConsoleTextStyle.Rgb(_params[k - 2], _params[k - 1], _params[k]);
            default:
                k = _params.Count; // unknown form: the rest of the sequence is its arguments
                return current;
        }
    }
}
