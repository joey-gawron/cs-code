//! Interactive ssh authentication for Remote-SSH. ssh asks for passwords,
//! key passphrases and host key confirmations through $SSH_ASKPASS, which the
//! connection sets to the editor binary (with SSH_ASKPASS_REQUIRE=force, so
//! OpenSSH ≥ 8.4 uses it even when the editor has a terminal). That helper
//! process (`runHelper`: `cs-zig --askpass PROMPT`, or ssh running the editor
//! with CS_ZIG_ASKPASS set) hands the question to the `Listener` of the
//! connection that started ssh over a Unix socket, the editor shows it, and
//! the helper prints the answer for ssh.
//!
//! The socket sits in a fresh 0700 directory under $XDG_RUNTIME_DIR (or
//! /tmp) and only serves our own uid. Answers pass through once and are never
//! kept. Wire format: the helper sends "<SSH_ASKPASS_PROMPT>\n<prompt>" and
//! shuts down its side; the editor answers "+<answer>" or "-" (refused).

const std = @import("std");
const protocol = @import("protocol.zig");

const linux = @import("sys.zig");
const Allocator = std.mem.Allocator;
const Io = std.Io;

/// Names the listener's socket in the environment of ssh (and so of the helper).
pub const socket_env = "CS_ZIG_ASKPASS";
const max_request = 16 * 1024;
const max_answer = 4096;

pub const Kind = enum {
    /// A password, passphrase or one-time code: masked input.
    secret,
    /// "Are you sure you want to continue connecting (yes/no/[fingerprint])?":
    /// the answer is the word "yes" or "no".
    yes_no,
    /// SSH_ASKPASS_PROMPT=confirm (ControlMaster=ask, agent confirmation):
    /// only accepting or refusing counts.
    confirm,
};

/// What a question is; null for SSH_ASKPASS_PROMPT=none, a notice that needs
/// no answer ("Confirm user presence for key …").
pub fn classify(hint: []const u8, prompt: []const u8) ?Kind {
    if (std.mem.eql(u8, hint, "none")) return null;
    if (std.mem.eql(u8, hint, "confirm")) return .confirm;
    if (std.mem.indexOf(u8, prompt, "(yes/no") != null) return .yes_no;
    return .secret;
}

/// The current environment (from /proc/self/environ, as a child would
/// inherit it) plus what makes ssh ask through `program` and `socket`.
pub fn sshEnviron(alloc: Allocator, io: Io, program: []const u8, socket: []const u8) !std.process.Environ.Map {
    var map = try currentEnviron(alloc, io);
    try map.put("SSH_ASKPASS", program);
    try map.put("SSH_ASKPASS_REQUIRE", "force");
    try map.put(socket_env, socket);
    return map;
}

pub fn currentEnviron(alloc: Allocator, io: Io) !std.process.Environ.Map {
    var map: std.process.Environ.Map = .init(alloc);
    if (!linux.linux) {
        for (linux.environEntries()) |entry_z| {
            const entry = std.mem.span(entry_z.?);
            const eq = std.mem.indexOfScalar(u8, entry, '=') orelse continue;
            if (eq > 0) try map.put(entry[0..eq], entry[eq + 1 ..]);
        }
        return map;
    }
    const data = Io.Dir.cwd().readFileAlloc(io, "/proc/self/environ", alloc, .limited(4 << 20)) catch return map;
    var it = std.mem.splitScalar(u8, data, 0);
    while (it.next()) |entry| {
        const eq = std.mem.indexOfScalar(u8, entry, '=') orelse continue;
        if (eq > 0) try map.put(entry[0..eq], entry[eq + 1 ..]);
    }
    return map;
}

// ---- The helper (run by ssh) ---------------------------------------------------

/// Asks the editor listening on `socket_path` and writes the answer and a
/// newline to `out_fd`. Returns the exit status for ssh (0 = answered).
pub fn runHelper(socket_path: []const u8, hint: []const u8, prompt: []const u8, out_fd: i32) u8 {
    const fd = connectTo(socket_path) catch return 1;
    defer protocol.closeFd(fd);
    sendAll(fd, hint) catch return 1;
    sendAll(fd, "\n") catch return 1;
    sendAll(fd, prompt[0..@min(prompt.len, max_request -| (hint.len + 1))]) catch return 1;
    _ = linux.shutdown(fd, linux.SHUT.WR);
    // Blocks until the user answers (or the editor goes away: EOF).
    var buf: [max_answer + 2]u8 = undefined;
    defer std.crypto.secureZero(u8, &buf);
    var n: usize = 0;
    while (n < buf.len - 1) {
        const k = protocol.readSome(fd, buf[n .. buf.len - 1]) catch return 1;
        if (k == 0) break;
        n += k;
    }
    if (n == 0 or buf[0] != '+') return 1;
    buf[n] = '\n';
    protocol.writeAll(out_fd, buf[1 .. n + 1]) catch return 1;
    return 0;
}

