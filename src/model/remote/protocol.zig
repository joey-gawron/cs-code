//! The wire protocol between the editor and cs-zig-server (Remote-SSH).
//!
//! After a one-line text banner ("CS-ZIG-SERVER <protocol> <arch>\n", so the
//! client can skip anything a login shell prints first) both sides exchange
//! frames: an 8-byte header (JSON length, payload length; little endian), a
//! JSON object, then raw payload bytes. File contents, process output and
//! scan results travel in the payload, so they are never escaped.
//!
//!   request       {"id":7,"method":"read","params":{"path":"/etc/hosts"}}
//!   response      {"id":7,"result":{...}}   or   {"id":7,"error":"FileNotFound"}
//!   notification  {"method":"changed","params":{...}}   (no id, no answer)
//!
//! Shared by the client (model/remote/client.zig) and the server
//! (src/server), so it only uses std and raw Linux syscalls (the server is
//! built without libc).

const std = @import("std");
const linux = @import("sys.zig");

const Allocator = std.mem.Allocator;

/// Bumped on any incompatible change; part of the remote install directory.
pub const protocol_version: u32 = 1;
pub const banner_magic = "CS-ZIG-SERVER";
/// Printed by the launch script when the server binary isn't installed.
pub const missing_magic = "CS-ZIG-MISSING";

pub const header_len = 8;
pub const max_json = 64 << 20;
pub const max_payload = 1 << 30;

/// Errors that cross the wire by name (`@errorName` on the server).
pub const RemoteError = error{
    FileNotFound,
    AccessDenied,
    PermissionDenied,
    IsDir,
    NotDir,
    PathAlreadyExists,
    DirNotEmpty,
    NoSpaceLeft,
    FileTooBig,
    StreamTooLong,
    NameTooLong,
    SymLinkLoop,
    ReadOnlyFileSystem,
    BadPathName,
    InvalidArgument,
    /// Any other server-side failure.
    RemoteFailure,
    /// The connection is down (or was never up).
    NotConnected,
    Timeout,
    BadMessage,
};

pub fn errorFromName(name: []const u8) RemoteError {
    inline for (@typeInfo(RemoteError).error_set.error_names.?) |e| {
        if (std.mem.eql(u8, e, name)) return @field(RemoteError, e);
    }
    return error.RemoteFailure;
}

// ---- Frames ---------------------------------------------------------------------

pub const Frame = struct {
    json: []const u8,
    payload: []const u8,
};

pub fn encodeHeader(json_len: usize, payload_len: usize) [header_len]u8 {
    var h: [header_len]u8 = undefined;
    std.mem.writeInt(u32, h[0..4], @intCast(json_len), .little);
    std.mem.writeInt(u32, h[4..8], @intCast(payload_len), .little);
    return h;
}

/// Splits a byte stream into frames. Feed it whatever `read` returned; the
/// slices `next` returns stay valid until the next `feed`.
pub const FrameReader = struct {
    buf: std.ArrayList(u8) = .empty,
    /// Start of the first unconsumed byte.
    start: usize = 0,

    pub fn deinit(self: *FrameReader, gpa: Allocator) void {
        self.buf.deinit(gpa);
    }

    pub fn feed(self: *FrameReader, gpa: Allocator, bytes: []const u8) !void {
        // Drop consumed bytes before growing.
        if (self.start > 0) {
            const rest = self.buf.items.len - self.start;
            std.mem.copyForwards(u8, self.buf.items[0..rest], self.buf.items[self.start..]);
            self.buf.shrinkRetainingCapacity(rest);
            self.start = 0;
        }
        try self.buf.appendSlice(gpa, bytes);
    }

    pub fn next(self: *FrameReader) error{BadMessage}!?Frame {
        const avail = self.buf.items[self.start..];
        if (avail.len < header_len) return null;
        const json_len = std.mem.readInt(u32, avail[0..4], .little);
        const payload_len = std.mem.readInt(u32, avail[4..8], .little);
        if (json_len > max_json or payload_len > max_payload) return error.BadMessage;
        const total = header_len + @as(usize, json_len) + payload_len;
        if (avail.len < total) return null;
        self.start += total;
        return .{
            .json = avail[header_len..][0..json_len],
            .payload = avail[header_len + json_len ..][0..payload_len],
        };
    }
};

// ---- Raw fd I/O (no libc on the server) -----------------------------------------

