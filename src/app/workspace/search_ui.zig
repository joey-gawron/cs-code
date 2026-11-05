//! Find in Files results as a tree in the bottom panel, like the .NET
//! app's Search view: a summary line, then files (chevron, icon, name, the
//! folder dimmed, a count badge) with their matching lines under them (line
//! number, the line with its matches highlighted). Click a line to open the
//! file at the match; Up/Down/Left/Right/Enter/Delete drive it from the
//! keyboard; F4 / Shift+F4 step through the matches. With a replacement typed
//! in the find panel each match shows what it would become, and Replace All
//! applies it to the open files (left unsaved, as Replace in Files always
//! was). The searching itself is search.zig's worker; this file only shows,
//! navigates and edits what came back.

const std = @import("std");
const builtin = @import("builtin");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const find_panel = @import("find_panel.zig");
const quick_panel = @import("quick_panel.zig");
const platform = @import("../../framework/platform/platform.zig");
const ui = @import("../../framework/ui/ui.zig");
const search = @import("../../model/project/search.zig");
const search_tree = @import("../../model/project/search_tree.zig");
const regex_mod = @import("../../framework/util/regex.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Rect = ui.Rect;
const Color = ui.Color;

pub const row_h: f32 = 22;
const summary_h: f32 = 26;
/// The prompt tag of the Replace All confirmation.
pub const replace_tag: u32 = 410;

/// What the panel header's buttons do.
pub const Action = enum { clear, collapse_all, open_in_editor, replace_all };

/// A row: a file (`match` null) or one of its matching lines.
pub const Sel = struct { file: u32, match: ?u32 = null };

/// A painted row and its buttons (empty rects when it has none).
const RowHit = struct { rect: Rect, sel: Sel, dismiss: Rect = .{}, replace: Rect = .{} };

pub const State = struct {
    tree: ?search_tree.Tree = null,
    /// The query that produced the tree (owned); replace follows it, not the form.
    pattern: []u8 = &.{},
    regex: bool = false,
    case_sensitive: bool = false,
    /// The regex of a regex query, for replace previews and groups.
    re: ?regex_mod.Regex = null,
    searching: bool = false,
    files_searched: usize = 0,
    truncated: bool = false,
    regex_error: bool = false,
    started_ms: i64 = 0,
    elapsed_ms: i64 = 0,
    /// Why the tree is empty after results were there: the summary says so.
    emptied_by: enum { none, replace, dismiss } = .none,
    /// The results text and match ranges, for Open in Editor (owned).
    text: []u8 = &.{},
    ranges: [][2]usize = &.{},
    sel: ?Sel = null,
    /// Which hit of the selected line F4 is on.
    hit: u32 = 0,
    scroll_y: f32 = 0,
    /// From the last paint.
    rect: Rect = .{},
    rows: std.ArrayList(RowHit) = .empty,
};

pub fn deinit(ws: *Workspace) void {
    const st = &ws.panel.search;
    freeResults(ws.gpa, st);
    st.rows.deinit(ws.gpa);
}

fn freeResults(gpa: Allocator, st: *State) void {
    if (st.tree) |*t| t.deinit();
    st.tree = null;
    if (st.re) |*r| r.deinit();
    st.re = null;
    gpa.free(st.pattern);
    gpa.free(st.text);
    gpa.free(st.ranges);
    st.pattern = &.{};
    st.text = &.{};
    st.ranges = &.{};
    st.sel = null;
    st.scroll_y = 0;
}

// ---- Receiving results --------------------------------------------------------------

/// A search started: the old results go, the summary says so.
pub fn begin(ws: *Workspace, q: search.Query) void {
    const st = &ws.panel.search;
    freeResults(ws.gpa, st);
    st.pattern = ws.gpa.dupe(u8, q.pattern) catch &.{};
    st.regex = q.regex;
    st.case_sensitive = q.case_sensitive;
    st.searching = true;
    st.emptied_by = .none;
    st.regex_error = false;
    st.truncated = false;
    st.files_searched = 0;
    st.started_ms = platform.nowMs();
}

/// The search finished with `r`.
pub fn finish(ws: *Workspace, r: *const search.Results, q: search.Query) void {
    const st = &ws.panel.search;
    const gpa = ws.gpa;
    st.searching = false;
    st.elapsed_ms = platform.nowMs() - st.started_ms;
    st.files_searched = r.files_searched;
    st.truncated = r.truncated;
    st.regex_error = r.regex_error;
    st.emptied_by = .none;
    if (st.tree) |*t| t.deinit();
    st.tree = search_tree.build(gpa, r.text, r.matches) catch null;
    gpa.free(st.text);
    gpa.free(st.ranges);
    st.text = gpa.dupe(u8, r.text) catch &.{};
    st.ranges = gpa.dupe([2]usize, r.matches) catch &.{};
    if (st.re) |*old| old.deinit();
    st.re = if (q.regex) regex_mod.Regex.compile(gpa, q.pattern, .{ .case_insensitive = !q.case_sensitive }) catch null else null;
    st.sel = null;
    if (st.tree) |t| if (t.files.len > 0) {
        st.sel = .{ .file = 0 };
    };
    st.scroll_y = 0;
}

pub fn hasResults(ws: *const Workspace) bool {
    const t = ws.panel.search.tree orelse return false;
    return t.files.len > 0;
}

/// The Open in Editor button: the results as a Find Results document.
fn openInEditor(ws: *Workspace) void {
    const st = &ws.panel.search;
    if (st.text.len == 0) return;
    ws.openResultsInEditor(st.text, st.ranges);
}

// ---- Rows ---------------------------------------------------------------------------

fn rowCount(t: *const search_tree.Tree) usize {
    var n: usize = 0;
    for (t.files) |f| n += 1 + if (f.collapsed) 0 else f.matches.len;
    return n;
}

fn flatIndex(t: *const search_tree.Tree, sel: Sel) usize {
    var n: usize = 0;
    for (t.files, 0..) |f, i| {
        if (i == sel.file) return n + if (sel.match) |m| 1 + m else 0;
        n += 1 + if (f.collapsed) 0 else f.matches.len;
    }
    return n;
}

fn selAt(t: *const search_tree.Tree, flat: usize) ?Sel {
    var n: usize = 0;
    for (t.files, 0..) |f, i| {
        if (flat == n) return .{ .file = @intCast(i) };
        n += 1;
        if (f.collapsed) continue;
        if (flat < n + f.matches.len) return .{ .file = @intCast(i), .match = @intCast(flat - n) };
        n += f.matches.len;
    }
    return null;
}

fn sameSel(a: ?Sel, b: Sel) bool {
    const s = a orelse return false;
    return s.file == b.file and s.match == b.match;
}

fn clampScroll(st: *State, viewport: f32) void {
    const t = st.tree orelse {
        st.scroll_y = 0;
        return;
    };
    const content = @as(f32, @floatFromInt(rowCount(&t))) * row_h;
    st.scroll_y = std.math.clamp(st.scroll_y, 0, @max(0, content - viewport));
}

fn viewportH(st: *const State) f32 {
    return @max(row_h, st.rect.h - summary_h);
}

fn ensureVisible(st: *State) void {
    const t = st.tree orelse return;
    const sel = st.sel orelse return;
    const top = @as(f32, @floatFromInt(flatIndex(&t, sel))) * row_h;
    const vh = viewportH(st);
    if (top < st.scroll_y) st.scroll_y = top else if (top + row_h > st.scroll_y + vh) st.scroll_y = top + row_h - vh;
    clampScroll(st, vh);
}

// ---- Painting -----------------------------------------------------------------------

fn summaryText(st: *const State, buf: []u8) []const u8 {
    if (st.searching) return "Searching…";
    if (st.regex_error) return "Invalid regular expression";
    const t = st.tree orelse return "";
    if (t.hits == 0 and st.emptied_by == .replace) return "All results replaced";
    if (t.hits == 0 and st.emptied_by == .dismiss) return "All results dismissed";
    if (t.hits == 0) return std.fmt.bufPrint(buf, "No results found for '{s}' ({d} files searched)", .{ st.pattern, st.files_searched }) catch "No results found";
    return std.fmt.bufPrint(buf, "{d} result{s} in {d} file{s} \u{2014} {d} ms{s}", .{
        t.hits,
        if (t.hits == 1) "" else "s",
        t.files.len,
        if (t.files.len == 1) "" else "s",
        st.elapsed_ms,
        if (st.truncated) " (truncated, refine the search)" else "",
    }) catch "";
}

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const st = &ws.panel.search;
    const theme = ctx.theme;
    const scene = ctx.scene;
    st.rect = body;
    st.rows.clearRetainingCapacity();
    scene.fill(body, theme.background);

    // The summary line.
    const summary: Rect = .{ .x = body.x, .y = body.y, .w = body.w, .h = summary_h };
    var sbuf: [320]u8 = undefined;
    const text = summaryText(st, &sbuf);
    if (text.len > 0) {
        _ = ctx.labelFit(.{ .x = body.x + 14, .y = summary.y, .w = @max(0, body.w - 28), .h = summary_h }, text, ctx.fonts.ui_small, theme.list_foreground_dim);
    }
    const t = st.tree orelse {
        if (!st.searching) {
            const shortcut = if (builtin.os.tag.isDarwin()) "Cmd+Shift+F" else "Ctrl+Shift+F";
            var hint: [160]u8 = undefined;
            const msg = std.fmt.bufPrint(&hint, "Find in Files ({s}) searches the project; its results appear here.", .{shortcut}) catch "";
            _ = ctx.labelFit(.{ .x = body.x + 14, .y = summary.y, .w = @max(0, body.w - 28), .h = summary_h }, msg, ctx.fonts.ui, theme.list_foreground_dim);
        }
        return;
    };

    const list: Rect = .{ .x = body.x, .y = body.y + summary_h, .w = body.w, .h = @max(0, body.h - summary_h) };
    clampScroll(st, list.h);
    scene.pushClip(list) catch return;
    defer scene.popClip();

    // What a match would become, read once per paint.
    var replace: ?[]u8 = null;
    defer if (replace) |r| ws.gpa.free(r);
    if (ws.find) |f| if (f.replace_buffer.len() > 0) {
        replace = f.replace_input.contents(ws.gpa) catch null;
    };
    const preserve = if (ws.find) |f| f.toggles.get(.preserve_case) else false;
    const root: ?[]const u8 = if (ws.project) |p| p.root else null;
    const focused = ws.focus == .panel;

    var y = list.y - st.scroll_y;
    for (t.files, 0..) |file, fi| {
        if (y + row_h > list.y and y < list.bottom()) {
            const row: Rect = .{ .x = list.x, .y = y, .w = list.w, .h = row_h };
            paintFileRow(ws, ctx, row, file, .{ .file = @intCast(fi) }, root, replace != null, focused);
        }
        y += row_h;
        if (file.collapsed) continue;
        // Skip the rows above the view without painting them.
        if (y + @as(f32, @floatFromInt(file.matches.len)) * row_h <= list.y) {
            y += @as(f32, @floatFromInt(file.matches.len)) * row_h;
            continue;
        }
        for (file.matches, 0..) |m, mi| {
            if (y >= list.bottom()) break;
            if (y + row_h > list.y) {
                const row: Rect = .{ .x = list.x, .y = y, .w = list.w, .h = row_h };
                paintMatchRow(ws, ctx, row, m, .{ .file = @intCast(fi), .match = @intCast(mi) }, replace, preserve, focused);
            }
            y += row_h;
        }
    }
    paintScrollbar(ctx, list, st.scroll_y, @as(f32, @floatFromInt(rowCount(&t))) * row_h);
}

