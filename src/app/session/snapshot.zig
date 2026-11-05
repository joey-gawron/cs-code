//! A workspace as a saved session and back: open files in tab order with
//! their groups, carets, scroll and folds, the split layout, the side bar,
//! and the text of every unsaved buffer (hot exit), so a session that quits
//! or crashes comes back as it was. Terminals, language servers and undo
//! history are not saved: the first two live on in the session's process, and
//! they and the history start afresh after a real restart.

const std = @import("std");
const store_mod = @import("store.zig");
const workspace_mod = @import("../workspace/workspace.zig");
const groups = @import("../workspace/groups.zig");
const editor_mod = @import("../editor/editor.zig");
const fold_map = @import("../../model/text/fold_map.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Selection = editor_mod.Selection;
const Doc = store_mod.Doc;
const max_groups = workspace_mod.max_groups;
/// groups.Layout's node capacity.
const layout_cap = 3 * max_groups;
const log = std.log.scoped(.session);

/// A buffer's unsaved text to put in the backups folder as `file`.
pub const Backup = struct { file: []const u8, text: []u8 };

pub const Capture = struct {
    arena: std.heap.ArenaAllocator,
    doc: Doc,
    backups: []Backup,

    pub fn deinit(self: *Capture, gpa: Allocator) void {
        for (self.backups) |b| gpa.free(b.text);
        gpa.free(self.backups);
        self.arena.deinit();
    }
};

/// Tabs that hold a real document: not the agenda, a terminal, a diff or a
/// commit message, and not a generated buffer.
fn savable(t: workspace_mod.Tab) bool {
    if (t.agenda != null or t.terminal != null or t.git_diff != null or t.git_commit != null) return false;
    const b = t.editor.buffer;
    return b.display_name == null and !b.read_only;
}

/// Captures `ws`. The unsaved texts come back as `backups`, for the caller to
/// write (none when `keep_unsaved` is off: they were discarded).
pub fn capture(gpa: Allocator, ws: *Workspace, name_unnamed: bool, keep_unsaved: bool) !Capture {
    var arena: std.heap.ArenaAllocator = .init(gpa);
    errdefer arena.deinit();
    const a = arena.allocator();
    var backups: std.ArrayList(Backup) = .empty;
    errdefer {
        for (backups.items) |b| gpa.free(b.text);
        backups.deinit(gpa);
    }

    var tabs: std.ArrayList(store_mod.TabDoc) = .empty;
    // Doc index of each workspace tab, -1 for ones left out.
    const index_of = try a.alloc(i32, ws.tabs.items.len);
    var seen: std.AutoHashMapUnmanaged(u32, void) = .empty;
    for (ws.tabs.items, index_of) |t, *idx| {
        idx.* = -1;
        if (!savable(t)) continue;
        const b = t.editor.buffer;
        // A second view of one buffer is not saved twice.
        if ((try seen.getOrPut(a, b.id)).found_existing) continue;
        const ed = t.editor;
        var doc: store_mod.TabDoc = .{ .group = t.group, .transient = t.transient };
        if (b.path) |p| {
            doc.path = try a.dupe(u8, p);
        } else if (b.len() == 0 or !keep_unsaved) continue;
        if (keep_unsaved and (b.isDirty() or b.path == null)) {
            const text = try ed.contents(gpa);
            errdefer gpa.free(text);
            const file = try std.fmt.allocPrint(a, "{d}.txt", .{b.id});
            try backups.append(gpa, .{ .file = file, .text = text });
            doc.backup = file;
        }
        const sels = try a.alloc(store_mod.SelDoc, ed.selections.items.len);
        for (ed.selections.items, sels) |s, *out| out.* = .{ .anchor = s.anchor, .head = s.head };
        doc.selections = sels;
        doc.primary = ed.primary;
        doc.scroll_x = ed.scroll_x;
        doc.scroll_y = ed.scroll_y;
        const folds = try a.alloc([2]u32, ed.display.folds.folds.items.len);
        for (ed.display.folds.folds.items, folds) |f, *out| out.* = fold_map.FoldMap.foldRows(b, f);
        doc.folds = folds;
        idx.* = @intCast(tabs.items.len);
        try tabs.append(a, doc);
    }

    const n_groups = ws.groupCount();
    const active = try a.alloc(i32, n_groups);
    for (active, 0..) |*slot, g| {
        slot.* = -1;
        if (ws.group_active[g]) |ed| if (ws.tabIndexOf(ed)) |i| {
            slot.* = index_of[i];
        };
    }

    const layout = try a.alloc(store_mod.NodeDoc, ws.group_layout.len);
    for (ws.group_layout.nodes[0..ws.group_layout.len], layout) |node, *out| {
        out.* = .{
            .group = node.group,
            .across = node.across,
            .children = blk: {
                const kids = try a.alloc(u32, node.n);
                for (node.children[0..node.n], kids) |k, *o| o.* = k;
                break :blk kids;
            },
            .shares = try a.dupe(f32, node.shares[0..node.n]),
        };
    }

    return .{
        .arena = arena,
        .doc = .{
            .last_used = store_mod.nowSeconds(),
            .unnamed = name_unnamed,
            .folder = if (ws.project) |p| try a.dupe(u8, p.root) else null,
            .tabs = tabs.items,
            .active = active,
            .focused_group = ws.focused_group,
            .layout = layout,
            .sidebar_visible = ws.sidebar_visible,
            .sidebar_width = ws.sidebar_width,
        },
        .backups = try backups.toOwnedSlice(gpa),
    };
}

/// Whether `nodes` is a split tree `Layout` can hold: node 0 the root,
/// children after their parents, at most `max_groups` distinct groups.
pub fn layoutValid(nodes: []const store_mod.NodeDoc) bool {
    if (nodes.len == 0 or nodes.len > layout_cap) return false;
    var seen: [max_groups]bool = @splat(false);
    var leaves: usize = 0;
    for (nodes, 0..) |n, i| {
        if (n.group) |g| {
            if (g >= max_groups or seen[g] or n.children.len != 0) return false;
            seen[g] = true;
            leaves += 1;
        } else {
            if (n.children.len == 0 or n.children.len > max_groups or n.shares.len != n.children.len) return false;
            var sum: f32 = 0;
            for (n.children, n.shares) |c, s| {
                if (c <= i or c >= nodes.len or !(s > 0)) return false;
                sum += s;
            }
            if (@abs(sum - 1) > 0.01) return false;
        }
    }
    // Every node but the root has exactly one parent.
    var parents: [layout_cap]u8 = @splat(0);
    for (nodes) |n| {
        for (n.children) |c| parents[c] += 1;
    }
    for (nodes[1..], 1..) |_, i| if (parents[i] != 1) return false;
    if (parents[0] != 0) return false;
    return leaves >= 1 and leaves <= max_groups;
}

pub const RestoreNotes = struct {
    files_missing: usize = 0,
    restored: usize = 0,
};

/// Opens a session's tabs in `ws` (which should hold nothing yet). Files that
/// are gone and have no backup are skipped; a buffer with a backup comes back
/// with that text, unsaved.
pub fn restore(ws: *Workspace, store: *const store_mod.Store, name: []const u8, doc: Doc) RestoreNotes {
    var notes: RestoreNotes = .{};
    if (doc.folder) |f| if (isDir(ws, f)) {
        ws.openProject(f, true) catch |err| log.warn("cannot reopen folder {s}: {t}", .{ f, err });
    };
    if (layoutValid(doc.layout)) {
        var l: groups.Layout = .single;
        l.len = @intCast(doc.layout.len);
        for (doc.layout, l.nodes[0..doc.layout.len]) |n, *out| {
            out.* = .{ .group = n.group, .across = n.across, .n = @intCast(n.children.len) };
            for (n.children, n.shares, 0..) |c, s, k| {
                out.children[k] = @intCast(c);
                out.shares[k] = s;
            }
        }
        ws.group_layout = l;
    }
    const n_groups = ws.groupCount();

    // The editor opened for each doc tab (null: skipped).
    var editors: std.ArrayList(?*Editor) = .empty;
    defer editors.deinit(ws.gpa);
    for (doc.tabs) |t| {
        const ed = restoreTab(ws, store, name, t, n_groups, &notes);
        editors.append(ws.gpa, ed) catch {};
        if (ed != null) notes.restored += 1;
    }

    for (doc.active, 0..) |idx, g| {
        if (g >= n_groups or idx < 0 or idx >= editors.items.len) continue;
        if (editors.items[@intCast(idx)]) |ed| ws.group_active[g] = ed;
    }
    ws.focused_group = if (doc.focused_group < n_groups) doc.focused_group else 0;
    if (ws.group_active[ws.focused_group]) |ed| {
        if (ws.tabIndexOf(ed)) |i| ws.activateTab(i);
    }
    ws.sidebar_visible = doc.sidebar_visible;
    ws.sidebar_width = @max(120, doc.sidebar_width);
    ws.dirty = true;
    return notes;
}

fn isDir(ws: *Workspace, path: []const u8) bool {
    const st = std.Io.Dir.cwd().statFile(ws.io, path, .{}) catch return false;
    return st.kind == .directory;
}

fn restoreTab(ws: *Workspace, store: *const store_mod.Store, name: []const u8, t: store_mod.TabDoc, n_groups: u8, notes: *RestoreNotes) ?*Editor {
    const backup: ?[]u8 = if (t.backup) |f| store.readBackup(name, f) else null;
    defer if (backup) |b| ws.gpa.free(b);

    var ed: *Editor = undefined;
    if (t.path) |p| {
        const exists = blk: {
            std.Io.Dir.cwd().access(ws.io, p, .{}) catch break :blk false;
            break :blk true;
        } or std.mem.startsWith(u8, p, "ssh://");
        if (!exists and backup == null) {
            notes.files_missing += 1;
            return null;
        }
        ed = ws.openFile(p, t.transient) catch |err| {
            log.warn("cannot reopen {s}: {t}", .{ p, err });
            return null;
        };
        if (backup) |b| ed.replaceContentsMinimal(b);
    } else {
        const b = backup orelse return null;
        ed = ws.newFile() catch return null;
        ed.setText(b, false);
        // setText made it undoable: a restored buffer starts with a clean history.
        ed.buffer.clearHistory();
    }
    if (ws.tabIndexOf(ed)) |i| ws.tabs.items[i].group = if (t.group < n_groups) t.group else 0;

    if (t.selections.len > 0) {
        var sels: std.ArrayList(Selection) = .empty;
        defer sels.deinit(ws.gpa);
        for (t.selections) |s| sels.append(ws.gpa, .range(s.anchor, s.head)) catch {};
        ed.setSelections(sels.items);
        if (t.primary < ed.selections.items.len) ed.primary = t.primary;
    }
    for (t.folds) |f| {
        if (f[1] < ed.buffer.lineCount() and f[0] < f[1]) ed.display.folds.fold(ws.gpa, ed.buffer, f[0], f[1]) catch {};
    }
    ed.scroll_x = t.scroll_x;
    ed.scroll_y = t.scroll_y;
    return ed;
}
