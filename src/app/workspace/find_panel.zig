//! The bottom find / replace panel (ctrl+f, ctrl+h, ctrl+i) with regex,
//! case, whole-word, wrap and in-selection toggles. Matches are highlighted
//! live in the target editor; "Find All" turns them into selections.
//! ctrl+shift+f switches it to Find in Files (Find / Where / Replace rows);
//! the search itself runs in the workspace.

const std = @import("std");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const movement = @import("../../model/text/movement.zig");
const regex_mod = @import("../../framework/util/regex.zig");
const ui = @import("../../framework/ui/ui.zig");

const Allocator = std.mem.Allocator;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Selection = editor_mod.Selection;
const Rect = ui.Rect;

pub const Toggle = enum { regex, case_sensitive, whole_word, wrap, in_selection, highlight };

pub const Button = enum { find, find_prev, find_all, replace, replace_all, find_in_files, replace_in_files };

pub const Mode = enum { buffer, files };

const max_matches = 100_000;
/// Undo steps kept by each of the panel's inputs.
const input_undo_limit = 100;

pub const FindPanel = struct {
    gpa: Allocator,
    find_buffer: *Buffer,
    find_input: *Editor,
    replace_buffer: *Buffer,
    replace_input: *Editor,
    where_buffer: *Buffer,
    where_input: *Editor,
    mode: Mode = .buffer,
    show_replace: bool = false,
    focus_replace: bool = false,
    focus_where: bool = false,
    incremental: bool = false,
    toggles: std.EnumArray(Toggle, bool) = .init(.{
        .regex = false,
        .case_sensitive = false,
        .whole_word = false,
        .wrap = true,
        .in_selection = false,
        .highlight = true,
    }),

    matches: std.ArrayList([2]usize) = .empty,
    current: ?usize = null,
    regex_error: bool = false,
    searched_query_version: u64 = std.math.maxInt(u64),
    searched_buffer: ?*Buffer = null,
    searched_buffer_version: u64 = std.math.maxInt(u64),
    searched_toggles: u8 = 0,
    /// Selection the search is restricted to (in-selection mode).
    scope: ?[2]usize = null,

    // Geometry from the last paint.
    rect: Rect = .{},
    toggle_rects: std.EnumArray(Toggle, Rect) = .initFill(.{}),
    button_rects: std.EnumArray(Button, Rect) = .initFill(.{}),
    find_rect: Rect = .{},
    replace_rect: Rect = .{},
    where_rect: Rect = .{},

    pub fn create(gpa: Allocator, env: *editor_mod.Env) !*FindPanel {
        const self = try gpa.create(FindPanel);
        errdefer gpa.destroy(self);
        const fb = try gpa.create(Buffer);
        fb.* = try Buffer.initEmpty(gpa, 0);
        const rb = try gpa.create(Buffer);
        rb.* = try Buffer.initEmpty(gpa, 0);
        const wb = try gpa.create(Buffer);
        wb.* = try Buffer.initEmpty(gpa, 0);
        // The panel lives as long as the window: every search typed (or
        // taken from the selection) would stay in its undo history.
        for ([_]*Buffer{ fb, rb, wb }) |b| b.undo_limit = input_undo_limit;
        self.* = .{
            .gpa = gpa,
            .find_buffer = fb,
            .find_input = try Editor.create(gpa, env, fb),
            .replace_buffer = rb,
            .replace_input = try Editor.create(gpa, env, rb),
            .where_buffer = wb,
            .where_input = try Editor.create(gpa, env, wb),
        };
        self.where_input.mode = .single_line;
        self.where_input.placeholder = "Where: <open folders>, <open files>, /path, *.zig, -build/";
        self.find_input.mode = .single_line;
        self.find_input.placeholder = "Find";
        self.replace_input.mode = .single_line;
        self.replace_input.placeholder = "Replace";
        return self;
    }

    pub fn destroy(self: *FindPanel) void {
        self.find_input.destroy();
        self.replace_input.destroy();
        self.where_input.destroy();
        self.find_buffer.deinit();
        self.replace_buffer.deinit();
        self.where_buffer.deinit();
        self.gpa.destroy(self.find_buffer);
        self.gpa.destroy(self.replace_buffer);
        self.gpa.destroy(self.where_buffer);
        self.matches.deinit(self.gpa);
        self.gpa.destroy(self);
    }

    pub fn activeInput(self: *FindPanel) *Editor {
        if (self.mode == .files and self.focus_where) return self.where_input;
        return if (self.show_replace and self.focus_replace) self.replace_input else self.find_input;
    }

    /// Tab: find → where → replace (files mode) or find ↔ replace.
    pub fn cycleFocus(self: *FindPanel) void {
        if (self.mode == .files) {
            if (self.focus_where) {
                self.focus_where = false;
                self.focus_replace = true;
            } else if (self.focus_replace) {
                self.focus_replace = false;
            } else self.focus_where = true;
        } else if (self.show_replace) self.focus_replace = !self.focus_replace;
    }

    pub fn height(self: *const FindPanel) f32 {
        if (self.mode == .files) return 108;
        return if (self.show_replace) 74 else 40;
    }

    fn toggleBits(self: *const FindPanel) u8 {
        var bits: u8 = 0;
        inline for (@typeInfo(Toggle).@"enum".field_values, 0..) |v, i| {
            if (self.toggles.get(@fromBackingInt(v))) bits |= 1 << i;
        }
        return bits;
    }

    pub fn flip(self: *FindPanel, t: Toggle) void {
        self.toggles.set(t, !self.toggles.get(t));
    }

    /// Recomputes matches in `target` if the query, options or text changed.
    pub fn update(self: *FindPanel, target: *Editor) void {
        const bits = self.toggleBits();
        if (self.searched_query_version == self.find_buffer.version and self.searched_buffer == target.buffer and
            self.searched_buffer_version == target.buffer.version and self.searched_toggles == bits) return;
        self.searched_query_version = self.find_buffer.version;
        self.searched_buffer = target.buffer;
        self.searched_buffer_version = target.buffer.version;
        self.searched_toggles = bits;
        self.search(target);
    }

    fn search(self: *FindPanel, target: *Editor) void {
        self.matches.clearRetainingCapacity();
        self.current = null;
        self.regex_error = false;
        defer self.publishHighlights(target);
        const needle = self.find_input.contents(self.gpa) catch return;
        defer self.gpa.free(needle);
        if (needle.len == 0) return;
        const rope = &target.buffer.rope;
        const lo: usize = if (self.scope) |s| @min(s[0], rope.len()) else 0;
        const hi: usize = if (self.scope) |s| @min(s[1], rope.len()) else rope.len();
        const opts: SearchOptions = .{
            .case_sensitive = self.toggles.get(.case_sensitive),
            .whole_word = self.toggles.get(.whole_word),
        };
        if (self.toggles.get(.regex)) {
            var re = regex_mod.Regex.compile(self.gpa, needle, .{ .case_insensitive = !opts.case_sensitive }) catch {
                self.regex_error = true;
                return;
            };
            defer re.deinit();
            findRegex(self.gpa, &self.matches, rope, lo, hi, &re, opts) catch {};
        } else {
            findPlain(self.gpa, &self.matches, rope, lo, hi, needle, opts) catch {};
        }
    }

    fn publishHighlights(self: *FindPanel, target: *Editor) void {
        target.highlights.clearRetainingCapacity();
        if (!self.toggles.get(.highlight)) return;
        target.highlights.appendSlice(target.gpa, self.matches.items) catch {};
    }

    /// Selects the next (or previous) match relative to the primary caret.
    pub fn findNext(self: *FindPanel, target: *Editor, forward: bool) bool {
        self.update(target);
        const n = self.matches.items.len;
        if (n == 0) return false;
        const sel = target.primarySelection();
        var idx: ?usize = null;
        if (forward) {
            for (self.matches.items, 0..) |m, i| if (m[0] >= sel.end() and !(m[0] == sel.start() and m[1] == sel.end())) {
                idx = i;
                break;
            };
            if (idx == null and self.toggles.get(.wrap)) idx = 0;
        } else {
            var i = n;
            while (i > 0) {
                i -= 1;
                if (self.matches.items[i][1] <= sel.start() and !(self.matches.items[i][0] == sel.start())) {
                    idx = i;
                    break;
                }
            }
            if (idx == null and self.toggles.get(.wrap)) idx = n - 1;
        }
        const i = idx orelse return false;
        self.current = i;
        const m = self.matches.items[i];
        target.pushSelectionHistory();
        target.setSingleSelection(.range(m[0], m[1]));
        target.afterCommand(true);
        return true;
    }

    /// Incremental find: select the first match at/after the search origin.
    pub fn findFrom(self: *FindPanel, target: *Editor, origin: usize) void {
        self.update(target);
        for (self.matches.items, 0..) |m, i| if (m[0] >= origin) {
            self.current = i;
            target.setSingleSelection(.range(m[0], m[1]));
            target.afterCommand(false);
            return;
        };
        if (self.matches.items.len > 0) {
            const m = self.matches.items[0];
            self.current = 0;
            target.setSingleSelection(.range(m[0], m[1]));
            target.afterCommand(false);
        }
    }

    pub fn findAll(self: *FindPanel, target: *Editor) void {
        self.update(target);
        if (self.matches.items.len == 0) return;
        const sels = self.gpa.alloc(Selection, self.matches.items.len) catch return;
        defer self.gpa.free(sels);
        for (self.matches.items, sels) |m, *s| s.* = .range(m[0], m[1]);
        target.pushSelectionHistory();
        target.setSelections(sels);
        target.afterCommand(false);
    }

    /// Replacement text for one match, expanding $1 / \1 groups in regex mode.
    fn replacementFor(self: *FindPanel, alloc: Allocator, target: *Editor, m: [2]usize, template: []const u8) ![]const u8 {
        if (!self.toggles.get(.regex)) return template;
        const needle = try self.find_input.contents(alloc);
        var re = try regex_mod.Regex.compile(self.gpa, needle, .{ .case_insensitive = !self.toggles.get(.case_sensitive) });
        defer re.deinit();
        const p = target.buffer.offsetToPoint(m[0]);
        const ls = target.buffer.lineStart(p.row);
        const line = try target.buffer.textRange(alloc, ls, target.buffer.lineEnd(p.row));
        const rm = re.matchAt(line, m[0] - ls) orelse return template;
        var out: std.ArrayList(u8) = .empty;
        var i: usize = 0;
        while (i < template.len) : (i += 1) {
            const ch = template[i];
            if ((ch == '$' or ch == '\\') and i + 1 < template.len and std.ascii.isDigit(template[i + 1])) {
                const g = template[i + 1] - '0';
                i += 1;
                if (g == 0) {
                    try out.appendSlice(alloc, line[rm.start..rm.end]);
                } else if (g <= 9) {
                    if (rm.groups[g - 1]) |grp| try out.appendSlice(alloc, line[grp[0]..grp[1]]);
                }
            } else if (ch == '\\' and i + 1 < template.len and template[i + 1] == 'n') {
                try out.append(alloc, '\n');
                i += 1;
            } else try out.append(alloc, ch);
        }
        return out.items;
    }

    pub fn replaceNext(self: *FindPanel, target: *Editor) void {
        self.update(target);
        const sel = target.primarySelection();
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        // If the current selection is a match, replace it; then move on.
        for (self.matches.items) |m| {
            if (m[0] == sel.start() and m[1] == sel.end()) {
                const template = self.replace_input.contents(arena.allocator()) catch return;
                const text = self.replacementFor(arena.allocator(), target, m, template) catch return;
                var reps = [_]Editor.Replacement{.{ .start = m[0], .end = m[1], .text = text, .sel_anchor = text.len, .sel_head = text.len }};
                target.applyReplacements(&reps, .other);
                break;
            }
        }
        _ = self.findNext(target, true);
    }

    pub fn replaceAll(self: *FindPanel, target: *Editor) usize {
        self.update(target);
        const n = self.matches.items.len;
        if (n == 0) return 0;
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const template = self.replace_input.contents(alloc) catch return 0;
        const reps = alloc.alloc(Editor.Replacement, n) catch return 0;
        for (self.matches.items, reps) |m, *r| {
            const text = self.replacementFor(alloc, target, m, template) catch template;
            r.* = .{ .start = m[0], .end = m[1], .text = text, .sel_anchor = text.len, .sel_head = text.len };
        }
        target.applyReplacements(reps, .other);
        target.setSingleSelection(target.primarySelection());
        return n;
    }

    pub fn paint(self: *FindPanel, ctx: *const ui.Ctx, area: Rect) void {
        const theme = ctx.theme;
        const scene = ctx.scene;
        self.rect = area;
        scene.fill(area, theme.panel_background);
        scene.fill(.{ .x = area.x, .y = area.y, .w = area.w, .h = 1 }, theme.panel_border);

        const pad: f32 = 6;
        const bh: f32 = 28;
        var x = area.x + pad;
        const row1_y = area.y + pad;
        const all_labels = [_]struct { Toggle, []const u8 }{
            .{ .regex, ".*" },
            .{ .case_sensitive, "Aa" },
            .{ .whole_word, "“”" },
            .{ .wrap, "⤶" },
            .{ .in_selection, "≡" },
            .{ .highlight, "▭" },
        };
        // Find in Files only uses the matching options; keep the inputs aligned.
        const labels = if (self.mode == .files) all_labels[0..3] else all_labels[0..];
        self.toggle_rects = .initFill(.{});
        if (self.mode == .files) x += (bh + 3) * 3;
        for (labels) |l| {
            const r: Rect = .{ .x = x, .y = row1_y, .w = bh, .h = bh };
            self.toggle_rects.set(l[0], r);
            const on = self.toggles.get(l[0]);
            scene.quad(r, .{
                .color = if (on) theme.button_active else if (ctx.hovered(r)) theme.button_background.lighten(0.05) else theme.button_background,
                .border_color = theme.input_border,
                .border_width = 1,
                .radius = 3,
            });
            ctx.labelCentered(r, l[1], ctx.fonts.ui_small, if (on) theme.tab_active_foreground else theme.list_foreground_dim);
            x += bh + 3;
        }
        x += pad;

        const button_w: f32 = 82;
        const buttons_w = button_w * 3 + 6;
        const input_w = @max(80, area.right() - pad - buttons_w - pad - x);
        self.find_rect = .{ .x = x, .y = row1_y, .w = input_w, .h = bh };
        self.button_rects = .initFill(.{});
        if (self.mode == .files) {
            self.paintFilesMode(ctx, input_w, bh, pad, button_w);
            return;
        }
        self.where_rect = .{};
        paintInput(ctx, self.find_rect, self.find_input, !self.focus_replace or !self.show_replace, self.regex_error);

        // Match count inside the input.
        if (self.matches.items.len > 0 or self.find_buffer.len() > 0) {
            var buf: [48]u8 = undefined;
            const label = if (self.current) |cur|
                std.fmt.bufPrint(&buf, "{d} of {d}", .{ cur + 1, self.matches.items.len }) catch ""
            else
                std.fmt.bufPrint(&buf, "{d} matches", .{self.matches.items.len}) catch "";
            _ = ctx.labelRight(.{ .x = self.find_rect.x, .y = self.find_rect.y, .w = self.find_rect.w - 8, .h = bh }, label, ctx.fonts.ui_small, theme.list_foreground_dim);
        }

        var bx = self.find_rect.right() + pad;
        const row1 = [_]struct { Button, []const u8 }{ .{ .find, "Find" }, .{ .find_prev, "Find Prev" }, .{ .find_all, "Find All" } };
        for (row1) |b| {
            const r: Rect = .{ .x = bx, .y = row1_y, .w = button_w, .h = bh };
            self.button_rects.set(b[0], r);
            paintButton(ctx, r, b[1]);
            bx += button_w + 3;
        }

        if (self.show_replace) {
            const row2_y = row1_y + bh + pad;
            self.replace_rect = .{ .x = self.find_rect.x, .y = row2_y, .w = input_w, .h = bh };
            paintInput(ctx, self.replace_rect, self.replace_input, self.focus_replace, false);
            bx = self.replace_rect.right() + pad;
            const row2 = [_]struct { Button, []const u8 }{ .{ .replace, "Replace" }, .{ .replace_all, "Replace All" } };
            for (row2) |b| {
                const r: Rect = .{ .x = bx, .y = row2_y, .w = button_w, .h = bh };
                self.button_rects.set(b[0], r);
                paintButton(ctx, r, b[1]);
                bx += button_w + 3;
            }
        } else {
            self.button_rects.set(.replace, .{});
            self.button_rects.set(.replace_all, .{});
        }
    }

    /// Rows: Find / Where / Replace, each with its button.
    fn paintFilesMode(self: *FindPanel, ctx: *const ui.Ctx, input_w: f32, bh: f32, pad: f32, button_w: f32) void {
        const rows = [_]struct { *Rect, *Editor, bool, ?Button, []const u8 }{
            .{ &self.find_rect, self.find_input, !self.focus_where and !self.focus_replace, .find_in_files, "Find" },
            .{ &self.where_rect, self.where_input, self.focus_where, null, "" },
            .{ &self.replace_rect, self.replace_input, self.focus_replace, .replace_in_files, "Replace" },
        };
        var y = self.find_rect.y;
        for (rows, 0..) |row, i| {
            row[0].* = .{ .x = self.find_rect.x, .y = y, .w = input_w, .h = bh };
            paintInput(ctx, row[0].*, row[1], row[2], i == 0 and self.regex_error);
            if (row[3]) |b| {
                const r: Rect = .{ .x = row[0].right() + pad, .y = y, .w = button_w, .h = bh };
                self.button_rects.set(b, r);
                paintButton(ctx, r, row[4]);
            }
            y += bh + pad;
        }
        // Row labels in the toggle column.
        const label_x = self.rect.x + pad;
        const theme = ctx.theme;
        _ = ctx.label(.{ .x = label_x, .y = self.find_rect.y, .w = (bh + 3) * 3 - 6, .h = bh }, "Find:", ctx.fonts.ui_small, theme.list_foreground_dim);
        _ = ctx.label(.{ .x = label_x, .y = self.where_rect.y, .w = self.find_rect.x - label_x - pad, .h = bh }, "Where:", ctx.fonts.ui_small, theme.list_foreground_dim);
        _ = ctx.label(.{ .x = label_x, .y = self.replace_rect.y, .w = self.find_rect.x - label_x - pad, .h = bh }, "Replace:", ctx.fonts.ui_small, theme.list_foreground_dim);
    }

    pub fn hitToggle(self: *const FindPanel, x: f32, y: f32) ?Toggle {
        inline for (@typeInfo(Toggle).@"enum".field_values) |v| {
            const t: Toggle = @enumFromInt(v);
            if (self.toggle_rects.get(t).contains(x, y)) return t;
        }
        return null;
    }

    pub fn hitButton(self: *const FindPanel, x: f32, y: f32) ?Button {
        inline for (@typeInfo(Button).@"enum".field_values) |v| {
            const b: Button = @enumFromInt(v);
            if (self.button_rects.get(b).contains(x, y)) return b;
        }
        return null;
    }
};

