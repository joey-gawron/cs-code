//! The window's contents: menu bar, side bar, tabs, editors, find panel,
//! status bar and overlays, plus input routing and workspace-level actions.

const std = @import("std");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const keymap_mod = @import("../settings/keymap.zig");
const ui_state = @import("../settings/ui_state.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const ui = @import("../../framework/ui/ui.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const syntax_mod = @import("../../model/syntax/syntax.zig");
const languages = @import("../../model/syntax/languages.zig");
const Executor = @import("../../framework/util/executor.zig").Executor;
const FileWatcher = @import("../../framework/platform/file_watcher.zig").FileWatcher;
const project_mod = @import("../../model/project/project.zig");
const quick_panel = @import("quick_panel.zig");
const find_panel = @import("find_panel.zig");
const commands = @import("commands.zig");
const menu = @import("menu.zig");
const toolbar = @import("toolbar.zig");
const context_menu = @import("context_menu.zig");
const symbols = @import("../../model/project/symbols.zig");
const search = @import("../../model/project/search.zig");
const org = @import("../org/org.zig");
const org_ui = @import("org_ui.zig");
const agenda_mod = @import("../../model/org/agenda.zig");
const lsp_mod = @import("../../model/lsp/lsp.zig");
const lsp_ui = @import("lsp_ui.zig");
const git_ui = @import("git_ui.zig");
const git_status_ui = @import("git_status_ui.zig");
const groups = @import("groups.zig");
const terminal_ui = @import("terminal_ui.zig");
const settings_ui = @import("settings_ui.zig");
const panel_ui = @import("panel_ui.zig");
const leader_ui = @import("leader_ui.zig");
const file_cmd = @import("file_cmd.zig");
const path_complete = @import("path_complete.zig");
const TerminalView = @import("../terminal/terminal_view.zig").TerminalView;
const remote_ui = @import("remote_ui.zig");
const vfs = @import("../../model/remote/vfs.zig");
const rpath = @import("../../model/remote/path.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Action = keymap_mod.Action;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Scene = scene_mod.Scene;
const Theme = theme_mod.Theme;
const QuickPanel = quick_panel.QuickPanel;
const FindPanel = find_panel.FindPanel;
const Project = project_mod.Project;
const log = std.log.scoped(.workspace);

pub const Host = struct {
    ctx: *anyopaque,
    setTitle: *const fn (ctx: *anyopaque, title: [:0]const u8) void,
    setCursor: *const fn (ctx: *anyopaque, shape: platform.CursorShape) void,
    toggleFullscreen: *const fn (ctx: *anyopaque) void,
};

/// Theme / settings services provided by the app (works headless too).
pub const Prefs = struct {
    ctx: *anyopaque,
    reloadSettings: *const fn (ctx: *anyopaque) void,
    /// Show a scheme temporarily (null = stop previewing).
    previewScheme: *const fn (ctx: *anyopaque, name: ?[]const u8) void,
    /// Make a scheme (or "auto") the setting and save it.
    chooseScheme: *const fn (ctx: *anyopaque, name: []const u8) void,
    /// "Dayfox until 18:12 (sunset)" for the scheme picker.
    describeAuto: *const fn (ctx: *anyopaque, buf: []u8) []const u8,
    settingsPath: *const fn (ctx: *anyopaque, buf: []u8) ?[]const u8,
    /// Every selectable scheme (built-in and installed).
    schemes: *const fn (ctx: *anyopaque) []const theme_mod.Scheme,
};

pub const Tab = struct {
    editor: *Editor,
    /// The view group (split pane) the tab lives in.
    group: u8 = 0,
    /// Sublime's preview tab: replaced by the next preview until edited.
    transient: bool = false,
    /// Set for the org agenda tab.
    agenda: ?*agenda_mod.Agenda = null,
    /// Set for terminal tabs (drawn instead of the editor; see terminal_ui.zig).
    terminal: ?*TerminalView = null,
    /// Set for the Settings tab (settings_ui.zig).
    settings: ?*settings_ui.View = null,
    /// Set for Diff, Log and Git Output tabs (opened from Source Control; git_status_ui.zig).
    git_diff: ?*git_status_ui.Diff = null,
    /// Set for a commit message (COMMIT_EDITMSG) tab (git_popup.zig).
    git_commit: ?*git_status_ui.CommitTab = null,
};

const Focus = enum {
    editor,
    find,
    overlay,
    /// The panel under the editors (panel_ui.zig).
    panel,
};

/// Reopen Closed File's history: the paths of files whose last view closed,
/// newest last, each once, and only the latest `max` of them.
pub const ClosedPaths = struct {
    items: std.ArrayList([]u8) = .empty,

    const max = 100;

    pub fn deinit(self: *ClosedPaths, gpa: Allocator) void {
        for (self.items.items) |p| gpa.free(p);
        self.items.deinit(gpa);
    }

    /// Adds a copy of `path` as the newest.
    pub fn record(self: *ClosedPaths, gpa: Allocator, path: []const u8) void {
        self.forget(gpa, path);
        const copy = gpa.dupe(u8, path) catch return;
        self.items.append(gpa, copy) catch return gpa.free(copy);
        if (self.items.items.len > max) gpa.free(self.items.orderedRemove(0));
    }

    /// Drops `path` (a deleted file).
    pub fn forget(self: *ClosedPaths, gpa: Allocator, path: []const u8) void {
        var i: usize = 0;
        while (i < self.items.items.len) {
            if (std.mem.eql(u8, self.items.items[i], path)) gpa.free(self.items.orderedRemove(i)) else i += 1;
        }
    }

    /// The newest path, now the caller's.
    pub fn pop(self: *ClosedPaths) ?[]u8 {
        return self.items.pop();
    }
};

const PromptTag = enum(u32) { save_as = 1, open_path, confirm_close, confirm_quit, rename, select_scheme, confirm_reload, open_folder };

/// Hook for modal editing (vim). Returns true if the key was consumed.
pub const KeyHook = struct {
    ctx: *anyopaque,
    handleKey: *const fn (ctx: *anyopaque, ws: *Workspace, editor: *Editor, key: platform.KeyEvent) bool,
    statusText: *const fn (ctx: *anyopaque, buf: []u8) []const u8,
    /// Draws a command line over the status bar; returns false if inactive.
    paintCommandLine: *const fn (ctx: *anyopaque, ui_ctx: *const ui.Ctx, rect: Rect) bool,
    blockCaret: *const fn (ctx: *anyopaque) bool,
    onToggle: *const fn (ctx: *anyopaque, editor: ?*Editor, enabled: bool) void,
    /// True when space starts the leader keys (leader_ui.zig) rather than
    /// being one of the hook's keys.
    leaderState: *const fn (ctx: *anyopaque) bool,
    /// A key the leader took, for a macro the hook is recording.
    recordKey: *const fn (ctx: *anyopaque, key: platform.KeyEvent) void,
    /// After a leader command ran: the hook's state follows the active view.
    /// `started_in` was active when the sequence began; the command may
    /// have closed it.
    afterLeader: *const fn (ctx: *anyopaque, ws: *Workspace, started_in: ?*Editor) void,
    enabled: bool = false,
};

pub const max_groups = 4;

/// Sublime's View > Layout presets (groups.preset gives each as a split
/// tree). Groups are numbered row by row.
pub const GroupLayout = enum {
    single,
    columns_2,
    columns_3,
    columns_4,
    rows_2,
    rows_3,
    grid_4,

    pub fn cols(l: GroupLayout) u8 {
        return switch (l) {
            .columns_2, .grid_4 => 2,
            .columns_3 => 3,
            .columns_4 => 4,
            else => 1,
        };
    }

    pub fn rows(l: GroupLayout) u8 {
        return switch (l) {
            .rows_2, .grid_4 => 2,
            .rows_3 => 3,
            else => 1,
        };
    }

    pub fn count(l: GroupLayout) u8 {
        return l.cols() * l.rows();
    }
};

/// A tab's hit areas from the last paint.
const TabHit = struct { rect: Rect, close: Rect, index: usize };

/// The keywords SPC p t lists and vim's ]t / [t go to (Doom's hl-todo).
pub const todo_pattern = "\\b(TODO|FIXME|HACK|BUG|XXX)\\b";

/// How much bigger SPC t b (Doom's big-font-mode) makes the editor font.
const big_font_step: f32 = 4;

pub const tabbar_h: f32 = 26;
pub const status_h: f32 = 22;
/// The title row (menus and Quick Launch) and the toolbar under it, as in cs-code.
pub const menubar_h: f32 = 30;
pub const toolbar_h: f32 = 30;
const sidebar_row_h: f32 = 22;
/// The Explorer's header: title row, tool row and filter box, above the tree.
const sidebar_header_h: f32 = 96;
/// The narrowest the side bar's edge drags it to; below half of that it
/// collapses, and closes if the drag ends there.
const sidebar_min_w: f32 = 120;
/// The strip along the left edge that brings a hidden side bar back.
const sidebar_strip_w: f32 = 8;

/// The side bar `w` wide in a window `window_w` wide: at most 60% of the
/// window, but never below the minimum.
fn sidebarFit(w: f32, window_w: f32) f32 {
    return @round(@max(sidebar_min_w, @min(w, window_w * 0.6)));
}

/// The side bar while its edge is dragged to `x` in a window `window_w`
/// wide, having been `from` wide when the drag began. Below half the minimum
/// width it collapses (hidden if the drag ends there) and keeps `from`: on
/// its way there the drag passes the minimum, which mustn't become its width.
fn sidebarDrag(from: f32, x: f32, window_w: f32) ui_state.State {
    if (x < sidebar_min_w / 2) return .{ .sidebar_visible = false, .sidebar_width = from };
    return .{ .sidebar_visible = true, .sidebar_width = sidebarFit(x, window_w) };
}

pub const Workspace = struct {
    gpa: Allocator,
    io: Io,
    env: *editor_mod.Env,
    theme: *const Theme,
    registry: *syntax_mod.Registry,
    executor: *Executor,
    keymap: *keymap_mod.Keymap,
    fonts: ui.Fonts,
    host: ?Host = null,
    prefs: ?Prefs = null,

    project: ?*Project = null,
    buffers: std.ArrayList(*Buffer) = .empty,
    next_buffer_id: u32 = 1,
    /// All tabs in display order; each belongs to a group (split pane).
    tabs: std.ArrayList(Tab) = .empty,
    mru: std.ArrayList(*Editor) = .empty,
    /// The groups as a split tree, with the (draggable) splitter positions.
    group_layout: groups.Layout = .single,
    focused_group: u8 = 0,
    /// The group focused before the current one (vim's ctrl-w p).
    prev_focused_group: ?u8 = null,
    /// SPC w m m: this group fills the editor area while the others keep
    /// their places; a layout change or focusing another group ends it.
    zoomed_group: ?u8 = null,
    /// The view each group shows (null = empty group).
    group_active: [max_groups]?*Editor = @splat(null),
    /// The splitter between groups being dragged.
    drag_split: ?groups.Splitter = null,
    /// Editor receiving the current mouse drag.
    mouse_editor: ?*Editor = null,
    closed_paths: ClosedPaths = .{},

    sidebar_visible: bool = true,
    /// The side bar's width when shown (never below sidebar_min_w).
    sidebar_width: f32 = 240,
    /// The side bar's state as state.json has it (null: not read yet).
    saved_ui_state: ?ui_state.State = null,
    /// The side bar's width when the current edge drag began, kept if the
    /// drag ends collapsed.
    sidebar_drag_from: f32 = 0,
    /// The last press hid or reopened the side bar (its × or the strip).
    sidebar_toggle_click: bool = false,
    sidebar_scroll: f32 = 0,
    sidebar_rows: std.ArrayList(u32) = .empty,
    sidebar_rows_generation: u32 = std.math.maxInt(u32),
    root_expanded: bool = true,
    tabs_visible: bool = true,
    status_visible: bool = true,
    menu_visible: bool = true,
    distraction_free: bool = false,

    overlay: ?*QuickPanel = null,
    find: ?*FindPanel = null,
    /// Find in Files: the running search, and the match ranges of the
    /// Find Results buffer (for F4 / shift+F4).
    search_job: ?*search.Job = null,
    /// Searches whose `onSearchDone` hasn't run, superseded ones included.
    searches_running: u32 = 0,
    /// Watches the folders of open files for changes made by other programs.
    watcher: ?FileWatcher = null,
    watcher_failed: bool = false,
    watched_signature: u64 = 0,
    /// Files changed on disk while they had unsaved edits, awaiting an answer.
    reload_queue: std.ArrayList([]u8) = .empty,
    reload_prompt: ?[]u8 = null,
    result_ranges: std.ArrayList([2]usize) = .empty,
    result_index: ?usize = null,
    shutting_down: bool = false,
    find_visible: bool = false,
    focus: Focus = .editor,
    /// Selections/scroll to restore when Goto Line / incremental find is cancelled.
    restore_selections: ?[]editor_mod.Selection = null,
    restore_scroll: f32 = 0,
    pending_close: ?usize = null,

    menu_open: ?usize = null,
    /// A right-click menu. It and `menu_open` are never open together; they
    /// share the hover, rectangles and painting below.
    context_menu: ?context_menu.Open = null,
    menu_hover_item: ?usize = null,
    menu_title_rects: [menu.menus.len]Rect = undefined,
    menu_item_rects: std.ArrayList(Rect) = .empty,
    menu_rect: Rect = .{},

    status_message: [160]u8 = undefined,
    status_message_len: usize = 0,
    status_message_until: i64 = 0,

    size: platform.Vec2 = .{ .x = 1280, .y = 800 },
    mouse: platform.Vec2 = .{},
    mouse_down_in: enum { none, editor, sidebar_edge, sidebar_strip, splitter, find, overlay, tab, panel, panel_edge } = .none,
    pressed_tab: ?usize = null,

    menubar_rect: Rect = .{},
    toolbar_rect: Rect = .{},
    sidebar_rect: Rect = .{},
    /// The strip that reopens the hidden side bar (empty while it shows).
    sidebar_strip_rect: Rect = .{},
    /// The whole area right of the sidebar holding the groups.
    editor_rect: Rect = .{},
    group_tabbar_rects: [max_groups]Rect = @splat(.{}),
    group_editor_rects: [max_groups]Rect = @splat(.{}),
    find_rect: Rect = .{},
    status_rect: Rect = .{},
    tab_hits: std.ArrayList(TabHit) = .empty,

    // Macro recording (Sublime's ctrl+q / ctrl+shift+q).
    recording: bool = false,
    macro: std.ArrayList(platform.KeyEvent) = .empty,
    macro_arena: std.heap.ArenaAllocator,
    playing_macro: bool = false,

    key_hook: ?KeyHook = null,
    org_keys: org.KeyState = .{},
    /// The targets of an open refile list (org_ui).
    org_refile: ?org_ui.RefileTargets = null,
    /// A capture waiting for its text (org_ui).
    org_capture: ?org_ui.Capture = null,
    /// The agenda to go back to after an action on one of its items.
    org_agenda_return: ?*Editor = null,
    /// An org source block being edited in a tab of its own (org_ui).
    org_src: ?org_ui.SrcEdit = null,
    /// A pending leader key sequence and its which-key popup.
    leader: leader_ui.State = .{},
    /// `:set splitbelow` / `splitright`: new windows go below / right
    /// (window_cmd.zig).
    split_below: bool = false,
    split_right: bool = false,
    /// SPC f R / f D prompts (file_cmd.zig).
    file_cmd: file_cmd.State = .{},
    /// The font size before SPC t b made it bigger.
    big_font_from: ?f32 = null,
    /// Called when a file's last view closes (session clients waiting on it).
    file_closed_hook: ?struct { ctx: *anyopaque, onFileClosed: *const fn (ctx: *anyopaque, path: []const u8) void } = null,
    /// Quit the whole session process, not just close the window.
    quit_session: bool = false,
    /// Remote-SSH connections (remote_ui.zig).
    remote: remote_ui.State = .{},
    lsp: ?*lsp_mod.Lsp = null,
    lsp_state: lsp_ui.State = .{},
    /// The panel under the editors (terminals, language servers).
    panel: panel_ui.State = .{},
    git_state: git_ui.State = .{},
    /// Called when the active editor changes (org agenda, etc. may care).
    should_quit: bool = false,
    dirty: bool = true,
    title_buf: [512]u8 = undefined,

    pub fn init(gpa: Allocator, io: Io, env: *editor_mod.Env, theme: *const Theme, registry: *syntax_mod.Registry, executor: *Executor, keymap: *keymap_mod.Keymap, fonts: ui.Fonts) Workspace {
        return .{
            .gpa = gpa,
            .io = io,
            .env = env,
            .theme = theme,
            .registry = registry,
            .executor = executor,
            .keymap = keymap,
            .fonts = fonts,
            .macro_arena = .init(gpa),
        };
    }

    pub fn deinit(self: *Workspace) void {
        self.shutting_down = true;
        if (self.search_job) |job| job.cancel();
        // Remote calls in flight (a project scan, a search) fail fast from
        // here on, so a stalled link can't hold up the wait below.
        remote_ui.shutdown(self);
        // Let running searches finish (they own a pointer to us, and post to
        // the executor): superseded ones too, which may still be walking.
        while (self.searches_running > 0) {
            self.executor.drain();
            std.Thread.yield() catch {};
        }
        self.result_ranges.deinit(self.gpa);
        if (self.watcher) |*w| w.deinit();
        for (self.reload_queue.items) |p| self.gpa.free(p);
        self.reload_queue.deinit(self.gpa);
        if (self.reload_prompt) |p| self.gpa.free(p);
        // Waits for running git jobs: before lsp_ui, which drops anything they let in.
        git_status_ui.deinit(self);
        git_ui.deinit(self);
        lsp_ui.deinit(self);
        panel_ui.deinit(self);
        if (self.overlay) |o| o.destroy();
        if (self.find) |f| f.destroy();
        for (self.tabs.items) |t| {
            t.editor.destroy();
            if (t.agenda) |a| {
                a.deinit();
                self.gpa.destroy(a);
            }
            if (t.terminal) |tv| tv.destroy();
            if (t.settings) |sv| sv.destroy();
            if (t.git_diff) |d| d.destroy(null);
            if (t.git_commit) |c| c.destroy();
        }
        self.tabs.deinit(self.gpa);
        for (self.buffers.items) |b| {
            b.deinit();
            self.gpa.destroy(b);
        }
        self.buffers.deinit(self.gpa);
        self.mru.deinit(self.gpa);
        self.closed_paths.deinit(self.gpa);
        if (self.project) |p| p.destroy();
        self.sidebar_rows.deinit(self.gpa);
        self.tab_hits.deinit(self.gpa);
        self.menu_item_rects.deinit(self.gpa);
        context_menu.close(self);
        org_ui.clearRefile(self);
        org_ui.clearCapture(self);
        self.macro.deinit(self.gpa);
        self.macro_arena.deinit();
        if (self.restore_selections) |r| self.gpa.free(r);
        // Last: the project and remote terminals use their connections until freed.
        remote_ui.deinit(self);
    }

    // ---- Files and tabs ---------------------------------------------------

    pub fn openProject(self: *Workspace, path: []const u8, blocking: bool) !void {
        if (self.project) |p| p.destroy();
        self.project = try Project.create(self.gpa, self.io, self.executor, path);
        if (self.lsp) |l| l.setRoot(self.project.?.root);
        if (blocking) self.project.?.scanBlocking() else self.project.?.scan();
    }

    /// The view shown in the focused group.
    pub fn activeEditor(self: *Workspace) ?*Editor {
        return self.group_active[self.focused_group];
    }

    /// Index in `tabs` of the active view.
    pub fn activeIndex(self: *Workspace) ?usize {
        const ed = self.activeEditor() orelse return null;
        return self.tabIndexOf(ed);
    }

    pub fn tabIndexOf(self: *Workspace, ed: *Editor) ?usize {
        for (self.tabs.items, 0..) |t, i| if (t.editor == ed) return i;
        return null;
    }

    pub fn groupCount(self: *const Workspace) u8 {
        return self.group_layout.count();
    }

    /// Indices of the tabs in group `g`, in order.
    fn groupTabs(self: *Workspace, g: u8, buf: []usize) []usize {
        var n: usize = 0;
        for (self.tabs.items, 0..) |t, i| if (t.group == g and n < buf.len) {
            buf[n] = i;
            n += 1;
        };
        return buf[0..n];
    }

    /// Adds a tab after the active one in the focused group and activates it.
    pub fn insertTab(self: *Workspace, tab: Tab) !usize {
        var t = tab;
        t.group = self.focused_group;
        const at = if (self.activeIndex()) |i| i + 1 else self.tabs.items.len;
        try self.tabs.insert(self.gpa, at, t);
        self.activateTab(at);
        return at;
    }

    /// The view a group falls back to after losing its active one: its most
    /// recently used remaining tab.
    fn pickGroupActive(self: *Workspace, g: u8, near: usize) ?*Editor {
        for (self.mru.items) |e| {
            if (self.tabIndexOf(e)) |i| if (self.tabs.items[i].group == g) return e;
        }
        var best: ?*Editor = null;
        for (self.tabs.items, 0..) |t, i| if (t.group == g) {
            best = t.editor;
            if (i >= near) break;
        };
        return best;
    }

    /// The editor receiving keystrokes (an input field when a panel has focus).
    pub fn focusedEditor(self: *Workspace) ?*Editor {
        switch (self.focus) {
            .overlay => if (self.overlay) |o| return o.input,
            .find => if (self.find) |f| return f.activeInput(),
            .panel => if (panel_ui.focusedEditor(self)) |ed| return ed,
            .editor => if (settings_ui.focusedInput(self)) |ed| return ed,
        }
        return self.activeEditor();
    }

    fn findTabByPath(self: *Workspace, path: []const u8) ?usize {
        for (self.tabs.items, 0..) |t, i| {
            if (t.editor.buffer.path) |p| if (std.mem.eql(u8, p, path)) return i;
        }
        return null;
    }

    pub fn resolvePath(self: *Workspace, path: []const u8) ![]u8 {
        if (std.fs.path.isAbsolute(path) or rpath.isRemote(path)) return self.gpa.dupe(u8, path);
        if (std.mem.eql(u8, path, "~") or std.mem.startsWith(u8, path, "~/")) if (std.c.getenv("HOME")) |h|
            return std.mem.concat(self.gpa, u8, &.{ std.mem.span(h), path[1..] });
        if (self.project) |p| return std.fs.path.join(self.gpa, &.{ p.root, path });
        var buf: [std.fs.max_path_bytes]u8 = @splat(0); // See `realPath`.
        const n = std.process.currentPath(self.io, &buf) catch return self.gpa.dupe(u8, path);
        return std.fs.path.join(self.gpa, &.{ buf[0..n], path });
    }

    /// `path` absolute, with symlinks resolved; the caller's to free.
    pub fn realPath(self: *Workspace, path: []const u8) ![]u8 {
        // Zeroed: std finds the path's end by scanning the whole buffer, and
        // valgrind would take the length (and all that follows from it) as
        // undefined because of the bytes after the NUL. Likewise for every
        // buffer given to std.process.currentPath.
        var buf: [std.fs.max_path_bytes]u8 = @splat(0);
        const n = try Io.Dir.cwd().realPathFile(self.io, path, &buf);
        return self.gpa.dupe(u8, buf[0..n]);
    }

    pub fn openFile(self: *Workspace, path_in: []const u8, transient: bool) !*Editor {
        const path = try self.resolvePath(path_in);
        defer self.gpa.free(path);
        if (self.findTabByPath(path)) |i| {
            if (!transient) self.tabs.items[i].transient = false;
            self.activateTab(i);
            return self.tabs.items[i].editor;
        }
        const buffer = try self.gpa.create(Buffer);
        errdefer self.gpa.destroy(buffer);
        buffer.* = Buffer.load(self.gpa, self.io, self.next_buffer_id, path) catch |err| switch (err) {
            error.FileNotFound => blk: {
                // Like Sublime: opening a missing path gives an empty buffer that saves there.
                var b = try Buffer.initEmpty(self.gpa, self.next_buffer_id);
                try b.setPath(path);
                break :blk b;
            },
            else => return err,
        };
        self.next_buffer_id += 1;
        buffer.setLanguage(self.registry, languages.forPath(path));
        try self.buffers.append(self.gpa, buffer);
        const editor = try Editor.create(self.gpa, self.env, buffer);
        if (self.lsp) |l| {
            if (self.project == null and l.root == null) l.setRoot(std.fs.path.dirname(path) orelse ".");
            l.openBuffer(buffer);
        }

        // A new preview replaces the previous preview tab.
        if (transient) {
            for (self.tabs.items, 0..) |t, i| if (t.transient) {
                self.closeTabNow(i);
                break;
            };
        }
        _ = try self.insertTab(.{ .editor = editor, .transient = transient });
        return editor;
    }

    /// Opens a command-line argument relative to `cwd`: a folder becomes the
    /// project, a file opens in a tab ("file:12:3" jumps to line 12, column 3).
    /// Returns the absolute path of an opened file.
    pub fn openArgument(self: *Workspace, cwd: []const u8, raw: []const u8) !?[]const u8 {
        if (rpath.isRemote(raw)) return remote_ui.openArgument(self, raw);
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var path = if (std.fs.path.isAbsolute(raw)) raw else try std.fs.path.join(alloc, &.{ cwd, raw });
        var line: ?u32 = null;
        var col: ?u32 = null;
        const exists = if (Io.Dir.cwd().statFile(self.io, path, .{})) |_| true else |_| false;
        if (!exists) {
            // Peel ":line" and ":col" suffixes.
            var parts: [2]?u32 = .{ null, null };
            var n: usize = 0;
            while (n < 2) : (n += 1) {
                const colon = std.mem.lastIndexOfScalar(u8, path, ':') orelse break;
                const num = std.fmt.parseInt(u32, path[colon + 1 ..], 10) catch break;
                parts[n] = num;
                path = path[0..colon];
            }
            if (n == 2) {
                line = parts[1];
                col = parts[0];
            } else if (n == 1) line = parts[0];
        }
        const stat = Io.Dir.cwd().statFile(self.io, path, .{}) catch null;
        if (stat != null and stat.?.kind == .directory) {
            const abs = try self.realPath(path);
            defer self.gpa.free(abs);
            if (self.project == null or !std.mem.eql(u8, self.project.?.root, abs)) try self.openProject(abs, false);
            return null;
        }
        const ed = try self.openFile(path, false);
        if (line) |l| {
            const row = @min(ed.buffer.lineCount() - 1, l -| 1);
            const start = ed.buffer.lineStart(row);
            const off = @min(start + (col orelse 1) -| 1, ed.buffer.lineEnd(row));
            ed.setSingleSelection(.caret(off));
            ed.afterCommand(true);
        }
        return ed.buffer.path;
    }

    /// File ▸ Open Folder…: makes the folder the project shown in the side bar
    /// (a side bar the user hid stays hidden). Open tabs stay open.
    fn openFolder(self: *Workspace, input: []const u8) void {
        if (rpath.isRemote(input)) {
            _ = remote_ui.openArgument(self, input) catch |err| self.setStatus("Unable to open {s}: {t}", .{ input, err });
            return;
        }
        const path = self.resolvePath(input) catch return;
        defer self.gpa.free(path);
        const stat = Io.Dir.cwd().statFile(self.io, path, .{}) catch |err| {
            self.setStatus("Unable to open {s}: {t}", .{ path, err });
            return;
        };
        if (stat.kind != .directory) {
            self.setStatus("Not a folder: {s}", .{path});
            return;
        }
        const abs = self.realPath(path) catch |err| {
            self.setStatus("Unable to open {s}: {t}", .{ path, err });
            return;
        };
        defer self.gpa.free(abs);
        if (self.project == null or !std.mem.eql(u8, self.project.?.root, abs)) {
            self.openProject(abs, false) catch |err| {
                self.setStatus("Unable to open {s}: {t}", .{ abs, err });
                return;
            };
        }
        self.root_expanded = true;
        self.updateTitle();
    }

    /// Drops IME compositions everywhere (the focused text field changed).
    pub fn clearPreedits(self: *Workspace) void {
        for (self.tabs.items) |t| t.editor.setPreedit("");
        if (self.find) |f| {
            f.find_input.setPreedit("");
            f.replace_input.setPreedit("");
            f.where_input.setPreedit("");
        }
        if (self.overlay) |o| o.input.setPreedit("");
    }

    /// The primary caret of `ed` in window coordinates (IME candidate placement).
    pub fn caretRect(self: *Workspace, ed: *Editor) platform.Rect {
        _ = self;
        const head = ed.primarySelection().head;
        const lh = ed.lineHeight();
        const x = ed.screenX(ed.xForOffset(head));
        const y = if (ed.mode == .single_line)
            ed.text_rect.y + (ed.text_rect.h - lh) / 2
        else
            ed.text_rect.y + @as(f32, @floatFromInt(ed.displayRowOfOffset(head))) * lh - ed.scroll_y;
        return .{ .x = x, .y = y, .w = 2, .h = lh };
    }

    /// Extra fds for the event loop: the file watcher (first), then terminals.
    pub fn extraPollFds(self: *Workspace, out: *std.ArrayList(platform.PollFd)) void {
        if (self.watcher == null and !self.watcher_failed) {
            self.watcher = FileWatcher.init(self.gpa) catch blk: {
                self.watcher_failed = true;
                break :blk null;
            };
        }
        if (self.watcher) |*w| {
            self.syncWatches(w);
            out.append(self.gpa, .{ .fd = w.fd }) catch {};
        }
        terminal_ui.pollFds(self, out);
    }

    /// Watches the folders of open files (re-synced when the set of paths changes).
    fn syncWatches(self: *Workspace, w: *FileWatcher) void {
        var h = std.hash.Wyhash.init(0);
        for (self.buffers.items) |b| if (b.path) |p| h.update(p);
        const sig = h.final();
        if (sig == self.watched_signature) return;
        self.watched_signature = sig;
        var dirs: std.ArrayList([]const u8) = .empty;
        defer dirs.deinit(self.gpa);
        for (self.buffers.items) |b| {
            const dir = std.fs.path.dirname(b.path orelse continue) orelse continue;
            for (dirs.items) |d| {
                if (std.mem.eql(u8, d, dir)) break;
            } else dirs.append(self.gpa, dir) catch {};
        }
        // ssh:// folders are watched by jiffy-edit-server on their host.
        vfs.syncRemoteWatches(self.gpa, dirs.items);
        var local: std.ArrayList([]const u8) = .empty;
        defer local.deinit(self.gpa);
        for (dirs.items) |d| if (!rpath.isRemote(d)) local.append(self.gpa, d) catch {};
        w.sync(local.items);
    }

    pub fn serviceExtraFds(self: *Workspace, fds: []const platform.PollFd) void {
        terminal_ui.service(self, fds);
        if (fds.len == 0 or !fds[0].readable()) return;
        const w = &(self.watcher orelse return);
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        var changes: std.ArrayList(FileWatcher.Change) = .empty;
        w.read(arena.allocator(), &changes);
        for (changes.items, 0..) |ch, i| {
            // Several events per save are common; handle each path once.
            const dup = for (changes.items[i + 1 ..]) |later| {
                if (std.mem.eql(u8, later.path, ch.path)) break true;
            } else false;
            if (dup) continue;
            git_status_ui.onDiskChange(self, ch.path);
            for (self.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, ch.path)) {
                self.checkDiskChange(b);
                break;
            };
        }
    }

    /// A watched file changed: reload a clean buffer in place, ask about a
    /// dirty one, and mark one whose file disappeared as modified.
    pub fn checkDiskChange(self: *Workspace, buffer: *Buffer) void {
        const path = buffer.path orelse return;
        const raw = vfs.readFile(self.gpa, self.io, path, 1 << 30) catch |err| {
            if (err == error.FileNotFound) {
                if (!buffer.isDirty()) {
                    buffer.saved_version = std.math.maxInt(u64);
                    self.setStatus("{s} was deleted on disk", .{buffer.fileName() orelse path});
                }
                self.dirty = true;
            }
            return;
        };
        defer self.gpa.free(raw);
        var line_ending: buffer_mod.LineEnding = .lf;
        const text = buffer_mod.normalizeText(self.gpa, raw, &line_ending) catch return;
        defer self.gpa.free(text);
        const current = buffer.rope.toOwnedSlice(self.gpa) catch return;
        defer self.gpa.free(current);
        // Our own save (or a touch) leaves the contents unchanged.
        if (std.mem.eql(u8, current, text)) {
            if (buffer.saved_version == std.math.maxInt(u64)) buffer.saved_version = buffer.version;
            return;
        }
        if (buffer.isDirty()) {
            for (self.reload_queue.items) |q| if (std.mem.eql(u8, q, path)) return;
            if (self.reload_prompt) |rp| if (std.mem.eql(u8, rp, path)) return;
            self.reload_queue.append(self.gpa, self.gpa.dupe(u8, path) catch return) catch {};
            self.nextReloadPrompt();
            return;
        }
        self.reloadBuffer(buffer, text);
        self.setStatus("Reloaded {s}", .{buffer.fileName() orelse path});
    }

    fn reloadBuffer(self: *Workspace, buffer: *Buffer, text: []const u8) void {
        // Any view of the buffer applies the edit; the others follow via listeners.
        for (self.tabs.items) |t| if (t.editor.buffer == buffer) {
            t.editor.replaceContentsMinimal(text);
            break;
        };
        buffer.saved_version = buffer.version;
        if (self.lsp) |l| l.savedBuffer(buffer);
        self.dirty = true;
    }

    /// Asks about the next queued file (one prompt at a time, and never over
    /// another overlay).
    fn nextReloadPrompt(self: *Workspace) void {
        if (self.overlay != null) return;
        // A prompt closed without an answer (Escape) keeps the edits.
        if (self.reload_prompt) |stale| {
            self.gpa.free(stale);
            self.reload_prompt = null;
        }
        const path = self.reload_queue.pop() orelse return;
        self.reload_prompt = path;
        const name = std.fs.path.basename(path);
        self.openList(.list, std.fmt.allocPrint(self.gpa, "{s} changed on disk. Reload it and lose your unsaved changes?", .{name}) catch "Save changes?", @intFromEnum(PromptTag.confirm_reload), &.{ "Reload", "Keep My Changes" });
    }

    /// After the paths on the command line: with none, the org agenda if
    /// "org_agenda_on_startup" is set; an empty file if nothing is open.
    pub fn openStartTabs(self: *Workspace, opened_paths: bool) !void {
        if (!opened_paths and self.env.settings.org_agenda_on_startup) org_ui.openAgenda(self, .week);
        if (self.tabs.items.len == 0) _ = try self.newFile();
    }

    pub fn newFile(self: *Workspace) !*Editor {
        const buffer = try self.gpa.create(Buffer);
        buffer.* = try Buffer.initEmpty(self.gpa, self.next_buffer_id);
        self.next_buffer_id += 1;
        try self.buffers.append(self.gpa, buffer);
        const editor = try Editor.create(self.gpa, self.env, buffer);
        _ = try self.insertTab(.{ .editor = editor });
        return editor;
    }

    // ---- Groups (split panes) ----------------------------------------------

    /// Switches layout; views in groups that no longer exist join the last group.
    pub fn setGroupLayout(self: *Workspace, layout_kind: GroupLayout) void {
        const n = layout_kind.count();
        self.group_layout = groups.preset(layout_kind);
        self.zoomed_group = null;
        for (self.tabs.items) |*t| if (t.group >= n) {
            t.group = n - 1;
        };
        var g: usize = n;
        while (g < max_groups) : (g += 1) self.group_active[g] = null;
        if (self.focused_group >= n) self.focused_group = n - 1;
        if (self.prev_focused_group) |p| if (p >= n) {
            self.prev_focused_group = null;
        };
        // The merged group keeps (or gains) an active view.
        if (self.group_active[n - 1] == null) self.group_active[n - 1] = self.pickGroupActive(n - 1, 0);
        if (self.activeEditor()) |ed| if (self.tabIndexOf(ed)) |i| self.activateTab(i);
        self.dirty = true;
    }

    pub fn focusGroup(self: *Workspace, g: u8) void {
        if (g >= self.groupCount()) return;
        if (self.activeEditor()) |prev| prev.focused = false;
        if (g != self.focused_group) self.prev_focused_group = self.focused_group;
        self.focused_group = g;
        self.focus = .editor;
        if (self.activeIndex()) |i| self.activateTab(i) else self.updateTitle();
    }

    /// Moves a view to the end of group `g` and focuses it there.
    pub fn moveTabToGroup(self: *Workspace, index: usize, g: u8) void {
        if (g >= self.groupCount() or index >= self.tabs.items.len) return;
        var tab = self.tabs.orderedRemove(index);
        const from = tab.group;
        if (from == g) {
            self.tabs.insert(self.gpa, index, tab) catch {};
            return;
        }
        tab.group = g;
        self.tabs.append(self.gpa, tab) catch return;
        if (self.group_active[from] == tab.editor) self.group_active[from] = self.pickGroupActive(from, index);
        self.activateTab(self.tabs.items.len - 1);
    }

    /// ctrl+k ctrl+up: add a pane (a column beside a single group) and move
    /// the current view into it.
    fn newPane(self: *Workspace) void {
        const ed = self.activeEditor();
        if (!groups.addPane(self)) return;
        if (ed) |e| if (self.tabIndexOf(e)) |i| self.moveTabToGroup(i, self.focused_group);
    }

    /// ctrl+k ctrl+down: merge the focused group into its neighbour.
    fn closePane(self: *Workspace) void {
        _ = groups.closeWindow(self, self.focused_group, .hide);
    }

    /// File > New View into File: a second editor on the same buffer.
    pub fn cloneView(self: *Workspace) void {
        const src = self.activeEditor() orelse return;
        _ = self.cloneViewOf(src);
    }

    /// A new view of `src`'s buffer in the focused group, with its selections and scroll.
    pub fn cloneViewOf(self: *Workspace, src: *Editor) ?*Editor {
        const ed = Editor.create(self.gpa, self.env, src.buffer) catch return null;
        ed.setSelections(src.selections.items);
        ed.scroll_y = src.scroll_y;
        _ = self.insertTab(.{ .editor = ed }) catch {
            ed.destroy();
            return null;
        };
        return ed;
    }

    pub fn activateTab(self: *Workspace, index: usize) void {
        if (index >= self.tabs.items.len) return;
        if (self.activeEditor()) |prev| {
            prev.buffer.breakUndoGroup();
            prev.focused = false;
        }
        const tab = self.tabs.items[index];
        if (tab.group != self.focused_group) self.prev_focused_group = self.focused_group;
        self.focused_group = tab.group;
        self.group_active[tab.group] = tab.editor;
        const ed = tab.editor;
        // Most-recently-used order for ctrl+tab.
        for (self.mru.items, 0..) |e, i| if (e == ed) {
            _ = self.mru.orderedRemove(i);
            break;
        };
        self.mru.insert(self.gpa, 0, ed) catch {};
        ed.touchCaret();
        if (self.focus == .editor) ed.focused = true;
        self.updateTitle();
        self.revealActiveInSidebar(false);
    }

    pub fn closeTabNow(self: *Workspace, index: usize) void {
        const tab = self.tabs.orderedRemove(index);
        const ed = tab.editor;
        org_ui.tabClosed(self, ed);
        if (tab.agenda) |a| {
            a.deinit();
            self.gpa.destroy(a);
        }
        if (tab.terminal) |tv| tv.destroy();
        if (tab.settings) |sv| sv.destroy();
        if (tab.git_diff) |d| d.destroy(ed.buffer);
        if (tab.git_commit) |c| git_status_ui.commitTabClosed(self, c, ed.buffer);
        for (self.mru.items, 0..) |e, i| if (e == ed) {
            _ = self.mru.orderedRemove(i);
            break;
        };
        const buffer = ed.buffer;
        // Count views by tab: language servers listen to the buffer too.
        const last_view = groups.soleView(self, ed);
        // Closing one of several views (a vim window) closes no file.
        if (last_view) if (buffer.path) |p| self.closed_paths.record(self.gpa, p);
        ed.destroy();
        // Free the buffer when no other view uses it.
        if (last_view) if (self.lsp) |l| l.closeBuffer(buffer);
        if (buffer.listeners.items.len == 0) {
            if (self.file_closed_hook) |h| if (buffer.path) |p| h.onFileClosed(h.ctx, p);
            for (self.buffers.items, 0..) |b, i| if (b == buffer) {
                _ = self.buffers.swapRemove(i);
                break;
            };
            buffer.deinit();
            self.gpa.destroy(buffer);
        }
        if (self.group_active[tab.group] == ed) {
            self.group_active[tab.group] = self.pickGroupActive(tab.group, index);
        }
        if (self.mouse_editor == ed) self.mouse_editor = null;
        if (self.find) |f| f.searched_buffer = null;
        if (self.resultsTab() == null) {
            self.result_ranges.clearRetainingCapacity();
            self.result_index = null;
        }
        self.updateTitle();
    }

    pub fn closeTab(self: *Workspace, index: usize) void {
        if (index >= self.tabs.items.len) return;
        const ed = self.tabs.items[index].editor;
        if (ed.buffer.isDirty() and groups.soleView(self, ed)) {
            self.pending_close = index;
            self.activateTab(index);
            const name = ed.buffer.fileName() orelse "untitled";
            self.openList(.list, std.fmt.allocPrint(self.gpa, "Save changes to {s} before closing?", .{name}) catch "Save changes?", @intFromEnum(PromptTag.confirm_close), &.{ "Save", "Don't Save", "Cancel" });
            return;
        }
        self.closeTabNow(index);
    }

    /// SPC b O: closes the focused group's other tabs; those with unsaved
    /// changes stay, as with Close All.
    fn closeOtherTabs(self: *Workspace) void {
        const keep = self.activeEditor() orelse return;
        var kept: usize = 0;
        var i = self.tabs.items.len;
        while (i > 0) {
            i -= 1;
            const t = self.tabs.items[i];
            if (t.group != self.focused_group or t.editor == keep) continue;
            if (t.editor.buffer.isDirty()) kept += 1 else self.closeTabNow(i);
        }
        if (kept > 0) self.setStatus("{d} tab(s) with unsaved changes stay open", .{kept});
    }

    fn saveEditor(self: *Workspace, ed: *Editor) void {
        if (ed.buffer.path == null) {
            self.promptSaveAs(self.defaultDir());
            return;
        }
        ed.buffer.save(self.io) catch |err| {
            self.setStatus("Unable to save {s}: {t}", .{ ed.buffer.path.?, err });
            return;
        };
        for (self.tabs.items) |*t| if (t.editor.buffer == ed.buffer) {
            t.transient = false;
        };
        self.setStatus("Saved {s}", .{ed.buffer.path.?});
        if (self.prefs) |pr| {
            var pbuf: [std.fs.max_path_bytes]u8 = undefined;
            var kbuf: [std.fs.max_path_bytes]u8 = undefined;
            const is_settings = if (pr.settingsPath(pr.ctx, &pbuf)) |sp| std.mem.eql(u8, sp, ed.buffer.path.?) else false;
            const is_keymap = if (keymap_mod.Keymap.userPath(&kbuf)) |kp| std.mem.eql(u8, kp, ed.buffer.path.?) else false;
            if (is_settings or is_keymap) self.reloadConfig(if (is_keymap) "Key bindings" else "Settings");
        }
        if (self.lsp) |l| {
            if (!l.hasServer(ed.buffer)) l.openBuffer(ed.buffer) else l.savedBuffer(ed.buffer);
        }
        if (self.project) |p| if (p.relativePath(ed.buffer.path.?) != null and p.findEntry(p.relativePath(ed.buffer.path.?).?) == null) p.scan();
    }

    /// Reads settings.json and keymap.json again; says `what` was reloaded,
    /// or what is wrong in keymap.json.
    fn reloadConfig(self: *Workspace, what: []const u8) void {
        const pr = self.prefs orelse return;
        pr.reloadSettings(pr.ctx);
        if (self.keymap.warnings.items.len > 0) {
            self.setStatus("keymap.json: {s}", .{self.keymap.warnings.items[0..@min(self.keymap.warnings.items.len, 140)]});
        } else self.setStatus("{s} reloaded", .{what});
    }

    /// The folder of the active view's file.
    pub fn activeFolder(self: *Workspace) ?[]const u8 {
        const ed = self.activeEditor() orelse return null;
        return std.fs.path.dirname(ed.buffer.path orelse return null);
    }

    fn defaultDir(self: *Workspace) []const u8 {
        if (self.activeEditor()) |ed| if (ed.buffer.path) |p| return p;
        if (self.project) |p| return p.root;
        return "";
    }

    /// Takes the side bar's visibility and width from the last run (state.json).
    pub fn loadUiState(self: *Workspace) void {
        const st = ui_state.load(self.gpa, self.io) orelse ui_state.State{ .sidebar_visible = self.sidebar_visible, .sidebar_width = self.sidebar_width };
        self.sidebar_visible = st.sidebar_visible;
        self.sidebar_width = @max(sidebar_min_w, st.sidebar_width);
        self.saved_ui_state = st;
    }

    /// Writes state.json when the side bar differs from what it holds.
    fn saveUiState(self: *Workspace) void {
        const st: ui_state.State = .{ .sidebar_visible = self.sidebar_visible, .sidebar_width = self.sidebar_width };
        if (self.saved_ui_state) |saved| if (std.meta.eql(saved, st)) return;
        ui_state.save(self.gpa, self.io, st) catch |err| return log.warn("state.json: {t}", .{err});
        self.saved_ui_state = st;
    }

    /// Shows or hides the side bar, remembered for the next start.
    fn setSidebarVisible(self: *Workspace, visible: bool) void {
        self.sidebar_visible = visible;
        self.saveUiState();
    }

    /// Dragging the side bar's edge (or out of the strip) to `x`: below half
    /// the minimum width it collapses; dragged back out, it shows again at
    /// the dragged width.
    fn dragSidebarEdge(self: *Workspace, x: f32) void {
        const st = sidebarDrag(self.sidebar_drag_from, self.size.x - x, self.size.x);
        self.sidebar_visible = st.sidebar_visible;
        self.sidebar_width = st.sidebar_width;
    }

    fn revealActiveInSidebar(self: *Workspace, expand: bool) void {
        const ed = self.activeEditor() orelse return;
        const path = ed.buffer.path orelse return;
        const project = self.project orelse return;
        const rel = project.relativePath(path) orelse return;
        if (expand) {
            project.reveal(rel);
            self.root_expanded = true;
        }
    }

    pub fn updateTitle(self: *Workspace) void {
        const host = self.host orelse return;
        var w: std.Io.Writer = .fixed(self.title_buf[0 .. self.title_buf.len - 1]);
        if (self.activeEditor()) |ed| {
            if (ed.buffer.path) |p| w.writeAll(p) catch {} else w.writeAll(ed.buffer.fileName() orelse "untitled") catch {};
            if (ed.buffer.isDirty()) w.writeAll(" •") catch {};
        }
        if (self.project) |p| w.print(" ({s})", .{p.name}) catch {};
        if (remote_ui.currentHost(self)) |h| w.print(" [SSH: {s}]", .{h}) catch {};
        w.writeAll(" - CS Zig") catch {};
        const n = w.buffered().len;
        self.title_buf[n] = 0;
        host.setTitle(host.ctx, self.title_buf[0..n :0]);
    }

    pub fn setStatus(self: *Workspace, comptime fmt: []const u8, args: anytype) void {
        const msg = std.fmt.bufPrint(&self.status_message, fmt, args) catch self.status_message[0..];
        self.status_message_len = msg.len;
        self.status_message_until = platform.nowMs() + 4000;
        self.dirty = true;
    }

    // ---- Overlays ---------------------------------------------------------

    fn closeOverlay(self: *Workspace) void {
        if (self.overlay) |o| o.destroy();
        self.overlay = null;
        self.focus = if (self.find_visible) .find else .editor;
        if (self.focus == .editor) self.focus = .editor;
        self.closeMenus();
    }

    pub fn newOverlay(self: *Workspace, kind: quick_panel.Kind) ?*QuickPanel {
        if (self.overlay) |o| o.destroy();
        self.overlay = QuickPanel.create(self.gpa, self.env, kind) catch return null;
        self.focus = .overlay;
        return self.overlay;
    }

    pub fn openGotoAnything(self: *Workspace, initial: []const u8) void {
        const o = self.newOverlay(.goto_anything) orelse return;
        o.input.placeholder = "Goto Anything — file, :line, @symbol";
        if (self.project) |p| if (p.snapshot) |snap| {
            for (snap.files) |idx| {
                const e = snap.entries[idx];
                o.addItem(.{ .title = e.name, .detail = e.path, .payload = .{ .file = idx } }) catch break;
            }
        };
        if (initial.len > 0) o.input.insertText(initial);
        self.saveRestorePoint();
    }

    pub fn openCommandPalette(self: *Workspace) void {
        const o = self.newOverlay(.command_palette) orelse return;
        o.input.placeholder = "Command Palette";
        var buf: [64]u8 = undefined;
        for (commands.all) |cmd| {
            const shortcut = self.keymap.shortcutFor(cmd.action, &buf) orelse "";
            o.addItem(.{ .title = cmd.caption, .shortcut = o.arena().dupe(u8, shortcut) catch "", .payload = .{ .action = cmd.action } }) catch break;
        }
        for (&languages.all) |*lang| {
            const caption = std.fmt.allocPrint(o.arena(), "Set Syntax: {s}", .{lang.name}) catch continue;
            o.addItem(.{ .title = caption, .payload = .{ .syntax = lang } }) catch break;
        }
        o.addItem(.{ .title = "Set Syntax: Plain Text", .payload = .{ .syntax = &languages.plain_text } }) catch {};
    }

    fn openSchemePicker(self: *Workspace) void {
        const pr = self.prefs orelse return;
        const o = self.newOverlay(.list) orelse return;
        o.prompt_label = "Select Color Scheme";
        o.prompt_tag = @intFromEnum(PromptTag.select_scheme);
        o.input.placeholder = "Filter";
        var buf: [128]u8 = undefined;
        const auto_detail = o.arena().dupe(u8, pr.describeAuto(pr.ctx, &buf)) catch "";
        o.addItem(.{ .title = "Auto", .detail = auto_detail, .payload = .{ .value = 0 } }) catch {};
        for (pr.schemes(pr.ctx), 0..) |sch, i| {
            o.addItem(.{ .title = sch.name, .detail = if (sch.light) "light" else "dark", .payload = .{ .value = i + 1 } }) catch {};
        }
        _ = o.refilter();
        // Start on the active scheme.
        for (o.filtered.items, 0..) |f, i| {
            if (std.ascii.eqlIgnoreCase(o.items.items[f.index].title, self.theme.name)) o.selected = i;
        }
    }

    /// Live preview while moving through the scheme picker.
    fn previewSelectedScheme(self: *Workspace) void {
        const o = self.overlay orelse return;
        if (o.prompt_tag != @intFromEnum(PromptTag.select_scheme)) return;
        const pr = self.prefs orelse return;
        const item = o.selectedItem() orelse return;
        pr.previewScheme(pr.ctx, if (std.mem.eql(u8, item.title, "Auto")) null else item.title);
    }

    fn openGotoLine(self: *Workspace) void {
        if (self.activeEditor() == null) return;
        const o = self.newOverlay(.goto_line) orelse return;
        o.input.placeholder = "Goto Line — type a line number";
        self.saveRestorePoint();
    }

    fn openGotoSymbol(self: *Workspace) void {
        const ed = self.activeEditor() orelse return;
        const o = self.newOverlay(.goto_symbol) orelse return;
        o.input.placeholder = "Goto Symbol";
        fillSymbols(o, ed);
        self.saveRestorePoint();
    }

    fn fillSymbols(o: *QuickPanel, ed: *Editor) void {
        var list = symbols.extract(o.gpa, ed.buffer) catch return;
        defer list.deinit(o.gpa);
        for (list.items) |sym| {
            const p = ed.buffer.offsetToPoint(sym.offset);
            o.addItem(.{
                .title = o.arena().dupe(u8, sym.name) catch continue,
                .detail = std.fmt.allocPrint(o.arena(), "line {d}", .{p.row + 1}) catch "",
                .payload = .{ .offset = sym.offset },
            }) catch break;
        }
        for (list.items) |sym| o.gpa.free(sym.name);
    }

    pub fn openLocationPanel(self: *Workspace, label: []const u8) ?*QuickPanel {
        const o = self.newOverlay(.locations) orelse return null;
        o.prompt_label = label;
        o.input.placeholder = "Filter";
        return o;
    }

    /// File ▸ Open File… and Open Folder…: path prompts starting in the
    /// project root (else, for a folder, the current file's folder);
    /// `.file_here` (SPC .) starts in the current file's folder.
    fn promptOpenPath(self: *Workspace, what: enum { file, file_here, folder }) void {
        const here = self.activeFolder();
        const start = (if (what == .file_here) here else null) orelse if (self.project) |p| p.root else if (what == .folder) here orelse "" else "";
        self.promptPathIn(what == .folder, start);
    }

    /// An Open File (`folder`: Open Folder) prompt listing `start`.
    fn promptPathIn(self: *Workspace, folder: bool, start: []const u8) void {
        var buf: [std.fs.max_path_bytes + 1]u8 = undefined;
        // A trailing '/' lists the folder's entries straight away.
        const initial = if (start.len == 0 or start[start.len - 1] == '/') start else std.fmt.bufPrint(&buf, "{s}/", .{start}) catch start;
        if (folder) {
            self.openPrompt("Open Folder:", @intFromEnum(PromptTag.open_folder), initial);
        } else self.openPrompt("Open File:", @intFromEnum(PromptTag.open_path), initial);
        path_complete.begin(self, .{ .dirs_only = folder });
    }

    fn promptSaveAs(self: *Workspace, initial: []const u8) void {
        self.openPrompt("Save As:", @intFromEnum(PromptTag.save_as), initial);
        path_complete.begin(self, .{});
    }

    pub fn openPrompt(self: *Workspace, label: []const u8, tag: u32, initial: []const u8) void {
        const o = self.newOverlay(.prompt) orelse return;
        // Painted every frame: `label` may be the caller's stack buffer.
        o.prompt_label = o.arena().dupe(u8, label) catch "";
        o.prompt_tag = tag;
        if (initial.len > 0) o.input.setText(initial, false);
    }

    pub fn openList(self: *Workspace, kind: quick_panel.Kind, label: []const u8, tag: u32, items: []const []const u8) void {
        // `label` is the caller's heap copy (taken), or the "Save changes?"
        // fallback when it couldn't make one; the panel keeps its own.
        defer if (label.ptr != "Save changes?".ptr) self.gpa.free(label);
        const o = self.newOverlay(kind) orelse return;
        o.prompt_label = o.arena().dupe(u8, label) catch "";
        o.prompt_tag = tag;
        for (items, 0..) |it, i| o.addItem(.{ .title = it, .payload = .{ .value = i } }) catch {};
    }

    fn saveRestorePoint(self: *Workspace) void {
        const ed = self.activeEditor() orelse return;
        if (self.restore_selections) |r| self.gpa.free(r);
        self.restore_selections = self.gpa.dupe(editor_mod.Selection, ed.selections.items) catch null;
        self.restore_scroll = ed.scroll_y;
    }

    fn restore(self: *Workspace) void {
        const ed = self.activeEditor() orelse return;
        if (self.restore_selections) |r| {
            ed.setSelections(r);
            ed.scroll_y = self.restore_scroll;
            self.gpa.free(r);
            self.restore_selections = null;
        }
    }

    fn dropRestorePoint(self: *Workspace) void {
        if (self.restore_selections) |r| self.gpa.free(r);
        self.restore_selections = null;
    }

    /// Live previews while typing in Goto Line / Goto Anything (":42", "@sym").
    fn overlayInputChanged(self: *Workspace) void {
        const o = self.overlay orelse return;
        if (o.path_mode != null) {
            path_complete.tidy(self, o);
            path_complete.list(self, o);
        }
        _ = o.refilter();
        const q = o.last_query.items;
        const ed = self.activeEditor() orelse return;
        switch (o.kind) {
            .goto_line => gotoLinePreview(ed, q),
            .goto_anything => {
                if (std.mem.indexOfScalar(u8, q, ':')) |i| {
                    if (i == 0) gotoLinePreview(ed, q[1..]);
                } else if (std.mem.indexOfScalar(u8, q, '@')) |i| {
                    if (i == 0 and o.items.items.len > 0 and o.items.items[0].payload == .file) {
                        // Switch to symbol mode for the current file.
                        o.clearItems();
                        o.kind = .goto_anything;
                        fillSymbols(o, ed);
                    }
                    self.symbolFilter(o, q[i + 1 ..]);
                }
            },
            .goto_symbol => self.previewSymbol(o),
            else => {},
        }
    }

    fn symbolFilter(self: *Workspace, o: *QuickPanel, q: []const u8) void {
        _ = self;
        o.filtered.clearRetainingCapacity();
        for (o.items.items, 0..) |item, i| {
            if (item.payload != .offset) continue;
            const m = o.matcher.match(q, item.title, false) orelse continue;
            const pos = o.filter_arena.allocator().dupe(u16, m.positions) catch continue;
            o.filtered.append(o.gpa, .{ .index = @intCast(i), .score = m.score, .positions = pos }) catch break;
        }
        if (q.len > 0) std.mem.sort(@TypeOf(o.filtered.items[0]), o.filtered.items, {}, struct {
            fn lt(_: void, a: @TypeOf(o.filtered.items[0]), b: @TypeOf(o.filtered.items[0])) bool {
                return a.score > b.score;
            }
        }.lt);
        o.selected = 0;
        o.scroll = 0;
    }

    fn previewSymbol(self: *Workspace, o: *QuickPanel) void {
        const ed = self.activeEditor() orelse return;
        const item = o.selectedItem() orelse return;
        if (item.payload == .offset) {
            ed.setSingleSelection(.caret(item.payload.offset));
            ed.ensureCaretVisible(true);
        }
    }

    fn gotoLinePreview(ed: *Editor, q: []const u8) void {
        const trimmed = std.mem.trim(u8, q, " ");
        const line = std.fmt.parseInt(u32, trimmed, 10) catch return;
        const row = @min(ed.buffer.lineCount() - 1, line -| 1);
        ed.setSingleSelection(.caret(ed.buffer.lineStart(row)));
        ed.ensureCaretVisible(true);
    }

    fn confirmOverlay(self: *Workspace) void {
        const o = self.overlay orelse return;
        // A path prompt may take the picked entry, or list a folder instead.
        if (o.path_mode != null and path_complete.confirm(self, o)) return;
        _ = o.refilter();
        const q = self.gpa.dupe(u8, o.last_query.items) catch return;
        defer self.gpa.free(q);
        const item = o.selectedItem();
        const kind = o.kind;
        const tag = o.prompt_tag;
        self.dropRestorePoint();
        // Closed, but freed only once the item has been acted on: its strings
        // (a location's path, a host's name) live in the panel's arena.
        self.overlay = null;
        self.closeOverlay();
        defer o.destroy();
        switch (kind) {
            .goto_line => {},
            .goto_symbol => {},
            .goto_anything => {
                if (item) |it| switch (it.payload) {
                    .file => |idx| {
                        const p = self.project.?;
                        const abs = p.absolutePath(self.gpa, p.snapshot.?.entries[idx].path) catch return;
                        defer self.gpa.free(abs);
                        const ed = self.openFile(abs, false) catch |err| {
                            self.setStatus("Unable to open: {t}", .{err});
                            return;
                        };
                        // "file:42" jumps to the line.
                        if (std.mem.indexOfScalar(u8, q, ':')) |i| gotoLinePreview(ed, q[i + 1 ..]);
                    },
                    .offset => {},
                    else => {},
                };
            },
            .command_palette => {
                if (item) |it| switch (it.payload) {
                    .action => |a| self.perform(a),
                    .syntax => |lang| if (self.activeEditor()) |ed| {
                        if (self.lsp) |l| l.closeBuffer(ed.buffer);
                        ed.buffer.setLanguage(self.registry, lang);
                        if (self.lsp) |l| l.openBuffer(ed.buffer);
                    },
                    else => {},
                };
            },
            .prompt => if (!org_ui.confirmPrompt(self, tag, q) and !remote_ui.confirmPrompt(self, tag, q) and !panel_ui.confirmPrompt(self, tag, q) and !git_status_ui.confirmPrompt(self, tag, q) and !file_cmd.confirmPrompt(self, tag, q)) self.confirmPrompt(@enumFromInt(tag), q),
            .locations => if (item) |it| switch (it.payload) {
                .location => |loc| lsp_ui.confirmLocation(self, loc.path, loc.line, loc.character, loc.utf8),
                else => {},
            },
            .list => if (!lsp_ui.confirmList(self, tag, if (item) |it| it.payload.value else null) and !org_ui.confirmList(self, tag, if (item) |it| it.payload.value else null) and !remote_ui.confirmList(self, tag, item, q) and !git_status_ui.confirmList(self, tag, item, q) and !file_cmd.confirmList(self, tag, item)) self.confirmList(@enumFromInt(tag), if (item) |it| it.payload.value else 2),
        }
    }

    fn confirmPrompt(self: *Workspace, tag: PromptTag, input: []const u8) void {
        const trimmed = std.mem.trim(u8, input, " ");
        if (trimmed.len == 0) return;
        switch (tag) {
            .save_as => {
                const ed = self.activeEditor() orelse return;
                const path = self.resolvePath(trimmed) catch return;
                defer self.gpa.free(path);
                ed.buffer.setPath(path) catch return;
                ed.buffer.setLanguage(self.registry, languages.forPath(path));
                self.saveEditor(ed);
                self.updateTitle();
            },
            .open_path => {
                _ = self.openFile(trimmed, false) catch |err| self.setStatus("Unable to open {s}: {t}", .{ trimmed, err });
            },
            .open_folder => self.openFolder(trimmed),
            .rename => {
                const ed = self.activeEditor() orelse return;
                const l = self.lsp orelse return;
                if (!l.rename(ed.buffer, self.lsp_state.pending_rename_offset, trimmed)) self.setStatus("No language server for this file", .{});
            },
            else => {},
        }
    }

    fn confirmList(self: *Workspace, tag: PromptTag, choice: usize) void {
        switch (tag) {
            .confirm_close => {
                const index = self.pending_close orelse return;
                self.pending_close = null;
                if (index >= self.tabs.items.len) return;
                switch (choice) {
                    0 => {
                        const ed = self.tabs.items[index].editor;
                        self.saveEditor(ed);
                        if (!ed.buffer.isDirty()) self.closeTabNow(index);
                    },
                    1 => self.closeTabNow(index),
                    else => {},
                }
            },
            .select_scheme => {},
            .confirm_reload => {
                const path = self.reload_prompt orelse return;
                defer {
                    self.gpa.free(path);
                    self.reload_prompt = null;
                    self.nextReloadPrompt();
                }
                if (choice != 0) return;
                for (self.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, path)) {
                    const raw = vfs.readFile(self.gpa, self.io, path, 1 << 30) catch return;
                    defer self.gpa.free(raw);
                    var line_ending: buffer_mod.LineEnding = .lf;
                    const text = buffer_mod.normalizeText(self.gpa, raw, &line_ending) catch return;
                    defer self.gpa.free(text);
                    self.reloadBuffer(b, text);
                    break;
                };
            },
            .confirm_quit => {
                if (choice == 0) {
                    for (self.tabs.items) |t| if (t.editor.buffer.isDirty() and t.editor.buffer.path != null) {
                        t.editor.buffer.save(self.io) catch {};
                    };
                    self.should_quit = true;
                } else if (choice == 1) self.should_quit = true;
            },
            else => {},
        }
    }

    // ---- Find panel -------------------------------------------------------

    fn showFind(self: *Workspace, replace: bool, incremental: bool) void {
        if (self.find == null) self.find = FindPanel.create(self.gpa, self.env) catch return;
        const f = self.find.?;
        f.mode = .buffer;
        f.show_replace = replace;
        f.incremental = incremental;
        f.focus_replace = false;
        f.focus_where = false;
        self.find_visible = true;
        self.focus = .find;
        if (self.activeEditor()) |ed| {
            f.scope = null;
            const sel = ed.primarySelection();
            if (!sel.isEmpty() and ed.selections.items.len == 1) {
                const text = ed.buffer.textRange(self.gpa, sel.start(), sel.end()) catch return;
                defer self.gpa.free(text);
                if (std.mem.indexOfScalar(u8, text, '\n') == null) {
                    f.find_input.setText(text, true);
                } else if (f.toggles.get(.in_selection)) {
                    f.scope = .{ sel.start(), sel.end() };
                }
            }
            f.find_input.selectAll();
            if (incremental) self.saveRestorePoint();
            f.searched_buffer = null;
        }
    }

    fn showFindInFiles(self: *Workspace) void {
        self.showFind(true, false);
        const f = self.find orelse return;
        f.mode = .files;
        f.scope = null;
        if (f.where_buffer.len() == 0) f.where_input.setText(if (self.project != null) "<open folders>" else "<open files>", false);
        if (self.activeEditor()) |ed| ed.highlights.clearRetainingCapacity();
    }

    /// Find in Files in `where` (a folder, "~/org"), waiting for the pattern.
    pub fn showFindIn(self: *Workspace, where: []const u8) void {
        self.showFindInFiles();
        const f = self.find orelse return;
        f.where_input.setText(where, false);
    }

    /// SPC p t: Find in Files for the TODO keywords across the project;
    /// the panel keeps the search, as after any other, but not the regex
    /// and case toggles it needed (buffer Find shares them).
    fn findTodos(self: *Workspace) void {
        self.showFindIn("<open folders>");
        const f = self.find orelse return;
        const toggles = f.toggles;
        f.find_input.setText(todo_pattern, false);
        f.toggles.set(.regex, true);
        f.toggles.set(.case_sensitive, true);
        f.toggles.set(.whole_word, false);
        self.startFindInFiles();
        f.toggles = toggles;
    }

    /// Starts searching the "Where" locations for the find string on a
    /// background thread; results land in the Find Results tab.
    fn startFindInFiles(self: *Workspace) void {
        const f = self.find orelse return;
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const pattern = f.find_input.contents(alloc) catch return;
        if (pattern.len == 0) return;
        const where_text = f.where_input.contents(alloc) catch return;
        const home: ?[]const u8 = if (std.c.getenv("HOME")) |h| std.mem.span(h) else null;
        const where = search.Where.parse(alloc, where_text, home) catch return;

        var roots: std.ArrayList([]const u8) = .empty;
        var files: std.ArrayList([]const u8) = .empty;
        var overlays: std.ArrayList(search.Overlay) = .empty;
        if (where.open_folders) if (self.project) |p| roots.append(alloc, p.root) catch return;
        roots.appendSlice(alloc, where.roots.items) catch return;
        for (self.buffers.items) |b| {
            const path = b.path orelse continue;
            if (where.open_files or roots.items.len == 0) files.append(alloc, path) catch return;
            // Unsaved changes are searched instead of the file on disk.
            if (b.isDirty()) overlays.append(alloc, .{ .path = path, .text = b.rope.toOwnedSlice(alloc) catch continue }) catch return;
        }
        const query: search.Query = .{
            .pattern = pattern,
            .regex = f.toggles.get(.regex),
            .case_sensitive = f.toggles.get(.case_sensitive),
            .whole_word = f.toggles.get(.whole_word),
        };
        // A newer search supersedes the running one (its result is dropped).
        if (self.search_job) |old| old.cancel();
        self.search_job = search.Job.start(self.gpa, self.io, self.executor, query, roots.items, files.items, &where, overlays.items, self, onSearchDone) catch |err| {
            self.setStatus("Find in Files failed: {t}", .{err});
            return;
        };
        self.searches_running += 1;
        const ed = self.resultsEditor() orelse return;
        const msg = std.fmt.allocPrint(alloc, "Searching for \"{s}\"…\n", .{pattern}) catch return;
        setResultsText(ed, msg);
        self.result_ranges.clearRetainingCapacity();
        self.result_index = null;
        self.hideFind();
        self.setStatus("Searching…", .{});
    }

    fn onSearchDone(ctx: *anyopaque, job: *search.Job) void {
        const self: *Workspace = @ptrCast(@alignCast(ctx));
        defer job.destroy();
        self.searches_running -= 1;
        // A cancelled or superseded search: drop it.
        if (self.search_job != job) return;
        self.search_job = null;
        if (self.shutting_down) return;
        self.dirty = true;
        const r = job.result orelse {
            self.setStatus("Find in Files failed", .{});
            return;
        };
        // Reuse the results tab without stealing focus if it was closed meanwhile.
        const ed = if (self.resultsTab()) |i| self.tabs.items[i].editor else self.resultsEditor() orelse return;
        setResultsText(ed, r.text);
        ed.setSingleSelection(.caret(0));
        ed.scroll_y = 0;
        self.result_ranges.clearRetainingCapacity();
        self.result_ranges.appendSlice(self.gpa, r.matches) catch {};
        self.result_index = null;
        ed.highlights.clearRetainingCapacity();
        ed.highlights.appendSlice(self.gpa, r.matches) catch {};
        if (r.regex_error) {
            self.setStatus("Invalid regular expression", .{});
        } else self.setStatus("{d} matches across {d} files", .{ r.matches.len, r.files_matched });
    }

    pub fn isFindResults(ed: *const Editor) bool {
        return ed.buffer.language == &languages.find_results;
    }

    fn resultsTab(self: *Workspace) ?usize {
        for (self.tabs.items, 0..) |t, i| if (isFindResults(t.editor)) return i;
        return null;
    }

    /// The Find Results tab (created on first use), activated.
    fn resultsEditor(self: *Workspace) ?*Editor {
        if (self.resultsTab()) |i| {
            self.activateTab(i);
            return self.tabs.items[i].editor;
        }
        const buffer = self.gpa.create(Buffer) catch return null;
        buffer.* = Buffer.initEmpty(self.gpa, self.next_buffer_id) catch {
            self.gpa.destroy(buffer);
            return null;
        };
        self.next_buffer_id += 1;
        buffer.display_name = "Find Results";
        buffer.language = &languages.find_results;
        buffer.read_only = true;
        self.buffers.append(self.gpa, buffer) catch return null;
        const ed = Editor.create(self.gpa, self.env, buffer) catch return null;
        _ = self.insertTab(.{ .editor = ed }) catch return null;
        self.focus = .editor;
        return ed;
    }

    fn setResultsText(ed: *Editor, text: []const u8) void {
        ed.buffer.read_only = false;
        ed.setText(text, false);
        ed.buffer.read_only = true;
        ed.buffer.saved_version = ed.buffer.version;
        // Generated: undo would keep every search's results for good.
        ed.buffer.clearHistory();
    }

    /// Double-click in Find Results: open the file at the clicked line.
    fn gotoResultAtCaret(self: *Workspace, ed: *Editor) void {
        const head = ed.primarySelection().head;
        const p = ed.buffer.offsetToPoint(head);
        self.gotoResultLine(ed, p.row, p.column, 0);
    }

    /// Opens the location a results line refers to; `len` > 0 selects that many bytes.
    fn gotoResultLine(self: *Workspace, results: *Editor, row: u32, col_in_line: usize, len: usize) void {
        var line_buf: std.ArrayList(u8) = .empty;
        defer line_buf.deinit(self.gpa);
        const line = results.buffer.lineText(row, &line_buf) catch return;
        const parsed = search.parseResultLine(line) orelse return;
        // The file header is the nearest unindented "path:" line above.
        var path: ?[]u8 = null;
        defer if (path) |pp| self.gpa.free(pp);
        var r = row;
        while (r > 0 and path == null) {
            r -= 1;
            line_buf.clearRetainingCapacity();
            const l = results.buffer.lineText(r, &line_buf) catch return;
            if (l.len > 1 and l[0] != ' ' and l[l.len - 1] == ':') path = self.gpa.dupe(u8, l[0 .. l.len - 1]) catch return;
        }
        const file = path orelse return;
        const target = self.openFile(file, false) catch |err| {
            self.setStatus("Unable to open {s}: {t}", .{ file, err });
            return;
        };
        const trow = @min(parsed.line_number -| 1, target.buffer.lineCount() - 1);
        const ls = target.buffer.lineStart(trow);
        const le = target.buffer.lineEnd(trow);
        const start = @min(ls + (col_in_line -| parsed.text_start), le);
        target.setSingleSelection(.range(start, @min(start + len, le)));
        target.afterCommand(true);
        self.focus = .editor;
    }

    /// F4 / shift+F4: step through Find in Files matches.
    fn stepResult(self: *Workspace, forward: bool) void {
        const n = self.result_ranges.items.len;
        const ti = self.resultsTab() orelse return;
        if (n == 0) {
            self.setStatus("No results", .{});
            return;
        }
        const i: usize = if (self.result_index) |cur| (if (forward) (cur + 1) % n else (cur + n - 1) % n) else if (forward) 0 else n - 1;
        self.result_index = i;
        const results = self.tabs.items[ti].editor;
        const m = self.result_ranges.items[i];
        results.setSingleSelection(.range(m[0], m[1]));
        results.ensureCaretVisible(true);
        const p = results.buffer.offsetToPoint(m[0]);
        self.gotoResultLine(results, p.row, p.column, m[1] - m[0]);
        self.setStatus("Result {d} of {d}", .{ i + 1, n });
    }

    /// Replace in every file listed in Find Results (left open, unsaved, like Sublime).
    fn replaceInFiles(self: *Workspace) void {
        const f = self.find orelse return;
        const ti = self.resultsTab() orelse {
            self.setStatus("Run Find in Files first", .{});
            return;
        };
        const text = self.tabs.items[ti].editor.contents(self.gpa) catch return;
        defer self.gpa.free(text);
        f.scope = null;
        var total: usize = 0;
        var files: usize = 0;
        var it = std.mem.splitScalar(u8, text, '\n');
        while (it.next()) |line| {
            if (line.len < 2 or (line[0] != '/' and !rpath.isRemote(line)) or line[line.len - 1] != ':') continue;
            const ed = self.openFile(line[0 .. line.len - 1], false) catch continue;
            const n = f.replaceAll(ed);
            total += n;
            if (n > 0) files += 1;
        }
        self.hideFind();
        self.setStatus("Replaced {d} occurrences in {d} files (unsaved)", .{ total, files });
    }

    fn hideFind(self: *Workspace) void {
        self.find_visible = false;
        self.focus = .editor;
        if (self.activeEditor()) |ed| ed.highlights.clearRetainingCapacity();
        self.dropRestorePoint();
    }

    fn findNext(self: *Workspace, forward: bool) void {
        const ed = self.activeEditor() orelse return;
        if (self.find == null) {
            // F3 without a find string: search for the word under the caret.
            self.find = FindPanel.create(self.gpa, self.env) catch return;
        }
        const f = self.find.?;
        if (f.find_buffer.len() == 0) {
            ed.findUnderExpand(false);
            const sel = ed.primarySelection();
            const text = ed.buffer.textRange(self.gpa, sel.start(), sel.end()) catch return;
            defer self.gpa.free(text);
            f.find_input.setText(text, true);
        }
        if (!f.findNext(ed, forward)) self.setStatus("Unable to find {s}", .{"match"});
    }

    // ---- Input ------------------------------------------------------------

    pub fn handleEvent(self: *Workspace, ev: platform.Event) void {
        self.dirty = true;
        // A leader sequence takes the keys, from terminals and input methods too.
        if (self.leader.active) switch (ev) {
            .key_down => |k| return self.onKey(k),
            .text_input, .preedit => return,
            else => {},
        };
        if (ev == .mouse_down and self.restOfSidebarToggle(ev.mouse_down)) return;
        if (settings_ui.handleEvent(self, ev)) return;
        if (terminal_ui.handleEvent(self, ev)) return;
        switch (ev) {
            .key_down => |k| self.onKey(k),
            .text_input => |t| if (self.focusedEditor()) |ed| {
                ed.setPreedit("");
                ed.insertText(t);
                self.afterFocusedEdit();
            },
            .preedit => |p| if (self.focusedEditor()) |ed| ed.setPreedit(p.text),
            .mouse_down => |m| self.onMouseDown(m),
            .mouse_up => |m| self.onMouseUp(m),
            .mouse_move => |m| self.onMouseMove(m),
            .scroll => |s| self.onScroll(s),
            .resize => |r| self.size = .{ .x = r.width, .y = r.height },
            .focus => |f| {
                if (self.activeEditor()) |ed| ed.focused = f and self.focus == .editor;
            },
            .close_requested => self.requestQuit(),
            .command => |name| if (std.meta.stringToEnum(keymap_mod.Action, name)) |action| self.perform(action),
            .mouse_exit, .wake, .key_up => {},
        }
    }

    fn requestQuit(self: *Workspace) void {
        var dirty: usize = 0;
        for (self.tabs.items) |t| if (t.editor.buffer.isDirty()) {
            dirty += 1;
        };
        if (dirty == 0) {
            self.should_quit = true;
            return;
        }
        self.openList(.list, std.fmt.allocPrint(self.gpa, "{d} file(s) have unsaved changes", .{dirty}) catch "Save changes?", @intFromEnum(PromptTag.confirm_quit), &.{ "Save All and Exit", "Exit Without Saving", "Cancel" });
    }

    pub fn onKey(self: *Workspace, k: platform.KeyEvent) void {
        const chord: keymap_mod.Chord = .{ .key = k.key, .mods = k.mods };
        const toggle_chord = keymap_mod.Chord.parse("ctrl+q") catch unreachable;
        if (self.recording and !self.playing_macro and !chord.eql(toggle_chord)) {
            const copy: platform.KeyEvent = .{ .key = k.key, .mods = k.mods, .repeat = k.repeat, .text = self.macro_arena.allocator().dupe(u8, k.text) catch "" };
            self.macro.append(self.gpa, copy) catch {};
        }
        if (self.leader.active) return leader_ui.key(self, k);

        if (self.menuIsOpen()) {
            if (self.onMenuKey(k)) return;
        }
        // The Menu key and shift+F10: the editor's right-click menu at the caret.
        if (k.key == .named and (k.key.named == .context_menu or (k.key.named == .f10 and k.mods.eql(.{ .shift = true })))) {
            if (self.focus == .editor) if (self.activeEditor()) |ed| {
                const r = self.caretRect(ed);
                context_menu.open(self, if (terminal_ui.focused(self) != null) .terminal else .editor, .{ .x = r.x, .y = r.y + r.h }, null);
                return;
            };
        }

        // Overlay navigation.
        if (self.focus == .overlay) if (self.overlay) |o| {
            const picking_scheme = o.prompt_tag == @intFromEnum(PromptTag.select_scheme);
            if (o.path_mode) |*pm| if (k.key == .named) switch (k.key.named) {
                // Path prompts: Tab completes, ctrl+backspace drops a folder,
                // and an entry picked with the arrows is what Enter takes.
                .tab => {
                    _ = path_complete.complete(self, o);
                    return;
                },
                .backspace => if (k.mods.ctrl and path_complete.deleteComponent(self, o)) return,
                .up, .down, .page_up, .page_down => pm.picked = o.filtered.items.len > 0,
                else => {},
            };
            if (k.key == .named) switch (k.key.named) {
                .escape => {
                    if (picking_scheme) if (self.prefs) |pr| pr.previewScheme(pr.ctx, null);
                    self.restore();
                    self.closeOverlay();
                    return;
                },
                .enter => {
                    if (picking_scheme) {
                        if (self.prefs) |pr| if (o.selectedItem()) |item| {
                            pr.previewScheme(pr.ctx, null);
                            pr.chooseScheme(pr.ctx, if (std.mem.eql(u8, item.title, "Auto")) "auto" else item.title);
                        };
                        self.closeOverlay();
                        return;
                    }
                    self.confirmOverlay();
                    return;
                },
                .up => {
                    o.moveSelection(-1);
                    if (o.kind == .goto_symbol) self.previewSymbol(o);
                    self.previewSelectedScheme();
                    return;
                },
                .down => {
                    o.moveSelection(1);
                    if (o.kind == .goto_symbol) self.previewSymbol(o);
                    self.previewSelectedScheme();
                    return;
                },
                .page_up => {
                    o.moveSelection(-@as(isize, @intCast(o.maxVisible())));
                    return;
                },
                .page_down => {
                    o.moveSelection(@intCast(o.maxVisible()));
                    return;
                },
                else => {},
            };
        };

        // Find panel keys.
        if (self.focus == .find) if (self.find) |f| {
            if (k.key == .named) switch (k.key.named) {
                .escape => {
                    if (f.incremental) self.restore();
                    self.hideFind();
                    return;
                },
                .enter => {
                    if (f.mode == .files) {
                        if (f.focus_replace) self.replaceInFiles() else self.startFindInFiles();
                        return;
                    }
                    const ed = self.activeEditor() orelse return;
                    if (k.mods.ctrl and k.mods.alt) {
                        _ = f.replaceAll(ed);
                    } else if (k.mods.alt) {
                        f.findAll(ed);
                        self.hideFind();
                    } else if (f.show_replace and f.focus_replace) {
                        f.replaceNext(ed);
                    } else {
                        _ = f.findNext(ed, !k.mods.shift);
                        if (f.incremental) {
                            self.dropRestorePoint();
                            self.hideFind();
                        }
                    }
                    return;
                },
                .tab => if (f.show_replace or f.mode == .files) {
                    f.cycleFocus();
                    return;
                },
                else => {},
            };
        };

        // The panel's own keys (the Source Control view's).
        if (self.focus == .panel and panel_ui.handleKey(self, k)) return;

        // Completion popup navigation, and any key dismisses a hover.
        if (self.lsp_state.hover != null) lsp_ui.closeHover(self);
        // Vim's space: the leader, ahead of completion and org keys.
        if (self.focus == .editor and leader_ui.isSpace(k)) if (self.key_hook) |h| if (h.enabled and h.leaderState(h.ctx)) {
            h.recordKey(h.ctx, k);
            return leader_ui.begin(self, false);
        };
        if (self.focus == .editor and lsp_ui.completionKey(self, k)) return;

        // Org buffers, the agenda and the Source Control and Diff tabs get first
        // pick of keys.
        if (self.focus == .editor) if (self.activeIndex()) |ai| {
            const tab = self.tabs.items[ai];
            if (tab.agenda != null) {
                if (org_ui.agendaKey(self, ai, k)) return;
            } else if (tab.git_diff != null or tab.git_commit != null) {
                if (git_status_ui.handleKey(self, ai, k)) return;
            } else if (org.isOrg(tab.editor)) {
                if (org_ui.handleKey(self, tab.editor, k)) {
                    self.afterFocusedEdit();
                    return;
                }
            }
        };

        if (self.focus == .editor) if (self.key_hook) |hook| if (hook.enabled) {
            // The second chord of a sequence (ctrl+k left) goes to the keymap; unbound, to vim.
            if (self.keymap.pending != null) switch (self.keymap.match(chord, true)) {
                .action => |a| {
                    self.perform(a);
                    return;
                },
                else => {},
            };
            if (self.activeEditor()) |ed| if (hook.handleKey(hook.ctx, self, ed, k)) return;
        };

        switch (self.keymap.match(chord, true)) {
            .pending => {
                var buf: [32]u8 = undefined;
                self.setStatus("{s} was pressed, waiting for second key…", .{chord.format(&buf)});
                return;
            },
            .action => |a| {
                self.perform(a);
                return;
            },
            .none => {},
        }
        if (k.text.len > 0) self.typeText(k.text);
    }

    fn typeText(self: *Workspace, text: []const u8) void {
        const ed = self.focusedEditor() orelse return;
        ed.insertText(text);
        self.afterFocusedEdit();
        if (self.focus == .editor) lsp_ui.afterTyping(self, ed, text);
    }

    /// After an edit in an input field: refilter overlays / rerun find.
    fn afterFocusedEdit(self: *Workspace) void {
        switch (self.focus) {
            .overlay => self.overlayInputChanged(),
            .find => if (self.find) |f| if (self.activeEditor()) |ed| {
                if (f.mode == .files) return;
                f.update(ed);
                if (f.incremental or true) {
                    // Find-as-you-type from the original caret.
                    const origin = if (self.restore_selections) |r| r[0].start() else ed.primarySelection().start();
                    if (f.incremental) f.findFrom(ed, origin);
                }
            },
            .panel => {},
            .editor => if (self.activeEditor()) |ed| {
                if (self.lsp_state.completion != null) lsp_ui.refilter(self);
                // Editing a preview tab makes it permanent.
                for (self.tabs.items) |*t| if (t.editor == ed) {
                    t.transient = false;
                };
            },
        }
    }

    pub fn perform(self: *Workspace, action: Action) void {
        if (terminal_ui.perform(self, action)) return;
        switch (action) {
            .cancel => {
                if (self.menuIsOpen()) {
                    self.closeMenus();
                } else if (self.overlay != null) {
                    self.restore();
                    self.closeOverlay();
                    self.org_agenda_return = null;
                } else if (self.find_visible) {
                    self.hideFind();
                } else if (self.panel.visible and (self.focus == .panel or self.panel.view == .language_servers)) {
                    // A terminal panel stays while typing in a file.
                    panel_ui.hide(self);
                } else if (self.focusedEditor()) |ed| ed.singleSelection();
            },
            .quit => self.requestQuit(),
            .quit_without_saving => self.should_quit = true,
            .new_file => _ = self.newFile() catch null,
            .open_file => self.promptOpenPath(.file),
            .open_file_here => self.promptOpenPath(.file_here),
            .open_folder => self.promptOpenPath(.folder),
            .save => if (self.activeEditor()) |ed| {
                // An org source block's tab saves into its block.
                if (org_ui.isSrcTab(self, ed)) return org_ui.finishSrc(self);
                self.saveEditor(ed);
            },
            .save_as => if (self.activeEditor()) |ed| self.promptSaveAs(ed.buffer.path orelse self.defaultDir()),
            .save_all => for (self.tabs.items) |t| {
                if (t.editor.buffer.isDirty() and t.editor.buffer.path != null) self.saveEditor(t.editor);
            },
            .revert_file => if (self.activeEditor()) |ed| self.revert(ed),
            .close_tab => if (self.activeIndex()) |i| self.closeTab(i),
            .close_all => {
                var i = self.tabs.items.len;
                while (i > 0) {
                    i -= 1;
                    if (!self.tabs.items[i].editor.buffer.isDirty()) self.closeTabNow(i);
                }
            },
            .close_other_tabs => self.closeOtherTabs(),
            .reopen_closed => if (self.closed_paths.pop()) |p| {
                defer self.gpa.free(p);
                _ = self.openFile(p, false) catch {};
            },
            .switch_buffer => file_cmd.switchBuffer(self),
            .scratch_buffer => file_cmd.scratchBuffer(self),
            .yank_buffer => file_cmd.yankBuffer(self),
            .copy_file_path, .copy_relative_path => file_cmd.copyPath(self, action == .copy_relative_path),
            .rename_file => file_cmd.promptRename(self),
            .delete_file => file_cmd.confirmDelete(self),
            .open_in_browser => file_cmd.openInBrowser(self),
            .install_shell_command => file_cmd.installShellCommand(self),
            .next_tab, .prev_tab => {
                // Cycles within the focused group.
                var buf: [256]usize = undefined;
                const in_group = self.groupTabs(self.focused_group, &buf);
                if (in_group.len == 0) return;
                const cur = self.activeIndex() orelse in_group[0];
                const pos = std.mem.indexOfScalar(usize, in_group, cur) orelse 0;
                const next = if (action == .next_tab) (pos + 1) % in_group.len else (pos + in_group.len - 1) % in_group.len;
                self.activateTab(in_group[next]);
            },
            .next_tab_in_stack, .prev_tab_in_stack => {
                // Most-recently-used order, within the focused group.
                var stack: [256]*Editor = undefined;
                var n: usize = 0;
                for (self.mru.items) |e| if (self.tabIndexOf(e)) |i| if (self.tabs.items[i].group == self.focused_group and n < stack.len) {
                    stack[n] = e;
                    n += 1;
                };
                if (n < 2) return;
                const target = if (action == .next_tab_in_stack) stack[1] else stack[n - 1];
                if (self.tabIndexOf(target)) |i| self.activateTab(i);
            },
            .tab_1, .tab_2, .tab_3, .tab_4, .tab_5, .tab_6, .tab_7, .tab_8, .tab_9 => {
                const n = @intFromEnum(action) - @intFromEnum(Action.tab_1);
                var buf: [256]usize = undefined;
                const in_group = self.groupTabs(self.focused_group, &buf);
                if (n < in_group.len) self.activateTab(in_group[n]);
            },
            .clone_file => self.cloneView(),
            .layout_single => self.setGroupLayout(.single),
            .layout_columns_2 => self.setGroupLayout(.columns_2),
            .layout_columns_3 => self.setGroupLayout(.columns_3),
            .layout_columns_4 => self.setGroupLayout(.columns_4),
            .layout_rows_2 => self.setGroupLayout(.rows_2),
            .layout_rows_3 => self.setGroupLayout(.rows_3),
            .layout_grid_4 => self.setGroupLayout(.grid_4),
            .focus_group_1, .focus_group_2, .focus_group_3, .focus_group_4 => {
                self.focusGroup(@intCast(@intFromEnum(action) - @intFromEnum(Action.focus_group_1)));
            },
            .next_group => self.focusGroup((self.focused_group + 1) % self.groupCount()),
            .prev_group => groups.focusNext(self, false, false),
            .focus_group_left => _ = groups.focusDirection(self, .left, 1, false),
            .focus_group_right => _ = groups.focusDirection(self, .right, 1, false),
            .focus_group_up => _ = groups.focusDirection(self, .up, 1, false),
            .focus_group_down => _ = groups.focusDirection(self, .down, 1, false),
            .move_to_next_group, .move_to_prev_group => if (self.activeIndex()) |i| {
                const n = self.groupCount();
                self.moveTabToGroup(i, if (action == .move_to_next_group) (self.focused_group + 1) % n else (self.focused_group + n - 1) % n);
            },
            .move_to_group_1, .move_to_group_2, .move_to_group_3, .move_to_group_4 => {
                const g: u8 = @intCast(@intFromEnum(action) - @intFromEnum(Action.move_to_group_1));
                if (self.activeIndex()) |i| self.moveTabToGroup(i, g);
            },
            .new_pane => self.newPane(),
            .close_pane => self.closePane(),
            .zoom_group => if (self.zoomed_group != null) {
                self.zoomed_group = null;
            } else if (self.groupCount() > 1) {
                self.zoomed_group = self.focused_group;
            } else self.setStatus("Only one group", .{}),
            .reveal_in_sidebar => {
                self.setSidebarVisible(true);
                self.revealActiveInSidebar(true);
            },
            .toggle_sidebar => self.setSidebarVisible(!self.sidebar_visible),
            .toggle_minimap => self.env.settings.show_minimap = !self.env.settings.show_minimap,
            .toggle_status_bar => self.status_visible = !self.status_visible,
            .toggle_panel => panel_ui.toggle(self),
            .toggle_menu => self.menu_visible = !self.menu_visible,
            .toggle_tabs => self.tabs_visible = !self.tabs_visible,
            .toggle_line_numbers => self.env.settings.show_line_numbers = !self.env.settings.show_line_numbers,
            .toggle_indent_guides => self.env.settings.draw_indent_guides = !self.env.settings.draw_indent_guides,
            .toggle_whitespace => self.env.settings.draw_white_space = if (self.env.settings.draw_white_space == .all) .selection else .all,
            .toggle_read_only => if (self.activeEditor()) |ed| {
                ed.buffer.read_only = !ed.buffer.read_only;
                self.setStatus("{s} is {s}", .{ tabTitle(ed), if (ed.buffer.read_only) "read-only" else "writable" });
            },
            .toggle_fullscreen => if (self.host) |h| h.toggleFullscreen(h.ctx),
            .toggle_distraction_free => {
                self.distraction_free = !self.distraction_free;
                // The panel isn't shown there.
                if (self.focus == .panel) self.focus = .editor;
                if (self.host) |h| h.toggleFullscreen(h.ctx);
            },
            .increase_font, .decrease_font, .reset_font, .toggle_big_font => self.changeFontSize(action),
            .command_palette => if (self.overlay != null and self.overlay.?.kind == .command_palette) self.closeOverlay() else self.openCommandPalette(),
            .goto_anything => if (self.overlay != null and self.overlay.?.kind == .goto_anything) self.closeOverlay() else self.openGotoAnything(""),
            .goto_line => self.openGotoLine(),
            .goto_symbol => self.openGotoSymbol(),
            .goto_word => self.openGotoAnything("#"),
            .show_find => self.showFind(false, false),
            .show_find_in_files => self.showFindInFiles(),
            .search_directory => self.showFindIn(self.activeFolder() orelse "<open folders>"),
            .project_todos => self.findTodos(),
            .rescan_project => if (self.project) |p| {
                p.scan();
                self.setStatus("Rescanning {s}", .{p.name});
            } else self.setStatus("No folder is open", .{}),
            .next_result => self.stepResult(true),
            .prev_result => self.stepResult(false),
            .show_replace => self.showFind(true, false),
            .show_incremental_find => self.showFind(false, true),
            .find_next => self.findNext(true),
            .find_prev => self.findNext(false),
            .find_under => {
                if (self.activeEditor()) |ed| {
                    const sel = ed.primarySelection();
                    if (sel.isEmpty()) ed.findUnderExpand(false);
                    self.useSelectionForFind();
                    self.findNext(true);
                }
            },
            .use_selection_for_find => self.useSelectionForFind(),
            .replace_next => if (self.find) |f| if (self.activeEditor()) |ed| f.replaceNext(ed),
            .replace_all => if (self.find) |f| if (self.activeEditor()) |ed| {
                const n = f.replaceAll(ed);
                self.setStatus("Replaced {d} occurrences", .{n});
            },
            .find_all => if (self.find) |f| if (self.activeEditor()) |ed| f.findAll(ed),
            .toggle_find_regex => if (self.find) |f| f.flip(.regex),
            .toggle_find_case => if (self.find) |f| f.flip(.case_sensitive),
            .toggle_find_whole_word => if (self.find) |f| f.flip(.whole_word),
            .toggle_record_macro => {
                self.recording = !self.recording;
                if (self.recording) {
                    self.macro.clearRetainingCapacity();
                    _ = self.macro_arena.reset(.retain_capacity);
                    self.setStatus("Recording macro", .{});
                } else self.setStatus("Macro recorded ({d} keys)", .{self.macro.items.len});
            },
            .run_macro => self.playMacro(),
            .lsp_goto_definition, .lsp_goto_implementation, .lsp_goto_type_definition, .lsp_find_references, .lsp_hover, .lsp_format, .lsp_complete => if (self.activeEditor()) |ed| {
                const l = self.lsp orelse return;
                const head = ed.primarySelection().head;
                const ok = switch (action) {
                    .lsp_goto_definition => lsp_ui.goto(self, ed, .definition, false),
                    .lsp_goto_implementation => lsp_ui.goto(self, ed, .implementation, false),
                    .lsp_goto_type_definition => lsp_ui.goto(self, ed, .type_definition, false),
                    .lsp_find_references => l.references(ed.buffer, head),
                    .lsp_hover => blk: {
                        self.lsp_state.rest_requested = false;
                        break :blk l.hover(ed.buffer, head);
                    },
                    .lsp_format => l.format(ed.buffer),
                    else => l.completion(ed.buffer, head, null),
                };
                if (!ok) self.setStatus("No language server for {s}", .{ed.buffer.language.name});
            },
            .lsp_code_actions => lsp_ui.requestCodeActions(self),
            .lsp_rename => if (self.activeEditor()) |ed| {
                var s = @import("../../model/text/movement.zig").Scanner.init(&ed.buffer.rope);
                const head = ed.primarySelection().head;
                self.lsp_state.pending_rename_offset = head;
                const w = @import("../../model/text/movement.zig").wordAt(&s, head);
                const word = if (w) |r| ed.buffer.textRange(self.gpa, r[0], r[1]) catch "" else "";
                defer if (w != null) self.gpa.free(word);
                self.openPrompt("Rename symbol to:", @intFromEnum(PromptTag.rename), word);
            },
            .lsp_show_status => panel_ui.showStatus(self),
            .lsp_clear_log => if (self.lsp) |l| l.clearLog(),
            .lsp_next_diagnostic, .lsp_prev_diagnostic => lsp_ui.gotoDiagnostic(self, action == .lsp_next_diagnostic, false),
            .lsp_next_error, .lsp_prev_error => lsp_ui.gotoDiagnostic(self, action == .lsp_next_error, true),
            .lsp_list_diagnostics => lsp_ui.listDiagnostics(self),
            .git_toggle_blame, .git_toggle_inline_blame, .git_show_commit, .git_copy_commit_hash => git_ui.perform(self, action),
            .git_status => git_status_ui.open(self),
            .lsp_restart => if (self.lsp) |l| {
                l.restartAll();
                for (self.buffers.items) |b| l.openBuffer(b);
                self.setStatus("Restarted language servers", .{});
            },
            .select_color_scheme => self.openSchemePicker(),
            .open_keymap => {
                var kbuf: [std.fs.max_path_bytes]u8 = undefined;
                const kp = keymap_mod.Keymap.userPath(&kbuf) orelse return;
                if (Io.Dir.cwd().statFile(self.io, kp, .{})) |_| {} else |_| {
                    const template = keymap_mod.Keymap.template(self.gpa) catch return;
                    defer self.gpa.free(template);
                    if (std.fs.path.dirname(kp)) |dir| Io.Dir.cwd().createDirPath(self.io, dir) catch {};
                    Io.Dir.cwd().writeFile(self.io, .{ .sub_path = kp, .data = template }) catch |err| {
                        self.setStatus("Unable to create keymap.json: {t}", .{err});
                        return;
                    };
                }
                _ = self.openFile(kp, false) catch |err| self.setStatus("Unable to open keymap.json: {t}", .{err});
            },
            .reload_config => self.reloadConfig("Settings and key bindings"),
            .remote_connect => remote_ui.promptConnect(self),
            .remote_open_folder => remote_ui.promptOpenFolder(self),
            .remote_close => remote_ui.closeConnection(self),
            .quit_session => {
                self.quit_session = true;
                self.requestQuit();
            },
            .open_settings => settings_ui.open(self, .settings),
            .open_keyboard_shortcuts => settings_ui.open(self, .shortcuts),
            .open_settings_json => if (self.prefs) |pr| {
                var pbuf: [std.fs.max_path_bytes]u8 = undefined;
                const sp = pr.settingsPath(pr.ctx, &pbuf) orelse return;
                @import("../settings/settings_file.zig").ensureExists(self.io) catch |err| {
                    self.setStatus("Unable to create settings: {t}", .{err});
                    return;
                };
                _ = self.openFile(sp, false) catch |err| self.setStatus("Unable to open settings: {t}", .{err});
            },
            .org_agenda => org_ui.openAgenda(self, .week),
            .org_agenda_day => org_ui.openAgenda(self, .day),
            .org_todo_list => org_ui.openAgenda(self, .todo),
            .org_capture => org_ui.startCapture(self),
            .org_schedule => org_ui.runCommand(self, .schedule),
            .org_deadline => org_ui.runCommand(self, .deadline),
            .org_timestamp => org_ui.runCommand(self, .timestamp),
            .org_timestamp_inactive => org_ui.runCommand(self, .timestamp_inactive),
            .org_open_link => org_ui.runCommand(self, .open_link),
            .org_goto_capture_file => org_ui.gotoCaptureFile(self),
            .org_find_notes => self.promptPathIn(false, org_ui.notes_folder),
            .org_search_notes => self.showFindIn(org_ui.notes_folder),
            .org_set_tags,
            .org_refile,
            .org_archive,
            .org_clock_in,
            .org_clock_out,
            .org_clock_goto,
            .org_clock_report,
            .org_edit_src,
            .org_export_html,
            .org_export_markdown,
            .org_table_align,
            .org_table_insert_row,
            .org_table_delete_row,
            .org_table_insert_column,
            .org_table_delete_column,
            .org_table_insert_hline,
            .org_agenda_dispatch,
            .org_agenda_search,
            .org_agenda_tags,
            => org_ui.perform(self, action),
            .org_cycle_todo,
            .org_toggle_checkbox,
            .org_global_cycle,
            .org_insert_heading,
            .org_priority_up,
            .org_priority_down,
            .org_promote,
            .org_demote,
            .org_promote_subtree,
            .org_demote_subtree,
            .org_move_subtree_up,
            .org_move_subtree_down,
            => if (self.activeEditor()) |ed| {
                switch (action) {
                    .org_cycle_todo => _ = org.cycleTodo(ed, true),
                    .org_toggle_checkbox => _ = org.toggleCheckbox(ed),
                    .org_global_cycle => org.globalCycle(ed, &self.org_keys.global),
                    .org_priority_up, .org_priority_down => _ = org.cyclePriority(ed, action == .org_priority_up),
                    .org_promote, .org_demote => _ = org.shiftLevel(ed, if (action == .org_promote) -1 else 1, false),
                    .org_promote_subtree, .org_demote_subtree => _ = org.shiftLevel(ed, if (action == .org_promote_subtree) -1 else 1, true),
                    .org_move_subtree_up, .org_move_subtree_down => _ = org.moveSubtree(ed, action == .org_move_subtree_up),
                    else => _ = org.insertHeading(ed, true, false),
                }
                ed.afterCommand(false);
            },
            .leader => leader_ui.begin(self, false),
            .show_leader_keys => leader_ui.begin(self, true),
            .toggle_vim_mode => if (self.key_hook) |*h| {
                h.enabled = !h.enabled;
                h.onToggle(h.ctx, self.activeEditor(), h.enabled);
                self.setStatus("Vim mode {s}", .{if (h.enabled) "enabled" else "disabled"});
            },
            else => {
                const ed = self.focusedEditor() orelse return;
                if (ed.perform(action)) self.afterFocusedEdit();
            },
        }
    }

    fn useSelectionForFind(self: *Workspace) void {
        const ed = self.activeEditor() orelse return;
        if (self.find == null) self.find = FindPanel.create(self.gpa, self.env) catch return;
        const sel = ed.primarySelection();
        const text = ed.buffer.textRange(self.gpa, sel.start(), sel.end()) catch return;
        defer self.gpa.free(text);
        self.find.?.find_input.setText(text, true);
    }

    pub fn playMacro(self: *Workspace) void {
        if (self.macro.items.len == 0 or self.recording) return;
        self.playing_macro = true;
        defer self.playing_macro = false;
        const events = self.gpa.dupe(platform.KeyEvent, self.macro.items) catch return;
        defer self.gpa.free(events);
        for (events) |e| self.onKey(e);
    }

    fn revert(self: *Workspace, ed: *Editor) void {
        const path = ed.buffer.path orelse return;
        const fresh = Buffer.load(self.gpa, self.io, ed.buffer.id, path) catch return;
        const text = fresh.rope.toOwnedSlice(self.gpa) catch return;
        defer self.gpa.free(text);
        var f = fresh;
        f.deinit();
        ed.setText(text, false);
        ed.setSingleSelection(.caret(0));
        ed.buffer.saved_version = ed.buffer.version;
    }

    fn changeFontSize(self: *Workspace, action: Action) void {
        const s = self.env.settings;
        const size = s.font_size;
        s.font_size = switch (action) {
            .increase_font => @min(48, size + 1),
            .decrease_font => @max(6, size - 1),
            .toggle_big_font => self.big_font_from orelse @min(48, size + big_font_step),
            else => 13,
        };
        // Big font remembers the size to go back to; ctrl+0 forgets it.
        switch (action) {
            .toggle_big_font => self.big_font_from = if (self.big_font_from == null) size else null,
            .reset_font => self.big_font_from = null,
            else => {},
        }
        self.env.font = self.env.text.font(.{ .family = s.font_family, .size = s.font_size }) catch self.env.font;
    }

    // ---- Menus ------------------------------------------------------------

    pub fn menuIsOpen(self: *const Workspace) bool {
        return self.menu_open != null or self.context_menu != null;
    }

    pub fn closeMenus(self: *Workspace) void {
        self.menu_open = null;
        context_menu.close(self);
    }

    /// The items of the open menu: the right-click menu's or a menu bar menu's.
    fn openMenuItems(self: *const Workspace) ?[]const menu.Item {
        if (self.context_menu) |m| return m.items.items;
        if (self.menu_open) |i| return menu.menus[i].items;
        return null;
    }

    fn chooseMenuItem(self: *Workspace, item: menu.Item) void {
        if (item == .separator) return;
        if (self.context_menu != null) return context_menu.choose(self, item);
        self.menu_open = null;
        if (item == .action) self.perform(item.action.action);
    }

    fn onMenuKey(self: *Workspace, k: platform.KeyEvent) bool {
        const items = self.openMenuItems() orelse return false;
        if (k.key != .named) return true;
        switch (k.key.named) {
            .escape => self.closeMenus(),
            .left => if (self.menu_open) |open| {
                self.menu_open = (open + menu.menus.len - 1) % menu.menus.len;
            },
            .right => if (self.menu_open) |open| {
                self.menu_open = (open + 1) % menu.menus.len;
            },
            .down, .up => {
                var i: usize = self.menu_hover_item orelse (if (k.key.named == .down) items.len - 1 else 0);
                var tries: usize = 0;
                while (tries < items.len) : (tries += 1) {
                    i = if (k.key.named == .down) (i + 1) % items.len else (i + items.len - 1) % items.len;
                    if (items[i] != .separator) break;
                }
                self.menu_hover_item = i;
            },
            .enter => if (self.menu_hover_item) |i| if (i < items.len) self.chooseMenuItem(items[i]),
            else => {},
        }
        return true;
    }

    // ---- Mouse ------------------------------------------------------------

    /// Whether press `m` is the rest of a double-click whose first press hid
    /// or reopened the side bar: it would land on whatever took the ×'s or
    /// the strip's place, the tab bar or a tree row.
    fn restOfSidebarToggle(self: *Workspace, m: platform.MouseEvent) bool {
        if (m.click_count < 2) self.sidebar_toggle_click = false;
        return self.sidebar_toggle_click;
    }

    fn onMouseDown(self: *Workspace, m: platform.MouseEvent) void {
        const x = m.pos.x;
        const y = m.pos.y;

        // Menus.
        if (self.openMenuItems()) |items| {
            if (self.menu_rect.contains(x, y)) {
                if (m.button == .middle) return;
                for (self.menu_item_rects.items, 0..) |r, i| if (r.contains(x, y)) {
                    if (i < items.len) self.chooseMenuItem(items[i]);
                    return;
                };
                return;
            }
            if (self.context_menu != null) {
                // A click elsewhere closes it; a right click opens the next one there.
                context_menu.close(self);
                if (m.button != .right) return;
            } else if (!self.menubar_rect.contains(x, y)) {
                self.menu_open = null;
                return;
            }
        }
        if (toolbar.onMouseDown(self, m)) return;
        if (self.menu_visible and self.menubar_rect.contains(x, y)) {
            for (self.menu_title_rects, 0..) |r, i| if (r.contains(x, y)) {
                self.menu_open = if (self.menu_open == i) null else i;
                self.menu_hover_item = null;
                return;
            };
            self.menu_open = null;
            return;
        }

        // Overlay is modal: clicks outside dismiss it.
        if (self.overlay) |o| {
            if (o.panel_rect.contains(x, y)) {
                if (o.rowAt(x, y)) |row| {
                    if (m.button != .left) return;
                    o.selected = row;
                    if (o.path_mode) |*pm| pm.picked = true;
                    if (m.click_count >= 1) self.confirmOverlay();
                } else {
                    self.mouse_down_in = .overlay;
                    o.input.onMouseDown(m);
                }
                return;
            }
            self.restore();
            self.closeOverlay();
            self.org_agenda_return = null;
            return;
        }

        if (self.sidebar_strip_rect.contains(x, y)) {
            if (m.button == .left) {
                self.mouse_down_in = .sidebar_strip;
                self.sidebar_drag_from = self.sidebar_width;
            }
            return;
        }
        if (m.button == .left and self.sidebar_visible and !self.distraction_free and @abs(x - self.sidebar_rect.x) < 4 and self.sidebar_rect.contains(x + 4, y)) {
            self.mouse_down_in = .sidebar_edge;
            self.sidebar_drag_from = self.sidebar_width;
            return;
        }
        if (self.sidebar_visible and !self.distraction_free and self.sidebar_rect.contains(x, y)) {
            self.onSidebarClick(m);
            return;
        }
        if (panel_ui.onMouseDown(self, m)) return;
        if (self.splitterAt(x, y)) |split| {
            if (m.button != .left) return;
            self.drag_split = split;
            self.mouse_down_in = .splitter;
            return;
        }
        if (self.tabs_visible) for (self.group_tabbar_rects[0..self.groupCount()]) |r| if (r.contains(x, y)) {
            self.onTabClick(m);
            return;
        };
        if (self.find_visible and self.find_rect.contains(x, y)) {
            self.onFindClick(m);
            return;
        }
        if (self.groupAt(x, y)) |g| {
            // Clicking into a group focuses it (the find panel stays open).
            if (g != self.focused_group) self.focusGroup(g);
            if (self.activeEditor()) |ed| {
                self.focus = .editor;
                ed.focused = true;
                self.mouse_down_in = .editor;
                self.mouse_editor = ed;
                ed.onMouseDown(m);
                if (m.button == .right) {
                    self.mouse_down_in = .none;
                    self.mouse_editor = null;
                    context_menu.open(self, .editor, m.pos, null);
                    return;
                }
                for (self.tabs.items) |*t| if (t.editor == ed and m.click_count >= 2) {
                    t.transient = false;
                };
                if (m.click_count == 2 and isFindResults(ed)) self.gotoResultAtCaret(ed);
                if (m.click_count == 2) org_ui.onDoubleClick(self, ed);
                if (m.click_count == 2) git_status_ui.onDoubleClick(self, ed);
            }
        }
    }

    /// The group whose editor area contains (x, y).
    fn groupAt(self: *Workspace, x: f32, y: f32) ?u8 {
        for (self.group_editor_rects[0..self.groupCount()], 0..) |r, g| if (r.contains(x, y)) return @intCast(g);
        return null;
    }

    /// Splitter `s` as a 1px line in the editor area.
    fn splitterRect(self: *const Workspace, s: groups.Splitter) Rect {
        const area = self.editor_rect;
        if (s.across) {
            const y0 = @round(area.y + area.h * s.from);
            return .{ .x = @round(area.x + area.w * s.at), .y = y0, .w = 1, .h = @round(area.y + area.h * s.to) - y0 };
        }
        const x0 = @round(area.x + area.w * s.from);
        return .{ .x = x0, .y = @round(area.y + area.h * s.at), .w = @round(area.x + area.w * s.to) - x0, .h = 1 };
    }

    /// The splitter between groups under (x, y), if any.
    fn splitterAt(self: *Workspace, x: f32, y: f32) ?groups.Splitter {
        if (!self.editor_rect.contains(x, y) or self.zoomed_group != null) return null;
        var buf: [max_groups]groups.Splitter = undefined;
        for (self.group_layout.splitters(&buf)) |s| {
            const r = self.splitterRect(s);
            const near = if (s.across)
                @abs(x - r.x) < 4 and y >= r.y and y <= r.y + r.h
            else
                @abs(y - r.y) < 4 and x >= r.x and x <= r.x + r.w;
            if (near) return s;
        }
        return null;
    }

    fn dragSplitTo(self: *Workspace, pos: platform.Vec2) void {
        const split = self.drag_split orelse return;
        const area = self.editor_rect;
        const frac = if (split.across) (pos.x - area.x) / @max(1, area.w) else (pos.y - area.y) / @max(1, area.h);
        self.group_layout.dragSplitter(split.slot, frac);
    }

    fn onMouseUp(self: *Workspace, m: platform.MouseEvent) void {
        switch (self.mouse_down_in) {
            .editor => if (self.mouse_editor) |ed| ed.onMouseUp(m),
            .overlay => if (self.overlay) |o| o.input.onMouseUp(m),
            .find => if (self.find) |f| f.activeInput().onMouseUp(m),
            .panel => if (panel_ui.viewEditor(self)) |ed| ed.onMouseUp(m),
            .tab => {
                if (self.pressed_tab) |i| if (m.button == .middle) {
                    for (self.tab_hits.items) |hit| if (hit.index == i and hit.rect.contains(m.pos.x, m.pos.y)) {
                        self.closeTab(i);
                        break;
                    };
                };
            },
            // The drag left the side bar at its new width, or hidden if it
            // ended collapsed.
            .sidebar_edge => self.saveUiState(),
            // A click (no drag past the collapse point) reopens it as it was.
            .sidebar_strip => {
                self.setSidebarVisible(true);
                self.sidebar_toggle_click = true;
            },
            else => {},
        }
        self.mouse_down_in = .none;
        self.pressed_tab = null;
        self.drag_split = null;
        self.mouse_editor = null;
    }

    fn onMouseMove(self: *Workspace, m: platform.MouseMoveEvent) void {
        self.mouse = m.pos;
        lsp_ui.mouseMoved(self, m.pos);
        if (self.openMenuItems()) |items| {
            for (self.menu_item_rects.items, 0..) |r, i| if (r.contains(m.pos.x, m.pos.y) and i < items.len) {
                self.menu_hover_item = if (items[i] == .separator) null else i;
            };
            if (self.menu_open != null) for (self.menu_title_rects, 0..) |r, i| if (r.contains(m.pos.x, m.pos.y)) {
                self.menu_open = i;
            };
        }
        switch (self.mouse_down_in) {
            .editor => if (self.mouse_editor) |ed| ed.onMouseMove(m),
            .overlay => if (self.overlay) |o| o.input.onMouseMove(m),
            .find => if (self.find) |f| f.activeInput().onMouseMove(m),
            .sidebar_edge => self.dragSidebarEdge(m.pos.x),
            // Pulled out of the strip past the collapse point: from here on
            // it is a drag of the side bar's edge.
            .sidebar_strip => {
                self.dragSidebarEdge(m.pos.x);
                if (self.sidebar_visible) self.mouse_down_in = .sidebar_edge;
            },
            .splitter => self.dragSplitTo(m.pos),
            .panel => if (panel_ui.viewEditor(self)) |ed| ed.onMouseMove(m),
            .panel_edge => panel_ui.dragEdge(self, m.pos),
            else => {
                // Hover state (minimap, gutter) for every visible view.
                for (self.group_active[0..self.groupCount()]) |ged| if (ged) |ed| ed.onMouseMove(m);
                if (self.panel.visible) if (panel_ui.viewEditor(self)) |ed| ed.onMouseMove(m);
            },
        }
        if (self.host) |h| {
            var shape: platform.CursorShape = .default;
            const split = if (self.mouse_down_in == .splitter) self.drag_split else if (self.mouse_down_in == .none) self.splitterAt(m.pos.x, m.pos.y) else null;
            const over_panel = switch (self.mouse_down_in) {
                .none, .panel, .panel_edge => panel_ui.cursorShape(self, m.pos),
                else => null,
            };
            if (over_panel) |ps| {
                shape = ps;
            } else if (self.mouse_down_in == .sidebar_edge or (self.sidebar_visible and @abs(m.pos.x - self.sidebar_rect.x) < 4 and self.sidebar_rect.contains(m.pos.x + 4, m.pos.y))) {
                shape = .ew_resize;
            } else if (self.sidebar_strip_rect.contains(m.pos.x, m.pos.y) or (self.sidebar_visible and self.sidebarCloseRect().contains(m.pos.x, m.pos.y))) {
                shape = .pointer;
            } else if (split) |sp| {
                shape = if (sp.across) .ew_resize else .ns_resize;
            } else if (self.overlay == null and self.groupAt(m.pos.x, m.pos.y) != null) {
                if (self.group_active[self.groupAt(m.pos.x, m.pos.y).?]) |ed| shape = ed.cursorShapeAt(m.pos);
            } else if (self.find_visible and (self.find.?.find_rect.contains(m.pos.x, m.pos.y) or self.find.?.replace_rect.contains(m.pos.x, m.pos.y) or self.find.?.where_rect.contains(m.pos.x, m.pos.y))) {
                shape = .text;
            }
            h.setCursor(h.ctx, shape);
        }
    }

    fn onScroll(self: *Workspace, s: platform.ScrollEvent) void {
        const x = s.pos.x;
        const y = s.pos.y;
        if (self.overlay) |o| {
            if (o.panel_rect.contains(x, y)) {
                const steps: isize = if (s.delta.y > 0) 1 else if (s.delta.y < 0) -1 else 0;
                const max_scroll = o.filtered.items.len -| o.maxVisible();
                const next: isize = @as(isize, @intCast(o.scroll)) + steps;
                o.scroll = @intCast(std.math.clamp(next, 0, @as(isize, @intCast(max_scroll))));
            }
            return;
        }
        if (self.sidebar_visible and self.sidebar_rect.contains(x, y)) {
            const dy = if (s.unit == .lines) s.delta.y * sidebar_row_h * 3 else s.delta.y;
            const content = @as(f32, @floatFromInt(self.sidebar_rows.items.len + 1)) * sidebar_row_h + sidebar_header_h;
            self.sidebar_scroll = std.math.clamp(self.sidebar_scroll + dy, 0, @max(0, content - self.sidebar_rect.h + sidebar_row_h));
            return;
        }
        if (panel_ui.onScroll(self, s)) return;
        // Scrolling goes to the view under the pointer, focused or not.
        if (self.groupAt(x, y)) |g| if (self.group_active[g]) |ed| ed.onScroll(s);
    }

    fn onTabClick(self: *Workspace, m: platform.MouseEvent) void {
        for (self.tab_hits.items) |hit| {
            if (!hit.rect.contains(m.pos.x, m.pos.y)) continue;
            const i = hit.index;
            self.mouse_down_in = .tab;
            self.pressed_tab = i;
            if (m.button == .middle) return;
            if (m.button == .right) {
                // The menu's entries act on the active tab: make it this one.
                self.mouse_down_in = .none;
                self.pressed_tab = null;
                self.activateTab(i);
                self.focus = .editor;
                context_menu.open(self, .tab, m.pos, null);
                return;
            }
            if (m.button != .left) return;
            if (hit.close.contains(m.pos.x, m.pos.y)) {
                self.closeTab(i);
                return;
            }
            self.activateTab(i);
            self.focus = .editor;
            if (m.click_count >= 2) self.tabs.items[i].transient = false;
            return;
        }
        // Double-click on an empty tab bar opens a new file in that group.
        if (m.button != .left) return;
        for (self.group_tabbar_rects[0..self.groupCount()], 0..) |r, g| if (r.contains(m.pos.x, m.pos.y)) {
            if (g != self.focused_group) self.focusGroup(@intCast(g));
            if (m.click_count >= 2) _ = self.newFile() catch {};
            return;
        };
    }

    fn onFindClick(self: *Workspace, m: platform.MouseEvent) void {
        const f = self.find orelse return;
        const x = m.pos.x;
        const y = m.pos.y;
        self.focus = .find;
        if (f.hitToggle(x, y)) |t| {
            f.flip(t);
            return;
        }
        if (f.hitButton(x, y)) |b| {
            if (b == .find_in_files) return self.startFindInFiles();
            if (b == .replace_in_files) return self.replaceInFiles();
            const ed = self.activeEditor() orelse return;
            switch (b) {
                .find => _ = f.findNext(ed, true),
                .find_prev => _ = f.findNext(ed, false),
                .find_all => {
                    f.findAll(ed);
                    self.hideFind();
                },
                .replace => f.replaceNext(ed),
                .replace_all => {
                    const n = f.replaceAll(ed);
                    self.setStatus("Replaced {d} occurrences", .{n});
                },
                .find_in_files => self.startFindInFiles(),
                .replace_in_files => self.replaceInFiles(),
            }
            return;
        }
        if (f.find_rect.contains(x, y)) {
            f.focus_replace = false;
            f.focus_where = false;
            self.mouse_down_in = .find;
            f.find_input.onMouseDown(m);
        } else if (f.mode == .files and f.where_rect.contains(x, y)) {
            f.focus_replace = false;
            f.focus_where = true;
            self.mouse_down_in = .find;
            f.where_input.onMouseDown(m);
        } else if (f.show_replace and f.replace_rect.contains(x, y)) {
            f.focus_replace = true;
            f.focus_where = false;
            self.mouse_down_in = .find;
            f.replace_input.onMouseDown(m);
        }
    }

    fn refreshSidebarRows(self: *Workspace) void {
        const p = self.project orelse {
            self.sidebar_rows.clearRetainingCapacity();
            return;
        };
        const gen = p.generation +% @as(u32, @intFromBool(self.root_expanded));
        if (gen == self.sidebar_rows_generation and self.sidebar_rows.items.len > 0) return;
        self.sidebar_rows_generation = gen;
        const rows = p.visibleEntries(self.gpa) catch return;
        defer self.gpa.free(rows);
        self.sidebar_rows.clearRetainingCapacity();
        if (self.root_expanded) self.sidebar_rows.appendSlice(self.gpa, rows) catch {};
    }

    /// The × at the right end of the side bar's header row.
    fn sidebarCloseRect(self: *const Workspace) Rect {
        const sb = self.sidebar_rect;
        return .{ .x = sb.right() - 26, .y = sb.y + 3, .w = 22, .h = 24 };
    }

    fn onSidebarClick(self: *Workspace, m: platform.MouseEvent) void {
        // The header row (pinned above the tree).
        if (m.pos.y < self.sidebar_rect.y + sidebar_header_h) {
            if (m.button == .left and self.sidebarCloseRect().contains(m.pos.x, m.pos.y)) {
                self.setSidebarVisible(false);
                self.sidebar_toggle_click = true;
            }
            return;
        }
        const p = self.project orelse return;
        const rel_y = m.pos.y - self.sidebar_rect.y - sidebar_header_h + self.sidebar_scroll;
        const row_i: usize = @intFromFloat(@floor(rel_y / sidebar_row_h));
        if (m.button == .right) return self.onSidebarRightClick(p, row_i, m.pos);
        if (m.button != .left) return;
        if (row_i == 0) {
            self.root_expanded = !self.root_expanded;
            self.sidebar_rows_generation = std.math.maxInt(u32);
            return;
        }
        if (row_i - 1 >= self.sidebar_rows.items.len) return;
        const idx = self.sidebar_rows.items[row_i - 1];
        const entry = p.snapshot.?.entries[idx];
        switch (entry.kind) {
            .dir => if (!entry.ignored) p.toggleExpanded(entry.path),
            .file => {
                const abs = p.absolutePath(self.gpa, entry.path) catch return;
                defer self.gpa.free(abs);
                // Single click previews (transient tab), double click opens.
                _ = self.openFile(abs, m.click_count < 2) catch |err| self.setStatus("Unable to open {s}: {t}", .{ entry.path, err });
                self.focus = .editor;
            },
        }
    }

    /// The right-click menu of a side bar row: the root and folders get the
    /// folder menu; a file is previewed first, so Rename and Delete act on it.
    fn onSidebarRightClick(self: *Workspace, p: *Project, row_i: usize, pos: platform.Vec2) void {
        if (row_i == 0) return context_menu.open(self, .folder, pos, p.root);
        if (row_i - 1 >= self.sidebar_rows.items.len) return;
        const entry = p.snapshot.?.entries[self.sidebar_rows.items[row_i - 1]];
        const abs = p.absolutePath(self.gpa, entry.path) catch return;
        defer self.gpa.free(abs);
        switch (entry.kind) {
            .dir => context_menu.open(self, .folder, pos, abs),
            .file => {
                _ = self.openFile(abs, true) catch |err| {
                    self.setStatus("Unable to open {s}: {t}", .{ entry.path, err });
                    return;
                };
                self.focus = .editor;
                context_menu.open(self, .file, pos, abs);
            },
        }
    }

    /// Closes the focused group's tabs after the active one; those with
    /// unsaved changes stay, as with Close Others.
    pub fn closeTabsToRight(self: *Workspace) void {
        const keep = self.activeIndex() orelse return;
        var kept: usize = 0;
        var i = self.tabs.items.len;
        while (i > keep + 1) {
            i -= 1;
            const t = self.tabs.items[i];
            if (t.group != self.focused_group) continue;
            if (t.editor.buffer.isDirty()) kept += 1 else self.closeTabNow(i);
        }
        if (kept > 0) self.setStatus("{d} tab(s) with unsaved changes stay open", .{kept});
    }

    // ---- Layout and paint -------------------------------------------------

    fn layout(self: *Workspace) void {
        var top: f32 = 0;
        var bottom = self.size.y;
        const df = self.distraction_free;
        if (self.menu_visible and !df) {
            self.menubar_rect = .{ .x = 0, .y = 0, .w = self.size.x, .h = menubar_h };
            self.toolbar_rect = .{ .x = 0, .y = menubar_h, .w = self.size.x, .h = toolbar_h };
            top = menubar_h + toolbar_h;
        } else {
            self.menubar_rect = .{};
            self.toolbar_rect = .{};
        }
        if (self.status_visible and !df) {
            self.status_rect = .{ .x = 0, .y = bottom - status_h, .w = self.size.x, .h = status_h };
            bottom -= status_h;
        } else self.status_rect = .{};
        // The Explorer is docked on the right, as in cs-code.
        const left: f32 = 0;
        var right: f32 = 0;
        self.sidebar_rect = .{};
        self.sidebar_strip_rect = .{};
        if (!df) {
            if (self.sidebar_visible) {
                // Fitted to the window as a drag would leave it, so a width
                // kept from a wider one doesn't crowd out the editors. In a
                // window narrower than the minimum it takes the whole width,
                // so its × stays in view.
                right = @min(sidebarFit(self.sidebar_width, self.size.x), self.size.x);
                self.sidebar_rect = .{ .x = self.size.x - right, .y = top, .w = right, .h = bottom - top };
            } else {
                right = sidebar_strip_w;
                self.sidebar_strip_rect = .{ .x = self.size.x - right, .y = top, .w = right, .h = bottom - top };
            }
        }
        if (self.panel.visible and !df) {
            const h = panel_ui.height(self, bottom - top);
            self.panel.rect = .{ .x = left, .y = bottom - h, .w = self.size.x - left - right, .h = h };
            bottom -= h;
        } else self.panel.rect = .{};
        if (self.find_visible and self.find != null) {
            const h = self.find.?.height();
            self.find_rect = .{ .x = left, .y = bottom - h, .w = self.size.x - left - right, .h = h };
            bottom -= h;
        } else self.find_rect = .{};
        self.editor_rect = .{ .x = left, .y = top, .w = @max(0, self.size.x - left - right), .h = @max(0, bottom - top) };
        if (df and self.groupCount() == 1) {
            // Distraction free: a centered column of text.
            const max_w: f32 = 900;
            if (self.editor_rect.w > max_w) {
                self.editor_rect.x += (self.editor_rect.w - max_w) / 2;
                self.editor_rect.w = max_w;
            }
        }

        // Groups: the cells of the split tree, at the (draggable) splitter
        // fractions; a zoomed group takes the whole area while it has the focus.
        const area = self.editor_rect;
        if (self.zoomed_group) |z| if (z != self.focused_group) {
            self.zoomed_group = null;
        };
        const cells = self.group_layout.cells();
        self.group_tabbar_rects = @splat(.{});
        self.group_editor_rects = @splat(.{});
        for (cells[0..self.groupCount()], 0..) |fractions, g| {
            if (!self.groupShown(g)) continue;
            const f = if (self.zoomed_group == null) fractions else [4]f32{ 0, 0, 1, 1 };
            // Leave a 1px line between neighbouring groups.
            const x0 = @round(area.x + area.w * f[0]) + @as(f32, if (f[0] > 0) 1 else 0);
            const y0 = @round(area.y + area.h * f[1]) + @as(f32, if (f[1] > 0) 1 else 0);
            const cell: Rect = .{ .x = x0, .y = y0, .w = @max(0, @round(area.x + area.w * f[2]) - x0), .h = @max(0, @round(area.y + area.h * f[3]) - y0) };
            if (self.tabs_visible and !df) {
                self.group_tabbar_rects[g] = .{ .x = cell.x, .y = cell.y, .w = cell.w, .h = tabbar_h };
                self.group_editor_rects[g] = .{ .x = cell.x, .y = cell.y + tabbar_h, .w = cell.w, .h = @max(0, cell.h - tabbar_h) };
            } else self.group_editor_rects[g] = cell;
        }
    }

    /// Group `g` is on screen: every group, or the zoomed one.
    fn groupShown(self: *const Workspace, g: usize) bool {
        return if (self.zoomed_group) |z| z == g else true;
    }

    pub fn paint(self: *Workspace, scene: *Scene) void {
        self.layout();
        const ctx: ui.Ctx = .{ .scene = scene, .text = self.env.text, .theme = self.theme, .fonts = self.fonts, .mouse = self.mouse };
        const theme = self.theme;
        scene.fill(.{ .x = 0, .y = 0, .w = self.size.x, .h = self.size.y }, if (self.distraction_free) theme.background else theme.window_background);

        for (0..self.groupCount()) |g| {
            if (!self.groupShown(g)) continue;
            const er = self.group_editor_rects[g];
            // Squeezed out by a side bar that fills the window: laid out at
            // zero width, its scroll thumb would land on the side bar.
            if (er.w == 0) continue;
            const ed = self.group_active[g] orelse {
                scene.fill(er, theme.window_background);
                continue;
            };
            const focused_group = g == self.focused_group;
            ed.focused = focused_group and self.focus == .editor;
            if (self.key_hook) |h| ed.block_caret = h.enabled and h.blockCaret(h.ctx);
            ed.layout(er);
            if (terminal_ui.paint(self, ed, er, scene)) continue;
            if (settings_ui.paint(self, ed, er, scene)) continue;
            if (focused_group and self.find_visible) if (self.find) |f| if (f.mode == .buffer) f.update(ed);
            if (isFindResults(ed) and ed.highlights.items.len != self.result_ranges.items.len and !(focused_group and self.find_visible)) {
                ed.highlights.clearRetainingCapacity();
                ed.highlights.appendSlice(self.gpa, self.result_ranges.items) catch {};
            }
            ed.paint(scene, theme);
        }
        // Lines between groups.
        var splitters: [max_groups]groups.Splitter = undefined;
        if (self.zoomed_group == null) for (self.group_layout.splitters(&splitters)) |s| scene.fill(self.splitterRect(s), theme.tab_separator);

        panel_ui.paint(self, &ctx);

        self.tab_hits.clearRetainingCapacity();
        if (self.tabs_visible and !self.distraction_free) {
            for (0..self.groupCount()) |g| if (self.groupShown(g)) self.paintTabs(&ctx, @intCast(g));
        }
        if (!self.distraction_free) {
            if (self.sidebar_visible) self.paintSidebar(&ctx) else self.paintSidebarStrip(&ctx);
        }
        if (self.find_visible) if (self.find) |f| f.paint(&ctx, self.find_rect);
        self.panel.status_item = .{};
        if (self.status_visible and !self.distraction_free) self.paintStatus(&ctx);
        if (self.menu_visible and !self.distraction_free) {
            self.paintMenuBar(&ctx);
            toolbar.paint(self, &ctx);
        }

        {
            scene.pushLayer() catch return;
            defer scene.popLayer();
            lsp_ui.paint(self, &ctx);
        }
        if (leader_ui.visible(self)) {
            scene.pushLayer() catch return;
            defer scene.popLayer();
            leader_ui.paint(self, &ctx);
        }
        if (self.overlay) |o| {
            scene.pushLayer() catch return;
            defer scene.popLayer();
            o.input.focused = true;
            o.paint(&ctx, self.editor_rect);
        }
        if (self.menuIsOpen()) {
            scene.pushLayer() catch return;
            defer scene.popLayer();
            self.paintMenuDropdown(&ctx);
        }
    }

    fn paintTabs(self: *Workspace, ctx: *const ui.Ctx, group: u8) void {
        const theme = ctx.theme;
        const scene = ctx.scene;
        const bar = self.group_tabbar_rects[group];
        scene.fill(bar, theme.tabbar_background);
        scene.fill(.{ .x = bar.x, .y = bar.bottom() - 1, .w = bar.w, .h = 1 }, theme.tab_separator);
        var index_buf: [256]usize = undefined;
        const indices = self.groupTabs(group, &index_buf);
        const n = indices.len;
        if (n == 0) return;

        const pad_x: f32 = 10;
        const close_w: f32 = 16;
        var widths = self.gpa.alloc(f32, n) catch return;
        defer self.gpa.free(widths);
        var total: f32 = 0;
        for (indices, 0..) |ti, i| {
            const title = tabTitle(self.tabs.items[ti].editor);
            widths[i] = std.math.clamp(self.env.text.measure(title, self.fonts.ui) + pad_x * 2 + close_w + 8, 80, 260);
            total += widths[i];
        }
        const avail = bar.w;
        if (total > avail) {
            const each = @max(70, avail / @as(f32, @floatFromInt(n)));
            for (widths) |*w| w.* = @min(w.*, each);
        }

        scene.pushClip(bar) catch return;
        defer scene.popClip();
        var x = bar.x;
        const group_focused = group == self.focused_group or self.groupCount() == 1;
        for (indices, 0..) |ti, i| {
            const t = self.tabs.items[ti];
            const w = widths[i];
            const r: Rect = .{ .x = x, .y = bar.y, .w = w, .h = bar.h };
            const active = t.editor == self.group_active[group];
            const hovered = ctx.hovered(r);
            const bg = if (active) theme.background else if (hovered) theme.tab_hover else theme.tab_inactive;
            scene.fill(r, bg);
            // The active tab carries the accent along its top edge and joins the editor below.
            if (active) scene.fill(.{ .x = r.x, .y = r.y, .w = r.w, .h = 2 }, if (group_focused) theme.input_border_focus else theme.tab_separator);

            var title_buf: [300]u8 = undefined;
            const base = tabTitle(t.editor);
            const dirty = t.editor.buffer.isDirty();
            const title = if (dirty) std.fmt.bufPrint(&title_buf, "{s} *", .{base}) catch base else base;
            const fg = if (active) theme.tab_active_foreground else theme.tab_inactive_foreground;
            const label_rect: Rect = .{ .x = r.x + pad_x, .y = r.y + 1, .w = r.w - pad_x - close_w - 12, .h = r.h - 1 };
            scene.pushClip(label_rect) catch {};
            _ = ctx.labelFit(label_rect, title, self.fonts.ui, if (t.transient) fg.withAlpha(0.8) else fg);
            scene.popClip();

            const close: Rect = .{ .x = r.right() - close_w - 6, .y = r.y + (r.h - close_w) / 2 + 1, .w = close_w, .h = close_w };
            self.tab_hits.append(self.gpa, .{ .rect = r, .close = close, .index = ti }) catch {};
            const close_hovered = ctx.hovered(close);
            if (close_hovered) scene.quad(close, .{ .color = theme.tab_hover.lighten(0.08), .radius = 3 });
            ctx.closeIcon(close.x + close.w / 2, close.y + close.h / 2, 8, if (active or hovered) fg else fg.withAlpha(0.85));
            x += w;
        }
    }

    pub fn tabTitle(ed: *Editor) []const u8 {
        return ed.buffer.fileName() orelse "untitled";
    }

    fn paintSidebar(self: *Workspace, ctx: *const ui.Ctx) void {
        const theme = ctx.theme;
        const scene = ctx.scene;
        const sb = self.sidebar_rect;
        scene.fill(sb, theme.sidebar_background);
        scene.fill(.{ .x = sb.x, .y = sb.y, .w = 1, .h = sb.h }, theme.tab_separator);
        // Title row: the Explorer's name, and a drop-down, pin and close on the right.
        const close = self.sidebarCloseRect();
        if (ctx.hovered(close)) scene.quad(close, .{ .color = theme.sidebar_hover, .radius = 3 });
        ctx.closeIcon(close.x + close.w / 2, close.y + close.h / 2, 8, theme.sidebar_foreground);
        const title_y = sb.y;
        _ = ctx.label(.{ .x = sb.x + 8, .y = title_y, .w = 120, .h = 30 }, if (self.project != null) "Explorer" else "Open Files", self.fonts.ui, theme.sidebar_foreground);
        {
            const cy = title_y + 15;
            var gx = close.x - 14;
            // Pin: a small tilted pin drawn with a head and a shaft.
            scene.quad(.{ .x = gx - 2, .y = cy - 5, .w = 4, .h = 6 }, .{ .color = theme.sidebar_foreground, .radius = 1 });
            scene.fill(.{ .x = gx - 3.5, .y = cy + 1, .w = 7, .h = 1.5 }, theme.sidebar_foreground);
            scene.fill(.{ .x = gx - 0.5, .y = cy + 2.5, .w = 1, .h = 4 }, theme.sidebar_foreground);
            gx -= 20;
            ctx.chevron(gx, cy, true, theme.sidebar_foreground);
        }
        const heading_w = @max(0, close.x - 4 - (sb.x + 16));
        const p = self.project orelse {
            var y = sb.y + 34;
            const active_ed = self.activeEditor();
            for (0..self.groupCount()) |g| {
                if (self.groupCount() > 1) {
                    var gbuf: [16]u8 = undefined;
                    const label = std.fmt.bufPrint(&gbuf, "GROUP {d}", .{g + 1}) catch "";
                    _ = ctx.label(.{ .x = sb.x + 16, .y = y, .w = sb.w - 32, .h = sidebar_row_h }, label, self.fonts.ui_small, theme.sidebar_heading);
                    y += sidebar_row_h;
                }
                for (self.tabs.items) |t| if (t.group == g) {
                    const row: Rect = .{ .x = sb.x + 1, .y = y, .w = sb.w - 1, .h = sidebar_row_h };
                    if (t.editor == active_ed) scene.fill(row, theme.sidebar_selected) else if (ctx.hovered(row)) scene.fill(row, theme.sidebar_hover);
                    _ = ctx.labelFit(.{ .x = sb.x + 22, .y = y, .w = sb.w - 30, .h = sidebar_row_h }, tabTitle(t.editor), self.fonts.ui, theme.sidebar_foreground);
                    y += sidebar_row_h;
                };
            }
            return;
        };
        _ = heading_w;
        self.refreshSidebarRows();
        // Tool row (new file, new folder, refresh, collapse all) and the filter box.
        {
            const cy = sb.y + 30 + 15;
            var ix = sb.right() - 20;
            const dim = Color.hex(0xc5c5c5);
            // Collapse all: two overlapping boxes.
            scene.quad(.{ .x = ix - 3, .y = cy - 5, .w = 8, .h = 8 }, .{ .border_color = dim, .border_width = 1.25, .radius = 1 });
            scene.quad(.{ .x = ix - 6, .y = cy - 2, .w = 8, .h = 8 }, .{ .color = theme.sidebar_background, .border_color = dim, .border_width = 1.25, .radius = 1 });
            ix -= 22;
            // Refresh: an open ring.
            scene.quad(.{ .x = ix - 5, .y = cy - 5, .w = 10, .h = 10 }, .{ .border_color = dim, .border_width = 1.5, .radius = 5 });
            scene.fill(.{ .x = ix + 1, .y = cy - 6, .w = 6, .h = 3 }, theme.sidebar_background);
            scene.fill(.{ .x = ix + 1, .y = cy - 7, .w = 5, .h = 1.5 }, dim);
            scene.fill(.{ .x = ix + 4.5, .y = cy - 7, .w = 1.5, .h = 4.5 }, dim);
            ix -= 22;
            ctx.folderIcon(ix - 7, cy + 1, theme.folder_icon, false);
            scene.fill(.{ .x = ix + 4, .y = cy - 7, .w = 1.5, .h = 6 }, Color.hex(0x75beff));
            scene.fill(.{ .x = ix + 1.75, .y = cy - 4.75, .w = 6, .h = 1.5 }, Color.hex(0x75beff));
            ix -= 22;
            ctx.fileIcon(ix - 6, cy, dim);
            scene.fill(.{ .x = ix + 3, .y = cy - 7, .w = 1.5, .h = 6 }, Color.hex(0x75beff));
            scene.fill(.{ .x = ix + 0.75, .y = cy - 4.75, .w = 6, .h = 1.5 }, Color.hex(0x75beff));
            const filter: Rect = .{ .x = sb.x + 8, .y = sb.y + 64, .w = sb.w - 16, .h = 24 };
            scene.quad(filter, .{ .color = theme.input_background, .radius = 3 });
            const mc = filter.y + filter.h / 2;
            scene.quad(.{ .x = filter.x + 8, .y = mc - 5, .w = 8, .h = 8 }, .{ .border_color = theme.list_foreground_dim, .border_width = 1.25, .radius = 4 });
            for (0..3) |i| {
                const t: f32 = @floatFromInt(i);
                scene.fill(.{ .x = filter.x + 15 + t, .y = mc + 2 + t, .w = 1.5, .h = 1.5 }, theme.list_foreground_dim);
            }
            _ = ctx.label(.{ .x = filter.x + 26, .y = filter.y, .w = filter.w - 30, .h = filter.h }, "Filter", self.fonts.ui, theme.list_foreground_dim);
        }
        const body: Rect = .{ .x = sb.x + 1, .y = sb.y + sidebar_header_h, .w = sb.w - 1, .h = @max(0, sb.h - sidebar_header_h) };
        scene.pushClip(body) catch return;
        defer scene.popClip();

        const active_rel: ?[]const u8 = blk: {
            const ed = self.activeEditor() orelse break :blk null;
            break :blk p.relativePath(ed.buffer.path orelse break :blk null);
        };

        const indent: f32 = 14;
        const base_x = sb.x + 6;
        var y = sb.y + sidebar_header_h - self.sidebar_scroll;

        // Root folder row.
        self.paintSidebarRow(ctx, .{ .x = sb.x + 1, .y = y, .w = sb.w - 1, .h = sidebar_row_h }, base_x, p.name, .dir, self.root_expanded, false, false);
        y += sidebar_row_h;

        const snap = p.snapshot orelse return;
        for (self.sidebar_rows.items) |idx| {
            if (y > sb.bottom()) break;
            if (y + sidebar_row_h < body.y) {
                y += sidebar_row_h;
                continue;
            }
            const e = snap.entries[idx];
            const x = base_x + indent * @as(f32, @floatFromInt(e.depth + 1));
            const row: Rect = .{ .x = sb.x + 1, .y = y, .w = sb.w - 1, .h = sidebar_row_h };
            const selected = active_rel != null and std.mem.eql(u8, active_rel.?, e.path);
            self.paintSidebarRow(ctx, row, x, e.name, e.kind, e.kind == .dir and p.isExpanded(e.path), selected, e.ignored);
            y += sidebar_row_h;
        }
    }

    fn paintSidebarRow(self: *Workspace, ctx: *const ui.Ctx, row: Rect, x: f32, name: []const u8, kind: project_mod.Kind, expanded: bool, selected: bool, dim: bool) void {
        const theme = ctx.theme;
        // Not from the pinned header over a row scrolled part-way under it.
        const hovered = ctx.hovered(row) and ctx.mouse.y >= self.sidebar_rect.y + sidebar_header_h;
        if (selected) ctx.scene.fill(row, theme.sidebar_selected) else if (hovered) ctx.scene.fill(row, theme.sidebar_hover);
        const cy = row.y + row.h / 2;
        const fg = if (dim) theme.sidebar_foreground_dim.withAlpha(0.6) else theme.sidebar_foreground;
        if (kind == .dir) {
            ctx.chevron(x + 4, cy, expanded, theme.sidebar_foreground_dim);
            ctx.folderIcon(x + 14, cy, if (dim) theme.folder_icon.withAlpha(0.5) else theme.folder_icon, expanded);
        } else {
            ctx.fileIcon(x + 13, cy, if (dim) theme.sidebar_foreground_dim.withAlpha(0.5) else Color.hex(0xc5c5c5));
        }
        const tx = x + 34;
        _ = ctx.labelFit(.{ .x = tx, .y = row.y, .w = row.right() - tx - 8, .h = row.h }, name, self.fonts.ui, fg);
    }

    /// The strip standing in for the hidden side bar: a click brings it back,
    /// a drag pulls it out to a width.
    fn paintSidebarStrip(self: *Workspace, ctx: *const ui.Ctx) void {
        const theme = ctx.theme;
        const r = self.sidebar_strip_rect;
        const hot = ctx.hovered(r) or self.mouse_down_in == .sidebar_strip;
        ctx.scene.fill(r, if (hot) theme.sidebar_hover else theme.sidebar_background);
        ctx.scene.fill(.{ .x = r.x, .y = r.y, .w = 1, .h = r.h }, theme.tab_separator);
        // A small ◂ (the tree's glyph is too wide for the strip), a pixel column at a time.
        const color = if (hot) theme.sidebar_foreground else theme.sidebar_foreground_dim;
        const cy = @round(r.y + r.h / 2);
        for (0..4) |i| {
            const t: f32 = @floatFromInt(i);
            ctx.scene.fill(.{ .x = r.x + 5 - t, .y = cy - 3 + t, .w = 1, .h = 7 - 2 * t }, color);
        }
    }

    fn paintStatus(self: *Workspace, ctx: *const ui.Ctx) void {
        const theme = ctx.theme;
        const r = self.status_rect;
        if (self.key_hook) |hook| if (hook.enabled and hook.paintCommandLine(hook.ctx, ctx, r)) return;
        ctx.scene.fill(r, theme.statusbar_background);
        const font = self.fonts.ui;
        const fg = theme.statusbar_foreground;
        // The remote indicator, a green block at the left edge as in cs-code.
        const remote: Rect = .{ .x = r.x, .y = r.y, .w = 26, .h = r.h };
        ctx.scene.fill(remote, Color.hex(0x16825d));
        ctx.labelCentered(remote, "><", self.fonts.ui_bold, Color.hex(0xffffff));
        var x = r.x + 26 + 10;
        var buf: [256]u8 = undefined;
        if (remote_ui.statusLabel(self, &buf)) |t| x += ctx.label(.{ .x = x, .y = r.y, .w = 320, .h = r.h }, t, self.fonts.ui_bold, fg) + 16;

        if (self.key_hook) |hook| if (hook.enabled) {
            const t = hook.statusText(hook.ctx, &buf);
            if (t.len > 0) x += ctx.label(.{ .x = x, .y = r.y, .w = 200, .h = r.h }, t, self.fonts.ui_bold, fg) + 16;
        };
        if (leader_ui.statusText(self, &buf)) |t| x += ctx.label(.{ .x = x, .y = r.y, .w = 200, .h = r.h }, t, self.fonts.ui_bold, fg) + 16;
        if (self.recording) {
            ctx.dot(x + 4, r.y + r.h / 2, 4, Color.hex(0xff6b6b));
            x += 12 + ctx.label(.{ .x = x + 12, .y = r.y, .w = 200, .h = r.h }, "Recording macro", font, fg) + 16;
        }

        // Diagnostics of the active file: errors and warnings.
        var errors: usize = 0;
        var warnings: usize = 0;
        if (self.activeEditor()) |ed| for (ed.buffer.diagnostics.items) |d| switch (d.severity) {
            .err => errors += 1,
            .warning => warnings += 1,
            else => {},
        };
        {
            var dbuf: [48]u8 = undefined;
            const dtext = std.fmt.bufPrint(&dbuf, "✖ {d}   ⚠ {d}", .{ errors, warnings }) catch "";
            x += ctx.label(.{ .x = x, .y = r.y, .w = 120, .h = r.h }, dtext, font, fg) + 16;
        }

        if (settings_ui.statusText(self, &buf)) |t| {
            _ = ctx.label(.{ .x = x, .y = r.y, .w = 600, .h = r.h }, t, font, fg);
        } else if (terminal_ui.statusText(self, &buf)) |t| {
            x += ctx.label(.{ .x = x, .y = r.y, .w = 600, .h = r.h }, t, font, fg) + 20;
            _ = panel_ui.paintStatusItem(self, ctx, r, r.right() - 14);
        } else if (self.activeEditor()) |ed| {
            const sel = ed.primarySelection();
            const p = ed.buffer.offsetToPoint(sel.head);
            const col = ed.displayColumn(sel.head);
            var text: []const u8 = undefined;
            if (ed.selections.items.len > 1) {
                text = std.fmt.bufPrint(&buf, "{d} selection regions", .{ed.selections.items.len}) catch "";
            } else if (!sel.isEmpty()) {
                const a = ed.buffer.offsetToPoint(sel.start());
                const b = ed.buffer.offsetToPoint(sel.end());
                if (a.row == b.row) {
                    text = std.fmt.bufPrint(&buf, "{d} characters selected", .{sel.len()}) catch "";
                } else {
                    text = std.fmt.bufPrint(&buf, "{d} lines, {d} characters selected", .{ b.row - a.row + 1, sel.len() }) catch "";
                }
            } else {
                text = std.fmt.bufPrint(&buf, "Ln {d}, Col {d}", .{ p.row + 1, col + 1 }) catch "";
            }
            // The message under the caret.
            if (lsp_ui.diagnosticAt(ed, sel.head)) |d| {
                const msg = d.message[0..@min(d.message.len, 120)];
                const nl = std.mem.indexOfScalar(u8, msg, '\n') orelse msg.len;
                x += ctx.label(.{ .x = x, .y = r.y, .w = 600, .h = r.h }, msg[0..nl], font, fg) + 16;
            }

            // Right side, rightmost first: position, indentation, encoding, line ending, language.
            var rx = r.right() - 14;
            rx -= ctx.labelRight(.{ .x = r.x, .y = r.y, .w = rx - r.x, .h = r.h }, text, font, fg) + 20;
            var ibuf: [32]u8 = undefined;
            const indent = std.fmt.bufPrint(&ibuf, "{s}: {d}", .{ if (ed.buffer.translate_tabs_to_spaces) "Spaces" else "Tab Size", ed.buffer.tab_size }) catch "";
            rx -= ctx.labelRight(.{ .x = r.x, .y = r.y, .w = rx - r.x, .h = r.h }, indent, font, fg) + 20;
            rx -= ctx.labelRight(.{ .x = r.x, .y = r.y, .w = rx - r.x, .h = r.h }, "UTF-8", font, fg) + 20;
            rx -= ctx.labelRight(.{ .x = r.x, .y = r.y, .w = rx - r.x, .h = r.h }, if (ed.buffer.line_ending == .crlf) "CRLF" else "LF", font, fg) + 20;
            var lbuf: [96]u8 = undefined;
            const lang_label = if (ed.buffer.syntax_skipped) std.fmt.bufPrint(&lbuf, "{s} (large file: no highlighting)", .{ed.buffer.language.name}) catch ed.buffer.language.name else ed.buffer.language.name;
            rx -= ctx.labelRight(.{ .x = r.x, .y = r.y, .w = rx - r.x, .h = r.h }, lang_label, font, fg) + 20;
            // A running org clock: its heading and time so far.
            var cbuf: [160]u8 = undefined;
            if (org_ui.clockStatus(self, &cbuf)) |ct| rx -= ctx.labelRight(.{ .x = r.x, .y = r.y, .w = rx - r.x, .h = r.h }, ct, font, fg) + 20;
            // The language server: its state and progress; a click opens its log.
            _ = panel_ui.paintStatusItem(self, ctx, r, rx);
        }
        if (self.status_message_len > 0 and platform.nowMs() < self.status_message_until) {
            _ = ctx.label(.{ .x = x, .y = r.y, .w = 500, .h = r.h }, self.status_message[0..self.status_message_len], font, fg);
        }
    }

    fn paintMenuBar(self: *Workspace, ctx: *const ui.Ctx) void {
        const theme = ctx.theme;
        const r = self.menubar_rect;
        ctx.scene.fill(r, theme.menubar_background);
        var x = r.x + 6;
        for (menu.menus, 0..) |m, i| {
            const w = self.env.text.measure(m.title, self.fonts.ui) + 18;
            const tr: Rect = .{ .x = x, .y = r.y + 2, .w = w, .h = r.h - 4 };
            self.menu_title_rects[i] = tr;
            if (self.menu_open == i) {
                ctx.scene.quad(tr, .{ .color = theme.menu_selected, .radius = 4 });
            } else if (ctx.hovered(tr)) {
                ctx.scene.quad(tr, .{ .color = theme.sidebar_hover, .radius = 4 });
            }
            ctx.labelCentered(tr, m.title, self.fonts.ui, theme.menubar_foreground);
            x += w;
        }
    }

    /// The open menu: a menu bar menu under its title, or a right-click
    /// menu at its anchor, kept inside the window.
    fn paintMenuDropdown(self: *Workspace, ctx: *const ui.Ctx) void {
        const items = self.openMenuItems() orelse return;
        const theme = ctx.theme;
        var buf: [64]u8 = undefined;
        var width: f32 = if (self.context_menu != null) 180 else 220;
        for (items) |item| if (item != .separator) {
            const sc = if (item == .action) self.keymap.shortcutFor(item.action.action, &buf) orelse "" else "";
            width = @max(width, self.env.text.measure(item.caption(), self.fonts.ui) + self.env.text.measure(sc, self.fonts.ui_small) + 70);
        };
        var h: f32 = 8;
        for (items) |item| h += if (item == .separator) 9 else 26;
        const panel: Rect = if (self.context_menu) |m|
            context_menu.place(m.anchor, width, h, .{ .w = self.size.x, .h = self.size.y })
        else blk: {
            const title_rect = self.menu_title_rects[self.menu_open.?];
            break :blk .{ .x = title_rect.x, .y = title_rect.bottom() + 2, .w = width, .h = h };
        };
        self.menu_rect = panel;
        ctx.shadow(panel, 6, 10);
        ctx.scene.quad(panel, .{ .color = theme.menu_background, .border_color = theme.panel_border, .border_width = 1, .radius = 6 });
        self.menu_item_rects.clearRetainingCapacity();
        var y = panel.y + 4;
        for (items, 0..) |item, i| {
            switch (item) {
                .separator => {
                    const r: Rect = .{ .x = panel.x, .y = y, .w = panel.w, .h = 9 };
                    self.menu_item_rects.append(self.gpa, r) catch {};
                    ctx.scene.fill(.{ .x = panel.x + 10, .y = y + 4, .w = panel.w - 20, .h = 1 }, theme.menu_separator);
                    y += 9;
                },
                .action, .command => {
                    const r: Rect = .{ .x = panel.x + 4, .y = y, .w = panel.w - 8, .h = 26 };
                    self.menu_item_rects.append(self.gpa, r) catch {};
                    if (self.menu_hover_item == i) ctx.scene.quad(r, .{ .color = theme.menu_selected, .radius = 4 });
                    _ = ctx.label(.{ .x = r.x + 14, .y = r.y, .w = r.w, .h = r.h }, item.caption(), self.fonts.ui, theme.menubar_foreground);
                    if (item == .action) if (self.keymap.shortcutFor(item.action.action, &buf)) |sc| {
                        _ = ctx.labelRight(.{ .x = r.x, .y = r.y, .w = r.w - 12, .h = r.h }, sc, self.fonts.ui_small, theme.menu_shortcut);
                    };
                    y += 26;
                },
            }
        }
    }

    /// Timers: the org C-c prefix falls back to copy, the which-key popup shows.
    pub fn tick(self: *Workspace) void {
        if (self.overlay == null and (self.reload_prompt != null or self.reload_queue.items.len > 0)) self.nextReloadPrompt();
        org_ui.tick(self);
        leader_ui.tick(self);
        if (self.lsp) |l| l.flush();
        lsp_ui.tick(self);
        git_ui.tick(self);
        git_status_ui.tick(self);
        remote_ui.tick(self);
    }

    /// Time until something animated (caret blink, status message) changes.
    pub fn nextRedrawIn(self: *Workspace) ?i64 {
        if (self.org_keys.prefix_since) |since| return @max(1, 1000 - (platform.nowMs() - since));
        const caret_ms: ?i64 = blk: {
            const ed = self.focusedEditor() orelse break :blk null;
            if (!ed.focused or !self.env.settings.caret_blink) break :blk null;
            const elapsed = platform.nowMs() - ed.caret_epoch;
            break :blk 500 - @mod(elapsed, 500);
        };
        const blame_ms = git_ui.nextTimer(self);
        const status_ms = git_status_ui.nextTimer(self);
        const git_ms = if (blame_ms) |b| (if (status_ms) |st| @min(b, st) else b) else status_ms;
        const caret_or_git = if (caret_ms) |cm| (if (git_ms) |g| @min(cm, g) else cm) else git_ms;
        const leader_ms = leader_ui.nextTimer(self);
        const local_soonest = if (caret_or_git) |l| (if (leader_ms) |w| @min(l, w) else l) else leader_ms;
        // Reconnect attempts after a dropped Remote-SSH connection.
        const remote_ms = remote_ui.nextTimer(self);
        var soonest = if (local_soonest) |l| (if (remote_ms) |r| @min(l, r) else l) else remote_ms;
        // A running org clock's minutes in the status bar.
        if (org_ui.nextTimer(self)) |t| soonest = if (soonest) |s| @min(s, t) else t;
        if (lsp_ui.nextTimer(self)) |t| return if (soonest) |s| @min(s, t) else t;
        return soonest;
    }
};

