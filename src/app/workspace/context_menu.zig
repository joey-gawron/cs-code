//! Right-click menus. A right click (ctrl+click on macOS, or the Menu key
//! and shift+F10 in an editor) opens one at the pointer, listing what makes
//! sense for the surface clicked: the text, a tab, a file or folder in the
//! side bar, a terminal or the Source Control view. The menu bar's dropdown
//! paints, hovers and navigates it (Workspace.paintMenuPanel).
//!
//! Most entries are editor actions, which act on the active view, so
//! opening a menu first makes the clicked thing active (the tab, the file).
//! Entries that act on the clicked path itself are menu.Command values,
//! carried out by `run`.

const std = @import("std");
const builtin = @import("builtin");
const workspace_mod = @import("workspace.zig");
const menu = @import("menu.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const file_cmd = @import("file_cmd.zig");
const terminal_ui = @import("terminal_ui.zig");
const git_status_ui = @import("git_status_ui.zig");
const rpath = @import("../../model/remote/path.zig");
const Action = @import("../settings/keymap.zig").Action;

const Workspace = workspace_mod.Workspace;
const Rect = scene_mod.Rect;
const Item = menu.Item;

/// An open right-click menu.
pub const Open = struct {
    surface: Surface,
    items: std.ArrayList(Item) = .empty,
    /// Where it was opened (the pointer, or the caret for the keyboard).
    anchor: platform.Vec2,
    /// The clicked file or folder, for menu.Command entries (owned).
    path: ?[]u8 = null,

    pub fn deinit(self: *Open, gpa: std.mem.Allocator) void {
        self.items.deinit(gpa);
        if (self.path) |p| gpa.free(p);
    }
};

pub const Surface = enum { editor, tab, file, folder, terminal, source_control };

fn a(caption: []const u8, action: Action) Item {
    return .{ .action = .{ .caption = caption, .action = action } };
}
fn cmd(caption: []const u8, command: menu.Command) Item {
    return .{ .command = .{ .caption = caption, .command = command } };
}
const sep: Item = .separator;

const reveal_caption = if (builtin.os.tag.isDarwin()) "Reveal in Finder" else "Open Containing Folder";

const edit_items = [_]Item{ a("Cut", .cut), a("Copy", .copy), a("Paste", .paste) };
const lsp_items = [_]Item{
    a("Code Actions…", .lsp_code_actions),
    a("Go to Definition", .lsp_goto_definition),
    a("Find References", .lsp_find_references),
    a("Rename Symbol…", .lsp_rename),
    a("Format Document", .lsp_format),
};
const org_items = [_]Item{
    a("Cycle TODO State", .org_cycle_todo),
    a("Toggle Checkbox", .org_toggle_checkbox),
    a("Schedule…", .org_schedule),
    a("Deadline…", .org_deadline),
    a("Open Link", .org_open_link),
};
const file_items = [_]Item{
    a("Copy Path", .copy_file_path),
    a("Copy Path from Project", .copy_relative_path),
    a("Reveal in Side Bar", .reveal_in_sidebar),
};
const tab_items = [_]Item{
    a("Close", .close_tab),
    a("Close Others", .close_other_tabs),
    cmd("Close Tabs to the Right", .close_tabs_to_right),
    sep,
    a("Copy Path", .copy_file_path),
    a("Copy Path from Project", .copy_relative_path),
    a("Reveal in Side Bar", .reveal_in_sidebar),
    cmd(reveal_caption, .reveal_in_os),
    sep,
    a("Move to Next Group", .move_to_next_group),
};
const sidebar_file_items = [_]Item{
    a("Rename…", .rename_file),
    a("Delete…", .delete_file),
    sep,
    cmd("Copy Path", .copy_path),
    cmd("Copy Path from Project", .copy_relative_path),
    cmd(reveal_caption, .reveal_in_os),
    cmd("Open with Default App", .open_externally),
    sep,
    cmd("New Terminal Here", .new_terminal_here),
};
const sidebar_folder_items = [_]Item{
    cmd("Find in Folder…", .find_in_folder),
    cmd("New Terminal Here", .new_terminal_here),
    sep,
    cmd("Copy Path", .copy_path),
    cmd("Copy Path from Project", .copy_relative_path),
    cmd(reveal_caption, .reveal_in_os),
};
const terminal_items = [_]Item{ a("Copy", .copy), a("Paste", .paste), a("Select All", .select_all) };
const source_control_items = [_]Item{
    cmd("Stage", .git_stage),
    cmd("Unstage", .git_unstage),
    cmd("Discard…", .git_discard),
    sep,
    cmd("Open", .git_open),
};

/// Opens the menu for `surface` at `anchor`. `path` is the clicked file or
/// folder for side bar menus (copied).
pub fn open(ws: *Workspace, surface: Surface, anchor: platform.Vec2, path: ?[]const u8) void {
    close(ws);
    var m: Open = .{ .surface = surface, .anchor = anchor };
    build(ws, &m) catch {
        m.deinit(ws.gpa);
        return;
    };
    if (path) |p| m.path = ws.gpa.dupe(u8, p) catch {
        m.deinit(ws.gpa);
        return;
    };
    ws.menu_open = null;
    ws.menu_hover_item = null;
    ws.context_menu = m;
    ws.dirty = true;
}

pub fn close(ws: *Workspace) void {
    if (ws.context_menu) |*m| m.deinit(ws.gpa);
    ws.context_menu = null;
    ws.menu_hover_item = null;
}

fn build(ws: *Workspace, m: *Open) !void {
    const gpa = ws.gpa;
    const items = &m.items;
    switch (m.surface) {
        .editor => {
            const ed = ws.activeEditor();
            const read_only = if (ed) |e| e.buffer.read_only else true;
            if (read_only) try items.append(gpa, a("Copy", .copy)) else try items.appendSlice(gpa, &edit_items);
            try items.append(gpa, a("Select All", .select_all));
            if (ed) |e| {
                if (!read_only and ws.lsp != null and e.buffer.path != null) {
                    try items.append(gpa, sep);
                    try items.appendSlice(gpa, &lsp_items);
                }
                if (!read_only and isOrg(e.buffer.path)) {
                    try items.append(gpa, sep);
                    try items.appendSlice(gpa, &org_items);
                }
                if (!read_only) {
                    try items.append(gpa, sep);
                    try items.append(gpa, a("Toggle Comment", .toggle_comment));
                }
                if (e.buffer.path != null) {
                    try items.append(gpa, sep);
                    try items.appendSlice(gpa, &file_items);
                }
            }
        },
        .tab => try items.appendSlice(gpa, &tab_items),
        .file => try items.appendSlice(gpa, &sidebar_file_items),
        .folder => try items.appendSlice(gpa, &sidebar_folder_items),
        .terminal => try items.appendSlice(gpa, &terminal_items),
        .source_control => try items.appendSlice(gpa, &source_control_items),
    }
}

fn isOrg(path: ?[]const u8) bool {
    const p = path orelse return false;
    return std.mem.endsWith(u8, p, ".org");
}

/// Runs the chosen item and closes the menu.
pub fn choose(ws: *Workspace, item: Item) void {
    var m = ws.context_menu orelse return;
    ws.context_menu = null;
    ws.menu_hover_item = null;
    defer m.deinit(ws.gpa);
    switch (item) {
        .action => |x| ws.perform(x.action),
        .command => |x| run(ws, &m, x.command),
        .separator => {},
    }
    ws.dirty = true;
}

fn run(ws: *Workspace, m: *const Open, command: menu.Command) void {
    // Tab menus act on the active file, which the click made the clicked tab's.
    const path: ?[]const u8 = m.path orelse if (ws.activeEditor()) |ed| ed.buffer.path else null;
    switch (command) {
        .copy_path, .copy_relative_path => {
            const p = path orelse return;
            const text = if (command == .copy_path) p else if (ws.project) |proj| proj.relativePath(p) orelse p else p;
            ws.env.clipboard.set(ws.env.clipboard.ctx, text);
            ws.setStatus("Copied {s}", .{text});
        },
        .reveal_in_os => file_cmd.revealInOs(ws, path orelse return, m.surface == .folder),
        .open_externally => {
            const p = path orelse return;
            if (rpath.isRemote(p)) return ws.setStatus("Can't open a file on another machine here", .{});
            file_cmd.openExternally(ws, p);
        },
        .new_terminal_here => {
            const p = path orelse return;
            if (rpath.isRemote(p)) return ws.setStatus("Terminals run on this machine; use the remote's shell", .{});
            const dir = if (m.surface == .folder) p else std.fs.path.dirname(p) orelse p;
            terminal_ui.openAt(ws, dir);
        },
        .find_in_folder => ws.showFindIn(path orelse return),
        .close_tabs_to_right => ws.closeTabsToRight(),
        .git_stage, .git_unstage, .git_discard, .git_open => {
            const p = ws.panel.source_control orelse return;
            _ = git_status_ui.statusKey(p, switch (command) {
                .git_stage => 's',
                .git_unstage => 'u',
                .git_discard => 'x',
                else => 'o',
            });
        },
    }
}

/// The open menu's panel: below and right of the anchor, moved back inside
/// `bounds` when it would cross its right or bottom edge.
pub fn place(anchor: platform.Vec2, w: f32, h: f32, bounds: Rect) Rect {
    var x = anchor.x;
    var y = anchor.y;
    if (x + w > bounds.right()) x = @max(bounds.x, anchor.x - w);
    if (y + h > bounds.bottom()) y = @max(bounds.y, anchor.y - h);
    return .{ .x = x, .y = y, .w = w, .h = h };
}

test "place keeps the menu inside the window" {
    const bounds: Rect = .{ .x = 0, .y = 0, .w = 800, .h = 600 };
    // Room below and to the right: opens there.
    try std.testing.expectEqual(Rect{ .x = 100, .y = 100, .w = 200, .h = 150 }, place(.{ .x = 100, .y = 100 }, 200, 150, bounds));
    // Near the right edge: opens to the left of the pointer.
    try std.testing.expectEqual(@as(f32, 550), place(.{ .x = 750, .y = 100 }, 200, 150, bounds).x);
    // Near the bottom: opens above.
    try std.testing.expectEqual(@as(f32, 420), place(.{ .x = 100, .y = 570 }, 200, 150, bounds).y);
    // Taller than the window: pinned to the top.
    try std.testing.expectEqual(@as(f32, 0), place(.{ .x = 100, .y = 300 }, 200, 700, bounds).y);
}
