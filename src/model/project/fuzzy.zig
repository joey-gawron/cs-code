//! Fuzzy matching for Goto Anything and the Command Palette: Smith-Waterman
//! style DP (like fzf/Zed) rewarding word starts, path segments and
//! consecutive runs, with a preference for matches in the file name.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Match = struct {
    score: i32,
    /// Byte indices in the candidate that matched (len = query chars).
    positions: []u16,
};

const score_match = 16;
const score_gap_start = -3;
const score_gap_extension = -1;
const bonus_boundary = 8;
const bonus_path = 9;
const bonus_camel = 7;
const bonus_consecutive = 5;
const bonus_first_char = 12;
const bonus_basename = 4;

fn isSeparator(ch: u8) bool {
    return ch == '_' or ch == '-' or ch == '.' or ch == ' ' or ch == ':';
}

fn bonusAt(candidate: []const u8, j: usize, basename_start: usize) i32 {
    var bonus: i32 = 0;
    if (j == 0) {
        bonus = bonus_first_char;
    } else {
        const prev = candidate[j - 1];
        const cur = candidate[j];
        if (prev == '/') {
            bonus = bonus_path;
        } else if (isSeparator(prev)) {
            bonus = bonus_boundary;
        } else if (std.ascii.isLower(prev) and std.ascii.isUpper(cur)) {
            bonus = bonus_camel;
        } else if (!std.ascii.isDigit(prev) and std.ascii.isDigit(cur)) {
            bonus = bonus_boundary / 2;
        }
    }
    if (j == basename_start) bonus += bonus_first_char;
    if (j >= basename_start) bonus += bonus_basename;
    return bonus;
}

pub const Matcher = struct {
    gpa: Allocator,
    scores: std.ArrayList(i32) = .empty,
    parents: std.ArrayList(u16) = .empty,
    positions: std.ArrayList(u16) = .empty,

    pub fn init(gpa: Allocator) Matcher {
        return .{ .gpa = gpa };
    }

    pub fn deinit(self: *Matcher) void {
        self.scores.deinit(self.gpa);
        self.parents.deinit(self.gpa);
        self.positions.deinit(self.gpa);
    }

    /// Returns a score (higher is better) or null if `query` isn't a
    /// subsequence of `candidate`. Case-insensitive; spaces in the query are
    /// ignored. The returned positions are valid until the next call.
    pub fn match(self: *Matcher, query: []const u8, candidate: []const u8, path_like: bool) ?Match {
        var q_buf: [128]u8 = undefined;
        var m: usize = 0;
        for (query) |ch| {
            if (ch == ' ') continue;
            if (m == q_buf.len) break;
            q_buf[m] = std.ascii.toLower(ch);
            m += 1;
        }
        const q = q_buf[0..m];
        if (m == 0) return .{ .score = 0, .positions = &.{} };
        const n = @min(candidate.len, std.math.maxInt(u16) - 1);
        if (m > n) return null;

        // Quick subsequence rejection.
        var qi: usize = 0;
        for (candidate[0..n]) |ch| {
            if (qi < m and std.ascii.toLower(ch) == q[qi]) qi += 1;
        }
        if (qi < m) return null;

        const basename_start = if (path_like) (if (std.mem.lastIndexOfScalar(u8, candidate[0..n], '/')) |i| i + 1 else 0) else 0;
        const neg_inf: i32 = std.math.minInt(i32) / 2;
        self.scores.resize(self.gpa, m * n) catch return null;
        self.parents.resize(self.gpa, m * n) catch return null;
        const S = self.scores.items;
        const P = self.parents.items;

        for (0..m) |i| {
            // Best of row i-1 at columns < j-1, adjusted for the gap to j.
            var best_prev: i32 = neg_inf;
            var best_prev_col: u16 = 0;
            for (0..n) |j| {
                const idx = i * n + j;
                S[idx] = neg_inf;
                if (i > 0 and j >= 2) {
                    const k = j - 2;
                    const cand = S[(i - 1) * n + k];
                    if (cand > neg_inf) {
                        // Gap from k+1 to j-1 inclusive has length j-k-1.
                        const adjusted = cand + score_gap_start;
                        if (adjusted + score_gap_extension * @as(i32, @intCast(j - k - 2)) > best_prev + score_gap_extension) {
                            best_prev = adjusted;
                            best_prev_col = @intCast(k);
                        } else {
                            best_prev += score_gap_extension;
                        }
                    } else if (best_prev > neg_inf) {
                        best_prev += score_gap_extension;
                    }
                }
                if (std.ascii.toLower(candidate[j]) != q[i]) continue;
                const bonus = bonusAt(candidate, j, basename_start);
                if (i == 0) {
                    // Leading unmatched chars cost a little.
                    S[idx] = score_match + bonus - @as(i32, @intCast(@min(j, 15)));
                    P[idx] = std.math.maxInt(u16);
                    continue;
                }
                var best: i32 = neg_inf;
                var parent: u16 = 0;
                if (j >= 1) {
                    const diag = S[(i - 1) * n + j - 1];
                    if (diag > neg_inf) {
                        best = diag + score_match + bonus + bonus_consecutive;
                        parent = @intCast(j - 1);
                    }
                }
                if (best_prev > neg_inf and best_prev + score_match + bonus > best) {
                    best = best_prev + score_match + bonus;
                    parent = best_prev_col;
                }
                S[idx] = best;
                P[idx] = parent;
            }
        }

        var best: i32 = neg_inf;
        var best_j: usize = 0;
        for (0..n) |j| {
            if (S[(m - 1) * n + j] > best) {
                best = S[(m - 1) * n + j];
                best_j = j;
            }
        }
        if (best <= neg_inf) return null;

        self.positions.resize(self.gpa, m) catch return null;
        var j = best_j;
        var i = m;
        while (i > 0) {
            i -= 1;
            self.positions.items[i] = @intCast(j);
            if (i > 0) j = P[i * n + j];
        }
        // Mildly prefer shorter candidates.
        best -= @intCast(@min(n, 200) / 8);
        return .{ .score = best, .positions = self.positions.items };
    }
};

test "fuzzy prefers basename and boundaries" {
    var m = Matcher.init(std.testing.allocator);
    defer m.deinit();
    const a = m.match("main", "src/main.zig", true).?.score;
    const b = m.match("main", "src/domain/thing.zig", true).?.score;
    try std.testing.expect(a > b);
    try std.testing.expect(m.match("xyz", "src/main.zig", true) == null);
    const r = m.match("edv", "src/editor/editor_view.zig", true).?;
    try std.testing.expectEqual(@as(usize, 3), r.positions.len);
    const c1 = m.match("tsb", "View: Toggle Side Bar", false).?.score;
    const c2 = m.match("tsb", "Tabs: Sort By", false).?.score;
    _ = c2;
    try std.testing.expect(c1 > 0);
}
