//! Git blame for a buffer: `git blame --porcelain --contents -` runs on the
//! buffer's current text (so unsaved edits line up) on a worker thread and
//! is parsed into a commit per line. Between runs, buffer changes move the
//! rows along and edited lines read as uncommitted, like Zed's blame.

const std = @import("std");
const git = @import("git.zig");
const buffer_mod = @import("../text/buffer.zig");

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;
const Change = buffer_mod.Change;

pub const Commit = struct {
    /// Full hex hash; all zeros for lines that aren't committed.
    hash: []const u8,
    author: []const u8 = "",
    author_mail: []const u8 = "",
    /// Unix seconds.
    author_time: i64 = 0,
    /// Minutes east of UTC.
    author_tz: i32 = 0,
    summary: []const u8 = "",

    pub fn isUncommitted(self: *const Commit) bool {
        for (self.hash) |ch| if (ch != '0') return false;
        return true;
    }

    pub fn shortHash(self: *const Commit) []const u8 {
        return self.hash[0..@min(self.hash.len, 7)];
    }
};

/// Shown for lines edited since the last run.
pub const uncommitted: Commit = .{
    .hash = "0000000000000000000000000000000000000000",
    .author = "Not Committed Yet",
    .summary = "Uncommitted changes",
};

/// Row value: nothing to show (the empty line after a final newline, or
/// rows of a file git couldn't blame).
pub const no_commit: u32 = std.math.maxInt(u32);
/// Row value: edited since the last run.
pub const edited: u32 = std.math.maxInt(u32) - 1;

// ---- Porcelain output -------------------------------------------------------------

pub const Porcelain = struct {
    arena: std.heap.ArenaAllocator,
    commits: []Commit,
    /// Index into `commits` per line, or `no_commit`.
    lines: []u32,

    pub fn deinit(self: *Porcelain) void {
        self.arena.deinit();
    }
};

fn isHash(s: []const u8) bool {
    if (s.len != 40 and s.len != 64) return false;
    for (s) |ch| if (!std.ascii.isHex(ch)) return false;
    return true;
}

/// "+0200" / "-0430" → minutes east of UTC.
fn parseTz(s: []const u8) i32 {
    if (s.len != 5 or (s[0] != '+' and s[0] != '-')) return 0;
    const hh = std.fmt.parseInt(i32, s[1..3], 10) catch return 0;
    const mm = std.fmt.parseInt(i32, s[3..5], 10) catch return 0;
    const v = hh * 60 + mm;
    return if (s[0] == '-') -v else v;
}

/// Parses `git blame --porcelain`: each line is "<hash> <orig> <final> [<n>]",
/// the commit's headers the first time it appears, then "\t<text>".
/// `min_lines` pads the result (with `no_commit`) to the buffer's row count.
pub fn parsePorcelain(gpa: Allocator, text: []const u8, min_lines: usize) !Porcelain {
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const alloc = arena.allocator();
    var commits: std.ArrayList(Commit) = .empty;
    var index: std.StringHashMapUnmanaged(u32) = .empty;
    var lines: std.ArrayList(u32) = .empty;
    var current: ?u32 = null;
    var final_row: usize = 0;

    var it = std.mem.splitScalar(u8, text, '\n');
    while (it.next()) |line| {
        if (line.len == 0) continue;
        if (line[0] == '\t') {
            const c = current orelse continue;
            if (lines.items.len <= final_row) try lines.appendNTimes(alloc, no_commit, final_row + 1 - lines.items.len);
            lines.items[final_row] = c;
            continue;
        }
        const sp = std.mem.indexOfScalar(u8, line, ' ') orelse line.len;
        const key = line[0..sp];
        const value = if (sp < line.len) line[sp + 1 ..] else "";
        if (isHash(key)) {
            var nums = std.mem.tokenizeScalar(u8, value, ' ');
            _ = nums.next();
            const final = std.fmt.parseInt(usize, nums.next() orelse continue, 10) catch continue;
            if (final == 0) continue;
            const gop = try index.getOrPut(alloc, key);
            if (!gop.found_existing) {
                gop.key_ptr.* = try alloc.dupe(u8, key);
                gop.value_ptr.* = @intCast(commits.items.len);
                try commits.append(alloc, .{ .hash = gop.key_ptr.* });
            }
            current = gop.value_ptr.*;
            final_row = final - 1;
            continue;
        }
        const c = &commits.items[current orelse continue];
        if (std.mem.eql(u8, key, "author")) {
            c.author = try alloc.dupe(u8, value);
        } else if (std.mem.eql(u8, key, "author-mail")) {
            c.author_mail = try alloc.dupe(u8, std.mem.trim(u8, value, "<>"));
        } else if (std.mem.eql(u8, key, "author-time")) {
            c.author_time = std.fmt.parseInt(i64, value, 10) catch 0;
        } else if (std.mem.eql(u8, key, "author-tz")) {
            c.author_tz = parseTz(value);
        } else if (std.mem.eql(u8, key, "summary")) {
            c.summary = try alloc.dupe(u8, value);
        }
    }
    if (lines.items.len < min_lines) try lines.appendNTimes(alloc, no_commit, min_lines - lines.items.len);
    return .{ .arena = arena, .commits = commits.items, .lines = lines.items };
}

