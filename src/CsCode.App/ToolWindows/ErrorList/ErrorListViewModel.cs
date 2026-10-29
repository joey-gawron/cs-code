using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.ToolWindows.ErrorList;

/// <summary>One Error List row (or a file group header when grouping by file).</summary>
public sealed record ErrorRow(
    Uri Uri, string FileName, string Directory, int Line, int Column, DiagnosticSeverity Severity,
    string Message, string? Source, string? Code, TextRangeInfo Range, bool IsHeader = false, int HeaderCount = 0)
{
    public string Location => IsHeader ? "" : $"{FileName} ({Line}, {Column})";
    public string SeverityIcon => Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" };
    public string SeverityColorId => Severity switch { DiagnosticSeverity.Error => "problemsErrorIcon.foreground", DiagnosticSeverity.Warning => "problemsWarningIcon.foreground", _ => "problemsInfoIcon.foreground" };
    public string HeaderText => IsHeader ? $"{FileName}  ({HeaderCount})  {Directory}" : "";
}

/// <summary>Plain range copy so rows do not reference Core text types in bindings.</summary>
public readonly record struct TextRangeInfo(int StartLine, int StartChar, int EndLine, int EndChar);

public enum ErrorListSort { Severity, File, Line }

/// <summary>
/// The Error List model. Diagnostics arrive from language servers on arbitrary threads at arbitrary rates; this model only marks itself dirty
/// there. A worker thread then snapshots <see cref="IDiagnosticService.GetAll"/>, filters, sorts and groups (never the UI thread), at
/// most once per <see cref="MinIntervalMs"/> (10 Hz), and the UI thread swaps in the finished immutable array: one property change, no
/// per-row work. The list control virtualises, so 50k rows cost nothing to display.
/// </summary>
public sealed class ErrorListViewModel : ObservableObject, IDisposable
{
    public const int DefaultMinIntervalMs = 100;

    private readonly IDiagnosticService _diagnostics;
    private readonly IUiDispatcher _ui;
    private readonly INavigationService? _navigation;
    private readonly DisposableStore _subs = new();
    private readonly object _gate = new();
    private readonly int _minIntervalMs;
    private bool _dirty, _running, _disposed;
    private long _lastRunTicks;
    private bool _showErrors = true, _showWarnings = true, _showInfo = true, _groupByFile;
    private string _search = "";
    private ErrorListSort _sort = ErrorListSort.Severity;
    private IReadOnlyList<ErrorRow> _rows = [];
    private int _errors, _warnings, _infos;
    private int _rebuilds;

    public ErrorListViewModel(IDiagnosticService diagnostics, IUiDispatcher ui, INavigationService? navigation = null, int minIntervalMs = DefaultMinIntervalMs)
    {
        _diagnostics = diagnostics;
        _ui = ui;
        _navigation = navigation;
        _minIntervalMs = minIntervalMs;
        _subs.Add(diagnostics.Changed.Subscribe(_ => MarkDirty()));
        MarkDirty();
    }

    /// <summary>The currently displayed rows (replaced wholesale; never mutated).</summary>
    public IReadOnlyList<ErrorRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    public int ErrorCount { get => _errors; private set => SetProperty(ref _errors, value); }
    public int WarningCount { get => _warnings; private set => SetProperty(ref _warnings, value); }
    public int InfoCount { get => _infos; private set => SetProperty(ref _infos, value); }

