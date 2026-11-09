//! The Org Agenda tool window, docked in the side bar under an Explorer /
//! Org Agenda tab strip as in cs-code: Day / Week / To do buttons, a
//! previous / Today / next row, a filter box, the entries grouped by day (or
//! by file) with a second line saying why each is there, and the selected
//! entry's details below. Edits go to the entry's file and the window comes
//! back refreshed, on the tab it left.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const org_ui = @import("org_ui.zig");
const org = @import("../org/org.zig");
const editor_mod = @import("../editor/editor.zig");
const keymap_mod = @import("../settings/keymap.zig");
const agenda_mod = @import("../../model/org/agenda.zig");
const date = @import("../../model/org/date.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const ui = @import("../../framework/ui/ui.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const scm_ui = @import("scm_ui.zig");
const Editor = editor_mod.Editor;
const Action = keymap_mod.Action;
const Agenda = agenda_mod.Agenda;
const Item = agenda_mod.Item;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;

pub const Tab = enum { explorer, agenda, scm };

/// The tab strip under the side bar's content.
pub const tab_h: f32 = 26;
const header_h: f32 = 30;
const group_h: f32 = 28;
const item_h: f32 = 40;
const details_max: f32 = 190;
const min_width: f32 = 300;

const accent = Color.hex(0x3794ff);
const todo_color = Color.hex(0xf48771);
const done_color = Color.hex(0x89d185);
const priority_color = Color.hex(0xe2c08d);
const overdue_color = Color.hex(0xf48771);
const upcoming_color = Color.hex(0xcca700);
const active_fill = Color.hex(0x094771);
const active_border = Color.hex(0x007acc);

const What = union(enum) {
    tab: Tab,
    view: agenda_mod.View,
    previous,
    next,
    today,
    capture,
    notes,
    refresh,
    more,
    filter,
    row: usize,
    check: usize,
    open,
};

const Hit = struct { rect: Rect, what: What };

/// An action waiting for its prompt (a date, tags, a refile target): where
/// the window was and how the entry's file stood, to put things back.
const Pending = struct {
    editor: *Editor,
    previous: ?*Editor,
    was_open: bool,
    was_dirty: bool,
};

pub const State = struct {
    tab: Tab = .explorer,
    engine: ?*Agenda = null,
    /// Which open org buffers (and at which versions) the entries came from.
    signature: u64 = 0,
    selected: usize = 0,
    scroll: f32 = 0,
    focused: bool = false,
    filter_focused: bool = false,
    filter: std.ArrayList(u8) = .empty,
    hits: std.ArrayList(Hit) = .empty,
    list_rect: Rect = .{},
    content_h: f32 = 0,
    pending: ?Pending = null,

    pub fn deinit(self: *State, gpa: Allocator) void {
        if (self.engine) |a| {
            a.deinit();
            gpa.destroy(a);
        }
        self.filter.deinit(gpa);
        self.hits.deinit(gpa);
    }
};

fn ensure(ws: *Workspace) ?*Agenda {
    const st = &ws.agenda;
    if (st.engine) |a| return a;
    const a = ws.gpa.create(Agenda) catch return null;
    a.* = Agenda.init(ws.gpa, .week);
    st.engine = a;
    refresh(ws);
    return a;
}

/// Shows the window in `view` (View > Org Agenda, Org: Agenda, the to-do list).
pub fn show(ws: *Workspace, view: agenda_mod.View) void {
    const a = ensure(ws) orelse return;
    const st = &ws.agenda;
    a.setView(view);
    a.goToday();
    st.tab = .agenda;
    st.focused = true;
    st.scroll = 0;
    st.selected = 0;
    ws.setSidebarVisible(true);
    ws.sidebar_width = @max(ws.sidebar_width, min_width);
    refresh(ws);
}

/// Rebuilds the entries from the open buffers and the agenda files.
pub fn refresh(ws: *Workspace) void {
    const a = ws.agenda.engine orelse return;
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const sources = org_ui.collectSources(ws, arena.allocator()) catch return;
    a.build(sources) catch |err| {
        ws.setStatus("Agenda failed: {t}", .{err});
        return;
    };
    ws.agenda.signature = signature(ws);
    const n = itemCount(a);
    if (ws.agenda.selected >= n) ws.agenda.selected = if (n == 0) 0 else n - 1;
}

/// Open org buffers change what the agenda lists: typing in one rebuilds it.
fn signature(ws: *Workspace) u64 {
    var sig: u64 = ws.buffers.items.len;
    for (ws.buffers.items) |b| {
        if (!std.mem.eql(u8, b.language.name, "Org")) continue;
        sig +%= (@as(u64, @intFromPtr(b)) >> 4) *% 31 +% b.version;
    }
    return sig;
}

fn itemCount(a: *const Agenda) usize {
    var n: usize = 0;
    for (a.groups.items) |g| n += g.items.len;
    return n;
}

fn itemAt(a: *const Agenda, flat: usize) ?Item {
    var i = flat;
    for (a.groups.items) |g| {
        if (i < g.items.len) return g.items[i];
        i -= g.items.len;
    }
    return null;
}

/// The entry shown selected, if any.
pub fn selectedItem(ws: *Workspace) ?Item {
    const a = ws.agenda.engine orelse return null;
    return itemAt(a, ws.agenda.selected);
}

// ---- Painting -----------------------------------------------------------------

/// The bottom tab strip, on both tabs.
const tab_pad: f32 = 16;

const TabLabel = struct { tab: Tab, text: []const u8 };

fn tabLabels(ws: *Workspace) [3]TabLabel {
    return .{
        .{ .tab = .explorer, .text = if (ws.project != null) "Explorer" else "Open Files" },
        .{ .tab = .agenda, .text = "Org Agenda" },
        .{ .tab = .scm, .text = "Source Control" },
    };
}

pub fn paintTabs(ws: *Workspace, ctx: *const ui.Ctx) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    const st = &ws.agenda;
    const sb = ws.sidebar_rect;
    const strip: Rect = .{ .x = sb.x + 1, .y = sb.bottom() - tab_h, .w = sb.w - 1, .h = tab_h };
    scene.fill(strip, theme.tabbar_background);
    scene.fill(.{ .x = strip.x, .y = strip.y, .w = strip.w, .h = 1 }, theme.tab_separator);
    const labels = tabLabels(ws);
    var x = strip.x;
    for (labels) |l| {
        const w = ctx.text.measure(l.text, ws.fonts.ui_small) + tab_pad;
        const r: Rect = .{ .x = x, .y = strip.y, .w = w, .h = tab_h };
        const active = st.tab == l.tab;
        if (active) {
            scene.fill(.{ .x = r.x, .y = r.y + 1, .w = r.w, .h = r.h - 1 }, theme.sidebar_background);
            scene.fill(.{ .x = r.x, .y = r.y, .w = r.w, .h = 1.5 }, active_border);
        } else if (ctx.hovered(r)) scene.fill(r, theme.tab_hover);
        ctx.labelCentered(r, l.text, ws.fonts.ui_small, if (active) theme.tab_active_foreground else theme.tab_inactive_foreground);
        x += w;
    }
}

