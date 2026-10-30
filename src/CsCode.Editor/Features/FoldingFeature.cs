using System.Text.RegularExpressions;
using AvaloniaEdit.Folding;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>A foldable region as whole lines: the first line stays visible, lines (StartLine, EndLine] fold away.</summary>
public readonly record struct FoldRange(int StartLine, int EndLine, FoldingRangeKind? Kind = null, string? CollapsedText = null);

/// <summary>
/// Folding ranges from a document snapshot, off the UI thread: language-configuration markers (<c>#region</c>/<c>#endregion</c>…) and
/// indentation (the VS Code default for languages without a folding provider), and the merge of several providers' ranges
/// (<see cref="Merge"/>). Pure and deterministic; capped so a pathological file cannot hand the UI an unbounded list.
/// </summary>
public static class FoldComputer
{
    public const int MaxRanges = 5000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(20);

    public static IReadOnlyList<FoldRange> Compute(ITextDocument doc, LanguageConfiguration config, int tabSize, CancellationToken ct = default) =>
        Compute(doc.LineCount, doc.GetLineText, config, tabSize, ct);

    /// <summary>The line source is read once per line: pass a snapshot's accessors so the whole computation sees ONE version of the text.</summary>
    public static IReadOnlyList<FoldRange> Compute(int count, Func<int, string> getLine, LanguageConfiguration config, int tabSize, CancellationToken ct = default)
    {
        var lines = new string[count];
        for (var i = 0; i < count; i++) lines[i] = getLine(i);
        var result = new List<FoldRange>();

        var markerKeys = new HashSet<int>();
        foreach (var r in Markers(lines, config, ct))
        {
            result.Add(r);
            markerKeys.Add(r.StartLine);
        }
        foreach (var r in Indentation(lines, tabSize, ct))
            if (!markerKeys.Contains(r.StartLine)) result.Add(r);

        result.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine) : b.EndLine.CompareTo(a.EndLine));
        if (result.Count > MaxRanges) result.RemoveRange(MaxRanges, result.Count - MaxRanges);
        return result;
    }

    /// <summary>The language configuration's marker regions alone (<c>#region</c> … <c>#endregion</c>), in document order.</summary>
    public static IReadOnlyList<FoldRange> MarkerRanges(int count, Func<int, string> getLine, LanguageConfiguration config, CancellationToken ct = default)
    {
        if (config.FoldingMarkersStart is not { Length: > 0 } || config.FoldingMarkersEnd is not { Length: > 0 }) return [];
        var lines = new string[count];
        for (var i = 0; i < count; i++) lines[i] = getLine(i);
        var result = Markers(lines, config, ct).ToList();
        result.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return result;
    }

    /// <summary>
    /// Several sources' ranges as one valid set, the way VS Code merges its folding providers (<c>sanitizeAndMerge</c>, folding's syntax range
    /// provider): <paramref name="byRank"/> holds each source's ranges, best first; ranges are taken by start line, and of ranges that start on
    /// the same line only the best-ranked source's stays; a range inside the current one nests, one that starts after it closes the levels it is
    /// past, and one that overlaps it without nesting is kept as it is. Invalid ranges (end not after start, negative lines, past
    /// <paramref name="lineCount"/>) are dropped first. At most <see cref="MaxRanges"/>.
    /// </summary>
    public static IReadOnlyList<FoldRange> Merge(IReadOnlyList<IReadOnlyList<FoldRange>> byRank, int lineCount)
    {
        var all = new List<(FoldRange Range, int Rank)>();
        for (var rank = 0; rank < byRank.Count; rank++)
            foreach (var r in byRank[rank])
                if (r.StartLine >= 0 && r.EndLine > r.StartLine && r.EndLine < lineCount) all.Add((r, rank));
        all.Sort((a, b) => a.Range.StartLine != b.Range.StartLine ? a.Range.StartLine.CompareTo(b.Range.StartLine) : a.Rank.CompareTo(b.Rank));

        var result = new List<FoldRange>(Math.Min(all.Count, MaxRanges));
        FoldRange? top = null;
        var previous = new Stack<FoldRange>();
        foreach (var (entry, _) in all)
        {
            if (result.Count >= MaxRanges) break;
            if (top is not { } t)
            {
                top = entry;
                result.Add(entry);
                continue;
            }
            if (entry.StartLine <= t.StartLine) continue; // the same start line: a better-ranked source's range is already there
            if (entry.EndLine <= t.EndLine)
            {
                previous.Push(t); // nested in the current range
                top = entry;
            }
            else if (entry.StartLine > t.EndLine)
            {
                // after the current range: back out to the enclosing range that still contains it (if any)
                FoldRange? enclosing = null;
                while (previous.Count > 0)
                {
                    var up = previous.Pop();
                    if (entry.StartLine <= up.EndLine)
                    {
                        enclosing = up;
                        break;
                    }
                }
                if (enclosing is { } e) previous.Push(e);
                top = entry;
            }
            result.Add(entry); // (overlapping the current range without nesting: kept, as VS Code keeps it)
        }
        return result;
    }

    private static IEnumerable<FoldRange> Markers(string[] lines, LanguageConfiguration config, CancellationToken ct)
    {
        if (config.FoldingMarkersStart is not { Length: > 0 } startPattern || config.FoldingMarkersEnd is not { Length: > 0 } endPattern) yield break;
        Regex start, end;
        try
        {
            start = new Regex(startPattern, RegexOptions.CultureInvariant, RegexTimeout);
            end = new Regex(endPattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            yield break;
        }
        var stack = new Stack<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            bool isStart, isEnd;
            try
            {
                isStart = start.IsMatch(lines[i]);
                isEnd = !isStart && end.IsMatch(lines[i]);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            if (isStart) stack.Push(i);
            else if (isEnd && stack.Count > 0)
            {
                var s = stack.Pop();
                if (i > s) yield return new FoldRange(s, i, FoldingRangeKind.Region);
            }
        }
    }

    /// <summary>
    /// A line starts a region when the next non-blank line is indented deeper; the region ends at the last line before indentation returns to
    /// its level. Blank lines never end a region and never start one; trailing blank lines are not part of it.
    /// </summary>
    private static IEnumerable<FoldRange> Indentation(string[] lines, int tabSize, CancellationToken ct)
    {
        var n = lines.Length;
        var indent = new int[n]; // -1 = blank
        for (var i = 0; i < n; i++) indent[i] = IndentOf(lines[i], tabSize);

        var stack = new Stack<(int Line, int Indent)>();
        for (var i = 0; i < n; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (indent[i] < 0) continue;
            while (stack.Count > 0 && stack.Peek().Indent >= indent[i])
            {
                var (start, _) = stack.Pop();
                var end = LastNonBlankBefore(indent, i);
                if (end > start) yield return new FoldRange(start, end);
            }
            // a line opens a region only if the next non-blank line is deeper (checked lazily: pushing is harmless when nothing deeper follows)
            stack.Push((i, indent[i]));
        }
        var lastLine = LastNonBlankBefore(indent, n);
        while (stack.Count > 0)
        {
            var (start, _) = stack.Pop();
            if (lastLine > start) yield return new FoldRange(start, lastLine);
        }
    }

    private static int LastNonBlankBefore(int[] indent, int exclusive)
    {
        var l = exclusive - 1;
        while (l >= 0 && indent[l] < 0) l--;
        return l;
    }

    private static int IndentOf(string line, int tabSize)
    {
        var width = 0;
        var i = 0;
        for (; i < line.Length; i++)
        {
            if (line[i] == ' ') width++;
            else if (line[i] == '\t') width += tabSize - width % tabSize;
            else break;
        }
        return i == line.Length ? -1 : width;
    }
}

