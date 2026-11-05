//! The repository the workspace looks at, kept current without any view
//! being open: the Explorer's decorations (a colour and a letter per
//! changed file, a dot on the folders holding changes), the status bar's
//! branch with its ahead / behind counts, and what the Source Control dock
//! lists. One `git status` runs at a time (the next is queued behind it);
//! saves, changes on disk, and changes of the index or HEAD made by any
//! other program start one. Commands the dock runs (stage, commit, push…)
//! are `run`: they refresh when they end and report what git said when they
//! fail. The repository is the project's, else the active file's.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const git_ui = @import("git_ui.zig");
const git = @import("../../model/git/git.zig");
const status_mod = @import("../../model/git/status.zig");
const rpath = @import("../../model/remote/path.zig");
const platform = @import("../../framework/platform/platform.zig");
const Color = @import("../../framework/gpu/scene.zig").Color;
const theme_mod = @import("../../framework/ui/theme.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Status = status_mod.Status;

/// How often the index and HEAD are checked for changes made elsewhere.
const poll_ms = 1000;

/// What changed in a file, least important first (a folder takes the most
/// important of what is inside it).
pub const Kind = enum(u8) { untracked, renamed, added, modified, deleted, conflict };

pub const Decoration = struct {
    /// The letter after the name; 0 for a folder (a dot).
    letter: u8,
    kind: Kind,
};

/// What the last `git status` found, with the Explorer's lookups built from it.
pub const Snapshot = struct {
    arena: std.heap.ArenaAllocator,
    status: Status,
    root: []const u8,
    /// Absolute path → decoration, for changed files.
    files: std.StringHashMapUnmanaged(Decoration) = .empty,
    /// Absolute path of a folder → the most important change inside it.
    folders: std.StringHashMapUnmanaged(Kind) = .empty,

    pub fn deinit(self: *Snapshot) void {
        self.status.deinit();
        self.arena.deinit();
    }
};

/// A command the dock started.
const Op = struct {
    ws: *Workspace,
    job: *git.Job,
    label: []u8,
    done: []u8,
};

pub const State = struct {
    /// The folder `rev-parse` was last asked about, and its answer.
    search_dir: ?[]u8 = null,
    root: ?[]u8 = null,
    git_dir: ?[]u8 = null,
    /// git isn't installed, or the folder isn't in a repository.
    unavailable: bool = false,
    snapshot: ?*Snapshot = null,
    /// Bumped whenever a new snapshot replaces the last (and when the
    /// repository changes), so painters know to look again.
    generation: u32 = 0,
    job: ?*git.Job = null,
    /// What `job` is: looking for the repository, or its status.
    job_is_status: bool = false,
    stale: bool = true,
    stamp: i128 = 0,
    saved_sig: u64 = 0,
    last_poll: i64 = 0,
    ops: std.ArrayList(*Op) = .empty,
    /// What the running commands are called ("Pushing"), for the dock's header.
    busy: ?[]const u8 = null,
    /// The commit message being written in the dock.
    message: std.ArrayList(u8) = .empty,

    pub fn deinitState(self: *State, gpa: Allocator) void {
        if (self.job) |j| j.detach();
        self.job = null;
        for (self.ops.items) |op| {
            op.job.detach();
            gpa.free(op.label);
            gpa.free(op.done);
            gpa.destroy(op);
        }
        self.ops.deinit(gpa);
        if (self.snapshot) |s| {
            s.deinit();
            gpa.destroy(s);
        }
        if (self.search_dir) |d| gpa.free(d);
        if (self.root) |d| gpa.free(d);
        if (self.git_dir) |d| gpa.free(d);
        self.message.deinit(gpa);
    }
};

pub fn deinit(ws: *Workspace) void {
    ws.git_state.scm.deinitState(ws.gpa);
}

pub fn state(ws: *Workspace) *State {
    return &ws.git_state.scm;
}

// ---- Looking for the repository ---------------------------------------------------

/// The folder to ask git about: the project's, else the active file's.
fn candidate(ws: *Workspace) ?[]const u8 {
    if (ws.project) |p| return if (rpath.isRemote(p.root)) null else p.root;
    if (ws.activeIndex()) |i| if (ws.tabs.items[i].editor.buffer.path) |path| {
        if (rpath.isRemote(path)) return null;
        return std.fs.path.dirname(path);
    };
    return null;
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

fn forget(ws: *Workspace, st: *State) void {
    const gpa = ws.gpa;
    if (st.snapshot) |s| {
        s.deinit();
        gpa.destroy(s);
    }
    st.snapshot = null;
    if (st.root) |r| gpa.free(r);
    st.root = null;
    if (st.git_dir) |d| gpa.free(d);
    st.git_dir = null;
    st.generation +%= 1;
}

fn findRepository(ws: *Workspace, st: *State, dir: []const u8) void {
    const gpa = ws.gpa;
    if (st.search_dir) |d| gpa.free(d);
    st.search_dir = gpa.dupe(u8, dir) catch null;
    forget(ws, st);
    st.unavailable = false;
    st.job_is_status = false;
    st.job = git.Job.start(git_ui.runner(ws), &.{ "rev-parse", "--show-toplevel", "--absolute-git-dir" }, dir, null, null, ws, onRevParse) catch {
        st.unavailable = true;
        return;
    };
}

fn onRevParse(ctx: *anyopaque, job: *git.Job) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    const st = state(ws);
    st.job = null;
    const out = job.output orelse {
        st.unavailable = true;
        ws.dirty = true;
        return;
    };
    var lines = std.mem.tokenizeAny(u8, out, "\r\n");
    const root = lines.next() orelse "";
    const git_dir = lines.next() orelse "";
    if (root.len == 0) {
        st.unavailable = true;
        return;
    }
    st.root = ws.gpa.dupe(u8, root) catch return;
    st.git_dir = ws.gpa.dupe(u8, git_dir) catch null;
    st.stale = true;
    st.generation +%= 1;
    startStatus(ws);
}

// ---- Status -----------------------------------------------------------------------

/// The decoration of a changed file: the work tree's letter, else the index's.
pub fn decorationOf(e: *const status_mod.Entry) ?Decoration {
    switch (e.kind) {
        .ignored => return null,
        .untracked => return .{ .letter = 'U', .kind = .untracked },
        .unmerged => return .{ .letter = '!', .kind = .conflict },
        .changed, .renamed => {
            const letter = if (e.y != '.') e.y else e.x;
            const kind: Kind = switch (letter) {
                'A' => .added,
                'D' => .deleted,
                'R', 'C' => .renamed,
                else => .modified,
            };
            return .{ .letter = letter, .kind = kind };
        },
    }
}

/// Fills `snap`'s lookups from its status: each changed file, and each
/// folder above one (up to the repository root).
pub fn buildDecorations(snap: *Snapshot) !void {
    const alloc = snap.arena.allocator();
    for (snap.status.entries) |*e| {
        const d = decorationOf(e) orelse continue;
        const abs = try std.fmt.allocPrint(alloc, "{s}/{s}", .{ snap.root, std.mem.trimEnd(u8, e.path, "/") });
        try snap.files.put(alloc, abs, d);
        var dir = std.fs.path.dirname(abs);
        while (dir) |dd| {
            if (dd.len <= snap.root.len) break;
            const gop = try snap.folders.getOrPut(alloc, dd);
            if (!gop.found_existing or @intFromEnum(gop.value_ptr.*) < @intFromEnum(d.kind)) gop.value_ptr.* = d.kind;
            dir = std.fs.path.dirname(dd);
        }
    }
}

fn parseStatus(job: *git.Job) void {
    const gpa = job.runner.gpa;
    const snap = gpa.create(Snapshot) catch return;
    snap.* = .{ .arena = .init(gpa), .status = undefined, .root = "" };
    snap.status = status_mod.parse(gpa, job.output.?) catch {
        snap.arena.deinit();
        gpa.destroy(snap);
        return;
    };
    snap.root = snap.arena.allocator().dupe(u8, job.cwd) catch {
        snap.status.deinit();
        snap.arena.deinit();
        gpa.destroy(snap);
        return;
    };
    buildDecorations(snap) catch {
        snap.status.deinit();
        snap.arena.deinit();
        gpa.destroy(snap);
        return;
    };
    job.payload = snap;
}

fn freeSnapshot(job: *git.Job) void {
    const snap: *Snapshot = @ptrCast(@alignCast(job.payload.?));
    snap.deinit();
    job.runner.gpa.destroy(snap);
}

/// Runs `git status` (queued behind one that's running).
pub fn startStatus(ws: *Workspace) void {
    const st = state(ws);
    st.stale = false;
    if (st.job != null) {
        st.stale = true;
        return;
    }
    const root = st.root orelse return;
    st.stamp = if (st.git_dir) |d| git.repoStamp(ws.io, d) else 0;
    st.saved_sig = savedSignature(ws, root);
    st.job_is_status = true;
    st.job = git.Job.start(git_ui.runner(ws), &status_mod.status_argv, root, null, parseStatus, ws, onStatus) catch {
        st.unavailable = true;
        return;
    };
    st.job.?.free_payload = freeSnapshot;
}

fn onStatus(ctx: *anyopaque, job: *git.Job) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    const st = state(ws);
    st.job = null;
    if (job.payload) |ptr| {
        job.payload = null;
        const snap: *Snapshot = @ptrCast(@alignCast(ptr));
        if (st.snapshot) |old| {
            old.deinit();
            ws.gpa.destroy(old);
        }
        st.snapshot = snap;
        st.generation +%= 1;
        ws.dirty = true;
    }
    if (st.stale) startStatus(ws);
}

