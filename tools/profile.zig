//! `zig build profile -- --bin PATH [--runs N] [--out FILE] [--compare FILE]`:
//! memory and speed of cs-zig in scripted headless scenarios, saved as
//! JSON so a later build can be checked against a recorded baseline.
//!
//! Each run starts `PATH --headless 1280x800 --no-lsp` with a scenario's
//! files and keys (the headless driver renders a frame after every step),
//! ends with a settle pump, and records
//!   peak_footprint_kb  macOS: the highest physical footprint sampled or
//!                  reported by the kernel (0 elsewhere)
//!   peak_rss_kb    the larger of wait4's ru_maxrss (the kernel's high-water
//!                  mark, which also sees spikes between samples) and the
//!                  highest sampled Rss
//!   steady_rss_kb  median Rss during the settle pump
//!   wall_ms        spawn to exit, minus the settle pump: startup, loading,
//!                  the scripted frames and teardown
//!   cpu_ms         user + system time of all the editor's threads (and of
//!                  the processes it started and reaped, like a terminal's shell)
//! Rss is sampled every 10 ms from /proc/PID/smaps_rollup, which counts
//! pages exactly; VmRSS/VmHWM in /proc/PID/status and ru_maxrss come from
//! per-CPU counters that can be a few MB off on a many-core machine. On
//! macOS it is the resident size from proc_pid_rusage (ru_maxrss there is in
//! bytes, not KiB), and `peak_footprint_kb` is Activity Monitor's Memory
//! (the kernel's physical footprint, compressed pages included) at its
//! highest, which is what a user sees grow when something leaks or caches.
//! The table shows the median of the runs (after unmeasured warm-up runs);
//! --compare prints the change from an earlier results file and flags a
//! median that is more than --threshold % worse and also worse than every
//! baseline run. Memory repeats within a percent or two; times vary by tens
//! of percent between runs on a busy machine (the second rule keeps that
//! noise from being flagged), so compare times from runs made back to back
//! and with more --runs.
//!
//! The inputs are pinned so another build gets the same work: the project is
//! a `git archive` of --project-rev (default: the baseline's commit), and the
//! big files are generated from a fixed seed (tools/fixtures.zig, shared with
//! the performance tests). They go to --fixtures when missing. Every run
//! gets a fresh, empty XDG_CONFIG_HOME and XDG_STATE_HOME (no user settings,
//! schemes, key bindings or saved session) and SHELL=/bin/sh for terminals.

const std = @import("std");
const builtin = @import("builtin");
const fixtures = @import("fixtures");
const proc_stats = @import("proc_stats");
const linux = std.os.linux;
const macos = builtin.os.tag.isDarwin();
const Io = std.Io;
const Allocator = std.mem.Allocator;

