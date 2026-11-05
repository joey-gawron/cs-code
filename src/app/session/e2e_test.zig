//! End to end: the real `cs` binary (build option `exe_path`) with its own
//! HOME and CS_ZIG_HOME, so nothing here touches the user's sessions. Edits
//! happen in headless runs; a daemon is a real background process. State is
//! read back from the session files and from `sessions list|info`.

const std = @import("std");
const opts = @import("e2e_options");

const testing = std.testing;
const Io = std.Io;
const gpa = testing.allocator;

const Env = struct {
    root: []u8,
    map: std.process.Environ.Map,

    fn create(tag: []const u8) !Env {
        const io = testing.io;
        // Short: a Unix socket path is limited to about 100 bytes.
        const root = try std.fmt.allocPrint(gpa, "/tmp/cs-e2e-{s}-{d}", .{ tag, std.c.getpid() });
        errdefer gpa.free(root);
        Io.Dir.cwd().deleteTree(io, root) catch {};
        for ([_][]const u8{ "home", "h", "proj/src" }) |sub| {
            const p = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ root, sub });
            defer gpa.free(p);
            try Io.Dir.cwd().createDirPath(io, p);
        }
        var env: Env = .{ .root = root, .map = try testing.environ.createMap(gpa) };
        errdefer env.map.deinit();
        const home = try env.path("home");
        defer gpa.free(home);
        const data = try env.path("h");
        defer gpa.free(data);
        try env.map.put("HOME", home);
        try env.map.put("CS_ZIG_HOME", data);
        _ = env.map.swapRemove("XDG_RUNTIME_DIR");
        _ = env.map.swapRemove("XDG_DATA_HOME");
        try env.write("proj/src/main.zig", "pub fn main() void {}\n");
        try env.write("proj/README.md", "# Notes\n\nline two\n");
        return env;
    }

    fn destroy(self: *Env) void {
        Io.Dir.cwd().deleteTree(testing.io, self.root) catch {};
        gpa.free(self.root);
        self.map.deinit();
    }

    fn path(self: *const Env, rel: []const u8) ![]u8 {
        return std.fmt.allocPrint(gpa, "{s}/{s}", .{ self.root, rel });
    }

    fn write(self: *const Env, rel: []const u8, text: []const u8) !void {
        const p = try self.path(rel);
        defer gpa.free(p);
        try Io.Dir.cwd().writeFile(testing.io, .{ .sub_path = p, .data = text });
    }

    fn read(self: *const Env, rel: []const u8) ![]u8 {
        const p = try self.path(rel);
        defer gpa.free(p);
        return Io.Dir.cwd().readFileAlloc(testing.io, p, gpa, .limited(1 << 20));
    }

    const Result = struct {
        stdout: []u8,
        stderr: []u8,
        code: u8,

        fn deinit(self: Result) void {
            gpa.free(self.stdout);
            gpa.free(self.stderr);
        }
    };

    fn cs(self: *Env, args: []const []const u8) !Result {
        var argv: std.ArrayList([]const u8) = .empty;
        defer argv.deinit(gpa);
        try argv.append(gpa, opts.exe_path);
        try argv.appendSlice(gpa, args);
        const r = try std.process.run(gpa, testing.io, .{ .argv = argv.items, .environ_map = &self.map });
        return .{
            .stdout = r.stdout,
            .stderr = r.stderr,
            .code = switch (r.term) {
                .exited => |c| c,
                else => 255,
            },
        };
    }

    /// A headless run of session `name`: `extra` are the steps, after the fixed arguments.
    fn headless(self: *Env, name: []const u8, extra: []const []const u8) !void {
        var args: std.ArrayList([]const u8) = .empty;
        defer args.deinit(gpa);
        try args.appendSlice(gpa, &.{ "--headless", "900x600", "--no-lsp", "--session", name });
        try args.appendSlice(gpa, extra);
        const r = try self.cs(args.items);
        defer r.deinit();
        if (r.code != 0) {
            std.debug.print("cs exited {d}:\n{s}\n", .{ r.code, r.stderr });
            return error.CsFailed;
        }
    }

    /// The tabs of a saved session: path ("" for untitled) and backup text ("" for none).
    fn tabs(self: *Env, arena: std.mem.Allocator, name: []const u8) ![]Tab {
        const rel = try std.fmt.allocPrint(arena, "h/sessions/{s}.json", .{name});
        const raw = try self.read(rel);
        defer gpa.free(raw);
        const parsed = try std.json.parseFromSliceLeaky(std.json.Value, arena, raw, .{});
        var out: std.ArrayList(Tab) = .empty;
        for (parsed.object.get("tabs").?.array.items) |t| {
            const p = t.object.get("path").?;
            const b = t.object.get("backup").?;
            var backup: []const u8 = "";
            if (b == .string) {
                const file = try std.fmt.allocPrint(arena, "h/sessions/{s}.backups/{s}", .{ name, b.string });
                const text = try self.read(file);
                defer gpa.free(text);
                backup = try arena.dupe(u8, text);
            }
            try out.append(arena, .{ .path = if (p == .string) p.string else "", .backup = backup });
        }
        return out.items;
    }

    fn proj(self: *const Env, rel: []const u8) ![]u8 {
        return std.fmt.allocPrint(gpa, "{s}/proj/{s}", .{ self.root, rel });
    }
};

