//! The panel under the editor groups, like VS Code's: it spans the editor
//! area above the status bar, its top edge drags to resize it, and its ×,
//! Escape (Sublime's hide_panel; in the log view) or View ▸ Panel
//! (ctrl+shift+y) close it. Its header switches between three views.
//!
//! Terminal: shells and commands on ptys (terminal_ui.zig does their keys
//! and output, as for terminal tabs). ctrl+` shows, focuses and hides it,
//! ctrl+shift+` adds a shell, and with two or more a list on the right
//! picks one. Terminal: Run Command… (ctrl+shift+b) runs a shell command
//! line (`$SHELL -c`) in the project root, keeping its output and exit
//! code; Run Last Command (ctrl+b) runs it again, in the same terminal
//! once that one has finished. A shell that exits cleanly closes; anything
//! else stays until a key is pressed.
//!
//! Source Control: a magit-style status of the repository, with popups for
//! committing, pushing, branches and more (git_status_ui.zig,
//! git_popup.zig; Git: Show Status, ctrl+shift+g; ? lists the keys).
//!
//! Language Servers: on the left the servers that were started or failed
//! to start, with their state and work-done progress; on the right their
//! log (`Lsp.log`: starts, exits, failed requests, window/logMessage and
//! stderr) in a read-only view that can be scrolled, selected and copied,
//! and that follows new output while it is scrolled to the end. Picking a
//! server in the list narrows the log to it. The status bar's language
//! server item (the active file's server, with its state and progress)
//! opens and closes this view on that server.

const std = @import("std");
const c = @import("c");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const platform = @import("../../framework/platform/platform.zig");
const lsp_mod = @import("../../model/lsp/lsp.zig");
const ui = @import("../../framework/ui/ui.zig");
const Palette = @import("../terminal/palette.zig").Palette;
const TerminalView = @import("../terminal/terminal_view.zig").TerminalView;
const editor_view = @import("../editor/editor_view.zig");
const terminal_ui = @import("terminal_ui.zig");
const git_status_ui = @import("git_status_ui.zig");
const problems_ui = @import("problems_ui.zig");
const context_menu = @import("context_menu.zig");
const rpath = @import("../../model/remote/path.zig");
const pty = @import("../../framework/platform/pty.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Status = lsp_mod.Lsp.Status;
const Rect = ui.Rect;
const Color = ui.Color;
const Theme = ui.Theme;

const header_h: f32 = 30;
const min_h: f32 = 90;
/// The strip at the top edge that drags to resize.
const edge_h: f32 = 5;
const all_row_h: f32 = 28;
const row_h: f32 = 42;
const term_row_h: f32 = 26;

pub const View = enum { terminal, source_control, language_servers, problems };

/// A clickable part of the panel, from the last paint.
const Hit = union(enum) {
    close,
    view: View,
    clear,
    restart,
    refresh,
    git_help,
    /// A row of the server list (null: "All servers").
    server: ?[]const u8,
    new_terminal,
    run_command,
    kill_terminal,
    /// A row of the terminal list, and its ×.
    terminal: usize,
    close_terminal: usize,
};

/// A terminal in the panel.
pub const Term = struct {
    view: *TerminalView,
    /// The command line it runs (Run Command), or null for a shell.
    command: ?[]u8 = null,
};

pub const State = struct {
    visible: bool = false,
    view: View = .terminal,
    /// The height it was dragged to (0: a third of the window).
    height: f32 = 0,
    terminals: std.ArrayList(Term) = .empty,
    active_terminal: usize = 0,
    /// For Run Last Command (and the Run Command prompt).
    last_command: ?[]u8 = null,
    term_rect: Rect = .{},
    /// The Source Control view (made by Git: Show Status).
    source_control: ?*git_status_ui.Panel = null,
    scm_rect: Rect = .{},
    /// The log view and its buffer, made when the panel first shows.
    log: ?*Editor = null,
    buffer: ?*Buffer = null,
    /// The server whose log is shown (null: all). Config names live as
    /// long as the program.
    filter: ?[]const u8 = null,
    /// What the log view holds: entries up to `shown_seq` of `shown_epoch`
    /// for `shown_filter` (null epoch: nothing yet).
    shown_seq: u64 = 0,
    shown_epoch: ?u32 = null,
    shown_filter: ?[]const u8 = null,
    rect: Rect = .{},
    log_rect: Rect = .{},
    hits: std.ArrayList(struct { rect: Rect, hit: Hit }) = .empty,
    /// The status bar item from the last paint, and the server it names.
    status_item: Rect = .{},
    status_item_server: ?[]const u8 = null,
};

pub fn deinit(ws: *Workspace) void {
    const st = &ws.panel;
    for (st.terminals.items) |t| freeTerm(ws.gpa, t);
    st.terminals.deinit(ws.gpa);
    if (st.last_command) |cmd| ws.gpa.free(cmd);
    if (st.source_control) |p| p.destroy();
    if (st.log) |ed| ed.destroy();
    if (st.buffer) |b| {
        b.deinit();
        ws.gpa.destroy(b);
    }
    st.hits.deinit(ws.gpa);
}

fn sameName(a: ?[]const u8, b: ?[]const u8) bool {
    if (a == null or b == null) return a == null and b == null;
    return std.mem.eql(u8, a.?, b.?);
}

/// The log view, made on first use.
fn logEditor(ws: *Workspace) ?*Editor {
    const st = &ws.panel;
    if (st.log) |ed| return ed;
    const buffer = ws.gpa.create(Buffer) catch return null;
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return null;
    };
    ws.next_buffer_id += 1;
    buffer.display_name = "Language Servers";
    buffer.read_only = true;
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return null;
    };
    ed.plain = true;
    ed.focused = false;
    st.buffer = buffer;
    st.log = ed;
    return ed;
}

