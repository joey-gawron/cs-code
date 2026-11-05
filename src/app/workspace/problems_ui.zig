//! The panel's Error List: every open file's language-server diagnostics in
//! a table (severity, description, file, line, column), like Visual
//! Studio's. The buttons above it show how many errors, warnings and
//! messages there are and filter by them; double-click or Enter goes to the
//! diagnostic, Up and Down move through the rows.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const panel_ui = @import("panel_ui.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const platform = @import("../../framework/platform/platform.zig");
const ui = @import("../../framework/ui/ui.zig");

const Workspace = workspace_mod.Workspace;
const Buffer = buffer_mod.Buffer;
const Rect = ui.Rect;
const Color = ui.Color;

const toolbar_h: f32 = 30;
const header_h: f32 = 24;
const row_h: f32 = 24;
/// More rows than this are counted but not listed.
const max_rows = 5000;

const Category = enum { errors, warnings, messages };

fn category(s: buffer_mod.Severity) Category {
    return switch (s) {
        .err => .errors,
        .warning => .warnings,
        .info, .hint => .messages,
    };
}

pub const State = struct {
    show: [3]bool = .{ true, true, true },
    selected: usize = 0,
    scroll: f32 = 0,
    /// The toggle buttons and the table, from the last paint.
    toggles: [3]Rect = .{ .{}, .{}, .{} },
    list: Rect = .{},
};

const Item = struct { buffer: *Buffer, index: usize };

/// The shown rows, errors first, then by file and position; `counts` has
/// every diagnostic's category count, shown or not.
fn collect(ws: *Workspace, items: *std.ArrayList(Item), counts: *[3]usize) void {
    const st = &ws.lsp_state.problems;
    for (ws.buffers.items) |b| {
        if (b.path == null) continue;
        for (b.diagnostics.items, 0..) |d, i| {
            const cat = @intFromEnum(category(d.severity));
            counts[cat] += 1;
            if (!st.show[cat] or items.items.len >= max_rows) continue;
            items.append(ws.gpa, .{ .buffer = b, .index = i }) catch return;
        }
    }
    std.mem.sort(Item, items.items, {}, struct {
        fn lt(_: void, a: Item, b: Item) bool {
            const da = a.buffer.diagnostics.items[a.index];
            const db = b.buffer.diagnostics.items[b.index];
            if (da.severity != db.severity) return @intFromEnum(da.severity) < @intFromEnum(db.severity);
            if (a.buffer != b.buffer) return std.mem.lessThan(u8, a.buffer.path.?, b.buffer.path.?);
            return da.start < db.start;
        }
    }.lt);
}

fn severityColor(s: buffer_mod.Severity) Color {
    return switch (s) {
        .err => Color.hex(0xf14c4c),
        .warning => Color.hex(0xcca700),
        .info, .hint => Color.hex(0x75beff),
    };
}

fn severityGlyph(s: buffer_mod.Severity) []const u8 {
    return switch (s) {
        .err => "✖",
        .warning => "⚠",
        .info, .hint => "ⓘ",
    };
}

