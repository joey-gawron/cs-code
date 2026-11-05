//! Tiny PNG codec: an encoder (uncompressed deflate blocks) for screenshots
//! and a decoder for the app icon's PNGs (8-bit RGB/RGBA only).

const std = @import("std");
const Allocator = std.mem.Allocator;

const signature = "\x89PNG\r\n\x1a\n";

/// Decoded pixels; the caller frees `pixels`.
pub const Image = struct {
    width: u32,
    height: u32,
    /// RGBA8, straight alpha, rows top to bottom.
    pixels: []u8,
};

/// Encodes RGBA8 pixels. `bottom_up` flips rows (glReadPixels order).
pub fn encodeRgba(gpa: Allocator, pixels: []const u8, width: u32, height: u32, bottom_up: bool) ![]u8 {
    // Raw scanlines with a filter byte each.
    const row_len = width * 4 + 1;
    const raw = try gpa.alloc(u8, row_len * height);
    defer gpa.free(raw);
    for (0..height) |y| {
        const src_y = if (bottom_up) height - 1 - y else y;
        raw[y * row_len] = 0;
        @memcpy(raw[y * row_len + 1 ..][0 .. width * 4], pixels[src_y * width * 4 ..][0 .. width * 4]);
    }

    // zlib stream of stored blocks.
    var z: std.ArrayList(u8) = .empty;
    defer z.deinit(gpa);
    try z.appendSlice(gpa, &.{ 0x78, 0x01 });
    var off: usize = 0;
    while (true) {
        const n: usize = @min(raw.len - off, 65535);
        const last = off + n == raw.len;
        try z.append(gpa, if (last) 1 else 0);
        const len16: u16 = @intCast(n);
        try z.appendSlice(gpa, &std.mem.toBytes(std.mem.nativeToLittle(u16, len16)));
        try z.appendSlice(gpa, &std.mem.toBytes(std.mem.nativeToLittle(u16, ~len16)));
        try z.appendSlice(gpa, raw[off..][0..n]);
        off += n;
        if (last) break;
    }
    var a: u32 = 1;
    var b: u32 = 0;
    for (raw) |byte| {
        a = (a + byte) % 65521;
        b = (b + a) % 65521;
    }
    try z.appendSlice(gpa, &std.mem.toBytes(std.mem.nativeToBig(u32, (b << 16) | a)));

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    try out.appendSlice(gpa, signature);
    var ihdr: [13]u8 = undefined;
    std.mem.writeInt(u32, ihdr[0..4], width, .big);
    std.mem.writeInt(u32, ihdr[4..8], height, .big);
    ihdr[8] = 8; // bit depth
    ihdr[9] = 6; // RGBA
    ihdr[10] = 0;
    ihdr[11] = 0;
    ihdr[12] = 0;
    try chunk(gpa, &out, "IHDR", &ihdr);
    try chunk(gpa, &out, "IDAT", z.items);
    try chunk(gpa, &out, "IEND", "");
    return out.toOwnedSlice(gpa);
}

fn chunk(gpa: Allocator, out: *std.ArrayList(u8), kind: *const [4]u8, data: []const u8) !void {
    var len: [4]u8 = undefined;
    std.mem.writeInt(u32, &len, @intCast(data.len), .big);
    try out.appendSlice(gpa, &len);
    try out.appendSlice(gpa, kind);
    try out.appendSlice(gpa, data);
    var crc = std.hash.Crc32.init();
    crc.update(kind);
    crc.update(data);
    var crc_bytes: [4]u8 = undefined;
    std.mem.writeInt(u32, &crc_bytes, crc.final(), .big);
    try out.appendSlice(gpa, &crc_bytes);
}

