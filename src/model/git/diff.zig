//! `git diff` output split into files and hunks (the Source Control view's
//! inline diffs), and the patches that stage, unstage or discard a hunk or
//! some of its lines through `git apply`, made the way magit makes them.

const std = @import("std");

const Allocator = std.mem.Allocator;

pub const Hunk = struct {
    /// "@@ -12,3 +14,5 @@ fn main() void {"
    header: []const u8,
    old_start: u32 = 0,
    new_start: u32 = 0,
    /// The lines after the header, each with its ' ', '+', '-' or '\'
    /// (combined diffs: two columns of them).
    lines: []const []const u8,
};

pub const File = struct {
    /// Relative to the repository root: the new path (a deletion's old one).
    path: []const u8,
    /// The lines before the first hunk: "diff --git", "index", "---",
    /// "+++", mode changes, "Binary files … differ".
    header: []const []const u8,
    hunks: []Hunk,
    /// "diff --cc": an unmerged file's combined diff, whose hunks can't be applied.
    combined: bool = false,

    /// Header lines worth showing under the file: mode changes and binary notes.
    pub fn isNote(line: []const u8) bool {
        return std.mem.startsWith(u8, line, "old mode ") or std.mem.startsWith(u8, line, "new mode ") or
            std.mem.startsWith(u8, line, "Binary files ") or std.mem.startsWith(u8, line, "GIT binary patch");
    }
};

/// Undoes git's C-style quoting of a path ("\"a\\tb\"" → "a<tab>b"); other
/// text is returned as is.
pub fn unquote(alloc: Allocator, s: []const u8) ![]const u8 {
    if (s.len < 2 or s[0] != '"' or s[s.len - 1] != '"') return s;
    var out: std.ArrayList(u8) = .empty;
    var i: usize = 1;
    while (i < s.len - 1) : (i += 1) {
        const ch = s[i];
        if (ch != '\\' or i + 1 >= s.len - 1) {
            try out.append(alloc, ch);
            continue;
        }
        i += 1;
        const e = s[i];
        switch (e) {
            'n' => try out.append(alloc, '\n'),
            't' => try out.append(alloc, '\t'),
            'r' => try out.append(alloc, '\r'),
            'a' => try out.append(alloc, 7),
            'b' => try out.append(alloc, 8),
            'f' => try out.append(alloc, 12),
            'v' => try out.append(alloc, 11),
            '0'...'3' => {
                // Three octal digits: a byte of a UTF-8 name.
                if (i + 2 < s.len - 1) {
                    const v = std.fmt.parseInt(u8, s[i .. i + 3], 8) catch {
                        try out.append(alloc, e);
                        continue;
                    };
                    try out.append(alloc, v);
                    i += 2;
                } else try out.append(alloc, e);
            },
            else => try out.append(alloc, e),
        }
    }
    return out.items;
}

/// The path of a "--- a/x" / "+++ b/x" line (null for /dev/null).
fn sidePath(alloc: Allocator, rest: []const u8) !?[]const u8 {
    const p = try unquote(alloc, std.mem.trimEnd(u8, rest, "\t"));
    if (std.mem.eql(u8, p, "/dev/null")) return null;
    if (std.mem.startsWith(u8, p, "a/") or std.mem.startsWith(u8, p, "b/")) return p[2..];
    return p;
}

/// The path of "diff --git a/P b/P" when both sides are the same (no
/// "---"/"+++" lines: binary files and mode changes).
fn gitLinePath(alloc: Allocator, rest: []const u8) !?[]const u8 {
    if (rest.len > 0 and rest[0] == '"') {
        const end = std.mem.indexOfPos(u8, rest, 1, "\" ") orelse return null;
        const p = try unquote(alloc, rest[0 .. end + 1]);
        return if (std.mem.startsWith(u8, p, "a/")) p[2..] else p;
    }
    if (rest.len < 5 or (rest.len - 5) % 2 != 0) return null;
    const n = (rest.len - 5) / 2;
    if (!std.mem.startsWith(u8, rest, "a/") or !std.mem.eql(u8, rest[n + 2 .. n + 5], " b/")) return null;
    if (!std.mem.eql(u8, rest[2 .. n + 2], rest[n + 5 ..])) return null;
    return rest[2 .. n + 2];
}

/// The number after `sign` in a hunk header ("+14,5" → 14).
fn headerNumber(header: []const u8, sign: u8) u32 {
    const at = std.mem.lastIndexOfScalar(u8, header[0 .. std.mem.indexOfPos(u8, header, 2, "@@") orelse header.len], sign) orelse return 0;
    var end = at + 1;
    while (end < header.len and std.ascii.isDigit(header[end])) end += 1;
    return std.fmt.parseInt(u32, header[at + 1 .. end], 10) catch 0;
}

