//! Pages: the Welcome page shown when no folder is open, and the New Project
//! page (File ▸ New Project…). A page fills the area of the editors under a
//! tab strip of its own, like a document tab in cs-code; closing the pages
//! brings the editors back. This module also owns File ▸ Open Recent, Close
//! Folder, Clone Repository and the native Open Folder panel.
//!
//! Like terminal_ui.zig it is glue between the workspace and one feature:
//! the workspace calls `paint`, `handleEvent`, `perform` and the two
//! `confirm…` hooks, and everything else lives here.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const keymap_mod = @import("../settings/keymap.zig");
const recent = @import("../settings/recent.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const ui = @import("../../framework/ui/ui.zig");
const folder_picker = @import("../../framework/platform/folder_picker.zig");
const templates = @import("../../model/project/templates.zig");
const git = @import("../../model/git/git.zig");
const quick_panel = @import("quick_panel.zig");
const Executor = @import("../../framework/util/executor.zig").Executor;

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Action = keymap_mod.Action;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const FontId = text_mod.FontId;

pub const Page = enum { welcome, new_project };

/// Prompt and list tags this module owns (workspace.zig's and remote_ui's
/// are below 400).
pub const PromptKind = enum(u32) { clone_url = 400 };
pub const ListKind = enum(u32) { open_recent = 410, open_created };

pub const State = struct {
    /// The Welcome tab was opened by hand (else it shows by itself while no
    /// folder and no file is open).
    welcome_open: bool = false,
    /// The Welcome tab was closed while nothing was open.
    welcome_closed: bool = false,
    shown: Page = .welcome,
    new: ?*NewProject = null,
    recent: recent.List = .{},
    recent_loaded: bool = false,
    hits: std.ArrayList(Hit) = .empty,
    clone_job: ?*CloneJob = null,
    /// A created project waiting for the answer to "open it here?".
    created_dir: ?[]u8 = null,
    created_main: ?[]u8 = null,
};

const Click = union(enum) {
    tab: Page,
    close_tab: Page,
    link: Link,
    recent: usize,
    generator: usize,
    field_name,
    field_location,
    browse,
    dropdown: u8,
    dropdown_item: struct { option: u8, choice: u8 },
    git_box,
    create,
    cancel,
    none,
};

const Link = enum { new_project, open_folder, open_file, new_file, clone, palette };

const Hit = struct { rect: Rect, click: Click };

// ---- Remembered between pages (for the life of the process) ---------------

var last_gen: usize = 0;
var last_choices: [templates.generators.len][templates.max_options]u8 = @splat(@splat(0));
var last_git: bool = true;

// ---- Text fields ----------------------------------------------------------

const Field = struct {
    text: std.ArrayList(u8) = .empty,
    caret: usize = 0,
    /// The whole text is selected: the next typing replaces it.
    all: bool = false,

    fn deinit(self: *Field, gpa: Allocator) void {
        self.text.deinit(gpa);
    }

    fn set(self: *Field, gpa: Allocator, s: []const u8) void {
        self.text.clearRetainingCapacity();
        self.text.appendSlice(gpa, s) catch {};
        self.caret = self.text.items.len;
    }

    fn insert(self: *Field, gpa: Allocator, s: []const u8) void {
        if (self.all) {
            self.text.clearRetainingCapacity();
            self.caret = 0;
            self.all = false;
        }
        for (s) |ch| if (ch == '\n' or ch == '\r' or ch < 32) return;
        self.text.insertSlice(gpa, self.caret, s) catch return;
        self.caret += s.len;
    }

    fn prev(self: *const Field) usize {
        var i = self.caret;
        if (i == 0) return 0;
        i -= 1;
        while (i > 0 and (self.text.items[i] & 0xC0) == 0x80) i -= 1;
        return i;
    }

    fn next(self: *const Field) usize {
        var i = self.caret;
        if (i >= self.text.items.len) return i;
        i += 1;
        while (i < self.text.items.len and (self.text.items[i] & 0xC0) == 0x80) i += 1;
        return i;
    }

    fn backspace(self: *Field) void {
        if (self.all) return self.clear();
        const p = self.prev();
        self.text.replaceRangeAssumeCapacity(p, self.caret - p, "");
        self.caret = p;
    }

    fn delete(self: *Field) void {
        if (self.all) return self.clear();
        const n = self.next();
        self.text.replaceRangeAssumeCapacity(self.caret, n - self.caret, "");
    }

    fn clear(self: *Field) void {
        self.text.clearRetainingCapacity();
        self.caret = 0;
        self.all = false;
    }

    fn slice(self: *const Field) []const u8 {
        return self.text.items;
    }
};

// ---- The New Project page --------------------------------------------------

const Focus = enum { name, location, browse, option, git, create, cancel };

pub const NewProject = struct {
    gen: usize,
    choices: [templates.generators.len][templates.max_options]u8,
    name: Field = .{},
    location: Field = .{},
    git: bool,
    focus: Focus = .name,
    focus_option: u8 = 0,
    dropdown: ?u8 = null,
    list_scroll: f32 = 0,
    name_error: ?[]const u8 = null,
    folder_error: ?[]const u8 = null,
    busy: bool = false,
    status: [300]u8 = undefined,
    status_len: usize = 0,
    status_is_error: bool = false,
    job: ?*CreateJob = null,

    fn deinit(self: *NewProject, gpa: Allocator) void {
        if (self.job) |j| j.ws = null;
        self.name.deinit(gpa);
        self.location.deinit(gpa);
        gpa.destroy(self);
    }

    fn setStatus(self: *NewProject, is_error: bool, comptime fmt: []const u8, args: anytype) void {
        self.status_is_error = is_error;
        self.status_len = (std.fmt.bufPrint(&self.status, fmt, args) catch @as([]u8, self.status[0..0])).len;
    }

    fn options(self: *const NewProject) []const templates.Option {
        return templates.generators[self.gen].options;
    }

    fn targetDir(self: *const NewProject, gpa: Allocator) ?[]u8 {
        const loc = (templates.expandLocation(gpa, std.mem.trim(u8, self.location.slice(), " ")) catch return null) orelse return null;
        defer gpa.free(loc);
        return std.fmt.allocPrint(gpa, "{s}/{s}", .{ std.mem.trimEnd(u8, loc, "/"), self.name.slice() }) catch null;
    }

    /// Re-checks the name and the folder after an edit or a change of generator.
    fn revalidate(self: *NewProject, ws: *Workspace) void {
        self.name_error = templates.validateName(self.gen, self.name.slice());
        self.folder_error = null;
        const loc = std.mem.trim(u8, self.location.slice(), " ");
        const abs = templates.expandLocation(ws.gpa, loc) catch null;
        if (abs == null) {
            self.folder_error = "Location must be an absolute path.";
            return;
        }
        ws.gpa.free(abs.?);
        if (self.name_error != null) return;
        const dir = self.targetDir(ws.gpa) orelse return;
        defer ws.gpa.free(dir);
        self.folder_error = switch (templates.folderState(ws.io, dir)) {
            .free, .empty => null,
            .not_empty => "That folder already exists and is not empty.",
            .not_a_folder => "A file with that name already exists.",
        };
    }

    fn canCreate(self: *const NewProject) bool {
        return !self.busy and self.name_error == null and self.folder_error == null;
    }

    fn activeField(self: *NewProject) ?*Field {
        return switch (self.focus) {
            .name => &self.name,
            .location => &self.location,
            else => null,
        };
    }

    /// Tab order: name, location, Browse, the options, the Git box, Create, Cancel.
    fn moveFocus(self: *NewProject, dir: i32) void {
        const nopt: i32 = @intCast(self.options().len);
        var idx: i32 = switch (self.focus) {
            .name => 0,
            .location => 1,
            .browse => 2,
            .option => 3 + self.focus_option,
            .git => 3 + nopt,
            .create => 4 + nopt,
            .cancel => 5 + nopt,
        };
        idx = @mod(idx + dir, 6 + nopt);
        if (idx == 0) {
            self.focus = .name;
        } else if (idx == 1) {
            self.focus = .location;
        } else if (idx == 2) {
            self.focus = .browse;
        } else if (idx < 3 + nopt) {
            self.focus = .option;
            self.focus_option = @intCast(idx - 3);
        } else if (idx == 3 + nopt) {
            self.focus = .git;
        } else if (idx == 4 + nopt) {
            self.focus = .create;
        } else self.focus = .cancel;
        if (self.activeField()) |f| {
            f.all = true;
            f.caret = f.text.items.len;
        }
    }

    /// The focused option's next or previous choice.
    fn step(self: *NewProject, dir: i32) void {
        const o = self.focus_option;
        const count: i32 = @intCast(self.options()[o].choices.len);
        const cur: i32 = self.choices[self.gen][o];
        self.choices[self.gen][o] = @intCast(@mod(cur + dir, count));
    }

    fn selectGenerator(self: *NewProject, dir: i32) void {
        const count: i32 = templates.generators.len;
        self.gen = @intCast(@mod(@as(i32, @intCast(self.gen)) + dir, count));
    }

    fn rememberChoices(self: *const NewProject) void {
        last_gen = self.gen;
        last_choices = self.choices;
        last_git = self.git;
    }
};

pub fn deinit(ws: *Workspace) void {
    const st = &ws.page;
    if (st.new) |n| n.deinit(ws.gpa);
    st.new = null;
    if (st.clone_job) |j| j.ws = null;
    st.recent.deinit(ws.gpa);
    st.hits.deinit(ws.gpa);
    if (st.created_dir) |d| ws.gpa.free(d);
    if (st.created_main) |d| ws.gpa.free(d);
}

// ---- Which pages show ------------------------------------------------------

/// Nothing worth showing is open: no tabs, or only empty untitled buffers.
fn nothingOpen(ws: *const Workspace) bool {
    for (ws.tabs.items) |t| {
        if (t.terminal != null or t.agenda != null or t.git_diff != null or t.git_commit != null) return false;
        if (t.editor.buffer.path != null or t.editor.buffer.len() > 0) return false;
    }
    return true;
}

fn welcomeShown(ws: *const Workspace) bool {
    const st = &ws.page;
    return st.welcome_open or (ws.project == null and nothingOpen(ws) and !st.welcome_closed);
}

pub fn visible(ws: *const Workspace) bool {
    return welcomeShown(ws) or ws.page.new != null;
}

fn current(ws: *const Workspace) Page {
    const st = &ws.page;
    if (st.new == null) return .welcome;
    if (!welcomeShown(ws)) return .new_project;
    return st.shown;
}

fn loadRecent(ws: *Workspace) void {
    if (ws.page.recent_loaded) return;
    ws.page.recent_loaded = true;
    ws.page.recent = recent.load(ws.gpa, ws.io);
}

/// Called when a folder becomes the project: it goes to the top of the recent list.
pub fn noteOpened(ws: *Workspace, path: []const u8) void {
    loadRecent(ws);
    ws.page.recent.push(ws.gpa, path) catch return;
    recent.save(ws.gpa, ws.io, &ws.page.recent) catch {};
    ws.page.welcome_closed = true;
    ws.page.welcome_open = false;
}

// ---- Actions ---------------------------------------------------------------

pub fn perform(ws: *Workspace, action: Action) void {
    switch (action) {
        .new_project => openNewProject(ws),
        .show_welcome => {
            ws.page.welcome_open = true;
            ws.page.shown = .welcome;
            ws.focus = .editor;
        },
        .open_recent => openRecentList(ws),
        .close_folder => closeFolder(ws),
        .add_folder_to_workspace => ws.setStatus("This window shows one folder; use File ▸ Open Folder to switch", .{}),
        .clone_repository => promptClone(ws),
        else => {},
    }
}

/// File ▸ Open Folder…: the system's panel where there is one.
pub fn openFolderDialog(ws: *Workspace) void {
    if (!folder_picker.available) return ws.promptOpenPath(.folder);
    var start_buf: [std.fs.max_path_bytes + 1]u8 = undefined;
    const start_src = if (ws.project) |p| p.root else "";
    const start = std.fmt.bufPrintSentinel(&start_buf, "{s}", .{start_src}, 0) catch return;
    const picked = folder_picker.pick(ws.gpa, start, "Open Folder") orelse return;
    defer ws.gpa.free(picked);
    ws.openFolder(picked);
}

fn closeFolder(ws: *Workspace) void {
    const p = ws.project orelse return;
    p.destroy();
    ws.project = null;
    ws.page.welcome_closed = false;
    ws.updateTitle();
}

fn openRecentList(ws: *Workspace) void {
    loadRecent(ws);
    const items = ws.page.recent.items.items;
    if (items.len == 0) return ws.setStatus("No recent folders", .{});
    const label = ws.gpa.dupe(u8, "Open Recent") catch return;
    ws.openList(.list, label, @intFromEnum(ListKind.open_recent), @as([]const []const u8, @ptrCast(items)));
}

fn promptClone(ws: *Workspace) void {
    ws.openPrompt("Repository URL:", @intFromEnum(PromptKind.clone_url), "");
}

pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (tag != @intFromEnum(PromptKind.clone_url)) return false;
    const url = std.mem.trim(u8, input, " ");
    if (url.len == 0) return true;
    startClone(ws, url);
    return true;
}

