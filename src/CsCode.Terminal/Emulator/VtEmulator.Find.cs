using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Terminal.Emulator;

/// <summary>
/// A match of <see cref="VtEmulator.Find(TerminalFindOptions, long, int, bool)"/>: from (<see cref="Line"/>, <see cref="StartColumn"/>) to
/// (<see cref="EndLine"/>, <see cref="EndColumn"/>), absolute rows and inclusive columns (a wide character's two cells). A match in a line that
/// wrapped at the right margin can continue on the next rows.
/// </summary>
public readonly record struct TerminalMatch(long Line, int StartColumn, long EndLine, int EndColumn)
{
    /// <summary>A match within one row.</summary>
    public TerminalMatch(long line, int startColumn, int endColumn) : this(line, startColumn, line, endColumn) { }

    /// <summary>The columns this match covers on row <paramref name="line"/> of a grid <paramref name="columns"/> wide, or null.</summary>
    public (int Start, int End)? ColumnsOn(long line, int columns)
    {
        if (line < Line || line > EndLine) return null;
        var start = line == Line ? StartColumn : 0;
        var end = line == EndLine ? EndColumn : columns - 1;
        return end < start ? null : (start, Math.Min(end, columns - 1));
    }

    public bool StartsBefore(TerminalMatch other) => Line < other.Line || (Line == other.Line && StartColumn < other.StartColumn);
}

/// <summary>What Find looks for (VS Code's terminal find widget: Match Case, Match Whole Word, Use Regular Expression).</summary>
public sealed record TerminalFindOptions(string Query, bool MatchCase = false, bool WholeWord = false, bool Regex = false)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private Regex? _regex;
    private bool _compiled;

    /// <summary>A regular expression that does not compile (Find then finds nothing).</summary>
    public bool IsInvalid => Regex && Compiled is null && Query.Length > 0;

    internal Regex? Compiled
    {
        get
        {
            if (_compiled) return _regex;
            _compiled = true;
            if (!Regex || Query.Length == 0) return null;
            try
            {
                _regex = new Regex(Query, (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, Timeout);
            }
            catch (ArgumentException) { _regex = null; }
            return _regex;
        }
    }
}

/// <summary>Counts for the find widget: how many matches (up to the cap) and which one the current is (1-based, 0: none).</summary>
public readonly record struct TerminalFindCount(int Total, int Index, bool Capped);

public sealed partial class VtEmulator
{
    /// <summary>The most matches counted (VS Code's find widget stops at 1000 as well).</summary>
    public const int MaxFindCount = 1000;

    /// <summary>The old single-option search (tests): plain text, optionally case-sensitive.</summary>
    public TerminalMatch? Find(string query, long fromLine, int fromColumn, bool backward, bool matchCase) =>
        Find(new TerminalFindOptions(query, matchCase), fromLine, fromColumn, backward);

    /// <summary>
    /// Find in terminal (VS Code's Ctrl+F): the next match strictly after (<paramref name="backward"/>: before) the cell (<paramref name="fromLine"/>,
    /// <paramref name="fromColumn"/>), line by line through the scrollback and the screen (the alternate screen: its rows only), wrapping around
    /// once. Lines that wrapped at the right margin are searched as one line, so a match can continue on the next row. A start outside the
    /// rows starts at the end the search moves away from (a backward search from <see cref="long.MaxValue"/> begins at the bottom). Worker only.
    /// </summary>
    public TerminalMatch? Find(TerminalFindOptions options, long fromLine, int fromColumn, bool backward)
    {
        if (options.Query.Length == 0 || options.IsInvalid) return null;
        var (first, last) = FindRange();
        if (last < first) return null;
        var inRange = fromLine >= first && fromLine <= last;
        var from = new TerminalMatch(fromLine, fromColumn, fromLine, fromColumn);
        var text = new StringBuilder();
        var cells = new List<(long Line, int Column)>();
        (long, long) Next((long Start, long End) l) => backward
            ? l.Start <= first ? LogicalLine(last, first, last) : LogicalLine(l.Start - 1, first, last)
            : l.End >= last ? LogicalLine(first, first, last) : LogicalLine(l.End + 1, first, last);

        // the start's logical line: the matches on the far side of the start
        var home = LogicalLine(inRange ? fromLine : backward ? last : first, first, last);
        var here = MatchesIn(options, home.Start, home.End, text, cells);
        if (backward)
        {
            for (var i = here.Count - 1; i >= 0; i--)
                if (!inRange || here[i].StartsBefore(from)) return here[i];
        }
        else
        {
            foreach (var m in here)
                if (!inRange || from.StartsBefore(m)) return m;
        }
        // every other logical line once, wrapping around (logical lines partition the rows: the walk comes back home)
        for (var line = Next(home); line != home; line = Next(line))
        {
            var matches = MatchesIn(options, line.Item1, line.Item2, text, cells);
            if (matches.Count > 0) return backward ? matches[^1] : matches[0];
        }
        // back home: the match on the near side of the start (the start's own match when it is the only one)
        return inRange && here.Count > 0 ? backward ? here[^1] : here[0] : null;
    }

