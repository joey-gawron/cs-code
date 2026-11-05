//! A connection to jiffy-edit-server on another machine (VS Code's Remote-SSH
//! client side). It starts `ssh host <launch script>` (or, for tests and
//! CS_ZIG_REMOTE_TRANSPORT=local, the server binary directly), uploads the
//! server through that same session if the host doesn't have this build
//! (the launch script installs and starts it), and then serves as the
//! vfs.Backend for `ssh://host/...` paths. When ssh needs a password, a
//! passphrase or a host key confirmation, its askpass helper reaches this
//! connection (askpass.zig) and the question goes to the owner as an event.
//!
//! Threads: requests (`call`) may come from any thread and block until their
//! answer; a reader thread routes answers to their callers, writes process
//! output into the local pipes of remote processes, and posts file-change
//! and state events to the main thread through the executor. When the
//! channel drops, waiting calls fail with error.NotConnected, remote process
//! pipes reach EOF, and the owner decides when to reconnect.

const std = @import("std");
const builtin = @import("builtin");
const protocol = @import("protocol.zig");
const path_mod = @import("path.zig");
const vfs = @import("vfs.zig");
const ssh = @import("ssh.zig");
const askpass = @import("askpass.zig");
const project = @import("../project/project.zig");
const search = @import("../project/search.zig");
const executor_mod = @import("../../framework/util/executor.zig");

const linux = @import("sys.zig");
const Allocator = std.mem.Allocator;
const Io = std.Io;
const Value = std.json.Value;
const Executor = executor_mod.Executor;
const SpinLock = executor_mod.SpinLock;
const log = std.log.scoped(.remote);

pub const State = enum(u8) {
    idle,
    connecting,
    connected,
    /// The channel dropped; the owner may reconnect.
    lost,
    /// Connecting failed (see `statusText`).
    failed,
};

pub const Transport = union(enum) {
    /// `ssh <host> sh -c '<launch script>'`, deploying the server if needed.
    ssh,
    /// Runs this server binary as a local subprocess instead (no ssh).
    subprocess: []const u8,
};

pub const Options = struct {
    transport: Transport = .ssh,
    /// Server builds the connection may upload, one per architecture.
    binaries: []const ssh.Binary = &.{},
    /// The ssh executable (tests substitute one that runs commands locally).
    ssh_program: []const u8 = "ssh",
    /// What ssh runs to ask for passwords and host key confirmations
    /// (SSH_ASKPASS: the editor binary, see askpass.zig). Without it, or
    /// when `connect` blocks its caller, ssh runs in BatchMode.
    askpass: ?[]const u8 = null,
    /// Default limit for a request's answer.
    timeout_ms: i64 = 60_000,
};

pub const Event = union(enum) {
    /// A watched file changed on the remote (full ssh:// path).
    changed: struct { path: []const u8, deleted: bool },
    /// The state or the status text changed.
    state,
    /// ssh asks something (password, passphrase, host key): show it and
    /// reply with `answerPrompt`. The text is valid during the callback.
    prompt: Prompt,
    /// Question `id` went away unanswered (the attempt ended).
    prompt_closed: u32,
};

pub const PromptKind = askpass.Kind;
pub const Prompt = struct { id: u32, kind: PromptKind, text: []const u8 };

/// Main-thread callbacks.
pub const Sink = struct {
    ctx: *anyopaque,
    onEvent: *const fn (ctx: *anyopaque, conn: *Connection, event: Event) void,
};

pub const Response = struct {
    parsed: std.json.Parsed(Value),
    /// In the allocator passed to `call`.
    payload: []u8,
    alloc: Allocator,

    pub fn result(self: *const Response) ?Value {
        return protocol.get(self.parsed.value, "result");
    }

    pub fn deinit(self: *Response) void {
        self.parsed.deinit();
        self.alloc.free(self.payload);
    }
};

const Waiter = struct {
    event: Io.Event = .unset,
    /// Allocator for `payload` (the caller's); `json` uses the connection's.
    alloc: Allocator,
    json: []u8 = &.{},
    payload: []u8 = &.{},
    failed: bool = false,
};

pub const CallOptions = struct {
    timeout_ms: ?i64 = null,
    /// When it becomes true the server is asked to cancel (searches).
    cancel: ?*const std.atomic.Value(bool) = null,
};

