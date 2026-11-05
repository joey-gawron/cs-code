//! The Source Control view of the panel under the editors (Tools ▸ Git
//! Status, Git: Show Status, ctrl+shift+g; panel_ui.zig): a status buffer
//! like Emacs magit's. A header names HEAD and its upstream, then sections
//! list untracked files, unstaged and staged changes (files that TAB opens
//! into their hunks), stashes, and unpushed, unpulled or recent commits.
//! s, u and x stage, unstage and discard the section, file, hunk or marked
//! lines at the caret; RET visits it; c, P, F, f, b, z, l, d, m, r, X, A, V
//! and t open magit's popups and ? lists them all (git_popup.zig). The
//! repository is the project root's, else the active file's, else the
//! working directory's. While shown it refreshes after saves, changes on
//! disk, index or HEAD changes (a commit or `git add` in a terminal), and
//! when it comes into view. What git printed is kept for `$`; diffs, logs
//! and commits open in tabs, where Enter jumps to the line or commit.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const platform = @import("../../framework/platform/platform.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const Color = @import("../../framework/gpu/scene.zig").Color;
const languages = @import("../../model/syntax/languages.zig");
const quick_panel = @import("quick_panel.zig");
const git = @import("../../model/git/git.zig");
const diff_mod = @import("../../model/git/diff.zig");
const status_mod = @import("../../model/git/status.zig");
const rpath = @import("../../model/remote/path.zig");
const git_ui = @import("git_ui.zig");
const git_popup = @import("git_popup.zig");
const scm_state = @import("scm_state.zig");
const scm_ui = @import("scm_ui.zig");
const panel_ui = @import("panel_ui.zig");
const Palette = @import("../terminal/palette.zig").Palette;

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Status = status_mod.Status;
const Item = status_mod.Item;
const Section = status_mod.Section;

/// A commit message tab (`Tab.git_commit`), and its closing.
pub const CommitTab = git_popup.CommitTab;
pub const commitTabClosed = git_popup.commitTabClosed;
/// Draws an open popup over the view (panel_ui).
pub const paintPopup = git_popup.paint;

/// How often the repository is checked for index / HEAD changes.
const poll_ms = 1000;
/// Untracked files larger than this aren't shown as a diff.
const max_new_file_diff = 4 << 20;
/// `$` keeps about this much of git's output.
const max_process_log = 1 << 20;

/// Prompts and lists of the view and its popups.
pub const PromptKind = enum(u32) { confirm = 500, pick, branch_create, branch_new, branch_rename, stash_message, tag_name };

/// What finishes a git command run from the view.
pub const After = union(enum) {
    /// Refresh, saying it's done (or what git said went wrong).
    refresh,
    /// The output goes to the Diff tab.
    diff,
    /// The output goes to the Log tab.
    log,
    /// git_popup takes the result (commit preparation, pickers).
    popup: git_popup.Next,
};

/// How to run a git command (`run`).
pub const Run = struct {
    input: ?[]const u8 = null,
    /// "Pushing": the status bar says "Pushing…" while it runs.
    label: []const u8 = "",
    /// Said when it succeeds (else "<label>: done").
    done: []const u8 = "",
    /// The Diff or Log tab's title.
    title: []const u8 = "",
    after: After = .refresh,
    /// git may not prompt on a terminal or open an editor (pushes, merges).
    batch: bool = false,
};

/// A git command started by `run`.
const Op = struct {
    panel: *Panel,
    job: *git.Job = undefined,
    arena: std.heap.ArenaAllocator,
    after: After,
    label: []const u8 = "",
    done: []const u8 = "",
    title: []const u8 = "",
};

/// What the answer yes to a question does.
pub const Request = struct {
    arena: std.heap.ArenaAllocator,
    argv: []const []const u8 = &.{},
    run: Run = .{},
    /// Files to delete (untracked ones) instead of running git.
    delete: []const []const u8 = &.{},
    /// What a picker listed.
    choices: []const git_popup.Choice = &.{},
    /// Instead of either: git_popup's own step.
    then: ?git_popup.Then = null,

    pub fn create(gpa: Allocator) !*Request {
        const r = try gpa.create(Request);
        r.* = .{ .arena = .init(gpa) };
        return r;
    }

    pub fn destroy(self: *Request, gpa: Allocator) void {
        self.arena.deinit();
        gpa.destroy(self);
    }

    pub fn alloc(self: *Request) Allocator {
        return self.arena.allocator();
    }
};

/// The Source Control view's state (`ws.panel.source_control`). It owns its
/// editor and buffer, which aren't a tab's.
pub const Panel = struct {
    ws: *Workspace,
    editor: *Editor,
    buffer: *Buffer,
    view: status_mod.View,
    /// Folders searched for a repository, in order: the project root, the
    /// active file's folder, the working directory.
    candidates: [3]?[]u8 = @splat(null),
    candidate: usize = 0,
    /// The repository's git directory (`.git`, or a worktree's).
    git_dir: ?[]u8 = null,
    /// `rev-parse` or `status`.
    job: ?*git.Job = null,
    /// Commands started from the view (stage, commit, push…).
    ops: std.ArrayList(*Op) = .empty,
    /// A status run is due.
    stale: bool = false,
    /// `git.repoStamp` and the saved versions of the repository's buffers
    /// when the last run started.
    stamp: i128 = 0,
    saved_sig: u64 = 0,
    last_poll: i64 = 0,
    was_active: bool = false,
    /// The files have been shown once (the first time the caret goes to the first).
    shown: bool = false,
    /// Hash of the text in the buffer (unchanged refreshes leave it alone).
    shown_hash: u64 = 0,
    /// v: the row of the mark; moving selects whole lines from it (magit's region).
    mark: ?u32 = null,
    /// g was the last key: g goes to the top, r is dropped (vim's gg, evil's gr).
    after_g: bool = false,
    /// The open popup (git_popup.zig).
    popup: ?git_popup.State = null,
    /// A yes/no question's action, or a picker's or prompt's purpose.
    pending: ?*Request = null,
    pick: git_popup.Pick = .checkout,
    /// What git printed for the commands run, newest last (`$`).
    process_log: std.ArrayList(u8) = .empty,
    /// The commit being prepared, and a message to offer again (an aborted
    /// or failed commit's).
    commit: ?*git_popup.CommitPlan = null,
    last_message: ?[]u8 = null,

    pub fn destroy(self: *Panel) void {
        const gpa = self.ws.gpa;
        self.detach();
        for (self.candidates) |c| if (c) |p| gpa.free(p);
        if (self.git_dir) |d| gpa.free(d);
        if (self.pending) |r| r.destroy(gpa);
        if (self.commit) |c| c.destroy(gpa);
        if (self.last_message) |m| gpa.free(m);
        self.process_log.deinit(gpa);
        self.view.deinit();
        self.editor.destroy();
        self.buffer.deinit();
        gpa.destroy(self.buffer);
        gpa.destroy(self);
    }

    /// Running jobs free themselves from now on.
    pub fn detach(self: *Panel) void {
        const gpa = self.ws.gpa;
        if (self.job) |j| j.detach();
        self.job = null;
        for (self.ops.items) |op| {
            op.job.detach();
            op.arena.deinit();
            gpa.destroy(op);
        }
        self.ops.clearAndFree(gpa);
    }

    pub fn status(self: *const Panel) ?*const Status {
        return if (self.view.status) |*s| s else null;
    }
};

