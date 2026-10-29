using System.Diagnostics;
using System.Text.RegularExpressions;
using CsCode.Platform.Logging;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public class FileLoggerTests : IDisposable
{
    private readonly TempDirectory _dir = new("cscode-log-");
    public void Dispose() => _dir.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FileLoggerProvider Create(Action<FileLoggerOptions>? configure = null)
    {
        var options = new FileLoggerOptions { Directory = _dir.Path };
        configure?.Invoke(options);
        return new FileLoggerProvider(new TestAppPaths(), options);
    }

    private string Log => Path.Combine(_dir.Path, "cscode.log");

    // ---- format ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lines_carry_timestamp_level_category_and_message()
    {
        using var provider = Create();
        var logger = provider.CreateLogger("My.Category");
        logger.LogInformation("hello {Name}", "world");
        logger.LogWarning("careful");
        logger.LogError("bad");
        await provider.FlushAsync();

        var lines = File.ReadAllLines(Log);
        Assert.Equal(3, lines.Length);
        Assert.Matches(new Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \[INF\] My\.Category: hello world$"), lines[0]);
        Assert.Contains("[WRN] My.Category: careful", lines[1]);
        Assert.Contains("[ERR] My.Category: bad", lines[2]);
    }

    [Fact]
    public async Task Levels_below_the_minimum_are_not_written()
    {
        using var provider = Create(o => o.MinLevel = LogLevel.Warning);
        var logger = provider.CreateLogger("c");
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.False(logger.IsEnabled(LogLevel.None));
        logger.LogInformation("quiet");
        logger.LogDebug("quiet");
        logger.LogWarning("loud");
        await provider.FlushAsync();
        Assert.Single(File.ReadAllLines(Log));
    }

    [Fact]
    public async Task Exceptions_and_multiline_messages_are_written_as_one_indented_block()
    {
        using var provider = Create();
        var logger = provider.CreateLogger("c");
        logger.LogError(new InvalidOperationException("boom"), "operation failed\nsecond line");
        logger.LogInformation("next entry");
        await provider.FlushAsync();

        var text = File.ReadAllText(Log);
        Assert.Contains("operation failed\n    second line", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
        // every entry still starts at column 0 with a timestamp; everything else is indented
        var starts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith(' ')).ToArray();
        Assert.Equal(2, starts.Length);
    }

    [Fact]
    public async Task An_existing_log_file_is_appended_to_across_restarts()
    {
        using (var p1 = Create()) { p1.CreateLogger("c").LogInformation("first run"); await p1.FlushAsync(); }
        using (var p2 = Create()) { p2.CreateLogger("c").LogInformation("second run"); await p2.FlushAsync(); }
        var lines = File.ReadAllLines(Log);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first run", lines[0]);
        Assert.Contains("second run", lines[1]);
    }

    [Fact]
    public void The_defaults_are_five_files_of_five_megabytes()
    {
        var o = new FileLoggerOptions();
        Assert.Equal(5 * 1024 * 1024, o.MaxFileBytes);
        Assert.Equal(5, o.MaxFiles);
        Assert.Equal(LogLevel.Information, o.MinLevel);
    }

    // ---- rotation ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Files_rotate_by_size_and_never_exceed_the_file_count()
    {
        using var provider = Create(o => { o.MaxFileBytes = 2000; o.MaxFiles = 3; });
        var logger = provider.CreateLogger("rot");
        for (var i = 0; i < 400; i++) logger.LogInformation("entry number {I:D4} with some padding to make the line longer", i);
        await provider.FlushAsync();

        var files = Directory.GetFiles(_dir.Path, "cscode*.log").Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["cscode.1.log", "cscode.2.log", "cscode.log"], files);               // 3 files: current + 2 rotated, nothing older
        foreach (var f in Directory.GetFiles(_dir.Path)) Assert.True(new FileInfo(f).Length <= 2000 + 200, $"{f} grew past the cap");

        // newest entries are in the current file, older ones in the higher numbers; the oldest were discarded
        var current = File.ReadAllText(Log);
        var older = File.ReadAllText(Path.Combine(_dir.Path, "cscode.1.log"));
        Assert.Contains("0399", current);
        Assert.DoesNotContain("0399", older);
        Assert.DoesNotContain("entry number 0000", string.Join('\n', Directory.GetFiles(_dir.Path).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task A_single_file_configuration_just_truncates_by_rotation_into_nothing()
    {
        using var provider = Create(o => { o.MaxFileBytes = 1000; o.MaxFiles = 1; });
        var logger = provider.CreateLogger("one");
        for (var i = 0; i < 100; i++) logger.LogInformation("line {I:D3} xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", i);
        await provider.FlushAsync();
        Assert.Equal(["cscode.log"], Directory.GetFiles(_dir.Path).Select(f => Path.GetFileName(f)!).ToArray());
        Assert.True(new FileInfo(Log).Length <= 1200);
    }

    // ---- bounded queue / threading -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_writer_cannot_keep_up_the_oldest_entries_are_dropped_counted_and_reported()
    {
        using var provider = Create(o => o.QueueCapacity = 64);
        using var gate = new ManualResetEventSlim();
        using var stalled = new ManualResetEventSlim();
        provider.WriteHook = () => { stalled.Set(); gate.Wait(); };

        var logger = provider.CreateLogger("flood");
        logger.LogInformation("trigger");                        // makes the writer pick up a batch and stall in the hook
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(5), Ct));
        for (var i = 0; i < 1000; i++) logger.LogInformation("flood {I}", i);

        Assert.True(provider.DroppedCount >= 1000 - 64, $"dropped only {provider.DroppedCount}");
        provider.WriteHook = null;
        gate.Set();
        await provider.FlushAsync();

        var text = File.ReadAllText(Log);
        Assert.Contains("flood 999", text);                      // the newest survive
        Assert.DoesNotContain("flood 0\n", text);                // the oldest were dropped
        Assert.Contains("log entries were dropped", text);       // and the drop is visible in the log itself
    }

    [Fact]
    public void Logging_never_blocks_the_caller_even_while_the_writer_is_stalled()
    {
        using var provider = Create(o => o.QueueCapacity = 1024);
        using var gate = new ManualResetEventSlim();
        using var stalled = new ManualResetEventSlim();
        provider.WriteHook = () => { stalled.Set(); gate.Wait(); };
        var logger = provider.CreateLogger("fast");
        logger.LogInformation("trigger");
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++) logger.LogInformation("message {I}", i);
        sw.Stop();
        provider.WriteHook = null;   // detach before the events are disposed: the writer thread still runs the hook until released
        gate.Set();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"20,000 log calls took {sw.Elapsed.TotalMilliseconds:F0} ms with a stalled writer");
    }

    [Fact]
    public async Task Concurrent_writers_never_interleave_or_lose_lines_when_nothing_is_dropped()
    {
        using var provider = Create(o => o.QueueCapacity = 1_000_000);
        var logger = provider.CreateLogger("mt");
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) logger.LogInformation("t{T}-{I:D4}", t, i);
        }, Ct)).ToArray();
        await Task.WhenAll(tasks);
        await provider.FlushAsync();

        Assert.Equal(0, provider.DroppedCount);
        var lines = File.ReadAllLines(Log);
        Assert.Equal(16_000, lines.Length);
        Assert.All(lines, l => Assert.Matches(new Regex(@"^\d{4}-.*\[INF\] mt: t\d-\d{4}$"), l));
        Assert.Equal(16_000, lines.Distinct().Count());
        // within one thread, order is preserved
        for (var t = 0; t < 8; t++)
        {
            var mine = lines.Where(l => l.Contains($": t{t}-")).Select(l => int.Parse(l[^4..])).ToArray();
            Assert.Equal(Enumerable.Range(0, 2000), mine);
        }
    }

    [Fact]
    public void Dispose_drains_the_queue_before_closing()
    {
        var provider = Create(o => o.QueueCapacity = 100_000);
        var logger = provider.CreateLogger("drain");
        for (var i = 0; i < 5000; i++) logger.LogInformation("m {I}", i);
        provider.Dispose();
        Assert.Equal(5000, File.ReadAllLines(Log).Length);
        provider.Dispose();                                      // idempotent
        logger.LogInformation("after dispose");                  // ignored, no throw
    }

    [Fact]
    public void The_writer_is_a_background_below_normal_thread_named_for_diagnostics()
    {
        using var provider = Create();
        var field = typeof(FileLoggerProvider).GetField("_writer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var writer = (Thread)field.GetValue(provider)!;
        Assert.True(writer.IsBackground);
        Assert.Equal(ThreadPriority.BelowNormal, writer.Priority);
        Assert.Equal("CsCode.Log", writer.Name);
    }

    [Fact]
    public async Task An_unwritable_log_location_never_throws_into_the_caller()
    {
        var blocker = _dir.WriteFile("not-a-dir", "x");
        using var provider = new FileLoggerProvider(new TestAppPaths(), new FileLoggerOptions { Directory = Path.Combine(blocker, "logs") });
        var logger = provider.CreateLogger("c");
        logger.LogError("this goes nowhere");
        await provider.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);   // and the writer thread survives
        logger.LogError("still alive");
        await provider.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public void A_disposed_provider_and_its_loggers_are_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var provider = Create();
            var logger = provider.CreateLogger("leak");
            logger.LogInformation("x");
            provider.Dispose();
            return [provider, logger];
        });
    }
}