/// Splits `git diff` output (a/ b/ prefixes) into files and hunks. Strings
/// point into `text` or `alloc`.
pub fn parse(alloc: Allocator, text: []const u8) ![]File {
    var files: std.ArrayList(File) = .empty;
    var header: std.ArrayList([]const u8) = .empty;
    var hunks: std.ArrayList(Hunk) = .empty;
    var lines: std.ArrayList([]const u8) = .empty;
    var current: ?File = null;
    var in_hunk = false;

    const body = if (text.len > 0 and text[text.len - 1] == '\n') text[0 .. text.len - 1] else text;
    var it = std.mem.splitScalar(u8, body, '\n');
    while (true) {
        const maybe = it.next();
        const l = maybe orelse "";
        const new_file = std.mem.startsWith(u8, l, "diff --git ") or std.mem.startsWith(u8, l, "diff --cc ") or std.mem.startsWith(u8, l, "diff --combined ");
        const new_hunk = std.mem.startsWith(u8, l, "@@");
        if (in_hunk and (maybe == null or new_file or new_hunk)) {
            hunks.items[hunks.items.len - 1].lines = try lines.toOwnedSlice(alloc);
            in_hunk = false;
        }
        if (current != null and (maybe == null or new_file)) {
            var f = current.?;
            f.header = try header.toOwnedSlice(alloc);
            f.hunks = try hunks.toOwnedSlice(alloc);
            if (f.path.len > 0) try files.append(alloc, f);
            current = null;
        }
        if (maybe == null) break;
        if (new_file) {
            const combined = !std.mem.startsWith(u8, l, "diff --git ");
            var path: []const u8 = "";
            if (combined) {
                path = try unquote(alloc, l[std.mem.indexOfScalarPos(u8, l, 5, ' ').? + 1 ..]);
            } else if (try gitLinePath(alloc, l["diff --git ".len..])) |p| path = p;
            current = .{ .path = path, .header = &.{}, .hunks = &.{}, .combined = combined };
            try header.append(alloc, l);
            continue;
        }
        const f = &(current orelse continue);
        if (new_hunk) {
            try hunks.append(alloc, .{ .header = l, .old_start = headerNumber(l, '-'), .new_start = headerNumber(l, '+'), .lines = &.{} });
            in_hunk = true;
            continue;
        }
        if (in_hunk) {
            try lines.append(alloc, l);
            continue;
        }
        try header.append(alloc, l);
        if (std.mem.startsWith(u8, l, "+++ ")) {
            if (try sidePath(alloc, l[4..])) |p| f.path = p;
        } else if (std.mem.startsWith(u8, l, "--- ")) {
            // A deletion has no new path; this is the old one.
            if (f.path.len == 0) if (try sidePath(alloc, l[4..])) |p| {
                f.path = p;
            };
        } else if (std.mem.startsWith(u8, l, "rename to ")) {
            f.path = try unquote(alloc, l["rename to ".len..]);
        } else if (std.mem.startsWith(u8, l, "copy to ")) {
            f.path = try unquote(alloc, l["copy to ".len..]);
        }
    }
    return files.toOwnedSlice(alloc);
}

/// The whole patch of `file` (header and every hunk), as git printed it.
pub fn filePatch(alloc: Allocator, file: *const File) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    for (file.header) |l| try out.print(alloc, "{s}\n", .{l});
    for (file.hunks) |h| {
        try out.print(alloc, "{s}\n", .{h.header});
        for (h.lines) |l| try out.print(alloc, "{s}\n", .{l});
    }
    return out.toOwnedSlice(alloc);
}

/// The header lines `git apply` needs: "diff --git", mode lines and the
/// "---"/"+++" pair (no "index" line, which a partial patch would contradict).
fn writeHeader(alloc: Allocator, out: *std.ArrayList(u8), file: *const File, whole: bool) !void {
    for (file.header) |l| {
        if (std.mem.startsWith(u8, l, "index ")) continue;
        if (!whole) {
            // Part of a new or deleted file's lines leaves the file in place.
            if (std.mem.startsWith(u8, l, "new file mode") or std.mem.startsWith(u8, l, "deleted file mode")) continue;
            if (std.mem.eql(u8, l, "--- /dev/null")) {
                try out.print(alloc, "--- a/{s}\n", .{file.path});
                continue;
            }
            if (std.mem.eql(u8, l, "+++ /dev/null")) {
                try out.print(alloc, "+++ b/{s}\n", .{file.path});
                continue;
            }
        }
        try out.print(alloc, "{s}\n", .{l});
    }
}