    public bool ShowErrors { get => _showErrors; set { if (SetProperty(ref _showErrors, value)) MarkDirty(); } }
    public bool ShowWarnings { get => _showWarnings; set { if (SetProperty(ref _showWarnings, value)) MarkDirty(); } }
    public bool ShowInfo { get => _showInfo; set { if (SetProperty(ref _showInfo, value)) MarkDirty(); } }
    public bool GroupByFile { get => _groupByFile; set { if (SetProperty(ref _groupByFile, value)) MarkDirty(); } }
    public ErrorListSort Sort { get => _sort; set { if (SetProperty(ref _sort, value)) MarkDirty(); } }
    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) MarkDirty(); } }

    /// <summary>How many worker rebuilds ran (tests assert throttling).</summary>
    public int RebuildCount => Volatile.Read(ref _rebuilds);

    /// <summary>Awaits the currently scheduled rebuild (tests and shutdown).</summary>
    public async Task WaitIdleAsync(int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            bool busy;
            lock (_gate) busy = _dirty || _running;
            if (!busy) return;
            await Task.Delay(5).ConfigureAwait(true);
        }
    }

    private void MarkDirty()
    {
        bool start;
        lock (_gate)
        {
            if (_disposed) return;
            _dirty = true;
            start = !_running;
            if (start) _running = true;
        }
        if (start) _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (true)
        {
            var wait = _minIntervalMs - (int)((System.Diagnostics.Stopwatch.GetTimestamp() - _lastRunTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (wait > 0 && _lastRunTicks != 0) await Task.Delay(wait).ConfigureAwait(false);
            bool filterErr, filterWarn, filterInfo, group;
            string search;
            ErrorListSort sort;
            lock (_gate)
            {
                if (_disposed || !_dirty) { _running = false; return; }
                _dirty = false;
                (filterErr, filterWarn, filterInfo, group, search, sort) = (_showErrors, _showWarnings, _showInfo, _groupByFile, _search, _sort);
            }
            _lastRunTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Result result;
            try { result = Build(filterErr, filterWarn, filterInfo, group, search, sort); }
            catch (Exception) { continue; /* a provider handed us something odd: skip this pass, the next change retries */ }
            Interlocked.Increment(ref _rebuilds);
            _ui.PostCoalesced("errorlist.apply", () =>
            {
                Rows = result.Rows;
                ErrorCount = result.Errors;
                WarningCount = result.Warnings;
                InfoCount = result.Infos;
            });
        }
    }

    private sealed record Result(IReadOnlyList<ErrorRow> Rows, int Errors, int Warnings, int Infos);

    private Result Build(bool errors, bool warnings, bool info, bool group, string search, ErrorListSort sort)
    {
        var all = _diagnostics.GetAll();
        int e = 0, w = 0, i = 0;
        var rows = new List<ErrorRow>(Math.Min(all.Count, 100_000));
        foreach (var (uri, d) in all)
        {
            switch (d.Severity)
            {
                case DiagnosticSeverity.Error: e++; break;
                case DiagnosticSeverity.Warning: w++; break;
                default: i++; break;
            }
            var show = d.Severity switch { DiagnosticSeverity.Error => errors, DiagnosticSeverity.Warning => warnings, _ => info };
            if (!show) continue;
            var path = uri.IsFile ? uri.LocalPath : uri.ToString();
            var name = Path.GetFileName(path);
            if (search.Length > 0
                && !(d.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || name.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || (d.Code?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) || (d.Source?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                continue;
            rows.Add(new ErrorRow(uri, name, Path.GetDirectoryName(path) ?? "", d.Range.Start.Line + 1, d.Range.Start.Character + 1, d.Severity, FirstLine(d.Message), d.Source, d.Code,
                new TextRangeInfo(d.Range.Start.Line, d.Range.Start.Character, d.Range.End.Line, d.Range.End.Character)));
        }

        Comparison<ErrorRow> cmp = sort switch
        {
            ErrorListSort.File => static (a, b) => Compare(a, b, fileFirst: true),
            ErrorListSort.Line => static (a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : Compare(a, b, fileFirst: true),
            _ => static (a, b) => Compare(a, b, fileFirst: false),
        };
        rows.Sort(cmp);

        if (!group) return new Result(rows, e, w, i);
        var grouped = new List<ErrorRow>(rows.Count + 64);
        for (var start = 0; start < rows.Count;)
        {
            var end = start;
            while (end < rows.Count && rows[end].Uri == rows[start].Uri) end++;
            var first = rows[start];
            grouped.Add(first with { IsHeader = true, HeaderCount = end - start, Message = "", Source = null, Code = null });
            for (var k = start; k < end; k++) grouped.Add(rows[k]);
            start = end;
        }
        return new Result(grouped, e, w, i);
    }

    private static int Compare(ErrorRow a, ErrorRow b, bool fileFirst)
    {
        if (!fileFirst && a.Severity != b.Severity) return a.Severity.CompareTo(b.Severity);
        var f = string.Compare(a.Uri.ToString(), b.Uri.ToString(), StringComparison.OrdinalIgnoreCase);
        if (f != 0) return f;
        if (a.Severity != b.Severity) return a.Severity.CompareTo(b.Severity);
        return a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);
    }

    private static string FirstLine(string message)
    {
        var nl = message.IndexOf('\n');
        return nl < 0 ? message : message[..nl].TrimEnd('\r');
    }

    /// <summary>Double-click / Enter: opens the file at the diagnostic's range.</summary>
    public async Task NavigateAsync(ErrorRow row)
    {
        if (_navigation is null || row.IsHeader && row.Line <= 0) return;
        var r = row.Range;
        await _navigation.OpenAsync(row.Uri, new CsCode.Core.Text.TextRange(new(r.StartLine, r.StartChar), new(r.EndLine, r.EndChar))).ConfigureAwait(true);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _dirty = false; }
        _subs.Dispose();
    }
}
