//! Remote tests: the protocol units, plus the client talking to a real
//! cs-zig-server started as a local subprocess (the ssh transport only
//! differs in the command that starts it). The "remote" files are a temp
//! folder, reached through ssh://testhost/... paths.

const std = @import("std");
const options = @import("server_test_options");
const protocol = @import("../model/remote/protocol.zig");
const path_mod = @import("../model/remote/path.zig");
const vfs = @import("../model/remote/vfs.zig");
const ssh = @import("../model/remote/ssh.zig");
const askpass = @import("../model/remote/askpass.zig");
const client = @import("../model/remote/client.zig");
const project = @import("../model/project/project.zig");
const search = @import("../model/project/search.zig");
const Executor = @import("../framework/util/executor.zig").Executor;

const testing = std.testing;
const Allocator = std.mem.Allocator;

test {
    _ = protocol;
    _ = path_mod;
    _ = vfs;
    _ = ssh;
    _ = askpass;
}

const Fixture = struct {
    gpa: Allocator,
    tmp: testing.TmpDir,
    /// Absolute path of the temp folder, and its ssh://testhost form.
    local_root: []u8,
    root: []u8,
    executor: Executor,
    conn: *client.Connection,
    events: std.ArrayList([]u8) = .empty,

    fn init(self: *Fixture) !void {
        const gpa = testing.allocator;
        const ignore: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.IGN }, .mask = std.posix.sigemptyset(), .flags = 0 };
        std.posix.sigaction(std.posix.SIG.PIPE, &ignore, null);
        self.gpa = gpa;
        self.tmp = testing.tmpDir(.{});
        errdefer self.tmp.cleanup();
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const n = try self.tmp.dir.realPath(testing.io, &buf);
        self.local_root = try gpa.dupe(u8, buf[0..n]);
        self.root = try path_mod.format(gpa, "testhost", self.local_root);
        self.executor = .init(gpa);
        self.events = .empty;
        self.conn = try client.Connection.create(gpa, testing.io, &self.executor, "testhost", .{ .transport = .{ .subprocess = options.server_path } });
        self.conn.sink = .{ .ctx = self, .onEvent = onEvent };
        try vfs.register("testhost", self.conn.backend());
        try self.conn.connect();
    }

    fn deinit(self: *Fixture) void {
        self.conn.destroy();
        self.executor.deinit();
        for (self.events.items) |e| self.gpa.free(e);
        self.events.deinit(self.gpa);
        self.gpa.free(self.root);
        self.gpa.free(self.local_root);
        self.tmp.cleanup();
    }

    fn onEvent(ctx: *anyopaque, _: *client.Connection, event: client.Event) void {
        const self: *Fixture = @ptrCast(@alignCast(ctx));
        switch (event) {
            .changed => |c| self.events.append(self.gpa, self.gpa.dupe(u8, c.path) catch return) catch {},
            .state, .prompt, .prompt_closed => {},
        }
    }

    fn remote(self: *Fixture, rel: []const u8) ![]u8 {
        return std.fs.path.join(self.gpa, &.{ self.root, rel });
    }

    fn write(self: *Fixture, rel: []const u8, data: []const u8) !void {
        if (std.fs.path.dirname(rel)) |d| try self.tmp.dir.createDirPath(testing.io, d);
        try self.tmp.dir.writeFile(testing.io, .{ .sub_path = rel, .data = data });
    }
};

