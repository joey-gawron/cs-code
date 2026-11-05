//! A persistent, reference-counted B+ tree whose nodes cache a monoidal
//! summary of their contents — a port of Zed's `sum_tree` crate.
//!
//! Items live in leaves; every node caches the summary of each child (or item)
//! plus its own total. Cursors walk the tree while accumulating a *dimension*
//! (some projection of the summary, e.g. a byte offset or a row/column point)
//! and can seek to any target in O(log n).
//!
//! Persistence: nodes are immutable once shared. `clone()` is O(1) (it bumps
//! the root's atomic refcount), and every mutating operation copies-on-write
//! any node whose refcount is > 1, so mutating a tree never affects its clones.
//! Snapshots may be cloned/released from multiple threads concurrently.
//!
//! Protocols (all checked at comptime):
//!
//! * Item: `pub const Summary: type` and `pub fn summary(self: *const Item) Summary`.
//!   Items are plain old data: copied by value, never deinitialized.
//! * Summary: `pub const zero: Summary` and
//!   `pub fn add(self: *Summary, other: *const Summary) void` (associative, not
//!   necessarily commutative).
//! * Dimension D: `zero` (a decl `D.zero`, or `0` for numeric types) and a way
//!   to accumulate summaries: `pub fn addSummary(self: *D, s: *const Summary) void`
//!   on D, or — for types that cannot carry methods, like `usize` — a
//!   `pub fn addToDimension(self: *const Summary, comptime D: type, d: *D) void`
//!   on the summary. Numeric dimensions compare with `std.math.order`; struct
//!   dimensions used directly as seek targets provide
//!   `pub fn cmp(a: *const D, b: *const D) std.math.Order`.
//! * Seek target T (see `compareTarget`): either the cursor's dimension itself,
//!   a type with `pub fn cmpDim(self: *const T, pos: anytype) std.math.Order`,
//!   or any type the dimension knows how to compare against through
//!   `pub fn orderTarget(self: *const D, target: anytype) std.math.Order`
//!   (this is how `Dims2(A, B)` lets you seek by either `A` or `B`).

const std = @import("std");
const Allocator = std.mem.Allocator;
const Order = std.math.Order;
const assert = std.debug.assert;

pub const Bias = enum {
    left,
    right,

    pub fn invert(self: Bias) Bias {
        return switch (self) {
            .left => .right,
            .right => .left,
        };
    }
};

// ---------------------------------------------------------------------------
// Dimension / seek-target protocol

fn hasDecl(comptime T: type, comptime name: []const u8) bool {
    return switch (@typeInfo(T)) {
        .@"struct", .@"union", .@"enum", .@"opaque" => @hasDecl(T, name),
        else => false,
    };
}

fn isNumeric(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .int, .float => true,
        else => false,
    };
}

/// The zero value of dimension `D`.
pub fn dimensionZero(comptime D: type) D {
    if (comptime hasDecl(D, "zero")) return D.zero;
    if (comptime isNumeric(D)) return 0;
    @compileError("dimension " ++ @typeName(D) ++ " needs a `pub const zero`");
}

/// `d += summary` for dimension `D`.
pub fn addSummaryToDimension(comptime D: type, d: *D, summary: anytype) void {
    const S = @TypeOf(summary.*);
    if (comptime hasDecl(D, "addSummary")) {
        d.addSummary(summary);
    } else if (comptime hasDecl(S, "addToDimension")) {
        summary.addToDimension(D, d);
    } else {
        @compileError("cannot use " ++ @typeName(D) ++ " as a dimension of " ++ @typeName(S) ++
            ": add `addSummary` to the dimension or `addToDimension` to the summary");
    }
}

/// Total order between two values of dimension `D`.
pub fn compareDimension(comptime D: type, a: *const D, b: *const D) Order {
    if (comptime hasDecl(D, "cmp")) return D.cmp(a, b);
    if (comptime isNumeric(D)) return std.math.order(a.*, b.*);
    @compileError("dimension " ++ @typeName(D) ++ " needs `pub fn cmp(a: *const D, b: *const D) std.math.Order`");
}

fn isComparable(comptime D: type) bool {
    return hasDecl(D, "cmp") or isNumeric(D);
}

/// Orders seek target `target` (a pointer) relative to cursor position
/// `position` (a pointer to the cursor's dimension).
pub fn compareTarget(target: anytype, position: anytype) Order {
    const T = @TypeOf(target.*);
    const D = @TypeOf(position.*);
    if (comptime hasDecl(T, "cmpDim")) {
        return target.cmpDim(position);
    } else if (comptime T == D and isComparable(D)) {
        return compareDimension(D, target, position);
    } else if (comptime hasDecl(D, "orderTarget")) {
        return position.orderTarget(target);
    } else {
        @compileError("cannot seek a cursor over " ++ @typeName(D) ++ " to a " ++ @typeName(T));
    }
}

/// Dimension that tracks nothing; use it for plain iteration.
pub const Unit = struct {
    pub const zero: Unit = .{};
    pub fn addSummary(self: *Unit, summary: anytype) void {
        _ = self;
        _ = summary;
    }
};

/// Seek target that lies beyond every position (used for `suffix`).
pub const End = struct {
    pub fn cmpDim(self: *const End, position: anytype) Order {
        _ = self;
        _ = position;
        return .gt;
    }
};

/// Tracks two dimensions at once. A cursor over `Dims2(A, B)` can seek by an
/// `A` target or by a `B` target (or by anything comparable to either), and
/// `start()`/`end()` report both coordinates — e.g. byte offset and point.
pub fn Dims2(comptime A: type, comptime B: type) type {
    if (A == B) @compileError("Dims2 components must be distinct types");
    return struct {
        a: A,
        b: B,

        const Self = @This();

        pub const zero: Self = .{ .a = dimensionZero(A), .b = dimensionZero(B) };

        pub fn addSummary(self: *Self, summary: anytype) void {
            addSummaryToDimension(A, &self.a, summary);
            addSummaryToDimension(B, &self.b, summary);
        }

        /// Order of `target` relative to `self`, dispatched to the component
        /// whose type matches the target.
        pub fn orderTarget(self: *const Self, target: anytype) Order {
            const T = @TypeOf(target.*);
            if (comptime T == A) {
                return compareTarget(target, &self.a);
            } else if (comptime T == B) {
                return compareTarget(target, &self.b);
            } else {
                @compileError("cannot seek Dims2(" ++ @typeName(A) ++ ", " ++ @typeName(B) ++ ") by " ++ @typeName(T));
            }
        }
    };
}

// ---------------------------------------------------------------------------
// The tree

/// A SumTree with Zed's default branching (TREE_BASE = 6).
pub fn SumTree(comptime Item: type) type {
    return SumTreeWithBase(Item, 6);
}

