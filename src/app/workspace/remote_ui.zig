//! Workspace side of Remote-SSH: "Remote: Connect to Host…" (hosts from
//! ~/.ssh/config or typed), the remote folder prompt, `cs-zig ssh://host/path`,
//! the "SSH: host" status, reconnecting with backoff after a drop, remote
//! file-change events (which reuse the local reload logic), and ssh's
//! questions: passwords and passphrases in a masked prompt, unknown host keys
//! as Yes/No (Escape cancels the connect).
//!
//! Remote files are ordinary buffers whose path is `ssh://host/abs/path`
//! (see model/remote/path.zig); model/remote/vfs.zig routes their I/O to the
//! host's connection, so most of the editor never knows they're remote.

const std = @import("std");
const builtin = @import("builtin");
const workspace_mod = @import("workspace.zig");
const quick_panel = @import("quick_panel.zig");
const platform = @import("../../framework/platform/platform.zig");
const client = @import("../../model/remote/client.zig");
const vfs = @import("../../model/remote/vfs.zig");
const rpath = @import("../../model/remote/path.zig");
const ssh = @import("../../model/remote/ssh.zig");
const path_complete = @import("path_complete.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Connection = client.Connection;
const QuickPanel = quick_panel.QuickPanel;

pub const PromptKind = enum(u32) { connect_host = 300, open_folder, askpass };

/// A question from ssh waiting for the user (one shown at a time).
const Question = struct {
    conn: *Connection,
    id: u32,
    kind: client.PromptKind,
    /// Owned.
    text: []u8,
};

const Entry = struct {
    conn: *Connection,
    last_state: client.State = .idle,
    ever_connected: bool = false,
    /// Reconnect attempts since the last success, and when to try next.
    attempts: u32 = 0,
    retry_at: i64 = 0,
};

pub const State = struct {
    entries: std.ArrayList(Entry) = .empty,
    /// What to open once the host is connected (a path, or null = ask).
    pending_host: ?[]u8 = null,
    pending_path: ?[]u8 = null,
    /// Host of the last "Open Folder" prompt.
    prompt_host: ?[]u8 = null,
    /// Server builds that can be uploaded (owned), found on first use.
    binaries: std.ArrayList(ssh.Binary) = .empty,
    native_server: ?[]u8 = null,
    binaries_found: bool = false,
    /// This executable, ssh's SSH_ASKPASS (owned).
    askpass_program: ?[]u8 = null,
    /// ssh's questions; the first is on screen in `shown`.
    questions: std.ArrayList(Question) = .empty,
    shown: ?*QuickPanel = null,
};

/// Early in shutdown: disconnects, so in-flight scans and reads fail fast,
/// and flushes connection events while the workspace is still whole.
pub fn shutdown(ws: *Workspace) void {
    for (ws.remote.entries.items) |e| e.conn.quiesce();
}

/// Last in shutdown (remote terminals and the project are gone by then).
pub fn deinit(ws: *Workspace) void {
    const st = &ws.remote;
    for (st.entries.items) |e| e.conn.destroy();
    st.entries.deinit(ws.gpa);
    clearPending(ws);
    if (st.prompt_host) |h| ws.gpa.free(h);
    for (st.binaries.items) |b| ws.gpa.free(b.path);
    st.binaries.deinit(ws.gpa);
    if (st.native_server) |p| ws.gpa.free(p);
    if (st.askpass_program) |p| ws.gpa.free(p);
    for (st.questions.items) |q| ws.gpa.free(q.text);
    st.questions.deinit(ws.gpa);
}

fn clearPending(ws: *Workspace) void {
    if (ws.remote.pending_host) |h| ws.gpa.free(h);
    if (ws.remote.pending_path) |p| ws.gpa.free(p);
    ws.remote.pending_host = null;
    ws.remote.pending_path = null;
}

// ---- Connections ---------------------------------------------------------------

/// Locates cs-zig-server builds: $CS_ZIG_SERVER_DIR/<arch>/, then
/// <exe dir>/../share/cs-zig-server/<arch>/ (`zig build server`), then the
/// native build next to the editor (`zig build`).
fn findBinaries(ws: *Workspace) void {
    const st = &ws.remote;
    if (st.binaries_found) return;
    st.binaries_found = true;
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const exe_dir = dir_buf[0 .. std.process.executableDirPath(ws.io, &dir_buf) catch 0];
    const env_dir: ?[]const u8 = if (std.c.getenv("CS_ZIG_SERVER_DIR")) |d| std.mem.span(d) else null;
    const native = @tagName(builtin.cpu.arch);
    st.native_server = std.fs.path.join(ws.gpa, &.{ exe_dir, "cs-zig-server" }) catch null;
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    if (std.process.executablePath(ws.io, &exe_buf)) |n| {
        st.askpass_program = ws.gpa.dupe(u8, exe_buf[0..n]) catch null;
    } else |_| {}
    for ([_][]const u8{ "x86_64", "aarch64" }) |arch| {
        var candidates: [3]?[]u8 = .{ null, null, null };
        defer for (candidates) |cand| if (cand) |p| ws.gpa.free(p);
        if (env_dir) |d| candidates[0] = std.fs.path.join(ws.gpa, &.{ d, arch, "cs-zig-server" }) catch null;
        candidates[1] = std.fs.path.join(ws.gpa, &.{ exe_dir, "..", "share", "cs-zig-server", arch, "cs-zig-server" }) catch null;
        if (std.mem.eql(u8, arch, native)) if (st.native_server) |n| {
            candidates[2] = ws.gpa.dupe(u8, n) catch null;
        };
        for (&candidates) |*cand| {
            const p = cand.* orelse continue;
            _ = std.Io.Dir.cwd().statFile(ws.io, p, .{}) catch continue;
            st.binaries.append(ws.gpa, .{ .arch = arch, .path = p }) catch break;
            cand.* = null;
            break;
        }
    }
}

/// $CS_ZIG_SSH_CONFIG: an ssh config file used instead of ~/.ssh/config.
fn sshConfigFile() ?[]const u8 {
    const f = std.c.getenv("CS_ZIG_SSH_CONFIG") orelse return null;
    return std.mem.span(f);
}

fn entryFor(ws: *Workspace, conn: *const Connection) ?*Entry {
    for (ws.remote.entries.items) |*e| if (e.conn == conn) return e;
    return null;
}

pub fn connectionFor(ws: *Workspace, host: []const u8) ?*Connection {
    for (ws.remote.entries.items) |e| if (std.mem.eql(u8, e.conn.host, host)) return e.conn;
    return null;
}

/// The connection for `host`, created (and registered with vfs) if new.
fn obtain(ws: *Workspace, host: []const u8) !*Connection {
    if (connectionFor(ws, host)) |c| return c;
    findBinaries(ws);
    const st = &ws.remote;
    // CS_ZIG_REMOTE_TRANSPORT=local runs the server here instead of over
    // ssh: every host becomes this machine (development and tests).
    const local = if (std.c.getenv("CS_ZIG_REMOTE_TRANSPORT")) |t| std.mem.eql(u8, std.mem.span(t), "local") else false;
    const transport: client.Transport = if (local) .{ .subprocess = st.native_server orelse return error.NoServerBinary } else .ssh;
    const conn = try Connection.create(ws.gpa, ws.io, ws.executor, host, .{ .transport = transport, .binaries = st.binaries.items, .askpass = st.askpass_program, .config_file = sshConfigFile() });
    errdefer conn.destroy();
    conn.sink = .{ .ctx = ws, .onEvent = onEvent };
    try vfs.register(host, conn.backend());
    try st.entries.append(ws.gpa, .{ .conn = conn });
    return conn;
}

fn isUp(conn: *const Connection) bool {
    return conn.currentState() == .connected;
}

/// The host the window is about: the project's, else the active file's.
pub fn currentHost(ws: *Workspace) ?[]const u8 {
    if (ws.project) |p| if (rpath.host(p.root)) |h| return h;
    if (ws.activeEditor()) |ed| if (ed.buffer.path) |p| if (rpath.host(p)) |h| return h;
    return null;
}

/// "SSH: devbox" (plus the connection state when it isn't up).
pub fn statusLabel(ws: *Workspace, buf: []u8) ?[]const u8 {
    const host = currentHost(ws) orelse return null;
    const conn = connectionFor(ws, host);
    const suffix: []const u8 = if (conn) |c| switch (c.currentState()) {
        .connected => "",
        .connecting => " (connecting…)",
        .lost => " (reconnecting…)",
        .failed => " (disconnected)",
        .idle => " (closed)",
    } else " (not connected)";
    return std.fmt.bufPrint(buf, "SSH: {s}{s}", .{ host, suffix }) catch null;
}

// ---- Events ----------------------------------------------------------------------

fn onEvent(ctx: *anyopaque, conn: *Connection, event: client.Event) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    ws.dirty = true;
    switch (event) {
        .changed => |c| {
            for (ws.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, c.path)) {
                ws.checkDiskChange(b);
                break;
            };
        },
        .state => onState(ws, conn),
        .prompt => |p| addQuestion(ws, conn, p),
        .prompt_closed => |id| questionClosed(ws, conn, id),
    }
}