/// Decodes a non-interlaced PNG with 8-bit RGB or RGBA pixels (color types 2
/// and 6, what rsvg-convert writes). Palette, grayscale, 16-bit and
/// interlaced images fail with error.PngUnsupported; malformed ones (bad
/// chunk lengths or CRCs, too little pixel data) with the other errors.
/// Meant for bundled assets (the embedded icons, which `zig build test`
/// decodes): the CRCs only catch accidental damage, and Zig 0.16's inflate
/// can panic on a corrupt deflate stream.
pub fn decodeRgba(gpa: Allocator, bytes: []const u8) !Image {
    if (!std.mem.startsWith(u8, bytes, signature)) return error.NotPng;
    var header: ?Header = null;
    var idat: std.ArrayList(u8) = .empty;
    defer idat.deinit(gpa);
    var pos: usize = signature.len;
    while (true) {
        // Length, type, data, CRC of type and data.
        if (bytes.len - pos < 12) return error.PngTruncated;
        const len = std.mem.readInt(u32, bytes[pos..][0..4], .big);
        if (len > bytes.len - pos - 12) return error.PngTruncated;
        const kind = bytes[pos + 4 ..][0..4];
        const data = bytes[pos + 8 ..][0..len];
        const crc = std.mem.readInt(u32, bytes[pos + 8 + len ..][0..4], .big);
        if (std.hash.Crc32.hash(bytes[pos + 4 .. pos + 8 + len]) != crc) return error.PngBadCrc;
        pos += 12 + len;
        if (std.mem.eql(u8, kind, "IHDR")) {
            header = try Header.parse(data);
        } else if (std.mem.eql(u8, kind, "IDAT")) {
            try idat.appendSlice(gpa, data);
        } else if (std.mem.eql(u8, kind, "IEND")) {
            break;
        }
    }
    const h = header orelse return error.PngBadHeader;

    // Each row is a filter byte and the row's bytes. Deflate expands at most
    // 1032:1, so a header claiming more than the data can hold is rejected
    // before allocating for it.
    const bpp: usize = if (h.alpha) 4 else 3;
    const row_len = try std.math.add(usize, try std.math.mul(usize, h.width, bpp), 1);
    const raw_len = try std.math.mul(usize, row_len, h.height);
    if (raw_len / 1032 > idat.items.len) return error.PngTruncated;
    const raw = try gpa.alloc(u8, raw_len);
    defer gpa.free(raw);
    {
        const window = try gpa.alloc(u8, std.compress.flate.max_window_len);
        defer gpa.free(window);
        var input: std.Io.Reader = .fixed(idat.items);
        var inflate: std.compress.flate.Decompress = .init(&input, .zlib, window);
        inflate.reader.readSliceAll(raw) catch |err| switch (err) {
            error.ReadFailed => return inflate.err orelse error.ReadFailed,
            error.EndOfStream => return error.PngTruncated,
        };
    }

    const pixels = try gpa.alloc(u8, @as(usize, h.width) * h.height * 4);
    errdefer gpa.free(pixels);
    var prior: []const u8 = &.{};
    for (0..h.height) |y| {
        const line = raw[y * row_len ..][0..row_len];
        const row = line[1..];
        try unfilter(line[0], row, prior, bpp);
        prior = row;
        for (0..h.width) |x| {
            const src = row[x * bpp ..][0..bpp];
            pixels[(y * h.width + x) * 4 ..][0..4].* = .{ src[0], src[1], src[2], if (h.alpha) src[3] else 255 };
        }
    }
    return .{ .width = h.width, .height = h.height, .pixels = pixels };
}

const Header = struct {
    width: u32,
    height: u32,
    alpha: bool,

    fn parse(data: []const u8) !Header {
        if (data.len != 13) return error.PngBadHeader;
        const width = std.mem.readInt(u32, data[0..4], .big);
        const height = std.mem.readInt(u32, data[4..8], .big);
        if (width == 0 or height == 0) return error.PngBadHeader;
        // Compression and filter method 0 are the only ones defined.
        if (data[10] != 0 or data[11] != 0) return error.PngBadHeader;
        // Bit depth 8, color type 2 (RGB) or 6 (RGBA), no interlacing.
        if (data[8] != 8 or (data[9] != 2 and data[9] != 6) or data[12] != 0) return error.PngUnsupported;
        return .{ .width = width, .height = height, .alpha = data[9] == 6 };
    }
};

/// Undoes a row's filter in place. `prior` is the row above, already
/// unfiltered (empty for the first row); `bpp` is bytes per pixel.
fn unfilter(filter: u8, row: []u8, prior: []const u8, bpp: usize) !void {
    for (0..row.len) |i| {
        const left: u8 = if (i >= bpp) row[i - bpp] else 0;
        const up: u8 = if (prior.len > 0) prior[i] else 0;
        const up_left: u8 = if (prior.len > 0 and i >= bpp) prior[i - bpp] else 0;
        row[i] +%= switch (filter) {
            0 => 0,
            1 => left,
            2 => up,
            3 => @intCast((@as(u9, left) + up) / 2),
            4 => paeth(left, up, up_left),
            else => return error.PngBadFilter,
        };
    }
}

