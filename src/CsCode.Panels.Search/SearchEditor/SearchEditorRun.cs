using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.SearchEditor;

/// <summary>What a run needs (the shared engine and services); built per run from the search services.</summary>
internal sealed record SearchEditorRunContext(
    SearchEngine Engine,
    IUiDispatcher Ui,
    IReadOnlyList<Uri> Folders,
    IDocumentManager? Documents,
    IFileSystem? FileSystem,
    TaskScheduler Lane);

/// <summary>
/// One search of a search editor, from the header to the results in the text. The engine searches (the Find in Files window's engine: its own
/// threads, cancellable); this run's worker, on the search editor lane (one dedicated thread, never the UI thread), reads the full matching lines
/// and their context, formats each file's block and works out where it goes in path order; the UI thread only inserts small chunks of finished
/// text (at most about 4 ms of them per dispatcher callback, at background priority) and rewrites the summary line. Everything a run writes is
/// one undo step. A superseded run is <see cref="Dispose">disposed</see>: the engine stops, the worker stops, queued chunks are dropped.
/// </summary>
internal sealed class SearchEditorRun : IDisposable
{
    /// <summary>A chunk is cut at about this many characters of text (the UI inserts it in one edit).</summary>
    internal const int ChunkChars = 32 * 1024;
    private const double SliceMs = 4;
    public const string Searching = "Searching…";

    private readonly SearchEditorRunContext _ctx;
    private readonly ITextDocument _doc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<object> _input = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<Chunk> _chunks = new();
    private readonly TaskCompletionSource<SearchCompletion?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DisposableStore _subs = new();
    private readonly IReadOnlyList<FileMatches>? _preset;
    private SearchSession? _session;
    private IDisposable? _undoGroup;
    private int _separatorLine; // the empty line between the header and the summary (moves when lines are added to the header meanwhile)
    private int _drainScheduled;
    private bool _applying, _stopped;

    private sealed record Chunk(IReadOnlyList<(int Line, string Text)> Inserts, string Summary, bool Final, SearchCompletion? Completion);

    /// <param name="preset">Results to show instead of searching (Open Results in Editor: what the Find in Files window found).</param>
    public SearchEditorRun(SearchEditorRunContext ctx, ITextDocument document, SearchEditorConfig config, IReadOnlyList<FileMatches>? preset = null)
    {
        _ctx = ctx;
        _doc = document;
        Config = config;
        _preset = preset;
    }

    public SearchEditorConfig Config { get; }

    /// <summary>Completes (UI thread) when the run's last chunk is in the text: with the engine's outcome, or null when it was stopped or could not start.</summary>
    public Task<SearchCompletion?> Completion => _done.Task;

    public bool IsFinished => _done.Task.IsCompleted;

    /// <summary>True while the run itself is editing the document (UI thread): its own edits are not the user's.</summary>
    public bool IsApplying => _applying;

    /// <summary>Chunks applied so far (tests: results stream in several edits).</summary>
    public int ChunksApplied { get; private set; }

    /// <summary>The engine session (tests); null with preset results or after the run.</summary>
    internal SearchSession? Session => _session;

    /// <summary>
    /// Starts the run (UI thread; returns at once): writes the empty results with "Searching…" under the header, then searches. False when the
    /// document has no header to search with.
    /// </summary>
    public bool Start()
    {
        var header = SearchEditorFormat.CountHeaderLines(_doc.GetLineText, _doc.LineCount);
        if (header == 0)
        {
            _done.TrySetResult(null);
            return false;
        }
        _undoGroup = (_doc as IEditorDocument)?.OpenUndoGroup();
        _separatorLine = header;
        var lastHeader = header - 1;
        var end = _doc.PositionAt(_doc.Length);
        var start = new TextPosition(lastHeader, _doc.GetLineText(lastHeader).Length);
        var noFolder = _ctx.Folders.Count == 0 && !Config.OnlyOpenEditors && _preset is null;
        var summary = noFolder ? SearchEditorFormat.NoResults + " - No folder is open. Open a folder to search it." : Searching;
        if (!Write([new TextEdit(new TextRange(start, end), "\n\n" + summary + "\n\n")]))
        {
            Stop();
            return false;
        }
        _subs.Add(_doc.Changed.Subscribe(OnDocumentChanged));
        if (noFolder)
        {
            Finish(null);
            return true;
        }

        _ = Task.Factory.StartNew(WorkAsync, _cts.Token, TaskCreationOptions.DenyChildAttach, _ctx.Lane).Unwrap();
        if (_preset is not null)
        {
            foreach (var f in _preset) _input.Writer.TryWrite(f);
            _input.Writer.TryWrite(new SearchCompletion(SearchOutcome.Completed, SearchStats.Empty));
            _input.Writer.TryComplete();
            return true;
        }
        var writer = _input.Writer;
        // Engine threads: only enqueue here. Subscribed before the session runs (a bad pattern completes at once).
        _session = _ctx.Engine.Start(Config.ToQuery(), session =>
        {
            _subs.Add(session.Batches.Subscribe(b => { foreach (var f in b.Files) writer.TryWrite(f); }));
            _subs.Add(session.Completed.Subscribe(c =>
            {
                writer.TryWrite(c);
                writer.TryComplete();
            }));
        });
        return true;
    }

    // ------------------------------------------------------------------------------------- worker lane

