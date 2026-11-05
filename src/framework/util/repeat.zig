//! Zig has no array-repeat operator: `s` repeated `times` times, at compile time.

pub fn repeat(comptime s: []const u8, comptime times: usize) *const [s.len * times]u8 {
    const buf = comptime blk: {
        @setEvalBranchQuota(times * 4 + 100);
        var b: [s.len * times]u8 = undefined;
        for (0..times) |i| @memcpy(b[i * s.len ..][0..s.len], s);
        const done = b;
        break :blk done;
    };
    return &buf;
}
