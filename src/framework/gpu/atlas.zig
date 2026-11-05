//! Single-channel texture atlas for glyph coverage masks, packed with a
//! simple shelf allocator.

const std = @import("std");
const gl = @import("gl.zig");

pub const Region = struct { x: u32, y: u32, w: u32, h: u32 };

pub const Atlas = struct {
    texture: gl.GLuint = 0,
    size: u32,
    shelf_x: u32 = 0,
    shelf_y: u32 = 0,
    shelf_h: u32 = 0,
    /// Bumped whenever the atlas is cleared, so caches know their regions are gone.
    generation: u32 = 0,

    const padding = 1;
    pub const max_size = 8192;

    pub fn init(size: u32) Atlas {
        var self: Atlas = .{ .size = size };
        self.createTexture();
        return self;
    }

    fn createTexture(self: *Atlas) void {
        if (self.texture != 0) gl.p.DeleteTextures(1, @ptrCast(&self.texture));
        gl.p.GenTextures(1, @ptrCast(&self.texture));
        gl.p.BindTexture(gl.TEXTURE_2D, self.texture);
        gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.p.PixelStorei(gl.UNPACK_ALIGNMENT, 1);
        const s: gl.GLsizei = @intCast(self.size);
        gl.p.TexImage2D(gl.TEXTURE_2D, 0, @intCast(gl.R8), s, s, 0, gl.RED, gl.UNSIGNED_BYTE, null);
    }

    pub fn deinit(self: *Atlas) void {
        if (self.texture != 0) gl.p.DeleteTextures(1, @ptrCast(&self.texture));
    }

    /// Returns null when full; callers then `reset` (possibly growing) and
    /// re-rasterize what the current frame needs.
    pub fn reserve(self: *Atlas, w: u32, h: u32) ?Region {
        if (w + padding > self.size or h + padding > self.size) return null;
        if (self.shelf_x + w + padding > self.size) {
            self.shelf_y += self.shelf_h;
            self.shelf_x = 0;
            self.shelf_h = 0;
        }
        if (self.shelf_y + h + padding > self.size) return null;
        const region: Region = .{ .x = self.shelf_x, .y = self.shelf_y, .w = w, .h = h };
        self.shelf_x += w + padding;
        self.shelf_h = @max(self.shelf_h, h + padding);
        return region;
    }

    pub fn upload(self: *Atlas, region: Region, pixels: []const u8, pitch: u32) void {
        if (region.w == 0 or region.h == 0) return;
        gl.p.BindTexture(gl.TEXTURE_2D, self.texture);
        gl.p.PixelStorei(gl.UNPACK_ALIGNMENT, 1);
        gl.p.PixelStorei(gl.UNPACK_ROW_LENGTH, @intCast(pitch));
        gl.p.TexSubImage2D(gl.TEXTURE_2D, 0, @intCast(region.x), @intCast(region.y), @intCast(region.w), @intCast(region.h), gl.RED, gl.UNSIGNED_BYTE, pixels.ptr);
        gl.p.PixelStorei(gl.UNPACK_ROW_LENGTH, 0);
    }

    /// Forget all regions; grows the texture if a single frame overflowed it.
    pub fn reset(self: *Atlas, grow: bool) void {
        if (grow and self.size < max_size) {
            self.size *= 2;
        }
        self.createTexture();
        self.shelf_x = 0;
        self.shelf_y = 0;
        self.shelf_h = 0;
        self.generation +%= 1;
    }
};