fn backoffMs(attempts: u32) i64 {
    return @min(@as(i64, 30_000), @as(i64, 1000) << @intCast(@min(attempts, 5)));
}

fn onState(ws: *Workspace, conn: *Connection) void {
    const e = entryFor(ws, conn) orelse return;
    const state = conn.currentState();
    const was = e.last_state;
    e.last_state = state;
    var buf: [240]u8 = undefined;
    const text = conn.statusText(&buf);
    switch (state) {
        .connecting => if (text.len > 0) ws.setStatus("{s}", .{text}),
        .connected => if (was != .connected) {
            e.attempts = 0;
            e.retry_at = 0;
            if (e.ever_connected) reconnected(ws, conn) else ws.setStatus("Connected to {s}", .{conn.host});
            e.ever_connected = true;
            openPending(ws, conn);
        },
        .lost => {
            e.retry_at = platform.nowMs() + backoffMs(0);
            ws.setStatus("Connection to {s} lost; reconnecting…", .{conn.host});
        },
        .failed => if (was != .failed) {
            if (e.ever_connected) {
                // A reconnect attempt failed: keep trying, further apart.
                e.attempts += 1;
                e.retry_at = platform.nowMs() + backoffMs(e.attempts);
                ws.setStatus("{s}: {s}; retrying in {d}s", .{ conn.host, text, @divTrunc(backoffMs(e.attempts), 1000) });
            } else {
                ws.setStatus("{s}", .{text});
                if (ws.remote.pending_host) |h| if (std.mem.eql(u8, h, conn.host)) clearPending(ws);
            }
        },
        .idle => if (was == .connecting) {
            // Canceled (Escape on ssh's question).
            if (ws.remote.pending_host) |h| if (std.mem.eql(u8, h, conn.host)) clearPending(ws);
            if (text.len > 0) ws.setStatus("{s}", .{text});
        },
    }
    ws.updateTitle();
}

