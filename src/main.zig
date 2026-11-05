const std = @import("std");
const c = @import("c");
const App = @import("app.zig").App;
const platform = @import("framework/platform/platform.zig");
const window_mod = @import("framework/platform/window.zig");
const keymap = @import("app/settings/keymap.zig");
const session = @import("app/session/session.zig");
const session_cli = @import("app/session/cli.zig");
const session_store = @import("app/session/store.zig");
const persist = @import("app/session/persist.zig");
const askpass = @import("model/remote/askpass.zig");
const login_path = @import("framework/platform/login_path.zig");

const usage =
    \\Usage: cs-zig [options] [path[:line[:col]]...]
    \\
    \\Opens files, or a folder as a project (like `subl .`). If a cs-zig
    \\session is already running, the paths open there and this command returns
    \\immediately; otherwise this process becomes the session.
    \\
    \\Options:
    \\  -w, --wait          Wait until the files are closed (for $EDITOR / git)
    \\  --daemon            Start a background session (no window) and exit;
    \\                      later `cs-zig` calls connect to it instantly and
    \\                      language servers stay warm
    \\  --kill-server       Stop the running session
    \\  -s, --session NAME  Use a named session, restoring its files, layout and
    \\                      unsaved text (default: "default"); created if new
    \\  -n, --new-window    Open a new unnamed session (window-1, window-2, ...)
    \\  --list-sessions     Same as `sessions list`
    \\  sessions COMMAND    list, info, attach, save, kill, rename, delete NAME
    \\                      (`cs-zig sessions help`)
    \\  --standalone        Don't connect to or start a session
    \\  --x11 / --wayland   Force a window system (Linux)
    \\  --vim               Start with vim mode enabled
    \\  --no-lsp            Don't start language servers
    \\  --remote HOST       Open the paths on HOST over ssh, like ssh://HOST/path
    \\                      (relative paths start in the remote home)
    \\  --askpass PROMPT    For ssh started by the editor: ask PROMPT in the
    \\                      editor and print the answer (SSH_ASKPASS)
    \\  -h, --help          Show this help
    \\
    \\Headless (offscreen) rendering, for screenshots and UI tests:
    \\  --headless WxH      Render offscreen at this device-pixel size
    \\  --scale S           Device scale factor (default 1)
    \\  --key CHORD         Press a key chord, e.g. ctrl+shift+p (repeatable)
    \\  --type TEXT         Type text
    \\  --click X,Y[,N[,B]] Click at logical X,Y (N = click count; B = left,
    \\                      right or middle, optionally +shift, +ctrl, +alt, +super)
    \\  --action NAME       Run an action, e.g. toggle_sidebar
    \\  --pump MS           Keep processing background work (LSP) for MS ms
    \\  --screenshot PATH   Write the final frame as PNG and exit
    \\                      (a step that quits the editor ends the steps)
    \\
;

const Step = union(enum) {
    pump: []const u8,
    key: []const u8,
    type: []const u8,
    click: []const u8,
    action: []const u8,
};