// ---- Showing and hiding ------------------------------------------------------------

/// Shows the panel on `view`; `focus` gives it the keyboard. The terminal
/// view starts a shell if it has none.
fn show(ws: *Workspace, view: View, focus: bool) void {
    const st = &ws.panel;
    // Git: Show Status makes the view and finds the repository.
    if (view == .source_control and st.source_control == null) return git_status_ui.open(ws);
    // The log view's editor is also the clock terminals blink their cursor by.
    _ = logEditor(ws) orelse return;
    st.view = view;
    st.visible = true;
    if (view == .terminal and st.terminals.items.len == 0) {
        if (!newTerminal(ws, .file_folder)) return;
    }
    if (focus) focusPanel(ws);
    ws.dirty = true;
}

/// The Source Control view (from git_status_ui.open), focused.
pub fn showSourceControl(ws: *Workspace) void {
    show(ws, .source_control, true);
}

/// Shows the log of `server` (null: all servers).
pub fn showLanguageServers(ws: *Workspace, server: ?[]const u8, focus: bool) void {
    ws.panel.filter = server;
    show(ws, .language_servers, focus);
}

/// Shows the Error List; `focus` gives it the keyboard.
pub fn showProblems(ws: *Workspace, focus: bool) void {
    show(ws, .problems, focus);
}

pub fn hide(ws: *Workspace) void {
    ws.panel.visible = false;
    if (ws.focus == .panel) ws.focus = .editor;
    if (ws.panel.log) |ed| ed.focused = false;
    ws.dirty = true;
}

fn focusPanel(ws: *Workspace) void {
    if (ws.activeEditor()) |ed| ed.focused = false;
    ws.focus = .panel;
    if (focusedEditor(ws)) |ed| {
        ed.focused = true;
        ed.touchCaret();
    }
}

/// The text view of the shown view: the Source Control list or the log
/// (null for terminals).
pub fn viewEditor(ws: *Workspace) ?*Editor {
    const st = &ws.panel;
    return switch (st.view) {
        .terminal => null,
        .source_control => if (st.source_control) |p| p.editor else null,
        .language_servers => st.log,
        .problems => null,
    };
}

/// What takes edit commands while the panel has focus: the view's text, or
/// for terminals the log view, whose caret clock their cursors blink by.
pub fn focusedEditor(ws: *Workspace) ?*Editor {
    return viewEditor(ws) orelse ws.panel.log;
}

/// Keys while the panel has focus (the Source Control view's). Returns
/// true if consumed.
pub fn handleKey(ws: *Workspace, k: platform.KeyEvent) bool {
    const st = &ws.panel;
    if (st.visible and st.view == .problems) return problems_ui.viewKey(ws, k);
    if (!st.visible or st.view != .source_control) return false;
    return git_status_ui.viewKey(ws, k);
}

/// View ▸ Panel.
pub fn toggle(ws: *Workspace) void {
    if (ws.panel.visible) hide(ws) else show(ws, ws.panel.view, true);
}

/// LSP: Show Language Server Status: the panel, on the active file's server.
pub fn showStatus(ws: *Workspace) void {
    showLanguageServers(ws, activeServer(ws), true);
}

/// ctrl+` (as in VS Code): shows and focuses the terminal view, or, from
/// there, hides the panel.
pub fn toggleTerminal(ws: *Workspace) void {
    const st = &ws.panel;
    if (st.visible and st.view == .terminal and ws.focus == .panel) return hide(ws);
    show(ws, .terminal, true);
}

fn activeServer(ws: *Workspace) ?[]const u8 {
    const l = ws.lsp orelse return null;
    const ed = ws.activeEditor() orelse return null;
    const s = l.statusFor(ed.buffer) orelse return null;
    return s.name;
}

// ---- Terminals ---------------------------------------------------------------------

pub const run_command_tag: u32 = 400;

fn freeTerm(gpa: Allocator, t: Term) void {
    t.view.destroy();
    if (t.command) |cmd| gpa.free(cmd);
}

pub fn terminals(ws: *Workspace) []Term {
    return ws.panel.terminals.items;
}

/// The terminal the terminal view shows.
pub fn activeTerminal(ws: *Workspace) ?*TerminalView {
    const st = &ws.panel;
    if (st.terminals.items.len == 0) return null;
    return st.terminals.items[@min(st.active_terminal, st.terminals.items.len - 1)].view;
}

/// The panel terminal that has the keyboard, if any.
pub fn focusedTerminal(ws: *Workspace) ?*TerminalView {
    const st = &ws.panel;
    if (ws.focus != .panel or !st.visible or st.view != .terminal) return null;
    return activeTerminal(ws);
}

fn indexOf(ws: *Workspace, tv: *TerminalView) ?usize {
    for (ws.panel.terminals.items, 0..) |t, i| if (t.view == tv) return i;
    return null;
}

/// Restarts the cursor blink (a key went to a panel terminal).
pub fn touch(ws: *Workspace) void {
    if (ws.panel.log) |ed| ed.touchCaret();
}

/// The size a new terminal starts at, before its first layout.
fn startRect(ws: *Workspace) Rect {
    const st = &ws.panel;
    if (st.term_rect.w >= 100 and st.term_rect.h >= 40) return st.term_rect;
    return .{ .w = @max(400, ws.editor_rect.w), .h = @max(120, height(ws, ws.size.y - 60) - header_h) };
}

