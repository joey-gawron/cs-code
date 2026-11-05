//! Long lines (minified files, one-line data dumps) are positioned with a
//! `LineIndex` (a checkpoint every few KB, see
//! framework/text/line_index.zig) instead of a full `LineLayout`, and only
//! the part on screen is shaped when drawing. Each editor caches the indexes
//! of the long lines it recently showed and keeps them in step with edits.

const std = @import("std");
const line_index = @import("../framework/text/line_index.zig");
const text_mod = @import("../framework/text/text_system.zig");
const buffer_mod = @import("../model/text/buffer.zig");

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;
const LineIndex = line_index.LineIndex;

/// Lines longer than this many bytes use a `LineIndex`.
pub const threshold = 16 * 1024;

/// Indexes kept per editor (least recently used ones are dropped).
const max_cached = 64;
/// More edits than this to a line between two uses: rebuild it once
/// instead (typing at thousands of carets on one line).
const max_pending_edits = 8;

const Entry = struct {
    row: u32,
    index: LineIndex = .{},
    adv: line_index.Advances,
    font: text_mod.FontId,
    scale: f32,
    valid: bool = false,
    pending: u32 = 0,
    used: u64 = 0,
};

/// Reads a buffer line for the index.
const LineSource = struct {
    buffer: *const Buffer,
    start: usize,
    len: usize,

    fn read(ctx: *const anyopaque, start: usize, end: usize, out: []u8) []const u8 {
        const self: *const LineSource = @ptrCast(@alignCast(ctx));
        return self.buffer.rope.copyRange(self.start + start, self.start + end, out);
    }
};

/// A long line's index, bound to its buffer line for queries. Offsets are
/// line-relative bytes, x is logical px from the line start.
pub const View = struct {
    entry: *Entry,
    src: LineSource,

    fn source(v: *const View) line_index.Source {
        return .{ .ctx = &v.src, .read = LineSource.read, .len = v.src.len };
    }

    pub fn adv(v: *const View) *const line_index.Advances {
        return &v.entry.adv;
    }

    pub fn index(v: *const View) *const LineIndex {
        return &v.entry.index;
    }

    pub fn posAt(v: *const View, offset: usize) line_index.Pos {
        return v.entry.index.posAt(&v.entry.adv, v.source(), offset);
    }

    pub fn xAt(v: *const View, offset: usize) f64 {
        if (offset == 0) return 0;
        return v.entry.adv.toPx(v.posAt(offset).x);
    }

    /// The char boundary nearest to `x`.
    pub fn offsetAtX(v: *const View, x: f64) usize {
        return v.entry.index.byteAtX(&v.entry.adv, v.source(), v.entry.adv.fromPx(x));
    }

    pub fn width(v: *const View) f64 {
        return v.entry.adv.toPx(v.entry.index.width());
    }

    pub fn spaceWidth(v: *const View) f64 {
        return v.entry.adv.toPx(v.entry.adv.ascii[' ']);
    }

    /// Bytes [start, end) of the whole checkpoint blocks under x0..x1: the
    /// part of the line worth shaping for a viewport there.
    pub fn blocksBetween(v: *const View, x0: f64, x1: f64) [2]usize {
        const idx = &v.entry.index;
        const a = idx.checkpointAtX(v.entry.adv.fromPx(x0));
        const b = idx.checkpointAtX(v.entry.adv.fromPx(x1)) + 1;
        return .{ idx.checkpoint(a).byte, idx.checkpoint(b).byte };
    }
};

pub const Cache = struct {
    entries: std.ArrayList(*Entry) = .empty,
    clock: u64 = 0,

    pub fn deinit(self: *Cache, gpa: Allocator) void {
        for (self.entries.items) |e| {
            e.index.deinit(gpa);
            gpa.destroy(e);
        }
        self.entries.deinit(gpa);
    }

    /// The index of buffer row `row` (a long line), built on first use.
    pub fn view(self: *Cache, gpa: Allocator, text: *text_mod.TextSystem, font: text_mod.FontId, buffer: *const Buffer, row: u32) ?View {
        const start = buffer.lineStart(row);
        const src: LineSource = .{ .buffer = buffer, .start = start, .len = buffer.lineEnd(row) - start };
        self.clock += 1;
        const entry = self.find(row) orelse self.add(gpa, row) orelse return null;
        entry.used = self.clock;
        entry.pending = 0;
        if (!entry.valid or entry.font != font or entry.scale != text.scale or entry.adv.tab_size != buffer.tab_size or
            entry.index.end.byte != src.len)
        {
            entry.font = font;
            entry.scale = text.scale;
            entry.adv = text.lineAdvances(font, buffer.tab_size);
            const v: View = .{ .entry = entry, .src = src };
            entry.index.build(gpa, &entry.adv, v.source()) catch return null;
            entry.valid = true;
        }
        return .{ .entry = entry, .src = src };
    }

    fn find(self: *Cache, row: u32) ?*Entry {
        for (self.entries.items) |e| if (e.row == row) return e;
        return null;
    }

    fn add(self: *Cache, gpa: Allocator, row: u32) ?*Entry {
        if (self.entries.items.len >= max_cached) {
            var lru: usize = 0;
            for (self.entries.items, 0..) |e, i| if (e.used < self.entries.items[lru].used) {
                lru = i;
            };
            const e = self.entries.items[lru];
            e.index.cps.clearRetainingCapacity();
            e.row = row;
            e.valid = false;
            return e;
        }
        const e = gpa.create(Entry) catch return null;
        e.* = .{ .row = row, .adv = undefined, .font = 0, .scale = 0 };
        self.entries.append(gpa, e) catch {
            gpa.destroy(e);
            return null;
        };
        return e;
    }

    /// Keeps the indexes in step with a buffer edit: an edit inside one line
    /// updates its index, rows after an edit move, anything else is rebuilt
    /// when next shown.
    pub fn applyChange(self: *Cache, gpa: Allocator, buffer: *const Buffer, change: buffer_mod.Change) void {
        const r0 = change.start_point.row;
        const r1 = change.old_end_point.row;
        for (self.entries.items) |e| {
            if (e.row < r0) continue;
            if (e.row > r1) {
                e.row = e.row - r1 + change.new_end_point.row;
                continue;
            }
            if (!e.valid) continue;
            if (r0 != r1 or change.new_end_point.row != r0 or e.pending >= max_pending_edits) {
                e.valid = false;
                continue;
            }
            e.pending += 1;
            const start = buffer.lineStart(e.row);
            const v: View = .{ .entry = e, .src = .{ .buffer = buffer, .start = start, .len = buffer.lineEnd(e.row) - start } };
            e.index.update(gpa, &e.adv, v.source(), change.start_point.column, change.old_end_point.column, change.new_end_point.column) catch {
                e.valid = false;
            };
        }
    }
};
