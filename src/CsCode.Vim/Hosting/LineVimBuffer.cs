using System.Text;

namespace CsCode.Vim;

/// <summary>
/// Self-contained <see cref="IVimBuffer"/> over a list of strings. Single-line edits are O(line length); a large batch is applied
/// in ONE pass over the buffer (the way the engine submits <c>:%s</c>, <c>dG</c> ...), so it stays usable on 100k+ line buffers.
/// Used by the performance tests and by anything that wants a Vim-editable string without a document model.
/// </summary>
public sealed class LineVimBuffer : IVimBuffer
{
    private List<string> _lines = [""];

    public LineVimBuffer(string text = "", string name = "[No Name]", string languageId = "plaintext")
    {
        Name = name;
        LanguageId = languageId;
        SetText(text);
    }

    public string Name { get; set; }
    public Uri? Uri { get; set; }
    public string LanguageId { get; set; }
    public int Version { get; private set; } = 1;
    public int LineCount => _lines.Count;
    public string EndOfLine { get; private set; } = "\n";
    public bool IsReadOnly { get; set; }

    public string GetLine(int line) => (uint)line < (uint)_lines.Count ? _lines[line] : "";
    public int GetLineLength(int line) => GetLine(line).Length;

    /// <summary>Replaces the whole content (not an undoable Vim change; use for test setup).</summary>
    public void SetText(string text)
    {
        EndOfLine = text.Contains("\r\n") ? "\r\n" : "\n";
        _lines = [.. text.Replace("\r\n", "\n").Split('\n')];
        Version++;
    }

    public string GetText() => string.Join('\n', _lines);

    public bool ApplyEdit(in VimEdit edit)
    {
        if (IsReadOnly) return false;
        var (s, e) = (Clamp(edit.Start), Clamp(edit.End));
        if (e < s) return false;
        if (s.Line == e.Line && edit.Text.IndexOf('\n') < 0)
        {
            var l = _lines[s.Line];
            _lines[s.Line] = edit.Text.Length == 0 && s.Character == 0 && e.Character == l.Length ? "" : string.Concat(l.AsSpan(0, s.Character), edit.Text, l.AsSpan(e.Character));
        }
        else
        {
            var merged = string.Concat(_lines[s.Line].AsSpan(0, s.Character), edit.Text, _lines[e.Line].AsSpan(e.Character));
            var parts = merged.Split('\n');
            var count = e.Line - s.Line + 1;
            if (parts.Length == count) for (var i = 0; i < count; i++) _lines[s.Line + i] = parts[i];
            else
            {
                _lines.RemoveRange(s.Line, count);
                _lines.InsertRange(s.Line, parts);
            }
        }
        Version++;
        return true;
    }

    public bool ApplyEdits(ReadOnlySpan<VimEdit> edits)
    {
        if (IsReadOnly) return false;
        if (edits.Length == 0) return true;
        if (edits.Length == 1) return ApplyEdit(edits[0]);
        var sorted = edits.ToArray();
        Array.Sort(sorted, (a, b) => a.Start.CompareTo(b.Start) is var c && c != 0 ? c : a.End.CompareTo(b.End));
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i] = sorted[i] with { Start = Clamp(sorted[i].Start), End = Clamp(sorted[i].End) };
            if (sorted[i].End < sorted[i].Start || (i > 0 && sorted[i].Start < sorted[i - 1].End)) return false;
        }

        var old = _lines;
        var result = new List<string>(old.Count + 16);
        var cur = new StringBuilder();
        int line = 0, col = 0;

        void CopyTo(int toLine, int toCol)
        {
            while (line < toLine)
            {
                if (cur.Length == 0 && col == 0) result.Add(old[line]);
                else { cur.Append(old[line], col, old[line].Length - col); result.Add(cur.ToString()); cur.Clear(); }
                line++;
                col = 0;
            }
            if (toCol > col) { cur.Append(old[line], col, toCol - col); col = toCol; }
        }

        foreach (var e in sorted)
        {
            CopyTo(e.Start.Line, e.Start.Character);
            var text = e.Text;
            var from = 0;
            for (int nl; (nl = text.IndexOf('\n', from)) >= 0; from = nl + 1)
            {
                cur.Append(text, from, nl - from);
                result.Add(cur.ToString());
                cur.Clear();
            }
            cur.Append(text, from, text.Length - from);
            line = e.End.Line;
            col = e.End.Character;
        }
        CopyTo(old.Count - 1, old[^1].Length);
        result.Add(cur.ToString());
        _lines = result;
        Version++;
        return true;
    }

    private TextPos Clamp(Core.Text.TextPosition p)
    {
        var line = Math.Clamp(p.Line, 0, _lines.Count - 1);
        return new TextPos(line, Math.Clamp(p.Character, 0, _lines[line].Length));
    }

    private readonly record struct TextPos(int Line, int Character)
    {
        public int CompareTo(TextPos o) => Line != o.Line ? Line.CompareTo(o.Line) : Character.CompareTo(o.Character);
        public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;
        public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;
        public static implicit operator Core.Text.TextPosition(TextPos p) => new(p.Line, p.Character);
    }
}
