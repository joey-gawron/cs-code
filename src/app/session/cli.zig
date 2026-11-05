//! `cs-zig sessions ...`: list, inspect, save, kill, rename and delete
//! sessions from the command line. (`--session NAME` opens one: main.zig.)

const std = @import("std");
const c = @import("c");
const session = @import("session.zig");
const store_mod = @import("store.zig");
const platform = @import("../../framework/platform/platform.zig");

const Allocator = std.mem.Allocator;

pub const usage =
    \\Usage: cs-zig sessions <command>
    \\
    \\  list                 Saved and running sessions
    \\  info NAME            One line about a running session (pid, files, unsaved, folder)
    \\  attach NAME          Open NAME's window (same as --session NAME)
    \\  save NAME            Write a running session's file now
    \\  kill NAME            Quit a running session (unsaved text is kept as backups)
    \\  rename OLD NEW       Rename a saved session that is not running
    \\  delete NAME          Delete a saved session that is not running
    \\
;

fn out(comptime fmt: []const u8, args: anytype) void {
    var buf: [4096]u8 = undefined;
    const text = std.fmt.bufPrint(&buf, fmt, args) catch return;
    _ = session.writeAll(1, text);
}

fn err(comptime fmt: []const u8, args: anytype) void {
    std.debug.print("cs-zig: " ++ fmt ++ "\n", args);
}

/// The exit code.
pub fn run(gpa: Allocator, io: std.Io, args: []const []const u8) u8 {
    const cmd = if (args.len > 0) args[0] else "list";
    const eql = std.mem.eql;
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();
    const a = arena.allocator();
    var store = store_mod.Store.open(gpa, io) orelse {
        err("no data folder (HOME is not set)", .{});
        return 1;
    };
    defer store.deinit();

    if (eql(u8, cmd, "list")) return list(gpa, io, a, &store);
    if (eql(u8, cmd, "-h") or eql(u8, cmd, "--help") or eql(u8, cmd, "help")) {
        out("{s}", .{usage});
        return 0;
    }
    const name = if (args.len > 1) args[1] else {
        err("sessions {s} needs a session name\n{s}", .{ cmd, usage });
        return 1;
    };
    if (!store_mod.validName(name)) {
        err("\"{s}\" is not a session name (letters, digits, . _ -)", .{name});
        return 1;
    }
    var sock_buf: [std.fs.max_path_bytes]u8 = undefined;
    const sock = session.socketPath(&sock_buf, name);
    const live: ?i32 = if (sock) |p| session.connect(p) else null;
    defer if (live) |fd| {
        _ = c.close(fd);
    };

    if (eql(u8, cmd, "info")) {
        const fd = live orelse return notRunning(name);
        var line: [std.fs.max_path_bytes + 64]u8 = undefined;
        const reply = session.requestLine(gpa, fd, .{ .cmd = .info }, &line) orelse return noAnswer(name);
        out("{s}\n", .{reply});
        return 0;
    }
    if (eql(u8, cmd, "save")) {
        const fd = live orelse return notRunning(name);
        return if (session.sendRequest(gpa, fd, .{ .cmd = .save })) 0 else noAnswer(name);
    }
    if (eql(u8, cmd, "kill")) {
        const fd = live orelse return notRunning(name);
        if (!session.sendRequest(gpa, fd, .{ .cmd = .quit })) return noAnswer(name);
        // Its socket goes away when the process has written its file and exited.
        const deadline = platform.nowMs() + 10_000;
        while (platform.nowMs() < deadline) {
            if (session.connect(sock.?)) |again| {
                _ = c.close(again);
                var req: std.c.timespec = .{ .sec = 0, .nsec = 50 * std.time.ns_per_ms };
                _ = std.c.nanosleep(&req, null);
            } else break;
        }
        out("session {s} stopped\n", .{name});
        return 0;
    }
    if (eql(u8, cmd, "delete")) {
        if (live != null) {
            err("session {s} is running: `cs-zig sessions kill {s}` first", .{ name, name });
            return 1;
        }
        if (!store.exists(name)) {
            err("no saved session \"{s}\"", .{name});
            return 1;
        }
        store.remove(name) catch |e| {
            err("cannot delete {s}: {t}", .{ name, e });
            return 1;
        };
        out("session {s} deleted\n", .{name});
        return 0;
    }
    if (eql(u8, cmd, "rename")) {
        const to = if (args.len > 2) args[2] else {
            err("sessions rename needs the old and the new name", .{});
            return 1;
        };
        if (live != null) {
            err("session {s} is running: rename it from its window (Session: Rename…)", .{name});
            return 1;
        }
        store.rename(name, to) catch |e| {
            switch (e) {
                error.NameTaken => err("a session named {s} exists", .{to}),
                error.NoSuchSession => err("no saved session \"{s}\"", .{name}),
                error.InvalidName => err("\"{s}\" is not a session name", .{to}),
                else => err("cannot rename: {t}", .{e}),
            }
            return 1;
        };
        out("session {s} is now {s}\n", .{ name, to });
        return 0;
    }
    err("unknown sessions command \"{s}\"\n{s}", .{ cmd, usage });
    return 1;
}