/// Starts a shell in a new panel terminal and makes it the shown one.
fn newTerminal(ws: *Workspace, where: terminal_ui.Where) bool {
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir = terminal_ui.startDir(ws, where, &dir_buf);
    const tv = TerminalView.create(ws.gpa, ws.env, .{ .cwd = dir, .rect = startRect(ws) }) catch |err| {
        ws.setStatus("Unable to start a terminal: {t}", .{err});
        return false;
    };
    ws.panel.terminals.append(ws.gpa, .{ .view = tv }) catch {
        tv.destroy();
        return false;
    };
    ws.panel.active_terminal = ws.panel.terminals.items.len - 1;
    return true;
}

/// Terminal: New Terminal in Panel (ctrl+shift+`): in the current file's
/// folder, or the focused terminal's.
pub fn addTerminal(ws: *Workspace) void {
    if (!newTerminal(ws, .file_folder)) return;
    show(ws, .terminal, true);
}

/// Terminal: Run Command… asks for a command line (the last one, selected).
pub fn promptRunCommand(ws: *Workspace) void {
    ws.openPrompt("Run Command:", run_command_tag, ws.panel.last_command orelse "");
    if (ws.overlay) |o| o.input.selectAll();
}

/// The Run Command prompt's answer. Returns false for other prompts.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (tag != run_command_tag) return false;
    const cmd = std.mem.trim(u8, input, " \t");
    if (cmd.len > 0) runCommand(ws, cmd);
    return true;
}

/// Terminal: Run Last Command (asks for one if nothing ran yet).
pub fn rerunCommand(ws: *Workspace) void {
    if (ws.panel.last_command) |cmd| runCommand(ws, cmd) else promptRunCommand(ws);
}

/// Runs `cmd_in` with the shell (`-c`) in the project root (else the
/// current file's folder), in a terminal that replaces a finished run of
/// the same command or else is added.
pub fn runCommand(ws: *Workspace, cmd_in: []const u8) void {
    const st = &ws.panel;
    const gpa = ws.gpa;
    // `cmd_in` may be `last_command` itself.
    const cmd = gpa.dupe(u8, cmd_in) catch return;
    if (st.last_command) |old| gpa.free(old);
    st.last_command = gpa.dupe(u8, cmd) catch null;
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir = terminal_ui.startDir(ws, .project_root, &dir_buf);
    // The user's shell here; on a remote host, its sh.
    const shell = if (rpath.isRemote(dir)) "sh" else pty.defaultShell();
    const tv = TerminalView.create(gpa, ws.env, .{ .cwd = dir, .argv = &.{ shell, "-c", cmd }, .rect = startRect(ws) }) catch |err| {
        ws.setStatus("Unable to run {s}: {t}", .{ cmd, err });
        gpa.free(cmd);
        return;
    };
    tv.setTitle(cmd);
    // Where and what, above the output.
    if (std.fmt.allocPrint(gpa, "\x1b[90m{s}\x1b[0m\r\n\x1b[1m$ {s}\x1b[0m\r\n", .{ dir, cmd })) |banner| {
        defer gpa.free(banner);
        tv.term.screen.write(banner);
    } else |_| {}
    const term: Term = .{ .view = tv, .command = cmd };
    const reuse = for (st.terminals.items, 0..) |t, i| {
        if (t.command != null and t.view.finished() and std.mem.eql(u8, t.command.?, cmd)) break i;
    } else null;
    if (reuse) |i| {
        freeTerm(gpa, st.terminals.items[i]);
        st.terminals.items[i] = term;
        st.active_terminal = i;
    } else {
        st.terminals.append(gpa, term) catch return freeTerm(gpa, term);
        st.active_terminal = st.terminals.items.len - 1;
    }
    show(ws, .terminal, true);
}

/// Removes a panel terminal, killing its program; the last one's going
/// hides the terminal view.
pub fn closeTerminal(ws: *Workspace, tv: *TerminalView) void {
    const st = &ws.panel;
    const i = indexOf(ws, tv) orelse return;
    freeTerm(ws.gpa, st.terminals.orderedRemove(i));
    if (i < st.active_terminal) st.active_terminal -= 1;
    if (st.active_terminal >= st.terminals.items.len) st.active_terminal = st.terminals.items.len -| 1;
    if (st.terminals.items.len == 0 and st.view == .terminal) hide(ws);
    ws.dirty = true;
}

/// Exited cleanly (status 0).
fn succeeded(tv: *TerminalView) bool {
    const e = tv.term.exitStatus() orelse return false;
    return e == .code and e.code == 0;
}

/// Reads the panel terminals' output and handles programs that ended: a
/// shell that exits cleanly closes; a command, or a shell that failed,
/// stays with its exit status until a key is pressed.
pub fn service(ws: *Workspace) void {
    const st = &ws.panel;
    var i: usize = 0;
    while (i < st.terminals.items.len) {
        const t = st.terminals.items[i];
        const tv = t.view;
        i += 1;
        if (tv.exited) continue;
        if (tv.service()) ws.dirty = true;
        if (tv.title_changed) {
            tv.title_changed = false;
            ws.dirty = true;
        }
        if (!tv.finished()) continue;
        ws.dirty = true;
        const ok = succeeded(tv);
        if (t.command) |cmd| {
            tv.showExit();
            if (ok) {
                ws.setStatus("{s}: done", .{cmd});
            } else if (tv.term.exitStatus()) |e| switch (e) {
                .code => |code| ws.setStatus("{s}: exited with code {d}", .{ cmd, code }),
                .signal => |sig| ws.setStatus("{s}: killed by signal {d}", .{ cmd, sig }),
            } else ws.setStatus("{s}: exited", .{cmd});
        } else if (ok) {
            closeTerminal(ws, tv);
            i -= 1;
        } else {
            tv.showExit();
            ws.setStatus("{s}: process exited", .{tv.title()});
        }
    }
}

