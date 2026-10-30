using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace CsCode.Platform.Watching;

// macOS only. FSEvents (what FileSystemWatcher uses on macOS) has two problems the watcher cannot live with:
//  * its item flags are sticky: a flag stays on a path until FSEvents has reported that path to some client, so a file created before the
//    stream started still says "created" when it is modified, every creation also says "modified", and FileSystemWatcher raises Changed and
//    Created (and Deleted) for one event, in that order — which the coalescer merged into Changed for every new file. FsEventsReconciler
//    classifies each reported path from what is on disk and what the watch has seen instead (as VS Code's parcel watcher does);
//  * a stream is always the whole subtree: a non-recursive watch of ~ (for ~/.zshrc) would stream every event under ~/Library into the
//    process. KqueueDirectoryBackend watches one directory and its files with kqueue instead.

/// <summary>The OS watch of one root: a <see cref="FileSystemWatcher"/>, except for a non-recursive watch on macOS (kqueue, files-9).</summary>
internal static class OsWatchBackend
{
    public static IWatchBackend Create(string root, bool recursive, RawSink sink, Action<Exception> onFatal, TimeSpan pollInterval) =>
        OperatingSystem.IsMacOS() && !recursive ? new KqueueDirectoryBackend(root, sink, pollInterval) : new FswBackend(root, recursive, sink, onFatal);
}

/// <summary>What <c>lstat</c> says about one path: enough to tell a new, changed or replaced entry from the one seen before.</summary>
internal readonly record struct MacEntry(bool IsDirectory, bool IsLink, ulong Inode, long MTimeNs, long Size, long BirthNs);

