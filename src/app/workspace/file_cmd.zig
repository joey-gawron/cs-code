//! Buffer and file commands, the leader's SPC b and SPC f (palette entries
//! too): a list of the open tabs to switch to, the scratch buffer, copying
//! a file's path or text, renaming and deleting the file (through vfs, so
//! ssh:// files work too), and opening it or a URL with the desktop's
//! handler.

const std = @import("std");
const builtin = @import("builtin");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const quick_panel = @import("quick_panel.zig");
const path_complete = @import("path_complete.zig");
const languages = @import("../../model/syntax/languages.zig");
const vfs = @import("../../model/remote/vfs.zig");
const rpath = @import("../../model/remote/path.zig");

const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const QuickPanel = quick_panel.QuickPanel;

pub const PromptKind = enum(u32) { switch_buffer = 600, rename, confirm_delete };

pub const State = struct {
    /// The buffer (Buffer.id) a rename or delete prompt is about: the active
    /// view could change before it is answered.
    target: u32 = 0,
};

/// SPC , / SPC b b: every tab (files, terminals, the agenda), most recently
/// used first and the active one last, so Enter goes back to the previous.
pub fn switchBuffer(ws: *Workspace) void {
    const current = ws.activeEditor();
    const o = ws.newOverlay(.list) orelse return;
    o.prompt_label = "Switch to Tab";
    o.prompt_tag = @intFromEnum(PromptKind.switch_buffer);
    o.input.placeholder = "Filter";
    for (ws.mru.items) |ed| if (ed != current) addTab(ws, o, ed);
    if (current) |ed| addTab(ws, o, ed);
}

fn addTab(ws: *Workspace, o: *QuickPanel, ed: *Editor) void {
    const tab = ws.tabs.items[ws.tabIndexOf(ed) orelse return];
    const detail = if (tab.terminal != null) "terminal" else if (tab.agenda != null) "agenda" else if (ed.buffer.path) |p| blk: {
        const project = ws.project orelse break :blk p;
        break :blk project.relativePath(p) orelse p;
    } else "";
    o.addItem(.{
        .title = o.arena().dupe(u8, Workspace.tabTitle(ed)) catch return,
        .detail = o.arena().dupe(u8, detail) catch "",
        .payload = .{ .value = @intFromPtr(ed) },
    }) catch {};
}

/// SPC x: the scratch buffer, made on first use: text that never asks to be
/// saved (Emacs's *scratch*) until Save As makes it a file.
pub fn scratchBuffer(ws: *Workspace) void {
    for (ws.tabs.items, 0..) |t, i| if (t.editor.buffer.scratch) return ws.activateTab(i);
    const ed = ws.newFile() catch return;
    ed.buffer.scratch = true;
    ed.buffer.display_name = "*scratch*";
}

/// SPC b y: the whole text to the clipboard.
pub fn yankBuffer(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    const text = ed.buffer.rope.toOwnedSlice(ws.gpa) catch return;
    defer ws.gpa.free(text);
    ws.env.clipboard.set(ws.env.clipboard.ctx, text);
    ws.setStatus("Copied {d} lines", .{ed.buffer.lineCount()});
}

/// SPC f y / f Y: the file's path (an ssh:// one as it is), or its path in
/// the project, to the clipboard.
pub fn copyPath(ws: *Workspace, in_project: bool) void {
    const path = activePath(ws) orelse return;
    const text = if (!in_project) path else if (ws.project) |p| p.relativePath(path) orelse path else path;
    ws.env.clipboard.set(ws.env.clipboard.ctx, text);
    ws.setStatus("Copied {s}", .{text});
}

/// SPC f R: asks for the file's new path (with path completion).
pub fn promptRename(ws: *Workspace) void {
    const path = activePath(ws) orelse return;
    ws.file_cmd.target = ws.activeEditor().?.buffer.id;
    ws.openPrompt("Rename to:", @intFromEnum(PromptKind.rename), path);
    path_complete.begin(ws, .{});
}

/// SPC f D: asks, then deletes the file.
pub fn confirmDelete(ws: *Workspace) void {
    const path = activePath(ws) orelse return;
    ws.file_cmd.target = ws.activeEditor().?.buffer.id;
    const o = ws.newOverlay(.list) orelse return;
    o.prompt_label = std.fmt.allocPrint(o.arena(), "Delete {s}?", .{std.fs.path.basename(path)}) catch "Delete this file?";
    o.prompt_tag = @intFromEnum(PromptKind.confirm_delete);
    for ([_][]const u8{ "Delete", "Cancel" }, 0..) |choice, i| o.addItem(.{ .title = choice, .payload = .{ .value = i } }) catch {};
}

