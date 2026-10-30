using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// The in-editor Find (AvaloniaEdit's stock <see cref="SearchPanel"/>) made safe for big documents and hostile patterns, the way VS Code's
/// find widget behaves (editor-4). The stock panel re-searched the WHOLE document on the UI thread on every edit while it was open (a 4 MB
/// file with Find open on "e": ~490k matches rebuilt per keystroke, 425 ms each), had no result limit, and compiled the user's regex with no
/// match timeout (a backtracking pattern hung the UI thread for good). This component changes three things:
/// <list type="bullet">
/// <item>a result limit of <see cref="MaxResults"/> (VS Code's <c>MATCHES_LIMIT</c>, 19,999): only the first ones are highlighted, and the
/// panel says so;</item>
/// <item>a regex match timeout (<see cref="MatchTimeout"/>): a pattern that backtracks without end is stopped, the matches found so far stay,
/// and the panel says why;</item>
/// <item>an edit no longer re-searches anything: the highlighted matches move with it (the panel's own segment collection, updated per change
/// like VS Code's tracked decorations), and once the typing has paused for <see cref="EditDelay"/> (VS Code: 100 ms) the document is
/// searched again on the editor worker, from an immutable snapshot; the results replace the old ones on the UI thread only if the text has
/// not moved on meanwhile.</item>
/// </list>
/// A new pattern or option (typing in the find box, the panel opening) still searches at once on the UI thread, as VS Code does, bounded by
/// the limit and the timeout: Enter right after typing a pattern (a recorded macro, a quick user) must find the match.
///
/// The panel offers no hooks for any of it, so this works against AvaloniaEdit 12's private layout by reflection: the panel's strategy field
/// (swapped right after the panel built its own: <see cref="SearchPanel.SearchOptionsChanged"/> fires between the two), its per-edit
/// handler (unsubscribed from the document), its result collection (moved with each change) and its <c>DoSearch</c> (re-run with the
/// worker's results). If that layout changes, nothing is installed and the stock behaviour stays (<see cref="Supported"/>; the test of this
/// component says so).
/// </summary>
internal sealed class FindSearch : IDisposable
{
    /// <summary>The most matches one search keeps (VS Code's find widget: 19,999).</summary>
    public const int MaxResults = 19_999;

