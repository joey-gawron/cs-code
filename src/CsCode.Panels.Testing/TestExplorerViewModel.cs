using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Testing.Engine;
using CsCode.Ui.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Testing;

/// <summary>One visible row of the explorer: a node of the test tree at its depth, with what the row shows.</summary>
public sealed class TestRow : INotifyPropertyChanged
{
    internal TestRow(TestNode node) => Node = node;

    public TestNode Node { get; }
    public string Label => Node.Label;
    public int Depth => Node.Depth;
    public double Indent => Node.Depth * 14;
    public bool HasChildren => Node.Children.Count > 0;
    public bool IsExpanded { get; internal set; }
    public string Chevron => !HasChildren ? "" : IsExpanded ? "chevron-down" : "chevron-right";
    public TestOutcome Outcome => Node.Outcome;
    public string? Error => Node.Error;

    /// <summary>The state icon (Codicon name).</summary>
    public string StateIcon => Node.Error is not null ? "warning" : Node.Outcome switch
    {
        TestOutcome.Passed => "pass-filled",
        TestOutcome.Failed => "error",
        TestOutcome.Skipped => "circle-slash",
        TestOutcome.Running => "loading",
        TestOutcome.Queued => "history",
        _ => "circle",
    };

    /// <summary>The state colour (VS Code's testing.icon* defaults).</summary>
    public string StateColor => Node.Error is not null ? "#CCA700" : Node.Outcome switch
    {
        TestOutcome.Passed => "#73C991",
        TestOutcome.Failed => "#F14C4C",
        TestOutcome.Skipped => "#848484",
        TestOutcome.Running or TestOutcome.Queued => "#CCA700",
        _ => "#848484",
    };

    /// <summary>The kind icon shown after the state (project, namespace, class...).</summary>
    public string? KindIcon => Node.Kind switch
    {
        TestNodeKind.Project => "project",
        TestNodeKind.Target => "package",
        TestNodeKind.Namespace or TestNodeKind.Module => "symbol-namespace",
        TestNodeKind.File => "file-code",
        TestNodeKind.Class => "symbol-class",
        TestNodeKind.Suite => "symbol-structure",
        _ => null,
    };

    public string DurationText => Node.Duration is { } d && Node.Outcome is not (TestOutcome.Running or TestOutcome.Queued) ? TestController.Format(d) : "";

    /// <summary>The second part of a project row: its framework ("xunit.v3", "cargo", "jest"), or the discovery error.</summary>
    public string Detail => Node.Kind == TestNodeKind.Project ? (Node.Error ?? Node.Project.Framework) : Node.Kind == TestNodeKind.Test && Node.SkipReason is { } s ? "skip: " + s : "";

    public string Tooltip => Node.TestId is { } id && Node.Kind == TestNodeKind.Test ? id + (Node.Message is { } m ? Environment.NewLine + m : "") : Node.Error ?? Node.Label;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The node's state changed: every derived property may differ.</summary>
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => $"{new string(' ', Depth * 2)}{Label} [{Outcome}]{(DurationText.Length > 0 ? " " + DurationText : "")}";
}

