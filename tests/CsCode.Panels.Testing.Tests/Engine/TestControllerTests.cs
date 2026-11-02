using CsCode.Testing.Engine;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Engine;

public sealed class TestControllerTests
{
    private static (TestController Controller, FakeProvider Provider, TestProject Project) Create(List<string>? output = null)
    {
        var provider = new FakeProvider();
        var project = provider.AddProject("Calc.Tests", "/w/calc",
            FakeProvider.Test("Calc.MathTests.Adds", "/w/calc/MathTests.cs", 5),
            FakeProvider.Test("Calc.MathTests.Subtracts", "/w/calc/MathTests.cs", 9),
            FakeProvider.Test("Calc.Other.Later", "/w/calc/Other.cs", 3, skip: "later"));
        var controller = new TestController([provider], Inline.Post, line => output?.Add(line));
        return (controller, provider, project);
    }

    [Fact]
    public async Task Discovery_builds_project_namespace_class_test_groups()
    {
        var (c, _, _) = Create();
        await c.SetFoldersAsync(["/w"]);
        var project = Assert.Single(c.Tree.Projects);
        Assert.Equal("Calc.Tests", project.Label);
        var ns = Assert.Single(project.Children);
        Assert.Equal((TestNodeKind.Namespace, "Calc"), (ns.Kind, ns.Label));
        Assert.Equal(["MathTests", "Other"], ns.Children.Select(n => n.Label));
        Assert.Equal(["Adds", "Subtracts"], ns.Children[0].Children.Select(n => n.Label)); // source order
        Assert.Equal(3, c.Tree.TestCount);
        Assert.False(c.IsDiscovering);
    }

