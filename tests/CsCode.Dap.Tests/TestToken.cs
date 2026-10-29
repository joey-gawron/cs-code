global using static CsCode.Dap.Tests.TestToken;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>xUnit's per-test cancellation token (analyzer xUnit1051 wants it on every cancellable call).</summary>
public static class TestToken
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>Tests that read process-wide counters (threads, fds, child processes) or assert latency budgets must not share the process with other running tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}

/// <summary>Measured numbers from the threading/soak tests: always a diagnostic message; also appended to the file named by CSCODE_DAP_TEST_METRICS.</summary>
public static class TestMetrics
{
    private static readonly object Gate = new();

    public static void Report(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path)
            lock (Gate) File.AppendAllText(path, text + "\n");
    }
}