pub fn paintInput(ctx: *const ui.Ctx, r: Rect, input: *Editor, focused: bool, err: bool) void {
    const theme = ctx.theme;
    const border = if (err) theme.syntax.constant else if (focused) theme.input_border_focus else theme.input_border;
    ctx.scene.quad(r, .{ .color = theme.input_background, .border_color = border, .border_width = 1, .radius = 3 });
    input.focused = focused;
    input.layout(r.inset(6, 0));
    input.paint(ctx.scene, theme);
}

fn paintButton(ctx: *const ui.Ctx, r: Rect, label: []const u8) void {
    const theme = ctx.theme;
    ctx.scene.quad(r, .{
        .color = if (ctx.hovered(r)) theme.button_background.lighten(0.06) else theme.button_background,
        .border_color = theme.input_border,
        .border_width = 1,
        .radius = 3,
    });
    ctx.labelCentered(r, label, ctx.fonts.ui, theme.list_foreground);
}

// -- searching the buffer -----------------------------------------------------
//
// Both searches read the rope's chunks where they lie: no copy of the buffer
// (or of a 50 MB line) per keystroke. Whole words are judged by the bytes
// around the searched range too, so a scope doesn't cut words.

pub const SearchOptions = struct {
    case_sensitive: bool = false,
    whole_word: bool = false,
    /// Stops after this many matches.
    limit: usize = max_matches,
};

