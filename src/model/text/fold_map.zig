//! Folded regions (a simplified Zed `FoldMap`): maps buffer rows to display
//! rows by hiding the lines inside each fold. Folds are stored as byte
//! offsets and move with edits; hidden row ranges are recomputed lazily.

const std = @import("std");
const buffer_mod = @import("buffer.zig");
const selection = @import("selection.zig");

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;

pub const Fold = struct {
    /// End of the header line (its '\n' is the first hidden byte).
    start: usize,
    /// End of the last hidden line.
    end: usize,
};

pub const FoldMap = struct {
    folds: std.ArrayList(Fold) = .empty,
    /// Hidden row ranges [first, last], sorted and disjoint.
    hidden: std.ArrayList([2]u32) = .empty,
    /// Number of hidden rows before (and including) each range, for O(log n) mapping.
    hidden_before: std.ArrayList(u32) = .empty,
    cached_version: u64 = std.math.maxInt(u64),
    dirty: bool = true,
    /// Bumped whenever the set of folds changes (for caches built on top).
    generation: u64 = 0,

    // Vim's per-window fold state (see fold_tree.zig), kept here so the
    // offsets move with edits like `folds` do.
    /// Manual folds (zf, :fold): definitions that stay while open.
    manual: std.ArrayList(Fold) = .empty,
    /// The closed folds, set aside while folding is off ('nofoldenable').
    stashed: std.ArrayList(Fold) = .empty,
    disabled: bool = false,
    /// 'foldlevel' (null until set: every fold open) and 'foldmethod'.
    level: ?u32 = null,
    method: Method = .indent,
    /// Set while lines are added right after folds (vim's `o` and `p`): fold
    /// ends at the insertion point stay put instead of taking the text in.
    keep_ends: bool = false,

    pub const Method = enum { indent, manual, syntax };

    pub fn deinit(self: *FoldMap, gpa: Allocator) void {
        self.folds.deinit(gpa);
        self.hidden.deinit(gpa);
        self.hidden_before.deinit(gpa);
        self.manual.deinit(gpa);
        self.stashed.deinit(gpa);
    }

    pub fn isEmpty(self: *const FoldMap) bool {
        return self.folds.items.len == 0;
    }

    /// Hides rows (header_row, last_row]. Nested folds are kept (they matter
    /// again when the outer fold opens).
    pub fn fold(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, header_row: u32, last_row: u32) !void {
        if (last_row <= header_row) return;
        const f: Fold = .{ .start = buffer.lineEnd(header_row), .end = buffer.lineEnd(last_row) };
        for (self.folds.items) |existing| if (existing.start == f.start and existing.end == f.end) return;
        try self.folds.append(gpa, f);
        std.mem.sort(Fold, self.folds.items, {}, struct {
            fn lt(_: void, a: Fold, b: Fold) bool {
                return a.start < b.start or (a.start == b.start and a.end > b.end);
            }
        }.lt);
        self.dirty = true;
        self.generation +%= 1;
    }

    /// Adds many folds (rows [header, last]) with one sort instead of one per
    /// fold, so "fold all" stays O(n log n) on huge files.
    pub fn foldMany(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, rows: []const [2]u32) !void {
        try self.folds.ensureUnusedCapacity(gpa, rows.len);
        for (rows) |r| {
            if (r[1] <= r[0]) continue;
            self.folds.appendAssumeCapacity(.{ .start = buffer.lineEnd(r[0]), .end = buffer.lineEnd(r[1]) });
        }
        sortFolds(self.folds.items);
        // Sorted, so duplicates are adjacent.
        var n: usize = 0;
        for (self.folds.items) |f| {
            if (n > 0 and self.folds.items[n - 1].start == f.start and self.folds.items[n - 1].end == f.end) continue;
            self.folds.items[n] = f;
            n += 1;
        }
        self.folds.shrinkRetainingCapacity(n);
        self.dirty = true;
        self.generation +%= 1;
    }

    /// Removes folds whose header is `row`. Returns whether any were removed.
    pub fn unfoldHeader(self: *FoldMap, buffer: *const Buffer, row: u32) bool {
        const at = buffer.lineEnd(row);
        var removed = false;
        var i: usize = 0;
        while (i < self.folds.items.len) {
            if (self.folds.items[i].start == at) {
                _ = self.folds.orderedRemove(i);
                removed = true;
            } else i += 1;
        }
        if (removed) {
            self.dirty = true;
            self.generation +%= 1;
        }
        return removed;
    }

    /// Removes every fold hiding `offset`.
    pub fn unfoldContaining(self: *FoldMap, offset: usize) bool {
        var removed = false;
        var i: usize = 0;
        while (i < self.folds.items.len) {
            const f = self.folds.items[i];
            if (offset > f.start and offset <= f.end) {
                _ = self.folds.orderedRemove(i);
                removed = true;
            } else i += 1;
        }
        if (removed) {
            self.dirty = true;
            self.generation +%= 1;
        }
        return removed;
    }

    pub fn unfoldAll(self: *FoldMap) void {
        self.folds.clearRetainingCapacity();
        self.dirty = true;
        self.generation +%= 1;
    }

    pub fn isFoldHeader(self: *FoldMap, buffer: *const Buffer, row: u32) bool {
        const at = buffer.lineEnd(row);
        for (self.folds.items) |f| if (f.start == at) return true;
        return false;
    }

    pub fn applyChange(self: *FoldMap, change: buffer_mod.Change) void {
        transformFolds(&self.manual, change, !self.keep_ends);
        transformFolds(&self.stashed, change, !self.keep_ends);
        if (self.folds.items.len == 0) return;
        var i: usize = 0;
        while (i < self.folds.items.len) {
            const f = &self.folds.items[i];
            f.start = selection.transformOffset(f.start, change.start, change.old_len, change.new_len, false);
            f.end = selection.transformOffset(f.end, change.start, change.old_len, change.new_len, !self.keep_ends);
            // An edit that swallows the header newline dissolves the fold.
            if (f.end <= f.start) _ = self.folds.orderedRemove(i) else i += 1;
        }
        self.dirty = true;
        self.generation +%= 1;
    }

    fn transformFolds(list: *std.ArrayList(Fold), change: buffer_mod.Change, end_bias_right: bool) void {
        var i: usize = 0;
        while (i < list.items.len) {
            const f = &list.items[i];
            f.start = selection.transformOffset(f.start, change.start, change.old_len, change.new_len, false);
            f.end = selection.transformOffset(f.end, change.start, change.old_len, change.new_len, end_bias_right);
            if (f.end <= f.start) _ = list.orderedRemove(i) else i += 1;
        }
    }

    fn sortFolds(folds: []Fold) void {
        std.mem.sort(Fold, folds, {}, struct {
            fn lt(_: void, a: Fold, b: Fold) bool {
                return a.start < b.start or (a.start == b.start and a.end > b.end);
            }
        }.lt);
    }

    // ---- Vim fold state -----------------------------------------------------

    /// Rows [header, last] of a fold.
    pub fn foldRows(buffer: *const Buffer, f: Fold) [2]u32 {
        return .{ buffer.offsetToPoint(f.start).row, buffer.offsetToPoint(f.end).row };
    }

    pub fn appendRows(gpa: Allocator, buffer: *const Buffer, folds: []const Fold, out: *std.ArrayList([2]u32)) !void {
        for (folds) |f| try out.append(gpa, foldRows(buffer, f));
    }

    /// The closed folds as vim sees them (the stashed ones while folding is off).
    pub fn closedFolds(self: *const FoldMap) []const Fold {
        return if (self.disabled) self.stashed.items else self.folds.items;
    }

    /// Replaces the closed folds (the stashed ones while folding is off).
    pub fn setClosed(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, rows: []const [2]u32) !void {
        const list = if (self.disabled) &self.stashed else &self.folds;
        list.clearRetainingCapacity();
        for (rows) |r| if (r[1] > r[0]) try list.append(gpa, .{ .start = buffer.lineEnd(r[0]), .end = buffer.lineEnd(r[1]) });
        sortFolds(list.items);
        self.dirty = true;
        self.generation +%= 1;
    }

    /// 'foldenable': turning folding off sets the closed folds aside so all
    /// text shows; turning it on closes them again.
    pub fn setEnabled(self: *FoldMap, gpa: Allocator, enabled: bool) !void {
        if (enabled != self.disabled) return;
        if (enabled) {
            for (self.stashed.items) |f| {
                for (self.folds.items) |e| {
                    if (e.start == f.start and e.end == f.end) break;
                } else try self.folds.append(gpa, f);
            }
            self.stashed.clearRetainingCapacity();
            sortFolds(self.folds.items);
        } else {
            std.mem.swap(std.ArrayList(Fold), &self.folds, &self.stashed);
            self.folds.clearRetainingCapacity();
        }
        self.disabled = !enabled;
        self.dirty = true;
        self.generation +%= 1;
    }

    /// Adds a manual fold over rows [header_row, last_row].
    pub fn addManual(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, header_row: u32, last_row: u32) !void {
        if (last_row <= header_row) return;
        const f: Fold = .{ .start = buffer.lineEnd(header_row), .end = buffer.lineEnd(last_row) };
        for (self.manual.items) |e| if (e.start == f.start and e.end == f.end) return;
        try self.manual.append(gpa, f);
        sortFolds(self.manual.items);
    }

    /// Removes the manual fold over exactly rows [header_row, last_row].
    pub fn removeManual(self: *FoldMap, buffer: *const Buffer, header_row: u32, last_row: u32) bool {
        for (self.manual.items, 0..) |f, i| {
            const r = foldRows(buffer, f);
            if (r[0] == header_row and r[1] == last_row) {
                _ = self.manual.orderedRemove(i);
                return true;
            }
        }
        return false;
    }

    // ---- A closed fold as one line (vim's `:help fold-behavior`) ------------

    /// First row of the closed fold `row` is in (`row` when it isn't folded).
    pub fn closedStart(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32) u32 {
        if (self.isEmpty() or !self.isHidden(gpa, buffer, row)) return row;
        return self.bufferRow(gpa, buffer, self.displayRow(gpa, buffer, row));
    }

    /// Last row of the closed fold `row` is in (`row` when it isn't folded).
    pub fn closedEnd(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32) u32 {
        if (self.isEmpty()) return row;
        return @max(row, self.nextVisible(gpa, buffer, self.closedStart(gpa, buffer, row)) - 1);
    }

    pub fn inClosedFold(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32) bool {
        const start = self.closedStart(gpa, buffer, row);
        return self.closedEnd(gpa, buffer, start) > start;
    }

    /// `n` lines down from `row`, a closed fold being one line (stops at the last).
    pub fn linesDown(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32, n: usize) u32 {
        var r = row;
        var i: usize = 0;
        while (i < n) : (i += 1) {
            const e = self.closedEnd(gpa, buffer, r);
            if (e + 1 >= buffer.lineCount()) break;
            r = e + 1;
        }
        return r;
    }

    /// `n` lines up from `row` to the first line of a closed fold there.
    pub fn linesUp(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32, n: usize) u32 {
        var r = self.closedStart(gpa, buffer, row);
        var i: usize = 0;
        while (i < n and r > 0) : (i += 1) r = self.closedStart(gpa, buffer, r - 1);
        return r;
    }

    /// Byte range [start, end) widened so a closed fold it starts or ends in
    /// is whole: from its first line's start, to its last line's end (or, for
    /// a linewise range, the start of the line after).
    pub fn wholeFolds(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, start: usize, end: usize, linewise: bool) [2]usize {
        if (self.isEmpty()) return .{ start, end };
        var s = start;
        var e = end;
        const first = buffer.offsetToPoint(start).row;
        if (self.inClosedFold(gpa, buffer, first)) s = buffer.lineStart(self.closedStart(gpa, buffer, first));
        // The row of the last character in the range.
        const last = buffer.offsetToPoint(if (end > start) end - 1 else end).row;
        if (self.inClosedFold(gpa, buffer, last)) {
            const end_row = self.closedEnd(gpa, buffer, last);
            if (linewise) {
                e = if (end_row + 1 < buffer.lineCount()) buffer.lineStart(end_row + 1) else buffer.len();
            } else e = @max(e, buffer.lineEnd(end_row));
        }
        return .{ s, e };
    }

    fn refresh(self: *FoldMap, gpa: Allocator, buffer: *const Buffer) void {
        if (!self.dirty and self.cached_version == buffer.version) return;
        self.dirty = false;
        self.cached_version = buffer.version;
        self.hidden.clearRetainingCapacity();
        self.hidden_before.clearRetainingCapacity();
        var total: u32 = 0;
        for (self.folds.items) |f| {
            const first = buffer.offsetToPoint(f.start).row + 1;
            const last = buffer.offsetToPoint(f.end).row;
            if (last < first) continue;
            if (self.hidden.items.len > 0) {
                const prev = &self.hidden.items[self.hidden.items.len - 1];
                if (first <= prev.*[1] + 1) {
                    if (last > prev.*[1]) {
                        total += last - prev.*[1];
                        prev.*[1] = last;
                        self.hidden_before.items[self.hidden_before.items.len - 1] = total;
                    }
                    continue;
                }
            }
            total += last - first + 1;
            self.hidden.append(gpa, .{ first, last }) catch return;
            self.hidden_before.append(gpa, total) catch return;
        }
    }

    /// The hidden row ranges [first, last] (sorted, disjoint), and the number
    /// of hidden rows up to and including each.
    pub fn hiddenRanges(self: *FoldMap, gpa: Allocator, buffer: *const Buffer) struct { []const [2]u32, []const u32 } {
        if (self.folds.items.len == 0) return .{ &.{}, &.{} };
        self.refresh(gpa, buffer);
        return .{ self.hidden.items, self.hidden_before.items };
    }

    pub fn isHidden(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32) bool {
        if (self.folds.items.len == 0) return false;
        self.refresh(gpa, buffer);
        const i = self.rangeIndexAtOrBefore(row) orelse return false;
        return row <= self.hidden.items[i][1];
    }

    fn rangeIndexAtOrBefore(self: *const FoldMap, row: u32) ?usize {
        var lo: usize = 0;
        var hi: usize = self.hidden.items.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (self.hidden.items[mid][0] <= row) lo = mid + 1 else hi = mid;
        }
        return if (lo == 0) null else lo - 1;
    }

    /// Display row of a buffer row (a hidden row maps to its fold header).
    pub fn displayRow(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32) u32 {
        if (self.folds.items.len == 0) return row;
        self.refresh(gpa, buffer);
        const i = self.rangeIndexAtOrBefore(row) orelse return row;
        const range = self.hidden.items[i];
        if (row <= range[1]) {
            const before: u32 = if (i == 0) 0 else self.hidden_before.items[i - 1];
            return range[0] - 1 - before;
        }
        return row - self.hidden_before.items[i];
    }

    pub fn bufferRow(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, display_row: u32) u32 {
        if (self.folds.items.len == 0) return display_row;
        self.refresh(gpa, buffer);
        for (self.hidden.items, self.hidden_before.items) |range, total| {
            const before = total - (range[1] - range[0] + 1);
            if (display_row + before < range[0]) return display_row + before;
        }
        return display_row + (if (self.hidden_before.items.len > 0) self.hidden_before.items[self.hidden_before.items.len - 1] else 0);
    }

    pub fn displayRowCount(self: *FoldMap, gpa: Allocator, buffer: *const Buffer) u32 {
        const lines = buffer.lineCount();
        if (self.folds.items.len == 0) return lines;
        self.refresh(gpa, buffer);
        const hidden: u32 = if (self.hidden_before.items.len > 0) self.hidden_before.items[self.hidden_before.items.len - 1] else 0;
        return lines - hidden;
    }

    /// Next visible buffer row after `row`.
    pub fn nextVisible(self: *FoldMap, gpa: Allocator, buffer: *const Buffer, row: u32) u32 {
        if (self.folds.items.len == 0) return row + 1;
        self.refresh(gpa, buffer);
        const next = row + 1;
        const i = self.rangeIndexAtOrBefore(next) orelse return next;
        const range = self.hidden.items[i];
        return if (next <= range[1]) range[1] + 1 else next;
    }
};

