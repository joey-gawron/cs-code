//! Positions on very long lines (minified files, one-line data dumps)
//! without laying the whole line out.
//!
//! A shaped `LineLayout` keeps an x for every byte and a glyph for every
//! char, and HarfBuzz needs ~40 bytes per char while shaping: gigabytes for
//! a 50 MB line. A `LineIndex` instead keeps a checkpoint (byte, display
//! column, x) every `block_bytes` bytes and computes positions inside a
//! block on demand from per-character advances. Those are the advances
//! HarfBuzz gives each char, so positions match the shaped glyphs exactly
//! unless the font kerns (monospace code fonts don't). Only the visible part
//! of such a line is ever shaped, when it's drawn.
//!
//! x values are integers in HarfBuzz units (1/64 device px), so sums over
//! millions of chars stay exact; `Advances.toPx` converts to logical px
//! (as f64: an f32 is off by 32 px at the end of a 50 MB line).

const std = @import("std");

const Allocator = std.mem.Allocator;

/// Bytes between checkpoints.
pub const block_bytes = 2048;

/// How wide each char is, in 1/64 device px.
pub const Advances = struct {
    /// Advance of each ASCII char ('\t' is unused: tabs advance to the next
    /// tab stop in spaces); -1 asks `other` (control chars, which may need a
    /// fallback font).
    ascii: [128]i32,
    /// Advance of a non-ASCII codepoint (0 for combining marks).
    other: *const fn (ctx: ?*anyopaque, font: u32, cp: u21) i32,
    ctx: ?*anyopaque = null,
    font: u32 = 0,
    tab_size: u32 = 4,
    /// HarfBuzz units per logical px (64 × the display scale).
    units_per_px: f64 = 64,

    pub fn toPx(a: *const Advances, x: i64) f64 {
        return @as(f64, @floatFromInt(x)) / a.units_per_px;
    }

    pub fn fromPx(a: *const Advances, px: f64) i64 {
        return @intFromFloat(@round(std.math.clamp(px * a.units_per_px, -9.0e15, 9.0e15)));
    }
};

/// Reads line bytes [start, end) (line-relative) into `out` and returns the
/// filled slice (`out.len >= end - start`).
pub const Source = struct {
    ctx: *const anyopaque,
    read: *const fn (ctx: *const anyopaque, start: usize, end: usize, out: []u8) []const u8,
    /// Line length in bytes (without the newline).
    len: usize,
};

/// Where a char boundary is.
pub const Pos = struct {
    /// Line-relative byte offset.
    byte: usize = 0,
    /// Display column: chars, with tabs expanded to the next tab stop.
    col: u64 = 0,
    /// 1/64 device px from the line start.
    x: i64 = 0,
};

/// Scans the chars of `bytes` (which start at `pos.byte`) that begin before
/// line byte `limit` (`.whole`: that end at or before it), moving `pos` past
/// them. A char cut off at the end of `bytes` is left for the next call unless
/// `line_end` (the line ends there). Returns the bytes consumed and the
/// largest line offset of a tab seen.
fn scan(adv: *const Advances, pos: *Pos, bytes: []const u8, limit: usize, line_end: bool, mode: enum { started, whole }) struct { usize, ?usize } {
    const base = pos.byte;
    const stop = @min(bytes.len, limit -| base);
    const tab: u64 = @max(1, adv.tab_size);
    const space: i64 = adv.ascii[' '];
    var x = pos.x;
    var col = pos.col;
    var last_tab: ?usize = null;
    var i: usize = 0;
    while (i < stop) {
        const b = bytes[i];
        if (b < 0x80) {
            if (b == '\t') {
                const w = tab - col % tab;
                col += w;
                x += @as(i64, @intCast(w)) * space;
                last_tab = base + i;
            } else {
                const w = adv.ascii[b];
                x += if (w >= 0) w else adv.other(adv.ctx, adv.font, b);
                col += 1;
            }
            i += 1;
            continue;
        }
        var n: usize = std.unicode.utf8ByteSequenceLength(b) catch 1;
        if (i + n > bytes.len) {
            if (!line_end) break;
            n = bytes.len - i;
        }
        if (mode == .whole and base + i + n > limit) break;
        const cp: u21 = std.unicode.utf8Decode(bytes[i..][0..n]) catch 0xFFFD;
        x += adv.other(adv.ctx, adv.font, cp);
        col += 1;
        i += n;
    }
    pos.* = .{ .byte = base + i, .col = col, .x = x };
    return .{ i, last_tab };
}

