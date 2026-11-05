//! Path completion in the prompts that take a file or folder (File ▸ Open
//! File…, Open Folder…, Save As…, Remote: Open Folder…), like a shell's:
//! the list under the input shows the folder typed so far (through its last
//! '/'; `~/` is home, a relative path is under the project root) filtered
//! by the name after it, names starting with it first, then fuzzy matches;
//! dotfiles once the name starts with '.'. Tab or a click completes the
//! selected entry (a folder gets its '/' and is listed in turn),
//! ctrl+backspace deletes back to the previous '/', and Enter takes the
//! typed path, or the entry picked with the arrow keys. In a prompt for a
//! file, a folder is listed instead of confirmed. As in Emacs, typing `~/`
//! or `/` right after a '/' starts over from home or the root.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const quick_panel = @import("quick_panel.zig");
const vfs = @import("../../model/remote/vfs.zig");
const rpath = @import("../../model/remote/path.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const QuickPanel = quick_panel.QuickPanel;

/// Big folders (/nix/store) are cut off here.
const max_entries = 20000;
/// Symlinks are followed (to tell folders) up to this many per listing.
const max_link_stats = 256;

pub const Options = struct {
    /// Only folders are listed and completed (Open Folder).
    dirs_only: bool = false,
    /// The prompt takes paths on this host (Remote: Open Folder).
    host: ?[]const u8 = null,
};

/// Makes the open prompt a path prompt and lists its initial text's folder.
pub fn begin(ws: *Workspace, opts: Options) void {
    const o = ws.overlay orelse return;
    const gpa = ws.gpa;
    var cwd_buf: [std.fs.max_path_bytes]u8 = @splat(0); // See Workspace.realPath.
    const base = if (ws.project) |p| p.root else if (std.process.currentPath(ws.io, &cwd_buf)) |n| cwd_buf[0..n] else |_| "/";
    o.path_mode = .{
        .dirs_only = opts.dirs_only,
        .host = if (opts.host) |h| gpa.dupe(u8, h) catch null else null,
        .base = gpa.dupe(u8, base) catch &.{},
    };
    refresh(ws, o);
}

/// The folder part of `typed` (through its last '/') and the name after it.
fn split(typed: []const u8) struct { dir: []const u8, name: []const u8 } {
    const slash = std.mem.lastIndexOfScalar(u8, typed, '/') orelse return .{ .dir = "", .name = typed };
    return .{ .dir = typed[0 .. slash + 1], .name = typed[slash + 1 ..] };
}

/// Where `typed` (a folder or file as typed in the prompt) is.
fn resolve(alloc: Allocator, pm: *const quick_panel.PathMode, typed: []const u8) ![]const u8 {
    if (pm.host) |h| return rpath.format(alloc, h, if (typed.len == 0) "/" else typed);
    if (rpath.isRemote(typed) or std.fs.path.isAbsolute(typed)) return typed;
    if (std.mem.eql(u8, typed, "~") or std.mem.startsWith(u8, typed, "~/")) {
        const home = std.mem.span(std.c.getenv("HOME") orelse return error.NoHome);
        return std.mem.concat(alloc, u8, &.{ home, typed[1..] });
    }
    return std.fs.path.join(alloc, &.{ pm.base, typed });
}

fn contents(o: *QuickPanel, gpa: Allocator) ?[]u8 {
    return o.input.contents(gpa) catch null;
}

/// Lists the typed folder into the panel's items if it changed, then refilters.
pub fn refresh(ws: *Workspace, o: *QuickPanel) void {
    list(ws, o);
    _ = o.refilter();
}

/// Lists the typed folder's entries into the panel's items (when the
/// folder changed). A folder that can't be listed gives no entries.
pub fn list(ws: *Workspace, o: *QuickPanel) void {
    const pm = if (o.path_mode) |*p| p else return;
    const gpa = ws.gpa;
    const typed = contents(o, gpa) orelse return;
    defer gpa.free(typed);
    const dir = split(typed).dir;
    if (pm.listed) |l| if (std.mem.eql(u8, l, dir)) return;
    if (pm.listed) |l| gpa.free(l);
    pm.listed = gpa.dupe(u8, dir) catch null;
    o.items.clearRetainingCapacity();
    o.last_version = std.math.maxInt(u64);
    if (pm.names) |*a| _ = a.reset(.retain_capacity) else pm.names = .init(gpa);
    const names = pm.names.?.allocator();

    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const path = resolve(alloc, pm, dir) catch return;
    const entries = vfs.list(alloc, ws.io, path) catch return;
    var link_stats: usize = 0;
    for (entries[0..@min(entries.len, max_entries)]) |e| {
        var is_dir = e.kind == .directory;
        if (e.kind == .sym_link and link_stats < max_link_stats) {
            link_stats += 1;
            const target = std.fs.path.join(alloc, &.{ path, e.name }) catch continue;
            is_dir = if (vfs.stat(ws.io, target)) |st| st.kind == .directory else |_| false;
        }
        if (pm.dirs_only and !is_dir) continue;
        const title = std.fmt.allocPrint(names, "{s}{s}", .{ e.name, if (is_dir) "/" else "" }) catch continue;
        o.addItem(.{ .title = title, .payload = .{ .path_entry = is_dir } }) catch break;
    }
}

/// After an edit: "…/~/" becomes "~/" and "…//" becomes "/" (with the
/// caret at the end), so a new path can be typed over the prefilled one.
pub fn tidy(ws: *Workspace, o: *QuickPanel) void {
    const pm = if (o.path_mode) |*p| p else return;
    const gpa = ws.gpa;
    const typed = contents(o, gpa) orelse return;
    defer gpa.free(typed);
    const sel = o.input.primarySelection();
    if (o.input.selections.items.len != 1 or !sel.isEmpty() or sel.head != typed.len) return;
    if (typed.len > 2 and std.mem.endsWith(u8, typed, "//")) {
        o.input.setText("/", false);
    } else if (typed.len > 3 and std.mem.endsWith(u8, typed, "/~/") and pm.host == null) {
        o.input.setText("~/", false);
    }
}

fn setInput(ws: *Workspace, o: *QuickPanel, text: []const u8) void {
    o.input.setText(text, false);
    refresh(ws, o);
}

/// Tab: completes the selected entry. Returns false if there is none.
pub fn complete(ws: *Workspace, o: *QuickPanel) bool {
    const item = o.selectedItem() orelse return false;
    const gpa = ws.gpa;
    const typed = contents(o, gpa) orelse return false;
    defer gpa.free(typed);
    const text = std.mem.concat(gpa, u8, &.{ split(typed).dir, item.title }) catch return false;
    defer gpa.free(text);
    setInput(ws, o, text);
    return true;
}

/// ctrl+backspace with the caret at the end: deletes back to the previous
/// '/' ("/a/b/c" and "/a/b/c/" become "/a/b/"). Returns false otherwise.
pub fn deleteComponent(ws: *Workspace, o: *QuickPanel) bool {
    const gpa = ws.gpa;
    const typed = contents(o, gpa) orelse return false;
    defer gpa.free(typed);
    const sel = o.input.primarySelection();
    if (o.input.selections.items.len != 1 or !sel.isEmpty() or sel.head != typed.len or typed.len == 0) return false;
    const end = if (typed[typed.len - 1] == '/') typed.len - 1 else typed.len;
    const cut = if (std.mem.lastIndexOfScalar(u8, typed[0..end], '/')) |i| i + 1 else 0;
    setInput(ws, o, typed[0..cut]);
    return true;
}

/// Enter (or a click on an entry): an entry picked with the arrow keys or
/// the mouse replaces the typed name, and in a prompt for a file a folder
/// is listed instead. Returns true if the prompt stays open.
pub fn confirm(ws: *Workspace, o: *QuickPanel) bool {
    const pm = if (o.path_mode) |*p| p else return false;
    const gpa = ws.gpa;
    var text = contents(o, gpa) orelse return false;
    defer gpa.free(text);
    if (pm.picked) if (o.selectedItem()) |item| {
        const picked = std.mem.concat(gpa, u8, &.{ split(text).dir, item.title }) catch return false;
        gpa.free(text);
        text = picked;
        o.input.setText(text, false);
    };
    if (pm.dirs_only) return false;
    const trimmed = std.mem.trim(u8, text, " ");
    if (trimmed.len == 0) return false;
    const is_dir = trimmed[trimmed.len - 1] == '/' or blk: {
        var arena = std.heap.ArenaAllocator.init(gpa);
        defer arena.deinit();
        const path = resolve(arena.allocator(), pm, trimmed) catch break :blk false;
        const st = vfs.stat(ws.io, path) catch break :blk false;
        break :blk st.kind == .directory;
    };
    if (!is_dir) return false;
    if (trimmed[trimmed.len - 1] == '/') {
        refresh(ws, o);
    } else {
        const with_slash = std.mem.concat(gpa, u8, &.{ trimmed, "/" }) catch return true;
        defer gpa.free(with_slash);
        setInput(ws, o, with_slash);
    }
    return true;
}

test "a typed path splits into its folder and the name being typed" {
    const a = split("/home/me/src/ma");
    try std.testing.expectEqualStrings("/home/me/src/", a.dir);
    try std.testing.expectEqualStrings("ma", a.name);
    const b = split("~/");
    try std.testing.expectEqualStrings("~/", b.dir);
    try std.testing.expectEqualStrings("", b.name);
    const c = split("build");
    try std.testing.expectEqualStrings("", c.dir);
    try std.testing.expectEqualStrings("build", c.name);
}
