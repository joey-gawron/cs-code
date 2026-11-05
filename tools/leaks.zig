//! `zig build leaks [-- options]`: scripted headless sessions that exercise
//! every feature of the editor, run under the leak checking of Debug and
//! ReleaseSafe builds (the allocator `main` gets reports every leak with the
//! stack that allocated it when the program returns), failing on a leak, a
//! crash, a hang or a use of freed memory the allocator catches.
//!
//!   --bin PATH        the editor (default zig-out/bin/cs); must be a Debug
//!                     or ReleaseSafe build
//!   --only A,B        run only these scenarios (`--list` names them all)
//!   --fixtures DIR    the generated project and the runs' homes
//!                     (default /tmp/cs-zig-leaks)
//!   --grammars DIR    tree-sitter grammars (tools/fetch-grammars.sh); without
//!                     them nothing parses, and parsers are never exercised
//!   --tool leaks      run each session under macOS's `leaks --atExit`
//!                     instead, which finds what C libraries (FreeType,
//!                     HarfBuzz, fontconfig, libvterm, tree-sitter, Cocoa)
//!                     leak; works on any build
//!   --timeout S       per-session limit, default 300 s
//!   --shots DIR       save each session's last frame as DIR/<scenario>.png,
//!                     to check that it reaches what it claims to
//!   --verbose         show the sessions' whole stderr
//!
//! Every session gets an empty home (settings, state, sessions) and a plain
//! shell. Sessions that need a tool (zls, lldb-dap, a C compiler, a running
//! Lima VM for Remote-SSH) are skipped, and said to be, when it is missing.

const std = @import("std");
const fixtures = @import("fixtures");
const Io = std.Io;
const Allocator = std.mem.Allocator;

const usage =
    \\Usage: zig build leaks -- [--bin PATH] [--only A,B] [--list] [--fixtures DIR]
    \\                          [--grammars DIR] [--tool leaks] [--timeout S] [--verbose]
    \\
;

const Tool = enum { allocator, leaks };

const Options = struct {
    bin: []const u8 = "zig-out/bin/cs",
    only: ?[]const u8 = null,
    list: bool = false,
    fixtures: []const u8 = "/tmp/cs-zig-leaks",
    grammars: ?[]const u8 = null,
    tool: Tool = .allocator,
    timeout_s: i64 = 300,
    shots: ?[]const u8 = null,
    verbose: bool = false,
    /// Set by build.zig when the build has no leak checking (ReleaseFast).
    unchecked_build: bool = false,
    /// The folder `zig build` runs in: the project's `src` is copied from it.
    repo: []const u8 = ".",
    /// The user's home, where Lima keeps its ssh configuration.
    home: []const u8 = "/",
};

// ---- Scenarios ---------------------------------------------------------------

const Scenario = enum {
    startup,
    editing,
    bigfiles,
    tabs,
    splits,
    terminal,
    search,
    settings,
    pages,
    org,
    vim,
    macros,
    git,
    session,
    lsp,
    debug,
    remote,

    fn description(s: Scenario) []const u8 {
        return switch (s) {
            .startup => "empty window; every view toggle, font zoom, 30 frames",
            .editing => "a big source file: typing, multi-cursor, undo/redo, clipboard, folds, line commands",
            .bigfiles => "a 3 MiB log and a 1 MiB one-line JSON: scrolling, wrapping, editing, undo",
            .tabs => "40 files open at once, switching, closing, reopening",
            .splits => "columns, rows, grid, moving files between panes, closing panes, zoom",
            .terminal => "a tab terminal and panel terminals: input, rename, exit, kill",
            .search => "find bar, Find in Files and its results tree, replace, Go to File/Symbol/Line, palette",
            .settings => "Settings and Keyboard Shortcuts pages, editing a setting, the theme list",
            .pages => "Welcome and New Project pages",
            .org => "an org file: agenda views, to do list, capture, TODO cycling, export",
            .vim => "vim mode: operators, text objects, macros, registers, :s, :g, search, folds",
            .macros => "recorded macros and multiple cursors",
            .git => "Source Control view, blame, inline blame, commit details",
            .session => "a named session saved, restored with unsaved text, and restored again",
            .lsp => "zls: diagnostics, completion, hover, definition, references, rename, format, restart",
            .debug => "lldb-dap: breakpoint, step over/into/out, watch, evaluate, stop",
            .remote => "Remote-SSH to the Lima VM: tree, open, edit, terminal, search",
        };
    }

    /// Programs the scenario can not run without.
    fn needs(s: Scenario) []const []const u8 {
        return switch (s) {
            .lsp => &.{"zls"},
            .debug => &.{ "lldb-dap", "cc" },
            .remote => &.{"limactl"},
            else => &.{},
        };
    }
};

const Run = struct {
    argv: []const []const u8,
    /// Extra environment for this run.
    env: []const [2][]const u8 = &.{},
};

const Args = struct {
    arena: Allocator,
    list: std.ArrayList([]const u8) = .empty,

    fn add(a: *Args, items: []const []const u8) !void {
        try a.list.appendSlice(a.arena, items);
    }
    fn key(a: *Args, k: []const u8) !void {
        try a.add(&.{ "--key", k });
    }
    fn keys(a: *Args, ks: []const []const u8) !void {
        for (ks) |k| try a.key(k);
    }
    fn act(a: *Args, name: []const u8) !void {
        try a.add(&.{ "--action", name });
    }
    fn acts(a: *Args, names: []const []const u8) !void {
        for (names) |n| try a.act(n);
    }
    fn typ(a: *Args, text: []const u8) !void {
        try a.add(&.{ "--type", text });
    }
    fn pump(a: *Args, ms: u32) !void {
        try a.add(&.{ "--pump", try std.fmt.allocPrint(a.arena, "{d}", .{ms}) });
    }
    fn repeatKey(a: *Args, n: usize, k: []const u8) !void {
        for (0..n) |_| try a.key(k);
    }
    fn repeatAct(a: *Args, n: usize, name: []const u8) !void {
        for (0..n) |_| try a.act(name);
    }
};

