//! Org tables: `| a | b |` rows and `|---+---|` separators, parsed from
//! their lines, edited as cells and written back aligned, as org's table
//! editor does on TAB.

const std = @import("std");

const Allocator = std.mem.Allocator;

pub const Row = union(enum) {
    /// `|---+---|`
    hline,
    cells: std.ArrayList([]const u8),
};

pub const Table = struct {
    arena: std.heap.ArenaAllocator,
    rows: std.ArrayList(Row) = .empty,
    /// The indentation before the first '|'.
    indent: []const u8 = "",

    pub fn deinit(self: *Table) void {
        self.arena.deinit();
    }

    fn alloc(self: *Table) Allocator {
        return self.arena.allocator();
    }

    pub fn columns(self: *const Table) usize {
        var n: usize = 0;
        for (self.rows.items) |r| if (r == .cells) {
            n = @max(n, r.cells.items.len);
        };
        return @max(n, 1);
    }

    pub fn cell(self: *const Table, row: usize, col: usize) []const u8 {
        const r = self.rows.items[row];
        if (r != .cells or col >= r.cells.items.len) return "";
        return r.cells.items[col];
    }

    pub fn insertRow(self: *Table, at: usize, hline: bool) !void {
        const row: Row = if (hline) .hline else .{ .cells = .empty };
        try self.rows.insert(self.alloc(), @min(at, self.rows.items.len), row);
    }

    pub fn deleteRow(self: *Table, at: usize) void {
        if (at < self.rows.items.len) _ = self.rows.orderedRemove(at);
    }

    pub fn swapRows(self: *Table, a: usize, b: usize) void {
        if (a >= self.rows.items.len or b >= self.rows.items.len) return;
        std.mem.swap(Row, &self.rows.items[a], &self.rows.items[b]);
    }

    pub fn insertColumn(self: *Table, at: usize) !void {
        for (self.rows.items) |*r| if (r.* == .cells) {
            while (r.cells.items.len < at) try r.cells.append(self.alloc(), "");
            try r.cells.insert(self.alloc(), at, "");
        };
    }

    pub fn deleteColumn(self: *Table, at: usize) void {
        for (self.rows.items) |*r| if (r.* == .cells and at < r.cells.items.len) {
            _ = r.cells.orderedRemove(at);
        };
    }

    pub fn swapColumns(self: *Table, a: usize, b: usize) !void {
        const n = @max(a, b) + 1;
        for (self.rows.items) |*r| if (r.* == .cells) {
            while (r.cells.items.len < n) try r.cells.append(self.alloc(), "");
            std.mem.swap([]const u8, &r.cells.items[a], &r.cells.items[b]);
        };
    }

    /// The aligned text of the table, rows joined by '\n' (no final one).
    /// `cell_at`, if given, receives the offset in that text of the start
    /// of each cell's content as `cell_at[row * columns + col]`.
    pub fn format(self: *const Table, gpa: Allocator, cell_at: ?[]usize) ![]u8 {
        const ncols = self.columns();
        const widths = try gpa.alloc(usize, ncols);
        defer gpa.free(widths);
        @memset(widths, 1);
        for (self.rows.items) |r| if (r == .cells) for (r.cells.items, 0..) |c, i| {
            widths[i] = @max(widths[i], displayWidth(c));
        };
        var out: std.Io.Writer.Allocating = .init(gpa);
        errdefer out.deinit();
        const w = &out.writer;
        for (self.rows.items, 0..) |r, ri| {
            if (ri > 0) try w.writeByte('\n');
            try w.writeAll(self.indent);
            switch (r) {
                .hline => {
                    try w.writeByte('|');
                    for (widths, 0..) |cw, i| {
                        if (i > 0) try w.writeByte('+');
                        try w.splatByteAll('-', cw + 2);
                    }
                    try w.writeByte('|');
                },
                .cells => |cells| {
                    for (widths, 0..) |cw, i| {
                        try w.writeAll("| ");
                        if (cell_at) |ca| if (ri * ncols + i < ca.len) {
                            ca[ri * ncols + i] = out.written().len;
                        };
                        const c = if (i < cells.items.len) cells.items[i] else "";
                        try w.writeAll(c);
                        try w.splatByteAll(' ', cw - displayWidth(c) + 1);
                    }
                    try w.writeByte('|');
                },
            }
        }
        return out.toOwnedSlice();
    }
};