/// Appends the non-overlapping matches of `needle` in [lo, hi) of `rope`.
/// Each chunk is searched in place; a match straddling chunks is found in a
/// seam: the last needle.len bytes before a chunk plus its first needle.len.
pub fn findPlain(gpa: Allocator, out: *std.ArrayList([2]usize), rope: anytype, lo: usize, hi: usize, needle: []const u8, opts: SearchOptions) !void {
    const n = needle.len;
    if (n == 0) return;
    var scan: PlainScan = .{ .gpa = gpa, .out = out, .needle = needle, .opts = opts, .next = lo };
    // The text [carry_start, cur) holding the starts not decided yet (at most
    // n: deciding one takes the byte after the match), and the byte before it.
    var carry: std.ArrayList(u8) = .empty;
    defer carry.deinit(gpa);
    var carry_start = lo;
    var carry_before: u8 = if (lo > 0) rope.byteAt(lo - 1) else '\n';
    var cur = lo;
    var it = rope.chunks(lo, hi);
    while (it.next()) |chunk| {
        if (chunk.len >= n) {
            const before = if (carry.items.len > 0) carry.items[carry.items.len - 1] else carry_before;
            if (carry.items.len > 0) {
                try carry.appendSlice(gpa, chunk[0..n]);
                if (!try scan.run(carry.items, carry_start, carry_before, null, cur)) return;
            }
            if (!try scan.run(chunk, cur, before, null, cur + chunk.len - n)) return;
            carry_before = if (chunk.len > n) chunk[chunk.len - n - 1] else before;
            carry.clearRetainingCapacity();
            try carry.appendSlice(gpa, chunk[chunk.len - n ..]);
            cur += chunk.len;
            carry_start = cur - n;
        } else {
            // A chunk shorter than the needle: grow the carry instead.
            try carry.appendSlice(gpa, chunk);
            cur += chunk.len;
            if (carry.items.len > n) {
                if (!try scan.run(carry.items, carry_start, carry_before, null, cur - n)) return;
                const drop = carry.items.len - n;
                carry_before = carry.items[drop - 1];
                std.mem.copyForwards(u8, carry.items, carry.items[drop..]);
                carry.shrinkRetainingCapacity(n);
                carry_start += drop;
            }
        }
    }
    const after: u8 = if (hi < rope.len()) rope.byteAt(hi) else '\n';
    _ = try scan.run(carry.items, carry_start, carry_before, after, (cur + 1) -| n);
}

