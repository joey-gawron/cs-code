//! Working-tree status for the Source Control view, laid out like magit's
//! status buffer: `git status --porcelain=v2 -z --branch` parsed into the
//! branch (with ahead/behind) and changed files, plus what `gather` adds
//! (the unstaged and staged diffs, recent, unpushed and unpulled commits,
//! stashes). The view's text has a Head/Merge header, then Untracked files,
//! Unstaged changes, Staged changes, Stashes and the commits, as sections
//! whose files expand into their hunks; every line knows what it shows.
//! Also maps a line of `git diff` output back to a file and line.

const std = @import("std");
const theme_mod = @import("../../framework/ui/theme.zig");
const Color = @import("../../framework/gpu/scene.zig").Color;
const syntax_mod = @import("../syntax/syntax.zig");
const rope_mod = @import("../text/rope.zig");
const git = @import("git.zig");
const diff = @import("diff.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;

/// `git` arguments for a status run (in the repository root). Optional
/// locks off: a background status must not rewrite the index under the
/// user's own git commands (and the rewrite would look like a change).
pub const status_argv = [_][]const u8{ "--no-optional-locks", "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all" };

/// `git diff` as the view parses it (`--cached` for the staged side).
pub const diff_argv = [_][]const u8{ "--no-optional-locks", "-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/" };

/// Untracked files and similar long lists stop after this many lines.
pub const max_group_lines = 5000;
/// Commits listed under Recent commits, and at most under Unmerged into / Unpulled from.
const recent_commits = "10";
const max_commits = "256";

pub const Kind = enum { changed, renamed, unmerged, untracked, ignored };

pub const Entry = struct {
    kind: Kind,
    /// Index (staged) and work tree letters as git prints them: '.' for
    /// unchanged, else M T A D R C U.
    x: u8 = '.',
    y: u8 = '.',
    /// Relative to the repository root.
    path: []const u8,
    /// Renames and copies: the path before.
    orig_path: ?[]const u8 = null,
};

pub const Branch = struct {
    /// Commit hash, or "(initial)" before the first commit.
    oid: []const u8 = "",
    /// null when HEAD is detached.
    head: ?[]const u8 = null,
    upstream: ?[]const u8 = null,
    ahead: u32 = 0,
    behind: u32 = 0,
    /// Ahead/behind were printed: the upstream exists (a configured
    /// upstream without them is gone).
    has_ab: bool = false,

    pub fn initial(self: Branch) bool {
        return std.mem.eql(u8, self.oid, "(initial)");
    }
};

pub const Commit = struct {
    /// Abbreviated.
    hash: []const u8,
    subject: []const u8,
};

pub const Stash = struct {
    /// "stash@{0}"
    name: []const u8,
    subject: []const u8,
};

pub const Status = struct {
    arena: std.heap.ArenaAllocator,
    branch: Branch,
    entries: []Entry,
    // From `gather` (empty after `parse` alone).
    unstaged: []diff.File = &.{},
    staged: []diff.File = &.{},
    head: ?Commit = null,
    upstream_head: ?Commit = null,
    recent: []Commit = &.{},
    unpushed: []Commit = &.{},
    unpulled: []Commit = &.{},
    stashes: []Stash = &.{},

    pub fn deinit(self: *Status) void {
        self.arena.deinit();
    }
};

/// The rest of `rec` after `n` space-separated fields (paths may contain spaces).
fn afterFields(rec: []const u8, n: usize) ?[]const u8 {
    var i: usize = 0;
    var seen: usize = 0;
    while (i < rec.len) : (i += 1) {
        if (rec[i] != ' ') continue;
        seen += 1;
        if (seen == n) return rec[i + 1 ..];
    }
    return null;
}

fn parseHeader(alloc: Allocator, b: *Branch, line: []const u8) !void {
    const sp = std.mem.indexOfScalar(u8, line, ' ') orelse return;
    const key = line[0..sp];
    const value = line[sp + 1 ..];
    if (std.mem.eql(u8, key, "branch.oid")) {
        b.oid = try alloc.dupe(u8, value);
    } else if (std.mem.eql(u8, key, "branch.head")) {
        b.head = if (std.mem.eql(u8, value, "(detached)")) null else try alloc.dupe(u8, value);
    } else if (std.mem.eql(u8, key, "branch.upstream")) {
        b.upstream = try alloc.dupe(u8, value);
    } else if (std.mem.eql(u8, key, "branch.ab")) {
        // "+3 -1"
        var it = std.mem.tokenizeScalar(u8, value, ' ');
        const ahead = it.next() orelse return;
        const behind = it.next() orelse return;
        if (ahead.len < 2 or behind.len < 2) return;
        b.ahead = std.fmt.parseInt(u32, ahead[1..], 10) catch return;
        b.behind = std.fmt.parseInt(u32, behind[1..], 10) catch return;
        b.has_ab = true;
    }
}

/// Parses `git status --porcelain=v2 -z --branch` output (copied into the result).
pub fn parse(gpa: Allocator, output: []const u8) !Status {
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const alloc = arena.allocator();
    var branch: Branch = .{};
    var entries: std.ArrayList(Entry) = .empty;
    var it = std.mem.splitScalar(u8, output, 0);
    while (it.next()) |rec| {
        if (rec.len < 3 or rec[1] != ' ') continue;
        switch (rec[0]) {
            '#' => try parseHeader(alloc, &branch, rec[2..]),
            // 1 XY sub mH mI mW hH hI path
            '1' => {
                const path = afterFields(rec, 8) orelse continue;
                if (rec.len < 4) continue;
                try entries.append(alloc, .{ .kind = .changed, .x = rec[2], .y = rec[3], .path = try alloc.dupe(u8, path) });
            },
            // 2 XY sub mH mI mW hH hI Xscore path NUL origPath
            '2' => {
                const path = afterFields(rec, 9) orelse continue;
                const orig = it.next() orelse "";
                if (rec.len < 4) continue;
                try entries.append(alloc, .{ .kind = .renamed, .x = rec[2], .y = rec[3], .path = try alloc.dupe(u8, path), .orig_path = try alloc.dupe(u8, orig) });
            },
            // u XY sub m1 m2 m3 mW h1 h2 h3 path
            'u' => {
                const path = afterFields(rec, 10) orelse continue;
                if (rec.len < 4) continue;
                try entries.append(alloc, .{ .kind = .unmerged, .x = rec[2], .y = rec[3], .path = try alloc.dupe(u8, path) });
            },
            '?' => try entries.append(alloc, .{ .kind = .untracked, .x = '?', .y = '?', .path = try alloc.dupe(u8, rec[2..]) }),
            '!' => try entries.append(alloc, .{ .kind = .ignored, .x = '!', .y = '!', .path = try alloc.dupe(u8, rec[2..]) }),
            else => {},
        }
    }
    return .{ .arena = arena, .branch = branch, .entries = try entries.toOwnedSlice(alloc) };
}

/// "hash NUL subject" lines (`--format=%h%x00%s`).
pub fn parseCommits(alloc: Allocator, output: []const u8) ![]Commit {
    var out: std.ArrayList(Commit) = .empty;
    var it = std.mem.tokenizeScalar(u8, output, '\n');
    while (it.next()) |l| {
        const nul = std.mem.indexOfScalar(u8, l, 0) orelse continue;
        try out.append(alloc, .{ .hash = l[0..nul], .subject = l[nul + 1 ..] });
    }
    return out.toOwnedSlice(alloc);
}

/// Runs the rest of a status refresh in `root` (on the worker, after `git
/// status`): the diffs and commits the view shows. A part that fails is
/// left empty.
pub fn gather(st: *Status, io: Io, root: []const u8) void {
    const a = st.arena.allocator();
    const Run = struct {
        fn run(alloc: Allocator, i: Io, cwd: []const u8, args: []const []const u8) ?[]u8 {
            var argv: std.ArrayList([]const u8) = .empty;
            argv.append(alloc, "git") catch return null;
            argv.appendSlice(alloc, args) catch return null;
            return git.runCommand(alloc, i, argv.items, cwd, null) catch null;
        }
    };
    if (Run.run(a, io, root, &diff_argv)) |out| st.unstaged = diff.parse(a, out) catch &.{};
    if (Run.run(a, io, root, &(diff_argv ++ [_][]const u8{"--cached"}))) |out| st.staged = diff.parse(a, out) catch &.{};
    const log = [_][]const u8{ "--no-optional-locks", "log", "--no-color", "--format=%h%x00%s" };
    const b = st.branch;
    if (!b.initial()) if (Run.run(a, io, root, &(log ++ [_][]const u8{ "-n", recent_commits, "HEAD", "--" }))) |out| {
        st.recent = parseCommits(a, out) catch &.{};
        if (st.recent.len > 0) st.head = st.recent[0];
    };
    if (b.upstream != null and b.has_ab) {
        if (Run.run(a, io, root, &(log ++ [_][]const u8{ "-n", "1", "@{upstream}", "--" }))) |out| {
            const c = parseCommits(a, out) catch &.{};
            if (c.len > 0) st.upstream_head = c[0];
        }
        if (b.ahead > 0) if (Run.run(a, io, root, &(log ++ [_][]const u8{ "-n", max_commits, "@{upstream}..HEAD", "--" }))) |out| {
            st.unpushed = parseCommits(a, out) catch &.{};
        };
        if (b.behind > 0) if (Run.run(a, io, root, &(log ++ [_][]const u8{ "-n", max_commits, "HEAD..@{upstream}", "--" }))) |out| {
            st.unpulled = parseCommits(a, out) catch &.{};
        };
    }
    if (Run.run(a, io, root, &.{ "--no-optional-locks", "stash", "list", "--no-color", "--format=%gd%x00%s" })) |out| {
        const c = parseCommits(a, out) catch &.{};
        const stashes = a.alloc(Stash, c.len) catch return;
        for (c, stashes) |s, *o| o.* = .{ .name = s.hash, .subject = s.subject };
        st.stashes = stashes;
    }
}

/// An operation that stopped part way (conflicts, a rebase stopping to
/// edit), from the files git leaves in `git_dir`.
pub fn inProgress(io: Io, git_dir: []const u8) ?[]const u8 {
    const markers = [_]struct { file: []const u8, what: []const u8 }{
        .{ .file = "rebase-merge", .what = "Rebasing" },
        .{ .file = "rebase-apply", .what = "Rebasing" },
        .{ .file = "MERGE_HEAD", .what = "Merging" },
        .{ .file = "CHERRY_PICK_HEAD", .what = "Cherry-picking" },
        .{ .file = "REVERT_HEAD", .what = "Reverting" },
        .{ .file = "BISECT_LOG", .what = "Bisecting" },
    };
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    for (markers) |m| {
        const p = std.fmt.bufPrint(&buf, "{s}/{s}", .{ git_dir, m.file }) catch continue;
        if (Io.Dir.cwd().statFile(io, p, .{})) |_| return m.what else |_| {}
    }
    return null;
}

// ---- Sections -------------------------------------------------------------------

pub const Section = enum {
    untracked,
    unstaged,
    staged,
    stashes,
    /// "Unmerged into <upstream>": commits not pushed yet.
    unpushed,
    /// "Unpulled from <upstream>".
    unpulled,
    recent,

    pub fn title(s: Section) []const u8 {
        return switch (s) {
            .untracked => "Untracked files",
            .unstaged => "Unstaged changes",
            .staged => "Staged changes",
            .stashes => "Stashes",
            .unpushed => "Unmerged into",
            .unpulled => "Unpulled from",
            .recent => "Recent commits",
        };
    }

    /// A section of files (else of commits or stashes).
    pub fn hasFiles(s: Section) bool {
        return switch (s) {
            .untracked, .unstaged, .staged => true,
            else => false,
        };
    }
};

/// Whether a file shows in a section: a file staged and edited again is in
/// both Unstaged and Staged changes; conflicts are unstaged.
pub fn inSection(e: *const Entry, s: Section) bool {
    return switch (s) {
        .untracked => e.kind == .untracked,
        .unstaged => e.kind == .unmerged or ((e.kind == .changed or e.kind == .renamed) and e.y != '.'),
        .staged => (e.kind == .changed or e.kind == .renamed) and e.x != '.',
        else => false,
    };
}

/// What magit calls the change: "modified", "new file", ...
fn changeLabel(e: *const Entry, s: Section) []const u8 {
    if (e.kind == .unmerged) return "unmerged";
    return switch (if (s == .staged) e.x else e.y) {
        'M' => "modified",
        'A' => "new file",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "typechange",
        else => "changed",
    };
}

const file_labels = [_][]const u8{ "modified   ", "new file   ", "deleted    ", "renamed    ", "copied     ", "typechange ", "unmerged   ", "changed    " };

/// A path as shown: control characters (a newline would split the line) as '?'.
const Shown = struct {
    path: []const u8,
    pub fn format(self: Shown, w: *std.Io.Writer) std.Io.Writer.Error!void {
        for (self.path) |ch| try w.writeByte(if (ch < 0x20 or ch == 0x7f) '?' else ch);
    }
};

pub const ItemKind = enum {
    /// The "Head:" (index 0) or "Merge:" (index 1) line: HEAD or the upstream.
    ref,
    section,
    file,
    /// A mode change or "Binary files differ" under a file.
    note,
    hunk,
    line,
    commit,
    stash,
};

/// What a line of the view shows.
pub const Item = struct {
    kind: ItemKind,
    section: Section = .untracked,
    /// file, note, hunk, line: the file's index in `Status.entries`;
    /// commit, stash: the index in its list; ref: 0 HEAD, 1 upstream.
    index: u32 = 0,
    /// hunk, line: the hunk's index in the file's diff.
    hunk: u32 = 0,
    /// line: the index in the hunk's lines.
    line: u32 = 0,

    /// What TAB shows and hides from here: the section, file or hunk
    /// (a diff line folds its hunk, a note its file).
    pub fn fold(self: Item) ?Item {
        return switch (self.kind) {
            .section, .file, .hunk => self,
            .line => .{ .kind = .hunk, .section = self.section, .index = self.index, .hunk = self.hunk },
            .note => .{ .kind = .file, .section = self.section, .index = self.index },
            .ref, .commit, .stash => null,
        };
    }

    /// The item one level up (`^`).
    pub fn parent(self: Item) ?Item {
        return switch (self.kind) {
            .ref, .section => null,
            .line => .{ .kind = .hunk, .section = self.section, .index = self.index, .hunk = self.hunk },
            .hunk, .note => .{ .kind = .file, .section = self.section, .index = self.index },
            .file, .commit, .stash => .{ .kind = .section, .section = self.section },
        };
    }

    /// A line that starts a section of its own (n and p stop there).
    pub fn heading(self: Item) bool {
        return switch (self.kind) {
            .note, .line => false,
            else => true,
        };
    }

    pub fn eql(a: Item, b: Item) bool {
        return a.kind == b.kind and a.section == b.section and a.index == b.index and a.hunk == b.hunk and a.line == b.line;
    }
};

/// Where the caret was before a refresh, to put it back on the same thing.
const Anchor = struct {
    item: Item,
    /// The file's path, or the commit's hash or the stash's name (owned).
    name: ?[]u8 = null,
    /// Its place among its section's files, commits or stashes.
    ordinal: u32 = 0,
};

pub const View = struct {
    gpa: Allocator,
    /// Repository root (owned); null until found.
    root: ?[]u8 = null,
    status: ?Status = null,
    /// Shown instead of the files ("Not a git repository: …"), owned.
    note: ?[]u8 = null,
    /// An operation that stopped part way ("Merging"), shown under the head.
    in_progress: ?[]const u8 = null,
    text: std.ArrayList(u8) = .empty,
    /// What each line of `text` shows.
    targets: std.ArrayList(?Item) = .empty,
    /// Sections, files and hunks shown the other way from their default.
    toggled: std.StringHashMapUnmanaged(void) = .empty,
    /// What shows by default (magit's levels, keys 1-4): 1 section headings,
    /// 2 files, 3 hunk headings, 4 everything.
    level: u8 = 2,
    /// No level was picked yet: as 2, with Stashes hidden (magit's start).
    initial: bool = true,
    /// Index into `Status.unstaged` / `.staged` of each entry's diff.
    diffs: [2]std.StringHashMapUnmanaged(u32) = .{ .empty, .empty },

    pub fn init(gpa: Allocator) View {
        return .{ .gpa = gpa };
    }

    pub fn deinit(self: *View) void {
        if (self.root) |r| self.gpa.free(r);
        if (self.status) |*s| s.deinit();
        if (self.note) |n| self.gpa.free(n);
        self.text.deinit(self.gpa);
        self.targets.deinit(self.gpa);
        self.clearToggled();
        self.toggled.deinit(self.gpa);
        for (&self.diffs) |*d| d.deinit(self.gpa);
    }

    fn clearToggled(self: *View) void {
        var it = self.toggled.keyIterator();
        while (it.next()) |k| self.gpa.free(k.*);
        self.toggled.clearRetainingCapacity();
    }

    pub fn setRoot(self: *View, root: ?[]const u8) !void {
        const owned = if (root) |r| try self.gpa.dupe(u8, r) else null;
        if (self.root) |r| self.gpa.free(r);
        self.root = owned;
        self.clearToggled();
    }

    /// Shows `fmt` instead of the files (null `fmt` shows them again).
    pub fn setNote(self: *View, comptime fmt: ?[]const u8, args: anytype) void {
        if (self.note) |n| self.gpa.free(n);
        self.note = null;
        if (fmt) |f| self.note = std.fmt.allocPrint(self.gpa, f, args) catch null;
    }

    /// Drops the files (another repository, or none).
    pub fn clearStatus(self: *View) void {
        if (self.status) |*s| s.deinit();
        self.status = null;
        for (&self.diffs) |*d| d.clearRetainingCapacity();
    }

    pub fn targetAt(self: *const View, row: u32) ?Item {
        if (row >= self.targets.items.len) return null;
        return self.targets.items[row];
    }

    /// The file an item is about (file, note, hunk, line).
    pub fn entry(self: *const View, t: Item) ?*const Entry {
        switch (t.kind) {
            .file, .note, .hunk, .line => {},
            else => return null,
        }
        const s = self.status orelse return null;
        return if (t.index < s.entries.len) &s.entries[t.index] else null;
    }

    /// The diff of a file of Unstaged or Staged changes.
    pub fn fileDiff(self: *const View, section: Section, index: u32) ?*const diff.File {
        const s = self.status orelse return null;
        if (index >= s.entries.len) return null;
        const side: usize = switch (section) {
            .unstaged => 0,
            .staged => 1,
            else => return null,
        };
        const i = self.diffs[side].get(s.entries[index].path) orelse return null;
        const files = if (side == 0) s.unstaged else s.staged;
        return &files[i];
    }

    pub fn hunk(self: *const View, t: Item) ?*const diff.Hunk {
        if (t.kind != .hunk and t.kind != .line) return null;
        const f = self.fileDiff(t.section, t.index) orelse return null;
        return if (t.hunk < f.hunks.len) &f.hunks[t.hunk] else null;
    }

    pub fn commit(self: *const View, t: Item) ?Commit {
        const s = self.status orelse return null;
        switch (t.kind) {
            .commit => {
                const list = switch (t.section) {
                    .unpushed => s.unpushed,
                    .unpulled => s.unpulled,
                    .recent => s.recent,
                    else => return null,
                };
                return if (t.index < list.len) list[t.index] else null;
            },
            .ref => return if (t.index == 0) s.head else s.upstream_head,
            else => return null,
        }
    }

    pub fn stash(self: *const View, t: Item) ?Stash {
        const s = self.status orelse return null;
        if (t.kind != .stash or t.index >= s.stashes.len) return null;
        return s.stashes[t.index];
    }

    /// The files of a file section, as indices into `Status.entries`.
    fn sectionLen(self: *const View, s: Section) usize {
        const st = self.status orelse return 0;
        return switch (s) {
            .stashes => st.stashes.len,
            .unpushed => st.unpushed.len,
            .unpulled => st.unpulled.len,
            .recent => st.recent.len,
            else => blk: {
                var n: usize = 0;
                for (st.entries) |*e| {
                    if (inSection(e, s)) n += 1;
                }
                break :blk n;
            },
        };
    }

    // ---- Showing and hiding -------------------------------------------------------

    /// The key of a section, file or hunk in `toggled`.
    fn key(self: *const View, buf: []u8, t: Item) []const u8 {
        const tag = @tagName(t.section);
        return switch (t.kind) {
            .section => std.fmt.bufPrint(buf, "s/{s}", .{tag}) catch "",
            .file => std.fmt.bufPrint(buf, "f/{s}/{s}", .{ tag, if (self.entry(t)) |e| e.path else "" }) catch "",
            .hunk => blk: {
                const h = self.hunk(t) orelse break :blk "";
                // The range and the start of the context: it moves with edits above, as in magit.
                const header = h.header[0..@min(h.header.len, 80)];
                break :blk std.fmt.bufPrint(buf, "h/{s}/{s}/{s}", .{ tag, if (self.entry(t)) |e| e.path else "", header }) catch "";
            },
            else => "",
        };
    }

    fn defaultShown(self: *const View, t: Item) bool {
        return switch (t.kind) {
            .section => self.level >= 2 and !(self.initial and t.section == .stashes),
            .file => self.level >= 3,
            .hunk => self.level != 3,
            else => true,
        };
    }

    /// The section, file or hunk shows what's under it.
    pub fn shown(self: *const View, t: Item) bool {
        var buf: [std.fs.max_path_bytes + 128]u8 = undefined;
        const k = self.key(&buf, t);
        return self.defaultShown(t) != (k.len > 0 and self.toggled.contains(k));
    }

    fn setShown(self: *View, t: Item, show: bool) !void {
        var buf: [std.fs.max_path_bytes + 128]u8 = undefined;
        const k = self.key(&buf, t);
        if (k.len == 0) return;
        if (show == self.defaultShown(t)) {
            if (self.toggled.fetchRemove(k)) |kv| self.gpa.free(kv.key);
        } else if (!self.toggled.contains(k)) {
            const owned = try self.gpa.dupe(u8, k);
            errdefer self.gpa.free(owned);
            try self.toggled.put(self.gpa, owned, {});
        }
    }

    /// TAB: shows or hides what's under the section, file or hunk at
    /// `row` (from a diff line, its hunk). Returns the row of its heading,
    /// or null if there's nothing to fold there.
    pub fn toggle(self: *View, row: u32) !?u32 {
        const t = (self.targetAt(row) orelse return null).fold() orelse return null;
        if (t.kind == .file and self.fileDiff(t.section, t.index) == null) return null;
        try self.setShown(t, !self.shown(t));
        try self.rebuild();
        return self.rowOf(t);
    }

    /// 1-4: shows everything down to `level` and hides the rest.
    pub fn setLevel(self: *View, level: u8) !void {
        self.level = std.math.clamp(level, 1, 4);
        self.initial = false;
        self.clearToggled();
        try self.rebuild();
    }

    pub fn rowOf(self: *const View, t: Item) ?u32 {
        for (self.targets.items, 0..) |maybe, i| if (maybe) |x| if (x.eql(t)) return @intCast(i);
        return null;
    }

    // ---- Moving around ------------------------------------------------------------

    /// The next (or previous) line that starts a section, file, hunk or commit.
    pub fn nextHeading(self: *const View, row: u32, forward: bool) ?u32 {
        const n: u32 = @intCast(self.targets.items.len);
        var r = row;
        while (true) {
            if (forward) {
                if (r + 1 >= n) return null;
                r += 1;
            } else {
                if (r == 0) return null;
                r -= 1;
            }
            if (self.targets.items[r]) |t| if (t.heading()) return r;
        }
    }

    /// `^`: the row of what holds the line at `row`.
    pub fn parentRow(self: *const View, row: u32) ?u32 {
        const t = self.targetAt(row) orelse return null;
        return self.rowOf(t.parent() orelse return null);
    }

    /// The first file's row (else the first heading's).
    pub fn firstItemRow(self: *const View) ?u32 {
        for (self.targets.items, 0..) |t, i| if (t != null and t.?.kind == .file) return @intCast(i);
        for (self.targets.items, 0..) |t, i| if (t != null and t.?.kind != .ref) return @intCast(i);
        return null;
    }

    // ---- Refreshing ---------------------------------------------------------------

    fn anchorAt(self: *const View, row: u32) !?Anchor {
        const t = self.targetAt(row) orelse return null;
        var a: Anchor = .{ .item = t };
        switch (t.kind) {
            .file, .note, .hunk, .line => if (self.entry(t)) |e| {
                a.name = try self.gpa.dupe(u8, e.path);
            },
            .commit => if (self.commit(t)) |c| {
                a.name = try self.gpa.dupe(u8, c.hash);
            },
            .stash => if (self.stash(t)) |s| {
                a.name = try self.gpa.dupe(u8, s.name);
            },
            else => {},
        }
        // Its place in the section: the files above it.
        var r = row;
        while (r > 0) : (r -= 1) {
            const above = self.targets.items[r - 1] orelse continue;
            if (above.section != t.section or above.kind == .section) break;
            if (above.kind == .file or above.kind == .commit or above.kind == .stash) a.ordinal += 1;
        }
        if (t.kind != .file and t.kind != .commit and t.kind != .stash) a.ordinal -|= 1;
        return a;
    }

    /// The row for the caret that was on `a`: the same line of the same
    /// file (the same hunk, else its neighbour once one is staged), else the
    /// file in its place in the section (the next one), else the section.
    fn findAnchor(self: *const View, a: Anchor) ?u32 {
        const t = a.item;
        if (t.kind == .ref or t.kind == .section) return self.rowOf(t);
        const st = self.status orelse return null;
        var same: ?Item = null;
        if (a.name) |name| switch (t.kind) {
            .file, .note, .hunk, .line => for (st.entries, 0..) |*e, i| {
                if (inSection(e, t.section) and std.mem.eql(u8, e.path, name)) {
                    same = .{ .kind = .file, .section = t.section, .index = @intCast(i) };
                    break;
                }
            },
            .commit, .stash => for (0..self.sectionLen(t.section)) |i| {
                const it: Item = .{ .kind = t.kind, .section = t.section, .index = @intCast(i) };
                const n = if (t.kind == .commit) (self.commit(it) orelse continue).hash else (self.stash(it) orelse continue).name;
                if (std.mem.eql(u8, n, name)) {
                    same = it;
                    break;
                }
            },
            else => {},
        };
        if (same) |f| {
            if ((t.kind == .hunk or t.kind == .line) and f.kind == .file) {
                if (self.fileDiff(f.section, f.index)) |d| if (d.hunks.len > 0) {
                    const h: u32 = @intCast(@min(t.hunk, d.hunks.len - 1));
                    const hk: Item = .{ .kind = .hunk, .section = f.section, .index = f.index, .hunk = h };
                    if (t.kind == .line) {
                        const lines = d.hunks[h].lines.len;
                        if (lines > 0) if (self.rowOf(.{ .kind = .line, .section = f.section, .index = f.index, .hunk = h, .line = @intCast(@min(t.line, lines - 1)) })) |r| return r;
                    }
                    if (self.rowOf(hk)) |r| return r;
                };
            }
            if (self.rowOf(f)) |r| return r;
        }
        // Gone from the section: what's now in its place.
        var seen: u32 = 0;
        var last: ?u32 = null;
        var header: ?u32 = null;
        for (self.targets.items, 0..) |maybe, i| {
            const x = maybe orelse continue;
            if (x.section != t.section or x.kind == .ref) continue;
            if (x.kind == .section) {
                header = @intCast(i);
                continue;
            }
            if (x.kind != .file and x.kind != .commit and x.kind != .stash) continue;
            if (seen == a.ordinal) return @intCast(i);
            seen += 1;
            last = @intCast(i);
        }
        return last orelse header;
    }

    /// Takes `status` (replacing the old one) and rebuilds the text.
    /// Returns the row for the caret now on `caret_row` (see `findAnchor`;
    /// else the first file).
    pub fn update(self: *View, status: Status, caret_row: u32) !u32 {
        const anchor = try self.anchorAt(caret_row);
        defer if (anchor) |a| if (a.name) |n| self.gpa.free(n);
        if (self.status) |*s| s.deinit();
        self.status = status;
        for (&self.diffs, [_][]diff.File{ status.unstaged, status.staged }) |*d, files| {
            d.clearRetainingCapacity();
            for (files, 0..) |f, i| try d.put(self.gpa, f.path, @intCast(i));
        }
        try self.rebuild();
        const last: u32 = @intCast(self.targets.items.len - 1);
        if (anchor) |a| if (self.findAnchor(a)) |r| return r;
        if (anchor == null and caret_row <= last and self.targetAt(caret_row) == null) return caret_row;
        return self.firstItemRow() orelse @min(caret_row, last);
    }

    fn line(self: *View, target: ?Item, comptime fmt: []const u8, args: anytype) !void {
        if (self.targets.items.len > 0) try self.text.append(self.gpa, '\n');
        try self.text.print(self.gpa, fmt, args);
        try self.targets.append(self.gpa, target);
    }

    pub fn rebuild(self: *View) !void {
        self.text.clearRetainingCapacity();
        self.targets.clearRetainingCapacity();
        if (self.note) |n| return self.line(null, "{s}", .{n});
        const st = self.status orelse return self.line(null, "Reading git status…", .{});
        const b = st.branch;

        // Head:     main Subject of the last commit
        const head_item: Item = .{ .kind = .ref, .index = 0 };
        const subject = if (st.head) |c| c.subject else "";
        if (b.initial()) {
            try self.line(head_item, "Head:     {s} (no commits yet)", .{b.head orelse "HEAD"});
        } else {
            // Detached: the commit instead of a branch.
            const name = b.head orelse if (st.head) |c| c.hash else b.oid[0..@min(b.oid.len, 7)];
            try self.line(head_item, "Head:     {s}{s}{s}", .{ name, if (subject.len > 0) " " else "", subject });
        }
        if (b.upstream) |up| {
            if (b.has_ab) {
                const up_subject = if (st.upstream_head) |c| c.subject else "";
                try self.line(.{ .kind = .ref, .index = 1 }, "Merge:    {s}{s}{s}", .{ up, if (up_subject.len > 0) " " else "", up_subject });
            } else try self.line(null, "Merge:    {s} (gone)", .{up});
        }
        if (self.in_progress) |what| try self.line(null, "State:    {s} (s marks a conflict resolved; ? lists the keys)", .{what});

        try self.fileSection(st, .untracked);
        try self.fileSection(st, .unstaged);
        try self.fileSection(st, .staged);
        try self.commitSection(st, .stashes);
        if (st.unpushed.len > 0) {
            try self.commitSection(st, .unpushed);
        } else try self.commitSection(st, .recent);
        try self.commitSection(st, .unpulled);
    }

    fn fileSection(self: *View, st: Status, s: Section) !void {
        const count = self.sectionLen(s);
        if (count == 0) return;
        const header: Item = .{ .kind = .section, .section = s };
        try self.line(null, "", .{});
        try self.line(header, "{s} ({d})", .{ s.title(), count });
        if (!self.shown(header)) return;
        var listed: usize = 0;
        for (st.entries, 0..) |*e, i| {
            if (!inSection(e, s)) continue;
            if (listed == max_group_lines) {
                try self.line(header, "({d} more not shown)", .{count - listed});
                break;
            }
            listed += 1;
            const t: Item = .{ .kind = .file, .section = s, .index = @intCast(i) };
            if (s == .untracked) {
                try self.line(t, "{f}", .{Shown{ .path = e.path }});
                continue;
            }
            const label = changeLabel(e, s);
            if (s == .staged and e.orig_path != null) {
                try self.line(t, "{s: <11}{f} -> {f}", .{ label, Shown{ .path = e.orig_path.? }, Shown{ .path = e.path } });
            } else try self.line(t, "{s: <11}{f}", .{ label, Shown{ .path = e.path } });
            const d = self.fileDiff(s, @intCast(i)) orelse continue;
            if (!self.shown(t)) continue;
            for (d.header) |h| if (diff.File.isNote(h)) try self.line(.{ .kind = .note, .section = s, .index = @intCast(i) }, "{s}", .{h});
            for (d.hunks, 0..) |*h, hi| {
                const ht: Item = .{ .kind = .hunk, .section = s, .index = @intCast(i), .hunk = @intCast(hi) };
                try self.line(ht, "{s}", .{h.header});
                if (!self.shown(ht)) continue;
                for (h.lines, 0..) |l, li| {
                    try self.line(.{ .kind = .line, .section = s, .index = @intCast(i), .hunk = @intCast(hi), .line = @intCast(li) }, "{s}", .{l});
                }
            }
        }
    }

    fn commitSection(self: *View, st: Status, s: Section) !void {
        const count = self.sectionLen(s);
        if (count == 0) return;
        const header: Item = .{ .kind = .section, .section = s };
        try self.line(null, "", .{});
        switch (s) {
            .unpushed => try self.line(header, "{s} {s} ({d})", .{ s.title(), st.branch.upstream orelse "", st.branch.ahead }),
            .unpulled => try self.line(header, "{s} {s} ({d})", .{ s.title(), st.branch.upstream orelse "", st.branch.behind }),
            .stashes => try self.line(header, "{s} ({d})", .{ s.title(), count }),
            else => try self.line(header, "{s}", .{s.title()}),
        }
        if (!self.shown(header)) return;
        for (0..count) |i| {
            const t: Item = .{ .kind = if (s == .stashes) .stash else .commit, .section = s, .index = @intCast(i) };
            if (s == .stashes) {
                const z = st.stashes[i];
                try self.line(t, "{s} {s}", .{ z.name, z.subject });
            } else {
                const c = self.commit(t).?;
                try self.line(t, "{s} {s}", .{ c.hash, c.subject });
            }
        }
    }

    /// The paths an item stands for, relative to the root: a file (with a
    /// staged rename's old path), or every file of a section.
    pub fn paths(self: *const View, alloc: Allocator, t: Item) ![]const []const u8 {
        var out: std.ArrayList([]const u8) = .empty;
        const st = self.status orelse return out.items;
        for (st.entries, 0..) |*e, i| {
            if (t.kind == .section) {
                if (!inSection(e, t.section)) continue;
            } else if (t.index != i) continue;
            try out.append(alloc, e.path);
            if (e.orig_path) |o| if (t.section == .staged) try out.append(alloc, o);
        }
        return out.items;
    }
};

// ---- Highlighting -------------------------------------------------------------------

fn isHash(s: []const u8) bool {
    if (s.len < 7) return false;
    for (s) |ch| if (!std.ascii.isHex(ch) or std.ascii.isUpper(ch)) return false;
    return true;
}

/// Colors the Source Control view by the shape of its lines (as the text is
/// generated); the diff lines' backgrounds come from the view (git_status_ui).
pub fn highlight(gpa: Allocator, rope: *const rope_mod.Rope, start: usize, end: usize, s: *const theme_mod.Syntax, out: *std.ArrayList(syntax_mod.HighlightSpan)) void {
    out.clearRetainingCapacity();
    var line_buf: std.ArrayList(u8) = .empty;
    defer line_buf.deinit(gpa);
    const Add = struct {
        o: *std.ArrayList(syntax_mod.HighlightSpan),
        g: Allocator,
        base: usize,
        fn span(self: @This(), a: usize, b: usize, color: Color) void {
            self.styled(a, b, color, .normal);
        }
        fn styled(self: @This(), a: usize, b: usize, color: Color, style: theme_mod.FontStyle) void {
            if (b > a) self.o.append(self.g, .{ .start = self.base + a, .end = self.base + b, .color = color, .style = style }) catch {};
        }
    };
    var row = rope.offsetToPoint(start).row;
    const last = rope.offsetToPoint(end).row;
    while (row <= last) : (row += 1) {
        line_buf.clearRetainingCapacity();
        const base = rope.lineStart(row);
        var it = rope.chunks(base, rope.lineEnd(row));
        while (it.next()) |chunk| line_buf.appendSlice(gpa, chunk) catch return;
        const l = line_buf.items;
        if (l.len == 0) continue;
        const add: Add = .{ .o = out, .g = gpa, .base = base };
        if (std.mem.startsWith(u8, l, "@@")) {
            // "@@ -1,4 +1,5 @@ fn main() {": the range, then the context.
            const close = std.mem.indexOfPos(u8, l, 2, "@@") orelse l.len;
            const range_end = @min(l.len, close + 2);
            add.span(0, range_end, s.comment);
            add.span(range_end, l.len, s.function);
            continue;
        }
        switch (l[0]) {
            '+' => {
                add.span(0, l.len, s.string);
                continue;
            },
            '-' => {
                add.span(0, l.len, s.constant);
                continue;
            },
            '\\' => {
                add.span(0, l.len, s.comment);
                continue;
            },
            ' ' => continue,
            else => {},
        }
        if (l.len > 10 and (std.mem.startsWith(u8, l, "Head:     ") or std.mem.startsWith(u8, l, "Merge:    ") or std.mem.startsWith(u8, l, "State:    "))) {
            // "Head:     main Subject": the label, the branch (or hash), the subject.
            add.span(0, 10, s.comment);
            if (l[0] == 'S') {
                add.span(10, l.len, s.constant);
                continue;
            }
            const name_end = std.mem.indexOfScalarPos(u8, l, 10, ' ') orelse l.len;
            const name = l[10..name_end];
            add.styled(10, name_end, if (isHash(name)) s.comment else if (l[0] == 'M') s.string else s.keyword, .bold);
            if (std.mem.endsWith(u8, l, "(gone)") or std.mem.endsWith(u8, l, "(no commits yet)")) {
                add.span(name_end, l.len, s.comment);
            }
            continue;
        }
        if (sectionHeading(l)) |title_end| {
            // "Unmerged into origin/main (2)": the title, the upstream, the count.
            add.styled(0, title_end, s.function, .bold);
            const paren = if (l[l.len - 1] == ')') std.mem.lastIndexOf(u8, l, " (") orelse l.len else l.len;
            if (paren > title_end) add.styled(title_end, paren, s.string, .bold);
            add.span(paren, l.len, s.comment);
            continue;
        }
        if (l.len > 11) if (fileLabel(l)) |color| {
            // "modified   src/a.zig", "renamed    old -> new": the name in bold, as a heading.
            add.span(0, 11, color(s));
            const arrow = std.mem.indexOf(u8, l, " -> ");
            if (arrow) |a| add.span(11, a + 4, s.comment);
            add.styled(if (arrow) |a| a + 4 else 11, l.len, s.foreground, .bold);
            continue;
        };
        if (std.mem.startsWith(u8, l, "stash@{")) {
            const sp = std.mem.indexOfScalar(u8, l, ' ') orelse l.len;
            add.span(0, sp, s.number);
            continue;
        }
        if (std.mem.indexOfScalar(u8, l, ' ')) |sp| if (isHash(l[0..sp])) {
            add.span(0, sp, s.comment);
            continue;
        };
        if (std.mem.startsWith(u8, l, "old mode ") or std.mem.startsWith(u8, l, "new mode ") or std.mem.startsWith(u8, l, "Binary files ") or
            (l[0] == '(' and l[l.len - 1] == ')'))
        {
            add.span(0, l.len, s.comment);
        }
    }
}

/// A section heading's title length ("Unstaged changes (2)" → 16), else null.
fn sectionHeading(l: []const u8) ?usize {
    inline for (@typeInfo(Section).@"enum".field_values) |v| {
        const t = @as(Section, @enumFromInt(v)).title();
        if (std.mem.startsWith(u8, l, t) and (l.len == t.len or l[t.len] == ' ')) {
            const rest = l[t.len..];
            if (rest.len == 0 or rest[rest.len - 1] == ')') return t.len;
        }
    }
    return null;
}

fn fileLabel(l: []const u8) ?*const fn (s: *const theme_mod.Syntax) Color {
    const Colors = struct {
        fn added(s: *const theme_mod.Syntax) Color {
            return s.string;
        }
        fn removed(s: *const theme_mod.Syntax) Color {
            return s.constant;
        }
        fn moved(s: *const theme_mod.Syntax) Color {
            return s.keyword;
        }
        fn changed(s: *const theme_mod.Syntax) Color {
            return s.number;
        }
    };
    for (file_labels) |lab| if (std.mem.startsWith(u8, l, lab)) {
        return switch (lab[0]) {
            'n' => Colors.added,
            'd' => Colors.removed,
            'r', 'c' => if (lab[1] == 'h') Colors.changed else Colors.moved,
            else => Colors.changed,
        };
    };
    return null;
}

// ---- Logs and commit messages -------------------------------------------------------

/// `git log` arguments for the Log tab (a revision or --all follows).
pub const log_argv = [_][]const u8{ "--no-optional-locks", "log", "--graph", "--no-color", "--decorate=short", "--format=%h%d %s \u{2014} %an, %ar", "-n", "256" };

/// The commit hash on a line of the Log tab ("| * 1a2b3c4 (HEAD) Subject").
pub fn logHash(l: []const u8) ?[]const u8 {
    var i: usize = 0;
    while (i < l.len and std.mem.indexOfScalar(u8, "*|/\\_ .-", l[i]) != null) i += 1;
    const end = std.mem.indexOfScalarPos(u8, l, i, ' ') orelse l.len;
    return if (isHash(l[i..end])) l[i..end] else null;
}

/// Colors the Log tab: the graph, hashes, ref names, and the author and age.
pub fn logHighlight(gpa: Allocator, rope: *const rope_mod.Rope, start: usize, end: usize, s: *const theme_mod.Syntax, out: *std.ArrayList(syntax_mod.HighlightSpan)) void {
    out.clearRetainingCapacity();
    var line_buf: std.ArrayList(u8) = .empty;
    defer line_buf.deinit(gpa);
    var row = rope.offsetToPoint(start).row;
    const last = rope.offsetToPoint(end).row;
    while (row <= last) : (row += 1) {
        line_buf.clearRetainingCapacity();
        const base = rope.lineStart(row);
        var it = rope.chunks(base, rope.lineEnd(row));
        while (it.next()) |chunk| line_buf.appendSlice(gpa, chunk) catch return;
        const l = line_buf.items;
        const span = struct {
            fn add(o: *std.ArrayList(syntax_mod.HighlightSpan), g: Allocator, b: usize, x: usize, y: usize, color: Color) void {
                if (y > x) o.append(g, .{ .start = b + x, .end = b + y, .color = color }) catch {};
            }
        }.add;
        const hash = logHash(l) orelse {
            span(out, gpa, base, 0, l.len, s.comment);
            continue;
        };
        const h0 = @intFromPtr(hash.ptr) - @intFromPtr(l.ptr);
        span(out, gpa, base, 0, h0, s.comment);
        span(out, gpa, base, h0, h0 + hash.len, s.number);
        var pos = h0 + hash.len;
        if (std.mem.startsWith(u8, l[pos..], " (")) if (std.mem.indexOfScalarPos(u8, l, pos, ')')) |close| {
            span(out, gpa, base, pos + 1, close + 1, s.keyword);
            pos = close + 1;
        };
        if (std.mem.lastIndexOf(u8, l, " \u{2014} ")) |dash| if (dash >= pos) span(out, gpa, base, dash, l.len, s.comment);
    }
}

/// The line that ends a commit message; the diff shown below it is ignored.
pub const scissors = "# ------------------------ >8 ------------------------";

/// The text of a COMMIT_EDITMSG tab: `message` to edit, how to finish, and
/// (below the scissors line, as `git commit -v` shows it) `diff_text`.
pub fn commitTemplate(alloc: Allocator, message: []const u8, about: []const u8, diff_text: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);
    const msg = std.mem.trimEnd(u8, message, " \t\r\n");
    try out.print(alloc, "{s}\n", .{msg});
    if (msg.len > 0) try out.append(alloc, '\n');
    try out.appendSlice(alloc,
        \\# Please enter the commit message for your changes. Lines starting
        \\# with '#' will be ignored, and an empty message aborts the commit.
        \\# ctrl+c ctrl+c commits (so does saving and closing this tab);
        \\# ctrl+c ctrl+k aborts.
        \\#
        \\
    );
    try out.print(alloc, "# {s}\n", .{about});
    if (diff_text.len > 0) {
        try out.print(alloc, "{s}\n# Do not modify or remove the line above.\n# Everything below it will be ignored.\n{s}", .{ scissors, diff_text });
        if (diff_text[diff_text.len - 1] != '\n') try out.append(alloc, '\n');
    }
    return out.toOwnedSlice(alloc);
}

