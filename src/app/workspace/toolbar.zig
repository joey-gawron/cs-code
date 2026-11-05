//! The two rows under the window's top edge, as in cs-code: the title row
//! (menus on the left, a Quick Launch box in the middle, the folder's name)
//! and the toolbar (back/forward, file and edit buttons, the run
//! configuration box and Start). The buttons run the editor's own actions;
//! the run configuration box and Start belong to debugger support and only
//! sit in their place until it lands.

const std = @import("std");
const scene_mod = @import("../../framework/gpu/scene.zig");
const platform = @import("../../framework/platform/platform.zig");
const ui = @import("../../framework/ui/ui.zig");
const keymap_mod = @import("../settings/keymap.zig");
const workspace_mod = @import("workspace.zig");

const Workspace = workspace_mod.Workspace;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Action = keymap_mod.Action;

const Icon = enum { back, forward, new_file, open_folder, save, save_all, undo, redo, find };

/// `action` is null for a button whose feature (navigation history) is not built yet.
const Button = struct { icon: Icon, action: ?Action, tip: []const u8 };

/// Groups of buttons; a gap sits between groups.
const groups = [_][]const Button{
    &.{
        .{ .icon = .back, .action = null, .tip = "Navigate Backward" },
        .{ .icon = .forward, .action = null, .tip = "Navigate Forward" },
    },
    &.{
        .{ .icon = .new_file, .action = .new_file, .tip = "New File" },
        .{ .icon = .open_folder, .action = .open_folder, .tip = "Open Folder" },
        .{ .icon = .save, .action = .save, .tip = "Save" },
        .{ .icon = .save_all, .action = .save_all, .tip = "Save All" },
    },
    &.{
        .{ .icon = .undo, .action = .undo, .tip = "Undo" },
        .{ .icon = .redo, .action = .redo, .tip = "Redo" },
    },
    &.{
        .{ .icon = .find, .action = .show_find, .tip = "Find" },
    },
};

const button_w: f32 = 26;
const group_gap: f32 = 8;
const pad: f32 = 8;
const search_h: f32 = 22;

/// The Quick Launch box: centered in the window, but clear of the menus.
fn searchRect(ws: *const Workspace) Rect {
    const r = ws.menubar_rect;
    var menus_right: f32 = 0;
    for (ws.menu_title_rects) |t| menus_right = @max(menus_right, t.right());
    const w = std.math.clamp(r.w * 0.3, 200, 380);
    const x = @max(menus_right + 24, @round((r.w - w) / 2));
    return .{ .x = x, .y = r.y + (r.h - search_h) / 2, .w = @min(w, @max(0, r.w - x - 8)), .h = search_h };
}

fn buttonRect(ws: *const Workspace, group: usize, index: usize) Rect {
    var x = ws.toolbar_rect.x + pad;
    for (groups[0..group]) |g| x += @as(f32, @floatFromInt(g.len)) * button_w + group_gap;
    x += @as(f32, @floatFromInt(index)) * button_w;
    return .{ .x = x, .y = ws.toolbar_rect.y + 2, .w = button_w, .h = ws.toolbar_rect.h - 4 };
}

/// Where the run configuration box starts: after the last button group.
fn configRect(ws: *const Workspace) Rect {
    const last = groups.len - 1;
    const b = buttonRect(ws, last, groups[last].len - 1);
    return .{ .x = b.right() + group_gap + 6, .y = ws.toolbar_rect.y + 4, .w = 150, .h = ws.toolbar_rect.h - 8 };
}