/// A tab opened from the Source Control view (`Tab.git_diff`): a diff or
/// commit, a log, or git's output.
pub const Diff = struct {
    gpa: Allocator,
    kind: Kind,
    /// The repository the diff's paths are relative to.
    root: []u8,
    title: [160]u8 = undefined,

    pub const Kind = enum { diff, log, process };

    /// `buffer` is the tab's (it may live on in another view, under a plain name).
    pub fn destroy(self: *Diff, buffer: ?*Buffer) void {
        if (buffer) |b| b.display_name = switch (self.kind) {
            .diff => "Diff",
            .log => "Log",
            .process => "Git Output",
        };
        self.gpa.free(self.root);
        self.gpa.destroy(self);
    }
};

/// Detaches running jobs before shutdown (the view is freed with the panel).
pub fn deinit(ws: *Workspace) void {
    if (ws.panel.source_control) |p| p.detach();
}

pub fn caretRow(ed: *Editor) u32 {
    return ed.buffer.offsetToPoint(ed.primarySelection().head).row;
}

fn isUnder(path: []const u8, root: []const u8) bool {
    if (!std.mem.startsWith(u8, path, root)) return false;
    return path.len == root.len or path[root.len] == '/' or (root.len > 0 and root[root.len - 1] == '/');
}

/// Saved versions of the open buffers inside `root` (saves and reloads change it).
fn savedSignature(ws: *Workspace, root: []const u8) u64 {
    var h = std.hash.Wyhash.init(0);
    for (ws.buffers.items) |b| {
        const p = b.path orelse continue;
        if (!isUnder(p, root)) continue;
        h.update(std.mem.asBytes(&b.id));
        h.update(std.mem.asBytes(&b.saved_version));
    }
    return h.final();
}

/// Puts generated text into a read-only tab (not undoable, not "unsaved").
fn setText(ed: *Editor, text: []const u8) void {
    var line_ending: buffer_mod.LineEnding = .lf;
    const clean = buffer_mod.normalizeText(ed.buffer.gpa, text, &line_ending) catch return;
    defer ed.buffer.gpa.free(clean);
    ed.buffer.read_only = false;
    ed.setText(clean, false);
    ed.buffer.read_only = true;
    ed.buffer.saved_version = ed.buffer.version;
    ed.buffer.clearHistory();
}

/// Shows the view's text, the caret on `row` (a mark goes).
fn show(p: *Panel, row: u32) void {
    const ed = p.editor;
    const hash = std.hash.Wyhash.hash(0, p.view.text.items);
    if (hash != p.shown_hash) {
        p.shown_hash = hash;
        const scroll = ed.scroll_y;
        setText(ed, p.view.text.items);
        ed.scroll_y = scroll;
    }
    p.mark = null;
    const r = @min(row, ed.buffer.lineCount() - 1);
    ed.setSingleSelection(.caret(ed.buffer.lineStart(r)));
    ed.ensureCaretVisible(false);
    p.ws.dirty = true;
}

fn showNote(p: *Panel, comptime fmt: []const u8, args: anytype) void {
    p.view.setNote(fmt, args);
    p.view.rebuild() catch return;
    show(p, 0);
}

// ---- Opening ------------------------------------------------------------------------

/// Git: Show Status. Shows the Source Control view (made on first use)
/// and looks for the repository again (the active file may be in another);
/// from the view itself, just refreshes.
pub fn open(ws: *Workspace) void {
    var candidates: [3]?[]const u8 = @splat(null);
    var cwd_buf: [std.fs.max_path_bytes]u8 = @splat(0); // See Workspace.realPath.
    var remote = false;
    if (ws.project) |proj| {
        if (rpath.isRemote(proj.root)) remote = true else candidates[0] = proj.root;
    }
    if (ws.activeIndex()) |ai| {
        if (ws.tabs.items[ai].editor.buffer.path) |path| {
            if (rpath.isRemote(path)) remote = true else candidates[1] = std.fs.path.dirname(path);
        }
    }
    if (std.process.currentPath(ws.io, &cwd_buf)) |n| candidates[2] = cwd_buf[0..n] else |_| {}

    const from_view = ws.focus == .panel and showing(ws);
    const p = ws.panel.source_control orelse create(ws) orelse return;
    panel_ui.showSourceControl(ws);
    // Looking for the repository is a refresh already.
    p.was_active = true;
    if (from_view) {
        p.stale = true;
        return;
    }
    search(p, candidates, remote);
}

/// The view is on screen.
fn showing(ws: *Workspace) bool {
    return ws.panel.visible and ws.panel.view == .source_control;
}

fn create(ws: *Workspace) ?*Panel {
    const buffer = ws.gpa.create(Buffer) catch return null;
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return null;
    };
    ws.next_buffer_id += 1;
    buffer.display_name = "Source Control";
    buffer.language = &languages.git_status_list;
    buffer.read_only = true;
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return null;
    };
    ed.plain = true;
    const p = ws.gpa.create(Panel) catch {
        ed.destroy();
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return null;
    };
    p.* = .{ .ws = ws, .editor = ed, .buffer = buffer, .view = .init(ws.gpa) };
    ed.row_background = .{ .ctx = p, .color = rowColor };
    ws.panel.source_control = p;
    return p;
}

/// Starts looking for the repository in `candidates`.
fn search(p: *Panel, candidates: [3]?[]const u8, remote: bool) void {
    const gpa = p.ws.gpa;
    for (&p.candidates) |*c| {
        if (c.*) |old| gpa.free(old);
        c.* = null;
    }
    var n: usize = 0;
    for (candidates) |maybe| {
        const dir = maybe orelse continue;
        const dup = for (p.candidates[0..n]) |c| {
            if (std.mem.eql(u8, c.?, dir)) break true;
        } else false;
        if (dup) continue;
        p.candidates[n] = gpa.dupe(u8, dir) catch continue;
        n += 1;
    }
    p.candidate = 0;
    if (n == 0) {
        p.view.clearStatus();
        p.view.setRoot(null) catch {};
        if (remote) {
            showNote(p, "Source control isn't available for remote folders yet.", .{});
        } else showNote(p, "Open a folder or a file to see its git status.", .{});
        return;
    }
    if (p.job) |j| j.detach();
    p.job = null;
    // The current files stay up until the repository is known.
    if (p.view.status == null) showNote(p, "Looking for a git repository…", .{});
    nextCandidate(p);
}

fn nextCandidate(p: *Panel) void {
    while (p.candidate < p.candidates.len) : (p.candidate += 1) {
        const dir = p.candidates[p.candidate] orelse continue;
        p.job = git.Job.start(git_ui.runner(p.ws), &.{ "rev-parse", "--show-toplevel", "--absolute-git-dir" }, dir, null, null, p, onRevParse) catch |err| {
            if (err == error.GitMissing) return showNote(p, "Git isn't installed (no git on PATH).", .{});
            continue;
        };
        return;
    }
    p.view.clearStatus();
    p.view.setRoot(null) catch {};
    showNote(p, "Not a git repository: {s}", .{p.candidates[0] orelse ""});
}