/// <summary>
/// Code folding of one view: ranges come from the worker and are applied through AvaloniaEdit's <see cref="FoldingManager"/>, which also draws
/// the +/- margin and keeps the regions anchored while the user types. Applying a result is one cheap, versioned UI step; a result for an older
/// version is dropped.
/// <para>
/// Sources, combined as VS Code combines them (T32): every <see cref="IFoldingRangeProvider"/> for the document (language servers, the syntax
/// tree) is asked at once, each with its own deadline, and their ranges are merged in rank order (<see cref="FoldComputer.Merge"/>) together with
/// the language's <c>#region</c> markers (which servers and trees do not always report), so no provider loses its ranges because another
/// answered first. When no provider has anything (none registered, all silent, failed or too slow), indentation and markers fold the document.
/// A tree-sitter document whose tree was not there in time is folded again when it lands (<c>TokenizationManager.SyntaxTreeArrived</c>), without
/// waiting for an edit; every document is folded again when folding providers come or go.
/// </para>
/// </summary>
internal sealed class FoldingFeature : IFoldingFeature, ISettingsAware, IThemeAware, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly CodeEditorControl _v;
    private readonly FoldingManager _manager;
    private readonly DisposableStore _d = new();
    private readonly object _key = new();
    private IReadOnlyList<int>? _pendingFolds;
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public FoldingFeature(CodeEditorControl view)
    {
        _v = view;
        _manager = FoldingManager.Install(view.TextArea);
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule()));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(IFoldingRangeProvider)) Schedule();
        }));
        _d.Add(view.Factory.Tokens.SyntaxTreeArrived.Subscribe(d =>
        {
            if (ReferenceEquals(d, _v.Document)) Schedule(immediate: true);
        }));
        view.Controller.FoldedLinesProvider = () => FoldedStartLines;
        view.Controller.FoldRestorer = RestoreFolds;
        ApplyBrushes();
        Schedule(immediate: true);
    }

    /// <summary>Foldable regions currently installed (tests, diagnostics).</summary>
    internal int RangeCount => _manager.AllFoldings.Count();

    /// <summary>The installed regions as (first line, last line), in document order (tests, diagnostics). UI thread.</summary>
    internal IReadOnlyList<(int Start, int End)> Ranges => _manager.AllFoldings.Select(LinesOf).ToList();

    /// <summary>
    /// The installed regions whose lines contain <paramref name="line"/> (header line included), outermost first, as (first line, last line):
    /// what sticky scroll shows. Uses the manager's segment tree (no scan of all regions). UI thread.
    /// </summary>
    internal List<(int Start, int End)> RegionsContaining(int line)
    {
        var result = new List<(int Start, int End)>();
        var buffer = _v.Document.Buffer;
        if (line < 0 || line >= buffer.LineCount) return result;
        var dl = buffer.GetLineByNumber(line + 1);
        foreach (var f in _manager.GetFoldingsContaining(dl.Offset))
        {
            var (s, e) = LinesOf(f);
            if (s <= line && e >= line) result.Add((s, e));
        }
        result.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        return result;
    }

    public IReadOnlyList<int> FoldedStartLines =>
        _manager.AllFoldings.Where(f => f.IsFolded).Select(f => _v.Document.PositionAt(f.StartOffset).Line).Distinct().OrderBy(l => l).ToList();

    public void RestoreFolds(IReadOnlyList<int> startLines)
    {
        if (startLines.Count == 0) return;
        _pendingFolds = startLines;
        ApplyPendingFolds();
    }

    private void ApplyPendingFolds()
    {
        if (_pendingFolds is not { } wanted || !_manager.AllFoldings.Any()) return;
        var set = wanted.ToHashSet();
        foreach (var f in _manager.AllFoldings)
            if (set.Contains(_v.Document.PositionAt(f.StartOffset).Line)) f.IsFolded = true;
        _pendingFolds = null;
    }

    /// <summary>The hidden text of the collapsed region whose placeholder is at <paramref name="offset"/> (its header-line end), first 20 lines; null when none.</summary>
    internal string? FoldedTextAt(int offset)
    {
        var section = _manager.GetFoldingsAt(offset).FirstOrDefault(f => f.IsFolded && f.StartOffset == offset);
        if (section is null) return null;
        var lines = section.TextContent.Split('\n');
        return string.Join('\n', lines.Take(20)) + (lines.Length > 20 ? "\n…" : "");
    }

    // ---- commands ----

    public void Fold(bool recursive = false) => Toggle(fold: true, recursive);
    public void Unfold(bool recursive = false) => Toggle(fold: false, recursive);

    public void ToggleFold()
    {
        var sections = SectionsAtCaret();
        var innermost = sections.OrderByDescending(s => s.StartOffset).FirstOrDefault();
        if (innermost is not null) innermost.IsFolded = !innermost.IsFolded;
    }

    public void FoldAll()
    {
        foreach (var f in _manager.AllFoldings) f.IsFolded = true;
    }

    public void UnfoldAll()
    {
        foreach (var f in _manager.AllFoldings) f.IsFolded = false;
    }

    private void Toggle(bool fold, bool recursive)
    {
        var sections = SectionsAtCaret().ToList();
        if (sections.Count == 0) return;
        if (recursive)
        {
            var outer = fold ? sections.OrderByDescending(s => s.StartOffset).First() : sections.OrderBy(s => s.StartOffset).First();
            foreach (var f in _manager.AllFoldings.Where(f => f.StartOffset >= outer.StartOffset && f.EndOffset <= outer.EndOffset)) f.IsFolded = fold;
            return;
        }
        // fold: the innermost region that is open; unfold: the innermost folded region
        var target = fold
            ? sections.Where(s => !s.IsFolded).OrderByDescending(s => s.StartOffset).FirstOrDefault()
            : sections.Where(s => s.IsFolded).OrderByDescending(s => s.StartOffset).FirstOrDefault();
        if (target is not null) target.IsFolded = fold;
    }

    // ---- by line (the modal editing surface: Vim's zo zc za zO zC zR zM and closed-fold queries) ----

    /// <summary>Lines of a region: its first (visible) line and its last.</summary>
    private (int Start, int End) LinesOf(FoldingSection f) => (_v.Document.PositionAt(f.StartOffset).Line, _v.Document.PositionAt(f.EndOffset).Line);

    /// <summary>The outermost collapsed region whose lines include <paramref name="line"/>. Uses the manager's segment tree, never a scan.</summary>
    internal bool TryGetClosedFold(int line, out int startLine, out int endLine)
    {
        startLine = endLine = line;
        var buffer = _v.Document.Buffer;
        if (line < 0 || line >= buffer.LineCount) return false;
        var dl = buffer.GetLineByNumber(line + 1);
        FoldingSection? best = null;
        void Consider(FoldingSection f)
        {
            if (!f.IsFolded) return;
            var (s, e) = LinesOf(f);
            if (line < s || line > e) return;
            if (best is null || f.StartOffset < best.StartOffset || (f.StartOffset == best.StartOffset && f.EndOffset > best.EndOffset)) best = f;
        }
        foreach (var f in _manager.GetFoldingsContaining(dl.Offset)) Consider(f);
        foreach (var f in _manager.GetFoldingsContaining(dl.EndOffset)) Consider(f);
        foreach (var f in _manager.GetFoldingsAt(dl.EndOffset)) Consider(f);
        if (best is null) return false;
        (startLine, endLine) = LinesOf(best);
        return true;
    }

    internal void FoldLines(EditorFoldAction action, int startLine, int endLine)
    {
        if (endLine < startLine) (startLine, endLine) = (endLine, startLine);
        switch (action)
        {
            case EditorFoldAction.OpenAll: UnfoldAll(); return;
            case EditorFoldAction.CloseAll: FoldAll(); return;
        }
        // regions touching the line range, innermost (latest start) first
        var sections = _manager.AllFoldings.Where(f =>
        {
            var (s, e) = LinesOf(f);
            return s <= endLine && e >= startLine;
        }).OrderByDescending(f => f.StartOffset).ThenBy(f => f.EndOffset).ToList();
        if (sections.Count == 0) return;
        switch (action)
        {
            case EditorFoldAction.OpenRecursive:
                foreach (var f in sections) f.IsFolded = false;
                break;
            case EditorFoldAction.CloseRecursive:
                foreach (var f in sections) f.IsFolded = true;
                break;
            case EditorFoldAction.Open:
                // the outermost closed region shows; opening it reveals the next level (Vim's zo)
                if (sections.LastOrDefault(f => f.IsFolded) is { } closed) closed.IsFolded = false;
                break;
            case EditorFoldAction.Close:
                if (sections.FirstOrDefault(f => !f.IsFolded) is { } open) open.IsFolded = true;
                break;
            case EditorFoldAction.Toggle:
                if (sections.LastOrDefault(f => f.IsFolded) is { } c) c.IsFolded = false;
                else sections[0].IsFolded = true;
                break;
        }
    }

    private IEnumerable<FoldingSection> SectionsAtCaret()
    {
        var caret = _v.Editor.CaretOffset;
        var line = _v.Document.PositionAt(caret).Line;
        var lineStart = _v.Document.OffsetAt(new TextPosition(line, 0));
        var lineEnd = lineStart + _v.Document.GetLineText(line).Length;
        // regions that start on the caret's line (their "header" line) or contain the caret
        return _manager.AllFoldings.Where(f => (f.StartOffset >= lineStart && f.StartOffset <= lineEnd) || (f.StartOffset <= caret && f.EndOffset >= caret));
    }

    // ---- computing + applying ----

    private void Schedule(bool immediate = false)
    {
        if (_disposed || !_v.Settings.Folding) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        var delay = immediate ? TimeSpan.Zero : Debounce;
        _ = RunAfterAsync(delay, cts.Token);
    }

    private async Task RunAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed) return;
        var doc = _v.Document;
        var config = _v.LanguageConfiguration.Base;
        var tabSize = _v.Settings.TabSize;
        var providers = _v.Services.Features.GetProviders<IFoldingRangeProvider>(doc);
        // Post the work (latest-wins): everything below the Post runs on the editor worker, from a snapshot.
        _v.Factory.Worker.Post(WorkPriority.Background, _key, async token =>
        {
            var snapshot = doc.Snapshot();
            IReadOnlyList<FoldRange> ranges;
            try
            {
                ranges = await ComputeAsync(doc, snapshot, config, tabSize, providers, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _v.Post(() =>
            {
                if (_disposed || doc.Version != snapshot.Version) return; // a newer version is on its way: this result would be stale
                Apply(ranges);
            });
        });
    }

    /// <summary>How long one provider may take before its answer is left out (the others' and the fallback still fold the document).</summary>
    internal static TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal static async Task<IReadOnlyList<FoldRange>> ComputeAsync(ITextDocument doc, DocumentSnapshot snapshot, LanguageConfiguration config, int tabSize,
        IReadOnlyList<IFoldingRangeProvider> providers, CancellationToken ct)
    {
        if (providers.Count > 0)
        {
            var asks = new Task<IReadOnlyList<FoldingRange>?>[providers.Count];
            for (var i = 0; i < providers.Count; i++) asks[i] = AskAsync(providers[i], doc, ct);
            var answers = await Task.WhenAll(asks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var byRank = new List<IReadOnlyList<FoldRange>>(answers.Length + 1);
            foreach (var answer in answers)
                if (answer is { Count: > 0 })
                    byRank.Add(answer.Select(r => new FoldRange(r.StartLine, r.EndLine, r.Kind, r.CollapsedText)).ToList());
            if (byRank.Count > 0)
            {
                // the language's region markers last: a provider's own range on the same line (with its collapsed text) wins
                byRank.Add(FoldComputer.MarkerRanges(snapshot.LineCount, snapshot.GetLineText, config, ct));
                return FoldComputer.Merge(byRank, snapshot.LineCount);
            }
        }
        // nothing from any provider: never leave the document without folds
        return FoldComputer.Compute(snapshot.LineCount, snapshot.GetLineText, config, tabSize, ct);
    }

    /// <summary>One provider's answer within <see cref="ProviderTimeout"/>; null when it has none, fails or is too slow (a bad provider is "no result").</summary>
    private static async Task<IReadOnlyList<FoldingRange>?> AskAsync(IFoldingRangeProvider provider, ITextDocument doc, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProviderTimeout);
            return await provider.ProvideFoldingRangesAsync(doc, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // slow or hung
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>UI thread, small: converts line ranges to offsets and hands them to AvaloniaEdit, which keeps existing fold states.</summary>
    private void Apply(IReadOnlyList<FoldRange> ranges)
    {
        var doc = _v.Document;
        var folds = new List<NewFolding>(ranges.Count);
        foreach (var r in ranges)
        {
            if (r.EndLine >= doc.LineCount || r.StartLine >= r.EndLine) continue;
            var startLine = doc.GetLineText(r.StartLine);
            var start = doc.OffsetAt(new TextPosition(r.StartLine, startLine.Length));
            var endLine = doc.GetLineText(r.EndLine);
            var end = doc.OffsetAt(new TextPosition(r.EndLine, endLine.Length));
            if (end <= start) continue;
            folds.Add(new NewFolding(start, end) { Name = r.CollapsedText ?? " … " });
        }
        folds.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        _manager.UpdateFoldings(folds, -1);
        ApplyPendingFolds();
    }

    private void ApplyBrushes()
    {
        var p = _v.Palette;
        foreach (var margin in _v.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.SetValue(FoldingMargin.FoldingMarkerBrushProperty, p.FoldingControl);
            margin.SetValue(FoldingMargin.SelectedFoldingMarkerBrushProperty, p.Foreground);
            margin.SetValue(FoldingMargin.FoldingMarkerBackgroundBrushProperty, p.Background);
            margin.SetValue(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, p.Background);
        }
    }

    public void OnThemeChanged() => ApplyBrushes();

    public void OnSettingsChanged()
    {
        if (_v.Settings.Folding) Schedule(immediate: true);
        else _manager.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _d.Dispose();
        _v.Controller.FoldedLinesProvider = null;
        _v.Controller.FoldRestorer = null;
        FoldingManager.Uninstall(_manager);
    }
}
