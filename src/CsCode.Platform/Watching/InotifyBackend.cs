using System.Runtime.InteropServices;
using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Platform.Watching;

/// <summary>
/// The Linux backend: one inotify instance per root and one watch per directory, added a directory at a time so that a directory nobody wants
/// the contents of (<see cref="WatchVerdict.Prune"/>: every subscription on the root excludes it, like <c>node_modules/</c>, or it holds a
/// <c>CACHEDIR.TAG</c>, like Cargo's <c>target/</c>) costs no watch, neither when the root is armed nor when the directory appears later. A
/// <see cref="FileSystemWatcher"/> with IncludeSubdirectories watches every directory below its root and drops excluded events only afterwards:
/// one window on a Rust + JS monorepo held 75,892 of the user's 90,112 watches (fs.inotify.max_user_watches), 56,489 of them in <c>target/</c>,
/// and starved every other program on the machine.
/// <list type="bullet">
/// <item><b>Threads</b>: the constructor walks the tree on the caller's thread (the IO lane). A reader thread then waits in poll(2) on the inotify
/// fd and an eventfd; <see cref="Dispose"/> writes the eventfd, and the reader closes both fds and exits (Dispose waits for it). That also holds when
/// the root was deleted first and no watch is left, where a FileSystemWatcher keeps its instance and thread until the process exits.</item>
/// <item><b>Events</b>, as FileSystemWatcher reports them: created, deleted, changed (IN_MODIFY, IN_ATTRIB) and renamed, for files and directories.
/// An IN_MOVED_FROM directly followed by the IN_MOVED_TO with its cookie is one rename; an unpaired FROM (moved out of the tree, or into a
/// directory left out) is a deletion, an unpaired TO a creation. A FROM that ends a read waits up to <see cref="PairingWindowMs"/> for its TO.
/// Events about a watched directory itself are left to its parent's watch, which reports them by name; a left-out directory's own appearance,
/// removal and renaming are reported that way too.</item>
/// <item><b>Directories that come and move</b>: one created or moved in is watched with its subtree before the event that reports it is passed
/// on, so the <see cref="FileWatcher"/>, which lists a reported directory and announces what is already inside, misses nothing created before a
/// watch was in place. A directory renamed inside the tree keeps its watches (only their paths change); one moved out loses them, so its later
/// events are not reported under the old path. The walk reads the disk as it is now while the queue may still describe the past (a name reused
/// at once: <c>mkdir X; mv X Y; mkdir X</c>; an exchange: <c>mv --exchange</c>), so which directory a path names is asked of inotify itself —
/// adding a watch for a path answers with the descriptor of the directory that is there — before a watch is moved to a new path.</item>
/// <item><b>The root going</b> (moved away, or its watch removed by the kernel: deleted, unmounted): the backend drops its watches, so nothing is
/// reported under the old path, and calls <c>onRootLost</c>; the FileWatcher then waits for the path or re-arms on what is there.</item>
/// <item><b>Left-out directories</b>: only the topmost ones are remembered (the walk does not enter them), with the ones watched only for a
/// want (<see cref="WatchVerdict.WatchForWant"/>). When subscriptions or their wants change, <see cref="RevisitPruned"/> watches those wanted now
/// and lets go of those no longer wanted. A <c>CACHEDIR.TAG</c> that appears in a watched directory (written, or renamed into place) drops the
/// watches in and below it: Cargo creates <c>target/</c> with its tag already inside (a temporary directory renamed into place), other tools may
/// write the tag after the first subdirectories.</item>
/// <item><b>Errors</b>: running out of watches (ENOSPC), instances (EMFILE) or kernel memory (ENOMEM) while arming throws an
/// <see cref="IOException"/> (the FileWatcher then polls); the same while adding a directory later stops adding watches, still reports every
/// event of that read, adds <c>Changed(root)</c> (consumers rescan what the missing watches would have reported) and reports the failure once
/// through <c>onFatal</c>. Directories that vanish, turn out not to be directories or cannot be read are skipped; symlinked ones are never entered.
/// IN_Q_OVERFLOW reports <c>Changed(root)</c> and walks the tree again: directories that appeared unseen get watches, and watches the walk does
/// not reach (directories deleted or moved out while events were lost) are removed.</item>
/// </list>
/// </summary>
internal sealed class InotifyBackend : IWatchBackend
{
    /// <summary>How long a FROM that ends a read waits for its TO before it counts as moved out.</summary>
    public const int PairingWindowMs = 10;

    private const int BufferSize = 64 * 1024; // per read; a non-recursive watch (one directory) makes do with a quarter
    private const int HeaderSize = 16; // struct inotify_event { int wd; uint32_t mask, cookie, len; char name[]; }
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    // <sys/inotify.h>, <sys/eventfd.h>, <poll.h>, <errno.h>: the same values on every architecture .NET runs Linux on.
    internal const uint InModify = 0x2, InAttrib = 0x4, InMovedFrom = 0x40, InMovedTo = 0x80, InCreate = 0x100, InDelete = 0x200,
        InMoveSelf = 0x800, InQOverflow = 0x4000, InIgnored = 0x8000, InOnlyDir = 0x0100_0000, InDontFollow = 0x0200_0000,
        InExclUnlink = 0x0400_0000, InIsDir = 0x4000_0000;

