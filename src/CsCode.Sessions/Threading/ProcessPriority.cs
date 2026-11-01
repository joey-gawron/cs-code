using System.Runtime.InteropServices;

namespace CsCode.Sessions;

/// <summary>
/// A <b>reversible</b> low priority for the whole process: the warm spare starts and waits in it, so its start-up (≈ 0.3-0.8 s of CPU, right after
/// the claim that started it) does not compete with the window the user just opened, and leaves it the moment it is claimed (docs/SESSIONS.md,
/// "The warm spare"). Plain <c>nice</c> cannot be used: an unprivileged process can raise its nice value but never lower it again, so a claimed
/// spare would stay a slow session for good.
/// <list type="bullet">
/// <item>macOS: the Darwin background state (<c>setpriority(PRIO_DARWIN_PROCESS, 0, PRIO_DARWIN_BG)</c>, what <c>taskpolicy -b</c> does): lowest CPU
/// priority (efficiency cores on Apple silicon) and throttled disk I/O; cleared with <c>setpriority(PRIO_DARWIN_PROCESS, 0, 0)</c>.</item>
/// <item>Linux: the nice value of the process's scheduler autogroup (<c>/proc/self/autogroup</c>; a CsCode process started by the launcher leads a
/// session of its own, <c>setsid</c>, so the autogroup holds only it). Raising it back to 0 needs no privilege, unlike a task's nice value. It
/// has an effect only where autogroups are in use (<c>kernel.sched_autogroup_enabled</c>, the default on desktop distributions, and a process
/// that is not in a cgroup with its own CPU controller, which systemd units can be); elsewhere it is a harmless no-op. Only a process that leads
/// its own session touches it: one that shares a session (started from a shell directly, e.g. <c>cscode --host --spare</c>, where <c>setsid</c>
/// fails because the shell made it a process-group leader) shares that session's autogroup, and lowering it would slow the whole terminal
/// session; such a process stays at normal priority (entering returns false, leaving writes nothing).</item>
/// <item>Windows: not done (false).</item>
/// </list>
/// Best effort: every call returns false instead of throwing when the OS refuses or lacks it.
/// </summary>
public static class ProcessPriority
{
    /// <summary>The autogroup nice value of a process in the background (0 is normal, 19 the lowest).</summary>
    public const int LinuxBackgroundNice = 10;

    private const int LinuxWriteAttempts = 6;
    private const int PrioDarwinProcess = 4; // <sys/resource.h> PRIO_DARWIN_PROCESS
    private const int PrioDarwinBg = 0x1000; // PRIO_DARWIN_BG

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, uint who, int prio);

    [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int GetPriority(int which, uint who);

    /// <summary>Where Linux keeps this process's autogroup (tests point it elsewhere).</summary>
    internal static string LinuxAutogroupPath { get; set; } = "/proc/self/autogroup";

    /// <summary>Whether this process leads its own session, so its autogroup is its alone (tests replace it).</summary>
    internal static Func<bool> LinuxLeadsOwnSession { get; set; } = Ipc.UnixNative.LeadsOwnSession;

    /// <summary>Lowers this process's priority (see the class remarks). True when it took effect.</summary>
    public static bool EnterBackground() => Set(background: true);

    /// <summary>Back to normal priority. True when this process now runs at normal priority (also when it never left it).</summary>
    public static bool LeaveBackground() => Set(background: false);

    /// <summary>Whether this process is in the background state now; null when the OS cannot tell.</summary>
    public static bool? IsBackground()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Marshal.SetLastPInvokeError(0);
                var value = GetPriority(PrioDarwinProcess, 0);
                if (value == -1 && Marshal.GetLastPInvokeError() != 0) return null;
                return value != 0;
            }
            if (OperatingSystem.IsLinux()) return ReadAutogroupNice() is { } nice ? nice > 0 : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    private static bool Set(bool background)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return SetPriority(PrioDarwinProcess, 0, background ? PrioDarwinBg : 0) == 0;
            if (OperatingSystem.IsLinux())
            {
                if (!File.Exists(LinuxAutogroupPath)) return false;
                // Another session's autogroup (a shell's) is not this process's to change: never lowered, so never raised either.
                if (!LinuxLeadsOwnSession()) return !background;
                var value = background ? LinuxBackgroundNice.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
                // The kernel takes one autogroup change per 100 ms, system-wide (EAGAIN otherwise, e.g. two spares starting together): a few tries,
                // so a claim never leaves its session in the background.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.WriteAllText(LinuxAutogroupPath, value);
                        return true;
                    }
                    catch (IOException) when (attempt < LinuxWriteAttempts - 1) { Thread.Sleep(40); }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>The nice value in <c>/proc/self/autogroup</c> ("/autogroup-123 nice 10"); null when there is none.</summary>
    internal static int? ReadAutogroupNice()
    {
        try
        {
            var text = File.ReadAllText(LinuxAutogroupPath);
            var at = text.IndexOf(" nice ", StringComparison.Ordinal);
            return at >= 0 && int.TryParse(text.AsSpan(at + 6).Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var nice)
                ? nice : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
