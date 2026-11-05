//! Keeps a session's file up to date. The loop calls `tick` once per
//! iteration: a cheap fingerprint of the workspace (tabs, buffer versions,
//! carets, scroll, folds, layout, side bar) says whether anything changed, and
//! a change is written at most 1.5 s later, even while typing (the timer is
//! armed once per save, never pushed out). `flush` writes at once: on quit,
//! on SIGTERM and when a client asks.
//!
//! Unsaved text is saved as hot-exit backups. A quit the user decided on in
//! the window (after its save prompts) writes the layout without them, so
//! "Don't Save" is honoured; every other end (SIGTERM, `quit`, a lost window
//! connection) keeps them.

const std = @import("std");
const store_mod = @import("store.zig");
const snapshot = @import("snapshot.zig");
const workspace_mod = @import("../workspace/workspace.zig");
const platform = @import("../../framework/platform/platform.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const log = std.log.scoped(.session);

pub const delay_ms: i64 = 1500;

/// Set by the SIGTERM/SIGHUP handler; the loop quits when it sees it.
pub var terminate: std.atomic.Value(bool) = .init(false);

fn onSignal(_: std.posix.SIG) callconv(.c) void {
    terminate.store(true, .release);
}

pub fn installSignalHandlers() void {
    const act: std.posix.Sigaction = .{ .handler = .{ .handler = onSignal }, .mask = std.posix.sigemptyset(), .flags = 0 };
    std.posix.sigaction(std.posix.SIG.TERM, &act, null);
    std.posix.sigaction(std.posix.SIG.HUP, &act, null);
}

pub const Autosave = struct {
    gpa: Allocator,
    store: store_mod.Store,
    /// The session's name; owned.
    name: []u8,
    unnamed: bool,
    /// A file from a newer version is in the way: nothing is written.
    blocked: bool = false,
    last_print: u64 = 0,
    /// Monotonic ms when the change waiting to be written was first seen; 0 for none.
    pending_since: i64 = 0,
    /// Hash of each backup's text as last written, by file name; owned keys.
    written: std.StringHashMapUnmanaged(u64) = .empty,

    pub fn init(gpa: Allocator, store: store_mod.Store, name: []const u8, unnamed: bool) !Autosave {
        return .{ .gpa = gpa, .store = store, .name = try gpa.dupe(u8, name), .unnamed = unnamed };
    }

    pub fn deinit(self: *Autosave) void {
        var it = self.written.keyIterator();
        while (it.next()) |k| self.gpa.free(k.*);
        self.written.deinit(self.gpa);
        self.store.deinit();
        self.gpa.free(self.name);
    }

    /// Renames the session (its files are the caller's business).
    pub fn setName(self: *Autosave, new: []const u8) !void {
        const copy = try self.gpa.dupe(u8, new);
        self.gpa.free(self.name);
        self.name = copy;
        // The backups moved with the folder; their names are unchanged.
    }

    /// Starts from the state just restored: nothing to write until it changes.
    pub fn settle(self: *Autosave, ws: *Workspace) void {
        self.last_print = fingerprint(ws);
        self.pending_since = 0;
    }

    pub fn tick(self: *Autosave, ws: *Workspace) void {
        if (self.blocked) return;
        const fp = fingerprint(ws);
        const now = platform.nowMs();
        if (fp != self.last_print) {
            self.last_print = fp;
            if (self.pending_since == 0) self.pending_since = now;
        }
        if (self.pending_since != 0 and now - self.pending_since >= delay_ms) self.write(ws, true);
    }

    /// Milliseconds until the pending change is due, if there is one.
    pub fn nextDeadlineMs(self: *const Autosave) ?i32 {
        if (self.pending_since == 0) return null;
        const left = delay_ms - (platform.nowMs() - self.pending_since);
        return @intCast(@max(1, left));
    }

    pub fn flush(self: *Autosave, ws: *Workspace, keep_unsaved: bool) void {
        if (self.blocked) return;
        self.write(ws, keep_unsaved);
    }

    fn write(self: *Autosave, ws: *Workspace, keep_unsaved: bool) void {
        self.pending_since = 0;
        var cap = snapshot.capture(self.gpa, ws, self.unnamed, keep_unsaved) catch |err| return log.warn("cannot capture session {s}: {t}", .{ self.name, err });
        defer cap.deinit(self.gpa);
        var keep: std.ArrayList([]const u8) = .empty;
        defer keep.deinit(self.gpa);
        for (cap.backups) |b| {
            keep.append(self.gpa, b.file) catch {};
            const h = std.hash.Wyhash.hash(0, b.text);
            if (self.written.get(b.file)) |prev| if (prev == h) continue;
            self.store.writeBackup(self.name, b.file, b.text) catch |err| {
                log.warn("cannot back up {s}: {t}", .{ b.file, err });
                continue;
            };
            if (self.written.getEntry(b.file)) |e| {
                e.value_ptr.* = h;
            } else if (self.gpa.dupe(u8, b.file)) |k| {
                self.written.put(self.gpa, k, h) catch self.gpa.free(k);
            } else |_| {}
        }
        self.store.save(self.name, cap.doc) catch |err| return log.warn("cannot save session {s}: {t}", .{ self.name, err });
        self.store.pruneBackups(self.name, keep.items);
        // A backup deleted by the prune must be written again if it comes back.
        var drop: std.ArrayList([]const u8) = .empty;
        defer drop.deinit(self.gpa);
        var it = self.written.keyIterator();
        while (it.next()) |k| {
            const wanted = for (keep.items) |f| {
                if (std.mem.eql(u8, f, k.*)) break true;
            } else false;
            if (!wanted) drop.append(self.gpa, k.*) catch {};
        }
        for (drop.items) |k| if (self.written.fetchRemove(k)) |kv| self.gpa.free(kv.key);
    }
};

/// What changes when something a session holds changes. Not a checksum of
/// the text: buffer versions stand in for it.
pub fn fingerprint(ws: *Workspace) u64 {
    var h = std.hash.Wyhash.init(0);
    const add = struct {
        fn f(h_: *std.hash.Wyhash, v: anytype) void {
            h_.update(std.mem.asBytes(&v));
        }
    }.f;
    add(&h, ws.tabs.items.len);
    for (ws.tabs.items) |t| {
        add(&h, @intFromPtr(t.editor));
        add(&h, t.group);
        add(&h, t.transient);
        add(&h, t.editor.buffer.version);
        add(&h, t.editor.buffer.saved_version);
        add(&h, t.editor.scroll_y);
        add(&h, t.editor.scroll_x);
        add(&h, t.editor.selections.items.len);
        for (t.editor.selections.items) |s| {
            add(&h, s.anchor);
            add(&h, s.head);
        }
        add(&h, t.editor.display.folds.folds.items.len);
        if (t.editor.buffer.path) |p| h.update(p);
    }
    for (ws.group_active) |g| add(&h, @intFromPtr(g));
    add(&h, ws.focused_group);
    add(&h, ws.group_layout.len);
    for (ws.group_layout.nodes[0..ws.group_layout.len]) |n| {
        add(&h, n.n);
        add(&h, n.across);
        add(&h, n.shares);
    }
    add(&h, ws.sidebar_visible);
    add(&h, ws.sidebar_width);
    if (ws.project) |p| h.update(p.root);
    return h.final();
}

pub const Attached = struct {
    autosave: Autosave,
    notes: snapshot.RestoreNotes = .{},
    /// "from backup", "newer": what the user should be told.
    message: ?[]const u8 = null,
};

/// Restores session `name` into `ws` if it was saved, and returns the
/// autosaver for it. Null when there is no data folder.
pub fn attach(gpa: Allocator, io: std.Io, ws: *Workspace, name: []const u8, unnamed: bool) ?Attached {
    var store = store_mod.Store.open(gpa, io) orelse return null;
    var attached: Attached = .{ .autosave = Autosave.init(gpa, store, name, unnamed) catch {
        store.deinit();
        return null;
    } };
    switch (store.load(name)) {
        .none => {},
        .newer => {
            attached.autosave.blocked = true;
            attached.message = "This session was saved by a newer version: it is not loaded and will not be overwritten";
        },
        .loaded => |l| {
            var loaded = l;
            defer loaded.deinit();
            attached.notes = snapshot.restore(ws, &store, name, loaded.parsed.value);
            if (loaded.from_backup) attached.message = "The session file was damaged; the previous version was restored";
        },
    }
    attached.autosave.settle(ws);
    return attached;
}
