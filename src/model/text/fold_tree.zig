//! Vim's view of a window's folds (`:help folding`): a tree of nested folds
//! built from fold definitions (computed indent / org / syntax folds plus
//! manual `zf` folds) and the folds currently closed in a FoldMap.
//!
//! Rows are inclusive. A fold [first, last] keeps `first` (its header)
//! visible and hides the rest when closed, so a closed fold is one display
//! line — the FoldMap convention, and how Vim draws marker/syntax folds.
//!
//! The operations follow Vim's fold.c: `setFold` is setManualFold (zo zc zO
//! zC), `setFoldRange` is opFoldRange (visual / `:foldopen`), `moveTo` is
//! foldMoveTo (zj zk [z ]z), `applyLevel` re-applies 'foldlevel' and
//! `deleteRange` picks what zd / zD remove.

const std = @import("std");

const Allocator = std.mem.Allocator;

pub const Node = struct {
    first: u32,
    last: u32,
    /// Nesting depth, 1 for top-level folds ('foldlevel' counts these).
    level: u32 = 1,
    parent: ?u32 = null,
    /// Index just past this fold's subtree (nodes are in pre-order).
    end: u32 = 0,
    closed: bool = false,
    /// Defined by indentation / headings / syntax.
    auto: bool = false,
    /// Created with zf / :fold; zd can delete it.
    manual: bool = false,
};

pub const Defs = struct {
    auto: []const [2]u32 = &.{},
    manual: []const [2]u32 = &.{},
    /// Closed folds. A closed range that matches no definition (e.g. made
    /// by Sublime's fold command) is a fold of its own until it opens.
    closed: []const [2]u32 = &.{},
};

