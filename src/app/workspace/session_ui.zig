//! Workspace side of sessions: "Session: Switch…" (a list of saved and running
//! sessions; typing a new name makes one), "New Session…", "Rename Session…"
//! and "Delete Session…". A session is a process, so switching starts (or
//! shows) the other session's process and closes this window; the saved files
//! and the autosaver are store.zig's and persist.zig's.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const quick_panel = @import("quick_panel.zig");
const store_mod = @import("../session/store.zig");
const session = @import("../session/session.zig");

const Workspace = workspace_mod.Workspace;

pub const PromptKind = enum(u32) { switch_session = 400, new_session, rename_session, delete_session, confirm_delete };

/// "5m ago", "2h ago", "3d ago".
pub fn ago(buf: []u8, now: i64, then: i64) []const u8 {
    if (then <= 0) return "never";
    const d = @max(0, now - then);
    if (d < 60) return "just now";
    if (d < 3600) return std.fmt.bufPrint(buf, "{d}m ago", .{@divTrunc(d, 60)}) catch "";
    if (d < 86400) return std.fmt.bufPrint(buf, "{d}h ago", .{@divTrunc(d, 3600)}) catch "";
    return std.fmt.bufPrint(buf, "{d}d ago", .{@divTrunc(d, 86400)}) catch "";
}

fn isRunning(running: []const session.Running, name: []const u8) bool {
    for (running) |r| if (std.mem.eql(u8, r.name, name)) return true;
    return false;
}

/// Opens the list of sessions. `kind` says what choosing one does.
fn openChooser(ws: *Workspace, kind: PromptKind, label: []const u8, skip_current: bool) void {
    var scratch: std.heap.ArenaAllocator = .init(ws.gpa);
    defer scratch.deinit();
    const a = scratch.allocator();
    var store = store_mod.Store.open(ws.gpa, ws.io) orelse return ws.setStatus("No data folder for sessions", .{});
    defer store.deinit();
    const saved = store.list(a) catch &.{};
    const running = session.running(ws.gpa, ws.io, a);
    const label_copy = ws.gpa.dupe(u8, label) catch return;
    ws.openList(.list, label_copy, @intFromEnum(kind), &.{});
    const o = ws.overlay orelse return;
    if (kind == .switch_session) o.input.placeholder = "choose a session, or type a name to create one";
    const now = store_mod.nowSeconds();
    const current = ws.session_name orelse "";
    var n: usize = 0;
    for (saved) |info| {
        if (skip_current and std.mem.eql(u8, info.name, current)) continue;
        addItem(o, a, info.name, info.folder, info.tabs, info.last_used, now, isRunning(running, info.name), std.mem.eql(u8, info.name, current), &n);
    }
    // A running session that has not saved yet (just started).
    for (running) |r| {
        if (skip_current and std.mem.eql(u8, r.name, current)) continue;
        const known = for (saved) |s| {
            if (std.mem.eql(u8, s.name, r.name)) break true;
        } else false;
        if (!known) addItem(o, a, r.name, null, 0, 0, now, true, std.mem.eql(u8, r.name, current), &n);
    }
}

fn addItem(o: *quick_panel.QuickPanel, scratch: std.mem.Allocator, name: []const u8, folder: ?[]const u8, tabs: usize, last_used: i64, now: i64, is_running: bool, is_current: bool, n: *usize) void {
    var ago_buf: [32]u8 = undefined;
    const when = ago(&ago_buf, now, last_used);
    const state: []const u8 = if (is_current) "this window" else if (is_running) "running" else "saved";
    const detail = std.fmt.allocPrint(scratch, "{s} · {d} {s} · {s}{s}{s}", .{ state, tabs, if (tabs == 1) "file" else "files", when, if (folder != null) " · " else "", folder orelse "" }) catch return;
    // Items keep pointers: they must live in the panel's arena.
    const item: quick_panel.Item = .{
        .title = o.arena().dupe(u8, name) catch return,
        .detail = o.arena().dupe(u8, detail) catch return,
        .payload = .{ .value = n.* },
    };
    o.addItem(item) catch return;
    n.* += 1;
}

pub fn promptSwitch(ws: *Workspace) void {
    openChooser(ws, .switch_session, "Switch Session:", false);
}

pub fn promptNew(ws: *Workspace) void {
    ws.openPrompt("New Session (name):", @intFromEnum(PromptKind.new_session), "");
}

