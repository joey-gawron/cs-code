using System.Diagnostics;
using CsCode.Sessions.Ipc;
using Xunit;

namespace CsCode.Sessions.Tests;

public class RuntimeDirectoryTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) => k => vars.FirstOrDefault(v => v.Key == k).Value;

    /// <summary>No /run/user directory and nothing under /tmp yet (a machine without logind).</summary>
    private static DirectoryOwner NothingThere(string path) => DirectoryOwner.Missing;

    [Fact]
    public void Without_cscode_home_the_per_user_runtime_dir_is_used()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux layout");
        Assert.Equal("/run/user/1000/cscode", RuntimeDirectory.Resolve(Env(("XDG_RUNTIME_DIR", "/run/user/1000")), 1000, NothingThere).Path);
        Assert.Equal("/tmp/cscode-1000", RuntimeDirectory.Resolve(Env(), 1000, NothingThere).Path);
        Assert.Equal("/tmp/cscode-1000", RuntimeDirectory.Resolve(Env(("XDG_RUNTIME_DIR", "relative/is/invalid")), 1000, NothingThere).Path);
    }

    [Fact]
    public void Without_xdg_runtime_dir_the_users_run_directory_is_used_when_it_exists()
    {
        // sessions-7: a shell that lost XDG_RUNTIME_DIR (su -, env -i, ssh without pam_systemd) meets the desktop's processes in the same directory.
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux layout");
        DirectoryOwner LoggedIn(string path) => path == "/run/user/1000" ? DirectoryOwner.ThisUser : DirectoryOwner.Missing;
        Assert.Equal("/run/user/1000/cscode", RuntimeDirectory.Resolve(Env(), 1000, LoggedIn).Path);
        // Someone else's /run/user/<uid> (or a planted link) is never used.
        Assert.Equal("/tmp/cscode-1000", RuntimeDirectory.Resolve(Env(), 1000, p => p == "/run/user/1000" ? DirectoryOwner.Other : DirectoryOwner.Missing).Path);
    }

    [Fact]
    public void A_tmp_name_another_user_took_first_is_skipped()
    {
        // sessions-8: another local user pre-creates /tmp/cscode-<uid> (mode 0700, theirs): the next free name, or this user's own, is used.
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux layout");
        DirectoryOwner Taken(string path) => path == "/tmp/cscode-1000" ? DirectoryOwner.Other : DirectoryOwner.Missing;
        Assert.Equal("/tmp/cscode-1000-1", RuntimeDirectory.Resolve(Env(), 1000, Taken).Path);
        DirectoryOwner TwoTaken(string path) => path is "/tmp/cscode-1000" or "/tmp/cscode-1000-1" ? DirectoryOwner.Other
            : path.StartsWith("/tmp/", StringComparison.Ordinal) ? DirectoryOwner.ThisUser : DirectoryOwner.Missing;
        Assert.Equal("/tmp/cscode-1000-2", RuntimeDirectory.Resolve(Env(), 1000, TwoTaken).Path);
        // All taken: the first name, which Ensure rejects (background mode is unavailable rather than shared with a stranger).
        Assert.Equal("/tmp/cscode-1000", RuntimeDirectory.Resolve(Env(), 1000, _ => DirectoryOwner.Other).Path);
    }

    [Fact]
    public void Probe_tells_this_users_directories_from_foreign_ones_and_links()
    {
        if (OperatingSystem.IsWindows() || IsRoot()) Assert.Skip("Unix ownership, as a non-root user");
        var root = Path.Combine("/tmp", "csrp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(DirectoryOwner.ThisUser, RuntimeDirectory.Probe(root));
            Assert.Equal(DirectoryOwner.Missing, RuntimeDirectory.Probe(Path.Combine(root, "nope")));
            Assert.Equal(DirectoryOwner.Other, RuntimeDirectory.Probe("/usr")); // root's
            File.CreateSymbolicLink(Path.Combine(root, "link"), root);
            Assert.Equal(DirectoryOwner.Other, RuntimeDirectory.Probe(Path.Combine(root, "link")));
            File.WriteAllText(Path.Combine(root, "file"), "");
            Assert.Equal(DirectoryOwner.Other, RuntimeDirectory.Probe(Path.Combine(root, "file")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Ensure_rejects_a_directory_of_another_user_and_a_symbolic_link()
    {
        // sessions-8: the mode alone proved nothing (another user's 0700 directory passed); the owner and links are checked now.
        if (OperatingSystem.IsWindows() || IsRoot()) Assert.Skip("Unix ownership, as a non-root user");
        Assert.Contains("another user", new RuntimeDirectory("/usr", "").Ensure());
        var root = Path.Combine("/tmp", "csrl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            File.CreateSymbolicLink(Path.Combine(root, "run"), target);
            Assert.Contains("symbolic link", new RuntimeDirectory(Path.Combine(root, "run"), "").Ensure());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Root may chmod anything, so the ownership tests mean nothing for it.</summary>
    private static bool IsRoot() => File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal)) is { } line
        && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2] == "0";

    [Fact]
    public void Cscode_home_gets_its_own_runtime_dir_so_tests_never_reach_the_real_one()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux layout");
        var shortHome = RuntimeDirectory.Resolve(Env(("CSCODE_HOME", "/tmp/h1"), ("XDG_RUNTIME_DIR", "/run/user/1000")), 1000, NothingThere);
        Assert.Equal("/tmp/h1/run", shortHome.Path);

        var longHome = "/home/someone/" + new string('d', 120);
        var a = RuntimeDirectory.Resolve(Env(("CSCODE_HOME", longHome), ("XDG_RUNTIME_DIR", "/run/user/1000")), 1000, NothingThere);
        var b = RuntimeDirectory.Resolve(Env(("CSCODE_HOME", longHome + "x"), ("XDG_RUNTIME_DIR", "/run/user/1000")), 1000, NothingThere);
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
    public void Session_locks_live_with_the_data_and_are_not_listed_as_sessions()
    {
        // sessions-7: the lock is found through the data directory, whatever runtime directory a launch resolved.
        using var rt = new TestRuntime();
        using var held = FileLock.TryAcquire(rt.Store.LockPathOf("work"));
        using var launcher = FileLock.TryAcquire(rt.Store.LauncherLockPath);
        Assert.NotNull(held);
        Assert.NotNull(launcher);
        Assert.StartsWith(rt.Store.SessionsDir + Path.DirectorySeparatorChar, rt.Store.LockPathOf("work"));
        Assert.NotEqual(rt.Store.LockPathOf("launcher"), rt.Store.LauncherLockPath); // a session may be called "launcher"
        Assert.Empty(rt.Store.List());
        Assert.True(FileLock.IsHeldElsewhere(new SessionStore(Path.Combine(rt.Root, "data")).LockPathOf("work")));
    }

    /// <summary>A directory this user cannot write into (null when chmod does not restrict this user, e.g. root).</summary>
    internal static string? ReadOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(path, "probe"), "");
            MakeWritable(path);
            return null; // chmod does not stop this user
        }
        catch (UnauthorizedAccessException) { return path; }
    }

    internal static void MakeWritable(string path)
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void A_lock_another_process_holds_is_told_from_one_that_cannot_be_taken_at_all()
    {
        // sessions-7: with the locks in the data directory, a read-only data directory made every launch wait 15 s for the launcher lock and
        // every session process refuse with "owned by another CsCode process": both looked like a lock someone held.
        using var rt = new TestRuntime();
        var path = rt.Store.LockPathOf("work");
        using (var first = FileLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(FileLock.TryAcquire(path, out var problem));
            Assert.Null(problem); // held: waiting may help
        }
        var ro = ReadOnlyDirectory(Path.Combine(rt.Root, "ro"));
        if (ro is null) Assert.Skip("this user can write into a mode 0500 directory");
        try
        {
            Assert.Null(FileLock.TryAcquire(Path.Combine(ro, ".locks", "work.lock"), out var problem));
            Assert.NotNull(problem);
            var clock = Stopwatch.StartNew();
            Assert.Null(FileLock.Acquire(Path.Combine(ro, "launcher.lock"), TimeSpan.FromSeconds(10), out problem));
            Assert.NotNull(problem);
            Assert.True(clock.ElapsedMilliseconds < 2000, $"waited {clock.ElapsedMilliseconds} ms for a lock that can never be taken");
        }
        finally { MakeWritable(ro); }
    }

    [Fact]
    public void A_session_lock_takes_and_honours_the_lock_older_builds_used()
    {
        // sessions-7: an older build's process (still running after an upgrade) locks only <runtime>/locks/<name>.lock.
        using var rt = new TestRuntime();
        var legacy = rt.Runtime.LegacySessionLockPath("work");
        Assert.Equal(Path.Combine(rt.Runtime.Path, "locks", "work.lock"), legacy);
        using (var older = FileLock.TryAcquire(legacy))
        {
            Assert.NotNull(older);
            Assert.True(SessionLock.IsHeldElsewhere(rt.Store, rt.Runtime, "work"));
            Assert.Null(SessionLock.TryAcquire(rt.Store, rt.Runtime, "work"));
            Assert.False(FileLock.IsHeldElsewhere(rt.Store.LockPathOf("work"))); // the failed take let go of the new lock again
        }
        using (var mine = SessionLock.TryAcquire(rt.Store, rt.Runtime, "work"))
        {
            Assert.NotNull(mine);
            Assert.True(FileLock.IsHeldElsewhere(rt.Store.LockPathOf("work")));
            Assert.True(FileLock.IsHeldElsewhere(legacy)); // what an older build checks
        }
        Assert.False(SessionLock.IsHeldElsewhere(rt.Store, rt.Runtime, "work"));

        // A data directory that cannot hold locks is an error, not "someone else has it".
        var ro = ReadOnlyDirectory(rt.Store.LocksDir);
        if (ro is null) return;
        try { Assert.Throws<IOException>(() => SessionLock.TryAcquire(rt.Store, rt.Runtime, "other")); }
        finally { MakeWritable(ro); }
    }

    [Fact]
    public void A_lock_is_exclusive_until_disposed()
    {
        using var rt = new TestRuntime();
        var path = rt.Store.LockPathOf("work");
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
        var path = rt.Store.LauncherLockPath;
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
        var path = rt.Store.LockPathOf("held");
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
