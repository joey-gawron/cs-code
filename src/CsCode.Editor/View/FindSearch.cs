using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// The in-editor Find (AvaloniaEdit's stock <see cref="SearchPanel"/>) made safe for big documents and hostile patterns, the way VS Code's
/// find widget behaves (editor-4). The stock panel re-searched the WHOLE document on the UI thread on every edit while it was open (a 4 MB
/// file with Find open on "e": ~490k matches rebuilt per keystroke, 425 ms each), had no result limit, and compiled the user's regex with no
/// match timeout (a backtracking pattern hung the UI thread for good). This component changes:
/// <list type="bullet">
/// <item>a result limit of <see cref="MaxResults"/> (VS Code's <c>MATCHES_LIMIT</c>, 19,999): only the first ones are highlighted, and the
/// panel says so. The limit is on what the panel HOLDS, not on what Find reaches: like VS Code (which searches the model once it holds
/// <c>MATCHES_LIMIT</c> decorations), Find Next / Previous (F3, Shift+F3, Enter, Shift+Enter) past the last held match search the text from
/// the caret, a new pattern selects the match after the caret wherever it is, and Replace All replaces every match;</item>
/// <item>a regex match timeout (<see cref="MatchTimeout"/>): a pattern that backtracks without end is stopped, the matches found so far stay,
/// and the panel says why. Such a pattern is not run again after edits (only a new pattern or option, or reopening the panel, searches
/// again);</item>
/// <item>an edit no longer re-searches anything: the highlighted matches move with it (the panel's own segment collection, updated per change
/// like VS Code's tracked decorations), and once the typing has paused for <see cref="EditDelay"/> (VS Code: 100 ms) the document is
/// searched again on the editor worker, from an immutable snapshot, in slices of <see cref="TokenizationSession.SliceBudget"/> that a newer
/// edit cancels; the results replace the old ones on the UI thread only if the text has not moved on meanwhile;</item>
/// <item>Replace and Replace All search the CURRENT text (VS Code does too) instead of using the held matches, which after an edit are only
/// moved until the re-search lands: a deleted match left an empty segment there that Replace All filled with the replacement.</item>
/// </list>
/// A new pattern or option (typing in the find box, the panel opening) still searches at once on the UI thread, as VS Code does, bounded by
/// the limit and the timeout: Enter right after typing a pattern (a recorded macro, a quick user) must find the match.
///
/// The panel offers no hooks for any of it, so this works against AvaloniaEdit 12's private layout by reflection: the panel's strategy field
/// (swapped right after the panel built its own: <see cref="SearchPanel.SearchOptionsChanged"/> fires between the two), its per-edit
/// handler (unsubscribed from the document), its result collection (moved with each change), its <c>DoSearch</c> (re-run with the
/// worker's results), <c>UpdateSearch</c> and <c>SelectResult</c>, and its result type. Find Next / Previous / Replace / Replace All are
/// taken over by command bindings placed before the panel's own (they decline, so the panel's run, whenever the held matches answer the
/// command exactly) and by a tunnelling Enter handler on the panel. If that layout changes, nothing is installed and the stock behaviour stays
/// (<see cref="Supported"/>; the test of this component says so).
/// </summary>
internal sealed class FindSearch : IDisposable
{
    /// <summary>The most matches one search keeps (VS Code's find widget: 19,999).</summary>
    public const int MaxResults = 19_999;

