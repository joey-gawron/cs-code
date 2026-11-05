//! Highlighting inside org source blocks: the code of each
//! `#+BEGIN_SRC lang` block in view is parsed with that language's grammar,
//! limited to the block's lines, and its spans laid over the org ones.
//! Blocks are small and only the visible ones are parsed, each paint.

const std = @import("std");
const c = @import("c");
const syntax_mod = @import("../syntax/syntax.zig");
const languages = @import("../syntax/languages.zig");
const Rope = @import("../text/rope.zig").Rope;

const Allocator = std.mem.Allocator;

/// How far above the visible text a block's #+BEGIN_SRC is looked for.
const max_block_lines = 5000;

fn lineText(gpa: Allocator, rope: *const Rope, row: u32, buf: *std.ArrayList(u8)) []const u8 {
    buf.clearRetainingCapacity();
    var it = rope.chunks(rope.lineStart(row), rope.lineEnd(row));
    while (it.next()) |chunk| buf.appendSlice(gpa, chunk) catch break;
    return buf.items;
}

fn startsWithCi(line: []const u8, prefix: []const u8) bool {
    return std.ascii.startsWithIgnoreCase(std.mem.trimStart(u8, line, " \t"), prefix);
}

/// Appends the spans of the source blocks overlapping [start, end) to
/// `out`. Grammars not loaded yet start loading (through `registry`) and
/// color later paints.
pub fn add(gpa: Allocator, registry: *syntax_mod.Registry, rope: *const Rope, start: usize, end: usize, out: *std.ArrayList(syntax_mod.HighlightSpan)) void {
    var buf: std.ArrayList(u8) = .empty;
    defer buf.deinit(gpa);
    const first = rope.offsetToPoint(start).row;
    const last = rope.offsetToPoint(end).row;
    const count = rope.lineCount();
    // A block open above the window: back to its #+BEGIN_SRC (unless an
    // #+END_SRC comes first).
    var row = first;
    var steps: u32 = 0;
    while (row > 0 and steps < max_block_lines) : (steps += 1) {
        const l = lineText(gpa, rope, row, &buf);
        if (startsWithCi(l, "#+BEGIN_SRC")) break;
        if (startsWithCi(l, "#+END_SRC") and row != first) {
            row = first;
            break;
        }
        row -= 1;
    }
    while (row <= last and row < count) : (row += 1) {
        const head = lineText(gpa, rope, row, &buf);
        if (!startsWithCi(head, "#+BEGIN_SRC")) continue;
        const trimmed = std.mem.trim(u8, head, " \t\r");
        var words = std.mem.tokenizeAny(u8, trimmed["#+BEGIN_SRC".len..], " \t");
        const name = words.next() orelse continue;
        const config = languages.forInjection(name) orelse continue;
        var body_end_row = row + 1;
        while (body_end_row < count and !startsWithCi(lineText(gpa, rope, body_end_row, &buf), "#+END_SRC")) body_end_row += 1;
        if (body_end_row >= count or body_end_row == row + 1) {
            row = body_end_row;
            continue;
        }
        const body_start = rope.lineStart(row + 1);
        const body_end = rope.lineStart(body_end_row);
        if (registry.request(config)) |lang| highlightRange(gpa, lang, rope, body_start, body_end, row + 1, body_end_row, start, end, out);
        row = body_end_row;
    }
}

fn highlightRange(gpa: Allocator, lang: *syntax_mod.Language, rope: *const Rope, body_start: usize, body_end: usize, start_row: u32, end_row: u32, start: usize, end: usize, out: *std.ArrayList(syntax_mod.HighlightSpan)) void {
    var syn = syntax_mod.Syntax.init(gpa, lang) catch return;
    defer syn.deinit();
    const ranges = [_]c.TSRange{.{
        .start_point = .{ .row = start_row, .column = 0 },
        .end_point = .{ .row = end_row, .column = 0 },
        .start_byte = @intCast(body_start),
        .end_byte = @intCast(body_end),
    }};
    if (!c.ts_parser_set_included_ranges(syn.parser, &ranges, ranges.len)) return;
    const tree = c.ts_parser_parse(syn.parser, null, syntax_mod.Syntax.ropeInput(rope)) orelse return;
    defer c.ts_tree_delete(tree);
    syn.highlightTree(lang, tree, &ranges, rope, @max(start, body_start), @min(end, body_end), out);
}
