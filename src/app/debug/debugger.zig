//! The debugger as the editor uses it: the breakpoints, the launch
//! configurations and the one debug session, with the commands F5 and the
//! toolbar run. The workspace shows what it holds (debug_ui.zig) and opens
//! the files it reveals.

const std = @import("std");
const Allocator = std.mem.Allocator;
const dap = @import("../../model/dap/dap.zig");
const platform = @import("../../framework/platform/platform.zig");
const Executor = @import("../../framework/util/executor.zig").Executor;
const editor_mod = @import("../editor/editor.zig");

const Session = dap.session.Session;

/// How long a stop may take before the adapter is killed.
const stop_grace_ms = 3000;

pub const Reveal = struct {
    path: []u8,
    line: u32,
};

pub const Debugger = struct {
    gpa: Allocator,
    io: std.Io,
    executor: *Executor,
    store: dap.breakpoints.Store,
    session: Session,
    transport: ?*dap.process.ProcessTransport = null,
    configs: ?dap.launch.Set = null,
    selected: usize = 0,
    /// Where the last run started, for Restart.
    root: ?[]u8 = null,
    /// Real paths of files named another way, by the name used.
    real_paths: std.StringHashMapUnmanaged([]u8) = .empty,
    reveal_pending: ?Reveal = null,
    /// Something to show changed (cleared by `takeChanged`).
    changed: bool = false,
    /// The panel should be shown (a run began).
    show_panel: bool = false,
    stop_deadline_ms: i64 = 0,
    restart_pending: bool = false,
    /// A refusal to start, for the status bar.
    message: ?[]u8 = null,
    /// The file the active editor shows, for ${file}.
    active_file: ?[]u8 = null,

    pub fn create(gpa: Allocator, io: std.Io, executor: *Executor) !*Debugger {
        const self = try gpa.create(Debugger);
        self.* = .{
            .gpa = gpa,
            .io = io,
            .executor = executor,
            .store = .init(gpa),
            .session = undefined,
        };
        self.session = .init(gpa, &self.store, .{ .ctx = self, .notify = onEvent });
        return self;
    }

    pub fn destroy(self: *Debugger) void {
        if (self.transport) |t| {
            self.killDebuggee();
            self.transport = null;
            t.shutdown();
        }
        self.session.deinit();
        self.store.deinit();
        if (self.configs) |*c| c.deinit();
        if (self.root) |r| self.gpa.free(r);
        var it = self.real_paths.iterator();
        while (it.next()) |e| {
            self.gpa.free(e.key_ptr.*);
            self.gpa.free(e.value_ptr.*);
        }
        self.real_paths.deinit(self.gpa);
        if (self.reveal_pending) |r| self.gpa.free(r.path);
        if (self.message) |m| self.gpa.free(m);
        if (self.active_file) |f| self.gpa.free(f);
        self.gpa.destroy(self);
    }

    // ---- Events from the session ------------------------------------------------

    fn onEvent(ctx: *anyopaque, event: dap.session.Event) void {
        const self: *Debugger = @ptrCast(@alignCast(ctx));
        self.changed = true;
        switch (event) {
            .changed => {},
            .reveal => |r| {
                const path = r.path orelse return;
                if (self.reveal_pending) |old| self.gpa.free(old.path);
                self.reveal_pending = .{ .path = self.gpa.dupe(u8, self.normalize(path)) catch return, .line = r.line };
            },
            .ended => {
                self.stop_deadline_ms = 0;
                if (self.transport) |t| {
                    t.close();
                    self.transport = null;
                }
                if (self.restart_pending) {
                    self.restart_pending = false;
                    self.startFromRoot() catch |err| self.setMessage("Could not restart: {t}", .{err});
                }
            },
        }
    }

    fn setMessage(self: *Debugger, comptime fmt: []const u8, args: anytype) void {
        if (self.message) |m| self.gpa.free(m);
        self.message = std.fmt.allocPrint(self.gpa, fmt, args) catch null;
        if (self.message) |m| self.session.addOutput(.important, m);
        self.changed = true;
    }

    // ---- Paths ------------------------------------------------------------------

    /// `path` as the file system names it (symlinks resolved), cached.
    pub fn normalize(self: *Debugger, path: []const u8) []const u8 {
        if (self.real_paths.get(path)) |r| return r;
        var buf: [std.fs.max_path_bytes]u8 = @splat(0);
        const real = if (std.Io.Dir.cwd().realPathFile(self.io, path, &buf)) |n| buf[0..n] else |_| path;
        const key = self.gpa.dupe(u8, path) catch return path;
        const value = self.gpa.dupe(u8, real) catch {
            self.gpa.free(key);
            return path;
        };
        self.real_paths.put(self.gpa, key, value) catch {
            self.gpa.free(key);
            self.gpa.free(value);
            return path;
        };
        return value;
    }

    // ---- Breakpoints ------------------------------------------------------------

    /// F9 / a click in the gutter: `row` is the 0-based editor row.
    pub fn toggleBreakpoint(self: *Debugger, path: []const u8, row: u32) void {
        const real = self.normalize(path);
        _ = self.store.toggle(real, row + 1) catch return;
        self.session.sendBreakpoints(real);
        self.changed = true;
    }

    pub fn setBreakpointEnabled(self: *Debugger, path: []const u8, line: u32, enabled: bool) void {
        self.store.setEnabled(path, line, enabled);
        self.session.sendBreakpoints(path);
        self.changed = true;
    }

    pub fn removeBreakpoint(self: *Debugger, path: []const u8, line: u32) void {
        self.store.remove(path, line);
        self.session.sendBreakpoints(path);
        self.changed = true;
    }

    pub fn removeAllBreakpoints(self: *Debugger) void {
        var paths: std.ArrayList([]const u8) = .empty;
        defer paths.deinit(self.gpa);
        for (self.store.files.keys()) |p| paths.append(self.gpa, p) catch {};
        self.store.clear();
        for (paths.items) |p| self.session.sendBreakpoints(p);
        self.changed = true;
    }

    /// What the gutter and the line background show for a row (0-based).
    pub fn markFor(self: *Debugger, path: []const u8, row: u32) editor_mod.DebugMark {
        const s = &self.session;
        const line = row + 1;
        var mark: editor_mod.DebugMark = .none;
        if (self.store.get(path, line)) |bp| {
            mark = if (!bp.enabled) .breakpoint_disabled else if (s.isLive() and s.state != .starting and !bp.verified) .breakpoint_unverified else .breakpoint;
        }
        if (s.state == .stopped and s.frame_index < s.frames.len) {
            const f = s.frames[s.frame_index];
            if (f.line == line) if (f.path) |fp| if (std.mem.eql(u8, self.normalize(fp), path)) {
                const top = s.frame_index == 0;
                mark = switch (mark) {
                    .breakpoint, .breakpoint_unverified => if (top) .current_breakpoint else .frame_breakpoint,
                    else => if (top) .current else .frame,
                };
            };
        }
        return mark;
    }

    fn hookMark(ctx: *anyopaque, path: []const u8, row: u32) editor_mod.DebugMark {
        const self: *Debugger = @ptrCast(@alignCast(ctx));
        if (self.store.files.count() == 0 and self.session.state != .stopped) return .none;
        return self.markFor(path, row);
    }

    fn hookToggle(ctx: *anyopaque, path: []const u8, row: u32) void {
        const self: *Debugger = @ptrCast(@alignCast(ctx));
        self.toggleBreakpoint(path, row);
    }

    pub fn hook(self: *Debugger) editor_mod.DebugHook {
        return .{ .ctx = self, .mark = hookMark, .toggle = hookToggle };
    }

    // ---- Running ----------------------------------------------------------------

    pub fn isLive(self: *const Debugger) bool {
        return self.session.isLive();
    }

    pub fn setActiveFile(self: *Debugger, path: ?[]const u8) void {
        if (self.active_file) |f| self.gpa.free(f);
        self.active_file = if (path) |p| self.gpa.dupe(u8, p) catch null else null;
    }

    /// Reads the folder's launch.json (or guesses a configuration).
    pub fn loadConfigs(self: *Debugger, root: []const u8) !void {
        if (self.configs) |*c| c.deinit();
        self.configs = null;
        const set = try dap.launch.load(self.gpa, self.io, root, .{ .workspace_folder = root, .file = self.active_file });
        self.configs = set;
        if (self.selected >= set.configs.len) self.selected = 0;
        self.changed = true;
    }

    pub fn selectedConfig(self: *const Debugger) ?dap.launch.Config {
        const set = self.configs orelse return null;
        if (set.configs.len == 0) return null;
        return set.configs[@min(self.selected, set.configs.len - 1)];
    }

    /// F5: continues a stopped program, else starts the selected configuration.
    pub fn startOrContinue(self: *Debugger, root: []const u8) void {
        switch (self.session.state) {
            .stopped => return self.session.continue_(),
            .starting, .running => return,
            .idle, .ended => {},
        }
        if (self.root) |r| self.gpa.free(r);
        self.root = self.gpa.dupe(u8, root) catch return;
        self.startFromRoot() catch |err| switch (err) {
            error.NoConfiguration => self.setMessage("No launch configuration: add .vscode/launch.json or build the program first.", .{}),
            error.BadLaunchJson => self.setMessage("launch.json could not be read.", .{}),
            error.AdapterNotFound => self.setMessage("No debug adapter found: install lldb-dap (Xcode or the Command Line Tools).", .{}),
            else => self.setMessage("Could not start debugging: {t}", .{err}),
        };
    }

    fn startFromRoot(self: *Debugger) !void {
        const root = self.root orelse return error.NoConfiguration;
        try self.loadConfigs(root);
        const config = self.selectedConfig() orelse return error.NoConfiguration;
        const argv = try dap.launch.adapterArgv(self.gpa, self.io, config);
        defer dap.launch.freeArgv(self.gpa, argv);
        if (self.message) |m| self.gpa.free(m);
        self.message = null;
        const t = try dap.process.ProcessTransport.spawn(self.gpa, self.io, self.executor, &self.session, argv, root);
        self.transport = t;
        self.session.begin(t.transport(), config.request, config.arguments_json) catch |err| {
            t.close();
            self.transport = null;
            return err;
        };
        self.show_panel = true;
        self.changed = true;
    }

    pub fn stop(self: *Debugger) void {
        if (!self.session.isLive()) return;
        self.session.stop();
        self.stop_deadline_ms = platform.nowMs() + stop_grace_ms;
    }

    pub fn restart(self: *Debugger, root: []const u8) void {
        if (!self.session.isLive()) return self.startOrContinue(root);
        self.restart_pending = true;
        self.stop();
    }

    fn killDebuggee(self: *Debugger) void {
        if (self.session.debuggee_pid) |p| if (p > 1) {
            _ = std.c.kill(@intCast(p), .KILL);
        };
    }

    /// Called every frame: kills an adapter that did not end when asked.
    pub fn service(self: *Debugger) void {
        if (self.stop_deadline_ms != 0 and platform.nowMs() >= self.stop_deadline_ms and self.session.isLive()) {
            self.stop_deadline_ms = 0;
            self.killDebuggee();
            if (self.transport) |t| {
                t.close();
                self.transport = null;
            }
            self.session.adapterExited();
        }
    }

    pub fn takeReveal(self: *Debugger) ?Reveal {
        const r = self.reveal_pending orelse return null;
        self.reveal_pending = null;
        return r;
    }

    pub fn takeChanged(self: *Debugger) bool {
        const c = self.changed;
        self.changed = false;
        return c;
    }
};
