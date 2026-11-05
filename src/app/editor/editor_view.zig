//! Painting and mouse handling for `Editor`: gutter, text with syntax
//! colors, Sublime-style selections, carets, indent guides, minimap and the
//! overlay scrollbar.

const std = @import("std");
const editor_mod = @import("editor.zig");
const movement = @import("../../model/text/movement.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const platform = @import("../../framework/platform/platform.zig");
const syntax_mod = @import("../../model/syntax/syntax.zig");
const org_src_highlight = @import("../../model/org/src_highlight.zig");
const blame_view = @import("blame_view.zig");
const long_line = @import("long_line.zig");
const line_index = @import("../../framework/text/line_index.zig");

const Editor = editor_mod.Editor;
const Selection = editor_mod.Selection;
const Scene = scene_mod.Scene;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Theme = theme_mod.Theme;
const Scanner = movement.Scanner;

const text_padding: f32 = 4;
const minimap_scale: f32 = 1.0 / 7.0;

pub fn layout(self: *Editor, bounds: Rect) void {
    self.bounds = bounds;
    const settings = self.env.settings;
    const cw = self.charWidth();
    if (self.mode == .single_line) {
        self.gutter_rect = .{ .x = bounds.x, .y = bounds.y, .w = 0, .h = bounds.h };
        self.minimap_rect = .{ .x = bounds.right(), .y = bounds.y, .w = 0, .h = bounds.h };
        self.scrollbar_rect = .{ .x = bounds.right(), .y = bounds.y, .w = 0, .h = 0 };
        self.text_rect = bounds;
        return;
    }
    var gutter_w: f32 = cw;
    if (settings.show_line_numbers and !self.plain) {
        var digits: f32 = 1;
        var n = self.buffer.lineCount();
        while (n >= 10) : (n /= 10) digits += 1;
        gutter_w = @max(digits, 3) * cw + cw * 3;
    }
    gutter_w += blame_view.columnWidth(self);
    const minimap_w: f32 = if (settings.show_minimap and !self.plain) @round(@min(120, bounds.w * 0.12)) else 0;
    self.gutter_rect = .{ .x = bounds.x, .y = bounds.y, .w = gutter_w, .h = bounds.h };
    self.minimap_rect = .{ .x = bounds.right() - minimap_w, .y = bounds.y, .w = minimap_w, .h = bounds.h };
    self.text_rect = .{ .x = bounds.x + gutter_w, .y = bounds.y, .w = @max(0, bounds.w - gutter_w - minimap_w), .h = bounds.h };
    self.scrollbar_rect = .{ .x = bounds.right() - 10, .y = bounds.y, .w = 10, .h = bounds.h };
    var wrap_cols: u32 = 0;
    if (self.wrapEnabled()) {
        // "wrap_width" forces a column; otherwise wrap at the view edge
        // (leaving room for the caret and the overlay scrollbar).
        const avail = self.text_rect.w - 2 * text_padding - cw - (if (minimap_w == 0) self.scrollbar_rect.w else 0);
        wrap_cols = if (settings.wrap_width > 0) settings.wrap_width else @intFromFloat(@max(8, @floor(avail / cw)));
        self.scroll_x = 0;
    }
    self.display.setWrap(wrap_cols, self.tabSize());
    self.clampScroll();
}

pub fn originX(self: *const Editor) f32 {
    return screenX(self, 0);
}

/// Window x of text x `x` (logical px from the text origin). Computed in
/// f64: far into a long line, x and scroll_x are both beyond f32 precision.
pub fn screenX(self: *const Editor, x: f64) f32 {
    return self.text_rect.x + text_padding + @as(f32, @floatCast(x - self.scroll_x));
}

/// Text x of window x `wx`.
pub fn textX(self: *const Editor, wx: f32) f64 {
    return @as(f64, wx - self.text_rect.x - text_padding) + self.scroll_x;
}

/// Top of display row `d`.
pub fn rowY(self: *Editor, d: u32) f32 {
    if (self.mode == .single_line) {
        // Vertically centered in the input.
        return self.text_rect.y + (self.text_rect.h - self.lineHeight()) / 2 + @as(f32, @floatFromInt(d)) * self.lineHeight();
    }
    return self.text_rect.y + @as(f32, @floatFromInt(d)) * self.lineHeight() - self.scroll_y;
}

/// Visible display rows, and the buffer rows they show.
const VisibleRange = struct {
    first_d: u32,
    last_d: u32,
    first: u32,
    last: u32,
    /// Buffer bytes that can be on screen (sorted, inclusive ends): the
    /// visible rows, minus what's left and right of the viewport on long lines.
    bytes: []const [2]usize = &.{},

    fn containsDisplay(vis: VisibleRange, d: u32) bool {
        return d >= vis.first_d and d <= vis.last_d;
    }

    /// Whether any of [a, b] is on screen (thousands of carets on one long
    /// line shouldn't all be positioned every frame).
    fn intersects(vis: VisibleRange, a: usize, b: usize) bool {
        const r = vis.bytes;
        if (r.len == 0) return true;
        var lo: usize = 0;
        var hi: usize = r.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (r[mid][1] < a) lo = mid + 1 else hi = mid;
        }
        return lo < r.len and r[lo][0] <= b;
    }
};

/// Fills `vis.bytes` (backed by `out`).
fn visibleBytes(self: *Editor, vis: *VisibleRange, out: *std.ArrayList([2]usize)) void {
    if (self.mode == .single_line) return;
    var from = self.buffer.lineStart(vis.first);
    var d = vis.first_d;
    while (d <= vis.last_d) : (d += 1) {
        const seg = self.segmentAt(d);
        if (seg.end - seg.start <= long_slice_bytes) continue;
        const ll = self.longLine(seg.row) orelse continue;
        const a, const b = rowWindow(self, seg, ll, self.buffer.lineStart(seg.row));
        // Cut out (seg.start, a) and (b, seg.end).
        if (a > seg.start + 1) {
            out.append(self.gpa, .{ from, seg.start }) catch return;
            from = a;
        }
        if (seg.end > b + 1) {
            out.append(self.gpa, .{ from, b }) catch return;
            from = seg.end;
        }
    }
    if (out.items.len == 0) return; // no long lines: everything visible counts
    out.append(self.gpa, .{ from, self.buffer.lineEnd(vis.last) + 1 }) catch return;
    vis.bytes = out.items;
}

