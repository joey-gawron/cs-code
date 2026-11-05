//! The diff editor: two versions of a file compared side by side (aligned
//! rows, filler where one side has no line) or inline (removed lines, then
//! added ones), changed lines tinted and the changed part of a modified line
//! marked. It opens as a tab from the Source Control window and the
//! Timeline; both sides are read first (git objects, or the file as it is on
//! disk or in its open buffer). F7 and Shift+F7 move between changes.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const linediff = @import("../../model/text/linediff.zig");
const git = @import("../../model/git/git.zig");
const git_ui = @import("git_ui.zig");
const scm_state = @import("scm_state.zig");
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
const max_drawn_bytes = 1024;

/// Where one side of a comparison comes from.
pub const Source = union(enum) {
    /// No file: everything on the other side is added or removed.
    empty,
    /// A git object (`HEAD:path`, `:path`, `<rev>^:path`).
    git: []const u8,
    /// A file: its open buffer's text, else what is on disk.
    file: []const u8,
};

pub const Request = struct {
    /// The tab's name.
    title: []const u8,
    /// The repository-relative path, for the header.
    path: []const u8,
    left_label: []const u8,
    right_label: []const u8,
    left: Source,
    right: Source,
    /// The working file the right side shows, to open from the toolbar.
    open_path: ?[]const u8 = null,
    /// A single click's tab, replaced by the next one.
    preview: bool = true,
};

const RowKind = enum { same, removed, added, changed };

const Row = struct {
    l: ?u32,
    r: ?u32,
    kind: RowKind,
    /// The first row of a change (for F7).
    start: bool = false,
};

const Mode = enum { side_by_side, inline_ };

const What = enum { mode, previous, next, open_file };

const Hit = struct { rect: Rect, what: What };

pub const View = struct {
    gpa: Allocator,
    title: []u8,
    path: []u8,
    left_label: []u8,
    right_label: []u8,
    open_path: ?[]u8,
    preview: bool,
    left_text: []u8,
    right_text: []u8,
    left_lines: [][]const u8,
    right_lines: [][]const u8,
    hunks: []linediff.Hunk,
    mode: Mode = .side_by_side,
    rows: std.ArrayList(Row) = .empty,
    /// The mode `rows` were built for.
    rows_mode: ?Mode = null,
    scroll_y: f32 = 0,
    scroll_x: f32 = 0,
    /// The row of the change F7 last went to (null after scrolling by hand).
    current: ?usize = null,
    rect: Rect = .{},
    body: Rect = .{},
    hits: std.ArrayList(Hit) = .empty,

    pub fn destroy(self: *View) void {
        const gpa = self.gpa;
        gpa.free(self.title);
        gpa.free(self.path);
        gpa.free(self.left_label);
        gpa.free(self.right_label);
        if (self.open_path) |p| gpa.free(p);
        gpa.free(self.left_text);
        gpa.free(self.right_text);
        gpa.free(self.left_lines);
        gpa.free(self.right_lines);
        gpa.free(self.hunks);
        self.rows.deinit(gpa);
        self.hits.deinit(gpa);
        gpa.destroy(self);
    }

    /// How many changes there are.
    pub fn changeCount(self: *const View) usize {
        return self.hunks.len;
    }

    fn rebuild(self: *View) void {
        self.rows.clearRetainingCapacity();
        self.rows_mode = self.mode;
        const gpa = self.gpa;
        var li: u32 = 0;
        var ri: u32 = 0;
        for (self.hunks) |h| {
            while (li < h.a_start) : ({
                li += 1;
                ri += 1;
            }) self.rows.append(gpa, .{ .l = li, .r = ri, .kind = .same }) catch return;
            const first = self.rows.items.len;
            if (self.mode == .side_by_side) {
                const n = @max(h.a_len, h.b_len);
                for (0..n) |k| {
                    const kk: u32 = @intCast(k);
                    const l: ?u32 = if (kk < h.a_len) h.a_start + kk else null;
                    const r: ?u32 = if (kk < h.b_len) h.b_start + kk else null;
                    const kind: RowKind = if (l != null and r != null) .changed else if (l != null) .removed else .added;
                    self.rows.append(gpa, .{ .l = l, .r = r, .kind = kind }) catch return;
                }
            } else {
                for (0..h.a_len) |k| self.rows.append(gpa, .{ .l = h.a_start + @as(u32, @intCast(k)), .r = null, .kind = .removed }) catch return;
                for (0..h.b_len) |k| self.rows.append(gpa, .{ .l = null, .r = h.b_start + @as(u32, @intCast(k)), .kind = .added }) catch return;
            }
            if (self.rows.items.len > first) self.rows.items[first].start = true;
            li = h.a_start + h.a_len;
            ri = h.b_start + h.b_len;
        }
        while (li < self.left_lines.len and ri < self.right_lines.len) : ({
            li += 1;
            ri += 1;
        }) self.rows.append(gpa, .{ .l = li, .r = ri, .kind = .same }) catch return;
    }

    fn rowCount(self: *View) usize {
        if (self.rows_mode == null or self.rows_mode.? != self.mode) self.rebuild();
        return self.rows.items.len;
    }

    /// Goes to the next (or previous) change: the first one after the one
    /// last jumped to, else after the top of the view.
    fn jump(self: *View, lh: f32, forward: bool) void {
        _ = self.rowCount();
        const top: usize = @intFromFloat(@max(0, @floor(self.scroll_y / lh + 0.5)));
        if (forward) {
            var i = if (self.current) |c| c + 1 else top;
            while (i < self.rows.items.len) : (i += 1) if (self.rows.items[i].start) return self.scrollTo(lh, i);
        } else {
            var i = self.current orelse top;
            while (i > 0) {
                i -= 1;
                if (self.rows.items[i].start) return self.scrollTo(lh, i);
            }
        }
    }

    fn scrollTo(self: *View, lh: f32, row: usize) void {
        // A couple of lines of context above it.
        self.scroll_y = @max(0, (@as(f32, @floatFromInt(row)) - 2) * lh);
        self.clamp(lh);
        self.current = row;
    }

    fn clamp(self: *View, lh: f32) void {
        const total = @as(f32, @floatFromInt(self.rows.items.len)) * lh;
        self.scroll_y = std.math.clamp(self.scroll_y, 0, @max(0, total - self.body.h + lh * 2));
    }
};