fn onRevParse(ctx: *anyopaque, job: *git.Job) void {
    const p: *Panel = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    p.job = null;
    if (job.runner.missing) return showNote(p, "Git isn't installed (no git on PATH).", .{});
    const out = job.output orelse {
        p.candidate += 1;
        return nextCandidate(p);
    };
    var lines = std.mem.tokenizeAny(u8, out, "\r\n");
    const root = lines.next() orelse "";
    const git_dir = lines.next() orelse "";
    if (root.len == 0) {
        p.candidate += 1;
        return nextCandidate(p);
    }
    const gpa = p.ws.gpa;
    const same = if (p.view.root) |r| std.mem.eql(u8, r, root) else false;
    if (!same) {
        p.view.clearStatus();
        p.view.setRoot(root) catch return;
        p.shown = false;
    }
    if (p.git_dir) |d| gpa.free(d);
    p.git_dir = gpa.dupe(u8, git_dir) catch null;
    p.view.setNote(null, .{});
    startStatus(p);
}

// ---- Refreshing ---------------------------------------------------------------------

fn parseStatus(job: *git.Job) void {
    const gpa = job.runner.gpa;
    const st = gpa.create(Status) catch return;
    st.* = status_mod.parse(gpa, job.output.?) catch {
        gpa.destroy(st);
        return;
    };
    status_mod.gather(st, job.runner.io, job.cwd);
    job.payload = st;
}

fn freeStatus(job: *git.Job) void {
    const st: *Status = @ptrCast(@alignCast(job.payload.?));
    st.deinit();
    job.runner.gpa.destroy(st);
}

/// Runs `git status` and the rest of a refresh (or queues a run behind the running job).
pub fn startStatus(p: *Panel) void {
    p.stale = false;
    if (p.job != null) {
        p.stale = true;
        return;
    }
    const root = p.view.root orelse return;
    p.stamp = if (p.git_dir) |d| git.repoStamp(p.ws.io, d) else 0;
    p.saved_sig = savedSignature(p.ws, root);
    const job = git.Job.start(git_ui.runner(p.ws), &status_mod.status_argv, root, null, parseStatus, p, onStatus) catch {
        return showNote(p, "Unable to run git status in {s}", .{root});
    };
    job.free_payload = freeStatus;
    p.job = job;
}

fn onStatus(ctx: *anyopaque, job: *git.Job) void {
    const p: *Panel = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    p.job = null;
    const ptr = job.payload orelse {
        showNote(p, "git status failed in {s}", .{p.view.root orelse ""});
        return;
    };
    job.payload = null;
    const st: *Status = @ptrCast(@alignCast(ptr));
    defer p.ws.gpa.destroy(st);
    p.view.in_progress = if (p.git_dir) |d| status_mod.inProgress(p.ws.io, d) else null;
    var row = p.view.update(st.*, caretRow(p.editor)) catch return;
    if (!p.shown) {
        p.shown = true;
        row = p.view.firstItemRow() orelse 0;
    }
    show(p, row);
    if (p.stale) startStatus(p);
}

/// A file changed on disk (from the file watcher).
pub fn onDiskChange(ws: *Workspace, path: []const u8) void {
    scm_state.onDiskChange(ws, path);
    const p = ws.panel.source_control orelse return;
    if (p.view.root) |root| if (isUnder(path, root)) {
        p.stale = true;
    };
}

/// Refreshes the view while it's shown: saves, index / HEAD changes, its
/// coming into view.
pub fn tick(ws: *Workspace) void {
    const p = ws.panel.source_control orelse return;
    const active = showing(ws);
    if (active and !p.was_active) p.stale = true;
    p.was_active = active;
    if (!active) return;
    const root = p.view.root orelse return;
    if (p.job != null) return;
    const now = platform.nowMs();
    if (now - p.last_poll >= poll_ms) {
        p.last_poll = now;
        if (p.git_dir) |d| if (git.repoStamp(ws.io, d) != p.stamp) {
            p.stale = true;
        };
    }
    if (savedSignature(ws, root) != p.saved_sig) p.stale = true;
    if (p.stale) startStatus(p);
}

/// Time until the next repository check (while the view is shown).
pub fn nextTimer(ws: *Workspace) ?i64 {
    const p = ws.panel.source_control orelse return null;
    if (!showing(ws) or p.view.root == null) return null;
    return @max(1, p.last_poll + poll_ms - platform.nowMs());
}

// ---- Painting -----------------------------------------------------------------------

/// The diff lines' backgrounds, as in magit: added and removed lines
/// tinted green and red (the scheme's terminal colors), hunk headings
/// shaded, the caret's hunk stronger.
fn rowColor(ctx: *anyopaque, row: u32, theme: *const theme_mod.Theme) ?Color {
    const p: *Panel = @ptrCast(@alignCast(ctx));
    const t = p.view.targetAt(row) orelse return null;
    if (t.kind != .hunk and t.kind != .line) return null;
    const here = blk: {
        const c = p.view.targetAt(caretRow(p.editor)) orelse break :blk false;
        if (c.kind != .hunk and c.kind != .line) break :blk false;
        break :blk c.section == t.section and c.index == t.index and c.hunk == t.hunk;
    };
    if (t.kind == .hunk) return theme.foreground.withAlpha(if (here) 0.13 else 0.07);
    const f = p.view.fileDiff(t.section, t.index) orelse return null;
    const h = p.view.hunk(t) orelse return null;
    if (t.line >= h.lines.len) return null;
    const l = h.lines[t.line];
    // A combined diff (a conflict) has a column per side.
    const marks = l[0..@min(l.len, @as(usize, if (f.combined) 2 else 1))];
    const ansi = Palette.fromTheme(theme).ansi;
    if (std.mem.indexOfScalar(u8, marks, '+') != null) return ansi[2].withAlpha(if (here) 0.22 else 0.11);
    if (std.mem.indexOfScalar(u8, marks, '-') != null) return ansi[1].withAlpha(if (here) 0.22 else 0.11);
    return if (here) theme.foreground.withAlpha(0.035) else null;
}

// ---- Running git --------------------------------------------------------------------

/// Runs `git argv…` in the repository; `r.after` takes the result.
pub fn run(p: *Panel, argv: []const []const u8, r: Run) void {
    const ws = p.ws;
    const root = p.view.root orelse return ws.setStatus("No git repository", .{});
    const op = ws.gpa.create(Op) catch return;
    op.* = .{ .panel = p, .arena = .init(ws.gpa), .after = r.after };
    const a = op.arena.allocator();
    op.label = a.dupe(u8, r.label) catch "";
    op.done = a.dupe(u8, r.done) catch "";
    op.title = a.dupe(u8, r.title) catch "";
    op.job = git.Job.startWith(git_ui.runner(ws), argv, root, .{ .input = r.input, .capture = true, .batch = r.batch }, null, op, onOp) catch |err| {
        op.arena.deinit();
        ws.gpa.destroy(op);
        if (err == error.GitMissing) return ws.setStatus("Git isn't installed (no git on PATH)", .{});
        return ws.setStatus("Unable to run git", .{});
    };
    p.ops.append(ws.gpa, op) catch {
        op.job.detach();
        op.arena.deinit();
        ws.gpa.destroy(op);
        return;
    };
    if (r.label.len > 0) ws.setStatus("{s}…", .{r.label});
}