/// The panel's height for `avail` px between the menu bar and the status
/// bar, leaving the editors a tab bar and a few lines.
pub fn height(ws: *Workspace, avail: f32) f32 {
    const want = if (ws.panel.height > 0) ws.panel.height else @round(ws.size.y / 3);
    const max = @max(@min(min_h, avail * 0.5), avail - 120);
    return @round(std.math.clamp(want, @min(min_h, max), max));
}

// ---- Status text -------------------------------------------------------------------

/// The dot color for a server: green ready, blue busy, yellow starting,
/// red down (the scheme's own, as picked for terminals).
pub fn stateColor(theme: *const Theme, s: Status) Color {
    const ansi = Palette.fromTheme(theme).ansi;
    return switch (s.state) {
        .ready => if (s.server != null and s.server.?.currentProgress() != null) ansi[4] else ansi[2],
        .starting => ansi[3],
        .exited, .failed => ansi[1],
    };
}

fn notInstalled(s: Status) bool {
    const err = s.err orelse return false;
    return err == error.FileNotFound;
}

fn stateText(s: Status) []const u8 {
    return switch (s.state) {
        .starting => "starting…",
        .ready => if (s.server != null and s.server.?.currentProgress() != null) "busy" else "ready",
        .exited => "exited",
        .failed => if (notInstalled(s)) "not installed" else "failed",
    };
}

/// "Indexing 3/10 45%": a progress's title, message and percentage.
fn progressText(p: *const lsp_mod.Progress, buf: []u8) []const u8 {
    var w: std.Io.Writer = .fixed(buf);
    w.writeAll(p.title) catch {};
    if (p.message.len > 0) w.print("{s}{s}", .{ if (p.title.len > 0) " " else "", p.message }) catch {};
    if (p.percentage) |pct| w.print(" {d}%", .{pct}) catch {};
    return firstLine(w.buffered());
}

fn firstLine(text: []const u8) []const u8 {
    return text[0 .. std.mem.indexOfScalar(u8, text, '\n') orelse text.len];
}

/// The second line of a server's row: progress, why it failed, or what it is.
fn detailText(s: Status, buf: []u8) []const u8 {
    var w: std.Io.Writer = .fixed(buf);
    if (s.state == .failed and s.err != null) {
        if (notInstalled(s)) {
            const program = for (&lsp_mod.configs) |*cfg| {
                if (std.mem.eql(u8, cfg.name, s.name)) break cfg.argv[0];
            } else s.name;
            w.print("{s} not found on PATH", .{program}) catch {};
        } else w.print("{t}", .{s.err.?}) catch {};
        return w.buffered();
    }
    const server = s.server orelse return "";
    if (server.currentProgress()) |p| if (s.state == .ready) return progressText(p, buf);
    if (s.state == .failed) return "initialize failed (see the log)";
    // "v0.14.0 · pid 4242 · 3 files"
    var sep: []const u8 = "";
    const v = server.version();
    if (v.len > 0) {
        w.print("{s}{s}", .{ if (std.ascii.isDigit(v[0])) "v" else "", v }) catch {};
        sep = " · ";
    }
    if (server.pid()) |id| {
        w.print("{s}pid {d}", .{ sep, id }) catch {};
        sep = " · ";
    } else if (server.remote != null) {
        w.print("{s}remote", .{sep}) catch {};
        sep = " · ";
    }
    if (s.state != .exited) {
        const n = server.docCount();
        w.print("{s}{d} file{s}", .{ sep, n, if (n == 1) "" else "s" }) catch {};
    }
    return w.buffered();
}

// ---- The log -----------------------------------------------------------------------

/// "14:03:27.512" in local time.
fn clock(ms: i64, buf: *[16]u8) []const u8 {
    const t: c.time_t = @intCast(@divFloor(ms, 1000));
    var tm: c.struct_tm = undefined;
    if (c.localtime_r(&t, &tm) == null) return "--:--:--";
    return std.fmt.bufPrint(buf, "{d:0>2}:{d:0>2}:{d:0>2}.{d:0>3}", .{
        @as(u32, @intCast(tm.tm_hour)),
        @as(u32, @intCast(tm.tm_min)),
        @as(u32, @intCast(tm.tm_sec)),
        @as(u32, @intCast(@mod(ms, 1000))),
    }) catch "";
}

/// One entry: "[14:03:27.512] [zls] [error] text", continuation lines indented.
fn formatEntry(out: *std.ArrayList(u8), gpa: Allocator, e: lsp_mod.LogEntry, with_server: bool) !void {
    var tbuf: [16]u8 = undefined;
    try out.print(gpa, "[{s}] ", .{clock(e.time_ms, &tbuf)});
    if (with_server) if (e.server) |s| try out.print(gpa, "[{s}] ", .{s});
    try out.appendSlice(gpa, switch (e.level) {
        .err => "[error] ",
        .warning => "[warning] ",
        .stderr => "[stderr] ",
        .info, .log => "",
    });
    var it = std.mem.splitScalar(u8, e.text, '\n');
    var first = true;
    while (it.next()) |line| {
        if (!first) try out.appendSlice(gpa, "\n    ");
        first = false;
        try out.appendSlice(gpa, line);
    }
    try out.append(gpa, '\n');
}

