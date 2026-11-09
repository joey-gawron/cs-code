//! The Source Control window, docked in the side bar next to the Explorer
//! (Visual Studio's Git Changes / VS Code's Source Control view): a header
//! with the branch and the Fetch, Pull, Push and Refresh buttons, a commit
//! box with Amend and Sign-off, and the changes in Merge Changes, Staged
//! Changes and Changes groups. Click a file to see its change, double-click
//! to open it; Space stages or unstages, Delete discards. What the window
//! shows and runs comes from scm_state.zig.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const scm_state = @import("scm_state.zig");
const agenda_ui = @import("agenda_ui.zig");
const quick_panel = @import("quick_panel.zig");
const git = @import("../../model/git/git.zig");
const status_mod = @import("../../model/git/status.zig");
const git_ui = @import("git_ui.zig");
const diff_view = @import("diff_view.zig");
const graph_view = @import("graph_view.zig");
const history = @import("../../model/git/history.zig");
const blame_mod = @import("../../model/git/blame.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const ui = @import("../../framework/ui/ui.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Entry = status_mod.Entry;

const header_h: f32 = 30;
const toolbar_h: f32 = 30;
const box_h: f32 = 64;
const button_h: f32 = 26;
const option_h: f32 = 24;
const group_h: f32 = 26;
const file_h: f32 = 24;
const min_width: f32 = 300;
const timeline_head_h: f32 = 26;
const timeline_row_h: f32 = 40;
const margin: f32 = 10;
const accent = Color.hex(0x007acc);
const accent_hover = Color.hex(0x1a8ad4);
const disabled_fill = Color.hex(0x3c3c3c);

/// Prompts and questions of the window (the git popups have 500 to 506).
pub const tag_confirm: u32 = 520;
pub const tag_branch_pick: u32 = 521;
pub const tag_branch_name: u32 = 522;
pub const tag_menu: u32 = 523;
pub const tag_stash_message: u32 = 524;

pub const Group = enum { merge, staged, changes };

const Row = struct {
    /// Null: the group's header row.
    entry: ?u32,
    group: Group,
};

const What = union(enum) {
    branch,
    fetch,
    pull,
    push,
    refresh,
    more,
    message,
    commit,
    amend,
    signoff,
    group_toggle: Group,
    group_stage: Group,
    group_unstage,
    group_discard: Group,
    row: usize,
    row_open: usize,
    row_discard: usize,
    row_stage: usize,
    timeline_toggle,
    timeline_row: usize,
};

const Hit = struct { rect: Rect, what: What };

const Focus = enum { list, message };

/// A question waiting for its answer: what to run if it's yes.
const Pending = struct {
    argv: []const []const u8,
    /// Paths to delete (untracked files being discarded).
    label: []u8,
    owned: [][]u8,
    clear: bool = false,
    /// Sent to the command's stdin (a patch).
    input: ?[]u8 = null,

    fn destroy(self: *Pending, gpa: Allocator) void {
        if (self.input) |i| gpa.free(i);
        for (self.owned) |s| gpa.free(s);
        gpa.free(self.owned);
        gpa.free(self.argv);
        gpa.free(self.label);
        gpa.destroy(self);
    }
};

pub const Ui = struct {
    focus: Focus = .list,
    scroll: f32 = 0,
    content_h: f32 = 0,
    list_rect: Rect = .{},
    box_rect: Rect = .{},
    rows: std.ArrayList(Row) = .empty,
    /// The status generation `rows` were made from (and the collapsed groups).
    rows_generation: u32 = std.math.maxInt(u32),
    collapsed: [3]bool = @splat(false),
    selected: usize = 0,
    hits: std.ArrayList(Hit) = .empty,
    /// The caret in the commit message (a byte offset).
    caret: usize = 0,
    amend: bool = false,
    signoff: bool = false,
    pending: ?*Pending = null,
    /// What a branch list's items stand for: remote or not.
    branches: std.ArrayList(Branch) = .empty,
    listing: ?*git.Job = null,
    timeline: Timeline = .{},

    pub fn deinitUi(self: *Ui, gpa: Allocator) void {
        self.rows.deinit(gpa);
        self.hits.deinit(gpa);
        if (self.pending) |p| p.destroy(gpa);
        for (self.branches.items) |b| {
            gpa.free(b.name);
            gpa.free(b.remote);
        }
        self.branches.deinit(gpa);
        if (self.listing) |j| j.detach();
        self.timeline.deinitTimeline(gpa);
    }
};

const Branch = struct { name: []u8, remote: []u8 };

/// The commits that changed the active file (the Timeline pane).
pub const Timeline = struct {
    open: bool = true,
    /// The file it is for (absolute), and the status generation it was read under.
    path: ?[]u8 = null,
    generation: u32 = std.math.maxInt(u32),
    log: ?history.Log = null,
    job: ?*git.Job = null,
    scroll: f32 = 0,
    selected: ?usize = null,
    rect: Rect = .{},
    list: Rect = .{},

    fn deinitTimeline(self: *Timeline, gpa: Allocator) void {
        if (self.job) |j| j.detach();
        if (self.path) |p| gpa.free(p);
        if (self.log) |*l| l.deinit();
    }
};

fn ui_(ws: *Workspace) *Ui {
    return &scm_state.state(ws).ui;
}

fn active(ws: *Workspace) bool {
    return ws.agenda.tab == .scm and ws.sidebar_visible and !ws.distraction_free;
}

/// Brings the window up (the View menu, ctrl+shift+g).
pub fn show(ws: *Workspace) void {
    ws.agenda.tab = .scm;
    ws.sidebar_visible = true;
    ws.sidebar_width = @max(ws.sidebar_width, min_width);
    onShown(ws);
}

pub fn onShown(ws: *Workspace) void {
    ws.sidebar_width = @max(ws.sidebar_width, min_width);
    ws.agenda.focused = false;
    scm_state.state(ws).stale = true;
    ui_(ws).focus = .list;
}

// ---- Rows -----------------------------------------------------------------------------

fn inGroup(e: *const Entry, g: Group) bool {
    return switch (g) {
        .merge => e.kind == .unmerged,
        .staged => status_mod.inSection(e, .staged),
        .changes => e.kind == .untracked or (e.kind != .unmerged and status_mod.inSection(e, .unstaged)),
    };
}

fn rebuildRows(ws: *Workspace) void {
    const st = scm_state.state(ws);
    const u = &st.ui;
    u.rows.clearRetainingCapacity();
    u.rows_generation = st.generation;
    const snap = st.snapshot orelse return;
    for ([_]Group{ .merge, .staged, .changes }) |g| {
        var count: usize = 0;
        for (snap.status.entries) |*e| if (inGroup(e, g)) {
            count += 1;
        };
        if (count == 0) continue;
        u.rows.append(ws.gpa, .{ .entry = null, .group = g }) catch return;
        if (u.collapsed[@intFromEnum(g)]) continue;
        for (snap.status.entries, 0..) |*e, i| if (inGroup(e, g)) {
            u.rows.append(ws.gpa, .{ .entry = @intCast(i), .group = g }) catch return;
        };
    }
    if (u.selected >= u.rows.items.len) u.selected = u.rows.items.len -| 1;
}

fn groupCount(ws: *Workspace, g: Group) usize {
    const snap = scm_state.state(ws).snapshot orelse return 0;
    var n: usize = 0;
    for (snap.status.entries) |*e| if (inGroup(e, g)) {
        n += 1;
    };
    return n;
}

fn groupTitle(g: Group) []const u8 {
    return switch (g) {
        .merge => "Merge Changes",
        .staged => "Staged Changes",
        .changes => "Changes",
    };
}

// ---- Painting ---------------------------------------------------------------------------

fn addHit(ws: *Workspace, rect: Rect, what: What) void {
    ui_(ws).hits.append(ws.gpa, .{ .rect = rect, .what = what }) catch {};
}

fn iconButton(ws: *Workspace, ctx: *const ui.Ctx, r: Rect, what: What, enabled: bool) void {
    if (enabled and ctx.hovered(r)) ctx.scene.quad(r, .{ .color = ctx.theme.sidebar_hover, .radius = 3 });
    if (enabled) addHit(ws, r, what);
}

fn arrow(ctx: *const ui.Ctx, cx: f32, cy: f32, up: bool, color: Color) void {
    // A shaft and a head, a pixel row at a time.
    ctx.scene.fill(.{ .x = cx - 0.75, .y = cy - 4, .w = 1.5, .h = 8 }, color);
    for (0..4) |i| {
        const t: f32 = @floatFromInt(i);
        const y = if (up) cy - 4 + t else cy + 4 - t - 1;
        ctx.scene.fill(.{ .x = cx - t - 0.5, .y = y, .w = 2 * t + 1, .h = 1 }, color);
    }
}

fn plus(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color) void {
    ctx.scene.fill(.{ .x = cx - 4.5, .y = cy - 0.75, .w = 9, .h = 1.5 }, color);
    ctx.scene.fill(.{ .x = cx - 0.75, .y = cy - 4.5, .w = 1.5, .h = 9 }, color);
}

fn minus(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color) void {
    ctx.scene.fill(.{ .x = cx - 4.5, .y = cy - 0.75, .w = 9, .h = 1.5 }, color);
}

/// A ring with an arrow head at its open end (refresh, discard).
fn ring(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color, bg: Color) void {
    ctx.scene.quad(.{ .x = cx - 5, .y = cy - 5, .w = 10, .h = 10 }, .{ .border_color = color, .border_width = 1.5, .radius = 5 });
    ctx.scene.fill(.{ .x = cx + 0.5, .y = cy - 6.5, .w = 6, .h = 3.5 }, bg);
    ctx.scene.fill(.{ .x = cx + 0.5, .y = cy - 7, .w = 5, .h = 1.5 }, color);
    ctx.scene.fill(.{ .x = cx + 4, .y = cy - 7, .w = 1.5, .h = 4.5 }, color);
}

fn dots(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color) void {
    for ([_]f32{ -5, 0, 5 }) |dx| ctx.dot(cx + dx, cy, 1.4, color);
}

fn branchGlyph(ctx: *const ui.Ctx, x: f32, cy: f32, color: Color) void {
    ctx.scene.fill(.{ .x = x + 3.5, .y = cy - 4, .w = 1.2, .h = 8 }, color);
    ctx.dot(x + 4, cy - 4.5, 2, color);
    ctx.dot(x + 4, cy + 4.5, 2, color);
}

pub fn paintBody(ws: *Workspace, ctx: *const ui.Ctx) void {
    const st = scm_state.state(ws);
    const u = &st.ui;
    if (u.rows_generation != st.generation) rebuildRows(ws);
    const theme = ctx.theme;
    const scene = ctx.scene;
    const sb = ws.sidebar_rect;
    u.hits.clearRetainingCapacity();
    const left = sb.x + margin;
    const right = sb.right() - margin;
    const dim = theme.sidebar_foreground_dim;
    const fg = theme.sidebar_foreground;
    var y = sb.y + header_h + 2;

    if (st.unavailable or st.root == null) {
        const text = if (git_ui.runner(ws).missing) "Git isn't installed (no git on PATH)." else if (st.unavailable) "This folder isn't a git repository." else "Looking for a git repository…";
        _ = ctx.label(.{ .x = left, .y = y + 6, .w = right - left, .h = 22 }, text, ws.fonts.ui, dim);
        return;
    }

    // Header: the branch, and the actions.
    {
        var bbuf: [128]u8 = undefined;
        const branch = scm_state.branchLabel(ws, &bbuf) orelse "";
        const bw = @min(right - left - 130, ctx.text.measure(branch, ws.fonts.ui) + 34);
        const br: Rect = .{ .x = left - 2, .y = y + 2, .w = @max(60, bw), .h = 24 };
        if (ctx.hovered(br)) scene.quad(br, .{ .color = theme.sidebar_hover, .radius = 3 });
        branchGlyph(ctx, br.x + 6, br.y + br.h / 2, fg);
        _ = ctx.labelFit(.{ .x = br.x + 22, .y = br.y, .w = br.w - 26, .h = br.h }, branch, ws.fonts.ui, fg);
        addHit(ws, br, .branch);
        var ix = right - 11;
        const busy = st.busy != null;
        const buttons = [_]struct { what: What, kind: u8 }{
            .{ .what = .more, .kind = 'm' },
            .{ .what = .refresh, .kind = 'r' },
            .{ .what = .push, .kind = 'u' },
            .{ .what = .pull, .kind = 'd' },
            .{ .what = .fetch, .kind = 'f' },
        };
        for (buttons) |b| {
            const r: Rect = .{ .x = ix - 11, .y = y + 3, .w = 22, .h = 22 };
            iconButton(ws, ctx, r, b.what, !busy or b.kind == 'm' or b.kind == 'r');
            const c = if (!busy or b.kind == 'm' or b.kind == 'r') fg else dim.withAlpha(0.5);
            switch (b.kind) {
                'm' => dots(ctx, ix, y + 14, c),
                'r' => ring(ctx, ix, y + 14, c, theme.sidebar_background),
                'u' => arrow(ctx, ix, y + 14, true, c),
                'd' => arrow(ctx, ix, y + 14, false, c),
                else => {
                    // Fetch: a down arrow over a line.
                    arrow(ctx, ix, y + 12, false, c);
                    scene.fill(.{ .x = ix - 5, .y = y + 19, .w = 10, .h = 1.5 }, c);
                },
            }
            ix -= 24;
        }
    }
    y += toolbar_h;

    // The commit message.
    const focused_box = u.focus == .message and ws.agenda.focused;
    const box: Rect = .{ .x = left, .y = y, .w = right - left, .h = box_h };
    u.box_rect = box;
    scene.quad(box, .{ .color = theme.input_background, .border_color = if (focused_box) accent else theme.tab_separator, .border_width = 1, .radius = 2 });
    addHit(ws, box, .message);
    {
        scene.pushClip(.{ .x = box.x + 1, .y = box.y + 1, .w = box.w - 2, .h = box.h - 2 }) catch return;
        defer scene.popClip();
        const lh: f32 = 18;
        if (st.message.items.len == 0) {
            var pbuf: [128]u8 = undefined;
            const head = if (scm_state.state(ws).snapshot) |s| (s.status.branch.head orelse "detached HEAD") else "main";
            const ph = std.fmt.bufPrint(&pbuf, "Message ({s}\u{23ce} to commit on '{s}')", .{ if (@import("builtin").os.tag.isDarwin()) "\u{2318}" else "Ctrl+", head }) catch "Message";
            _ = ctx.label(.{ .x = box.x + 8, .y = box.y + 4, .w = box.w - 16, .h = lh }, ph, ws.fonts.ui, dim);
        } else {
            var line_y = box.y + 4;
            var it = std.mem.splitScalar(u8, st.message.items, '\n');
            while (it.next()) |line| : (line_y += lh) {
                if (line_y > box.bottom()) break;
                _ = ctx.label(.{ .x = box.x + 8, .y = line_y, .w = box.w - 16, .h = lh }, line, ws.fonts.ui, fg);
            }
        }
        if (focused_box) {
            // The caret: after the text before it on its line.
            const upto = st.message.items[0..@min(u.caret, st.message.items.len)];
            var row: f32 = 0;
            var start: usize = 0;
            for (upto, 0..) |ch, i| if (ch == '\n') {
                row += 1;
                start = i + 1;
            };
            const cx = box.x + 8 + ctx.text.measure(upto[start..], ws.fonts.ui);
            scene.fill(.{ .x = @round(cx), .y = box.y + 5 + row * lh, .w = 1, .h = lh - 3 }, fg);
        }
    }
    y += box_h + 8;

    // Commit.
    const snap_staged = groupCount(ws, .staged);
    const snap_changes = groupCount(ws, .changes);
    {
        const r: Rect = .{ .x = left, .y = y, .w = right - left, .h = button_h };
        const can = snap_staged > 0 or snap_changes > 0 or u.amend;
        const label = if (u.amend) "Commit (Amend)" else if (snap_staged == 0 and snap_changes > 0) "Commit All" else "Commit";
        const fill = if (!can) disabled_fill else if (ctx.hovered(r)) accent_hover else accent;
        scene.quad(r, .{ .color = fill, .radius = 2 });
        ctx.labelCentered(r, label, ws.fonts.ui, if (can) Color.hex(0xffffff) else dim);
        if (can) addHit(ws, r, .commit);
    }
    y += button_h + 6;

    // Amend and Sign-off.
    {
        var x = left;
        const opts = [_]struct { text: []const u8, on: bool, what: What }{
            .{ .text = "Amend", .on = u.amend, .what = .amend },
            .{ .text = "Sign-off", .on = u.signoff, .what = .signoff },
        };
        for (opts) |o| {
            const w = ctx.text.measure(o.text, ws.fonts.ui_small) + 28;
            const r: Rect = .{ .x = x, .y = y, .w = w, .h = option_h };
            const cb: Rect = .{ .x = x + 2, .y = y + 6, .w = 12, .h = 12 };
            scene.quad(cb, .{ .color = if (o.on) accent else theme.input_background, .border_color = if (o.on) accent else dim, .border_width = 1, .radius = 2 });
            if (o.on) {
                scene.fill(.{ .x = cb.x + 3, .y = cb.y + 6, .w = 2, .h = 2 }, Color.hex(0xffffff));
                scene.fill(.{ .x = cb.x + 5, .y = cb.y + 4, .w = 2, .h = 2 }, Color.hex(0xffffff));
                scene.fill(.{ .x = cb.x + 7, .y = cb.y + 2, .w = 2, .h = 2 }, Color.hex(0xffffff));
            }
            _ = ctx.label(.{ .x = x + 20, .y = y, .w = w - 20, .h = option_h }, o.text, ws.fonts.ui_small, fg);
            addHit(ws, r, o.what);
            x += w + 8;
        }
        if (st.busy) |b| {
            var buf: [64]u8 = undefined;
            const text = std.fmt.bufPrint(&buf, "{s}\u{2026}", .{b}) catch b;
            _ = ctx.labelRight(.{ .x = left, .y = y, .w = right - left, .h = option_h }, text, ws.fonts.ui_small, dim);
        }
    }
    y += option_h + 4;

    // The changes, and under them the Timeline of the active file.
    const region: Rect = .{ .x = sb.x + 1, .y = y, .w = sb.w - 1, .h = @max(0, sb.bottom() - agenda_ui.tab_h - y) };
    const tl_h: f32 = if (region.h < 170) 0 else if (u.timeline.open) std.math.clamp(region.h * 0.45, 110, 320) else timeline_head_h;
    const list: Rect = .{ .x = region.x, .y = region.y, .w = region.w, .h = region.h - tl_h };
    u.list_rect = list;
    if (tl_h > 0) paintTimeline(ws, ctx, .{ .x = region.x, .y = region.bottom() - tl_h, .w = region.w, .h = tl_h });
    scene.fill(.{ .x = list.x, .y = list.y - 1, .w = list.w, .h = 1 }, theme.tab_separator);
    scene.pushClip(list) catch return;
    defer scene.popClip();
    if (u.rows.items.len == 0) {
        u.content_h = 0;
        _ = ctx.label(.{ .x = left, .y = list.y + 8, .w = right - left, .h = 22 }, if (st.snapshot == null) "Reading the status…" else "No changes. The working tree is clean.", ws.fonts.ui, dim);
        return;
    }
    var content: f32 = 0;
    var ry = list.y - u.scroll;
    for (u.rows.items, 0..) |row, ri| {
        const h: f32 = if (row.entry == null) group_h else file_h;
        content += h;
        defer ry += h;
        if (ry + h < list.y or ry > list.bottom()) continue;
        const r: Rect = .{ .x = list.x, .y = ry, .w = list.w, .h = h };
        paintRow(ws, ctx, row, ri, r);
    }
    u.content_h = content;
}

fn paintRow(ws: *Workspace, ctx: *const ui.Ctx, row: Row, ri: usize, r: Rect) void {
    const st = scm_state.state(ws);
    const u = &st.ui;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const fg = theme.sidebar_foreground;
    const dim = theme.sidebar_foreground_dim;
    const hovered = ctx.hovered(r) and u.list_rect.contains(ctx.mouse.x, ctx.mouse.y);
    const cy = r.y + r.h / 2;
    const right = r.right() - margin;
    if (row.entry == null) {
        if (hovered) scene.fill(r, theme.sidebar_hover);
        const collapsed = u.collapsed[@intFromEnum(row.group)];
        ctx.chevron(r.x + 14, cy, !collapsed, dim);
        const title = groupTitle(row.group);
        const tw = ctx.label(.{ .x = r.x + 26, .y = r.y, .w = r.w - 90, .h = r.h }, title, ws.fonts.ui_bold, fg);
        addHit(ws, r, .{ .group_toggle = row.group });
        // The count, as a pill at the right edge.
        var nb: [16]u8 = undefined;
        const n = std.fmt.bufPrint(&nb, "{d}", .{groupCount(ws, row.group)}) catch "";
        const pw = ctx.text.measure(n, ws.fonts.ui_small) + 12;
        const pill: Rect = .{ .x = right - pw, .y = cy - 8, .w = pw, .h = 16 };
        scene.quad(pill, .{ .color = Color.hex(0x4d4d4d), .radius = 8 });
        ctx.labelCentered(pill, n, ws.fonts.ui_small, Color.hex(0xffffff));
        _ = tw;
        if (hovered) {
            // Stage All / Unstage All and Discard All, left of the count.
            var bx = pill.x - 14;
            const bg = theme.sidebar_hover;
            _ = bg;
            switch (row.group) {
                .staged => {
                    const b: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
                    iconButton(ws, ctx, b, .group_unstage, true);
                    minus(ctx, bx, cy, fg);
                },
                .changes => {
                    const b1: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
                    iconButton(ws, ctx, b1, .{ .group_stage = .changes }, true);
                    plus(ctx, bx, cy, fg);
                    bx -= 22;
                    const b2: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
                    iconButton(ws, ctx, b2, .{ .group_discard = .changes }, true);
                    ring(ctx, bx, cy, fg, if (hovered) theme.sidebar_hover else theme.sidebar_background);
                },
                .merge => {
                    const b: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
                    iconButton(ws, ctx, b, .{ .group_stage = .merge }, true);
                    plus(ctx, bx, cy, fg);
                },
            }
        }
        return;
    }
    const snap = st.snapshot orelse return;
    const e = &snap.status.entries[row.entry.?];
    if (ri == u.selected) scene.fill(r, theme.sidebar_selected) else if (hovered) scene.fill(r, theme.sidebar_hover);
    addHit(ws, r, .{ .row = ri });
    const d = scm_state.decorationOf(e);
    const color = if (d) |dd| scm_state.kindColor(theme, if (row.group == .staged and e.x == 'A') .added else dd.kind) else fg;
    var x = r.x + 26;
    ctx.fileIcon(x + 7, cy, Color.hex(0xc5c5c5));
    x += 22;
    // Letter at the right edge; the hover buttons sit left of it.
    const letter: u8 = if (row.group == .staged) e.x else (if (e.kind == .untracked) 'U' else if (e.kind == .unmerged) '!' else e.y);
    var tail = right;
    if (letter != '.') {
        const lb = [_]u8{letter};
        _ = ctx.labelRight(.{ .x = r.x, .y = r.y, .w = right - r.x, .h = r.h }, &lb, ws.fonts.ui, color);
        tail -= 16;
    }
    var bx = tail - 10;
    if (hovered) {
        const bg = if (ri == u.selected) theme.sidebar_selected else theme.sidebar_hover;
        const area_w: f32 = if (row.group == .changes) 22 * 3 else 22 * 2;
        scene.fill(.{ .x = tail - area_w, .y = r.y, .w = area_w + 16, .h = r.h }, bg);
        const stage_what: What = .{ .row_stage = ri };
        const sb: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
        iconButton(ws, ctx, sb, stage_what, true);
        if (row.group == .staged) minus(ctx, bx, cy, fg) else plus(ctx, bx, cy, fg);
        bx -= 22;
        if (row.group == .changes) {
            const db: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
            iconButton(ws, ctx, db, .{ .row_discard = ri }, true);
            ring(ctx, bx, cy, fg, bg);
            bx -= 22;
        }
        const ob: Rect = .{ .x = bx - 10, .y = cy - 10, .w = 20, .h = 20 };
        iconButton(ws, ctx, ob, .{ .row_open = ri }, true);
        ctx.fileIcon(bx, cy, fg);
        tail = bx - 12;
    }
    // Name in the change's colour, the folder dimmed after it.
    const base = std.fs.path.basename(std.mem.trimEnd(u8, e.path, "/"));
    const dir = std.fs.path.dirname(std.mem.trimEnd(u8, e.path, "/")) orelse "";
    const struck = d != null and d.?.kind == .deleted;
    const nw = ctx.labelFit(.{ .x = x, .y = r.y, .w = @max(20, tail - x), .h = r.h }, base, ws.fonts.ui, color);
    if (struck) scene.fill(.{ .x = x, .y = @round(cy), .w = nw, .h = 1 }, color);
    if (dir.len > 0 and tail - x - nw > 30) _ = ctx.labelFit(.{ .x = x + nw + 6, .y = r.y, .w = tail - x - nw - 6, .h = r.h }, dir, ws.fonts.ui_small, dim);
}

// ---- The Timeline ---------------------------------------------------------------------------

/// The active file, when it is one of the repository's.
fn timelineFile(ws: *Workspace) ?[]const u8 {
    const root = scm_state.state(ws).root orelse return null;
    const ed = ws.activeEditor() orelse return null;
    const path = ed.buffer.path orelse return null;
    if (path.len <= root.len + 1 or !std.mem.startsWith(u8, path, root) or path[root.len] != '/') return null;
    return path;
}

/// Follows the active file, and reads its history again when the repository changed.
pub fn tick(ws: *Workspace) void {
    if (!active(ws)) return;
    const st = scm_state.state(ws);
    const tl = &st.ui.timeline;
    if (!tl.open or st.root == null) return;
    if (timelineFile(ws)) |p| {
        const same = if (tl.path) |old| std.mem.eql(u8, old, p) else false;
        if (!same) {
            if (tl.path) |old| ws.gpa.free(old);
            tl.path = ws.gpa.dupe(u8, p) catch null;
            if (tl.log) |*l| l.deinit();
            tl.log = null;
            tl.selected = null;
            tl.scroll = 0;
            tl.generation = std.math.maxInt(u32);
            if (tl.job) |j| j.detach();
            tl.job = null;
        }
    }
    if (tl.path == null or tl.job != null or st.snapshot == null) return;
    if (tl.generation == st.generation) return;
    tl.generation = st.generation;
    const root = st.root.?;
    const rel = tl.path.?[root.len + 1 ..];
    tl.job = git.Job.start(git_ui.runner(ws), &.{ "log", "--follow", "-n", "80", "-z", history.log_format, "--", rel }, root, null, null, ws, onTimeline) catch null;
}

fn onTimeline(ctx: *anyopaque, job: *git.Job) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    const tl = &ui_(ws).timeline;
    tl.job = null;
    const out = job.output orelse return;
    if (tl.log) |*l| l.deinit();
    tl.log = history.parse(ws.gpa, out) catch null;
    ws.dirty = true;
}