const Project = struct {
    root: []const u8,
    src: []const u8,
    rope: []const u8,
    editor: []const u8,
    log: []const u8,
    json: []const u8,
    org: []const u8,
    readme: []const u8,
    cprog: []const u8,
    demo_main: []const u8,
    demo_dir: []const u8,
    empty_dir: []const u8,
    sources: []const []const u8,
};

fn scenarioRuns(arena: Allocator, s: Scenario, p: *const Project, opts: *const Options) ![]const Run {
    var runs: std.ArrayList(Run) = .empty;
    var a: Args = .{ .arena = arena };
    switch (s) {
        .startup => {
            try a.add(&.{p.empty_dir});
            try a.pump(300);
            for ([_][]const u8{ "toggle_sidebar", "toggle_minimap", "toggle_status_bar", "toggle_line_numbers", "toggle_word_wrap", "toggle_tabs", "toggle_whitespace", "toggle_indent_guides", "toggle_menu", "toggle_distraction_free" }) |n| {
                try a.act(n);
                try a.act(n);
            }
            try a.repeatAct(3, "increase_font");
            try a.repeatAct(3, "decrease_font");
            try a.act("reset_font");
            try a.act("toggle_big_font");
            try a.act("toggle_big_font");
            try a.act("toggle_panel");
            try a.act("toggle_panel");
            try a.repeatKey(30, "escape");
        },
        .editing => {
            try a.add(&.{ p.root, p.rope });
            try a.act("goto_line");
            try a.typ("120");
            try a.key("enter");
            try a.typ("// typed into a big file ");
            try a.acts(&.{ "undo", "redo", "soft_undo", "soft_redo", "select_all", "copy", "single_selection", "move_doc_end", "paste", "undo" });
            try a.acts(&.{ "move_doc_start", "find_under_expand", "find_under_expand", "find_all_under", "upper_case", "lower_case", "title_case", "single_selection" });
            try a.repeatAct(6, "add_cursor_below");
            try a.typ("x");
            try a.acts(&.{ "undo", "single_selection", "duplicate_line", "swap_line_down", "swap_line_up", "join_lines", "delete_line", "insert_line_after", "insert_line_before", "toggle_comment", "toggle_comment", "indent", "unindent" });
            try a.acts(&.{ "sort_lines", "reverse_lines", "unique_lines", "trim_trailing_whitespace", "convert_indentation_to_tabs", "convert_indentation_to_spaces" });
            try a.acts(&.{ "fold", "fold_level_1", "unfold_all", "expand_selection_line", "expand_selection_brackets", "jump_to_bracket", "split_selection_into_lines" });
            try a.repeatKey(40, "pagedown");
            try a.repeatKey(40, "pageup");
            for (0..50) |_| {
                try a.typ("edit ");
                try a.act("undo");
                try a.act("redo");
            }
            try a.acts(&.{ "toggle_read_only", "toggle_read_only", "revert_file" });
            try a.pump(200);
        },
        .bigfiles => {
            try a.add(&.{ p.log, p.json });
            try a.key("ctrl+end");
            try a.repeatKey(15, "pageup");
            try a.act("toggle_word_wrap");
            try a.repeatKey(10, "pagedown");
            try a.act("toggle_word_wrap");
            try a.act("next_tab");
            try a.key("ctrl+end");
            try a.repeatKey(20, "left");
            try a.typ("EDIT");
            try a.acts(&.{ "undo", "redo", "move_doc_start", "select_all", "cut", "undo" });
            try a.act("prev_tab");
            try a.acts(&.{ "select_all", "copy", "move_doc_end", "paste", "undo" });
            try a.pump(300);
        },
        .tabs => {
            try a.add(&.{p.root});
            try a.add(p.sources[0..@min(40, p.sources.len)]);
            try a.repeatAct(45, "next_tab");
            try a.repeatAct(12, "prev_tab");
            try a.repeatAct(20, "close_tab");
            try a.repeatAct(6, "reopen_closed");
            try a.acts(&.{ "tab_1", "tab_3", "close_other_tabs", "close_all" });
            try a.pump(200);
        },
        .splits => {
            try a.add(&.{ p.root, p.rope, p.editor });
            for ([_][]const u8{ "layout_columns_2", "layout_rows_2", "layout_grid_4", "layout_columns_3", "layout_single" }) |n| {
                try a.act(n);
                try a.acts(&.{ "next_group", "move_to_next_group", "clone_file", "next_group", "prev_group" });
            }
            try a.act("layout_grid_4");
            try a.acts(&.{ "focus_group_1", "focus_group_2", "focus_group_3", "focus_group_4", "zoom_group", "zoom_group", "new_pane", "close_pane", "close_pane" });
            try a.act("layout_single");
            try a.pump(200);
        },
        .terminal => {
            try a.add(&.{p.root});
            try a.act("new_terminal");
            try a.pump(900);
            try a.typ("echo leak-check; ls");
            try a.key("enter");
            try a.pump(900);
            try a.act("new_panel_terminal");
            try a.pump(600);
            try a.typ("cat /dev/null");
            try a.key("enter");
            try a.act("new_panel_terminal");
            try a.act("rename_terminal");
            try a.typ("build");
            try a.key("enter");
            try a.act("new_terminal_at_project_root");
            try a.pump(600);
            try a.typ("exit");
            try a.key("enter");
            try a.pump(900);
            try a.act("toggle_panel_terminal");
            try a.act("toggle_panel_terminal");
            try a.act("run_command");
            try a.typ("echo from run");
            try a.key("enter");
            try a.pump(900);
            try a.act("run_last_command");
            try a.pump(600);
        },
        .search => {
            try a.add(&.{ p.root, p.rope });
            try a.act("show_find");
            try a.typ("chunk");
            try a.repeatAct(12, "find_next");
            try a.repeatAct(4, "find_prev");
            try a.acts(&.{ "toggle_find_case", "toggle_find_whole_word", "toggle_find_regex", "toggle_find_preserve_case", "toggle_find_regex", "find_all" });
            try a.key("escape");
            try a.act("show_replace");
            try a.typ("zzz");
            try a.acts(&.{ "toggle_find_case", "toggle_find_case" });
            try a.key("escape");
            try a.act("show_find_in_files");
            try a.typ("Allocator");
            try a.key("enter");
            try a.pump(1500);
            try a.repeatAct(8, "next_result");
            try a.repeatAct(3, "prev_result");
            try a.act("show_replace_in_files");
            try a.typ("Allocator");
            try a.key("escape");
            try a.act("goto_anything");
            try a.typ("sum_tree");
            try a.pump(300);
            try a.key("enter");
            try a.act("goto_symbol");
            try a.typ("push");
            try a.pump(200);
            try a.key("escape");
            try a.act("goto_line");
            try a.typ("30");
            try a.key("enter");
            try a.act("command_palette");
            try a.typ("memory");
            try a.pump(200);
            try a.key("escape");
            try a.act("project_todos");
            try a.pump(800);
            try a.act("rescan_project");
            try a.pump(500);
        },
        .settings => {
            try a.add(&.{ p.root, p.readme });
            try a.act("open_settings");
            try a.typ("font");
            try a.repeatKey(4, "down");
            try a.key("escape");
            try a.act("open_settings");
            try a.act("open_keyboard_shortcuts");
            try a.typ("save");
            try a.pump(200);
            try a.act("open_settings_json");
            try a.act("open_keymap");
            try a.act("select_color_scheme");
            try a.repeatKey(5, "down");
            try a.key("up");
            try a.key("enter");
            try a.act("select_color_scheme");
            try a.repeatKey(3, "down");
            try a.key("escape");
            try a.act("reload_config");
            try a.pump(300);
            try a.repeatAct(2, "close_tab");
        },
        .pages => {
            try a.add(&.{p.empty_dir});
            try a.act("show_welcome");
            try a.pump(200);
            try a.act("new_project");
            try a.repeatKey(6, "down");
            try a.typ("sample");
            try a.repeatKey(3, "tab");
            try a.key("escape");
            try a.act("show_welcome");
            try a.act("open_recent");
            try a.key("escape");
            try a.act("new_project");
            try a.repeatKey(2, "up");
            try a.pump(200);
        },
        .org => {
            try a.add(&.{ p.root, p.org });
            try a.act("org_agenda");
            try a.pump(300);
            try a.act("org_agenda_day");
            try a.act("org_todo_list");
            try a.pump(300);
            try a.acts(&.{ "org_agenda", "org_agenda_search" });
            try a.typ("report");
            try a.key("enter");
            try a.act("org_agenda_tags");
            try a.key("escape");
            try a.act("close_tab");
            try a.act("org_cycle_todo");
            try a.repeatAct(3, "org_cycle_todo");
            try a.acts(&.{ "org_toggle_checkbox", "org_global_cycle", "org_global_cycle", "org_promote_subtree", "org_demote_subtree", "org_move_subtree_down", "org_move_subtree_up", "org_priority_up", "org_priority_down", "org_table_align", "org_insert_heading" });
            try a.act("org_capture");
            try a.key("escape");
            try a.act("org_export_html");
            try a.pump(300);
            try a.act("org_export_markdown");
            try a.pump(300);
            try a.acts(&.{ "org_find_notes", "org_clock_in", "org_clock_out", "org_clock_report" });
            try a.key("escape");
            try a.pump(300);
        },
        .vim => {
            try a.add(&.{ "--vim", p.root, p.rope });
            try a.typ("ggdd");
            try a.typ("u");
            try a.typ("3dw");
            try a.typ("ciwreplaced");
            try a.key("escape");
            try a.typ(".");
            try a.typ("qadwjq");
            try a.typ("@a");
            try a.typ("3@a");
            try a.typ("@@");
            try a.typ("yyjp");
            try a.typ("\"ayw");
            try a.typ("\"ap");
            try a.typ("ma");
            try a.typ("G");
            try a.typ("'a");
            try a.typ("vjjy");
            try a.key("escape");
            try a.key("ctrl+v");
            try a.typ("jjlld");
            try a.typ("V");
            try a.typ("jd");
            try a.typ("zfjzo");
            try a.typ("zazA");
            try a.typ("/chunk");
            try a.key("enter");
            try a.typ("nnN*#");
            try a.typ(":%s/const/CONST/g");
            try a.key("enter");
            try a.typ("u");
            try a.typ(":g/pub fn/normal A;");
            try a.key("enter");
            try a.typ(":t.");
            try a.key("enter");
            try a.typ(":m0");
            try a.key("enter");
            try a.typ(":sp");
            try a.key("enter");
            try a.typ(":vs");
            try a.key("enter");
            try a.key("ctrl+w");
            try a.key("w");
            try a.typ(":only");
            try a.key("enter");
            try a.typ("ggVGJ");
            try a.typ("u");
            try a.typ("ggjjAhello");
            try a.key("escape");
            try a.typ("o");
            try a.typ("world");
            try a.key("escape");
            try a.typ("5i-");
            try a.key("escape");
            try a.typ("ggqqIx");
            try a.key("escape");
            try a.typ("jq");
            try a.typ("10@q");
            try a.pump(200);
        },
        .macros => {
            try a.add(&.{ p.root, p.rope });
            try a.act("toggle_record_macro");
            try a.typ("macro ");
            try a.acts(&.{ "move_line_end", "insert_newline", "move_up", "toggle_record_macro" });
            try a.repeatAct(8, "run_macro");
            try a.acts(&.{ "find_under_expand", "find_under_expand_skip", "find_all_under", "add_cursor_above", "add_cursor_below", "split_selection_into_lines" });
            try a.typ("multi");
            try a.acts(&.{ "undo", "single_selection" });
            try a.pump(100);
        },
        .git => {
            try a.add(&.{ p.root, p.readme });
            try a.act("git_status");
            try a.pump(1500);
            try a.act("git_toggle_blame");
            try a.pump(1500);
            try a.act("git_toggle_blame");
            try a.act("git_toggle_inline_blame");
            try a.pump(1500);
            try a.act("git_show_commit");
            try a.pump(1500);
            try a.key("escape");
            try a.act("git_copy_commit_hash");
            try a.act("git_toggle_inline_blame");
            try a.act("git_status");
            try a.repeatKey(3, "down");
            try a.key("escape");
            try a.pump(300);
        },
        .session => {
            // Saved by the first run, restored by the second with its unsaved
            // text, then restored again after the second changed it.
            try a.add(&.{ "--session", "leakcheck", p.root, p.readme, p.rope });
            try a.act("layout_columns_2");
            try a.typ("unsaved text that the session keeps ");
            try a.act("new_file");
            try a.typ("scratch buffer");
            try a.act("fold");
            try a.pump(2000);
            try runs.append(arena, .{ .argv = try arena.dupe([]const u8, a.list.items) });
            var b: Args = .{ .arena = arena };
            try b.add(&.{ "--session", "leakcheck" });
            try b.act("next_group");
            try b.typ("more ");
            try b.act("close_tab");
            try b.pump(2000);
            try runs.append(arena, .{ .argv = try arena.dupe([]const u8, b.list.items) });
            var c: Args = .{ .arena = arena };
            try c.add(&.{ "--session", "leakcheck" });
            try c.act("session_switch");
            try c.key("escape");
            try c.pump(500);
            try runs.append(arena, .{ .argv = try arena.dupe([]const u8, c.list.items) });
            return runs.items;
        },
        .lsp => {
            try a.add(&.{ p.demo_dir, p.demo_main });
            try a.pump(5000);
            try a.act("goto_line");
            try a.typ("3");
            try a.key("enter");
            try a.repeatAct(2, "move_word_right");
            try a.acts(&.{ "lsp_hover", "lsp_goto_definition", "lsp_find_references" });
            try a.pump(1500);
            try a.key("escape");
            try a.acts(&.{ "lsp_goto_type_definition", "lsp_goto_implementation", "lsp_code_actions", "lsp_signature_help" });
            try a.pump(800);
            try a.key("escape");
            try a.act("goto_line");
            try a.typ("11");
            try a.key("enter");
            try a.act("move_line_end");
            try a.typ(" std.");
            try a.act("lsp_complete");
            try a.pump(2500);
            try a.key("escape");
            try a.acts(&.{ "undo", "lsp_list_diagnostics", "lsp_next_diagnostic", "lsp_prev_diagnostic", "lsp_next_error", "lsp_prev_error" });
            try a.act("goto_line");
            try a.typ("3");
            try a.key("enter");
            try a.act("move_word_right");
            try a.act("lsp_rename");
            try a.typ("Pt");
            try a.key("enter");
            try a.pump(2500);
            try a.acts(&.{ "lsp_format", "save", "lsp_show_status", "lsp_clear_log" });
            try a.pump(2500);
            try a.act("lsp_restart");
            try a.pump(4000);
            try a.acts(&.{ "lsp_hover", "lsp_list_diagnostics" });
            try a.pump(1500);
        },
        .debug => {
            try a.add(&.{ p.root, p.cprog });
            // The line of `int result = compute(x, item.count);`.
            try a.act("goto_line");
            try a.typ("18");
            try a.key("enter");
            try a.act("debug_toggle_breakpoint");
            try a.act("debug_start");
            try a.pump(6000);
            try a.act("debug_add_watch");
            try a.typ("x");
            try a.key("enter");
            try a.pump(500);
            try a.act("debug_evaluate");
            try a.typ("x + 1");
            try a.key("enter");
            try a.pump(800);
            try a.act("debug_step_over");
            try a.pump(1500);
            try a.act("debug_step_into");
            try a.pump(1500);
            try a.act("debug_step_out");
            try a.pump(1500);
            try a.acts(&.{ "debug_show_panel", "debug_restart" });
            try a.pump(5000);
            try a.act("debug_pause");
            try a.pump(500);
            try a.act("debug_stop");
            try a.pump(1500);
            try a.acts(&.{ "debug_remove_all_breakpoints", "debug_start" });
            try a.pump(5000);
            try a.act("debug_stop");
            try a.pump(1000);
        },
        .remote => {
            try a.add(&.{ "--remote", "lima-jiffy-ubuntu", "/tmp" });
            try a.pump(20000);
            try a.act("show_find_in_files");
            try a.typ("cs");
            try a.key("enter");
            try a.pump(3000);
            try a.act("new_terminal");
            try a.pump(3000);
            try a.typ("uname -a");
            try a.key("enter");
            try a.pump(1500);
            try a.act("remote_close");
            try a.pump(2000);
            const env = try arena.alloc([2][]const u8, 1);
            env[0] = .{ "CS_ZIG_SSH_CONFIG", try std.fmt.allocPrint(arena, "{s}/.lima/jiffy-ubuntu/ssh.config", .{opts.home}) };
            try runs.append(arena, .{ .argv = try arena.dupe([]const u8, a.list.items), .env = env });
            return runs.items;
        },
    }
    try runs.append(arena, .{ .argv = try arena.dupe([]const u8, a.list.items) });
    return runs.items;
}

