//! Vim folding (`:help folding`, `:help fold-commands`) on top of the
//! editor's folds.
//!
//! Model. Each view's folds form a tree (model/text/fold_tree.zig) of
//!  - automatic folds from 'foldmethod': "indent" (the default: the editor's
//!    indentation folds, the same ones as ctrl+shift+[ and the gutter, and
//!    heading subtrees in Org files) or "syntax" (tree-sitter nodes, see
//!    model/syntax/syntax_folds.zig; indent when there is no grammar), and
//!  - manual folds made with zf / zF / :fold, kept in the FoldMap so they
//!    move with edits. Unlike Vim they also work with foldmethod=indent: the
//!    two kinds nest into one tree. zd / zD / zE delete only manual folds
//!    (E351 on an automatic one, as Vim says for foldmethod=indent).
//!    `:set foldmethod=manual` turns the current automatic folds into manual
//!    ones and stops computing new ones, as in Vim; leaving manual drops them.
//! A fold covers rows [header, last]: when closed the header stays visible
//! with "…" and is one line for motions and operators (Vim's marker / syntax
//! folds look the same). Open/closed state is the FoldMap's set of closed
//! folds, which Sublime's fold commands and the gutter share. A closed range
//! without a definition (Sublime folding a selection) is a fold until opened.
//! 'foldlevel' starts unset, meaning every fold open (zm then closes the
//! deepest level); 'nofoldenable' (zn zi) sets the closed folds aside.

const std = @import("std");
const vim_mod = @import("vim.zig");
const editor_mod = @import("../editor/editor.zig");
const motions = @import("motions.zig");
const platform = @import("../../framework/platform/platform.zig");
const fold_tree = @import("../../model/text/fold_tree.zig");
const fold_map = @import("../../model/text/fold_map.zig");
const syntax_folds = @import("../../model/syntax/syntax_folds.zig");

const Vim = vim_mod.Vim;
const Editor = editor_mod.Editor;
const FoldTree = fold_tree.FoldTree;
const FoldMap = fold_map.FoldMap;
const Ctx = motions.Ctx;

// ---- Closed folds as lines --------------------------------------------------

/// First row of the closed fold `row` is in (`row` when it isn't folded).
pub fn closedStart(ed: *Editor, row: u32) u32 {
    return ed.display.folds.closedStart(ed.gpa, ed.buffer, row);
}

/// Last row of the closed fold `row` is in (`row` when it isn't folded).
pub fn closedEnd(ed: *Editor, row: u32) u32 {
    return ed.display.folds.closedEnd(ed.gpa, ed.buffer, row);
}

fn inClosedFold(ed: *Editor, row: u32) bool {
    return ed.display.folds.inClosedFold(ed.gpa, ed.buffer, row);
}

/// `n` lines down from `row` (j, + with an operator), a closed fold being one line.
pub fn linesDown(ed: *Editor, row: u32, n: usize) u32 {
    return ed.display.folds.linesDown(ed.gpa, ed.buffer, row, n);
}

/// `n` lines up from `row`, a closed fold being one line.
pub fn linesUp(ed: *Editor, row: u32, n: usize) u32 {
    return ed.display.folds.linesUp(ed.gpa, ed.buffer, row, n);
}

/// Rows of `n` lines starting at `row` (dd, yy, cc, >>, zF), a closed fold
/// being one line.
pub fn lineSpan(ed: *Editor, row: u32, n: usize) [2]u32 {
    const first = closedStart(ed, row);
    return .{ first, closedEnd(ed, linesDown(ed, first, n -| 1)) };
}

/// An operator takes a closed fold whole (`:help fold-behavior`): a range
/// starting in one starts at its first line, one ending in it ends with its
/// last line ("dl" deletes the whole closed fold).
pub fn includeClosed(ed: *Editor, ranges: []motions.Range) void {
    for (ranges) |*r| {
        const s, const e = ed.display.folds.wholeFolds(ed.gpa, ed.buffer, r.start, r.end, r.linewise);
        r.start = s;
        r.end = e;
    }
}