// ---- ssh's questions (askpass) ----------------------------------------------------

fn addQuestion(ws: *Workspace, conn: *Connection, p: client.Prompt) void {
    const text = ws.gpa.dupe(u8, p.text) catch return conn.answerPrompt(p.id, null);
    ws.remote.questions.append(ws.gpa, .{ .conn = conn, .id = p.id, .kind = p.kind, .text = text }) catch {
        ws.gpa.free(text);
        return conn.answerPrompt(p.id, null);
    };
    if (ws.remote.questions.items.len == 1) showQuestion(ws);
}

/// Puts the first question on screen: a masked prompt for secrets, Yes/No
/// for host keys and confirmations.
fn showQuestion(ws: *Workspace) void {
    const st = &ws.remote;
    st.shown = null;
    if (st.questions.items.len == 0) return;
    const q = st.questions.items[0];
    const tag = @intFromEnum(PromptKind.askpass);
    if (q.kind == .secret) {
        ws.openPrompt("", tag, "");
        const o = ws.overlay orelse return;
        o.masked = true;
        o.prompt_label = oneLine(o.arena(), q.conn.host, q.text) catch "";
    } else {
        // The first line ("The authenticity of host … can't be established.").
        var lines = std.mem.tokenizeAny(u8, q.text, "\r\n");
        const label = oneLine(ws.gpa, q.conn.host, lines.next() orelse "") catch return;
        ws.openList(.list, label, tag, &.{});
        const o = ws.overlay orelse return;
        var fingerprint: []const u8 = "";
        while (lines.next()) |line| if (std.mem.indexOf(u8, line, "fingerprint is") != null) {
            fingerprint = o.arena().dupe(u8, std.mem.trim(u8, line, " ")) catch "";
        };
        const host_key = q.kind == .yes_no;
        o.two_line = host_key;
        o.addItem(.{ .title = if (host_key) "Yes, trust this host key and connect" else "Yes", .detail = fingerprint, .payload = .{ .value = 0 } }) catch {};
        o.addItem(.{ .title = "No", .detail = if (host_key) "Don't connect" else "", .payload = .{ .value = 1 } }) catch {};
    }
    st.shown = ws.overlay;
}

