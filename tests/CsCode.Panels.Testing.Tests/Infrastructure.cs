using System.Collections.Concurrent;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;

namespace CsCode.Panels.Testing.Tests;

/// <summary>Records what a provider reports.</summary>
internal sealed class RecordingSink : ITestRunSink
{
    public ConcurrentQueue<DiscoveredTest> DiscoveredTests { get; } = new();
    public ConcurrentQueue<string> StartedIds { get; } = new();
    public ConcurrentQueue<TestResult> Results { get; } = new();
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Discovered(TestProject project, DiscoveredTest test) => DiscoveredTests.Enqueue(test);
    public void Started(TestProject project, string testId) => StartedIds.Enqueue(testId);
    public void Finished(TestProject project, TestResult result) => Results.Enqueue(result);
    public void Output(string line) => Lines.Enqueue(line);

    public TestResult Result(string id, string? caseLabel = null) => Results.Single(r => r.TestId == id && r.CaseLabel == caseLabel);
}

/// <summary>A scripted process runner: answers by program and arguments, records every start.</summary>
internal sealed class ScriptedProcesses : IProcessRunner
{
    public ConcurrentQueue<ProcessSpec> Started { get; } = new();
    public Func<ProcessSpec, (int? Exit, string[] Stdout, string[] Stderr)> Answer { get; set; } = _ => (0, [], []);
    /// <summary>When set, a run waits for this before answering (cancellation tests).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<int?> RunAsync(ProcessSpec spec, Action<string> stdout, Action<string> stderr, CancellationToken ct)
    {
        Started.Enqueue(spec);
        if (Gate is { } gate)
        {
            try { await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        var (exit, outLines, errLines) = Answer(spec);
        foreach (var l in outLines) stdout(l);
        foreach (var l in errLines) stderr(l);
        return exit;
    }
}

/// <summary>A provider whose discovery and runs are scripted (controller and UI tests).</summary>
internal sealed class FakeProvider(string id = "fake") : ITestProvider
{
    public string Id => id;
    public string Label => "Fake";
    public IReadOnlyList<string> LanguageIds { get; init; } = ["csharp"];
    public List<TestProject> Projects { get; } = [];
    public Dictionary<string, List<DiscoveredTest>> Tests { get; } = [];
    public ConcurrentQueue<TestRunRequest> Runs { get; } = new();
    public ConcurrentQueue<TestRunRequest> Debugs { get; } = new();
    /// <summary>What a run reports for a test id (default: passed).</summary>
    public Func<string, TestResult> Outcome { get; set; } = tid => new TestResult(tid, TestOutcome.Passed, TimeSpan.FromMilliseconds(5));
    public TaskCompletionSource? RunGate { get; set; }
    public Func<string, IReadOnlyList<SourceTest>> Scan { get; set; } = _ => [];

    public TestProject AddProject(string name, string dir, params DiscoveredTest[] tests)
    {
        var p = new TestProject(id, name, Path.Combine(dir, name + ".proj"), dir, "fakeunit");
        Projects.Add(p);
        Tests[p.Key] = [.. tests];
        return p;
    }

    /// <summary>How many times a folder was looked at (discovery started).</summary>
    public int Detections => Volatile.Read(ref _detections);
    private int _detections;

    public Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        Interlocked.Increment(ref _detections);
        return Task.FromResult<IReadOnlyList<TestProject>>([.. Projects.Where(p => p.Directory.StartsWith(folder, StringComparison.Ordinal))]);
    }

    public Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DiscoveredTest>>(Tests[project.Key]);

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        Runs.Enqueue(request);
        if (RunGate is { } gate) await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        foreach (var t in Tests[request.Project.Key].Where(t => request.TestIds is null || request.TestIds.Contains(t.Id)))
        {
            sink.Started(request.Project, t.Id);
            sink.Finished(request.Project, Outcome(t.Id));
        }
        return new TestRunOutcome(0);
    }

    public Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        Debugs.Enqueue(request);
        return RunAsync(request, sink, ct);
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) => Scan(text);

    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) => test.Id == source.Name;

    public static DiscoveredTest Test(string id, string file, int line, string? skip = null)
    {
        var lastDot = id.LastIndexOf('.');
        var cls = id[..lastDot];
        var clsDot = cls.LastIndexOf('.');
        IReadOnlyList<TestGroup> groups = clsDot > 0
            ? [new TestGroup(cls[..clsDot], TestNodeKind.Namespace), new TestGroup(cls[(clsDot + 1)..], TestNodeKind.Class)]
            : [new TestGroup(cls, TestNodeKind.Class)];
        return new DiscoveredTest(id, id[(lastDot + 1)..], groups, new TestLocation(file, line), skip);
    }
}

/// <summary>Runs posted actions inline (the controller's owner thread is the test's thread).</summary>
internal static class Inline
{
    public static readonly Action<Action> Post = a => a();
}

/// <summary>A debug launcher that records configurations and returns no session.</summary>
internal sealed class RecordingLauncher : ITestDebugLauncher
{
    public List<DebugConfiguration> Started { get; } = [];
    public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        Started.Add(configuration);
        return ValueTask.FromResult<IDebugSession?>(null);
    }
}
