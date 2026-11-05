//! Moving subtrees: refile (C-c C-w) under another heading, here or in
//! another org file, and archive (C-c $) to the archive file.

const std = @import("std");
const parser = @import("parser.zig");

const Allocator = std.mem.Allocator;

pub const Range = struct { start: usize, end: usize };

/// The subtree whose heading line starts at `at`: through the line before
/// the next heading of the same or a higher level (or the end), with its
/// final newline.
pub fn subtree(text: []const u8, at: usize) ?Range {
    const first_end = std.mem.indexOfScalarPos(u8, text, at, '\n') orelse text.len;
    const head = parser.parseHeadingLine(text[at..first_end], .{}) orelse return null;
    var pos = if (first_end < text.len) first_end + 1 else text.len;
    while (pos < text.len) {
        const nl = std.mem.indexOfScalarPos(u8, text, pos, '\n') orelse text.len;
        if (parser.parseHeadingLine(text[pos..nl], .{})) |h| if (h.level <= head.level) break;
        pos = if (nl < text.len) nl + 1 else text.len;
    }
    return .{ .start = at, .end = pos };
}

/// `sub` with every heading moved so the first is at `level`.
pub fn relevel(gpa: Allocator, sub: []const u8, level: usize) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var first_level: ?usize = null;
    var lines = std.mem.splitScalar(u8, sub, '\n');
    var first = true;
    while (lines.next()) |line| {
        if (!first) try out.append(gpa, '\n');
        first = false;
        if (parser.parseHeadingLine(line, .{})) |h| {
            const base = first_level orelse blk: {
                first_level = h.level;
                break :blk h.level;
            };
            const new_level: usize = @max(1, @as(isize, @intCast(h.level)) - @as(isize, @intCast(base)) + @as(isize, @intCast(level)));
            try out.appendNTimes(gpa, '*', new_level);
            try out.appendSlice(gpa, line[h.level..]);
        } else try out.appendSlice(gpa, line);
    }
    return out.toOwnedSlice(gpa);
}

/// A heading as a refile target: its outline path ("A/B/C") and where it is.
pub const Target = struct {
    /// Titles from the top, joined by '/'.
    path: []const u8,
    level: u8,
    offset: usize,
};

/// Every heading of `text` with its outline path (in `alloc`).
pub fn targets(alloc: Allocator, text: []const u8) ![]Target {
    var out: std.ArrayList(Target) = .empty;
    var stack: std.ArrayList([]const u8) = .empty;
    var levels: std.ArrayList(u8) = .empty;
    var pos: usize = 0;
    while (pos < text.len) {
        const nl = std.mem.indexOfScalarPos(u8, text, pos, '\n') orelse text.len;
        if (parser.parseHeadingLine(text[pos..nl], .{})) |h| {
            while (levels.items.len > 0 and levels.items[levels.items.len - 1] >= h.level) {
                _ = levels.pop();
                _ = stack.pop();
            }
            try stack.append(alloc, h.title);
            try levels.append(alloc, h.level);
            try out.append(alloc, .{ .path = try std.mem.join(alloc, "/", stack.items), .level = h.level, .offset = pos });
        }
        pos = nl + 1;
    }
    return out.items;
}

/// The heading at outline path `path` in `text`, as it is now.
pub fn find(alloc: Allocator, text: []const u8, path: []const u8) !?Target {
    for (try targets(alloc, text)) |t| if (std.mem.eql(u8, t.path, path)) return t;
    return null;
}

pub const Insertion = struct { offset: usize, text: []u8 };

/// Where and what to insert to put `sub` as the last child of `under`, or
/// at the end of the file at the top level without one.
pub fn insertion(gpa: Allocator, text: []const u8, under: ?Target, sub: []const u8) !Insertion {
    const level: usize = if (under) |u| u.level + 1 else 1;
    const moved = try relevel(gpa, std.mem.trimEnd(u8, sub, "\n"), level);
    defer gpa.free(moved);
    const at = if (under) |u| (subtree(text, u.offset) orelse Range{ .start = u.offset, .end = text.len }).end else text.len;
    // On lines of its own.
    const need_nl = at > 0 and text[at - 1] != '\n';
    const out = try std.mem.concat(gpa, u8, &.{ if (need_nl) "\n" else "", moved, "\n" });
    return .{ .offset = at, .text = out };
}

/// The archive file for `path` and the heading to file under (empty for
/// the top level): from `#+ARCHIVE: file::heading` ("%s" is the file's
/// name), else org's default "%s_archive::".
pub fn archiveLocation(alloc: Allocator, text: []const u8, path: []const u8) !struct { path: []const u8, heading: []const u8 } {
    var spec: []const u8 = "%s_archive::";
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |line| {
        if (std.ascii.startsWithIgnoreCase(line, "#+ARCHIVE:")) spec = std.mem.trim(u8, line["#+ARCHIVE:".len..], " \t\r");
    }
    const sep = std.mem.indexOf(u8, spec, "::");
    const file_part = if (sep) |s| spec[0..s] else spec;
    const heading = if (sep) |s| std.mem.trim(u8, spec[s + 2 ..], " *") else "";
    const name = std.fs.path.basename(path);
    const file = try std.mem.replaceOwned(u8, alloc, if (file_part.len == 0) "%s" else file_part, "%s", name);
    const dir = std.fs.path.dirname(path) orelse ".";
    const abs = if (std.fs.path.isAbsolute(file)) file else try std.fs.path.join(alloc, &.{ dir, file });
    return .{ .path = abs, .heading = heading };
}

