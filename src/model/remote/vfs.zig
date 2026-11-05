//! The file-system seam for remote editing. Local paths go straight to
//! std.Io; `ssh://host/...` paths go to the backend registered for `host`
//! (a model/remote/client.zig Connection). Buffers load and save through
//! here, the project scan and Find in Files ask it whether their roots are
//! remote, and the LSP client spawns remote language servers through it.
//!
//! The registry is process-wide: a path names its machine, so any thread
//! (project scans and searches run on workers) can resolve it. Backends stay
//! registered for the life of the process; a closed connection just fails
//! with error.NotConnected.

const std = @import("std");
const path_mod = @import("path.zig");
const SpinLock = @import("../../framework/util/executor.zig").SpinLock;
const project = @import("../project/project.zig");
const search = @import("../project/search.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const isRemote = path_mod.isRemote;

pub const Kind = enum { file, directory, sym_link, other };

pub const Stat = struct {
    kind: Kind,
    size: u64 = 0,
    mtime_ns: i128 = 0,
};

pub const DirEntry = struct { name: []const u8, kind: Kind };

/// A process on the remote machine, seen through local pipes: write its
/// stdin to `stdin_fd`, read its stdout from `stdout_fd` (EOF when it exits
/// or the connection drops). Language servers run on pipes, terminals on a
/// pty allocated on the remote machine.
pub const Process = struct {
    stdin_fd: i32,
    stdout_fd: i32,
    vtable: *const VTable,

    pub const VTable = struct {
        /// Stops the process; `stdout_fd` reaches EOF.
        kill: *const fn (p: *Process) void,
        /// Frees everything (after the stdout reader is done).
        destroy: *const fn (p: *Process) void,
        /// The terminal size (pty processes).
        resize: *const fn (p: *Process, rows: u16, cols: u16) void,
        /// The exit code once it has exited (negative: the killing signal).
        exitCode: *const fn (p: *Process) ?i64,
        /// Its current directory, as an ssh:// path in `buf`.
        currentDir: *const fn (p: *Process, buf: []u8) ?[]const u8,
    };

    pub fn kill(self: *Process) void {
        self.vtable.kill(self);
    }

    pub fn destroy(self: *Process) void {
        self.vtable.destroy(self);
    }

    pub fn resize(self: *Process, rows: u16, cols: u16) void {
        self.vtable.resize(self, rows, cols);
    }

    pub fn exitCode(self: *Process) ?i64 {
        return self.vtable.exitCode(self);
    }

    pub fn currentDir(self: *Process, buf: []u8) ?[]const u8 {
        return self.vtable.currentDir(self, buf);
    }
};

/// What a remote machine provides. Paths are the machine's own absolute
/// paths ("/home/me/x"), without the ssh:// prefix.
pub const Backend = struct {
    ctx: *anyopaque,
    vtable: *const VTable,

    pub const VTable = struct {
        readFile: *const fn (ctx: *anyopaque, gpa: Allocator, path: []const u8, limit: usize) anyerror![]u8,
        /// Atomic (temp file + rename), like a local save.
        writeFile: *const fn (ctx: *anyopaque, path: []const u8, data: []const u8) anyerror!void,
        stat: *const fn (ctx: *anyopaque, path: []const u8) anyerror!Stat,
        /// Directory listing, directories first; strings live in `alloc`.
        list: *const fn (ctx: *anyopaque, alloc: Allocator, path: []const u8) anyerror![]DirEntry,
        makeDir: *const fn (ctx: *anyopaque, path: []const u8) anyerror!void,
        rename: *const fn (ctx: *anyopaque, from: []const u8, to: []const u8) anyerror!void,
        delete: *const fn (ctx: *anyopaque, path: []const u8) anyerror!void,
        /// The project scan, run on the remote machine.
        scan: *const fn (ctx: *anyopaque, gpa: Allocator, root: []const u8) anyerror!project.Snapshot,
        /// Runs a Find in Files job's remote part (the job holds ssh:// paths).
        search: *const fn (ctx: *anyopaque, job: *search.Job) anyerror!search.Results,
        spawn: *const fn (ctx: *anyopaque, argv: []const []const u8, cwd: []const u8) anyerror!*Process,
        /// A terminal: `argv` (empty = the remote login shell) on a remote pty.
        spawnPty: *const fn (ctx: *anyopaque, argv: []const []const u8, cwd: []const u8, rows: u16, cols: u16) anyerror!*Process,
        /// Replaces the set of watched directories.
        watch: *const fn (ctx: *anyopaque, dirs: []const []const u8) void,
        /// Remote process id of the server (the LSP `processId`).
        serverPid: *const fn (ctx: *anyopaque) i64,
    };
};

// ---- Registry --------------------------------------------------------------------

const max_hosts = 16;

const Registered = struct {
    host: [256]u8,
    host_len: usize,
    backend: Backend,

    fn name(self: *const Registered) []const u8 {
        return self.host[0..self.host_len];
    }
};

var lock: SpinLock = .{};
var registry: [max_hosts]?Registered = @splat(null);

pub fn register(host: []const u8, backend: Backend) error{ TooManyHosts, NameTooLong }!void {
    if (host.len > 256) return error.NameTooLong;
    lock.lock();
    defer lock.unlock();
    var free: ?usize = null;
    for (&registry, 0..) |*slot, i| {
        if (slot.*) |*r| {
            if (std.mem.eql(u8, r.name(), host)) {
                r.backend = backend;
                return;
            }
        } else if (free == null) free = i;
    }
    const i = free orelse return error.TooManyHosts;
    registry[i] = .{ .host = undefined, .host_len = host.len, .backend = backend };
    @memcpy(registry[i].?.host[0..host.len], host);
}

pub fn unregister(host: []const u8) void {
    lock.lock();
    defer lock.unlock();
    for (&registry) |*slot| if (slot.*) |*r| if (std.mem.eql(u8, r.name(), host)) {
        slot.* = null;
    };
}

pub fn backendForHost(host: []const u8) ?Backend {
    lock.lock();
    defer lock.unlock();
    for (&registry) |*slot| if (slot.*) |*r| if (std.mem.eql(u8, r.name(), host)) return r.backend;
    return null;
}

pub const Resolved = struct { backend: Backend, path: []const u8 };

/// The backend and machine path for a remote path; null for local paths.
pub fn resolve(path: []const u8) error{NotConnected}!?Resolved {
    const r = path_mod.parse(path) orelse return null;
    const b = backendForHost(r.host) orelse return error.NotConnected;
    return .{ .backend = b, .path = r.path };
}

// ---- Operations ----------------------------------------------------------------

pub fn readFile(gpa: Allocator, io: Io, path: []const u8, limit: usize) ![]u8 {
    if (try resolve(path)) |r| return r.backend.vtable.readFile(r.backend.ctx, gpa, r.path, limit);
    return readLocalExact(gpa, io, path, limit);
}

/// Reads a local file into an allocation of exactly its size. (`readFileAlloc`
/// grows its buffer as it streams, which overshoots by up to half the size of
/// a big file.) Files that report no length (/proc, pipes) are streamed.
fn readLocalExact(gpa: Allocator, io: Io, path: []const u8, limit: usize) ![]u8 {
    const file = try Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);
    const size = file.length(io) catch 0;
    if (size == 0) return Io.Dir.cwd().readFileAlloc(io, path, gpa, .limited(limit));
    if (size > limit) return error.StreamTooLong;
    const buf = try gpa.alloc(u8, @intCast(size));
    errdefer gpa.free(buf);
    const n = try file.readPositionalAll(io, buf, 0);
    // The file shrank while we read it.
    if (n < buf.len) return gpa.realloc(buf, n);
    return buf;
}

