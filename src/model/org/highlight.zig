//! Org-mode syntax highlighting (a custom highlighter: there are no
//! tree-sitter highlight queries for org).

const std = @import("std");
const theme_mod = @import("../../framework/ui/theme.zig");
const syntax_mod = @import("../syntax/syntax.zig");
const rope_mod = @import("../text/rope.zig");
const parser = @import("parser.zig");
const date = @import("date.zig");
const Color = @import("../../framework/gpu/scene.zig").Color;

const Allocator = std.mem.Allocator;
const Rope = rope_mod.Rope;
const HighlightSpan = syntax_mod.HighlightSpan;

fn levelColor(s: *const theme_mod.Syntax, level: usize) Color {
    const colors = [_]Color{ s.function, s.number, s.keyword, s.string, s.operator, s.comment, s.escape, s.label };
    return colors[(level - 1) % colors.len];
}

fn push(out: *std.ArrayList(HighlightSpan), gpa: Allocator, start: usize, end: usize, color: Color) void {
    if (end <= start) return;
    out.append(gpa, .{ .start = start, .end = end, .color = color }) catch {};
}

/// Custom highlighter (see languages.zig), producing spans for [start, end).
pub fn highlight(gpa: Allocator, rope: *const Rope, start: usize, end: usize, s: *const theme_mod.Syntax, out: *std.ArrayList(HighlightSpan)) void {
    out.clearRetainingCapacity();
    const first_row = rope.offsetToPoint(start).row;
    const last_row = rope.offsetToPoint(end).row;
    var line_buf: std.ArrayList(u8) = .empty;
    defer line_buf.deinit(gpa);

    // Are we inside a #+BEGIN block? Look back a bounded distance.
    var in_block = false;
    var r = first_row;
    var looked: u32 = 0;
    while (r > 0 and looked < 400) : (looked += 1) {
        r -= 1;
        const line = lineText(gpa, rope, r, &line_buf);
        const t = std.mem.trimStart(u8, line, " \t");
        if (std.ascii.startsWithIgnoreCase(t, "#+END_")) break;
        if (std.ascii.startsWithIgnoreCase(t, "#+BEGIN_")) {
            in_block = true;
            break;
        }
    }

    const keywords: parser.Keywords = .{};
    var row = first_row;
    while (row <= last_row) : (row += 1) {
        const line = lineText(gpa, rope, row, &line_buf);
        const base = rope.lineStart(row);
        const trimmed = std.mem.trimStart(u8, line, " \t");
        const indent = line.len - trimmed.len;

        if (std.ascii.startsWithIgnoreCase(trimmed, "#+BEGIN_") or std.ascii.startsWithIgnoreCase(trimmed, "#+END_")) {
            in_block = std.ascii.startsWithIgnoreCase(trimmed, "#+BEGIN_");
            push(out, gpa, base + indent, base + line.len, s.comment);
            continue;
        }
        if (in_block) {
            push(out, gpa, base, base + line.len, s.string);
            continue;
        }
        if (parser.parseHeadingLine(line, keywords)) |h| {
            const done = h.todo != null and keywords.isDone(h.todo.?);
            const color = if (done) s.comment else levelColor(s, h.level);
            push(out, gpa, base, base + line.len, color);
            if (h.todo) |kw| push(out, gpa, base + h.todo_start, base + h.todo_start + kw.len, if (done) s.string else if (std.mem.eql(u8, kw, "TODO")) s.constant else s.label);
            if (h.priority != null) push(out, gpa, base + h.priority_start, base + h.priority_start + 4, s.number);
            if (h.tags.len > 0) push(out, gpa, base + h.tags_start, base + h.tags_start + h.tags.len, s.comment);
            inlineMarkup(gpa, line, base, s, out, true);
            continue;
        }
        if (trimmed.len > 0 and trimmed[0] == '#' and (trimmed.len == 1 or trimmed[1] == ' ')) {
            push(out, gpa, base + indent, base + line.len, s.comment);
            continue;
        }
        if (std.mem.startsWith(u8, trimmed, "#+")) {
            const colon = std.mem.indexOfScalar(u8, trimmed, ':') orelse trimmed.len;
            push(out, gpa, base + indent, base + indent + @min(colon + 1, trimmed.len), s.comment);
            const value_color = if (std.ascii.startsWithIgnoreCase(trimmed, "#+TITLE")) s.number else s.foreground;
            push(out, gpa, base + indent + @min(colon + 1, trimmed.len), base + line.len, value_color);
            continue;
        }
        // Drawers and properties.
        if (trimmed.len > 1 and trimmed[0] == ':' and std.mem.indexOfScalarPos(u8, trimmed, 1, ':') != null and
            std.mem.indexOfScalar(u8, trimmed[1..std.mem.indexOfScalarPos(u8, trimmed, 1, ':').?], ' ') == null)
        {
            const close = std.mem.indexOfScalarPos(u8, trimmed, 1, ':').?;
            push(out, gpa, base + indent, base + indent + close + 1, s.comment);
            continue;
        }
        if (trimmed.len > 0 and trimmed[0] == '|') {
            push(out, gpa, base + indent, base + line.len, s.foreground);
            var i: usize = indent;
            while (i < line.len) : (i += 1) if (line[i] == '|' or (line[i] == '-' and std.mem.indexOfScalar(u8, line, '+') != null) or line[i] == '+') push(out, gpa, base + i, base + i + 1, s.punctuation);
            continue;
        }
        // Planning keywords.
        for ([_][]const u8{ "SCHEDULED:", "DEADLINE:", "CLOSED:" }) |kw| {
            var pos: usize = 0;
            while (std.mem.indexOfPos(u8, line, pos, kw)) |p| : (pos = p + kw.len) {
                push(out, gpa, base + p, base + p + kw.len, if (kw[0] == 'D') s.constant else s.comment);
            }
        }
        // List bullets and checkboxes.
        if (listMarker(trimmed)) |m| {
            push(out, gpa, base + indent, base + indent + m.bullet_len, s.operator);
            if (m.checkbox) |cb| {
                const cb_start = base + indent + m.bullet_len + 1;
                push(out, gpa, cb_start, cb_start + 3, if (cb == ' ') s.keyword else s.string);
            }
        }
        inlineMarkup(gpa, line, base, s, out, false);
    }
}

