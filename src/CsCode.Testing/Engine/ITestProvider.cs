using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Testing.Engine;

/// <summary>Starts a debug session for a test provider (the run controller's bridge to <see cref="IDebugService"/>).</summary>
public interface ITestDebugLauncher
{
    /// <summary>Starts <paramref name="configuration"/>; null when the debugger could not start (the debug service has told the user why).</summary>
    ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct);
}

/// <summary>
/// One kind of test project (built in; there is no extension system): finds the projects in a folder, discovers their tests, runs and debugs
/// them, and finds the tests a source file declares (for the editor's glyphs). Everything is called off the UI thread.
/// </summary>
public interface ITestProvider
{
    /// <summary>"dotnet", "rust", "javascript".</summary>
    string Id { get; }

    string Label { get; }

    /// <summary>The editor languages whose documents can declare this provider's tests.</summary>
    IReadOnlyList<string> LanguageIds { get; }

    /// <summary>The test projects under <paramref name="folder"/> (a local directory).</summary>
    Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct);

    /// <summary>The project's tests. May build; progress and tool output go to <paramref name="log"/>.</summary>
    Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct);

    /// <summary>Runs the request's tests, reporting each start and result to <paramref name="sink"/> as they come.</summary>
    Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct);

    /// <summary>Runs the request's tests under the debugger; results are reported as far as the provider can read them from the session.</summary>
    Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct);

    /// <summary>The tests <paramref name="text"/> (the file at <paramref name="filePath"/>) declares, from its text alone. Cheap: runs per edit.</summary>
    IReadOnlyList<SourceTest> ScanSource(string filePath, string text);

    /// <summary>
    /// The groups of tests the file declares (a fixture class, a suite block), from its text alone, for a "Run All Tests" lens above each;
    /// cheap, as <see cref="ScanSource"/>. Default: none.
    /// </summary>
    IReadOnlyList<SourceTestGroup> ScanGroups(string filePath, string text) => [];

    /// <summary>
    /// Fires (on any thread) when the provider's projects or their tests may have changed without a source file being saved: a build system
    /// configured or built them. The controller then detects this provider's projects again and discovers them (<see cref="TestController"/>).
    /// Default: never.
    /// </summary>
    IEventSource<bool> ProjectsChanged => EventSource.Never<bool>();

    /// <summary>Whether a scanned <paramref name="source"/> test is the discovered <paramref name="test"/> (which may live in another file).</summary>
    bool Matches(DiscoveredTest test, string filePath, SourceTest source) =>
        test.Location is { } loc && PathsEqual(loc.FilePath, filePath) && TestIds.EndsWithSegment(test.Id, source.Name);

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Helpers on test ids.</summary>
public static class TestIds
{
    /// <summary>
    /// Whether <paramref name="id"/> is <paramref name="name"/> or ends with it after a separator (<c>.</c>, <c>::</c>, <c>/</c>, <c> &gt; </c>,
    /// <c>+</c>): <c>lib/tests::adds</c> ends with <c>tests::adds</c>, <c>Ns.C.M</c> with <c>C.M</c>, but <c>Ns.C.Madds</c> does not end with <c>adds</c>.
    /// </summary>
    public static bool EndsWithSegment(string id, string name)
    {
        if (string.Equals(id, name, StringComparison.Ordinal)) return true;
        if (name.Length == 0 || !id.EndsWith(name, StringComparison.Ordinal)) return false;
        var before = id.AsSpan(0, id.Length - name.Length);
        return before.EndsWith(".") || before.EndsWith("::") || before.EndsWith("/") || before.EndsWith(" > ") || before.EndsWith("+");
    }
}