pub fn onMouseDown(ws: *Workspace, m: platform.MouseEvent) bool {
    if (!ws.menu_visible or ws.distraction_free) return false;
    if (m.button != .left) return ws.toolbar_rect.contains(m.pos.x, m.pos.y);
    if (ws.menubar_rect.contains(m.pos.x, m.pos.y)) {
        if (searchRect(ws).contains(m.pos.x, m.pos.y)) {
            ws.menu_open = null;
            ws.perform(.command_palette);
            return true;
        }
        return false;
    }
    if (!ws.toolbar_rect.contains(m.pos.x, m.pos.y)) return false;
    ws.menu_open = null;
    for (groups, 0..) |g, gi| for (g, 0..) |b, i| {
        if (buttonRect(ws, gi, i).contains(m.pos.x, m.pos.y)) {
            if (b.action) |a| ws.perform(a);
            return true;
        }
    };
    return true;
}

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx) void {
    if (!ws.menu_visible or ws.distraction_free) return;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const font = ws.fonts.ui;

    // Title row: Quick Launch and the folder's name.
    const sr = searchRect(ws);
    if (sr.w > 60) {
        scene.quad(sr, .{ .color = theme.input_background, .border_color = if (ctx.hovered(sr)) theme.input_border else theme.input_background, .border_width = 1, .radius = 3 });
        drawMagnifier(ctx, sr.x + 12, sr.y + sr.h / 2, theme.list_foreground_dim);
        _ = ctx.labelFit(.{ .x = sr.x + 26, .y = sr.y, .w = sr.w - 34, .h = sr.h }, "Search (Ctrl+Shift+P)", font, theme.list_foreground_dim);
        if (ws.project) |p| {
            const w = ws.env.text.measure(p.name, font) + 20;
            const chip: Rect = .{ .x = sr.right() + 10, .y = sr.y, .w = w, .h = sr.h };
            if (chip.right() < ws.menubar_rect.right() - 8) {
                scene.quad(chip, .{ .color = theme.input_background, .radius = 3 });
                ctx.labelCentered(chip, p.name, font, theme.menubar_foreground);
            }
        }
    }

    // Toolbar.
    const tb = ws.toolbar_rect;
    scene.fill(tb, theme.menubar_background);
    const icon_color = Color.hex(0xc5c5c5);
    for (groups, 0..) |g, gi| for (g, 0..) |b, i| {
        const r = buttonRect(ws, gi, i);
        if (ctx.hovered(r)) scene.quad(r, .{ .color = theme.sidebar_hover, .radius = 3 });
        drawIcon(ctx, b.icon, r.x + r.w / 2, r.y + r.h / 2, icon_color);
    };
    const cr = configRect(ws);
    scene.quad(cr, .{ .color = theme.menubar_background, .border_color = theme.input_border, .border_width = 1, .radius = 2 });
    _ = ctx.labelFit(.{ .x = cr.x + 6, .y = cr.y, .w = cr.w - 28, .h = cr.h }, "No configurations", ws.fonts.ui_small, theme.list_foreground_dim);
    drawChevronDown(ctx, cr.right() - 13, cr.y + cr.h / 2, theme.menubar_foreground);
    const start_x = cr.right() + 14;
    drawPlay(ctx, start_x + 4, tb.y + tb.h / 2, Color.hex(0x6cc667), true);
    const sw = ctx.label(.{ .x = start_x + 18, .y = tb.y, .w = 60, .h = tb.h }, "Start", font, theme.menubar_foreground);
    drawPlay(ctx, start_x + 18 + sw + 18, tb.y + tb.h / 2, theme.list_foreground_dim, false);
}

fn drawMagnifier(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color) void {
    ctx.scene.quad(.{ .x = cx - 5, .y = cy - 5, .w = 8, .h = 8 }, .{ .border_color = color, .border_width = 1.25, .radius = 4 });
    for (0..4) |i| {
        const t: f32 = @floatFromInt(i);
        ctx.scene.fill(.{ .x = cx + 2 + t, .y = cy + 2 + t, .w = 1.5, .h = 1.5 }, color);
    }
}

fn drawChevronDown(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color) void {
    for (0..5) |i| {
        const t: f32 = @floatFromInt(i);
        ctx.scene.fill(.{ .x = cx - 4 + t, .y = cy - 2 + t * 0.7, .w = 1.5, .h = 1.5 }, color);
        ctx.scene.fill(.{ .x = cx + 4 - t - 1.5, .y = cy - 2 + t * 0.7, .w = 1.5, .h = 1.5 }, color);
    }
}