fn paintTimeline(ws: *Workspace, ctx: *const ui.Ctx, r: Rect) void {
    const st = scm_state.state(ws);
    const tl = &st.ui.timeline;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const dim = theme.sidebar_foreground_dim;
    const fg = theme.sidebar_foreground;
    tl.rect = r;
    scene.fill(.{ .x = r.x, .y = r.y, .w = r.w, .h = 1 }, theme.tab_separator);
    const head: Rect = .{ .x = r.x, .y = r.y + 1, .w = r.w, .h = timeline_head_h - 1 };
    if (ctx.hovered(head)) scene.fill(head, theme.sidebar_hover);
    ctx.chevron(r.x + 14, head.y + head.h / 2, tl.open, dim);
    const tw = ctx.label(.{ .x = r.x + 26, .y = head.y, .w = 120, .h = head.h }, "TIMELINE", ws.fonts.ui_small, fg);
    if (tl.path) |p| _ = ctx.labelFit(.{ .x = r.x + 26 + tw + 8, .y = head.y, .w = @max(0, r.w - tw - 44), .h = head.h }, std.fs.path.basename(p), ws.fonts.ui_small, dim);
    addHit(ws, head, .timeline_toggle);
    tl.list = .{};
    if (!tl.open) return;
    const list: Rect = .{ .x = r.x, .y = head.bottom(), .w = r.w, .h = @max(0, r.bottom() - head.bottom()) };
    tl.list = list;
    scene.pushClip(list) catch return;
    defer scene.popClip();
    const log = tl.log orelse {
        _ = ctx.label(.{ .x = r.x + 14, .y = list.y + 4, .w = r.w - 28, .h = 22 }, if (tl.path == null) "Open a file to see its history." else "Reading the history\u{2026}", ws.fonts.ui, dim);
        return;
    };
    if (log.commits.len == 0) {
        _ = ctx.label(.{ .x = r.x + 14, .y = list.y + 4, .w = r.w - 28, .h = 22 }, "No commits touch this file yet.", ws.fonts.ui, dim);
        return;
    }
    const now = blame_mod.unixNow();
    var y = list.y - tl.scroll;
    for (log.commits, 0..) |c, i| {
        defer y += timeline_row_h;
        if (y + timeline_row_h < list.y or y > list.bottom()) continue;
        const row: Rect = .{ .x = r.x, .y = y, .w = r.w, .h = timeline_row_h };
        const hovered = ctx.hovered(row) and list.contains(ctx.mouse.x, ctx.mouse.y);
        if (tl.selected != null and tl.selected.? == i) scene.fill(row, theme.sidebar_selected) else if (hovered) scene.fill(row, theme.sidebar_hover);
        addHit(ws, row, .{ .timeline_row = i });
        // A commit's dot on the line down the left.
        scene.fill(.{ .x = r.x + 18, .y = y, .w = 1, .h = timeline_row_h }, theme.tab_separator);
        ctx.dot(r.x + 18.5, y + 14, 3.5, Color.hex(0x3794ff));
        _ = ctx.labelFit(.{ .x = r.x + 32, .y = y + 3, .w = r.w - 46, .h = 18 }, c.subject, ws.fonts.ui, fg);
        var buf: [160]u8 = undefined;
        var age: [48]u8 = undefined;
        const detail = std.fmt.bufPrint(&buf, "{s}, {s}", .{ c.author, blame_mod.formatRelative(&age, now, c.time) }) catch c.author;
        _ = ctx.labelFit(.{ .x = r.x + 32, .y = y + 21, .w = r.w - 110, .h = 16 }, detail, ws.fonts.ui_small, dim);
        _ = ctx.labelRight(.{ .x = r.x, .y = y + 21, .w = r.w - 12, .h = 16 }, c.short(), ws.env.font, dim);
    }
}