    // What FileSystemWatcher asks for (FileName | DirectoryName | LastWrite | Size), plus IN_MOVE_SELF for the root moving away. Not
    // IN_DELETE_SELF: the IN_IGNORED that always follows it says all we need. IN_EXCL_UNLINK: nothing about files already unlinked.
    private const uint WatchMask = InCreate | InDelete | InMovedFrom | InMovedTo | InModify | InAttrib | InMoveSelf | InOnlyDir | InExclUnlink;
    private const int InNonBlock = 0x800, InCloExec = 0x80000, EfdNonBlock = 0x800, EfdCloExec = 0x80000;
    private const short PollIn = 0x1, PollErr = 0x8, PollHup = 0x10, PollNval = 0x20;
    internal const int Enoent = 2, Eintr = 4, Eagain = 11, Enomem = 12, Eacces = 13, Enfile = 23, Emfile = 24, Enotdir = 20, Enospc = 28;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", EntryPoint = "inotify_init1", SetLastError = true)]
    private static extern int InotifyInit1(int flags);

    [DllImport("libc", EntryPoint = "inotify_add_watch", SetLastError = true)]
    private static extern int InotifyAddWatch(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mask);

    [DllImport("libc", EntryPoint = "inotify_rm_watch", SetLastError = true)]
    private static extern int InotifyRmWatch(int fd, int wd);