/// The message part of a COMMIT_EDITMSG text (above the scissors line);
/// `git commit --cleanup=strip` drops its comments.
pub fn commitMessage(text: []const u8) []const u8 {
    if (std.mem.startsWith(u8, text, scissors)) return "";
    var search: usize = 0;
    while (std.mem.indexOfPos(u8, text, search, "\n" ++ scissors)) |i| {
        const after = i + 1 + scissors.len;
        if (after == text.len or text[after] == '\n' or text[after] == '\r') return text[0 .. i + 1];
        search = after;
    }
    return text;
}

/// A message without its comment lines and surrounding blank lines (what
/// `--cleanup=strip` keeps, before it collapses blank lines).
pub fn messageText(alloc: Allocator, message: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);
    var it = std.mem.splitScalar(u8, message, '\n');
    while (it.next()) |l| {
        if (l.len > 0 and l[0] == '#') continue;
        if (out.items.len == 0 and std.mem.trim(u8, l, " \t\r").len == 0) continue;
        try out.appendSlice(alloc, std.mem.trimEnd(u8, l, " \t\r"));
        try out.append(alloc, '\n');
    }
    while (out.items.len > 0 and out.items[out.items.len - 1] == '\n') out.items.len -= 1;
    return out.toOwnedSlice(alloc);
}

