//! A terminal's pty, here or on a Remote-SSH host. Terminal sessions use
//! `AnyPty` in place of framework's `Pty`: a working directory of
//! ssh://host/dir starts the program on that host (cs-zig-server allocates
//! the pty there and proxies it over the connection), anything else is a
//! local `Pty`. The methods mirror `Pty`'s.

const std = @import("std");
const pty_mod = @import("../../framework/platform/pty.zig");
const vfs = @import("vfs.zig");
const rpath = @import("path.zig");

const linux = @import("sys.zig");
const Allocator = std.mem.Allocator;

pub const Options = pty_mod.Options;
pub const Exit = pty_mod.Exit;
pub const ReadResult = pty_mod.Pty.ReadResult;

pub const AnyPty = struct {
    /// Poll this for output: the local master, or the remote output pipe.
    fd: i32,
    local: ?pty_mod.Pty = null,
    remote: ?*vfs.Process = null,

    pub fn spawn(gpa: Allocator, opts: Options) !AnyPty {
        if (opts.cwd) |cwd| if (rpath.isRemote(cwd)) {
            // No argv: the remote login shell (a local $SHELL means nothing there).
            const p = try vfs.spawnRemotePty(opts.argv orelse &.{}, cwd, opts.rows, opts.cols);
            // Never block the UI: like a pty master, both ends are non-blocking.
            setNonblocking(p.stdout_fd);
            setNonblocking(p.stdin_fd);
            return .{ .fd = p.stdout_fd, .remote = p };
        };
        const local = try pty_mod.Pty.spawn(gpa, opts);
        return .{ .fd = local.fd, .local = local };
    }

    pub fn deinit(self: *AnyPty) void {
        if (self.local) |*l| l.deinit();
        if (self.remote) |p| {
            p.kill();
            p.destroy();
        }
        self.* = undefined;
    }

    pub fn read(self: *AnyPty, buf: []u8) ReadResult {
        if (self.local) |*l| return l.read(buf);
        const rc = linux.read(self.fd, buf.ptr, buf.len);
        return switch (linux.errno(rc)) {
            .SUCCESS => if (rc == 0) .eof else .{ .data = rc },
            .AGAIN, .INTR => .would_block,
            else => .eof,
        };
    }

    /// Writes as much of `bytes` as is accepted now; returns the count.
    pub fn write(self: *AnyPty, bytes: []const u8) usize {
        if (self.local) |*l| return l.write(bytes);
        const p = self.remote.?;
        var done: usize = 0;
        while (done < bytes.len) {
            const rc = linux.write(p.stdin_fd, bytes[done..].ptr, bytes.len - done);
            switch (linux.errno(rc)) {
                .SUCCESS => done += rc,
                .INTR => continue,
                else => break,
            }
        }
        return done;
    }

    pub fn resize(self: *AnyPty, rows: u16, cols: u16) void {
        if (self.local) |*l| return l.resize(rows, cols);
        self.remote.?.resize(rows, cols);
    }

    pub fn checkExit(self: *AnyPty) ?Exit {
        if (self.local) |*l| return l.checkExit();
        const code = self.remote.?.exitCode() orelse return null;
        return if (code >= 0) .{ .code = @truncate(@as(u64, @intCast(code))) } else .{ .signal = @truncate(@as(u64, @intCast(-code))) };
    }

    /// The program's current directory (ssh:// for a remote one).
    pub fn currentDir(self: *const AnyPty, buf: []u8) ?[]const u8 {
        if (self.local) |*l| return l.currentDir(buf);
        return self.remote.?.currentDir(buf);
    }
};

const setNonblocking = linux.setNonblocking;
