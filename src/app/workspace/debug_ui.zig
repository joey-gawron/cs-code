//! The debug view of the panel under the editors: Locals, Watch and the
//! Debug Console on the left, the Call Stack and the Breakpoints on the
//! right, laid out as in cs-code's debug windows (Name, Value and Type
//! columns, a yellow arrow on the stopped frame). Also what the debug
//! actions do (F5, F9, F10, F11 …) and the glue that opens the file a stop
//! reveals.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const panel_ui = @import("panel_ui.zig");
const platform = @import("../../framework/platform/platform.zig");
const ui = @import("../../framework/ui/ui.zig");
const keymap_mod = @import("../settings/keymap.zig");
const debugger_mod = @import("../debug/debugger.zig");
const dap = @import("../../model/dap/dap.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Debugger = debugger_mod.Debugger;
const Session = dap.session.Session;
const Rect = ui.Rect;
const Color = ui.Color;
const Action = keymap_mod.Action;

const row_h: f32 = 20;
const header_h: f32 = 22;
const tab_h: f32 = 26;

pub const watch_tag: u32 = 0xDB61;
pub const evaluate_tag: u32 = 0xDB62;

pub const Tab = enum { locals, watch, console, call_stack, breakpoints };

const Hit = union(enum) {
    tab: Tab,
    /// Expand or collapse a variable by its reference.
    toggle: i64,
    frame: usize,
    breakpoint: usize,
    breakpoint_enabled: usize,
    breakpoint_remove: usize,
    add_watch,
    remove_watch: usize,
    evaluate,
    clear_console,
    row,
};

pub const State = struct {
    debugger: ?*Debugger = null,
    left: Tab = .locals,
    right: Tab = .call_stack,
    scroll: [5]f32 = @splat(0),
    /// Variables shown open, by reference (valid for one stop).
    expanded: std.AutoHashMapUnmanaged(i64, void) = .empty,
    left_rect: Rect = .{},
    right_rect: Rect = .{},
    hits: std.ArrayList(struct { rect: Rect, hit: Hit }) = .empty,
    /// The console's line count at the last paint, to follow new output.
    console_seen: u64 = 0,
    /// Stop count at the last paint: expansion does not outlive a stop.
    epoch_seen: u32 = 0,
    /// The project whose launch configurations are loaded (by identity).
    configs_root: ?[*]const u8 = null,
};

pub fn deinit(ws: *Workspace) void {
    const st = &ws.debug;
    st.expanded.deinit(ws.gpa);
    st.hits.deinit(ws.gpa);
    if (st.debugger) |d| d.destroy();
    st.debugger = null;
}

/// The debugger, made on first use (it puts its hook in the editors' environment).
pub fn ensure(ws: *Workspace) ?*Debugger {
    const st = &ws.debug;
    if (st.debugger) |d| return d;
    const d = Debugger.create(ws.gpa, ws.io, ws.executor) catch return null;
    st.debugger = d;
    ws.env.debug = d.hook();
    return d;
}

fn projectRoot(ws: *Workspace) ?[]const u8 {
    if (ws.project) |p| return p.root;
    const ed = ws.activeEditor() orelse return null;
    const path = ed.buffer.path orelse return null;
    return std.fs.path.dirname(path);
}

// ---- The glue ------------------------------------------------------------------

/// Every loop turn: runs the debugger's timers, opens the file a stop
/// reveals, shows the panel when a run begins and asks for a repaint when
/// anything it shows changed.
pub fn tick(ws: *Workspace) void {
    const d = ensure(ws) orelse return;
    d.service();
    if (ws.activeEditor()) |ed| {
        if (ed.buffer.path) |p| d.setActiveFile(p);
    }
    if (d.show_panel) {
        d.show_panel = false;
        panel_ui.showDebug(ws);
    }
    if (d.takeReveal()) |r| {
        defer ws.gpa.free(r.path);
        reveal(ws, r.path, r.line);
    }
    if (ws.project) |p| if (ws.debug.configs_root != p.root.ptr and !d.isLive()) {
        ws.debug.configs_root = p.root.ptr;
        d.loadConfigs(p.root) catch {};
    };
    const epoch = d.session.epoch;
    if (epoch != ws.debug.epoch_seen) {
        ws.debug.epoch_seen = epoch;
        ws.debug.expanded.clearRetainingCapacity();
    }
    if (d.takeChanged()) ws.dirty = true;
}

fn reveal(ws: *Workspace, path: []const u8, line: u32) void {
    const ed = ws.openFile(path, false) catch |err| {
        ws.setStatus("Cannot show {s}: {t}", .{ path, err });
        return;
    };
    const row = @min(ed.buffer.lineCount() -| 1, line -| 1);
    ed.setSingleSelection(.caret(ed.buffer.lineStart(row)));
    ed.afterCommand(true);
    ws.dirty = true;
}

/// The debug actions. Returns false for any other action.
pub fn perform(ws: *Workspace, action: Action) bool {
    switch (action) {
        .debug_start, .debug_stop, .debug_restart, .debug_step_over, .debug_step_into, .debug_step_out, .debug_pause, .debug_toggle_breakpoint, .debug_remove_all_breakpoints, .debug_add_watch, .debug_evaluate, .debug_show_panel => {},
        else => return false,
    }
    const d = ensure(ws) orelse return true;
    const s = &d.session;
    switch (action) {
        .debug_start => {
            if (projectRoot(ws)) |root| d.startOrContinue(root) else ws.setStatus("Open a folder to debug.", .{});
        },
        .debug_stop => d.stop(),
        .debug_restart => if (projectRoot(ws)) |root| d.restart(root),
        .debug_step_over => s.stepOver(),
        .debug_step_into => s.stepInto(),
        .debug_step_out => s.stepOut(),
        .debug_pause => s.pause(),
        .debug_toggle_breakpoint => {
            const ed = ws.activeEditor() orelse return true;
            const path = ed.buffer.path orelse return true;
            const row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
            d.toggleBreakpoint(path, row);
        },
        .debug_remove_all_breakpoints => d.removeAllBreakpoints(),
        .debug_add_watch => ws.openPrompt("Add Watch:", watch_tag, ""),
        .debug_evaluate => ws.openPrompt("Evaluate:", evaluate_tag, ""),
        .debug_show_panel => panel_ui.showDebug(ws),
        else => unreachable,
    }
    if (d.takeChanged()) ws.dirty = true;
    return true;
}

/// The Add Watch and Evaluate prompts' answers. Returns false for other prompts.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (tag != watch_tag and tag != evaluate_tag) return false;
    const text = std.mem.trim(u8, input, " \t");
    if (text.len == 0) return true;
    const d = ensure(ws) orelse return true;
    if (tag == watch_tag) {
        d.session.addWatch(text) catch {};
        ws.debug.left = .watch;
    } else {
        d.session.evaluateRepl(text);
        ws.debug.left = .console;
    }
    panel_ui.showDebug(ws);
    ws.dirty = true;
    return true;
}

