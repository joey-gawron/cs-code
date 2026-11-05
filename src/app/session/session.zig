//! Session daemon: one long-lived editor process per session that `cs-zig`
//! invocations connect to over a Unix socket (like `emacsclient` or
//! `subl`). Buffers, project scans and language servers stay alive when the
//! window closes, so reopening is instant.
//!
//! Protocol: one JSON object per line from the client,
//!   {"cmd":"open","cwd":"/dir","paths":["a.zig"],"wait":false}
//!   {"cmd":"quit"} / {"cmd":"ping"} / {"cmd":"save"} / {"cmd":"info"}
//! answered with "ok" lines ("ok <pid>" to ping, "ok <pid> <tabs> <unsaved>
//! <folder>" to info); `wait` clients get "done" when their files close.
//! Saved sessions on disk are store.zig's; what keeps them current is persist.zig.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");
const platform = @import("../../framework/platform/platform.zig");

const Allocator = std.mem.Allocator;
const log = std.log.scoped(.session);

pub const Command = enum { open, quit, ping, save, info };

pub const Request = struct {
    cmd: Command = .open,
    cwd: []const u8 = "",
    paths: []const []const u8 = &.{},
    wait: bool = false,
};

/// Where the sockets are: `$CS_ZIG_HOME/run` when set (tests and portable
/// installs never meet the user's own sessions), else
/// $XDG_RUNTIME_DIR/cs-zig, else /tmp/cs-zig-<uid>. Created, mode 0700.
pub fn socketDir(buf: []u8) ?[:0]const u8 {
    const dir = blk: {
        if (std.c.getenv("CS_ZIG_HOME")) |x| if (x[0] != 0) {
            break :blk std.fmt.bufPrintSentinel(buf, "{s}/run", .{std.mem.span(x)}, 0) catch return null;
        };
        if (std.c.getenv("XDG_RUNTIME_DIR")) |x| {
            break :blk std.fmt.bufPrintSentinel(buf, "{s}/cs-zig", .{std.mem.span(x)}, 0) catch return null;
        }
        break :blk std.fmt.bufPrintSentinel(buf, "/tmp/cs-zig-{d}", .{c.getuid()}, 0) catch return null;
    };
    // A fresh CS_ZIG_HOME has no parent for `run` yet.
    if (std.mem.lastIndexOfScalar(u8, dir, '/')) |i| if (i > 0) {
        var parent: [std.fs.max_path_bytes]u8 = undefined;
        @memcpy(parent[0..i], dir[0..i]);
        parent[i] = 0;
        _ = c.mkdir(@ptrCast(&parent), 0o700);
    };
    _ = c.mkdir(dir.ptr, 0o700);
    return dir;
}

/// Socket path for a named session: `<socketDir>/<name>.sock`.
pub fn socketPath(buf: []u8, name: []const u8) ?[:0]const u8 {
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir = socketDir(&dir_buf) orelse return null;
    return std.fmt.bufPrintSentinel(buf, "{s}/{s}.sock", .{ dir, name }, 0) catch null;
}

fn unixAddr(path: [:0]const u8) ?c.struct_sockaddr_un {
    var addr: c.struct_sockaddr_un = std.mem.zeroes(c.struct_sockaddr_un);
    addr.sun_family = c.AF_UNIX;
    if (path.len >= addr.sun_path.len) return null;
    @memcpy(addr.sun_path[0..path.len], path);
    return addr;
}

/// A close-on-exec Unix stream socket (macOS has no SOCK_CLOEXEC, so its
/// flags are set afterwards there).
fn unixSocket(nonblock: bool) i32 {
    if (builtin.os.tag == .linux) {
        return c.socket(c.AF_UNIX, c.SOCK_STREAM | c.SOCK_CLOEXEC | if (nonblock) c.SOCK_NONBLOCK else 0, 0);
    }
    const fd = c.socket(c.AF_UNIX, c.SOCK_STREAM, 0);
    if (fd >= 0) setFlags(fd, nonblock);
    return fd;
}

fn setFlags(fd: i32, nonblock: bool) void {
    _ = c.fcntl(fd, c.F_SETFD, @as(c_int, c.FD_CLOEXEC));
    if (nonblock) _ = c.fcntl(fd, c.F_SETFL, c.fcntl(fd, c.F_GETFL) | c.O_NONBLOCK);
}

fn acceptClient(listen_fd: i32) i32 {
    if (builtin.os.tag == .linux) return std.c.accept4(listen_fd, null, null, c.SOCK_CLOEXEC);
    const fd = std.c.accept(listen_fd, null, null);
    if (fd >= 0) setFlags(fd, false);
    return fd;
}

/// Connects to a running session, or returns null.
pub fn connect(path: [:0]const u8) ?i32 {
    const fd = unixSocket(false);
    if (fd < 0) return null;
    var addr = unixAddr(path) orelse {
        _ = c.close(fd);
        return null;
    };
    if (std.c.connect(fd, @ptrCast(&addr), @sizeOf(c.struct_sockaddr_un)) != 0) {
        _ = c.close(fd);
        return null;
    }
    return fd;
}

