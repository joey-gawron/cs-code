//! The terminal emulation engine: a thin wrapper over libvterm (the library
//! behind Neovim's terminal and emacs-libvterm). The rest of the editor only
//! sees `Screen`, `Cell`, `Color` and `Key`, so the engine can be swapped
//! (say, for ghostty's libghostty-vt) without touching the views.
//!
//! Lines that scroll off the top are kept in a scrollback ring. Rows are
//! addressed as one virtual buffer: scrollback lines first (oldest first),
//! then the screen. `Screen.firstLineId` makes those row numbers stable
//! while lines scroll by (selections use it).

const std = @import("std");
const c = @import("c");

const Allocator = std.mem.Allocator;

pub const Color = union(enum) {
    default,
    /// 0–15 are the theme's ANSI colors, 16–255 the xterm cube and grays.
    indexed: u8,
    rgb: [3]u8,
};

/// Bit layout matches the ZT_* flags in vterm_shim.c.
pub const Attrs = packed struct(u8) {
    bold: bool = false,
    italic: bool = false,
    /// 0 none, 1 single, 2 double, 3 curly.
    underline: u2 = 0,
    blink: bool = false,
    reverse: bool = false,
    conceal: bool = false,
    strike: bool = false,
};

pub const Cell = struct {
    /// The character and one combining mark; 0 = empty.
    chars: [2]u21 = .{ 0, 0 },
    /// 2 for a wide character, 0 for the cell a wide character covers.
    width: u2 = 1,
    attrs: Attrs = .{},
    fg: Color = .default,
    bg: Color = .default,

    pub fn isBlank(cell: Cell) bool {
        return (cell.chars[0] == 0 or cell.chars[0] == ' ') and cell.bg == .default and !cell.attrs.reverse and cell.attrs.underline == 0 and !cell.attrs.strike;
    }

    /// Appends the cell's characters as UTF-8 (a space for an empty cell).
    pub fn appendUtf8(cell: Cell, gpa: Allocator, out: *std.ArrayList(u8)) !void {
        if (cell.width == 0) return;
        if (cell.chars[0] == 0) return out.append(gpa, ' ');
        for (cell.chars) |cp| {
            if (cp == 0) break;
            var buf: [4]u8 = undefined;
            const n = std.unicode.utf8Encode(cp, &buf) catch continue;
            try out.appendSlice(gpa, buf[0..n]);
        }
    }
};

pub const CursorShape = enum { block, underline, bar };

pub const Key = union(enum) {
    enter,
    tab,
    backspace,
    escape,
    up,
    down,
    left,
    right,
    insert,
    delete,
    home,
    end,
    page_up,
    page_down,
    /// F1–F12.
    function: u8,
};

pub const Mods = packed struct(u3) {
    shift: bool = false,
    alt: bool = false,
    ctrl: bool = false,

    fn toVTerm(m: Mods) c.VTermModifier {
        return @as(u3, @bitCast(m));
    }
};

pub const MouseMode = enum { none, click, drag, move };

/// Mirrors `ZtCell` in vterm_shim.c.
const ZtCell = extern struct {
    chars: [6]u32,
    width: i32,
    flags: u32,
    fg: c.VTermColor,
    bg: c.VTermColor,
};

extern fn zt_screen_get_cell(screen: ?*const c.VTermScreen, row: c_int, col: c_int, out: *ZtCell) c_int;
extern fn zt_cells_get(cells: ?*const c.VTermScreenCell, i: c_int, out: *ZtCell) void;
extern fn zt_cells_set(cells: ?*c.VTermScreenCell, i: c_int, in: *const ZtCell) void;
extern fn zt_value_int(val: ?*const c.VTermValue) c_int;
extern fn zt_value_string(val: ?*const c.VTermValue, str: *[*c]const u8, len: *usize, initial: *c_int, final: *c_int) void;