// ---- Painting --------------------------------------------------------------------

fn tabLabel(tab: Tab) []const u8 {
    return switch (tab) {
        .locals => "Locals",
        .watch => "Watch 1",
        .console => "Debug Console",
        .call_stack => "Call Stack",
        .breakpoints => "Breakpoints",
    };
}

fn addHit(ws: *Workspace, rect: Rect, hit: Hit) void {
    ws.debug.hits.append(ws.gpa, .{ .rect = rect, .hit = hit }) catch {};
}

const red = Color.hex(0xf48771);
const yellow = Color.hex(0xffee62);
const green = Color.hex(0x6cc667);

/// A line of a list pane.
const Row = struct {
    depth: u8 = 0,
    name: []const u8 = "",
    value: []const u8 = "",
    type_name: []const u8 = "",
    reference: i64 = 0,
    expanded: bool = false,
    /// Drawn in `kind`'s colour.
    kind: enum { normal, dim, err } = .normal,
    hit: Hit = .row,
    /// 1: a yellow arrow, 2: a green one (a picked caller frame).
    arrow: u8 = 0,
    /// A breakpoint's dot: 0 none, 1 enabled, 2 disabled, 3 unverified.
    dot: u8 = 0,
    /// Name and value share the width (a call stack's lines).
    wide_name: bool = false,
};

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const st = &ws.debug;
    st.hits.clearRetainingCapacity();
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.fill(body, theme.background);
    const d = ensure(ws) orelse return;
    const left_w = @round(body.w * 0.58);
    st.left_rect = .{ .x = body.x, .y = body.y, .w = left_w, .h = body.h };
    st.right_rect = .{ .x = body.x + left_w + 1, .y = body.y, .w = body.w - left_w - 1, .h = body.h };
    scene.fill(.{ .x = body.x + left_w, .y = body.y, .w = 1, .h = body.h }, theme.tab_separator);
    paintPane(ws, ctx, d, st.left_rect, &.{ .locals, .watch, .console }, &st.left);
    paintPane(ws, ctx, d, st.right_rect, &.{ .call_stack, .breakpoints }, &st.right);
}