/// Writes next to the target and renames, so a crash never truncates the file.
pub fn writeFileAtomic(gpa: Allocator, io: Io, path: []const u8, data: []const u8) !void {
    if (try resolve(path)) |r| return r.backend.vtable.writeFile(r.backend.ctx, r.path, data);
    const tmp = try std.fmt.allocPrint(gpa, "{s}.cs-zig-tmp", .{path});
    defer gpa.free(tmp);
    try Io.Dir.cwd().writeFile(io, .{ .sub_path = tmp, .data = data });
    try Io.Dir.rename(.cwd(), tmp, .cwd(), path, io);
}

pub fn stat(io: Io, path: []const u8) !Stat {
    if (try resolve(path)) |r| return r.backend.vtable.stat(r.backend.ctx, r.path);
    const st = try Io.Dir.cwd().statFile(io, path, .{});
    return .{ .kind = kindOf(st.kind), .size = st.size, .mtime_ns = st.mtime.nanoseconds };
}

pub fn makeDir(io: Io, path: []const u8) !void {
    if (try resolve(path)) |r| return r.backend.vtable.makeDir(r.backend.ctx, r.path);
    try Io.Dir.cwd().createDirPath(io, path);
}

/// Both paths must be on the same machine.
pub fn rename(io: Io, from: []const u8, to: []const u8) !void {
    if (!path_mod.sameMachine(from, to)) return error.CrossMachineRename;
    if (try resolve(from)) |r| return r.backend.vtable.rename(r.backend.ctx, r.path, path_mod.parse(to).?.path);
    try Io.Dir.rename(.cwd(), from, .cwd(), to, io);
}

