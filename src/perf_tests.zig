//! Performance tests: the memory and complexity claims of
//! docs/ARCHITECTURE.md, checked on inputs as big as the editor is built
//! for. The 100 MiB log and the 20 MiB one-line JSON are the files `zig
//! build profile` opens (tools/fixtures.zig makes both), loaded the way the
//! editor opens a file (`Buffer.load`).
//!
//! The budgets are deterministic: allocations and bytes, counted by a
//! wrapper around the testing allocator, and rows re-wrapped, so they hold
//! in a Debug build and on a busy machine. They are the measured counts
//! with 2x headroom or more; memory or allocations that grew with the file
//! would miss them by orders of magnitude. Under `zig build perf`
//! (ReleaseFast, `build_options.perf`) the tests also print how long each
//! step took and fail past its budget of CPU time: 5 to 8x the median on a
//! 22-thread desktop, and at least 5 ms.
//!
//! build.zig runs only the tests named "perf: …" from this root, so the
//! tests of the files imported here don't run a second time.

const std = @import("std");
const build_options = @import("build_options");
const fixtures = @import("fixtures");
const buffer_mod = @import("model/text/buffer.zig");
const rope_mod = @import("model/text/rope.zig");
const display_map = @import("model/text/display_map.zig");
const line_index = @import("framework/text/line_index.zig");
const regex_mod = @import("framework/util/regex.zig");
const long_line = @import("app/editor/long_line.zig");
const find_panel = @import("app/workspace/find_panel.zig");

/// Zig has no array-repeat operator; this is `s` repeated `times` times.
fn repeat(comptime s: []const u8, comptime times: usize) *const [s.len * times]u8 {
    return comptime blk: {
        var out: [s.len * times]u8 = undefined;
        for (0..times) |i| @memcpy(out[i * s.len ..][0..s.len], s);
        const done = out;
        break :blk &done;
    };
}

const testing = std.testing;
const Allocator = std.mem.Allocator;
const Io = std.Io;
const Buffer = buffer_mod.Buffer;
const Rope = rope_mod.Rope;
const DisplayMap = display_map.DisplayMap;

/// What tools/fixtures.zig makes, pinned: profile baselines recorded with
/// other inputs aren't comparable.
const log_len = 104_857_605;
const log_lines = 926_802;
const log_hash = 0xed69436bf1241457;
const json_len = 20_971_569;
const json_hash = 0xc4103e66e2fc6998;

/// Counts what goes through to `child`: allocations (growing a block counts
/// as one), the bytes they add, and the bytes held now and at most.
const CountingAllocator = struct {
    child: Allocator,
    /// Since `reset`.
    allocs: usize = 0,
    bytes: usize = 0,
    peak: usize = 0,
    live: usize = 0,

    fn allocator(self: *CountingAllocator) Allocator {
        return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
    }

    /// Starts counting afresh; the peak starts from what is held now.
    fn reset(self: *CountingAllocator) void {
        self.allocs = 0;
        self.bytes = 0;
        self.peak = self.live;
    }

    fn sized(self: *CountingAllocator, old: usize, new: usize) void {
        if (new > old) {
            self.allocs += 1;
            self.bytes += new - old;
            self.live += new - old;
            self.peak = @max(self.peak, self.live);
        } else self.live -= old - new;
    }

    fn alloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        const p = self.child.rawAlloc(len, alignment, ret_addr) orelse return null;
        self.sized(0, len);
        return p;
    }

    fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        if (!self.child.rawResize(memory, alignment, new_len, ret_addr)) return false;
        self.sized(memory.len, new_len);
        return true;
    }

    fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        const p = self.child.rawRemap(memory, alignment, new_len, ret_addr) orelse return null;
        self.sized(memory.len, new_len);
        return p;
    }

    fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {
        const self: *CountingAllocator = @ptrCast(@alignCast(ctx));
        self.child.rawFree(memory, alignment, ret_addr);
        self.live -= memory.len;
    }
};

