//! The big generated inputs of `zig build profile` (tools/profile.zig) and
//! the performance tests (src/perf_tests.zig), and of the leak runner
//! (tools/leaks.zig, with smaller sizes): a server log and a one-line JSON,
//! made from fixed seeds so every build measures the same bytes. All import
//! this file (build.zig's `fixtures` module), so the tests load exactly the
//! file a profile scenario opens; the tests pin its size, line count and
//! hash, since recorded profile baselines are only comparable while it stays
//! the same.

const std = @import("std");
const Io = std.Io;

/// The log of the `biglog` scenario.
pub const log_bytes = 100 << 20;
/// The JSON of the `minified` scenario.
pub const json_bytes = 20 << 20;

/// splitmix64: a fixed generator, so the fixtures never change with Zig's std.
const Rng = struct {
    state: u64,

    fn next(r: *Rng) u64 {
        r.state +%= 0x9e3779b97f4a7c15;
        var z = r.state;
        z = (z ^ (z >> 30)) *% 0xbf58476d1ce4e5b9;
        z = (z ^ (z >> 27)) *% 0x94d049bb133111eb;
        return z ^ (z >> 31);
    }

    fn below(r: *Rng, n: u64) u64 {
        return r.next() % n;
    }

    fn pick(r: *Rng, items: []const []const u8) []const u8 {
        return items[@intCast(r.below(items.len))];
    }
};

/// A server log: timestamped lines of 50-200 bytes, mostly INFO. Stops at
/// the end of the line that reaches `bytes`.
pub fn writeLog(w: *Io.Writer, bytes: usize) Io.Writer.Error!void {
    var rng: Rng = .{ .state = 1 };
    const levels = [_][]const u8{ "INFO ", "INFO ", "INFO ", "INFO ", "DEBUG", "DEBUG", "WARN ", "ERROR" };
    const methods = [_][]const u8{ "GET", "GET", "GET", "POST", "PUT", "DELETE" };
    const routes = [_][]const u8{ "/api/v1/items", "/api/v1/users", "/api/v1/orders", "/static/js/app.js", "/healthz", "/api/v2/search" };
    const codes = [_][]const u8{ "200", "200", "200", "201", "304", "404", "500" };
    const notes = [_][]const u8{ "cache miss", "cache hit", "retrying upstream", "slow query", "connection reused", "token refreshed", "rate limited" };
    var line_buf: [512]u8 = undefined;
    var written: usize = 0;
    var t_ms: u64 = 0;
    while (written < bytes) {
        var line: Io.Writer = .fixed(&line_buf);
        t_ms += rng.below(250);
        const s = t_ms / 1000;
        try line.print("2026-10-{d:0>2}T{d:0>2}:{d:0>2}:{d:0>2}.{d:0>3}Z {s} [worker-{d:0>2}] ", .{ 1 + (s / 86400) % 28, (s / 3600) % 24, (s / 60) % 60, s % 60, t_ms % 1000, rng.pick(&levels), rng.below(32) });
        switch (rng.below(4)) {
            0, 1 => try line.print("{s} {s}/{d} status={s} bytes={d} dur_ms={d}.{d:0>3} req={x:0>16}\n", .{ rng.pick(&methods), rng.pick(&routes), rng.below(100000), rng.pick(&codes), rng.below(65536), rng.below(900), rng.below(1000), rng.next() }),
            2 => try line.print("{s} for key=user:{d}:session after {d} ms\n", .{ rng.pick(&notes), rng.below(1 << 20), rng.below(5000) }),
            else => try line.print("gc pause {d} us, heap {d} KiB -> {d} KiB, {d} tasks\n", .{ rng.below(20000), 40000 + rng.below(9000), 30000 + rng.below(9000), rng.below(4000) }),
        }
        try w.writeAll(line.buffered());
        written += line.buffered().len;
    }
}

/// One line of JSON (no newline at all): an array of small records.
pub fn writeJson(w: *Io.Writer, bytes: usize) Io.Writer.Error!void {
    var rng: Rng = .{ .state = 2 };
    const tags = [_][]const u8{ "red", "green", "blue", "sale", "new", "bulk", "fragile" };
    const head = "{\"generator\":\"cs-zig profile\",\"items\":[";
    try w.writeAll(head);
    var rec_buf: [512]u8 = undefined;
    var written: usize = head.len;
    var id: u64 = 0;
    while (written < bytes) : (id += 1) {
        var rec: Io.Writer = .fixed(&rec_buf);
        if (id > 0) try rec.writeByte(',');
        try rec.print("{{\"id\":{d},\"name\":\"item-{d}\",\"sku\":\"SKU-{x:0>8}\",\"price\":{d}.{d:0>2},\"qty\":{d},", .{ id, id, rng.next() & 0xffffffff, rng.below(1000), rng.below(100), rng.below(500) });
        try rec.print("\"active\":{s},\"tags\":[\"{s}\",\"{s}\"],\"dims\":{{\"w\":{d}.{d},\"h\":{d}.{d}}},\"note\":\"{s} {s}\"}}", .{ if (rng.below(2) == 1) "true" else "false", rng.pick(&tags), rng.pick(&tags), rng.below(100), rng.below(10), rng.below(100), rng.below(10), rng.pick(&tags), rng.pick(&tags) });
        try w.writeAll(rec.buffered());
        written += rec.buffered().len;
    }
    try w.writeAll("]}");
}