/// Runs a request (a question answered yes, a picker's choice).
pub fn runRequest(p: *Panel, r: *Request) void {
    if (r.then) |then| return git_popup.runThen(p, then, r);
    if (r.delete.len > 0) return deleteFiles(p, r.delete);
    run(p, r.argv, r.run);
}

fn onOp(ctx: *anyopaque, job: *git.Job) void {
    const op: *Op = @ptrCast(@alignCast(ctx));
    const p = op.panel;
    const ws = p.ws;
    defer {
        job.destroy();
        for (p.ops.items, 0..) |o, i| if (o == op) {
            _ = p.ops.swapRemove(i);
            break;
        };
        op.arena.deinit();
        ws.gpa.destroy(op);
    }
    logProcess(p, job);
    if (job.output == null) {
        ws.setStatus("Unable to run git {s}", .{if (job.argv.len > 1) job.argv[1] else ""});
        return;
    }
    if (op.after == .popup) return git_popup.onResult(p, op.after.popup, job);
    if (job.failed) {
        reportFailure(p, op.label, job);
        startStatus(p);
        return;
    }
    const out = job.output.?;
    switch (op.after) {
        .refresh => {
            if (op.done.len > 0) {
                ws.setStatus("{s}", .{op.done});
            } else if (op.label.len > 0) ws.setStatus("{s}: done", .{op.label});
            startStatus(p);
        },
        .diff => if (out.len == 0) ws.setStatus("No differences", .{}) else {
            _ = showText(p, .diff, op.title, out);
        },
        .log => if (out.len == 0) ws.setStatus("No commits", .{}) else {
            _ = showText(p, .log, op.title, out);
        },
        .popup => unreachable,
    }
}

/// The line of git's output that says what went wrong.
pub fn errorLine(job: *const git.Job) []const u8 {
    const text = if (job.stderr) |e| (if (std.mem.trim(u8, e, " \r\n").len > 0) e else job.output orelse "") else job.output orelse "";
    var fallback: []const u8 = "";
    var it = std.mem.tokenizeAny(u8, text, "\r\n");
    while (it.next()) |raw| {
        const l = std.mem.trim(u8, raw, " \t");
        if (std.mem.startsWith(u8, l, "fatal: ") or std.mem.startsWith(u8, l, "error: ")) return l;
        if (std.mem.startsWith(u8, l, "! [")) return l;
        if (fallback.len == 0 and !std.mem.startsWith(u8, l, "hint: ")) fallback = l;
    }
    return fallback;
}

pub fn reportFailure(p: *Panel, label: []const u8, job: *const git.Job) void {
    const why = errorLine(job);
    const what = if (label.len > 0) label else if (job.argv.len > 1) job.argv[1] else "git";
    if (why.len > 0) {
        p.ws.setStatus("{s} failed: {s}  ($ shows git's output)", .{ what, why[0..@min(why.len, 200)] });
    } else p.ws.setStatus("{s} failed ($ shows git's output)", .{what});
}

/// Adds a finished command and what it printed to the process log.
fn logProcess(p: *Panel, job: *const git.Job) void {
    const gpa = p.ws.gpa;
    const log = &p.process_log;
    if (log.items.len > max_process_log) {
        // Drop the oldest half, at a command's start.
        const cut = std.mem.indexOfPos(u8, log.items, log.items.len / 2, "\n$ ") orelse log.items.len;
        const keep = log.items.len - cut;
        std.mem.copyForwards(u8, log.items[0..keep], log.items[cut..]);
        log.shrinkRetainingCapacity(keep);
    }
    if (log.items.len > 0) log.append(gpa, '\n') catch return;
    log.appendSlice(gpa, "$ git") catch return;
    for (job.argv[1..]) |arg| {
        if (arg.len == 0 or std.mem.indexOfAny(u8, arg, " \t'\"") != null) {
            log.print(gpa, " '{s}'", .{arg}) catch return;
        } else log.print(gpa, " {s}", .{arg}) catch return;
    }
    log.append(gpa, '\n') catch return;
    for ([_]?[]const u8{ job.output, job.stderr }) |maybe| {
        const text = maybe orelse continue;
        // Long outputs (diffs, logs) are in their own tabs.
        const shown = text[0..@min(text.len, 4000)];
        log.appendSlice(gpa, shown) catch return;
        if (shown.len < text.len) log.print(gpa, "\n… ({d} more bytes)", .{text.len - shown.len}) catch return;
        if (shown.len > 0 and shown[shown.len - 1] != '\n') log.append(gpa, '\n') catch return;
    }
    if (job.output == null) {
        log.appendSlice(gpa, "[git could not be run]\n") catch return;
    } else if (job.failed) log.appendSlice(gpa, "[failed]\n") catch return;
    // An open output tab follows.
    for (p.ws.tabs.items) |t| if (t.git_diff) |d| if (d.kind == .process) {
        setText(t.editor, log.items);
        t.editor.setSingleSelection(.caret(t.editor.buffer.len()));
        t.editor.ensureCaretVisible(false);
    };
}

/// `$`: git's output for the commands run from the view.
fn showProcess(p: *Panel) void {
    if (p.process_log.items.len == 0) return p.ws.setStatus("No git commands run yet", .{});
    const ed = showText(p, .process, "Git Output", p.process_log.items) orelse return;
    ed.setSingleSelection(.caret(ed.buffer.len()));
    ed.ensureCaretVisible(false);
}

fn deleteFiles(p: *Panel, paths: []const []const u8) void {
    const ws = p.ws;
    const root = p.view.root orelse return;
    var deleted: usize = 0;
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    for (paths) |rel| {
        const abs = std.fmt.bufPrint(&buf, "{s}/{s}", .{ root, rel }) catch continue;
        Io.Dir.cwd().deleteFile(ws.io, abs) catch |err| {
            ws.setStatus("Unable to delete {s}: {t}", .{ rel, err });
            continue;
        };
        deleted += 1;
    }
    if (deleted > 0) ws.setStatus("Deleted {d} file{s}", .{ deleted, if (deleted == 1) "" else "s" });
    startStatus(p);
}

// ---- Questions ----------------------------------------------------------------------

/// Asks `question`; yes runs `r` (taken).
pub fn confirm(p: *Panel, question: []const u8, r: *Request) void {
    const ws = p.ws;
    if (p.pending) |old| old.destroy(ws.gpa);
    p.pending = r;
    const label = ws.gpa.dupe(u8, question) catch return;
    ws.openList(.list, label, @intFromEnum(PromptKind.confirm), &.{ "Yes", "No" });
}

/// Lists (yes/no questions, pickers). Returns false for other lists.
pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item, query: []const u8) bool {
    if (scm_ui.confirmList(ws, tag, item, query)) return true;
    const p = ws.panel.source_control orelse return false;
    if (tag == @intFromEnum(PromptKind.pick)) {
        git_popup.picked(p, if (item) |it| it.payload.value else null, query);
        return true;
    }
    if (tag != @intFromEnum(PromptKind.confirm)) return false;
    const r = p.pending orelse return true;
    p.pending = null;
    defer r.destroy(ws.gpa);
    if (item == null or item.?.payload.value != 0) return true;
    runRequest(p, r);
    return true;
}