/// ssh's prompt on one line, naming the host when it doesn't.
fn oneLine(alloc: Allocator, host: []const u8, text: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    if (std.mem.indexOf(u8, text, host) == null) try out.print(alloc, "{s}: ", .{host});
    var lines = std.mem.tokenizeAny(u8, text, "\r\n");
    var sep: []const u8 = "";
    while (lines.next()) |line| {
        const t = std.mem.trim(u8, line, " \t");
        if (t.len == 0) continue;
        try out.print(alloc, "{s}{s}", .{ sep, t });
        sep = " ";
    }
    return out.toOwnedSlice(alloc);
}

/// Replies to the shown question (null refuses it; `cancel` also gives up
/// the connect) and shows the next one.
fn answerQuestion(ws: *Workspace, reply: ?[]const u8, cancel: bool) void {
    const st = &ws.remote;
    if (st.questions.items.len == 0) return;
    const q = st.questions.orderedRemove(0);
    defer ws.gpa.free(q.text);
    st.shown = null;
    q.conn.answerPrompt(q.id, reply);
    if (cancel) q.conn.cancelConnect();
    showQuestion(ws);
}

/// ssh stopped waiting (the attempt ended): drop the question.
fn questionClosed(ws: *Workspace, conn: *Connection, id: u32) void {
    const st = &ws.remote;
    for (st.questions.items, 0..) |q, i| if (q.conn == conn and q.id == id) {
        _ = st.questions.orderedRemove(i);
        ws.gpa.free(q.text);
        if (i == 0) {
            if (ws.overlay != null and ws.overlay == st.shown) ws.perform(.cancel);
            showQuestion(ws);
        }
        return;
    };
}

/// After a reconnect: files and the tree may have changed meanwhile, and
/// remote language servers died with the old channel.
fn reconnected(ws: *Workspace, conn: *Connection) void {
    if (ws.project) |p| if (ownedBy(conn, p.root)) p.scan();
    for (ws.buffers.items) |b| if (b.path) |p| if (ownedBy(conn, p)) ws.checkDiskChange(b);
    if (ws.lsp) |l| if (l.root) |r| if (ownedBy(conn, r)) {
        l.restartAll();
        for (ws.buffers.items) |b| l.openBuffer(b);
    };
    ws.setStatus("Reconnected to {s}", .{conn.host});
}

fn ownedBy(conn: *const Connection, path: []const u8) bool {
    const h = rpath.host(path) orelse return false;
    return std.mem.eql(u8, h, conn.host);
}

fn openPending(ws: *Workspace, conn: *Connection) void {
    const host = ws.remote.pending_host orelse return;
    if (!std.mem.eql(u8, host, conn.host)) return;
    const path = ws.remote.pending_path;
    ws.remote.pending_path = null;
    clearPending(ws);
    if (path) |p| {
        defer ws.gpa.free(p);
        _ = openArgument(ws, p) catch |err| ws.setStatus("Unable to open {s}: {t}", .{ p, err });
    } else promptOpenFolder(ws);
}

/// Retries dropped connections when their backoff has passed.
pub fn tick(ws: *Workspace) void {
    // A question's panel that closed unanswered (Escape, a click outside,
    // another panel) cancels its connect.
    const st = &ws.remote;
    if (st.questions.items.len > 0) {
        const on_screen = if (ws.overlay) |o| o == st.shown and o.prompt_tag == @intFromEnum(PromptKind.askpass) else false;
        if (!on_screen) answerQuestion(ws, null, true);
    }
    const now = platform.nowMs();
    for (ws.remote.entries.items) |*e| {
        if (e.retry_at == 0 or now < e.retry_at) continue;
        const state = e.conn.currentState();
        if (state != .lost and state != .failed) continue;
        e.retry_at = 0;
        e.conn.connectAsync();
    }
}

/// Milliseconds until the next reconnect attempt (keeps the loop awake).
pub fn nextTimer(ws: *Workspace) ?i64 {
    var best: ?i64 = null;
    const now = platform.nowMs();
    for (ws.remote.entries.items) |e| if (e.retry_at != 0) {
        const t = @max(1, e.retry_at - now);
        best = if (best) |b| @min(b, t) else t;
    };
    return best;
}

// ---- Opening -----------------------------------------------------------------

