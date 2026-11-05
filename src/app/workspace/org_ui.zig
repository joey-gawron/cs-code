//! Workspace glue for org-mode: key routing, date prompts, capture, the
//! agenda tab.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const platform = @import("../../framework/platform/platform.zig");
const languages = @import("../../model/syntax/languages.zig");
const org = @import("../org/org.zig");
const agenda_mod = @import("../../model/org/agenda.zig");
const date = @import("../../model/org/date.zig");
const clock = @import("../../model/org/clock.zig");
const refile_mod = @import("../../model/org/refile.zig");
const parser = @import("../../model/org/parser.zig");
const export_mod = @import("../../model/org/export.zig");
const capture_mod = @import("../../model/org/capture.zig");
const file_cmd = @import("file_cmd.zig");
const Action = @import("../settings/keymap.zig").Action;

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Agenda = agenda_mod.Agenda;
const Io = std.Io;

pub const PromptKind = enum(u32) { schedule = 100, deadline, timestamp, timestamp_inactive, capture, set_tags, agenda_search, agenda_tags, agenda_filter_tags, agenda_filter_text };

/// Notes, like Emacs's org-directory: the agenda reads it and SPC n f /
/// SPC n s look in it.
pub const notes_folder = "~/org";

/// Org keys for the active org editor. Returns true if consumed.
pub fn handleKey(ws: *Workspace, ed: *Editor, k: platform.KeyEvent) bool {
    switch (org.handleKey(&ws.org_keys, ed, k)) {
        .pass => return false,
        .done => {
            ed.afterCommand(false);
            return true;
        },
        .command => |cmd| {
            runCommand(ws, cmd);
            return true;
        },
        .copy_then_pass => {
            ed.copy(false);
            return false;
        },
    }
}

/// C-c with no second key within a second just copies.
pub fn tick(ws: *Workspace) void {
    const since = ws.org_keys.prefix_since orelse return;
    if (platform.nowMs() - since < 1000) return;
    ws.org_keys.prefix_since = null;
    if (ws.activeEditor()) |ed| ed.copy(false);
}

pub fn runCommand(ws: *Workspace, cmd: org.Command) void {
    switch (cmd) {
        .schedule => promptPlanning(ws, .schedule),
        .deadline => promptPlanning(ws, .deadline),
        .timestamp => ws.openPrompt("Timestamp (fri, +3d, nov 20 14:00, 2pm):", @intFromEnum(PromptKind.timestamp), "today"),
        .timestamp_inactive => ws.openPrompt("Inactive timestamp (fri, +3d, nov 20 14:00, 2pm):", @intFromEnum(PromptKind.timestamp_inactive), "today"),
        .open_link => openLink(ws),
        .agenda => openAgenda(ws, .week),
        .todo_list => openAgenda(ws, .todo),
        .set_tags => promptTags(ws),
        .capture => startCapture(ws),
        .export_html => perform(ws, .org_export_html),
        .refile, .archive, .edit_src, .clock_in, .clock_out, .clock_report => perform(ws, switch (cmd) {
            .refile => .org_refile,
            .archive => .org_archive,
            .edit_src => .org_edit_src,
            .clock_in => .org_clock_in,
            .clock_out => .org_clock_out,
            else => .org_clock_report,
        }),
        // The rest are handled inside org.handleKey.
        else => {},
    }
}

pub fn confirmPrompt(ws: *Workspace, tag: u32, input: []const u8) bool {
    if (tag < @intFromEnum(PromptKind.schedule) or tag > @intFromEnum(PromptKind.agenda_filter_text)) return false;
    const kind: PromptKind = @enumFromInt(tag);
    switch (kind) {
        .capture => {
            capture(ws, input);
            return true;
        },
        .agenda_search => {
            openAgendaQuery(ws, .search, std.mem.trim(u8, input, " "), "");
            return true;
        },
        .agenda_tags => {
            openAgendaQuery(ws, .tags, std.mem.trim(u8, input, " "), "");
            return true;
        },
        .agenda_filter_tags, .agenda_filter_text => {
            setAgendaFilter(ws, kind, std.mem.trim(u8, input, " "));
            return true;
        },
        else => {},
    }
    defer returnToAgenda(ws);
    const ed = ws.activeEditor() orelse return true;
    if (kind == .set_tags) {
        if (org.setTags(ed, input)) ed.afterCommand(false);
        return true;
    }
    // An emptied Schedule / Deadline prompt removes the date it had.
    if ((kind == .schedule or kind == .deadline) and std.mem.trim(u8, input, " ").len == 0) {
        if (org.removePlanning(ed, planningKeyword(kind))) ed.afterCommand(false);
        return true;
    }
    const d = date.parseInput(input, date.Date.today()) orelse {
        ws.setStatus("Can't understand date: {s}", .{input});
        return true;
    };
    switch (kind) {
        .schedule, .deadline => org.setPlanning(ed, planningKeyword(kind), d),
        .timestamp => org.insertTimestamp(ed, d, true),
        .timestamp_inactive => org.insertTimestamp(ed, d, false),
        else => unreachable,
    }
    return true;
}

fn planningKeyword(kind: PromptKind) []const u8 {
    return if (kind == .schedule) "SCHEDULED:" else "DEADLINE:";
}

/// Schedule / Deadline: org's date prompt, starting from the date the entry has.
fn promptPlanning(ws: *Workspace, kind: PromptKind) void {
    const label = if (kind == .schedule)
        "Schedule (fri, +3d, nov 20, 14:00, fri 10:00 +1w; empty removes):"
    else
        "Deadline (fri, +3d, nov 20, 14:00, fri -3d; empty removes):";
    var buf: [96]u8 = undefined;
    const current = if (ws.activeEditor()) |ed| org.planningValue(ed, planningKeyword(kind), &buf) orelse "" else "";
    ws.openPrompt(label, @intFromEnum(kind), current);
}

/// C-c C-q: the heading's tags, edited in a prompt that lists the file's.
fn promptTags(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    if (!org.isOrg(ed)) return;
    var buf: [256]u8 = undefined;
    const current = org.headingTags(ed, &buf) orelse return ws.setStatus("Not under a heading", .{});
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const known = org.knownTags(ed, arena.allocator());
    const joined = std.mem.join(arena.allocator(), " ", known) catch "";
    var label_buf: [512]u8 = undefined;
    const label = if (known.len == 0)
        "Tags (space separated):"
    else
        std.fmt.bufPrint(&label_buf, "Tags (in this file: {s}):", .{joined[0..@min(joined.len, 400)]}) catch "Tags:";
    ws.openPrompt(label, @intFromEnum(PromptKind.set_tags), current);
}

