//! The panel's Memory view (Developer: Show Memory): what the process holds
//! right now, as the kernel counts it (resident size, footprint, its peak),
//! what the program allocated (live bytes, the peak, live allocations), and
//! what each subsystem keeps (buffers and their text, undo history, syntax
//! trees, the glyph atlas and text caches, terminals, language servers),
//! with a history of the last few minutes drawn as bars. Reset Peak starts
//! a new measurement window; Copy Report puts the numbers on the clipboard.
//!
//! The history is sampled once a second while the view is shown, and by any
//! event that reaches the redraw timer while it is hidden, so a fresh view
//! already has a little behind it. The numbers are cheap to collect: nothing
//! here walks text.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const panel_ui = @import("panel_ui.zig");
const platform = @import("../../framework/platform/platform.zig");
const proc_stats = @import("../../framework/platform/proc_stats.zig");
const mem_stats = @import("../../framework/util/mem_stats.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const ui = @import("../../framework/ui/ui.zig");

const Workspace = workspace_mod.Workspace;
const Rect = ui.Rect;
const Color = ui.Color;

const toolbar_h: f32 = 30;
const row_h: f32 = 20;
const chart_h: f32 = 28;

/// Samples kept, one per `sample_interval_ms`.
pub const history_len = 180;
pub const sample_interval_ms: i64 = 1000;

pub const Sample = struct {
    resident: u64 = 0,
    footprint: u64 = 0,
    live: u64 = 0,
};

/// One window per process, so the history is a static: no allocation to leak.
var history: [history_len]Sample = @splat(.{});
var history_count: usize = 0;
var history_head: usize = 0;
var last_sample_ms: ?i64 = null;
var buttons: [2]Rect = .{ .{}, .{} };

/// Everything the view shows.
pub const Report = struct {
    proc: ?proc_stats.Usage,
    alloc: ?mem_stats.Counting.Snapshot,
    buffers: usize = 0,
    buffer_bytes: usize = 0,
    undo_steps: usize = 0,
    undo_bytes: usize = 0,
    syntax_trees: usize = 0,
    diagnostics: usize = 0,
    text: text_mod.TextSystem.Stats,
    terminals: usize = 0,
    lsp_servers: usize = 0,
    background_tasks: u32 = 0,
};

pub fn collect(ws: *Workspace) Report {
    var r: Report = .{
        .proc = proc_stats.current(),
        .alloc = if (mem_stats.current()) |c| c.snapshot() else null,
        .text = ws.env.text.stats(),
        .terminals = ws.panel.terminals.items.len,
        .lsp_servers = if (ws.lsp) |l| l.servers.items.len else 0,
        .background_tasks = ws.executor.active.load(.monotonic),
    };
    r.buffers = ws.buffers.items.len;
    for (ws.buffers.items) |b| {
        r.buffer_bytes += b.rope.len();
        const u = b.undoStats();
        r.undo_steps += u.steps;
        r.undo_bytes += u.bytes;
        if (b.syntax != null) r.syntax_trees += 1;
        r.diagnostics += b.diagnostics.items.len;
    }
    return r;
}

/// Adds a point to the history if a second has passed since the last one.
pub fn sample(r: *const Report) void {
    const now = platform.nowMs();
    if (last_sample_ms) |last| if (now - last < sample_interval_ms) return;
    last_sample_ms = now;
    push(.{
        .resident = if (r.proc) |p| p.resident else 0,
        .footprint = if (r.proc) |p| p.footprint else 0,
        .live = if (r.alloc) |a| a.live_bytes else 0,
    });
}

fn push(s: Sample) void {
    history[history_head] = s;
    history_head = (history_head + 1) % history_len;
    history_count = @min(history_len, history_count + 1);
}

/// Oldest first.
fn at(i: usize) Sample {
    const start = (history_head + history_len - history_count) % history_len;
    return history[(start + i) % history_len];
}

/// Milliseconds until the view wants to be drawn again, or null (it is not
/// shown). The event loop calls this for every timer it sets, hidden or not,
/// which is what keeps a hidden view's history going.
pub fn nextTimer(ws: *Workspace) ?i64 {
    const shown = ws.panel.visible and ws.panel.view == .memory;
    if (!shown) {
        const r = collect(ws);
        sample(&r);
        return null;
    }
    const last = last_sample_ms orelse return 1;
    return @max(1, sample_interval_ms - (platform.nowMs() - last));
}

// ---- Text --------------------------------------------------------------------

fn bytesText(buf: []u8, n: u64) []const u8 {
    const f: f64 = @floatFromInt(n);
    if (n >= 1 << 30) return std.fmt.bufPrint(buf, "{d:.2} GiB", .{f / (1 << 30)}) catch "";
    if (n >= 1 << 20) return std.fmt.bufPrint(buf, "{d:.1} MiB", .{f / (1 << 20)}) catch "";
    if (n >= 1 << 10) return std.fmt.bufPrint(buf, "{d:.1} KiB", .{f / (1 << 10)}) catch "";
    return std.fmt.bufPrint(buf, "{d} B", .{n}) catch "";
}

const Row = struct { name: []const u8, value: []const u8 };

/// `column` is where the view draws the section.
const Section = struct { title: []const u8, start: usize, end: usize, column: u8 };

/// The rows of every section, as text; one place for the view and the report.
const Table = struct {
    bufs: [32][48]u8 = undefined,
    used: usize = 0,
    rows: [32]Row = undefined,
    sections: [4]Section = undefined,
    n: usize = 0,

    fn add(t: *Table, name: []const u8, comptime fmt: []const u8, args: anytype) void {
        if (t.n >= t.rows.len) return;
        const text = std.fmt.bufPrint(&t.bufs[t.n], fmt, args) catch "";
        t.rows[t.n] = .{ .name = name, .value = text };
        t.n += 1;
    }

    fn bytes(t: *Table, name: []const u8, v: u64) void {
        if (t.n >= t.rows.len) return;
        t.rows[t.n] = .{ .name = name, .value = bytesText(&t.bufs[t.n], v) };
        t.n += 1;
    }
};

fn fill(t: *Table, r: *const Report) void {
    t.n = 0;
    var s: usize = 0;
    var start = t.n;
    if (r.proc) |p| {
        t.bytes("Resident", p.resident);
        t.bytes("Footprint", p.footprint);
        t.bytes("Peak", p.peak_footprint);
        t.add("CPU time", "{d:.1} s", .{@as(f64, @floatFromInt(p.cpu_ms)) / 1000});
    } else t.add("Process", "not available", .{});
    t.sections[s] = .{ .title = "Process", .start = start, .end = t.n, .column = 0 };
    s += 1;
    start = t.n;
    if (r.alloc) |a| {
        t.bytes("Live", a.live_bytes);
        t.bytes("Peak", a.peak_bytes);
        t.add("Live allocs", "{d}", .{a.live_allocs});
        t.add("Total allocs", "{d}", .{a.total_allocs});
    } else t.add("Allocator", "not counted", .{});
    t.sections[s] = .{ .title = "Allocator", .start = start, .end = t.n, .column = 1 };
    s += 1;
    start = t.n;
    t.add("Buffers", "{d}", .{r.buffers});
    t.bytes("Text", r.buffer_bytes);
    t.add("Undo steps", "{d}", .{r.undo_steps});
    t.bytes("Undo text", r.undo_bytes);
    t.add("Syntax trees", "{d}", .{r.syntax_trees});
    t.add("Diagnostics", "{d}", .{r.diagnostics});
    t.sections[s] = .{ .title = "Buffers", .start = start, .end = t.n, .column = 2 };
    s += 1;
    start = t.n;
    t.add("Fonts", "{d}", .{r.text.faces});
    t.add("Glyphs cached", "{d}", .{r.text.glyph_entries});
    t.add("Atlas", "{d}²", .{r.text.atlas_side});
    t.add("Line layouts", "{d}", .{r.text.line_layouts});
    t.add("Terminals", "{d}", .{r.terminals});
    t.add("Servers", "{d}", .{r.lsp_servers});
    t.add("Tasks", "{d}", .{r.background_tasks});
    t.sections[s] = .{ .title = "Rendering", .start = start, .end = t.n, .column = 3 };
}

/// The report as plain text (Copy Report, and the headless checks).
pub fn writeReport(w: *std.Io.Writer, r: *const Report) !void {
    var t: Table = .{};
    fill(&t, r);
    for (t.sections) |sec| {
        try w.print("{s}\n", .{sec.title});
        for (t.rows[sec.start..sec.end]) |row| try w.print("  {s:<20} {s}\n", .{ row.name, row.value });
    }
}

// ---- Painting ------------------------------------------------------------------

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    const report = collect(ws);
    sample(&report);

    scene.fill(body, theme.background);
    scene.pushClip(body) catch return;
    defer scene.popClip();

    // Toolbar: the title and the two buttons.
    const toolbar: Rect = .{ .x = body.x, .y = body.y, .w = body.w, .h = toolbar_h };
    scene.fill(toolbar, theme.tabbar_background);
    _ = ctx.label(.{ .x = body.x + 12, .y = body.y, .w = 200, .h = toolbar_h }, "Memory", ws.fonts.ui_bold, theme.tab_active_foreground);
    var x = body.right() - 8;
    const labels = [2][]const u8{ "Copy Report", "Reset Peak" };
    for (labels, 0..) |label, i| {
        const w = ctx.text.measure(label, ws.fonts.ui_small) + 20;
        x -= w;
        const r: Rect = .{ .x = x, .y = body.y + 4, .w = w, .h = toolbar_h - 8 };
        buttons[i] = r;
        scene.quad(r, .{ .color = if (ctx.hovered(r)) theme.button_active else theme.button_background, .radius = 3 });
        ctx.labelCentered(r, label, ws.fonts.ui_small, theme.tab_active_foreground);
        x -= 6;
    }
    scene.fill(.{ .x = body.x, .y = toolbar.bottom() - 1, .w = body.w, .h = 1 }, theme.tab_separator);

    // Left: the tables. Right: the history charts.
    const content: Rect = .{ .x = body.x, .y = toolbar.bottom(), .w = body.w, .h = @max(0, body.bottom() - toolbar.bottom()) };
    const left_w = @min(@round(content.w * 0.62), 720);
    var t: Table = .{};
    fill(&t, &report);
    paintTable(ws, ctx, .{ .x = content.x, .y = content.y, .w = left_w, .h = content.h }, &t);
    scene.fill(.{ .x = content.x + left_w, .y = content.y, .w = 1, .h = content.h }, theme.tab_separator);
    paintCharts(ws, ctx, .{ .x = content.x + left_w + 1, .y = content.y, .w = @max(0, content.w - left_w - 1), .h = content.h }, &report);
}