/// Every node except the root holds between `tree_base` and `2 * tree_base`
/// children (internal) or items (leaf); an internal root holds at least 2.
/// All leaves are at the same depth.
pub fn SumTreeWithBase(comptime Item: type, comptime tree_base: usize) type {
    if (tree_base < 2) @compileError("tree base must be at least 2");
    if (2 * tree_base > 254) @compileError("tree base too large");

    return struct {
        const Self = @This();

        pub const Summary = Item.Summary;
        pub const base = tree_base;
        pub const capacity = 2 * tree_base;

        /// Upper bound on the height of any tree (leaf height = 0). A tree of
        /// height h holds at least 2 * base^h items; we assume fewer than 2^48.
        pub const max_height: usize = blk: {
            var h: usize = 0;
            var min_items: u128 = 2 * base;
            while (min_items <= (1 << 48)) : (h += 1) min_items *= base;
            break :blk h;
        };

        /// `null` means the tree is empty; an empty tree owns no memory.
        root: ?*Node = null,

        const Node = struct {
            ref_count: std.atomic.Value(u32),
            /// 0 for leaves.
            height: u8,
            len: usize,
            summary: Summary,
            /// Per-child (internal) or per-item (leaf) summaries.
            summaries: [capacity]Summary,

            fn isLeaf(n: *const Node) bool {
                return n.height == 0;
            }
            fn asLeaf(n: *Node) *Leaf {
                assert(n.height == 0);
                return @fieldParentPtr("node", n);
            }
            fn asInternal(n: *Node) *Internal {
                assert(n.height > 0);
                return @fieldParentPtr("node", n);
            }
            fn items(n: *Node) []Item {
                return n.asLeaf().items[0..n.len];
            }
            fn children(n: *Node) []*Node {
                return n.asInternal().children[0..n.len];
            }
            fn childSummaries(n: *Node) []Summary {
                return n.summaries[0..n.len];
            }
            fn recomputeSummary(n: *Node) void {
                n.summary = sumOf(n.childSummaries());
            }
        };

        const Leaf = struct {
            node: Node,
            items: [capacity]Item,
        };

        const Internal = struct {
            node: Node,
            children: [capacity]*Node,
        };

        fn sumOf(summaries: []const Summary) Summary {
            var total: Summary = Summary.zero;
            for (summaries) |*s| total.add(s);
            return total;
        }

        fn createLeaf(gpa: Allocator) Allocator.Error!*Node {
            const leaf = try gpa.create(Leaf);
            leaf.node = .{
                .ref_count = .init(1),
                .height = 0,
                .len = 0,
                .summary = Summary.zero,
                .summaries = undefined,
            };
            return &leaf.node;
        }

        fn createInternal(gpa: Allocator, h: u8) Allocator.Error!*Node {
            assert(h > 0);
            const internal = try gpa.create(Internal);
            internal.node = .{
                .ref_count = .init(1),
                .height = h,
                .len = 0,
                .summary = Summary.zero,
                .summaries = undefined,
            };
            return &internal.node;
        }

        fn createLeafFrom(gpa: Allocator, items: []const Item) Allocator.Error!*Node {
            assert(items.len > 0 and items.len <= capacity);
            const n = try createLeaf(gpa);
            const leaf = n.asLeaf();
            @memcpy(leaf.items[0..items.len], items);
            for (items, n.summaries[0..items.len]) |*it, *s| s.* = it.summary();
            n.len = items.len;
            n.recomputeSummary();
            return n;
        }

        fn destroyNode(gpa: Allocator, n: *Node) void {
            if (n.isLeaf()) gpa.destroy(n.asLeaf()) else gpa.destroy(n.asInternal());
        }

        fn retain(n: *Node) void {
            _ = n.ref_count.fetchAdd(1, .monotonic);
        }

        fn release(gpa: Allocator, n: *Node) void {
            if (n.ref_count.fetchSub(1, .acq_rel) != 1) return;
            if (!n.isLeaf()) for (n.children()) |child| release(gpa, child);
            destroyNode(gpa, n);
        }

        /// Arc::make_mut: ensures `slot.*` is uniquely owned, copying it if
        /// shared. Returns the (possibly new) node now stored in `slot`.
        fn makeMut(gpa: Allocator, slot: **Node) Allocator.Error!*Node {
            const n = slot.*;
            if (n.ref_count.load(.acquire) == 1) return n;
            const copy = if (n.isLeaf()) try createLeaf(gpa) else try createInternal(gpa, n.height);
            copy.len = n.len;
            copy.summary = n.summary;
            @memcpy(copy.childSummaries(), n.childSummaries());
            if (n.isLeaf()) {
                @memcpy(copy.items(), n.items());
            } else {
                @memcpy(copy.children(), n.children());
                for (copy.children()) |child| retain(child);
            }
            slot.* = copy;
            release(gpa, n);
            return copy;
        }

        // -- construction / lifetime ---------------------------------------

        /// An empty tree. Allocation-free.
        pub fn init() Self {
            return .{};
        }

        /// Builds a balanced tree bottom-up in O(n).
        pub fn fromSlice(gpa: Allocator, items: []const Item) Allocator.Error!Self {
            if (items.len == 0) return .{};

            var level: std.ArrayList(*Node) = .empty;
            defer level.deinit(gpa);
            errdefer for (level.items) |n| release(gpa, n);
            var next_level: std.ArrayList(*Node) = .empty;
            defer next_level.deinit(gpa);

            // Distribute items evenly so that every node gets >= base entries
            // whenever there is more than one node on a level.
            const leaf_count = divCeil(items.len, capacity);
            try level.ensureTotalCapacityPrecise(gpa, leaf_count);
            var lo: usize = 0;
            for (0..leaf_count) |i| {
                const hi = items.len * (i + 1) / leaf_count;
                level.appendAssumeCapacity(try createLeafFrom(gpa, items[lo..hi]));
                lo = hi;
            }

            var h: u8 = 0;
            while (level.items.len > 1) {
                h += 1;
                const count = level.items.len;
                const parent_count = divCeil(count, capacity);
                next_level.clearRetainingCapacity();
                try next_level.ensureTotalCapacityPrecise(gpa, parent_count);
                {
                    errdefer for (next_level.items) |p| destroyNode(gpa, p);
                    for (0..parent_count) |_| next_level.appendAssumeCapacity(try createInternal(gpa, h));
                }
                lo = 0;
                for (next_level.items, 0..) |parent, i| {
                    const hi = count * (i + 1) / parent_count;
                    const kids = level.items[lo..hi];
                    @memcpy(parent.asInternal().children[0..kids.len], kids);
                    for (kids, parent.summaries[0..kids.len]) |kid, *s| s.* = kid.summary;
                    parent.len = kids.len;
                    parent.recomputeSummary();
                    lo = hi;
                }
                level.clearRetainingCapacity();
                level.appendSliceAssumeCapacity(next_level.items);
            }
            return .{ .root = level.items[0] };
        }

        /// O(1): shares all nodes with `self`.
        pub fn clone(self: *const Self) Self {
            if (self.root) |r| retain(r);
            return self.*;
        }

        pub fn deinit(self: *Self, gpa: Allocator) void {
            if (self.root) |r| release(gpa, r);
            self.* = undefined;
        }

        // -- queries --------------------------------------------------------

        pub fn summary(self: *const Self) Summary {
            return if (self.root) |r| r.summary else Summary.zero;
        }

        /// The total extent of the tree in dimension `D`.
        pub fn extent(self: *const Self, comptime D: type) D {
            var d = dimensionZero(D);
            if (self.root) |r| addSummaryToDimension(D, &d, &r.summary);
            return d;
        }

        pub fn isEmpty(self: *const Self) bool {
            return self.root == null;
        }

        pub fn first(self: *const Self) ?*const Item {
            var n = self.root orelse return null;
            while (!n.isLeaf()) n = n.children()[0];
            return &n.items()[0];
        }

        pub fn last(self: *const Self) ?*const Item {
            var n = self.root orelse return null;
            while (!n.isLeaf()) n = n.children()[n.len - 1];
            return &n.items()[n.len - 1];
        }

        pub fn height(self: *const Self) usize {
            return if (self.root) |r| r.height else 0;
        }

        /// Number of items. O(n / base) — walks every internal node.
        pub fn itemCount(self: *const Self) usize {
            return if (self.root) |r| countItems(r) else 0;
        }

        fn countItems(n: *Node) usize {
            if (n.isLeaf()) return n.len;
            var total: usize = 0;
            for (n.children()) |child| total += countItems(child);
            return total;
        }

        // -- mutation -------------------------------------------------------
        //
        // On error.OutOfMemory a mutated tree is left structurally valid and
        // leak-free (it can be deinit'ed) but its contents are unspecified,
        // except for `updateLast` and `push` into a non-full last leaf, which
        // are all-or-nothing. Clones are never affected.

        pub fn push(self: *Self, gpa: Allocator, item: Item) Allocator.Error!void {
            // Fast path: room in the rightmost leaf.
            if (self.root) |r| {
                var n = r;
                while (!n.isLeaf()) n = n.children()[n.len - 1];
                if (n.len < capacity) {
                    var path: [max_height + 1]*Node = undefined;
                    const depth = try self.makeRightSpineMut(gpa, &path);
                    const leaf = path[depth - 1];
                    leaf.asLeaf().items[leaf.len] = item;
                    leaf.summaries[leaf.len] = item.summary();
                    leaf.len += 1;
                    fixRightSpine(path[0..depth]);
                    return;
                }
            }
            const one = [1]Item{item};
            try self.extend(gpa, &one);
        }

        pub fn extend(self: *Self, gpa: Allocator, items: []const Item) Allocator.Error!void {
            if (items.len == 0) return;
            const other = try fromSlice(gpa, items);
            try self.append(gpa, other);
        }

        /// Concatenates `other` onto the end of `self`, taking ownership of
        /// `other` (even on error). O(log n): the shorter tree is grafted
        /// onto the matching level of the taller tree's edge spine (Zed's
        /// `append`/`push_tree_recursive`/`append_large`).
        pub fn append(self: *Self, gpa: Allocator, other: Self) Allocator.Error!void {
            const other_root = other.root orelse return;
            const self_root = self.root orelse {
                self.root = other_root;
                return;
            };
            if (self_root.height >= other_root.height) {
                const split = try appendRec(gpa, &self.root.?, other_root, .right) orelse return;
                self.root = joinRoots(gpa, self.root.?, split) catch |err| {
                    release(gpa, split);
                    return err;
                };
            } else {
                // Graft `self` onto the left spine of the taller `other`.
                self.root = null;
                var new_root = other_root;
                errdefer release(gpa, new_root);
                if (try appendRec(gpa, &new_root, self_root, .left)) |split| {
                    new_root = joinRoots(gpa, split, new_root) catch |err| {
                        release(gpa, split);
                        return err;
                    };
                }
                self.root = new_root;
            }
        }

        /// Mutates the last item in place (no-op on an empty tree) and
        /// recomputes the summaries along the right spine. `f` must not
        /// touch the tree.
        pub fn updateLast(
            self: *Self,
            gpa: Allocator,
            ctx: anytype,
            comptime f: fn (@TypeOf(ctx), *Item) void,
        ) Allocator.Error!void {
            if (self.root == null) return;
            var path: [max_height + 1]*Node = undefined;
            const depth = try self.makeRightSpineMut(gpa, &path);
            const leaf = path[depth - 1];
            const it = &leaf.items()[leaf.len - 1];
            f(ctx, it);
            leaf.summaries[leaf.len - 1] = it.summary();
            fixRightSpine(path[0..depth]);
        }

        /// Mutates, in place, the item that `cursor(D).seek(target, bias)`
        /// lands on, copying shared nodes on the way down (clones are
        /// unaffected), then recomputes the summaries above it. `f(ctx, item,
        /// start)` gets the item and the position of its start, and returns
        /// whether it changed the item. Returns false when there is no such
        /// item (empty tree, target past the end) or `f` declined. O(log n);
        /// allocation-free when the path is uniquely owned.
        pub fn updateAt(
            self: *Self,
            gpa: Allocator,
            comptime D: type,
            target: anytype,
            bias: Bias,
            ctx: anytype,
            comptime f: fn (@TypeOf(ctx), *Item, D) bool,
        ) Allocator.Error!bool {
            if (self.root == null) return false;
            var path: [max_height + 1]*Node = undefined;
            var index: [max_height + 1]usize = undefined;
            var slot: **Node = &self.root.?;
            var pos = dimensionZero(D);
            var depth: usize = 0;
            while (true) {
                const n = try makeMut(gpa, slot);
                // Same descent as `Cursor.seekInternal`.
                const i = for (n.childSummaries(), 0..) |*s, i| {
                    var child_end = pos;
                    addSummaryToDimension(D, &child_end, s);
                    if (!Cursor(D).shouldSkip(compareTarget(target, &child_end), bias)) break i;
                    pos = child_end;
                } else return false;
                path[depth] = n;
                index[depth] = i;
                depth += 1;
                if (n.isLeaf()) break;
                slot = &n.asInternal().children[i];
            }
            const leaf = path[depth - 1];
            const it = &leaf.items()[index[depth - 1]];
            if (!f(ctx, it, pos)) return false;
            leaf.summaries[index[depth - 1]] = it.summary();
            var k = depth;
            while (k > 0) {
                k -= 1;
                if (k + 1 < depth) path[k].summaries[index[k]] = path[k + 1].summary;
                path[k].recomputeSummary();
            }
            return true;
        }

        /// Copies-on-write every node on the right spine, storing them
        /// root-first in `path`. Returns the number of nodes.
        fn makeRightSpineMut(self: *Self, gpa: Allocator, path: *[max_height + 1]*Node) Allocator.Error!usize {
            var slot: **Node = &self.root.?;
            var depth: usize = 0;
            while (true) {
                const n = try makeMut(gpa, slot);
                path[depth] = n;
                depth += 1;
                if (n.isLeaf()) return depth;
                slot = &n.asInternal().children[n.len - 1];
            }
        }

        /// Recomputes summaries bottom-up after the last leaf changed.
        fn fixRightSpine(path: []const *Node) void {
            var i = path.len;
            while (i > 0) {
                i -= 1;
                const n = path[i];
                if (i + 1 < path.len) n.summaries[n.len - 1] = path[i + 1].summary;
                n.recomputeSummary();
            }
        }

        const Side = enum { left, right };

        /// Inserts the contents of `other` at the `side` edge of the subtree
        /// in `slot`, which must be at least as tall as `other`. Consumes
        /// `other`. If the node overflows it splits; the returned sibling
        /// belongs immediately on `side` of the node in `slot`.
        fn appendRec(gpa: Allocator, slot: **Node, other: *Node, comptime side: Side) Allocator.Error!?*Node {
            const n = makeMut(gpa, slot) catch |err| {
                release(gpa, other);
                return err;
            };
            assert(n.height >= other.height);

            if (n.isLeaf()) {
                defer release(gpa, other);
                return insertEntries(Item, gpa, n, other.items(), other.childSummaries(), side);
            }

            const delta = n.height - other.height;
            if (delta == 0) {
                // Adopt other's children; drop other's own node.
                var kids: [capacity]*Node = undefined;
                var kid_summaries: [capacity]Summary = undefined;
                const count = other.len;
                @memcpy(kids[0..count], other.children());
                @memcpy(kid_summaries[0..count], other.childSummaries());
                for (kids[0..count]) |kid| retain(kid);
                release(gpa, other);
                return insertChildren(gpa, n, kids[0..count], kid_summaries[0..count], side);
            }
            if (delta == 1 and other.len >= base) {
                const s = [1]Summary{other.summary};
                const kid = [1]*Node{other};
                return insertChildren(gpa, n, &kid, &s, side);
            }

            const idx = if (side == .right) n.len - 1 else 0;
            const child_slot = &n.asInternal().children[idx];
            const maybe_split = try appendRec(gpa, child_slot, other, side);
            n.summaries[idx] = child_slot.*.summary;
            if (maybe_split) |split| {
                const s = [1]Summary{split.summary};
                const kid = [1]*Node{split};
                return insertChildren(gpa, n, &kid, &s, side);
            }
            n.recomputeSummary();
            return null;
        }

        /// Like `insertEntries`, but takes ownership of `kids` even on error.
        fn insertChildren(gpa: Allocator, n: *Node, kids: []const *Node, kid_summaries: []const Summary, comptime side: Side) Allocator.Error!?*Node {
            return insertEntries(*Node, gpa, n, kids, kid_summaries, side) catch |err| {
                for (kids) |kid| release(gpa, kid);
                return err;
            };
        }

        fn entriesOf(comptime E: type, n: *Node) *[capacity]E {
            return if (E == Item) &n.asLeaf().items else &n.asInternal().children;
        }

        /// Inserts entries (items for leaves, children for internal nodes)
        /// on the `side` edge of `n`. On overflow the combined entries are
        /// split in half; `n` keeps one half and the returned new sibling
        /// holds the half on `side`. Allocates before mutating anything.
        fn insertEntries(comptime E: type, gpa: Allocator, n: *Node, new: []const E, new_summaries: []const Summary, comptime side: Side) Allocator.Error!?*Node {
            const entries = entriesOf(E, n);
            const old_len = n.len;
            const total = old_len + new.len;

            if (total <= capacity) {
                switch (side) {
                    .right => {
                        @memcpy(entries[old_len..total], new);
                        @memcpy(n.summaries[old_len..total], new_summaries);
                    },
                    .left => {
                        std.mem.copyBackwards(E, entries[new.len..total], entries[0..old_len]);
                        std.mem.copyBackwards(Summary, n.summaries[new.len..total], n.summaries[0..old_len]);
                        @memcpy(entries[0..new.len], new);
                        @memcpy(n.summaries[0..new.len], new_summaries);
                    },
                }
                n.len = total;
                n.recomputeSummary();
                return null;
            }

            const sibling = if (E == Item) try createLeaf(gpa) else try createInternal(gpa, n.height);
            var all: [2 * capacity]E = undefined;
            var all_summaries: [2 * capacity]Summary = undefined;
            switch (side) {
                .right => {
                    @memcpy(all[0..old_len], entries[0..old_len]);
                    @memcpy(all[old_len..total], new);
                    @memcpy(all_summaries[0..old_len], n.summaries[0..old_len]);
                    @memcpy(all_summaries[old_len..total], new_summaries);
                },
                .left => {
                    @memcpy(all[0..new.len], new);
                    @memcpy(all[new.len..total], entries[0..old_len]);
                    @memcpy(all_summaries[0..new.len], new_summaries);
                    @memcpy(all_summaries[new.len..total], n.summaries[0..old_len]);
                },
            }
            const mid = (total + 1) / 2;
            const left_node, const right_node = switch (side) {
                .right => .{ n, sibling },
                .left => .{ sibling, n },
            };
            fillEntries(E, left_node, all[0..mid], all_summaries[0..mid]);
            fillEntries(E, right_node, all[mid..total], all_summaries[mid..total]);
            return sibling;
        }

        fn fillEntries(comptime E: type, n: *Node, src: []const E, src_summaries: []const Summary) void {
            @memcpy(entriesOf(E, n)[0..src.len], src);
            @memcpy(n.summaries[0..src.len], src_summaries);
            n.len = src.len;
            n.recomputeSummary();
        }

        fn joinRoots(gpa: Allocator, left: *Node, right: *Node) Allocator.Error!*Node {
            assert(left.height == right.height);
            if (left.height + 1 > max_height) @panic("SumTree: maximum height exceeded");
            const n = try createInternal(gpa, left.height + 1);
            const kids = [2]*Node{ left, right };
            const s = [2]Summary{ left.summary, right.summary };
            fillEntries(*Node, n, &kids, &s);
            return n;
        }

        // -- iteration ------------------------------------------------------

        pub fn cursor(self: *const Self, comptime D: type) Cursor(D) {
            return Cursor(D).init(self.root);
        }

        pub fn iter(self: *const Self) Iterator {
            return .{ .inner = self.cursor(Unit) };
        }

        pub const Iterator = struct {
            inner: Cursor(Unit),

            pub fn next(it: *Iterator) ?*const Item {
                const x = it.inner.item() orelse return null;
                it.inner.next();
                return x;
            }
        };

        /// Copies all items into a new slice (mostly for tests/debugging).
        pub fn toOwnedSlice(self: *const Self, gpa: Allocator) Allocator.Error![]Item {
            var list: std.ArrayList(Item) = .empty;
            errdefer list.deinit(gpa);
            var it = self.iter();
            while (it.next()) |x| try list.append(gpa, x.*);
            return list.toOwnedSlice(gpa);
        }

        /// Verifies structural invariants and cached summaries. For tests.
        pub fn checkInvariants(self: *const Self) error{InvariantViolation}!void {
            const r = self.root orelse return;
            if (!r.isLeaf() and r.len < 2) return error.InvariantViolation;
            try checkNode(r, true);
        }

        fn checkNode(n: *Node, is_root: bool) error{InvariantViolation}!void {
            if (n.len == 0 or n.len > capacity) return error.InvariantViolation;
            if (!is_root and n.len < base) return error.InvariantViolation;
            if (n.ref_count.load(.monotonic) == 0) return error.InvariantViolation;
            if (n.isLeaf()) {
                for (n.items(), n.childSummaries()) |*it, s| {
                    if (!std.meta.eql(it.summary(), s)) return error.InvariantViolation;
                }
            } else {
                for (n.children(), n.childSummaries()) |child, s| {
                    if (child.height + 1 != n.height) return error.InvariantViolation;
                    if (!std.meta.eql(child.summary, s)) return error.InvariantViolation;
                    try checkNode(child, false);
                }
            }
            if (!std.meta.eql(sumOf(n.childSummaries()), n.summary)) return error.InvariantViolation;
        }

        // -- cursor ---------------------------------------------------------

        /// A position within the tree, tracking dimension `D` (the sum of `D`
        /// over all items before the current one). A fresh cursor sits on the
        /// first item. Besides "on an item" a cursor can be *at the end*
        /// (`item() == null`, `start()` = total) or *before the start*
        /// (reached by `prev()` from the first item; `item() == null`,
        /// `start()` = zero).
        ///
        /// A cursor borrows the tree's nodes: it is invalidated by any
        /// mutation or deinit of the tree it came from (clones keep it alive).
        pub fn Cursor(comptime D: type) type {
            return struct {
                const C = @This();

                const Entry = struct {
                    node: *Node,
                    /// Index of the child/item we are in.
                    index: usize,
                    /// Position at the start of `node`.
                    position: D,
                };

                root: ?*Node,
                stack: [max_height + 1]Entry = undefined,
                depth: usize = 0,
                /// Position at the start of the current item.
                position: D,
                at_end: bool,

                fn init(root: ?*Node) C {
                    var c: C = .{ .root = root, .position = dimensionZero(D), .at_end = root == null };
                    if (root) |r| c.descendFirst(r, dimensionZero(D));
                    return c;
                }

                /// Moves back to the first item (as if freshly created).
                pub fn reset(c: *C) void {
                    c.depth = 0;
                    c.position = dimensionZero(D);
                    c.at_end = c.root == null;
                    if (c.root) |r| c.descendFirst(r, dimensionZero(D));
                }

                pub fn item(c: *const C) ?*const Item {
                    if (c.depth == 0) return null;
                    const e = &c.stack[c.depth - 1];
                    return &e.node.items()[e.index];
                }

                pub fn itemSummary(c: *const C) ?*const Summary {
                    if (c.depth == 0) return null;
                    const e = &c.stack[c.depth - 1];
                    return &e.node.summaries[e.index];
                }

                /// Position before the current item.
                pub fn start(c: *const C) D {
                    return c.position;
                }

                /// Position after the current item (== `start()` when there is
                /// no current item).
                pub fn end(c: *const C) D {
                    var pos = c.position;
                    if (c.itemSummary()) |s| addSummaryToDimension(D, &pos, s);
                    return pos;
                }

                pub fn isAtEnd(c: *const C) bool {
                    return c.depth == 0 and c.at_end;
                }

                /// The item after the current one, without moving.
                pub fn nextItem(c: *const C) ?*const Item {
                    if (c.depth == 0) {
                        if (c.at_end) return null;
                        var n = c.root orelse return null;
                        while (!n.isLeaf()) n = n.children()[0];
                        return &n.items()[0];
                    }
                    var i = c.depth;
                    while (i > 0) {
                        i -= 1;
                        const e = &c.stack[i];
                        if (e.index + 1 < e.node.len) {
                            if (e.node.isLeaf()) return &e.node.items()[e.index + 1];
                            var n = e.node.children()[e.index + 1];
                            while (!n.isLeaf()) n = n.children()[0];
                            return &n.items()[0];
                        }
                    }
                    return null;
                }

                /// The item before the current one, without moving.
                pub fn prevItem(c: *const C) ?*const Item {
                    if (c.depth == 0) {
                        if (!c.at_end) return null;
                        var n = c.root orelse return null;
                        while (!n.isLeaf()) n = n.children()[n.len - 1];
                        return &n.items()[n.len - 1];
                    }
                    var i = c.depth;
                    while (i > 0) {
                        i -= 1;
                        const e = &c.stack[i];
                        if (e.index > 0) {
                            if (e.node.isLeaf()) return &e.node.items()[e.index - 1];
                            var n = e.node.children()[e.index - 1];
                            while (!n.isLeaf()) n = n.children()[n.len - 1];
                            return &n.items()[n.len - 1];
                        }
                    }
                    return null;
                }

                fn pushEntry(c: *C, n: *Node, index: usize, pos: D) void {
                    if (c.depth >= c.stack.len) @panic("SumTree cursor: maximum height exceeded");
                    c.stack[c.depth] = .{ .node = n, .index = index, .position = pos };
                    c.depth += 1;
                }

                fn descendFirst(c: *C, from: *Node, pos: D) void {
                    var n = from;
                    while (true) {
                        c.pushEntry(n, 0, pos);
                        if (n.isLeaf()) break;
                        n = n.children()[0];
                    }
                    c.position = pos;
                    c.at_end = false;
                }

                fn descendLast(c: *C, from: *Node, from_pos: D) void {
                    var n = from;
                    var pos = from_pos;
                    while (true) {
                        const idx = n.len - 1;
                        c.pushEntry(n, idx, pos);
                        for (n.summaries[0..idx]) |*s| addSummaryToDimension(D, &pos, s);
                        if (n.isLeaf()) break;
                        n = n.children()[idx];
                    }
                    c.position = pos;
                    c.at_end = false;
                }

                /// Advances to the next item. From "before the start" this
                /// moves to the first item; at the end it does nothing.
                pub fn next(c: *C) void {
                    if (c.depth == 0) {
                        if (c.at_end) return;
                        if (c.root) |r| c.descendFirst(r, dimensionZero(D)) else c.at_end = true;
                        return;
                    }
                    var e = &c.stack[c.depth - 1];
                    addSummaryToDimension(D, &c.position, &e.node.summaries[e.index]);
                    e.index += 1;
                    if (e.index < e.node.len) return;
                    c.depth -= 1;
                    while (c.depth > 0) {
                        e = &c.stack[c.depth - 1];
                        e.index += 1;
                        if (e.index < e.node.len) {
                            c.descendFirst(e.node.children()[e.index], c.position);
                            return;
                        }
                        c.depth -= 1;
                    }
                    c.at_end = true;
                }

                /// Moves to the previous item. From the end this moves to the
                /// last item; from the first item it moves "before the start".
                pub fn prev(c: *C) void {
                    if (c.depth == 0) {
                        if (!c.at_end) return;
                        if (c.root) |r| c.descendLast(r, dimensionZero(D));
                        return;
                    }
                    var e = &c.stack[c.depth - 1];
                    if (e.index > 0) {
                        e.index -= 1;
                        c.position = e.position;
                        for (e.node.summaries[0..e.index]) |*s| addSummaryToDimension(D, &c.position, s);
                        return;
                    }
                    c.depth -= 1;
                    while (c.depth > 0) {
                        e = &c.stack[c.depth - 1];
                        if (e.index > 0) {
                            e.index -= 1;
                            var pos = e.position;
                            for (e.node.summaries[0..e.index]) |*s| addSummaryToDimension(D, &pos, s);
                            c.descendLast(e.node.children()[e.index], pos);
                            return;
                        }
                        c.depth -= 1;
                    }
                    c.position = dimensionZero(D);
                    c.at_end = false;
                }

                /// Seeks from the start of the tree. `target` is a pointer to
                /// a seek target (see `compareTarget`), e.g. `&@as(usize, 7)`
                /// or `&point`. Lands on the first item
                /// whose end is `>= target` (`.left`) or `> target` (`.right`);
                /// i.e. when `target` falls exactly on the boundary between
                /// items A and B, `.left` stays on A and `.right` moves to B
                /// (and zero-extent items at the boundary are skipped by
                /// `.right` only). If no such item exists the cursor ends up
                /// at the end. Returns true iff the target hits the cursor
                /// exactly: `target == end()` for `.left`, `target == start()`
                /// for `.right`.
                pub fn seek(c: *C, target: anytype, bias: Bias) bool {
                    c.depth = 0;
                    c.position = dimensionZero(D);
                    c.at_end = c.root == null;
                    return c.seekForward(target, bias);
                }

                /// Like `seek`, but continues from the current position (the
                /// target must not be behind it). The current item itself is
                /// re-examined, so this never skips past an item containing
                /// the target.
                pub fn seekForward(c: *C, target: anytype, bias: Bias) bool {
                    var agg: NoAggregate = .{};
                    return c.seekInternal(target, bias, &agg) catch |err| switch (err) {};
                }

                /// Returns a new tree with the items from the current position
                /// up to where `seekForward(target, bias)` lands (exclusive),
                /// and leaves the cursor there — Zed's `Cursor::slice`.
                /// Whole subtrees are shared, so this is O(log n).
                pub fn slice(c: *C, gpa: Allocator, target: anytype, bias: Bias) Allocator.Error!Self {
                    var agg: SliceAggregate = .{ .gpa = gpa };
                    errdefer {
                        agg.tree.deinit(gpa);
                        if (agg.leaf) |l| destroyNode(gpa, l);
                    }
                    _ = try c.seekInternal(target, bias, &agg);
                    return agg.tree;
                }

                /// All items from the current one to the end; leaves the
                /// cursor at the end.
                pub fn suffix(c: *C, gpa: Allocator) Allocator.Error!Self {
                    const target: End = .{};
                    return c.slice(gpa, &target, .right);
                }

                /// The sum of dimension `Output` over the items from the
                /// current position up to where `seekForward(target, bias)`
                /// lands, leaving the cursor there. O(log n).
                pub fn summary(c: *C, comptime Output: type, target: anytype, bias: Bias) Output {
                    var agg: SummaryAggregate(Output) = .{ .out = dimensionZero(Output) };
                    _ = c.seekInternal(target, bias, &agg) catch |err| switch (err) {};
                    return agg.out;
                }

                fn shouldSkip(order: Order, bias: Bias) bool {
                    return order == .gt or (order == .eq and bias == .right);
                }

                fn seekInternal(c: *C, target: anytype, bias: Bias, agg: anytype) @TypeOf(agg.*).Error!bool {
                    assert(compareTarget(target, &c.position) != .lt); // cannot seek backward
                    if (c.depth == 0 and !c.at_end) {
                        // Before the start (or freshly reset): descend from the root.
                        const r = c.root orelse unreachable;
                        c.position = dimensionZero(D);
                        c.pushEntry(r, 0, c.position);
                    }

                    var ascending = false;
                    outer: while (c.depth > 0) {
                        const e = &c.stack[c.depth - 1];
                        const n = e.node;
                        if (!n.isLeaf()) {
                            if (ascending) e.index += 1;
                            while (e.index < n.len) : (e.index += 1) {
                                var child_end = c.position;
                                addSummaryToDimension(D, &child_end, &n.summaries[e.index]);
                                if (shouldSkip(compareTarget(target, &child_end), bias)) {
                                    c.position = child_end;
                                    try agg.pushTree(n.children()[e.index]);
                                } else {
                                    c.pushEntry(n.children()[e.index], 0, c.position);
                                    ascending = false;
                                    continue :outer;
                                }
                            }
                        } else {
                            while (e.index < n.len) : (e.index += 1) {
                                var item_end = c.position;
                                addSummaryToDimension(D, &item_end, &n.summaries[e.index]);
                                if (shouldSkip(compareTarget(target, &item_end), bias)) {
                                    c.position = item_end;
                                    try agg.pushItem(&n.items()[e.index], &n.summaries[e.index]);
                                } else {
                                    try agg.endLeaf();
                                    break :outer;
                                }
                            }
                            try agg.endLeaf();
                        }
                        c.depth -= 1;
                        ascending = true;
                    }
                    c.at_end = c.depth == 0;

                    var hit = c.position;
                    if (bias == .left) {
                        if (c.itemSummary()) |s| addSummaryToDimension(D, &hit, s);
                    }
                    return compareTarget(target, &hit) == .eq;
                }
            };
        }

        const NoAggregate = struct {
            const Error = error{};
            fn pushTree(_: *NoAggregate, _: *Node) Error!void {}
            fn pushItem(_: *NoAggregate, _: *const Item, _: *const Summary) Error!void {}
            fn endLeaf(_: *NoAggregate) Error!void {}
        };

        fn SummaryAggregate(comptime Output: type) type {
            return struct {
                out: Output,
                const Error = error{};
                fn pushTree(a: *@This(), n: *Node) Error!void {
                    addSummaryToDimension(Output, &a.out, &n.summary);
                }
                fn pushItem(a: *@This(), _: *const Item, s: *const Summary) Error!void {
                    addSummaryToDimension(Output, &a.out, s);
                }
                fn endLeaf(_: *@This()) Error!void {}
            };
        }

        /// Collects skipped subtrees (shared) and items (copied along with
        /// their summaries, batched per leaf) into a new tree. Reusing the
        /// summaries matters for big items: a rope chunk's is a pass over
        /// its text.
        const SliceAggregate = struct {
            gpa: Allocator,
            tree: Self = .{},
            /// The leaf `pushItem` is filling (owned here until `endLeaf`).
            leaf: ?*Node = null,

            const Error = Allocator.Error;

            fn pushTree(a: *SliceAggregate, n: *Node) Error!void {
                retain(n);
                try a.tree.append(a.gpa, .{ .root = n });
            }
            fn pushItem(a: *SliceAggregate, it: *const Item, s: *const Summary) Error!void {
                const n = a.leaf orelse blk: {
                    const l = try createLeaf(a.gpa);
                    a.leaf = l;
                    break :blk l;
                };
                n.asLeaf().items[n.len] = it.*;
                n.summaries[n.len] = s.*;
                n.len += 1;
            }
            fn endLeaf(a: *SliceAggregate) Error!void {
                const n = a.leaf orelse return;
                a.leaf = null;
                n.recomputeSummary();
                try a.tree.append(a.gpa, .{ .root = n });
            }
        };
    };
}

