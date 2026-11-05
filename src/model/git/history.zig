//! Commit history: `git log` parsed into commits, and the lanes a graph
//! view draws them in. A lane carries a line down the page to the commit
//! it waits for; a commit sits in the lane that waits for it, merges the
//! other lanes waiting for it into its own, and hands one lane to each
//! parent (the first parent keeps the commit's lane).

const std = @import("std");

const Allocator = std.mem.Allocator;

/// `git log -z` with this format: fields separated by 0x1f, records by NUL.
pub const log_format = "--format=%H%x1f%P%x1f%an%x1f%at%x1f%D%x1f%s";

pub const Commit = struct {
    hash: []const u8,
    parents: []const []const u8,
    author: []const u8,
    time: i64,
    /// What points at it: "HEAD -> main, origin/main, tag: v1".
    refs: []const u8,
    subject: []const u8,

    pub fn short(self: *const Commit) []const u8 {
        return self.hash[0..@min(7, self.hash.len)];
    }
};

pub const Log = struct {
    arena: std.heap.ArenaAllocator,
    commits: []Commit,

    pub fn deinit(self: *Log) void {
        self.arena.deinit();
    }
};

/// Parses `git log -z <log_format>` output (copied into the result).
pub fn parse(gpa: Allocator, output: []const u8) !Log {
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const a = arena.allocator();
    var commits: std.ArrayList(Commit) = .empty;
    var records = std.mem.splitScalar(u8, output, 0);
    while (records.next()) |rec| {
        const text = std.mem.trimStart(u8, rec, "\n");
        if (text.len == 0) continue;
        var f = std.mem.splitScalar(u8, text, 0x1f);
        const hash = f.next() orelse continue;
        const parents_text = f.next() orelse continue;
        const author = f.next() orelse continue;
        const time_text = f.next() orelse continue;
        const refs = f.next() orelse continue;
        const subject = f.rest();
        if (hash.len < 7) continue;
        var parents: std.ArrayList([]const u8) = .empty;
        var pit = std.mem.tokenizeScalar(u8, parents_text, ' ');
        while (pit.next()) |p| try parents.append(a, try a.dupe(u8, p));
        try commits.append(a, .{
            .hash = try a.dupe(u8, hash),
            .parents = try parents.toOwnedSlice(a),
            .author = try a.dupe(u8, author),
            .time = std.fmt.parseInt(i64, time_text, 10) catch 0,
            .refs = try a.dupe(u8, refs),
            .subject = try a.dupe(u8, subject),
        });
    }
    return .{ .arena = arena, .commits = try commits.toOwnedSlice(a) };
}

/// What one lane does in one row of the graph.
pub const Cell = struct {
    /// A line arrives from above.
    above: bool = false,
    /// A line leaves below.
    below: bool = false,
    /// A lane that ends at this commit (from above, into its node).
    merge_in: bool = false,
    /// A lane that starts at this commit (its node, down into the lane).
    out: bool = false,
};

pub const GraphRow = struct {
    /// The commit's lane.
    lane: u16,
    cells: []Cell,
};

pub const Graph = struct {
    arena: std.heap.ArenaAllocator,
    rows: []GraphRow,
    /// The most lanes any row has.
    width: u16,

    pub fn deinit(self: *Graph) void {
        self.arena.deinit();
    }
};