/// <summary>The libc calls of the macOS backends: <c>lstat</c>, <c>open(O_EVTONLY)</c>, <c>close</c>, <c>kqueue</c> and <c>kevent</c>.</summary>
internal static class MacWatchNative
{
    public const uint NoteDelete = 0x1, NoteWrite = 0x2, NoteExtend = 0x4, NoteAttrib = 0x8, NoteLink = 0x10, NoteRename = 0x20, NoteRevoke = 0x40;
    public const short EvfiltVnode = -4;
    public const ushort EvAdd = 0x1, EvEnable = 0x4, EvClear = 0x20;
    private const int OEvtOnly = 0x8000, OCloexec = 0x1000000;
    private const int Eintr = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEvent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint FFlags;
        public nint Data;
        public nint UData;
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int LStatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer); // Intel macOS: the 64-bit-inode lstat

    [DllImport("libc", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetAttrList([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [In] byte[] attrList, [Out] byte[] buffer, nuint size, uint options);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close")]
    public static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "kqueue", SetLastError = true)]
    public static extern int KQueue();

    [DllImport("libc", EntryPoint = "kevent", SetLastError = true)]
    public static extern int KEventCall(int kq, [In] KEvent[]? changes, int nchanges, [Out] KEvent[]? events, int nevents, IntPtr timeout);

    public static bool Interrupted => Marshal.GetLastPInvokeError() == Eintr;

    /// <summary>The entry at <paramref name="path"/> itself (a symlink is not followed), or null when there is none (or it cannot be read).</summary>
    public static MacEntry? TryLStat(string path)
    {
        // struct stat (64-bit inodes): st_mode u16 @4, st_ino u64 @8, st_mtimespec @48, st_birthtimespec @80 (each tv_sec i64 + tv_nsec i64),
        // st_size i64 @96; 144 bytes in all.
        var buffer = new byte[144];
        try
        {
            var rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatInode64(path, buffer) : LStat(path, buffer);
            if (rc != 0) return null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
        static long Ns(byte[] b, int at) => BitConverter.ToInt64(b, at) * 1_000_000_000 + BitConverter.ToInt64(b, at + 8);
        var mode = BitConverter.ToUInt16(buffer, 4);
        return new MacEntry((mode & 0xF000) == 0x4000, (mode & 0xF000) == 0xA000, BitConverter.ToUInt64(buffer, 8), Ns(buffer, 48), BitConverter.ToInt64(buffer, 96), Ns(buffer, 80));
    }

    /// <summary>
    /// The name of the entry at <paramref name="path"/> as its volume stores it (a symlink is not followed), or null when it cannot be read. On a
    /// case-insensitive volume (APFS's default) <c>lstat</c> of <c>a.txt</c> finds <c>A.txt</c> too: this tells them apart.
    /// </summary>
    public static string? TryStoredName(string path)
    {
        // struct attrlist { u16 bitmapcount = ATTR_BIT_MAP_COUNT (5); u16 reserved; u32 commonattr = ATTR_CMN_NAME (1); u32 vol, dir, file, fork }.
        // The buffer: u32 length, then an attrreference_t (i32 offset from itself, u32 length with the NUL) and the UTF-8 name.
        var attrList = new byte[24];
        attrList[0] = 5;
        attrList[4] = 1;
        var buffer = new byte[4 + 8 + 1024];
        try
        {
            if (GetAttrList(path, attrList, buffer, (nuint)buffer.Length, 1 /* FSOPT_NOFOLLOW */) != 0) return null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
        var start = 4 + BitConverter.ToInt32(buffer, 4);
        var length = (int)BitConverter.ToUInt32(buffer, 8) - 1;
        if (start < 12 || length < 0 || start + length > buffer.Length) return null;
        return System.Text.Encoding.UTF8.GetString(buffer, start, length);
    }

    /// <summary>An event-only descriptor for <paramref name="path"/> (it never keeps a volume from being unmounted), or -1.</summary>
    public static int OpenEventOnly(string path)
    {
        try { return Open(path, OEvtOnly | OCloexec); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return -1; }
    }

    public static long UnixNowNs() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
}

/// <summary>
/// One kqueue and one thread for every vnode watch of the process. A watch is an event-only descriptor of a file or directory; its handler
/// runs on the kqueue thread with the NOTE_* flags that fired, so handlers only record what happened and hand the work to the pool.
/// </summary>
internal static class KqueueHub
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<long, (string Path, Action<uint> Handler)> Handlers = new();
    private static int _kq = -1;
    private static long _nextId;

    /// <summary>A vnode watch; disposing it closes the descriptor, which removes it from the kqueue.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly long _id;
        private int _fd;
        internal Registration(long id, int fd) { _id = id; _fd = fd; }

        public void Dispose()
        {
            Handlers.TryRemove(_id, out _);
            var fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0) MacWatchNative.Close(fd);
        }
    }

    /// <summary>Watches <paramref name="path"/> for <paramref name="notes"/> (NOTE_* flags); null when it cannot be opened (gone, no access, no descriptors left).</summary>
    public static Registration? Watch(string path, uint notes, Action<uint> handler)
    {
        var kq = Start();
        if (kq < 0) return null;
        var fd = MacWatchNative.OpenEventOnly(path);
        if (fd < 0) return null;
        var id = Interlocked.Increment(ref _nextId);
        Handlers[id] = (path, handler);
        var change = new[]
        {
            new MacWatchNative.KEvent
            {
                Ident = (nuint)fd, Filter = MacWatchNative.EvfiltVnode, Flags = MacWatchNative.EvAdd | MacWatchNative.EvEnable | MacWatchNative.EvClear,
                FFlags = notes, UData = (nint)id,
            },
        };
        if (MacWatchNative.KEventCall(kq, change, 1, null, 0, IntPtr.Zero) < 0)
        {
            Handlers.TryRemove(id, out _);
            MacWatchNative.Close(fd);
            return null;
        }
        return new Registration(id, fd);
    }

    /// <summary>Live vnode watches on <paramref name="path"/> (tests).</summary>
    internal static int WatchesOn(string path) => Handlers.Values.Count(h => h.Path == path);

    private static int Start()
    {
        if (Volatile.Read(ref _kq) >= 0) return _kq;
        lock (Gate)
        {
            if (_kq >= 0) return _kq;
            int kq;
            try { kq = MacWatchNative.KQueue(); }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return -1; }
            if (kq < 0) return -1;
            new Thread(() => Run(kq)) { IsBackground = true, Name = "cscode.kqueue" }.Start();
            Volatile.Write(ref _kq, kq);
            return kq;
        }
    }

    private static void Run(int kq)
    {
        var events = new MacWatchNative.KEvent[64];
        while (true)
        {
            var n = MacWatchNative.KEventCall(kq, null, 0, events, events.Length, IntPtr.Zero);
            if (n < 0)
            {
                if (MacWatchNative.Interrupted) continue;
                Thread.Sleep(100); // should not happen; never spin
                continue;
            }
            for (var i = 0; i < n; i++)
            {
                if (!Handlers.TryGetValue(events[i].UData, out var watch)) continue; // disposed meanwhile
                try { watch.Handler(events[i].FFlags); }
                catch (Exception) { /* a handler only records and queues; never let one take the thread down */ }
            }
        }
    }
}