// ---- Clocking -----------------------------------------------------------------

/// C-c C-x C-i: clocks in on the heading at the caret, clocking out of
/// the running one first (in whatever file it is).
fn clockIn(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    if (!org.isOrg(ed)) return ws.setStatus("Clocking works in org files", .{});
    var tbuf: [120]u8 = undefined;
    const h = org.headingAtCaret(ed, &tbuf) orelse return ws.setStatus("Not under a heading", .{});
    const now = clock.nowMinutes();
    if (ws.org_keys.clock != null) clockOut(ws, false);
    org.clockIn(ed, h.row, now);
    ed.afterCommand(false);
    const path = ed.buffer.path orelse "";
    ws.org_keys.clock = .{ .start = now };
    const c = &ws.org_keys.clock.?;
    c.path_len = @min(path.len, c.path_buf.len);
    @memcpy(c.path_buf[0..c.path_len], path[0..c.path_len]);
    c.title_len = @min(h.title.len, c.title_buf.len);
    @memcpy(c.title_buf[0..c.title_len], h.title[0..c.title_len]);
    ws.setStatus("Clocked in: {s}", .{h.title});
}

/// C-c C-x C-o: closes the running clock (or one found in this file).
fn clockOut(ws: *Workspace, say: bool) void {
    const now = clock.nowMinutes();
    var ed: ?*Editor = null;
    if (ws.org_keys.clock) |*c| {
        if (c.path_len > 0) ed = ws.openFile(c.path(), false) catch null;
    }
    if (ed == null) ed = ws.activeEditor();
    const e = ed orelse return;
    ws.org_keys.clock = null;
    const minutes = org.clockOut(e, now) orelse {
        if (say) ws.setStatus("No clock is running", .{});
        return;
    };
    e.afterCommand(false);
    var dbuf: [16]u8 = undefined;
    if (say) ws.setStatus("Clocked out after {s}", .{clock.formatDuration(&dbuf, minutes)});
}

fn clockGoto(ws: *Workspace) void {
    const c = ws.org_keys.clock orelse return ws.setStatus("No clock is running", .{});
    const ed = ws.openFile(c.path(), false) catch return;
    var tbuf: [120]u8 = undefined;
    const running = org.runningClock(ed, &tbuf) orelse return;
    ed.setSingleSelection(.caret(ed.buffer.lineStart(running.row)));
    ed.afterCommand(true);
}

/// "⏱ Heading 0:42" while a clock runs.
pub fn clockStatus(ws: *Workspace, buf: []u8) ?[]const u8 {
    const c = ws.org_keys.clock orelse return null;
    var dbuf: [16]u8 = undefined;
    const title = c.title();
    return std.fmt.bufPrint(buf, "⏱ {s} {s}", .{ title[0..@min(title.len, 40)], clock.formatDuration(&dbuf, clock.nowMinutes() - c.start) }) catch null;
}

/// The next minute, while a clock runs (its time in the status bar).
pub fn nextTimer(ws: *Workspace) ?i64 {
    if (ws.org_keys.clock == null) return null;
    return 60_000 - @mod(platform.nowMs(), 60_000) + 50;
}

/// Org actions with more than one step (see org_ui's sections below).
pub fn perform(ws: *Workspace, action: Action) void {
    switch (action) {
        .org_agenda_dispatch => dispatch(ws),
        .org_agenda_search => ws.openPrompt("Search headings for (words):", @intFromEnum(PromptKind.agenda_search), ""),
        .org_agenda_tags => ws.openPrompt("Match tags (work+urgent-someday|home):", @intFromEnum(PromptKind.agenda_tags), ""),
        .org_set_tags => promptTags(ws),
        .org_clock_in => clockIn(ws),
        .org_refile => refile(ws),
        .org_edit_src => editSrc(ws),
        .org_export_html, .org_export_markdown => exportFile(ws, if (action == .org_export_html) .html else .markdown),
        .org_archive => archive(ws),
        .org_clock_out => clockOut(ws, true),
        .org_clock_goto => clockGoto(ws),
        .org_clock_report => if (ws.activeEditor()) |ed| {
            org.clockReport(ed, clock.nowMinutes());
            ed.afterCommand(false);
        },
        .org_table_align, .org_table_insert_row, .org_table_delete_row, .org_table_insert_column, .org_table_delete_column, .org_table_insert_hline => {
            const ed = ws.activeEditor() orelse return;
            const op: org.TableOp = switch (action) {
                .org_table_align => .align_table,
                .org_table_insert_row => .insert_row,
                .org_table_delete_row => .delete_row,
                .org_table_insert_column => .insert_column,
                .org_table_delete_column => .delete_column,
                else => .insert_hline,
            };
            if (org.tableCommand(ed, op)) ed.afterCommand(false) else ws.setStatus("Not in a table", .{});
        },
        else => {},
    }
}

// ---- Capture ------------------------------------------------------------------

/// A capture in progress: the template and where it started (owned).
pub const Capture = struct {
    arena: std.heap.ArenaAllocator,
    templates: []const capture_mod.Template,
    chosen: usize = 0,
    link: []const u8 = "",
    selection: []const u8 = "",
    file_name: []const u8 = "",
};

pub const capture_tag: u32 = 0xCA97;

pub fn clearCapture(ws: *Workspace) void {
    if (ws.org_capture) |*c| c.arena.deinit();
    ws.org_capture = null;
}

