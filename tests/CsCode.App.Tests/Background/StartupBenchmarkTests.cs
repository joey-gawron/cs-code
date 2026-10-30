using System.Globalization;
using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// The start-up measurements of docs/SESSIONS.md ("Start-up performance"), repeated enough to give medians: the runtime floor
/// (<c>cscode --version</c>), a cold start to the first painted frame (no spare), a resident session shown again, and a warm-spare claim. Opt-in
/// (they take a minute and only mean something on a quiet machine): set <c>CSCODE_STARTUP_BENCH</c> to a file the results are appended to, and
/// <c>CSCODE_TEST_EXECUTABLE</c> to measure another build (e.g. the ReadyToRun publish). The budgets themselves are asserted by
/// <see cref="BackgroundSessionProcessTests"/>.
/// </summary>
public class StartupBenchmarkTests
{
    private static string? ResultsFile => Environment.GetEnvironmentVariable("CSCODE_STARTUP_BENCH") is { Length: > 0 } f ? f : null;

    private static void Report(string metric, IReadOnlyList<double> samples)
    {
        var sorted = samples.Order().ToArray();
        var median = sorted[sorted.Length / 2];
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm} {Label()} {metric}: median {median:0} ms, min {sorted[0]:0}, max {sorted[^1]:0} (n={sorted.Length}: {string.Join(", ", samples.Select(s => s.ToString("0", CultureInfo.InvariantCulture)))}); load {Load()}");
        TestContext.Current.SendDiagnosticMessage(line);
        File.AppendAllText(ResultsFile!, line + "\n");
    }

    private static string Label() => Environment.GetEnvironmentVariable("CSCODE_STARTUP_BENCH_LABEL") is { Length: > 0 } l ? l : SessionProcessHarness.ExecutableUnderTest;

    private static string Load()
    {
        try { return File.ReadAllText("/proc/loadavg").Split(' ')[0]; }
        catch (IOException) { return "?"; }
    }

    private static void SkipUnlessRequested()
    {
        if (ResultsFile is null) Assert.Skip("opt-in benchmark: set CSCODE_STARTUP_BENCH=<results file>");
    }

    [Fact]
    public async Task Runtime_floor_cold_start_resident_show_and_spare_claim()
    {
        SkipUnlessRequested();
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": false", trace: false);
        var file = h.WriteFile("Widget.cs", "namespace Demo;\n\npublic class Widget\n{\n    public int Size { get; set; }\n}\n");

        // 1. The runtime floor: process start, runtime start, Main, exit.
        var version = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var run = await h.RunAsync("--version");
            Assert.True(run.ExitCode == 0, run.Error);
            version.Add(run.Elapsed.TotalMilliseconds);
        }
        Report("version", version);

        // 2. Cold start: launcher → new session process → services → window → first painted frame → launcher exits.
        var cold = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var run = await h.RunAsync("--session", "cold" + i, file);
            Assert.True(run.ExitCode == 0, run.Error);
            cold.Add(run.Elapsed.TotalMilliseconds);
            var quit = await h.RunAsync("--quit", "--all");
            Assert.True(quit.ExitCode == 0, quit.Error);
            Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.ProcessesOfThisHome().Count == 0, 20_000));
        }
        Report("cold start", cold);

        // 3. Resident show: a hidden session shown again.
        var open = await h.RunAsync("--session", "L", file);
        Assert.True(open.ExitCode == 0, open.Error);
        var pid = h.Discover().Single(p => p.Session == "L").Pid;
        await Task.Delay(2000); // let the first window's background work finish
        var resident = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            h.Must(pid, new IpcMessage { Type = IpcVerbs.Hide });
            await Task.Delay(300);
            var run = await h.RunAsync("--session", "L");
            Assert.True(run.ExitCode == 0, run.Error);
            resident.Add(run.Elapsed.TotalMilliseconds);
        }
        Report("resident show", resident);
        var quitAll = await h.RunAsync("--quit", "--all");
        Assert.True(quitAll.ExitCode == 0, quitAll.Error);
        Assert.True(await SessionProcessHarness.WaitUntilAsync(() => h.ProcessesOfThisHome().Count == 0, 20_000));
    }

    [Fact]
    public async Task Warm_spare_claim()
    {
        SkipUnlessRequested();
        using var h = new SessionProcessHarness("\"cscode.background.warmSpare\": true", trace: false);
        var file = h.WriteFile("Widget.cs", "namespace Demo;\n\npublic class Widget\n{\n    public int Size { get; set; }\n}\n");
        var claims = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            // A fresh spare each time: started by --background (the first) or by the previous claim, then left to finish its warm-up and trim.
            if (i == 0) Assert.Equal(0, (await h.RunAsync("--background")).ExitCode);
            await h.WaitForAsync(p => p.IsSpare, what: "warm spare");
            await Task.Delay(6000);
            var run = await h.RunAsync("--session", "S" + i, file);
            Assert.True(run.ExitCode == 0, run.Error);
            claims.Add(run.Elapsed.TotalMilliseconds);
        }
        Report("spare claim", claims);
    }
}