/// Scroll that puts the end of the log at the bottom of the view.
fn endScroll(ed: *Editor) f32 {
    const rows: f32 = @floatFromInt(ed.displayRowCount());
    return @max(0, rows * ed.lineHeight() - ed.text_rect.h);
}

fn atEnd(ed: *Editor) bool {
    return ed.scroll_y >= endScroll(ed) - ed.lineHeight() / 2;
}

fn replaceText(ed: *Editor, text: []const u8) void {
    ed.buffer.read_only = false;
    ed.setText(text, false);
    ed.buffer.read_only = true;
    ed.buffer.clearHistory();
}

/// Adds `text` at the end, leaving selections where they are (a caret at
/// the end moves to the new end).
fn appendText(ed: *Editor, text: []const u8) void {
    if (text.len == 0) return;
    const end = ed.buffer.len();
    ed.buffer.read_only = false;
    ed.applying_edit = true;
    ed.buffer.edit(&.{.{ .start = end, .end = end, .text = text }}, .other, &.{}, &.{}) catch {};
    ed.applying_edit = false;
    ed.buffer.read_only = true;
    ed.buffer.clearHistory();
    for (ed.selections.items) |*sel| if (sel.anchor == end and sel.head == end) {
        sel.anchor = end + text.len;
        sel.head = end + text.len;
    };
}

/// Brings the log view up to date with `Lsp.log`: new entries are added at
/// the end; a different filter, or entries dropped or cleared, rebuild it.
fn refresh(ws: *Workspace, ed: *Editor) void {
    const st = &ws.panel;
    const l = ws.lsp orelse return;
    const full = st.shown_epoch == null or st.shown_epoch.? != l.log_epoch or !sameName(st.shown_filter, st.filter);
    if (!full and st.shown_seq + 1 >= l.next_log_seq) return;
    const follow = !sameName(st.shown_filter, st.filter) or atEnd(ed);
    const entries = l.log.items;
    // Sequence numbers are consecutive, so the new entries start here.
    const from = if (full or entries.len == 0) 0 else @min(entries.len, (st.shown_seq + 1) -| entries[0].seq);
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(ws.gpa);
    for (entries[from..]) |e| {
        // The client's own notes (restarts) belong to every server's log.
        if (st.filter) |f| if (e.server) |name| if (!std.mem.eql(u8, name, f)) continue;
        formatEntry(&text, ws.gpa, e, st.filter == null) catch break;
    }
    if (full) replaceText(ed, text.items) else appendText(ed, text.items);
    st.shown_seq = l.next_log_seq - 1;
    st.shown_epoch = l.log_epoch;
    st.shown_filter = st.filter;
    if (follow) {
        ed.scroll_y = endScroll(ed);
        ed.clampScroll();
    }
}

// ---- Painting ----------------------------------------------------------------------

fn addHit(ws: *Workspace, rect: Rect, hit: Hit) void {
    ws.panel.hits.append(ws.gpa, .{ .rect = rect, .hit = hit }) catch {};
}

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx) void {
    const st = &ws.panel;
    st.hits.clearRetainingCapacity();
    st.log_rect = .{};
    st.term_rect = .{};
    st.scm_rect = .{};
    if (!st.visible or st.rect.h <= 0) return;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const r = st.rect;
    scene.pushClip(r) catch return;
    defer scene.popClip();

    // Header: the views' tabs on the left, actions on the right.
    const header: Rect = .{ .x = r.x, .y = r.y, .w = r.w, .h = header_h };
    scene.fill(header, theme.tabbar_background);
    const tabs = [_]struct { label: []const u8, view: View }{
        .{ .label = "Error List", .view = .problems },
        .{ .label = "Terminal", .view = .terminal },
        .{ .label = "Source Control", .view = .source_control },
        .{ .label = "Language Servers", .view = .language_servers },
    };
    var tx = r.x + 14;
    for (tabs) |t| {
        const w = ctx.text.measure(t.label, ws.fonts.ui);
        const hit: Rect = .{ .x = tx - 8, .y = r.y, .w = w + 16, .h = header_h };
        const active = st.view == t.view;
        const fg = if (active or ctx.hovered(hit)) theme.tab_active_foreground else theme.tab_inactive_foreground;
        _ = ctx.label(.{ .x = tx, .y = r.y, .w = w, .h = header_h }, t.label, ws.fonts.ui, fg);
        if (active) scene.fill(.{ .x = tx - 8, .y = header.bottom() - 2, .w = w + 16, .h = 2 }, theme.input_border_focus);
        addHit(ws, hit, .{ .view = t.view });
        tx += w + 26;
    }

    var bx = header.right() - 6;
    const close: Rect = .{ .x = bx - 24, .y = header.y + 3, .w = 24, .h = header_h - 6 };
    if (ctx.hovered(close)) scene.quad(close, .{ .color = theme.tab_hover, .radius = 4 });
    ctx.closeIcon(close.x + close.w / 2, close.y + close.h / 2, 8, theme.tab_inactive_foreground);
    addHit(ws, close, .close);
    bx = close.x - 4;
    const Button = struct { label: []const u8, hit: Hit };
    const buttons: []const Button = switch (st.view) {
        .terminal => if (st.terminals.items.len > 0)
            &.{ .{ .label = "Kill", .hit = .kill_terminal }, .{ .label = "Run…", .hit = .run_command }, .{ .label = "+", .hit = .new_terminal } }
        else
            &.{ .{ .label = "Run…", .hit = .run_command }, .{ .label = "+", .hit = .new_terminal } },
        .source_control => &.{ .{ .label = "Refresh", .hit = .refresh }, .{ .label = "?", .hit = .git_help } },
        .language_servers => &.{ .{ .label = "Clear", .hit = .clear }, .{ .label = "Restart", .hit = .restart } },
        .problems => &.{},
    };
    for (buttons) |b| {
        const w = @max(28, ctx.text.measure(b.label, ws.fonts.ui_small) + 16);
        const br: Rect = .{ .x = bx - w, .y = header.y + 4, .w = w, .h = header_h - 8 };
        if (ctx.hovered(br)) scene.quad(br, .{ .color = theme.tab_hover, .radius = 4 });
        ctx.labelCentered(br, b.label, ws.fonts.ui_small, theme.tab_inactive_foreground);
        addHit(ws, br, b.hit);
        bx = br.x - 4;
    }
    // The top edge (also the resize handle).
    scene.fill(.{ .x = r.x, .y = r.y, .w = r.w, .h = 1 }, theme.tab_separator);

    const body: Rect = .{ .x = r.x, .y = header.bottom(), .w = r.w, .h = @max(0, r.h - header_h) };
    switch (st.view) {
        .problems => problems_ui.paint(ws, ctx, body),
        .terminal => paintTerminals(ws, ctx, body),
        .source_control => if (st.source_control) |p| {
            const ed = p.editor;
            ed.focused = ws.focus == .panel;
            ed.layout(body);
            ed.paint(scene, theme);
            git_status_ui.paintPopup(ws, ctx, body);
            st.scm_rect = body;
        },
        .language_servers => {
            const list_w = @round(std.math.clamp(r.w * 0.24, 170, 300));
            const list: Rect = .{ .x = body.x, .y = body.y, .w = @min(list_w, body.w), .h = body.h };
            scene.fill(list, theme.sidebar_background);
            scene.fill(.{ .x = list.right(), .y = body.y, .w = 1, .h = body.h }, theme.tab_separator);
            paintServers(ws, ctx, list);
            const log_rect: Rect = .{ .x = list.right() + 1, .y = body.y, .w = @max(0, body.right() - list.right() - 1), .h = body.h };
            paintLog(ws, ctx, log_rect);
        },
    }
}