/// Cursors inside a closed fold move to its first line, keeping their
/// column (Vim's foldAdjustCursor).
pub fn adjustCursors(ed: *Editor) void {
    if (ed.display.folds.isEmpty()) return;
    var c = Ctx.init(ed.buffer);
    var moved = false;
    for (ed.selections.items) |*sel| {
        const row = c.row(sel.head);
        if (!ed.isRowHidden(row)) continue;
        sel.* = .caret(c.clampNormal(ed.offsetForX(closedStart(ed, row), ed.xForOffset(sel.head))));
        moved = true;
    }
    if (moved) ed.normalize();
}

/// After a Normal mode command, a cursor left inside a closed fold opens it
/// where Vim's default 'foldopen' would (searches, marks, %, horizontal
/// motions, undo) and otherwise (j k G gg H L ctrl-d ...) moves to the
/// fold's first line.
pub fn afterCommand(ed: *Editor, k: platform.KeyEvent) void {
    if (ed.display.folds.isEmpty()) return;
    var hidden = false;
    for (ed.selections.items) |sel| {
        if (ed.isRowHidden(ed.buffer.offsetToPoint(sel.head).row)) {
            hidden = true;
            break;
        }
    }
    if (!hidden) return;
    if (verticalKey(k)) adjustCursors(ed) else ed.revealCarets();
}

fn verticalKey(k: platform.KeyEvent) bool {
    if (k.key == .named) return switch (k.key.named) {
        .up, .down, .page_up, .page_down, .enter => true,
        else => false,
    };
    if (k.mods.ctrl) return switch (k.key.char) {
        'd', 'u', 'f', 'b', 'e', 'y' => true,
        else => false,
    };
    const ch: u21 = if (k.text.len > 0) k.text[0] else k.key.char;
    return ch < 128 and std.mem.indexOfScalar(u8, "jkGgHLM+-_", @intCast(ch)) != null;
}

/// `o`: opens a line below the cursor's line, or below the last line of the
/// closed fold it is on (the new line stays out of the fold).
pub fn openLineBelow(ed: *Editor) void {
    var c = Ctx.init(ed.buffer);
    var on_fold = false;
    for (ed.selections.items) |sel| {
        if (inClosedFold(ed, c.row(sel.head))) on_fold = true;
    }
    if (!on_fold) return ed.insertLine(true);
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    var reps: std.ArrayList(Editor.Replacement) = .empty;
    for (ed.selections.items) |sel| {
        const last = closedEnd(ed, c.row(sel.head));
        // Autoindent from that line, as Vim does.
        const indent = ed.buffer.textRange(alloc, ed.buffer.lineStart(last), ed.firstNonWhitespace(last)) catch return;
        var rep: Editor.Replacement = undefined;
        if (last + 1 < ed.buffer.lineCount()) {
            const at = ed.buffer.lineStart(last + 1);
            const text = std.mem.concat(alloc, u8, &.{ indent, "\n" }) catch return;
            rep = .{ .start = at, .end = at, .text = text, .sel_anchor = indent.len, .sel_head = indent.len };
        } else {
            const at = ed.buffer.lineEnd(last);
            const text = std.mem.concat(alloc, u8, &.{ "\n", indent }) catch return;
            rep = .{ .start = at, .end = at, .text = text, .sel_anchor = text.len, .sel_head = text.len };
        }
        // Several cursors on one fold open one line.
        for (reps.items) |r| {
            if (r.start == rep.start) break;
        } else reps.append(alloc, rep) catch return;
    }
    // A fold ending the buffer would otherwise take in the new line.
    ed.display.folds.keep_ends = true;
    defer ed.display.folds.keep_ends = false;
    ed.applyReplacements(reps.items, .other);
}

// ---- The fold tree ----------------------------------------------------------

/// The fold tree of `ed`'s view (see the file comment).
pub fn buildTree(ed: *Editor) !FoldTree {
    const gpa = ed.gpa;
    const fm = &ed.display.folds;
    var auto: std.ArrayList([2]u32) = .empty;
    defer auto.deinit(gpa);
    var manual: std.ArrayList([2]u32) = .empty;
    defer manual.deinit(gpa);
    var closed: std.ArrayList([2]u32) = .empty;
    defer closed.deinit(gpa);
    try autoFolds(ed, &auto);
    try FoldMap.appendRows(gpa, ed.buffer, fm.manual.items, &manual);
    try FoldMap.appendRows(gpa, ed.buffer, fm.closedFolds(), &closed);
    return FoldTree.build(gpa, .{ .auto = auto.items, .manual = manual.items, .closed = closed.items });
}

