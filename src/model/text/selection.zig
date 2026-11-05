//! Multi-cursor selections over buffer byte offsets.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Selection = struct {
    /// Fixed end; `head` is where the caret is.
    anchor: usize,
    head: usize,
    /// Remembered x (logical px) for vertical movement.
    goal_x: ?f64 = null,

    pub fn caret(offset: usize) Selection {
        return .{ .anchor = offset, .head = offset };
    }

    pub fn range(a: usize, h: usize) Selection {
        return .{ .anchor = a, .head = h };
    }

    pub fn start(self: Selection) usize {
        return @min(self.anchor, self.head);
    }

    pub fn end(self: Selection) usize {
        return @max(self.anchor, self.head);
    }

    pub fn isEmpty(self: Selection) bool {
        return self.anchor == self.head;
    }

    pub fn reversed(self: Selection) bool {
        return self.head < self.anchor;
    }

    pub fn len(self: Selection) usize {
        return self.end() - self.start();
    }
};

/// Sorts by start and merges overlapping (or touching, when non-empty and
/// sharing a boundary is meaningless) selections, like Sublime does after
/// every command. Returns the index of the selection containing `primary`.
pub fn normalize(sels: *std.ArrayList(Selection), primary_head: ?usize) usize {
    if (sels.items.len == 0) return 0;
    std.mem.sort(Selection, sels.items, {}, struct {
        fn lt(_: void, a: Selection, b: Selection) bool {
            if (a.start() != b.start()) return a.start() < b.start();
            return a.end() < b.end();
        }
    }.lt);
    var out: usize = 0;
    for (sels.items[1..]) |sel| {
        const last = &sels.items[out];
        // Touching non-empty selections stay separate (ctrl+d on "aaaa").
        const overlaps = sel.start() < last.end() or sel.start() == last.start() or
            (sel.start() == last.end() and (sel.isEmpty() or last.isEmpty()));
        if (overlaps) {
            const s = @min(last.start(), sel.start());
            const e = @max(last.end(), sel.end());
            const rev = last.reversed();
            last.* = if (rev) Selection.range(e, s) else Selection.range(s, e);
        } else {
            out += 1;
            sels.items[out] = sel;
        }
    }
    sels.shrinkRetainingCapacity(out + 1);
    if (primary_head) |h| {
        for (sels.items, 0..) |sel, i| {
            if (h >= sel.start() and h <= sel.end()) return i;
        }
    }
    return sels.items.len - 1;
}

/// How a position moves when text is replaced around it.
pub fn transformOffset(offset: usize, start: usize, old_len: usize, new_len: usize, bias_right: bool) usize {
    if (offset < start) return offset;
    if (offset > start + old_len or (offset == start + old_len and old_len > 0)) return offset + new_len - old_len;
    // Inside the replaced range (or exactly at an insertion point).
    if (offset == start and !bias_right) return start;
    if (old_len == 0) return if (bias_right) offset + new_len else offset;
    return start + new_len;
}

test "normalize merges overlapping" {
    const gpa = std.testing.allocator;
    var sels: std.ArrayList(Selection) = .empty;
    defer sels.deinit(gpa);
    try sels.appendSlice(gpa, &.{ .range(5, 10), .range(0, 3), .range(8, 12), .caret(20), .caret(20) });
    _ = normalize(&sels, null);
    try std.testing.expectEqual(@as(usize, 3), sels.items.len);
    try std.testing.expectEqual(@as(usize, 5), sels.items[1].start());
    try std.testing.expectEqual(@as(usize, 12), sels.items[1].end());
}

test "transformOffset" {
    // insert 3 bytes at 5
    try std.testing.expectEqual(@as(usize, 4), transformOffset(4, 5, 0, 3, true));
    try std.testing.expectEqual(@as(usize, 8), transformOffset(5, 5, 0, 3, true));
    try std.testing.expectEqual(@as(usize, 5), transformOffset(5, 5, 0, 3, false));
    try std.testing.expectEqual(@as(usize, 9), transformOffset(6, 5, 0, 3, true));
    // delete [5,8)
    try std.testing.expectEqual(@as(usize, 5), transformOffset(7, 5, 3, 0, true));
    try std.testing.expectEqual(@as(usize, 6), transformOffset(9, 5, 3, 0, true));
}