fn addHit(ws: *Workspace, rect: Rect, what: What) void {
    ws.agenda.hits.append(ws.gpa, .{ .rect = rect, .what = what }) catch {};
}

/// Draws a small icon button's hover plate and registers it.
fn iconButton(ws: *Workspace, ctx: *const ui.Ctx, r: Rect, what: What) void {
    if (ctx.hovered(r)) ctx.scene.quad(r, .{ .color = ctx.theme.sidebar_hover, .radius = 3 });
    addHit(ws, r, what);
}

fn textButton(ws: *Workspace, ctx: *const ui.Ctx, r: Rect, label: []const u8, active: bool, what: What) void {
    const theme = ctx.theme;
    if (active) {
        ctx.scene.quad(r, .{ .color = active_fill, .border_color = active_border, .border_width = 1, .radius = 3 });
    } else if (ctx.hovered(r)) ctx.scene.quad(r, .{ .color = theme.sidebar_hover, .radius = 3 });
    ctx.labelCentered(r, label, ws.fonts.ui, if (active) Color.hex(0xffffff) else theme.sidebar_foreground);
    addHit(ws, r, what);
}

pub fn paintBody(ws: *Workspace, ctx: *const ui.Ctx) void {
    const st = &ws.agenda;
    const a = ensure(ws) orelse return;
    if (signature(ws) != st.signature) refresh(ws);
    const theme = ctx.theme;
    const scene = ctx.scene;
    const sb = ws.sidebar_rect;
    st.hits.clearRetainingCapacity();
    const left = sb.x + 12;
    const right = sb.right() - 10;
    const dim = theme.sidebar_foreground_dim;
    const fg = theme.sidebar_foreground;
    var y = sb.y + header_h + 2;

    // Day / Week / To do, and the actions.
    {
        var x = left - 4;
        const views = [_]struct { v: agenda_mod.View, text: []const u8 }{
            .{ .v = .day, .text = "Day" },
            .{ .v = .week, .text = "Week" },
            .{ .v = .todo, .text = "To do" },
        };
        for (views) |v| {
            const w = ctx.text.measure(v.text, ws.fonts.ui) + 16;
            const active = a.view == v.v or (v.v == .todo and a.view == .todo);
            textButton(ws, ctx, .{ .x = x, .y = y, .w = w, .h = 24 }, v.text, active, .{ .view = v.v });
            x += w + 2;
        }
        const cy = y + 12;
        var ix = right - 11;
        // More: three dots.
        {
            const r: Rect = .{ .x = ix - 11, .y = y, .w = 22, .h = 24 };
            iconButton(ws, ctx, r, .more);
            for ([_]f32{ -5, 0, 5 }) |dx| scene.quad(.{ .x = ix + dx - 1, .y = cy + 1, .w = 2, .h = 2 }, .{ .color = fg, .radius = 1 });
            ix -= 24;
        }
        // Refresh: an open ring with an arrow head.
        {
            const r: Rect = .{ .x = ix - 11, .y = y, .w = 22, .h = 24 };
            iconButton(ws, ctx, r, .refresh);
            scene.quad(.{ .x = ix - 5, .y = cy - 5, .w = 10, .h = 10 }, .{ .border_color = fg, .border_width = 1.5, .radius = 5 });
            scene.fill(.{ .x = ix + 1, .y = cy - 6, .w = 6, .h = 3 }, theme.sidebar_background);
            scene.fill(.{ .x = ix + 1, .y = cy - 7, .w = 5, .h = 1.5 }, fg);
            scene.fill(.{ .x = ix + 4.5, .y = cy - 7, .w = 1.5, .h = 4.5 }, fg);
            ix -= 24;
        }
        // Notes: a page with lines.
        {
            const r: Rect = .{ .x = ix - 11, .y = y, .w = 22, .h = 24 };
            iconButton(ws, ctx, r, .notes);
            scene.quad(.{ .x = ix - 6, .y = cy - 6, .w = 12, .h = 12 }, .{ .border_color = fg, .border_width = 1.25, .radius = 1 });
            scene.fill(.{ .x = ix - 3, .y = cy - 2, .w = 6, .h = 1.25 }, fg);
            scene.fill(.{ .x = ix - 3, .y = cy + 1.5, .w = 6, .h = 1.25 }, fg);
            ix -= 24;
        }
        // Capture: a plus.
        {
            const r: Rect = .{ .x = ix - 11, .y = y, .w = 22, .h = 24 };
            iconButton(ws, ctx, r, .capture);
            scene.fill(.{ .x = ix - 6, .y = cy - 0.75, .w = 12, .h = 1.5 }, fg);
            scene.fill(.{ .x = ix - 0.75, .y = cy - 6, .w = 1.5, .h = 12 }, fg);
        }
    }
    y += 28;

    // Previous / Today / next.
    if (a.view != .todo) {
        var x = left;
        const prev: Rect = .{ .x = x, .y = y, .w = 24, .h = 24 };
        iconButton(ws, ctx, prev, .previous);
        chevronGlyph(ctx, prev, false, fg);
        x += 26;
        const tw = ctx.text.measure("Today", ws.fonts.ui) + 18;
        textButton(ws, ctx, .{ .x = x, .y = y, .w = tw, .h = 24 }, "Today", false, .today);
        x += tw + 2;
        const next: Rect = .{ .x = x, .y = y, .w = 24, .h = 24 };
        iconButton(ws, ctx, next, .next);
        chevronGlyph(ctx, next, true, fg);
        y += 28;
    }

    // The period.
    {
        var buf: [96]u8 = undefined;
        _ = ctx.labelFit(.{ .x = left, .y = y, .w = right - left, .h = 22 }, periodTitle(a, &buf), ws.fonts.ui_bold, fg);
        y += 24;
    }

    // The filter box.
    {
        const box: Rect = .{ .x = left - 4, .y = y, .w = right - left + 8, .h = 24 };
        scene.quad(box, .{ .color = theme.input_background, .border_color = if (st.filter_focused) theme.input_border_focus else .transparent, .border_width = 1, .radius = 3 });
        const mc = box.y + box.h / 2;
        scene.quad(.{ .x = box.x + 8, .y = mc - 5, .w = 8, .h = 8 }, .{ .border_color = theme.list_foreground_dim, .border_width = 1.25, .radius = 4 });
        for (0..3) |i| {
            const t: f32 = @floatFromInt(i);
            scene.fill(.{ .x = box.x + 15 + t, .y = mc + 2 + t, .w = 1.5, .h = 1.5 }, theme.list_foreground_dim);
        }
        const text_r: Rect = .{ .x = box.x + 26, .y = box.y, .w = box.w - 32, .h = box.h };
        if (st.filter.items.len == 0) {
            _ = ctx.label(text_r, "Filter (words; :tag)", ws.fonts.ui, theme.list_foreground_dim);
        } else {
            const w = ctx.labelFit(text_r, st.filter.items, ws.fonts.ui, fg);
            if (st.filter_focused) scene.fill(.{ .x = text_r.x + @min(w, text_r.w) + 1, .y = box.y + 5, .w = 1, .h = box.h - 10 }, fg);
        }
        if (st.filter_focused and st.filter.items.len == 0) scene.fill(.{ .x = text_r.x, .y = box.y + 5, .w = 1, .h = box.h - 10 }, fg);
        addHit(ws, box, .filter);
        y += 30;
    }

    // The list, and the selected entry's details under it.
    const body_bottom = sb.bottom() - tab_h;
    const has_sel = itemAt(a, st.selected) != null;
    const avail = body_bottom - y;
    const details_h: f32 = if (has_sel) @min(details_max, @round(avail * 0.42)) else 0;
    const list: Rect = .{ .x = sb.x + 1, .y = y, .w = sb.w - 1, .h = @max(0, avail - details_h) };
    st.list_rect = list;
    paintList(ws, ctx, a, list);
    if (has_sel) paintDetails(ws, ctx, itemAt(a, st.selected).?, .{ .x = sb.x + 1, .y = list.bottom(), .w = sb.w - 1, .h = details_h });
    _ = dim;
}