fn divCeil(a: usize, b: usize) usize {
    return (a + b - 1) / b;
}

// ---------------------------------------------------------------------------
// Tests

const testing = std.testing;

/// Test item: summary = count + sum + max.
const TestItem = struct {
    value: u32,

    pub const Summary = struct {
        count: usize = 0,
        sum: u64 = 0,
        max: u32 = 0,

        pub const zero: Summary = .{};

        pub fn add(self: *Summary, other: *const Summary) void {
            self.count += other.count;
            self.sum += other.sum;
            self.max = @max(self.max, other.max);
        }

        /// `usize` as a dimension means "item count".
        pub fn addToDimension(self: *const Summary, comptime D: type, d: *D) void {
            if (D != usize) @compileError("unsupported dimension");
            d.* += self.count;
        }
    };

    pub fn summary(self: *const TestItem) Summary {
        return .{ .count = 1, .sum = self.value, .max = self.value };
    }
};

/// Dimension: running sum of values.
const Sum = struct {
    value: u64 = 0,
    pub const zero: Sum = .{};
    pub fn addSummary(self: *Sum, s: *const TestItem.Summary) void {
        self.value += s.sum;
    }
    pub fn cmp(a: *const Sum, b: *const Sum) Order {
        return std.math.order(a.value, b.value);
    }
};