test "dragging the side bar's edge: collapse below half the minimum width" {
    const expectEqual = std.testing.expectEqual;
    const shown = struct {
        fn f(w: f32) ui_state.State {
            return .{ .sidebar_visible = true, .sidebar_width = w };
        }
    }.f;
    const collapsed: ui_state.State = .{ .sidebar_visible = false, .sidebar_width = 320 };
    // A 320 px side bar's edge dragged smoothly to the left edge and back out.
    try expectEqual(shown(300), sidebarDrag(320, 299.6, 1280));
    // Between the collapse point and the 120 px minimum it stays at the minimum,
    try expectEqual(shown(120), sidebarDrag(320, 100, 1280));
    try expectEqual(shown(120), sidebarDrag(320, 60, 1280));
    // and below that it collapses (hidden if the drag ends there), keeping
    // the width it had before the drag rather than the minimum it passed.
    try expectEqual(collapsed, sidebarDrag(320, 59.5, 1280));
    try expectEqual(collapsed, sidebarDrag(320, 0, 1280));
    try expectEqual(shown(200), sidebarDrag(320, 200, 1280));
    // At most 60% of the window, but never below the minimum, even in a narrow one.
    try expectEqual(shown(768), sidebarDrag(320, 1000, 1280));
    try expectEqual(shown(120), sidebarDrag(320, 150, 150));
    try expectEqual(@as(f32, 768), sidebarFit(5000, 1280));
}