/// A "<" or ">" a pixel column at a time, centred in `r`.
fn chevronGlyph(ctx: *const ui.Ctx, r: Rect, right: bool, color: Color) void {
    const cx = r.x + r.w / 2;
    const cy = r.y + r.h / 2;
    var i: i32 = -4;
    while (i <= 4) : (i += 1) {
        const off: f32 = @floatFromInt(4 - @as(i32, @intCast(@abs(i))));
        const x = if (right) cx - 2 + off else cx + 2 - off;
        ctx.scene.fill(.{ .x = x - 0.5, .y = cy + @as(f32, @floatFromInt(i)) - 0.5, .w = 1.6, .h = 1.2 }, color);
    }
}

fn periodTitle(a: *const Agenda, buf: []u8) []const u8 {
    return switch (a.view) {
        .week => blk: {
            const end = a.start.addDays(6);
            if (a.start.month == end.month) {
                break :blk std.fmt.bufPrint(buf, "Week {d} · {d} – {d} {s} {d}", .{ a.start.isoWeek(), a.start.day, end.day, date.month_names[end.month - 1], @as(u32, @intCast(end.year)) }) catch "";
            }
            break :blk std.fmt.bufPrint(buf, "Week {d} · {d} {s} – {d} {s} {d}", .{ a.start.isoWeek(), a.start.day, date.month_names[a.start.month - 1][0..3], end.day, date.month_names[end.month - 1][0..3], @as(u32, @intCast(end.year)) }) catch "";
        },
        .day => std.fmt.bufPrint(buf, "{s} {d} {s} {d}", .{ date.day_names[a.start.weekday()], a.start.day, date.month_names[a.start.month - 1], @as(u32, @intCast(a.start.year)) }) catch "",
        .todo => "All TODO items",
        .search => "Search",
        .tags => "Tags",
    };
}

