namespace CsCode.Core.Text;

// Additive Core primitive added by the merge-conflict work (docs/SOURCE-CONTROL.md, "Merge conflicts"): the parser of conflict markers that the
// editor's inline conflict resolution (decorations, code lens, accept commands) and the Source Control window's staging check share. UI-free and
// allocation-light; it works on any text, git or not.

/// <summary>
/// One part of a conflict block: the marker line that opens it (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> for the current side, <c>|||||||</c> for a
/// common ancestor, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> (the footer) for the incoming side) and its content lines. Lines are 0-based;
/// <see cref="ContentEndLine"/> is exclusive, so an empty side has <see cref="ContentStartLine"/> == <see cref="ContentEndLine"/>. Offsets are into
/// the text that was parsed: the content runs from <see cref="ContentStart"/> to <see cref="ContentEnd"/>, the terminator of its last line
/// included (it always ends at the start of the next marker line). <see cref="Name"/> is what follows the marker (<c>HEAD</c>, a branch, a commit).
/// </summary>
public sealed record MergeConflictRegion(int HeaderLine, int ContentStartLine, int ContentEndLine, int ContentStart, int ContentEnd, string Name)
{
    public int ContentLength => ContentEnd - ContentStart;

    /// <summary>The content as a range of whole lines (start of its first line to the start of the next marker line).</summary>
    public TextRange ContentRange => new(new TextPosition(ContentStartLine, 0), new TextPosition(ContentEndLine, 0));
}

/// <summary>Which part of a conflict block a position is in.</summary>
public enum MergeConflictPart { None, CurrentHeader, Current, CommonAncestors, Splitter, Incoming, IncomingFooter }

/// <summary>What a resolution keeps: the current side (ours), the incoming side (theirs), or both (current first).</summary>
public enum MergeConflictChoice { Current, Incoming, Both }

/// <summary>
/// One conflict block, as git (and diff3, zdiff3) writes it:
/// <code>
/// &lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD          ← StartLine (Current.HeaderLine)
/// current lines
/// ||||||| base          ← optional, one or more (CommonAncestors)
/// base lines
/// =======               ← SplitterLine
/// incoming lines
/// &gt;&gt;&gt;&gt;&gt;&gt;&gt; feature       ← EndLine (Incoming.HeaderLine)
/// </code>
/// <see cref="Start"/> is the offset of the first marker line, <see cref="End"/> the offset just past the footer line (its terminator included,
/// when it has one). <see cref="Range"/> is the same as a text range.
/// </summary>
public sealed record MergeConflict(int Start, int End, int StartLine, int EndLine, TextPosition EndPosition, MergeConflictRegion Current,
    IReadOnlyList<MergeConflictRegion> CommonAncestors, int SplitterLine, MergeConflictRegion Incoming)
{
    /// <summary>The whole block: from the start of the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line to past the <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> line's terminator.</summary>
    public TextRange Range => new(new TextPosition(StartLine, 0), EndPosition);

    /// <summary>True when <paramref name="line"/> (0-based) is one of the block's lines, markers included.</summary>
    public bool ContainsLine(int line) => line >= StartLine && line <= EndLine;

    /// <summary>The part of the block <paramref name="line"/> is on (<see cref="MergeConflictPart.None"/> outside it).</summary>
    public MergeConflictPart PartAt(int line)
    {
        if (!ContainsLine(line)) return MergeConflictPart.None;
        if (line == StartLine) return MergeConflictPart.CurrentHeader;
        if (line == EndLine) return MergeConflictPart.IncomingFooter;
        if (line == SplitterLine) return MergeConflictPart.Splitter;
        if (line > SplitterLine) return MergeConflictPart.Incoming;
        return CommonAncestors.Count > 0 && line >= CommonAncestors[0].HeaderLine ? MergeConflictPart.CommonAncestors : MergeConflictPart.Current;
    }

    /// <summary>
    /// The text that replaces the block for <paramref name="choice"/> (VS Code's rule): the current content, the incoming content, or the current
    /// content followed by the incoming content. <paramref name="text"/> is the text that was parsed.
    /// </summary>
    public string Resolve(string text, MergeConflictChoice choice) => choice switch
    {
        MergeConflictChoice.Current => text.Substring(Current.ContentStart, Current.ContentLength),
        MergeConflictChoice.Incoming => text.Substring(Incoming.ContentStart, Incoming.ContentLength),
        _ => string.Concat(text.AsSpan(Current.ContentStart, Current.ContentLength), text.AsSpan(Incoming.ContentStart, Incoming.ContentLength)),
    };
}

/// <summary>
/// Finds conflict blocks in text, with VS Code's merge-conflict rules: a block starts at a line beginning with <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>,
/// may hold common-ancestor sections (lines beginning with <c>|||||||</c>, diff3 / zdiff3) before its splitter (a line that is exactly
/// <c>=======</c>), and ends at a line beginning with <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>. A block without a splitter is not a conflict; a start
/// marker inside an open block (nested or broken markers) stops the scan: the blocks found before it are kept, nothing after it is reported.
/// Lines end with <c>\n</c>, <c>\r\n</c> or <c>\r</c>.
/// </summary>
public static class MergeConflictParser
{
    public const string StartMarker = "<<<<<<<";
    public const string CommonAncestorsMarker = "|||||||";
    public const string SplitterMarker = "=======";
    public const string EndMarker = ">>>>>>>";