pub const Connection = struct {
    gpa: Allocator,
    io: Io,
    executor: *Executor,
    /// The ssh destination, as in `ssh://<host>/path`.
    host: []u8,
    /// "ssh://host", prepended to remote paths in results.
    prefix: []u8,
    options: Options,
    sink: ?Sink = null,
    state: std.atomic.Value(State) = .init(.idle),
    /// A connect attempt is running (on some thread).
    busy: std.atomic.Value(bool) = .init(false),
    /// Set by `disconnect`: stop a running attempt, don't report the drop.
    closing: std.atomic.Value(bool) = .init(false),
    /// Requests can be sent.
    up: std.atomic.Value(bool) = .init(false),

    status_lock: SpinLock = .{},
    status_buf: [240]u8 = undefined,
    status_len: usize = 0,

    child: ?std.process.Child = null,
    child_pid: linux.pid_t = 0,
    to_server: i32 = -1,
    from_server: i32 = -1,
    child_stderr: i32 = -1,
    reader: ?std.Thread = null,
    reader_done: std.atomic.Value(bool) = .init(false),
    stderr_reader: ?std.Thread = null,
    /// The end of ssh's stderr, for error messages ("Permission denied").
    stderr_tail: [512]u8 = undefined,
    stderr_len: std.atomic.Value(usize) = .init(0),
    /// Bytes read during the handshake that already belong to frames.
    leftover: std.ArrayList(u8) = .empty,

    write_lock: Io.Mutex = .init,
    /// Guards `pending` and `procs`.
    lock: Io.Mutex = .init,
    pending: std.AutoHashMapUnmanaged(u32, *Waiter) = .empty,
    procs: std.AutoHashMapUnmanaged(u32, *Proc) = .empty,
    next_id: std.atomic.Value(u32) = .init(1),

    /// From the server's `init` answer.
    home: []u8 = &.{},
    /// Replaced by each connect while the main thread may read it (a remote
    /// language server's start).
    remote_pid: std.atomic.Value(i64) = .init(0),
    arch_buf: [16]u8 = undefined,
    arch_len: usize = 0,
    /// Watched directories (remote paths), sent again after a reconnect.
    watch_dirs: std.ArrayList([]u8) = .empty,
    watch_lock: SpinLock = .{},
    /// Successful connects so far (>1 means this was a reconnect).
    connects: u32 = 0,
    /// Takes ssh's questions; started by the first attempt that can ask.
    listener: ?*askpass.Listener = null,
    /// This attempt can ask the user (connectAsync with an askpass program).
    interactive: bool = false,
    /// Events go to the executor (off once quiesced).
    posting: std.atomic.Value(bool) = .init(true),
    quiesced: bool = false,

    pub fn create(gpa: Allocator, io: Io, executor: *Executor, host: []const u8, options: Options) !*Connection {
        const self = try gpa.create(Connection);
        errdefer gpa.destroy(self);
        const owned = try gpa.dupe(u8, host);
        errdefer gpa.free(owned);
        self.* = .{
            .gpa = gpa,
            .io = io,
            .executor = executor,
            .host = owned,
            .prefix = try std.fmt.allocPrint(gpa, "{s}{s}", .{ path_mod.scheme, host }),
            .options = options,
        };
        return self;
    }

    /// Shutdown, part one: disconnects (calls in flight fail at once) and
    /// delivers the events already posted, while their receiver still exists.
    /// Nothing is posted afterwards. The object stays valid for `destroy`.
    pub fn quiesce(self: *Connection) void {
        if (self.quiesced) return;
        self.quiesced = true;
        self.disconnect();
        self.posting.store(false, .release);
        self.executor.drain();
        self.sink = null;
    }

    /// Process exit only: backends stay registered for the process lifetime.
    pub fn destroy(self: *Connection) void {
        self.quiesce();
        if (self.listener) |l| l.destroy();
        vfs.unregister(self.host);
        for (self.watch_dirs.items) |d| self.gpa.free(d);
        self.watch_dirs.deinit(self.gpa);
        self.leftover.deinit(self.gpa);
        self.pending.deinit(self.gpa);
        self.procs.deinit(self.gpa);
        self.gpa.free(self.home);
        self.gpa.free(self.prefix);
        self.gpa.free(self.host);
        self.gpa.destroy(self);
    }

    pub fn backend(self: *Connection) vfs.Backend {
        return .{ .ctx = self, .vtable = &backend_vtable };
    }

    pub fn currentState(self: *const Connection) State {
        return self.state.load(.acquire);
    }

    pub fn arch(self: *const Connection) []const u8 {
        return self.arch_buf[0..self.arch_len];
    }

    /// "Uploading jiffy-edit-server… 40%", "Permission denied (publickey)".
    pub fn statusText(self: *Connection, buf: []u8) []const u8 {
        self.status_lock.lock();
        defer self.status_lock.unlock();
        const n = @min(buf.len, self.status_len);
        @memcpy(buf[0..n], self.status_buf[0..n]);
        return buf[0..n];
    }

    fn setStatus(self: *Connection, comptime fmt: []const u8, args: anytype) void {
        self.status_lock.lock();
        const s = std.fmt.bufPrint(&self.status_buf, fmt, args) catch self.status_buf[0..];
        self.status_len = s.len;
        self.status_lock.unlock();
        self.postState();
    }

    fn setState(self: *Connection, state: State) void {
        self.state.store(state, .release);
        self.postState();
    }

    // ---- Connecting -----------------------------------------------------------

    /// Connects on a background thread; progress and the outcome arrive as
    /// `.state` events. Does nothing while an attempt is running.
    pub fn connectAsync(self: *Connection) void {
        if (self.busy.swap(true, .acq_rel)) return;
        self.state.store(.connecting, .release);
        self.interactive = self.canAsk();
        const thread = std.Thread.spawn(.{}, connectThread, .{self}) catch {
            self.busy.store(false, .release);
            self.setStatus("Cannot start connection thread", .{});
            self.setState(.failed);
            return;
        };
        thread.detach();
    }

    fn connectThread(self: *Connection) void {
        self.connectInner() catch {};
        self.busy.store(false, .release);
    }

    /// Connects on the calling thread (command line, tests). Nobody can
    /// answer ssh's questions meanwhile, so key or agent auth only.
    pub fn connect(self: *Connection) !void {
        if (self.busy.swap(true, .acq_rel)) return error.Busy;
        defer self.busy.store(false, .release);
        self.interactive = false;
        return self.connectInner();
    }

    /// Starts the askpass listener if this connection can ask the user.
    fn canAsk(self: *Connection) bool {
        if (self.options.transport != .ssh or self.options.askpass == null or self.sink == null) return false;
        if (self.listener != null) return true;
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const env = askpass.currentEnviron(arena.allocator(), self.io) catch return false;
        self.listener = askpass.Listener.create(self.gpa, self.io, env.get("XDG_RUNTIME_DIR"), .{
            .ctx = self,
            .ask = askUser,
            .notice = showNotice,
            .withdrawn = questionGone,
        }) catch |err| {
            log.warn("askpass: {t}; ssh runs in BatchMode", .{err});
            return false;
        };
        return true;
    }

    /// Answers question `id` of a `.prompt` event; null refuses it.
    pub fn answerPrompt(self: *Connection, id: u32, reply: ?[]const u8) void {
        if (self.listener) |l| _ = l.answer(id, reply);
    }

    /// Gives up an attempt that waits for the user (Escape on a password
    /// prompt): refuses the question, and the attempt ends as `.idle`.
    pub fn cancelConnect(self: *Connection) void {
        if (self.busy.load(.acquire)) {
            self.closing.store(true, .release);
            self.setStatus("Canceled connecting to {s}", .{self.host});
        }
        self.withdrawPrompt();
    }

    /// Refuses the open question, if any (its attempt ended or was stopped).
    fn withdrawPrompt(self: *Connection) void {
        const l = self.listener orelse return;
        if (l.withdraw()) |id| self.post(.{ .conn = self, .kind = .prompt_closed, .id = id });
    }

    // Listener callbacks (its thread).

    fn askUser(ctx: *anyopaque, q: askpass.Question) bool {
        const self = of(ctx);
        if (self.closing.load(.acquire) or !self.posting.load(.acquire) or self.sink == null) return false;
        const text = self.gpa.dupe(u8, q.text) catch return false;
        self.post(.{ .conn = self, .kind = .prompt, .path = text, .id = q.id, .prompt_kind = q.kind });
        return true;
    }

    fn showNotice(ctx: *anyopaque, text: []const u8) void {
        of(ctx).setStatus("{s}", .{text});
    }

    fn questionGone(ctx: *anyopaque, id: u32) void {
        const self = of(ctx);
        self.post(.{ .conn = self, .kind = .prompt_closed, .id = id });
    }

    fn connectInner(self: *Connection) !void {
        self.closing.store(false, .release);
        self.state.store(.connecting, .release);
        // Whatever ssh still asks is moot once the attempt is over.
        defer self.withdrawPrompt();
        self.connectChannel() catch |err| {
            self.closeChannel();
            if (self.closing.load(.acquire)) {
                self.setState(.idle);
                return err;
            }
            var tail: [512]u8 = undefined;
            const why = self.stderrLine(&tail);
            if (why.len > 0) self.setStatus("{s}", .{why}) else self.setStatus("Cannot connect to {s}: {t}", .{ self.host, err });
            self.setState(.failed);
            return err;
        };
        if (self.closing.load(.acquire)) {
            // Canceled just as it came up.
            self.closeChannel();
            self.setState(.idle);
            return error.Canceled;
        }
        self.connects += 1;
        self.setStatus("Connected to {s}", .{self.host});
        self.state.store(.connected, .seq_cst);
        self.postState();
        // A drop the main thread took before `.connected` was ignored there:
        // post it again. (seq_cst here, in the reader and in the handler: if
        // the handler missed `.connected`, this sees the channel down.)
        if (!self.up.load(.seq_cst)) self.postDropped();
    }

    fn connectChannel(self: *Connection) !void {
        self.setStatus("Connecting to {s}…", .{self.host});
        try self.startServer();
        // Output after the last line read, kept for the second read.
        var line: std.ArrayList(u8) = .empty;
        defer line.deinit(self.gpa);
        switch (try self.readBanner(&line)) {
            .ready => {},
            // The launch script waits for the build in the same session, so
            // a first deploy runs ssh once (and asks for a password once).
            .missing => |machine| {
                try self.upload(machine.slice());
                if (try self.readBanner(&line) == .missing) return error.ServerDidNotStart;
            },
        }
        self.up.store(true, .release);
        self.reader_done.store(false, .release);
        self.reader = try std.Thread.spawn(.{}, readerLoop, .{self});

        var resp = try self.call(self.gpa, "init", .{}, "", .{ .timeout_ms = 20_000 });
        defer resp.deinit();
        const info = resp.result();
        // The main thread reads `home` at any time, reconnecting included: it
        // is set once, before the first `.connected` (a host's home stays).
        if (self.home.len == 0) self.home = try self.gpa.dupe(u8, protocol.getStr(info, "home") orelse "/");
        self.remote_pid.store(protocol.getInt(info, "pid") orelse 0, .release);
        const a = protocol.getStr(info, "arch") orelse "";
        self.arch_len = @min(a.len, self.arch_buf.len);
        @memcpy(self.arch_buf[0..self.arch_len], a[0..self.arch_len]);
        if ((protocol.getInt(info, "protocol") orelse 0) != protocol.protocol_version) return error.ProtocolMismatch;
        self.resendWatches();
    }

    const Machine = struct {
        buf: [32]u8 = undefined,
        len: usize = 0,

        fn init(name: []const u8) Machine {
            var m: Machine = .{};
            const trimmed = std.mem.trim(u8, name, " ");
            m.len = @min(trimmed.len, m.buf.len);
            @memcpy(m.buf[0..m.len], trimmed[0..m.len]);
            return m;
        }

        fn slice(self: *const Machine) []const u8 {
            return self.buf[0..self.len];
        }
    };

    const Started = union(enum) { ready, missing: Machine };

    /// Spawns the transport (for ssh, with the launch script).
    fn startServer(self: *Connection) !void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var argv: std.ArrayList([]const u8) = .empty;
        var env: ?std.process.Environ.Map = null;
        switch (self.options.transport) {
            .subprocess => |exe| try argv.appendSlice(alloc, &.{ exe, "--stdio" }),
            .ssh => {
                var tags: std.ArrayList(ssh.ArchTag) = .empty;
                for (self.options.binaries) |b| {
                    const tag = self.binaryTag(alloc, b.path) catch continue;
                    try tags.append(alloc, .{ .arch = b.arch, .tag = tag });
                }
                var dest_buf: [300]u8 = undefined;
                env = try self.sshArgs(alloc, &argv);
                try argv.append(alloc, path_mod.sshDestination(&dest_buf, self.host));
                try argv.append(alloc, try ssh.launchCommand(alloc, tags.items));
            },
        }
        try self.spawnChild(argv.items, if (env) |*e| e else null);
    }

    /// Reads up to the server's banner, or the launch script's report that
    /// the build is missing. `line` holds output not consumed yet.
    fn readBanner(self: *Connection, line: *std.ArrayList(u8)) !Started {
        // Skip whatever the login shell prints; frames follow the banner line.
        var deadline = std.Io.Timestamp.now(self.io, .awake).addDuration(.fromSeconds(60));
        var buf: [4096]u8 = undefined;
        while (true) {
            if (self.closing.load(.acquire)) return error.Canceled;
            // The user may take their time over a password.
            if (self.listener) |l| if (l.asking()) {
                deadline = std.Io.Timestamp.now(self.io, .awake).addDuration(.fromSeconds(60));
            };
            const left = std.Io.Timestamp.now(self.io, .awake).durationTo(deadline).toMilliseconds();
            if (left <= 0) return error.Timeout;
            if (!protocol.waitReadable(self.from_server, @intCast(@min(left, 100)))) continue;
            const n = try protocol.readSome(self.from_server, &buf);
            if (n == 0) return error.ConnectionClosed;
            try line.appendSlice(self.gpa, buf[0..n]);
            while (std.mem.indexOfScalar(u8, line.items, '\n')) |nl| {
                const text = std.mem.trim(u8, line.items[0..nl], " \r");
                const started: ?Started = if (std.mem.startsWith(u8, text, protocol.banner_magic))
                    .ready
                else if (std.mem.startsWith(u8, text, protocol.missing_magic))
                    .{ .missing = .init(text[protocol.missing_magic.len..]) }
                else
                    null;
                const rest = line.items[nl + 1 ..];
                std.mem.copyForwards(u8, line.items[0..rest.len], rest);
                line.shrinkRetainingCapacity(rest.len);
                const s = started orelse continue;
                if (s == .ready) {
                    self.leftover.clearRetainingCapacity();
                    try self.leftover.appendSlice(self.gpa, line.items);
                }
                return s;
            }
        }
    }

    /// The ssh program and options, plus the environment that sends ssh's
    /// questions to the editor when this attempt can ask (else BatchMode).
    fn sshArgs(self: *Connection, alloc: Allocator, argv: *std.ArrayList([]const u8)) !?std.process.Environ.Map {
        try argv.append(alloc, self.options.ssh_program);
        try argv.appendSlice(alloc, &ssh.base_args);
        if (self.interactive) if (self.listener) |l| if (self.options.askpass) |program| {
            return try askpass.sshEnviron(alloc, self.io, program, l.path());
        };
        try argv.appendSlice(alloc, &ssh.batch_args);
        return null;
    }

    fn binaryTag(self: *Connection, alloc: Allocator, path: []const u8) ![]const u8 {
        const data = try Io.Dir.cwd().readFileAlloc(self.io, path, self.gpa, .limited(256 << 20));
        defer self.gpa.free(data);
        var buf: [24]u8 = undefined;
        return alloc.dupe(u8, ssh.versionTag(&buf, data));
    }

    /// Sends the server build for `machine` (as `uname -m` printed it) to the
    /// launch script waiting on the channel, which installs and starts it.
    fn upload(self: *Connection, machine: []const u8) !void {
        const arch_name = protocol.normalizeArch(machine) orelse {
            self.setStatus("{s}: unsupported architecture {s}", .{ self.host, machine });
            return error.UnsupportedArchitecture;
        };
        const bin = for (self.options.binaries) |b| {
            if (std.mem.eql(u8, b.arch, arch_name)) break b;
        } else {
            self.setStatus("No jiffy-edit-server build for {s} (run `zig build server`)", .{arch_name});
            return error.NoServerBinary;
        };
        const data = try Io.Dir.cwd().readFileAlloc(self.io, bin.path, self.gpa, .limited(256 << 20));
        defer self.gpa.free(data);
        var tag_buf: [24]u8 = undefined;
        var header_buf: [64]u8 = undefined;
        const header = try ssh.installHeader(&header_buf, ssh.versionTag(&tag_buf, data), data.len);
        protocol.writeAll(self.to_server, header) catch return error.UploadFailed;

        const chunk = 64 * 1024;
        var off: usize = 0;
        var last_pct: usize = 101;
        while (off < data.len) {
            if (self.closing.load(.acquire)) return error.Canceled;
            const end = @min(data.len, off + chunk);
            protocol.writeAll(self.to_server, data[off..end]) catch return error.UploadFailed;
            off = end;
            const pct = off * 100 / data.len;
            if (pct / 5 != last_pct / 5) {
                last_pct = pct;
                self.setStatus("Uploading jiffy-edit-server ({s}) to {s}… {d}%", .{ arch_name, self.host, pct });
            }
        }
        self.setStatus("Starting jiffy-edit-server on {s}…", .{self.host});
    }

    fn spawnChild(self: *Connection, argv: []const []const u8, env: ?*const std.process.Environ.Map) !void {
        self.stderr_len.store(0, .release);
        var child = try std.process.spawn(self.io, .{ .argv = argv, .environ_map = env, .stdin = .pipe, .stdout = .pipe, .stderr = .pipe });
        // We own the pipes; `wait` must not close them under the reader.
        self.to_server = child.stdin.?.handle;
        self.from_server = child.stdout.?.handle;
        self.child_stderr = child.stderr.?.handle;
        child.stdin = null;
        child.stdout = null;
        child.stderr = null;
        self.child_pid = child.id.?;
        self.child = child;
        self.stderr_reader = std.Thread.spawn(.{}, stderrLoop, .{self}) catch null;
    }

    fn stderrLoop(self: *Connection) void {
        var buf: [512]u8 = undefined;
        while (true) {
            const n = protocol.readSome(self.child_stderr, &buf) catch break;
            if (n == 0) break;
            // Keep the last bytes (ssh's final message is what we want).
            const old = self.stderr_len.load(.acquire);
            const keep = @min(old, self.stderr_tail.len - n);
            std.mem.copyForwards(u8, self.stderr_tail[0..keep], self.stderr_tail[old - keep .. old]);
            @memcpy(self.stderr_tail[keep..][0..n], buf[0..n]);
            self.stderr_len.store(keep + n, .release);
        }
    }

    /// The last non-empty line ssh (or the server) wrote to stderr.
    fn stderrLine(self: *Connection, out: []u8) []const u8 {
        const n = self.stderr_len.load(.acquire);
        const text = std.mem.trimEnd(u8, self.stderr_tail[0..n], " \r\n");
        const start = if (std.mem.lastIndexOfScalar(u8, text, '\n')) |i| i + 1 else 0;
        const line = std.mem.trim(u8, text[start..], " \r");
        const len = @min(line.len, out.len);
        @memcpy(out[0..len], line[0..len]);
        return out[0..len];
    }

    /// Stops the channel's process and threads. Not concurrent with itself:
    /// called by the connecting thread, or on the main thread after a drop.
    fn closeChannel(self: *Connection) void {
        self.up.store(false, .release);
        {
            self.write_lock.lockUncancelable(self.io);
            defer self.write_lock.unlock(self.io);
            if (self.to_server >= 0) protocol.closeFd(self.to_server);
            self.to_server = -1;
        }
        // EOF lets the server stop its processes and exit; give it a moment.
        var waited: u32 = 0;
        while (self.reader != null and !self.reader_done.load(.acquire) and waited < 50) : (waited += 1) {
            Io.sleep(self.io, .fromMilliseconds(10), .awake) catch {};
        }
        if (self.child != null) _ = linux.kill(self.child_pid, linux.SIG.TERM);
        if (self.reader) |t| t.join();
        self.reader = null;
        if (self.stderr_reader) |t| t.join();
        self.stderr_reader = null;
        if (self.child) |*child| _ = child.wait(self.io) catch {};
        self.child = null;
        if (self.from_server >= 0) protocol.closeFd(self.from_server);
        self.from_server = -1;
        if (self.child_stderr >= 0) protocol.closeFd(self.child_stderr);
        self.child_stderr = -1;
        self.failAll();
    }

    /// Closes the connection on purpose (Remote: Close Connection).
    pub fn disconnect(self: *Connection) void {
        self.closing.store(true, .release);
        // An open question would keep ssh, and so the attempt, waiting.
        self.withdrawPrompt();
        // A connect attempt notices `closing` within ~100 ms.
        while (self.busy.load(.acquire)) std.Thread.yield() catch {};
        self.closeChannel();
        self.state.store(.idle, .release);
        self.setStatus("Disconnected from {s}", .{self.host});
    }

    /// Fails waiting calls and ends remote processes' output (EOF).
    fn failAll(self: *Connection) void {
        self.lock.lockUncancelable(self.io);
        defer self.lock.unlock(self.io);
        var it = self.pending.valueIterator();
        while (it.next()) |w| {
            w.*.failed = true;
            w.*.event.set(self.io);
        }
        self.pending.clearRetainingCapacity();
        var pit = self.procs.valueIterator();
        while (pit.next()) |p| p.*.endOutput(false);
    }

    // ---- Requests -------------------------------------------------------------

    fn sendFrame(self: *Connection, json: []const u8, payload: []const u8) !void {
        self.write_lock.lockUncancelable(self.io);
        defer self.write_lock.unlock(self.io);
        if (self.to_server < 0) return error.NotConnected;
        protocol.writeFrame(self.to_server, json, payload) catch return error.NotConnected;
    }

    /// Fire and forget (process stdin, cancel, kill).
    pub fn notify(self: *Connection, method: []const u8, params: anytype, payload: []const u8) void {
        if (!self.up.load(.acquire)) return;
        const json = std.json.Stringify.valueAlloc(self.gpa, .{ .method = method, .params = params }, .{}) catch return;
        defer self.gpa.free(json);
        self.sendFrame(json, payload) catch {};
    }

    /// Sends a request and waits for its answer (any thread). The answer's
    /// payload is allocated with `alloc`.
    pub fn call(self: *Connection, alloc: Allocator, method: []const u8, params: anytype, payload: []const u8, opts: CallOptions) !Response {
        if (!self.up.load(.acquire)) return error.NotConnected;
        const id = self.next_id.fetchAdd(1, .monotonic);
        var waiter: Waiter = .{ .alloc = alloc };
        {
            self.lock.lockUncancelable(self.io);
            defer self.lock.unlock(self.io);
            try self.pending.put(self.gpa, id, &waiter);
        }
        const json = std.json.Stringify.valueAlloc(self.gpa, .{ .id = id, .method = method, .params = params }, .{}) catch |err| {
            self.forget(id, &waiter);
            return err;
        };
        defer self.gpa.free(json);
        self.sendFrame(json, payload) catch |err| {
            self.forget(id, &waiter);
            return err;
        };

        const timeout_ms = opts.timeout_ms orelse self.options.timeout_ms;
        const deadline = Io.Timestamp.now(self.io, .awake).addDuration(.fromMilliseconds(timeout_ms));
        var cancel_sent = false;
        while (!waiter.event.isSet()) {
            waiter.event.waitTimeout(self.io, .{ .duration = .{ .raw = .fromMilliseconds(100), .clock = .awake } }) catch {};
            if (waiter.event.isSet()) break;
            if (opts.cancel) |c| if (!cancel_sent and c.load(.acquire)) {
                cancel_sent = true;
                self.notify("cancel", .{ .id = id }, "");
            };
            if (Io.Timestamp.now(self.io, .awake).nanoseconds > deadline.nanoseconds) {
                self.forget(id, &waiter);
                return error.Timeout;
            }
        }
        if (waiter.failed) return error.NotConnected;
        defer self.gpa.free(waiter.json);
        errdefer alloc.free(waiter.payload);
        const parsed = std.json.parseFromSlice(Value, self.gpa, waiter.json, .{ .allocate = .alloc_always }) catch return error.BadMessage;
        if (protocol.getStr(parsed.value, "error")) |name| {
            const err = protocol.errorFromName(name);
            parsed.deinit();
            return err;
        }
        return .{ .parsed = parsed, .payload = waiter.payload, .alloc = alloc };
    }

    /// Removes a waiter nobody will answer; if the reader already took it,
    /// waits for it to finish filling it in.
    fn forget(self: *Connection, id: u32, waiter: *Waiter) void {
        self.lock.lockUncancelable(self.io);
        const removed = self.pending.remove(id);
        self.lock.unlock(self.io);
        if (removed) return;
        waiter.event.waitUncancelable(self.io);
        if (!waiter.failed) {
            self.gpa.free(waiter.json);
            waiter.alloc.free(waiter.payload);
        }
    }

    // ---- Reader thread ------------------------------------------------------------

    fn readerLoop(self: *Connection) void {
        var frames: protocol.FrameReader = .{};
        defer frames.deinit(self.gpa);
        var buf: [64 * 1024]u8 = undefined;
        var ok = true;
        frames.feed(self.gpa, self.leftover.items) catch {
            ok = false;
        };
        outer: while (ok) {
            while (frames.next() catch break :outer) |frame| self.dispatch(frame);
            const n = protocol.readSome(self.from_server, &buf) catch break;
            if (n == 0) break;
            frames.feed(self.gpa, buf[0..n]) catch break;
        }
        // The channel is gone: fail calls now, clean up on the main thread.
        self.up.store(false, .seq_cst);
        self.reader_done.store(true, .release);
        self.failAll();
        if (!self.closing.load(.acquire)) self.postDropped();
    }

    fn dispatch(self: *Connection, frame: protocol.Frame) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const msg = std.json.parseFromSliceLeaky(Value, alloc, frame.json, .{}) catch {
            log.warn("{s}: bad frame from server", .{self.host});
            return;
        };
        if (protocol.getStr(msg, "method")) |method| return self.handleNotification(alloc, method, protocol.get(msg, "params"), frame.payload);
        const id = protocol.getInt(msg, "id") orelse return;
        if (id < 0 or id > std.math.maxInt(u32)) return;
        self.lock.lockUncancelable(self.io);
        const kv = self.pending.fetchRemove(@intCast(id));
        self.lock.unlock(self.io);
        const w = (kv orelse return).value;
        // Copy out of the frame buffer; the caller frees.
        w.json = self.gpa.dupe(u8, frame.json) catch {
            w.failed = true;
            w.event.set(self.io);
            return;
        };
        w.payload = w.alloc.dupe(u8, frame.payload) catch {
            self.gpa.free(w.json);
            w.failed = true;
            w.event.set(self.io);
            return;
        };
        w.event.set(self.io);
    }

    fn handleNotification(self: *Connection, alloc: Allocator, method: []const u8, params: ?Value, payload: []const u8) void {
        if (std.mem.eql(u8, method, "proc.out") or std.mem.eql(u8, method, "proc.exit")) {
            const handle = protocol.getInt(params, "handle") orelse return;
            self.lock.lockUncancelable(self.io);
            const p = self.procs.get(@intCast(@max(0, handle))) orelse {
                self.lock.unlock(self.io);
                return;
            };
            // Take the process's output lock before letting go of the table,
            // so `destroy` can't free it meanwhile.
            p.out_lock.lockUncancelable(self.io);
            self.lock.unlock(self.io);
            defer p.out_lock.unlock(self.io);
            if (method.len == "proc.out".len) {
                // Queued, never written here: whoever reads the pipe (the UI
                // thread, for terminals) may be waiting on a call behind us.
                if (!p.out_done) p.out_buf.appendSlice(self.gpa, payload) catch {};
            } else {
                // The code first, so a reader seeing EOF can ask for it.
                p.exit_code.store(protocol.getInt(params, "code") orelse -1, .release);
                p.out_done = true;
            }
            p.out_ready.signal(self.io);
        } else if (std.mem.eql(u8, method, "changed")) {
            const changes = protocol.get(params, "changes") orelse return;
            if (changes != .array) return;
            for (changes.array.items) |ch| {
                const path = protocol.getStr(ch, "path") orelse continue;
                const full = std.fmt.allocPrint(alloc, "{s}{s}", .{ self.prefix, path }) catch return;
                self.postChanged(full, protocol.getBool(ch, "deleted"));
            }
        }
    }

    // ---- Events for the main thread ----------------------------------------------

    const Posted = struct {
        conn: *Connection,
        kind: enum { state, dropped, changed, prompt, prompt_closed },
        /// Owned: the changed path, or the question's text.
        path: []u8 = &.{},
        deleted: bool = false,
        id: u32 = 0,
        prompt_kind: PromptKind = .secret,
    };

    fn post(self: *Connection, p: Posted) void {
        if (!self.posting.load(.acquire)) return self.gpa.free(p.path);
        const item = self.gpa.create(Posted) catch return self.gpa.free(p.path);
        item.* = p;
        self.executor.post(.{ .ctx = item, .run = runPosted });
    }

    fn postState(self: *Connection) void {
        self.post(.{ .conn = self, .kind = .state });
    }

    fn postDropped(self: *Connection) void {
        self.post(.{ .conn = self, .kind = .dropped });
    }

    fn postChanged(self: *Connection, path: []const u8, deleted: bool) void {
        const owned = self.gpa.dupe(u8, path) catch return;
        self.post(.{ .conn = self, .kind = .changed, .path = owned, .deleted = deleted });
    }

    fn runPosted(ctx: *anyopaque) void {
        const p: *Posted = @ptrCast(@alignCast(ctx));
        const self = p.conn;
        defer {
            self.gpa.free(p.path);
            self.gpa.destroy(p);
        }
        switch (p.kind) {
            .dropped => {
                // Main thread: the reader has exited (or is exiting). Drops of
                // a channel that never got up are the connect attempt's
                // business, and with the channel up the drop was an older one's.
                if (self.closing.load(.acquire) or self.state.load(.seq_cst) != .connected or self.up.load(.acquire)) return;
                // Connected, so an attempt still busy is only finishing up
                // (nothing it does waits for this thread): wait for it
                // rather than lose the drop.
                while (self.busy.load(.acquire)) std.Thread.yield() catch {};
                self.closeChannel();
                self.setStatus("Connection to {s} lost", .{self.host});
                self.state.store(.lost, .release);
                if (self.sink) |s| s.onEvent(s.ctx, self, .state);
            },
            .state => if (self.sink) |s| s.onEvent(s.ctx, self, .state),
            .changed => if (self.sink) |s| s.onEvent(s.ctx, self, .{ .changed = .{ .path = p.path, .deleted = p.deleted } }),
            .prompt => if (self.sink) |s|
                s.onEvent(s.ctx, self, .{ .prompt = .{ .id = p.id, .kind = p.prompt_kind, .text = p.path } })
            else
                self.answerPrompt(p.id, null),
            .prompt_closed => if (self.sink) |s| s.onEvent(s.ctx, self, .{ .prompt_closed = p.id }),
        }
    }

    // ---- Watches ----------------------------------------------------------------------

    /// Replaces the watched directories (remote paths); kept for reconnects.
    pub fn watch(self: *Connection, dirs: []const []const u8) void {
        self.watch_lock.lock();
        const same = dirs.len == self.watch_dirs.items.len and for (dirs, self.watch_dirs.items) |a, b| {
            if (!std.mem.eql(u8, a, b)) break false;
        } else true;
        if (!same) {
            for (self.watch_dirs.items) |d| self.gpa.free(d);
            self.watch_dirs.clearRetainingCapacity();
            for (dirs) |d| {
                const owned = self.gpa.dupe(u8, d) catch continue;
                self.watch_dirs.append(self.gpa, owned) catch self.gpa.free(owned);
            }
        }
        self.watch_lock.unlock();
        if (!same) self.resendWatches();
    }

    fn resendWatches(self: *Connection) void {
        self.watch_lock.lock();
        const json = std.json.Stringify.valueAlloc(self.gpa, .{ .method = "watch", .params = .{ .dirs = self.watch_dirs.items } }, .{});
        self.watch_lock.unlock();
        const j = json catch return;
        defer self.gpa.free(j);
        if (self.up.load(.acquire)) self.sendFrame(j, "") catch {};
    }

    // ---- Backend --------------------------------------------------------------------

    const backend_vtable: vfs.Backend.VTable = .{
        .readFile = beReadFile,
        .writeFile = beWriteFile,
        .stat = beStat,
        .list = beList,
        .makeDir = beMakeDir,
        .rename = beRename,
        .delete = beDelete,
        .scan = beScan,
        .search = beSearch,
        .spawn = beSpawn,
        .spawnPty = beSpawnPty,
        .watch = beWatch,
        .serverPid = beServerPid,
    };

    fn of(ctx: *anyopaque) *Connection {
        return @ptrCast(@alignCast(ctx));
    }

    fn beReadFile(ctx: *anyopaque, gpa: Allocator, path: []const u8, limit: usize) anyerror![]u8 {
        var resp = try of(ctx).call(gpa, "read", .{ .path = path, .limit = limit }, "", .{});
        const data = resp.payload;
        resp.payload = &.{};
        resp.deinit();
        return data;
    }

    fn beWriteFile(ctx: *anyopaque, path: []const u8, data: []const u8) anyerror!void {
        const self = of(ctx);
        var resp = try self.call(self.gpa, "write", .{ .path = path }, data, .{});
        resp.deinit();
    }

    fn kindFrom(name: ?[]const u8) vfs.Kind {
        const n = name orelse return .other;
        return std.meta.stringToEnum(vfs.Kind, n) orelse .other;
    }

    fn beStat(ctx: *anyopaque, path: []const u8) anyerror!vfs.Stat {
        const self = of(ctx);
        var resp = try self.call(self.gpa, "stat", .{ .path = path }, "", .{});
        defer resp.deinit();
        const r = resp.result();
        return .{
            .kind = kindFrom(protocol.getStr(r, "kind")),
            .size = @intCast(@max(0, protocol.getInt(r, "size") orelse 0)),
            .mtime_ns = protocol.getInt(r, "mtime") orelse 0,
        };
    }

    fn beList(ctx: *anyopaque, alloc: Allocator, path: []const u8) anyerror![]vfs.DirEntry {
        const self = of(ctx);
        var resp = try self.call(self.gpa, "list", .{ .path = path }, "", .{});
        defer resp.deinit();
        const entries = protocol.get(resp.result(), "entries") orelse return &.{};
        if (entries != .array) return &.{};
        var out: std.ArrayList(vfs.DirEntry) = .empty;
        for (entries.array.items) |e| {
            const name = protocol.getStr(e, "name") orelse continue;
            try out.append(alloc, .{ .name = try alloc.dupe(u8, name), .kind = kindFrom(protocol.getStr(e, "kind")) });
        }
        vfs.sortEntries(out.items);
        return out.items;
    }

    fn beMakeDir(ctx: *anyopaque, path: []const u8) anyerror!void {
        const self = of(ctx);
        var resp = try self.call(self.gpa, "mkdir", .{ .path = path }, "", .{});
        resp.deinit();
    }

    fn beRename(ctx: *anyopaque, from: []const u8, to: []const u8) anyerror!void {
        const self = of(ctx);
        // Both sides must be on this host.
        const to_path = if (path_mod.parse(to)) |r| r.path else to;
        var resp = try self.call(self.gpa, "rename", .{ .from = from, .to = to_path }, "", .{});
        resp.deinit();
    }

    fn beDelete(ctx: *anyopaque, path: []const u8) anyerror!void {
        const self = of(ctx);
        var resp = try self.call(self.gpa, "delete", .{ .path = path }, "", .{});
        resp.deinit();
    }

    fn beScan(ctx: *anyopaque, gpa: Allocator, root: []const u8) anyerror!project.Snapshot {
        const self = of(ctx);
        var resp = try self.call(self.gpa, "scan", .{ .root = root }, "", .{ .timeout_ms = 10 * 60_000 });
        defer resp.deinit();
        var arena = std.heap.ArenaAllocator.init(gpa);
        errdefer arena.deinit();
        const alloc = arena.allocator();
        var entries: std.ArrayList(project.Entry) = .empty;
        var files: std.ArrayList(u32) = .empty;
        var dec: protocol.Decoder = .{ .data = resp.payload };
        while (!dec.done()) {
            const e = try protocol.decodeScanEntry(&dec);
            const path = try alloc.dupe(u8, e.path);
            if (!e.dir) try files.append(alloc, @intCast(entries.items.len));
            try entries.append(alloc, .{
                .path = path,
                .name = std.fs.path.basename(path),
                .kind = if (e.dir) .dir else .file,
                .depth = e.depth,
                .ignored = e.ignored,
            });
        }
        return .{ .arena = arena, .entries = entries.items, .files = files.items, .truncated = protocol.getBool(resp.result(), "truncated") };
    }

    fn beSearch(ctx: *anyopaque, job: *search.Job) anyerror!search.Results {
        const self = of(ctx);
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        // Only what lives on this host (paths without the ssh:// prefix).
        var roots: std.ArrayList([]const u8) = .empty;
        var files: std.ArrayList([]const u8) = .empty;
        for (job.roots) |p| if (self.ownPath(p)) |r| try roots.append(alloc, r);
        for (job.files) |p| if (self.ownPath(p)) |r| try files.append(alloc, r);
        var enc: protocol.Encoder = .{};
        defer enc.deinit(self.gpa);
        for (job.overlays) |o| if (self.ownPath(o.path)) |r| {
            try enc.bytes(self.gpa, r);
            try enc.bytes(self.gpa, o.text);
        };
        const q = job.query;
        var resp = try self.call(self.gpa, "search", .{
            .pattern = q.pattern,
            .regex = q.regex,
            .case_sensitive = q.case_sensitive,
            .whole_word = q.whole_word,
            .context = q.context,
            .roots = roots.items,
            .files = files.items,
            .includes = job.where.includes.items,
            .excludes = job.where.excludes.items,
            .prefix = self.prefix,
        }, enc.out.items, .{ .timeout_ms = 10 * 60_000, .cancel = &job.cancelled });
        defer resp.deinit();
        const r = resp.result();
        var out = std.heap.ArenaAllocator.init(job.gpa);
        errdefer out.deinit();
        const oa = out.allocator();
        var matches: std.ArrayList([2]usize) = .empty;
        if (protocol.get(r, "matches")) |m| if (m == .array) for (m.array.items) |pair| {
            if (pair != .array or pair.array.items.len != 2) continue;
            const a = pair.array.items[0];
            const b = pair.array.items[1];
            if (a != .integer or b != .integer) continue;
            try matches.append(oa, .{ @intCast(a.integer), @intCast(b.integer) });
        };
        const text = try oa.dupe(u8, resp.payload);
        return .{
            .arena = out,
            .text = text,
            .matches = matches.items,
            .files_searched = @intCast(@max(0, protocol.getInt(r, "files_searched") orelse 0)),
            .files_matched = @intCast(@max(0, protocol.getInt(r, "files_matched") orelse 0)),
            .truncated = protocol.getBool(r, "truncated"),
            .regex_error = protocol.getBool(r, "regex_error"),
        };
    }

    /// The machine path of `p` if it is on this host.
    fn ownPath(self: *const Connection, p: []const u8) ?[]const u8 {
        const r = path_mod.parse(p) orelse return null;
        return if (std.mem.eql(u8, r.host, self.host)) r.path else null;
    }

    fn beWatch(ctx: *anyopaque, dirs: []const []const u8) void {
        of(ctx).watch(dirs);
    }

    fn beServerPid(ctx: *anyopaque) i64 {
        return of(ctx).remote_pid.load(.acquire);
    }

    fn beSpawn(ctx: *anyopaque, argv: []const []const u8, cwd: []const u8) anyerror!*vfs.Process {
        const self = of(ctx);
        const p = try Proc.create(self);
        errdefer p.destroyNow();
        var resp = try self.call(self.gpa, "spawn", .{ .handle = p.handle, .argv = argv, .cwd = cwd }, "", .{ .timeout_ms = 30_000 });
        resp.deinit();
        p.pump = try std.Thread.spawn(.{}, Proc.pumpLoop, .{p});
        return &p.public;
    }

    fn beSpawnPty(ctx: *anyopaque, argv: []const []const u8, cwd: []const u8, rows: u16, cols: u16) anyerror!*vfs.Process {
        const self = of(ctx);
        const p = try Proc.create(self);
        errdefer p.destroyNow();
        var resp = try self.call(self.gpa, "spawn", .{ .handle = p.handle, .argv = argv, .cwd = cwd, .pty = .{ .rows = rows, .cols = cols } }, "", .{ .timeout_ms = 30_000 });
        resp.deinit();
        p.pump = try std.Thread.spawn(.{}, Proc.pumpLoop, .{p});
        return &p.public;
    }

    fn removeProc(self: *Connection, handle: u32) void {
        self.lock.lockUncancelable(self.io);
        defer self.lock.unlock(self.io);
        _ = self.procs.remove(handle);
    }
};