fn visibleRange(self: *Editor) VisibleRange {
    const lh = self.lineHeight();
    const count = self.displayRowCount();
    const first: u32 = @min(count - 1, @as(u32, @intFromFloat(@max(0, @floor(self.scroll_y / lh)))));
    const last: u32 = @min(count - 1, @as(u32, @intFromFloat(@max(0, @ceil((self.scroll_y + self.text_rect.h) / lh)))));
    return .{ .first_d = first, .last_d = last, .first = self.bufferRow(first), .last = self.bufferRow(last) };
}

/// Display row of `offset`, or null when it's scrolled out of view or folded away.
fn visibleDisplayRow(self: *Editor, vis: VisibleRange, offset: usize) ?u32 {
    const row = self.buffer.offsetToPoint(offset).row;
    if (row < vis.first or row > vis.last or self.isRowHidden(row)) return null;
    const d = self.displayRowOfOffset(offset);
    return if (vis.containsDisplay(d)) d else null;
}

/// Window x where the text of segment `seg` would put its line's index 0.
fn segmentOriginX(self: *Editor, seg: Editor.Segment, layout_line: *const text_mod.LineLayout, map: []const u32) f32 {
    const ls = self.buffer.lineStart(seg.row);
    return originX(self) + self.segmentIndent(seg) - layout_line.xForIndex(map[@min(seg.start - ls, map.len - 1)]);
}

/// Cheap check for gutter fold markers: does the next non-blank line nest deeper?
fn isFoldable(self: *Editor, row: u32) bool {
    const count = self.buffer.lineCount();
    if (row + 1 >= count) return false;
    if (std.mem.eql(u8, self.buffer.language.name, "Org")) {
        const level = self.orgHeadingLevel(row) orelse return false;
        if (self.orgHeadingLevel(row + 1)) |next| return next > level;
        return true;
    }
    const indent = self.lineIndentWidth(row) orelse return false;
    var r = row + 1;
    while (r < count and r < row + 50) : (r += 1) {
        const w = self.lineIndentWidth(r) orelse continue;
        return w > indent;
    }
    return false;
}

/// Lines longer than this (bytes) are only colored where they're on screen.
const long_line_bytes = 4096;

/// Long-line segments longer than this are shaped only under the viewport.
const long_slice_bytes = 2 * line_index.block_bytes;

/// What's laid out of one display row: `layout` shapes bytes [start, end)
/// of the row's line (`map`: each byte's layout index, end included), with
/// layout x 0 at window x `x`.
const RowText = struct {
    layout: *const text_mod.LineLayout,
    text: []const u8,
    map: []const u32,
    start: usize,
    end: usize,
    x: f32,
};

/// Bytes of a long line's segment under the viewport: whole checkpoint
/// blocks, so scrolling within them reuses the shaped text.
fn rowWindow(self: *Editor, seg: Editor.Segment, ll: long_line.View, ls: usize) [2]usize {
    if (seg.end - seg.start <= long_slice_bytes) return .{ seg.start, seg.end };
    // Line x = text x - indent + x of the segment start.
    const off = ll.xAt(seg.start - ls) - self.segmentIndent(seg);
    const r = ll.blocksBetween(off + textX(self, self.text_rect.x), off + textX(self, self.text_rect.right()));
    const a = std.math.clamp(ls + r[0], seg.start, seg.end);
    return .{ a, std.math.clamp(ls + r[1], a, seg.end) };
}

/// A long line's row: only `rowWindow` is tab-expanded and shaped (in the
/// editor's scratch buffers, like `displayLine`).
fn longRowText(self: *Editor, seg: Editor.Segment, ll: long_line.View, ls: usize) RowText {
    const a, const b = rowWindow(self, seg, ll, ls);
    const pos = ll.posAt(a - ls);
    self.line_buf.resize(self.gpa, b - a) catch return emptyRowText(self, a);
    const bytes = self.buffer.rope.copyRange(a, b, self.line_buf.items);
    self.display_buf.clearRetainingCapacity();
    self.display_map.clearRetainingCapacity();
    const tab: u64 = self.tabSize();
    var col = pos.col;
    for (bytes) |ch| {
        self.display_map.append(self.gpa, @intCast(self.display_buf.items.len)) catch return emptyRowText(self, a);
        if (ch == '\t') {
            const spaces = tab - col % tab;
            self.display_buf.appendNTimes(self.gpa, ' ', @intCast(spaces)) catch return emptyRowText(self, a);
            col += spaces;
        } else {
            self.display_buf.append(self.gpa, ch) catch return emptyRowText(self, a);
            if (ch & 0xC0 != 0x80) col += 1;
        }
    }
    self.display_map.append(self.gpa, @intCast(self.display_buf.items.len)) catch return emptyRowText(self, a);
    const x = self.segmentIndent(seg) + ll.adv().toPx(pos.x) - ll.xAt(seg.start - ls);
    return .{
        .layout = self.env.text.layoutLine(self.display_buf.items, self.env.font),
        .text = self.display_buf.items,
        .map = self.display_map.items,
        .start = a,
        .end = b,
        .x = screenX(self, x),
    };
}

fn emptyRowText(self: *Editor, at: usize) RowText {
    const zero = struct {
        const map = [_]u32{0};
    };
    return .{ .layout = self.env.text.layoutLine("", self.env.font), .text = "", .map = &zero.map, .start = at, .end = at, .x = 0 };
}

/// The byte column whose display index is the last one at or before `di`.
fn colAtDisplayIndex(map: []const u32, di: usize) usize {
    var lo: usize = 0;
    var hi: usize = map.len - 1;
    while (lo < hi) {
        const mid = (lo + hi + 1) / 2;
        if (map[mid] <= di) lo = mid else hi = mid - 1;
    }
    return lo;
}

/// How a byte of text is drawn: syntax color and font style.
const Paint = struct {
    color: Color,
    style: text_mod.FontStyle = .normal,

    fn eql(a: Paint, b: Paint) bool {
        return a.style == b.style and eqlColor(a.color, b.color);
    }
};

/// Per-byte syntax colors and styles for [start, end), painted outer-to-inner.
fn syntaxColors(self: *Editor, theme: *const Theme, start: usize, end: usize, out: *std.ArrayList(?Paint)) void {
    out.clearRetainingCapacity();
    out.appendNTimes(self.gpa, null, end - start) catch return;
    var spans: std.ArrayList(syntax_mod.HighlightSpan) = .empty;
    defer spans.deinit(self.gpa);
    if (self.buffer.syntax) |*syntax| {
        syntax.highlight(&self.buffer.rope, start, end, &spans);
    } else if (self.buffer.language.custom_highlight) |f| {
        f(self.gpa, &self.buffer.rope, start, end, &theme.syntax, &spans);
        // The code in org source blocks, in its own language.
        if (self.env.registry) |reg| if (std.mem.eql(u8, self.buffer.language.name, "Org")) org_src_highlight.add(self.gpa, reg, &self.buffer.rope, start, end, &spans);
    } else return;
    for (spans.items) |span| {
        // Highlighters may return spans reaching outside the window.
        if (span.end <= start or span.start >= end) continue;
        const s = @max(span.start, start) - start;
        const e = @min(span.end, end) - start;
        if (e > s) @memset(out.items[s..e], Paint{ .color = span.color, .style = span.style });
    }
}

