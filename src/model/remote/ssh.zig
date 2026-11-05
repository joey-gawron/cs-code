//! The ssh side of Remote-SSH: command lines that launch jiffy-edit-server on
//! a host (deploying it first when the host lacks this exact build), and the
//! host list for "Remote: Connect to Host…" from ~/.ssh/config.
//!
//! Everything goes through the system `ssh`, so ~/.ssh/config, agents,
//! ProxyJump and ControlMaster work as in a terminal (a shared master asks
//! for its password once). Passwords, key passphrases and unknown host keys
//! are asked in the editor through SSH_ASKPASS (askpass.zig); a connect that
//! blocks its caller can't show them, so it runs in BatchMode instead.

const std = @import("std");
const protocol = @import("protocol.zig");
const glob = @import("../project/search.zig").glob;

const Allocator = std.mem.Allocator;
const Io = std.Io;

/// Options for every ssh invocation (after the program name).
pub const base_args = [_][]const u8{
    "-T", // no tty: the channel carries binary frames
    "-e", "none", // and no escape character
    "-o", "ConnectTimeout=15",
    "-o", "ServerAliveInterval=15",
    "-o", "ServerAliveCountMax=3",
};

/// Added when nobody can answer ssh's questions: fail at once instead.
pub const batch_args = [_][]const u8{ "-o", "BatchMode=yes" };

/// A server binary the editor can upload, for one architecture.
pub const Binary = struct {
    /// Zig's arch name ("x86_64", "aarch64").
    arch: []const u8,
    path: []const u8,
};

/// Install directory name on the remote: protocol version + content hash, so
/// every distinct build gets its own directory (like VS Code's commit id).
pub fn versionTag(buf: *[24]u8, data: []const u8) []const u8 {
    return std.fmt.bufPrint(buf, "p{d}-{x:0>16}", .{ protocol.protocol_version, std.hash.Wyhash.hash(0, data) }) catch unreachable;
}

pub const ArchTag = struct { arch: []const u8, tag: []const u8 };

/// The remote command that starts the installed server for the host's
/// architecture. If it isn't there, the script prints "JIFFY-EDIT-MISSING
/// <uname -m>" and waits for the build on stdin, in the same ssh session (so
/// a password is asked once): an `installHeader` line, then exactly that many
/// bytes. It installs them as ~/.jiffy-edit-server/<tag>/jiffy-edit-server (a temp
/// file, checked for size, then renamed) and execs it; on EOF or a short
/// upload it exits without installing anything.
/// Wrapped in `sh -c '…'` so it works whatever the login shell is.
pub fn launchCommand(gpa: Allocator, tags: []const ArchTag) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    try out.appendSlice(gpa, "sh -c 'case \"$(uname -m)\" in ");
    for (tags) |t| {
        const patterns = if (std.mem.eql(u8, t.arch, "x86_64")) "x86_64|amd64" else if (std.mem.eql(u8, t.arch, "aarch64")) "aarch64|arm64" else t.arch;
        try out.print(gpa, "{s}) v={s};; ", .{ patterns, t.tag });
    }
    // `read` takes stdin one byte at a time and `head -c` stops at the size,
    // so what follows the binary (the client's frames) reaches the server.
    try out.print(gpa,
        \\*) v=none;; esac; f="$HOME/.jiffy-edit-server/$v/jiffy-edit-server"; if [ -x "$f" ]; then exec "$f" --stdio; fi; echo "{s} $(uname -m)"; read -r v n || exit 1; case "$v" in ""|*[!0-9a-z-]*) exit 1;; esac; case "$n" in ""|*[!0-9]*) exit 1;; esac; d="$HOME/.jiffy-edit-server/$v"; f="$d/jiffy-edit-server"; mkdir -p "$d" && head -c "$n" > "$f.$$" && [ $(wc -c < "$f.$$") -eq "$n" ] && chmod 755 "$f.$$" && mv -f "$f.$$" "$f" && exec "$f" --stdio; rm -f "$f.$$"; echo "Cannot install jiffy-edit-server in $d" >&2; exit 1'
    , .{protocol.missing_magic});
    return out.toOwnedSlice(gpa);
}

/// The line the client sends after "JIFFY-EDIT-MISSING", before the binary.
pub fn installHeader(buf: []u8, tag: []const u8, size: usize) ![]const u8 {
    return std.fmt.bufPrint(buf, "{s} {d}\n", .{ tag, size });
}

