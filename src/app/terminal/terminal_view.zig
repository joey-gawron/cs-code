//! A terminal in a tab: paints a `Terminal`'s cells on a grid of the
//! editor's monospace font with colors from the active scheme, and turns
//! keys, mouse and wheel input into terminal input, selection and
//! scrollback. The workspace glue is in app/workspace/terminal_ui.zig.

const std = @import("std");
const editor_mod = @import("../editor/editor.zig");
const terminal_mod = @import("../../model/terminal/terminal.zig");
const palette_mod = @import("palette.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const theme_mod = @import("../../framework/ui/theme.zig");

const Allocator = std.mem.Allocator;
const Terminal = terminal_mod.Terminal;
const vterm = terminal_mod.vterm;
const Cell = vterm.Cell;
const Palette = palette_mod.Palette;
const Scene = scene_mod.Scene;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Theme = theme_mod.Theme;

const pad_x: f32 = 6;
const pad_y: f32 = 4;

pub const Options = struct {
    /// Starting directory.
    cwd: []const u8,
    /// Default: the user's shell.
    argv: ?[]const []const u8 = null,
    /// Area the terminal will get, to start the program at the right size.
    rect: Rect = .{ .w = 800, .h = 480 },
};

pub const TerminalView = struct {
    gpa: Allocator,
    env: *editor_mod.Env,
    term: *Terminal,
    /// Tab title: the program name until it sets one (OSC 0/2).
    title_buf: [160]u8 = undefined,
    title_len: usize = 0,
    title_changed: bool = false,
    /// Where the program started.
    start_dir: []u8,
    /// Area of the last layout, in window coordinates.
    rect: Rect = .{},
    cell_w: f32 = 8,
    cell_h: f32 = 16,
    /// Lines scrolled back from the live screen (0 = following output).
    scroll: usize = 0,
    /// Unused part of pixel (touchpad) scrolling, in lines.
    scroll_rest: f32 = 0,
    /// `pushed_total` when last serviced: keeps a scrolled-back view still.
    seen_pushed: u64 = 0,
    selection: ?Selection = null,
    /// A mouse drag is selecting.
    selecting: bool = false,
    select_unit: enum { char, word, line } = .char,
    /// The word or line first clicked (word / line selection extends from it).
    unit_anchor: [2]Point = undefined,
    /// Button held while the application gets mouse reports.
    reporting_button: ?u8 = null,
    /// The program has exited; the tab stays until a key is pressed.
    exited: bool = false,

    cells: std.ArrayList(Cell) = .empty,
    line: std.ArrayList(u8) = .empty,
    spans: std.ArrayList(text_mod.ColorSpan) = .empty,
    /// Byte offset in `line` of each column's text.
    starts: std.ArrayList(u32) = .empty,

    /// A position between cells: `line` is a stable line id (see vterm.Screen).
    pub const Point = struct {
        line: u64,
        col: u16,

        fn lessThan(a: Point, b: Point) bool {
            return a.line < b.line or (a.line == b.line and a.col < b.col);
        }
    };

    pub const Selection = struct { anchor: Point, head: Point };

    pub fn create(gpa: Allocator, env: *editor_mod.Env, opts: Options) !*TerminalView {
        const self = try gpa.create(TerminalView);
        errdefer gpa.destroy(self);
        self.* = .{ .gpa = gpa, .env = env, .term = undefined, .start_dir = try gpa.dupe(u8, opts.cwd) };
        errdefer gpa.free(self.start_dir);
        self.measure();
        const size = self.gridSize(opts.rect);
        self.term = try Terminal.create(gpa, .{ .argv = opts.argv, .cwd = opts.cwd, .rows = size[0], .cols = size[1] });
        const program = if (opts.argv) |a| a[0] else terminal_mod.defaultShell();
        self.setTitle(std.fs.path.basename(program));
        self.rect = opts.rect;
        return self;
    }

    pub fn destroy(self: *TerminalView) void {
        self.term.destroy();
        self.gpa.free(self.start_dir);
        self.cells.deinit(self.gpa);
        self.line.deinit(self.gpa);
        self.spans.deinit(self.gpa);
        self.starts.deinit(self.gpa);
        self.gpa.destroy(self);
    }

    pub fn title(self: *const TerminalView) []const u8 {
        return self.title_buf[0..self.title_len];
    }

    pub fn setTitle(self: *TerminalView, t: []const u8) void {
        var n = @min(t.len, self.title_buf.len);
        // Don't cut a UTF-8 sequence in half.
        while (n > 0 and n < t.len and t[n] & 0xC0 == 0x80) n -= 1;
        @memcpy(self.title_buf[0..n], t[0..n]);
        self.title_len = n;
        self.title_changed = true;
    }

    // ---- Event loop -----------------------------------------------------------

    /// Reads output and flushes input; true when the view needs a redraw.
    pub fn service(self: *TerminalView) bool {
        const changed = self.term.service();
        const screen = self.term.screen;
        // A scrolled-back view stays on the same lines while output scrolls.
        if (self.scroll > 0) {
            self.scroll = @min(self.scroll + (screen.pushed_total -| self.seen_pushed), screen.scrollbackLen());
        }
        self.seen_pushed = screen.pushed_total;
        if (screen.title_changed) {
            screen.title_changed = false;
            if (screen.title.items.len > 0) self.setTitle(screen.title.items);
        }
        return changed;
    }

    /// The program is gone (`term.eof`).
    pub fn finished(self: *const TerminalView) bool {
        return self.term.eof;
    }

    /// Shows why the program ended under its last output; the tab stays
    /// open until a key is pressed.
    pub fn showExit(self: *TerminalView) void {
        if (self.exited) return;
        self.exited = true;
        var buf: [96]u8 = undefined;
        const msg = if (self.term.exitStatus()) |e| switch (e) {
            .code => |code| std.fmt.bufPrint(&buf, "\r\n\x1b[0;90m[Process exited with code {d} — press any key to close]\x1b[0m", .{code}),
            .signal => |sig| std.fmt.bufPrint(&buf, "\r\n\x1b[0;90m[Process killed by signal {d} — press any key to close]\x1b[0m", .{sig}),
        } else std.fmt.bufPrint(&buf, "\r\n\x1b[0;90m[Process exited — press any key to close]\x1b[0m", .{});
        self.term.screen.write(msg catch "");
        self.term.screen.cursor_visible = false;
        self.scroll = 0;
    }

    // ---- Layout and painting ------------------------------------------------------

    fn measure(self: *TerminalView) void {
        const m = self.env.text.metrics(self.env.font);
        const s = self.env.text.scale;
        self.cell_w = m.advance;
        // Whole device pixels, so box-drawing characters meet.
        self.cell_h = @max(1, @round(m.line_height * s)) / s;
    }

    fn gridSize(self: *const TerminalView, rect: Rect) [2]u16 {
        const rows = @floor((rect.h - 2 * pad_y) / self.cell_h);
        const cols = @floor((rect.w - 2 * pad_x) / self.cell_w);
        return .{ @intFromFloat(std.math.clamp(rows, 1, 1000)), @intFromFloat(std.math.clamp(cols, 2, 2000)) };
    }

    /// Fits the grid to `rect` (the program sees the new size).
    pub fn layout(self: *TerminalView, rect: Rect) void {
        self.rect = rect;
        self.measure();
        const size = self.gridSize(rect);
        self.term.resize(size[0], size[1]);
        self.scroll = @min(self.scroll, self.term.screen.scrollbackLen());
    }

    /// Virtual row (see vterm.Screen) shown at the top of the view.
    fn topRow(self: *const TerminalView) usize {
        return self.term.screen.scrollbackLen() - @min(self.scroll, self.term.screen.scrollbackLen());
    }

    const CellPaint = struct { fg: Color, bg: ?Color, style: text_mod.FontStyle };

    fn cellPaint(screen: *const vterm.Screen, pal: *const Palette, cell: Cell) CellPaint {
        var fg_col = cell.fg;
        // Bold text in the first eight colors uses their bright versions.
        if (cell.attrs.bold) if (fg_col == .indexed and fg_col.indexed < 8) {
            fg_col = .{ .indexed = fg_col.indexed + 8 };
        };
        var fg = pal.resolve(fg_col, true);
        var bg: ?Color = if (cell.bg == .default) null else pal.resolve(cell.bg, false);
        if (cell.attrs.reverse != screen.reverse_video) {
            const swapped = fg;
            fg = bg orelse pal.bg;
            bg = swapped;
        }
        if (cell.attrs.conceal) fg = bg orelse pal.bg;
        return .{ .fg = fg, .bg = bg, .style = .init(cell.attrs.italic, cell.attrs.bold) };
    }

    /// `cursor_on` is the blink phase (the editor caret's).
    pub fn paint(self: *TerminalView, scene: *Scene, theme: *const Theme, focused: bool, cursor_on: bool) void {
        const pal = Palette.fromTheme(theme);
        const screen = self.term.screen;
        const text = self.env.text;
        const font = self.env.font;
        const m = text.metrics(font);
        const cw = self.cell_w;
        const ch = self.cell_h;
        const cols = screen.cols;
        const rows = screen.rows;
        scene.fill(self.rect, if (screen.reverse_video) pal.fg else pal.bg);
        scene.pushClip(self.rect) catch return;
        defer scene.popClip();
        self.cells.resize(self.gpa, cols) catch return;

        const ox = self.rect.x + pad_x;
        const oy = self.rect.y + pad_y;
        const baseline_off = @round((ch - (m.ascent + m.descent)) / 2 + m.ascent);
        const top = self.topRow();
        const sel = self.selectionRows();

        for (0..rows) |r| {
            const vr = top + r;
            if (vr >= screen.totalRows()) break;
            const row = screen.rowCells(vr, self.cells.items);
            const y = oy + @as(f32, @floatFromInt(r)) * ch;

            // Backgrounds, in runs of one color.
            var col: usize = 0;
            while (col < row.len) {
                const bg = cellPaint(screen, &pal, row[col]).bg;
                var end = col + 1;
                while (end < row.len and sameOptColor(cellPaint(screen, &pal, row[end]).bg, bg)) end += 1;
                if (bg) |b| scene.fill(.{ .x = ox + @as(f32, @floatFromInt(col)) * cw, .y = y, .w = @as(f32, @floatFromInt(end - col)) * cw, .h = ch }, b);
                col = end;
            }
            if (sel) |s| if (vr >= s[0].row and vr <= s[1].row) {
                const c0: usize = if (vr == s[0].row) s[0].col else 0;
                const c1: usize = if (vr == s[1].row) s[1].col else cols;
                if (c1 > c0) scene.fill(.{ .x = ox + @as(f32, @floatFromInt(c0)) * cw, .y = y, .w = @as(f32, @floatFromInt(c1 - c0)) * cw, .h = ch }, theme.selection);
            };
            self.paintRowText(scene, screen, &pal, row, ox, y, baseline_off);
            // Underline and strikethrough.
            for (row, 0..) |cell, c| {
                if (cell.width == 0 or (cell.attrs.underline == 0 and !cell.attrs.strike)) continue;
                const p = cellPaint(screen, &pal, cell);
                const x = ox + @as(f32, @floatFromInt(c)) * cw;
                const w = cw * @as(f32, @floatFromInt(cell.width));
                const t = m.underline_thickness;
                if (cell.attrs.underline != 0) {
                    const uy = y + baseline_off + @max(m.underline_position, t);
                    scene.fill(.{ .x = x, .y = uy, .w = w, .h = t }, p.fg);
                    if (cell.attrs.underline == 2) scene.fill(.{ .x = x, .y = uy + 2 * t, .w = w, .h = t }, p.fg);
                }
                if (cell.attrs.strike) scene.fill(.{ .x = x, .y = y + baseline_off - @round(m.ascent * 0.3), .w = w, .h = t }, p.fg);
            }
        }

        self.paintCursor(scene, theme, &pal, focused, cursor_on, top, ox, oy, baseline_off);

        // Where the view is in the scrollback.
        const total = screen.totalRows();
        if (self.scroll > 0 and total > rows) {
            const track_h = self.rect.h;
            const h = @max(24, track_h * @as(f32, @floatFromInt(rows)) / @as(f32, @floatFromInt(total)));
            const frac = @as(f32, @floatFromInt(top)) / @as(f32, @floatFromInt(total - rows));
            scene.quad(.{ .x = self.rect.right() - 8, .y = self.rect.y + (track_h - h) * frac, .w = 5, .h = h }, .{ .color = theme.scrollbar_puck, .radius = 2.5 });
        }
    }

    /// One row of text. Rows whose shaping lines up with the cell grid
    /// (the usual case) are drawn as one run; otherwise (fallback fonts,
    /// wide characters) each cell is drawn at its own column.
    fn paintRowText(self: *TerminalView, scene: *Scene, screen: *const vterm.Screen, pal: *const Palette, row: []const Cell, ox: f32, y: f32, baseline_off: f32) void {
        const text = self.env.text;
        const font = self.env.font;
        const cw = self.cell_w;
        var last: usize = 0;
        for (row, 0..) |cell, c| if (cell.width != 0 and cell.chars[0] != 0 and cell.chars[0] != ' ') {
            last = c + 1;
        };
        if (last == 0) return;
        self.line.clearRetainingCapacity();
        self.spans.clearRetainingCapacity();
        self.starts.clearRetainingCapacity();
        var current: ?CellPaint = null;
        for (row[0..last]) |cell| {
            self.starts.append(self.gpa, @intCast(self.line.items.len)) catch return;
            if (cell.width == 0) continue;
            const p = cellPaint(screen, pal, cell);
            if (current) |cur| if (!sameColor(cur.fg, p.fg) or cur.style != p.style) {
                self.spans.append(self.gpa, .{ .end = @intCast(self.line.items.len), .color = cur.fg, .style = cur.style }) catch return;
            };
            current = p;
            cell.appendUtf8(self.gpa, &self.line) catch return;
        }
        if (current) |cur| self.spans.append(self.gpa, .{ .end = @intCast(self.line.items.len), .color = cur.fg, .style = cur.style }) catch return;

        const shaped = text.layoutLine(self.line.items, font);
        const aligned = for (row[0..last], 0..) |cell, c| {
            if (cell.width == 0) continue;
            if (@abs(shaped.xForIndex(self.starts.items[c]) - @as(f32, @floatFromInt(c)) * cw) > 0.5) break false;
        } else true;
        if (aligned) {
            text.drawLine(scene, shaped, ox, y + baseline_off, pal.fg, self.spans.items);
            return;
        }
        for (row[0..last], 0..) |cell, c| {
            if (cell.width == 0 or cell.chars[0] == 0 or cell.chars[0] == ' ') continue;
            const a = self.starts.items[c];
            const b = if (c + 1 < last) self.starts.items[c + 1] else @as(u32, @intCast(self.line.items.len));
            if (b <= a) continue;
            const p = cellPaint(screen, pal, cell);
            const piece = text.layoutLine(self.line.items[a..b], font);
            text.drawLine(scene, piece, ox + @as(f32, @floatFromInt(c)) * cw, y + baseline_off, p.fg, &.{.{ .end = std.math.maxInt(u32), .color = p.fg, .style = p.style }});
        }
    }

    fn paintCursor(self: *TerminalView, scene: *Scene, theme: *const Theme, pal: *const Palette, focused: bool, cursor_on: bool, top: usize, ox: f32, oy: f32, baseline_off: f32) void {
        const screen = self.term.screen;
        if (!screen.cursor_visible or self.exited) return;
        const vr = screen.scrollbackLen() + screen.cursor_row;
        if (vr < top or vr >= top + screen.rows) return;
        const blinks = screen.cursor_blink and self.env.settings.caret_blink;
        if (focused and blinks and !cursor_on) return;
        const cell = screen.screenCell(screen.cursor_row, @min(screen.cursor_col, screen.cols - 1));
        const x = ox + @as(f32, @floatFromInt(screen.cursor_col)) * self.cell_w;
        const y = oy + @as(f32, @floatFromInt(vr - top)) * self.cell_h;
        const w = self.cell_w * @as(f32, @floatFromInt(@max(cell.width, 1)));
        const r: Rect = .{ .x = x, .y = y, .w = w, .h = self.cell_h };
        if (!focused) {
            scene.quad(r, .{ .border_color = theme.caret, .border_width = 1 });
            return;
        }
        switch (screen.cursor_shape) {
            .bar => scene.fill(.{ .x = x, .y = y, .w = 2, .h = self.cell_h }, theme.caret),
            .underline => scene.fill(.{ .x = x, .y = y + self.cell_h - 2, .w = w, .h = 2 }, theme.caret),
            .block => {
                // Above the text, with the character redrawn in the background color.
                scene.pushLayer() catch return;
                defer scene.popLayer();
                scene.fill(r, theme.caret);
                if (cell.width == 0 or cell.chars[0] == 0 or cell.chars[0] == ' ') return;
                self.line.clearRetainingCapacity();
                cell.appendUtf8(self.gpa, &self.line) catch return;
                const p = cellPaint(screen, pal, cell);
                const l = self.env.text.layoutLine(self.line.items, self.env.font);
                self.env.text.drawLine(scene, l, x, y + baseline_off, pal.bg, &.{.{ .end = std.math.maxInt(u32), .color = p.bg orelse pal.bg, .style = p.style }});
            },
        }
    }

    // ---- Keyboard ---------------------------------------------------------------

    fn modsOf(m: platform.Modifiers) vterm.Mods {
        return .{ .shift = m.shift, .alt = m.alt, .ctrl = m.ctrl };
    }

    /// Sends a key to the program. Returns false for keys it has no use for.
    pub fn key(self: *TerminalView, k: platform.KeyEvent) bool {
        const screen = self.term.screen;
        const mods = modsOf(k.mods);
        switch (k.key) {
            .named => |n| {
                const vk: vterm.Key = switch (n) {
                    .enter => .enter,
                    .tab => .tab,
                    .backspace => .backspace,
                    .escape => .escape,
                    .up => .up,
                    .down => .down,
                    .left => .left,
                    .right => .right,
                    .insert => .insert,
                    .delete => .delete,
                    .home => .home,
                    .end => .end,
                    .page_up => .page_up,
                    .page_down => .page_down,
                    .f1, .f2, .f3, .f4, .f5, .f6, .f7, .f8, .f9, .f10, .f11, .f12 => .{ .function = @intCast(@intFromEnum(n) - @intFromEnum(platform.NamedKey.f1) + 1) },
                    else => return false,
                };
                screen.key(vk, mods);
            },
            .char => |cp| {
                if (k.mods.ctrl or k.mods.alt) {
                    // Keys arrive unshifted; alt+shift+a is ESC A.
                    const ch = if (k.mods.shift and cp < 128 and std.ascii.isLower(@intCast(cp))) std.ascii.toUpper(@intCast(cp)) else cp;
                    screen.unichar(ch, mods);
                } else if (k.text.len > 0) {
                    screen.text(k.text);
                } else {
                    screen.unichar(cp, mods);
                }
            },
        }
        self.afterInput();
        return true;
    }

    /// Text committed by an input method.
    pub fn typeText(self: *TerminalView, t: []const u8) void {
        self.term.screen.text(t);
        self.afterInput();
    }

    pub fn paste(self: *TerminalView, t: []const u8) void {
        self.term.screen.paste(t);
        self.afterInput();
    }

    /// Typing jumps back to the live screen and drops the selection.
    fn afterInput(self: *TerminalView) void {
        self.scroll = 0;
        self.selection = null;
        self.term.flush();
    }

    /// shift+PgUp / shift+PgDn and the wheel: move through the scrollback.
    pub fn scrollBy(self: *TerminalView, lines: isize) void {
        const max: isize = @intCast(self.term.screen.scrollbackLen());
        const next = std.math.clamp(@as(isize, @intCast(self.scroll)) + lines, 0, max);
        self.scroll = @intCast(next);
    }

    pub fn pageScroll(self: *TerminalView, up: bool) void {
        const page: isize = @max(1, @as(isize, self.term.screen.rows) - 1);
        self.scrollBy(if (up) page else -page);
    }

    // ---- Selection and clipboard ---------------------------------------------------

    pub fn hasSelection(self: *const TerminalView) bool {
        const s = self.selection orelse return false;
        return s.anchor.line != s.head.line or s.anchor.col != s.head.col;
    }

    const RowCol = struct { row: usize, col: u16 };

    /// The selection as virtual rows and columns, ordered (start, end).
    fn selectionRows(self: *const TerminalView) ?[2]RowCol {
        const s = self.selection orelse return null;
        if (!self.hasSelection()) return null;
        const a, const b = if (Point.lessThan(s.head, s.anchor)) .{ s.head, s.anchor } else .{ s.anchor, s.head };
        const first = self.term.screen.firstLineId();
        if (b.line < first) return null;
        const start: RowCol = if (a.line < first) .{ .row = 0, .col = 0 } else .{ .row = @intCast(a.line - first), .col = a.col };
        return .{ start, .{ .row = @intCast(b.line - first), .col = b.col } };
    }

    /// The selected text (null without a selection).
    pub fn selectedText(self: *const TerminalView, gpa: Allocator) ?[]u8 {
        const s = self.selectionRows() orelse return null;
        return self.term.screen.textRange(gpa, s[0].row, s[0].col, s[1].row, s[1].col) catch null;
    }

    pub fn selectAll(self: *TerminalView) void {
        const screen = self.term.screen;
        const first = screen.firstLineId();
        self.selection = .{ .anchor = .{ .line = first, .col = 0 }, .head = .{ .line = first + screen.totalRows() - 1, .col = screen.cols } };
    }

    pub fn clearSelection(self: *TerminalView) void {
        self.selection = null;
    }

    // ---- Mouse --------------------------------------------------------------------

    /// The point between cells nearest `pos`, and the cell under it.
    fn pointAt(self: *const TerminalView, pos: platform.Vec2) struct { between: Point, cell: Point, screen_row: ?u16 } {
        const screen = self.term.screen;
        const fx = (pos.x - self.rect.x - pad_x) / self.cell_w;
        const fy = (pos.y - self.rect.y - pad_y) / self.cell_h;
        const row: usize = @intFromFloat(std.math.clamp(@floor(fy), 0, @as(f32, @floatFromInt(screen.rows - 1))));
        const vr = self.topRow() + row;
        const id = screen.firstLineId() + vr;
        const cols_f: f32 = @floatFromInt(screen.cols);
        const between: u16 = @intFromFloat(std.math.clamp(@round(fx), 0, cols_f));
        const cell: u16 = @intFromFloat(std.math.clamp(@floor(fx), 0, cols_f - 1));
        const sb = screen.scrollbackLen();
        return .{
            .between = .{ .line = id, .col = between },
            .cell = .{ .line = id, .col = cell },
            .screen_row = if (vr >= sb) @intCast(vr - sb) else null,
        };
    }

    /// The application asked for mouse reports (vim, htop, tmux); shift
    /// still selects.
    /// Whether the program in the terminal gets mouse clicks (it turned on
    /// mouse reporting; shift keeps them for the terminal's own selection).
    pub fn reportsMouse(self: *const TerminalView, mods: platform.Modifiers) bool {
        return self.term.screen.mouse_mode != .none and !mods.shift and self.scroll == 0;
    }

    fn buttonCode(b: platform.MouseButton) ?u8 {
        return switch (b) {
            .left => 1,
            .middle => 2,
            .right => 3,
            else => null,
        };
    }

    pub fn mouseDown(self: *TerminalView, m: platform.MouseEvent) void {
        const p = self.pointAt(m.pos);
        if (self.reportsMouse(m.mods)) {
            const code = buttonCode(m.button) orelse return;
            const row = p.screen_row orelse return;
            const screen = self.term.screen;
            screen.mouseMove(row, p.cell.col, modsOf(m.mods));
            screen.mouseButton(code, true, modsOf(m.mods));
            self.reporting_button = code;
            self.term.flush();
            return;
        }
        if (m.button != .left) return;
        self.selecting = true;
        switch (m.click_count) {
            0, 1 => {
                self.select_unit = .char;
                self.selection = .{ .anchor = p.between, .head = p.between };
            },
            2 => {
                self.select_unit = .word;
                self.unit_anchor = self.wordAt(p.cell);
                self.selection = .{ .anchor = self.unit_anchor[0], .head = self.unit_anchor[1] };
            },
            else => {
                self.select_unit = .line;
                self.unit_anchor = .{ .{ .line = p.cell.line, .col = 0 }, .{ .line = p.cell.line, .col = self.term.screen.cols } };
                self.selection = .{ .anchor = self.unit_anchor[0], .head = self.unit_anchor[1] };
            },
        }
    }

    pub fn mouseMove(self: *TerminalView, m: platform.MouseMoveEvent) void {
        const p = self.pointAt(m.pos);
        if (self.reporting_button != null or (self.term.screen.mouse_mode == .move and self.reportsMouse(m.mods))) {
            const row = p.screen_row orelse return;
            self.term.screen.mouseMove(row, p.cell.col, modsOf(m.mods));
            self.term.flush();
            return;
        }
        if (!self.selecting) return;
        const sel = &(self.selection orelse return);
        switch (self.select_unit) {
            .char => sel.head = p.between,
            .word => {
                const w = self.wordAt(p.cell);
                if (Point.lessThan(w[0], self.unit_anchor[0])) {
                    sel.* = .{ .anchor = self.unit_anchor[1], .head = w[0] };
                } else sel.* = .{ .anchor = self.unit_anchor[0], .head = w[1] };
            },
            .line => {
                if (p.cell.line < self.unit_anchor[0].line) {
                    sel.* = .{ .anchor = self.unit_anchor[1], .head = .{ .line = p.cell.line, .col = 0 } };
                } else sel.* = .{ .anchor = self.unit_anchor[0], .head = .{ .line = p.cell.line, .col = self.term.screen.cols } };
            },
        }
    }

    pub fn mouseUp(self: *TerminalView, m: platform.MouseEvent) void {
        if (self.reporting_button) |code| {
            self.term.screen.mouseButton(code, false, modsOf(m.mods));
            self.reporting_button = null;
            self.term.flush();
        }
        self.selecting = false;
        if (!self.hasSelection()) self.selection = null;
    }

    pub fn wheel(self: *TerminalView, s: platform.ScrollEvent) void {
        // Positive y moves toward newer output.
        const lines_f = if (s.unit == .lines) s.delta.y * 3 else s.delta.y / self.cell_h;
        self.scroll_rest += lines_f;
        const whole = @trunc(self.scroll_rest);
        self.scroll_rest -= whole;
        const n: isize = @intFromFloat(whole);
        if (n == 0) return;
        const screen = self.term.screen;
        if (self.reportsMouse(s.mods)) {
            const p = self.pointAt(s.pos);
            const row = p.screen_row orelse return;
            screen.mouseMove(row, p.cell.col, modsOf(s.mods));
            for (0..@abs(n)) |_| screen.mouseButton(if (n < 0) 4 else 5, true, modsOf(s.mods));
            self.term.flush();
        } else if (screen.alt_screen) {
            // Full-screen programs without mouse support (less, man) get arrow keys.
            for (0..@abs(n)) |_| screen.key(if (n < 0) .up else .down, .{});
            self.term.flush();
        } else {
            self.scrollBy(-n);
        }
    }

    /// The word around `p` (a run of non-blank, non-bracket characters).
    fn wordAt(self: *TerminalView, p: Point) [2]Point {
        const screen = self.term.screen;
        const first = screen.firstLineId();
        if (p.line < first) return .{ p, p };
        self.cells.resize(self.gpa, screen.cols) catch return .{ p, p };
        const cells = screen.rowCells(@intCast(p.line - first), self.cells.items);
        const isWord = struct {
            fn f(cell: Cell) bool {
                const cp = cell.chars[0];
                if (cell.width == 0) return true;
                if (cp == 0 or cp == ' ' or cp == '\t') return false;
                return std.mem.indexOfScalar(u21, &.{ '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>', '|', ';', ',' }, cp) == null;
            }
        }.f;
        var a: usize = p.col;
        if (!isWord(cells[a])) return .{ p, .{ .line = p.line, .col = p.col + 1 } };
        while (a > 0 and isWord(cells[a - 1])) a -= 1;
        var b: usize = p.col + 1;
        while (b < cells.len and isWord(cells[b])) b += 1;
        return .{ .{ .line = p.line, .col = @intCast(a) }, .{ .line = p.line, .col = @intCast(b) } };
    }
};

fn sameColor(a: Color, b: Color) bool {
    return a.r == b.r and a.g == b.g and a.b == b.b and a.a == b.a;
}

fn sameOptColor(a: ?Color, b: ?Color) bool {
    if (a == null or b == null) return a == null and b == null;
    return sameColor(a.?, b.?);
}