// ---- Dates --------------------------------------------------------------------------

pub fn unixNow() i64 {
    var ts: std.c.timespec = undefined;
    _ = std.c.clock_gettime(.REALTIME, &ts);
    return ts.sec;
}

/// "just now", "5 minutes ago", "3 days ago", "2 months ago", "4 years ago".
pub fn formatRelative(buf: []u8, now: i64, then: i64) []const u8 {
    const diff = now - then;
    if (diff < 60) return "just now";
    const Unit = struct { name: []const u8, value: i64 };
    const days = @divTrunc(diff, 86400);
    const u: Unit = if (diff < 3600)
        .{ .name = "minute", .value = @divTrunc(diff, 60) }
    else if (diff < 36 * 3600)
        .{ .name = "hour", .value = @divTrunc(diff, 3600) }
    else if (days < 14)
        .{ .name = "day", .value = @max(1, days) }
    else if (days < 70)
        .{ .name = "week", .value = @divTrunc(days, 7) }
    else if (days < 365)
        .{ .name = "month", .value = @min(11, @divTrunc(days, 30)) }
    else
        .{ .name = "year", .value = @divTrunc(days, 365) };
    return std.fmt.bufPrint(buf, "{d} {s}{s} ago", .{ u.value, u.name, if (u.value == 1) "" else "s" }) catch "";
}

// ---- Keeping rows in step with edits ------------------------------------------------

/// What a buffer change does to rows: rows [start, start + old_count)
/// become [start, start + new_count), all edited except `keep`, which is
/// an old row that survives unchanged.
pub const RowEdit = struct {
    start: u32,
    old_count: u32,
    new_count: u32,
    keep: enum { none, first, last } = .none,
};

/// `first_len` / `last_len`: lengths of the change's first and last row
/// after the change (to tell "enter at the end of a line" from an edit).
pub fn rowEdit(change: Change, first_len: u32, last_len: u32) RowEdit {
    const r0 = change.start_point.row;
    const r1 = change.old_end_point.row;
    const r2 = change.new_end_point.row;
    const c0 = change.start_point.column;
    var e: RowEdit = .{ .start = r0, .old_count = r1 - r0 + 1, .new_count = r2 - r0 + 1 };
    if (r1 == r0 and r2 == r0) return e;
    if (c0 == 0 and change.old_end_point.column == 0 and change.new_end_point.column == 0) {
        // Whole lines inserted, replaced or deleted before row r1.
        e.keep = .last;
    } else if (first_len == c0) {
        // Nothing follows the change on its first row. That row is intact
        // if the change began at its end: an insertion that leaves no tail
        // on the last row, or a deletion that starts with the newline.
        const at_eol = if (change.old_len == 0)
            last_len == change.new_end_point.column
        else
            r1 == r0 + 1 and change.old_len == change.old_end_point.column + 1;
        if (at_eol) e.keep = .first;
    }
    return e;
}

pub fn applyRowEdit(gpa: Allocator, rows: *std.ArrayList(u32), e: RowEdit) !void {
    const len = rows.items.len;
    if (e.start >= len) return;
    const old_count = @min(e.old_count, len - e.start);
    const kept: ?u32 = switch (e.keep) {
        .none => null,
        .first => rows.items[e.start],
        .last => rows.items[e.start + old_count - 1],
    };
    const tail = e.start + old_count;
    if (e.new_count > old_count) {
        const grow = e.new_count - old_count;
        try rows.resize(gpa, len + grow);
        std.mem.copyBackwards(u32, rows.items[tail + grow ..], rows.items[tail..len]);
    } else if (e.new_count < old_count) {
        const shrink = old_count - e.new_count;
        std.mem.copyForwards(u32, rows.items[tail - shrink ..], rows.items[tail..len]);
        rows.shrinkRetainingCapacity(len - shrink);
    }
    const new_rows = rows.items[e.start .. e.start + e.new_count];
    @memset(new_rows, edited);
    if (kept) |k| switch (e.keep) {
        .first => new_rows[0] = k,
        .last => new_rows[new_rows.len - 1] = k,
        .none => {},
    };
}