fn paintPane(ws: *Workspace, ctx: *const ui.Ctx, d: *Debugger, rect: Rect, tabs: []const Tab, active: *Tab) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.pushClip(rect) catch return;
    defer scene.popClip();
    const strip: Rect = .{ .x = rect.x, .y = rect.y, .w = rect.w, .h = tab_h };
    scene.fill(strip, theme.window_background);
    var tx = rect.x + 10;
    for (tabs) |t| {
        const label = tabLabel(t);
        const w = ctx.text.measure(label, ws.fonts.ui_small);
        const hit: Rect = .{ .x = tx - 6, .y = rect.y, .w = w + 12, .h = tab_h };
        const is_active = active.* == t;
        const fg = if (is_active or ctx.hovered(hit)) theme.tab_active_foreground else theme.tab_inactive_foreground;
        _ = ctx.label(.{ .x = tx, .y = rect.y, .w = w, .h = tab_h }, label, ws.fonts.ui_small, fg);
        if (is_active) scene.fill(.{ .x = tx - 6, .y = rect.y + tab_h - 2, .w = w + 12, .h = 2 }, theme.input_border_focus);
        addHit(ws, hit, .{ .tab = t });
        tx += w + 22;
    }
    if (tabs[0] == .locals) {
        var buf: [96]u8 = undefined;
        const text = stateText(d, &buf);
        _ = ctx.labelRight(.{ .x = rect.x, .y = rect.y, .w = rect.w - 10, .h = tab_h }, text, ws.fonts.ui_small, stateColor(d));
    }
    const content: Rect = .{ .x = rect.x, .y = rect.y + tab_h, .w = rect.w, .h = @max(0, rect.h - tab_h) };
    paintTab(ws, ctx, d, content, active.*);
}

fn stateText(d: *Debugger, buf: []u8) []const u8 {
    const s = &d.session;
    return switch (s.state) {
        .idle => "Not debugging",
        .starting => "Starting…",
        .running => "Running",
        .stopped => std.fmt.bufPrint(buf, "Paused{s}{s}", .{ if (s.stop_reason.len > 0) " on " else "", s.stop_reason }) catch "Paused",
        .ended => if (s.exit_code) |c| std.fmt.bufPrint(buf, "Ended (exit code {d})", .{c}) catch "Ended" else "Ended",
    };
}

fn stateColor(d: *Debugger) Color {
    return switch (d.session.state) {
        .stopped => yellow,
        .running, .starting => green,
        else => Color.hex(0x9d9d9d),
    };
}

