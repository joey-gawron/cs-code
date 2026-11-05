//! cs-zig-server: the remote half of Remote-SSH editing (VS Code's "VS Code
//! Server"). The editor starts it on the remote machine with
//! `ssh host ~/.cs-zig-server/<version>/cs-zig-server --stdio` and talks
//! model/remote/protocol.zig frames over stdin/stdout. It serves files, the
//! project scan and Find in Files (the same code the editor runs locally),
//! inotify watches, and child processes with piped stdio (language servers).
//!
//! No GUI, font or GPU code and no libc, so one static binary per
//! architecture runs on any Linux host (`zig build server`).

const std = @import("std");
const builtin = @import("builtin");
const protocol = @import("../model/remote/protocol.zig");
const project = @import("../model/project/project.zig");
const search = @import("../model/project/search.zig");
const pty = @import("pty.zig");

const linux = std.os.linux;
const Allocator = std.mem.Allocator;
const Io = std.Io;
const Value = std.json.Value;
const log = std.log.scoped(.server);

pub const std_options: std.Options = .{ .log_level = .warn };

const usage =
    \\Usage: cs-zig-server --stdio
    \\
    \\The remote side of cs-zig's Remote-SSH editing. Speaks the editor's
    \\framed JSON protocol on stdin/stdout; the editor starts it over ssh.
    \\
;

pub fn main(init: std.process.Init) !u8 {
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    var stdio = false;
    for (args[1..]) |a| {
        if (std.mem.eql(u8, a, "--stdio")) {
            stdio = true;
        } else if (std.mem.eql(u8, a, "--version")) {
            std.debug.print("cs-zig-server protocol {d} ({t})\n", .{ protocol.protocol_version, builtin.cpu.arch });
            return 0;
        } else {
            std.debug.print("{s}", .{usage});
            return 2;
        }
    }
    if (!stdio) {
        std.debug.print("{s}", .{usage});
        return 2;
    }
    // A language server that dies must not take us down with SIGPIPE.
    const ignore: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.IGN }, .mask = std.posix.sigemptyset(), .flags = 0 };
    std.posix.sigaction(std.posix.SIG.PIPE, &ignore, null);
    var server: Server = .{ .gpa = init.gpa, .io = init.io, .environ = init.environ_map, .watcher = .init() };
    defer server.deinit();
    server.run();
    return 0;
}

const Method = enum { init, stat, read, write, list, mkdir, rename, delete, watch, scan, search, cancel, spawn, stdin, close_stdin, kill, resize, cwd, shutdown };

/// A child process: a language server on pipes, or a terminal's shell on a
/// pty. Its output is forwarded as "proc.out" notifications by `forward` on
/// its own thread.
const Proc = struct {
    server: *Server,
    handle: u32,
    /// Pipe children; null for pty children (reaped with wait4).
    child: ?std.process.Child,
    pid: linux.pid_t,
    /// Our ends of the child's pipes (both the pty master for a pty);
    /// stdin is -1 once closed.
    stdin_fd: i32,
    stdout_fd: i32,
    is_pty: bool = false,
    exited: bool = false,
};

