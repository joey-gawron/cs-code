//! Vim-style window management (used by vim/window.zig, the directional
//! focus actions and Sublime's View > Layout presets). As in vim, the groups
//! are the leaves of a split tree: a split lays its children side by side or
//! stacked, each with a share of its area, so splitting one group leaves the
//! others as they are. The planning functions are pure (a layout in, a group
//! mapping out), so the transitions are unit-tested; `apply` carries a plan
//! out.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");

const Workspace = workspace_mod.Workspace;
const GroupLayout = workspace_mod.GroupLayout;
const Editor = editor_mod.Editor;
const max_groups = workspace_mod.max_groups;

pub const Dir = enum { left, right, up, down };

/// The smallest share of the editor area a group is sized to (as when dragging).
const min_share: f32 = 0.1;

const identity: [max_groups]u8 = blk: {
    var a: [max_groups]u8 = undefined;
    for (&a, 0..) |*x, i| x.* = @intCast(i);
    break :blk a;
};

/// Marks the leaf a plan adds until `normalize` numbers it.
const fresh: u8 = max_groups;

pub const Node = struct {
    /// Leaves: the group shown; null for a split.
    group: ?u8 = null,
    /// Splits: children side by side (vim's :vsplit) rather than stacked.
    across: bool = false,
    children: [max_groups]u8 = @splat(0),
    /// Each child's share of the split's width (height), summing to 1.
    shares: [max_groups]f32 = @splat(0),
    n: u8 = 0,

    fn leaf(g: u8) Node {
        return .{ .group = g };
    }

    /// A split of `children` in equal shares.
    fn split(across: bool, children: []const u8) Node {
        var node: Node = .{ .across = across, .n = @intCast(children.len) };
        for (children, 0..) |c, i| {
            node.children[i] = c;
            node.shares[i] = 1 / @as(f32, @floatFromInt(children.len));
        }
        return node;
    }

    /// Puts the splitter after child `index` at `at` (a fraction of the
    /// split), keeping both neighbours at least `lo`.
    fn moveSplitter(node: *Node, index: usize, at: f32, lo: f32) void {
        var start: f32 = 0;
        for (node.shares[0..index]) |s| start += s;
        const pair = node.shares[index] + node.shares[index + 1];
        const m = @min(lo, pair / 2);
        const before = std.math.clamp(at - start, m, pair - m);
        node.shares[index] = before;
        node.shares[index + 1] = pair - before;
    }
};

/// Child `index` of split node `node`.
pub const Slot = struct { node: u8, index: u8 };

/// The line after child `slot.index` of a split: at `at` across (down) the
/// editor area, running from `from` to `to` the other way (fractions).
pub const Splitter = struct { slot: Slot, across: bool, at: f32, from: f32, to: f32 };

