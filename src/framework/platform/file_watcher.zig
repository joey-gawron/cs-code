//! Watches the folders of open files with inotify (FSEvents on macOS), so
//! files changed by other programs can be reloaded. Directories are watched
//! rather than files because editors and tools often save by writing a new
//! file and renaming it over the old one, which a per-file watch would lose.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");

const Allocator = std.mem.Allocator;
const log = std.log.scoped(.watcher);

pub const Change = struct { path: []const u8, deleted: bool };

pub const FileWatcher = if (builtin.os.tag.isDarwin()) FsEventsWatcher else InotifyWatcher;

/// macOS: an FSEvents stream over the folders (cocoa.m), which reports
/// changes on its own thread and signals a pipe.
const FsEventsWatcher = struct {
    gpa: Allocator,
    handle: *c.JeWatcher,
    /// Readable when changes are waiting.
    fd: i32,

    pub const Change = @import("file_watcher.zig").Change;

    pub fn init(gpa: Allocator) !FsEventsWatcher {
        const handle = c.je_watcher_create() orelse return error.WatcherUnavailable;
        return .{ .gpa = gpa, .handle = handle, .fd = c.je_watcher_fd(handle) };
    }

    pub fn deinit(self: *FsEventsWatcher) void {
        c.je_watcher_destroy(self.handle);
    }

    /// Watches exactly `wanted` (directory paths).
    pub fn sync(self: *FsEventsWatcher, wanted: []const []const u8) void {
        const ptrs = self.gpa.alloc([*]const u8, wanted.len) catch return;
        defer self.gpa.free(ptrs);
        const lens = self.gpa.alloc(usize, wanted.len) catch return;
        defer self.gpa.free(lens);
        for (wanted, ptrs, lens) |w, *p, *l| {
            p.* = w.ptr;
            l.* = w.len;
        }
        c.je_watcher_set_dirs(self.handle, @ptrCast(ptrs.ptr), lens.ptr, wanted.len);
    }

    /// Drains pending events; appends changed paths (allocated in `alloc`).
    pub fn read(self: *FsEventsWatcher, alloc: Allocator, out: *std.ArrayList(FsEventsWatcher.Change)) void {
        var len: usize = 0;
        const raw = c.je_watcher_read(self.handle, &len) orelse return;
        defer c.free(raw);
        var rest = raw[0..len];
        while (rest.len > 1) {
            const end = std.mem.indexOfScalarPos(u8, rest, 1, 0) orelse break;
            const path = alloc.dupe(u8, rest[1..end]) catch return;
            out.append(alloc, .{ .path = path, .deleted = rest[0] == 'd' }) catch return;
            rest = rest[end + 1 ..];
        }
    }
};

