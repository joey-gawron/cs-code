//! A persistent rope of UTF-8 text built on `SumTree` — a port of Zed's
//! `rope` crate.
//!
//! Text is stored in `Chunk`s of at most `CHUNK_MAX` bytes held inline in the
//! tree's leaves. Invariants (checked by `checkInvariants`):
//!   * chunks are non-empty and never split a UTF-8 codepoint;
//!   * every chunk except the last holds at least `CHUNK_MIN - 3` bytes
//!     (the 3 bytes of slack let chunk boundaries fall on char boundaries).
//! The rope assumes valid UTF-8 with `\n` line endings; normalization happens
//! before text gets here.
//!
//! Positions are byte offsets (`usize`) or `Point`s (row + byte column).
//! Edits are O(log n + inserted text) and never affect clones: `replace`
//! builds the new tree out of shared slices of the old one.

const std = @import("std");
const Allocator = std.mem.Allocator;
const Order = std.math.Order;
const assert = std.debug.assert;
const sum_tree = @import("sum_tree.zig");

pub const Bias = sum_tree.Bias;

/// 1 KB chunks keep per-chunk overhead (its 40-byte summary, leaf headers)
/// near 5% of the text; 128-byte chunks cost 40%. Typing stays cheap because
/// an edit inside one chunk is made in place (see `replace`). Tests also use
/// small chunks (`RopeWith`) to get many chunk boundaries out of little text.
pub const CHUNK_MAX = 1024;
pub const CHUNK_MIN = CHUNK_MAX / 2;

/// A row/column position. `column` is in bytes.
pub const Point = struct {
    row: u32 = 0,
    column: u32 = 0,

    pub const zero: Point = .{};

    pub fn init(row: u32, column: u32) Point {
        return .{ .row = row, .column = column };
    }

    pub fn order(a: Point, b: Point) Order {
        return switch (std.math.order(a.row, b.row)) {
            .eq => std.math.order(a.column, b.column),
            else => |o| o,
        };
    }

    /// Dimension/seek-target protocol (see sum_tree.zig).
    pub fn cmp(a: *const Point, b: *const Point) Order {
        return order(a.*, b.*);
    }

    pub fn lessThan(a: Point, b: Point) bool {
        return order(a, b) == .lt;
    }

    pub fn eql(a: Point, b: Point) bool {
        return a.row == b.row and a.column == b.column;
    }

    pub fn min(a: Point, b: Point) Point {
        return if (order(a, b) == .gt) b else a;
    }

    pub fn max(a: Point, b: Point) Point {
        return if (order(a, b) == .lt) b else a;
    }

    /// Position reached by moving `b` (a row/column extent) from `a`: if
    /// `b` stays on one row the columns add, otherwise `b`'s column wins.
    pub fn add(a: Point, b: Point) Point {
        if (b.row == 0) return .{ .row = a.row, .column = a.column + b.column };
        return .{ .row = a.row + b.row, .column = b.column };
    }

    /// Inverse of `add`: `b.add(a.sub(b)) == a`. Requires `a >= b`.
    pub fn sub(a: Point, b: Point) Point {
        assert(order(a, b) != .lt);
        if (a.row == b.row) return .{ .row = 0, .column = a.column - b.column };
        return .{ .row = a.row - b.row, .column = a.column };
    }

    /// Dimension protocol: a cursor over `Point` tracks `summary.lines`.
    pub fn addSummary(self: *Point, summary: *const TextSummary) void {
        self.* = self.add(summary.lines);
    }
};

/// Summary of a span of text (Zed's `TextSummary`, minus UTF-16 fields).
pub const TextSummary = struct {
    /// Length in bytes.
    len: usize = 0,
    /// Number of codepoints (including newlines).
    chars: usize = 0,
    /// Number of newlines, and bytes after the last one.
    lines: Point = .{},
    /// Codepoints on the first / last line (newline excluded).
    first_line_chars: u32 = 0,
    last_line_chars: u32 = 0,
    /// The row with the most codepoints (the earliest on ties), relative to
    /// the start of the span, and its codepoint count.
    longest_row: u32 = 0,
    longest_row_chars: u32 = 0,

    pub const zero: TextSummary = .{};

    /// Line by line: newlines are found and continuation bytes counted
    /// with SIMD (every chunk summary is a pass over its text).
    pub fn fromText(text: []const u8) TextSummary {
        var s: TextSummary = .{ .len = text.len };
        var start: usize = 0;
        while (true) {
            const nl = std.mem.findScalarPos(u8, text, start, '\n');
            const line = text[start .. nl orelse text.len];
            const line_chars: u32 = @intCast(line.len - countContinuations(line));
            if (s.lines.row == 0) s.first_line_chars = line_chars;
            if (line_chars > s.longest_row_chars) {
                s.longest_row = s.lines.row;
                s.longest_row_chars = line_chars;
            }
            s.chars += line_chars;
            if (nl) |i| {
                s.chars += 1;
                s.lines.row += 1;
                start = i + 1;
            } else {
                s.lines.column = @intCast(line.len);
                s.last_line_chars = line_chars;
                return s;
            }
        }
    }

    /// Summary of `self` followed by `other` (Zed's `TextSummary::add_assign`).
    pub fn add(self: *TextSummary, other: *const TextSummary) void {
        const joined_chars = self.last_line_chars + other.first_line_chars;
        if (joined_chars > self.longest_row_chars) {
            self.longest_row = self.lines.row;
            self.longest_row_chars = joined_chars;
        }
        if (other.longest_row_chars > self.longest_row_chars) {
            self.longest_row = self.lines.row + other.longest_row;
            self.longest_row_chars = other.longest_row_chars;
        }
        if (self.lines.row == 0) self.first_line_chars += other.first_line_chars;
        if (other.lines.row == 0) {
            self.last_line_chars += other.first_line_chars;
        } else {
            self.last_line_chars = other.last_line_chars;
        }
        self.chars += other.chars;
        self.len += other.len;
        self.lines = self.lines.add(other.lines);
    }

    /// Dimension protocol: a cursor over `TextSummary` accumulates everything.
    pub fn addSummary(self: *TextSummary, other: *const TextSummary) void {
        self.add(other);
    }

    /// Dimension protocol: `usize` (alias `ByteOffset`) tracks byte length.
    pub fn addToDimension(self: *const TextSummary, comptime D: type, d: *D) void {
        if (D != usize) @compileError("TextSummary cannot be projected onto " ++ @typeName(D));
        d.* += self.len;
    }

    /// Seek protocol: a cursor over `TextSummary` can seek by byte offset
    /// (`usize`) or by `Point`.
    pub fn orderTarget(self: *const TextSummary, target: anytype) Order {
        const T = @TypeOf(target.*);
        if (comptime T == usize) {
            return std.math.order(target.*, self.len);
        } else if (comptime T == Point) {
            return Point.order(target.*, self.lines);
        } else {
            @compileError("cannot seek a TextSummary cursor by " ++ @typeName(T));
        }
    }
};

/// Byte offsets are a `usize` dimension over `TextSummary`.
pub const ByteOffset = usize;

/// Cursor dimension tracking byte offset (`.a`) and point (`.b`); seek it by
/// either a `usize` or a `Point`.
pub const OffsetPoint = sum_tree.Dims2(usize, Point);

/// A leaf item: up to `CHUNK_MAX` bytes of text stored inline.
pub const Chunk = ChunkWithMax(CHUNK_MAX);

pub fn ChunkWithMax(comptime max: usize) type {
    return struct {
        const Self = @This();

        len: std.math.IntFittingRange(0, max) = 0,
        buf: [max]u8 = undefined,

        pub const Summary = TextSummary;

        pub fn init(bytes: []const u8) Self {
            assert(bytes.len > 0 and bytes.len <= max);
            var c: Self = .{ .len = @intCast(bytes.len) };
            @memcpy(c.buf[0..bytes.len], bytes);
            return c;
        }

        pub fn text(self: *const Self) []const u8 {
            return self.buf[0..self.len];
        }

        pub fn summary(self: *const Self) TextSummary {
            return .fromText(self.text());
        }

        // `SumTree.updateLast` callbacks (context first).
        fn appendBytes(bytes: []const u8, self: *Self) void {
            assert(self.len + bytes.len <= max);
            @memcpy(self.buf[self.len..][0..bytes.len], bytes);
            self.len += @intCast(bytes.len);
        }

        fn setBytes(bytes: []const u8, self: *Self) void {
            self.* = .init(bytes);
        }

        /// Replaces bytes [at, at + old) with `text` (`SumTree.updateAt` callback).
        const Splice = struct { at: usize, old: usize, text: []const u8 };

        fn splice(e: Splice, self: *Self, _: usize) bool {
            const tail = self.len - e.at - e.old;
            const new_len = self.len - e.old + e.text.len;
            assert(new_len <= max);
            const src = self.buf[e.at + e.old ..][0..tail];
            const dst = self.buf[e.at + e.text.len ..][0..tail];
            if (e.text.len > e.old) std.mem.copyBackwards(u8, dst, src) else std.mem.copyForwards(u8, dst, src);
            @memcpy(self.buf[e.at..][0..e.text.len], e.text);
            self.len = @intCast(new_len);
            return true;
        }
    };
}

