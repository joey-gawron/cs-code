//! Line diffs (Myers' O(ND) algorithm) of two texts, and what the editor
//! makes of them: hunks for the diff editor, and the added / modified /
//! deleted marks of the gutter. Common lines at both ends are trimmed first,
//! so the usual small edit of a big file costs next to nothing; a diff with
//! more than `max_edits` changed lines in the middle is reported as one
//! hunk rather than searched for.

const std = @import("std");

const Allocator = std.mem.Allocator;

/// A run of changed lines: `a_len` lines from `a_start` of the old text
/// were replaced by `b_len` lines from `b_start` of the new one.
pub const Hunk = struct {
    a_start: u32,
    a_len: u32,
    b_start: u32,
    b_len: u32,
};

/// Past this many differing lines the middle is one hunk.
pub const max_edits: usize = 1000;

/// The lines of `text` (a final newline doesn't start another line).
pub fn splitLines(gpa: Allocator, text: []const u8) ![][]const u8 {
    var lines: std.ArrayList([]const u8) = .empty;
    errdefer lines.deinit(gpa);
    var it = std.mem.splitScalar(u8, text, '\n');
    while (it.next()) |l| {
        if (it.peek() == null and l.len == 0) break;
        try lines.append(gpa, if (l.len > 0 and l[l.len - 1] == '\r') l[0 .. l.len - 1] else l);
    }
    return lines.toOwnedSlice(gpa);
}

