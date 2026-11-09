//! The Git Graph tab: the repository's commits newest first with the lanes
//! of branches and merges drawn beside them, the branches and tags that
//! point at a commit as chips, and below the list the files the selected
//! commit changed; click one to see that change in the diff editor.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const history = @import("../../model/git/history.zig");
const blame_mod = @import("../../model/git/blame.zig");
const git = @import("../../model/git/git.zig");
const git_ui = @import("git_ui.zig");
const scm_state = @import("scm_state.zig");
const diff_view = @import("diff_view.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const ui = @import("../../framework/ui/ui.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;

const toolbar_h: f32 = 30;
const row_h: f32 = 24;
const file_h: f32 = 22;
const lane_w: f32 = 14;
const max_commits = "400";

const lane_colors = [_]u24{ 0x3794ff, 0xe5a000, 0x89d185, 0xb180d7, 0xf14c4c, 0x2bc7b4, 0xd7ba7d };

const What = union(enum) { refresh, row: usize, file: usize };
const Hit = struct { rect: Rect, what: What };

pub const View = struct {
    gpa: Allocator,
    log: history.Log,
    graph: history.Graph,
    selected: usize = 0,
    scroll_y: f32 = 0,
    files_scroll: f32 = 0,
    /// The files of the selected commit (a read in flight, or its answer).
    files: []history.ChangedFile = &.{},
    files_arena: std.heap.ArenaAllocator,
    files_for: ?[]u8 = null,
    files_job: ?*git.Job = null,
    rect: Rect = .{},
    list: Rect = .{},
    details: Rect = .{},
    hits: std.ArrayList(Hit) = .empty,
    ws: *Workspace,

    pub fn destroy(self: *View) void {
        if (self.files_job) |j| j.detach();
        self.log.deinit();
        self.graph.deinit();
        self.files_arena.deinit();
        if (self.files_for) |h| self.gpa.free(h);
        self.hits.deinit(self.gpa);
        self.gpa.destroy(self);
    }
};

// ---- Opening ---------------------------------------------------------------------------

pub fn open(ws: *Workspace) void {
    const st = scm_state.state(ws);
    const root = st.root orelse return ws.setStatus("Git: this folder isn't a git repository", .{});
    if (st.graph_job != null) return;
    st.graph_job = git.Job.start(git_ui.runner(ws), &.{ "log", "--all", "--date-order", "-n", max_commits, "-z", history.log_format }, root, null, null, ws, onLog) catch null;
}

pub fn cancelOpening(ws: *Workspace) void {
    const st = scm_state.state(ws);
    if (st.graph_job) |j| j.detach();
    st.graph_job = null;
}

fn onLog(ctx: *anyopaque, job: *git.Job) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    const st = scm_state.state(ws);
    st.graph_job = null;
    const out = job.output orelse return ws.setStatus("Git: unable to read the log", .{});
    const gpa = ws.gpa;
    var log = history.parse(gpa, out) catch return;
    const graph = history.layout(gpa, log.commits) catch {
        log.deinit();
        return;
    };
    const view = gpa.create(View) catch {
        log.deinit();
        var g = graph;
        g.deinit();
        return;
    };
    view.* = .{ .gpa = gpa, .log = log, .graph = graph, .files_arena = .init(gpa), .ws = ws };
    show(ws, view);
    selectRow(ws, view, 0);
}

