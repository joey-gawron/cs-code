//! Sublime's quick panel overlay: Goto Anything (files, `:line`, `@symbol`),
//! the Command Palette, Goto Line / Symbol and simple input prompts. The
//! input is a single-line Editor; items are fuzzy-filtered as you type.
//! Prompts for a path list the typed folder's entries (path_complete.zig).

const std = @import("std");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const keymap = @import("../settings/keymap.zig");
const ui = @import("../../framework/ui/ui.zig");
const fuzzy = @import("../../model/project/fuzzy.zig");
const languages = @import("../../model/syntax/languages.zig");

const Allocator = std.mem.Allocator;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Rect = ui.Rect;

pub const Kind = enum {
    goto_anything,
    command_palette,
    goto_line,
    goto_symbol,
    /// Free-form input (save as, open path, rename...).
    prompt,
    /// A plain list chosen by the caller (e.g. syntax picker).
    list,
    /// LSP definitions / references.
    locations,
};

pub const Payload = union(enum) {
    none,
    action: keymap.Action,
    /// Project entry index.
    file: u32,
    syntax: *const languages.LanguageConfig,
    /// Buffer offset (symbols).
    offset: usize,
    /// Caller-defined value (lists).
    value: usize,
    location: struct { path: []const u8, line: u32, character: u32, utf8: bool },
    /// An entry of a path prompt's folder: true for a folder.
    path_entry: bool,
};

/// A prompt that takes a path (path_complete.zig). Strings are owned.
pub const PathMode = struct {
    /// Only folders are listed (Open Folder).
    dirs_only: bool = false,
    /// A remote prompt's paths are on this host (listed through ssh://host).
    host: ?[]u8 = null,
    /// What relative paths are relative to.
    base: []u8 = &.{},
    /// The folder (as typed, through its last '/') whose entries are the items.
    listed: ?[]u8 = null,
    /// The arrow keys picked an entry: Enter takes it instead of the typed text.
    picked: bool = false,
    /// The entries' names (apart from the item arena, which holds the
    /// prompt's label and outlives a listing).
    names: ?std.heap.ArenaAllocator = null,

    fn deinit(self: *PathMode, gpa: Allocator) void {
        if (self.host) |h| gpa.free(h);
        gpa.free(self.base);
        if (self.listed) |l| gpa.free(l);
        if (self.names) |*a| a.deinit();
    }
};

pub const Item = struct {
    title: []const u8,
    detail: []const u8 = "",
    shortcut: []const u8 = "",
    payload: Payload = .none,
};

const Filtered = struct {
    index: u32,
    score: i32,
    positions: []const u16,
};