// ---- A buffer's blame ---------------------------------------------------------------

pub const Notify = struct {
    ctx: *anyopaque,
    onUpdate: *const fn (ctx: *anyopaque, blame: *Blame) void,
};

/// Blame of one buffer, owned by the workspace. Views forward their
/// buffer's changes to `onChange` (each change once, by version). Job
/// callbacks never touch the buffer (it may be closed by then): follow-up
/// runs are left to the owner, which calls `refresh` when `stale` is set.
pub const Blame = struct {
    runner: *git.Runner,
    buffer: *Buffer,
    /// Buffers are compared by id too (a freed buffer's address can be reused).
    buffer_id: u32,
    /// The file blamed and its folder (git runs there).
    path: []u8,
    root: ?[]const u8 = null,
    notify: ?Notify = null,

    result: ?Porcelain = null,
    /// Commit index per buffer row (or `edited` / `no_commit`).
    rows: std.ArrayList(u32) = .empty,
    /// Buffer version `rows` matches.
    version: u64,
    state: enum { idle, running, unavailable } = .idle,
    job: ?*git.Job = null,
    /// The running job blames a snapshot: edits made since are replayed on its result.
    pending: std.ArrayList(RowEdit) = .empty,
    snapshot_taken: bool = false,
    /// Another run was asked for while one ran.
    rerun: bool = false,
    /// A run is due (queued, root just found, or changes were missed).
    stale: bool = false,
    /// Full commit descriptions (hash → text) for the commit popup.
    details: std.StringHashMapUnmanaged([]u8) = .empty,
    /// The repository's state when the last run started (see `repoChanged`).
    repo_stamp: i128 = 0,

    pub fn create(runner: *git.Runner, buffer: *Buffer, notify: ?Notify) !*Blame {
        const gpa = runner.gpa;
        const self = try gpa.create(Blame);
        errdefer gpa.destroy(self);
        self.* = .{
            .runner = runner,
            .buffer = buffer,
            .buffer_id = buffer.id,
            .path = try gpa.dupe(u8, buffer.path orelse return error.NoPath),
            .notify = notify,
            .version = buffer.version,
        };
        return self;
    }

    pub fn destroy(self: *Blame) void {
        const gpa = self.runner.gpa;
        if (self.job) |j| j.detach();
        if (self.result) |*r| r.deinit();
        self.rows.deinit(gpa);
        self.pending.deinit(gpa);
        var it = self.details.iterator();
        while (it.next()) |e| {
            gpa.free(e.key_ptr.*);
            gpa.free(e.value_ptr.*);
        }
        self.details.deinit(gpa);
        gpa.free(self.path);
        gpa.destroy(self);
    }

    pub fn dir(self: *const Blame) []const u8 {
        return std.fs.path.dirname(self.path) orelse "/";
    }

    pub fn isFor(self: *const Blame, buffer: *const Buffer) bool {
        return self.buffer == buffer and self.buffer_id == buffer.id;
    }

    /// Whether there is anything to show.
    pub fn hasData(self: *const Blame) bool {
        return self.result != null and self.rows.items.len > 0;
    }

    /// A run is going on or about to start.
    pub fn busy(self: *const Blame) bool {
        return self.state == .running or self.stale;
    }

    /// The commit of `row`, or null when there's nothing to show.
    pub fn commitAt(self: *const Blame, row: u32) ?*const Commit {
        if (row >= self.rows.items.len) return null;
        const r = self.result orelse return null;
        const v = self.rows.items[row];
        if (v == no_commit) return null;
        if (v == edited or v >= r.commits.len) return &uncommitted;
        return &r.commits[v];
    }

    /// Rows `a` and `b` show the same commit (uncommitted lines group together).
    pub fn sameCommit(self: *const Blame, a: u32, b: u32) bool {
        const ca = self.commitAt(a) orelse return false;
        const cb = self.commitAt(b) orelse return false;
        if (ca == cb) return true;
        return ca.isUncommitted() and cb.isUncommitted();
    }

    /// Starts a run (or queues one behind the running one).
    pub fn refresh(self: *Blame) void {
        self.stale = false;
        if (self.state == .running) {
            self.rerun = true;
            return;
        }
        if (self.runner.missing or self.buffer.len() > git.max_output) {
            self.state = .unavailable;
            return;
        }
        if (self.runner.cachedRoot(self.dir())) |root| {
            if (root.len == 0) {
                self.state = .unavailable;
                return;
            }
            self.root = root;
            self.startBlame();
            return;
        }
        self.job = git.Job.start(self.runner, &.{ "rev-parse", "--show-toplevel" }, self.dir(), null, null, self, onRevParse) catch {
            self.state = .unavailable;
            return;
        };
        self.snapshot_taken = false;
        self.state = .running;
    }

    fn onRevParse(ctx: *anyopaque, job: *git.Job) void {
        const self: *Blame = @ptrCast(@alignCast(ctx));
        self.job = null;
        self.state = .idle;
        const root = if (job.output) |o| git.parseToplevel(o) else "";
        self.runner.rememberRoot(self.dir(), root);
        job.destroy();
        self.rerun = false;
        if (self.runner.cachedRoot(self.dir())) |r| if (r.len > 0) {
            // The blame itself runs from the owner's next `refresh`.
            self.stale = true;
            if (self.notify) |n| n.onUpdate(n.ctx, self);
            return;
        };
        self.state = .unavailable;
        if (self.notify) |n| n.onUpdate(n.ctx, self);
    }

    /// See `git.repoStamp`.
    fn readRepoStamp(self: *const Blame) i128 {
        const root = self.root orelse return 0;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const git_dir = std.fmt.bufPrint(&buf, "{s}/.git", .{root}) catch return 0;
        return git.repoStamp(self.runner.io, git_dir);
    }

    /// Whether the repository changed since the last run (a commit made
    /// outside the editor changes blame without touching the file).
    pub fn repoChanged(self: *const Blame) bool {
        if (self.state != .idle or self.root == null) return false;
        return self.readRepoStamp() != self.repo_stamp;
    }

    fn startBlame(self: *Blame) void {
        const gpa = self.runner.gpa;
        self.repo_stamp = self.readRepoStamp();
        const text = self.snapshotText(gpa) catch return;
        defer gpa.free(text);
        const argv = [_][]const u8{ "blame", "--porcelain", "--contents", "-", "--", std.fs.path.basename(self.path) };
        const job = git.Job.start(self.runner, &argv, self.dir(), text, parseJob, self, onBlame) catch {
            self.state = .unavailable;
            return;
        };
        job.free_payload = freeParsed;
        self.job = job;
        self.pending.clearRetainingCapacity();
        self.snapshot_taken = true;
        self.state = .running;
    }

    /// The buffer's text as it is on disk (CRLF files compare as CRLF).
    fn snapshotText(self: *Blame, gpa: Allocator) ![]u8 {
        const text = try self.buffer.rope.toOwnedSlice(gpa);
        if (self.buffer.line_ending != .crlf) return text;
        defer gpa.free(text);
        const out = try gpa.alloc(u8, text.len + std.mem.count(u8, text, "\n"));
        var j: usize = 0;
        for (text) |ch| {
            if (ch == '\n') {
                out[j] = '\r';
                j += 1;
            }
            out[j] = ch;
            j += 1;
        }
        return out;
    }

    /// Worker thread: parse the output into the job's payload.
    fn parseJob(job: *git.Job) void {
        const gpa = job.runner.gpa;
        const input = job.input orelse "";
        const rows = std.mem.count(u8, input, "\n") + 1;
        const p = gpa.create(Porcelain) catch return;
        p.* = parsePorcelain(gpa, job.output.?, rows) catch {
            gpa.destroy(p);
            return;
        };
        job.payload = p;
    }

    fn freeParsed(job: *git.Job) void {
        const p: *Porcelain = @ptrCast(@alignCast(job.payload.?));
        p.deinit();
        job.runner.gpa.destroy(p);
    }

    fn onBlame(ctx: *anyopaque, job: *git.Job) void {
        const self: *Blame = @ptrCast(@alignCast(ctx));
        const gpa = self.runner.gpa;
        self.job = null;
        self.state = .idle;
        defer job.destroy();
        if (job.payload) |ptr| {
            const p: *Porcelain = @ptrCast(@alignCast(ptr));
            job.payload = null;
            defer gpa.destroy(p);
            if (self.result) |*old| old.deinit();
            self.result = p.*;
            self.rows.clearRetainingCapacity();
            self.rows.appendSlice(gpa, p.lines) catch {};
            for (self.pending.items) |e| applyRowEdit(gpa, &self.rows, e) catch {};
        } else if (self.result == null) {
            // Untracked file, a repository without commits...: nothing to show.
            self.rows.clearRetainingCapacity();
        }
        self.pending.clearRetainingCapacity();
        if (self.rerun) {
            self.rerun = false;
            self.stale = true;
        }
        if (self.notify) |n| n.onUpdate(n.ctx, self);
    }

    /// Buffer listener side (called by every view of the buffer; applied once).
    pub fn onChange(self: *Blame, buffer: *Buffer, change: Change) void {
        if (buffer.version <= self.version) return;
        const gpa = self.runner.gpa;
        if (buffer.version != self.version + 1) {
            // Changes were missed: the rows can't be mapped any more.
            self.version = buffer.version;
            self.rows.clearRetainingCapacity();
            self.pending.clearRetainingCapacity();
            self.stale = true;
            return;
        }
        self.version = buffer.version;
        const first_len = buffer.lineLen(change.start_point.row);
        const last_len = buffer.lineLen(change.new_end_point.row);
        const e = rowEdit(change, first_len, last_len);
        if (self.state == .running and self.snapshot_taken) self.pending.append(gpa, e) catch {
            self.stale = true;
        };
        if (self.rows.items.len > 0) applyRowEdit(gpa, &self.rows, e) catch {
            self.rows.clearRetainingCapacity();
            self.stale = true;
        };
    }

    // ---- Commit details (git show) ------------------------------------------------

    /// Starts `git show` for `hash`; `done` gets the job (see `storeDetails`).
    pub fn fetchDetails(self: *Blame, hash: []const u8, ctx: *anyopaque, done: *const fn (ctx: *anyopaque, job: *git.Job) void) ?*git.Job {
        const argv = [_][]const u8{ "show", "-s", "--no-color", "--format=%H%x00%an%x00%ae%x00%at%x00%ai%x00%B", hash };
        return git.Job.start(self.runner, &argv, self.dir(), null, null, ctx, done) catch null;
    }

    /// Caches a finished `git show` (by hash) and returns the popup text.
    pub fn storeDetails(self: *Blame, hash: []const u8, output: []const u8) ?[]const u8 {
        const gpa = self.runner.gpa;
        if (self.details.get(hash)) |d| return d;
        const text = formatDetails(gpa, output, unixNow()) catch return null;
        const key = gpa.dupe(u8, hash) catch {
            gpa.free(text);
            return null;
        };
        self.details.put(gpa, key, text) catch {
            gpa.free(key);
            gpa.free(text);
            return null;
        };
        return text;
    }
};

