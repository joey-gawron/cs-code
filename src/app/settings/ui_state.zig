//! Window state kept between runs that isn't a setting: whether the side bar
//! is shown and how wide it is. It lives in
//! `$XDG_STATE_HOME/cs-zig/state.json` (`~/.local/state/cs-zig/`),
//! is written (atomically) only when it changes, and a missing or broken
//! file means the defaults.

const std = @import("std");
const vfs = @import("../../model/remote/vfs.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const State = struct {
    sidebar_visible: bool,
    sidebar_width: f32,
};

pub fn path(buf: []u8) ?[]const u8 {
    if (std.c.getenv("XDG_STATE_HOME")) |x| if (x[0] != 0) {
        return std.fmt.bufPrint(buf, "{s}/cs-zig/state.json", .{std.mem.span(x)}) catch null;
    };
    const home = std.c.getenv("HOME") orelse return null;
    return std.fmt.bufPrint(buf, "{s}/.local/state/cs-zig/state.json", .{std.mem.span(home)}) catch null;
}

/// The state saved last, or null when there is none (or it doesn't parse).
pub fn load(gpa: Allocator, io: Io) ?State {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = path(&buf) orelse return null;
    const raw = Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(64 << 10)) catch return null;
    defer gpa.free(raw);
    return parse(gpa, raw);
}

fn parse(gpa: Allocator, raw: []const u8) ?State {
    const parsed = std.json.parseFromSlice(State, gpa, raw, .{ .ignore_unknown_fields = true }) catch return null;
    defer parsed.deinit();
    if (!std.math.isFinite(parsed.value.sidebar_width)) return null;
    return parsed.value;
}

pub fn save(gpa: Allocator, io: Io, state: State) !void {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = path(&buf) orelse return error.NoHome;
    if (std.fs.path.dirname(p)) |dir| try Io.Dir.cwd().createDirPath(io, dir);
    var data: [256]u8 = undefined;
    try vfs.writeFileAtomic(gpa, io, p, try format(&data, state));
}

fn format(buf: []u8, state: State) ![]const u8 {
    var w: Io.Writer = .fixed(buf);
    try std.json.Stringify.value(state, .{ .whitespace = .indent_2 }, &w);
    try w.writeByte('\n');
    return w.buffered();
}

test "state round trip" {
    const gpa = std.testing.allocator;
    var buf: [256]u8 = undefined;
    const text = try format(&buf, .{ .sidebar_visible = false, .sidebar_width = 312 });
    try std.testing.expectEqualStrings(
        \\{
        \\  "sidebar_visible": false,
        \\  "sidebar_width": 312
        \\}
        \\
    , text);
    try std.testing.expectEqual(State{ .sidebar_visible = false, .sidebar_width = 312 }, parse(gpa, text).?);
}

test "a broken state file means the defaults" {
    const gpa = std.testing.allocator;
    try std.testing.expectEqual(@as(?State, null), parse(gpa, "{\"sidebar_visible\": fals"));
    try std.testing.expectEqual(@as(?State, null), parse(gpa, "{\"sidebar_visible\": false}"));
    try std.testing.expectEqual(@as(?State, null), parse(gpa, "{\"sidebar_visible\": 1, \"sidebar_width\": 200}"));
    try std.testing.expectEqual(@as(?State, null), parse(gpa, "[]"));
    // Keys from later versions are skipped.
    try std.testing.expectEqual(State{ .sidebar_visible = true, .sidebar_width = 200 }, parse(gpa, "{\"sidebar_visible\": true, \"sidebar_width\": 200, \"panel\": 3}").?);
}
