//! Window commands, vim's ctrl-w + key, on the split-pane groups: shared by
//! vim (ctrl-w, `:wincmd`, `:split`; vim/window.zig) and the leader's SPC w,
//! which works with vim off too. A window is a group (groups.zig keeps them
//! in a split tree like vim's), numbered left to right and top to bottom.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const groups = @import("groups.zig");
const editor_mod = @import("../editor/editor.zig");

const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;

pub const no_room = std.fmt.comptimePrint("E36: Not enough room (max {d} windows)", .{workspace_mod.max_groups});
pub const last_window = "E444: Cannot close last window";

/// Runs window command `ch` (ctrl-w `ch`) with `count` (0: none); returns a
/// vim error message when it can't. `q` closes the window, or the tab in a
/// single window (vim's own ctrl-w q is `:quit`).
pub fn run(ws: *Workspace, ch: u21, count: usize) ?[]const u8 {
    const g = ws.focused_group;
    switch (ch) {
        's', 'S' => return splitWindow(ws, false, .clone, count),
        'v' => return splitWindow(ws, true, .clone, count),
        'n' => return splitWindow(ws, false, .empty, count),
        'h', 'j', 'k', 'l' => _ = groups.focusDirection(ws, switch (ch) {
            'h' => .left,
            'j' => .down,
            'k' => .up,
            else => .right,
        }, count, true),
        'w', 'W' => if (count > 0) focusNumber(ws, count) else groups.focusNext(ws, ch == 'w', true),
        'p' => if (ws.prev_focused_group) |p| if (p < ws.groupCount() and ws.group_active[p] != null) ws.focusGroup(p),
        't' => focusNumber(ws, 1),
        'b' => focusNumber(ws, ws.groupCount()),
        'c' => if (!groups.closeWindow(ws, numbered(ws, count), .close)) return last_window,
        'q' => {
            if (count > 0) focusNumber(ws, count);
            if (!groups.closeWindow(ws, ws.focused_group, .close)) ws.perform(.close_tab);
        },
        'o' => {
            if (count > 0) focusNumber(ws, count);
            groups.only(ws);
        },
        'T' => _ = groups.closeWindow(ws, g, .to_tab),
        '=' => groups.equalize(ws),
        '+', '-', '>', '<', '_', '|' => if (ws.activeEditor()) |ed| resize(ws, ed, ch, count),
        'x' => _ = groups.exchange(ws, count),
        'r', 'R' => if (!groups.rotate(ws, ch == 'r', @max(count, 1))) return "E443: Cannot rotate when another window is split",
        'H', 'J', 'K', 'L' => groups.moveToEdge(ws, switch (ch) {
            'H' => .left,
            'J' => .down,
            'K' => .up,
            else => .right,
        }),
        else => {},
    }
    return null;
}

/// ctrl-w + - > <: `count` lines or columns more or less; _ |: that many,
/// or as large as possible.
fn resize(ws: *Workspace, ed: *Editor, ch: u21, count: usize) void {
    const g = ws.focused_group;
    const n: f32 = @floatFromInt(@max(count, 1));
    switch (ch) {
        '+', '-' => _ = groups.resizeBy(ws, g, false, if (ch == '+') n * ed.lineHeight() else -n * ed.lineHeight()),
        '>', '<' => _ = groups.resizeBy(ws, g, true, if (ch == '>') n * ed.charWidth() else -n * ed.charWidth()),
        '_' => _ = groups.setSize(ws, g, false, if (count > 0) height(ws, ed, count) else null),
        else => _ = groups.setSize(ws, g, true, if (count > 0) n * ed.charWidth() else null),
    }
}

fn splitWindow(ws: *Workspace, vertical: bool, view: groups.NewView, size: usize) ?[]const u8 {
    split(ws, vertical, view, size) catch |err| return if (err == error.NoRoom) no_room else @errorName(err);
    return null;
}

/// Splits the focused window (`vertical`: side by side) to show `view`,
/// `size` lines or columns big (0: half), above or left of it unless
/// `:set splitbelow` / `splitright`.
pub fn split(ws: *Workspace, vertical: bool, view: groups.NewView, size: usize) !void {
    try groups.split(ws, vertical, if (vertical) ws.split_right else ws.split_below, view);
    if (size == 0) return;
    const ed = ws.activeEditor() orelse return;
    const px = if (vertical) @as(f32, @floatFromInt(size)) * ed.charWidth() else height(ws, ed, size);
    _ = groups.setSize(ws, ws.focused_group, vertical, px);
}

/// Group `count` (1-based), or the focused one without a count.
pub fn numbered(ws: *Workspace, count: usize) u8 {
    return if (count > 0) @intCast(@min(count, ws.groupCount()) - 1) else ws.focused_group;
}

/// Focuses window `number` (the last if there are fewer); vim keys can't act
/// in an empty group, so those are left alone.
pub fn focusNumber(ws: *Workspace, number: usize) void {
    const g = numbered(ws, @max(number, 1));
    if (ws.group_active[g] != null) ws.focusGroup(g);
}

/// Pixel height of a group showing `lines` lines.
pub fn height(ws: *Workspace, ed: *Editor, lines: usize) f32 {
    const chrome: f32 = if (ws.tabs_visible) workspace_mod.tabbar_h else 0;
    return @as(f32, @floatFromInt(lines)) * ed.lineHeight() + chrome;
}