pub const Screen = struct {
    gpa: Allocator,
    vt: *c.VTerm,
    vts: *c.VTermScreen,
    rows: u16,
    cols: u16,

    /// Lines scrolled off the top, as a ring (`sb_start` is the oldest).
    scrollback: []?[]Cell = &.{},
    sb_start: usize = 0,
    sb_len: usize = 0,
    /// Lines ever pushed minus lines popped back: gives every line a stable id.
    pushed_total: u64 = 0,

    title: std.ArrayList(u8) = .empty,
    title_pending: std.ArrayList(u8) = .empty,
    title_changed: bool = false,
    cursor_row: u16 = 0,
    cursor_col: u16 = 0,
    cursor_visible: bool = true,
    cursor_blink: bool = true,
    cursor_shape: CursorShape = .block,
    alt_screen: bool = false,
    reverse_video: bool = false,
    mouse_mode: MouseMode = .none,
    focus_reporting: bool = false,
    bell: bool = false,
    /// Something on screen changed since the view last cleared it.
    damaged: bool = true,
    /// Bytes for the child: encoded keys, pastes and replies to queries.
    output: std.ArrayList(u8) = .empty,
    /// Reused while copying a line into the scrollback.
    scratch: std.ArrayList(Cell) = .empty,

    pub const default_scrollback = 10_000;

    pub fn create(gpa: Allocator, rows: u16, cols: u16, scrollback_lines: usize) !*Screen {
        const self = try gpa.create(Screen);
        errdefer gpa.destroy(self);
        const vt = c.vterm_new(@max(rows, 1), @max(cols, 1)) orelse return error.OutOfMemory;
        errdefer c.vterm_free(vt);
        const vts = c.vterm_obtain_screen(vt) orelse return error.OutOfMemory;
        self.* = .{ .gpa = gpa, .vt = vt, .vts = vts, .rows = @max(rows, 1), .cols = @max(cols, 1) };
        self.scrollback = try gpa.alloc(?[]Cell, @max(scrollback_lines, 1));
        @memset(self.scrollback, null);
        c.vterm_set_utf8(vt, 1);
        c.vterm_output_set_callback(vt, onOutput, self);
        c.vterm_screen_set_callbacks(vts, &screen_callbacks, self);
        c.vterm_screen_set_damage_merge(vts, c.VTERM_DAMAGE_SCROLL);
        c.vterm_screen_enable_altscreen(vts, 1);
        c.vterm_screen_enable_reflow(vts, true);
        c.vterm_screen_reset(vts, 1);
        return self;
    }

    pub fn destroy(self: *Screen) void {
        const gpa = self.gpa;
        c.vterm_free(self.vt);
        self.clearScrollback();
        gpa.free(self.scrollback);
        self.title.deinit(gpa);
        self.title_pending.deinit(gpa);
        self.output.deinit(gpa);
        self.scratch.deinit(gpa);
        gpa.destroy(self);
    }

    /// Feeds output of the child process.
    pub fn write(self: *Screen, bytes: []const u8) void {
        _ = c.vterm_input_write(self.vt, bytes.ptr, bytes.len);
        c.vterm_screen_flush_damage(self.vts);
    }

    pub fn resize(self: *Screen, rows: u16, cols: u16) void {
        const r = @max(rows, 1);
        const cl = @max(cols, 1);
        if (r == self.rows and cl == self.cols) return;
        self.rows = r;
        self.cols = cl;
        c.vterm_set_size(self.vt, r, cl);
        c.vterm_screen_flush_damage(self.vts);
        self.damaged = true;
    }

    // ---- Reading cells ------------------------------------------------------

    pub fn scrollbackLen(self: *const Screen) usize {
        return self.sb_len;
    }

    /// Scrollback lines plus screen rows.
    pub fn totalRows(self: *const Screen) usize {
        return self.sb_len + self.rows;
    }

    /// Stable id of virtual row 0 (the oldest scrollback line).
    pub fn firstLineId(self: *const Screen) u64 {
        return self.pushed_total - self.sb_len;
    }

    /// Cell `col` of screen row `row`.
    pub fn screenCell(self: *const Screen, row: u16, col: u16) Cell {
        var zt: ZtCell = undefined;
        if (zt_screen_get_cell(self.vts, row, col, &zt) == 0) return .{};
        return fromZt(&zt);
    }

    /// Fills `out` (at least `cols` long) with virtual row `r`; returns `cols` cells.
    pub fn rowCells(self: *const Screen, r: usize, out: []Cell) []Cell {
        const row = out[0..self.cols];
        if (r < self.sb_len) {
            const line = self.scrollback[(self.sb_start + r) % self.scrollback.len].?;
            const n = @min(line.len, row.len);
            @memcpy(row[0..n], line[0..n]);
            @memset(row[n..], .{});
            return row;
        }
        const sr: u16 = @intCast(@min(r - self.sb_len, self.rows - 1));
        for (row, 0..) |*cell, col| cell.* = self.screenCell(sr, @intCast(col));
        return row;
    }

    /// Text of virtual rows [start_row, end_row] from `start_col` on the
    /// first row to `end_col` (exclusive) on the last; trailing blanks of
    /// each line are dropped.
    pub fn textRange(self: *const Screen, gpa: Allocator, start_row: usize, start_col: u16, end_row: usize, end_col: u16) ![]u8 {
        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(gpa);
        const cells = try gpa.alloc(Cell, self.cols);
        defer gpa.free(cells);
        var r = start_row;
        while (r <= end_row and r < self.totalRows()) : (r += 1) {
            const row = self.rowCells(r, cells);
            const from: usize = if (r == start_row) @min(start_col, row.len) else 0;
            const to: usize = if (r == end_row) @min(end_col, row.len) else row.len;
            const line_start = out.items.len;
            var col = from;
            while (col < to) : (col += 1) try row[col].appendUtf8(gpa, &out);
            while (out.items.len > line_start and out.items[out.items.len - 1] == ' ') out.items.len -= 1;
            if (r != end_row) try out.append(gpa, '\n');
        }
        return out.toOwnedSlice(gpa);
    }

    // ---- Input --------------------------------------------------------------

    pub fn key(self: *Screen, k: Key, mods: Mods) void {
        const vk: c.VTermKey = switch (k) {
            .enter => c.VTERM_KEY_ENTER,
            .tab => c.VTERM_KEY_TAB,
            .backspace => c.VTERM_KEY_BACKSPACE,
            .escape => c.VTERM_KEY_ESCAPE,
            .up => c.VTERM_KEY_UP,
            .down => c.VTERM_KEY_DOWN,
            .left => c.VTERM_KEY_LEFT,
            .right => c.VTERM_KEY_RIGHT,
            .insert => c.VTERM_KEY_INS,
            .delete => c.VTERM_KEY_DEL,
            .home => c.VTERM_KEY_HOME,
            .end => c.VTERM_KEY_END,
            .page_up => c.VTERM_KEY_PAGEUP,
            .page_down => c.VTERM_KEY_PAGEDOWN,
            .function => |n| @intCast(c.VTERM_KEY_FUNCTION_0 + @as(c_int, n)),
        };
        c.vterm_keyboard_key(self.vt, vk, mods.toVTerm());
    }

    /// A character key with modifiers (plain text should use `text`).
    pub fn unichar(self: *Screen, cp: u21, mods: Mods) void {
        // libvterm sends CSI-u for ctrl+i/j/m/[ (to tell them from Tab,
        // Enter, Esc), which shells don't understand: send the C0 byte like
        // xterm does.
        if (mods.ctrl) if (controlByte(cp)) |b| {
            if (mods.alt) self.output.append(self.gpa, 0x1b) catch {};
            self.output.append(self.gpa, b) catch {};
            return;
        };
        c.vterm_keyboard_unichar(self.vt, cp, mods.toVTerm());
    }

    /// Typed text (already UTF-8): sent as is.
    pub fn text(self: *Screen, bytes: []const u8) void {
        self.output.appendSlice(self.gpa, bytes) catch {};
    }

    /// Pasted text, bracketed when the application asked for it. Newlines
    /// become carriage returns, as a typed Enter would send.
    pub fn paste(self: *Screen, bytes: []const u8) void {
        c.vterm_keyboard_start_paste(self.vt);
        var i: usize = 0;
        while (i < bytes.len) : (i += 1) {
            const b = bytes[i];
            if (b == '\r' and i + 1 < bytes.len and bytes[i + 1] == '\n') continue;
            self.output.append(self.gpa, if (b == '\n') '\r' else b) catch {};
        }
        c.vterm_keyboard_end_paste(self.vt);
    }

    /// Mouse reporting, for applications that enabled it (`mouse_mode`).
    pub fn mouseMove(self: *Screen, row: u16, col: u16, mods: Mods) void {
        c.vterm_mouse_move(self.vt, row, col, mods.toVTerm());
    }

    /// Buttons are 1 left, 2 middle, 3 right, 4/5 wheel up/down.
    pub fn mouseButton(self: *Screen, button: u8, pressed: bool, mods: Mods) void {
        c.vterm_mouse_button(self.vt, button, pressed, mods.toVTerm());
    }

    pub fn focus(self: *Screen, focused: bool) void {
        const state = c.vterm_obtain_state(self.vt);
        if (focused) c.vterm_state_focus_in(state) else c.vterm_state_focus_out(state);
    }

    // ---- Scrollback ring --------------------------------------------------------

    fn pushLine(self: *Screen, line: []Cell) void {
        const cap = self.scrollback.len;
        if (self.sb_len == cap) {
            // Full: drop the oldest.
            if (self.scrollback[self.sb_start]) |old| self.gpa.free(old);
            self.scrollback[self.sb_start] = null;
            self.sb_start = (self.sb_start + 1) % cap;
            self.sb_len -= 1;
        }
        self.scrollback[(self.sb_start + self.sb_len) % cap] = line;
        self.sb_len += 1;
        self.pushed_total += 1;
    }

    fn popLine(self: *Screen) ?[]Cell {
        if (self.sb_len == 0) return null;
        const i = (self.sb_start + self.sb_len - 1) % self.scrollback.len;
        const line = self.scrollback[i];
        self.scrollback[i] = null;
        self.sb_len -= 1;
        self.pushed_total -= 1;
        return line;
    }

    fn clearScrollback(self: *Screen) void {
        for (self.scrollback) |*l| if (l.*) |line| {
            self.gpa.free(line);
            l.* = null;
        };
        self.sb_start = 0;
        self.sb_len = 0;
    }
};

