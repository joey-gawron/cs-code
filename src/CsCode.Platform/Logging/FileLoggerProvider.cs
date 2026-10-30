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
/// another process rotates it. Batches are rare (a log line is an event), so the open per batch costs nothing measurable.</item>
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
    private FileStream? _file;
    private long _fileSize;

    /// <summary>How long a batch waits for another process's batch before writing without the lock (a stuck holder must not stop logging).</summary>
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

    /// <summary>Completes once every entry logged before this call has been written and flushed to the file.</summary>
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
        List<TaskCompletionSource>? markers = null;
        FileStream? gate = null;
        try
        {
            gate = AcquireLock();
            var dropped = Interlocked.Read(ref _dropped);
            if (dropped > _droppedReported)
            {
                WriteLine(_clock.GetUtcNow().UtcDateTime, LogLevel.Warning, "CsCode.Logging", $"{dropped - _droppedReported} log entries were dropped because the log writer could not keep up");
                _droppedReported = dropped;
            }

            foreach (var e in batch)
            {
                if (e.Marker is not null) { (markers ??= new()).Add(e.Marker); continue; }
                WriteLine(e.Utc, e.Level, e.Category, e.Message);
            }
            _file?.Flush(flushToDisk: false);
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
        // Flush requests are completed no matter what happened above, or FlushAsync callers would hang.
        if (markers is not null) foreach (var m in markers) m.TrySetResult();
    }

    /// <summary>
    /// The cross-process lock for one batch: the lock file opened with <see cref="FileShare.None"/> (an exclusive <c>flock</c> on Unix, a
    /// sharing lock on Windows). Null when the directory cannot hold it or another process keeps it past <see cref="LockTimeout"/>: the batch
    /// is then written without it, as logging must go on.
    /// </summary>
    private FileStream? AcquireLock()
    {
        try { System.IO.Directory.CreateDirectory(_dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var deadline = Environment.TickCount64 + (long)LockTimeout.TotalMilliseconds;
        while (true)
        {
            try { return new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1); }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(5); } // held by another process's batch
            catch (IOException) { return null; }
        }
    }

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

    public void Dispose()
    {
        lock (_q)
        {
            if (_stopping) return;
            _stopping = true;
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