/// Custom seek target: "the first position whose sum reaches at least N",
/// expressed through `cmpDim`.
const SumAtLeast = struct {
    n: u64,
    pub fn cmpDim(self: *const SumAtLeast, pos: anytype) Order {
        const P = @TypeOf(pos.*);
        const v = if (P == Sum) pos.value else pos.b.value;
        return std.math.order(self.n, v);
    }
};

fn testTree(comptime base_: usize) type {
    return SumTreeWithBase(TestItem, base_);
}

fn itemsOf(values: []const u32, buf: []TestItem) []TestItem {
    for (values, 0..) |v, i| buf[i] = .{ .value = v };
    return buf[0..values.len];
}

fn expectTreeEquals(comptime T: type, tree: *const T, expected: []const TestItem) !void {
    try tree.checkInvariants();
    const got = try tree.toOwnedSlice(testing.allocator);
    defer testing.allocator.free(got);
    try testing.expectEqual(expected.len, got.len);
    for (expected, got) |e, g| try testing.expectEqual(e.value, g.value);
    var expected_summary: TestItem.Summary = .zero;
    for (expected) |*e| expected_summary.add(&e.summary());
    try testing.expectEqual(expected_summary, tree.summary());
}

test "empty tree" {
    const T = testTree(2);
    var t = T.init();
    defer t.deinit(testing.allocator);
    try testing.expect(t.isEmpty());
    try testing.expect(t.first() == null);
    try testing.expect(t.last() == null);
    try testing.expectEqual(@as(usize, 0), t.itemCount());
    var c = t.cursor(usize);
    try testing.expect(c.item() == null);
    try testing.expect(c.isAtEnd());
    c.next();
    c.prev();
    try testing.expect(c.item() == null);
    try testing.expect(c.seek(&@as(usize, 0), .right));
    var s = try c.suffix(testing.allocator);
    defer s.deinit(testing.allocator);
    try testing.expect(s.isEmpty());
    var it = t.iter();
    try testing.expect(it.next() == null);
}