/// Prompts of the popups. Returns false for other prompts.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (scm_ui.confirmPrompt(ws, tag, input)) return true;
    if (tag < @intFromEnum(PromptKind.branch_create) or tag > @intFromEnum(PromptKind.tag_name)) return false;
    const p = ws.panel.source_control orelse return true;
    git_popup.prompted(p, @enumFromInt(tag), std.mem.trim(u8, input, " \t"));
    return true;
}

// ---- Keys and clicks -----------------------------------------------------------------

/// Keys in a Diff, Log or commit message tab. Returns true if consumed.
pub fn handleKey(ws: *Workspace, index: usize, k: platform.KeyEvent) bool {
    const tab = ws.tabs.items[index];
    if (tab.git_commit) |c| return git_popup.commitKey(ws, c, index, k);
    if (k.mods.ctrl or k.mods.alt) return false;
    if (tab.git_diff) |d| return diffKey(ws, d, tab.editor, index, k);
    return false;
}

/// Keys in the Source Control view (it has focus). Returns true if consumed.
pub fn viewKey(ws: *Workspace, k: platform.KeyEvent) bool {
    const p = ws.panel.source_control orelse return false;
    if (p.popup != null) return git_popup.key(p, k);
    if (k.mods.alt) return false;
    if (k.mods.ctrl) {
        // ctrl+g drops the mark, as in Emacs.
        if (keyChar(k) == 'g' and !k.mods.shift and (p.mark != null or !p.editor.primarySelection().isEmpty())) {
            clearMark(p);
            return true;
        }
        return false;
    }
    const after_g = p.after_g;
    p.after_g = false;
    if (k.key == .named) switch (k.key.named) {
        .enter => {
            visit(p);
            return true;
        },
        .tab => {
            if (k.mods.shift) setLevel(p, p.view.level % 4 + 1) else toggle(p);
            return true;
        },
        .escape => {
            if (p.mark == null and p.editor.primarySelection().isEmpty()) return false;
            clearMark(p);
            return true;
        },
        .up, .down => {
            if (p.mark == null) return false;
            moveLine(p, k.key.named == .down);
            return true;
        },
        else => return false,
    };
    const ch = keyChar(k) orelse return false;
    if (after_g and ch == 'g') {
        moveTo(p, 0);
        return true;
    }
    if (after_g and ch == 'r') return true;
    return statusKey(p, ch);
}

/// A key of the view (also run from the ? popup). Returns true if it's one.
pub fn statusKey(p: *Panel, ch: u21) bool {
    const ws = p.ws;
    const row = caretRow(p.editor);
    switch (ch) {
        'j' => moveLine(p, true),
        'k' => moveLine(p, false),
        'n' => if (p.view.nextHeading(row, true)) |r| moveTo(p, r),
        'p' => if (p.view.nextHeading(row, false)) |r| moveTo(p, r),
        '^' => if (p.view.parentRow(row)) |r| moveTo(p, r),
        'G' => moveTo(p, @intCast(p.view.targets.items.len -| 1)),
        '1', '2', '3', '4' => setLevel(p, @intCast(ch - '0')),
        'o' => visit(p),
        's' => act(p, .stage),
        'u' => act(p, .unstage),
        'x' => act(p, .discard),
        'S' => stageAll(p),
        'U' => unstageAll(p),
        'v' => if (p.mark != null) clearMark(p) else {
            p.mark = row;
            moveTo(p, row);
            ws.setStatus("Mark set: move to select lines, then s, u or x (v or Escape drops it)", .{});
        },
        'g' => {
            p.after_g = true;
            p.stale = true;
            ws.setStatus("Refreshing git status", .{});
        },
        'q' => panel_ui.hide(ws),
        '$' => showProcess(p),
        '!' => {
            panel_ui.promptRunCommand(ws);
            if (ws.overlay) |o| o.input.setText("git ", false);
        },
        else => return git_popup.open(p, ch),
    }
    return true;
}

fn keyChar(k: platform.KeyEvent) ?u21 {
    return switch (k.key) {
        .char => if (k.text.len == 1) k.text[0] else k.key.char,
        .named => null,
    };
}

/// Puts the caret on `row`; with the mark set, selects the lines between.
fn moveTo(p: *Panel, row: u32) void {
    const ed = p.editor;
    const r = @min(row, ed.buffer.lineCount() - 1);
    if (p.mark) |m| {
        const from = @min(m, r);
        const to = @max(m, r);
        if (r >= m) {
            ed.setSingleSelection(.range(ed.buffer.lineStart(from), ed.buffer.lineEnd(to)));
        } else ed.setSingleSelection(.range(ed.buffer.lineEnd(to), ed.buffer.lineStart(from)));
    } else ed.setSingleSelection(.caret(ed.buffer.lineStart(r)));
    ed.ensureCaretVisible(false);
    p.ws.dirty = true;
}

fn moveLine(p: *Panel, down: bool) void {
    const row = caretRow(p.editor);
    if (down) moveTo(p, row + 1) else moveTo(p, row -| 1);
}

fn clearMark(p: *Panel) void {
    p.mark = null;
    moveTo(p, caretRow(p.editor));
}

fn toggle(p: *Panel) void {
    const row = p.view.toggle(caretRow(p.editor)) catch return orelse return;
    show(p, row);
}

fn setLevel(p: *Panel, level: u8) void {
    const t = p.view.targetAt(caretRow(p.editor));
    p.view.setLevel(level) catch return;
    // The caret stays on what it was on, else the heading above it.
    var target = t;
    var row: ?u32 = null;
    while (target) |x| : (target = x.parent()) {
        row = p.view.rowOf(x);
        if (row != null) break;
    }
    show(p, row orelse p.view.firstItemRow() orelse 0);
    p.ws.setStatus("Showing {s}", .{switch (level) {
        1 => "section headings",
        2 => "files",
        3 => "hunk headings",
        else => "everything",
    }});
}

fn diffKey(ws: *Workspace, d: *Diff, ed: *Editor, index: usize, k: platform.KeyEvent) bool {
    if (k.key == .named) {
        if (k.key.named != .enter) return false;
        switch (d.kind) {
            .diff => gotoDiffLine(ws, d, ed),
            .log => showLogCommit(ws, ed),
            .process => {},
        }
        return true;
    }
    switch (keyChar(k) orelse return false) {
        'q' => ws.closeTab(index),
        'n', 'j' => if (d.kind != .diff) {
            _ = ed.perform(.move_down);
        } else return false,
        'p', 'k' => if (d.kind != .diff) {
            _ = ed.perform(.move_up);
        } else return false,
        else => return false,
    }
    return true;
}

/// A double click in a Diff tab opens the clicked line; in a Log tab, its commit.
pub fn onDoubleClick(ws: *Workspace, ed: *Editor) void {
    const i = ws.tabIndexOf(ed) orelse return;
    const d = ws.tabs.items[i].git_diff orelse return;
    switch (d.kind) {
        .diff => gotoDiffLine(ws, d, ed),
        .log => showLogCommit(ws, ed),
        .process => {},
    }
}

/// The panel header's ? button: the popup listing every key.
pub fn showHelp(ws: *Workspace) void {
    const p = ws.panel.source_control orelse return;
    _ = git_popup.open(p, '?');
    panel_ui.showSourceControl(ws);
}

/// A click in the view closes its popup.
pub fn onViewClick(ws: *Workspace) void {
    const p = ws.panel.source_control orelse return;
    if (p.popup != null) {
        p.popup = null;
        ws.dirty = true;
    }
}

