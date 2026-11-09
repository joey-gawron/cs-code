//! The commands the Source Control window runs, against real repositories
//! in temporary folders (the system git; skipped when there is none):
//! staging, commits, branches, stashes, remotes, hooks, hunk patches,
//! discarding, conflicts and the log. The argument lists are the window's.

const std = @import("std");
const git = @import("git.zig");
const status_mod = @import("status.zig");
const history = @import("history.zig");
const linediff = @import("../text/linediff.zig");

const testing = std.testing;
const gpa = testing.allocator;

const Repo = struct {
    tmp: std.testing.TmpDir,
    dir: []u8,

    fn init(name: []const u8) !Repo {
        var tmp = testing.tmpDir(.{});
        errdefer tmp.cleanup();
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const n = try tmp.dir.realPath(testing.io, &buf);
        const base = buf[0..n];
        const dir = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ base, name });
        errdefer gpa.free(dir);
        try tmp.dir.createDir(testing.io, name, .default_dir);
        var r: Repo = .{ .tmp = tmp, .dir = dir };
        const out = try r.runIn(dir, &.{ "init", "-q", "-b", "main" });
        gpa.free(out);
        return r;
    }

    fn deinit(self: *Repo) void {
        gpa.free(self.dir);
        self.tmp.cleanup();
    }

    /// Runs git in `cwd`, failing if it fails.
    fn runIn(self: *Repo, cwd: []const u8, args: []const []const u8) ![]u8 {
        const o = try self.runOut(cwd, args, null);
        defer gpa.free(o.stderr);
        if (!o.ok) {
            gpa.free(o.stdout);
            return error.GitFailed;
        }
        return o.stdout;
    }

    fn runOut(self: *Repo, cwd: []const u8, args: []const []const u8, input: ?[]const u8) !git.Output {
        _ = self;
        var argv: std.ArrayList([]const u8) = .empty;
        defer argv.deinit(gpa);
        const hooks = try std.fmt.allocPrint(gpa, "core.hooksPath={s}/.git/hooks", .{cwd});
        defer gpa.free(hooks);
        try argv.appendSlice(gpa, &.{ "git", "-c", "commit.gpgsign=false", "-c", "user.name=Ada", "-c", "user.email=ada@example.com", "-c", hooks, "-c", "protocol.file.allow=always" });
        try argv.appendSlice(gpa, args);
        return git.runFull(gpa, testing.io, argv.items, cwd, input, true);
    }

    fn git_(self: *Repo, args: []const []const u8) ![]u8 {
        return self.runIn(self.dir, args);
    }

    fn gitOut(self: *Repo, args: []const []const u8, input: ?[]const u8) !git.Output {
        return self.runOut(self.dir, args, input);
    }

    fn gitFree(self: *Repo, args: []const []const u8) !void {
        gpa.free(try self.git_(args));
    }

    fn write(self: *Repo, path: []const u8, data: []const u8) !void {
        const full = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ self.dir, path });
        defer gpa.free(full);
        if (std.fs.path.dirname(path)) |d| {
            const dd = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ self.dir, d });
            defer gpa.free(dd);
            std.Io.Dir.cwd().createDirPath(testing.io, dd) catch {};
        }
        try std.Io.Dir.cwd().writeFile(testing.io, .{ .sub_path = full, .data = data });
    }

    fn read(self: *Repo, path: []const u8) ![]u8 {
        const full = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ self.dir, path });
        defer gpa.free(full);
        return std.Io.Dir.cwd().readFileAlloc(testing.io, full, gpa, .limited(1 << 20));
    }

    fn commit(self: *Repo, message: []const u8) !void {
        const o = try self.gitOut(&.{ "commit", "-F", "-", "--cleanup=strip" }, message);
        defer gpa.free(o.stdout);
        defer gpa.free(o.stderr);
        if (!o.ok) return error.GitFailed;
    }

    /// The parsed `git status` the window reads.
    fn status(self: *Repo) !status_mod.Status {
        var argv: std.ArrayList([]const u8) = .empty;
        defer argv.deinit(gpa);
        try argv.append(gpa, "git");
        try argv.appendSlice(gpa, &status_mod.status_argv);
        const out = (try git.runCommand(gpa, testing.io, argv.items, self.dir, null)) orelse return error.GitFailed;
        defer gpa.free(out);
        return status_mod.parse(gpa, out);
    }

    fn subjectOfHead(self: *Repo) ![]u8 {
        const out = try self.git_(&.{ "log", "-1", "--format=%s" });
        defer gpa.free(out);
        return gpa.dupe(u8, std.mem.trim(u8, out, "\n"));
    }
};