/// <summary>
/// The Test Explorer's view-model: the visible rows (a flat list of the tree's expanded part, filtered), the selection and its details, the
/// summary line, and the actions of the toolbar and the context menu. Created when the tool window shows and disposed when it hides; the tree
/// itself (and every result) lives in <see cref="TestController"/>, so nothing is lost when the window closes.
/// </summary>
public sealed class TestExplorerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TestingHost _host;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<TestNode, TestRow> _rows = [];
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private string _filter = "";
    private TestRow? _selected;
    private bool _disposed;

    public TestExplorerViewModel(TestingHost host)
    {
        _host = host;
        _subs.Add(Controller.Changed.Subscribe(OnChanged));
        _subs.Add(Controller.StatusChanged.Subscribe(_ => { Notify(nameof(Status)); Notify(nameof(Summary)); }));
        _subs.Add(Controller.RunFinished.Subscribe(_ => NotifyRunState()));
        Rebuild();
    }

    public TestController Controller => _host.Controller;

    public BulkObservableCollection<TestRow> Rows { get; } = new();

    public string Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value ?? "";
            Notify();
            Rebuild();
        }
    }

    public TestRow? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            Notify();
            Notify(nameof(Details));
            Notify(nameof(HasDetails));
        }
    }

    public bool IsRunning => Controller.IsRunning;
    public bool CanRun => !Controller.IsRunning && Controller.Tree.Projects.Count > 0;
    public bool HasFailed => Controller.Tree.AllTests().Any(t => t.Outcome == TestOutcome.Failed);
    public string Status => Controller.Status;
    public bool HasFolder => _host.Workspace is null || _host.Workspace.Folders.Count > 0;
    public bool IsEmpty => Controller.Tree.Projects.Count == 0;

    /// <summary>What an empty explorer says.</summary>
    public string EmptyText => !HasFolder ? "Open a folder to discover its tests." : Controller.IsDiscovering ? "Discovering tests…"
        : "No tests found. CsCode looks for .NET test projects, cargo packages and packages that use Jest or Vitest.";

    /// <summary>"✓ 12 passed  ✗ 2 failed  ⊘ 1 skipped  ○ 3 not run · 1.2 s".</summary>
    public string Summary
    {
        get
        {
            var s = Controller.Summary();
            if (s.Total == 0) return Status;
            var parts = new List<string>();
            if (s.Passed > 0) parts.Add($"{s.Passed} passed");
            if (s.Failed > 0) parts.Add($"{s.Failed} failed");
            if (s.Skipped > 0) parts.Add($"{s.Skipped} skipped");
            if (s.NotRun > 0) parts.Add($"{s.NotRun} not run");
            var text = $"{s.Total} tests: " + string.Join(", ", parts);
            if (Controller.LastRunElapsed is { } e) text += $" · last run {TestController.Format(e)}";
            if (Status.Length > 0) text += " · " + Status;
            return text;
        }
    }

    public bool HasDetails => _selected is not null;

    /// <summary>The selected node's details: name, state and time, message, stack trace, output (what the run reported).</summary>
    public string Details
    {
        get
        {
            if (_selected?.Node is not { } n) return "";
            var sb = new StringBuilder();
            sb.Append(n.TestId ?? n.Label);
            sb.Append("  —  ").Append(n.Error is not null ? "discovery failed" : OutcomeText(n.Outcome));
            if (n.Duration is { } d) sb.Append(" in ").Append(TestController.Format(d));
            sb.AppendLine();
            if (n.Location is { } loc) sb.AppendLine($"{loc.FilePath}:{loc.Line + 1}");
            if (n.Error is { } err) sb.AppendLine().AppendLine(err);
            if (n.SkipReason is { } skip && n.Outcome is TestOutcome.None or TestOutcome.Skipped) sb.AppendLine().AppendLine("Skip: " + skip);
            if (n.Message is { Length: > 0 } msg) sb.AppendLine().AppendLine(msg);
            if (n.StackTrace is { Length: > 0 } st) sb.AppendLine().AppendLine("Stack trace:").AppendLine(st);
            if (n.Output is { Length: > 0 } output) sb.AppendLine().AppendLine("Output:").AppendLine(output);
            if (n.IsGroup && n.Kind != TestNodeKind.Case)
            {
                var tests = n.Tests().ToList();
                sb.AppendLine().AppendLine($"{tests.Count} tests: {tests.Count(t => t.Outcome == TestOutcome.Passed)} passed, {tests.Count(t => t.Outcome == TestOutcome.Failed)} failed, {tests.Count(t => t.Outcome == TestOutcome.Skipped)} skipped");
            }
            return sb.ToString().TrimEnd();
        }
    }

    internal static string OutcomeText(TestOutcome o) => o switch
    {
        TestOutcome.Passed => "passed",
        TestOutcome.Failed => "failed",
        TestOutcome.Skipped => "skipped",
        TestOutcome.Running => "running",
        TestOutcome.Queued => "queued",
        _ => "not run",
    };

    // ------------------------------------------------------------------------------------------------ rows

    private void OnChanged(TestTreeChange change)
    {
        if (_disposed) return;
        if (change.Structure || FilterActive) Rebuild();
        else foreach (var n in change.Nodes) if (_rows.TryGetValue(n, out var row)) row.Refresh();
        NotifyRunState();
    }

    private void NotifyRunState()
    {
        Notify(nameof(IsRunning));
        Notify(nameof(CanRun));
        Notify(nameof(HasFailed));
        Notify(nameof(Summary));
        Notify(nameof(IsEmpty));
        Notify(nameof(EmptyText));
        if (_selected is not null) Notify(nameof(Details));
    }

    private bool FilterActive => _filter.Trim().Length > 0;

    internal bool IsExpanded(TestNode n) =>
        FilterActive || _expanded.Contains(n.Path) || (!_collapsed.Contains(n.Path) && (n.Kind == TestNodeKind.Project || (n.Depth == 1 && n.Parent?.Children.Count == 1)));

    /// <summary>Rebuilds the visible rows (one Reset), keeping row objects (and so the selection) for nodes that are still there.</summary>
    internal void Rebuild()
    {
        if (_disposed) return;
        var filter = ParseFilter(_filter);
        var keep = _selected; // the list drops its selection on the Reset (and tells us through the binding): put it back after
        var list = new List<TestRow>();
        var live = new HashSet<TestNode>();
        foreach (var p in Controller.Tree.Projects) Add(p, list, live, filter);
        foreach (var gone in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(gone);
        Rows.ReplaceAll(list);
        foreach (var r in list) r.Refresh();
        if (keep is not null)
        {
            // the same node's row, else (a rediscovery replaced the node) its successor by path; null when it is filtered out or gone
            var path = keep.Node.Path;
            var again = live.Contains(keep.Node) ? _rows.GetValueOrDefault(keep.Node) : list.FirstOrDefault(r => r.Node.Path == path);
            if (again is not null && !list.Contains(again)) again = null;
            if (!ReferenceEquals(_selected, again)) Selected = again;
            else if (again is not null) Notify(nameof(Selected)); // the list lost it: push it back
        }
        NotifyRunState();
    }

    private void Add(TestNode node, List<TestRow> list, HashSet<TestNode> live, Func<TestNode, bool>? filter)
    {
        if (filter is not null && !Visible(node, filter)) return;
        if (!_rows.TryGetValue(node, out var row)) _rows[node] = row = new TestRow(node);
        live.Add(node);
        row.IsExpanded = IsExpanded(node);
        list.Add(row);
        if (!row.IsExpanded) return;
        foreach (var c in node.Children) Add(c, list, live, filter is not null && filter(node) && !node.IsGroup ? null : filter);
    }

    private static bool Visible(TestNode node, Func<TestNode, bool> filter) => filter(node) || node.Children.Any(c => Visible(c, filter));

    /// <summary>The filter: words matched (all of them, ignoring case) against a test's id and label; <c>@failed</c>, <c>@passed</c>, <c>@skipped</c>, <c>@notrun</c> select by state.</summary>
    internal static Func<TestNode, bool>? ParseFilter(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return null;
        var states = new HashSet<TestOutcome>();
        var terms = new List<string>();
        foreach (var w in words)
        {
            switch (w.ToLowerInvariant())
            {
                case "@failed": states.Add(TestOutcome.Failed); break;
                case "@passed": states.Add(TestOutcome.Passed); break;
                case "@skipped": states.Add(TestOutcome.Skipped); break;
                case "@notrun": states.Add(TestOutcome.None); break;
                default: terms.Add(w); break;
            }
        }
        return n =>
        {
            if (n.IsGroup) return false; // groups show when a test under them matches
            if (states.Count > 0 && !states.Contains(n.Outcome)) return false;
            var hay = (n.TestId ?? "") + " " + n.Label;
            return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        };
    }

    public void Toggle(TestRow row)
    {
        if (!row.HasChildren) return;
        if (row.IsExpanded) Collapse(row); else Expand(row);
    }

    public void Expand(TestRow row)
    {
        _collapsed.Remove(row.Node.Path);
        _expanded.Add(row.Node.Path);
        Rebuild();
    }

    public void Collapse(TestRow row)
    {
        _expanded.Remove(row.Node.Path);
        _collapsed.Add(row.Node.Path);
        Rebuild();
    }

    public void CollapseAll()
    {
        _expanded.Clear();
        foreach (var n in Controller.Tree.Projects.SelectMany(p => p.SelfAndDescendants()).Where(n => n.Children.Count > 0)) _collapsed.Add(n.Path);
        Rebuild();
    }

    public void ExpandAll()
    {
        _collapsed.Clear();
        foreach (var n in Controller.Tree.Projects.SelectMany(p => p.SelfAndDescendants()).Where(n => n.Children.Count > 0 && n.IsGroup)) _expanded.Add(n.Path);
        Rebuild();
    }

    /// <summary>Expands the node's ancestors and selects it (Reveal in Test Explorer from the editor).</summary>
    public void Reveal(TestNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) { _collapsed.Remove(p.Path); _expanded.Add(p.Path); }
        if (FilterActive) _filter = "";
        Notify(nameof(Filter));
        Rebuild();
        Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));
        RevealRequested?.Invoke(Selected);
    }

    /// <summary>Raised when a row should be scrolled into view and focused.</summary>
    public event Action<TestRow?>? RevealRequested;

    // ------------------------------------------------------------------------------------------------ actions

    public Task RunAllAsync() => _host.RunNodesAsync([.. Controller.Tree.Projects], debug: false);
    public Task DebugAllAsync() => _host.RunNodesAsync([.. Controller.Tree.Projects], debug: true);
    public Task RunFailedAsync() => _host.RunNodesAsync([.. Controller.Tree.AllTests().Where(t => t.Outcome == TestOutcome.Failed)], debug: false);

    /// <summary>Runs the selected node (or everything when nothing is selected).</summary>
    public Task RunSelectedAsync(bool debug = false) =>
        _selected is { } row ? _host.RunNodesAsync([row.Node], debug) : debug ? DebugAllAsync() : RunAllAsync();

    public Task RunAsync(TestRow row, bool debug) => _host.RunNodesAsync([row.Node], debug);

    public void Cancel() => Controller.Cancel();

    public Task RefreshAsync() => Controller.RefreshAsync();

    public void ClearResults() => Controller.ClearResults();

    /// <summary>Opens the node's source (a test at its line, a class or file at its declaration, a project its project file).</summary>
    public async Task GoToAsync(TestRow row, bool failure = false, bool preview = true)
    {
        var loc = failure ? row.Node.FailureLocation ?? row.Node.Location : row.Node.Location;
        if (row.Node.IsCase) loc = row.Node.Parent?.Location;
        if (loc is null || _host.Services.GetService<INavigationService>() is not { } nav) return;
        var pos = new TextPosition(loc.Line, 0);
        await nav.OpenAsync(new Uri(loc.FilePath), new TextRange(pos, pos), preview).ConfigureAwait(true);
    }

    /// <summary>The text Ctrl+C copies: the test's id (or the group's label).</summary>
    public string CopyText(TestRow row) => row.Node.TestId is { } id && !row.Node.IsCase ? id : row.Node.Label;

    // ------------------------------------------------------------------------------------------------ plumbing

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        Rows.Clear();
        _rows.Clear();
        RevealRequested = null;
        PropertyChanged = null;
    }
}
