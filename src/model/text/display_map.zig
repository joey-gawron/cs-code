//! Buffer rows → display rows, through folds and soft wrap (Zed stacks a
//! FoldMap and a WrapMap the same way).
//!
//! Each buffer row contributes 0 display rows (folded away), 1, or several
//! (soft-wrapped segments). Wrapping is computed in display columns (tabs
//! expanded, wide CJK characters count as two), which matches monospace
//! layout. When neither folds nor wrapping are active every call is identity.
//!
//! Wrap breaks live in blocks of consecutive rows (`Block`): a cumulative
//! break count per row plus one pooled array of break columns, so a row that
//! doesn't wrap costs 4 bytes and an edit re-splices a block or two (edited
//! rows are re-wrapped lazily). A full wrap is one pass over the rope's
//! chunks. Buffers over `sync_wrap_bytes` are wrapped on a background thread
//! against an O(1) rope snapshot: until the result lands the view keeps the
//! previous wrap (or none), and edits made meanwhile are replayed onto it.

const std = @import("std");
const repeat = @import("../../framework/util/repeat.zig").repeat;
const buffer_mod = @import("buffer.zig");
const fold_map = @import("fold_map.zig");
const rope_mod = @import("rope.zig");
const executor_mod = @import("../../framework/util/executor.zig");

const Allocator = std.mem.Allocator;
const Buffer = buffer_mod.Buffer;
const Rope = rope_mod.Rope;
const Executor = executor_mod.Executor;

/// Buffers up to this size are wrapped on the spot (a few milliseconds);
/// bigger ones in the background when the map has an executor.
pub const sync_wrap_bytes: usize = 4 << 20;
/// "word_wrap": "auto" leaves bigger files unwrapped (see `Editor.wrapEnabled`):
/// past this a text file is more likely a log or a dump than prose, and its
/// background wrap would visibly reflow the view after opening.
pub const auto_wrap_bytes: usize = 16 << 20;
/// More edited rows than this (a huge paste or undo) are re-wrapped in the
/// background too.
const sync_rewrap_rows = 50_000;
/// Rows per wrap block.
const block_rows: u32 = 1024;
/// An edit inside a row with this many breaks shifts them instead of
/// re-wrapping the row on the spot (see `Wraps.splice`).
const interpolate_breaks = 4096;

/// A buffer edit in rows: rows [row, row + old_n) became `new_n` rows. The
/// columns place it when it stays inside one row.
const RowEdit = struct {
    row: u32,
    old_n: u32,
    new_n: u32,
    start_col: u32,
    old_end_col: u32,
    new_end_col: u32,

    fn of(change: buffer_mod.Change) RowEdit {
        const row = change.start_point.row;
        return .{
            .row = row,
            .old_n = change.old_end_point.row - row + 1,
            .new_n = change.new_end_point.row - row + 1,
            .start_col = change.start_point.column,
            .old_end_col = change.old_end_point.column,
            .new_end_col = change.new_end_point.column,
        };
    }
};

pub const Segment = struct {
    row: u32,
    /// Which wrapped piece of the row (0 = first).
    index: u32,
    /// Byte offsets of the segment in the buffer (end excludes the newline).
    start: usize,
    end: usize,
    /// Display columns of indentation drawn before continuation segments.
    indent_cols: u32,
};

const Params = struct {
    cols: u32,
    tab_size: u32,
    /// Indent continuation segments like the row (unless that's half the width).
    indent: bool,

    fn eql(a: Params, b: Params) bool {
        return a.cols == b.cols and a.tab_size == b.tab_size and a.indent == b.indent;
    }

    /// Continuation indent of a row with `lead` columns of leading blanks.
    fn indentFor(p: Params, lead: u32) u32 {
        return if (p.indent and lead < p.cols / 2) lead else 0;
    }
};

/// East Asian wide characters take two cells.
fn charWidth(cp: u21) u32 {
    if (cp < 0x1100) return 1;
    if ((cp >= 0x1100 and cp <= 0x115F) or (cp >= 0x2E80 and cp <= 0xA4CF) or (cp >= 0xAC00 and cp <= 0xD7A3) or
        (cp >= 0xF900 and cp <= 0xFAFF) or (cp >= 0xFE30 and cp <= 0xFE4F) or (cp >= 0xFF00 and cp <= 0xFF60) or
        (cp >= 0xFFE0 and cp <= 0xFFE6) or (cp >= 0x1F300 and cp <= 0x1FAFF) or (cp >= 0x20000 and cp <= 0x3FFFD)) return 2;
    return 1;
}

/// Length of the prefix of `bytes` (at most `max`) that is ASCII but no tab:
/// one display column per byte.
fn plainRun(bytes: []const u8, max: usize) usize {
    const n = @min(bytes.len, max);
    const V = @Vector(16, u8);
    var i: usize = 0;
    while (i + 16 <= n) : (i += 16) {
        const v: V = bytes[i..][0..16].*;
        const high: u16 = @bitCast(v >= @as(V, @splat(0x80)));
        const tabs: u16 = @bitCast(v == @as(V, @splat('\t')));
        if (high | tabs != 0) return i + @ctz(high | tabs);
    }
    while (i < n and bytes[i] < 0x80 and bytes[i] != '\t') i += 1;
    return i;
}

/// Where the tail of `bytes` with no blank, tab or non-ASCII byte starts.
fn quietFrom(bytes: []const u8) usize {
    const V = @Vector(16, u8);
    var end = bytes.len;
    while (end >= 16) : (end -= 16) {
        const v: V = bytes[end - 16 ..][0..16].*;
        const high: u16 = @bitCast(v >= @as(V, @splat(0x80)));
        const spaces: u16 = @bitCast(v == @as(V, @splat(' ')));
        const tabs: u16 = @bitCast(v == @as(V, @splat('\t')));
        const loud = high | spaces | tabs;
        if (loud != 0) return end - 16 + (16 - @clz(loud));
    }
    while (end > 0) : (end -= 1) {
        const b = bytes[end - 1];
        if (b == ' ' or b == '\t' or b >= 0x80) return end;
    }
    return 0;
}