    /// <summary>The longest one regex match operation may run before the search is stopped (settable for tests).</summary>
    internal static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Typing pause before an edit's re-search (VS Code: 100 ms).</summary>
    internal static TimeSpan EditDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? StrategyField = typeof(SearchPanel).GetField("_strategy", Private);
    private static readonly FieldInfo? RendererField = typeof(SearchPanel).GetField("_renderer", Private);
    private static readonly MethodInfo? DoSearchMethod = typeof(SearchPanel).GetMethod("DoSearch", Private, [typeof(bool)]);
    private static readonly MethodInfo? PanelTextChanged = typeof(SearchPanel).GetMethod("TextArea_Document_TextChanged", Private, [typeof(object), typeof(EventArgs)]);
    private static readonly ConstructorInfo? RegexStrategyCtor = typeof(SearchPanel).Assembly.GetType("AvaloniaEdit.Search.RegexSearchStrategy")
        ?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Regex), typeof(bool)]);

    /// <summary>True when AvaloniaEdit's private layout is the one this component was written against (tests).</summary>
    internal static bool Supported =>
        StrategyField is not null && RendererField is not null && DoSearchMethod is not null && PanelTextChanged is not null && RegexStrategyCtor is not null;

    private readonly CodeEditorControl _view;
    private SearchPanel? _panel;
    private EventHandler? _panelTextChanged; // the panel's own per-edit handler (a whole-document search), kept off the document
    private IEnumerable? _panelResults; // the panel's result collection
    private Action<DocumentChangeEventArgs>? _moveResults; // ...and its UpdateOffsets
    private TextDocument? _document;
    private Strategy? _strategy;
    private Ready? _ready; // a worker re-search waiting to be handed to the panel
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    private sealed record Ready(ITextSourceVersion Version, Strategy Strategy, List<ISearchResult> Results, bool TimedOut, bool Capped);

    public FindSearch(CodeEditorControl view)
    {
        _view = view;
        // the editor builds its search panel when its template is applied (after this event fires): bind to it then, and on demand (ShowFind)
        view.Editor.TemplateApplied += OnTemplateApplied;
        view.Editor.TextArea.DocumentChanged += OnTextAreaDocumentChanged;
        Attach();
    }

    // ---- what tests and the panel label read ----

    /// <summary>Whole-document searches run on the UI thread (a new pattern or option, the panel opening): never one per edit.</summary>
    internal int UiThreadSearches { get; private set; }

    /// <summary>Re-searches after edits, run on the editor worker.</summary>
    internal int WorkerSearches => Volatile.Read(ref _workerSearches);
    private int _workerSearches;

    /// <summary>Where the matches the panel highlights start, as they are now (moved with the edits since the last search).</summary>
    internal IEnumerable<int> ResultStarts => _panelResults?.OfType<TextSegment>().Select(s => s.StartOffset).ToArray() ?? [];

    /// <summary>Matches the panel highlights.</summary>
    internal int ResultCount => _panelResults?.OfType<TextSegment>().Count() ?? 0;

    internal bool LastTimedOut { get; private set; }
    internal bool LastCapped { get; private set; }
    private int _lastCount;

    /// <summary>The message shown in the panel for a stopped or capped search (null when there is none).</summary>
    internal string? Message =>
        LastTimedOut ? $"The regular expression took too long and was stopped ({_lastCount:N0} matches found before that)"
        : LastCapped ? $"More than {MaxResults:N0} matches: only the first {MaxResults:N0} are highlighted"
        : null;

    // ---- binding to the panel ----

    private void OnTemplateApplied(object? sender, Avalonia.Controls.Primitives.TemplateAppliedEventArgs e) => _view.Post(Attach);

    /// <summary>Binds to the editor's current search panel (a re-applied template builds a new one). UI thread.</summary>
    internal void Attach()
    {
        if (_disposed || !Supported) return;
        var panel = _view.Editor.SearchPanel;
        if (panel is null || ReferenceEquals(panel, _panel)) return;
        Detach();
        try
        {
            _panelTextChanged = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), panel, PanelTextChanged!);
            var renderer = RendererField!.GetValue(panel);
            _panelResults = renderer?.GetType().GetProperty("CurrentResults")?.GetValue(renderer) as IEnumerable;
            _moveResults = _panelResults is null ? null
                : (Action<DocumentChangeEventArgs>?)Delegate.CreateDelegate(typeof(Action<DocumentChangeEventArgs>), _panelResults, "UpdateOffsets", false, false);
        }
        catch (ArgumentException)
        {
            _moveResults = null;
        }
        if (_moveResults is null)
        {
            _panelTextChanged = null;
            _panelResults = null;
            return; // not the layout this was written for: the stock panel as it is
        }
        _panel = panel;
        panel.SearchOptionsChanged += OnSearchOptionsChanged;
        BindDocument(_view.Editor.TextArea.Document);
    }

    private void Detach()
    {
        // (the panel's own per-edit handler is never put back: the panel is going away, and a document shared with other views must not hold it)
        if (_panel is not null) _panel.SearchOptionsChanged -= OnSearchOptionsChanged;
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _panel = null;
        _document = null;
        _strategy = null;
        _ready = null;
        _debounce?.Cancel();
    }

    /// <summary>The text area got another document: the panel subscribes its handler to it after this runs, so the swap happens a turn later.</summary>
    private void OnTextAreaDocumentChanged(object? sender, EventArgs e) => _view.Post(() => BindDocument(_view.Editor.TextArea.Document));

    private void BindDocument(TextDocument? document)
    {
        if (_disposed || _panel is null || ReferenceEquals(document, _document)) return;
        if (_document is not null) _document.Changed -= OnDocumentChanged; // (the panel unsubscribed its own handler from it already)
        _document = document;
        _ready = null;
        _debounce?.Cancel();
        if (document is null) return;
        if (_panelTextChanged is not null) document.TextChanged -= _panelTextChanged; // no whole-document search per edit
        document.Changed += OnDocumentChanged;
    }

    /// <summary>The panel just built its stock strategy for new options (and is about to search with it): ours takes its place.</summary>
    private void OnSearchOptionsChanged(object? sender, SearchOptionsChangedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _panel)) return;
        var inner = CreateInner(e) ?? StrategyField!.GetValue(_panel) as ISearchStrategy;
        if (inner is null) return;
        _strategy = new Strategy(this, inner);
        _ready = null;
        StrategyField!.SetValue(_panel, _strategy);
    }

    /// <summary>The stock regex strategy (it makes the panel's result objects), over a regex WITH a match timeout.</summary>
    private static ISearchStrategy? CreateInner(SearchOptionsChangedEventArgs e)
    {
        var options = RegexOptions.Multiline | (e.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        var pattern = e.UseRegex ? e.SearchPattern ?? "" : Regex.Escape(e.SearchPattern ?? "");
        try
        {
            return RegexStrategyCtor?.Invoke([new Regex(pattern, options, MatchTimeout), e.WholeWords]) as ISearchStrategy;
        }
        catch (Exception ex) when (ex is ArgumentException or TargetInvocationException)
        {
            return null; // the panel's own factory accepted the pattern, so this does not happen; its strategy stays then
        }
    }

    // ---- edits ----

    /// <summary>UI thread, on every change (the typing path): the highlighted matches move with the change; the re-search waits for a pause.</summary>
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _ready = null; // computed for an older text
        if (_panel is null || _panel.IsClosed) return; // a closed panel holds no matches; opening it searches afresh
        _moveResults?.Invoke(e);
        ScheduleResearch();
    }

    private void ScheduleResearch()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(EditDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _view.Post(() =>
            {
                if (!cts.IsCancellationRequested) StartResearch();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>UI thread: hands the current text (an immutable snapshot) to the editor worker for the re-search.</summary>
    private void StartResearch()
    {
        if (_disposed || _panel is not { IsClosed: false } || _strategy is not { } strategy || _document is not { } document) return;
        if (string.IsNullOrEmpty(_panel.SearchPattern)) return;
        var snapshot = document.CreateSnapshot();
        var version = document.Version;
        if (version is null) return;
        _view.Factory.Worker.Post(WorkPriority.Background, this, ct =>
        {
            if (ct.IsCancellationRequested) return;
            var results = Collect(strategy.Inner.FindAll(snapshot, 0, snapshot.TextLength), out var timedOut, out var capped);
            Interlocked.Increment(ref _workerSearches);
            _view.Post(() => Apply(new Ready(version, strategy, results, timedOut, capped)));
        });
    }

    /// <summary>UI thread: the worker's results, handed to the panel when they are still for the current text and pattern.</summary>
    private void Apply(Ready ready)
    {
        if (_disposed || _panel is not { IsClosed: false } panel || !ReferenceEquals(ready.Strategy, _strategy)) return;
        if (_document?.Version is not { } version || !version.Equals(ready.Version)) return; // the text moved on: its own re-search follows
        _ready = ready;
        try
        {
            DoSearchMethod!.Invoke(panel, [false]); // the panel asks FindAll again, which hands these over
        }
        catch (TargetInvocationException)
        {
            // the panel could not take them: what it shows stays until the next search
        }
        finally
        {
            _ready = null;
        }
    }

    /// <summary>The panel asks for every match (its DoSearch; always the whole document). UI thread.</summary>
    private List<ISearchResult> FindAll(Strategy strategy, ITextSource document, int offset, int length)
    {
        if (_ready is { } ready && ReferenceEquals(ready.Strategy, strategy) && _document?.Version is { } version && version.Equals(ready.Version))
            return Show(ready.Results, ready.TimedOut, ready.Capped); // the re-search after an edit, done on the worker for exactly this text
        // a new pattern or option, the panel opening: now, on this thread (like VS Code), bounded by the limit and the timeout
        UiThreadSearches++;
        _debounce?.Cancel(); // this is the newest search there is
        var results = Collect(strategy.Inner.FindAll(document, offset, length), out var timedOut, out var capped);
        return Show(results, timedOut, capped);
    }

    private List<ISearchResult> Show(List<ISearchResult> results, bool timedOut, bool capped)
    {
        LastTimedOut = timedOut;
        LastCapped = capped;
        _lastCount = results.Count;
        if (timedOut || capped) _view.Post(ShowMessage); // after the panel's own label update, which runs when this search returns
        return results;
    }

    /// <summary>Takes matches from <paramref name="results"/> up to the limit; a regex that runs into its timeout ends the search there.</summary>
    private static List<ISearchResult> Collect(IEnumerable<ISearchResult> results, out bool timedOut, out bool capped)
    {
        var list = new List<ISearchResult>();
        timedOut = capped = false;
        try
        {
            foreach (var r in results)
            {
                if (list.Count >= MaxResults)
                {
                    capped = true;
                    break;
                }
                list.Add(r);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
        }
        return list;
    }

    private void ShowMessage()
    {
        if (_disposed || _panel is not { IsClosed: false } panel || Message is not { } message) return;
        if (panel.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_MessageContent") is { } label) label.Text = message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _view.Editor.TemplateApplied -= OnTemplateApplied;
        _view.Editor.TextArea.DocumentChanged -= OnTextAreaDocumentChanged;
        _debounce?.Dispose();
        _debounce = null;
        _panelTextChanged = null;
        _panelResults = null;
        _moveResults = null;
    }

    /// <summary>The strategy the panel holds: the stock one underneath, bounded, its results handed over by its owner.</summary>
    private sealed class Strategy(FindSearch owner, ISearchStrategy inner) : ISearchStrategy
    {
        public ISearchStrategy Inner => inner;

        public IEnumerable<ISearchResult> FindAll(ITextSource document, int offset, int length) => owner.FindAll(this, document, offset, length);

        public ISearchResult? FindNext(ITextSource document, int offset, int length)
        {
            try
            {
                return inner.FindAll(document, offset, length).FirstOrDefault();
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        public bool Equals(ISearchStrategy? other) => other is Strategy s && s.Inner.Equals(inner);

        public override bool Equals(object? obj) => obj is ISearchStrategy s && Equals(s);

        public override int GetHashCode() => inner.GetHashCode();
    }
}
