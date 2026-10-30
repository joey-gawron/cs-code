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
///
/// Whose file is whose: the owner keeps its file open with a SHARED lock (<c>flock</c>; on Windows, the open handle itself) for as long as
/// it lives, and the kernel drops that lock when the process ends, however it ends. Another process collects a file only when it can take
/// the EXCLUSIVE lock, i.e. when its owner is gone. (Comparing the pid's start time does not work across processes on Linux: .NET derives
/// it from a boot time each process computes for itself, so a live process always looked dead to another one, which deleted its file and
/// counted a crash against a healthy grammar.)
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
    private readonly FileStream? _owner; // held open (and locked) for the recorder's life: "my process is alive"
    private readonly byte[]? _memory;
    private GCHandle _pin;
    private readonly byte* _p;
    private readonly string? _path;
    private readonly object _readGate = new(); // Current (the watchdog's thread) against Dispose (the lane): never read an unmapped page
    private static int _instances;
    private string? _current;
    private volatile bool _disposed;

    private FlightRecorder(string? path, MemoryMappedFile? file, MemoryMappedViewAccessor? view, FileStream? owner = null)
    {
        _path = path;
        _file = file;
        _view = view;
        _owner = owner;
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
        FileStream? owner = null;
        try
        {
            Directory.CreateDirectory(directory);
            // a second recorder in the same process and directory (a second composition: tests) must not share the first one's file
            var instance = Interlocked.Increment(ref _instances);
            if (suffix is null && File.Exists(Path.Combine(directory, Environment.ProcessId + ".flight"))) suffix = "i" + instance;
            var path = Path.Combine(directory, Environment.ProcessId + (suffix is null ? "" : "-" + suffix) + ".flight");
            owner = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            owner.SetLength(Size);
            OwnerLock.HoldShared(owner);
            var file = MemoryMappedFile.CreateFromFile(owner, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            var view = file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            return new FlightRecorder(path, file, view, owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            owner?.Dispose();
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
            lock (_readGate)
            {
                if (_disposed || Volatile.Read(ref *(int*)(_p + OffState)) == 0) return (false, null, null, TimeSpan.Zero);
                var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref *(long*)(_p + OffEntered)));
                var (grammar, identity) = ReadName(_p);
                return (true, grammar, identity, elapsed);
            }
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
                var bytes = new byte[Size];
                bool ownerCertainlyGone;
                using (var stream = OwnerLock.OpenIfOwnerGone(file, out ownerCertainlyGone))
                {
                    if (stream is null) continue; // its owner holds it: a live process
                    if (stream.ReadAtLeast(bytes, Size, throwOnEndOfStream: false) < Size) continue;
                }
                int pid, state;
                (string?, string?) name;
                fixed (byte* p = bytes)
                {
                    if (*(int*)(p + OffMagic) != Magic || *(int*)(p + OffVersion) != FormatVersion) continue;
                    pid = *(int*)(p + OffPid);
                    state = *(int*)(p + OffState);
                    name = ReadName(p);
                }
                if (pid == Environment.ProcessId) continue;
                // where locks cannot say (a file system without them), a file whose pid runs is left alone: a crash counted late at worst,
                // never a live process's file taken away
                if (!ownerCertainlyGone && PidExists(pid)) continue;
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

    private static bool PidExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
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
        lock (_readGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file!.Dispose();
            _owner?.Dispose(); // releases the owner's lock; the file goes right after
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

/// <summary>
/// "Is the process that owns this flight file still alive?", answered by the kernel: the owner holds a shared <c>flock</c> on its file for its
/// whole life (dropped by the kernel when the process ends, crash or not; descriptors are close-on-exec, so no child process inherits it), and
/// a collector may take the file only with an exclusive one. On Windows the owner's open handle plays the lock (a collector opens without
/// sharing).
/// </summary>
internal static partial class OwnerLock
{
    private const int LockShared = 1, LockExclusive = 2, LockNonBlocking = 4;

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int fd, int operation);

    private static int WouldBlock => OperatingSystem.IsLinux() ? 11 : 35; // EWOULDBLOCK (= EAGAIN): Linux 11, macOS/BSD 35

    /// <summary>The owner's side: a shared lock on its open file (.NET takes one too on most file systems; this makes it explicit).</summary>
    public static void HoldShared(FileStream owner)
    {
        if (OperatingSystem.IsWindows()) return; // the handle itself
        try
        {
            _ = Flock((int)owner.SafeFileHandle.DangerousGetHandle(), LockShared | LockNonBlocking);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// The collector's side: <paramref name="file"/> opened for reading when its owner is gone, or null while the owner lives.
    /// <paramref name="certain"/> is false when the file system could not say (no locks there): the caller then checks the pid.
    /// </summary>
    public static FileStream? OpenIfOwnerGone(string file, out bool certain)
    {
        certain = true;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return null; // open by its (live) owner
            }
        }
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            if (Flock((int)stream.SafeFileHandle.DangerousGetHandle(), LockExclusive | LockNonBlocking) == 0) return stream;
            if (Marshal.GetLastPInvokeError() == WouldBlock)
            {
                stream.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        certain = false;
        return stream;
    }
}