/// The folds 'foldmethod' defines, as [header, last] rows.
pub fn autoFolds(ed: *Editor, out: *std.ArrayList([2]u32)) !void {
    const gpa = ed.gpa;
    switch (ed.display.folds.method) {
        .manual => return,
        .syntax => if (ed.buffer.syntax) |*syn| {
            syn.update(&ed.buffer.rope);
            if (syn.tree != null) return syntax_folds.foldRanges(gpa, syn, &ed.buffer.rope, out);
        },
        .indent => {},
    }
    const rows = ed.buffer.lineCount();
    if (std.mem.eql(u8, ed.buffer.language.name, "Org")) {
        var r: u32 = 0;
        while (r < rows) : (r += 1) {
            if (ed.orgHeadingLevel(r) == null) continue;
            if (ed.foldEndFor(r)) |last| try out.append(gpa, .{ r, last });
        }
        return;
    }
    const indents = try gpa.alloc(?u32, rows);
    defer gpa.free(indents);
    for (indents, 0..) |*w, r| w.* = ed.lineIndentWidth(@intCast(r));
    try fold_tree.indentFolds(gpa, indents, out);
}

/// Writes the tree's open/closed state back to the view.
fn commit(ed: *Editor, tree: *const FoldTree) void {
    var rows: std.ArrayList([2]u32) = .empty;
    defer rows.deinit(ed.gpa);
    tree.closedRanges(ed.gpa, &rows) catch return;
    ed.display.folds.setClosed(ed.gpa, ed.buffer, rows.items) catch return;
    adjustCursors(ed);
}

/// Cursor rows, each once.
fn cursorRows(vim: *Vim, ed: *Editor, out: *std.ArrayList(u32)) void {
    var c = Ctx.init(ed.buffer);
    const heads = if (vim.isVisual()) vim.visual_cursors.items else null;
    const n = if (heads) |h| h.len else ed.selections.items.len;
    for (0..n) |i| {
        const row = c.row(if (heads) |h| h[i] else ed.selections.items[i].head);
        if (std.mem.indexOfScalar(u32, out.items, row) == null) out.append(ed.gpa, row) catch return;
    }
}

fn noFold(vim: *Vim) void {
    vim.setMessageText("E490: No fold found");
}

// ---- Commands ---------------------------------------------------------------

/// z{ch} in Normal mode (also the second key of an operator's `dzj`).
/// Returns null for z commands that aren't about folds (zt, zz, ...), else
/// whether the command is complete.
pub fn normal(vim: *Vim, ed: *Editor, ch: u21) ?bool {
    if (vim.operator) |op| {
        switch (ch) {
            'j', 'k' => motion(vim, ed, true, ch == 'j'),
            // zfzf is zF.
            'f' => if (op == .fold) createLines(vim, ed),
            else => {},
        }
        return true;
    }
    const gpa = ed.gpa;
    const fm = &ed.display.folds;
    const count: usize = @max(vim.count, 1);
    switch (ch) {
        'f' => {
            vim.operator = .fold;
            return false;
        },
        'F' => createLines(vim, ed),
        'j', 'k' => motion(vim, ed, true, ch == 'j'),
        'o', 'O', 'c', 'C', 'a', 'A', 'v' => atCursors(vim, ed, ch, count),
        'x', 'X' => {
            // Undo manual opening / closing: re-apply 'foldlevel' (zx then zv).
            var tree = buildTree(ed) catch return true;
            defer tree.deinit();
            tree.applyLevel(fm.level orelse tree.deepest());
            if (ch == 'x') openCursorLines(vim, ed, &tree);
            commit(ed, &tree);
        },
        'M', 'R', 'm', 'r' => {
            var tree = buildTree(ed) catch return true;
            defer tree.deinit();
            const deepest = tree.deepest();
            const old = fm.level orelse deepest;
            const level: u32 = switch (ch) {
                'M' => 0,
                'R' => deepest,
                'm' => old -| @as(u32, @intCast(@min(count, std.math.maxInt(u32)))),
                else => @min(deepest, old +| @as(u32, @intCast(@min(count, std.math.maxInt(u32))))),
            };
            fm.level = level;
            if (ch == 'r' and level == old) return true;
            // Folding more turns folding on.
            if (ch == 'M' or ch == 'm') fm.setEnabled(gpa, true) catch {};
            tree.applyLevel(level);
            commit(ed, &tree);
        },
        'n' => fm.setEnabled(gpa, false) catch {},
        'N' => {
            fm.setEnabled(gpa, true) catch {};
            adjustCursors(ed);
        },
        'i' => {
            fm.setEnabled(gpa, fm.disabled) catch {};
            adjustCursors(ed);
        },
        'd', 'D' => {
            var rows: std.ArrayList(u32) = .empty;
            defer rows.deinit(gpa);
            cursorRows(vim, ed, &rows);
            var ranges: std.ArrayList([2]u32) = .empty;
            defer ranges.deinit(gpa);
            for (rows.items) |r| ranges.append(gpa, .{ r, r }) catch return true;
            deleteFolds(vim, ed, ranges.items, ch == 'D');
        },
        'E' => eliminate(ed),
        else => return null,
    }
    ed.ensureCaretVisible(false);
    return true;
}