fn paintTab(ws: *Workspace, ctx: *const ui.Ctx, d: *Debugger, rect: Rect, tab: Tab) void {
    const st = &ws.debug;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const s = &d.session;
    var rows: std.ArrayList(Row) = .empty;
    defer rows.deinit(ws.gpa);
    // Text the rows point at lives until the pane is painted.
    var arena_state: std.heap.ArenaAllocator = .init(ws.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();
    var columns = true;
    var actions_h: f32 = 0;

    switch (tab) {
        .locals => buildLocals(ws, s, &rows),
        .watch => buildWatch(ws, s, &rows),
        .call_stack => {
            columns = false;
            buildStack(ws, arena, s, &rows);
        },
        .breakpoints => {
            columns = false;
            buildBreakpoints(ws, arena, d, &rows);
        },
        .console => {
            columns = false;
            actions_h = row_h + 6;
            buildConsole(ws, s, &rows);
        },
    }

    var top = rect.y;
    if (columns) {
        const hdr: Rect = .{ .x = rect.x, .y = top, .w = rect.w, .h = header_h };
        scene.fill(.{ .x = rect.x, .y = hdr.bottom() - 1, .w = rect.w, .h = 1 }, theme.tab_separator);
        const cols = columnsFor(rect);
        const dim = theme.list_foreground_dim;
        _ = ctx.label(.{ .x = cols[0] + 6, .y = hdr.y, .w = cols[1] - cols[0], .h = hdr.h }, "Name", ws.fonts.ui_small, dim);
        _ = ctx.label(.{ .x = cols[1] + 6, .y = hdr.y, .w = cols[2] - cols[1], .h = hdr.h }, "Value", ws.fonts.ui_small, dim);
        _ = ctx.label(.{ .x = cols[2] + 6, .y = hdr.y, .w = rect.right() - cols[2], .h = hdr.h }, "Type", ws.fonts.ui_small, dim);
        scene.fill(.{ .x = cols[1], .y = hdr.y, .w = 1, .h = hdr.h }, theme.tab_separator);
        scene.fill(.{ .x = cols[2], .y = hdr.y, .w = 1, .h = hdr.h }, theme.tab_separator);
        top += header_h;
    }
    if (tab == .console) {
        // The console's own actions, above its lines.
        const bar: Rect = .{ .x = rect.x, .y = rect.y, .w = rect.w, .h = actions_h };
        scene.fill(.{ .x = rect.x, .y = bar.bottom() - 1, .w = rect.w, .h = 1 }, theme.tab_separator);
        const eval_r: Rect = .{ .x = rect.x + 8, .y = bar.y + 3, .w = @min(rect.w - 90, 220), .h = row_h };
        scene.quad(eval_r, .{ .color = theme.input_background, .border_color = if (ctx.hovered(eval_r)) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 3 });
        _ = ctx.labelFit(.{ .x = eval_r.x + 8, .y = eval_r.y, .w = eval_r.w - 16, .h = eval_r.h }, "Evaluate expression…", ws.fonts.ui_small, theme.list_foreground_dim);
        addHit(ws, eval_r, .evaluate);
        const clear_r: Rect = .{ .x = eval_r.right() + 8, .y = bar.y + 3, .w = 52, .h = row_h };
        if (ctx.hovered(clear_r)) scene.quad(clear_r, .{ .color = theme.tab_hover, .radius = 4 });
        ctx.labelCentered(clear_r, "Clear", ws.fonts.ui_small, theme.tab_inactive_foreground);
        addHit(ws, clear_r, .clear_console);
        top += actions_h;
    }

    const view: Rect = .{ .x = rect.x, .y = top, .w = rect.w, .h = @max(0, rect.bottom() - top) };
    if (view.h <= 0) return;
    scene.pushClip(view) catch return;
    defer scene.popClip();

    const idx = @intFromEnum(tab);
    const total_h = @as(f32, @floatFromInt(rows.items.len)) * row_h;
    const max_scroll = @max(0, total_h - view.h);
    if (tab == .console and s.output_seq != st.console_seen) {
        st.console_seen = s.output_seq;
        st.scroll[idx] = max_scroll;
    }
    st.scroll[idx] = std.math.clamp(st.scroll[idx], 0, max_scroll);
    const first: usize = @intFromFloat(@floor(st.scroll[idx] / row_h));
    var i = first;
    var y = view.y + @as(f32, @floatFromInt(first)) * row_h - st.scroll[idx];
    const cols = columnsFor(rect);
    while (i < rows.items.len and y < view.bottom()) : (i += 1) {
        const r = rows.items[i];
        const rr: Rect = .{ .x = view.x, .y = y, .w = view.w, .h = row_h };
        if (ctx.hovered(rr)) scene.fill(rr, theme.sidebar_hover);
        addHit(ws, rr, r.hit);
        paintRow(ws, ctx, rr, r, columns, cols);
        y += row_h;
    }
    if (rows.items.len == 0) {
        _ = ctx.labelFit(.{ .x = view.x + 12, .y = view.y + 4, .w = view.w - 24, .h = row_h }, emptyText(tab, s), ws.fonts.ui, theme.list_foreground_dim);
    }
}

fn emptyText(tab: Tab, s: *const Session) []const u8 {
    if (tab == .breakpoints) return "No breakpoints. F9 or a click in the gutter sets one.";
    return switch (s.state) {
        .idle, .ended => "Not debugging. F5 starts the selected configuration.",
        .starting, .running => "The program is running.",
        .stopped => "Nothing to show.",
    };
}

fn columnsFor(rect: Rect) [3]f32 {
    return .{ rect.x, rect.x + @round(rect.w * 0.28), rect.x + @round(rect.w * 0.78) };
}

fn paintRow(ws: *Workspace, ctx: *const ui.Ctx, rr: Rect, r: Row, columns: bool, cols: [3]f32) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    const fg = switch (r.kind) {
        .normal => theme.list_foreground,
        .dim => theme.list_foreground_dim,
        .err => red,
    };
    var x = rr.x + 8 + @as(f32, @floatFromInt(r.depth)) * 16;
    if (r.arrow != 0) {
        const color = if (r.arrow == 1) yellow else green;
        const cy = rr.y + rr.h / 2;
        scene.fill(.{ .x = rr.x + 4, .y = cy - 2, .w = 5, .h = 4 }, color);
        for (0..6) |k| {
            const t: f32 = @floatFromInt(k);
            const half = 5.5 - t;
            scene.fill(.{ .x = rr.x + 9 + t, .y = cy - half, .w = 1, .h = half * 2 }, color);
        }
        x = rr.x + 22;
    }
    if (r.dot != 0) {
        const cy = rr.y + rr.h / 2;
        const box: Rect = .{ .x = rr.x + 8, .y = cy - 6, .w = 12, .h = 12 };
        scene.quad(box, .{ .border_color = theme.list_foreground_dim, .border_width = 1, .radius = 2 });
        if (r.dot == 1 or r.dot == 3) scene.fill(.{ .x = box.x + 3, .y = box.y + 3, .w = 6, .h = 6 }, theme.list_foreground);
        const dot_color = switch (r.dot) {
            2 => Color.hex(0x848484),
            else => Color.hex(0xe51400),
        };
        ctx.dot(rr.x + 32, cy, 5, dot_color);
        x = rr.x + 44;
    }
    if (columns) {
        if (r.reference != 0 or r.expanded) ctx.chevron(x + 4, rr.y + rr.h / 2, r.expanded, theme.list_foreground_dim);
        const name_x = x + 14;
        _ = ctx.labelFit(.{ .x = name_x, .y = rr.y, .w = @max(0, cols[1] - name_x - 6), .h = rr.h }, r.name, ws.fonts.ui, fg);
        _ = ctx.labelFit(.{ .x = cols[1] + 6, .y = rr.y, .w = cols[2] - cols[1] - 12, .h = rr.h }, r.value, ws.fonts.ui, if (r.kind == .err) red else theme.list_foreground);
        _ = ctx.labelFit(.{ .x = cols[2] + 6, .y = rr.y, .w = @max(0, rr.right() - cols[2] - 10), .h = rr.h }, r.type_name, ws.fonts.ui, theme.list_foreground_dim);
    } else if (r.wide_name) {
        const w = ctx.labelFit(.{ .x = x, .y = rr.y, .w = @max(0, rr.w * 0.5 - x + rr.x), .h = rr.h }, r.name, ws.fonts.ui, fg);
        _ = ctx.labelFit(.{ .x = x + w + 14, .y = rr.y, .w = @max(0, rr.right() - x - w - 24), .h = rr.h }, r.value, ws.fonts.ui, theme.list_foreground_dim);
    } else {
        _ = ctx.labelFit(.{ .x = x, .y = rr.y, .w = @max(0, rr.right() - x - 8), .h = rr.h }, r.name, ws.fonts.mono, fg);
    }
}

