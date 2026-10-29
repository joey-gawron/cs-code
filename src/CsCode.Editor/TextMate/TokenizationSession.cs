using System.Diagnostics;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using Microsoft.Extensions.Logging;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;

namespace CsCode.Editor.TextMate;

/// <summary>
/// Syntax tokenisation of ONE document, shared by all of its views. Edits update the line structure on the UI thread (cheap, no text
/// inspected); all tokenising happens on the editor worker from immutable <see cref="DocumentSnapshot"/>s, viewport first, in slices of at
/// most <see cref="SliceBudget"/> so an interactive job never waits long. Results are published with the document version they were computed
/// for and refused when stale. Far jumps in huge files do not tokenise everything in between: beyond <see cref="MaxCatchUpLines"/> the
/// viewport is tokenised from a fresh state ("island"), which is right except inside a multi-line construct opened above the window.
/// </summary>
internal sealed class TokenizationSession : IDisposable
{
    public static readonly TimeSpan SliceBudget = TimeSpan.FromMilliseconds(6);
    public const int MaxCatchUpLines = 2000;
    public const int IslandOverscanLines = 200;
    public const int ReadAheadLines = 3000;
    public const int MaxTokenizedLineLength = 10_000;
    private static readonly TimeSpan[] PerLineLimits = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(300)];

    private readonly TokenizationManager _owner;
    private readonly EditorDocument _doc;
    private readonly DisposableStore _subscriptions = new();
    private readonly Emitter<(int First, int Last)> _tokensChanged = new();
    private readonly List<int> _pairs = new(); // worker-only scratch
    private readonly object _redrawGate = new();
    private int _viewFirst;
    private int _viewLast = 60;
    private int _redrawFirst = int.MaxValue, _redrawLast = -1;
    private bool _redrawPosted;
    private volatile bool _disposed;
    private volatile bool _hasGrammar;
    private int _views;

    public TokenizationSession(TokenizationManager owner, EditorDocument doc)
    {
        _owner = owner;
        _doc = doc;
        Store = new LineTokenStore();
        Store.Reset(doc.LineCount, doc.Version);
        _subscriptions.Add(doc.Changed.Subscribe(OnDocumentChanged));
        _subscriptions.Add(doc.LanguageIdChanged.Subscribe(_ => Restart()));
        // No work is scheduled until a view attaches (AttachView): nothing is tokenised for a document nobody sees.
    }

    internal LineTokenStore Store { get; }
    public ScopeInterner Interner => _owner.Interner;
    public EditorDocument Document => _doc;

    /// <summary>True once a grammar exists for the document's language (false for plain text).</summary>
    public bool HasGrammar => _hasGrammar;

    /// <summary>Fires on the UI thread, coalesced, with the line range whose tokens changed (views redraw that range).</summary>
    public IEventSource<(int First, int Last)> TokensChanged => _tokensChanged;

    // ---- UI-thread entry points ----

    /// <summary>The views report what is on screen; tokenising of that range takes priority over everything else here.</summary>
    public void SetViewport(int firstLine, int lastLine)
    {
        Volatile.Write(ref _viewFirst, Math.Max(0, firstLine));
        Volatile.Write(ref _viewLast, Math.Max(firstLine, lastLine));
        if (!Store.HasTokens(firstLine, lastLine) || lastLine >= Store.ValidLines) Schedule(WorkPriority.Visible);
    }

    private void OnDocumentChanged(TextDocumentChangedEventArgs e)
    {
        foreach (var c in e.Changes)
            Store.ApplyChange(c.Range.Start.Line, c.Range.End.Line - c.Range.Start.Line, CountBreaks(c.Text));
        Store.SetVersion(e.Version);
        if (Store.LineCount != _doc.LineCount) Store.Reset(_doc.LineCount, e.Version); // e.g. an edit that split a CRLF pair: resync instead of drifting
        Schedule(WorkPriority.Interactive);
    }

    internal void RestartFromManager() => Restart();

    /// <summary>
    /// A view started showing this document. Tokenisation only works while at least one view exists (nothing is tokenised for a document
    /// nobody sees); when the last view goes away the token memory is released and rebuilt on demand.
    /// </summary>
    public void AttachView()
    {
        if (Interlocked.Increment(ref _views) == 1) Schedule(WorkPriority.Visible);
    }

    public void DetachView()
    {
        if (Interlocked.Decrement(ref _views) <= 0)
        {
            Interlocked.Exchange(ref _views, 0);
            Store.Trim(aggressive: true, 0, 0);
        }
    }

    private void Restart()
    {
        Store.Reset(_doc.LineCount, _doc.Version);
        _hasGrammar = false;
        Schedule(WorkPriority.Visible);
        NotifyChanged(0, int.MaxValue);
    }

    /// <summary>
    /// The coarse token class (comment/string/regex/other) at a position, from the most recent tokens (possibly one edit stale, which is
    /// what auto-closing needs). <see cref="TokenKind.Other"/> when the line is not tokenised.
    /// </summary>
    public TokenKind KindAt(int line, int character)
    {
        var tokens = Store.GetTokens(line);
        if (tokens is null || tokens.Length == 0) return TokenKind.Other;
        var probe = Math.Max(0, character - 1); // the character just left of the caret decides ("don't pair a quote typed inside a string")
        var lo = 0;
        var hi = tokens.Length / 2 - 1;
        var found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (tokens[mid * 2] <= probe)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return Interner.Kind(tokens[found * 2 + 1]);
    }

    private void Schedule(WorkPriority priority)
    {
        if (_disposed || Volatile.Read(ref _views) <= 0) return;
        _owner.Worker.Post(priority, this, _ => RunSlice());
    }

    // ---- worker ----

    private void RunSlice()
    {
        if (_disposed) return;
        var grammar = _owner.Grammars.GrammarForLanguage(_doc.LanguageId);
        _hasGrammar = grammar is not null;
        if (grammar is null) return;

        var snap = _doc.Snapshot();
        if (Store.Version != snap.Version || Store.LineCount != snap.LineCount) return; // the UI has not caught up with this version yet: its Changed handler schedules the next slice

        var lineCount = snap.LineCount;
        var vFirst = Math.Min(Volatile.Read(ref _viewFirst), lineCount - 1);
        var vLast = Math.Min(Math.Max(Volatile.Read(ref _viewLast), vFirst), lineCount - 1);
        var valid = Store.ValidLines;
        var target = Math.Min(lineCount - 1, vLast + ReadAheadLines);

        var viewportDone = Store.HasTokens(vFirst, vLast) && vLast < valid;
        int begin;
        if (!viewportDone) begin = Math.Min(vFirst, valid);
        else if (valid <= target && vFirst - valid <= MaxCatchUpLines) begin = valid; // viewport is good: read ahead along the valid chain
        else return;                                                                  // nothing useful left to do for now
        var (start, state) = Store.ResumePoint(begin);
        var chain = true;
        if (vFirst - start > MaxCatchUpLines && !viewportDone)
        {
            start = Math.Max(0, vFirst - IslandOverscanLines) / LineTokenStore.CheckpointInterval * LineTokenStore.CheckpointInterval;
            state = null;
            chain = false;
            target = vLast; // an island is just the window
        }
        if (start > target) return;

        var clock = Stopwatch.StartNew();
        var batch = new List<int[]>(16);
        var checkpoints = new List<(int, IStateStack)>(4);
        var batchStart = start;
        var line = start;
        for (; line <= target; line++)
        {
            if (_disposed) return;
            if (line > start && line % LineTokenStore.CheckpointInterval == 0 && state is not null) checkpoints.Add((line, state));
            var text = snap.GetLineText(line);
            batch.Add(TokenizeLine(grammar, text, ref state));

            var sliceOver = clock.Elapsed >= SliceBudget;
            if (batch.Count >= 16 || sliceOver || line == target)
            {
                if (!Store.Publish(snap.Version, batchStart, batch, checkpoints, chain)) return; // stale: drop it, a newer job follows the newer version
                NotifyChanged(batchStart, line);
                batchStart = line + 1;
                batch.Clear();
                checkpoints.Clear();
                if (sliceOver && line < target)
                {
                    // Yield to anything more urgent, then carry on. Visible lines keep Visible priority; read-ahead drops to Background.
                    Schedule(line < vLast ? WorkPriority.Visible : WorkPriority.Background);
                    return;
                }
            }
        }
    }

    private int[] TokenizeLine(IGrammar grammar, string text, ref IStateStack? state)
    {
        _pairs.Clear();
        var length = text.Length;
        try
        {
            var input = length > MaxTokenizedLineLength ? text[..MaxTokenizedLineLength] : text;
            // A line that hits the time limit is truncated. The first lines of a grammar pay for lazy regex compilation, so a stopped-early
            // line is retried with escalating limits before a truncated result is accepted (only a genuinely pathological line gets that far).
            ITokenizeLineResult result = grammar.TokenizeLine(input, state, PerLineLimits[0]);
            for (var attempt = 1; attempt < PerLineLimits.Length && result is TokenizeLineResult { StoppedEarly: true }; attempt++)
                result = grammar.TokenizeLine(input, state, PerLineLimits[attempt]);
            state = result.RuleStack;
            var previousId = -1;
            foreach (var token in result.Tokens)
            {
                if (token.StartIndex >= Math.Max(length, 1) && _pairs.Count > 0) continue; // the synthetic end-of-line token
                var id = Interner.Intern(token.Scopes);
                if (id == previousId) continue; // adjacent tokens with the same scopes are one run
                _pairs.Add(Math.Min(token.StartIndex, length));
                _pairs.Add(id);
                previousId = id;
            }
        }
        catch (Exception ex)
        {
            // A grammar that throws on one line must not stop the document: leave the line plain and keep the incoming state.
            _owner.Log.LogDebug(ex, "TextMate tokenisation failed on a line of {Uri}", _doc.Uri);
            _pairs.Clear();
        }
        if (_pairs.Count == 0)
        {
            _pairs.Add(0);
            _pairs.Add(0);
        }
        return _pairs.ToArray();
    }

    private void NotifyChanged(int first, int last)
    {
        lock (_redrawGate)
        {
            _redrawFirst = Math.Min(_redrawFirst, first);
            _redrawLast = Math.Max(_redrawLast, last);
            if (_redrawPosted) return;
            _redrawPosted = true;
        }
        _owner.Ui.Post(() =>
        {
            int f, l;
            lock (_redrawGate)
            {
                f = _redrawFirst;
                l = _redrawLast;
                _redrawFirst = int.MaxValue;
                _redrawLast = -1;
                _redrawPosted = false;
            }
            if (!_disposed && l >= f) _tokensChanged.Fire((f, l));
        });
    }

    internal static int CountBreaks(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                n++;
            }
            else if (text[i] == '\n') n++;
        }
        return n;
    }

    /// <summary>Memory trimming for this session (called by the manager from the memory monitor's thread: everything here is thread-safe).</summary>
    internal long Trim(MemoryTrimLevel level) =>
        level == MemoryTrimLevel.Idle
            ? 0
            : Store.Trim(level == MemoryTrimLevel.Aggressive, Math.Max(0, Volatile.Read(ref _viewFirst) - 500), Volatile.Read(ref _viewLast) + 500);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
        _tokensChanged.Clear();
        Store.Clear();
        _owner.Remove(this);
    }
}
