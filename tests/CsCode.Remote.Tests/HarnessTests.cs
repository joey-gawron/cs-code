using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>The test harness itself, where a fault of its own would fail (or hide) the tests that use it.</summary>
public class HarnessTests
{
    // Tests poll Dump while the server works (ReconnectReclaimTests every 25 ms), and the server's file logger holds .cscode.lock beside its logs
    // exclusively during each batch: reading that file throws, which failed the polling test instead of showing the log.
    [Fact]
    public async Task Dump_reads_the_server_logs_while_the_server_holds_its_log_lock()
    {
        await using var host = new RemoteTestHost();
        var logs = Path.Combine(host.ServerDataDir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "cscode.log"), "2026-10-30T12:00:00.000Z [INF] CsCode.Server.Tasks: Task 'serve' started\n");
        using (new FileStream(Path.Combine(logs, ".cscode.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("Task 'serve' started", host.Dump(), StringComparison.Ordinal);
    }
}