/// z{ch} in Visual mode: zf, zd, zD, zo, zO, zc and zC act on the selected
/// lines and end Visual mode; zj / zk move the cursor; the rest act on the
/// cursor line as in Normal mode.
pub fn visual(vim: *Vim, ed: *Editor, ch: u21) void {
    switch (ch) {
        'j', 'k' => return motion(vim, ed, true, ch == 'j'),
        'f', 'd', 'D', 'o', 'O', 'c', 'C' => {},
        else => {
            vim.exitVisual(ed);
            _ = normal(vim, ed, ch);
            return;
        },
    }
    var c = Ctx.init(ed.buffer);
    var rows: std.ArrayList([2]u32) = .empty;
    defer rows.deinit(ed.gpa);
    for (ed.selections.items) |sel| {
        const last = if (sel.end() > sel.start()) c.row(sel.end() - 1) else c.row(sel.end());
        rows.append(ed.gpa, .{ c.row(sel.start()), last }) catch return;
    }
    if (rows.items.len == 0) return;
    vim.exitVisual(ed);
    // The cursor goes to the start of the area, like after an operator.
    ed.setSingleSelection(.caret(c.clampNormal(ed.buffer.lineStart(rows.items[0][0]))));
    switch (ch) {
        'f' => create(ed, rows.items),
        'd', 'D' => deleteFolds(vim, ed, rows.items, ch == 'D'),
        else => openCloseRange(vim, ed, rows.items, ch == 'o' or ch == 'O', ch == 'O' or ch == 'C'),
    }
    ed.ensureCaretVisible(false);
}

/// zo zO zc zC za zA zv at every cursor.
fn atCursors(vim: *Vim, ed: *Editor, ch: u21, count: usize) void {
    const fm = &ed.display.folds;
    // Closing turns folding on (the tree reads the same closed folds either way).
    if (ch == 'c' or ch == 'C') fm.setEnabled(ed.gpa, true) catch {};
    var tree = buildTree(ed) catch return;
    defer tree.deinit();
    var rows: std.ArrayList(u32) = .empty;
    defer rows.deinit(ed.gpa);
    cursorRows(vim, ed, &rows);
    var missing = false;
    for (rows.items) |row| {
        const found = switch (ch) {
            'o' => tree.setFoldRepeat(row, count, true),
            'O' => tree.setFold(row, true, true),
            'c' => tree.setFoldRepeat(row, count, false),
            'C' => tree.setFold(row, false, true),
            'a', 'A' => blk: {
                // On a closed fold open it, else close one (and turn folding on).
                if (!fm.disabled and tree.closedAt(row) != null)
                    break :blk if (ch == 'a') tree.setFoldRepeat(row, count, true) else tree.setFold(row, true, true);
                fm.setEnabled(ed.gpa, true) catch {};
                break :blk if (ch == 'a') tree.setFoldRepeat(row, count, false) else tree.setFold(row, false, true);
            },
            else => blk: {
                openRow(&tree, row);
                break :blk true;
            },
        };
        if (!found) missing = true;
    }
    if (missing) noFold(vim);
    commit(ed, &tree);
}

