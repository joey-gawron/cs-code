using System.Text;
using CsCode.Terminal.Emulator;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>Which cell properties an oracle reports reliably (libvterm has no faint or overline; tmux's capture has no underline colour...).</summary>
[Flags]
public enum Compared
{
    Text = 1,
    Colors = 2,
    Bold = 4,
    Italic = 8,
    Underline = 16,
    Inverse = 32,
    Strike = 64,
    Dim = 128,
    Blink = 256,
    Invisible = 512,
    Cursor = 1024,
    AlternateScreen = 2048,
    UnderlineStyle = 4096,
    All = Text | Colors | Bold | Italic | Underline | Inverse | Strike | Dim | Blink | Invisible | Cursor | AlternateScreen | UnderlineStyle,
}

/// <summary>One cell as every emulator can describe it.</summary>
public readonly record struct RefCell(string Text, bool Spacer, uint Fg, uint Bg, CellFlags Flags, UnderlineStyle Underline);

/// <summary>A whole screen, emulator-independent: cells, cursor, alternate screen.</summary>
public sealed class ScreenState(int columns, int rows)
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
    public RefCell[,] Cells { get; } = new RefCell[rows, columns];
    public int CursorX { get; set; }
    public int CursorY { get; set; }
    public bool AlternateScreen { get; set; }

    public static ScreenState From(VtEmulator t)
    {
        var s = new ScreenState(t.Columns, t.Rows) { CursorX = t.CursorX, CursorY = t.CursorY, AlternateScreen = t.IsAlternateScreen };
        for (var r = 0; r < t.Rows; r++)
        {
            var row = t.GetScreenRow(r);
            for (var c = 0; c < t.Columns; c++)
            {
                var cell = row[c];
                var a = cell.Attributes;
                s.Cells[r, c] = new RefCell(cell.IsSpacer ? "" : cell.Rune == 0 ? " " : row.CellText(c), cell.IsSpacer, a.Foreground, a.Background,
                    a.Flags & ~(CellFlags.Wide | CellFlags.WideSpacer), a.Underline);
            }
        }
        return s;
    }

    public string Text()
    {
        var sb = new StringBuilder();
        for (var r = 0; r < Rows; r++)
        {
            var line = new StringBuilder();
            for (var c = 0; c < Columns; c++) line.Append(Cells[r, c].Spacer ? "" : Cells[r, c].Text);
            sb.Append(line.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Differences between two screens over the properties in <paramref name="what"/>; empty when they agree.</summary>
    public static List<string> Diff(ScreenState ours, ScreenState reference, Compared what, int max = 20)
    {
        var diffs = new List<string>();
        if (ours.Columns != reference.Columns || ours.Rows != reference.Rows)
        {
            diffs.Add($"size {ours.Columns}x{ours.Rows} vs {reference.Columns}x{reference.Rows}");
            return diffs;
        }
        if ((what & Compared.Cursor) != 0 && (ours.CursorX, ours.CursorY) != (reference.CursorX, reference.CursorY))
            diffs.Add($"cursor ({ours.CursorX},{ours.CursorY}) vs ({reference.CursorX},{reference.CursorY})");
        if ((what & Compared.AlternateScreen) != 0 && ours.AlternateScreen != reference.AlternateScreen)
            diffs.Add($"alternate screen {ours.AlternateScreen} vs {reference.AlternateScreen}");
        for (var r = 0; r < ours.Rows && diffs.Count < max; r++)
        for (var c = 0; c < ours.Columns && diffs.Count < max; c++)
        {
            var a = ours.Cells[r, c];
            var b = reference.Cells[r, c];
            if (a.Spacer || b.Spacer)
            {
                if (a.Spacer != b.Spacer && (what & Compared.Text) != 0) diffs.Add($"[{r},{c}] spacer {a.Spacer} vs {b.Spacer}");
                continue;
            }
            var ta = a.Text.Length == 0 ? " " : a.Text;
            var tb = b.Text.Length == 0 ? " " : b.Text;
            if ((what & Compared.Text) != 0 && ta != tb) diffs.Add($"[{r},{c}] text '{Escape(ta)}' vs '{Escape(tb)}'");
            var blank = ta == " " && tb == " ";
            if ((what & Compared.Colors) != 0)
            {
                if (a.Bg != b.Bg) diffs.Add($"[{r},{c}] bg {TermColor.Describe(a.Bg)} vs {TermColor.Describe(b.Bg)}");
                if (!blank && a.Fg != b.Fg) diffs.Add($"[{r},{c}] fg {TermColor.Describe(a.Fg)} vs {TermColor.Describe(b.Fg)} ('{Escape(ta)}')");
            }
            if (blank) continue; // the rendition of an empty cell is invisible except its background
            void Flag(Compared c2, CellFlags f, string name)
            {
                if ((what & c2) != 0 && ((a.Flags & f) != 0) != ((b.Flags & f) != 0)) diffs.Add($"[{r},{c}] {name} {(a.Flags & f) != 0} vs {(b.Flags & f) != 0} ('{Escape(ta)}')");
            }
            Flag(Compared.Bold, CellFlags.Bold, "bold");
            Flag(Compared.Italic, CellFlags.Italic, "italic");
            Flag(Compared.Inverse, CellFlags.Inverse, "inverse");
            Flag(Compared.Strike, CellFlags.Strikethrough, "strike");
            Flag(Compared.Dim, CellFlags.Dim, "dim");
            Flag(Compared.Blink, CellFlags.Blink, "blink");
            Flag(Compared.Invisible, CellFlags.Invisible, "invisible");
            if ((what & Compared.Underline) != 0)
            {
                var ua = (what & Compared.UnderlineStyle) != 0 ? a.Underline : a.Underline == UnderlineStyle.None ? UnderlineStyle.None : UnderlineStyle.Single;
                var ub = (what & Compared.UnderlineStyle) != 0 ? b.Underline : b.Underline == UnderlineStyle.None ? UnderlineStyle.None : UnderlineStyle.Single;
                if (ua != ub) diffs.Add($"[{r},{c}] underline {a.Underline} vs {b.Underline}");
            }
        }
        return diffs;
    }

    private static string Escape(string s) => string.Concat(s.Select(ch => ch < ' ' ? $"\\x{(int)ch:x2}" : ch.ToString()));
}

/// <summary>A reference terminal that renders a byte stream on a screen of a given size.</summary>
public interface IOracle : IDisposable
{
    string Name { get; }
    Compared Capabilities { get; }
    ScreenState Run(byte[] input, int columns, int rows);
}