fn isContinuation(byte: u8) bool {
    return (byte & 0xC0) == 0x80;
}

fn countContinuations(bytes: []const u8) usize {
    const V = @Vector(32, u8);
    var n: usize = 0;
    var i: usize = 0;
    while (i + 32 <= bytes.len) : (i += 32) {
        const v: V = bytes[i..][0..32].*;
        const hits: @Vector(32, bool) = (v & @as(V, @splat(0xC0))) == @as(V, @splat(0x80));
        n += @popCount(@as(u32, @bitCast(hits)));
    }
    for (bytes[i..]) |b| n += @intFromBool(isContinuation(b));
    return n;
}

fn isCharBoundary(bytes: []const u8, i: usize) bool {
    return i == 0 or i >= bytes.len or !isContinuation(bytes[i]);
}

/// The largest char boundary <= `i`, looking back at most 3 bytes (so that
/// invalid UTF-8 can never produce an empty split); falls back to `i`.
fn floorCharBoundary(bytes: []const u8, i: usize) usize {
    var j = i;
    while (j > 0 and i - j < 3 and !isCharBoundary(bytes, j)) j -= 1;
    return if (isCharBoundary(bytes, j)) j else i;
}

const replacement_char: u21 = 0xFFFD;

const Decoded = struct { cp: u21, len: u3 };

/// Decodes the codepoint at the start of `bytes`; invalid sequences decode
/// as U+FFFD covering one byte.
fn decodeFirst(bytes: []const u8) Decoded {
    const invalid: Decoded = .{ .cp = replacement_char, .len = 1 };
    const n = std.unicode.utf8ByteSequenceLength(bytes[0]) catch return invalid;
    if (n > bytes.len) return invalid;
    const cp = std.unicode.utf8Decode(bytes[0..n]) catch return invalid;
    return .{ .cp = cp, .len = n };
}

/// Decodes the codepoint ending at the end of `bytes`.
fn decodeLast(bytes: []const u8) Decoded {
    var i = bytes.len - 1;
    while (i > 0 and bytes.len - i < 4 and isContinuation(bytes[i])) i -= 1;
    const d = decodeFirst(bytes[i..]);
    if (i + d.len != bytes.len) return .{ .cp = replacement_char, .len = 1 };
    return d;
}

/// The point at the end of `bytes`, relative to its start.
fn pointAtEnd(bytes: []const u8) Point {
    const rows = std.mem.countScalar(u8, bytes, '\n');
    const column = if (std.mem.findScalarLast(u8, bytes, '\n')) |nl| bytes.len - nl - 1 else bytes.len;
    return .{ .row = @intCast(rows), .column = @intCast(column) };
}

/// Byte offset of `target` (relative to the start of `bytes`), with the
/// column clamped to the end of its line.
fn offsetOfPoint(bytes: []const u8, target: Point) usize {
    var line_start: usize = 0;
    for (0..target.row) |_| {
        const nl = std.mem.findScalarPos(u8, bytes, line_start, '\n') orelse return bytes.len;
        line_start = nl + 1;
    }
    const line_end = std.mem.findScalarPos(u8, bytes, line_start, '\n') orelse bytes.len;
    return line_start + @min(@as(usize, target.column), line_end - line_start);
}

fn divCeil(a: usize, b: usize) usize {
    return (a + b - 1) / b;
}

/// The rope used throughout the editor.
pub const Rope = RopeWith(6, CHUNK_MAX);

