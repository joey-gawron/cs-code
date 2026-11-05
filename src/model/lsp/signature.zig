//! textDocument/signatureHelp responses: the signature to show and which
//! parameter of it the caret is on.

const std = @import("std");
const protocol = @import("protocol.zig");

const Allocator = std.mem.Allocator;
const Value = std.json.Value;

pub const Signature = struct {
    label: []const u8,
    /// The byte range of the active parameter in `label`; empty when none.
    param_start: usize,
    param_end: usize,
    /// The signature's documentation as plain text, possibly empty.
    doc: []const u8,
    /// Which of the server's signatures this is, and how many there are.
    index: usize,
    count: usize,
};

/// The byte offset in `text` after `units` UTF-16 code units (LSP label offsets).
fn utf16ToByte(text: []const u8, units: usize) usize {
    var u: usize = 0;
    var i: usize = 0;
    while (i < text.len and u < units) {
        const len = std.unicode.utf8ByteSequenceLength(text[i]) catch 1;
        u += if (len == 4) 2 else 1;
        i += len;
    }
    return @min(i, text.len);
}

/// The active signature of a signatureHelp `result`, or null when the server
/// has none (the popup closes). Strings are allocated from `alloc`.
pub fn parse(alloc: Allocator, result: Value) !?Signature {
    const sigs = protocol.get(result, "signatures") orelse return null;
    if (sigs != .array or sigs.array.items.len == 0) return null;
    const count = sigs.array.items.len;
    const active_sig: usize = @intCast(@max(0, @min(protocol.getInt(result, "activeSignature") orelse 0, @as(i64, @intCast(count)) - 1)));
    const sig = sigs.array.items[active_sig];
    const label = protocol.getStr(sig, "label") orelse return null;

    // A signature's own activeParameter wins over the response's.
    const active_param = protocol.getInt(sig, "activeParameter") orelse protocol.getInt(result, "activeParameter") orelse 0;
    var start: usize = 0;
    var end: usize = 0;
    if (protocol.get(sig, "parameters")) |params| if (params == .array and active_param >= 0 and active_param < params.array.items.len) {
        const param_label = protocol.get(params.array.items[@intCast(active_param)], "label");
        if (param_label) |pl| switch (pl) {
            .string => |s| if (s.len > 0) if (std.mem.indexOf(u8, label, s)) |at| {
                start = at;
                end = at + s.len;
            },
            .array => |a| if (a.items.len == 2 and a.items[0] == .integer and a.items[1] == .integer) {
                start = utf16ToByte(label, @intCast(@max(0, a.items[0].integer)));
                end = utf16ToByte(label, @intCast(@max(0, a.items[1].integer)));
                if (end < start) end = start;
            },
            else => {},
        };
    };

    var doc: std.ArrayList(u8) = .empty;
    if (protocol.get(sig, "documentation")) |d| protocol.markupToText(alloc, d, &doc) catch {};
    return .{
        .label = try alloc.dupe(u8, label),
        .param_start = start,
        .param_end = end,
        .doc = try alloc.dupe(u8, std.mem.trim(u8, doc.items, " \n")),
        .index = active_sig,
        .count = count,
    };
}

fn parseJson(arena: Allocator, text: []const u8) !Value {
    return try std.json.parseFromSliceLeaky(Value, arena, text, .{});
}

test "the active parameter is found from label offsets" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const v = try parseJson(arena.allocator(),
        \\{"signatures":[{"label":"fn add(a: i32, b: i32) i32","parameters":[{"label":[7,13]},{"label":[15,21]}],
        \\"documentation":"Adds."}],"activeSignature":0,"activeParameter":1}
    );
    const s = (try parse(arena.allocator(), v)).?;
    try std.testing.expectEqualStrings("b: i32", s.label[s.param_start..s.param_end]);
    try std.testing.expectEqualStrings("Adds.", s.doc);
    try std.testing.expectEqual(@as(usize, 1), s.count);
}

test "the active parameter is found from a label string" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const v = try parseJson(arena.allocator(),
        \\{"signatures":[{"label":"f(x, y)","parameters":[{"label":"x"},{"label":"y"}],"activeParameter":0},
        \\{"label":"f(x)","parameters":[{"label":"x"}]}],"activeSignature":0,"activeParameter":1}
    );
    const s = (try parse(arena.allocator(), v)).?;
    // The signature's own activeParameter (0) beats the response's (1).
    try std.testing.expectEqualStrings("x", s.label[s.param_start..s.param_end]);
    try std.testing.expectEqual(@as(usize, 2), s.count);
}

test "offsets count UTF-16 units, not bytes" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    // "é" is two bytes but one unit, so [5,6] is "b" and starts at byte 6.
    const v = try parseJson(arena.allocator(),
        \\{"signatures":[{"label":"f(é, b)","parameters":[{"label":[2,3]},{"label":[5,6]}]}],"activeParameter":1}
    );
    const s = (try parse(arena.allocator(), v)).?;
    try std.testing.expectEqualStrings("b", s.label[s.param_start..s.param_end]);
}

test "no signatures closes the popup" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    try std.testing.expect((try parse(arena.allocator(), try parseJson(arena.allocator(), "null"))) == null);
    try std.testing.expect((try parse(arena.allocator(), try parseJson(arena.allocator(), "{\"signatures\":[]}"))) == null);
}