test "closed paths: each once, newest last, at most the latest hundred" {
    const gpa = std.testing.allocator;
    var closed: ClosedPaths = .{};
    defer closed.deinit(gpa);
    // Opening and closing the same files over and over keeps one entry each.
    for (0..50) |_| {
        closed.record(gpa, "/p/a.zig");
        closed.record(gpa, "/p/b.zig");
    }
    try std.testing.expectEqual(@as(usize, 2), closed.items.items.len);
    closed.record(gpa, "/p/a.zig");
    try std.testing.expectEqualStrings("/p/b.zig", closed.items.items[0]);
    // A deleted file can't be reopened.
    closed.forget(gpa, "/p/b.zig");
    try std.testing.expectEqual(@as(usize, 1), closed.items.items.len);
    // Past the cap the oldest go.
    var buf: [32]u8 = undefined;
    for (0..250) |i| closed.record(gpa, try std.fmt.bufPrint(&buf, "/p/{d}.zig", .{i}));
    try std.testing.expectEqual(@as(usize, ClosedPaths.max), closed.items.items.len);
    try std.testing.expectEqualStrings("/p/150.zig", closed.items.items[0]);
    const newest = closed.pop().?;
    defer gpa.free(newest);
    try std.testing.expectEqualStrings("/p/249.zig", newest);
}