/// A rope over a SumTree with the given branching base and chunk size
/// (tests use small ones to get deep trees out of little text).
pub fn RopeWith(comptime tree_base: usize, comptime chunk_max: usize) type {
    // appendTree's rebalanced halves must fit in a chunk.
    if (chunk_max < 16 or chunk_max > std.math.maxInt(u16)) @compileError("chunk size out of range");
    return struct {
        const Self = @This();
        const chunk_min = chunk_max / 2;

        pub const TextChunk = ChunkWithMax(chunk_max);
        pub const Tree = sum_tree.SumTreeWithBase(TextChunk, tree_base);

        tree: Tree = .{},

        // -- lifetime -------------------------------------------------------

        /// An empty rope. Allocation-free.
        pub fn init() Self {
            return .{};
        }

        pub fn fromSlice(gpa: Allocator, text: []const u8) Allocator.Error!Self {
            var r: Self = .{};
            errdefer r.deinit(gpa);
            try r.push(gpa, text);
            return r;
        }

        pub fn deinit(self: *Self, gpa: Allocator) void {
            self.tree.deinit(gpa);
            self.* = undefined;
        }

        /// O(1) snapshot; shares all chunks with `self`.
        pub fn clone(self: *const Self) Self {
            return .{ .tree = self.tree.clone() };
        }

        // -- whole-rope queries ---------------------------------------------

        pub fn summary(self: *const Self) TextSummary {
            return self.tree.summary();
        }

        pub fn len(self: *const Self) usize {
            return self.tree.summary().len;
        }

        pub fn isEmpty(self: *const Self) bool {
            return self.tree.isEmpty();
        }

        pub fn maxPoint(self: *const Self) Point {
            return self.tree.summary().lines;
        }

        pub fn lineCount(self: *const Self) u32 {
            return self.maxPoint().row + 1;
        }

        /// O(n / CHUNK_MAX).
        pub fn chunkCount(self: *const Self) usize {
            return self.tree.itemCount();
        }

        /// A raw cursor over the chunk tree tracking dimension `D` (e.g.
        /// `usize`, `Point`, `OffsetPoint`, `TextSummary`).
        pub fn cursor(self: *const Self, comptime D: type) Tree.Cursor(D) {
            return self.tree.cursor(D);
        }

        // -- editing --------------------------------------------------------

        /// Appends `text`, topping up the last chunk first.
        pub fn push(self: *Self, gpa: Allocator, text: []const u8) Allocator.Error!void {
            var rest = text;
            if (rest.len == 0) return;
            if (self.tree.last()) |tail| {
                var n = @min(chunk_max - @as(usize, tail.len), rest.len);
                if (n < rest.len) n = floorCharBoundary(rest, n);
                if (n > 0) {
                    try self.tree.updateLast(gpa, rest[0..n], TextChunk.appendBytes);
                    rest = rest[n..];
                }
            }
            if (rest.len > 0) try self.pushNewChunks(gpa, rest);
        }

        /// Appends `text` as fresh, maximally full chunks. Big texts are built
        /// in batches, so the temporary chunk array stays small (~1 MB) instead
        /// of matching the text's size. A batch is a whole number of leaves,
        /// so bulk loads fill every leaf.
        fn pushNewChunks(self: *Self, gpa: Allocator, text: []const u8) Allocator.Error!void {
            var small: [8]TextChunk = undefined;
            const batch_max = Tree.capacity * @max(1, (1 << 20) / (Tree.capacity * @sizeOf(TextChunk)));
            // Every chunk but the last holds >= chunk_max - 3 bytes.
            const max_count = @min(divCeil(text.len, chunk_max - 3), batch_max);
            const on_heap = max_count > small.len;
            const buf: []TextChunk = if (on_heap) try gpa.alloc(TextChunk, max_count) else &small;
            defer if (on_heap) gpa.free(buf);

            var rest = text;
            while (rest.len > 0) {
                var count: usize = 0;
                while (rest.len > 0 and count < buf.len) {
                    var n: usize = @min(chunk_max, rest.len);
                    if (n < rest.len) n = floorCharBoundary(rest, n);
                    buf[count] = .init(rest[0..n]);
                    count += 1;
                    rest = rest[n..];
                }
                if (count <= 2) {
                    for (buf[0..count]) |c| try self.tree.push(gpa, c);
                } else {
                    try self.tree.append(gpa, try Tree.fromSlice(gpa, buf[0..count]));
                }
            }
        }

        /// Appends `other`, taking ownership of it.
        pub fn append(self: *Self, gpa: Allocator, other: Self) Allocator.Error!void {
            return self.appendTree(gpa, other.tree);
        }

        /// Appends a chunk tree (taking ownership) and repairs the seam so
        /// the min-fill invariant holds: if the two chunks meeting there fit
        /// in one they are merged, and if one of them is underfull they are
        /// rebalanced into two roughly equal halves.
        fn appendTree(self: *Self, gpa: Allocator, other: Tree) Allocator.Error!void {
            var o = other;
            const head = o.first() orelse return;
            const tail = self.tree.last() orelse {
                self.tree = o;
                return;
            };
            const combined_len = @as(usize, tail.len) + head.len;
            if (combined_len > chunk_max and tail.len >= chunk_min and head.len >= chunk_min) {
                return self.tree.append(gpa, o);
            }
            defer o.deinit(gpa);

            var buf: [2 * chunk_max]u8 = undefined;
            @memcpy(buf[0..tail.len], tail.text());
            @memcpy(buf[tail.len..combined_len], head.text());
            const combined = buf[0..combined_len];

            var c = o.cursor(sum_tree.Unit);
            c.next();
            var rest = try c.suffix(gpa);
            {
                errdefer rest.deinit(gpa);
                if (combined_len <= chunk_max) {
                    try self.tree.updateLast(gpa, combined, TextChunk.setBytes);
                } else {
                    const mid = floorCharBoundary(combined, combined_len / 2);
                    try self.tree.updateLast(gpa, combined[0..mid], TextChunk.setBytes);
                    try self.tree.push(gpa, .init(combined[mid..]));
                }
            }
            try self.tree.append(gpa, rest);
        }

        /// Replaces the bytes in [start, end) with `text`. Both ends must be
        /// char boundaries. Zed-style splice: the result is assembled from
        /// shared slices of the old tree, so this is O(log n + text.len),
        /// clones are unaffected, and on error `self` is left unchanged.
        /// Only the chunks the edit touches are rebuilt, and a small result
        /// is split in two halves, leaving room for the typing that usually
        /// follows to take the in-place path.
        pub fn replace(self: *Self, gpa: Allocator, start: usize, end: usize, text: []const u8) Allocator.Error!void {
            assert(start <= end and end <= self.len());
            if (try self.replaceInChunk(gpa, start, end, text)) return;
            // [lo, hi): the chunks holding `start` and `end`, and any between.
            var c = self.tree.cursor(usize);
            _ = c.seek(&start, .left);
            const lo = c.start();
            _ = c.seekForward(&end, .left);
            const hi = c.end();
            c.reset();
            var result: Self = .{ .tree = try c.slice(gpa, &lo, .right) };
            errdefer result.deinit(gpa);
            try result.appendTree(gpa, (try self.rechunk(gpa, lo, start, text, end, hi)).tree);
            _ = c.seekForward(&hi, .right);
            try result.appendTree(gpa, try c.suffix(gpa));
            self.tree.deinit(gpa);
            self.* = result;
        }

        /// [lo, start) ++ text ++ [end, hi) as a new rope; up to two chunks'
        /// worth comes out as one chunk or two balanced ones.
        fn rechunk(self: *const Self, gpa: Allocator, lo: usize, start: usize, text: []const u8, end: usize, hi: usize) Allocator.Error!Self {
            var m: Self = .{};
            errdefer m.deinit(gpa);
            if ((start - lo) + text.len + (hi - end) > 2 * chunk_max - 6) {
                var head = self.chunks(lo, start);
                while (head.next()) |s| try m.push(gpa, s);
                try m.push(gpa, text);
                var tail = self.chunks(end, hi);
                while (tail.next()) |s| try m.push(gpa, s);
                return m;
            }
            var buf: [2 * chunk_max]u8 = undefined;
            var n = self.copyRange(lo, start, &buf).len;
            @memcpy(buf[n..][0..text.len], text);
            n += text.len;
            n += self.copyRange(end, hi, buf[n..]).len;
            if (n == 0) return m;
            if (n <= chunk_max) {
                try m.tree.push(gpa, .init(buf[0..n]));
            } else {
                // Both halves hold chunk_min - 3 ..= chunk_max bytes.
                const mid = floorCharBoundary(buf[0..n], n / 2);
                try m.tree.push(gpa, .init(buf[0..mid]));
                try m.tree.push(gpa, .init(buf[mid..n]));
            }
            return m;
        }

        /// Fast path of `replace` (typing): an edit inside one chunk that
        /// keeps it within its size bounds is made in place, copying only
        /// the shared nodes on the chunk's root path. Returns false, having
        /// changed nothing, when the edit doesn't qualify.
        fn replaceInChunk(self: *Self, gpa: Allocator, start: usize, end: usize, text: []const u8) Allocator.Error!bool {
            var c = self.tree.cursor(usize);
            _ = c.seek(&start, .left);
            const chunk = c.item() orelse return false;
            const cs = c.start();
            if (end > cs + chunk.len) return false;
            const new_len = chunk.len - (end - start) + text.len;
            if (new_len == 0 or new_len > chunk_max) return false;
            // Only the last chunk may drop below the minimum fill.
            if (new_len < chunk.len and new_len + 3 < chunk_min and cs + chunk.len < self.len()) return false;
            const e: TextChunk.Splice = .{ .at = start - cs, .old = end - start, .text = text };
            const done = try self.tree.updateAt(gpa, usize, &start, .left, e, TextChunk.splice);
            assert(done);
            return true;
        }

        /// A new rope holding the bytes in [start, end). O(log n).
        pub fn slice(self: *const Self, gpa: Allocator, start: usize, end: usize) Allocator.Error!Self {
            assert(start <= end and end <= self.len());
            var result: Self = .{};
            errdefer result.deinit(gpa);
            var sc: SliceCursor = .init(self, start);
            try sc.sliceInto(gpa, &result, end);
            return result;
        }

        /// Walks forward through the chunks copying byte ranges into other
        /// ropes (Zed's `rope::Cursor`).
        const SliceCursor = struct {
            inner: Tree.Cursor(usize),
            offset: usize,

            fn init(rope: *const Self, offset: usize) SliceCursor {
                var c = rope.tree.cursor(usize);
                _ = c.seek(&offset, .right);
                return .{ .inner = c, .offset = offset };
            }

            fn seekForward(sc: *SliceCursor, to: usize) void {
                assert(to >= sc.offset);
                _ = sc.inner.seekForward(&to, .right);
                sc.offset = to;
            }

            /// Appends the text in [offset, to) to `dst` and advances to `to`.
            fn sliceInto(sc: *SliceCursor, gpa: Allocator, dst: *Self, to: usize) Allocator.Error!void {
                assert(to >= sc.offset);
                if (sc.inner.item()) |chunk| {
                    const cs = sc.inner.start();
                    try dst.push(gpa, chunk.text()[sc.offset - cs .. @min(to, sc.inner.end()) - cs]);
                }
                if (to > sc.inner.end()) {
                    sc.inner.next();
                    try dst.appendTree(gpa, try sc.inner.slice(gpa, &to, .right));
                    if (sc.inner.item()) |chunk| {
                        try dst.push(gpa, chunk.text()[0 .. to - sc.inner.start()]);
                    }
                }
                sc.offset = to;
            }
        };

        // -- coordinates ----------------------------------------------------

        /// Offsets past the end clamp to `maxPoint()`.
        pub fn offsetToPoint(self: *const Self, offset: usize) Point {
            const s = self.tree.summary();
            if (offset >= s.len) return s.lines;
            var c = self.tree.cursor(OffsetPoint);
            _ = c.seek(&offset, .left);
            const at = c.start();
            const chunk = c.item().?;
            return at.b.add(pointAtEnd(chunk.text()[0 .. offset - at.a]));
        }

        /// Columns past the end of their line clamp to the line end; points
        /// past the end of the rope clamp to `len()`. Does not snap to char
        /// boundaries (see `clipPoint`).
        pub fn pointToOffset(self: *const Self, point: Point) usize {
            const s = self.tree.summary();
            if (point.order(s.lines) != .lt) return s.len;
            var c = self.tree.cursor(OffsetPoint);
            _ = c.seek(&point, .left);
            const at = c.start();
            const chunk = c.item().?;
            return at.a + offsetOfPoint(chunk.text(), point.sub(at.b));
        }

        /// Offset of the first byte of `row` (`len()` for rows past the end).
        pub fn lineStart(self: *const Self, row: u32) usize {
            return self.pointToOffset(.{ .row = row, .column = 0 });
        }

        /// Offset of the `\n` ending `row`, or `len()` on the last row.
        pub fn lineEnd(self: *const Self, row: u32) usize {
            return self.pointToOffset(.{ .row = row, .column = std.math.maxInt(u32) });
        }

        /// Length of `row` in bytes, excluding the newline.
        pub fn lineLen(self: *const Self, row: u32) u32 {
            return @intCast(self.lineEnd(row) - self.lineStart(row));
        }

        /// Clamps `offset` to the rope and snaps it to a char boundary
        /// (backwards for `.left`, forwards for `.right`).
        pub fn clipOffset(self: *const Self, offset: usize, bias: Bias) usize {
            const total = self.len();
            if (offset >= total) return total;
            var c = self.tree.cursor(usize);
            _ = c.seek(&offset, .right);
            const at = c.start();
            const bytes = c.item().?.text();
            var i = offset - at;
            switch (bias) {
                .left => while (!isCharBoundary(bytes, i)) {
                    i -= 1;
                },
                .right => while (!isCharBoundary(bytes, i)) {
                    i += 1;
                },
            }
            return at + i;
        }

        /// Clamps `point` to the rope (row) and its line (column), then
        /// snaps the column to a char boundary according to `bias`.
        pub fn clipPoint(self: *const Self, point: Point, bias: Bias) Point {
            const max_point = self.maxPoint();
            if (point.order(max_point) != .lt) return max_point;
            var c = self.tree.cursor(OffsetPoint);
            _ = c.seek(&point, .left);
            const at = c.start();
            const bytes = c.item().?.text();
            var i = offsetOfPoint(bytes, point.sub(at.b));
            switch (bias) {
                .left => while (!isCharBoundary(bytes, i)) {
                    i -= 1;
                },
                .right => while (!isCharBoundary(bytes, i)) {
                    i += 1;
                },
            }
            return at.b.add(pointAtEnd(bytes[0..i]));
        }

        /// Summary of the bytes in [start, end). O(log n).
        pub fn textSummaryForRange(self: *const Self, start: usize, end: usize) TextSummary {
            assert(start <= end and end <= self.len());
            var c = self.tree.cursor(usize);
            _ = c.seek(&start, .right);
            var result: TextSummary = .{};
            if (c.item()) |chunk| {
                const cs = c.start();
                result.add(&TextSummary.fromText(chunk.text()[start - cs .. @min(end, c.end()) - cs]));
            }
            if (end > c.end()) {
                c.next();
                result.add(&c.summary(TextSummary, &end, .right));
                if (c.item()) |chunk| {
                    result.add(&TextSummary.fromText(chunk.text()[0 .. end - c.start()]));
                }
            }
            return result;
        }

        pub fn byteAt(self: *const Self, offset: usize) u8 {
            assert(offset < self.len());
            var c = self.tree.cursor(usize);
            _ = c.seek(&offset, .right);
            return c.item().?.text()[offset - c.start()];
        }

        /// Decodes the codepoint starting at `offset` (a char boundary).
        pub fn charAt(self: *const Self, offset: usize) u21 {
            assert(offset < self.len());
            var c = self.tree.cursor(usize);
            _ = c.seek(&offset, .right);
            return decodeFirst(c.item().?.text()[offset - c.start() ..]).cp;
        }

        // -- iteration ------------------------------------------------------
        //
        // Iterators borrow the rope's chunks: slices they return stay valid
        // as long as this rope (or a clone of it) is alive and unmodified.

        /// Yields the text in [start, end) as consecutive non-empty slices.
        pub fn chunks(self: *const Self, start: usize, end: usize) Chunks {
            assert(start <= end and end <= self.len());
            var c = self.tree.cursor(usize);
            _ = c.seek(&start, .right);
            return .{ .inner = c, .range_start = start, .range_end = end };
        }

        pub const Chunks = struct {
            inner: Tree.Cursor(usize),
            range_start: usize,
            range_end: usize,

            pub fn next(it: *Chunks) ?[]const u8 {
                const chunk = it.inner.item() orelse return null;
                const cs = it.inner.start();
                if (cs >= it.range_end) return null;
                const bytes = chunk.text();
                const lo = @max(it.range_start, cs) - cs;
                const hi = @min(it.range_end, cs + bytes.len) - cs;
                it.inner.next();
                if (lo >= hi) return null; // empty range
                return bytes[lo..hi];
            }

            /// Byte offset at which the next slice will start.
            pub fn offset(it: *const Chunks) usize {
                return @min(@max(it.range_start, it.inner.start()), it.range_end);
            }
        };

        /// Yields the text in [start, end) as slices, last slice first.
        pub fn reversedChunks(self: *const Self, start: usize, end: usize) ReversedChunks {
            assert(start <= end and end <= self.len());
            var c = self.tree.cursor(usize);
            _ = c.seek(&end, .left);
            return .{ .inner = c, .range_start = start, .range_end = end };
        }

        pub const ReversedChunks = struct {
            inner: Tree.Cursor(usize),
            range_start: usize,
            range_end: usize,

            pub fn next(it: *ReversedChunks) ?[]const u8 {
                const chunk = it.inner.item() orelse return null;
                const cs = it.inner.start();
                const bytes = chunk.text();
                const ce = cs + bytes.len;
                if (ce <= it.range_start) return null;
                const lo = @max(it.range_start, cs) - cs;
                const hi = @min(it.range_end, ce) - cs;
                it.inner.prev();
                if (lo >= hi) return null; // empty range
                return bytes[lo..hi];
            }
        };

        /// Codepoints from `start` (a char boundary) to the end.
        pub fn chars(self: *const Self, start: usize) Chars {
            return .{ .inner = self.chunks(start, self.len()), .current_offset = start, .offset = start };
        }

        pub const Chars = struct {
            inner: Chunks,
            current: []const u8 = &.{},
            /// Offset of `current[0]`.
            current_offset: usize,
            /// Byte offset of the codepoint most recently returned by `next`.
            offset: usize,

            pub fn next(it: *Chars) ?u21 {
                while (it.current.len == 0) it.current = it.inner.next() orelse return null;
                const d = decodeFirst(it.current);
                it.offset = it.current_offset;
                it.current = it.current[d.len..];
                it.current_offset += d.len;
                return d.cp;
            }
        };

        /// Codepoints before `end` (a char boundary), last first.
        pub fn reversedChars(self: *const Self, end: usize) ReversedChars {
            return .{ .inner = self.reversedChunks(0, end), .current_end = end, .offset = end };
        }

        pub const ReversedChars = struct {
            inner: ReversedChunks,
            current: []const u8 = &.{},
            /// Offset just past the end of `current`.
            current_end: usize,
            /// Byte offset of the codepoint most recently returned by `next`.
            offset: usize,

            pub fn next(it: *ReversedChars) ?u21 {
                while (it.current.len == 0) it.current = it.inner.next() orelse return null;
                const d = decodeLast(it.current);
                it.current = it.current[0 .. it.current.len - d.len];
                it.current_end -= d.len;
                it.offset = it.current_end;
                return d.cp;
            }
        };

        // -- copying out ----------------------------------------------------

        pub fn writeRange(self: *const Self, start: usize, end: usize, w: *std.Io.Writer) std.Io.Writer.Error!void {
            var it = self.chunks(start, end);
            while (it.next()) |s| try w.writeAll(s);
        }

        /// Copies [start, end) into `out` (which must be large enough) and
        /// returns the filled prefix.
        pub fn copyRange(self: *const Self, start: usize, end: usize, out: []u8) []u8 {
            assert(out.len >= end - start);
            var i: usize = 0;
            var it = self.chunks(start, end);
            while (it.next()) |s| {
                @memcpy(out[i..][0..s.len], s);
                i += s.len;
            }
            return out[0..i];
        }

        pub fn rangeToOwnedSlice(self: *const Self, gpa: Allocator, start: usize, end: usize) Allocator.Error![]u8 {
            const out = try gpa.alloc(u8, end - start);
            _ = self.copyRange(start, end, out);
            return out;
        }

        pub fn toOwnedSlice(self: *const Self, gpa: Allocator) Allocator.Error![]u8 {
            return self.rangeToOwnedSlice(gpa, 0, self.len());
        }

        pub fn eqlSlice(self: *const Self, text: []const u8) bool {
            if (text.len != self.len()) return false;
            var i: usize = 0;
            var it = self.chunks(0, text.len);
            while (it.next()) |s| {
                if (!std.mem.eql(u8, s, text[i..][0..s.len])) return false;
                i += s.len;
            }
            return true;
        }

        /// `{f}` format support: writes the whole text.
        pub fn format(self: Self, w: *std.Io.Writer) std.Io.Writer.Error!void {
            return self.writeRange(0, self.len(), w);
        }

        // -- search ---------------------------------------------------------

        /// Offset of the first occurrence of `needle` at or after `from`.
        /// Naive scan; matches may span chunk boundaries.
        pub fn findNext(self: *const Self, needle: []const u8, from: usize) ?usize {
            const total = self.len();
            if (from > total) return null;
            if (needle.len == 0) return from;
            if (needle.len > total - from) return null;
            var it = self.chunks(from, total);
            var pos = from;
            while (it.next()) |s| {
                var i: usize = 0;
                while (std.mem.findScalarPos(u8, s, i, needle[0])) |j| {
                    if (matchesAt(s[j..], it, needle)) return pos + j;
                    i = j + 1;
                }
                pos += s.len;
            }
            return null;
        }

        /// Whether `needle` starts at `head[0]`, continuing into the slices
        /// that `tail` (passed by value) would yield next.
        fn matchesAt(head: []const u8, tail: Chunks, needle: []const u8) bool {
            const n = @min(head.len, needle.len);
            if (!std.mem.eql(u8, head[0..n], needle[0..n])) return false;
            var rest = needle[n..];
            var it = tail;
            while (rest.len > 0) {
                const s = it.next() orelse return false;
                const k = @min(s.len, rest.len);
                if (!std.mem.eql(u8, s[0..k], rest[0..k])) return false;
                rest = rest[k..];
            }
            return true;
        }

        // -- testing --------------------------------------------------------

        /// Checks the tree and chunk invariants (assumes valid UTF-8 content).
        pub fn checkInvariants(self: *const Self) error{InvariantViolation}!void {
            try self.tree.checkInvariants();
            var it = self.tree.iter();
            var prev_len: ?usize = null;
            while (it.next()) |chunk| {
                const bytes = chunk.text();
                if (bytes.len == 0 or bytes.len > chunk_max) return error.InvariantViolation;
                if (!std.unicode.utf8ValidateSlice(bytes)) return error.InvariantViolation;
                // Only the last chunk may be underfull.
                if (prev_len) |p| if (p + 3 < chunk_min) return error.InvariantViolation;
                prev_len = bytes.len;
            }
        }
    };
}