/// The split tree. Node 0 is the root and nodes come after their parent;
/// groups are numbered in order (left to right, top to bottom), like vim's
/// windows.
pub const Layout = struct {
    /// Room for a full tree plus the nodes an edit leaves behind.
    const cap = 3 * max_groups;

    nodes: [cap]Node = @splat(.{}),
    len: u8 = 0,

    pub const single: Layout = blk: {
        var l: Layout = .{};
        _ = l.add(.leaf(0));
        break :blk l;
    };

    pub fn count(l: *const Layout) u8 {
        var n: u8 = 0;
        for (l.nodes[0..l.len]) |node| n += @intFromBool(node.group != null);
        return n;
    }

    fn add(l: *Layout, node: Node) u8 {
        l.nodes[l.len] = node;
        l.len += 1;
        return l.len - 1;
    }

    fn leafOf(l: *const Layout, g: u8) ?u8 {
        for (l.nodes[0..l.len], 0..) |node, i| if (node.group == g) return @intCast(i);
        return null;
    }

    /// Where node `i` sits in its split; null for the root.
    fn parentOf(l: *const Layout, i: u8) ?Slot {
        for (l.nodes[0..l.len], 0..) |node, p| if (node.group == null) {
            for (node.children[0..node.n], 0..) |c, k| if (c == i) return .{ .node = @intCast(p), .index = @intCast(k) };
        };
        return null;
    }

    /// Node `i`, past splits with a single child.
    fn resolve(l: *const Layout, i: u8) u8 {
        var j = i;
        while (l.nodes[j].group == null and l.nodes[j].n == 1) j = l.nodes[j].children[0];
        return j;
    }

    /// The deepest split side by side (`across`) or stacked above group `g`,
    /// and the child holding `g`.
    fn lineOf(l: *const Layout, g: u8, across: bool) ?Slot {
        var i = l.leafOf(g) orelse return null;
        while (l.parentOf(i)) |s| {
            if (l.nodes[s.node].across == across) return s;
            i = s.node;
        }
        return null;
    }

    /// Each node's area as fractions of the editor area: x0, y0, x1, y1.
    pub fn bounds(l: *const Layout) [cap][4]f32 {
        var out: [cap][4]f32 = undefined;
        out[0] = .{ 0, 0, 1, 1 };
        for (l.nodes[0..l.len], 0..) |node, i| if (node.group == null) {
            const b = out[i];
            const a: usize = if (node.across) 0 else 1;
            var at = b[a];
            for (node.children[0..node.n], node.shares[0..node.n], 0..) |c, s, k| {
                out[c] = b;
                out[c][a] = at;
                // The last child ends exactly at the edge.
                at = if (k + 1 == node.n) b[a + 2] else at + (b[a + 2] - b[a]) * s;
                out[c][a + 2] = at;
            }
        };
        return out;
    }

    /// Each group's cell as fractions of the editor area: x0, y0, x1, y1.
    pub fn cells(l: *const Layout) [max_groups][4]f32 {
        const b = l.bounds();
        var out: [max_groups][4]f32 = @splat(.{ 0, 0, 0, 0 });
        for (l.nodes[0..l.len], 0..) |node, i| if (node.group) |g| {
            out[g] = b[i];
        };
        return out;
    }

    /// Split `i`'s start and size across (down) as fractions of the editor area.
    fn extent(l: *const Layout, i: u8) [2]f32 {
        const b = l.bounds()[i];
        const a: usize = if (l.nodes[i].across) 0 else 1;
        return .{ b[a], @max(b[a + 2] - b[a], 1e-6) };
    }

    /// The lines between the children of each split.
    pub fn splitters(l: *const Layout, buf: *[max_groups]Splitter) []Splitter {
        const b = l.bounds();
        var n: usize = 0;
        for (l.nodes[0..l.len], 0..) |node, i| if (node.group == null) {
            const a: usize = if (node.across) 0 else 1;
            for (node.children[1..node.n], 0..) |c, k| {
                const r = b[c];
                buf[n] = .{ .slot = .{ .node = @intCast(i), .index = @intCast(k) }, .across = node.across, .at = r[a], .from = r[1 - a], .to = r[3 - a] };
                n += 1;
            }
        };
        return buf[0..n];
    }

    /// Drags the splitter after `s` to `at`, a fraction of the editor's width
    /// (height).
    pub fn dragSplitter(l: *Layout, s: Slot, at: f32) void {
        if (s.node >= l.len or l.nodes[s.node].group != null or s.index + 1 >= l.nodes[s.node].n) return;
        const e = l.extent(s.node);
        l.nodes[s.node].moveSplitter(s.index, (at - e[0]) / e[1], min_share / e[1]);
    }

    /// vim's ctrl-w =: each split shares out its area by how many groups line
    /// up that way in each child, so the groups come out the same size.
    pub fn equalize(l: *Layout) void {
        for (l.nodes[0..l.len]) |*node| if (node.group == null) {
            var total: f32 = 0;
            for (node.children[0..node.n], node.shares[0..node.n]) |c, *s| {
                s.* = l.weight(c, node.across);
                total += s.*;
            }
            for (node.shares[0..node.n]) |*s| s.* /= total;
        };
    }

    /// How many groups line up side by side (`across`) or stacked in node `i`.
    fn weight(l: *const Layout, i: u8, across: bool) f32 {
        const node = l.nodes[i];
        if (node.group != null) return 1;
        var w: f32 = 0;
        for (node.children[0..node.n]) |c| {
            const cw = l.weight(c, across);
            w = if (node.across == across) w + cw else @max(w, cw);
        }
        return w;
    }

    /// The same splits the same way, whatever their sizes.
    fn sameShape(a: *const Layout, b: *const Layout) bool {
        if (a.len != b.len) return false;
        for (a.nodes[0..a.len], b.nodes[0..b.len]) |x, y| {
            if ((x.group == null) != (y.group == null) or x.n != y.n) return false;
            if (x.group == null and (x.across != y.across or !std.mem.eql(u8, x.children[0..x.n], y.children[0..y.n]))) return false;
        }
        return true;
    }

    /// Takes group `g`'s leaf out of its split, giving its share to the child
    /// before it (after it, for the first). Returns the group there nearest
    /// `g`, which takes in its views.
    fn remove(l: *Layout, g: u8) ?u8 {
        const s = l.parentOf(l.leafOf(g) orelse return null) orelse return null;
        const node = &l.nodes[s.node];
        const i = s.index;
        const to = if (i > 0) i - 1 else 1;
        node.shares[to] += node.shares[i];
        var heir = node.children[to];
        while (l.nodes[heir].group == null) {
            const sub = l.nodes[heir];
            heir = sub.children[if (i > 0) sub.n - 1 else 0];
        }
        std.mem.copyForwards(u8, node.children[i .. node.n - 1], node.children[i + 1 .. node.n]);
        std.mem.copyForwards(f32, node.shares[i .. node.n - 1], node.shares[i + 1 .. node.n]);
        node.n -= 1;
        return l.nodes[heir].group;
    }
};

/// Tidies the tree after an edit, as vim does: a split left with one child
/// gives way to it, a split inside one the same way joins it, and the leaves
/// are numbered in order. `map[old]` is where each group (and `fresh`) went.
fn normalize(l: *const Layout) struct { layout: Layout, map: [max_groups + 1]u8 } {
    var b: Builder = .{ .src = l };
    _ = b.copy(l.resolve(0));
    return .{ .layout = b.dst, .map = b.map };
}