fn firstLine(text: []const u8) []const u8 {
    return text[0 .. std.mem.indexOfScalar(u8, text, '\n') orelse text.len];
}

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const st = &ws.lsp_state.problems;
    const theme = ctx.theme;
    const scene = ctx.scene;
    var items: std.ArrayList(Item) = .empty;
    defer items.deinit(ws.gpa);
    var counts: [3]usize = .{ 0, 0, 0 };
    collect(ws, &items, &counts);

    scene.fill(body, theme.background);
    scene.pushClip(body) catch return;
    defer scene.popClip();

    // Toolbar: the three filter buttons.
    const toolbar: Rect = .{ .x = body.x, .y = body.y, .w = body.w, .h = toolbar_h };
    scene.fill(toolbar, theme.tabbar_background);
    const labels = [3]struct { glyph: []const u8, one: []const u8, many: []const u8, color: Color }{
        .{ .glyph = "✖", .one = "Error", .many = "Errors", .color = severityColor(.err) },
        .{ .glyph = "⚠", .one = "Warning", .many = "Warnings", .color = severityColor(.warning) },
        .{ .glyph = "ⓘ", .one = "Message", .many = "Messages", .color = severityColor(.info) },
    };
    var x = body.x + 8;
    var buf: [48]u8 = undefined;
    for (labels, 0..) |l, i| {
        const text = std.fmt.bufPrint(&buf, "{d} {s}", .{ counts[i], if (counts[i] == 1) l.one else l.many }) catch "";
        const tw = ctx.text.measure(text, ws.fonts.ui_small);
        const w = tw + 38;
        const r: Rect = .{ .x = x, .y = body.y + 3, .w = w, .h = toolbar_h - 6 };
        st.toggles[i] = r;
        if (st.show[i]) scene.quad(r, .{ .color = theme.tab_hover, .radius = 3 }) else if (ctx.hovered(r)) scene.quad(r, .{ .color = theme.tab_hover.withAlpha(0.5), .radius = 3 });
        _ = ctx.label(.{ .x = r.x + 8, .y = r.y, .w = 20, .h = r.h }, l.glyph, ws.fonts.ui_small, if (st.show[i]) l.color else l.color.withAlpha(0.45));
        _ = ctx.label(.{ .x = r.x + 28, .y = r.y, .w = tw, .h = r.h }, text, ws.fonts.ui_small, if (st.show[i]) theme.tab_active_foreground else theme.tab_inactive_foreground);
        x = r.right() + 4;
    }
    scene.fill(.{ .x = body.x, .y = toolbar.bottom() - 1, .w = body.w, .h = 1 }, theme.tab_separator);

    // Columns: severity, description (the rest), file, line, column.
    const col_icon: f32 = 34;
    const col_col: f32 = 56;
    const col_line: f32 = 56;
    const col_file: f32 = @min(220, @max(120, body.w * 0.22));
    const x_col = body.right() - col_col;
    const x_line = x_col - col_line;
    const x_file = x_line - col_file;
    const x_desc = body.x + col_icon;
    const hdr: Rect = .{ .x = body.x, .y = toolbar.bottom(), .w = body.w, .h = header_h };
    scene.fill(hdr, theme.tabbar_background);
    for ([_]struct { text: []const u8, x: f32 }{
        .{ .text = "Description", .x = x_desc },
        .{ .text = "File", .x = x_file },
        .{ .text = "Line", .x = x_line },
        .{ .text = "Col", .x = x_col },
    }) |c| _ = ctx.label(.{ .x = c.x + 6, .y = hdr.y, .w = 200, .h = hdr.h }, c.text, ws.fonts.ui_small, theme.tab_inactive_foreground);
    scene.fill(.{ .x = body.x, .y = hdr.bottom() - 1, .w = body.w, .h = 1 }, theme.tab_separator);

    const list: Rect = .{ .x = body.x, .y = hdr.bottom(), .w = body.w, .h = @max(0, body.bottom() - hdr.bottom()) };
    st.list = list;
    if (items.items.len == 0) {
        const msg = if (counts[0] + counts[1] + counts[2] == 0) "No issues found in the open files." else "No issues match the filters.";
        _ = ctx.labelFit(.{ .x = list.x + 14, .y = list.y + 4, .w = @max(0, list.w - 28), .h = row_h }, msg, ws.fonts.ui, theme.list_foreground_dim);
        return;
    }
    st.selected = @min(st.selected, items.items.len - 1);
    const total_h = @as(f32, @floatFromInt(items.items.len)) * row_h;
    st.scroll = std.math.clamp(st.scroll, 0, @max(0, total_h - list.h));

    scene.pushClip(list) catch return;
    defer scene.popClip();
    const first: usize = @intFromFloat(@floor(st.scroll / row_h));
    var y = list.y + @as(f32, @floatFromInt(first)) * row_h - st.scroll;
    const focused = ws.focus == .panel;
    var i = first;
    while (i < items.items.len and y < list.bottom()) : (i += 1) {
        const it = items.items[i];
        const d = it.buffer.diagnostics.items[it.index];
        const row: Rect = .{ .x = list.x, .y = y, .w = list.w, .h = row_h };
        if (i == st.selected) {
            scene.fill(row, if (focused) theme.list_selected else theme.sidebar_selected);
        } else if (ctx.hovered(row)) {
            scene.fill(row, theme.sidebar_hover);
        }
        _ = ctx.label(.{ .x = row.x + 12, .y = y, .w = 20, .h = row_h }, severityGlyph(d.severity), ws.fonts.ui_small, severityColor(d.severity));
        const fg = if (i == st.selected and focused) Color.white else theme.list_foreground;
        _ = ctx.labelFit(.{ .x = x_desc + 6, .y = y, .w = @max(0, x_file - x_desc - 12), .h = row_h }, firstLine(d.message), ws.fonts.ui, fg);
        const p = it.buffer.offsetToPoint(d.start);
        _ = ctx.labelFit(.{ .x = x_file + 6, .y = y, .w = col_file - 10, .h = row_h }, std.fs.path.basename(it.buffer.path.?), ws.fonts.ui, fg);
        var num: [16]u8 = undefined;
        _ = ctx.label(.{ .x = x_line + 6, .y = y, .w = col_line, .h = row_h }, std.fmt.bufPrint(&num, "{d}", .{p.row + 1}) catch "", ws.fonts.ui, fg);
        _ = ctx.label(.{ .x = x_col + 6, .y = y, .w = col_col, .h = row_h }, std.fmt.bufPrint(&num, "{d}", .{p.column + 1}) catch "", ws.fonts.ui, fg);
        y += row_h;
    }
}

