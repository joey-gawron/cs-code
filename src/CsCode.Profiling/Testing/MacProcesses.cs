using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CsCode.Profiling.Testing;

/// <summary>
/// macOS's process table for the toolkit, through libproc (what <c>ps</c> reads): every pid, a process's parent, name and resident set, and this
/// process's children. Struct offsets are those of <c>sys/proc_info.h</c> (the same on arm64 and x86-64).
/// </summary>
internal static class MacProcesses
{
    private const string LibProc = "/usr/lib/libproc.dylib";
    private const int PROC_PIDTASKINFO = 4;
    private const int PROC_PIDTBSDINFO = 3;
    private const int BsdInfoBytes = 136, BsdPpid = 16, BsdComm = 48, MaxComLen = 16; // struct proc_bsdinfo
    private const int TaskInfoBytes = 96, TaskResident = 8; // struct proc_taskinfo

    [DllImport(LibProc)] private static extern int proc_listallpids(int[]? buffer, int bufferSize);
    [DllImport(LibProc)] private static extern int proc_listchildpids(int ppid, int[]? buffer, int bufferSize);
    [DllImport(LibProc)] private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int bufferSize);

    public static int[] AllPids() => List(() => proc_listallpids(null, 0), (buffer, bytes) => proc_listallpids(buffer, bytes));

    public static int[] ChildPids(int pid) => List(() => proc_listchildpids(pid, null, 0), (buffer, bytes) => proc_listchildpids(pid, buffer, bytes));

    /// <summary>The parent and the short name (the kernel's p_comm) of a process, or null when it is gone.</summary>
    public static (int Parent, string Name)? Info(int pid)
    {
        var info = new byte[BsdInfoBytes];
        if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, info.Length) != info.Length) return null;
        var comm = info.AsSpan(BsdComm, MaxComLen);
        var end = comm.IndexOf((byte)0);
        return ((int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(BsdPpid)), Encoding.UTF8.GetString(end < 0 ? comm : comm[..end]));
    }

    /// <summary>The resident set of a process (bytes), 0 when it is gone or cannot be read.</summary>
    public static long Resident(int pid)
    {
        var info = new byte[TaskInfoBytes];
        return proc_pidinfo(pid, PROC_PIDTASKINFO, 0, info, info.Length) == info.Length ? (long)BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(TaskResident)) : 0;
    }

    private static int[] List(Func<int> count, Func<int[], int, int> fill)
    {
        // Both calls answer a number of pids: without a buffer an estimate, with one what was written (a full buffer may have been too small).
        var n = count();
        while (n >= 0)
        {
            var pids = new int[n + 64];
            var got = fill(pids, pids.Length * sizeof(int));
            if (got <= 0) return [];
            if (got < pids.Length) return pids[..got];
            n = got * 2;
        }
        return [];
    }
}