fn paintScrollbar(ctx: *const ui.Ctx, list: Rect, offset: f32, content: f32) void {
    if (content <= list.h) return;
    const track = list.h;
    const thumb_h = @max(24, track * list.h / content);
    const thumb_y = list.y + (track - thumb_h) * (offset / (content - list.h));
    ctx.scene.quad(.{ .x = list.right() - 8, .y = thumb_y, .w = 6, .h = thumb_h }, .{ .color = ctx.theme.list_foreground_dim.withAlpha(0.35), .radius = 3 });
}

fn rowBackground(ctx: *const ui.Ctx, ws: *Workspace, row: Rect, sel: Sel, focused: bool) *RowHit {
    const st = &ws.panel.search;
    const theme = ctx.theme;
    if (sameSel(st.sel, sel)) {
        ctx.scene.fill(row, if (focused) theme.list_selected else theme.sidebar_selected);
    } else if (ctx.hovered(row)) {
        ctx.scene.fill(row, theme.sidebar_hover);
    }
    // A full list can't happen: rows are only the painted ones, and the
    // capacity of the last paint is reused.
    st.rows.append(ws.gpa, .{ .rect = row, .sel = sel }) catch return &scratch_hit;
    return &st.rows.items[st.rows.items.len - 1];
}

var scratch_hit: RowHit = .{ .rect = .{}, .sel = .{ .file = 0 } };