/// Org: Capture… picks a template (when settings.json has several), asks
/// for the text and files it without leaving the current view.
pub fn startCapture(ws: *Workspace) void {
    clearCapture(ws);
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    const alloc = arena.allocator();
    const settings = ws.env.settings;
    var templates = capture_mod.parseTemplates(alloc, settings.org_capture_templates_json);
    if (templates.len == 0) {
        // cs-code's four: tasks, notes and events go where org_capture_file /
        // org_capture_heading say, the journal into journal.org's date tree.
        const file = settings.org_capture_file;
        const heading = settings.org_capture_heading;
        const four = alloc.alloc(capture_mod.Template, 4) catch return arena.deinit();
        four[0] = .{ .key = "t", .description = "Task", .file = file, .heading = heading, .template = "* TODO %^{Task}\n  %U" };
        four[1] = .{ .key = "n", .description = "Note", .file = file, .heading = heading, .template = "* %^{Title}\n  %U" };
        four[2] = .{ .key = "e", .description = "Event", .file = file, .heading = heading, .template = "* %^{What}\n  %^T" };
        four[3] = .{ .key = "j", .description = "Journal", .file = "~/org/journal.org", .template = "* %<%H:%M> %^{Title}", .datetree = true };
        templates = four;
    }
    var c: Capture = .{ .arena = arena, .templates = templates };
    // %a, %i and %f: where capture started.
    if (ws.activeEditor()) |ed| {
        const sel = ed.primarySelection();
        if (!sel.isEmpty()) c.selection = ed.buffer.textRange(alloc, sel.start(), sel.end()) catch "";
        if (ed.buffer.path) |p| {
            c.file_name = std.fs.path.basename(p);
            c.link = std.fmt.allocPrint(alloc, "file:{s}::{d}", .{ p, ed.buffer.offsetToPoint(sel.head).row + 1 }) catch "";
        }
    }
    ws.org_capture = c;
    if (templates.len == 1) return promptCapture(ws);
    var titles: std.ArrayList([]const u8) = .empty;
    defer titles.deinit(ws.gpa);
    for (templates) |t| titles.append(ws.gpa, std.fmt.allocPrint(alloc, "[{s}] {s}", .{ t.key, t.description }) catch return) catch return;
    ws.openList(.list, ws.gpa.dupe(u8, "Capture template:") catch "Save changes?", capture_tag, titles.items);
}

fn promptCapture(ws: *Workspace) void {
    const c = ws.org_capture orelse return;
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Capture ({s}):", .{c.templates[c.chosen].description}) catch "Capture:";
    ws.openPrompt(label, @intFromEnum(PromptKind.capture), "");
}

fn capture(ws: *Workspace, input: []const u8) void {
    defer clearCapture(ws);
    const text = std.mem.trim(u8, input, " \t");
    if (text.len == 0) return;
    const c = ws.org_capture orelse return;
    const t = c.templates[c.chosen];
    const path = capturePath(ws.gpa, t.file) catch return;
    defer ws.gpa.free(path);
    const now = date.now();
    const entry = capture_mod.expand(ws.gpa, t.template, .{
        .input = text,
        .now = .{ .date = now.date, .time = now.time },
        .link = c.link,
        .selection = c.selection,
        .file_name = c.file_name,
        .user = if (std.c.getenv("USER")) |u| std.mem.span(u) else "",
    }) catch return;
    defer ws.gpa.free(entry);
    const Ctx = struct {
        template: capture_mod.Template,
        entry: []const u8,
        today: date.Date,
        fn compute(x: @This(), gpa: Allocator, file_text: []const u8) !Insertion {
            const ins = try capture_mod.place(gpa, file_text, x.template, x.entry, x.today);
            return .{ .offset = ins.offset, .text = ins.text, .end = ins.end };
        }
    };
    insertInFile(ws, path, Ctx{ .template = t, .entry = entry, .today = now.date }) catch |err| {
        ws.setStatus("Unable to capture to {s}: {t}", .{ path, err });
        return;
    };
    ws.setStatus("Captured \"{s}\" to {s}", .{ text, path });
    refreshAgendas(ws);
}

/// SPC n N: the capture file (an empty buffer that saves there if it
/// doesn't exist yet).
pub fn gotoCaptureFile(ws: *Workspace) void {
    const path = capturePath(ws.gpa, ws.env.settings.org_capture_file) catch return;
    defer ws.gpa.free(path);
    _ = ws.openFile(path, false) catch |err| ws.setStatus("Unable to open {s}: {t}", .{ path, err });
}

/// The capture file with "~/" expanded; other relative paths are in the home folder.
fn capturePath(gpa: Allocator, setting: []const u8) ![]u8 {
    if (std.fs.path.isAbsolute(setting)) return gpa.dupe(u8, setting);
    const home = std.mem.span(std.c.getenv("HOME") orelse return error.NoHome);
    const rel = if (std.mem.startsWith(u8, setting, "~/")) setting[2..] else setting;
    return std.fs.path.join(gpa, &.{ home, rel });
}

const Insertion = struct { offset: usize, text: []u8, end: ?usize = null };

/// Inserts what `ctx.compute(gpa, text)` says into the org file `path`:
/// in its open buffer if there is one (saved if it had no unsaved changes),
/// else on disk (created if missing).
fn insertInFile(ws: *Workspace, path: []const u8, ctx: anytype) !void {
    for (ws.buffers.items) |b| {
        const p = b.path orelse continue;
        if (!std.mem.eql(u8, p, path)) continue;
        const text = try b.rope.toOwnedSlice(ws.gpa);
        defer ws.gpa.free(text);
        const ins = try ctx.compute(ws.gpa, text);
        defer ws.gpa.free(ins.text);
        // An editor on the buffer to edit it through (undo, other views).
        for (ws.tabs.items) |t| if (t.editor.buffer == b) {
            const was_clean = !b.isDirty();
            var edits = [_]editor_mod.Edit{.{ .start = ins.offset, .end = ins.end orelse ins.offset, .text = ins.text }};
            t.editor.commitTransform(&edits, .other);
            if (was_clean) try b.save(ws.io);
            return;
        };
    }
    const cwd = Io.Dir.cwd();
    const text = cwd.readFileAlloc(ws.io, path, ws.gpa, .limited(16 << 20)) catch |err| switch (err) {
        error.FileNotFound => try ws.gpa.dupe(u8, ""),
        else => return err,
    };
    defer ws.gpa.free(text);
    const ins = try ctx.compute(ws.gpa, text);
    defer ws.gpa.free(ins.text);
    const out = try std.mem.concat(ws.gpa, u8, &.{ text[0..ins.offset], ins.text, text[ins.end orelse ins.offset ..] });
    defer ws.gpa.free(out);
    if (std.fs.path.dirname(path)) |dir| try cwd.createDirPath(ws.io, dir);
    try cwd.writeFile(ws.io, .{ .sub_path = path, .data = out });
}

// ---- Refile and archive ---------------------------------------------------------

