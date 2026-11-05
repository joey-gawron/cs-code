//! Vim window commands on Sublime's split panes: ctrl-w + key, `:split`,
//! `:vsplit`, `:new`, `:close`, `:only`, `:hide`, `:wincmd`, `:resize`, and
//! the tab commands. A vim window is a group (workspace/groups.zig keeps them
//! in a split tree like vim's); vim's tab pages are the focused group's tabs.
//! The ctrl-w commands themselves are workspace/window_cmd.zig's, which the
//! leader's SPC w runs too.

const std = @import("std");
const vim_mod = @import("vim.zig");
const ex = @import("ex.zig");
const workspace_mod = @import("../workspace/workspace.zig");
const groups = @import("../workspace/groups.zig");
const window_cmd = @import("../workspace/window_cmd.zig");
const editor_mod = @import("../editor/editor.zig");

const Vim = vim_mod.Vim;
const VKey = vim_mod.VKey;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;

/// ctrl-w: waits for the window command, shown like other pending keys.
pub fn begin(vim: *Vim) bool {
    vim.prefix = .ctrl_w;
    const shown = "^W";
    if (vim.pending_len + shown.len <= vim.pending.len) {
        @memcpy(vim.pending[vim.pending_len..][0..shown.len], shown);
        vim.pending_len += shown.len;
    }
    return false;
}

/// The key after ctrl-w; a count may come before or in between. Returns
/// true when the command is complete.
pub fn key(vim: *Vim, ws: *Workspace, vk: VKey) bool {
    const ch: u21 = switch (vk) {
        .char => |c| c,
        // ctrl-w ctrl-v is ctrl-w v; ctrl-w ctrl-c cancels.
        .ctrl => |c| if (c == 'c') return true else c,
        .named => |n| switch (n) {
            .left, .backspace => 'h',
            .right => 'l',
            .up => 'k',
            .down => 'j',
            else => return true,
        },
    };
    if (ch >= '0' and ch <= '9' and (ch != '0' or vim.count > 0)) {
        vim.count = vim.count * 10 + @as(usize, ch - '0');
        vim.prefix = .ctrl_w;
        return false;
    }
    command(vim, ws, ch, vim.count);
    return true;
}

/// Runs window command `ch` (ctrl-w `ch`, `:wincmd ch`); `count` 0 means none.
pub fn command(vim: *Vim, ws: *Workspace, ch: u21, count: usize) void {
    if (ch == 'q') {
        // `:quit`: unsaved changes stop it, and the last window quits.
        if (count > 0) window_cmd.focusNumber(ws, count);
        if (ws.activeEditor()) |cur| ex.execute(vim, ws, cur, "quit");
    } else if (window_cmd.run(ws, ch, count)) |msg| vim.setMessageText(msg);
}

/// `:e {file}`: the window shows the file. A view that only duplicated a
/// buffer shown elsewhere goes (vim's window just changes buffer, as after
/// `ctrl-w v :e other`); other views stay as tabs of the group.
pub fn edit(vim: *Vim, ws: *Workspace, ed: *Editor, path: []const u8) void {
    const shown = groups.openFileHere(ws, path) catch return vim.setMessageText("E484: Can't open file");
    if (shown != ed and !groups.soleView(ws, ed)) if (ws.tabIndexOf(ed)) |i| ws.closeTabNow(i);
}

/// `:q` in a split closes the window instead of the tab; false with one group.
pub fn quitSplit(ws: *Workspace) bool {
    return groups.closeWindow(ws, ws.focused_group, .close);
}

// ---- Tabs ---------------------------------------------------------------------

/// gt / gT: `{n}gt` goes to tab n of the focused group, `{n}gT` back n tabs.
pub fn gotoTab(ws: *Workspace, forward: bool, count: usize) void {
    if (forward and count > 0) return focusTab(ws, count);
    var i: usize = 0;
    while (i < @max(count, 1)) : (i += 1) ws.perform(if (forward) .next_tab else .prev_tab);
}

