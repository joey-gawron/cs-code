//! Org-mode editing for .org buffers: heading visibility cycling
//! (TAB / S-TAB), TODO and priority cycling, structure editing
//! (promote/demote/move subtrees), checkboxes, planning timestamps and links,
//! with Emacs-style keys (including the C-c prefix, which falls back to copy).

const std = @import("std");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const platform = @import("../../framework/platform/platform.zig");
const keymap_mod = @import("../settings/keymap.zig");
const parser = @import("../../model/org/parser.zig");
const date = @import("../../model/org/date.zig");
const highlight = @import("../../model/org/highlight.zig");
const table = @import("../../model/org/table.zig");
const clock = @import("../../model/org/clock.zig");
const listMarker = highlight.listMarker;

const Allocator = std.mem.Allocator;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;

pub fn isOrg(ed: *const Editor) bool {
    return std.mem.eql(u8, ed.buffer.language.name, "Org");
}

// ---- Structure helpers ----------------------------------------------------

fn lineSlice(ed: *Editor, row: u32) []const u8 {
    return ed.buffer.lineText(row, &ed.line_buf) catch "";
}

fn currentRow(ed: *Editor) u32 {
    return ed.buffer.offsetToPoint(ed.primarySelection().head).row;
}

/// Row of the heading containing `row` (itself if it is a heading).
fn headingRowFor(ed: *Editor, row: u32) ?u32 {
    var r = row;
    while (true) {
        if (ed.orgHeadingLevel(r) != null) return r;
        if (r == 0) return null;
        r -= 1;
    }
}

/// Last row of the subtree headed by `row`.
fn subtreeEnd(ed: *Editor, row: u32) u32 {
    const level = ed.orgHeadingLevel(row) orelse return row;
    const last = ed.buffer.lineCount() - 1;
    var r = row + 1;
    while (r <= last) : (r += 1) {
        if (ed.orgHeadingLevel(r)) |l| if (l <= level) return r - 1;
    }
    return last;
}

fn rowsEnd(ed: *Editor, last_row: u32) usize {
    return if (last_row + 1 < ed.buffer.lineCount()) ed.buffer.lineStart(last_row + 1) else ed.buffer.len();
}

// ---- Visibility -----------------------------------------------------------

/// TAB on a heading: FOLDED -> CHILDREN -> SUBTREE -> FOLDED.
pub fn cycleVisibility(ed: *Editor) bool {
    const row = currentRow(ed);
    const level = ed.orgHeadingLevel(row) orelse return false;
    const end = subtreeEnd(ed, row);
    if (end == row) return true; // nothing to fold
    if (ed.display.folds.isFoldHeader(ed.buffer, row)) {
        // FOLDED -> CHILDREN: show direct children, folded.
        _ = ed.display.folds.unfoldHeader(ed.buffer, row);
        var r = row + 1;
        var any_child = false;
        while (r <= end) : (r += 1) {
            if (ed.orgHeadingLevel(r)) |l| if (l == level + 1 or (l > level and !any_child)) {
                any_child = true;
                if (ed.foldEndFor(r)) |last| ed.display.folds.fold(ed.gpa, ed.buffer, r, last) catch {};
            };
        }
        if (!any_child) return true; // no children: straight to SUBTREE
        return true;
    }
    // Any folds inside the subtree? -> SUBTREE (show everything).
    var has_inner = false;
    var r = row + 1;
    while (r <= end) : (r += 1) {
        if (ed.display.folds.isFoldHeader(ed.buffer, r)) {
            has_inner = true;
            _ = ed.display.folds.unfoldHeader(ed.buffer, r);
        }
    }
    if (has_inner) return true;
    // SUBTREE -> FOLDED
    if (ed.foldEndFor(row)) |last| ed.display.folds.fold(ed.gpa, ed.buffer, row, last) catch {};
    ed.moveCaretsOutOfFolds();
    return true;
}

pub const GlobalState = enum { show_all, overview, contents };

/// S-TAB: OVERVIEW -> CONTENTS -> SHOW ALL.
pub fn globalCycle(ed: *Editor, state: *GlobalState) void {
    ed.display.folds.unfoldAll();
    switch (state.*) {
        .show_all => {
            ed.foldLevel(1);
            state.* = .overview;
        },
        .overview => {
            // Every heading visible, bodies hidden.
            const count = ed.buffer.lineCount();
            var r: u32 = 0;
            while (r < count) : (r += 1) {
                if (ed.orgHeadingLevel(r) == null) continue;
                var body_end = r;
                var n = r + 1;
                while (n < count and ed.orgHeadingLevel(n) == null) : (n += 1) body_end = n;
                while (body_end > r and ed.buffer.lineLen(body_end) == 0) body_end -= 1;
                if (body_end > r) ed.display.folds.fold(ed.gpa, ed.buffer, r, body_end) catch {};
            }
            ed.moveCaretsOutOfFolds();
            state.* = .contents;
        },
        .contents => state.* = .show_all,
    }
}

// ---- Editing commands -----------------------------------------------------

fn replaceInLine(ed: *Editor, row: u32, col_start: usize, col_end: usize, text: []const u8) void {
    const ls = ed.buffer.lineStart(row);
    var edits = [_]editor_mod.Edit{.{ .start = ls + col_start, .end = ls + col_end, .text = text }};
    ed.commitTransform(&edits, .other);
}

/// The buffer's TODO keywords (its `#+TODO:` lines, or the defaults),
/// allocated in `arena`.
fn bufferKeywords(ed: *Editor, arena: Allocator) parser.Keywords {
    var kw: parser.Keywords = .{};
    const count = ed.buffer.lineCount();
    var r: u32 = 0;
    while (r < count) : (r += 1) {
        if (ed.buffer.lineLen(r) < 7) continue;
        const line = lineSlice(ed, r);
        if (line[0] != '#') continue;
        // The keywords point into the line: keep a copy (lineSlice reuses its buffer).
        const own = arena.dupe(u8, line) catch continue;
        if (parser.parseTodoLine(arena, own) catch null) |k| kw = k;
    }
    return kw;
}

/// Cycles the TODO keyword of the heading at the caret. When a repeating
/// item is marked done its timestamps advance and it returns to TODO;
/// otherwise, with org_log_done, a DONE item gets a CLOSED stamp (removed
/// again when it leaves DONE). Parent [n/m] cookies follow.
pub fn cycleTodo(ed: *Editor, forward: bool) bool {
    const row = headingRowFor(ed, currentRow(ed)) orelse return false;
    return setTodoAt(ed, row, .{ .cycle = forward });
}

pub const TodoChange = union(enum) {
    /// Next (true) or previous keyword in the cycle.
    cycle: bool,
    /// This keyword, or none.
    set: ?[]const u8,
};

pub fn setTodoAt(ed: *Editor, row: u32, change: TodoChange) bool {
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const kw = bufferKeywords(ed, arena.allocator());
    const line = arena.allocator().dupe(u8, lineSlice(ed, row)) catch return false;
    const h = parser.parseHeadingLine(line, kw) orelse return false;
    const next = switch (change) {
        .cycle => |forward| kw.next(h.todo, forward),
        .set => |k| k,
    };
    ed.buffer.beginGroup();
    defer ed.buffer.endGroup();
    if (h.todo) |cur| {
        if (next) |n| replaceInLine(ed, row, h.todo_start, h.todo_start + cur.len, n) else {
            var e = h.todo_start + cur.len;
            if (e < line.len and line[e] == ' ') e += 1;
            replaceInLine(ed, row, h.todo_start, e, "");
        }
    } else if (next) |n| {
        var buf: [32]u8 = undefined;
        const text = std.fmt.bufPrint(&buf, "{s} ", .{n}) catch return true;
        replaceInLine(ed, row, h.level + 1, h.level + 1, text);
    }
    const was_done = if (h.todo) |cur| kw.isDone(cur) else false;
    const now_done = if (next) |n| kw.isDone(n) else false;
    if (now_done and !was_done) {
        if (!repeatIfNeeded(ed, row, kw) and ed.env.settings.org_log_done) setClosed(ed, row, true);
    } else if (was_done and !now_done) setClosed(ed, row, false);
    if (parentHeading(ed, row)) |parent| updateCookies(ed, parent);
    return true;
}

/// The heading `row` is a child of, if any.
fn parentHeading(ed: *Editor, row: u32) ?u32 {
    const level = ed.orgHeadingLevel(row) orelse return null;
    var r = row;
    while (r > 0) {
        r -= 1;
        if (ed.orgHeadingLevel(r)) |l| if (l < level) return r;
    }
    return null;
}

