//! A project folder (Zed's "worktree"): scanned on a background thread into
//! a sorted tree for the sidebar and a flat file list for Goto Anything.
//! Respects the root .gitignore (simple patterns) plus Sublime's default
//! folder excludes.

const std = @import("std");
const Executor = @import("../../framework/util/executor.zig").Executor;
const vfs = @import("../remote/vfs.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const log = std.log.scoped(.project);

pub const Kind = enum { file, dir };

pub const Entry = struct {
    /// Path relative to the project root ("src/main.zig").
    path: []const u8,
    name: []const u8,
    kind: Kind,
    depth: u16,
    /// Directory excluded by .gitignore: shown dimmed, not descended into.
    ignored: bool = false,
};

const default_excludes = [_][]const u8{ ".git", ".svn", ".hg", "CVS", ".Trash", ".zig-cache", "zig-cache", "node_modules", "__pycache__", ".DS_Store" };
const max_entries = 200_000;

pub const Snapshot = struct {
    arena: std.heap.ArenaAllocator,
    /// Depth-first, directories before files, case-insensitive order.
    entries: []Entry,
    /// Indices of file entries (for fuzzy file search).
    files: []u32,
    truncated: bool,

    pub fn deinit(self: *Snapshot) void {
        self.arena.deinit();
    }
};

pub const Project = struct {
    gpa: Allocator,
    io: Io,
    root: []u8,
    name: []const u8,
    snapshot: ?Snapshot = null,
    scanning: bool = false,
    /// The background scan's result, for `finishScan` (null if it failed).
    scanned: ?Snapshot = null,
    /// Relative paths of expanded directories (survives rescans).
    expanded: std.StringHashMapUnmanaged(void) = .empty,
    executor: *Executor,
    /// Bumped whenever the snapshot or expansion state changes.
    generation: u32 = 0,

    pub fn create(gpa: Allocator, io: Io, executor: *Executor, root: []const u8) !*Project {
        const self = try gpa.create(Project);
        const owned = try gpa.dupe(u8, std.mem.trimEnd(u8, root, "/"));
        self.* = .{ .gpa = gpa, .io = io, .root = owned, .name = std.fs.path.basename(owned), .executor = executor };
        return self;
    }

    pub fn destroy(self: *Project) void {
        // A scan in flight owns a reference to `self`; wait for it.
        while (self.scanning) {
            self.executor.drain();
            std.Thread.yield() catch {};
        }
        if (self.snapshot) |*s| s.deinit();
        var it = self.expanded.keyIterator();
        while (it.next()) |k| self.gpa.free(k.*);
        self.expanded.deinit(self.gpa);
        self.gpa.free(self.root);
        self.gpa.destroy(self);
    }

    pub fn isExpanded(self: *const Project, rel: []const u8) bool {
        return self.expanded.contains(rel);
    }

    pub fn toggleExpanded(self: *Project, rel: []const u8) void {
        if (self.expanded.fetchRemove(rel)) |kv| {
            self.gpa.free(kv.key);
        } else {
            const key = self.gpa.dupe(u8, rel) catch return;
            self.expanded.put(self.gpa, key, {}) catch self.gpa.free(key);
        }
        self.generation +%= 1;
    }

    pub fn setExpanded(self: *Project, rel: []const u8, expanded: bool) void {
        if (self.isExpanded(rel) != expanded) self.toggleExpanded(rel);
    }

    pub fn absolutePath(self: *const Project, gpa: Allocator, rel: []const u8) ![]u8 {
        return std.fs.path.join(gpa, &.{ self.root, rel });
    }

    /// Starts a background rescan; the result replaces the snapshot on the main thread.
    pub fn scan(self: *Project) void {
        if (self.scanning) return;
        self.scanning = true;
        self.executor.spawn(scanThread, .{self}) catch |err| {
            log.err("failed to start scan: {t}", .{err});
            self.scanning = false;
        };
    }

    /// Synchronous scan (headless mode / tests).
    pub fn scanBlocking(self: *Project) void {
        const snap = buildSnapshot(self.gpa, self.io, self.root) catch |err| {
            log.err("scan failed: {t}", .{err});
            return;
        };
        self.install(snap);
    }

    fn install(self: *Project, snap: Snapshot) void {
        if (self.snapshot) |*old| old.deinit();
        self.snapshot = snap;
        self.generation +%= 1;
    }

    fn scanThread(self: *Project) void {
        // Once posted, `destroy` may free the project: the executor is the
        // last thing touched. Nothing here can fail to post, or `scanning`
        // would stay set and `destroy` wait forever.
        const executor = self.executor;
        defer executor.taskDone();
        self.scanned = buildSnapshot(self.gpa, self.io, self.root) catch |err| blk: {
            log.err("scan failed: {t}", .{err});
            break :blk null;
        };
        executor.post(.{ .ctx = self, .run = finishScan });
    }

    fn finishScan(ctx: *anyopaque) void {
        const self: *Project = @ptrCast(@alignCast(ctx));
        if (self.scanned) |snap| self.install(snap);
        self.scanned = null;
        self.scanning = false;
    }

    /// Indices of entries visible in the sidebar (all ancestors expanded).
    pub fn visibleEntries(self: *const Project, gpa: Allocator) ![]u32 {
        const snap = self.snapshot orelse return &.{};
        var out: std.ArrayList(u32) = .empty;
        var hide_below: ?u16 = null;
        for (snap.entries, 0..) |e, i| {
            if (hide_below) |d| {
                if (e.depth > d) continue;
                hide_below = null;
            }
            try out.append(gpa, @intCast(i));
            if (e.kind == .dir and !self.isExpanded(e.path)) hide_below = e.depth;
        }
        return out.toOwnedSlice(gpa);
    }

    /// Index of the entry with this relative path.
    pub fn findEntry(self: *const Project, rel: []const u8) ?u32 {
        const snap = self.snapshot orelse return null;
        for (snap.entries, 0..) |e, i| if (std.mem.eql(u8, e.path, rel)) return @intCast(i);
        return null;
    }

    /// Expands every ancestor directory of `rel`.
    pub fn reveal(self: *Project, rel: []const u8) void {
        var i: usize = 0;
        while (std.mem.indexOfScalarPos(u8, rel, i, '/')) |slash| : (i = slash + 1) {
            self.setExpanded(rel[0..slash], true);
        }
    }

    /// Relative path of `abs` inside this project, if it is inside.
    pub fn relativePath(self: *const Project, abs: []const u8) ?[]const u8 {
        if (!std.mem.startsWith(u8, abs, self.root)) return null;
        if (abs.len == self.root.len) return "";
        if (abs[self.root.len] != '/') return null;
        return abs[self.root.len + 1 ..];
    }
};

const Ignore = struct {
    /// Exact names / relative paths (from patterns without wildcards).
    names: std.ArrayList([]const u8) = .empty,
    /// "*.ext" suffix patterns.
    suffixes: std.ArrayList([]const u8) = .empty,

    fn load(alloc: Allocator, io: Io, root: []const u8) Ignore {
        var self: Ignore = .{};
        const path = std.fs.path.join(alloc, &.{ root, ".gitignore" }) catch return self;
        const text = Io.Dir.cwd().readFileAlloc(io, path, alloc, .limited(1 << 20)) catch return self;
        var lines = std.mem.splitScalar(u8, text, '\n');
        while (lines.next()) |raw| {
            var line = std.mem.trim(u8, raw, " \t\r");
            if (line.len == 0 or line[0] == '#' or line[0] == '!') continue;
            line = std.mem.trim(u8, line, "/");
            if (line.len == 0) continue;
            if (std.mem.startsWith(u8, line, "*.") and std.mem.indexOfAny(u8, line[2..], "*?[/") == null) {
                self.suffixes.append(alloc, line[1..]) catch {};
            } else if (std.mem.indexOfAny(u8, line, "*?[") == null) {
                self.names.append(alloc, line) catch {};
            }
        }
        return self;
    }

    fn matches(self: *const Ignore, rel: []const u8, name: []const u8) bool {
        for (self.names.items) |n| {
            if (std.mem.eql(u8, n, name) or std.mem.eql(u8, n, rel)) return true;
        }
        for (self.suffixes.items) |s| if (std.mem.endsWith(u8, name, s)) return true;
        return false;
    }
};

fn lessEntry(_: void, a: Io.Dir.Entry, b: Io.Dir.Entry) bool {
    const a_dir = a.kind == .directory;
    const b_dir = b.kind == .directory;
    if (a_dir != b_dir) return a_dir;
    return std.ascii.lessThanIgnoreCase(a.name, b.name);
}

/// Walks `root` (honouring .gitignore and the default excludes). A remote
/// root (ssh://host/...) is walked by jiffy-edit-server on that host.
pub fn buildSnapshot(gpa: Allocator, io: Io, root: []const u8) !Snapshot {
    if (try vfs.scan(gpa, root)) |remote| return remote;
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const alloc = arena.allocator();
    const ignore = Ignore.load(alloc, io, root);
    var entries: std.ArrayList(Entry) = .empty;
    var files: std.ArrayList(u32) = .empty;
    var truncated = false;
    var dir = try Io.Dir.cwd().openDir(io, root, .{ .iterate = true });
    defer dir.close(io);
    try walk(alloc, io, dir, "", 0, &ignore, &entries, &files, &truncated);
    return .{ .arena = arena, .entries = entries.items, .files = files.items, .truncated = truncated };
}

fn walk(alloc: Allocator, io: Io, dir: Io.Dir, prefix: []const u8, depth: u16, ignore: *const Ignore, entries: *std.ArrayList(Entry), files: *std.ArrayList(u32), truncated: *bool) !void {
    var children: std.ArrayList(Io.Dir.Entry) = .empty;
    var it = dir.iterate();
    while (it.next(io) catch null) |entry| {
        try children.append(alloc, .{ .name = try alloc.dupe(u8, entry.name), .kind = entry.kind, .inode = entry.inode });
    }
    std.mem.sort(Io.Dir.Entry, children.items, {}, lessEntry);

    for (children.items) |child| {
        if (entries.items.len >= max_entries) {
            truncated.* = true;
            return;
        }
        var excluded = false;
        for (default_excludes) |ex| if (std.mem.eql(u8, child.name, ex)) {
            excluded = true;
        };
        if (excluded) continue;
        const rel = if (prefix.len == 0) child.name else try std.fmt.allocPrint(alloc, "{s}/{s}", .{ prefix, child.name });
        const name = rel[rel.len - child.name.len ..];
        const ignored = ignore.matches(rel, child.name);
        switch (child.kind) {
            .directory => {
                try entries.append(alloc, .{ .path = rel, .name = name, .kind = .dir, .depth = depth, .ignored = ignored });
                if (ignored) continue;
                var sub = dir.openDir(io, child.name, .{ .iterate = true }) catch continue;
                defer sub.close(io);
                try walk(alloc, io, sub, rel, depth + 1, ignore, entries, files, truncated);
            },
            .file, .sym_link => {
                if (ignored) continue;
                try files.append(alloc, @intCast(entries.items.len));
                try entries.append(alloc, .{ .path = rel, .name = name, .kind = .file, .depth = depth });
            },
            else => {},
        }
    }
}
