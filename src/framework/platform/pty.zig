//! A pseudo-terminal with a child process (normally the user's shell) on its
//! slave side, for the built-in terminal. The master fd is non-blocking and
//! is polled by the event loop alongside the window connection.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");

const macos = builtin.os.tag.isDarwin();
extern "c" var environ: [*:null]?[*:0]u8;

const Allocator = std.mem.Allocator;
const log = std.log.scoped(.pty);

pub const Options = struct {
    /// The program and its arguments; argv[0] is looked up in PATH when it
    /// has no '/'. Default: `defaultShell()`.
    argv: ?[]const []const u8 = null,
    /// Working directory of the child (default: ours).
    cwd: ?[]const u8 = null,
    rows: u16 = 24,
    cols: u16 = 80,
    /// Extra "NAME=value" entries; they replace inherited ones of the same name.
    env: []const []const u8 = &.{},
};

/// How the child ended.
pub const Exit = union(enum) {
    code: u8,
    signal: u8,
};

pub const Pty = struct {
    /// Master side (non-blocking, close-on-exec).
    fd: i32,
    pid: c.pid_t,
    exit: ?Exit = null,

    /// Opens a pty and starts the child on it as a new session, with the
    /// slave as its controlling terminal and stdio.
    pub fn spawn(gpa: Allocator, opts: Options) !Pty {
        // Everything the child needs is prepared before fork: after it, only
        // async-signal-safe calls are allowed (we may have threads).
        var arena_state = std.heap.ArenaAllocator.init(gpa);
        defer arena_state.deinit();
        const arena = arena_state.allocator();

        const args = opts.argv orelse &.{defaultShell()};
        if (args.len == 0) return error.NoProgram;
        const argv = try arena.allocSentinel(?[*:0]const u8, args.len, null);
        for (args, 0..) |a, i| argv[i] = try arena.dupeSentinel(u8, a, 0);
        const envp = try buildEnv(arena, opts.env);
        const cwd_z: ?[*:0]const u8 = if (opts.cwd) |d| try arena.dupeSentinel(u8, d, 0) else null;
        const program = argv[0].?;
        const search_path = std.mem.indexOfScalar(u8, args[0], '/') == null;

        // macOS's posix_openpt takes only O_RDWR and O_NOCTTY.
        const master = c.posix_openpt(c.O_RDWR | c.O_NOCTTY | if (macos) 0 else c.O_CLOEXEC);
        if (master < 0) return error.PtyUnavailable;
        errdefer _ = c.close(master);
        if (macos) _ = c.fcntl(master, c.F_SETFD, @as(c_int, c.FD_CLOEXEC));
        if (c.grantpt(master) != 0 or c.unlockpt(master) != 0) return error.PtyUnavailable;
        var name_buf: [128]u8 = undefined;
        if (c.ptsname_r(master, &name_buf, name_buf.len) != 0) return error.PtyUnavailable;
        const slave = c.open(@ptrCast(&name_buf), c.O_RDWR | c.O_NOCTTY | c.O_CLOEXEC);
        if (slave < 0) return error.PtyUnavailable;
        defer _ = c.close(slave);

        // UTF-8 aware line editing (backspace over multi-byte characters).
        var tio: c.struct_termios = undefined;
        if (c.tcgetattr(slave, &tio) == 0) {
            tio.c_iflag |= c.IUTF8;
            _ = c.tcsetattr(slave, c.TCSANOW, &tio);
        }
        setSize(master, opts.rows, opts.cols);
        // Without close_range (macOS), descriptors are closed one by one.
        const max_fd: c_int = if (macos) @intCast(std.math.clamp(c.sysconf(c._SC_OPEN_MAX), 256, 65536)) else 0;

        const pid = c.fork();
        if (pid < 0) return error.ForkFailed;
        if (pid == 0) {
            // Child.
            _ = c.setsid();
            _ = c.ioctl(slave, c.TIOCSCTTY, @as(c_int, 0));
            _ = c.dup2(slave, 0);
            _ = c.dup2(slave, 1);
            _ = c.dup2(slave, 2);
            // Don't leak the editor's descriptors (sockets, pipes) into the shell.
            if (macos) {
                var fd: c_int = 3;
                while (fd < max_fd) : (fd += 1) _ = c.close(fd);
            } else {
                _ = std.os.linux.close_range(3, std.math.maxInt(i32), .{ .UNSHARE = false, .CLOEXEC = false });
            }
            // The editor ignores SIGPIPE; ignored signals survive exec.
            const dfl: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.DFL }, .mask = std.posix.sigemptyset(), .flags = 0 };
            for ([_]std.posix.SIG{ .PIPE, .INT, .QUIT, .TERM, .HUP, .CHLD, .TSTP, .TTIN, .TTOU }) |sig| std.posix.sigaction(sig, &dfl, null);
            const none = std.posix.sigemptyset();
            std.posix.sigprocmask(std.posix.SIG.SETMASK, &none, null);
            if (cwd_z) |d| _ = c.chdir(d);
            if (search_path) {
                if (macos) {
                    // No execvpe: execvp searches PATH in the environment it gets.
                    environ = @ptrCast(envp.ptr);
                    _ = c.execvp(program, @ptrCast(argv.ptr));
                } else {
                    _ = c.execvpe(program, @ptrCast(argv.ptr), @ptrCast(envp.ptr));
                }
            } else {
                _ = c.execve(program, @ptrCast(argv.ptr), @ptrCast(envp.ptr));
            }
            c._exit(127);
        }

        const flags = c.fcntl(master, c.F_GETFL);
        _ = c.fcntl(master, c.F_SETFL, flags | c.O_NONBLOCK);
        return .{ .fd = master, .pid = pid };
    }

    /// Hangs up on the child (SIGHUP, like closing a terminal window) and
    /// reaps it, killing it if it doesn't go away within ~50 ms.
    pub fn deinit(self: *Pty) void {
        if (self.exit == null) {
            // The whole session: the shell and its jobs.
            _ = c.kill(-self.pid, c.SIGHUP);
            _ = c.kill(self.pid, c.SIGHUP);
        }
        _ = c.close(self.fd);
        if (self.exit == null) {
            var tries: usize = 0;
            while (self.checkExit() == null) : (tries += 1) {
                if (tries == 50) {
                    _ = c.kill(-self.pid, c.SIGKILL);
                    _ = c.kill(self.pid, c.SIGKILL);
                    var status: c_int = 0;
                    _ = c.waitpid(self.pid, &status, 0);
                    break;
                }
                var req: std.c.timespec = .{ .sec = 0, .nsec = std.time.ns_per_ms };
                _ = std.c.nanosleep(&req, null);
            }
        }
        self.* = undefined;
    }

    pub const ReadResult = union(enum) {
        data: usize,
        /// Nothing to read right now.
        would_block,
        /// The slave side is closed (every process using it has exited).
        eof,
    };

    pub fn read(self: *Pty, buf: []u8) ReadResult {
        const n = c.read(self.fd, buf.ptr, buf.len);
        if (n > 0) return .{ .data = @intCast(n) };
        if (n == 0) return .eof;
        const err = std.posix.errno(n);
        return switch (err) {
            .AGAIN, .INTR => .would_block,
            // EIO: the last slave fd was closed.
            else => .eof,
        };
    }

    /// Writes as much of `bytes` as the pty accepts now; returns the count.
    pub fn write(self: *Pty, bytes: []const u8) usize {
        var done: usize = 0;
        while (done < bytes.len) {
            const n = c.write(self.fd, bytes[done..].ptr, bytes.len - done);
            if (n <= 0) {
                if (n < 0 and std.posix.errno(n) == .INTR) continue;
                break;
            }
            done += @intCast(n);
        }
        return done;
    }

    /// Tells the child its new size (it gets SIGWINCH).
    pub fn resize(self: *Pty, rows: u16, cols: u16) void {
        setSize(self.fd, rows, cols);
    }

    /// Reaps the child if it has exited (non-blocking).
    pub fn checkExit(self: *Pty) ?Exit {
        if (self.exit) |e| return e;
        var status: c_int = 0;
        const r = c.waitpid(self.pid, &status, c.WNOHANG);
        if (r != self.pid) return null;
        const s: u32 = @bitCast(status);
        self.exit = if (s & 0x7f == 0) .{ .code = @truncate(s >> 8) } else .{ .signal = @truncate(s & 0x7f) };
        return self.exit;
    }

    /// The child's current working directory (`/proc/<pid>/cwd`, or
    /// proc_pidinfo on macOS), if readable.
    pub fn currentDir(self: *const Pty, buf: []u8) ?[]const u8 {
        if (macos) {
            if (c.je_process_cwd(self.pid, buf.ptr, buf.len) != 0) return null;
            return std.mem.sliceTo(buf, 0);
        }
        var link_buf: [64]u8 = undefined;
        const link = std.fmt.bufPrintSentinel(&link_buf, "/proc/{d}/cwd", .{self.pid}, 0) catch return null;
        const n = std.c.readlink(link.ptr, buf.ptr, buf.len);
        if (n <= 0) return null;
        return buf[0..@intCast(n)];
    }
};