fn paintTable(ws: *Workspace, ctx: *const ui.Ctx, area: Rect, t: *const Table) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.pushClip(area) catch return;
    defer scene.popClip();
    const col_w = @floor(area.w / 4);
    var y: [4]f32 = @splat(area.y + 6);
    for (t.sections) |sec| {
        const col_x = area.x + @as(f32, @floatFromInt(sec.column)) * col_w;
        const cy = &y[sec.column];
        _ = ctx.label(.{ .x = col_x + 12, .y = cy.*, .w = col_w - 24, .h = row_h }, sec.title, ws.fonts.ui_bold, theme.tab_active_foreground);
        cy.* += row_h;
        for (t.rows[sec.start..sec.end]) |row| {
            _ = ctx.labelFit(.{ .x = col_x + 20, .y = cy.*, .w = col_w * 0.62, .h = row_h }, row.name, ws.fonts.ui_small, theme.list_foreground_dim);
            _ = ctx.labelRight(.{ .x = col_x + 20, .y = cy.*, .w = col_w - 36, .h = row_h }, row.value, ws.fonts.ui_small, theme.list_foreground);
            cy.* += row_h;
        }
        cy.* += 6;
    }
}

fn paintCharts(ws: *Workspace, ctx: *const ui.Ctx, area: Rect, r: *const Report) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.pushClip(area) catch return;
    defer scene.popClip();
    const Series = struct { name: []const u8, field: enum { resident, footprint, live }, color: Color };
    const series = [_]Series{
        .{ .name = "Resident", .field = .resident, .color = Color.hex(0x4e9ad8) },
        .{ .name = "Footprint", .field = .footprint, .color = Color.hex(0x7ab87a) },
        .{ .name = "Allocator live", .field = .live, .color = Color.hex(0xd8a14e) },
    };
    _ = r;
    var y = area.y + 4;
    const w = area.w - 32;
    for (series) |s| {
        var lo: u64 = std.math.maxInt(u64);
        var hi: u64 = 0;
        var now: u64 = 0;
        for (0..history_count) |i| {
            const v = value(at(i), s.field);
            lo = @min(lo, v);
            hi = @max(hi, v);
            now = v;
        }
        if (history_count == 0) lo = 0;
        var b_now: [32]u8 = undefined;
        var b_min: [32]u8 = undefined;
        var b_max: [32]u8 = undefined;
        var b_text: [96]u8 = undefined;
        _ = ctx.label(.{ .x = area.x + 16, .y = y, .w = w, .h = row_h }, s.name, ws.fonts.ui_bold, theme.tab_active_foreground);
        const caption = std.fmt.bufPrint(&b_text, "now {s}   min {s}   max {s}", .{ bytesText(&b_now, now), bytesText(&b_min, lo), bytesText(&b_max, hi) }) catch "";
        _ = ctx.labelRight(.{ .x = area.x + 16, .y = y, .w = w, .h = row_h }, caption, ws.fonts.ui_small, theme.list_foreground_dim);
        y += row_h;
        const chart: Rect = .{ .x = area.x + 16, .y = y, .w = w, .h = chart_h };
        scene.quad(chart, .{ .color = theme.input_background, .radius = 3 });
        // Bars scaled from zero to the highest sample (a little headroom), newest at the right.
        const bar_w: f32 = @max(1, @floor(chart.w / history_len));
        const top: f32 = @floatFromInt(@max(hi, 1));
        var i: usize = 0;
        while (i < history_count) : (i += 1) {
            const v: f32 = @floatFromInt(value(at(i), s.field));
            const h = @max(1, @round(v / top * (chart_h - 6)));
            const bx = chart.right() - @as(f32, @floatFromInt(history_count - i)) * bar_w;
            if (bx < chart.x) continue;
            scene.fill(.{ .x = bx, .y = chart.bottom() - 2 - h, .w = @max(1, bar_w - 1), .h = h }, s.color);
        }
        y += chart_h + 8;
    }
    if (history_count < 3) {
        _ = ctx.label(.{ .x = area.x + 16, .y = y, .w = w, .h = row_h }, "Sampling once a second…", ws.fonts.ui_small, theme.list_foreground_dim);
    }
}