/// zv: opens just the folds that hide `row`.
fn openRow(tree: *FoldTree, row: u32) void {
    while (tree.closedAt(row)) |i| tree.nodes.items[i].closed = false;
}

fn openCursorLines(vim: *Vim, ed: *Editor, tree: *FoldTree) void {
    var rows: std.ArrayList(u32) = .empty;
    defer rows.deinit(ed.gpa);
    cursorRows(vim, ed, &rows);
    for (rows.items) |r| openRow(tree, r);
}

/// zo / zc (one level) or zO / zC over row ranges: Visual mode, :foldopen,
/// :foldclose.
pub fn openCloseRange(vim: *Vim, ed: *Editor, ranges: []const [2]u32, opening: bool, recurse: bool) void {
    if (!opening) ed.display.folds.setEnabled(ed.gpa, true) catch {};
    var tree = buildTree(ed) catch return;
    defer tree.deinit();
    var found = false;
    for (ranges) |r| found = tree.setFoldRange(r[0], r[1], opening, recurse) or found;
    if (!found) noFold(vim);
    commit(ed, &tree);
}

/// zf{motion}, {Visual}zf, zF, :fold — a manual fold over each row range,
/// created closed (as Vim does for manual folds); turns folding on.
pub fn create(ed: *Editor, ranges: []const [2]u32) void {
    const fm = &ed.display.folds;
    fm.setEnabled(ed.gpa, true) catch {};
    for (ranges) |r| {
        // A fold hides the lines after its first, so it needs two.
        if (r[1] <= r[0]) continue;
        fm.addManual(ed.gpa, ed.buffer, r[0], r[1]) catch return;
        fm.fold(ed.gpa, ed.buffer, r[0], r[1]) catch return;
    }
    adjustCursors(ed);
}

/// The `.fold` operator over motion / text object ranges.
pub fn createRanges(ed: *Editor, ranges: []const motions.Range) void {
    var c = Ctx.init(ed.buffer);
    var rows: std.ArrayList([2]u32) = .empty;
    defer rows.deinit(ed.gpa);
    for (ranges) |r| {
        const last = if (r.end > r.start) c.row(r.end - 1) else c.row(r.end);
        rows.append(ed.gpa, .{ c.row(r.start), last }) catch return;
    }
    create(ed, rows.items);
}

/// zF: a fold over [count] lines at each cursor.
fn createLines(vim: *Vim, ed: *Editor) void {
    const n = @max(vim.count, 1) * @max(vim.op_count, 1);
    var c = Ctx.init(ed.buffer);
    var rows: std.ArrayList([2]u32) = .empty;
    defer rows.deinit(ed.gpa);
    for (ed.selections.items) |sel| rows.append(ed.gpa, lineSpan(ed, c.row(sel.head), n)) catch return;
    create(ed, rows.items);
}

/// zd / zD (recursive) over row ranges. Only manual folds can be deleted
/// (and closed ranges without a definition, which simply open).
fn deleteFolds(vim: *Vim, ed: *Editor, ranges: []const [2]u32, recurse: bool) void {
    var tree = buildTree(ed) catch return;
    defer tree.deinit();
    var targets: std.ArrayList(u32) = .empty;
    defer targets.deinit(ed.gpa);
    for (ranges) |r| tree.deleteRange(r[0], r[1], false, &targets) catch return;
    if (targets.items.len == 0) return noFold(vim);
    var refused = false;
    for (targets.items) |i| {
        if (!deletable(&tree, i)) {
            refused = true;
            continue;
        }
        deleteNode(ed, &tree, i);
        if (recurse) {
            var j = i + 1;
            while (j < tree.nodes.items[i].end) : (j += 1) if (deletable(&tree, j)) deleteNode(ed, &tree, j);
        }
    }
    if (refused) vim.setMessageText("E351: Cannot delete fold with current 'foldmethod'");
    commit(ed, &tree);
}

