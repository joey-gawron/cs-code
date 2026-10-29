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
/// indentation (the VS Code default for languages without a folding provider). Pure and deterministic; capped so a pathological file cannot
/// hand the UI an unbounded list.
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
/// Code folding of one view: ranges come from the worker (<see cref="FoldComputer"/>, or <see cref="IFoldingRangeProvider"/> when a language
/// server offers them) and are applied through AvaloniaEdit's <see cref="FoldingManager"/>, which also draws the +/- margin and keeps the
/// regions anchored while the user types. Applying a result is one cheap, versioned UI step; a result for an older version is dropped.
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
        view.Controller.FoldedLinesProvider = () => FoldedStartLines;
        view.Controller.FoldRestorer = RestoreFolds;
        ApplyBrushes();
        Schedule(immediate: true);
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

    private static async Task<IReadOnlyList<FoldRange>> ComputeAsync(EditorDocument doc, DocumentSnapshot snapshot, LanguageConfiguration config, int tabSize,
        IReadOnlyList<IFoldingRangeProvider> providers, CancellationToken ct)
    {
        if (providers.Count > 0)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var provided = await providers[0].ProvideFoldingRangesAsync(doc, timeout.Token).ConfigureAwait(false);
                if (provided is { Count: > 0 })
                    return provided.Where(r => r.EndLine > r.StartLine).Select(r => new FoldRange(r.StartLine, r.EndLine, r.Kind, r.CollapsedText)).Take(FoldComputer.MaxRanges).ToList();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // slow/hung provider: fall back to the local computation, never leave the document without folds
            }
        }
        return FoldComputer.Compute(snapshot.LineCount, snapshot.GetLineText, config, tabSize, ct);
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
        _disposed = true;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _d.Dispose();
        _v.Controller.FoldedLinesProvider = null;
        _v.Controller.FoldRestorer = null;
        FoldingManager.Uninstall(_manager);
    }
}