/// A file changed on disk (from the file watcher).
pub fn onDiskChange(ws: *Workspace, path: []const u8) void {
    const st = state(ws);
    const root = st.root orelse return;
    if (!isUnder(path, root)) return;
    // Git's own bookkeeping churns on every command; the index and HEAD are
    // checked by their stamp instead.
    if (std.mem.indexOf(u8, path, "/.git/") != null) return;
    st.stale = true;
}

/// Looks for the repository when the folder changes, and refreshes after
/// saves, and changes of the index or HEAD made by any program.
pub fn tick(ws: *Workspace) void {
    const st = state(ws);
    const dir = candidate(ws) orelse return;
    const searched = if (st.search_dir) |d| std.mem.eql(u8, d, dir) else false;
    if (!searched and st.job == null) return findRepository(ws, st, dir);
    const root = st.root orelse return;
    if (st.job != null) return;
    const now = platform.nowMs();
    if (now - st.last_poll >= poll_ms) {
        st.last_poll = now;
        if (st.git_dir) |d| if (git.repoStamp(ws.io, d) != st.stamp) {
            st.stale = true;
        };
    }
    if (savedSignature(ws, root) != st.saved_sig) st.stale = true;
    if (st.stale) startStatus(ws);
}

pub fn nextTimer(ws: *Workspace) ?i64 {
    const st = state(ws);
    if (st.root == null) return null;
    return @max(1, st.last_poll + poll_ms - platform.nowMs());
}