pub fn writeAll(fd: i32, bytes: []const u8) bool {
    var off: usize = 0;
    while (off < bytes.len) {
        const n = c.write(fd, bytes[off..].ptr, bytes.len - off);
        if (n <= 0) return false;
        off += @intCast(n);
    }
    return true;
}

/// Client side: sends `req` and blocks until "ok" (or "done" when waiting).
/// Returns false if the session didn't answer.
pub fn sendRequest(gpa: Allocator, fd: i32, req: Request) bool {
    const json = std.json.Stringify.valueAlloc(gpa, req, .{}) catch return false;
    defer gpa.free(json);
    if (!writeAll(fd, json) or !writeAll(fd, "\n")) return false;
    var buf: [256]u8 = undefined;
    var have: usize = 0;
    var got_ok = false;
    while (true) {
        const n = c.read(fd, buf[have..].ptr, buf.len - have);
        if (n <= 0) return got_ok;
        have += @intCast(n);
        while (std.mem.indexOfScalar(u8, buf[0..have], '\n')) |nl| {
            const line = buf[0..nl];
            if (std.mem.startsWith(u8, line, "ok")) {
                got_ok = true;
                if (!req.wait) return true;
            } else if (std.mem.eql(u8, line, "done")) return true;
            std.mem.copyForwards(u8, &buf, buf[nl + 1 .. have]);
            have -= nl + 1;
        }
        if (have == buf.len) have = 0;
    }
}

/// Sends `req` and returns the first reply line (in `buf`), for the verbs
/// that answer with data.
pub fn requestLine(gpa: Allocator, fd: i32, req: Request, buf: []u8) ?[]const u8 {
    const json = std.json.Stringify.valueAlloc(gpa, req, .{}) catch return null;
    defer gpa.free(json);
    if (!writeAll(fd, json) or !writeAll(fd, "\n")) return null;
    var have: usize = 0;
    while (have < buf.len) {
        const n = c.read(fd, buf[have..].ptr, buf.len - have);
        if (n <= 0) return null;
        have += @intCast(n);
        if (std.mem.indexOfScalar(u8, buf[0..have], '\n')) |nl| return buf[0..nl];
    }
    return null;
}

pub const Running = struct { name: []const u8, pid: i32 };

/// The sessions with a live process, found through their sockets. Sockets
/// nobody answers on are removed. Strings live in `arena`.
pub fn running(gpa: Allocator, io: std.Io, arena: Allocator) []Running {
    var out: std.ArrayList(Running) = .empty;
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_path = socketDir(&dir_buf) orelse return out.items;
    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return out.items;
    defer dir.close(io);
    var it = dir.iterate();
    while (it.next(io) catch null) |entry| {
        if (!std.mem.endsWith(u8, entry.name, ".sock")) continue;
        const name = entry.name[0 .. entry.name.len - ".sock".len];
        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        const path = std.fmt.bufPrintSentinel(&path_buf, "{s}/{s}", .{ dir_path, entry.name }, 0) catch continue;
        const fd = connect(path) orelse {
            _ = c.unlink(path.ptr);
            continue;
        };
        defer _ = c.close(fd);
        var line: [64]u8 = undefined;
        const reply = requestLine(gpa, fd, .{ .cmd = .ping }, &line) orelse continue;
        const pid = if (std.mem.startsWith(u8, reply, "ok ")) std.fmt.parseInt(i32, reply[3..], 10) catch 0 else 0;
        out.append(arena, .{ .name = arena.dupe(u8, name) catch continue, .pid = pid }) catch {};
    }
    return out.items;
}

pub const Client = struct {
    fd: i32,
    buf: std.ArrayList(u8) = .empty,
    /// Files this client is waiting on (--wait); "done" when all are closed.
    waiting: std.ArrayList([]u8) = .empty,
    closed: bool = false,
};

pub const Handler = struct {
    ctx: *anyopaque,
    /// Returns the absolute paths opened (for --wait bookkeeping).
    onRequest: *const fn (ctx: *anyopaque, client: *Client, req: Request) void,
};