fn deletable(tree: *const FoldTree, i: u32) bool {
    const node = tree.nodes.items[i];
    return node.manual or !node.auto;
}

fn deleteNode(ed: *Editor, tree: *FoldTree, i: u32) void {
    const node = &tree.nodes.items[i];
    if (node.manual) _ = ed.display.folds.removeManual(ed.buffer, node.first, node.last);
    node.manual = false;
    // Its lines show again; folds nested in it keep their state.
    node.closed = false;
}

/// zE: deletes every manual fold (automatic ones stay, see the file comment).
fn eliminate(ed: *Editor) void {
    var tree = buildTree(ed) catch return;
    defer tree.deinit();
    for (0..tree.nodes.items.len) |i| {
        if (deletable(&tree, @intCast(i))) deleteNode(ed, &tree, @intCast(i));
    }
    ed.display.folds.manual.clearRetainingCapacity();
    commit(ed, &tree);
}

// ---- Motions ----------------------------------------------------------------

/// zj / zk (`updown`) and ]z / [z for every cursor: moves the cursors (to
/// column 0, like Vim) or, with an operator pending, applies it over the
/// motion — an exclusive one, so `dzj` from the start of a line deletes the
/// lines up to the next fold.
pub fn motion(vim: *Vim, ed: *Editor, updown: bool, forward: bool) void {
    var tree = buildTree(ed) catch return;
    defer tree.deinit();
    const n = @max(vim.count, 1) * @max(vim.op_count, 1);
    var c = Ctx.init(ed.buffer);
    if (vim.operator) |op| {
        const ranges = ed.gpa.alloc(motions.Range, ed.selections.items.len) catch return;
        defer ed.gpa.free(ranges);
        for (ed.selections.items, ranges) |sel, *r| {
            // Vim cancels the operator when the motion fails.
            const target = tree.moveTo(c.row(sel.head), updown, forward, n) orelse return;
            r.* = exclusiveRange(ed, &c, sel.head, ed.buffer.lineStart(target));
        }
        includeClosed(ed, ranges);
        vim.operate(ed, op, ranges);
        return;
    }
    if (vim.isVisual()) {
        for (vim.visual_cursors.items) |*cur| {
            if (tree.moveTo(c.row(cur.*), updown, forward, n)) |t| cur.* = ed.buffer.lineStart(t);
        }
        vim.syncVisual(ed);
        ed.ensureCaretVisible(false);
        return;
    }
    ed.pushSelectionHistory();
    for (ed.selections.items) |*sel| {
        if (tree.moveTo(c.row(sel.head), updown, forward, n)) |t| sel.* = .caret(ed.buffer.lineStart(t));
    }
    ed.normalize();
    ed.ensureCaretVisible(false);
}

/// An exclusive motion between `a` and `b`, with Vim's rules for an end in
/// column 0 (`:help exclusive-linewise`).
fn exclusiveRange(ed: *Editor, c: *Ctx, a: usize, b: usize) motions.Range {
    const lo = @min(a, b);
    const hi = @max(a, b);
    if (hi > lo and hi == c.lineStart(hi)) {
        const lo_row = c.row(lo);
        const hi_row = c.row(hi);
        if (lo <= c.firstNonBlank(lo_row)) {
            const last = hi_row - 1;
            const end = if (last + 1 < ed.buffer.lineCount()) ed.buffer.lineStart(last + 1) else ed.buffer.len();
            return .{ .start = ed.buffer.lineStart(lo_row), .end = end, .linewise = true };
        }
        return .{ .start = lo, .end = ed.buffer.lineEnd(hi_row - 1) };
    }
    return .{ .start = lo, .end = hi };
}

// ---- Ex ---------------------------------------------------------------------

/// Is `name` an abbreviation of `full` at least `min` long (`:fo` for `:fold`)?
fn abbrev(name: []const u8, full: []const u8, min: usize) bool {
    return name.len >= min and name.len <= full.len and std.mem.eql(u8, name, full[0..name.len]);
}

