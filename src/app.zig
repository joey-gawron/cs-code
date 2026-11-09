//! Application shell: wires the three layers together. Owns the (optional)
//! window or headless target, GPU resources, text system, workspace, language
//! servers and the session server, and runs the event loop.
//!
//! The window can come and go: in a session daemon, closing it keeps every
//! buffer, project scan and language server alive for the next client.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");

// Layer 1: framework.
const window_mod = @import("framework/platform/window.zig");
const system_clipboard = @import("framework/platform/system_clipboard.zig");
const Headless = @import("framework/platform/headless.zig").Headless;
const platform = @import("framework/platform/platform.zig");
const gl = @import("framework/gpu/gl.zig");
const scene_mod = @import("framework/gpu/scene.zig");
const Renderer = @import("framework/gpu/renderer.zig").Renderer;
const Atlas = @import("framework/gpu/atlas.zig").Atlas;
const text_mod = @import("framework/text/text_system.zig");
const theme_mod = @import("framework/ui/theme.zig");
const ui = @import("framework/ui/ui.zig");
const Executor = @import("framework/util/executor.zig").Executor;
const png = @import("framework/util/png.zig");

// Layer 2: model.
const syntax_mod = @import("model/syntax/syntax.zig");
const lsp_mod = @import("model/lsp/lsp.zig");