pub fn confirmList(ws: *Workspace, tag: u32, item: ?quick_panel.Item) bool {
    const choice: usize = if (item) |it| it.payload.value else 1;
    if (tag == @intFromEnum(ListKind.open_recent)) {
        if (item) |it| {
            const path = ws.gpa.dupe(u8, it.title) catch return true;
            defer ws.gpa.free(path);
            ws.openFolder(path);
        }
        return true;
    }
    if (tag == @intFromEnum(ListKind.open_created)) {
        const st = &ws.page;
        defer {
            if (st.created_dir) |d| ws.gpa.free(d);
            if (st.created_main) |d| ws.gpa.free(d);
            st.created_dir = null;
            st.created_main = null;
        }
        if (choice == 0) if (st.created_dir) |d| openCreated(ws, d, st.created_main orelse "");
        return true;
    }
    return false;
}

// ---- Opening the New Project page -------------------------------------------

fn wakeUi(ctx: *anyopaque) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    ws.dirty = true;
}

fn openNewProject(ws: *Workspace) void {
    const st = &ws.page;
    ws.focus = .editor;
    if (st.new != null) {
        st.shown = .new_project;
        return;
    }
    loadRecent(ws);
    templates.startToolChecks(ws.gpa, ws.io, ws.executor, .{ .ctx = ws, .run = wakeUi });
    const n = ws.gpa.create(NewProject) catch return;
    n.* = .{ .gen = last_gen, .choices = last_choices, .git = last_git };
    // The folder of the latest project's parent, else the home folder.
    var loc_buf: [std.fs.max_path_bytes]u8 = undefined;
    const location: []const u8 = blk: {
        if (st.recent.items.items.len > 0) if (std.fs.path.dirname(st.recent.items.items[0])) |d| break :blk d;
        if (ws.project) |p| if (std.fs.path.dirname(p.root)) |d| break :blk d;
        const home = std.c.getenv("HOME") orelse break :blk "/";
        break :blk std.fmt.bufPrint(&loc_buf, "{s}", .{std.mem.span(home)}) catch "/";
    };
    n.location.set(ws.gpa, location);
    const free = templates.freeName(ws.gpa, ws.io, location) catch null;
    if (free) |f| {
        n.name.set(ws.gpa, f);
        ws.gpa.free(f);
    } else n.name.set(ws.gpa, "untitled");
    n.name.all = true;
    n.revalidate(ws);
    st.new = n;
    st.shown = .new_project;
}