/// :[range]fo[ld], :[range]foldo[pen][!], :[range]foldc[lose][!]; false
/// when `name` isn't one of them.
pub fn exCommand(vim: *Vim, ed: *Editor, name: []const u8, bang: bool, first: u32, last: u32) bool {
    if (abbrev(name, "fold", 2)) {
        create(ed, &.{.{ first, last }});
    } else if (abbrev(name, "foldopen", 5)) {
        openCloseRange(vim, ed, &.{.{ first, last }}, true, bang);
    } else if (abbrev(name, "foldclose", 5)) {
        openCloseRange(vim, ed, &.{.{ first, last }}, false, bang);
    } else return false;
    ed.ensureCaretVisible(false);
    return true;
}

/// Ex ranges take closed folds whole (`:help fold-behavior`).
pub fn wholeFolds(ed: *Editor, range: anytype) @TypeOf(range) {
    var r = range;
    r.first = closedStart(ed, r.first);
    r.last = closedEnd(ed, r.last);
    return r;
}

/// :set foldlevel=N (fdl), [no|inv]foldenable (fen), foldmethod=indent|
/// syntax|expr|manual (fdm), and `?` to show one. False for other options.
pub fn setOption(vim: *Vim, ed: *Editor, key_in: []const u8, value: []const u8) bool {
    const fm = &ed.display.folds;
    const eql = std.mem.eql;
    const show = key_in.len > 0 and key_in[key_in.len - 1] == '?';
    const key = if (show) key_in[0 .. key_in.len - 1] else key_in;
    var buf: [64]u8 = undefined;
    if (eql(u8, key, "foldlevel") or eql(u8, key, "fdl")) {
        if (show or value.len == 0) {
            var tree = buildTree(ed) catch return true;
            defer tree.deinit();
            vim.setMessageText(std.fmt.bufPrint(&buf, "foldlevel={d}", .{fm.level orelse tree.deepest()}) catch "");
            return true;
        }
        const level = std.fmt.parseInt(u32, value, 10) catch {
            vim.setMessageText("E521: Number required after =");
            return true;
        };
        fm.level = level;
        var tree = buildTree(ed) catch return true;
        defer tree.deinit();
        tree.applyLevel(level);
        commit(ed, &tree);
    } else if (eql(u8, key, "foldenable") or eql(u8, key, "fen")) {
        if (show) {
            vim.setMessageText(if (fm.disabled) "nofoldenable" else "  foldenable");
        } else {
            fm.setEnabled(ed.gpa, true) catch {};
            adjustCursors(ed);
        }
    } else if (eql(u8, key, "nofoldenable") or eql(u8, key, "nofen")) {
        fm.setEnabled(ed.gpa, false) catch {};
    } else if (eql(u8, key, "invfoldenable") or eql(u8, key, "invfen")) {
        fm.setEnabled(ed.gpa, fm.disabled) catch {};
        adjustCursors(ed);
    } else if (eql(u8, key, "foldmethod") or eql(u8, key, "fdm")) {
        if (show or value.len == 0) {
            vim.setMessageText(std.fmt.bufPrint(&buf, "foldmethod={t}", .{fm.method}) catch "");
            return true;
        }
        // nvim's treesitter folds are a foldexpr.
        const method: FoldMap.Method = if (eql(u8, value, "expr")) .syntax else std.meta.stringToEnum(FoldMap.Method, value) orelse {
            vim.setMessageText(std.fmt.bufPrint(&buf, "E474: Invalid argument: foldmethod={s}", .{value}) catch "E474");
            return true;
        };
        setMethod(ed, method);
    } else return false;
    return true;
}

/// Changing 'foldmethod' to manual keeps the folds it made as manual ones;
/// leaving manual drops the manual folds (Vim recreates folds from the new
/// method). Open/closed state stays.
fn setMethod(ed: *Editor, method: FoldMap.Method) void {
    const fm = &ed.display.folds;
    if (method == fm.method) return;
    if (method == .manual) {
        var auto: std.ArrayList([2]u32) = .empty;
        defer auto.deinit(ed.gpa);
        autoFolds(ed, &auto) catch return;
        for (auto.items) |r| fm.addManual(ed.gpa, ed.buffer, r[0], r[1]) catch return;
    } else if (fm.method == .manual) {
        fm.manual.clearRetainingCapacity();
    }
    fm.method = method;
}
