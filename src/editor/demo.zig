//! Scripted demo steps (`cs --demo "scroll:2000;key:down;quit"`), the same grammar as the Rust
//! spike's `demo.rs` apart from `mark:` (input-method composition), which is not implemented.
//!
//! GUI automation is not available on the development machine (no accessibility access for
//! synthetic events), so the editor drives itself from a script instead. This module only
//! parses; the main loop runs the steps.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Step = union(enum) {
    /// Scroll down by this many pixels (negative scrolls up).
    scroll: f64,
    /// Scroll right by this many pixels (negative scrolls left).
    scrollx: f64,
    /// A keystroke such as `down`, `cmd-down` or `pagedown`.
    key: []const u8,
    /// Text typed one character at a time.
    type: []const u8,
    /// A left click at window coordinates, in points.
    click: [2]f32,
    /// Moves the caret to this 1-based line.
    goto: usize,
    /// Pauses for this many milliseconds.
    wait: u64,
    quit,
};

pub const Parsed = struct {
    steps: []Step,
    errors: [][]const u8,
};

/// Parses a `;`-separated script. Steps that did not parse are reported in `errors`; empty steps
/// are skipped. Strings point into `script`, which must outlive the result.
pub fn parse(arena: Allocator, script: []const u8) !Parsed {
    var steps: std.ArrayList(Step) = .empty;
    var errors: std.ArrayList([]const u8) = .empty;
    var it = std.mem.splitScalar(u8, script, ';');
    while (it.next()) |raw| {
        if (std.mem.trim(u8, raw, " \t").len == 0) continue;
        if (parseStep(raw)) |step| try steps.append(arena, step) else try errors.append(arena, raw);
    }
    return .{ .steps = steps.items, .errors = errors.items };
}

fn parseStep(raw: []const u8) ?Step {
    var name = raw;
    var arg: []const u8 = "";
    if (std.mem.indexOfScalar(u8, raw, ':')) |i| {
        name = raw[0..i];
        arg = raw[i + 1 ..];
    }
    name = std.mem.trim(u8, name, " \t");
    // `type:` keeps its argument verbatim so spaces can be typed; the rest are trimmed.
    const trimmed = std.mem.trim(u8, arg, " \t");
    const eql = std.mem.eql;
    if (eql(u8, name, "scroll")) return .{ .scroll = std.fmt.parseFloat(f64, trimmed) catch return null };
    if (eql(u8, name, "scrollx")) return .{ .scrollx = std.fmt.parseFloat(f64, trimmed) catch return null };
    if (eql(u8, name, "key") and trimmed.len > 0) return .{ .key = trimmed };
    if (eql(u8, name, "type") and arg.len > 0) return .{ .type = arg };
    if (eql(u8, name, "click")) {
        const comma = std.mem.indexOfScalar(u8, trimmed, ',') orelse return null;
        const x = std.fmt.parseFloat(f32, std.mem.trim(u8, trimmed[0..comma], " ")) catch return null;
        const y = std.fmt.parseFloat(f32, std.mem.trim(u8, trimmed[comma + 1 ..], " ")) catch return null;
        return .{ .click = .{ x, y } };
    }
    if (eql(u8, name, "goto")) {
        const line = std.fmt.parseInt(usize, trimmed, 10) catch return null;
        return if (line > 0) .{ .goto = line } else null;
    }
    if (eql(u8, name, "wait")) return .{ .wait = std.fmt.parseInt(u64, trimmed, 10) catch return null };
    if (eql(u8, name, "quit") and trimmed.len == 0) return .quit;
    return null;
}

test "parses every step kind" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const p = try parse(arena.allocator(), "scroll:2000; scrollx:-40;key:cmd-down;type:hi there;click:120.5,300;goto:42;wait:500;quit;");
    try std.testing.expectEqual(@as(usize, 0), p.errors.len);
    try std.testing.expectEqual(@as(usize, 8), p.steps.len);
    try std.testing.expectEqual(@as(f64, 2000), p.steps[0].scroll);
    try std.testing.expectEqualStrings("hi there", p.steps[3].type);
    try std.testing.expectEqual(@as(usize, 42), p.steps[5].goto);
    try std.testing.expect(p.steps[7] == .quit);
}

test "reports unknown and malformed steps" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const p = try parse(arena.allocator(), "jump:3;scroll:far;click:1;goto:0;key:;wait:2");
    try std.testing.expectEqual(@as(usize, 1), p.steps.len);
    try std.testing.expectEqual(@as(usize, 5), p.errors.len);
}