test "remote files: read, write, stat, list, errors" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    const gpa = f.gpa;
    const io = testing.io;
    try testing.expectEqual(client.State.connected, f.conn.currentState());
    try testing.expect(f.conn.home.len > 0);
    try f.write("a.txt", "hello\nworld\n");
    try f.write("sub/b.bin", "\x00\x01\xff");

    const a = try f.remote("a.txt");
    defer gpa.free(a);
    const text = try vfs.readFile(gpa, io, a, 1 << 20);
    defer gpa.free(text);
    try testing.expectEqualStrings("hello\nworld\n", text);
    // Binary payloads are not escaped.
    const b = try f.remote("sub/b.bin");
    defer gpa.free(b);
    const bin = try vfs.readFile(gpa, io, b, 1 << 20);
    defer gpa.free(bin);
    try testing.expectEqualSlices(u8, "\x00\x01\xff", bin);

    const c = try f.remote("sub/new.txt");
    defer gpa.free(c);
    try vfs.writeFileAtomic(gpa, io, c, "saved remotely");
    const back = try f.tmp.dir.readFileAlloc(io, "sub/new.txt", gpa, .limited(1 << 20));
    defer gpa.free(back);
    try testing.expectEqualStrings("saved remotely", back);

    try testing.expectEqual(vfs.Kind.directory, (try vfs.stat(io, f.root)).kind);
    const st = try vfs.stat(io, a);
    try testing.expectEqual(vfs.Kind.file, st.kind);
    try testing.expectEqual(@as(u64, 12), st.size);

    const missing = try f.remote("nope.txt");
    defer gpa.free(missing);
    try testing.expectError(error.FileNotFound, vfs.readFile(gpa, io, missing, 100));
    try testing.expectError(error.FileNotFound, vfs.stat(io, missing));

    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const entries = try vfs.list(arena.allocator(), io, f.root);
    try testing.expectEqual(@as(usize, 2), entries.len);
    try testing.expectEqualStrings("sub", entries[0].name);
    try testing.expectEqual(vfs.Kind.directory, entries[0].kind);
    try testing.expectEqualStrings("a.txt", entries[1].name);

    const d = try f.remote("made/deep");
    defer gpa.free(d);
    try vfs.makeDir(io, d);
    const moved = try f.remote("made/deep/a2.txt");
    defer gpa.free(moved);
    try vfs.rename(io, c, moved);
    try vfs.delete(io, moved);
    try testing.expectError(error.FileNotFound, vfs.stat(io, moved));
}

test "remote project scan and find in files" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    const gpa = f.gpa;
    try f.write(".gitignore", "build/\n*.log\n");
    try f.write("src/main.zig", "const x = 1; // needle\npub fn main() void {}\n");
    try f.write("README.md", "No needle here? A NEEDLE.\n");
    try f.write("build/out.txt", "needle in ignored dir\n");
    try f.write("trace.log", "needle in ignored file\n");

    var snap = try project.buildSnapshot(gpa, testing.io, f.root);
    defer snap.deinit();
    var saw_main = false;
    var saw_build = false;
    for (snap.entries) |e| {
        if (std.mem.eql(u8, e.path, "src/main.zig")) saw_main = true;
        if (std.mem.eql(u8, e.path, "build")) saw_build = e.ignored and e.kind == .dir;
        try testing.expect(!std.mem.eql(u8, e.path, "trace.log"));
    }
    try testing.expect(saw_main and saw_build);
    try testing.expectEqual(@as(usize, 3), snap.files.len); // .gitignore, README.md, src/main.zig

    // Run a job's search synchronously, as the worker would.
    const main_path = try f.remote("src/main.zig");
    defer gpa.free(main_path);
    var job: search.Job = .{
        .gpa = gpa,
        .io = testing.io,
        .executor = &f.executor,
        .arena = .init(gpa),
        .query = .{ .pattern = "needle" },
        .roots = &.{f.root},
        .files = &.{},
        .where = .{},
        // An unsaved edit is searched instead of the file on disk.
        .overlays = &.{.{ .path = main_path, .text = "needle needle\n" }},
        .ctx = undefined,
        .done = undefined,
    };
    defer job.arena.deinit();
    var results = try job.search();
    defer results.deinit();
    try testing.expectEqual(@as(usize, 2), results.files_matched);
    try testing.expectEqual(@as(usize, 4), results.matches.len);
    const header = try std.fmt.allocPrint(gpa, "{s}:\n", .{main_path});
    defer gpa.free(header);
    try testing.expect(std.mem.indexOf(u8, results.text, header) != null);
    for (results.matches) |m| try testing.expect(std.ascii.eqlIgnoreCase(results.text[m[0]..m[1]], "needle"));
    try testing.expect(std.mem.indexOf(u8, results.text, "ignored") == null);
}

test "remote watch reports changes" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    try f.write("w.txt", "one");
    f.conn.watch(&.{f.local_root});
    // The watch request is asynchronous; a round trip makes sure it landed.
    _ = try vfs.stat(testing.io, f.root);
    try f.write("w.txt", "two");
    const want = try f.remote("w.txt");
    defer f.gpa.free(want);
    var waited: u32 = 0;
    while (waited < 200) : (waited += 1) {
        f.executor.drain();
        for (f.events.items) |e| if (std.mem.eql(u8, e, want)) return;
        try std.Io.sleep(testing.io, .fromMilliseconds(10), .awake);
    }
    return error.NoChangeEvent;
}