/// Finds the soft-wrap breaks of one row, fed in pieces (a row can span rope
/// chunks). A segment breaks after its last blank that fits, else mid-word.
const LineWrapper = struct {
    params: Params,
    /// Byte columns where continuation segments start.
    breaks: std.ArrayList(u32) = .empty,
    /// Bytes of the row seen so far.
    byte: u32 = 0,
    /// Display columns within the current segment, and within the row (for tab stops).
    col: u32 = 0,
    abs_col: u32 = 0,
    seg_start: u32 = 0,
    /// Just after the segment's last blank, and the width of what followed it.
    last_blank: ?u32 = null,
    since_blank: u32 = 0,
    /// Width of the leading blanks (final once something else shows up).
    lead: u32 = 0,
    in_lead: bool = true,
    indent: u32 = 0,

    fn nextRow(lw: *LineWrapper) void {
        var breaks = lw.breaks;
        breaks.clearRetainingCapacity();
        lw.* = .{ .params = lw.params, .breaks = breaks };
    }

    fn limit(lw: *const LineWrapper) u32 {
        return if (lw.breaks.items.len == 0) lw.params.cols else lw.params.cols -| lw.indent;
    }

    /// `n` more ASCII bytes without blanks or tabs, with no blank to break
    /// after (minified code, data): segments are simply `limit` columns.
    fn fillQuiet(lw: *LineWrapper, gpa: Allocator, n: usize) Allocator.Error!void {
        lw.in_lead = false;
        var rest = n;
        while (true) {
            const room = @min(rest, lw.limit() -| lw.col);
            lw.col += @intCast(room);
            lw.abs_col += @intCast(room);
            lw.byte += @intCast(room);
            lw.since_blank += @intCast(room);
            rest -= room;
            if (rest == 0) return;
            // The next byte overflows: break before it (a segment holds at
            // least one char).
            if (lw.byte > lw.seg_start) {
                if (lw.breaks.items.len == 0) lw.indent = lw.params.indentFor(lw.lead);
                try lw.breaks.append(gpa, lw.byte);
                lw.col = 0;
                lw.seg_start = lw.byte;
            } else {
                lw.col += 1;
                lw.abs_col += 1;
                lw.byte += 1;
                lw.since_blank += 1;
                rest -= 1;
                if (rest == 0) return;
            }
        }
    }

    /// Feeds the row's next bytes (no newline); `ends_row` says none follow.
    fn feed(lw: *LineWrapper, gpa: Allocator, bytes: []const u8, ends_row: bool) Allocator.Error!void {
        // The rest fits (without tabs no char is wider than its bytes).
        if (ends_row and lw.col + bytes.len <= lw.limit() and std.mem.findScalar(u8, bytes, '\t') == null) return;
        const tab = lw.params.tab_size;
        const quiet = quietFrom(bytes);
        var i: usize = 0;
        while (i < bytes.len) {
            if (i >= quiet and lw.last_blank == null) return lw.fillQuiet(gpa, bytes.len - i);
            // A run of plain ASCII that fits in the segment, in bulk.
            const run = plainRun(bytes[i..], lw.limit() -| lw.col);
            if (run > 0) {
                const s = bytes[i..][0..run];
                if (lw.in_lead) {
                    const blanks = for (s, 0..) |ch, k| {
                        if (ch != ' ') break k;
                    } else run;
                    lw.lead += @intCast(blanks);
                    lw.in_lead = blanks == run;
                }
                if (std.mem.findScalarLast(u8, s, ' ')) |k| {
                    lw.last_blank = lw.byte + @as(u32, @intCast(k)) + 1;
                    lw.since_blank = @intCast(run - k - 1);
                } else {
                    lw.since_blank += @intCast(run);
                }
                lw.col += @intCast(run);
                lw.abs_col += @intCast(run);
                lw.byte += @intCast(run);
                i += run;
                continue;
            }
            var len: usize = 1;
            var cp: u21 = bytes[i];
            if (cp >= 0x80) {
                len = std.unicode.utf8ByteSequenceLength(bytes[i]) catch 1;
                cp = if (i + len <= bytes.len) std.unicode.utf8Decode(bytes[i..][0..len]) catch 0xFFFD else 0xFFFD;
            }
            const blank = cp == ' ' or cp == '\t';
            const w: u32 = if (cp == '\t') tab - lw.abs_col % tab else charWidth(cp);
            if (!blank) lw.in_lead = false;
            if (lw.col + w > lw.limit() and lw.byte > lw.seg_start) {
                // The first break settles the continuation indent (a break
                // inside the leading blanks means they outgrow the width).
                if (lw.breaks.items.len == 0) lw.indent = if (lw.in_lead) 0 else lw.params.indentFor(lw.lead);
                const at = if (lw.last_blank) |b| (if (b > lw.seg_start) b else lw.byte) else lw.byte;
                try lw.breaks.append(gpa, at);
                lw.col = if (at == lw.byte) 0 else lw.since_blank;
                lw.seg_start = at;
                lw.last_blank = null;
                continue;
            }
            if (lw.in_lead) lw.lead += w;
            lw.col += w;
            lw.abs_col += w;
            lw.byte += @intCast(len);
            i += len;
            if (blank) {
                lw.last_blank = lw.byte;
                lw.since_blank = 0;
            } else {
                lw.since_blank += w;
            }
        }
    }
};

/// Wraps buffer rows [first, first + count) in one pass over the rope's
/// chunks, handing each row's breaks to `sink.row`. Stops with
/// error.Canceled once `cancel` is set.
fn wrapRows(gpa: Allocator, rope: *const Rope, params: Params, first: u32, count: u32, sink: anytype, cancel: ?*const std.atomic.Value(bool)) !void {
    var lw: LineWrapper = .{ .params = params };
    defer lw.breaks.deinit(gpa);
    var it = rope.chunks(rope.lineStart(first), rope.lineEnd(first + count - 1));
    while (it.next()) |bytes| {
        if (cancel) |c| if (c.load(.monotonic)) return error.Canceled;
        var rest = bytes;
        while (std.mem.findScalar(u8, rest, '\n')) |nl| {
            try lw.feed(gpa, rest[0..nl], true);
            try sink.row(lw.breaks.items);
            lw.nextRow();
            rest = rest[nl + 1 ..];
        }
        try lw.feed(gpa, rest, false);
    }
    try sink.row(lw.breaks.items);
}

/// Soft-wrap breaks of a run of consecutive rows.
const Block = struct {
    /// ends[i]: breaks on the block's rows 0...i.
    ends: std.ArrayList(u32) = .empty,
    /// Break columns (bytes from the row start) of the block's rows, in order.
    cols: std.ArrayList(u32) = .empty,
    /// Rows [dirty_lo, dirty_hi) were edited and show unwrapped until re-wrapped.
    dirty_lo: u32 = 0,
    dirty_hi: u32 = 0,

    fn deinit(b: *Block, gpa: Allocator) void {
        b.ends.deinit(gpa);
        b.cols.deinit(gpa);
    }

    fn rowCount(b: *const Block) u32 {
        return @intCast(b.ends.items.len);
    }

    fn breaksBefore(b: *const Block, i: u32) u32 {
        return if (i == 0) 0 else b.ends.items[i - 1];
    }

    fn rowBreaks(b: *const Block, i: u32) []const u32 {
        return b.cols.items[b.breaksBefore(i)..b.ends.items[i]];
    }

    fn isDirty(b: *const Block, i: u32) bool {
        return i >= b.dirty_lo and i < b.dirty_hi;
    }

    fn markDirty(b: *Block, lo: u32, hi: u32) void {
        if (lo >= hi) return;
        if (b.dirty_lo >= b.dirty_hi) {
            b.dirty_lo = lo;
            b.dirty_hi = hi;
        } else {
            b.dirty_lo = @min(b.dirty_lo, lo);
            b.dirty_hi = @max(b.dirty_hi, hi);
        }
    }

    /// New breaks for rows [lo, hi): `counts[k]` of `cols` belong to row lo + k.
    fn setRows(b: *Block, gpa: Allocator, lo: u32, hi: u32, counts: []const u32, cols: []const u32) !void {
        if (counts.len != hi - lo) return error.OutOfSync;
        const a = b.breaksBefore(lo);
        const z = b.breaksBefore(hi);
        try b.cols.replaceRange(gpa, a, z - a, cols);
        var total = a;
        for (b.ends.items[lo..hi], counts) |*e, n| {
            total += n;
            e.* = total;
        }
        for (b.ends.items[hi..]) |*e| e.* = e.* - z + total;
    }
};