fn paintList(ws: *Workspace, ctx: *const ui.Ctx, a: *const Agenda, list: Rect) void {
    const st = &ws.agenda;
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.pushClip(list) catch return;
    defer scene.popClip();
    const today = date.Date.today();
    const left = list.x + 12;
    var y = list.y - st.scroll;
    var flat: usize = 0;
    var empty = true;
    for (a.groups.items) |g| {
        if (g.items.len > 0) empty = false;
        // The group's header.
        if (y + group_h > list.y and y < list.bottom()) {
            const is_today = if (g.day) |d| d.eql(today) else false;
            var hbuf: [96]u8 = undefined;
            var dbuf: [64]u8 = undefined;
            const title = if (g.day) |d|
                (if (is_today) std.fmt.bufPrint(&hbuf, "Today · {s}", .{longDay(d, &dbuf)}) catch g.header else longDay(d, &hbuf))
            else
                g.header;
            const color = if (is_today) accent else theme.sidebar_foreground;
            _ = ctx.labelFit(.{ .x = left - 4, .y = y + 4, .w = list.w - 60, .h = group_h - 4 }, title, ws.fonts.ui, color);
            if (g.items.len > 0) {
                var nbuf: [12]u8 = undefined;
                _ = ctx.labelRight(.{ .x = list.x, .y = y + 4, .w = list.w - 12, .h = group_h - 4 }, std.fmt.bufPrint(&nbuf, "{d}", .{g.items.len}) catch "", ws.fonts.ui_small, theme.sidebar_foreground_dim);
            }
        }
        y += group_h;
        for (g.items) |item| {
            if (y + item_h > list.y and y < list.bottom()) {
                paintItem(ws, ctx, item, .{ .x = list.x, .y = y, .w = list.w, .h = item_h }, flat);
            }
            y += item_h;
            flat += 1;
        }
        y += 4;
    }
    if (empty and a.groups.items.len == 0 or (empty and a.view == .todo)) {
        _ = ctx.label(.{ .x = left, .y = list.y + 8, .w = list.w - 24, .h = 24 }, "No entries", ws.fonts.ui, theme.sidebar_foreground_dim);
    }
    st.content_h = y + st.scroll - list.y;
    const max_scroll = @max(0, st.content_h - list.h);
    if (st.scroll > max_scroll) st.scroll = max_scroll;
}

/// "Tuesday 3 November".
fn longDay(d: date.Date, buf: []u8) []const u8 {
    return std.fmt.bufPrint(buf, "{s} {d} {s}", .{ date.day_names[d.weekday()], d.day, date.month_names[d.month - 1] }) catch "";
}