/// Opens `ssh://host/path[:line[:col]]`: a folder becomes the project, a
/// file opens in a tab. Connects first: blocking without a window (command
/// line, headless), in the background with one. Returns the opened file's
/// path, or null for folders and deferred opens.
pub fn openArgument(ws: *Workspace, raw: []const u8) !?[]const u8 {
    const r = rpath.parse(raw) orelse return error.NotRemote;
    const conn = try obtain(ws, r.host);
    if (!isUp(conn)) {
        if (ws.host != null) {
            clearPending(ws);
            ws.remote.pending_host = try ws.gpa.dupe(u8, r.host);
            ws.remote.pending_path = try ws.gpa.dupe(u8, raw);
            conn.connectAsync();
            return null;
        }
        ws.setStatus("Connecting to {s}…", .{r.host});
        conn.connect() catch |err| {
            var buf: [240]u8 = undefined;
            std.log.err("{s}", .{conn.statusText(&buf)});
            return err;
        };
        if (entryFor(ws, conn)) |e| {
            e.ever_connected = true;
            e.last_state = .connected;
        }
        ws.setStatus("Connected to {s}", .{r.host});
    }
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const machine_path = try rpath.expandHome(alloc, r.path, conn.home);
    var path = try rpath.format(alloc, r.host, machine_path);
    var line: ?u32 = null;
    var col: ?u32 = null;
    var stat = vfs.stat(ws.io, path) catch null;
    if (stat == null) {
        // Peel ":line" and ":col" suffixes.
        var parts: [2]?u32 = .{ null, null };
        var n: usize = 0;
        while (n < 2) : (n += 1) {
            const colon = std.mem.lastIndexOfScalar(u8, path, ':') orelse break;
            if (colon < rpath.scheme.len + r.host.len) break;
            parts[n] = std.fmt.parseInt(u32, path[colon + 1 ..], 10) catch break;
            path = path[0..colon];
        }
        if (n == 2) {
            line = parts[1];
            col = parts[0];
        } else if (n == 1) line = parts[0];
        stat = vfs.stat(ws.io, path) catch null;
    }
    if (stat != null and stat.?.kind == .directory) {
        const root = std.mem.trimEnd(u8, path, "/");
        if (ws.project == null or !std.mem.eql(u8, ws.project.?.root, root)) try ws.openProject(root, false);
        ws.updateTitle();
        return null;
    }
    const ed = try ws.openFile(path, false);
    if (line) |l| {
        const row = @min(ed.buffer.lineCount() - 1, l -| 1);
        const start = ed.buffer.lineStart(row);
        const off = @min(start + (col orelse 1) -| 1, ed.buffer.lineEnd(row));
        ed.setSingleSelection(.caret(off));
        ed.afterCommand(true);
    }
    return ed.buffer.path;
}

/// "Remote: Connect to Host…": ~/.ssh/config hosts, or type user@host[:port].
pub fn promptConnect(ws: *Workspace) void {
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const home: []const u8 = if (std.c.getenv("HOME")) |h| std.mem.span(h) else "/";
    const hosts = ssh.configHosts(arena.allocator(), ws.io, home, sshConfigFile()) catch &.{};
    const label = ws.gpa.dupe(u8, "Connect to Host (from ~/.ssh/config, or type user@host):") catch return;
    ws.openList(.list, label, @intFromEnum(PromptKind.connect_host), &.{});
    const o = ws.overlay orelse return;
    o.input.placeholder = "user@host[:port]";
    // Items keep pointers: the titles live in the panel's arena.
    for (hosts, 0..) |h, i| o.addItem(.{ .title = o.arena().dupe(u8, h) catch continue, .payload = .{ .value = i } }) catch {};
}

/// "Remote: Open Folder…" on the current (or last) host.
pub fn promptOpenFolder(ws: *Workspace) void {
    const host = currentHost(ws) orelse blk: {
        for (ws.remote.entries.items) |e| if (isUp(e.conn)) break :blk e.conn.host;
        break :blk null;
    } orelse return promptConnect(ws);
    const conn = connectionFor(ws, host) orelse return promptConnect(ws);
    if (ws.remote.prompt_host) |h| ws.gpa.free(h);
    ws.remote.prompt_host = ws.gpa.dupe(u8, host) catch null;
    var label_buf: [300]u8 = undefined;
    const label = std.fmt.bufPrint(&label_buf, "Open Folder on {s}:", .{host}) catch "Open Folder:";
    var init_buf: [std.fs.max_path_bytes]u8 = undefined;
    const initial = if (ws.project) |p| (if (rpath.parse(p.root)) |r| r.path else conn.home) else conn.home;
    const text = std.fmt.bufPrint(&init_buf, "{s}/", .{std.mem.trimEnd(u8, initial, "/")}) catch "";
    ws.openPrompt(label, @intFromEnum(PromptKind.open_folder), text);
    path_complete.begin(ws, .{ .dirs_only = true, .host = host });
}