fn openTimelineRow(ws: *Workspace, index: usize) void {
    const st = scm_state.state(ws);
    const tl = &st.ui.timeline;
    const log = tl.log orelse return;
    if (index >= log.commits.len) return;
    const root = st.root orelse return;
    const path = tl.path orelse return;
    if (path.len <= root.len + 1) return;
    tl.selected = index;
    const c = log.commits[index];
    graph_view.openCommitFile(ws, c.hash, c.short(), c.parents, .{ .status = 'M', .path = path[root.len + 1 ..] });
}

/// Brings up the dock with the Timeline open (a command).
pub fn showTimeline(ws: *Workspace) void {
    show(ws);
    const tl = &ui_(ws).timeline;
    tl.open = true;
    tl.generation = std.math.maxInt(u32);
}

// ---- Input ------------------------------------------------------------------------------

fn clampScroll(u: *Ui) void {
    u.scroll = std.math.clamp(u.scroll, 0, @max(0, u.content_h - u.list_rect.h));
}

pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    const shown = active(ws);
    switch (ev) {
        .mouse_down => |m| {
            if (!shown) return false;
            if (ws.menuIsOpen() or ws.overlay != null) return false;
            const sb = ws.sidebar_rect;
            if (!sb.contains(m.pos.x, m.pos.y)) {
                ws.agenda.focused = false;
                return false;
            }
            if (m.pos.y >= sb.bottom() - agenda_ui.tab_h or m.pos.y < sb.y + header_h) return false;
            return click(ws, m);
        },
        .scroll => |s| {
            if (!shown or ws.overlay != null) return false;
            const u = ui_(ws);
            if (u.timeline.list.contains(s.pos.x, s.pos.y)) {
                const tl = &u.timeline;
                const n: f32 = if (tl.log) |l| @floatFromInt(l.commits.len) else 0;
                const dy = if (s.unit == .lines) s.delta.y * timeline_row_h else s.delta.y;
                tl.scroll = std.math.clamp(tl.scroll + dy, 0, @max(0, n * timeline_row_h - tl.list.h));
                return true;
            }
            if (!u.list_rect.contains(s.pos.x, s.pos.y)) return false;
            const dy = if (s.unit == .lines) s.delta.y * file_h else s.delta.y;
            u.scroll += dy;
            clampScroll(u);
            return true;
        },
        .key_down => |k| {
            if (!shown or !ws.agenda.focused or ws.overlay != null or ws.menuIsOpen() or ws.leader.active) return false;
            return key(ws, k);
        },
        else => return false,
    }
}