fn lineText(gpa: Allocator, rope: *const Rope, row: u32, buf: *std.ArrayList(u8)) []const u8 {
    buf.clearRetainingCapacity();
    var it = rope.chunks(rope.lineStart(row), rope.lineEnd(row));
    while (it.next()) |chunk| buf.appendSlice(gpa, chunk) catch break;
    return buf.items;
}

pub const ListMarker = struct { bullet_len: usize, checkbox: ?u8 };

pub fn listMarker(t: []const u8) ?ListMarker {
    var n: usize = 0;
    if (t.len >= 2 and (t[0] == '-' or t[0] == '+') and t[1] == ' ') {
        n = 1;
    } else {
        while (n < t.len and std.ascii.isDigit(t[n])) n += 1;
        if (n == 0 or n + 1 >= t.len or (t[n] != '.' and t[n] != ')') or t[n + 1] != ' ') return null;
        n += 1;
    }
    var cb: ?u8 = null;
    if (t.len >= n + 4 and t[n] == ' ' and t[n + 1] == '[' and t[n + 3] == ']' and (t[n + 2] == ' ' or t[n + 2] == 'X' or t[n + 2] == 'x' or t[n + 2] == '-')) cb = t[n + 2];
    return .{ .bullet_len = n, .checkbox = cb };
}

/// Timestamps, links, statistics cookies and *emphasis*.
fn inlineMarkup(gpa: Allocator, line: []const u8, base: usize, s: *const theme_mod.Syntax, out: *std.ArrayList(HighlightSpan), heading: bool) void {
    var i: usize = 0;
    while (i < line.len) {
        const ch = line[i];
        if (ch == '<' or ch == '[') {
            if (date.parseTimestamp(line, i)) |ts| {
                push(out, gpa, base + ts.start, base + ts.end, s.attribute);
                i = ts.end;
                continue;
            }
            if (ch == '[' and i + 1 < line.len and line[i + 1] == '[') {
                if (std.mem.indexOfPos(u8, line, i, "]]")) |close| {
                    push(out, gpa, base + i, base + close + 2, s.function);
                    i = close + 2;
                    continue;
                }
            }
            if (ch == '[') {
                // [2/5] or [40%]
                if (std.mem.indexOfScalarPos(u8, line, i, ']')) |close| {
                    const inner = line[i + 1 .. close];
                    if (inner.len > 0 and inner.len < 10 and (std.mem.indexOfScalar(u8, inner, '/') != null or inner[inner.len - 1] == '%')) {
                        var all = true;
                        for (inner) |b| if (!std.ascii.isDigit(b) and b != '/' and b != '%') {
                            all = false;
                        };
                        if (all) {
                            push(out, gpa, base + i, base + close + 1, s.number);
                            i = close + 1;
                            continue;
                        }
                    }
                }
            }
        }
        if (!heading and (ch == '*' or ch == '/' or ch == '=' or ch == '~' or ch == '_' or ch == '+')) {
            const before_ok = i == 0 or std.mem.indexOfScalar(u8, " \t('\"{-", line[i - 1]) != null;
            if (before_ok and i + 1 < line.len and line[i + 1] != ' ') {
                if (std.mem.indexOfScalarPos(u8, line, i + 1, ch)) |close| {
                    const after_ok = close + 1 >= line.len or std.mem.indexOfScalar(u8, " \t.,;:!?')\"}-", line[close + 1]) != null;
                    if (after_ok and line[close - 1] != ' ' and close > i + 1) {
                        const color = switch (ch) {
                            '*' => s.number,
                            '/' => s.keyword,
                            '=', '~' => s.string,
                            '+' => s.comment,
                            else => s.function,
                        };
                        push(out, gpa, base + i, base + close + 1, color);
                        i = close + 1;
                        continue;
                    }
                }
            }
        }
        i += 1;
    }
}


test "list markers" {
    try std.testing.expectEqual(@as(?u8, ' '), listMarker("- [ ] item").?.checkbox);
    try std.testing.expectEqual(@as(?u8, 'X'), listMarker("12. [X] item").?.checkbox);
    try std.testing.expect(listMarker("-not a list") == null);
}
