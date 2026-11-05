//! Apps that Finder or the Dock start on macOS get launchd's minimal PATH
//! (/usr/bin:/bin:/usr/sbin:/sbin), so language servers from Homebrew, Nix,
//! npm or cargo aren't found and terminals start without the user's tools.
//! Like VS Code, an app started that way asks the user's login shell for
//! its PATH, then restarts itself once with it, so every child process (and
//! Zig's own PATH lookup, which reads the environment at startup) sees it.

const std = @import("std");
const builtin = @import("builtin");
const Io = std.Io;
const Allocator = std.mem.Allocator;

const log = std.log.scoped(.login_path);

/// Set in the restarted process so it doesn't ask again.
const guard_env = "CS_ZIG_LOGIN_PATH";

/// How long the login shell may take; slow shell startups get the PATH
/// launchd gave us instead.
const timeout_ms = 5000;

const marker = "_JE_PATH_";

/// On macOS, when running from an app bundle and not from a terminal,
/// restarts the process with the login shell's PATH. Returns when there's
/// nothing to do or anything fails.
pub fn adopt(gpa: Allocator, io: Io, env: *std.process.Environ.Map, args: []const [:0]const u8) void {
    if (!builtin.os.tag.isDarwin()) return;
    if (env.get(guard_env) != null) return;
    // Started from a terminal (je, cs-zig, open -a from a shell): the
    // PATH is already the shell's.
    if (env.get("TERM") != null) return;

    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const exe_n = std.process.executablePath(io, &exe_buf) catch return;
    const exe = exe_buf[0..exe_n];
    if (std.mem.indexOf(u8, exe, ".app/Contents/MacOS/") == null) return;

    const path = loginPath(gpa, io, env.get("SHELL") orelse "/bin/zsh") orelse return;
    defer gpa.free(path);
    if (env.get("PATH")) |old| if (std.mem.eql(u8, old, path)) return;

    env.put(guard_env, "1") catch return;
    env.put("PATH", path) catch return;
    var argv: std.ArrayList([]const u8) = .empty;
    defer argv.deinit(gpa);
    argv.append(gpa, exe) catch return;
    for (args[1..]) |a| argv.append(gpa, a) catch return;
    const err = std.process.replace(io, .{ .argv = argv.items, .environ_map = env });
    log.warn("restarting with the login shell's PATH failed: {t}", .{err});
}

/// Runs `shell -ilc` (interactive, so .zshrc and .bashrc count too) and
/// returns its PATH, or null if it fails or takes too long.
fn loginPath(gpa: Allocator, io: Io, shell: []const u8) ?[]u8 {
    var child = std.process.spawn(io, .{
        .argv = &.{ shell, "-ilc", "printf '\\n" ++ marker ++ "%s" ++ marker ++ "\\n' \"$PATH\"" },
        .stdin = .ignore,
        .stdout = .pipe,
        .stderr = .ignore,
    }) catch |err| {
        log.warn("cannot run {s}: {t}", .{ shell, err });
        return null;
    };
    defer child.kill(io);

    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(gpa);
    const fd = child.stdout.?.handle;
    const start = std.Io.Clock.awake.now(io);
    while (true) {
        const elapsed: i64 = @intCast(start.durationTo(std.Io.Clock.awake.now(io)).toMilliseconds());
        if (elapsed >= timeout_ms) {
            log.warn("{s} took over {d} ms to start; keeping launchd's PATH", .{ shell, timeout_ms });
            return null;
        }
        var pfd = [_]std.c.pollfd{.{ .fd = fd, .events = std.c.POLL.IN, .revents = 0 }};
        if (std.c.poll(&pfd, 1, @intCast(timeout_ms - elapsed)) <= 0) continue;
        var buf: [4096]u8 = undefined;
        const n = std.c.read(fd, &buf, buf.len);
        if (n <= 0) break;
        out.appendSlice(gpa, buf[0..@intCast(n)]) catch return null;
        if (out.items.len > 1 << 20) return null;
    }
    _ = child.wait(io) catch {};
    return parse(gpa, out.items);
}

/// The PATH between the markers in the shell's output, which may also hold
/// whatever its startup files print.
fn parse(gpa: Allocator, output: []const u8) ?[]u8 {
    const begin = (std.mem.lastIndexOf(u8, output, marker) orelse return null);
    const head = output[0..begin];
    const open = (std.mem.lastIndexOf(u8, head, marker) orelse return null) + marker.len;
    const path = head[open..];
    if (path.len == 0) return null;
    return gpa.dupe(u8, path) catch null;
}

test "parse finds the PATH between the markers" {
    const gpa = std.testing.allocator;
    const p = parse(gpa, "welcome!\n" ++ marker ++ "/opt/homebrew/bin:/usr/bin" ++ marker ++ "\n").?;
    defer gpa.free(p);
    try std.testing.expectEqualStrings("/opt/homebrew/bin:/usr/bin", p);
    try std.testing.expect(parse(gpa, "no markers here") == null);
    try std.testing.expect(parse(gpa, marker ++ marker) == null);
}