// ---- Rows ----------------------------------------------------------------------

fn addVariables(ws: *Workspace, s: *const Session, rows: *std.ArrayList(Row), reference: i64, depth: u8) void {
    const children = s.childrenOf(reference) orelse return;
    switch (children) {
        .loading => rows.append(ws.gpa, .{ .depth = depth, .name = "Loading…", .kind = .dim }) catch {},
        .failed => |msg| rows.append(ws.gpa, .{ .depth = depth, .name = msg, .kind = .err }) catch {},
        .items => |items| for (items) |v| {
            const open = v.reference != 0 and ws.debug.expanded.contains(v.reference);
            rows.append(ws.gpa, .{
                .depth = depth,
                .name = v.name,
                .value = v.value,
                .type_name = v.type_name,
                .reference = v.reference,
                .expanded = open,
                .hit = if (v.reference != 0) .{ .toggle = v.reference } else .row,
            }) catch return;
            if (open) addVariables(ws, s, rows, v.reference, depth + 1);
        },
    }
}

fn buildLocals(ws: *Workspace, s: *const Session, rows: *std.ArrayList(Row)) void {
    if (s.state != .stopped or s.scopes.len == 0) return;
    for (s.scopes) |scope| {
        if (std.ascii.findIgnoreCase(scope.name, "local") != null or std.ascii.findIgnoreCase(scope.name, "argument") != null) {
            return addVariables(ws, s, rows, scope.reference, 0);
        }
    }
    addVariables(ws, s, rows, s.scopes[0].reference, 0);
}

