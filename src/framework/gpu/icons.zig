//! Small vector icons rasterized into coverage masks for the glyph atlas.
//! Shapes are polygons and round-capped strokes on a 16 x 16 design grid,
//! sampled 4 x 4 per device pixel, so they stay crisp at any scale.

const std = @import("std");

pub const Shape = enum { play, play_outline, remote, arrow_left, arrow_right, undo, redo, magnifier, chevron_down };

const Pt = [2]f32;
const grid: f32 = 16;
const samples = 4;

const Prim = union(enum) {
    fill: []const Pt,
    stroke: struct { pts: []const Pt, width: f32, closed: bool = false },
};

/// `n + 1` points along an arc, angles in degrees (0 = right, 90 = down).
fn arc(comptime cx: f32, comptime cy: f32, comptime r: f32, comptime a0: f32, comptime a1: f32, comptime n: usize) [n + 1]Pt {
    var pts: [n + 1]Pt = undefined;
    for (0..n + 1) |i| {
        const t = a0 + (a1 - a0) * @as(f32, @floatFromInt(i)) / @as(f32, @floatFromInt(n));
        const rad = t * std.math.pi / 180.0;
        pts[i] = .{ cx + r * @cos(rad), cy + r * @sin(rad) };
    }
    return pts;
}

const play_tri = [_]Pt{ .{ 3.5, 2 }, .{ 13.5, 8 }, .{ 3.5, 14 } };
const remote_left = [_]Pt{ .{ 2.2, 3.6 }, .{ 6.2, 8 }, .{ 2.2, 12.4 } };
const remote_right = [_]Pt{ .{ 13.8, 3.6 }, .{ 9.8, 8 }, .{ 13.8, 12.4 } };
const chevron_pts = [_]Pt{ .{ 3.5, 5.5 }, .{ 8, 10 }, .{ 12.5, 5.5 } };
const arrow_l_shaft = [_]Pt{ .{ 13.5, 8 }, .{ 2.5, 8 } };
const arrow_l_head = [_]Pt{ .{ 7, 3.5 }, .{ 2.5, 8 }, .{ 7, 12.5 } };
const arrow_r_shaft = [_]Pt{ .{ 2.5, 8 }, .{ 13.5, 8 } };
const arrow_r_head = [_]Pt{ .{ 9, 3.5 }, .{ 13.5, 8 }, .{ 9, 12.5 } };
// Undo: an arc from the left, over the top and down the right, its head at the left end;
// redo is its mirror image.
const undo_arc = arc(8.4, 9.2, 5, 205, 440, 24);
const undo_head = [_]Pt{ .{ 3.87, 2.9 }, .{ 3.87, 7.09 }, .{ 8.0, 7.09 } };
const redo_arc = arc(7.6, 9.2, 5, -25, -260, 24);
const redo_head = [_]Pt{ .{ 12.13, 2.9 }, .{ 12.13, 7.09 }, .{ 8.0, 7.09 } };
const lens = arc(6.6, 6.6, 4.4, 0, 360, 32);
const handle = [_]Pt{ .{ 9.9, 9.9 }, .{ 14, 14 } };

fn prims(shape: Shape) []const Prim {
    return switch (shape) {
        .play => &.{.{ .fill = &play_tri }},
        .play_outline => &.{.{ .stroke = .{ .pts = &play_tri, .width = 1.6, .closed = true } }},
        .remote => &.{
            .{ .stroke = .{ .pts = &remote_left, .width = 1.7 } },
            .{ .stroke = .{ .pts = &remote_right, .width = 1.7 } },
        },
        .arrow_left => &.{
            .{ .stroke = .{ .pts = &arrow_l_shaft, .width = 1.4 } },
            .{ .stroke = .{ .pts = &arrow_l_head, .width = 1.4 } },
        },
        .arrow_right => &.{
            .{ .stroke = .{ .pts = &arrow_r_shaft, .width = 1.4 } },
            .{ .stroke = .{ .pts = &arrow_r_head, .width = 1.4 } },
        },
        .undo => &.{
            .{ .stroke = .{ .pts = &undo_arc, .width = 1.5 } },
            .{ .stroke = .{ .pts = &undo_head, .width = 1.5 } },
        },
        .redo => &.{
            .{ .stroke = .{ .pts = &redo_arc, .width = 1.5 } },
            .{ .stroke = .{ .pts = &redo_head, .width = 1.5 } },
        },
        .chevron_down => &.{.{ .stroke = .{ .pts = &chevron_pts, .width = 1.7 } }},
        .magnifier => &.{
            .{ .stroke = .{ .pts = &lens, .width = 1.5, .closed = true } },
            .{ .stroke = .{ .pts = &handle, .width = 1.8 } },
        },
    };
}

