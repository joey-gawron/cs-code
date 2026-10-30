using System.Runtime.InteropServices;
using System.Text;
using CsCode.Platform.Io;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// lsp-core-8: saving keeps the file — hard links, owner, group, mode, extended attributes — as VS Code (which writes in place) and Vim
/// (backupcopy=auto) do. A rename-over is used only when it loses nothing; otherwise the content is written in place, and a write that fails part
/// way puts the old content back.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")] // every test starts with RequireLinux(), which skips elsewhere
public sealed class FileIdentityOnWriteTests : IDisposable
{
    private readonly FsFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsRoot => UnixNative.EffectiveUid() == 0;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("statx and xattrs are Linux-only; elsewhere only the mode is kept");
    }

    private static UnixFileIdentity Stat(string path) => UnixNative.TryStat(path) ?? throw new InvalidOperationException("statx failed");

    private static ulong Inode(string path)
    {
        var buffer = new byte[256];
        Assert.Equal(0, StatX(-100, path, 0, 0x100 /* STATX_INO */, buffer));
        return BitConverter.ToUInt64(buffer, 32);
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, int flags);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? value, nuint size);

    private string HardLinked(string content)
    {
        var f = _f.P("f.txt");
        File.WriteAllText(f, content);
        Assert.Equal(0, Link(f, _f.P("g.txt")));
        return f;
    }

    [Fact]
    public async Task Saving_a_hard_linked_file_updates_every_name_of_it()
    {
        RequireLinux();
        var f = HardLinked("a\n");
        var inode = Inode(f);

        await _f.Fs.WriteFileAsync(_f.U("f.txt"), "b\n"u8.ToArray(), ct: Ct);

        Assert.Equal("b\n", File.ReadAllText(f));
        Assert.Equal("b\n", File.ReadAllText(_f.P("g.txt")));   // the reviewer's repro printed 'a'
        Assert.Equal(inode, Inode(f));
        Assert.Equal(2u, Stat(f).Links);
        Assert.Equal(["f.txt", "g.txt"], Directory.GetFiles(_f.Temp.Path).Select(Path.GetFileName).Order()); // no temp file left
    }

    [Fact]
    public async Task Saving_keeps_the_executable_bit_and_set_id_bits()
    {
        RequireLinux();
        var f = _f.P("run.sh");
        File.WriteAllText(f, "#!/bin/sh\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.SetGroup;
        File.SetUnixFileMode(f, mode);
        await _f.Fs.WriteFileAsync(_f.U("run.sh"), "#!/bin/sh\necho hi\n"u8.ToArray(), ct: Ct);
        Assert.Equal(mode, File.GetUnixFileMode(f));
    }

    [Fact]
    public async Task Saving_keeps_a_group_the_user_belongs_to()
    {
        RequireLinux();
        var groups = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Groups:", StringComparison.Ordinal))[7..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToArray();
        var f = _f.P("shared.txt");
        File.WriteAllText(f, "a");
        var own = Stat(f);
        var other = groups.FirstOrDefault(g => g != own.Gid, uint.MaxValue);
        if (other == uint.MaxValue) { Assert.Skip("the user belongs to a single group"); return; }
        Assert.True(UnixNative.TryChown(f, own.Uid, other));
        var inode = Inode(f);

        await _f.Fs.WriteFileAsync(_f.U("shared.txt"), "b"u8.ToArray(), ct: Ct);

        Assert.Equal("b", File.ReadAllText(f));
        Assert.Equal(other, Stat(f).Gid);   // a new file would have had the user's primary group
        Assert.NotEqual(inode, Inode(f));   // and it was still an atomic replace: the group could be given to the temp file
    }

    [Fact]
    public async Task Saving_keeps_extended_attributes()
    {
        RequireLinux();
        var f = _f.P("tagged.txt");
        File.WriteAllText(f, "a");
        var value = "kept"u8.ToArray();
        if (SetXattr(f, "user.cscode.test", value, (nuint)value.Length, 0) != 0) { Assert.Skip($"no user xattrs on this file system (errno {Marshal.GetLastPInvokeError()})"); return; }

        await _f.Fs.WriteFileAsync(_f.U("tagged.txt"), "b"u8.ToArray(), ct: Ct);

        var buffer = new byte[16];
        var n = GetXattr(f, "user.cscode.test", buffer, (nuint)buffer.Length);
        Assert.Equal("kept", Encoding.UTF8.GetString(buffer, 0, (int)Math.Max(0, n)));
        Assert.Equal("b", File.ReadAllText(f));
    }

    [Fact]
    public async Task A_writable_file_in_a_read_only_directory_is_saved_in_place()
    {
        RequireLinux();
        if (IsRoot) { Assert.Skip("root may create files anywhere"); return; }
        var dir = _f.P("locked");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "x.txt");
        File.WriteAllText(f, "a");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await _f.Fs.WriteFileAsync(new Uri(f), "b"u8.ToArray(), ct: Ct);
            Assert.Equal("b", File.ReadAllText(f));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task An_in_place_save_that_fails_part_way_puts_the_old_content_back()
    {
        RequireLinux();
        var old = new string('o', 300_000);
        var f = HardLinked(old);
        var chunks = 0;
        _f.Fs.ChunkHook = () => { if (++chunks == 2) throw new IOException("disk full (simulated)"); };
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.WriteFileAsync(_f.U("f.txt"), Encoding.ASCII.GetBytes(new string('n', 400_000)), ct: Ct));
        }
        finally { _f.Fs.ChunkHook = null; }

        Assert.Equal(old, File.ReadAllText(f));
        Assert.Equal(old, File.ReadAllText(_f.P("g.txt")));
        Assert.Equal(2u, Stat(f).Links);
    }

    [Fact]
    public async Task A_cancelled_in_place_save_puts_the_old_content_back()
    {
        RequireLinux();
        var old = new string('o', 200_000);
        var f = HardLinked(old);
        using var cts = new CancellationTokenSource();
        _f.Fs.ChunkHook = cts.Cancel;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await _f.Fs.WriteFileAsync(_f.U("f.txt"), Encoding.ASCII.GetBytes(new string('n', 500_000)), ct: cts.Token));
        }
        finally { _f.Fs.ChunkHook = null; }
        Assert.Equal(old, File.ReadAllText(_f.P("g.txt")));
    }

    [Fact]
    public async Task A_failure_before_an_in_place_write_leaves_the_file_untouched()
    {
        RequireLinux();
        var f = HardLinked("keep");
        _f.Fs.BeforeAtomicCommit = () => throw new IOException("injected");
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await _f.Fs.WriteFileAsync(_f.U("f.txt"), "new"u8.ToArray(), ct: Ct));
        }
        finally { _f.Fs.BeforeAtomicCommit = null; }
        Assert.Equal("keep", File.ReadAllText(f));
        Assert.Equal(["f.txt", "g.txt"], Directory.GetFiles(_f.Temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Writing_through_the_router_tells_savers_that_files_keep_their_identity()
    {
        var router = new FileSystem.FileSystemRouter(_f.Fs);
        Assert.True(router.PreservesFileOnWrite(_f.U("a.txt")));
        Assert.False(((Core.Platform.IFileSystem)router).PreservesFileOnWrite(new Uri("ssh://host/x"))); // no provider: the local fs says no
    }
}