/// Appends rows to a list of blocks holding `per_block` rows each.
const BlockWriter = struct {
    gpa: Allocator,
    out: *std.ArrayList(Block),
    per_block: u32 = block_rows,
    cur: Block = .{},

    fn deinit(bw: *BlockWriter) void {
        bw.cur.deinit(bw.gpa);
    }

    fn room(bw: *BlockWriter) Allocator.Error!void {
        if (bw.cur.rowCount() == bw.per_block) try bw.flush();
        if (bw.cur.ends.capacity == 0) try bw.cur.ends.ensureTotalCapacityPrecise(bw.gpa, bw.per_block);
    }

    /// The `wrapRows` sink.
    fn row(bw: *BlockWriter, breaks: []const u32) Allocator.Error!void {
        try bw.room();
        try bw.cur.cols.appendSlice(bw.gpa, breaks);
        bw.cur.ends.appendAssumeCapacity(@intCast(bw.cur.cols.items.len));
    }

    fn copyRows(bw: *BlockWriter, src: *const Block, lo: u32, hi: u32) Allocator.Error!void {
        var i = lo;
        while (i < hi) : (i += 1) {
            try bw.row(src.rowBreaks(i));
            const k = bw.cur.rowCount() - 1;
            if (src.isDirty(i)) bw.cur.markDirty(k, k + 1);
        }
    }

    /// `n` rows to wrap later.
    fn dirtyRows(bw: *BlockWriter, n: u32) Allocator.Error!void {
        var left = n;
        while (left > 0) {
            try bw.room();
            const at = bw.cur.rowCount();
            const k = @min(left, bw.per_block - at);
            bw.cur.ends.appendNTimesAssumeCapacity(@intCast(bw.cur.cols.items.len), k);
            bw.cur.markDirty(at, at + k);
            left -= k;
        }
    }

    fn flush(bw: *BlockWriter) Allocator.Error!void {
        if (bw.cur.rowCount() == 0) return;
        bw.cur.cols.shrinkAndFree(bw.gpa, bw.cur.cols.items.len);
        try bw.out.append(bw.gpa, bw.cur);
        bw.cur = .{};
    }
};

/// `wrapRows` sink collecting the breaks of a few rows.
const Collect = struct {
    gpa: Allocator,
    counts: std.ArrayList(u32) = .empty,
    cols: std.ArrayList(u32) = .empty,

    fn deinit(c: *Collect) void {
        c.counts.deinit(c.gpa);
        c.cols.deinit(c.gpa);
    }

    fn row(c: *Collect, breaks: []const u32) Allocator.Error!void {
        try c.cols.appendSlice(c.gpa, breaks);
        try c.counts.append(c.gpa, @intCast(breaks.len));
    }
};

/// The soft-wrap breaks of every buffer row, for one set of `Params`.
const Wraps = struct {
    params: Params,
    blocks: std.ArrayList(Block) = .empty,
    /// Rows and breaks before each block (len = blocks + 1) while `sums_valid`.
    row_starts: std.ArrayList(u32) = .empty,
    break_starts: std.ArrayList(u32) = .empty,
    sums_valid: bool = false,
    /// Rows waiting in the blocks' dirty ranges (an upper bound).
    dirty_rows: usize = 0,
    /// Rows `rewrap` has wrapped, in all (src/perf_tests.zig checks that an
    /// edit re-wraps only its own).
    rewrapped_rows: usize = 0,
    /// Wants a full re-wrap (and is shown until that lands).
    stale: bool = false,

    fn deinit(w: *Wraps, gpa: Allocator) void {
        for (w.blocks.items) |*b| b.deinit(gpa);
        w.blocks.deinit(gpa);
        w.row_starts.deinit(gpa);
        w.break_starts.deinit(gpa);
    }

    fn compute(gpa: Allocator, rope: *const Rope, params: Params, cancel: ?*const std.atomic.Value(bool)) !Wraps {
        var w: Wraps = .{ .params = params };
        errdefer w.deinit(gpa);
        var bw: BlockWriter = .{ .gpa = gpa, .out = &w.blocks };
        defer bw.deinit();
        try wrapRows(gpa, rope, params, 0, rope.lineCount(), &bw, cancel);
        try bw.flush();
        return w;
    }

    fn ensureSums(w: *Wraps, gpa: Allocator) Allocator.Error!void {
        if (w.sums_valid) return;
        const n = w.blocks.items.len;
        try w.row_starts.resize(gpa, n + 1);
        try w.break_starts.resize(gpa, n + 1);
        var rows: u32 = 0;
        var breaks: u32 = 0;
        for (w.blocks.items, 0..) |*b, i| {
            w.row_starts.items[i] = rows;
            w.break_starts.items[i] = breaks;
            rows += b.rowCount();
            breaks += b.breaksBefore(b.rowCount());
        }
        w.row_starts.items[n] = rows;
        w.break_starts.items[n] = breaks;
        w.sums_valid = true;
    }

    // The queries below need the sums.

    fn rowCount(w: *const Wraps) u32 {
        return w.row_starts.items[w.blocks.items.len];
    }

    /// The block holding `row` (< rowCount) and the row's index in it.
    fn locate(w: *const Wraps, row: u32) struct { usize, u32 } {
        var lo: usize = 0;
        var hi: usize = w.blocks.items.len;
        while (lo + 1 < hi) {
            const mid = (lo + hi) / 2;
            if (w.row_starts.items[mid] <= row) lo = mid else hi = mid;
        }
        return .{ lo, row - w.row_starts.items[lo] };
    }

    /// Breaks on the rows before `row` (<= rowCount).
    fn breaksBefore(w: *const Wraps, row: u32) u32 {
        if (row >= w.rowCount()) return w.break_starts.items[w.blocks.items.len];
        const b, const i = w.locate(row);
        return w.break_starts.items[b] + w.blocks.items[b].breaksBefore(i);
    }

    fn rowBreaks(w: *const Wraps, row: u32) []const u32 {
        const b, const i = w.locate(row);
        return w.blocks.items[b].rowBreaks(i);
    }

    fn segmentOf(w: *const Wraps, buffer: *const Buffer, row: u32, index: u32) Segment {
        const ls = buffer.lineStart(row);
        const le = buffer.lineEnd(row);
        const breaks = w.rowBreaks(row);
        return .{
            .row = row,
            .index = index,
            .start = if (index == 0) ls else ls + breaks[index - 1],
            .end = if (index < breaks.len) ls + breaks[index] else le,
            .indent_cols = if (index > 0) continuationIndent(buffer, w.params, ls, le) else 0,
        };
    }

    /// The edited rows show unwrapped until `rewrap`.
    fn splice(w: *Wraps, gpa: Allocator, e: RowEdit) !void {
        try w.ensureSums(gpa);
        const row = e.row;
        const old_n = e.old_n;
        const new_n = e.new_n;
        if (old_n == 0 or row + old_n > w.rowCount()) return error.OutOfSync;
        if (old_n == 1 and new_n == 1 and try w.shiftLongRow(gpa, e)) return;
        const first, const i = w.locate(row);
        const last, const j = w.locate(row + old_n - 1);
        const blocks = w.blocks.items;
        var end = last + 1;
        var rows = i + new_n + (blocks[last].rowCount() - j - 1);
        // A small result takes in the next block, so blocks don't fragment.
        if (rows < block_rows / 2 and end < blocks.len) {
            rows += blocks[end].rowCount();
            end += 1;
        }
        var out: std.ArrayList(Block) = .empty;
        defer out.deinit(gpa);
        errdefer for (out.items) |*b| b.deinit(gpa);
        {
            const parts = (rows + block_rows - 1) / block_rows;
            var bw: BlockWriter = .{ .gpa = gpa, .out = &out, .per_block = (rows + parts - 1) / parts };
            defer bw.deinit();
            try bw.copyRows(&blocks[first], 0, i);
            try bw.dirtyRows(new_n);
            try bw.copyRows(&blocks[last], j + 1, blocks[last].rowCount());
            if (end > last + 1) try bw.copyRows(&blocks[last + 1], 0, blocks[last + 1].rowCount());
            try bw.flush();
        }
        try w.blocks.ensureUnusedCapacity(gpa, out.items.len);
        for (w.blocks.items[first..end]) |*b| b.deinit(gpa);
        w.blocks.replaceRangeAssumeCapacity(first, end - first, out.items);
        out.clearRetainingCapacity();
        w.sums_valid = false;
        w.dirty_rows += new_n;
    }

    /// An edit inside a very long row (minified code, a data dump) keeps its
    /// breaks, shifted past the edit: re-wrapping it exactly is a pass over
    /// the whole row, so that is left to a full re-wrap in the background
    /// (`stale`), like Zed's interpolated wrap edits.
    fn shiftLongRow(w: *Wraps, gpa: Allocator, e: RowEdit) !bool {
        const b, const k = w.locate(e.row);
        const blk = &w.blocks.items[b];
        const old = blk.rowBreaks(k);
        if (old.len < interpolate_breaks or blk.isDirty(k)) return false;
        var moved: std.ArrayList(u32) = .empty;
        defer moved.deinit(gpa);
        try moved.ensureTotalCapacityPrecise(gpa, old.len);
        var last: u32 = 0;
        for (old) |x| {
            const y = if (x <= e.start_col) x else if (x >= e.old_end_col) x - e.old_end_col + e.new_end_col else continue;
            if (y <= last) continue;
            moved.appendAssumeCapacity(y);
            last = y;
        }
        try blk.setRows(gpa, k, k + 1, &[_]u32{@intCast(moved.items.len)}, moved.items);
        w.sums_valid = false;
        w.stale = true;
        return true;
    }

    /// Wraps the rows edits left dirty.
    fn rewrap(w: *Wraps, gpa: Allocator, rope: *const Rope) !void {
        if (w.dirty_rows == 0) return;
        try w.ensureSums(gpa);
        w.sums_valid = false;
        var sink: Collect = .{ .gpa = gpa };
        defer sink.deinit();
        for (w.blocks.items, w.row_starts.items[0..w.blocks.items.len]) |*b, start| {
            if (b.dirty_lo >= b.dirty_hi) continue;
            sink.counts.clearRetainingCapacity();
            sink.cols.clearRetainingCapacity();
            try wrapRows(gpa, rope, w.params, start + b.dirty_lo, b.dirty_hi - b.dirty_lo, &sink, null);
            w.rewrapped_rows += sink.counts.items.len;
            try b.setRows(gpa, b.dirty_lo, b.dirty_hi, sink.counts.items, sink.cols.items);
            b.dirty_lo = 0;
            b.dirty_hi = 0;
        }
        w.dirty_rows = 0;
    }
};