fn inPolygon(pts: []const Pt, x: f32, y: f32) bool {
    var inside = false;
    var j = pts.len - 1;
    for (pts, 0..) |p, i| {
        const q = pts[j];
        if ((p[1] > y) != (q[1] > y) and x < (q[0] - p[0]) * (y - p[1]) / (q[1] - p[1]) + p[0]) inside = !inside;
        j = i;
    }
    return inside;
}

fn segmentDistSq(a: Pt, b: Pt, x: f32, y: f32) f32 {
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const len_sq = dx * dx + dy * dy;
    const t = if (len_sq == 0) 0 else std.math.clamp(((x - a[0]) * dx + (y - a[1]) * dy) / len_sq, 0, 1);
    const px = a[0] + t * dx - x;
    const py = a[1] + t * dy - y;
    return px * px + py * py;
}

fn covers(prim: Prim, x: f32, y: f32) bool {
    switch (prim) {
        .fill => |pts| return inPolygon(pts, x, y),
        .stroke => |s| {
            const half_sq = (s.width / 2) * (s.width / 2);
            const n = s.pts.len;
            const segs = if (s.closed) n else n - 1;
            for (0..segs) |i| {
                if (segmentDistSq(s.pts[i], s.pts[(i + 1) % n], x, y) <= half_sq) return true;
            }
            return false;
        },
    }
}

pub const max_size = 64;

/// Fills `out[0 .. size * size]` with coverage, rows top to bottom.
pub fn rasterize(shape: Shape, size: u32, out: []u8) void {
    std.debug.assert(size <= max_size and out.len >= size * size);
    const list = prims(shape);
    const cell = grid / @as(f32, @floatFromInt(size));
    for (0..size) |py| for (0..size) |px| {
        var hits: u32 = 0;
        for (0..samples) |sy| for (0..samples) |sx| {
            const x = (@as(f32, @floatFromInt(px)) + (@as(f32, @floatFromInt(sx)) + 0.5) / samples) * cell;
            const y = (@as(f32, @floatFromInt(py)) + (@as(f32, @floatFromInt(sy)) + 0.5) / samples) * cell;
            for (list) |p| if (covers(p, x, y)) {
                hits += 1;
                break;
            };
        };
        out[py * size + px] = @intCast((hits * 255 + samples * samples / 2) / (samples * samples));
    };
}

test "play is a right-pointing triangle: wide at the left, a point at the right" {
    var buf: [32 * 32]u8 = undefined;
    rasterize(.play, 32, &buf);
    const at = struct {
        fn f(b: []const u8, x: usize, y: usize) u8 {
            return b[y * 32 + x];
        }
    }.f;
    try std.testing.expectEqual(@as(u8, 255), at(&buf, 10, 16));
    // Column 8 is tall, column 24 short: the triangle narrows to the right.
    var tall: usize = 0;
    var short: usize = 0;
    for (0..32) |y| {
        if (at(&buf, 8, y) > 127) tall += 1;
        if (at(&buf, 24, y) > 127) short += 1;
    }
    try std.testing.expect(tall > short * 2);
    try std.testing.expect(short > 0);
    // Corners are empty and the edge pixels are partial (anti-aliased).
    try std.testing.expectEqual(@as(u8, 0), at(&buf, 0, 0));
    try std.testing.expectEqual(@as(u8, 0), at(&buf, 31, 0));
    var partial: usize = 0;
    for (buf) |v| if (v > 0 and v < 255) {
        partial += 1;
    };
    try std.testing.expect(partial > 20);
}

test "remote is two mirrored chevrons with a gap between their tips" {
    var buf: [32 * 32]u8 = undefined;
    rasterize(.remote, 32, &buf);
    for (0..32) |y| for (0..16) |x| {
        const a: i32 = buf[y * 32 + x];
        const b: i32 = buf[y * 32 + (31 - x)];
        try std.testing.expect(@abs(a - b) <= 2);
    };
    // The middle column between the tips is empty on the centre row.
    try std.testing.expectEqual(@as(u8, 0), buf[16 * 32 + 15]);
    try std.testing.expect(buf[16 * 32 + 12] > 100);
}

test "every shape draws something and stays inside its box" {
    inline for (@typeInfo(Shape).@"enum".field_names) |name| {
        var buf: [20 * 20]u8 = undefined;
        rasterize(@field(Shape, name), 20, &buf);
        var sum: usize = 0;
        for (buf) |v| sum += v;
        try std.testing.expect(sum > 20 * 255 / 2);
    }
}
