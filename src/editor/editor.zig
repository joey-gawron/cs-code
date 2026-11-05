//! The editor surface: one element that owns a rope `Buffer`, a caret, a scroll position and the
//! drawing of exactly the visible rows. This is the Zig counterpart of the Rust spike's
//! `cs-editor` element, and the reference for it is jiffy-edit's `app/editor/editor_view.zig`,
//! cut down to a single caret, no folds, no soft wrap and no syntax colors.
//!
//! Geometry. Document positions (row tops, scroll offsets, x positions on long lines) are f64.
//! Only viewport-relative values become the renderer's f32, so a row near the end of a million
//! lines, or a column deep into a 5,000,000-character line, lands on the pixel it should. Short
//! lines are shaped whole (and cached across frames by the text system); lines past
//! `long_line.threshold` are positioned from a `LineIndex` and only the checkpoint blocks under
//! the viewport are shaped.

const std = @import("std");
const platform = @import("../framework/platform/platform.zig");
const scene_mod = @import("../framework/gpu/scene.zig");
const text_mod = @import("../framework/text/text_system.zig");
const buffer_mod = @import("../model/text/buffer.zig");
const movement = @import("../model/text/movement.zig");
const long_line = @import("long_line.zig");

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;
const Selection = buffer_mod.Selection;
const Scene = scene_mod.Scene;
const Color = scene_mod.Color;
const Rect = scene_mod.Rect;

const theme = struct {
    const background = Color.hex(0x1e1e1e);
    const foreground = Color.hex(0xd4d4d4);
    const gutter_fg = Color.hex(0x858585);
    const gutter_fg_active = Color.hex(0xc6c6c6);
    const selection = Color.hex(0x264f78);
    const caret = Color.hex(0xaeafad);
    const line_highlight = Color.hex(0x282828);
};

const gutter_pad = 10;
const text_pad = 8;
const blink_ms = 500;

