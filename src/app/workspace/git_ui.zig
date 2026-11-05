//! Workspace side of git blame: one `Blame` per buffer shown with blame
//! (column or inline), re-run after saves, reloads and commits; the commit
//! popup (in the LSP hover popup) on hover, click or command; and the Git
//! commands.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const blame_view = @import("../editor/blame_view.zig");
const lsp_ui = @import("lsp_ui.zig");
const keymap = @import("../settings/keymap.zig");
const platform = @import("../../framework/platform/platform.zig");
const git = @import("../../model/git/git.zig");
const blame_mod = @import("../../model/git/blame.zig");
const scm_state = @import("scm_state.zig");
const quick_diff_mod = @import("../editor/quick_diff.zig");
const rpath = @import("../../model/remote/path.zig");

const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Blame = blame_mod.Blame;

/// Mouse rest over a blame entry before its commit pops up.
const hover_delay_ms = 400;
/// How often the repositories of visible blames are checked for commits.
const repo_poll_ms = 1000;

const Popup = struct {
    editor: *Editor,
    blame: *Blame,
    hash: [64]u8 = undefined,
    hash_len: usize = 0,
    row: u32,
    at_mouse: bool,
    mouse: platform.Vec2,
};

pub const State = struct {
    runner: ?git.Runner = null,
    blames: std.ArrayList(*Blame) = .empty,
    /// Buffer `saved_version` seen per blame (a change means saved or reloaded).
    saved: std.AutoHashMapUnmanaged(*Blame, u64) = .empty,
    last_poll: i64 = 0,
    rest_pos: platform.Vec2 = .{},
    rest_since: i64 = 0,
    rest_done: bool = true,
    /// A popup waiting for `git show`.
    popup: ?Popup = null,
    show_job: ?*git.Job = null,
    /// The repository's status, decorations and commands (scm_state.zig).
    scm: scm_state.State = .{},
    /// The change markers of open files (editors point at them).
    quick: std.ArrayList(*quick_diff_mod.QuickDiff) = .empty,
};

pub fn deinit(ws: *Workspace) void {
    scm_state.deinit(ws);
    const st = &ws.git_state;
    for (ws.tabs.items) |t| t.editor.quick_diff = null;
    for (st.quick.items) |q| q.destroy();
    st.quick.deinit(ws.gpa);
    if (st.show_job) |j| j.detach();
    for (st.blames.items) |b| b.destroy();
    st.blames.deinit(ws.gpa);
    st.saved.deinit(ws.gpa);
    if (st.runner) |*r| r.deinit();
}

pub fn runner(ws: *Workspace) *git.Runner {
    const st = &ws.git_state;
    if (st.runner == null) st.runner = git.Runner.init(ws.gpa, ws.io, ws.executor);
    return &st.runner.?;
}

fn onUpdate(ctx: *anyopaque, b: *Blame) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    ws.dirty = true;
    // The blame column was asked for, but there's nothing to show.
    if (ws.activeEditor()) |ed| if (ed.blame == b and ed.show_blame and !b.hasData() and !b.busy()) {
        ws.setStatus("No git blame for {s}", .{ed.buffer.fileName() orelse "this buffer"});
    };
}

fn blameFor(ws: *Workspace, buffer: *const editor_mod.Buffer) ?*Blame {
    for (ws.git_state.blames.items) |b| if (b.isFor(buffer)) return b;
    return null;
}

fn wantsBlame(ws: *Workspace, ed: *const Editor) bool {
    return ed.mode == .full and ed.buffer.path != null and (ed.show_blame or ws.env.settings.git_blame_inline);
}

fn dropBlame(ws: *Workspace, index: usize) void {
    const st = &ws.git_state;
    const b = st.blames.swapRemove(index);
    for (ws.tabs.items) |t| if (t.editor.blame == b) {
        t.editor.blame = null;
    };
    if (st.popup) |p| if (p.blame == b) {
        st.popup = null;
    };
    _ = st.saved.remove(b);
    b.destroy();
}

