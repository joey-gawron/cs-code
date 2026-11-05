//! Find in Files: searches many files on a background thread and formats
//! Sublime-style "Find Results" text (file headers, numbered match lines
//! with a little context). The main thread gets the finished text plus the
//! byte ranges of every match, so it can drop them into a results buffer.

const std = @import("std");
const builtin = @import("builtin");
const Executor = @import("../../framework/util/executor.zig").Executor;
const regex_mod = @import("../../framework/util/regex.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const rope_mod = @import("../text/rope.zig");
const syntax_mod = @import("../syntax/syntax.zig");
const project = @import("project.zig");
const vfs = @import("../remote/vfs.zig");
const rpath = @import("../remote/path.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const Query = struct {
    pattern: []const u8,
    regex: bool = false,
    case_sensitive: bool = false,
    whole_word: bool = false,
    /// Lines of context around each match.
    context: u32 = 2,
};

/// Text of a file that is open with unsaved changes (searched instead of disk).
pub const Overlay = struct { path: []const u8, text: []const u8 };

/// Where to search, parsed from Sublime's comma separated "Where" field:
/// folders and files, "*.zig" include filters and "-*.md" / "-build/" excludes.
pub const Where = struct {
    roots: std.ArrayList([]const u8) = .empty,
    includes: std.ArrayList([]const u8) = .empty,
    excludes: std.ArrayList([]const u8) = .empty,
    open_files: bool = false,
    open_folders: bool = false,

    /// Parses `text` into `alloc` (which must outlive the result).
    pub fn parse(alloc: Allocator, text: []const u8, home: ?[]const u8) !Where {
        var w: Where = .{};
        var it = std.mem.splitScalar(u8, text, ',');
        while (it.next()) |raw| {
            const part = std.mem.trim(u8, raw, " \t");
            if (part.len == 0) continue;
            if (std.mem.eql(u8, part, "<open files>")) {
                w.open_files = true;
            } else if (std.mem.eql(u8, part, "<open folders>") or std.mem.eql(u8, part, "<project>")) {
                w.open_folders = true;
            } else if (part[0] == '-') {
                try w.excludes.append(alloc, part[1..]);
            } else if (part[0] == '/' or part[0] == '~' or rpath.isRemote(part)) {
                const p = if (part[0] == '~' and home != null) try std.fmt.allocPrint(alloc, "{s}{s}", .{ home.?, part[1..] }) else part;
                try w.roots.append(alloc, std.mem.trimEnd(u8, p, "/"));
            } else {
                try w.includes.append(alloc, part);
            }
        }
        if (w.roots.items.len == 0 and !w.open_files) w.open_folders = true;
        return w;
    }

    /// Whether a file (path relative to its search root) passes the filters.
    pub fn accepts(self: *const Where, rel: []const u8) bool {
        const base = std.fs.path.basename(rel);
        for (self.excludes.items) |ex| {
            const pat = std.mem.trimEnd(u8, ex, "/");
            if (glob(pat, base) or glob(pat, rel)) return false;
            // "-build/" or "-build" also excludes everything under a build dir.
            var dirs = std.mem.splitScalar(u8, rel, '/');
            while (dirs.next()) |d| if (dirs.peek() != null and glob(pat, d)) return false;
        }
        if (self.includes.items.len == 0) return true;
        for (self.includes.items) |inc| if (glob(inc, base) or glob(inc, rel)) return true;
        return false;
    }
};

/// Shell-style wildcard match: `*` and `?`.
pub fn glob(pattern: []const u8, name: []const u8) bool {
    var p: usize = 0;
    var n: usize = 0;
    var star: ?usize = null;
    var mark: usize = 0;
    while (n < name.len) {
        if (p < pattern.len and (pattern[p] == '?' or pattern[p] == name[n])) {
            p += 1;
            n += 1;
        } else if (p < pattern.len and pattern[p] == '*') {
            star = p;
            mark = n;
            p += 1;
        } else if (star) |s| {
            p = s + 1;
            mark += 1;
            n = mark;
        } else return false;
    }
    while (p < pattern.len and pattern[p] == '*') p += 1;
    return p == pattern.len;
}

pub const Results = struct {
    arena: std.heap.ArenaAllocator,
    /// The whole results text.
    text: []const u8,
    /// Match byte ranges in `text`.
    matches: [][2]usize,
    files_searched: usize,
    files_matched: usize,
    truncated: bool,
    regex_error: bool,

    pub fn deinit(self: *Results) void {
        self.arena.deinit();
    }
};

const max_file_size = 32 * 1024 * 1024;
const max_matches = 50_000;

/// One search running on a worker thread. The main thread owns the job:
/// `cancel` it to stop early; `done` runs on the main thread when finished.
pub const Job = struct {
    gpa: Allocator,
    io: Io,
    executor: *Executor,
    arena: std.heap.ArenaAllocator,
    query: Query,
    /// Folders (or files) to walk, and individual files, filtered by `where`.
    roots: []const []const u8,
    files: []const []const u8,
    where: Where,
    overlays: []const Overlay,
    cancelled: std.atomic.Value(bool) = .init(false),
    result: ?Results = null,
    ctx: *anyopaque,
    done: *const fn (ctx: *anyopaque, job: *Job) void,
    /// Written before each file header: jiffy-edit-server searching for a
    /// remote client prints "ssh://host" + its own absolute paths.
    header_prefix: []const u8 = "",

    /// Starts a search; the file list is gathered on the worker (walking big
    /// folders can take a while). Everything is copied.
    pub fn start(
        gpa: Allocator,
        io: Io,
        executor: *Executor,
        query: Query,
        roots: []const []const u8,
        files: []const []const u8,
        where: *const Where,
        overlays: []const Overlay,
        ctx: *anyopaque,
        done: *const fn (ctx: *anyopaque, job: *Job) void,
    ) !*Job {
        const self = try gpa.create(Job);
        errdefer gpa.destroy(self);
        self.* = .{
            .gpa = gpa,
            .io = io,
            .executor = executor,
            .arena = .init(gpa),
            .query = query,
            .roots = &.{},
            .files = &.{},
            .where = .{},
            .overlays = &.{},
            .ctx = ctx,
            .done = done,
        };
        errdefer self.arena.deinit();
        const alloc = self.arena.allocator();
        self.query.pattern = try alloc.dupe(u8, query.pattern);
        self.roots = try dupeAll(alloc, roots);
        self.files = try dupeAll(alloc, files);
        try self.where.includes.appendSlice(alloc, try dupeAll(alloc, where.includes.items));
        try self.where.excludes.appendSlice(alloc, try dupeAll(alloc, where.excludes.items));
        const owned_overlays = try alloc.alloc(Overlay, overlays.len);
        for (overlays, owned_overlays) |ov, *o| o.* = .{ .path = try alloc.dupe(u8, ov.path), .text = try alloc.dupe(u8, ov.text) };
        self.overlays = owned_overlays;
        try executor.spawn(run, .{self});
        return self;
    }

    fn dupeAll(alloc: Allocator, items: []const []const u8) ![]const []const u8 {
        const out = try alloc.alloc([]const u8, items.len);
        for (items, out) |it, *o| o.* = try alloc.dupe(u8, it);
        return out;
    }

    /// The deduplicated file list: explicit files, then everything under the roots.
    fn gatherFiles(self: *Job, alloc: Allocator) ![]const []const u8 {
        var list: std.ArrayList([]const u8) = .empty;
        var seen: std.StringHashMapUnmanaged(void) = .empty;
        for (self.files) |f| {
            if (!self.where.accepts(std.fs.path.basename(f))) continue;
            if ((try seen.getOrPut(alloc, f)).found_existing) continue;
            try list.append(alloc, f);
        }
        var walked: std.ArrayList([]const u8) = .empty;
        for (self.roots) |root| {
            if (self.cancelled.load(.acquire)) break;
            walked.clearRetainingCapacity();
            collectFiles(alloc, self.gpa, self.io, root, &self.where, &walked) catch continue;
            for (walked.items) |f| {
                if ((try seen.getOrPut(alloc, f)).found_existing) continue;
                try list.append(alloc, f);
            }
        }
        return list.items;
    }

    pub fn cancel(self: *Job) void {
        self.cancelled.store(true, .release);
    }

    /// Main thread, after `done` ran (or the job was cancelled and finished).
    pub fn destroy(self: *Job) void {
        if (self.result) |*r| r.deinit();
        self.arena.deinit();
        self.gpa.destroy(self);
    }

    fn run(self: *Job) void {
        // Once posted, the job is the main thread's to free: the executor
        // is the last thing touched.
        const executor = self.executor;
        defer executor.taskDone();
        self.result = self.search() catch null;
        executor.post(.{ .ctx = self, .run = finish });
    }

    fn finish(ctx: *anyopaque) void {
        const self: *Job = @ptrCast(@alignCast(ctx));
        self.done(self.ctx, self);
    }

    fn overlayFor(self: *const Job, path: []const u8) ?[]const u8 {
        for (self.overlays) |o| if (std.mem.eql(u8, o.path, path)) return o.text;
        return null;
    }

    /// Runs the search on the calling thread (`run` does it on the worker).
    /// Remote roots and files are searched on their machine.
    pub fn search(self: *Job) !Results {
        if (try vfs.searchRemote(self)) |remote| return remote;
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        errdefer arena.deinit();
        const alloc = arena.allocator();
        var out: std.ArrayList(u8) = .empty;
        var matches: std.ArrayList([2]usize) = .empty;
        const files = try self.gatherFiles(alloc);
        try out.print(alloc, "Searching {d} file{s} for \"{s}\"\n\n", .{ files.len, if (files.len == 1) "" else "s", self.query.pattern });

        var re: ?regex_mod.Regex = null;
        defer if (re) |*r| r.deinit();
        if (self.query.regex) {
            re = regex_mod.Regex.compile(self.gpa, self.query.pattern, .{ .case_insensitive = !self.query.case_sensitive }) catch {
                try out.appendSlice(alloc, "Invalid regular expression\n");
                return .{ .arena = arena, .text = out.items, .matches = &.{}, .files_searched = 0, .files_matched = 0, .truncated = false, .regex_error = true };
            };
        }

        var line_hits: std.ArrayList([2]usize) = .empty;
        defer line_hits.deinit(self.gpa);
        var searched: usize = 0;
        var matched_files: usize = 0;
        var truncated = false;
        for (files) |path| {
            if (self.cancelled.load(.acquire)) break;
            if (matches.items.len >= max_matches) {
                truncated = true;
                break;
            }
            var owned: ?[]u8 = null;
            defer if (owned) |o| self.gpa.free(o);
            const text = self.overlayFor(path) orelse blk: {
                // Regular files only: opening a FIFO (the walk lists symlinks,
                // which may point at one) waits for a writer, and would hold
                // up the search and so quitting.
                const stat = Io.Dir.cwd().statFile(self.io, path, .{}) catch continue;
                if (stat.kind != .file) continue;
                owned = Io.Dir.cwd().readFileAlloc(self.io, path, self.gpa, .limited(max_file_size)) catch continue;
                break :blk owned.?;
            };
            searched += 1;
            // Skip binary files.
            if (std.mem.indexOfScalar(u8, text[0..@min(text.len, 8000)], 0) != null) continue;
            if (try self.searchFile(alloc, path, text, if (re) |*r| r else null, &out, &matches, &line_hits)) matched_files += 1;
        }

        if (self.cancelled.load(.acquire)) {
            try out.appendSlice(alloc, "Search cancelled\n");
        } else {
            try out.print(alloc, "{d} match{s} across {d} file{s}{s}\n", .{
                matches.items.len,
                if (matches.items.len == 1) "" else "es",
                matched_files,
                if (matched_files == 1) "" else "s",
                if (truncated) " (stopped early: too many matches)" else "",
            });
        }
        return .{
            .arena = arena,
            .text = out.items,
            .matches = matches.items,
            .files_searched = searched,
            .files_matched = matched_files,
            .truncated = truncated,
            .regex_error = false,
        };
    }

    /// Appends one file's block; returns whether it had matches.
    fn searchFile(
        self: *Job,
        alloc: Allocator,
        path: []const u8,
        text: []const u8,
        re: ?*regex_mod.Regex,
        out: *std.ArrayList(u8),
        matches: *std.ArrayList([2]usize),
        hits: *std.ArrayList([2]usize),
    ) !bool {
        const ctx_lines = self.query.context;
        var any = false;
        // Last row written (for context and ".." gaps).
        var last_written: ?usize = null;
        var row: usize = 0;
        var line_start: usize = 0;
        // Context lines before a match need the previous lines' starts.
        var recent: [8]usize = undefined;
        var pending_after: u32 = 0;
        while (line_start <= text.len) : (row += 1) {
            const line_end = std.mem.indexOfScalarPos(u8, text, line_start, '\n') orelse text.len;
            const line = std.mem.trimEnd(u8, text[line_start..line_end], "\r");
            recent[row % recent.len] = line_start;
            hits.clearRetainingCapacity();
            self.matchLine(line, re, hits);
            if (hits.items.len > 0) {
                if (!any) {
                    try out.print(alloc, "{s}{s}:\n", .{ self.header_prefix, path });
                    any = true;
                }
                // Leading context (not already written).
                const first_ctx = row -| @min(ctx_lines, @as(u32, @intCast(@min(row, recent.len - 1))));
                const from = if (last_written) |lw| @max(first_ctx, lw + 1) else first_ctx;
                if (last_written) |lw| if (from > lw + 1) try out.appendSlice(alloc, "  ..\n");
                var r = from;
                while (r < row) : (r += 1) try writeLine(alloc, out, r, lineAt(text, recent[r % recent.len]), ' ');
                const prefix_len = try writeLinePrefix(alloc, out, row, ':');
                const base = out.items.len;
                try out.appendSlice(alloc, line);
                try out.append(alloc, '\n');
                _ = prefix_len;
                for (hits.items) |h| {
                    if (matches.items.len >= max_matches) break;
                    try matches.append(alloc, .{ base + h[0], base + h[1] });
                }
                last_written = row;
                pending_after = ctx_lines;
            } else if (pending_after > 0 and last_written != null) {
                try writeLine(alloc, out, row, line, ' ');
                last_written = row;
                pending_after -= 1;
            }
            if (line_end >= text.len) break;
            line_start = line_end + 1;
        }
        if (any) try out.append(alloc, '\n');
        return any;
    }

    fn matchLine(self: *Job, line: []const u8, re: ?*regex_mod.Regex, hits: *std.ArrayList([2]usize)) void {
        const q = self.query;
        if (q.pattern.len == 0) return;
        if (re) |r| {
            var pos: usize = 0;
            while (pos <= line.len) {
                const m = r.search(line, pos) orelse break;
                if (!q.whole_word or isWholeWord(line, m.start, m.end)) hits.append(self.gpa, .{ m.start, m.end }) catch return;
                pos = if (m.end > m.start) m.end else m.end + 1;
            }
            return;
        }
        var pos: usize = 0;
        while (pos + q.pattern.len <= line.len) {
            const at = (if (q.case_sensitive) std.mem.indexOfPos(u8, line, pos, q.pattern) else indexOfIgnoreCase(line, pos, q.pattern)) orelse break;
            if (!q.whole_word or isWholeWord(line, at, at + q.pattern.len)) hits.append(self.gpa, .{ at, at + q.pattern.len }) catch return;
            pos = at + q.pattern.len;
        }
    }
};

fn lineAt(text: []const u8, start: usize) []const u8 {
    const end = std.mem.indexOfScalarPos(u8, text, start, '\n') orelse text.len;
    return std.mem.trimEnd(u8, text[start..end], "\r");
}

/// "   12: " for matches, "   12  " for context lines.
fn writeLinePrefix(alloc: Allocator, out: *std.ArrayList(u8), row: usize, mark: u8) !usize {
    const before = out.items.len;
    try out.print(alloc, "{d: >6}{c} ", .{ row + 1, mark });
    return out.items.len - before;
}

fn writeLine(alloc: Allocator, out: *std.ArrayList(u8), row: usize, line: []const u8, mark: u8) !void {
    _ = try writeLinePrefix(alloc, out, row, mark);
    try out.appendSlice(alloc, line);
    try out.append(alloc, '\n');
}

fn isWordChar(ch: u8) bool {
    return std.ascii.isAlphanumeric(ch) or ch == '_' or ch >= 0x80;
}

fn isWholeWord(hay: []const u8, start: usize, end: usize) bool {
    const before_ok = start == 0 or !isWordChar(hay[start - 1]);
    const after_ok = end >= hay.len or !isWordChar(hay[end]);
    return before_ok and after_ok;
}

fn indexOfIgnoreCase(hay: []const u8, start: usize, needle: []const u8) ?usize {
    if (needle.len == 0 or needle.len > hay.len) return null;
    var i = start;
    while (i + needle.len <= hay.len) : (i += 1) {
        if (std.ascii.eqlIgnoreCase(hay[i .. i + needle.len], needle)) return i;
    }
    return null;
}

/// Collects files under `root` (honouring .gitignore and default excludes),
/// filtered by `where`. Paths are absolute and allocated in `alloc`.
pub fn collectFiles(alloc: Allocator, gpa: Allocator, io: Io, root: []const u8, where: *const Where, out: *std.ArrayList([]const u8)) !void {
    const stat = Io.Dir.cwd().statFile(io, root, .{}) catch return;
    if (stat.kind != .directory) {
        if (where.accepts(std.fs.path.basename(root))) try out.append(alloc, try alloc.dupe(u8, root));
        return;
    }
    var snap = try project.buildSnapshot(gpa, io, root);
    defer snap.deinit();
    for (snap.files) |idx| {
        const e = snap.entries[idx];
        if (!where.accepts(e.path)) continue;
        try out.append(alloc, try std.fs.path.join(alloc, &.{ root, e.path }));
    }
}

// ---- Parsing results text (for jumping to a result) -----------------------------

pub const ResultLine = struct { line_number: u32, is_match: bool, text_start: usize };

/// Parses "   12: text" / "   12  text"; null for headers and blank lines.
pub fn parseResultLine(line: []const u8) ?ResultLine {
    var i: usize = 0;
    while (i < line.len and line[i] == ' ') i += 1;
    if (i == 0) return null;
    const num_start = i;
    while (i < line.len and std.ascii.isDigit(line[i])) i += 1;
    if (i == num_start or i >= line.len) return null;
    const n = std.fmt.parseInt(u32, line[num_start..i], 10) catch return null;
    const mark = line[i];
    if (mark != ':' and mark != ' ') return null;
    return .{ .line_number = n, .is_match = mark == ':', .text_start = @min(line.len, i + 2) };
}

/// Colors for the Find Results buffer: file headers, line numbers (match
/// lines brighter than context), and the summary lines.
pub fn highlight(gpa: Allocator, rope: *const rope_mod.Rope, start: usize, end: usize, s: *const theme_mod.Syntax, out: *std.ArrayList(syntax_mod.HighlightSpan)) void {
    out.clearRetainingCapacity();
    var line_buf: std.ArrayList(u8) = .empty;
    defer line_buf.deinit(gpa);
    const first = rope.offsetToPoint(start).row;
    const last = rope.offsetToPoint(end).row;
    var row = first;
    while (row <= last) : (row += 1) {
        line_buf.clearRetainingCapacity();
        const base = rope.lineStart(row);
        var it = rope.chunks(base, rope.lineEnd(row));
        while (it.next()) |chunk| line_buf.appendSlice(gpa, chunk) catch return;
        const line = line_buf.items;
        if (line.len == 0) continue;
        if (line[0] != ' ') {
            const color = if (line[line.len - 1] == ':') s.function else s.comment;
            out.append(gpa, .{ .start = base, .end = base + line.len, .color = color }) catch return;
        } else if (parseResultLine(line)) |r| {
            out.append(gpa, .{ .start = base, .end = base + r.text_start, .color = if (r.is_match) s.number else s.comment }) catch return;
        } else {
            out.append(gpa, .{ .start = base, .end = base + line.len, .color = s.comment }) catch return;
        }
    }
}

test "glob and where filters" {
    try std.testing.expect(glob("*.zig", "main.zig"));
    try std.testing.expect(!glob("*.zig", "main.c"));
    try std.testing.expect(glob("a?c*", "abcdef"));
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const w = try Where.parse(arena.allocator(), "*.zig, -build/, -*_test.zig", null);
    try std.testing.expect(w.open_folders);
    try std.testing.expect(w.accepts("src/main.zig"));
    try std.testing.expect(!w.accepts("build/out.zig"));
    try std.testing.expect(!w.accepts("src/x_test.zig"));
    try std.testing.expect(!w.accepts("README.md"));
    // Folders, on this machine or another.
    const dirs = try Where.parse(arena.allocator(), "~/org, ssh://devbox/srv/app/", "/home/me");
    try std.testing.expectEqualStrings("/home/me/org", dirs.roots.items[0]);
    try std.testing.expectEqualStrings("ssh://devbox/srv/app", dirs.roots.items[1]);
    try std.testing.expect(!dirs.open_folders);
}

test "result line parsing" {
    const m = parseResultLine("    12: const x = 1;").?;
    try std.testing.expectEqual(@as(u32, 12), m.line_number);
    try std.testing.expect(m.is_match);
    try std.testing.expectEqual(@as(usize, 8), m.text_start);
    try std.testing.expect(parseResultLine("/abs/path.zig:") == null);
    try std.testing.expect(!parseResultLine("     3  context").?.is_match);
}

extern "c" fn mkfifoat(dirfd: std.c.fd_t, path: [*:0]const u8, mode: std.c.mode_t) c_int;

test "a search skips a FIFO instead of waiting for a writer" {
    // The walk lists symlinks as files; one to a FIFO would block the search
    // in open(), and quitting waits for every search.
    const gpa = std.testing.allocator;
    const io = std.testing.io;
    const linux = std.os.linux;
    // Raw syscalls on Linux (the server's tests have no libc), libc on macOS.
    const on_linux = builtin.os.tag == .linux;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(io, .{ .sub_path = "a.txt", .data = "needle\n" });
    if (on_linux) {
        try std.testing.expectEqual(@as(usize, 0), linux.mknodat(tmp.dir.handle, "fifo", linux.S.IFIFO | 0o600, 0));
        try std.testing.expectEqual(@as(usize, 0), linux.symlinkat("fifo", tmp.dir.handle, "pipe.txt"));
    } else {
        try std.testing.expectEqual(@as(c_int, 0), mkfifoat(tmp.dir.handle, "fifo", 0o600));
        try std.testing.expectEqual(@as(c_int, 0), std.c.symlinkat("fifo", tmp.dir.handle, "pipe.txt"));
    }
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(io, &buf)];
    const a_path = try std.fs.path.join(gpa, &.{ root, "a.txt" });
    defer gpa.free(a_path);
    const pipe_path = try std.fs.path.join(gpa, &.{ root, "pipe.txt" });
    defer gpa.free(pipe_path);
    var job: Job = .{
        .gpa = gpa,
        .io = io,
        .executor = undefined,
        .arena = .init(gpa),
        .query = .{ .pattern = "needle" },
        .roots = &.{},
        .files = &.{ pipe_path, a_path },
        .where = .{},
        .overlays = &.{},
        .ctx = undefined,
        .done = undefined,
    };
    defer job.arena.deinit();
    const Search = struct {
        fn run(j: *Job, out: *?Results, finished: *std.atomic.Value(bool)) void {
            out.* = j.search() catch null;
            finished.store(true, .release);
        }
    };
    var results: ?Results = null;
    defer if (results) |*r| r.deinit();
    var finished: std.atomic.Value(bool) = .init(false);
    const thread = try std.Thread.spawn(.{}, Search.run, .{ &job, &results, &finished });
    var waited: u32 = 0;
    while (!finished.load(.acquire) and waited < 500) : (waited += 1) try std.Io.sleep(io, .fromMilliseconds(10), .awake);
    const blocked = !finished.load(.acquire);
    // A stuck search goes on once a writer opens (and closes) the FIFO.
    if (blocked) {
        if (on_linux) {
            const rc = linux.openat(tmp.dir.handle, "fifo", .{ .ACCMODE = .WRONLY, .NONBLOCK = true }, 0);
            if (linux.errno(rc) == .SUCCESS) _ = linux.close(@intCast(rc));
        } else {
            const fd = std.c.openat(tmp.dir.handle, "fifo", .{ .ACCMODE = .WRONLY, .NONBLOCK = true });
            if (fd >= 0) _ = std.c.close(fd);
        }
    }
    thread.join();
    try std.testing.expect(!blocked);
    try std.testing.expectEqual(@as(usize, 1), results.?.files_matched);
}