fn click(ws: *Workspace, m: platform.MouseEvent) bool {
    const u = ui_(ws);
    ws.agenda.focused = true;
    var hit: ?What = null;
    // Later hits sit on top of earlier ones (a row's buttons over the row).
    var i = u.hits.items.len;
    while (i > 0) {
        i -= 1;
        const h = u.hits.items[i];
        if (!h.rect.contains(m.pos.x, m.pos.y)) continue;
        // The list is clipped: rows scrolled under the controls don't count.
        const in_list = switch (h.what) {
            .group_toggle, .group_stage, .group_unstage, .group_discard, .row, .row_open, .row_discard, .row_stage => true,
            else => false,
        };
        if (in_list and !u.list_rect.contains(m.pos.x, m.pos.y)) continue;
        if (h.what == .timeline_row and !u.timeline.list.contains(m.pos.x, m.pos.y)) continue;
        hit = h.what;
        break;
    }
    const what = hit orelse {
        u.focus = .list;
        return true;
    };
    if (m.button != .left) return true;
    if (what != .message) u.focus = .list;
    switch (what) {
        .branch => pickBranch(ws),
        .fetch => run(ws, &.{ "fetch", "--prune" }, "Fetching", "Fetched", true),
        .pull => run(ws, &.{"pull"}, "Pulling", "Pulled", true),
        .push => push(ws),
        .refresh => scm_state.state(ws).stale = true,
        .more => openMenu(ws),
        .message => {
            u.focus = .message;
            u.caret = scm_state.state(ws).message.items.len;
        },
        .commit => commit(ws),
        .amend => u.amend = !u.amend,
        .signoff => u.signoff = !u.signoff,
        .group_toggle => |g| {
            u.collapsed[@intFromEnum(g)] = !u.collapsed[@intFromEnum(g)];
            rebuildRows(ws);
        },
        .group_stage => |g| stageGroup(ws, g),
        .group_unstage => unstageAll(ws),
        .group_discard => discardAll(ws),
        .row => |ri| {
            u.selected = ri;
            if (m.click_count >= 2) openRowFile(ws, ri) else openRowChange(ws, ri);
        },
        .row_open => |ri| openRowFile(ws, ri),
        .row_discard => |ri| discardRow(ws, ri),
        .row_stage => |ri| toggleRow(ws, ri),
        .timeline_toggle => {
            u.timeline.open = !u.timeline.open;
            if (u.timeline.open) u.timeline.generation = std.math.maxInt(u32);
        },
        .timeline_row => |ti| openTimelineRow(ws, ti),
    }
    return true;
}