test "push, extend, fromSlice, iterate, prev/next" {
    const gpa = testing.allocator;
    inline for (.{ 2, 3, 6 }) |b| {
        const T = testTree(b);
        var model: std.ArrayList(TestItem) = .empty;
        defer model.deinit(gpa);
        var t = T.init();
        defer t.deinit(gpa);
        for (0..200) |i| {
            const it: TestItem = .{ .value = @intCast(i * 7 % 13) };
            if (i % 3 == 0) {
                try t.push(gpa, it);
                try model.append(gpa, it);
            } else {
                const two = [_]TestItem{ it, .{ .value = 1 } };
                try t.extend(gpa, &two);
                try model.appendSlice(gpa, &two);
            }
        }
        try expectTreeEquals(T, &t, model.items);
        try testing.expectEqual(model.items.len, t.itemCount());
        try testing.expectEqual(model.items[0].value, t.first().?.value);
        try testing.expectEqual(model.items[model.items.len - 1].value, t.last().?.value);

        var f = try T.fromSlice(gpa, model.items);
        defer f.deinit(gpa);
        try expectTreeEquals(T, &f, model.items);

        // Walk backwards from the end with prev(), checking positions.
        var c = f.cursor(usize);
        _ = c.seek(&model.items.len, .right);
        try testing.expect(c.isAtEnd());
        var i: usize = model.items.len;
        while (i > 0) {
            i -= 1;
            c.prev();
            try testing.expectEqual(model.items[i].value, c.item().?.value);
            try testing.expectEqual(i, c.start());
            try testing.expectEqual(i + 1, c.end());
        }
        c.prev();
        try testing.expect(c.item() == null);
        try testing.expect(!c.isAtEnd());
        try testing.expectEqual(@as(usize, 0), c.start());
        c.next();
        try testing.expectEqual(model.items[0].value, c.item().?.value);
        try testing.expectEqual(model.items[1].value, c.nextItem().?.value);
        try testing.expect(c.prevItem() == null);
    }
}