/// `git show --format=%H%x00%an%x00%ae%x00%at%x00%ai%x00%B` → text like
/// `git log`'s header and indented message.
pub fn formatDetails(gpa: Allocator, output: []const u8, now: i64) ![]u8 {
    var parts = std.mem.splitScalar(u8, output, 0);
    const hash = parts.next() orelse "";
    const name = parts.next() orelse "";
    const mail = parts.next() orelse "";
    const time = std.fmt.parseInt(i64, parts.next() orelse "0", 10) catch 0;
    const date = parts.next() orelse "";
    const message = std.mem.trim(u8, parts.rest(), " \r\n");
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var rel_buf: [48]u8 = undefined;
    try out.print(gpa, "commit {s}\nAuthor: {s} <{s}>\nDate:   {s} ({s})\n", .{
        std.mem.trim(u8, hash, " \r\n"), name, mail, date, formatRelative(&rel_buf, now, time),
    });
    var lines = std.mem.splitScalar(u8, message, '\n');
    try out.append(gpa, '\n');
    while (lines.next()) |l| {
        const line = std.mem.trimEnd(u8, l, "\r");
        if (line.len > 0) try out.appendSlice(gpa, "    ");
        try out.appendSlice(gpa, line);
        try out.append(gpa, '\n');
    }
    // No trailing newline (the popup would show an empty last line).
    if (out.items.len > 0) out.items.len -= 1;
    return out.toOwnedSlice(gpa);
}

