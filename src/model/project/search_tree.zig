//! The structured form of Find in Files results: files, each with its
//! matching lines and the match ranges inside every line, for the results
//! tree. It is built from what `search.Job` returns (the Find Results text
//! and the byte ranges of every match), so local and remote searches share it.

const std = @import("std");
const search = @import("search.zig");

const Allocator = std.mem.Allocator;

/// One matching line. `preview` is the line's text (leading whitespace
/// trimmed, clipped to `max_preview`); `ranges` are byte ranges in `preview`.
pub const Match = struct {
    /// 1-based.
    line: u32,
    /// Byte column of the first match in the original line, 0-based.
    column: u32,
    /// Byte column in the original line where `preview` starts.
    preview_col: u32,
    preview: []const u8,
    ranges: []const [2]u32,
};

pub const File = struct {
    path: []const u8,
    matches: []Match,
    collapsed: bool = false,
    /// Total hits in the file (a line can hold several).
    hits: u32 = 0,
};

pub const max_preview = 300;

pub const Tree = struct {
    arena: std.heap.ArenaAllocator,
    files: []File,
    /// Matching lines in all files, and every hit.
    lines: usize,
    hits: usize,

    pub fn deinit(self: *Tree) void {
        self.arena.deinit();
    }

    /// Removes a file (the user dismissed it).
    pub fn dismissFile(self: *Tree, i: usize) void {
        self.hits -= self.files[i].hits;
        self.lines -= self.files[i].matches.len;
        std.mem.copyForwards(File, self.files[i .. self.files.len - 1], self.files[i + 1 ..]);
        self.files = self.files[0 .. self.files.len - 1];
    }

    /// Removes one matching line; the file goes when it was its last.
    pub fn dismissMatch(self: *Tree, file: usize, match: usize) void {
        const f = &self.files[file];
        self.hits -= @min(self.hits, f.matches[match].ranges.len);
        f.hits -|= @intCast(f.matches[match].ranges.len);
        self.lines -= 1;
        std.mem.copyForwards(Match, f.matches[match .. f.matches.len - 1], f.matches[match + 1 ..]);
        f.matches = f.matches[0 .. f.matches.len - 1];
        if (f.matches.len == 0) self.dismissFile(file);
    }
};

/// Builds the tree from the results `text` and the match `ranges` in it
/// (ascending byte ranges, each inside one match line).
pub fn build(gpa: Allocator, text: []const u8, ranges: []const [2]usize) !Tree {
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const alloc = arena.allocator();

    var files: std.ArrayList(File) = .empty;
    var cur_matches: std.ArrayList(Match) = .empty;
    var cur_path: ?[]const u8 = null;
    var cur_hits: u32 = 0;
    var lines: usize = 0;
    var hits: usize = 0;
    var next_range: usize = 0;

    var pos: usize = 0;
    while (pos < text.len) {
        const eol = std.mem.indexOfScalarPos(u8, text, pos, '\n') orelse text.len;
        const line = std.mem.trimEnd(u8, text[pos..eol], "\r");
        defer pos = eol + 1;
        if (line.len == 0) continue;
        if (line[0] != ' ') {
            // A file header ("/path:"); the "Searching" and summary lines don't end in a colon.
            if (line.len > 1 and line[line.len - 1] == ':') {
                try flush(alloc, &files, &cur_path, &cur_matches, &cur_hits);
                cur_path = try alloc.dupe(u8, line[0 .. line.len - 1]);
            }
            continue;
        }
        const parsed = search.parseResultLine(line) orelse continue;
        if (!parsed.is_match or cur_path == null) continue;
        const text_off = pos + parsed.text_start;
        const line_end = pos + line.len;
        // Trim leading whitespace for the preview; ranges follow.
        var lead: usize = 0;
        const body = text[@min(text_off, line_end)..line_end];
        while (lead < body.len and (body[lead] == ' ' or body[lead] == '\t')) lead += 1;
        // Skip ranges ahead of this line (they belong to a line we dropped).
        while (next_range < ranges.len and ranges[next_range][0] < text_off) next_range += 1;
        var local: std.ArrayList([2]u32) = .empty;
        const first_col: u32 = if (next_range < ranges.len and ranges[next_range][0] <= line_end)
            @intCast(ranges[next_range][0] - text_off)
        else
            0;
        // The preview is clipped to a window around the first match.
        var start = lead;
        if (first_col > lead + 40 and body.len > max_preview) start = first_col - 24;
        // Don't cut inside a UTF-8 sequence.
        while (start < body.len and start > 0 and (body[start] & 0xC0) == 0x80) start += 1;
        var end = @min(body.len, start + max_preview);
        while (end < body.len and (body[end] & 0xC0) == 0x80) end -= 1;
        while (next_range < ranges.len and ranges[next_range][0] <= line_end) : (next_range += 1) {
            const r = ranges[next_range];
            if (r[0] < text_off) continue;
            const a = r[0] - text_off;
            const b = @min(r[1] - text_off, body.len);
            if (a < start or a >= end) continue;
            try local.append(alloc, .{ @intCast(a - start), @intCast(@min(b, end) - start) });
        }
        const n_hits: u32 = @intCast(local.items.len);
        try cur_matches.append(alloc, .{
            .line = parsed.line_number,
            .column = first_col,
            .preview_col = @intCast(start),
            .preview = try alloc.dupe(u8, body[start..end]),
            .ranges = local.items,
        });
        cur_hits += n_hits;
        hits += n_hits;
        lines += 1;
    }
    try flush(alloc, &files, &cur_path, &cur_matches, &cur_hits);
    return .{ .arena = arena, .files = files.items, .lines = lines, .hits = hits };
}

