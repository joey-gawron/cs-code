//! The user's line breakpoints: the source of truth that a debug session
//! mirrors to its adapter. Lines are 1-based, as in the protocol and the
//! gutter's numbers.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Breakpoint = struct {
    line: u32,
    enabled: bool = true,
    /// The adapter's verdict from the last session that set it.
    verified: bool = false,
    /// Why the adapter did not verify it.
    message: ?[]u8 = null,
};

pub const Store = struct {
    gpa: Allocator,
    /// Keys and messages are owned; each list is sorted by line.
    files: std.StringArrayHashMapUnmanaged(std.ArrayList(Breakpoint)) = .empty,
    /// Bumped on every change, so a session knows to send again.
    version: u32 = 0,

    pub fn init(gpa: Allocator) Store {
        return .{ .gpa = gpa };
    }

    pub fn deinit(self: *Store) void {
        for (self.files.keys(), self.files.values()) |k, *v| {
            for (v.items) |bp| if (bp.message) |m| self.gpa.free(m);
            v.deinit(self.gpa);
            self.gpa.free(k);
        }
        self.files.deinit(self.gpa);
    }

    fn index(items: []const Breakpoint, line: u32) union(enum) { found: usize, insert: usize } {
        var lo: usize = 0;
        var hi: usize = items.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (items[mid].line == line) return .{ .found = mid };
            if (items[mid].line < line) lo = mid + 1 else hi = mid;
        }
        return .{ .insert = lo };
    }

    pub fn get(self: *const Store, path: []const u8, line: u32) ?*const Breakpoint {
        const list = self.files.get(path) orelse return null;
        return switch (index(list.items, line)) {
            .found => |i| &list.items[i],
            .insert => null,
        };
    }

    pub fn forFile(self: *const Store, path: []const u8) []const Breakpoint {
        return if (self.files.get(path)) |l| l.items else &.{};
    }

    /// Adds a breakpoint on the line, or removes the one that is there.
    /// Returns whether one exists afterwards.
    pub fn toggle(self: *Store, path: []const u8, line: u32) !bool {
        if (self.files.getPtr(path)) |list| {
            switch (index(list.items, line)) {
                .found => |i| {
                    self.dropMessage(&list.items[i]);
                    _ = list.orderedRemove(i);
                    self.version +%= 1;
                    return false;
                },
                .insert => |i| {
                    try list.insert(self.gpa, i, .{ .line = line });
                    self.version +%= 1;
                    return true;
                },
            }
        }
        const key = try self.gpa.dupe(u8, path);
        errdefer self.gpa.free(key);
        var list: std.ArrayList(Breakpoint) = .empty;
        errdefer list.deinit(self.gpa);
        try list.append(self.gpa, .{ .line = line });
        try self.files.put(self.gpa, key, list);
        self.version +%= 1;
        return true;
    }

    fn dropMessage(self: *Store, bp: *Breakpoint) void {
        if (bp.message) |m| self.gpa.free(m);
        bp.message = null;
    }

    pub fn setEnabled(self: *Store, path: []const u8, line: u32, enabled: bool) void {
        const list = self.files.getPtr(path) orelse return;
        switch (index(list.items, line)) {
            .found => |i| if (list.items[i].enabled != enabled) {
                list.items[i].enabled = enabled;
                self.version +%= 1;
            },
            .insert => {},
        }
    }

    pub fn remove(self: *Store, path: []const u8, line: u32) void {
        if (self.get(path, line) != null) _ = self.toggle(path, line) catch {};
    }

    pub fn clear(self: *Store) void {
        for (self.files.values()) |*list| {
            for (list.items) |*bp| self.dropMessage(bp);
            list.clearRetainingCapacity();
        }
        self.version +%= 1;
    }

    /// Records the adapter's verdict. Does not bump `version` (it is not an edit).
    pub fn setVerdict(self: *Store, path: []const u8, line: u32, verified: bool, message: ?[]const u8) void {
        const list = self.files.getPtr(path) orelse return;
        switch (index(list.items, line)) {
            .found => |i| {
                const bp = &list.items[i];
                bp.verified = verified;
                self.dropMessage(bp);
                if (message) |m| bp.message = self.gpa.dupe(u8, m) catch null;
            },
            .insert => {},
        }
    }

    /// Forgets every verdict (a session ended).
    pub fn clearVerdicts(self: *Store) void {
        for (self.files.values()) |*list| for (list.items) |*bp| {
            bp.verified = false;
            self.dropMessage(bp);
        };
    }

    pub fn total(self: *const Store) usize {
        var n: usize = 0;
        for (self.files.values()) |l| n += l.items.len;
        return n;
    }
};

const testing = std.testing;

test "toggle adds, keeps lines sorted and removes" {
    var s = Store.init(testing.allocator);
    defer s.deinit();
    try testing.expect(try s.toggle("/a.c", 9));
    try testing.expect(try s.toggle("/a.c", 3));
    try testing.expect(try s.toggle("/a.c", 5));
    const lines = s.forFile("/a.c");
    try testing.expectEqual(@as(u32, 3), lines[0].line);
    try testing.expectEqual(@as(u32, 5), lines[1].line);
    try testing.expectEqual(@as(u32, 9), lines[2].line);
    try testing.expect(!(try s.toggle("/a.c", 5)));
    try testing.expectEqual(@as(usize, 2), s.total());
    try testing.expect(s.get("/a.c", 5) == null);
    try testing.expect(s.get("/a.c", 9) != null);
}

test "files are separate and version moves on edits only" {
    var s = Store.init(testing.allocator);
    defer s.deinit();
    _ = try s.toggle("/a.c", 1);
    _ = try s.toggle("/b.c", 1);
    try testing.expectEqual(@as(usize, 1), s.forFile("/b.c").len);
    const v = s.version;
    s.setVerdict("/a.c", 1, true, null);
    try testing.expectEqual(v, s.version);
    try testing.expect(s.get("/a.c", 1).?.verified);
    s.setEnabled("/a.c", 1, false);
    try testing.expect(s.version != v);
    try testing.expect(!s.get("/a.c", 1).?.enabled);
}

test "an unverified breakpoint keeps its message until cleared" {
    var s = Store.init(testing.allocator);
    defer s.deinit();
    _ = try s.toggle("/a.c", 4);
    s.setVerdict("/a.c", 4, false, "no code on this line");
    try testing.expectEqualStrings("no code on this line", s.get("/a.c", 4).?.message.?);
    s.clearVerdicts();
    try testing.expect(s.get("/a.c", 4).?.message == null);
}