fn controlByte(cp: u21) ?u8 {
    return switch (cp) {
        'a'...'z' => @intCast(cp - 'a' + 1),
        '@', ' ', '2' => 0,
        '[', '3' => 0x1b,
        '\\', '4' => 0x1c,
        ']', '5' => 0x1d,
        '^', '6' => 0x1e,
        '_', '7', '/' => 0x1f,
        '?', '8' => 0x7f,
        else => null,
    };
}

fn colorFrom(col: c.VTermColor, default_flag: u8) Color {
    const t = col.type;
    if (t & default_flag != 0) return .default;
    if (t & c.VTERM_COLOR_TYPE_MASK == c.VTERM_COLOR_INDEXED) return .{ .indexed = col.indexed.idx };
    return .{ .rgb = .{ col.rgb.red, col.rgb.green, col.rgb.blue } };
}

fn colorTo(col: Color, default_flag: u8) c.VTermColor {
    var out: c.VTermColor = undefined;
    switch (col) {
        .default => {
            out.indexed = .{ .type = @as(u8, c.VTERM_COLOR_INDEXED) | default_flag, .idx = 0 };
        },
        .indexed => |i| out.indexed = .{ .type = c.VTERM_COLOR_INDEXED, .idx = i },
        .rgb => |rgb| out.rgb = .{ .type = c.VTERM_COLOR_RGB, .red = rgb[0], .green = rgb[1], .blue = rgb[2] },
    }
    return out;
}