fn flush(alloc: Allocator, files: *std.ArrayList(File), path: *?[]const u8, matches: *std.ArrayList(Match), hits: *u32) !void {
    if (path.*) |p| {
        if (matches.items.len > 0) try files.append(alloc, .{ .path = p, .matches = matches.items, .hits = hits.* });
    }
    path.* = null;
    matches.* = .empty;
    hits.* = 0;
}

/// Splits `path` for display: the file name, and the folder (relative to
/// `root` when it is inside it) with no trailing slash.
pub fn displayPath(path: []const u8, root: ?[]const u8) struct { name: []const u8, folder: []const u8 } {
    const name = std.fs.path.basename(path);
    var dir = std.fs.path.dirname(path) orelse "";
    if (root) |r| if (std.mem.startsWith(u8, dir, r) and (dir.len == r.len or dir[r.len] == '/')) {
        dir = std.mem.trimStart(u8, dir[r.len..], "/");
    };
    return .{ .name = name, .folder = dir };
}

/// VS Code's "preserve case": a replacement takes the case shape of the
/// text it replaces: all upper case, all lower case or Capitalised.
pub fn preserveCase(out: []u8, matched: []const u8, replacement: []const u8) []const u8 {
    if (replacement.len > out.len) return replacement;
    @memcpy(out[0..replacement.len], replacement);
    const r = out[0..replacement.len];
    var letters: usize = 0;
    var upper: usize = 0;
    var lower: usize = 0;
    for (matched) |ch| {
        if (std.ascii.isUpper(ch)) {
            upper += 1;
            letters += 1;
        } else if (std.ascii.isLower(ch)) {
            lower += 1;
            letters += 1;
        }
    }
    if (letters == 0) return r;
    if (upper == letters and letters > 1) {
        for (r) |*ch| ch.* = std.ascii.toUpper(ch.*);
    } else if (lower == letters) {
        for (r) |*ch| ch.* = std.ascii.toLower(ch.*);
    } else if (std.ascii.isUpper(matched[0]) and r.len > 0) {
        r[0] = std.ascii.toUpper(r[0]);
    }
    return r;
}

const testing = std.testing;

