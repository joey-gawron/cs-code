using TextMateSharp.Grammars;

namespace CsCode.Editor.TextMate;

/// <summary>
/// Per-document tokenisation state: for each line an <c>int[]</c> of <c>(startColumn, scopeStackId)</c> pairs (null = not tokenised), plus
/// the TextMate rule-stack at the START of every <see cref="CheckpointInterval"/>th line (so re-tokenising after an edit resumes within a
/// few lines of it instead of holding a rule stack per line, which is the classic memory hog).
///
/// Who touches it: the UI thread applies structural edits (<see cref="ApplyChange"/>) and reads lines for rendering; the editor worker
/// publishes freshly computed lines (<see cref="Publish"/>). A short lock serialises them (critical sections are a few reference copies),
/// and every publication carries the document version it was computed for — a stale one is refused, never applied.
/// </summary>
internal sealed class LineTokenStore
{
    public const int CheckpointInterval = 8;

    private readonly object _gate = new();
    private readonly List<int[]?> _lines = new();
    private IStateStack?[] _checkpoints = new IStateStack?[16];
    private int _validLines;
    private int _version;
    private long _tokenInts;

    public int Version { get { lock (_gate) return _version; } }
    public int LineCount { get { lock (_gate) return _lines.Count; } }

    /// <summary>Lines [0, ValidLines) are consistent with the current text: tokens AND the state chain. Lines beyond may hold stale tokens (still shown until refreshed).</summary>
    public int ValidLines { get { lock (_gate) return _validLines; } }

    /// <summary>Approximate managed bytes: token arrays (4 B per int + 24 B header) + list slots + checkpoints.</summary>
    public long EstimatedBytes
    {
        get
        {
            lock (_gate) return _tokenInts * 4 + _lines.Count * 32L + _checkpoints.Length * 120L;
        }
    }

    public void Reset(int lineCount, int version)
    {
        lock (_gate)
        {
            _lines.Clear();
            for (var i = 0; i < lineCount; i++) _lines.Add(null);
            Array.Clear(_checkpoints);
            _validLines = 0;
            _tokenInts = 0;
            _version = version;
        }
    }

    /// <summary>
    /// A change inside line <paramref name="startLine"/> that removed <paramref name="removedBreaks"/> and inserted
    /// <paramref name="insertedBreaks"/> line breaks. Existing tokens stay (stale) so the line does not flash plain while the worker
    /// catches up; the chain is cut at the edited line.
    /// </summary>
    public void ApplyChange(int startLine, int removedBreaks, int insertedBreaks)
    {
        lock (_gate)
        {
            if (startLine >= _lines.Count) return;
            if (removedBreaks > 0)
            {
                var n = Math.Min(removedBreaks, _lines.Count - startLine - 1);
                for (var i = 0; i < n; i++) _tokenInts -= _lines[startLine + 1 + i]?.Length ?? 0;
                _lines.RemoveRange(startLine + 1, n);
            }
            if (insertedBreaks > 0) _lines.InsertRange(startLine + 1, new int[]?[insertedBreaks]);
            if (_validLines > startLine) _validLines = startLine;
        }
    }

    public void SetVersion(int version)
    {
        lock (_gate) _version = version;
    }

    /// <summary>The token pairs of a line, or null when the line has not been tokenised (render it plain).</summary>
    public int[]? GetTokens(int line)
    {
        lock (_gate) return line >= 0 && line < _lines.Count ? _lines[line] : null;
    }

    /// <summary>True when every line in [first, last] has tokens (stale or not).</summary>
    public bool HasTokens(int first, int last)
    {
        lock (_gate)
        {
            last = Math.Min(last, _lines.Count - 1);
            for (var i = Math.Max(0, first); i <= last; i++)
                if (_lines[i] is null) return false;
            return true;
        }
    }

    /// <summary>
    /// The nearest resumable point at or before <paramref name="line"/>: the first line of a checkpoint block whose state is known and
    /// whose preceding lines are chain-valid. Line 0 always qualifies (initial state, null).
    /// </summary>
    public (int Line, IStateStack? State) ResumePoint(int line)
    {
        lock (_gate)
        {
            var c = Math.Min(line, _validLines) / CheckpointInterval * CheckpointInterval;
            while (c > 0 && (c / CheckpointInterval >= _checkpoints.Length || _checkpoints[c / CheckpointInterval] is null)) c -= CheckpointInterval;
            return (c, c == 0 ? null : _checkpoints[c / CheckpointInterval]);
        }
    }

    /// <summary>
    /// Stores freshly tokenised lines [<paramref name="firstLine"/>, +count). Refused (false) when the document moved on since
    /// <paramref name="version"/>. <paramref name="checkpoints"/> are (line, stateAtStartOfLine); <paramref name="chain"/> = the run continues
    /// the valid prefix (not an approximate island), so the valid prefix may grow over it.
    /// </summary>
    public bool Publish(int version, int firstLine, IReadOnlyList<int[]> lines, IReadOnlyList<(int Line, IStateStack State)> checkpoints, bool chain)
    {
        lock (_gate)
        {
            if (_version != version || firstLine < 0 || firstLine + lines.Count > _lines.Count) return false;
            for (var i = 0; i < lines.Count; i++)
            {
                _tokenInts -= _lines[firstLine + i]?.Length ?? 0;
                _lines[firstLine + i] = lines[i];
                _tokenInts += lines[i].Length;
            }
            if (!chain) return true;
            foreach (var (line, state) in checkpoints)
            {
                var slot = line / CheckpointInterval;
                if (slot >= _checkpoints.Length) Array.Resize(ref _checkpoints, Math.Max(slot + 1, _checkpoints.Length * 2));
                _checkpoints[slot] = state;
            }
            if (firstLine <= _validLines) _validLines = Math.Max(_validLines, firstLine + lines.Count);
            return true;
        }
    }

    /// <summary>
    /// Forgets the tokens of lines [<paramref name="first"/>, <paramref name="last"/>] (they render plain and are recomputed when they come
    /// into view). Used by the tree-sitter engine for far-away lines whose syntax changed; refused when the document moved on.
    /// </summary>
    public void ClearLines(int version, int first, int last)
    {
        lock (_gate)
        {
            if (_version != version) return;
            last = Math.Min(last, _lines.Count - 1);
            for (var i = Math.Max(0, first); i <= last; i++)
            {
                if (_lines[i] is not { } t) continue;
                _tokenInts -= t.Length;
                _lines[i] = null;
            }
        }
    }

    /// <summary>
    /// Releases memory. Moderate: token arrays outside [keepFrom, keepTo] go (the cheap checkpoints stay, so the lines come back fast when
    /// scrolled to). Aggressive: everything, including checkpoints. Returns approximate bytes freed.
    /// </summary>
    public long Trim(bool aggressive, int keepFrom, int keepTo)
    {
        lock (_gate)
        {
            var before = _tokenInts * 4 + _checkpoints.Length * 120L;
            for (var i = 0; i < _lines.Count; i++)
            {
                if (!aggressive && i >= keepFrom && i <= keepTo) continue;
                if (_lines[i] is { } t)
                {
                    _tokenInts -= t.Length;
                    _lines[i] = null;
                }
            }
            if (aggressive)
            {
                Array.Clear(_checkpoints);
                if (_checkpoints.Length > 16) _checkpoints = new IStateStack?[16];
                _validLines = 0;
            }
            return Math.Max(0, before - (_tokenInts * 4 + _checkpoints.Length * 120L));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            Array.Clear(_checkpoints);
            _validLines = 0;
            _tokenInts = 0;
        }
    }
}
