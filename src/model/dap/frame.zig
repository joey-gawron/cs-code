//! DAP wire framing: `Content-Length: N\r\n\r\n` followed by N bytes of JSON.

const std = @import("std");
const Allocator = std.mem.Allocator;

/// A frame larger than this is refused (a hostile or broken adapter).
pub const max_frame_bytes = 32 << 20;

/// Collects bytes as they arrive and hands out whole frames.
pub const Reader = struct {
    buf: std.ArrayList(u8) = .empty,

    pub fn deinit(self: *Reader, gpa: Allocator) void {
        self.buf.deinit(gpa);
    }

    pub fn push(self: *Reader, gpa: Allocator, bytes: []const u8) !void {
        try self.buf.appendSlice(gpa, bytes);
    }

    /// The next complete frame's body, owned by the caller; null when more
    /// bytes are needed. `error.BadFrame` when the header is unusable.
    pub fn pop(self: *Reader, gpa: Allocator) !?[]u8 {
        const header_end = std.mem.indexOf(u8, self.buf.items, "\r\n\r\n") orelse return null;
        var len: ?usize = null;
        var lines = std.mem.splitSequence(u8, self.buf.items[0..header_end], "\r\n");
        while (lines.next()) |line| {
            if (std.ascii.startsWithIgnoreCase(line, "content-length:")) {
                len = std.fmt.parseInt(usize, std.mem.trim(u8, line["content-length:".len..], " "), 10) catch return error.BadFrame;
            }
        }
        const body_len = len orelse return error.BadFrame;
        if (body_len > max_frame_bytes) return error.BadFrame;
        const total = header_end + 4 + body_len;
        if (self.buf.items.len < total) return null;
        const body = try gpa.dupe(u8, self.buf.items[header_end + 4 .. total]);
        std.mem.copyForwards(u8, self.buf.items, self.buf.items[total..]);
        self.buf.shrinkRetainingCapacity(self.buf.items.len - total);
        return body;
    }
};

/// The header for a body of `len` bytes.
pub fn header(buf: *[48]u8, len: usize) []const u8 {
    return std.fmt.bufPrint(buf, "Content-Length: {d}\r\n\r\n", .{len}) catch unreachable;
}

/// Header and body as one buffer (owned by the caller).
pub fn encode(gpa: Allocator, body: []const u8) ![]u8 {
    var h: [48]u8 = undefined;
    const head = header(&h, body.len);
    const out = try gpa.alloc(u8, head.len + body.len);
    @memcpy(out[0..head.len], head);
    @memcpy(out[head.len..], body);
    return out;
}

const testing = std.testing;

test "a frame split across reads is returned once complete" {
    const gpa = testing.allocator;
    var r: Reader = .{};
    defer r.deinit(gpa);
    try r.push(gpa, "Content-Length: 13\r\n\r\n{\"a\"");
    try testing.expect((try r.pop(gpa)) == null);
    try r.push(gpa, ":1,\"b\":2}");
    const first = (try r.pop(gpa)).?;
    defer gpa.free(first);
    try testing.expectEqualStrings("{\"a\":1,\"b\":2}", first);
}

test "two frames in one read come out in order" {
    const gpa = testing.allocator;
    var r: Reader = .{};
    defer r.deinit(gpa);
    try r.push(gpa, "Content-Length: 2\r\n\r\n{}Content-Length: 4\r\n\r\n[11]");
    const a = (try r.pop(gpa)).?;
    defer gpa.free(a);
    const b = (try r.pop(gpa)).?;
    defer gpa.free(b);
    try testing.expectEqualStrings("{}", a);
    try testing.expectEqualStrings("[11]", b);
    try testing.expect((try r.pop(gpa)) == null);
}

test "other headers are ignored and a missing length is an error" {
    const gpa = testing.allocator;
    var r: Reader = .{};
    defer r.deinit(gpa);
    try r.push(gpa, "Content-Type: x\r\ncontent-length: 2\r\n\r\n{}");
    const a = (try r.pop(gpa)).?;
    gpa.free(a);
    try r.push(gpa, "Content-Type: x\r\n\r\n");
    try testing.expectError(error.BadFrame, r.pop(gpa));
}

test "encode prepends the header" {
    const gpa = testing.allocator;
    const out = try encode(gpa, "{\"x\":1}");
    defer gpa.free(out);
    try testing.expectEqualStrings("Content-Length: 7\r\n\r\n{\"x\":1}", out);
}