/// A double click in the Source Control view visits what was clicked.
pub fn onViewDoubleClick(ws: *Workspace) void {
    const p = ws.panel.source_control orelse return;
    visit(p);
}

fn absPath(p: *Panel, buf: []u8, rel: []const u8) ?[]const u8 {
    const root = p.view.root orelse return null;
    return std.fmt.bufPrint(buf, "{s}/{s}", .{ root, rel }) catch null;
}

/// RET: opens the file (at the diff line's place), shows the commit or
/// stash, or folds the section.
fn visit(p: *Panel) void {
    const ws = p.ws;
    const row = caretRow(p.editor);
    const t = p.view.targetAt(row) orelse return;
    switch (t.kind) {
        .section => return toggle(p),
        .commit, .ref => return if (p.view.commit(t)) |c| showCommit(p, c.hash) else {},
        .stash => return if (p.view.stash(t)) |s| showStash(p, s.name) else {},
        else => {},
    }
    const e = p.view.entry(t) orelse return;
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const abs = absPath(p, &buf, e.path) orelse return;
    // A deleted file has nothing to open: show what was removed.
    if (Io.Dir.cwd().statFile(ws.io, abs, .{})) |_| {} else |_| {
        ws.setStatus("{s} is deleted; showing its diff", .{e.path});
        return diffAt(p, t);
    }
    const line: ?u32 = if (p.view.hunk(t)) |h| (if (t.kind == .line) status_mod.hunkLine(h, t.line) else @max(1, h.new_start)) else null;
    const target = ws.openFile(abs, false) catch |err| return ws.setStatus("Unable to open {s}: {t}", .{ e.path, err });
    if (line) |l| {
        const r = @min(l - 1, target.buffer.lineCount() - 1);
        target.setSingleSelection(.caret(target.buffer.lineStart(r)));
        target.afterCommand(true);
    }
    ws.focus = .editor;
}

// ---- Staging, unstaging, discarding --------------------------------------------------

const Act = enum { stage, unstage, discard };

/// The rows of the marked lines or of a selection (null: none).
fn regionRows(p: *Panel) ?[2]u32 {
    const ed = p.editor;
    // The mark's lines, its own included.
    if (p.mark) |m| {
        const r = caretRow(ed);
        return .{ @min(m, r), @max(m, r) };
    }
    const sel = ed.primarySelection();
    if (sel.isEmpty()) return null;
    const a = ed.buffer.offsetToPoint(sel.start());
    const b = ed.buffer.offsetToPoint(sel.end());
    var last = b.row;
    if (b.column == 0 and last > a.row) last -= 1;
    return .{ a.row, last };
}

/// s, u, x: on the marked lines, else on what's at the caret.
fn act(p: *Panel, what: Act) void {
    const ws = p.ws;
    if (p.view.status == null) return;
    if (regionRows(p)) |rows| return actRegion(p, what, rows);
    const t = p.view.targetAt(caretRow(p.editor)) orelse return ws.setStatus("Nothing to {t} here", .{what});
    switch (t.kind) {
        .section => if (t.section.hasFiles()) actFiles(p, what, t.section, &.{}) else if (what == .discard and t.section == .stashes) {
            ws.setStatus("Drop stashes one at a time (x on one)", .{});
        } else ws.setStatus("Nothing to {t} here", .{what}),
        .file, .note => actFiles(p, what, t.section, &.{t.index}),
        .hunk, .line => actHunk(p, what, t, null),
        .stash => if (what == .discard) git_popup.dropStash(p, p.view.stash(t).?.name) else ws.setStatus("Nothing to {t} here (z a applies a stash)", .{what}),
        .commit, .ref => ws.setStatus("Nothing to {t} here", .{what}),
    }
}

/// A region: lines of one hunk, or files of one section.
fn actRegion(p: *Panel, what: Act, rows: [2]u32) void {
    const ws = p.ws;
    var first: ?Item = null;
    var lines: [2]u32 = .{ std.math.maxInt(u32), 0 };
    var one_hunk = true;
    var files: std.ArrayList(u32) = .empty;
    defer files.deinit(ws.gpa);
    var one_section = true;
    var r = rows[0];
    while (r <= rows[1]) : (r += 1) {
        const t = p.view.targetAt(r) orelse continue;
        const f = first orelse blk: {
            first = t;
            break :blk t;
        };
        if (t.section != f.section) one_section = false;
        if ((t.kind != .line and t.kind != .hunk) or t.index != f.index or t.hunk != f.hunk or t.section != f.section) one_hunk = false;
        if (t.kind == .line) {
            lines[0] = @min(lines[0], t.line);
            lines[1] = @max(lines[1], t.line + 1);
        } else if (t.kind == .hunk) lines[0] = 0;
        if (t.kind == .file or t.kind == .note or t.kind == .hunk or t.kind == .line) {
            if (std.mem.indexOfScalar(u32, files.items, t.index) == null) files.append(ws.gpa, t.index) catch return;
        } else if (t.kind != .section) one_section = false;
    }
    const f = first orelse return ws.setStatus("Nothing to {t} here", .{what});
    if (one_hunk) {
        if (lines[1] == 0) lines[1] = std.math.maxInt(u32);
        return actHunk(p, what, f, lines);
    }
    if (one_section and f.section.hasFiles() and files.items.len > 0) return actFiles(p, what, f.section, files.items);
    ws.setStatus("Mark lines of one hunk, or files of one section", .{});
}

/// Paths of `indices` (all of the section's files if empty), NUL-separated
/// for `--pathspec-from-file`.
fn pathInput(p: *Panel, alloc: Allocator, section: Section, indices: []const u32) ![]const u8 {
    var input: std.ArrayList(u8) = .empty;
    const paths = if (indices.len == 0)
        try p.view.paths(alloc, .{ .kind = .section, .section = section })
    else blk: {
        var list: std.ArrayList([]const u8) = .empty;
        for (indices) |i| try list.appendSlice(alloc, try p.view.paths(alloc, .{ .kind = .file, .section = section, .index = i }));
        break :blk list.items;
    };
    for (paths) |path| {
        try input.appendSlice(alloc, path);
        try input.append(alloc, 0);
    }
    return input.items;
}

/// "src/a.zig", or "3 files".
fn describe(p: *Panel, buf: []u8, section: Section, indices: []const u32) []const u8 {
    if (indices.len == 1) if (p.view.entry(.{ .kind = .file, .section = section, .index = indices[0] })) |e| return e.path;
    if (indices.len > 0) return std.fmt.bufPrint(buf, "{d} files", .{indices.len}) catch "files";
    const st = p.status() orelse return "files";
    var n: usize = 0;
    var only: []const u8 = "";
    for (st.entries) |*e| if (status_mod.inSection(e, section)) {
        n += 1;
        only = e.path;
    };
    if (n == 1) return only;
    return std.fmt.bufPrint(buf, "{d} files", .{n}) catch "files";
}

