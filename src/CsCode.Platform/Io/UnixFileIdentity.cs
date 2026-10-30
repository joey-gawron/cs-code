using System.Runtime.InteropServices;

namespace CsCode.Platform.Io;

/// <summary>What a rename-over would change about an existing file: its link count, owner, group and mode (Linux <c>statx</c>).</summary>
internal readonly record struct UnixFileIdentity(uint Links, uint Uid, uint Gid, UnixFileMode Mode);

/// <summary>
/// The few libc calls a save needs to keep a file's identity (lsp-core-8): <c>statx</c> (link count, owner, group), <c>geteuid</c>, <c>chown</c> and the
/// extended-attribute calls (ACLs live in <c>system.posix_acl_*</c>, SELinux labels in <c>security.selinux</c>). Linux only: elsewhere
/// <see cref="TryStat"/> returns null and callers keep only the mode, as before. Every call is best effort and never throws.
/// </summary>
internal static class UnixNative
{
    private const int AtFdCwd = -100;
    private const uint StatxMode = 0x2, StatxNlink = 0x4, StatxUid = 0x8, StatxGid = 0x10;
    private const int Enotsup = 95, Enodata = 61;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint ListXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[]? list, nuint size);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? value, nuint size);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, int flags);

    /// <summary>The identity of <paramref name="path"/> (symlinks followed), or null when it cannot be read or this is not Linux.</summary>
    public static UnixFileIdentity? TryStat(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            // struct statx: stx_mask u32 @0, stx_blksize u32 @4, stx_attributes u64 @8, stx_nlink u32 @16, stx_uid u32 @20, stx_gid u32 @24,
            // stx_mode u16 @28 … 256 bytes in all; the layout is the same on every architecture.
            var buffer = new byte[256];
            if (StatX(AtFdCwd, path, 0, StatxMode | StatxNlink | StatxUid | StatxGid, buffer) != 0) return null;
            var mask = BitConverter.ToUInt32(buffer, 0);
            if ((mask & (StatxNlink | StatxUid | StatxGid)) != (StatxNlink | StatxUid | StatxGid)) return null;
            return new UnixFileIdentity(BitConverter.ToUInt32(buffer, 16), BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt32(buffer, 24),
                (UnixFileMode)(BitConverter.ToUInt16(buffer, 28) & 0xFFF));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    public static uint? EffectiveUid()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return GetEuid(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>Gives <paramref name="path"/> the owner and group; false when the kernel refuses (not root, not a member of the group).</summary>
    public static bool TryChown(string path, uint uid, uint gid)
    {
        try { return Chown(path, uid, gid) == 0; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    /// <summary>
    /// Copies every extended attribute of <paramref name="from"/> onto <paramref name="to"/>. True when there was nothing to copy (or the file
    /// system has none) or everything was copied; false when one could not be read or set (the caller then keeps the original file).
    /// </summary>
    public static bool TryCopyXattrs(string from, string to)
    {
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            var size = ListXattr(from, null, 0);
            if (size < 0) return Marshal.GetLastPInvokeError() == Enotsup;
            if (size == 0) return true;
            var list = new byte[size];
            size = ListXattr(from, list, (nuint)list.Length);
            if (size < 0) return false;
            var start = 0;
            for (var i = 0; i < size; i++)
            {
                if (list[i] != 0) continue;
                var name = System.Text.Encoding.UTF8.GetString(list, start, i - start);
                start = i + 1;
                if (name.Length == 0) continue;
                var length = GetXattr(from, name, null, 0);
                if (length < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Enodata) continue; // removed meanwhile
                    return false;
                }
                var value = new byte[length];
                if (length > 0 && GetXattr(from, name, value, (nuint)value.Length) != length) return false;
                if (SetXattr(to, name, value, (nuint)value.Length, 0) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }
}