pub fn paint(self: *Editor, scene: *Scene, theme: *const Theme) void {
    const settings = self.env.settings;
    const text = self.env.text;
    const lh = self.lineHeight();
    const m = self.metrics();
    const cw = self.charWidth();
    const baseline_off = if (self.mode == .single_line) 2 + m.ascent else settings.line_padding_top + m.ascent;

    self.buffer.updateSyntax();
    const single = self.mode == .single_line;
    if (!single) scene.fill(self.bounds, theme.background);

    var vis = visibleRange(self);
    var vis_bytes: std.ArrayList([2]usize) = .empty;
    defer vis_bytes.deinit(self.gpa);
    visibleBytes(self, &vis, &vis_bytes);
    // Colors are computed per visible row, for its on-screen bytes only: one
    // range from the first to the last visible row would span everything
    // folded in between (a whole file under "fold all").
    var colors: std.ArrayList(?Paint) = .empty;
    defer colors.deinit(self.gpa);
    const plain: Paint = .{ .color = theme.foreground };

    const ox = originX(self);

    scene.pushClip(self.text_rect) catch return;

    // Caret rows (for the gutter and the optional line highlight).
    var caret_rows: std.AutoHashMapUnmanaged(u32, void) = .empty;
    defer caret_rows.deinit(self.gpa);
    var caret_line: [2]usize = .{ 1, 0 }; // the last caret's line, to skip repeats
    for (self.selections.items) |sel| {
        if (sel.head >= caret_line[0] and sel.head <= caret_line[1]) continue;
        const row = self.buffer.offsetToPoint(sel.head).row;
        caret_line = .{ self.buffer.lineStart(row), self.buffer.lineEnd(row) };
        caret_rows.put(self.gpa, row, {}) catch {};
    }

    if (self.row_background) |rb| if (!single) {
        var d = vis.first_d;
        while (d <= vis.last_d) : (d += 1) {
            const color = rb.color(rb.ctx, self.segmentAt(d).row, theme) orelse continue;
            scene.fill(.{ .x = self.text_rect.x, .y = rowY(self, d), .w = self.text_rect.w, .h = lh }, color);
        }
    };

    if (settings.highlight_line) {
        for (self.selections.items) |sel| {
            const d = visibleDisplayRow(self, vis, sel.head) orelse continue;
            scene.fill(.{ .x = self.text_rect.x, .y = rowY(self, d), .w = self.text_rect.w, .h = lh }, theme.line_highlight);
        }
    }

    paintSelections(self, scene, theme, vis);
    paintFindHighlights(self, scene, theme, vis);
    if (!single) paintDiagnostics(self, scene, theme, vis);

    // Text, indent guides and selected whitespace, one display row (a whole
    // line, or one soft-wrapped piece of it) at a time.
    var span_buf: std.ArrayList(text_mod.ColorSpan) = .empty;
    defer span_buf.deinit(self.gpa);
    var s = Scanner.init(&self.buffer.rope);
    var prev_indent: u32 = 0;
    var line_indent: u32 = 0;
    var d = vis.first_d;
    while (d <= vis.last_d) : (d += 1) {
        const seg = if (single) Editor.Segment{ .row = 0, .index = 0, .start = 0, .end = self.buffer.len(), .indent_cols = 0 } else self.segmentAt(d);
        const row = seg.row;
        const y = rowY(self, d);
        const ls = self.buffer.lineStart(row);
        const long = self.longLine(row);
        const rt: RowText = if (long) |ll| longRowText(self, seg, ll, ls) else blk: {
            const layout_line, const dl = self.lineLayout(row);
            break :blk .{ .layout = layout_line, .text = dl.text, .map = dl.map, .start = ls, .end = ls + dl.map.len - 1, .x = segmentOriginX(self, seg, layout_line, dl.map) };
        };
        const layout_line = rt.layout;
        const le = if (long) |ll| ls + ll.src.len else rt.end;
        const sx = rt.x;
        // The part of the segment that's laid out (on a long line, what's on screen).
        const draw_start = @max(seg.start, rt.start);
        const draw_end = @max(draw_start, @min(seg.end, rt.end));
        const idx_start = rt.map[draw_start - rt.start];
        const idx_end = rt.map[draw_end - rt.start];

        // Folded header: a "…" placeholder after the text.
        if (!single and seg.end == le and draw_end == le and self.display.folds.isFoldHeader(self.buffer, row)) {
            const fx = sx + layout_line.xForIndex(idx_end) + cw;
            const box: Rect = .{ .x = fx, .y = y + 2, .w = cw * 2.6, .h = lh - 4 };
            scene.quad(box, .{ .color = theme.find_highlight.withAlpha(0.12), .border_color = theme.gutter_foreground, .border_width = 1, .radius = 3 });
            _ = text.drawText(scene, "…", self.env.font, fx + cw * 0.8, y + baseline_off, theme.gutter_foreground_active);
        }

        // Indent guides (continuation rows repeat their line's guides).
        if (settings.draw_indent_guides and !single and !self.plain) {
            const tab = self.tabSize();
            if (seg.index == 0) {
                var indent_cols: u32 = 0;
                var blank = true;
                if (long != null) {
                    if (self.lineIndentWidth(row)) |w| {
                        indent_cols = w;
                        blank = false;
                    }
                } else for (rt.text) |ch| {
                    if (ch != ' ') {
                        blank = false;
                        break;
                    }
                    indent_cols += 1;
                }
                if (blank) indent_cols = prev_indent else prev_indent = indent_cols;
                line_indent = indent_cols;
                var level: u32 = tab;
                while (level < indent_cols + (if (blank) @as(u32, 1) else 0) and level <= indent_cols) : (level += tab) {
                    const gx = if (long) |ll| @round(screenX(self, @as(f64, @floatFromInt(level)) * ll.spaceWidth())) else @round(ox + layout_line.xForIndex(@min(level, rt.text.len)));
                    const gx_blank = @round(ox + @as(f32, @floatFromInt(level)) * cw);
                    scene.fill(.{ .x = if (long != null or level <= rt.text.len) gx else gx_blank, .y = y, .w = 1, .h = lh }, theme.indent_guide);
                }
            } else {
                var level: u32 = tab;
                while (level < line_indent and level < seg.indent_cols) : (level += tab) {
                    scene.fill(.{ .x = @round(ox + @as(f32, @floatFromInt(level)) * cw), .y = y, .w = 1, .h = lh }, theme.indent_guide);
                }
            }
        }

        // The bytes of this row that can be on screen: the segment, narrowed
        // to the horizontal viewport on long unwrapped lines (a minified
        // file's one line shouldn't be colored in full every frame).
        var win_start = draw_start;
        var win_end = draw_end;
        if (long == null and !single and seg.end - seg.start > long_line_bytes and !self.wrapEnabled()) {
            const left = colAtDisplayIndex(rt.map, layout_line.indexForX(@floatCast(@max(0, self.scroll_x - text_padding))));
            const right = colAtDisplayIndex(rt.map, layout_line.indexForX(@floatCast(self.scroll_x + self.text_rect.w))) + 2;
            win_start = @max(seg.start, ls + left);
            win_end = @max(win_start, @min(seg.end, ls + right));
        }

        // Syntax color spans in display coordinates.
        span_buf.clearRetainingCapacity();
        syntaxColors(self, theme, win_start, win_end, &colors);
        const c0 = win_start - rt.start;
        const c1 = win_end - rt.start;
        if (colors.items.len == c1 - c0) {
            var col = c0;
            while (col < c1) {
                const p = colors.items[col - c0] orelse plain;
                var end_col = col + 1;
                while (end_col < c1 and (colors.items[end_col - c0] orelse plain).eql(p)) end_col += 1;
                span_buf.append(self.gpa, .{ .end = rt.map[end_col], .color = p.color, .style = p.style }) catch break;
                col = end_col;
            }
        }
        text.drawLineRange(scene, layout_line, sx, y + baseline_off, theme.foreground, span_buf.items, idx_start, if (draw_end == le) std.math.maxInt(u32) else idx_end);

        // Whitespace markers inside selections.
        if (settings.draw_white_space != .none) {
            for (self.selections.items) |sel| {
                if (sel.isEmpty() and settings.draw_white_space == .selection) continue;
                const a = if (settings.draw_white_space == .all) win_start else @max(sel.start(), win_start);
                const b = if (settings.draw_white_space == .all) win_end else @min(sel.end(), win_end);
                if (a >= b) continue;
                var o = a;
                while (o < b) : (o += 1) {
                    const ch = s.byte(o);
                    if (ch != ' ' and ch != '\t') continue;
                    const x0 = sx + layout_line.xForIndex(rt.map[o - rt.start]);
                    const x1 = sx + layout_line.xForIndex(rt.map[o - rt.start + 1]);
                    const cy = y + lh / 2;
                    if (ch == ' ') {
                        scene.fill(.{ .x = (x0 + x1) / 2 - 1, .y = cy - 1, .w = 2, .h = 2 }, theme.whitespace);
                    } else {
                        scene.fill(.{ .x = x0 + 2, .y = cy, .w = @max(0, x1 - x0 - 4), .h = 1 }, theme.whitespace);
                    }
                }
                if (settings.draw_white_space == .all) break;
            }
        }
    }

    if (single and self.buffer.len() == 0 and self.placeholder.len > 0) {
        _ = text.drawText(scene, self.placeholder, self.env.font, ox, rowY(self, 0) + baseline_off, theme.gutter_foreground);
    }
    if (settings.match_brackets and !single) paintBracketMatches(self, scene, theme, vis);
    if (!single) blame_view.paintInline(self, scene, theme, vis.first_d, vis.last_d);
    paintCarets(self, scene, theme, vis);
    scene.popClip();
    if (single) return;

    paintGutter(self, scene, theme, vis, &caret_rows);
    blame_view.paintColumn(self, scene, theme, vis.first_d, vis.last_d);
    if (settings.show_minimap and self.minimap_rect.w > 0) paintMinimap(self, scene, theme);
    paintScrollbar(self, scene, theme);
}