test "a tree groups matching lines by file with ranges in the preview" {
    const text =
        "Searching 2 files for \"foo\"\n\n" ++
        "/p/a.zig:\n" ++
        "     3  context\n" ++
        "     4:     const foo = foo + 1;\n" ++
        "     5  more\n" ++
        "  ..\n" ++
        "    20: pub fn Foo() void {}\n" ++
        "\n" ++
        "/p/b.zig:\n" ++
        "     1: foo\n" ++
        "\n" ++
        "3 matches across 2 files\n";
    var ranges: std.ArrayList([2]usize) = .empty;
    defer ranges.deinit(testing.allocator);
    // Only the match lines count (the "Searching" line has foo too).
    var at: usize = 30;
    while (at + 3 <= text.len) : (at += 1) {
        if (std.ascii.eqlIgnoreCase(text[at .. at + 3], "foo")) try ranges.append(testing.allocator, .{ at, at + 3 });
    }
    var tree = try build(testing.allocator, text, ranges.items);
    defer tree.deinit();
    try testing.expectEqual(@as(usize, 2), tree.files.len);
    try testing.expectEqualStrings("/p/a.zig", tree.files[0].path);
    try testing.expectEqual(@as(usize, 2), tree.files[0].matches.len);
    try testing.expectEqual(@as(u32, 4), tree.files[0].matches[0].line);
    try testing.expectEqualStrings("const foo = foo + 1;", tree.files[0].matches[0].preview);
    try testing.expectEqual(@as(usize, 2), tree.files[0].matches[0].ranges.len);
    try testing.expectEqual(@as(u32, 4), tree.files[0].matches[0].preview_col);
    try testing.expectEqual([2]u32{ 6, 9 }, tree.files[0].matches[0].ranges[0]);
    try testing.expectEqual([2]u32{ 12, 15 }, tree.files[0].matches[0].ranges[1]);
    try testing.expectEqual(@as(u32, 20), tree.files[0].matches[1].line);
    try testing.expectEqual(@as(u32, 3), tree.files[0].hits);
    try testing.expectEqual(@as(usize, 3), tree.lines);
    try testing.expectEqual(@as(usize, 4), tree.hits);
}

test "dismissing a match or file keeps the totals" {
    const text = "/a:\n     1: x\n     2: x\n\n/b:\n     1: x\n";
    const ranges = [_][2]usize{ .{ 12, 13 }, .{ 22, 23 }, .{ 37, 38 } };
    var tree = try build(testing.allocator, text, &ranges);
    defer tree.deinit();
    try testing.expectEqual(@as(usize, 3), tree.hits);
    tree.dismissMatch(0, 0);
    try testing.expectEqual(@as(usize, 2), tree.hits);
    try testing.expectEqual(@as(usize, 1), tree.files[0].matches.len);
    tree.dismissMatch(0, 0);
    try testing.expectEqual(@as(usize, 1), tree.files.len);
    try testing.expectEqualStrings("/b", tree.files[0].path);
    tree.dismissFile(0);
    try testing.expectEqual(@as(usize, 0), tree.files.len);
    try testing.expectEqual(@as(usize, 0), tree.hits);
}

test "long lines are clipped around the first match without splitting characters" {
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(testing.allocator);
    try line.appendSlice(testing.allocator, "/f:\n     7: ");
    const text_start = line.items.len;
    for (0..200) |_| try line.appendSlice(testing.allocator, "ü");
    try line.appendSlice(testing.allocator, "needle");
    for (0..200) |_| try line.appendSlice(testing.allocator, "é");
    try line.append(testing.allocator, '\n');
    const at = text_start + 400;
    var tree = try build(testing.allocator, line.items, &.{.{ at, at + 6 }});
    defer tree.deinit();
    const m = tree.files[0].matches[0];
    try testing.expect(m.preview.len <= max_preview);
    try testing.expect(std.unicode.utf8ValidateSlice(m.preview));
    try testing.expectEqualStrings("needle", m.preview[m.ranges[0][0]..m.ranges[0][1]]);
}

test "display paths are relative to the project root" {
    const d = displayPath("/proj/src/app/x.zig", "/proj");
    try testing.expectEqualStrings("x.zig", d.name);
    try testing.expectEqualStrings("src/app", d.folder);
    try testing.expectEqualStrings("/elsewhere", displayPath("/elsewhere/y", "/proj").folder);
}

test "preserve case follows the matched text" {
    var buf: [64]u8 = undefined;
    try testing.expectEqualStrings("BAR", preserveCase(&buf, "FOO", "bar"));
    try testing.expectEqualStrings("bar", preserveCase(&buf, "foo", "Bar"));
    try testing.expectEqualStrings("Bar", preserveCase(&buf, "Foo", "bar"));
    try testing.expectEqualStrings("bAr", preserveCase(&buf, "fOo", "bAr"));
    try testing.expectEqualStrings("Bar", preserveCase(&buf, "123", "Bar"));
}
