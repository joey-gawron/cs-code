//! Per-frame timing (`CS_FRAME_STATS=1`), the Zig counterpart of the Rust spike's `stats.rs`.
//!
//! `build` is CPU time from the start of the frame to the end of scene recording (layout, shaping,
//! glyph rasterization, quads and sprites): the same span as the Rust "render to end of paint"
//! figure, the one the comparison table quotes. `draw` is the OpenGL submission and `present` is
//! the buffer flush, which on macOS can block waiting for the display; `total` adds all three.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub fn nowNs() u64 {
    var ts: std.c.timespec = undefined;
    _ = std.c.clock_gettime(.MONOTONIC, &ts);
    return @as(u64, @intCast(ts.sec)) * std.time.ns_per_s + @as(u64, @intCast(ts.nsec));
}

pub fn ms(ns: u64) f64 {
    return @as(f64, @floatFromInt(ns)) / 1e6;
}

pub const Frame = struct {
    build: u64 = 0,
    draw: u64 = 0,
    present: u64 = 0,

    pub fn total(f: Frame) u64 {
        return f.build + f.draw + f.present;
    }
};

pub const Summary = struct {
    frames: usize,
    p50_ms: f64,
    p95_ms: f64,
    max_ms: f64,
};

pub const Stats = struct {
    frames: std.ArrayList(Frame) = .empty,

    pub fn record(s: *Stats, gpa: Allocator, f: Frame) void {
        s.frames.append(gpa, f) catch {};
    }

    pub fn deinit(s: *Stats, gpa: Allocator) void {
        s.frames.deinit(gpa);
    }

    /// Summary of one phase across the frames from `skip` on, by nearest rank (as the Rust side).
    pub fn summary(s: *const Stats, gpa: Allocator, skip: usize, comptime field: enum { build, draw, present, total }) Summary {
        if (skip >= s.frames.items.len) return .{ .frames = 0, .p50_ms = 0, .p95_ms = 0, .max_ms = 0 };
        const frames = s.frames.items[skip..];
        const values = gpa.alloc(u64, frames.len) catch return .{ .frames = 0, .p50_ms = 0, .p95_ms = 0, .max_ms = 0 };
        defer gpa.free(values);
        for (frames, values) |f, *v| v.* = switch (field) {
            .build => f.build,
            .draw => f.draw,
            .present => f.present,
            .total => f.total(),
        };
        std.mem.sort(u64, values, {}, std.sort.asc(u64));
        return .{
            .frames = values.len,
            .p50_ms = ms(rank(values, 0.50)),
            .p95_ms = ms(rank(values, 0.95)),
            .max_ms = ms(values[values.len - 1]),
        };
    }
};

fn rank(sorted: []const u64, p: f64) u64 {
    const n: f64 = @floatFromInt(sorted.len);
    const i = std.math.clamp(@as(usize, @intFromFloat(@ceil(p * n))), 1, sorted.len) - 1;
    return sorted[i];
}

test "percentiles by nearest rank" {
    const gpa = std.testing.allocator;
    var stats: Stats = .{};
    defer stats.deinit(gpa);
    for (1..101) |i| stats.record(gpa, .{ .build = i * std.time.ns_per_ms });
    const s = stats.summary(gpa, 0, .build);
    try std.testing.expectEqual(@as(usize, 100), s.frames);
    try std.testing.expectEqual(@as(f64, 50), s.p50_ms);
    try std.testing.expectEqual(@as(f64, 95), s.p95_ms);
    try std.testing.expectEqual(@as(f64, 100), s.max_ms);
}