fn paintFileRow(ws: *Workspace, ctx: *const ui.Ctx, row: Rect, file: search_tree.File, sel: Sel, root: ?[]const u8, can_replace: bool, focused: bool) void {
    const theme = ctx.theme;
    const hit = rowBackground(ctx, ws, row, sel, focused);
    const selected = sameSel(ws.panel.search.sel, sel);
    const fg = if (selected and focused) Color.hex(0xffffff) else theme.sidebar_foreground;
    const dim = if (selected and focused) Color.hex(0xffffff).withAlpha(0.7) else theme.sidebar_foreground_dim;
    ctx.chevron(row.x + 16, row.y + row.h / 2, !file.collapsed, dim);
    ctx.fileIcon(row.x + 26, row.y + row.h / 2, dim);
    const d = search_tree.displayPath(file.path, root);
    var x = row.x + 46;
    // The count badge and the hover buttons sit on the right.
    var right = row.right() - 18;
    var nbuf: [16]u8 = undefined;
    const count = std.fmt.bufPrint(&nbuf, "{d}", .{file.hits}) catch "";
    const badge_w = ctx.text.measure(count, ctx.fonts.ui_small) + 12;
    const badge: Rect = .{ .x = right - badge_w, .y = row.y + 3, .w = badge_w, .h = row_h - 6 };
    ctx.scene.quad(badge, .{ .color = theme.button_background, .radius = (row_h - 6) / 2 });
    ctx.labelCentered(badge, count, ctx.fonts.ui_small, theme.list_foreground);
    right = badge.x - 6;
    if (ctx.hovered(row)) {
        const x_rect: Rect = .{ .x = right - 20, .y = row.y + 2, .w = 20, .h = row_h - 4 };
        if (ctx.hovered(x_rect)) ctx.scene.quad(x_rect, .{ .color = theme.tab_hover, .radius = 4 });
        ctx.closeIcon(x_rect.x + 10, x_rect.y + x_rect.h / 2, 7, dim);
        hit.dismiss = x_rect;
        right = x_rect.x - 4;
        if (can_replace) {
            const w = ctx.text.measure("Replace", ctx.fonts.ui_small) + 14;
            const r: Rect = .{ .x = right - w, .y = row.y + 2, .w = w, .h = row_h - 4 };
            if (ctx.hovered(r)) ctx.scene.quad(r, .{ .color = theme.tab_hover, .radius = 4 });
            ctx.labelCentered(r, "Replace", ctx.fonts.ui_small, dim);
            hit.replace = r;
            right = r.x - 4;
        }
    }
    const avail = @max(0, right - x - 6);
    ctx.scene.pushClip(.{ .x = x, .y = row.y, .w = avail, .h = row.h }) catch return;
    defer ctx.scene.popClip();
    x += ctx.label(.{ .x = x, .y = row.y, .w = avail, .h = row.h }, d.name, ctx.fonts.ui, fg) + 8;
    if (d.folder.len > 0) _ = ctx.label(.{ .x = x, .y = row.y, .w = @max(0, row.x + row.w - x), .h = row.h }, d.folder, ctx.fonts.ui_small, dim);
}