// ---- What the painters ask ----------------------------------------------------------

/// The decoration of `abs` (a file's, or a folder's dot).
pub fn decoration(ws: *Workspace, abs: []const u8) ?Decoration {
    const snap = state(ws).snapshot orelse return null;
    if (snap.files.get(abs)) |d| return d;
    if (snap.folders.get(abs)) |k| return .{ .letter = 0, .kind = k };
    return null;
}

fn isDark(theme: *const theme_mod.Theme) bool {
    const c = theme.background;
    return c.r * 0.299 + c.g * 0.587 + c.b * 0.114 < 0.5;
}

/// VS Code's gitDecoration colours.
pub fn kindColor(theme: *const theme_mod.Theme, kind: Kind) Color {
    const dark = isDark(theme);
    return Color.hex(switch (kind) {
        .modified => if (dark) 0xe2c08d else 0x895503,
        .added => if (dark) 0x81b88b else 0x587c0c,
        .deleted => if (dark) 0xc74e39 else 0xad0707,
        .renamed, .untracked => if (dark) 0x73c991 else 0x007100,
        .conflict => if (dark) 0xe4676b else 0xad0707,
    });
}

/// The status bar's branch: "main", "main*" with changes, and the counts
/// ahead of and behind the upstream.
pub fn branchLabel(ws: *Workspace, buf: []u8) ?[]const u8 {
    const snap = state(ws).snapshot orelse return null;
    const b = snap.status.branch;
    var dirty = false;
    for (snap.status.entries) |e| if (e.kind != .ignored) {
        dirty = true;
        break;
    };
    var w: std.Io.Writer = .fixed(buf);
    if (b.head) |h| w.writeAll(h) catch return null else w.print("({s})", .{b.oid[0..@min(7, b.oid.len)]}) catch return null;
    if (dirty) w.writeByte('*') catch return null;
    if (b.has_ab and (b.ahead > 0 or b.behind > 0)) {
        w.writeAll("  ") catch {};
        if (b.behind > 0) w.print("\u{2193}{d}", .{b.behind}) catch {};
        if (b.ahead > 0) w.print("{s}\u{2191}{d}", .{ if (b.behind > 0) " " else "", b.ahead }) catch {};
    }
    return w.buffered();
}