const PlainScan = struct {
    gpa: Allocator,
    out: *std.ArrayList([2]usize),
    needle: []const u8,
    opts: SearchOptions,
    /// Matches don't overlap: the next one starts here or later.
    next: usize,

    /// Adds the matches in `hay` (the text at buffer offset `base`) that
    /// start before `start_end`. `before` / `after` are the bytes around
    /// `hay`; `after` may be null when no match can end at hay's end.
    /// Returns false once the match limit is reached.
    fn run(self: *PlainScan, hay: []const u8, base: usize, before: u8, after: ?u8, start_end: usize) !bool {
        const n = self.needle.len;
        if (start_end <= base or hay.len < n) return true;
        const window = hay[0..@min(hay.len, start_end - base + n - 1)];
        std.debug.assert(after != null or window.len < hay.len);
        var pos = self.next -| base;
        while (indexOfFold(window, pos, self.needle, self.opts.case_sensitive)) |at| {
            const end = at + n;
            if (!self.opts.whole_word or isWholeWord(hay, at, end, before, after orelse '\n')) {
                try self.out.append(self.gpa, .{ base + at, base + end });
                if (self.out.items.len >= self.opts.limit) return false;
            }
            self.next = base + end;
            pos = end;
        }
        return true;
    }
};

/// Appends the matches of `re` in [lo, hi) of `rope`, line by line (`^` and
/// `$` anchor at lines). A line inside one chunk is matched where it lies;
/// only lines spanning chunks are copied.
pub fn findRegex(gpa: Allocator, out: *std.ArrayList([2]usize), rope: anytype, lo: usize, hi: usize, re: *regex_mod.Regex, opts: SearchOptions) !void {
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(gpa);
    var before: u8 = if (lo > 0) rope.byteAt(lo - 1) else '\n';
    var line_start = lo;
    var it = rope.chunks(lo, hi);
    while (it.next()) |chunk| {
        var rest = chunk;
        while (std.mem.indexOfScalar(u8, rest, '\n')) |nl| {
            const text = if (line.items.len == 0) rest[0..nl] else joined: {
                try line.appendSlice(gpa, rest[0..nl]);
                break :joined line.items;
            };
            if (!try regexLine(gpa, out, re, text, line_start, before, '\n', opts)) return;
            line_start += text.len + 1;
            before = '\n';
            line.clearRetainingCapacity();
            rest = rest[nl + 1 ..];
        }
        try line.appendSlice(gpa, rest);
    }
    const after: u8 = if (hi < rope.len()) rope.byteAt(hi) else '\n';
    _ = try regexLine(gpa, out, re, line.items, line_start, before, after, opts);
}