fn buildWatch(ws: *Workspace, s: *const Session, rows: *std.ArrayList(Row)) void {
    for (s.watches.items, 0..) |w, i| {
        const open = w.reference != 0 and ws.debug.expanded.contains(w.reference);
        rows.append(ws.gpa, .{
            .name = w.expr,
            .value = w.value orelse if (s.state == .stopped) "…" else "",
            .type_name = w.type_name orelse "",
            .reference = w.reference,
            .expanded = open,
            .kind = if (w.is_error) .err else .normal,
            .hit = if (w.reference != 0) .{ .toggle = w.reference } else .{ .remove_watch = i },
        }) catch return;
        if (open) addVariables(ws, s, rows, w.reference, 1);
    }
    rows.append(ws.gpa, .{ .name = "Add item to watch", .kind = .dim, .hit = .add_watch }) catch {};
}

fn buildStack(ws: *Workspace, arena: Allocator, s: *const Session, rows: *std.ArrayList(Row)) void {
    if (s.state != .stopped) return;
    for (s.frames, 0..) |f, i| {
        const loc = if (f.path) |p|
            std.fmt.allocPrint(arena, "{s}:{d}", .{ std.fs.path.basename(p), f.line }) catch ""
        else
            "";
        rows.append(ws.gpa, .{
            .name = f.name,
            .value = loc,
            .arrow = if (i == s.frame_index) (if (i == 0) 1 else 2) else 0,
            .kind = if (f.path == null) .dim else .normal,
            .hit = .{ .frame = i },
            .wide_name = true,
        }) catch return;
    }
}

