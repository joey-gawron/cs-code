//! Decodes image files (the preview's images) to RGBA8. PNGs go through
//! png.zig; everything else (JPEG, GIF, BMP, TIFF, WebP, PNGs it can't read)
//! through AppKit on macOS.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");
const png = @import("../util/png.zig");
const Allocator = std.mem.Allocator;

pub const Image = png.Image;

pub const Error = error{ UnsupportedImage, OutOfMemory };

/// The image in `bytes`, at most `max_width` pixels wide (wider ones are
/// scaled down). The caller frees `pixels` with `gpa`.
pub fn decode(gpa: Allocator, bytes: []const u8, max_width: u32) Error!Image {
    if (std.mem.startsWith(u8, bytes, "\x89PNG\r\n\x1a\n")) {
        if (png.decodeRgba(gpa, bytes)) |img| {
            return shrink(gpa, img, max_width);
        } else |err| switch (err) {
            error.OutOfMemory => return error.OutOfMemory,
            else => {},
        }
    }
    if (comptime builtin.os.tag.isDarwin()) {
        var w: c_int = 0;
        var h: c_int = 0;
        var rgba: [*c]u8 = null;
        if (c.je_image_decode(bytes.ptr, bytes.len, @intCast(max_width), &w, &h, &rgba) == 0) return error.UnsupportedImage;
        defer std.c.free(rgba);
        const n = @as(usize, @intCast(w)) * @as(usize, @intCast(h)) * 4;
        const pixels = try gpa.dupe(u8, rgba[0..n]);
        return .{ .width = @intCast(w), .height = @intCast(h), .pixels = pixels };
    }
    return error.UnsupportedImage;
}

/// Box-filters `img` down to `max_width` wide (frees the original).
fn shrink(gpa: Allocator, img: Image, max_width: u32) !Image {
    if (max_width == 0 or img.width <= max_width) return img;
    defer gpa.free(img.pixels);
    const tw = max_width;
    const th = @max(1, @as(u32, @intCast(@as(u64, img.height) * tw / img.width)));
    const out = try gpa.alloc(u8, @as(usize, tw) * th * 4);
    errdefer gpa.free(out);
    for (0..th) |y| {
        const y0 = y * img.height / th;
        const y1 = @max(y0 + 1, (y + 1) * img.height / th);
        for (0..tw) |x| {
            const x0 = x * img.width / tw;
            const x1 = @max(x0 + 1, (x + 1) * img.width / tw);
            var sum: [4]u64 = .{ 0, 0, 0, 0 };
            var n: u64 = 0;
            for (y0..y1) |sy| for (x0..x1) |sx| {
                const p = img.pixels[(sy * img.width + sx) * 4 ..][0..4];
                // Weight colour by alpha so transparent pixels do not bleed.
                const a: u64 = p[3];
                sum[0] += p[0] * a;
                sum[1] += p[1] * a;
                sum[2] += p[2] * a;
                sum[3] += a;
                n += 1;
            };
            const o = out[(y * tw + x) * 4 ..][0..4];
            if (sum[3] == 0) {
                o.* = .{ 0, 0, 0, 0 };
            } else {
                o[0] = @intCast(sum[0] / sum[3]);
                o[1] = @intCast(sum[1] / sum[3]);
                o[2] = @intCast(sum[2] / sum[3]);
                o[3] = @intCast(sum[3] / n);
            }
        }
    }
    return .{ .width = tw, .height = th, .pixels = out };
}

test "a wide PNG is scaled down" {
    const gpa = std.testing.allocator;
    var px: [8 * 2 * 4]u8 = undefined;
    for (0..16) |i| px[i * 4 ..][0..4].* = .{ 200, 100, 50, 255 };
    const bytes = try png.encodeRgba(gpa, &px, 8, 2, false);
    defer gpa.free(bytes);
    const img = try decode(gpa, bytes, 4);
    defer gpa.free(img.pixels);
    try std.testing.expectEqual(@as(u32, 4), img.width);
    try std.testing.expectEqual(@as(u32, 1), img.height);
    try std.testing.expectEqual(@as(u8, 200), img.pixels[0]);
    try std.testing.expectEqual(@as(u8, 255), img.pixels[3]);
}