fn notRunning(name: []const u8) u8 {
    err("session \"{s}\" is not running", .{name});
    return 1;
}

fn noAnswer(name: []const u8) u8 {
    err("session \"{s}\" did not answer", .{name});
    return 1;
}

const Row = struct {
    name: []const u8,
    state: []const u8,
    pid: i32 = 0,
    files: usize = 0,
    unsaved: usize = 0,
    last_used: i64 = 0,
    folder: []const u8 = "",
};

fn list(gpa: Allocator, io: std.Io, a: Allocator, store: *const store_mod.Store) u8 {
    const saved = store.list(a) catch &.{};
    const running = session.running(gpa, io, a);
    var rows: std.ArrayList(Row) = .empty;
    const now = store_mod.nowSeconds();
    for (running) |r| {
        var row: Row = .{ .name = r.name, .state = "running", .pid = r.pid, .last_used = now };
        for (saved) |s| if (std.mem.eql(u8, s.name, r.name)) {
            row.files = s.tabs;
            row.last_used = s.last_used;
            row.folder = s.folder orelse "";
        };
        // The live numbers come from the process.
        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        if (session.socketPath(&path_buf, r.name)) |p| if (session.connect(p)) |fd| {
            defer _ = c.close(fd);
            var line: [std.fs.max_path_bytes + 64]u8 = undefined;
            if (session.requestLine(gpa, fd, .{ .cmd = .info }, &line)) |reply| {
                var it = std.mem.splitScalar(u8, reply, ' ');
                _ = it.next(); // ok
                _ = it.next(); // pid
                row.files = std.fmt.parseInt(usize, it.next() orelse "", 10) catch row.files;
                row.unsaved = std.fmt.parseInt(usize, it.next() orelse "", 10) catch 0;
                const folder = it.rest();
                if (folder.len > 0 and !std.mem.eql(u8, folder, "-")) row.folder = a.dupe(u8, folder) catch "";
            }
        };
        rows.append(a, row) catch {};
    }
    for (saved) |s| {
        const is_running = for (running) |r| {
            if (std.mem.eql(u8, r.name, s.name)) break true;
        } else false;
        if (is_running) continue;
        rows.append(a, .{ .name = s.name, .state = "saved", .files = s.tabs, .last_used = s.last_used, .folder = s.folder orelse "" }) catch {};
    }
    if (rows.items.len == 0) {
        out("no sessions\n", .{});
        return 0;
    }
    var width: usize = 4;
    for (rows.items) |r| width = @max(width, r.name.len);
    var line: std.ArrayList(u8) = .empty;
    cells(&line, a, width, .{ "NAME", "STATE", "PID", "FILES", "UNSAVED", "LAST USED", "FOLDER" });
    for (rows.items) |r| {
        var pid_buf: [16]u8 = undefined;
        var files_buf: [16]u8 = undefined;
        var unsaved_buf: [16]u8 = undefined;
        var when_buf: [32]u8 = undefined;
        cells(&line, a, width, .{
            r.name,
            r.state,
            if (r.pid > 0) std.fmt.bufPrint(&pid_buf, "{d}", .{r.pid}) catch "-" else "-",
            std.fmt.bufPrint(&files_buf, "{d}", .{r.files}) catch "",
            std.fmt.bufPrint(&unsaved_buf, "{d}", .{r.unsaved}) catch "",
            @import("../workspace/session_ui.zig").ago(&when_buf, now, r.last_used),
            r.folder,
        });
    }
    return 0;
}

/// One table line: the name padded to `width`, the numbers right-aligned.
fn cells(line: *std.ArrayList(u8), a: Allocator, width: usize, c7: [7][]const u8) void {
    line.clearRetainingCapacity();
    const widths = [7]usize{ width, 8, 6, 5, 7, 10, 0 };
    const right = [7]bool{ false, false, true, true, true, false, false };
    for (c7, widths, right, 0..) |text, w, r, i| {
        const pad = if (text.len < w) w - text.len else 0;
        if (r) line.appendNTimes(a, ' ', pad) catch {};
        line.appendSlice(a, text) catch {};
        if (!r and i + 1 < c7.len) line.appendNTimes(a, ' ', pad) catch {};
        if (i + 1 < c7.len) line.appendSlice(a, "  ") catch {};
    }
    line.append(a, '\n') catch {};
    _ = session.writeAll(1, line.items);
}
