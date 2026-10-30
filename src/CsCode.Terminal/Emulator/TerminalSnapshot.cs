namespace CsCode.Terminal.Emulator;

public enum MouseTracking : byte { None, X10, Normal, ButtonEvent, AnyEvent }

public enum MouseEncoding : byte { Default, Utf8, Sgr, Urxvt }

/// <summary>DECSCUSR shapes.</summary>
public enum CursorShape : byte { Default, Block, Underline, Bar }

/// <summary>The modes input encoding needs (keys, mouse, paste, focus): published by the worker on every change, read by the UI at key time.</summary>
public sealed record TerminalModes
{
    public static readonly TerminalModes Initial = new();

    public bool ApplicationCursorKeys { get; init; }
    public bool ApplicationKeypad { get; init; }
    public bool BracketedPaste { get; init; }
    public bool FocusEvents { get; init; }
    public MouseTracking Mouse { get; init; }
    public MouseEncoding MouseEncoding { get; init; }
    public bool AlternateScreen { get; init; }
    /// <summary>DECSET 1007: the wheel sends cursor keys on the alternate screen (xterm.js sends them whenever the alternate screen is up).</summary>
    public bool AlternateScroll { get; init; } = true;
    public bool LineFeedNewLine { get; init; }
}

/// <summary>
/// Everything the renderer needs for one frame, immutable: the visible rows (unchanged rows are the same <see cref="TerminalRow"/> instances
/// as in the previous snapshot, which is the renderer's row-level dirty tracking), the cursor, the viewport's place in the scrollback and the
/// screen-wide modes. Built on the terminal's worker at most once per frame the UI asked for.
/// </summary>
public sealed class TerminalSnapshot
{
    public required int Columns { get; init; }
    public required int RowCount { get; init; }
    public required TerminalRow[] Rows { get; init; }

    /// <summary>Cursor column/row in the visible rows, or -1 when the cursor's row is not in view (scrolled back).</summary>
    public required int CursorX { get; init; }
    public required int CursorY { get; init; }
    public required bool CursorVisible { get; init; }
    public required CursorShape CursorShape { get; init; }
    /// <summary>DECSCUSR or DECSET 12 asked for blinking (null: the program did not say; the user setting decides).</summary>
    public required bool? CursorBlink { get; init; }

    /// <summary>Absolute number of the first visible row; <see cref="ScreenTop"/> is that of the screen's first row (the bottom of the scrollback).</summary>
    public required long ViewportTop { get; init; }
    public required long ScreenTop { get; init; }
    /// <summary>Absolute number of the oldest row kept in the scrollback.</summary>
    public required long ScrollbackTop { get; init; }
    public bool IsAtBottom => ViewportTop >= ScreenTop;

    public required bool ReverseVideo { get; init; }
    public required TerminalModes Modes { get; init; }
    /// <summary>OSC 4 palette changes (index → RGB), or null when the program changed none.</summary>
    public IReadOnlyDictionary<int, uint>? PaletteOverrides { get; init; }
    public required long Sequence { get; init; }
}

/// <summary>Theme colours the emulator answers OSC 4/10/11/12 queries with (RGB as 0xRRGGBB). Implementations must be thread safe.</summary>
public interface ITerminalPalette
{
    int Foreground { get; }
    int Background { get; }
    int Cursor { get; }
    /// <summary>One of the 16 ANSI colours (0-15).</summary>
    int Ansi(int index);
}

/// <summary>xterm's default colours (the 6x6x6 cube and the grey ramp for 16-255, VS Code's dark ANSI set for 0-15).</summary>
public sealed class DefaultTerminalPalette : ITerminalPalette
{
    public static readonly DefaultTerminalPalette Instance = new();

    private static readonly int[] Ansi16 =
    [
        0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xE5E5E5,
    ];

    public int Foreground => 0xCCCCCC;
    public int Background => 0x1E1E1E;
    public int Cursor => 0xFFFFFF;
    public int Ansi(int index) => Ansi16[index & 15];

    /// <summary>RGB of a 256-colour index (16-255 are fixed; 0-15 come from <paramref name="palette"/>).</summary>
    public static int Color256(ITerminalPalette palette, int index)
    {
        if (index < 16) return palette.Ansi(index);
        if (index < 232)
        {
            var i = index - 16;
            static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
            return (Level(i / 36) << 16) | (Level(i / 6 % 6) << 8) | Level(i % 6);
        }
        var grey = 8 + (index - 232) * 10;
        return (grey << 16) | (grey << 8) | grey;
    }
}