pub const refile_tag: u32 = 0x0F11E;

/// The refile targets listed in the quick panel, by item value.
pub const RefileTargets = struct {
    arena: std.heap.ArenaAllocator,
    /// The file of each target, and its outline path ("" = the top level).
    items: []const struct { path: []const u8, olpath: []const u8 },
};

pub fn clearRefile(ws: *Workspace) void {
    if (ws.org_refile) |*r| r.arena.deinit();
    ws.org_refile = null;
}

/// The subtree at the caret: the buffer's text (in `alloc`), its range,
/// and the heading's outline path.
fn subtreeAtCaret(ed: *Editor, alloc: Allocator) ?struct { text: []const u8, range: refile_mod.Range, olpath: []const u8 } {
    var tbuf: [120]u8 = undefined;
    const h = org.headingAtCaret(ed, &tbuf) orelse return null;
    const text = ed.buffer.rope.toOwnedSlice(alloc) catch return null;
    const at = ed.buffer.lineStart(h.row);
    const range = refile_mod.subtree(text, at) orelse return null;
    var olpath: []const u8 = "";
    for (refile_mod.targets(alloc, text) catch &.{}) |t| if (t.offset == at) {
        olpath = t.path;
    };
    return .{ .text = text, .range = range, .olpath = olpath };
}

/// C-c C-w: lists every heading of the agenda files (and each file's top
/// level) to move the subtree at the caret under.
fn refile(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    if (!org.isOrg(ed)) return;
    const src_path = ed.buffer.path orelse return ws.setStatus("Save the file before refiling from it", .{});
    var scratch = std.heap.ArenaAllocator.init(ws.gpa);
    defer scratch.deinit();
    const sub = subtreeAtCaret(ed, scratch.allocator()) orelse return ws.setStatus("Not under a heading", .{});
    clearRefile(ws);
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    const alloc = arena.allocator();
    const sources = collectSources(ws, alloc) catch return arena.deinit();
    var items: std.ArrayList(@typeInfo(@FieldType(RefileTargets, "items")).pointer.child) = .empty;
    var titles: std.ArrayList([]const u8) = .empty;
    defer titles.deinit(ws.gpa);
    for (sources) |src| {
        const name = std.fs.path.basename(src.path);
        const same = std.mem.eql(u8, src.path, src_path);
        items.append(alloc, .{ .path = src.path, .olpath = "" }) catch break;
        titles.append(ws.gpa, std.fmt.allocPrint(alloc, "{s} (top level)", .{name}) catch break) catch break;
        for (refile_mod.targets(alloc, if (same) sub.text else src.text) catch continue) |t| {
            // Not into itself.
            if (same and t.offset >= sub.range.start and t.offset < sub.range.end) continue;
            items.append(alloc, .{ .path = src.path, .olpath = t.path }) catch break;
            titles.append(ws.gpa, std.fmt.allocPrint(alloc, "{s} › {s}", .{ name, t.path }) catch break) catch break;
        }
    }
    ws.org_refile = .{ .arena = arena, .items = items.items };
    const label = std.fmt.allocPrint(ws.gpa, "Refile \"{s}\" to:", .{sub.olpath}) catch "Save changes?";
    ws.openList(.list, label, refile_tag, titles.items);
}

pub fn confirmList(ws: *Workspace, tag: u32, value: ?usize) bool {
    if (tag == dispatch_tag) {
        runDispatch(ws, value orelse return true);
        return true;
    }
    if (tag == capture_tag) {
        if (ws.org_capture) |*c| {
            c.chosen = @min(value orelse 0, c.templates.len - 1);
            promptCapture(ws);
        }
        return true;
    }
    if (tag != refile_tag) return false;
    defer clearRefile(ws);
    defer returnToAgenda(ws);
    const targets = ws.org_refile orelse return true;
    const v = value orelse return true;
    if (v >= targets.items.len) return true;
    const t = targets.items[v];
    doRefile(ws, t.path, t.olpath) catch |err| ws.setStatus("Refile failed: {t}", .{err});
    return true;
}

fn doRefile(ws: *Workspace, path: []const u8, olpath: []const u8) !void {
    const ed = ws.activeEditor() orelse return;
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const sub = subtreeAtCaret(ed, alloc) orelse return;
    const moved = sub.text[sub.range.start..sub.range.end];
    if (std.mem.eql(u8, path, ed.buffer.path orelse "")) {
        // Within the file: the text without the subtree, then it inserted.
        const rest = try std.mem.concat(alloc, u8, &.{ sub.text[0..sub.range.start], sub.text[sub.range.end..] });
        const under = if (olpath.len > 0) (try refile_mod.find(alloc, rest, olpath)) orelse return error.TargetGone else null;
        const ins = try refile_mod.insertion(alloc, rest, under, moved);
        const out = try std.mem.concat(alloc, u8, &.{ rest[0..ins.offset], ins.text, rest[ins.offset..] });
        const caret = ins.offset + @intFromBool(ins.text.len > 0 and ins.text[0] == '\n');
        var reps = [_]Editor.Replacement{.{ .start = 0, .end = ed.buffer.len(), .text = out, .sel_anchor = caret, .sel_head = caret }};
        ed.applyReplacements(&reps, .other);
    } else {
        const Ctx = struct {
            olpath: []const u8,
            sub: []const u8,
            fn compute(c: @This(), gpa: Allocator, text: []const u8) !Insertion {
                var a = std.heap.ArenaAllocator.init(gpa);
                defer a.deinit();
                const under = if (c.olpath.len > 0) (try refile_mod.find(a.allocator(), text, c.olpath)) orelse return error.TargetGone else null;
                const ins = try refile_mod.insertion(gpa, text, under, c.sub);
                return .{ .offset = ins.offset, .text = ins.text };
            }
        };
        try insertInFile(ws, path, Ctx{ .olpath = olpath, .sub = moved });
        var edits = [_]editor_mod.Edit{.{ .start = sub.range.start, .end = sub.range.end, .text = "" }};
        ed.commitTransform(&edits, .other);
    }
    ed.afterCommand(true);
    ws.setStatus("Refiled to {s}{s}{s}", .{ std.fs.path.basename(path), if (olpath.len > 0) " › " else "", olpath });
    refreshAgendas(ws);
}