fn paintMatchRow(ws: *Workspace, ctx: *const ui.Ctx, row: Rect, m: search_tree.Match, sel: Sel, replace: ?[]const u8, preserve: bool, focused: bool) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    const hit = rowBackground(ctx, ws, row, sel, focused);
    const selected = sameSel(ws.panel.search.sel, sel);
    const fg = if (selected and focused) Color.hex(0xffffff) else theme.sidebar_foreground;
    const dim = if (selected and focused) Color.hex(0xffffff).withAlpha(0.7) else theme.sidebar_foreground_dim;
    var nbuf: [16]u8 = undefined;
    const num = std.fmt.bufPrint(&nbuf, "{d}", .{m.line}) catch "";
    _ = ctx.labelRight(.{ .x = row.x + 30, .y = row.y, .w = 38, .h = row.h }, num, ctx.fonts.ui_small, dim);
    const px = row.x + 78;
    {
        scene.pushClip(.{ .x = px, .y = row.y, .w = @max(0, row.w - 78 - 36), .h = row.h }) catch return;
        defer scene.popClip();
        paintPreview(ws, ctx, row, px, m, replace, preserve, fg);
    }
    if (ctx.hovered(row)) {
        const x_rect: Rect = .{ .x = row.right() - 28, .y = row.y + 2, .w = 20, .h = row_h - 4 };
        if (ctx.hovered(x_rect)) scene.quad(x_rect, .{ .color = theme.tab_hover, .radius = 4 });
        ctx.closeIcon(x_rect.x + 10, x_rect.y + x_rect.h / 2, 7, dim);
        hit.dismiss = x_rect;
    }
}