const Server = struct {
    gpa: Allocator,
    io: Io,
    environ: *std.process.Environ.Map,
    watcher: Watcher,
    /// Frames are written from several threads (searches, process output).
    out_lock: Io.Mutex = .init,
    /// Guards `procs`, `searches`, `stopping` and `login_env`.
    lock: Io.Mutex = .init,
    procs: std.AutoHashMapUnmanaged(u32, *Proc) = .empty,
    searches: std.AutoHashMapUnmanaged(i64, *search.Job) = .empty,
    /// Set by `shutdown`: a search that registers after it is canceled at once.
    stopping: bool = false,
    /// The login shell's environment (PATH from ~/.profile etc.), resolved
    /// on the first spawn like VS Code does; null if that failed.
    login_env: ?std.process.Environ.Map = null,
    login_env_done: bool = false,
    stdout_dead: std.atomic.Value(bool) = .init(false),
    /// Worker and `forward` threads still running; `shutdown` waits for them.
    threads: std.atomic.Value(u32) = .init(0),

    /// After `shutdown`, when no thread is left.
    fn deinit(self: *Server) void {
        self.procs.deinit(self.gpa);
        self.searches.deinit(self.gpa);
        if (self.login_env) |*e| e.deinit();
        self.watcher.deinit(self.gpa);
    }

    fn run(self: *Server) void {
        // The banner: the client skips whatever a login shell printed first.
        var banner: [64]u8 = undefined;
        const line = std.fmt.bufPrint(&banner, "{s} {d} {t}\n", .{ protocol.banner_magic, protocol.protocol_version, builtin.cpu.arch }) catch unreachable;
        protocol.writeAll(1, line) catch return;

        var reader: protocol.FrameReader = .{};
        defer reader.deinit(self.gpa);
        var buf: [64 * 1024]u8 = undefined;
        loop: while (!self.stdout_dead.load(.acquire)) {
            var fds = [_]linux.pollfd{
                .{ .fd = 0, .events = linux.POLL.IN, .revents = 0 },
                .{ .fd = self.watcher.fd, .events = linux.POLL.IN, .revents = 0 },
            };
            const rc = linux.poll(&fds, if (self.watcher.fd >= 0) 2 else 1, -1);
            if (linux.errno(rc) == .INTR) continue;
            if (fds[1].revents & linux.POLL.IN != 0) self.watcher.drain(self);
            if (fds[0].revents == 0) continue;
            const n = protocol.readSome(0, &buf) catch break;
            if (n == 0) break;
            reader.feed(self.gpa, buf[0..n]) catch break;
            while (reader.next() catch break :loop) |frame| {
                if (!self.handleFrame(frame)) break :loop;
            }
        }
        self.shutdown();
    }

    /// Stops children and waits for running workers before exiting: they use
    /// the server (on main's stack) and the allocator until their last line.
    fn shutdown(self: *Server) void {
        self.lock.lockUncancelable(self.io);
        self.stopping = true;
        var it = self.procs.valueIterator();
        while (it.next()) |p| if (!p.*.exited) {
            // Hang up on a terminal's group as closing it does: a job left
            // running keeps the pty open, but nohup'd ones live on, as over ssh.
            if (p.*.is_pty) _ = linux.kill(-p.*.pid, linux.SIG.HUP);
            _ = linux.kill(p.*.pid, linux.SIG.KILL);
            // The child of a launcher that didn't exec holds the output open
            // until its stdin closes (language servers exit then).
            if (!p.*.is_pty and p.*.stdin_fd >= 0) {
                protocol.closeFd(p.*.stdin_fd);
                p.*.stdin_fd = -1;
            }
        };
        var sit = self.searches.valueIterator();
        while (sit.next()) |j| j.*.cancel();
        self.lock.unlock(self.io);
        // A scan can't be canceled, and a terminal's job that ignores the
        // hangup or has a group of its own can keep the pty open: past a few
        // seconds, exit as is.
        var waited: u32 = 0;
        while (self.threads.load(.acquire) > 0) : (waited += 1) {
            if (waited == 300) std.process.exit(0);
            Io.sleep(self.io, .fromMilliseconds(10), .awake) catch {};
        }
    }

    // ---- Writing ------------------------------------------------------------

    fn send(self: *Server, json: []const u8, payload: []const u8) void {
        self.out_lock.lockUncancelable(self.io);
        defer self.out_lock.unlock(self.io);
        protocol.writeFrame(1, json, payload) catch self.stdout_dead.store(true, .release);
    }

    /// Requests sent as notifications (id -1, e.g. "watch") get no answer.
    fn respond(self: *Server, id: i64, result: anytype, payload: []const u8) void {
        if (id < 0) return;
        const json = std.json.Stringify.valueAlloc(self.gpa, .{ .id = id, .result = result }, .{}) catch return;
        defer self.gpa.free(json);
        self.send(json, payload);
    }

    fn respondError(self: *Server, id: i64, err: anyerror) void {
        if (id < 0) return;
        const json = std.json.Stringify.valueAlloc(self.gpa, .{ .id = id, .@"error" = @errorName(err) }, .{}) catch return;
        defer self.gpa.free(json);
        self.send(json, "");
    }

    fn notify(self: *Server, method: []const u8, params: anytype, payload: []const u8) void {
        const json = std.json.Stringify.valueAlloc(self.gpa, .{ .method = method, .params = params }, .{}) catch return;
        defer self.gpa.free(json);
        self.send(json, payload);
    }

    // ---- Requests -----------------------------------------------------------

    /// Returns false to stop serving.
    fn handleFrame(self: *Server, frame: protocol.Frame) bool {
        var parsed = std.json.parseFromSlice(Value, self.gpa, frame.json, .{}) catch {
            log.warn("bad frame", .{});
            return true;
        };
        defer parsed.deinit();
        const msg = parsed.value;
        const method = std.meta.stringToEnum(Method, protocol.getStr(msg, "method") orelse return true) orelse {
            if (protocol.getInt(msg, "id")) |id| self.respondError(id, error.InvalidArgument);
            return true;
        };
        if (method == .shutdown) return false;
        const id = protocol.getInt(msg, "id");
        self.dispatch(method, id orelse -1, protocol.get(msg, "params"), frame.payload) catch |err| {
            if (id) |i| self.respondError(i, err);
        };
        return true;
    }

    fn dispatch(self: *Server, method: Method, id: i64, params: ?Value, payload: []const u8) !void {
        const io = self.io;
        const cwd = Io.Dir.cwd();
        switch (method) {
            .init => self.respond(id, .{
                .protocol = protocol.protocol_version,
                .home = self.environ.get("HOME") orelse "/",
                .pid = linux.getpid(),
                .arch = @tagName(builtin.cpu.arch),
            }, ""),
            .stat => {
                const st = try cwd.statFile(io, try pathParam(params, "path"), .{});
                self.respond(id, .{ .kind = kindName(st.kind), .size = st.size, .mtime = @as(i64, @intCast(st.mtime.nanoseconds)) }, "");
            },
            .read => {
                const limit: usize = @intCast(@max(0, protocol.getInt(params, "limit") orelse protocol.max_payload));
                const data = try cwd.readFileAlloc(io, try pathParam(params, "path"), self.gpa, .limited(@min(limit, protocol.max_payload)));
                defer self.gpa.free(data);
                self.respond(id, .{ .size = data.len }, data);
            },
            .write => {
                const path = try pathParam(params, "path");
                // Next to the target, then rename: a crash never truncates the file.
                const tmp = try std.fmt.allocPrint(self.gpa, "{s}.cs-zig-tmp", .{path});
                defer self.gpa.free(tmp);
                try cwd.writeFile(io, .{ .sub_path = tmp, .data = payload });
                try Io.Dir.rename(cwd, tmp, cwd, path, io);
                self.respond(id, .{ .size = payload.len }, "");
            },
            .list => try self.list(id, try pathParam(params, "path")),
            .mkdir => {
                try cwd.createDirPath(io, try pathParam(params, "path"));
                self.respond(id, .{}, "");
            },
            .rename => {
                try Io.Dir.rename(cwd, try pathParam(params, "from"), cwd, try pathParam(params, "to"), io);
                self.respond(id, .{}, "");
            },
            .delete => {
                const path = try pathParam(params, "path");
                const st = try cwd.statFile(io, path, .{ .follow_symlinks = false });
                if (st.kind == .directory) {
                    if (protocol.getBool(params, "recursive")) try cwd.deleteTree(io, path) else try cwd.deleteDir(io, path);
                } else try cwd.deleteFile(io, path);
                self.respond(id, .{}, "");
            },
            .watch => {
                var arena = std.heap.ArenaAllocator.init(self.gpa);
                defer arena.deinit();
                self.watcher.sync(self.gpa, try protocol.getStrings(arena.allocator(), params, "dirs"));
                self.respond(id, .{}, "");
            },
            .scan => try self.startWorker(id, params, payload, scanWorker),
            .search => try self.startWorker(id, params, payload, searchWorker),
            .cancel => {
                const target = protocol.getInt(params, "id") orelse return;
                self.lock.lockUncancelable(io);
                defer self.lock.unlock(io);
                if (self.searches.get(target)) |job| job.cancel();
            },
            .spawn => try self.spawn(id, params),
            // Under the lock: `forward` frees a process once it exits.
            .stdin, .close_stdin, .kill, .resize, .cwd => {
                self.lock.lockUncancelable(io);
                defer self.lock.unlock(io);
                const handle: u32 = @intCast(@max(0, protocol.getInt(params, "handle") orelse return));
                const p = self.procs.get(handle) orelse return;
                switch (method) {
                    .stdin => if (p.stdin_fd >= 0) protocol.writeAll(p.stdin_fd, payload) catch {},
                    // A pty's master is its output too: only hanging up closes it.
                    .close_stdin => if (p.stdin_fd >= 0 and !p.is_pty) {
                        protocol.closeFd(p.stdin_fd);
                        p.stdin_fd = -1;
                    },
                    .resize => if (p.is_pty and p.stdin_fd >= 0) {
                        const rows = protocol.getInt(params, "rows") orelse 24;
                        const cols = protocol.getInt(params, "cols") orelse 80;
                        pty.setSize(p.stdin_fd, @intCast(std.math.clamp(rows, 1, 1000)), @intCast(std.math.clamp(cols, 1, 1000)));
                    },
                    .cwd => {
                        var link_buf: [32]u8 = undefined;
                        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
                        const link = std.fmt.bufPrintSentinel(&link_buf, "/proc/{d}/cwd", .{p.pid}, 0) catch return error.NameTooLong;
                        const n = linux.readlink(link, &path_buf, path_buf.len);
                        if (linux.errno(n) != .SUCCESS) return error.FileNotFound;
                        self.respond(id, .{ .path = path_buf[0..n] }, "");
                    },
                    else => if (!p.exited) {
                        if (p.is_pty) {
                            // Like closing a terminal window: hang up on the session.
                            _ = linux.kill(-p.pid, linux.SIG.HUP);
                            _ = linux.kill(p.pid, linux.SIG.HUP);
                        } else _ = linux.kill(p.pid, linux.SIG.KILL);
                    },
                }
            },
            .shutdown => {},
        }
    }

    fn list(self: *Server, id: i64, path: []const u8) !void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var dir = try Io.Dir.cwd().openDir(self.io, path, .{ .iterate = true });
        defer dir.close(self.io);
        const Item = struct { name: []const u8, kind: []const u8 };
        var items: std.ArrayList(Item) = .empty;
        var it = dir.iterate();
        while (try it.next(self.io)) |e| {
            var kind = e.kind;
            // Show symlinked folders as folders.
            if (kind == .sym_link) if (dir.statFile(self.io, e.name, .{})) |st| {
                kind = st.kind;
            } else |_| {};
            try items.append(alloc, .{ .name = try alloc.dupe(u8, e.name), .kind = kindName(kind) });
        }
        self.respond(id, .{ .entries = items.items }, "");
    }

    // ---- Workers: scan and search ---------------------------------------------

    const Work = struct {
        server: *Server,
        id: i64,
        /// Owns the parsed params and payload copy.
        arena: std.heap.ArenaAllocator,
        params: Value,
        payload: []const u8,
    };

    fn startWorker(self: *Server, id: i64, params: ?Value, payload: []const u8, comptime worker: fn (*Work) anyerror!void) !void {
        const work = try self.gpa.create(Work);
        errdefer self.gpa.destroy(work);
        work.* = .{ .server = self, .id = id, .arena = .init(self.gpa), .params = .null, .payload = "" };
        errdefer work.arena.deinit();
        const alloc = work.arena.allocator();
        // Parsed values point into the frame buffer: copy them for the thread.
        const json = try std.json.Stringify.valueAlloc(alloc, params orelse Value.null, .{});
        work.params = try std.json.parseFromSliceLeaky(Value, alloc, json, .{});
        work.payload = try alloc.dupe(u8, payload);
        const S = struct {
            fn run(w: *Work) void {
                const server = w.server;
                worker(w) catch |err| server.respondError(w.id, err);
                w.arena.deinit();
                server.gpa.destroy(w);
                // Once counted out, main may return and the server is gone:
                // the count is the last thing touched.
                _ = server.threads.fetchSub(1, .release);
            }
        };
        _ = self.threads.fetchAdd(1, .monotonic);
        const thread = std.Thread.spawn(.{}, S.run, .{work}) catch |err| {
            _ = self.threads.fetchSub(1, .monotonic);
            return err;
        };
        thread.detach();
    }

    fn scanWorker(w: *Work) !void {
        const self = w.server;
        var snap = try project.buildSnapshot(self.gpa, self.io, try pathParam(w.params, "root"));
        defer snap.deinit();
        var enc: protocol.Encoder = .{};
        defer enc.deinit(self.gpa);
        for (snap.entries) |e| try protocol.encodeScanEntry(&enc, self.gpa, .{ .path = e.path, .dir = e.kind == .dir, .ignored = e.ignored, .depth = e.depth });
        self.respond(w.id, .{ .count = snap.entries.len, .truncated = snap.truncated }, enc.out.items);
    }

    fn searchWorker(w: *Work) !void {
        const self = w.server;
        const alloc = w.arena.allocator();
        const p = w.params;
        var overlays: std.ArrayList(search.Overlay) = .empty;
        var dec: protocol.Decoder = .{ .data = w.payload };
        while (!dec.done()) try overlays.append(alloc, .{ .path = try dec.bytes(), .text = try dec.bytes() });
        var where: search.Where = .{};
        try where.includes.appendSlice(alloc, try protocol.getStrings(alloc, p, "includes"));
        try where.excludes.appendSlice(alloc, try protocol.getStrings(alloc, p, "excludes"));
        var job: search.Job = .{
            .gpa = self.gpa,
            .io = self.io,
            .executor = undefined,
            .arena = .init(self.gpa),
            .query = .{
                .pattern = protocol.getStr(p, "pattern") orelse "",
                .regex = protocol.getBool(p, "regex"),
                .case_sensitive = protocol.getBool(p, "case_sensitive"),
                .whole_word = protocol.getBool(p, "whole_word"),
                .context = @intCast(std.math.clamp(protocol.getInt(p, "context") orelse 2, 0, 7)),
            },
            .roots = try protocol.getStrings(alloc, p, "roots"),
            .files = try protocol.getStrings(alloc, p, "files"),
            .where = where,
            .overlays = overlays.items,
            .ctx = undefined,
            .done = undefined,
            .header_prefix = protocol.getStr(p, "prefix") orelse "",
        };
        defer job.arena.deinit();
        {
            self.lock.lockUncancelable(self.io);
            defer self.lock.unlock(self.io);
            try self.searches.put(self.gpa, w.id, &job);
            // Too late for shutdown's cancel, which then waits for this thread.
            if (self.stopping) job.cancel();
        }
        defer {
            self.lock.lockUncancelable(self.io);
            _ = self.searches.remove(w.id);
            self.lock.unlock(self.io);
        }
        var results = try job.search();
        defer results.deinit();
        self.respond(w.id, .{
            .matches = results.matches,
            .files_searched = results.files_searched,
            .files_matched = results.files_matched,
            .truncated = results.truncated,
            .regex_error = results.regex_error,
        }, results.text);
    }

    // ---- Processes ----------------------------------------------------------

    fn spawn(self: *Server, id: i64, params: ?Value) !void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const argv = try protocol.getStrings(arena.allocator(), params, "argv");
        const handle: u32 = @intCast(@max(0, protocol.getInt(params, "handle") orelse return error.InvalidArgument));
        const cwd = protocol.getStr(params, "cwd");
        const env = self.loginEnv();
        // Terminals may leave argv empty: the login shell.
        if (protocol.get(params, "pty") != null) return self.spawnPty(id, handle, argv, cwd, env, params);
        if (argv.len == 0) return error.InvalidArgument;
        var child = try std.process.spawn(self.io, .{
            .argv = argv,
            .cwd = if (cwd) |c| .{ .path = c } else .inherit,
            .environ_map = env,
            .stdin = .pipe,
            .stdout = .pipe,
            .stderr = .ignore,
        });
        const p = self.gpa.create(Proc) catch |err| {
            child.kill(self.io);
            return err;
        };
        // The pipes are ours from here on (Child.wait must not close them).
        p.* = .{ .server = self, .handle = handle, .child = child, .pid = child.id.?, .stdin_fd = child.stdin.?.handle, .stdout_fd = child.stdout.?.handle };
        p.child.?.stdin = null;
        p.child.?.stdout = null;
        try self.startProc(id, p);
    }

    /// A terminal: argv (default: the login shell) on a new pty.
    fn spawnPty(self: *Server, id: i64, handle: u32, argv_in: []const []const u8, cwd: ?[]const u8, login_env: ?*const std.process.Environ.Map, params: ?Value) !void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var env = std.process.Environ.Map.init(alloc);
        const base = login_env orelse self.environ;
        var it = base.iterator();
        while (it.next()) |e| try env.put(e.key_ptr.*, e.value_ptr.*);
        try env.put("TERM", "xterm-256color");
        try env.put("COLORTERM", "truecolor");
        const shell = env.get("SHELL") orelse "/bin/sh";
        const argv: []const []const u8 = if (argv_in.len > 0 and argv_in[0].len > 0) argv_in else &.{shell};
        const size = protocol.get(params, "pty");
        const rows = std.math.clamp(protocol.getInt(size, "rows") orelse 24, 1, 1000);
        const cols = std.math.clamp(protocol.getInt(size, "cols") orelse 80, 1, 1000);
        const spawned = try pty.spawn(alloc, argv, cwd, &env, @intCast(rows), @intCast(cols));
        const p = self.gpa.create(Proc) catch |err| {
            _ = linux.kill(spawned.pid, linux.SIG.KILL);
            _ = pty.wait(spawned.pid);
            protocol.closeFd(spawned.master);
            return err;
        };
        p.* = .{ .server = self, .handle = handle, .child = null, .pid = spawned.pid, .stdin_fd = spawned.master, .stdout_fd = spawned.master, .is_pty = true };
        try self.startProc(id, p);
    }

    fn startProc(self: *Server, id: i64, p: *Proc) !void {
        {
            self.lock.lockUncancelable(self.io);
            defer self.lock.unlock(self.io);
            try self.procs.put(self.gpa, p.handle, p);
        }
        // Answer before any output, so the client knows the process first.
        self.respond(id, .{ .pid = p.pid }, "");
        _ = self.threads.fetchAdd(1, .monotonic);
        const thread = std.Thread.spawn(.{}, forward, .{p}) catch |err| {
            _ = self.threads.fetchSub(1, .monotonic);
            _ = linux.kill(p.pid, linux.SIG.KILL);
            return err;
        };
        thread.detach();
    }

    /// Forwards a child's stdout until it closes, then reports the exit.
    fn forward(p: *Proc) void {
        const self = p.server;
        var buf: [64 * 1024]u8 = undefined;
        while (true) {
            const n = protocol.readSome(p.stdout_fd, &buf) catch break;
            if (n == 0) break;
            self.notify("proc.out", .{ .handle = p.handle }, buf[0..n]);
        }
        if (p.is_pty) {
            // stdin is the same fd: stop writers first.
            self.lock.lockUncancelable(self.io);
            p.stdin_fd = -1;
            self.lock.unlock(self.io);
        }
        protocol.closeFd(p.stdout_fd);
        const code: i64 = if (p.child) |*child| (if (child.wait(self.io)) |term| switch (term) {
            .exited => |c| c,
            else => -1,
        } else |_| -1) else pty.wait(p.pid);
        self.lock.lockUncancelable(self.io);
        p.exited = true;
        _ = self.procs.remove(p.handle);
        if (p.stdin_fd >= 0) protocol.closeFd(p.stdin_fd);
        self.lock.unlock(self.io);
        self.notify("proc.exit", .{ .handle = p.handle, .code = code }, "");
        self.gpa.destroy(p);
        // Once counted out, main may return and the server is gone: the
        // count is the last thing touched.
        _ = self.threads.fetchSub(1, .release);
    }

    /// The environment of `$SHELL -l` (once), so tools on a PATH set in
    /// ~/.profile or ~/.zprofile are found.
    fn loginEnv(self: *Server) ?*const std.process.Environ.Map {
        self.lock.lockUncancelable(self.io);
        defer self.lock.unlock(self.io);
        if (!self.login_env_done) {
            self.login_env_done = true;
            self.login_env = resolveLoginEnv(self.gpa, self.io, self.environ.get("SHELL") orelse "/bin/sh") catch null;
        }
        return if (self.login_env) |*e| e else null;
    }
};