/// <summary>
/// macOS: a non-recursive watch of one directory with kqueue (files-9): a vnode watch on the directory (entries added, removed, renamed; the
/// directory itself deleted or moved) and one on each file in it (written, extended, truncated), compared with a snapshot of the directory
/// to tell created, changed and deleted apart. Nothing below the directory reaches the process, unlike an FSEvents stream, which is always
/// the whole subtree. Files beyond the descriptor budget (<see cref="MaxWatchedFiles"/> per directory, <see cref="MaxWatchedFilesInAll"/>
/// in all) are compared every poll interval instead. When the directory goes (deleted, moved away) its entries are reported deleted, and
/// the path is looked at every poll interval: the directory there, once there is one again, is watched and its content reported created.
/// </summary>
internal sealed class KqueueDirectoryBackend : IWatchBackend
{
    public const int MaxWatchedFiles = 256;
    public const int MaxWatchedFilesInAll = 2048;
    private const uint DirectoryNotes = MacWatchNative.NoteWrite | MacWatchNative.NoteLink | MacWatchNative.NoteDelete | MacWatchNative.NoteRename | MacWatchNative.NoteRevoke;
    private const uint FileNotes = MacWatchNative.NoteWrite | MacWatchNative.NoteExtend | MacWatchNative.NoteAttrib | MacWatchNative.NoteDelete | MacWatchNative.NoteRename;
    private const uint Gone = MacWatchNative.NoteDelete | MacWatchNative.NoteRename | MacWatchNative.NoteRevoke;

    private static int _watchedFilesInAll;

    private readonly string _root;
    private readonly RawSink _sink;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly Dictionary<string, KqueueHub.Registration> _fileWatches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingFiles = new(StringComparer.Ordinal);
    private Dictionary<string, MacEntry> _entries = new(StringComparer.Ordinal);
    private KqueueHub.Registration? _directoryWatch;
    private Timer? _timer;          // polls the files without a watch of their own, or the path while the directory is gone
    private bool _scanAll;
    private bool _directoryGone;
    private bool _waitingForDirectory;
    private bool _working;
    private bool _disposed;

    public KqueueDirectoryBackend(string root, RawSink sink, TimeSpan pollInterval)
    {
        _root = root;
        _sink = sink;
        _pollInterval = pollInterval;
        lock (_gate)
        {
            if (!ArmDirectory()) throw new DirectoryNotFoundException($"Cannot watch '{root}'");
            _entries = List() ?? new(StringComparer.Ordinal);
            foreach (var (name, entry) in _entries) if (NeedsWatch(entry)) WatchFile(name);
            UpdateTimer();
        }
    }

    /// <summary>Files of this directory with a vnode watch of their own (tests).</summary>
    internal int WatchedFiles { get { lock (_gate) return _fileWatches.Count; } }

    // Under _gate. The watch goes first: whatever changes after it is seen, at worst also in the listing that follows.
    private bool ArmDirectory()
    {
        _directoryWatch = KqueueHub.Watch(_root, DirectoryNotes, notes => OnEvent(null, notes));
        return _directoryWatch is not null;
    }

    // A file's content changes in place, which its directory does not see. (A symlink is not followed: re-pointing one replaces it, which the
    // directory sees; writes to its target are the target directory's business, as with inotify.)
    private static bool NeedsWatch(MacEntry entry) => !entry.IsDirectory && !entry.IsLink;