/// Nothing but comments and blank lines: git would abort the commit.
pub fn messageEmpty(message: []const u8) bool {
    var it = std.mem.splitScalar(u8, message, '\n');
    while (it.next()) |l| {
        if (l.len > 0 and l[0] == '#') continue;
        if (std.mem.trim(u8, l, " \t\r").len > 0) return false;
    }
    return true;
}

// ---- Diff locations ---------------------------------------------------------------

pub const DiffLocation = struct {
    /// Relative to the repository root (a slice of the diff text).
    path: []const u8,
    /// 1-based line in the new version.
    line: u32,
};

/// The file and line a row of `git diff` output (a/ b/ prefixes) points
/// at: a context or added line is that line of the new version, a removed
/// line the place it was removed from, a header the file's first change.
pub fn diffLocation(text: []const u8, row: u32) ?DiffLocation {
    var path: ?[]const u8 = null;
    var new_line: u32 = 1;
    var in_hunk = false;
    // `row` is a header line of the current file: answered at its first hunk.
    var waiting = false;
    var it = std.mem.splitScalar(u8, text, '\n');
    var r: u32 = 0;
    while (it.next()) |l| : (r += 1) {
        if (std.mem.startsWith(u8, l, "diff --git ")) {
            // The previous file had no hunks (a mode change, a binary file).
            if (waiting) return .{ .path = path orelse return null, .line = 1 };
            in_hunk = false;
            path = null;
        } else if (std.mem.startsWith(u8, l, "@@")) {
            // "@@ -12,3 +14,5 @@ context"
            in_hunk = true;
            new_line = 1;
            if (std.mem.indexOfScalar(u8, l, '+')) |plus| {
                var end = plus + 1;
                while (end < l.len and std.ascii.isDigit(l[end])) end += 1;
                new_line = std.fmt.parseInt(u32, l[plus + 1 .. end], 10) catch 1;
            }
            if (waiting or r == row) return .{ .path = path orelse return null, .line = @max(1, new_line) };
            continue;
        } else if (in_hunk) {
            const here = new_line;
            // Removed lines (and "\ No newline") don't exist in the new version.
            if (l.len == 0 or (l[0] != '-' and l[0] != '\\')) new_line += 1;
            if (r == row) return .{ .path = path orelse return null, .line = @max(1, here) };
            continue;
        } else if (std.mem.startsWith(u8, l, "+++ b/")) {
            path = l["+++ b/".len..];
        } else if (std.mem.startsWith(u8, l, "--- a/")) {
            // Deleted files have no new path ("+++ /dev/null"): the old one.
            path = l["--- a/".len..];
        }
        if (r == row) waiting = true;
    }
    if (waiting) if (path) |p| return .{ .path = p, .line = 1 };
    return null;
}