pub const QuickPanel = struct {
    gpa: Allocator,
    kind: Kind,
    prompt_label: []const u8 = "",
    /// What to do with a prompt's input (interpreted by the workspace).
    prompt_tag: u32 = 0,
    /// Password input: bullets instead of the text.
    masked: bool = false,
    path_mode: ?PathMode = null,
    input_buffer: *Buffer,
    input: *Editor,
    items: std.ArrayList(Item) = .empty,
    /// Owns item strings.
    item_arena: std.heap.ArenaAllocator,
    filtered: std.ArrayList(Filtered) = .empty,
    filter_arena: std.heap.ArenaAllocator,
    matcher: fuzzy.Matcher,
    selected: usize = 0,
    scroll: usize = 0,
    last_query: std.ArrayList(u8) = .empty,
    last_version: u64 = std.math.maxInt(u64),
    /// Two-line rows (files) vs one-line rows (commands).
    two_line: bool = false,
    /// Geometry from the last paint, for mouse hit-testing.
    list_rect: Rect = .{},
    row_h: f32 = 30,
    panel_rect: Rect = .{},

    pub fn create(gpa: Allocator, env: *editor_mod.Env, kind: Kind) !*QuickPanel {
        const self = try gpa.create(QuickPanel);
        errdefer gpa.destroy(self);
        const buf = try gpa.create(Buffer);
        buf.* = try Buffer.initEmpty(gpa, 0);
        const input = try Editor.create(gpa, env, buf);
        input.mode = .single_line;
        self.* = .{
            .gpa = gpa,
            .kind = kind,
            .input_buffer = buf,
            .input = input,
            .item_arena = .init(gpa),
            .filter_arena = .init(gpa),
            .matcher = .init(gpa),
            .two_line = kind == .goto_anything,
        };
        return self;
    }

    pub fn destroy(self: *QuickPanel) void {
        if (self.path_mode) |*pm| pm.deinit(self.gpa);
        self.input.destroy();
        self.input_buffer.deinit();
        self.gpa.destroy(self.input_buffer);
        self.items.deinit(self.gpa);
        self.item_arena.deinit();
        self.filtered.deinit(self.gpa);
        self.filter_arena.deinit();
        self.matcher.deinit();
        self.last_query.deinit(self.gpa);
        self.gpa.destroy(self);
    }

    pub fn arena(self: *QuickPanel) Allocator {
        return self.item_arena.allocator();
    }

    pub fn addItem(self: *QuickPanel, item: Item) !void {
        try self.items.append(self.gpa, item);
        self.last_version = std.math.maxInt(u64);
    }

    pub fn clearItems(self: *QuickPanel) void {
        self.items.clearRetainingCapacity();
        _ = self.item_arena.reset(.retain_capacity);
        self.last_version = std.math.maxInt(u64);
    }

    /// The input text (valid until the input changes).
    pub fn query(self: *QuickPanel) []const u8 {
        const text = self.input.contents(self.gpa) catch return "";
        defer self.gpa.free(text);
        self.last_query.clearRetainingCapacity();
        self.last_query.appendSlice(self.gpa, text) catch {};
        return self.last_query.items;
    }

    /// Re-filters when the input changed. Returns true if it did.
    pub fn refilter(self: *QuickPanel) bool {
        if (self.input_buffer.version == self.last_version) return false;
        self.last_version = self.input_buffer.version;
        const q_full = self.query();
        // Goto Anything's ":" and "@" suffixes don't filter files.
        var q = q_full;
        if (self.kind == .goto_anything) {
            if (std.mem.indexOfAny(u8, q, ":@#")) |i| q = q[0..i];
        }
        _ = self.filter_arena.reset(.retain_capacity);
        const alloc = self.filter_arena.allocator();
        self.filtered.clearRetainingCapacity();
        if (self.path_mode) |*pm| {
            pm.picked = false;
            self.filterPath(q_full);
            return true;
        }
        if (self.kind == .prompt or self.kind == .goto_line) return true;
        for (self.items.items, 0..) |item, i| {
            const candidate = if (self.kind == .goto_anything) item.detail else item.title;
            const m = self.matcher.match(q, candidate, self.kind == .goto_anything) orelse continue;
            // Positions index into `detail` (the path) for files; shift to the title.
            var positions = alloc.dupe(u16, m.positions) catch continue;
            if (self.kind == .goto_anything) {
                const base: u16 = @intCast(item.detail.len - item.title.len);
                var n: usize = 0;
                for (positions) |p| if (p >= base) {
                    positions[n] = p - base;
                    n += 1;
                };
                positions = positions[0..n];
            }
            self.filtered.append(self.gpa, .{ .index = @intCast(i), .score = m.score, .positions = positions }) catch break;
        }
        if (q.len > 0) {
            std.mem.sort(Filtered, self.filtered.items, self, struct {
                fn lt(ctx: *QuickPanel, a: Filtered, b: Filtered) bool {
                    if (a.score != b.score) return a.score > b.score;
                    return ctx.items.items[a.index].detail.len < ctx.items.items[b.index].detail.len;
                }
            }.lt);
        }
        self.selected = 0;
        self.scroll = 0;
        return true;
    }

    /// A path prompt's entries for the name after the last '/': those that
    /// start with it (ignoring case) in listing order, then other fuzzy
    /// matches by score. Dotfiles only once the name starts with '.'.
    fn filterPath(self: *QuickPanel, typed: []const u8) void {
        const alloc = self.filter_arena.allocator();
        const name = if (std.mem.lastIndexOfScalar(u8, typed, '/')) |i| typed[i + 1 ..] else typed;
        const dotfiles = name.len > 0 and name[0] == '.';
        const first_fuzzy = blk: {
            for (self.items.items, 0..) |item, i| {
                if (item.title[0] == '.' and !dotfiles) continue;
                if (item.title.len < name.len or !std.ascii.eqlIgnoreCase(item.title[0..name.len], name)) continue;
                const positions = alloc.alloc(u16, name.len) catch continue;
                for (positions, 0..) |*p, j| p.* = @intCast(j);
                self.filtered.append(self.gpa, .{ .index = @intCast(i), .score = std.math.maxInt(i32), .positions = positions }) catch break;
            }
            break :blk self.filtered.items.len;
        };
        if (name.len > 0) {
            for (self.items.items, 0..) |item, i| {
                if (item.title[0] == '.' and !dotfiles) continue;
                if (item.title.len >= name.len and std.ascii.eqlIgnoreCase(item.title[0..name.len], name)) continue;
                const m = self.matcher.match(name, item.title, false) orelse continue;
                const positions = alloc.dupe(u16, m.positions) catch continue;
                self.filtered.append(self.gpa, .{ .index = @intCast(i), .score = m.score, .positions = positions }) catch break;
            }
            std.mem.sort(Filtered, self.filtered.items[first_fuzzy..], {}, struct {
                fn lt(_: void, a: Filtered, b: Filtered) bool {
                    return a.score > b.score;
                }
            }.lt);
        }
        self.selected = 0;
        self.scroll = 0;
    }

    pub fn selectedItem(self: *const QuickPanel) ?Item {
        if (self.filtered.items.len == 0) return null;
        return self.items.items[self.filtered.items[@min(self.selected, self.filtered.items.len - 1)].index];
    }

    pub fn maxVisible(self: *const QuickPanel) usize {
        return if (self.two_line) 10 else 12;
    }

    pub fn moveSelection(self: *QuickPanel, delta: isize) void {
        const n = self.filtered.items.len;
        if (n == 0) return;
        const cur: isize = @intCast(self.selected);
        var next = cur + delta;
        if (delta == 1 or delta == -1) {
            // Arrow keys wrap around.
            next = @mod(next, @as(isize, @intCast(n)));
        } else {
            next = std.math.clamp(next, 0, @as(isize, @intCast(n - 1)));
        }
        self.selected = @intCast(next);
        const vis = self.maxVisible();
        if (self.selected < self.scroll) self.scroll = self.selected;
        if (self.selected >= self.scroll + vis) self.scroll = self.selected + 1 - vis;
    }

    pub fn paint(self: *QuickPanel, ctx: *const ui.Ctx, area: Rect) void {
        const theme = ctx.theme;
        const scene = ctx.scene;
        _ = self.refilter();
        const width = @round(@min(680, @max(360, area.w * 0.6)));
        const x = @round(area.x + (area.w - width) / 2);
        const y = area.y + 8;
        const pad: f32 = 8;
        const input_h: f32 = 34;
        self.row_h = if (self.two_line) 46 else 30;
        const shown = @min(self.filtered.items.len -| self.scroll, self.maxVisible());
        const list_h = @as(f32, @floatFromInt(shown)) * self.row_h;
        const has_label = self.prompt_label.len > 0;
        const label_h: f32 = if (has_label) 22 else 0;
        const h = pad + label_h + input_h + (if (shown > 0) pad + list_h else 0) + pad;
        const panel: Rect = .{ .x = x, .y = y, .w = width, .h = h };
        self.panel_rect = panel;

        ctx.shadow(panel, 6, 14);
        scene.quad(panel, .{ .color = theme.overlay_background, .border_color = theme.panel_border, .border_width = 1, .radius = 6 });

        if (has_label) {
            _ = ctx.label(.{ .x = x + pad + 2, .y = y + pad, .w = width - 2 * pad, .h = label_h }, self.prompt_label, ctx.fonts.ui, theme.list_foreground_dim);
        }
        const input_rect: Rect = .{ .x = x + pad, .y = y + pad + label_h, .w = width - 2 * pad, .h = input_h };
        scene.quad(input_rect, .{ .color = theme.input_background, .border_color = theme.input_border_focus, .border_width = 1, .radius = 4 });
        self.input.layout(input_rect.inset(8, 0));
        if (self.masked) self.paintMasked(ctx, input_rect.inset(8, 0)) else self.input.paint(scene, theme);

        const list: Rect = .{ .x = x + pad, .y = input_rect.bottom() + pad, .w = width - 2 * pad, .h = list_h };
        self.list_rect = list;
        if (shown == 0) return;
        scene.pushClip(list) catch return;
        defer scene.popClip();
        for (0..shown) |i| {
            const fi = self.scroll + i;
            const f = self.filtered.items[fi];
            const item = self.items.items[f.index];
            const row: Rect = .{ .x = list.x, .y = list.y + @as(f32, @floatFromInt(i)) * self.row_h, .w = list.w, .h = self.row_h };
            if (fi == self.selected) {
                scene.quad(row, .{ .color = theme.list_selected, .radius = 4 });
            } else if (ctx.hovered(row)) {
                scene.quad(row, .{ .color = theme.list_selected.withAlpha(0.4), .radius = 4 });
            }
            const text_x = row.x + 10;
            var shortcut_w: f32 = 0;
            if (item.shortcut.len > 0) {
                shortcut_w = ctx.labelRight(.{ .x = row.x, .y = row.y, .w = row.w - 10, .h = self.row_h }, item.shortcut, ctx.fonts.ui_small, theme.list_foreground_dim) + 16;
            }
            if (self.two_line) {
                const top: Rect = .{ .x = text_x, .y = row.y + 4, .w = row.w - 20, .h = 20 };
                _ = drawHighlighted(ctx, top, item.title, f.positions, ctx.fonts.ui, theme.list_foreground, theme.list_match);
                _ = ctx.labelFit(.{ .x = text_x, .y = row.y + 23, .w = row.w - 20, .h = 18 }, item.detail, ctx.fonts.ui_small, theme.list_foreground_dim);
            } else {
                const r: Rect = .{ .x = text_x, .y = row.y, .w = row.w - 20 - shortcut_w, .h = self.row_h };
                const w = drawHighlighted(ctx, r, item.title, f.positions, ctx.fonts.ui, theme.list_foreground, theme.list_match);
                if (item.detail.len > 0) {
                    _ = ctx.label(.{ .x = text_x + w + 12, .y = row.y, .w = 200, .h = self.row_h }, item.detail, ctx.fonts.ui_small, theme.list_foreground_dim);
                }
            }
        }
    }

    /// One bullet per byte, and the caret.
    fn paintMasked(self: *QuickPanel, ctx: *const ui.Ctx, r: Rect) void {
        const bullet = "•";
        var buf: [64 * bullet.len]u8 = undefined;
        const n = @min(self.input_buffer.len(), 64);
        for (0..n) |i| @memcpy(buf[i * bullet.len ..][0..bullet.len], bullet);
        const head = @min(self.input.primarySelection().head, n);
        const caret_x = r.x + ctx.text.measure(buf[0 .. head * bullet.len], ctx.fonts.ui);
        _ = ctx.label(r, buf[0 .. n * bullet.len], ctx.fonts.ui, ctx.theme.list_foreground);
        ctx.scene.fill(.{ .x = caret_x, .y = r.y + 8, .w = 2, .h = r.h - 16 }, ctx.theme.caret);
    }

    /// Index into `filtered` for a click position.
    pub fn rowAt(self: *const QuickPanel, x: f32, y: f32) ?usize {
        if (!self.list_rect.contains(x, y)) return null;
        const i: usize = @intFromFloat((y - self.list_rect.y) / self.row_h);
        const fi = self.scroll + i;
        if (fi >= self.filtered.items.len) return null;
        return fi;
    }
};

