//! A text buffer: a Rope plus file identity, undo history and edit
//! notifications. Several editors (views) may share one buffer.
//!
//! Undo is a stack of transactions; each transaction is the list of primitive
//! replacements in the order they were applied, so undoing in reverse order is
//! always valid regardless of how edits were batched.

const std = @import("std");
const rope_mod = @import("rope.zig");
const selection = @import("selection.zig");
const platform = @import("../../framework/platform/platform.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
pub const Rope = rope_mod.Rope;
pub const Point = rope_mod.Point;
pub const Selection = selection.Selection;

pub const LineEnding = enum { lf, crlf };

/// One replacement, in the coordinates of the text before the batch.
pub const Edit = struct {
    start: usize,
    end: usize,
    text: []const u8,
};

/// Describes a primitive replacement that just happened, for listeners
/// (views transform selections, the syntax tree re-parses incrementally).
pub const Change = struct {
    start: usize,
    old_len: usize,
    new_len: usize,
    start_point: Point,
    old_end_point: Point,
    new_end_point: Point,
};

pub const EditKind = enum { typing, deleting, other };

const Record = struct {
    start: usize,
    old_text: []u8,
    new_text: []u8,
};

const Transaction = struct {
    records: std.ArrayList(Record) = .empty,
    selections_before: []Selection = &.{},
    selections_after: []Selection = &.{},
    kind: EditKind = .other,
    time: i64 = 0,

    fn deinit(self: *Transaction, gpa: Allocator) void {
        for (self.records.items) |r| {
            gpa.free(r.old_text);
            gpa.free(r.new_text);
        }
        self.records.deinit(gpa);
        gpa.free(self.selections_before);
        gpa.free(self.selections_after);
    }
};

pub const Listener = struct {
    ctx: *anyopaque,
    onChange: *const fn (ctx: *anyopaque, buffer: *Buffer, change: Change) void,
};

pub const Buffer = struct {
    gpa: Allocator,
    id: u32,
    rope: Rope,
    /// Absolute path, if the buffer is backed by a file.
    path: ?[]u8 = null,
    /// Tab title for special buffers ("*Org Agenda*").
    display_name: ?[]const u8 = null,
    /// Generated buffers refuse edits.
    read_only: bool = false,
    /// Edits never make it dirty, so closing it never asks to save (the
    /// scratch buffer, like Emacs's *scratch*), until setPath gives it a file.
    scratch: bool = false,
    /// Version counter, bumped by every change; compared with `saved_version`.
    version: u64 = 0,
    saved_version: u64 = 0,
    line_ending: LineEnding = .lf,
    tab_size: u32 = 4,
    translate_tabs_to_spaces: bool = true,

    undo_stack: std.ArrayList(Transaction) = .empty,
    redo_stack: std.ArrayList(Transaction) = .empty,
    /// Undo steps kept, the oldest dropped first (null: all). For inputs
    /// that live as long as the window (the find panel's, vim's command
    /// line), which would otherwise keep every search ever typed.
    undo_limit: ?usize = null,
    /// Whether the top of the undo stack may still absorb more typing.
    group_open: bool = false,
    /// While > 0 every edit joins one transaction (vim: an insert session undoes as a unit).
    forced_group_depth: u32 = 0,
    forced_group_started: bool = false,

    listeners: std.ArrayList(Listener) = .empty,

    pub fn initEmpty(gpa: Allocator, id: u32) !Buffer {
        return .{ .gpa = gpa, .id = id, .rope = Rope.init() };
    }

    pub fn initText(gpa: Allocator, id: u32, text: []const u8) !Buffer {
        return .{ .gpa = gpa, .id = id, .rope = try Rope.fromSlice(gpa, text) };
    }

    pub fn load(gpa: Allocator, io: Io, id: u32, path: []const u8) !Buffer {
        const raw = try std.Io.Dir.cwd().readFileAlloc(io, path, gpa, .limited(1 << 30));
        defer gpa.free(raw);
        var buf: Buffer = .{ .gpa = gpa, .id = id, .rope = Rope.init() };
        errdefer buf.deinit();
        // Valid UTF-8 with LF line endings (the common case) is used as is;
        // only files that need fixing up get a normalized copy.
        const needs_copy = std.mem.indexOfScalar(u8, raw, '\r') != null or
            std.mem.startsWith(u8, raw, "\xEF\xBB\xBF") or !std.unicode.utf8ValidateSlice(raw);
        const text = if (needs_copy) try normalizeText(gpa, raw, &buf.line_ending) else raw;
        defer if (needs_copy) gpa.free(text);
        buf.rope = try Rope.fromSlice(gpa, text);
        buf.path = try gpa.dupe(u8, path);
        buf.detectIndentation(text);
        return buf;
    }

    pub fn deinit(self: *Buffer) void {
        self.rope.deinit(self.gpa);
        if (self.path) |p| self.gpa.free(p);
        for (self.undo_stack.items) |*t| t.deinit(self.gpa);
        for (self.redo_stack.items) |*t| t.deinit(self.gpa);
        self.undo_stack.deinit(self.gpa);
        self.redo_stack.deinit(self.gpa);
        self.listeners.deinit(self.gpa);
    }

    pub fn isDirty(self: *const Buffer) bool {
        return !self.scratch and self.version != self.saved_version;
    }

    pub fn len(self: *const Buffer) usize {
        return self.rope.len();
    }

    pub fn fileName(self: *const Buffer) ?[]const u8 {
        if (self.display_name) |n| return n;
        const p = self.path orelse return null;
        return std.fs.path.basename(p);
    }

    pub fn save(self: *Buffer, io: Io) !void {
        const path = self.path orelse return error.NoPath;
        const text = try self.rope.toOwnedSlice(self.gpa);
        defer self.gpa.free(text);
        var out = text;
        var converted: ?[]u8 = null;
        defer if (converted) |cv| self.gpa.free(cv);
        if (self.line_ending == .crlf) {
            const n = std.mem.count(u8, text, "\n");
            const cv = try self.gpa.alloc(u8, text.len + n);
            var j: usize = 0;
            for (text) |ch| {
                if (ch == '\n') {
                    cv[j] = '\r';
                    j += 1;
                }
                cv[j] = ch;
                j += 1;
            }
            converted = cv;
            out = cv;
        }
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out });
        self.saved_version = self.version;
    }

    pub fn setPath(self: *Buffer, path: []const u8) !void {
        const owned = try self.gpa.dupe(u8, path);
        if (self.path) |p| self.gpa.free(p);
        self.path = owned;
        // Saved as a file (Save As, :w name), the scratch buffer is the file's.
        if (self.scratch) {
            self.scratch = false;
            self.display_name = null;
        }
    }

    pub fn addListener(self: *Buffer, listener: Listener) !void {
        try self.listeners.append(self.gpa, listener);
    }

    pub fn removeListener(self: *Buffer, ctx: *anyopaque) void {
        var i: usize = 0;
        while (i < self.listeners.items.len) {
            if (self.listeners.items[i].ctx == ctx) _ = self.listeners.swapRemove(i) else i += 1;
        }
    }

    // ---- Editing ----------------------------------------------------------

    /// Applies non-overlapping `edits` (any order) as one undoable step.
    pub fn edit(self: *Buffer, edits: []const Edit, kind: EditKind, sels_before: []const Selection, sels_after: []const Selection) !void {
        if (edits.len == 0) return;
        if (self.read_only) return error.ReadOnly;
        const sorted = try self.gpa.dupe(Edit, edits);
        defer self.gpa.free(sorted);
        std.mem.sort(Edit, sorted, {}, struct {
            fn lt(_: void, a: Edit, b: Edit) bool {
                return a.start < b.start;
            }
        }.lt);

        const now = platform.nowMs();
        const forced = self.forced_group_depth > 0 and self.forced_group_started and self.undo_stack.items.len > 0;
        const merge = forced or self.group_open and self.undo_stack.items.len > 0 and kind != .other and blk: {
            const top = &self.undo_stack.items[self.undo_stack.items.len - 1];
            break :blk top.kind == kind and now - top.time < 2000 and selectionsEql(top.selections_after, sels_before);
        };

        var tx: Transaction = if (merge) self.undo_stack.pop().? else .{
            .selections_before = try self.gpa.dupe(Selection, sels_before),
            .kind = kind,
        };
        errdefer tx.deinit(self.gpa);
        tx.time = now;

        // Apply back to front so earlier offsets stay valid.
        var i = sorted.len;
        while (i > 0) {
            i -= 1;
            const e = sorted[i];
            const record = try self.replaceRaw(e.start, e.end, e.text);
            try tx.records.append(self.gpa, record);
        }

        self.gpa.free(tx.selections_after);
        tx.selections_after = try self.gpa.dupe(Selection, sels_after);
        try self.undo_stack.append(self.gpa, tx);
        for (self.redo_stack.items) |*t| t.deinit(self.gpa);
        self.redo_stack.clearRetainingCapacity();
        if (self.undo_limit) |limit| while (self.undo_stack.items.len > limit) {
            var oldest = self.undo_stack.orderedRemove(0);
            oldest.deinit(self.gpa);
        };
        if (self.forced_group_depth > 0) self.forced_group_started = true;

        // Typing groups break at whitespace boundaries, like Sublime's word-sized undo.
        const ends_word = kind == .typing and sorted.len > 0 and sorted[0].text.len > 0 and
            std.ascii.isWhitespace(sorted[0].text[sorted[0].text.len - 1]);
        self.group_open = kind != .other and !ends_word;
    }

    /// Groups every edit until `endGroup` into a single undo step.
    pub fn beginGroup(self: *Buffer) void {
        if (self.forced_group_depth == 0) self.forced_group_started = false;
        self.forced_group_depth += 1;
    }

    pub fn endGroup(self: *Buffer) void {
        if (self.forced_group_depth > 0) self.forced_group_depth -= 1;
        if (self.forced_group_depth == 0) self.forced_group_started = false;
        self.group_open = false;
    }

    /// Ends the current undo group (cursor moved, focus changed...).
    pub fn breakUndoGroup(self: *Buffer) void {
        if (self.forced_group_depth > 0) return;
        self.group_open = false;
    }

    fn replaceRaw(self: *Buffer, start: usize, end: usize, text: []const u8) !Record {
        const old_text = try self.rope.rangeToOwnedSlice(self.gpa, start, end);
        errdefer self.gpa.free(old_text);
        const new_text = try self.gpa.dupe(u8, text);
        errdefer self.gpa.free(new_text);
        try self.applyReplace(start, end, text);
        return .{ .start = start, .old_text = old_text, .new_text = new_text };
    }

    fn applyReplace(self: *Buffer, start: usize, end: usize, text: []const u8) !void {
        const start_point = self.rope.offsetToPoint(start);
        const old_end_point = self.rope.offsetToPoint(end);
        try self.rope.replace(self.gpa, start, end, text);
        self.version += 1;
        const change: Change = .{
            .start = start,
            .old_len = end - start,
            .new_len = text.len,
            .start_point = start_point,
            .old_end_point = old_end_point,
            .new_end_point = self.rope.offsetToPoint(start + text.len),
        };
        for (self.listeners.items) |l| l.onChange(l.ctx, self, change);
    }

    /// Forgets undo and redo (generated text, like the Source Control tab's).
    pub fn clearHistory(self: *Buffer) void {
        for (self.undo_stack.items) |*t| t.deinit(self.gpa);
        for (self.redo_stack.items) |*t| t.deinit(self.gpa);
        self.undo_stack.clearRetainingCapacity();
        self.redo_stack.clearRetainingCapacity();
        self.group_open = false;
    }

    /// Returns the selections to restore, or null if there is nothing to undo.
    pub fn undo(self: *Buffer) !?[]const Selection {
        var tx = self.undo_stack.pop() orelse return null;
        errdefer tx.deinit(self.gpa);
        var i = tx.records.items.len;
        while (i > 0) {
            i -= 1;
            const r = tx.records.items[i];
            try self.applyReplace(r.start, r.start + r.new_text.len, r.old_text);
        }
        try self.redo_stack.append(self.gpa, tx);
        self.group_open = false;
        return self.redo_stack.items[self.redo_stack.items.len - 1].selections_before;
    }

    pub fn redo(self: *Buffer) !?[]const Selection {
        var tx = self.redo_stack.pop() orelse return null;
        errdefer tx.deinit(self.gpa);
        for (tx.records.items) |r| {
            try self.applyReplace(r.start, r.start + r.old_text.len, r.new_text);
        }
        try self.undo_stack.append(self.gpa, tx);
        self.group_open = false;
        return self.undo_stack.items[self.undo_stack.items.len - 1].selections_after;
    }

    // ---- Queries ----------------------------------------------------------

    pub fn lineCount(self: *const Buffer) u32 {
        return self.rope.lineCount();
    }

    pub fn lineStart(self: *const Buffer, row: u32) usize {
        return self.rope.lineStart(row);
    }

    pub fn lineEnd(self: *const Buffer, row: u32) usize {
        return self.rope.lineEnd(row);
    }

    pub fn lineLen(self: *const Buffer, row: u32) u32 {
        return self.rope.lineLen(row);
    }

    pub fn offsetToPoint(self: *const Buffer, offset: usize) Point {
        return self.rope.offsetToPoint(offset);
    }

    pub fn pointToOffset(self: *const Buffer, point: Point) usize {
        return self.rope.pointToOffset(point);
    }

    /// Copies a line (without '\n') into `out`, returning the used slice.
    pub fn lineText(self: *const Buffer, row: u32, out: *std.ArrayList(u8)) ![]const u8 {
        out.clearRetainingCapacity();
        const s = self.lineStart(row);
        const e = self.lineEnd(row);
        var it = self.rope.chunks(s, e);
        while (it.next()) |chunk| try out.appendSlice(self.gpa, chunk);
        return out.items;
    }

    pub fn textRange(self: *const Buffer, gpa: Allocator, start: usize, end: usize) ![]u8 {
        return self.rope.rangeToOwnedSlice(gpa, start, end);
    }

    fn detectIndentation(self: *Buffer, text: []const u8) void {
        var tab_lines: usize = 0;
        var space_lines: usize = 0;
        var widths = [_]usize{0} * *9;
        var prev_spaces: usize = 0;
        var lines = std.mem.splitScalar(u8, text, '\n');
        var n: usize = 0;
        while (lines.next()) |line| : (n += 1) {
            if (n > 2000) break;
            if (line.len == 0) continue;
            if (line[0] == '\t') {
                tab_lines += 1;
                continue;
            }
            var spaces: usize = 0;
            while (spaces < line.len and line[spaces] == ' ') spaces += 1;
            if (spaces == line.len) continue;
            if (spaces > prev_spaces and spaces - prev_spaces <= 8) {
                space_lines += 1;
                widths[spaces - prev_spaces] += 1;
            }
            prev_spaces = spaces;
        }
        if (tab_lines > space_lines) {
            self.translate_tabs_to_spaces = false;
        } else if (space_lines > 0) {
            self.translate_tabs_to_spaces = true;
            var best: usize = 4;
            for ([_]usize{ 2, 4, 8, 3 }) |w| {
                if (widths[w] > widths[best]) best = w;
            }
            self.tab_size = @intCast(best);
        }
    }
};