/// The shown terminal, with the list of terminals on its right when there
/// are several.
fn paintTerminals(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const st = &ws.panel;
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.fill(body, theme.background);
    const tv = activeTerminal(ws) orelse {
        _ = ctx.labelFit(.{ .x = body.x + 16, .y = body.y + 8, .w = @max(0, body.w - 32), .h = 24 }, "No terminals. + starts a shell; Run… runs a command.", ws.fonts.ui, theme.list_foreground_dim);
        return;
    };
    var area = body;
    if (st.terminals.items.len >= 2) {
        const list_w = @round(std.math.clamp(body.w * 0.18, 150, 240));
        const list: Rect = .{ .x = body.right() - list_w, .y = body.y, .w = list_w, .h = body.h };
        area.w = @max(0, list.x - 1 - body.x);
        scene.fill(list, theme.sidebar_background);
        scene.fill(.{ .x = list.x - 1, .y = body.y, .w = 1, .h = body.h }, theme.tab_separator);
        paintTerminalList(ws, ctx, list);
    }
    const focused = ws.focus == .panel;
    if (st.log) |ed| ed.focused = focused;
    tv.layout(area);
    tv.paint(scene, theme, focused, if (st.log) |ed| editor_view.caretVisible(ed) else focused);
    st.term_rect = area;
}

/// A terminal's dot: dim while a shell runs, blue while a command does,
/// then green or red by how it ended.
fn termColor(theme: *const Theme, t: Term) Color {
    const ansi = Palette.fromTheme(theme).ansi;
    if (!t.view.finished()) return if (t.command != null) ansi[4] else theme.sidebar_foreground_dim;
    return if (succeeded(t.view)) ansi[2] else ansi[1];
}

fn paintTerminalList(ws: *Workspace, ctx: *const ui.Ctx, list: Rect) void {
    const st = &ws.panel;
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.pushClip(list) catch return;
    defer scene.popClip();
    var y = list.y + 4;
    for (st.terminals.items, 0..) |t, i| {
        if (y > list.bottom()) break;
        const row: Rect = .{ .x = list.x, .y = y, .w = list.w, .h = term_row_h };
        const active = i == st.active_terminal;
        const hovered = ctx.hovered(row);
        if (active) scene.fill(row, theme.sidebar_selected) else if (hovered) scene.fill(row, theme.sidebar_hover);
        ctx.dot(row.x + 14, row.y + row.h / 2, 3.5, termColor(theme, t));
        const name = if (t.command) |cmd| cmd else t.view.title();
        const fg = if (active) theme.tab_active_foreground else theme.sidebar_foreground;
        _ = ctx.labelFit(.{ .x = row.x + 26, .y = row.y, .w = @max(0, row.w - 26 - 28), .h = row.h }, name, ws.fonts.ui_small, fg);
        // Its × (ahead of the row in hit order).
        if (hovered or active) {
            const x_rect: Rect = .{ .x = row.right() - 24, .y = row.y + 3, .w = 20, .h = row.h - 6 };
            if (ctx.hovered(x_rect)) scene.quad(x_rect, .{ .color = theme.tab_hover, .radius = 4 });
            ctx.closeIcon(x_rect.x + x_rect.w / 2, x_rect.y + x_rect.h / 2, 7, theme.sidebar_foreground_dim);
            addHit(ws, x_rect, .{ .close_terminal = i });
        }
        addHit(ws, row, .{ .terminal = i });
        y += term_row_h;
    }
}