/// Draws `text` with the chars at `positions` in `match_color`.
fn drawHighlighted(ctx: *const ui.Ctx, r: Rect, text: []const u8, positions: []const u16, font: ui.FontId, color: ui.Color, match_color: ui.Color) f32 {
    var spans: [64]@import("../../framework/text/text_system.zig").ColorSpan = undefined;
    var n: usize = 0;
    var i: usize = 0;
    var pi: usize = 0;
    while (i < text.len and n + 2 < spans.len) {
        const is_match = pi < positions.len and positions[pi] == i;
        var j = i + 1;
        if (is_match) {
            pi += 1;
            while (j < text.len and pi < positions.len and positions[pi] == j) : (j += 1) pi += 1;
        } else {
            while (j < text.len and !(pi < positions.len and positions[pi] == j)) j += 1;
        }
        spans[n] = .{ .end = @intCast(j), .color = if (is_match) match_color else color };
        n += 1;
        i = j;
    }
    const m = ctx.text.metrics(font);
    const layout = ctx.text.layoutLine(text, font);
    const baseline = r.y + @round((r.h - (m.ascent + m.descent)) / 2 + m.ascent);
    ctx.scene.pushClip(r) catch return layout.width;
    defer ctx.scene.popClip();
    ctx.text.drawLine(ctx.scene, layout, r.x, baseline, color, spans[0..n]);
    return layout.width;
}