/// Lays `commits` (newest first, children before parents) out in lanes.
pub fn layout(gpa: Allocator, commits: []const Commit) !Graph {
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const a = arena.allocator();
    var rows = try a.alloc(GraphRow, commits.len);
    var width: u16 = 0;
    // The commit each lane waits for.
    var active: std.ArrayList(?[]const u8) = .empty;
    defer active.deinit(gpa);
    var before: std.ArrayList(bool) = .empty;
    defer before.deinit(gpa);

    for (commits, 0..) |c, ri| {
        before.clearRetainingCapacity();
        for (active.items) |x| try before.append(gpa, x != null);

        var lane: ?usize = null;
        var merges: std.ArrayList(usize) = .empty;
        defer merges.deinit(gpa);
        for (active.items, 0..) |x, j| {
            const want = x orelse continue;
            if (!std.mem.eql(u8, want, c.hash)) continue;
            if (lane == null) lane = j else try merges.append(gpa, j);
        }
        const node = lane orelse blk: {
            const free = for (active.items, 0..) |x, j| {
                if (x == null) break j;
            } else null;
            if (free) |j| break :blk j;
            try active.append(gpa, null);
            try before.append(gpa, false);
            break :blk active.items.len - 1;
        };
        for (merges.items) |m| active.items[m] = null;

        var outs: std.ArrayList(usize) = .empty;
        defer outs.deinit(gpa);
        if (c.parents.len == 0) {
            active.items[node] = null;
        } else {
            active.items[node] = c.parents[0];
            for (c.parents[1..]) |p| {
                const existing = for (active.items, 0..) |x, j| {
                    if (x != null and std.mem.eql(u8, x.?, p)) break j;
                } else null;
                if (existing) |j| {
                    try outs.append(gpa, j);
                    continue;
                }
                const free = for (active.items, 0..) |x, j| {
                    if (x == null and j != node) break j;
                } else null;
                const k = free orelse blk: {
                    try active.append(gpa, null);
                    try before.append(gpa, false);
                    break :blk active.items.len - 1;
                };
                active.items[k] = p;
                try outs.append(gpa, k);
            }
        }

        const n = @max(before.items.len, active.items.len);
        const cells = try a.alloc(Cell, n);
        @memset(cells, .{});
        for (cells, 0..) |*cell, j| {
            cell.above = j < before.items.len and before.items[j];
            cell.below = j < active.items.len and active.items[j] != null;
        }
        for (merges.items) |m| cells[m].merge_in = true;
        for (outs.items) |k| cells[k].out = true;
        cells[node].above = node < before.items.len and before.items[node];
        rows[ri] = .{ .lane = @intCast(node), .cells = cells };
        width = @max(width, @as(u16, @intCast(n)));
        // Lanes that ended are dropped from the right edge.
        while (active.items.len > 0 and active.items[active.items.len - 1] == null) _ = active.pop();
    }
    return .{ .arena = arena, .rows = rows, .width = width };
}

/// The files a commit changed: `git show --name-status --format= -z`.
pub const ChangedFile = struct {
    /// A, M, D, R, C, T.
    status: u8,
    path: []const u8,
    /// Renames and copies: the path before.
    old_path: ?[]const u8 = null,
};

pub fn parseNameStatus(a: Allocator, output: []const u8) ![]ChangedFile {
    var files: std.ArrayList(ChangedFile) = .empty;
    var it = std.mem.splitScalar(u8, output, 0);
    while (it.next()) |field| {
        const code = std.mem.trimStart(u8, field, "\n");
        if (code.len == 0) continue;
        const status = code[0];
        if (status == 'R' or status == 'C') {
            const old = it.next() orelse break;
            const new = it.next() orelse break;
            try files.append(a, .{ .status = status, .path = try a.dupe(u8, new), .old_path = try a.dupe(u8, old) });
        } else {
            const path = it.next() orelse break;
            try files.append(a, .{ .status = status, .path = try a.dupe(u8, path) });
        }
    }
    return files.toOwnedSlice(a);
}

const testing = std.testing;

fn testCommit(hash: []const u8, parents: []const []const u8) Commit {
    return .{ .hash = hash, .parents = parents, .author = "a", .time = 0, .refs = "", .subject = "s" };
}