/// SPC o b: the file in the desktop's handler for it (a browser for HTML).
pub fn openInBrowser(ws: *Workspace) void {
    const path = activePath(ws) orelse return;
    if (rpath.isRemote(path)) {
        ws.setStatus("Can't open a file on another machine here", .{});
        return;
    }
    openExternally(ws, path);
}

/// The desktop's "open this with its handler" command.
const open_command = if (builtin.os.tag.isDarwin()) "open" else "xdg-open";

/// Opens `target` (a URL or a local file) with xdg-open (open on macOS),
/// without waiting for it: a small thread of its own reaps it.
pub fn openExternally(ws: *Workspace, target: []const u8) void {
    var child = std.process.spawn(ws.io, .{ .argv = &.{ open_command, target }, .stdout = .ignore, .stderr = .ignore }) catch {
        ws.setStatus("Unable to open {s}", .{target});
        return;
    };
    if (std.Thread.spawn(.{}, reap, .{ ws.io, child })) |t| t.detach() else |_| _ = child.wait(ws.io) catch {};
    ws.setStatus("Opening {s}", .{target});
}

/// Shows `path` in the desktop's file manager: selected in Finder on macOS,
/// or its folder (the folder itself, for a folder) opened elsewhere.
pub fn revealInOs(ws: *Workspace, path: []const u8, is_dir: bool) void {
    if (rpath.isRemote(path)) {
        ws.setStatus("Can't show a file on another machine here", .{});
        return;
    }
    if (builtin.os.tag.isDarwin()) {
        var child = std.process.spawn(ws.io, .{ .argv = &.{ "open", "-R", path }, .stdout = .ignore, .stderr = .ignore }) catch {
            ws.setStatus("Unable to reveal {s}", .{path});
            return;
        };
        if (std.Thread.spawn(.{}, reap, .{ ws.io, child })) |t| t.detach() else |_| _ = child.wait(ws.io) catch {};
        return;
    }
    openExternally(ws, if (is_dir) path else std.fs.path.dirname(path) orelse path);
}

/// Where the shell commands go: on PATH on every Mac, and what VS Code and
/// Sublime Text use.
const shell_command_dir = "/usr/local/bin";

/// macOS app: links `je` and `cs-zig` in /usr/local/bin to the launcher
/// in the bundle (Contents/Resources/bin/cs-zig, from make-dmg.sh),
/// asking for an administrator's password if the directory isn't writable.
pub fn installShellCommand(ws: *Workspace) void {
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = std.process.executableDirPath(ws.io, &exe_buf) catch 0;
    var launcher_buf: [std.fs.max_path_bytes]u8 = undefined;
    const launcher = std.fmt.bufPrint(&launcher_buf, "{s}/../Resources/bin/cs-zig", .{exe_buf[0..n]}) catch return;
    std.Io.Dir.cwd().access(ws.io, launcher, .{}) catch {
        ws.setStatus("Only the app (CS Zig.app) can install its shell commands", .{});
        return;
    };
    // Resolve the bundle's path so the links survive the app being run
    // from somewhere else in the meantime.
    var real_buf: [std.fs.max_path_bytes]u8 = undefined;
    const real_n = std.Io.Dir.cwd().realPathFile(ws.io, launcher, &real_buf) catch {
        ws.setStatus("Unable to find the app's launcher", .{});
        return;
    };
    const real = real_buf[0..real_n];
    // The path goes inside single quotes in a shell script, and that inside
    // double quotes in AppleScript.
    if (std.mem.indexOfAny(u8, real, "'\"\\") != null) {
        ws.setStatus("Can't install from a path with a quote in it: {s}", .{real});
        return;
    }

    var script_buf: [3 * std.fs.max_path_bytes]u8 = undefined;
    const script = std.fmt.bufPrint(&script_buf, "mkdir -p " ++ shell_command_dir ++ " && ln -sfn '{s}' " ++ shell_command_dir ++ "/cs-zig && ln -sfn '{s}' " ++ shell_command_dir ++ "/je", .{ real, real }) catch return;
    const writable = if (std.Io.Dir.cwd().access(ws.io, shell_command_dir, .{ .write = true })) |_| true else |_| false;
    if (writable) {
        var child = std.process.spawn(ws.io, .{ .argv = &.{ "/bin/sh", "-c", script }, .stdout = .ignore, .stderr = .ignore }) catch {
            ws.setStatus("Unable to run /bin/sh", .{});
            return;
        };
        const term = child.wait(ws.io) catch {
            ws.setStatus("Unable to install the shell commands", .{});
            return;
        };
        if (term == .exited and term.exited == 0) {
            ws.setStatus("Installed je and cs-zig in " ++ shell_command_dir, .{});
        } else {
            ws.setStatus("Unable to install the shell commands in " ++ shell_command_dir, .{});
        }
        return;
    }
    // AppleScript asks for the password.
    var osa_buf: [4 * std.fs.max_path_bytes]u8 = undefined;
    const osa = std.fmt.bufPrint(&osa_buf, "do shell script \"{s}\" with administrator privileges", .{script}) catch return;
    var child = std.process.spawn(ws.io, .{ .argv = &.{ "/usr/bin/osascript", "-e", osa }, .stdout = .ignore, .stderr = .ignore }) catch {
        ws.setStatus("Unable to run osascript", .{});
        return;
    };
    if (std.Thread.spawn(.{}, reap, .{ ws.io, child })) |t| t.detach() else |_| _ = child.wait(ws.io) catch {};
    ws.setStatus("Installing je and cs-zig in " ++ shell_command_dir ++ " (asks for an administrator)", .{});
}