fn skipWithoutGit() !void {
    if (!git.available(gpa, testing.io)) return error.SkipZigTest;
}

fn entryFor(st: *const status_mod.Status, path: []const u8) ?*const status_mod.Entry {
    for (st.entries) |*e| if (std.mem.eql(u8, e.path, path)) return e;
    return null;
}

test "stage and unstage a file, as the window's + and - buttons do" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "--", "a.txt" });
    try r.commit("First");
    try r.write("a.txt", "one\ntwo\n");
    {
        var st = try r.status();
        defer st.deinit();
        const e = entryFor(&st, "a.txt").?;
        try testing.expect(status_mod.inSection(e, .unstaged) and !status_mod.inSection(e, .staged));
    }
    try r.gitFree(&.{ "add", "--", "a.txt" });
    {
        var st = try r.status();
        defer st.deinit();
        const e = entryFor(&st, "a.txt").?;
        try testing.expect(status_mod.inSection(e, .staged) and !status_mod.inSection(e, .unstaged));
    }
    try r.gitFree(&.{ "reset", "-q", "HEAD", "--", "a.txt" });
    {
        var st = try r.status();
        defer st.deinit();
        const e = entryFor(&st, "a.txt").?;
        try testing.expect(status_mod.inSection(e, .unstaged) and !status_mod.inSection(e, .staged));
    }
}

test "before the first commit a file is unstaged with rm --cached" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "--", "a.txt" });
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expect(st.branch.initial());
        try testing.expect(status_mod.inSection(entryFor(&st, "a.txt").?, .staged));
    }
    try r.gitFree(&.{ "rm", "--cached", "-r", "-q", "--", "a.txt" });
    var st = try r.status();
    defer st.deinit();
    try testing.expect(entryFor(&st, "a.txt").?.kind == .untracked);
}

test "commit takes its message on stdin; amend with no message keeps it; sign-off adds the trailer" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("  Add a file\n\nWith a body line.\n");
    const subject = try r.subjectOfHead();
    defer gpa.free(subject);
    try testing.expectEqualStrings("Add a file", subject);
    // Amend without a message: --no-edit.
    try r.write("a.txt", "one\ntwo\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.gitFree(&.{ "commit", "--amend", "--no-edit" });
    const same = try r.subjectOfHead();
    defer gpa.free(same);
    try testing.expectEqualStrings("Add a file", same);
    const count = try r.git_(&.{ "rev-list", "--count", "HEAD" });
    defer gpa.free(count);
    try testing.expectEqualStrings("1\n", count);
    // Sign-off.
    try r.write("b.txt", "b\n");
    try r.gitFree(&.{ "add", "-A" });
    const o = try r.gitOut(&.{ "commit", "-s", "-F", "-", "--cleanup=strip" }, "Add b");
    defer gpa.free(o.stdout);
    defer gpa.free(o.stderr);
    try testing.expect(o.ok);
    const body = try r.git_(&.{ "log", "-1", "--format=%B" });
    defer gpa.free(body);
    try testing.expect(std.mem.indexOf(u8, body, "Signed-off-by: Ada <ada@example.com>") != null);
}

test "Commit All with nothing staged: -a takes the tracked changes, not new files" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    try r.write("a.txt", "changed\n");
    try r.write("new.txt", "untracked\n");
    const o = try r.gitOut(&.{ "commit", "-a", "-F", "-", "--cleanup=strip" }, "Second");
    defer gpa.free(o.stdout);
    defer gpa.free(o.stderr);
    try testing.expect(o.ok);
    var st = try r.status();
    defer st.deinit();
    try testing.expect(entryFor(&st, "a.txt") == null);
    try testing.expect(entryFor(&st, "new.txt").?.kind == .untracked);
}

