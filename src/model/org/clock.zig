//! Org clocking: `CLOCK: [2026-10-22 Thu 09:00]--[2026-10-22 Thu 10:30] =>  1:30`
//! lines in a heading's :LOGBOOK: drawer; an open one (no end) is running.

const std = @import("std");
const date = @import("date.zig");

pub const Clock = struct {
    /// Minutes since 1970-01-01 00:00, local time.
    start: i64,
    /// Null while the clock runs.
    end: ?i64,

    pub fn minutes(self: Clock, now: i64) i64 {
        return @max(0, (self.end orelse now) - self.start);
    }
};

/// Minutes since the epoch of a timestamp with a time (midnight without).
pub fn stampMinutes(ts: date.Timestamp) i64 {
    const t = ts.time orelse date.Time{ .hour = 0, .minute = 0 };
    return ts.date.toDays() * 1440 + @as(i64, t.hour) * 60 + t.minute;
}

pub fn nowMinutes() i64 {
    const n = date.now();
    return n.date.toDays() * 1440 + @as(i64, n.time.hour) * 60 + n.time.minute;
}

/// Parses a `CLOCK:` line (leading whitespace allowed).
pub fn parseLine(line: []const u8) ?Clock {
    const t = std.mem.trimStart(u8, line, " \t");
    if (!std.mem.startsWith(u8, t, "CLOCK:")) return null;
    const open = std.mem.indexOfScalar(u8, t, '[') orelse return null;
    // Parsed one end at a time: a range of inactive stamps is one Timestamp.
    const close = std.mem.indexOfScalarPos(u8, t, open, ']') orelse return null;
    const first = date.parseTimestamp(t[0 .. close + 1], open) orelse return null;
    var c: Clock = .{ .start = stampMinutes(first), .end = null };
    if (std.mem.startsWith(u8, t[close + 1 ..], "--[")) {
        if (date.parseTimestamp(t, close + 3)) |second| c.end = stampMinutes(second);
    }
    return c;
}

/// "[2026-10-22 Thu 09:00]" for minutes since the epoch.
pub fn formatStamp(buf: []u8, minutes: i64) []const u8 {
    const d = date.Date.fromDays(@divFloor(minutes, 1440));
    const m: u32 = @intCast(@mod(minutes, 1440));
    var dbuf: [32]u8 = undefined;
    return std.fmt.bufPrint(buf, "[{s} {d:0>2}:{d:0>2}]", .{ d.format(&dbuf), m / 60, m % 60 }) catch "";
}

/// "1:30", org's duration format.
pub fn formatDuration(buf: []u8, minutes: i64) []const u8 {
    const m: u64 = @intCast(@max(0, minutes));
    return std.fmt.bufPrint(buf, "{d}:{d:0>2}", .{ m / 60, m % 60 }) catch "";
}

/// The closed CLOCK line for `start`..`end`, with `indent`.
pub fn closedLine(buf: []u8, indent: []const u8, start: i64, end: i64) []const u8 {
    var a: [40]u8 = undefined;
    var b: [40]u8 = undefined;
    var d: [16]u8 = undefined;
    return std.fmt.bufPrint(buf, "{s}CLOCK: {s}--{s} => {s: >5}", .{ indent, formatStamp(&a, start), formatStamp(&b, end), formatDuration(&d, end - start) }) catch "";
}

test "clock lines" {
    const c = parseLine("   CLOCK: [2026-10-22 Thu 09:00]--[2026-10-22 Thu 10:30] =>  1:30").?;
    try std.testing.expectEqual(@as(i64, 90), c.minutes(0));
    const running = parseLine("CLOCK: [2026-10-22 Thu 09:00]").?;
    try std.testing.expect(running.end == null);
    try std.testing.expectEqual(@as(i64, 15), running.minutes(running.start + 15));
    try std.testing.expect(parseLine("CLOSED: [2026-10-22 Thu 09:00]") == null);
    var buf: [96]u8 = undefined;
    try std.testing.expectEqualStrings("  CLOCK: [2026-10-22 Thu 09:00]--[2026-10-22 Thu 10:30] =>  1:30", closedLine(&buf, "  ", c.start, c.end.?));
    try std.testing.expectEqualStrings("12:05", formatDuration(&buf, 725));
}