// ---- Tests ------------------------------------------------------------------------------

const testing = std.testing;

test "porcelain parsing" {
    const text =
        "1111111111111111111111111111111111111111 1 1 2\n" ++
        "author Ada Lovelace\n" ++
        "author-mail <ada@example.com>\n" ++
        "author-time 1700000000\n" ++
        "author-tz +0130\n" ++
        "committer Ada Lovelace\n" ++
        "committer-mail <ada@example.com>\n" ++
        "committer-time 1700000000\n" ++
        "committer-tz +0130\n" ++
        "summary First commit\n" ++
        "boundary\n" ++
        "filename a.txt\n" ++
        "\tone\n" ++
        "1111111111111111111111111111111111111111 2 2\n" ++
        "\ttwo\n" ++
        "0000000000000000000000000000000000000000 3 3 1\n" ++
        "author Not Committed Yet\n" ++
        "author-mail <not.committed.yet>\n" ++
        "author-time 1700000500\n" ++
        "author-tz -0500\n" ++
        "summary Version of a.txt from a.txt\n" ++
        "previous 1111111111111111111111111111111111111111 a.txt\n" ++
        "filename a.txt\n" ++
        "\tthree\n" ++
        "2222222222222222222222222222222222222222 3 4 1\n" ++
        "author Charles Babbage\n" ++
        "author-mail <cb@example.com>\n" ++
        "author-time 1700000100\n" ++
        "author-tz +0000\n" ++
        "summary Second: with spaces\n" ++
        "filename a.txt\n" ++
        "\t\tindented four\n";
    var p = try parsePorcelain(testing.allocator, text, 5);
    defer p.deinit();
    try testing.expectEqual(@as(usize, 3), p.commits.len);
    try testing.expectEqualSlices(u32, &.{ 0, 0, 1, 2, no_commit }, p.lines);
    const first = p.commits[0];
    try testing.expectEqualStrings("Ada Lovelace", first.author);
    try testing.expectEqualStrings("ada@example.com", first.author_mail);
    try testing.expectEqual(@as(i64, 1700000000), first.author_time);
    try testing.expectEqual(@as(i32, 90), first.author_tz);
    try testing.expectEqualStrings("First commit", first.summary);
    try testing.expectEqualStrings("1111111", first.shortHash());
    try testing.expect(!first.isUncommitted());
    try testing.expect(p.commits[1].isUncommitted());
    try testing.expectEqual(@as(i32, -300), p.commits[1].author_tz);
    try testing.expectEqualStrings("Second: with spaces", p.commits[2].summary);
    // Empty output (e.g. an empty file) still covers the buffer's rows.
    var empty = try parsePorcelain(testing.allocator, "", 1);
    defer empty.deinit();
    try testing.expectEqualSlices(u32, &.{no_commit}, empty.lines);
}