fn paintItem(ws: *Workspace, ctx: *const ui.Ctx, item: Item, r: Rect, flat: usize) void {
    const st = &ws.agenda;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const selected = st.selected == flat;
    if (selected) scene.fill(r, theme.sidebar_selected) else if (ctx.hovered(r) and ctx.mouse.y >= st.list_rect.y) scene.fill(r, theme.sidebar_hover);
    addHit(ws, r, .{ .row = flat });
    const dim = theme.sidebar_foreground_dim;
    const fg = if (item.done) dim else theme.sidebar_foreground;
    const x0 = r.x + 12;

    // The check circle: click marks it done, or back to to do.
    const cx = x0 + 4;
    const cy = r.y + 12;
    if (item.todo.len > 0) {
        const circle: Rect = .{ .x = cx - 8, .y = cy - 8, .w = 16, .h = 16 };
        if (item.done) {
            scene.quad(.{ .x = cx - 5, .y = cy - 5, .w = 10, .h = 10 }, .{ .color = done_color, .radius = 5 });
            scene.fill(.{ .x = cx - 2.5, .y = cy, .w = 2, .h = 1.5 }, theme.sidebar_background);
            scene.fill(.{ .x = cx - 1, .y = cy - 0.5, .w = 4, .h = 1.5 }, theme.sidebar_background);
        } else {
            scene.quad(.{ .x = cx - 5, .y = cy - 5, .w = 10, .h = 10 }, .{ .border_color = if (ctx.hovered(circle)) fg else dim, .border_width = 1.25, .radius = 5 });
        }
        addHit(ws, circle, .{ .check = flat });
    }

    // First line: state, priority, title.
    var x = x0 + 22;
    const line1: Rect = .{ .x = x, .y = r.y + 2, .w = 0, .h = 20 };
    const limit = r.right() - 10;
    if (item.todo.len > 0) {
        const c = if (item.done) done_color else todo_color;
        x += ctx.label(.{ .x = x, .y = line1.y, .w = limit - x, .h = line1.h }, item.todo, ws.fonts.ui_bold, c) + 6;
    }
    if (item.priority) |p| {
        var pbuf: [4]u8 = undefined;
        const text = std.fmt.bufPrint(&pbuf, "[#{c}]", .{p}) catch "";
        x += ctx.label(.{ .x = x, .y = line1.y, .w = limit - x, .h = line1.h }, text, ws.fonts.ui, priority_color) + 6;
    }
    if (x < limit) {
        const w = ctx.labelFit(.{ .x = x, .y = line1.y, .w = limit - x, .h = line1.h }, item.title, ws.fonts.ui, fg);
        if (item.done) scene.fill(.{ .x = x, .y = line1.y + 10, .w = @min(w, limit - x), .h = 1 }, fg);
    }

    // Second line: why it is here, its file's category and its tags.
    var buf: [256]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    var sep = false;
    for ([_][]const u8{ item.label, item.category, item.tags }) |part| {
        if (part.len == 0) continue;
        if (sep) w.writeAll(" · ") catch break;
        w.writeAll(part) catch break;
        sep = true;
    }
    const second = switch (item.kind) {
        .overdue => overdue_color,
        .upcoming => upcoming_color,
        .normal => dim,
    };
    _ = ctx.labelFit(.{ .x = x0 + 22, .y = r.y + 21, .w = limit - (x0 + 22), .h = 16 }, w.buffered(), ws.fonts.ui_small, second);
}

fn paintDetails(ws: *Workspace, ctx: *const ui.Ctx, item: Item, r: Rect) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.fill(r, theme.sidebar_background);
    scene.fill(.{ .x = r.x, .y = r.y, .w = r.w, .h = 1 }, theme.tab_separator);
    scene.pushClip(r) catch return;
    defer scene.popClip();
    const left = r.x + 12;
    const width = r.w - 24;
    const dim = theme.sidebar_foreground_dim;
    var y = r.y + 6;

    // Title with its state and priority.
    var x = left;
    if (item.todo.len > 0) x += ctx.label(.{ .x = x, .y = y, .w = width, .h = 20 }, item.todo, ws.fonts.ui_bold, if (item.done) done_color else todo_color) + 6;
    if (item.priority) |p| {
        var pbuf: [4]u8 = undefined;
        x += ctx.label(.{ .x = x, .y = y, .w = width, .h = 20 }, std.fmt.bufPrint(&pbuf, "[#{c}]", .{p}) catch "", ws.fonts.ui, priority_color) + 6;
    }
    _ = ctx.labelFit(.{ .x = x, .y = y, .w = r.right() - 12 - x, .h = 20 }, item.title, ws.fonts.ui_bold, theme.sidebar_foreground);
    y += 20;

    var buf: [320]u8 = undefined;
    if (item.outline.len > 0) {
        _ = ctx.labelFit(.{ .x = left, .y = y, .w = width, .h = 18 }, item.outline, ws.fonts.ui_small, dim);
        y += 18;
    }
    const base = std.fs.path.basename(item.target.path);
    _ = ctx.labelFit(.{ .x = left, .y = y, .w = width, .h = 18 }, std.fmt.bufPrint(&buf, "{s}:{d}", .{ base, item.line + 1 }) catch base, ws.fonts.ui_small, dim);
    y += 18;
    const planning = [_]struct { name: []const u8, value: []const u8 }{
        .{ .name = "Scheduled", .value = item.scheduled },
        .{ .name = "Deadline", .value = item.deadline },
        .{ .name = "Closed", .value = item.closed },
        .{ .name = "Tags", .value = item.tags },
    };
    for (planning) |p| {
        if (p.value.len == 0) continue;
        if (y + 18 > r.bottom() - 30) break;
        _ = ctx.label(.{ .x = left, .y = y, .w = 80, .h = 18 }, p.name, ws.fonts.ui_small, dim);
        _ = ctx.labelFit(.{ .x = left + 74, .y = y, .w = width - 74, .h = 18 }, p.value, ws.fonts.ui_small, theme.sidebar_foreground);
        y += 18;
    }

    // Its notes, as far as they fit above the Open button.
    var lines = std.mem.splitScalar(u8, item.notes, '\n');
    while (lines.next()) |ln| {
        if (y + 18 > r.bottom() - 30) break;
        _ = ctx.labelFit(.{ .x = left, .y = y, .w = width, .h = 18 }, ln, ws.fonts.ui_small, theme.sidebar_foreground);
        y += 18;
    }

    const open_w = ctx.text.measure("Open", ws.fonts.ui) + 20;
    const open_r: Rect = .{ .x = r.right() - 12 - open_w, .y = r.bottom() - 28, .w = open_w, .h = 22 };
    scene.quad(open_r, .{ .color = if (ctx.hovered(open_r)) theme.button_active else theme.button_background, .radius = 3 });
    ctx.labelCentered(open_r, "Open", ws.fonts.ui, Color.hex(0xffffff));
    addHit(ws, open_r, .open);
}