fn key(ws: *Workspace, k: platform.KeyEvent) bool {
    const u = ui_(ws);
    if (u.focus == .message) return messageKey(ws, k);
    const m = k.mods;
    if (m.ctrl or m.alt or (m.super and k.key != .named)) return false;
    switch (k.key) {
        .named => |n| switch (n) {
            .up => return moveSelection(ws, -1),
            .down => return moveSelection(ws, 1),
            .enter => {
                if (m.super) {
                    commit(ws);
                    return true;
                }
                if (selectedRow(ws)) |r| if (r.entry != null) openRowChange(ws, u.selected) else toggleGroup(ws, r.group);
                return true;
            },
            .delete, .backspace => {
                if (selectedRow(ws)) |r| if (r.entry != null) discardRow(ws, u.selected);
                return true;
            },
            .escape => {
                ws.agenda.focused = false;
                ws.focus = .editor;
                return true;
            },
            .tab => {
                u.focus = .message;
                u.caret = scm_state.state(ws).message.items.len;
                return true;
            },
            else => return false,
        },
        .char => |cp| {
            if (m.super) return false;
            if (cp == ' ') {
                if (selectedRow(ws)) |r| if (r.entry != null) toggleRow(ws, u.selected) else toggleGroup(ws, r.group);
                return true;
            }
            return false;
        },
    }
}

fn selectedRow(ws: *Workspace) ?Row {
    const u = ui_(ws);
    if (u.selected >= u.rows.items.len) return null;
    return u.rows.items[u.selected];
}

fn moveSelection(ws: *Workspace, delta: isize) bool {
    const u = ui_(ws);
    if (u.rows.items.len == 0) return true;
    const next: isize = @as(isize, @intCast(u.selected)) + delta;
    u.selected = @intCast(std.math.clamp(next, 0, @as(isize, @intCast(u.rows.items.len)) - 1));
    // Keep it in view.
    var y: f32 = 0;
    for (u.rows.items[0..u.selected]) |r| y += if (r.entry == null) group_h else file_h;
    const h: f32 = if (u.rows.items[u.selected].entry == null) group_h else file_h;
    if (y < u.scroll) u.scroll = y else if (y + h > u.scroll + u.list_rect.h) u.scroll = y + h - u.list_rect.h;
    clampScroll(u);
    return true;
}