fn buildBreakpoints(ws: *Workspace, arena: Allocator, d: *Debugger, rows: *std.ArrayList(Row)) void {
    var n: usize = 0;
    for (d.store.files.keys(), d.store.files.values()) |path, list| {
        for (list.items) |bp| {
            const loc = std.fmt.allocPrint(arena, "line {d}", .{bp.line}) catch "";
            const unverified = d.session.isLive() and d.session.state != .starting and !bp.verified and bp.enabled;
            rows.append(ws.gpa, .{
                .name = std.fs.path.basename(path),
                .value = loc,
                .dot = if (!bp.enabled) 2 else if (unverified) 3 else 1,
                .hit = .{ .breakpoint = n },
                .wide_name = true,
            }) catch return;
            n += 1;
        }
    }
}

fn buildConsole(ws: *Workspace, s: *const Session, rows: *std.ArrayList(Row)) void {
    for (s.output.items) |line| {
        rows.append(ws.gpa, .{
            .name = line.text,
            .kind = switch (line.category) {
                .stderr, .important => .err,
                .console => .dim,
                else => .normal,
            },
        }) catch return;
    }
}

// ---- Mouse -----------------------------------------------------------------------

/// A click in the debug view. Returns true if it was on one of its parts.
pub fn onMouseDown(ws: *Workspace, m: platform.MouseEvent) bool {
    const st = &ws.debug;
    const d = st.debugger orelse return false;
    if (!st.left_rect.contains(m.pos.x, m.pos.y) and !st.right_rect.contains(m.pos.x, m.pos.y)) return false;
    if (m.button != .left) return true;
    // Later hits (rows) lie under earlier ones (tabs): the last match is the topmost.
    var found: ?Hit = null;
    for (st.hits.items) |h| if (h.rect.contains(m.pos.x, m.pos.y)) {
        found = h.hit;
    };
    const hit = found orelse return true;
    const s = &d.session;
    switch (hit) {
        .tab => |t| switch (t) {
            .locals, .watch, .console => st.left = t,
            .call_stack, .breakpoints => st.right = t,
        },
        .toggle => |ref| {
            if (st.expanded.remove(ref)) {} else {
                st.expanded.put(ws.gpa, ref, {}) catch {};
                s.expand(ref);
            }
        },
        .frame => |i| s.selectFrame(i),
        .breakpoint => |i| openBreakpoint(ws, d, i, m.click_count >= 2),
        .breakpoint_enabled, .breakpoint_remove, .row => {},
        .add_watch => ws.openPrompt("Add Watch:", watch_tag, ""),
        .remove_watch => |i| if (m.click_count >= 2) s.removeWatch(i),
        .evaluate => ws.openPrompt("Evaluate:", evaluate_tag, ""),
        .clear_console => s.clearOutput(),
    }
    ws.dirty = true;
    return true;
}

fn nthBreakpoint(d: *Debugger, n: usize) ?struct { path: []const u8, line: u32 } {
    var i: usize = 0;
    for (d.store.files.keys(), d.store.files.values()) |path, list| {
        for (list.items) |bp| {
            if (i == n) return .{ .path = path, .line = bp.line };
            i += 1;
        }
    }
    return null;
}

fn openBreakpoint(ws: *Workspace, d: *Debugger, n: usize, remove: bool) void {
    const bp = nthBreakpoint(d, n) orelse return;
    if (remove) return d.removeBreakpoint(bp.path, bp.line);
    reveal(ws, bp.path, bp.line);
}

pub fn onScroll(ws: *Workspace, s: platform.ScrollEvent) bool {
    const st = &ws.debug;
    const tab: Tab = if (st.left_rect.contains(s.pos.x, s.pos.y)) st.left else if (st.right_rect.contains(s.pos.x, s.pos.y)) st.right else return false;
    const idx = @intFromEnum(tab);
    const px: f32 = if (s.unit == .lines) -s.delta.y * row_h * 3 else -s.delta.y;
    st.scroll[idx] = @max(0, st.scroll[idx] + px);
    ws.dirty = true;
    return true;
}

pub fn cursorShape(ws: *Workspace, pos: platform.Vec2) ?platform.CursorShape {
    const st = &ws.debug;
    for (st.hits.items) |h| if (h.rect.contains(pos.x, pos.y)) return switch (h.hit) {
        .row => null,
        else => .pointer,
    };
    return null;
}
