using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace CsCode.Editor.TreeSitter.Isolation;

/// <summary>
/// The "black box" of the tree-sitter lane: which grammar's native code the lane is executing right now, and since when. Before every call
/// that runs grammar code (a parse slice, an injection parse, a query over a grammar's tree) the lane writes the grammar into a small
/// MEMORY-MAPPED file and clears it afterwards — a few plain stores, no system call, no allocation. Because the mapping is shared with the
/// kernel's page cache, the last state survives a crash of the process (SIGSEGV in a grammar's external scanner): the next CsCode process
/// finds the file of a process that is gone with "inside grammar X" in it (<see cref="CollectCrashes"/>) and the quarantine counts it. In the
/// same process a watchdog reads <see cref="Current"/> to see a call that never returns (an endless loop in a scanner: the parse's progress
/// callback is never reached), docs/TREE-SITTER.md "Isolation".
///
/// One file per process (<c>&lt;dir&gt;/&lt;pid&gt;.flight</c>; background mode runs several CsCode processes), deleted on a clean exit. Writes come
/// from the tree-sitter lane only; <see cref="Current"/> may be read from any thread (a torn read during a switch is harmless: the watchdog
/// acts only on a state that has not changed for seconds).
/// </summary>
public sealed unsafe class FlightRecorder : IDisposable
{
    private const int Magic = 0x46545343; // "CSTF"
    private const int FormatVersion = 1;
    private const int Size = 256;
    private const int OffMagic = 0, OffVersion = 4, OffPid = 8, OffState = 12, OffEntered = 16, OffProcessStart = 24, OffNameLength = 32, OffName = 36;
    private const int MaxName = Size - OffName;

    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte[]? _memory;
    private GCHandle _pin;
    private readonly byte* _p;
    private readonly string? _path;
    private string? _current;
    private bool _disposed;

    private FlightRecorder(string? path, MemoryMappedFile? file, MemoryMappedViewAccessor? view)
    {
        _path = path;
        _file = file;
        _view = view;
        if (view is not null)
        {
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _p = p + view.PointerOffset;
        }
        else
        {
            _memory = new byte[Size];
            _pin = GCHandle.Alloc(_memory, GCHandleType.Pinned);
            _p = (byte*)_pin.AddrOfPinnedObject();
        }
        *(int*)(_p + OffMagic) = Magic;
        *(int*)(_p + OffVersion) = FormatVersion;
        *(int*)(_p + OffPid) = Environment.ProcessId;
        *(long*)(_p + OffProcessStart) = ProcessStartTicks();
        Volatile.Write(ref *(int*)(_p + OffState), 0);
    }

    /// <summary>A recorder that only feeds the in-process watchdog (no file: tests, no user data directory).</summary>
    public static FlightRecorder InMemory() => new(null, null, null);