    [DllImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    private static extern int EventFd(uint initval, int flags);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadFd(int fd, [Out] byte[] buffer, nint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteFd(int fd, ref ulong value, nint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFd(int fd);

    /// <summary>One inotify_event as read(2) returns it; <see cref="Name"/> is empty for an event about the watched directory itself.</summary>
    internal readonly record struct RawEvent(int Wd, uint Mask, uint Cookie, string Name);

    private readonly record struct Output(RawKind Kind, string Path, string? OldPath);

    // A FROM waiting for its TO: resolved when it was read, because the TO may move the directory it names.
    private readonly record struct PendingMove(uint Cookie, string Path, bool IsDir);

    private readonly string _root;
    private readonly bool _recursive;
    private readonly DirectoryVerdict _verdict;
    private readonly RawSink _sink;
    private readonly Action<Exception> _onFatal;
    private readonly Action _onRootLost;
    private readonly object _gate = new();                                       // guards everything below, and the fds' lifetime
    private readonly Dictionary<int, string> _dirs = new();                      // wd → the directory it watches
    private readonly Dictionary<string, int> _wds = new(StringComparer.Ordinal); // directory → wd
    private readonly HashSet<string> _pruned = new(StringComparer.Ordinal);      // topmost directories left out (or not there when last tried)
    // Watched directories whose verdict is not a plain Watch: WatchForWant (only because a subscription wants a path at or below) or Shallow
    // (a cache directory: watched at its top only, so a tag or virtual-environment marker coming or going is seen).
    private readonly Dictionary<string, WatchVerdict> _kept = new(StringComparer.Ordinal);
    private readonly int _fd;
    private readonly int _wakeFd;
    private readonly int _rootWd;
    private readonly Thread? _reader;
    private PendingMove? _pendingFrom;
    private bool _closed;           // both fds closed (by the reader on its way out, by Dispose without a reader, or by a failed constructor)
    private bool _disposeRequested;
    private bool _outOfWatches;     // the OS refused a watch: no more are added (the FileWatcher replaces this backend)
    private int _fatalReported;

    /// <summary>Tests: an errno to fail <c>inotify_add_watch</c> with for a path (0: no fault).</summary>
    internal Func<string, int>? AddWatchFault;

    /// <summary>Arms the watch (walks the tree on this thread). Throws <see cref="DirectoryNotFoundException"/> for a missing root, an <see cref="IOException"/> when the OS limits are reached.</summary>
    public InotifyBackend(string root, bool recursive, DirectoryVerdict verdict, RawSink sink, Action<Exception> onFatal, Action onRootLost)
        : this(root, recursive, verdict, sink, onFatal, onRootLost, startReader: true) { }

    /// <summary><paramref name="startReader"/> false (tests): nothing reads the fd but <see cref="Pump"/> and <see cref="Process"/>.</summary>
    internal InotifyBackend(string root, bool recursive, DirectoryVerdict verdict, RawSink sink, Action<Exception> onFatal, Action onRootLost, bool startReader)
    {
        _root = root;
        _recursive = recursive;
        _verdict = verdict;
        _sink = sink;
        _onFatal = onFatal;
        _onRootLost = onRootLost;

        _fd = InotifyInit1(InNonBlock | InCloExec);
        if (_fd < 0) throw InitError(Marshal.GetLastPInvokeError());
        _wakeFd = EventFd(0, EfdNonBlock | EfdCloExec);
        if (_wakeFd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            CloseFd(_fd);
            throw new IOException($"Cannot create an eventfd to stop the inotify reader: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
        try
        {
            lock (_gate)
            {
                AddTree(root, isRoot: true, WatchVerdict.Watch);
                _rootWd = _wds[root];
            }
            if (startReader)
            {
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "CsCode.inotify" };
                _reader.Start();
            }
        }
        catch
        {
            CloseFds(); // closing the inotify fd drops every watch the walk added
            throw;
        }
    }

    /// <summary>Directories watched right now (tests and diagnostics).</summary>
    internal int WatchCount { get { lock (_gate) return _dirs.Count; } }

    /// <summary>The directories watched right now, under the paths events are reported with (tests and diagnostics).</summary>
    internal string[] WatchedDirectories { get { lock (_gate) return _dirs.Values.ToArray(); } }

    /// <summary>The topmost directories left out (tests and diagnostics).</summary>
    internal string[] PrunedDirectories { get { lock (_gate) return _pruned.Order(StringComparer.Ordinal).ToArray(); } }

    /// <summary>The directories watched only for a want (tests and diagnostics).</summary>
    internal string[] WatchedForWant { get { lock (_gate) return Kept(WatchVerdict.WatchForWant); } }

    /// <summary>The cache directories watched at their top only (tests and diagnostics).</summary>
    internal string[] ShallowDirectories { get { lock (_gate) return Kept(WatchVerdict.Shallow); } }

    private string[] Kept(WatchVerdict verdict) => _kept.Where(kv => kv.Value == verdict).Select(static kv => kv.Key).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The root's watch descriptor (tests feed <see cref="Process"/> synthetic events for it).</summary>
    internal int RootWd => _rootWd;

    /// <summary>The inotify fd (tests read its watch count from /proc/self/fdinfo).</summary>
    internal int Fd => _fd;

    /// <summary>The reader thread: it has exited once Dispose returns. Null for a backend made without one.</summary>
    internal Thread? ReaderThread => _reader;

    /// <summary>Both fds are closed.</summary>
    internal bool IsClosed { get { lock (_gate) return _closed; } }

    public void RevisitPruned()
    {
        IOException? fatal = null;
        lock (_gate)
        {
            if (_closed || _disposeRequested || _outOfWatches) return;
            try { Revisit(under: null); }
            catch (IOException ex) { fatal = ex; _outOfWatches = true; }
        }
        if (fatal is not null) ReportFatal(fatal);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
            if (_closed) return; // the reader is gone already (or never started)
            if (_reader is null)
            {
                CloseFds(); // nothing to wake
                return;
            }
            ulong one = 1;
            WriteFd(_wakeFd, ref one, sizeof(ulong)); // cannot fail: the counter is far from its limit
        }
        if (!ReferenceEquals(Thread.CurrentThread, _reader)) _reader.Join(JoinTimeout);
    }

    // ---- the reader ----------------------------------------------------------------------------------------------------------------

    private void ReadLoop()
    {
        var buffer = new byte[_recursive ? BufferSize : BufferSize / 4];
        var fds = new PollFd[2];
        try
        {
            while (true)
            {
                bool pending;
                lock (_gate) pending = _pendingFrom is not null;
                fds[0] = new PollFd { Fd = _fd, Events = PollIn };
                fds[1] = new PollFd { Fd = _wakeFd, Events = PollIn };
                var ready = Poll(fds, 2, pending ? PairingWindowMs : -1);
                if (ready < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Eintr) continue;
                    throw new IOException("poll on the inotify fd failed: " + Marshal.GetPInvokeErrorMessage(errno));
                }
                if (fds[1].Revents != 0) return;                             // Dispose
                if (ready == 0) { Process([], endOfBurst: true); continue; } // the TO of a pending FROM did not come
                if ((fds[0].Revents & PollIn) != 0)
                {
                    var n = ReadFd(_fd, buffer, buffer.Length);
                    if (n < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno is Eintr or Eagain) continue;
                        throw new IOException("Reading the inotify fd failed: " + Marshal.GetPInvokeErrorMessage(errno));
                    }
                    Process(buffer.AsSpan(0, (int)n), endOfBurst: false);
                }
                else if ((fds[0].Revents & (PollErr | PollHup | PollNval)) != 0) throw new IOException("The inotify fd reported an error");
            }
        }
        catch (Exception ex)
        {
            // A dead reader is a dead watch: let the FileWatcher replace it (an exception escaping this thread would end the process).
            if (!Volatile.Read(ref _disposeRequested)) ReportFatal(ex as IOException ?? new IOException("The inotify reader failed: " + ex.Message, ex));
        }
        finally
        {
            CloseFds();
        }
    }

    /// <summary>Tests, on a backend without a reader: handles what is queued on the fd now, as the reader would, then ends the burst.</summary>
    internal void Pump()
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            nint n;
            lock (_gate)
            {
                if (_closed) return;
                n = ReadFd(_fd, buffer, buffer.Length);
            }
            if (n <= 0) break; // EAGAIN: nothing more queued
            Process(buffer.AsSpan(0, (int)n), endOfBurst: false);
        }
        Process([], endOfBurst: true);
    }

    private void CloseFds()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            CloseFd(_fd); // drops every watch of this instance
            CloseFd(_wakeFd);
            _dirs.Clear();
            _wds.Clear();
            _pruned.Clear();
            _kept.Clear();
            _pendingFrom = null;
        }
    }

    private void ReportFatal(Exception ex)
    {
        if (Interlocked.Exchange(ref _fatalReported, 1) == 0) _onFatal(ex);
    }

    /// <summary>
    /// Splits what read(2) returned into its inotify_event records (native byte order; a name is NUL-padded to its length). A record cut off
    /// at the end is left out — read(2) never returns one. Returns the number of bytes consumed.
    /// </summary>
    internal static int Parse(ReadOnlySpan<byte> buffer, List<RawEvent> into)
    {
        var pos = 0;
        while (buffer.Length - pos >= HeaderSize)
        {
            var header = buffer.Slice(pos, HeaderSize);
            var wd = MemoryMarshal.Read<int>(header);
            var mask = MemoryMarshal.Read<uint>(header[4..]);
            var cookie = MemoryMarshal.Read<uint>(header[8..]);
            var len = MemoryMarshal.Read<uint>(header[12..]);
            if (len > (uint)(buffer.Length - pos - HeaderSize)) break;
            var name = buffer.Slice(pos + HeaderSize, (int)len);
            var nul = name.IndexOf((byte)0);
            if (nul >= 0) name = name[..nul];
            into.Add(new RawEvent(wd, mask, cookie, name.IsEmpty ? "" : Encoding.UTF8.GetString(name)));
            pos += HeaderSize + (int)len;
        }
        return pos;
    }

    /// <summary>
    /// Handles one read's worth of events (the reader; tests feed it synthetic bytes) and passes what they mean to the sink, outside the lock
    /// and in order. <paramref name="endOfBurst"/>: nothing more is coming for now, so a FROM still waiting for its TO was a move out of the tree.
    /// </summary>
    internal void Process(ReadOnlySpan<byte> buffer, bool endOfBurst)
    {
        var events = new List<RawEvent>();
        Parse(buffer, events);
        var output = new List<Output>();
        IOException? fatal = null;
        var rootLost = false;
        lock (_gate)
        {
            if (_closed) return;
            foreach (var e in events)
            {
                // Out of watches while adding a new directory: this event is reported all the same (its output goes first), and so is every
                // later one; no more watches are added.
                try { rootLost |= Handle(e, output); }
                catch (IOException ex) { fatal ??= ex; _outOfWatches = true; }
            }
            if (endOfBurst) FlushPendingMove(output);
            // What the missing watches would have reported is unknown: consumers rescan (the backend that replaces this one starts from now).
            if (fatal is not null) output.Add(new Output(RawKind.Changed, _root, null));
        }
        foreach (var o in output) _sink(o.Kind, o.Path, o.OldPath);
        if (rootLost) _onRootLost();
        if (fatal is not null) ReportFatal(fatal);
    }

    // Under _gate. True when the root went (the caller tells the FileWatcher, outside the lock).
    private bool Handle(RawEvent e, List<Output> output)
    {
        if ((e.Mask & InQOverflow) != 0)
        {
            // Events were lost: consumers rescan the root, and the watches are brought in line with the tree as it is now.
            FlushPendingMove(output);
            output.Add(new Output(RawKind.Changed, _root, null));
            return Resync();
        }
        if ((e.Mask & InIgnored) != 0)
        {
            // The watch is gone. One we removed ourselves is forgotten already; otherwise the kernel removed it (its directory deleted or unmounted).
            if (!_dirs.Remove(e.Wd, out var gone)) return false;
            if (_wds.TryGetValue(gone, out var wd) && wd == e.Wd)
            {
                _wds.Remove(gone);
                _kept.Remove(gone);
            }
            return e.Wd == _rootWd;
        }

        if (_pendingFrom is { } from)
        {
            _pendingFrom = null;
            if ((e.Mask & InMovedTo) != 0 && e.Cookie == from.Cookie && _dirs.TryGetValue(e.Wd, out var toDir) && e.Name.Length > 0)
            {
                var to = Path.Join(toDir, e.Name);
                output.Add(new Output(RawKind.Renamed, to, from.Path));
                if (from.IsDir) DirectoryRenamed(from.Path, to);
                else
                {
                    if (IsMarker(Path.GetFileName(from.Path), Path.GetDirectoryName(from.Path)!)) MarkerChanged(Path.GetDirectoryName(from.Path)!, Path.GetFileName(from.Path), gone: true, output);
                    if (IsMarker(e.Name, toDir)) MarkerChanged(toDir, e.Name, gone: false, output);
                }
                return false;
            }
            MovedOut(from, output);
        }

        if (!_dirs.TryGetValue(e.Wd, out var dir)) return false; // a watch removed since; its last events may still be queued
        if (e.Name.Length == 0)
        {
            // About the watched directory itself: its parent's watch reports it by name. The root moved away: its watches would go on reporting
            // the moved tree under the old path, so drop them, and tell the FileWatcher.
            if (e.Wd != _rootWd || (e.Mask & InMoveSelf) == 0) return false;
            Unwatch(_root);
            return true;
        }

        var path = Path.Join(dir, e.Name);
        var isDir = (e.Mask & InIsDir) != 0;
        if ((e.Mask & InMovedFrom) != 0) _pendingFrom = new PendingMove(e.Cookie, path, isDir);
        else if ((e.Mask & (InCreate | InMovedTo)) != 0) // created, or moved in from outside (or from a directory left out)
        {
            output.Add(new Output(RawKind.Created, path, null));
            if (isDir) Appeared(path);
            else if (IsMarker(e.Name, dir)) MarkerChanged(dir, e.Name, gone: false, output);
        }
        else if ((e.Mask & InDelete) != 0)
        {
            output.Add(new Output(RawKind.Deleted, path, null));
            if (isDir) ForgetPruned(path); // its own watch (if any) ends with IN_IGNORED
            else if (IsMarker(e.Name, dir)) MarkerChanged(dir, e.Name, gone: true, output);
        }
        else if ((e.Mask & (InModify | InAttrib)) != 0)
        {
            output.Add(new Output(RawKind.Changed, path, null));
            if (!isDir && (e.Mask & InModify) != 0 && IsMarker(e.Name, dir)) MarkerChanged(dir, e.Name, gone: false, output); // written after it was created
        }
        return false;
    }

    private static bool HasMarker(string dir) =>
        File.Exists(Path.Join(dir, WatchExclusion.CacheDirTagName)) || File.Exists(Path.Join(dir, WatchExclusion.VirtualEnvMarkerName));

    // A file whose coming, going or content decides whether its directory is a cache: the tag, or a Python virtual environment's pyvenv.cfg.
    private bool IsMarker(string name, string dir) =>
        _recursive && (string.Equals(name, WatchExclusion.CacheDirTagName, StringComparison.Ordinal) || string.Equals(name, WatchExclusion.VirtualEnvMarkerName, StringComparison.Ordinal))
        && !string.Equals(dir, _root, StringComparison.Ordinal);

    // Under _gate.
    private void FlushPendingMove(List<Output> output)
    {
        if (_pendingFrom is not { } from) return;
        _pendingFrom = null;
        MovedOut(from, output);
    }

    // Under _gate. Moved out of the tree, or into a directory nobody watches: gone, as far as this root is concerned.
    private void MovedOut(PendingMove from, List<Output> output)
    {
        output.Add(new Output(RawKind.Deleted, from.Path, null));
        if (from.IsDir) Unwatch(from.Path);
        else if (Path.GetDirectoryName(from.Path) is { } dir && IsMarker(Path.GetFileName(from.Path), dir)) MarkerChanged(dir, Path.GetFileName(from.Path), gone: true, output);
    }

    // Under _gate. A directory appeared below a watched one.
    private void Appeared(string path)
    {
        if (!_recursive) return;
        var verdict = _verdict(path);
        if (verdict == WatchVerdict.Prune) _pruned.Add(path);
        else AddTree(path, isRoot: false, verdict);
    }

    /// <summary>
    /// Under _gate. Marker <paramref name="name"/> (a cache tag, or a virtual environment's pyvenv.cfg) came, went (<paramref name="gone"/>) or was
    /// written in watched directory <paramref name="dir"/>: whether it is a cache directory may have changed. Becoming one (Cargo's tag renamed
    /// into a fresh target/, or written after the first subdirectories; pyvenv.cfg going) drops the watches below it. Ceasing to be one —
    /// pyvenv.cfg written after uv's tag — walks what is in it and reports the directory created, so the FileWatcher announces what it holds:
    /// nothing reported it while it was left out. A tag that goes is not looked at: it goes first when the whole cache is deleted
    /// (<c>cargo clean</c>, <c>rm -rf target</c>), and walking what is about to be deleted is the cost the tag exists to avoid.
    /// </summary>
    private void MarkerChanged(string dir, string name, bool gone, List<Output> output)
    {
        if (!_wds.ContainsKey(dir)) return;
        if (gone && string.Equals(name, WatchExclusion.CacheDirTagName, StringComparison.Ordinal)) return;
        var now = _verdict(dir); // looks at the markers afresh
        ApplyVerdict(dir, now, output);
        if (now is WatchVerdict.Watch or WatchVerdict.WatchForWant && _wds.ContainsKey(dir))
        {
            // What is below may be left out now (a tag above it) or wanted back (the tag gone).
            foreach (var below in _wds.Keys.Where(d => d.Length > dir.Length && IsAtOrBelow(d, dir)).OrderBy(static d => d.Length).ToArray())
                if (_wds.ContainsKey(below)) ApplyVerdict(below, _verdict(below), null); // (gone with a directory above it otherwise)
            Revisit(under: dir);
        }
    }

    /// <summary>
    /// Under _gate: a watched directory's verdict changed (or may have). Left out: its watches go. Shallow: the watches below it go. Watched
    /// again after being shallow: what is in it is walked, and <paramref name="output"/> (when given) gets the directory reported created.
    /// </summary>
    private void ApplyVerdict(string dir, WatchVerdict now, List<Output>? output)
    {
        var was = _kept.GetValueOrDefault(dir, WatchVerdict.Watch);
        switch (now)
        {
            case WatchVerdict.Prune:
                Unwatch(dir);
                if (_wds.ContainsKey(Path.GetDirectoryName(dir)!)) _pruned.Add(dir);
                return;
            case WatchVerdict.Shallow:
                if (was != WatchVerdict.Shallow) UnwatchBelow(dir);
                _kept[dir] = WatchVerdict.Shallow;
                return;
            default:
                SetKept(dir, now);
                if (was != WatchVerdict.Shallow) return;
                AddTree(dir, isRoot: false, now); // its watch is in place: the walk lists it and goes down
                output?.Add(new Output(RawKind.Created, dir, null));
                return;
        }
    }

    private void SetKept(string dir, WatchVerdict verdict)
    {
        if (verdict == WatchVerdict.Watch) _kept.Remove(dir);
        else _kept[dir] = verdict;
    }

    // Under _gate. A directory was renamed inside the tree (a paired FROM/TO).
    private void DirectoryRenamed(string from, string to)
    {
        if (!_recursive) return;
        // Something watched at `to` already, other than what is being renamed: the two were exchanged (renameat2 RENAME_EXCHANGE), or its
        // label is stale. Drop it: `to` is looked at afresh below, and an exchange's second rename does the same for `from`.
        if (_wds.TryGetValue(to, out var atTo) && !(_wds.TryGetValue(from, out var fromWd) && fromWd == atTo)) Unwatch(to);

        var verdict = _verdict(to);
        if (verdict == WatchVerdict.Prune)
        {
            Unwatch(from);
            if (_wds.ContainsKey(Path.GetDirectoryName(to)!)) _pruned.Add(to);
            return;
        }
        if (_wds.TryGetValue(from, out var wd))
        {
            // Is the directory at `to` the watched one? Adding a watch for the path answers with the descriptor of whatever is there now.
            var there = InotifyAddWatch(_fd, to, WatchMask | InDontFollow);
            var errno = there < 0 ? Marshal.GetLastPInvokeError() : 0;
            if (there == wd || errno is Enoent or Enotdir) // it is (or `to` is gone already: keep the label, later events correct it)
            {
                Repath(from, to);
                ApplyVerdict(to, verdict, null); // renamed out of a cache, into a want, …: a shallow one is walked now, and the reverse
                if (_wds.ContainsKey(to)) Revisit(under: to); // a pattern may have named the old path
                return;
            }
            if (errno is Enospc or Enomem) throw new IOException(LimitMessage("watches", "max_user_watches", errno));
            // Another directory is at `to` already (the walk read the disk ahead of this event), and `to` is walked as the directory it is.
            // The watched one keeps its label only if it is still at `from` (mkdir X; mv X Y; mkdir X): if it moved on — maybe out of the
            // tree — its watch would report under a path it does not have, so it goes; whatever is at `from` now is added by the event that
            // put it there.
            var atFrom = InotifyAddWatch(_fd, from, WatchMask | InDontFollow);
            if (atFrom != wd)
            {
                Unwatch(from);
                if (atFrom >= 0 && !_dirs.ContainsKey(atFrom)) InotifyRmWatch(_fd, atFrom); // only a probe
            }
        }
        else Unwatch(from); // not watched (left out, or never seen): forget what was known below it
        AddTree(to, isRoot: false, verdict);
    }

    // Under _gate. The watches at and below `from` (and the left-out directories there) now belong to `to`.
    private void Repath(string from, string to)
    {
        foreach (var (wd, dir) in _dirs.ToArray())
        {
            if (!IsAtOrBelow(dir, from)) continue;
            var moved = to + dir[from.Length..];
            _dirs[wd] = moved;
            if (_wds.TryGetValue(dir, out var w) && w == wd) _wds.Remove(dir);
            _wds[moved] = wd;
        }
        foreach (var dir in _pruned.Where(d => IsAtOrBelow(d, from)).ToArray())
        {
            _pruned.Remove(dir);
            _pruned.Add(to + dir[from.Length..]);
        }
        foreach (var (dir, verdict) in _kept.Where(kv => IsAtOrBelow(kv.Key, from)).ToArray())
        {
            _kept.Remove(dir);
            _kept[to + dir[from.Length..]] = verdict;
        }
    }

    // Under _gate. Removes the watches of a directory and everything below it, and forgets the left-out directories there. `keep`: a
    // descriptor whose label goes but whose kernel watch stays (the caller labels it again).
    private void Unwatch(string path, int keep = -1)
    {
        foreach (var (wd, dir) in _dirs.ToArray())
        {
            if (!IsAtOrBelow(dir, path)) continue;
            if (wd != keep) InotifyRmWatch(_fd, wd); // its IN_IGNORED finds nothing left to forget
            _dirs.Remove(wd);
            if (_wds.TryGetValue(dir, out var w) && w == wd) _wds.Remove(dir);
        }
        ForgetPruned(path);
        foreach (var dir in _kept.Keys.Where(d => IsAtOrBelow(d, path)).ToArray()) _kept.Remove(dir);
    }

    // Under _gate: removes the watches strictly below `dir` and forgets what was known there; `dir`'s own watch stays.
    private void UnwatchBelow(string dir)
    {
        foreach (var (wd, path) in _dirs.ToArray())
        {
            if (path.Length <= dir.Length || !IsAtOrBelow(path, dir)) continue;
            InotifyRmWatch(_fd, wd);
            _dirs.Remove(wd);
            if (_wds.TryGetValue(path, out var w) && w == wd) _wds.Remove(path);
        }
        _pruned.RemoveWhere(d => d.Length > dir.Length && IsAtOrBelow(d, dir));
        foreach (var path in _kept.Keys.Where(d => d.Length > dir.Length && IsAtOrBelow(d, dir)).ToArray()) _kept.Remove(path);
    }

    // Under _gate.
    private void ForgetPruned(string path)
    {
        if (_pruned.Count > 0) _pruned.RemoveWhere(d => IsAtOrBelow(d, path));
    }

    /// <summary>
    /// Under _gate: subscriptions or their wants changed. Left-out directories (at or below <paramref name="under"/>, or all) that someone wants
    /// now are watched; directories watched only for a want are let go when it is gone. Throws an IOException when out of watches.
    /// </summary>
    private void Revisit(string? under)
    {
        if (_outOfWatches) return;
        foreach (var dir in _pruned.ToArray())
        {
            if (under is not null && !IsAtOrBelow(dir, under)) continue;
            var verdict = _verdict(dir);
            if (verdict == WatchVerdict.Prune) continue;
            _pruned.Remove(dir);
            if (_wds.ContainsKey(Path.GetDirectoryName(dir)!)) AddTree(dir, isRoot: false, verdict); // else a stale entry
        }
        foreach (var dir in _kept.Keys.Where(d => under is null || IsAtOrBelow(d, under)).OrderBy(static d => d.Length).ToArray())
        {
            if (!_wds.ContainsKey(dir)) { _kept.Remove(dir); continue; } // went with a directory above it
            ApplyVerdict(dir, _verdict(dir), null);
        }
    }

    /// <summary>
    /// Under _gate. After an overflow the labels may be anything — renames, exchanges, a name reused, all lost — so they are rebuilt, not
    /// reconciled: the tree is walked into empty maps, each descriptor labelled with the path the walk reached it by (inotify answers with
    /// the existing descriptor for a directory already watched, so nothing is watched twice), and every old descriptor the walk did not reach —
    /// a directory deleted while its IN_IGNORED was lost, one moved out of the tree while its IN_MOVED_FROM was, or one nobody wants any more —
    /// is removed, so nothing is reported under a path it no longer has. True when the root itself is gone or replaced.
    /// </summary>
    private bool Resync()
    {
        if (_outOfWatches) return false;
        var old = _dirs.Keys.ToArray();
        _dirs.Clear();
        _wds.Clear();
        _pruned.Clear();
        _kept.Clear();
        var rootGone = false;
        try { AddTree(_root, isRoot: true, WatchVerdict.Watch, resync: true); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException) { rootGone = true; }
        finally
        {
            foreach (var wd in old)
                if (!_dirs.ContainsKey(wd)) InotifyRmWatch(_fd, wd); // EINVAL for one the kernel freed already: harmless
        }
        // Another directory at the root's path now (the old one moved or deleted while events were lost): the FileWatcher re-arms on it.
        if (!_wds.TryGetValue(_root, out var rootWd) || rootWd != _rootWd) rootGone = true;
        return rootGone;
    }

    private static bool IsAtOrBelow(string path, string dir) =>
        path.StartsWith(dir, StringComparison.Ordinal) && (path.Length == dir.Length || path[dir.Length] == '/' || dir.EndsWith('/'));

    /// <summary>One walk's state: what it still has to visit, how often each path was pushed, and whether it rebuilds the maps (<see cref="Resync"/>).</summary>
    internal sealed class Walk(bool resync)
    {
        public readonly Stack<(string Dir, WatchVerdict Verdict)> Stack = new();
        private readonly Dictionary<string, int> _pushes = new(StringComparer.Ordinal);
        public bool Resync { get; } = resync;

        /// <summary>
        /// Pushes <paramref name="dir"/> unless this walk pushed it twice already: a walk ends whatever the disk and the labels say (a path is
        /// pushed again only when crossed labels were dropped and it must be walked afresh).
        /// </summary>
        public void Push(string dir, WatchVerdict verdict)
        {
            _pushes.TryGetValue(dir, out var n);
            if (n >= 2) return;
            _pushes[dir] = n + 1;
            Stack.Push((dir, verdict));
        }
    }

    /// <summary>
    /// Under _gate: watches <paramref name="top"/> (whose verdict is <paramref name="verdict"/>) and, for a recursive backend, every directory
    /// below it that someone wants — each one before it is listed, so a subdirectory created meanwhile is either listed or reported. A shallow
    /// directory is watched but not entered, nor is a symlinked one. <paramref name="resync"/>: the maps were emptied and are rebuilt from what
    /// the walk finds. Throws an <see cref="IOException"/> when the OS is out of watches, and for the root anything that keeps it from being
    /// watched. Adds nothing once the OS refused a watch.
    /// </summary>
    private void AddTree(string top, bool isRoot, WatchVerdict verdict, bool resync = false)
    {
        if (_outOfWatches) return;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var walk = new Walk(resync);
        walk.Push(top, verdict);
        var first = true;
        while (walk.Stack.Count > 0)
        {
            var (dir, dirVerdict) = walk.Stack.Pop();
            var asRoot = isRoot && first;
            first = false;
            if (!AddWatch(dir, asRoot, walk)) continue;
            if (!asRoot)
            {
                // A tag or pyvenv.cfg written between the verdict and the watch raised no event (`mkdir target; write the tag` read late):
                // with the watch in place, a marker found now decides again.
                if (_recursive && HasMarker(dir) && _verdict(dir) is var now && now != dirVerdict)
                {
                    if (now == WatchVerdict.Prune)
                    {
                        Unwatch(dir);
                        if (_wds.ContainsKey(Path.GetDirectoryName(dir)!)) _pruned.Add(dir);
                        continue;
                    }
                    dirVerdict = now;
                }
                _pruned.Remove(dir);
                SetKept(dir, dirVerdict);
                if (dirVerdict == WatchVerdict.Shallow) continue; // a cache directory: its top only
            }
            if (!_recursive) continue;

            IEnumerable<string> subdirs;
            try
            {
                subdirs = new System.IO.Enumeration.FileSystemEnumerable<string>(dir, static (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath(), options)
                {
                    ShouldIncludePredicate = static (ref System.IO.Enumeration.FileSystemEntry e) => e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0,
                }.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // gone or unreadable since it was watched

            foreach (var sub in subdirs)
            {
                var subVerdict = _verdict(sub);
                if (subVerdict == WatchVerdict.Prune) _pruned.Add(sub);
                else walk.Push(sub, subVerdict);
            }
        }
    }

    // Under _gate. False when there is nothing to watch (vanished, not a directory, unreadable) or the directory is already watched under another
    // path that names it too (a bind mount).
    private bool AddWatch(string dir, bool isRoot, Walk walk)
    {
        var wd = TryAddWatch(dir, isRoot);
        if (wd < 0) return false;
        if (walk.Resync)
        {
            // Rebuilding: the walk's path is the label. A descriptor labelled already in this walk is the same directory under two paths.
            if (_dirs.TryGetValue(wd, out var seen) && !string.Equals(seen, dir, StringComparison.Ordinal)) return isRoot;
            Label(wd, dir);
            return true;
        }

        // inotify just said that `dir` is `wd`: another descriptor labelled `dir` is stale (its directory moved; the walk, or the rename event
        // still queued, adds it under its real path). Two labels for one path would make every later relabelling move both.
        if (_wds.TryGetValue(dir, out var holder) && holder != wd) Unwatch(dir, keep: wd);

        if (_dirs.TryGetValue(wd, out var known) && !string.Equals(known, dir, StringComparison.Ordinal))
        {
            // This directory is watched already, under another path. Which directory does that path name now? inotify answers with its descriptor.
            var there = InotifyAddWatch(_fd, known, WatchMask | InDontFollow);
            if (there == wd) return isRoot; // the same directory at both paths (a bind mount): watched once, never walked twice — a mount can loop
            if (there >= 0 && _dirs.ContainsKey(there))
            {
                // Crossed labels: the directory at `known` is watched too, under yet another label (an exchange, a swap through a third name, both
                // still queued). Relabelling by prefix would move both and undo itself; drop both subtrees and walk both paths afresh.
                Unwatch(known);
                Unwatch(dir);
                PushVerdict(known, walk);
                wd = TryAddWatch(dir, isRoot); // `wd` itself went with `known`'s subtree
                if (wd < 0) return false;
                Label(wd, dir);
                return true;
            }
            // The label is stale (a rename whose events are still queued): the watch and those below it follow the directory, and whatever is at
            // the old path now is walked as the directory it is.
            Repath(known, dir);
            if (there >= 0)
            {
                InotifyRmWatch(_fd, there); // only a probe: the walk adds it properly
                PushVerdict(known, walk);
            }
        }
        Label(wd, dir);
        return true;
    }

    private void Label(int wd, string dir)
    {
        _dirs[wd] = dir;
        _wds[dir] = wd;
    }

    // Under _gate: `dir` is to be walked (below a watched directory) unless its verdict leaves it out.
    private void PushVerdict(string dir, Walk walk)
    {
        var verdict = _verdict(dir);
        if (verdict != WatchVerdict.Prune) walk.Push(dir, verdict);
        else if (_wds.ContainsKey(Path.GetDirectoryName(dir)!)) _pruned.Add(dir);
    }

    /// <summary>
    /// Under _gate: adds a watch, or answers -1 when there is nothing to watch. A directory below the root that is not (or no longer) there is
    /// remembered with the left-out ones: a rename of its parent still queued moves it with the others, and the revisit after it tries again
    /// (<c>mkdir X/new; mv X Y</c> read late would otherwise leave <c>Y/new</c> unwatched for good). Throws when the OS is out of watches, and
    /// for the root anything that keeps it from being watched.
    /// </summary>
    private int TryAddWatch(string dir, bool isRoot)
    {
        // The root may be a symlink (a folder opened through one); below it, links are never followed.
        var fault = AddWatchFault?.Invoke(dir) ?? 0;
        var wd = fault != 0 ? -1 : InotifyAddWatch(_fd, dir, WatchMask | (isRoot ? 0 : InDontFollow));
        if (wd >= 0) return wd;
        var errno = fault != 0 ? fault : Marshal.GetLastPInvokeError();
        if (errno is Enospc or Enomem) throw new IOException(LimitMessage("watches", "max_user_watches", errno));
        if (!isRoot)
        {
            if (errno is Enoent or Enotdir) _pruned.Add(dir);
            return -1;
        }
        var message = $"Cannot watch '{dir}': {Marshal.GetPInvokeErrorMessage(errno)}";
        throw errno switch
        {
            Enoent or Enotdir => new DirectoryNotFoundException(message),
            Eacces => new UnauthorizedAccessException(message),
            _ => new IOException(message),
        };
    }

    private static IOException InitError(int errno) => errno is Emfile or Enfile or Enomem
        ? new IOException(LimitMessage("instances", "max_user_instances", errno))
        : new IOException("Cannot create an inotify instance: " + Marshal.GetPInvokeErrorMessage(errno));

    private static string LimitMessage(string what, string setting, int errno)
    {
        string limit;
        try { limit = File.ReadAllText("/proc/sys/fs/inotify/" + setting).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { limit = "?"; }
        return $"The configured user limit ({limit}) on the number of inotify {what} has been reached, or the kernel is out of memory ({Marshal.GetPInvokeErrorMessage(errno)})";
    }
}
