//! The system calls the remote code uses. cs-zig-server has no libc, so
//! on Linux these are the raw syscalls of std.os.linux; on macOS (the
//! editor only) they are libc calls with the same conventions: a usize
//! result that `errno` decodes, and Linux-only flags emulated.

const std = @import("std");
const builtin = @import("builtin");

pub const linux = builtin.os.tag == .linux;
const L = std.os.linux;
const C = std.c;

pub const E = if (linux) L.E else C.E;
pub const pid_t = if (linux) L.pid_t else C.pid_t;
pub const pollfd = if (linux) L.pollfd else C.pollfd;
pub const POLL = if (linux) L.POLL else C.POLL;
pub const F = if (linux) L.F else C.F;
pub const SIG = if (linux) L.SIG else C.SIG;
pub const AF = if (linux) L.AF else C.AF;
pub const SHUT = if (linux) L.SHUT else C.SHUT;
pub const socklen_t = if (linux) L.socklen_t else C.socklen_t;
pub const sockaddr = if (linux) L.sockaddr else C.sockaddr;

pub const SOCK = if (linux) L.SOCK else struct {
    pub const STREAM = C.SOCK.STREAM;
    /// Not a macOS flag: `socket` and `accept4` set FD_CLOEXEC for it.
    pub const CLOEXEC: u32 = 1 << 28;
};

pub const MSG = if (linux) L.MSG else struct {
    /// The editor ignores SIGPIPE, so EPIPE is what a closed peer gives.
    pub const NOSIGNAL: u32 = 0;
};

/// Decodes a result of the functions below.
pub fn errno(rc: usize) E {
    if (linux) return L.errno(rc);
    const signed: isize = @bitCast(rc);
    return if (signed > -4096 and signed < 0) @enumFromInt(-signed) else .SUCCESS;
}

/// libc's -1 + errno as Linux's -errno.
fn wrap(rc: anytype) usize {
    if (rc >= 0) return @intCast(rc);
    const e: isize = @intFromEnum(C.errno(@as(c_int, -1)));
    return @bitCast(-e);
}

pub fn read(fd: i32, buf: [*]u8, len: usize) usize {
    return if (linux) L.read(fd, buf, len) else wrap(C.read(fd, buf, len));
}

pub fn write(fd: i32, buf: [*]const u8, len: usize) usize {
    return if (linux) L.write(fd, buf, len) else wrap(C.write(fd, buf, len));
}

pub fn close(fd: i32) usize {
    return if (linux) L.close(fd) else wrap(C.close(fd));
}

pub fn poll(fds: [*]pollfd, n: usize, timeout: i32) usize {
    return if (linux) L.poll(fds, n, timeout) else wrap(C.poll(fds, @intCast(n), timeout));
}

pub fn kill(pid: pid_t, sig: anytype) usize {
    return if (linux) L.kill(pid, sig) else wrap(C.kill(pid, sig));
}

pub fn getpid() pid_t {
    return if (linux) L.getpid() else C.getpid();
}

pub fn mkdir(path: [*:0]const u8, mode: u32) usize {
    return if (linux) L.mkdir(path, mode) else wrap(C.mkdir(path, @intCast(mode)));
}

pub fn rmdir(path: [*:0]const u8) usize {
    return if (linux) L.rmdir(path) else wrap(C.rmdir(path));
}

pub fn unlink(path: [*:0]const u8) usize {
    return if (linux) L.unlink(path) else wrap(C.unlink(path));
}

pub fn fillRandom(buf: []u8) void {
    if (linux) {
        _ = L.getrandom(buf.ptr, buf.len, 0);
    } else C.arc4random_buf(buf.ptr, buf.len);
}

/// Sets O_NONBLOCK on `fd`.
pub fn setNonblocking(fd: i32) void {
    if (linux) {
        const flags = L.fcntl(fd, L.F.GETFL, 0);
        _ = L.fcntl(fd, L.F.SETFL, flags | @as(usize, 1 << @bitOffsetOf(L.O, "NONBLOCK")));
    } else {
        const flags = C.fcntl(fd, C.F.GETFL);
        _ = C.fcntl(fd, C.F.SETFL, flags | @as(c_int, @bitCast(C.O{ .NONBLOCK = true })));
    }
}