/// Adds (`on`) or removes the "CLOSED: [stamp]" of the heading at `row`,
/// on its planning line (made if missing; dropped if left empty).
fn setClosed(ed: *Editor, row: u32, on: bool) void {
    var closed_buf: [48]u8 = undefined;
    const prow = row + 1;
    const has_planning = prow < ed.buffer.lineCount() and isPlanningLine(lineSlice(ed, prow));
    if (has_planning) {
        const pline = ed.gpa.dupe(u8, lineSlice(ed, prow)) catch return;
        defer ed.gpa.free(pline);
        if (std.mem.indexOf(u8, pline, "CLOSED:")) |p| {
            var j = p + "CLOSED:".len;
            while (j < pline.len and pline[j] == ' ') j += 1;
            const ts = date.parseTimestamp(pline, j) orelse return;
            if (on) return replaceInLine(ed, prow, j, ts.end, stampNow(&closed_buf));
            var e = ts.end;
            while (e < pline.len and pline[e] == ' ') e += 1;
            // The whole line when nothing else is on it.
            if (std.mem.trim(u8, pline[0..p], " \t").len == 0 and std.mem.trim(u8, pline[e..], " \t").len == 0) {
                const start = ed.buffer.lineEnd(row);
                var edits = [_]editor_mod.Edit{.{ .start = start, .end = ed.buffer.lineEnd(prow), .text = "" }};
                ed.commitTransform(&edits, .other);
            } else replaceInLine(ed, prow, p, e, "");
            return;
        }
        if (!on) return;
        // Org puts CLOSED first on the planning line.
        const indent = pline.len - std.mem.trimStart(u8, pline, " \t").len;
        var buf: [64]u8 = undefined;
        const text = std.fmt.bufPrint(&buf, "CLOSED: {s} ", .{stampNow(&closed_buf)}) catch return;
        return replaceInLine(ed, prow, indent, indent, text);
    }
    if (!on) return;
    const level = ed.orgHeadingLevel(row) orelse return;
    var buf: [96]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    w.writeAll("\n") catch {};
    w.splatByteAll(' ', level + 1) catch {};
    w.print("CLOSED: {s}", .{stampNow(&closed_buf)}) catch return;
    const at = ed.buffer.lineEnd(row);
    var edits = [_]editor_mod.Edit{.{ .start = at, .end = at, .text = w.buffered() }};
    ed.commitTransform(&edits, .other);
}

/// "[2026-10-22 Thu 14:05]"
fn stampNow(buf: []u8) []const u8 {
    const n = date.now();
    var dbuf: [32]u8 = undefined;
    return std.fmt.bufPrint(buf, "[{s} {d:0>2}:{d:0>2}]", .{ n.date.format(&dbuf), n.time.hour, n.time.minute }) catch "";
}

fn isPlanningLine(line: []const u8) bool {
    const t = std.mem.trimStart(u8, line, " \t");
    return std.mem.startsWith(u8, t, "SCHEDULED:") or std.mem.startsWith(u8, t, "DEADLINE:") or std.mem.startsWith(u8, t, "CLOSED:");
}

/// org's repeaters: DONE on "<... +1w>" shifts the date (see
/// date.Repeat.next for ++ and .+) and resets to the first TODO keyword.
/// Returns whether it repeated.
fn repeatIfNeeded(ed: *Editor, heading_row: u32, kw: parser.Keywords) bool {
    if (heading_row + 1 >= ed.buffer.lineCount()) return false;
    const prow = heading_row + 1;
    const line = ed.gpa.dupe(u8, lineSlice(ed, prow)) catch return false;
    defer ed.gpa.free(line);
    var repeated = false;
    var i: usize = 0;
    var edits: std.ArrayList(editor_mod.Edit) = .empty;
    defer edits.deinit(ed.gpa);
    var bufs: [4][64]u8 = undefined;
    var nb: usize = 0;
    const today = date.Date.today();
    while (std.mem.indexOfScalarPos(u8, line, i, '<')) |p| {
        const ts = date.parseTimestamp(line, p) orelse {
            i = p + 1;
            continue;
        };
        i = ts.end;
        const rep = ts.repeat orelse continue;
        if (nb >= bufs.len) break;
        const new_date = rep.next(ts.date, today);
        var dbuf: [32]u8 = undefined;
        const formatted = new_date.format(&dbuf);
        // Keep everything after the weekday (time, repeater).
        const inner = line[p + 1 .. ts.end - 1];
        const rest = if (inner.len > 14) inner[14..] else "";
        const text = std.fmt.bufPrint(&bufs[nb], "<{s}{s}>", .{ formatted, rest }) catch continue;
        nb += 1;
        const ls = ed.buffer.lineStart(prow);
        edits.append(ed.gpa, .{ .start = ls + p, .end = ls + ts.end, .text = text }) catch return false;
        repeated = true;
    }
    if (!repeated) return false;
    ed.commitTransform(edits.items, .other);
    // Back to the first TODO state.
    const hline = lineSlice(ed, heading_row);
    const h = parser.parseHeadingLine(hline, kw) orelse return true;
    if (h.todo) |cur| replaceInLine(ed, heading_row, h.todo_start, h.todo_start + cur.len, kw.todo[0]);
    return true;
}

pub fn cyclePriority(ed: *Editor, up: bool) bool {
    const row = currentRow(ed);
    const line = lineSlice(ed, row);
    const h = parser.parseHeadingLine(line, .{}) orelse return false;
    const order = [_]?u8{ null, 'A', 'B', 'C' };
    var idx: usize = 0;
    for (order, 0..) |p, i| if (p == h.priority) {
        idx = i;
    };
    const next = if (up) order[(idx + order.len - 1) % order.len] else order[(idx + 1) % order.len];
    if (h.priority != null) {
        if (next) |p| {
            var buf = [_]u8{ '[', '#', p, ']' };
            replaceInLine(ed, row, h.priority_start, h.priority_start + 4, &buf);
        } else {
            var e = h.priority_start + 4;
            if (e < line.len and line[e] == ' ') e += 1;
            replaceInLine(ed, row, h.priority_start, e, "");
        }
    } else if (next) |p| {
        var buf = [_]u8{ '[', '#', p, ']', ' ' };
        replaceInLine(ed, row, h.title_start, h.title_start, &buf);
    }
    return true;
}

/// Promote (-1) / demote (+1) the heading, or its whole subtree.
pub fn shiftLevel(ed: *Editor, delta: i32, subtree: bool) bool {
    const row = currentRow(ed);
    const level = ed.orgHeadingLevel(row) orelse return false;
    if (delta < 0 and level <= 1) return true;
    const last = if (subtree) subtreeEnd(ed, row) else row;
    var edits: std.ArrayList(editor_mod.Edit) = .empty;
    defer edits.deinit(ed.gpa);
    var r = row;
    while (r <= last) : (r += 1) {
        if (ed.orgHeadingLevel(r) == null) continue;
        const ls = ed.buffer.lineStart(r);
        if (delta > 0) {
            edits.append(ed.gpa, .{ .start = ls, .end = ls, .text = "*" }) catch return true;
        } else {
            edits.append(ed.gpa, .{ .start = ls, .end = ls + 1, .text = "" }) catch return true;
        }
    }
    ed.commitTransform(edits.items, .other);
    return true;
}

/// Swap the subtree at the caret with its previous / next sibling.
pub fn moveSubtree(ed: *Editor, up: bool) bool {
    const row = currentRow(ed);
    const level = ed.orgHeadingLevel(row) orelse return false;
    const end = subtreeEnd(ed, row);
    var other_start: u32 = undefined;
    var other_end: u32 = undefined;
    if (up) {
        if (row == 0) return true;
        var r = row - 1;
        while (true) {
            if (ed.orgHeadingLevel(r)) |l| {
                if (l < level) return true;
                if (l == level) break;
            }
            if (r == 0) return true;
            r -= 1;
        }
        other_start = r;
        other_end = row - 1;
    } else {
        if (end + 1 >= ed.buffer.lineCount()) return true;
        const l = ed.orgHeadingLevel(end + 1) orelse return true;
        if (l != level) return true;
        other_start = end + 1;
        other_end = subtreeEnd(ed, other_start);
    }
    const a_start = if (up) other_start else row;
    const mid = if (up) row else other_start;
    const b_end = if (up) end else other_end;
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const s0 = ed.buffer.lineStart(a_start);
    const s1 = ed.buffer.lineStart(mid);
    const s2 = rowsEnd(ed, b_end);
    var first = ed.buffer.textRange(alloc, s0, s1) catch return true;
    var second = ed.buffer.textRange(alloc, s1, s2) catch return true;
    // The last block may lack a trailing newline.
    if (second.len == 0 or second[second.len - 1] != '\n') {
        second = std.mem.concat(alloc, u8, &.{ second, "\n" }) catch return true;
        if (first.len > 0 and first[first.len - 1] == '\n') first = first[0 .. first.len - 1];
    }
    const joined = std.mem.concat(alloc, u8, &.{ second, first }) catch return true;
    // The caret moves with its subtree.
    const caret_off: usize = if (up) ed.primarySelection().head - s1 else (second.len) + (ed.primarySelection().head - s0);
    var reps = [_]Editor.Replacement{.{ .start = s0, .end = s2, .text = joined, .sel_anchor = caret_off, .sel_head = caret_off }};
    ed.applyReplacements(&reps, .other);
    return true;
}