/// The line's text from `px`, its matches highlighted, or (with a
/// replacement typed) struck through and followed by what replaces them.
fn paintPreview(ws: *Workspace, ctx: *const ui.Ctx, row: Rect, px: f32, m: search_tree.Match, replace: ?[]const u8, preserve: bool, fg: Color) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    const font = ctx.fonts.ui;
    var x = px;
    var pos: usize = 0;
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const st = &ws.panel.search;
    for (m.ranges) |r| {
        const a: usize = r[0];
        const b: usize = r[1];
        if (a < pos or b > m.preview.len) continue;
        x += ctx.label(.{ .x = x, .y = row.y, .w = 4000, .h = row.h }, m.preview[pos..a], font, fg);
        const matched = m.preview[a..b];
        const w = ctx.text.measure(matched, font);
        if (replace) |tmpl| {
            // The match struck through on red, then what replaces it on green.
            scene.quad(.{ .x = x, .y = row.y + 3, .w = w, .h = row_h - 6 }, .{ .color = Color.hexa(0xff5555, 0.28), .radius = 2 });
            _ = ctx.label(.{ .x = x, .y = row.y, .w = 4000, .h = row.h }, matched, font, fg.withAlpha(0.8));
            scene.fill(.{ .x = x, .y = row.y + @round(row_h / 2), .w = w, .h = 1 }, fg.withAlpha(0.8));
            x += w;
            const text = find_panel.expandReplacement(arena.allocator(), if (st.re) |*re| re else null, m.preview, a, b, tmpl, preserve) catch tmpl;
            const rw = ctx.text.measure(text, font);
            scene.quad(.{ .x = x, .y = row.y + 3, .w = rw, .h = row_h - 6 }, .{ .color = Color.hexa(0x57d27a, 0.3), .radius = 2 });
            x += ctx.label(.{ .x = x, .y = row.y, .w = 4000, .h = row.h }, text, font, fg);
        } else {
            scene.quad(.{ .x = x, .y = row.y + 3, .w = w, .h = row_h - 6 }, .{ .color = theme.find_highlight, .border_color = theme.find_highlight_border, .border_width = 1, .radius = 2 });
            x += ctx.label(.{ .x = x, .y = row.y, .w = 4000, .h = row.h }, matched, font, fg);
        }
        pos = b;
    }
    if (pos < m.preview.len) _ = ctx.label(.{ .x = x, .y = row.y, .w = 4000, .h = row.h }, m.preview[pos..], font, fg);
}