fn show(ws: *Workspace, view: *View) void {
    for (ws.tabs.items, 0..) |t, i| if (t.graph) |old| {
        ws.tabs.items[i].graph = view;
        old.destroy();
        ws.activateTab(i);
        ws.focus = .editor;
        return;
    };
    const buffer = ws.gpa.create(Buffer) catch return view.destroy();
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    ws.next_buffer_id += 1;
    buffer.display_name = "Git Graph";
    buffer.read_only = true;
    ws.buffers.append(ws.gpa, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch return view.destroy();
    _ = ws.insertTab(.{ .editor = ed, .graph = view }) catch {
        ed.destroy();
        return view.destroy();
    };
    ws.focus = .editor;
    ws.updateTitle();
}

// ---- The selected commit's files ---------------------------------------------------------

fn selectRow(ws: *Workspace, v: *View, row: usize) void {
    if (v.log.commits.len == 0) return;
    v.selected = @min(row, v.log.commits.len - 1);
    const c = v.log.commits[v.selected];
    if (v.files_for) |h| if (std.mem.eql(u8, h, c.hash)) return;
    if (v.files_job) |j| j.detach();
    v.files_job = null;
    v.files = &.{};
    v.files_scroll = 0;
    _ = v.files_arena.reset(.retain_capacity);
    if (v.files_for) |h| v.gpa.free(h);
    v.files_for = v.gpa.dupe(u8, c.hash) catch null;
    const root = scm_state.state(ws).root orelse return;
    // A root commit has no parent to compare with: --root lists its files.
    v.files_job = git.Job.start(git_ui.runner(ws), &.{ "show", "--name-status", "--format=", "-z", "-M", "--root", c.hash }, root, null, null, v, onFiles) catch null;
}

fn onFiles(ctx: *anyopaque, job: *git.Job) void {
    const v: *View = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    v.files_job = null;
    const out = job.output orelse return;
    v.files = history.parseNameStatus(v.files_arena.allocator(), out) catch &.{};
    v.ws.dirty = true;
}

// ---- Painting --------------------------------------------------------------------------

fn laneX(x0: f32, lane: usize) f32 {
    return x0 + @as(f32, @floatFromInt(lane)) * lane_w + lane_w / 2;
}

fn laneColor(lane: usize) Color {
    return Color.hex(lane_colors[lane % lane_colors.len]);
}

fn vline(scene: *scene_mod.Scene, x: f32, y0: f32, y1: f32, c: Color) void {
    scene.fill(.{ .x = x - 0.75, .y = y0, .w = 1.5, .h = y1 - y0 }, c);
}

fn hline(scene: *scene_mod.Scene, x0: f32, x1: f32, y: f32, c: Color) void {
    scene.fill(.{ .x = @min(x0, x1), .y = y - 0.75, .w = @abs(x1 - x0), .h = 1.5 }, c);
}

fn paintGraphRow(scene: *scene_mod.Scene, row: history.GraphRow, x0: f32, y: f32, selected: bool, bg: Color) void {
    const ymid = y + row_h / 2;
    const nx = laneX(x0, row.lane);
    for (row.cells, 0..) |cell, j| {
        const cx = laneX(x0, j);
        const col = laneColor(j);
        if (j == row.lane) {
            if (cell.above) vline(scene, cx, y, ymid, col);
            if (cell.below) vline(scene, cx, ymid, y + row_h, col);
            continue;
        }
        if (cell.merge_in) {
            vline(scene, cx, y, ymid, col);
            hline(scene, cx, nx, ymid, col);
        } else if (cell.out) {
            // The lane may already carry a line from above.
            if (cell.above) vline(scene, cx, y, ymid, col);
            hline(scene, nx, cx, ymid, col);
            vline(scene, cx, ymid, y + row_h, col);
        } else if (cell.above and cell.below) {
            vline(scene, cx, y, y + row_h, col);
        }
    }
    const col = laneColor(row.lane);
    scene.quad(.{ .x = nx - 5, .y = ymid - 5, .w = 10, .h = 10 }, .{ .color = if (selected) bg else col, .border_color = col, .border_width = 2, .radius = 5 });
}

/// The branch and tag chips for `refs` ("HEAD -> main, origin/main, tag: v1").
fn paintRefs(ctx: *const ui.Ctx, ws: *Workspace, refs: []const u8, x_in: f32, y: f32, limit: f32) f32 {
    var x = x_in;
    var it = std.mem.splitSequence(u8, refs, ", ");
    while (it.next()) |raw| {
        if (raw.len == 0) continue;
        var name = raw;
        var color = Color.hex(0x3794ff);
        if (std.mem.startsWith(u8, name, "HEAD -> ")) {
            name = name["HEAD -> ".len..];
            color = Color.hex(0x89d185);
        } else if (std.mem.startsWith(u8, name, "tag: ")) {
            name = name[5..];
            color = Color.hex(0xe5a000);
        } else if (std.mem.indexOfScalar(u8, name, '/') != null) {
            color = Color.hex(0xb180d7);
        } else if (std.mem.eql(u8, name, "HEAD")) {
            color = Color.hex(0x89d185);
        }
        const w = ctx.text.measure(name, ws.fonts.ui_small) + 12;
        if (x + w > limit) break;
        const chip: Rect = .{ .x = x, .y = y + 4, .w = w, .h = row_h - 8 };
        ctx.scene.quad(chip, .{ .color = color.withAlpha(0.2), .border_color = color, .border_width = 1, .radius = 3 });
        ctx.labelCentered(chip, name, ws.fonts.ui_small, color);
        x += w + 5;
    }
    return x;
}

pub fn paint(ws: *Workspace, ed: *Editor, rect: Rect, scene: *scene_mod.Scene) bool {
    const i = ws.tabIndexOf(ed) orelse return false;
    const v = ws.tabs.items[i].graph orelse return false;
    const theme = ws.theme;
    const ctx: ui.Ctx = .{ .scene = scene, .text = ws.env.text, .theme = theme, .fonts = ws.fonts, .mouse = ws.mouse };
    v.rect = rect;
    v.hits.clearRetainingCapacity();
    scene.fill(rect, theme.background);
    scene.pushClip(rect) catch return true;
    defer scene.popClip();

    const bar: Rect = .{ .x = rect.x, .y = rect.y, .w = rect.w, .h = toolbar_h };
    scene.fill(bar, theme.tabbar_background);
    scene.fill(.{ .x = bar.x, .y = bar.bottom() - 1, .w = bar.w, .h = 1 }, theme.tab_separator);
    {
        var buf: [96]u8 = undefined;
        const title = std.fmt.bufPrint(&buf, "Git Graph  \u{2014}  {d} commits", .{v.log.commits.len}) catch "Git Graph";
        _ = ctx.label(.{ .x = bar.x + 12, .y = bar.y, .w = 400, .h = bar.h }, title, ws.fonts.ui, theme.sidebar_foreground);
        const w = ctx.text.measure("Refresh", ws.fonts.ui_small) + 18;
        const r: Rect = .{ .x = bar.right() - w - 10, .y = bar.y + 4, .w = w, .h = toolbar_h - 8 };
        if (ctx.hovered(r)) scene.quad(r, .{ .color = theme.sidebar_hover, .radius = 3 });
        ctx.labelCentered(r, "Refresh", ws.fonts.ui_small, theme.sidebar_foreground);
        v.hits.append(v.gpa, .{ .rect = r, .what = .refresh }) catch {};
    }

    const body: Rect = .{ .x = rect.x, .y = bar.bottom(), .w = rect.w, .h = @max(0, rect.h - toolbar_h) };
    const details_h = @min(220, body.h * 0.34);
    v.list = .{ .x = body.x, .y = body.y, .w = body.w, .h = body.h - details_h };
    v.details = .{ .x = body.x, .y = body.y + body.h - details_h, .w = body.w, .h = details_h };

    // The commits.
    {
        scene.pushClip(v.list) catch return true;
        defer scene.popClip();
        const content = @as(f32, @floatFromInt(v.log.commits.len)) * row_h;
        v.scroll_y = std.math.clamp(v.scroll_y, 0, @max(0, content - v.list.h));
        const first: usize = @intFromFloat(@floor(v.scroll_y / row_h));
        const last = @min(v.log.commits.len, first + @as(usize, @intFromFloat(@ceil(v.list.h / row_h))) + 1);
        const graph_w = @as(f32, @floatFromInt(v.graph.width)) * lane_w + 10;
        const hash_w: f32 = 62;
        const date_w: f32 = 110;
        const author_w: f32 = 130;
        const right_cols = hash_w + date_w + author_w + 16;
        const now = blame_mod.unixNow();
        var ri = first;
        while (ri < last) : (ri += 1) {
            const c = v.log.commits[ri];
            const y = v.list.y + @as(f32, @floatFromInt(ri)) * row_h - v.scroll_y;
            const r: Rect = .{ .x = v.list.x, .y = y, .w = v.list.w, .h = row_h };
            const sel = ri == v.selected;
            if (sel) scene.fill(r, theme.sidebar_selected) else if (ctx.hovered(r) and v.list.contains(ctx.mouse.x, ctx.mouse.y)) scene.fill(r, theme.sidebar_hover);
            v.hits.append(v.gpa, .{ .rect = r, .what = .{ .row = ri } }) catch {};
            paintGraphRow(scene, v.graph.rows[ri], v.list.x + 8, y, sel, if (sel) theme.sidebar_selected else theme.background);
            var x = v.list.x + 8 + graph_w;
            const limit = v.list.right() - right_cols;
            x = paintRefs(&ctx, ws, c.refs, x, y, limit - 80);
            _ = ctx.labelFit(.{ .x = x, .y = y, .w = @max(40, limit - x), .h = row_h }, c.subject, ws.fonts.ui, theme.sidebar_foreground);
            var dbuf: [48]u8 = undefined;
            const age = blame_mod.formatRelative(&dbuf, now, c.time);
            const cx = v.list.right() - right_cols + 8;
            _ = ctx.labelFit(.{ .x = cx, .y = y, .w = author_w - 6, .h = row_h }, c.author, ws.fonts.ui_small, theme.sidebar_foreground_dim);
            _ = ctx.labelFit(.{ .x = cx + author_w, .y = y, .w = date_w - 6, .h = row_h }, age, ws.fonts.ui_small, theme.sidebar_foreground_dim);
            _ = ctx.label(.{ .x = cx + author_w + date_w, .y = y, .w = hash_w, .h = row_h }, c.short(), ws.env.font, theme.sidebar_foreground_dim);
        }
    }

    // The selected commit's files.
    {
        scene.fill(.{ .x = v.details.x, .y = v.details.y, .w = v.details.w, .h = 1 }, theme.tab_separator);
        scene.pushClip(v.details) catch return true;
        defer scene.popClip();
        if (v.selected < v.log.commits.len) {
            const c = v.log.commits[v.selected];
            var hb: [512]u8 = undefined;
            const head = std.fmt.bufPrint(&hb, "{s}  {s}", .{ c.short(), c.subject }) catch c.subject;
            scene.fill(.{ .x = v.details.x, .y = v.details.y + 1, .w = v.details.w, .h = 26 }, theme.tabbar_background);
            _ = ctx.labelFit(.{ .x = v.details.x + 12, .y = v.details.y + 1, .w = v.details.w - 24, .h = 26 }, head, ws.fonts.ui_bold, theme.sidebar_foreground);
            var y = v.details.y + 28 - v.files_scroll;
            if (v.files.len == 0) {
                const text = if (v.files_job != null) "Reading the changes\u{2026}" else "No files changed.";
                _ = ctx.label(.{ .x = v.details.x + 12, .y = y, .w = 300, .h = file_h }, text, ws.fonts.ui, theme.sidebar_foreground_dim);
            }
            for (v.files, 0..) |f, fi| {
                defer y += file_h;
                if (y + file_h < v.details.y or y > v.details.bottom()) continue;
                const r: Rect = .{ .x = v.details.x, .y = y, .w = v.details.w, .h = file_h };
                if (ctx.hovered(r) and v.details.contains(ctx.mouse.x, ctx.mouse.y)) scene.fill(r, theme.sidebar_hover);
                v.hits.append(v.gpa, .{ .rect = r, .what = .{ .file = fi } }) catch {};
                const kind: scm_state.Kind = switch (f.status) {
                    'A' => .added,
                    'D' => .deleted,
                    'R', 'C' => .renamed,
                    else => .modified,
                };
                const col = scm_state.kindColor(theme, kind);
                const letter = [_]u8{f.status};
                _ = ctx.label(.{ .x = v.details.x + 14, .y = y, .w = 16, .h = file_h }, &letter, ws.fonts.ui, col);
                const base = std.fs.path.basename(f.path);
                const dir = std.fs.path.dirname(f.path) orelse "";
                const bw = ctx.label(.{ .x = v.details.x + 34, .y = y, .w = v.details.w - 50, .h = file_h }, base, ws.fonts.ui, col);
                if (dir.len > 0) _ = ctx.labelFit(.{ .x = v.details.x + 34 + bw + 8, .y = y, .w = @max(0, v.details.w - 60 - bw), .h = file_h }, dir, ws.fonts.ui_small, theme.sidebar_foreground_dim);
            }
        }
    }
    return true;
}

// ---- Input -----------------------------------------------------------------------------

fn active(ws: *Workspace) ?*View {
    const i = ws.activeIndex() orelse return null;
    return ws.tabs.items[i].graph;
}

fn viewAt(ws: *Workspace, pos: platform.Vec2) ?*View {
    for (ws.tabs.items) |t| if (t.graph) |v| {
        if (v.rect.contains(pos.x, pos.y) and ws.group_active[t.group] == t.editor) return v;
    };
    return null;
}

pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    switch (ev) {
        .key_down => |k| {
            if (ws.menuIsOpen() or ws.focus != .editor or ws.overlay != null) return false;
            const v = active(ws) orelse return false;
            if (k.mods.ctrl or k.mods.alt or k.mods.super) return false;
            switch (k.key) {
                .named => |n| switch (n) {
                    .up => selectRow(ws, v, v.selected -| 1),
                    .down => selectRow(ws, v, v.selected + 1),
                    .page_up => selectRow(ws, v, v.selected -| 10),
                    .page_down => selectRow(ws, v, v.selected + 10),
                    .home => selectRow(ws, v, 0),
                    .end => selectRow(ws, v, v.log.commits.len),
                    else => return false,
                },
                .char => return false,
            }
            keepVisible(v);
            return true;
        },
        .mouse_down => |m| {
            if (ws.menuIsOpen() or ws.overlay != null) return false;
            const v = viewAt(ws, m.pos) orelse return false;
            ws.focus = .editor;
            if (m.button != .left) return true;
            var i = v.hits.items.len;
            while (i > 0) {
                i -= 1;
                const h = v.hits.items[i];
                if (!h.rect.contains(m.pos.x, m.pos.y)) continue;
                switch (h.what) {
                    .refresh => open(ws),
                    .row => |r| if (v.list.contains(m.pos.x, m.pos.y)) selectRow(ws, v, r),
                    .file => |f| if (v.details.contains(m.pos.x, m.pos.y)) openFile(ws, v, f),
                }
                break;
            }
            return true;
        },
        .scroll => |s| {
            if (ws.overlay != null) return false;
            const v = viewAt(ws, s.pos) orelse return false;
            const dy = if (s.unit == .lines) s.delta.y * row_h * 3 else s.delta.y;
            if (v.details.contains(s.pos.x, s.pos.y)) {
                const content = @as(f32, @floatFromInt(v.files.len)) * file_h;
                v.files_scroll = std.math.clamp(v.files_scroll + dy, 0, @max(0, content - v.details.h + 30));
            } else v.scroll_y = @max(0, v.scroll_y + dy);
            return true;
        },
        else => return false,
    }
}