pub const FoldTree = struct {
    gpa: Allocator,
    /// Pre-order: by first row, a fold before the folds it contains.
    nodes: std.ArrayList(Node) = .empty,

    pub fn build(gpa: Allocator, defs: Defs) !FoldTree {
        var self: FoldTree = .{ .gpa = gpa };
        errdefer self.deinit();
        try self.nodes.ensureTotalCapacity(gpa, defs.auto.len + defs.manual.len + defs.closed.len);
        for (defs.auto) |r| if (r[1] > r[0]) self.nodes.appendAssumeCapacity(.{ .first = r[0], .last = r[1], .auto = true });
        for (defs.manual) |r| if (r[1] > r[0]) self.nodes.appendAssumeCapacity(.{ .first = r[0], .last = r[1], .manual = true });
        for (defs.closed) |r| if (r[1] > r[0]) self.nodes.appendAssumeCapacity(.{ .first = r[0], .last = r[1], .closed = true });
        std.mem.sort(Node, self.nodes.items, {}, struct {
            fn lt(_: void, a: Node, b: Node) bool {
                return a.first < b.first or (a.first == b.first and a.last > b.last);
            }
        }.lt);
        // One node per range.
        var n: usize = 0;
        for (self.nodes.items) |node| {
            if (n > 0) {
                const prev = &self.nodes.items[n - 1];
                if (prev.first == node.first and prev.last == node.last) {
                    prev.closed = prev.closed or node.closed;
                    prev.auto = prev.auto or node.auto;
                    prev.manual = prev.manual or node.manual;
                    continue;
                }
            }
            self.nodes.items[n] = node;
            n += 1;
        }
        self.nodes.shrinkRetainingCapacity(n);
        // Nesting. A fold that only partly overlaps the previous one becomes
        // its sibling (Vim can't create those, but stale offsets can).
        var stack: std.ArrayList(u32) = .empty;
        defer stack.deinit(gpa);
        for (self.nodes.items, 0..) |*node, i| {
            while (stack.items.len > 0) {
                const top = &self.nodes.items[stack.items[stack.items.len - 1]];
                if (node.last <= top.last) break;
                top.end = @intCast(i);
                _ = stack.pop();
            }
            node.parent = if (stack.items.len > 0) stack.items[stack.items.len - 1] else null;
            node.level = @intCast(stack.items.len + 1);
            try stack.append(gpa, @intCast(i));
        }
        for (stack.items) |i| self.nodes.items[i].end = @intCast(n);
        return self;
    }

    pub fn deinit(self: *FoldTree) void {
        self.nodes.deinit(self.gpa);
    }

    /// Children of `parent` (null: the top-level folds), in order.
    pub const Children = struct {
        tree: *const FoldTree,
        i: u32,
        end: u32,

        pub fn next(self: *Children) ?u32 {
            if (self.i >= self.end) return null;
            const i = self.i;
            self.i = self.tree.nodes.items[i].end;
            return i;
        }
    };

    pub fn children(self: *const FoldTree, parent: ?u32) Children {
        if (parent) |p| return .{ .tree = self, .i = p + 1, .end = self.nodes.items[p].end };
        return .{ .tree = self, .i = 0, .end = @intCast(self.nodes.items.len) };
    }

    fn contains(self: *const FoldTree, i: u32, row: u32) bool {
        const node = self.nodes.items[i];
        return node.first <= row and row <= node.last;
    }

    /// The child of `parent` containing `row`.
    fn find(self: *const FoldTree, parent: ?u32, row: u32) ?u32 {
        var it = self.children(parent);
        while (it.next()) |i| {
            if (self.nodes.items[i].first > row) return null;
            if (self.contains(i, row)) return i;
        }
        return null;
    }

    pub fn deepest(self: *const FoldTree) u32 {
        var d: u32 = 0;
        for (self.nodes.items) |node| d = @max(d, node.level);
        return d;
    }

    /// The outermost closed fold containing `row` (Vim's foldClosed()).
    pub fn closedAt(self: *const FoldTree, row: u32) ?u32 {
        var parent: ?u32 = null;
        while (self.find(parent, row)) |i| {
            if (self.nodes.items[i].closed) return i;
            parent = i;
        }
        return null;
    }

    /// The innermost fold containing `row`.
    pub fn innermost(self: *const FoldTree, row: u32) ?u32 {
        var parent: ?u32 = null;
        var found: ?u32 = null;
        while (self.find(parent, row)) |i| {
            found = i;
            parent = i;
        }
        return found;
    }

    /// Opens or closes a fold containing `row`; false when there is none.
    /// Opening opens the outermost closed fold (and, with `recurse`, every
    /// fold inside it). Closing closes the innermost open fold not inside a
    /// closed one (with `recurse`, every fold containing `row`).
    pub fn setFold(self: *FoldTree, row: u32, opening: bool, recurse: bool) bool {
        var parent: ?u32 = null;
        var deepest_open: ?u32 = null;
        var found = false;
        while (self.find(parent, row)) |i| {
            found = true;
            const node = &self.nodes.items[i];
            if (!opening and recurse) {
                node.closed = true;
            } else if (node.closed) {
                if (opening) {
                    node.closed = false;
                    if (recurse) self.openNested(i);
                }
                break;
            }
            deepest_open = i;
            parent = i;
        }
        if (!opening) if (deepest_open) |i| {
            self.nodes.items[i].closed = true;
        };
        return found;
    }

    /// `setFold` repeated `count` times.
    pub fn setFoldRepeat(self: *FoldTree, row: u32, count: usize, opening: bool) bool {
        var found = false;
        var n: usize = 0;
        while (n < @max(count, 1)) : (n += 1) {
            if (!self.setFold(row, opening, false)) break;
            found = true;
        }
        return found;
    }

    pub fn openNested(self: *FoldTree, i: u32) void {
        for (self.nodes.items[i + 1 .. self.nodes.items[i].end]) |*node| node.closed = false;
    }

    /// `setFold` for every line of [first, last] (Visual zo / zc, `:foldopen`).
    pub fn setFoldRange(self: *FoldTree, first: u32, last: u32, opening: bool, recurse: bool) bool {
        var found = false;
        var row = first;
        while (row <= last) {
            var next = row;
            // Opening one level: the next fold to open is after this one.
            if (opening and !recurse) if (self.closedAt(row)) |i| {
                next = self.nodes.items[i].last;
            };
            found = self.setFold(row, opening, recurse) or found;
            // Closing one level: continue after the fold just closed.
            if (!opening and !recurse) if (self.closedAt(row)) |i| {
                next = self.nodes.items[i].last;
            };
            if (next == std.math.maxInt(u32)) break;
            row = next + 1;
        }
        return found;
    }

    /// 'foldlevel': folds nested deeper than `level` close, the rest open.
    pub fn applyLevel(self: *FoldTree, level: u32) void {
        for (self.nodes.items) |*node| node.closed = node.level > level;
    }

    /// zj / zk (`updown`) and ]z / [z: the row `count` fold moves from `row`,
    /// or null when the cursor wouldn't move.
    ///  zj: start of the next fold; zk: end of the previous fold. At each
    ///      nesting level the sibling after / before the cursor's fold
    ///      counts, the deepest one wins, and closed folds aren't entered.
    ///  ]z / [z: end / start of the innermost open fold around the cursor;
    ///      when already there, of the fold containing that one.
    pub fn moveTo(self: *const FoldTree, row: u32, updown: bool, forward: bool, count: usize) ?u32 {
        var cur = row;
        var moved = false;
        var n: usize = 0;
        while (n < @max(count, 1)) : (n += 1) {
            var found = cur;
            var parent: ?u32 = null;
            level: while (true) {
                // The fold containing `cur` at this level, and its neighbours.
                var prev: ?u32 = null;
                var hit: ?u32 = null;
                var after: ?u32 = null;
                var it = self.children(parent);
                while (it.next()) |i| {
                    if (self.contains(i, cur)) {
                        hit = i;
                        after = it.next();
                        break;
                    }
                    if (self.nodes.items[i].first > cur) {
                        after = i;
                        break;
                    }
                    prev = i;
                }
                const i = hit orelse {
                    // Between folds: only zj / zk look at the neighbours.
                    if (!updown) break :level;
                    if (forward) {
                        if (after) |a| found = self.nodes.items[a].first;
                    } else if (prev) |p| found = self.nodes.items[p].last;
                    break :level;
                };
                const node = self.nodes.items[i];
                if (updown) {
                    if (forward) {
                        if (after) |a| if (self.nodes.items[a].first > cur) {
                            found = self.nodes.items[a].first;
                        };
                    } else if (prev) |p| if (self.nodes.items[p].last < cur) {
                        found = self.nodes.items[p].last;
                    };
                    if (node.closed) break :level;
                } else {
                    // [z and ]z stop at a closed fold.
                    if (node.closed) break :level;
                    if (forward) {
                        if (node.last > cur) found = node.last;
                    } else if (node.first < cur) found = node.first;
                }
                parent = i;
            }
            if (found == cur) break;
            cur = found;
            moved = true;
        }
        return if (moved) cur else null;
    }

    /// The fold zd deletes at `row`: the innermost one, or the closed fold
    /// the row is in.
    pub fn foldToDelete(self: *const FoldTree, row: u32) ?u32 {
        var parent: ?u32 = null;
        var found: ?u32 = null;
        while (self.find(parent, row)) |i| {
            found = i;
            if (self.nodes.items[i].closed) break;
            parent = i;
        }
        return found;
    }

    /// zd / zD over [first, last]: appends the folds to delete (one level of
    /// every fold that (partly) overlaps the rows, or with `recurse` also
    /// everything nested in them).
    pub fn deleteRange(self: *const FoldTree, first: u32, last: u32, recurse: bool, out: *std.ArrayList(u32)) !void {
        var row = first;
        while (row <= last) {
            const i = self.foldToDelete(row) orelse {
                row += 1;
                continue;
            };
            try out.append(self.gpa, i);
            if (recurse) {
                var j = i + 1;
                while (j < self.nodes.items[i].end) : (j += 1) try out.append(self.gpa, j);
            }
            const node_last = self.nodes.items[i].last;
            if (node_last == std.math.maxInt(u32)) break;
            row = @max(row, node_last) + 1;
        }
    }

    /// Appends the ranges of the closed folds.
    pub fn closedRanges(self: *const FoldTree, gpa: Allocator, out: *std.ArrayList([2]u32)) !void {
        for (self.nodes.items) |node| if (node.closed) try out.append(gpa, .{ node.first, node.last });
    }
};