const usage =
    \\Usage: zig build profile -- [options]
    \\
    \\  --bin PATH          cs-zig binary to measure (default zig-out/bin/cs;
    \\                      build it with -Doptimize=ReleaseFast)
    \\  --runs N            Measured runs per scenario (default 5)
    \\  --warmup N          Unmeasured runs before them (default 1)
    \\  --only A,B          Run only these scenarios
    \\  --out FILE          Write the results as JSON
    \\  --compare FILE      Print the change from an earlier results file
    \\  --threshold PCT     Flag changes worse than this (default 5)
    \\  --fixtures DIR      Generated inputs (default /tmp/cs-zig-profile)
    \\  --project-rev REV   Commit of this repo used as the test project
    \\                      (default 9544a2d, the first baseline's)
    \\  --size WxH          Headless frame size (default 1280x800)
    \\  --settle MS         Final pump; steady RSS is sampled during it (default 1000)
    \\  --soak-cycles N     Cycles of the soak scenario (default 120)
    \\  --shots DIR         Save a screenshot of each scenario (from a warm-up run)
    \\  --trace DIR         Save each run's RSS samples as CSV
    \\  --print             Print each scenario's command (with its environment) and exit,
    \\                      e.g. to run one under valgrind or perf
    \\
    \\Exits with 1 when --compare flags a change.
    \\
;

const Options = struct {
    bin: []const u8 = "zig-out/bin/cs",
    runs: u32 = 5,
    warmup: u32 = 1,
    only: ?[]const u8 = null,
    out: ?[]const u8 = null,
    compare: ?[]const u8 = null,
    threshold: f64 = 5,
    fixtures: []const u8 = "/tmp/cs-zig-profile",
    project_rev: []const u8 = "9544a2d2fbfe45519573f1e172c7febe08730a9b",
    size: []const u8 = "1280x800",
    settle_ms: u32 = 1000,
    soak_cycles: u32 = 120,
    shots: ?[]const u8 = null,
    trace: ?[]const u8 = null,
    print: bool = false,
};

// ---- Scenarios ---------------------------------------------------------------

const Scenario = enum {
    empty,
    project,
    tabs,
    biglog,
    minified,
    @"vim-splits",
    leader,
    search,
    terminal,
    memory,
    soak,

    fn description(s: Scenario) []const u8 {
        return switch (s) {
            .empty => "no files: startup, 300 frames (escape), shutdown",
            .project => "project folder + src/app/workspace/workspace.zig (scan, Zig highlighting), 60 x pagedown",
            .tabs => "50 of the project's .zig files at once, ctrl+pagedown through all of them",
            .biglog => "100 MiB plain-text log, ctrl+end, 30 x pageup",
            .minified => "20 MiB single-line JSON, ctrl+end, 30 x left",
            .@"vim-splits" => "--vim, project + workspace.zig, ctrl-w v/s/l/s to 4 windows, 40 x ctrl-w w, ctrl+` panel terminal (/bin/sh)",
            .leader => "--vim, project + workspace.zig, 30 x space f, which-key (450 ms pump), escape",
            .search => "project + workspace.zig, Find in Files over the project, 30 x next result, Go to File",
            .terminal => "panel terminal (/bin/sh): 40 x echo through the shell, 20 x clear",
            .memory => "project + workspace.zig, the Memory view with 8 s of history",
            .soak => "thousands of edits, scrolls, tab/pane/terminal/search/settings open-and-close cycles; RSS must plateau",
        };
    }

    /// Everything after `--headless WxH --no-lsp`, before the settle pump.
    fn args(s: Scenario, fx: *const Fixtures, a: *Args) !void {
        switch (s) {
            .empty => try a.frames(300, "escape"),
            .project => {
                try a.add(&.{ fx.project, fx.workspace_zig });
                try a.frames(60, "pagedown");
            },
            .tabs => {
                try a.add(fx.tabs);
                try a.frames(fx.tabs.len, "ctrl+pagedown");
            },
            .biglog => {
                try a.add(&.{ fx.biglog, "--key", "ctrl+end" });
                try a.frames(30, "pageup");
            },
            .minified => {
                try a.add(&.{ fx.minified, "--key", "ctrl+end" });
                try a.frames(30, "left");
            },
            .@"vim-splits" => {
                try a.add(&.{ "--vim", fx.project, fx.workspace_zig });
                // v: two columns; s: the left one in two rows; l s: the right one too.
                for ([_][]const u8{ "v", "s", "l", "s" }) |k| try a.add(&.{ "--key", "ctrl+w", "--key", k });
                for (0..40) |_| try a.add(&.{ "--key", "ctrl+w", "--key", "w" });
                try a.add(&.{ "--action", "toggle_panel_terminal" });
            },
            .leader => {
                try a.add(&.{ "--vim", fx.project, fx.workspace_zig });
                // Past which_key_delay_ms (400), so the popup shows each time.
                for (0..30) |_| try a.add(&.{ "--key", "space", "--key", "f", "--pump", "450", "--key", "escape" });
            },
            .search => {
                try a.add(&.{ fx.project, fx.workspace_zig, "--action", "show_find_in_files", "--type", "Allocator", "--key", "enter", "--pump", "1500" });
                for (0..30) |_| try a.add(&.{ "--action", "next_result" });
                try a.add(&.{ "--action", "goto_anything", "--type", "sum_tree", "--pump", "300", "--key", "enter" });
            },
            .terminal => {
                try a.add(&.{ fx.project, "--action", "new_panel_terminal", "--pump", "800" });
                for (0..40) |_| try a.add(&.{ "--type", "echo profile", "--key", "enter", "--pump", "30" });
                for (0..20) |_| try a.add(&.{ "--type", "clear", "--key", "enter", "--pump", "30" });
            },
            .memory => {
                try a.add(&.{ fx.project, fx.workspace_zig, "--action", "show_memory" });
                for (0..8) |_| try a.add(&.{ "--pump", "1100" });
            },
            .soak => try a.add(&.{ fx.project, fx.workspace_zig, "--script", fx.soak }),
        }
    }
};

const Args = struct {
    gpa: Allocator,
    list: std.ArrayList([]const u8) = .empty,

    fn add(a: *Args, items: []const []const u8) !void {
        try a.list.appendSlice(a.gpa, items);
    }

    /// `n` key presses: the headless driver renders after each one.
    fn frames(a: *Args, n: usize, key: []const u8) !void {
        for (0..n) |_| try a.add(&.{ "--key", key });
    }
};

// ---- Measuring ---------------------------------------------------------------

const Run = struct {
    peak_rss_kb: i64,
    steady_rss_kb: i64,
    peak_footprint_kb: i64,
    /// Median RSS of the run's last quarter minus that of its second: what
    /// kept growing after the warm-up (the soak scenario's plateau check).
    growth_rss_kb: i64,
    wall_ms: i64,
    cpu_ms: i64,
};

const Sample = struct {
    t_ms: i64,
    /// Exact, from smaps_rollup.
    rss_kb: i64,
    /// From status: approximate, for the trace.
    vm_rss_kb: i64,
    vm_hwm_kb: i64,
    /// macOS: the physical footprint; 0 elsewhere.
    footprint_kb: i64 = 0,
};

/// Runs the editor once, sampling its memory every 10 ms until it exits.
fn measure(gpa: Allocator, io: Io, argv: []const []const u8, env: *const std.process.Environ.Map, opts: *const Options, trace: ?[]const u8) !Run {
    // A fresh config and state folder for every run.
    var run_dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const run_dir = try std.fmt.bufPrint(&run_dir_buf, "{s}/run", .{opts.fixtures});
    const cwd = Io.Dir.cwd();
    try cwd.deleteTree(io, run_dir);
    for ([_][]const u8{ "config", "state" }) |sub| {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        try cwd.createDirPath(io, try std.fmt.bufPrint(&buf, "{s}/{s}", .{ run_dir, sub }));
    }

    const start = Io.Clock.awake.now(io);
    const child = try std.process.spawn(io, .{
        .argv = argv,
        .cwd = .{ .path = opts.fixtures },
        .environ_map = env,
        .stdin = .ignore,
        .stdout = .ignore,
    });
    const pid = child.id.?;

    var samples: std.ArrayList(Sample) = .empty;
    defer samples.deinit(gpa);
    var path_bufs: [2][64]u8 = undefined;
    const rollup_path = try std.fmt.bufPrint(&path_bufs[0], "/proc/{d}/smaps_rollup", .{pid});
    const status_path = try std.fmt.bufPrint(&path_bufs[1], "/proc/{d}/status", .{pid});
    var status: u32 = 0;
    var ru: Rusage = undefined;
    while (true) {
        if (macos) {
            if (proc_stats.ofPid(pid)) |u| {
                const peak = @max(u.footprint, u.peak_footprint);
                try samples.append(gpa, .{
                    .t_ms = start.untilNow(io, .awake).toMilliseconds(),
                    .rss_kb = @intCast(u.resident / 1024),
                    .vm_rss_kb = @intCast(u.resident / 1024),
                    .vm_hwm_kb = 0,
                    .footprint_kb = @intCast(peak / 1024),
                });
            }
        } else {
            var bufs: [2][4096]u8 = undefined;
            // A zombie has neither (an empty smaps_rollup, no VmRSS line).
            const rollup = cwd.readFile(io, rollup_path, &bufs[0]) catch "";
            const status_text = cwd.readFile(io, status_path, &bufs[1]) catch "";
            if (field(rollup, "Rss:")) |rss| try samples.append(gpa, .{
                .t_ms = start.untilNow(io, .awake).toMilliseconds(),
                .rss_kb = rss,
                .vm_rss_kb = field(status_text, "VmRSS:") orelse 0,
                .vm_hwm_kb = field(status_text, "VmHWM:") orelse 0,
            });
        }
        if (try waitNoHang(pid, &status, &ru)) break;
        try io.sleep(.fromMilliseconds(10), .awake);
    }
    const wall_ms = start.untilNow(io, .awake).toMilliseconds();

    if (!W.IFEXITED(status) or W.EXITSTATUS(status) != 0) {
        if (W.IFSIGNALED(status)) {
            std.debug.print("the editor was killed by signal {t}\n", .{W.TERMSIG(status)});
        } else std.debug.print("the editor exited with status {d}\n", .{W.EXITSTATUS(status)});
        return error.EditorFailed;
    }

    if (trace) |path| {
        var file = try cwd.createFile(io, path, .{});
        defer file.close(io);
        var wbuf: [4096]u8 = undefined;
        var fw = file.writer(io, &wbuf);
        try fw.interface.writeAll("t_ms,rss_kb,vm_rss_kb,vm_hwm_kb,footprint_kb\n");
        for (samples.items) |s| try fw.interface.print("{d},{d},{d},{d},{d}\n", .{ s.t_ms, s.rss_kb, s.vm_rss_kb, s.vm_hwm_kb, s.footprint_kb });
        try fw.interface.flush();
    }

    // Steady state: the samples of the settle pump, which is the last thing
    // the driver does before a teardown of a few ms (the margins keep that
    // and the work before the pump out).
    const window_end = wall_ms - 100;
    const window_start = wall_ms - @as(i64, opts.settle_ms) + 100;
    var steady: std.ArrayList(i64) = .empty;
    defer steady.deinit(gpa);
    // ru_maxrss is in KiB on Linux, bytes on macOS.
    var peak: i64 = if (macos) @divTrunc(ru.maxrss, 1024) else ru.maxrss;
    var peak_footprint: i64 = 0;
    for (samples.items) |s| {
        peak = @max(peak, s.rss_kb);
        peak_footprint = @max(peak_footprint, s.footprint_kb);
        if (s.t_ms >= window_start and s.t_ms <= window_end) try steady.append(gpa, s.rss_kb);
    }
    if (steady.items.len == 0) return error.NoSteadySamples; // a run shorter than --settle?

    // Growth: quarters of the run, the first being warm-up.
    var growth: i64 = 0;
    if (samples.items.len >= 40) {
        const n = samples.items.len;
        var second: std.ArrayList(i64) = .empty;
        defer second.deinit(gpa);
        var last: std.ArrayList(i64) = .empty;
        defer last.deinit(gpa);
        for (samples.items[n / 4 .. n / 2]) |s| try second.append(gpa, s.rss_kb);
        for (samples.items[n - n / 4 ..]) |s| try last.append(gpa, s.rss_kb);
        growth = median(last.items) - median(second.items);
    }

    return .{
        .peak_rss_kb = peak,
        .peak_footprint_kb = peak_footprint,
        .growth_rss_kb = growth,
        .steady_rss_kb = median(steady.items),
        .wall_ms = wall_ms - opts.settle_ms,
        .cpu_ms = ms(ru.utime) + ms(ru.stime),
    };
}

/// The number after `name` in /proc/PID/status ("VmRSS:\t  123456 kB").
fn field(text: []const u8, name: []const u8) ?i64 {
    const at = std.mem.indexOf(u8, text, name) orelse return null;
    var it = std.mem.tokenizeAny(u8, text[at + name.len ..], " \t");
    return std.fmt.parseInt(i64, it.next() orelse return null, 10) catch null;
}

const W = if (macos) std.c.W else linux.W;
const Rusage = if (macos) std.c.rusage else linux.rusage;

extern "c" fn wait4(pid: std.c.pid_t, status: *c_int, options: c_int, ru: *std.c.rusage) std.c.pid_t;

/// Reaps `pid` if it has exited (true), filling `status` and `ru`.
fn waitNoHang(pid: i32, status: *u32, ru: *Rusage) !bool {
    while (true) {
        if (macos) {
            var st: c_int = 0;
            const rc = wait4(pid, &st, std.c.W.NOHANG, ru);
            status.* = @bitCast(st);
            if (rc == pid) return true;
            if (rc == 0) return false;
            const e = std.c.errno(rc);
            if (e == .INTR) continue;
            std.debug.print("wait4: {t}\n", .{e});
            return error.WaitFailed;
        }
        const rc = linux.wait4(pid, status, linux.W.NOHANG, ru);
        switch (linux.errno(rc)) {
            .SUCCESS => return rc == pid,
            .INTR => continue,
            else => |e| {
                std.debug.print("wait4: {t}\n", .{e});
                return error.WaitFailed;
            },
        }
    }
}

fn ms(t: anytype) i64 {
    return @as(i64, t.sec) * 1000 + @divTrunc(t.usec, 1000);
}

/// Sorts `values` in place.
fn median(values: []i64) i64 {
    if (values.len == 0) return 0;
    std.mem.sort(i64, values, {}, std.sort.asc(i64));
    const mid = values.len / 2;
    return if (values.len % 2 == 1) values[mid] else @divTrunc(values[mid - 1] + values[mid], 2);
}

// ---- Fixtures ----------------------------------------------------------------

const Fixtures = struct {
    project: []const u8,
    workspace_zig: []const u8,
    tabs: []const []const u8,
    biglog: []const u8,
    minified: []const u8,
    project_sha: []const u8,
    /// The soak scenario's steps (`--script`).
    soak: []const u8,
};

/// Creates whatever is missing under `opts.fixtures`. Strings live in `arena`.
fn prepareFixtures(arena: Allocator, io: Io, opts: *const Options) !Fixtures {
    const cwd = Io.Dir.cwd();
    try cwd.createDirPath(io, opts.fixtures);

    // The project: this repository at a fixed commit, so later builds open the same files.
    const rev = try git(arena, io, &.{ "rev-parse", "--verify", try std.fmt.allocPrint(arena, "{s}^{{commit}}", .{opts.project_rev}) });
    const project = try std.fmt.allocPrint(arena, "{s}/project-{s}", .{ opts.fixtures, rev[0..12] });
    if (cwd.access(io, project, .{})) |_| {} else |_| {
        std.debug.print("fixtures: git archive {s} -> {s}\n", .{ rev[0..12], project });
        const tmp = try std.fmt.allocPrint(arena, "{s}.tmp", .{project});
        const tar = try std.fmt.allocPrint(arena, "{s}.tar", .{project});
        try cwd.deleteTree(io, tmp);
        try cwd.createDirPath(io, tmp);
        _ = try git(arena, io, &.{ "archive", "--format=tar", "-o", tar, rev });
        try exec(arena, io, &.{ "tar", "-xf", tar, "-C", tmp });
        try cwd.deleteFile(io, tar);
        try cwd.rename(tmp, cwd, project, io);
    }

    // 50 of its .zig files, evenly spread over the sorted list.
    var all: std.ArrayList([]const u8) = .empty;
    {
        var dir = try cwd.openDir(io, project, .{ .iterate = true });
        defer dir.close(io);
        var walker = try dir.walk(arena);
        defer walker.deinit();
        while (try walker.next(io)) |entry| {
            if (entry.kind != .file or !std.mem.startsWith(u8, entry.path, "src/") or !std.mem.endsWith(u8, entry.path, ".zig")) continue;
            try all.append(arena, try std.fmt.allocPrint(arena, "{s}/{s}", .{ project, entry.path }));
        }
    }
    std.mem.sort([]const u8, all.items, {}, lessThan);
    const n_tabs = @min(50, all.items.len);
    const tabs = try arena.alloc([]const u8, n_tabs);
    for (tabs, 0..) |*t, i| t.* = all.items[i * all.items.len / n_tabs];

    const biglog = try std.fmt.allocPrint(arena, "{s}/biglog-100MiB.log", .{opts.fixtures});
    try generate(io, biglog, fixtures.log_bytes, fixtures.writeLog);
    const minified = try std.fmt.allocPrint(arena, "{s}/minified-20MiB.json", .{opts.fixtures});
    try generate(io, minified, fixtures.json_bytes, fixtures.writeJson);

    const soak = try std.fmt.allocPrint(arena, "{s}/soak-{d}.script", .{ opts.fixtures, opts.soak_cycles });
    try writeSoak(arena, io, soak, opts.soak_cycles);

    return .{
        .project = project,
        .workspace_zig = try std.fmt.allocPrint(arena, "{s}/src/app/workspace/workspace.zig", .{project}),
        .tabs = tabs,
        .biglog = biglog,
        .minified = minified,
        .project_sha = rev,
        .soak = soak,
    };
}

/// Names the soak's Go to File opens, a different one each cycle (the project's own files).
const soak_files = [_][]const u8{ "rope", "sum_tree", "buffer", "display_map", "editor_view", "workspace", "find_panel", "text_system", "scene", "atlas", "renderer", "theme", "keymap", "terminal_ui", "panel_ui", "lsp", "search", "org", "vim", "project" };

/// The soak scenario's steps: `cycles` rounds of everything that creates and
/// drops state, each ending where it started. An unsaved scratch buffer is
/// where the typing goes (closing it never asks to save).
fn writeSoak(arena: Allocator, io: Io, path: []const u8, cycles: u32) !void {
    var w: Io.Writer.Allocating = .init(arena);
    const out = &w.writer;
    for (0..cycles) |n| {
        const name = soak_files[n % soak_files.len];
        try out.print("# cycle {d}\n", .{n});
        // Open a few files, page through one, close them all.
        for (0..3) |k| try out.print("action goto_anything\ntype {s}\nkey enter\n", .{soak_files[(n + k * 7) % soak_files.len]});
        for (0..8) |_| try out.writeAll("key pagedown\n");
        for (0..8) |_| try out.writeAll("key pageup\n");
        try out.writeAll("action next_tab\naction prev_tab\naction close_all\n");
        // Edit a scratch buffer: typing, undo, redo, clipboard, multi-cursor, long lines.
        try out.print("action scratch_buffer\ntype text typed in cycle {d} {s} {s} {s}\n", .{ n, name, name, name });
        try out.writeAll("action select_all\naction copy\naction paste\naction paste\naction undo\naction redo\naction duplicate_line\naction add_cursor_below\ntype x\naction undo\naction single_selection\naction delete_line\n");
        for (0..20) |_| try out.writeAll("type word \n");
        for (0..10) |_| try out.writeAll("action undo\n");
        try out.writeAll("action close_tab\n");
        // Panes.
        try out.writeAll("action layout_columns_2\naction layout_grid_4\naction layout_single\n");
        // A shell in a tab and in the panel.
        try out.writeAll("action new_terminal\npump 30\ntype echo soak\nkey enter\ntype exit\nkey enter\npump 30\naction close_tab\n");
        // Find, Find in Files, the results tree.
        try out.print("action show_find\ntype {s}\naction find_next\naction find_next\nkey escape\n", .{name});
        try out.print("action show_find_in_files\ntype {s}\nkey enter\npump 60\naction next_result\naction next_result\nkey escape\naction toggle_panel\n", .{name});
        // Pages and views.
        try out.writeAll("action open_settings\ntype font\nkey escape\naction close_tab\naction show_memory\npump 20\naction toggle_panel\n");
        // Zoom: font changes rebuild the glyph caches.
        try out.writeAll("action increase_font\naction decrease_font\naction toggle_word_wrap\naction toggle_word_wrap\n");
    }
    try Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = w.written() });
}

