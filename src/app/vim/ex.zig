//! Ex commands for vim mode (`:w`, `:q`, `:%s/a/b/g`, `:42`, `:set ...`).

const std = @import("std");
const vim_mod = @import("vim.zig");
const folds = @import("folds.zig");
const window = @import("window.zig");
const groups = @import("../workspace/groups.zig");
const workspace_mod = @import("../workspace/workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const regex_mod = @import("../../framework/util/regex.zig");
const motions = @import("motions.zig");
const pattern_mod = @import("pattern.zig");

const platform_mod = @import("../../framework/platform/platform.zig");

const Vim = vim_mod.Vim;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;

const Range = struct { first: u32, last: u32, given: bool };

fn parseLine(vim: *Vim, ed: *Editor, s: []const u8, i: *usize) ?u32 {
    if (i.* >= s.len) return null;
    const cur = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    const last = motions.lastRow(ed.buffer);
    var base: ?u32 = null;
    switch (s[i.*]) {
        '.' => {
            base = cur;
            i.* += 1;
        },
        '$' => {
            base = last;
            i.* += 1;
        },
        '\'' => {
            if (i.* + 1 < s.len) {
                const m = s[i.* + 1];
                i.* += 2;
                const lv = vim.last_visual;
                if (m == '<' or m == '>') {
                    const v = lv orelse return null;
                    const a = ed.buffer.offsetToPoint(@min(v.anchor, ed.buffer.len())).row;
                    const b = ed.buffer.offsetToPoint(@min(v.cursor, ed.buffer.len())).row;
                    base = if (m == '<') @min(a, b) else @max(a, b);
                } else if (m >= 'a' and m <= 'z') {
                    const off = vim.marks[m - 'a'] orelse return null;
                    base = ed.buffer.offsetToPoint(@min(off, ed.buffer.len())).row;
                }
            }
        },
        '0'...'9' => {
            const start = i.*;
            while (i.* < s.len and std.ascii.isDigit(s[i.*])) i.* += 1;
            const n = std.fmt.parseInt(u32, s[start..i.*], 10) catch return null;
            base = @min(last, n -| 1);
        },
        else => {},
    }
    // Offsets: .+3, $-2
    while (i.* < s.len and (s[i.*] == '+' or s[i.*] == '-')) {
        const sign = s[i.*];
        i.* += 1;
        const start = i.*;
        while (i.* < s.len and std.ascii.isDigit(s[i.*])) i.* += 1;
        const n = if (i.* > start) std.fmt.parseInt(u32, s[start..i.*], 10) catch 1 else 1;
        const b = base orelse cur;
        base = if (sign == '+') @min(last, b + n) else b -| n;
    }
    return base;
}

fn parseRange(vim: *Vim, ed: *Editor, s: []const u8, i: *usize) Range {
    const cur = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    if (i.* < s.len and s[i.*] == '%') {
        i.* += 1;
        return .{ .first = 0, .last = motions.lastRow(ed.buffer), .given = true };
    }
    const a = parseLine(vim, ed, s, i) orelse return .{ .first = cur, .last = cur, .given = false };
    if (i.* < s.len and (s[i.*] == ',' or s[i.*] == ';')) {
        i.* += 1;
        const b = parseLine(vim, ed, s, i) orelse a;
        return .{ .first = @min(a, b), .last = @max(a, b), .given = true };
    }
    return .{ .first = a, .last = a, .given = true };
}

pub fn execute(vim: *Vim, ws: *Workspace, ed: *Editor, cmd_in: []const u8) void {
    const cmd = std.mem.trim(u8, cmd_in, " \t");
    var i: usize = 0;
    var range = parseRange(vim, ed, cmd, &i);
    const rest = std.mem.trimStart(u8, cmd[i..], " ");
    var name_end: usize = 0;
    while (name_end < rest.len and std.ascii.isAlphabetic(rest[name_end])) name_end += 1;
    var name = rest[0..name_end];
    var bang = false;
    var args = rest[name_end..];
    if (args.len > 0 and args[0] == '!') {
        bang = true;
        args = args[1..];
    }
    args = std.mem.trim(u8, args, " ");
    // Symbol commands (s with odd delimiters, <, >) aren't alphabetic.
    if (name.len == 0 and rest.len > 0 and (rest[0] == '<' or rest[0] == '>' or rest[0] == '&')) {
        name = rest[0..1];
        args = rest[1..];
    }

    const eql = std.mem.eql;
    if (name.len == 0) {
        if (range.given) {
            ed.pushSelectionHistory();
            ed.setSingleSelection(.caret(ed.buffer.lineStart(range.last)));
            vim.enterNormal(ed);
            var c = @import("motions.zig").Ctx.init(ed.buffer);
            ed.setSingleSelection(.caret(c.firstNonBlank(range.last)));
            ed.ensureCaretVisible(true);
        }
        return;
    }
    // A command's range takes closed folds whole; then :fold, :foldopen, :foldclose.
    range = folds.wholeFolds(ed, range);
    if (folds.exCommand(vim, ed, name, bang, range.first, range.last)) return;
    // Windows (:split, :close, :only, :wincmd, :resize, ...) and tabs.
    if (window.exCommand(vim, ws, ed, cmd, name, args, bang)) return;
    if (eql(u8, name, "w") or eql(u8, name, "write")) {
        if (args.len > 0) saveAs(ws, ed, args) else ws.perform(.save);
    } else if (eql(u8, name, "wq") or eql(u8, name, "x") or eql(u8, name, "xit") or eql(u8, name, "exit")) {
        if (args.len > 0) saveAs(ws, ed, args) else if (ed.buffer.isDirty() or eql(u8, name, "wq")) ws.perform(.save);
        if (!ed.buffer.isDirty() and !window.quitSplit(ws)) ws.perform(.close_tab);
    } else if (eql(u8, name, "q") or eql(u8, name, "quit") or eql(u8, name, "bd") or eql(u8, name, "bdelete")) {
        // :q leaves a buffer that another view shows (with its changes) alone.
        const shared = name[0] == 'q' and !groups.soleView(ws, ed);
        if (ed.buffer.isDirty() and !bang and !shared) {
            vim.setMessageText("E37: No write since last change (add ! to override)");
            return;
        }
        if (bang and !shared) ed.buffer.saved_version = ed.buffer.version;
        // In a split, :q closes the window.
        if (name[0] == 'q' and window.quitSplit(ws)) return;
        const last_tab = ws.tabs.items.len <= 1 and (eql(u8, name, "q") or eql(u8, name, "quit"));
        ws.perform(.close_tab);
        window.closeIfEmpty(ws);
        if (last_tab) ws.should_quit = true;
    } else if (eql(u8, name, "qa") or eql(u8, name, "qall") or eql(u8, name, "quitall")) {
        if (bang) ws.should_quit = true else ws.perform(.quit);
    } else if (eql(u8, name, "wa") or eql(u8, name, "wall")) {
        ws.perform(.save_all);
    } else if (eql(u8, name, "wqa") or eql(u8, name, "xa") or eql(u8, name, "xall")) {
        ws.perform(.save_all);
        ws.should_quit = true;
    } else if (eql(u8, name, "e") or eql(u8, name, "edit")) {
        if (args.len == 0) {
            if (bang) ws.perform(.revert_file);
        } else {
            window.edit(vim, ws, ed, args);
        }
    } else if (eql(u8, name, "noh") or eql(u8, name, "nohlsearch")) {
        ed.highlights.clearRetainingCapacity();
    } else if (eql(u8, name, "s") or eql(u8, name, "substitute")) {
        substitute(vim, ed, range, args);
    } else if (eql(u8, name, "set") or eql(u8, name, "se")) {
        setOption(vim, ws, ed, args);
    } else if (eql(u8, name, "bn") or eql(u8, name, "bnext")) {
        ws.perform(.next_tab);
    } else if (eql(u8, name, "bp") or eql(u8, name, "bprevious") or eql(u8, name, "bprev")) {
        ws.perform(.prev_tab);
    } else if (eql(u8, name, "enew")) {
        _ = ws.newFile() catch {};
    } else if (eql(u8, name, "sort") or eql(u8, name, "sor")) {
        selectRows(ed, if (range.given) range else .{ .first = 0, .last = motions.lastRow(ed.buffer), .given = true }, true);
        ed.permuteLines(if (std.mem.indexOfScalar(u8, args, 'i') != null) .sort else .sort_case);
        vim.enterNormal(ed);
    } else if (eql(u8, name, "d") or eql(u8, name, "delete") or eql(u8, name, "y") or eql(u8, name, "yank")) {
        const yank = name[0] == 'y';
        var r = [_]@import("motions.zig").Range{.{
            .start = ed.buffer.lineStart(range.first),
            .end = if (range.last + 1 < ed.buffer.lineCount()) ed.buffer.lineStart(range.last + 1) else ed.buffer.len(),
            .linewise = true,
        }};
        ed.setSingleSelection(.caret(r[0].start));
        vim.operate(ed, if (yank) .yank else .delete, &r);
    } else if (eql(u8, name, ">") or eql(u8, name, "<")) {
        selectRows(ed, range, true);
        if (name[0] == '>') ed.indentLines() else ed.unindentLines();
        vim.enterNormal(ed);
    } else if (eql(u8, name, "j") or eql(u8, name, "join")) {
        selectRows(ed, range, true);
        ed.joinLines();
        vim.enterNormal(ed);
    } else if (eql(u8, name, "pu") or eql(u8, name, "put")) {
        put(vim, ed, range, args, bang);
    } else if (eql(u8, name, "t") or eql(u8, name, "co") or eql(u8, name, "copy") or eql(u8, name, "m") or eql(u8, name, "move")) {
        copyOrMove(vim, ed, range, args, name[0] == 'm');
    } else if (eql(u8, name, "norm") or eql(u8, name, "normal")) {
        normal(vim, ws, ed, range, args);
    } else if (eql(u8, name, "g") or eql(u8, name, "global") or eql(u8, name, "v") or eql(u8, name, "vglobal")) {
        globalCommand(vim, ws, ed, if (range.given) range else .{ .first = 0, .last = motions.lastRow(ed.buffer), .given = true }, args, bang or name[0] == 'v');
    } else if (eql(u8, name, "syntax") or eql(u8, name, "sy")) {
        ws.perform(.command_palette);
    } else {
        var buf: [96]u8 = undefined;
        vim.setMessageText(std.fmt.bufPrint(&buf, "E492: Not an editor command: {s}", .{cmd}) catch "E492");
    }
}

fn saveAs(ws: *Workspace, ed: *Editor, path: []const u8) void {
    ed.buffer.setPath(path) catch return;
    ws.perform(.save);
    ws.updateTitle();
}

fn selectRows(ed: *Editor, range: Range, _: bool) void {
    const end = ed.buffer.lineEnd(range.last);
    ed.setSingleSelection(.range(ed.buffer.lineStart(range.first), end));
}

fn setOption(vim: *Vim, ws: *Workspace, ed: *Editor, args: []const u8) void {
    var it = std.mem.tokenizeScalar(u8, args, ' ');
    while (it.next()) |opt| {
        const eq = std.mem.indexOfScalar(u8, opt, '=');
        const key = if (eq) |e| opt[0..e] else opt;
        const value = if (eq) |e| opt[e + 1 ..] else "";
        if (window.setOption(ws, key)) continue;
        const settings = ed.env.settings;
        const eql = std.mem.eql;
        if (eql(u8, key, "nu") or eql(u8, key, "number")) settings.show_line_numbers = true else if (eql(u8, key, "nonu") or eql(u8, key, "nonumber")) settings.show_line_numbers = false else if (eql(u8, key, "list")) settings.draw_white_space = .all else if (eql(u8, key, "nolist")) settings.draw_white_space = .selection else if (eql(u8, key, "et") or eql(u8, key, "expandtab")) ed.buffer.translate_tabs_to_spaces = true else if (eql(u8, key, "noet") or eql(u8, key, "noexpandtab")) ed.buffer.translate_tabs_to_spaces = false else if (eql(u8, key, "ts") or eql(u8, key, "tabstop") or eql(u8, key, "sw") or eql(u8, key, "shiftwidth") or eql(u8, key, "sts")) {
            const n = std.fmt.parseInt(u32, value, 10) catch continue;
            if (n > 0 and n <= 16) ed.buffer.tab_size = n;
        } else if (eql(u8, key, "ic") or eql(u8, key, "ignorecase")) {
            vim.ignore_case = true;
        } else if (eql(u8, key, "noic") or eql(u8, key, "noignorecase")) {
            vim.ignore_case = false;
        } else if (eql(u8, key, "scs") or eql(u8, key, "smartcase")) {
            vim.smart_case = true;
        } else if (eql(u8, key, "noscs") or eql(u8, key, "nosmartcase")) {
            vim.smart_case = false;
        } else if (eql(u8, key, "hls") or eql(u8, key, "hlsearch")) {
            // Matches are always highlighted.
        } else if (eql(u8, key, "nohls") or eql(u8, key, "nohlsearch")) {
            ed.highlights.clearRetainingCapacity();
        } else if (eql(u8, key, "minimap")) settings.show_minimap = true else if (eql(u8, key, "nominimap")) settings.show_minimap = false else if (folds.setOption(vim, ed, key, value)) {} else {
            var buf: [96]u8 = undefined;
            vim.setMessageText(std.fmt.bufPrint(&buf, "E518: Unknown option: {s}", .{key}) catch "E518");
        }
    }
}

/// :[range]s/pattern/replacement/[flags] — `&` and \1..\9 in the replacement.
fn substitute(vim: *Vim, ed: *Editor, range: Range, args: []const u8) void {
    if (args.len < 2) return;
    const delim = args[0];
    var parts: [3][]const u8 = .{ "", "", "" };
    var part: usize = 0;
    var start: usize = 1;
    var i: usize = 1;
    while (i < args.len and part < 2) : (i += 1) {
        if (args[i] == '\\' and i + 1 < args.len and args[i + 1] == delim) {
            i += 1;
            continue;
        }
        if (args[i] == delim) {
            parts[part] = args[start..i];
            part += 1;
            start = i + 1;
        }
    }
    if (part < 2) {
        parts[part] = args[start..];
        part += 1;
    } else parts[2] = args[start..];
    var pattern = parts[0];
    if (pattern.len == 0) pattern = vim.last_search orelse return;
    const replacement = parts[1];
    const flags = parts[2];
    const global = std.mem.indexOfScalar(u8, flags, 'g') != null;
    const ignore_case = std.mem.indexOfScalar(u8, flags, 'i') != null;
    vim.setSearch(pattern, true);

    const gpa = ed.gpa;
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const t = pattern_mod.translate(alloc, pattern) catch return;
    const no_ignore = std.mem.indexOfScalar(u8, flags, 'I') != null;
    const ci = !no_ignore and (ignore_case or pattern_mod.ignoresCase(t.case, vim.ignore_case, vim.smart_case, pattern));
    var re = regex_mod.Regex.compile(gpa, t.pattern, .{ .case_insensitive = ci }) catch {
        vim.setMessageText("E486: Invalid pattern");
        return;
    };
    defer re.deinit();

    var edits: std.ArrayList(editor_mod.Edit) = .empty;
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(gpa);
    var count: usize = 0;
    var row = range.first;
    while (row <= range.last) : (row += 1) {
        const text = ed.buffer.lineText(row, &line) catch return;
        const base = ed.buffer.lineStart(row);
        var pos: usize = 0;
        while (pos <= text.len) {
            const m = re.search(text, pos) orelse break;
            var out: std.ArrayList(u8) = .empty;
            var j: usize = 0;
            while (j < replacement.len) : (j += 1) {
                const ch = replacement[j];
                if (ch == '&') {
                    out.appendSlice(alloc, text[m.start..m.end]) catch return;
                } else if (ch == '\\' and j + 1 < replacement.len) {
                    j += 1;
                    const nx = replacement[j];
                    if (nx >= '1' and nx <= '9') {
                        if (m.groups[nx - '1']) |g| out.appendSlice(alloc, text[g[0]..g[1]]) catch return;
                    } else if (nx == 'n' or nx == 'r') {
                        out.append(alloc, '\n') catch return;
                    } else if (nx == 't') {
                        out.append(alloc, '\t') catch return;
                    } else out.append(alloc, nx) catch return;
                } else out.append(alloc, ch) catch return;
            }
            edits.append(alloc, .{ .start = base + m.start, .end = base + m.end, .text = out.items }) catch return;
            count += 1;
            if (!global) break;
            pos = if (m.end > m.start) m.end else m.end + 1;
        }
    }
    if (count == 0) {
        vim.setMessageText("E486: Pattern not found");
        return;
    }
    // Cursor to the start of the last substituted line, like vim.
    const last_row = ed.buffer.offsetToPoint(edits.items[edits.items.len - 1].start).row;
    ed.commitTransform(edits.items, .other);
    var c = @import("motions.zig").Ctx.init(ed.buffer);
    ed.setSingleSelection(.caret(c.firstNonBlank(last_row)));
    vim.enterNormal(ed);
    var buf: [64]u8 = undefined;
    vim.setMessageText(std.fmt.bufPrint(&buf, "{d} substitution(s)", .{count}) catch "");
}

/// The text of rows first..last with their newlines (the last without one when
/// the buffer ends there), and the offset just past them.
fn rowsText(alloc: std.mem.Allocator, ed: *Editor, first: u32, last: u32) ?struct { text: []u8, end: usize } {
    const start = ed.buffer.lineStart(first);
    const end = if (last + 1 < ed.buffer.lineCount()) ed.buffer.lineStart(last + 1) else ed.buffer.len();
    const text = ed.buffer.textRange(alloc, start, end) catch return null;
    return .{ .text = text, .end = end };
}

/// Where lines inserted after row `after` go (null: above the first line),
/// and whether a newline must come first because the buffer ends without one.
fn insertionPoint(ed: *Editor, after: ?u32) struct { at: usize, lead_newline: bool } {
    const row = after orelse return .{ .at = 0, .lead_newline = false };
    if (row + 1 < ed.buffer.lineCount()) return .{ .at = ed.buffer.lineStart(row + 1), .lead_newline = false };
    const len = ed.buffer.len();
    return .{ .at = len, .lead_newline = !endsWithNewline(ed) };
}

fn endsWithNewline(ed: *Editor) bool {
    const len = ed.buffer.len();
    if (len == 0) return true;
    var c = motions.Ctx.init(ed.buffer);
    return c.s.byte(len - 1) == '\n';
}

fn insertLines(vim: *Vim, ed: *Editor, after: ?u32, text_in: []const u8) void {
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    const where = insertionPoint(ed, after);
    var text = text_in;
    if (text.len == 0 or text[text.len - 1] != '\n') text = std.mem.concat(alloc, u8, &.{ text, "\n" }) catch return;
    if (where.lead_newline) text = std.mem.concat(alloc, u8, &.{ "\n", text[0 .. text.len - 1] }) catch return;
    var edits = [_]editor_mod.Edit{.{ .start = where.at, .end = where.at, .text = text }};
    ed.commitTransform(&edits, .other);
    const first_new: u32 = if (after) |r| r + 1 else 0;
    var c = motions.Ctx.init(ed.buffer);
    ed.setSingleSelection(.caret(c.firstNonBlank(first_new)));
    vim.enterNormal(ed);
}

/// :[line]put [x] — the register's text as lines below `line` (above with !).
fn put(vim: *Vim, ed: *Editor, range: Range, args: []const u8, above: bool) void {
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    if (args.len > 0) vim.register = args[0];
    defer vim.register = null;
    const reg = vim.readRegister(ed, arena.allocator()) orelse {
        vim.setMessageText("E353: Nothing in register");
        return;
    };
    insertLines(vim, ed, if (above) (if (range.last == 0) null else range.last - 1) else range.last, reg.text);
}

/// :[range]t {address} (copy) and :[range]m {address} (move): below the
/// address line; address 0 is above the first line.
fn copyOrMove(vim: *Vim, ed: *Editor, range: Range, args: []const u8, move: bool) void {
    var arena = std.heap.ArenaAllocator.init(ed.gpa);
    defer arena.deinit();
    const alloc = arena.allocator();
    var i: usize = 0;
    const trimmed = std.mem.trim(u8, args, " ");
    const after: ?u32 = if (std.mem.eql(u8, trimmed, "0")) null else parseLine(vim, ed, trimmed, &i) orelse {
        vim.setMessageText("E14: Invalid address");
        return;
    };
    const rows = rowsText(alloc, ed, range.first, range.last) orelse return;
    if (!move) return insertLines(vim, ed, after, rows.text);
    // Moving into the lines being moved changes nothing.
    if (after) |a| if (a >= range.first and a < range.last) {
        vim.setMessageText("E134: Cannot move a range of lines into itself");
        return;
    };
    const dest_row: u32 = after orelse 0;
    const dest_before_first = after == null or dest_row < range.first;
    const where = insertionPoint(ed, after);
    var text = rows.text;
    if (text.len == 0 or text[text.len - 1] != '\n') text = std.mem.concat(alloc, u8, &.{ text, "\n" }) catch return;
    if (where.lead_newline) text = std.mem.concat(alloc, u8, &.{ "\n", text[0 .. text.len - 1] }) catch return;
    var del_start = ed.buffer.lineStart(range.first);
    // The last line of a buffer with no final newline takes the newline before it.
    if (rows.end == ed.buffer.len() and !endsWithNewline(ed) and del_start > 0) del_start -= 1;
    var edits = [_]editor_mod.Edit{
        .{ .start = del_start, .end = rows.end, .text = "" },
        .{ .start = where.at, .end = where.at, .text = text },
    };
    _ = dest_before_first;
    ed.commitTransform(&edits, .other);
    const moved_to: u32 = if (after == null) 0 else if (dest_row < range.first) dest_row + 1 else dest_row - (range.last - range.first);
    var c = motions.Ctx.init(ed.buffer);
    ed.setSingleSelection(.caret(c.firstNonBlank(moved_to + (range.last - range.first))));
    vim.enterNormal(ed);
}

/// :[range]normal {keys} — the keys as normal-mode commands on each line.
fn normal(vim: *Vim, ws: *Workspace, ed: *Editor, range: Range, keys: []const u8) void {
    var rows: std.ArrayList(u32) = .empty;
    defer rows.deinit(ed.gpa);
    var r = range.first;
    while (r <= range.last) : (r += 1) rows.append(ed.gpa, r) catch return;
    ed.buffer.beginGroup();
    defer ed.buffer.endGroup();
    var delta: i64 = 0;
    for (rows.items) |row0| {
        const row: i64 = @as(i64, row0) + delta;
        if (row < 0 or row >= ed.buffer.lineCount()) continue;
        const before: i64 = ed.buffer.lineCount();
        ed.setSingleSelection(.caret(ed.buffer.lineStart(@intCast(row))));
        vim.enterNormal(ed);
        runKeys(vim, ws, ed, keys);
        delta += @as(i64, ed.buffer.lineCount()) - before;
    }
}

/// Feeds `keys` to vim as typed; whatever is left open ends as on Escape.
pub fn runKeys(vim: *Vim, ws: *Workspace, ed: *Editor, keys: []const u8) void {
    var it = std.unicode.Utf8View.initUnchecked(keys).iterator();
    while (it.nextCodepointSlice()) |cp| {
        const code = std.unicode.utf8Decode(cp) catch continue;
        const ev: platform_mod.KeyEvent = switch (code) {
            0x1b => .{ .key = .{ .named = .escape }, .mods = .{} },
            '\r' => .{ .key = .{ .named = .enter }, .mods = .{} },
            0x01...0x0c, 0x0e...0x1a => .{ .key = .{ .char = code + 0x60 }, .mods = .{ .ctrl = true } },
            else => .{ .key = .{ .char = code }, .mods = .{}, .text = cp },
        };
        if (!vim.handleKey(ws, ed, ev) and ev.text.len > 0) ed.insertText(ev.text);
    }
    if (vim.mode != .normal) _ = vim.handleKey(ws, ed, .{ .key = .{ .named = .escape }, .mods = .{} });
}

/// :[range]g/pattern/command (and :v, :g!) — `command` on every line that
/// matches (does not match); one undo step.
fn globalCommand(vim: *Vim, ws: *Workspace, ed: *Editor, range: Range, args: []const u8, invert: bool) void {
    if (args.len < 2) return;
    const delim = args[0];
    var end: usize = 1;
    while (end < args.len and args[end] != delim) : (end += 1) {
        if (args[end] == '\\' and end + 1 < args.len) end += 1;
    }
    const pattern = args[1..@min(end, args.len)];
    const command = if (end < args.len) std.mem.trim(u8, args[end + 1 ..], " ") else "";
    var re = vim.compileSearch(if (pattern.len == 0) (vim.last_search orelse return) else pattern) orelse {
        vim.setMessageText("E486: Invalid pattern");
        return;
    };
    defer re.deinit();
    if (pattern.len > 0) vim.setSearch(pattern, true);

    var rows: std.ArrayList(u32) = .empty;
    defer rows.deinit(ed.gpa);
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(ed.gpa);
    var row = range.first;
    while (row <= range.last) : (row += 1) {
        const text = ed.buffer.lineText(row, &line) catch return;
        if ((re.search(text, 0) != null) != invert) rows.append(ed.gpa, row) catch return;
    }
    if (rows.items.len == 0) {
        vim.setMessageText("E486: Pattern not found");
        return;
    }
    ed.buffer.beginGroup();
    defer ed.buffer.endGroup();
    var delta: i64 = 0;
    for (rows.items) |row0| {
        const cur: i64 = @as(i64, row0) + delta;
        if (cur < 0 or cur >= ed.buffer.lineCount()) continue;
        const before: i64 = ed.buffer.lineCount();
        ed.setSingleSelection(.caret(ed.buffer.lineStart(@intCast(cur))));
        if (command.len == 0) continue;
        execute(vim, ws, ed, command);
        delta += @as(i64, ed.buffer.lineCount()) - before;
    }
    vim.enterNormal(ed);
}
