//! Symbol extraction for Goto Symbol (ctrl+r / "@" in Goto Anything): walks
//! the tree-sitter tree for declarations with a `name` field, falling back
//! to a line regex for languages without a grammar.

const std = @import("std");
const c = @import("c");
const buffer_mod = @import("../text/buffer.zig");
const Regex = @import("../../framework/util/regex.zig").Regex;

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;

pub const Symbol = struct {
    /// Owned by the caller's allocator.
    name: []u8,
    offset: usize,
};

const symbol_kinds = [_][]const u8{
    "function", "method", "class", "struct", "enum", "union", "trait", "interface", "impl", "module", "namespace", "type_alias", "type_definition", "type_declaration", "test_declaration", "macro",
};

fn isSymbolNode(kind: []const u8) bool {
    for (symbol_kinds) |k| if (std.mem.indexOf(u8, kind, k) != null) {
        // Calls and references aren't declarations.
        if (std.mem.indexOf(u8, kind, "call") != null or std.mem.indexOf(u8, kind, "reference") != null or std.mem.endsWith(u8, kind, "_type")) return false;
        return true;
    };
    return false;
}

pub fn extract(gpa: Allocator, buffer: *Buffer) !std.ArrayList(Symbol) {
    var out: std.ArrayList(Symbol) = .empty;
    errdefer {
        for (out.items) |s| gpa.free(s.name);
        out.deinit(gpa);
    }
    buffer.updateSyntax();
    if (buffer.syntax) |*syntax| if (syntax.tree) |tree| {
        var cursor = c.ts_tree_cursor_new(c.ts_tree_root_node(tree));
        defer c.ts_tree_cursor_delete(&cursor);
        var depth: u32 = 0;
        // Pre-order traversal.
        while (true) {
            const node = c.ts_tree_cursor_current_node(&cursor);
            const kind = std.mem.span(c.ts_node_type(node));
            var descend = true;
            if (c.ts_node_is_named(node)) {
                if (isSymbolNode(kind) or isZigContainer(buffer, node, kind)) {
                    const name_node = c.ts_node_child_by_field_name(node, "name", 4);
                    if (!c.ts_node_is_null(name_node)) {
                        const s = c.ts_node_start_byte(name_node);
                        const e = c.ts_node_end_byte(name_node);
                        if (e > s and e - s < 200) {
                            try out.append(gpa, .{ .name = try buffer.textRange(gpa, s, e), .offset = s });
                        }
                    }
                }
                // Don't descend into function bodies' locals for huge files.
                if (depth > 12) descend = false;
            }
            if (descend and c.ts_tree_cursor_goto_first_child(&cursor)) {
                depth += 1;
                continue;
            }
            while (!c.ts_tree_cursor_goto_next_sibling(&cursor)) {
                if (!c.ts_tree_cursor_goto_parent(&cursor)) return out;
                depth -= 1;
            }
        }
    };
    try regexFallback(gpa, buffer, &out);
    return out;
}

/// Zig: `const Foo = struct { ... }` declares a type.
fn isZigContainer(buffer: *Buffer, node: c.TSNode, kind: []const u8) bool {
    if (!std.mem.eql(u8, kind, "variable_declaration")) return false;
    if (buffer.language.grammar == null or !std.mem.eql(u8, buffer.language.grammar.?, "zig")) return false;
    var i: u32 = 0;
    const n = c.ts_node_named_child_count(node);
    while (i < n) : (i += 1) {
        const child = c.ts_node_named_child(node, i);
        const ck = std.mem.span(c.ts_node_type(child));
        if (std.mem.indexOf(u8, ck, "struct") != null or std.mem.indexOf(u8, ck, "enum") != null or
            std.mem.indexOf(u8, ck, "union") != null or std.mem.indexOf(u8, ck, "opaque") != null) return true;
    }
    return false;
}

fn regexFallback(gpa: Allocator, buffer: *Buffer, out: *std.ArrayList(Symbol)) !void {
    var re = try Regex.compile(gpa, "^\\s*(?:pub\\s+|export\\s+|async\\s+|static\\s+)*(?:fn|def|class|function|func|struct|enum|impl|trait|interface|type|\\*+)\\s+([A-Za-z_][\\w.]*)", .{});
    defer re.deinit();
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(gpa);
    var row: u32 = 0;
    while (row < buffer.lineCount()) : (row += 1) {
        const text = try buffer.lineText(row, &line);
        const m = re.matchAt(text, 0) orelse continue;
        const g = m.groups[0] orelse continue;
        try out.append(gpa, .{ .name = try gpa.dupe(u8, text[g[0]..g[1]]), .offset = buffer.lineStart(row) + g[0] });
    }
}
