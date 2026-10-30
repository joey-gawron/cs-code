using System.Globalization;
using System.Runtime.InteropServices;

namespace CsCode.MemoryTests.Responsiveness;

/// <summary>The machine's one-minute load average, for the notes of a timing test (Linux: /proc/loadavg; elsewhere libc's getloadavg).</summary>
internal static class LoadAverage
{
    [DllImport("libc", EntryPoint = "getloadavg")]
    private static extern int GetLoadAvg([Out] double[] loads, int count);

    public static string OneMinute()
    {
        if (OperatingSystem.IsLinux()) return File.ReadAllText("/proc/loadavg").Split(' ')[0];
        var loads = new double[1];
        try { return GetLoadAvg(loads, 1) == 1 ? loads[0].ToString("F2", CultureInfo.InvariantCulture) : "?"; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return "?"; }
    }
}