fn eqlColor(a: Color, b: Color) bool {
    return a.r == b.r and a.g == b.g and a.b == b.b and a.a == b.a;
}

/// One display row's piece of a range, in window x (cut off a little past
/// the view: on a long line it can be millions of px wide).
const RowSpan = struct { d: u32, x0: f32, x1: f32 };

fn selectionRowSpans(self: *Editor, sel: Selection, vis: VisibleRange, out: *std.ArrayList(RowSpan)) void {
    out.clearRetainingCapacity();
    const first = @max(self.displayRowOfOffset(sel.start()), vis.first_d -| 1);
    const last = @min(self.displayRowOfOffset(sel.end()), vis.last_d + 1, self.displayRowCount() - 1);
    if (first > last) return;
    const nl_w = self.charWidth() * 0.6;
    var d = first;
    while (d <= last) : (d += 1) {
        const seg = self.segmentAt(d);
        const s_off = std.math.clamp(sel.start(), seg.start, seg.end);
        const e_off = std.math.clamp(sel.end(), seg.start, seg.end);
        const x0 = self.xInSegment(seg, s_off);
        var x1 = self.xInSegment(seg, e_off);
        // The newline of a selected line end (not a wrap point) shows as a sliver.
        if (sel.end() > seg.end and seg.end == self.buffer.lineEnd(seg.row)) x1 += nl_w;
        if (x1 <= x0) continue;
        const left = self.text_rect.x - 64;
        const right = self.text_rect.right() + 64;
        out.append(self.gpa, .{ .d = d, .x0 = std.math.clamp(screenX(self, x0), left, right), .x1 = std.math.clamp(screenX(self, x1), left, right) }) catch return;
    }
}

