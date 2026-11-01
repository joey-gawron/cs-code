using System.Globalization;
using System.Runtime.InteropServices;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>
/// CPU time of the CALLING thread, in milliseconds, on Linux (<c>/proc/thread-self/schedstat</c>) and macOS (Mach <c>thread_info</c>); null
/// elsewhere. The flood tests read it on the UI thread before and after a flood: the UI thread's own work, whatever the machine's load (the
/// dispatcher latency also counts the time the thread waited for a CPU, which a loaded shared machine inflates).
/// </summary>
internal static class ThreadCpu
{
    public static double? CurrentThreadMs()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var fields = File.ReadAllText("/proc/thread-self/schedstat").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return long.Parse(fields[0], CultureInfo.InvariantCulture) / 1e6;
            }
            if (OperatingSystem.IsMacOS())
            {
                // THREAD_BASIC_INFO (flavor 3): user_time {sec, usec}, system_time {sec, usec}, then six more ints. (The send right
                // mach_thread_self adds is not released: a test reads this a few times.)
                var info = new int[10];
                var count = info.Length;
                if (thread_info(mach_thread_self(), 3, info, ref count) != 0) return null;
                return (info[0] + info[2]) * 1000.0 + (info[1] + info[3]) / 1000.0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    [DllImport("/usr/lib/libSystem.dylib")] private static extern uint mach_thread_self();
    [DllImport("/usr/lib/libSystem.dylib")] private static extern int thread_info(uint thread, int flavor, [In, Out] int[] info, ref int count);
}