/// `sub` (a subtree's text) with org's archive properties added to its
/// heading's property drawer.
pub fn archived(gpa: Allocator, sub: []const u8, props: struct { time: []const u8, file: []const u8, olpath: []const u8, category: []const u8, todo: []const u8 }) ![]u8 {
    const nl = std.mem.indexOfScalar(u8, sub, '\n') orelse sub.len;
    const head = parser.parseHeadingLine(sub[0..nl], .{}) orelse return gpa.dupe(u8, sub);
    var indent_buf: [32]u8 = undefined;
    const indent = indent_buf[0..@min(head.level + 1, indent_buf.len)];
    @memset(indent, ' ');
    // After the planning line and into an existing drawer.
    var pos = if (nl < sub.len) nl + 1 else sub.len;
    const next_nl = std.mem.indexOfScalarPos(u8, sub, pos, '\n') orelse sub.len;
    const next = std.mem.trimStart(u8, sub[pos..next_nl], " \t");
    if (std.mem.startsWith(u8, next, "SCHEDULED:") or std.mem.startsWith(u8, next, "DEADLINE:") or std.mem.startsWith(u8, next, "CLOSED:")) {
        pos = if (next_nl < sub.len) next_nl + 1 else sub.len;
    }
    var drawer: std.ArrayList(u8) = .empty;
    defer drawer.deinit(gpa);
    const line_end = std.mem.indexOfScalarPos(u8, sub, pos, '\n') orelse sub.len;
    const has_drawer = std.ascii.eqlIgnoreCase(std.mem.trim(u8, sub[pos..line_end], " \t\r"), ":PROPERTIES:");
    if (has_drawer) pos = if (line_end < sub.len) line_end + 1 else sub.len else try drawer.print(gpa, "{s}:PROPERTIES:\n", .{indent});
    inline for (.{ .{ "ARCHIVE_TIME", "time" }, .{ "ARCHIVE_FILE", "file" }, .{ "ARCHIVE_OLPATH", "olpath" }, .{ "ARCHIVE_CATEGORY", "category" }, .{ "ARCHIVE_TODO", "todo" } }) |kv| {
        const value = @field(props, kv[1]);
        if (value.len > 0) try drawer.print(gpa, "{s}:{s}: {s}\n", .{ indent, kv[0], value });
    }
    if (!has_drawer) try drawer.print(gpa, "{s}:END:\n", .{indent});
    // Before the drawer's contents or the body: the text up to `pos`
    // needs its final newline.
    const head_part = sub[0..pos];
    const glue: []const u8 = if (head_part.len > 0 and head_part[head_part.len - 1] != '\n') "\n" else "";
    return std.mem.concat(gpa, u8, &.{ head_part, glue, drawer.items, sub[pos..] });
}

test "subtree and relevel" {
    const gpa = std.testing.allocator;
    const text = "* A\n** B\nbody\n*** C\n** D\n* E\n";
    const b = subtree(text, 4).?;
    try std.testing.expectEqualStrings("** B\nbody\n*** C\n", text[b.start..b.end]);
    const moved = try relevel(gpa, text[b.start..b.end], 1);
    defer gpa.free(moved);
    try std.testing.expectEqualStrings("* B\nbody\n** C\n", moved);
}

test "targets and insertion" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const text = "* Work\n** Projects\n** Inbox\n* Home\n";
    const ts = try targets(a, text);
    try std.testing.expectEqual(@as(usize, 4), ts.len);
    try std.testing.expectEqualStrings("Work/Projects", ts[1].path);
    const proj = (try find(a, text, "Work/Projects")).?;
    const ins = try insertion(a, text, proj, "* TODO Task\n  body\n");
    const out = try std.mem.concat(a, u8, &.{ text[0..ins.offset], ins.text, text[ins.offset..] });
    try std.testing.expectEqualStrings("* Work\n** Projects\n*** TODO Task\n  body\n** Inbox\n* Home\n", out);
    const top = try insertion(a, "* X", null, "*** Y\n");
    try std.testing.expectEqualStrings("\n* Y\n", top.text);
}

test "archive location and properties" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const loc = try archiveLocation(a, "* x\n", "/notes/work.org");
    try std.testing.expectEqualStrings("/notes/work.org_archive", loc.path);
    try std.testing.expectEqualStrings("", loc.heading);
    const loc2 = try archiveLocation(a, "#+ARCHIVE: archive/%s::* Done\n", "/notes/work.org");
    try std.testing.expectEqualStrings("/notes/archive/work.org", loc2.path);
    try std.testing.expectEqualStrings("Done", loc2.heading);
    const out = try archived(a, "** DONE Task\n   CLOSED: [2026-10-22 Thu]\nbody\n", .{ .time = "2026-10-22 Thu 14:00", .file = "/notes/work.org", .olpath = "Work", .category = "work", .todo = "DONE" });
    try std.testing.expectEqualStrings(
        \\** DONE Task
        \\   CLOSED: [2026-10-22 Thu]
        \\   :PROPERTIES:
        \\   :ARCHIVE_TIME: 2026-10-22 Thu 14:00
        \\   :ARCHIVE_FILE: /notes/work.org
        \\   :ARCHIVE_OLPATH: Work
        \\   :ARCHIVE_CATEGORY: work
        \\   :ARCHIVE_TODO: DONE
        \\   :END:
        \\body
        \\
    , out);
}