pub const Editor = struct {
    gpa: Allocator,
    buffer: Buffer,
    text: *text_mod.TextSystem,
    font: text_mod.FontId,
    lh: f32 = 20,
    cw: f32 = 8,
    ascent: f32 = 14,
    /// Logical window size.
    width: f32 = 0,
    height: f32 = 0,
    scroll_y: f64 = 0,
    scroll_x: f64 = 0,
    sel: Selection = Selection.caret(0),
    long: long_line.Cache = .{},
    line_buf: std.ArrayList(u8) = .empty,
    disp_buf: std.ArrayList(u8) = .empty,
    disp_map: std.ArrayList(u32) = .empty,
    blink_epoch_ms: i64 = 0,
    /// Set by anything that changes what the frame shows; the main loop clears it when it draws.
    dirty: bool = true,
    /// The caret must be scrolled into view at the next layout.
    reveal: bool = false,

    pub fn init(gpa: Allocator, buf: Buffer, text: *text_mod.TextSystem, font: text_mod.FontId) !*Editor {
        const self = try gpa.create(Editor);
        self.* = .{ .gpa = gpa, .buffer = buf, .text = text, .font = font };
        self.metricsChanged();
        self.blink_epoch_ms = platform.nowMs();
        try self.buffer.addListener(.{ .ctx = self, .onChange = onChange });
        return self;
    }

    pub fn deinit(self: *Editor) void {
        self.long.deinit(self.gpa);
        self.line_buf.deinit(self.gpa);
        self.disp_buf.deinit(self.gpa);
        self.disp_map.deinit(self.gpa);
        self.buffer.deinit();
        self.gpa.destroy(self);
    }

    pub fn metricsChanged(self: *Editor) void {
        const m = self.text.metrics(self.font);
        self.cw = m.advance;
        self.lh = @ceil(m.line_height * 1.25);
        self.ascent = m.ascent + @floor((self.lh - m.line_height) / 2);
    }

    fn onChange(ctx: *anyopaque, buf: *Buffer, change: buffer_mod.Change) void {
        const self: *Editor = @ptrCast(@alignCast(ctx));
        self.long.applyChange(self.gpa, buf, change);
    }

    // ---- Geometry -----------------------------------------------------------

    fn rows(self: *const Editor) u32 {
        return self.buffer.lineCount();
    }

    fn gutterWidth(self: *const Editor) f32 {
        var digits: u32 = 1;
        var n = self.rows();
        while (n >= 10) : (n /= 10) digits += 1;
        return @as(f32, @floatFromInt(@max(digits, 3))) * self.cw + 2 * gutter_pad;
    }

    fn textX(self: *const Editor) f32 {
        return self.gutterWidth() + text_pad;
    }

    fn textWidth(self: *const Editor) f32 {
        return @max(1, self.width - self.textX());
    }

    fn docHeight(self: *const Editor) f64 {
        return @as(f64, @floatFromInt(self.rows())) * self.lh;
    }

    fn maxScrollY(self: *const Editor) f64 {
        return @max(0, self.docHeight() - self.height + self.lh);
    }

    /// Top of `row` in window coordinates: the f64 subtraction happens before the narrowing.
    fn rowTop(self: *const Editor, row: u32) f32 {
        return @floatCast(@as(f64, @floatFromInt(row)) * self.lh - self.scroll_y);
    }

    fn visibleRows(self: *const Editor) [2]u32 {
        const n = self.rows();
        const first: u32 = @intFromFloat(@min(@as(f64, @floatFromInt(n - 1)), @max(0, @floor(self.scroll_y / self.lh))));
        const last: u32 = @intFromFloat(@min(@as(f64, @floatFromInt(n - 1)), @max(0, @floor((self.scroll_y + self.height) / self.lh))));
        return .{ first, last };
    }

    pub fn resize(self: *Editor, width: f32, height: f32) void {
        self.width = width;
        self.height = height;
        self.clampScroll();
        self.dirty = true;
    }

    fn clampScroll(self: *Editor) void {
        self.scroll_y = std.math.clamp(self.scroll_y, 0, self.maxScrollY());
        self.scroll_x = @max(0, self.scroll_x);
    }

    // ---- Row layout -----------------------------------------------------------

    /// One buffer line laid out for drawing and hit-testing. For a long line only a window of it
    /// is shaped (`a`..`b`, line-relative bytes) and `x0` is where the window starts on the line.
    const RowView = struct {
        row: u32,
        start: usize,
        len: usize,
        layout: *const text_mod.LineLayout,
        /// Display index of every line byte (null: identity, the line has no tabs).
        map: ?[]const u32,
        a: usize,
        b: usize,
        x0: f64,
        long: ?long_line.View,

        fn dispIndex(rv: *const RowView, byte: usize) usize {
            const rel = byte - rv.a;
            return if (rv.map) |m| m[rel] else rel;
        }

        /// The x of line byte `byte` on the line (logical px), f64 on long lines.
        fn xOf(rv: *const RowView, byte: usize) f64 {
            if (rv.long) |ll| return ll.xAt(byte);
            return rv.layout.xForIndex(rv.dispIndex(byte));
        }

        /// The char boundary nearest to line x `x`.
        fn byteAtX(rv: *const RowView, x: f64) usize {
            if (rv.long) |ll| return ll.offsetAtX(x);
            const di = rv.layout.indexForX(@floatCast(x - rv.x0));
            const m = rv.map orelse return di;
            // Last byte whose display index is at or before di; a tab spans several indexes.
            var lo: usize = 0;
            var hi: usize = m.len - 1;
            while (lo < hi) {
                const mid = (lo + hi + 1) / 2;
                if (m[mid] <= di) lo = mid else hi = mid - 1;
            }
            if (lo + 1 < m.len and di - m[lo] > m[lo + 1] - di) lo += 1;
            return lo;
        }

        fn width(rv: *const RowView) f64 {
            if (rv.long) |ll| return ll.width();
            return rv.layout.width;
        }
    };

    fn rowView(self: *Editor, row: u32, want_window: bool) RowView {
        const start = self.buffer.lineStart(row);
        const end = self.buffer.lineEnd(row);
        const len = end - start;
        if (len > long_line.threshold) {
            if (self.long.view(self.gpa, self.text, self.font, &self.buffer, row)) |ll| {
                var a: usize = 0;
                var b: usize = 0;
                if (want_window) {
                    const r = ll.blocksBetween(self.scroll_x, self.scroll_x + self.textWidth());
                    a = r[0];
                    b = r[1];
                }
                const pos = ll.posAt(a);
                const has_tab = self.expand(start + a, start + b, pos.col);
                return .{
                    .row = row,
                    .start = start,
                    .len = len,
                    .layout = self.text.layoutLine(self.disp_buf.items, self.font),
                    .map = if (has_tab) self.disp_map.items else null,
                    .a = a,
                    .b = b,
                    .x0 = ll.entry.adv.toPx(pos.x),
                    .long = ll,
                };
            }
        }
        const has_tab = self.expand(start, end, 0);
        return .{
            .row = row,
            .start = start,
            .len = len,
            .layout = self.text.layoutLine(self.disp_buf.items, self.font),
            .map = if (has_tab) self.disp_map.items else null,
            .a = 0,
            .b = len,
            .x0 = 0,
            .long = null,
        };
    }

    /// Fills `disp_buf` with buffer bytes [start, end) with tabs expanded to spaces, `disp_map`
    /// with each byte's display index (end included). `col` is the display column of `start`.
    /// Returns whether there was a tab.
    fn expand(self: *Editor, start: usize, end: usize, col0: u64) bool {
        self.disp_buf.clearRetainingCapacity();
        self.disp_map.clearRetainingCapacity();
        self.line_buf.resize(self.gpa, end - start) catch return false;
        const bytes = self.buffer.rope.copyRange(start, end, self.line_buf.items);
        if (std.mem.indexOfScalar(u8, bytes, '\t') == null) {
            self.disp_buf.appendSlice(self.gpa, bytes) catch {};
            return false;
        }
        const tab: u64 = @max(1, self.buffer.tab_size);
        var col = col0;
        self.disp_map.ensureTotalCapacity(self.gpa, bytes.len + 1) catch return true;
        for (bytes) |ch| {
            self.disp_map.appendAssumeCapacity(@intCast(self.disp_buf.items.len));
            if (ch == '\t') {
                const n = tab - col % tab;
                self.disp_buf.appendNTimes(self.gpa, ' ', @intCast(n)) catch {};
                col += n;
            } else {
                self.disp_buf.append(self.gpa, ch) catch {};
                if (ch & 0xC0 != 0x80) col += 1;
            }
        }
        self.disp_map.appendAssumeCapacity(@intCast(self.disp_buf.items.len));
        return true;
    }

    // ---- Painting ---------------------------------------------------------------

    pub fn paint(self: *Editor, scene: *Scene, now_ms: i64) void {
        self.clampScroll();
        if (self.reveal) self.revealCaret();
        const full: Rect = .{ .x = 0, .y = 0, .w = self.width, .h = self.height };
        scene.fill(full, theme.background);
        const tx = self.textX();
        const text_rect: Rect = .{ .x = tx - text_pad / 2, .y = 0, .w = self.width - tx + text_pad / 2, .h = self.height };
        scene.pushClip(text_rect) catch return;

        const vis = self.visibleRows();
        const caret_row = self.buffer.offsetToPoint(self.sel.head).row;
        const sel_start = self.sel.start();
        const sel_end = self.sel.end();
        const sel_empty = self.sel.isEmpty();
        var row = vis[0];
        while (row <= vis[1]) : (row += 1) {
            const y = self.rowTop(row);
            if (sel_empty and row == caret_row) scene.fill(.{ .x = text_rect.x, .y = y, .w = text_rect.w, .h = self.lh }, theme.line_highlight);
            const rv = self.rowView(row, true);
            const x0: f32 = @floatCast(@as(f64, tx) - self.scroll_x + rv.x0);
            if (!sel_empty and sel_end >= rv.start and sel_start <= rv.start + rv.len) self.paintSelection(scene, rv, y, sel_start, sel_end);
            self.text.drawLine(scene, rv.layout, x0, y + self.ascent, theme.foreground, &.{});
        }
        if (now_ms - self.blink_epoch_ms < 0 or @mod(@divFloor(now_ms - self.blink_epoch_ms, blink_ms), 2) == 0) self.paintCaret(scene);
        scene.popClip();
        self.paintGutter(scene, vis, caret_row);
    }

    fn paintSelection(self: *Editor, scene: *Scene, rv: RowView, y: f32, sel_start: usize, sel_end: usize) void {
        const tx: f64 = self.textX();
        const left_edge = tx - self.scroll_x;
        // Line x of both ends; an end outside the line extends to the line's edge.
        const x_start: f64 = if (sel_start <= rv.start) 0 else rv.xOf(sel_start - rv.start);
        const past_end = sel_end > rv.start + rv.len;
        const x_end: f64 = if (past_end) rv.width() + self.cw else rv.xOf(sel_end - rv.start);
        const a = std.math.clamp(left_edge + x_start, tx, @as(f64, self.width));
        const b = std.math.clamp(left_edge + x_end, tx, @as(f64, self.width));
        if (b > a) scene.fill(.{ .x = @floatCast(a), .y = y, .w = @floatCast(b - a), .h = self.lh }, theme.selection);
    }

    fn caretX(self: *Editor) f64 {
        const row = self.buffer.offsetToPoint(self.sel.head).row;
        const rv = self.rowView(row, false);
        return rv.xOf(self.sel.head - rv.start);
    }

    fn paintCaret(self: *Editor, scene: *Scene) void {
        const row = self.buffer.offsetToPoint(self.sel.head).row;
        const rv = self.rowView(row, false);
        const x: f32 = @floatCast(@as(f64, self.textX()) - self.scroll_x + rv.xOf(self.sel.head - rv.start));
        scene.fill(.{ .x = x, .y = self.rowTop(row), .w = 2, .h = self.lh }, theme.caret);
    }

    fn paintGutter(self: *Editor, scene: *Scene, vis: [2]u32, caret_row: u32) void {
        const gw = self.gutterWidth();
        scene.fill(.{ .x = 0, .y = 0, .w = gw, .h = self.height }, theme.background);
        scene.pushClip(.{ .x = 0, .y = 0, .w = gw, .h = self.height }) catch return;
        defer scene.popClip();
        var buf: [16]u8 = undefined;
        var row = vis[0];
        while (row <= vis[1]) : (row += 1) {
            const label = std.fmt.bufPrint(&buf, "{d}", .{row + 1}) catch continue;
            const layout = self.text.layoutLine(label, self.font);
            const color = if (row == caret_row) theme.gutter_fg_active else theme.gutter_fg;
            self.text.drawLine(scene, layout, gw - gutter_pad - layout.width, self.rowTop(row) + self.ascent, color, &.{});
        }
    }

    pub fn caretRect(self: *Editor) platform.Rect {
        const row = self.buffer.offsetToPoint(self.sel.head).row;
        const x = @as(f64, self.textX()) - self.scroll_x + self.caretX();
        return .{ .x = @floatCast(std.math.clamp(x, 0, 1e6)), .y = self.rowTop(row), .w = 2, .h = self.lh };
    }

    // ---- Scrolling -----------------------------------------------------------

    pub fn scrollBy(self: *Editor, dx: f64, dy: f64) void {
        self.scroll_y += dy;
        self.scroll_x += dx;
        self.clampScroll();
        self.dirty = true;
    }

    fn revealCaret(self: *Editor) void {
        self.reveal = false;
        const p = self.buffer.offsetToPoint(self.sel.head);
        const top = @as(f64, @floatFromInt(p.row)) * self.lh;
        if (top < self.scroll_y) self.scroll_y = top;
        if (top + self.lh > self.scroll_y + self.height) self.scroll_y = top + self.lh - self.height;
        self.clampScroll();
        const x = self.caretX();
        const w: f64 = self.textWidth();
        if (x < self.scroll_x) self.scroll_x = @max(0, x - w / 4);
        if (x + 2 * self.cw > self.scroll_x + w) self.scroll_x = x + 2 * self.cw - w + w / 4;
        self.clampScroll();
    }

    // ---- Caret movement ----------------------------------------------------------

    fn setCaret(self: *Editor, head: usize, extend: bool, keep_goal: bool) void {
        const goal = if (keep_goal) self.sel.goal_x else null;
        self.sel = if (extend) Selection.range(self.sel.anchor, head) else Selection.caret(head);
        self.sel.goal_x = goal;
        self.touch();
    }

    /// Anything the user did: the caret shows solid for a moment and the caret is revealed.
    fn touch(self: *Editor) void {
        self.blink_epoch_ms = platform.nowMs();
        self.reveal = true;
        self.dirty = true;
    }

    fn moveVertical(self: *Editor, delta: i64, extend: bool) void {
        const p = self.buffer.offsetToPoint(self.sel.head);
        const rv = self.rowView(p.row, false);
        const goal = self.sel.goal_x orelse rv.xOf(self.sel.head - rv.start);
        const target: i64 = @as(i64, p.row) + delta;
        const last: i64 = @as(i64, self.rows()) - 1;
        if (target < 0) return self.setCaret(0, extend, false);
        if (target > last) return self.setCaret(self.buffer.len(), extend, false);
        const row: u32 = @intCast(target);
        const tv = self.rowView(row, false);
        const head = tv.start + tv.byteAtX(goal);
        self.setCaret(head, extend, true);
        self.sel.goal_x = goal;
    }

    fn page(self: *const Editor) i64 {
        return @max(1, @as(i64, @intFromFloat(self.height / self.lh)) - 1);
    }

    fn lineBounds(self: *Editor, offset: usize) [2]usize {
        const row = self.buffer.offsetToPoint(offset).row;
        return .{ self.buffer.lineStart(row), self.buffer.lineEnd(row) };
    }

    pub fn gotoLine(self: *Editor, line: usize) void {
        const row: u32 = @intCast(@min(line -| 1, self.rows() - 1));
        self.setCaret(self.buffer.lineStart(row), false, false);
        // Centre it, as VS Code's go-to-line does.
        const top = @as(f64, @floatFromInt(row)) * self.lh;
        self.scroll_y = top - (self.height - self.lh) / 2;
        self.reveal = false;
        self.clampScroll();
    }

    pub fn click(self: *Editor, x: f32, y: f32, extend: bool) void {
        const row_f = @floor((@as(f64, y) + self.scroll_y) / self.lh);
        const row: u32 = @intFromFloat(std.math.clamp(row_f, 0, @as(f64, @floatFromInt(self.rows() - 1))));
        const rv = self.rowView(row, false);
        const lx = @as(f64, x) - self.textX() + self.scroll_x;
        self.setCaret(rv.start + rv.byteAtX(lx), extend, false);
        self.reveal = false;
    }

    // ---- Keys and text --------------------------------------------------------------

    pub fn onKey(self: *Editor, ev: platform.KeyEvent) !void {
        const m = ev.mods;
        const extend = m.shift;
        const cmd = m.command();
        var sc = movement.Scanner.init(&self.buffer.rope);
        const head = self.sel.head;
        switch (ev.key) {
            .named => |k| switch (k) {
                .left => {
                    if (!extend and !self.sel.isEmpty()) return self.setCaret(self.sel.start(), false, false);
                    if (cmd) return self.setCaret(self.lineBounds(head)[0], extend, false);
                    self.setCaret(if (m.alt) movement.prevWordStart(&sc, head) else sc.prevBoundary(head), extend, false);
                },
                .right => {
                    if (!extend and !self.sel.isEmpty()) return self.setCaret(self.sel.end(), false, false);
                    if (cmd) return self.setCaret(self.lineBounds(head)[1], extend, false);
                    self.setCaret(if (m.alt) movement.nextWordEnd(&sc, head) else sc.nextBoundary(head), extend, false);
                },
                .up => if (cmd) self.setCaret(0, extend, false) else self.moveVertical(-1, extend),
                .down => if (cmd) self.setCaret(self.buffer.len(), extend, false) else self.moveVertical(1, extend),
                .home => self.setCaret(if (cmd) 0 else self.lineBounds(head)[0], extend, false),
                .end => self.setCaret(if (cmd) self.buffer.len() else self.lineBounds(head)[1], extend, false),
                .page_up => {
                    self.scroll_y -= @as(f64, @floatFromInt(self.page())) * self.lh;
                    self.moveVertical(-self.page(), extend);
                },
                .page_down => {
                    self.scroll_y += @as(f64, @floatFromInt(self.page())) * self.lh;
                    self.moveVertical(self.page(), extend);
                },
                .enter => try self.newline(),
                .tab => try self.insert("    ", .typing),
                .backspace => try self.deleteBackward(&sc, m.alt),
                .delete => try self.deleteForward(&sc),
                else => {},
            },
            .char => |ch| {
                if (cmd and !m.alt) switch (ch) {
                    'z' => return self.undoRedo(m.shift),
                    'a' => {
                        self.sel = Selection.range(0, self.buffer.len());
                        return self.touch();
                    },
                    else => {},
                };
                if (ev.text.len > 0 and !cmd and !m.ctrl) try self.insert(ev.text, .typing);
            },
        }
    }

    pub fn onText(self: *Editor, text: []const u8) !void {
        try self.insert(text, .typing);
    }

    fn undoRedo(self: *Editor, redo: bool) !void {
        const sels = (if (redo) try self.buffer.redo() else try self.buffer.undo()) orelse return;
        if (sels.len > 0) self.sel = Selection.range(@min(sels[0].anchor, self.buffer.len()), @min(sels[0].head, self.buffer.len()));
        self.touch();
    }

    fn replace(self: *Editor, start: usize, end: usize, text: []const u8, kind: buffer_mod.EditKind) !void {
        const before = [_]Selection{self.sel};
        const after = [_]Selection{Selection.caret(start + text.len)};
        try self.buffer.edit(&.{.{ .start = start, .end = end, .text = text }}, kind, &before, &after);
        self.sel = after[0];
        self.touch();
    }

    fn insert(self: *Editor, text: []const u8, kind: buffer_mod.EditKind) !void {
        try self.replace(self.sel.start(), self.sel.end(), text, kind);
    }

    /// Enter: a line break followed by the current line's indentation (bounded, so a long line
    /// costs nothing extra).
    fn newline(self: *Editor) !void {
        var out: [66]u8 = undefined;
        out[0] = '\n';
        var n: usize = 1;
        const bounds = self.lineBounds(self.sel.start());
        var sc = movement.Scanner.init(&self.buffer.rope);
        var o = bounds[0];
        while (o < self.sel.start() and n < out.len) : (o += 1) {
            const ch = sc.byte(o);
            if (ch != ' ' and ch != '\t') break;
            out[n] = ch;
            n += 1;
        }
        try self.insert(out[0..n], .other);
    }

    fn deleteBackward(self: *Editor, sc: *movement.Scanner, word: bool) !void {
        if (!self.sel.isEmpty()) return self.replace(self.sel.start(), self.sel.end(), "", .deleting);
        if (self.sel.head == 0) return;
        const start = if (word) movement.prevWordStart(sc, self.sel.head) else sc.prevBoundary(self.sel.head);
        try self.replace(start, self.sel.head, "", .deleting);
    }

    fn deleteForward(self: *Editor, sc: *movement.Scanner) !void {
        if (!self.sel.isEmpty()) return self.replace(self.sel.start(), self.sel.end(), "", .deleting);
        if (self.sel.head >= self.buffer.len()) return;
        try self.replace(self.sel.head, sc.nextBoundary(self.sel.head), "", .deleting);
    }

    /// One line of state for the demo log, so a run can be checked without looking at it.
    pub fn describe(self: *Editor, w: *std.Io.Writer) !void {
        const p = self.buffer.offsetToPoint(self.sel.head);
        try w.print("line {d} byte-col {d}, caret at byte {d}, scroll {d:.1}/{d:.1}, {d} lines, {d} bytes", .{
            p.row + 1, p.column, self.sel.head, self.scroll_y, self.scroll_x, self.rows(), self.buffer.len(),
        });
    }
};