/// Indent of a row's continuation segments (decided as `LineWrapper` does).
fn continuationIndent(buffer: *const Buffer, p: Params, ls: usize, le: usize) u32 {
    if (!p.indent) return 0;
    var lead: u32 = 0;
    var it = buffer.rope.chunks(ls, le);
    while (it.next()) |bytes| for (bytes) |ch| {
        if (ch == ' ') lead += 1 else if (ch == '\t') lead += p.tab_size - lead % p.tab_size else return p.indentFor(lead);
        if (lead >= p.cols / 2) return 0;
    };
    return p.indentFor(lead);
}

/// A full wrap computed on a background thread.
const Job = struct {
    gpa: Allocator,
    executor: *Executor,
    /// The map waiting for the result (null once it's gone).
    map: ?*DisplayMap,
    rope: Rope,
    params: Params,
    /// One reference for the worker, one for the map's side.
    refs: std.atomic.Value(u32) = .init(2),
    cancel: std.atomic.Value(bool) = .init(false),
    result: ?Wraps = null,
    /// Main thread: edits since the snapshot, replayed onto the result.
    edits: std.ArrayList(RowEdit) = .empty,

    fn run(job: *Job) void {
        const executor = job.executor;
        job.result = Wraps.compute(job.gpa, &job.rope, job.params, &job.cancel) catch null;
        executor.post(.{ .ctx = job, .run = finished });
        job.release();
        // Last, so whoever waits for the executor to go idle knows the job
        // is freed (by this thread when the map let go first).
        executor.taskDone();
    }

    fn release(job: *Job) void {
        if (job.refs.fetchSub(1, .acq_rel) != 1) return;
        if (job.result) |*w| w.deinit(job.gpa);
        job.rope.deinit(job.gpa);
        job.edits.deinit(job.gpa);
        job.gpa.destroy(job);
    }

    /// Main thread, from the executor.
    fn finished(ctx: *anyopaque) void {
        const job: *Job = @ptrCast(@alignCast(ctx));
        defer job.release();
        if (job.map) |map| map.land(job);
    }
};