/// s, u, x on files (`indices` into the entries; empty: the whole section).
fn actFiles(p: *Panel, what: Act, section: Section, indices: []const u32) void {
    const ws = p.ws;
    const st = p.status() orelse return;
    var name_buf: [std.fs.max_path_bytes]u8 = undefined;
    const name = describe(p, &name_buf, section, indices);
    const r = Request.create(ws.gpa) catch return;
    var keep = false;
    defer if (!keep) r.destroy(ws.gpa);
    const a = r.alloc();
    const input = pathInput(p, a, section, indices) catch return;
    if (input.len == 0) return;
    var question: []const u8 = "";
    switch (what) {
        .stage => {
            if (section == .staged) return ws.setStatus("Already staged (u unstages)", .{});
            r.argv = &.{ "--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul" };
            r.run = .{ .input = input, .label = a.dupe(u8, std.fmt.bufPrint(&name_buf, "Staging {s}", .{name}) catch "Staging") catch "Staging", .done = "Staged" };
        },
        .unstage => {
            if (section != .staged) return ws.setStatus("Not staged (s stages)", .{});
            r.argv = if (st.branch.initial())
                &.{ "--literal-pathspecs", "rm", "--cached", "-q", "-r", "--pathspec-from-file=-", "--pathspec-file-nul" }
            else
                &.{ "--literal-pathspecs", "reset", "-q", "--pathspec-from-file=-", "--pathspec-file-nul" };
            r.run = .{ .input = input, .label = a.dupe(u8, std.fmt.bufPrint(&name_buf, "Unstaging {s}", .{name}) catch "Unstaging") catch "Unstaging", .done = "Unstaged" };
        },
        .discard => switch (section) {
            .untracked => {
                var paths: std.ArrayList([]const u8) = .empty;
                var it = std.mem.splitScalar(u8, input[0 .. input.len - 1], 0);
                while (it.next()) |path| paths.append(a, path) catch return;
                r.delete = paths.items;
                question = std.fmt.allocPrint(a, "Delete {s} (untracked)?", .{name}) catch return;
            },
            .unstaged => {
                for (st.entries, 0..) |*e, i| {
                    if (e.kind != .unmerged or !status_mod.inSection(e, .unstaged)) continue;
                    if (indices.len == 0 or std.mem.indexOfScalar(u32, indices, @intCast(i)) != null) {
                        return ws.setStatus("{s} has conflicts: resolve them (or check out a side) first", .{e.path});
                    }
                }
                r.argv = &.{ "--literal-pathspecs", "checkout", "--pathspec-from-file=-", "--pathspec-file-nul" };
                r.run = .{ .input = input, .label = "Discarding", .done = "Discarded unstaged changes" };
                question = std.fmt.allocPrint(a, "Discard unstaged changes to {s}?", .{name}) catch return;
            },
            .staged => {
                if (indices.len == 0) return ws.setStatus("Discard staged files one at a time, or unstage them (U) first", .{});
                var added = false;
                var changed = false;
                var also_unstaged = false;
                for (indices) |i| {
                    const e = &st.entries[i];
                    if (e.x == 'R' or e.x == 'C') return ws.setStatus("{s} is a staged rename: unstage it (u), then discard", .{e.path});
                    if (e.x == 'A') added = true else changed = true;
                    if (e.y != '.') also_unstaged = true;
                }
                if (added and changed) return ws.setStatus("Discard new and changed files separately", .{});
                if (added) {
                    r.argv = &.{ "--literal-pathspecs", "rm", "-f", "-q", "--pathspec-from-file=-", "--pathspec-file-nul" };
                    question = std.fmt.allocPrint(a, "Delete {s} (a staged new file)?", .{name}) catch return;
                } else {
                    r.argv = &.{ "--literal-pathspecs", "checkout", "HEAD", "--pathspec-from-file=-", "--pathspec-file-nul" };
                    question = std.fmt.allocPrint(a, "Discard staged changes to {s}{s}?", .{ name, if (also_unstaged) " (and its unstaged changes)" else "" }) catch return;
                }
                r.run = .{ .input = input, .label = "Discarding", .done = "Discarded staged changes" };
            },
            else => return,
        },
    }
    keep = true;
    if (question.len > 0) return confirm(p, question, r);
    runRequest(p, r);
    r.destroy(ws.gpa);
}

/// s, u, x on a hunk, or on `lines` [first, end) of it.
fn actHunk(p: *Panel, what: Act, t: Item, lines: ?[2]u32) void {
    const ws = p.ws;
    const f = p.view.fileDiff(t.section, t.index) orelse return;
    const h = p.view.hunk(t) orelse return;
    if (f.combined) return ws.setStatus("Edit the conflict away, then stage the whole file (s on it) to mark it resolved", .{});
    const Plan = struct { reverse: bool, argv: []const []const u8, label: []const u8 };
    const plan: Plan = switch (t.section) {
        .unstaged => switch (what) {
            .stage => .{ .reverse = false, .argv = &.{ "apply", "--cached", "-" }, .label = "Staging" },
            .unstage => return ws.setStatus("Not staged (s stages)", .{}),
            .discard => .{ .reverse = true, .argv = &.{ "apply", "--reverse", "-" }, .label = "Discarding" },
        },
        .staged => switch (what) {
            .stage => return ws.setStatus("Already staged (u unstages)", .{}),
            .unstage => .{ .reverse = true, .argv = &.{ "apply", "--cached", "--reverse", "-" }, .label = "Unstaging" },
            .discard => .{ .reverse = true, .argv = &.{ "apply", "--index", "--reverse", "-" }, .label = "Discarding" },
        },
        else => return,
    };
    const r = Request.create(ws.gpa) catch return;
    const a = r.alloc();
    const range = lines orelse .{ 0, std.math.maxInt(u32) };
    const patch = (diff_mod.partialPatch(a, f, h, range[0], @min(range[1], h.lines.len), plan.reverse) catch null) orelse {
        r.destroy(ws.gpa);
        return ws.setStatus("No changes in the marked lines", .{});
    };
    const part = if (lines != null) "lines" else "hunk";
    r.argv = plan.argv;
    r.run = .{
        .input = patch,
        .label = std.fmt.allocPrint(a, "{s} {s} of {s}", .{ plan.label, part, f.path }) catch plan.label,
        .done = std.fmt.allocPrint(a, "{s} {s}", .{ switch (what) {
            .stage => "Staged",
            .unstage => "Unstaged",
            .discard => "Discarded",
        }, part }) catch "",
    };
    if (what == .discard) {
        const q = std.fmt.allocPrint(a, "Discard {s} of {s}?", .{ part, f.path }) catch return r.destroy(ws.gpa);
        return confirm(p, q, r);
    }
    runRequest(p, r);
    r.destroy(ws.gpa);
}

/// S: stages every change to tracked files (`git add -u`).
fn stageAll(p: *Panel) void {
    run(p, &.{ "add", "-u" }, .{ .label = "Staging all tracked changes", .done = "Staged all tracked changes" });
}

/// U: unstages everything.
fn unstageAll(p: *Panel) void {
    const st = p.status() orelse return;
    if (st.branch.initial()) {
        run(p, &.{ "rm", "--cached", "-r", "-q", "--", "." }, .{ .label = "Unstaging everything", .done = "Unstaged everything" });
    } else run(p, &.{ "reset", "-q" }, .{ .label = "Unstaging everything", .done = "Unstaged everything" });
}

// ---- Diffs, commits and logs ---------------------------------------------------------