test "relative dates" {
    var buf: [48]u8 = undefined;
    const now: i64 = 1_800_000_000;
    try testing.expectEqualStrings("just now", formatRelative(&buf, now, now - 5));
    try testing.expectEqualStrings("just now", formatRelative(&buf, now, now + 3600));
    try testing.expectEqualStrings("1 minute ago", formatRelative(&buf, now, now - 61));
    try testing.expectEqualStrings("45 minutes ago", formatRelative(&buf, now, now - 45 * 60));
    try testing.expectEqualStrings("1 hour ago", formatRelative(&buf, now, now - 3600));
    try testing.expectEqualStrings("35 hours ago", formatRelative(&buf, now, now - 35 * 3600));
    try testing.expectEqualStrings("3 days ago", formatRelative(&buf, now, now - 3 * 86400 - 100));
    try testing.expectEqualStrings("2 weeks ago", formatRelative(&buf, now, now - 15 * 86400));
    try testing.expectEqualStrings("3 months ago", formatRelative(&buf, now, now - 95 * 86400));
    try testing.expectEqualStrings("11 months ago", formatRelative(&buf, now, now - 362 * 86400));
    try testing.expectEqualStrings("1 year ago", formatRelative(&buf, now, now - 400 * 86400));
    try testing.expectEqualStrings("6 years ago", formatRelative(&buf, now, now - 6 * 366 * 86400));
}