test "seek bias semantics on boundaries and zero-extent items" {
    const gpa = testing.allocator;
    inline for (.{ 2, 6 }) |b| {
        const T = testTree(b);
        // Items' Sum extents: [0,1) [1,3) [3,3) [3,6)
        var buf: [4]TestItem = undefined;
        var t = try T.fromSlice(gpa, itemsOf(&.{ 1, 2, 0, 3 }, &buf));
        defer t.deinit(gpa);
        var c = t.cursor(Dims2(usize, Sum));

        const Case = struct { target: u64, bias: Bias, index: usize, start: u64, hit: bool };
        const cases = [_]Case{
            .{ .target = 0, .bias = .left, .index = 0, .start = 0, .hit = false },
            .{ .target = 0, .bias = .right, .index = 0, .start = 0, .hit = true },
            .{ .target = 1, .bias = .left, .index = 0, .start = 0, .hit = true },
            .{ .target = 1, .bias = .right, .index = 1, .start = 1, .hit = true },
            .{ .target = 2, .bias = .left, .index = 1, .start = 1, .hit = false },
            .{ .target = 2, .bias = .right, .index = 1, .start = 1, .hit = false },
            .{ .target = 3, .bias = .left, .index = 1, .start = 1, .hit = true },
            .{ .target = 3, .bias = .right, .index = 3, .start = 3, .hit = true },
            .{ .target = 6, .bias = .left, .index = 3, .start = 3, .hit = true },
            .{ .target = 6, .bias = .right, .index = 4, .start = 6, .hit = true },
            .{ .target = 7, .bias = .left, .index = 4, .start = 6, .hit = false },
        };
        for (cases) |case| {
            const target: Sum = .{ .value = case.target };
            try testing.expectEqual(case.hit, c.seek(&target, case.bias));
            try testing.expectEqual(case.index, c.start().a);
            try testing.expectEqual(case.start, c.start().b.value);
            try testing.expectEqual(case.index == 4, c.item() == null);
            // Same via a custom `cmpDim` target.
            const custom: SumAtLeast = .{ .n = case.target };
            try testing.expectEqual(case.hit, c.seek(&custom, case.bias));
            try testing.expectEqual(case.index, c.start().a);
        }
        // Seeking by the other component of Dims2 (item count).
        try testing.expect(c.seek(&@as(usize, 2), .right));
        try testing.expectEqual(@as(u32, 0), c.item().?.value);
        try testing.expectEqual(@as(u64, 3), c.start().b.value);
        try testing.expectEqual(@as(u64, 3), c.end().b.value);
        // seekForward re-examines the current item.
        try testing.expect(c.seekForward(&Sum{ .value = 3 }, .left));
        try testing.expectEqual(@as(usize, 2), c.start().a);
        try testing.expect(c.seekForward(&Sum{ .value = 3 }, .right));
        try testing.expectEqual(@as(usize, 3), c.start().a);
    }
}

