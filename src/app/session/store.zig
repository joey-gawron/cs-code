//! Saved sessions on disk: `<data>/sessions/<name>.json` (schema below), the
//! previous version beside it as `.bak`, and hot-exit backups of unsaved
//! buffers in `<name>.backups/`. Writes are atomic; a torn or unreadable file
//! falls back to `.bak`, and when both are broken the damaged file is kept as
//! `.corrupt` and the session starts empty. A file written by a newer schema
//! is never loaded or overwritten. Pure data and file handling: the workspace
//! side is snapshot.zig.

const std = @import("std");
const builtin = @import("builtin");
const vfs = @import("../../model/remote/vfs.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const schema_version: u32 = 1;

pub const SelDoc = struct { anchor: usize, head: usize };

pub const TabDoc = struct {
    /// Null for an untitled buffer.
    path: ?[]const u8 = null,
    group: u8 = 0,
    transient: bool = false,
    selections: []const SelDoc = &.{},
    primary: usize = 0,
    scroll_x: f64 = 0,
    scroll_y: f32 = 0,
    /// Folded regions as [header row, last row].
    folds: []const [2]u32 = &.{},
    /// File name in the session's backups folder holding the unsaved text.
    backup: ?[]const u8 = null,
};

/// One node of the split tree (groups.zig's Node, with its arrays cut to length).
pub const NodeDoc = struct {
    group: ?u8 = null,
    across: bool = false,
    children: []const u32 = &.{},
    shares: []const f32 = &.{},
};

pub const Doc = struct {
    version: u32 = schema_version,
    /// Seconds since the epoch.
    last_used: i64 = 0,
    /// Unnamed sessions are the ones `--new-window` makes.
    unnamed: bool = false,
    /// The project folder (local), if one is open.
    folder: ?[]const u8 = null,
    tabs: []const TabDoc = &.{},
    /// Per group: the index in `tabs` of its active tab, or -1.
    active: []const i32 = &.{},
    focused_group: u8 = 0,
    layout: []const NodeDoc = &.{},
    sidebar_visible: bool = true,
    sidebar_width: f32 = 240,
};

pub const Loaded = struct {
    parsed: std.json.Parsed(Doc),
    /// The main file was unreadable and `.bak` was used.
    from_backup: bool = false,

    pub fn deinit(self: *Loaded) void {
        self.parsed.deinit();
    }
};

pub const LoadResult = union(enum) {
    /// No saved session of that name (or both files were broken: the
    /// damaged one is kept as `.corrupt`).
    none,
    /// Written by a newer schema: left alone, nothing is saved over it.
    newer,
    loaded: Loaded,
};

pub const Info = struct {
    name: []const u8,
    last_used: i64,
    unnamed: bool,
    folder: ?[]const u8,
    tabs: usize,
};

pub fn validName(name: []const u8) bool {
    if (name.len == 0 or name.len > 64 or name[0] == '.') return false;
    for (name) |ch| switch (ch) {
        'a'...'z', 'A'...'Z', '0'...'9', '.', '_', '-' => {},
        else => return false,
    };
    return true;
}

pub fn nowSeconds() i64 {
    var ts: std.c.timespec = undefined;
    _ = std.c.clock_gettime(.REALTIME, &ts);
    return @intCast(ts.sec);
}

/// `$CS_ZIG_HOME` (portable installs and tests), else the platform's data folder.
pub fn dataDir(buf: []u8) ?[]const u8 {
    if (std.c.getenv("CS_ZIG_HOME")) |x| if (x[0] != 0) return std.fmt.bufPrint(buf, "{s}", .{std.mem.span(x)}) catch null;
    if (std.c.getenv("XDG_DATA_HOME")) |x| if (x[0] != 0) return std.fmt.bufPrint(buf, "{s}/cs-zig", .{std.mem.span(x)}) catch null;
    const home = std.c.getenv("HOME") orelse return null;
    if (builtin.os.tag.isDarwin()) return std.fmt.bufPrint(buf, "{s}/Library/Application Support/cs-zig", .{std.mem.span(home)}) catch null;
    return std.fmt.bufPrint(buf, "{s}/.local/share/cs-zig", .{std.mem.span(home)}) catch null;
}

pub const Store = struct {
    gpa: Allocator,
    io: Io,
    /// The folder holding `<name>.json`; owned.
    dir: []u8,

    pub fn open(gpa: Allocator, io: Io) ?Store {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const data = dataDir(&buf) orelse return null;
        return openAt(gpa, io, std.fmt.allocPrint(gpa, "{s}/sessions", .{data}) catch return null);
    }

    /// A store in `dir` (taken).
    pub fn openAt(gpa: Allocator, io: Io, dir: []u8) Store {
        return .{ .gpa = gpa, .io = io, .dir = dir };
    }

    pub fn deinit(self: *Store) void {
        self.gpa.free(self.dir);
    }

    fn path(self: *const Store, buf: []u8, name: []const u8, suffix: []const u8) ![]const u8 {
        return std.fmt.bufPrint(buf, "{s}/{s}{s}", .{ self.dir, name, suffix });
    }

    pub fn exists(self: *const Store, name: []const u8) bool {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const p = self.path(&buf, name, ".json") catch return false;
        Io.Dir.cwd().access(self.io, p, .{}) catch return false;
        return true;
    }

    /// Writes the session. The previous file stays as `.bak`.
    pub fn save(self: *const Store, name: []const u8, doc: Doc) !void {
        if (!validName(name)) return error.InvalidName;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        var bak_buf: [std.fs.max_path_bytes]u8 = undefined;
        const p = try self.path(&buf, name, ".json");
        const bak = try self.path(&bak_buf, name, ".json.bak");
        try Io.Dir.cwd().createDirPath(self.io, self.dir);
        const json = try std.json.Stringify.valueAlloc(self.gpa, doc, .{ .whitespace = .indent_2 });
        defer self.gpa.free(json);
        Io.Dir.rename(.cwd(), p, .cwd(), bak, self.io) catch {};
        try vfs.writeFileAtomic(self.gpa, self.io, p, json);
    }

    pub fn load(self: *const Store, name: []const u8) LoadResult {
        if (!validName(name)) return .none;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        var bak_buf: [std.fs.max_path_bytes]u8 = undefined;
        const p = self.path(&buf, name, ".json") catch return .none;
        const bak = self.path(&bak_buf, name, ".json.bak") catch return .none;
        const main_state = self.read(p);
        switch (main_state) {
            .loaded => |l| return .{ .loaded = l },
            .newer => return .newer,
            .missing, .broken => {},
        }
        const bak_state = self.read(bak);
        switch (bak_state) {
            .loaded => |l| {
                var m = l;
                m.from_backup = true;
                return .{ .loaded = m };
            },
            .newer => return .newer,
            .missing, .broken => {},
        }
        if (main_state == .broken) {
            var corrupt_buf: [std.fs.max_path_bytes]u8 = undefined;
            if (self.path(&corrupt_buf, name, ".json.corrupt")) |c| Io.Dir.rename(.cwd(), p, .cwd(), c, self.io) catch {} else |_| {}
        }
        return .none;
    }

    const Read = union(enum) { missing, broken, newer, loaded: Loaded };

    fn read(self: *const Store, p: []const u8) Read {
        const raw = Io.Dir.cwd().readFileAlloc(self.io, p, self.gpa, .limited(64 << 20)) catch |err| return if (err == error.FileNotFound) .missing else .broken;
        defer self.gpa.free(raw);
        const parsed = std.json.parseFromSlice(Doc, self.gpa, raw, .{ .ignore_unknown_fields = true, .allocate = .alloc_always }) catch {
            // A newer schema may have changed a field's type: say so rather than "corrupt".
            if (newerVersion(self.gpa, raw)) return .newer;
            return .broken;
        };
        if (parsed.value.version > schema_version) {
            parsed.deinit();
            return .newer;
        }
        if (!sane(parsed.value)) {
            parsed.deinit();
            return .broken;
        }
        return .{ .loaded = .{ .parsed = parsed } };
    }

    fn newerVersion(gpa: Allocator, raw: []const u8) bool {
        const V = struct { version: u32 = 0 };
        const p = std.json.parseFromSlice(V, gpa, raw, .{ .ignore_unknown_fields = true }) catch return false;
        defer p.deinit();
        return p.value.version > schema_version;
    }

    fn sane(doc: Doc) bool {
        if (!std.math.isFinite(doc.sidebar_width)) return false;
        for (doc.layout) |n| for (n.shares) |s| if (!std.math.isFinite(s)) return false;
        for (doc.tabs) |t| if (!std.math.isFinite(t.scroll_x) or !std.math.isFinite(t.scroll_y)) return false;
        return true;
    }

    // ---- Hot-exit backups -------------------------------------------------

    fn backupsDir(self: *const Store, buf: []u8, name: []const u8) ![]const u8 {
        return self.path(buf, name, ".backups");
    }

    pub fn writeBackup(self: *const Store, name: []const u8, file: []const u8, text: []const u8) !void {
        var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const dir = try self.backupsDir(&dir_buf, name);
        try Io.Dir.cwd().createDirPath(self.io, dir);
        const p = try std.fmt.bufPrint(&buf, "{s}/{s}", .{ dir, file });
        try vfs.writeFileAtomic(self.gpa, self.io, p, text);
    }

    /// The backup's text, the caller's to free.
    pub fn readBackup(self: *const Store, name: []const u8, file: []const u8) ?[]u8 {
        if (std.mem.indexOfAny(u8, file, "/\\") != null) return null;
        var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const dir = self.backupsDir(&dir_buf, name) catch return null;
        const p = std.fmt.bufPrint(&buf, "{s}/{s}", .{ dir, file }) catch return null;
        return Io.Dir.cwd().readFileAlloc(self.io, p, self.gpa, .limited(1 << 30)) catch null;
    }

    /// Deletes the backups not in `keep`.
    pub fn pruneBackups(self: *const Store, name: []const u8, keep: []const []const u8) void {
        var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
        const dir_path = self.backupsDir(&dir_buf, name) catch return;
        var dir = Io.Dir.cwd().openDir(self.io, dir_path, .{ .iterate = true }) catch return;
        defer dir.close(self.io);
        var stale: std.ArrayList([]u8) = .empty;
        defer {
            for (stale.items) |s| self.gpa.free(s);
            stale.deinit(self.gpa);
        }
        var it = dir.iterate();
        while (it.next(self.io) catch null) |entry| {
            const wanted = for (keep) |k| {
                if (std.mem.eql(u8, k, entry.name)) break true;
            } else false;
            if (!wanted) stale.append(self.gpa, self.gpa.dupe(u8, entry.name) catch continue) catch {};
        }
        for (stale.items) |s| dir.deleteFile(self.io, s) catch {};
    }

    // ---- Managing sessions ---------------------------------------------------

    /// Every saved session, most recently used first. Strings live in `arena`.
    pub fn list(self: *const Store, arena: Allocator) ![]Info {
        var out: std.ArrayList(Info) = .empty;
        var dir = Io.Dir.cwd().openDir(self.io, self.dir, .{ .iterate = true }) catch return out.items;
        defer dir.close(self.io);
        var it = dir.iterate();
        while (it.next(self.io) catch null) |entry| {
            if (entry.kind != .file or !std.mem.endsWith(u8, entry.name, ".json")) continue;
            const name = entry.name[0 .. entry.name.len - ".json".len];
            if (!validName(name)) continue;
            var info: Info = .{ .name = try arena.dupe(u8, name), .last_used = 0, .unnamed = false, .folder = null, .tabs = 0 };
            switch (self.load(name)) {
                .loaded => |l| {
                    var loaded = l;
                    defer loaded.deinit();
                    const d = loaded.parsed.value;
                    info.last_used = d.last_used;
                    info.unnamed = d.unnamed;
                    info.tabs = d.tabs.len;
                    if (d.folder) |f| info.folder = try arena.dupe(u8, f);
                },
                .newer, .none => {},
            }
            try out.append(arena, info);
        }
        std.mem.sort(Info, out.items, {}, newerFirst);
        return out.items;
    }

    fn newerFirst(_: void, a: Info, b: Info) bool {
        if (a.last_used != b.last_used) return a.last_used > b.last_used;
        return std.mem.lessThan(u8, a.name, b.name);
    }

    pub fn remove(self: *const Store, name: []const u8) !void {
        if (!validName(name)) return error.InvalidName;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        inline for (.{ ".json", ".json.bak", ".json.corrupt" }) |suffix| {
            const p = try self.path(&buf, name, suffix);
            Io.Dir.cwd().deleteFile(self.io, p) catch {};
        }
        const dir = try self.backupsDir(&buf, name);
        Io.Dir.cwd().deleteTree(self.io, dir) catch {};
    }

    /// Renames a saved session (its backups come along). The new name must be free.
    pub fn rename(self: *const Store, from: []const u8, to: []const u8) !void {
        if (!validName(from) or !validName(to)) return error.InvalidName;
        if (self.exists(to)) return error.NameTaken;
        if (!self.exists(from)) return error.NoSuchSession;
        var a: [std.fs.max_path_bytes]u8 = undefined;
        var b: [std.fs.max_path_bytes]u8 = undefined;
        inline for (.{ ".json", ".json.bak", ".backups" }) |suffix| {
            const src = try self.path(&a, from, suffix);
            const dst = try self.path(&b, to, suffix);
            Io.Dir.rename(.cwd(), src, .cwd(), dst, self.io) catch |err| if (err != error.FileNotFound) return err;
        }
    }

    /// `window-N` for the first N with no saved session of that name.
    pub fn freshWindowName(self: *const Store, buf: []u8, taken: *const fn (ctx: ?*anyopaque, name: []const u8) bool, ctx: ?*anyopaque) []const u8 {
        var n: u32 = 1;
        while (true) : (n += 1) {
            const name = std.fmt.bufPrint(buf, "window-{d}", .{n}) catch return "window-0";
            if (!self.exists(name) and !taken(ctx, name)) return name;
        }
    }
};

// ---- Tests ----------------------------------------------------------------

const testing = std.testing;

fn testStore(tmp: *testing.TmpDir) !Store {
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = try tmp.dir.realPath(testing.io, &path_buf);
    return Store.openAt(testing.allocator, testing.io, try std.fmt.allocPrint(testing.allocator, "{s}/sessions", .{path_buf[0..n]}));
}

const sample: Doc = .{
    .last_used = 1000,
    .folder = "/work/proj",
    .tabs = &.{
        .{ .path = "/work/proj/a.zig", .group = 0, .selections = &.{.{ .anchor = 3, .head = 7 }}, .scroll_y = 120.5, .folds = &.{.{ 4, 9 }} },
        .{ .path = null, .group = 1, .backup = "1.txt" },
    },
    .active = &.{ 0, 1 },
    .focused_group = 1,
    .layout = &.{
        .{ .across = true, .children = &.{ 1, 2 }, .shares = &.{ 0.4, 0.6 } },
        .{ .group = 0 },
        .{ .group = 1 },
    },
    .sidebar_visible = false,
    .sidebar_width = 311,
};

test "names" {
    try testing.expect(validName("default"));
    try testing.expect(validName("work-1.x_y"));
    try testing.expect(!validName(""));
    try testing.expect(!validName(".hidden"));
    try testing.expect(!validName("a/b"));
    try testing.expect(!validName("a b"));
    const long: [65]u8 = @splat('x');
    try testing.expect(!validName(&long));
}

test "a session round-trips" {
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var store = try testStore(&tmp);
    defer store.deinit();
    try testing.expect(store.load("work") == .none);
    try store.save("work", sample);
    var l = store.load("work").loaded;
    defer l.deinit();
    const d = l.parsed.value;
    try testing.expect(!l.from_backup);
    try testing.expectEqualStrings("/work/proj", d.folder.?);
    try testing.expectEqual(@as(usize, 2), d.tabs.len);
    try testing.expectEqual(@as(usize, 7), d.tabs[0].selections[0].head);
    try testing.expectEqual(@as(f32, 120.5), d.tabs[0].scroll_y);
    try testing.expectEqual([2]u32{ 4, 9 }, d.tabs[0].folds[0]);
    try testing.expect(d.tabs[1].path == null);
    try testing.expectEqualStrings("1.txt", d.tabs[1].backup.?);
    try testing.expectEqual(@as(u8, 1), d.focused_group);
    try testing.expectEqual(@as(f32, 0.6), d.layout[0].shares[1]);
    try testing.expect(!d.sidebar_visible);
}

test "a torn file falls back to the backup, and both broken is kept as corrupt" {
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var store = try testStore(&tmp);
    defer store.deinit();
    var older = sample;
    older.last_used = 1;
    try store.save("s", older);
    try store.save("s", sample); // the first is now s.json.bak
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = try std.fmt.bufPrint(&path_buf, "{s}/s.json", .{store.dir});
    try Io.Dir.cwd().writeFile(testing.io, .{ .sub_path = p, .data = "{\"version\": 1, \"tab" });
    {
        var l = store.load("s").loaded;
        defer l.deinit();
        try testing.expect(l.from_backup);
        try testing.expectEqual(@as(i64, 1), l.parsed.value.last_used);
    }
    var bak_buf: [std.fs.max_path_bytes]u8 = undefined;
    const bak = try std.fmt.bufPrint(&bak_buf, "{s}/s.json.bak", .{store.dir});
    try Io.Dir.cwd().writeFile(testing.io, .{ .sub_path = bak, .data = "not json" });
    try testing.expect(store.load("s") == .none);
    var c_buf: [std.fs.max_path_bytes]u8 = undefined;
    try Io.Dir.cwd().access(testing.io, try std.fmt.bufPrint(&c_buf, "{s}/s.json.corrupt", .{store.dir}), .{});
}

test "a newer schema is neither loaded nor replaced" {
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var store = try testStore(&tmp);
    defer store.deinit();
    try Io.Dir.cwd().createDirPath(testing.io, store.dir);
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = try std.fmt.bufPrint(&path_buf, "{s}/n.json", .{store.dir});
    try Io.Dir.cwd().writeFile(testing.io, .{ .sub_path = p, .data = "{\"version\": 99, \"tabs\": \"a new shape\"}" });
    try testing.expect(store.load("n") == .newer);
}

test "backups are written, read and pruned" {
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var store = try testStore(&tmp);
    defer store.deinit();
    try store.writeBackup("s", "1.txt", "unsaved one");
    try store.writeBackup("s", "2.txt", "unsaved two");
    const one = store.readBackup("s", "1.txt").?;
    defer testing.allocator.free(one);
    try testing.expectEqualStrings("unsaved one", one);
    try testing.expect(store.readBackup("s", "../x") == null);
    store.pruneBackups("s", &.{"2.txt"});
    try testing.expect(store.readBackup("s", "1.txt") == null);
    const two = store.readBackup("s", "2.txt").?;
    defer testing.allocator.free(two);
}

test "list, rename and remove" {
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var store = try testStore(&tmp);
    defer store.deinit();
    var old = sample;
    old.last_used = 5;
    try store.save("old", old);
    try store.save("new", sample);
    try store.writeBackup("new", "1.txt", "x");
    var arena: std.heap.ArenaAllocator = .init(testing.allocator);
    defer arena.deinit();
    const infos = try store.list(arena.allocator());
    try testing.expectEqual(@as(usize, 2), infos.len);
    try testing.expectEqualStrings("new", infos[0].name);
    try testing.expectEqual(@as(usize, 2), infos[0].tabs);
    try testing.expectEqualStrings("/work/proj", infos[0].folder.?);

    try testing.expectError(error.NameTaken, store.rename("new", "old"));
    try testing.expectError(error.InvalidName, store.rename("new", "a/b"));
    try store.rename("new", "renamed");
    try testing.expect(!store.exists("new"));
    const b = store.readBackup("renamed", "1.txt").?;
    testing.allocator.free(b);

    try store.remove("renamed");
    try testing.expect(!store.exists("renamed"));
    try testing.expect(store.readBackup("renamed", "1.txt") == null);
}