fn goTo(ws: *Workspace, index: usize) void {
    var items: std.ArrayList(Item) = .empty;
    defer items.deinit(ws.gpa);
    var counts: [3]usize = .{ 0, 0, 0 };
    collect(ws, &items, &counts);
    if (index >= items.items.len) return;
    const it = items.items[index];
    const path = it.buffer.path orelse return;
    const offset = it.buffer.diagnostics.items[it.index].start;
    const ed = ws.openFile(path, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ path, err });
        return;
    };
    ed.pushSelectionHistory();
    ed.setSingleSelection(.caret(offset));
    ed.afterCommand(true);
    ws.focus = .editor;
    ed.focused = true;
}

/// A click in the Error List's body. Returns true if it was in the view.
pub fn onMouseDown(ws: *Workspace, m: platform.MouseEvent) bool {
    const st = &ws.lsp_state.problems;
    if (m.button != .left) return false;
    ws.dirty = true;
    for (st.toggles, 0..) |r, i| if (r.contains(m.pos.x, m.pos.y)) {
        st.show[i] = !st.show[i];
        return true;
    };
    if (!st.list.contains(m.pos.x, m.pos.y)) return false;
    if (ws.activeEditor()) |ed| ed.focused = false;
    ws.focus = .panel;
    const row: usize = @intFromFloat(@max(0, @floor((m.pos.y - st.list.y + st.scroll) / row_h)));
    st.selected = row;
    if (m.click_count >= 2) goTo(ws, row);
    return true;
}

pub fn onScroll(ws: *Workspace, s: platform.ScrollEvent) void {
    const st = &ws.lsp_state.problems;
    st.scroll = @max(0, st.scroll - s.delta.y * (if (s.unit == .lines) row_h * 3 else 1));
    ws.dirty = true;
}

/// Keys while the view has the keyboard. Returns true if consumed.
pub fn viewKey(ws: *Workspace, k: platform.KeyEvent) bool {
    const st = &ws.lsp_state.problems;
    if (k.key != .named) return false;
    var items: std.ArrayList(Item) = .empty;
    defer items.deinit(ws.gpa);
    var counts: [3]usize = .{ 0, 0, 0 };
    collect(ws, &items, &counts);
    const n = items.items.len;
    const page: usize = @max(1, @as(usize, @intFromFloat(st.list.h / row_h)) -| 1);
    switch (k.key.named) {
        .escape => {
            panel_ui.hide(ws);
            return true;
        },
        .enter => goTo(ws, st.selected),
        .down => st.selected = @min(n -| 1, st.selected + 1),
        .up => st.selected -|= 1,
        .page_down => st.selected = @min(n -| 1, st.selected + page),
        .page_up => st.selected -|= page,
        .home => st.selected = 0,
        .end => st.selected = n -| 1,
        else => return false,
    }
    // Keep the selection in view.
    const top = @as(f32, @floatFromInt(st.selected)) * row_h;
    if (top < st.scroll) st.scroll = top;
    if (top + row_h > st.scroll + st.list.h) st.scroll = top + row_h - st.list.h;
    ws.dirty = true;
    return true;
}