/// Adds the matches of `re` in one line at buffer offset `base`.
/// Returns false once the match limit is reached.
fn regexLine(gpa: Allocator, out: *std.ArrayList([2]usize), re: *regex_mod.Regex, line: []const u8, base: usize, before: u8, after: u8, opts: SearchOptions) !bool {
    var pos: usize = 0;
    while (pos <= line.len) {
        const m = re.search(line, pos) orelse break;
        if (!opts.whole_word or isWholeWord(line, m.start, m.end, before, after)) {
            try out.append(gpa, .{ base + m.start, base + m.end });
            if (out.items.len >= opts.limit) return false;
        }
        pos = if (m.end > m.start) m.end else m.end + 1;
    }
    return true;
}

/// Whether hay[start..end] is a whole word; `before` / `after` are the bytes
/// around `hay` ('\n' at the buffer's ends).
fn isWholeWord(hay: []const u8, start: usize, end: usize, before: u8, after: u8) bool {
    const b = if (start > 0) hay[start - 1] else before;
    const a = if (end < hay.len) hay[end] else after;
    return !movement.isWordChar(b) and !movement.isWordChar(a);
}

/// First occurrence of `needle` in `hay` at or after `from`, ASCII case
/// folded unless `case_sensitive`. Candidates come a vector of starts at a
/// time from where the needle's first, middle and last bytes all match.
fn indexOfFold(hay: []const u8, from: usize, needle: []const u8, case_sensitive: bool) ?usize {
    const n = needle.len;
    if (n == 0 or hay.len < n or from > hay.len - n) return null;
    const last_start = hay.len - n;
    const lanes = comptime std.simd.suggestVectorLength(u8) orelse 16;
    if (last_start + 1 < lanes) {
        for (from..last_start + 1) |i| if (eqlFold(hay[i..][0..n], needle, case_sensitive)) return i;
        return null;
    }
    const V = @Vector(lanes, u8);
    const Mask = @Int(.unsigned, lanes);
    const probes = [3]usize{ 0, n / 2, n - 1 };
    var fold: [3]V = undefined;
    var want: [3]V = undefined;
    for (probes, &fold, &want) |p, *f, *w| {
        // OR-ing in 0x20 maps exactly the ASCII letters onto lower case.
        const bit: u8 = if (!case_sensitive and std.ascii.isAlphabetic(needle[p])) 0x20 else 0;
        f.* = @splat(bit);
        w.* = @splat(needle[p] | bit);
    }
    var i = from;
    while (i <= last_start) {
        // The last block moves back to end at the last start; its starts
        // before `i` were searched already.
        const block = @min(i, last_start + 1 - lanes);
        var hits = ~@as(Mask, 0) << @intCast(i - block);
        for (probes, fold, want) |p, f, w| {
            const v: V = hay[block + p ..][0..lanes].*;
            hits &= @as(Mask, @bitCast((v | f) == w));
        }
        while (hits != 0) : (hits &= hits - 1) {
            const at = block + @ctz(hits);
            if (eqlFold(hay[at..][0..n], needle, case_sensitive)) return at;
        }
        i = block + lanes;
    }
    return null;
}