/// Tab `number` (1-based; the last if there are fewer) of the focused group.
fn focusTab(ws: *Workspace, number: usize) void {
    var last: ?usize = null;
    var n: usize = 0;
    for (ws.tabs.items, 0..) |t, i| if (t.group == ws.focused_group) {
        n += 1;
        last = i;
        if (n == number) break;
    };
    if (last) |i| ws.activateTab(i);
}

/// `:tabclose`: closes the view; an emptied group closes too.
fn tabClose(vim: *Vim, ws: *Workspace, ed: *Editor, bang: bool) void {
    const sole = groups.soleView(ws, ed);
    if (ed.buffer.isDirty() and sole and !bang) return vim.setMessageText("E37: No write since last change (add ! to override)");
    const g = ws.focused_group;
    var in_group: usize = 0;
    for (ws.tabs.items) |t| if (t.group == g) {
        in_group += 1;
    };
    if (in_group == 1 and ws.groupCount() == 1) return vim.setMessageText("E784: Cannot close last tab page");
    if (bang and sole) ed.buffer.saved_version = ed.buffer.version;
    if (ws.tabIndexOf(ed)) |i| ws.closeTabNow(i);
    closeIfEmpty(ws);
}

/// After closing a tab: a split group left empty closes (vim keys can't act there).
pub fn closeIfEmpty(ws: *Workspace) void {
    if (ws.group_active[ws.focused_group] == null) _ = groups.closeWindow(ws, ws.focused_group, .hide);
}

// ---- Ex commands ----------------------------------------------------------------

/// `name` abbreviates `full` (at least `min` letters, like `:sp` for `:split`).
fn abbrev(name: []const u8, full: []const u8, min: usize) bool {
    return name.len >= min and name.len <= full.len and std.mem.startsWith(u8, full, name);
}

/// A count typed before the command (`:3wincmd w`).
fn leadingCount(cmd: []const u8) usize {
    var n: usize = 0;
    for (cmd) |c| {
        if (!std.ascii.isDigit(c)) break;
        n = n *| 10 +| (c - '0');
    }
    return n;
}

/// Window and tab ex commands; false if `name` isn't one of them.
pub fn exCommand(vim: *Vim, ws: *Workspace, ed: *Editor, cmd: []const u8, name: []const u8, args: []const u8, bang: bool) bool {
    const count = leadingCount(cmd);
    if (abbrev(name, "vertical", 4)) {
        verticalEx(vim, ws, ed, args, count);
    } else if (abbrev(name, "split", 2)) {
        splitEx(vim, ws, false, args, .clone, count);
    } else if (abbrev(name, "vsplit", 2)) {
        splitEx(vim, ws, true, args, .clone, count);
    } else if (abbrev(name, "new", 3)) {
        splitEx(vim, ws, false, args, .empty, count);
    } else if (abbrev(name, "vnew", 3)) {
        splitEx(vim, ws, true, args, .empty, count);
    } else if (abbrev(name, "close", 3)) {
        if (!groups.closeWindow(ws, window_cmd.numbered(ws, count), .close)) vim.setMessageText(window_cmd.last_window);
    } else if (abbrev(name, "only", 2)) {
        if (count > 0) window_cmd.focusNumber(ws, count);
        groups.only(ws);
    } else if (abbrev(name, "hide", 3)) {
        if (!groups.closeWindow(ws, window_cmd.numbered(ws, count), .hide)) vim.setMessageText(window_cmd.last_window);
    } else if (abbrev(name, "wincmd", 4)) {
        if (args.len == 0) {
            vim.setMessageText("E471: Argument required");
        } else command(vim, ws, args[0], count);
    } else if (abbrev(name, "resize", 3)) {
        resize(vim, ws, ed, false, args);
    } else if (abbrev(name, "tabnew", 6) or abbrev(name, "tabedit", 4)) {
        if (args.len > 0) {
            _ = groups.openFileHere(ws, args) catch vim.setMessageText("E484: Can't open file");
        } else _ = ws.newFile() catch {};
    } else if (abbrev(name, "tabnext", 4)) {
        const n = std.fmt.parseInt(usize, args, 10) catch 0;
        if (n > 0) focusTab(ws, n) else ws.perform(.next_tab);
    } else if (abbrev(name, "tabprevious", 4) or abbrev(name, "tabNext", 4)) {
        gotoTab(ws, false, std.fmt.parseInt(usize, args, 10) catch 1);
    } else if (abbrev(name, "tabclose", 4)) {
        tabClose(vim, ws, ed, bang);
    } else if (abbrev(name, "tabfirst", 6) or abbrev(name, "tabrewind", 4)) {
        focusTab(ws, 1);
    } else if (abbrev(name, "tablast", 4)) {
        focusTab(ws, std.math.maxInt(usize));
    } else return false;
    return true;
}