/// The line of the file a diff line of the view stands for (1-based, in
/// the new version; a removed line: where it was).
pub fn hunkLine(h: *const diff.Hunk, index: u32) u32 {
    var n = h.new_start;
    for (h.lines[0..@min(index, h.lines.len)]) |l| {
        if (l.len == 0 or (l[0] != '-' and l[0] != '\\')) n += 1;
    }
    return @max(1, n);
}

/// A `git diff`-style patch adding `contents` as the new file `path`: what
/// an untracked file's diff shows (git only diffs those with --no-index,
/// which exits with 1 when they differ).
pub fn newFileDiff(alloc: Allocator, path: []const u8, contents: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);
    try out.print(alloc, "diff --git a/{s} b/{s}\nnew file (untracked)\n", .{ path, path });
    if (std.mem.indexOfScalar(u8, contents[0..@min(contents.len, 8000)], 0) != null) {
        try out.print(alloc, "Binary files /dev/null and b/{s} differ\n", .{path});
        return out.toOwnedSlice(alloc);
    }
    try out.print(alloc, "--- /dev/null\n+++ b/{s}\n", .{path});
    if (contents.len == 0) return out.toOwnedSlice(alloc);
    const body = if (contents[contents.len - 1] == '\n') contents[0 .. contents.len - 1] else contents;
    try out.print(alloc, "@@ -0,0 +1,{d} @@\n", .{std.mem.count(u8, body, "\n") + 1});
    var it = std.mem.splitScalar(u8, body, '\n');
    while (it.next()) |l| try out.print(alloc, "+{s}\n", .{l});
    if (body.len == contents.len) try out.appendSlice(alloc, "\\ No newline at end of file\n");
    return out.toOwnedSlice(alloc);
}