test "slice, suffix and summary" {
    const gpa = testing.allocator;
    const T = testTree(2);
    var buf: [100]TestItem = undefined;
    var values: [100]u32 = undefined;
    for (&values, 0..) |*v, i| v.* = @intCast(i);
    const items = itemsOf(&values, &buf);
    var t = try T.fromSlice(gpa, items);
    defer t.deinit(gpa);

    var c = t.cursor(usize);
    _ = c.seek(&@as(usize, 10), .right);
    var mid = try c.slice(gpa, &@as(usize, 37), .right);
    defer mid.deinit(gpa);
    try expectTreeEquals(T, &mid, items[10..37]);
    try testing.expectEqual(@as(u32, 37), c.item().?.value);
    const s = c.summary(Sum, &@as(usize, 50), .right);
    try testing.expectEqual(@as(u64, (37 + 49) * 13 / 2), s.value);
    var rest = try c.suffix(gpa);
    defer rest.deinit(gpa);
    try expectTreeEquals(T, &rest, items[50..]);
    try testing.expect(c.isAtEnd());

    // Appending the pieces back together reproduces the middle of the tree.
    var joined = mid.clone();
    defer joined.deinit(gpa);
    try joined.append(gpa, rest.clone());
    var expected: [100]TestItem = undefined;
    @memcpy(expected[0..27], items[10..37]);
    @memcpy(expected[27..77], items[50..]);
    try expectTreeEquals(T, &joined, expected[0..77]);
    // The originals are untouched.
    try expectTreeEquals(T, &mid, items[10..37]);
    try expectTreeEquals(T, &rest, items[50..]);
    try expectTreeEquals(T, &t, items);
}

test "updateLast copies on write" {
    const gpa = testing.allocator;
    const T = testTree(2);
    var buf: [20]TestItem = undefined;
    var values: [20]u32 = undefined;
    for (&values, 0..) |*v, i| v.* = @intCast(i);
    const items = itemsOf(&values, &buf);
    var t = try T.fromSlice(gpa, items);
    defer t.deinit(gpa);
    var snapshot = t.clone();
    defer snapshot.deinit(gpa);

    const add = struct {
        fn f(amount: u32, it: *TestItem) void {
            it.value += amount;
        }
    }.f;
    try t.updateLast(gpa, @as(u32, 100), add);
    try testing.expectEqual(@as(u32, 119), t.last().?.value);
    try testing.expectEqual(@as(u32, 119), t.summary().max);
    try t.checkInvariants();
    try expectTreeEquals(T, &snapshot, items);

    var empty = T.init();
    try empty.updateLast(gpa, @as(u32, 1), add);
    try testing.expect(empty.isEmpty());
}

test "updateAt mutates the seeked item and copies on write" {
    const gpa = testing.allocator;
    const T = testTree(2);
    var buf: [40]TestItem = undefined;
    var values: [40]u32 = undefined;
    for (&values, 0..) |*v, i| v.* = @intCast(i);
    const items = itemsOf(&values, &buf);
    var t = try T.fromSlice(gpa, items);
    defer t.deinit(gpa);
    var snapshot = t.clone();
    defer snapshot.deinit(gpa);

    const Ctx = struct { amount: u32, start: ?usize = null };
    const add = struct {
        fn f(ctx: *Ctx, it: *TestItem, start: usize) bool {
            ctx.start = start;
            if (ctx.amount == 0) return false;
            it.value += ctx.amount;
            return true;
        }
    }.f;
    var model: [40]TestItem = undefined;
    @memcpy(&model, items);
    // Item count is the `usize` dimension: target 10 with .left lands on
    // item 9 (whose end is 10), with .right on item 10.
    var ctx: Ctx = .{ .amount = 100 };
    try testing.expect(try t.updateAt(gpa, usize, &@as(usize, 10), .left, &ctx, add));
    try testing.expectEqual(@as(?usize, 9), ctx.start);
    model[9].value += 100;
    ctx = .{ .amount = 1000 };
    try testing.expect(try t.updateAt(gpa, usize, &@as(usize, 10), .right, &ctx, add));
    try testing.expectEqual(@as(?usize, 10), ctx.start);
    model[10].value += 1000;
    // Seeking by a custom target over another dimension (the running sum
    // reaches 1 at the end of item 1).
    const addBySum = struct {
        fn f(amount: u32, it: *TestItem, start: Sum) bool {
            std.debug.assert(start.value == 0);
            it.value += amount;
            return true;
        }
    }.f;
    try testing.expect(try t.updateAt(gpa, Sum, &SumAtLeast{ .n = 1 }, .left, @as(u32, 5), addBySum));
    model[1].value += 5;
    try expectTreeEquals(T, &t, &model);
    try testing.expectEqual(@as(u32, 1010), t.summary().max);
    // Declined, or past the end: nothing changes.
    ctx = .{ .amount = 0 };
    try testing.expect(!try t.updateAt(gpa, usize, &@as(usize, 3), .right, &ctx, add));
    ctx = .{ .amount = 1 };
    try testing.expect(!try t.updateAt(gpa, usize, &@as(usize, 40), .right, &ctx, add));
    try expectTreeEquals(T, &t, &model);
    try expectTreeEquals(T, &snapshot, items);

    var empty = T.init();
    try testing.expect(!try empty.updateAt(gpa, usize, &@as(usize, 0), .left, &ctx, add));
}

test "append trees of different heights" {
    const gpa = testing.allocator;
    const T = testTree(2);
    var buf: [600]TestItem = undefined;
    var values: [600]u32 = undefined;
    for (&values, 0..) |*v, i| v.* = @intCast(i);
    const items = itemsOf(&values, &buf);
    const splits = [_]usize{ 0, 1, 2, 3, 5, 9, 17, 40, 100, 300, 599, 600 };
    for (splits) |k| {
        var a = try T.fromSlice(gpa, items[0..k]);
        defer a.deinit(gpa);
        const b = try T.fromSlice(gpa, items[k..]);
        var a_snapshot = a.clone();
        defer a_snapshot.deinit(gpa);
        try a.append(gpa, b);
        try expectTreeEquals(T, &a, items);
        try expectTreeEquals(T, &a_snapshot, items[0..k]);
    }
    // Self-append through a clone.
    var t = try T.fromSlice(gpa, items[0..50]);
    defer t.deinit(gpa);
    try t.append(gpa, t.clone());
    var expected: [100]TestItem = undefined;
    @memcpy(expected[0..50], items[0..50]);
    @memcpy(expected[50..100], items[0..50]);
    try expectTreeEquals(T, &t, &expected);
}

fn modelSeek(model: []const TestItem, target: u64, bias: Bias) usize {
    var pos: u64 = 0;
    for (model, 0..) |it, i| {
        const item_end = pos + it.value;
        const order = std.math.order(target, item_end);
        if (!(order == .gt or (order == .eq and bias == .right))) return i;
        pos = item_end;
    }
    return model.len;
}

fn modelSum(model: []const TestItem) u64 {
    var s: u64 = 0;
    for (model) |it| s += it.value;
    return s;
}