// ---- Fixtures ----------------------------------------------------------------

fn run(arena: Allocator, io: Io, cwd: ?[]const u8, argv: []const []const u8) ![]const u8 {
    const r = try std.process.run(arena, io, .{ .argv = argv, .cwd = if (cwd) |c| .{ .path = c } else .inherit });
    if (r.term != .exited or r.term.exited != 0) {
        std.debug.print("{s} failed:\n{s}{s}\n", .{ argv[0], r.stdout, r.stderr });
        return error.CommandFailed;
    }
    return std.mem.trim(u8, r.stdout, " \n");
}

fn write(io: Io, path: []const u8, text: []const u8) !void {
    try Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = text });
}

fn generate(io: Io, path: []const u8, bytes: usize, body: *const fn (*Io.Writer, usize) Io.Writer.Error!void) !void {
    var file = try Io.Dir.cwd().createFile(io, path, .{});
    defer file.close(io);
    var buf: [64 << 10]u8 = undefined;
    var fw = file.writer(io, &buf);
    try body(&fw.interface, bytes);
    try fw.interface.flush();
}

const demo_main =
    \\const std = @import("std");
    \\
    \\const Point = struct {
    \\    x: i32,
    \\    y: i32,
    \\
    \\    fn add(self: Point, other: Point) Point {
    \\        return .{ .x = self.x + other.x, .y = self.y + other.y };
    \\    }
    \\};
    \\
    \\pub fn main() void {
    \\    const p = Point{ .x = 1, .y = 2 };
    \\    const q = p.add(p);
    \\    const count: u8 = "three";
    \\    std.debug.print("{d} {d} {d}\n", .{ q.x, count, missing });
    \\    const r = std.mem.cop;
    \\    _ = r;
    \\}
    \\