test "fold map rows" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "a\nb\nc\nd\ne\nf\ng\n");
    defer buf.deinit();
    var fm: FoldMap = .{};
    defer fm.deinit(gpa);
    try fm.fold(gpa, &buf, 1, 3); // hide c, d
    try std.testing.expect(!fm.isHidden(gpa, &buf, 1));
    try std.testing.expect(fm.isHidden(gpa, &buf, 2));
    try std.testing.expect(fm.isHidden(gpa, &buf, 3));
    try std.testing.expect(!fm.isHidden(gpa, &buf, 4));
    try std.testing.expectEqual(@as(u32, 2), fm.displayRow(gpa, &buf, 4));
    try std.testing.expectEqual(@as(u32, 4), fm.bufferRow(gpa, &buf, 2));
    try std.testing.expectEqual(@as(u32, 1), fm.displayRow(gpa, &buf, 3));
    try std.testing.expectEqual(@as(u32, 6), fm.displayRowCount(gpa, &buf));
    try std.testing.expectEqual(@as(u32, 4), fm.nextVisible(gpa, &buf, 1));
    try fm.fold(gpa, &buf, 5, 6); // hide g
    try std.testing.expectEqual(@as(u32, 5), fm.displayRowCount(gpa, &buf));
    try std.testing.expectEqual(@as(u32, 5), fm.bufferRow(gpa, &buf, 3));
    try std.testing.expectEqual(@as(u32, 7), fm.bufferRow(gpa, &buf, 4));
}