fn paintServers(ws: *Workspace, ctx: *const ui.Ctx, list: Rect) void {
    const st = &ws.panel;
    const theme = ctx.theme;
    const scene = ctx.scene;
    var statuses: std.ArrayList(Status) = .empty;
    defer statuses.deinit(ws.gpa);
    if (ws.lsp) |l| l.statusList(ws.gpa, &statuses) catch {};
    scene.pushClip(list) catch return;
    defer scene.popClip();

    var y = list.y + 4;
    const all: Rect = .{ .x = list.x, .y = y, .w = list.w, .h = all_row_h };
    if (st.filter == null) scene.fill(all, theme.sidebar_selected) else if (ctx.hovered(all)) scene.fill(all, theme.sidebar_hover);
    _ = ctx.labelFit(.{ .x = all.x + 14, .y = all.y, .w = all.w - 28, .h = all.h }, "All servers", ws.fonts.ui, if (st.filter == null) theme.tab_active_foreground else theme.sidebar_foreground);
    addHit(ws, all, .{ .server = null });
    y += all_row_h;

    var buf: [192]u8 = undefined;
    for (statuses.items) |s| {
        if (y > list.bottom()) break;
        const row: Rect = .{ .x = list.x, .y = y, .w = list.w, .h = row_h };
        const selected = sameName(st.filter, s.name);
        if (selected) scene.fill(row, theme.sidebar_selected) else if (ctx.hovered(row)) scene.fill(row, theme.sidebar_hover);
        ctx.dot(row.x + 18, row.y + 13, 4, stateColor(theme, s));
        const state = stateText(s);
        const state_w = ctx.labelRight(.{ .x = row.x, .y = row.y + 2, .w = row.w - 12, .h = 22 }, state, ws.fonts.ui_small, theme.sidebar_foreground_dim);
        _ = ctx.labelFit(.{ .x = row.x + 30, .y = row.y + 2, .w = @max(0, row.w - 30 - state_w - 20), .h = 22 }, s.name, ws.fonts.ui, if (selected) theme.tab_active_foreground else theme.sidebar_foreground);
        const detail = detailText(s, &buf);
        const busy = s.state == .ready and s.server != null and s.server.?.currentProgress() != null;
        _ = ctx.labelFit(.{ .x = row.x + 30, .y = row.y + 21, .w = @max(0, row.w - 42), .h = 18 }, detail, ws.fonts.ui_small, if (busy) theme.syntax.function else theme.sidebar_foreground_dim);
        addHit(ws, row, .{ .server = s.name });
        y += row_h;
    }
}

fn paintLog(ws: *Workspace, ctx: *const ui.Ctx, rect: Rect) void {
    const st = &ws.panel;
    const ed = st.log orelse return;
    st.log_rect = rect;
    ed.focused = ws.focus == .panel;
    ed.layout(rect);
    refresh(ws, ed);
    ed.paint(ctx.scene, ctx.theme);
    if (ed.buffer.len() > 0) return;
    const hint = if (ws.lsp == null)
        "Language servers are off (--no-lsp)."
    else if (st.filter != null)
        "Nothing logged for this server."
    else
        "No language server output yet. A server starts when you open a file in a language that has one.";
    _ = ctx.labelFit(.{ .x = rect.x + 16, .y = rect.y + 8, .w = @max(0, rect.w - 32), .h = 24 }, hint, ws.fonts.ui, ctx.theme.list_foreground_dim);
}

/// The status bar's language server item, right-aligned so its text ends
/// at `right`. Returns how far left it reaches past `right` (0 if none).
pub fn paintStatusItem(ws: *Workspace, ctx: *const ui.Ctx, bar: Rect, right: f32) f32 {
    const st = &ws.panel;
    st.status_item = .{};
    st.status_item_server = null;
    const l = ws.lsp orelse return 0;
    const theme = ctx.theme;
    const status: ?Status = if (ws.activeEditor()) |ed| l.statusFor(ed.buffer) else null;
    var buf: [160]u8 = undefined;
    var pbuf: [128]u8 = undefined;
    const text: []const u8 = if (status) |s| switch (s.state) {
        .ready => if (s.server.?.currentProgress()) |p| std.fmt.bufPrint(&buf, "{s}: {s}", .{ s.name, progressText(p, &pbuf) }) catch s.name else s.name,
        else => std.fmt.bufPrint(&buf, "{s} ({s})", .{ s.name, stateText(s) }) catch s.name,
    } else blk: {
        // Any server at all: keep the panel a click away.
        if (l.servers.items.len == 0 and l.failed.count() == 0) return 0;
        break :blk "Language Servers";
    };
    const font = ws.fonts.ui_small;
    const pad: f32 = 8;
    const dot_w: f32 = if (status != null) 14 else 0;
    const text_w = @min(ctx.text.measure(text, font), 320);
    const item: Rect = .{ .x = right - text_w - dot_w - pad, .y = bar.y, .w = text_w + dot_w + pad * 2, .h = bar.h };
    const open = st.visible and st.view == .language_servers and sameName(st.filter, if (status) |s| s.name else null);
    if (open or ctx.hovered(item)) ctx.scene.fill(item, theme.statusbar_foreground.withAlpha(if (open) 0.16 else 0.1));
    if (status) |s| ctx.dot(item.x + pad + 4, bar.y + bar.h / 2, 4, stateColor(theme, s));
    _ = ctx.labelFit(.{ .x = item.x + pad + dot_w, .y = bar.y, .w = text_w, .h = bar.h }, text, font, theme.statusbar_foreground);
    st.status_item = item;
    st.status_item_server = if (status) |s| s.name else null;
    return item.w - pad;
}

