using System.Runtime.InteropServices;
using System.Text;

namespace CsCode.Platform.Watching;

/// <summary>
/// The Linux backend: one inotify instance per root and one watch per directory, added a directory at a time so that a directory the prune
/// predicate rejects (every subscription on the root excludes it: <c>target/</c>, <c>node_modules/</c>…) costs no watch, neither when the root is
/// armed nor when the directory appears later. A <see cref="FileSystemWatcher"/> with IncludeSubdirectories watches every directory below its root
/// and drops excluded events only afterwards: one window on a Rust + JS monorepo held 75,892 of the user's 90,112 watches
/// (fs.inotify.max_user_watches), 56,489 of them in <c>target/</c>, and starved every other program on the machine.
/// <list type="bullet">
/// <item><b>Threads</b>: the constructor walks the tree on the caller's thread (the IO lane). A reader thread then waits in poll(2) on the inotify
/// fd and an eventfd; <see cref="Dispose"/> writes the eventfd, and the reader closes both fds and exits (Dispose waits for it). That also holds when
/// the root was deleted first and no watch is left, where a FileSystemWatcher keeps its instance and thread until the process exits.</item>
/// <item><b>Events</b>, as FileSystemWatcher reports them: created, deleted, changed (IN_MODIFY, IN_ATTRIB) and renamed, for files and directories.
/// An IN_MOVED_FROM directly followed by the IN_MOVED_TO with its cookie is one rename; an unpaired FROM (moved out of the tree, or into a
/// pruned directory) is a deletion, an unpaired TO a creation. A FROM that ends a read waits up to <see cref="PairingWindowMs"/> for its TO.
/// Events about a watched directory itself are left to its parent's watch, which reports them by name; the root's own are dropped.</item>
/// <item><b>New directories</b> (created or moved in, and not pruned) are watched with their subtree before the event that reports them is
/// passed on, so the <see cref="FileWatcher"/>, which lists a reported directory and announces what is already inside, misses nothing created
/// before a watch was in place. A directory renamed inside the tree keeps its watches (only their paths change); one moved out loses them, so
/// its later events are not reported under the old path. The root moved away loses all of them, for the same reason.</item>
/// <item><b>Pruned directories</b>: only the topmost ones are remembered (the walk does not enter them). When a subscription with fewer excludes
/// joins, <see cref="RevisitPruned"/> watches those nobody excludes any more.</item>
/// <item><b>Errors</b>: running out of watches (ENOSPC), instances (EMFILE) or kernel memory (ENOMEM) while arming throws an
/// <see cref="IOException"/> (the FileWatcher then polls); the same while adding a directory later is reported once through <c>onFatal</c>.
/// Directories that vanish, turn out not to be directories or cannot be read are skipped, symlinked ones are never entered.
/// IN_Q_OVERFLOW reports the root changed (consumers rescan) and walks the tree again for directories that appeared unseen.</item>
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
    private const int Enoent = 2, Eintr = 4, Eagain = 11, Enomem = 12, Eacces = 13, Enfile = 23, Emfile = 24, Enotdir = 20, Enospc = 28;

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
    private readonly Func<string, bool> _isPruned;
    private readonly RawSink _sink;
    private readonly Action<Exception> _onFatal;
    private readonly object _gate = new();                                       // guards everything below, and the fds' lifetime
    private readonly Dictionary<int, string> _dirs = new();                      // wd → the directory it watches
    private readonly Dictionary<string, int> _wds = new(StringComparer.Ordinal); // directory → wd
    private readonly HashSet<string> _pruned = new(StringComparer.Ordinal);      // topmost directories left unwatched: nobody wants them
    private readonly int _fd;
    private readonly int _wakeFd;
    private readonly int _rootWd;
    private readonly Thread? _reader;
    private PendingMove? _pendingFrom;
    private bool _closed;           // both fds closed (by the reader on its way out, or by a failed constructor)
    private bool _disposeRequested;
    private int _fatalReported;

    /// <summary>Arms the watch (walks the tree on this thread). Throws <see cref="DirectoryNotFoundException"/> for a missing root, an <see cref="IOException"/> when the OS limits are reached.</summary>
    public InotifyBackend(string root, bool recursive, Func<string, bool> isPruned, RawSink sink, Action<Exception> onFatal)
    {
        _root = root;
        _recursive = recursive;
        _isPruned = isPruned;
        _sink = sink;
        _onFatal = onFatal;

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
                AddTree(root, isRoot: true);
                _rootWd = _wds[root];
            }
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "CsCode.inotify" };
            _reader.Start();
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

    /// <summary>The topmost directories left unwatched because every subscription excludes them (tests and diagnostics).</summary>
    internal string[] PrunedDirectories { get { lock (_gate) return _pruned.Order(StringComparer.Ordinal).ToArray(); } }

    /// <summary>The root's watch descriptor (tests feed <see cref="Process"/> synthetic events for it).</summary>
    internal int RootWd => _rootWd;

    /// <summary>The inotify fd (tests read its watch count from /proc/self/fdinfo).</summary>
    internal int Fd => _fd;

    /// <summary>The reader thread: it has exited once Dispose returns.</summary>
    internal Thread? ReaderThread => _reader;

    public void RevisitPruned()
    {
        IOException? fatal = null;
        lock (_gate)
        {
            if (_closed || _disposeRequested || _pruned.Count == 0) return;
            try { RevisitPrunedUnder(null); }
            catch (IOException ex) { fatal = ex; }
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
            ulong one = 1;
            WriteFd(_wakeFd, ref one, sizeof(ulong)); // cannot fail: the counter is far from its limit
        }
        if (_reader is not null && !ReferenceEquals(Thread.CurrentThread, _reader)) _reader.Join(JoinTimeout);
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
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                foreach (var e in events) Handle(e, output);
                if (endOfBurst) FlushPendingMove(output);
            }
            catch (IOException ex) { fatal = ex; } // out of watches while adding a new directory: what was handled so far is still reported
        }
        foreach (var o in output) _sink(o.Kind, o.Path, o.OldPath);
        if (fatal is not null) ReportFatal(fatal);
    }

    // Under _gate.
    private void Handle(RawEvent e, List<Output> output)
    {
        if ((e.Mask & InQOverflow) != 0)
        {
            // Events were lost: consumers rescan the root, and directories that appeared unseen get their watches now.
            FlushPendingMove(output);
            output.Add(new Output(RawKind.Changed, _root, null));
            Resync();
            return;
        }
        if ((e.Mask & InIgnored) != 0)
        {
            // The watch is gone (its directory deleted or unmounted, or removed by us): forget the descriptor, which the kernel may reuse.
            if (_dirs.Remove(e.Wd, out var gone) && _wds.TryGetValue(gone, out var wd) && wd == e.Wd) _wds.Remove(gone);
            return;
        }

        if (_pendingFrom is { } from)
        {
            _pendingFrom = null;
            if ((e.Mask & InMovedTo) != 0 && e.Cookie == from.Cookie && _dirs.TryGetValue(e.Wd, out var toDir) && e.Name.Length > 0)
            {
                var to = Path.Join(toDir, e.Name);
                if (from.IsDir) DirectoryRenamed(from.Path, to);
                output.Add(new Output(RawKind.Renamed, to, from.Path));
                return;
            }
            MovedOut(from, output);
        }

        if (!_dirs.TryGetValue(e.Wd, out var dir)) return; // a watch removed since; its last events may still be queued
        if (e.Name.Length == 0)
        {
            // About the watched directory itself: its parent's watch reports it by name. The root moved away: its watches would go on
            // reporting the moved tree under the old path, so drop them (the FileWatcher re-arms on whatever is at the path later).
            if (e.Wd == _rootWd && (e.Mask & InMoveSelf) != 0) Unwatch(_root);
            return;
        }

        var path = Path.Join(dir, e.Name);
        var isDir = (e.Mask & InIsDir) != 0;
        if ((e.Mask & InMovedFrom) != 0) _pendingFrom = new PendingMove(e.Cookie, path, isDir);
        else if ((e.Mask & (InCreate | InMovedTo)) != 0) // created, or moved in from outside (or from a pruned directory)
        {
            if (isDir) Appeared(path);
            output.Add(new Output(RawKind.Created, path, null));
        }
        else if ((e.Mask & InDelete) != 0)
        {
            if (isDir) ForgetPruned(path); // its own watch (if any) ends with IN_IGNORED
            output.Add(new Output(RawKind.Deleted, path, null));
        }
        else if ((e.Mask & (InModify | InAttrib)) != 0) output.Add(new Output(RawKind.Changed, path, null));
    }

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
        if (from.IsDir) Unwatch(from.Path);
        output.Add(new Output(RawKind.Deleted, from.Path, null));
    }

    // Under _gate. A directory appeared below a watched one.
    private void Appeared(string path)
    {
        if (!_recursive) return;
        if (_isPruned(path)) _pruned.Add(path);
        else AddTree(path, isRoot: false);
    }

    // Under _gate. A directory was renamed inside the tree: the watches follow it (inotify watches inodes), only their paths change.
    private void DirectoryRenamed(string from, string to)
    {
        if (!_recursive) return;
        if (_wds.ContainsKey(from) && !_isPruned(to))
        {
            Repath(from, to);
            RevisitPrunedUnder(to); // a pattern may have named the old path
            return;
        }
        Unwatch(from);
        Appeared(to);
    }

    // Under _gate.
    private void Repath(string from, string to)
    {
        foreach (var (wd, dir) in _dirs.ToArray())
        {
            if (!IsAtOrBelow(dir, from)) continue;
            var moved = to + dir[from.Length..];
            _dirs[wd] = moved;
            _wds.Remove(dir);
            _wds[moved] = wd;
        }
        foreach (var dir in _pruned.Where(d => IsAtOrBelow(d, from)).ToArray())
        {
            _pruned.Remove(dir);
            _pruned.Add(to + dir[from.Length..]);
        }
    }

    // Under _gate. Removes the watches of a directory and everything below it, and forgets the pruned directories there.
    private void Unwatch(string path)
    {
        foreach (var (wd, dir) in _dirs.ToArray())
        {
            if (!IsAtOrBelow(dir, path)) continue;
            InotifyRmWatch(_fd, wd); // its IN_IGNORED finds nothing left to forget
            _dirs.Remove(wd);
            _wds.Remove(dir);
        }
        ForgetPruned(path);
    }

    // Under _gate.
    private void ForgetPruned(string path)
    {
        if (_pruned.Count > 0) _pruned.RemoveWhere(d => IsAtOrBelow(d, path));
    }

    // Under _gate. Watches the pruned directories (at or below `under`, or all) that somebody wants now. Throws an IOException when out of watches.
    private void RevisitPrunedUnder(string? under)
    {
        foreach (var dir in _pruned.ToArray())
        {
            if (under is not null && !IsAtOrBelow(dir, under)) continue;
            if (_isPruned(dir)) continue;
            _pruned.Remove(dir);
            if (_wds.ContainsKey(Path.GetDirectoryName(dir)!)) AddTree(dir, isRoot: false); // else a stale entry: its parent is not watched any more
        }
    }

    // Under _gate. After an overflow: walk the tree again; directories already watched keep their descriptors.
    private void Resync()
    {
        try { AddTree(_root, isRoot: true); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException) { } // the root went: the FileWatcher notices
    }

    private static bool IsAtOrBelow(string path, string dir) =>
        path.StartsWith(dir, StringComparison.Ordinal) && (path.Length == dir.Length || path[dir.Length] == '/' || dir.EndsWith('/'));

    /// <summary>
    /// Under _gate: watches <paramref name="top"/> and, for a recursive backend, every directory below it that is not pruned — each one before
    /// it is listed, so a subdirectory created meanwhile is either listed or reported. Symlinked directories are not entered. Throws an
    /// <see cref="IOException"/> when the OS is out of watches, and for the root anything that keeps it from being watched.
    /// </summary>
    private void AddTree(string top, bool isRoot)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var stack = new Stack<string>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (!AddWatch(dir, isRoot && ReferenceEquals(dir, top)) || !_recursive) continue;

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
                if (_isPruned(sub)) _pruned.Add(sub);
                else stack.Push(sub);
            }
        }
    }

    // Under _gate. False when there is nothing to watch (vanished, not a directory, unreadable) or the directory is already watched under another path.
    private bool AddWatch(string dir, bool isRoot)
    {
        // The root may be a symlink (a folder opened through one); below it, links are never followed.
        var wd = InotifyAddWatch(_fd, dir, WatchMask | (isRoot ? 0 : InDontFollow));
        if (wd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is Enospc or Enomem) throw new IOException(LimitMessage("watches", "max_user_watches", errno));
            if (!isRoot) return false;
            var message = $"Cannot watch '{dir}': {Marshal.GetPInvokeErrorMessage(errno)}";
            throw errno switch
            {
                Enoent or Enotdir => new DirectoryNotFoundException(message),
                Eacces => new UnauthorizedAccessException(message),
                _ => new IOException(message),
            };
        }
        if (_dirs.TryGetValue(wd, out var known) && !string.Equals(known, dir, StringComparison.Ordinal))
        {
            // Watched already under another path. That path is gone: a rename not seen yet (or lost in an overflow), so the watches follow.
            // It is still there: the same directory twice (a bind mount), watched once and never walked twice — a mount can loop back on itself.
            if (Directory.Exists(known)) return isRoot;
            Repath(known, dir);
            return true;
        }
        _dirs[wd] = dir;
        _wds[dir] = wd;
        return true;
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