/// The diff of what's at `t` (a file or hunk: its file; a section: all of
/// it) in the Diff tab.
pub fn diffAt(p: *Panel, t: Item) void {
    const ws = p.ws;
    switch (t.kind) {
        .commit, .ref => return if (p.view.commit(t)) |c| showCommit(p, c.hash) else {},
        .stash => return if (p.view.stash(t)) |s| showStash(p, s.name) else {},
        else => {},
    }
    if (!t.section.hasFiles()) return ws.setStatus("No diff here", .{});
    var title_buf: [160]u8 = undefined;
    const title = blk: {
        var w: std.Io.Writer = .fixed(&title_buf);
        if (p.view.entry(t)) |e| {
            w.print("Diff: {s}", .{std.fs.path.basename(e.path)}) catch {};
            if (t.section == .staged) w.writeAll(" (staged)") catch {};
        } else w.print("Diff: {s}", .{t.section.title()}) catch {};
        break :blk w.buffered();
    };

    // Untracked files: git won't diff them (without --no-index), so the
    // diff is made here.
    if (t.section == .untracked) {
        const e = p.view.entry(t) orelse return ws.setStatus("Untracked files have no diff", .{});
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const abs = absPath(p, &buf, e.path) orelse return;
        const contents = Io.Dir.cwd().readFileAlloc(ws.io, abs, ws.gpa, .limited(max_new_file_diff)) catch |err| {
            return ws.setStatus("Unable to read {s}: {t}", .{ e.path, err });
        };
        defer ws.gpa.free(contents);
        const d = status_mod.newFileDiff(ws.gpa, e.path, contents) catch return;
        defer ws.gpa.free(d);
        _ = showText(p, .diff, title, d);
        return;
    }

    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    var argv: std.ArrayList([]const u8) = .empty;
    argv.appendSlice(alloc, &.{ "--no-optional-locks", "--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/" }) catch return;
    if (t.section == .staged) argv.append(alloc, "--cached") catch return;
    argv.append(alloc, "--") catch return;
    if (t.kind == .section) {
        argv.appendSlice(alloc, p.view.paths(alloc, t) catch return) catch return;
    } else argv.appendSlice(alloc, p.view.paths(alloc, .{ .kind = .file, .section = t.section, .index = t.index }) catch return) catch return;
    run(p, argv.items, .{ .title = title, .after = .diff });
}

/// `git show` of a commit in the Diff tab.
pub fn showCommit(p: *Panel, rev: []const u8) void {
    var title_buf: [96]u8 = undefined;
    const title = std.fmt.bufPrint(&title_buf, "Commit {s}", .{rev}) catch "Commit";
    run(p, &.{ "--no-optional-locks", "show", "--no-color", "--no-ext-diff", "--format=fuller", "--stat", "-p", "--src-prefix=a/", "--dst-prefix=b/", rev, "--" }, .{ .title = title, .after = .diff });
}

pub fn showStash(p: *Panel, name: []const u8) void {
    var title_buf: [96]u8 = undefined;
    const title = std.fmt.bufPrint(&title_buf, "Stash {s}", .{name}) catch "Stash";
    run(p, &.{ "--no-optional-locks", "stash", "show", "-p", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", name }, .{ .title = title, .after = .diff });
}

/// Enter in a Log tab: the commit on the caret's line.
fn showLogCommit(ws: *Workspace, ed: *Editor) void {
    const p = ws.panel.source_control orelse return;
    const row = caretRow(ed);
    var buf: [512]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    var it = ed.buffer.rope.chunks(ed.buffer.lineStart(row), ed.buffer.lineEnd(row));
    while (it.next()) |chunk| w.writeAll(chunk[0..@min(chunk.len, w.buffer.len - w.end)]) catch break;
    const hash = status_mod.logHash(w.buffered()) orelse return ws.setStatus("No commit on this line", .{});
    showCommit(p, hash);
}

fn tabOfKind(ws: *Workspace, kind: Diff.Kind) ?usize {
    for (ws.tabs.items, 0..) |t, i| if (t.git_diff) |d| if (d.kind == kind) return i;
    return null;
}

/// Shows `text` in the tab of `kind` (created on first use), activated.
pub fn showText(p: *Panel, kind: Diff.Kind, title: []const u8, text: []const u8) ?*Editor {
    const ws = p.ws;
    const root = p.view.root orelse return null;
    const ed, const d = blk: {
        if (tabOfKind(ws, kind)) |i| {
            ws.activateTab(i);
            break :blk .{ ws.tabs.items[i].editor, ws.tabs.items[i].git_diff.? };
        }
        const d = ws.gpa.create(Diff) catch return null;
        d.* = .{ .gpa = ws.gpa, .kind = kind, .root = ws.gpa.dupe(u8, root) catch {
            ws.gpa.destroy(d);
            return null;
        } };
        const buffer = ws.gpa.create(Buffer) catch {
            d.destroy(null);
            return null;
        };
        buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
            ws.gpa.destroy(buffer);
            d.destroy(null);
            return null;
        };
        ws.next_buffer_id += 1;
        buffer.read_only = true;
        switch (kind) {
            .diff => if (languages.byName("Diff")) |lang| buffer.setLanguage(ws.registry, lang),
            .log => buffer.language = &languages.git_log,
            .process => {},
        }
        ws.buffers.append(ws.gpa, buffer) catch {
            buffer.deinit();
            ws.gpa.destroy(buffer);
            d.destroy(null);
            return null;
        };
        const ed = Editor.create(ws.gpa, ws.env, buffer) catch {
            d.destroy(null);
            return null;
        };
        if (kind != .diff) ed.plain = true;
        _ = ws.insertTab(.{ .editor = ed, .git_diff = d }) catch {
            ed.destroy();
            d.destroy(buffer);
            return null;
        };
        break :blk .{ ed, d };
    };
    // The repository may differ from the last tab's.
    if (!std.mem.eql(u8, d.root, root)) {
        const owned = ws.gpa.dupe(u8, root) catch return null;
        ws.gpa.free(d.root);
        d.root = owned;
    }
    const n = @min(title.len, d.title.len);
    @memcpy(d.title[0..n], title[0..n]);
    ed.buffer.display_name = d.title[0..n];
    setText(ed, text);
    ed.setSingleSelection(.caret(0));
    ed.scroll_y = 0;
    ws.focus = .editor;
    ws.updateTitle();
    ws.dirty = true;
    return ed;
}

/// Enter in a Diff tab: the file at the caret line's place.
fn gotoDiffLine(ws: *Workspace, d: *Diff, ed: *Editor) void {
    const text = ed.buffer.rope.toOwnedSlice(ws.gpa) catch return;
    defer ws.gpa.free(text);
    const loc = status_mod.diffLocation(text, caretRow(ed)) orelse return;
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const abs = std.fmt.bufPrint(&buf, "{s}/{s}", .{ d.root, loc.path }) catch return;
    if (Io.Dir.cwd().statFile(ws.io, abs, .{})) |_| {} else |_| {
        return ws.setStatus("{s} no longer exists", .{loc.path});
    }
    const target = ws.openFile(abs, false) catch |err| return ws.setStatus("Unable to open {s}: {t}", .{ loc.path, err });
    const row = @min(loc.line -| 1, target.buffer.lineCount() - 1);
    target.setSingleSelection(.caret(target.buffer.lineStart(row)));
    target.afterCommand(true);
}
