using System.Text;

namespace CsCode.Terminal.Emulator;

/// <summary>
/// The whole buffer as text for assistive technology (VS Code's "accessible buffer"): scrollback and screen, lines that wrapped at the right
/// margin joined into one, trailing blanks removed, the cursor as an offset into <see cref="Text"/>, and where each shell-integration prompt
/// starts (so a reader can jump from command to command).
/// </summary>
/// <param name="CursorOffset">Offset of the cursor in <see cref="Text"/> (-1: the cursor is not in the text, e.g. hidden by the alternate screen).</param>
/// <param name="PromptOffsets">Offsets of the lines shell integration marked as prompts, in order.</param>
/// <param name="Truncated">The oldest lines were left out to stay within the size asked for.</param>
public sealed record TerminalAccessibleBuffer(string Text, int CursorOffset, IReadOnlyList<int> PromptOffsets, bool Truncated);

/// <summary>Text views of a terminal for screen readers (the visible rows from a snapshot on the UI thread; the whole buffer on the worker).</summary>
public static class TerminalAccessibility
{
    /// <summary>
    /// The visible rows of <paramref name="snapshot"/> as text (soft-wrapped rows joined, trailing blanks trimmed, one line per hard line) and the
    /// cursor's offset in it (-1 when it is not in view). Cheap: a screenful of cells; safe on the UI thread.
    /// </summary>
    public static (string Text, int CursorOffset) VisibleText(TerminalSnapshot snapshot)
    {
        var sb = new StringBuilder(snapshot.RowCount * (snapshot.Columns + 1));
        var cursor = -1;
        var lineStart = 0;
        for (var r = 0; r < snapshot.Rows.Length && r < snapshot.RowCount; r++)
        {
            var row = snapshot.Rows[r];
            if (r > 0 && !row.IsWrapped)
            {
                Trim(sb, lineStart, ref cursor);
                sb.Append('\n');
                lineStart = sb.Length;
            }
            var rowStart = sb.Length;
            row.AppendText(sb);
            if (r == snapshot.CursorY && snapshot.CursorX >= 0)
            {
                // the cursor may sit past the row's written cells (after the prompt's blank): pad to it
                var offset = OffsetOfColumn(row, snapshot.CursorX);
                while (sb.Length - rowStart < offset) sb.Append(' ');
                cursor = rowStart + offset;
            }
        }
        Trim(sb, lineStart, ref cursor);
        // trailing empty lines carry nothing (the cursor's line stays)
        var end = sb.Length;
        while (end > 0 && sb[end - 1] == '\n' && end - 1 >= cursor) end--;
        return (sb.ToString(0, end), cursor);
    }

    /// <summary>The offset in a row's text (spacers skipped, clusters expanded) of <paramref name="column"/>.</summary>
    private static int OffsetOfColumn(TerminalRow row, int column)
    {
        var offset = 0;
        for (var c = 0; c < column; c++)
        {
            var cell = row[c];
            if (cell.IsSpacer) continue;
            offset += cell.Rune switch
            {
                0 => 1,
                < 0 => row.CellText(c).Length,
                < 0x10000 => 1,
                _ => 2,
            };
        }
        return offset;
    }

    private static void Trim(StringBuilder sb, int from, ref int cursor)
    {
        var length = sb.Length;
        while (length > from && sb[length - 1] == ' ' && length - 1 >= cursor) length--;
        sb.Length = length;
    }
}

public sealed partial class VtEmulator
{
    /// <summary>
    /// The accessible buffer (worker only): scrollback and screen as text, at most <paramref name="maxChars"/> characters (the newest kept).
    /// On the alternate screen (a full-screen program) only its rows.
    /// </summary>
    public TerminalAccessibleBuffer AccessibleBuffer(int maxChars = 4 * 1024 * 1024)
    {
        var (first, last) = FindRange();
        var sb = new StringBuilder();
        var prompts = new List<int>();
        var cursorLine = IsAlternateScreen ? CursorY : ScreenTop + CursorY;
        var cursor = -1;
        var lineStart = 0;
        for (var l = first; l <= last; l++)
        {
            var line = LineAtAbsolute(l);
            if (l > first && !line.Wrapped)
            {
                TrimEnd(sb, lineStart, cursor);
                sb.Append('\n');
                lineStart = sb.Length;
            }
            if (line.Mark == RowMark.PromptStart && !line.Wrapped) prompts.Add(sb.Length);
            var rowStart = sb.Length;
            AppendLineText(line, sb); // read in place: no row copies cached for the whole scrollback
            if (l == cursorLine)
            {
                var offset = 0;
                for (var c = 0; c < Math.Min(CursorX, line.Cells.Length); c++)
                {
                    var cell = line.Cells[c];
                    if (!cell.IsSpacer) offset += cell.Rune < 0 ? line.ClusterText(cell.Rune).Length : cell.Rune >= 0x10000 ? 2 : 1;
                }
                while (sb.Length - rowStart < offset) sb.Append(' ');
                cursor = rowStart + offset;
            }
        }
        TrimEnd(sb, lineStart, cursor);
        var end = sb.Length;
        while (end > 0 && sb[end - 1] == '\n' && end - 1 >= cursor) end--;
        sb.Length = end;
        if (sb.Length <= maxChars) return new TerminalAccessibleBuffer(sb.ToString(), cursor, prompts, false);
        // keep the newest text, from a line start
        var cut = sb.Length - maxChars;
        var newline = sb.ToString(cut, Math.Min(sb.Length - cut, 4096)).IndexOf('\n', StringComparison.Ordinal);
        if (newline >= 0) cut += newline + 1;
        return new TerminalAccessibleBuffer(sb.ToString(cut, sb.Length - cut), cursor >= cut ? cursor - cut : -1,
            prompts.Where(p => p >= cut).Select(p => p - cut).ToList(), true);

        static void TrimEnd(StringBuilder sb, int from, int cursor)
        {
            var length = sb.Length;
            while (length > from && sb[length - 1] == ' ' && length - 1 >= cursor) length--;
            sb.Length = length;
        }
    }
}