// ---- Opening ---------------------------------------------------------------------------

/// Reads the two sides, then shows them in a tab.
pub const Loader = struct {
    ws: *Workspace,
    req: Request,
    arena: std.heap.ArenaAllocator,
    left: ?[]u8 = null,
    right: ?[]u8 = null,
    left_job: ?*git.Job = null,
    right_job: ?*git.Job = null,
    left_done: bool = false,
    right_done: bool = false,
};

pub fn deinitLoaders(ws: *Workspace) void {
    const st = scm_state.state(ws);
    for (st.loaders.items) |l| destroyLoader(ws, l);
    st.loaders.deinit(ws.gpa);
}

fn destroyLoader(ws: *Workspace, l: *Loader) void {
    const gpa = ws.gpa;
    if (l.left_job) |j| j.detach();
    if (l.right_job) |j| j.detach();
    if (l.left) |t| gpa.free(t);
    if (l.right) |t| gpa.free(t);
    l.arena.deinit();
    gpa.destroy(l);
}

/// Opens the comparison (or refreshes the preview tab with it).
pub fn open(ws: *Workspace, req: Request) void {
    const gpa = ws.gpa;
    const l = gpa.create(Loader) catch return;
    l.* = .{ .ws = ws, .req = undefined, .arena = .init(gpa) };
    const a = l.arena.allocator();
    l.req = .{
        .title = a.dupe(u8, req.title) catch "",
        .path = a.dupe(u8, req.path) catch "",
        .left_label = a.dupe(u8, req.left_label) catch "",
        .right_label = a.dupe(u8, req.right_label) catch "",
        .left = dupSource(a, req.left),
        .right = dupSource(a, req.right),
        .open_path = if (req.open_path) |p| a.dupe(u8, p) catch null else null,
        .preview = req.preview,
    };
    const st = scm_state.state(ws);
    st.loaders.append(gpa, l) catch return destroyLoader(ws, l);
    start(l, true);
    start(l, false);
    maybeFinish(l);
}