fn closePage(ws: *Workspace, page: Page) void {
    const st = &ws.page;
    switch (page) {
        .welcome => {
            st.welcome_open = false;
            st.welcome_closed = true;
            st.shown = .new_project;
        },
        .new_project => {
            if (st.new) |n| n.deinit(ws.gpa);
            st.new = null;
            st.shown = .welcome;
        },
    }
}

// ---- Creating --------------------------------------------------------------

const CreateJob = struct {
    ws: ?*Workspace,
    gpa: Allocator,
    io: std.Io,
    executor: *Executor,
    req: templates.Request,
    name: []u8,
    dir: []u8,
    result: ?templates.Result = null,
    err: ?anyerror = null,

    fn run(self: *CreateJob) void {
        defer self.executor.taskDone();
        if (templates.generate(self.gpa, self.io, self.req)) |r| self.result = r else |e| self.err = e;
        self.executor.post(.{ .ctx = self, .run = done });
    }

    fn done(ctx: *anyopaque) void {
        const self: *CreateJob = @ptrCast(@alignCast(ctx));
        defer {
            if (self.result) |*r| r.deinit(self.gpa);
            self.gpa.free(self.name);
            self.gpa.free(self.dir);
            self.gpa.destroy(self);
        }
        const ws = self.ws orelse return;
        ws.dirty = true;
        const n = ws.page.new orelse return;
        n.job = null;
        n.busy = false;
        if (self.err) |e| {
            n.setStatus(true, "Could not create the project: {t}", .{e});
            return;
        }
        const r = self.result.?;
        if (r.warning) |w| ws.setStatus("Created {s} ({s})", .{ self.dir, w }) else ws.setStatus("Created {s}", .{self.dir});
        n.rememberChoices();
        closePage(ws, .new_project);
        if (ws.project == null) {
            openCreated(ws, self.dir, r.main_file);
        } else {
            const st = &ws.page;
            if (st.created_dir) |d| ws.gpa.free(d);
            if (st.created_main) |d| ws.gpa.free(d);
            st.created_dir = ws.gpa.dupe(u8, self.dir) catch null;
            st.created_main = ws.gpa.dupe(u8, r.main_file) catch null;
            const label = ws.gpa.dupe(u8, "Open the new project") catch return;
            ws.openList(.list, label, @intFromEnum(ListKind.open_created), &.{ "Open in This Window", "Don't Open" });
        }
    }
};

fn openCreated(ws: *Workspace, dir: []const u8, main_file: []const u8) void {
    ws.openFolder(dir);
    if (main_file.len > 0) _ = ws.openFile(main_file, false) catch |err| ws.setStatus("Unable to open {s}: {t}", .{ main_file, err });
}

fn startCreate(ws: *Workspace) void {
    const n = ws.page.new orelse return;
    n.revalidate(ws);
    if (!n.canCreate()) return;
    const dir = n.targetDir(ws.gpa) orelse return;
    errdefer ws.gpa.free(dir);
    const name = ws.gpa.dupe(u8, n.name.slice()) catch return;
    errdefer ws.gpa.free(name);
    const job = ws.gpa.create(CreateJob) catch return;
    job.* = .{
        .ws = ws,
        .gpa = ws.gpa,
        .io = ws.io,
        .executor = ws.executor,
        .req = .{ .gen = n.gen, .name = name, .dir = dir, .choices = n.choices[n.gen], .git = n.git },
        .name = name,
        .dir = dir,
    };
    ws.executor.spawn(CreateJob.run, .{job}) catch {
        ws.gpa.destroy(job);
        n.setStatus(true, "Could not start creating the project", .{});
        return;
    };
    n.job = job;
    n.busy = true;
    n.setStatus(false, "Creating {s}…", .{name});
}

// ---- Cloning ---------------------------------------------------------------