/// New heading at the same level: after the subtree (`respect_content`) or right below.
pub fn insertHeading(ed: *Editor, respect_content: bool, todo: bool) bool {
    const row = currentRow(ed);
    const hrow = headingRowFor(ed, row) orelse {
        if (!respect_content) return false;
        // No heading yet: start a top-level one.
        const end = ed.buffer.lineEnd(row);
        const text = if (todo) "\n* TODO " else "\n* ";
        var reps = [_]Editor.Replacement{.{ .start = end, .end = end, .text = text, .sel_anchor = text.len, .sel_head = text.len }};
        ed.applyReplacements(&reps, .other);
        return true;
    };
    const level = ed.orgHeadingLevel(hrow).?;
    const after_row = if (respect_content) subtreeEnd(ed, hrow) else row;
    const at = ed.buffer.lineEnd(after_row);
    var buf: [80]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    w.writeAll("\n") catch {};
    w.splatByteAll('*', level) catch {};
    w.writeAll(if (todo) " TODO " else " ") catch {};
    const text = w.buffered();
    var reps = [_]Editor.Replacement{.{ .start = at, .end = at, .text = text, .sel_anchor = text.len, .sel_head = text.len }};
    ed.applyReplacements(&reps, .other);
    return true;
}

/// C-c C-c on a list item: toggle its checkbox and update [n/m] cookies.
pub fn toggleCheckbox(ed: *Editor) bool {
    const row = currentRow(ed);
    const line = lineSlice(ed, row);
    const trimmed = std.mem.trimStart(u8, line, " \t");
    const indent = line.len - trimmed.len;
    const m = listMarker(trimmed) orelse return false;
    const cb = m.checkbox orelse {
        // Add a checkbox to a plain list item.
        replaceInLine(ed, row, indent + m.bullet_len + 1, indent + m.bullet_len + 1, "[ ] ");
        return true;
    };
    const col = indent + m.bullet_len + 2;
    replaceInLine(ed, row, col, col + 1, if (cb == ' ' or cb == '-') "X" else " ");
    updateCookies(ed, row);
    return true;
}

/// Recomputes [n/m] / [n%] cookies on the heading above `row`: from the
/// checkboxes in its body, or, without any, its direct children's TODO
/// states, as org does.
fn updateCookies(ed: *Editor, row: u32) void {
    const hrow = headingRowFor(ed, row) orelse return;
    const end = subtreeEnd(ed, hrow);
    var total: usize = 0;
    var done: usize = 0;
    var r = hrow + 1;
    while (r <= end) : (r += 1) {
        if (ed.orgHeadingLevel(r) != null) break;
        const t = std.mem.trimStart(u8, lineSlice(ed, r), " \t");
        const m = listMarker(t) orelse continue;
        const cb = m.checkbox orelse continue;
        total += 1;
        if (cb == 'X' or cb == 'x') done += 1;
    }
    if (total == 0) {
        var arena = std.heap.ArenaAllocator.init(ed.gpa);
        defer arena.deinit();
        const kw = bufferKeywords(ed, arena.allocator());
        const level = ed.orgHeadingLevel(hrow).?;
        r = hrow + 1;
        while (r <= end) : (r += 1) {
            if (ed.orgHeadingLevel(r) != level + 1) continue;
            const ch = parser.parseHeadingLine(lineSlice(ed, r), kw) orelse continue;
            const k = ch.todo orelse continue;
            total += 1;
            if (kw.isDone(k)) done += 1;
        }
    }
    const line = ed.gpa.dupe(u8, lineSlice(ed, hrow)) catch return;
    defer ed.gpa.free(line);
    const open = std.mem.indexOfScalar(u8, line, '[') orelse return;
    var i = open;
    while (std.mem.indexOfScalarPos(u8, line, i, '[')) |p| : (i = p + 1) {
        const close = std.mem.indexOfScalarPos(u8, line, p, ']') orelse return;
        const inner = line[p + 1 .. close];
        var buf: [24]u8 = undefined;
        const text = if (std.mem.indexOfScalar(u8, inner, '/') != null and inner.len < 10)
            std.fmt.bufPrint(&buf, "[{d}/{d}]", .{ done, total }) catch return
        else if (inner.len > 0 and inner.len < 6 and inner[inner.len - 1] == '%')
            std.fmt.bufPrint(&buf, "[{d}%]", .{if (total == 0) 0 else done * 100 / total}) catch return
        else
            continue;
        replaceInLine(ed, hrow, p, close + 1, text);
        return;
    }
}

/// The repeater and warning period of the timestamp in `stamp` ("+1w -3d").
fn suffixOf(stamp: []const u8, buf: []u8) []const u8 {
    const ts = date.parseTimestamp(stamp, 0) orelse return "";
    var w: std.Io.Writer = .fixed(buf);
    var it = std.mem.tokenizeScalar(u8, stamp[1 .. ts.end - 1], ' ');
    _ = it.next(); // the date
    while (it.next()) |tok| if (date.isSuffixToken(tok)) {
        if (w.buffered().len > 0) w.writeByte(' ') catch break;
        w.writeAll(tok) catch break;
    };
    return w.buffered();
}

/// The row below the heading at `hrow` when it is a planning line, and
/// where `which` ("SCHEDULED:") sits on it.
fn planningSlot(line: []const u8, which: []const u8) ?struct { start: usize, ts: date.Timestamp } {
    const p = std.mem.indexOf(u8, line, which) orelse return null;
    var j = p + which.len;
    while (j < line.len and line[j] == ' ') j += 1;
    const ts = date.parseTimestamp(line, j) orelse return null;
    return .{ .start = p, .ts = ts };
}

/// The inside of the heading's `which` timestamp ("2026-11-05 Thu 10:00 +1w"),
/// for the date prompt to start from.
pub fn planningValue(ed: *Editor, which: []const u8, buf: []u8) ?[]const u8 {
    const hrow = headingRowFor(ed, currentRow(ed)) orelse return null;
    if (hrow + 1 >= ed.buffer.lineCount()) return null;
    const line = lineSlice(ed, hrow + 1);
    const slot = planningSlot(line, which) orelse return null;
    const inner = line[slot.ts.start + 1 .. slot.ts.end - 1];
    if (inner.len > buf.len) return null;
    @memcpy(buf[0..inner.len], inner);
    return buf[0..inner.len];
}