// ---- Events -------------------------------------------------------------------

/// Called first for every event: the tab strip under the side bar, and,
/// with the Org Agenda tab showing, everything inside it.
pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    const st = &ws.agenda;
    const shown = ws.sidebar_visible and !ws.distraction_free;
    switch (ev) {
        .mouse_down => |m| {
            if (ws.menuIsOpen() or ws.overlay != null) return false;
            const sb = ws.sidebar_rect;
            if (!shown or !sb.contains(m.pos.x, m.pos.y)) {
                st.focused = false;
                st.filter_focused = false;
                return false;
            }
            if (m.pos.y >= sb.bottom() - tab_h) return tabClick(ws, m);
            if (st.tab != .agenda) {
                st.focused = false;
                return false;
            }
            // The title row's close button is the workspace's.
            if (m.pos.y < sb.y + header_h) return false;
            return click(ws, m);
        },
        .scroll => |s| {
            if (!shown or st.tab != .agenda or ws.overlay != null) return false;
            if (!st.list_rect.contains(s.pos.x, s.pos.y)) return false;
            const dy = if (s.unit == .lines) s.delta.y * item_h else s.delta.y;
            st.scroll = std.math.clamp(st.scroll + dy, 0, @max(0, st.content_h - st.list_rect.h));
            return true;
        },
        .key_down => |k| {
            if (!shown or st.tab != .agenda or !st.focused or ws.overlay != null or ws.menuIsOpen() or ws.leader.active) return false;
            return key(ws, k);
        },
        .text_input => |t| {
            if (!shown or st.tab != .agenda or !st.focused or !st.filter_focused) return false;
            typeFilter(ws, t);
            return true;
        },
        else => return false,
    }
}

fn tabClick(ws: *Workspace, m: platform.MouseEvent) bool {
    if (m.button != .left) return true;
    const st = &ws.agenda;
    const x = m.pos.x - (ws.sidebar_rect.x + 1);
    var edge: f32 = 0;
    st.tab = .scm;
    for (tabLabels(ws)) |l| {
        edge += ws.env.text.measure(l.text, ws.fonts.ui_small) + tab_pad;
        if (x < edge) {
            st.tab = l.tab;
            break;
        }
    }
    st.focused = st.tab == .agenda;
    if (st.tab == .scm) scm_ui.onShown(ws);
    if (st.tab == .agenda) {
        ws.sidebar_width = @max(ws.sidebar_width, min_width);
        _ = ensure(ws);
        refresh(ws);
    }
    return true;
}

fn click(ws: *Workspace, m: platform.MouseEvent) bool {
    const st = &ws.agenda;
    const a = ensure(ws) orelse return true;
    st.focused = true;
    st.filter_focused = false;
    var hit: ?What = null;
    // Later hits sit on top of earlier ones (the check circle over its row).
    var i = st.hits.items.len;
    while (i > 0) {
        i -= 1;
        const h = st.hits.items[i];
        if (!h.rect.contains(m.pos.x, m.pos.y)) continue;
        // The list is clipped: rows scrolled under the controls don't count.
        if (h.what == .row or h.what == .check) if (!st.list_rect.contains(m.pos.x, m.pos.y)) continue;
        hit = h.what;
        break;
    }
    const what = hit orelse return true;
    if (m.button == .right) {
        switch (what) {
            .row => |n| st.selected = n,
            else => {},
        }
        return true;
    }
    if (m.button != .left) return true;
    switch (what) {
        .tab => {},
        .view => |v| {
            a.setView(v);
            st.scroll = 0;
            st.selected = 0;
            refresh(ws);
        },
        .previous => shift(ws, false),
        .next => shift(ws, true),
        .today => {
            a.goToday();
            refresh(ws);
        },
        .capture => org_ui.startCapture(ws),
        .notes => ws.perform(.org_goto_capture_file),
        .refresh => refresh(ws),
        .more => org_ui.perform(ws, .org_agenda_dispatch),
        .filter => st.filter_focused = true,
        .row => |n| {
            st.selected = n;
            if (m.click_count >= 2) openSelected(ws);
        },
        .check => |n| {
            st.selected = n;
            toggleDone(ws);
        },
        .open => openSelected(ws),
    }
    ws.dirty = true;
    return true;
}