fn dupSource(a: Allocator, s: Source) Source {
    return switch (s) {
        .empty => .empty,
        .git => |g| .{ .git = a.dupe(u8, g) catch "" },
        .file => |f| .{ .file = a.dupe(u8, f) catch "" },
    };
}

fn start(l: *Loader, left: bool) void {
    const ws = l.ws;
    const src = if (left) l.req.left else l.req.right;
    const done_ptr = if (left) &l.left_done else &l.right_done;
    switch (src) {
        .empty => done_ptr.* = true,
        .file => |path| {
            const text = readFileText(ws, path);
            if (left) l.left = text else l.right = text;
            done_ptr.* = true;
        },
        .git => |spec| {
            const root = scm_state.state(ws).root orelse {
                done_ptr.* = true;
                return;
            };
            const job = git.Job.start(git_ui.runner(ws), &.{ "show", spec }, root, null, null, l, if (left) onLeft else onRight) catch {
                done_ptr.* = true;
                return;
            };
            if (left) l.left_job = job else l.right_job = job;
        },
    }
}

fn onLeft(ctx: *anyopaque, job: *git.Job) void {
    const l: *Loader = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    l.left_job = null;
    l.left_done = true;
    if (job.output) |o| l.left = l.ws.gpa.dupe(u8, o) catch null;
    maybeFinish(l);
}

fn onRight(ctx: *anyopaque, job: *git.Job) void {
    const l: *Loader = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    l.right_job = null;
    l.right_done = true;
    if (job.output) |o| l.right = l.ws.gpa.dupe(u8, o) catch null;
    maybeFinish(l);
}

/// A file's text: the open buffer's (unsaved edits included), else the disk's.
fn readFileText(ws: *Workspace, path: []const u8) ?[]u8 {
    for (ws.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, path)) {
        return b.textRange(ws.gpa, 0, b.len()) catch null;
    };
    return std.Io.Dir.cwd().readFileAlloc(ws.io, path, ws.gpa, .limited(64 << 20)) catch null;
}

fn maybeFinish(l: *Loader) void {
    if (!l.left_done or !l.right_done) return;
    const ws = l.ws;
    const st = scm_state.state(ws);
    for (st.loaders.items, 0..) |x, i| if (x == l) {
        _ = st.loaders.swapRemove(i);
        break;
    };
    defer destroyLoader(ws, l);
    const gpa = ws.gpa;
    const view = createView(gpa, l) orelse return;
    show(ws, view);
}

fn createView(gpa: Allocator, l: *Loader) ?*View {
    const view = gpa.create(View) catch return null;
    const left_text = if (l.left) |t| gpa.dupe(u8, t) catch return fail(gpa, view) else gpa.dupe(u8, "") catch return fail(gpa, view);
    const right_text = if (l.right) |t| gpa.dupe(u8, t) catch return fail(gpa, view) else gpa.dupe(u8, "") catch return fail(gpa, view);
    const left_lines = linediff.splitLines(gpa, left_text) catch return fail(gpa, view);
    const right_lines = linediff.splitLines(gpa, right_text) catch return fail(gpa, view);
    const hunks = linediff.diff(gpa, left_lines, right_lines) catch return fail(gpa, view);
    view.* = .{
        .gpa = gpa,
        .title = gpa.dupe(u8, l.req.title) catch return fail(gpa, view),
        .path = gpa.dupe(u8, l.req.path) catch return fail(gpa, view),
        .left_label = gpa.dupe(u8, l.req.left_label) catch return fail(gpa, view),
        .right_label = gpa.dupe(u8, l.req.right_label) catch return fail(gpa, view),
        .open_path = if (l.req.open_path) |p| gpa.dupe(u8, p) catch null else null,
        .preview = l.req.preview,
        .left_text = left_text,
        .right_text = right_text,
        .left_lines = left_lines,
        .right_lines = right_lines,
        .hunks = hunks,
    };
    return view;
}