/// C-c $ (C-c C-x C-s): moves the subtree at the caret to the archive
/// file (`#+ARCHIVE:`, else <file>_archive), with org's ARCHIVE_ properties.
fn archive(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    if (!org.isOrg(ed)) return;
    const path = ed.buffer.path orelse return ws.setStatus("Save the file before archiving from it", .{});
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const sub = subtreeAtCaret(ed, alloc) orelse return ws.setStatus("Not under a heading", .{});
    const loc = refile_mod.archiveLocation(alloc, sub.text, path) catch return;
    // The properties org records.
    var doc = parser.parse(ws.gpa, sub.text, path) catch return;
    defer doc.deinit();
    var category: []const u8 = doc.category;
    var todo: []const u8 = "";
    for (doc.headings.items) |h| if (h.offset == sub.range.start) {
        category = h.category;
        todo = h.todo orelse "";
    };
    const parent = if (std.mem.lastIndexOfScalar(u8, sub.olpath, '/')) |p| sub.olpath[0..p] else "";
    const n = date.now();
    var dbuf: [32]u8 = undefined;
    const time = std.fmt.allocPrint(alloc, "{s} {d:0>2}:{d:0>2}", .{ n.date.format(&dbuf), n.time.hour, n.time.minute }) catch return;
    const entry = refile_mod.archived(alloc, sub.text[sub.range.start..sub.range.end], .{ .time = time, .file = path, .olpath = parent, .category = category, .todo = todo }) catch return;
    const Ctx = struct {
        heading: []const u8,
        entry: []const u8,
        fn compute(c: @This(), gpa: Allocator, text: []const u8) !Insertion {
            var a = std.heap.ArenaAllocator.init(gpa);
            defer a.deinit();
            if (c.heading.len == 0) {
                const ins = try refile_mod.insertion(gpa, text, null, c.entry);
                return .{ .offset = ins.offset, .text = ins.text };
            }
            if (try refile_mod.find(a.allocator(), text, c.heading)) |under| {
                const ins = try refile_mod.insertion(gpa, text, under, c.entry);
                return .{ .offset = ins.offset, .text = ins.text };
            }
            // The heading, made at the end of the file.
            const moved = try refile_mod.relevel(a.allocator(), std.mem.trimEnd(u8, c.entry, "\n"), 2);
            const nl: []const u8 = if (text.len > 0 and text[text.len - 1] != '\n') "\n" else "";
            return .{ .offset = text.len, .text = try std.fmt.allocPrint(gpa, "{s}* {s}\n{s}\n", .{ nl, c.heading, moved }) };
        }
    };
    insertInFile(ws, loc.path, Ctx{ .heading = loc.heading, .entry = entry }) catch |err| {
        ws.setStatus("Unable to archive to {s}: {t}", .{ loc.path, err });
        return;
    };
    var edits = [_]editor_mod.Edit{.{ .start = sub.range.start, .end = sub.range.end, .text = "" }};
    ed.commitTransform(&edits, .other);
    ed.afterCommand(true);
    ws.setStatus("Archived to {s}", .{loc.path});
    refreshAgendas(ws);
}

fn refreshAgendas(ws: *Workspace) void {
    for (ws.tabs.items, 0..) |t, i| if (t.agenda != null) refresh(ws, i);
}

fn openLink(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    var buf: [1024]u8 = undefined;
    const target = org.linkAt(ed, &buf) orelse {
        ws.setStatus("No link at point", .{});
        return;
    };
    if (std.mem.startsWith(u8, target, "http://") or std.mem.startsWith(u8, target, "https://")) return file_cmd.openExternally(ws, target);
    // file:path::42 or a bare path, relative to the current file.
    var path = if (std.mem.startsWith(u8, target, "file:")) target[5..] else target;
    var line: ?u32 = null;
    if (std.mem.indexOf(u8, path, "::")) |sep| {
        line = std.fmt.parseInt(u32, path[sep + 2 ..], 10) catch null;
        path = path[0..sep];
    }
    var abs_buf: [std.fs.max_path_bytes]u8 = undefined;
    const abs = if (std.fs.path.isAbsolute(path)) path else blk: {
        const dir = std.fs.path.dirname(ed.buffer.path orelse "") orelse ".";
        break :blk std.fmt.bufPrint(&abs_buf, "{s}/{s}", .{ dir, path }) catch return;
    };
    const target_ed = ws.openFile(abs, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ abs, err });
        return;
    };
    if (line) |l| {
        target_ed.setSingleSelection(.caret(target_ed.buffer.lineStart(l -| 1)));
        target_ed.afterCommand(true);
    }
}

// ---- Agenda tab ---------------------------------------------------------------

pub fn openAgenda(ws: *Workspace, view: agenda_mod.View) void {
    openAgendaQuery(ws, view, "", "");
}

/// The agenda tab (reused if open) showing `view` with `query` (search
/// words, a tag match or TODO keywords) under `title` (a custom command's).
pub fn openAgendaQuery(ws: *Workspace, view: agenda_mod.View, query: []const u8, title: []const u8) void {
    // Reuse an existing agenda tab.
    for (ws.tabs.items, 0..) |t, i| if (t.agenda) |a| {
        a.setQuery(view, query, title) catch {};
        a.goToday();
        ws.activateTab(i);
        refresh(ws, i);
        return;
    };
    const buffer = ws.gpa.create(buffer_mod.Buffer) catch return;
    buffer.* = buffer_mod.Buffer.initEmpty(ws.gpa, 0) catch return;
    buffer.display_name = "*Org Agenda*";
    buffer.language = &languages.org_agenda;
    buffer.read_only = true;
    ws.buffers.append(ws.gpa, buffer) catch return;
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch return;
    const a = ws.gpa.create(Agenda) catch return;
    a.* = Agenda.init(ws.gpa, view);
    a.setQuery(view, query, title) catch {};
    const at = ws.insertTab(.{ .editor = ed, .agenda = a }) catch return;
    refresh(ws, at);
}

fn tabIndex(ws: *Workspace, a: *Agenda) ?usize {
    for (ws.tabs.items, 0..) |t, i| if (t.agenda == a) return i;
    return null;
}

