//! Vim emulation ("Vintage" mode, toggled with Tools > Vim Mode or --vim).
//!
//! Normal / insert / replace / visual (char, line, block) modes with counts,
//! registers, operators × motions × text objects, dot-repeat, macros
//! (q/@), search (/ ? n N * #), marks and ex commands. Space in normal and
//! visual mode starts the workspace's leader keys (workspace/leader_ui.zig).
//! Every command applies to all cursors, so vim composes with Sublime-style
//! multi-selection.

const std = @import("std");
const platform = @import("../../framework/platform/platform.zig");
const workspace_mod = @import("../workspace/workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const keymap_mod = @import("../settings/keymap.zig");
const motions = @import("motions.zig");
const folds = @import("folds.zig");
const ex = @import("ex.zig");
const window = @import("window.zig");
const lsp_ui = @import("../workspace/lsp_ui.zig");
const vfs = @import("../../model/remote/vfs.zig");
const rpath = @import("../../model/remote/path.zig");
const ui = @import("../../framework/ui/ui.zig");
const regex_mod = @import("../../framework/util/regex.zig");

const Allocator = std.mem.Allocator;
const Editor = editor_mod.Editor;
const Selection = editor_mod.Selection;
const Workspace = workspace_mod.Workspace;
const KeyEvent = platform.KeyEvent;
const Ctx = motions.Ctx;

pub const Mode = enum { normal, insert, replace, visual, visual_line, visual_block };

const Operator = enum { delete, change, yank, indent, unindent, toggle_case, lower, upper, reindent, fold, comment };

/// A key as vim sees it: produced text for printable keys, so shift+4 is '$'.
pub const VKey = union(enum) {
    char: u21,
    ctrl: u21,
    named: platform.NamedKey,
};

fn toVKey(k: KeyEvent) VKey {
    if (k.key == .named) {
        // ctrl+[ is Escape.
        return .{ .named = k.key.named };
    }
    if (k.mods.ctrl) {
        if (k.key.char == '[') return .{ .named = .escape };
        return .{ .ctrl = k.key.char };
    }
    if (k.text.len > 0) return .{ .char = std.unicode.utf8Decode(k.text[0..(std.unicode.utf8ByteSequenceLength(k.text[0]) catch 1)]) catch k.key.char };
    return .{ .char = k.key.char };
}

/// Chords vim leaves to the Sublime keymap.
fn passThrough(k: KeyEvent) bool {
    if (k.mods.alt or k.mods.super) return true;
    if (k.key == .named) return switch (k.key.named) {
        .f1, .f2, .f3, .f4, .f5, .f6, .f7, .f8, .f9, .f10, .f11, .f12, .tab, .insert, .context_menu => true,
        else => k.mods.ctrl,
    };
    if (k.mods.ctrl) {
        if (k.mods.shift) return true;
        const handled = "rdufbeyaxvoiw[";
        return std.mem.indexOfScalar(u8, handled, @intCast(@min(k.key.char, 127))) == null;
    }
    return false;
}

const Prefix = enum { g, z, f, F, t, T, r, m, quote, backtick, q, at, register, inner, around, bracket_open, bracket_close, Z, ctrl_w };

pub const Register = struct {
    text: []u8,
    linewise: bool,
};

const MacroKeys = std.ArrayList(KeyEvent);

pub const Vim = struct {
    gpa: Allocator,
    mode: Mode = .normal,
    count: usize = 0,
    op_count: usize = 0,
    register: ?u8 = null,
    operator: ?Operator = null,
    prefix: ?Prefix = null,
    /// Typed so far for the current command (shown like 'showcmd').
    pending: [24]u8 = undefined,
    pending_len: usize = 0,

    registers: [128]?Register = @splat(null),
    last_find: ?struct { char: u21, forward: bool, till: bool } = null,
    last_search: ?[]u8 = null,
    last_search_forward: bool = true,
    marks: [26]?usize = @splat(null),

    // Dot repeat: keys of the command in progress / the last change.
    arena: std.heap.ArenaAllocator,
    cmd_keys: MacroKeys = .empty,
    last_change: MacroKeys = .empty,
    cmd_version: u64 = 0,
    recording_change: bool = false,
    replaying: usize = 0,

    // Macros (q / @).
    macro_reg: ?u8 = null,
    macros: [128]MacroKeys = @splat(MacroKeys.empty),
    last_macro: ?u8 = null,

    // Visual mode: one anchor per cursor.
    visual_anchors: std.ArrayList(usize) = .empty,
    visual_cursors: std.ArrayList(usize) = .empty,
    last_visual: ?struct { mode: Mode, anchor: usize, cursor: usize } = null,

    // Command line (: / ?).
    cmdline_buffer: ?*buffer_mod.Buffer = null,
    cmdline: ?*Editor = null,
    cmdline_kind: u8 = ':',
    last_ex: ?[]u8 = null,
    message: [128]u8 = undefined,
    message_len: usize = 0,

    target_list: std.ArrayList(usize) = .empty,

    // Counted insert (`3ix<Esc>`): the text typed so far, repeated on Escape.
    insert_repeat: usize = 1,
    insert_open_line: bool = false,
    insert_typed: std.ArrayList(u8) = .empty,

    pub fn init(gpa: Allocator) Vim {
        return .{ .gpa = gpa, .arena = .init(gpa) };
    }

    pub fn deinit(self: *Vim) void {
        for (self.registers) |r| if (r) |reg| self.gpa.free(reg.text);
        for (&self.macros) |*m| m.deinit(self.gpa);
        self.cmd_keys.deinit(self.gpa);
        self.last_change.deinit(self.gpa);
        self.visual_anchors.deinit(self.gpa);
        self.visual_cursors.deinit(self.gpa);
        self.target_list.deinit(self.gpa);
        self.insert_typed.deinit(self.gpa);
        if (self.last_search) |s| self.gpa.free(s);
        if (self.last_ex) |s| self.gpa.free(s);
        if (self.cmdline) |c| c.destroy();
        if (self.cmdline_buffer) |b| {
            b.deinit();
            self.gpa.destroy(b);
        }
        self.arena.deinit();
    }

    pub fn hook(self: *Vim) workspace_mod.KeyHook {
        return .{ .ctx = self, .handleKey = handleKeyThunk, .statusText = statusText, .paintCommandLine = paintCommandLine, .blockCaret = blockCaret, .onToggle = onToggle, .leaderState = leaderState, .recordKey = recordKey, .afterLeader = afterLeader };
    }

    fn handleKeyThunk(ctx: *anyopaque, ws: *Workspace, ed: *Editor, k: KeyEvent) bool {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        return self.handleKey(ws, ed, k);
    }

    /// Space starts the leader in normal and visual mode, unless a count,
    /// register or operator wants it (`3 SPC`, `d SPC`: vim's motion).
    fn leaderState(ctx: *anyopaque) bool {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        const idle = self.prefix == null and self.operator == null and self.count == 0 and self.register == null and self.cmdline == null;
        return idle and (self.mode == .normal or self.isVisual());
    }

    /// Keys the leader takes still go into a macro being recorded (q).
    fn recordKey(ctx: *anyopaque, k: KeyEvent) void {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        if (self.macro_reg) |reg| self.macros[reg].append(self.gpa, self.copyKey(k)) catch {};
    }

    /// After a leader command: a count or register typed before it is
    /// dropped, visual mode ends as on Escape (its region was the command's
    /// input) in the view it was in, unless the command closed it or opened
    /// an overlay (`SPC :`: the command picked there takes the region), and
    /// carets sit on characters in whichever view is active now.
    fn afterLeader(ctx: *anyopaque, ws: *Workspace, started_in: ?*Editor) void {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        if (self.mode == .insert or self.mode == .replace) return;
        self.resetPending();
        if (ws.focus == .overlay) return;
        if (self.isVisual()) if (started_in) |ed| if (ws.tabIndexOf(ed) != null) {
            self.exitVisual(ed);
            if (ws.activeEditor() == ed) return;
        };
        if (ws.activeEditor()) |ed| self.enterNormal(ed);
    }

    fn blockCaret(ctx: *anyopaque) bool {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        return self.mode != .insert and self.mode != .replace;
    }

    fn onToggle(ctx: *anyopaque, ed: ?*Editor, enabled: bool) void {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        self.resetPending();
        self.closeCmdline();
        if (ed) |e| {
            if (enabled) self.enterNormal(e) else {
                if (self.mode == .insert or self.mode == .replace) e.buffer.endGroup();
                e.caret_override.clearRetainingCapacity();
                e.block_caret = false;
            }
        }
        self.mode = .normal;
    }

    fn statusText(ctx: *anyopaque, buf: []u8) []const u8 {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        var w: std.Io.Writer = .fixed(buf);
        const name = switch (self.mode) {
            .normal => "NORMAL",
            .insert => "-- INSERT --",
            .replace => "-- REPLACE --",
            .visual => "-- VISUAL --",
            .visual_line => "-- VISUAL LINE --",
            .visual_block => "-- VISUAL BLOCK --",
        };
        w.writeAll(name) catch {};
        if (self.macro_reg) |r| w.print("  recording @{c}", .{r}) catch {};
        if (self.pending_len > 0) w.print("  {s}", .{self.pending[0..self.pending_len]}) catch {};
        if (self.message_len > 0) w.print("  {s}", .{self.message[0..self.message_len]}) catch {};
        return w.buffered();
    }

    fn paintCommandLine(ctx: *anyopaque, uictx: *const ui.Ctx, rect: ui.Rect) bool {
        const self: *Vim = @ptrCast(@alignCast(ctx));
        const cl = self.cmdline orelse return false;
        uictx.scene.fill(rect, uictx.theme.statusbar_background);
        var prefix: [1]u8 = .{self.cmdline_kind};
        const w = uictx.label(.{ .x = rect.x + 10, .y = rect.y, .w = 20, .h = rect.h }, &prefix, uictx.fonts.mono, uictx.theme.foreground);
        cl.focused = true;
        cl.layout(.{ .x = rect.x + 10 + w, .y = rect.y, .w = rect.w - 20 - w, .h = rect.h });
        cl.paint(uictx.scene, uictx.theme);
        return true;
    }

    pub fn setMessageText(self: *Vim, text: []const u8) void {
        const n = @min(text.len, self.message.len);
        @memcpy(self.message[0..n], text[0..n]);
        self.message_len = n;
    }

    fn setMessage(self: *Vim, comptime fmt: []const u8, args: anytype) void {
        const msg = std.fmt.bufPrint(&self.message, fmt, args) catch self.message[0..];
        self.message_len = msg.len;
    }

    // ---- Entry point -----------------------------------------------------

    pub fn handleKey(self: *Vim, ws: *Workspace, ed: *Editor, k: KeyEvent) bool {
        // Macro recording sees every key except the `q` that stops it.
        if (self.macro_reg) |reg| {
            const stops = self.mode == .normal and self.prefix == null and self.cmdline == null and toVKey(k) == .char and toVKey(k).char == 'q';
            if (!stops) self.macros[reg].append(self.gpa, self.copyKey(k)) catch {};
        }
        if (self.cmdline != null) return self.cmdlineKey(ws, ed, k);
        self.message_len = 0;
        return switch (self.mode) {
            .insert, .replace => self.insertKey(ws, ed, k),
            .normal => self.normalKey(ws, ed, k),
            .visual, .visual_line, .visual_block => self.visualKey(ws, ed, k),
        };
    }

    fn copyKey(self: *Vim, k: KeyEvent) KeyEvent {
        return .{ .key = k.key, .mods = k.mods, .repeat = k.repeat, .text = self.arena.allocator().dupe(u8, k.text) catch "" };
    }

    fn resetPending(self: *Vim) void {
        self.count = 0;
        self.op_count = 0;
        self.register = null;
        self.operator = null;
        self.prefix = null;
        self.pending_len = 0;
    }

    fn pushPending(self: *Vim, k: KeyEvent) void {
        const t = if (k.text.len > 0 and !k.mods.ctrl) k.text else "";
        if (self.pending_len + t.len <= self.pending.len) {
            @memcpy(self.pending[self.pending_len..][0..t.len], t);
            self.pending_len += t.len;
        }
    }

    fn totalCount(self: *const Vim) usize {
        return @max(self.count, 1) * @max(self.op_count, 1);
    }

    pub fn enterNormal(self: *Vim, ed: *Editor) void {
        self.mode = .normal;
        ed.block_caret = true;
        ed.caret_override.clearRetainingCapacity();
        var c = Ctx.init(ed.buffer);
        for (ed.selections.items) |*sel| sel.* = .caret(c.clampNormal(sel.head));
        ed.normalize();
    }

    fn enterInsert(self: *Vim, ed: *Editor) void {
        self.insert_repeat = 1;
        self.insert_open_line = false;
        self.insert_typed.clearRetainingCapacity();
        self.mode = .insert;
        ed.block_caret = false;
        ed.caret_override.clearRetainingCapacity();
        ed.touchCaret();
    }

    // ---- Insert mode ------------------------------------------------------

    fn insertKey(self: *Vim, ws: *Workspace, ed: *Editor, k: KeyEvent) bool {
        if (self.recording_change) self.cmd_keys.append(self.gpa, self.copyKey(k)) catch {};
        const vk = toVKey(k);
        if (self.insert_repeat > 1) self.trackTyped(vk, k);
        switch (vk) {
            .named => |n| if (n == .escape) {
                self.leaveInsert(ed);
                return true;
            },
            .ctrl => |ch| switch (ch) {
                'w' => {
                    ed.deleteMotion(.word_left);
                    return true;
                },
                'u' => {
                    ed.deleteMotion(.line_start);
                    return true;
                },
                'h' => {
                    ed.deleteBackward();
                    return true;
                },
                'c' => {
                    self.leaveInsert(ed);
                    return true;
                },
                else => {},
            },
            .char => if (self.mode == .replace and k.text.len > 0) {
                // Overwrite the char under each cursor (but never the newline).
                var c = Ctx.init(ed.buffer);
                for (ed.selections.items) |*sel| {
                    if (sel.isEmpty() and sel.head < c.lineEnd(sel.head)) sel.* = .range(sel.head, c.s.nextBoundary(sel.head));
                }
                ed.insertText(k.text);
                return true;
            },
        }
        _ = ws;
        return false;
    }

    /// Keeps the text of a counted insert, to type it again on Escape.
    fn trackTyped(self: *Vim, vk: VKey, k: KeyEvent) void {
        switch (vk) {
            .char => if (k.text.len > 0) self.insert_typed.appendSlice(self.gpa, k.text) catch {},
            .named => |n| switch (n) {
                .enter => self.insert_typed.append(self.gpa, '\n') catch {},
                .backspace => {
                    var i = self.insert_typed.items.len;
                    while (i > 0) {
                        i -= 1;
                        if (self.insert_typed.items[i] & 0xC0 != 0x80) break;
                    }
                    self.insert_typed.shrinkRetainingCapacity(i);
                },
                else => {},
            },
            .ctrl => {},
        }
    }

    fn leaveInsert(self: *Vim, ed: *Editor) void {
        if (self.insert_repeat > 1 and self.insert_typed.items.len > 0 and self.mode == .insert) {
            const typed = self.gpa.dupe(u8, self.insert_typed.items) catch null;
            if (typed) |text| {
                defer self.gpa.free(text);
                for (1..self.insert_repeat) |_| {
                    if (self.insert_open_line) ed.insertText("\n");
                    ed.insertText(text);
                }
            }
        }
        self.insert_repeat = 1;
        // The cursor steps back onto the last inserted char.
        var c = Ctx.init(ed.buffer);
        for (ed.selections.items) |*sel| {
            const h = sel.head;
            const ls = c.lineStart(h);
            sel.* = .caret(if (h > ls) c.s.prevBoundary(h) else h);
        }
        ed.buffer.endGroup();
        if (self.recording_change) {
            self.recording_change = false;
            self.saveChange();
        }
        self.enterNormal(ed);
    }

    fn saveChange(self: *Vim) void {
        if (self.replaying > 0) return;
        self.last_change.clearRetainingCapacity();
        self.last_change.appendSlice(self.gpa, self.cmd_keys.items) catch {};
    }

    // ---- Normal mode ------------------------------------------------------

    fn normalKey(self: *Vim, ws: *Workspace, ed: *Editor, k: KeyEvent) bool {
        if (self.pending_len == 0 and self.prefix == null and self.operator == null and self.count == 0 and self.register == null) {
            self.cmd_keys.clearRetainingCapacity();
            self.cmd_version = ed.buffer.version;
        }
        // Keys vim doesn't use (ctrl+p, ctrl+s, F12, alt+...) go to the regular keymap.
        if (self.prefix == null and self.operator == null and passThrough(k)) {
            self.resetPending();
            return false;
        }
        self.cmd_keys.append(self.gpa, self.copyKey(k)) catch {};
        self.pushPending(k);
        const vk = toVKey(k);

        const done = self.normalCommand(ws, ed, vk) catch true;
        if (done and ws.activeEditor() != ed) {
            // The command moved to another view (or closed this one).
            self.resetPending();
            if (ws.activeEditor()) |cur| self.enterNormal(cur);
            return true;
        }
        if (done) {
            const changed = ed.buffer.version != self.cmd_version;
            if (self.mode == .insert or self.mode == .replace) {
                self.recording_change = true;
            } else if (changed) {
                self.saveChange();
            }
            if (self.mode == .normal) {
                // A cursor that landed in a closed fold ('foldopen').
                folds.afterCommand(ed, k);
                var c = Ctx.init(ed.buffer);
                for (ed.selections.items) |*sel| if (sel.isEmpty()) {
                    sel.* = .caret(c.clampNormal(sel.head));
                };
                ed.normalize();
                ed.block_caret = true;
            }
            self.resetPending();
            ed.touchCaret();
        }
        return true;
    }

    /// Returns true when the command is complete (or invalid).
    fn normalCommand(self: *Vim, ws: *Workspace, ed: *Editor, vk: VKey) !bool {
        if (self.prefix) |prefix| return self.prefixed(ws, ed, prefix, vk);

        // Counts ("0" is a motion unless a count is in progress).
        if (vk == .char and vk.char >= '0' and vk.char <= '9' and (vk.char != '0' or self.count > 0 or self.op_count > 0)) {
            const d: usize = vk.char - '0';
            if (self.operator != null) self.op_count = self.op_count * 10 + d else self.count = self.count * 10 + d;
            return false;
        }

        if (self.operator) |op| {
            // dd, cc, yy, >>, <<, g~~, guu, gUU: linewise on count lines.
            const same = switch (vk) {
                .char => |ch| switch (op) {
                    .delete => ch == 'd',
                    .change => ch == 'c',
                    .yank => ch == 'y',
                    .indent => ch == '>',
                    .unindent => ch == '<',
                    .reindent => ch == '=',
                    .toggle_case => ch == '~',
                    .lower => ch == 'u',
                    .upper => ch == 'U',
                    .comment => ch == 'c',
                    // zfzf goes through the z prefix.
                    .fold => false,
                },
                else => false,
            };
            if (same) {
                self.operateLines(ed, op, self.totalCount());
                return true;
            }
            switch (vk) {
                .char => |ch| switch (ch) {
                    'i' => {
                        self.prefix = .inner;
                        return false;
                    },
                    'a' => {
                        self.prefix = .around;
                        return false;
                    },
                    else => {},
                },
                .named => |n| if (n == .escape) return true,
                else => {},
            }
            if (try self.motionTargets(ws, ed, vk)) |kind| {
                self.applyOperatorToMotion(ed, op, kind);
                return true;
            }
            return self.prefix == null;
        }

        switch (vk) {
            .named => |n| switch (n) {
                .escape => {
                    ed.singleSelection();
                    ed.highlights.clearRetainingCapacity();
                    return true;
                },
                else => {
                    _ = try self.motionTargets(ws, ed, vk);
                    return true;
                },
            },
            .ctrl => |ch| switch (ch) {
                'r' => {
                    var i: usize = 0;
                    while (i < @max(self.count, 1)) : (i += 1) ed.redo();
                    return true;
                },
                'd', 'u' => {
                    const half: f32 = @as(f32, @floatFromInt(ed.visibleRows() / 2)) * ed.lineHeight();
                    ed.scroll_y += if (ch == 'd') half else -half;
                    ed.clampScroll();
                    self.moveVertical(ed, @intCast(ed.visibleRows() / 2), ch == 'd');
                    return true;
                },
                'f', 'b' => {
                    _ = ed.perform(if (ch == 'f') .move_page_down else .move_page_up);
                    return true;
                },
                'e', 'y' => {
                    _ = ed.perform(if (ch == 'e') .scroll_line_down else .scroll_line_up);
                    return true;
                },
                'a', 'x' => {
                    self.increment(ed, if (ch == 'a') @intCast(@max(self.count, 1)) else -@as(i64, @intCast(@max(self.count, 1))));
                    return true;
                },
                'v' => {
                    self.enterVisual(ed, .visual_block);
                    return true;
                },
                'o' => {
                    ws.perform(.soft_undo);
                    return true;
                },
                'i' => {
                    ws.perform(.soft_redo);
                    return true;
                },
                'w' => return window.begin(self),
                else => return true,
            },
            .char => |ch| switch (ch) {
                'd' => self.operator = .delete,
                'c' => self.operator = .change,
                'y' => self.operator = .yank,
                '>' => self.operator = .indent,
                '<' => self.operator = .unindent,
                '=' => self.operator = .reindent,
                'g' => self.prefix = .g,
                'z' => self.prefix = .z,
                'Z' => self.prefix = .Z,
                'f' => self.prefix = .f,
                'F' => self.prefix = .F,
                't' => self.prefix = .t,
                'T' => self.prefix = .T,
                'r' => self.prefix = .r,
                'm' => self.prefix = .m,
                '\'' => self.prefix = .quote,
                '`' => self.prefix = .backtick,
                'q' => {
                    if (self.macro_reg) |reg| {
                        self.macro_reg = null;
                        self.setMessage("recorded @{c}", .{reg});
                        return true;
                    }
                    self.prefix = .q;
                },
                '@' => self.prefix = .at,
                '"' => self.prefix = .register,
                '[' => self.prefix = .bracket_open,
                ']' => self.prefix = .bracket_close,
                'i' => {
                    ed.buffer.beginGroup();
                    const times = self.totalCount();
                    self.enterInsert(ed);
                    self.insert_repeat = times;
                    return true;
                },
                'a' => {
                    ed.buffer.beginGroup();
                    var c = Ctx.init(ed.buffer);
                    for (ed.selections.items) |*sel| {
                        const h = sel.head;
                        sel.* = .caret(if (h < c.lineEnd(h)) c.s.nextBoundary(h) else h);
                    }
                    const times = self.totalCount();
                    self.enterInsert(ed);
                    self.insert_repeat = times;
                    return true;
                },
                'I' => {
                    ed.buffer.beginGroup();
                    var c = Ctx.init(ed.buffer);
                    for (ed.selections.items) |*sel| sel.* = .caret(c.firstNonBlank(c.row(sel.head)));
                    const times = self.totalCount();
                    self.enterInsert(ed);
                    self.insert_repeat = times;
                    return true;
                },
                'A' => {
                    ed.buffer.beginGroup();
                    var c = Ctx.init(ed.buffer);
                    for (ed.selections.items) |*sel| sel.* = .caret(c.lineEnd(sel.head));
                    const times = self.totalCount();
                    self.enterInsert(ed);
                    self.insert_repeat = times;
                    return true;
                },
                'o', 'O' => {
                    ed.buffer.beginGroup();
                    // On a closed fold: below its last line / above its first.
                    if (ch == 'o') folds.openLineBelow(ed) else ed.insertLine(false);
                    const times = self.totalCount();
                    self.enterInsert(ed);
                    self.insert_repeat = times;
                    self.insert_open_line = true;
                    return true;
                },
                'R' => {
                    ed.buffer.beginGroup();
                    self.enterInsert(ed);
                    self.mode = .replace;
                    return true;
                },
                'v' => {
                    self.enterVisual(ed, .visual);
                    return true;
                },
                'V' => {
                    self.enterVisual(ed, .visual_line);
                    return true;
                },
                'x', 'X', 's' => {
                    var c = Ctx.init(ed.buffer);
                    const n = @max(self.count, 1);
                    const ranges = try self.gpa.alloc(motions.Range, ed.selections.items.len);
                    defer self.gpa.free(ranges);
                    for (ed.selections.items, 0..) |sel, i| {
                        var a = sel.head;
                        var b = sel.head;
                        const ls = c.lineStart(a);
                        const le = c.lineEnd(a);
                        var j: usize = 0;
                        while (j < n) : (j += 1) {
                            if (ch == 'X') {
                                if (a > ls) a = c.s.prevBoundary(a);
                            } else if (b < le) b = c.s.nextBoundary(b);
                        }
                        ranges[i] = .{ .start = a, .end = b };
                    }
                    // dl / dh / cl: a closed fold goes whole.
                    folds.includeClosed(ed, ranges);
                    self.operate(ed, if (ch == 's') .change else .delete, ranges);
                    return true;
                },
                'S' => {
                    self.operateLines(ed, .change, @max(self.count, 1));
                    return true;
                },
                'D', 'C' => {
                    var c = Ctx.init(ed.buffer);
                    const ranges = try self.gpa.alloc(motions.Range, ed.selections.items.len);
                    defer self.gpa.free(ranges);
                    for (ed.selections.items, 0..) |sel, i| {
                        const r = c.row(sel.head) + @as(u32, @intCast(@max(self.count, 1) - 1));
                        ranges[i] = .{ .start = sel.head, .end = ed.buffer.lineEnd(@min(r, ed.buffer.lineCount() - 1)) };
                    }
                    folds.includeClosed(ed, ranges);
                    self.operate(ed, if (ch == 'D') .delete else .change, ranges);
                    return true;
                },
                'Y' => {
                    self.operateLines(ed, .yank, @max(self.count, 1));
                    return true;
                },
                'p', 'P' => {
                    self.put(ed, ch == 'p', @max(self.count, 1));
                    return true;
                },
                'J' => {
                    self.join(ed, @max(self.count, 2), true);
                    return true;
                },
                'u' => {
                    var i: usize = 0;
                    while (i < @max(self.count, 1)) : (i += 1) ed.undo();
                    return true;
                },
                '.' => {
                    self.repeatChange(ws, ed);
                    return true;
                },
                '~' => {
                    var c = Ctx.init(ed.buffer);
                    const ranges = try self.gpa.alloc(motions.Range, ed.selections.items.len);
                    defer self.gpa.free(ranges);
                    for (ed.selections.items, 0..) |sel, i| {
                        var b = sel.head;
                        var j: usize = 0;
                        while (j < @max(self.count, 1) and b < c.lineEnd(sel.head)) : (j += 1) b = c.s.nextBoundary(b);
                        ranges[i] = .{ .start = sel.head, .end = b };
                    }
                    self.operate(ed, .toggle_case, ranges);
                    for (ed.selections.items, ranges) |*sel, r| sel.* = .caret(c.clampNormal(r.end));
                    return true;
                },
                '*', '#' => {
                    self.searchWordUnderCursor(ed, ch == '*');
                    return true;
                },
                'K' => {
                    ws.perform(.lsp_hover);
                    return true;
                },
                'n', 'N' => {
                    self.searchNext(ed, ch == 'n', @max(self.count, 1));
                    return true;
                },
                '/', '?', ':' => {
                    self.openCmdline(ws, ed, @intCast(ch));
                    return true;
                },
                '&' => {
                    if (self.last_ex) |cmd| ex.execute(self, ws, ed, cmd);
                    return true;
                },
                else => {
                    _ = try self.motionTargets(ws, ed, vk);
                    if (self.prefix == null) return true;
                },
            },
        }
        return false;
    }

    fn prefixed(self: *Vim, ws: *Workspace, ed: *Editor, prefix: Prefix, vk: VKey) !bool {
        self.prefix = null;
        if (prefix == .ctrl_w) return window.key(self, ws, vk);
        if (vk == .named and vk.named == .escape) return true;
        const ch: u21 = switch (vk) {
            .char => |c| c,
            else => return true,
        };
        switch (prefix) {
            .register => {
                if (ch < 128) self.register = @intCast(ch);
                return false;
            },
            .q => {
                if (ch < 128 and (std.ascii.isAlphanumeric(@intCast(ch)))) {
                    const reg: u8 = std.ascii.toLower(@intCast(ch));
                    // Uppercase register appends.
                    if (!std.ascii.isUpper(@intCast(ch))) self.macros[reg].clearRetainingCapacity();
                    self.macro_reg = reg;
                }
                return true;
            },
            .at => {
                const reg: u8 = if (ch == '@') self.last_macro orelse return true else if (ch == ':') ':' else @intCast(@min(ch, 127));
                if (reg == ':') {
                    if (self.last_ex) |cmd| ex.execute(self, ws, ed, cmd);
                    return true;
                }
                self.last_macro = reg;
                // The keys replay with nothing pending (`2@a`'s count isn't
                // the first key's, and SPC starts the leader).
                const times = @max(self.count, 1);
                self.resetPending();
                self.playMacro(ws, reg, times);
                return true;
            },
            .m => {
                if (ch >= 'a' and ch <= 'z') self.marks[ch - 'a'] = ed.primarySelection().head;
                return true;
            },
            .r => {
                var buf: [4]u8 = undefined;
                const n = std.unicode.utf8Encode(ch, &buf) catch return true;
                self.replaceChars(ed, buf[0..n], @max(self.count, 1));
                return true;
            },
            .Z => {
                // :x and :q! (a split closes its window).
                if (ch == 'Z') ex.execute(self, ws, ed, "x") else if (ch == 'Q') ex.execute(self, ws, ed, "q!");
                return true;
            },
            .z => {
                // Folding (zo zc za zf zj ... see folds.zig), else scrolling.
                if (folds.normal(self, ed, ch)) |done| return done;
                const lh = ed.lineHeight();
                // Display rows: closed folds and wrapped lines as drawn.
                const row: f32 = @floatFromInt(ed.displayRowOfOffset(ed.primarySelection().head));
                switch (ch) {
                    'z', '.' => ed.scroll_y = row * lh - ed.text_rect.h / 2 + lh / 2,
                    't', '\r' => ed.scroll_y = row * lh,
                    'b', '-' => ed.scroll_y = row * lh - ed.text_rect.h + lh,
                    else => {},
                }
                ed.clampScroll();
                return true;
            },
            .g => {
                if (self.operator == null) switch (ch) {
                    'v' => {
                        if (self.last_visual) |lv| {
                            self.enterVisual(ed, lv.mode);
                            self.visual_anchors.items[0] = lv.anchor;
                            self.visual_cursors.items[0] = lv.cursor;
                            self.syncVisual(ed);
                        }
                        return true;
                    },
                    'i' => {
                        ed.buffer.beginGroup();
                        self.enterInsert(ed);
                        return true;
                    },
                    'J' => {
                        self.join(ed, @max(self.count, 2), false);
                        return true;
                    },
                    'd' => {
                        // The language server's definition, else the word's
                        // first occurrence in the file.
                        if (!lsp_ui.goto(ws, ed, .definition, true)) _ = lsp_ui.gotoFirstOccurrence(ed, ed.primarySelection().head);
                        return true;
                    },
                    'D', 'r', 'I', 'x' => {
                        ws.perform(switch (ch) {
                            'I' => .lsp_goto_implementation,
                            'x' => .org_open_link,
                            else => .lsp_find_references,
                        });
                        return true;
                    },
                    'f' => {
                        self.gotoFile(ws, ed);
                        return true;
                    },
                    '~', 'u', 'U', 'c' => {
                        self.operator = switch (ch) {
                            '~' => .toggle_case,
                            'u' => .lower,
                            'c' => .comment,
                            else => .upper,
                        };
                        return false;
                    },
                    't', 'T' => {
                        window.gotoTab(ws, ch == 't', self.count);
                        return true;
                    },
                    else => {},
                };
                if (self.operator != null) switch (ch) {
                    '~', 'u', 'U', 'c' => {
                        // g~g~ / gugu / gUgU / gcgc
                        const op: Operator = switch (ch) {
                            '~' => .toggle_case,
                            'u' => .lower,
                            'c' => .comment,
                            else => .upper,
                        };
                        if (self.operator == op) {
                            self.operateLines(ed, op, self.totalCount());
                            return true;
                        }
                    },
                    else => {},
                };
                // g-motions (gg, ge, gE, g_, gj, gk).
                self.prefix = null;
                const kind = try self.gMotion(ed, ch) orelse return true;
                if (self.operator) |op| self.applyOperatorToMotion(ed, op, kind);
                return true;
            },
            .inner, .around => {
                const op = self.operator;
                const ranges = try self.textObject(ed, ch, prefix == .around) orelse return true;
                defer self.gpa.free(ranges);
                folds.includeClosed(ed, ranges);
                if (op) |o| self.operate(ed, o, ranges);
                return true;
            },
            .f, .F, .t, .T, .quote, .backtick, .bracket_open, .bracket_close => {
                const bracket = prefix == .bracket_open or prefix == .bracket_close;
                if (bracket and self.operator == null and self.bracketCommand(ws, ed, prefix == .bracket_close, ch)) return true;
                const kind = try self.prefixedMotion(ed, prefix, ch) orelse return true;
                if (self.operator) |op| self.applyOperatorToMotion(ed, op, kind);
                return true;
            },
            .ctrl_w => unreachable,
        }
    }

    /// ]d [d diagnostics, ]e [e errors, ]b [b tabs, ]t [t TODO keywords
    /// and ]SPC [SPC blank lines, as in Doom (vim-unimpaired's); false for
    /// other keys.
    fn bracketCommand(self: *Vim, ws: *Workspace, ed: *Editor, forward: bool, ch: u21) bool {
        const n = @max(self.count, 1);
        switch (ch) {
            'd', 'e', 'b' => for (0..n) |_| ws.perform(switch (ch) {
                'd' => if (forward) .lsp_next_diagnostic else .lsp_prev_diagnostic,
                'e' => if (forward) .lsp_next_error else .lsp_prev_error,
                else => if (forward) .next_tab else .prev_tab,
            }),
            't' => self.gotoTodo(ed, forward, n),
            ' ' => blankLines(ed, forward, n),
            else => return false,
        }
        return true;
    }

    /// ]t / [t: the `n`th line below (above) with a TODO keyword, wrapping
    /// around; the cursor goes to the keyword.
    fn gotoTodo(self: *Vim, ed: *Editor, forward: bool, n: usize) void {
        var re = regex_mod.Regex.compile(self.gpa, workspace_mod.todo_pattern, .{}) catch return;
        defer re.deinit();
        var line: std.ArrayList(u8) = .empty;
        defer line.deinit(self.gpa);
        const rows = ed.buffer.lineCount();
        var row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
        var target: ?usize = null;
        var found: usize = 0;
        // A whole round without one ends the search.
        var misses: u32 = 0;
        while (found < n and misses < rows) {
            row = if (forward) (row + 1) % rows else (row + rows - 1) % rows;
            const text = ed.buffer.lineText(row, &line) catch return;
            const m = re.search(text, 0) orelse {
                misses += 1;
                continue;
            };
            target = ed.buffer.lineStart(row) + m.start;
            found += 1;
            misses = 0;
        }
        const at = target orelse return self.setMessage("No TODO keywords", .{});
        ed.pushSelectionHistory();
        ed.setSingleSelection(.caret(at));
        ed.ensureCaretVisible(false);
    }

    /// ]SPC / [SPC: `n` blank lines below (above) each cursor's line; the
    /// cursors stay on their text.
    fn blankLines(ed: *Editor, below: bool, n: usize) void {
        var arena = std.heap.ArenaAllocator.init(ed.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const text = alloc.alloc(u8, n) catch return;
        @memset(text, '\n');
        var c = Ctx.init(ed.buffer);
        var edits: std.ArrayList(editor_mod.Edit) = .empty;
        const sels = alloc.dupe(Selection, ed.selections.items) catch return;
        for (sels) |*sel| {
            const at = if (below) c.lineEnd(sel.head) else c.lineStart(sel.head);
            if (edits.items.len == 0 or edits.items[edits.items.len - 1].start != at) edits.append(alloc, .{ .start = at, .end = at, .text = text }) catch return;
            // Lines added above, by this cursor's edit or earlier ones, push it down.
            const before = edits.items.len - @intFromBool(below);
            sel.* = .caret(sel.head + before * n);
        }
        ed.commit(edits.items, sels, .other);
    }

    // ---- Motions ----------------------------------------------------------

    /// Per-cursor motion targets of the last computed motion.
    fn targets(self: *Vim) *std.ArrayList(usize) {
        return &self.target_list;
    }

    /// Computes motion targets for `vk`. In normal mode (no operator) the
    /// cursors move; with an operator the targets are left for the caller.
    /// Returns null if `vk` isn't a motion (or awaits more keys).
    fn motionTargets(self: *Vim, ws: *Workspace, ed: *Editor, vk: VKey) !?motions.Kind {
        if (!self.isVisual() or self.operator != null) return self.motionTargetsFrom(ws, ed, vk);
        // A motion starts at the visual cursor, not at the head of the
        // selection shown (a forward selection's head is one past its cursor,
        // and a block has a selection per row).
        ed.selections.clearRetainingCapacity();
        for (self.visual_cursors.items) |cur| ed.selections.append(ed.gpa, .caret(cur)) catch {};
        const kind = self.motionTargetsFrom(ws, ed, vk);
        if (self.isVisual()) self.syncVisual(ed);
        return kind;
    }

    fn motionTargetsFrom(self: *Vim, ws: *Workspace, ed: *Editor, vk: VKey) !?motions.Kind {
        _ = ws;
        var c = Ctx.init(ed.buffer);
        const n = self.totalCount();
        const t = self.targets();
        t.clearRetainingCapacity();
        var kind: motions.Kind = .exclusive;
        const for_op = self.operator != null;
        const ch: u21 = switch (vk) {
            .char => |x| x,
            .named => |nk| switch (nk) {
                .left, .backspace => 'h',
                .right => 'l',
                .up => 'k',
                .down => 'j',
                .home => '0',
                .end => '$',
                .enter => '+',
                .page_down, .page_up => {
                    _ = ed.perform(if (nk == .page_down) .move_page_down else .move_page_up);
                    return null;
                },
                else => return null,
            },
            else => return null,
        };
        switch (ch) {
            'f', 'F', 't', 'T', 'g', '\'', '`', '[', ']', 'z' => {
                self.prefix = switch (ch) {
                    'f' => .f,
                    'F' => .F,
                    't' => .t,
                    'T' => .T,
                    'g' => .g,
                    '\'' => .quote,
                    '`' => .backtick,
                    '[' => .bracket_open,
                    'z' => .z,
                    else => .bracket_close,
                };
                return null;
            },
            'j', 'k', '+', '-', '_' => {
                kind = .linewise;
                if (!for_op and (ch == 'j' or ch == 'k')) {
                    self.moveVertical(ed, n, ch == 'j');
                    return kind;
                }
                for (ed.selections.items) |sel| {
                    const r = c.row(sel.head);
                    // A closed fold counts as one line.
                    const nr: u32 = switch (ch) {
                        'j', '+' => folds.linesDown(ed, r, n),
                        'k', '-' => folds.linesUp(ed, r, n),
                        else => folds.linesDown(ed, r, n - 1),
                    };
                    try t.append(self.gpa, if (ch == 'j' or ch == 'k') ed.buffer.lineStart(nr) else c.firstNonBlank(nr));
                }
            },
            else => {
                for (ed.selections.items) |sel| {
                    const h = sel.head;
                    const target: usize = switch (ch) {
                        'h' => blk: {
                            var o = h;
                            var i: usize = 0;
                            while (i < n and o > c.lineStart(h)) : (i += 1) o = c.s.prevBoundary(o);
                            break :blk o;
                        },
                        'l', ' ' => blk: {
                            var o = h;
                            var i: usize = 0;
                            const limit = if (for_op) c.lineEnd(h) else c.lastChar(c.row(h));
                            while (i < n and o < limit) : (i += 1) o = c.s.nextBoundary(o);
                            break :blk o;
                        },
                        'w', 'W' => blk: {
                            var o = h;
                            var i: usize = 0;
                            while (i < n) : (i += 1) o = c.wordForward(o, ch == 'W', for_op and self.operator == .change);
                            // dw at the end of a line stops before the newline.
                            if (for_op and c.row(o) > c.row(h) and self.operator != .change) {
                                const le = c.lineEnd(h);
                                if (o > le) o = le;
                            }
                            break :blk o;
                        },
                        'b', 'B' => blk: {
                            var o = h;
                            var i: usize = 0;
                            while (i < n) : (i += 1) o = c.wordBackward(o, ch == 'B');
                            break :blk o;
                        },
                        'e', 'E' => blk: {
                            kind = .inclusive;
                            var o = h;
                            var i: usize = 0;
                            while (i < n) : (i += 1) o = c.wordEnd(o, ch == 'E', false);
                            break :blk o;
                        },
                        '0' => c.lineStart(h),
                        '^' => c.firstNonBlank(c.row(h)),
                        '$' => blk: {
                            kind = .inclusive;
                            const r: u32 = @intCast(@min(ed.buffer.lineCount() - 1, c.row(h) + n - 1));
                            break :blk if (for_op) c.lastChar(r) else c.lastChar(r);
                        },
                        '|' => blk: {
                            var o = c.lineStart(h);
                            var i: usize = 1;
                            while (i < n and o < c.lineEnd(h)) : (i += 1) o = c.s.nextBoundary(o);
                            break :blk o;
                        },
                        'G' => blk: {
                            kind = .linewise;
                            const r: u32 = if (self.count == 0 and self.op_count == 0) ed.buffer.lineCount() - 1 else @intCast(@min(ed.buffer.lineCount(), n) - 1);
                            break :blk c.firstNonBlank(r);
                        },
                        '%' => blk: {
                            kind = .inclusive;
                            if (self.count > 0) {
                                // N% goes to that percentage of the file.
                                kind = .linewise;
                                const r: u32 = @intCast((ed.buffer.lineCount() * @min(self.count, 100) + 99) / 100 -| 1);
                                break :blk c.firstNonBlank(r);
                            }
                            break :blk c.matchingBracket(h) orelse h;
                        },
                        '}' => c.paragraphForward(h, n),
                        '{' => c.paragraphBackward(h, n),
                        'H', 'L', 'M' => blk: {
                            kind = .linewise;
                            const lh = ed.lineHeight();
                            const first: u32 = @intFromFloat(@ceil(ed.scroll_y / lh));
                            const rows = ed.visibleRows();
                            const last = @min(ed.buffer.lineCount() - 1, first + rows - 1);
                            const r: u32 = switch (ch) {
                                'H' => @min(last, first + @as(u32, @intCast(@max(self.count, 1) - 1))),
                                'L' => last -| @as(u32, @intCast(@max(self.count, 1) - 1)),
                                else => first + (last - first) / 2,
                            };
                            break :blk c.firstNonBlank(r);
                        },
                        ';', ',' => blk: {
                            const lf = self.last_find orelse break :blk h;
                            const fwd = if (ch == ';') lf.forward else !lf.forward;
                            kind = if (fwd) .inclusive else .exclusive;
                            break :blk c.findInLine(h, lf.char, fwd, lf.till, n) orelse h;
                        },
                        else => return null,
                    };
                    try t.append(self.gpa, target);
                }
            },
        }
        if (!for_op) self.moveCursorsTo(ed, kind);
        return kind;
    }

    fn gMotion(self: *Vim, ed: *Editor, ch: u21) !?motions.Kind {
        var c = Ctx.init(ed.buffer);
        const n = self.totalCount();
        const t = self.targets();
        t.clearRetainingCapacity();
        var kind: motions.Kind = .exclusive;
        switch (ch) {
            'g' => {
                kind = .linewise;
                const r: u32 = if (self.count == 0 and self.op_count == 0) 0 else @intCast(@min(ed.buffer.lineCount(), n) - 1);
                for (ed.selections.items) |_| try t.append(self.gpa, c.firstNonBlank(r));
            },
            'e', 'E' => {
                kind = .inclusive;
                for (ed.selections.items) |sel| {
                    var o = sel.head;
                    var i: usize = 0;
                    while (i < n) : (i += 1) o = c.wordEndBackward(o, ch == 'E');
                    try t.append(self.gpa, o);
                }
            },
            '_' => {
                kind = .inclusive;
                for (ed.selections.items) |sel| {
                    const r: u32 = @intCast(@min(ed.buffer.lineCount() - 1, c.row(sel.head) + n - 1));
                    var o = c.lastChar(r);
                    while (o > ed.buffer.lineStart(r) and (c.s.byte(o) == ' ' or c.s.byte(o) == '\t')) o -= 1;
                    try t.append(self.gpa, o);
                }
            },
            'j', 'k' => {
                if (self.operator == null) {
                    self.moveVertical(ed, n, ch == 'j');
                    return .linewise;
                }
                return null;
            },
            '0' => for (ed.selections.items) |sel| try t.append(self.gpa, c.lineStart(sel.head)),
            'm' => for (ed.selections.items) |sel| {
                const ls = c.lineStart(sel.head);
                try t.append(self.gpa, ed.offsetForX(c.row(sel.head), (ed.xForOffset(c.lineEnd(sel.head)) - ed.xForOffset(ls)) / 2));
            },
            else => return null,
        }
        if (self.operator == null) self.moveCursorsTo(ed, kind);
        return kind;
    }

    fn prefixedMotion(self: *Vim, ed: *Editor, prefix: Prefix, ch: u21) !?motions.Kind {
        var c = Ctx.init(ed.buffer);
        const n = self.totalCount();
        const t = self.targets();
        t.clearRetainingCapacity();
        var kind: motions.Kind = .exclusive;
        switch (prefix) {
            .f, .F, .t, .T => {
                const forward = prefix == .f or prefix == .t;
                const till = prefix == .t or prefix == .T;
                self.last_find = .{ .char = ch, .forward = forward, .till = till };
                kind = if (forward) .inclusive else .exclusive;
                for (ed.selections.items) |sel| {
                    try t.append(self.gpa, c.findInLine(sel.head, ch, forward, till, n) orelse sel.head);
                }
            },
            .quote, .backtick => {
                if (ch < 'a' or ch > 'z') return null;
                const mark = self.marks[ch - 'a'] orelse return null;
                const m = @min(mark, ed.buffer.len());
                kind = if (prefix == .quote) .linewise else .exclusive;
                for (ed.selections.items) |_| try t.append(self.gpa, if (prefix == .quote) c.firstNonBlank(c.row(m)) else m);
            },
            .bracket_open, .bracket_close => {
                // [z ]z: start / end of the open fold (moves or operates itself).
                if (ch == 'z') {
                    folds.motion(self, ed, false, prefix == .bracket_close);
                    return null;
                }
                // [( [{ ]) ]}: unmatched brackets.
                const open: u8, const close: u8 = switch (ch) {
                    '(', ')' => .{ '(', ')' },
                    '{', '}' => .{ '{', '}' },
                    else => return null,
                };
                for (ed.selections.items) |sel| {
                    const r = c.bracketObject(sel.head, open, close, true) orelse {
                        try t.append(self.gpa, sel.head);
                        continue;
                    };
                    try t.append(self.gpa, if (prefix == .bracket_open) r.start else r.end - 1);
                }
                kind = .inclusive;
            },
            else => return null,
        }
        if (self.operator == null) self.moveCursorsTo(ed, kind);
        return kind;
    }

    fn moveCursorsTo(self: *Vim, ed: *Editor, kind: motions.Kind) void {
        const t = self.targets();
        var c = Ctx.init(ed.buffer);
        if (self.isVisual()) {
            for (self.visual_cursors.items, 0..) |*cur, i| if (i < t.items.len) {
                cur.* = t.items[i];
            };
            self.syncVisual(ed);
            ed.ensureCaretVisible(false);
            return;
        }
        ed.pushSelectionHistory();
        for (ed.selections.items, 0..) |*sel, i| if (i < t.items.len) {
            sel.* = .caret(c.clampNormal(t.items[i]));
        };
        _ = kind;
        ed.normalize();
        ed.ensureCaretVisible(false);
    }

    fn moveVertical(self: *Vim, ed: *Editor, n: usize, down: bool) void {
        var c = Ctx.init(ed.buffer);
        const cursors = if (self.isVisual()) self.visual_cursors.items else null;
        const count = if (cursors) |cs| cs.len else ed.selections.items.len;
        var i: usize = 0;
        while (i < count) : (i += 1) {
            const head = if (cursors) |cs| cs[i] else ed.selections.items[i].head;
            const goal = (if (cursors == null) ed.selections.items[i].goal_x else null) orelse ed.xForOffset(head);
            // Move over display rows: folds are skipped, wrapped lines walked visually.
            const d = ed.displayRowOfOffset(head);
            const last_d = ed.displayRowCount() - 1;
            const nd: u32 = if (down) @intCast(@min(last_d, d + n)) else d -| @as(u32, @intCast(n));
            var target = ed.offsetForDisplayRow(nd, goal);
            if (self.mode == .normal or self.isVisual()) {
                // Never land on the newline of a non-empty line.
                target = c.clampNormal(target);
            }
            if (cursors) |cs| cs[i] = target else {
                ed.selections.items[i] = .caret(target);
                ed.selections.items[i].goal_x = goal;
            }
        }
        if (self.isVisual()) self.syncVisual(ed) else ed.normalize();
        ed.ensureCaretVisible(false);
    }

    // ---- Operators --------------------------------------------------------

    fn applyOperatorToMotion(self: *Vim, ed: *Editor, op: Operator, kind: motions.Kind) void {
        const t = self.targets();
        var c = Ctx.init(ed.buffer);
        const ranges = self.gpa.alloc(motions.Range, @min(t.items.len, ed.selections.items.len)) catch return;
        defer self.gpa.free(ranges);
        for (ranges, 0..) |*r, i| {
            const h = ed.selections.items[i].head;
            const target = t.items[i];
            const lo = @min(h, target);
            const hi = @max(h, target);
            r.* = switch (kind) {
                .exclusive => .{ .start = lo, .end = hi },
                .inclusive => .{ .start = lo, .end = c.s.nextBoundary(hi) },
                .linewise => self.lineRange(ed, c.row(lo), c.row(hi)),
            };
        }
        // A closed fold is operated on whole.
        folds.includeClosed(ed, ranges);
        self.operate(ed, op, ranges);
    }

    fn lineRange(_: *Vim, ed: *Editor, first: u32, last: u32) motions.Range {
        const end = if (last + 1 < ed.buffer.lineCount()) ed.buffer.lineStart(last + 1) else ed.buffer.len();
        return .{ .start = ed.buffer.lineStart(first), .end = end, .linewise = true };
    }

    fn operateLines(self: *Vim, ed: *Editor, op: Operator, n: usize) void {
        var c = Ctx.init(ed.buffer);
        const ranges = self.gpa.alloc(motions.Range, ed.selections.items.len) catch return;
        defer self.gpa.free(ranges);
        for (ed.selections.items, ranges) |sel, *r| {
            // A closed fold counts as one line.
            const span = folds.lineSpan(ed, c.row(sel.head), n);
            r.* = self.lineRange(ed, span[0], span[1]);
        }
        self.operate(ed, op, ranges);
    }

    /// Applies `op` to one range per cursor.
    pub fn operate(self: *Vim, ed: *Editor, op: Operator, ranges: []motions.Range) void {
        if (ranges.len == 0) return;
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var c = Ctx.init(ed.buffer);
        const linewise = ranges[0].linewise;

        // Register contents (yank / delete / change).
        if (op == .delete or op == .change or op == .yank) {
            var parts: std.ArrayList([]const u8) = .empty;
            for (ranges) |r| {
                var text = ed.buffer.textRange(alloc, r.start, r.end) catch return;
                if (r.linewise and (text.len == 0 or text[text.len - 1] != '\n')) text = std.mem.concat(alloc, u8, &.{ text, "\n" }) catch return;
                parts.append(alloc, text) catch return;
            }
            const joined = std.mem.join(alloc, if (linewise) "" else "\n", parts.items) catch return;
            self.storeRegister(ed, joined, linewise, op == .yank);
        }

        switch (op) {
            .yank => {
                ed.pushSelectionHistory();
                for (ed.selections.items, 0..) |*sel, i| if (i < ranges.len) {
                    sel.* = .caret(ranges[i].start);
                };
                ed.normalize();
                if (self.isVisual()) self.enterNormal(ed);
            },
            .delete, .change => {
                ed.buffer.beginGroup();
                // A change stays grouped until insert mode ends.
                defer if (op == .delete) ed.buffer.endGroup();
                const reps = alloc.alloc(Editor.Replacement, ranges.len) catch return;
                for (ranges, reps) |r, *rep| {
                    var start = r.start;
                    var end = r.end;
                    var text: []const u8 = "";
                    if (r.linewise) {
                        if (op == .change) {
                            // cc keeps the line (and its indentation), clearing the text.
                            const first_row = c.row(start);
                            const indent_end = c.firstNonBlank(first_row);
                            text = ed.buffer.textRange(alloc, start, indent_end) catch "";
                            if (end > start and c.s.byte(end - 1) == '\n') end -= 1;
                        } else if (end == ed.buffer.len() and end > start and start > 0 and c.s.byte(end - 1) != '\n') {
                            // Last lines with no newline of their own take the one before them.
                            start -= 1;
                        }
                    }
                    rep.* = .{ .start = start, .end = end, .text = text, .sel_anchor = text.len, .sel_head = text.len };
                }
                ed.applyReplacements(reps, .other);
                if (op == .change) {
                    self.enterInsert(ed);
                } else {
                    if (self.isVisual()) self.enterNormal(ed);
                    if (linewise) {
                        // Land on the first non-blank of the line that took their place.
                        var c2 = Ctx.init(ed.buffer);
                        for (ed.selections.items) |*sel| sel.* = .caret(c2.firstNonBlank(c2.row(sel.head)));
                    }
                }
            },
            .indent, .unindent, .reindent, .comment => {
                if (op == .reindent) return;
                ed.pushSelectionHistory();
                const sels = alloc.alloc(Selection, ranges.len) catch return;
                for (ranges, sels) |r, *s| s.* = .range(r.start, r.end);
                ed.setSelections(sels);
                if (op == .comment) {
                    // gc: line comments on every line the ranges touch.
                    ed.toggleComment(false);
                } else {
                    // In visual mode a count repeats the shift ("3>").
                    const times: usize = if (self.isVisual()) @max(self.count, 1) else 1;
                    var i: usize = 0;
                    while (i < times) : (i += 1) {
                        if (op == .indent) ed.indentLines() else ed.unindentLines();
                    }
                }
                var c2 = Ctx.init(ed.buffer);
                for (ed.selections.items) |*s| s.* = .caret(c2.firstNonBlank(c2.row(s.start())));
                ed.normalize();
                if (self.isVisual()) self.enterNormal(ed);
            },
            .toggle_case, .lower, .upper => {
                const reps = alloc.alloc(Editor.Replacement, ranges.len) catch return;
                for (ranges, reps) |r, *rep| {
                    const text = ed.buffer.textRange(alloc, r.start, r.end) catch return;
                    for (text) |*b| switch (op) {
                        .lower => b.* = std.ascii.toLower(b.*),
                        .upper => b.* = std.ascii.toUpper(b.*),
                        else => b.* = if (std.ascii.isUpper(b.*)) std.ascii.toLower(b.*) else std.ascii.toUpper(b.*),
                    };
                    rep.* = .{ .start = r.start, .end = r.end, .text = text, .sel_anchor = 0, .sel_head = 0 };
                }
                ed.applyReplacements(reps, .other);
                if (self.isVisual()) self.enterNormal(ed);
            },
            .fold => {
                // zf: a closed manual fold over the lines.
                if (self.isVisual()) self.enterNormal(ed);
                folds.createRanges(ed, ranges);
            },
        }
    }

    fn storeRegister(self: *Vim, ed: *Editor, text: []const u8, linewise: bool, is_yank: bool) void {
        const reg: u8 = self.register orelse '"';
        if (reg == '_') return;
        const set = struct {
            fn f(v: *Vim, r: u8, t: []const u8, lw: bool, append: bool) void {
                const idx = std.ascii.toLower(r);
                if (append) if (v.registers[idx]) |old| {
                    const joined = std.mem.concat(v.gpa, u8, &.{ old.text, t }) catch return;
                    v.gpa.free(old.text);
                    v.registers[idx] = .{ .text = joined, .linewise = lw or old.linewise };
                    return;
                };
                if (v.registers[idx]) |old| v.gpa.free(old.text);
                v.registers[idx] = .{ .text = v.gpa.dupe(u8, t) catch return, .linewise = lw };
            }
        }.f;
        set(self, reg, text, linewise, std.ascii.isUpper(reg));
        if (reg != '"') set(self, '"', text, linewise, false);
        if (is_yank) set(self, '0', text, linewise, false);
        // The unnamed register is the system clipboard (like Zed's vim mode).
        if (reg == '"' or reg == '+' or reg == '*') {
            ed.env.clipboard.set(ed.env.clipboard.ctx, text);
            if (ed.env.clipboard_whole_line) |old| ed.env.gpa.free(old);
            ed.env.clipboard_whole_line = null;
        }
    }

    fn readRegister(self: *Vim, ed: *Editor, alloc: Allocator) ?Register {
        const reg: u8 = self.register orelse '"';
        if (reg == '"' or reg == '+' or reg == '*') {
            const text = ed.env.clipboard.get(ed.env.clipboard.ctx, alloc) orelse return null;
            // Our own yank: keep its linewise flag.
            if (self.registers['"']) |own| if (std.mem.eql(u8, own.text, text)) return .{ .text = text, .linewise = own.linewise };
            return .{ .text = text, .linewise = text.len > 0 and text[text.len - 1] == '\n' };
        }
        const r = self.registers[std.ascii.toLower(reg)] orelse return null;
        return .{ .text = alloc.dupe(u8, r.text) catch return null, .linewise = r.linewise };
    }

    fn put(self: *Vim, ed: *Editor, after: bool, count: usize) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const reg = self.readRegister(ed, alloc) orelse return;
        var text: std.ArrayList(u8) = .empty;
        var i: usize = 0;
        while (i < count) : (i += 1) text.appendSlice(alloc, reg.text) catch return;
        var c = Ctx.init(ed.buffer);
        const reps = alloc.alloc(Editor.Replacement, ed.selections.items.len) catch return;
        for (ed.selections.items, reps) |sel, *r| {
            if (reg.linewise) {
                // Lines go after / before a closed fold as a whole.
                const row = if (after) folds.closedEnd(ed, c.row(sel.head)) else folds.closedStart(ed, c.row(sel.head));
                if (after) {
                    if (row + 1 < ed.buffer.lineCount()) {
                        const at = ed.buffer.lineStart(row + 1);
                        r.* = .{ .start = at, .end = at, .text = text.items, .sel_anchor = 0, .sel_head = 0 };
                    } else {
                        // Last line without trailing newline: prepend one.
                        const at = ed.buffer.len();
                        const t = std.mem.concat(alloc, u8, &.{ "\n", std.mem.trimEnd(u8, text.items, "\n") }) catch return;
                        r.* = .{ .start = at, .end = at, .text = t, .sel_anchor = 1, .sel_head = 1 };
                    }
                } else {
                    const at = ed.buffer.lineStart(row);
                    r.* = .{ .start = at, .end = at, .text = text.items, .sel_anchor = 0, .sel_head = 0 };
                }
            } else {
                const at = if (after and sel.head < c.lineEnd(sel.head)) c.s.nextBoundary(sel.head) else sel.head;
                const end_off = if (text.items.len > 0) text.items.len - 1 else 0;
                r.* = .{ .start = at, .end = at, .text = text.items, .sel_anchor = end_off, .sel_head = end_off };
            }
        }
        // Lines put after a fold that ends the buffer stay out of it.
        ed.display.folds.keep_ends = reg.linewise;
        ed.applyReplacements(reps, .other);
        ed.display.folds.keep_ends = false;
        if (reg.linewise) {
            var c2 = Ctx.init(ed.buffer);
            for (ed.selections.items) |*sel| sel.* = .caret(c2.firstNonBlank(c2.row(sel.head)));
        }
    }

    fn join(self: *Vim, ed: *Editor, count: usize, spaces: bool) void {
        _ = self;
        var c = Ctx.init(ed.buffer);
        var edits: std.ArrayList(editor_mod.Edit) = .empty;
        defer edits.deinit(ed.gpa);
        for (ed.selections.items) |sel| {
            const first = c.row(sel.head);
            var r = first;
            while (r < first + count - 1 and r + 1 < ed.buffer.lineCount()) : (r += 1) {
                const end = ed.buffer.lineEnd(r);
                const next_first = if (spaces) c.firstNonBlank(r + 1) else ed.buffer.lineStart(r + 1);
                const sep: []const u8 = if (!spaces or next_first == ed.buffer.lineEnd(r + 1) or end == ed.buffer.lineStart(r)) "" else " ";
                edits.append(ed.gpa, .{ .start = end, .end = next_first, .text = sep }) catch return;
            }
        }
        ed.commitTransform(edits.items, .other);
    }

    fn replaceChars(self: *Vim, ed: *Editor, ch: []const u8, count: usize) void {
        _ = self;
        var c = Ctx.init(ed.buffer);
        var arena = std.heap.ArenaAllocator.init(ed.gpa);
        defer arena.deinit();
        const reps = arena.allocator().alloc(Editor.Replacement, ed.selections.items.len) catch return;
        for (ed.selections.items, reps) |sel, *r| {
            var end = sel.head;
            var i: usize = 0;
            while (i < count and end < c.lineEnd(sel.head)) : (i += 1) end = c.s.nextBoundary(end);
            if (i < count) {
                r.* = .{ .start = sel.head, .end = sel.head, .text = "", .sel_anchor = 0, .sel_head = 0 };
                continue;
            }
            var text: std.ArrayList(u8) = .empty;
            i = 0;
            while (i < count) : (i += 1) text.appendSlice(arena.allocator(), ch) catch return;
            r.* = .{ .start = sel.head, .end = end, .text = text.items, .sel_anchor = text.items.len - ch.len, .sel_head = text.items.len - ch.len };
        }
        ed.applyReplacements(reps, .other);
    }

    fn increment(self: *Vim, ed: *Editor, delta: i64) void {
        _ = self;
        var c = Ctx.init(ed.buffer);
        var arena = std.heap.ArenaAllocator.init(ed.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var reps: std.ArrayList(Editor.Replacement) = .empty;
        for (ed.selections.items) |sel| {
            // The number at or after the cursor on this line.
            const le = c.lineEnd(sel.head);
            var o = sel.head;
            while (o > c.lineStart(sel.head) and std.ascii.isDigit(c.s.byte(o - 1)) and std.ascii.isDigit(c.s.byte(o))) o -= 1;
            while (o < le and !std.ascii.isDigit(c.s.byte(o))) o += 1;
            if (o >= le) continue;
            var start = o;
            if (start > 0 and c.s.byte(start - 1) == '-') start -= 1;
            var end = o;
            while (end < le and std.ascii.isDigit(c.s.byte(end))) end += 1;
            const text = ed.buffer.textRange(alloc, start, end) catch continue;
            const value = std.fmt.parseInt(i64, text, 10) catch continue;
            const new = std.fmt.allocPrint(alloc, "{d}", .{value + delta}) catch continue;
            reps.append(alloc, .{ .start = start, .end = end, .text = new, .sel_anchor = new.len - 1, .sel_head = new.len - 1 }) catch continue;
        }
        ed.applyReplacements(reps.items, .other);
    }

    fn textObject(self: *Vim, ed: *Editor, ch: u21, around: bool) !?[]motions.Range {
        var c = Ctx.init(ed.buffer);
        const heads = if (self.isVisual()) self.visual_cursors.items else blk: {
            const hs = try self.gpa.alloc(usize, ed.selections.items.len);
            for (ed.selections.items, hs) |s, *h| h.* = s.head;
            break :blk hs;
        };
        defer if (!self.isVisual()) self.gpa.free(heads);
        var out = try self.gpa.alloc(motions.Range, heads.len);
        var n: usize = 0;
        for (heads) |h| {
            const r: ?motions.Range = switch (ch) {
                'w' => c.wordObject(h, false, around, @max(self.count, 1)),
                'W' => c.wordObject(h, true, around, @max(self.count, 1)),
                '"', '\'', '`' => c.quoteObject(h, ch, around),
                '(', ')', 'b' => c.bracketObject(h, '(', ')', around),
                '{', '}', 'B' => c.bracketObject(h, '{', '}', around),
                '[', ']' => c.bracketObject(h, '[', ']', around),
                '<', '>' => c.bracketObject(h, '<', '>', around),
                'p' => c.paragraphObject(h, around),
                else => null,
            };
            if (r) |range| {
                out[n] = range;
                n += 1;
            }
        }
        if (n == 0) {
            self.gpa.free(out);
            return null;
        }
        if (self.isVisual()) {
            // Expand the visual selection to the object.
            for (out[0..n], 0..) |r, i| if (i < self.visual_anchors.items.len) {
                self.visual_anchors.items[i] = r.start;
                self.visual_cursors.items[i] = if (r.end > r.start) c.s.prevBoundary(r.end) else r.start;
            };
            if (out[0].linewise and self.mode == .visual) self.mode = .visual_line;
            self.syncVisual(ed);
            self.gpa.free(out);
            return null;
        }
        return try self.gpa.realloc(out, n);
    }

    // ---- Visual mode ------------------------------------------------------

    pub fn isVisual(self: *const Vim) bool {
        return self.mode == .visual or self.mode == .visual_line or self.mode == .visual_block;
    }

    fn enterVisual(self: *Vim, ed: *Editor, mode: Mode) void {
        if (!self.isVisual()) {
            self.visual_anchors.clearRetainingCapacity();
            self.visual_cursors.clearRetainingCapacity();
            for (ed.selections.items) |sel| {
                self.visual_anchors.append(self.gpa, sel.head) catch {};
                self.visual_cursors.append(self.gpa, sel.head) catch {};
            }
        }
        self.mode = mode;
        ed.block_caret = true;
        self.syncVisual(ed);
    }

    /// Mirrors the vim visual state into editor selections for display.
    pub fn syncVisual(self: *Vim, ed: *Editor) void {
        var c = Ctx.init(ed.buffer);
        var sels: std.ArrayList(Selection) = .empty;
        defer sels.deinit(self.gpa);
        ed.caret_override.clearRetainingCapacity();
        for (self.visual_anchors.items, self.visual_cursors.items) |a, cur| {
            ed.caret_override.append(ed.gpa, cur) catch {};
            switch (self.mode) {
                .visual => {
                    const hi = @max(a, cur);
                    // Closed folds are selected whole.
                    var r = [_]motions.Range{.{ .start = @min(a, cur), .end = if (hi < ed.buffer.len()) c.s.nextBoundary(hi) else hi }};
                    folds.includeClosed(ed, &r);
                    sels.append(self.gpa, if (cur >= a) .range(r[0].start, r[0].end) else .range(r[0].end, r[0].start)) catch {};
                },
                .visual_line => {
                    var r = [_]motions.Range{self.lineRange(ed, c.row(@min(a, cur)), c.row(@max(a, cur)))};
                    folds.includeClosed(ed, &r);
                    sels.append(self.gpa, if (cur >= a) .range(r[0].start, r[0].end) else .range(r[0].end, r[0].start)) catch {};
                },
                .visual_block => {
                    const x0 = ed.xForOffset(a);
                    const x1 = ed.xForOffset(cur);
                    const left = @min(x0, x1);
                    const right = @max(x0, x1) + ed.charWidth() * 0.5;
                    var r = @min(c.row(a), c.row(cur));
                    while (r <= @max(c.row(a), c.row(cur))) : (r += 1) {
                        const s = ed.offsetForX(r, left);
                        var e = ed.offsetForX(r, right);
                        if (e == s and s < ed.buffer.lineEnd(r)) e = c.s.nextBoundary(s);
                        sels.append(self.gpa, .range(s, e)) catch {};
                    }
                },
                else => {},
            }
        }
        ed.selections.clearRetainingCapacity();
        ed.selections.appendSlice(ed.gpa, sels.items) catch {};
        if (ed.selections.items.len == 0) ed.selections.append(ed.gpa, .caret(0)) catch {};
        ed.primary = ed.selections.items.len - 1;
        ed.touchCaret();
    }

    pub fn exitVisual(self: *Vim, ed: *Editor) void {
        if (self.visual_anchors.items.len > 0) {
            self.last_visual = .{ .mode = self.mode, .anchor = self.visual_anchors.items[0], .cursor = self.visual_cursors.items[0] };
        }
        const cursors = self.gpa.dupe(usize, self.visual_cursors.items) catch return;
        defer self.gpa.free(cursors);
        self.mode = .normal;
        ed.selections.clearRetainingCapacity();
        for (cursors) |cur| ed.selections.append(ed.gpa, .caret(cur)) catch {};
        if (ed.selections.items.len == 0) ed.selections.append(ed.gpa, .caret(0)) catch {};
        self.enterNormal(ed);
    }

    fn visualRanges(self: *Vim, ed: *Editor) ![]motions.Range {
        const out = try self.gpa.alloc(motions.Range, ed.selections.items.len);
        for (ed.selections.items, out) |sel, *r| r.* = .{ .start = sel.start(), .end = sel.end(), .linewise = self.mode == .visual_line };
        return out;
    }

    fn visualKey(self: *Vim, ws: *Workspace, ed: *Editor, k: KeyEvent) bool {
        if (self.prefix == null and passThrough(k)) return false;
        const vk = toVKey(k);
        self.pushPending(k);
        defer if (self.prefix == null and self.count == 0) {
            self.pending_len = 0;
        };
        if (self.prefix) |prefix| {
            self.prefix = null;
            const ch: u21 = switch (vk) {
                .char => |c| c,
                else => return true,
            };
            switch (prefix) {
                .inner, .around => {
                    const r = self.textObject(ed, ch, prefix == .around) catch null;
                    if (r) |ranges| self.gpa.free(ranges);
                },
                .r => {
                    var buf: [4]u8 = undefined;
                    const n = std.unicode.utf8Encode(ch, &buf) catch return true;
                    self.visualReplace(ed, buf[0..n]);
                },
                .register => {
                    if (ch < 128) self.register = @intCast(ch);
                    return true;
                },
                .g => switch (ch) {
                    'g', 'e', 'E', '_' => _ = self.gMotion(ed, ch) catch null,
                    'u', 'U', '~', 'c' => {
                        const ranges = self.visualRanges(ed) catch return true;
                        defer self.gpa.free(ranges);
                        self.operate(ed, switch (ch) {
                            'u' => .lower,
                            'U' => .upper,
                            'c' => .comment,
                            else => .toggle_case,
                        }, ranges);
                    },
                    else => {},
                },
                .z => folds.visual(self, ed, ch),
                else => _ = self.prefixedMotion(ed, prefix, ch) catch null,
            }
            self.count = 0;
            return true;
        }
        if (vk == .char and vk.char >= '1' and vk.char <= '9' or (vk == .char and vk.char == '0' and self.count > 0)) {
            self.count = self.count * 10 + (vk.char - '0');
            return true;
        }
        switch (vk) {
            .named => |n| {
                if (n == .escape) {
                    self.exitVisual(ed);
                    self.count = 0;
                    return true;
                }
                _ = self.motionTargets(ws, ed, vk) catch null;
                return true;
            },
            .ctrl => |ch| {
                if (ch == 'v') self.switchVisual(ed, .visual_block);
                if (ch == 'c') self.exitVisual(ed);
                if (ch == 'w') {
                    // Window commands leave visual mode.
                    self.exitVisual(ed);
                    _ = window.begin(self);
                }
                return true;
            },
            .char => |ch| {
                const op: ?Operator = switch (ch) {
                    'd', 'x', 'X', 'D' => .delete,
                    'c', 's', 'S', 'C' => .change,
                    'y', 'Y' => .yank,
                    '>' => .indent,
                    '<' => .unindent,
                    '~' => .toggle_case,
                    'u' => .lower,
                    'U' => .upper,
                    else => null,
                };
                if (op) |o| {
                    if (ch == 'X' or ch == 'D' or ch == 'S' or ch == 'C' or ch == 'Y') self.mode = .visual_line;
                    if (self.mode == .visual_line) self.syncVisual(ed);
                    const ranges = self.visualRanges(ed) catch return true;
                    defer self.gpa.free(ranges);
                    self.last_visual = .{ .mode = self.mode, .anchor = self.visual_anchors.items[0], .cursor = self.visual_cursors.items[0] };
                    if (o == .change) ed.buffer.beginGroup();
                    self.operate(ed, o, ranges);
                    if (self.mode != .insert) self.enterNormal(ed) else self.recording_change = false;
                    self.count = 0;
                    return true;
                }
                switch (ch) {
                    'v' => self.switchVisual(ed, .visual),
                    'V' => self.switchVisual(ed, .visual_line),
                    'o', 'O' => {
                        for (self.visual_anchors.items, self.visual_cursors.items) |*a, *cur| std.mem.swap(usize, a, cur);
                        self.syncVisual(ed);
                    },
                    'i' => self.prefix = .inner,
                    'a' => self.prefix = .around,
                    'r' => self.prefix = .r,
                    'g' => self.prefix = .g,
                    'z' => self.prefix = .z,
                    '"' => self.prefix = .register,
                    'f' => self.prefix = .f,
                    'F' => self.prefix = .F,
                    't' => self.prefix = .t,
                    'T' => self.prefix = .T,
                    '\'' => self.prefix = .quote,
                    '`' => self.prefix = .backtick,
                    'J' => {
                        const n = (self.visualRanges(ed) catch return true);
                        defer self.gpa.free(n);
                        var c = Ctx.init(ed.buffer);
                        const rows = c.row(n[0].end) - c.row(n[0].start) + 1;
                        self.exitVisual(ed);
                        ed.setSingleSelection(.caret(n[0].start));
                        self.join(ed, @max(rows, 2), true);
                    },
                    'p', 'P' => {
                        const ranges = self.visualRanges(ed) catch return true;
                        defer self.gpa.free(ranges);
                        var arena = std.heap.ArenaAllocator.init(self.gpa);
                        defer arena.deinit();
                        const reg = self.readRegister(ed, arena.allocator()) orelse return true;
                        const reps = arena.allocator().alloc(Editor.Replacement, ranges.len) catch return true;
                        for (ranges, reps) |r, *rep| rep.* = .{ .start = r.start, .end = r.end, .text = reg.text, .sel_anchor = 0, .sel_head = 0 };
                        ed.applyReplacements(reps, .other);
                        self.enterNormal(ed);
                    },
                    'I', 'A' => if (self.mode == .visual_block) {
                        // Block insert: a cursor on every line of the block.
                        ed.buffer.beginGroup();
                        var carets: std.ArrayList(Selection) = .empty;
                        defer carets.deinit(self.gpa);
                        for (ed.selections.items) |sel| carets.append(self.gpa, .caret(if (ch == 'I') sel.start() else sel.end())) catch {};
                        self.mode = .normal;
                        ed.setSelections(carets.items);
                        self.enterInsert(ed);
                        self.recording_change = false;
                    } else {
                        ed.buffer.beginGroup();
                        const target = if (ch == 'I') ed.selections.items[0].start() else ed.selections.items[ed.selections.items.len - 1].end();
                        self.mode = .normal;
                        ed.setSingleSelection(.caret(target));
                        self.enterInsert(ed);
                    },
                    ':' => {
                        self.exitVisual(ed);
                        self.openCmdline(ws, ed, ':');
                        if (self.cmdline) |cl| cl.insertText("'<,'>");
                    },
                    '*', '#' => {
                        self.exitVisual(ed);
                        self.searchWordUnderCursor(ed, ch == '*');
                    },
                    else => {
                        _ = self.motionTargets(ws, ed, vk) catch null;
                        self.count = 0;
                    },
                }
            },
        }
        return true;
    }

    fn switchVisual(self: *Vim, ed: *Editor, mode: Mode) void {
        if (self.mode == mode) {
            self.exitVisual(ed);
        } else {
            self.mode = mode;
            self.syncVisual(ed);
        }
    }

    fn visualReplace(self: *Vim, ed: *Editor, ch: []const u8) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const reps = alloc.alloc(Editor.Replacement, ed.selections.items.len) catch return;
        for (ed.selections.items, reps) |sel, *r| {
            const text = ed.buffer.textRange(alloc, sel.start(), sel.end()) catch return;
            var out: std.ArrayList(u8) = .empty;
            var it = std.unicode.Utf8View.initUnchecked(text).iterator();
            while (it.nextCodepointSlice()) |cp| out.appendSlice(alloc, if (cp[0] == '\n') "\n" else ch) catch return;
            r.* = .{ .start = sel.start(), .end = sel.end(), .text = out.items, .sel_anchor = 0, .sel_head = 0 };
        }
        ed.applyReplacements(reps, .other);
        self.enterNormal(ed);
    }

    // ---- Repeat and macros -----------------------------------------------

    fn repeatChange(self: *Vim, ws: *Workspace, ed: *Editor) void {
        if (self.last_change.items.len == 0 or self.replaying > 4) return;
        const keys = self.gpa.dupe(KeyEvent, self.last_change.items) catch return;
        defer self.gpa.free(keys);
        const times = @max(self.count, 1);
        self.resetPending();
        self.replaying += 1;
        defer self.replaying -= 1;
        var i: usize = 0;
        while (i < times) : (i += 1) {
            for (keys) |key| {
                const target = ws.activeEditor() orelse ed;
                if (!self.handleKey(ws, target, key)) {
                    // Insert-mode text goes through the normal typing path.
                    if (key.text.len > 0) target.insertText(key.text) else ws.onKey(key);
                    // The keys a leader sequence took aren't in the change,
                    // so the one that started it (alt+m) starts nothing.
                    ws.leader.active = false;
                }
            }
        }
    }

    fn playMacro(self: *Vim, ws: *Workspace, reg: u8, times: usize) void {
        if (self.replaying > 8) return;
        const keys = self.gpa.dupe(KeyEvent, self.macros[reg].items) catch return;
        defer self.gpa.free(keys);
        self.replaying += 1;
        defer self.replaying -= 1;
        var i: usize = 0;
        while (i < times) : (i += 1) {
            for (keys) |key| ws.onKey(key);
        }
    }

    // ---- Search -----------------------------------------------------------

    pub fn setSearch(self: *Vim, pattern: []const u8, forward: bool) void {
        if (self.last_search) |s| self.gpa.free(s);
        self.last_search = self.gpa.dupe(u8, pattern) catch null;
        self.last_search_forward = forward;
    }

    /// Vim regex flavour → ours: \< \> become \b; a trailing \c means ignore case.
    fn compileSearch(self: *Vim, pattern: []const u8) ?regex_mod.Regex {
        var buf: std.ArrayList(u8) = .empty;
        defer buf.deinit(self.gpa);
        var ci = false;
        var i: usize = 0;
        while (i < pattern.len) : (i += 1) {
            if (pattern[i] == '\\' and i + 1 < pattern.len) {
                switch (pattern[i + 1]) {
                    '<', '>' => buf.appendSlice(self.gpa, "\\b") catch return null,
                    'c' => ci = true,
                    else => buf.appendSlice(self.gpa, pattern[i .. i + 2]) catch return null,
                }
                i += 1;
            } else buf.append(self.gpa, pattern[i]) catch return null;
        }
        // smartcase
        var has_upper = false;
        for (pattern) |ch| if (std.ascii.isUpper(ch)) {
            has_upper = true;
        };
        return regex_mod.Regex.compile(self.gpa, buf.items, .{ .case_insensitive = ci or !has_upper }) catch null;
    }

    /// All matches of the last search (also used for hlsearch).
    fn searchMatches(self: *Vim, ed: *Editor, out: *std.ArrayList([2]usize)) void {
        out.clearRetainingCapacity();
        const pattern = self.last_search orelse return;
        var re = self.compileSearch(pattern) orelse return;
        defer re.deinit();
        var line: std.ArrayList(u8) = .empty;
        defer line.deinit(ed.gpa);
        var row: u32 = 0;
        while (row < ed.buffer.lineCount() and out.items.len < 10_000) : (row += 1) {
            const text = ed.buffer.lineText(row, &line) catch return;
            const base = ed.buffer.lineStart(row);
            var pos: usize = 0;
            while (pos <= text.len) {
                const m = re.search(text, pos) orelse break;
                out.append(ed.gpa, .{ base + m.start, base + m.end }) catch return;
                pos = if (m.end > m.start) m.end else m.end + 1;
            }
        }
    }

    fn searchNext(self: *Vim, ed: *Editor, same_direction: bool, count: usize) void {
        var matches: std.ArrayList([2]usize) = .empty;
        defer matches.deinit(ed.gpa);
        self.searchMatches(ed, &matches);
        ed.highlights.clearRetainingCapacity();
        ed.highlights.appendSlice(ed.gpa, matches.items) catch {};
        if (matches.items.len == 0) {
            self.setMessage("Pattern not found", .{});
            return;
        }
        const forward = if (same_direction) self.last_search_forward else !self.last_search_forward;
        var pos = if (self.isVisual()) self.visual_cursors.items[0] else ed.primarySelection().head;
        var n: usize = 0;
        while (n < count) : (n += 1) {
            var found: ?usize = null;
            if (forward) {
                for (matches.items) |m| if (m[0] > pos) {
                    found = m[0];
                    break;
                };
                if (found == null) found = matches.items[0][0];
            } else {
                var i = matches.items.len;
                while (i > 0) {
                    i -= 1;
                    if (matches.items[i][0] < pos) {
                        found = matches.items[i][0];
                        break;
                    }
                }
                if (found == null) found = matches.items[matches.items.len - 1][0];
            }
            pos = found.?;
        }
        const t = self.targets();
        t.clearRetainingCapacity();
        for (ed.selections.items) |_| t.append(self.gpa, pos) catch {};
        if (self.operator) |op| {
            self.applyOperatorToMotion(ed, op, .exclusive);
        } else if (self.isVisual()) {
            self.visual_cursors.items[0] = pos;
            self.syncVisual(ed);
        } else {
            ed.pushSelectionHistory();
            ed.setSingleSelection(.caret(pos));
        }
        ed.ensureCaretVisible(false);
    }

    fn searchWordUnderCursor(self: *Vim, ed: *Editor, forward: bool) void {
        var c = Ctx.init(ed.buffer);
        const h = ed.primarySelection().head;
        // The keyword under or after the cursor.
        var o = h;
        const le = c.lineEnd(h);
        while (o < le and !@import("../../model/text/movement.zig").isWordChar(c.s.charAt(o))) o = c.s.nextBoundary(o);
        const w = @import("../../model/text/movement.zig").wordAt(&c.s, o) orelse return;
        const word = ed.buffer.textRange(self.gpa, w[0], w[1]) catch return;
        defer self.gpa.free(word);
        const escaped = regex_mod.escape(self.gpa, word) catch return;
        defer self.gpa.free(escaped);
        const pattern = std.fmt.allocPrint(self.gpa, "\\<{s}\\>", .{escaped}) catch return;
        defer self.gpa.free(pattern);
        self.setSearch(pattern, forward);
        ed.setSingleSelection(.caret(w[0]));
        self.searchNext(ed, true, @max(self.count, 1));
    }

    /// gf: edits the file whose name is under the cursor, next to this file
    /// if it is there, else as `:e` takes it (from the project root, ~/);
    /// an absolute path is on the machine this file is on.
    fn gotoFile(self: *Vim, ws: *Workspace, ed: *Editor) void {
        var c = Ctx.init(ed.buffer);
        const head = ed.primarySelection().head;
        var start = head;
        var end = head;
        while (start > c.lineStart(head) and isFileNameChar(c.s.byte(start - 1))) start -= 1;
        while (end < c.lineEnd(head) and isFileNameChar(c.s.byte(end))) end += 1;
        if (start == end) return self.setMessage("E446: No file name under cursor", .{});
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const name = ed.buffer.textRange(alloc, start, end) catch return;
        const here = ed.buffer.path orelse "";
        const path = if (std.fs.path.isAbsolute(name)) blk: {
            const host = rpath.authority(here) orelse break :blk name;
            break :blk std.mem.concat(alloc, u8, &.{ host, name }) catch return;
        } else blk: {
            const dir = std.fs.path.dirname(here) orelse break :blk name;
            const in_dir = std.fs.path.join(alloc, &.{ dir, name }) catch return;
            break :blk if (vfs.stat(ws.io, in_dir)) |_| in_dir else |_| name;
        };
        const full = ws.resolvePath(path) catch return;
        defer ws.gpa.free(full);
        const st = vfs.stat(ws.io, full) catch return self.setMessage("E447: Can't find file \"{s}\" in path", .{name});
        if (st.kind == .directory) return self.setMessage("E447: Can't find file \"{s}\" in path", .{name});
        window.edit(self, ws, ed, full);
    }

    fn isFileNameChar(ch: u8) bool {
        return std.ascii.isAlphanumeric(ch) or std.mem.indexOfScalar(u8, "/._-+~@", ch) != null;
    }

    // ---- Command line -----------------------------------------------------

    fn openCmdline(self: *Vim, ws: *Workspace, ed: *Editor, kind: u8) void {
        _ = ws;
        if (self.cmdline_buffer == null) {
            const b = self.gpa.create(buffer_mod.Buffer) catch return;
            b.* = buffer_mod.Buffer.initEmpty(self.gpa, 0) catch return self.gpa.destroy(b);
            // Kept for the session: every command line would stay in its undo.
            b.undo_limit = 100;
            self.cmdline_buffer = b;
            self.cmdline = Editor.create(self.gpa, ed.env, b) catch return;
            self.cmdline.?.mode = .single_line;
        } else {
            self.cmdline = Editor.create(self.gpa, ed.env, self.cmdline_buffer.?) catch return;
            self.cmdline.?.mode = .single_line;
        }
        self.cmdline.?.setText("", false);
        self.cmdline_kind = kind;
        self.resetPending();
    }

    fn closeCmdline(self: *Vim) void {
        if (self.cmdline) |cl| cl.destroy();
        self.cmdline = null;
    }

    fn cmdlineKey(self: *Vim, ws: *Workspace, ed: *Editor, k: KeyEvent) bool {
        const cl = self.cmdline.?;
        if (k.key == .named) switch (k.key.named) {
            .escape => {
                self.closeCmdline();
                return true;
            },
            .enter => {
                const text = cl.contents(self.gpa) catch "";
                defer self.gpa.free(text);
                const kind = self.cmdline_kind;
                self.closeCmdline();
                if (kind == ':') {
                    if (text.len > 0) {
                        if (self.last_ex) |old| self.gpa.free(old);
                        self.last_ex = self.gpa.dupe(u8, text) catch null;
                    }
                    ex.execute(self, ws, ed, text);
                    // A cursor sent into a closed fold (:42) goes to its first line.
                    if (ws.activeEditor()) |cur| folds.adjustCursors(cur);
                } else {
                    if (text.len > 0) self.setSearch(text, kind == '/') else self.last_search_forward = kind == '/';
                    self.searchNext(ed, true, 1);
                    // Searching opens the folds a match is in ('foldopen').
                    if (self.isVisual()) {
                        for (self.visual_cursors.items) |cur| _ = ed.display.folds.unfoldContaining(cur);
                        self.syncVisual(ed);
                    } else ed.revealCarets();
                }
                return true;
            },
            .backspace => if (cl.buffer.len() == 0) {
                self.closeCmdline();
                return true;
            },
            else => {},
        };
        if (k.mods.ctrl and k.key == .char and k.key.char == 'c') {
            self.closeCmdline();
            return true;
        }
        const chord: keymap_mod.Chord = .{ .key = k.key, .mods = k.mods };
        switch (ws.keymap.match(chord, true)) {
            .action => |a| _ = cl.perform(a),
            else => if (k.text.len > 0) cl.insertText(k.text),
        }
        return true;
    }
};