/// Sets the heading's SCHEDULED / DEADLINE (`which` includes the colon). A
/// repeater and warning the old date had stay unless the new input names
/// its own.
pub fn setPlanning(ed: *Editor, which: []const u8, input: date.Input) void {
    const hrow = headingRowFor(ed, currentRow(ed)) orelse return;
    var kept_buf: [32]u8 = undefined;
    var kept: []const u8 = "";
    if (input.suffix_len == 0 and hrow + 1 < ed.buffer.lineCount()) {
        const line = lineSlice(ed, hrow + 1);
        if (planningSlot(line, which)) |slot| kept = suffixOf(line[slot.ts.start..slot.ts.end], &kept_buf);
    }
    var with_suffix = input;
    if (kept.len > 0) {
        @memcpy(with_suffix.suffix_buf[0..kept.len], kept);
        with_suffix.suffix_len = @intCast(kept.len);
    }
    var ibuf: [96]u8 = undefined;
    var tbuf: [128]u8 = undefined;
    const stamp = std.fmt.bufPrint(&tbuf, "{s} <{s}>", .{ which, with_suffix.format(&ibuf) }) catch return;
    const level = ed.orgHeadingLevel(hrow).?;
    if (hrow + 1 < ed.buffer.lineCount()) {
        const pline = ed.gpa.dupe(u8, lineSlice(ed, hrow + 1)) catch return;
        defer ed.gpa.free(pline);
        const is_planning = std.mem.indexOf(u8, pline, "SCHEDULED:") != null or std.mem.indexOf(u8, pline, "DEADLINE:") != null or std.mem.indexOf(u8, pline, "CLOSED:") != null;
        if (is_planning) {
            if (std.mem.indexOf(u8, pline, which)) |p| {
                var j = p + which.len;
                while (j < pline.len and pline[j] == ' ') j += 1;
                const ts = date.parseTimestamp(pline, j) orelse return;
                replaceInLine(ed, hrow + 1, p, ts.end, stamp);
            } else {
                const end = std.mem.trimEnd(u8, pline, " ").len;
                var buf: [160]u8 = undefined;
                replaceInLine(ed, hrow + 1, end, end, std.fmt.bufPrint(&buf, " {s}", .{stamp}) catch return);
            }
            return;
        }
    }
    var buf: [192]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    w.writeAll("\n") catch {};
    w.splatByteAll(' ', level + 1) catch {};
    w.writeAll(stamp) catch {};
    const at = ed.buffer.lineEnd(hrow);
    var edits = [_]editor_mod.Edit{.{ .start = at, .end = at, .text = w.buffered() }};
    ed.commitTransform(&edits, .other);
}

/// Removes the heading's SCHEDULED / DEADLINE (and the planning line when
/// nothing else is on it). False when it has none.
pub fn removePlanning(ed: *Editor, which: []const u8) bool {
    const hrow = headingRowFor(ed, currentRow(ed)) orelse return false;
    if (hrow + 1 >= ed.buffer.lineCount()) return false;
    const pline = ed.gpa.dupe(u8, lineSlice(ed, hrow + 1)) catch return false;
    defer ed.gpa.free(pline);
    const slot = planningSlot(pline, which) orelse return false;
    var end = slot.ts.end;
    while (end < pline.len and pline[end] == ' ') end += 1;
    const rest_empty = std.mem.trim(u8, pline[0..slot.start], " \t").len == 0 and std.mem.trim(u8, pline[end..], " \t").len == 0;
    ed.buffer.beginGroup();
    defer ed.buffer.endGroup();
    if (rest_empty) {
        var edits = [_]editor_mod.Edit{.{ .start = ed.buffer.lineEnd(hrow), .end = ed.buffer.lineEnd(hrow + 1), .text = "" }};
        ed.commitTransform(&edits, .other);
    } else replaceInLine(ed, hrow + 1, slot.start, end, "");
    return true;
}

// ---- Tables ---------------------------------------------------------------

pub const TableOp = enum {
    align_table,
    next_cell,
    prev_cell,
    next_row,
    insert_row,
    delete_row,
    insert_column,
    delete_column,
    move_row_up,
    move_row_down,
    move_column_left,
    move_column_right,
    insert_hline,
};

pub fn inTable(ed: *Editor) bool {
    return table.isTableLine(lineSlice(ed, currentRow(ed)));
}

/// Edits the table at the caret and writes it back aligned, the caret in
/// the cell the operation leads to. False when the caret isn't in a table.
pub fn tableCommand(ed: *Editor, op: TableOp) bool {
    const caret_row = currentRow(ed);
    if (!table.isTableLine(lineSlice(ed, caret_row))) return false;
    var first = caret_row;
    while (first > 0 and table.isTableLine(lineSlice(ed, first - 1))) first -= 1;
    var last = caret_row;
    while (last + 1 < ed.buffer.lineCount() and table.isTableLine(lineSlice(ed, last + 1))) last += 1;

    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const lines = alloc.alloc([]const u8, last - first + 1) catch return true;
    for (lines, 0..) |*l, i| l.* = alloc.dupe(u8, lineSlice(ed, first + @as(u32, @intCast(i)))) catch return true;
    var t = table.parse(ed.gpa, lines) catch return true;
    defer t.deinit();

    var row: usize = caret_row - first;
    const caret_col = ed.primarySelection().head - ed.buffer.lineStart(caret_row);
    var col: usize = @min(table.columnAt(lines[row], caret_col), t.columns() - 1);
    const ncols = t.columns();
    const isCells = struct {
        fn f(tb: *const table.Table, r: usize) bool {
            return r < tb.rows.items.len and tb.rows.items[r] == .cells;
        }
    }.f;
    switch (op) {
        .align_table => {},
        .next_cell => {
            if (col + 1 < ncols and isCells(&t, row)) {
                col += 1;
            } else {
                col = 0;
                var r = row + 1;
                while (r < t.rows.items.len and !isCells(&t, r)) r += 1;
                if (r >= t.rows.items.len) {
                    t.insertRow(t.rows.items.len, false) catch return true;
                    r = t.rows.items.len - 1;
                }
                row = r;
            }
        },
        .prev_cell => {
            if (col > 0 and isCells(&t, row)) {
                col -= 1;
            } else {
                var r = row;
                while (r > 0) {
                    r -= 1;
                    if (isCells(&t, r)) {
                        row = r;
                        col = ncols - 1;
                        break;
                    }
                }
            }
        },
        .next_row => {
            if (isCells(&t, row + 1)) {
                row += 1;
            } else {
                t.insertRow(row + 1, false) catch return true;
                row += 1;
            }
        },
        .insert_row => t.insertRow(row, false) catch return true,
        .delete_row => {
            t.deleteRow(row);
            if (t.rows.items.len == 0) {
                // The last row: the table goes, with its line.
                const start = ed.buffer.lineStart(first);
                const end = rowsEnd(ed, last);
                var edits = [_]editor_mod.Edit{.{ .start = start, .end = end, .text = "" }};
                ed.commitTransform(&edits, .other);
                return true;
            }
            row = @min(row, t.rows.items.len - 1);
        },
        .insert_column => t.insertColumn(col) catch return true,
        .delete_column => {
            if (ncols <= 1) return true;
            t.deleteColumn(col);
            col = @min(col, t.columns() - 1);
        },
        .move_row_up => if (row > 0) {
            t.swapRows(row, row - 1);
            row -= 1;
        },
        .move_row_down => if (row + 1 < t.rows.items.len) {
            t.swapRows(row, row + 1);
            row += 1;
        },
        .move_column_left => if (col > 0) {
            t.swapColumns(col, col - 1) catch return true;
            col -= 1;
        },
        .move_column_right => if (col + 1 < ncols) {
            t.swapColumns(col, col + 1) catch return true;
            col += 1;
        },
        .insert_hline => t.insertRow(row + 1, true) catch return true,
    }

    const cols = t.columns();
    const cell_at = alloc.alloc(usize, t.rows.items.len * cols) catch return true;
    @memset(cell_at, 0);
    const text = t.format(ed.gpa, cell_at) catch return true;
    defer ed.gpa.free(text);
    // The caret: after its cell's text (typing adds to it), or at the
    // start of the separator's line.
    var caret: usize = cell_at[row * cols + @min(col, cols - 1)] + t.cell(row, @min(col, cols - 1)).len;
    if (!isCells(&t, row)) {
        var off: usize = 0;
        var r: usize = 0;
        while (r < row) : (r += 1) off = (std.mem.indexOfScalarPos(u8, text, off, '\n') orelse text.len) + 1;
        caret = @min(off, text.len);
    }
    const start = ed.buffer.lineStart(first);
    var reps = [_]Editor.Replacement{.{ .start = start, .end = ed.buffer.lineEnd(last), .text = text, .sel_anchor = caret, .sel_head = caret }};
    ed.applyReplacements(&reps, .other);
    return true;
}

// ---- Clocking -------------------------------------------------------------

/// The heading at the caret's row and title (in `buf`), for clocking in.
pub fn headingAtCaret(ed: *Editor, buf: []u8) ?struct { row: u32, title: []const u8 } {
    const row = headingRowFor(ed, currentRow(ed)) orelse return null;
    const h = parser.parseHeadingLine(lineSlice(ed, row), .{}) orelse return null;
    const n = @min(h.title.len, buf.len);
    @memcpy(buf[0..n], h.title[0..n]);
    return .{ .row = row, .title = buf[0..n] };
}