fn lessThan(_: void, a: []const u8, b: []const u8) bool {
    return std.mem.lessThan(u8, a, b);
}

/// Writes `path` with `body` (through a temporary file) unless it exists.
fn generate(io: Io, path: []const u8, bytes: usize, body: *const fn (*Io.Writer, usize) Io.Writer.Error!void) !void {
    const cwd = Io.Dir.cwd();
    if (cwd.statFile(io, path, .{})) |st| {
        if (st.size >= bytes) return;
    } else |_| {}
    std.debug.print("fixtures: generating {s}\n", .{path});
    var tmp_buf: [std.fs.max_path_bytes]u8 = undefined;
    const tmp = try std.fmt.bufPrint(&tmp_buf, "{s}.tmp", .{path});
    {
        var file = try cwd.createFile(io, tmp, .{});
        defer file.close(io);
        var buf: [64 << 10]u8 = undefined;
        var fw = file.writer(io, &buf);
        try body(&fw.interface, bytes);
        try fw.interface.flush();
    }
    try cwd.rename(tmp, cwd, path, io);
}

/// Runs git in the current directory (this repository); returns its trimmed stdout.
fn git(arena: Allocator, io: Io, args: []const []const u8) ![]const u8 {
    const argv = try std.mem.concat(arena, []const u8, &.{ &.{"git"}, args });
    const r = try std.process.run(arena, io, .{ .argv = argv });
    if (r.term != .exited or r.term.exited != 0) {
        std.debug.print("git {s} failed: {s}\n", .{ args[0], r.stderr });
        return error.GitFailed;
    }
    return std.mem.trim(u8, r.stdout, " \n");
}