    /// <summary>
    /// A recorder backed by <c>&lt;directory&gt;/&lt;pid&gt;[-&lt;suffix&gt;].flight</c> (a suffix for a second lane of the same process); falls back to
    /// <see cref="InMemory"/> when the file cannot be created.
    /// </summary>
    public static FlightRecorder Open(string directory, string? suffix = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Environment.ProcessId + (suffix is null ? "" : "-" + suffix) + ".flight");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) fs.SetLength(Size);
            var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, Size, MemoryMappedFileAccess.ReadWrite);
            var view = file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            return new FlightRecorder(path, file, view);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return InMemory();
        }
    }

    /// <summary>The backing file, or null (in memory).</summary>
    public string? FilePath => _path;

    /// <summary>The record <see cref="Enter"/> takes for a grammar: built once per grammar, so entering allocates nothing.</summary>
    public static string RecordFor(string grammar, string identity) => grammar + "|" + identity;

    /// <summary>
    /// Marks the lane as inside the native code of the grammar <paramref name="record"/> names (<see cref="RecordFor"/>) until the returned scope
    /// is disposed, which restores what was recorded before (an injected grammar inside its host's highlight). Lane thread only.
    /// </summary>
    public Scope Enter(string record)
    {
        if (_disposed) return default;
        var previous = _current;
        var wasInside = Volatile.Read(ref *(int*)(_p + OffState)) != 0;
        Write(record);
        *(long*)(_p + OffEntered) = Environment.TickCount64;
        Volatile.Write(ref *(int*)(_p + OffState), 1);
        return new Scope(this, wasInside ? previous : null);
    }

    private void Restore(string? previous)
    {
        if (_disposed) return;
        if (previous is null)
        {
            Volatile.Write(ref *(int*)(_p + OffState), 0);
            return;
        }
        Write(previous);
        // the outer call is still running: keep its clock where it was would need a stack; restarting it is the conservative choice
        *(long*)(_p + OffEntered) = Environment.TickCount64;
    }

    private void Write(string record)
    {
        if (ReferenceEquals(record, _current)) return;
        _current = record;
        Volatile.Write(ref *(int*)(_p + OffNameLength), 0);
        // encoded straight into the mapping (no allocation); a record too long for the slot (never, for grammar names) is cut to its name
        if (!Encoding.UTF8.TryGetBytes(record, new Span<byte>(_p + OffName, MaxName), out var n))
            n = Encoding.UTF8.GetBytes(record.AsSpan(0, Math.Min(record.Length, MaxName / 4)), new Span<byte>(_p + OffName, MaxName));
        Volatile.Write(ref *(int*)(_p + OffNameLength), n);
    }

    /// <summary>What the lane is doing: inside a grammar (which, and for how long) or not. Any thread.</summary>
    public (bool Inside, string? Grammar, string? Identity, TimeSpan Elapsed) Current
    {
        get
        {
            if (_disposed || Volatile.Read(ref *(int*)(_p + OffState)) == 0) return (false, null, null, TimeSpan.Zero);
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref *(long*)(_p + OffEntered)));
            var (grammar, identity) = ReadName(_p);
            return (true, grammar, identity, elapsed);
        }
    }

    private static (string? Grammar, string? Identity) ReadName(byte* p)
    {
        var n = Math.Clamp(Volatile.Read(ref *(int*)(p + OffNameLength)), 0, MaxName);
        if (n == 0) return (null, null);
        var record = Encoding.UTF8.GetString(p + OffName, n);
        var bar = record.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? (record, null) : (record[..bar], record[(bar + 1)..]);
    }

    /// <summary>
    /// The grammars that killed earlier processes: every <c>*.flight</c> file in <paramref name="directory"/> whose process is gone and that was
    /// inside a grammar when it went. Their files are deleted (each crash is reported once); files of live processes are left alone.
    /// </summary>
    public static IReadOnlyList<(string Grammar, string Identity)> CollectCrashes(string directory)
    {
        var crashes = new List<(string, string)>();
        string[] files;
        try
        {
            if (!Directory.Exists(directory)) return crashes;
            files = Directory.GetFiles(directory, "*.flight");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return crashes;
        }
        foreach (var file in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length < Size) continue;
                int pid, state;
                long start;
                (string?, string?) name;
                fixed (byte* p = bytes)
                {
                    if (*(int*)(p + OffMagic) != Magic || *(int*)(p + OffVersion) != FormatVersion) continue;
                    pid = *(int*)(p + OffPid);
                    state = *(int*)(p + OffState);
                    start = *(long*)(p + OffProcessStart);
                    name = ReadName(p);
                }
                if (pid == Environment.ProcessId || IsAlive(pid, start)) continue;
                if (state != 0 && name is (string g, string id)) crashes.Add((g, id));
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // another process is collecting the same file, or it is being written: leave it
            }
        }
        return crashes;
    }

    private static bool IsAlive(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == startTicks || startTicks == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static long ProcessStartTicks()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A clean shutdown: the file goes away (nothing to report next time). Call it on the lane that writes (or when that lane is gone): the
    /// mapping is released here.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file!.Dispose();
            try
            {
                if (_path is not null) File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        else if (_pin.IsAllocated) _pin.Free();
    }

    /// <summary>Ends a <see cref="Enter"/>: restores the previous state.</summary>
    public readonly struct Scope(FlightRecorder? owner, string? previous) : IDisposable
    {
        public void Dispose() => owner?.Restore(previous);
    }
}
