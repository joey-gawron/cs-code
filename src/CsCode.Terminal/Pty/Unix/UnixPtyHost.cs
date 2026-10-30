using System.Runtime.InteropServices;
using static CsCode.Terminal.Pty.Unix.UnixNative;

namespace CsCode.Terminal.Pty.Unix;

/// <summary>
/// A program on a Unix pseudo-terminal, without <c>fork()</c> from managed code: <c>posix_openpt</c> / <c>grantpt</c> / <c>unlockpt</c> /
/// <c>ptsname_r</c> for the pair, then <c>posix_spawn</c> with <c>POSIX_SPAWN_SETSID</c> (the child leads a new session) and file actions that
/// open the slave as fd 0 — opened by a session leader without <c>O_NOCTTY</c>, it becomes the controlling terminal — and dup it to 1 and 2.
/// Every other descriptor is closed in the child (<c>closefrom</c>), every signal disposition reset and the signal mask cleared (the runtime
/// ignores SIGPIPE: a shell must not inherit that).
/// <para>
/// Threads (dedicated, never the pool, never the UI): a <b>reader</b> polls the master, a pidfd for the child's exit and a wake pipe, and hands
/// output to the listener (blocking in the listener throttles the program, as a terminal should); a <b>writer</b> drains the bounded
/// <see cref="PtyWriteQueue"/> into the non-blocking master, waiting for POLLOUT when the program does not read. The reader also reaps the
/// child, so no zombie is left even when the host was disposed first. Descriptors are closed by whichever of the two threads ends last.
/// </para>
/// </summary>
public sealed unsafe class UnixPtyHost : IPtyHost
{
    /// <summary>Default bound of the write queue: a large paste fits, an endless one into a program that never reads does not grow memory.</summary>
    public const long DefaultMaxPendingWriteBytes = 16L * 1024 * 1024;

    /// <summary>How long output is still read after the child exited while something else (a background job) keeps the terminal open.</summary>
    internal static readonly TimeSpan ExitDrainTime = TimeSpan.FromMilliseconds(150);

    private const int ReadBufferBytes = 64 * 1024;

    private readonly PtyWriteQueue _queue;
    private readonly object _gate = new();
    private int _master = -1;
    private int _pidfd = -1;
    private int _readerWakeRead = -1, _readerWakeWrite = -1;
    private int _writerWakeRead = -1, _writerWakeWrite = -1;
    private int _pid;
    private int _exitCode = -1;
    private volatile bool _exited;
    private volatile bool _disposed;
    private volatile bool _slaveGone;
    private int _references; // threads alive + the owner; descriptors are closed when it drops to 0
    private IPtyListener? _listener;
    private Timer? _killTimer;
    private string? _slavePath;

    public UnixPtyHost(long maxPendingWriteBytes = DefaultMaxPendingWriteBytes)
    {
        _queue = new PtyWriteQueue(maxPendingWriteBytes);
    }

    public int ProcessId => Volatile.Read(ref _pid);
    public bool HasExited => _exited;
    public int? ExitCode => _exited ? Volatile.Read(ref _exitCode) : null;
    public long PendingWriteBytes => _queue.Pending;
    public long MaxPendingWriteBytes => _queue.Capacity;

    /// <summary>The slave device ("/dev/pts/7"), for diagnostics and tests.</summary>
    public string? SlavePath => _slavePath;