fn paintSelections(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange) void {
    const lh = self.lineHeight();
    const vis_start = self.buffer.lineStart(vis.first);
    const vis_end = self.buffer.lineEnd(vis.last);
    var spans: std.ArrayList(RowSpan) = .empty;
    defer spans.deinit(self.gpa);
    const color = if (self.focused) theme.selection else theme.inactive_selection;
    const r: f32 = 3;
    for (self.selections.items) |sel| {
        if (sel.isEmpty() or sel.end() < vis_start or sel.start() > vis_end + 1 or !vis.intersects(sel.start(), sel.end())) continue;
        selectionRowSpans(self, sel, vis, &spans);
        // Round only the corners that aren't covered by the neighbouring row,
        // so multi-line selections read as one shape like Sublime's.
        for (spans.items, 0..) |sp, i| {
            const prev: ?RowSpan = if (i > 0 and spans.items[i - 1].d + 1 == sp.d) spans.items[i - 1] else null;
            const next: ?RowSpan = if (i + 1 < spans.items.len and spans.items[i + 1].d == sp.d + 1) spans.items[i + 1] else null;
            const tl: f32 = if (prev == null or prev.?.x0 > sp.x0 or prev.?.x1 <= sp.x0) r else 0;
            const tr: f32 = if (prev == null or prev.?.x1 < sp.x1) r else 0;
            const bl: f32 = if (next == null or next.?.x0 > sp.x0 or next.?.x1 <= sp.x0) r else 0;
            const br: f32 = if (next == null or next.?.x1 < sp.x1) r else 0;
            scene.quad(.{ .x = sp.x0, .y = rowY(self, sp.d), .w = sp.x1 - sp.x0, .h = lh }, .{
                .color = color,
                .radii = .{ tl, tr, br, bl },
            });
        }
    }
}

fn paintFindHighlights(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange) void {
    if (self.highlights.items.len == 0) return;
    const lh = self.lineHeight();
    const vis_start = self.buffer.lineStart(vis.first);
    const vis_end = self.buffer.lineEnd(vis.last);
    var spans: std.ArrayList(RowSpan) = .empty;
    defer spans.deinit(self.gpa);
    for (self.highlights.items) |h| {
        if (h[1] < vis_start or h[0] > vis_end or !vis.intersects(h[0], h[1])) continue;
        selectionRowSpans(self, .range(h[0], h[1]), vis, &spans);
        for (spans.items) |sp| {
            scene.quad(.{ .x = sp.x0, .y = rowY(self, sp.d), .w = sp.x1 - sp.x0, .h = lh }, .{
                .color = theme.find_highlight,
                .border_color = theme.find_highlight_border,
                .border_width = 1,
                .radius = 2,
            });
        }
    }
}

fn diagnosticColor(theme: *const Theme, sev: @import("../../model/text/buffer.zig").Severity) Color {
    return switch (sev) {
        .err => theme.syntax.constant,
        .warning => theme.syntax.number,
        .info => theme.syntax.function,
        .hint => theme.syntax.comment,
    };
}

/// Squiggly underlines under diagnostic ranges.
fn paintDiagnostics(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange) void {
    const diags = self.buffer.diagnostics.items;
    if (diags.len == 0) return;
    const lh = self.lineHeight();
    const vis_start = self.buffer.lineStart(vis.first);
    const vis_end = self.buffer.lineEnd(vis.last);
    var spans: std.ArrayList(RowSpan) = .empty;
    defer spans.deinit(self.gpa);
    var s = Scanner.init(&self.buffer.rope);
    // Draw the least severe first so errors end up on top.
    var sev: u8 = 4;
    while (sev >= 1) : (sev -= 1) {
        for (diags) |d| {
            if (@intFromEnum(d.severity) != sev) continue;
            if (d.end < vis_start or d.start > vis_end or !vis.intersects(d.start, d.end)) continue;
            // Zero-width ranges still get one char of squiggle.
            const end = if (d.end > d.start) d.end else s.nextBoundary(d.start);
            const start = if (end == d.start and d.start > 0) s.prevBoundary(d.start) else d.start;
            selectionRowSpans(self, .range(start, end), vis, &spans);
            const color = diagnosticColor(theme, d.severity);
            for (spans.items) |sp| {
                const y = rowY(self, sp.d) + lh - 3;
                var x = sp.x0;
                var up = true;
                while (x < sp.x1) : (x += 2) {
                    scene.fill(.{ .x = x, .y = if (up) y else y + 1.5, .w = 2, .h = 1.25 }, color);
                    up = !up;
                }
            }
        }
        if (sev == 1) break;
    }
}

fn paintBracketMatches(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange) void {
    if (self.selections.items.len > 8) return;
    var s = Scanner.init(&self.buffer.rope);
    const lh = self.lineHeight();
    for (self.selections.items) |sel| {
        if (!sel.isEmpty()) continue;
        const h = sel.head;
        var at: ?usize = null;
        if (h < s.len() and (movement.isOpenBracket(s.charAt(h)) or movement.isCloseBracket(s.charAt(h)))) {
            at = h;
        } else if (h > 0 and (movement.isCloseBracket(s.charAt(h - 1)) or movement.isOpenBracket(s.charAt(h - 1)))) {
            at = h - 1;
        }
        const a = at orelse continue;
        const b = movement.matchingBracket(&s, a) orelse continue;
        for ([_]usize{ a, b }) |o| {
            const d = visibleDisplayRow(self, vis, o) orelse continue;
            const seg = self.segmentForOffset(o);
            const x0 = self.xInSegment(seg, o);
            const x1 = self.xInSegment(seg, o + 1);
            scene.fill(.{ .x = screenX(self, x0), .y = rowY(self, d) + lh - 2, .w = @floatCast(x1 - x0), .h = 1 }, theme.bracket_match);
        }
    }
}

pub fn caretVisible(self: *const Editor) bool {
    if (!self.focused) return false;
    if (!self.env.settings.caret_blink) return true;
    const elapsed = platform.nowMs() - self.caret_epoch;
    return @mod(elapsed, 1000) < 500 or elapsed < 500;
}

fn paintCarets(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange) void {
    if (self.preedit.items.len > 0) paintPreedit(self, scene, theme, vis);
    if (!caretVisible(self)) return;
    if (self.caret_override.items.len > 0) {
        for (self.caret_override.items) |o| paintCaret(self, scene, theme, vis, o);
    } else {
        for (self.selections.items) |sel| paintCaret(self, scene, theme, vis, sel.head);
    }
}

/// IME composition: drawn over the text at the caret, underlined.
fn paintPreedit(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange) void {
    const head = self.primarySelection().head;
    const d = if (self.mode == .single_line) 0 else visibleDisplayRow(self, vis, head) orelse return;
    const lh = self.lineHeight();
    const x = screenX(self, self.xForOffset(head));
    const y = rowY(self, d);
    const w = self.env.text.measure(self.preedit.items, self.env.font);
    scene.pushLayer() catch return;
    defer scene.popLayer();
    scene.fill(.{ .x = x, .y = y, .w = w + 2, .h = lh }, theme.background);
    const m = self.metrics();
    const baseline = y + (if (self.mode == .single_line) 2 else self.env.settings.line_padding_top) + m.ascent;
    _ = self.env.text.drawText(scene, self.preedit.items, self.env.font, x, baseline, theme.foreground);
    scene.fill(.{ .x = x, .y = y + lh - 2, .w = w, .h = 1 }, theme.caret);
}

