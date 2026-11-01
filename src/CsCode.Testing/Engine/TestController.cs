using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;

namespace CsCode.Testing.Engine;

/// <summary>What changed in the test tree: its shape (projects, tests added or removed), states (the nodes listed), or the run status.</summary>
public sealed record TestTreeChange(bool Structure, IReadOnlyCollection<TestNode> Nodes);

/// <summary>Counts of the tests' states, for the explorer's summary line.</summary>
public sealed record TestSummary(int Total, int Passed, int Failed, int Skipped, int NotRun, TimeSpan Duration);

/// <summary>
/// Owns the test tree and drives the providers: discovery of the workspace's folders, runs and debug runs (one at a time), cancellation, and the
/// re-runs (failed tests, the last run). Public members are called on the owner thread (the UI thread in the app) and every change to the tree is
/// made there: the providers work on the thread pool and report through a sink that queues events and posts one flush at a time to the owner
/// thread (<c>post</c>), so a run that reports thousands of results costs the owner a few batches. Tool output goes straight to the output
/// callback (the output channel is thread-safe and bounded).
/// </summary>
public sealed class TestController : IDisposable
{
    private readonly IReadOnlyList<ITestProvider> _providers;
    private readonly Action<Action> _post;
    private readonly Action<string> _output;
    private readonly Func<string, bool> _providerEnabled;
    private readonly Emitter<TestTreeChange> _changed = new();
    private readonly Emitter<string> _status = new();
    private readonly ConcurrentQueue<Action<HashSet<TestNode>>> _queue = new();
    private int _flushPosted;
    private CancellationTokenSource? _discovery;
    private CancellationTokenSource? _run;
    private IReadOnlyList<string> _folders = [];
    private List<(string ProjectKey, IReadOnlyList<string>? Ids)>? _lastRun;
    private bool _lastRunDebug;
    private bool _disposed;

    public TestController(IReadOnlyList<ITestProvider> providers, Action<Action> post, Action<string>? output = null, Func<string, bool>? providerEnabled = null)
    {
        _providers = providers;
        _post = post;
        _output = output ?? (_ => { });
        _providerEnabled = providerEnabled ?? (_ => true);
    }

    public TestTree Tree { get; } = new();
    public IReadOnlyList<ITestProvider> Providers => _providers;
    public IEventSource<TestTreeChange> Changed => _changed;
    /// <summary>A short status ("Discovering tests…", "Building rs_sample…", "") for the summary line.</summary>
    public IEventSource<string> StatusChanged => _status;
    public string Status { get; private set; } = "";
    public bool IsDiscovering => _discovery is not null;
    public bool IsRunning => _run is not null;
    public bool IsDebugging { get; private set; }
    public bool HasLastRun => _lastRun is not null;
    public DateTimeOffset? LastRunFinished { get; private set; }
    public TimeSpan? LastRunElapsed { get; private set; }
    /// <summary>Set by the host: starts debug sessions (null: debugging is unavailable).</summary>
    public ITestDebugLauncher? Debugger { get; set; }

    /// <summary>Raised on the owner thread when a run ends (true: it was cancelled).</summary>
    public IEventSource<bool> RunFinished => _runFinished;
    private readonly Emitter<bool> _runFinished = new();

    /// <summary>Messages for the user (a run could not start, a build failed): the host shows them.</summary>
    public IEventSource<string> Errors => _errors;
    private readonly Emitter<string> _errors = new();

    public ITestProvider? ProviderFor(TestProject project) => _providers.FirstOrDefault(p => p.Id == project.ProviderId);

    // ------------------------------------------------------------------------------------------------ discovery

    /// <summary>Sets the workspace's (local) folders and discovers their tests again.</summary>
    public Task SetFoldersAsync(IReadOnlyList<string> folders)
    {
        _folders = folders;
        return RefreshAsync();
    }

