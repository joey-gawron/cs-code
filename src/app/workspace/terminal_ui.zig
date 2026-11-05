//! Workspace glue for terminal tabs: opening them in the right folder,
//! routing input to the focused one, painting, and servicing their ptys
//! from the event loop. Terminals in the panel under the editors
//! (panel_ui.zig) get their keys, drags and output from here too.
//!
//! A terminal tab is an ordinary tab (so it works with split panes, tab
//! switching and the MRU stack) whose editor shows an empty read-only buffer
//! named after the terminal's title; `Tab.terminal` is drawn instead of it.
//!
//! Keys go to the program, except window chords (ctrl+shift+…, ctrl+alt+…,
//! alt+shift+…, super+…, ctrl/alt+digit, ctrl+tab, ctrl+PgUp/PgDn, ctrl+=,
//! ctrl+-, ctrl+`, F11), which run their key binding when they have one,
//! and the keys bound to the leader (alt+m) and the command palette (alt+x).
//! ctrl+shift+c / ctrl+shift+v copy and paste (ctrl+c copies when there is
//! a selection), shift+Insert pastes, shift+PgUp/PgDn scroll, ctrl+shift+w
//! closes the tab.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const editor_view = @import("../editor/editor_view.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const keymap_mod = @import("../settings/keymap.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const TerminalView = @import("../terminal/terminal_view.zig").TerminalView;
const panel_ui = @import("panel_ui.zig");
const context_menu = @import("context_menu.zig");

const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Action = keymap_mod.Action;
const Rect = scene_mod.Rect;

pub const Where = enum {
    /// The active file's folder (or the folder a terminal's shell is in).
    file_folder,
    project_root,
};

/// The terminal shown in the focused group, if any.
pub fn active(ws: *Workspace) ?*TerminalView {
    const i = ws.activeIndex() orelse return null;
    return ws.tabs.items[i].terminal;
}

/// The terminal that has the keyboard: the focused group's tab, or the panel's.
pub fn focused(ws: *Workspace) ?*TerminalView {
    return switch (ws.focus) {
        .editor => active(ws),
        .panel => panel_ui.focusedTerminal(ws),
        else => null,
    };
}

/// Folder for a new terminal: the active file's (in an empty pane, the last
/// edited file's), else the project root, else the editor's working
/// directory. From a terminal, the folder its shell is in now, so "new
/// terminal" opens next to it.
pub fn startDir(ws: *Workspace, where: Where, buf: []u8) []const u8 {
    if (where == .project_root) if (ws.project) |p| return p.root;
    if (focused(ws)) |tv| return tv.term.currentDir(buf) orelse tv.start_dir;
    if (ws.activeEditor()) |ed| {
        if (ed.buffer.path) |p| if (std.fs.path.dirname(p)) |d| return d;
    } else {
        // An empty pane: the file edited last.
        for (ws.mru.items) |e| if (e.buffer.path) |p| if (std.fs.path.dirname(p)) |d| return d;
    }
    if (ws.project) |p| return p.root;
    @memset(buf, 0); // See Workspace.realPath.
    const n = std.process.currentPath(ws.io, buf) catch return "/";
    return buf[0..n];
}

/// Opens a terminal in a new tab of the focused group.
pub fn open(ws: *Workspace, where: Where) void {
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    openAt(ws, startDir(ws, where, &dir_buf));
}

/// Opens a terminal in `dir`, in a new tab of the focused group.
pub fn openAt(ws: *Workspace, dir: []const u8) void {
    var rect = ws.group_editor_rects[ws.focused_group];
    if (rect.w < 100 or rect.h < 60) rect = .{ .w = 800, .h = 480 };
    const tv = TerminalView.create(ws.gpa, ws.env, .{ .cwd = dir, .rect = rect }) catch |err| {
        ws.setStatus("Unable to start a terminal: {t}", .{err});
        return;
    };
    const buffer = ws.gpa.create(Buffer) catch return tv.destroy();
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return tv.destroy();
    };
    ws.next_buffer_id += 1;
    buffer.display_name = tv.title();
    buffer.read_only = true;
    ws.buffers.append(ws.gpa, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return tv.destroy();
    };
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch return tv.destroy();
    _ = ws.insertTab(.{ .editor = ed, .terminal = tv }) catch {
        ed.destroy();
        return tv.destroy();
    };
    ws.focus = .editor;
    ws.updateTitle();
}