const CloneJob = struct {
    ws: ?*Workspace,
    gpa: Allocator,
    io: std.Io,
    executor: *Executor,
    url: []u8,
    dir: []u8,
    parent: []u8,
    ok: bool = false,
    message: ?[]u8 = null,

    fn run(self: *CloneJob) void {
        defer self.executor.taskDone();
        const out = git.runFull(self.gpa, self.io, &.{ "git", "clone", "--", self.url, self.dir }, self.parent, null, true) catch |e| {
            self.message = std.fmt.allocPrint(self.gpa, "git: {t}", .{e}) catch null;
            self.executor.post(.{ .ctx = self, .run = done });
            return;
        };
        defer self.gpa.free(out.stdout);
        self.ok = out.ok;
        if (!out.ok) {
            const line = std.mem.trim(u8, out.stderr, " \r\n");
            const last = if (std.mem.lastIndexOfScalar(u8, line, '\n')) |i| line[i + 1 ..] else line;
            self.message = self.gpa.dupe(u8, last) catch null;
        }
        self.gpa.free(out.stderr);
        self.executor.post(.{ .ctx = self, .run = done });
    }

    fn done(ctx: *anyopaque) void {
        const self: *CloneJob = @ptrCast(@alignCast(ctx));
        defer {
            if (self.message) |m| self.gpa.free(m);
            self.gpa.free(self.url);
            self.gpa.free(self.dir);
            self.gpa.free(self.parent);
            self.gpa.destroy(self);
        }
        const ws = self.ws orelse return;
        ws.dirty = true;
        ws.page.clone_job = null;
        if (!self.ok) return ws.setStatus("Clone failed: {s}", .{self.message orelse "git clone did not finish"});
        ws.setStatus("Cloned {s}", .{self.dir});
        ws.openFolder(self.dir);
    }
};

/// The repository's name from its URL: `…/repo.git` → `repo`.
fn repoName(url: []const u8) []const u8 {
    var s = std.mem.trimEnd(u8, url, "/");
    if (std.mem.endsWith(u8, s, ".git")) s = s[0 .. s.len - 4];
    const cut = std.mem.lastIndexOfAny(u8, s, "/:") orelse return s;
    return s[cut + 1 ..];
}

fn startClone(ws: *Workspace, url: []const u8) void {
    if (ws.page.clone_job != null) return ws.setStatus("A clone is already running", .{});
    const name = repoName(url);
    if (name.len == 0 or templates.validateName(indexOfEmpty(), name) != null) return ws.setStatus("Cannot tell a folder name from {s}", .{url});
    loadRecent(ws);
    const home = std.c.getenv("HOME") orelse return;
    const parent = (if (ws.project) |p| (std.fs.path.dirname(p.root) orelse std.mem.span(home)) else std.mem.span(home));
    const job = ws.gpa.create(CloneJob) catch return;
    job.* = .{
        .ws = ws,
        .gpa = ws.gpa,
        .io = ws.io,
        .executor = ws.executor,
        .url = ws.gpa.dupe(u8, url) catch return,
        .dir = std.fmt.allocPrint(ws.gpa, "{s}/{s}", .{ parent, name }) catch return,
        .parent = ws.gpa.dupe(u8, parent) catch return,
    };
    ws.executor.spawn(CloneJob.run, .{job}) catch return;
    ws.page.clone_job = job;
    ws.setStatus("Cloning {s}…", .{url});
}

fn indexOfEmpty() usize {
    return templates.indexOf(.empty);
}

// ---- Events ----------------------------------------------------------------

/// True when the page took the event.
pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    if (!visible(ws)) return false;
    if (ws.overlay != null or ws.menuIsOpen() or ws.leader.active) return false;
    const area = ws.editor_rect;
    switch (ev) {
        .mouse_down => |m| {
            if (!area.contains(m.pos.x, m.pos.y)) return false;
            ws.focus = .editor;
            if (m.button == .left) click(ws, m.pos.x, m.pos.y);
            return true;
        },
        .mouse_up => |m| return area.contains(m.pos.x, m.pos.y),
        .mouse_move => |m| return area.contains(m.pos.x, m.pos.y),
        .scroll => |s| {
            if (!area.contains(s.pos.x, s.pos.y)) return false;
            if (ws.page.new) |n| if (current(ws) == .new_project) {
                n.list_scroll = @max(0, n.list_scroll - s.delta.y * @as(f32, if (s.unit == .lines) 30 else 1));
            };
            return true;
        },
        .key_down => |k| {
            if (ws.focus != .editor or current(ws) != .new_project) return false;
            return key(ws, ws.page.new.?, k);
        },
        .text_input => |t| {
            if (ws.focus != .editor or current(ws) != .new_project) return false;
            const n = ws.page.new.?;
            if (n.activeField()) |f| {
                f.insert(ws.gpa, t);
                n.revalidate(ws);
            }
            return true;
        },
        .preedit => return ws.focus == .editor and current(ws) == .new_project,
        else => return false,
    }
}

fn click(ws: *Workspace, x: f32, y: f32) void {
    const st = &ws.page;
    // Later hits are painted on top (the open drop-down).
    var i = st.hits.items.len;
    while (i > 0) {
        i -= 1;
        const h = st.hits.items[i];
        if (!h.rect.contains(x, y)) continue;
        return act(ws, h.click, x);
    }
    if (st.new) |n| n.dropdown = null;
}

fn act(ws: *Workspace, c: Click, x: f32) void {
    const st = &ws.page;
    const n_opt = st.new;
    if (n_opt) |n| if (c != .dropdown and c != .dropdown_item) {
        n.dropdown = null;
    };
    switch (c) {
        .tab => |p| st.shown = p,
        .close_tab => |p| closePage(ws, p),
        .link => |l| switch (l) {
            .new_project => perform(ws, .new_project),
            .open_folder => ws.perform(.open_folder),
            .open_file => ws.perform(.open_file),
            .new_file => {
                st.welcome_closed = true;
                ws.perform(.new_file);
            },
            .clone => promptClone(ws),
            .palette => ws.perform(.command_palette),
        },
        .recent => |i| {
            if (i < st.recent.items.items.len) {
                const path = ws.gpa.dupe(u8, st.recent.items.items[i]) catch return;
                defer ws.gpa.free(path);
                ws.openFolder(path);
            }
        },
        .generator => |g| if (n_opt) |n| {
            n.gen = g;
            n.dropdown = null;
            n.revalidate(ws);
            if (n.focus == .option) n.focus = .name;
        },
        .field_name => if (n_opt) |n| {
            n.focus = .name;
            n.name.caret = n.name.slice().len;
            n.name.all = false;
        },
        .field_location => if (n_opt) |n| {
            n.focus = .location;
            n.location.caret = n.location.slice().len;
            n.location.all = false;
        },
        .browse => if (n_opt) |n| browse(ws, n),
        .dropdown => |o| if (n_opt) |n| {
            n.focus = .option;
            n.focus_option = o;
            n.dropdown = if (n.dropdown == o) null else o;
        },
        .dropdown_item => |d| if (n_opt) |n| {
            n.choices[n.gen][d.option] = d.choice;
            n.dropdown = null;
        },
        .git_box => if (n_opt) |n| {
            n.git = !n.git;
        },
        .create => startCreate(ws),
        .cancel => closePage(ws, .new_project),
        .none => {},
    }
    _ = x;
}