fn paintCaret(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange, offset: usize) void {
    const lh = self.lineHeight();
    var s = Scanner.init(&self.buffer.rope);
    if (!vis.intersects(offset, offset)) return;
    const d = if (self.mode == .single_line) 0 else visibleDisplayRow(self, vis, offset) orelse return;
    const seg = self.segmentForOffset(offset);
    const xo = self.xInSegment(seg, offset);
    const x = screenX(self, xo);
    const y = rowY(self, d);
    if (self.block_caret) {
        const next = s.nextBoundary(offset);
        var w = self.charWidth();
        if (next > offset and s.byte(offset) != '\n') w = @floatCast(self.xInSegment(seg, next) - xo);
        scene.fill(.{ .x = x, .y = y, .w = @max(w, 2), .h = lh }, theme.caret.withAlpha(0.7));
    } else {
        scene.fill(.{ .x = x - 1, .y = y, .w = 2, .h = lh }, theme.caret);
    }
}

fn paintGutter(self: *Editor, scene: *Scene, theme: *const Theme, vis: VisibleRange, caret_rows: *std.AutoHashMapUnmanaged(u32, void)) void {
    const g = self.gutter_rect;
    scene.fill(g, theme.background);
    if (!self.env.settings.show_line_numbers or self.plain) return;
    scene.pushClip(g) catch return;
    defer scene.popClip();
    const m = self.metrics();
    const cw = self.charWidth();
    const baseline_off = self.env.settings.line_padding_top + m.ascent;
    var buf: [16]u8 = undefined;
    const hover = self.hover_gutter;
    const lh = self.lineHeight();
    // Most severe diagnostic per visible row.
    var diagnostic_rows: std.AutoHashMapUnmanaged(u32, @import("../../model/text/buffer.zig").Severity) = .empty;
    defer diagnostic_rows.deinit(self.gpa);
    for (self.buffer.diagnostics.items) |d| {
        const r = self.buffer.offsetToPoint(d.start).row;
        if (r < vis.first or r > vis.last) continue;
        const gop = diagnostic_rows.getOrPut(self.gpa, r) catch continue;
        if (!gop.found_existing or @intFromEnum(d.severity) < @intFromEnum(gop.value_ptr.*)) gop.value_ptr.* = d.severity;
    }
    var d = vis.first_d;
    while (d <= vis.last_d) : (d += 1) {
        // Wrapped continuation rows get no number.
        const seg = self.segmentAt(d);
        if (seg.index != 0) continue;
        const row = seg.row;
        const label = std.fmt.bufPrint(&buf, "{d}", .{row + 1}) catch continue;
        const w = self.env.text.measure(label, self.env.font);
        const active = caret_rows.contains(row);
        const color = if (active) theme.gutter_foreground_active else theme.gutter_foreground;
        const y = rowY(self, d);
        _ = self.env.text.drawText(scene, label, self.env.font, g.right() - cw * 2 - w, y + baseline_off, color);
        if (diagnostic_rows.get(row)) |sev| {
            scene.quad(.{ .x = g.x + 4, .y = y + lh / 2 - 3, .w = 6, .h = 6 }, .{ .color = diagnosticColor(theme, sev), .radius = 3 });
        }
        // Fold markers: always on folded headers, on hover for foldable lines.
        const folded = self.display.folds.isFoldHeader(self.buffer, row);
        if (folded or (hover and isFoldable(self, row))) {
            const glyph = if (folded) "▸" else "▾";
            const fw = self.env.text.measure(glyph, self.env.font);
            _ = self.env.text.drawText(scene, glyph, self.env.font, g.right() - cw * 1.1 - fw / 2, y + baseline_off, if (folded) theme.caret else theme.gutter_foreground);
        }
    }
}

fn minimapLineHeight(self: *const Editor) f32 {
    return @max(2, @round(self.lineHeight() * minimap_scale * 1.5));
}

fn minimapScroll(self: *Editor) f32 {
    const mlh = minimapLineHeight(self);
    const total = @as(f32, @floatFromInt(self.buffer.lineCount())) * mlh;
    const avail = self.minimap_rect.h;
    if (total <= avail) return 0;
    const max = self.maxScrollY();
    if (max <= 0) return 0;
    return (self.scroll_y / max) * (total - avail);
}

fn paintMinimap(self: *Editor, scene: *Scene, theme: *const Theme) void {
    const mm = self.minimap_rect;
    scene.fill(mm, theme.background);
    scene.pushClip(mm) catch return;
    defer scene.popClip();
    const mlh = minimapLineHeight(self);
    const mcw = @max(1, self.charWidth() * minimap_scale);
    const mscroll = minimapScroll(self);
    const count = self.buffer.lineCount();
    const first: u32 = @min(count - 1, @as(u32, @intFromFloat(@floor(mscroll / mlh))));
    const last: u32 = @min(count - 1, first + @as(u32, @intFromFloat(@ceil(mm.h / mlh))) + 1);

    var colors: std.ArrayList(?Paint) = .empty;
    defer colors.deinit(self.gpa);
    // The minimap shows only a line's first columns, so only those bytes are
    // read and colored. Short ranges are colored in one query; long lines
    // (minified files) row by row.
    const max_bytes: usize = @intFromFloat(@max(256, mm.w / mcw * 4));
    const range_start = self.buffer.lineStart(first);
    const batched = self.buffer.lineEnd(last) - range_start <= 1 << 20;
    if (batched) syntaxColors(self, theme, range_start, self.buffer.lineEnd(last), &colors);

    const tab = self.tabSize();
    var row = first;
    while (row <= last) : (row += 1) {
        const y = mm.y + @as(f32, @floatFromInt(row)) * mlh - mscroll;
        const ls = self.buffer.lineStart(row);
        const le = @min(self.buffer.lineEnd(row), ls + max_bytes);
        self.line_buf.clearRetainingCapacity();
        var chunks = self.buffer.rope.chunks(ls, le);
        while (chunks.next()) |chunk| self.line_buf.appendSlice(self.gpa, chunk) catch break;
        const line = self.line_buf.items;
        // Index of `ls` in `colors`.
        const base: usize = if (batched) ls - range_start else blk: {
            syntaxColors(self, theme, ls, le, &colors);
            break :blk 0;
        };
        if (base + line.len > colors.items.len) continue;
        var col: u32 = 0;
        var i: usize = 0;
        while (i < line.len) {
            const ch = line[i];
            if (ch == ' ') {
                col += 1;
                i += 1;
                continue;
            }
            if (ch == '\t') {
                col += tab - col % tab;
                i += 1;
                continue;
            }
            // A run of non-blank chars with one color becomes one block.
            const color = if (colors.items[base + i]) |p| p.color else theme.foreground;
            const start_col = col;
            while (i < line.len and line[i] != ' ' and line[i] != '\t') {
                const cc = if (colors.items[base + i]) |p| p.color else theme.foreground;
                if (!eqlColor(cc, color)) break;
                if (line[i] & 0xC0 != 0x80) col += 1;
                i += 1;
            }
            const x = mm.x + 4 + @as(f32, @floatFromInt(start_col)) * mcw;
            if (x > mm.right()) break;
            scene.fill(.{ .x = x, .y = y, .w = @as(f32, @floatFromInt(col - start_col)) * mcw, .h = @max(1, mlh - 1) }, color.withAlpha(0.55));
        }
    }

    // The visible region.
    try_viewport: {
        scene.pushLayer() catch break :try_viewport;
        defer scene.popLayer();
        const lh = self.lineHeight();
        const first_row: f32 = @floatFromInt(self.bufferRow(@intFromFloat(@floor(self.scroll_y / lh))));
        const vy = mm.y + first_row * mlh - mscroll;
        const vh = self.text_rect.h / lh * mlh;
        scene.fill(.{ .x = mm.x, .y = vy, .w = mm.w, .h = vh }, if (self.hover_minimap or self.drag == .minimap) theme.minimap_viewport_hover else theme.minimap_viewport);
    }
}