// ---- Commands -----------------------------------------------------------------------

pub const RunOptions = struct {
    input: ?[]const u8 = null,
    /// The dock's header while it runs: "Pushing".
    label: []const u8 = "",
    /// Said when it succeeds.
    done: []const u8 = "",
    /// Network commands: git may not prompt on a terminal or open an editor.
    batch: bool = false,
};

/// Starts `git argv...` in the repository; when it ends the status is
/// refreshed, and a failure is said in the status bar.
pub fn run(ws: *Workspace, argv: []const []const u8, o: RunOptions) void {
    const st = state(ws);
    const root = st.root orelse return;
    const gpa = ws.gpa;
    const op = gpa.create(Op) catch return;
    op.* = .{ .ws = ws, .job = undefined, .label = gpa.dupe(u8, o.label) catch &.{}, .done = gpa.dupe(u8, o.done) catch &.{} };
    op.job = git.Job.startWith(git_ui.runner(ws), argv, root, .{ .input = o.input, .capture = true, .batch = o.batch }, null, op, onOp) catch |err| {
        gpa.free(op.label);
        gpa.free(op.done);
        gpa.destroy(op);
        if (err == error.GitMissing) ws.setStatus("Git isn't installed (no git on PATH).", .{});
        return;
    };
    st.ops.append(gpa, op) catch {};
    if (o.label.len > 0) st.busy = op.label;
    ws.dirty = true;
}

fn onOp(ctx: *anyopaque, job: *git.Job) void {
    const op: *Op = @ptrCast(@alignCast(ctx));
    const ws = op.ws;
    const gpa = ws.gpa;
    defer {
        job.destroy();
        const st = state(ws);
        for (st.ops.items, 0..) |o, i| if (o == op) {
            _ = st.ops.orderedRemove(i);
            break;
        };
        st.busy = null;
        for (st.ops.items) |o| if (o.label.len > 0) {
            st.busy = o.label;
        };
        gpa.free(op.label);
        gpa.free(op.done);
        gpa.destroy(op);
    }
    if (job.failed or job.output == null) {
        const msg = errorLine(job);
        ws.setStatus("Git: {s}", .{msg});
    } else if (op.done.len > 0) {
        ws.setStatus("{s}", .{op.done});
    }
    const st = state(ws);
    st.stale = true;
    ws.dirty = true;
}