const Builder = struct {
    src: *const Layout,
    dst: Layout = .{},
    map: [max_groups + 1]u8 = @splat(0),
    leaves: u8 = 0,

    fn copy(b: *Builder, i: u8) u8 {
        const node = b.src.nodes[i];
        if (node.group) |g| {
            b.map[g] = b.leaves;
            b.leaves += 1;
            return b.dst.add(.leaf(b.leaves - 1));
        }
        const at = b.dst.add(.{ .across = node.across });
        for (node.children[0..node.n], node.shares[0..node.n]) |c, s| b.addChild(at, c, s);
        return at;
    }

    fn addChild(b: *Builder, to: u8, child: u8, share: f32) void {
        const c = b.src.resolve(child);
        const node = b.src.nodes[c];
        if (node.group == null and node.across == b.dst.nodes[to].across) {
            for (node.children[0..node.n], node.shares[0..node.n]) |gc, s| b.addChild(to, gc, share * s);
            return;
        }
        const new = b.copy(c);
        const p = &b.dst.nodes[to];
        p.children[p.n] = new;
        p.shares[p.n] = share;
        p.n += 1;
    }
};

/// Sublime's View > Layout presets as split trees.
pub fn preset(kind: GroupLayout) Layout {
    var l: Layout = .{};
    switch (kind) {
        .single => return .single,
        .grid_4 => {
            _ = l.add(.split(false, &.{ 1, 4 }));
            _ = l.add(.split(true, &.{ 2, 3 }));
            _ = l.add(.leaf(0));
            _ = l.add(.leaf(1));
            _ = l.add(.split(true, &.{ 5, 6 }));
            _ = l.add(.leaf(2));
            _ = l.add(.leaf(3));
        },
        else => {
            const n = kind.count();
            const children = [_]u8{ 1, 2, 3, 4 };
            _ = l.add(.split(kind.cols() > 1, children[0..n]));
            for (0..n) |g| _ = l.add(.leaf(@intCast(g)));
        },
    }
    return l;
}

/// A layout change that keeps the views: old group `g`'s views go to group
/// `map[g]` (several groups may merge into one).
pub const Plan = struct {
    layout: Layout,
    map: [max_groups]u8 = identity,
    /// The group focused afterwards.
    focus: u8,
    /// Splits: the new, still empty group.
    new_group: ?u8 = null,
};

/// A plan for edited tree `t`, focusing the group it adds.
fn planAdding(t: *const Layout) Plan {
    const r = normalize(t);
    return .{ .layout = r.layout, .map = r.map[0..max_groups].*, .focus = r.map[fresh], .new_group = r.map[fresh] };
}

/// vim's `:split` (`across`: `:vsplit`) of group `g`: a new group in half of
/// its cell, above / left of it (vim's default) unless `after` ('splitbelow',
/// 'splitright'). Null when there are `max_groups` already.
pub fn planSplit(l: Layout, g: u8, across: bool, after: bool) ?Plan {
    if (l.count() >= max_groups) return null;
    var t = l;
    const at = t.leafOf(g) orelse return null;
    const old = t.add(.leaf(g));
    const new = t.add(.leaf(fresh));
    const pair: [2]u8 = if (after) .{ old, new } else .{ new, old };
    t.nodes[at] = .split(across, &pair);
    return planAdding(&t);
}

/// Sublime's ctrl+k ctrl+up: a new group at the end of the outermost split
/// (a second column beside a single group). Null when there is no room.
pub fn planAppend(l: Layout) ?Plan {
    if (l.count() >= max_groups) return null;
    var t = l;
    const body = t.add(t.nodes[0]);
    const new = t.add(.leaf(fresh));
    t.nodes[0] = .split(l.nodes[0].group != null or l.nodes[0].across, &.{ body, new });
    return planAdding(&t);
}

/// Closing group `g`: its space and views go to the group before it in its
/// split (the one after it, for the first).
pub fn planClose(l: Layout, g: u8) ?Plan {
    if (l.count() < 2) return null;
    var t = l;
    const heir = t.remove(g) orelse return null;
    const r = normalize(&t);
    var p: Plan = .{ .layout = r.layout, .map = r.map[0..max_groups].*, .focus = r.map[heir] };
    p.map[g] = r.map[heir];
    return p;
}

/// vim's ctrl-w H/J/K/L: group `g` takes the full height (width) at the far
/// left / bottom / top / right; the others keep their arrangement. When only
/// the order changes, so do the sizes stay.
pub fn planEdge(l: Layout, g: u8, dir: Dir) Plan {
    if (l.count() < 2) return .{ .layout = l, .focus = g };
    const across = dir == .left or dir == .right;
    const first = dir == .left or dir == .up;
    var t = l;
    const at = t.leafOf(g).?;
    _ = t.remove(g);
    const root = t.resolve(0);
    const body = if (root == 0) t.add(t.nodes[0]) else root;
    const pair: [2]u8 = if (first) .{ at, body } else .{ body, at };
    t.nodes[0] = .split(across, &pair);
    const r = normalize(&t);
    var p: Plan = .{ .layout = r.layout, .map = r.map[0..max_groups].*, .focus = r.map[g] };
    if (p.layout.sameShape(&l)) p.layout = l;
    return p;
}

/// Exchanges the views of groups `a` and `b`; the focus stays at `a` (vim's ctrl-w x).
pub fn planSwap(l: Layout, a: u8, b: u8) Plan {
    var p: Plan = .{ .layout = l, .focus = a };
    p.map[a] = b;
    p.map[b] = a;
    return p;
}