fn fail(gpa: Allocator, view: *View) ?*View {
    gpa.destroy(view);
    return null;
}

/// Puts the view in the preview diff tab (reused), or a new tab.
fn show(ws: *Workspace, view: *View) void {
    if (view.preview) for (ws.tabs.items, 0..) |t, i| if (t.diff_view) |old| if (old.preview) {
        ws.tabs.items[i].diff_view = view;
        t.editor.buffer.display_name = view.title;
        old.destroy();
        ws.activateTab(i);
        ws.focus = .editor;
        ws.updateTitle();
        return;
    };
    const buffer = ws.gpa.create(Buffer) catch return view.destroy();
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    ws.next_buffer_id += 1;
    buffer.display_name = view.title;
    buffer.read_only = true;
    ws.buffers.append(ws.gpa, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch return view.destroy();
    _ = ws.insertTab(.{ .editor = ed, .diff_view = view, .transient = view.preview }) catch {
        ed.destroy();
        return view.destroy();
    };
    ws.focus = .editor;
    ws.updateTitle();
}

// ---- Painting --------------------------------------------------------------------------

fn tint(theme: anytype, kind: RowKind, right: bool) ?Color {
    const dark = theme.background.r * 0.299 + theme.background.g * 0.587 + theme.background.b * 0.114 < 0.5;
    return switch (kind) {
        .same => null,
        .removed => (if (dark) Color.hex(0xff0000) else Color.hex(0xff0000)).withAlpha(if (dark) 0.2 else 0.16),
        .added => (if (dark) Color.hex(0x9bb955) else Color.hex(0x9bb955)).withAlpha(if (dark) 0.2 else 0.3),
        .changed => if (right) (Color.hex(0x9bb955)).withAlpha(if (dark) 0.2 else 0.3) else Color.hex(0xff0000).withAlpha(if (dark) 0.2 else 0.16),
    };
}

fn strong(kind: RowKind, right: bool) Color {
    return if (kind == .removed or (kind == .changed and !right)) Color.hex(0xff0000).withAlpha(0.35) else Color.hex(0x9bb955).withAlpha(0.4);
}

fn expandTabs(line: []const u8, buf: []u8) []const u8 {
    if (std.mem.indexOfScalar(u8, line, '\t') == null) return line[0..@min(line.len, max_drawn_bytes)];
    var n: usize = 0;
    var col: usize = 0;
    for (line) |ch| {
        if (ch == '\t') {
            const w = 4 - col % 4;
            if (n + w > buf.len) break;
            @memset(buf[n..][0..w], ' ');
            n += w;
            col += w;
        } else {
            if (n >= buf.len) break;
            buf[n] = ch;
            n += 1;
            if (ch & 0xC0 != 0x80) col += 1;
        }
    }
    return buf[0..n];
}

/// The paint hook of a diff tab (before the editor would paint). True if `ed` is one.
pub fn paint(ws: *Workspace, ed: *Editor, rect: Rect, scene: *scene_mod.Scene) bool {
    const i = ws.tabIndexOf(ed) orelse return false;
    const v = ws.tabs.items[i].diff_view orelse return false;
    const theme = ws.theme;
    const ctx: ui.Ctx = .{ .scene = scene, .text = ws.env.text, .theme = theme, .fonts = ws.fonts, .mouse = ws.mouse };
    v.rect = rect;
    v.hits.clearRetainingCapacity();
    scene.fill(rect, theme.background);
    scene.pushClip(rect) catch return true;
    defer scene.popClip();
    const lh = ed.lineHeight();
    const cw = ed.charWidth();

    // Toolbar: what is compared, and the buttons.
    const bar: Rect = .{ .x = rect.x, .y = rect.y, .w = rect.w, .h = toolbar_h };
    scene.fill(bar, theme.tabbar_background);
    scene.fill(.{ .x = bar.x, .y = bar.bottom() - 1, .w = bar.w, .h = 1 }, theme.tab_separator);
    {
        var bx = bar.right() - 10;
        const labels = [_]struct { what: What, text: []const u8 }{
            .{ .what = .open_file, .text = "Open File" },
            .{ .what = .mode, .text = if (v.mode == .side_by_side) "Inline" else "Side by Side" },
        };
        for (labels) |b| {
            if (b.what == .open_file and v.open_path == null) continue;
            const w = ctx.text.measure(b.text, ws.fonts.ui_small) + 18;
            const r: Rect = .{ .x = bx - w, .y = bar.y + 4, .w = w, .h = toolbar_h - 8 };
            if (ctx.hovered(r)) scene.quad(r, .{ .color = theme.sidebar_hover, .radius = 3 });
            ctx.labelCentered(r, b.text, ws.fonts.ui_small, theme.sidebar_foreground);
            v.hits.append(v.gpa, .{ .rect = r, .what = b.what }) catch {};
            bx -= w + 6;
        }
        // Previous and next change.
        for ([_]What{ .next, .previous }) |wh| {
            const r: Rect = .{ .x = bx - 24, .y = bar.y + 4, .w = 24, .h = toolbar_h - 8 };
            if (ctx.hovered(r)) scene.quad(r, .{ .color = theme.sidebar_hover, .radius = 3 });
            const cx = r.x + r.w / 2;
            const cy = r.y + r.h / 2;
            for (0..4) |k| {
                const t: f32 = @floatFromInt(k);
                const y = if (wh == .previous) cy - 3 + t else cy + 3 - t - 1;
                scene.fill(.{ .x = cx - t, .y = y, .w = 2 * t + 1, .h = 1 }, theme.sidebar_foreground);
            }
            v.hits.append(v.gpa, .{ .rect = r, .what = wh }) catch {};
            bx -= 26;
        }
        var cbuf: [32]u8 = undefined;
        const count = std.fmt.bufPrint(&cbuf, "{d} change{s}", .{ v.changeCount(), if (v.changeCount() == 1) "" else "s" }) catch "";
        _ = ctx.labelRight(.{ .x = bar.x, .y = bar.y, .w = bx - bar.x - 8, .h = bar.h }, count, ws.fonts.ui_small, theme.sidebar_foreground_dim);
        var tbuf: [320]u8 = undefined;
        const title = std.fmt.bufPrint(&tbuf, "{s}  \u{2194}  {s}", .{ v.left_label, v.right_label }) catch v.title;
        _ = ctx.labelFit(.{ .x = bar.x + 12, .y = bar.y, .w = @max(40, bx - bar.x - 130), .h = bar.h }, title, ws.fonts.ui, theme.sidebar_foreground);
    }

    const body: Rect = .{ .x = rect.x, .y = bar.bottom(), .w = rect.w, .h = @max(0, rect.h - toolbar_h) };
    v.body = body;
    const total_rows = v.rowCount();
    v.clamp(lh);
    scene.pushClip(body) catch return true;
    defer scene.popClip();

    const digits = blk: {
        var n: usize = @max(v.left_lines.len, v.right_lines.len);
        var d: f32 = 3;
        while (n >= 1000) : (n /= 10) d += 1;
        break :blk d;
    };
    const num_w = digits * cw + 16;
    const first: usize = @intFromFloat(@floor(v.scroll_y / lh));
    const last = @min(total_rows, first + @as(usize, @intFromFloat(@ceil(body.h / lh))) + 2);
    const side = v.mode == .side_by_side;
    const pane_w = if (side) @floor(body.w / 2) else body.w;
    const panes: usize = if (side) 2 else 1;
    var tabbuf: [max_drawn_bytes * 4]u8 = undefined;

    for (0..panes) |pi| {
        const px = body.x + @as(f32, @floatFromInt(pi)) * pane_w;
        const pane: Rect = .{ .x = px, .y = body.y, .w = if (pi == panes - 1) body.right() - px else pane_w, .h = body.h };
        scene.pushClip(pane) catch continue;
        defer scene.popClip();
        const is_right = side and pi == 1;
        // Inline shows both numbers, side by side shows the pane's own.
        const gutter_w = if (side) num_w else num_w * 2;
        scene.fill(.{ .x = pane.x, .y = pane.y, .w = gutter_w, .h = pane.h }, theme.background);
        var ri = first;
        while (ri < last) : (ri += 1) {
            const row = v.rows.items[ri];
            const y = body.y + @as(f32, @floatFromInt(ri)) * lh - v.scroll_y;
            const r: Rect = .{ .x = pane.x, .y = y, .w = pane.w, .h = lh };
            const has = if (side) (if (is_right) row.r != null else row.l != null) else true;
            if (tint(theme, row.kind, is_right)) |c| {
                if (has or !side) scene.fill(r, c) else scene.fill(r, Color.hex(0x808080).withAlpha(0.08));
            }
            const text_x = pane.x + gutter_w + (if (side) @as(f32, 8) else 20) - v.scroll_x;
            // Numbers.
            var nb: [16]u8 = undefined;
            if (side) {
                const idx = if (is_right) row.r else row.l;
                if (idx) |ix| {
                    const n = std.fmt.bufPrint(&nb, "{d}", .{ix + 1}) catch "";
                    _ = ctx.labelRight(.{ .x = pane.x, .y = y, .w = num_w - 8, .h = lh }, n, ws.env.font, theme.gutter_foreground);
                }
            } else {
                if (row.l) |ix| {
                    const n = std.fmt.bufPrint(&nb, "{d}", .{ix + 1}) catch "";
                    _ = ctx.labelRight(.{ .x = pane.x, .y = y, .w = num_w - 8, .h = lh }, n, ws.env.font, theme.gutter_foreground);
                }
                if (row.r) |ix| {
                    const n = std.fmt.bufPrint(&nb, "{d}", .{ix + 1}) catch "";
                    _ = ctx.labelRight(.{ .x = pane.x + num_w, .y = y, .w = num_w - 8, .h = lh }, n, ws.env.font, theme.gutter_foreground);
                }
            }
            // The line.
            const line: ?[]const u8 = if (side) (if (is_right) (if (row.r) |ix| v.right_lines[ix] else null) else (if (row.l) |ix| v.left_lines[ix] else null)) else (if (row.r) |ix| v.right_lines[ix] else if (row.l) |ix| v.left_lines[ix] else null);
            if (line) |ln| {
                scene.pushClip(.{ .x = pane.x + gutter_w, .y = pane.y, .w = pane.w - gutter_w, .h = pane.h }) catch {};
                defer scene.popClip();
                const drawn = expandTabs(ln, &tabbuf);
                // The changed part of a modified line.
                if (row.kind == .changed and row.l != null and row.r != null) {
                    const a = expandTabs(v.left_lines[row.l.?], &tabbuf);
                    var other: [max_drawn_bytes * 4]u8 = undefined;
                    const b = expandTabs(v.right_lines[row.r.?], &other);
                    var pre: usize = 0;
                    while (pre < a.len and pre < b.len and a[pre] == b[pre]) pre += 1;
                    var suf: usize = 0;
                    while (suf < a.len - pre and suf < b.len - pre and a[a.len - 1 - suf] == b[b.len - 1 - suf]) suf += 1;
                    // Whole characters only.
                    const mine = if (is_right) b else a;
                    while (pre > 0 and pre < mine.len and mine[pre] & 0xC0 == 0x80) pre -= 1;
                    var end = mine.len - @min(suf, mine.len - pre);
                    while (end < mine.len and mine[end] & 0xC0 == 0x80) end += 1;
                    if (end > pre) {
                        const x0 = text_x + ctx.text.measure(mine[0..pre], ws.env.font);
                        const x1 = text_x + ctx.text.measure(mine[0..end], ws.env.font);
                        scene.fill(.{ .x = x0, .y = y, .w = @max(2, x1 - x0), .h = lh }, strong(row.kind, is_right));
                    }
                }
                _ = ctx.label(.{ .x = text_x, .y = y, .w = pane.w, .h = lh }, drawn, ws.env.font, theme.foreground);
                if (!side) {
                    const mark: []const u8 = switch (row.kind) {
                        .removed => "-",
                        .added => "+",
                        else => "",
                    };
                    if (mark.len > 0) _ = ctx.label(.{ .x = pane.x + gutter_w + 6, .y = y, .w = 10, .h = lh }, mark, ws.env.font, theme.gutter_foreground_active);
                }
            }
        }
        scene.fill(.{ .x = pane.x + gutter_w - 1, .y = pane.y, .w = 1, .h = pane.h }, theme.tab_separator);
    }
    if (side) scene.fill(.{ .x = body.x + pane_w - 1, .y = body.y, .w = 1, .h = body.h }, theme.tab_separator);
    return true;
}

// ---- Input -----------------------------------------------------------------------------

fn active(ws: *Workspace) ?*View {
    const i = ws.activeIndex() orelse return null;
    return ws.tabs.items[i].diff_view;
}

fn viewAt(ws: *Workspace, pos: platform.Vec2) ?*View {
    for (ws.tabs.items) |t| if (t.diff_view) |v| {
        if (v.rect.contains(pos.x, pos.y) and ws.group_active[t.group] == t.editor) return v;
    };
    return null;
}

pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    switch (ev) {
        .key_down => |k| {
            if (ws.menuIsOpen() or ws.focus != .editor or ws.overlay != null) return false;
            const v = active(ws) orelse return false;
            return handleKey(ws, v, k);
        },
        .mouse_down => |m| {
            if (ws.menuIsOpen() or ws.overlay != null) return false;
            const v = viewAt(ws, m.pos) orelse return false;
            ws.focus = .editor;
            if (m.button != .left) return true;
            for (v.hits.items) |h| if (h.rect.contains(m.pos.x, m.pos.y)) {
                perform(ws, v, h.what);
                return true;
            };
            // A click in the tab keeps it.
            v.preview = false;
            return true;
        },
        .scroll => |s| {
            if (ws.overlay != null) return false;
            const v = viewAt(ws, s.pos) orelse return false;
            const lh = lineHeightOf(ws);
            const dy = if (s.unit == .lines) s.delta.y * lh * 3 else s.delta.y;
            const dx = if (s.unit == .lines) s.delta.x * lh * 3 else s.delta.x;
            v.scroll_y += dy;
            v.current = null;
            v.scroll_x = @max(0, v.scroll_x + dx);
            v.clamp(lh);
            return true;
        },
        else => return false,
    }
}