test "vim fold state follows edits" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "a\nb\nc\nd\ne\n");
    defer buf.deinit();
    var fm: FoldMap = .{};
    defer fm.deinit(gpa);
    const Listener = struct {
        fm: *FoldMap,
        fn onChange(ctx: *anyopaque, _: *Buffer, change: buffer_mod.Change) void {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            self.fm.applyChange(change);
        }
    };
    var listener: Listener = .{ .fm = &fm };
    try buf.addListener(.{ .ctx = &listener, .onChange = Listener.onChange });
    try fm.addManual(gpa, &buf, 1, 3);
    try fm.setClosed(gpa, &buf, &.{.{ 1, 3 }});
    try std.testing.expect(fm.isHidden(gpa, &buf, 2));
    // 'nofoldenable' shows everything but remembers the closed fold.
    try fm.setEnabled(gpa, false);
    try std.testing.expect(fm.isEmpty());
    try std.testing.expectEqual(@as(usize, 1), fm.closedFolds().len);
    // Lines inserted above move manual and stashed folds down.
    try buf.edit(&.{.{ .start = 0, .end = 0, .text = "x\ny\n" }}, .other, &.{}, &.{});
    try std.testing.expectEqual([2]u32{ 3, 5 }, FoldMap.foldRows(&buf, fm.manual.items[0]));
    try fm.setEnabled(gpa, true);
    try std.testing.expect(fm.isHidden(gpa, &buf, 4));
    try std.testing.expect(!fm.isHidden(gpa, &buf, 6));
    try std.testing.expect(fm.removeManual(&buf, 3, 5));
    try std.testing.expectEqual(@as(usize, 0), fm.manual.items.len);
    // Deleting the folded lines removes the fold.
    try fm.addManual(gpa, &buf, 3, 5);
    try buf.edit(&.{.{ .start = buf.lineStart(3), .end = buf.lineStart(6), .text = "" }}, .other, &.{}, &.{});
    try std.testing.expect(fm.isEmpty());
    try std.testing.expectEqual(@as(usize, 0), fm.manual.items.len);
}