fn scrollbarPuck(self: *Editor) ?Rect {
    const track = self.scrollbar_rect;
    const max = self.maxScrollY();
    if (max <= 0) return null;
    const content = max + self.text_rect.h;
    const puck_h = @max(24, track.h * self.text_rect.h / content);
    const puck_y = track.y + (self.scroll_y / max) * (track.h - puck_h);
    return .{ .x = track.x + 2, .y = puck_y, .w = track.w - 4, .h = puck_h };
}

fn paintScrollbar(self: *Editor, scene: *Scene, theme: *const Theme) void {
    const puck = scrollbarPuck(self) orelse return;
    scene.pushLayer() catch return;
    defer scene.popLayer();
    const active = self.hover_scrollbar or self.drag == .scrollbar;
    scene.quad(puck, .{ .color = if (active) theme.scrollbar_puck_hover else theme.scrollbar_puck, .radius = 3 });
}

// ---- Mouse --------------------------------------------------------------

/// Buffer offset under a window position (clamped into the text).
pub fn hitTest(self: *Editor, pos: platform.Vec2) usize {
    const lh = self.lineHeight();
    if (self.mode == .single_line) return self.offsetForX(0, textX(self, pos.x));
    const rel_y = pos.y - self.text_rect.y + self.scroll_y;
    const count = self.displayRowCount();
    if (rel_y < 0) return 0;
    const row_f = @floor(rel_y / lh);
    if (row_f >= @as(f32, @floatFromInt(count))) return self.buffer.len();
    return self.offsetForDisplayRow(@intFromFloat(row_f), textX(self, pos.x));
}

fn rowAt(self: *Editor, y: f32) u32 {
    const lh = self.lineHeight();
    const rel = @max(0, y - self.text_rect.y + self.scroll_y);
    return self.bufferRow(@min(self.displayRowCount() - 1, @as(u32, @intFromFloat(@floor(rel / lh)))));
}

fn lineSelection(self: *Editor, row: u32) Selection {
    const start = self.buffer.lineStart(row);
    const end = if (row + 1 < self.buffer.lineCount()) self.buffer.lineStart(row + 1) else self.buffer.len();
    return .range(start, end);
}

/// Selection for the current drag position given the drag mode and origin.
fn dragSelection(self: *Editor, offset: usize) Selection {
    var s = Scanner.init(&self.buffer.rope);
    const origin = self.drag_origin;
    switch (self.drag) {
        .words => {
            const run = movement.classRunAt(&s, offset);
            if (offset < origin.start()) return .range(origin.end(), run[0]);
            return .range(origin.start(), @max(run[1], origin.end()));
        },
        .lines, .gutter => {
            const line = lineSelection(self, self.buffer.offsetToPoint(offset).row);
            if (line.start() < origin.start()) return .range(origin.end(), line.start());
            return .range(origin.start(), line.end());
        },
        else => return .{ .anchor = origin.anchor, .head = offset },
    }
}

fn applyDrag(self: *Editor, current: Selection) void {
    self.selections.clearRetainingCapacity();
    self.selections.appendSlice(self.gpa, self.drag_base.items) catch {};
    self.selections.append(self.gpa, current) catch {};
    self.primary = self.selections.items.len - 1;
    self.normalize();
}

/// A right click, before the workspace opens its menu, whose entries act
/// on the selection: inside a selection it stays; elsewhere in the text the
/// caret moves there, as in Sublime Text and VS Code. On the gutter,
/// minimap or scrollbar nothing moves.
fn rightClick(self: *Editor, pos: platform.Vec2) void {
    if (pos.x >= self.scrollbar_rect.x and self.scrollbar_rect.w > 0) return;
    if (self.minimap_rect.w > 0 and self.minimap_rect.contains(pos.x, pos.y)) return;
    if (self.gutter_rect.contains(pos.x, pos.y)) return;
    const offset = hitTest(self, pos);
    for (self.selections.items) |sel| {
        if (!sel.isEmpty() and sel.start() <= offset and offset <= sel.end()) return;
    }
    self.pushSelectionHistory();
    self.setSingleSelection(.caret(offset));
}