/// Time budgets, checked only by `zig build perf` (a Debug build is too slow
/// for times to be asserted). A budget is on the thread's CPU time, which
/// time slicing doesn't stretch: with every core of the machine busy, wall
/// times grew up to 14x and CPU times up to 5x. The wall time is printed
/// beside it.
const Timer = struct {
    io: Io,
    wall: Io.Timestamp,
    cpu: Io.Timestamp,

    fn begin(io: Io) Timer {
        return .{ .io = io, .wall = Io.Clock.awake.now(io), .cpu = Io.Clock.cpu_thread.now(io) };
    }

    /// Prints the times since `begin` and fails past `budget_us` of CPU time.
    fn check(t: Timer, what: []const u8, budget_us: i64) !void {
        if (!build_options.perf) return;
        const cpu_us = t.print(what);
        std.debug.print("   budget {d:>7.1} ms\n", .{ms(budget_us)});
        if (cpu_us > budget_us) return error.OverTimeBudget;
    }

    /// Prints the times since `begin`, with no budget: for a test's own
    /// setup, which runs no editor code.
    fn note(t: Timer, what: []const u8) void {
        if (!build_options.perf) return;
        _ = t.print(what);
        std.debug.print("\n", .{});
    }

    /// Prints the times since `begin` on an unfinished line and returns the
    /// CPU time.
    fn print(t: Timer, what: []const u8) i64 {
        const cpu_us = t.cpu.untilNow(t.io, .cpu_thread).toMicroseconds();
        const wall_us = t.wall.untilNow(t.io, .awake).toMicroseconds();
        std.debug.print("  {s:<44} {d:>9.2} ms cpu {d:>9.2} ms wall", .{ what, ms(cpu_us), ms(wall_us) });
        return cpu_us;
    }

    fn ms(us: i64) f64 {
        return @as(f64, @floatFromInt(us)) / 1000;
    }
};

/// Starts a test's times on a line of their own, after the test runner's
/// "1/3 name...".
fn newLine() void {
    if (build_options.perf) std.debug.print("\n", .{});
}

/// Writes a generated input into `tmp` and returns its absolute path.
fn writeFixture(gpa: Allocator, io: Io, tmp: *testing.TmpDir, name: []const u8, bytes: usize, body: *const fn (*Io.Writer, usize) Io.Writer.Error!void) ![]u8 {
    {
        var file = try tmp.dir.createFile(io, name, .{});
        defer file.close(io);
        var buf: [64 << 10]u8 = undefined;
        var fw = file.writer(io, &buf);
        try body(&fw.interface, bytes);
        try fw.interface.flush();
    }
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = try tmp.dir.realPath(io, &path_buf);
    return std.fs.path.join(gpa, &.{ path_buf[0..n], name });
}

/// Wyhash of the text, read chunk by chunk where it lies.
fn contentHash(rope: *const Rope) u64 {
    var h: std.hash.Wyhash = .init(0);
    var it = rope.chunks(0, rope.len());
    while (it.next()) |s| h.update(s);
    return h.final();
}

fn divCeil(a: usize, b: usize) usize {
    return (a + b - 1) / b;
}

/// An edit through the undo history, as a command makes it (no carets).
fn replace(buf: *Buffer, start: usize, end: usize, text: []const u8) !void {
    try buf.edit(&.{.{ .start = start, .end = end, .text = text }}, .other, &.{}, &.{});
}

/// Keeps the last change a buffer reports.
const LastChange = struct {
    change: ?buffer_mod.Change = null,

    fn onChange(ctx: *anyopaque, _: *Buffer, change: buffer_mod.Change) void {
        const self: *LastChange = @ptrCast(@alignCast(ctx));
        self.change = change;
    }

    fn listener(self: *LastChange) buffer_mod.Listener {
        return .{ .ctx = self, .onChange = onChange };
    }
};