fn toggleGroup(ws: *Workspace, g: Group) void {
    const u = ui_(ws);
    u.collapsed[@intFromEnum(g)] = !u.collapsed[@intFromEnum(g)];
    rebuildRows(ws);
}

// ---- The commit message --------------------------------------------------------------------

fn messageKey(ws: *Workspace, k: platform.KeyEvent) bool {
    const st = scm_state.state(ws);
    const u = &st.ui;
    const msg = &st.message;
    u.caret = @min(u.caret, msg.items.len);
    const m = k.mods;
    switch (k.key) {
        .named => |n| switch (n) {
            .escape => {
                u.focus = .list;
                return true;
            },
            .enter => {
                if (m.super or m.ctrl) {
                    commit(ws);
                } else insert(ws, "\n");
                return true;
            },
            .backspace => {
                if (u.caret > 0) {
                    var n_bytes: usize = 1;
                    while (n_bytes < u.caret and msg.items[u.caret - n_bytes] & 0xC0 == 0x80) n_bytes += 1;
                    msg.replaceRange(ws.gpa, u.caret - n_bytes, n_bytes, "") catch {};
                    u.caret -= n_bytes;
                }
                return true;
            },
            .delete => {
                if (u.caret < msg.items.len) {
                    var n_bytes: usize = 1;
                    while (u.caret + n_bytes < msg.items.len and msg.items[u.caret + n_bytes] & 0xC0 == 0x80) n_bytes += 1;
                    msg.replaceRange(ws.gpa, u.caret, n_bytes, "") catch {};
                }
                return true;
            },
            .left => {
                if (u.caret > 0) {
                    u.caret -= 1;
                    while (u.caret > 0 and msg.items[u.caret] & 0xC0 == 0x80) u.caret -= 1;
                }
                return true;
            },
            .right => {
                if (u.caret < msg.items.len) {
                    u.caret += 1;
                    while (u.caret < msg.items.len and msg.items[u.caret] & 0xC0 == 0x80) u.caret += 1;
                }
                return true;
            },
            .home => {
                while (u.caret > 0 and msg.items[u.caret - 1] != '\n') u.caret -= 1;
                return true;
            },
            .end => {
                while (u.caret < msg.items.len and msg.items[u.caret] != '\n') u.caret += 1;
                return true;
            },
            .up, .down => {
                // Same column on the neighbouring line (bytes).
                var ls = u.caret;
                while (ls > 0 and msg.items[ls - 1] != '\n') ls -= 1;
                const col = u.caret - ls;
                if (n == .up) {
                    if (ls == 0) return true;
                    var ps = ls - 1;
                    while (ps > 0 and msg.items[ps - 1] != '\n') ps -= 1;
                    u.caret = ps + @min(col, ls - 1 - ps);
                } else {
                    var le = u.caret;
                    while (le < msg.items.len and msg.items[le] != '\n') le += 1;
                    if (le >= msg.items.len) return true;
                    var ne = le + 1;
                    while (ne < msg.items.len and msg.items[ne] != '\n') ne += 1;
                    u.caret = le + 1 + @min(col, ne - le - 1);
                }
                return true;
            },
            .tab => {
                u.focus = .list;
                return true;
            },
            else => return false,
        },
        .char => |cp| {
            if (m.super) {
                if (cp == 'v') {
                    const text = ws.env.clipboard.get(ws.env.clipboard.ctx, ws.gpa) orelse return true;
                    defer ws.gpa.free(text);
                    insert(ws, text);
                    return true;
                }
                if (cp == 'a') {
                    u.caret = msg.items.len;
                    return true;
                }
                return false;
            }
            if (m.ctrl or m.alt) return false;
            if (k.text.len > 0) {
                insert(ws, k.text);
                return true;
            }
            var enc: [4]u8 = undefined;
            const len = std.unicode.utf8Encode(cp, &enc) catch return false;
            insert(ws, enc[0..len]);
            return true;
        },
    }
}

fn insert(ws: *Workspace, text: []const u8) void {
    const st = scm_state.state(ws);
    const u = &st.ui;
    st.message.insertSlice(ws.gpa, @min(u.caret, st.message.items.len), text) catch return;
    u.caret = @min(u.caret, st.message.items.len - text.len) + text.len;
}

// ---- Commands -------------------------------------------------------------------------------

fn run(ws: *Workspace, argv: []const []const u8, label: []const u8, done: []const u8, batch: bool) void {
    scm_state.run(ws, argv, .{ .label = label, .done = done, .batch = batch });
}

fn push(ws: *Workspace) void {
    const st = scm_state.state(ws);
    const snap = st.snapshot orelse return;
    if (snap.status.branch.upstream == null) {
        // A branch without an upstream is published to origin.
        const head = snap.status.branch.head orelse return ws.setStatus("Git: push needs a branch, not a detached HEAD", .{});
        run(ws, &.{ "push", "-u", "origin", head }, "Pushing", "Pushed", true);
    } else run(ws, &.{"push"}, "Pushing", "Pushed", true);
}

fn commit(ws: *Workspace) void {
    const st = scm_state.state(ws);
    const u = &st.ui;
    const staged = groupCount(ws, .staged);
    const changes = groupCount(ws, .changes);
    const message = std.mem.trim(u8, st.message.items, " \t\r\n");
    if (message.len == 0 and !u.amend) return ws.setStatus("Enter a commit message first.", .{});
    if (staged == 0 and !u.amend and changes == 0) return ws.setStatus("There are no changes to commit.", .{});
    if (staged == 0 and !u.amend) {
        // Nothing staged: offer to commit all tracked changes.
        return ask(ws, "There are no staged changes to commit. Stage all your changes and commit them directly?", &.{ "commit", "-a", "-F", "-", "--cleanup=strip" }, .{ .signoff = u.signoff, .message = true });
    }
    runCommit(ws, false);
}

fn runCommit(ws: *Workspace, all: bool) void {
    const st = scm_state.state(ws);
    const u = &st.ui;
    const message = std.mem.trim(u8, st.message.items, " \t\r\n");
    var argv: std.ArrayList([]const u8) = .empty;
    defer argv.deinit(ws.gpa);
    argv.append(ws.gpa, "commit") catch return;
    if (all) argv.append(ws.gpa, "-a") catch return;
    if (u.signoff) argv.append(ws.gpa, "-s") catch return;
    if (u.amend) argv.append(ws.gpa, "--amend") catch return;
    if (message.len == 0) {
        argv.append(ws.gpa, "--no-edit") catch return;
        scm_state.run(ws, argv.items, .{ .label = "Committing", .done = "Committed", .clear_message = true });
    } else {
        argv.appendSlice(ws.gpa, &.{ "-F", "-", "--cleanup=strip" }) catch return;
        scm_state.run(ws, argv.items, .{ .input = message, .label = "Committing", .done = "Committed", .clear_message = true });
    }
    u.amend = false;
}

fn entryAt(ws: *Workspace, ri: usize) ?struct { e: *const Entry, group: Group } {
    const u = ui_(ws);
    if (ri >= u.rows.items.len) return null;
    const row = u.rows.items[ri];
    const idx = row.entry orelse return null;
    const snap = scm_state.state(ws).snapshot orelse return null;
    return .{ .e = &snap.status.entries[idx], .group = row.group };
}

fn toggleRow(ws: *Workspace, ri: usize) void {
    const r = entryAt(ws, ri) orelse return;
    if (r.group == .staged) unstagePath(ws, r.e) else stagePath(ws, r.e);
}

fn stagePath(ws: *Workspace, e: *const Entry) void {
    scm_state.run(ws, &.{ "add", "--", e.path }, .{});
}