pub const LineIndex = struct {
    /// Checkpoints; cps[0] is the line start, bytes strictly increase and
    /// are all char boundaries. Blocks are [cps[i].byte, cps[i + 1].byte).
    cps: std.ArrayList(Pos) = .empty,
    /// The line end.
    end: Pos = .{},
    /// No tab is after this byte (null: the line has none). May be larger
    /// than the real last tab: it only decides when edits re-scan the tail.
    last_tab: ?usize = null,

    pub fn deinit(self: *LineIndex, gpa: Allocator) void {
        self.cps.deinit(gpa);
    }

    /// Width of the line in 1/64 device px.
    pub fn width(self: *const LineIndex) i64 {
        return self.end.x;
    }

    /// Indexes the whole line.
    pub fn build(self: *LineIndex, gpa: Allocator, adv: *const Advances, src: Source) Allocator.Error!void {
        self.cps.clearRetainingCapacity();
        try self.cps.append(gpa, .{});
        self.last_tab = null;
        self.end = try self.scanFrom(gpa, adv, src, .{}, src.len);
    }

    /// Scans from `from` to line byte `to` (or the first boundary after it),
    /// appending a checkpoint every `block_bytes`. Returns the position reached.
    fn scanFrom(self: *LineIndex, gpa: Allocator, adv: *const Advances, src: Source, from: Pos, to: usize) Allocator.Error!Pos {
        var buf: [64 * 1024]u8 = undefined;
        var pos = from;
        var block_start = from.byte;
        while (pos.byte < to) {
            // A char can run up to 3 bytes past `to`.
            const read_end = @min(src.len, to + 3, pos.byte + buf.len);
            const bytes = src.read(src.ctx, pos.byte, read_end, &buf);
            const line_end = read_end == src.len;
            var off: usize = 0;
            while (pos.byte < to) {
                const target = @min(to, block_start + block_bytes);
                const used, const tab = scan(adv, &pos, bytes[off..], target, line_end, .started);
                if (tab) |t| self.last_tab = @max(self.last_tab orelse 0, t);
                off += used;
                if (pos.byte < target) break; // needs the next piece
                if (pos.byte < to and pos.byte < src.len) try self.cps.append(gpa, pos);
                block_start = pos.byte;
            }
            if (off == 0) break; // can't make progress (truncated source)
        }
        return pos;
    }

    /// Updates the index after line bytes [a, old_b) were replaced by
    /// [a, new_b) (`src` reads the new line). Re-scans the edited block and
    /// shifts the checkpoints after it, unless a tab after the edit lands on a
    /// different tab stop (then the rest of the line is re-scanned).
    pub fn update(self: *LineIndex, gpa: Allocator, adv: *const Advances, src: Source, a: usize, old_b: usize, new_b: usize) Allocator.Error!void {
        const cps = self.cps.items;
        if (cps.len == 0 or a > self.end.byte) return self.build(gpa, adv, src);
        const delta: i64 = @as(i64, @intCast(new_b)) - @as(i64, @intCast(old_b));
        // Checkpoints at or before `a` don't change.
        const first = self.checkpointAtByte(a);
        // The first one after the replaced bytes, and everything after it, shift.
        var next_cp = first + 1;
        while (next_cp < cps.len and cps[next_cp].byte < old_b) next_cp += 1;

        const old_last_tab = self.last_tab;
        self.last_tab = if (old_last_tab) |t| (if (t < a) t else if (t >= old_b) shiftBy(t, delta) else a) else null;

        if (next_cp == cps.len) {
            self.cps.shrinkRetainingCapacity(first + 1);
            self.end = try self.scanFrom(gpa, adv, src, cps[first], src.len);
            return;
        }
        const old_next = cps[next_cp];
        const tail = try gpa.dupe(Pos, cps[next_cp..]);
        defer gpa.free(tail);
        self.cps.shrinkRetainingCapacity(first + 1);
        const target = shiftBy(old_next.byte, delta);
        const reached = try self.scanFrom(gpa, adv, src, self.cps.items[first], target);
        if (reached.byte != target) {
            // The edit didn't end on a char boundary: index the rest afresh.
            self.end = try self.scanFrom(gpa, adv, src, reached, src.len);
            return;
        }
        const dcol: i64 = @as(i64, @intCast(reached.col)) - @as(i64, @intCast(old_next.col));
        const dx = reached.x - old_next.x;
        const tabs_after = if (old_last_tab) |t| t >= old_next.byte else false;
        if (tabs_after and @mod(dcol, @as(i64, @max(1, adv.tab_size))) != 0) {
            try self.cps.append(gpa, reached);
            self.end = try self.scanFrom(gpa, adv, src, reached, src.len);
            return;
        }
        try self.cps.ensureUnusedCapacity(gpa, tail.len);
        for (tail) |p| self.cps.appendAssumeCapacity(shiftPos(p, delta, dcol, dx));
        self.end = shiftPos(self.end, delta, dcol, dx);
    }

    fn shiftBy(v: usize, d: i64) usize {
        return @intCast(@as(i64, @intCast(v)) + d);
    }

    fn shiftPos(p: Pos, d: i64, dcol: i64, dx: i64) Pos {
        return .{ .byte = shiftBy(p.byte, d), .col = @intCast(@as(i64, @intCast(p.col)) + dcol), .x = p.x + dx };
    }

    /// Index of the last checkpoint at or before line byte `byte`.
    pub fn checkpointAtByte(self: *const LineIndex, byte: usize) usize {
        const cps = self.cps.items;
        var lo: usize = 0;
        var hi: usize = cps.len - 1;
        while (lo < hi) {
            const mid = (lo + hi + 1) / 2;
            if (cps[mid].byte <= byte) lo = mid else hi = mid - 1;
        }
        return lo;
    }

    /// Index of the last checkpoint at or before `x`.
    pub fn checkpointAtX(self: *const LineIndex, x: i64) usize {
        const cps = self.cps.items;
        var lo: usize = 0;
        var hi: usize = cps.len - 1;
        while (lo < hi) {
            const mid = (lo + hi + 1) / 2;
            if (cps[mid].x <= x) lo = mid else hi = mid - 1;
        }
        return lo;
    }

    /// Checkpoint `i`, or the line end for `i == cps.len`.
    pub fn checkpoint(self: *const LineIndex, i: usize) Pos {
        return if (i < self.cps.items.len) self.cps.items[i] else self.end;
    }

    /// Position of the char boundary at line byte `byte` (clamped to the line;
    /// a byte inside a char gives the start of that char, like `LineLayout`).
    pub fn posAt(self: *const LineIndex, adv: *const Advances, src: Source, byte: usize) Pos {
        if (byte >= self.end.byte) return self.end;
        var pos = self.cps.items[self.checkpointAtByte(byte)];
        var buf: [block_bytes + 8]u8 = undefined;
        while (pos.byte < byte) {
            const end = @min(self.end.byte, @max(byte, pos.byte + 4) + 3, pos.byte + buf.len);
            const bytes = src.read(src.ctx, pos.byte, end, &buf);
            const used, _ = scan(adv, &pos, bytes, byte, end == self.end.byte, .whole);
            if (used == 0) break;
        }
        return pos;
    }

    /// The char boundary nearest to `x`. A char followed by zero-width ones
    /// (combining marks) counts as one: boundaries inside it aren't returned.
    pub fn byteAtX(self: *const LineIndex, adv: *const Advances, src: Source, x: i64) usize {
        if (x <= 0 or self.cps.items.len == 0) return skipZeroWidth(self, adv, src, 0);
        if (x >= self.end.x) return self.end.byte;
        const i = self.checkpointAtX(x);
        var prev = self.cps.items[i];
        const block_end = self.checkpoint(i + 1).byte;
        var buf: [block_bytes + 8]u8 = undefined;
        while (prev.byte < self.end.byte) {
            const end = @min(self.end.byte, @max(block_end, prev.byte + 1) + 4, prev.byte + buf.len);
            const bytes = src.read(src.ctx, prev.byte, end, &buf);
            var off: usize = 0;
            while (off < bytes.len) {
                var next = prev;
                const used, _ = scan(adv, &next, bytes[off..], next.byte + 1, end == self.end.byte, .started);
                if (used == 0) break;
                off += used;
                if (next.x <= x) {
                    prev = next;
                    continue;
                }
                // `x` is inside this char: pick the nearer edge, past any marks.
                if (x - prev.x > next.x - x) return skipZeroWidth(self, adv, src, next.byte);
                return prev.byte;
            }
            if (off == 0) break;
        }
        return prev.byte;
    }

    /// The boundary after any zero-width chars at `byte`.
    fn skipZeroWidth(self: *const LineIndex, adv: *const Advances, src: Source, byte: usize) usize {
        var pos: Pos = .{ .byte = byte };
        var buf: [64]u8 = undefined;
        while (pos.byte < self.end.byte) {
            const end = @min(self.end.byte, pos.byte + buf.len);
            const bytes = src.read(src.ctx, pos.byte, end, &buf);
            var next = pos;
            const used, _ = scan(adv, &next, bytes, pos.byte + 1, end == self.end.byte, .started);
            if (used == 0 or next.x != pos.x) break;
            pos = next;
        }
        return pos.byte;
    }
};