test "perf: a 100 MiB log: load, queries, edits, search, clone, undo, soft wrap" {
    newLine();
    const io = testing.io;
    var counter: CountingAllocator = .{ .child = testing.allocator };
    const gpa = counter.allocator();
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();

    var t = Timer.begin(io);
    const path = try writeFixture(testing.allocator, io, &tmp, "biglog-100MiB.log", fixtures.log_bytes, fixtures.writeLog);
    defer testing.allocator.free(path);
    t.note("generate the 100 MiB log");

    // Opened as a file from the command line or Goto Anything is.
    t = .begin(io);
    var buf = try Buffer.load(gpa, io, 1, path);
    defer buf.deinit();
    try t.check("load it (read, validate, build the rope)", 800_000);
    try testing.expectEqual(@as(usize, log_len), buf.len());
    try testing.expectEqual(@as(u32, log_lines), buf.lineCount());
    // The bulk load fills every 1 KB chunk (all ASCII: no char to keep
    // whole), so the rope holds ~1.05x the text. While it is built the
    // file's bytes sit in one buffer of exactly their size, with a 1 MB
    // batch of chunks on top.
    try testing.expectEqual(divCeil(log_len, rope_mod.CHUNK_MAX), buf.rope.chunkCount());
    try testing.expect(counter.live * 100 <= log_len * 110);
    try testing.expect(counter.peak <= log_len + counter.live + (2 << 20));
    // An allocation per tree node (a leaf per 12 chunks, and the levels
    // above), a few per batch, and the file's: 9,661 measured, where one
    // per chunk would add 102,400.
    try testing.expect(counter.allocs <= 20_000);
    t = .begin(io);
    try testing.expectEqual(@as(u64, log_hash), contentHash(&buf.rope));
    try t.check("hash it", 80_000);

    // offset <-> point at the start, the middle and the end, and the start
    // and end of 1,000 rows: O(log n) seeks over the chunk summaries, with
    // no allocation.
    counter.reset();
    t = .begin(io);
    const max = buf.rope.maxPoint();
    try testing.expectEqual(@as(u32, log_lines - 1), max.row);
    for ([_]usize{ 0, 1, log_len / 2, log_len / 2 + 777, log_len - 1, log_len }) |off| {
        const p = buf.offsetToPoint(off);
        try testing.expectEqual(off, buf.pointToOffset(p));
        try testing.expectEqual(off, buf.lineStart(p.row) + p.column);
        if (p.row > 0) try testing.expectEqual(@as(u8, '\n'), buf.rope.byteAt(buf.lineStart(p.row) - 1));
    }
    try testing.expect(buf.offsetToPoint(log_len).eql(max));
    var row: u32 = 0;
    while (row < log_lines) : (row += log_lines / 1000) {
        const ls = buf.lineStart(row);
        try testing.expect(buf.offsetToPoint(ls).eql(.init(row, 0)));
        try testing.expectEqual(ls + buf.lineLen(row), buf.lineEnd(row));
    }
    try t.check("offset <-> point at 1,000 rows", 10_000);
    try testing.expectEqual(@as(usize, 0), counter.allocs);

    // Edits in the middle and near the end, recorded for undo. One inside a
    // chunk is made in place: only the undo record allocates. One that
    // overfills a chunk (the load left them all full) or spans chunks
    // rebuilds just those chunks and splices the rest from shared slices of
    // the old tree: O(log n) new nodes, never the 110 MB of a rebuild.
    const Cost = enum { in_place, spliced };
    const Edit = struct { from_end: bool, at: usize, del: usize, ins: []const u8, cost: Cost };
    const paste = repeat("pasted text spanning several chunks, ", 100);
    const needle = "jiffy-needle-42";
    const edits = [_]Edit{
        .{ .from_end = false, .at = 0, .del = 0, .ins = "x", .cost = .spliced },
        .{ .from_end = false, .at = 1, .del = 0, .ins = "y", .cost = .in_place },
        .{ .from_end = false, .at = 100, .del = 1, .ins = "", .cost = .in_place },
        .{ .from_end = false, .at = 200, .del = 0, .ins = paste, .cost = .spliced },
        .{ .from_end = false, .at = 10_000, .del = 5000, .ins = "", .cost = .spliced },
        .{ .from_end = true, .at = 5000, .del = 0, .ins = needle ++ " ", .cost = .spliced },
        .{ .from_end = true, .at = 4000, .del = 1, .ins = "", .cost = .in_place },
        .{ .from_end = true, .at = 3000, .del = 0, .ins = paste, .cost = .spliced },
        .{ .from_end = true, .at = 2500, .del = 2000, .ins = "", .cost = .spliced },
    };
    const mid = log_len / 2;
    const height = buf.rope.tree.height();
    var needle_at: usize = 0;
    t = .begin(io);
    for (edits) |e| {
        const at = if (e.from_end) buf.len() - e.at else mid + e.at;
        if (std.mem.startsWith(u8, e.ins, needle)) needle_at = at;
        counter.reset();
        try replace(&buf, at, at + e.del, e.ins);
        switch (e.cost) {
            .in_place => try testing.expect(counter.allocs <= 8 and counter.bytes <= 2 << 10),
            .spliced => try testing.expect(counter.allocs <= 16 * (height + 1) and counter.bytes <= 384 << 10),
        }
    }
    try t.check("9 edits", 7_000);
    try buf.rope.checkInvariants();

    // Find searches the chunks where they lie (a match across two in a seam
    // of needle-length bytes): no copy of the text.
    var matches: std.ArrayList([2]usize) = .empty;
    defer matches.deinit(gpa);
    counter.reset();
    t = .begin(io);
    try find_panel.findPlain(gpa, &matches, &buf.rope, 0, buf.len(), "JIFFY-Needle-42", .{});
    try t.check("find a needle near the end", 100_000);
    try testing.expectEqualSlices([2]usize, &.{.{ needle_at, needle_at + needle.len }}, matches.items);
    try testing.expect(counter.bytes <= 1024);

    // Regex find goes line by line (in the last 8 MiB, as Find does in a
    // selection), holding at most a line: the range isn't copied. (Copying
    // every line, not only those spanning chunks, would allocate no more, so
    // that isn't checked.)
    var re = try regex_mod.Regex.compile(gpa, "jiffy-n[e]+dle-\\d+", .{ .case_insensitive = true });
    defer re.deinit();
    matches.clearRetainingCapacity();
    counter.reset();
    t = .begin(io);
    try find_panel.findRegex(gpa, &matches, &buf.rope, buf.len() - (8 << 20), buf.len(), &re, .{});
    try t.check("regex find in the last 8 MiB", 15_000);
    try testing.expectEqualSlices([2]usize, &.{.{ needle_at, needle_at + needle.len }}, matches.items);
    try testing.expect(counter.bytes <= 64 << 10);

    // clone() is O(1): the snapshot shares every node. An in-place edit of
    // either copies exactly the nodes on its chunk's root path.
    {
        counter.reset();
        var snapshot = buf.rope.clone();
        defer snapshot.deinit(gpa);
        try testing.expectEqual(@as(usize, 0), counter.allocs);
        const at = log_len / 4; // in a chunk no edit touched
        const byte = buf.rope.byteAt(at);
        counter.reset();
        try snapshot.replace(gpa, at, at + 1, "");
        try testing.expectEqual(snapshot.tree.height() + 1, counter.allocs);
        try testing.expect(counter.bytes <= 32 << 10);
        try testing.expectEqual(byte, buf.rope.byteAt(at));
        try testing.expectEqual(buf.len() - 1, snapshot.len());
    }

    // Undo puts back every byte.
    t = .begin(io);
    var undone: usize = 0;
    while (try buf.undo()) |_| undone += 1;
    try t.check("undo the 9 edits", 5_000);
    try testing.expectEqual(edits.len, undone);
    try testing.expectEqual(@as(u64, log_hash), contentHash(&buf.rope));

    // Soft wrap keeps a cumulative break count per row, in blocks of 1024
    // rows: a row that doesn't wrap costs 4 bytes.
    var dm: DisplayMap = .{};
    defer dm.deinit(gpa);
    counter.reset();
    const live = counter.live;
    t = .begin(io);
    dm.setWrap(200, 4); // wider than any row of the log
    try testing.expectEqual(@as(u32, log_lines), dm.displayRowCount(gpa, &buf));
    try t.check("soft wrap it at 200 columns", 250_000);
    try testing.expect(counter.live - live <= log_lines * 4 + (256 << 10));

    // An edit splices the block or two of rows around it and marks its rows
    // dirty; the next query re-wraps just those: here the inserted line
    // (into two display rows) and the row it pushed down, then the row left
    // by undoing it.
    var last: LastChange = .{};
    try buf.addListener(last.listener());
    defer buf.removeListener(&last);
    const at = buf.lineStart(log_lines / 2);
    try replace(&buf, at, at, repeat("a new line, wrapped at the 200 columns of this soft wrap: ", 5) ++ "\n");
    const rewrapped = dm.wraps.?.rewrapped_rows;
    counter.reset();
    t = .begin(io);
    dm.applyChange(gpa, last.change.?);
    try testing.expectEqual(@as(u32, log_lines + 2), dm.displayRowCount(gpa, &buf));
    try t.check("re-wrap after inserting a line", 5_000);
    try testing.expectEqual(rewrapped + 2, dm.wraps.?.rewrapped_rows);
    try testing.expect(counter.bytes <= 128 << 10);
    _ = try buf.undo();
    dm.applyChange(gpa, last.change.?);
    try testing.expectEqual(@as(u32, log_lines), dm.displayRowCount(gpa, &buf));
    try testing.expectEqual(rewrapped + 3, dm.wraps.?.rewrapped_rows);
}