fn eqlFold(a: []const u8, b: []const u8, case_sensitive: bool) bool {
    return if (case_sensitive) std.mem.eql(u8, a, b) else std.ascii.eqlIgnoreCase(a, b);
}

// -- tests --------------------------------------------------------------------

const testing = std.testing;
const rope_mod = @import("../../model/text/rope.zig");

/// The search as it was over one flat copy of the buffer.
fn findFlat(hay: []const u8, lo: usize, hi: usize, needle: []const u8, re: ?*regex_mod.Regex, opts: SearchOptions) !std.ArrayList([2]usize) {
    var out: std.ArrayList([2]usize) = .empty;
    errdefer out.deinit(testing.allocator);
    const whole = struct {
        fn f(h: []const u8, s: usize, e: usize) bool {
            return (s == 0 or !movement.isWordChar(h[s - 1])) and (e >= h.len or !movement.isWordChar(h[e]));
        }
    }.f;
    if (re) |r| {
        var line_start = lo;
        while (line_start <= hi) {
            const line_end = std.mem.indexOfScalarPos(u8, hay, line_start, '\n') orelse hay.len;
            const line = hay[line_start..@min(line_end, hi)];
            var pos: usize = 0;
            while (pos <= line.len) {
                const m = r.search(line, pos) orelse break;
                if (!opts.whole_word or whole(hay, line_start + m.start, line_start + m.end)) {
                    try out.append(testing.allocator, .{ line_start + m.start, line_start + m.end });
                    if (out.items.len >= opts.limit) return out;
                }
                pos = if (m.end > m.start) m.end else m.end + 1;
            }
            if (line_end >= hi) break;
            line_start = line_end + 1;
        }
        return out;
    }
    var pos = lo;
    while (pos + needle.len <= hi) {
        var at = pos;
        while (at + needle.len <= hi and !eqlFold(hay[at..][0..needle.len], needle, opts.case_sensitive)) at += 1;
        if (at + needle.len > hi) break;
        if (!opts.whole_word or whole(hay, at, at + needle.len)) {
            try out.append(testing.allocator, .{ at, at + needle.len });
            if (out.items.len >= opts.limit) return out;
        }
        pos = at + needle.len;
    }
    return out;
}