/// Indentation folds, one per line followed by more-indented lines (Sublime's
/// and this editor's `foldEndFor`): [header, last more-indented line].
/// `indents[r]` is the indent width of row r, null for blank lines (which
/// never end a fold and are never its last line). Sorted like FoldTree nodes.
pub fn indentFolds(gpa: Allocator, indents: []const ?u32, out: *std.ArrayList([2]u32)) !void {
    const start = out.items.len;
    var stack: std.ArrayList([2]u32) = .empty; // (row, indent)
    defer stack.deinit(gpa);
    var last_text: u32 = 0;
    for (indents, 0..) |maybe, r| {
        const w = maybe orelse continue;
        while (stack.items.len > 0 and stack.items[stack.items.len - 1][1] >= w) {
            const top = stack.pop().?;
            if (last_text > top[0]) try out.append(gpa, .{ top[0], last_text });
        }
        try stack.append(gpa, .{ @intCast(r), w });
        last_text = @intCast(r);
    }
    while (stack.pop()) |top| {
        if (last_text > top[0]) try out.append(gpa, .{ top[0], last_text });
    }
    std.mem.sort([2]u32, out.items[start..], {}, struct {
        fn lt(_: void, a: [2]u32, b: [2]u32) bool {
            return a[0] < b[0] or (a[0] == b[0] and a[1] > b[1]);
        }
    }.lt);
}