test "branches: create, switch, and the current one in the status" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    try r.gitFree(&.{ "checkout", "-b", "feature/x" });
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expectEqualStrings("feature/x", st.branch.head.?);
    }
    try r.write("a.txt", "feature\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("On the feature");
    try r.gitFree(&.{ "checkout", "main" });
    const text = try r.read("a.txt");
    defer gpa.free(text);
    try testing.expectEqualStrings("one\n", text);
    // The picker's listing: local and remote refs by full name.
    const refs = try r.git_(&.{ "for-each-ref", "--sort=-committerdate", "--format=%(refname)", "refs/heads", "refs/remotes" });
    defer gpa.free(refs);
    try testing.expect(std.mem.indexOf(u8, refs, "refs/heads/feature/x") != null);
    try testing.expect(std.mem.indexOf(u8, refs, "refs/heads/main") != null);
    // A checkout that would lose changes is refused, and the window says why.
    try r.write("a.txt", "dirty\n");
    const o = try r.gitOut(&.{ "checkout", "feature/x" }, null);
    defer gpa.free(o.stdout);
    defer gpa.free(o.stderr);
    try testing.expect(!o.ok);
    try testing.expect(std.mem.indexOf(u8, o.stderr, "would be overwritten") != null);
}

test "stash keeps untracked files too, and pop brings everything back" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    try r.write("a.txt", "edited\n");
    try r.write("fresh.txt", "new\n");
    try r.gitFree(&.{ "stash", "push", "-u", "-m", "work in progress" });
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expectEqual(@as(usize, 0), st.entries.len);
    }
    const list = try r.git_(&.{ "stash", "list" });
    defer gpa.free(list);
    try testing.expect(std.mem.indexOf(u8, list, "work in progress") != null);
    try r.gitFree(&.{ "stash", "pop" });
    var st = try r.status();
    defer st.deinit();
    try testing.expect(status_mod.inSection(entryFor(&st, "a.txt").?, .unstaged));
    try testing.expect(entryFor(&st, "fresh.txt").?.kind == .untracked);
}

test "discard: tracked files go back to the index, untracked ones are deleted" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    try r.write("a.txt", "scribbles\n");
    try r.write("junk.txt", "junk\n");
    try r.gitFree(&.{ "checkout", "--", "a.txt" });
    try r.gitFree(&.{ "clean", "-f", "-q", "--", "junk.txt" });
    const text = try r.read("a.txt");
    defer gpa.free(text);
    try testing.expectEqualStrings("one\n", text);
    var st = try r.status();
    defer st.deinit();
    try testing.expectEqual(@as(usize, 0), st.entries.len);
}

test "ahead and behind a remote, fetch, pull and push" {
    try skipWithoutGit();
    var r = try Repo.init("local");
    defer r.deinit();
    // A bare remote beside the working copy.
    const remote = try std.fmt.allocPrint(gpa, "{s}/../remote.git", .{r.dir});
    defer gpa.free(remote);
    gpa.free(try r.runIn(r.dir, &.{ "init", "-q", "--bare", "-b", "main", "../remote.git" }));
    try r.gitFree(&.{ "remote", "add", "origin", "../remote.git" });
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    // No upstream yet: the window publishes with -u origin <branch>.
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expect(st.branch.upstream == null);
    }
    try r.gitFree(&.{ "push", "-u", "origin", "main" });
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expectEqualStrings("origin/main", st.branch.upstream.?);
        try testing.expect(st.branch.has_ab and st.branch.ahead == 0 and st.branch.behind == 0);
    }
    // A local commit: ahead 1.
    try r.write("a.txt", "one\ntwo\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("Local");
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expectEqual(@as(u32, 1), st.branch.ahead);
        try testing.expectEqual(@as(u32, 0), st.branch.behind);
    }
    try r.gitFree(&.{"push"});
    // Someone else pushes: after a fetch, behind 1.
    var other = try Repo.init("other");
    defer other.deinit();
    const clone_target = try std.fmt.allocPrint(gpa, "{s}/clone", .{other.dir});
    defer gpa.free(clone_target);
    gpa.free(try other.runIn(other.dir, &.{ "clone", "-q", remote, clone_target }));
    {
        const f = try std.fmt.allocPrint(gpa, "{s}/b.txt", .{clone_target});
        defer gpa.free(f);
        try std.Io.Dir.cwd().writeFile(testing.io, .{ .sub_path = f, .data = "from elsewhere\n" });
        gpa.free(try other.runIn(clone_target, &.{ "add", "-A" }));
        const o = try other.runOut(clone_target, &.{ "commit", "-q", "-m", "Remote work" }, null);
        gpa.free(o.stdout);
        gpa.free(o.stderr);
        gpa.free(try other.runIn(clone_target, &.{"push"}));
    }
    try r.gitFree(&.{ "fetch", "--prune" });
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expectEqual(@as(u32, 0), st.branch.ahead);
        try testing.expectEqual(@as(u32, 1), st.branch.behind);
    }
    try r.gitFree(&.{"pull"});
    var st = try r.status();
    defer st.deinit();
    try testing.expectEqual(@as(u32, 0), st.branch.behind);
    const b = try r.read("b.txt");
    defer gpa.free(b);
    try testing.expectEqualStrings("from elsewhere\n", b);
}