pub const Server = struct {
    gpa: Allocator,
    listen_fd: i32,
    path: [:0]u8,
    clients: std.ArrayList(*Client) = .empty,

    pub fn listen(gpa: Allocator, path: [:0]const u8) !Server {
        // A socket left behind by a crashed session refuses connections.
        if (connect(path)) |fd| {
            _ = c.close(fd);
            return error.SessionRunning;
        }
        _ = c.unlink(path.ptr);
        const fd = unixSocket(true);
        if (fd < 0) return error.SocketFailed;
        errdefer _ = c.close(fd);
        var addr = unixAddr(path) orelse return error.PathTooLong;
        if (std.c.bind(fd, @ptrCast(&addr), @sizeOf(c.struct_sockaddr_un)) != 0) return error.BindFailed;
        if (c.listen(fd, 8) != 0) return error.ListenFailed;
        log.debug("listening on {s}", .{path});
        return .{ .gpa = gpa, .listen_fd = fd, .path = try gpa.dupeSentinel(u8, path, 0) };
    }

    pub fn deinit(self: *Server) void {
        for (self.clients.items) |cl| self.freeClient(cl);
        self.clients.deinit(self.gpa);
        _ = c.close(self.listen_fd);
        _ = c.unlink(self.path.ptr);
        self.gpa.free(self.path);
    }

    /// The session was renamed: its socket follows, so `--session NEW` finds it.
    pub fn rename(self: *Server, new_path: [:0]const u8) !void {
        if (std.c.rename(self.path.ptr, new_path.ptr) != 0) return error.RenameFailed;
        const copy = try self.gpa.dupeSentinel(u8, new_path, 0);
        self.gpa.free(self.path);
        self.path = copy;
    }

    fn freeClient(self: *Server, cl: *Client) void {
        _ = c.close(cl.fd);
        cl.buf.deinit(self.gpa);
        for (cl.waiting.items) |p| self.gpa.free(p);
        cl.waiting.deinit(self.gpa);
        self.gpa.destroy(cl);
    }

    /// Appends the fds to wait on (listen socket first, then clients).
    pub fn pollFds(self: *const Server, out: *std.ArrayList(platform.PollFd), gpa: Allocator) void {
        out.append(gpa, .{ .fd = self.listen_fd }) catch {};
        for (self.clients.items) |cl| out.append(gpa, .{ .fd = cl.fd }) catch {};
    }

    /// Handles readiness for the fds that `pollFds` produced.
    pub fn service(self: *Server, fds: []const platform.PollFd, handler: Handler) void {
        if (fds.len == 0) return;
        // Clients first: the list may grow when accepting.
        const n_clients = @min(fds.len - 1, self.clients.items.len);
        for (fds[1..][0..n_clients], self.clients.items[0..n_clients]) |f, cl| {
            if (f.revents != 0) self.readClient(cl, handler);
        }
        if (fds[0].readable()) {
            while (true) {
                const cfd = acceptClient(self.listen_fd);
                if (cfd < 0) break;
                const cl = self.gpa.create(Client) catch {
                    _ = c.close(cfd);
                    break;
                };
                cl.* = .{ .fd = cfd };
                self.clients.append(self.gpa, cl) catch self.freeClient(cl);
            }
        }
        // Drop disconnected clients.
        var i: usize = 0;
        while (i < self.clients.items.len) {
            const cl = self.clients.items[i];
            if (cl.closed) {
                _ = self.clients.swapRemove(i);
                self.freeClient(cl);
            } else i += 1;
        }
    }

    fn readClient(self: *Server, cl: *Client, handler: Handler) void {
        var tmp: [4096]u8 = undefined;
        const n = c.read(cl.fd, &tmp, tmp.len);
        if (n <= 0) {
            cl.closed = true;
            return;
        }
        cl.buf.appendSlice(self.gpa, tmp[0..@intCast(n)]) catch return;
        while (std.mem.indexOfScalar(u8, cl.buf.items, '\n')) |nl| {
            const line = cl.buf.items[0..nl];
            if (std.json.parseFromSlice(Request, self.gpa, line, .{ .ignore_unknown_fields = true })) |parsed| {
                defer parsed.deinit();
                handler.onRequest(handler.ctx, cl, parsed.value);
            } else |err| {
                log.warn("bad request: {t}", .{err});
                _ = writeAll(cl.fd, "error\n");
            }
            std.mem.copyForwards(u8, cl.buf.items, cl.buf.items[nl + 1 ..]);
            cl.buf.shrinkRetainingCapacity(cl.buf.items.len - nl - 1);
        }
    }

    pub fn reply(_: *Server, cl: *Client, line: []const u8) void {
        _ = writeAll(cl.fd, line);
        _ = writeAll(cl.fd, "\n");
    }

    pub fn addWait(self: *Server, cl: *Client, path: []const u8) void {
        cl.waiting.append(self.gpa, self.gpa.dupe(u8, path) catch return) catch {};
    }

    /// A file's last tab closed: release clients waiting on it.
    pub fn fileClosed(self: *Server, path: []const u8) void {
        for (self.clients.items) |cl| {
            var i: usize = 0;
            while (i < cl.waiting.items.len) {
                if (std.mem.eql(u8, cl.waiting.items[i], path)) {
                    self.gpa.free(cl.waiting.swapRemove(i));
                } else i += 1;
            }
        }
        for (self.clients.items) |cl| {
            if (cl.waiting.items.len == 0 and cl.waiting.capacity > 0) {
                self.reply(cl, "done");
                cl.waiting.clearAndFree(self.gpa);
            }
        }
    }

    /// Wakes waiting clients (e.g. when the window closes, nothing is being edited).
    pub fn releaseAll(self: *Server) void {
        for (self.clients.items) |cl| if (cl.waiting.items.len > 0) {
            for (cl.waiting.items) |p| self.gpa.free(p);
            cl.waiting.clearAndFree(self.gpa);
            self.reply(cl, "done");
        };
    }
};
