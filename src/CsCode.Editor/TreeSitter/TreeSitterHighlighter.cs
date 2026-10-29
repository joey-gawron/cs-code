using System.Diagnostics;
using CsCode.Core.Text;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;
using CsCode.Editor.TreeSitter.Highlighting;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// Edits recorded on the UI thread for the tree-sitter engine of one document, drained in order on the worker. Bounded: when edits pile up
/// unconsumed (no slice ran, e.g. no view), older ones are dropped and the engine falls back to a full parse (it detects the gap by version).
/// </summary>
internal sealed class TreeSitterEditQueue
{
    private const int MaxPending = 4096;
    private readonly object _gate = new();
    private readonly List<(int Version, IReadOnlyList<TextContentChange> Changes)> _pending = new();

    public void Add(int version, IReadOnlyList<TextContentChange> changes)
    {
        lock (_gate)
        {
            if (_pending.Count >= MaxPending) _pending.RemoveRange(0, _pending.Count / 2);
            _pending.Add((version, changes));
        }
    }

    public List<(int Version, IReadOnlyList<TextContentChange> Changes)> TakeUpTo(int version)
    {
        lock (_gate)
        {
            var n = 0;
            while (n < _pending.Count && _pending[n].Version <= version) n++;
            var taken = _pending.GetRange(0, n);
            _pending.RemoveRange(0, n);
            return taken;
        }
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
    }
}

/// <summary>
/// The EXPERIMENTAL tree-sitter engine of ONE document (owned by its <see cref="TokenizationSession"/>). It produces exactly what the
/// TextMate path produces — <c>(startColumn, scopeStackId)</c> rows in the session's <see cref="LineTokenStore"/> — so the colorizer, themes,
/// auto-closing (<c>KindAt</c>) and bracket matching work unchanged; only how the rows are computed differs.
///
/// Per worker slice (≤ <see cref="TokenizationSession.SliceBudget"/>): drain the recorded edits into the tree (<c>ts_tree_edit</c>), continue
/// the incremental parse until the slice budget (a halted parse resumes next slice; an edit cancels it), then highlight the viewport
/// (always after a new tree or a scroll — cheap), then the lines the edit or <c>ts_tree_get_changed_ranges</c> dirtied near the viewport, then
/// read-ahead once typing pauses. Dirty lines far from the viewport just lose their tokens and are highlighted when they come into view.
/// Rows identical to the stored ones are not republished, so a re-highlight that changes nothing costs the UI nothing.
///
/// Confined to the editor worker except <see cref="RequestCancel"/> (UI thread, on every edit).
/// </summary>
internal sealed class TreeSitterHighlighter : IDisposable
{
    public const int ReadBehindLines = 500;
    public const int ChunkLines = 200;

    private readonly TreeSitterService _service;
    private readonly SyntaxDocument _syntax;
    private readonly PaintBuffer _paint = new();
    private readonly DirtyLines _dirty = new();
    private readonly List<int> _pairs = new();
    private readonly List<int[]> _batch = new();
    private SnapshotTextInput? _text;
    private int _editedVersion = int.MinValue;
    private int _treeVersion = int.MinValue;
    private (int First, int Last, int Generation) _viewportDone = (-1, -1, -1);
    private bool _fullRefresh = true;
    private bool _disposed;

    private TreeSitterHighlighter(TreeSitterService service, SyntaxDocument syntax, string languageId, int generation)
    {
        _service = service;
        _syntax = syntax;
        LanguageId = languageId;
        Generation = generation;
    }

    public string LanguageId { get; }

    /// <summary>The session restart generation this engine was created for (a restart replaces it).</summary>
    public int Generation { get; }

    public SyntaxDocument Syntax => _syntax;

    /// <summary>Approximate native bytes of the syntax tree (updated after each parse; read from any thread).</summary>
    public long EstimatedBytes => Volatile.Read(ref _estimatedBytes);
    private long _estimatedBytes;

    /// <summary>Worker-side timings of the last completed parse and the last highlight pass (benchmarks, tests).</summary>
    public TimeSpan LastParseTime => _syntax.LastParseTime;
    public TimeSpan LastHighlightTime { get; private set; }
    public int CompletedParses { get; private set; }

    /// <summary>The managed thread that ran the last slice (tests prove it is the editor worker, never the UI thread).</summary>
    public int LastSliceThreadId { get; private set; }