// ---------------------------------------------------------------------------
// Tests

const testing = std.testing;

fn expectRope(r: anytype, expected: []const u8) !void {
    try r.checkInvariants();
    const got = try r.toOwnedSlice(testing.allocator);
    defer testing.allocator.free(got);
    try testing.expectEqualStrings(expected, got);
    try testing.expect(r.eqlSlice(expected));
    try testing.expectEqual(expected.len, r.len());
    try testing.expectEqual(TextSummary.fromText(expected), r.summary());
}

test "point arithmetic" {
    const p = Point.init(2, 5);
    try testing.expect(Point.eql(p.add(.init(0, 3)), .init(2, 8)));
    try testing.expect(Point.eql(p.add(.init(1, 3)), .init(3, 3)));
    try testing.expect(Point.eql(Point.init(3, 3).sub(p), .init(1, 3)));
    try testing.expect(Point.eql(Point.init(2, 9).sub(p), .init(0, 4)));
    try testing.expect(Point.lessThan(p, .init(2, 6)));
    try testing.expect(Point.lessThan(p, .init(3, 0)));
    try testing.expectEqual(Order.gt, Point.order(p, .init(1, 100)));
    try testing.expect(Point.eql(Point.min(p, .init(1, 9)), .init(1, 9)));
    try testing.expect(Point.eql(Point.max(p, .init(1, 9)), p));
}