test "perf: typing 10,000 characters one at a time into the middle of a 10 MB buffer" {
    newLine();
    const io = testing.io;
    var counter: CountingAllocator = .{ .child = testing.allocator };
    const gpa = counter.allocator();
    // The first 10 MiB of the log.
    var text: Io.Writer.Allocating = .init(testing.allocator);
    defer text.deinit();
    try fixtures.writeLog(&text.writer, 10 << 20);
    var buf = try Buffer.initText(gpa, 1, text.written());
    defer buf.deinit();
    const hash = contentHash(&buf.rope);

    // A keystroke is an edit at the caret, recorded for undo (typing merges
    // into word-sized steps).
    const typed = "The quick brown fox jumps over the lazy dog, again and again.\n";
    var caret = buf.lineStart(buf.lineCount() / 2);
    var max_allocs: usize = 0;
    var max_bytes: usize = 0;
    var total_allocs: usize = 0;
    var total_bytes: usize = 0;
    const t = Timer.begin(io);
    for (0..10_000) |i| {
        counter.reset();
        try buf.edit(&.{.{ .start = caret, .end = caret, .text = typed[i % typed.len ..][0..1] }}, .typing, &.{.caret(caret)}, &.{.caret(caret + 1)});
        caret += 1;
        max_allocs = @max(max_allocs, counter.allocs);
        max_bytes = @max(max_bytes, counter.bytes);
        total_allocs += counter.allocs;
        total_bytes += counter.bytes;
    }
    try t.check("10,000 keystrokes", 350_000);
    // No keystroke rebuilds the rope (10.5 MB): most change their chunk in
    // place, and one that fills it up splits it, adding O(log n) nodes.
    // Measured: 3.6 allocations and 380 bytes a keystroke, at most 29
    // allocations and 125 KB.
    try testing.expect(total_allocs <= 10_000 * 8);
    try testing.expect(total_bytes <= 10_000 * 1024);
    try testing.expect(max_allocs <= 64);
    try testing.expect(max_bytes <= 256 << 10);

    while (try buf.undo()) |_| {}
    try testing.expectEqual(hash, contentHash(&buf.rope));
}

