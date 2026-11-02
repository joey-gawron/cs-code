using System.Collections.Concurrent;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using Xunit;

namespace CsCode.ProjectTemplates.Tests;

/// <summary>Records what a generator reports.</summary>
public sealed class RecordingProgress : IProjectGenerationProgress
{
    private readonly ConcurrentQueue<string> _steps = new();
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Steps => [.. _steps];
    public IReadOnlyList<string> Lines => [.. _lines];
    public string Log => string.Join('\n', _lines);

    public void Step(string message) => _steps.Enqueue(message);
    public void Output(string line) => _lines.Enqueue(line);
}

/// <summary>A runner that answers from a script (tests of the steps around the tools).</summary>
public sealed class ScriptedRunner(Func<ToolRun, ToolResult> answer) : IToolRunner
{
    public ConcurrentQueue<ToolRun> Runs { get; } = new();

    public Task<ToolResult> RunAsync(ToolRun run, Action<string>? onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Runs.Enqueue(run);
        var result = answer(run);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine?.Invoke(line);
        return Task.FromResult(result);
    }
}

/// <summary>
/// Git for the tests: a throwaway identity and no global or system configuration (the user's signing or hooks settings must not reach a test
/// commit). Set once for the test process, which runs nothing else that reads git's configuration.
/// </summary>
public static class TestGit
{
    private static readonly Lazy<bool> Configured = new(() =>
    {
        var empty = Path.Combine(Path.GetTempPath(), "cscode-newproject-gitconfig-" + Environment.ProcessId);
        File.WriteAllText(empty, "");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "CsCode Tests");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tests@cscode.invalid");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "CsCode Tests");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tests@cscode.invalid");
        return true;
    });

    public static void Configure() => _ = Configured.Value;
}

/// <summary>Network policy of the real-tool tests: downloads (pytest from PyPI, GoogleTest/Catch2 by FetchContent) only with the user's go-ahead.</summary>
public static class Network
{
    public const string Variable = "CSCODE_TEST_DOWNLOADS";

    public static bool Allowed => Environment.GetEnvironmentVariable(Variable) == "1";

    public static void RequireOrSkip(string what)
    {
        if (!Allowed) Assert.Skip($"{what} downloads from the internet; set {Variable}=1 to run it (only with the user's go-ahead)");
    }
}