// ---- The editor side ----------------------------------------------------------

pub const Question = struct {
    id: u32,
    kind: Kind,
    /// ssh's prompt, valid during the callback.
    text: []const u8,
};

/// Callbacks, on the listener's thread.
pub const Handler = struct {
    ctx: *anyopaque,
    /// Shows a question; answer it with `Listener.answer`. False refuses it.
    ask: *const fn (ctx: *anyopaque, q: Question) bool,
    /// Text to show that needs no answer.
    notice: *const fn (ctx: *anyopaque, text: []const u8) void,
    /// Question `id` went away unanswered (its helper exited).
    withdrawn: *const fn (ctx: *anyopaque, id: u32) void,
};

/// A socket that takes one question at a time; a thread serves it.
pub const Listener = struct {
    gpa: Allocator,
    io: Io,
    handler: Handler,
    dir_buf: [108]u8 = undefined,
    dir_len: usize = 0,
    path_buf: [108]u8 = undefined,
    path_len: usize = 0,
    fd: i32 = -1,
    thread: ?std.Thread = null,
    stop: std.atomic.Value(bool) = .init(false),
    /// Guards the open question.
    lock: Io.Mutex = .init,
    /// The helper waiting for an answer, or -1.
    waiting_fd: i32 = -1,
    waiting_id: u32 = 0,
    next_id: u32 = 1,
    answered: Io.Event = .unset,
    asking_flag: std.atomic.Value(bool) = .init(false),

    /// `runtime_dir` is $XDG_RUNTIME_DIR, if set.
    pub fn create(gpa: Allocator, io: Io, runtime_dir: ?[]const u8, handler: Handler) !*Listener {
        const self = try gpa.create(Listener);
        errdefer gpa.destroy(self);
        self.* = .{ .gpa = gpa, .io = io, .handler = handler };
        const base = if (runtime_dir) |d| (if (d.len > 0 and d[0] == '/' and d.len < 60) d else "/tmp") else "/tmp";
        try self.makeDir(base);
        errdefer _ = linux.rmdir(self.dirZ().ptr);
        const sock = try std.fmt.bufPrintSentinel(&self.path_buf, "{s}/sock", .{self.dirZ()}, 0);
        self.path_len = sock.len;
        self.fd = try bindSocket(sock);
        errdefer {
            protocol.closeFd(self.fd);
            _ = linux.unlink(self.pathZ());
        }
        self.thread = try std.Thread.spawn(.{}, run, .{self});
        return self;
    }

    pub fn destroy(self: *Listener) void {
        self.stop.store(true, .release);
        _ = self.withdraw();
        // Wakes the thread from accept().
        if (connectTo(self.path())) |fd| protocol.closeFd(fd) else |_| {}
        if (self.thread) |t| t.join();
        protocol.closeFd(self.fd);
        _ = linux.unlink(self.pathZ());
        _ = linux.rmdir(self.dirZ().ptr);
        self.gpa.destroy(self);
    }

    /// The socket, for CS_ZIG_ASKPASS.
    pub fn path(self: *const Listener) []const u8 {
        return self.path_buf[0..self.path_len];
    }

    fn pathZ(self: *Listener) [*:0]const u8 {
        return self.path_buf[0..self.path_len :0].ptr;
    }

    fn dirZ(self: *Listener) [:0]const u8 {
        return self.dir_buf[0..self.dir_len :0];
    }

    /// A question is waiting for the user.
    pub fn asking(self: *const Listener) bool {
        return self.asking_flag.load(.acquire);
    }

    /// Answers question `id` (null refuses it). False if it isn't open any more.
    pub fn answer(self: *Listener, id: u32, reply: ?[]const u8) bool {
        self.lock.lockUncancelable(self.io);
        defer self.lock.unlock(self.io);
        if (self.waiting_fd < 0 or self.waiting_id != id) return false;
        self.finish(reply);
        return true;
    }

    /// Refuses the open question, if any, and returns its id.
    pub fn withdraw(self: *Listener) ?u32 {
        self.lock.lockUncancelable(self.io);
        defer self.lock.unlock(self.io);
        if (self.waiting_fd < 0) return null;
        self.finish(null);
        return self.waiting_id;
    }

    /// Under `lock`.
    fn finish(self: *Listener, reply: ?[]const u8) void {
        sendReply(self.waiting_fd, reply);
        protocol.closeFd(self.waiting_fd);
        self.waiting_fd = -1;
        self.asking_flag.store(false, .release);
        self.answered.set(self.io);
    }

    fn makeDir(self: *Listener, base: []const u8) !void {
        var attempt: u32 = 0;
        while (attempt < 8) : (attempt += 1) {
            var random: [6]u8 = undefined;
            linux.fillRandom(&random);
            const dir = try std.fmt.bufPrintSentinel(&self.dir_buf, "{s}/cs-zig-askpass-{d}-{s}", .{ base, linux.getpid(), &std.fmt.bytesToHex(random, .lower) }, 0);
            switch (linux.errno(linux.mkdir(dir, 0o700))) {
                .SUCCESS => {
                    self.dir_len = dir.len;
                    return;
                },
                .EXIST => continue,
                else => return error.CannotCreateSocketDir,
            }
        }
        return error.CannotCreateSocketDir;
    }

    fn run(self: *Listener) void {
        while (!self.stop.load(.acquire)) {
            const rc = linux.accept4(self.fd, linux.SOCK.CLOEXEC);
            if (linux.errno(rc) != .SUCCESS) {
                if (self.stop.load(.acquire)) break;
                Io.sleep(self.io, .fromMilliseconds(100), .awake) catch {};
                continue;
            }
            const fd: i32 = @intCast(rc);
            if (self.stop.load(.acquire)) {
                protocol.closeFd(fd);
                break;
            }
            self.serve(fd);
        }
    }

    /// One helper: read its question, wait for the answer.
    fn serve(self: *Listener, fd: i32) void {
        var buf: [max_request]u8 = undefined;
        const request = if (sameUser(fd)) readRequest(fd, &buf) else null;
        const req = request orelse return protocol.closeFd(fd);
        const nl = std.mem.indexOfScalar(u8, req, '\n') orelse return protocol.closeFd(fd);
        const hint = req[0..nl];
        const text = std.mem.trimEnd(u8, req[nl + 1 ..], " \r\n");
        const kind = classify(hint, text) orelse {
            self.handler.notice(self.handler.ctx, text);
            sendReply(fd, "");
            return protocol.closeFd(fd);
        };

        self.lock.lockUncancelable(self.io);
        const id = self.next_id;
        self.next_id +%= 1;
        self.waiting_fd = fd;
        self.waiting_id = id;
        self.answered.reset();
        self.asking_flag.store(true, .release);
        self.lock.unlock(self.io);

        if (!self.handler.ask(self.handler.ctx, .{ .id = id, .kind = kind, .text = text })) _ = self.answer(id, null);
        while (!self.answered.isSet()) {
            if (self.stop.load(.acquire)) {
                _ = self.withdraw();
                break;
            }
            if (hungUp(fd)) {
                if (self.withdraw()) |gone| self.handler.withdrawn(self.handler.ctx, gone);
                break;
            }
            self.answered.waitTimeout(self.io, .{ .duration = .{ .raw = .fromMilliseconds(200), .clock = .awake } }) catch {};
        }
    }
};