pub const DisplayMap = struct {
    folds: fold_map.FoldMap = .{},
    /// Wrap at this many display columns; 0 = no wrapping.
    wrap_cols: u32 = 0,
    tab_size: u32 = 4,
    /// Indent continuation lines to the line's indentation (Sublime's default).
    indent_continuations: bool = true,
    /// When set, buffers bigger than `sync_bytes` are wrapped in the background.
    executor: ?*Executor = null,
    sync_bytes: usize = sync_wrap_bytes,
    /// Hears when a background wrap is about to land, and has (a view keeps
    /// its scroll position on the same text).
    reflow: ?Reflow = null,

    /// The breaks shown, kept in step with edits by `applyChange` (edited
    /// rows are re-wrapped lazily). They are for other parameters than the
    /// current ones while `job` computes new ones.
    wraps: ?Wraps = null,
    /// The background wrap in flight (one at a time).
    job: ?*Job = null,
    /// Bumped whenever `wraps` changes.
    wraps_version: u64 = 0,
    /// Breaks inside the hidden fold ranges (cumulative), when folds and
    /// wrapping combine; valid for `hidden_key`.
    hidden_breaks: std.ArrayList(u32) = .empty,
    hidden_key: [3]u64 = @splat(std.math.maxInt(u64)),

    pub const Reflow = struct {
        ctx: *anyopaque,
        before: *const fn (ctx: *anyopaque) void,
        after: *const fn (ctx: *anyopaque) void,
    };

    pub fn deinit(self: *DisplayMap, gpa: Allocator) void {
        self.folds.deinit(gpa);
        self.dropJob();
        self.dropWraps(gpa);
        self.hidden_breaks.deinit(gpa);
    }

    /// Forces a full re-wrap (the current breaks show until it's done).
    pub fn invalidate(self: *DisplayMap) void {
        if (self.wraps) |*w| w.stale = true;
    }

    /// Takes effect at the next query: small buffers re-wrap then, big ones
    /// keep their current breaks until the background wrap lands.
    pub fn setWrap(self: *DisplayMap, cols: u32, tab_size: u32) void {
        self.wrap_cols = cols;
        self.tab_size = tab_size;
    }

    /// Keeps folds and wrap rows in step with a buffer edit.
    pub fn applyChange(self: *DisplayMap, gpa: Allocator, change: buffer_mod.Change) void {
        self.folds.applyChange(change);
        const e: RowEdit = .of(change);
        if (self.wraps) |*w| {
            self.wraps_version +%= 1;
            w.splice(gpa, e) catch self.dropWraps(gpa);
        }
        if (self.job) |job| {
            // A result that can't be brought up to date is thrown away.
            job.edits.append(gpa, e) catch job.cancel.store(true, .monotonic);
        }
    }

    fn dropWraps(self: *DisplayMap, gpa: Allocator) void {
        if (self.wraps) |*w| w.deinit(gpa);
        self.wraps = null;
        self.wraps_version +%= 1;
    }

    /// Lets go of the background job (it frees itself).
    fn dropJob(self: *DisplayMap) void {
        const job = self.job orelse return;
        self.job = null;
        job.map = null;
        job.cancel.store(true, .monotonic);
        // The worker checks `cancel` every chunk. Once it has let go its
        // completion is queued (or running now) and can be withdrawn.
        while (job.refs.load(.acquire) > 1) std.Thread.yield() catch {};
        if (job.executor.tryCancel(.{ .ctx = job, .run = Job.finished })) job.release();
    }

    fn params(self: *const DisplayMap) Params {
        return .{ .cols = self.wrap_cols, .tab_size = @max(1, self.tab_size), .indent = self.indent_continuations };
    }

    /// The breaks to show, up to date with edits, or null to show rows unwrapped.
    fn ensure(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer) ?*Wraps {
        if (self.wrap_cols == 0) {
            self.dropJob();
            if (self.wraps != null) self.dropWraps(gpa);
            return null;
        }
        const want = self.params();
        if (self.wraps) |*w| {
            if (w.dirty_rows > sync_rewrap_rows and self.executor != null) w.stale = true;
        }
        const current = if (self.wraps) |*w| !w.stale and w.params.eql(want) else false;
        if (!current) self.refresh(gpa, buffer, want);
        const w = if (self.wraps) |*x| x else return null;
        w.ensureSums(gpa) catch {
            self.dropWraps(gpa);
            return null;
        };
        if (w.rowCount() != buffer.lineCount()) {
            self.dropWraps(gpa);
            return null;
        }
        if (w.dirty_rows > 0 and (!w.stale or w.dirty_rows <= sync_rewrap_rows)) {
            self.wraps_version +%= 1;
            w.rewrap(gpa, &buffer.rope) catch {
                self.dropWraps(gpa);
                return null;
            };
            w.ensureSums(gpa) catch {
                self.dropWraps(gpa);
                return null;
            };
        }
        return w;
    }

    /// Starts wrapping with `want`: on the spot for small buffers, else in
    /// the background.
    fn refresh(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, want: Params) void {
        if (self.job) |job| {
            // Out of date: it stops, and the first query after it lands starts over.
            if (!job.params.eql(want)) job.cancel.store(true, .monotonic);
            return;
        }
        if (buffer.len() > self.sync_bytes) {
            if (self.executor) |executor| {
                self.startJob(gpa, executor, buffer, want) catch {};
                if (self.job != null) return;
            }
        }
        const w = Wraps.compute(gpa, &buffer.rope, want, null) catch return;
        self.dropWraps(gpa);
        self.wraps = w;
    }

    fn startJob(self: *DisplayMap, gpa: Allocator, executor: *Executor, buffer: *const Buffer, want: Params) !void {
        const job = try gpa.create(Job);
        job.* = .{ .gpa = gpa, .executor = executor, .map = self, .rope = buffer.rope.clone(), .params = want };
        executor.spawn(Job.run, .{job}) catch |err| {
            job.rope.deinit(gpa);
            gpa.destroy(job);
            return err;
        };
        self.job = job;
    }

    /// Main thread: the background wrap is done.
    fn land(self: *DisplayMap, job: *Job) void {
        std.debug.assert(self.job == job);
        const gpa = job.gpa;
        var w = job.result orelse {
            self.job = null;
            return;
        };
        if (job.cancel.load(.monotonic) or !job.params.eql(self.params())) {
            self.job = null;
            return;
        }
        job.result = null;
        for (job.edits.items) |e| w.splice(gpa, e) catch {
            w.deinit(gpa);
            self.job = null;
            return;
        };
        // Views anchor their scroll position while the old breaks still
        // answer (a job in flight keeps those queries from starting another).
        if (self.reflow) |r| r.before(r.ctx);
        self.dropWraps(gpa);
        self.wraps = w;
        self.job = null;
        if (self.reflow) |r| r.after(r.ctx);
    }

    // ---- Folds and wrap together ------------------------------------------

    /// Number of `ranges` (hidden fold rows, sorted) starting before `row`.
    fn rangesBefore(ranges: []const [2]u32, row: u32) usize {
        var lo: usize = 0;
        var hi: usize = ranges.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (ranges[mid][0] < row) lo = mid + 1 else hi = mid;
        }
        return lo;
    }

    /// `row`, or the header of the fold hiding it.
    fn visibleRow(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, row: u32) u32 {
        if (self.folds.isEmpty()) return row;
        const ranges, _ = self.folds.hiddenRanges(gpa, buffer);
        const k = rangesBefore(ranges, row + 1);
        if (k > 0 and row <= ranges[k - 1][1]) return ranges[k - 1][0] - 1;
        return row;
    }

    /// Breaks inside hidden ranges 0..k (exclusive).
    fn hiddenBreaksBefore(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, w: *const Wraps, ranges: []const [2]u32, k: usize) u32 {
        if (k == 0) return 0;
        const key = [3]u64{ self.folds.generation, buffer.version, self.wraps_version };
        if (!std.mem.eql(u64, &key, &self.hidden_key)) blk: {
            self.hidden_breaks.resize(gpa, ranges.len) catch {
                self.hidden_key = @splat(std.math.maxInt(u64));
                break :blk;
            };
            var total: u32 = 0;
            for (ranges, self.hidden_breaks.items) |r, *out| {
                total += w.breaksBefore(r[1] + 1) - w.breaksBefore(r[0]);
                out.* = total;
            }
            self.hidden_key = key;
        }
        if (std.mem.eql(u64, &key, &self.hidden_key)) return self.hidden_breaks.items[k - 1];
        var total: u32 = 0;
        for (ranges[0..k]) |r| total += w.breaksBefore(r[1] + 1) - w.breaksBefore(r[0]);
        return total;
    }

    /// Display rows before buffer row `row` (<= lineCount): the visible rows
    /// before it, with their continuation segments.
    fn rowsBefore(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, w: *const Wraps, row: u32) u32 {
        if (self.folds.isEmpty()) return row + w.breaksBefore(row);
        const ranges, const hidden_before = self.folds.hiddenRanges(gpa, buffer);
        var k = rangesBefore(ranges, row);
        // Inside a hidden range: as many as before its first row.
        const at = if (k > 0 and row <= ranges[k - 1][1]) blk: {
            k -= 1;
            break :blk ranges[k][0];
        } else row;
        const hidden_rows: u32 = if (k > 0) hidden_before[k - 1] else 0;
        return at - hidden_rows + w.breaksBefore(at) - self.hiddenBreaksBefore(gpa, buffer, w, ranges, k);
    }

    // ---- Queries ------------------------------------------------------------------

    pub fn displayRowCount(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer) u32 {
        const w = self.ensure(gpa, buffer) orelse return self.folds.displayRowCount(gpa, buffer);
        return self.rowsBefore(gpa, buffer, w, buffer.lineCount());
    }

    /// First display row of buffer row `row` (a hidden row maps to its fold header).
    pub fn displayRow(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, row: u32) u32 {
        const w = self.ensure(gpa, buffer) orelse return self.folds.displayRow(gpa, buffer, row);
        const r = self.visibleRow(gpa, buffer, @min(row, buffer.lineCount() - 1));
        return self.rowsBefore(gpa, buffer, w, r);
    }

    /// The segment shown on display row `d`.
    pub fn segmentAt(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, d: u32) Segment {
        const w = self.ensure(gpa, buffer) orelse {
            const r = @min(self.folds.bufferRow(gpa, buffer, d), buffer.lineCount() - 1);
            return .{ .row = r, .index = 0, .start = buffer.lineStart(r), .end = buffer.lineEnd(r), .indent_cols = 0 };
        };
        const lines = buffer.lineCount();
        const dd = @min(d, self.rowsBefore(gpa, buffer, w, lines) -| 1);
        // The last row starting at or before dd.
        var lo: u32 = 0;
        var hi: u32 = lines;
        while (lo + 1 < hi) {
            const mid = lo + (hi - lo) / 2;
            if (self.rowsBefore(gpa, buffer, w, mid) <= dd) lo = mid else hi = mid;
        }
        const r = self.visibleRow(gpa, buffer, lo);
        const index = @min(dd - self.rowsBefore(gpa, buffer, w, r), w.rowBreaks(r).len);
        return w.segmentOf(buffer, r, @intCast(index));
    }

    /// The segment containing `offset` (a break position belongs to the
    /// segment it starts, so a caret there shows at the start of the next line).
    pub fn segmentForOffset(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, offset: usize) Segment {
        const row = buffer.offsetToPoint(offset).row;
        const w = self.ensure(gpa, buffer) orelse {
            return .{ .row = row, .index = 0, .start = buffer.lineStart(row), .end = buffer.lineEnd(row), .indent_cols = 0 };
        };
        const col = offset - buffer.lineStart(row);
        const breaks = w.rowBreaks(row);
        // Breaks at or before `col`.
        var lo: usize = 0;
        var hi: usize = breaks.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (breaks[mid] <= col) lo = mid + 1 else hi = mid;
        }
        return w.segmentOf(buffer, row, @intCast(lo));
    }

    /// Display row of the segment containing `offset`.
    pub fn displayRowOfOffset(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, offset: usize) u32 {
        const seg = self.segmentForOffset(gpa, buffer, offset);
        return self.displayRow(gpa, buffer, seg.row) + seg.index;
    }

    pub fn isRowHidden(self: *DisplayMap, gpa: Allocator, buffer: *const Buffer, row: u32) bool {
        return self.folds.isHidden(gpa, buffer, row);
    }
};