/// ctrl+`: from a terminal back to the last file; otherwise to the most
/// recently used terminal, or a new one.
pub fn toggle(ws: *Workspace) void {
    const want_terminal = active(ws) == null;
    for (ws.mru.items) |e| if (ws.tabIndexOf(e)) |i| if ((ws.tabs.items[i].terminal != null) == want_terminal) {
        ws.activateTab(i);
        ws.focus = .editor;
        return;
    };
    if (want_terminal) open(ws, .file_folder);
}

/// Input for terminals: keys and text when one has focus, clicks and the
/// wheel over one. Returns true if consumed.
pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    switch (ev) {
        .key_down => |k| {
            if (ws.menuIsOpen()) return false;
            const tv = focused(ws) orelse return false;
            return handleKey(ws, tv, k);
        },
        .text_input => |t| {
            const tv = focused(ws) orelse return false;
            if (!tv.exited) tv.typeText(t);
            return true;
        },
        .preedit => return focused(ws) != null,
        .mouse_down => |m| {
            if (ws.menuIsOpen() or ws.overlay != null) return false;
            const g = groupAt(ws, m.pos) orelse return false;
            const ed = ws.group_active[g] orelse return false;
            const tv = terminalOf(ws, ed) orelse return false;
            if (g != ws.focused_group) ws.focusGroup(g);
            ws.focus = .editor;
            ed.focused = true;
            if (m.button == .right and !tv.reportsMouse(m.mods)) {
                context_menu.open(ws, .terminal, m.pos, null);
                return true;
            }
            tv.mouseDown(m);
            return true;
        },
        .mouse_move => |m| {
            for (ws.tabs.items) |t| if (t.terminal) |tv| if (tv.selecting or tv.reporting_button != null) tv.mouseMove(m);
            for (panel_ui.terminals(ws)) |t| if (t.view.selecting or t.view.reporting_button != null) t.view.mouseMove(m);
            // Programs that track every motion (mouse mode 1003) under the pointer.
            for (ws.group_active[0..ws.groupCount()]) |ged| if (ged) |ed| if (terminalOf(ws, ed)) |tv| trackMotion(tv, m);
            if (ws.panel.visible and ws.panel.view == .terminal) if (panel_ui.activeTerminal(ws)) |tv| trackMotion(tv, m);
            return false;
        },
        .mouse_up => |m| {
            for (ws.tabs.items) |t| if (t.terminal) |tv| if (tv.selecting or tv.reporting_button != null) tv.mouseUp(m);
            for (panel_ui.terminals(ws)) |t| if (t.view.selecting or t.view.reporting_button != null) t.view.mouseUp(m);
            return false;
        },
        .scroll => |s| {
            if (ws.overlay != null) return false;
            for (ws.group_editor_rects[0..ws.groupCount()], 0..) |r, g| if (r.contains(s.pos.x, s.pos.y)) {
                const ed = ws.group_active[g] orelse return false;
                const tv = terminalOf(ws, ed) orelse return false;
                tv.wheel(s);
                return true;
            };
            return false;
        },
        .focus => |f| {
            if (focused(ws)) |tv| if (tv.term.screen.focus_reporting and !tv.exited) {
                tv.term.screen.focus(f);
                tv.term.flush();
            };
            return false;
        },
        else => return false,
    }
}

fn trackMotion(tv: *TerminalView, m: platform.MouseMoveEvent) void {
    if (tv.term.screen.mouse_mode == .move and !tv.selecting and tv.reporting_button == null and tv.rect.contains(m.pos.x, m.pos.y)) tv.mouseMove(m);
}

fn terminalOf(ws: *Workspace, ed: *Editor) ?*TerminalView {
    const i = ws.tabIndexOf(ed) orelse return null;
    return ws.tabs.items[i].terminal;
}

/// The group whose view area holds `pos`, leaving the splitters and the side
/// bar's edge (4 px either side) to the workspace.
fn groupAt(ws: *Workspace, pos: platform.Vec2) ?u8 {
    const area = ws.editor_rect;
    for (ws.group_editor_rects[0..ws.groupCount()], 0..) |r, g| {
        if (!r.contains(pos.x, pos.y)) continue;
        if (r.x > area.x + 1 and pos.x < r.x + 4) return null;
        if ((r.right() < area.right() - 1 or ws.sidebar_rect.w > 0) and pos.x > r.right() - 4) return null;
        if (r.bottom() < area.bottom() - 1 and pos.y > r.bottom() - 4) return null;
        return @intCast(g);
    }
    return null;
}