pub fn delete(io: Io, path: []const u8) !void {
    if (try resolve(path)) |r| return r.backend.vtable.delete(r.backend.ctx, r.path);
    try Io.Dir.cwd().deleteFile(io, path);
}

pub fn kindOf(k: Io.File.Kind) Kind {
    return switch (k) {
        .file => .file,
        .directory => .directory,
        .sym_link => .sym_link,
        else => .other,
    };
}

/// Lists a directory (directories first, then by name); strings live in `alloc`.
pub fn list(alloc: Allocator, io: Io, path: []const u8) ![]DirEntry {
    if (try resolve(path)) |r| return r.backend.vtable.list(r.backend.ctx, alloc, r.path);
    var dir = try Io.Dir.cwd().openDir(io, path, .{ .iterate = true });
    defer dir.close(io);
    var out: std.ArrayList(DirEntry) = .empty;
    var it = dir.iterate();
    while (try it.next(io)) |e| try out.append(alloc, .{ .name = try alloc.dupe(u8, e.name), .kind = kindOf(e.kind) });
    sortEntries(out.items);
    return out.items;
}

pub fn sortEntries(entries: []DirEntry) void {
    std.mem.sort(DirEntry, entries, {}, struct {
        fn lt(_: void, a: DirEntry, b: DirEntry) bool {
            if ((a.kind == .directory) != (b.kind == .directory)) return a.kind == .directory;
            return std.ascii.lessThanIgnoreCase(a.name, b.name);
        }
    }.lt);
}

/// Remote project scan; null for a local root.
pub fn scan(gpa: Allocator, root: []const u8) !?project.Snapshot {
    const r = (try resolve(root)) orelse return null;
    return try r.backend.vtable.scan(r.backend.ctx, gpa, r.path);
}

/// Hands a Find in Files job to the machine its first remote path is on;
/// null when everything it searches is local.
pub fn searchRemote(job: *search.Job) !?search.Results {
    for ([_][]const []const u8{ job.roots, job.files }) |paths| for (paths) |p| {
        if (try resolve(p)) |r| return try r.backend.vtable.search(r.backend.ctx, job);
    };
    return null;
}

/// Starts `argv` on the machine `cwd` is on (which must be remote).
pub fn spawnRemote(argv: []const []const u8, cwd: []const u8) !*Process {
    const r = (try resolve(cwd)) orelse return error.NotRemote;
    return r.backend.vtable.spawn(r.backend.ctx, argv, r.path);
}

/// Starts a terminal program on the machine `cwd` is on (which must be remote).
pub fn spawnRemotePty(argv: []const []const u8, cwd: []const u8, rows: u16, cols: u16) !*Process {
    const r = (try resolve(cwd)) orelse return error.NotRemote;
    return r.backend.vtable.spawnPty(r.backend.ctx, argv, r.path, rows, cols);
}

pub fn serverPid(path: []const u8) ?i64 {
    const r = (resolve(path) catch return null) orelse return null;
    return r.backend.vtable.serverPid(r.backend.ctx);
}

/// Watches the remote ones among `dirs` (ssh:// paths), grouped per host;
/// local paths are left to the local FileWatcher.
pub fn syncRemoteWatches(gpa: Allocator, dirs: []const []const u8) void {
    var hosts: [max_hosts]Registered = undefined;
    var n: usize = 0;
    lock.lock();
    for (&registry) |*slot| if (slot.*) |r| {
        hosts[n] = r;
        n += 1;
    };
    lock.unlock();
    var mine: std.ArrayList([]const u8) = .empty;
    defer mine.deinit(gpa);
    for (hosts[0..n]) |*h| {
        mine.clearRetainingCapacity();
        for (dirs) |d| if (path_mod.parse(d)) |r| if (std.mem.eql(u8, r.host, h.name())) {
            mine.append(gpa, r.path) catch return;
        };
        h.backend.vtable.watch(h.backend.ctx, mine.items);
    }
}

test "local paths bypass the registry; unknown hosts are not connected" {
    const io = std.testing.io;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = try tmp.dir.realPath(io, &buf);
    const gpa = std.testing.allocator;
    const file = try std.fs.path.join(gpa, &.{ buf[0..n], "a.txt" });
    defer gpa.free(file);
    try writeFileAtomic(gpa, io, file, "hello");
    const text = try readFile(gpa, io, file, 1 << 20);
    defer gpa.free(text);
    try std.testing.expectEqualStrings("hello", text);
    try std.testing.expectEqual(Kind.file, (try stat(io, file)).kind);
    try std.testing.expectError(error.NotConnected, readFile(gpa, io, "ssh://nowhere/etc/hosts", 10));
    try std.testing.expect((try resolve(file)) == null);
}