;

const c_program =
    \\#include <stdio.h>
    \\
    \\typedef struct { const char *name; int count; } Item;
    \\
    \\static int compute(int first, int second) {
    \\    int sum = first + second;
    \\    int product = first * second;
    \\    return sum + product;
    \\}
    \\
    \\int main(int argc, char **argv) {
    \\    int x = 42;
    \\    const char *s = "hello world";
    \\    double pi = 3.14159;
    \\    int done = 0;
    \\    Item item = { "widget", 3 };
    \\    int numbers[5] = { 0, 3, 6, 9, 12 };
    \\    int result = compute(x, item.count);
    \\    printf("result %d %s %f %d %d\n", result, s, pi, numbers[2], done);
    \\    return 0;
    \\}
    \\
;

const org_file =
    \\#+TITLE: Work
    \\#+TODO: TODO NEXT WAITING | DONE CANCELLED
    \\* TODO [#A] Ship the report :work:urgent:
    \\  SCHEDULED: <2026-11-05 Thu>
    \\  Checklist for the report.
    \\  - [ ] outline
    \\  - [ ] review
    \\* TODO Review the design doc
    \\  DEADLINE: <2026-11-07 Sat>
    \\* WAITING Answer from legal
    \\  SCHEDULED: <2026-11-04 Wed>
    \\* Standup
    \\  <2026-11-05 Thu 09:30 +1d>
    \\* Project :proj:
    \\** TODO Write the tests
    \\   SCHEDULED: <2026-11-06 Fri>
    \\** DONE Read the spec
    \\   CLOSED: [2026-11-01 Sun 10:00]
    \\| Task  | Owner | Days |
    \\|-------+-------+------|
    \\| Parse | Ann   |    3 |
    \\| Render| Bob   |   12 |
    \\
;

/// Creates the project the sessions open: a copy of this repository's src,
/// a few generated files, a git repository and a compiled C program.
fn prepare(arena: Allocator, io: Io, opts: *const Options) !Project {
    const cwd = Io.Dir.cwd();
    const root = try std.fmt.allocPrint(arena, "{s}/project", .{opts.fixtures});
    const stamp = try std.fmt.allocPrint(arena, "{s}/project.ready", .{opts.fixtures});
    const src = try std.fmt.allocPrint(arena, "{s}/src", .{root});
    const fresh = if (cwd.access(io, stamp, .{})) |_| false else |_| true;
    if (fresh) {
        std.debug.print("fixtures: creating the project in {s}\n", .{root});
        try cwd.deleteTree(io, root);
        try cwd.createDirPath(io, root);
        const from = try std.fmt.allocPrint(arena, "{s}/src", .{opts.repo});
        _ = try run(arena, io, null, &.{ "cp", "-R", from, src });
        const dirs = [_][]const u8{ "demo", ".vscode", "empty" };
        for (dirs) |d| try cwd.createDirPath(io, try std.fmt.allocPrint(arena, "{s}/{s}", .{ root, d }));
        try write(io, try std.fmt.allocPrint(arena, "{s}/demo/main.zig", .{root}), demo_main);
        try write(io, try std.fmt.allocPrint(arena, "{s}/prog.c", .{root}), c_program);
        try write(io, try std.fmt.allocPrint(arena, "{s}/notes.org", .{root}), org_file);
        try write(io, try std.fmt.allocPrint(arena, "{s}/README.md", .{root}), "# Leak check\n\nA project the leak runner opens.\n\n- one\n- two\n\n```zig\nconst x = 1;\n```\n");
        try write(io, try std.fmt.allocPrint(arena, "{s}/.vscode/launch.json", .{root}),
            \\{ "version": "0.2.0", "configurations": [
            \\  { "name": "prog", "type": "lldb-dap", "request": "launch", "program": "${workspaceFolder}/prog", "cwd": "${workspaceFolder}" } ] }
            \\
        );
        try generate(io, try std.fmt.allocPrint(arena, "{s}/app.log", .{root}), 3 << 20, fixtures.writeLog);
        try generate(io, try std.fmt.allocPrint(arena, "{s}/min.json", .{root}), 1 << 20, fixtures.writeJson);
        // The C program is optional: no compiler, no debug session.
        _ = run(arena, io, root, &.{ "cc", "-g", "-O0", "-o", "prog", "prog.c" }) catch {};
        _ = try run(arena, io, root, &.{ "git", "init", "-q" });
        _ = try run(arena, io, root, &.{ "git", "-c", "user.name=leaks", "-c", "user.email=leaks@example.com", "add", "-A" });
        _ = try run(arena, io, root, &.{ "git", "-c", "user.name=leaks", "-c", "user.email=leaks@example.com", "commit", "-q", "-m", "first" });
        try write(io, try std.fmt.allocPrint(arena, "{s}/README.md", .{root}), "# Leak check\n\nA project the leak runner opens, edited.\n\n- one\n- two\n- three\n");
        try write(io, stamp, "ok\n");
    }

    // The files of src, evenly spread, for the many-tabs session.
    var all: std.ArrayList([]const u8) = .empty;
    {
        var dir = try cwd.openDir(io, src, .{ .iterate = true });
        defer dir.close(io);
        var walker = try dir.walk(arena);
        defer walker.deinit();
        while (try walker.next(io)) |entry| {
            if (entry.kind != .file or !std.mem.endsWith(u8, entry.path, ".zig")) continue;
            try all.append(arena, try std.fmt.allocPrint(arena, "{s}/{s}", .{ src, entry.path }));
        }
    }
    std.mem.sort([]const u8, all.items, {}, struct {
        fn lt(_: void, a: []const u8, b: []const u8) bool {
            return std.mem.lessThan(u8, a, b);
        }
    }.lt);
    const n = @min(40, all.items.len);
    const sources = try arena.alloc([]const u8, n);
    for (sources, 0..) |*s, i| s.* = all.items[i * all.items.len / @max(n, 1)];

    return .{
        .root = root,
        .src = src,
        .rope = try std.fmt.allocPrint(arena, "{s}/model/text/rope.zig", .{src}),
        .editor = try std.fmt.allocPrint(arena, "{s}/app/editor/editor.zig", .{src}),
        .log = try std.fmt.allocPrint(arena, "{s}/app.log", .{root}),
        .json = try std.fmt.allocPrint(arena, "{s}/min.json", .{root}),
        .org = try std.fmt.allocPrint(arena, "{s}/notes.org", .{root}),
        .readme = try std.fmt.allocPrint(arena, "{s}/README.md", .{root}),
        .cprog = try std.fmt.allocPrint(arena, "{s}/prog.c", .{root}),
        .demo_main = try std.fmt.allocPrint(arena, "{s}/demo/main.zig", .{root}),
        .demo_dir = try std.fmt.allocPrint(arena, "{s}/demo", .{root}),
        .empty_dir = try std.fmt.allocPrint(arena, "{s}/empty", .{root}),
        .sources = sources,
    };
}

// ---- Running -----------------------------------------------------------------

const Outcome = enum { passed, failed, skipped };

fn onPath(arena: Allocator, io: Io, env: *const std.process.Environ.Map, name: []const u8) bool {
    const path = env.get("PATH") orelse return false;
    var it = std.mem.tokenizeScalar(u8, path, ':');
    while (it.next()) |dir| {
        const full = std.fmt.allocPrint(arena, "{s}/{s}", .{ dir, name }) catch return false;
        if (Io.Dir.cwd().access(io, full, .{})) |_| return true else |_| {}
    }
    // Xcode's command line tools answer for lldb-dap and cc.
    const r = std.process.run(arena, io, .{ .argv = &.{ "xcrun", "-f", name } }) catch return false;
    return r.term == .exited and r.term.exited == 0;
}

fn limaRunning(arena: Allocator, io: Io) bool {
    const r = std.process.run(arena, io, .{ .argv = &.{ "limactl", "list", "--format", "{{.Name}} {{.Status}}" } }) catch return false;
    return std.mem.indexOf(u8, r.stdout, "jiffy-ubuntu Running") != null;
}

/// macOS's `leaks` reads a process's memory only if the process allows it
/// (com.apple.security.get-task-allow): a copy of the editor, ad-hoc signed
/// with that entitlement, next to the `share` folder its fonts are found in.
fn signedCopy(arena: Allocator, io: Io, opts: *const Options, bin: []const u8) ![]const u8 {
    const cwd = Io.Dir.cwd();
    const dir = try std.fmt.allocPrint(arena, "{s}/signed", .{opts.fixtures});
    try cwd.deleteTree(io, dir);
    try cwd.createDirPath(io, try std.fmt.allocPrint(arena, "{s}/bin", .{dir}));
    const copy = try std.fmt.allocPrint(arena, "{s}/bin/cs", .{dir});
    _ = try run(arena, io, null, &.{ "cp", bin, copy });
    const share = try std.fmt.allocPrint(arena, "{s}/../share", .{std.fs.path.dirname(bin) orelse "."});
    _ = try run(arena, io, null, &.{ "ln", "-s", share, try std.fmt.allocPrint(arena, "{s}/share", .{dir}) });
    const plist = try std.fmt.allocPrint(arena, "{s}/get-task-allow.plist", .{opts.fixtures});
    try write(io, plist,
        \\<?xml version="1.0" encoding="UTF-8"?>
        \\<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        \\<plist version="1.0"><dict><key>com.apple.security.get-task-allow</key><true/></dict></plist>
        \\
    );
    _ = try run(arena, io, null, &.{ "codesign", "-s", "-", "-f", "--entitlements", plist, copy });
    return copy;
}

/// How a session ended, judged from what it printed and how it exited.
const Verdict = struct {
    ok: bool,
    leaks: usize = 0,
    why: []const u8 = "",
};

fn judge(stderr: []const u8, stdout: []const u8, term: std.process.Child.Term, tool: Tool) Verdict {
    switch (tool) {
        .allocator => {
            var leaks: usize = 0;
            var it = std.mem.splitScalar(u8, stderr, '\n');
            while (it.next()) |line| {
                if (std.mem.indexOf(u8, line, "leaked ") != null and std.mem.indexOf(u8, line, "allocated at") != null) leaks += 1;
            }
            if (leaks > 0) return .{ .ok = false, .leaks = leaks, .why = "leaked memory" };
            if (std.mem.indexOf(u8, stderr, "panic:") != null) return .{ .ok = false, .why = "panic" };
            switch (term) {
                .exited => |code| if (code != 0) return .{ .ok = false, .why = "exited with an error" },
                .signal => return .{ .ok = false, .why = "killed by a signal" },
                else => return .{ .ok = false, .why = "stopped abnormally" },
            }
            return .{ .ok = true };
        },
        .leaks => {
            // The tool can only look into a process that allows it: a binary
            // without get-task-allow reports "0 leaks" about nothing.
            if (std.mem.indexOf(u8, stdout, "is not debuggable") != null) return .{ .ok = false, .why = "leaks cannot read the process (it needs the get-task-allow entitlement)" };
            // "Process 123: 2 leaks for 160 total leaked bytes." (or "1 leak for ...").
            const marker = std.mem.indexOf(u8, stdout, " leaks for ") orelse std.mem.indexOf(u8, stdout, " leak for ") orelse return .{ .ok = false, .why = "no leaks report" };
            var start = marker;
            while (start > 0 and std.ascii.isDigit(stdout[start - 1])) start -= 1;
            const n = std.fmt.parseInt(usize, stdout[start..marker], 10) catch return .{ .ok = false, .why = "unreadable leaks report" };
            if (n > 0) return .{ .ok = false, .leaks = n, .why = "leaked memory" };
            return .{ .ok = true };
        },
    }
}

fn printHead(text: []const u8, needle: []const u8, lines: usize) void {
    const at = std.mem.indexOf(u8, text, needle) orelse return;
    var begin = at;
    while (begin > 0 and text[begin - 1] != '\n') begin -= 1;
    var it = std.mem.splitScalar(u8, text[begin..], '\n');
    var i: usize = 0;
    while (it.next()) |line| : (i += 1) {
        if (i >= lines) break;
        std.debug.print("      {s}\n", .{line});
    }
}

pub fn main(init: std.process.Init) !u8 {
    const gpa = init.gpa;
    const io = init.io;
    var arena_state: std.heap.ArenaAllocator = .init(gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    var opts: Options = .{ .home = init.environ_map.get("HOME") orelse "/" };
    var args = try init.minimal.args.iterateAllocator(gpa);
    defer args.deinit();
    _ = args.skip();
    while (args.next()) |arg| {
        const eql = std.mem.eql;
        if (eql(u8, arg, "-h") or eql(u8, arg, "--help")) {
            std.debug.print("{s}", .{usage});
            return 0;
        } else if (eql(u8, arg, "--list")) {
            opts.list = true;
        } else if (eql(u8, arg, "--verbose")) {
            opts.verbose = true;
        } else if (eql(u8, arg, "--unchecked-build")) {
            opts.unchecked_build = true;
        } else if (eql(u8, arg, "--tool")) {
            const v = args.next() orelse return error.MissingArgument;
            opts.tool = if (eql(u8, v, "leaks")) .leaks else if (eql(u8, v, "allocator")) .allocator else {
                std.debug.print("--tool is allocator or leaks\n", .{});
                return 2;
            };
        } else if (eql(u8, arg, "--timeout")) {
            opts.timeout_s = try std.fmt.parseInt(i64, args.next() orelse return error.MissingArgument, 10);
        } else for ([_]struct { []const u8, *[]const u8 }{
            .{ "--bin", &opts.bin },
            .{ "--fixtures", &opts.fixtures },
            .{ "--repo", &opts.repo },
        }) |o| {
            if (eql(u8, arg, o[0])) {
                o[1].* = try arena.dupe(u8, args.next() orelse return error.MissingArgument);
                break;
            }
        } else if (eql(u8, arg, "--only")) {
            opts.only = try arena.dupe(u8, args.next() orelse return error.MissingArgument);
        } else if (eql(u8, arg, "--shots")) {
            opts.shots = try arena.dupe(u8, args.next() orelse return error.MissingArgument);
        } else if (eql(u8, arg, "--grammars")) {
            opts.grammars = try arena.dupe(u8, args.next() orelse return error.MissingArgument);
        } else {
            std.debug.print("unknown option: {s}\n\n{s}", .{ arg, usage });
            return 2;
        }
    }

    if (opts.list) {
        for (std.enums.values(Scenario)) |s| std.debug.print("{s:<10} {s}\n", .{ @tagName(s), s.description() });
        return 0;
    }
    if (opts.only) |only| {
        var it = std.mem.tokenizeScalar(u8, only, ',');
        while (it.next()) |n| if (std.meta.stringToEnum(Scenario, n) == null) {
            std.debug.print("unknown scenario {s}; --list names them\n", .{n});
            return 2;
        };
    }
    if (opts.unchecked_build and opts.tool == .allocator) {
        std.debug.print("this build has no leak checking: build with -Doptimize=Debug or ReleaseSafe, or use --tool leaks\n", .{});
        return 2;
    }

    const cwd = Io.Dir.cwd();
    const bin = cwd.realPathFileAlloc(io, opts.bin, arena) catch |err| {
        std.debug.print("--bin {s}: {t}\n", .{ opts.bin, err });
        return 2;
    };
    try cwd.createDirPath(io, opts.fixtures);
    opts.fixtures = try cwd.realPathFileAlloc(io, opts.fixtures, arena);
    if (opts.shots) |dir| {
        try cwd.createDirPath(io, dir);
        opts.shots = try cwd.realPathFileAlloc(io, dir, arena);
    }
    opts.repo = try cwd.realPathFileAlloc(io, opts.repo, arena);
    const project = try prepare(arena, io, &opts);
    const run_bin = if (opts.tool == .leaks) try signedCopy(arena, io, &opts, bin) else bin;

    var base_env = try init.environ_map.clone(arena);
    try base_env.put("SHELL", "/bin/sh");
    _ = base_env.swapRemove("IN_NIX_SHELL");
    if (opts.grammars) |g| try base_env.put("CS_ZIG_GRAMMARS", try cwd.realPathFileAlloc(io, g, arena));

    var passed: usize = 0;
    var failed: usize = 0;
    var skipped: usize = 0;
    const timer_start = Io.Clock.awake.now(io);
    std.debug.print("binary   {s}\ntool     {s}\n", .{ bin, if (opts.tool == .leaks) "leaks --atExit" else "the allocator's leak checking" });
    for (std.enums.values(Scenario)) |s| {
        const name = @tagName(s);
        if (opts.only) |only| {
            var it = std.mem.tokenizeScalar(u8, only, ',');
            while (it.next()) |n| {
                if (std.mem.eql(u8, n, name)) break;
            } else continue;
        }
        var missing: ?[]const u8 = null;
        for (s.needs()) |prog| if (!onPath(arena, io, &base_env, prog)) {
            missing = prog;
            break;
        };
        if (missing == null and s == .remote and !limaRunning(arena, io)) missing = "a running jiffy-ubuntu Lima VM";
        if (missing) |m| {
            std.debug.print("{s:<10} SKIP  needs {s}\n", .{ name, m });
            skipped += 1;
            continue;
        }

        // A home of its own for every scenario.
        const home = try std.fmt.allocPrint(arena, "{s}/run/{s}", .{ opts.fixtures, name });
        try cwd.deleteTree(io, home);
        for ([_][]const u8{ "home", "config", "state", "data" }) |sub| try cwd.createDirPath(io, try std.fmt.allocPrint(arena, "{s}/{s}", .{ home, sub }));
        var env = try base_env.clone(arena);
        try env.put("HOME", try std.fmt.allocPrint(arena, "{s}/home", .{home}));
        try env.put("XDG_CONFIG_HOME", try std.fmt.allocPrint(arena, "{s}/config", .{home}));
        try env.put("XDG_STATE_HOME", try std.fmt.allocPrint(arena, "{s}/state", .{home}));
        try env.put("CS_ZIG_HOME", try std.fmt.allocPrint(arena, "{s}/data", .{home}));
        _ = env.swapRemove("XDG_RUNTIME_DIR");

        const runs = try scenarioRuns(arena, s, &project, &opts);
        const started = Io.Clock.awake.now(io);
        var outcome: Outcome = .passed;
        var total_leaks: usize = 0;
        for (runs, 0..) |r, ri| {
            var renv = try env.clone(arena);
            for (r.env) |kv| try renv.put(kv[0], kv[1]);
            var argv: std.ArrayList([]const u8) = .empty;
            if (opts.tool == .leaks) try argv.appendSlice(arena, &.{ "leaks", "--atExit", "--" });
            try argv.append(arena, run_bin);
            try argv.appendSlice(arena, &.{ "--headless", "1280x800", "--standalone" });
            if (s != .lsp and s != .remote) try argv.append(arena, "--no-lsp");
            try argv.appendSlice(arena, r.argv);
            if (opts.shots) |dir| if (ri + 1 == runs.len) try argv.appendSlice(arena, &.{ "--screenshot", try std.fmt.allocPrint(arena, "{s}/{s}.png", .{ dir, name }) });

            const result = std.process.run(gpa, io, .{
                .argv = argv.items,
                .cwd = .{ .path = project.root },
                .environ_map = &renv,
                .timeout = .{ .duration = .{ .raw = .fromSeconds(opts.timeout_s), .clock = .awake } },
            }) catch |err| {
                std.debug.print("{s:<10} FAIL  run {d}: {t}\n", .{ name, ri + 1, err });
                outcome = .failed;
                break;
            };
            defer gpa.free(result.stdout);
            defer gpa.free(result.stderr);
            const v = judge(result.stderr, result.stdout, result.term, opts.tool);
            total_leaks += v.leaks;
            if (opts.verbose) std.debug.print("{s}", .{result.stderr});
            if (!v.ok) {
                outcome = .failed;
                std.debug.print("{s:<10} FAIL  run {d}: {s}", .{ name, ri + 1, v.why });
                if (v.leaks > 0) std.debug.print(" ({d} reports)", .{v.leaks});
                std.debug.print("\n", .{});
                switch (opts.tool) {
                    .allocator => {
                        if (v.leaks > 0) printHead(result.stderr, "leaked ", 26) else printHead(result.stderr, "panic:", 24);
                        if (v.leaks == 0 and std.mem.indexOf(u8, result.stderr, "panic:") == null) {
                            const tail = result.stderr[result.stderr.len -| 600..];
                            std.debug.print("      ...{s}\n", .{tail});
                        }
                    },
                    .leaks => if (std.mem.indexOf(u8, result.stdout, "STACK OF") != null) printHead(result.stdout, "STACK OF", 30) else printHead(result.stdout, "is not debuggable", 4),
                }
                break;
            }
        }
        const secs = @as(f64, @floatFromInt(started.untilNow(io, .awake).toMilliseconds())) / 1000;
        if (outcome == .passed) {
            passed += 1;
            std.debug.print("{s:<10} ok    {d:.1} s  {s}\n", .{ name, secs, s.description() });
        } else failed += 1;
    }
    const total = @as(f64, @floatFromInt(timer_start.untilNow(io, .awake).toMilliseconds())) / 1000;
    std.debug.print("\n{d} clean, {d} failed, {d} skipped in {d:.0} s\n", .{ passed, failed, skipped, total });
    return if (failed > 0) 1 else 0;
}