const Tab = struct { path: []const u8, backup: []const u8 };

test "tabs and unsaved text survive a relaunch, and Don't Save is honoured" {
    var env = try Env.create("relaunch");
    defer env.destroy();
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();
    const a = arena.allocator();
    const readme = try env.proj("README.md");
    defer gpa.free(readme);
    const main_zig = try env.proj("src/main.zig");
    defer gpa.free(main_zig);
    const folder = try env.proj("");
    defer gpa.free(folder);

    try env.headless("work", &.{ folder, main_zig, readme, "--type", "EDITED ", "--action", "new_file", "--type", "scratch text" });
    const first = try env.tabs(a, "work");
    try testing.expectEqual(@as(usize, 3), first.len);
    try testing.expectEqualStrings("", first[0].backup); // main.zig is as on disk
    try testing.expect(std.mem.startsWith(u8, first[1].backup, "EDITED # Notes"));
    try testing.expectEqualStrings("", first[2].path);
    try testing.expectEqualStrings("scratch text", first[2].backup);
    // The file itself was not touched.
    const on_disk = try env.read("proj/README.md");
    defer gpa.free(on_disk);
    try testing.expectEqualStrings("# Notes\n\nline two\n", on_disk);

    // A second run with no paths restores all of it, and saves the same state back.
    try env.headless("work", &.{});
    const second = try env.tabs(a, "work");
    try testing.expectEqual(first.len, second.len);
    for (first, second) |x, y| {
        try testing.expectEqualStrings(x.path, y.path);
        try testing.expectEqualStrings(x.backup, y.backup);
    }

    // Quitting without saving drops the unsaved text but keeps the files open.
    try env.headless("work", &.{ "--action", "quit_without_saving" });
    const third = try env.tabs(a, "work");
    try testing.expectEqual(@as(usize, 2), third.len);
    for (third) |t| try testing.expectEqualStrings("", t.backup);
}

test "a file deleted since the session was saved is skipped, its unsaved text is not" {
    var env = try Env.create("gone");
    defer env.destroy();
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();
    const a = arena.allocator();
    const readme = try env.proj("README.md");
    defer gpa.free(readme);
    const util = try env.proj("src/util.zig");
    defer gpa.free(util);
    try env.write("proj/src/util.zig", "pub const x = 1;\n");

    try env.headless("s", &.{ readme, util, "--type", "kept " });
    // util.zig (the active tab, edited) and README.md (clean) vanish and change.
    try env.write("proj/README.md", "# changed behind our back\n");
    try Io.Dir.cwd().deleteFile(testing.io, util);
    try env.headless("s", &.{});
    const tabs = try env.tabs(a, "s");
    try testing.expectEqual(@as(usize, 2), tabs.len);
    try testing.expect(std.mem.startsWith(u8, tabs[1].backup, "kept pub const x"));
}