test "remote processes: piped stdio and a language server round trip" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    const gpa = f.gpa;

    const cat = try vfs.spawnRemote(&.{"cat"}, f.root);
    try protocol.writeAll(cat.stdin_fd, "ping\n");
    var buf: [4096]u8 = undefined;
    try testing.expect(protocol.waitReadable(cat.stdout_fd, 5000));
    const n = try protocol.readSome(cat.stdout_fd, &buf);
    try testing.expectEqualStrings("ping\n", buf[0..n]);
    cat.kill();
    // EOF after kill.
    while (try protocol.readSome(cat.stdout_fd, &buf) > 0) {}
    cat.destroy();

    try testing.expectError(error.FileNotFound, vfs.spawnRemote(&.{"no-such-program-cs-zig"}, f.root));

    // zls through the proxy (skipped when it isn't installed).
    const zls = vfs.spawnRemote(&.{"zls"}, f.root) catch return;
    defer zls.destroy();
    const uri = try std.fmt.allocPrint(gpa, "file://{s}", .{f.local_root});
    defer gpa.free(uri);
    const body = try std.json.Stringify.valueAlloc(gpa, .{ .jsonrpc = "2.0", .id = 1, .method = "initialize", .params = .{ .processId = null, .rootUri = uri, .capabilities = struct {}{} } }, .{});
    defer gpa.free(body);
    const header = try std.fmt.allocPrint(gpa, "Content-Length: {d}\r\n\r\n", .{body.len});
    defer gpa.free(header);
    try protocol.writeAll(zls.stdin_fd, header);
    try protocol.writeAll(zls.stdin_fd, body);
    var got: std.ArrayList(u8) = .empty;
    defer got.deinit(gpa);
    while (std.mem.indexOf(u8, got.items, "\"capabilities\"") == null) {
        if (!protocol.waitReadable(zls.stdout_fd, 20_000)) return error.NoAnswerFromZls;
        const k = try protocol.readSome(zls.stdout_fd, &buf);
        if (k == 0) return error.ZlsExited;
        try got.appendSlice(gpa, buf[0..k]);
    }
    try testing.expect(std.mem.startsWith(u8, got.items, "Content-Length: "));
    zls.kill();
    while (try protocol.readSome(zls.stdout_fd, &buf) > 0) {}
}

test "a dropped channel fails calls and ends process output" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    const p = try vfs.spawnRemote(&.{"cat"}, f.root);
    defer p.destroy();
    // The main thread reads `home` whenever it likes, reconnects included.
    const home = f.conn.home;
    const home_copy = try f.gpa.dupe(u8, home);
    defer f.gpa.free(home_copy);
    // Kill the server behind the client's back.
    _ = std.os.linux.kill(f.conn.child_pid, std.os.linux.SIG.KILL);
    var buf: [64]u8 = undefined;
    while (try protocol.readSome(p.stdout_fd, &buf) > 0) {}
    var waited: u32 = 0;
    while (f.conn.up.load(.acquire) and waited < 500) : (waited += 1) try std.Io.sleep(testing.io, .fromMilliseconds(10), .awake);
    try testing.expectError(error.NotConnected, vfs.stat(testing.io, f.root));
    // The main thread learns about it from the executor.
    waited = 0;
    while (f.conn.currentState() != .lost and waited < 500) : (waited += 1) {
        f.executor.drain();
        try std.Io.sleep(testing.io, .fromMilliseconds(10), .awake);
    }
    try testing.expectEqual(client.State.lost, f.conn.currentState());
    // And reconnecting brings everything back.
    try f.conn.connect();
    try testing.expectEqual(vfs.Kind.directory, (try vfs.stat(testing.io, f.root)).kind);
    try testing.expectEqualStrings(home_copy, home);
}

