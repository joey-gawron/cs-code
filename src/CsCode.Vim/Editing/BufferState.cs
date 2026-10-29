using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>One saved region: lines [Start, Start+NewCount) of the buffer currently hold the "new" text; <see cref="Old"/> is what to put back on undo.</summary>
internal sealed class UndoEntry
{
    public int Start;
    public string[] Old = [];
    public int NewCount;
}

/// <summary>One undoable change (everything between two undo syncs). Headers form a tree: Parent = the state before, Children = alternatives.</summary>
internal sealed class UndoHeader
{
    public int Seq;
    public readonly List<UndoEntry> Entries = [];
    public TextPosition Cursor;
    public TextPosition?[]? NamedMarks;
    public long TimeTicks;
    public UndoHeader? Parent;
    public readonly List<UndoHeader> Children = [];
    public UndoHeader? Preferred;
    public int SavedChars;
}

/// <summary>Vim's undo tree (u, Ctrl-R, :undo N, g-, g+, :earlier/:later). Linear history is the common case; branches appear after undo+change.</summary>
internal sealed class UndoTree
{
    public UndoHeader Root { get; private set; } = new() { Seq = 0 };
    public UndoHeader Current { get; set; }
    public int LastSeq { get; private set; }
    /// <summary>When true the next saved region starts a new header (a new undo step).</summary>
    public bool Synced { get; set; } = true;
    public long SavedChars { get; private set; }
    public const long MaxSavedChars = 48_000_000;

    public UndoTree() => Current = Root;

    public bool IsEmpty => Root.Children.Count == 0;

    /// <summary>The header currently receiving entries (only valid while !Synced).</summary>
    public UndoHeader Open => Current;

    public UndoHeader NewHeader(TextPosition cursor, TextPosition?[] marks, long ticks, int undoLevels)
    {
        var h = new UndoHeader { Seq = ++LastSeq, Cursor = cursor, NamedMarks = (TextPosition?[])marks.Clone(), Parent = Current, TimeTicks = ticks };
        Current.Children.Add(h);
        Current.Preferred = h;
        Current = h;
        Synced = false;
        Trim(Math.Max(1, undoLevels));
        return h;
    }

    public void AddSaved(UndoHeader h, int chars)
    {
        h.SavedChars += chars;
        SavedChars += chars;
    }

    /// <summary>Drops the oldest headers on the current path beyond the level / memory limits (they become the new baseline).</summary>
    private void Trim(int levels)
    {
        while (true)
        {
            var depth = 0;
            for (var h = Current; h.Parent is not null; h = h.Parent) depth++;
            if (depth <= levels && SavedChars <= MaxSavedChars) return;
            if (depth <= 1) return;
            // oldest header on the path
            var oldest = Current;
            while (oldest.Parent!.Parent is not null) oldest = oldest.Parent;
            SavedChars -= SubtreeChars(Root) - SubtreeChars(oldest);
            oldest.Entries.Clear();
            oldest.SavedChars = 0;
            oldest.Parent = null;
            Root = oldest;
        }
    }

    private static long SubtreeChars(UndoHeader h)
    {
        long n = h.SavedChars;
        foreach (var c in h.Children) n += SubtreeChars(c);
        return n;
    }

    public void Clear()
    {
        Root = new UndoHeader { Seq = 0 };
        Current = Root;
        LastSeq = 0;
        SavedChars = 0;
        Synced = true;
    }

    public UndoHeader? FindSeq(int seq)
    {
        var stack = new Stack<UndoHeader>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            if (h.Seq == seq) return h;
            foreach (var c in h.Children) stack.Push(c);
        }
        return null;
    }
}

/// <summary>Per-buffer Vim state: undo tree, marks, change list, last-insert position, the line saved for U. Holds no reference to the buffer.</summary>
internal sealed class BufferState
{
    public const int JumpListSize = 100;