// ---- Tests ------------------------------------------------------------------

const testing = std.testing;

/// The fold `foldEndFor` gives for `row`, computed the slow way.
fn naiveIndentEnd(indents: []const ?u32, row: usize) ?u32 {
    const indent = indents[row] orelse return null;
    var last: ?u32 = null;
    var r = row + 1;
    while (r < indents.len) : (r += 1) {
        const w = indents[r] orelse continue;
        if (w <= indent) break;
        last = @intCast(r);
    }
    return last;
}

test "indent folds match foldEndFor" {
    const gpa = testing.allocator;
    const cases = [_][]const ?u32{
        &.{ 0, 4, 8, 8, 4, 0, 4, null, 4, 0 },
        &.{ 0, 4, null, null, 0, 8, 4, 2, 0, null },
        &.{ 4, 0, 8, 12, 12, 8, 4, 12 },
        &.{ null, 0, null },
        &.{},
    };
    for (cases) |indents| {
        var got: std.ArrayList([2]u32) = .empty;
        defer got.deinit(gpa);
        try indentFolds(gpa, indents, &got);
        var want: std.ArrayList([2]u32) = .empty;
        defer want.deinit(gpa);
        for (0..indents.len) |r| if (naiveIndentEnd(indents, r)) |last| try want.append(gpa, .{ @intCast(r), last });
        try testing.expectEqualSlices([2]u32, want.items, got.items);
    }
}

/// fn a {          0  fold A [0, 6]
///     if x {      1  fold B [1, 3]
///         y       2    fold C [2, 3] (manual)
///         z       3
///     }           4
///     w {         5  fold D [5, 6]
///         v       6
/// }               7
/// fn b {          8  fold E [8, 9]
///     u           9
/// }              10
fn sampleTree(closed: []const [2]u32) !FoldTree {
    return FoldTree.build(testing.allocator, .{
        .auto = &.{ .{ 0, 6 }, .{ 1, 3 }, .{ 5, 6 }, .{ 8, 9 } },
        .manual = &.{.{ 2, 3 }},
        .closed = closed,
    });
}