// ---- Tests ------------------------------------------------------------------

const testing = std.testing;

const SliceSource = struct {
    text: []const u8,

    fn read(ctx: *const anyopaque, start: usize, end: usize, out: []u8) []const u8 {
        const self: *const SliceSource = @ptrCast(@alignCast(ctx));
        const n = end - start;
        @memcpy(out[0..n], self.text[start..end]);
        return out[0..n];
    }

    fn source(self: *const SliceSource) Source {
        return .{ .ctx = self, .read = read, .len = self.text.len };
    }
};

/// A fake monospace font: 10 units per cell, CJK 2 cells, marks zero.
fn testOther(_: ?*anyopaque, _: u32, cp: u21) i32 {
    if (cp < 0x20) return 13;
    if (cp >= 0x300 and cp <= 0x36F) return 0;
    if (cp >= 0x4E00 and cp <= 0x9FFF) return 20;
    return 10;
}

fn testAdvances() Advances {
    var a: Advances = .{ .ascii = @splat(10), .other = testOther, .tab_size = 4, .units_per_px = 10 };
    a.ascii['i'] = 7; // one narrow char, to catch accidental "count × width" math
    a.ascii[0x01] = -1; // measured by `other`
    return a;
}

/// Reference positions computed char by char over the whole text.
fn reference(gpa: Allocator, adv: *const Advances, text: []const u8) ![]Pos {
    var out: std.ArrayList(Pos) = .empty;
    errdefer out.deinit(gpa);
    var pos: Pos = .{};
    try out.append(gpa, pos);
    while (pos.byte < text.len) {
        _ = scan(adv, &pos, text[pos.byte..], pos.byte + 1, true, .started);
        try out.append(gpa, pos);
    }
    return out.toOwnedSlice(gpa);
}