pub fn refresh(ws: *Workspace, index: usize) void {
    const tab = ws.tabs.items[index];
    const a = tab.agenda orelse return;
    const ed = tab.editor;
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const sources = collectSources(ws, arena.allocator()) catch return;
    a.build(sources) catch |err| {
        ws.setStatus("Agenda failed: {t}", .{err});
        return;
    };
    const row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    ed.buffer.read_only = false;
    ed.setText(a.text.items, false);
    ed.buffer.read_only = true;
    // Edits to the agenda text don't count as unsaved changes, nor are they
    // undoable (each refresh would keep the whole agenda twice).
    ed.buffer.saved_version = ed.buffer.version;
    ed.buffer.clearHistory();
    const r = @min(row, ed.buffer.lineCount() - 1);
    ed.setSingleSelection(.caret(ed.buffer.lineStart(if (row == 0) firstItemRow(a) else r)));
    ed.scroll_y = 0;
    ed.ensureCaretVisible(false);
}

fn firstItemRow(a: *const Agenda) u32 {
    for (a.targets.items, 0..) |t, i| if (t != null) return @intCast(i);
    return 0;
}

/// The paths "org_agenda_files" lists (folders: their .org files), or null
/// when it's unset.
fn configuredAgendaFiles(ws: *Workspace, alloc: Allocator) ?[]const []const u8 {
    const json = ws.env.settings.org_agenda_files_json;
    if (json.len == 0) return null;
    const list = std.json.parseFromSliceLeaky([]const []const u8, alloc, json, .{}) catch return null;
    var out: std.ArrayList([]const u8) = .empty;
    for (list) |entry| {
        const path = capturePath(alloc, entry) catch continue;
        if (Io.Dir.cwd().openDir(ws.io, path, .{ .iterate = true })) |dir_const| {
            var dir = dir_const;
            defer dir.close(ws.io);
            var it = dir.iterate();
            while (it.next(ws.io) catch null) |e| {
                if (e.kind != .file or !std.mem.endsWith(u8, e.name, ".org")) continue;
                out.append(alloc, std.fmt.allocPrint(alloc, "{s}/{s}", .{ path, e.name }) catch continue) catch {};
            }
        } else |_| out.append(alloc, path) catch {};
    }
    return out.items;
}

fn collectSources(ws: *Workspace, alloc: Allocator) ![]agenda_mod.Source {
    var out: std.ArrayList(agenda_mod.Source) = .empty;
    var seen: std.StringHashMapUnmanaged(void) = .empty;
    if (configuredAgendaFiles(ws, alloc)) |files| {
        // Just these files, their open buffers' text when they're open.
        for (files) |path| {
            if (seen.contains(path)) continue;
            try seen.put(alloc, path, {});
            const text = blk: {
                for (ws.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, path)) break :blk try b.rope.toOwnedSlice(alloc);
                break :blk Io.Dir.cwd().readFileAlloc(ws.io, path, alloc, .limited(16 << 20)) catch continue;
            };
            try out.append(alloc, .{ .path = path, .text = text });
        }
        return out.items;
    }
    // Open org buffers first (unsaved changes count).
    for (ws.buffers.items) |b| {
        const path = b.path orelse continue;
        if (!std.mem.eql(u8, b.language.name, "Org")) continue;
        try out.append(alloc, .{ .path = path, .text = try b.rope.toOwnedSlice(alloc) });
        try seen.put(alloc, path, {});
    }
    var paths: std.ArrayList([]const u8) = .empty;
    if (ws.project) |p| if (p.snapshot) |snap| {
        for (snap.files) |idx| {
            const e = snap.entries[idx];
            if (!std.mem.endsWith(u8, e.name, ".org")) continue;
            try paths.append(alloc, try p.absolutePath(alloc, e.path));
        }
    };
    if (std.c.getenv("HOME")) |home| {
        const dir_path = try std.fmt.allocPrint(alloc, "{s}{s}", .{ std.mem.span(home), notes_folder[1..] });
        if (Io.Dir.cwd().openDir(ws.io, dir_path, .{ .iterate = true })) |dir_const| {
            var dir = dir_const;
            defer dir.close(ws.io);
            var it = dir.iterate();
            while (it.next(ws.io) catch null) |entry| {
                if (entry.kind != .file or !std.mem.endsWith(u8, entry.name, ".org")) continue;
                try paths.append(alloc, try std.fmt.allocPrint(alloc, "{s}/{s}", .{ dir_path, entry.name }));
            }
        } else |_| {}
    }
    for (paths.items) |path| {
        if (seen.contains(path)) continue;
        const text = Io.Dir.cwd().readFileAlloc(ws.io, path, alloc, .limited(16 << 20)) catch continue;
        try out.append(alloc, .{ .path = path, .text = text });
        try seen.put(alloc, path, {});
    }
    return out.items;
}

/// Keys in the agenda tab (Emacs org-agenda style).
pub fn agendaKey(ws: *Workspace, index: usize, k: platform.KeyEvent) bool {
    const tab = ws.tabs.items[index];
    const a = tab.agenda orelse return false;
    const ed = tab.editor;
    if (k.mods.ctrl or k.mods.alt) return false;
    const row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    const ch: u21 = switch (k.key) {
        .char => if (k.text.len == 1) k.text[0] else k.key.char,
        .named => |n| switch (n) {
            .enter, .tab => {
                gotoItem(ws, a, row);
                return true;
            },
            .right, .left => if (k.mods.shift) {
                cycleItem(ws, index, row, k.key.named == .right);
                return true;
            } else return false,
            else => return false,
        },
    };
    switch (ch) {
        'n', 'j' => _ = ed.perform(.move_down),
        'p', 'k' => _ = ed.perform(.move_up),
        'o' => gotoItem(ws, a, row),
        't' => cycleItem(ws, index, row, true),
        // Filters: by tag, text and the item's category; | clears them.
        '/' => ws.openPrompt("Filter by tags (+work-home):", @intFromEnum(PromptKind.agenda_filter_tags), a.filter.tags),
        '=' => ws.openPrompt("Filter by text:", @intFromEnum(PromptKind.agenda_filter_text), a.filter.text),
        '<' => if (a.targetAt(row)) |target| {
            var f = a.filter;
            f.category = if (std.mem.eql(u8, f.category, target.category)) "" else target.category;
            a.setFilter(f) catch {};
            refresh(ws, index);
        },
        '|' => {
            a.setFilter(.{}) catch {};
            refresh(ws, index);
        },
        's' => ws.openPrompt("Search headings for (words):", @intFromEnum(PromptKind.agenda_search), ""),
        'm' => ws.openPrompt("Match tags (work+urgent-someday|home):", @intFromEnum(PromptKind.agenda_tags), ""),
        // On the item, in its file: priority, tags, schedule, deadline,
        // clocking, archive and refile.
        '+', '-' => itemAction(ws, index, row, if (ch == '+') .org_priority_up else .org_priority_down),
        ':' => itemAction(ws, index, row, .org_set_tags),
        'S', '>' => itemAction(ws, index, row, .org_schedule),
        'D' => itemAction(ws, index, row, .org_deadline),
        'I' => itemAction(ws, index, row, .org_clock_in),
        'O' => {
            clockOut(ws, true);
            ws.activateTab(index);
            refresh(ws, index);
        },
        '$' => itemAction(ws, index, row, .org_archive),
        'W' => itemAction(ws, index, row, .org_refile),
        'f' => {
            a.shift(true);
            refresh(ws, index);
        },
        'b' => {
            a.shift(false);
            refresh(ws, index);
        },
        '.' => {
            a.goToday();
            refresh(ws, index);
        },
        'd' => {
            a.setView(.day);
            refresh(ws, index);
        },
        'w' => {
            a.setView(.week);
            refresh(ws, index);
        },
        'T' => {
            a.setView(.todo);
            refresh(ws, index);
        },
        'r', 'g' => refresh(ws, index),
        'q' => ws.closeTab(index),
        else => return false,
    }
    return true;
}

