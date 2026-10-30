using System.Runtime.InteropServices;

namespace CsCode.Terminal.Pty.Unix;

/// <summary>
/// libc calls of the Unix PTY host. Linux (glibc) is the tested platform; the macOS constants are written from its headers and untested here.
/// The variadic <c>ioctl</c>/<c>fcntl</c> are declared with their one fixed extra argument, which is passed like a named argument on
/// x86-64 and arm64 Linux (not on Apple arm64: see docs/TERMINAL.md, known gaps).
/// </summary>
internal static unsafe class UnixNative
{
    private const string Libc = "libc";

    public static readonly bool IsLinux = OperatingSystem.IsLinux();

    // ---- open(2) flags
    public static int O_RDWR => 2;
    public static int O_NOCTTY => IsLinux ? 0x100 : 0x20000;
    public static int O_NONBLOCK => IsLinux ? 0x800 : 0x4;
    public static int O_CLOEXEC => IsLinux ? 0x80000 : 0x1000000;

    // ---- fcntl
    public const int F_GETFL = 3;
    public const int F_SETFL = 4;

    // ---- errno
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int ECHILD = 10;
    public const int ESRCH = 3;
    public static int EAGAIN => IsLinux ? 11 : 35;

    // ---- signals
    public const int SIGHUP = 1;
    public const int SIGINT = 2;
    public const int SIGKILL = 9;
    public static int SIGCONT => IsLinux ? 18 : 19;

    // ---- poll
    public const short POLLIN = 0x1;
    public const short POLLOUT = 0x4;
    public const short POLLERR = 0x8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;

    // ---- waitpid
    public const int WNOHANG = 1;

    // ---- posix_spawn flags (glibc: spawn.h; macOS: spawn.h)
    public const short POSIX_SPAWN_SETSIGDEF = 0x04;
    public const short POSIX_SPAWN_SETSIGMASK = 0x08;
    public static short POSIX_SPAWN_SETSID => IsLinux ? (short)0x80 : (short)0x400;

    // ---- ioctl
    public static nuint TIOCSWINSZ => IsLinux ? (nuint)0x5414 : (nuint)0x80087467;

    /// <summary>Generous upper bounds of the opaque libc structures (glibc x86-64: file actions 80 bytes, attributes 336, sigset_t 128).</summary>
    public const int SpawnStructBytes = 1024;
    public const int SigsetBytes = 256;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [DllImport(Libc, SetLastError = true)] public static extern int posix_openpt(int flags);
    [DllImport(Libc, SetLastError = true)] public static extern int grantpt(int fd);
    [DllImport(Libc, SetLastError = true)] public static extern int unlockpt(int fd);
    [DllImport(Libc, SetLastError = true)] public static extern int ptsname_r(int fd, byte* buf, nuint buflen);

    [DllImport(Libc)] public static extern int posix_spawn_file_actions_init(void* fileActions);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_destroy(void* fileActions);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_addopen(void* fileActions, int fd, byte* path, int oflag, uint mode);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_adddup2(void* fileActions, int fd, int newfd);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_addclosefrom_np(void* fileActions, int from);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_addchdir_np(void* fileActions, byte* path);
    [DllImport(Libc)] public static extern int posix_spawnattr_init(void* attr);
    [DllImport(Libc)] public static extern int posix_spawnattr_destroy(void* attr);
    [DllImport(Libc)] public static extern int posix_spawnattr_setflags(void* attr, short flags);
    [DllImport(Libc)] public static extern int posix_spawnattr_setsigdefault(void* attr, void* sigset);
    [DllImport(Libc)] public static extern int posix_spawnattr_setsigmask(void* attr, void* sigset);
    [DllImport(Libc)] public static extern int sigemptyset(void* set);
    [DllImport(Libc)] public static extern int sigfillset(void* set);
    [DllImport(Libc)] public static extern int posix_spawn(int* pid, byte* path, void* fileActions, void* attr, byte** argv, byte** envp);

    [DllImport(Libc, SetLastError = true)] public static extern int ioctl(int fd, nuint request, void* arg);
    [DllImport(Libc, SetLastError = true)] public static extern int fcntl(int fd, int cmd, int arg);
    [DllImport(Libc, SetLastError = true)] public static extern int kill(int pid, int sig);
    [DllImport(Libc, SetLastError = true)] public static extern int waitpid(int pid, int* status, int options);
    [DllImport(Libc, SetLastError = true)] public static extern int pidfd_open(int pid, uint flags);
    [DllImport(Libc, SetLastError = true)] public static extern int poll(PollFd* fds, nuint nfds, int timeout);
    [DllImport(Libc, SetLastError = true)] public static extern nint read(int fd, byte* buf, nuint count);
    [DllImport(Libc, SetLastError = true)] public static extern nint write(int fd, byte* buf, nuint count);
    [DllImport(Libc, SetLastError = true)] public static extern int close(int fd);
    [DllImport(Libc, SetLastError = true)] public static extern int pipe(int* fds);
    [DllImport(Libc, SetLastError = true)] public static extern int tcgetpgrp(int fd);

    /// <summary>Exit code from a waitpid status: the code for a normal exit, 128 + n for a signal (as shells report it).</summary>
    public static int DecodeWaitStatus(int status)
    {
        var termSig = status & 0x7f;
        if (termSig == 0) return (status >> 8) & 0xff;
        if (termSig == 0x7f) return 128; // stopped: not an exit (never asked for with WUNTRACED)
        return 128 + termSig;
    }

    /// <summary>A close-on-exec, non-blocking pipe (read end, write end) for waking a poll loop.</summary>
    public static (int Read, int Write) WakePipe()
    {
        var fds = stackalloc int[2];
        if (pipe(fds) != 0) throw new PtySpawnException("pipe() failed", Marshal.GetLastPInvokeError());
        for (var i = 0; i < 2; i++)
        {
            SetFlags(fds[i], O_NONBLOCK);
            fcntl(fds[i], 2 /* F_SETFD */, 1 /* FD_CLOEXEC */);
        }
        return (fds[0], fds[1]);
    }

    public static void SetFlags(int fd, int add)
    {
        var flags = fcntl(fd, F_GETFL, 0);
        if (flags >= 0) fcntl(fd, F_SETFL, flags | add);
    }

    public static void Wake(int fd)
    {
        if (fd < 0) return;
        byte b = 1;
        write(fd, &b, 1); // EAGAIN (pipe full) is fine: a wake-up is already pending
    }

    public static void Drain(int fd)
    {
        var buf = stackalloc byte[64];
        while (read(fd, buf, 64) > 0) { }
    }

    /// <summary>A NUL-terminated UTF-8 copy in unmanaged memory (freed by the caller with <see cref="NativeMemory.Free"/>).</summary>
    public static byte* Utf8z(string s)
    {
        var count = System.Text.Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)count + 1);
        fixed (char* c = s) System.Text.Encoding.UTF8.GetBytes(c, s.Length, p, count);
        p[count] = 0;
        return p;
    }
}
