//! Pseudo-terminals for remote terminals, with raw Linux syscalls (the
//! server has no libc): /dev/ptmx, fork, and the program exec'd on the slave
//! side as a new session. The master fd is the terminal's input and output.

const std = @import("std");
const linux = std.os.linux;

const Allocator = std.mem.Allocator;

pub const Spawned = struct { pid: linux.pid_t, master: i32 };

/// Starts `argv` on a new pty. `env` is the child's whole environment;
/// argv[0] is looked up in its PATH when it has no '/'.
pub fn spawn(alloc: Allocator, argv: []const []const u8, cwd: ?[]const u8, env: *const std.process.Environ.Map, rows: u16, cols: u16) !Spawned {
    // Everything the child needs is prepared before fork: after it, only
    // syscalls (other threads may hold locks).
    const program = try findProgram(alloc, argv[0], env.get("PATH") orelse "/usr/bin:/bin");
    const argv_z = try alloc.allocSentinel(?[*:0]const u8, argv.len, null);
    for (argv, 0..) |a, i| argv_z[i] = try alloc.dupeSentinel(u8, a, 0);
    const envp = try alloc.allocSentinel(?[*:0]const u8, env.count(), null);
    var it = env.iterator();
    var n: usize = 0;
    while (it.next()) |e| : (n += 1) envp[n] = try std.fmt.allocPrintSentinel(alloc, "{s}={s}", .{ e.key_ptr.*, e.value_ptr.* }, 0);
    const cwd_z: ?[*:0]const u8 = if (cwd) |d| try alloc.dupeSentinel(u8, d, 0) else null;

    const rc = linux.open("/dev/ptmx", .{ .ACCMODE = .RDWR, .NOCTTY = true, .CLOEXEC = true }, 0);
    if (linux.errno(rc) != .SUCCESS) return error.PtyUnavailable;
    const master: i32 = @intCast(rc);
    errdefer _ = linux.close(master);
    var unlock: c_int = 0;
    if (linux.errno(linux.ioctl(master, linux.T.IOCSPTLCK, @intFromPtr(&unlock))) != .SUCCESS) return error.PtyUnavailable;
    var index: c_uint = 0;
    if (linux.errno(linux.ioctl(master, linux.T.IOCGPTN, @intFromPtr(&index))) != .SUCCESS) return error.PtyUnavailable;
    const slave_path = try std.fmt.allocPrintSentinel(alloc, "/dev/pts/{d}", .{index}, 0);
    setSize(master, rows, cols);

    const pid_rc = linux.fork();
    if (linux.errno(pid_rc) != .SUCCESS) return error.ForkFailed;
    if (pid_rc == 0) {
        // Child: a new session whose controlling terminal is the slave.
        _ = linux.setsid();
        const s = linux.open(slave_path, .{ .ACCMODE = .RDWR }, 0);
        if (linux.errno(s) != .SUCCESS) linux.exit_group(126);
        const slave: i32 = @intCast(s);
        _ = linux.ioctl(slave, linux.T.IOCSCTTY, 0);
        _ = linux.dup2(slave, 0);
        _ = linux.dup2(slave, 1);
        _ = linux.dup2(slave, 2);
        _ = linux.close_range(3, std.math.maxInt(i32), .{ .UNSHARE = false, .CLOEXEC = false });
        // The server ignores SIGPIPE; ignored signals survive exec.
        const dfl: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.DFL }, .mask = std.posix.sigemptyset(), .flags = 0 };
        for ([_]std.posix.SIG{ .PIPE, .INT, .QUIT, .TERM, .HUP, .CHLD, .TSTP, .TTIN, .TTOU }) |sig| std.posix.sigaction(sig, &dfl, null);
        const none = std.posix.sigemptyset();
        std.posix.sigprocmask(std.posix.SIG.SETMASK, &none, null);
        if (cwd_z) |d| _ = linux.chdir(d);
        _ = linux.execve(program, argv_z, envp);
        linux.exit_group(127);
    }
    return .{ .pid = @intCast(pid_rc), .master = master };
}

pub fn setSize(fd: i32, rows: u16, cols: u16) void {
    var ws: std.posix.winsize = .{ .row = rows, .col = cols, .xpixel = 0, .ypixel = 0 };
    _ = linux.ioctl(fd, linux.T.IOCSWINSZ, @intFromPtr(&ws));
}

/// Reaps `pid`: its exit code, or minus the signal that killed it.
pub fn wait(pid: linux.pid_t) i64 {
    var status: i32 = 0;
    while (true) {
        const rc = linux.wait4(pid, &status, 0, null);
        switch (linux.errno(rc)) {
            .SUCCESS => break,
            .INTR => continue,
            else => return -1,
        }
    }
    const bits: u32 = @bitCast(status);
    if (bits & 0x7f == 0) return (bits >> 8) & 0xff;
    return -@as(i64, bits & 0x7f);
}

fn findProgram(alloc: Allocator, name: []const u8, path_env: []const u8) ![:0]const u8 {
    if (std.mem.indexOfScalar(u8, name, '/') != null) return alloc.dupeSentinel(u8, name, 0);
    var it = std.mem.splitScalar(u8, path_env, ':');
    while (it.next()) |dir| {
        if (dir.len == 0) continue;
        const full = try std.fmt.allocPrintSentinel(alloc, "{s}/{s}", .{ dir, name }, 0);
        if (linux.errno(linux.access(full, linux.X_OK)) == .SUCCESS) return full;
    }
    return error.FileNotFound;
}