/// A rope of tiny, uneven chunks: built from pieces, then edited.
fn testRope(comptime R: type, rng: std.Random, text: *std.ArrayList(u8)) !R {
    var r = R.init();
    errdefer r.deinit(testing.allocator);
    const alphabet = "aaabAB _-\nxyz";
    var piece: [40]u8 = undefined;
    for (0..80) |_| {
        const len = rng.intRangeAtMost(usize, 1, piece.len);
        for (piece[0..len]) |*c| c.* = alphabet[rng.uintLessThan(usize, alphabet.len)];
        const at = rng.uintAtMost(usize, r.len());
        const del = @min(rng.uintAtMost(usize, 6), r.len() - at);
        try r.replace(testing.allocator, at, at + del, piece[0..len]);
    }
    const flat = try r.toOwnedSlice(testing.allocator);
    defer testing.allocator.free(flat);
    text.clearRetainingCapacity();
    try text.appendSlice(testing.allocator, flat);
    return r;
}

fn expectSameAsFlat(r: anytype, text: []const u8, lo: usize, hi: usize, needle: []const u8, re: ?*regex_mod.Regex, opts: SearchOptions) !void {
    var want = try findFlat(text, lo, hi, needle, re, opts);
    defer want.deinit(testing.allocator);
    var got: std.ArrayList([2]usize) = .empty;
    defer got.deinit(testing.allocator);
    if (re) |p| try findRegex(testing.allocator, &got, r, lo, hi, p, opts) else try findPlain(testing.allocator, &got, r, lo, hi, needle, opts);
    try testing.expectEqualSlices([2]usize, want.items, got.items);
}

test "find: plain matches across chunk boundaries" {
    const R = rope_mod.RopeWith(4, 16);
    var prng: std.Random.DefaultPrng = .init(7);
    const rng = prng.random();
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(testing.allocator);
    for (0..6) |_| {
        var r = try testRope(R, rng, &text);
        defer r.deinit(testing.allocator);
        for (0..60) |_| {
            // Needles cut from the text (shorter and longer than a chunk) or typed.
            const len = rng.intRangeAtMost(usize, 1, 40);
            const at = rng.uintLessThan(usize, text.items.len - len);
            const needle = if (rng.boolean()) text.items[at..][0..len] else "aB"[0..rng.intRangeAtMost(usize, 1, 2)];
            const lo = if (rng.boolean()) 0 else rng.uintAtMost(usize, text.items.len);
            const hi = if (rng.boolean()) text.items.len else rng.intRangeAtMost(usize, lo, text.items.len);
            for ([_]bool{ false, true }) |case_sensitive| for ([_]bool{ false, true }) |whole_word| {
                const opts: SearchOptions = .{ .case_sensitive = case_sensitive, .whole_word = whole_word };
                try expectSameAsFlat(&r, text.items, lo, hi, needle, null, opts);
            };
            try expectSameAsFlat(&r, text.items, lo, hi, needle, null, .{ .limit = 3 });
        }
    }
}

test "find: vector search at block edges" {
    var prng: std.Random.DefaultPrng = .init(3);
    const rng = prng.random();
    var hay: [200]u8 = undefined;
    var needle: [40]u8 = undefined;
    for (0..400) |_| {
        const len = rng.uintAtMost(usize, hay.len);
        for (hay[0..len]) |*c| c.* = "aAb\"_"[rng.uintLessThan(usize, 5)];
        const n = rng.intRangeAtMost(usize, 1, needle.len);
        if (len >= n and rng.boolean()) {
            // Cut from the hay, with some letters' case flipped.
            const at = rng.uintAtMost(usize, len - n);
            for (needle[0..n], hay[at..][0..n]) |*c, h| c.* = if (h == 'a' and rng.boolean()) 'A' else h;
        } else for (needle[0..n]) |*c| {
            c.* = "aAb\"_"[rng.uintLessThan(usize, 5)];
        }
        for ([_]bool{ false, true }) |case_sensitive| for (0..len + 1) |from| {
            var want: ?usize = null;
            var i = from;
            while (i + n <= len) : (i += 1) if (eqlFold(hay[i..][0..n], needle[0..n], case_sensitive)) {
                want = i;
                break;
            };
            try testing.expectEqual(want, indexOfFold(hay[0..len], from, needle[0..n], case_sensitive));
        };
    }
}