/// Holds a reconnect's thread in one of its last posts while the channel
/// drops and the main thread takes the drop, as if it came in right then.
const DropWhileConnecting = struct {
    executor: *Executor,
    conn: *client.Connection,
    hold_in: Hold,
    held: std.atomic.Value(bool) = .init(false),
    released: std.atomic.Value(bool) = .init(false),
    drop_posted: std.atomic.Value(bool) = .init(false),

    const Hold = enum {
        /// The "Connected to …" status, posted just before `.connected`.
        status,
        /// `.connected` itself.
        connected,
        /// As `.status`, then each later post of the attempt until the main
        /// thread runs it: the drop it posts again is handled while it is
        /// still busy.
        repost,
    };

    fn wake(ctx: *anyopaque) void {
        const self: *DropWhileConnecting = @ptrCast(@alignCast(ctx));
        // Once released, the only poster is the attempt (the reader is gone).
        if (self.released.load(.acquire)) {
            if (self.hold_in != .repost) return;
            self.waitTaken();
            // And a moment more, so the main thread runs it meanwhile.
            std.Io.sleep(testing.io, .fromMilliseconds(50), .awake) catch {};
            return;
        }
        // While held, the only other poster is the reader, with its drop.
        if (self.held.load(.acquire)) return self.drop_posted.store(true, .release);
        var buf: [240]u8 = undefined;
        const state = self.conn.currentState();
        const hold = switch (self.hold_in) {
            .status, .repost => state == .connecting and std.mem.startsWith(u8, self.conn.statusText(&buf), "Connected"),
            .connected => state == .connected,
        };
        if (!hold) return;
        self.held.store(true, .release);
        _ = std.os.linux.kill(self.conn.child_pid, std.os.linux.SIG.KILL);
        var waited: u32 = 0;
        while (!self.drop_posted.load(.acquire) and waited < 500) : (waited += 1) std.Io.sleep(testing.io, .fromMilliseconds(10), .awake) catch {};
        self.waitTaken();
        self.released.store(true, .release);
    }

    /// Until the main thread has taken what is queued.
    fn waitTaken(self: *DropWhileConnecting) void {
        var waited: u32 = 0;
        while (waited < 500) : (waited += 1) {
            self.executor.lock.lock();
            const queued = self.executor.pending.items.len;
            self.executor.lock.unlock();
            if (queued == 0) break;
            std.Io.sleep(testing.io, .fromMilliseconds(10), .awake) catch {};
        }
    }
};

test "a drop just as a reconnect comes up is not lost" {
    for ([_]DropWhileConnecting.Hold{ .status, .connected, .repost }) |hold_in| {
        var f: Fixture = undefined;
        try f.init();
        defer f.deinit();
        _ = std.os.linux.kill(f.conn.child_pid, std.os.linux.SIG.KILL);
        var waited: u32 = 0;
        while (f.conn.currentState() != .lost and waited < 500) : (waited += 1) {
            f.executor.drain();
            try std.Io.sleep(testing.io, .fromMilliseconds(10), .awake);
        }
        try testing.expectEqual(client.State.lost, f.conn.currentState());

        // Reconnect as the window does, and drop again as it comes up.
        var hold: DropWhileConnecting = .{ .executor = &f.executor, .conn = f.conn, .hold_in = hold_in };
        f.executor.waker = DropWhileConnecting.wake;
        f.executor.waker_ctx = &hold;
        defer f.executor.waker = null;
        f.conn.connectAsync();
        waited = 0;
        while ((f.conn.busy.load(.acquire) or f.conn.currentState() != .lost) and waited < 300) : (waited += 1) {
            f.executor.drain();
            try std.Io.sleep(testing.io, .fromMilliseconds(10), .awake);
        }
        try testing.expect(hold.drop_posted.load(.acquire));
        // Not "connected" with the channel down: lost, so it reconnects.
        try testing.expectEqual(client.State.lost, f.conn.currentState());
    }
}

/// Stands in for ssh: skips the options and the destination and runs the
/// remote command with sh, in a fake home. Everything but ssh itself (the
/// launch script, missing-server detection, the upload in the same session,
/// install layout) is real.
const fake_ssh =
    \\#!/bin/sh
    \\while [ $# -gt 0 ]; do
    \\  case "$1" in
    \\    -e|-o|-p|-l|-i|-F|-J) shift 2 ;;
    \\    -*) shift ;;
    \\    *) break ;;
    \\  esac
    \\done
    \\shift
    \\HOME="$FAKE_HOME" exec sh -c "$*"
    \\
;