    // Under _gate.
    private void WatchFile(string name)
    {
        if (_fileWatches.ContainsKey(name)) return;
        if (_fileWatches.Count >= MaxWatchedFiles) return;
        if (Interlocked.Increment(ref _watchedFilesInAll) > MaxWatchedFilesInAll) { Interlocked.Decrement(ref _watchedFilesInAll); return; }
        var registration = KqueueHub.Watch(Path.Join(_root, name), FileNotes, notes => OnEvent(name, notes));
        if (registration is null) { Interlocked.Decrement(ref _watchedFilesInAll); return; }
        _fileWatches[name] = registration;
    }

    // Under _gate.
    private void UnwatchFile(string name)
    {
        if (!_fileWatches.Remove(name, out var registration)) return;
        registration.Dispose();
        Interlocked.Decrement(ref _watchedFilesInAll);
    }

    // Under _gate: poll while some file has no watch, or while the directory is gone.
    private void UpdateTimer()
    {
        var poll = _waitingForDirectory || _entries.Any(e => NeedsWatch(e.Value) && !_fileWatches.ContainsKey(e.Key));
        if (poll && _timer is null) _timer = new Timer(static s => ((KqueueDirectoryBackend)s!).OnEvent(null, 0), this, _pollInterval, _pollInterval);
        else if (!poll && _timer is not null) { _timer.Dispose(); _timer = null; }
    }

    // On the kqueue thread or a timer: record what to look at and let one pool item do the looking.
    private void OnEvent(string? file, uint notes)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (file is null)
            {
                _scanAll = true;
                if ((notes & Gone) != 0) _directoryGone = true;
            }
            else
            {
                _pendingFiles.Add(file);
                if ((notes & Gone) != 0) _scanAll = true; // replaced or removed: the directory's own event says which
            }
            if (_working) return;
            _working = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static s => ((KqueueDirectoryBackend)s!).Work(), this);
    }

    private void Work()
    {
        while (true)
        {
            var changes = new List<(RawKind Kind, string Path)>();
            lock (_gate)
            {
                if (_disposed || (!_scanAll && !_directoryGone && _pendingFiles.Count == 0)) { _working = false; return; }
                if (_directoryGone || _waitingForDirectory) LookAtDirectory(changes);
                else if (_scanAll) ScanAll(changes);
                else foreach (var name in _pendingFiles) ScanFile(name, changes);
                _scanAll = false;
                _directoryGone = false;
                _pendingFiles.Clear();
            }
            foreach (var (kind, path) in changes) _sink(kind, path, null);
        }
    }

    // Under _gate: the directory was deleted or moved away (or is still gone): report what it held, then watch whatever is at the path now.
    private void LookAtDirectory(List<(RawKind, string)> changes)
    {
        if (!_waitingForDirectory)
        {
            foreach (var name in _fileWatches.Keys.ToArray()) UnwatchFile(name);
            _directoryWatch?.Dispose();
            _directoryWatch = null;
            // (Not the directory itself: its deletion would hide those of its entries, reported a moment ago or now. The owner looks at the
            // path again after deletions; when there were none, this backend's own look at the path below finds the directory made again.)
            foreach (var name in _entries.Keys) changes.Add((RawKind.Deleted, Path.Join(_root, name)));
            _entries = new(StringComparer.Ordinal);
            _waitingForDirectory = true;
        }
        if (Directory.Exists(_root) && ArmDirectory())
        {
            _waitingForDirectory = false;
            ScanAll(changes); // everything in the new directory is new
        }
        UpdateTimer();
    }

    // Under _gate.
    private void ScanAll(List<(RawKind, string)> changes)
    {
        var current = List();
        if (current is null) { _directoryGone = true; LookAtDirectory(changes); return; }
        foreach (var (name, now) in current)
        {
            var path = Path.Join(_root, name);
            if (!_entries.TryGetValue(name, out var before)) changes.Add((RawKind.Created, path));
            else if (before.Inode != now.Inode || before.IsDirectory != now.IsDirectory)
            {
                // replaced (an atomic save, a directory deleted and made again): its watch is on the old file
                changes.Add((RawKind.Deleted, path));
                changes.Add((RawKind.Created, path));
                UnwatchFile(name);
            }
            else if (!now.IsDirectory && (before.MTimeNs != now.MTimeNs || before.Size != now.Size)) changes.Add((RawKind.Changed, path));
            if (NeedsWatch(now)) WatchFile(name);
        }
        foreach (var name in _entries.Keys)
        {
            if (current.ContainsKey(name)) continue;
            changes.Add((RawKind.Deleted, Path.Join(_root, name)));
            UnwatchFile(name);
        }
        _entries = current;
        UpdateTimer();
    }

    // Under _gate: a watched file was written to. Anything else (gone, replaced) is the directory's business.
    private void ScanFile(string name, List<(RawKind, string)> changes)
    {
        if (!_entries.TryGetValue(name, out var before)) return;
        var path = Path.Join(_root, name);
        var now = MacWatchNative.TryLStat(path);
        if (now is not { } e || e.Inode != before.Inode || e.IsDirectory != before.IsDirectory) { ScanAll(changes); return; }
        if (e.MTimeNs == before.MTimeNs && e.Size == before.Size) return; // attributes only
        _entries[name] = e;
        changes.Add((RawKind.Changed, path));
    }

    /// <summary>The directory's entries by name, or null when it is not there.</summary>
    private Dictionary<string, MacEntry>? List()
    {
        var result = new Dictionary<string, MacEntry>(StringComparer.Ordinal);
        try
        {
            var names = new FileSystemEnumerable<string>(_root, static (ref FileSystemEntry e) => e.FileName.ToString(),
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false });
            foreach (var name in names)
                if (MacWatchNative.TryLStat(Path.Join(_root, name)) is { } entry) result[name] = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var name in _fileWatches.Keys.ToArray()) UnwatchFile(name);
            _directoryWatch?.Dispose();
            _directoryWatch = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>