fn reap(io: std.Io, child: std.process.Child) void {
    var c = child;
    _ = c.wait(io) catch {};
}

/// The active view's file, or null after saying it has none.
fn activePath(ws: *Workspace) ?[]const u8 {
    const ed = ws.activeEditor() orelse return null;
    return ed.buffer.path orelse {
        ws.setStatus("{s} isn't a file", .{Workspace.tabTitle(ed)});
        return null;
    };
}

/// The buffer a prompt asked about, if it is still open and a file.
fn promptedBuffer(ws: *Workspace) ?*Buffer {
    for (ws.buffers.items) |b| if (b.id == ws.file_cmd.target and b.path != null) return b;
    return null;
}

/// Prompts; false if the tag isn't ours.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (tag != @intFromEnum(PromptKind.rename)) return false;
    const typed = std.mem.trim(u8, input, " ");
    if (typed.len == 0) return true;
    const buffer = promptedBuffer(ws) orelse return true;
    const to = ws.resolvePath(typed) catch return true;
    defer ws.gpa.free(to);
    rename(ws, buffer, to);
    return true;
}

/// Quick panel lists; false if the tag isn't ours.
pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item) bool {
    const switching = tag == @intFromEnum(PromptKind.switch_buffer);
    if (!switching and tag != @intFromEnum(PromptKind.confirm_delete)) return false;
    const choice = (item orelse return true).payload.value;
    if (switching) {
        for (ws.tabs.items, 0..) |t, i| if (@intFromPtr(t.editor) == choice) {
            ws.activateTab(i);
            break;
        };
    } else if (choice == 0) if (promptedBuffer(ws)) |b| delete(ws, b);
    return true;
}

/// Moves the file (never over another one); its views follow: tab titles,
/// syntax and the language server go by the new name.
fn rename(ws: *Workspace, buffer: *Buffer, to: []const u8) void {
    const from = buffer.path.?;
    if (std.mem.eql(u8, from, to)) return;
    if (vfs.stat(ws.io, to)) |_| {
        ws.setStatus("{s} already exists", .{to});
        return;
    } else |_| {}
    if (std.fs.path.dirname(to)) |dir| vfs.makeDir(ws.io, dir) catch {};
    vfs.rename(ws.io, from, to) catch |err| {
        if (err == error.CrossMachineRename) {
            ws.setStatus("Can't move a file to another machine", .{});
        } else ws.setStatus("Unable to rename {s}: {t}", .{ from, err });
        return;
    };
    if (ws.lsp) |l| l.closeBuffer(buffer);
    buffer.setPath(to) catch return;
    buffer.setLanguage(ws.registry, languages.forPath(to));
    if (ws.lsp) |l| l.openBuffer(buffer);
    ws.updateTitle();
    if (ws.project) |p| p.scan();
    ws.setStatus("Renamed to {s}", .{to});
}

/// Deletes the file and closes its views (without asking: it is gone).
fn delete(ws: *Workspace, buffer: *Buffer) void {
    const path = buffer.path.?;
    vfs.delete(ws.io, path) catch |err| {
        ws.setStatus("Unable to delete {s}: {t}", .{ path, err });
        return;
    };
    ws.setStatus("Deleted {s}", .{std.fs.path.basename(path)});
    // Closing the last view frees the buffer, and `path` with it.
    const gone = ws.gpa.dupe(u8, path) catch null;
    defer if (gone) |g| ws.gpa.free(g);
    var i = ws.tabs.items.len;
    while (i > 0) {
        i -= 1;
        if (ws.tabs.items[i].editor.buffer == buffer) ws.closeTabNow(i);
    }
    // Reopen Closed File (ctrl+shift+t) skips it: the file is gone.
    if (gone) |g| ws.closed_paths.forget(ws.gpa, g);
    if (ws.project) |p| p.scan();
}