// ---- Opening ------------------------------------------------------------------------

/// Opens the file of `sel` at its match (a preview tab unless `keep`).
fn open(ws: *Workspace, sel: Sel, hit: u32, keep: bool) void {
    const st = &ws.panel.search;
    const t = st.tree orelse return;
    const file = t.files[sel.file];
    const ed = ws.openFile(file.path, !keep) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ file.path, err });
        return;
    };
    if (sel.match) |mi| {
        const m = file.matches[mi];
        const buf = ed.buffer;
        const row = @min(m.line -| 1, buf.lineCount() - 1);
        const ls = buf.lineStart(row);
        const le = buf.lineEnd(row);
        const first: [2]u32 = if (hit < m.ranges.len) m.ranges[hit] else .{ 0, 0 };
        const a = @min(ls + m.preview_col + first[0], le);
        const b = @min(ls + m.preview_col + first[1], le);
        ed.setSingleSelection(.range(a, b));
        ed.afterCommand(true);
    }
    ws.focus = .editor;
}

// ---- Input --------------------------------------------------------------------------

fn dismiss(ws: *Workspace, sel: Sel) void {
    const st = &ws.panel.search;
    var t = st.tree orelse return;
    if (sel.match) |mi| t.dismissMatch(sel.file, mi) else t.dismissFile(sel.file);
    st.tree = t;
    if (t.files.len == 0) {
        st.sel = null;
        st.emptied_by = .dismiss;
        return;
    }
    const fi = @min(sel.file, @as(u32, @intCast(t.files.len - 1)));
    st.sel = .{ .file = fi };
    ensureVisible(st);
}

/// A click on the panel body. A double click keeps the file's tab.
pub fn click(ws: *Workspace, m: platform.MouseEvent) void {
    const st = &ws.panel.search;
    var t = st.tree orelse return;
    for (st.rows.items) |h| {
        if (!h.rect.contains(m.pos.x, m.pos.y)) continue;
        st.sel = h.sel;
        st.hit = 0;
        if (m.button != .left) return;
        if (h.dismiss.contains(m.pos.x, m.pos.y)) return dismiss(ws, h.sel);
        if (h.replace.contains(m.pos.x, m.pos.y)) return replaceFile(ws, h.sel.file);
        if (h.sel.match == null) {
            t.files[h.sel.file].collapsed = !t.files[h.sel.file].collapsed;
            return;
        }
        open(ws, h.sel, 0, m.click_count >= 2);
        return;
    }
}

pub fn scroll(ws: *Workspace, s: platform.ScrollEvent) void {
    const st = &ws.panel.search;
    st.scroll_y += if (s.unit == .lines) s.delta.y * row_h * 3 else s.delta.y;
    clampScroll(st, viewportH(st));
}

/// Keys while the panel has focus. Returns true if consumed.
pub fn key(ws: *Workspace, k: platform.KeyEvent) bool {
    const st = &ws.panel.search;
    var t = st.tree orelse return false;
    if (t.files.len == 0 or k.key != .named) return false;
    const n = rowCount(&t);
    const cur = st.sel orelse Sel{ .file = 0 };
    const flat = flatIndex(&t, cur);
    st.hit = 0;
    switch (k.key.named) {
        .down => if (flat + 1 < n) {
            st.sel = selAt(&t, flat + 1);
        },
        .up => if (flat > 0) {
            st.sel = selAt(&t, flat - 1);
        },
        .page_down => st.sel = selAt(&t, @min(n - 1, flat + @as(usize, @intFromFloat(viewportH(st) / row_h)))),
        .page_up => st.sel = selAt(&t, flat -| @as(usize, @intFromFloat(viewportH(st) / row_h))),
        .home => st.sel = selAt(&t, 0),
        .end => st.sel = selAt(&t, n - 1),
        .left => {
            if (cur.match != null) {
                st.sel = .{ .file = cur.file };
            } else t.files[cur.file].collapsed = true;
        },
        .right => if (cur.match == null) {
            if (t.files[cur.file].collapsed) t.files[cur.file].collapsed = false else if (t.files[cur.file].matches.len > 0) {
                st.sel = .{ .file = cur.file, .match = 0 };
            }
        },
        .enter => {
            if (cur.match == null) {
                t.files[cur.file].collapsed = !t.files[cur.file].collapsed;
            } else open(ws, cur, 0, true);
        },
        .delete, .backspace => dismiss(ws, cur),
        else => return false,
    }
    ensureVisible(st);
    return true;
}