fn shift(ws: *Workspace, forward: bool) void {
    const a = ws.agenda.engine orelse return;
    a.shift(forward);
    ws.agenda.scroll = 0;
    refresh(ws);
}

fn typeFilter(ws: *Workspace, text: []const u8) void {
    const st = &ws.agenda;
    st.filter.appendSlice(ws.gpa, text) catch return;
    applyFilter(ws);
}

/// The filter box: words match titles, states and categories; `:tag` or
/// `+tag` keeps entries with that tag.
fn applyFilter(ws: *Workspace) void {
    const a = ws.agenda.engine orelse return;
    var tags: [160]u8 = undefined;
    var words: [160]u8 = undefined;
    var tn: usize = 0;
    var wn: usize = 0;
    var it = std.mem.tokenizeScalar(u8, ws.agenda.filter.items, ' ');
    while (it.next()) |tok| {
        if ((tok[0] == ':' or tok[0] == '+') and tok.len > 1) {
            const tag = std.mem.trim(u8, tok[1..], ":");
            if (tag.len == 0 or tn + tag.len + 1 > tags.len) continue;
            tags[tn] = '+';
            @memcpy(tags[tn + 1 ..][0..tag.len], tag);
            tn += tag.len + 1;
        } else {
            if (wn + tok.len + 1 > words.len) continue;
            if (wn > 0) {
                words[wn] = ' ';
                wn += 1;
            }
            @memcpy(words[wn..][0..tok.len], tok);
            wn += tok.len;
        }
    }
    a.setFilter(.{ .tags = tags[0..tn], .text = words[0..wn] }) catch return;
    ws.agenda.selected = 0;
    ws.agenda.scroll = 0;
    refresh(ws);
}

fn key(ws: *Workspace, k: platform.KeyEvent) bool {
    const st = &ws.agenda;
    const a = ensure(ws) orelse return false;
    const m = k.mods;
    if (m.ctrl or m.alt or m.super) return false;

    if (st.filter_focused) {
        switch (k.key) {
            .named => |n| switch (n) {
                .escape, .enter => {
                    st.filter_focused = false;
                    return true;
                },
                .backspace => {
                    if (st.filter.items.len > 0) {
                        // Whole UTF-8 characters.
                        var n_bytes: usize = 1;
                        while (n_bytes < st.filter.items.len and st.filter.items[st.filter.items.len - n_bytes] & 0xC0 == 0x80) n_bytes += 1;
                        st.filter.shrinkRetainingCapacity(st.filter.items.len - n_bytes);
                        applyFilter(ws);
                    }
                    return true;
                },
                .down => {
                    st.filter_focused = false;
                    return true;
                },
                else => return false,
            },
            .char => |cp| {
                if (k.text.len > 0) {
                    typeFilter(ws, k.text);
                    return true;
                }
                var enc: [4]u8 = undefined;
                const len = std.unicode.utf8Encode(cp, &enc) catch return false;
                typeFilter(ws, enc[0..len]);
                return true;
            },
        }
    }

    const n = itemCount(a);
    const ch: u21 = switch (k.key) {
        .char => if (k.text.len == 1) k.text[0] else k.key.char,
        .named => |nk| switch (nk) {
            .down => {
                if (n > 0) st.selected = @min(st.selected + 1, n - 1);
                scrollToSelected(ws);
                return true;
            },
            .up => {
                st.selected -|= 1;
                scrollToSelected(ws);
                return true;
            },
            .page_down => {
                if (n > 0) st.selected = @min(st.selected + 8, n - 1);
                scrollToSelected(ws);
                return true;
            },
            .page_up => {
                st.selected -|= 8;
                scrollToSelected(ws);
                return true;
            },
            .home => {
                st.selected = 0;
                scrollToSelected(ws);
                return true;
            },
            .end => {
                st.selected = if (n == 0) 0 else n - 1;
                scrollToSelected(ws);
                return true;
            },
            .enter => {
                openSelected(ws);
                return true;
            },
            .escape => {
                st.focused = false;
                return true;
            },
            else => return false,
        },
    };
    switch (ch) {
        ' ' => toggleDone(ws),
        'o' => openSelected(ws),
        't' => cycleTodo(ws, true),
        'T' => cycleTodo(ws, false),
        's', '>' => itemAction(ws, .org_schedule),
        'D' => itemAction(ws, .org_deadline),
        '+' => itemAction(ws, .org_priority_up),
        '-' => itemAction(ws, .org_priority_down),
        ':' => itemAction(ws, .org_set_tags),
        'I' => itemAction(ws, .org_clock_in),
        'O' => ws.perform(.org_clock_out),
        '$' => itemAction(ws, .org_archive),
        'W' => itemAction(ws, .org_refile),
        'd' => setView(ws, .day),
        'w', 'a' => setView(ws, .week),
        'f' => shift(ws, true),
        'b' => shift(ws, false),
        '.' => {
            a.goToday();
            refresh(ws);
        },
        'c', 'k' => org_ui.startCapture(ws),
        'r', 'g' => refresh(ws),
        '/' => st.filter_focused = true,
        else => return false,
    }
    ws.dirty = true;
    return true;
}