fn exec(arena: Allocator, io: Io, argv: []const []const u8) !void {
    const r = try std.process.run(arena, io, .{ .argv = argv });
    if (r.term != .exited or r.term.exited != 0) {
        std.debug.print("{s} failed: {s}\n", .{ argv[0], r.stderr });
        return error.CommandFailed;
    }
}

/// A shell command that runs a scenario like `measure` does.
fn printCommand(w: *Io.Writer, name: []const u8, argv: []const []const u8, opts: *const Options) !void {
    try w.print("# {s}\nmkdir -p {s}/run/config {s}/run/state && cd {s} && env -u IN_NIX_SHELL SHELL=/bin/sh XDG_CONFIG_HOME={s}/run/config XDG_STATE_HOME={s}/run/state", .{ name, opts.fixtures, opts.fixtures, opts.fixtures, opts.fixtures, opts.fixtures });
    for (argv) |arg| {
        const plain = for (arg) |ch| {
            if (!std.ascii.isAlphanumeric(ch) and std.mem.indexOfScalar(u8, "/._-+,=:", ch) == null) break false;
        } else true;
        if (plain) try w.print(" {s}", .{arg}) else try w.print(" '{s}'", .{arg});
    }
    try w.writeAll("\n");
}

// ---- Results -----------------------------------------------------------------