/// Reads a buffer line for a LineIndex as the editor's long-line views do,
/// counting the bytes read.
const LineSource = struct {
    rope: *const Rope,
    len: usize,
    read_bytes: *usize,

    fn read(ctx: *const anyopaque, start: usize, end: usize, out: []u8) []const u8 {
        const self: *const LineSource = @ptrCast(@alignCast(ctx));
        self.read_bytes.* += end - start;
        return self.rope.copyRange(start, end, out);
    }

    fn source(self: *const LineSource) line_index.Source {
        return .{ .ctx = self, .read = read, .len = self.len };
    }
};

/// A monospace font: 8 px per char, in 1/64 px. The index needs only an
/// advance per char, not a TextSystem.
const cell = 8 * 64;

fn monoAdvance(_: ?*anyopaque, _: u32, _: u21) i32 {
    return cell;
}

test "perf: a 20 MiB one-line JSON: LineIndex checkpoints, an edit, find in the line" {
    newLine();
    const io = testing.io;
    var counter: CountingAllocator = .{ .child = testing.allocator };
    const gpa = counter.allocator();
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeFixture(testing.allocator, io, &tmp, "minified-20MiB.json", fixtures.json_bytes, fixtures.writeJson);
    defer testing.allocator.free(path);

    var t = Timer.begin(io);
    var buf = try Buffer.load(gpa, io, 1, path);
    defer buf.deinit();
    try t.check("load the 20 MiB one-line JSON", 150_000);
    try testing.expectEqual(@as(usize, json_len), buf.len());
    try testing.expectEqual(@as(u32, 1), buf.lineCount());
    try testing.expectEqual(@as(u32, json_len), buf.rope.summary().longest_row_chars);
    try testing.expect(counter.live * 100 <= json_len * 110);
    try testing.expectEqual(@as(u64, json_hash), contentHash(&buf.rope));
    // Too long to lay out whole: views index it instead.
    try testing.expect(buf.lineLen(0) > long_line.threshold);

    // A checkpoint (24 bytes) every 2 KB, where a shaped layout would take
    // ~40 bytes a char.
    const adv: line_index.Advances = .{ .ascii = @splat(cell), .other = monoAdvance };
    var read_bytes: usize = 0;
    var src: LineSource = .{ .rope = &buf.rope, .len = buf.len(), .read_bytes = &read_bytes };
    var idx: line_index.LineIndex = .{};
    defer idx.deinit(gpa);
    const live = counter.live;
    t = .begin(io);
    try idx.build(gpa, &adv, src.source());
    try t.check("index it", 120_000);
    try testing.expectEqual(divCeil(json_len, line_index.block_bytes), idx.cps.items.len);
    try testing.expect(counter.live - live <= 2 * idx.cps.items.len * @sizeOf(line_index.Pos));
    try testing.expectEqual(@as(i64, json_len * cell), idx.width());

    // A keystroke in the middle re-scans its block and shifts the
    // checkpoints after it.
    const col = json_len / 2 + 5;
    try replace(&buf, col, col, "x");
    src.len = buf.len();
    read_bytes = 0;
    counter.reset();
    t = .begin(io);
    try idx.update(gpa, &adv, src.source(), col, col, col + 1);
    try t.check("update the index after a keystroke", 5_000);
    try testing.expect(read_bytes <= line_index.block_bytes + 8);
    try testing.expect(counter.allocs <= 2);
    try testing.expectEqual(@as(i64, (json_len + 1) * cell), idx.width());
    for ([_]usize{ 0, col - 3, col, col + 1, col + 4000, json_len }) |b| {
        try testing.expectEqual(@as(i64, @intCast(b)) * cell, idx.posAt(&adv, src.source(), b).x);
    }

    // Find searches the line in place, chunk by chunk.
    var matches: std.ArrayList([2]usize) = .empty;
    defer matches.deinit(gpa);
    try replace(&buf, json_len - 100, json_len - 100, "jiffy-needle-42");
    counter.reset();
    t = .begin(io);
    try find_panel.findPlain(gpa, &matches, &buf.rope, 0, buf.len(), "jiffy-needle-42", .{});
    try t.check("find a needle at the end of the line", 25_000);
    try testing.expectEqualSlices([2]usize, &.{.{ json_len - 100, json_len - 100 + 15 }}, matches.items);
    try testing.expect(counter.bytes <= 1024);

    _ = try buf.undo();
    _ = try buf.undo();
    try testing.expectEqual(@as(u64, json_hash), contentHash(&buf.rope));
}