fn setView(ws: *Workspace, v: agenda_mod.View) void {
    const a = ws.agenda.engine orelse return;
    a.setView(v);
    ws.agenda.scroll = 0;
    ws.agenda.selected = 0;
    refresh(ws);
}

fn scrollToSelected(ws: *Workspace) void {
    const st = &ws.agenda;
    const a = st.engine orelse return;
    // Walk the groups to the selected row's top.
    var y: f32 = 0;
    var flat: usize = 0;
    for (a.groups.items) |g| {
        y += group_h;
        for (g.items) |_| {
            if (flat == st.selected) {
                if (y < st.scroll) st.scroll = y - group_h;
                if (y + item_h > st.scroll + st.list_rect.h) st.scroll = y + item_h - st.list_rect.h;
                st.scroll = @max(0, st.scroll);
                return;
            }
            y += item_h;
            flat += 1;
        }
        y += 4;
    }
}

// ---- Actions on the selected entry ----------------------------------------------

/// Enter / double-click: the heading in the editor.
fn openSelected(ws: *Workspace) void {
    const item = selectedItem(ws) orelse return;
    const ed = ws.openFile(item.target.path, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ item.target.path, err });
        return;
    };
    ed.setSingleSelection(.caret(@min(item.target.offset, ed.buffer.len())));
    ed.afterCommand(true);
    ws.agenda.focused = false;
    ws.focus = .editor;
    ed.focused = true;
}

fn toggleDone(ws: *Workspace) void {
    const item = selectedItem(ws) orelse return;
    if (item.todo.len == 0) return;
    // Done goes back to to do; anything else is done (the keyword after the
    // open ones in the cycle, as org's agenda `t` would reach it).
    const begin = beginEdit(ws, item) orelse return;
    const kw: []const u8 = if (item.done) "TODO" else "DONE";
    _ = org.setTodoAt(begin.editor, headingRow(begin.editor, item), .{ .set = kw });
    finishEdit(ws);
}

fn cycleTodo(ws: *Workspace, forward: bool) void {
    const item = selectedItem(ws) orelse return;
    const begin = beginEdit(ws, item) orelse return;
    _ = org.setTodoAt(begin.editor, headingRow(begin.editor, item), .{ .cycle = forward });
    finishEdit(ws);
}

/// The heading's row in its file as it is now, which may have moved since
/// the agenda was built: found again by offset, then by its title.
fn headingRow(ed: *Editor, item: Item) u32 {
    const offset = @min(item.target.offset, ed.buffer.len());
    return ed.buffer.offsetToPoint(offset).row;
}

/// Opens the entry's file with the caret on its heading, remembering how
/// things stood to put them back.
fn beginEdit(ws: *Workspace, item: Item) ?Pending {
    const st = &ws.agenda;
    const previous = ws.activeEditor();
    var was_open = false;
    var was_dirty = false;
    for (ws.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, item.target.path)) {
        was_open = true;
        was_dirty = b.isDirty();
    };
    const ed = ws.openFile(item.target.path, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ item.target.path, err });
        return null;
    };
    ed.setSingleSelection(.caret(@min(item.target.offset, ed.buffer.len())));
    const p: Pending = .{ .editor = ed, .previous = previous, .was_open = was_open, .was_dirty = was_dirty };
    st.pending = p;
    return p;
}

/// An org action (date, tags, priority, clock, refile, archive) on the entry.
fn itemAction(ws: *Workspace, action: Action) void {
    const item = selectedItem(ws) orelse return;
    _ = beginEdit(ws, item) orelse return;
    ws.perform(action);
    // Prompts (dates, tags, refile) come back once answered.
    if (ws.overlay == null) finishEdit(ws);
}

/// After an edit: the file saved when it had no unsaved changes of its own,
/// closed again when only the agenda had opened it, the previous tab back,
/// the agenda refreshed. Also what org_ui calls once a prompt is answered.
pub fn finishEdit(ws: *Workspace) void {
    const st = &ws.agenda;
    const p = st.pending orelse return;
    st.pending = null;
    if (!p.was_dirty and p.editor.buffer.isDirty() and p.editor.buffer.path != null) {
        p.editor.buffer.save(ws.io) catch |err| ws.setStatus("Unable to save: {t}", .{err});
    }
    if (!p.was_open) {
        for (ws.tabs.items, 0..) |t, i| if (t.editor == p.editor) {
            if (!t.editor.buffer.isDirty()) ws.closeTabNow(i);
            break;
        };
    }
    if (p.previous) |prev| for (ws.tabs.items, 0..) |t, i| if (t.editor == prev) {
        ws.activateTab(i);
        break;
    };
    refresh(ws);
    ws.dirty = true;
}

/// An answered or cancelled prompt of an action begun here.
pub fn promptDone(ws: *Workspace, answered: bool) void {
    if (ws.agenda.pending == null) return;
    if (answered) finishEdit(ws) else ws.agenda.pending = null;
}