/// Checks the index against `reference` (`every`: how many boundaries to
/// skip between posAt checks).
fn expectMatchesReference(idx: *const LineIndex, adv: *const Advances, src: Source, text: []const u8, every: usize) !void {
    const gpa = testing.allocator;
    const ref = try reference(gpa, adv, text);
    defer gpa.free(ref);
    try testing.expectEqual(ref[ref.len - 1], idx.end);
    var k: usize = 0;
    while (k < ref.len) : (k += every) try testing.expectEqual(ref[k], idx.posAt(adv, src, ref[k].byte));
    try testing.expectEqual(@as(usize, 0), idx.cps.items[0].byte);
    for (idx.cps.items, 0..) |cp, i| {
        if (i > 0) try testing.expect(cp.byte > idx.cps.items[i - 1].byte);
        // Every checkpoint is on a char boundary, with the right state.
        var lo: usize = 0;
        var hi: usize = ref.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (ref[mid].byte < cp.byte) lo = mid + 1 else hi = mid;
        }
        try testing.expect(lo < ref.len and ref[lo].byte == cp.byte);
        try testing.expectEqual(ref[lo], cp);
        // Blocks stay small enough to scan quickly.
        try testing.expect(idx.checkpoint(i + 1).byte - cp.byte <= block_bytes + 3);
    }
}

fn makeText(gpa: Allocator, pieces: []const []const u8, repeat: usize) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    for (0..repeat) |i| try out.appendSlice(gpa, pieces[i % pieces.len]);
    return out.toOwnedSlice(gpa);
}

/// Zig has no array-repeat operator; this is `s` repeated `times` times.
fn repeatStr(comptime s: []const u8, comptime times: usize) []const u8 {
    return comptime blk: {
        @setEvalBranchQuota(times * 10 + 10);
        var out: []const u8 = "";
        for (0..times) |_| out = out ++ s;
        break :blk out;
    };
}

test "checkpointed positions match a char-by-char scan (tabs, CJK, combining marks)" {
    const gpa = testing.allocator;
    const adv = testAdvances();
    const text = try makeText(gpa, &.{ "{\"k\":1,", "\tx", "日本語", "e\u{301}", "  ii", "\t\t", "a\u{301}\u{302}b", "€", "\x01" }, 2000);
    defer gpa.free(text);
    const ss: SliceSource = .{ .text = text };
    var idx: LineIndex = .{};
    defer idx.deinit(gpa);
    try idx.build(gpa, &adv, ss.source());
    try testing.expect(idx.cps.items.len > text.len / block_bytes);
    try expectMatchesReference(&idx, &adv, ss.source(), text, 1);
    // An offset inside a char is at that char's start (like LineLayout.xs).
    const ref = try reference(gpa, &adv, text);
    defer gpa.free(ref);
    for (ref[0 .. ref.len - 1], ref[1..]) |r, next| {
        if (next.byte - r.byte > 1) try testing.expectEqual(r, idx.posAt(&adv, ss.source(), next.byte - 1));
    }
}