/// A patch of the lines [first, end) of `hunk` (indices into `hunk.lines`;
/// the whole hunk for 0, lines.len), for `git apply`. As in magit, the
/// changes left out become context or disappear depending on which side the
/// patch is applied to: forward (staging) keeps unselected removals as
/// context and drops unselected additions; `reverse` (unstaging,
/// discarding) does the opposite. Null when the range selects no change.
pub fn partialPatch(alloc: Allocator, file: *const File, hunk: *const Hunk, first: usize, end: usize, reverse: bool) !?[]u8 {
    if (file.combined) return null;
    const whole = first == 0 and end >= hunk.lines.len;
    var body: std.ArrayList(u8) = .empty;
    defer body.deinit(alloc);
    var old_count: u32 = 0;
    var new_count: u32 = 0;
    var changes: usize = 0;
    var dropped_prev = false;
    for (hunk.lines, 0..) |l, i| {
        const selected = i >= first and i < end;
        const kind: u8 = if (l.len == 0) ' ' else l[0];
        switch (kind) {
            '+', '-' => {
                const keep_as_change = selected;
                if (keep_as_change) {
                    changes += 1;
                    try body.print(alloc, "{s}\n", .{l});
                    if (kind == '+') new_count += 1 else old_count += 1;
                    dropped_prev = false;
                } else if ((kind == '-') != reverse) {
                    // Unselected and present on the side it applies to: context.
                    try body.print(alloc, " {s}\n", .{l[1..]});
                    old_count += 1;
                    new_count += 1;
                    dropped_prev = false;
                } else dropped_prev = true;
            },
            '\\' => {
                // "\ No newline at end of file" belongs to the line before.
                if (!dropped_prev) try body.print(alloc, "{s}\n", .{l});
            },
            else => {
                try body.print(alloc, " {s}\n", .{if (l.len > 0) l[1..] else l});
                old_count += 1;
                new_count += 1;
                dropped_prev = false;
            },
        }
    }
    if (changes == 0) return null;
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);
    try writeHeader(alloc, &out, file, whole);
    // A side that gains lines starts at 1 even where the hunk's was 0 (empty).
    const old_start = if (old_count > 0) @max(hunk.old_start, 1) else hunk.old_start;
    const new_start = if (new_count > 0) @max(hunk.new_start, 1) else hunk.new_start;
    if (whole) {
        try out.print(alloc, "{s}\n", .{hunk.header});
    } else try out.print(alloc, "@@ -{d},{d} +{d},{d} @@\n", .{ old_start, old_count, new_start, new_count });
    try out.appendSlice(alloc, body.items);
    return try out.toOwnedSlice(alloc);
}

// ---- Tests --------------------------------------------------------------------------

const testing = std.testing;

