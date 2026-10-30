using System.Text;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;

namespace CsCode.Platform.Logging;

public sealed class FileLoggerOptions
{
    /// <summary>Directory of the log files; null = <see cref="IAppPaths.LogsDir"/>.</summary>
    public string? Directory { get; set; }
    public string BaseName { get; set; } = "cscode";
    /// <summary>Rotate the current file once it would exceed this size (default 5 MB).</summary>
    public long MaxFileBytes { get; set; } = 5L * 1024 * 1024;
    /// <summary>Total files kept: the current one plus <c>MaxFiles - 1</c> rotated ones (default 5, i.e. at most 25 MB on disk).</summary>
    public int MaxFiles { get; set; } = 5;
    /// <summary>Entries buffered between the callers and the writer thread; when full the OLDEST entries are dropped (and counted).</summary>
    public int QueueCapacity { get; set; } = 8192;
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// Rolling file logger: <c>{LogsDir}/cscode.log</c>, rotated to <c>cscode.1.log … cscode.{MaxFiles-1}.log</c> by size.
/// <list type="bullet">
/// <item><b>Callers never wait for the disk.</b> A log call formats its message and enqueues it (a short lock); a dedicated below-normal
/// writer thread writes batches. The queue is a bounded ring: when it overflows the oldest entries are dropped, counted
/// (<see cref="DroppedCount"/>) and reported in the log as a warning line.</item>
/// <item>Thread-safe; each line carries a UTC timestamp, level and category; exceptions follow on indented lines.</item>
/// <item>Disposing drains the queue and closes the file. <see cref="FlushAsync"/> completes when everything logged so far is on disk.</item>
/// <item><b>Several processes share the file</b> (background mode runs one CsCode process per session, all logging here). Each batch is
/// written under an exclusive lock on <c>.{BaseName}.lock</c> beside it, into the file opened for that batch: a stream kept open would
/// write at its own idea of the end and overwrite the other processes' lines, and would follow the file into <c>cscode.1.log</c> when
/// another process rotates it. Batches are rare (a log line is an event), so the open per batch costs nothing measurable. A batch waits for
/// another process's batch as long as that takes and is never written without the lock while that process holds it (see
/// <see cref="AcquireLock"/>); only a lock that cannot be taken at all is done without.</item>
/// </list>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly record struct Entry(DateTime Utc, LogLevel Level, string Category, string Message, TaskCompletionSource? Marker);

    private readonly FileLoggerOptions _options;
    private readonly TimeProvider _clock;
    private readonly string _dir;
    private readonly object _q = new();
    private readonly Entry[] _ring;
    private readonly Thread _writer;
    private int _head, _count;

    /// <summary>Test hook: runs on the writer thread before each batch is written (lets tests stall the writer deterministically).</summary>
    internal Action? WriteHook { get; set; }
    private long _dropped;
    private long _droppedReported;
    private bool _stopping;
    /// <summary><see cref="Environment.TickCount64"/> at which a stopping provider stops waiting for the lock; 0 while it runs (no limit).</summary>
    private long _stopDeadline;
    private FileStream? _file;
    private long _fileSize;

    /// <summary>
    /// How long a stopping provider (<see cref="Dispose"/>) waits, all batches together, for another process to let go of the lock before it drops
    /// what is still queued. A running provider has no limit (<see cref="AcquireLock"/>).
    /// </summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    public FileLoggerProvider(IAppPaths paths, FileLoggerOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new FileLoggerOptions();
        _clock = clock ?? TimeProvider.System;
        _dir = _options.Directory ?? paths.LogsDir;
        _ring = new Entry[Math.Max(16, _options.QueueCapacity)];
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "CsCode.Log", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public string LogFilePath => Path.Combine(_dir, _options.BaseName + ".log");

    private string LockFilePath => Path.Combine(_dir, "." + _options.BaseName + ".lock");

    /// <summary>Entries discarded because the writer could not keep up.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _options.MinLevel;

    internal void Enqueue(LogLevel level, string category, string message)
    {
        var entry = new Entry(_clock.GetUtcNow().UtcDateTime, level, category, message, null);
        lock (_q)
        {
            if (_stopping) return;
            Add(entry);
            Monitor.Pulse(_q);
        }
    }