/// Runs `action` on the agenda item at `row`, in its file, then comes back
/// to the agenda, refreshed (after the prompt, for those that ask).
fn itemAction(ws: *Workspace, agenda_index: usize, row: u32, action: Action) void {
    const a = ws.tabs.items[agenda_index].agenda.?;
    const target = a.targetAt(row) orelse return;
    const agenda_ed = ws.tabs.items[agenda_index].editor;
    const ed = ws.openFile(target.path, false) catch return;
    ed.setSingleSelection(.caret(@min(target.offset, ed.buffer.len())));
    ws.org_agenda_return = agenda_ed;
    ws.perform(action);
    // A prompt (tags, dates, refile) comes back once answered.
    if (ws.overlay == null) returnToAgenda(ws);
}

/// After an action on an agenda item: back to the agenda, refreshed, on
/// the same row.
fn returnToAgenda(ws: *Workspace) void {
    const agenda_ed = ws.org_agenda_return orelse return;
    ws.org_agenda_return = null;
    for (ws.tabs.items, 0..) |t, i| if (t.editor == agenda_ed) {
        const row = agenda_ed.buffer.offsetToPoint(agenda_ed.primarySelection().head).row;
        ws.activateTab(i);
        refresh(ws, i);
        agenda_ed.setSingleSelection(.caret(agenda_ed.buffer.lineStart(@min(row, agenda_ed.buffer.lineCount() - 1))));
        return;
    };
}

fn setAgendaFilter(ws: *Workspace, kind: PromptKind, value: []const u8) void {
    for (ws.tabs.items, 0..) |t, i| if (t.agenda) |a| {
        var f = a.filter;
        if (kind == .agenda_filter_tags) f.tags = value else f.text = value;
        a.setFilter(f) catch {};
        ws.activateTab(i);
        refresh(ws, i);
        return;
    };
}

/// A double click on an agenda item opens it.
pub fn onDoubleClick(ws: *Workspace, ed: *Editor) void {
    for (ws.tabs.items) |t| if (t.editor == ed) if (t.agenda) |a| {
        gotoItem(ws, a, ed.buffer.offsetToPoint(ed.primarySelection().head).row);
        return;
    };
}

// ---- Source blocks ---------------------------------------------------------------

pub const SrcEdit = struct {
    /// The tab with the block's text, in its language.
    src_ed: *Editor,
    /// The org file's editor and the block's #+BEGIN_SRC row in it.
    org_ed: *Editor,
    begin_row: u32,
    indent_buf: [64]u8 = undefined,
    indent_len: usize = 0,
};

pub fn isSrcTab(ws: *Workspace, ed: *Editor) bool {
    const s = ws.org_src orelse return false;
    return s.src_ed == ed;
}

/// A tab is closing: forget a source edit that used it (closing the
/// block's tab without saving drops the changes, as in Emacs).
pub fn tabClosed(ws: *Workspace, ed: *Editor) void {
    const s = ws.org_src orelse return;
    // The org file may still be open in another tab.
    if (s.src_ed == ed) ws.org_src = null else if (s.org_ed == ed) {
        for (ws.tabs.items) |t| if (t.editor == ed) return;
        ws.org_src = null;
    }
}

/// C-c ' in a source block: its code in a tab of its own, in the block's
/// language. C-c ' / SPC m ' or saving there puts it back.
fn editSrc(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    if (isSrcTab(ws, ed)) return finishSrc(ws);
    if (!org.isOrg(ed)) return;
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const block = org.srcBlockAt(ed, arena.allocator()) orelse return ws.setStatus("Not in a source block", .{});
    if (ws.org_src != null) finishSrc(ws);
    const lang = languages.forInjection(block.lang) orelse &languages.plain_text;
    const src_ed = ws.newFile() catch return;
    src_ed.buffer.scratch = true;
    src_ed.buffer.display_name = "*Org Src*";
    src_ed.buffer.setLanguage(ws.registry, lang);
    src_ed.setText(block.body, false);
    src_ed.buffer.saved_version = src_ed.buffer.version;
    src_ed.setSingleSelection(.caret(0));
    var s: SrcEdit = .{ .src_ed = src_ed, .org_ed = ed, .begin_row = block.begin_row };
    s.indent_len = @min(block.indent.len, s.indent_buf.len);
    @memcpy(s.indent_buf[0..s.indent_len], block.indent[0..s.indent_len]);
    ws.org_src = s;
    ws.setStatus("Editing the {s} block: save (or SPC m ') to put it back, close the tab to drop the changes", .{if (block.lang.len > 0) block.lang else "source"});
}