    public UndoTree Undo { get; } = new();
    public TextPosition?[] Marks { get; } = new TextPosition?[26];
    public TextPosition? OpStart, OpEnd;            // '[ ']
    public TextPosition? VisualStart, VisualEnd;     // '< '>
    public VimMode VisualMode = VimMode.Visual;
    public int VisualCurswant;
    public TextPosition? LastChange;                // '.
    public TextPosition? LastInsert;                // '^
    public TextPosition? LastCursor;                // '"
    public readonly List<TextPosition> ChangeList = [];
    public int ChangeListIdx;
    public bool NewChange = true;
    public int KnownVersion = -1;
    /// <summary>Lines marked by an active :global (kept in sync with line inserts/deletes).</summary>
    public List<int>? GlobalLines;

    // 'U'
    public int ULineNr = -1;
    public string? ULineText;
    public int ULineCol;

    public void AddChange(TextPosition pos, int textWidth)
    {
        LastChange = pos;
        var add = false;
        if (NewChange || ChangeList.Count == 0)
        {
            if (ChangeList.Count == 0) add = true;
            else
            {
                var p = ChangeList[^1];
                if (p.Line != pos.Line) add = true;
                else
                {
                    var cols = textWidth == 0 ? 79 : textWidth;
                    add = p.Character + cols < pos.Character || pos.Character + cols < p.Character;
                }
            }
            if (add)
            {
                NewChange = false;
                if (ChangeList.Count == JumpListSize) ChangeList.RemoveAt(0);
                ChangeList.Add(pos);
            }
        }
        ChangeList[^1] = pos;
        ChangeListIdx = ChangeList.Count;
    }

    /// <summary>Vim's mark_adjust: lines [line1, line2] moved by <paramref name="amount"/> (or were deleted when <paramref name="delete"/>), later lines by <paramref name="amountAfter"/>.</summary>
    public void MarkAdjust(int line1, int line2, int amount, int amountAfter, bool delete)
    {
        TextPosition? Adj(TextPosition? p, bool nodel)
        {
            if (p is not { } q) return p;
            if (q.Line >= line1 && q.Line <= line2)
            {
                if (delete) return nodel ? new TextPosition(Math.Max(0, line1 - 1), q.Character) : null;
                return new TextPosition(q.Line + amount, q.Character);
            }
            if (amountAfter != 0 && q.Line > line2) return new TextPosition(q.Line + amountAfter, q.Character);
            return p;
        }
        for (var i = 0; i < Marks.Length; i++) Marks[i] = Adj(Marks[i], false);
        OpStart = Adj(OpStart, true); OpEnd = Adj(OpEnd, true);
        VisualStart = Adj(VisualStart, true); VisualEnd = Adj(VisualEnd, true);
        LastChange = Adj(LastChange, true); LastInsert = Adj(LastInsert, true); LastCursor = Adj(LastCursor, true);
        for (var i = 0; i < ChangeList.Count; i++) ChangeList[i] = Adj(ChangeList[i], true)!.Value;
        if (GlobalLines is { } gl)
            for (var i = 0; i < gl.Count; i++)
            {
                var l = gl[i];
                if (l < 0) continue;
                if (l >= line1 && l <= line2) gl[i] = delete ? -1 : l + amount;
                else if (amountAfter != 0 && l > line2) gl[i] = l + amountAfter;
            }
        if (ULineNr >= line1 && ULineNr <= line2 && delete) { ULineNr = -1; ULineText = null; }
        else if (ULineNr >= 0 && ULineNr > line2 && amountAfter != 0) ULineNr += amountAfter;
    }

    /// <summary>Clamps every mark to a buffer of <paramref name="lineCount"/> lines (after a foreign edit we cannot track precisely).</summary>
    public void ClampMarks(int lineCount)
    {
        TextPosition? C(TextPosition? p) => p is { } q && q.Line >= lineCount ? null : p;
        for (var i = 0; i < Marks.Length; i++) Marks[i] = C(Marks[i]);
        OpStart = C(OpStart); OpEnd = C(OpEnd); VisualStart = C(VisualStart); VisualEnd = C(VisualEnd);
        LastChange = C(LastChange); LastInsert = C(LastInsert); LastCursor = C(LastCursor);
        ChangeList.RemoveAll(p => p.Line >= lineCount);
        ChangeListIdx = Math.Min(ChangeListIdx, ChangeList.Count);
        if (ULineNr >= lineCount) { ULineNr = -1; ULineText = null; }
    }
}