/// The blame of `ed`'s buffer, started if there is none yet.
fn ensureBlame(ws: *Workspace, ed: *Editor) ?*Blame {
    const st = &ws.git_state;
    if (blameFor(ws, ed.buffer)) |b| return b;
    if (ed.mode != .full or ed.buffer.path == null) return null;
    const b = Blame.create(runner(ws), ed.buffer, .{ .ctx = ws, .onUpdate = onUpdate }) catch return null;
    st.blames.append(ws.gpa, b) catch {
        b.destroy();
        return null;
    };
    st.saved.put(ws.gpa, b, ed.buffer.saved_version) catch {};
    for (ws.tabs.items) |t| if (t.editor.buffer == ed.buffer) {
        t.editor.blame = b;
    };
    b.refresh();
    return b;
}

// ---- Change markers -----------------------------------------------------------------

fn quickFor(ws: *Workspace, buffer: *const editor_mod.Buffer) ?*quick_diff_mod.QuickDiff {
    for (ws.git_state.quick.items) |q| if (q.buffer == buffer) return q;
    return null;
}

fn isUnder(path: []const u8, root: []const u8) bool {
    if (!std.mem.startsWith(u8, path, root)) return false;
    return path.len > root.len and path[root.len] == '/';
}

fn onOriginal(ctx: *anyopaque, job: *git.Job) void {
    const q: *quick_diff_mod.QuickDiff = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    q.job = null;
    // A file git doesn't know (untracked) has no original: no markers.
    q.setOriginal(job.output);
}

/// Gives the visible file editors of the repository their change markers,
/// reads each one's original again when the repository changed, and
/// recomputes the marks once edits have paused.
fn syncQuickDiffs(ws: *Workspace) void {
    const st = &ws.git_state;
    var i: usize = 0;
    while (i < st.quick.items.len) {
        const q = st.quick.items[i];
        const alive = for (ws.buffers.items) |b| {
            if (b == q.buffer) break if (b.path) |p| std.mem.eql(u8, p, q.path) else false;
        } else false;
        if (alive) {
            i += 1;
            continue;
        }
        for (ws.tabs.items) |t| if (t.editor.quick_diff == q) {
            t.editor.quick_diff = null;
        };
        _ = st.quick.swapRemove(i);
        q.destroy();
    }
    const scm = &st.scm;
    const root = scm.root orelse return;
    for (ws.group_active[0..ws.groupCount()]) |maybe| {
        const ed = maybe orelse continue;
        if (ed.mode != .full or ed.plain) continue;
        const path = ed.buffer.path orelse continue;
        if (rpath.isRemote(path) or !isUnder(path, root) or quickFor(ws, ed.buffer) != null) continue;
        const q = quick_diff_mod.QuickDiff.create(ws.gpa, ed.buffer, path) catch continue;
        st.quick.append(ws.gpa, q) catch {
            q.destroy();
            continue;
        };
    }
    const now = platform.nowMs();
    for (ws.tabs.items) |t| t.editor.quick_diff = quickFor(ws, t.editor.buffer);
    for (st.quick.items) |q| {
        if (q.job == null and q.generation != scm.generation and scm.snapshot != null and q.path.len > root.len) {
            q.generation = scm.generation;
            var arg_buf: [std.fs.max_path_bytes + 1]u8 = undefined;
            if (std.fmt.bufPrint(&arg_buf, ":{s}", .{q.path[root.len + 1 ..]})) |arg| {
                q.job = git.Job.start(runner(ws), &.{ "show", arg }, root, null, null, q, onOriginal) catch null;
            } else |_| {}
        }
        if (q.update(now)) ws.dirty = true;
    }
}

