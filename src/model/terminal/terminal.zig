//! A terminal session: a program (normally the user's shell) on a
//! pseudo-terminal, with its output emulated by `vterm.Screen`. The event
//! loop polls `pollFd` and calls `service`; input goes to `screen` (keys,
//! pastes) and reaches the child on the next `flush`.

const std = @import("std");
const platform = @import("../../framework/platform/platform.zig");
const pty_mod = @import("../../framework/platform/pty.zig");
pub const vterm = @import("vterm.zig");

const Allocator = std.mem.Allocator;
/// Pty's interface; a cwd of ssh://host/dir runs the shell on that host.
const Pty = @import("../remote/remote_pty.zig").AnyPty;
pub const Exit = pty_mod.Exit;
pub const defaultShell = pty_mod.defaultShell;
pub const Screen = vterm.Screen;

const POLLIN: i16 = 1;
const POLLOUT: i16 = 4;

pub const Options = struct {
    /// Default: the user's shell (`defaultShell`).
    argv: ?[]const []const u8 = null,
    cwd: ?[]const u8 = null,
    rows: u16 = 24,
    cols: u16 = 80,
    scrollback: usize = Screen.default_scrollback,
};

pub const Terminal = struct {
    gpa: Allocator,
    pty: Pty,
    screen: *Screen,
    /// Input the pty hasn't accepted yet (a big paste into a busy program).
    pending: std.ArrayList(u8) = .empty,
    /// The slave side closed: the program (and anything it started) is gone.
    eof: bool = false,

    /// Time spent reading per `service` call, so a flood (`yes`) can't
    /// starve the UI; the rest is read on the next loop iteration.
    const service_budget_ms = 12;

    pub fn create(gpa: Allocator, opts: Options) !*Terminal {
        const self = try gpa.create(Terminal);
        errdefer gpa.destroy(self);
        const screen = try Screen.create(gpa, opts.rows, opts.cols, opts.scrollback);
        errdefer screen.destroy();
        const pty = try Pty.spawn(gpa, .{ .argv = opts.argv, .cwd = opts.cwd, .rows = screen.rows, .cols = screen.cols });
        self.* = .{ .gpa = gpa, .pty = pty, .screen = screen };
        return self;
    }

    /// Hangs up on the program (SIGHUP) and frees everything.
    pub fn destroy(self: *Terminal) void {
        self.pty.deinit();
        self.screen.destroy();
        self.pending.deinit(self.gpa);
        self.gpa.destroy(self);
    }

    /// The fd to poll, or null once the program is gone.
    pub fn pollFd(self: *const Terminal) ?platform.PollFd {
        if (self.eof) return null;
        return .{ .fd = self.pty.fd, .events = POLLIN | (if (self.pending.items.len > 0) POLLOUT else 0) };
    }

    /// Reads the program's output into the screen and sends pending input.
    /// Returns true if anything changed. Never blocks.
    pub fn service(self: *Terminal) bool {
        var changed = false;
        const deadline = platform.nowMs() + service_budget_ms;
        var buf: [16 * 1024]u8 = undefined;
        while (!self.eof and platform.nowMs() < deadline) {
            switch (self.pty.read(&buf)) {
                .data => |n| {
                    self.screen.write(buf[0..n]);
                    changed = true;
                },
                .would_block => break,
                .eof => {
                    self.eof = true;
                    changed = true;
                    // The exit status shows up a moment after the hangup.
                    var tries: usize = 0;
                    while (self.pty.checkExit() == null and tries < 50) : (tries += 1) {
                        var req: std.c.timespec = .{ .sec = 0, .nsec = std.time.ns_per_ms };
                        _ = std.c.nanosleep(&req, null);
                    }
                },
            }
        }
        self.flush();
        return changed;
    }

    /// Moves the screen's encoded input to the pty (as much as it takes now).
    pub fn flush(self: *Terminal) void {
        const out = &self.screen.output;
        if (out.items.len > 0) {
            if (!self.eof) self.pending.appendSlice(self.gpa, out.items) catch {};
            out.clearRetainingCapacity();
        }
        if (self.eof or self.pending.items.len == 0) return;
        const n = self.pty.write(self.pending.items);
        self.pending.replaceRangeAssumeCapacity(0, n, &.{});
    }

    pub fn resize(self: *Terminal, rows: u16, cols: u16) void {
        if (rows == self.screen.rows and cols == self.screen.cols) return;
        self.screen.resize(rows, cols);
        if (!self.eof) self.pty.resize(self.screen.rows, self.screen.cols);
    }

    /// How the program ended, once it has (null while it runs, or if it
    /// closed the terminal but hasn't exited).
    pub fn exitStatus(self: *Terminal) ?Exit {
        return self.pty.checkExit();
    }

    /// The program's current directory (follows `cd` in a shell).
    pub fn currentDir(self: *const Terminal, buf: []u8) ?[]const u8 {
        if (self.eof) return null;
        return self.pty.currentDir(buf);
    }
};

test {
    _ = vterm;
}

/// Services `term` until its program exits (tests only).
fn runToExit(term: *Terminal) !void {
    var spins: usize = 0;
    while (!term.eof) : (spins += 1) {
        if (spins > 5000) return error.Timeout;
        if (!term.service()) {
            var req: std.c.timespec = .{ .sec = 0, .nsec = std.time.ns_per_ms };
            _ = std.c.nanosleep(&req, null);
        }
    }
}

test "runs a command and shows its colored output" {
    const term = try Terminal.create(std.testing.allocator, .{
        .argv = &.{ "/bin/sh", "-c", "printf 'hello\\n\\033[31mred\\033[0m %s' \"$COLORTERM\"" },
        .rows = 5,
        .cols = 30,
    });
    defer term.destroy();
    try runToExit(term);
    const text = try term.screen.textRange(std.testing.allocator, 0, 0, 1, 30);
    defer std.testing.allocator.free(text);
    try std.testing.expectEqualStrings("hello\nred truecolor", text);
    try std.testing.expectEqual(vterm.Color{ .indexed = 1 }, term.screen.screenCell(1, 0).fg);
    try std.testing.expectEqual(vterm.Color.default, term.screen.screenCell(1, 4).fg);
    try std.testing.expectEqual(Exit{ .code = 0 }, term.exitStatus().?);
}

test "typed input reaches the program and resizing reaches the pty" {
    const term = try Terminal.create(std.testing.allocator, .{
        .argv = &.{ "/bin/sh", "-c", "read line; stty size; echo \"got $line\"" },
        .rows = 5,
        .cols = 30,
    });
    defer term.destroy();
    term.resize(6, 40);
    term.screen.text("hi");
    term.screen.key(.enter, .{});
    term.flush();
    try runToExit(term);
    const all = try term.screen.textRange(std.testing.allocator, 0, 0, term.screen.totalRows() - 1, term.screen.cols);
    defer std.testing.allocator.free(all);
    // The tty echoes the typed line.
    try std.testing.expect(std.mem.indexOf(u8, all, "hi\n6 40\ngot hi") != null);
}