/// A remote process behind two local pipes. The owner writes `public.stdin_fd`
/// (a pump thread forwards it as "stdin" notifications) and reads
/// `public.stdout_fd` (the reader thread queues "proc.out" payloads, which a
/// writer thread moves into the other end).
const Proc = struct {
    conn: *Connection,
    handle: u32,
    public: vfs.Process,
    /// Read end of the stdin pipe (the pump's).
    stdin_r: i32,
    /// Write end of the stdout pipe (the writer's; it closes it).
    stdout_w: i32,
    /// Guards the output queue.
    out_lock: Io.Mutex = .init,
    out_ready: Io.Condition = .init,
    out_buf: std.ArrayList(u8) = .empty,
    /// No more output: the writer delivers `out_buf`, then closes the pipe.
    out_done: bool = false,
    pump: ?std.Thread = null,
    writer: ?std.Thread = null,
    /// From "proc.exit"; `unknown` while it runs.
    exit_code: std.atomic.Value(i64) = .init(unknown),

    const unknown = std.math.minInt(i64);
    const vtable: vfs.Process.VTable = .{ .kill = kill, .destroy = destroy, .resize = resize, .exitCode = exitCode, .currentDir = currentDir };

    fn resize(p: *vfs.Process, rows: u16, cols: u16) void {
        const self: *Proc = @fieldParentPtr("public", p);
        self.conn.notify("resize", .{ .handle = self.handle, .rows = rows, .cols = cols }, "");
    }

    fn exitCode(p: *vfs.Process) ?i64 {
        const self: *Proc = @fieldParentPtr("public", p);
        const code = self.exit_code.load(.acquire);
        return if (code == unknown) null else code;
    }

    fn currentDir(p: *vfs.Process, buf: []u8) ?[]const u8 {
        const self: *Proc = @fieldParentPtr("public", p);
        const conn = self.conn;
        var resp = conn.call(conn.gpa, "cwd", .{ .handle = self.handle }, "", .{ .timeout_ms = 5000 }) catch return null;
        defer resp.deinit();
        const dir = protocol.getStr(resp.result(), "path") orelse return null;
        return std.fmt.bufPrint(buf, "{s}{s}", .{ conn.prefix, dir }) catch null;
    }

    fn create(conn: *Connection) !*Proc {
        var in_fds: [2]i32 = undefined;
        var out_fds: [2]i32 = undefined;
        if (linux.errno(linux.pipeCloexec(&in_fds)) != .SUCCESS) return error.SystemResources;
        errdefer for (in_fds) |fd| protocol.closeFd(fd);
        if (linux.errno(linux.pipeCloexec(&out_fds)) != .SUCCESS) return error.SystemResources;
        errdefer for (out_fds) |fd| protocol.closeFd(fd);
        const self = try conn.gpa.create(Proc);
        errdefer conn.gpa.destroy(self);
        self.* = .{
            .conn = conn,
            .handle = conn.next_id.fetchAdd(1, .monotonic),
            .public = .{ .stdin_fd = in_fds[1], .stdout_fd = out_fds[0], .vtable = &vtable },
            .stdin_r = in_fds[0],
            .stdout_w = out_fds[1],
        };
        // Registered before the spawn request, so no output is missed
        // (it queues until the writer runs).
        {
            conn.lock.lockUncancelable(conn.io);
            defer conn.lock.unlock(conn.io);
            try conn.procs.put(conn.gpa, self.handle, self);
        }
        errdefer conn.removeProc(self.handle);
        self.writer = try std.Thread.spawn(.{}, writerLoop, .{self});
        return self;
    }

    /// Ends the output: after what's queued, or right away (`discard`).
    fn endOutput(self: *Proc, discard: bool) void {
        const io = self.conn.io;
        self.out_lock.lockUncancelable(io);
        defer self.out_lock.unlock(io);
        self.out_done = true;
        if (discard) self.out_buf.clearRetainingCapacity();
        self.out_ready.signal(io);
    }

    fn writerLoop(self: *Proc) void {
        const io = self.conn.io;
        var chunk: std.ArrayList(u8) = .empty;
        defer chunk.deinit(self.conn.gpa);
        var broken = false;
        while (true) {
            self.out_lock.lockUncancelable(io);
            while (self.out_buf.items.len == 0 and !self.out_done) self.out_ready.waitUncancelable(io, &self.out_lock);
            std.mem.swap(std.ArrayList(u8), &chunk, &self.out_buf);
            const done = self.out_done and self.out_buf.items.len == 0;
            self.out_lock.unlock(io);
            if (chunk.items.len > 0 and !broken) protocol.writeAll(self.stdout_w, chunk.items) catch {
                broken = true;
            };
            chunk.clearRetainingCapacity();
            if (done) break;
        }
        protocol.closeFd(self.stdout_w);
    }

    fn pumpLoop(self: *Proc) void {
        var buf: [64 * 1024]u8 = undefined;
        while (true) {
            const n = protocol.readSome(self.stdin_r, &buf) catch break;
            if (n == 0) break;
            // Keep draining even when the connection is down, so writers never block.
            self.conn.notify("stdin", .{ .handle = self.handle }, buf[0..n]);
        }
        self.conn.notify("close_stdin", .{ .handle = self.handle }, "");
    }

    fn kill(p: *vfs.Process) void {
        const self: *Proc = @fieldParentPtr("public", p);
        self.conn.notify("kill", .{ .handle = self.handle }, "");
        self.endOutput(true);
    }

    fn destroy(p: *vfs.Process) void {
        const self: *Proc = @fieldParentPtr("public", p);
        self.destroyNow();
    }

    fn destroyNow(self: *Proc) void {
        self.conn.removeProc(self.handle);
        // EOF for the pump.
        protocol.closeFd(self.public.stdin_fd);
        if (self.pump) |t| t.join();
        self.endOutput(true);
        // A writer stuck on a full pipe fails once nobody can read it.
        protocol.closeFd(self.public.stdout_fd);
        if (self.writer) |t| t.join();
        protocol.closeFd(self.stdin_r);
        self.out_buf.deinit(self.conn.gpa);
        self.conn.gpa.destroy(self);
    }
};