/// The neighbour closest to left + up - up_left.
fn paeth(left: u8, up: u8, up_left: u8) u8 {
    const p = @as(i16, left) + up - up_left;
    const dl = @abs(p - left);
    const du = @abs(p - up);
    const dul = @abs(p - up_left);
    if (dl <= du and dl <= dul) return left;
    if (du <= dul) return up;
    return up_left;
}

test "png round trip" {
    const gpa = std.testing.allocator;
    // 3×2 RGBA, written bottom up (glReadPixels order).
    const pixels = [_]u8{
        1,  2,  3,  255, 40,  50,  60,  128, 70,  80,  90,  0,
        10, 20, 30, 40,  200, 210, 220, 230, 255, 255, 255, 255,
    };
    const file = try encodeRgba(gpa, &pixels, 3, 2, true);
    defer gpa.free(file);
    const image = try decodeRgba(gpa, file);
    defer gpa.free(image.pixels);
    try std.testing.expectEqual(@as(u32, 3), image.width);
    try std.testing.expectEqual(@as(u32, 2), image.height);
    try std.testing.expectEqualSlices(u8, pixels[12..], image.pixels[0..12]);
    try std.testing.expectEqualSlices(u8, pixels[0..12], image.pixels[12..]);
}

test "png decodes rgb with filters" {
    const gpa = std.testing.allocator;
    // 3×2 RGB from ImageMagick: row 0 unfiltered, row 1 with filter 3 (average).
    const file = "\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR\x00\x00\x00\x03\x00\x00\x00\x02\x08\x02\x00\x00\x00\x12\x16\xf1\x4d" ++
        "\x00\x00\x00\x1cIDAT\x08\xd7\x63\xf8\xcf\xc0\xc0\xf0\x9f\x81\x81\xe1\x3f\xf3\x44\x05\x83\xef\x05\xcf\x19\x19\x1b\x01\x40\x8a\x06\xb3\x44\xd2\x95\x52" ++
        "\x00\x00\x00\x00IEND\xae\x42\x60\x82";
    const image = try decodeRgba(gpa, file);
    defer gpa.free(image.pixels);
    try std.testing.expectEqualSlices(u8, &.{
        0xff, 0,    0,    255, 0,    0xff, 0,    255, 0,    0,    0xff, 255,
        0x10, 0x20, 0x30, 255, 0xff, 0xff, 0xff, 255, 0x80, 0x80, 0x80, 255,
    }, image.pixels);
}

test "png rejects malformed files" {
    const gpa = std.testing.allocator;
    const pixels: [16 * 16 * 4]u8 = @splat(7);
    const file = try encodeRgba(gpa, &pixels, 16, 16, false);
    defer gpa.free(file);
    try std.testing.expectError(error.NotPng, decodeRgba(gpa, "GIF89a garbage"));
    try std.testing.expectError(error.PngTruncated, decodeRgba(gpa, file[0 .. file.len / 2]));
    // A flipped pixel byte inside IDAT.
    const corrupt = try gpa.dupe(u8, file);
    defer gpa.free(corrupt);
    corrupt[file.len / 2] ^= 0xff;
    try std.testing.expectError(error.PngBadCrc, decodeRgba(gpa, corrupt));
    // A header one row taller than the pixel data, with its CRC fixed.
    const tall = try gpa.dupe(u8, file);
    defer gpa.free(tall);
    std.mem.writeInt(u32, tall[20..24], 17, .big);
    std.mem.writeInt(u32, tall[29..33], std.hash.Crc32.hash(tall[12..29]), .big);
    try std.testing.expectError(error.PngTruncated, decodeRgba(gpa, tall));
    // A header that claims far more pixels (16384×16384) than the data holds.
    var huge: std.ArrayList(u8) = .empty;
    defer huge.deinit(gpa);
    try huge.appendSlice(gpa, signature);
    try chunk(gpa, &huge, "IHDR", &.{ 0, 0, 0x40, 0, 0, 0, 0x40, 0, 8, 6, 0, 0, 0 });
    try chunk(gpa, &huge, "IEND", "");
    try std.testing.expectError(error.PngTruncated, decodeRgba(gpa, huge.items));
}