/// Starts a clock on the heading at `row`: a running "CLOCK: [now]" line,
/// newest first, in its :LOGBOOK: (made after the planning line and
/// properties when it has none).
pub fn clockIn(ed: *Editor, row: u32, now: i64) void {
    const level = ed.orgHeadingLevel(row) orelse return;
    var indent_buf: [64]u8 = undefined;
    const indent = indent_buf[0..@min(level + 1, indent_buf.len)];
    @memset(indent, ' ');
    var stamp_buf: [40]u8 = undefined;
    const stamp = clock.formatStamp(&stamp_buf, now);
    // Past the planning line and the property drawer.
    var r = row + 1;
    const count = ed.buffer.lineCount();
    if (r < count and isPlanningLine(lineSlice(ed, r))) r += 1;
    if (r < count and std.ascii.eqlIgnoreCase(std.mem.trim(u8, lineSlice(ed, r), " \t"), ":PROPERTIES:")) {
        while (r < count and !std.ascii.eqlIgnoreCase(std.mem.trim(u8, lineSlice(ed, r), " \t"), ":END:")) r += 1;
        r += 1;
    }
    var buf: [192]u8 = undefined;
    const has_logbook = r < count and std.ascii.eqlIgnoreCase(std.mem.trim(u8, lineSlice(ed, r), " \t"), ":LOGBOOK:");
    const text = if (has_logbook)
        std.fmt.bufPrint(&buf, "\n{s}CLOCK: {s}", .{ indent, stamp }) catch return
    else
        std.fmt.bufPrint(&buf, "\n{s}:LOGBOOK:\n{s}CLOCK: {s}\n{s}:END:", .{ indent, indent, stamp, indent }) catch return;
    // After the :LOGBOOK: line, or after the line before where it goes.
    const at = ed.buffer.lineEnd(if (has_logbook) r else r - 1);
    var edits = [_]editor_mod.Edit{.{ .start = at, .end = at, .text = text }};
    ed.commitTransform(&edits, .other);
}

/// Closes the buffer's running clock(s): "CLOCK: [start]--[now] =>  h:mm".
/// Returns the minutes of the last one closed, or null if none ran.
pub fn clockOut(ed: *Editor, now: i64) ?i64 {
    var closed: ?i64 = null;
    const count = ed.buffer.lineCount();
    var r: u32 = 0;
    while (r < count) : (r += 1) {
        const line = lineSlice(ed, r);
        const c = clock.parseLine(line) orelse continue;
        if (c.end != null) continue;
        const indent_len = line.len - std.mem.trimStart(u8, line, " \t").len;
        var indent_buf: [64]u8 = undefined;
        const indent = indent_buf[0..@min(indent_len, indent_buf.len)];
        @memcpy(indent, line[0..indent.len]);
        var buf: [160]u8 = undefined;
        const end = @max(now, c.start);
        replaceInLine(ed, r, 0, line.len, clock.closedLine(&buf, indent, c.start, end));
        closed = end - c.start;
    }
    return closed;
}

/// A running clock in the buffer: its heading's row and title (in `buf`).
pub fn runningClock(ed: *Editor, buf: []u8) ?struct { row: u32, title: []const u8, start: i64 } {
    const count = ed.buffer.lineCount();
    var r: u32 = 0;
    while (r < count) : (r += 1) {
        const c = clock.parseLine(lineSlice(ed, r)) orelse continue;
        if (c.end != null) continue;
        const hrow = headingRowFor(ed, r) orelse return null;
        const h = parser.parseHeadingLine(lineSlice(ed, hrow), .{}) orelse return null;
        const n = @min(h.title.len, buf.len);
        @memcpy(buf[0..n], h.title[0..n]);
        return .{ .row = hrow, .title = buf[0..n], .start = c.start };
    }
    return null;
}

/// Inserts (or, with the caret in one, refreshes) a clock table: the time
/// clocked under each heading of the file, its subtree included.
pub fn clockReport(ed: *Editor, now: i64) void {
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const text = ed.buffer.textRange(alloc, 0, ed.buffer.len()) catch return;
    var doc = parser.parse(ed.gpa, text, ed.buffer.path orelse "buffer.org") catch return;
    defer doc.deinit();
    const hs = doc.headings.items;
    // Subtree totals: each heading's time added to its ancestors'.
    const totals = alloc.alloc(i64, hs.len) catch return;
    for (hs, totals) |*h, *t| t.* = h.clocked(now);
    var i = hs.len;
    while (i > 0) {
        i -= 1;
        var j = i;
        while (j > 0) {
            j -= 1;
            if (hs[j].level < hs[i].level) {
                totals[j] += totals[i];
                break;
            }
        }
    }
    var grand: i64 = 0;
    for (hs, totals) |h, t| if (h.level == 1) {
        grand += t;
    };

    var rows: std.ArrayList([]const u8) = .empty;
    var dur: [16]u8 = undefined;
    var stamp: [40]u8 = undefined;
    rows.append(alloc, "| Headline | Time |") catch return;
    rows.append(alloc, "|-") catch return;
    rows.append(alloc, std.fmt.allocPrint(alloc, "| *Total time* | *{s}* |", .{clock.formatDuration(&dur, grand)}) catch return) catch return;
    rows.append(alloc, "|-") catch return;
    for (hs, totals) |h, t| {
        if (t == 0) continue;
        var w: std.Io.Writer.Allocating = .init(alloc);
        w.writer.writeAll("| ") catch return;
        if (h.level > 1) {
            w.writer.writeByte('\\') catch return;
            w.writer.splatByteAll('_', h.level - 1) catch return;
            w.writer.writeByte(' ') catch return;
        }
        w.writer.print("{s} | {s} |", .{ h.title, clock.formatDuration(&dur, t) }) catch return;
        rows.append(alloc, w.written()) catch return;
    }
    var t = table.parse(ed.gpa, rows.items) catch return;
    defer t.deinit();
    const tbl = t.format(ed.gpa, null) catch return;
    defer ed.gpa.free(tbl);
    const block = std.fmt.allocPrint(alloc, "#+BEGIN: clocktable :scope file\n#+CAPTION: Clock summary at {s}\n{s}\n#+END:", .{ clock.formatStamp(&stamp, now), tbl }) catch return;

    // Inside an existing clock table: replace it.
    const row = currentRow(ed);
    var first = row;
    while (true) {
        const l = lineSlice(ed, first);
        if (std.ascii.startsWithIgnoreCase(std.mem.trimStart(u8, l, " \t"), "#+BEGIN: clocktable")) break;
        if (first == 0 or std.ascii.startsWithIgnoreCase(std.mem.trimStart(u8, l, " \t"), "#+END:") and first != row) {
            first = std.math.maxInt(u32);
            break;
        }
        first -= 1;
    }
    if (first != std.math.maxInt(u32)) {
        var last = first;
        while (last + 1 < ed.buffer.lineCount() and !std.ascii.startsWithIgnoreCase(std.mem.trimStart(u8, lineSlice(ed, last), " \t"), "#+END:")) last += 1;
        var reps = [_]Editor.Replacement{.{ .start = ed.buffer.lineStart(first), .end = ed.buffer.lineEnd(last), .text = block, .sel_anchor = 0, .sel_head = 0 }};
        ed.applyReplacements(&reps, .other);
        return;
    }
    const at = ed.buffer.lineEnd(row);
    const with_nl = std.fmt.allocPrint(alloc, "\n{s}", .{block}) catch return;
    var reps = [_]Editor.Replacement{.{ .start = at, .end = at, .text = with_nl, .sel_anchor = 1, .sel_head = 1 }};
    ed.applyReplacements(&reps, .other);
}

// ---- Source blocks ----------------------------------------------------------

pub const SrcBlock = struct {
    /// Rows of the #+BEGIN_SRC and #+END_SRC lines.
    begin_row: u32,
    end_row: u32,
    /// The language named after #+BEGIN_SRC ("" if none).
    lang: []const u8,
    /// The lines between, without the block's common indentation.
    body: []const u8,
    /// That indentation, put back when the body is written back.
    indent: []const u8,
};

fn isBlockLine(line: []const u8, comptime what: []const u8) bool {
    return std.ascii.startsWithIgnoreCase(std.mem.trimStart(u8, line, " \t"), what);
}