const Metric = struct {
    median: i64,
    runs: []const i64,
};

const ScenarioResult = struct {
    name: []const u8,
    description: []const u8,
    peak_rss_kb: Metric,
    steady_rss_kb: Metric,
    peak_footprint_kb: Metric,
    growth_rss_kb: Metric,
    wall_ms: Metric,
    cpu_ms: Metric,
};

const Results = struct {
    format: u32 = 2,
    binary: []const u8,
    binary_bytes: u64,
    project_rev: []const u8,
    size: []const u8,
    settle_ms: u32,
    warmup: u32,
    unix_time: i64,
    scenarios: []const ScenarioResult,
};

const metric_names = [_][]const u8{ "peak_rss_kb", "steady_rss_kb", "peak_footprint_kb", "growth_rss_kb", "wall_ms", "cpu_ms" };

fn summarize(arena: Allocator, name: []const u8, description: []const u8, runs: []const Run) !ScenarioResult {
    var result: ScenarioResult = undefined;
    result.name = name;
    result.description = description;
    inline for (metric_names) |m| {
        const values = try arena.alloc(i64, runs.len);
        for (runs, values) |r, *v| v.* = @field(r, m);
        const sorted = try arena.dupe(i64, values);
        @field(result, m) = .{ .median = median(sorted), .runs = values };
    }
    return result;
}