fn keepVisible(v: *View) void {
    const y = @as(f32, @floatFromInt(v.selected)) * row_h;
    if (y < v.scroll_y) v.scroll_y = y else if (y + row_h > v.scroll_y + v.list.h) v.scroll_y = y + row_h - v.list.h;
}

/// The change one commit made to a file, in the diff editor.
pub fn openFile(ws: *Workspace, v: *View, index: usize) void {
    if (index >= v.files.len or v.selected >= v.log.commits.len) return;
    const f = v.files[index];
    const c = v.log.commits[v.selected];
    openCommitFile(ws, c.hash, c.short(), c.parents, f);
}

pub fn openCommitFile(ws: *Workspace, hash: []const u8, short: []const u8, parents: []const []const u8, f: history.ChangedFile) void {
    var after: [std.fs.max_path_bytes + 48]u8 = undefined;
    var before: [std.fs.max_path_bytes + 48]u8 = undefined;
    var title: [160]u8 = undefined;
    var ll: [std.fs.max_path_bytes + 32]u8 = undefined;
    var rl: [std.fs.max_path_bytes + 32]u8 = undefined;
    const right_spec = std.fmt.bufPrint(&after, "{s}:{s}", .{ hash, f.path }) catch return;
    const old_path = f.old_path orelse f.path;
    const have_parent = parents.len > 0;
    const left_spec = if (have_parent) (std.fmt.bufPrint(&before, "{s}^:{s}", .{ hash, old_path }) catch return) else "";
    const base = std.fs.path.basename(f.path);
    diff_view.open(ws, .{
        .title = std.fmt.bufPrint(&title, "{s} ({s})", .{ base, short }) catch base,
        .path = f.path,
        .left_label = std.fmt.bufPrint(&ll, "{s} ({s}^)", .{ old_path, short }) catch old_path,
        .right_label = std.fmt.bufPrint(&rl, "{s} ({s})", .{ f.path, short }) catch f.path,
        .left = if (!have_parent or f.status == 'A') .empty else .{ .git = left_spec },
        .right = if (f.status == 'D') .empty else .{ .git = right_spec },
        .preview = true,
    });
}