const InotifyWatcher = struct {
    gpa: Allocator,
    fd: i32,
    /// Watch descriptor → directory path (owned).
    dirs: std.AutoHashMapUnmanaged(i32, []u8) = .empty,

    pub const Change = @import("file_watcher.zig").Change;

    pub fn init(gpa: Allocator) !InotifyWatcher {
        const fd = c.inotify_init1(c.IN_NONBLOCK | c.IN_CLOEXEC);
        if (fd < 0) return error.InotifyUnavailable;
        return .{ .gpa = gpa, .fd = fd };
    }

    pub fn deinit(self: *InotifyWatcher) void {
        var it = self.dirs.valueIterator();
        while (it.next()) |p| self.gpa.free(p.*);
        self.dirs.deinit(self.gpa);
        _ = c.close(self.fd);
    }

    fn wdOf(self: *const InotifyWatcher, dir: []const u8) ?i32 {
        var it = self.dirs.iterator();
        while (it.next()) |e| if (std.mem.eql(u8, e.value_ptr.*, dir)) return e.key_ptr.*;
        return null;
    }

    /// Watches exactly `wanted` (directory paths): adds new ones, drops the rest.
    pub fn sync(self: *InotifyWatcher, wanted: []const []const u8) void {
        // Drop directories no longer needed.
        var stale: std.ArrayList(i32) = .empty;
        defer stale.deinit(self.gpa);
        var it = self.dirs.iterator();
        while (it.next()) |e| {
            var keep = false;
            for (wanted) |w| if (std.mem.eql(u8, w, e.value_ptr.*)) {
                keep = true;
                break;
            };
            if (!keep) stale.append(self.gpa, e.key_ptr.*) catch {};
        }
        for (stale.items) |wd| {
            _ = c.inotify_rm_watch(self.fd, wd);
            if (self.dirs.fetchRemove(wd)) |kv| self.gpa.free(kv.value);
        }
        for (wanted) |dir| {
            if (self.wdOf(dir) != null) continue;
            const z = self.gpa.dupeSentinel(u8, dir, 0) catch continue;
            defer self.gpa.free(z);
            const mask: u32 = c.IN_CLOSE_WRITE | c.IN_MOVED_TO | c.IN_CREATE | c.IN_DELETE | c.IN_MOVED_FROM;
            const wd = c.inotify_add_watch(self.fd, z.ptr, mask);
            if (wd < 0) {
                log.debug("cannot watch {s}", .{dir});
                continue;
            }
            const owned = self.gpa.dupe(u8, dir) catch continue;
            self.dirs.put(self.gpa, wd, owned) catch self.gpa.free(owned);
        }
    }

    /// Drains pending events; appends changed paths (allocated in `alloc`).
    pub fn read(self: *InotifyWatcher, alloc: Allocator, out: *std.ArrayList(InotifyWatcher.Change)) void {
        var buf: [16 * 1024]u8 align(@alignOf(c.struct_inotify_event)) = undefined;
        while (true) {
            const n = c.read(self.fd, &buf, buf.len);
            if (n <= 0) return;
            var off: usize = 0;
            const len: usize = @intCast(n);
            while (off + @sizeOf(c.struct_inotify_event) <= len) {
                const ev: *const c.struct_inotify_event = @ptrCast(@alignCast(&buf[off]));
                const name_len: usize = ev.len;
                const name_start = off + @sizeOf(c.struct_inotify_event);
                off = name_start + name_len;
                if (name_len == 0) continue;
                const name = std.mem.sliceTo(buf[name_start..][0..name_len], 0);
                const dir = self.dirs.get(ev.wd) orelse continue;
                const path = std.fs.path.join(alloc, &.{ dir, name }) catch continue;
                const deleted = ev.mask & (c.IN_DELETE | c.IN_MOVED_FROM) != 0;
                out.append(alloc, .{ .path = path, .deleted = deleted }) catch return;
            }
        }
    }
};

test "reports writes in a watched folder" {
    const gpa = std.testing.allocator;
    var w = try FileWatcher.init(gpa);
    defer w.deinit();
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = try tmp.dir.realPath(std.testing.io, &path_buf);
    const dir = path_buf[0..n];
    w.sync(&.{dir});
    try tmp.dir.writeFile(std.testing.io, .{ .sub_path = "a.txt", .data = "hi" });
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    var changes: std.ArrayList(FileWatcher.Change) = .empty;
    // FSEvents reports from another thread, after its latency.
    var fds = [_]std.c.pollfd{.{ .fd = w.fd, .events = std.c.POLL.IN, .revents = 0 }};
    _ = std.c.poll(&fds, 1, 5000);
    w.read(arena.allocator(), &changes);
    try std.testing.expect(changes.items.len > 0);
    try std.testing.expectEqualStrings("a.txt", std.fs.path.basename(changes.items[0].path));
    // Unwatched folders report nothing.
    w.sync(&.{});
    try tmp.dir.writeFile(std.testing.io, .{ .sub_path = "b.txt", .data = "x" });
    changes.clearRetainingCapacity();
    w.read(arena.allocator(), &changes);
    try std.testing.expectEqual(@as(usize, 0), changes.items.len);
}