test "text summary" {
    const s = TextSummary.fromText("héllo wörld 😀\n");
    try testing.expectEqual(@as(usize, 19), s.len);
    try testing.expectEqual(@as(usize, 14), s.chars);
    try testing.expect(s.lines.eql(.init(1, 0)));
    try testing.expectEqual(@as(u32, 13), s.first_line_chars);
    try testing.expectEqual(@as(u32, 0), s.last_line_chars);
    try testing.expectEqual(@as(u32, 0), s.longest_row);
    try testing.expectEqual(@as(u32, 13), s.longest_row_chars);

    const t = TextSummary.fromText("ab\ncdef\n\nxyz");
    try testing.expectEqual(@as(u32, 1), t.longest_row);
    try testing.expectEqual(@as(u32, 4), t.longest_row_chars);
    try testing.expectEqual(@as(u32, 2), t.first_line_chars);
    try testing.expectEqual(@as(u32, 3), t.last_line_chars);

    // add() agrees with fromText() at every split point.
    const text = "ab\ncdéf\n\nxyz😀😀\nq";
    for (0..text.len + 1) |i| {
        if (!isCharBoundary(text, i)) continue;
        var left = TextSummary.fromText(text[0..i]);
        left.add(&TextSummary.fromText(text[i..]));
        try testing.expectEqual(TextSummary.fromText(text), left);
    }
}

test "empty rope" {
    const gpa = testing.allocator;
    var r = Rope.init();
    defer r.deinit(gpa);
    try expectRope(&r, "");
    try testing.expect(r.isEmpty());
    try testing.expect(r.maxPoint().eql(.zero));
    try testing.expectEqual(@as(u32, 1), r.lineCount());
    try testing.expectEqual(@as(u32, 0), r.lineLen(0));
    try testing.expectEqual(@as(usize, 0), r.lineStart(0));
    try testing.expectEqual(@as(usize, 0), r.lineEnd(0));
    try testing.expect(r.offsetToPoint(0).eql(.zero));
    try testing.expectEqual(@as(usize, 0), r.pointToOffset(.init(3, 4)));
    try testing.expectEqual(@as(usize, 0), r.clipOffset(5, .right));
    try testing.expect(r.clipPoint(.init(1, 1), .left).eql(.zero));
    try testing.expectEqual(TextSummary.zero, r.textSummaryForRange(0, 0));
    var it = r.chunks(0, 0);
    try testing.expect(it.next() == null);
    var rit = r.reversedChunks(0, 0);
    try testing.expect(rit.next() == null);
    var ch = r.chars(0);
    try testing.expect(ch.next() == null);
    var rch = r.reversedChars(0);
    try testing.expect(rch.next() == null);
    try testing.expectEqual(@as(?usize, 0), r.findNext("", 0));
    try testing.expectEqual(@as(?usize, null), r.findNext("a", 0));

    var s = try r.slice(gpa, 0, 0);
    defer s.deinit(gpa);
    try expectRope(&s, "");
    try r.replace(gpa, 0, 0, "");
    try expectRope(&r, "");
    try r.append(gpa, Rope.init());
    try expectRope(&r, "");
}