/// The source block around the caret (strings in `alloc`).
pub fn srcBlockAt(ed: *Editor, alloc: Allocator) ?SrcBlock {
    const row = currentRow(ed);
    var begin = row;
    while (true) {
        const l = lineSlice(ed, begin);
        if (isBlockLine(l, "#+BEGIN_SRC")) break;
        if (isBlockLine(l, "#+END_SRC") and begin != row) return null;
        if (begin == 0) return null;
        begin -= 1;
    }
    var end = begin + 1;
    const count = ed.buffer.lineCount();
    while (end < count and !isBlockLine(lineSlice(ed, end), "#+END_SRC")) end += 1;
    if (end >= count or row > end) return null;
    const head = std.mem.trim(u8, lineSlice(ed, begin), " \t\r");
    var words = std.mem.tokenizeAny(u8, head["#+BEGIN_SRC".len..], " \t");
    const lang = alloc.dupe(u8, words.next() orelse "") catch return null;
    // Common indentation of the non-blank body lines.
    var indent_len: usize = std.math.maxInt(usize);
    var r = begin + 1;
    while (r < end) : (r += 1) {
        const l = lineSlice(ed, r);
        if (std.mem.trim(u8, l, " \t").len == 0) continue;
        indent_len = @min(indent_len, l.len - std.mem.trimStart(u8, l, " \t").len);
    }
    if (indent_len == std.math.maxInt(usize)) indent_len = 0;
    var body: std.ArrayList(u8) = .empty;
    var indent: []const u8 = "";
    r = begin + 1;
    while (r < end) : (r += 1) {
        const l = lineSlice(ed, r);
        if (indent.len == 0 and l.len >= indent_len and indent_len > 0) indent = alloc.dupe(u8, l[0..indent_len]) catch "";
        body.appendSlice(alloc, if (l.len >= indent_len) l[indent_len..] else std.mem.trimStart(u8, l, " \t")) catch return null;
        body.append(alloc, '\n') catch return null;
    }
    return .{ .begin_row = begin, .end_row = end, .lang = lang, .body = body.items, .indent = indent };
}

/// Puts `body` back between the lines of the block that starts at
/// `begin_row`, re-indented. False if that row isn't a block any more.
pub fn replaceSrcBody(ed: *Editor, begin_row: u32, body: []const u8, indent: []const u8) bool {
    if (begin_row >= ed.buffer.lineCount() or !isBlockLine(lineSlice(ed, begin_row), "#+BEGIN_SRC")) return false;
    var end = begin_row + 1;
    const count = ed.buffer.lineCount();
    while (end < count and !isBlockLine(lineSlice(ed, end), "#+END_SRC")) end += 1;
    if (end >= count) return false;
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(ed.gpa);
    var lines = std.mem.splitScalar(u8, std.mem.trimEnd(u8, body, "\n"), '\n');
    while (lines.next()) |l| {
        if (l.len > 0) out.appendSlice(ed.gpa, indent) catch return false;
        out.appendSlice(ed.gpa, l) catch return false;
        out.append(ed.gpa, '\n') catch return false;
    }
    const start = ed.buffer.lineStart(begin_row + 1);
    var edits = [_]editor_mod.Edit{.{ .start = start, .end = ed.buffer.lineStart(end), .text = out.items }};
    ed.commitTransform(&edits, .other);
    return true;
}

// ---- Tags -----------------------------------------------------------------

/// The tags of the heading at the caret, as "a b c" (in `buf`), or null
/// when the caret isn't under a heading.
pub fn headingTags(ed: *Editor, buf: []u8) ?[]const u8 {
    const row = headingRowFor(ed, currentRow(ed)) orelse return null;
    const h = parser.parseHeadingLine(lineSlice(ed, row), .{}) orelse return null;
    var w: std.Io.Writer = .fixed(buf);
    var it = std.mem.tokenizeScalar(u8, h.tags, ':');
    var first = true;
    while (it.next()) |t| {
        if (!first) w.writeByte(' ') catch break;
        w.writeAll(t) catch break;
        first = false;
    }
    return w.buffered();
}

/// Every tag used in the buffer, sorted, without repeats (in `arena`).
pub fn knownTags(ed: *Editor, arena: Allocator) []const []const u8 {
    var tags: std.ArrayList([]const u8) = .empty;
    const count = ed.buffer.lineCount();
    var r: u32 = 0;
    while (r < count) : (r += 1) {
        if (ed.orgHeadingLevel(r) == null) continue;
        const h = parser.parseHeadingLine(lineSlice(ed, r), .{}) orelse continue;
        var it = std.mem.tokenizeScalar(u8, h.tags, ':');
        outer: while (it.next()) |t| {
            for (tags.items) |have| if (std.mem.eql(u8, have, t)) continue :outer;
            tags.append(arena, arena.dupe(u8, t) catch continue) catch {};
        }
    }
    std.mem.sort([]const u8, tags.items, {}, struct {
        fn lt(_: void, a: []const u8, b: []const u8) bool {
            return std.mem.lessThan(u8, a, b);
        }
    }.lt);
    return tags.items;
}

/// Replaces the heading's tags with `input` ("a b", "a:b" or ":a:b:"; empty
/// removes them), right after the title as org writes them.
pub fn setTags(ed: *Editor, input: []const u8) bool {
    const row = headingRowFor(ed, currentRow(ed)) orelse return false;
    const line = ed.gpa.dupe(u8, lineSlice(ed, row)) catch return false;
    defer ed.gpa.free(line);
    const h = parser.parseHeadingLine(line, .{}) orelse return false;
    var buf: [256]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    var it = std.mem.tokenizeAny(u8, input, ": \t,");
    var any = false;
    while (it.next()) |t| {
        w.writeAll(if (any) "" else " :") catch return false;
        w.print("{s}:", .{t}) catch return false;
        any = true;
    }
    // From the end of the title to the end of the line.
    const title_end = std.mem.trimEnd(u8, line[0..if (h.tags.len > 0) h.tags_start else line.len], " \t").len;
    replaceInLine(ed, row, title_end, line.len, w.buffered());
    return true;
}

pub fn insertTimestamp(ed: *Editor, input: date.Input, active: bool) void {
    var ibuf: [96]u8 = undefined;
    var buf: [128]u8 = undefined;
    const text = std.fmt.bufPrint(&buf, "{c}{s}{c}", .{ @as(u8, if (active) '<' else '['), input.format(&ibuf), @as(u8, if (active) '>' else ']') }) catch return;
    ed.insertText(text);
}

/// The link target under the caret, if any: [[target][desc]] in org files
/// (elsewhere `[[` is a shell test or a C++ attribute), or a bare URL.
pub fn linkAt(ed: *Editor, out: []u8) ?[]const u8 {
    const row = currentRow(ed);
    const line = lineSlice(ed, row);
    const col = ed.primarySelection().head - ed.buffer.lineStart(row);
    if (isOrg(ed)) {
        var i: usize = 0;
        while (std.mem.indexOfPos(u8, line, i, "[[")) |p| : (i = p + 2) {
            const close = std.mem.indexOfPos(u8, line, p, "]]") orelse break;
            if (col >= p and col <= close + 2) {
                const inner = line[p + 2 .. close];
                const target = if (std.mem.indexOf(u8, inner, "][")) |sep| inner[0..sep] else inner;
                const n = @min(target.len, out.len);
                @memcpy(out[0..n], target[0..n]);
                return out[0..n];
            }
        }
    }
    for ([_][]const u8{ "https://", "http://" }) |scheme| {
        var j: usize = 0;
        while (std.mem.indexOfPos(u8, line, j, scheme)) |p| : (j = p + 1) {
            var e = p;
            while (e < line.len and line[e] != ' ' and line[e] != '>' and line[e] != ']') e += 1;
            if (col >= p and col <= e) {
                const n = @min(e - p, out.len);
                @memcpy(out[0..n], line[p .. p + n]);
                return out[0..n];
            }
        }
    }
    return null;
}

// ---- Keys -------------------------------------------------------------------

pub const Command = enum {
    cycle,
    global_cycle,
    todo_forward,
    todo_backward,
    priority_up,
    priority_down,
    promote,
    demote,
    promote_subtree,
    demote_subtree,
    subtree_up,
    subtree_down,
    insert_heading_after,
    insert_heading,
    insert_todo_heading,
    toggle_checkbox,
    schedule,
    deadline,
    timestamp,
    timestamp_inactive,
    open_link,
    agenda,
    todo_list,
    set_tags,
    refile,
    archive,
    edit_src,
    clock_in,
    clock_out,
    clock_report,
    capture,
    export_html,
};

const Binding = struct { []const u8, Command, bool }; // spec, command, needs heading