test "log records parse: parents, author, time, refs and subject" {
    const gpa = testing.allocator;
    const out = "aaaaaaa111\x1fbbbbbbb222 ccccccc333\x1fAda\x1f1700000000\x1fHEAD -> main, tag: v1\x1fMerge it\x00" ++
        "\nbbbbbbb222\x1f\x1fBob\x1f1690000000\x1f\x1fRoot: with \x1f in? no\x00";
    var log = try parse(gpa, out);
    defer log.deinit();
    try testing.expectEqual(@as(usize, 2), log.commits.len);
    const c = log.commits[0];
    try testing.expectEqualStrings("aaaaaaa111", c.hash);
    try testing.expectEqual(@as(usize, 2), c.parents.len);
    try testing.expectEqualStrings("ccccccc333", c.parents[1]);
    try testing.expectEqualStrings("Ada", c.author);
    try testing.expectEqual(@as(i64, 1700000000), c.time);
    try testing.expectEqualStrings("HEAD -> main, tag: v1", c.refs);
    try testing.expectEqualStrings("aaaaaaa", c.short());
    try testing.expectEqual(@as(usize, 0), log.commits[1].parents.len);
}

test "a straight history stays in lane 0" {
    const gpa = testing.allocator;
    const commits = [_]Commit{
        testCommit("ccccccc", &.{"bbbbbbb"}),
        testCommit("bbbbbbb", &.{"aaaaaaa"}),
        testCommit("aaaaaaa", &.{}),
    };
    var g = try layout(gpa, &commits);
    defer g.deinit();
    try testing.expectEqual(@as(u16, 1), g.width);
    for (g.rows) |r| try testing.expectEqual(@as(u16, 0), r.lane);
    try testing.expect(!g.rows[0].cells[0].above and g.rows[0].cells[0].below);
    try testing.expect(g.rows[1].cells[0].above and g.rows[1].cells[0].below);
    try testing.expect(g.rows[2].cells[0].above and !g.rows[2].cells[0].below);
}

test "a branch and its merge use a second lane, and the lane ends where it joins" {
    const gpa = testing.allocator;
    // m merges f (a feature) into main; both come from base.
    const commits = [_]Commit{
        testCommit("mmmmmmm", &.{ "ttttttt", "fffffff" }),
        testCommit("ttttttt", &.{"bbbbbbb"}),
        testCommit("fffffff", &.{"bbbbbbb"}),
        testCommit("bbbbbbb", &.{}),
    };
    var g = try layout(gpa, &commits);
    defer g.deinit();
    try testing.expectEqual(@as(u16, 2), g.width);
    // m: lane 0, and a line out into lane 1 for its second parent.
    try testing.expectEqual(@as(u16, 0), g.rows[0].lane);
    try testing.expect(g.rows[0].cells[1].out and g.rows[0].cells[1].below);
    // t stays in lane 0; f sits in lane 1.
    try testing.expectEqual(@as(u16, 0), g.rows[1].lane);
    try testing.expect(g.rows[1].cells[1].above and g.rows[1].cells[1].below);
    try testing.expectEqual(@as(u16, 1), g.rows[2].lane);
    // The base is waited for by both lanes: it takes lane 0 and lane 1 ends in it.
    try testing.expectEqual(@as(u16, 0), g.rows[3].lane);
    try testing.expect(g.rows[3].cells[1].merge_in and !g.rows[3].cells[1].below);
}

test "name-status output with renames" {
    const gpa = testing.allocator;
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const files = try parseNameStatus(arena.allocator(), "M\x00src/a.zig\x00A\x00new.txt\x00R100\x00old name.txt\x00new name.txt\x00D\x00gone\x00");
    try testing.expectEqual(@as(usize, 4), files.len);
    try testing.expectEqual(@as(u8, 'M'), files[0].status);
    try testing.expectEqualStrings("src/a.zig", files[0].path);
    try testing.expectEqual(@as(u8, 'R'), files[2].status);
    try testing.expectEqualStrings("old name.txt", files[2].old_path.?);
    try testing.expectEqualStrings("new name.txt", files[2].path);
    try testing.expectEqual(@as(u8, 'D'), files[3].status);
}