fn browse(ws: *Workspace, n: *NewProject) void {
    n.focus = .browse;
    if (!folder_picker.available) return n.setStatus(true, "There is no folder panel here: type the location", .{});
    var start_buf: [std.fs.max_path_bytes + 1]u8 = undefined;
    const abs = templates.expandLocation(ws.gpa, n.location.slice()) catch null;
    defer if (abs) |a| ws.gpa.free(a);
    const start = std.fmt.bufPrintSentinel(&start_buf, "{s}", .{abs orelse ""}, 0) catch return;
    const picked = folder_picker.pick(ws.gpa, start, "Choose where to create the project") orelse return;
    defer ws.gpa.free(picked);
    n.location.set(ws.gpa, picked);
    n.revalidate(ws);
}

fn key(ws: *Workspace, n: *NewProject, k: platform.KeyEvent) bool {
    const mods = k.mods;
    const cmd = mods.command();
    // Chords that are not the page's go on to the window's key bindings.
    if ((cmd or mods.alt) and !(k.key == .char and (k.key.char == 'a' or k.key.char == 'v') and cmd and !mods.alt and !mods.shift)) return false;
    if (n.dropdown) |o| return dropdownKey(n, o, k);
    switch (k.key) {
        .char => |ch| {
            if (cmd and ch == 'a') {
                if (n.activeField()) |f| {
                    f.all = true;
                    f.caret = f.text.items.len;
                }
                return true;
            }
            if (cmd and ch == 'v') {
                const text = ws.env.clipboard.get(ws.env.clipboard.ctx, ws.gpa) orelse return true;
                defer ws.gpa.free(text);
                if (n.activeField()) |f| {
                    f.insert(ws.gpa, text);
                    n.revalidate(ws);
                }
                return true;
            }
            if (ch == ' ') switch (n.focus) {
                .git => {
                    n.git = !n.git;
                    return true;
                },
                .option => {
                    n.dropdown = n.focus_option;
                    return true;
                },
                .browse => {
                    browse(ws, n);
                    return true;
                },
                .create => {
                    startCreate(ws);
                    return true;
                },
                .cancel => {
                    closePage(ws, .new_project);
                    return true;
                },
                else => {},
            };
            // A plain character types into the focused field.
            if (k.text.len > 0) if (n.activeField()) |f| {
                f.insert(ws.gpa, k.text);
                n.revalidate(ws);
            };
            return true;
        },
        .named => |nk| switch (nk) {
            .tab => {
                n.moveFocus(if (mods.shift) -1 else 1);
                return true;
            },
            .escape => {
                if (n.busy) {
                    if (n.job) |j| j.ws = null;
                    n.job = null;
                    n.busy = false;
                    n.setStatus(false, "Stopped waiting; the project may still be made", .{});
                } else closePage(ws, .new_project);
                return true;
            },
            .enter => {
                switch (n.focus) {
                    .browse => browse(ws, n),
                    .cancel => closePage(ws, .new_project),
                    .option => n.dropdown = n.focus_option,
                    else => startCreate(ws),
                }
                return true;
            },
            .backspace => {
                if (n.activeField()) |f| {
                    f.backspace();
                    n.revalidate(ws);
                }
                return true;
            },
            .delete => {
                if (n.activeField()) |f| {
                    f.delete();
                    n.revalidate(ws);
                }
                return true;
            },
            .left => {
                if (n.activeField()) |f| {
                    f.all = false;
                    f.caret = f.prev();
                } else if (n.focus == .option) n.step(-1);
                return true;
            },
            .right => {
                if (n.activeField()) |f| {
                    f.all = false;
                    f.caret = f.next();
                } else if (n.focus == .option) n.step(1);
                return true;
            },
            .home => {
                if (n.activeField()) |f| {
                    f.all = false;
                    f.caret = 0;
                }
                return true;
            },
            .end => {
                if (n.activeField()) |f| {
                    f.all = false;
                    f.caret = f.text.items.len;
                }
                return true;
            },
            .up, .down => {
                const down = nk == .down;
                if (n.focus == .option) {
                    n.step(if (down) 1 else -1);
                } else if (n.focus == .name or n.focus == .location) {
                    n.selectGenerator(if (down) 1 else -1);
                    n.revalidate(ws);
                }
                return true;
            },
            else => return true,
        },
    }
}

fn dropdownKey(n: *NewProject, o: u8, k: platform.KeyEvent) bool {
    const count = n.options()[o].choices.len;
    switch (k.key) {
        .named => |nk| switch (nk) {
            .escape, .enter => n.dropdown = null,
            .up => if (n.choices[n.gen][o] > 0) {
                n.choices[n.gen][o] -= 1;
            },
            .down => if (n.choices[n.gen][o] + 1 < count) {
                n.choices[n.gen][o] += 1;
            },
            else => {},
        },
        .char => |ch| if (ch == ' ') {
            n.dropdown = null;
        },
    }
    return true;
}

// ---- Painting --------------------------------------------------------------

const link_color = Color.hex(0x3794ff);
const error_color = Color.hex(0xf48771);

fn largeFont(ws: *Workspace, scale: f32) FontId {
    const desc = ws.env.text.faces.items[ws.fonts.ui].desc;
    return ws.env.text.font(.{ .family = desc.family, .size = desc.size * scale }) catch ws.fonts.ui_bold;
}

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx) void {
    if (!visible(ws)) return;
    const st = &ws.page;
    st.hits.clearRetainingCapacity();
    const area = ws.editor_rect;
    const theme = ctx.theme;
    const scene = ctx.scene;
    // Its own layer: quads and text are drawn per layer, so the editors'
    // text below would show through a fill on theirs.
    scene.pushLayer() catch return;
    defer scene.popLayer();
    scene.fill(area, theme.background);
    scene.pushClip(area) catch return;
    defer scene.popClip();
    paintTabs(ws, ctx, area);
    const body: Rect = .{ .x = area.x, .y = area.y + workspace_mod.tabbar_h, .w = area.w, .h = @max(0, area.h - workspace_mod.tabbar_h) };
    switch (current(ws)) {
        .welcome => paintWelcome(ws, ctx, body),
        .new_project => paintNewProject(ws, ctx, body),
    }
}