    /// <summary>Worker only. Null (and the reason logged once by the service) when tree-sitter cannot handle the language here.</summary>
    public static TreeSitterHighlighter? TryCreate(TreeSitterService service, string languageId, int generation)
    {
        if (service.BundleFor(languageId) is not { } bundle) return null;
        try
        {
            return new TreeSitterHighlighter(service, new SyntaxDocument(service.Registry, bundle), languageId, generation);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Any thread: an edit made the running parse stale.</summary>
    public void RequestCancel() => _syntax.RequestCancel();

    /// <summary>One worker slice for <paramref name="session"/>.</summary>
    public void RunSlice(TokenizationSession session)
    {
        if (_disposed) return;
        LastSliceThreadId = Environment.CurrentManagedThreadId;
        var store = session.Store;
        var snap = session.Document.Snapshot();
        if (store.Version != snap.Version || store.LineCount != snap.LineCount) return; // the UI's Changed handler schedules the next slice
        var deadline = Stopwatch.GetTimestamp() + (long)(TokenizationSession.SliceBudget.TotalSeconds * Stopwatch.Frequency);
        if (_text is null || !ReferenceEquals(_text.Snapshot, snap)) _text = new SnapshotTextInput(snap);
        var text = _text;

        // 1. bring the tree in line with the text: apply the recorded edits in order (a gap in versions means edits were lost: full parse)
        if (_editedVersion != snap.Version) ApplyEdits(session, snap.Version);

        // 2. parse (incremental, sliced, cancellable)
        if (_treeVersion != snap.Version)
        {
            if (_syntax.Parse(text, deadline) == ParseOutcome.Halted)
            {
                if (session.Document.Version == snap.Version) session.ScheduleSlice(WorkPriority.Visible); // out of budget: resume next slice
                return; // otherwise a newer edit cancelled it and its Changed handler scheduled the next slice
            }
            _treeVersion = snap.Version;
            CompletedParses++;
            Volatile.Write(ref _estimatedBytes, _syntax.EstimatedTreeBytes);
            if (_fullRefresh)
            {
                _fullRefresh = false;
                _dirty.Clear();
                _dirty.Add(0, snap.LineCount - 1);
            }
            else
            {
                foreach (var (start, end) in _syntax.ChangedRanges)
                    _dirty.Add(text.LineOfOffset(start), text.LineOfOffset(Math.Max(start, end - 1)));
            }
            _viewportDone = (-1, -1, -1);
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                session.ScheduleSlice(WorkPriority.Interactive); // the parse used the slice: highlight with a fresh one
                return;
            }
        }

        // 3. highlight: viewport, dirty lines near it, then read-ahead
        var lineCount = snap.LineCount;
        _dirty.Clamp(lineCount);
        var (vFirst, vLast) = session.Viewport;
        vFirst = Math.Clamp(vFirst, 0, lineCount - 1);
        vLast = Math.Clamp(vLast, vFirst, lineCount - 1);
        var windowFirst = Math.Max(0, vFirst - ReadBehindLines);
        var windowLast = Math.Min(lineCount - 1, vLast + TokenizationSession.ReadAheadLines);
        foreach (var (f, l) in _dirty.TakeOutside(windowFirst, windowLast)) store.ClearLines(snap.Version, f, l);

        var started = Stopwatch.GetTimestamp();
        try
        {
            if (_viewportDone != (vFirst, vLast, _syntax.Generation))
            {
                if (!Highlight(session, text, vFirst, vLast)) return;
                _dirty.Take(vFirst, vLast);
                _viewportDone = (vFirst, vLast, _syntax.Generation);
                session.MarkViewportReady(snap.Version);
            }
            // dirty lines nearest the viewport first (below, then above)
            while (!_dirty.IsEmpty && Stopwatch.GetTimestamp() < deadline)
            {
                var next = NearestDirty(vLast);
                var taken = _dirty.Take(next.First, Math.Min(next.Last, next.First + ChunkLines - 1));
                foreach (var (f, l) in taken)
                    if (!Highlight(session, text, f, l)) return;
            }
            if (!_dirty.IsEmpty)
            {
                session.ScheduleSlice(WorkPriority.Background);
                return;
            }
            // read-ahead: lines without tokens around the viewport, only once the typing pauses (as on the TextMate path)
            var missing = FirstMissing(store, vLast + 1, windowLast) ?? FirstMissing(store, windowFirst, vFirst - 1);
            if (missing is not { } m) return;
            if (!session.IsQuiet)
            {
                session.ScheduleReadAheadLater();
                return;
            }
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var end = m <= vFirst - 1 ? Math.Min(vFirst - 1, m + ChunkLines - 1) : Math.Min(windowLast, m + ChunkLines - 1);
                if (!Highlight(session, text, m, end)) return;
                if ((FirstMissing(store, vLast + 1, windowLast) ?? FirstMissing(store, windowFirst, vFirst - 1)) is not { } again) return;
                m = again;
            }
            session.ScheduleSlice(WorkPriority.Background);
        }
        finally
        {
            LastHighlightTime = Stopwatch.GetElapsedTime(started);
        }
    }