test "find: whole word and case options" {
    const text = "foo Foo foobar barfoo FOO_x foo-FOO\nfoo";
    var r = try rope_mod.RopeWith(4, 16).fromSlice(testing.allocator, text);
    defer r.deinit(testing.allocator);
    const Case = struct { opts: SearchOptions, lo: usize = 0, hi: usize = text.len, want: []const [2]usize };
    const cases = [_]Case{
        .{ .opts = .{}, .want = &.{ .{ 0, 3 }, .{ 4, 7 }, .{ 8, 11 }, .{ 18, 21 }, .{ 22, 25 }, .{ 28, 31 }, .{ 32, 35 }, .{ 36, 39 } } },
        .{ .opts = .{ .case_sensitive = true }, .want = &.{ .{ 0, 3 }, .{ 8, 11 }, .{ 18, 21 }, .{ 28, 31 }, .{ 36, 39 } } },
        .{ .opts = .{ .whole_word = true }, .want = &.{ .{ 0, 3 }, .{ 4, 7 }, .{ 28, 31 }, .{ 32, 35 }, .{ 36, 39 } } },
        .{ .opts = .{ .whole_word = true, .case_sensitive = true }, .want = &.{ .{ 0, 3 }, .{ 28, 31 }, .{ 36, 39 } } },
        // A scope doesn't cut words: "foo" inside "foobar" stays part of it.
        .{ .opts = .{ .whole_word = true }, .lo = 8, .hi = 11, .want = &.{} },
        .{ .opts = .{}, .lo = 8, .hi = 11, .want = &.{.{ 8, 11 }} },
        .{ .opts = .{ .limit = 2 }, .want = &.{ .{ 0, 3 }, .{ 4, 7 } } },
    };
    for (cases) |c| {
        var got: std.ArrayList([2]usize) = .empty;
        defer got.deinit(testing.allocator);
        try findPlain(testing.allocator, &got, &r, c.lo, c.hi, "foo", c.opts);
        try testing.expectEqualSlices([2]usize, c.want, got.items);
    }
    // Multi-line needles (from "use selection for find") match across lines.
    var got: std.ArrayList([2]usize) = .empty;
    defer got.deinit(testing.allocator);
    try findPlain(testing.allocator, &got, &r, 0, text.len, "FOO\nf", .{ .case_sensitive = true });
    try testing.expectEqualSlices([2]usize, &.{.{ 32, 37 }}, got.items);
}

test "find: regex line by line across chunks" {
    const R = rope_mod.RopeWith(4, 16);
    var prng: std.Random.DefaultPrng = .init(11);
    const rng = prng.random();
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(testing.allocator);
    const patterns = [_][]const u8{ "a+b", "^a", "b$", "\\bx\\w*", "a*", "[AB]_?", "^$", "y.z" };
    for (0..4) |_| {
        var r = try testRope(R, rng, &text);
        defer r.deinit(testing.allocator);
        for (patterns) |p| for ([_]bool{ false, true }) |case_sensitive| {
            var re = try regex_mod.Regex.compile(testing.allocator, p, .{ .case_insensitive = !case_sensitive });
            defer re.deinit();
            const lo = rng.uintAtMost(usize, text.items.len / 2);
            const hi = rng.intRangeAtMost(usize, lo, text.items.len);
            for ([_]bool{ false, true }) |whole_word| {
                const opts: SearchOptions = .{ .case_sensitive = case_sensitive, .whole_word = whole_word };
                try expectSameAsFlat(&r, text.items, 0, text.items.len, p, &re, opts);
                try expectSameAsFlat(&r, text.items, lo, hi, p, &re, opts);
            }
        };
    }
}

test "find: one long line in a real rope" {
    // 300 KB on one line: hundreds of 1 KB chunks, matches on every seam.
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(testing.allocator);
    while (text.items.len < 300_000) try text.appendSlice(testing.allocator, "{\"tag\":\"Magna\",\"n\":7},");
    var r = try rope_mod.Rope.fromSlice(testing.allocator, text.items);
    defer r.deinit(testing.allocator);
    for ([_][]const u8{ "\"magna\"", "magna", "\"", "},{\"tag\":\"Magna\",\"n\":7},{" }) |needle| {
        try expectSameAsFlat(&r, text.items, 0, text.items.len, needle, null, .{});
        try expectSameAsFlat(&r, text.items, 0, text.items.len, needle, null, .{ .case_sensitive = true, .whole_word = true });
    }
    var re = try regex_mod.Regex.compile(testing.allocator, "ma\\w+", .{ .case_insensitive = true });
    defer re.deinit();
    try expectSameAsFlat(&r, text.items, 0, text.items.len, "", &re, .{});
}
