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
    // TestAppPaths owns a temp directory of its own: it must be disposed or every run leaves one behind on the (shared) machine.
    private readonly TestAppPaths _paths = new();
    public void Dispose() { _paths.Dispose(); _dir.Dispose(); }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FileLoggerProvider Create(Action<FileLoggerOptions>? configure = null)
    {
        var options = new FileLoggerOptions { Directory = _dir.Path };
        configure?.Invoke(options);
        return new FileLoggerProvider(_paths, options);
    }

    private string Log => Path.Combine(_dir.Path, "cscode.log");

    private string LockFile => Path.Combine(_dir.Path, ".cscode.lock");

    // Another process's batch in progress: the lock file held exactly as the provider takes it (a separate handle, so an flock of its own).
    private FileStream HoldLock() => new(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);

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
        Assert.Equal(["cscode.log"], Directory.GetFiles(_dir.Path, "*.log").Select(f => Path.GetFileName(f)!).ToArray());
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

    // ---- several processes ---------------------------------------------------------------------------------------------------------

    // Background mode runs one CsCode process per session, all logging to the same file. Two providers on one directory stand in for
    // two processes: each opens its own file handle (and takes its own lock), exactly as separate processes do.
    [Fact]
    public async Task Two_processes_logging_to_the_same_file_never_overwrite_each_others_lines()
    {
        using var a = Create(o => o.QueueCapacity = 100_000);
        using var b = Create(o => o.QueueCapacity = 100_000);
        var tasks = new[] { (a, "A"), (b, "B") }.SelectMany(p => Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            var logger = p.Item1.CreateLogger(p.Item2);
            for (var i = 0; i < 1000; i++) logger.LogInformation("t{T}-{I:D4}", t, i);
        }, Ct))).ToArray();
        await Task.WhenAll(tasks);
        await Task.WhenAll(a.FlushAsync(), b.FlushAsync());

        var lines = File.ReadAllLines(Log);
        Assert.All(lines, l => Assert.Matches(new Regex(@"^\d{4}-\S+ \[INF\] [AB]: t\d-\d{4}$"), l));
        Assert.Equal(8000, lines.Distinct().Count());
        Assert.Equal(8000, lines.Length);
    }

    [Fact]
    public async Task Rotation_by_one_process_never_strands_another_one_writing_past_the_cap()
    {
        using var a = Create(o => { o.MaxFileBytes = 2000; o.MaxFiles = 3; });
        using var b = Create(o => { o.MaxFileBytes = 2000; o.MaxFiles = 3; });
        var la = a.CreateLogger("A");
        var lb = b.CreateLogger("B");
        for (var i = 0; i < 200; i++)
        {
            la.LogInformation("entry {I:D4} with some padding to make the line longer", i);
            await a.FlushAsync();
            lb.LogInformation("entry {I:D4} with some padding to make the line longer", i);
            await b.FlushAsync();
        }

        var files = Directory.GetFiles(_dir.Path, "*.log").Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["cscode.1.log", "cscode.2.log", "cscode.log"], files);
        foreach (var f in files) Assert.True(new FileInfo(Path.Combine(_dir.Path, f)).Length <= 2000 + 200, $"{f} grew past the cap");
        var current = File.ReadAllText(Log);
        Assert.Contains("B: entry 0199", current);
    }

    // A holder stalled mid-batch (a process stopped in a debugger) writes the rest of its batch at the offset it opened the file at once it goes on:
    // lines written meanwhile without the lock would be overwritten, or cut in half. So a running provider waits, however long, and loses nothing
    // the bounded queue keeps.
    [Fact]
    public async Task A_batch_waits_for_another_process_holding_the_lock_instead_of_writing_without_it()
    {
        using var provider = Create();
        var logger = provider.CreateLogger("c");
        Task flushed;
        using (HoldLock())
        {
            for (var i = 0; i < 10; i++) logger.LogInformation("waited {I}", i);
            flushed = provider.FlushAsync();
            await Task.Delay(FileLoggerProvider.LockTimeout + TimeSpan.FromMilliseconds(500), Ct);
            Assert.False(File.Exists(Log), "a batch was written while another process held the lock:\n" + (File.Exists(Log) ? File.ReadAllText(Log) : ""));
            Assert.False(flushed.IsCompleted);
        }
        await flushed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"c: waited {i}"), File.ReadAllLines(Log).Select(l => l[(l.IndexOf("] ", StringComparison.Ordinal) + 2)..]));
        Assert.Equal(0, provider.DroppedCount);
    }

    // At exit the wait is bounded (Dispose must not hang on a stalled holder): what is still queued then is dropped, never written without the lock.
    [Fact]
    public void Disposing_while_another_process_keeps_the_lock_stays_bounded_and_drops_the_rest_instead_of_writing_it_unlocked()
    {
        var provider = Create();
        var logger = provider.CreateLogger("c");
        using (HoldLock())
        {
            logger.LogInformation("queued at exit 1");
            logger.LogInformation("queued at exit 2");
            var clock = Stopwatch.StartNew();
            provider.Dispose();
            clock.Stop();
            Assert.True(clock.Elapsed < FileLoggerProvider.LockTimeout + TimeSpan.FromSeconds(2), $"Dispose took {clock.Elapsed.TotalMilliseconds:F0} ms");
            Assert.False(File.Exists(Log), "the batch was written without the lock:\n" + (File.Exists(Log) ? File.ReadAllText(Log) : ""));
            Assert.Equal(2, provider.DroppedCount);
        }
    }

    [Fact]
    public async Task Disposing_waits_for_another_process_s_batch_that_ends_in_time_and_then_writes_everything()
    {
        var provider = Create();
        var logger = provider.CreateLogger("c");
        var held = HoldLock();
        logger.LogInformation("written at exit");
        var release = Task.Run(async () => { await Task.Delay(100, Ct); held.Dispose(); }, Ct);
        provider.Dispose();
        await release;
        Assert.Contains("c: written at exit", File.ReadAllText(Log));
        Assert.Equal(0, provider.DroppedCount);
    }

    // Waiting only helps against another process's batch. A lock that cannot be taken at all (a read-only or full file system, a lock file this user
    // cannot open, a broken link) is given up at once, and the lines are written without it as before; retrying would cost every batch, and the
    // exit, the whole wait.
    [Theory]
    [InlineData("dangling")]   // a link into a folder that does not exist: ENOENT (a DirectoryNotFoundException), an IOException that is not contention
    [InlineData("loop")]       // a link to itself: ELOOP, a plain IOException
    [InlineData("read-only")]  // a lock file left by another user: EACCES
    public async Task A_lock_that_cannot_be_taken_at_all_is_not_waited_for_and_the_lines_are_still_written(string kind)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        Assert.SkipWhen(kind == "read-only" && Environment.UserName == "root", "permission bits are not enforced for root");
        switch (kind)
        {
            case "dangling": File.CreateSymbolicLink(LockFile, Path.Combine(_dir.Path, "missing", "cscode.lock")); break;
            case "loop": File.CreateSymbolicLink(LockFile, Path.GetFileName(LockFile)); break;
            default: File.WriteAllText(LockFile, ""); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(LockFile, UnixFileMode.UserRead); break;
        }
        using var provider = Create();
        var logger = provider.CreateLogger("c");
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            logger.LogInformation("line {I}", i);
            await provider.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        logger.LogInformation("line 3");
        provider.Dispose();
        clock.Stop();
        Assert.True(clock.Elapsed < FileLoggerProvider.LockTimeout, $"four batches took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(4, File.ReadAllLines(Log).Length);
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
        using var provider = new FileLoggerProvider(_paths, new FileLoggerOptions { Directory = Path.Combine(blocker, "logs") });
        var logger = provider.CreateLogger("c");
        logger.LogError("this goes nowhere");
        await provider.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);   // and the writer thread survives
        logger.LogError("still alive");
        await provider.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    // A batch whose writing fails part-way still completes every flush request in it, also those queued after the line that failed: a full disk
    // fails the write of the line that spills the file's 16 KB buffer, in the middle of the batch. /dev/full fails every write with ENOSPC.
    [Fact]
    public async Task A_flush_completes_even_when_its_batch_fails_before_reaching_it()
    {
        Assert.SkipUnless(File.Exists("/dev/full"), "needs /dev/full");
        File.CreateSymbolicLink(Log, "/dev/full");
        using var provider = Create();
        using var gate = new ManualResetEventSlim();
        using var stalled = new ManualResetEventSlim();
        provider.WriteHook = () => { stalled.Set(); gate.Wait(); };
        var logger = provider.CreateLogger("c");
        logger.LogInformation("trigger");                        // the writer takes it and stalls, so the next entry and the flush form one batch
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(5), Ct));
        logger.LogInformation("fails {Padding}", new string('x', 20_000));
        var flushed = provider.FlushAsync();
        provider.WriteHook = null;
        gate.Set();
        await flushed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
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
