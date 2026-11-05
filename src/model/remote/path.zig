//! Remote paths: `ssh://host/abs/path`, where `host` is whatever `ssh`
//! accepts as a destination (an ~/.ssh/config alias, `user@host`, or
//! `user@host:port`). Buffers, projects and Find Results all carry this form,
//! so the path alone says which machine a file lives on. The usual
//! `std.fs.path` helpers keep working on it (`basename`, `dirname`, `join`),
//! with one exception: it isn't `isAbsolute`, so callers check `isRemote`.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const scheme = "ssh://";

pub const Remote = struct {
    /// The destination for ssh ("devbox", "me@devbox:2222").
    host: []const u8,
    /// Absolute path on that machine (at least "/").
    path: []const u8,
};

pub fn isRemote(path: []const u8) bool {
    return std.mem.startsWith(u8, path, scheme) and path.len > scheme.len;
}

/// Splits `ssh://host/abs` into host and path; null for local paths.
pub fn parse(path: []const u8) ?Remote {
    if (!isRemote(path)) return null;
    const rest = path[scheme.len..];
    const slash = std.mem.indexOfScalar(u8, rest, '/') orelse return .{ .host = rest, .path = "/" };
    if (slash == 0) return null;
    return .{ .host = rest[0..slash], .path = rest[slash..] };
}

/// "ssh://host" for a remote path (the prefix to put back on remote results).
pub fn authority(path: []const u8) ?[]const u8 {
    const r = parse(path) orelse return null;
    return path[0 .. scheme.len + r.host.len];
}

pub fn host(path: []const u8) ?[]const u8 {
    return (parse(path) orelse return null).host;
}

/// Builds `ssh://host/path` (a relative `path` is made absolute).
pub fn format(gpa: Allocator, host_name: []const u8, path: []const u8) ![]u8 {
    const sep: []const u8 = if (path.len > 0 and path[0] == '/') "" else "/";
    return std.fmt.allocPrint(gpa, "{s}{s}{s}{s}", .{ scheme, host_name, sep, path });
}

/// Both local, or both on the same host.
pub fn sameMachine(a: []const u8, b: []const u8) bool {
    const ha = host(a);
    const hb = host(b);
    if (ha == null or hb == null) return ha == null and hb == null;
    return std.mem.eql(u8, ha.?, hb.?);
}

/// Expands a leading "~" of a remote path against the remote home. "/~"
/// counts too: that's how ssh://host/~/x (and `--remote host x`) parse.
pub fn expandHome(gpa: Allocator, path: []const u8, home: []const u8) ![]u8 {
    const rest = if (std.mem.startsWith(u8, path, "/~")) path[1..] else path;
    if (rest.len > 0 and rest[0] == '~' and (rest.len == 1 or rest[1] == '/')) {
        return std.fmt.allocPrint(gpa, "{s}{s}", .{ std.mem.trimEnd(u8, home, "/"), rest[1..] });
    }
    return gpa.dupe(u8, path);
}

/// The argument for `ssh`: a plain destination, or the URI form when a port
/// is part of it (OpenSSH understands `ssh://user@host:port`).
pub fn sshDestination(buf: []u8, host_name: []const u8) []const u8 {
    if (std.mem.indexOfScalar(u8, host_name, ':') == null) return host_name;
    return std.fmt.bufPrint(buf, "{s}{s}", .{ scheme, host_name }) catch host_name;
}

test "parse and format remote paths" {
    const r = parse("ssh://devbox/home/me/src/main.zig").?;
    try std.testing.expectEqualStrings("devbox", r.host);
    try std.testing.expectEqualStrings("/home/me/src/main.zig", r.path);
    try std.testing.expectEqualStrings("/", parse("ssh://me@devbox:2222").?.path);
    try std.testing.expectEqualStrings("me@devbox:2222", parse("ssh://me@devbox:2222/x").?.host);
    try std.testing.expect(parse("/home/me") == null);
    try std.testing.expect(parse("ssh://") == null);
    try std.testing.expect(parse("ssh:///abs") == null);
    try std.testing.expectEqualStrings("ssh://devbox", authority("ssh://devbox/a/b").?);

    const gpa = std.testing.allocator;
    const p = try format(gpa, "devbox", "/srv/app");
    defer gpa.free(p);
    try std.testing.expectEqualStrings("ssh://devbox/srv/app", p);
    // std.fs.path keeps working on the remote form.
    try std.testing.expectEqualStrings("app", std.fs.path.basename(p));
    try std.testing.expectEqualStrings("ssh://devbox/srv", std.fs.path.dirname(p).?);
    const joined = try std.fs.path.join(gpa, &.{ p, "src/x.zig" });
    defer gpa.free(joined);
    try std.testing.expectEqualStrings("ssh://devbox/srv/app/src/x.zig", joined);

    try std.testing.expect(sameMachine("/a", "/b"));
    try std.testing.expect(sameMachine("ssh://h/a", "ssh://h/b"));
    try std.testing.expect(!sameMachine("ssh://h/a", "/a"));
    try std.testing.expect(!sameMachine("ssh://h/a", "ssh://g/a"));

    for ([_][2][]const u8{
        .{ "~/proj", "/home/me/proj" },
        .{ "/~/proj", "/home/me/proj" },
        .{ "/~", "/home/me" },
        .{ "/~x/proj", "/~x/proj" },
        .{ "/srv/~/x", "/srv/~/x" },
    }) |c| {
        const e = try expandHome(gpa, c[0], "/home/me/");
        defer gpa.free(e);
        try std.testing.expectEqualStrings(c[1], e);
    }
    var buf: [64]u8 = undefined;
    try std.testing.expectEqualStrings("devbox", sshDestination(&buf, "devbox"));
    try std.testing.expectEqualStrings("ssh://me@h:2222", sshDestination(&buf, "me@h:2222"));
}
