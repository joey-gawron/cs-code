using System.Text;
using CsCode.Core.Text;

namespace CsCode.Panels.Git.Engine;

/// <summary>
/// Builds the text of a file with only some of its changes applied: what staging or unstaging one hunk writes to the index (git's
/// <c>add -p</c> without a patch file: the new blob is hashed and put in the index directly, as VS Code does). Lines are compared WITH their
/// terminators, so a newline added at the end of the file, or a CRLF turned into LF, is a change of its own and survives the splice.
/// </summary>
public static class TextSplice
{
    /// <summary>The lines of <paramref name="text"/>, each with its terminator (the last one may have none). Empty text has no lines.</summary>
    public static List<string> SplitKeepEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines.Add(text.Substring(start, i + 1 - start));
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>The changes between <paramref name="original"/> and <paramref name="modified"/>, on lines with their terminators.</summary>
    public static IReadOnlyList<DiffChange> Diff(IReadOnlyList<string> original, IReadOnlyList<string> modified) =>
        LineDiff.Compute(original, modified, new LineDiffOptions(false, TimeSpan.FromSeconds(10))).Changes;

    /// <summary>
    /// <paramref name="original"/> with the changes towards <paramref name="modified"/> that <paramref name="select"/> picks applied, and the rest not.
    /// Returns null when no change was picked.
    /// </summary>
    public static string? Apply(string original, string modified, Func<DiffChange, bool> select)
    {
        var a = SplitKeepEnds(original);
        var b = SplitKeepEnds(modified);
        var picked = false;
        var sb = new StringBuilder(Math.Max(original.Length, modified.Length));
        var at = 0;
        foreach (var c in Diff(a, b))
        {
            if (!select(c)) continue;
            picked = true;
            for (; at < c.OriginalStart; at++) sb.Append(a[at]);
            for (var j = c.ModifiedStart; j < c.ModifiedEnd; j++) sb.Append(b[j]);
            at = c.OriginalEnd;
        }
        if (!picked) return null;
        for (; at < a.Count; at++) sb.Append(a[at]);
        return sb.ToString();
    }

    /// <summary>
    /// <see cref="Apply"/> on bytes: <paramref name="original"/> with the picked changes towards <paramref name="modified"/> applied. Both are
    /// compared byte for byte (each byte read as one Latin-1 character, which maps back to the same byte), so the lines that are not picked keep
    /// their exact bytes in any ASCII-compatible encoding (UTF-8, Windows-1252, Latin-1, Shift-JIS, ...) and nothing is decoded or re-encoded.
    /// A UTF-8 BOM is not a line's content: <paramref name="original"/>'s BOM, or its lack of one, is kept and <paramref name="modified"/>'s is
    /// ignored. Null when no change was picked.
    /// </summary>
    public static byte[]? ApplyBytes(byte[] original, byte[] modified, Func<DiffChange, bool> select)
    {
        var bom = HasUtf8Bom(original) ? 3 : 0;
        var modifiedBom = HasUtf8Bom(modified) ? 3 : 0;
        var next = Apply(Encoding.Latin1.GetString(original, bom, original.Length - bom), Encoding.Latin1.GetString(modified, modifiedBom, modified.Length - modifiedBom), select);
        if (next is null) return null;
        var body = Encoding.Latin1.GetBytes(next);
        return bom == 0 ? body : [.. original.AsSpan(0, bom), .. body];
    }

    private static bool HasUtf8Bom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    /// <summary>Binary as git sees it: a NUL in the first 8000 bytes (UTF-16 text included; hunks of such files are not staged).</summary>
    public static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0);

    /// <summary>
    /// True when change <paramref name="c"/> touches the modified-side lines <paramref name="startLine"/>..<paramref name="endLine"/> (0-based,
    /// inclusive). A pure deletion sits between two lines and is picked by either of them.
    /// </summary>
    public static bool TouchesModifiedLines(DiffChange c, int startLine, int endLine)
    {
        if (c.ModifiedLength == 0) return c.ModifiedStart >= startLine && c.ModifiedStart <= endLine + 1;
        return c.ModifiedStart <= endLine && c.ModifiedEnd > startLine;
    }

    /// <summary>True when change <paramref name="c"/> touches the original-side lines <paramref name="startLine"/>..<paramref name="endLine"/> (0-based, inclusive).</summary>
    public static bool TouchesOriginalLines(DiffChange c, int startLine, int endLine)
    {
        if (c.OriginalLength == 0) return c.OriginalStart >= startLine && c.OriginalStart <= endLine + 1;
        return c.OriginalStart <= endLine && c.OriginalEnd > startLine;
    }
}
