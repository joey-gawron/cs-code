using System.Runtime.InteropServices;

namespace CsCode.Platform.FileSystem;

/// <summary>
/// The few libc calls the <c>file:</c> file system needs to tell entries and volumes apart: <c>lstat</c>/<c>statx</c> (device and inode),
/// <c>pathconf(_PC_CASE_SENSITIVE)</c> (macOS) and <c>copyfile</c> (macOS file copies and directory metadata). Every call but
/// <see cref="CopyFile(string, string, bool, Action?, CancellationToken)"/> is best effort and never throws: an answer that cannot be had is null
/// (or the old assumption).
/// </summary>
internal static class FileSystemNative
{
    // Linux statx: stx_ino u64 @32, stx_dev_major u32 @136, stx_dev_minor u32 @140 (256 bytes, the same on every architecture).
    private const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100;
    private const uint StatxIno = 0x100;
    // macOS struct stat (64-bit inodes): st_dev i32 @0, st_mode u16 @4, st_nlink u16 @6, st_ino u64 @8 (144 bytes).
    private const int PcCaseSensitive = 11; // <sys/unistd.h> _PC_CASE_SENSITIVE
    private const uint CopyfileMetadata = 0x1 | 0x2 | 0x4; // COPYFILE_ACL | COPYFILE_STAT | COPYFILE_XATTR
    private const uint CopyfileData = 0x8, CopyfileExcl = 1 << 17, CopyfileNoFollowSrc = 1 << 18, CopyfileClone = 1 << 24;
    private const uint CopyfileStateStatusCb = 6;
    private const int CopyfileCopyData = 4, CopyfileProgress = 4, CopyfileContinue = 0, CopyfileQuit = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CopyfileCallback(int what, int stage, IntPtr state, IntPtr src, IntPtr dst, IntPtr ctx);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int LStatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer); // Intel macOS: the 64-bit-inode lstat

    [DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
    private static extern nint PathConf([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int name);

    [DllImport("libc", EntryPoint = "copyfile", SetLastError = true)]
    private static extern int CopyFile([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, IntPtr state, uint flags);

    [DllImport("libc", EntryPoint = "copyfile_state_alloc")]
    private static extern IntPtr CopyfileStateAlloc();

    [DllImport("libc", EntryPoint = "copyfile_state_free")]
    private static extern int CopyfileStateFree(IntPtr state);

    [DllImport("libc", EntryPoint = "copyfile_state_set")]
    private static extern int CopyfileStateSet(IntPtr state, uint flag, IntPtr value);

    /// <summary>The (device, inode) of <paramref name="path"/> itself (a symlink is not followed), or null when it cannot be read or this is Windows.</summary>
    public static (ulong Device, ulong Inode)? TryIdentity(string path)
    {
        try
        {
            var buffer = new byte[256];
            if (OperatingSystem.IsLinux())
            {
                if (StatX(AtFdCwd, path, AtSymlinkNoFollow, StatxIno, buffer) != 0 || (BitConverter.ToUInt32(buffer, 0) & StatxIno) == 0) return null;
                return (((ulong)BitConverter.ToUInt32(buffer, 136) << 32) | BitConverter.ToUInt32(buffer, 140), BitConverter.ToUInt64(buffer, 32));
            }
            if (OperatingSystem.IsMacOS())
            {
                var rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatInode64(path, buffer) : LStat(path, buffer);
                if (rc != 0) return null;
                return ((ulong)BitConverter.ToUInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8));
            }
            return null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>
    /// Whether two paths name the same directory entry (same device and inode): true, false, or null when either cannot be stat'ed (a path that
    /// does not exist, or a system where this is not known).
    /// </summary>
    public static bool? SameEntry(string a, string b) =>
        TryIdentity(a) is { } x && TryIdentity(b) is { } y ? x == y : null;

    /// <summary>
    /// Whether the volume holding <paramref name="path"/> tells names apart by case (files-8). Windows: no. Linux: yes (case-folding directories
    /// are not detected). macOS asks the volume (<c>pathconf(_PC_CASE_SENSITIVE)</c> on the nearest existing ancestor): APFS and HFS+ are
    /// case-insensitive by default, but either can be formatted case-sensitive; no answer means the default.
    /// </summary>
    public static bool IsCaseSensitive(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        if (!OperatingSystem.IsMacOS()) return true;
        try
        {
            for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if (!Directory.Exists(p) && !File.Exists(p)) continue;
                var answer = PathConf(p, PcCaseSensitive);
                return answer == 1;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return false;
    }

    /// <summary>
    /// macOS: copies the file <paramref name="from"/> to <paramref name="to"/>, which must not exist, with its data, mode, ACL and extended
    /// attributes (quarantine, Finder tags) as Finder's copy does (gap-11). With <paramref name="clone"/> the data is an APFS clone where the volume
    /// can make one (O(1)); otherwise, and on volumes that cannot clone, it is copied block by block, calling <paramref name="progress"/> after
    /// each block and stopping (<see cref="OperationCanceledException"/>) as soon as <paramref name="ct"/> is cancelled. Throws
    /// <see cref="IOException"/> when the copy fails; whatever it left at <paramref name="to"/> is the caller's to delete.
    /// </summary>
    public static void CopyFile(string from, string to, bool clone, Action? progress, CancellationToken ct)
    {
        Exception? raised = null;
        // Never let an exception cross the native frame: the hook's is kept, the copy told to stop, and the exception rethrown here.
        CopyfileCallback callback = (what, stage, _, _, _, _) =>
        {
            try
            {
                if (what == CopyfileCopyData && stage == CopyfileProgress) progress?.Invoke();
                return ct.IsCancellationRequested ? CopyfileQuit : CopyfileContinue;
            }
            catch (Exception ex) { raised = ex; return CopyfileQuit; }
        };
        var state = CopyfileStateAlloc();
        if (state == IntPtr.Zero) throw new IOException($"Cannot copy '{from}': out of memory");
        try
        {
            CopyfileStateSet(state, CopyfileStateStatusCb, Marshal.GetFunctionPointerForDelegate(callback));
            var flags = CopyfileMetadata | CopyfileData | CopyfileExcl | CopyfileNoFollowSrc | (clone ? CopyfileClone : 0);
            if (CopyFile(from, to, state, flags) == 0) return;
            var errno = Marshal.GetLastPInvokeError();
            if (raised is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(raised);
            ct.ThrowIfCancellationRequested();
            throw new IOException($"Cannot copy '{from}' to '{to}': {Marshal.GetPInvokeErrorMessage(errno)}");
        }
        finally
        {
            CopyfileStateFree(state);
            GC.KeepAlive(callback);
        }
    }

    /// <summary>macOS: gives the directory <paramref name="to"/> the ACL, mode, flags and extended attributes of <paramref name="from"/> (as Finder's copy does). False when it could not.</summary>
    public static bool TryCopyDirectoryMetadata(string from, string to)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try { return CopyFile(from, to, IntPtr.Zero, CopyfileMetadata) == 0; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }
}