fn fromZt(zt: *const ZtCell) Cell {
    const first = zt.chars[0];
    return .{
        // The right half of a wide character is marked with (uint32_t)-1.
        .chars = if (first == 0xffff_ffff) .{ 0, 0 } else .{ @intCast(@min(first, 0x10ffff)), @intCast(@min(zt.chars[1], 0x10ffff)) },
        .width = if (first == 0xffff_ffff) 0 else @intCast(std.math.clamp(zt.width, 1, 2)),
        .attrs = @bitCast(@as(u8, @truncate(zt.flags))),
        .fg = colorFrom(zt.fg, c.VTERM_COLOR_DEFAULT_FG),
        .bg = colorFrom(zt.bg, c.VTERM_COLOR_DEFAULT_BG),
    };
}

fn toZt(cell: Cell) ZtCell {
    var zt: ZtCell = .{ .chars = @splat(0), .width = cell.width, .flags = @as(u8, @bitCast(cell.attrs)), .fg = colorTo(cell.fg, c.VTERM_COLOR_DEFAULT_FG), .bg = colorTo(cell.bg, c.VTERM_COLOR_DEFAULT_BG) };
    if (cell.width == 0) {
        zt.chars[0] = 0xffff_ffff;
        zt.width = 1;
    } else {
        zt.chars[0] = cell.chars[0];
        zt.chars[1] = cell.chars[1];
    }
    return zt;
}

// ---- libvterm callbacks -------------------------------------------------------

const screen_callbacks: c.VTermScreenCallbacks = .{
    .damage = onDamage,
    .movecursor = onMoveCursor,
    .settermprop = onTermProp,
    .bell = onBell,
    .sb_pushline = onPushLine,
    .sb_popline = onPopLine,
    .sb_clear = onClearScrollback,
};