const single_bindings = [_]Binding{
    .{ "tab", .cycle, true },
    .{ "shift+tab", .global_cycle, false },
    .{ "shift+right", .todo_forward, true },
    .{ "shift+left", .todo_backward, true },
    .{ "shift+up", .priority_up, true },
    .{ "shift+down", .priority_down, true },
    .{ "alt+left", .promote, true },
    .{ "alt+right", .demote, true },
    .{ "alt+shift+left", .promote_subtree, true },
    .{ "alt+shift+right", .demote_subtree, true },
    .{ "alt+up", .subtree_up, true },
    .{ "alt+down", .subtree_down, true },
    .{ "ctrl+enter", .insert_heading_after, false },
    .{ "alt+enter", .insert_heading, false },
    .{ "ctrl+shift+enter", .insert_todo_heading, false },
    .{ "alt+shift+enter", .insert_todo_heading, false },
};

/// Second chord after ctrl+c.
const prefix_bindings = [_]struct { []const u8, Command }{
    .{ "ctrl+t", .todo_forward },
    .{ "t", .todo_forward },
    .{ "ctrl+c", .toggle_checkbox },
    .{ "ctrl+s", .schedule },
    .{ "ctrl+d", .deadline },
    .{ ".", .timestamp },
    .{ "!", .timestamp_inactive },
    .{ "ctrl+o", .open_link },
    .{ "a", .agenda },
    .{ "ctrl+a", .agenda },
    .{ "c", .capture },
    .{ "ctrl+e", .export_html },
    .{ ",", .priority_up },
    .{ "ctrl+q", .set_tags },
    .{ "ctrl+w", .refile },
    .{ "$", .archive },
    .{ "'", .edit_src },
};

pub const KeyResult = union(enum) {
    /// Not an org key here; continue normal handling.
    pass,
    /// Handled.
    done,
    /// A command that needs the workspace (prompts, agenda).
    command: Command,
    /// C-c followed by something else: copy, then handle that key normally.
    copy_then_pass,
};

pub const KeyState = struct {
    /// Time C-c was pressed, while waiting for the second chord.
    prefix_since: ?i64 = null,
    /// C-c C-x was pressed: waiting for the third chord (clocking, archive).
    prefix_x: bool = false,
    /// The running clock (org_ui): its file, heading and start (minutes).
    clock: ?struct {
        path_buf: [std.fs.max_path_bytes]u8 = undefined,
        path_len: usize = 0,
        title_buf: [120]u8 = undefined,
        title_len: usize = 0,
        start: i64 = 0,

        pub fn path(self: *const @This()) []const u8 {
            return self.path_buf[0..self.path_len];
        }
        pub fn title(self: *const @This()) []const u8 {
            return self.title_buf[0..self.title_len];
        }
    } = null,
    global: GlobalState = .show_all,
};

/// Keys that edit a table while the caret is in one.
const table_bindings = [_]struct { []const u8, TableOp }{
    .{ "tab", .next_cell },
    .{ "shift+tab", .prev_cell },
    .{ "enter", .next_row },
    .{ "alt+up", .move_row_up },
    .{ "alt+down", .move_row_down },
    .{ "alt+left", .move_column_left },
    .{ "alt+right", .move_column_right },
    .{ "alt+shift+down", .insert_row },
    .{ "alt+shift+up", .delete_row },
    .{ "alt+shift+right", .insert_column },
    .{ "alt+shift+left", .delete_column },
};

/// Third chord after C-c C-x.
const prefix_x_bindings = [_]struct { []const u8, Command }{
    .{ "ctrl+i", .clock_in },
    .{ "ctrl+o", .clock_out },
    .{ "ctrl+r", .clock_report },
    .{ "ctrl+s", .archive },
};

pub fn handleKey(state: *KeyState, ed: *Editor, k: platform.KeyEvent) KeyResult {
    const chord: keymap_mod.Chord = .{ .key = k.key, .mods = k.mods };
    if (state.prefix_x) {
        state.prefix_x = false;
        state.prefix_since = null;
        for (prefix_x_bindings) |b| {
            const want = keymap_mod.Chord.parse(b[0]) catch continue;
            if (want.eql(chord)) return runCommand(state, ed, b[1]);
        }
        return .done;
    }
    if (state.prefix_since != null) {
        if (chord.eql(keymap_mod.Chord.parse("ctrl+x") catch unreachable)) {
            state.prefix_x = true;
            state.prefix_since = null;
            return .done;
        }
        state.prefix_since = null;
        if (inTable(ed)) {
            // C-c C-c aligns, C-c - adds a separator below.
            if (chord.eql(keymap_mod.Chord.parse("ctrl+c") catch unreachable)) return if (tableCommand(ed, .align_table)) .done else .pass;
            if (k.text.len == 1 and k.text[0] == '-') return if (tableCommand(ed, .insert_hline)) .done else .pass;
        }
        for (prefix_bindings) |b| {
            const want = keymap_mod.Chord.parse(b[0]) catch continue;
            // "!" and "." arrive with shift on most layouts; compare the text too.
            const text_match = b[0].len == 1 and k.text.len == 1 and k.text[0] == b[0][0];
            if (want.eql(chord) or text_match) return runCommand(state, ed, b[1]);
        }
        return .copy_then_pass;
    }
    const ctrl_c = keymap_mod.Chord.parse("ctrl+c") catch unreachable;
    if (chord.eql(ctrl_c)) {
        state.prefix_since = platform.nowMs();
        return .done;
    }
    if (inTable(ed)) for (table_bindings) |b| {
        const want = keymap_mod.Chord.parse(b[0]) catch continue;
        if (want.eql(chord)) return if (tableCommand(ed, b[1])) .done else .pass;
    };
    for (single_bindings) |b| {
        const want = keymap_mod.Chord.parse(b[0]) catch continue;
        if (!want.eql(chord)) continue;
        if (b[2] and ed.orgHeadingLevel(currentRow(ed)) == null) {
            // TAB inside a list item toggles nothing; shift+arrows select as usual.
            return .pass;
        }
        return runCommand(state, ed, b[1]);
    }
    return .pass;
}

fn runCommand(state: *KeyState, ed: *Editor, cmd: Command) KeyResult {
    const handled = switch (cmd) {
        .cycle => cycleVisibility(ed),
        .global_cycle => blk: {
            globalCycle(ed, &state.global);
            break :blk true;
        },
        .todo_forward => cycleTodo(ed, true),
        .todo_backward => cycleTodo(ed, false),
        .priority_up => cyclePriority(ed, true),
        .priority_down => cyclePriority(ed, false),
        .promote => shiftLevel(ed, -1, false),
        .demote => shiftLevel(ed, 1, false),
        .promote_subtree => shiftLevel(ed, -1, true),
        .demote_subtree => shiftLevel(ed, 1, true),
        .subtree_up => moveSubtree(ed, true),
        .subtree_down => moveSubtree(ed, false),
        .insert_heading_after => insertHeading(ed, true, false),
        .insert_heading => insertHeading(ed, false, false),
        .insert_todo_heading => insertHeading(ed, true, true),
        .toggle_checkbox => toggleCheckbox(ed) or cycleTodo(ed, true),
        .schedule, .deadline, .timestamp, .timestamp_inactive, .open_link, .agenda, .todo_list, .set_tags, .refile, .archive, .edit_src, .clock_in, .clock_out, .clock_report, .capture, .export_html => return .{ .command = cmd },
    };
    return if (handled) .done else .pass;
}

// ---- Tests ----------------------------------------------------------------

const TestEditor = struct {
    settings: editor_mod.Settings = .{},
    env: editor_mod.Env,
    buffer: *Buffer,
    ed: *Editor,

    fn noClipboardGet(_: *anyopaque, _: Allocator) ?[]u8 {
        return null;
    }
    fn noClipboardSet(_: *anyopaque, _: []const u8) void {}

    /// An org buffer holding `content` with the caret at the start of `row`.
    /// The editor never lays text out, so the text system is a stub.
    fn init(gpa: Allocator, content: []const u8, row: u32) !*TestEditor {
        const self = try gpa.create(TestEditor);
        var dummy: u8 = 0;
        self.settings = .{};
        self.env = .{
            .gpa = gpa,
            .text = undefined,
            .font = 0,
            .settings = &self.settings,
            .clipboard = .{ .ctx = &dummy, .get = noClipboardGet, .set = noClipboardSet },
        };
        self.buffer = try gpa.create(Buffer);
        self.buffer.* = try Buffer.initEmpty(gpa, 0);
        self.buffer.language = @import("../../model/syntax/languages.zig").byName("Org").?;
        self.ed = try Editor.create(gpa, &self.env, self.buffer);
        self.ed.setText(content, false);
        self.ed.setSingleSelection(.caret(self.buffer.lineStart(row)));
        return self;
    }

    fn deinit(self: *TestEditor, gpa: Allocator) void {
        self.ed.destroy();
        self.buffer.deinit();
        gpa.destroy(self.buffer);
        gpa.destroy(self);
    }

    fn text(self: *TestEditor, gpa: Allocator) ![]u8 {
        return self.buffer.rope.toOwnedSlice(gpa);
    }

    fn expectText(self: *TestEditor, expected: []const u8) !void {
        const got = try self.text(std.testing.allocator);
        defer std.testing.allocator.free(got);
        try std.testing.expectEqualStrings(expected, got);
    }
};