fn printTable(w: *Io.Writer, results: []const ScenarioResult, runs: usize) !void {
    try w.print("\n{s:<12} {s:>14} {s:>14} {s:>16} {s:>10} {s:>10}   median of {d} runs, min..max below\n", .{ "scenario", "peak RSS MiB", "steady RSS MiB", "peak footprint MiB", "wall ms", "cpu ms", runs });
    for (results) |r| {
        try w.print("{s:<12} {d:>14.1} {d:>14.1} {d:>16.1} {d:>10} {d:>10}\n", .{ r.name, mb(r.peak_rss_kb.median), mb(r.steady_rss_kb.median), mb(r.peak_footprint_kb.median), unsigned(r.wall_ms.median), unsigned(r.cpu_ms.median) });
        var bufs: [5][40]u8 = undefined;
        const peak_lo, const peak_hi = minMax(r.peak_rss_kb.runs);
        const steady_lo, const steady_hi = minMax(r.steady_rss_kb.runs);
        const fp_lo, const fp_hi = minMax(r.peak_footprint_kb.runs);
        const wall_lo, const wall_hi = minMax(r.wall_ms.runs);
        const cpu_lo, const cpu_hi = minMax(r.cpu_ms.runs);
        try w.print("{s:<12} {s:>14} {s:>14} {s:>16} {s:>10} {s:>10}\n", .{
            "",
            try std.fmt.bufPrint(&bufs[0], "{d:.1}..{d:.1}", .{ mb(peak_lo), mb(peak_hi) }),
            try std.fmt.bufPrint(&bufs[1], "{d:.1}..{d:.1}", .{ mb(steady_lo), mb(steady_hi) }),
            try std.fmt.bufPrint(&bufs[2], "{d:.1}..{d:.1}", .{ mb(fp_lo), mb(fp_hi) }),
            try std.fmt.bufPrint(&bufs[3], "{d}..{d}", .{ wall_lo, wall_hi }),
            try std.fmt.bufPrint(&bufs[4], "{d}..{d}", .{ cpu_lo, cpu_hi }),
        });
    }
}

/// A width makes `{d}` print a sign on signed integers.
fn unsigned(v: i64) u64 {
    return @intCast(@max(v, 0));
}

/// KiB to MiB.
fn mb(kb: i64) f64 {
    return @as(f64, @floatFromInt(kb)) / 1024;
}

fn minMax(values: []const i64) [2]i64 {
    if (values.len == 0) return .{ 0, 0 };
    return .{ std.mem.min(i64, values), std.mem.max(i64, values) };
}