/// Window-level chords that run their key binding instead of reaching the
/// program (when one is bound).
fn windowChord(ch: keymap_mod.Chord) bool {
    const m = ch.mods;
    if (m.super or (m.ctrl and m.shift) or (m.ctrl and m.alt) or (m.alt and m.shift)) return true;
    switch (ch.key) {
        .char => |cp| {
            if ((m.ctrl or m.alt) and cp >= '0' and cp <= '9') return true;
            if (m.ctrl and (cp == '=' or cp == '-' or cp == '`')) return true;
        },
        .named => |n| switch (n) {
            .tab, .page_up, .page_down => return m.ctrl,
            .f11 => return true,
            else => {},
        },
    }
    return false;
}

/// The leader and the command palette (Doom's M-x) reach the editor from a
/// terminal on whatever keys they are bound to.
fn passesTerminals(result: keymap_mod.Keymap.Result) bool {
    return switch (result) {
        .action => |a| a == .leader or a == .command_palette,
        else => false,
    };
}

/// Closes a terminal's tab, or removes it from the panel.
fn close(ws: *Workspace, tv: *TerminalView) void {
    for (ws.tabs.items, 0..) |t, i| if (t.terminal == tv) return ws.closeTab(i);
    panel_ui.closeTerminal(ws, tv);
}

fn handleKey(ws: *Workspace, tv: *TerminalView, k: platform.KeyEvent) bool {
    // After "[Process exited …]" any key closes it, except keys bound to
    // something else (ctrl+b runs the command again).
    if (tv.exited) {
        if (k.mods.ctrl or k.mods.alt or k.mods.super or k.key == .named) {
            switch (ws.keymap.match(.{ .key = k.key, .mods = k.mods }, false)) {
                .action => |a| if (a != .cancel) {
                    ws.perform(a);
                    return true;
                },
                .pending => return true,
                .none => {},
            }
        }
        close(ws, tv);
        return true;
    }
    const m = k.mods;
    const only_shift = m.shift and !m.ctrl and !m.alt and !m.super;
    const ctrl_shift = m.ctrl and m.shift and !m.alt and !m.super;
    // macOS: Command+C / Command+V, as in Terminal.app.
    const command = m.super and !m.ctrl and !m.alt and !m.shift;
    switch (k.key) {
        .char => |cp| {
            if (command and cp == 'c') return copy(ws, tv);
            if (command and cp == 'v') return paste(ws, tv);
            if (ctrl_shift and cp == 'c') return copy(ws, tv);
            if (ctrl_shift and cp == 'v') return paste(ws, tv);
            // ctrl+w is the shell's; ctrl+shift+w closes the tab, as in gnome-terminal.
            if (ctrl_shift and cp == 'w') {
                close(ws, tv);
                return true;
            }
            // Like Sublime (and Windows Terminal): ctrl+c copies a selection.
            if (m.ctrl and !m.shift and !m.alt and cp == 'c' and tv.hasSelection()) {
                _ = copy(ws, tv);
                tv.clearSelection();
                return true;
            }
        },
        .named => |n| switch (n) {
            .insert => {
                if (only_shift) return paste(ws, tv);
                if (m.ctrl and !m.shift and !m.alt) return copy(ws, tv);
            },
            .page_up, .page_down => if (only_shift and !tv.term.screen.alt_screen) {
                tv.pageScroll(n == .page_up);
                return true;
            },
            else => {},
        },
    }
    const chord: keymap_mod.Chord = .{ .key = k.key, .mods = k.mods };
    if (ws.keymap.pending != null or windowChord(chord) or passesTerminals(ws.keymap.peek(chord, false))) switch (ws.keymap.match(chord, false)) {
        .action => |a| {
            ws.perform(a);
            return true;
        },
        .pending => {
            var buf: [32]u8 = undefined;
            ws.setStatus("{s} was pressed, waiting for second key…", .{chord.format(&buf)});
            return true;
        },
        .none => {},
    };
    if (tv.key(k)) {
        if (ws.focus == .panel) {
            panel_ui.touch(ws);
        } else if (ws.activeIndex()) |i| ws.tabs.items[i].editor.touchCaret();
    }
    return true;
}

fn copy(ws: *Workspace, tv: *TerminalView) bool {
    const text = tv.selectedText(ws.gpa) orelse return true;
    defer ws.gpa.free(text);
    ws.env.clipboard.set(ws.env.clipboard.ctx, text);
    return true;
}

fn paste(ws: *Workspace, tv: *TerminalView) bool {
    const text = ws.env.clipboard.get(ws.env.clipboard.ctx, ws.gpa) orelse return true;
    defer ws.gpa.free(text);
    tv.paste(text);
    return true;
}