/// vim's ctrl-w r / R: rotates the views of `g`'s row (column) one step
/// `downwards` (rightwards) or back; the focus moves with `g`'s view. Null
/// when a window there is split (vim's E443).
pub fn planRotate(l: Layout, g: u8, downwards: bool) ?Plan {
    var p: Plan = .{ .layout = l, .focus = g };
    const s = l.parentOf(l.leafOf(g) orelse return p) orelse return p;
    const node = l.nodes[s.node];
    var line: [max_groups]u8 = undefined;
    for (node.children[0..node.n], 0..) |c, i| line[i] = l.nodes[c].group orelse return null;
    const n = node.n;
    for (line[0..n], 0..) |grp, i| p.map[grp] = line[if (downwards) (i + 1) % n else (i + n - 1) % n];
    p.focus = p.map[g];
    return p;
}

/// The group next to `from` in direction `dir`: the nearest one overlapping
/// it across that direction, preferring the one level with its middle.
pub fn neighbour(l: *const Layout, from: u8, dir: Dir) ?u8 {
    const eps = 1e-4;
    const cells = l.cells();
    const a = cells[from];
    const horizontal = dir == .left or dir == .right;
    const mid = if (horizontal) (a[1] + a[3]) / 2 else (a[0] + a[2]) / 2;
    var best: ?u8 = null;
    var best_gap = std.math.inf(f32);
    var best_level = false;
    for (cells[0..l.count()], 0..) |b, i| {
        if (i == from) continue;
        const gap = switch (dir) {
            .left => a[0] - b[2],
            .right => b[0] - a[2],
            .up => a[1] - b[3],
            .down => b[1] - a[3],
        };
        if (gap < -eps) continue;
        const lo = if (horizontal) @max(a[1], b[1]) else @max(a[0], b[0]);
        const hi = if (horizontal) @min(a[3], b[3]) else @min(a[2], b[2]);
        if (hi - lo <= eps) continue;
        const level = if (horizontal) b[1] <= mid and mid <= b[3] else b[0] <= mid and mid <= b[2];
        if (gap < best_gap - eps or (@abs(gap - best_gap) <= eps and level and !best_level)) {
            best = @intCast(i);
            best_gap = gap;
            best_level = level;
        }
    }
    return best;
}

/// Sizes child `pos` of a split to `share` of it (null: as large as
/// possible); the others split the rest in proportion to their sizes, each
/// keeping at least `lo`.
pub fn sizeLine(shares: []f32, pos: usize, share: ?f32, lo: f32) void {
    if (shares.len < 2) return;
    const nf: f32 = @floatFromInt(shares.len);
    const m = @min(lo, 1 / nf);
    const want = std.math.clamp(share orelse 1, m, 1 - (nf - 1) * m);
    const rest = 1 - want;
    var others: f32 = 0;
    for (shares, 0..) |s, i| if (i != pos) {
        others += s;
    };
    var proportional = others > 0;
    for (shares, 0..) |s, i| if (i != pos and others > 0 and rest * s / others < m - 1e-6) {
        proportional = false;
    };
    for (shares, 0..) |*s, i| s.* = if (i == pos) want else if (proportional) rest * s.* / others else rest / (nf - 1);
}

// ---- Applying plans ---------------------------------------------------------

/// True if no other view shows `ed`'s buffer.
pub fn soleView(ws: *Workspace, ed: *Editor) bool {
    for (ws.tabs.items) |t| if (t.editor != ed and t.editor.buffer == ed.buffer) return false;
    return true;
}

fn mruRank(ws: *Workspace, ed: *Editor) usize {
    for (ws.mru.items, 0..) |e, i| if (e == ed) return i;
    return std.math.maxInt(usize);
}

const ApplyOptions = struct {
    /// Its view stays active where groups merge.
    keep: ?u8 = null,
    /// The group being closed: its view loses where groups merge.
    closing: ?u8 = null,
    /// Make the groups equal (vim's 'equalalways' after splits and closes).
    equalize: bool = true,
};

/// Whether old group `a`'s view should stay active over `b`'s when they merge.
fn beats(ws: *Workspace, a: u8, b: u8, o: ApplyOptions) bool {
    const va = ws.group_active[a];
    const vb = ws.group_active[b];
    if ((va == null) != (vb == null)) return va != null;
    if (o.keep) |k| if (a == k or b == k) return a == k;
    if (o.closing) |c| if (a == c or b == c) return b == c;
    if (va == null) return false;
    return mruRank(ws, va.?) < mruRank(ws, vb.?);
}