fn screenOf(user: ?*anyopaque) *Screen {
    return @ptrCast(@alignCast(user.?));
}

fn onOutput(s: [*c]const u8, len: usize, user: ?*anyopaque) callconv(.c) void {
    const self = screenOf(user);
    self.output.appendSlice(self.gpa, s[0..len]) catch {};
}

fn onDamage(_: c.VTermRect, user: ?*anyopaque) callconv(.c) c_int {
    screenOf(user).damaged = true;
    return 1;
}

fn onMoveCursor(pos: c.VTermPos, _: c.VTermPos, visible: c_int, user: ?*anyopaque) callconv(.c) c_int {
    const self = screenOf(user);
    self.cursor_row = @intCast(@max(0, pos.row));
    self.cursor_col = @intCast(@max(0, pos.col));
    self.cursor_visible = visible != 0;
    self.damaged = true;
    return 1;
}

fn onTermProp(prop: c.VTermProp, val: ?*c.VTermValue, user: ?*anyopaque) callconv(.c) c_int {
    const self = screenOf(user);
    self.damaged = true;
    switch (prop) {
        c.VTERM_PROP_CURSORVISIBLE => self.cursor_visible = zt_value_int(val) != 0,
        c.VTERM_PROP_CURSORBLINK => self.cursor_blink = zt_value_int(val) != 0,
        c.VTERM_PROP_ALTSCREEN => self.alt_screen = zt_value_int(val) != 0,
        c.VTERM_PROP_REVERSE => self.reverse_video = zt_value_int(val) != 0,
        c.VTERM_PROP_FOCUSREPORT => self.focus_reporting = zt_value_int(val) != 0,
        c.VTERM_PROP_CURSORSHAPE => self.cursor_shape = switch (zt_value_int(val)) {
            c.VTERM_PROP_CURSORSHAPE_UNDERLINE => .underline,
            c.VTERM_PROP_CURSORSHAPE_BAR_LEFT => .bar,
            else => .block,
        },
        c.VTERM_PROP_MOUSE => self.mouse_mode = switch (zt_value_int(val)) {
            c.VTERM_PROP_MOUSE_CLICK => .click,
            c.VTERM_PROP_MOUSE_DRAG => .drag,
            c.VTERM_PROP_MOUSE_MOVE => .move,
            else => .none,
        },
        c.VTERM_PROP_TITLE => {
            // OSC 0/2 may arrive in fragments.
            var str: [*c]const u8 = null;
            var len: usize = 0;
            var initial: c_int = 0;
            var final: c_int = 0;
            zt_value_string(val, &str, &len, &initial, &final);
            if (initial != 0) self.title_pending.clearRetainingCapacity();
            if (len > 0) self.title_pending.appendSlice(self.gpa, str[0..len]) catch {};
            if (final != 0) {
                self.title.clearRetainingCapacity();
                self.title.appendSlice(self.gpa, self.title_pending.items) catch {};
                self.title_changed = true;
            }
        },
        else => return 0,
    }
    return 1;
}

fn onBell(user: ?*anyopaque) callconv(.c) c_int {
    screenOf(user).bell = true;
    return 1;
}

fn onPushLine(cols: c_int, cells: ?*const c.VTermScreenCell, user: ?*anyopaque) callconv(.c) c_int {
    const self = screenOf(user);
    // One allocation per line (output floods push thousands per second);
    // trailing blanks aren't stored.
    const line = &self.scratch;
    line.clearRetainingCapacity();
    var used: usize = 0;
    var i: c_int = 0;
    while (i < cols) : (i += 1) {
        var zt: ZtCell = undefined;
        zt_cells_get(cells, i, &zt);
        const cell = fromZt(&zt);
        line.append(self.gpa, cell) catch break;
        if (!cell.isBlank()) used = line.items.len;
    }
    const owned = self.gpa.dupe(Cell, line.items[0..used]) catch return 0;
    self.pushLine(owned);
    self.damaged = true;
    return 1;
}

fn onPopLine(cols: c_int, cells: ?*c.VTermScreenCell, user: ?*anyopaque) callconv(.c) c_int {
    const self = screenOf(user);
    const line = self.popLine() orelse return 0;
    defer self.gpa.free(line);
    var i: c_int = 0;
    while (i < cols) : (i += 1) {
        const idx: usize = @intCast(i);
        const zt = toZt(if (idx < line.len) line[idx] else .{});
        zt_cells_set(cells, i, &zt);
    }
    self.damaged = true;
    return 1;
}