// ---- Tests ------------------------------------------------------------------------

const testing = std.testing;

test "porcelain v2 parsing" {
    const out = "# branch.oid 1234567890abcdef1234567890abcdef12345678\x00" ++
        "# branch.head main\x00" ++
        "# branch.upstream origin/main\x00" ++
        "# branch.ab +2 -1\x00" ++
        "1 M. N... 100644 100644 100644 aaaa bbbb src/staged file.zig\x00" ++
        "1 .M N... 100644 100644 100644 aaaa aaaa src/edited.zig\x00" ++
        "1 MD N... 100644 100644 000000 aaaa bbbb both.txt\x00" ++
        "2 R. N... 100644 100644 100644 aaaa aaaa R100 new name.zig\x00old name.zig\x00" ++
        "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict.c\x00" ++
        "? notes/todo.txt\x00";
    var st = try parse(testing.allocator, out);
    defer st.deinit();
    try testing.expectEqualStrings("main", st.branch.head.?);
    try testing.expectEqualStrings("origin/main", st.branch.upstream.?);
    try testing.expect(st.branch.has_ab);
    try testing.expectEqual(@as(u32, 2), st.branch.ahead);
    try testing.expectEqual(@as(u32, 1), st.branch.behind);
    try testing.expect(!st.branch.initial());
    try testing.expectEqual(@as(usize, 6), st.entries.len);
    try testing.expectEqualStrings("src/staged file.zig", st.entries[0].path);
    try testing.expectEqual(@as(u8, 'M'), st.entries[0].x);
    try testing.expectEqual(@as(u8, '.'), st.entries[0].y);
    try testing.expectEqual(Kind.renamed, st.entries[3].kind);
    try testing.expectEqualStrings("new name.zig", st.entries[3].path);
    try testing.expectEqualStrings("old name.zig", st.entries[3].orig_path.?);
    try testing.expectEqual(Kind.unmerged, st.entries[4].kind);
    try testing.expectEqualStrings("conflict.c", st.entries[4].path);
    try testing.expectEqual(Kind.untracked, st.entries[5].kind);
    try testing.expectEqualStrings("notes/todo.txt", st.entries[5].path);
    // A file staged and then edited again shows in both sections; conflicts are unstaged.
    try testing.expect(inSection(&st.entries[2], .staged));
    try testing.expect(inSection(&st.entries[2], .unstaged));
    try testing.expect(!inSection(&st.entries[0], .unstaged));
    try testing.expect(!inSection(&st.entries[4], .staged));
    try testing.expect(inSection(&st.entries[4], .unstaged));
}