/// The position of hit `hit` of line `sel` among all hits, in tree order.
fn ordinalOf(t: *const search_tree.Tree, sel: Sel, hit: u32) usize {
    var n: usize = 0;
    for (t.files, 0..) |f, fi| {
        for (f.matches, 0..) |m, mi| {
            if (fi == sel.file and sel.match != null and mi == sel.match.?) return n + hit;
            n += m.ranges.len;
        }
    }
    return n;
}

fn fromOrdinal(t: *const search_tree.Tree, ordinal: usize) ?struct { sel: Sel, hit: u32 } {
    var n: usize = 0;
    for (t.files, 0..) |f, fi| {
        for (f.matches, 0..) |m, mi| {
            if (ordinal < n + m.ranges.len) return .{ .sel = .{ .file = @intCast(fi), .match = @intCast(mi) }, .hit = @intCast(ordinal - n) };
            n += m.ranges.len;
        }
    }
    return null;
}

/// F4 / Shift+F4: the next or previous hit, across files and wrapping
/// around, expanding a collapsed file. Returns false when there are no
/// results to step through.
pub fn step(ws: *Workspace, forward: bool) bool {
    const st = &ws.panel.search;
    const t = st.tree orelse return false;
    if (t.hits == 0) return false;
    // From a file row (or nothing selected) the first (last) hit is next.
    const target: usize = if (st.sel) |s| blk: {
        if (s.match == null) break :blk if (forward) ordinalOfFile(&t, s.file) else (ordinalOfFile(&t, s.file) + t.hits - 1) % t.hits;
        const cur = ordinalOf(&t, s, st.hit);
        break :blk if (forward) (cur + 1) % t.hits else (cur + t.hits - 1) % t.hits;
    } else if (forward) 0 else t.hits - 1;
    const next = fromOrdinal(&t, target) orelse return false;
    t.files[next.sel.file].collapsed = false;
    st.sel = next.sel;
    st.hit = next.hit;
    ensureVisible(st);
    open(ws, next.sel, next.hit, false);
    var buf: [64]u8 = undefined;
    ws.setStatus("{s}", .{std.fmt.bufPrint(&buf, "Result {d} of {d}", .{ target + 1, t.hits }) catch ""});
    return true;
}

/// The ordinal of the first hit of file `file`.
fn ordinalOfFile(t: *const search_tree.Tree, file: u32) usize {
    var n: usize = 0;
    for (t.files[0..file]) |f| n += f.hits;
    return n;
}

pub fn runAction(ws: *Workspace, a: Action) void {
    const st = &ws.panel.search;
    switch (a) {
        .clear => {
            freeResults(ws.gpa, st);
            st.searching = false;
        },
        .collapse_all => if (st.tree) |*t| {
            // Collapse everything; a second press expands again.
            var any_open = false;
            for (t.files) |f| if (!f.collapsed) {
                any_open = true;
            };
            for (t.files) |*f| f.collapsed = any_open;
            st.scroll_y = 0;
            if (st.sel) |s| st.sel = .{ .file = s.file };
        },
        .open_in_editor => openInEditor(ws),
        .replace_all => replaceAll(ws),
    }
}

// ---- Replace ------------------------------------------------------------------------

