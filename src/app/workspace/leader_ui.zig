//! The leader keys' workspace side, as in Doom Emacs: a sequence of keys
//! after the leader runs a command from settings/leader.zig's tree, and the
//! which-key popup lists the keys that can follow.
//!
//! Space starts a sequence in vim's normal and visual modes (a pending count,
//! register or operator keeps it vim's: `3 SPC`, `d SPC`), the `leader`
//! command (alt+m) anywhere, terminals and vim's insert mode included.
//! While a sequence is pending every key comes here first (Workspace.onKey,
//! and ahead of terminals in handleEvent), so macros record and replay it.
//! Escape or ctrl+g aborts; backspace takes back a key.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const leader = @import("../settings/leader.zig");
const keymap_mod = @import("../settings/keymap.zig");
const commands = @import("commands.zig");
const window_cmd = @import("window_cmd.zig");
const lsp_ui = @import("lsp_ui.zig");
const org = @import("../org/org.zig");
const platform = @import("../../framework/platform/platform.zig");
const ui = @import("../../framework/ui/ui.zig");

const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Chord = keymap_mod.Chord;
const Child = leader.Tree.Child;
const Rect = ui.Rect;

/// How long "SPC x y is not bound" shows in the popup's place while the
/// status bar (which says it too) is hidden.
const notice_ms = 2000;

const escape: Chord = .{ .key = .{ .named = .escape }, .mods = .{} };
const ctrl_g: Chord = .{ .key = .{ .char = 'g' }, .mods = .{ .ctrl = true } };
const backspace: Chord = .{ .key = .{ .named = .backspace }, .mods = .{} };

pub const State = struct {
    active: bool = false,
    /// The which-key popup is up: after which_key_delay_ms, or at once.
    shown: bool = false,
    /// The keys after the leader so far.
    seq: leader.Seq = .{},
    /// When the sequence started, or was found not bound.
    since: i64 = 0,
    /// The view active when it started (vim's visual region is there).
    editor: ?*Editor = null,
    /// `seq` isn't bound, said in the popup's place.
    notice: bool = false,
    /// The keys that can follow `seq`, worked out on each key press (never
    /// per frame).
    children: [64]Child = undefined,
    n_children: u8 = 0,
};

/// Space without modifiers: vim's leader key.
pub fn isSpace(k: platform.KeyEvent) bool {
    return k.key == .char and k.key.char == ' ' and k.mods.none();
}

/// Starts a sequence; `at_once` shows the popup without the delay.
pub fn begin(ws: *Workspace, at_once: bool) void {
    ws.keymap.pending = null;
    ws.org_keys.prefix_since = null;
    ws.closeMenus();
    if (ws.lsp_state.hover != null) lsp_ui.closeHover(ws);
    ws.leader = .{
        .active = true,
        .shown = at_once or ws.env.settings.which_key_delay_ms == 0,
        .since = platform.nowMs(),
        .editor = ws.activeEditor(),
    };
    refresh(ws);
}

/// A key while a sequence is pending.
pub fn key(ws: *Workspace, k: platform.KeyEvent) void {
    const st = &ws.leader;
    // Vim's q doesn't see the keys taken here.
    if (ws.key_hook) |h| if (h.enabled) h.recordKey(h.ctx, k);
    const chord: Chord = .{ .key = k.key, .mods = k.mods };
    if (chord.eql(escape) or chord.eql(ctrl_g)) {
        st.active = false;
        return;
    }
    if (chord.eql(backspace)) {
        if (st.seq.len == 0) {
            st.active = false;
        } else {
            st.seq.len -= 1;
            refresh(ws);
        }
        return;
    }
    if (!st.seq.push(.fromEvent(k))) return notBound(ws);
    switch (ws.keymap.leader.lookup(st.seq, context(ws))) {
        .command => |target| {
            st.active = false;
            run(ws, target);
        },
        .prefix => refresh(ws),
        .none => notBound(ws),
    }
}

fn run(ws: *Workspace, target: leader.Target) void {
    const started_in = ws.leader.editor;
    switch (target) {
        .action => |a| ws.perform(a),
        .window => |ch| if (window_cmd.run(ws, ch, 0)) |msg| ws.setStatus("{s}", .{msg}),
        .group, .unbound => unreachable,
    }
    if (ws.key_hook) |h| if (h.enabled) h.afterLeader(h.ctx, ws, started_in);
}

fn notBound(ws: *Workspace) void {
    const st = &ws.leader;
    var buf: [64]u8 = undefined;
    ws.setStatus("{s} is not bound", .{st.seq.format(&buf)});
    st.active = false;
    st.notice = !ws.status_visible or ws.distraction_free;
    st.since = platform.nowMs();
}

/// The localleader (SPC m) is org files'.
fn context(ws: *Workspace) leader.Ctx {
    const ed = ws.activeEditor() orelse return .{};
    return .{ .org = org.isOrg(ed) };
}

fn refresh(ws: *Workspace) void {
    const st = &ws.leader;
    const kids = ws.keymap.leader.children(st.seq, context(ws), &st.children);
    for (kids) |*c| if (c.label.len == 0) {
        c.label = fallbackLabel(c.target);
    };
    st.n_children = @intCast(kids.len);
}

/// For keymap.json entries without a "label": a command's palette caption
/// without its category, else its name.
fn fallbackLabel(target: leader.Target) []const u8 {
    return switch (target) {
        .action => |a| for (commands.all) |c| {
            if (c.action != a) continue;
            const colon = std.mem.indexOf(u8, c.caption, ": ") orelse break c.caption;
            break c.caption[colon + 2 ..];
        } else @tagName(a),
        .window => "Window command",
        .group, .unbound => "+prefix",
    };
}

