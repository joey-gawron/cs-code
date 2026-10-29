using System.Runtime.InteropServices;

namespace CsCode.Lsp.Runtime;

/// <summary>
/// OS-level scheduling hints. .NET's Thread.Priority is a no-op on Linux, so lane threads additionally get a per-thread nice value
/// (Linux nice is per-thread: setpriority(PRIO_PROCESS, tid)). Lowering priority never needs privileges. Best effort, never throws.
/// </summary>
internal static class NativePriority
{
    private const int PrioProcess = 0;

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, uint who, int prio);

    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int GetTid();

    /// <summary>Lowers the calling thread's priority (Linux: nice). Returns false when unsupported / refused.</summary>
    public static bool TryLowerCurrentThread(int nice)
    {
        if (!OperatingSystem.IsLinux() || nice <= 0) return false;
        try { return SetPriority(PrioProcess, (uint)GetTid(), nice) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { return false; }
    }

    /// <summary>Nice value of the calling thread (Linux only; used by tests and diagnostics).</summary>
    public static int? GetCurrentThreadNice()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return ReadNice($"/proc/self/task/{GetTid()}/stat"); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException) { return null; }
    }

    /// <summary>Nice value of a process (Linux only).</summary>
    public static int? GetProcessNice(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return ReadNice($"/proc/{pid}/stat"); }
        catch (IOException) { return null; }
    }

    private static int? ReadNice(string statPath)
    {
        var text = File.ReadAllText(statPath);
        // field 2 (comm) may contain spaces/parens: parse after the LAST ')'
        var close = text.LastIndexOf(')');
        if (close < 0) return null;
        var fields = text[(close + 2)..].Split(' ');
        // after comm: state is field 3 => index 0; nice is field 19 => index 16
        return fields.Length > 16 && int.TryParse(fields[16], out var nice) ? nice : null;
    }
}