test "ssh deployment: upload the server when missing, reuse it after" {
    const gpa = testing.allocator;
    const io = testing.io;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(io, &buf)];
    try tmp.dir.createDirPath(io, "home");
    const home = try std.fs.path.join(gpa, &.{ root, "home" });
    defer gpa.free(home);
    const script_text = try std.mem.replaceOwned(u8, gpa, fake_ssh, "$FAKE_HOME", home);
    defer gpa.free(script_text);
    try tmp.dir.writeFile(io, .{ .sub_path = "fake-ssh", .data = script_text, .flags = .{ .permissions = .executable_file } });
    const script = try std.fs.path.join(gpa, &.{ root, "fake-ssh" });
    defer gpa.free(script);

    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const arch = @tagName(@import("builtin").cpu.arch);
    const conn = try client.Connection.create(gpa, io, &executor, "devbox", .{
        .transport = .ssh,
        .ssh_program = script,
        .binaries = &.{.{ .arch = arch, .path = options.server_path }},
    });
    defer conn.destroy();
    try conn.connect();
    try testing.expectEqual(client.State.connected, conn.currentState());
    try testing.expectEqualStrings(home, conn.home);

    // Installed under ~/.cs-zig-server/<protocol>-<content hash>/.
    const data = try std.Io.Dir.cwd().readFileAlloc(io, options.server_path, gpa, .limited(256 << 20));
    defer gpa.free(data);
    var tag_buf: [24]u8 = undefined;
    const installed = try std.fs.path.join(gpa, &.{ home, ".cs-zig-server", ssh.versionTag(&tag_buf, data), "cs-zig-server" });
    defer gpa.free(installed);
    const first = try std.Io.Dir.cwd().statFile(io, installed, .{});
    try testing.expectEqual(@as(u64, data.len), first.size);

    // A second connection starts the installed copy without uploading again.
    conn.disconnect();
    try testing.expectEqual(client.State.idle, conn.currentState());
    try conn.connect();
    try testing.expectEqual(client.State.connected, conn.currentState());
    const second = try std.Io.Dir.cwd().statFile(io, installed, .{});
    try testing.expectEqual(first.mtime.nanoseconds, second.mtime.nanoseconds);
    executor.drain();
}

test "ssh deployment: a bad upload installs nothing" {
    const gpa = testing.allocator;
    const io = testing.io;
    const ignore: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.IGN }, .mask = std.posix.sigemptyset(), .flags = 0 };
    std.posix.sigaction(std.posix.SIG.PIPE, &ignore, null);
    var tmp = testing.tmpDir(.{ .iterate = true });
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const home = buf[0..try tmp.dir.realPath(io, &buf)];
    // No build for this machine, so the script asks for one on stdin.
    const launch = try ssh.launchCommand(gpa, &.{.{ .arch = "none", .tag = "p1-0" }});
    defer gpa.free(launch);
    // Cut short, a tag that leaves the install folder, no size, nothing.
    for ([_][]const u8{ "p1-short 100\nonly this", "../p1-out 3\nabc", "p1-nosize\nabc", "" }) |input| {
        var child = try std.process.spawn(io, .{
            .argv = &.{ "sh", "-c", "HOME=\"$0\" exec sh -c \"$1\"", home, launch },
            .stdin = .pipe,
            .stdout = .ignore,
            .stderr = .ignore,
        });
        try protocol.writeAll(child.stdin.?.handle, input);
        child.stdin.?.close(io);
        child.stdin = null;
        try testing.expectEqual(std.process.Child.Term{ .exited = 1 }, try child.wait(io));
    }
    // Folders may be made, but no file is left behind.
    var walker = try tmp.dir.walk(gpa);
    defer walker.deinit();
    while (try walker.next(io)) |e| try testing.expectEqual(std.Io.File.Kind.directory, e.kind);
}