/// "SPC b" while a sequence is pending, for the status bar.
pub fn statusText(ws: *Workspace, buf: []u8) ?[]const u8 {
    if (!ws.leader.active) return null;
    return ws.leader.seq.format(buf);
}

/// The which-key delay and the notice's end.
pub fn tick(ws: *Workspace) void {
    const st = &ws.leader;
    const now = platform.nowMs();
    if (st.active and !st.shown and now - st.since >= ws.env.settings.which_key_delay_ms) {
        st.shown = true;
        ws.dirty = true;
    }
    if (st.notice and now - st.since >= notice_ms) {
        st.notice = false;
        ws.dirty = true;
    }
}

pub fn nextTimer(ws: *Workspace) ?i64 {
    const st = &ws.leader;
    const wait: i64 = if (st.active and !st.shown) ws.env.settings.which_key_delay_ms else if (st.notice) notice_ms else return null;
    return @max(1, wait - (platform.nowMs() - st.since));
}

pub fn visible(ws: *const Workspace) bool {
    return (ws.leader.active and ws.leader.shown) or ws.leader.notice;
}

// ---- Painting -----------------------------------------------------------------

const pad: f32 = 10;
const row_h: f32 = 22;
const title_h: f32 = 30;
/// Between a key and its label, and between columns.
const key_gap: f32 = 12;
const col_gap: f32 = 32;

/// The which-key popup, docked at the bottom of the editors (so it shows in
/// distraction-free mode too): the sequence so far, then the keys that can
/// follow in columns, groups as "+name". Or the not-bound notice.
pub fn paint(ws: *Workspace, ctx: *const ui.Ctx) void {
    if (ws.leader.active) paintKeys(ws, ctx) else paintNotice(ws, ctx);
}

fn paintKeys(ws: *Workspace, ctx: *const ui.Ctx) void {
    const st = &ws.leader;
    const theme = ctx.theme;
    const kids = st.children[0..st.n_children];
    var buf: [32]u8 = undefined;
    var key_w: f32 = 0;
    var label_w: f32 = 0;
    for (kids) |c| {
        key_w = @max(key_w, ctx.text.measure(c.key.format(&buf), ctx.fonts.mono));
        label_w = @max(label_w, ctx.text.measure(c.label, ctx.fonts.ui));
    }
    label_w = @min(label_w, 260);
    const area = ws.editor_rect;
    const room = area.w - 16 - 2 * pad + col_gap;
    var col_w = key_w + key_gap + label_w;
    const fit: usize = @intFromFloat(@max(1, room / (col_w + col_gap)));
    // No more rows than the editors hold: more columns instead, their
    // labels cut short.
    const max_rows: usize = @intFromFloat(@max(1, (area.h - 16 - title_h - pad) / row_h));
    const cols = @max(1, @min(kids.len, @max(fit, (kids.len + max_rows - 1) / max_rows)));
    if (cols > fit) {
        col_w = room / @as(f32, @floatFromInt(cols)) - col_gap;
        label_w = @max(0, col_w - key_w - key_gap);
    }
    const rows = (kids.len + cols - 1) / cols;
    const h = title_h + @as(f32, @floatFromInt(rows)) * row_h + pad;
    const box = popupRect(area, h);
    ctx.shadow(box, 5, 10);
    ctx.scene.quad(box, .{ .color = theme.overlay_background, .border_color = theme.panel_border, .border_width = 1, .radius = 5 });
    ctx.scene.pushClip(box) catch return;
    defer ctx.scene.popClip();
    var tbuf: [64]u8 = undefined;
    _ = ctx.label(.{ .x = box.x + pad, .y = box.y + 2, .w = box.w, .h = title_h }, st.seq.format(&tbuf), ctx.fonts.ui_bold, theme.list_foreground);
    for (kids, 0..) |c, i| {
        const x = box.x + pad + @as(f32, @floatFromInt(i / rows)) * (col_w + col_gap);
        const y = box.y + title_h + @as(f32, @floatFromInt(i % rows)) * row_h;
        _ = ctx.labelRight(.{ .x = x, .y = y, .w = key_w, .h = row_h }, c.key.format(&buf), ctx.fonts.mono, theme.syntax.constant);
        const color = if (c.target == .group) theme.syntax.function else theme.list_foreground;
        _ = ctx.labelFit(.{ .x = x + key_w + key_gap, .y = y, .w = label_w, .h = row_h }, c.label, ctx.fonts.ui, color);
    }
}

fn paintNotice(ws: *Workspace, ctx: *const ui.Ctx) void {
    var seq_buf: [64]u8 = undefined;
    var buf: [96]u8 = undefined;
    const text = std.fmt.bufPrint(&buf, "{s} is not bound", .{ws.leader.seq.format(&seq_buf)}) catch return;
    const box = popupRect(ws.editor_rect, title_h);
    const w = ctx.text.measure(text, ctx.fonts.ui) + 2 * pad;
    const r: Rect = .{ .x = box.x, .y = box.y, .w = @min(w, box.w), .h = box.h };
    ctx.shadow(r, 5, 10);
    ctx.scene.quad(r, .{ .color = ctx.theme.overlay_background, .border_color = ctx.theme.panel_border, .border_width = 1, .radius = 5 });
    _ = ctx.label(.{ .x = r.x + pad, .y = r.y, .w = r.w, .h = r.h }, text, ctx.fonts.ui, ctx.theme.list_foreground);
}

/// A popup `h` high at the bottom of `area`, as wide as it.
fn popupRect(area: Rect, h: f32) Rect {
    return .{ .x = area.x + 8, .y = @max(area.y, area.bottom() - h - 8), .w = @max(0, area.w - 16), .h = h };
}