fn sendAll(fd: i32, bytes: []const u8) !void {
    var off: usize = 0;
    while (off < bytes.len) {
        const rc = linux.send(fd, bytes[off..].ptr, bytes.len - off, linux.MSG.NOSIGNAL);
        switch (linux.errno(rc)) {
            .SUCCESS => off += rc,
            .INTR, .AGAIN => continue,
            else => return error.WriteFailed,
        }
    }
}

fn sendReply(fd: i32, reply: ?[]const u8) void {
    var buf: [max_answer + 1]u8 = undefined;
    defer std.crypto.secureZero(u8, &buf);
    const r = reply orelse return sendAll(fd, "-") catch {};
    if (r.len > max_answer or std.mem.indexOfAny(u8, r, "\r\n") != null) return sendAll(fd, "-") catch {};
    buf[0] = '+';
    @memcpy(buf[1..][0..r.len], r);
    sendAll(fd, buf[0 .. r.len + 1]) catch {};
}

/// The whole request (the helper shuts down its side), or null.
fn readRequest(fd: i32, buf: []u8) ?[]const u8 {
    var n: usize = 0;
    while (n < buf.len) {
        if (!protocol.waitReadable(fd, 5000)) return null;
        const k = protocol.readSome(fd, buf[n..]) catch return null;
        if (k == 0) return buf[0..n];
        n += k;
    }
    return null;
}