fn lineHeightOf(ws: *Workspace) f32 {
    const ed = ws.activeEditor() orelse return 18;
    return ed.lineHeight();
}

fn perform(ws: *Workspace, v: *View, what: What) void {
    const lh = lineHeightOf(ws);
    switch (what) {
        .mode => {
            v.mode = if (v.mode == .side_by_side) .inline_ else .side_by_side;
            v.scroll_y = 0;
            v.scroll_x = 0;
        },
        .previous => v.jump(lh, false),
        .next => v.jump(lh, true),
        .open_file => if (v.open_path) |p| {
            _ = ws.openFile(p, false) catch return;
        },
    }
    v.preview = false;
}

fn handleKey(ws: *Workspace, v: *View, k: platform.KeyEvent) bool {
    const lh = lineHeightOf(ws);
    const m = k.mods;
    if (m.ctrl or m.alt or m.super) return false;
    switch (k.key) {
        .named => |n| switch (n) {
            .f7 => {
                v.jump(lh, !m.shift);
                return true;
            },
            .up => {
                v.current = null;
                v.scroll_y -= lh;
            },
            .down => {
                v.current = null;
                v.scroll_y += lh;
            },
            .page_up => v.scroll_y -= v.body.h - lh * 2,
            .page_down => v.scroll_y += v.body.h - lh * 2,
            .home => v.scroll_y = 0,
            .end => v.scroll_y = 1e9,
            .left => v.scroll_x = @max(0, v.scroll_x - 40),
            .right => v.scroll_x += 40,
            else => return false,
        },
        .char => return false,
    }
    v.clamp(lh);
    return true;
}

