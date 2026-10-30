using System.Runtime.InteropServices;

namespace CsCode.Terminal.Pty.Unix;

/// <summary>
/// The process table for the terminal: names of foreground processes, the members of a terminal's session and a program's working directory.
/// Linux reads <c>/proc</c>; macOS asks libproc (<see cref="MacProcessTable"/>). Elsewhere every answer is empty.
/// </summary>
internal static class ProcessTable
{
    /// <summary>The table can be read on this platform.</summary>
    public static bool IsAvailable { get; } = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>The short name of a process (<c>/proc/&lt;pid&gt;/comm</c>; macOS: <c>proc_name</c>), or null.</summary>
    public static string? Name(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsMacOS()) return MacProcessTable.Name(pid);
        try
        {
            var comm = File.ReadAllText($"/proc/{pid}/comm").TrimEnd('\n');
            return comm.Length > 0 ? comm : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every process whose session id is <paramref name="sid"/> (empty without a process table).</summary>
    public static IReadOnlyList<int> SessionMembers(int sid) => SessionMembersWithStart(sid).Select(m => m.Pid).ToList();

    /// <summary>
    /// Every process whose session id is <paramref name="sid"/>, with its start time (Linux: clock ticks since boot, <c>/proc/&lt;pid&gt;/stat</c>'s
    /// starttime; macOS: microseconds since the epoch): a pid and its start time name one process, also after the pid was reused. Empty
    /// without a process table.
    /// </summary>
    public static IReadOnlyList<(int Pid, ulong StartTicks)> SessionMembersWithStart(int sid)
    {
        var result = new List<(int, ulong)>();
        if (sid <= 0) return result;
        if (OperatingSystem.IsMacOS())
        {
            foreach (var pid in MacProcessTable.AllPids())
                if (Stat(pid) is { } s && s.Session == sid) result.Add((pid, s.StartTicks));
            return result;
        }
        if (!OperatingSystem.IsLinux()) return result;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsAsciiDigit(name[0]) || !int.TryParse(name, out var pid)) continue;
            if (Stat(pid) is { } stat && stat.Session == sid) result.Add((pid, stat.StartTicks));
        }
        return result;
    }

    /// <summary>
    /// What the terminal needs of a process: state (<c>ps</c>'s letter: R, S, T, Z…), parent, process group, session and start time (see
    /// <see cref="SessionMembersWithStart"/>). Linux: fields of <c>/proc/&lt;pid&gt;/stat</c>.
    /// </summary>
    public static (char State, int Parent, int Group, int Session, ulong StartTicks)? Stat(int pid)
    {
        if (OperatingSystem.IsMacOS()) return MacProcessTable.Stat(pid);
        string text;
        try { text = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        // "pid (comm) S ppid pgrp session ...": comm may contain spaces and parentheses, so parse after the LAST ')'. starttime is field 22 of
        // the line, the 20th from the state.
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) return null;
        var fields = text[(close + 2)..].Split(' ');
        if (fields.Length < 20 || fields[0].Length == 0) return null;
        return int.TryParse(fields[1], out var ppid) && int.TryParse(fields[2], out var pgrp) && int.TryParse(fields[3], out var session)
            && ulong.TryParse(fields[19], out var start)
            ? (fields[0][0], ppid, pgrp, session, start)
            : null;
    }

    /// <summary>A process's working directory (Linux: <c>/proc/&lt;pid&gt;/cwd</c>; macOS: libproc), or null when unknown.</summary>
    public static string? WorkingDirectory(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsMacOS()) return MacProcessTable.WorkingDirectory(pid);
        if (!OperatingSystem.IsLinux()) return null;
        try { return new FileInfo($"/proc/{pid}/cwd").LinkTarget is { Length: > 0 } dir ? dir : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// macOS's process table through libproc (<c>proc_listallpids</c>, <c>proc_pidinfo</c>, <c>proc_name</c>) and <c>getsid</c>: what
/// <c>ps</c> and <c>lsof</c> read. Struct offsets are those of <c>sys/proc_info.h</c> (the same on arm64 and x86-64).
/// </summary>
internal static unsafe class MacProcessTable
{
    private const string LibProc = "/usr/lib/libproc.dylib";
    private const string LibC = "libc";

    private const int PROC_PIDTBSDINFO = 3;
    private const int PROC_PIDVNODEPATHINFO = 9;

    // struct proc_bsdinfo (136 bytes)
    private const int BsdInfoBytes = 136;
    private const int BsdStatus = 4, BsdPpid = 16, BsdPgid = 100, BsdStartSec = 120, BsdStartUsec = 128;

    // struct proc_vnodepathinfo: two vnode_info_path (a 152-byte vnode_info, then a MAXPATHLEN path); the first is the working directory
    private const int VnodePathInfoBytes = 2 * (152 + 1024);
    private const int CwdPathOffset = 152;

    [DllImport(LibProc)] private static extern int proc_listallpids(int* buffer, int bufferSize);
    [DllImport(LibProc)] private static extern int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int bufferSize);
    [DllImport(LibProc)] private static extern int proc_name(int pid, byte* buffer, uint bufferSize);
    [DllImport(LibC)] private static extern int getsid(int pid);

    public static int[] AllPids()
    {
        var count = proc_listallpids(null, 0);
        while (count > 0)
        {
            var pids = new int[count + 64];
            int n;
            fixed (int* p = pids) n = proc_listallpids(p, pids.Length * sizeof(int));
            if (n <= 0) break;
            if (n < pids.Length) return pids[..n];
            count = n * 2; // more processes appeared than fit: ask again
        }
        return [];
    }

    public static string? Name(int pid)
    {
        var buffer = stackalloc byte[256];
        var n = proc_name(pid, buffer, 256);
        return n > 0 ? Marshal.PtrToStringUTF8((nint)buffer, n) : null;
    }

    public static (char State, int Parent, int Group, int Session, ulong StartTicks)? Stat(int pid)
    {
        if (pid <= 0) return null;
        var info = stackalloc byte[BsdInfoBytes];
        if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, BsdInfoBytes) != BsdInfoBytes) return null;
        var state = *(uint*)(info + BsdStatus) switch
        {
            1 => 'I', // SIDL: being created
            2 => 'R',
            4 => 'T',
            5 => 'Z',
            _ => 'S',
        };
        var session = getsid(pid); // -1 once a zombie has no session any more
        var start = *(ulong*)(info + BsdStartSec) * 1_000_000 + *(ulong*)(info + BsdStartUsec);
        return (state, (int)*(uint*)(info + BsdPpid), (int)*(uint*)(info + BsdPgid), session, start);
    }

    public static string? WorkingDirectory(int pid)
    {
        var info = (byte*)NativeMemory.Alloc(VnodePathInfoBytes);
        try
        {
            if (proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, info, VnodePathInfoBytes) != VnodePathInfoBytes) return null;
            var path = Marshal.PtrToStringUTF8((nint)(info + CwdPathOffset));
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally { NativeMemory.Free(info); }
    }
}
