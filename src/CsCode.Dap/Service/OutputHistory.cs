using CsCode.Core.Debugging;

namespace CsCode.Dap.Service;

/// <summary>
/// The Debug Console history (what a console opened later replays): the NEWEST output up to <see cref="MaxLines"/> lines and
/// <see cref="MaxChars"/> characters. Not thread-safe: <see cref="DebugService"/> guards it with its lock.
/// <para>When the budget is exceeded, whole old events go while they fit in the excess; otherwise only the HEAD of the oldest event is cut, so a
/// flood batch followed by one more line still leaves the newest 5,000 lines (dropping whole batches used to leave 1).</para>
/// <para>The cut is an offset into the oldest event's text, not a copy: one big batch followed by thousands of one-line events (js-debug's
/// default output capture: a <c>console.log</c> of a large JSON, then a <c>console.log</c> loop) moves the offset by one line per event instead
/// of copying the whole batch every time, so adding stays O(line) amortised under the lock and allocates nothing for the cut. The remaining
/// text is materialised only when the history is read.</para>
/// </summary>
internal sealed class OutputHistory(int maxLines, int maxChars)
{
    private sealed class Entry(DebugOutputEventArgs e)
    {
        public DebugOutputEventArgs Event = e;
        /// <summary>Characters cut from the head of <see cref="DebugOutputEventArgs.Output"/>.</summary>
        public int Skip;
        /// <summary>'\n' in the text after <see cref="Skip"/>.</summary>
        public int Newlines = CountNewlines(e.Output, 0, e.Output.Length);
        public int Lines => Math.Max(1, Newlines);
        public int Chars => Event.Output.Length - Skip;

        public DebugOutputEventArgs Materialize()
        {
            if (Skip == 0) return Event;
            Event = Event with { Output = Event.Output[Skip..] }; // once: later reads (and cuts) work on the shorter text
            Skip = 0;
            return Event;
        }
    }

    private readonly LinkedList<Entry> _entries = new();
    private int _lines, _chars;

    public int MaxLines { get; } = maxLines;
    public int MaxChars { get; } = maxChars;
    public int Lines => _lines;
    public int Chars => _chars;
    public int Count => _entries.Count;

    public void Add(DebugOutputEventArgs e)
    {
        var entry = new Entry(e);
        _entries.AddLast(entry);
        _lines += entry.Lines;
        _chars += entry.Chars;
        Trim();
    }

    private void Trim()
    {
        while (_lines > MaxLines || _chars > MaxChars)
        {
            var first = _entries.First!.Value;
            var excessLines = _lines - MaxLines;
            var excessChars = _chars - MaxChars;
            if (_entries.Count > 1 && (first.Lines <= excessLines || first.Chars <= excessChars))
            {
                _entries.RemoveFirst();
                _lines -= first.Lines;
                _chars -= first.Chars;
                continue;
            }
            // Cut the head of the oldest event: past `excessLines` line ends, and at least `excessChars` characters. One line is always kept.
            var text = first.Event.Output;
            var start = first.Skip;
            var pos = start;
            var cutNewlines = 0;
            while (cutNewlines < excessLines && cutNewlines < first.Newlines - 1)
            {
                var nl = text.IndexOf('\n', pos);
                if (nl < 0) break;
                pos = nl + 1;
                cutNewlines++;
            }
            if (excessChars > 0 && pos - start < excessChars)
            {
                var target = Math.Min(text.Length - 1, start + excessChars);
                if (target > pos)
                {
                    cutNewlines += CountNewlines(text, pos, target - pos);
                    pos = target;
                }
            }
            if (pos <= start) break; // nothing left to cut (a single line within the budget): never loop
            var oldLines = first.Lines;
            var oldChars = first.Chars;
            first.Skip = pos;
            first.Newlines -= cutNewlines;
            _lines += first.Lines - oldLines;
            _chars += first.Chars - oldChars;
        }
    }

    public IReadOnlyList<DebugOutputEventArgs> Snapshot()
    {
        var result = new DebugOutputEventArgs[_entries.Count];
        var i = 0;
        foreach (var entry in _entries) result[i++] = entry.Materialize();
        return result;
    }

    /// <summary>Rewrites the events of one session (an ended session keeps its label without keeping the session object alive).</summary>
    public void Relabel(IDebugSession session, IDebugSession replacement)
    {
        foreach (var entry in _entries)
            if (ReferenceEquals(entry.Event.Session, session))
                entry.Event = entry.Event with { Session = replacement };
    }

    public void Clear()
    {
        _entries.Clear();
        _lines = _chars = 0;
    }

    private static int CountNewlines(string text, int start, int length) => text.AsSpan(start, length).Count('\n');
}