    private async Task WorkAsync()
    {
        var ct = _cts.Token;
        var layout = new ResultLayout(_ctx.Folders);
        SearchCompletion? completion = null;
        try
        {
            var reader = _input.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(true))
            {
                while (reader.TryRead(out var item))
                {
                    ct.ThrowIfCancellationRequested();
                    if (item is SearchCompletion c)
                    {
                        completion = c;
                        continue;
                    }
                    var file = (FileMatches)item;
                    if (file.Matches.Count == 0) continue;
                    var wanted = ResultBlockBuilder.LinesOf(file, Config.ContextLines);
                    var lines = await FileLines.ReadAsync(file, wanted, _ctx.Documents, _ctx.FileSystem, ct).ConfigureAwait(true);
                    layout.Add(file.Uri, ResultBlockBuilder.Build(file, Config.ContextLines, lines));
                    if (layout.PendingChars >= ChunkChars) Flush(layout, final: false, null);
                }
                if (layout.HasPending) Flush(layout, final: false, null); // nothing more queued right now: show what there is
            }
            Flush(layout, final: true, completion ?? new SearchCompletion(SearchOutcome.Cancelled, SearchStats.Empty));
        }
        catch (OperationCanceledException)
        {
            // superseded or closed: nothing more is written
        }
    }

    private void Flush(ResultLayout layout, bool final, SearchCompletion? completion)
    {
        var inserts = layout.TakeInserts();
        var summary = final ? FinalSummary(layout, completion!) : Searching + (layout.Matches > 0 ? " " + SearchEditorFormat.Summary(layout.Matches, layout.Files) : "");
        _chunks.Enqueue(new Chunk(inserts, summary, final, completion));
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0) _ctx.Ui.Post(Drain, UiPriority.Background);
    }

    private static string FinalSummary(ResultLayout layout, SearchCompletion c)
    {
        var counts = SearchEditorFormat.Summary(layout.Matches, layout.Files, limitHit: c.Outcome == SearchOutcome.Truncated || c.Stats.Truncated);
        return c.Outcome switch
        {
            SearchOutcome.Failed => SearchEditorFormat.SafeSummary(counts + " - " + (c.Error ?? "The search failed.")),
            SearchOutcome.Cancelled => counts + " - Search cancelled",
            _ => counts,
        };
    }

    // ------------------------------------------------------------------------------------------- UI thread

    /// <summary>Applies queued chunks for at most about 4 ms, then yields to the dispatcher (input and rendering get their turn).</summary>
    private void Drain()
    {
        Volatile.Write(ref _drainScheduled, 0);
        var start = Stopwatch.GetTimestamp();
        var budget = (long)(SliceMs * Stopwatch.Frequency / 1000.0);
        while (_chunks.TryDequeue(out var chunk))
        {
            if (_stopped) continue; // drop what a stopped run still had queued
            Apply(chunk);
            if (chunk.Final)
            {
                Finish(chunk.Completion);
                return;
            }
            if (Stopwatch.GetTimestamp() - start >= budget) break;
        }
        if (!_chunks.IsEmpty && Interlocked.Exchange(ref _drainScheduled, 1) == 0) _ctx.Ui.Post(Drain, UiPriority.Background);
    }

    private void Apply(Chunk chunk)
    {
        var summaryLine = _separatorLine + 1;
        var firstBlock = _separatorLine + 3;
        if (_doc.LineCount <= firstBlock)
        {
            Stop(); // the results' frame is gone (should not happen: edits below the header stop the run)
            return;
        }
        var edits = new List<TextEdit>(chunk.Inserts.Count + 1)
        {
            new(new TextRange(new TextPosition(summaryLine, 0), new TextPosition(summaryLine, _doc.GetLineText(summaryLine).Length)), chunk.Summary),
        };
        foreach (var (line, text) in chunk.Inserts) edits.Add(TextEdit.Insert(new TextPosition(firstBlock + line, 0), text));
        if (Write(edits)) ChunksApplied++;
        else Stop();
    }

    private bool Write(IReadOnlyList<TextEdit> edits)
    {
        _applying = true;
        try { return _doc.ApplyEdits(edits); }
        finally { _applying = false; }
    }

    /// <summary>
    /// The user edited the document while the run streams: lines added or removed in the header move the results' frame; an edit of the results
    /// themselves stops the run there (the user's text wins; nothing more is inserted).
    /// </summary>
    private void OnDocumentChanged(TextDocumentChangedEventArgs e)
    {
        if (_applying || _stopped) return;
        foreach (var c in e.Changes)
        {
            if (c.Range.End.Line < _separatorLine)
            {
                _separatorLine += LineBreaks(c.Text) - (c.Range.End.Line - c.Range.Start.Line);
                continue;
            }
            Stop();
            return;
        }
    }

    private static int LineBreaks(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') n++;
            else if (text[i] == '\r')
            {
                n++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
        }
        return n;
    }

    private void Finish(SearchCompletion? completion)
    {
        _stopped = true;
        _subs.Dispose();
        _session = null;
        _undoGroup?.Dispose(); // the whole run is one undo step
        _undoGroup = null;
        _done.TrySetResult(completion);
    }

    /// <summary>Stops the run: the engine, the worker and every queued chunk. Idempotent; call on the UI thread.</summary>
    public void Stop()
    {
        if (_stopped && _done.Task.IsCompleted) return;
        _stopped = true;
        _session?.Cancel();
        _session = null;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
        _input.Writer.TryComplete();
        while (_chunks.TryDequeue(out _)) { }
        _subs.Dispose();
        _undoGroup?.Dispose();
        _undoGroup = null;
        _done.TrySetResult(null);
    }

    public void Dispose() => Stop();
}