pub fn writeAll(fd: i32, bytes: []const u8) error{WriteFailed}!void {
    var off: usize = 0;
    while (off < bytes.len) {
        const rc = linux.write(fd, bytes[off..].ptr, bytes.len - off);
        switch (linux.errno(rc)) {
            .SUCCESS => {
                if (rc == 0) return error.WriteFailed;
                off += rc;
            },
            .INTR, .AGAIN => continue,
            else => return error.WriteFailed,
        }
    }
}

/// Returns 0 at end of file.
pub fn readSome(fd: i32, buf: []u8) error{ReadFailed}!usize {
    while (true) {
        const rc = linux.read(fd, buf.ptr, buf.len);
        switch (linux.errno(rc)) {
            .SUCCESS => return rc,
            .INTR => continue,
            else => return error.ReadFailed,
        }
    }
}

pub fn closeFd(fd: i32) void {
    _ = linux.close(fd);
}

/// Waits until `fd` is readable (or hung up); false on timeout.
pub fn waitReadable(fd: i32, timeout_ms: i32) bool {
    var fds = [_]linux.pollfd{.{ .fd = fd, .events = linux.POLL.IN, .revents = 0 }};
    while (true) {
        const rc = linux.poll(&fds, 1, timeout_ms);
        switch (linux.errno(rc)) {
            .SUCCESS => return rc > 0,
            .INTR => continue,
            else => return true,
        }
    }
}

/// Writes one frame. Callers serialize writers on the same fd.
pub fn writeFrame(fd: i32, json: []const u8, payload: []const u8) error{WriteFailed}!void {
    const h = encodeHeader(json.len, payload.len);
    try writeAll(fd, &h);
    try writeAll(fd, json);
    try writeAll(fd, payload);
}

// ---- Payload encodings ----------------------------------------------------------

/// Appends length-prefixed fields to a payload.
pub const Encoder = struct {
    out: std.ArrayList(u8) = .empty,

    pub fn deinit(self: *Encoder, gpa: Allocator) void {
        self.out.deinit(gpa);
    }

    pub fn int(self: *Encoder, gpa: Allocator, comptime T: type, v: T) !void {
        var b: [@sizeOf(T)]u8 = undefined;
        std.mem.writeInt(T, &b, v, .little);
        try self.out.appendSlice(gpa, &b);
    }

    pub fn bytes(self: *Encoder, gpa: Allocator, s: []const u8) !void {
        try self.int(gpa, u32, @intCast(s.len));
        try self.out.appendSlice(gpa, s);
    }
};

pub const Decoder = struct {
    data: []const u8,
    pos: usize = 0,

    pub fn done(self: *const Decoder) bool {
        return self.pos >= self.data.len;
    }

    pub fn int(self: *Decoder, comptime T: type) error{BadMessage}!T {
        if (self.pos + @sizeOf(T) > self.data.len) return error.BadMessage;
        const v = std.mem.readInt(T, self.data[self.pos..][0..@sizeOf(T)], .little);
        self.pos += @sizeOf(T);
        return v;
    }

    pub fn bytes(self: *Decoder) error{BadMessage}![]const u8 {
        const n = try self.int(u32);
        if (self.pos + n > self.data.len) return error.BadMessage;
        const s = self.data[self.pos..][0..n];
        self.pos += n;
        return s;
    }
};

/// One project entry as sent by `scan`: kind, ignored flag, depth, path.
pub const ScanEntry = struct {
    path: []const u8,
    dir: bool,
    ignored: bool,
    depth: u16,
};

pub fn encodeScanEntry(enc: *Encoder, gpa: Allocator, e: ScanEntry) !void {
    try enc.int(gpa, u8, @intFromBool(e.dir) | @as(u8, @intFromBool(e.ignored)) << 1);
    try enc.int(gpa, u16, e.depth);
    try enc.bytes(gpa, e.path);
}

pub fn decodeScanEntry(dec: *Decoder) !ScanEntry {
    const flags = try dec.int(u8);
    const depth = try dec.int(u16);
    return .{ .path = try dec.bytes(), .dir = flags & 1 != 0, .ignored = flags & 2 != 0, .depth = depth };
}

// ---- JSON helpers ---------------------------------------------------------------

pub const Value = std.json.Value;