fn setCloexec(fd: c_int) void {
    _ = C.fcntl(fd, C.F.SETFD, @as(c_int, C.FD_CLOEXEC));
}

/// pipe2(fds, O_CLOEXEC).
pub fn pipeCloexec(fds: *[2]i32) usize {
    if (linux) return L.pipe2(fds, .{ .CLOEXEC = true });
    const rc = C.pipe(fds);
    if (rc == 0) for (fds) |fd| setCloexec(fd);
    return wrap(rc);
}

pub fn socket(domain: u32, sock_type: u32, protocol: u32) usize {
    if (linux) return L.socket(domain, sock_type, protocol);
    const rc = C.socket(domain, sock_type & ~SOCK.CLOEXEC, protocol);
    if (rc >= 0 and sock_type & SOCK.CLOEXEC != 0) setCloexec(rc);
    return wrap(rc);
}

pub fn bind(fd: i32, addr: *const sockaddr, len: socklen_t) usize {
    return if (linux) L.bind(fd, addr, len) else wrap(C.bind(fd, addr, len));
}

pub fn listen(fd: i32, backlog: u32) usize {
    return if (linux) L.listen(fd, backlog) else wrap(C.listen(fd, @intCast(backlog)));
}

pub fn connect(fd: i32, addr: *const anyopaque, len: socklen_t) usize {
    return if (linux) L.connect(fd, addr, len) else wrap(C.connect(fd, @ptrCast(@alignCast(addr)), len));
}

pub fn accept4(fd: i32, flags: u32) usize {
    if (linux) return L.accept4(fd, null, null, flags);
    const rc = C.accept(fd, null, null);
    if (rc >= 0 and flags & SOCK.CLOEXEC != 0) setCloexec(rc);
    return wrap(rc);
}

pub fn send(fd: i32, buf: [*]const u8, len: usize, flags: u32) usize {
    return if (linux) L.sendto(fd, buf, len, flags, null, 0) else wrap(C.sendto(fd, buf, len, flags, null, 0));
}

pub fn shutdown(fd: i32, how: anytype) usize {
    return if (linux) L.shutdown(fd, how) else wrap(C.shutdown(fd, how));
}

extern "c" fn getpeereid(fd: c_int, uid: *C.uid_t, gid: *C.gid_t) c_int;

/// The user on the other end of a Unix socket.
pub fn peerUid(fd: i32) ?u32 {
    if (linux) {
        const Cred = extern struct { pid: i32, uid: u32, gid: u32 };
        var cred: Cred = undefined;
        var len: L.socklen_t = @sizeOf(Cred);
        const rc = L.getsockopt(fd, L.SOL.SOCKET, L.SO.PEERCRED, @ptrCast(&cred), &len);
        return if (L.errno(rc) == .SUCCESS) cred.uid else null;
    }
    var uid: C.uid_t = undefined;
    var gid: C.gid_t = undefined;
    return if (getpeereid(fd, &uid, &gid) == 0) uid else null;
}

pub fn getuid() u32 {
    return if (linux) L.getuid() else C.getuid();
}

pub const sockaddr_un = if (linux) L.sockaddr.un else C.sockaddr.un;

pub fn unixAddress(path: []const u8) !sockaddr_un {
    var addr: sockaddr_un = if (linux) .{ .path = @splat(0) } else .{ .family = C.AF.UNIX, .path = @splat(0) };
    if (path.len >= addr.path.len) return error.NameTooLong;
    @memcpy(addr.path[0..path.len], path);
    return addr;
}

extern "c" var environ: [*:null]?[*:0]u8;

/// This process's environment as "NAME=value" entries (Linux reads
/// /proc/self/environ instead, which also works without libc).
pub fn environEntries() []const ?[*:0]u8 {
    if (linux) return &.{};
    return std.mem.sliceTo(environ, null);
}
