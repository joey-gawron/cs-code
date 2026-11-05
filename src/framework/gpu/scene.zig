//! A frame's worth of GPU primitives, in device pixels (GPUI's `Scene`).
//!
//! UI code draws in logical pixels; the scene scales to device pixels as
//! primitives are recorded so the renderer never has to think about scale.
//! Primitives are grouped into layers: within a layer quads are drawn before
//! glyphs, and layers are drawn in order (overlays push a new layer).

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Color = struct {
    r: f32 = 0,
    g: f32 = 0,
    b: f32 = 0,
    a: f32 = 1,

    pub const transparent: Color = .{ .a = 0 };
    pub const white: Color = .{ .r = 1, .g = 1, .b = 1 };
    pub const black: Color = .{};

    pub fn hex(rgb: u24) Color {
        return .{
            .r = @as(f32, @floatFromInt((rgb >> 16) & 0xff)) / 255.0,
            .g = @as(f32, @floatFromInt((rgb >> 8) & 0xff)) / 255.0,
            .b = @as(f32, @floatFromInt(rgb & 0xff)) / 255.0,
        };
    }

    pub fn hexa(rgb: u24, alpha: f32) Color {
        var col = hex(rgb);
        col.a = alpha;
        return col;
    }

    pub fn withAlpha(self: Color, alpha: f32) Color {
        var col = self;
        col.a = alpha;
        return col;
    }

    /// Linear interpolation in sRGB space (good enough for UI tints).
    pub fn mix(a: Color, b: Color, t: f32) Color {
        return .{
            .r = a.r + (b.r - a.r) * t,
            .g = a.g + (b.g - a.g) * t,
            .b = a.b + (b.b - a.b) * t,
            .a = a.a + (b.a - a.a) * t,
        };
    }

    pub fn lighten(self: Color, amount: f32) Color {
        return mix(self, .{ .r = 1, .g = 1, .b = 1, .a = self.a }, amount);
    }

    pub fn darken(self: Color, amount: f32) Color {
        return mix(self, .{ .r = 0, .g = 0, .b = 0, .a = self.a }, amount);
    }

    fn arr(self: Color) [4]f32 {
        return .{ self.r, self.g, self.b, self.a };
    }
};

pub const Rect = struct {
    x: f32 = 0,
    y: f32 = 0,
    w: f32 = 0,
    h: f32 = 0,

    pub fn right(r: Rect) f32 {
        return r.x + r.w;
    }
    pub fn bottom(r: Rect) f32 {
        return r.y + r.h;
    }
    pub fn contains(r: Rect, px: f32, py: f32) bool {
        return px >= r.x and py >= r.y and px < r.x + r.w and py < r.y + r.h;
    }
    pub fn intersect(a: Rect, b: Rect) Rect {
        const x0 = @max(a.x, b.x);
        const y0 = @max(a.y, b.y);
        const x1 = @min(a.right(), b.right());
        const y1 = @min(a.bottom(), b.bottom());
        return .{ .x = x0, .y = y0, .w = @max(0, x1 - x0), .h = @max(0, y1 - y0) };
    }
    pub fn inset(r: Rect, dx: f32, dy: f32) Rect {
        return .{ .x = r.x + dx, .y = r.y + dy, .w = @max(0, r.w - 2 * dx), .h = @max(0, r.h - 2 * dy) };
    }
    pub fn scaled(r: Rect, s: f32) Rect {
        return .{ .x = r.x * s, .y = r.y * s, .w = r.w * s, .h = r.h * s };
    }
};

/// GPU instance layout for a rounded, bordered rectangle.
pub const Quad = extern struct {
    bounds: [4]f32,
    color: [4]f32,
    border_color: [4]f32,
    /// top-left, top-right, bottom-right, bottom-left
    corner_radii: [4]f32,
    /// top, right, bottom, left
    border_widths: [4]f32,
    clip: [4]f32,
};

/// GPU instance layout for an alpha-mask sprite from the glyph atlas.
pub const Sprite = extern struct {
    bounds: [4]f32,
    uv: [4]f32,
    color: [4]f32,
    clip: [4]f32,
};

pub const Layer = struct {
    quads: std.ArrayList(Quad) = .empty,
    sprites: std.ArrayList(Sprite) = .empty,
};

pub const QuadStyle = struct {
    color: Color = .transparent,
    border_color: Color = .transparent,
    border_width: f32 = 0,
    /// Per-side override: top, right, bottom, left (logical px).
    border_widths: ?[4]f32 = null,
    radius: f32 = 0,
    /// Per-corner override: tl, tr, br, bl (logical px).
    radii: ?[4]f32 = null,
};

