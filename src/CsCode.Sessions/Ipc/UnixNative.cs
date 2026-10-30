using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CsCode.Sessions.Ipc;

/// <summary>The few POSIX calls .NET does not expose: effective uid, peer credentials of a Unix socket, leaving the launcher's session, malloc_trim.
/// Every call is best effort and never throws for "unsupported".</summary>
internal static class UnixNative
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEUid();

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSid();

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    /// <summary>Effective uid of this process (null on Windows or when libc is unavailable).</summary>
    public static uint? EffectiveUid()
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return GetEUid(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// Starts a new session (detaches from the launcher's terminal and process group, so closing that terminal does not hang up a background
    /// CsCode). Fails harmlessly when this process already leads a process group (started from a shell directly).
    /// </summary>
    public static bool TryDetachFromTerminal()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return SetSid() >= 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Returns freed native heap pages to the OS (glibc only; a no-op elsewhere).</summary>
    public static bool TryMallocTrim()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return MallocTrim(0) != 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private const int SolSocket = 1;      // Linux SOL_SOCKET
    private const int SoPeerCred = 17;    // Linux SO_PEERCRED: struct ucred { pid_t pid; uid_t uid; gid_t gid; }
    private const int SolLocal = 0;       // macOS SOL_LOCAL
    private const int LocalPeerCred = 1;  // macOS LOCAL_PEERCRED: struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
    private const int LocalPeerPid = 2;   // macOS LOCAL_PEERPID

    /// <summary>The connected peer's uid (and pid where the OS reports it). Null when the OS cannot tell.</summary>
    public static (uint Uid, int? Pid)? PeerCredentials(Socket socket)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> ucred = stackalloc byte[12];
                if (socket.GetRawSocketOption(SolSocket, SoPeerCred, ucred) < 12) return null;
                return (BitConverter.ToUInt32(ucred[4..8]), BitConverter.ToInt32(ucred[..4]));
            }
            if (OperatingSystem.IsMacOS())
            {
                Span<byte> xucred = stackalloc byte[76];
                if (socket.GetRawSocketOption(SolLocal, LocalPeerCred, xucred) < 8) return null;
                int? pid = null;
                Span<byte> pidBytes = stackalloc byte[4];
                if (socket.GetRawSocketOption(SolLocal, LocalPeerPid, pidBytes) == 4) pid = BitConverter.ToInt32(pidBytes);
                return (BitConverter.ToUInt32(xucred[4..8]), pid);
            }
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ObjectDisposedException) { }
        return null;
    }
}
