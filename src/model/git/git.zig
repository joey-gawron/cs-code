//! Runs git on background threads: finds a file's repository root and
//! runs commands whose output (stdout) comes back on the main thread.
//! A missing git binary or a failing command just yields no output, so
//! files outside a repository quietly get no git features; commands whose
//! errors are shown (Source Control's) keep stderr and a failed run's output.

const std = @import("std");
const Executor = @import("../../framework/util/executor.zig").Executor;
const askpass = @import("../remote/askpass.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const log = std.log.scoped(.git);

/// Largest output read from git (and largest buffer sent to `git blame`).
pub const max_output = 64 * 1024 * 1024;

/// Shared by all git jobs of a workspace: counts running jobs (so shutdown
/// can wait for them) and caches repository roots per folder.
pub const Runner = struct {
    gpa: Allocator,
    io: Io,
    executor: *Executor,
    /// Jobs started and not yet finished on the main thread.
    alive: u32 = 0,
    /// Set once spawning git failed with "not found": nothing is tried again.
    missing: bool = false,
    /// Folder → repository root ("" when the folder isn't in a repository).
    roots: std.StringHashMapUnmanaged([]const u8) = .empty,

    pub fn init(gpa: Allocator, io: Io, executor: *Executor) Runner {
        return .{ .gpa = gpa, .io = io, .executor = executor };
    }

    /// Waits for running jobs (their results are dropped by their owners).
    pub fn deinit(self: *Runner) void {
        while (self.alive > 0) {
            self.executor.drain();
            if (self.alive > 0) std.Thread.yield() catch {};
        }
        var it = self.roots.iterator();
        while (it.next()) |e| {
            self.gpa.free(e.key_ptr.*);
            if (e.value_ptr.len > 0) self.gpa.free(e.value_ptr.*);
        }
        self.roots.deinit(self.gpa);
    }

    /// Known root of `dir`: null if unknown yet, "" if not in a repository.
    pub fn cachedRoot(self: *const Runner, dir: []const u8) ?[]const u8 {
        return self.roots.get(dir);
    }

    pub fn rememberRoot(self: *Runner, dir: []const u8, root: []const u8) void {
        if (self.roots.contains(dir)) return;
        const key = self.gpa.dupe(u8, dir) catch return;
        const value = if (root.len == 0) "" else self.gpa.dupe(u8, root) catch {
            self.gpa.free(key);
            return;
        };
        self.roots.put(self.gpa, key, value) catch {
            self.gpa.free(key);
            if (value.len > 0) self.gpa.free(value);
        };
    }
};

/// One git command on a worker thread. `done` runs on the main thread;
/// `detach` (main thread) instead lets the job free itself when it ends.
pub const Job = struct {
    runner: *Runner,
    arena: std.heap.ArenaAllocator,
    argv: []const []const u8,
    cwd: []const u8,
    input: ?[]const u8,
    /// stdout of a successful run (owned by the job); null if git failed.
    output: ?[]u8 = null,
    /// Runs on the worker after a successful run (e.g. to parse the output).
    process: ?*const fn (job: *Job) void = null,
    /// Whatever `process` produced; the owner takes it (setting it to
    /// null), else `free_payload` frees it with the job.
    payload: ?*anyopaque = null,
    free_payload: ?*const fn (job: *Job) void = null,
    ctx: ?*anyopaque,
    done: *const fn (ctx: *anyopaque, job: *Job) void,
    not_found: bool = false,
    capture: bool = false,
    batch: bool = false,
    /// With `capture`: stderr, and whether git failed (`output` is then
    /// still its stdout).
    stderr: ?[]u8 = null,
    failed: bool = false,

    /// Starts `git argv...` in `cwd`; `input` (copied) is sent on stdin.
    pub fn start(
        runner: *Runner,
        argv: []const []const u8,
        cwd: []const u8,
        input: ?[]const u8,
        process: ?*const fn (job: *Job) void,
        ctx: *anyopaque,
        done: *const fn (ctx: *anyopaque, job: *Job) void,
    ) !*Job {
        return startWith(runner, argv, cwd, .{ .input = input }, process, ctx, done);
    }

    /// `start` with `Options`.
    pub fn startWith(
        runner: *Runner,
        argv: []const []const u8,
        cwd: []const u8,
        options: Options,
        process: ?*const fn (job: *Job) void,
        ctx: *anyopaque,
        done: *const fn (ctx: *anyopaque, job: *Job) void,
    ) !*Job {
        if (runner.missing) return error.GitMissing;
        const gpa = runner.gpa;
        const self = try gpa.create(Job);
        errdefer gpa.destroy(self);
        self.* = .{
            .runner = runner,
            .arena = .init(gpa),
            .argv = &.{},
            .cwd = &.{},
            .input = null,
            .process = process,
            .ctx = ctx,
            .done = done,
            .capture = options.capture,
            .batch = options.batch,
        };
        errdefer self.arena.deinit();
        const alloc = self.arena.allocator();
        const owned = try alloc.alloc([]const u8, argv.len + 1);
        owned[0] = "git";
        for (argv, owned[1..]) |a, *o| o.* = try alloc.dupe(u8, a);
        self.argv = owned;
        self.cwd = try alloc.dupe(u8, cwd);
        if (options.input) |in| self.input = try alloc.dupe(u8, in);
        runner.alive += 1;
        errdefer runner.alive -= 1;
        try runner.executor.spawn(run, .{self});
        return self;
    }

    /// The owner no longer wants the result (main thread).
    pub fn detach(self: *Job) void {
        self.ctx = null;
    }

    /// Main thread, from `done`.
    pub fn destroy(self: *Job) void {
        const gpa = self.runner.gpa;
        if (self.payload != null) if (self.free_payload) |f| f(self);
        if (self.output) |o| gpa.free(o);
        if (self.stderr) |e| gpa.free(e);
        self.arena.deinit();
        gpa.destroy(self);
    }

    fn run(self: *Job) void {
        // Once posted, the job is the main thread's to free: the executor
        // is the last thing touched.
        const executor = self.runner.executor;
        defer executor.taskDone();
        if (self.capture) {
            if (runFull(self.runner.gpa, self.runner.io, self.argv, self.cwd, self.input, self.batch)) |out| {
                self.output = out.stdout;
                self.stderr = out.stderr;
                self.failed = !out.ok;
            } else |err| self.failedToRun(err);
        } else {
            self.output = runCommand(self.runner.gpa, self.runner.io, self.argv, self.cwd, self.input) catch |err| blk: {
                self.failedToRun(err);
                break :blk null;
            };
        }
        if (self.output != null and !self.failed) if (self.process) |p| p(self);
        executor.post(.{ .ctx = self, .run = finish });
    }

    fn failedToRun(self: *Job, err: anyerror) void {
        // A missing cwd fails the same way as a missing git.
        if (err == error.FileNotFound) {
            if (Io.Dir.cwd().statFile(self.runner.io, self.cwd, .{})) |_| {
                self.not_found = true;
            } else |_| {}
        }
        log.debug("{s} {s}: {t}", .{ self.argv[0], self.argv[1], err });
    }

    fn finish(ctx: *anyopaque) void {
        const self: *Job = @ptrCast(@alignCast(ctx));
        self.runner.alive -= 1;
        // Only "git" itself can be missing; a missing cwd fails differently.
        if (self.not_found) self.runner.missing = true;
        if (self.ctx) |c| self.done(c, self) else self.destroy();
    }
};

pub const Options = struct {
    /// Sent on stdin.
    input: ?[]const u8 = null,
    /// Keep stderr and a failed run's output (commands whose errors are shown).
    capture: bool = false,
    /// Git may not ask for credentials on a terminal or open an editor
    /// (pushes, pulls, merges): it fails, or takes the default message.
    batch: bool = false,
};

pub const Output = struct {
    stdout: []u8,
    stderr: []u8,
    /// Exited with status 0.
    ok: bool,
};

/// Runs a command to completion, keeping stdout and stderr whether or not
/// it succeeds. `batch`: see `Options`.
pub fn runFull(gpa: Allocator, io: Io, argv: []const []const u8, cwd: []const u8, input: ?[]const u8, batch: bool) !Output {
    var env: ?std.process.Environ.Map = null;
    defer if (env) |*e| e.deinit();
    if (batch) {
        env = try askpass.currentEnviron(gpa, io);
        try env.?.put("GIT_TERMINAL_PROMPT", "0");
        try env.?.put("GIT_EDITOR", "true");
    }
    var child = try std.process.spawn(io, .{
        .argv = argv,
        .cwd = .{ .path = cwd },
        .environ_map = if (env) |*e| e else null,
        .stdin = if (input != null) .pipe else .ignore,
        .stdout = .pipe,
        .stderr = .pipe,
    });
    defer child.kill(io);
    if (input) |data| {
        child.stdin.?.writeStreamingAll(io, data) catch {};
        child.stdin.?.close(io);
        child.stdin = null;
    }
    var streams: Io.File.MultiReader.Buffer(2) = undefined;
    var reader: Io.File.MultiReader = undefined;
    reader.init(gpa, io, streams.toStreams(), &.{ child.stdout.?, child.stderr.? });
    defer reader.deinit();
    while (reader.fill(64, .none)) |_| {
        if (reader.reader(0).buffered().len > max_output or reader.reader(1).buffered().len > max_output) return error.StreamTooLong;
    } else |err| switch (err) {
        error.EndOfStream => {},
        else => |e| return e,
    }
    try reader.checkAnyError();
    const term = try child.wait(io);
    const stdout = try reader.toOwnedSlice(0);
    errdefer gpa.free(stdout);
    const stderr = try reader.toOwnedSlice(1);
    return .{ .stdout = stdout, .stderr = stderr, .ok = term == .exited and term.exited == 0 };
}

/// Runs a command to completion and returns its stdout, or null when it
/// exits with an error. `input` is written to stdin first (git reads all
/// of it before printing anything, so this can't deadlock).
pub fn runCommand(gpa: Allocator, io: Io, argv: []const []const u8, cwd: []const u8, input: ?[]const u8) !?[]u8 {
    var child = try std.process.spawn(io, .{
        .argv = argv,
        .cwd = .{ .path = cwd },
        .stdin = if (input != null) .pipe else .ignore,
        .stdout = .pipe,
        .stderr = .ignore,
    });
    defer child.kill(io);
    if (input) |data| {
        child.stdin.?.writeStreamingAll(io, data) catch {};
        child.stdin.?.close(io);
        child.stdin = null;
    }
    var buf: [16 * 1024]u8 = undefined;
    var reader = child.stdout.?.readerStreaming(io, &buf);
    const out = try reader.interface.allocRemaining(gpa, .limited(max_output));
    errdefer gpa.free(out);
    const term = try child.wait(io);
    if (term != .exited or term.exited != 0) {
        gpa.free(out);
        return null;
    }
    return out;
}

/// `git rev-parse --show-toplevel` output → the root path.
pub fn parseToplevel(output: []const u8) []const u8 {
    return std.mem.trimEnd(u8, output, "\r\n");
}

/// Latest modification of a repository's index, HEAD and HEAD log (all in
/// `git_dir`, the `.git` folder): one changes on commit, checkout, reset,
/// rebase, staging and fetch... made by any program.
pub fn repoStamp(io: Io, git_dir: []const u8) i128 {
    var stamp: i128 = 0;
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    for ([_][]const u8{ "index", "HEAD", "logs/HEAD", "FETCH_HEAD" }) |name| {
        const p = std.fmt.bufPrint(&buf, "{s}/{s}", .{ git_dir, name }) catch continue;
        const st = Io.Dir.cwd().statFile(io, p, .{}) catch continue;
        stamp = @max(stamp, st.mtime.nanoseconds);
    }
    return stamp;
}

/// Whether `git` can be run at all (used to skip tests without it).
pub fn available(gpa: Allocator, io: Io) bool {
    const out = runCommand(gpa, io, &.{ "git", "--version" }, ".", null) catch return false;
    if (out) |o| gpa.free(o);
    return out != null;
}

test "a job's thread leaves the job alone once it is posted" {
    // The main thread may free a job as soon as it is posted. Draining on
    // the worker, inside `post`, runs `finish` (which frees it here) before
    // `run` returns, and freed memory is overwritten (the debug allocator
    // only faults once a whole page is free), so any touch of the job after
    // posting follows a garbage pointer.
    const Poisoned = struct {
        child: Allocator,
        fn allocator(self: *@This()) Allocator {
            return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
        }
        fn alloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            return self.child.rawAlloc(len, alignment, ret_addr);
        }
        fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            return self.child.rawResize(memory, alignment, new_len, ret_addr);
        }
        fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            return self.child.rawRemap(memory, alignment, new_len, ret_addr);
        }
        fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            @memset(memory, 0xaa);
            self.child.rawFree(memory, alignment, ret_addr);
        }
    };
    var poisoned: Poisoned = .{ .child = std.testing.allocator };
    const gpa = poisoned.allocator();
    if (!available(gpa, std.testing.io)) return error.SkipZigTest;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    executor.waker = struct {
        fn drainNow(ctx: *anyopaque) void {
            const ex: *Executor = @ptrCast(@alignCast(ctx));
            ex.drain();
        }
    }.drainNow;
    executor.waker_ctx = &executor;
    var runner: Runner = .init(gpa, std.testing.io, &executor);
    defer runner.deinit();
    const Owner = struct {
        fn done(_: *anyopaque, job: *Job) void {
            job.destroy();
        }
    };
    var owner: u8 = 0;
    // One at a time: `finish` keeps main-thread state (runner.alive) that
    // two workers draining at once would race on.
    for (0..8) |_| {
        _ = try Job.start(&runner, &.{"--version"}, ".", null, null, &owner, Owner.done);
        while (executor.active.load(.acquire) > 0) std.Thread.yield() catch {};
    }
}