// Layer 3: app.
const editor_mod = @import("app/editor/editor.zig");
const editor_view = @import("app/editor/editor_view.zig");
const keymap_mod = @import("app/settings/keymap.zig");
const workspace_mod = @import("app/workspace/workspace.zig");
const lsp_ui = @import("app/workspace/lsp_ui.zig");
const vim_mod = @import("app/vim/vim.zig");
const theme_manager = @import("app/settings/theme_manager.zig");
const settings_file = @import("app/settings/settings_file.zig");
const session = @import("app/session/session.zig");
const persist = @import("app/session/persist.zig");
const app_icon = @import("app/app_icon.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Window = window_mod.Window;
const log = std.log.scoped(.app);

pub const App = struct {
    gpa: Allocator,
    io: Io,
    window: ?Window = null,
    backend: ?window_mod.Backend = null,
    headless: ?Headless = null,
    /// Renderer and atlas exist only while a GL context does.
    gfx: bool = false,
    renderer: Renderer = undefined,
    atlas: Atlas = .{ .size = 1024 },
    text: text_mod.TextSystem,
    scene: scene_mod.Scene,
    settings: editor_mod.Settings = .{},
    env: editor_mod.Env,
    /// Points at `themes.theme`, which changes in place when the scheme switches.
    theme: *const theme_mod.Theme = undefined,
    themes: theme_manager.ThemeManager,
    /// Owns string settings (font names) loaded from settings.json.
    settings_arena: std.heap.ArenaAllocator,
    registry: syntax_mod.Registry,
    executor: Executor,
    keymap: keymap_mod.Keymap,
    workspace: workspace_mod.Workspace,
    vim: vim_mod.Vim,
    lsp: lsp_mod.Lsp,
    /// Clipboard used when there is no window.
    clipboard: ?[]u8 = null,
    scale: f32 = 1,
    last_caret_visible: bool = true,

    /// Background work posts here to wake the loop: an eventfd on Linux, a
    /// pipe (read end, write end) on macOS.
    wake_fd: i32,
    wake_write_fd: i32,
    server: ?session.Server = null,
    /// Session daemon: closing the window keeps the process (and its state) alive.
    keep_running: bool = false,
    /// Writes the session's file as it changes (null: standalone).
    autosave: ?persist.Autosave = null,
    /// The quit was decided in the window (after its save prompts).
    quit_by_user: bool = false,
    window_close_requested: bool = false,
    /// The text field the input method was last enabled for (compared, never dereferenced).
    ime_field: ?*const anyopaque = null,
    quit: bool = false,
    poll_fds: std.ArrayList(platform.PollFd) = .empty,

    pub const Options = struct {
        headless_size: ?[2]u32 = null,
        scale: f32 = 1,
        enable_lsp: bool = true,
        /// Open a window immediately (a daemon starts without one).
        open_window: bool = true,
        /// Serve the session protocol on this socket.
        socket_path: ?[:0]const u8 = null,
        keep_running: bool = false,
        backend: ?window_mod.Backend = null,
        /// Name of the saved session this process keeps and restores.
        session: ?[]const u8 = null,
        session_unnamed: bool = false,
    };

    pub fn create(gpa: Allocator, io: Io, opts: Options) !*App {
        const self = try gpa.create(App);
        errdefer gpa.destroy(self);
        self.* = .{
            .gpa = gpa,
            .io = io,
            .backend = opts.backend,
            .text = undefined,
            .scene = .init(gpa),
            .env = undefined,
            .registry = undefined,
            .executor = .init(gpa),
            .keymap = try keymap_mod.Keymap.initDefault(gpa),
            .workspace = undefined,
            .vim = undefined,
            .lsp = undefined,
            .themes = .init(),
            .settings_arena = .init(gpa),
            .wake_fd = undefined,
            .wake_write_fd = undefined,
        };
        self.wake_fd, self.wake_write_fd = try makeWakeFds();
        self.theme = &self.themes.theme;
        self.text = try text_mod.TextSystem.init(gpa, &self.atlas);

        if (opts.headless_size) |sz| {
            self.headless = try Headless.init(sz[0], sz[1]);
            try self.initGfx();
            self.scale = opts.scale;
        }

        settings_file.migrateLegacyConfig(gpa, io);
        const loaded = settings_file.load(gpa, io, self.settings_arena.allocator(), &self.settings, &self.themes.settings) catch |err| blk: {
            log.warn("settings.json: {t}", .{err});
            break :blk settings_file.Loaded{};
        };
        // Installed Sublime schemes (the real Breakers, etc.) replace the built-in
        // approximations; this also builds the theme.
        self.themes.loadSchemes(gpa, io);
        _ = self.themes.update(true);
        self.keymap.loadUserFile(gpa, io) catch |err| log.warn("keymap.json: {t}", .{err});
        self.text.setScale(self.scale);
        self.registry = .init(gpa, self.theme);
        if (std.c.getenv("CS_ZIG_GRAMMARS") == null) if (bundledGrammars(io, self.registry.grammar_dir)) |dir| {
            self.registry.grammar_dir = dir;
        };
        self.registry.executor = &self.executor;

        const fonts = try self.makeFonts();
        self.env = .{
            .gpa = gpa,
            .text = &self.text,
            .font = fonts.mono,
            .settings = &self.settings,
            .clipboard = .{ .ctx = self, .get = clipboardGet, .set = clipboardSet },
            .executor = &self.executor,
            .registry = &self.registry,
        };
        self.workspace = .init(gpa, io, &self.env, self.theme, &self.registry, &self.executor, &self.keymap, fonts);
        self.workspace.loadUiState();
        self.vim = vim_mod.Vim.init(gpa);
        self.workspace.key_hook = self.vim.hook();
        self.lsp = lsp_mod.Lsp.init(gpa, io, &self.executor, .{ .ctx = &self.workspace, .onResult = lsp_ui.onResult });
        self.lsp.setConfiguration(loaded.lsp_json);
        if (opts.enable_lsp) self.workspace.lsp = &self.lsp;
        self.workspace.prefs = .{
            .ctx = self,
            .reloadSettings = prefsReload,
            .previewScheme = prefsPreview,
            .chooseScheme = prefsChoose,
            .describeAuto = prefsDescribeAuto,
            .settingsPath = prefsPath,
            .schemes = prefsSchemes,
        };
        self.workspace.file_closed_hook = .{ .ctx = self, .onFileClosed = onFileClosed };
        self.registry.listener = .{ .ctx = self, .onLoaded = languageLoaded };
        if (loaded.vim_mode == true) self.workspace.perform(.toggle_vim_mode);
        self.executor.waker = wakeLoop;
        self.executor.waker_ctx = self;
        self.keep_running = opts.keep_running or self.settings.keep_running_in_background;
        applySyntaxLimit(&self.settings);

        if (self.headless) |h| {
            self.workspace.size = .{ .x = @as(f32, @floatFromInt(h.width)) / self.scale, .y = @as(f32, @floatFromInt(h.height)) / self.scale };
        }
        if (opts.session) |name| self.attachSession(name, opts.session_unnamed);
        if (opts.socket_path) |path| {
            self.server = session.Server.listen(gpa, path) catch |err| blk: {
                log.warn("session server unavailable: {t}", .{err});
                break :blk null;
            };
        }
        if (opts.open_window and self.headless == null) try self.openWindow();
        return self;
    }

    pub fn destroy(self: *App) void {
        // Language servers hold buffer listeners: stop them first.
        self.lsp.deinit();
        // Workspace.deinit still drains (a search, remote quiesce), and a host
        // that connects then opens files: none may reach the freed Lsp.
        self.workspace.lsp = null;
        self.workspace.deinit();
        self.vim.deinit();
        self.registry.deinit();
        self.text.deinit();
        self.deinitGfx();
        self.scene.deinit();
        self.keymap.deinit(self.gpa);
        self.themes.deinit(self.gpa);
        self.settings_arena.deinit();
        if (self.env.clipboard_whole_line) |w| self.gpa.free(w);
        if (self.clipboard) |cb| self.gpa.free(cb);
        // Owners have let their workers go, but a worker may still be on its
        // way out: `taskDone` (after posting, which wakes us) is its last touch
        // of the executor and the app.
        while (self.executor.active.load(.acquire) > 0) std.Thread.yield() catch {};
        self.executor.deinit();
        if (self.server) |*s| s.deinit();
        if (self.autosave) |*a| a.deinit();
        if (self.window) |w| w.destroy();
        if (self.headless) |*h| h.deinit();
        self.poll_fds.deinit(self.gpa);
        _ = c.close(self.wake_fd);
        if (self.wake_write_fd != self.wake_fd) _ = c.close(self.wake_write_fd);
        self.gpa.destroy(self);
    }

    fn makeFonts(self: *App) !ui.Fonts {
        const s = &self.settings;
        const mono = try self.text.font(.{ .family = s.font_family, .size = s.font_size });
        return .{
            .ui = try self.text.font(.{ .family = s.ui_font_family, .size = s.ui_font_size }),
            .ui_small = try self.text.font(.{ .family = s.ui_font_family, .size = s.ui_font_size - 1.5 }),
            .ui_bold = try self.text.font(.{ .family = s.ui_font_family, .size = s.ui_font_size, .weight = .bold }),
            .mono = mono,
        };
    }

    // ---- Window lifecycle --------------------------------------------------

    fn initGfx(self: *App) !void {
        self.renderer = try Renderer.init();
        self.atlas = Atlas.init(1024);
        self.text.resetGlyphs();
        self.gfx = true;
    }

    fn deinitGfx(self: *App) void {
        if (!self.gfx) return;
        self.renderer.deinit();
        self.atlas.deinit();
        self.atlas.texture = 0;
        self.gfx = false;
    }

    pub fn openWindow(self: *App) !void {
        if (self.window != null) return;
        // The window system keeps its own copy of the icon, so the decoded
        // pixels only live through `create`.
        const icons = app_icon.decodeAll(self.gpa) catch |err| blk: {
            log.warn("window icon: {t}", .{err});
            break :blk &.{};
        };
        const created = Window.create(self.gpa, .{ .title = "CS Zig", .icons = icons }, self.backend);
        app_icon.free(self.gpa, icons);
        const w = try created;
        self.window = w;
        errdefer self.closeWindow();
        try self.initGfx();
        self.scale = w.scale();
        self.text.setScale(self.scale);
        self.workspace.size = w.logicalSize();
        self.workspace.host = .{ .ctx = self, .setTitle = hostSetTitle, .setCursor = hostSetCursor, .toggleFullscreen = hostToggleFullscreen };
        self.workspace.dirty = true;
        self.workspace.updateTitle();
    }

    /// Restores saved session `name` into the workspace and starts keeping it.
    fn attachSession(self: *App, name: []const u8, unnamed: bool) void {
        const attached = persist.attach(self.gpa, self.io, &self.workspace, name, unnamed) orelse return;
        self.autosave = attached.autosave;
        self.workspace.session_name = self.gpa.dupe(u8, name) catch null;
        if (attached.message) |m| {
            self.workspace.setStatus("{s}", .{m});
        } else if (attached.notes.restored > 0) {
            self.workspace.setStatus("Restored session {s}: {d} {s}", .{ name, attached.notes.restored, if (attached.notes.restored == 1) "file" else "files" });
        }
    }

    /// Rename Session…: the files, the socket and the autosaver move to the new name.
    fn renameSession(self: *App) void {
        const new = self.workspace.session_rename_to orelse return;
        defer {
            self.gpa.free(new);
            self.workspace.session_rename_to = null;
        }
        const a = &(self.autosave orelse return);
        // Flush under the old name first, so there is a file to rename.
        a.flush(&self.workspace, true);
        a.store.rename(a.name, new) catch |err| return self.workspace.setStatus("Cannot rename session: {t}", .{err});
        if (self.server) |*srv| {
            var buf: [std.fs.max_path_bytes]u8 = undefined;
            if (session.socketPath(&buf, new)) |p| srv.rename(p) catch |err| log.warn("socket not renamed: {t}", .{err});
        }
        a.setName(new) catch {};
        if (self.workspace.session_name) |old| self.gpa.free(old);
        self.workspace.session_name = self.gpa.dupe(u8, new) catch null;
        self.workspace.setStatus("Session renamed to {s}", .{new});
    }

    /// Writes the session file now, keeping unsaved text unless the user quit.
    pub fn saveSession(self: *App, keep_unsaved: bool) void {
        if (self.autosave) |*a| a.flush(&self.workspace, keep_unsaved);
    }

    pub fn closeWindow(self: *App) void {
        const w = self.window orelse return;
        self.saveSession(true);
        // On Linux the clipboard's text goes with its window: hand what we
        // copied to wl-copy or xclip, which keep it available.
        if (w.ownedClipboard()) |text| {
            if (!system_clipboard.set(self.io, text)) {
                if (self.clipboard) |cb| self.gpa.free(cb);
                self.clipboard = self.gpa.dupe(u8, text) catch null;
            }
        }
        self.deinitGfx();
        w.destroy();
        self.window = null;
        self.workspace.host = null;
        // The workspace outlives the window (a session daemon): no leader
        // sequence carries over into the next one.
        self.workspace.leader = .{};
        // Nobody can edit the files clients are waiting on any more.
        if (self.server) |*s| s.releaseAll();
    }

    var bundled_grammars_buf: [std.fs.max_path_bytes]u8 = undefined;

    /// The grammars inside a macOS app bundle (tools/make-dmg.sh puts them in
    /// Contents/Resources/grammars, next to Contents/MacOS/cs-zig).
    /// Where grammars live when $CS_ZIG_GRAMMARS isn't set: beside the binary
    /// (zig build installs them to share/cs-zig/grammars; an app bundle keeps
    /// them in Resources), else the folder the build named, else the per-user
    /// one tools/fetch-grammars.sh fills. Null keeps `build_dir`.
    fn bundledGrammars(io: Io, build_dir: []const u8) ?[]const u8 {
        var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
        if (std.process.executableDirPath(io, &exe_buf)) |n| {
            const rels = [_][]const u8{ "../share/cs-zig/grammars", "../Resources/grammars" };
            for (rels) |rel| {
                const dir = std.fmt.bufPrint(&bundled_grammars_buf, "{s}/{s}", .{ exe_buf[0..n], rel }) catch continue;
                Io.Dir.cwd().access(io, dir, .{}) catch continue;
                return dir;
            }
        } else |_| {}
        if (build_dir.len > 0) if (Io.Dir.cwd().access(io, build_dir, .{})) |_| return null else |_| {};
        const home = std.c.getenv("HOME") orelse return null;
        const dir = std.fmt.bufPrint(&bundled_grammars_buf, "{s}/.local/share/cs-zig/grammars", .{std.mem.span(home)}) catch return null;
        Io.Dir.cwd().access(io, dir, .{}) catch return null;
        return dir;
    }

    fn makeWakeFds() ![2]i32 {
        if (builtin.os.tag == .linux) {
            const fd = c.eventfd(0, c.EFD_CLOEXEC | c.EFD_NONBLOCK);
            if (fd < 0) return error.SystemResources;
            return .{ fd, fd };
        }
        var fds: [2]c_int = undefined;
        if (c.pipe(&fds) != 0) return error.SystemResources;
        for (fds) |fd| {
            _ = c.fcntl(fd, c.F_SETFD, @as(c_int, c.FD_CLOEXEC));
            _ = c.fcntl(fd, c.F_SETFL, c.fcntl(fd, c.F_GETFL) | c.O_NONBLOCK);
        }
        return fds;
    }

    fn wakeLoop(ctx: *anyopaque) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        const one: u64 = 1;
        _ = c.write(self.wake_write_fd, &one, 8);
    }

    /// Copy, cut, paste and vim's unnamed register: the system clipboard,
    /// through the window, or without one (a session daemon between
    /// windows) through system_clipboard; our own copy only as a last resort.
    fn clipboardGet(ctx: *anyopaque, gpa: Allocator) ?[]u8 {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.window) |w| return w.getClipboard(gpa) catch null;
        if (self.headless == null) if (system_clipboard.get(gpa, self.io)) |text| return text;
        return if (self.clipboard) |cb| gpa.dupe(u8, cb) catch null else null;
    }

    fn clipboardSet(ctx: *anyopaque, text: []const u8) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.window) |w| {
            w.setClipboard(text) catch {};
            return;
        }
        if (self.headless == null and system_clipboard.set(self.io, text)) return;
        if (self.clipboard) |cb| self.gpa.free(cb);
        self.clipboard = self.gpa.dupe(u8, text) catch null;
    }

    fn hostSetTitle(ctx: *anyopaque, title: [:0]const u8) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.window) |w| w.setTitle(title);
    }

    fn hostSetCursor(ctx: *anyopaque, shape: platform.CursorShape) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.window) |w| w.setCursor(shape);
    }

    fn hostToggleFullscreen(ctx: *anyopaque) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.window) |w| w.toggleFullscreen();
    }

    // ---- Session requests ------------------------------------------------------

    /// Opens files Finder or the Dock handed to the app as tabs, like a
    /// session request.
    fn openDesktopPaths(self: *App, w: Window) void {
        const list = (w.takeOpenedPaths(self.gpa) catch return) orelse return;
        defer self.gpa.free(list);
        var it = std.mem.splitScalar(u8, list, '\n');
        while (it.next()) |path| {
            if (path.len == 0) continue;
            _ = self.workspace.openArgument("", path) catch |err| {
                self.workspace.setStatus("Unable to open {s}: {t}", .{ path, err });
            };
        }
        self.workspace.dirty = true;
    }

    fn onFileClosed(ctx: *anyopaque, path: []const u8) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.server) |*s| s.fileClosed(path);
    }

    fn onRequest(ctx: *anyopaque, client: *session.Client, req: session.Request) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        const server = &self.server.?;
        switch (req.cmd) {
            .ping => {
                var buf: [32]u8 = undefined;
                server.reply(client, std.fmt.bufPrint(&buf, "ok {d}", .{c.getpid()}) catch "ok");
            },
            .quit => {
                self.saveSession(true);
                server.reply(client, "ok");
                self.quit = true;
            },
            .save => {
                self.saveSession(true);
                server.reply(client, "ok");
            },
            .info => {
                const ws = &self.workspace;
                var unsaved: usize = 0;
                for (ws.buffers.items) |b| {
                    if (b.isDirty() and !b.scratch) unsaved += 1;
                }
                var buf: [std.fs.max_path_bytes + 64]u8 = undefined;
                const folder: []const u8 = if (ws.project) |p| p.root else "-";
                server.reply(client, std.fmt.bufPrint(&buf, "ok {d} {d} {d} {s}", .{ c.getpid(), ws.tabs.items.len, unsaved, folder }) catch "ok");
            },
            .open => {
                self.openWindow() catch |err| {
                    log.err("cannot open a window: {t}", .{err});
                    server.reply(client, "error");
                    return;
                };
                for (req.paths) |raw| {
                    const opened = self.workspace.openArgument(req.cwd, raw) catch |err| {
                        self.workspace.setStatus("Unable to open {s}: {t}", .{ raw, err });
                        continue;
                    };
                    if (req.wait) if (opened) |p| server.addWait(client, p);
                }
                self.workspace.openStartTabs(req.paths.len > 0) catch {};
                self.workspace.dirty = true;
                server.reply(client, "ok");
            },
        }
    }

    // ---- Settings / theme callbacks ----------------------------------------------

    /// A grammar loaded in the background: highlight the buffers waiting for it.
    fn languageLoaded(ctx: *anyopaque, lang: *syntax_mod.Language) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        if (self.workspace.shutting_down) return;
        for (self.workspace.buffers.items) |b| {
            if (b.languageLoaded(lang)) self.workspace.dirty = true;
        }
    }

    fn themeChanged(self: *App) void {
        self.registry.refreshColors();
        self.workspace.dirty = true;
    }

    fn reloadFonts(self: *App) void {
        const fonts = self.makeFonts() catch return;
        self.env.font = fonts.mono;
        self.workspace.fonts = fonts;
    }

    fn prefsReload(ctx: *anyopaque) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        const loaded = settings_file.load(self.gpa, self.io, self.settings_arena.allocator(), &self.settings, &self.themes.settings) catch |err| {
            self.workspace.setStatus("settings.json: {t}", .{err});
            return;
        };
        self.keymap.loadUserFile(self.gpa, self.io) catch |err| self.workspace.setStatus("keymap.json: {t}", .{err});
        // A leader sequence pending meanwhile (the mouse saved keymap.json)
        // ends: its keys and labels were the old file's.
        self.workspace.leader = .{};
        if (loaded.fonts_changed) self.reloadFonts();
        self.lsp.setConfiguration(loaded.lsp_json);
        if (loaded.vim_mode) |want| if (self.workspace.key_hook) |h| if (h.enabled != want) self.workspace.perform(.toggle_vim_mode);
        self.keep_running = self.keep_running or self.settings.keep_running_in_background;
        applySyntaxLimit(&self.settings);
        // Pick up schemes added to ~/.config/cs-zig/schemes since startup.
        self.themes.loadSchemes(self.gpa, self.io);
        _ = self.themes.update(true);
        self.themeChanged();
    }

    fn prefsPreview(ctx: *anyopaque, name: ?[]const u8) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        self.themes.setPreview(name);
        if (self.themes.update(false)) self.themeChanged();
    }

    fn prefsChoose(ctx: *anyopaque, name: []const u8) void {
        const self: *App = @ptrCast(@alignCast(ctx));
        self.themes.settings.color_scheme.set(name);
        self.themes.setPreview(null);
        settings_file.setString(self.gpa, self.io, "color_scheme", name) catch |err| {
            self.workspace.setStatus("Couldn't save color_scheme: {t}", .{err});
        };
        if (self.themes.update(true)) self.themeChanged();
    }

    fn prefsDescribeAuto(ctx: *anyopaque, buf: []u8) []const u8 {
        const self: *App = @ptrCast(@alignCast(ctx));
        const info = self.themes.dayInfo();
        const s = &self.themes.settings;
        const now_name = if (info.is_day) s.light_color_scheme.get() else s.dark_color_scheme.get();
        const next_name = if (info.is_day) s.dark_color_scheme.get() else s.light_color_scheme.get();
        const at = if (info.is_day) info.night_start else info.day_start;
        if (at >= 1440 or at < 0) return std.fmt.bufPrint(buf, "{s} (polar {s})", .{ now_name, if (info.is_day) "day" else "night" }) catch "";
        const h: u32 = @intFromFloat(@floor(at / 60));
        const m: u32 = @intFromFloat(@mod(@floor(at), 60));
        const why = if (info.from_sun) (if (info.is_day) "sunset" else "sunrise") else "scheduled";
        return std.fmt.bufPrint(buf, "{s} now, {s} at {d:0>2}:{d:0>2} ({s})", .{ now_name, next_name, h, m, why }) catch "";
    }

    fn applySyntaxLimit(settings: *const editor_mod.Settings) void {
        const mb = settings.syntax_max_file_size_mb;
        @import("model/text/buffer.zig").syntax_size_limit = if (mb == 0) std.math.maxInt(usize) else @as(usize, mb) << 20;
    }

    fn prefsSchemes(ctx: *anyopaque) []const theme_mod.Scheme {
        const self: *App = @ptrCast(@alignCast(ctx));
        return self.themes.schemes();
    }

    fn prefsPath(_: *anyopaque, buf: []u8) ?[]const u8 {
        return settings_file.path(buf);
    }

    /// Auto day/night: re-evaluate at most every 30 s.
    fn checkTheme(self: *App) void {
        if (!self.themes.isAuto()) return;
        const now = platform.nowMs();
        if (now - self.themes.last_check < 30_000) return;
        self.themes.last_check = now;
        if (self.themes.update(false)) self.themeChanged();
    }

    // ---- Rendering and events -------------------------------------------------------

    fn deviceSize(self: *App) [2]u32 {
        if (self.window) |w| {
            const bw, const bh = w.bufferSize();
            return .{ @intCast(@max(1, bw)), @intCast(@max(1, bh)) };
        }
        if (self.headless) |h| return .{ h.width, h.height };
        return .{ 1, 1 };
    }

    pub fn render(self: *App) void {
        if (!self.gfx) return;
        const size = self.deviceSize();
        self.scene.begin(@floatFromInt(size[0]), @floatFromInt(size[1]), self.scale) catch return;
        self.workspace.paint(&self.scene);
        if (self.headless) |h| gl.p.BindFramebuffer(gl.FRAMEBUFFER, h.fbo);
        self.renderer.draw(&self.scene, &self.atlas, self.theme.window_background);
        self.text.endFrame();
        self.workspace.dirty = false;
        if (self.text.atlas_full) {
            // The frame needed more glyphs than fit: grow and redraw.
            self.atlas.reset(true);
            self.text.atlas_full = false;
            self.workspace.dirty = true;
        }
        if (self.window) |w| {
            const field = self.workspace.focusedEditor();
            const id: ?*const anyopaque = if (field) |f| f else null;
            if (id != self.ime_field) {
                // text-input-v3 wants a fresh enable for each text field; a
                // half-composed word stays with the field it was typed in.
                if (self.ime_field != null) w.setTextInputRect(.{}, false);
                self.workspace.clearPreedits();
                self.ime_field = id;
            }
            if (field) |ed| w.setTextInputRect(self.workspace.caretRect(ed), true) else w.setTextInputRect(.{}, false);
        }
    }

    pub fn handleEvent(self: *App, ev: platform.Event) void {
        switch (ev) {
            .resize => |r| {
                if (r.scale != self.scale) {
                    self.scale = r.scale;
                    self.text.setScale(r.scale);
                    if (self.gfx) self.atlas.reset(false);
                }
            },
            .wake => self.executor.drain(),
            .close_requested => {
                // Decided after the event batch: closing the window frees its events.
                self.window_close_requested = true;
                return;
            },
            else => {},
        }
        self.workspace.handleEvent(ev);
    }

    fn computeTimeout(self: *App) i32 {
        var timeout: i32 = -1;
        if (self.window) |w| {
            if (self.workspace.dirty and w.canRender()) {
                timeout = 0;
            } else if (self.workspace.nextRedrawIn()) |ms| timeout = @intCast(@max(1, ms));
        }
        if (self.executor.active.load(.monotonic) > 0 and timeout < 0) timeout = 250;
        if (self.autosave) |*a| if (a.nextDeadlineMs()) |ms| {
            if (timeout < 0 or timeout > ms) timeout = ms;
        };
        // Wake up to switch day/night schemes even when idle.
        if (self.themes.isAuto() and (timeout < 0 or timeout > 60_000)) timeout = 60_000;
        return timeout;
    }

    pub fn run(self: *App) !void {
        while (!self.quit) {
            const timeout = self.computeTimeout();
            self.poll_fds.clearRetainingCapacity();
            try self.poll_fds.append(self.gpa, .{ .fd = self.wake_fd });
            if (self.server) |*srv| srv.pollFds(&self.poll_fds, self.gpa);
            const n_fixed = self.poll_fds.items.len;
            self.workspace.extraPollFds(&self.poll_fds);

            if (self.window) |w| {
                w.pollEvents(timeout, self.poll_fds.items) catch |err| {
                    log.err("window connection lost: {t}", .{err});
                    self.closeWindow();
                    if (!self.keep_running) self.quit = true;
                    continue;
                };
                for (w.events()) |ev| self.handleEvent(ev);
                self.openDesktopPaths(w);
            } else {
                _ = c.poll(@ptrCast(self.poll_fds.items.ptr), @intCast(self.poll_fds.items.len), timeout);
            }

            const fds = self.poll_fds.items;
            if (fds[0].readable()) {
                // A pipe may hold several wake-ups.
                var drain: [64]u8 = undefined;
                while (c.read(self.wake_fd, &drain, drain.len) == drain.len) {}
                self.executor.drain();
                self.workspace.dirty = true;
            }
            if (self.server) |*srv| srv.service(fds[1..n_fixed], .{ .ctx = self, .onRequest = onRequest });
            self.workspace.serviceExtraFds(fds[n_fixed..]);
            self.executor.drain();
            self.workspace.tick();
            if (self.workspace.session_rename_to != null) self.renameSession();
            if (self.autosave) |*a| a.tick(&self.workspace);
            if (persist.terminate.load(.acquire)) self.quit = true;
            self.checkTheme();

            if (self.window_close_requested) {
                self.window_close_requested = false;
                if (self.keep_running) self.closeWindow() else self.workspace.handleEvent(.close_requested);
            }
            if (self.workspace.should_quit) {
                self.workspace.should_quit = false;
                if (self.keep_running and !self.workspace.quit_session) {
                    self.closeWindow();
                } else {
                    self.quit = true;
                    self.quit_by_user = true;
                }
            }
            if (self.window == null and !self.keep_running) self.quit = true;

            if (self.window) |w| {
                const visible = blk: {
                    const ed = self.workspace.focusedEditor() orelse break :blk true;
                    break :blk editor_view.caretVisible(ed);
                };
                if (visible != self.last_caret_visible) {
                    self.last_caret_visible = visible;
                    self.workspace.dirty = true;
                }
                if (self.workspace.dirty and w.canRender()) {
                    self.render();
                    w.present();
                }
            }
        }
        self.saveSession(!self.quit_by_user or persist.terminate.load(.acquire));
    }

    /// Renders one frame offscreen and writes it as a PNG.
    pub fn screenshot(self: *App, path: []const u8) !void {
        // Two passes so atlas growth / late layout settle.
        self.render();
        if (self.workspace.dirty) self.render();
        self.render();
        const h = &self.headless.?;
        const pixels = try h.readPixels(self.gpa);
        defer self.gpa.free(pixels);
        const data = try png.encodeRgba(self.gpa, pixels, h.width, h.height, true);
        defer self.gpa.free(data);
        try Io.Dir.cwd().writeFile(self.io, .{ .sub_path = path, .data = data });
    }
};
