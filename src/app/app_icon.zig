//! The app icon as window icons: the PNGs build.zig embeds from
//! assets/icons/hicolor (rendered from the SVG by tools/gen-icons.sh),
//! decoded only while a window is being created.

const std = @import("std");
const platform = @import("../framework/platform/platform.zig");
const png = @import("../framework/util/png.zig");

const Allocator = std.mem.Allocator;

/// The sizes build.zig embeds (`window_icon_sizes`).
const embedded = [_]struct { size: u32, png: []const u8 }{
    .{ .size = 16, .png = @embedFile("icon_16") },
    .{ .size = 32, .png = @embedFile("icon_32") },
    .{ .size = 48, .png = @embedFile("icon_48") },
    .{ .size = 64, .png = @embedFile("icon_64") },
    .{ .size = 128, .png = @embedFile("icon_128") },
};

/// Every embedded size as straight-alpha RGBA. Release with `free`.
pub fn decodeAll(gpa: Allocator) ![]platform.IconImage {
    const icons = try gpa.alloc(platform.IconImage, embedded.len);
    // Empty until decoded, so `free` can take a partly filled list.
    @memset(icons, .{ .size = 0, .rgba = &.{} });
    errdefer free(gpa, icons);
    for (icons, embedded) |*icon, e| {
        const image = try png.decodeRgba(gpa, e.png);
        icon.* = .{ .size = e.size, .rgba = image.pixels };
        if (image.width != e.size or image.height != e.size) return error.IconSizeMismatch;
    }
    return icons;
}

pub fn free(gpa: Allocator, icons: []const platform.IconImage) void {
    for (icons) |icon| gpa.free(icon.rgba);
    gpa.free(icons);
}

test "embedded icons decode" {
    const gpa = std.testing.allocator;
    const icons = try decodeAll(gpa);
    defer free(gpa, icons);
    for (icons) |icon| {
        const n = icon.size;
        try std.testing.expectEqual(@as(usize, n * n * 4), icon.rgba.len);
        // Clear outside the tile's rounded corner, opaque green below the
        // glyph: rsvg-convert uses every filter type, so a wrong one shows.
        try std.testing.expectEqual(@as(u8, 0), icon.rgba[3]);
        const px = icon.rgba[((n * 9 / 10) * n + n / 2) * 4 ..][0..4];
        try std.testing.expectEqual(@as(u8, 255), px[3]);
        try std.testing.expect(px[1] > px[0] + 64 and px[1] > px[2] + 64);
    }
}