pub fn main(init: std.process.Init) !void {
    const gpa = init.gpa;
    const io = init.io;
    const args = try init.minimal.args.toSlice(init.arena.allocator());

    // ssh runs us as its SSH_ASKPASS (`$SSH_ASKPASS PROMPT`, with the socket
    // of the connection that started it in CS_ZIG_ASKPASS); `--askpass
    // PROMPT` does the same by hand.
    const askpass_socket = init.environ_map.get(askpass.socket_env);
    if ((args.len == 3 and std.mem.eql(u8, args[1], "--askpass")) or (askpass_socket != null and args.len == 2)) {
        const sock = askpass_socket orelse {
            std.debug.print("cs-zig --askpass: {s} is not set (only ssh started by the editor asks this way)\n", .{askpass.socket_env});
            std.process.exit(1);
        };
        const hint = init.environ_map.get("SSH_ASKPASS_PROMPT") orelse "";
        std.process.exit(askpass.runHelper(sock, hint, args[args.len - 1], 1));
    }

    // From Finder or the Dock on macOS: restart with the login shell's PATH.
    login_path.adopt(gpa, io, init.environ_map, args);

    var headless: ?[2]u32 = null;
    var scale: f32 = 1;
    var screenshot: ?[]const u8 = null;
    var vim = false;
    var enable_lsp = true;
    var wait = false;
    var daemon = false;
    var daemon_child = false;
    var kill_server = false;
    var standalone = false;
    var remote_host: ?[]const u8 = null;
    var session_name: []const u8 = "default";
    var session_given = false;
    var new_window = false;
    var backend: ?window_mod.Backend = null;
    var paths: std.ArrayList([]const u8) = .empty;
    defer paths.deinit(gpa);
    var steps: std.ArrayList(Step) = .empty;
    defer steps.deinit(gpa);

    if (args.len > 1 and std.mem.eql(u8, args[1], "sessions")) {
        // `sessions attach NAME` is `--session NAME`; the rest is session_cli's.
        if (args.len > 3 and std.mem.eql(u8, args[2], "attach")) {
            session_name = args[3];
            session_given = true;
        } else {
            const rest: []const []const u8 = @ptrCast(args[2..]);
            std.process.exit(session_cli.run(gpa, io, rest));
        }
    }
    var i: usize = if (session_given) 4 else 1;
    while (i < args.len) : (i += 1) {
        const arg = args[i];
        const next = struct {
            fn f(a: []const [:0]const u8, idx: *usize) ![]const u8 {
                idx.* += 1;
                if (idx.* >= a.len) return error.MissingArgument;
                return a[idx.*];
            }
        }.f;
        const eql = std.mem.eql;
        if (eql(u8, arg, "-h") or eql(u8, arg, "--help")) {
            std.debug.print("{s}", .{usage});
            return;
        } else if (eql(u8, arg, "--headless")) {
            const spec = try next(args, &i);
            const x = std.mem.indexOfScalar(u8, spec, 'x') orelse return error.BadSize;
            headless = .{ try std.fmt.parseInt(u32, spec[0..x], 10), try std.fmt.parseInt(u32, spec[x + 1 ..], 10) };
        } else if (eql(u8, arg, "--scale")) {
            scale = try std.fmt.parseFloat(f32, try next(args, &i));
        } else if (eql(u8, arg, "--screenshot")) {
            screenshot = try next(args, &i);
        } else if (eql(u8, arg, "--key")) {
            try steps.append(gpa, .{ .key = try next(args, &i) });
        } else if (eql(u8, arg, "--type")) {
            try steps.append(gpa, .{ .type = try next(args, &i) });
        } else if (eql(u8, arg, "--click")) {
            try steps.append(gpa, .{ .click = try next(args, &i) });
        } else if (eql(u8, arg, "--action")) {
            try steps.append(gpa, .{ .action = try next(args, &i) });
        } else if (eql(u8, arg, "--pump")) {
            try steps.append(gpa, .{ .pump = try next(args, &i) });
        } else if (eql(u8, arg, "--vim")) {
            vim = true;
        } else if (eql(u8, arg, "--no-lsp")) {
            enable_lsp = false;
        } else if (eql(u8, arg, "-w") or eql(u8, arg, "--wait")) {
            wait = true;
        } else if (eql(u8, arg, "--daemon")) {
            daemon = true;
        } else if (eql(u8, arg, "--daemon-child")) {
            daemon_child = true;
        } else if (eql(u8, arg, "--kill-server")) {
            kill_server = true;
        } else if (eql(u8, arg, "--standalone")) {
            standalone = true;
        } else if (eql(u8, arg, "--remote")) {
            remote_host = try next(args, &i);
        } else if (eql(u8, arg, "--session") or eql(u8, arg, "-s")) {
            session_name = try next(args, &i);
            session_given = true;
        } else if (eql(u8, arg, "-n") or eql(u8, arg, "--new-window")) {
            new_window = true;
        } else if (eql(u8, arg, "--list-sessions")) {
            std.process.exit(session_cli.run(gpa, io, &.{"list"}));
        } else if (eql(u8, arg, "--x11") or eql(u8, arg, "--wayland")) {
            // Only Linux has a choice of window system.
            backend = std.meta.stringToEnum(window_mod.Backend, arg[2..]);
        } else if (arg.len > 0 and arg[0] == '-') {
            std.debug.print("unknown option: {s}\n\n{s}", .{ arg, usage });
            return error.InvalidArgument;
        } else {
            try paths.append(gpa, arg);
        }
    }
    // `--remote HOST path` means ssh://HOST/path; relative paths (and no
    // paths at all) start in the remote home.
    if (remote_host) |host| {
        if (paths.items.len == 0) try paths.append(gpa, "~");
        for (paths.items) |*p| {
            if (std.mem.startsWith(u8, p.*, "ssh://")) continue;
            const rel = if (std.mem.eql(u8, p.*, ".")) "~" else p.*;
            const sep: []const u8 = if (rel[0] == '/') "" else if (rel[0] == '~') "/" else "/~/";
            p.* = try std.fmt.allocPrint(init.arena.allocator(), "ssh://{s}{s}{s}", .{ host, sep, rel });
        }
    }

    // Writing to a reader that went away must not kill the editor.
    const ignore: std.posix.Sigaction = .{ .handler = .{ .handler = std.posix.SIG.IGN }, .mask = std.posix.sigemptyset(), .flags = 0 };
    std.posix.sigaction(std.posix.SIG.PIPE, &ignore, null);

    if (!session_store.validName(session_name)) {
        std.debug.print("cs-zig: \"{s}\" is not a session name (letters, digits, . _ -)\n", .{session_name});
        return error.InvalidArgument;
    }
    if (headless) |size| return runHeadless(gpa, io, size, scale, enable_lsp, vim, paths.items, steps.items, screenshot, if (session_given and !standalone) session_name else null);

    var cwd_buf: [std.fs.max_path_bytes]u8 = @splat(0); // See Workspace.realPath.
    const cwd = cwd_buf[0 .. std.process.currentPath(io, &cwd_buf) catch 0];
    var name_buf: [32]u8 = undefined;
    if (new_window) session_name = freshWindowName(gpa, io, &name_buf);
    var sock_buf: [std.fs.max_path_bytes]u8 = undefined;
    const sock = session.socketPath(&sock_buf, session_name);
    const persisted: ?[]const u8 = if (standalone) null else session_name;

    if (kill_server) {
        const path = sock orelse return error.NoSocketPath;
        const fd = session.connect(path) orelse {
            std.debug.print("cs-zig: no session \"{s}\" is running\n", .{session_name});
            return;
        };
        defer _ = c.close(fd);
        _ = session.sendRequest(gpa, fd, .{ .cmd = .quit });
        return;
    }

    if (daemon) return startDaemon(gpa, io, session_name, sock orelse return error.NoSocketPath, enable_lsp);

    if (daemon_child) {
        // Our own session, detached from the launching terminal.
        _ = c.setsid();
        persist.installSignalHandlers();
        const app = try App.create(gpa, io, .{ .open_window = false, .socket_path = sock, .keep_running = true, .enable_lsp = enable_lsp, .backend = backend, .session = persisted, .session_unnamed = new_window });
        defer app.destroy();
        try app.run();
        return;
    }

    // Hand the paths to a running session if there is one.
    if (!standalone) if (sock) |path| if (session.connect(path)) |fd| {
        defer _ = c.close(fd);
        if (session.sendRequest(gpa, fd, .{ .cmd = .open, .cwd = cwd, .paths = paths.items, .wait = wait })) return;
        std.debug.print("cs-zig: the running session didn't answer; starting a new window\n", .{});
    };

    persist.installSignalHandlers();
    const app = try App.create(gpa, io, .{
        .socket_path = if (standalone) null else sock,
        .enable_lsp = enable_lsp,
        .backend = backend,
        .session = persisted,
        .session_unnamed = new_window,
    });
    defer app.destroy();
    const ws = &app.workspace;
    if (vim) ws.perform(.toggle_vim_mode);
    for (paths.items) |p| _ = ws.openArgument(cwd, p) catch |err| std.log.err("cannot open {s}: {t}", .{ p, err });
    try ws.openStartTabs(paths.items.len > 0);
    try app.run();
}