    /// <summary>All matches in the logical lines that touch rows <paramref name="fromLine"/>..<paramref name="toLine"/> (the visible rows), at most <paramref name="max"/>.</summary>
    public IReadOnlyList<TerminalMatch> FindAll(TerminalFindOptions options, long fromLine, long toLine, int max = MaxFindCount)
    {
        var result = new List<TerminalMatch>();
        if (options.Query.Length == 0 || options.IsInvalid) return result;
        var (first, last) = FindRange();
        fromLine = Math.Max(fromLine, first);
        toLine = Math.Min(toLine, last);
        var text = new StringBuilder();
        var cells = new List<(long, int)>();
        for (var line = fromLine; line <= toLine && result.Count < max;)
        {
            var (start, end) = LogicalLine(line, first, last);
            foreach (var m in MatchesIn(options, start, end, text, cells))
            {
                if (m.EndLine < fromLine || m.Line > toLine) continue;
                result.Add(m);
                if (result.Count >= max) break;
            }
            line = end + 1;
        }
        return result;
    }

    /// <summary>How many matches the rows hold (up to <see cref="MaxFindCount"/>) and the 1-based position of <paramref name="current"/> among them.</summary>
    public TerminalFindCount CountMatches(TerminalFindOptions options, TerminalMatch? current)
    {
        if (options.Query.Length == 0 || options.IsInvalid) return default;
        var (first, last) = FindRange();
        var text = new StringBuilder();
        var cells = new List<(long, int)>();
        int total = 0, index = 0;
        for (var line = first; line <= last;)
        {
            var (start, end) = LogicalLine(line, first, last);
            foreach (var m in MatchesIn(options, start, end, text, cells))
            {
                if (total >= MaxFindCount) return new TerminalFindCount(total, index, true);
                total++;
                if (current is { } c && m.Line == c.Line && m.StartColumn == c.StartColumn) index = total;
            }
            line = end + 1;
        }
        return new TerminalFindCount(total, index, false);
    }

    private (long First, long Last) FindRange() => IsAlternateScreen ? (0L, (long)Rows - 1) : (ScrollbackTop, ScreenTop + Rows - 1);

    /// <summary>The rows of the logical line holding <paramref name="line"/>: back to the row that did not continue a wrapped one, on through its continuations.</summary>
    private (long Start, long End) LogicalLine(long line, long first, long last)
    {
        var start = line;
        while (start > first && LineAtAbsolute(start).Wrapped) start--;
        var end = line;
        while (end < last && LineAtAbsolute(end + 1).Wrapped) end++;
        return (start, end);
    }

    /// <summary>The matches in the logical line of rows <paramref name="start"/>..<paramref name="end"/>, in order (zero-length regex matches skipped).</summary>
    private List<TerminalMatch> MatchesIn(TerminalFindOptions options, long start, long end, StringBuilder text, List<(long Line, int Column)> cells)
    {
        text.Clear();
        cells.Clear();
        for (var l = start; l <= end; l++) AppendRowText(LineAtAbsolute(l), l, text, cells, trimTrailing: l == end);
        var result = new List<TerminalMatch>();
        if (text.Length == 0) return result;
        var s = text.ToString();
        void Add(int index, int length)
        {
            if (length == 0 || options.WholeWord && !IsWholeWord(s, index, length)) return;
            var (line, column) = cells[index];
            var (endLine, endColumn) = cells[index + length - 1];
            if (LineAtAbsolute(endLine).Cells[endColumn].IsWide && endColumn + 1 < Columns) endColumn++;
            result.Add(new TerminalMatch(line, column, endLine, endColumn));
        }
        if (options.Compiled is { } regex)
        {
            try
            {
                for (var m = regex.Match(s); m.Success; m = m.NextMatch()) Add(m.Index, m.Length);
            }
            catch (RegexMatchTimeoutException) { } // a pathological expression: what was found so far
        }
        else
        {
            var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (var i = s.IndexOf(options.Query, comparison); i >= 0; i = i + 1 < s.Length ? s.IndexOf(options.Query, i + 1, comparison) : -1)
                Add(i, options.Query.Length);
        }
        return result;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWholeWord(string s, int index, int length) =>
        (index == 0 || !IsWordChar(s[index - 1]) || !IsWordChar(s[index])) &&
        (index + length >= s.Length || !IsWordChar(s[index + length]) || !IsWordChar(s[index + length - 1]));

    private Line LineAtAbsolute(long absolute) =>
        IsAlternateScreen ? _screen.Lines[absolute]
        : absolute >= ScreenTop ? _screen.Lines[absolute - ScreenTop] : Scrollback.LineAt((int)(absolute - ScrollbackTop));

    /// <summary>
    /// A row's text (blanks as spaces, spacers skipped) and, per UTF-16 unit, the cell it came from. A row that continues on the next one keeps
    /// its trailing blanks (they are part of the line); the last row of a logical line loses them.
    /// </summary>
    private static void AppendRowText(Line line, long absolute, StringBuilder text, List<(long, int)> columns, bool trimTrailing)
    {
        var cells = line.Cells;
        var count = cells.Length;
        if (trimTrailing)
            while (count > 0 && (cells[count - 1].Rune is 0 or ' ') && !cells[count - 1].IsSpacer) count--;
        for (var c = 0; c < count; c++)
        {
            var cell = cells[c];
            if (cell.IsSpacer) continue;
            var rune = cell.Rune;
            if (rune == 0) { text.Append(' '); columns.Add((absolute, c)); }
            else if (rune > 0 && rune < 0x10000) { text.Append((char)rune); columns.Add((absolute, c)); }
            else
            {
                var s = rune > 0 ? char.ConvertFromUtf32(rune) : line.ClusterText(rune);
                text.Append(s);
                for (var k = 0; k < s.Length; k++) columns.Add((absolute, c));
            }
        }
    }
}
