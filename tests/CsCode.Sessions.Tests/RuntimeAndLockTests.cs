using System.Diagnostics;
using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.Sessions.Tests;

public class RuntimeDirectoryTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) => k => vars.FirstOrDefault(v => v.Key == k).Value;

    [Fact]
    public void Without_cscode_home_the_per_user_runtime_dir_is_used()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux layout");
        Assert.Equal("/run/user/1000/cscode", RuntimeDirectory.Resolve(Env(("XDG_RUNTIME_DIR", "/run/user/1000")), 1000).Path);
        Assert.Equal("/tmp/cscode-1000", RuntimeDirectory.Resolve(Env(), 1000).Path);
        Assert.Equal("/tmp/cscode-1000", RuntimeDirectory.Resolve(Env(("XDG_RUNTIME_DIR", "relative/is/invalid")), 1000).Path);
    }

    [Fact]
    public void Cscode_home_gets_its_own_runtime_dir_so_tests_never_reach_the_real_one()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux layout");
        var shortHome = RuntimeDirectory.Resolve(Env(("CSCODE_HOME", "/tmp/h1"), ("XDG_RUNTIME_DIR", "/run/user/1000")), 1000);
        Assert.Equal("/tmp/h1/run", shortHome.Path);

        var longHome = "/home/someone/" + new string('d', 120);
        var a = RuntimeDirectory.Resolve(Env(("CSCODE_HOME", longHome), ("XDG_RUNTIME_DIR", "/run/user/1000")), 1000);
        var b = RuntimeDirectory.Resolve(Env(("CSCODE_HOME", longHome + "x"), ("XDG_RUNTIME_DIR", "/run/user/1000")), 1000);
        Assert.StartsWith("/run/user/1000/cscode-h", a.Path);
        Assert.NotEqual("/run/user/1000/cscode", a.Path);
        Assert.NotEqual(a.Path, b.Path);
        Assert.True(a.EndpointOf(4_194_304).Length < 104, "socket paths fit sun_path");
    }

    [Fact]
    public void Ensure_creates_a_private_directory_and_tightens_a_loose_one()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix modes"); return; }
        var root = Path.Combine("/tmp", "csrt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rt = new RuntimeDirectory(Path.Combine(root, "run"), "");
            Assert.Null(rt.Ensure());
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(Private, File.GetUnixFileMode(rt.Path));
            Assert.True(Directory.Exists(rt.LocksDir));

            File.SetUnixFileMode(rt.Path, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherWrite);
            Assert.Null(rt.Ensure());
            Assert.Equal(Private, File.GetUnixFileMode(rt.Path));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class FileLockTests
{
    [Fact]
    public void A_lock_is_exclusive_until_disposed()
    {
        using var rt = new TestRuntime();
        var path = rt.Runtime.SessionLockPath("work");
        using (var first = FileLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(FileLock.TryAcquire(path));
            Assert.True(FileLock.IsHeldElsewhere(path));
        }
        Assert.False(FileLock.IsHeldElsewhere(path));
        using var again = FileLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AcquireAsync_waits_for_the_holder_and_gives_up_at_the_timeout()
    {
        using var rt = new TestRuntime();
        var path = rt.Runtime.LauncherLockPath;
        var holder = FileLock.TryAcquire(path)!;
        Assert.Null(await FileLock.AcquireAsync(path, TimeSpan.FromMilliseconds(80)));
        var waiting = FileLock.AcquireAsync(path, TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        holder.Dispose();
        using var got = await waiting;
        Assert.NotNull(got);
    }

    [Fact]
    public async Task A_lock_held_by_a_process_is_released_when_that_process_is_killed()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/flock")) Assert.Skip("needs util-linux flock(1)");
        using var rt = new TestRuntime();
        var path = rt.Runtime.SessionLockPath("held");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // flock(1) takes the same flock(2) lock .NET uses for FileShare.None.
        using var holder = Process.Start(new ProcessStartInfo("/usr/bin/flock", ["-x", path, "/usr/bin/sleep", "60"]) { UseShellExecute = false })!;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!FileLock.IsHeldElsewhere(path) && deadline.ElapsedMilliseconds < 5000) await Task.Delay(20);
            Assert.True(FileLock.IsHeldElsewhere(path), "flock(1) did not take the lock");

            holder.Kill(entireProcessTree: true); // kill -9: no cleanup code runs in the holder
            await holder.WaitForExitAsync();

            deadline.Restart();
            while (FileLock.IsHeldElsewhere(path) && deadline.ElapsedMilliseconds < 5000) await Task.Delay(20);
            using var mine = FileLock.TryAcquire(path);
            Assert.NotNull(mine);
        }
        finally
        {
            if (!holder.HasExited) holder.Kill(entireProcessTree: true);
        }
    }
}