/// Writes the source tab's text back into its block and closes the tab.
pub fn finishSrc(ws: *Workspace) void {
    const s = ws.org_src orelse return;
    ws.org_src = null;
    const text = s.src_ed.buffer.rope.toOwnedSlice(ws.gpa) catch return;
    defer ws.gpa.free(text);
    const org_open = for (ws.tabs.items) |t| {
        if (t.editor == s.org_ed) break true;
    } else false;
    if (!org_open) return ws.setStatus("The org file was closed; the block's text is still in this tab", .{});
    if (!org.replaceSrcBody(s.org_ed, s.begin_row, text, s.indent_buf[0..s.indent_len])) {
        return ws.setStatus("The block moved in the org file; its text is still in this tab", .{});
    }
    for (ws.tabs.items, 0..) |t, i| if (t.editor == s.src_ed) {
        ws.closeTabNow(i);
        break;
    };
    for (ws.tabs.items, 0..) |t, i| if (t.editor == s.org_ed) {
        ws.activateTab(i);
        s.org_ed.setSingleSelection(.caret(s.org_ed.buffer.lineStart(s.begin_row)));
        s.org_ed.afterCommand(true);
        break;
    };
}

// ---- Export -------------------------------------------------------------------------

/// Writes the org file as HTML or Markdown next to it (plan.org → plan.html).
fn exportFile(ws: *Workspace, fmt: export_mod.Format) void {
    const ed = ws.activeEditor() orelse return;
    if (!org.isOrg(ed)) return ws.setStatus("Export works on org files", .{});
    const path = ed.buffer.path orelse return ws.setStatus("Save the file first: the export goes next to it", .{});
    const text = ed.buffer.rope.toOwnedSlice(ws.gpa) catch return;
    defer ws.gpa.free(text);
    const out = switch (fmt) {
        .html => export_mod.toHtml(ws.gpa, text, path),
        .markdown => export_mod.toMarkdown(ws.gpa, text, path),
    } catch |err| return ws.setStatus("Export failed: {t}", .{err});
    defer ws.gpa.free(out);
    const ext = std.fs.path.extension(path);
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const target = std.fmt.bufPrint(&buf, "{s}.{s}", .{ path[0 .. path.len - ext.len], if (fmt == .html) "html" else "md" }) catch return;
    Io.Dir.cwd().writeFile(ws.io, .{ .sub_path = target, .data = out }) catch |err| return ws.setStatus("Unable to write {s}: {t}", .{ target, err });
    ws.setStatus("Exported to {s}", .{target});
}

// ---- Agenda dispatcher -----------------------------------------------------------

pub const dispatch_tag: u32 = 0xD15;

/// A view of one's own, from settings.json's "org_agenda_custom_commands".
const CustomCommand = struct {
    key: []const u8 = "",
    description: []const u8 = "",
    /// "agenda", "day", "todo", "tags" or "search".
    type: []const u8 = "todo",
    match: []const u8 = "",
};

const builtin_views = [_]struct { key: []const u8, title: []const u8 }{
    .{ .key = "a", .title = "Agenda for the week" },
    .{ .key = "d", .title = "Agenda for today" },
    .{ .key = "t", .title = "All TODO items" },
    .{ .key = "s", .title = "Search headings…" },
    .{ .key = "m", .title = "Match tags…" },
};

fn customCommands(ws: *Workspace, alloc: Allocator) []const CustomCommand {
    const json = ws.env.settings.org_agenda_custom_commands_json;
    if (json.len == 0) return &.{};
    return std.json.parseFromSliceLeaky([]const CustomCommand, alloc, json, .{ .ignore_unknown_fields = true }) catch &.{};
}

/// SPC o a o: the built-in views and the custom ones, to pick from.
fn dispatch(ws: *Workspace) void {
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    var titles: std.ArrayList([]const u8) = .empty;
    defer titles.deinit(ws.gpa);
    for (builtin_views) |v| titles.append(ws.gpa, std.fmt.allocPrint(alloc, "[{s}] {s}", .{ v.key, v.title }) catch return) catch return;
    for (customCommands(ws, alloc)) |c| {
        titles.append(ws.gpa, std.fmt.allocPrint(alloc, "[{s}] {s}  ({s}: {s})", .{ c.key, c.description, c.type, c.match }) catch return) catch return;
    }
    ws.openList(.list, ws.gpa.dupe(u8, "Agenda view:") catch "Save changes?", dispatch_tag, titles.items);
}

fn runDispatch(ws: *Workspace, choice: usize) void {
    switch (choice) {
        0 => return openAgenda(ws, .week),
        1 => return openAgenda(ws, .day),
        2 => return openAgenda(ws, .todo),
        3 => return perform(ws, .org_agenda_search),
        4 => return perform(ws, .org_agenda_tags),
        else => {},
    }
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const customs = customCommands(ws, arena.allocator());
    const i = choice - builtin_views.len;
    if (i >= customs.len) return;
    const c = customs[i];
    const view: agenda_mod.View = if (std.mem.eql(u8, c.type, "tags"))
        .tags
    else if (std.mem.eql(u8, c.type, "search"))
        .search
    else if (std.mem.eql(u8, c.type, "agenda"))
        .week
    else if (std.mem.eql(u8, c.type, "day"))
        .day
    else
        .todo;
    var tbuf: [96]u8 = undefined;
    const title = std.fmt.bufPrint(&tbuf, "{s} — ", .{c.description}) catch "";
    openAgendaQuery(ws, view, c.match, title);
}

fn gotoItem(ws: *Workspace, a: *Agenda, row: u32) void {
    const target = a.targetAt(row) orelse return;
    const ed = ws.openFile(target.path, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ target.path, err });
        return;
    };
    ed.setSingleSelection(.caret(@min(target.offset, ed.buffer.len())));
    ed.afterCommand(true);
}

fn cycleItem(ws: *Workspace, agenda_index: usize, row: u32, forward: bool) void {
    const a = ws.tabs.items[agenda_index].agenda.?;
    const target = a.targetAt(row) orelse return;
    const agenda_ed = ws.tabs.items[agenda_index].editor;
    const ed = ws.openFile(target.path, false) catch return;
    ed.setSingleSelection(.caret(@min(target.offset, ed.buffer.len())));
    _ = org.cycleTodo(ed, forward);
    // Back to the agenda, refreshed.
    for (ws.tabs.items, 0..) |t, i| if (t.editor == agenda_ed) {
        ws.activateTab(i);
        refresh(ws, i);
        agenda_ed.setSingleSelection(.caret(agenda_ed.buffer.lineStart(@min(row, agenda_ed.buffer.lineCount() - 1))));
    };
}