test "multi-byte text" {
    const gpa = testing.allocator;
    const text = "héllo wörld 😀\n";
    var r = try Rope.fromSlice(gpa, text);
    defer r.deinit(gpa);
    try expectRope(&r, text);
    try testing.expect(r.maxPoint().eql(.init(1, 0)));
    try testing.expectEqual(@as(u32, 2), r.lineCount());
    try testing.expectEqual(@as(u32, 18), r.lineLen(0));
    try testing.expectEqual(@as(u32, 0), r.lineLen(1));

    try testing.expectEqual(@as(u8, 'h'), r.byteAt(0));
    try testing.expectEqual(@as(u21, 'é'), r.charAt(1));
    try testing.expectEqual(@as(u21, 'l'), r.charAt(3));
    try testing.expectEqual(@as(u21, 0x1F600), r.charAt(14));
    try testing.expectEqual(@as(u21, '\n'), r.charAt(18));

    try testing.expectEqual(@as(usize, 1), r.clipOffset(2, .left));
    try testing.expectEqual(@as(usize, 3), r.clipOffset(2, .right));
    try testing.expectEqual(@as(usize, 14), r.clipOffset(16, .left));
    try testing.expectEqual(@as(usize, 18), r.clipOffset(16, .right));
    try testing.expectEqual(@as(usize, 19), r.clipOffset(100, .left));
    try testing.expect(r.clipPoint(.init(0, 2), .left).eql(.init(0, 1)));
    try testing.expect(r.clipPoint(.init(0, 2), .right).eql(.init(0, 3)));
    try testing.expect(r.clipPoint(.init(0, 100), .left).eql(.init(0, 18)));
    try testing.expect(r.clipPoint(.init(7, 0), .left).eql(.init(1, 0)));

    try testing.expect(r.offsetToPoint(14).eql(.init(0, 14)));
    try testing.expect(r.offsetToPoint(19).eql(.init(1, 0)));
    try testing.expect(r.offsetToPoint(500).eql(.init(1, 0)));
    try testing.expectEqual(@as(usize, 14), r.pointToOffset(.init(0, 14)));
    try testing.expectEqual(@as(usize, 18), r.pointToOffset(.init(0, 999)));

    var ch = r.chars(8);
    try testing.expectEqual(@as(?u21, 'ö'), ch.next());
    try testing.expectEqual(@as(usize, 8), ch.offset);
    try testing.expectEqual(@as(?u21, 'r'), ch.next());
    try testing.expectEqual(@as(usize, 10), ch.offset);

    var rch = r.reversedChars(18);
    try testing.expectEqual(@as(?u21, 0x1F600), rch.next());
    try testing.expectEqual(@as(usize, 14), rch.offset);
    try testing.expectEqual(@as(?u21, ' '), rch.next());
    try testing.expectEqual(@as(usize, 13), rch.offset);

    const sub = r.textSummaryForRange(1, 18);
    try testing.expectEqual(TextSummary.fromText(text[1..18]), sub);
}

test "lines and trailing newline" {
    const gpa = testing.allocator;
    var r = try Rope.fromSlice(gpa, "abc\ndefg\n\nhi");
    defer r.deinit(gpa);
    try testing.expect(r.maxPoint().eql(.init(3, 2)));
    try testing.expectEqual(@as(u32, 4), r.lineCount());
    const starts = [_]usize{ 0, 4, 9, 10 };
    const lens = [_]u32{ 3, 4, 0, 2 };
    for (starts, lens, 0..) |s, l, row| {
        try testing.expectEqual(s, r.lineStart(@intCast(row)));
        try testing.expectEqual(l, r.lineLen(@intCast(row)));
        try testing.expectEqual(s + l, r.lineEnd(@intCast(row)));
    }
    try testing.expectEqual(@as(usize, 12), r.lineStart(10));
    try testing.expectEqual(@as(usize, 8), r.pointToOffset(.init(1, 100)));
    try testing.expect(r.offsetToPoint(9).eql(.init(2, 0)));
    try testing.expect(r.offsetToPoint(8).eql(.init(1, 4)));
    try testing.expectEqual(@as(u32, 1), r.summary().longest_row);

    var t = try Rope.fromSlice(gpa, "a\n");
    defer t.deinit(gpa);
    try testing.expect(t.maxPoint().eql(.init(1, 0)));
    try testing.expectEqual(@as(u32, 2), t.lineCount());
    try testing.expectEqual(@as(u32, 0), t.lineLen(1));
    try testing.expectEqual(@as(usize, 2), t.lineStart(1));
}

test "cursor dimensions" {
    const gpa = testing.allocator;
    const text = try sampleText(gpa, 2000, 9);
    defer gpa.free(text);
    var r = try RopeWith(2, 128).fromSlice(gpa, text);
    defer r.deinit(gpa);

    const row: u32 = r.maxPoint().row / 2;
    const row_start = r.lineStart(row);

    // Seek an offset+point cursor by point, then by offset.
    var c = r.cursor(OffsetPoint);
    _ = c.seek(&Point.init(row, 0), .right);
    try testing.expect(c.start().a <= row_start and row_start < c.end().a);
    try testing.expect(c.start().b.eql(r.offsetToPoint(c.start().a)));
    _ = c.seekForward(&@as(usize, row_start + 1), .left);
    try testing.expect(c.start().a <= row_start + 1 and row_start + 1 <= c.end().a);

    // A TextSummary cursor seeks by offset or point and tracks everything.
    var ts = r.cursor(TextSummary);
    _ = ts.seek(&@as(usize, 1000), .right);
    try testing.expectEqual(TextSummary.fromText(text[0..ts.start().len]), ts.start());
    _ = ts.seek(&Point.init(row, 0), .left);
    try testing.expectEqual(TextSummary.fromText(text[0..ts.start().len]), ts.start());

    // A Point cursor.
    var pc = r.cursor(Point);
    _ = pc.seek(&Point.init(row, 0), .right);
    try testing.expect(!Point.lessThan(Point.init(row, 0), pc.start()));
    try testing.expect(Point.lessThan(Point.init(row, 0), pc.end()));
}

fn sampleText(gpa: Allocator, pieces: usize, seed: u64) ![]u8 {
    const alphabet = [_][]const u8{ "a", "bc", "def", " ", "\n", "é", "ö", "€", "😀", "hello", "\n\n", "wörld" };
    var prng = std.Random.DefaultPrng.init(seed);
    const rand = prng.random();
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    for (0..pieces) |_| try out.appendSlice(gpa, alphabet[rand.uintLessThan(usize, alphabet.len)]);
    return out.toOwnedSlice(gpa);
}

test "chunks, reversed chunks and char iteration across chunk boundaries" {
    const gpa = testing.allocator;
    const text = try sampleText(gpa, 6000, 7);
    defer gpa.free(text);
    var r = try RopeWith(2, 128).fromSlice(gpa, text);
    defer r.deinit(gpa);
    try expectRope(&r, text);
    try testing.expect(r.chunkCount() > 50);
    try testing.expect(r.tree.height() >= 3);

    var prng = std.Random.DefaultPrng.init(1);
    const rand = prng.random();
    var buf: std.ArrayList(u8) = .empty;
    defer buf.deinit(gpa);
    for (0..200) |_| {
        const a = r.clipOffset(rand.uintAtMost(usize, text.len), .left);
        const b = r.clipOffset(rand.intRangeAtMost(usize, a, text.len), .right);

        buf.clearRetainingCapacity();
        var it = r.chunks(a, b);
        try testing.expectEqual(a, it.offset());
        while (it.next()) |s| {
            try testing.expect(s.len > 0);
            try buf.appendSlice(gpa, s);
        }
        try testing.expectEqualStrings(text[a..b], buf.items);

        var rit = r.reversedChunks(a, b);
        try collectReversed(gpa, &rit, &buf);
        try testing.expectEqualStrings(text[a..b], buf.items);

        // chars() from a, compared to std's decoder.
        var ch = r.chars(a);
        var view = std.unicode.Utf8View.initUnchecked(text[a..]).iterator();
        var expected_offset = a;
        for (0..50) |_| {
            const expected = view.nextCodepoint();
            const got = ch.next();
            try testing.expectEqual(expected, got);
            if (expected == null) break;
            try testing.expectEqual(expected_offset, ch.offset);
            try testing.expectEqual(got.?, r.charAt(expected_offset));
            expected_offset += std.unicode.utf8CodepointSequenceLength(got.?) catch unreachable;
        }

        // reversedChars() from b, compared to decoding backwards.
        var rch = r.reversedChars(b);
        var end_off = b;
        for (0..50) |_| {
            const got = rch.next();
            if (end_off == 0) {
                try testing.expect(got == null);
                break;
            }
            var start_off = end_off - 1;
            while (isContinuation(text[start_off])) start_off -= 1;
            try testing.expectEqual(try std.unicode.utf8Decode(text[start_off..end_off]), got.?);
            try testing.expectEqual(start_off, rch.offset);
            end_off = start_off;
        }
    }
}