/// fake_ssh behind a password, asked through $SSH_ASKPASS as ssh does without
/// BatchMode (and, on its first run, whether to trust the host key).
const fake_ssh_password =
    \\#!/bin/sh
    \\deny() { echo "$1" >&2; exit 255; }
    \\case "$*" in *BatchMode=yes*) deny 'me@devbox: Permission denied (password).' ;; esac
    \\[ "$SSH_ASKPASS_REQUIRE" = force ] || deny 'SSH_ASKPASS_REQUIRE is not force'
    \\if [ ! -e "$FAKE_HOME/.known_hosts" ]; then
    \\  answer=$("$SSH_ASKPASS" "The authenticity of host 'devbox (10.0.0.2)' can't be established.
    \\ED25519 key fingerprint is SHA256:not-a-real-key.
    \\Are you sure you want to continue connecting (yes/no/[fingerprint])? ")
    \\  [ "$answer" = yes ] || deny 'Host key verification failed.'
    \\  touch "$FAKE_HOME/.known_hosts"
    \\fi
    \\answer=$("$SSH_ASKPASS" 'Password:') || deny 'me@devbox: Permission denied (password).'
    \\[ "$answer" = 'fake password' ] || deny 'me@devbox: Permission denied (password).'
    \\
++ fake_ssh["#!/bin/sh\n".len..];

/// Plays the editor's part: answers ssh's questions from the event sink.
const Asker = struct {
    gpa: Allocator,
    reply: []const u8 = "fake password",
    /// Escape instead of answering.
    cancel: bool = false,
    kinds: std.ArrayList(client.PromptKind) = .empty,
    texts: std.ArrayList([]u8) = .empty,

    fn deinit(self: *Asker) void {
        for (self.texts.items) |t| self.gpa.free(t);
        self.texts.deinit(self.gpa);
        self.kinds.deinit(self.gpa);
    }

    fn onEvent(ctx: *anyopaque, conn: *client.Connection, event: client.Event) void {
        const self: *Asker = @ptrCast(@alignCast(ctx));
        const p = switch (event) {
            .prompt => |p| p,
            else => return,
        };
        self.kinds.append(self.gpa, p.kind) catch {};
        self.texts.append(self.gpa, self.gpa.dupe(u8, p.text) catch return) catch {};
        if (self.cancel) return conn.cancelConnect();
        conn.answerPrompt(p.id, if (p.kind == .yes_no) "yes" else self.reply);
    }

    /// connectAsync, delivering events (and so answering) until it's done.
    fn connect(self: *Asker, conn: *client.Connection, executor: *Executor) !client.State {
        _ = self;
        conn.connectAsync();
        var waited: u32 = 0;
        while (conn.busy.load(.acquire)) : (waited += 1) {
            if (waited > 3000) return error.Timeout;
            executor.drain();
            try std.Io.sleep(testing.io, .fromMilliseconds(10), .awake);
        }
        executor.drain();
        return conn.currentState();
    }
};

test "ssh with a password: the editor answers as SSH_ASKPASS" {
    const gpa = testing.allocator;
    const io = testing.io;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(io, &buf)];
    try tmp.dir.createDirPath(io, "home");
    const home = try std.fs.path.join(gpa, &.{ root, "home" });
    defer gpa.free(home);
    const script_text = try std.mem.replaceOwned(u8, gpa, fake_ssh_password, "$FAKE_HOME", home);
    defer gpa.free(script_text);
    try tmp.dir.writeFile(io, .{ .sub_path = "fake-ssh", .data = script_text, .flags = .{ .permissions = .executable_file } });
    const script = try std.fs.path.join(gpa, &.{ root, "fake-ssh" });
    defer gpa.free(script);

    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const arch = @tagName(@import("builtin").cpu.arch);
    const conn = try client.Connection.create(gpa, io, &executor, "devbox", .{
        .transport = .ssh,
        .ssh_program = script,
        .binaries = &.{.{ .arch = arch, .path = options.server_path }},
        .askpass = options.editor_path,
    });
    defer conn.destroy();
    var asker: Asker = .{ .gpa = gpa };
    defer asker.deinit();
    conn.sink = .{ .ctx = &asker, .onEvent = Asker.onEvent };
    var text: [240]u8 = undefined;

    // A blocking connect can't ask anyone: BatchMode, and ssh gives up.
    try testing.expectError(error.ConnectionClosed, conn.connect());
    executor.drain();
    try testing.expectEqual(@as(usize, 0), asker.kinds.items.len);

    // In the background, the host key and then the password are asked. The
    // first deploy uploads through the launch session: one ssh run, so one
    // password even without ControlMaster.
    try testing.expectEqual(client.State.connected, try asker.connect(conn, &executor));
    try testing.expectEqualStrings(home, conn.home);
    try testing.expectEqualSlices(client.PromptKind, &.{ .yes_no, .secret }, asker.kinds.items);
    try testing.expect(std.mem.indexOf(u8, asker.texts.items[0], "SHA256:not-a-real-key") != null);
    try testing.expectEqualStrings("Password:", asker.texts.items[1]);
    conn.disconnect();

    // A wrong password ends with ssh's message.
    asker.reply = "wrong";
    try testing.expectEqual(client.State.failed, try asker.connect(conn, &executor));
    try testing.expectEqualStrings("me@devbox: Permission denied (password).", conn.statusText(&text));

    // Escape on the question cancels the attempt.
    asker.cancel = true;
    try testing.expectEqual(client.State.idle, try asker.connect(conn, &executor));
    try testing.expectEqualStrings("Canceled connecting to devbox", conn.statusText(&text));
}