pub fn get(v: ?Value, key: []const u8) ?Value {
    const obj = v orelse return null;
    if (obj != .object) return null;
    return obj.object.get(key);
}

pub fn getStr(v: ?Value, key: []const u8) ?[]const u8 {
    const x = get(v, key) orelse return null;
    return if (x == .string) x.string else null;
}

pub fn getInt(v: ?Value, key: []const u8) ?i64 {
    const x = get(v, key) orelse return null;
    return if (x == .integer) x.integer else null;
}

pub fn getBool(v: ?Value, key: []const u8) bool {
    const x = get(v, key) orelse return false;
    return x == .bool and x.bool;
}

/// A JSON array of strings (other elements are skipped), allocated in `alloc`.
pub fn getStrings(alloc: Allocator, v: ?Value, key: []const u8) ![]const []const u8 {
    const x = get(v, key) orelse return &.{};
    if (x != .array) return &.{};
    var out: std.ArrayList([]const u8) = .empty;
    for (x.array.items) |item| if (item == .string) try out.append(alloc, item.string);
    return out.items;
}

/// Machine names as `uname -m` prints them, normalized to Zig's arch names.
pub fn normalizeArch(machine: []const u8) ?[]const u8 {
    const m = std.mem.trim(u8, machine, " \t\r\n");
    if (std.mem.eql(u8, m, "x86_64") or std.mem.eql(u8, m, "amd64")) return "x86_64";
    if (std.mem.eql(u8, m, "aarch64") or std.mem.eql(u8, m, "arm64")) return "aarch64";
    return null;
}

test "frames split across reads" {
    const gpa = std.testing.allocator;
    var stream: std.ArrayList(u8) = .empty;
    defer stream.deinit(gpa);
    for ([_][2][]const u8{ .{ "{\"id\":1}", "abc" }, .{ "{}", "" }, .{ "{\"method\":\"x\"}", "\x00\xff" } }) |f| {
        try stream.appendSlice(gpa, &encodeHeader(f[0].len, f[1].len));
        try stream.appendSlice(gpa, f[0]);
        try stream.appendSlice(gpa, f[1]);
    }
    var r: FrameReader = .{};
    defer r.deinit(gpa);
    var got: usize = 0;
    // One byte at a time: every frame still comes out whole.
    for (stream.items) |byte| {
        try r.feed(gpa, &.{byte});
        while (try r.next()) |frame| {
            switch (got) {
                0 => {
                    try std.testing.expectEqualStrings("{\"id\":1}", frame.json);
                    try std.testing.expectEqualStrings("abc", frame.payload);
                },
                1 => try std.testing.expectEqual(@as(usize, 0), frame.payload.len),
                else => try std.testing.expectEqualSlices(u8, "\x00\xff", frame.payload),
            }
            got += 1;
        }
    }
    try std.testing.expectEqual(@as(usize, 3), got);
}

test "oversized frames are rejected" {
    const gpa = std.testing.allocator;
    var r: FrameReader = .{};
    defer r.deinit(gpa);
    try r.feed(gpa, &encodeHeader(max_json + 1, 0));
    try std.testing.expectError(error.BadMessage, r.next());
}

test "scan entries and errors round-trip" {
    const gpa = std.testing.allocator;
    var enc: Encoder = .{};
    defer enc.deinit(gpa);
    try encodeScanEntry(&enc, gpa, .{ .path = "src", .dir = true, .ignored = false, .depth = 0 });
    try encodeScanEntry(&enc, gpa, .{ .path = "src/main.zig", .dir = false, .ignored = true, .depth = 1 });
    var dec: Decoder = .{ .data = enc.out.items };
    const a = try decodeScanEntry(&dec);
    const b = try decodeScanEntry(&dec);
    try std.testing.expect(dec.done());
    try std.testing.expect(a.dir and !a.ignored and a.depth == 0);
    try std.testing.expectEqualStrings("src/main.zig", b.path);
    try std.testing.expect(!b.dir and b.ignored and b.depth == 1);
    try std.testing.expectError(error.BadMessage, decodeScanEntry(&dec));

    try std.testing.expectEqual(error.FileNotFound, errorFromName("FileNotFound"));
    try std.testing.expectEqual(error.RemoteFailure, errorFromName("SomethingElse"));
    try std.testing.expectEqualStrings("aarch64", normalizeArch("arm64\n").?);
    try std.testing.expect(normalizeArch("riscv64") == null);
}