fn replaceText(ws: *Workspace) ?[]u8 {
    const f = ws.find orelse return null;
    if (f.replace_buffer.len() == 0) return null;
    return f.replace_input.contents(ws.gpa) catch null;
}

/// Replace All: one confirmation when it spans several files.
fn replaceAll(ws: *Workspace) void {
    const st = &ws.panel.search;
    const t = st.tree orelse return;
    if (t.hits == 0) return;
    const tmpl = replaceText(ws) orelse {
        ws.setStatus("Type a replacement in the find panel first (Cmd+Shift+H)", .{});
        return;
    };
    ws.gpa.free(tmpl);
    if (t.files.len <= 1) return replaceFiles(ws, 0, t.files.len);
    const label = std.fmt.allocPrint(ws.gpa, "Replace {d} occurrences in {d} files?", .{ t.hits, t.files.len }) catch return;
    ws.openList(.list, label, replace_tag, &.{ "Replace All", "Cancel" });
}

/// The Replace All confirmation (a quick panel list); false if the tag isn't ours.
pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item) bool {
    if (tag != replace_tag) return false;
    const choice = (item orelse return true).payload.value;
    if (choice == 0) if (ws.panel.search.tree) |t| replaceFiles(ws, 0, t.files.len);
    return true;
}

fn replaceFile(ws: *Workspace, file: u32) void {
    replaceFiles(ws, file, file + 1);
}

/// Replaces the matches of files `from..to`, in the open (unsaved) buffers.
/// A match whose text is no longer what the search found is skipped.
fn replaceFiles(ws: *Workspace, from: usize, to: usize) void {
    const st = &ws.panel.search;
    var t = st.tree orelse return;
    const tmpl = replaceText(ws) orelse return;
    defer ws.gpa.free(tmpl);
    const preserve = if (ws.find) |f| f.toggles.get(.preserve_case) else false;
    var replaced: usize = 0;
    var skipped: usize = 0;
    var done_files: usize = 0;
    // Back to front so dismissing a file doesn't shift the ones still to do.
    var fi = to;
    while (fi > from) {
        fi -= 1;
        const file = t.files[fi];
        const ed = ws.openFile(file.path, false) catch {
            skipped += file.hits;
            continue;
        };
        var arena = std.heap.ArenaAllocator.init(ws.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var reps: std.ArrayList(Editor.Replacement) = .empty;
        const buf = ed.buffer;
        for (file.matches) |m| {
            if (m.line == 0 or m.line > buf.lineCount()) {
                skipped += m.ranges.len;
                continue;
            }
            const row = m.line - 1;
            const ls = buf.lineStart(row);
            const line = buf.textRange(alloc, ls, buf.lineEnd(row)) catch continue;
            for (m.ranges) |r| {
                const a: usize = m.preview_col + r[0];
                const b: usize = m.preview_col + r[1];
                if (b > line.len or !std.mem.eql(u8, line[a..b], m.preview[r[0]..r[1]])) {
                    skipped += 1;
                    continue;
                }
                const text = find_panel.expandReplacement(alloc, if (st.re) |*re| re else null, line, a, b, tmpl, preserve) catch tmpl;
                reps.append(alloc, .{ .start = ls + a, .end = ls + b, .text = text, .sel_anchor = text.len, .sel_head = text.len }) catch continue;
                replaced += 1;
            }
        }
        if (reps.items.len > 0) {
            ed.applyReplacements(reps.items, .other);
            ed.setSingleSelection(ed.primarySelection());
            done_files += 1;
        }
        t.dismissFile(fi);
    }
    st.tree = t;
    if (t.files.len == 0) {
        st.sel = null;
        st.emptied_by = .replace;
    } else if (st.sel) |s| {
        if (s.file >= t.files.len) st.sel = .{ .file = @intCast(t.files.len - 1) };
    }
    ws.focus = .editor;
    if (skipped > 0) {
        ws.setStatus("Replaced {d} occurrences in {d} files (unsaved), skipped {d} that changed", .{ replaced, done_files, skipped });
    } else ws.setStatus("Replaced {d} occurrences in {d} files (unsaved)", .{ replaced, done_files });
}