/// A right-pointing triangle: filled for Start, an outline for Start Without Debugging.
fn drawPlay(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color, filled: bool) void {
    for (0..9) |i| {
        const t: f32 = @floatFromInt(i);
        const half = @min(t, 8 - t) * 0.9 + 0.5;
        ctx.scene.fill(.{ .x = cx - 4 + t * 0.9, .y = cy - half, .w = 1, .h = half * 2 }, color);
    }
    if (filled) return;
    for (1..8) |i| {
        const t: f32 = @floatFromInt(i);
        const half = @max(0, @min(t, 8 - t) * 0.9 - 1.2);
        if (half > 0) ctx.scene.fill(.{ .x = cx - 4 + t * 0.9 + 0.5, .y = cy - half, .w = 1, .h = half * 2 }, ctx.theme.menubar_background);
    }
}

fn arrow(ctx: *const ui.Ctx, cx: f32, cy: f32, dir: f32, color: Color) void {
    ctx.scene.fill(.{ .x = cx - 6, .y = cy - 0.75, .w = 12, .h = 1.5 }, color);
    for (0..5) |i| {
        const t: f32 = @floatFromInt(i);
        ctx.scene.fill(.{ .x = cx + dir * (6 - t) - 0.75 - (if (dir > 0) @as(f32, 0) else @as(f32, 0)), .y = cy - t - 0.75, .w = 1.5, .h = 1.5 }, color);
        ctx.scene.fill(.{ .x = cx + dir * (6 - t) - 0.75, .y = cy + t - 0.75, .w = 1.5, .h = 1.5 }, color);
    }
}

fn floppy(ctx: *const ui.Ctx, cx: f32, cy: f32, color: Color) void {
    ctx.scene.quad(.{ .x = cx - 6, .y = cy - 6, .w = 12, .h = 12 }, .{ .border_color = color, .border_width = 1.25, .radius = 1.5 });
    ctx.scene.fill(.{ .x = cx - 3, .y = cy - 5, .w = 6, .h = 4 }, color);
    ctx.scene.fill(.{ .x = cx - 3, .y = cy + 1, .w = 6, .h = 4 }, color.withAlpha(color.a * 0.7));
}

/// A counter-clockwise (or clockwise) arrow, as an open arc with a head.
fn curved(ctx: *const ui.Ctx, cx: f32, cy: f32, dir: f32, color: Color) void {
    ctx.scene.quad(.{ .x = cx - 5.5, .y = cy - 4.5, .w = 11, .h = 10 }, .{ .border_color = color, .border_width = 1.5, .radius = 5 });
    // Cut the arc's upper corner on the arrow's side and put the head there.
    ctx.scene.fill(.{ .x = if (dir < 0) cx - 6.5 else cx + 1, .y = cy - 6, .w = 5.5, .h = 5 }, ctx.theme.menubar_background);
    ctx.scene.fill(.{ .x = if (dir < 0) cx - 6 else cx + 1.5, .y = cy - 5, .w = 5, .h = 1.5 }, color);
    ctx.scene.fill(.{ .x = if (dir < 0) cx - 6 else cx + 4.5, .y = cy - 5, .w = 1.5, .h = 5 }, color);
}

fn drawIcon(ctx: *const ui.Ctx, icon: Icon, cx: f32, cy: f32, color: Color) void {
    switch (icon) {
        .back => arrow(ctx, cx, cy, -1, color),
        .forward => arrow(ctx, cx, cy, 1, color),
        .new_file => {
            ctx.fileIcon(cx - 7, cy, color);
            ctx.scene.fill(.{ .x = cx + 3, .y = cy - 8, .w = 1.5, .h = 6 }, Color.hex(0x75beff));
            ctx.scene.fill(.{ .x = cx + 0.75, .y = cy - 5.75, .w = 6, .h = 1.5 }, Color.hex(0x75beff));
        },
        .open_folder => ctx.folderIcon(cx - 7, cy + 1, Color.hex(0xdcb67a), true),
        .save => floppy(ctx, cx, cy, color),
        .save_all => {
            floppy(ctx, cx + 2, cy - 2, color.withAlpha(color.a * 0.6));
            floppy(ctx, cx - 2, cy + 2, color);
        },
        .undo => curved(ctx, cx, cy, -1, color),
        .redo => curved(ctx, cx, cy, 1, color),
        .find => drawMagnifier(ctx, cx - 1, cy - 1, color),
    }
}