pub fn promptRename(ws: *Workspace) void {
    const current = ws.session_name orelse return ws.setStatus("This window is not in a saved session", .{});
    ws.openPrompt("Rename Session (new name):", @intFromEnum(PromptKind.rename_session), current);
}

pub fn promptDelete(ws: *Workspace) void {
    openChooser(ws, .delete_session, "Delete Session:", true);
}

/// Starts (or shows) session `name`'s process and closes this window.
pub fn launch(ws: *Workspace, name: []const u8) void {
    if (!store_mod.validName(name)) return ws.setStatus("\"{s}\" is not a session name (letters, digits, . _ -)", .{name});
    if (ws.session_name) |cur| if (std.mem.eql(u8, cur, name)) return ws.setStatus("Already in session {s}", .{name});
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = std.process.executablePath(ws.io, &exe_buf) catch return ws.setStatus("Cannot find the cs-zig executable", .{});
    _ = std.process.spawn(ws.io, .{
        .argv = &.{ exe_buf[0..n], "--session", name },
        .stdin = .ignore,
        .stdout = .ignore,
        .stderr = .ignore,
    }) catch |err| return ws.setStatus("Cannot start session {s}: {t}", .{ name, err });
    ws.requestQuit();
}

/// Quick panel lists; false if the tag isn't ours.
pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item, query: []const u8) bool {
    const kind = std.enums.fromInt(PromptKind, tag) orelse return false;
    switch (kind) {
        .switch_session => {
            const typed = std.mem.trim(u8, query, " ");
            const name = if (item) |it| it.title else typed;
            if (name.len > 0) launch(ws, name);
        },
        .delete_session => if (item) |it| {
            var label_buf: [128]u8 = undefined;
            const label = std.fmt.bufPrint(&label_buf, "Delete session {s} and its unsaved-file backups?", .{it.title}) catch "Delete session?";
            ws.openList(.list, ws.gpa.dupe(u8, label) catch return true, @intFromEnum(PromptKind.confirm_delete), &.{});
            const o = ws.overlay orelse return true;
            o.addItem(.{ .title = o.arena().dupe(u8, "Delete") catch return true, .detail = o.arena().dupe(u8, it.title) catch return true, .payload = .{ .value = 0 } }) catch {};
            o.addItem(.{ .title = "Cancel", .payload = .{ .value = 1 } }) catch {};
        },
        .confirm_delete => if (item) |it| if (std.mem.eql(u8, it.title, "Delete")) deleteSaved(ws, it.detail),
        .new_session, .rename_session => return false,
    }
    return true;
}

/// Prompts; false if the tag isn't ours.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    const kind = std.enums.fromInt(PromptKind, tag) orelse return false;
    const name = std.mem.trim(u8, input, " ");
    switch (kind) {
        .new_session => {
            var store = store_mod.Store.open(ws.gpa, ws.io) orelse return true;
            defer store.deinit();
            if (store_mod.validName(name) and store.exists(name)) {
                ws.setStatus("Session {s} exists: use Switch Session", .{name});
                return true;
            }
            launch(ws, name);
        },
        .rename_session => requestRename(ws, name),
        else => return false,
    }
    return true;
}

fn requestRename(ws: *Workspace, name: []const u8) void {
    const cur = ws.session_name orelse return;
    if (std.mem.eql(u8, cur, name)) return;
    if (!store_mod.validName(name)) return ws.setStatus("\"{s}\" is not a session name (letters, digits, . _ -)", .{name});
    var store = store_mod.Store.open(ws.gpa, ws.io) orelse return;
    defer store.deinit();
    if (store.exists(name)) return ws.setStatus("A session named {s} exists", .{name});
    if (ws.session_rename_to) |old| ws.gpa.free(old);
    ws.session_rename_to = ws.gpa.dupe(u8, name) catch null;
}

fn deleteSaved(ws: *Workspace, name: []const u8) void {
    var scratch: std.heap.ArenaAllocator = .init(ws.gpa);
    defer scratch.deinit();
    if (isRunning(session.running(ws.gpa, ws.io, scratch.allocator()), name)) {
        return ws.setStatus("Session {s} is running: quit it first", .{name});
    }
    var store = store_mod.Store.open(ws.gpa, ws.io) orelse return;
    defer store.deinit();
    store.remove(name) catch |err| return ws.setStatus("Cannot delete session {s}: {t}", .{ name, err });
    ws.setStatus("Deleted session {s}", .{name});
}