fn paintTabs(ws: *Workspace, ctx: *const ui.Ctx, area: Rect) void {
    const st = &ws.page;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const bar: Rect = .{ .x = area.x, .y = area.y, .w = area.w, .h = workspace_mod.tabbar_h };
    scene.fill(bar, theme.tabbar_background);
    scene.fill(.{ .x = bar.x, .y = bar.bottom() - 1, .w = bar.w, .h = 1 }, theme.tab_separator);
    var x = bar.x;
    const cur = current(ws);
    const pages = [_]struct { page: Page, title: []const u8, open: bool }{
        .{ .page = .welcome, .title = "Welcome", .open = welcomeShown(ws) },
        .{ .page = .new_project, .title = "New Project", .open = st.new != null },
    };
    for (pages) |p| {
        if (!p.open) continue;
        const w = ws.env.text.measure(p.title, ws.fonts.ui) + 20 + 16 + 8;
        const r: Rect = .{ .x = x, .y = bar.y, .w = w, .h = bar.h };
        const active = p.page == cur;
        const hovered = ctx.hovered(r);
        scene.fill(r, if (active) theme.background else if (hovered) theme.tab_hover else theme.tab_inactive);
        if (active) scene.fill(.{ .x = r.x, .y = r.y, .w = r.w, .h = 2 }, theme.input_border_focus);
        const fg = if (active) theme.tab_active_foreground else theme.tab_inactive_foreground;
        _ = ctx.label(.{ .x = r.x + 10, .y = r.y + 1, .w = w - 40, .h = r.h - 1 }, p.title, ws.fonts.ui, fg);
        const close: Rect = .{ .x = r.right() - 22, .y = r.y + (r.h - 16) / 2 + 1, .w = 16, .h = 16 };
        if (ctx.hovered(close)) scene.quad(close, .{ .color = theme.tab_hover.lighten(0.08), .radius = 3 });
        ctx.closeIcon(close.x + 8, close.y + 8, 8, fg);
        st.hits.append(ws.gpa, .{ .rect = r, .click = .{ .tab = p.page } }) catch {};
        st.hits.append(ws.gpa, .{ .rect = close, .click = .{ .close_tab = p.page } }) catch {};
        x += w;
    }
}

fn dim(theme: *const ui.Theme) Color {
    return theme.list_foreground_dim;
}

fn linkRow(ws: *Workspace, ctx: *const ui.Ctx, x: f32, y: f32, w: f32, text: []const u8, link: Link) f32 {
    const r: Rect = .{ .x = x, .y = y, .w = w, .h = 24 };
    const hovered = ctx.hovered(r);
    const scene = ctx.scene;
    const font = ws.fonts.ui;
    const color = if (hovered) link_color.lighten(0.15) else link_color;
    // A small icon in the link's colour.
    const cy = r.y + r.h / 2;
    switch (link) {
        .new_project => {
            ctx.folderIcon(x, cy, color, false);
            scene.fill(.{ .x = x + 11, .y = cy + 2, .w = 5, .h = 1.2 }, color);
            scene.fill(.{ .x = x + 13, .y = cy, .w = 1.2, .h = 5 }, color);
        },
        .open_folder => ctx.folderIcon(x, cy, color, true),
        .clone => ctx.folderIcon(x, cy, color, false),
        .palette => {
            scene.quad(.{ .x = x, .y = cy - 7, .w = 15, .h = 14 }, .{ .border_color = color, .border_width = 1, .radius = 2 });
            scene.fill(.{ .x = x + 3, .y = cy - 1, .w = 5, .h = 1.2 }, color);
        },
        else => ctx.fileIcon(x, cy, color),
    }
    const tw = ctx.label(.{ .x = x + 24, .y = r.y, .w = w - 24, .h = r.h }, text, font, color);
    if (hovered) scene.fill(.{ .x = x + 24, .y = r.y + r.h - 5, .w = tw, .h = 1 }, color);
    ws.page.hits.append(ws.gpa, .{ .rect = .{ .x = x, .y = r.y, .w = 24 + tw, .h = r.h }, .click = .{ .link = link } }) catch {};
    return r.h;
}

fn paintWelcome(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    loadRecent(ws);
    const theme = ctx.theme;
    const title_font = largeFont(ws, 2.6);
    const head_font = largeFont(ws, 1.4);
    const x0 = body.x + 56;
    var y = body.y + 40;
    const th = ws.env.text.metrics(title_font).line_height;
    _ = ctx.label(.{ .x = x0, .y = y, .w = body.w - 112, .h = th }, "cs-zig", title_font, theme.foreground);
    y += th;
    _ = ctx.label(.{ .x = x0, .y = y, .w = body.w - 112, .h = 22 }, "A native editor written in Zig", ws.fonts.ui, dim(theme));
    y += 52;

    const col_w: f32 = 380;
    const hh = ws.env.text.metrics(head_font).line_height + 6;
    var ly = y;
    _ = ctx.label(.{ .x = x0, .y = ly, .w = col_w, .h = hh }, "Start", head_font, theme.foreground);
    ly += hh + 4;
    const links = [_]struct { text: []const u8, link: Link }{
        .{ .text = "Create a new project", .link = .new_project },
        .{ .text = "Open a folder", .link = .open_folder },
        .{ .text = "Clone a repository", .link = .clone },
        .{ .text = "Open a file", .link = .open_file },
        .{ .text = "New file", .link = .new_file },
        .{ .text = "Command palette", .link = .palette },
    };
    for (links) |l| ly += linkRow(ws, ctx, x0, ly, col_w, l.text, l.link);

    // Recent folders on the right when there is room, else below.
    const wide = body.w > 2 * col_w + 160;
    var rx = x0 + col_w + 60;
    var ry = y;
    if (!wide) {
        rx = x0;
        ry = ly + 28;
    }
    _ = ctx.label(.{ .x = rx, .y = ry, .w = col_w, .h = hh }, "Recent", head_font, theme.foreground);
    ry += hh + 4;
    const items = ws.page.recent.items.items;
    if (items.len == 0) {
        _ = ctx.label(.{ .x = rx, .y = ry, .w = col_w, .h = 24 }, "No recent folders", ws.fonts.ui, dim(theme));
    }
    for (items, 0..) |path, i| {
        if (ry + 40 > body.bottom()) break;
        const r: Rect = .{ .x = rx, .y = ry, .w = @min(body.right() - rx - 40, 520), .h = 38 };
        const hovered = ctx.hovered(r);
        const name = std.fs.path.basename(path);
        _ = ctx.labelFit(.{ .x = r.x, .y = r.y, .w = r.w, .h = 20 }, name, ws.fonts.ui, if (hovered) link_color.lighten(0.15) else link_color);
        _ = ctx.labelFit(.{ .x = r.x, .y = r.y + 18, .w = r.w, .h = 18 }, path, ws.fonts.ui_small, dim(theme));
        ws.page.hits.append(ws.gpa, .{ .rect = r, .click = .{ .recent = i } }) catch {};
        ry += 42;
    }
}

fn button(ws: *Workspace, ctx: *const ui.Ctx, r: Rect, text: []const u8, primary: bool, focused: bool, enabled: bool, click_kind: Click) void {
    const theme = ctx.theme;
    const hovered = ctx.hovered(r) and enabled;
    const bg = if (primary) (if (hovered) theme.input_border_focus.lighten(0.1) else theme.input_border_focus) else (if (hovered) theme.button_active else theme.button_background);
    ctx.scene.quad(r, .{ .color = if (enabled) bg else bg.withAlpha(0.45), .radius = 2, .border_color = if (focused) theme.foreground else .transparent, .border_width = if (focused) 1 else 0 });
    ctx.labelCentered(r, text, ws.fonts.ui, if (enabled) Color.hex(0xffffff) else Color.hex(0xffffff).withAlpha(0.6));
    if (enabled) ws.page.hits.append(ws.gpa, .{ .rect = r, .click = click_kind }) catch {};
}