// ---- Tests -----------------------------------------------------------------------------

const testing = std.testing;

fn testView(left: []const u8, right: []const u8) !*View {
    const gpa = testing.allocator;
    const view = try gpa.create(View);
    const lt = try gpa.dupe(u8, left);
    const rt = try gpa.dupe(u8, right);
    const ll = try linediff.splitLines(gpa, lt);
    const rl = try linediff.splitLines(gpa, rt);
    view.* = .{
        .gpa = gpa,
        .title = try gpa.dupe(u8, "t"),
        .path = try gpa.dupe(u8, "p"),
        .left_label = try gpa.dupe(u8, "l"),
        .right_label = try gpa.dupe(u8, "r"),
        .open_path = null,
        .preview = true,
        .left_text = lt,
        .right_text = rt,
        .left_lines = ll,
        .right_lines = rl,
        .hunks = try linediff.diff(gpa, ll, rl),
    };
    return view;
}

test "side by side rows align the sides, with filler where one has no line" {
    const v = try testView("a\nb\nc\nd\n", "a\nB\nB2\nd\n");
    defer v.destroy();
    try testing.expectEqual(@as(usize, 4), v.rowCount());
    try testing.expectEqual(RowKind.same, v.rows.items[0].kind);
    try testing.expectEqual(RowKind.changed, v.rows.items[1].kind);
    try testing.expect(v.rows.items[1].start);
    // c was replaced by B2, so the pair b/B is followed by c/B2.
    try testing.expectEqual(RowKind.changed, v.rows.items[2].kind);
    try testing.expectEqual(@as(?u32, 3), v.rows.items[3].l);
    try testing.expectEqual(@as(?u32, 3), v.rows.items[3].r);
}