fn unstagePath(ws: *Workspace, e: *const Entry) void {
    const snap = scm_state.state(ws).snapshot orelse return;
    if (snap.status.branch.initial()) {
        scm_state.run(ws, &.{ "rm", "--cached", "-r", "-q", "--", e.path }, .{});
    } else if (e.orig_path) |orig| {
        scm_state.run(ws, &.{ "reset", "-q", "HEAD", "--", e.path, orig }, .{});
    } else scm_state.run(ws, &.{ "reset", "-q", "HEAD", "--", e.path }, .{});
}

fn stageGroup(ws: *Workspace, g: Group) void {
    _ = g;
    scm_state.run(ws, &.{ "add", "-A" }, .{});
}

fn unstageAll(ws: *Workspace) void {
    const snap = scm_state.state(ws).snapshot orelse return;
    if (snap.status.branch.initial()) {
        scm_state.run(ws, &.{ "rm", "--cached", "-r", "-q", "--", "." }, .{});
    } else scm_state.run(ws, &.{ "reset", "-q", "HEAD" }, .{});
}

/// Asks, then discards one file's changes: tracked files go back to the
/// index, untracked ones are deleted.
fn discardRow(ws: *Workspace, ri: usize) void {
    const r = entryAt(ws, ri) orelse return;
    if (r.group == .staged) return;
    var qbuf: [320]u8 = undefined;
    if (r.e.kind == .untracked) {
        const q = std.fmt.bufPrint(&qbuf, "Delete {s}? The file is untracked and will be DELETED.", .{r.e.path}) catch "Delete this file?";
        ask(ws, q, &.{ "clean", "-f", "-q", "--", r.e.path }, .{});
    } else {
        const q = std.fmt.bufPrint(&qbuf, "Discard the changes in {s}? This is irreversible.", .{r.e.path}) catch "Discard the changes?";
        ask(ws, q, &.{ "checkout", "--", r.e.path }, .{});
    }
}

fn discardAll(ws: *Workspace) void {
    const snap = scm_state.state(ws).snapshot orelse return;
    var untracked = false;
    for (snap.status.entries) |*e| if (e.kind == .untracked) {
        untracked = true;
    };
    ask(ws, if (untracked) "Discard ALL changes, and delete the untracked files? This is irreversible." else "Discard ALL changes? This is irreversible.", &.{ "checkout", "--", "." }, .{ .also_clean = untracked });
}

const AskOptions = struct {
    signoff: bool = false,
    message: bool = false,
    also_clean: bool = false,
};

/// A question whose yes runs `argv` with `input` on its stdin.
pub fn askWithInput(ws: *Workspace, question: []const u8, argv: []const []const u8, input: []const u8) void {
    ask(ws, question, argv, .{});
    const u = ui_(ws);
    if (u.pending) |p| p.input = ws.gpa.dupe(u8, input) catch null;
}

/// Opens a Yes/No question; yes runs `argv`.
fn ask(ws: *Workspace, question: []const u8, argv: []const []const u8, o: AskOptions) void {
    const u = ui_(ws);
    const gpa = ws.gpa;
    if (u.pending) |p| p.destroy(gpa);
    u.pending = null;
    const p = gpa.create(Pending) catch return;
    var owned: std.ArrayList([]u8) = .empty;
    const args = gpa.alloc([]const u8, argv.len + @as(usize, if (o.signoff) 1 else 0)) catch return gpa.destroy(p);
    var n: usize = 0;
    for (argv) |a| {
        const copy = gpa.dupe(u8, a) catch continue;
        owned.append(gpa, copy) catch {
            gpa.free(copy);
            continue;
        };
        args[n] = copy;
        n += 1;
        if (o.signoff and n == 1) {
            const s = gpa.dupe(u8, "-s") catch continue;
            owned.append(gpa, s) catch {
                gpa.free(s);
                continue;
            };
            args[n] = s;
            n += 1;
        }
    }
    p.* = .{
        .argv = gpa.realloc(args, n) catch args[0..n],
        .label = gpa.dupe(u8, if (o.also_clean) "clean" else if (o.message) "message" else "") catch &.{},
        .owned = owned.toOwnedSlice(gpa) catch &.{},
    };
    u.pending = p;
    ws.openList(.list, gpa.dupe(u8, question) catch return, tag_confirm, &.{ "Yes", "No" });
}

fn answer(ws: *Workspace, yes: bool) void {
    const u = ui_(ws);
    const p = u.pending orelse return;
    u.pending = null;
    defer p.destroy(ws.gpa);
    if (!yes) return;
    if (std.mem.eql(u8, p.label, "message")) {
        const st = scm_state.state(ws);
        const message = std.mem.trim(u8, st.message.items, " \t\r\n");
        scm_state.run(ws, p.argv, .{ .input = message, .label = "Committing", .done = "Committed", .clear_message = true });
        return;
    }
    scm_state.run(ws, p.argv, .{ .input = p.input });
    if (std.mem.eql(u8, p.label, "clean")) scm_state.run(ws, &.{ "clean", "-f", "-d", "-q" }, .{});
}

fn absPath(ws: *Workspace, buf: []u8, rel: []const u8) ?[]const u8 {
    const root = scm_state.state(ws).root orelse return null;
    return std.fmt.bufPrint(buf, "{s}/{s}", .{ root, std.mem.trimEnd(u8, rel, "/") }) catch null;
}

fn openRowFile(ws: *Workspace, ri: usize) void {
    const r = entryAt(ws, ri) orelse return;
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const path = absPath(ws, &buf, r.e.path) orelse return;
    _ = ws.openFile(path, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ r.e.path, err });
        return;
    };
    ws.focus = .editor;
}

/// The change in the diff editor: a staged one HEAD against the index, an
/// unstaged one the index against the file, an untracked file against
/// nothing, a file deleted on disk against nothing.
fn openRowChange(ws: *Workspace, ri: usize) void {
    const r = entryAt(ws, ri) orelse return;
    openChange(ws, r.e, r.group, true);
}

pub fn openChange(ws: *Workspace, e: *const Entry, group: Group, preview: bool) void {
    const st = scm_state.state(ws);
    const snap = st.snapshot orelse return;
    var abs_buf: [std.fs.max_path_bytes]u8 = undefined;
    const abs = absPath(ws, &abs_buf, e.path) orelse return;
    // A conflicted file is edited, not compared.
    if (group == .merge) {
        _ = ws.openFile(abs, false) catch return;
        ws.focus = .editor;
        return;
    }
    var head: [std.fs.max_path_bytes + 8]u8 = undefined;
    var index: [std.fs.max_path_bytes + 2]u8 = undefined;
    var left_buf: [std.fs.max_path_bytes + 16]u8 = undefined;
    var right_buf: [std.fs.max_path_bytes + 16]u8 = undefined;
    const path = std.mem.trimEnd(u8, e.path, "/");
    const index_spec = std.fmt.bufPrint(&index, ":{s}", .{path}) catch return;
    const orig = e.orig_path orelse path;
    const head_spec = std.fmt.bufPrint(&head, "HEAD:{s}", .{orig}) catch return;
    const initial = snap.status.branch.initial();
    var title_buf: [160]u8 = undefined;
    const base = std.fs.path.basename(path);
    const deleted_on_disk = e.y == 'D';
    if (group == .staged) {
        const title = std.fmt.bufPrint(&title_buf, "{s} (Index)", .{base}) catch base;
        diff_view.open(ws, .{
            .title = title,
            .path = path,
            .left_label = std.fmt.bufPrint(&left_buf, "{s} (HEAD)", .{orig}) catch orig,
            .right_label = std.fmt.bufPrint(&right_buf, "{s} (Index)", .{path}) catch path,
            .left = if (initial or e.x == 'A') .empty else .{ .git = head_spec },
            .right = if (e.x == 'D') .empty else .{ .git = index_spec },
            .open_path = if (e.x == 'D') null else abs,
            .preview = preview,
            .kind = .staged,
        });
        return;
    }
    const title = std.fmt.bufPrint(&title_buf, "{s} (Working Tree)", .{base}) catch base;
    diff_view.open(ws, .{
        .title = title,
        .path = path,
        .left_label = std.fmt.bufPrint(&left_buf, "{s} (Index)", .{path}) catch path,
        .right_label = std.fmt.bufPrint(&right_buf, "{s} (Working Tree)", .{path}) catch path,
        .left = if (e.kind == .untracked) .empty else .{ .git = index_spec },
        .right = if (deleted_on_disk) .empty else .{ .file = abs },
        .open_path = if (deleted_on_disk) null else abs,
        .preview = preview,
        .kind = if (e.kind == .untracked) .commit else .worktree,
    });
}