/// macOS: turns what FileSystemWatcher raises for an FSEvents stream into true created/changed/deleted events. FSEvents flags are sticky (see
/// the top of this file), so the kind FileSystemWatcher raised is only a hint: each path is looked at (<c>lstat</c>, and the name as the volume
/// stores it, so that a case-only rename on a case-insensitive volume is a deletion of the old spelling) and compared with what the watch knows
/// — a snapshot of the tree's names taken when it was armed (bounded, built-in excludes pruned), plus every path reported since. A path that
/// exists is <b>created</b> when the watch has not seen it and it was not there when the watch was armed (or, where the snapshot did not reach,
/// was born after that), else <b>changed</b> (once per change: the other flags of the same event are dropped). A path that is gone is
/// <b>deleted</b>, unless the snapshot shows it was not there and the watch never saw it: then it came and went unseen and is not reported.
/// Where the snapshot does not reach, an unknown path that is gone is reported deleted (it may have existed). The tree holds at most
/// <see cref="MaxKnownPaths"/> paths: past that, the paths known longest (the snapshot's first) are forgotten to make room for the ones
/// reported now, and a forgotten path born after the watch was armed moves the "born after" line past its birth, so it is not created again.
/// </summary>
internal sealed class FsEventsReconciler
{
    public const int MaxKnownPaths = 20_000;

    private sealed class Node
    {
        public Dictionary<string, Node>? Children;
        public bool Listed;          // every entry the directory had when the watch was armed is a child (and none was dropped since)
        public bool Placeholder;     // only here to hold children: the watch has not seen this path itself
        public bool Detached;        // no longer in the tree (deleted, replaced, forgotten)
        public MacEntry? Seen;       // what lstat said when the watch last reported it (null: known from the snapshot only)
    }

    private readonly string _root;
    private readonly RawSink _sink;
    private readonly Func<string, bool> _isPruned;
    private readonly int _maxKnownPaths;
    private readonly object _gate = new();
    private readonly Queue<(Node Parent, string Name, Node Node)> _known = new(); // in the order they became known: the first to forget
    private long _armedAtNs;
    private long _bornAfterNs;   // an unknown path born after this is new: when the watch was armed, or later once paths born since were forgotten
    private Node _tree = new();
    private int _count;
    private bool _trustListing = true;

    public FsEventsReconciler(string root, RawSink sink, Func<string, bool> isPruned, int maxKnownPaths = MaxKnownPaths)
    {
        _root = root.Length > 1 ? root.TrimEnd('/') : root;
        _sink = sink;
        _isPruned = isPruned;
        _maxKnownPaths = maxKnownPaths;
        Snapshot();
    }

