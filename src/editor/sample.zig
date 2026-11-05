//! Built-in documents: a sample that exercises the awkward cases, and synthetic buffers of any
//! size for measuring scrolling and typing. The same documents as the Rust spike's `sample.rs`,
//! plus `longLine`, which the Rust side measured by hand.

const std = @import("std");
const Allocator = std.mem.Allocator;

/// A short document with ASCII, CJK, emoji (surrogate pairs in UTF-16), a tab, an empty line and
/// one very long line.
pub fn sampleText(gpa: Allocator) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    const head = [_][]const u8{
        "// cs-zig: Zig editor spike (hand-rolled GL renderer)",
        "fn main() {",
        "\tlet greeting = \"hello\"; // indented with a tab",
        "    let cjk = \"日本語のテキスト 中文 한국어\";",
        "    let emoji = \"😀 🚀 👍🏽 family: 👨‍👩‍👧\"; // surrogate pairs",
        "",
        "    println!(\"{greeting} {cjk} {emoji}\");",
    };
    for (head) |line| {
        try out.appendSlice(gpa, line);
        try out.append(gpa, '\n');
    }
    try out.appendSlice(gpa, "// A long line for horizontal scrolling: ");
    for (0..70) |i| {
        if (i > 0) try out.append(gpa, ' ');
        try out.print(gpa, "item{d:0>2}", .{i});
    }
    try out.appendSlice(gpa, " <- end of the long line\n");
    const tail = [_][]const u8{
        "}",
        "",
        "Arrows step over characters (code points here, not grapheme clusters): try 😀😀😀, 👍🏽 and 日本語.",
        "Up/down keep a preferred column; home/end, pageup/pagedown, cmd-up/cmd-down.",
    };
    for (tail, 0..) |line, i| {
        try out.appendSlice(gpa, line);
        if (i + 1 < tail.len) try out.append(gpa, '\n');
    }
    return out.toOwnedSlice(gpa);
}

/// `lines` lines of code-like text, with no trailing newline so the buffer has exactly `lines`
/// lines. Line lengths vary so horizontal extents differ from row to row.
pub fn generate(gpa: Allocator, lines: usize) ![]u8 {
    var out: std.ArrayList(u8) = try .initCapacity(gpa, lines * 64);
    errdefer out.deinit(gpa);
    const padding = "xxxxxxxxxxxxxxxxxxxxxxx";
    for (0..lines) |i| {
        if (i > 0) try out.append(gpa, '\n');
        const value = (i *% 7919) % 100_000;
        try out.print(gpa, "fn line_{d}() -> u64 {{ let value = {d}; value * 2 }} // {s}", .{ i, value, padding[0 .. i % 23] });
    }
    return out.toOwnedSlice(gpa);
}

/// One line of `chars` Unicode scalar values: ASCII, CJK, Hangul and emoji, including a skin-tone
/// sequence and a ZWJ family, the mix the Rust spike's 171,000-character typing number used.
pub fn longLine(gpa: Allocator, chars: usize) ![]u8 {
    const unit = "日本語のテキスト 😀 ascii text 中文 한국어 🚀 👍🏽 👨‍👩‍👧 ";
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var n: usize = 0;
    outer: while (true) {
        var it = std.unicode.Utf8View.initUnchecked(unit).iterator();
        while (it.nextCodepointSlice()) |cp| {
            if (n == chars) break :outer;
            try out.appendSlice(gpa, cp);
            n += 1;
        }
    }
    return out.toOwnedSlice(gpa);
}

test "generated buffers have exactly the requested lines" {
    const gpa = std.testing.allocator;
    for ([_]usize{ 1, 1000 }) |n| {
        const text = try generate(gpa, n);
        defer gpa.free(text);
        try std.testing.expectEqual(n - 1, std.mem.count(u8, text, "\n"));
    }
}

test "the long line has the requested number of code points and no newline" {
    const gpa = std.testing.allocator;
    const text = try longLine(gpa, 5000);
    defer gpa.free(text);
    try std.testing.expectEqual(@as(usize, 5000), try std.unicode.utf8CountCodepoints(text));
    try std.testing.expect(std.mem.indexOfScalar(u8, text, '\n') == null);
}

test "the sample covers the awkward cases" {
    const gpa = std.testing.allocator;
    const text = try sampleText(gpa);
    defer gpa.free(text);
    try std.testing.expect(std.mem.indexOfScalar(u8, text, '\t') != null);
    try std.testing.expect(std.mem.indexOf(u8, text, "日") != null);
    try std.testing.expect(std.mem.indexOf(u8, text, "\n\n") != null);
}