test "a pre-commit hook that fails stops the commit, and git's message reaches the window" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "one\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    try r.write(".git/hooks/pre-commit", "#!/bin/sh\necho 'lint failed: no TODOs allowed' >&2\nexit 1\n");
    const hook = try std.fmt.allocPrint(gpa, "{s}/.git/hooks/pre-commit", .{r.dir});
    defer gpa.free(hook);
    gpa.free(try r.runIn(r.dir, &.{ "--version" }));
    {
        const o = try git.runFull(gpa, testing.io, &.{ "chmod", "+x", hook }, r.dir, null, false);
        gpa.free(o.stdout);
        gpa.free(o.stderr);
    }
    try r.write("a.txt", "one\nTODO\n");
    try r.gitFree(&.{ "add", "-A" });
    const o = try r.gitOut(&.{ "commit", "-F", "-", "--cleanup=strip" }, "Second");
    defer gpa.free(o.stdout);
    defer gpa.free(o.stderr);
    try testing.expect(!o.ok);
    try testing.expect(std.mem.indexOf(u8, o.stderr, "lint failed") != null);
    const subject = try r.subjectOfHead();
    defer gpa.free(subject);
    try testing.expectEqualStrings("First", subject);
    // --no-verify is the user's to ask for; without it nothing was committed.
    var st = try r.status();
    defer st.deinit();
    try testing.expect(status_mod.inSection(entryFor(&st, "a.txt").?, .staged));
}

test "hunk patches: stage one change, unstage it, revert the other in the file" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    const original = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\n";
    try r.write("f.txt", original);
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    const edited = "l1\nCHANGED2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nappended\n";
    try r.write("f.txt", edited);

    const a = try linediff.splitLines(gpa, original);
    defer gpa.free(a);
    const b = try linediff.splitLines(gpa, edited);
    defer gpa.free(b);
    const hunks = try linediff.diff(gpa, a, b);
    defer gpa.free(hunks);
    try testing.expectEqual(@as(usize, 2), hunks.len);

    // Stage the second hunk (the appended line) only.
    const patch = try linediff.hunkPatch(gpa, "f.txt", original, edited, hunks[1]);
    defer gpa.free(patch);
    {
        const o = try r.gitOut(&.{ "apply", "--cached", "--unidiff-zero", "-" }, patch);
        defer gpa.free(o.stdout);
        defer gpa.free(o.stderr);
        try testing.expect(o.ok);
    }
    {
        const staged = try r.git_(&.{ "diff", "--cached" });
        defer gpa.free(staged);
        try testing.expect(std.mem.indexOf(u8, staged, "+appended") != null);
        try testing.expect(std.mem.indexOf(u8, staged, "CHANGED2") == null);
        const unstaged = try r.git_(&.{"diff"});
        defer gpa.free(unstaged);
        try testing.expect(std.mem.indexOf(u8, unstaged, "+CHANGED2") != null);
        try testing.expect(std.mem.indexOf(u8, unstaged, "appended") == null);
    }
    // Unstage it again: the reverse patch against the index.
    {
        const o = try r.gitOut(&.{ "apply", "--cached", "--unidiff-zero", "-R", "-" }, patch);
        defer gpa.free(o.stdout);
        defer gpa.free(o.stderr);
        try testing.expect(o.ok);
        const staged = try r.git_(&.{ "diff", "--cached" });
        defer gpa.free(staged);
        try testing.expectEqual(@as(usize, 0), staged.len);
    }
    // Revert the first hunk in the file: the reverse patch against the working tree.
    const first = try linediff.hunkPatch(gpa, "f.txt", original, edited, hunks[0]);
    defer gpa.free(first);
    {
        const o = try r.gitOut(&.{ "apply", "--unidiff-zero", "-R", "-" }, first);
        defer gpa.free(o.stdout);
        defer gpa.free(o.stderr);
        try testing.expect(o.ok);
    }
    const text = try r.read("f.txt");
    defer gpa.free(text);
    try testing.expectEqualStrings("l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nappended\n", text);
}