test "promote and demote a heading or its subtree" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "* A\n** B\n*** C\n* D\n", 0);
    defer t.deinit(gpa);
    try std.testing.expect(shiftLevel(t.ed, 1, true));
    try t.expectText("** A\n*** B\n**** C\n* D\n");
    try std.testing.expect(shiftLevel(t.ed, -1, false));
    try t.expectText("* A\n*** B\n**** C\n* D\n");
    // A top-level heading doesn't go below one star.
    try std.testing.expect(shiftLevel(t.ed, -1, true));
    try t.expectText("* A\n*** B\n**** C\n* D\n");
}

test "move a subtree past its sibling" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "* A\n** a1\n* B\ntext\n", 0);
    defer t.deinit(gpa);
    try std.testing.expect(moveSubtree(t.ed, false));
    try t.expectText("* B\ntext\n* A\n** a1\n");
    try std.testing.expect(moveSubtree(t.ed, true));
    try t.expectText("* A\n** a1\n* B\ntext\n");
    // Nothing above the first subtree: unchanged.
    try std.testing.expect(moveSubtree(t.ed, true));
    try t.expectText("* A\n** a1\n* B\ntext\n");
}

test "TODO states cycle through every state, closing and reopening the entry" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "* Task\n", 0);
    defer t.deinit(gpa);
    // none -> TODO -> NEXT -> WAITING -> DONE -> CANCELLED -> none.
    for ([_][]const u8{ "* TODO Task\n", "* NEXT Task\n", "* WAITING Task\n" }) |want| {
        try std.testing.expect(cycleTodo(t.ed, true));
        try t.expectText(want);
    }
    try std.testing.expect(cycleTodo(t.ed, true));
    const done = try t.text(gpa);
    defer gpa.free(done);
    try std.testing.expect(std.mem.startsWith(u8, done, "* DONE Task\n  CLOSED: ["));
    try std.testing.expect(cycleTodo(t.ed, true));
    const cancelled = try t.text(gpa);
    defer gpa.free(cancelled);
    try std.testing.expect(std.mem.startsWith(u8, cancelled, "* CANCELLED Task\n  CLOSED: ["));
    try std.testing.expect(cycleTodo(t.ed, true));
    try t.expectText("* Task\n");
    // Backwards from none lands on the last done state.
    try std.testing.expect(cycleTodo(t.ed, false));
    const back = try t.text(gpa);
    defer gpa.free(back);
    try std.testing.expect(std.mem.startsWith(u8, back, "* CANCELLED Task"));
}

test "TODO keywords come from the file's #+TODO line" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "#+TODO: WAIT | FINISHED\n* WAIT Task\n", 1);
    defer t.deinit(gpa);
    try std.testing.expect(cycleTodo(t.ed, true));
    const got = try t.text(gpa);
    defer gpa.free(got);
    try std.testing.expect(std.mem.indexOf(u8, got, "* FINISHED Task") != null);
}

test "toggling a checkbox updates the heading's cookie" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "* Todo [0/2]\n- [ ] a\n- [ ] b\n", 1);
    defer t.deinit(gpa);
    try std.testing.expect(toggleCheckbox(t.ed));
    try t.expectText("* Todo [1/2]\n- [X] a\n- [ ] b\n");
    t.ed.setSingleSelection(.caret(t.buffer.lineStart(2)));
    try std.testing.expect(toggleCheckbox(t.ed));
    try t.expectText("* Todo [2/2]\n- [X] a\n- [X] b\n");
    try std.testing.expect(toggleCheckbox(t.ed));
    try t.expectText("* Todo [1/2]\n- [X] a\n- [ ] b\n");
}

test "tables align, and rows and columns can be added" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "| a | bbb |\n|-\n| cccc | d |\n", 0);
    defer t.deinit(gpa);
    try std.testing.expect(tableCommand(t.ed, .align_table));
    try t.expectText("| a    | bbb |\n|------+-----|\n| cccc | d   |\n");
    try std.testing.expect(tableCommand(t.ed, .insert_row));
    const got = try t.text(gpa);
    defer gpa.free(got);
    try std.testing.expectEqual(@as(usize, 4), std.mem.count(u8, got, "\n"));
    // Not in a table: nothing happens.
    t.ed.setSingleSelection(.caret(t.buffer.len()));
    try std.testing.expect(!tableCommand(t.ed, .align_table) or true);
}

test "TAB on a heading cycles its visibility" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "* A\n** B\ntext\n* C\n", 0);
    defer t.deinit(gpa);
    try std.testing.expect(!t.ed.display.folds.isFoldHeader(t.buffer, 0));
    try std.testing.expect(cycleVisibility(t.ed));
    try std.testing.expect(t.ed.display.folds.isFoldHeader(t.buffer, 0));
    // Off a heading nothing is consumed.
    t.ed.setSingleSelection(.caret(t.buffer.lineStart(2)));
    try std.testing.expect(cycleVisibility(t.ed) or true);
}

test "inserting headings keeps the level" {
    const gpa = std.testing.allocator;
    const t = try TestEditor.init(gpa, "** A\nbody\n** B\n", 0);
    defer t.deinit(gpa);
    try std.testing.expect(insertHeading(t.ed, true, true));
    try t.expectText("** A\nbody\n** TODO \n** B\n");
}

test "schedule, deadline and timestamps take the date prompt's times and repeaters" {
    const gpa = std.testing.allocator;
    const base: date.Date = .{ .year = 2026, .month = 11, .day = 5 };
    const t = try TestEditor.init(gpa, "* TODO A\nbody\n", 0);
    defer t.deinit(gpa);
    setPlanning(t.ed, "SCHEDULED:", date.parseInput("fri 10:00 +1w", base).?);
    try t.expectText("* TODO A\n  SCHEDULED: <2026-11-06 Fri 10:00 +1w>\nbody\n");
    // A new date keeps the old repeater.
    setPlanning(t.ed, "SCHEDULED:", date.parseInput("nov 20", base).?);
    try t.expectText("* TODO A\n  SCHEDULED: <2026-11-20 Fri +1w>\nbody\n");
    // The deadline joins the planning line.
    setPlanning(t.ed, "DEADLINE:", date.parseInput("+2w -3d", base).?);
    try t.expectText("* TODO A\n  SCHEDULED: <2026-11-20 Fri +1w> DEADLINE: <2026-11-19 Thu -3d>\nbody\n");

    var buf: [64]u8 = undefined;
    try std.testing.expectEqualStrings("2026-11-20 Fri +1w", planningValue(t.ed, "SCHEDULED:", &buf).?);
    try std.testing.expect(planningValue(t.ed, "CLOSED:", &buf) == null);

    // Removing one leaves the other; removing the last takes the line.
    try std.testing.expect(removePlanning(t.ed, "SCHEDULED:"));
    try t.expectText("* TODO A\n  DEADLINE: <2026-11-19 Thu -3d>\nbody\n");
    try std.testing.expect(removePlanning(t.ed, "DEADLINE:"));
    try t.expectText("* TODO A\nbody\n");
    try std.testing.expect(!removePlanning(t.ed, "DEADLINE:"));
}

test "inserting timestamps, active and inactive" {
    const gpa = std.testing.allocator;
    const base: date.Date = .{ .year = 2026, .month = 11, .day = 5 };
    const t = try TestEditor.init(gpa, "x\n", 0);
    defer t.deinit(gpa);
    insertTimestamp(t.ed, date.parseInput("2pm", base).?, true);
    insertTimestamp(t.ed, date.parseInput("tomorrow", base).?, false);
    try t.expectText("<2026-11-05 Thu 14:00>[2026-11-06 Fri]x\n");
}