fn replaceSliceAppendTest(comptime R: type) !void {
    const gpa = testing.allocator;
    var r = try R.fromSlice(gpa, "Hello, world!\nSecond line\n");
    defer r.deinit(gpa);
    var before = r.clone();
    defer before.deinit(gpa);

    try r.replace(gpa, 7, 12, "zig");
    try expectRope(&r, "Hello, zig!\nSecond line\n");
    try r.replace(gpa, 0, 0, "> ");
    try expectRope(&r, "> Hello, zig!\nSecond line\n");
    try r.replace(gpa, r.len(), r.len(), "end");
    try expectRope(&r, "> Hello, zig!\nSecond line\nend");
    try r.replace(gpa, 2, r.len() - 3, "");
    try expectRope(&r, "> end");
    try expectRope(&before, "Hello, world!\nSecond line\n");

    var s = try before.slice(gpa, 7, 19);
    defer s.deinit(gpa);
    try expectRope(&s, "world!\nSecon");

    try r.push(gpa, " ünïcødé");
    try expectRope(&r, "> end ünïcødé");
    try r.append(gpa, try R.fromSlice(gpa, "\nappended"));
    try expectRope(&r, "> end ünïcødé\nappended");
    var snapshot = r.clone();
    defer snapshot.deinit(gpa);
    try r.append(gpa, r.clone());
    try expectRope(&r, "> end ünïcødé\nappended> end ünïcødé\nappended");
    try expectRope(&snapshot, "> end ünïcødé\nappended");

    // Large pushes and appends keep chunks full.
    const big = try sampleText(gpa, 2000, 3);
    defer gpa.free(big);
    var t = R.init();
    defer t.deinit(gpa);
    var model: std.ArrayList(u8) = .empty;
    defer model.deinit(gpa);
    var i: usize = 0;
    while (i < big.len) {
        var j = @min(big.len, i + 1 + (i * 7919) % 300);
        while (!isCharBoundary(big, j)) j += 1;
        if ((i / 10) % 2 == 0) {
            try t.push(gpa, big[i..j]);
        } else {
            try t.append(gpa, try R.fromSlice(gpa, big[i..j]));
        }
        try model.appendSlice(gpa, big[i..j]);
        try expectRope(&t, model.items);
        i = j;
    }
}

test "replace, slice, append, push, and clone isolation" {
    try replaceSliceAppendTest(Rope);
    try replaceSliceAppendTest(RopeWith(2, 16));
}

/// Zig has no array-repeat operator; this is `s` repeated `times` times.
fn repeat(comptime s: []const u8, comptime times: usize) []const u8 {
    return comptime blk: {
        var out: []const u8 = "";
        for (0..times) |_| out = out ++ s;
        break :blk out;
    };
}

/// The plain byte loop `TextSummary.fromText` replaced.
fn fromTextReference(text: []const u8) TextSummary {
    var s: TextSummary = .{ .len = text.len };
    for (text) |byte| {
        if (byte == '\n') {
            s.chars += 1;
            s.lines.row += 1;
            s.lines.column = 0;
            s.last_line_chars = 0;
        } else {
            s.lines.column += 1;
            if (!isContinuation(byte)) {
                s.chars += 1;
                s.last_line_chars += 1;
            }
        }
        if (s.lines.row == 0) s.first_line_chars = s.last_line_chars;
        if (s.last_line_chars > s.longest_row_chars) {
            s.longest_row = s.lines.row;
            s.longest_row_chars = s.last_line_chars;
        }
    }
    return s;
}

test "fromText matches the byte-by-byte summary" {
    const gpa = testing.allocator;
    const text = try sampleText(gpa, 3000, 21);
    defer gpa.free(text);
    var prng = std.Random.DefaultPrng.init(4);
    const rand = prng.random();
    for (0..500) |_| {
        const a = rand.uintAtMost(usize, text.len);
        const b = rand.intRangeAtMost(usize, a, @min(text.len, a + 200));
        try testing.expectEqual(fromTextReference(text[a..b]), TextSummary.fromText(text[a..b]));
    }
    for ([_][]const u8{ "", "\n", "\n\n", "abc", "€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€\nx" }) |t| {
        try testing.expectEqual(fromTextReference(t), TextSummary.fromText(t));
    }
}

test "in-place edits: typing fills a chunk, then it splits" {
    const gpa = testing.allocator;
    const R = RopeWith(2, 32);
    var model: std.ArrayList(u8) = .empty;
    defer model.deinit(gpa);
    try model.appendSlice(gpa, repeat("line one\nline two ü\n", 20));
    var r = try R.fromSlice(gpa, model.items);
    defer r.deinit(gpa);
    var caret: usize = 37;
    for (0..300) |i| {
        // Keep a snapshot around half the time: in-place edits must copy.
        var snapshot: ?R = if (i % 2 == 0) r.clone() else null;
        const before = try gpa.dupe(u8, model.items);
        defer gpa.free(before);
        const text: []const u8 = if (i % 7 == 0) "é" else if (i % 11 == 0) "\n" else "x";
        try r.replace(gpa, caret, caret, text);
        try model.insertSlice(gpa, caret, text);
        caret += text.len;
        if (i % 5 == 4) {
            // Backspace over the last insert.
            caret -= text.len;
            try r.replace(gpa, caret, caret + text.len, "");
            try model.replaceRange(gpa, caret, text.len, "");
        }
        try expectRope(&r, model.items);
        if (snapshot) |*s| {
            try expectRope(s, before);
            s.deinit(gpa);
        }
    }
    // Deleting down to (and past) the minimum fill, and emptying the rope.
    while (r.len() > 0) {
        const n = @min(r.len(), 5);
        const at = r.clipOffset(r.len() / 3, .left);
        const to = r.clipOffset(at + n, .right);
        try r.replace(gpa, at, to, "");
        try model.replaceRange(gpa, at, to - at, "");
        try expectRope(&r, model.items);
    }
}

test "writeRange, format, copyRange and findNext" {
    const gpa = testing.allocator;
    const text = try sampleText(gpa, 1500, 11);
    defer gpa.free(text);
    var r = try RopeWith(3, 128).fromSlice(gpa, text);
    defer r.deinit(gpa);

    var aw: std.Io.Writer.Allocating = .init(gpa);
    defer aw.deinit();
    try r.writeRange(10, 300, &aw.writer);
    try testing.expectEqualStrings(text[10..300], aw.written());

    aw.clearRetainingCapacity();
    try aw.writer.print("{f}", .{r});
    try testing.expectEqualStrings(text, aw.written());

    var out: [500]u8 = undefined;
    try testing.expectEqualStrings(text[100..400], r.copyRange(100, 400, &out));

    // Needles that straddle chunk boundaries.
    var prng = std.Random.DefaultPrng.init(5);
    const rand = prng.random();
    for (0..300) |_| {
        const a = rand.uintLessThan(usize, text.len - 20);
        const needle = text[a .. a + 1 + rand.uintLessThan(usize, 19)];
        const from = rand.uintAtMost(usize, a + 5);
        try testing.expectEqual(std.mem.findPos(u8, text, from, needle), r.findNext(needle, from));
    }
    try testing.expectEqual(@as(?usize, null), r.findNext("not in there", 0));
}

fn modelPoint(text: []const u8, offset: usize) Point {
    return pointAtEnd(text[0..offset]);
}

fn modelClipLeft(text: []const u8, offset: usize) usize {
    var i = @min(offset, text.len);
    while (!isCharBoundary(text, i)) i -= 1;
    return i;
}

fn modelClipRight(text: []const u8, offset: usize) usize {
    var i = @min(offset, text.len);
    while (!isCharBoundary(text, i)) i += 1;
    return i;
}

fn randomText(rand: std.Random, gpa: Allocator, out: *std.ArrayList(u8), max_pieces: usize) !void {
    const alphabet = [_][]const u8{ "a", "b", "c", "xyz", " ", "\n", "\n\n", "é", "ö", "€", "😀", "hello world" };
    out.clearRetainingCapacity();
    for (0..rand.uintAtMost(usize, max_pieces)) |_| try out.appendSlice(gpa, alphabet[rand.uintLessThan(usize, alphabet.len)]);
}

/// Concatenates the slices of a reversed iterator back into forward order.
fn collectReversed(gpa: Allocator, it: anytype, out: *std.ArrayList(u8)) !void {
    var parts: std.ArrayList([]const u8) = .empty;
    defer parts.deinit(gpa);
    while (it.next()) |s| {
        try testing.expect(s.len > 0);
        try parts.append(gpa, s);
    }
    out.clearRetainingCapacity();
    var i = parts.items.len;
    while (i > 0) {
        i -= 1;
        try out.appendSlice(gpa, parts.items[i]);
    }
}