fn paintField(ws: *Workspace, ctx: *const ui.Ctx, r: Rect, f: *const Field, focused: bool, click_kind: Click) void {
    const theme = ctx.theme;
    const scene = ctx.scene;
    scene.quad(r, .{ .color = theme.input_background, .border_color = if (focused) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 2 });
    const inner: Rect = .{ .x = r.x + 6, .y = r.y, .w = r.w - 12, .h = r.h };
    scene.pushClip(inner) catch return;
    defer scene.popClip();
    const font = ws.fonts.ui;
    if (f.all and f.text.items.len > 0) {
        const w = ws.env.text.measure(f.slice(), font);
        scene.fill(.{ .x = inner.x, .y = r.y + 4, .w = w, .h = r.h - 8 }, theme.selection);
    }
    _ = ctx.label(inner, f.slice(), font, theme.foreground);
    if (focused and !f.all) {
        const cx = inner.x + ws.env.text.measure(f.text.items[0..f.caret], font);
        scene.fill(.{ .x = cx, .y = r.y + 5, .w = 1, .h = r.h - 10 }, theme.caret);
    }
    ws.page.hits.append(ws.gpa, .{ .rect = r, .click = click_kind }) catch {};
}

fn paintDropdown(ws: *Workspace, ctx: *const ui.Ctx, r: Rect, text: []const u8, focused: bool, index: u8) void {
    const theme = ctx.theme;
    const hovered = ctx.hovered(r);
    ctx.scene.quad(r, .{ .color = if (hovered) theme.button_active else theme.button_background, .border_color = if (focused) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 2 });
    _ = ctx.labelFit(.{ .x = r.x + 8, .y = r.y, .w = r.w - 30, .h = r.h }, text, ws.fonts.ui, theme.foreground);
    ctx.chevron(r.right() - 14, r.y + r.h / 2, true, theme.foreground);
    ws.page.hits.append(ws.gpa, .{ .rect = r, .click = .{ .dropdown = index } }) catch {};
}

fn paintCheckbox(ws: *Workspace, ctx: *const ui.Ctx, x: f32, y: f32, checked: bool, focused: bool, text: []const u8) void {
    const theme = ctx.theme;
    const box: Rect = .{ .x = x, .y = y + 2, .w = 18, .h = 18 };
    const w = 18 + 8 + ws.env.text.measure(text, ws.fonts.ui);
    const hit: Rect = .{ .x = x, .y = y, .w = w, .h = 22 };
    ctx.scene.quad(box, .{ .color = if (checked) theme.input_border_focus else theme.input_background, .border_color = if (focused) theme.foreground else if (checked) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 2 });
    if (checked) {
        // A tick: a short stroke down and a long one up, from 2px squares.
        const white = Color.hex(0xffffff);
        const pts = [_][2]f32{ .{ 4, 9 }, .{ 5.5, 10.5 }, .{ 7, 12 }, .{ 8.5, 10.5 }, .{ 10, 9 }, .{ 11.5, 7.5 }, .{ 13, 6 } };
        for (pts) |pt| ctx.scene.fill(.{ .x = x + pt[0], .y = y + pt[1] - 1, .w = 2, .h = 2 }, white);
    }
    _ = ctx.label(.{ .x = x + 26, .y = y, .w = w, .h = 22 }, text, ws.fonts.ui, theme.foreground);
    ws.page.hits.append(ws.gpa, .{ .rect = hit, .click = .git_box }) catch {};
}