test "a pure addition has filler on the left, a pure removal on the right" {
    const v = try testView("a\nb\n", "a\nx\ny\nb\n");
    defer v.destroy();
    try testing.expectEqual(@as(usize, 4), v.rowCount());
    try testing.expectEqual(RowKind.added, v.rows.items[1].kind);
    try testing.expectEqual(@as(?u32, null), v.rows.items[1].l);
    try testing.expectEqual(@as(?u32, 1), v.rows.items[1].r);
    const w = try testView("a\nx\nb\n", "a\nb\n");
    defer w.destroy();
    try testing.expectEqual(@as(usize, 3), w.rowCount());
    try testing.expectEqual(RowKind.removed, w.rows.items[1].kind);
    try testing.expectEqual(@as(?u32, null), w.rows.items[1].r);
}

test "inline lists the removed lines, then the added ones" {
    const v = try testView("a\nb\nc\n", "a\nB\nB2\nc\n");
    defer v.destroy();
    v.mode = .inline_;
    try testing.expectEqual(@as(usize, 5), v.rowCount());
    try testing.expectEqual(RowKind.removed, v.rows.items[1].kind);
    try testing.expectEqual(RowKind.added, v.rows.items[2].kind);
    try testing.expectEqual(RowKind.added, v.rows.items[3].kind);
    try testing.expectEqual(RowKind.same, v.rows.items[4].kind);
}

test "F7 jumps to the next change and Shift+F7 back" {
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(testing.allocator);
    var new: std.ArrayList(u8) = .empty;
    defer new.deinit(testing.allocator);
    for (0..100) |i| {
        try text.print(testing.allocator, "line {d}\n", .{i});
        if (i == 30 or i == 70) try new.print(testing.allocator, "CHANGED {d}\n", .{i}) else try new.print(testing.allocator, "line {d}\n", .{i});
    }
    const v = try testView(text.items, new.items);
    defer v.destroy();
    try testing.expectEqual(@as(usize, 2), v.changeCount());
    v.body = .{ .x = 0, .y = 0, .w = 800, .h = 400 };
    _ = v.rowCount();
    v.jump(20, true);
    // Row 30 is two lines below the top.
    try testing.expectEqual(@as(f32, 28 * 20), v.scroll_y);
    v.jump(20, true);
    try testing.expectEqual(@as(f32, 68 * 20), v.scroll_y);
    v.jump(20, false);
    try testing.expectEqual(@as(f32, 28 * 20), v.scroll_y);
}