// ---- Tests ----------------------------------------------------------------------

const testing = std.testing;

test "wrapping and display rows" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "short\nthe quick brown fox jumps over\n    indented words that wrap here\nend");
    defer buf.deinit();
    var dm: DisplayMap = .{};
    defer dm.deinit(gpa);
    try std.testing.expectEqual(@as(u32, 4), dm.displayRowCount(gpa, &buf));
    dm.setWrap(12, 4);
    // "the quick brown fox jumps over" → "the quick " / "brown fox " / "jumps over"
    const total = dm.displayRowCount(gpa, &buf);
    try std.testing.expect(total > 4);
    try std.testing.expectEqual(@as(u32, 1), dm.displayRow(gpa, &buf, 1));
    const s1 = dm.segmentAt(gpa, &buf, 2);
    try std.testing.expectEqual(@as(u32, 1), s1.row);
    try std.testing.expectEqual(@as(u32, 1), s1.index);
    var tmp: std.ArrayList(u8) = .empty;
    defer tmp.deinit(gpa);
    const piece = try buf.textRange(gpa, s1.start, s1.end);
    defer gpa.free(piece);
    try std.testing.expectEqualStrings("brown fox ", piece);
    // Continuations of the indented line are indented.
    const s_ind = dm.segmentAt(gpa, &buf, dm.displayRow(gpa, &buf, 2) + 1);
    try std.testing.expectEqual(@as(u32, 4), s_ind.indent_cols);
    // Offsets map to the segment that starts at them.
    try std.testing.expectEqual(@as(u32, 2), dm.displayRowOfOffset(gpa, &buf, s1.start));
    // The last row is unaffected.
    try std.testing.expectEqual(total - 1, dm.displayRow(gpa, &buf, 3));
}

test "folds with wrap" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "a\nb\nc\nd");
    defer buf.deinit();
    var dm: DisplayMap = .{};
    defer dm.deinit(gpa);
    try dm.folds.fold(gpa, &buf, 0, 2);
    dm.setWrap(80, 4);
    try std.testing.expectEqual(@as(u32, 2), dm.displayRowCount(gpa, &buf));
    try std.testing.expectEqual(@as(u32, 3), dm.segmentAt(gpa, &buf, 1).row);
    try std.testing.expectEqual(@as(u32, 0), dm.displayRow(gpa, &buf, 2));
}

/// Forwards buffer changes to a display map, like an editor view does.
const TestView = struct {
    dm: *DisplayMap,
    fn onChange(ctx: *anyopaque, _: *Buffer, change: buffer_mod.Change) void {
        const self: *TestView = @ptrCast(@alignCast(ctx));
        self.dm.applyChange(std.testing.allocator, change);
    }
};

test "incremental re-wrap matches a full wrap" {
    const gpa = std.testing.allocator;
    var buf = try Buffer.initText(gpa, 1, "one two three four five six\nseven eight\nnine ten eleven twelve thirteen\nend");
    defer buf.deinit();
    var dm: DisplayMap = .{};
    defer dm.deinit(gpa);
    dm.setWrap(10, 4);
    _ = dm.displayRowCount(gpa, &buf);
    var ctx: TestView = .{ .dm = &dm };
    try buf.addListener(.{ .ctx = &ctx, .onChange = TestView.onChange });
    // Split a line, join two lines and grow one, in one step.
    try buf.edit(&.{
        .{ .start = 8, .end = 8, .text = "\nnew line words here " },
        .{ .start = 39, .end = 41, .text = " x" },
    }, .other, &.{}, &.{});
    var fresh: DisplayMap = .{};
    defer fresh.deinit(gpa);
    fresh.setWrap(10, 4);
    const n = fresh.displayRowCount(gpa, &buf);
    try std.testing.expectEqual(n, dm.displayRowCount(gpa, &buf));
    var d: u32 = 0;
    while (d < n) : (d += 1) {
        const a = dm.segmentAt(gpa, &buf, d);
        const b = fresh.segmentAt(gpa, &buf, d);
        try std.testing.expectEqual(b.start, a.start);
        try std.testing.expectEqual(b.end, a.end);
    }
}

/// The line-at-a-time wrap the streaming `LineWrapper` replaced: appends the
/// break columns of `text` (one line) to `out`, returns the continuation indent.
fn referenceWrap(gpa: Allocator, p: Params, text: []const u8, out: *std.ArrayList(u32)) !u32 {
    const width = p.cols;
    var lead: u32 = 0;
    for (text) |ch| {
        if (ch == ' ') lead += 1 else if (ch == '\t') lead += p.tab_size - lead % p.tab_size else break;
    }
    const indent = if (p.indent and lead < width / 2) lead else 0;
    var col: u32 = 0;
    var abs_col: u32 = 0;
    var seg_start: usize = 0;
    var last_break: ?usize = null;
    var i: usize = 0;
    while (i < text.len) {
        const len = std.unicode.utf8ByteSequenceLength(text[i]) catch 1;
        const cp: u21 = if (i + len <= text.len) std.unicode.utf8Decode(text[i .. i + len]) catch 0xFFFD else 0xFFFD;
        const w: u32 = if (cp == '\t') p.tab_size - abs_col % p.tab_size else charWidth(cp);
        const lim = if (seg_start == 0) width else width -| indent;
        if (col + w > lim and i > seg_start) {
            const at = if (last_break) |b| (if (b > seg_start) b else i) else i;
            try out.append(gpa, @intCast(at));
            col = 0;
            var j = at;
            while (j < i) {
                const l2 = std.unicode.utf8ByteSequenceLength(text[j]) catch 1;
                const c2: u21 = std.unicode.utf8Decode(text[j..@min(j + l2, text.len)]) catch 0xFFFD;
                col += if (c2 == '\t') p.tab_size else charWidth(c2);
                j += l2;
            }
            seg_start = at;
            last_break = null;
            continue;
        }
        col += w;
        abs_col += w;
        i += len;
        if (cp == ' ' or cp == '\t') last_break = i;
    }
    return indent;
}