fn splitEx(vim: *Vim, ws: *Workspace, vertical_split: bool, args: []const u8, empty: groups.NewView, count: usize) void {
    window_cmd.split(ws, vertical_split, if (args.len > 0) .{ .file = args } else empty, count) catch |err| {
        if (err == error.NoRoom) return vim.setMessageText(window_cmd.no_room);
        var buf: [96]u8 = undefined;
        vim.setMessageText(std.fmt.bufPrint(&buf, "E484: Can't open file {s}", .{args}) catch "E484");
    };
}

/// `:vert[ical] {cmd}`: splits and resizes go across.
fn verticalEx(vim: *Vim, ws: *Workspace, ed: *Editor, rest: []const u8, count: usize) void {
    var n: usize = 0;
    while (n < rest.len and std.ascii.isAlphabetic(rest[n])) n += 1;
    const inner = rest[0..n];
    const args = std.mem.trim(u8, std.mem.trimStart(u8, rest[n..], "!"), " ");
    if (abbrev(inner, "split", 2) or abbrev(inner, "vsplit", 2)) {
        splitEx(vim, ws, true, args, .clone, count);
    } else if (abbrev(inner, "new", 3) or abbrev(inner, "vnew", 3)) {
        splitEx(vim, ws, true, args, .empty, count);
    } else if (abbrev(inner, "resize", 3)) {
        resize(vim, ws, ed, true, args);
    } else ex.execute(vim, ws, ed, rest);
}

/// `:res[ize] [+-]N`: N lines (columns with `:vertical`), or as large as possible.
fn resize(vim: *Vim, ws: *Workspace, ed: *Editor, across: bool, args: []const u8) void {
    const a = std.mem.trim(u8, args, " ");
    const g = ws.focused_group;
    const unit = if (across) ed.charWidth() else ed.lineHeight();
    if (a.len == 0) {
        _ = groups.setSize(ws, g, across, null);
    } else if (a[0] == '+' or a[0] == '-') {
        const n: f32 = @floatFromInt(std.fmt.parseInt(usize, a[1..], 10) catch 1);
        _ = groups.resizeBy(ws, g, across, if (a[0] == '+') n * unit else -n * unit);
    } else {
        const n = std.fmt.parseInt(usize, a, 10) catch return vim.setMessageText("E475: Invalid argument");
        _ = groups.setSize(ws, g, across, if (across) @as(f32, @floatFromInt(n)) * unit else window_cmd.height(ws, ed, n));
    }
}

/// `:set splitbelow` / `splitright` (and their `no` forms); false for other options.
pub fn setOption(ws: *Workspace, name: []const u8) bool {
    const eql = std.mem.eql;
    const off = std.mem.startsWith(u8, name, "no");
    const opt = if (off) name[2..] else name;
    if (eql(u8, opt, "sb") or eql(u8, opt, "splitbelow")) {
        ws.split_below = !off;
    } else if (eql(u8, opt, "spr") or eql(u8, opt, "splitright")) {
        ws.split_right = !off;
    } else if (eql(u8, opt, "ea") or eql(u8, opt, "equalalways")) {
        // Always on: splits and closes equalize the groups.
    } else return false;
    return true;
}