// ---- Input -------------------------------------------------------------------------

fn clickStatusItem(ws: *Workspace) void {
    const st = &ws.panel;
    if (st.visible and st.view == .language_servers and sameName(st.filter, st.status_item_server)) return hide(ws);
    showLanguageServers(ws, st.status_item_server, false);
}

fn onTopEdge(st: *const State, pos: platform.Vec2) bool {
    return st.visible and st.rect.contains(pos.x, pos.y) and pos.y < st.rect.y + edge_h;
}

fn openMenu(ws: *Workspace, surface: context_menu.Surface, pos: platform.Vec2) bool {
    context_menu.open(ws, surface, pos, null);
    return true;
}

/// Clicks on the panel and the status bar item. Returns true if consumed.
pub fn onMouseDown(ws: *Workspace, m: platform.MouseEvent) bool {
    const st = &ws.panel;
    const pos = m.pos;
    if (st.status_item.contains(pos.x, pos.y)) {
        if (m.button == .left) clickStatusItem(ws);
        return true;
    }
    if (!st.visible or !st.rect.contains(pos.x, pos.y)) return false;
    if (onTopEdge(st, pos)) {
        ws.mouse_down_in = .panel_edge;
        return true;
    }
    for (st.hits.items) |h| if (h.rect.contains(pos.x, pos.y)) {
        if (m.button != .left) return true;
        switch (h.hit) {
            .close => hide(ws),
            .view => |v| show(ws, v, v != .language_servers),
            .refresh => git_status_ui.open(ws),
            .git_help => git_status_ui.showHelp(ws),
            .clear => if (ws.lsp) |l| l.clearLog(),
            .restart => ws.perform(.lsp_restart),
            .server => |name| st.filter = name,
            .new_terminal => addTerminal(ws),
            .run_command => promptRunCommand(ws),
            .kill_terminal => if (activeTerminal(ws)) |tv| closeTerminal(ws, tv),
            .terminal => |i| {
                st.active_terminal = i;
                focusPanel(ws);
            },
            .close_terminal => |i| if (i < st.terminals.items.len) closeTerminal(ws, st.terminals.items[i].view),
        }
        return true;
    };
    if (st.view == .problems and problems_ui.onMouseDown(ws, m)) return true;
    if (st.log_rect.contains(pos.x, pos.y)) if (st.log) |ed| {
        focusPanel(ws);
        ed.onMouseDown(m);
        if (m.button == .right) return openMenu(ws, .editor, pos);
        ws.mouse_down_in = .panel;
    };
    if (st.scm_rect.contains(pos.x, pos.y)) if (st.source_control) |p| {
        focusPanel(ws);
        git_status_ui.onViewClick(ws);
        p.editor.onMouseDown(m);
        if (m.button == .right) return openMenu(ws, .source_control, pos);
        ws.mouse_down_in = .panel;
        if (m.click_count == 2) git_status_ui.onViewDoubleClick(ws);
    };
    // A terminal: selection and mouse reports (drags are followed by terminal_ui).
    if (st.term_rect.contains(pos.x, pos.y)) if (activeTerminal(ws)) |tv| {
        focusPanel(ws);
        if (m.button == .right and !tv.reportsMouse(m.mods)) return openMenu(ws, .terminal, pos);
        tv.mouseDown(m);
    };
    return true;
}

/// Dragging the top edge.
pub fn dragEdge(ws: *Workspace, pos: platform.Vec2) void {
    const st = &ws.panel;
    st.height = @max(min_h, st.rect.bottom() - pos.y);
}

/// The wheel over the panel scrolls the log or the terminal. Returns true if consumed.
pub fn onScroll(ws: *Workspace, s: platform.ScrollEvent) bool {
    const st = &ws.panel;
    if (!st.visible or !st.rect.contains(s.pos.x, s.pos.y)) return false;
    if (st.view == .problems) problems_ui.onScroll(ws, s);
    if (st.log_rect.contains(s.pos.x, s.pos.y)) if (st.log) |ed| ed.onScroll(s);
    if (st.scm_rect.contains(s.pos.x, s.pos.y)) if (st.source_control) |p| p.editor.onScroll(s);
    if (st.term_rect.contains(s.pos.x, s.pos.y)) if (activeTerminal(ws)) |tv| tv.wheel(s);
    return true;
}

/// The pointer's shape over the panel or the status bar item (null: elsewhere).
pub fn cursorShape(ws: *Workspace, pos: platform.Vec2) ?platform.CursorShape {
    const st = &ws.panel;
    if (ws.mouse_down_in == .panel_edge or onTopEdge(st, pos)) return .ns_resize;
    if (st.status_item.contains(pos.x, pos.y)) return .pointer;
    if (!st.visible or !st.rect.contains(pos.x, pos.y)) return null;
    for (st.hits.items) |h| if (h.rect.contains(pos.x, pos.y)) return .pointer;
    if (st.log_rect.contains(pos.x, pos.y)) if (st.log) |ed| return ed.cursorShapeAt(pos);
    if (st.scm_rect.contains(pos.x, pos.y)) if (st.source_control) |p| return p.editor.cursorShapeAt(pos);
    if (st.term_rect.contains(pos.x, pos.y)) return .text;
    return .default;
}