/// Terminal commands, and what edit commands mean in a terminal. Returns
/// true if handled.
pub fn perform(ws: *Workspace, action: Action) bool {
    switch (action) {
        .new_terminal => open(ws, .file_folder),
        .new_terminal_at_project_root => open(ws, .project_root),
        .toggle_terminal => toggle(ws),
        .toggle_panel_terminal => panel_ui.toggleTerminal(ws),
        .new_panel_terminal => panel_ui.addTerminal(ws),
        .run_command => panel_ui.promptRunCommand(ws),
        .run_last_command => panel_ui.rerunCommand(ws),
        else => {
            const tv = focused(ws) orelse return false;
            switch (action) {
                .copy, .cut => _ = copy(ws, tv),
                .paste, .paste_and_indent => _ = paste(ws, tv),
                .select_all => tv.selectAll(),
                // Nothing to save, revert, clone or go to in a terminal tab
                // (with a panel terminal focused, these are the file's).
                .save, .save_as, .revert_file, .clone_file, .goto_line, .goto_symbol => if (ws.focus == .panel) return false,
                else => return false,
            }
        },
    }
    return true;
}

/// Draws the terminal of `ed`'s tab in `rect`; false if it isn't one.
pub fn paint(ws: *Workspace, ed: *Editor, rect: Rect, scene: *scene_mod.Scene) bool {
    const tv = terminalOf(ws, ed) orelse return false;
    tv.layout(rect);
    tv.paint(scene, ws.theme, ed.focused, editor_view.caretVisible(ed));
    return true;
}

/// Status bar text while a terminal is active.
pub fn statusText(ws: *Workspace, buf: []u8) ?[]const u8 {
    const tv = active(ws) orelse return null;
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    var dir = tv.term.currentDir(&dir_buf) orelse tv.start_dir;
    var home_prefix: []const u8 = "";
    if (std.c.getenv("HOME")) |h| {
        const home = std.mem.span(h);
        if (home.len > 1 and std.mem.startsWith(u8, dir, home) and (dir.len == home.len or dir[home.len] == '/')) {
            dir = dir[home.len..];
            home_prefix = "~";
        }
    }
    const screen = tv.term.screen;
    if (tv.exited) return std.fmt.bufPrint(buf, "Terminal  {s}{s}  (exited)", .{ home_prefix, dir }) catch null;
    if (tv.scroll > 0) return std.fmt.bufPrint(buf, "Terminal  {s}{s}  {d}×{d}  ({d} lines up)", .{ home_prefix, dir, screen.cols, screen.rows, tv.scroll }) catch null;
    return std.fmt.bufPrint(buf, "Terminal  {s}{s}  {d}×{d}", .{ home_prefix, dir, screen.cols, screen.rows }) catch null;
}

/// The ptys of running terminals, for the event loop.
pub fn pollFds(ws: *Workspace, out: *std.ArrayList(platform.PollFd)) void {
    for (ws.tabs.items) |t| if (t.terminal) |tv| if (tv.term.pollFd()) |fd| out.append(ws.gpa, fd) catch {};
    for (panel_ui.terminals(ws)) |t| if (t.view.term.pollFd()) |fd| out.append(ws.gpa, fd) catch {};
}

/// Reads every running terminal's output (non-blocking, so it doesn't
/// matter which fd woke us; it also covers terminals past the window
/// backend's poll limit), updates titles, and handles programs that ended:
/// a clean exit closes the tab, anything else leaves the tab open with the
/// exit status under the last output.
pub fn service(ws: *Workspace, fds: []const platform.PollFd) void {
    _ = fds;
    panel_ui.service(ws);
    var i: usize = 0;
    while (i < ws.tabs.items.len) {
        const t = ws.tabs.items[i];
        const tv = t.terminal orelse {
            i += 1;
            continue;
        };
        if (tv.exited) {
            i += 1;
            continue;
        }
        if (tv.service()) ws.dirty = true;
        if (tv.title_changed) {
            tv.title_changed = false;
            t.editor.buffer.display_name = tv.title();
            if (ws.activeIndex() == i) ws.updateTitle();
            ws.dirty = true;
        }
        if (tv.finished()) {
            ws.dirty = true;
            const status = tv.term.exitStatus();
            if (status != null and status.? == .code and status.?.code == 0) {
                ws.closeTab(i);
                continue;
            }
            tv.showExit();
            ws.setStatus("{s}: process exited", .{tv.title()});
        }
        i += 1;
    }
}
