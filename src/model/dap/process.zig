//! A debug adapter run as a child process speaking DAP over its stdio. A
//! reader thread frames and parses what it writes; the messages reach the
//! session on the main thread through the executor.

const std = @import("std");
const Allocator = std.mem.Allocator;
const Value = std.json.Value;
const frame = @import("frame.zig");
const session_mod = @import("session.zig");
const Executor = @import("../../framework/util/executor.zig").Executor;

const log = std.log.scoped(.dap);

pub const ProcessTransport = struct {
    gpa: Allocator,
    io: std.Io,
    executor: *Executor,
    /// Null once `close` was called: nothing more is delivered.
    session: ?*session_mod.Session,
    child: std.process.Child,
    in_fd: std.c.fd_t,
    out_fd: std.c.fd_t,
    reader: ?std.Thread = null,
    closing: bool = false,
    /// The reader's last message was delivered (main thread only).
    reader_done: bool = false,
    /// The adapter's stdout, closed once the reader is done after `close`.
    out_closer: ?std.Io.File = null,

    pub fn spawn(gpa: Allocator, io: std.Io, executor: *Executor, session: *session_mod.Session, argv: []const []const u8, cwd: ?[]const u8) !*ProcessTransport {
        var child = try std.process.spawn(io, .{
            .argv = argv,
            .cwd = if (cwd) |c| .{ .path = c } else .inherit,
            .stdin = .pipe,
            .stdout = .pipe,
            .stderr = .ignore,
        });
        errdefer child.kill(io);
        const self = try gpa.create(ProcessTransport);
        errdefer gpa.destroy(self);
        self.* = .{
            .gpa = gpa,
            .io = io,
            .executor = executor,
            .session = session,
            .child = child,
            .in_fd = child.stdin.?.handle,
            .out_fd = child.stdout.?.handle,
        };
        self.reader = try std.Thread.spawn(.{}, readerLoop, .{self});
        return self;
    }

    pub fn transport(self: *ProcessTransport) session_mod.Transport {
        return .{ .ctx = self, .write = write };
    }

    pub fn pid(self: *const ProcessTransport) ?i64 {
        return if (self.child.id) |id| @intCast(id) else null;
    }

    fn write(ctx: *anyopaque, body: []const u8) void {
        const self: *ProcessTransport = @ptrCast(@alignCast(ctx));
        if (self.closing) return;
        var head: [48]u8 = undefined;
        const h = frame.header(&head, body.len);
        writeAll(self.in_fd, h) catch return;
        writeAll(self.in_fd, body) catch return;
    }

    /// Ends the adapter and lets go of the session. Frees the transport now,
    /// or when the reader's last message has been through the executor.
    pub fn close(self: *ProcessTransport) void {
        if (self.closing) return;
        self.closing = true;
        self.session = null;
        // stdout stays open until the reader has seen EOF.
        const out = self.child.stdout;
        self.child.stdout = null;
        self.child.kill(self.io);
        if (self.reader_done) {
            self.finish(out);
        } else {
            self.out_closer = out;
        }
    }

    fn finish(self: *ProcessTransport, out: ?std.Io.File) void {
        if (self.reader) |t| t.join();
        self.reader = null;
        if (out) |f| _ = std.c.close(f.handle);
        self.gpa.destroy(self);
    }

    const Incoming = struct { transport: *ProcessTransport, parsed: ?std.json.Parsed(Value) };

    fn post(self: *ProcessTransport, parsed: ?std.json.Parsed(Value)) void {
        const incoming = self.gpa.create(Incoming) catch {
            if (parsed) |p| p.deinit();
            return;
        };
        incoming.* = .{ .transport = self, .parsed = parsed };
        self.executor.post(.{ .ctx = incoming, .run = runIncoming });
    }

    fn runIncoming(ctx: *anyopaque) void {
        const incoming: *Incoming = @ptrCast(@alignCast(ctx));
        const self = incoming.transport;
        const gpa = self.gpa;
        defer gpa.destroy(incoming);
        if (incoming.parsed) |*p| {
            defer p.deinit();
            if (self.session) |s| s.handleMessage(p.value);
            return;
        }
        // The reader's last message: the adapter's stdout is closed.
        self.reader_done = true;
        if (self.closing) {
            self.finish(self.out_closer);
        } else if (self.session) |s| {
            s.adapterExited();
        }
    }

    fn readerLoop(self: *ProcessTransport) void {
        const gpa = self.gpa;
        var reader: frame.Reader = .{};
        defer reader.deinit(gpa);
        var tmp: [65536]u8 = undefined;
        outer: while (true) {
            const n = std.c.read(self.out_fd, &tmp, tmp.len);
            if (n <= 0) break;
            reader.push(gpa, tmp[0..@intCast(n)]) catch break;
            while (true) {
                const body = (reader.pop(gpa) catch break :outer) orelse break;
                defer gpa.free(body);
                if (std.json.parseFromSlice(Value, gpa, body, .{})) |parsed| {
                    self.post(parsed);
                } else |err| log.warn("bad JSON from the adapter: {t}", .{err});
            }
        }
        self.post(null);
    }
};

fn writeAll(fd: std.c.fd_t, bytes: []const u8) !void {
    var off: usize = 0;
    while (off < bytes.len) {
        const n = std.c.write(fd, bytes[off..].ptr, bytes.len - off);
        if (n <= 0) return error.WriteFailed;
        off += @intCast(n);
    }
}