test "text appended after a fold at the end of the file stays visible" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "a\n  b");
    defer buf.deinit();
    var fm: FoldMap = .{};
    defer fm.deinit(gpa);
    try fm.fold(gpa, &buf, 0, 1);
    try fm.addManual(gpa, &buf, 0, 1);
    const at = buf.len();
    const change: buffer_mod.Change = .{ .start = at, .old_len = 0, .new_len = 3, .start_point = .{ .row = 1, .column = 3 }, .old_end_point = .{ .row = 1, .column = 3 }, .new_end_point = .{ .row = 2, .column = 2 } };
    // Typing at the end of a fold's last line extends it...
    fm.applyChange(change);
    try std.testing.expectEqual(at + 3, fm.folds.items[0].end);
    // ...but lines opened after the fold (`o`, `p`) stay out.
    fm.folds.items[0].end = at;
    fm.manual.items[0].end = at;
    fm.keep_ends = true;
    fm.applyChange(change);
    try std.testing.expectEqual(at, fm.folds.items[0].end);
    try std.testing.expectEqual(at, fm.manual.items[0].end);
}

test "a closed fold is one line" {
    const gpa = std.testing.allocator;
    // 0 a, 1 fn {, 2-3 body, 4 }, 5 b, 6 c {, 7 body
    var buf = try Buffer.initText(gpa, 1, "a\nfn {\n  x\n  y\n}\nb\nc {\n  z");
    defer buf.deinit();
    var fm: FoldMap = .{};
    defer fm.deinit(gpa);
    try fm.fold(gpa, &buf, 1, 3);
    try fm.fold(gpa, &buf, 6, 7);
    try std.testing.expectEqual(@as(u32, 1), fm.closedStart(gpa, &buf, 3));
    try std.testing.expectEqual(@as(u32, 3), fm.closedEnd(gpa, &buf, 1));
    try std.testing.expectEqual(@as(u32, 4), fm.closedEnd(gpa, &buf, 4));
    try std.testing.expect(fm.inClosedFold(gpa, &buf, 2) and !fm.inClosedFold(gpa, &buf, 4));
    // j / k counts: the fold is one step.
    try std.testing.expectEqual(@as(u32, 1), fm.linesDown(gpa, &buf, 0, 1));
    try std.testing.expectEqual(@as(u32, 4), fm.linesDown(gpa, &buf, 0, 2));
    try std.testing.expectEqual(@as(u32, 6), fm.linesDown(gpa, &buf, 4, 5)); // stops on the last line
    try std.testing.expectEqual(@as(u32, 1), fm.linesUp(gpa, &buf, 4, 1));
    try std.testing.expectEqual(@as(u32, 0), fm.linesUp(gpa, &buf, 4, 2));
    // dd on the header takes the whole fold, linewise.
    const dd = fm.wholeFolds(gpa, &buf, buf.lineStart(1), buf.lineStart(2), true);
    try std.testing.expectEqual([2]usize{ buf.lineStart(1), buf.lineStart(4) }, dd);
    // dl on the header: from its line start to the end of the fold's last line.
    const dl = fm.wholeFolds(gpa, &buf, buf.lineStart(1) + 1, buf.lineStart(1) + 2, false);
    try std.testing.expectEqual([2]usize{ buf.lineStart(1), buf.lineEnd(3) }, dl);
    // An exclusive range ending at column 0 of a fold doesn't take it.
    const dj = fm.wholeFolds(gpa, &buf, buf.lineStart(4), buf.lineStart(6), false);
    try std.testing.expectEqual([2]usize{ buf.lineStart(4), buf.lineStart(6) }, dj);
    // A fold at the end of the buffer.
    const last = fm.wholeFolds(gpa, &buf, buf.lineStart(5), buf.lineStart(7), true);
    try std.testing.expectEqual([2]usize{ buf.lineStart(5), buf.len() }, last);
}

test "foldMany matches folding one at a time" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "a\n  b\n  c\nd\n  e\nf");
    defer buf.deinit();
    var one: FoldMap = .{};
    defer one.deinit(gpa);
    try one.fold(gpa, &buf, 3, 4);
    try one.fold(gpa, &buf, 0, 2);
    var many: FoldMap = .{};
    defer many.deinit(gpa);
    try many.foldMany(gpa, &buf, &.{ .{ 3, 4 }, .{ 0, 2 }, .{ 0, 2 } });
    try std.testing.expectEqual(one.folds.items.len, many.folds.items.len);
    for (one.folds.items, many.folds.items) |a, b| {
        try std.testing.expectEqual(a.start, b.start);
        try std.testing.expectEqual(a.end, b.end);
    }
    try std.testing.expectEqual(one.displayRowCount(gpa, &buf), many.displayRowCount(gpa, &buf));
}