test "porcelain v2 branch headers" {
    {
        var st = try parse(testing.allocator, "# branch.oid (initial)\x00# branch.head main\x00? a\x00");
        defer st.deinit();
        try testing.expect(st.branch.initial());
        try testing.expect(st.branch.upstream == null);
    }
    {
        var st = try parse(testing.allocator, "# branch.oid abcdef0123\x00# branch.head (detached)\x00");
        defer st.deinit();
        try testing.expect(st.branch.head == null);
        try testing.expectEqual(@as(usize, 0), st.entries.len);
    }
    {
        // A configured upstream that no longer exists: no branch.ab.
        var st = try parse(testing.allocator, "# branch.oid abcdef0123\x00# branch.head topic\x00# branch.upstream origin/topic\x00");
        defer st.deinit();
        try testing.expect(!st.branch.has_ab);
        try testing.expectEqualStrings("origin/topic", st.branch.upstream.?);
    }
    {
        // Garbage and truncated records are skipped.
        var st = try parse(testing.allocator, "1 M\x00x\x00\x00# branch.ab +x -y\x00? ok\x00");
        defer st.deinit();
        try testing.expectEqual(@as(usize, 1), st.entries.len);
        try testing.expect(!st.branch.has_ab);
    }
}

const test_status = "# branch.oid 1234567890\x00# branch.head main\x00# branch.upstream origin/main\x00# branch.ab +1 -0\x00" ++
    "1 M. N... 100644 100644 100644 aaaa bbbb a.zig\x00" ++
    "1 .M N... 100644 100644 100644 aaaa aaaa src/b.zig\x00" ++
    "1 .D N... 100644 100644 000000 aaaa aaaa src/c.zig\x00" ++
    "2 R. N... 100644 100644 100644 aaaa aaaa R100 new.zig\x00old.zig\x00" ++
    "? u.txt\x00";

