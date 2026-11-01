namespace CsCode.Testing.Engine;

/// <summary>
/// A node of the test tree: a project, a grouping level (namespace, file, module, class, describe block), a test, or one data row of a test.
/// Owned by one thread (the UI thread in the app): <see cref="TestController"/> creates and updates nodes only there.
/// </summary>
public sealed class TestNode
{
    private readonly List<TestNode> _children = [];
    private Dictionary<string, TestNode>? _byKey;

    internal TestNode(TestNode? parent, string key, string label, TestNodeKind kind, TestProject project)
    {
        Parent = parent;
        Key = key;
        Label = label;
        Kind = kind;
        Project = project;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public TestNode? Parent { get; }
    /// <summary>Unique within its parent.</summary>
    public string Key { get; }
    public string Label { get; internal set; }
    public TestNodeKind Kind { get; }
    public TestProject Project { get; }
    public int Depth { get; }
    public IReadOnlyList<TestNode> Children => _children;

    /// <summary>The provider's id of the test (tests and cases: the test's), null for groups.</summary>
    public string? TestId { get; internal set; }
    public TestLocation? Location { get; internal set; }
    public string? SkipReason { get; internal set; }

    /// <summary>This node's own state; for a group, the aggregate of its children (see <see cref="TestTree.Aggregate"/>).</summary>
    public TestOutcome Outcome { get; internal set; }
    public TimeSpan? Duration { get; internal set; }
    public string? Message { get; internal set; }
    public string? StackTrace { get; internal set; }
    public string? Output { get; internal set; }
    public TestLocation? FailureLocation { get; internal set; }
    /// <summary>Set by the last discovery of the node's project: the provider still knows the test (false: only a run reported it).</summary>
    internal bool Discovered { get; set; }
    /// <summary>A discovery error shown on a project node.</summary>
    public string? Error { get; internal set; }

    public bool IsTest => Kind is TestNodeKind.Test;
    public bool IsCase => Kind is TestNodeKind.Case;
    public bool IsGroup => Kind is not (TestNodeKind.Test or TestNodeKind.Case);

    /// <summary>A stable id across rebuilds: the keys from the project down.</summary>
    public string Path => Parent is null ? Key : Parent.Path + "\u001f" + Key;

    internal TestNode GetOrAdd(string key, string label, TestNodeKind kind)
    {
        _byKey ??= [];
        if (_byKey.TryGetValue(key, out var existing)) return existing;
        var node = new TestNode(this, key, label, kind, Project);
        _byKey[key] = node;
        _children.Add(node);
        return node;
    }

    internal TestNode? Find(string key) => _byKey?.GetValueOrDefault(key);

    internal void Remove(TestNode child)
    {
        _children.Remove(child);
        _byKey?.Remove(child.Key);
    }

    internal void ClearChildren()
    {
        _children.Clear();
        _byKey?.Clear();
    }

    internal void SortChildren() => _children.Sort((a, b) =>
    {
        // groups before tests; tests in source order when both have a line in the same file, else by name
        var ga = a.IsGroup ? 0 : 1;
        var gb = b.IsGroup ? 0 : 1;
        if (ga != gb) return ga.CompareTo(gb);
        if (a.Location is { } la && b.Location is { } lb && la.FilePath == lb.FilePath && !a.IsGroup) return la.Line.CompareTo(lb.Line);
        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>Every test node below (and including) this one.</summary>
    public IEnumerable<TestNode> Tests()
    {
        if (IsTest) { yield return this; yield break; }
        if (IsCase) yield break;
        foreach (var c in _children)
            foreach (var t in c.Tests()) yield return t;
    }

    public IEnumerable<TestNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in _children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }

    public override string ToString() => $"{Kind} {Label} [{Outcome}]";
}

/// <summary>The workspace's test projects and their trees, with indexes by test id. UI-thread owned.</summary>
public sealed class TestTree
{
    private readonly List<TestNode> _projects = [];
    private readonly Dictionary<string, Dictionary<string, TestNode>> _tests = new(StringComparer.Ordinal); // project key -> test id -> node

    public IReadOnlyList<TestNode> Projects => _projects;

    public int TestCount => _tests.Values.Sum(d => d.Count);

    public TestNode? FindProject(string projectKey) => _projects.FirstOrDefault(p => p.Project.Key == projectKey);

    public TestNode? FindTest(string projectKey, string testId) => _tests.TryGetValue(projectKey, out var d) ? d.GetValueOrDefault(testId) : null;

    /// <summary>Finds a test by id in any project.</summary>
    public TestNode? FindTest(string testId)
    {
        foreach (var d in _tests.Values) if (d.TryGetValue(testId, out var n)) return n;
        return null;
    }

    public IEnumerable<TestNode> AllTests() => _tests.Values.SelectMany(d => d.Values);

    /// <summary>Adds (or returns) a project's root node.</summary>
    public TestNode AddProject(TestProject project)
    {
        if (FindProject(project.Key) is { } existing) return existing;
        var node = new TestNode(null, project.Key, project.Name, TestNodeKind.Project, project) { Location = new TestLocation(project.FilePath, 0) };
        _projects.Add(node);
        _projects.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        _tests[project.Key] = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        return node;
    }

    /// <summary>Removes the projects not in <paramref name="keep"/>.</summary>
    public void RetainProjects(IReadOnlySet<string> keep)
    {
        foreach (var p in _projects.Where(p => !keep.Contains(p.Project.Key)).ToList())
        {
            _projects.Remove(p);
            _tests.Remove(p.Project.Key);
        }
    }

    /// <summary>
    /// Replaces a project's tests with a discovery's, keeping the state and results of tests that are still there (by id) and dropping the
    /// tests that are gone; tests only a run reported are dropped too (the next run adds them back).
    /// </summary>
    public void ReplaceTests(TestProject project, IReadOnlyList<DiscoveredTest> tests)
    {
        var root = AddProject(project);
        var old = _tests[project.Key];
        var keep = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        root.ClearChildren();
        root.Error = null;
        foreach (var t in tests)
        {
            var node = Place(root, t);
            node.Discovered = true;
            if (old.TryGetValue(t.Id, out var prev) && !ReferenceEquals(prev, node)) CopyState(prev, node);
            keep[t.Id] = node;
        }
        _tests[project.Key] = keep;
        SortRecursive(root);
        Aggregate(root);
    }

    /// <summary>Adds a test a run reported that discovery did not know (no-op when it is known).</summary>
    public TestNode AddTest(TestProject project, DiscoveredTest test)
    {
        var root = AddProject(project);
        var index = _tests[project.Key];
        if (index.TryGetValue(test.Id, out var existing))
        {
            existing.Location ??= test.Location;
            return existing;
        }
        var node = Place(root, test);
        index[test.Id] = node;
        node.Parent?.SortChildren();
        return node;
    }

    private static TestNode Place(TestNode root, DiscoveredTest t)
    {
        var parent = root;
        foreach (var g in t.Groups)
        {
            var group = parent.GetOrAdd(g.Kind + ":" + g.Label, g.Label, g.Kind);
            group.Location ??= g.Location;
            parent = group;
        }
        var node = parent.GetOrAdd("t:" + t.Id, t.Label, TestNodeKind.Test);
        node.TestId = t.Id;
        node.Location = t.Location ?? node.Location;
        node.SkipReason = t.SkipReason;
        return node;
    }

    private static void CopyState(TestNode from, TestNode to)
    {
        to.Outcome = from.Outcome is TestOutcome.Running or TestOutcome.Queued ? TestOutcome.None : from.Outcome;
        to.Duration = from.Duration;
        to.Message = from.Message;
        to.StackTrace = from.StackTrace;
        to.Output = from.Output;
        to.FailureLocation = from.FailureLocation;
        foreach (var c in from.Children.Where(c => c.IsCase))
        {
            var copy = to.GetOrAdd(c.Key, c.Label, TestNodeKind.Case);
            copy.TestId = c.TestId;
            CopyState(c, copy);
        }
    }

    private static void SortRecursive(TestNode node)
    {
        node.SortChildren();
        foreach (var c in node.Children) if (c.IsGroup) SortRecursive(c);
    }

    /// <summary>
    /// Applies a result to a test (or to its case when <see cref="TestResult.CaseLabel"/> is set; the test then shows the worst of its cases).
    /// Returns the nodes whose state changed, ancestors included.
    /// </summary>
    public TestNode? Apply(TestNode test, TestResult r)
    {
        var target = test;
        if (r.CaseLabel is { } label)
        {
            target = test.GetOrAdd("c:" + label, label, TestNodeKind.Case);
            target.TestId = test.TestId;
        }
        target.Outcome = r.Outcome;
        target.Duration = r.Duration;
        target.Message = r.Message;
        target.StackTrace = r.StackTrace;
        target.Output = r.Output;
        target.FailureLocation = r.FailureLocation;
        if (!ReferenceEquals(target, test))
        {
            var cases = test.Children.Where(c => c.IsCase).ToList();
            test.Outcome = cases.Max(c => c.Outcome);
            test.Duration = cases.Aggregate(TimeSpan.Zero, (acc, c) => acc + (c.Duration ?? TimeSpan.Zero));
            var failed = cases.FirstOrDefault(c => c.Outcome == TestOutcome.Failed);
            test.Message = failed is null ? null : $"{cases.Count(c => c.Outcome == TestOutcome.Failed)} of {cases.Count} cases failed. First: {failed.Label}: {failed.Message}";
            test.FailureLocation = failed?.FailureLocation;
        }
        return target;
    }

    /// <summary>Recomputes the outcome and duration of every group under (and including) <paramref name="node"/> from its tests.</summary>
    public static void Aggregate(TestNode node)
    {
        if (!node.IsGroup) return;
        foreach (var c in node.Children) Aggregate(c);
        AggregateShallow(node);
    }

    /// <summary>Recomputes the groups above <paramref name="node"/> (after one of its results changed).</summary>
    public static void AggregateAncestors(TestNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) AggregateShallow(p);
    }

    internal static void AggregateShallow(TestNode node)
    {
        var outcome = TestOutcome.None;
        TimeSpan? duration = null;
        var any = false; var allSkipped = true; var anyNone = false;
        foreach (var c in node.Children)
        {
            any = true;
            if (c.Outcome is TestOutcome.Failed or TestOutcome.Running or TestOutcome.Queued) outcome = (TestOutcome)Math.Max((int)outcome, (int)c.Outcome);
            if (c.Outcome != TestOutcome.Skipped) allSkipped = false;
            if (c.Outcome == TestOutcome.None) anyNone = true;
            if (c.Duration is { } d) duration = (duration ?? TimeSpan.Zero) + d;
        }
        if (outcome == TestOutcome.None && any) outcome = allSkipped ? TestOutcome.Skipped : anyNone ? TestOutcome.None : TestOutcome.Passed;
        node.Outcome = outcome;
        node.Duration = duration;
    }

    /// <summary>Clears every result (Clear All Results).</summary>
    public void ClearResults()
    {
        foreach (var p in _projects)
            foreach (var n in p.SelfAndDescendants().ToList())
            {
                foreach (var c in n.Children.Where(c => c.IsCase).ToList()) n.Remove(c);
                n.Outcome = TestOutcome.None;
                n.Duration = null;
                n.Message = n.StackTrace = n.Output = null;
                n.FailureLocation = null;
            }
    }
}