/// Carries out `plan`. Where groups merge, one view stays active (the `keep`
/// group's, else the most recently used one, the `closing` group's last),
/// and views of a buffer the merged group already shows are closed: the
/// buffer and its changes live on in the other view.
fn apply(ws: *Workspace, plan: Plan, o: ApplyOptions) void {
    const old_count = ws.groupCount();
    var rank: [max_groups]u8 = @splat(0);
    for (0..old_count) |a| for (0..old_count) |b| {
        if (a != b and plan.map[a] == plan.map[b] and beats(ws, @intCast(b), @intCast(a), o)) rank[a] += 1;
    };
    var winner: [max_groups]?u8 = @splat(null);
    for (0..old_count) |g| {
        const n = plan.map[g];
        if (winner[n] == null or rank[g] < rank[winner[n].?]) winner[n] = @intCast(g);
    }
    var active: [max_groups]?*Editor = @splat(null);
    for (winner, 0..) |w, n| if (w) |g| {
        active[n] = ws.group_active[g];
    };

    var i = ws.tabs.items.len;
    while (i > 0) {
        i -= 1;
        const t = ws.tabs.items[i];
        if (t.group >= old_count or std.mem.indexOfScalar(?*Editor, &active, t.editor) != null) continue;
        const duplicate = for (ws.tabs.items) |other| {
            if (other.group != t.group and other.group < old_count and plan.map[other.group] == plan.map[t.group] and
                other.editor.buffer == t.editor.buffer and rank[other.group] < rank[t.group]) break true;
        } else false;
        if (duplicate) ws.closeTabNow(i);
    }

    for (ws.tabs.items) |*t| t.group = if (t.group < old_count) plan.map[t.group] else 0;
    ws.group_layout = plan.layout;
    ws.zoomed_group = null;
    ws.group_active = active;
    if (ws.prev_focused_group) |p| {
        ws.prev_focused_group = if (p < old_count and plan.map[p] != plan.focus) plan.map[p] else null;
    }
    ws.focused_group = plan.focus;
    if (o.equalize) equalize(ws);
    if (ws.activeIndex()) |idx| ws.activateTab(idx) else ws.updateTitle();
    ws.dirty = true;
}

pub const NewView = union(enum) {
    /// A second view of the current one (`:split`).
    clone,
    /// An empty buffer (`:new`).
    empty,
    /// A file (`:split path`).
    file: []const u8,
};

/// Splits the focused group like vim's `:split` (`across`: `:vsplit`); the
/// new group gets the focus and shows `view`.
pub fn split(ws: *Workspace, across: bool, after: bool, view: NewView) !void {
    const g = ws.focused_group;
    const plan = planSplit(ws.group_layout, g, across, after) orelse return error.NoRoom;
    const src = ws.activeEditor();
    apply(ws, plan, .{});
    switch (view) {
        .clone => _ = if (src) |s| ws.cloneViewOf(s) else try ws.newFile(),
        .empty => _ = try ws.newFile(),
        .file => |path| _ = openFileHere(ws, path) catch |err| {
            _ = closeWindow(ws, ws.focused_group, .close);
            return err;
        },
    }
    ws.prev_focused_group = plan.map[g];
}

/// Sublime's ctrl+k ctrl+up: a new, focused (still empty) group; see
/// `planAppend`. False when there is no room.
pub fn addPane(ws: *Workspace) bool {
    apply(ws, planAppend(ws.group_layout) orelse return false, .{});
    return true;
}

pub const Close = enum {
    /// ctrl-w c, :close, :q in a split: the view closes too, unless it holds
    /// the only copy of unsaved changes (then it stays as a tab, like a
    /// hidden buffer).
    close,
    /// :hide: every view stays, as a tab of the neighbouring group.
    hide,
    /// ctrl-w T: like hide, and the view stays the active one.
    to_tab,
};

/// Closes group `g` (a vim window) by merging it into its neighbour, where
/// its other views become tabs. False if it is the only group.
pub fn closeWindow(ws: *Workspace, g: u8, how: Close) bool {
    if (ws.groupCount() < 2 or g >= ws.groupCount()) return false;
    const view = ws.group_active[g];
    if (view) |v| if (how == .close and (!v.buffer.isDirty() or !soleView(ws, v))) {
        if (ws.tabIndexOf(v)) |i| ws.closeTabNow(i);
    };
    var plan = planClose(ws.group_layout, g).?;
    // Closing another window leaves the focus where it was.
    if (ws.focused_group != g) plan.focus = plan.map[ws.focused_group];
    apply(ws, plan, .{ .closing = g });
    if (how == .to_tab) if (view) |v| if (ws.tabIndexOf(v)) |i| ws.activateTab(i);
    return true;
}

/// vim's :only: the single layout; the other groups' views stay as tabs.
pub fn only(ws: *Workspace) void {
    if (ws.groupCount() < 2) return;
    apply(ws, .{ .layout = .single, .map = @splat(0), .focus = 0 }, .{ .keep = ws.focused_group });
}

/// vim's ctrl-w x: exchanges the focused group's views with the next group's
/// (the previous one's for the last), or group `number`'s (1-based).
pub fn exchange(ws: *Workspace, number: usize) bool {
    const n = ws.groupCount();
    const g = ws.focused_group;
    if (n < 2 or number > n) return false;
    const other: u8 = if (number > 0) @intCast(number - 1) else if (g + 1 < n) g + 1 else g - 1;
    if (other == g) return false;
    apply(ws, planSwap(ws.group_layout, g, other), .{ .equalize = false });
    return true;
}

/// vim's ctrl-w r / R. False when a window in the row (column) is split.
pub fn rotate(ws: *Workspace, downwards: bool, times: usize) bool {
    var i: usize = 0;
    while (i < times) : (i += 1) {
        apply(ws, planRotate(ws.group_layout, ws.focused_group, downwards) orelse return false, .{ .equalize = false });
    }
    return true;
}

/// vim's ctrl-w H/J/K/L.
pub fn moveToEdge(ws: *Workspace, dir: Dir) void {
    const plan = planEdge(ws.group_layout, ws.focused_group, dir);
    apply(ws, plan, .{ .equalize = !plan.layout.sameShape(&ws.group_layout) });
}

