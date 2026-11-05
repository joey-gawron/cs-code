//! The system clipboard when no window of ours is open (a session daemon
//! between windows), and keeping what we copied after our window closes.
//! macOS: NSPasteboard, which needs no window. Linux: the selection belongs
//! to a window, so this goes through wl-copy / wl-paste (Wayland) or xclip /
//! xsel (X11), which keep serving the text after they're started.

const std = @import("std");
const builtin = @import("builtin");
const Io = std.Io;
const Allocator = std.mem.Allocator;

const macos = builtin.os.tag.isDarwin();
const cocoa = if (macos) @import("cocoa.zig") else struct {};

const log = std.log.scoped(.clipboard);

/// How long a clipboard tool may take to answer.
const timeout_ms = 1000;

const Tool = struct { set: []const []const u8, get: []const []const u8 };

const wayland_tools = [_]Tool{.{ .set = &.{"wl-copy"}, .get = &.{ "wl-paste", "--no-newline" } }};
const x11_tools = [_]Tool{
    .{ .set = &.{ "xclip", "-selection", "clipboard", "-in" }, .get = &.{ "xclip", "-selection", "clipboard", "-out" } },
    .{ .set = &.{ "xsel", "--clipboard", "--input" }, .get = &.{ "xsel", "--clipboard", "--output" } },
};

fn tools() []const Tool {
    if (std.c.getenv("WAYLAND_DISPLAY") != null) return &wayland_tools;
    if (std.c.getenv("DISPLAY") != null) return &x11_tools;
    return &.{};
}

/// Puts `text` on the system clipboard. False if there is no way to.
pub fn set(io: Io, text: []const u8) bool {
    if (macos) {
        cocoa.setSystemClipboard(text);
        return true;
    }
    for (tools()) |t| if (run(io, t.set, text)) return true;
    return false;
}

/// The system clipboard's text (caller frees), or null.
pub fn get(gpa: Allocator, io: Io) ?[]u8 {
    if (macos) return cocoa.getSystemClipboard(gpa);
    for (tools()) |t| if (read(gpa, io, t.get)) |text| return text;
    return null;
}

/// Runs a copy tool with `text` on its stdin. wl-copy and xclip go on
/// serving the clipboard in the background once they've read it.
fn run(io: Io, argv: []const []const u8, text: []const u8) bool {
    var child = std.process.spawn(io, .{ .argv = argv, .stdin = .pipe, .stdout = .ignore, .stderr = .ignore }) catch return false;
    child.stdin.?.writeStreamingAll(io, text) catch {};
    child.stdin.?.close(io);
    child.stdin = null;
    const term = child.wait(io) catch return false;
    return term == .exited and term.exited == 0;
}

fn read(gpa: Allocator, io: Io, argv: []const []const u8) ?[]u8 {
    var child = std.process.spawn(io, .{ .argv = argv, .stdin = .ignore, .stdout = .pipe, .stderr = .ignore }) catch return null;
    defer child.kill(io);
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    const fd = child.stdout.?.handle;
    while (true) {
        var pfd = [_]std.c.pollfd{.{ .fd = fd, .events = std.c.POLL.IN, .revents = 0 }};
        if (std.c.poll(&pfd, 1, timeout_ms) <= 0) {
            log.warn("{s} didn't answer", .{argv[0]});
            out.deinit(gpa);
            return null;
        }
        var buf: [16384]u8 = undefined;
        const n = std.c.read(fd, &buf, buf.len);
        if (n <= 0) break;
        out.appendSlice(gpa, buf[0..@intCast(n)]) catch {
            out.deinit(gpa);
            return null;
        };
    }
    const term = child.wait(io) catch {
        out.deinit(gpa);
        return null;
    };
    if (term != .exited or term.exited != 0) {
        out.deinit(gpa);
        return null;
    }
    return out.toOwnedSlice(gpa) catch null;
}