fn onClearScrollback(user: ?*anyopaque) callconv(.c) c_int {
    const self = screenOf(user);
    self.clearScrollback();
    self.damaged = true;
    return 1;
}

// ---- Tests --------------------------------------------------------------------

fn rowText(screen: *Screen, r: usize) ![]u8 {
    return screen.textRange(std.testing.allocator, r, 0, r, screen.cols);
}

test "prints text and SGR colors" {
    const screen = try Screen.create(std.testing.allocator, 4, 20, 100);
    defer screen.destroy();
    screen.write("hello\r\n\x1b[31;1mred\x1b[0m \x1b[38;2;1;2;3mrgb\x1b[48;5;200mX");
    const r0 = try rowText(screen, 0);
    defer std.testing.allocator.free(r0);
    try std.testing.expectEqualStrings("hello", r0);
    const red = screen.screenCell(1, 0);
    try std.testing.expectEqual(@as(u21, 'r'), red.chars[0]);
    try std.testing.expectEqual(Color{ .indexed = 1 }, red.fg);
    try std.testing.expect(red.attrs.bold);
    try std.testing.expectEqual(Color.default, screen.screenCell(1, 3).fg);
    try std.testing.expectEqual(Color{ .rgb = .{ 1, 2, 3 } }, screen.screenCell(1, 4).fg);
    try std.testing.expectEqual(Color{ .indexed = 200 }, screen.screenCell(1, 7).bg);
    try std.testing.expectEqual(@as(u16, 1), screen.cursor_row);
    try std.testing.expectEqual(@as(u16, 8), screen.cursor_col);
}

test "wide characters, title and scrollback" {
    const screen = try Screen.create(std.testing.allocator, 3, 10, 2);
    defer screen.destroy();
    screen.write("\x1b]2;my title\x07日本");
    try std.testing.expectEqualStrings("my title", screen.title.items);
    try std.testing.expect(screen.title_changed);
    try std.testing.expectEqual(@as(u2, 2), screen.screenCell(0, 0).width);
    try std.testing.expectEqual(@as(u2, 0), screen.screenCell(0, 1).width);
    const wide = try rowText(screen, 0);
    defer std.testing.allocator.free(wide);
    try std.testing.expectEqualStrings("日本", wide);

    // Five more lines on a three-row screen: the ring keeps the newest two.
    screen.write("\r\na\r\nb\r\nc\r\nd\r\ne");
    try std.testing.expectEqual(@as(usize, 2), screen.scrollbackLen());
    try std.testing.expectEqual(@as(u64, 1), screen.firstLineId());
    const all = try screen.textRange(std.testing.allocator, 0, 0, screen.totalRows() - 1, screen.cols);
    defer std.testing.allocator.free(all);
    try std.testing.expectEqualStrings("a\nb\nc\nd\ne", all);

    // Growing the screen pulls lines back out of the scrollback.
    screen.resize(5, 10);
    try std.testing.expectEqual(@as(usize, 0), screen.scrollbackLen());
    const grown = try screen.textRange(std.testing.allocator, 0, 0, screen.totalRows() - 1, screen.cols);
    defer std.testing.allocator.free(grown);
    try std.testing.expectEqualStrings("a\nb\nc\nd\ne", grown);
}

test "key encoding" {
    const screen = try Screen.create(std.testing.allocator, 4, 20, 10);
    defer screen.destroy();
    const out = &screen.output;
    screen.key(.enter, .{});
    screen.key(.up, .{});
    screen.unichar('c', .{ .ctrl = true });
    screen.unichar('[', .{ .ctrl = true });
    screen.unichar('b', .{ .alt = true });
    screen.key(.tab, .{ .shift = true });
    screen.key(.{ .function = 5 }, .{});
    screen.text("é");
    try std.testing.expectEqualStrings("\r\x1b[A\x03\x1b\x1bb\x1b[Z\x1b[15~é", out.items);
    out.clearRetainingCapacity();

    // Application cursor keys (DECCKM) and bracketed paste, as set by the app.
    screen.write("\x1b[?1h\x1b[?2004h");
    screen.key(.up, .{});
    screen.paste("a\nb");
    try std.testing.expectEqualStrings("\x1bOA\x1b[200~a\rb\x1b[201~", out.items);
}