// ---- Branches and the menu -----------------------------------------------------------------

fn pickBranch(ws: *Workspace) void {
    const u = ui_(ws);
    const st = scm_state.state(ws);
    const root = st.root orelse return;
    if (u.listing != null) return;
    u.listing = git.Job.start(git_ui.runner(ws), &.{ "for-each-ref", "--sort=-committerdate", "--format=%(refname)", "refs/heads", "refs/remotes" }, root, null, null, ws, onBranches) catch null;
}

fn onBranches(ctx: *anyopaque, job: *git.Job) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    defer job.destroy();
    const u = ui_(ws);
    u.listing = null;
    const out = job.output orelse return;
    const gpa = ws.gpa;
    for (u.branches.items) |b| {
        gpa.free(b.name);
        gpa.free(b.remote);
    }
    u.branches.clearRetainingCapacity();
    const snap = scm_state.state(ws).snapshot;
    const current: ?[]const u8 = if (snap) |s| s.status.branch.head else null;
    ws.openList(.list, gpa.dupe(u8, "Select a branch or tag to check out") catch return, tag_branch_pick, &.{});
    const o = ws.overlay orelse return;
    o.input.placeholder = "branch name";
    o.addItem(.{ .title = o.arena().dupe(u8, "Create new branch\u{2026}") catch return, .detail = "", .payload = .{ .value = 0 } }) catch {};
    var seen: std.StringHashMapUnmanaged(void) = .empty;
    defer seen.deinit(gpa);
    // Local branches first, then the remote ones that have no local twin.
    for ([_][]const u8{ "refs/heads/", "refs/remotes/" }) |prefix| {
        var it = std.mem.tokenizeScalar(u8, out, '\n');
        while (it.next()) |ref| {
            if (!std.mem.startsWith(u8, ref, prefix)) continue;
            var name = ref[prefix.len..];
            var remote: []const u8 = "";
            if (prefix[5] == 'r') {
                const slash = std.mem.indexOfScalar(u8, name, '/') orelse continue;
                remote = name[0..slash];
                name = name[slash + 1 ..];
                if (std.mem.eql(u8, name, "HEAD")) continue;
            }
            if (remote.len == 0 and current != null and std.mem.eql(u8, name, current.?)) continue;
            if (seen.contains(name)) continue;
            const name_copy = gpa.dupe(u8, name) catch continue;
            const remote_copy = gpa.dupe(u8, remote) catch {
                gpa.free(name_copy);
                continue;
            };
            u.branches.append(gpa, .{ .name = name_copy, .remote = remote_copy }) catch {
                gpa.free(name_copy);
                gpa.free(remote_copy);
                continue;
            };
            seen.put(gpa, name_copy, {}) catch {};
            o.addItem(.{
                .title = o.arena().dupe(u8, if (remote.len > 0) ref[prefix.len..] else name) catch continue,
                .detail = if (remote.len > 0) "remote branch" else "branch",
                .payload = .{ .value = u.branches.items.len },
            }) catch {};
        }
    }
}

fn openMenu(ws: *Workspace) void {
    const gpa = ws.gpa;
    ws.openList(.list, gpa.dupe(u8, "Source Control") catch return, tag_menu, &.{});
    const o = ws.overlay orelse return;
    const items = [_][]const u8{
        "Commit",
        "Commit (Amend)",
        "Pull",
        "Push",
        "Fetch",
        "Stage All Changes",
        "Unstage All Changes",
        "Discard All Changes",
        "Checkout to\u{2026}",
        "Create Branch\u{2026}",
        "Stash\u{2026}",
        "Pop Latest Stash",
        "Show Git Graph",
        "Open Git Status (magit)",
    };
    for (items, 0..) |t, i| o.addItem(.{ .title = o.arena().dupe(u8, t) catch continue, .payload = .{ .value = i } }) catch {};
}

fn menuAction(ws: *Workspace, index: usize) void {
    const u = ui_(ws);
    switch (index) {
        0 => commit(ws),
        1 => {
            u.amend = true;
            commit(ws);
        },
        2 => run(ws, &.{"pull"}, "Pulling", "Pulled", true),
        3 => push(ws),
        4 => run(ws, &.{ "fetch", "--prune" }, "Fetching", "Fetched", true),
        5 => stageGroup(ws, .changes),
        6 => unstageAll(ws),
        7 => discardAll(ws),
        8 => pickBranch(ws),
        9 => ws.openPrompt("New branch name", tag_branch_name, ""),
        10 => ws.openPrompt("Stash message (optional)", tag_stash_message, ""),
        11 => run(ws, &.{ "stash", "pop" }, "Popping the stash", "Stash applied", false),
        12 => graph_view.open(ws),
        else => @import("git_status_ui.zig").open(ws),
    }
}

/// A list's answer. Returns false for other lists.
pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item, query: []const u8) bool {
    switch (tag) {
        tag_confirm => {
            answer(ws, item != null and item.?.payload.value == 0);
            return true;
        },
        tag_menu => {
            if (item) |it| menuAction(ws, it.payload.value);
            return true;
        },
        tag_branch_pick => {
            const u = ui_(ws);
            const it = item orelse {
                // Typed text that isn't in the list names a branch to create.
                if (query.len > 0) createBranch(ws, query);
                return true;
            };
            const v = it.payload.value;
            if (v == 0) {
                ws.openPrompt("New branch name", tag_branch_name, query);
                return true;
            }
            if (v - 1 >= u.branches.items.len) return true;
            const b = u.branches.items[v - 1];
            if (b.remote.len > 0) {
                var buf: [256]u8 = undefined;
                const spec = std.fmt.bufPrint(&buf, "{s}/{s}", .{ b.remote, b.name }) catch return true;
                scm_state.run(ws, &.{ "checkout", "--track", spec }, .{ .done = "Checked out" });
            } else scm_state.run(ws, &.{ "checkout", b.name }, .{ .done = "Checked out" });
            return true;
        },
        else => return false,
    }
}

fn createBranch(ws: *Workspace, name: []const u8) void {
    const trimmed = std.mem.trim(u8, name, " \t");
    if (trimmed.len == 0) return;
    scm_state.run(ws, &.{ "checkout", "-b", trimmed }, .{ .done = "Branch created" });
}

/// A prompt's answer. Returns false for other prompts.
pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    switch (tag) {
        tag_branch_name => {
            createBranch(ws, input);
            return true;
        },
        tag_stash_message => {
            const msg = std.mem.trim(u8, input, " \t");
            if (msg.len > 0) {
                scm_state.run(ws, &.{ "stash", "push", "-u", "-m", msg }, .{ .done = "Changes stashed" });
            } else scm_state.run(ws, &.{ "stash", "push", "-u" }, .{ .done = "Changes stashed" });
            return true;
        },
        else => return false,
    }
}