    /// <summary>The longest one regex match operation may run before the search is stopped (settable for tests).</summary>
    internal static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Typing pause before an edit's re-search (VS Code: 100 ms).</summary>
    internal static TimeSpan EditDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Tests: runs on the worker at the start of each slice of a re-search (the component, the slice's number in its re-search, from 1).</summary>
    internal static Action<FindSearch, int>? ResearchSliceHookForTests { get; set; }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? StrategyField = typeof(SearchPanel).GetField("_strategy", Private);
    private static readonly FieldInfo? RendererField = typeof(SearchPanel).GetField("_renderer", Private);
    private static readonly MethodInfo? DoSearchMethod = typeof(SearchPanel).GetMethod("DoSearch", Private, [typeof(bool)]);
    private static readonly MethodInfo? UpdateSearchMethod = typeof(SearchPanel).GetMethod("UpdateSearch", Private, Type.EmptyTypes);
    private static readonly MethodInfo? SelectResultMethod = typeof(SearchPanel).GetMethod("SelectResult", Private, [typeof(TextSegment)]);
    private static readonly MethodInfo? PanelTextChanged = typeof(SearchPanel).GetMethod("TextArea_Document_TextChanged", Private, [typeof(object), typeof(EventArgs)]);
    private static readonly ConstructorInfo? RegexStrategyCtor = typeof(SearchPanel).Assembly.GetType("AvaloniaEdit.Search.RegexSearchStrategy")
        ?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Regex), typeof(bool)]);
    private static readonly Type? SearchResultType = typeof(SearchPanel).Assembly.GetType("AvaloniaEdit.Search.SearchResult");
    private static readonly PropertyInfo? SearchResultData = SearchResultType?.GetProperty("Data", BindingFlags.Instance | BindingFlags.Public);

    /// <summary>True when AvaloniaEdit's private layout is the one this component was written against (tests).</summary>
    internal static bool Supported =>
        StrategyField is not null && RendererField is not null && DoSearchMethod is not null && UpdateSearchMethod is not null
        && SelectResultMethod is not null && PanelTextChanged is not null && RegexStrategyCtor is not null
        && SearchResultType is not null && typeof(TextSegment).IsAssignableFrom(SearchResultType) && typeof(ISearchResult).IsAssignableFrom(SearchResultType)
        && SearchResultData?.PropertyType == typeof(Match) && SearchResultData.CanWrite;

    private readonly CodeEditorControl _view;
    private readonly RoutedCommandBinding[] _bindings;
    private SearchPanel? _panel;
    private EventHandler? _panelTextChanged; // the panel's own per-edit handler (a whole-document search), kept off the document
    private IEnumerable? _panelResults; // the panel's result collection
    private Action<DocumentChangeEventArgs>? _moveResults; // ...and its UpdateOffsets
    private TextDocument? _document;
    private Strategy? _strategy;
    private Ready? _ready; // a worker re-search waiting to be handed to the panel
    private int? _selectFrom; // where the panel's next DoSearch(changeSelection: true) selects from (set as it starts, read by FindAll)
    private TextSegment? _firstHeld, _lastHeld; // the first and the last of a capped search's held matches (moved with the edits)
    private bool _replacing; // inside Replace All's own update: the panel's results are rebuilt after it
    private long _lastEdit; // Stopwatch timestamp of the last change
    private bool _researchArmed; // a pause timer is running
    private int _researchEpoch; // bumped to drop pending pause timers
    private CancellationTokenSource? _pause; // the pending pause timer's (cancelled with it: nothing waits on a dropped one)
    private Research? _research; // the re-search running on the worker
    private string? _note; // a message of the last Replace (null: the search's own)
    private bool _disposed;

    private sealed record Ready(ITextSourceVersion Version, Strategy Strategy, List<ISearchResult> Results, bool TimedOut, bool Capped);

    public FindSearch(CodeEditorControl view)
    {
        _view = view;
        _bindings =
        [
            new RoutedCommandBinding(SearchCommands.FindNext, (_, e) => Run(e, () => Navigate(backwards: false)), (_, e) => e.CanExecute = CanNavigate(backwards: false)),
            new RoutedCommandBinding(SearchCommands.FindPrevious, (_, e) => Run(e, () => Navigate(backwards: true)), (_, e) => e.CanExecute = CanNavigate(backwards: true)),
            new RoutedCommandBinding(SearchCommands.ReplaceNext, (_, e) => Run(e, ReplaceNext), (_, e) => e.CanExecute = CanReplace()),
            new RoutedCommandBinding(SearchCommands.ReplaceAll, (_, e) => Run(e, ReplaceAll), (_, e) => e.CanExecute = CanReplace()),
        ];
        // the editor builds its search panel when its template is applied (after this event fires): bind to it then, and on demand (ShowFind)
        view.Editor.TemplateApplied += OnTemplateApplied;
        view.Editor.TextArea.DocumentChanged += OnTextAreaDocumentChanged;
        if (Supported)
        {
            // F3 / Shift+F3 / Alt+R / Alt+A with the keyboard in the text area or the find box reach the text area's bindings: ours first
            for (var i = _bindings.Length - 1; i >= 0; i--) view.Editor.TextArea.CommandBindings.Insert(0, _bindings[i]);
        }
        Attach();
    }

    // ---- what tests and the panel label read ----

    /// <summary>Whole-document searches run on the UI thread (a new pattern or option, the panel opening): never one per edit.</summary>
    internal int UiThreadSearches { get; private set; }

    /// <summary>Re-searches after edits, run to the end on the editor worker.</summary>
    internal int WorkerSearches => Volatile.Read(ref _workerSearches);
    private int _workerSearches;

    /// <summary>Worker slices the re-searches took (each at most about one <see cref="TokenizationSession.SliceBudget"/>).</summary>
    internal int WorkerSlices => Volatile.Read(ref _workerSlices);
    private int _workerSlices;

    /// <summary>Re-searches a newer edit stopped before they finished.</summary>
    internal int WorkerSearchesStopped => Volatile.Read(ref _workerStopped);
    private int _workerStopped;

    /// <summary>Find Next / Previous answered by searching the text (the target was past the held matches).</summary>
    internal int TextSearches { get; private set; }

    /// <summary>Where the matches the panel highlights start, as they are now (moved with the edits since the last search).</summary>
    internal IEnumerable<int> ResultStarts => _panelResults?.OfType<TextSegment>().Select(s => s.StartOffset).ToArray() ?? [];

    /// <summary>The matches the panel highlights, in document order; none while the panel is closed (the overview ruler's marks). UI thread.</summary>
    internal IEnumerable<(int Start, int End)> Matches =>
        _panel is { IsClosed: false } && _panelResults is not null ? _panelResults.OfType<TextSegment>().Select(s => (s.StartOffset, s.EndOffset)) : [];

    /// <summary>Matches the panel highlights.</summary>
    internal int ResultCount => _panelResults?.OfType<TextSegment>().Count() ?? 0;

    internal bool LastTimedOut { get; private set; }
    internal bool LastCapped { get; private set; }
    private int _lastCount;

    /// <summary>The message shown in the panel for a stopped or capped search, or a stopped Replace (null when there is none).</summary>
    internal string? Message =>
        _note
        ?? (LastTimedOut ? $"The regular expression took too long and was stopped ({_lastCount:N0} matches found before that)"
        : LastCapped ? $"More than {MaxResults:N0} matches: only the first {MaxResults:N0} are highlighted"
        : null);

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
        panel.PropertyChanged += OnPanelPropertyChanged;
        // the find box's buttons raise the commands on the panel: ours first there too; Enter / Shift+Enter in its boxes call the panel directly
        for (var i = _bindings.Length - 1; i >= 0; i--) panel.CommandBindings.Insert(0, _bindings[i]);
        panel.AddHandler(InputElement.KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        BindDocument(_view.Editor.TextArea.Document);
    }

    private void Detach()
    {
        // (the panel's own per-edit handler is never put back: the panel is going away, and a document shared with other views must not hold it)
        if (_panel is not null)
        {
            _panel.SearchOptionsChanged -= OnSearchOptionsChanged;
            _panel.PropertyChanged -= OnPanelPropertyChanged;
            foreach (var b in _bindings) _panel.CommandBindings.Remove(b);
            _panel.RemoveHandler(InputElement.KeyDownEvent, OnPanelKeyDown);
        }
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _panel = null;
        _document = null;
        _strategy = null;
        _ready = null;
        _selectFrom = null;
        _firstHeld = _lastHeld = null;
        CancelResearch();
    }

    /// <summary>The panel opened or closed: its matches appear on or leave the overview ruler.</summary>
    private void OnPanelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is "VisualParent" or "IsVisible") _view.Post(() => _view.NotifyMarkersChanged(Decorations.ScrollDecorations.Sources.Find)); // closing takes the panel out of the tree (IsClosed is a plain property)
    }

    /// <summary>The text area got another document: the panel subscribes its handler to it after this runs, so the swap happens a turn later.</summary>
    private void OnTextAreaDocumentChanged(object? sender, EventArgs e) => _view.Post(() => BindDocument(_view.Editor.TextArea.Document));

    private void BindDocument(TextDocument? document)
    {
        if (_disposed || _panel is null || ReferenceEquals(document, _document)) return;
        if (_document is not null) _document.Changed -= OnDocumentChanged; // (the panel unsubscribed its own handler from it already)
        _document = document;
        _ready = null;
        CancelResearch();
        if (document is null) return;
        if (_panelTextChanged is not null) document.TextChanged -= _panelTextChanged; // no whole-document search per edit
        document.Changed += OnDocumentChanged;
    }

    /// <summary>The panel just built its stock strategy for new options (and is about to search with it): ours takes its place.</summary>
    private void OnSearchOptionsChanged(object? sender, SearchOptionsChangedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _panel)) return;
        var inner = CreateInner(e, out var regex);
        inner ??= StrategyField!.GetValue(_panel) as ISearchStrategy;
        if (inner is null) return;
        _strategy = new Strategy(this, inner, regex, e.WholeWords);
        _ready = null;
        StrategyField!.SetValue(_panel, _strategy);
        // UpdateSearch calls DoSearch(changeSelection: true) next, which selects the first match at or after this offset (computed the same way)
        var area = _view.Editor.TextArea;
        _selectFrom = string.IsNullOrEmpty(e.SearchPattern) ? null : Math.Max(area.Caret.Offset - area.Selection.Length, 0);
    }

    /// <summary>The stock regex strategy (it makes the panel's result objects), over a regex WITH a match timeout.</summary>
    private static ISearchStrategy? CreateInner(SearchOptionsChangedEventArgs e, out Regex? regex)
    {
        var options = RegexOptions.Multiline | (e.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        var pattern = e.UseRegex ? e.SearchPattern ?? "" : Regex.Escape(e.SearchPattern ?? "");
        regex = null;
        try
        {
            var r = new Regex(pattern, options, MatchTimeout);
            var inner = RegexStrategyCtor?.Invoke([r, e.WholeWords]) as ISearchStrategy;
            if (inner is not null) regex = r;
            return inner;
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
        if (_research is { } running)
        {
            running.Cts.Cancel(); // its results are for an older text: stop it, the pause after this edit starts the next one
            _research = null;
        }
        if (_replacing || _panel is null || _panel.IsClosed) return; // a closed panel holds no matches; opening it searches afresh
        _moveResults?.Invoke(e);
        // a pattern that ran into the match timeout is not run again on every pause (each run could hold the worker for the whole timeout)
        if (!LastTimedOut) ScheduleResearch();
    }

    /// <summary>
    /// Starts the re-search once the edits have paused for <see cref="EditDelay"/>. One timer at a time: a change only records its time, so a
    /// burst of changes (a Replace All in another view of the document, a formatter's edit list) costs nothing per change here.
    /// </summary>
    private void ScheduleResearch()
    {
        _lastEdit = Stopwatch.GetTimestamp();
        if (_researchArmed) return;
        _researchArmed = true;
        ArmResearch(EditDelay, _researchEpoch);
    }

    private void ArmResearch(TimeSpan delay, int epoch)
    {
        _pause?.Dispose();
        var pause = _pause = new CancellationTokenSource();
        _ = Task.Delay(delay, pause.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _view.Post(() => ResearchDue(epoch));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void ResearchDue(int epoch)
    {
        if (_disposed || epoch != _researchEpoch || !_researchArmed) return;
        var quiet = Stopwatch.GetElapsedTime(_lastEdit);
        if (quiet < EditDelay)
        {
            ArmResearch(EditDelay - quiet, epoch); // typed again meanwhile: wait for the rest of the pause
            return;
        }
        _researchArmed = false;
        StartResearch();
    }

    /// <summary>Drops a pending pause and stops a running re-search.</summary>
    private void CancelResearch()
    {
        _researchEpoch++;
        _researchArmed = false;
        _pause?.Cancel();
        _research?.Cts.Cancel();
        _research = null;
    }

    /// <summary>Text one regex call reads on the worker: a re-search goes through the document in pieces of this size, a slice at a time.</summary>
    internal const int ChunkChars = 64 * 1024;

    /// <summary>Text a chunk's regex call sees after the chunk (a match that starts in it is not cut off at its end) and before it (lookbehinds, \b).</summary>
    private const int ChunkAfter = 4 * 1024, ChunkBefore = 1024;

    /// <summary>A re-search on the worker: an immutable snapshot, the matches so far, and where the scan goes on in the next slice.</summary>
    private sealed class Research(EditorWorker worker, Strategy strategy, Regex regex, ITextSourceVersion version, ITextSource snapshot)
    {
        public EditorWorker Worker { get; } = worker;
        public Strategy Strategy { get; } = strategy;
        public Regex Regex { get; } = regex;
        public ITextSourceVersion Version { get; } = version;
        public ITextSource Snapshot { get; } = snapshot;
        public CancellationTokenSource Cts { get; } = new();
        public List<ISearchResult> Results { get; } = [];
        public int Position { get; set; } // where a continuous scan would try its next match
        public int Slices { get; set; }
        public bool TimedOut { get; set; }
        public bool Capped { get; set; }
    }

    /// <summary>UI thread: hands the current text (an immutable snapshot) to the editor worker for the re-search.</summary>
    private void StartResearch()
    {
        if (_disposed || _panel is not { IsClosed: false } || _strategy is not { } strategy || _document is not { } document) return;
        if (string.IsNullOrEmpty(_panel.SearchPattern) || LastTimedOut) return;
        if (strategy.Regex is not { } regex) return; // (the panel's own strategy: its matches stay moved until the next search)
        var version = document.Version;
        if (version is null) return;
        _research?.Cts.Cancel();
        var worker = _view.Factory.Worker;
        var research = _research = new Research(worker, strategy, regex, version, document.CreateSnapshot());
        worker.Post(WorkPriority.Background, research, ct => ResearchSlice(research, ct));
    }

    /// <summary>
    /// Worker: one slice of a re-search, at most about <see cref="TokenizationSession.SliceBudget"/>, then the next slice is posted behind
    /// whatever else waits (an interactive job never waits longer than a slice). The scan reads the snapshot in chunks of
    /// <see cref="ChunkChars"/> (no copy of the whole document per pause, and a regex call that finds nothing ends at the chunk instead of
    /// the document's end), takes the matches that START in the chunk the way one continuous scan would, and goes on where that scan would.
    /// A newer edit cancels it between matches. One regex call over a chunk cannot be interrupted: <see cref="MatchTimeout"/> bounds it, and a
    /// pattern that reached it is not re-run on edits.
    /// </summary>
    private void ResearchSlice(Research r, CancellationToken ct)
    {
        if (r.Cts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _workerStopped);
            return;
        }
        Interlocked.Increment(ref _workerSlices);
        ResearchSliceHookForTests?.Invoke(this, ++r.Slices);
        var deadline = Stopwatch.GetTimestamp() + (long)(TokenizationSession.SliceBudget.TotalSeconds * Stopwatch.Frequency);
        var length = r.Snapshot.TextLength;
        var done = false;
        try
        {
            while (true)
            {
                if (r.Position > length)
                {
                    done = true;
                    break;
                }
                // matches starting in [Position, chunkEnd) (an empty one may sit at the very end), read with some text either side
                var chunkEnd = (int)Math.Min(length + 1L, (long)r.Position + ChunkChars);
                var from = Math.Max(0, r.Position - ChunkBefore);
                var to = (int)Math.Min(length, (long)chunkEnd + ChunkAfter);
                var text = r.Snapshot.GetText(from, to - from);
                var resume = r.Position;
                var sliceOver = false;
                foreach (var m in r.Regex.EnumerateMatches(text, r.Position - from))
                {
                    var index = from + m.Index;
                    if (index >= chunkEnd) break; // the next chunk's: it finds it with its own context
                    resume = m.Length == 0 ? index + 1 : index + m.Length; // (after an empty match a scan moves on by one)
                    if (!r.Strategy.WholeWords || IsWholeWord(r.Snapshot, index, m.Length))
                    {
                        if (r.Results.Count >= MaxResults)
                        {
                            r.Capped = true;
                            done = true;
                            break;
                        }
                        r.Results.Add(CreateResult(index, m.Length, null)!);
                    }
                    if (Stopwatch.GetTimestamp() >= deadline || r.Cts.IsCancellationRequested)
                    {
                        sliceOver = true;
                        break;
                    }
                }
                if (done) break;
                if (sliceOver)
                {
                    r.Position = resume;
                    break;
                }
                r.Position = Math.Max(chunkEnd, resume);
                if (r.Position <= length && (Stopwatch.GetTimestamp() >= deadline || r.Cts.IsCancellationRequested)) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            r.TimedOut = true;
            done = true;
        }
        if (!done)
        {
            if (r.Cts.IsCancellationRequested) Interlocked.Increment(ref _workerStopped);
            else r.Worker.Post(WorkPriority.Background, r, next => ResearchSlice(r, next)); // its own key: only it is replaced
            return;
        }
        Interlocked.Increment(ref _workerSearches);
        _view.Post(() =>
        {
            if (ReferenceEquals(_research, r)) _research = null;
            Apply(new Ready(r.Version, r.Strategy, r.Results, r.TimedOut, r.Capped));
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
        var selectFrom = _selectFrom;
        _selectFrom = null;
        _note = null;
        if (_ready is { } ready && ReferenceEquals(ready.Strategy, strategy) && _document?.Version is { } version && version.Equals(ready.Version))
            return Show(ready.Results, ready.TimedOut, ready.Capped); // the re-search after an edit, done on the worker for exactly this text
        // a new pattern or option, the panel opening: now, on this thread (like VS Code), bounded by the limit and the timeout
        UiThreadSearches++;
        CancelResearch(); // this is the newest search there is
        var source = new StringTextSource(document.Text); // one string for the search below and the one after the caret
        var results = Collect(strategy.Inner.FindAll(source, offset, length), out var timedOut, out var capped);
        Show(results, timedOut, capped);
        // A new pattern selects the first match at or after the caret (the panel picks it among those it holds, else wraps to the first). When the
        // limit cut the matches off before the caret, that one is found in the text and held too, so the panel selects it (VS Code moves to
        // the next match by a model search once it holds MATCHES_LIMIT decorations).
        if (capped && selectFrom is { } from && strategy.Regex is { } regex && results.Count > 0 && results[^1].Offset < from)
        {
            try
            {
                if (NextMatch(regex, strategy.WholeWords, source, from) is { } next && CreateResult(next.Index, next.Length, next) is { } held) results.Add(held);
            }
            catch (RegexMatchTimeoutException)
            {
                // the panel wraps to the first match, as it would without the limit cutting anything off
            }
        }
        return results;
    }

    private List<ISearchResult> Show(List<ISearchResult> results, bool timedOut, bool capped)
    {
        LastTimedOut = timedOut;
        LastCapped = capped;
        _lastCount = results.Count;
        _firstHeld = capped ? results[0] as TextSegment : null;
        _lastHeld = capped ? results[MaxResults - 1] as TextSegment : null;
        if (timedOut || capped) _view.Post(ShowMessage); // after the panel's own label update, which runs when this search returns
        _view.Post(() => _view.NotifyMarkersChanged(Decorations.ScrollDecorations.Sources.Find)); // the overview ruler, once the panel holds these
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

    // ---- Find Next / Previous past the held matches, Replace and Replace All on the current text ----

    /// <summary>The panel is open with our strategy over a regex we hold (the stock panel's commands run otherwise).</summary>
    private bool Active([NotNullWhen(true)] out SearchPanel? panel, [NotNullWhen(true)] out Strategy? strategy, [NotNullWhen(true)] out Regex? regex)
    {
        panel = _panel;
        strategy = _strategy;
        regex = strategy?.Regex;
        return !_disposed && panel is { IsClosed: false } && strategy is not null && regex is not null && !string.IsNullOrEmpty(panel.SearchPattern);
    }

    private static void Run(ExecutedRoutedEventArgs e, Action action)
    {
        e.Handled = true;
        action();
    }

    /// <summary>
    /// True when the panel's own Find Next / Previous would answer from an incomplete list: the search was capped and the match it needs lies
    /// past the last held one (or, backwards from before the first, the last one in the document). Cheap: no search.
    /// </summary>
    private bool CanNavigate(bool backwards)
    {
        if (!Active(out _, out _, out _) || !LastCapped || _firstHeld is not { } first || _lastHeld is not { } last) return false;
        var area = _view.Editor.TextArea;
        if (!backwards) return area.Caret.Offset > last.StartOffset;
        var from = Math.Max(area.Caret.Offset - area.Selection.Length, 0);
        return from > last.StartOffset || from <= first.StartOffset;
    }

    private bool CanReplace() => Active(out var panel, out _, out _) && panel.IsReplaceMode;

    /// <summary>Enter / Shift+Enter in the find or replace box: Find Next / Previous (the panel's KeyDown handler), taken over like F3.</summary>
    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter) return;
        var backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!CanNavigate(backwards)) return;
        e.Handled = true;
        Navigate(backwards);
    }

    /// <summary>Find Next / Previous answered from the text (see <see cref="CanNavigate"/>). UI thread, like the panel's own.</summary>
    private void Navigate(bool backwards)
    {
        if (!CanNavigate(backwards) || !Active(out var panel, out var strategy, out var regex)) return;
        var first = _firstHeld!;
        var last = _lastHeld!;
        var area = _view.Editor.TextArea;
        var source = new StringTextSource(area.Document.Text);
        TextSearches++;
        try
        {
            if (!backwards)
            {
                if (NextMatch(regex, strategy.WholeWords, source, area.Caret.Offset) is { } next) Select(panel, next.Index, next.Length);
                else panel.FindNext(0); // nothing after the caret: wrap to the first match, as the panel does
                return;
            }
            var from = Math.Max(area.Caret.Offset - area.Selection.Length, 0);
            // the last match before the caret (or, from before the first match, the last in the document); the held ones are known up to `last`
            var limit = from <= first.StartOffset ? source.TextLength + 1 : from;
            var previous = PreviousMatch(regex, strategy.WholeWords, source, limit, LineStart(source.Text, last.StartOffset));
            if (previous is not null && previous.Index > last.StartOffset) Select(panel, previous.Index, previous.Length);
            else Select(panel, last.StartOffset, last.Length);
        }
        catch (RegexMatchTimeoutException)
        {
            _note = "The regular expression took too long and was stopped";
            ShowMessage();
        }
    }

    private void Select(SearchPanel panel, int start, int length)
    {
        SelectResultMethod!.Invoke(panel, [new TextSegment { StartOffset = start, Length = length }]);
        ShowMessage(); // the limit's message stays (the panel's "x of y" counts only the held matches)
    }

    /// <summary>
    /// Replace (Alt+R, the panel's button): the first match at or after the selection's start IN THE CURRENT TEXT (wrapping to the top) is selected
    /// and replaced by the replace box's text as it is (the panel's own Replace has no substitutions), then the panel searches again and selects
    /// the next one. The panel's own Replace took the match from the held ones, which after an edit are only moved until the re-search lands
    /// (a match edited into "fo" + "x" had its "fo" replaced), and replaced the selection even when nothing matched.
    /// </summary>
    private void ReplaceNext()
    {
        if (!CanReplace() || !Active(out var panel, out var strategy, out var regex)) return;
        var area = _view.Editor.TextArea;
        var from = Math.Max(area.Caret.Offset - area.Selection.Length, 0);
        var source = new StringTextSource(area.Document.Text);
        Match? match;
        try
        {
            match = NextMatch(regex, strategy.WholeWords, source, from) ?? (from > 0 ? NextMatch(regex, strategy.WholeWords, source, 0) : null);
        }
        catch (RegexMatchTimeoutException)
        {
            _note = "The regular expression took too long and was stopped: nothing was replaced";
            ShowMessage();
            return;
        }
        if (match is null) return;
        SelectResultMethod!.Invoke(panel, [new TextSegment { StartOffset = match.Index, Length = match.Length }]);
        if (!area.Selection.IsEmpty) area.Selection.ReplaceSelectionWithText(panel.ReplacePattern ?? string.Empty);
        UpdateSearchMethod!.Invoke(panel, null);
    }

    /// <summary>
    /// Replace All (Alt+A, the panel's button): every match of the CURRENT text, not the held ones (capped at <see cref="MaxResults"/>, and
    /// only moved after an edit until the re-search lands), as VS Code's replaceAll searches the model again. One undo step, like the panel's.
    /// A pattern that runs into the match timeout replaces nothing (half a Replace All is worse than none) and the panel says so.
    /// </summary>
    private void ReplaceAll()
    {
        if (!CanReplace() || !Active(out var panel, out var strategy, out var regex)) return;
        var document = _view.Editor.TextArea.Document;
        var source = new StringTextSource(document.Text);
        var found = new List<(int Start, int Length)>();
        try
        {
            for (var m = regex.Match(source.Text); m.Success; m = m.NextMatch())
                if (!strategy.WholeWords || IsWholeWord(source, m)) found.Add((m.Index, m.Length));
        }
        catch (RegexMatchTimeoutException)
        {
            _note = "The regular expression took too long and was stopped: nothing was replaced";
            ShowMessage();
            return;
        }
        if (found.Count == 0) return;
        var replacement = panel.ReplacePattern ?? string.Empty;
        CancelResearch();
        _replacing = true;
        try
        {
            using (document.RunUpdate())
                for (var i = found.Count - 1; i >= 0; i--) document.Replace(found[i].Start, found[i].Length, replacement);
        }
        finally
        {
            _replacing = false;
        }
        DoSearchMethod!.Invoke(panel, [false]); // what the replaced text holds now, at once (the held matches were not moved through it)
    }

    // ---- searching the text from an offset ----

    /// <summary>The first match starting at or after <paramref name="from"/> (as the panel's strategy finds them: whole words filtered).</summary>
    private static Match? NextMatch(Regex regex, bool wholeWords, StringTextSource source, int from)
    {
        if (from > source.TextLength) return null;
        for (var m = regex.Match(source.Text, from); m.Success; m = m.NextMatch())
            if (!wholeWords || IsWholeWord(source, m)) return m;
        return null;
    }

    /// <summary>
    /// The last match starting before <paramref name="limit"/>, searched backwards in growing blocks of whole lines (VS Code searches backwards
    /// line by line), no further back than <paramref name="floor"/>. Each block is searched as its own input up to the end of the line that
    /// holds the limit, so a block without a match never scans the rest of the document.
    /// </summary>
    private static Match? PreviousMatch(Regex regex, bool wholeWords, StringTextSource source, int limit, int floor)
    {
        var text = source.Text;
        limit = Math.Min(limit, text.Length + 1);
        var end = LineEnd(text, Math.Min(limit, text.Length)); // the end of the line that holds the limit
        var start = Math.Min(limit, text.Length);
        for (var block = 4096; ; block *= 2)
        {
            start = Math.Max(floor, LineStart(text, Math.Max(0, start - block)));
            Match? last = null;
            for (var m = regex.Match(text, start, end - start); m.Success && m.Index < limit; m = m.NextMatch())
                if (!wholeWords || IsWholeWord(source, m)) last = m;
            if (last is not null || start <= floor) return last;
        }
    }

    private static int LineStart(string text, int offset)
    {
        if (offset <= 0) return 0;
        var nl = text.LastIndexOf('\n', Math.Min(offset, text.Length) - 1);
        return nl + 1;
    }

    private static int LineEnd(string text, int offset)
    {
        if (offset >= text.Length) return text.Length;
        var nl = text.IndexOf('\n', offset);
        return nl < 0 ? text.Length : nl;
    }

    /// <summary>The panel's whole-word test (its strategy's): a word border at both ends.</summary>
    private static bool IsWholeWord(ITextSource source, Match m) => IsWholeWord(source, m.Index, m.Length);

    private static bool IsWholeWord(ITextSource source, int start, int length) => IsWordBorder(source, start) && IsWordBorder(source, start + length);

    private static bool IsWordBorder(ITextSource source, int offset) =>
        TextUtilities.GetNextCaretPosition(source, offset - 1, LogicalDirection.Forward, CaretPositioningMode.WordBorder) == offset;

    /// <summary>
    /// A result object of the panel's own type (it casts what the strategy returns to it). <paramref name="match"/> is what its
    /// <c>ReplaceWith</c> would use; nothing calls that (the panel replaces with the replace box's text as it is), and the worker's chunked
    /// scan has offsets only.
    /// </summary>
    private static ISearchResult? CreateResult(int start, int length, Match? match)
    {
        if (Activator.CreateInstance(SearchResultType!) is not TextSegment segment) return null;
        segment.StartOffset = start;
        segment.Length = length;
        if (match is not null) SearchResultData!.SetValue(segment, match);
        return segment as ISearchResult;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        foreach (var b in _bindings) _view.Editor.TextArea.CommandBindings.Remove(b);
        _pause?.Dispose();
        _pause = null;
        _view.Editor.TemplateApplied -= OnTemplateApplied;
        _view.Editor.TextArea.DocumentChanged -= OnTextAreaDocumentChanged;
        _panelTextChanged = null;
        _panelResults = null;
        _moveResults = null;
    }

    /// <summary>The strategy the panel holds: the stock one underneath, bounded, its results handed over by its owner.</summary>
    private sealed class Strategy(FindSearch owner, ISearchStrategy inner, Regex? regex, bool wholeWords) : ISearchStrategy
    {
        public ISearchStrategy Inner => inner;

        /// <summary>The regex <see cref="Inner"/> runs (null when the panel's own strategy had to stay): Find and Replace search with it from an offset.</summary>
        public Regex? Regex => regex;

        public bool WholeWords => wholeWords;

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