/// The first line of what git said went wrong: its stderr, else a refusal it printed on stdout.
pub fn errorLine(job: *const git.Job) []const u8 {
    const lines = [_]?[]const u8{ job.stderr, job.output };
    for (lines) |maybe| {
        const text = maybe orelse continue;
        var it = std.mem.tokenizeAny(u8, text, "\r\n");
        while (it.next()) |l| {
            const t = std.mem.trim(u8, l, " \t");
            if (t.len > 0) return t[0..@min(t.len, 240)];
        }
    }
    return "the command failed";
}

// ---- Tests --------------------------------------------------------------------------

const testing = std.testing;

fn testSnapshot(output: []const u8, root: []const u8) !Snapshot {
    var snap: Snapshot = .{ .arena = .init(testing.allocator), .status = undefined, .root = root };
    errdefer snap.arena.deinit();
    snap.status = try status_mod.parse(testing.allocator, output);
    errdefer snap.status.deinit();
    try buildDecorations(&snap);
    return snap;
}

test "decorations: a letter and a kind per file, the work tree's letter first" {
    var snap = try testSnapshot(
        "# branch.oid abc\x00" ++
            "1 .M N... 100644 100644 100644 a b src/main.zig\x00" ++
            "1 A. N... 000000 100644 100644 a b src/new.zig\x00" ++
            "1 MM N... 100644 100644 100644 a b both.zig\x00" ++
            "1 .D N... 100644 100644 000000 a b gone.zig\x00" ++
            "? notes.txt\x00" ++
            "u UU N... 1 2 3 4 a b c clash.zig\x00",
        "/repo",
    );
    defer snap.deinit();
    const f = &snap.files;
    try testing.expectEqual(Decoration{ .letter = 'M', .kind = .modified }, f.get("/repo/src/main.zig").?);
    try testing.expectEqual(Decoration{ .letter = 'A', .kind = .added }, f.get("/repo/src/new.zig").?);
    try testing.expectEqual(Decoration{ .letter = 'M', .kind = .modified }, f.get("/repo/both.zig").?);
    try testing.expectEqual(Decoration{ .letter = 'D', .kind = .deleted }, f.get("/repo/gone.zig").?);
    try testing.expectEqual(Decoration{ .letter = 'U', .kind = .untracked }, f.get("/repo/notes.txt").?);
    try testing.expectEqual(Decoration{ .letter = '!', .kind = .conflict }, f.get("/repo/clash.zig").?);
    try testing.expect(f.get("/repo/src") == null);
}

test "folders take the most important change inside them, up to the root" {
    var snap = try testSnapshot(
        "1 .M N... 100644 100644 100644 a b a/b/c/deep.zig\x00" ++
            "? a/b/new.txt\x00" ++
            "1 .D N... 100644 100644 000000 a b a/x/gone.zig\x00",
        "/repo",
    );
    defer snap.deinit();
    try testing.expectEqual(Kind.modified, snap.folders.get("/repo/a/b/c").?);
    try testing.expectEqual(Kind.modified, snap.folders.get("/repo/a/b").?);
    try testing.expectEqual(Kind.deleted, snap.folders.get("/repo/a/x").?);
    // a holds a modified file, an untracked one and a deleted one.
    try testing.expectEqual(Kind.deleted, snap.folders.get("/repo/a").?);
    try testing.expect(snap.folders.get("/repo") == null);
}

test "ignored files get no decoration" {
    var snap = try testSnapshot("! zig-out/\x00", "/repo");
    defer snap.deinit();
    try testing.expectEqual(@as(u32, 0), snap.files.count());
    try testing.expectEqual(@as(u32, 0), snap.folders.count());
}

test "the first line of git's complaint" {
    var job: git.Job = undefined;
    job.stderr = @constCast("\n  fatal: not a git repository\nmore");
    job.output = null;
    try testing.expectEqualStrings("fatal: not a git repository", errorLine(&job));
    job.stderr = @constCast("");
    job.output = @constCast("nothing to commit, working tree clean\n");
    try testing.expectEqualStrings("nothing to commit, working tree clean", errorLine(&job));
}
