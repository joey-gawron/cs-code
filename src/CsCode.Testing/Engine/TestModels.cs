namespace CsCode.Testing.Engine;

/// <summary>The state of a test (or, aggregated, of a group): what the explorer's icon shows.</summary>
public enum TestOutcome { None = 0, Skipped = 1, Passed = 2, Queued = 3, Running = 4, Failed = 5 }

/// <summary>What a node of the explorer's tree stands for (project &gt; namespace / file / module &gt; class / suite &gt; test &gt; case).</summary>
public enum TestNodeKind { Project, Target, Namespace, File, Module, Class, Suite, Test, Case }

/// <summary>A place in a source file: absolute path and 0-based line.</summary>
public sealed record TestLocation(string FilePath, int Line);

/// <summary>One level of grouping above a test: a namespace, a file, a module, a class, a describe block.</summary>
public sealed record TestGroup(string Label, TestNodeKind Kind, TestLocation? Location = null);

/// <summary>
/// A test as a provider found it. <see cref="Id"/> is unique within its project and is what the provider's runner understands (a fully
/// qualified .NET method name, a libtest path prefixed with the cargo target, a Jest file plus its describe/test names).
/// </summary>
public sealed record DiscoveredTest(string Id, string Label, IReadOnlyList<TestGroup> Groups, TestLocation? Location = null, string? SkipReason = null);

/// <summary>A test project: a .NET project, a cargo package, a package.json with Jest or Vitest.</summary>
public sealed record TestProject(string ProviderId, string Name, string FilePath, string Directory, string Framework = "")
{
    /// <summary>Unique across the workspace.</summary>
    public string Key => ProviderId + ":" + FilePath;
}

/// <summary>
/// One result. <see cref="CaseLabel"/> is set for one data row of a parameterised test (an xUnit theory's case): the explorer shows it as a
/// child of <see cref="TestId"/>. <see cref="FailureLocation"/> is where the failure happened, when the stack trace says (else the test's own).
/// </summary>
public sealed record TestResult(
    string TestId,
    TestOutcome Outcome,
    TimeSpan? Duration = null,
    string? Message = null,
    string? StackTrace = null,
    string? Output = null,
    TestLocation? FailureLocation = null,
    string? CaseLabel = null);

/// <summary>Where a provider reports what a discovery or a run produces. Called from any thread; implementations marshal and coalesce.</summary>
public interface ITestRunSink
{
    /// <summary>A test the run found that discovery did not know (a test from a base class, a generated one).</summary>
    void Discovered(TestProject project, DiscoveredTest test);
    void Started(TestProject project, string testId);
    void Finished(TestProject project, TestResult result);
    /// <summary>A line of the tool's output (build, runner): goes to the Tests output channel.</summary>
    void Output(string line);
    /// <summary>A short status for the explorer's summary line ("Building rs_sample…").</summary>
    void Status(string text) { }
}

/// <summary>A request: which tests of one project (null: all of them).</summary>
public sealed record TestRunRequest(TestProject Project, IReadOnlyList<string>? TestIds);

/// <summary>How a run of one project ended: the exit code (null: cancelled or not started) and, when it failed before any test ran, why.</summary>
public sealed record TestRunOutcome(int? ExitCode, string? Error = null)
{
    public static readonly TestRunOutcome Cancelled = new(ExitCode: null);
}

/// <summary>A test declared in a source file, as a scan of its text finds it: the provider's id (or the end of it) and its 0-based line.</summary>
public sealed record SourceTest(string Name, int Line, string Label);

/// <summary>Limits that keep a run's memory bounded (docs/MEMORY.md rule 3/4).</summary>
public static class TestLimits
{
    /// <summary>A test's captured output (and message + stack) beyond this many characters is cut, with a note.</summary>
    public const int MaxOutputPerTest = 64 * 1024;

    /// <summary>At most this many tests per project are taken from a discovery.</summary>
    public const int MaxTestsPerProject = 50_000;

    /// <summary>Files larger than this are not scanned for tests.</summary>
    public const long MaxScannedFileBytes = 2 * 1024 * 1024;

    public static string? Clip(string? text)
    {
        if (text is null || text.Length <= MaxOutputPerTest) return text;
        return text[..MaxOutputPerTest] + $"\n… ({text.Length - MaxOutputPerTest:N0} more characters not kept)";
    }
}