fn closedSet(t: *const FoldTree) ![][2]u32 {
    var out: std.ArrayList([2]u32) = .empty;
    try t.closedRanges(testing.allocator, &out);
    return out.toOwnedSlice(testing.allocator);
}

fn expectClosed(t: *const FoldTree, want: []const [2]u32) !void {
    const got = try closedSet(t);
    defer testing.allocator.free(got);
    try testing.expectEqualSlices([2]u32, want, got);
}

test "tree nesting and levels" {
    var t = try sampleTree(&.{ .{ 1, 3 }, .{ 20, 30 } });
    defer t.deinit();
    try testing.expectEqual(@as(usize, 6), t.nodes.items.len);
    const levels = [_]u32{ 1, 2, 3, 2, 1, 1 };
    for (t.nodes.items, levels) |node, l| try testing.expectEqual(l, node.level);
    try testing.expectEqual(@as(u32, 3), t.deepest());
    // A closed range matching a definition marks it; an unknown one is a node of its own.
    try testing.expect(t.nodes.items[1].closed and t.nodes.items[1].auto);
    try testing.expect(t.nodes.items[5].closed and !t.nodes.items[5].auto and !t.nodes.items[5].manual);
    try testing.expectEqual(@as(?u32, 1), t.closedAt(3));
    try testing.expectEqual(@as(?u32, null), t.closedAt(4));
    try testing.expectEqual(@as(?u32, 2), t.innermost(3));
}

test "zc closes the innermost open fold, zo the outermost closed one" {
    var t = try sampleTree(&.{});
    defer t.deinit();
    // zc on row 3: C, then B, then A.
    try testing.expect(t.setFold(3, false, false));
    try expectClosed(&t, &.{.{ 2, 3 }});
    try testing.expect(t.setFold(3, false, false));
    try expectClosed(&t, &.{ .{ 1, 3 }, .{ 2, 3 } });
    try testing.expect(t.setFold(3, false, false));
    try expectClosed(&t, &.{ .{ 0, 6 }, .{ 1, 3 }, .{ 2, 3 } });
    // zo opens one level at a time; nested folds keep their state.
    try testing.expect(t.setFold(0, true, false));
    try expectClosed(&t, &.{ .{ 1, 3 }, .{ 2, 3 } });
    // 2zo on row 3 opens B and then C.
    try testing.expect(t.setFoldRepeat(3, 2, true));
    try expectClosed(&t, &.{});
    // No fold on row 7.
    try testing.expect(!t.setFold(7, false, false));
}

test "zC and zO recurse" {
    var t = try sampleTree(&.{});
    defer t.deinit();
    try testing.expect(t.setFold(5, false, false)); // D
    try testing.expect(t.setFold(3, false, true));
    try expectClosed(&t, &.{ .{ 0, 6 }, .{ 1, 3 }, .{ 2, 3 }, .{ 5, 6 } });
    // zO on the closed A opens it and everything inside.
    try testing.expect(t.setFold(0, true, true));
    try expectClosed(&t, &.{});
}

test "fold range operations and foldlevel" {
    var t = try sampleTree(&.{});
    defer t.deinit();
    // Visual zc over rows 0..10 closes one level per fold: A and E (the
    // walk continues after each closed fold).
    try testing.expect(t.setFoldRange(0, 10, false, false));
    try expectClosed(&t, &.{ .{ 0, 6 }, .{ 8, 9 } });
    // :%foldclose! closes everything.
    _ = t.setFoldRange(0, 10, false, true);
    try expectClosed(&t, &.{ .{ 0, 6 }, .{ 1, 3 }, .{ 2, 3 }, .{ 5, 6 }, .{ 8, 9 } });
    // :%foldopen opens one level: A and E.
    _ = t.setFoldRange(0, 10, true, false);
    try expectClosed(&t, &.{ .{ 1, 3 }, .{ 2, 3 }, .{ 5, 6 } });
    t.applyLevel(1);
    try expectClosed(&t, &.{ .{ 1, 3 }, .{ 2, 3 }, .{ 5, 6 } });
    t.applyLevel(2);
    try expectClosed(&t, &.{.{ 2, 3 }});
    t.applyLevel(0);
    try expectClosed(&t, &.{ .{ 0, 6 }, .{ 1, 3 }, .{ 2, 3 }, .{ 5, 6 }, .{ 8, 9 } });
}