/// Focuses the group `count` steps in direction `dir`; `skip_empty` passes
/// over groups without a view (vim keys can't act in an empty one).
pub fn focusDirection(ws: *Workspace, dir: Dir, count: usize, skip_empty: bool) bool {
    var g = ws.focused_group;
    var step: usize = 0;
    walk: while (step < @max(count, 1)) : (step += 1) {
        var next = neighbour(&ws.group_layout, g, dir) orelse break;
        while (skip_empty and ws.group_active[next] == null) {
            next = neighbour(&ws.group_layout, next, dir) orelse break :walk;
        }
        g = next;
    }
    if (g == ws.focused_group) return false;
    ws.focusGroup(g);
    return true;
}

/// The next (previous) group in Sublime's order, wrapping around.
pub fn focusNext(ws: *Workspace, forward: bool, skip_empty: bool) void {
    const n = ws.groupCount();
    var g = ws.focused_group;
    for (0..n) |_| {
        g = if (forward) (g + 1) % n else (g + n - 1) % n;
        if (!skip_empty or ws.group_active[g] != null) break;
    }
    ws.focusGroup(g);
}

pub fn equalize(ws: *Workspace) void {
    ws.group_layout.equalize();
    ws.dirty = true;
}

/// Grows group `g` by `px` (shrinks it if negative) across (`across`) or
/// down, by moving the splitter on its far side (the near one for the last
/// group) in the deepest split that way. False if there is none.
pub fn resizeBy(ws: *Workspace, g: u8, across: bool, px: f32) bool {
    const l = &ws.group_layout;
    const area = if (across) ws.editor_rect.w else ws.editor_rect.h;
    const s = l.lineOf(g, across) orelse return false;
    if (area <= 0) return false;
    const size = l.extent(s.node)[1];
    const d = px / (area * size);
    const node = &l.nodes[s.node];
    var end: f32 = 0;
    for (node.shares[0 .. s.index + 1]) |x| end += x;
    if (s.index + 1 < node.n) {
        node.moveSplitter(s.index, end + d, min_share / size);
    } else node.moveSplitter(s.index - 1, end - node.shares[s.index] - d, min_share / size);
    ws.dirty = true;
    return true;
}

/// Sizes group `g` to `px` across (`across`) or down in the deepest split
/// that way; null: as large as the others allow, in every split that way.
pub fn setSize(ws: *Workspace, g: u8, across: bool, px: ?f32) bool {
    const l = &ws.group_layout;
    const area = if (across) ws.editor_rect.w else ws.editor_rect.h;
    if (area <= 0) return false;
    var i = l.leafOf(g) orelse return false;
    var sized = false;
    while (l.parentOf(i)) |s| {
        i = s.node;
        if (l.nodes[s.node].across != across or (sized and px != null)) continue;
        const size = l.extent(s.node)[1];
        const node = &l.nodes[s.node];
        sizeLine(node.shares[0..node.n], s.index, if (px) |p| p / (area * size) else null, min_share / size);
        sized = true;
    }
    if (sized) ws.dirty = true;
    return sized;
}

/// vim's `:e` / `:split` with a file: shows `path` in the focused group: its
/// tab there, else a new view of the buffer when another group has it open,
/// else the file is opened.
pub fn openFileHere(ws: *Workspace, path_in: []const u8) !*Editor {
    const path = try ws.resolvePath(path_in);
    defer ws.gpa.free(path);
    var other: ?*Editor = null;
    for (ws.tabs.items, 0..) |t, i| {
        const p = t.editor.buffer.path orelse continue;
        if (!std.mem.eql(u8, p, path)) continue;
        if (t.group == ws.focused_group) {
            ws.tabs.items[i].transient = false;
            ws.activateTab(i);
            return t.editor;
        }
        other = t.editor;
    }
    if (other) |src| return ws.cloneViewOf(src) orelse error.OutOfMemory;
    return ws.openFile(path, false);
}

// ---- Tests -------------------------------------------------------------------

const testing = std.testing;

fn expectMap(p: Plan, expected: []const u8) !void {
    try testing.expectEqualSlices(u8, expected, p.map[0..expected.len]);
}

fn expectCell(l: Layout, g: u8, expected: [4]f32) !void {
    const c = l.cells()[g];
    for (expected, c) |e, a| try testing.expectApproxEqAbs(e, a, 1e-5);
}

test "presets as split trees" {
    const grid = preset(.grid_4);
    try testing.expectEqual(@as(u8, 4), grid.count());
    try expectCell(grid, 1, .{ 0.5, 0, 1, 0.5 });
    try expectCell(grid, 2, .{ 0, 0.5, 0.5, 1 });
    try expectCell(preset(.columns_3), 1, .{ 1.0 / 3.0, 0, 2.0 / 3.0, 1 });
    try expectCell(preset(.rows_2), 1, .{ 0, 0.5, 1, 1 });
    try testing.expectEqual(@as(u8, 1), preset(.single).count());
}