fn selectionsEql(a: []const Selection, b: []const Selection) bool {
    if (a.len != b.len) return false;
    for (a, b) |x, y| if (x.anchor != y.anchor or x.head != y.head) return false;
    return true;
}

/// CRLF → LF (remembering the style) and replaces invalid UTF-8 with U+FFFD.
pub fn normalizeText(gpa: Allocator, raw: []const u8, line_ending: *LineEnding) ![]u8 {
    var out: std.ArrayList(u8) = try .initCapacity(gpa, raw.len);
    errdefer out.deinit(gpa);
    var crlf: usize = 0;
    var lf: usize = 0;
    var i: usize = 0;
    // Skip a UTF-8 BOM.
    if (std.mem.startsWith(u8, raw, "\xEF\xBB\xBF")) i = 3;
    while (i < raw.len) {
        const b = raw[i];
        if (b == '\r' and i + 1 < raw.len and raw[i + 1] == '\n') {
            crlf += 1;
            i += 1;
            continue;
        }
        if (b == '\n') lf += 1;
        if (b < 0x80) {
            try out.append(gpa, b);
            i += 1;
            continue;
        }
        const n = std.unicode.utf8ByteSequenceLength(b) catch 0;
        if (n > 0 and i + n <= raw.len and std.unicode.utf8ValidateSlice(raw[i .. i + n])) {
            try out.appendSlice(gpa, raw[i .. i + n]);
            i += n;
        } else {
            try out.appendSlice(gpa, "\u{FFFD}");
            i += 1;
        }
    }
    // `lf` counts every newline, including those that were CRLF.
    line_ending.* = if (crlf * 2 > lf) .crlf else .lf;
    return out.toOwnedSlice(gpa);
}

test "an undo limit keeps the newest steps" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initEmpty(gpa, 0);
    defer buf.deinit();
    buf.undo_limit = 3;
    const none: []const Selection = &.{};
    for ("0123456789") |*ch| try buf.edit(&.{.{ .start = 0, .end = buf.len(), .text = ch[0..1] }}, .other, none, none);
    try std.testing.expectEqual(@as(usize, 3), buf.undo_stack.items.len);
    // The last three edits undo; the ones dropped stay.
    var undone: usize = 0;
    while (try buf.undo()) |_| undone += 1;
    try std.testing.expectEqual(@as(usize, 3), undone);
    const text = try buf.rope.toOwnedSlice(gpa);
    defer gpa.free(text);
    try std.testing.expectEqualStrings("6", text);
}