    // Under _gate (or in the constructor).
    private void Snapshot()
    {
        _armedAtNs = MacWatchNative.UnixNowNs(); // before the listing: whatever is born after this was not in it
        _bornAfterNs = _armedAtNs;
        Reset();
        _trustListing = true;
        var queue = new Queue<(string Path, Node Node)>();
        queue.Enqueue((_root, _tree));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        while (queue.Count > 0)
        {
            var (dir, node) = queue.Dequeue();
            node.Children ??= new(StringComparer.Ordinal);
            var complete = true;
            try
            {
                var entries = new FileSystemEnumerable<(string Name, bool IsDir, bool Link)>(dir,
                    static (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), options);
                foreach (var (name, isDir, link) in entries)
                {
                    if (_count >= _maxKnownPaths) { complete = false; break; }
                    var child = Add(node, name, new Node());
                    var path = dir + "/" + name;
                    if (isDir && !link && !_isPruned(path)) queue.Enqueue((path, child));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
            node.Listed = complete;
            if (_count >= _maxKnownPaths) break; // the directories still queued stay unlisted
        }
    }

    // Under _gate: forgets the whole tree.
    private void Reset()
    {
        _tree = new Node();
        _count = 0;
        _known.Clear();
    }

    // Under _gate.
    private Node Add(Node parent, string name, Node node)
    {
        (parent.Children ??= new(StringComparer.Ordinal))[name] = node;
        _count++;
        _known.Enqueue((parent, name, node));
        return node;
    }

    // Under _gate: `node` and everything below it left the tree.
    private void Detach(Node node)
    {
        node.Detached = true;
        _count--;
        if (node.Children is not null) foreach (var child in node.Children.Values) Detach(child);
    }

    // Under _gate: true when there is room for one more path, after forgetting the paths known longest if need be.
    private bool HasRoom()
    {
        while (_count >= _maxKnownPaths)
            if (!ForgetOne()) return false;
        if (_known.Count > 2 * _maxKnownPaths)
        {
            // most are paths deleted since: drop them, so that a busy watch never grows the queue
            var live = _known.Where(k => !k.Node.Detached).ToArray();
            _known.Clear();
            foreach (var k in live) _known.Enqueue(k);
        }
        return true;
    }

    // Under _gate: forgets the path known longest that holds nothing (a directory's children go first).
    private bool ForgetOne()
    {
        while (_known.TryDequeue(out var k))
        {
            if (k.Node.Detached || k.Node.Children is { Count: > 0 }) continue;
            k.Parent.Children!.Remove(k.Name);
            k.Parent.Listed = false; // the listing no longer proves an unknown path there is new
            Detach(k.Node);
            // An event for it later finds an unknown path: born after this line it would be created again, so the line moves past its birth.
            if (k.Node.Seen is { } seen && seen.BirthNs > _bornAfterNs) _bornAfterNs = seen.BirthNs;
            return true;
        }
        return false;
    }

    /// <summary>A raw event from FileSystemWatcher. Called on its thread (and on the kqueue thread for the root): serialised here.</summary>
    public void Handle(RawKind kind, string path, string? oldPath)
    {
        var changes = new List<(RawKind Kind, string Path)>(2);
        lock (_gate)
        {
            if (kind == RawKind.Renamed && oldPath is not null)
            {
                Classify(oldPath, RawKind.Deleted, changes);
                Classify(path, RawKind.Renamed, changes);
            }
            else Classify(path, kind, changes);
        }
        foreach (var (k, p) in changes) _sink(k, p, null);
    }

    /// <summary>Events were dropped (FSEvents overflow, MustScanSubDirs): the snapshot no longer proves what was not there.</summary>
    public void Overflowed()
    {
        lock (_gate) _trustListing = false;
    }

    /// <summary>
    /// The root directory was deleted or moved away (FileSystemWatcher reports nothing then). Reports everything the watch knew below it as
    /// deleted — not only its top: a directory of the same name in a tree moved there at once is reported created again, which with this
    /// deletion reads as replaced, and what the old one held must be reported gone by itself then — so the owner looks at the path again.
    /// Until <see cref="RootFound"/>, nothing is known about the path.
    /// </summary>
    public void RootLost()
    {
        var changes = new List<(RawKind Kind, string Path)>();
        lock (_gate)
        {
            Report(_tree, _root, changes);
            Reset();
        }
        foreach (var (k, p) in changes) _sink(k, p, null);

        static void Report(Node node, string path, List<(RawKind, string)> changes)
        {
            if (node.Children is null) return;
            foreach (var (name, child) in node.Children)
            {
                var childPath = path + "/" + name;
                if (!child.Placeholder) changes.Add((RawKind.Deleted, childPath));
                Report(child, childPath, changes);
            }
        }
    }

    /// <summary>
    /// A directory is at the root's path again after <see cref="RootLost"/> (the stream follows the path, so it reports what happens in it):
    /// what it holds was not seen arriving — a directory made there and filled, or a whole tree moved there — so it is reported created (its
    /// subdirectories' content by the owner, which looks inside every directory reported created), and the snapshot is taken again.
    /// </summary>
    public void RootFound()
    {
        var changes = new List<(RawKind Kind, string Path)>();
        lock (_gate)
        {
            Snapshot();
            if (_tree.Children is { } top) foreach (var name in top.Keys) changes.Add((RawKind.Created, _root + "/" + name));
        }
        foreach (var (k, p) in changes) _sink(k, p, null);
    }

    // Under _gate.
    private void Classify(string path, RawKind hint, List<(RawKind, string)> changes)
    {
        var segments = Segments(path);
        if (segments is null) { changes.Add((hint == RawKind.Renamed ? RawKind.Created : hint, path)); return; } // the root itself, or not below it
        var parent = Find(segments, segments.Length - 1, create: false);
        var name = segments[^1];
        Node? node = null;
        parent?.Children?.TryGetValue(name, out node);
        var parentListed = _trustListing && parent is { Listed: true, Placeholder: false };

        if (MacWatchNative.TryLStat(path) is not { } now || !IsStoredAs(path, name))
        {
            if (node is not null)
            {
                parent!.Children!.Remove(name);
                Detach(node);
                changes.Add((RawKind.Deleted, path));
            }
            else if (!parentListed) changes.Add((RawKind.Deleted, path)); // it may have been there: say so (at worst once too often)
            return;                                                         // else it came and went unseen
        }

        if (node is { Placeholder: false })
        {
            if (node.Seen == now) return; // another flag of an event already reported
            var replaced = node.Seen is { } before && (before.Inode != now.Inode || before.IsDirectory != now.IsDirectory);
            node.Seen = now;
            if (replaced)
            {
                // deleted and made again under the same name: what the watch knew below it is gone
                if (node.Children is not null) foreach (var child in node.Children.Values) Detach(child);
                node.Children = null;
                node.Listed = false;
                changes.Add((RawKind.Deleted, path));
                changes.Add((RawKind.Created, path));
            }
            else changes.Add((RawKind.Changed, path));
            return;
        }

        var created = parentListed || hint == RawKind.Renamed || now.BirthNs > _bornAfterNs;
        changes.Add((created ? RawKind.Created : RawKind.Changed, path));
        if (node is not null) { node.Placeholder = false; node.Seen = now; return; }
        if (!HasRoom()) { if (parent is not null) parent.Listed = false; return; }
        if (parent is { Detached: true }) parent = null; // forgotten to make room: find (or make) it again
        parent ??= Find(segments, segments.Length - 1, create: true);
        if (parent is null) return;
        Add(parent, name, new Node { Seen = now });
    }

    // A case-only rename on a case-insensitive volume leaves lstat of the old spelling finding the renamed entry: that spelling is gone.
    private static bool IsStoredAs(string path, string name) =>
        MacWatchNative.TryStoredName(path) is not { } stored || stored == name
        || string.Equals(stored.Normalize(), name.Normalize(), StringComparison.Ordinal); // (APFS keeps either Unicode form; both find it)

    private string[]? Segments(string path)
    {
        if (path.Length <= _root.Length + 1 || !path.StartsWith(_root, StringComparison.Ordinal) || path[_root.Length] != '/') return null;
        return path[(_root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    // Under _gate: the node of the first `depth` segments; with `create`, missing directories on the way are added as placeholders.
    private Node? Find(string[] segments, int depth, bool create)
    {
        var node = _tree;
        for (var i = 0; i < depth; i++)
        {
            Node? next = null;
            if (node.Children?.TryGetValue(segments[i], out next) != true || next is null)
            {
                if (!create || !HasRoom() || node.Detached) return null;
                next = Add(node, segments[i], new Node { Placeholder = true });
            }
            node = next;
        }
        return node;
    }
}

/// <summary>
/// macOS, with <see cref="FswBackend"/>: the <see cref="FsEventsReconciler"/> of one stream, and a vnode watch on its root directory — FSEvents
/// (through FileSystemWatcher) reports nothing when the root itself is deleted or moved away. The watch is armed again, and the content of the
/// directory found there announced, at once when one is already back at the path, else as soon as one arrives there: the root's parent is
/// watched (kqueue) while the root is gone, since the stream says nothing about a directory moved to its own path (it follows the path, so
/// it reports what happens in that directory later, but a tree moved there and left alone would never be announced). The root's loss and
/// the announcement after it are reported in that order, whole, whichever threads they come from: a name the old and the new directory
/// share must reach the owner deleted then created (replaced), never created then deleted (nothing at all).
/// </summary>
internal sealed class MacRootWatch : IDisposable
{
    private const uint Gone = MacWatchNative.NoteDelete | MacWatchNative.NoteRename | MacWatchNative.NoteRevoke;

    private readonly string _root;
    private readonly object _gate = new(); // held while the loss or the announcement is reported
    private KqueueHub.Registration? _watch;
    private KqueueHub.Registration? _parentWatch; // while the root is lost and nothing is back at its path
    private int _generation;               // of _watch: a loss reported by a watch replaced since is stale
    private bool _lost;
    private bool _disposed;

    public MacRootWatch(string root, RawSink sink)
    {
        _root = root;
        var pruned = new ExcludeMatcher(root, ExcludeMatcher.BuiltInWatcherExcludes);
        Reconciler = new FsEventsReconciler(root, sink, pruned.IsExcluded);
        EnsureWatched();
    }

    public FsEventsReconciler Reconciler { get; }

    /// <summary>A raw event of the stream: a root lost before it is watched (and announced) again first, then the event is classified.</summary>
    public void Handle(RawKind kind, string path, string? oldPath)
    {
        EnsureWatched();
        Reconciler.Handle(kind, path, oldPath);
    }

    private void EnsureWatched()
    {
        if (Volatile.Read(ref _watch) is not null) return;
        lock (_gate)
        {
            if (_disposed || _watch is not null) return;
            if (!TryWatchRoot())
            {
                if (!_lost || _parentWatch is not null) return;
                // Nothing at the path yet: whatever is made or moved there, the parent's watch reports (the stream does not). Looked at again
                // once that watch is armed, for a directory that arrived in between.
                _parentWatch = Path.GetDirectoryName(_root) is { Length: > 0 } parent ? KqueueHub.Watch(parent, MacWatchNative.NoteWrite, _ => EnsureWatched()) : null;
                if (!TryWatchRoot()) return;
            }
            _parentWatch?.Dispose();
            _parentWatch = null;
            if (!_lost) return;
            _lost = false;
            Reconciler.RootFound();
        }
    }

    // Under _gate.
    private bool TryWatchRoot()
    {
        var generation = ++_generation;
        _watch = KqueueHub.Watch(_root, Gone, _ => OnRootLost(generation));
        return _watch is not null;
    }

    /// <summary>The root is gone (its watch said so, or the stream asked for a rescan of a root that is not there).</summary>
    public void OnRootLost() => OnRootLost(0);

    private void OnRootLost(int generation)
    {
        lock (_gate)
        {
            if (_disposed || (generation != 0 && generation != _generation)) return;
            _watch?.Dispose();
            _watch = null;
            _lost = true;
            Reconciler.RootLost();
        }
        EnsureWatched(); // already back (replaced in an instant)?
    }

    public void Dispose()
    {
        KqueueHub.Registration? watch, parentWatch;
        lock (_gate)
        {
            _disposed = true;
            watch = _watch;
            _watch = null;
            parentWatch = _parentWatch;
            _parentWatch = null;
        }
        watch?.Dispose();
        parentWatch?.Dispose();
    }
}