const sample =
    \\diff --git a/src/x.zig b/src/x.zig
    \\index 1111111..2222222 100644
    \\--- a/src/x.zig
    \\+++ b/src/x.zig
    \\@@ -10,4 +10,5 @@ fn main() void {
    \\ context ten
    \\-removed
    \\+added eleven
    \\+added twelve
    \\ context thirteen
    \\@@ -40 +41 @@
    \\-old
    \\+new
    \\\ No newline at end of file
    \\diff --git a/gone.txt b/gone.txt
    \\deleted file mode 100644
    \\index 3333333..0000000
    \\--- a/gone.txt
    \\+++ /dev/null
    \\@@ -1,2 +0,0 @@
    \\-a
    \\-b
    \\diff --git a/run.sh b/run.sh
    \\old mode 100644
    \\new mode 100755
    \\diff --git "a/odd\tname" "b/odd\tname"
    \\new file mode 100644
    \\index 0000000..4444444
    \\--- /dev/null
    \\+++ "b/odd\tname"
    \\@@ -0,0 +1 @@
    \\+x
    \\
;

test "diff parsing" {
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const files = try parse(arena.allocator(), sample);
    try testing.expectEqual(@as(usize, 4), files.len);
    try testing.expectEqualStrings("src/x.zig", files[0].path);
    try testing.expectEqual(@as(usize, 2), files[0].hunks.len);
    try testing.expectEqual(@as(usize, 4), files[0].header.len);
    try testing.expectEqual(@as(u32, 10), files[0].hunks[0].old_start);
    try testing.expectEqual(@as(u32, 41), files[0].hunks[1].new_start);
    try testing.expectEqual(@as(usize, 5), files[0].hunks[0].lines.len);
    try testing.expectEqual(@as(usize, 3), files[0].hunks[1].lines.len);
    try testing.expectEqualStrings("gone.txt", files[1].path);
    try testing.expectEqualStrings("run.sh", files[2].path);
    try testing.expectEqual(@as(usize, 0), files[2].hunks.len);
    try testing.expect(File.isNote(files[2].header[1]));
    try testing.expectEqualStrings("odd\tname", files[3].path);
    try testing.expectEqualStrings("+x", files[3].hunks[0].lines[0]);
    const whole = try filePatch(arena.allocator(), &files[0]);
    try testing.expect(std.mem.startsWith(u8, sample, whole));

    const cc = try parse(arena.allocator(), "diff --cc conflict.c\nindex 1,2..0\n--- a/conflict.c\n+++ b/conflict.c\n@@@ -1,1 -1,1 +1,5 @@@\n++<<<<<<< HEAD\n + ours\n");
    try testing.expectEqual(@as(usize, 1), cc.len);
    try testing.expect(cc[0].combined);
    try testing.expectEqualStrings("conflict.c", cc[0].path);
    try testing.expectEqual(@as(?[]u8, null), try partialPatch(arena.allocator(), &cc[0], &cc[0].hunks[0], 0, 9, false));
    try testing.expectEqual(@as(usize, 0), (try parse(arena.allocator(), "")).len);
}

test "partial patches" {
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const files = try parse(a, sample);
    const f = &files[0];
    const h = &f.hunks[0];

    // The whole hunk keeps its header (and drops the index line).
    try testing.expectEqualStrings(
        \\diff --git a/src/x.zig b/src/x.zig
        \\--- a/src/x.zig
        \\+++ b/src/x.zig
        \\@@ -10,4 +10,5 @@ fn main() void {
        \\ context ten
        \\-removed
        \\+added eleven
        \\+added twelve
        \\ context thirteen
        \\
    , (try partialPatch(a, f, h, 0, h.lines.len, false)).?);

    // Staging only "added twelve": the removal stays as context, the other addition goes.
    try testing.expectEqualStrings(
        \\diff --git a/src/x.zig b/src/x.zig
        \\--- a/src/x.zig
        \\+++ b/src/x.zig
        \\@@ -10,3 +10,4 @@
        \\ context ten
        \\ removed
        \\+added twelve
        \\ context thirteen
        \\
    , (try partialPatch(a, f, h, 3, 4, false)).?);

    // Unstaging (reverse) the removal alone: additions are context there.
    try testing.expectEqualStrings(
        \\diff --git a/src/x.zig b/src/x.zig
        \\--- a/src/x.zig
        \\+++ b/src/x.zig
        \\@@ -10,5 +10,4 @@
        \\ context ten
        \\-removed
        \\ added eleven
        \\ added twelve
        \\ context thirteen
        \\
    , (try partialPatch(a, f, h, 1, 2, true)).?);

    // Context only selects nothing.
    try testing.expect((try partialPatch(a, f, h, 0, 1, false)) == null);

    // "\ No newline" follows its line in or out.
    const h2 = &f.hunks[1];
    const keep = (try partialPatch(a, f, h2, 1, 2, false)).?;
    try testing.expect(std.mem.endsWith(u8, keep, "@@ -40,1 +41,2 @@\n old\n+new\n\\ No newline at end of file\n"));
    const drop = (try partialPatch(a, f, h2, 0, 1, false)).?;
    try testing.expect(std.mem.endsWith(u8, drop, "@@ -40,1 +41,0 @@\n-old\n"));

    // Part of a deleted file's lines: the file stays.
    const g = &files[1];
    const part = (try partialPatch(a, g, &g.hunks[0], 0, 1, false)).?;
    try testing.expectEqualStrings("diff --git a/gone.txt b/gone.txt\n--- a/gone.txt\n+++ b/gone.txt\n@@ -1,2 +1,1 @@\n-a\n b\n", part);
}

test "quoted paths" {
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try testing.expectEqualStrings("plain", try unquote(a, "plain"));
    try testing.expectEqualStrings("a\"b\\c", try unquote(a, "\"a\\\"b\\\\c\""));
    try testing.expectEqualStrings("caf\xc3\xa9", try unquote(a, "\"caf\\303\\251\""));
    try testing.expectEqualStrings("a b.txt", (try gitLinePath(a, "a/a b.txt b/a b.txt")).?);
    try testing.expect((try gitLinePath(a, "a/x b/y")) == null);
}