test "ssh failures surface ssh's message" {
    const gpa = testing.allocator;
    const io = testing.io;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(io, &buf)];
    try tmp.dir.writeFile(io, .{ .sub_path = "fake-ssh", .data = "#!/bin/sh\necho 'me@devbox: Permission denied (publickey).' >&2\nexit 255\n", .flags = .{ .permissions = .executable_file } });
    const script = try std.fs.path.join(gpa, &.{ root, "fake-ssh" });
    defer gpa.free(script);
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const conn = try client.Connection.create(gpa, io, &executor, "devbox", .{ .transport = .ssh, .ssh_program = script });
    defer conn.destroy();
    try testing.expectError(error.ConnectionClosed, conn.connect());
    try testing.expectEqual(client.State.failed, conn.currentState());
    var text: [240]u8 = undefined;
    try testing.expectEqualStrings("me@devbox: Permission denied (publickey).", conn.statusText(&text));
    executor.drain();
}

test "remote terminals: a pty on the server" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    const gpa = f.gpa;
    try f.write("sub/x", "");
    const sub = try f.remote("sub");
    defer gpa.free(sub);
    const p = try vfs.spawnRemotePty(&.{ "/bin/sh", "-c", "stty size; pwd; [ -t 0 ] && echo tty; read line; echo \"got $line\"; stty size; exit 3" }, sub, 7, 33);
    defer p.destroy();
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(gpa);
    var buf: [4096]u8 = undefined;
    const readUntil = struct {
        fn until(fd: i32, list: *std.ArrayList(u8), a: Allocator, needle: []const u8, b: []u8) !void {
            while (std.mem.indexOf(u8, list.items, needle) == null) {
                if (!protocol.waitReadable(fd, 5000)) return error.Timeout;
                const k = try protocol.readSome(fd, b);
                if (k == 0) return error.EndOfStream;
                try list.appendSlice(a, b[0..k]);
            }
        }
    }.until;
    try readUntil(p.stdout_fd, &out, gpa, "tty", &buf);
    try testing.expect(std.mem.indexOf(u8, out.items, "7 33") != null);
    try testing.expect(std.mem.indexOf(u8, out.items, f.local_root) != null);
    // The shell's directory, as an ssh:// path.
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(sub, p.currentDir(&dir_buf).?);
    p.resize(20, 100);
    // The pty echoes input and turns \n into \r\n, like a terminal.
    try protocol.writeAll(p.stdin_fd, "hi\n");
    try readUntil(p.stdout_fd, &out, gpa, "20 100", &buf);
    try testing.expect(std.mem.indexOf(u8, out.items, "got hi\r\n") != null);
    while (try protocol.readSome(p.stdout_fd, &buf) > 0) {}
    try testing.expectEqual(@as(?i64, 3), p.exitCode());
}

test "unread process output never blocks requests" {
    var f: Fixture = undefined;
    try f.init();
    defer f.deinit();
    // Far more than a pipe holds, and nobody reads it yet...
    const p = try vfs.spawnRemote(&.{ "sh", "-c", "head -c 4000000 /dev/zero" }, f.root);
    defer p.destroy();
    try std.Io.sleep(testing.io, .fromMilliseconds(300), .awake);
    // ...yet calls still get through (the reader thread only queues output).
    try testing.expectEqual(vfs.Kind.directory, (try vfs.stat(testing.io, f.root)).kind);
    var total: usize = 0;
    var buf: [64 * 1024]u8 = undefined;
    while (true) {
        const n = try protocol.readSome(p.stdout_fd, &buf);
        if (n == 0) break;
        total += n;
    }
    try testing.expectEqual(@as(usize, 4_000_000), total);
}

/// What a server sent before it exited: answers by request id, and process
/// exits by handle.
const Closed = struct { answered: [8]bool = @splat(false), exited: [8]bool = @splat(false) };