/// The user's shell: $SHELL, except inside `nix develop` / `nix-shell`
/// (IN_NIX_SHELL), which point $SHELL at a bash built without readline;
/// there, or with $SHELL unset, the login shell from the passwd database.
/// The result may live in libc's static passwd buffer: copy it.
pub fn defaultShell() []const u8 {
    const env_shell: []const u8 = if (std.c.getenv("SHELL")) |s| std.mem.span(s) else "";
    if (env_shell.len > 0 and std.c.getenv("IN_NIX_SHELL") == null) return env_shell;
    if (std.c.getpwuid(std.c.getuid())) |pw| if (pw.shell) |s| {
        const login = std.mem.span(s);
        if (login.len > 0) return login;
    };
    return if (env_shell.len > 0) env_shell else "/bin/sh";
}

fn setSize(fd: i32, rows: u16, cols: u16) void {
    var ws: c.struct_winsize = .{ .ws_row = rows, .ws_col = cols, .ws_xpixel = 0, .ws_ypixel = 0 };
    _ = c.ioctl(fd, c.TIOCSWINSZ, &ws);
}

/// Our environment with terminal-related variables replaced.
fn buildEnv(arena: Allocator, extra: []const []const u8) ![:null]?[*:0]const u8 {
    const defaults = [_][]const u8{ "TERM=xterm-256color", "COLORTERM=truecolor", "TERM_PROGRAM=cs-zig" };
    // Stale sizes would override the pty's.
    const drop = [_][]const u8{ "COLUMNS", "LINES" };
    var list: std.ArrayList(?[*:0]const u8) = .empty;
    var i: usize = 0;
    outer: while (std.c.environ[i]) |entry| : (i += 1) {
        const s = std.mem.span(entry);
        const eq = std.mem.indexOfScalar(u8, s, '=') orelse continue;
        const name = s[0..eq];
        for (drop) |d| if (std.mem.eql(u8, name, d)) continue :outer;
        for (defaults) |d| if (sameName(d, name)) continue :outer;
        for (extra) |d| if (sameName(d, name)) continue :outer;
        try list.append(arena, entry);
    }
    defaults: for (defaults) |d| {
        const name = d[0..std.mem.indexOfScalar(u8, d, '=').?];
        for (extra) |e| if (sameName(e, name)) continue :defaults;
        try list.append(arena, try arena.dupeSentinel(u8, d, 0));
    }
    for (extra) |e| try list.append(arena, try arena.dupeSentinel(u8, e, 0));
    return list.toOwnedSliceSentinel(arena, null);
}