    /// <summary>A cheap test (one vectorised search): false means <paramref name="text"/> holds no conflict.</summary>
    public static bool MayContainConflicts(ReadOnlySpan<char> text) => text.IndexOf(StartMarker.AsSpan(), StringComparison.Ordinal) >= 0;

    /// <summary>The conflict blocks of <paramref name="text"/>, in document order.</summary>
    public static IReadOnlyList<MergeConflict> Parse(string text, CancellationToken ct = default)
    {
        if (!MayContainConflicts(text)) return [];
        var found = new List<MergeConflict>();
        // the open block: its start marker, ancestors, splitter
        var open = false;
        int startLine = 0, startOffset = 0, startContent = 0, splitterLine = -1, splitterStart = 0, splitterEnd = 0;
        string startName = "";
        var ancestors = new List<(int Line, int Offset, int ContentStart, string Name)>();
        var line = 0;
        var offset = 0;
        while (offset <= text.Length)
        {
            if ((line & 1023) == 0) ct.ThrowIfCancellationRequested();
            var span = text.AsSpan(offset);
            var eolAt = span.IndexOfAny('\n', '\r');
            var length = eolAt < 0 ? span.Length : eolAt;
            var next = eolAt < 0 ? text.Length : offset + eolAt + (span[eolAt] == '\r' && eolAt + 1 < span.Length && span[eolAt + 1] == '\n' ? 2 : 1);
            var content = span[..length];
            if (content.Length >= 7)
            {
                if (content.StartsWith(StartMarker, StringComparison.Ordinal))
                {
                    if (open) break; // a start marker inside a block: give up, keep what was found (VS Code)
                    open = true;
                    startLine = line;
                    startOffset = offset;
                    startContent = next;
                    startName = NameOf(content);
                    splitterLine = -1;
                    ancestors.Clear();
                }
                else if (open && splitterLine < 0 && content.StartsWith(CommonAncestorsMarker, StringComparison.Ordinal))
                    ancestors.Add((line, offset, next, NameOf(content)));
                else if (open && splitterLine < 0 && content.SequenceEqual(SplitterMarker))
                {
                    splitterLine = line;
                    splitterStart = offset;
                    splitterEnd = next;
                }
                else if (open && content.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    if (splitterLine >= 0)
                    {
                        var (currentEndLine, currentEnd) = ancestors.Count > 0 ? (ancestors[0].Line, ancestors[0].Offset) : (splitterLine, splitterStart);
                        var current = new MergeConflictRegion(startLine, startLine + 1, currentEndLine, startContent, currentEnd, startName);
                        var common = new MergeConflictRegion[ancestors.Count];
                        for (var i = 0; i < ancestors.Count; i++)
                        {
                            var a = ancestors[i];
                            var (endLine, endOffset) = i + 1 < ancestors.Count ? (ancestors[i + 1].Line, ancestors[i + 1].Offset) : (splitterLine, splitterStart);
                            common[i] = new MergeConflictRegion(a.Line, a.Line + 1, endLine, a.ContentStart, endOffset, a.Name);
                        }
                        var incoming = new MergeConflictRegion(line, splitterLine + 1, line, splitterEnd, offset, NameOf(content));
                        var endPosition = eolAt < 0 ? new TextPosition(line, length) : new TextPosition(line + 1, 0);
                        found.Add(new MergeConflict(startOffset, next, startLine, line, endPosition, current, common, splitterLine, incoming));
                    }
                    open = false; // a footer without a splitter: not a conflict, the next start marker begins afresh
                    ancestors.Clear();
                }
            }
            if (eolAt < 0) break;
            offset = next;
            line++;
        }
        return found;
    }

    private static string NameOf(ReadOnlySpan<char> markerLine) => markerLine.Length > 8 ? markerLine[8..].ToString() : "";

    /// <summary>
    /// Whether <paramref name="text"/> still has a conflict marker line, as VS Code's git support checks before staging a conflicted file: a line
    /// beginning with <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> or <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> followed by whitespace, or a line that is exactly
    /// <c>=======</c>.
    /// </summary>
    public static bool HasConflictMarkers(ReadOnlySpan<char> text)
    {
        if (text.IndexOf(StartMarker.AsSpan(), StringComparison.Ordinal) < 0 && text.IndexOf(EndMarker.AsSpan(), StringComparison.Ordinal) < 0
            && text.IndexOf(SplitterMarker.AsSpan(), StringComparison.Ordinal) < 0) return false;
        while (true)
        {
            var eol = text.IndexOfAny('\n', '\r');
            var line = eol < 0 ? text : text[..eol];
            if (line.Length >= 7)
            {
                if (line.SequenceEqual(SplitterMarker)) return true;
                if ((line.StartsWith(StartMarker, StringComparison.Ordinal) || line.StartsWith(EndMarker, StringComparison.Ordinal)) && line.Length > 7 && char.IsWhiteSpace(line[7])) return true;
            }
            if (eol < 0) return false;
            text = text[(eol + 1)..];
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every conflict of <paramref name="conflicts"/> (parsed from it) replaced by its resolution for
    /// <paramref name="choice"/>: what the whole file would be after Accept All Current / Incoming / Both.
    /// </summary>
    public static string ResolveAll(string text, IReadOnlyList<MergeConflict> conflicts, MergeConflictChoice choice)
    {
        if (conflicts.Count == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var at = 0;
        foreach (var c in conflicts)
        {
            sb.Append(text, at, c.Start - at);
            sb.Append(c.Resolve(text, choice));
            at = c.End;
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }
}