/// Host aliases from ssh config text (patterns with wildcards are skipped);
/// `include` receives each Include argument. Strings point into `text`.
pub fn parseConfig(alloc: Allocator, text: []const u8, hosts: *std.ArrayList([]const u8), includes: *std.ArrayList([]const u8)) !void {
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |raw| {
        const line = std.mem.trim(u8, raw, " \t\r");
        if (line.len == 0 or line[0] == '#') continue;
        const sep = std.mem.indexOfAny(u8, line, " \t=") orelse continue;
        const key = line[0..sep];
        var rest = std.mem.trimStart(u8, line[sep..], " \t=");
        if (std.ascii.eqlIgnoreCase(key, "Include")) {
            var it = std.mem.tokenizeAny(u8, rest, " \t");
            while (it.next()) |inc| try includes.append(alloc, inc);
        } else if (std.ascii.eqlIgnoreCase(key, "Host")) {
            if (std.mem.indexOfScalar(u8, rest, '#')) |hash| rest = rest[0..hash];
            var it = std.mem.tokenizeAny(u8, rest, " \t");
            while (it.next()) |h| {
                if (std.mem.indexOfAny(u8, h, "*?!") != null) continue;
                for (hosts.items) |seen| {
                    if (std.mem.eql(u8, seen, h)) break;
                } else try hosts.append(alloc, h);
            }
        }
    }
}

/// Host aliases from ~/.ssh/config and the files it includes (one level of
/// `Include`, with `*` in the file name). Allocated in `alloc`.
pub fn configHosts(alloc: Allocator, io: Io, home: []const u8) ![]const []const u8 {
    var hosts: std.ArrayList([]const u8) = .empty;
    var includes: std.ArrayList([]const u8) = .empty;
    const ssh_dir = try std.fs.path.join(alloc, &.{ home, ".ssh" });
    const main_path = try std.fs.path.join(alloc, &.{ ssh_dir, "config" });
    const text = Io.Dir.cwd().readFileAlloc(io, main_path, alloc, .limited(1 << 20)) catch return hosts.items;
    try parseConfig(alloc, text, &hosts, &includes);
    var ignored: std.ArrayList([]const u8) = .empty;
    for (includes.items) |raw| {
        const inc = if (std.mem.startsWith(u8, raw, "~/"))
            try std.fs.path.join(alloc, &.{ home, raw[2..] })
        else if (raw[0] == '/') raw else try std.fs.path.join(alloc, &.{ ssh_dir, raw });
        const dir_path = std.fs.path.dirname(inc) orelse continue;
        const pattern = std.fs.path.basename(inc);
        var dir = Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch continue;
        defer dir.close(io);
        var it = dir.iterate();
        while (it.next(io) catch null) |e| {
            if (!glob(pattern, e.name)) continue;
            const sub = dir.readFileAlloc(io, e.name, alloc, .limited(1 << 20)) catch continue;
            try parseConfig(alloc, sub, &hosts, &ignored);
        }
    }
    return hosts.items;
}

test "ssh config hosts" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();
    var hosts: std.ArrayList([]const u8) = .empty;
    var includes: std.ArrayList([]const u8) = .empty;
    try parseConfig(alloc,
        \\# comment
        \\Include config.d/*
        \\Host devbox build-*  # dev machines
        \\    HostName 10.0.0.2
        \\host=pi
        \\Host *
        \\  ServerAliveInterval 30
        \\Host devbox !bad
    , &hosts, &includes);
    try std.testing.expectEqual(@as(usize, 2), hosts.items.len);
    try std.testing.expectEqualStrings("devbox", hosts.items[0]);
    try std.testing.expectEqualStrings("pi", hosts.items[1]);
    try std.testing.expectEqualStrings("config.d/*", includes.items[0]);
}

test "launch command" {
    const gpa = std.testing.allocator;
    var buf: [24]u8 = undefined;
    const tag = versionTag(&buf, "binary");
    try std.testing.expect(std.mem.startsWith(u8, tag, "p1-"));
    const launch = try launchCommand(gpa, &.{.{ .arch = "x86_64", .tag = tag }});
    defer gpa.free(launch);
    try std.testing.expect(std.mem.startsWith(u8, launch, "sh -c '"));
    try std.testing.expect(std.mem.endsWith(u8, launch, "'"));
    // The script is single-quoted as a whole: it must not contain quotes itself.
    try std.testing.expectEqual(@as(usize, 2), std.mem.count(u8, launch, "'"));
    try std.testing.expect(std.mem.indexOf(u8, launch, "x86_64|amd64) v=p1-") != null);
    var line: [64]u8 = undefined;
    try std.testing.expectEqualStrings("p1-0 42\n", try installHeader(&line, "p1-0", 42));
}
