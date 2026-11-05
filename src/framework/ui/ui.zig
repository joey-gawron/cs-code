//! Drawing helpers shared by the workspace chrome.

const std = @import("std");
const scene_mod = @import("../gpu/scene.zig");
const text_mod = @import("../text/text_system.zig");
const theme_mod = @import("theme.zig");
const platform = @import("../platform/platform.zig");
const icons = @import("../gpu/icons.zig");

pub const IconShape = icons.Shape;

pub const Scene = scene_mod.Scene;
pub const Rect = scene_mod.Rect;
pub const Color = scene_mod.Color;
pub const FontId = text_mod.FontId;
pub const TextSystem = text_mod.TextSystem;
pub const Theme = theme_mod.Theme;

pub const Fonts = struct {
    ui: FontId,
    ui_small: FontId,
    ui_bold: FontId,
    mono: FontId,
};

pub const Ctx = struct {
    scene: *Scene,
    text: *TextSystem,
    theme: *const Theme,
    fonts: Fonts,
    mouse: platform.Vec2,

    pub fn hovered(self: *const Ctx, r: Rect) bool {
        return r.contains(self.mouse.x, self.mouse.y);
    }

    /// Draws text vertically centered in `r`, starting at r.x. Returns its width.
    pub fn label(self: *const Ctx, r: Rect, text: []const u8, font: FontId, color: Color) f32 {
        const m = self.text.metrics(font);
        const baseline = r.y + @round((r.h - (m.ascent + m.descent)) / 2 + m.ascent);
        return self.text.drawText(self.scene, text, font, r.x, baseline, color);
    }

    /// Like `label` but truncates with an ellipsis to fit `r.w`.
    pub fn labelFit(self: *const Ctx, r: Rect, text: []const u8, font: FontId, color: Color) f32 {
        const w = self.text.measure(text, font);
        if (w <= r.w) return self.label(r, text, font, color);
        const ell = "…";
        const ell_w = self.text.measure(ell, font);
        const layout = self.text.layoutLine(text, font);
        const cut = layout.indexContainingX(@max(0, r.w - ell_w));
        var buf: [512]u8 = undefined;
        const n = @min(cut, buf.len - ell.len);
        @memcpy(buf[0..n], text[0..n]);
        @memcpy(buf[n..][0..ell.len], ell);
        return self.label(r, buf[0 .. n + ell.len], font, color);
    }

    pub fn labelCentered(self: *const Ctx, r: Rect, text: []const u8, font: FontId, color: Color) void {
        const w = self.text.measure(text, font);
        _ = self.label(.{ .x = r.x + @round((r.w - w) / 2), .y = r.y, .w = w, .h = r.h }, text, font, color);
    }

    pub fn labelRight(self: *const Ctx, r: Rect, text: []const u8, font: FontId, color: Color) f32 {
        const w = self.text.measure(text, font);
        _ = self.label(.{ .x = r.right() - w, .y = r.y, .w = w, .h = r.h }, text, font, color);
        return w;
    }

    /// Soft drop shadow approximated with stacked translucent rounded rects.
    pub fn shadow(self: *const Ctx, r: Rect, radius: f32, spread: f32) void {
        const steps = 6;
        var i: usize = 0;
        while (i < steps) : (i += 1) {
            const t = @as(f32, @floatFromInt(i + 1)) / steps;
            const grow = spread * t;
            const alpha = self.theme.overlay_shadow.a * (1 - t) * 0.35;
            self.scene.quad(.{ .x = r.x - grow, .y = r.y - grow * 0.5, .w = r.w + grow * 2, .h = r.h + grow * 2 }, .{
                .color = self.theme.overlay_shadow.withAlpha(alpha),
                .radius = radius + grow,
            });
        }
    }

    /// A simple folder glyph (body + tab), `open` draws it outlined.
    pub fn folderIcon(self: *const Ctx, x: f32, cy: f32, color: Color, open: bool) void {
        const w: f32 = 14;
        const h: f32 = 10;
        const y = @round(cy - h / 2 + 1);
        self.scene.quad(.{ .x = x, .y = y - 2, .w = 6, .h = 3 }, .{ .color = color, .radii = .{ 1, 1, 0, 0 } });
        if (open) {
            self.scene.quad(.{ .x = x, .y = y, .w = w, .h = h }, .{ .border_color = color, .border_width = 1.25, .radius = 1.5 });
        } else {
            self.scene.quad(.{ .x = x, .y = y, .w = w, .h = h }, .{ .color = color, .radius = 1.5 });
        }
    }

    /// A small "document" glyph for files.
    pub fn fileIcon(self: *const Ctx, x: f32, cy: f32, color: Color) void {
        const w: f32 = 10;
        const h: f32 = 13;
        const y = @round(cy - h / 2);
        self.scene.quad(.{ .x = x + 2, .y = y, .w = w, .h = h }, .{ .border_color = color, .border_width = 1.25, .radius = 1.5 });
        self.scene.fill(.{ .x = x + 4, .y = y + 4, .w = w - 4, .h = 1 }, color.withAlpha(color.a * 0.6));
        self.scene.fill(.{ .x = x + 4, .y = y + 7, .w = w - 4, .h = 1 }, color.withAlpha(color.a * 0.6));
    }

    /// Disclosure chevron: pointing right (collapsed) or down (expanded).
    pub fn chevron(self: *const Ctx, cx: f32, cy: f32, expanded: bool, color: Color) void {
        const glyph = if (expanded) "▾" else "▸";
        const w = self.text.measure(glyph, self.fonts.ui);
        _ = self.label(.{ .x = @round(cx - w / 2), .y = cy - 10, .w = w, .h = 20 }, glyph, self.fonts.ui, color);
    }

    /// An "×" close glyph drawn with two small quads per pixel row.
    pub fn closeIcon(self: *const Ctx, cx: f32, cy: f32, size: f32, color: Color) void {
        const steps: usize = @intFromFloat(size);
        const half = size / 2;
        var i: usize = 0;
        while (i < steps) : (i += 1) {
            const t = @as(f32, @floatFromInt(i));
            self.scene.fill(.{ .x = cx - half + t, .y = cy - half + t, .w = 1.5, .h = 1.5 }, color);
            self.scene.fill(.{ .x = cx + half - t - 1.5, .y = cy - half + t, .w = 1.5, .h = 1.5 }, color);
        }
    }

    /// A vector icon (icons.zig) centred on (cx, cy), `size` logical px square, anti-aliased at the device scale.
    pub fn icon(self: *const Ctx, shape: IconShape, cx: f32, cy: f32, size: f32, color: Color) void {
        const s = self.scene.scale;
        const px: u32 = @intFromFloat(@max(1, @round(size * s)));
        const region = self.text.iconRegion(shape, px) orelse return;
        const half = @as(f32, @floatFromInt(px)) / 2;
        self.scene.sprite(.{ .x = @round(cx * s - half), .y = @round(cy * s - half), .w = @floatFromInt(px), .h = @floatFromInt(px) }, .{
            .x = @floatFromInt(region.x),
            .y = @floatFromInt(region.y),
            .w = @floatFromInt(region.w),
            .h = @floatFromInt(region.h),
        }, color);
    }

    pub fn dot(self: *const Ctx, cx: f32, cy: f32, r: f32, color: Color) void {
        self.scene.quad(.{ .x = cx - r, .y = cy - r, .w = r * 2, .h = r * 2 }, .{ .color = color, .radius = r });
    }
};