    private void ApplyEdits(TokenizationSession session, int version)
    {
        var pending = session.TreeSitterEdits.TakeUpTo(version);
        var continuous = _syntax.HasTree && _editedVersion != int.MinValue;
        var expected = _editedVersion + 1;
        foreach (var (v, _) in pending)
        {
            if (v != expected) continuous = false;
            expected = v + 1;
        }
        if (!continuous || expected - 1 != version)
        {
            _syntax.DropTree();
            _fullRefresh = true;
            _treeVersion = int.MinValue;
        }
        else
        {
            foreach (var (_, changes) in pending)
                foreach (var c in changes)
                {
                    var breaks = TokenizationSession.CountBreaks(c.Text);
                    var (endLine, endChar) = EndOf(c.Range.Start, c.Text);
                    _syntax.ApplyEdit(new TextEditInfo(c.RangeOffset, c.RangeOffset + c.RangeLength, c.RangeOffset + c.Text.Length,
                        c.Range.Start.Line, c.Range.Start.Character, c.Range.End.Line, c.Range.End.Character, endLine, endChar));
                    _dirty.Shift(c.Range.Start.Line, c.Range.End.Line - c.Range.Start.Line, breaks);
                    _dirty.Add(c.Range.Start.Line, c.Range.Start.Line + breaks);
                }
        }
        _editedVersion = version;
    }

    private static (int Line, int Character) EndOf(TextPosition start, string inserted)
    {
        var lastBreak = inserted.LastIndexOfAny(['\n', '\r']);
        if (lastBreak < 0) return (start.Line, start.Character + inserted.Length);
        return (start.Line + TokenizationSession.CountBreaks(inserted), inserted.Length - lastBreak - 1);
    }

    private (int First, int Last) NearestDirty(int below)
    {
        var ranges = _dirty.Ranges;
        foreach (var r in ranges)
            if (r.Last > below) return r.First > below ? r : (below + 1, r.Last);
        return ranges[^1];
    }

    private static int? FirstMissing(LineTokenStore store, int first, int last)
    {
        for (var i = Math.Max(0, first); i <= last; i++)
            if (store.GetTokens(i) is null) return i;
        return null;
    }

    /// <summary>Highlights lines [first, last] and publishes the rows that changed. False when the document moved on (stop the slice).</summary>
    private bool Highlight(TokenizationSession session, SnapshotTextInput text, int first, int last)
    {
        if (last < first) return true;
        var store = session.Store;
        var start = text.LineStart(first);
        var end = text.LineEnd(last);
        _syntax.Highlight(text, start, end, _service.Stacks, _paint);
        var root = _paint.Length > 0 ? -1 : _service.Stacks.Root(_syntax.Language.RootScopeKey);
        var runStart = -1;
        for (var line = first; line <= last; line++)
        {
            var row = Row(text, line, root);
            var existing = store.GetTokens(line);
            var same = existing is not null && existing.AsSpan().SequenceEqual(row);
            if (same)
            {
                if (runStart >= 0 && !Flush(session, text.Snapshot.Version, runStart, line - 1)) return false;
                runStart = -1;
                continue;
            }
            if (runStart < 0) runStart = line;
            _batch.Add(row);
        }
        if (runStart >= 0 && !Flush(session, text.Snapshot.Version, runStart, last)) return false;
        _paint.Shrink(1 << 20);
        return true;
    }

    private bool Flush(TokenizationSession session, int version, int first, int last)
    {
        var ok = session.Store.Publish(version, first, _batch, [], chain: false);
        _batch.Clear();
        if (ok) session.NotifyTokensChanged(first, last);
        return ok;
    }

    private int[] Row(SnapshotTextInput text, int line, int root)
    {
        _pairs.Clear();
        var start = text.LineStart(line);
        var end = text.LineEnd(line);
        if (end <= start || _paint.Length == 0)
        {
            _pairs.Add(0);
            _pairs.Add(_paint.Length == 0 ? root : _paint.StackAt(Math.Min(start, _paint.Start + _paint.Length - 1)));
            return _pairs.ToArray();
        }
        var previous = int.MinValue;
        for (var i = start; i < end; i++)
        {
            var id = _paint.StackAt(i);
            if (id == previous) continue;
            _pairs.Add(i - start);
            _pairs.Add(id);
            previous = id;
        }
        return _pairs.ToArray();
    }

    /// <summary>Worker only: frees the parser, tree and query cursor.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _syntax.Dispose();
        _paint.Shrink(0);
        Volatile.Write(ref _estimatedBytes, 0);
    }
}
