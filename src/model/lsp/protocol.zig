//! LSP helpers: file URIs, position encodings and JSON value access.

const std = @import("std");
const Buffer = @import("../text/buffer.zig").Buffer;
const rpath = @import("../remote/path.zig");

const Allocator = std.mem.Allocator;
const Value = std.json.Value;

pub const Encoding = enum { utf8, utf16 };

pub const Position = struct { line: u32, character: u32 };

/// `ssh://host/abs` becomes `file:///abs`: remote servers run on that host.
pub fn pathToUri(gpa: Allocator, path_in: []const u8) ![]u8 {
    const path = if (rpath.parse(path_in)) |r| r.path else path_in;
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    try out.appendSlice(gpa, "file://");
    for (path) |ch| {
        if (std.ascii.isAlphanumeric(ch) or std.mem.indexOfScalar(u8, "/-._~", ch) != null) {
            try out.append(gpa, ch);
        } else {
            var buf: [3]u8 = undefined;
            _ = try std.fmt.bufPrint(&buf, "%{X:0>2}", .{ch});
            try out.appendSlice(gpa, &buf);
        }
    }
    return out.toOwnedSlice(gpa);
}

pub fn uriToPath(gpa: Allocator, uri: []const u8) ![]u8 {
    const prefix = "file://";
    if (!std.mem.startsWith(u8, uri, prefix)) return error.NotAFileUri;
    const rest = uri[prefix.len..];
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var i: usize = 0;
    while (i < rest.len) : (i += 1) {
        if (rest[i] == '%' and i + 2 < rest.len) {
            const byte = std.fmt.parseInt(u8, rest[i + 1 .. i + 3], 16) catch {
                try out.append(gpa, '%');
                continue;
            };
            try out.append(gpa, byte);
            i += 2;
        } else try out.append(gpa, rest[i]);
    }
    return out.toOwnedSlice(gpa);
}

fn utf16Len(bytes: []const u8) u32 {
    var n: u32 = 0;
    var i: usize = 0;
    while (i < bytes.len) {
        const len = std.unicode.utf8ByteSequenceLength(bytes[i]) catch 1;
        n += if (len == 4) 2 else 1;
        i += len;
    }
    return n;
}

pub fn offsetToPosition(buffer: *const Buffer, offset: usize, enc: Encoding, scratch: *std.ArrayList(u8)) Position {
    const p = buffer.offsetToPoint(@min(offset, buffer.len()));
    if (enc == .utf8) return .{ .line = p.row, .character = p.column };
    const line = buffer.lineText(p.row, scratch) catch return .{ .line = p.row, .character = p.column };
    return .{ .line = p.row, .character = utf16Len(line[0..@min(p.column, line.len)]) };
}

pub fn positionToOffset(buffer: *const Buffer, pos: Position, enc: Encoding, scratch: *std.ArrayList(u8)) usize {
    if (pos.line >= buffer.lineCount()) return buffer.len();
    const start = buffer.lineStart(pos.line);
    const line_len = buffer.lineLen(pos.line);
    if (enc == .utf8) return start + @min(pos.character, line_len);
    const line = buffer.lineText(pos.line, scratch) catch return start;
    var units: u32 = 0;
    var i: usize = 0;
    while (i < line.len and units < pos.character) {
        const len = std.unicode.utf8ByteSequenceLength(line[i]) catch 1;
        units += if (len == 4) 2 else 1;
        i += len;
    }
    return start + @min(i, line.len);
}

// ---- JSON access ----------------------------------------------------------------

pub fn get(v: ?Value, key: []const u8) ?Value {
    const val = v orelse return null;
    if (val != .object) return null;
    return val.object.get(key);
}

pub fn getStr(v: ?Value, key: []const u8) ?[]const u8 {
    const val = get(v, key) orelse return null;
    return if (val == .string) val.string else null;
}

pub fn getInt(v: ?Value, key: []const u8) ?i64 {
    const val = get(v, key) orelse return null;
    return switch (val) {
        .integer => |i| i,
        .float => |f| @intFromFloat(f),
        else => null,
    };
}

pub fn getBool(v: ?Value, key: []const u8) bool {
    const val = get(v, key) orelse return false;
    return switch (val) {
        .bool => |b| b,
        .object => true, // e.g. "hoverProvider": { ... }
        else => false,
    };
}

pub fn getPosition(v: ?Value) ?Position {
    const line = getInt(v, "line") orelse return null;
    const character = getInt(v, "character") orelse return null;
    return .{ .line = @intCast(@max(0, line)), .character = @intCast(@max(0, character)) };
}

pub const Range = struct { start: Position, end: Position };

pub fn getRange(v: ?Value) ?Range {
    const r = v orelse return null;
    return .{
        .start = getPosition(get(r, "start")) orelse return null,
        .end = getPosition(get(r, "end")) orelse return null,
    };
}

/// Flattens hover / documentation content (MarkupContent, MarkedString or
/// arrays of them) into plain text, dropping markdown code fences.
pub fn markupToText(gpa: Allocator, v: Value, out: *std.ArrayList(u8)) !void {
    switch (v) {
        .string => |s| try appendMarkdown(gpa, s, out),
        .object => {
            if (getStr(v, "value")) |s| try appendMarkdown(gpa, s, out);
        },
        .array => |arr| for (arr.items, 0..) |item, i| {
            if (i > 0) try out.appendSlice(gpa, "\n");
            try markupToText(gpa, item, out);
        },
        else => {},
    }
}

fn appendMarkdown(gpa: Allocator, s: []const u8, out: *std.ArrayList(u8)) !void {
    var lines = std.mem.splitScalar(u8, s, '\n');
    while (lines.next()) |line| {
        const t = std.mem.trimEnd(u8, line, "\r");
        if (std.mem.startsWith(u8, std.mem.trimStart(u8, t, " "), "```")) continue;
        // Light markdown cleanup: drop escapes like "\_".
        var i: usize = 0;
        while (i < t.len) : (i += 1) {
            if (t[i] == '\\' and i + 1 < t.len and std.mem.indexOfScalar(u8, "_*`[]()#<>", t[i + 1]) != null) continue;
            try out.append(gpa, t[i]);
        }
        try out.append(gpa, '\n');
    }
}

test "uri round trip" {
    const gpa = std.testing.allocator;
    const uri = try pathToUri(gpa, "/home/joe/my file.zig");
    defer gpa.free(uri);
    try std.testing.expectEqualStrings("file:///home/joe/my%20file.zig", uri);
    const path = try uriToPath(gpa, uri);
    defer gpa.free(path);
    try std.testing.expectEqualStrings("/home/joe/my file.zig", path);
    // Remote paths map to the host's own file URIs.
    const remote = try pathToUri(gpa, "ssh://me@devbox:2222/srv/a.zig");
    defer gpa.free(remote);
    try std.testing.expectEqualStrings("file:///srv/a.zig", remote);
}

test "utf16 positions" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "a😀b\nxy");
    defer buf.deinit();
    var scratch: std.ArrayList(u8) = .empty;
    defer scratch.deinit(gpa);
    // 'b' is at byte 5, utf-16 column 3.
    try std.testing.expectEqual(@as(u32, 3), offsetToPosition(&buf, 5, .utf16, &scratch).character);
    try std.testing.expectEqual(@as(usize, 5), positionToOffset(&buf, .{ .line = 0, .character = 3 }, .utf16, &scratch));
    try std.testing.expectEqual(@as(usize, 8), positionToOffset(&buf, .{ .line = 1, .character = 1 }, .utf8, &scratch));
}