/// The helper exited (closed its end entirely).
fn hungUp(fd: i32) bool {
    var fds = [_]linux.pollfd{.{ .fd = fd, .events = 0, .revents = 0 }};
    const rc = linux.poll(&fds, 1, 0);
    return linux.errno(rc) == .SUCCESS and rc > 0 and (fds[0].revents & (linux.POLL.HUP | linux.POLL.ERR)) != 0;
}

fn sameUser(fd: i32) bool {
    return linux.peerUid(fd) == linux.getuid();
}

const unixAddress = linux.unixAddress;

fn bindSocket(path: []const u8) !i32 {
    const addr = try unixAddress(path);
    const rc = linux.socket(linux.AF.UNIX, linux.SOCK.STREAM | linux.SOCK.CLOEXEC, 0);
    if (linux.errno(rc) != .SUCCESS) return error.SystemResources;
    const fd: i32 = @intCast(rc);
    errdefer protocol.closeFd(fd);
    if (linux.errno(linux.bind(fd, @ptrCast(&addr), @sizeOf(linux.sockaddr_un))) != .SUCCESS) return error.BindFailed;
    if (linux.errno(linux.listen(fd, 8)) != .SUCCESS) return error.ListenFailed;
    return fd;
}

fn connectTo(path: []const u8) !i32 {
    const addr = try unixAddress(path);
    const rc = linux.socket(linux.AF.UNIX, linux.SOCK.STREAM | linux.SOCK.CLOEXEC, 0);
    if (linux.errno(rc) != .SUCCESS) return error.SystemResources;
    const fd: i32 = @intCast(rc);
    errdefer protocol.closeFd(fd);
    while (true) switch (linux.errno(linux.connect(fd, @ptrCast(&addr), @sizeOf(linux.sockaddr_un)))) {
        .SUCCESS => return fd,
        .INTR => continue,
        else => return error.ConnectionRefused,
    };
}

test "classify ssh questions" {
    try std.testing.expectEqual(Kind.secret, classify("", "me@devbox's password: ").?);
    try std.testing.expectEqual(Kind.secret, classify("", "Enter passphrase for key '/home/me/.ssh/id_ed25519': ").?);
    try std.testing.expectEqual(Kind.yes_no, classify("", "The authenticity of host 'devbox (10.0.0.2)' can't be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ").?);
    try std.testing.expectEqual(Kind.confirm, classify("confirm", "Allow shared connection to devbox? ").?);
    try std.testing.expectEqual(@as(?Kind, null), classify("none", "Confirm user presence for key ED25519-SK SHA256:x"));
}

test "listener round trip with the helper" {
    const S = struct {
        listener: *Listener = undefined,
        seen: std.atomic.Value(u32) = .init(0),
        fn ask(ctx: *anyopaque, q: Question) bool {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            _ = self.seen.fetchAdd(1, .monotonic);
            if (std.mem.eql(u8, q.text, "Refuse:")) return false;
            // Answers from another thread, like the UI would.
            const t = std.Thread.spawn(.{}, answerLater, .{ self.listener, q.id }) catch return false;
            t.detach();
            return true;
        }
        fn answerLater(l: *Listener, id: u32) void {
            _ = l.answer(id, "fake answer");
        }
        fn notice(_: *anyopaque, _: []const u8) void {}
        fn withdrawn(_: *anyopaque, _: u32) void {}
    };
    var s: S = .{};
    const l = try Listener.create(std.testing.allocator, std.testing.io, null, .{ .ctx = &s, .ask = S.ask, .notice = S.notice, .withdrawn = S.withdrawn });
    defer l.destroy();
    s.listener = l;
    var fds: [2]i32 = undefined;
    try std.testing.expectEqual(linux.E.SUCCESS, linux.errno(linux.pipeCloexec(&fds)));
    defer for (fds) |fd| protocol.closeFd(fd);
    try std.testing.expectEqual(@as(u8, 0), runHelper(l.path(), "", "Password:", fds[1]));
    var buf: [64]u8 = undefined;
    const n = try protocol.readSome(fds[0], &buf);
    try std.testing.expectEqualStrings("fake answer\n", buf[0..n]);
    try std.testing.expectEqual(@as(u8, 1), runHelper(l.path(), "", "Refuse:", fds[1]));
    try std.testing.expectEqual(@as(u8, 0), runHelper(l.path(), "none", "Touch your key", fds[1]));
    try std.testing.expectEqual(@as(u32, 2), s.seen.load(.monotonic));
}