test "a hunk patch survives CRLF files and a missing final newline" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    const original = "a\r\nb\r\nc";
    const edited = "a\r\nB\r\nc";
    try r.write("f.txt", original);
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("First");
    try r.write("f.txt", edited);
    const a = try linediff.splitLines(gpa, original);
    defer gpa.free(a);
    const b = try linediff.splitLines(gpa, edited);
    defer gpa.free(b);
    const hunks = try linediff.diff(gpa, a, b);
    defer gpa.free(hunks);
    const patch = try linediff.hunkPatch(gpa, "f.txt", original, edited, hunks[0]);
    defer gpa.free(patch);
    const o = try r.gitOut(&.{ "apply", "--cached", "--unidiff-zero", "-" }, patch);
    defer gpa.free(o.stdout);
    defer gpa.free(o.stderr);
    try testing.expect(o.ok);
    const staged = try r.git_(&.{ "diff", "--cached", "--stat" });
    defer gpa.free(staged);
    try testing.expect(std.mem.indexOf(u8, staged, "f.txt") != null);
}

test "a merge conflict shows as unmerged, and staging the resolution clears it" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "base\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("Base");
    try r.gitFree(&.{ "checkout", "-q", "-b", "other" });
    try r.write("a.txt", "from other\n");
    try r.gitFree(&.{ "commit", "-q", "-a", "-m", "Other" });
    try r.gitFree(&.{ "checkout", "-q", "main" });
    try r.write("a.txt", "from main\n");
    try r.gitFree(&.{ "commit", "-q", "-a", "-m", "Main" });
    const merge = try r.gitOut(&.{ "merge", "other" }, null);
    defer gpa.free(merge.stdout);
    defer gpa.free(merge.stderr);
    try testing.expect(!merge.ok);
    {
        var st = try r.status();
        defer st.deinit();
        try testing.expect(entryFor(&st, "a.txt").?.kind == .unmerged);
    }
    const text = try r.read("a.txt");
    defer gpa.free(text);
    try testing.expect(std.mem.indexOf(u8, text, "<<<<<<<") != null);
    try r.write("a.txt", "resolved\n");
    try r.gitFree(&.{ "add", "--", "a.txt" });
    var st = try r.status();
    defer st.deinit();
    try testing.expect(entryFor(&st, "a.txt").?.kind != .unmerged);
}

test "the log graph and a file's history come from real commits" {
    try skipWithoutGit();
    var r = try Repo.init("r");
    defer r.deinit();
    try r.write("a.txt", "1\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("One");
    try r.gitFree(&.{ "checkout", "-q", "-b", "side" });
    try r.write("b.txt", "side\n");
    try r.gitFree(&.{ "add", "-A" });
    try r.commit("On the side");
    try r.gitFree(&.{ "checkout", "-q", "main" });
    try r.write("a.txt", "1\n2\n");
    try r.gitFree(&.{ "commit", "-q", "-a", "-m", "Two" });
    try r.gitFree(&.{ "merge", "-q", "--no-ff", "side", "-m", "Merge side" });
    const out = try r.git_(&.{ "log", "--all", "--date-order", "-z", history.log_format });
    defer gpa.free(out);
    var log = try history.parse(gpa, out);
    defer log.deinit();
    try testing.expectEqual(@as(usize, 4), log.commits.len);
    try testing.expectEqualStrings("Merge side", log.commits[0].subject);
    try testing.expectEqual(@as(usize, 2), log.commits[0].parents.len);
    var g = try history.layout(gpa, log.commits);
    defer g.deinit();
    try testing.expectEqual(@as(u16, 2), g.width);
    // The branch point is waited for by both lanes and ends the second.
    try testing.expectEqual(@as(u16, 0), g.rows[3].lane);
    // The files of the merge's first parent change: `show` against the commit.
    const files_out = try r.git_(&.{ "show", "--name-status", "--format=", "-z", "-M", "--root", log.commits[3].hash });
    defer gpa.free(files_out);
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const files = try history.parseNameStatus(arena.allocator(), files_out);
    try testing.expectEqual(@as(usize, 1), files.len);
    try testing.expectEqual(@as(u8, 'A'), files[0].status);
    try testing.expectEqualStrings("a.txt", files[0].path);
    // a.txt's own history: two commits, newest first.
    const fh = try r.git_(&.{ "log", "--follow", "-z", history.log_format, "--", "a.txt" });
    defer gpa.free(fh);
    var flog = try history.parse(gpa, fh);
    defer flog.deinit();
    try testing.expectEqual(@as(usize, 2), flog.commits.len);
    try testing.expectEqualStrings("Two", flog.commits[0].subject);
}