/// Matches blames to open buffers and views, starting runs where needed.
fn sync(ws: *Workspace) void {
    const st = &ws.git_state;
    // Buffers closed or saved under another name lose their blame.
    var i: usize = 0;
    while (i < st.blames.items.len) {
        const b = st.blames.items[i];
        const alive = for (ws.buffers.items) |buf| {
            if (b.isFor(buf)) break if (buf.path) |p| std.mem.eql(u8, p, b.path) else false;
        } else false;
        if (alive) i += 1 else dropBlame(ws, i);
    }
    // Visible views that show blame get one.
    for (ws.group_active[0..ws.groupCount()]) |maybe| {
        const ed = maybe orelse continue;
        if (wantsBlame(ws, ed)) _ = ensureBlame(ws, ed);
    }
    for (ws.tabs.items) |t| t.editor.blame = blameFor(ws, t.editor.buffer);
    // Saves and reloads from disk re-run blame; so do missed changes.
    for (st.blames.items) |b| {
        const gop = st.saved.getOrPut(ws.gpa, b) catch continue;
        if (!gop.found_existing or gop.value_ptr.* != b.buffer.saved_version) {
            const first = !gop.found_existing;
            gop.value_ptr.* = b.buffer.saved_version;
            if (!first) b.refresh();
        }
        if (b.stale) b.refresh();
    }
}

/// Re-runs visible blames whose repository changed (a commit in a terminal).
fn pollRepos(ws: *Workspace) void {
    const st = &ws.git_state;
    const now = platform.nowMs();
    if (now - st.last_poll < repo_poll_ms) return;
    st.last_poll = now;
    for (ws.group_active[0..ws.groupCount()]) |maybe| {
        const ed = maybe orelse continue;
        const b = ed.blame orelse continue;
        if (b.repoChanged()) b.refresh();
    }
}

pub fn tick(ws: *Workspace) void {
    scm_state.tick(ws);
    syncQuickDiffs(ws);
    sync(ws);
    pollRepos(ws);
    const st = &ws.git_state;
    // A click on the blame column.
    for (ws.group_active[0..ws.groupCount()]) |maybe| {
        const ed = maybe orelse continue;
        const row = ed.blame_clicked orelse continue;
        ed.blame_clicked = null;
        openPopup(ws, ed, row, true, ws.mouse);
    }
    // The mouse resting on a blame entry.
    const now = platform.nowMs();
    if (ws.mouse.x != st.rest_pos.x or ws.mouse.y != st.rest_pos.y) {
        st.rest_pos = ws.mouse;
        st.rest_since = now;
        st.rest_done = false;
    } else if (!st.rest_done and now - st.rest_since >= hover_delay_ms) {
        st.rest_done = true;
        if (ws.overlay == null and !ws.menuIsOpen()) if (ws.activeEditor()) |ed| {
            if (blame_view.entryAt(ed, st.rest_pos)) |row| openPopup(ws, ed, row, true, st.rest_pos);
        };
    }
    // Inline blame that just became due needs a frame.
    if (ws.activeEditor()) |ed| if (blame_view.inlineDueIn(ed)) |ms| {
        if (ms == 0) ws.dirty = true;
    };
}

/// Time until the next blame timer (inline blame, hover popup).
pub fn nextTimer(ws: *Workspace) ?i64 {
    const st = &ws.git_state;
    var soonest: ?i64 = scm_state.nextTimer(ws);
    {
        const now = platform.nowMs();
        for (st.quick.items) |q| if (q.dueIn(now)) |ms| {
            soonest = if (soonest) |s| @min(s, ms) else ms;
        };
    }
    if (ws.activeEditor()) |ed| if (blame_view.inlineDueIn(ed)) |ms| {
        soonest = if (soonest) |s| @min(s, @max(1, ms)) else @max(1, ms);
    };
    if (!st.rest_done) if (ws.activeEditor()) |ed| if (ed.blame != null) {
        const ms = @max(1, st.rest_since + hover_delay_ms - platform.nowMs());
        soonest = if (soonest) |s| @min(s, ms) else ms;
    };
    return soonest;
}

// ---- Commit popup -----------------------------------------------------------------

fn showPopup(ws: *Workspace, ed: *Editor, row: u32, text: []const u8, at_mouse: bool, mouse: platform.Vec2) void {
    lsp_ui.closeHover(ws);
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    const owned = arena.allocator().dupe(u8, text) catch {
        arena.deinit();
        return;
    };
    const offset = if (at_mouse) ed.buffer.lineStart(row) else ed.primarySelection().head;
    ws.lsp_state.hover = .{ .arena = arena, .editor = ed, .offset = offset, .text = owned, .at_mouse = at_mouse, .mouse = mouse };
    ws.dirty = true;
}