/// The hunks that turn `a` into `b` (caller frees).
pub fn diff(gpa: Allocator, a: []const []const u8, b: []const []const u8) ![]Hunk {
    var hunks: std.ArrayList(Hunk) = .empty;
    errdefer hunks.deinit(gpa);

    var prefix: usize = 0;
    while (prefix < a.len and prefix < b.len and std.mem.eql(u8, a[prefix], b[prefix])) prefix += 1;
    var suffix: usize = 0;
    while (suffix < a.len - prefix and suffix < b.len - prefix and
        std.mem.eql(u8, a[a.len - 1 - suffix], b[b.len - 1 - suffix])) suffix += 1;
    const am = a[prefix .. a.len - suffix];
    const bm = b[prefix .. b.len - suffix];
    if (am.len == 0 and bm.len == 0) return hunks.toOwnedSlice(gpa);
    if (am.len == 0 or bm.len == 0) {
        try hunks.append(gpa, .{ .a_start = @intCast(prefix), .a_len = @intCast(am.len), .b_start = @intCast(prefix), .b_len = @intCast(bm.len) });
        return hunks.toOwnedSlice(gpa);
    }

    // Lines become small integers, so the search compares numbers.
    var arena_state = std.heap.ArenaAllocator.init(gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();
    var ids: std.StringHashMapUnmanaged(u32) = .empty;
    const ai = try arena.alloc(u32, am.len);
    const bi = try arena.alloc(u32, bm.len);
    for (am, ai) |l, *id| id.* = try intern(arena, &ids, l);
    for (bm, bi) |l, *id| id.* = try intern(arena, &ids, l);

    const ok = try myers(gpa, arena, ai, bi, @intCast(prefix), &hunks);
    if (!ok) {
        hunks.clearRetainingCapacity();
        try hunks.append(gpa, .{ .a_start = @intCast(prefix), .a_len = @intCast(am.len), .b_start = @intCast(prefix), .b_len = @intCast(bm.len) });
    }
    return hunks.toOwnedSlice(gpa);
}

fn intern(arena: Allocator, ids: *std.StringHashMapUnmanaged(u32), line: []const u8) !u32 {
    const gop = try ids.getOrPut(arena, line);
    if (!gop.found_existing) gop.value_ptr.* = @intCast(ids.count() - 1);
    return gop.value_ptr.*;
}

/// Myers' forward search with the trace of every round kept, walked back to
/// the edit script. False when the texts differ by more than `max_edits`.
fn myers(gpa: Allocator, arena: Allocator, a: []const u32, b: []const u32, base: u32, out: *std.ArrayList(Hunk)) !bool {
    const n: isize = @intCast(a.len);
    const m: isize = @intCast(b.len);
    const max: isize = @min(n + m, @as(isize, @intCast(max_edits)));
    const width: usize = @intCast(2 * max + 3);
    const off: isize = max + 1;
    var v = try arena.alloc(isize, width);
    @memset(v, 0);
    // trace[d] is the part of V round d reads, as it was before the round.
    var trace: std.ArrayList([]isize) = .empty;
    var found: ?isize = null;
    var d: isize = 0;
    rounds: while (d <= max) : (d += 1) {
        // Round d reads diagonals -d-1 .. d+1 only.
        const lo: usize = @intCast(off - d - 1);
        const hi: usize = @intCast(off + d + 2);
        try trace.append(arena, try arena.dupe(isize, v[lo..hi]));
        var k: isize = -d;
        while (k <= d) : (k += 2) {
            var x: isize = if (k == -d or (k != d and v[@intCast(off + k - 1)] < v[@intCast(off + k + 1)]))
                v[@intCast(off + k + 1)]
            else
                v[@intCast(off + k - 1)] + 1;
            var y = x - k;
            while (x < n and y < m and a[@intCast(x)] == b[@intCast(y)]) {
                x += 1;
                y += 1;
            }
            v[@intCast(off + k)] = x;
            if (x >= n and y >= m) {
                found = d;
                break :rounds;
            }
        }
    }
    const last = found orelse return false;

    // Walk back from the end: each step is a deletion (a line of `a`), an
    // insertion (a line of `b`), or a diagonal of equal lines.
    const Step = struct { x: isize, y: isize, kind: enum { del, ins } };
    var steps: std.ArrayList(Step) = .empty;
    var x = n;
    var y = m;
    var dd = last;
    while (dd > 0) : (dd -= 1) {
        const pv = trace.items[@intCast(dd)];
        const k = x - y;
        const at = dd + 1; // pv[k + at] is diagonal k
        const prev_k: isize = if (k == -dd or (k != dd and pv[@intCast(k - 1 + at)] < pv[@intCast(k + 1 + at)])) k + 1 else k - 1;
        const prev_x = pv[@intCast(prev_k + at)];
        const prev_y = prev_x - prev_k;
        // The diagonal that led here ends at (x, y); the edit before it
        // ended at (prev_x, prev_y) and moved one line.
        if (prev_k == k + 1) {
            try steps.append(arena, .{ .x = prev_x, .y = prev_y, .kind = .ins });
        } else {
            try steps.append(arena, .{ .x = prev_x, .y = prev_y, .kind = .del });
        }
        x = prev_x;
        y = prev_y;
    }

    // Steps come last to first; merge neighbours into hunks.
    var i = steps.items.len;
    var cur: ?Hunk = null;
    while (i > 0) {
        i -= 1;
        const s = steps.items[i];
        const ax: u32 = @intCast(s.x);
        const by: u32 = @intCast(s.y);
        const touches = if (cur) |c| (ax == c.a_start - base + c.a_len and by == c.b_start - base + c.b_len) else false;
        if (!touches) {
            if (cur) |c| try out.append(gpa, c);
            cur = .{ .a_start = base + ax, .a_len = 0, .b_start = base + by, .b_len = 0 };
        }
        if (s.kind == .del) cur.?.a_len += 1 else cur.?.b_len += 1;
    }
    if (cur) |c| try out.append(gpa, c);
    return true;
}

/// How the gutter marks a line.
pub const MarkKind = enum { added, modified, deleted };

/// A run of marked lines of the new text, `rows` lines from `row`; a
/// `deleted` mark has no lines of its own and sits at the top of `row`.
pub const Mark = struct {
    kind: MarkKind,
    row: u32,
    rows: u32,
};

/// The gutter marks for `hunks`: a change that replaces as many lines as it
/// has is modified; extra new lines are added, and removed lines with
/// nothing replacing them leave a deleted mark.
pub fn marks(gpa: Allocator, hunks: []const Hunk) ![]Mark {
    var out: std.ArrayList(Mark) = .empty;
    errdefer out.deinit(gpa);
    for (hunks) |h| {
        if (h.a_len == 0) {
            try out.append(gpa, .{ .kind = .added, .row = h.b_start, .rows = h.b_len });
        } else if (h.b_len == 0) {
            try out.append(gpa, .{ .kind = .deleted, .row = h.b_start, .rows = 0 });
        } else {
            const both = @min(h.a_len, h.b_len);
            try out.append(gpa, .{ .kind = .modified, .row = h.b_start, .rows = both });
            if (h.b_len > both) try out.append(gpa, .{ .kind = .added, .row = h.b_start + both, .rows = h.b_len - both });
            if (h.a_len > both) try out.append(gpa, .{ .kind = .deleted, .row = h.b_start + both, .rows = 0 });
        }
    }
    return out.toOwnedSlice(gpa);
}

/// The marks of the new text against the old, from the texts themselves.
pub fn marksOf(gpa: Allocator, old: []const u8, new: []const u8) ![]Mark {
    const a = try splitLines(gpa, old);
    defer gpa.free(a);
    const b = try splitLines(gpa, new);
    defer gpa.free(b);
    const hunks = try diff(gpa, a, b);
    defer gpa.free(hunks);
    return marks(gpa, hunks);
}

const testing = std.testing;

/// Applies `hunks` to `a`, which must give `b`.
fn applyHunks(gpa: Allocator, a: []const []const u8, b: []const []const u8, hunks: []const Hunk) !bool {
    var out: std.ArrayList([]const u8) = .empty;
    defer out.deinit(gpa);
    var ai: usize = 0;
    for (hunks) |h| {
        while (ai < h.a_start) : (ai += 1) try out.append(gpa, a[ai]);
        ai += h.a_len;
        for (b[h.b_start .. h.b_start + h.b_len]) |l| try out.append(gpa, l);
    }
    while (ai < a.len) : (ai += 1) try out.append(gpa, a[ai]);
    if (out.items.len != b.len) return false;
    for (out.items, b) |x, y| if (!std.mem.eql(u8, x, y)) return false;
    return true;
}

fn expectHunks(old: []const u8, new: []const u8, expected: []const Hunk) !void {
    const gpa = testing.allocator;
    const a = try splitLines(gpa, old);
    defer gpa.free(a);
    const b = try splitLines(gpa, new);
    defer gpa.free(b);
    const hunks = try diff(gpa, a, b);
    defer gpa.free(hunks);
    try testing.expectEqualSlices(Hunk, expected, hunks);
    try testing.expect(try applyHunks(gpa, a, b, hunks));
}

test "lines split like git's: a final newline starts nothing, CRLF is one ending" {
    const gpa = testing.allocator;
    const l = try splitLines(gpa, "a\r\nb\n\nc");
    defer gpa.free(l);
    try testing.expectEqual(@as(usize, 4), l.len);
    try testing.expectEqualStrings("a", l[0]);
    try testing.expectEqualStrings("", l[2]);
    const e = try splitLines(gpa, "");
    defer gpa.free(e);
    try testing.expectEqual(@as(usize, 0), e.len);
}

test "identical texts have no hunks" {
    try expectHunks("a\nb\nc\n", "a\nb\nc\n", &.{});
}

test "one inserted, one deleted, one replaced line" {
    try expectHunks("a\nb\nc\n", "a\nx\nb\nc\n", &.{.{ .a_start = 1, .a_len = 0, .b_start = 1, .b_len = 1 }});
    try expectHunks("a\nb\nc\n", "a\nc\n", &.{.{ .a_start = 1, .a_len = 1, .b_start = 1, .b_len = 0 }});
    try expectHunks("a\nb\nc\n", "a\nB\nc\n", &.{.{ .a_start = 1, .a_len = 1, .b_start = 1, .b_len = 1 }});
}

test "changes at both ends and apart are separate hunks" {
    try expectHunks(
        "1\n2\n3\n4\n5\n6\n7\n",
        "one\n2\n3\n4\n5\n6\nseven\n",
        &.{
            .{ .a_start = 0, .a_len = 1, .b_start = 0, .b_len = 1 },
            .{ .a_start = 6, .a_len = 1, .b_start = 6, .b_len = 1 },
        },
    );
}

test "everything added to an empty text, and everything removed" {
    try expectHunks("", "a\nb\n", &.{.{ .a_start = 0, .a_len = 0, .b_start = 0, .b_len = 2 }});
    try expectHunks("a\nb\n", "", &.{.{ .a_start = 0, .a_len = 2, .b_start = 0, .b_len = 0 }});
}

test "a moved block is a deletion and an insertion that still reproduce the text" {
    try expectHunks("a\nb\nc\nd\ne\n", "a\nd\ne\nb\nc\n", &.{
        .{ .a_start = 1, .a_len = 2, .b_start = 1, .b_len = 0 },
        .{ .a_start = 5, .a_len = 0, .b_start = 3, .b_len = 2 },
    });
}

test "random edits always reproduce the new text" {
    const gpa = testing.allocator;
    var prng = std.Random.DefaultPrng.init(0xd1ff);
    const rand = prng.random();
    for (0..200) |_| {
        var old: std.ArrayList(u8) = .empty;
        defer old.deinit(gpa);
        var new: std.ArrayList(u8) = .empty;
        defer new.deinit(gpa);
        const n = rand.intRangeAtMost(usize, 0, 40);
        for (0..n) |_| {
            const line = [_]u8{ 'a' + rand.uintLessThan(u8, 6), '\n' };
            try old.appendSlice(gpa, &line);
            if (rand.uintLessThan(u8, 4) != 0) try new.appendSlice(gpa, &line);
            if (rand.uintLessThan(u8, 5) == 0) try new.appendSlice(gpa, &.{ 'a' + rand.uintLessThan(u8, 6), '\n' });
        }
        const a = try splitLines(gpa, old.items);
        defer gpa.free(a);
        const b = try splitLines(gpa, new.items);
        defer gpa.free(b);
        const hunks = try diff(gpa, a, b);
        defer gpa.free(hunks);
        try testing.expect(try applyHunks(gpa, a, b, hunks));
    }
}

test "a huge difference is one hunk, not a search" {
    const gpa = testing.allocator;
    var old: std.ArrayList(u8) = .empty;
    defer old.deinit(gpa);
    var new: std.ArrayList(u8) = .empty;
    defer new.deinit(gpa);
    for (0..9000) |i| {
        try old.print(gpa, "old {d}\n", .{i});
        try new.print(gpa, "new {d}\n", .{i});
    }
    const a = try splitLines(gpa, old.items);
    defer gpa.free(a);
    const b = try splitLines(gpa, new.items);
    defer gpa.free(b);
    const hunks = try diff(gpa, a, b);
    defer gpa.free(hunks);
    try testing.expectEqual(@as(usize, 1), hunks.len);
    try testing.expectEqual(@as(u32, 9000), hunks[0].b_len);
}

test "gutter marks: added, modified, deleted, and a longer replacement" {
    const gpa = testing.allocator;
    {
        const m = try marksOf(gpa, "a\nb\nc\n", "a\nx\nb\nc\n");
        defer gpa.free(m);
        try testing.expectEqualSlices(Mark, &.{.{ .kind = .added, .row = 1, .rows = 1 }}, m);
    }
    {
        const m = try marksOf(gpa, "a\nb\nc\n", "a\nc\n");
        defer gpa.free(m);
        try testing.expectEqualSlices(Mark, &.{.{ .kind = .deleted, .row = 1, .rows = 0 }}, m);
    }
    {
        const m = try marksOf(gpa, "a\nb\nc\n", "a\nB1\nB2\nc\n");
        defer gpa.free(m);
        try testing.expectEqualSlices(Mark, &.{
            .{ .kind = .modified, .row = 1, .rows = 1 },
            .{ .kind = .added, .row = 2, .rows = 1 },
        }, m);
    }
    {
        const m = try marksOf(gpa, "a\nb\nc\nd\n", "a\nB\nd\n");
        defer gpa.free(m);
        try testing.expectEqualSlices(Mark, &.{
            .{ .kind = .modified, .row = 1, .rows = 1 },
            .{ .kind = .deleted, .row = 2, .rows = 0 },
        }, m);
    }
}