fn sameName(entry: []const u8, name: []const u8) bool {
    return entry.len > name.len and entry[name.len] == '=' and std.mem.startsWith(u8, entry, name);
}

test "runs a command on a pty and reports its exit" {
    var pty = try Pty.spawn(std.testing.allocator, .{ .argv = &.{ "/bin/sh", "-c", "printf 'hello %s' \"$TERM\"; exit 3" }, .cols = 40, .rows = 10 });
    defer pty.deinit();
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(std.testing.allocator);
    var buf: [256]u8 = undefined;
    var spins: usize = 0;
    while (spins < 2000) : (spins += 1) {
        switch (pty.read(&buf)) {
            .data => |n| try out.appendSlice(std.testing.allocator, buf[0..n]),
            .eof => break,
            .would_block => {
                var req: std.c.timespec = .{ .sec = 0, .nsec = std.time.ns_per_ms };
                _ = std.c.nanosleep(&req, null);
            },
        }
    }
    try std.testing.expectEqualStrings("hello xterm-256color", out.items);
    var waited: usize = 0;
    while (pty.checkExit() == null and waited < 1000) : (waited += 1) {
        var req: std.c.timespec = .{ .sec = 0, .nsec = std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
    }
    try std.testing.expectEqual(Exit{ .code = 3 }, pty.checkExit().?);
}

test "starts in the requested directory with the requested size" {
    var pty = try Pty.spawn(std.testing.allocator, .{ .argv = &.{ "/bin/sh", "-c", "pwd; stty size" }, .cwd = "/", .cols = 33, .rows = 7 });
    defer pty.deinit();
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(std.testing.allocator);
    var buf: [256]u8 = undefined;
    var spins: usize = 0;
    while (spins < 2000) : (spins += 1) {
        switch (pty.read(&buf)) {
            .data => |n| try out.appendSlice(std.testing.allocator, buf[0..n]),
            .eof => break,
            .would_block => {
                var req: std.c.timespec = .{ .sec = 0, .nsec = std.time.ns_per_ms };
                _ = std.c.nanosleep(&req, null);
            },
        }
    }
    try std.testing.expectEqualStrings("/\r\n7 33\r\n", out.items);
}
