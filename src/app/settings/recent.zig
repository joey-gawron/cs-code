//! The folders opened lately, newest first, for the Welcome page and
//! File ▸ Open Recent. Kept in `recent.json` next to state.json.

const std = @import("std");
const vfs = @import("../../model/remote/vfs.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const max_recent = 10;

pub const List = struct {
    items: std.ArrayList([]u8) = .empty,

    pub fn deinit(self: *List, gpa: Allocator) void {
        for (self.items.items) |p| gpa.free(p);
        self.items.deinit(gpa);
    }

    /// Moves `path` to the front (adding it when new), dropping the oldest
    /// beyond `max_recent`.
    pub fn push(self: *List, gpa: Allocator, path: []const u8) !void {
        for (self.items.items, 0..) |p, i| if (std.mem.eql(u8, p, path)) {
            const moved = self.items.orderedRemove(i);
            try self.items.insert(gpa, 0, moved);
            return;
        };
        const copy = try gpa.dupe(u8, path);
        errdefer gpa.free(copy);
        try self.items.insert(gpa, 0, copy);
        while (self.items.items.len > max_recent) gpa.free(self.items.pop().?);
    }

    pub fn remove(self: *List, gpa: Allocator, path: []const u8) void {
        for (self.items.items, 0..) |p, i| if (std.mem.eql(u8, p, path)) {
            gpa.free(self.items.orderedRemove(i));
            return;
        };
    }
};

fn stateFile(buf: []u8) ?[]const u8 {
    if (std.c.getenv("XDG_STATE_HOME")) |x| if (x[0] != 0) {
        return std.fmt.bufPrint(buf, "{s}/cs-zig/recent.json", .{std.mem.span(x)}) catch null;
    };
    const home = std.c.getenv("HOME") orelse return null;
    return std.fmt.bufPrint(buf, "{s}/.local/state/cs-zig/recent.json", .{std.mem.span(home)}) catch null;
}

/// The saved list; empty when there is none or it does not parse.
pub fn load(gpa: Allocator, io: Io) List {
    var list: List = .{};
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = stateFile(&buf) orelse return list;
    const raw = Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(256 << 10)) catch return list;
    defer gpa.free(raw);
    parse(gpa, raw, &list);
    return list;
}

fn parse(gpa: Allocator, raw: []const u8, list: *List) void {
    const parsed = std.json.parseFromSlice([]const []const u8, gpa, raw, .{}) catch return;
    defer parsed.deinit();
    for (parsed.value) |item| {
        if (list.items.items.len >= max_recent) break;
        const copy = gpa.dupe(u8, item) catch return;
        list.items.append(gpa, copy) catch {
            gpa.free(copy);
            return;
        };
    }
}

pub fn save(gpa: Allocator, io: Io, list: *const List) !void {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = stateFile(&buf) orelse return error.NoHome;
    if (std.fs.path.dirname(p)) |dir| try Io.Dir.cwd().createDirPath(io, dir);
    const text = try format(gpa, list);
    defer gpa.free(text);
    try vfs.writeFileAtomic(gpa, io, p, text);
}

fn format(gpa: Allocator, list: *const List) ![]u8 {
    var out: Io.Writer.Allocating = .init(gpa);
    errdefer out.deinit();
    try std.json.Stringify.value(@as([]const []u8, list.items.items), .{ .whitespace = .indent_2 }, &out.writer);
    try out.writer.writeByte('\n');
    return out.toOwnedSlice();
}

test "newest first, no duplicates, at most max_recent" {
    const gpa = std.testing.allocator;
    var l: List = .{};
    defer l.deinit(gpa);
    try l.push(gpa, "/a");
    try l.push(gpa, "/b");
    try l.push(gpa, "/a");
    try std.testing.expectEqual(@as(usize, 2), l.items.items.len);
    try std.testing.expectEqualStrings("/a", l.items.items[0]);
    var buf: [16]u8 = undefined;
    for (0..15) |i| try l.push(gpa, try std.fmt.bufPrint(&buf, "/p{d}", .{i}));
    try std.testing.expectEqual(@as(usize, max_recent), l.items.items.len);
    try std.testing.expectEqualStrings("/p14", l.items.items[0]);
    l.remove(gpa, "/p14");
    try std.testing.expectEqualStrings("/p13", l.items.items[0]);
}

test "the list round-trips through JSON" {
    const gpa = std.testing.allocator;
    var l: List = .{};
    defer l.deinit(gpa);
    try l.push(gpa, "/one");
    try l.push(gpa, "/two with space");
    const text = try format(gpa, &l);
    defer gpa.free(text);
    var back: List = .{};
    defer back.deinit(gpa);
    parse(gpa, text, &back);
    try std.testing.expectEqual(@as(usize, 2), back.items.items.len);
    try std.testing.expectEqualStrings("/two with space", back.items.items[0]);
    var broken: List = .{};
    defer broken.deinit(gpa);
    parse(gpa, "{not json", &broken);
    try std.testing.expectEqual(@as(usize, 0), broken.items.items.len);
}