fn randomProse(gpa: Allocator, rand: std.Random, pieces: usize) ![]u8 {
    const words = [_][]const u8{ "a", "an", "the", "quick", "brown", "fox", " ", " ", "  ", "\t", "\n", "\n", "    ", "漢字", "😀", "ünïcødé", "supercalifragilisticexpialidocious", repeat("x", 70), repeat("{\"k\":[1,2],\"v\":\"ab\"}", 90) };
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    for (0..pieces) |_| {
        try out.appendSlice(gpa, words[rand.uintLessThan(usize, words.len)]);
        if (rand.uintLessThan(u32, 4) != 0) try out.append(gpa, ' ');
    }
    return out.toOwnedSlice(gpa);
}

/// Checks every row of `dm`'s current wrap against `referenceWrap`.
fn expectMatchesReference(gpa: Allocator, dm: *DisplayMap, buf: *const Buffer) !void {
    const w = dm.ensure(gpa, buf).?;
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(gpa);
    var expected: std.ArrayList(u32) = .empty;
    defer expected.deinit(gpa);
    for (w.blocks.items) |*b| {
        try testing.expect(b.rowCount() > 0 and b.rowCount() <= block_rows);
        try testing.expect(b.dirty_lo >= b.dirty_hi);
    }
    var row: u32 = 0;
    while (row < buf.lineCount()) : (row += 1) {
        line.clearRetainingCapacity();
        const text = try buf.lineText(row, &line);
        expected.clearRetainingCapacity();
        const indent = try referenceWrap(gpa, w.params, text, &expected);
        try testing.expectEqualSlices(u32, expected.items, w.rowBreaks(row));
        if (expected.items.len > 0) {
            const seg = dm.segmentAt(gpa, buf, dm.displayRow(gpa, buf, row) + 1);
            try testing.expectEqual(indent, seg.indent_cols);
        }
    }
}

test "streaming wrap matches the line-at-a-time reference" {
    const gpa = testing.allocator;
    var prng = std.Random.DefaultPrng.init(7);
    const text = try randomProse(gpa, prng.random(), 4000);
    defer gpa.free(text);
    var buf = try Buffer.initText(gpa, 1, text);
    defer buf.deinit();
    for ([_]u32{ 1, 2, 3, 5, 8, 13, 40, 200 }) |cols| {
        for ([_]u32{ 1, 4, 8 }) |tab| {
            for ([_]bool{ true, false }) |indent| {
                var dm: DisplayMap = .{ .indent_continuations = indent };
                defer dm.deinit(gpa);
                dm.setWrap(cols, tab);
                try expectMatchesReference(gpa, &dm, &buf);
            }
        }
    }
}

test "random edits keep the wrap in step with a full re-wrap" {
    const gpa = testing.allocator;
    var prng = std.Random.DefaultPrng.init(11);
    const rand = prng.random();
    const text = try randomProse(gpa, rand, 12_000); // ~3000 rows: several blocks
    defer gpa.free(text);
    var buf = try Buffer.initText(gpa, 1, text);
    defer buf.deinit();
    var dm: DisplayMap = .{};
    defer dm.deinit(gpa);
    dm.setWrap(17, 4);
    _ = dm.displayRowCount(gpa, &buf);
    var view: TestView = .{ .dm = &dm };
    try buf.addListener(.{ .ctx = &view, .onChange = TestView.onChange });
    for (0..120) |iter| {
        const len = buf.len();
        const a = buf.rope.clipOffset(rand.uintAtMost(usize, len), .left);
        const span: usize = switch (rand.uintLessThan(u32, 6)) {
            0 => 20_000, // across blocks
            1 => 0,
            else => 30,
        };
        const b = buf.rope.clipOffset(@min(len, a + rand.uintAtMost(usize, span)), .left);
        const ins = try randomProse(gpa, rand, if (rand.uintLessThan(u32, 8) == 0) 3000 else rand.uintAtMost(usize, 6));
        defer gpa.free(ins);
        try buf.edit(&.{.{ .start = a, .end = b, .text = ins }}, .other, &.{}, &.{});
        if (iter % 4 == 0) {
            try expectMatchesReference(gpa, &dm, &buf);
            var fresh: DisplayMap = .{};
            defer fresh.deinit(gpa);
            fresh.setWrap(17, 4);
            try testing.expectEqual(fresh.displayRowCount(gpa, &buf), dm.displayRowCount(gpa, &buf));
        }
    }
    buf.removeListener(&view);
}

/// Display rows computed one buffer row at a time from `dm`'s breaks.
fn expectFoldsAndWrapAgree(gpa: Allocator, dm: *DisplayMap, buf: *const Buffer) !void {
    const w = dm.ensure(gpa, buf).?;
    var d: u32 = 0;
    var row: u32 = 0;
    while (row < buf.lineCount()) : (row += 1) {
        if (dm.folds.isHidden(gpa, buf, row)) continue;
        try testing.expectEqual(d, dm.displayRow(gpa, buf, row));
        const segs: u32 = @intCast(w.rowBreaks(row).len + 1);
        for (0..segs) |k| {
            const seg = dm.segmentAt(gpa, buf, d + @as(u32, @intCast(k)));
            try testing.expectEqual(row, seg.row);
            try testing.expectEqual(@as(u32, @intCast(k)), seg.index);
        }
        d += segs;
    }
    try testing.expectEqual(d, dm.displayRowCount(gpa, buf));
    // Hidden rows map to their fold's header.
    row = 0;
    while (row < buf.lineCount()) : (row += 1) {
        if (!dm.folds.isHidden(gpa, buf, row)) continue;
        var header = row;
        while (dm.folds.isHidden(gpa, buf, header)) header -= 1;
        try testing.expectEqual(dm.displayRow(gpa, buf, header), dm.displayRow(gpa, buf, row));
    }
}

test "folds and wrap combine like a row-by-row count" {
    const gpa = testing.allocator;
    var prng = std.Random.DefaultPrng.init(3);
    const rand = prng.random();
    const text = try randomProse(gpa, rand, 2500);
    defer gpa.free(text);
    var buf = try Buffer.initText(gpa, 1, text);
    defer buf.deinit();
    var dm: DisplayMap = .{};
    defer dm.deinit(gpa);
    dm.setWrap(11, 4);
    const lines = buf.lineCount();
    for (0..30) |i| {
        const header = rand.uintLessThan(u32, lines - 1);
        const last = @min(lines - 1, header + 1 + rand.uintLessThan(u32, 40));
        try dm.folds.fold(gpa, &buf, header, last);
        if (i % 5 == 0) try expectFoldsAndWrapAgree(gpa, &dm, &buf);
    }
    try expectFoldsAndWrapAgree(gpa, &dm, &buf);
    // A fold reaching the last row.
    try dm.folds.fold(gpa, &buf, lines - 5, lines - 1);
    try expectFoldsAndWrapAgree(gpa, &dm, &buf);
    var last_visible = lines - 5;
    while (dm.folds.isHidden(gpa, &buf, last_visible)) last_visible -= 1;
    try testing.expectEqual(last_visible, dm.segmentAt(gpa, &buf, std.math.maxInt(u32)).row);
}

fn waitForJobs(executor: *Executor) void {
    while (executor.active.load(.acquire) > 0) std.Thread.yield() catch {};
    executor.drain();
}