test "the session file is written while the process runs, so a crash loses nothing" {
    var env = try Env.create("crash");
    defer env.destroy();
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();
    const a = arena.allocator();
    const readme = try env.proj("README.md");
    defer gpa.free(readme);

    var argv: std.ArrayList([]const u8) = .empty;
    defer argv.deinit(gpa);
    try argv.appendSlice(gpa, &.{ opts.exe_path, "--headless", "900x600", "--no-lsp", "--session", "crash", readme, "--type", "BEFORE-THE-CRASH ", "--pump", "20000" });
    var child = try std.process.spawn(testing.io, .{ .argv = argv.items, .environ_map = &env.map, .stdin = .ignore, .stdout = .ignore, .stderr = .ignore });
    var seen = false;
    var waited: usize = 0;
    while (waited < 150 and !seen) : (waited += 1) {
        var req: std.c.timespec = .{ .sec = 0, .nsec = 100 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
        const tabs = env.tabs(a, "crash") catch continue;
        seen = tabs.len == 1 and std.mem.startsWith(u8, tabs[0].backup, "BEFORE-THE-CRASH");
    }
    _ = std.c.kill(child.id.?, std.posix.SIG.KILL);
    _ = child.wait(testing.io) catch {};
    try testing.expect(seen);

    // A new process finds the unsaved text where the dead one left it.
    try env.headless("crash", &.{});
    const tabs = try env.tabs(a, "crash");
    try testing.expectEqual(@as(usize, 1), tabs.len);
    try testing.expect(std.mem.startsWith(u8, tabs[0].backup, "BEFORE-THE-CRASH # Notes"));
}

test "a background session: list, info, save, kill, and what cannot be done while it runs" {
    var env = try Env.create("daemon");
    defer env.destroy();
    const readme = try env.proj("README.md");
    defer gpa.free(readme);
    try env.headless("work", &.{ readme, "--type", "unsaved " });

    {
        const r = try env.cs(&.{ "--daemon", "--session", "work", "--no-lsp" });
        defer r.deinit();
        try testing.expectEqual(@as(u8, 0), r.code);
    }
    // Killed at the end whatever happens.
    defer if (env.cs(&.{ "sessions", "kill", "work" })) |r| r.deinit() else |_| {};

    {
        const r = try env.cs(&.{ "sessions", "list" });
        defer r.deinit();
        try testing.expect(std.mem.indexOf(u8, r.stdout, "work") != null);
        try testing.expect(std.mem.indexOf(u8, r.stdout, "running") != null);
    }
    {
        const r = try env.cs(&.{ "sessions", "info", "work" });
        defer r.deinit();
        // ok <pid> <files> <unsaved> <folder>
        var it = std.mem.tokenizeScalar(u8, std.mem.trim(u8, r.stdout, "\n"), ' ');
        try testing.expectEqualStrings("ok", it.next().?);
        _ = it.next();
        try testing.expectEqualStrings("1", it.next().?);
        try testing.expectEqualStrings("1", it.next().?);
    }
    {
        // Another process for the same name hands the request to the running one.
        const r = try env.cs(&.{ "sessions", "save", "work" });
        defer r.deinit();
        try testing.expectEqual(@as(u8, 0), r.code);
    }
    for ([_][]const []const u8{ &.{ "sessions", "delete", "work" }, &.{ "sessions", "rename", "work", "other" } }) |args| {
        const r = try env.cs(args);
        defer r.deinit();
        try testing.expectEqual(@as(u8, 1), r.code);
        try testing.expect(std.mem.indexOf(u8, r.stderr, "is running") != null);
    }
    {
        const r = try env.cs(&.{ "sessions", "kill", "work" });
        defer r.deinit();
        try testing.expectEqual(@as(u8, 0), r.code);
    }
    {
        const r = try env.cs(&.{ "sessions", "list" });
        defer r.deinit();
        try testing.expect(std.mem.indexOf(u8, r.stdout, "saved") != null);
        try testing.expect(std.mem.indexOf(u8, r.stdout, "running") == null);
    }
    // The unsaved text outlived the process.
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();
    const tabs = try env.tabs(arena.allocator(), "work");
    try testing.expect(std.mem.startsWith(u8, tabs[0].backup, "unsaved # Notes"));

    {
        const r = try env.cs(&.{ "sessions", "rename", "work", "renamed" });
        defer r.deinit();
        try testing.expectEqual(@as(u8, 0), r.code);
    }
    const moved = try env.tabs(arena.allocator(), "renamed");
    try testing.expect(std.mem.startsWith(u8, moved[0].backup, "unsaved # Notes"));
    {
        const r = try env.cs(&.{ "sessions", "delete", "renamed" });
        defer r.deinit();
        try testing.expectEqual(@as(u8, 0), r.code);
    }
    {
        const r = try env.cs(&.{ "sessions", "list" });
        defer r.deinit();
        try testing.expect(std.mem.indexOf(u8, r.stdout, "no sessions") != null);
    }
}

test "bad session names are refused" {
    var env = try Env.create("names");
    defer env.destroy();
    for ([_][]const u8{ "a/b", ".hidden", "has space" }) |name| {
        const r = try env.cs(&.{ "--headless", "300x200", "--session", name });
        defer r.deinit();
        try testing.expect(r.code != 0);
    }
}
