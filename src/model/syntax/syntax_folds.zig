//! Folds from the syntax tree (vim's 'foldmethod' "syntax" here, after
//! nvim's treesitter foldexpr): every named node spanning several lines
//! folds from the row it starts on to its last row, the outermost one when
//! several start on the same row. Grammars rarely ship folds.scm queries, so
//! this needs none; the one rule is that an indentation-delimited body
//! (Python's `block`, which doesn't open with `{`) folds with the statement
//! that owns it rather than on its own first line.

const std = @import("std");
const c = @import("c");
const syntax = @import("syntax.zig");
const Rope = @import("../text/rope.zig").Rope;
const Scanner = @import("../text/movement.zig").Scanner;

const Allocator = std.mem.Allocator;

/// Appends [first, last] row ranges, sorted by first row (outer folds first).
pub fn foldRanges(gpa: Allocator, syn: *const syntax.Syntax, rope: *const Rope, out: *std.ArrayList([2]u32)) !void {
    const tree = syn.tree orelse return;
    const start_len = out.items.len;
    var scanner = Scanner.init(rope);
    var cursor = c.ts_tree_cursor_new(c.ts_tree_root_node(tree));
    defer c.ts_tree_cursor_delete(&cursor);
    // Start rows of the current node's ancestors.
    var ancestors: std.ArrayList(u32) = .empty;
    defer ancestors.deinit(gpa);
    walk: while (true) {
        const node = c.ts_tree_cursor_current_node(&cursor);
        const first: u32 = c.ts_node_start_point(node).row;
        const end = c.ts_node_end_point(node);
        // A node ending at column 0 stops before that line.
        const last: u32 = if (end.column == 0 and end.row > first) end.row - 1 else end.row;
        if (ancestors.items.len > 0 and last > first and c.ts_node_is_named(node)) {
            const parent_first = ancestors.items[ancestors.items.len - 1];
            const body = first > parent_first and
                std.mem.eql(u8, std.mem.span(c.ts_node_type(node)), "block") and
                scanner.byte(c.ts_node_start_byte(node)) != '{';
            if (!body) try out.append(gpa, .{ first, last });
        }
        // Single-line nodes have no multi-line descendants.
        if (end.row > first and c.ts_tree_cursor_goto_first_child(&cursor)) {
            try ancestors.append(gpa, first);
            continue;
        }
        while (!c.ts_tree_cursor_goto_next_sibling(&cursor)) {
            if (!c.ts_tree_cursor_goto_parent(&cursor)) break :walk;
            _ = ancestors.pop();
        }
    }
    const items = out.items[start_len..];
    std.mem.sort([2]u32, items, {}, struct {
        fn lt(_: void, a: [2]u32, b: [2]u32) bool {
            return a[0] < b[0] or (a[0] == b[0] and a[1] > b[1]);
        }
    }.lt);
    // One fold per header row: the outermost.
    var n: usize = 0;
    for (items) |r| {
        if (n > 0 and items[n - 1][0] == r[0]) continue;
        items[n] = r;
        n += 1;
    }
    out.shrinkRetainingCapacity(start_len + n);
}