test "x to byte round trips on every char boundary" {
    const gpa = testing.allocator;
    const adv = testAdvances();
    const text = try makeText(gpa, &.{ "ab\tc", "日本", "e\u{301}x", "\t", "i", "o\u{308}\u{301}", "  " }, 3000);
    defer gpa.free(text);
    const ss: SliceSource = .{ .text = text };
    const src = ss.source();
    var idx: LineIndex = .{};
    defer idx.deinit(gpa);
    try idx.build(gpa, &adv, src);
    const ref = try reference(gpa, &adv, text);
    defer gpa.free(ref);
    for (ref, 0..) |r, k| {
        // Boundaries before a zero-width char share x with the one after it:
        // x maps back to the boundary after the whole cluster.
        var want = r.byte;
        var j = k;
        while (j + 1 < ref.len and ref[j + 1].x == r.x and text[ref[j].byte] != '\t') : (j += 1) want = ref[j + 1].byte;
        try testing.expectEqual(want, idx.byteAtX(&adv, src, r.x));
        // Anywhere in the left half of the next char maps to this boundary,
        // the right half to the next one.
        if (j + 1 < ref.len) {
            const next = ref[j + 1];
            const w = next.x - r.x;
            if (w >= 4) {
                try testing.expectEqual(want, idx.byteAtX(&adv, src, r.x + @divTrunc(w, 2) - 1));
                const after = idx.byteAtX(&adv, src, r.x + @divTrunc(w, 2) + 1);
                try testing.expectEqual(next.x, idx.posAt(&adv, src, after).x);
            }
        }
    }
    try testing.expectEqual(@as(usize, 0), idx.byteAtX(&adv, src, -50));
    try testing.expectEqual(text.len, idx.byteAtX(&adv, src, idx.width() + 50));
}

test "edits update the index like a rebuild" {
    // With tabs all over the line, and with tabs only near the start (edits
    // after them shift the checkpoints instead of re-scanning the tail).
    try expectEditsMatchRebuild(&.{ "abc,", "日本", "\t", "e\u{301}", "xyz;" });
    try expectEditsMatchRebuild(&.{ "abc,", "日本", "e\u{301}", "{\"k\":[1,2]}" });
}

fn expectEditsMatchRebuild(pieces: []const []const u8) !void {
    const gpa = testing.allocator;
    const adv = testAdvances();
    const base = try makeText(gpa, pieces, 4000);
    defer gpa.free(base);
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    try text.appendSlice(gpa, base);

    var idx: LineIndex = .{};
    defer idx.deinit(gpa);
    {
        const ss: SliceSource = .{ .text = text.items };
        try idx.build(gpa, &adv, ss.source());
    }
    const Edit = struct { at_frac: usize, del: usize, ins: []const u8 };
    const edits = [_]Edit{
        .{ .at_frac = 1000, .del = 0, .ins = "x" }, // typing at the end
        .{ .at_frac = 0, .del = 0, .ins = "ab" }, // at the start: shifts every checkpoint
        .{ .at_frac = 1, .del = 0, .ins = "\t" }, // a tab near the start
        .{ .at_frac = 500, .del = 0, .ins = "\t" }, // a tab changes later tab stops
        .{ .at_frac = 250, .del = 3, .ins = "" }, // deletion
        .{ .at_frac = 600, .del = 5000, .ins = "日" }, // across several blocks
        .{ .at_frac = 300, .del = 0, .ins = repeatStr("q", 9000) }, // a big paste
        .{ .at_frac = 700, .del = 1, .ins = "\t\t\t" },
        .{ .at_frac = 999, .del = 0, .ins = "abc" },
    };
    for (edits) |e| {
        var at = text.items.len * e.at_frac / 1000;
        // Edits happen on char boundaries.
        while (at < text.items.len and text.items[at] & 0xC0 == 0x80) at += 1;
        var end = @min(text.items.len, at + e.del);
        while (end < text.items.len and text.items[end] & 0xC0 == 0x80) end += 1;
        try text.replaceRange(gpa, at, end - at, e.ins);
        const ss: SliceSource = .{ .text = text.items };
        try idx.update(gpa, &adv, ss.source(), at, end, at + e.ins.len);
        try expectMatchesReference(&idx, &adv, ss.source(), text.items, 7);
    }
}