    [Fact]
    public async Task Run_all_reports_each_result_and_aggregates_groups()
    {
        var output = new List<string>();
        var (c, p, _) = Create(output);
        await c.SetFoldersAsync(["/w"]);
        p.Outcome = id => id.EndsWith("Subtracts", StringComparison.Ordinal)
            ? new TestResult(id, TestOutcome.Failed, TimeSpan.FromMilliseconds(3), "boom", "at X", FailureLocation: new TestLocation("/w/calc/MathTests.cs", 10))
            : id.EndsWith("Later", StringComparison.Ordinal) ? new TestResult(id, TestOutcome.Skipped) : new TestResult(id, TestOutcome.Passed, TimeSpan.FromMilliseconds(2));
        var finished = new List<bool>();
        using var sub = c.RunFinished.Subscribe(finished.Add);
        Assert.True(await c.RunAllAsync());

        var run = Assert.Single(p.Runs);
        Assert.Null(run.TestIds); // every test of the project: no filter
        Assert.Equal(TestOutcome.Passed, c.Tree.FindTest("Calc.MathTests.Adds")!.Outcome);
        var failed = c.Tree.FindTest("Calc.MathTests.Subtracts")!;
        Assert.Equal((TestOutcome.Failed, "boom"), (failed.Outcome, failed.Message));
        Assert.Equal(TestOutcome.Failed, failed.Parent!.Outcome); // the class
        Assert.Equal(TestOutcome.Failed, c.Tree.Projects[0].Outcome);
        Assert.Equal(TestOutcome.Skipped, c.Tree.FindTest("Calc.Other.Later")!.Parent!.Outcome); // a class whose only test was skipped
        Assert.Equal(TimeSpan.FromMilliseconds(5), failed.Parent.Duration);
        var s = c.Summary();
        Assert.Equal((3, 1, 1, 1, 0), (s.Total, s.Passed, s.Failed, s.Skipped, s.NotRun));
        Assert.False(c.IsRunning);
        Assert.Equal([false], finished);
        Assert.Contains(output, l => l.Contains("Finished", StringComparison.Ordinal) && l.Contains("1 passed, 1 failed, 1 skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_a_class_sends_its_tests_and_rerun_failed_sends_only_failures()
    {
        var (c, p, _) = Create();
        await c.SetFoldersAsync(["/w"]);
        p.Outcome = id => new TestResult(id, id.EndsWith("Adds", StringComparison.Ordinal) ? TestOutcome.Failed : TestOutcome.Passed);
        var cls = c.Tree.FindTest("Calc.MathTests.Adds")!.Parent!;
        await c.RunAsync([cls]);
        Assert.Equal(["Calc.MathTests.Adds", "Calc.MathTests.Subtracts"], p.Runs.Last().TestIds);
        Assert.Equal(TestOutcome.None, c.Tree.FindTest("Calc.Other.Later")!.Outcome); // not part of the run

        await c.RunFailedAsync();
        Assert.Equal(["Calc.MathTests.Adds"], p.Runs.Last().TestIds);

        p.Outcome = id => new TestResult(id, TestOutcome.Passed);
        await c.RunLastAsync();
        Assert.Equal(["Calc.MathTests.Adds"], p.Runs.Last().TestIds);
        Assert.Equal(TestOutcome.Passed, c.Tree.FindTest("Calc.MathTests.Adds")!.Outcome);
    }

    [Fact]
    public async Task Cases_of_a_test_show_under_it_and_the_test_shows_the_worst()
    {
        var (c, p, _) = Create();
        await c.SetFoldersAsync(["/w"]);
        var calls = 0;
        p.Outcome = id => id.EndsWith("Adds", StringComparison.Ordinal)
            ? new TestResult(id, ++calls == 1 ? TestOutcome.Passed : TestOutcome.Failed, TimeSpan.FromMilliseconds(1), CaseLabel: calls == 1 ? "Adds(1)" : "Adds(2)")
            : new TestResult(id, TestOutcome.Passed);
        // the fake reports once per test: report a second case by running twice into the same tree without the reset
        var adds = c.Tree.FindTest("Calc.MathTests.Adds")!;
        c.Tree.Apply(adds, p.Outcome(adds.TestId!));
        c.Tree.Apply(adds, p.Outcome(adds.TestId!));
        Assert.Equal(["Adds(1)", "Adds(2)"], adds.Children.Select(x => x.Label));
        Assert.True(adds.Children.All(x => x.IsCase));
        Assert.Equal(TestOutcome.Failed, adds.Outcome);
        Assert.Contains("1 of 2 cases failed", adds.Message);
        // a new run starts the test afresh: its cases go
        await c.RunAsync([adds]);
        Assert.Single(adds.Children);
    }

    [Fact]
    public async Task Cancel_returns_the_tests_that_did_not_finish_to_not_run()
    {
        var (c, p, _) = Create();
        await c.SetFoldersAsync(["/w"]);
        p.RunGate = new TaskCompletionSource();
        var run = c.RunAllAsync();
        Assert.True(c.IsRunning);
        // the tests are queued, then running
        Assert.All(c.Tree.AllTests(), t => Assert.True(t.Outcome is TestOutcome.Queued or TestOutcome.Running));
        Assert.False(await c.RunAllAsync()); // one run at a time
        c.Cancel();
        await run;
        Assert.False(c.IsRunning);
        Assert.All(c.Tree.AllTests(), t => Assert.Equal(TestOutcome.None, t.Outcome));
    }

    [Fact]
    public async Task Rediscovery_keeps_results_of_tests_that_remain_and_drops_removed_ones()
    {
        var (c, p, project) = Create();
        await c.SetFoldersAsync(["/w"]);
        await c.RunAllAsync();
        Assert.Equal(TestOutcome.Passed, c.Tree.FindTest("Calc.MathTests.Adds")!.Outcome);
        p.Tests[project.Key].RemoveAll(t => t.Id == "Calc.MathTests.Subtracts");
        p.Tests[project.Key].Add(FakeProvider.Test("Calc.MathTests.Multiplies", "/w/calc/MathTests.cs", 12));
        await c.RediscoverAsync(project);
        Assert.Equal(TestOutcome.Passed, c.Tree.FindTest("Calc.MathTests.Adds")!.Outcome);
        Assert.Null(c.Tree.FindTest("Calc.MathTests.Subtracts"));
        Assert.Equal(TestOutcome.None, c.Tree.FindTest("Calc.MathTests.Multiplies")!.Outcome);
        Assert.Equal(TestOutcome.None, c.Tree.Projects[0].Outcome); // a test that never ran
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task A_provider_whose_projects_changed_is_detected_again_alone_and_after_a_run()
    {
        var calc = new FakeProvider();
        var calcProject = calc.AddProject("Calc.Tests", "/w/calc", FakeProvider.Test("Calc.MathTests.Adds", "/w/calc/MathTests.cs", 5));
        var native = new FakeProvider("native");
        var first = native.AddProject("first_tests", "/w/native", FakeProvider.Test("first.Tests.A", "/w/native/a.cpp", 1));
        using var c = new TestController([calc, native], Inline.Post);
        native.Changed.Fire(true);
        Assert.Equal(0, native.Detections); // before the first discovery: nothing to update
        await c.SetFoldersAsync(["/w"]);
        var calcDetections = calc.Detections;

        // a build system's target went and another came: only this provider's projects change
        native.Projects.Clear();
        var second = native.AddProject("second_tests", "/w/native", FakeProvider.Test("second.Tests.B", "/w/native/b.cpp", 2));
        native.Changed.Fire(true);
        await WaitUntil(() => c.Tree.FindTest("second.Tests.B") is not null, "the new project's tests");
        Assert.Null(c.Tree.FindProject(first.Key));
        Assert.Equal(TestOutcome.None, c.Tree.FindTest("Calc.MathTests.Adds")!.Outcome);
        Assert.NotNull(c.Tree.FindProject(calcProject.Key));
        Assert.Equal(calcDetections, calc.Detections);

        // while a run is going the tree waits: the run's results land on the nodes it has, then the provider is detected again
        calc.RunGate = new TaskCompletionSource();
        var run = c.RunAllAsync();
        native.Tests[second.Key].Add(FakeProvider.Test("second.Tests.C", "/w/native/b.cpp", 5));
        native.Changed.Fire(true);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(c.Tree.FindTest("second.Tests.C"));
        calc.RunGate.SetResult();
        Assert.True(await run);
        await WaitUntil(() => c.Tree.FindTest("second.Tests.C") is not null, "the test added during the run");
        Assert.Equal(TestOutcome.Passed, c.Tree.FindTest("second.Tests.B")!.Outcome);
        Assert.Equal(TestOutcome.Passed, c.Tree.FindTest("Calc.MathTests.Adds")!.Outcome);
    }

    [Fact]
    public async Task A_result_for_an_unknown_test_adds_it()
    {
        var (c, p, project) = Create();
        await c.SetFoldersAsync(["/w"]);
        // a provider reports a test discovery did not know (inherited from a base class)
        p.Tests[project.Key].Add(new DiscoveredTest("Calc.Base.Inherited", "Inherited", [new TestGroup("Calc", TestNodeKind.Namespace), new TestGroup("Base", TestNodeKind.Class)]));
        var changes = new List<TestTreeChange>();
        using var sub = c.Changed.Subscribe(changes.Add);
        await c.RunAsync([c.Tree.Projects[0]]);
        Assert.NotNull(c.Tree.FindTest("Calc.Base.Inherited"));
        Assert.Contains(changes, ch => ch.Structure);
    }

    [Fact]
    public async Task Debugging_needs_a_launcher_and_runs_one_project()
    {
        var (c, p, _) = Create();
        var second = p.AddProject("Other.Tests", "/w/other", FakeProvider.Test("O.T.X", "/w/other/T.cs", 1));
        await c.SetFoldersAsync(["/w"]);
        var errors = new List<string>();
        using var sub = c.Errors.Subscribe(errors.Add);
        Assert.False(await c.RunAllAsync(debug: true));
        Assert.Contains(errors, e => e.Contains("debugger", StringComparison.Ordinal));
        c.Debugger = new RecordingLauncher();
        Assert.True(await c.RunAllAsync(debug: true));
        Assert.Single(p.Debugs);
        Assert.Contains(errors, e => e.Contains("one project at a time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Results_reach_the_owner_thread_in_batches()
    {
        var provider = new FakeProvider();
        var tests = Enumerable.Range(0, 500).Select(i => FakeProvider.Test($"N.C.T{i:000}", "/w/a/C.cs", i)).ToArray();
        provider.AddProject("Big", "/w/a", tests);
        var posted = new Queue<Action>();
        var c = new TestController([provider], posted.Enqueue);
        void Pump() { while (posted.Count > 0) posted.Dequeue()(); }
        var discovery = c.SetFoldersAsync(["/w"]);
        while (!discovery.IsCompleted) { Pump(); await Task.Delay(5, TestContext.Current.CancellationToken); }
        Pump();
        Assert.Equal(500, c.Tree.TestCount);
        var changes = 0;
        using var sub = c.Changed.Subscribe(_ => changes++);
        var run = c.RunAllAsync();
        while (!run.IsCompleted || posted.Count > 0) { Pump(); await Task.Delay(1, TestContext.Current.CancellationToken); }
        Pump();
        Assert.All(c.Tree.AllTests(), t => Assert.Equal(TestOutcome.Passed, t.Outcome));
        Assert.True(changes < 100, $"{changes} change notifications for 1000 events: the flush must batch them");
    }
}