fn randomizedTest(comptime b: usize, seed: u64) !void {
    const gpa = testing.allocator;
    const T = testTree(b);
    var prng = std.Random.DefaultPrng.init(seed);
    const rand = prng.random();

    var tree = T.init();
    defer tree.deinit(gpa);
    var model: std.ArrayList(TestItem) = .empty;
    defer model.deinit(gpa);

    const Snapshot = struct { tree: T, items: []TestItem };
    var snapshots: std.ArrayList(Snapshot) = .empty;
    defer {
        for (snapshots.items) |*s| {
            s.tree.deinit(gpa);
            gpa.free(s.items);
        }
        snapshots.deinit(gpa);
    }

    var scratch: std.ArrayList(TestItem) = .empty;
    defer scratch.deinit(gpa);

    for (0..1500) |_| {
        const op = rand.uintLessThan(u32, 10);
        switch (op) {
            0, 1 => {
                const it: TestItem = .{ .value = rand.uintLessThan(u32, 5) };
                try tree.push(gpa, it);
                try model.append(gpa, it);
            },
            2 => {
                scratch.clearRetainingCapacity();
                for (0..rand.uintLessThan(usize, 40)) |_| try scratch.append(gpa, .{ .value = rand.uintLessThan(u32, 5) });
                try tree.extend(gpa, scratch.items);
                try model.appendSlice(gpa, scratch.items);
            },
            3 => {
                // Append a whole random tree (sometimes much taller).
                scratch.clearRetainingCapacity();
                for (0..rand.uintLessThan(usize, if (rand.boolean()) 10 else 300)) |_| try scratch.append(gpa, .{ .value = rand.uintLessThan(u32, 5) });
                const other = try T.fromSlice(gpa, scratch.items);
                try tree.append(gpa, other);
                try model.appendSlice(gpa, scratch.items);
            },
            4, 5 => {
                // Splice: tree = tree[0..a] ++ new ++ tree[b..], via cursor slices.
                const n = model.items.len;
                const lo = rand.uintAtMost(usize, n);
                const hi = rand.intRangeAtMost(usize, lo, n);
                scratch.clearRetainingCapacity();
                for (0..rand.uintLessThan(usize, 8)) |_| try scratch.append(gpa, .{ .value = rand.uintLessThan(u32, 5) });

                // A left-biased slice to count `lo` stops on item lo-1 (whose
                // end is exactly `lo`), so it excludes that item.
                const bias: Bias = if (rand.boolean()) .right else .left;
                const cut = if (bias == .left and lo > 0) lo - 1 else lo;
                var c = tree.cursor(usize);
                var result = try c.slice(gpa, &lo, bias);
                errdefer result.deinit(gpa);
                try testing.expectEqual(cut, c.start());
                _ = c.seekForward(&hi, .right);
                try result.extend(gpa, scratch.items);
                try result.append(gpa, try c.suffix(gpa));
                tree.deinit(gpa);
                tree = result;
                try model.replaceRange(gpa, cut, hi - cut, scratch.items);
            },
            6 => {
                // Keep a snapshot to verify persistence at the end.
                if (snapshots.items.len < 30) {
                    try snapshots.append(gpa, .{ .tree = tree.clone(), .items = try gpa.dupe(TestItem, model.items) });
                }
            },
            7 => {
                if (model.items.len > 0) {
                    const add = struct {
                        fn f(v: u32, it: *TestItem) void {
                            it.value = v;
                        }
                    }.f;
                    const v = rand.uintLessThan(u32, 5);
                    try tree.updateLast(gpa, v, add);
                    model.items[model.items.len - 1].value = v;
                }
            },
            else => {
                // Seek checks in both biases, by Sum and by count.
                const total = modelSum(model.items);
                for (0..4) |_| {
                    const target = rand.uintAtMost(u64, total + 1);
                    const bias: Bias = if (rand.boolean()) .left else .right;
                    var c = tree.cursor(Dims2(usize, Sum));
                    const hit = c.seek(&Sum{ .value = target }, bias);
                    const idx = modelSeek(model.items, target, bias);
                    try testing.expectEqual(idx, c.start().a);
                    try testing.expectEqual(modelSum(model.items[0..idx]), c.start().b.value);
                    if (idx < model.items.len) {
                        try testing.expectEqual(model.items[idx].value, c.item().?.value);
                    } else {
                        try testing.expect(c.item() == null);
                    }
                    const expect_hit = switch (bias) {
                        .left => target == c.end().b.value,
                        .right => target == c.start().b.value,
                    };
                    try testing.expectEqual(expect_hit, hit);

                    // seekForward to a later target agrees with a fresh seek.
                    const target2 = rand.intRangeAtMost(u64, @min(target, total + 1), total + 1);
                    _ = c.seekForward(&Sum{ .value = target2 }, bias);
                    try testing.expectEqual(modelSeek(model.items, target2, bias), c.start().a);

                    // Random walk with next/prev; -1 means "before the start".
                    const len_i: isize = @intCast(model.items.len);
                    var pos: isize = @intCast(c.start().a);
                    for (0..8) |_| {
                        if (rand.boolean()) {
                            c.next();
                            if (pos < len_i) pos += 1;
                        } else {
                            c.prev();
                            if (pos > -1 and !(pos == len_i and len_i == 0)) pos -= 1;
                        }
                        if (pos == -1) {
                            try testing.expect(c.item() == null and !c.isAtEnd());
                            try testing.expectEqual(@as(usize, 0), c.start().a);
                        } else {
                            const p: usize = @intCast(pos);
                            try testing.expectEqual(p, c.start().a);
                            try testing.expectEqual(modelSum(model.items[0..p]), c.start().b.value);
                            if (p == model.items.len) {
                                try testing.expect(c.isAtEnd() and c.item() == null);
                            } else {
                                try testing.expectEqual(model.items[p].value, c.item().?.value);
                                const nxt = c.nextItem();
                                const prv = c.prevItem();
                                try testing.expectEqual(p + 1 == model.items.len, nxt == null);
                                try testing.expectEqual(p == 0, prv == null);
                                if (nxt) |x| try testing.expectEqual(model.items[p + 1].value, x.value);
                                if (prv) |x| try testing.expectEqual(model.items[p - 1].value, x.value);
                            }
                        }
                    }

                    // summary() between two count positions.
                    const n = model.items.len;
                    const lo = rand.uintAtMost(usize, n);
                    const hi = rand.intRangeAtMost(usize, lo, n);
                    var c2 = tree.cursor(usize);
                    _ = c2.seek(&lo, .right);
                    const s = c2.summary(Sum, &hi, .right);
                    try testing.expectEqual(modelSum(model.items[lo..hi]), s.value);
                    try testing.expectEqual(hi, c2.start());

                    // slice() between them.
                    var c3 = tree.cursor(usize);
                    _ = c3.seek(&lo, .right);
                    var sl = try c3.slice(gpa, &hi, .right);
                    defer sl.deinit(gpa);
                    try expectTreeEquals(T, &sl, model.items[lo..hi]);
                }
            },
        }
        try tree.checkInvariants();
        try testing.expectEqual(model.items.len, tree.extent(usize));
    }
    try expectTreeEquals(T, &tree, model.items);

    // Full forward and backward walks.
    var c = tree.cursor(usize);
    for (model.items, 0..) |it, i| {
        try testing.expectEqual(it.value, c.item().?.value);
        try testing.expectEqual(i, c.start());
        c.next();
    }
    try testing.expect(c.isAtEnd());
    var i = model.items.len;
    while (i > 0) {
        i -= 1;
        c.prev();
        try testing.expectEqual(model.items[i].value, c.item().?.value);
        try testing.expectEqual(i, c.start());
    }

    for (snapshots.items) |*s| try expectTreeEquals(T, &s.tree, s.items);
}

test "randomized against ArrayList model (base 2)" {
    for ([_]u64{ 1, 2, 3, 42 }) |seed| try randomizedTest(2, seed);
}

test "randomized against ArrayList model (base 3)" {
    for ([_]u64{ 7, 99 }) |seed| try randomizedTest(3, seed);
}

test "randomized against ArrayList model (base 6)" {
    for ([_]u64{ 5, 1234 }) |seed| try randomizedTest(6, seed);
}

fn setValue(v: u32, it: *TestItem) void {
    it.value = v;
}

fn allocationFailureScenario(gpa: Allocator) !void {
    const T = testTree(2);
    var buf: [200]TestItem = undefined;
    var values: [200]u32 = undefined;
    for (&values, 0..) |*v, i| v.* = @intCast(i);
    const items = itemsOf(&values, &buf);

    var a = try T.fromSlice(gpa, items[0..150]);
    defer a.deinit(gpa);
    var snapshot = a.clone();
    defer snapshot.deinit(gpa);
    try a.append(gpa, try T.fromSlice(gpa, items[150..]));
    var small = try T.fromSlice(gpa, items[0..3]);
    defer small.deinit(gpa);
    try small.append(gpa, a.clone()); // grafts onto the left spine of a shared tree
    for (items[0..20]) |it| try a.push(gpa, it);
    try a.updateLast(gpa, @as(u32, 5), setValue);
    var c = a.cursor(usize);
    _ = c.seek(&@as(usize, 17), .left);
    var middle = try c.slice(gpa, &@as(usize, 120), .right);
    defer middle.deinit(gpa);
    var rest = try c.suffix(gpa);
    defer rest.deinit(gpa);
    try middle.append(gpa, rest.clone());
    try snapshot.checkInvariants();
}

test "no leaks when allocations fail" {
    try testing.checkAllAllocationFailures(testing.allocator, allocationFailureScenario, .{});
}

test "clones can be shared across threads" {
    const gpa = testing.allocator;
    const T = testTree(2);
    var buf: [500]TestItem = undefined;
    var values: [500]u32 = undefined;
    for (&values, 0..) |*v, i| v.* = @intCast(i % 7);
    var t = try T.fromSlice(gpa, itemsOf(&values, &buf));
    defer t.deinit(gpa);

    const Worker = struct {
        fn run(snapshot: T, out: *u64) void {
            var s = snapshot;
            defer s.deinit(testing.allocator);
            var total: u64 = 0;
            var it = s.iter();
            while (it.next()) |x| total += x.value;
            out.* = total;
        }
    };
    var results: [4]u64 = undefined;
    var threads: [4]std.Thread = undefined;
    for (&threads, &results) |*th, *r| th.* = try std.Thread.spawn(.{}, Worker.run, .{ t.clone(), r });
    // Mutate the original concurrently; snapshots must not observe it.
    for (0..100) |_| try t.push(gpa, .{ .value = 1000 });
    for (threads) |th| th.join();
    for (results) |r| try testing.expectEqual(t.summary().sum - 100 * 1000, r);
}