fn value(s: Sample, field: anytype) u64 {
    return switch (field) {
        .resident => s.resident,
        .footprint => s.footprint,
        .live => s.live,
    };
}

// ---- Input ---------------------------------------------------------------------

/// A click in the view. Returns true if it hit a button.
pub fn onMouseDown(ws: *Workspace, m: platform.MouseEvent) bool {
    if (m.button != .left) return false;
    if (buttons[0].contains(m.pos.x, m.pos.y)) {
        copyReport(ws);
        return true;
    }
    if (buttons[1].contains(m.pos.x, m.pos.y)) {
        if (mem_stats.current()) |c| c.resetPeak();
        ws.setStatus("Memory: peak reset", .{});
        ws.dirty = true;
        return true;
    }
    return false;
}

fn copyReport(ws: *Workspace) void {
    const report = collect(ws);
    var aw: std.Io.Writer.Allocating = .init(ws.gpa);
    defer aw.deinit();
    writeReport(&aw.writer, &report) catch return;
    ws.env.clipboard.set(ws.env.clipboard.ctx, aw.written());
    ws.setStatus("Memory report copied", .{});
}

/// Keys while the view has the keyboard: Escape closes the panel.
pub fn viewKey(ws: *Workspace, k: platform.KeyEvent) bool {
    if (k.key != .named or k.key.named != .escape) return false;
    panel_ui.hide(ws);
    return true;
}

/// Show Memory: opens the panel on the view.
pub fn show(ws: *Workspace) void {
    panel_ui.showMemory(ws, true);
}

test "history keeps the newest samples in order" {
    history_count = 0;
    history_head = 0;
    for (0..history_len + 5) |i| push(.{ .resident = i });
    try std.testing.expectEqual(@as(usize, history_len), history_count);
    try std.testing.expectEqual(@as(u64, 5), at(0).resident);
    try std.testing.expectEqual(@as(u64, history_len + 4), at(history_len - 1).resident);
    history_count = 0;
}

test "byte sizes read like Activity Monitor's" {
    var buf: [32]u8 = undefined;
    try std.testing.expectEqualStrings("512 B", bytesText(&buf, 512));
    try std.testing.expectEqualStrings("1.5 KiB", bytesText(&buf, 1536));
    try std.testing.expectEqualStrings("100.0 MiB", bytesText(&buf, 100 << 20));
    try std.testing.expectEqualStrings("2.00 GiB", bytesText(&buf, 2 << 30));
}
