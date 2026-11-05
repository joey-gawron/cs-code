//! Counts what the process allocates through its general purpose allocator:
//! live bytes, the peak, and how many allocations are live and have been
//! made. `install` wraps the allocator `main` was given; the Memory view
//! (app/workspace/memory_ui.zig) and the leak and growth checks read the
//! counters. Everything is atomic: language servers, the executor and the
//! remote client allocate from other threads.
//!
//! The wrapper only forwards, so the leak checking of Debug builds (the
//! default allocator reports leaks when `main` returns) keeps working.

const std = @import("std");
const Allocator = std.mem.Allocator;
const Alignment = std.mem.Alignment;
const Atomic = std.atomic.Value;

pub const Counting = struct {
    child: Allocator,
    live_bytes: Atomic(usize) = .init(0),
    peak_bytes: Atomic(usize) = .init(0),
    live_allocs: Atomic(usize) = .init(0),
    total_allocs: Atomic(usize) = .init(0),
    total_bytes: Atomic(usize) = .init(0),

    pub fn init(child: Allocator) Counting {
        return .{ .child = child };
    }

    pub fn allocator(self: *Counting) Allocator {
        return .{ .ptr = self, .vtable = &.{
            .alloc = alloc,
            .resize = resize,
            .remap = remap,
            .free = free,
        } };
    }

    pub const Snapshot = struct {
        live_bytes: usize,
        peak_bytes: usize,
        live_allocs: usize,
        total_allocs: usize,
        total_bytes: usize,
    };

    pub fn snapshot(self: *const Counting) Snapshot {
        return .{
            .live_bytes = self.live_bytes.load(.monotonic),
            .peak_bytes = self.peak_bytes.load(.monotonic),
            .live_allocs = self.live_allocs.load(.monotonic),
            .total_allocs = self.total_allocs.load(.monotonic),
            .total_bytes = self.total_bytes.load(.monotonic),
        };
    }

    /// Makes the peak start from what is live now (a measurement window).
    pub fn resetPeak(self: *Counting) void {
        self.peak_bytes.store(self.live_bytes.load(.monotonic), .monotonic);
    }

    fn grow(self: *Counting, by: usize) void {
        const now = self.live_bytes.fetchAdd(by, .monotonic) + by;
        _ = self.total_bytes.fetchAdd(by, .monotonic);
        var peak = self.peak_bytes.load(.monotonic);
        while (now > peak) peak = self.peak_bytes.cmpxchgWeak(peak, now, .monotonic, .monotonic) orelse break;
    }

    fn shrink(self: *Counting, by: usize) void {
        _ = self.live_bytes.fetchSub(by, .monotonic);
    }

    fn alloc(ctx: *anyopaque, len: usize, alignment: Alignment, ret_addr: usize) ?[*]u8 {
        const self: *Counting = @ptrCast(@alignCast(ctx));
        const p = self.child.rawAlloc(len, alignment, ret_addr) orelse return null;
        self.grow(len);
        _ = self.live_allocs.fetchAdd(1, .monotonic);
        _ = self.total_allocs.fetchAdd(1, .monotonic);
        return p;
    }

    fn resize(ctx: *anyopaque, memory: []u8, alignment: Alignment, new_len: usize, ret_addr: usize) bool {
        const self: *Counting = @ptrCast(@alignCast(ctx));
        if (!self.child.rawResize(memory, alignment, new_len, ret_addr)) return false;
        self.resized(memory.len, new_len);
        return true;
    }

    fn remap(ctx: *anyopaque, memory: []u8, alignment: Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {
        const self: *Counting = @ptrCast(@alignCast(ctx));
        const p = self.child.rawRemap(memory, alignment, new_len, ret_addr) orelse return null;
        self.resized(memory.len, new_len);
        return p;
    }

    fn resized(self: *Counting, old_len: usize, new_len: usize) void {
        if (new_len >= old_len) self.grow(new_len - old_len) else self.shrink(old_len - new_len);
    }

    fn free(ctx: *anyopaque, memory: []u8, alignment: Alignment, ret_addr: usize) void {
        const self: *Counting = @ptrCast(@alignCast(ctx));
        self.child.rawFree(memory, alignment, ret_addr);
        self.shrink(memory.len);
        _ = self.live_allocs.fetchSub(1, .monotonic);
    }
};

/// The counters of the process's allocator, once `install` has run.
var installed: ?*Counting = null;

/// Wraps `child` in a counter owned by the process (a static) and returns
/// the allocator to use in its place.
pub fn install(child: Allocator) Allocator {
    const S = struct {
        var counting: Counting = undefined;
    };
    S.counting = .init(child);
    installed = &S.counting;
    return S.counting.allocator();
}

/// Null when the process never called `install` (tests).
pub fn current() ?*Counting {
    return installed;
}

test "counts live bytes, the peak and live allocations" {
    var c: Counting = .init(std.testing.allocator);
    const a = c.allocator();
    const x = try a.alloc(u8, 100);
    const y = try a.alloc(u32, 10);
    var s = c.snapshot();
    try std.testing.expectEqual(@as(usize, 140), s.live_bytes);
    try std.testing.expectEqual(@as(usize, 2), s.live_allocs);
    a.free(x);
    s = c.snapshot();
    try std.testing.expectEqual(@as(usize, 40), s.live_bytes);
    try std.testing.expectEqual(@as(usize, 140), s.peak_bytes);
    a.free(y);
    s = c.snapshot();
    try std.testing.expectEqual(@as(usize, 0), s.live_bytes);
    try std.testing.expectEqual(@as(usize, 0), s.live_allocs);
    try std.testing.expectEqual(@as(usize, 2), s.total_allocs);
}

test "growing and shrinking in place move the counters" {
    var c: Counting = .init(std.testing.allocator);
    const a = c.allocator();
    var list: std.ArrayList(u8) = .empty;
    defer list.deinit(a);
    for (0..1000) |i| try list.append(a, @intCast(i % 251));
    const s = c.snapshot();
    try std.testing.expect(s.live_bytes >= 1000);
    list.clearAndFree(a);
    try std.testing.expectEqual(@as(usize, 0), c.snapshot().live_bytes);
    try std.testing.expect(c.snapshot().peak_bytes >= 1000);
}