test "background wrap: edits made meanwhile are replayed, then a resize" {
    const gpa = testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    var prng = std.Random.DefaultPrng.init(5);
    const rand = prng.random();
    const text = try randomProse(gpa, rand, 6000);
    defer gpa.free(text);
    var buf = try Buffer.initText(gpa, 1, text);
    defer buf.deinit();
    var dm: DisplayMap = .{ .executor = &executor, .sync_bytes = 0 };
    defer dm.deinit(gpa);
    const Counter = struct {
        before: u32 = 0,
        after: u32 = 0,
        fn onBefore(ctx: *anyopaque) void {
            const c: *@This() = @ptrCast(@alignCast(ctx));
            c.before += 1;
        }
        fn onAfter(ctx: *anyopaque) void {
            const c: *@This() = @ptrCast(@alignCast(ctx));
            c.after += 1;
        }
    };
    var counter: Counter = .{};
    dm.reflow = .{ .ctx = &counter, .before = Counter.onBefore, .after = Counter.onAfter };
    var view: TestView = .{ .dm = &dm };
    try buf.addListener(.{ .ctx = &view, .onChange = TestView.onChange });
    defer buf.removeListener(&view);

    dm.setWrap(13, 4);
    // Unwrapped until the job lands (it can't before the executor drains).
    try testing.expectEqual(buf.lineCount(), dm.displayRowCount(gpa, &buf));
    try testing.expect(dm.job != null);
    try buf.edit(&.{.{ .start = 10, .end = 10, .text = "a fresh line of words\nand another one\n" }}, .other, &.{}, &.{});
    try buf.edit(&.{.{ .start = 100, .end = 400, .text = "" }}, .other, &.{}, &.{});
    try buf.edit(&.{.{ .start = buf.len(), .end = buf.len(), .text = "\nthe end of it all, wrapped" }}, .other, &.{}, &.{});
    waitForJobs(&executor);
    try testing.expect(dm.job == null and dm.wraps != null);
    try testing.expectEqual(@as(u32, 1), counter.after);
    try expectMatchesReference(gpa, &dm, &buf);

    // A resize keeps showing the old breaks until the new ones land.
    const old_rows = dm.displayRowCount(gpa, &buf);
    dm.setWrap(29, 4);
    try testing.expectEqual(old_rows, dm.displayRowCount(gpa, &buf));
    // ...and a second resize while that runs replaces it.
    dm.setWrap(31, 4);
    try testing.expectEqual(old_rows, dm.displayRowCount(gpa, &buf));
    waitForJobs(&executor); // the outdated job lands and is dropped
    _ = dm.displayRowCount(gpa, &buf); // starts the 31-column one
    waitForJobs(&executor);
    try testing.expectEqual(@as(u32, 31), dm.wraps.?.params.cols);
    try expectMatchesReference(gpa, &dm, &buf);

    // Wrap off frees the breaks right away.
    dm.setWrap(0, 4);
    try testing.expectEqual(buf.lineCount(), dm.displayRowCount(gpa, &buf));
    try testing.expect(dm.wraps == null);
}

test "a map can go away with a background wrap in flight" {
    const gpa = testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const text = repeat("lots of words to wrap around here\n", 3000);
    var buf = try Buffer.initText(gpa, 1, text);
    defer buf.deinit();
    for (0..3) |variant| {
        var dm: DisplayMap = .{ .executor = &executor, .sync_bytes = 0 };
        dm.setWrap(9, 4);
        _ = dm.displayRowCount(gpa, &buf);
        try testing.expect(dm.job != null);
        switch (variant) {
            0 => {}, // still running
            1 => while (executor.active.load(.acquire) > 0) std.Thread.yield() catch {}, // done, completion queued
            else => { // cancelled
                dm.setWrap(4, 4);
                _ = dm.displayRowCount(gpa, &buf);
            },
        }
        dm.deinit(gpa);
        waitForJobs(&executor);
    }
}

test "a background wrap is freed before its thread reports done" {
    // Waiting for the executor to go idle (tests, the app's teardown) must
    // mean the jobs are freed. Here the map lets go first: the worker
    // drains inside `post`, once the test knows which job to watch.
    const gpa = testing.allocator;
    const Watch = struct {
        var executor: Executor = undefined;
        var job: std.atomic.Value(usize) = .init(0);
        var freed: std.atomic.Value(bool) = .init(false);
        var freed_idle = false;
        var child_free: *const fn (*anyopaque, []u8, std.mem.Alignment, usize) void = undefined;

        fn drainWhenWatched(_: *anyopaque) void {
            while (job.load(.acquire) == 0) std.Thread.yield() catch {};
            executor.drain();
        }

        fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {
            if (@intFromPtr(memory.ptr) == job.load(.acquire)) {
                freed_idle = executor.active.load(.acquire) == 0;
                freed.store(true, .release);
            }
            child_free(ctx, memory, alignment, ret_addr);
        }
    };
    Watch.executor = .init(gpa);
    defer Watch.executor.deinit();
    Watch.executor.waker = Watch.drainWhenWatched;
    Watch.executor.waker_ctx = &Watch.executor;
    var vtable = gpa.vtable.*;
    Watch.child_free = vtable.free;
    vtable.free = Watch.free;
    const watched: Allocator = .{ .ptr = gpa.ptr, .vtable = &vtable };

    var buf = try Buffer.initText(gpa, 1, repeat("lots of words to wrap around here\n", 3000));
    defer buf.deinit();
    var dm: DisplayMap = .{ .executor = &Watch.executor, .sync_bytes = 0 };
    defer dm.deinit(gpa);
    dm.setWrap(9, 4);
    _ = dm.displayRowCount(watched, &buf);
    Watch.job.store(@intFromPtr(dm.job.?), .release);
    while (!Watch.freed.load(.acquire)) std.Thread.yield() catch {};
    while (Watch.executor.active.load(.acquire) > 0) std.Thread.yield() catch {};
    try testing.expect(!Watch.freed_idle);
    try testing.expect(dm.wraps != null);
}

test "an edit in a very long row shifts its breaks until a full re-wrap" {
    const gpa = testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const long = repeat("{\"k\":[1,2],\"v\":\"ab\"}", 3000); // 66 KB, no blanks: 5000 breaks at 13 columns
    var buf = try Buffer.initText(gpa, 1, "short line\n" ++ long ++ "\nlast line");
    defer buf.deinit();
    for ([_]?*Executor{ &executor, null }) |ex| {
        var dm: DisplayMap = .{ .executor = ex, .sync_bytes = 0 };
        defer dm.deinit(gpa);
        var view: TestView = .{ .dm = &dm };
        try buf.addListener(.{ .ctx = &view, .onChange = TestView.onChange });
        defer buf.removeListener(&view);
        dm.setWrap(13, 4);
        _ = dm.displayRowCount(gpa, &buf);
        waitForJobs(&executor);
        try expectMatchesReference(gpa, &dm, &buf);
        const before = dm.displayRowCount(gpa, &buf);

        const at = buf.lineStart(1) + 30_000;
        try buf.edit(&.{.{ .start = at, .end = at, .text = "xyz" }}, .typing, &.{}, &.{});
        if (ex != null) {
            // Shifted breaks (not an unwrapped row) until the background wrap lands.
            try testing.expect(dm.wraps.?.stale);
            try testing.expectEqual(before, dm.displayRowCount(gpa, &buf));
            try testing.expect(dm.job != null);
        }
        try buf.edit(&.{.{ .start = at + 1, .end = at + 7, .text = "" }}, .deleting, &.{}, &.{});
        for (0..2) |_| {
            _ = dm.displayRowCount(gpa, &buf);
            waitForJobs(&executor);
        }
        try expectMatchesReference(gpa, &dm, &buf);
    }
}