test "split: only the split group's cell divides" {
    // :vsplit of a single view: the new view goes left and is focused.
    var p = planSplit(.single, 0, true, false).?;
    try testing.expectEqual(@as(u8, 2), p.layout.count());
    try testing.expectEqual(@as(?u8, 0), p.new_group);
    try expectMap(p, &.{1});
    try expectCell(p.layout, 0, .{ 0, 0, 0.5, 1 });
    // 'splitright': to the right.
    p = planSplit(.single, 0, true, true).?;
    try testing.expectEqual(@as(?u8, 1), p.new_group);
    try expectMap(p, &.{0});
    p = planSplit(.single, 0, false, false).?;
    try expectCell(p.layout, 1, .{ 0, 0.5, 1, 1 });
    // A split the way of its line joins the line, in half of its cell.
    p = planSplit(preset(.columns_2), 1, true, false).?;
    try testing.expect(p.layout.sameShape(&preset(.columns_3)));
    try testing.expectEqual(@as(?u8, 1), p.new_group);
    try expectMap(p, &.{ 0, 2 });
    try expectCell(p.layout, 1, .{ 0.5, 0, 0.75, 1 });
    p = planSplit(preset(.rows_2), 1, false, true).?;
    try testing.expect(p.layout.sameShape(&preset(.rows_3)));
    try testing.expectEqual(@as(?u8, 2), p.new_group);
    try expectMap(p, &.{ 0, 1 });

    // :vsplit in the top of two rows: the top row splits, the bottom one
    // keeps the full width.
    p = planSplit(preset(.rows_2), 0, true, false).?;
    try testing.expectEqual(@as(u8, 3), p.layout.count());
    try testing.expectEqual(@as(?u8, 0), p.new_group);
    try expectMap(p, &.{ 1, 2 });
    try expectCell(p.layout, 0, .{ 0, 0, 0.5, 0.5 });
    try expectCell(p.layout, 1, .{ 0.5, 0, 1, 0.5 });
    try expectCell(p.layout, 2, .{ 0, 0.5, 1, 1 });
    // :split in the right of two columns, 'splitbelow'.
    p = planSplit(preset(.columns_2), 1, false, true).?;
    try testing.expectEqual(@as(?u8, 2), p.new_group);
    try expectMap(p, &.{ 0, 1 });
    try expectCell(p.layout, 0, .{ 0, 0, 0.5, 1 });
    try expectCell(p.layout, 2, .{ 0.5, 0.5, 1, 1 });

    try testing.expect(planSplit(preset(.columns_4), 2, true, false) == null);
    try testing.expect(planSplit(preset(.grid_4), 3, true, false) == null);
}

test "append: a new group at the end of the outermost split" {
    var p = planAppend(.single).?;
    try testing.expect(p.layout.sameShape(&preset(.columns_2)));
    try testing.expectEqual(@as(?u8, 1), p.new_group);
    p = planAppend(preset(.rows_2)).?;
    try testing.expect(p.layout.sameShape(&preset(.rows_3)));
    p = planAppend(planSplit(preset(.rows_2), 0, true, true).?.layout).?;
    try testing.expectEqual(@as(?u8, 3), p.new_group);
    p.layout.equalize();
    try expectCell(p.layout, 3, .{ 0, 2.0 / 3.0, 1, 1 });
    try testing.expect(planAppend(preset(.grid_4)) == null);
}

test "close: the space and views go to the group before" {
    try testing.expect(planClose(.single, 0) == null);
    var p = planClose(preset(.columns_3), 1).?;
    try testing.expect(p.layout.sameShape(&preset(.columns_2)));
    try expectMap(p, &.{ 0, 0, 1 });
    try testing.expectEqual(@as(u8, 0), p.focus);
    try expectCell(p.layout, 0, .{ 0, 0, 2.0 / 3.0, 1 });
    p = planClose(preset(.columns_2), 0).?;
    try testing.expectEqual(@as(u8, 1), p.layout.count());
    try expectMap(p, &.{ 0, 0 });
    p = planClose(preset(.rows_3), 0).?;
    try testing.expect(p.layout.sameShape(&preset(.rows_2)));
    try expectMap(p, &.{ 0, 0, 1 });
    // The grid's bottom right: the bottom left takes the bottom row.
    p = planClose(preset(.grid_4), 3).?;
    try expectMap(p, &.{ 0, 1, 2, 2 });
    try testing.expectEqual(@as(u8, 2), p.focus);
    try expectCell(p.layout, 2, .{ 0, 0.5, 1, 1 });

    // Two rows, the top one split: closing the bottom row gives the top
    // pair the full height, the views going to the nearer one.
    const top_split = planSplit(preset(.rows_2), 0, true, true).?.layout;
    p = planClose(top_split, 2).?;
    try testing.expect(p.layout.sameShape(&preset(.columns_2)));
    try expectMap(p, &.{ 0, 1, 1 });
    p = planClose(top_split, 0).?;
    try testing.expect(p.layout.sameShape(&preset(.rows_2)));
    try expectMap(p, &.{ 0, 0, 1 });
}