test "zj zk [z ]z" {
    var t = try sampleTree(&.{});
    defer t.deinit();
    // zj: from the header of A into its first nested fold, then onwards.
    try testing.expectEqual(@as(?u32, 1), t.moveTo(0, true, true, 1));
    try testing.expectEqual(@as(?u32, 2), t.moveTo(1, true, true, 1));
    try testing.expectEqual(@as(?u32, 5), t.moveTo(2, true, true, 1));
    try testing.expectEqual(@as(?u32, 8), t.moveTo(5, true, true, 1));
    try testing.expectEqual(@as(?u32, 8), t.moveTo(7, true, true, 1));
    try testing.expectEqual(@as(?u32, null), t.moveTo(8, true, true, 1));
    try testing.expectEqual(@as(?u32, 5), t.moveTo(0, true, true, 3));
    // zk: end of the previous fold.
    try testing.expectEqual(@as(?u32, 6), t.moveTo(8, true, false, 1));
    try testing.expectEqual(@as(?u32, 3), t.moveTo(5, true, false, 1));
    try testing.expectEqual(@as(?u32, 6), t.moveTo(7, true, false, 1));
    try testing.expectEqual(@as(?u32, null), t.moveTo(0, true, false, 1));
    // ]z / [z: ends of the innermost open fold, then of the one around it.
    try testing.expectEqual(@as(?u32, 3), t.moveTo(2, false, true, 1));
    try testing.expectEqual(@as(?u32, 6), t.moveTo(3, false, true, 1));
    try testing.expectEqual(@as(?u32, 2), t.moveTo(3, false, false, 1));
    try testing.expectEqual(@as(?u32, 1), t.moveTo(2, false, false, 1));
    try testing.expectEqual(@as(?u32, 0), t.moveTo(1, false, false, 1));
    try testing.expectEqual(@as(?u32, null), t.moveTo(7, false, true, 1));
    // A closed fold counts as one: zj from A's header skips its insides.
    _ = t.setFold(0, false, true);
    try testing.expectEqual(@as(?u32, 8), t.moveTo(0, true, true, 1));
    try testing.expectEqual(@as(?u32, null), t.moveTo(0, false, true, 1));
}

test "zd picks the innermost or the closed fold" {
    var t = try sampleTree(&.{.{ 1, 3 }});
    defer t.deinit();
    try testing.expectEqual(@as(?u32, 1), t.foldToDelete(2));
    try testing.expectEqual(@as(?u32, 3), t.foldToDelete(6));
    try testing.expectEqual(@as(?u32, null), t.foldToDelete(7));
    var out: std.ArrayList(u32) = .empty;
    defer out.deinit(testing.allocator);
    // Visual zd over everything: one level per fold (nested ones survive).
    try t.deleteRange(0, 10, false, &out);
    try testing.expectEqualSlices(u32, &.{ 0, 4 }, out.items);
    out.clearRetainingCapacity();
    try t.deleteRange(4, 5, false, &out);
    try testing.expectEqualSlices(u32, &.{0}, out.items);
    out.clearRetainingCapacity();
    // zD: with everything nested.
    try t.deleteRange(0, 0, true, &out);
    try testing.expectEqualSlices(u32, &.{ 0, 1, 2, 3 }, out.items);
}