/// Sends `requests` to a server of its own, closes the channel as the editor
/// does once the processes with handles below `procs` printed "up", and reads
/// until the server exits, which must be clean: status 0 and nothing on
/// stderr (the debug allocator reports leaks there).
fn closeWhenUp(requests: []const []const u8, procs: usize) !Closed {
    const gpa = testing.allocator;
    const io = testing.io;
    const ignore: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.IGN }, .mask = std.posix.sigemptyset(), .flags = 0 };
    std.posix.sigaction(std.posix.SIG.PIPE, &ignore, null);
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    const err_file = try tmp.dir.createFile(io, "stderr", .{});
    defer err_file.close(io);
    var child = try std.process.spawn(io, .{ .argv = &.{ options.server_path, "--stdio" }, .stdin = .pipe, .stdout = .pipe, .stderr = .{ .file = err_file } });
    defer child.kill(io);
    const from = child.stdout.?.handle;
    var c: [1]u8 = .{0};
    while (c[0] != '\n') if (try protocol.readSome(from, &c) == 0) return error.EndOfStream; // the banner
    for (requests) |r| try protocol.writeFrame(child.stdin.?.handle, r, "");

    var closed: Closed = .{};
    var up: [8]bool = @splat(false);
    var reader: protocol.FrameReader = .{};
    defer reader.deinit(gpa);
    var buf: [4096]u8 = undefined;
    while (true) {
        if (std.mem.indexOfScalar(bool, up[0..procs], false) == null) if (child.stdin) |stdin| {
            stdin.close(io);
            child.stdin = null;
        };
        if (!protocol.waitReadable(from, 10_000)) return error.Timeout;
        const n = try protocol.readSome(from, &buf);
        if (n == 0) break;
        try reader.feed(gpa, buf[0..n]);
        while (try reader.next()) |frame| {
            var parsed = try std.json.parseFromSlice(std.json.Value, gpa, frame.json, .{});
            defer parsed.deinit();
            if (protocol.getInt(parsed.value, "id")) |id| closed.answered[@intCast(id)] = true;
            const method = protocol.getStr(parsed.value, "method") orelse continue;
            const handle: usize = @intCast(protocol.getInt(protocol.get(parsed.value, "params"), "handle") orelse continue);
            if (std.mem.eql(u8, method, "proc.out") and std.mem.indexOf(u8, frame.payload, "up") != null) up[handle] = true;
            if (std.mem.eql(u8, method, "proc.exit")) closed.exited[handle] = true;
        }
    }
    try testing.expectEqual(std.process.Child.Term{ .exited = 0 }, try child.wait(io));
    const err_text = try tmp.dir.readFileAlloc(io, "stderr", gpa, .limited(1 << 20));
    defer gpa.free(err_text);
    try testing.expectEqualStrings("", err_text);
    return closed;
}

test "the server reports its processes' exits before it exits" {
    // A closed channel kills the server's processes, but their output threads
    // still use the server, which lives on main's stack: exiting before them
    // crashed it. Here a pipe and a terminal stay open a moment after the kill
    // (a background sleep holds each; the terminal's ignores the hangup), and
    // the pipe until its stdin closes too (`cat`, which the shell didn't exec).
    const closed = try closeWhenUp(&.{
        \\{"id":1,"method":"spawn","params":{"handle":0,"argv":["sh","-c","sleep 0.3 & echo up; cat; :"]}}
        ,
        \\{"id":2,"method":"spawn","params":{"handle":1,"argv":["/bin/sh","-c","trap '' HUP; sleep 0.3 & echo up; exec cat"],"pty":{}}}
        ,
    }, 2);
    try testing.expect(closed.exited[0] and closed.exited[1]);
}

test "the server answers scans and searches before it exits" {
    // Their threads use the server until their last line too. The channel
    // closes right after the requests, often before the search has started.
    const gpa = testing.allocator;
    const io = testing.io;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(io, .{ .sub_path = "a.txt", .data = "needle\n" });
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(io, &buf)];
    const scan = try std.json.Stringify.valueAlloc(gpa, .{ .id = 1, .method = "scan", .params = .{ .root = root } }, .{});
    defer gpa.free(scan);
    const find = try std.json.Stringify.valueAlloc(gpa, .{ .id = 2, .method = "search", .params = .{ .pattern = "needle", .roots = &.{root} } }, .{});
    defer gpa.free(find);
    const closed = try closeWhenUp(&.{ scan, find }, 0);
    try testing.expect(closed.answered[1] and closed.answered[2]);
}