fn resolveLoginEnv(gpa: Allocator, io: Io, shell: []const u8) !std.process.Environ.Map {
    const result = try std.process.run(gpa, io, .{
        .argv = &.{ shell, "-l", "-c", "env -0" },
        .stdout_limit = .limited(1 << 20),
        .stderr_limit = .limited(64 << 10),
        .timeout = .{ .duration = .{ .raw = .fromSeconds(5), .clock = .awake } },
    });
    defer gpa.free(result.stdout);
    defer gpa.free(result.stderr);
    if (result.term != .exited or result.term.exited != 0) return error.LoginShellFailed;
    var map = std.process.Environ.Map.init(gpa);
    errdefer map.deinit();
    var it = std.mem.splitScalar(u8, result.stdout, 0);
    while (it.next()) |kv| {
        const eq = std.mem.indexOfScalar(u8, kv, '=') orelse continue;
        if (eq == 0) continue;
        try map.put(kv[0..eq], kv[eq + 1 ..]);
    }
    if (map.get("PATH") == null) return error.LoginShellFailed;
    return map;
}

fn pathParam(params: ?Value, key: []const u8) ![]const u8 {
    const p = protocol.getStr(params, key) orelse return error.InvalidArgument;
    if (p.len == 0 or p[0] != '/') return error.BadPathName;
    return p;
}