/// Prints the change of every median from `old`; returns how many are
/// flagged: more than the threshold worse and worse than every baseline run
/// (all four metrics are lower-is-better).
fn printComparison(w: *Io.Writer, old: Results, new: []const ScenarioResult, project_rev: []const u8, opts: *const Options) !usize {
    try w.print("\nCompared with {s} (binary {s}):\n", .{ opts.compare.?, old.binary });
    if (!std.mem.eql(u8, old.project_rev, project_rev))
        try w.print("  note: the baseline's project was {s}, this one is {s}: the inputs differ\n", .{ old.project_rev, project_rev });
    if (!std.mem.eql(u8, old.size, opts.size) or old.settle_ms != opts.settle_ms)
        try w.print("  note: the baseline used --size {s} --settle {d}\n", .{ old.size, old.settle_ms });
    try w.print("{s:<12} {s:<14} {s:>12} {s:>12} {s:>9}   {s}\n", .{ "scenario", "metric", "baseline", "now", "change", "baseline runs" });
    var worse: usize = 0;
    for (new) |n| {
        const o = for (old.scenarios) |s| {
            if (std.mem.eql(u8, s.name, n.name)) break s;
        } else {
            try w.print("{s:<12} (not in the baseline)\n", .{n.name});
            continue;
        };
        inline for (metric_names) |m| {
            const before = @field(o, m).median;
            const after = @field(n, m).median;
            const pct = if (before == 0) 0 else 100 * @as(f64, @floatFromInt(after - before)) / @as(f64, @floatFromInt(before));
            const lo, const hi = minMax(@field(o, m).runs);
            const over = pct > opts.threshold;
            const flag = over and after > hi;
            if (flag) worse += 1;
            var bufs: [2][40]u8 = undefined;
            const change = try std.fmt.bufPrint(&bufs[0], "{s}{d:.1}%", .{ if (pct >= 0) "+" else "", pct });
            const runs = try std.fmt.bufPrint(&bufs[1], "{d}..{d}", .{ lo, hi });
            const note = if (flag) "  << worse" else if (over) "  (within the baseline's runs)" else "";
            try w.print("{s:<12} {s:<14} {d:>12} {d:>12} {s:>9}   {s}{s}\n", .{ n.name, m, unsigned(before), unsigned(after), change, runs, note });
        }
    }
    try w.print("{d} change(s) flagged: more than {d}% worse and worse than every baseline run\n", .{ worse, opts.threshold });
    return worse;
}

// ---- Main --------------------------------------------------------------------

