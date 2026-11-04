using System.Buffers;
using System.Runtime.CompilerServices;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;

namespace CsCode.Editor.Features;

/// <summary>The conflict blocks of one version of a document, and that version's text (kept only when there are blocks: resolving reads it).</summary>
internal sealed record MergeConflictSet(int Version, IReadOnlyList<MergeConflict> Conflicts, string Text)
{
    public static MergeConflictSet Empty(int version) => new(version, [], "");

    /// <summary>The block whose lines include <paramref name="line"/> (0-based), or null. Binary search: blocks are in document order.</summary>
    public MergeConflict? At(int line)
    {
        int lo = 0, hi = Conflicts.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var c = Conflicts[mid];
            if (line < c.StartLine) hi = mid - 1;
            else if (line > c.EndLine) lo = mid + 1;
            else return c;
        }
        return null;
    }
}

/// <summary>
/// Finds the conflict blocks of open documents (VS Code's merge-conflict tracker): once per document version, on the editor worker from the
/// version's immutable snapshot, shared by every view's decorations, the code lens provider and the accept commands. A document without
/// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> anywhere (the usual case) costs one pass over its rope in 64 K chunks through a pooled buffer: no text is
/// materialised. Only the latest version of each document is kept, and it goes with the document (a weak table).
/// </summary>
internal sealed class MergeConflictTracker(EditorWorker worker)
{
    private const int ChunkSize = 64 * 1024;

    private sealed class Entry
    {
        public MergeConflictSet? Done;
        public (int Version, Task<MergeConflictSet> Task)? Running;
    }

    private readonly ConditionalWeakTable<EditorDocument, Entry> _entries = new();
    private readonly object _gate = new();

    /// <summary>The set of <paramref name="doc"/>'s current version when it was already computed, else null. Any thread; never computes.</summary>
    public MergeConflictSet? TryGetCurrent(EditorDocument doc)
    {
        lock (_gate)
            return _entries.TryGetValue(doc, out var e) && e.Done is { } d && d.Version == doc.Version ? d : null;
    }

    /// <summary>
    /// The set of <paramref name="doc"/>'s current version: the cached one, the computation already running for it, or a new computation on the
    /// editor worker. Any thread; the caller never does the scan.
    /// </summary>
    public Task<MergeConflictSet> GetAsync(EditorDocument doc, CancellationToken ct = default)
    {
        var snapshot = doc.Snapshot();
        Task<MergeConflictSet> task;
        lock (_gate)
        {
            var e = _entries.GetValue(doc, _ => new Entry());
            if (e.Done is { } d && d.Version == snapshot.Version) return Task.FromResult(d);
            if (e.Running is { } r && r.Version == snapshot.Version) task = r.Task;
            else
            {
                task = ScanAsync(e, snapshot);
                e.Running = (snapshot.Version, task);
            }
        }
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task<MergeConflictSet> ScanAsync(Entry e, DocumentSnapshot snapshot)
    {
        try
        {
            var set = await worker.RunAsync(WorkPriority.Visible, c => Compute(snapshot, c)).ConfigureAwait(false);
            lock (_gate)
                if (e.Done is null || e.Done.Version < set.Version) e.Done = set;
            return set;
        }
        finally
        {
            lock (_gate)
                if (e.Running is { } r && r.Version == snapshot.Version) e.Running = null;
        }
    }

    /// <summary>Scans one snapshot (editor worker). Public for tests.</summary>
    internal static MergeConflictSet Compute(DocumentSnapshot snapshot, CancellationToken ct)
    {
        if (!MayContainConflicts(snapshot)) return MergeConflictSet.Empty(snapshot.Version);
        var text = snapshot.GetText();
        var conflicts = MergeConflictParser.Parse(text, ct);
        return conflicts.Count == 0 ? MergeConflictSet.Empty(snapshot.Version) : new MergeConflictSet(snapshot.Version, conflicts, text);
    }

    /// <summary>Whether the start marker occurs anywhere, read chunk by chunk from the rope (chunks overlap by the marker's length minus one).</summary>
    internal static bool MayContainConflicts(DocumentSnapshot snapshot)
    {
        var length = snapshot.Length;
        if (length < MergeConflictParser.StartMarker.Length) return false;
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(ChunkSize, length));
        try
        {
            var overlap = MergeConflictParser.StartMarker.Length - 1;
            for (var offset = 0; ;)
            {
                var count = Math.Min(buffer.Length, length - offset);
                snapshot.CopyTo(offset, buffer, 0, count);
                if (MergeConflictParser.MayContainConflicts(buffer.AsSpan(0, count))) return true;
                if (offset + count >= length) return false;
                offset += count - overlap;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
