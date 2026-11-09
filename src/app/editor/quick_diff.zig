//! The change markers in an editor's gutter (VS Code's "quick diff"): bars
//! where the buffer differs from the index's version of its file, green for
//! added lines, blue for modified ones and a red wedge where lines were
//! deleted. The original comes from git (git_ui.zig reads it again whenever
//! the repository changes); the marks are recomputed once edits have paused.

const std = @import("std");
const buffer_mod = @import("../../model/text/buffer.zig");
const git = @import("../../model/git/git.zig");
const linediff = @import("../../model/text/linediff.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const Color = @import("../../framework/gpu/scene.zig").Color;

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;

/// Edits must pause this long before the marks are recomputed.
pub const settle_ms = 250;
/// Bigger buffers get no markers (the text is copied to compare it).
pub const max_bytes = 4 << 20;
/// Width the gutter makes room for.
pub const gutter_width: f32 = 7;

pub const QuickDiff = struct {
    gpa: Allocator,
    buffer: *const Buffer,
    /// The path the original was read for (a rename drops the object).
    path: []u8,
    /// The index's version; null when the file has none (untracked).
    original: ?[]u8 = null,
    /// The original is current (reading is done).
    has_original: bool = false,
    /// The repository state the original was read under (scm_state generation).
    generation: u32 = std.math.maxInt(u32),
    job: ?*git.Job = null,
    marks: []linediff.Mark = &.{},
    /// The buffer version `marks` is for; the original changing resets it.
    marks_version: u64 = std.math.maxInt(u64),
    seen_version: u64 = std.math.maxInt(u64),
    changed_at: i64 = 0,

    pub fn create(gpa: Allocator, buffer: *const Buffer, path: []const u8) !*QuickDiff {
        const self = try gpa.create(QuickDiff);
        errdefer gpa.destroy(self);
        self.* = .{ .gpa = gpa, .buffer = buffer, .path = try gpa.dupe(u8, path) };
        return self;
    }

    pub fn destroy(self: *QuickDiff) void {
        if (self.job) |j| j.detach();
        if (self.original) |o| self.gpa.free(o);
        self.gpa.free(self.marks);
        self.gpa.free(self.path);
        self.gpa.destroy(self);
    }

    /// Takes (a copy of) the original read from git; null: the file has none.
    pub fn setOriginal(self: *QuickDiff, text: ?[]const u8) void {
        if (self.original) |o| self.gpa.free(o);
        self.original = if (text) |t| self.gpa.dupe(u8, t) catch null else null;
        self.has_original = true;
        self.marks_version = std.math.maxInt(u64);
        self.changed_at = 0;
    }

    /// Whether the marks are out of date and due: true after they were recomputed.
    /// `now` is monotonic milliseconds.
    pub fn update(self: *QuickDiff, now: i64) bool {
        const version = self.buffer.version;
        if (version != self.seen_version) {
            self.seen_version = version;
            self.changed_at = now;
        }
        if (!self.has_original or version == self.marks_version) return false;
        if (self.marks_version != std.math.maxInt(u64) and now - self.changed_at < settle_ms) return false;
        self.recompute();
        return true;
    }

    /// Milliseconds until `update` will recompute, or null if nothing waits.
    pub fn dueIn(self: *const QuickDiff, now: i64) ?i64 {
        if (!self.has_original or self.buffer.version == self.marks_version) return null;
        return @max(1, self.changed_at + settle_ms - now);
    }

    fn recompute(self: *QuickDiff) void {
        self.marks_version = self.buffer.version;
        self.gpa.free(self.marks);
        self.marks = &.{};
        const original = self.original orelse return;
        if (self.buffer.len() > max_bytes or original.len > max_bytes) return;
        const text = self.buffer.textRange(self.gpa, 0, self.buffer.len()) catch return;
        defer self.gpa.free(text);
        self.marks = linediff.marksOf(self.gpa, original, text) catch &.{};
    }

    /// The mark covering buffer `row`, from the first mark that might
    /// (`hint` moves forward as rows are asked in order).
    pub fn markAt(self: *const QuickDiff, row: u32, hint: *usize) ?linediff.Mark {
        while (hint.* < self.marks.len) {
            const m = self.marks[hint.*];
            const end = m.row + @max(m.rows, 1);
            if (end <= row and m.kind != .deleted) {
                hint.* += 1;
            } else if (m.kind == .deleted and m.row < row) {
                hint.* += 1;
            } else break;
        }
        if (hint.* >= self.marks.len) return null;
        const m = self.marks[hint.*];
        if (m.kind == .deleted) return if (m.row == row) m else null;
        return if (row >= m.row and row < m.row + m.rows) m else null;
    }
};

/// VS Code's gutter colours (editorGutter.*Background).
pub fn color(theme: *const theme_mod.Theme, kind: linediff.MarkKind) Color {
    const c = theme.background;
    const dark = c.r * 0.299 + c.g * 0.587 + c.b * 0.114 < 0.5;
    return Color.hex(switch (kind) {
        .modified => if (dark) 0x1b81a8 else 0x2090d3,
        .added => if (dark) 0x487e02 else 0x48985d,
        .deleted => if (dark) 0xf14c4c else 0xe51400,
    });
}

const testing = std.testing;

test "marks follow the buffer once edits settle, and a new original resets them" {
    const gpa = testing.allocator;
    var buffer = try Buffer.initText(gpa, 1, "a\nb\nc\n");
    defer buffer.deinit();
    const qd = try QuickDiff.create(gpa, &buffer, "/repo/f.txt");
    defer qd.destroy();
    // Nothing is known before the original is read.
    try testing.expect(!qd.update(1000));
    qd.setOriginal("a\nb\nc\n");
    try testing.expect(qd.update(1000));
    try testing.expectEqual(@as(usize, 0), qd.marks.len);
    // An edit is waited for: not recomputed at once, recomputed after the pause.
    const none: []const @import("../../model/text/selection.zig").Selection = &.{};
    try buffer.edit(&.{.{ .start = 2, .end = 2, .text = "X" }}, .other, none, none);
    try testing.expect(!qd.update(2000));
    try testing.expect(qd.dueIn(2100).? > 0);
    try testing.expect(!qd.update(2100));
    try testing.expect(qd.update(2000 + settle_ms));
    try testing.expectEqual(@as(usize, 1), qd.marks.len);
    try testing.expectEqual(linediff.MarkKind.modified, qd.marks[0].kind);
    try testing.expectEqual(@as(u32, 1), qd.marks[0].row);
    // The file has no original: no marks, however the buffer changes.
    qd.setOriginal(null);
    try testing.expect(qd.update(5000));
    try testing.expectEqual(@as(usize, 0), qd.marks.len);
}

test "markAt walks rows in order over added, modified and deleted marks" {
    const gpa = testing.allocator;
    var buffer = try Buffer.initText(gpa, 1, "");
    defer buffer.deinit();
    const qd = try QuickDiff.create(gpa, &buffer, "/repo/f.txt");
    defer qd.destroy();
    qd.marks = try gpa.dupe(linediff.Mark, &.{
        .{ .kind = .added, .row = 1, .rows = 2 },
        .{ .kind = .deleted, .row = 5, .rows = 0 },
        .{ .kind = .modified, .row = 7, .rows = 1 },
    });
    var hint: usize = 0;
    try testing.expect(qd.markAt(0, &hint) == null);
    try testing.expectEqual(linediff.MarkKind.added, qd.markAt(1, &hint).?.kind);
    try testing.expectEqual(linediff.MarkKind.added, qd.markAt(2, &hint).?.kind);
    try testing.expect(qd.markAt(3, &hint) == null);
    try testing.expect(qd.markAt(4, &hint) == null);
    try testing.expectEqual(linediff.MarkKind.deleted, qd.markAt(5, &hint).?.kind);
    try testing.expect(qd.markAt(6, &hint) == null);
    try testing.expectEqual(linediff.MarkKind.modified, qd.markAt(7, &hint).?.kind);
    try testing.expect(qd.markAt(8, &hint) == null);
}