test "rows follow edits" {
    const gpa = testing.allocator;
    var buffer = try Buffer.initText(gpa, 1, "a\nb\nc\nd\n");
    defer buffer.deinit();
    var rows: std.ArrayList(u32) = .empty;
    defer rows.deinit(gpa);
    try rows.appendSlice(gpa, &.{ 0, 1, 2, 3, no_commit });
    const Recorder = struct {
        rows: *std.ArrayList(u32),
        fn onChange(ctx: *anyopaque, b: *Buffer, change: Change) void {
            const r: *@This() = @ptrCast(@alignCast(ctx));
            const e = rowEdit(change, b.lineLen(change.start_point.row), b.lineLen(change.new_end_point.row));
            applyRowEdit(testing.allocator, r.rows, e) catch unreachable;
        }
    };
    var rec: Recorder = .{ .rows = &rows };
    try buffer.addListener(.{ .ctx = &rec, .onChange = Recorder.onChange });

    // Enter at the end of "a": "a" stays, the new line is edited.
    try buffer.edit(&.{.{ .start = 1, .end = 1, .text = "\n" }}, .other, &.{}, &.{});
    try testing.expectEqualSlices(u32, &.{ 0, edited, 1, 2, 3, no_commit }, rows.items);
    // Typing inside "c" edits just that row.
    try buffer.edit(&.{.{ .start = 5, .end = 5, .text = "x" }}, .other, &.{}, &.{});
    try testing.expectEqualSlices(u32, &.{ 0, edited, 1, edited, 3, no_commit }, rows.items);
    // Deleting the whole line "b\n" moves later rows up.
    try buffer.edit(&.{.{ .start = 3, .end = 5, .text = "" }}, .other, &.{}, &.{});
    try testing.expectEqualSlices(u32, &.{ 0, edited, edited, 3, no_commit }, rows.items);
    // Backspace joining the empty row 1 into "a" keeps "a".
    try buffer.edit(&.{.{ .start = 1, .end = 2, .text = "" }}, .other, &.{}, &.{});
    try testing.expectEqualSlices(u32, &.{ 0, edited, 3, no_commit }, rows.items);
    // Pasting two whole lines above "d".
    const d_start = buffer.lineStart(2);
    try buffer.edit(&.{.{ .start = d_start, .end = d_start, .text = "p\nq\n" }}, .other, &.{}, &.{});
    try testing.expectEqualSlices(u32, &.{ 0, edited, edited, edited, 3, no_commit }, rows.items);
    // An edit spanning rows mid-line marks the joined row.
    try buffer.edit(&.{.{ .start = 0, .end = buffer.lineStart(1) + 1, .text = "z" }}, .other, &.{}, &.{});
    try testing.expectEqualSlices(u32, &.{ edited, edited, edited, 3, no_commit }, rows.items);
    try testing.expectEqual(buffer.lineCount(), @as(u32, @intCast(rows.items.len)));
}

test "commit details text" {
    const out = "abc123\x00Ada\x00ada@example.com\x001700000000\x002023-11-14 22:13:20 +0000\x00Subject line\n\nBody text.\n\n";
    const text = try formatDetails(testing.allocator, out, 1700000000 + 2 * 86400);
    defer testing.allocator.free(text);
    try testing.expectEqualStrings(
        "commit abc123\nAuthor: Ada <ada@example.com>\nDate:   2023-11-14 22:13:20 +0000 (2 days ago)\n\n    Subject line\n\n    Body text.",
        text,
    );
}

fn sleepMs(ms: u32) void {
    var req: std.c.timespec = .{ .sec = 0, .nsec = @as(isize, ms) * std.time.ns_per_ms };
    _ = std.c.nanosleep(&req, null);
}

/// Runs the executor (and follow-up runs, as the workspace would) until idle.
fn settle(blame: *Blame, executor: anytype) void {
    var tries: usize = 0;
    while ((blame.state == .running or blame.stale) and tries < 5000) : (tries += 1) {
        executor.drain();
        if (blame.stale) blame.refresh();
        if (blame.state == .running) sleepMs(1);
    }
}