/// `window-N` for the first N with no saved and no running session of that name.
fn freshWindowName(gpa: std.mem.Allocator, io: std.Io, buf: []u8) []const u8 {
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();
    const running = session.running(gpa, io, arena.allocator());
    var store = session_store.Store.open(gpa, io);
    defer if (store) |*st| st.deinit();
    var n: u32 = 1;
    while (true) : (n += 1) {
        const name = std.fmt.bufPrint(buf, "window-{d}", .{n}) catch return "window-0";
        if (store) |*st| if (st.exists(name)) continue;
        const taken = for (running) |r| {
            if (std.mem.eql(u8, r.name, name)) break true;
        } else false;
        if (!taken) return name;
    }
}

/// Starts `cs-zig --daemon-child` detached and waits until its socket answers.
fn startDaemon(gpa: std.mem.Allocator, io: std.Io, name: []const u8, sock: [:0]const u8, enable_lsp: bool) !void {
    if (session.connect(sock)) |fd| {
        _ = c.close(fd);
        std.debug.print("cs-zig: session \"{s}\" is already running\n", .{name});
        return;
    }
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const n = std.process.executablePath(io, &exe_buf) catch return error.NoSelfExe;
    var argv: std.ArrayList([]const u8) = .empty;
    defer argv.deinit(gpa);
    try argv.appendSlice(gpa, &.{ exe_buf[0..n], "--daemon-child", "--session", name });
    if (!enable_lsp) try argv.append(gpa, "--no-lsp");
    _ = try std.process.spawn(io, .{ .argv = argv.items, .stdin = .ignore, .stdout = .ignore, .stderr = .ignore });
    const deadline = platform.nowMs() + 5000;
    while (platform.nowMs() < deadline) {
        if (session.connect(sock)) |fd| {
            _ = c.close(fd);
            std.debug.print("cs-zig: session \"{s}\" started ({s})\n", .{ name, sock });
            return;
        }
        var req: std.c.timespec = .{ .sec = 0, .nsec = 50 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
    }
    return error.DaemonDidNotStart;
}

fn runHeadless(gpa: std.mem.Allocator, io: std.Io, size: [2]u32, scale: f32, enable_lsp: bool, vim: bool, paths: []const []const u8, steps: []const Step, screenshot: ?[]const u8, session_name: ?[]const u8) !void {
    const app = try App.create(gpa, io, .{ .headless_size = size, .scale = scale, .enable_lsp = enable_lsp, .session = session_name });
    defer app.destroy();
    const ws = &app.workspace;
    if (vim) ws.perform(.toggle_vim_mode);

    var cwd_buf: [std.fs.max_path_bytes]u8 = @splat(0); // See Workspace.realPath.
    const cwd = cwd_buf[0 .. std.process.currentPath(io, &cwd_buf) catch 0];
    for (paths) |path| {
        const stat = std.Io.Dir.cwd().statFile(io, path, .{}) catch null;
        if (stat != null and stat.?.kind == .directory) {
            const abs = try ws.realPath(path);
            defer gpa.free(abs);
            try ws.openProject(abs, true);
        } else {
            _ = ws.openArgument(cwd, path) catch |err| std.log.err("cannot open {s}: {t}", .{ path, err });
        }
    }
    try ws.openStartTabs(paths.len > 0);

    // Lay out once so scripted input lands on real geometry.
    app.render();
    settleGrammars(app);
    for (steps) |step| {
        switch (step) {
            .key => |spec| app.handleEvent(.{ .key_down = try keyEvent(spec) }),
            .type => |text| {
                var it = (try std.unicode.Utf8View.init(text)).iterator();
                while (it.nextCodepointSlice()) |cp| {
                    const ev: platform.KeyEvent = .{ .key = .{ .char = std.unicode.utf8Decode(cp) catch ' ' }, .mods = .{}, .text = cp };
                    app.handleEvent(.{ .key_down = ev });
                }
            },
            .click => |spec| {
                var parts = std.mem.splitScalar(u8, spec, ',');
                const x = try std.fmt.parseFloat(f32, parts.next() orelse "0");
                const y = try std.fmt.parseFloat(f32, parts.next() orelse "0");
                const n = try std.fmt.parseInt(u8, parts.next() orelse "1", 10);
                var button: platform.MouseButton = .left;
                var mods: platform.Modifiers = .{};
                if (parts.next()) |b| {
                    var names = std.mem.splitScalar(u8, b, '+');
                    button = std.meta.stringToEnum(platform.MouseButton, names.next().?) orelse return error.BadButton;
                    while (names.next()) |mod| {
                        if (std.mem.eql(u8, mod, "shift")) mods.shift = true else if (std.mem.eql(u8, mod, "ctrl")) mods.ctrl = true else if (std.mem.eql(u8, mod, "alt")) mods.alt = true else if (std.mem.eql(u8, mod, "super")) mods.super = true else return error.BadModifier;
                    }
                }
                const pos: platform.Vec2 = .{ .x = x, .y = y };
                app.handleEvent(.{ .mouse_move = .{ .pos = pos, .mods = mods } });
                app.handleEvent(.{ .mouse_down = .{ .pos = pos, .button = button, .mods = mods, .click_count = n } });
                app.handleEvent(.{ .mouse_up = .{ .pos = pos, .button = button, .mods = mods, .click_count = n } });
            },
            .pump => |ms_text| {
                const ms = try std.fmt.parseInt(i64, ms_text, 10);
                const until = platform.nowMs() + ms;
                // Like the real event loop: background results, file watching, timers.
                var fds: std.ArrayList(platform.PollFd) = .empty;
                defer fds.deinit(ws.gpa);
                while (platform.nowMs() < until) {
                    app.executor.drain();
                    fds.clearRetainingCapacity();
                    ws.extraPollFds(&fds);
                    _ = std.c.poll(@ptrCast(fds.items.ptr), @intCast(fds.items.len), 10);
                    ws.serviceExtraFds(fds.items);
                    ws.tick();
                    if (app.autosave) |*a| a.tick(ws);
                }
            },
            .action => |name| {
                const action = std.meta.stringToEnum(keymap.Action, name) orelse return error.UnknownAction;
                ws.perform(action);
            },
        }
        app.executor.drain();
        app.render();
        // Quitting ends the run, as it ends the event loop.
        if (ws.should_quit) break;
    }
    settleGrammars(app);
    if (screenshot) |path| try app.screenshot(path);
    // Like the event loop's end: a quit in the steps was the user's, so unsaved text is dropped.
    app.saveSession(!ws.should_quit);
}

/// Grammars load in the background, and highlighting with one can ask for
/// more (a Vue file's TypeScript); big files parse in the background too:
/// renders until they have all arrived.
fn settleGrammars(app: *App) void {
    var waited: usize = 0;
    while ((app.registry.loading.count() > 0 or syntaxParsing(app)) and waited < 500) : (waited += 1) {
        var req: std.c.timespec = .{ .sec = 0, .nsec = 10 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
        app.executor.drain();
        app.render();
    }
}

fn syntaxParsing(app: *App) bool {
    for (app.workspace.buffers.items) |b| if (b.syntax) |*s| if (s.parsing()) return true;
    return false;
}

fn keyEvent(spec: []const u8) !platform.KeyEvent {
    const chord = try keymap.Chord.parse(spec);
    var ev: platform.KeyEvent = .{ .key = chord.key, .mods = chord.mods };
    // Plain printable keys also produce text, like a real keyboard.
    if (chord.key == .char and !chord.mods.ctrl and !chord.mods.alt) {
        const S = struct {
            var buf: [4]u8 = undefined;
        };
        var cp = chord.key.char;
        if (chord.mods.shift and cp < 128) cp = std.ascii.toUpper(@intCast(cp));
        const n = std.unicode.utf8Encode(cp, &S.buf) catch 0;
        ev.text = S.buf[0..n];
    }
    return ev;
}

test {
    _ = @import("model/text/selection.zig");
    _ = @import("model/lsp/signature.zig");
    _ = @import("app/session/store.zig");
    _ = @import("app/session/snapshot.zig");
    _ = @import("app/session/persist.zig");
    _ = @import("framework/util/regex.zig");
    _ = @import("framework/util/png.zig");
    _ = @import("framework/platform/login_path.zig");
    _ = @import("model/org/table.zig");
    _ = @import("framework/util/executor.zig");
    _ = @import("model/project/fuzzy.zig");
    _ = @import("model/project/templates.zig");
    _ = @import("app/settings/recent.zig");
    _ = @import("app/workspace/page_ui.zig");
    _ = @import("model/markdown/tests.zig");
    _ = @import("framework/platform/image_decode.zig");
    _ = @import("app/settings/keymap.zig");
    _ = @import("app/settings/catalog.zig");
    _ = @import("app/settings/bindings_file.zig");
    _ = @import("app/workspace/settings_ui.zig");
    _ = @import("app/settings/leader.zig");
    _ = @import("app/workspace/groups.zig");
    _ = @import("app/workspace/context_menu.zig");
    _ = @import("app/workspace/find_panel.zig");
    _ = @import("app/workspace/path_complete.zig");
    _ = @import("app/workspace/workspace.zig");
    _ = @import("vim_tests.zig");
    _ = @import("model/text/fold_map.zig");
    _ = @import("model/text/display_map.zig");
    _ = @import("framework/text/line_index.zig");
    _ = @import("framework/text/text_system.zig");
    _ = @import("model/text/buffer.zig");
    _ = @import("model/text/fold_tree.zig");
    _ = @import("framework/platform/file_watcher.zig");
    _ = @import("model/project/search.zig");
    _ = @import("model/project/search_tree.zig");
    _ = @import("model/org/date.zig");
    _ = @import("model/org/parser.zig");
    _ = @import("model/org/highlight.zig");
    _ = @import("model/org/agenda.zig");
    _ = @import("model/org/capture.zig");
    _ = @import("model/org/clock.zig");
    _ = @import("model/org/refile.zig");
    _ = @import("model/org/export.zig");
    _ = @import("app/org/org.zig");
    _ = @import("model/project/symbols.zig");
    _ = @import("framework/ui/theme.zig");
    _ = @import("framework/ui/scheme_loader.zig");
    _ = @import("app/settings/sun.zig");
    _ = @import("app/settings/settings_file.zig");
    _ = @import("app/settings/ui_state.zig");
    _ = @import("model/lsp/protocol.zig");
    _ = @import("model/lsp/lsp.zig");
    _ = @import("model/dap/dap.zig");
    _ = @import("model/syntax/languages.zig");
    _ = @import("model/syntax/injection.zig");
    _ = @import("model/git/git.zig");
    _ = @import("model/git/blame.zig");
    _ = @import("model/git/status.zig");
    _ = @import("model/git/diff.zig");
    _ = @import("framework/platform/pty.zig");
    _ = @import("model/terminal/terminal.zig");
    _ = @import("app/terminal/palette.zig");
    _ = @import("app/app_icon.zig");
}