pub const Scene = struct {
    gpa: Allocator,
    layers: std.ArrayList(Layer) = .empty,
    layer_count: usize = 0,
    current: usize = 0,
    layer_stack: std.ArrayList(usize) = .empty,
    clip_stack: std.ArrayList(Rect) = .empty,
    scale: f32 = 1,
    /// Device-pixel viewport.
    width: f32 = 0,
    height: f32 = 0,

    pub fn init(gpa: Allocator) Scene {
        return .{ .gpa = gpa };
    }

    pub fn deinit(self: *Scene) void {
        for (self.layers.items) |*layer| {
            layer.quads.deinit(self.gpa);
            layer.sprites.deinit(self.gpa);
        }
        self.layers.deinit(self.gpa);
        self.layer_stack.deinit(self.gpa);
        self.clip_stack.deinit(self.gpa);
    }

    pub fn begin(self: *Scene, device_width: f32, device_height: f32, scale: f32) !void {
        for (self.layers.items) |*layer| {
            layer.quads.clearRetainingCapacity();
            layer.sprites.clearRetainingCapacity();
        }
        if (self.layers.items.len == 0) try self.layers.append(self.gpa, .{});
        self.layer_count = 1;
        self.current = 0;
        self.layer_stack.clearRetainingCapacity();
        self.clip_stack.clearRetainingCapacity();
        self.scale = scale;
        self.width = device_width;
        self.height = device_height;
    }

    pub fn activeLayers(self: *const Scene) []const Layer {
        return self.layers.items[0..self.layer_count];
    }

    /// Subsequent primitives draw above everything recorded so far.
    pub fn pushLayer(self: *Scene) !void {
        try self.layer_stack.append(self.gpa, self.current);
        if (self.layer_count == self.layers.items.len) try self.layers.append(self.gpa, .{});
        self.current = self.layer_count;
        self.layer_count += 1;
    }

    pub fn popLayer(self: *Scene) void {
        self.current = self.layer_stack.pop() orelse 0;
    }

    /// Clip subsequent primitives to `rect` (logical px), intersected with the current clip.
    pub fn pushClip(self: *Scene, rect: Rect) !void {
        const device = rect.scaled(self.scale);
        const clipped = if (self.clip_stack.items.len > 0) device.intersect(self.clip_stack.getLast()) else device;
        try self.clip_stack.append(self.gpa, clipped);
    }

    pub fn popClip(self: *Scene) void {
        _ = self.clip_stack.pop();
    }

    pub fn currentClip(self: *const Scene) Rect {
        if (self.clip_stack.items.len > 0) return self.clip_stack.getLast();
        return .{ .x = 0, .y = 0, .w = self.width, .h = self.height };
    }

    /// Current clip in logical pixels.
    pub fn clipRect(self: *const Scene) Rect {
        return self.currentClip().scaled(1 / self.scale);
    }

    fn currentLayer(self: *Scene) *Layer {
        return &self.layers.items[self.current];
    }

    /// Logical-pixel rectangle snapped to device pixels.
    fn snap(self: *const Scene, r: Rect) Rect {
        const s = self.scale;
        const x0 = @round(r.x * s);
        const y0 = @round(r.y * s);
        const x1 = @round((r.x + r.w) * s);
        const y1 = @round((r.y + r.h) * s);
        return .{ .x = x0, .y = y0, .w = x1 - x0, .h = y1 - y0 };
    }

    pub fn quad(self: *Scene, rect: Rect, style: QuadStyle) void {
        if (rect.w <= 0 or rect.h <= 0) return;
        const clip = self.currentClip();
        const dev = self.snap(rect);
        if (dev.intersect(clip).w <= 0 or dev.intersect(clip).h <= 0) return;
        const s = self.scale;
        const radii = style.radii orelse .{ style.radius, style.radius, style.radius, style.radius };
        var borders = style.border_widths orelse .{ style.border_width, style.border_width, style.border_width, style.border_width };
        for (&borders) |*b| b.* = if (b.* > 0) @max(1, @round(b.* * s)) else 0;
        self.currentLayer().quads.append(self.gpa, .{
            .bounds = .{ dev.x, dev.y, dev.w, dev.h },
            .color = style.color.arr(),
            .border_color = style.border_color.arr(),
            .corner_radii = .{ radii[0] * s, radii[1] * s, radii[2] * s, radii[3] * s },
            .border_widths = borders,
            .clip = .{ clip.x, clip.y, clip.w, clip.h },
        }) catch {};
    }

    pub fn fill(self: *Scene, rect: Rect, color: Color) void {
        self.quad(rect, .{ .color = color });
    }

    /// `bounds` in device pixels (glyph placement is already pixel-snapped).
    pub fn sprite(self: *Scene, bounds: Rect, uv: Rect, color: Color) void {
        const clip = self.currentClip();
        if (bounds.x >= clip.right() or bounds.right() <= clip.x or bounds.y >= clip.bottom() or bounds.bottom() <= clip.y) return;
        self.currentLayer().sprites.append(self.gpa, .{
            .bounds = .{ bounds.x, bounds.y, bounds.w, bounds.h },
            .uv = .{ uv.x, uv.y, uv.w, uv.h },
            .color = color.arr(),
            .clip = .{ clip.x, clip.y, clip.w, clip.h },
        }) catch {};
    }
};