fn openPopup(ws: *Workspace, ed: *Editor, row: u32, at_mouse: bool, mouse: platform.Vec2) void {
    const st = &ws.git_state;
    const blame = ed.blame orelse return;
    const commit = blame.commitAt(row) orelse return;
    if (commit.isUncommitted()) {
        showPopup(ws, ed, row, "Uncommitted changes\n\nThis line differs from the last commit.", at_mouse, mouse);
        return;
    }
    if (blame.details.get(commit.hash)) |text| {
        showPopup(ws, ed, row, text, at_mouse, mouse);
        return;
    }
    if (st.show_job) |j| j.detach();
    st.show_job = null;
    var p: Popup = .{ .editor = ed, .blame = blame, .row = row, .at_mouse = at_mouse, .mouse = mouse };
    p.hash_len = @min(commit.hash.len, p.hash.len);
    @memcpy(p.hash[0..p.hash_len], commit.hash[0..p.hash_len]);
    st.popup = p;
    st.show_job = blame.fetchDetails(commit.hash, ws, onDetails);
}

fn onDetails(ctx: *anyopaque, job: *git.Job) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    const st = &ws.git_state;
    defer job.destroy();
    st.show_job = null;
    const p = st.popup orelse return;
    st.popup = null;
    // The blame and its view may have gone meanwhile.
    if (std.mem.indexOfScalar(*Blame, st.blames.items, p.blame) == null) return;
    if (ws.activeEditor() != p.editor or p.editor.blame != p.blame) return;
    const output = job.output orelse {
        ws.setStatus("git show failed for {s}", .{p.hash[0..@min(p.hash_len, 7)]});
        return;
    };
    const text = p.blame.storeDetails(p.hash[0..p.hash_len], output) orelse return;
    // A hover popup only if the mouse is still there.
    if (p.at_mouse and (@abs(ws.mouse.x - p.mouse.x) > 3 or @abs(ws.mouse.y - p.mouse.y) > 3)) return;
    showPopup(ws, p.editor, p.row, text, p.at_mouse, p.mouse);
}

// ---- Commands -----------------------------------------------------------------------

fn caretCommit(ws: *Workspace, ed: *Editor) ?*const blame_mod.Commit {
    const name = ed.buffer.fileName() orelse "this buffer";
    const blame = ensureBlame(ws, ed) orelse {
        ws.setStatus("No git blame for {s}", .{name});
        return null;
    };
    if (!blame.hasData()) {
        ws.setStatus("{s}", .{if (blame.busy()) "Git blame is still running" else "No git blame for this file"});
        return null;
    }
    const row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    return blame.commitAt(row) orelse {
        ws.setStatus("No commit for this line", .{});
        return null;
    };
}

pub fn perform(ws: *Workspace, action: keymap.Action) void {
    switch (action) {
        .git_toggle_blame => {
            const ed = ws.activeEditor() orelse return;
            ed.show_blame = !ed.show_blame;
            if (!ed.show_blame) return;
            const b = ensureBlame(ws, ed);
            if (b == null or (!b.?.busy() and !b.?.hasData())) {
                ws.setStatus("No git blame for {s}", .{ed.buffer.fileName() orelse "this buffer"});
            }
        },
        .git_toggle_inline_blame => {
            const s = ws.env.settings;
            s.git_blame_inline = !s.git_blame_inline;
            ws.setStatus("Inline git blame {s}", .{if (s.git_blame_inline) "on" else "off"});
            sync(ws);
        },
        .git_show_commit => {
            const ed = ws.activeEditor() orelse return;
            _ = caretCommit(ws, ed) orelse return;
            openPopup(ws, ed, ed.buffer.offsetToPoint(ed.primarySelection().head).row, false, .{});
        },
        .git_copy_commit_hash => {
            const ed = ws.activeEditor() orelse return;
            const commit = caretCommit(ws, ed) orelse return;
            if (commit.isUncommitted()) {
                ws.setStatus("This line isn't committed yet", .{});
                return;
            }
            ws.env.clipboard.set(ws.env.clipboard.ctx, commit.hash);
            ws.setStatus("Copied commit hash {s}", .{commit.shortHash()});
        },
        else => {},
    }
}
