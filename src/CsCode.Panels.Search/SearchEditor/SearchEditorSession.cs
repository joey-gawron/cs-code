using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.SearchEditor;

/// <summary>
/// The state of one open search editor (a <c>search-result</c> document): its current run, the query its results came from (what the matches
/// are highlighted with) and search-as-you-type. Created when the document opens (or turns into a search editor), disposed when it closes:
/// the run stops and every subscription goes.
/// </summary>
internal sealed class SearchEditorSession : IDisposable
{
    private readonly SearchEditorService _owner;
    private readonly DisposableStore _subs = new();
    private SearchEditorRun? _run;
    private CancellationTokenSource? _typing;
    private bool _disposed;

    public SearchEditorSession(SearchEditorService owner, ITextDocument document)
    {
        _owner = owner;
        Document = document;
        _subs.Add(document.Changed.Subscribe(OnChanged));
    }

    public ITextDocument Document { get; }

    /// <summary>The header of the last run (its matches are highlighted with it); null before the first run here (a saved file just opened).</summary>
    public SearchEditorConfig? LastConfig { get; private set; }

    /// <summary>The current (or last) run.</summary>
    public SearchEditorRun? Run => _run;

    public bool IsRunning => _run is { IsFinished: false };

    /// <summary>
    /// Searches again with the header as it is now (UI thread): the previous run stops, the results under the header are replaced as the new ones
    /// stream in. An empty query just clears the results. Null when there is nothing to run (no header, disposed).
    /// </summary>
    public SearchEditorRun? Rerun(IReadOnlyList<FileMatches>? preset = null)
    {
        if (_disposed) return null;
        CancelTyping();
        _run?.Dispose();
        _run = null;
        var header = SearchEditorFormat.CountHeaderLines(Document.GetLineText, Document.LineCount);
        if (header == 0) return null;
        var config = SearchEditorFormat.ParseHeader(Enumerable.Range(0, header).Select(Document.GetLineText).ToArray());
        LastConfig = config;
        _owner.OnRunStarting(this, config);
        if (config.Query.Length == 0)
        {
            ClearResults(header);
            _owner.HighlightsChanged(Document.Uri);
            return null;
        }
        var run = new SearchEditorRun(_owner.RunContext(), Document, config, preset);
        _run = run;
        run.Start();
        _ = NotifyWhenDoneAsync(run);
        _owner.HighlightsChanged(Document.Uri);
        return run;
    }

    private async Task NotifyWhenDoneAsync(SearchEditorRun run)
    {
        await run.Completion.ConfigureAwait(true);
        if (!_disposed && ReferenceEquals(run, _run)) _owner.HighlightsChanged(Document.Uri);
    }

    private void ClearResults(int header)
    {
        var start = new TextPosition(header - 1, Document.GetLineText(header - 1).Length);
        var end = Document.PositionAt(Document.Length);
        if (Document.OffsetAt(end) - Document.OffsetAt(start) <= 2 && Document.GetText(new TextRange(start, end)) is "\n" or "\r\n") return;
        _clearing = true;
        try { Document.ApplyEdits([new TextEdit(new TextRange(start, end), "\n")]); }
        finally { _clearing = false; }
    }

    private bool _clearing;

    /// <summary>Search as you type (<c>search.searchOnType</c>): an edit in the header searches again once typing pauses.</summary>
    private void OnChanged(TextDocumentChangedEventArgs e)
    {
        if (_disposed || _clearing || _run is { IsApplying: true } || !_owner.SearchOnType) return;
        var header = SearchEditorFormat.CountHeaderLines(Document.GetLineText, Document.LineCount);
        if (header == 0) return;
        var inHeader = false;
        foreach (var c in e.Changes)
            if (c.Range.Start.Line < header) { inHeader = true; break; }
        if (!inHeader) return;
        CancelTyping();
        var cts = _typing = new CancellationTokenSource();
        var ui = _owner.Ui;
        _ = Task.Delay(_owner.SearchOnTypeDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            ui.Post(() =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(_typing, cts)) Rerun();
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelTyping()
    {
        if (_typing is not { } t) return;
        _typing = null;
        t.Cancel();
        t.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTyping();
        _run?.Dispose();
        _run = null;
        _subs.Dispose();
    }
}