fn paintNewProject(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const n = ws.page.new orelse return;
    const theme = ctx.theme;
    const scene = ctx.scene;
    const text = ws.env.text;
    const gens = templates.generators;
    const x0 = body.x + 40;
    var y = body.y + 28;
    const title_font = largeFont(ws, 1.7);
    const th = text.metrics(title_font).line_height;
    _ = ctx.label(.{ .x = x0, .y = y, .w = body.w - 80, .h = th }, "Create a new project", title_font, theme.foreground);
    y += th + 18;

    // The list of generators.
    const row_h: f32 = 46;
    const list: Rect = .{ .x = x0, .y = y, .w = 290, .h = @min(row_h * gens.len + 2, @max(120, body.bottom() - y - 16)) };
    scene.quad(list, .{ .color = theme.input_background, .border_color = theme.input_border, .border_width = 1 });
    {
        scene.pushClip(list) catch return;
        defer scene.popClip();
        const max_scroll = @max(0, row_h * gens.len + 2 - list.h);
        n.list_scroll = std.math.clamp(n.list_scroll, 0, max_scroll);
        for (gens, 0..) |g, i| {
            const r: Rect = .{ .x = list.x + 1, .y = list.y + 1 + @as(f32, @floatFromInt(i)) * row_h - n.list_scroll, .w = list.w - 2, .h = row_h };
            const selected = i == n.gen;
            const hovered = ctx.hovered(r) and list.contains(ctx.mouse.x, ctx.mouse.y);
            if (selected) scene.fill(r, theme.list_selected) else if (hovered) scene.fill(r, theme.sidebar_hover);
            // The badge.
            const badge: Rect = .{ .x = r.x + 10, .y = r.y + 11, .w = 24, .h = 24 };
            scene.quad(badge, .{ .border_color = theme.list_foreground_dim, .border_width = 1, .radius = 3 });
            ctx.labelCentered(badge, g.badge, ws.fonts.ui_small, theme.list_foreground);
            _ = ctx.labelFit(.{ .x = r.x + 46, .y = r.y + 3, .w = r.w - 54, .h = 20 }, g.name, ws.fonts.ui, theme.foreground);
            _ = ctx.labelFit(.{ .x = r.x + 46, .y = r.y + 23, .w = r.w - 54, .h = 18 }, g.tagline, ws.fonts.ui_small, dim(theme));
            ws.page.hits.append(ws.gpa, .{ .rect = r, .click = .{ .generator = i } }) catch {};
        }
    }

    // The form.
    const fx = list.right() + 36;
    const label_w: f32 = 120;
    const cx = fx + label_w;
    const form_w = @min(@max(300, body.right() - cx - 40), 560);
    var fy = y;
    const row: f32 = 28;
    const pad: f32 = 10;

    _ = ctx.label(.{ .x = fx, .y = fy, .w = label_w, .h = row }, "Name:", ws.fonts.ui, theme.foreground);
    paintField(ws, ctx, .{ .x = cx, .y = fy, .w = form_w - 100, .h = row }, &n.name, n.focus == .name, .field_name);
    fy += row + pad;

    _ = ctx.label(.{ .x = fx, .y = fy, .w = label_w, .h = row }, "Location:", ws.fonts.ui, theme.foreground);
    paintField(ws, ctx, .{ .x = cx, .y = fy, .w = form_w - 100, .h = row }, &n.location, n.focus == .location, .field_location);
    button(ws, ctx, .{ .x = cx + form_w - 92, .y = fy, .w = 92, .h = row }, "Browse…", false, n.focus == .browse, true, .browse);
    fy += row + 6;

    // What will be made, or what is wrong.
    {
        var buf: [std.fs.max_path_bytes + 64]u8 = undefined;
        const msg_rect: Rect = .{ .x = cx, .y = fy, .w = form_w + 40, .h = 20 };
        if (n.name_error orelse n.folder_error) |e| {
            _ = ctx.labelFit(msg_rect, e, ws.fonts.ui_small, error_color);
        } else if (n.targetDir(ws.gpa)) |dir| {
            defer ws.gpa.free(dir);
            const line = std.fmt.bufPrint(&buf, "Project will be created in {s}", .{dir}) catch "";
            _ = ctx.labelFit(msg_rect, line, ws.fonts.ui_small, dim(theme));
        }
    }
    fy += 32;

    const opts = n.options();
    if (opts.len > 0) {
        _ = ctx.label(.{ .x = fx, .y = fy, .w = 200, .h = 22 }, "Options", ws.fonts.ui_bold, theme.foreground);
        fy += 28;
    }
    var open_dd: ?struct { rect: Rect, option: u8 } = null;
    for (opts, 0..) |o, oi| {
        const idx: u8 = @intCast(oi);
        _ = ctx.label(.{ .x = fx, .y = fy, .w = label_w, .h = row }, o.label, ws.fonts.ui, theme.foreground);
        const r: Rect = .{ .x = cx, .y = fy, .w = 250, .h = row };
        const choice = n.choices[n.gen][oi];
        paintDropdown(ws, ctx, r, o.choices[@min(choice, o.choices.len - 1)], n.focus == .option and n.focus_option == idx, idx);
        if (n.dropdown) |d| if (d == idx) {
            open_dd = .{ .rect = r, .option = idx };
        };
        fy += row;
        if (o.hint.len > 0) {
            _ = ctx.labelFit(.{ .x = cx, .y = fy, .w = form_w, .h = 18 }, o.hint, ws.fonts.ui_small, dim(theme));
            fy += 20;
        }
        fy += 8;
    }
    fy += 6;
    paintCheckbox(ws, ctx, cx, fy, n.git, n.focus == .git, "Create Git repository");
    fy += 34;

    // The toolchain's line.
    {
        const gen = gens[n.gen];
        var buf: [200]u8 = undefined;
        const tool_rect: Rect = .{ .x = cx, .y = fy, .w = form_w + 100, .h = 20 };
        if (gen.tool) |tool| {
            switch (templates.toolStatus(n.gen)) {
                .found => {
                    const v = templates.toolVersion(n.gen);
                    const line = if (v.len > 0 and std.ascii.isDigit(v[0])) (std.fmt.bufPrint(&buf, "{s} {s}", .{ tool.argv[0], v }) catch v) else v;
                    _ = ctx.labelFit(tool_rect, line, ws.fonts.ui_small, dim(theme));
                },
                .missing => {
                    const line = std.fmt.bufPrint(&buf, "{s} was not found (install it with: {s}); the project is still made", .{ tool.argv[0], tool.install }) catch "";
                    _ = ctx.labelFit(tool_rect, line, ws.fonts.ui_small, Color.hex(0xcca700));
                },
                else => _ = ctx.labelFit(tool_rect, "Checking the toolchain…", ws.fonts.ui_small, dim(theme)),
            }
        }
    }
    fy += 34;

    const can = n.canCreate();
    button(ws, ctx, .{ .x = cx + form_w - 190, .y = fy, .w = 90, .h = 30 }, "Create", true, n.focus == .create, can, .create);
    button(ws, ctx, .{ .x = cx + form_w - 92, .y = fy, .w = 92, .h = 30 }, "Cancel", false, n.focus == .cancel, true, .cancel);
    if (n.status_len > 0) {
        _ = ctx.labelFit(.{ .x = fx, .y = fy + 5, .w = form_w - 200 + label_w, .h = 20 }, n.status[0..n.status_len], ws.fonts.ui_small, if (n.status_is_error) error_color else dim(theme));
    }

    // The open drop-down, on top of everything.
    if (open_dd) |d| {
        const o = opts[d.option];
        const h = @as(f32, @floatFromInt(o.choices.len)) * 26 + 2;
        const pop: Rect = .{ .x = d.rect.x, .y = d.rect.bottom() + 1, .w = d.rect.w, .h = h };
        scene.pushLayer() catch return;
        defer scene.popLayer();
        ctx.shadow(pop, 4, 8);
        scene.quad(pop, .{ .color = theme.overlay_background, .border_color = theme.input_border, .border_width = 1, .radius = 2 });
        for (o.choices, 0..) |c, ci| {
            const r: Rect = .{ .x = pop.x + 1, .y = pop.y + 1 + @as(f32, @floatFromInt(ci)) * 26, .w = pop.w - 2, .h = 26 };
            const selected = n.choices[n.gen][d.option] == ci;
            if (ctx.hovered(r)) scene.fill(r, theme.list_selected) else if (selected) scene.fill(r, theme.sidebar_hover);
            _ = ctx.labelFit(.{ .x = r.x + 8, .y = r.y, .w = r.w - 16, .h = r.h }, c, ws.fonts.ui, theme.foreground);
            ws.page.hits.append(ws.gpa, .{ .rect = r, .click = .{ .dropdown_item = .{ .option = d.option, .choice = @intCast(ci) } } }) catch {};
        }
    }
}

test "repoName takes the last path segment" {
    try std.testing.expectEqualStrings("repo", repoName("https://github.com/a/repo.git"));
    try std.testing.expectEqualStrings("repo", repoName("git@github.com:a/repo.git/"));
    try std.testing.expectEqualStrings("repo", repoName("/tmp/repo"));
}

test "a text field edits by code point" {
    const gpa = std.testing.allocator;
    var f: Field = .{};
    defer f.deinit(gpa);
    f.insert(gpa, "aé");
    try std.testing.expectEqualStrings("aé", f.slice());
    f.backspace();
    try std.testing.expectEqualStrings("a", f.slice());
    f.insert(gpa, "bc");
    f.caret = 1;
    f.delete();
    try std.testing.expectEqualStrings("ac", f.slice());
    f.all = true;
    f.insert(gpa, "x");
    try std.testing.expectEqualStrings("x", f.slice());
    // Newlines never enter a single-line field.
    f.insert(gpa, "a\nb");
    try std.testing.expectEqualStrings("x", f.slice());
}