    /// <summary>Detects the projects of every folder and discovers their tests; projects that are gone leave the tree. Cancels a previous discovery.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        _discovery?.Cancel();
        var cts = _discovery = new CancellationTokenSource();
        SetStatus("Discovering tests…");
        var folders = _folders;
        var providers = _providers.Where(p => _providerEnabled(p.Id)).ToList();
        try
        {
            var projects = await Task.Run(async () =>
            {
                var all = new List<TestProject>();
                foreach (var folder in folders)
                    foreach (var p in providers)
                    {
                        try { all.AddRange(await p.DetectAsync(folder, cts.Token).ConfigureAwait(false)); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { _output($"[{p.Label}] Could not look for test projects in {folder}: {ex.Message}"); }
                    }
                return all;
            }, cts.Token).ConfigureAwait(false);
            Enqueue(changed =>
            {
                Tree.RetainProjects(projects.Select(p => p.Key).ToHashSet(StringComparer.Ordinal));
                foreach (var p in projects) changed.Add(Tree.AddProject(p));
                _structureChanged = true;
            });
            var sink = new Sink(this);
            await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token }, async (project, token) =>
            {
                var provider = ProviderFor(project)!;
                try
                {
                    var tests = await provider.DiscoverAsync(project, sink, token).ConfigureAwait(false);
                    Enqueue(changed =>
                    {
                        Tree.ReplaceTests(project, tests);
                        changed.Add(Tree.FindProject(project.Key)!);
                        _structureChanged = true;
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _output($"[{provider.Label}] Discovery of {project.Name} failed: {ex.Message}");
                    Enqueue(changed =>
                    {
                        if (Tree.FindProject(project.Key) is { } node) { node.Error = ex.Message; changed.Add(node); }
                    });
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Enqueue(_ =>
            {
                if (ReferenceEquals(_discovery, cts)) { _discovery = null; SetStatus(""); }
                cts.Dispose();
            });
        }
    }

    /// <summary>
    /// Discovers one project's tests again (a source file of it was saved). Skipped while a run or a full discovery is going (the run's results
    /// would land on nodes the new tree replaces).
    /// </summary>
    public async Task RediscoverAsync(TestProject project)
    {
        if (_disposed || _run is not null || _discovery is not null || ProviderFor(project) is not { } provider) return;
        try
        {
            var tests = await Task.Run(() => provider.DiscoverAsync(project, new Sink(this), CancellationToken.None)).ConfigureAwait(false);
            Enqueue(changed =>
            {
                if (_run is not null || Tree.FindProject(project.Key) is null) return;
                Tree.ReplaceTests(project, tests);
                changed.Add(Tree.FindProject(project.Key)!);
                _structureChanged = true;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _output($"[{provider.Label}] Discovery of {project.Name} failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------------------------------------ runs

    /// <summary>Runs every test of every project.</summary>
    public Task<bool> RunAllAsync(bool debug = false) => RunAsync(Tree.Projects, debug);

    /// <summary>Runs the tests that failed last.</summary>
    public Task<bool> RunFailedAsync(bool debug = false) => RunAsync([.. Tree.AllTests().Where(t => t.Outcome == TestOutcome.Failed)], debug);

    /// <summary>Repeats the last run (same tests, run or debug as asked).</summary>
    public Task<bool> RunLastAsync(bool? debug = null)
    {
        if (_lastRun is null) return Task.FromResult(false);
        var requests = _lastRun.Select(r => Tree.FindProject(r.ProjectKey) is { } p ? (p, r.Ids) : default).Where(x => x.p is not null).ToList();
        return StartAsync(requests!, debug ?? _lastRunDebug);
    }

    /// <summary>
    /// Runs the tests under <paramref name="nodes"/> (a project, a group, tests, cases: a case runs its test). Returns false when nothing was
    /// started (a run is already going, nothing to run).
    /// </summary>
    public Task<bool> RunAsync(IEnumerable<TestNode> nodes, bool debug = false)
    {
        var byProject = new Dictionary<string, (TestNode Root, HashSet<string>? Ids)>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var root = Tree.FindProject(node.Project.Key);
            if (root is null) continue;
            if (node.Kind == TestNodeKind.Project) { byProject[root.Project.Key] = (root, null); continue; }
            if (byProject.TryGetValue(root.Project.Key, out var existing) && existing.Ids is null) continue;
            var ids = existing.Ids ?? new HashSet<string>(StringComparer.Ordinal);
            if (node.IsCase && node.TestId is { } caseOf) ids.Add(caseOf);
            else foreach (var t in node.Tests()) if (t.TestId is { } id) ids.Add(id);
            byProject[root.Project.Key] = (root, ids);
        }
        var requests = new List<(TestNode Root, IReadOnlyList<string>? Ids)>();
        foreach (var (root, ids) in byProject.Values)
        {
            if (ids is not null && ids.Count == 0) continue;
            // every test of the project: run it whole (no filter on the command line)
            var all = ids is not null && root.Tests().All(t => t.TestId is { } id && ids.Contains(id));
            requests.Add((root, ids is null || all ? null : ids.Order(StringComparer.Ordinal).ToList()));
        }
        return StartAsync(requests, debug);
    }

    private async Task<bool> StartAsync(List<(TestNode Root, IReadOnlyList<string>? Ids)> requests, bool debug)
    {
        if (_disposed || requests.Count == 0) return false;
        if (_run is not null)
        {
            _errors.Fire("A test run is already in progress. Cancel it first.");
            return false;
        }
        if (debug && Debugger is null)
        {
            _errors.Fire("Debugging tests needs the debugger, which is not available.");
            return false;
        }
        if (debug && requests.Count > 1)
        {
            _errors.Fire($"Debugging runs the tests of one project at a time: debugging {requests[0].Root.Label} only.");
            requests = [requests[0]];
        }
        var cts = _run = new CancellationTokenSource();
        IsDebugging = debug;
        _lastRun = [.. requests.Select(r => (r.Root.Project.Key, r.Ids))];
        _lastRunDebug = debug;
        var changed = new HashSet<TestNode>();
        foreach (var (root, ids) in requests)
            foreach (var t in Selected(root, ids))
            {
                foreach (var c in t.Children.Where(c => c.IsCase).ToList()) t.Remove(c);
                t.Outcome = TestOutcome.Queued;
                t.Message = t.StackTrace = t.Output = null;
                t.FailureLocation = null;
                t.Duration = null;
                changed.Add(t);
            }
        foreach (var (root, _) in requests) { TestTree.Aggregate(root); changed.Add(root); }
        _changed.Fire(new TestTreeChange(true, changed));
        _output($"========== {(debug ? "Debugging" : "Running")} tests: {string.Join(", ", requests.Select(r => r.Root.Label + (r.Ids is null ? "" : $" ({r.Ids.Count} selected)")))} ==========");
        var started = DateTimeOffset.Now;
        var sink = new Sink(this);
        var cancelled = false;
        try
        {
            await Task.Run(async () =>
            {
                foreach (var (root, ids) in requests)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var project = root.Project;
                    var provider = ProviderFor(project)!;
                    Enqueue(set =>
                    {
                        foreach (var t in Selected(root, ids)) { t.Outcome = TestOutcome.Running; set.Add(t); }
                        TestTree.Aggregate(root);
                        set.Add(root);
                    });
                    TestRunOutcome outcome;
                    try
                    {
                        var request = new TestRunRequest(project, ids);
                        outcome = debug ? await provider.DebugAsync(request, sink, Debugger!, cts.Token).ConfigureAwait(false)
                                        : await provider.RunAsync(request, sink, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { outcome = new TestRunOutcome(-1, $"{provider.Label}: {ex.Message}"); }
                    if (outcome.Error is { } error)
                    {
                        _output(error);
                        _post(() => _errors.Fire(error));
                    }
                    if (outcome.ExitCode is null) cts.Token.ThrowIfCancellationRequested();
                    // tests the run did not report: back to "not run"
                    Enqueue(set => Settle(root, ids, set, outcome.Error));
                }
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            var elapsed = DateTimeOffset.Now - started;
            Enqueue(set =>
            {
                foreach (var (root, ids) in requests) Settle(root, ids, set, null);
                _run = null;
                IsDebugging = false;
                LastRunFinished = DateTimeOffset.Now;
                LastRunElapsed = elapsed;
                cts.Dispose();
                var s = Summary();
                _output($"========== {(cancelled ? "Cancelled" : "Finished")} in {Format(elapsed)}: {s.Passed} passed, {s.Failed} failed, {s.Skipped} skipped ==========");
                SetStatus("");
                _finishedPending = cancelled;
            });
        }
        return true;
    }

    private bool? _finishedPending;
    private bool _structureChanged;

    private static IEnumerable<TestNode> Selected(TestNode root, IReadOnlyList<string>? ids)
    {
        if (ids is null) return root.Tests();
        var set = ids as ICollection<string> ?? ids.ToHashSet(StringComparer.Ordinal);
        var lookup = set.Count > 8 ? set.ToHashSet(StringComparer.Ordinal) : null;
        return root.Tests().Where(t => t.TestId is { } id && (lookup?.Contains(id) ?? set.Contains(id)));
    }

    private static void Settle(TestNode root, IReadOnlyList<string>? ids, HashSet<TestNode> changed, string? error)
    {
        var any = false;
        foreach (var t in Selected(root, ids))
        {
            if (t.Outcome is not (TestOutcome.Running or TestOutcome.Queued)) continue;
            t.Outcome = TestOutcome.None;
            if (error is not null) t.Message = error;
            changed.Add(t);
            any = true;
        }
        if (any) { TestTree.Aggregate(root); changed.Add(root); }
    }

    /// <summary>Cancels the run (and a discovery): the tool processes are killed, a debug session is stopped.</summary>
    public void Cancel()
    {
        _run?.Cancel();
        _discovery?.Cancel();
    }

    /// <summary>Forgets every result.</summary>
    public void ClearResults()
    {
        if (_run is not null) return;
        Tree.ClearResults();
        foreach (var p in Tree.Projects) TestTree.Aggregate(p);
        _changed.Fire(new TestTreeChange(true, [.. Tree.Projects]));
    }

    public TestSummary Summary()
    {
        int total = 0, passed = 0, failed = 0, skipped = 0, none = 0;
        var duration = TimeSpan.Zero;
        foreach (var t in Tree.AllTests())
        {
            total++;
            switch (t.Outcome)
            {
                case TestOutcome.Passed: passed++; break;
                case TestOutcome.Failed: failed++; break;
                case TestOutcome.Skipped: skipped++; break;
                default: none++; break;
            }
            if (t.Duration is { } d) duration += d;
        }
        return new TestSummary(total, passed, failed, skipped, none, duration);
    }

    public static string Format(TimeSpan d) =>
        d.TotalMilliseconds < 1 ? "< 1 ms" : d.TotalSeconds < 1 ? $"{d.TotalMilliseconds:0} ms" : d.TotalMinutes < 1 ? $"{d.TotalSeconds:0.0} s" : $"{(int)d.TotalMinutes} min {d.Seconds} s";

    // ------------------------------------------------------------------------------------------------ the sink and the owner-thread flush

    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        _status.Fire(text);
    }

    private void Enqueue(Action<HashSet<TestNode>> apply)
    {
        _queue.Enqueue(apply);
        if (Interlocked.Exchange(ref _flushPosted, 1) == 0) _post(Flush);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushPosted, 0);
        if (_disposed) return;
        var changed = new HashSet<TestNode>();
        while (_queue.TryDequeue(out var apply)) apply(changed);
        if (changed.Count == 0 && !_structureChanged && _finishedPending is null) return;
        // the groups above every changed node show the new aggregate: each recomputed once, deepest first
        var parents = new HashSet<TestNode>();
        foreach (var n in changed) for (var p = n.Parent; p is not null && parents.Add(p); p = p.Parent) { }
        foreach (var p in parents.OrderByDescending(p => p.Depth)) { TestTree.AggregateShallow(p); changed.Add(p); }
        var structure = _structureChanged;
        _structureChanged = false;
        _changed.Fire(new TestTreeChange(structure, changed));
        if (_finishedPending is { } wasCancelled)
        {
            _finishedPending = null;
            _runFinished.Fire(wasCancelled);
        }
    }

    private sealed class Sink(TestController owner) : ITestRunSink
    {
        public void Discovered(TestProject project, DiscoveredTest test) => owner.Enqueue(changed =>
        {
            if (owner.Tree.FindTest(project.Key, test.Id) is null)
            {
                changed.Add(owner.Tree.AddTest(project, test));
                owner._structureChanged = true;
            }
        });

        public void Started(TestProject project, string testId) => owner.Enqueue(changed =>
        {
            if (owner.Tree.FindTest(project.Key, testId) is { } node && node.Outcome != TestOutcome.Running)
            {
                node.Outcome = TestOutcome.Running;
                changed.Add(node);
            }
        });

        public void Finished(TestProject project, TestResult result) => owner.Enqueue(changed =>
        {
            var node = owner.Tree.FindTest(project.Key, result.TestId);
            if (node is null)
            {
                // a result for a test nobody announced: add it under the project
                node = owner.Tree.AddTest(project, new DiscoveredTest(result.TestId, result.TestId, []));
                owner._structureChanged = true;
            }
            var before = node.Children.Count;
            var target = owner.Tree.Apply(node, result);
            if (node.Children.Count != before) owner._structureChanged = true; // a theory's first case row
            changed.Add(node);
            if (target is not null) changed.Add(target);
        });

        public void Output(string line) => owner._output(line);

        public void Status(string text) => owner.Enqueue(_ => owner.SetStatus(text));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _run?.Cancel();
        _discovery?.Cancel();
        _changed.Clear();
        _status.Clear();
        _errors.Clear();
        _runFinished.Clear();
    }
}