const test_unstaged =
    \\diff --git a/src/b.zig b/src/b.zig
    \\index 1111111..2222222 100644
    \\--- a/src/b.zig
    \\+++ b/src/b.zig
    \\@@ -1,2 +1,2 @@ fn one() void {
    \\-old
    \\+new
    \\ same
    \\@@ -9 +9 @@
    \\-x
    \\+y
    \\diff --git a/src/c.zig b/src/c.zig
    \\deleted file mode 100644
    \\index 3333333..0000000
    \\--- a/src/c.zig
    \\+++ /dev/null
    \\@@ -1 +0,0 @@
    \\-gone
    \\
;

/// A status as `gather` would leave it.
fn testStatus(gpa: Allocator, unstaged: []const u8) !Status {
    var st = try parse(gpa, test_status);
    const a = st.arena.allocator();
    st.unstaged = try diff.parse(a, try a.dupe(u8, unstaged));
    st.recent = try parseCommits(a, "abc1234\x00Second\ndef5678\x00First\n");
    st.head = st.recent[0];
    st.unpushed = st.recent[0..1];
    st.upstream_head = st.recent[1];
    st.stashes = try a.dupe(Stash, &.{.{ .name = "stash@{0}", .subject = "On main: wip" }});
    return st;
}

test "source control text" {
    const gpa = testing.allocator;
    var view = View.init(gpa);
    defer view.deinit();
    try view.setRoot("/repo");
    try view.rebuild();
    try testing.expectEqualStrings("Reading git status…", view.text.items);

    const first = try view.update(try testStatus(gpa, test_unstaged), 0);
    const expected =
        \\Head:     main Second
        \\Merge:    origin/main First
        \\
        \\Untracked files (1)
        \\u.txt
        \\
        \\Unstaged changes (2)
        \\modified   src/b.zig
        \\deleted    src/c.zig
        \\
        \\Staged changes (2)
        \\modified   a.zig
        \\renamed    old.zig -> new.zig
        \\
        \\Stashes (1)
        \\
        \\Unmerged into origin/main (1)
        \\abc1234 Second
    ;
    try testing.expectEqualStrings(expected, view.text.items);
    try testing.expectEqual(std.mem.count(u8, expected, "\n") + 1, view.targets.items.len);
    // The caret starts on the first file.
    try testing.expectEqual(@as(u32, 4), first);
    try testing.expectEqual(ItemKind.ref, view.targetAt(0).?.kind);
    try testing.expect(view.targetAt(2) == null);
    try testing.expectEqual(Section.unstaged, view.targetAt(6).?.section);
    try testing.expectEqualStrings("src/b.zig", view.entry(view.targetAt(7).?).?.path);
    try testing.expectEqualStrings("abc1234", view.commit(view.targetAt(17).?).?.hash);
    try testing.expectEqualStrings("Second", view.commit(view.targetAt(0).?).?.subject);

    // A section header stands for its files; staged renames take both paths.
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const staged = try view.paths(arena.allocator(), view.targetAt(10).?);
    try testing.expectEqual(@as(usize, 3), staged.len);
    try testing.expectEqualStrings("old.zig", staged[2]);

    // TAB on a file shows its hunks; on a diff line, hides its hunk.
    try testing.expectEqual(@as(?u32, 7), try view.toggle(7));
    try testing.expect(std.mem.indexOf(u8, view.text.items, "modified   src/b.zig\n@@ -1,2 +1,2 @@ fn one() void {\n-old\n+new\n same\n@@ -9 +9 @@\n-x\n+y\ndeleted    src/c.zig") != null);
    try testing.expectEqual(ItemKind.line, view.targetAt(10).?.kind);
    try testing.expectEqual(@as(?u32, 8), try view.toggle(10));
    try testing.expect(std.mem.indexOf(u8, view.text.items, "@@ -1,2 +1,2 @@ fn one() void {\n@@ -9 +9 @@\n-x") != null);
    // Untracked files have no diff to show.
    try testing.expectEqual(@as(?u32, null), try view.toggle(4));
    // n / p and ^ walk headings and parents.
    try testing.expectEqual(@as(?u32, 9), view.nextHeading(8, true));
    try testing.expectEqual(@as(?u32, 7), view.parentRow(9));
    try testing.expectEqual(@as(?u32, 6), view.parentRow(7));
    try testing.expectEqual(@as(?u32, 4), view.nextHeading(3, true));

    // The second hunk is staged: the caret on its line goes to the hunk left.
    try testing.expectEqual(@as(?u32, 8), try view.toggle(8));
    const on_y = std.mem.count(u8, view.text.items[0..std.mem.indexOf(u8, view.text.items, "+y").?], "\n");
    const fewer =
        \\diff --git a/src/b.zig b/src/b.zig
        \\--- a/src/b.zig
        \\+++ b/src/b.zig
        \\@@ -1,2 +1,2 @@ fn one() void {
        \\-old
        \\+new
        \\ same
        \\
    ;
    const row = try view.update(try testStatus(gpa, fewer), @intCast(on_y));
    try testing.expectEqual(ItemKind.line, view.targetAt(row).?.kind);
    try testing.expectEqual(@as(u32, 0), view.targetAt(row).?.hunk);

    // Level 1: only section headings; 4: everything, Stashes too.
    try view.setLevel(1);
    try testing.expect(std.mem.indexOf(u8, view.text.items, "u.txt") == null);
    try view.setLevel(4);
    try testing.expect(std.mem.indexOf(u8, view.text.items, "stash@{0} On main: wip") != null);
    try testing.expect(std.mem.indexOf(u8, view.text.items, "\n-old\n") != null);

    // A file that left its section: the caret goes to the one in its place.
    const r_c = view.rowOf(.{ .kind = .file, .section = .unstaged, .index = 2 }).?;
    const moved = "# branch.oid 1234567890\x00# branch.head main\x00" ++
        "1 .M N... 100644 100644 100644 aaaa aaaa src/b.zig\x00" ++
        "1 D. N... 100644 100644 000000 aaaa aaaa src/c.zig\x00" ++
        "1 .M N... 100644 100644 100644 aaaa aaaa src/d.zig\x00";
    const r2 = try view.update(try parse(gpa, moved), r_c);
    try testing.expectEqualStrings("src/d.zig", view.entry(view.targetAt(r2).?).?.path);

    // Detached; notes replace everything.
    _ = try view.update(try parse(gpa, "# branch.oid abcdef0123\x00# branch.head (detached)\x00"), 0);
    try testing.expectEqualStrings("Head:     abcdef0", view.text.items);
    view.setNote("Not a git repository: {s}", .{"/tmp/x"});
    try view.rebuild();
    try testing.expectEqualStrings("Not a git repository: /tmp/x", view.text.items);
    try testing.expectEqual(@as(?u32, null), view.firstItemRow());
}

test "source control highlighting" {
    const gpa = testing.allocator;
    var view = View.init(gpa);
    defer view.deinit();
    try view.setRoot("/repo");
    try view.setLevel(4);
    _ = try view.update(try testStatus(gpa, test_unstaged), 0);
    var rope = try rope_mod.Rope.fromSlice(gpa, view.text.items);
    defer rope.deinit(gpa);
    const s = theme_mod.mariana.syntax;
    var spans: std.ArrayList(syntax_mod.HighlightSpan) = .empty;
    defer spans.deinit(gpa);
    highlight(gpa, &rope, 0, rope.len(), &s, &spans);
    const colorAt = struct {
        fn f(list: []const syntax_mod.HighlightSpan, off: usize) ?Color {
            for (list) |sp| if (sp.start <= off and off < sp.end) return sp.color;
            return null;
        }
    }.f;
    const text = view.text.items;
    try testing.expectEqual(s.keyword, colorAt(spans.items, std.mem.indexOf(u8, text, "main").?).?);
    try testing.expectEqual(s.string, colorAt(spans.items, std.mem.indexOf(u8, text, "origin/main").?).?);
    try testing.expectEqual(s.function, colorAt(spans.items, std.mem.indexOf(u8, text, "Unstaged changes").?).?);
    try testing.expectEqual(s.comment, colorAt(spans.items, std.mem.indexOf(u8, text, "(2)").?).?);
    try testing.expectEqual(s.number, colorAt(spans.items, std.mem.indexOf(u8, text, "modified   src/b").?).?);
    try testing.expectEqual(s.foreground, colorAt(spans.items, std.mem.indexOf(u8, text, "src/b.zig").?).?);
    try testing.expectEqual(s.constant, colorAt(spans.items, std.mem.indexOf(u8, text, "deleted").?).?);
    try testing.expectEqual(s.string, colorAt(spans.items, std.mem.indexOf(u8, text, "+new").?).?);
    try testing.expectEqual(s.constant, colorAt(spans.items, std.mem.indexOf(u8, text, "-old").?).?);
    try testing.expectEqual(s.function, colorAt(spans.items, std.mem.indexOf(u8, text, "fn one").?).?);
    try testing.expectEqual(s.comment, colorAt(spans.items, std.mem.indexOf(u8, text, "abc1234 Second").?).?);
    try testing.expectEqual(s.number, colorAt(spans.items, std.mem.indexOf(u8, text, "stash@{0}").?).?);
    try testing.expect(colorAt(spans.items, std.mem.indexOf(u8, text, "u.txt").?) == null);
}

test "logs and commit messages" {
    try testing.expectEqualStrings("1a2b3c4", logHash("* 1a2b3c4 (HEAD -> main) Subject \u{2014} Ada, 2 days ago").?);
    try testing.expectEqualStrings("abcdef01", logHash("| | * abcdef01 Merge").?);
    try testing.expect(logHash("|/") == null);
    try testing.expect(logHash("* Subject only") == null);

    const gpa = testing.allocator;
    var rope = try rope_mod.Rope.fromSlice(gpa, "* 1a2b3c4 (HEAD -> main) Subject \u{2014} Ada, 2 days ago\n|/");
    defer rope.deinit(gpa);
    const s = theme_mod.mariana.syntax;
    var spans: std.ArrayList(syntax_mod.HighlightSpan) = .empty;
    defer spans.deinit(gpa);
    logHighlight(gpa, &rope, 0, rope.len(), &s, &spans);
    try testing.expectEqual(@as(usize, 5), spans.items.len);
    try testing.expectEqual(s.number, spans.items[1].color);
    try testing.expectEqual(@as(usize, 10), spans.items[2].start);
    try testing.expectEqual(s.keyword, spans.items[2].color);

    const t = try commitTemplate(gpa, "Fix it\n\nBody\n", "Amending 1a2b3c4 Old", "diff --git a/x b/x\n+y");
    defer gpa.free(t);
    try testing.expect(std.mem.startsWith(u8, t, "Fix it\n\nBody\n\n# Please enter"));
    try testing.expect(std.mem.indexOf(u8, t, "\n# Amending 1a2b3c4 Old\n" ++ scissors ++ "\n") != null);
    try testing.expect(std.mem.endsWith(u8, t, "+y\n"));
    try testing.expectEqualStrings("Fix it\n\nBody\n\n", commitMessage(t)[0..14]);
    try testing.expect(std.mem.indexOf(u8, commitMessage(t), "diff --git") == null);
    try testing.expect(!messageEmpty(commitMessage(t)));

    const empty = try commitTemplate(gpa, "", "On branch main", "");
    defer gpa.free(empty);
    try testing.expect(std.mem.startsWith(u8, empty, "\n# Please enter"));
    try testing.expect(messageEmpty(commitMessage(empty)));
    const kept = try messageText(gpa, commitMessage(t));
    defer gpa.free(kept);
    try testing.expectEqualStrings("Fix it\n\nBody", kept);
    try testing.expectEqualStrings("", commitMessage(scissors ++ "\nx"));
    // A scissors line that's only a prefix doesn't cut.
    try testing.expectEqualStrings("a\n" ++ scissors ++ "x\n", commitMessage("a\n" ++ scissors ++ "x\n"));
}

test "diff locations" {
    const d =
        \\diff --git a/src/x.zig b/src/x.zig
        \\index 1111111..2222222 100644
        \\--- a/src/x.zig
        \\+++ b/src/x.zig
        \\@@ -10,4 +10,5 @@ fn main() void {
        \\ context ten
        \\-removed
        \\+added eleven
        \\+added twelve
        \\ context thirteen
        \\@@ -40 +41 @@
        \\-old
        \\+new
        \\diff --git a/gone.txt b/gone.txt
        \\deleted file mode 100644
        \\--- a/gone.txt
        \\+++ /dev/null
        \\@@ -1,2 +0,0 @@
        \\-a
        \\-b
    ;
    const expectAt = struct {
        fn f(text: []const u8, row: u32, path: []const u8, line_no: u32) !void {
            const loc = diffLocation(text, row) orelse return error.NoLocation;
            try testing.expectEqualStrings(path, loc.path);
            try testing.expectEqual(line_no, loc.line);
        }
    }.f;
    try expectAt(d, 0, "src/x.zig", 10);
    try expectAt(d, 3, "src/x.zig", 10);
    try expectAt(d, 4, "src/x.zig", 10);
    try expectAt(d, 5, "src/x.zig", 10);
    try expectAt(d, 6, "src/x.zig", 11);
    try expectAt(d, 7, "src/x.zig", 11);
    try expectAt(d, 8, "src/x.zig", 12);
    try expectAt(d, 9, "src/x.zig", 13);
    try expectAt(d, 11, "src/x.zig", 41);
    try expectAt(d, 12, "src/x.zig", 41);
    try expectAt(d, 13, "gone.txt", 1);
    try expectAt(d, 18, "gone.txt", 1);
    try testing.expect(diffLocation(d, 99) == null);
    try testing.expect(diffLocation("", 0) == null);

    // The view's diff lines: the same lines of the new version.
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const files = try diff.parse(arena.allocator(), d);
    const h = &files[0].hunks[0];
    try testing.expectEqual(@as(u32, 10), hunkLine(h, 0));
    try testing.expectEqual(@as(u32, 11), hunkLine(h, 1));
    try testing.expectEqual(@as(u32, 12), hunkLine(h, 3));
    try testing.expectEqual(@as(u32, 13), hunkLine(h, 4));
}

test "new file diffs" {
    const gpa = testing.allocator;
    const d = try newFileDiff(gpa, "n.txt", "a\nb\n");
    defer gpa.free(d);
    try testing.expectEqualStrings("diff --git a/n.txt b/n.txt\nnew file (untracked)\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+a\n+b\n", d);
    try testing.expectEqual(@as(u32, 2), diffLocation(d, 6).?.line);
    const partial = try newFileDiff(gpa, "n.txt", "x");
    defer gpa.free(partial);
    try testing.expect(std.mem.endsWith(u8, partial, "@@ -0,0 +1,1 @@\n+x\n\\ No newline at end of file\n"));
    const bin = try newFileDiff(gpa, "b.bin", "\x00\x01");
    defer gpa.free(bin);
    try testing.expect(std.mem.endsWith(u8, bin, "Binary files /dev/null and b/b.bin differ\n"));

    // A newline in a file name can't split its line.
    var view = View.init(gpa);
    defer view.deinit();
    _ = try view.update(try parse(gpa, "# branch.oid (initial)\x00# branch.head main\x00? odd\nname\x00"), 0);
    try testing.expect(std.mem.endsWith(u8, view.text.items, "\nodd?name"));
    try testing.expectEqual(std.mem.count(u8, view.text.items, "\n") + 1, view.targets.items.len);
}

test "status of a temporary repository" {
    const gpa = testing.allocator;
    const io = testing.io;
    if (!git.available(gpa, io)) return error.SkipZigTest;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = try tmp.dir.realPath(io, &path_buf);
    const dir = path_buf[0..n];
    const Git = struct {
        fn run(d: []const u8, args: []const []const u8) !?[]u8 {
            var argv: std.ArrayList([]const u8) = .empty;
            defer argv.deinit(testing.allocator);
            try argv.appendSlice(testing.allocator, &.{ "git", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", "-c", "user.name=Ada", "-c", "user.email=ada@example.com" });
            try argv.appendSlice(testing.allocator, args);
            return (try git.runCommand(testing.allocator, testing.io, argv.items, d, null)) orelse error.GitFailed;
        }
    };
    gpa.free((try Git.run(dir, &.{ "init", "-q", "-b", "main" })).?);
    try tmp.dir.writeFile(io, .{ .sub_path = "kept.txt", .data = "one\n" });
    try tmp.dir.writeFile(io, .{ .sub_path = "old.txt", .data = "moved\n" });
    gpa.free((try Git.run(dir, &.{ "add", "." })).?);
    gpa.free((try Git.run(dir, &.{ "commit", "-q", "-m", "First" })).?);
    try tmp.dir.writeFile(io, .{ .sub_path = "kept.txt", .data = "one\ntwo\n" });
    gpa.free((try Git.run(dir, &.{ "mv", "old.txt", "new file.txt" })).?);
    try tmp.dir.writeFile(io, .{ .sub_path = "fresh.txt", .data = "new\n" });

    var argv: std.ArrayList([]const u8) = .empty;
    defer argv.deinit(gpa);
    try argv.append(gpa, "git");
    try argv.appendSlice(gpa, &status_argv);
    const out = (try git.runCommand(gpa, io, argv.items, dir, null)) orelse return error.GitFailed;
    defer gpa.free(out);
    var st = try parse(gpa, out);
    defer st.deinit();
    gather(&st, io, dir);
    try testing.expectEqualStrings("main", st.branch.head.?);
    try testing.expect(st.branch.upstream == null);
    try testing.expectEqual(@as(usize, 3), st.entries.len);
    try testing.expectEqualStrings("First", st.head.?.subject);
    try testing.expectEqual(@as(usize, 1), st.recent.len);
    try testing.expectEqual(@as(usize, 1), st.unstaged.len);
    try testing.expectEqualStrings("kept.txt", st.unstaged[0].path);
    try testing.expectEqualStrings("+two", st.unstaged[0].hunks[0].lines[1]);
    try testing.expectEqual(@as(usize, 1), st.staged.len);
    try testing.expectEqualStrings("new file.txt", st.staged[0].path);
    var seen: usize = 0;
    for (st.entries) |*e| {
        if (std.mem.eql(u8, e.path, "kept.txt")) {
            try testing.expect(inSection(e, .unstaged) and !inSection(e, .staged));
            seen += 1;
        } else if (std.mem.eql(u8, e.path, "new file.txt")) {
            try testing.expectEqual(Kind.renamed, e.kind);
            try testing.expectEqualStrings("old.txt", e.orig_path.?);
            try testing.expect(inSection(e, .staged));
            seen += 1;
        } else if (std.mem.eql(u8, e.path, "fresh.txt")) {
            try testing.expect(inSection(e, .untracked));
            seen += 1;
        }
    }
    try testing.expectEqual(@as(usize, 3), seen);

    // A hunk's patch stages with `git apply --cached`.
    const patch = (try diff.partialPatch(st.arena.allocator(), &st.unstaged[0], &st.unstaged[0].hunks[0], 0, 9, false)).?;
    const applied = (try git.runCommand(gpa, io, &.{ "git", "apply", "--cached", "-" }, dir, patch)) orelse return error.GitFailed;
    gpa.free(applied);
    const cached = (try git.runCommand(gpa, io, &.{ "git", "diff", "--cached", "--name-only" }, dir, null)) orelse return error.GitFailed;
    defer gpa.free(cached);
    try testing.expect(std.mem.indexOf(u8, cached, "kept.txt") != null);
}