test "move to an edge, exchange and rotate" {
    // Reordering a line keeps the sizes.
    var p = planEdge(preset(.columns_3), 2, .left);
    try testing.expect(p.layout.sameShape(&preset(.columns_3)));
    try expectMap(p, &.{ 1, 2, 0 });
    try testing.expectEqual(@as(u8, 0), p.focus);
    // ctrl-w H in two rows turns them into columns, the view on the left.
    p = planEdge(preset(.rows_2), 1, .left);
    try testing.expect(p.layout.sameShape(&preset(.columns_2)));
    try expectMap(p, &.{ 1, 0 });
    // ctrl-w J in three columns: the other two stay side by side above.
    p = planEdge(preset(.columns_3), 0, .down);
    try expectMap(p, &.{ 2, 0, 1 });
    p.layout.equalize();
    try expectCell(p.layout, 0, .{ 0, 0, 0.5, 0.5 });
    try expectCell(p.layout, 2, .{ 0, 0.5, 1, 1 });
    p = planEdge(preset(.columns_4), 0, .down);
    try expectMap(p, &.{ 3, 0, 1, 2 });
    try expectCell(p.layout, 3, .{ 0, 0.5, 1, 1 });
    // The grid's bottom right to the far left, at full height.
    p = planEdge(preset(.grid_4), 3, .left);
    try expectMap(p, &.{ 1, 2, 3, 0 });
    try testing.expectEqual(@as(u8, 0), p.focus);
    try expectCell(p.layout, 0, .{ 0, 0, 0.5, 1 });

    p = planSwap(preset(.columns_2), 0, 1);
    try expectMap(p, &.{ 1, 0 });
    try testing.expectEqual(@as(u8, 0), p.focus);

    p = planRotate(preset(.columns_3), 0, true).?;
    try expectMap(p, &.{ 1, 2, 0 });
    try testing.expectEqual(@as(u8, 1), p.focus);
    p = planRotate(preset(.rows_3), 0, false).?;
    try expectMap(p, &.{ 2, 0, 1 });
    p = planRotate(preset(.grid_4), 2, true).?;
    try expectMap(p, &.{ 0, 1, 3, 2 });
    // The bottom row's neighbour above is split: no rotating.
    try testing.expect(planRotate(planSplit(preset(.rows_2), 0, true, true).?.layout, 2, true) == null);
}

test "neighbours by geometry" {
    const grid = preset(.grid_4);
    try testing.expectEqual(@as(?u8, 1), neighbour(&grid, 0, .right));
    try testing.expectEqual(@as(?u8, 2), neighbour(&grid, 0, .down));
    try testing.expectEqual(@as(?u8, 2), neighbour(&grid, 3, .left));
    try testing.expectEqual(@as(?u8, 1), neighbour(&grid, 3, .up));
    try testing.expectEqual(@as(?u8, null), neighbour(&grid, 0, .left));
    const cols = preset(.columns_3);
    try testing.expectEqual(@as(?u8, 0), neighbour(&cols, 1, .left));
    try testing.expectEqual(@as(?u8, 2), neighbour(&cols, 1, .right));
    try testing.expectEqual(@as(?u8, null), neighbour(&cols, 1, .down));
    const rows = preset(.rows_2);
    try testing.expectEqual(@as(?u8, 1), neighbour(&rows, 0, .down));
    // Both halves of a split top row look down to the full-width bottom.
    const top_split = planSplit(rows, 0, true, true).?.layout;
    try testing.expectEqual(@as(?u8, 2), neighbour(&top_split, 1, .down));
    try testing.expectEqual(@as(?u8, 2), neighbour(&top_split, 0, .down));
    try testing.expectEqual(@as(?u8, 0), neighbour(&top_split, 2, .up));
}

test "splitters and sizes" {
    // Two rows, the top one split: one line across, one down the top row only.
    var l = planSplit(preset(.rows_2), 0, true, true).?.layout;
    var buf: [max_groups]Splitter = undefined;
    const lines = l.splitters(&buf);
    try testing.expectEqual(@as(usize, 2), lines.len);
    try testing.expect(!lines[0].across);
    try testing.expectApproxEqAbs(@as(f32, 0.5), lines[0].at, 1e-5);
    try testing.expect(lines[1].across);
    try testing.expectApproxEqAbs(@as(f32, 0.5), lines[1].to, 1e-5);
    // Dragging it moves only the top row's cells.
    l.dragSplitter(lines[1].slot, 0.3);
    try expectCell(l, 0, .{ 0, 0, 0.3, 0.5 });
    try expectCell(l, 2, .{ 0, 0.5, 1, 1 });
    // Stopping 10% of the editor area short of the edge.
    l.dragSplitter(lines[1].slot, 0.01);
    try expectCell(l, 0, .{ 0, 0, 0.1, 0.5 });

    // ctrl-w = sizes by how many groups line up each way: the column of two
    // rows below takes two thirds of the height.
    l = planSplit(planSplit(preset(.rows_2), 1, true, true).?.layout, 2, false, true).?.layout;
    l.equalize();
    try expectCell(l, 0, .{ 0, 0, 1, 1.0 / 3.0 });
    try expectCell(l, 1, .{ 0, 1.0 / 3.0, 0.5, 1 });
    try expectCell(l, 3, .{ 0.5, 2.0 / 3.0, 1, 1 });

    var shares = [_]f32{ 0.5, 0.5 };
    sizeLine(&shares, 0, null, 0.1);
    try testing.expectApproxEqAbs(@as(f32, 0.9), shares[0], 1e-5);
    // The middle of three takes half; the others split the rest evenly.
    var thirds = [_]f32{ 1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0 };
    sizeLine(&thirds, 1, 0.5, 0.1);
    try testing.expectApproxEqAbs(@as(f32, 0.25), thirds[0], 1e-5);
    try testing.expectApproxEqAbs(@as(f32, 0.5), thirds[1], 1e-5);
}
