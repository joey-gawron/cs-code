using System.Text;
using CsCode.Core.Text;

namespace CsCode.Panels.Org.Engine;

/// <summary>Text helpers for org files on disk: decoding, line endings, and applying <see cref="TextEdit"/>s to a string.</summary>
public static class OrgText
{
    /// <summary>UTF-8 (a BOM is dropped and reported), which is what org files are.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, out bool bom)
    {
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Encoding.UTF8.GetString(bom ? bytes[3..] : bytes);
    }

    public static byte[] Encode(string text, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (!bom) return body;
        var result = new byte[body.Length + 3];
        result[0] = 0xEF; result[1] = 0xBB; result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    /// <summary>The text's line ending: that of its first line break (<c>\n</c> when it has none).</summary>
    public static string DetectEol(string text)
    {
        var i = text.IndexOfAny(['\r', '\n']);
        if (i < 0 || text[i] == '\n') return "\n";
        return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
    }

    /// <summary>The edits with their line breaks (written as <c>\n</c>) in <paramref name="eol"/>.</summary>
    public static IReadOnlyList<TextEdit> WithEol(IReadOnlyList<TextEdit> edits, string eol) =>
        eol == "\n" ? edits : [.. edits.Select(e => e.NewText.Contains('\n') ? e with { NewText = e.NewText.Replace("\n", eol) } : e)];

    /// <summary>Applies non-overlapping edits (positions against <paramref name="text"/>) and returns the result.</summary>
    public static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) return text;
        var starts = LineStarts(text);
        int Offset(TextPosition p)
        {
            if (p.Line >= starts.Count) return text.Length;
            var lineStart = starts[p.Line];
            var lineEnd = p.Line + 1 < starts.Count ? starts[p.Line + 1] : text.Length;
            // the line's own text ends before its line break
            while (lineEnd > lineStart && (text[lineEnd - 1] == '\n' || text[lineEnd - 1] == '\r')) lineEnd--;
            return Math.Min(lineStart + p.Character, lineEnd);
        }
        var sb = new StringBuilder(text);
        foreach (var e in edits.Select(e => (Start: Offset(e.Range.Start), End: Offset(e.Range.End), e.NewText)).OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            sb.Remove(e.Start, e.End - e.Start).Insert(e.Start, e.NewText);
        return sb.ToString();
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] is '\n' or '\r') starts.Add(i + 1);
        }
        return starts;
    }
}