fn randomizedRopeTest(comptime R: type, seed: u64, iterations: usize, initial_len: usize) !void {
    const gpa = testing.allocator;
    var prng = std.Random.DefaultPrng.init(seed);
    const rand = prng.random();

    var model: std.ArrayList(u8) = .empty;
    defer model.deinit(gpa);
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    while (model.items.len < initial_len) {
        try randomText(rand, gpa, &text, 50);
        try model.appendSlice(gpa, text.items);
    }
    var rope = try R.fromSlice(gpa, model.items);
    defer rope.deinit(gpa);
    var scratch: std.ArrayList(u8) = .empty;
    defer scratch.deinit(gpa);

    const Snapshot = struct { rope: R, text: []u8 };
    var snapshots: std.ArrayList(Snapshot) = .empty;
    defer {
        for (snapshots.items) |*s| {
            s.rope.deinit(gpa);
            gpa.free(s.text);
        }
        snapshots.deinit(gpa);
    }

    for (0..iterations) |iter| {
        // Random replace at char boundaries.
        const n = model.items.len;
        const start = modelClipLeft(model.items, rand.uintAtMost(usize, n));
        const max_delete: usize = if (n > initial_len + 8000) 2000 else if (rand.uintLessThan(u32, 10) == 0) 1000 else 40;
        const end = modelClipRight(model.items, start + rand.uintAtMost(usize, @min(max_delete, n - start)));
        try randomText(rand, gpa, &text, if (rand.uintLessThan(u32, 10) == 0) 300 else 12);

        if (rand.uintLessThan(u32, 8) == 0 and snapshots.items.len < 20) {
            try snapshots.append(gpa, .{ .rope = rope.clone(), .text = try gpa.dupe(u8, model.items) });
        }

        try rope.replace(gpa, start, end, text.items);
        try model.replaceRange(gpa, start, end - start, text.items);
        const m = model.items;

        try testing.expectEqual(m.len, rope.len());
        try testing.expect(rope.maxPoint().eql(pointAtEnd(m)));
        // Whole-document checks are O(n); thin them out for big documents
        // (local checks below still run every iteration).
        if (m.len < 8000 or iter % 8 == 0) {
            try rope.checkInvariants();
            try testing.expect(rope.eqlSlice(m));
            try testing.expectEqual(TextSummary.fromText(m), rope.summary());
        }

        for (0..5) |_| {
            // offset <-> point round trips.
            const off = modelClipLeft(m, rand.uintAtMost(usize, m.len));
            const p = rope.offsetToPoint(off);
            try testing.expect(p.eql(modelPoint(m, off)));
            try testing.expectEqual(off, rope.pointToOffset(p));

            // Clipping arbitrary offsets and points.
            const raw = rand.uintAtMost(usize, m.len + 2);
            try testing.expectEqual(modelClipLeft(m, raw), rope.clipOffset(raw, .left));
            try testing.expectEqual(modelClipRight(m, raw), rope.clipOffset(raw, .right));
            const max_point = pointAtEnd(m);
            const rp: Point = .init(rand.uintAtMost(u32, max_point.row + 1), rand.uintAtMost(u32, 60));
            const expected_left, const expected_right = blk: {
                if (rp.order(max_point) != .lt) break :blk .{ max_point, max_point };
                const clamped = offsetOfPoint(m, rp);
                break :blk .{ modelPoint(m, modelClipLeft(m, clamped)), modelPoint(m, modelClipRight(m, clamped)) };
            };
            try testing.expect(rope.clipPoint(rp, .left).eql(expected_left));
            try testing.expect(rope.clipPoint(rp, .right).eql(expected_right));

            // Line queries.
            const row = rand.uintAtMost(u32, max_point.row);
            const ls = offsetOfPoint(m, .init(row, 0));
            const le = offsetOfPoint(m, .init(row, std.math.maxInt(u32)));
            try testing.expectEqual(ls, rope.lineStart(row));
            try testing.expectEqual(le, rope.lineEnd(row));
            try testing.expectEqual(@as(u32, @intCast(le - ls)), rope.lineLen(row));

            // Range summaries and slices.
            const a = modelClipLeft(m, rand.uintAtMost(usize, m.len));
            const span = if (rand.uintLessThan(u32, 5) == 0) m.len else 3000;
            const b = modelClipRight(m, rand.intRangeAtMost(usize, a, @min(m.len, a + span)));
            try testing.expectEqual(TextSummary.fromText(m[a..b]), rope.textSummaryForRange(a, b));
            if (iter % 8 == 0) {
                var sl = try rope.slice(gpa, a, b);
                defer sl.deinit(gpa);
                try expectRope(&sl, m[a..b]);
            }

            // Chunks in both directions.
            scratch.clearRetainingCapacity();
            var it = rope.chunks(a, b);
            while (it.next()) |s| try scratch.appendSlice(gpa, s);
            try testing.expectEqualStrings(m[a..b], scratch.items);
            var rit = rope.reversedChunks(a, b);
            try collectReversed(gpa, &rit, &scratch);
            try testing.expectEqualStrings(m[a..b], scratch.items);

            // chars() forward from a and reversedChars() back from b agree
            // with each other over [a, b).
            var fwd: [64]u21 = undefined;
            var fwd_off: [64]usize = undefined;
            var count: usize = 0;
            var ch = rope.chars(a);
            while (count < fwd.len) {
                const c = ch.next() orelse break;
                if (ch.offset >= b) break;
                fwd[count] = c;
                fwd_off[count] = ch.offset;
                count += 1;
            }
            if (count < fwd.len) {
                var rch = rope.reversedChars(b);
                var k = count;
                while (k > 0) {
                    k -= 1;
                    try testing.expectEqual(fwd[k], rch.next().?);
                    try testing.expectEqual(fwd_off[k], rch.offset);
                }
                if (a > 0) {
                    _ = rch.next().?;
                    try testing.expect(rch.offset < a);
                } else {
                    try testing.expect(rch.next() == null);
                }
            }

            if (m.len > 0) {
                const bo = rand.uintLessThan(usize, m.len);
                try testing.expectEqual(m[bo], rope.byteAt(bo));
            }
        }

        if (iter % 16 == 0 and m.len > 4) {
            const a = rand.uintLessThan(usize, m.len - 4);
            const needle = m[a .. a + 1 + rand.uintLessThan(usize, 4)];
            const from = rand.uintAtMost(usize, a);
            try testing.expectEqual(std.mem.findPos(u8, m, from, needle), rope.findNext(needle, from));
        }
    }

    for (snapshots.items) |*s| try expectRope(&s.rope, s.text);
}

test "randomized replaces against ArrayList model (base 6)" {
    try randomizedRopeTest(Rope, 1, 1200, 0);
    try randomizedRopeTest(Rope, 2, 1200, 0);
    try randomizedRopeTest(Rope, 3, 250, 40_000);
}

test "randomized replaces against ArrayList model (base 2, small chunks)" {
    try randomizedRopeTest(RopeWith(2, 128), 10, 1200, 0);
    try randomizedRopeTest(RopeWith(2, 128), 11, 400, 15_000);
    try randomizedRopeTest(RopeWith(2, 16), 12, 600, 3_000);
}

fn RopeAllocationFailureScenario(comptime R: type) type {
    return struct {
        fn run(gpa: Allocator) !void {
            const text = repeat("lorem ipsum dolor sit amet, ünïcødé 😀 consectetur\nadipiscing elit\n", 12);
            var r = try R.fromSlice(gpa, text);
            defer r.deinit(gpa);
            var snapshot = r.clone();
            defer snapshot.deinit(gpa);
            try r.replace(gpa, 100, 400, repeat("replacement text with ünïcødé\n", 7));
            try r.replace(gpa, 3, 3, "x");
            var s = try r.slice(gpa, 50, 700);
            defer s.deinit(gpa);
            try r.append(gpa, try snapshot.slice(gpa, 10, 300));
            try r.push(gpa, repeat("tail ", 40));
            try testing.expect(snapshot.eqlSlice(text));
        }
    };
}

test "no leaks when allocations fail" {
    try testing.checkAllAllocationFailures(testing.allocator, RopeAllocationFailureScenario(RopeWith(2, 128)).run, .{});
    try testing.checkAllAllocationFailures(testing.allocator, RopeAllocationFailureScenario(Rope).run, .{});
}