fn kindName(kind: Io.File.Kind) []const u8 {
    return switch (kind) {
        .file => "file",
        .directory => "directory",
        .sym_link => "sym_link",
        else => "other",
    };
}

/// inotify on the folders the client asked for (the folders of open files),
/// reported as "changed" notifications.
const Watcher = struct {
    fd: i32,
    /// Watch descriptor → directory path (owned).
    dirs: std.AutoHashMapUnmanaged(i32, []u8) = .empty,

    fn init() Watcher {
        const rc = linux.inotify_init1(linux.IN.NONBLOCK | linux.IN.CLOEXEC);
        return .{ .fd = if (linux.errno(rc) == .SUCCESS) @intCast(rc) else -1 };
    }

    fn deinit(self: *Watcher, gpa: Allocator) void {
        var it = self.dirs.valueIterator();
        while (it.next()) |d| gpa.free(d.*);
        self.dirs.deinit(gpa);
        if (self.fd >= 0) protocol.closeFd(self.fd);
    }

    fn sync(self: *Watcher, gpa: Allocator, wanted: []const []const u8) void {
        if (self.fd < 0) return;
        var stale: std.ArrayList(i32) = .empty;
        defer stale.deinit(gpa);
        var it = self.dirs.iterator();
        while (it.next()) |e| {
            for (wanted) |w| {
                if (std.mem.eql(u8, w, e.value_ptr.*)) break;
            } else stale.append(gpa, e.key_ptr.*) catch {};
        }
        for (stale.items) |wd| {
            _ = linux.inotify_rm_watch(self.fd, wd);
            if (self.dirs.fetchRemove(wd)) |kv| gpa.free(kv.value);
        }
        outer: for (wanted) |dir| {
            var vit = self.dirs.valueIterator();
            while (vit.next()) |v| if (std.mem.eql(u8, v.*, dir)) continue :outer;
            const z = gpa.dupeSentinel(u8, dir, 0) catch continue;
            defer gpa.free(z);
            const mask: u32 = linux.IN.CLOSE_WRITE | linux.IN.MOVED_TO | linux.IN.CREATE | linux.IN.DELETE | linux.IN.MOVED_FROM;
            const rc = linux.inotify_add_watch(self.fd, z.ptr, mask);
            if (linux.errno(rc) != .SUCCESS) continue;
            const owned = gpa.dupe(u8, dir) catch continue;
            self.dirs.put(gpa, @intCast(rc), owned) catch gpa.free(owned);
        }
    }

    fn drain(self: *Watcher, server: *Server) void {
        const gpa = server.gpa;
        var arena = std.heap.ArenaAllocator.init(gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const Change = struct { path: []const u8, deleted: bool };
        var changes: std.ArrayList(Change) = .empty;
        var buf: [16 * 1024]u8 align(@alignOf(linux.inotify_event)) = undefined;
        while (true) {
            const rc = linux.read(self.fd, &buf, buf.len);
            if (linux.errno(rc) != .SUCCESS or rc == 0) break;
            var off: usize = 0;
            while (off + @sizeOf(linux.inotify_event) <= rc) {
                const ev: *const linux.inotify_event = @ptrCast(@alignCast(&buf[off]));
                off += @sizeOf(linux.inotify_event) + ev.len;
                const name = ev.getName() orelse continue;
                const dir = self.dirs.get(ev.wd) orelse continue;
                const path = std.fs.path.join(alloc, &.{ dir, name }) catch continue;
                changes.append(alloc, .{ .path = path, .deleted = ev.mask & (linux.IN.DELETE | linux.IN.MOVED_FROM) != 0 }) catch break;
            }
        }
        if (changes.items.len > 0) server.notify("changed", .{ .changes = changes.items }, "");
    }
};