pub fn onMouseDown(self: *Editor, ev: platform.MouseEvent) void {
    self.touchCaret();
    self.buffer.breakUndoGroup();
    const pos = ev.pos;
    if (ev.button == .right) return rightClick(self, pos);
    if (ev.button != .left and ev.button != .middle) return;
    if (self.minimap_rect.w > 0 and self.minimap_rect.contains(pos.x, pos.y) and pos.x < self.scrollbar_rect.x) {
        self.drag = .minimap;
        scrollToMinimapY(self, pos.y, true);
        return;
    }
    if (scrollbarPuck(self)) |puck| {
        if (pos.x >= self.scrollbar_rect.x and pos.x <= self.scrollbar_rect.right()) {
            self.drag = .scrollbar;
            self.drag_grab_offset = if (puck.contains(pos.x, pos.y)) pos.y - puck.y else puck.h / 2;
            scrollbarDragTo(self, pos.y);
            return;
        }
    }
    if (blame_view.onMouseDown(self, pos)) return;
    self.pushSelectionHistory();
    self.drag_base.clearRetainingCapacity();
    if (ev.mods.command()) self.drag_base.appendSlice(self.gpa, self.selections.items) catch {};

    if (self.gutter_rect.contains(pos.x, pos.y) and !self.plain) {
        // The rightmost gutter column toggles folds.
        if (pos.x >= self.gutter_rect.right() - self.charWidth() * 2) {
            if (self.toggleFoldAt(rowAt(self, pos.y))) return;
        }
        const line = lineSelection(self, rowAt(self, pos.y));
        self.drag = .gutter;
        if (ev.mods.shift) {
            const p = self.primarySelection();
            self.drag_origin = .range(p.start(), p.start());
            applyDrag(self, dragSelection(self, line.start()));
        } else {
            self.drag_origin = line;
            applyDrag(self, line);
        }
        return;
    }

    const offset = hitTest(self, pos);
    if (ev.button == .middle or (ev.button == .left and ev.mods.alt)) {
        self.drag = .column;
        self.drag_origin = .caret(offset);
        self.drag_grab_offset = textX(self, pos.x);
        columnSelect(self, pos);
        return;
    }
    if (ev.button != .left) return;

    var s = Scanner.init(&self.buffer.rope);
    switch (ev.click_count) {
        1 => {
            if (ev.mods.shift and !ev.mods.command()) {
                const p = self.primarySelection();
                self.drag = .chars;
                self.drag_origin = .caret(p.anchor);
                self.drag_base.clearRetainingCapacity();
                applyDrag(self, .{ .anchor = p.anchor, .head = offset });
                return;
            }
            self.drag = .chars;
            self.drag_origin = .caret(offset);
            applyDrag(self, .caret(offset));
        },
        2 => {
            const run = movement.classRunAt(&s, offset);
            self.drag = .words;
            self.drag_origin = .range(run[0], run[1]);
            if (!ev.mods.command()) self.drag_base.clearRetainingCapacity() else if (self.drag_base.items.len > 0) _ = self.drag_base.pop();
            applyDrag(self, self.drag_origin);
        },
        else => {
            const line = lineSelection(self, self.buffer.offsetToPoint(offset).row);
            self.drag = .lines;
            self.drag_origin = line;
            if (!ev.mods.command()) self.drag_base.clearRetainingCapacity() else if (self.drag_base.items.len > 0) _ = self.drag_base.pop();
            applyDrag(self, line);
        },
    }
}

pub fn onMouseMove(self: *Editor, ev: platform.MouseMoveEvent) void {
    const pos = ev.pos;
    self.hover_minimap = self.minimap_rect.contains(pos.x, pos.y);
    self.hover_gutter = self.gutter_rect.contains(pos.x, pos.y);
    self.hover_scrollbar = self.scrollbar_rect.contains(pos.x, pos.y);
    blame_view.onMouseMove(self, pos);
    if (ev.pressed == null) return;
    switch (self.drag) {
        .none => {},
        .minimap => scrollToMinimapY(self, pos.y, false),
        .scrollbar => scrollbarDragTo(self, pos.y),
        .column => columnSelect(self, pos),
        else => {
            // Auto-scroll while dragging past the edges.
            const lh = self.lineHeight();
            if (pos.y < self.text_rect.y) self.scroll_y -= lh else if (pos.y > self.text_rect.bottom()) self.scroll_y += lh;
            self.clampScroll();
            applyDrag(self, dragSelection(self, hitTest(self, pos)));
            self.touchCaret();
        },
    }
}

pub fn onMouseUp(self: *Editor, _: platform.MouseEvent) void {
    self.drag = .none;
}

fn columnSelect(self: *Editor, pos: platform.Vec2) void {
    const start_row = self.buffer.offsetToPoint(self.drag_origin.anchor).row;
    const end_row = rowAt(self, pos.y);
    const x0 = self.drag_grab_offset;
    const x1 = textX(self, pos.x);
    self.selections.clearRetainingCapacity();
    self.selections.appendSlice(self.gpa, self.drag_base.items) catch {};
    var row = @min(start_row, end_row);
    while (row <= @max(start_row, end_row)) : (row += 1) {
        const a = self.offsetForX(row, x0);
        const b = self.offsetForX(row, x1);
        // Skip lines too short to reach the column (unless it's a caret column).
        if (@min(x0, x1) > self.lineWidth(row) + self.charWidth() and x0 != x1) continue;
        self.selections.append(self.gpa, .{ .anchor = a, .head = b }) catch {};
    }
    if (self.selections.items.len == 0) self.selections.append(self.gpa, .caret(self.drag_origin.anchor)) catch {};
    self.primary = self.selections.items.len - 1;
    self.normalize();
}

fn scrollToMinimapY(self: *Editor, y: f32, center: bool) void {
    const mlh = minimapLineHeight(self);
    const lh = self.lineHeight();
    const line = (y - self.minimap_rect.y + minimapScroll(self)) / mlh;
    _ = center;
    const row: u32 = @intFromFloat(@max(0, @min(line, @as(f32, @floatFromInt(self.buffer.lineCount() - 1)))));
    self.scroll_y = @as(f32, @floatFromInt(self.displayRow(row))) * lh - self.text_rect.h / 2;
    self.clampScroll();
}

fn scrollbarDragTo(self: *Editor, y: f32) void {
    const puck = scrollbarPuck(self) orelse return;
    const track = self.scrollbar_rect;
    const t: f32 = @floatCast((y - self.drag_grab_offset - track.y) / @max(1, track.h - puck.h));
    self.scroll_y = std.math.clamp(t, 0, 1) * self.maxScrollY();
}

pub fn onScroll(self: *Editor, ev: platform.ScrollEvent) void {
    const lh = self.lineHeight();
    var dx = ev.delta.x;
    var dy = ev.delta.y;
    if (ev.unit == .lines) {
        dx *= self.charWidth() * 6;
        dy *= lh * 3;
    }
    if (ev.mods.shift and dx == 0) {
        dx = dy;
        dy = 0;
    }
    self.scroll_y += dy;
    self.scroll_x += dx;
    self.clampScroll();
}

pub fn cursorShapeAt(self: *const Editor, pos: platform.Vec2) platform.CursorShape {
    if (self.text_rect.contains(pos.x, pos.y) and !self.scrollbar_rect.contains(pos.x, pos.y)) return .text;
    return .default;
}