/// Whether `line` is a table row: optional indentation, then '|'.
pub fn isTableLine(line: []const u8) bool {
    const t = std.mem.trimStart(u8, line, " \t");
    return t.len > 0 and t[0] == '|';
}

/// Parses the table's lines (each one isTableLine). Cell text is copied.
pub fn parse(gpa: Allocator, lines: []const []const u8) !Table {
    var t: Table = .{ .arena = .init(gpa) };
    errdefer t.deinit();
    const a = t.alloc();
    if (lines.len > 0) {
        const first = lines[0];
        t.indent = try a.dupe(u8, first[0 .. first.len - std.mem.trimStart(u8, first, " \t").len]);
    }
    for (lines) |line| {
        const body = std.mem.trim(u8, line, " \t\r");
        if (body.len >= 2 and body[0] == '|' and body[1] == '-') {
            try t.rows.append(a, .hline);
            continue;
        }
        var cells: std.ArrayList([]const u8) = .empty;
        var inner = body[1..];
        if (inner.len > 0 and inner[inner.len - 1] == '|') inner = inner[0 .. inner.len - 1];
        var it = std.mem.splitScalar(u8, inner, '|');
        while (it.next()) |c| try cells.append(a, try a.dupe(u8, std.mem.trim(u8, c, " \t")));
        try t.rows.append(a, .{ .cells = cells });
    }
    return t;
}

/// The column of byte `col` in a table row (cells counted by '|').
pub fn columnAt(line: []const u8, col: usize) usize {
    var n: usize = 0;
    var seen_first = false;
    for (line[0..@min(col, line.len)]) |ch| if (ch == '|') {
        if (seen_first) n += 1;
        seen_first = true;
    };
    return n;
}

/// Width in columns: one per code point (tables mostly hold ASCII; wide
/// characters are rare enough not to measure).
fn displayWidth(s: []const u8) usize {
    return std.unicode.utf8CountCodepoints(s) catch s.len;
}

test "align a table" {
    const gpa = std.testing.allocator;
    var t = try parse(gpa, &.{ "  | Name | Qty |", "  |-", "  | apples | 3 |", "  | kiwi" });
    defer t.deinit();
    try std.testing.expectEqual(@as(usize, 2), t.columns());
    var at: [8]usize = undefined;
    const text = try t.format(gpa, &at);
    defer gpa.free(text);
    try std.testing.expectEqualStrings(
        \\  | Name   | Qty |
        \\  |--------+-----|
        \\  | apples | 3   |
        \\  | kiwi   |     |
    , text);
    try std.testing.expectEqualStrings("apples", text[at[2 * 2] .. at[2 * 2] + 6]);
    try std.testing.expectEqual(@as(u8, '3'), text[at[2 * 2 + 1]]);
}

test "rows and columns" {
    const gpa = std.testing.allocator;
    var t = try parse(gpa, &.{ "| a | b |", "| c | d |" });
    defer t.deinit();
    try t.swapColumns(0, 1);
    try t.insertRow(1, true);
    try t.insertColumn(2);
    t.deleteRow(2);
    const text = try t.format(gpa, null);
    defer gpa.free(text);
    try std.testing.expectEqualStrings(
        \\| b | a |   |
        \\|---+---+---|
    , text);
    try std.testing.expectEqual(@as(usize, 1), columnAt("| a | b |", 5));
    try std.testing.expectEqual(@as(usize, 0), columnAt("| a | b |", 1));
}