    private void Add(Entry entry)
    {
        if (_count == _ring.Length)
        {
            // Full: drop the oldest. A marker (flush request) must never be lost, or FlushAsync would hang: complete it instead.
            var oldest = _ring[_head];
            oldest.Marker?.TrySetResult();
            _ring[_head] = default;
            _head = (_head + 1) % _ring.Length;
            _count--;
            Interlocked.Increment(ref _dropped);
        }
        _ring[(_head + _count) % _ring.Length] = entry;
        _count++;
    }

    /// <summary>
    /// Completes once every entry logged before this call has been written and flushed to the file (or dropped). While another process holds the
    /// lock that is when it lets go, however long that takes.
    /// </summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_q)
        {
            if (_stopping) return Task.CompletedTask;
            Add(new Entry(default, LogLevel.None, "", "", tcs));
            Monitor.Pulse(_q);
        }
        return tcs.Task;
    }

    // ---- writer thread -----------------------------------------------------------------------------------------------------------

    private void WriterLoop()
    {
        var batch = new List<Entry>(256);
        while (true)
        {
            lock (_q)
            {
                while (_count == 0 && !_stopping) Monitor.Wait(_q);
                if (_count == 0 && _stopping) break;
                for (var i = 0; i < 256 && _count > 0; i++)
                {
                    batch.Add(_ring[_head]);
                    _ring[_head] = default;
                    _head = (_head + 1) % _ring.Length;
                    _count--;
                }
            }
            try { WriteHook?.Invoke(); } catch (Exception) { /* test hook: must not be able to kill the writer either */ }
            Write(batch);
            batch.Clear();
        }
        CloseFile();
    }

    private void Write(List<Entry> batch)
    {
        FileStream? gate = null;
        try
        {
            gate = AcquireLock(out var stillHeld);
            if (stillHeld)
            {
                // Stopping, and another process kept the lock past the deadline: the batch is dropped (counted), never written without the lock.
                foreach (var e in batch) if (e.Marker is null) Interlocked.Increment(ref _dropped);
            }
            else
            {
                var dropped = Interlocked.Read(ref _dropped);
                if (dropped > _droppedReported)
                {
                    WriteLine(_clock.GetUtcNow().UtcDateTime, LogLevel.Warning, "CsCode.Logging", $"{dropped - _droppedReported} log entries were dropped because the log writer could not keep up");
                    _droppedReported = dropped;
                }

                foreach (var e in batch)
                    if (e.Marker is null) WriteLine(e.Utc, e.Level, e.Category, e.Message);
                _file?.Flush(flushToDisk: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down. Drop the rest of this batch; the next one opens the file again.
        }
        catch (Exception)
        {
            // Anything else is a bug in a formatter or the file system layer: an unhandled exception on this thread would terminate the
            // whole process, and there is nowhere left to report it. Swallow and keep serving later entries.
        }
        finally
        {
            CloseFile(); // before the lock goes: the next holder must find every byte of this batch in the file
            gate?.Dispose();
        }
        // Flush requests are completed no matter what happened above (every one in the batch, also those after a line that failed), or FlushAsync
        // callers would hang.
        foreach (var e in batch) e.Marker?.TrySetResult();
    }

    /// <summary>
    /// The cross-process lock for one batch: the lock file opened with <see cref="FileShare.None"/> (an exclusive <c>flock</c> on Unix, a
    /// sharing lock on Windows).
    /// <list type="bullet">
    /// <item><b>Another process holds it</b> (its batch is being written): wait, as long as that takes. Writing without it is no way out: a holder
    /// stalled mid-batch (a process stopped in a debugger) writes the rest of its batch at the offset it opened the file at once it goes on, over
    /// these lines or through the middle of them. Nothing waits on this background thread, and meanwhile the bounded queue drops the oldest
    /// entries and reports the drop once the lock is had.</item>
    /// <item><b>Stopping</b> (<see cref="Dispose"/>): the wait ends <see cref="LockTimeout"/> after Dispose began, so a stalled holder cannot hold
    /// up the exit for the whole join. Then <paramref name="stillHeld"/> is set and the batch, like each one after it that finds the lock still
    /// taken at its single try, is dropped and counted: losing a quitting process's last lines is better than corrupting another process's.</item>
    /// <item><b>It cannot be taken at all</b> (the folder or the file cannot be created or opened: a read-only remount, a full disk, an I/O error, a
    /// lock file of another user, a folder removed under the app): null at once, and the batch is written without it, as logging must go on.
    /// Waiting would not help, and would cost each batch, and the exit, the whole wait.</item>
    /// </list>
    /// </summary>
    private FileStream? AcquireLock(out bool stillHeld)
    {
        stillHeld = false;
        try { System.IO.Directory.CreateDirectory(_dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var pause = 1;
        while (true)
        {
            try { return new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1); }
            catch (IOException ex) when (IsHeldByAnother(ex))
            {
                var stop = Volatile.Read(ref _stopDeadline);
                if (stop != 0 && Environment.TickCount64 >= stop) { stillHeld = true; return null; }
                Thread.Sleep(pause); // another batch takes a millisecond or two; a stalled holder is polled at 20 Hz, not spun on
                pause = Math.Min(pause * 2, 50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// The lock is held by another process (or another handle), not "cannot be used": <c>flock</c> answered EWOULDBLOCK, which .NET reports as an
    /// IOException carrying the raw errno (11 on Linux, 35 on macOS and the BSDs); Windows reports a sharing or lock violation. The same test as
    /// CsCode.Sessions' <c>FileLock</c>, which Platform cannot reference.
    /// </summary>
    private static bool IsHeldByAnother(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 32 or 33
        : ex.HResult == (OperatingSystem.IsLinux() ? 11 : 35);

    private readonly StringBuilder _line = new();

    // One entry per write call, so the size cap is checked per line: a file never overshoots MaxFileBytes by more than one entry.
    private void WriteLine(DateTime utc, LogLevel level, string category, string message)
    {
        _line.Clear();
        AppendLine(_line, utc, level, category, message);
        WriteText(_line.ToString());
    }

    private static void AppendLine(StringBuilder sb, DateTime utc, LogLevel level, string category, string message)
    {
        sb.Append(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")).Append(" [").Append(Tag(level)).Append("] ").Append(category).Append(": ");
        // keep one entry == one block: continuation lines are indented so a grep for timestamps still finds every entry start
        sb.Append(message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
    }

    private static string Tag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
    };

    private void WriteText(string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (_file is null) OpenFile();
        if (_file is null) return;
        if (_fileSize > 0 && _fileSize + bytes.Length > _options.MaxFileBytes)
        {
            Rotate();
            OpenFile();
            if (_file is null) return;
        }
        _file.Write(bytes);
        _fileSize += bytes.Length;
    }

    private void OpenFile()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            _file = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 16 * 1024);
            _fileSize = _file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
        }
    }

    private void CloseFile()
    {
        try { _file?.Dispose(); } catch (IOException) { }
        _file = null;
        _fileSize = 0;
    }

    private void Rotate()
    {
        CloseFile();
        try
        {
            var keep = Math.Max(1, _options.MaxFiles);
            // cscode.{keep-1}.log is the oldest allowed: it is overwritten by its predecessor, so at most `keep` files exist.
            for (var i = keep - 1; i >= 1; i--)
            {
                var src = i == 1 ? LogFilePath : Numbered(i - 1);
                if (!File.Exists(src)) continue;
                File.Move(src, Numbered(i), overwrite: true);
            }
            if (keep == 1) File.Delete(LogFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: worst case the current file keeps growing */ }
    }

    private string Numbered(int n) => Path.Combine(_dir, $"{_options.BaseName}.{n}.log");

    // ---- lifetime ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stops: later entries are ignored, the writer drains the queue, closes the file and exits, and this waits for it. Another process holding
    /// the lock is waited for at most <see cref="LockTimeout"/> from here; whatever is still queued then is dropped (and counted in
    /// <see cref="DroppedCount"/>), never written without the lock (<see cref="AcquireLock"/>).
    /// </summary>
    public void Dispose()
    {
        lock (_q)
        {
            if (_stopping) return;
            _stopping = true;
            Volatile.Write(ref _stopDeadline, Environment.TickCount64 + (long)LockTimeout.TotalMilliseconds);
            Monitor.PulseAll(_q);
        }
        if (_writer != Thread.CurrentThread) _writer.Join(TimeSpan.FromSeconds(5)); // the writer drains whatever is queued, then exits
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message = message + "\n" + exception;
            owner.Enqueue(logLevel, category, message);
        }
    }
}