/// Quick panel lists; false if the tag isn't ours.
pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item, query: []const u8) bool {
    if (tag == @intFromEnum(PromptKind.askpass)) {
        const yes = if (item) |it| it.payload.value == 0 else false;
        const host_key = ws.remote.questions.items.len > 0 and ws.remote.questions.items[0].kind == .yes_no;
        // A host key wants the word; a confirmation only its exit status.
        answerQuestion(ws, if (yes) "yes" else if (host_key) "no" else null, false);
        return true;
    }
    if (tag != @intFromEnum(PromptKind.connect_host)) return false;
    const typed = std.mem.trim(u8, query, " ");
    // Typed text wins when it looks like a destination and isn't a listed alias.
    const looks_typed = std.mem.indexOfAny(u8, typed, "@:.") != null and (item == null or !std.mem.eql(u8, item.?.title, typed));
    const host = if (item != null and !looks_typed) item.?.title else typed;
    if (host.len == 0) return true;
    const conn = obtain(ws, host) catch |err| {
        ws.setStatus("Cannot connect to {s}: {t}", .{ host, err });
        return true;
    };
    if (isUp(conn)) {
        promptOpenFolder(ws);
        return true;
    }
    clearPending(ws);
    ws.remote.pending_host = ws.gpa.dupe(u8, host) catch null;
    ws.setStatus("Connecting to {s}…", .{host});
    conn.connectAsync();
    return true;
}

/// Prompts; false if the tag isn't ours.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (tag == @intFromEnum(PromptKind.askpass)) {
        // Exactly as typed: spaces can be part of a password.
        answerQuestion(ws, input, false);
        return true;
    }
    if (tag != @intFromEnum(PromptKind.open_folder)) return false;
    const host = ws.remote.prompt_host orelse return true;
    const trimmed = std.mem.trim(u8, input, " ");
    if (trimmed.len == 0) return true;
    const full = if (rpath.isRemote(trimmed)) ws.gpa.dupe(u8, trimmed) catch return true else rpath.format(ws.gpa, host, trimmed) catch return true;
    defer ws.gpa.free(full);
    _ = openArgument(ws, full) catch |err| ws.setStatus("Unable to open {s}: {t}", .{ full, err });
    return true;
}

/// "Remote: Close Connection": closes the host's files and folder, stops its
/// language servers and disconnects. Refuses while remote files are unsaved.
pub fn closeConnection(ws: *Workspace) void {
    const host_ref = currentHost(ws) orelse blk: {
        for (ws.remote.entries.items) |e| if (e.conn.currentState() != .idle) break :blk e.conn.host;
        break :blk null;
    } orelse {
        ws.setStatus("No remote connection", .{});
        return;
    };
    const conn = connectionFor(ws, host_ref) orelse return;
    for (ws.buffers.items) |b| if (b.path) |p| if (ownedBy(conn, p) and b.isDirty()) {
        ws.setStatus("Save or close unsaved files on {s} first", .{conn.host});
        return;
    };
    var i = ws.tabs.items.len;
    while (i > 0) {
        i -= 1;
        const p = ws.tabs.items[i].editor.buffer.path orelse continue;
        if (ownedBy(conn, p)) ws.closeTabNow(i);
    }
    if (ws.project) |p| if (ownedBy(conn, p.root)) {
        p.destroy();
        ws.project = null;
    };
    if (ws.lsp) |l| if (l.root) |r| if (ownedBy(conn, r)) {
        l.restartAll();
        ws.gpa.free(r);
        l.root = null;
        for (ws.buffers.items) |b| l.openBuffer(b);
    };
    if (entryFor(ws, conn)) |e| {
        e.retry_at = 0;
        e.ever_connected = false;
    }
    conn.disconnect();
    if (ws.tabs.items.len == 0) _ = ws.newFile() catch null;
    ws.setStatus("Closed connection to {s}", .{conn.host});
    ws.updateTitle();
    ws.dirty = true;
}