pub fn main(init: std.process.Init) !u8 {
    const gpa = init.gpa;
    const io = init.io;
    var arena_state: std.heap.ArenaAllocator = .init(gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    var opts: Options = .{};
    var args = try init.minimal.args.iterateAllocator(gpa);
    defer args.deinit();
    _ = args.skip();
    while (args.next()) |arg| {
        const eql = std.mem.eql;
        const value = struct {
            fn f(it: anytype, name: []const u8) ![]const u8 {
                const v = it.next() orelse {
                    std.debug.print("{s} needs a value\n", .{name});
                    return error.MissingArgument;
                };
                return v;
            }
        }.f;
        if (eql(u8, arg, "-h") or eql(u8, arg, "--help")) {
            std.debug.print("{s}", .{usage});
            return 0;
        } else if (eql(u8, arg, "--bin")) {
            opts.bin = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--runs")) {
            opts.runs = try std.fmt.parseInt(u32, try value(&args, arg), 10);
        } else if (eql(u8, arg, "--warmup")) {
            opts.warmup = try std.fmt.parseInt(u32, try value(&args, arg), 10);
        } else if (eql(u8, arg, "--only")) {
            opts.only = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--out")) {
            opts.out = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--compare")) {
            opts.compare = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--threshold")) {
            opts.threshold = try std.fmt.parseFloat(f64, try value(&args, arg));
        } else if (eql(u8, arg, "--fixtures")) {
            opts.fixtures = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--project-rev")) {
            opts.project_rev = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--size")) {
            opts.size = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--settle")) {
            opts.settle_ms = try std.fmt.parseInt(u32, try value(&args, arg), 10);
        } else if (eql(u8, arg, "--soak-cycles")) {
            opts.soak_cycles = try std.fmt.parseInt(u32, try value(&args, arg), 10);
        } else if (eql(u8, arg, "--shots")) {
            opts.shots = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--trace")) {
            opts.trace = try arena.dupe(u8, try value(&args, arg));
        } else if (eql(u8, arg, "--print")) {
            opts.print = true;
        } else {
            std.debug.print("unknown option: {s}\n\n{s}", .{ arg, usage });
            return 2;
        }
    }
    if (opts.runs == 0 or opts.settle_ms < 300) {
        std.debug.print("--runs must be at least 1 and --settle at least 300\n", .{});
        return 2;
    }
    if (opts.only) |only| {
        var it = std.mem.tokenizeScalar(u8, only, ',');
        while (it.next()) |n| if (std.meta.stringToEnum(Scenario, n) == null) {
            std.debug.print("unknown scenario {s}; the scenarios are:", .{n});
            for (std.enums.values(Scenario)) |scn| std.debug.print(" {t}", .{scn});
            std.debug.print("\n", .{});
            return 2;
        };
    }

    const cwd = Io.Dir.cwd();
    // The editor runs in the fixtures folder: make the binary path absolute.
    const bin = cwd.realPathFileAlloc(io, opts.bin, arena) catch |err| {
        std.debug.print("--bin {s}: {t}\n", .{ opts.bin, err });
        return 2;
    };
    const bin_bytes = (try cwd.statFile(io, bin, .{})).size;
    try cwd.createDirPath(io, opts.fixtures);
    opts.fixtures = try cwd.realPathFileAlloc(io, opts.fixtures, arena);
    const fx = try prepareFixtures(arena, io, &opts);
    if (opts.trace) |dir| try cwd.createDirPath(io, dir);
    if (opts.shots) |dir| {
        // The editor writes them, from the fixtures folder.
        try cwd.createDirPath(io, dir);
        opts.shots = try cwd.realPathFileAlloc(io, dir, arena);
    }

    // The editor's environment: the dev shell's (libraries, grammars) with
    // the user's configuration hidden and a plain shell for terminals.
    var env = try init.environ_map.clone(arena);
    try env.put("XDG_CONFIG_HOME", try std.fmt.allocPrint(arena, "{s}/run/config", .{opts.fixtures}));
    try env.put("XDG_STATE_HOME", try std.fmt.allocPrint(arena, "{s}/run/state", .{opts.fixtures}));
    try env.put("SHELL", "/bin/sh");
    _ = env.swapRemove("IN_NIX_SHELL"); // else terminals use the login shell

    var stdout_buf: [4096]u8 = undefined;
    var stdout = Io.File.stdout().writer(io, &stdout_buf);
    const out = &stdout.interface;
    try out.print("binary   {s} ({d} bytes)\nproject  {s}\nfixtures {s}\n", .{ bin, bin_bytes, fx.project_sha, opts.fixtures });
    try out.flush();

    var results: std.ArrayList(ScenarioResult) = .empty;
    var soak_failed = false;
    for (std.enums.values(Scenario)) |scn| {
        const name = @tagName(scn);
        if (opts.only) |only| {
            var it = std.mem.tokenizeScalar(u8, only, ',');
            while (it.next()) |n| {
                if (std.mem.eql(u8, n, name)) break;
            } else continue;
        }
        var a: Args = .{ .gpa = arena };
        try a.add(&.{ bin, "--headless", opts.size, "--no-lsp" });
        try scn.args(&fx, &a);
        try a.add(&.{ "--pump", try std.fmt.allocPrint(arena, "{d}", .{opts.settle_ms}) });
        if (opts.print) {
            try printCommand(out, name, a.list.items, &opts);
            continue;
        }

        try out.print("{s}: {s}\n", .{ name, scn.description() });
        try out.flush();
        // A screenshot needs a run of its own: it costs time and memory.
        for (0..@max(opts.warmup, @intFromBool(opts.shots != null))) |i| {
            var argv = a.list.items;
            if (i == 0) if (opts.shots) |dir| {
                argv = try std.mem.concat(arena, []const u8, &.{ argv, &.{ "--screenshot", try std.fmt.allocPrint(arena, "{s}/{s}.png", .{ dir, name }) } });
            };
            _ = try measure(gpa, io, argv, &env, &opts, null);
        }
        const runs = try arena.alloc(Run, opts.runs);
        for (runs, 0..) |*r, i| {
            const trace = if (opts.trace) |dir| try std.fmt.allocPrint(arena, "{s}/{s}-{d}.csv", .{ dir, name, i + 1 }) else null;
            r.* = try measure(gpa, io, a.list.items, &env, &opts, trace);
            try out.print("  run {d}: peak {d:.1} MiB, steady {d:.1} MiB, footprint {d:.1} MiB, wall {d} ms, cpu {d} ms\n", .{ i + 1, mb(r.peak_rss_kb), mb(r.steady_rss_kb), mb(r.peak_footprint_kb), r.wall_ms, r.cpu_ms });
            try out.flush();
        }
        const result = try summarize(arena, name, scn.description(), runs);
        try results.append(arena, result);
        if (scn == .soak) {
            // Plateau: what grew after the warm-up may be 8 MiB, or a twelfth of what the editor holds.
            const growth = result.growth_rss_kb.median;
            const budget = @max(8 << 10, @divTrunc(result.steady_rss_kb.median, 12));
            const ok = growth <= budget;
            try out.print("  soak: RSS grew {d:.1} MiB from the run's second to its last quarter (budget {d:.1} MiB): {s}\n", .{ mb(growth), mb(budget), if (ok) "plateaued" else "STILL GROWING" });
            if (!ok) soak_failed = true;
        }
    }

    if (opts.print) {
        try out.flush();
        return 0;
    }
    try printTable(out, results.items, opts.runs);

    if (opts.out) |path| {
        const doc: Results = .{
            .binary = bin,
            .binary_bytes = bin_bytes,
            .project_rev = fx.project_sha,
            .size = opts.size,
            .settle_ms = opts.settle_ms,
            .warmup = opts.warmup,
            .unix_time = Io.Clock.real.now(io).toSeconds(),
            .scenarios = results.items,
        };
        const json = try std.json.Stringify.valueAlloc(arena, doc, .{ .whitespace = .indent_2 });
        try cwd.writeFile(io, .{ .sub_path = path, .data = json });
        try out.print("\nwrote {s}\n", .{path});
    }

    var worse: usize = 0;
    if (opts.compare) |path| {
        const text = try cwd.readFileAlloc(io, path, arena, .limited(16 << 20));
        const old = try std.json.parseFromSliceLeaky(Results, arena, text, .{ .ignore_unknown_fields = true });
        worse = try printComparison(out, old, results.items, fx.project_sha, &opts);
    }
    try out.flush();
    return if (worse > 0 or soak_failed) 1 else 0;
}