    public void Start(PtyStartInfo info, IPtyListener listener)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_master >= 0) throw new InvalidOperationException("The PTY host was already started.");
            _listener = listener;
            OpenPair(info.Columns, info.Rows);
            try
            {
                _pid = Spawn(info);
            }
            catch
            {
                close(_master);
                _master = -1;
                throw;
            }
            (_readerWakeRead, _readerWakeWrite) = WakePipe();
            (_writerWakeRead, _writerWakeWrite) = WakePipe();
            _pidfd = TryPidfd(_pid);
            _references = _pidfd < 0 ? 4 : 3; // reader, writer, (waiter,) owner (released by Dispose)
            new Thread(ReaderLoop) { IsBackground = true, Name = $"cscode-pty-read-{_pid}" }.Start();
            new Thread(WriterLoop) { IsBackground = true, Name = $"cscode-pty-write-{_pid}" }.Start();
            if (_pidfd < 0) new Thread(WaitLoop) { IsBackground = true, Name = $"cscode-pty-wait-{_pid}" }.Start();
        }
    }

    private void OpenPair(int columns, int rows)
    {
        var master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (master < 0) throw new PtySpawnException("Could not open a pseudo-terminal (posix_openpt).", Marshal.GetLastPInvokeError());
        if (grantpt(master) != 0 || unlockpt(master) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            close(master);
            throw new PtySpawnException("Could not unlock the pseudo-terminal.", errno);
        }
        var name = stackalloc byte[256];
        if (ptsname_r(master, name, 256) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            close(master);
            throw new PtySpawnException("Could not name the pseudo-terminal's slave.", errno);
        }
        _slavePath = Marshal.PtrToStringUTF8((nint)name);
        SetFlags(master, O_NONBLOCK);
        _master = master;
        SetSize(columns, rows); // before the child starts: its first query sees the real size
    }

    private int Spawn(PtyStartInfo info)
    {
        var fa = NativeMemory.AllocZeroed(SpawnStructBytes);
        var attr = NativeMemory.AllocZeroed(SpawnStructBytes);
        var sigs = NativeMemory.AllocZeroed(SigsetBytes);
        var allocated = new List<nint>();
        byte* Keep(string s)
        {
            var p = Utf8z(s);
            allocated.Add((nint)p);
            return p;
        }
        try
        {
            if (posix_spawn_file_actions_init(fa) != 0 || posix_spawnattr_init(attr) != 0)
                throw new PtySpawnException("posix_spawn could not be prepared.");
            var slave = Keep(_slavePath!);
            Check(posix_spawn_file_actions_addopen(fa, 0, slave, O_RDWR, 0), "addopen");
            Check(posix_spawn_file_actions_adddup2(fa, 0, 1), "adddup2");
            Check(posix_spawn_file_actions_adddup2(fa, 0, 2), "adddup2");
            try { Check(posix_spawn_file_actions_addclosefrom_np(fa, 3), "addclosefrom_np"); }
            catch (EntryPointNotFoundException) { /* older libc: the runtime's descriptors are close-on-exec anyway */ }
            try { Check(posix_spawn_file_actions_addchdir_np(fa, Keep(info.WorkingDirectory)), "addchdir_np"); }
            catch (EntryPointNotFoundException) { throw new PtySpawnException("This C library cannot start a process in another directory (posix_spawn_file_actions_addchdir_np)."); }

            sigfillset(sigs);
            Check(posix_spawnattr_setsigdefault(attr, sigs), "setsigdefault");
            sigemptyset(sigs);
            Check(posix_spawnattr_setsigmask(attr, sigs), "setsigmask");
            Check(posix_spawnattr_setflags(attr, (short)(POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK)), "setflags");

            var argv = (byte**)NativeMemory.AllocZeroed((nuint)(info.Arguments.Count + 2), (nuint)sizeof(byte*));
            allocated.Add((nint)argv);
            argv[0] = Keep(info.Argv0 ?? info.FileName);
            for (var i = 0; i < info.Arguments.Count; i++) argv[i + 1] = Keep(info.Arguments[i]);

            var env = info.Environment.Where(kv => !string.IsNullOrEmpty(kv.Key) && !kv.Key.Contains('=')).ToArray();
            var envp = (byte**)NativeMemory.AllocZeroed((nuint)(env.Length + 1), (nuint)sizeof(byte*));
            allocated.Add((nint)envp);
            for (var i = 0; i < env.Length; i++) envp[i] = Keep(env[i].Key + "=" + env[i].Value);

            int pid;
            var rc = posix_spawn(&pid, Keep(info.FileName), fa, attr, argv, envp);
            if (rc != 0)
                throw new PtySpawnException($"Could not start '{info.FileName}': {Marshal.GetPInvokeErrorMessage(rc)}.", rc);
            return pid;
        }
        finally
        {
            posix_spawn_file_actions_destroy(fa);
            posix_spawnattr_destroy(attr);
            NativeMemory.Free(fa);
            NativeMemory.Free(attr);
            NativeMemory.Free(sigs);
            foreach (var p in allocated) NativeMemory.Free((void*)p);
        }

        static void Check(int rc, string what)
        {
            if (rc != 0) throw new PtySpawnException($"posix_spawn {what} failed: {Marshal.GetPInvokeErrorMessage(rc)}", rc);
        }
    }

    private static int TryPidfd(int pid)
    {
        if (!IsLinux) return -1;
        try { return pidfd_open(pid, 0); }
        catch (EntryPointNotFoundException) { return -1; } // glibc < 2.36: a waitpid thread stands in
    }

    // ------------------------------------------------------------------ reader

    private void ReaderLoop()
    {
        var buffer = GC.AllocateUninitializedArray<byte>(ReadBufferBytes, pinned: true);
        var fds = stackalloc PollFd[3];
        long exitedAt = 0;
        try
        {
            fixed (byte* buf = buffer)
            {
                while (true)
                {
                    var n = 0;
                    var masterIndex = -1;
                    var pidIndex = -1;
                    if (!_slaveGone) { masterIndex = n; fds[n++] = new PollFd { Fd = _master, Events = POLLIN }; }
                    if (!_exited && _pidfd >= 0) { pidIndex = n; fds[n++] = new PollFd { Fd = _pidfd, Events = POLLIN }; }
                    var wakeIndex = n;
                    fds[n++] = new PollFd { Fd = _readerWakeRead, Events = POLLIN };

                    var timeout = -1;
                    if (_exited)
                    {
                        var left = ExitDrainTime.TotalMilliseconds - Elapsed(exitedAt);
                        if (_slaveGone || left <= 0 || _disposed) break; // drained (or nobody wants the rest any more)
                        timeout = (int)Math.Ceiling(left);
                    }
                    var ready = poll(fds, (nuint)n, timeout);
                    if (ready < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == EINTR) continue;
                        break;
                    }
                    if ((fds[wakeIndex].Revents & POLLIN) != 0) Drain(_readerWakeRead);

                    if (masterIndex >= 0 && fds[masterIndex].Revents != 0)
                    {
                        var read = UnixNative.read(_master, buf, ReadBufferBytes);
                        if (read > 0)
                        {
                            if (!_disposed) Deliver(buffer.AsSpan(0, (int)read));
                        }
                        else if (read == 0 || Marshal.GetLastPInvokeError() is var e && e != EAGAIN && e != EINTR)
                        {
                            _slaveGone = true; // EIO: every slave descriptor is closed (Linux reports a hang-up this way)
                        }
                    }

                    if (pidIndex >= 0 && (fds[pidIndex].Revents & POLLIN) != 0 && TryReap())
                        exitedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (_pidfd < 0 && _exited && exitedAt == 0) exitedAt = System.Diagnostics.Stopwatch.GetTimestamp(); // set by the wait thread
                    // Disposed while the child still runs: nothing is delivered any more, but the loop keeps waiting for the exit (Kill
                    // escalates to SIGKILL) so the child is reaped and never left as a zombie.
                }
            }
            if (!_exited) WaitForExitBlocking(); // only after a poll failure: never leave a zombie
            var code = Volatile.Read(ref _exitCode);
            _queue.Close();
            Wake(_writerWakeWrite);
            try { _listener?.OnExited(code); }
            catch (Exception) { /* a listener failure must not leak the descriptors */ }
        }
        finally
        {
            Release();
        }
    }

    private void Deliver(ReadOnlySpan<byte> data)
    {
        try { _listener?.OnOutput(data); }
        catch (Exception) { /* the session's own problem; the PTY keeps flowing */ }
    }

    private static double Elapsed(long since) =>
        since == 0 ? 0 : (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private bool TryReap()
    {
        int status;
        var r = waitpid(_pid, &status, WNOHANG);
        if (r == _pid)
        {
            Volatile.Write(ref _exitCode, DecodeWaitStatus(status));
            _exited = true;
            return true;
        }
        if (r < 0 && Marshal.GetLastPInvokeError() == ECHILD)
        {
            // Someone else reaped it (only possible if the runtime's SIGCHLD handling reaps every child): the code is lost.
            Volatile.Write(ref _exitCode, 0);
            _exited = true;
            return true;
        }
        return false;
    }

    private void WaitForExitBlocking()
    {
        if (_pidfd < 0) { WaitForWaitThread(); return; }
        while (!_exited)
        {
            int status;
            var r = waitpid(_pid, &status, 0);
            if (r == _pid) { Volatile.Write(ref _exitCode, DecodeWaitStatus(status)); _exited = true; }
            else if (r < 0 && Marshal.GetLastPInvokeError() != EINTR) { _exited = true; }
        }
    }

    private void WaitForWaitThread()
    {
        while (!_exited) Thread.Sleep(10);
    }

    /// <summary>Without pidfd (old libc): one thread blocks in waitpid for the child.</summary>
    private void WaitLoop()
    {
        while (true)
        {
            int status;
            var r = waitpid(_pid, &status, 0);
            if (r == _pid) { Volatile.Write(ref _exitCode, DecodeWaitStatus(status)); break; }
            if (r < 0 && Marshal.GetLastPInvokeError() != EINTR) { Volatile.Write(ref _exitCode, 0); break; }
        }
        _exited = true;
        Wake(_readerWakeWrite);
        Release();
    }

    // ------------------------------------------------------------------ writer

    private void WriterLoop()
    {
        var fds = stackalloc PollFd[2];
        try
        {
            while (!_disposed && !_exited)
            {
                var chunk = _queue.Peek(out var token);
                if (chunk.IsEmpty)
                {
                    fds[0] = new PollFd { Fd = _writerWakeRead, Events = POLLIN };
                    if (poll(fds, 1, -1) < 0 && Marshal.GetLastPInvokeError() != EINTR) break;
                    Drain(_writerWakeRead);
                    continue;
                }
                nint written;
                fixed (byte* p = chunk.Span) written = write(_master, p, (nuint)chunk.Length);
                if (written > 0)
                {
                    _queue.Advance(token, (int)written);
                    continue;
                }
                var errno = Marshal.GetLastPInvokeError();
                if (written < 0 && errno == EINTR) continue;
                if (written < 0 && errno != EAGAIN) break; // EIO: the program is gone
                // The PTY's input buffer is full (the program is not reading): wait for room or a wake-up (dispose, an urgent write).
                fds[0] = new PollFd { Fd = _master, Events = POLLOUT };
                fds[1] = new PollFd { Fd = _writerWakeRead, Events = POLLIN };
                if (poll(fds, 2, -1) < 0 && Marshal.GetLastPInvokeError() != EINTR) break;
                if ((fds[1].Revents & POLLIN) != 0) Drain(_writerWakeRead);
            }
        }
        finally
        {
            _queue.Close();
            Release();
        }
    }

    // ------------------------------------------------------------------ API

    public bool Write(ReadOnlySpan<byte> data, bool urgent = false)
    {
        if (_disposed || _exited || _master < 0) return false;
        if (!_queue.Enqueue(data, urgent)) return false;
        Wake(_writerWakeWrite);
        return true;
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_master < 0 || _disposed) return;
            SetSize(columns, rows);
        }
    }

    private void SetSize(int columns, int rows)
    {
        var size = new WinSize { Columns = (ushort)Math.Clamp(columns, 1, ushort.MaxValue), Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue) };
        ioctl(_master, TIOCSWINSZ, &size); // the kernel sends SIGWINCH to the foreground process group
    }

    public void Kill(TimeSpan? grace = null)
    {
        int pid;
        lock (_gate)
        {
            pid = _pid;
            if (pid <= 0) return;
            if (_killTimer is not null) return; // already being killed
            // The timer escalates even if the child exits quickly: what it started in the background (same session) goes too.
            _killTimer = new Timer(_ => KillSession(pid), null, grace ?? TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        if (!_exited)
        {
            kill(-pid, SIGHUP); // the shell is a session (and process group) leader; bash forwards the hang-up to its jobs
            kill(-pid, SIGCONT); // stopped jobs must run to die
            kill(pid, SIGHUP);
        }
        else
        {
            KillSession(pid);
        }
    }

    /// <summary>SIGKILL to every process still in the child's session (its jobs, which have their own process groups) and its group.</summary>
    private void KillSession(int sid)
    {
        if (!_exited) kill(sid, SIGKILL);
        kill(-sid, SIGKILL);
        foreach (var pid in ProcessTable.SessionMembers(sid))
            kill(pid, SIGKILL);
        lock (_gate)
        {
            _killTimer?.Dispose();
        }
    }

    public string? GetForegroundProcessName()
    {
        int master;
        lock (_gate) master = _disposed ? -1 : _master;
        if (master < 0 || _exited) return null;
        var pgid = tcgetpgrp(master);
        if (pgid <= 0) pgid = _pid;
        return ProcessTable.Name(pgid);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_master < 0) return; // never started
        Kill(TimeSpan.FromMilliseconds(500));
        Wake(_readerWakeWrite);
        Wake(_writerWakeWrite);
        Release();
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        lock (_gate)
        {
            CloseFd(ref _master);
            CloseFd(ref _pidfd);
            CloseFd(ref _readerWakeRead);
            CloseFd(ref _readerWakeWrite);
            CloseFd(ref _writerWakeRead);
            CloseFd(ref _writerWakeWrite);
        }
        _listener = null;
    }

    private static void CloseFd(ref int fd)
    {
        if (fd >= 0) close(fd);
        fd = -1;
    }
}