test "blame a temporary repository" {
    const gpa = testing.allocator;
    const io = testing.io;
    if (!git.available(gpa, io)) return error.SkipZigTest;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = try tmp.dir.realPath(io, &path_buf);
    const dir = path_buf[0..n];

    const Git = struct {
        fn run(d: []const u8, args: []const []const u8) !void {
            var argv: std.ArrayList([]const u8) = .empty;
            defer argv.deinit(testing.allocator);
            try argv.appendSlice(testing.allocator, &.{ "git", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null" });
            try argv.appendSlice(testing.allocator, args);
            const out = try git.runCommand(testing.allocator, testing.io, argv.items, d, null);
            defer if (out) |o| testing.allocator.free(o);
            if (out == null) return error.GitFailed;
        }
    };
    try Git.run(dir, &.{ "init", "-q" });
    try tmp.dir.writeFile(io, .{ .sub_path = "f.txt", .data = "one\ntwo\n" });
    try Git.run(dir, &.{ "add", "f.txt" });
    try Git.run(dir, &.{ "-c", "user.name=Ada", "-c", "user.email=ada@example.com", "commit", "-q", "-m", "First", "--date=2020-01-01T00:00:00Z" });
    try tmp.dir.writeFile(io, .{ .sub_path = "f.txt", .data = "one\ntwo\nthree\n" });
    try Git.run(dir, &.{ "-c", "user.name=Bob", "-c", "user.email=bob@example.com", "commit", "-q", "-a", "-m", "Second\n\nMore words.", "--date=2021-06-01T12:00:00Z" });

    // The buffer has an unsaved line at the top.
    const file = try std.fs.path.join(gpa, &.{ dir, "f.txt" });
    defer gpa.free(file);
    var buffer = try Buffer.initText(gpa, 1, "zero\none\ntwo\nthree\n");
    defer buffer.deinit();
    try buffer.setPath(file);

    var executor = @import("../../framework/util/executor.zig").Executor.init(gpa);
    defer executor.deinit();
    var runner = git.Runner.init(gpa, io, &executor);
    defer runner.deinit();
    const blame = try Blame.create(&runner, &buffer, null);
    defer blame.destroy();
    blame.refresh();
    settle(blame, &executor);
    try testing.expect(blame.hasData());
    try testing.expectEqual(@as(usize, 5), blame.rows.items.len);
    try testing.expect(blame.commitAt(0).?.isUncommitted());
    try testing.expectEqualStrings("Ada", blame.commitAt(1).?.author);
    try testing.expectEqualStrings("First", blame.commitAt(2).?.summary);
    try testing.expectEqualStrings("Bob", blame.commitAt(3).?.author);
    try testing.expectEqual(@as(i64, 1622548800), blame.commitAt(3).?.author_time);
    try testing.expect(blame.sameCommit(1, 2));
    try testing.expect(!blame.sameCommit(2, 3));
    try testing.expect(blame.commitAt(4) == null);
    try testing.expect(blame.root != null);

    // Commit details for the popup.
    const Wait = struct {
        output: ?[]u8 = null,
        finished: bool = false,
        fn done(ctx: *anyopaque, job: *git.Job) void {
            const w: *@This() = @ptrCast(@alignCast(ctx));
            w.finished = true;
            w.output = job.output;
            job.output = null;
            job.destroy();
        }
    };
    var w: Wait = .{};
    _ = blame.fetchDetails(blame.commitAt(3).?.hash, &w, Wait.done) orelse return error.NoJob;
    var tries: usize = 0;
    while (!w.finished and tries < 2000) : (tries += 1) {
        executor.drain();
        if (!w.finished) sleepMs(1);
    }
    defer if (w.output) |o| gpa.free(o);
    const details = blame.storeDetails(blame.commitAt(3).?.hash, w.output orelse return error.NoOutput).?;
    try testing.expect(std.mem.indexOf(u8, details, "Author: Bob <bob@example.com>") != null);
    try testing.expect(std.mem.indexOf(u8, details, "    More words.") != null);

    // A blame dropped mid-run: its job finishes and frees itself (runner.deinit waits).
    const dropped = try Blame.create(&runner, &buffer, null);
    dropped.refresh();
    try testing.expect(dropped.state == .running);
    dropped.destroy();

    // Outside a repository (or untracked in an enclosing one): nothing, quietly.
    var plain = testing.tmpDir(.{});
    defer plain.cleanup();
    const pn = try plain.dir.realPath(io, &path_buf);
    const other = try std.fs.path.join(gpa, &.{ path_buf[0..pn], "x.txt" });
    defer gpa.free(other);
    var buffer2 = try Buffer.initText(gpa, 2, "x\n");
    defer buffer2.deinit();
    try buffer2.setPath(other);
    const blame2 = try Blame.create(&runner, &buffer2, null);
    defer blame2.destroy();
    blame2.refresh();
    settle(blame2, &executor);
    try testing.expect(!blame2.hasData());
}
