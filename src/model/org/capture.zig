//! org-capture's "entry under a headline" target: where a captured TODO goes
//! in a file and the text inserted there, like Emacs with the template
//! `(file+headline "~/org/inbox.org" "Tasks") "* TODO %?\n  %U"`.

const std = @import("std");
const parser = @import("parser.zig");
const refile = @import("refile.zig");
const date = @import("date.zig");

const Allocator = std.mem.Allocator;

pub const Insertion = struct {
    offset: usize,
    /// Allocated with the allocator passed to `entry`.
    text: []u8,
    /// Replace up to here instead of inserting (a date tree rebuilt).
    end: ?usize = null,
};

/// A capture template from settings.json's "org_capture_templates":
/// `{"key": "t", "description": "Todo", "file": "~/org/inbox.org",
/// "heading": "Tasks", "template": "* TODO %?\n  %U", "datetree": false}`.
pub const Template = struct {
    key: []const u8 = "t",
    description: []const u8 = "Todo",
    file: []const u8 = "~/org/inbox.org",
    /// Where under the file (empty: the top level, or the date tree's root).
    heading: []const u8 = "",
    /// %? and %^{Prompt} the text typed, %U / %u inactive date with / without
    /// time, %T / %t active ones (%^T and the like too), %<%H:%M> the time in
    /// a C format, %a a link to where capture started, %i the selection, %f
    /// that file's name, %n the user name, %% a percent sign.
    template: []const u8 = "* TODO %?\n  %U",
    /// Files under year / month / day headings (org's file+olp+datetree).
    datetree: bool = false,
};

/// The templates in `json` (an array), allocated in `alloc`; malformed
/// entries are skipped.
pub fn parseTemplates(alloc: Allocator, json: []const u8) []const Template {
    if (json.len == 0) return &.{};
    const parsed = std.json.parseFromSliceLeaky([]Template, alloc, json, .{ .ignore_unknown_fields = true, .allocate = .alloc_always }) catch return &.{};
    return parsed;
}

pub const Context = struct {
    input: []const u8,
    now: struct { date: date.Date, time: date.Time },
    /// "file:/path::12" where capture started, or "".
    link: []const u8 = "",
    selection: []const u8 = "",
    file_name: []const u8 = "",
    /// %n.
    user: []const u8 = "",
};

/// The text typed for `%?` and `%^{Prompt}` is `ctx.input`; `%^t`-style
/// escapes, which org asks a date for, use today's here.
fn writeStamp(w: *std.Io.Writer, kind: u8, d: []const u8, time: date.Time) !bool {
    switch (kind) {
        'U' => try w.print("[{s} {d:0>2}:{d:0>2}]", .{ d, time.hour, time.minute }),
        'u' => try w.print("[{s}]", .{d}),
        'T' => try w.print("<{s} {d:0>2}:{d:0>2}>", .{ d, time.hour, time.minute }),
        't' => try w.print("<{s}>", .{d}),
        else => return false,
    }
    return true;
}

/// `%<format>`: the time in a C format (%Y %y %m %d %e %H %M %S %a %A %b %B %%).
fn writeFormatted(w: *std.Io.Writer, format: []const u8, now: anytype) !void {
    const d = now.date;
    const year: u32 = @intCast(d.year);
    var i: usize = 0;
    while (i < format.len) : (i += 1) {
        if (format[i] != '%' or i + 1 >= format.len) {
            try w.writeByte(format[i]);
            continue;
        }
        i += 1;
        switch (format[i]) {
            'Y' => try w.print("{d}", .{year}),
            'y' => try w.print("{d:0>2}", .{year % 100}),
            'm' => try w.print("{d:0>2}", .{d.month}),
            'd' => try w.print("{d:0>2}", .{d.day}),
            'e' => try w.print("{d: >2}", .{d.day}),
            'H' => try w.print("{d:0>2}", .{now.time.hour}),
            'M' => try w.print("{d:0>2}", .{now.time.minute}),
            'S' => try w.writeAll("00"),
            'a' => try w.writeAll(date.day_names[d.weekday()][0..3]),
            'A' => try w.writeAll(date.day_names[d.weekday()]),
            'b' => try w.writeAll(date.month_names[d.month - 1][0..3]),
            'B' => try w.writeAll(date.month_names[d.month - 1]),
            '%' => try w.writeByte('%'),
            else => |other| try w.print("%{c}", .{other}),
        }
    }
}

/// The template with its %-escapes filled in.
pub fn expand(gpa: Allocator, template: []const u8, ctx: Context) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(gpa);
    errdefer out.deinit();
    const w = &out.writer;
    var dbuf: [32]u8 = undefined;
    const d = ctx.now.date.format(&dbuf);
    var i: usize = 0;
    while (i < template.len) : (i += 1) {
        const ch = template[i];
        if (ch != '%' or i + 1 >= template.len) {
            try w.writeByte(ch);
            continue;
        }
        i += 1;
        switch (template[i]) {
            '?' => try w.writeAll(ctx.input),
            'U', 'u', 'T', 't' => |kind| _ = try writeStamp(w, kind, d, ctx.now.time),
            '^' => {
                if (i + 1 < template.len and template[i + 1] == '{') {
                    // %^{Prompt|default|other}: the typed text, else the first choice.
                    const close = std.mem.indexOfScalarPos(u8, template, i + 2, '}') orelse {
                        try w.writeAll("%^");
                        continue;
                    };
                    var parts = std.mem.splitScalar(u8, template[i + 2 .. close], '|');
                    _ = parts.next();
                    const default = parts.next() orelse "";
                    try w.writeAll(if (ctx.input.len > 0) ctx.input else default);
                    i = close;
                } else if (i + 1 < template.len) {
                    i += 1;
                    // %^g asks for tags: none here.
                    if (template[i] != 'g' and !try writeStamp(w, template[i], d, ctx.now.time)) try w.print("%^{c}", .{template[i]});
                } else try w.writeAll("%^");
            },
            '<' => {
                const close = std.mem.indexOfScalarPos(u8, template, i + 1, '>') orelse {
                    try w.writeAll("%<");
                    continue;
                };
                try writeFormatted(w, template[i + 1 .. close], ctx.now);
                i = close;
            },
            'n' => try w.writeAll(ctx.user),
            'a' => if (ctx.link.len > 0) try w.print("[[{s}]]", .{ctx.link}),
            'i' => try w.writeAll(ctx.selection),
            'f' => try w.writeAll(ctx.file_name),
            '%' => try w.writeByte('%'),
            else => |other| try w.print("%{c}", .{other}),
        }
    }
    return out.toOwnedSlice();
}

/// Where `entry_text` (an expanded template) goes in `text` for `t`.
pub fn place(gpa: Allocator, text: []const u8, t: Template, entry_text: []const u8, today: date.Date) !Insertion {
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const a = arena.allocator();
    if (!t.datetree) {
        if (t.heading.len > 0) if (findSubtree(text, t.heading)) |sub| {
            const moved = try refile.relevel(a, std.mem.trimEnd(u8, entry_text, "\n"), sub.level + 1);
            return .{ .offset = sub.end, .text = try std.fmt.allocPrint(gpa, "\n{s}", .{moved}) };
        };
        // At the end of the file, under a new heading if one was named.
        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(gpa);
        if (text.len > 0 and text[text.len - 1] != '\n') try out.append(gpa, '\n');
        var level: usize = 1;
        if (t.heading.len > 0) {
            try out.print(gpa, "* {s}\n", .{t.heading});
            level = 2;
        }
        try out.appendSlice(gpa, try refile.relevel(a, std.mem.trimEnd(u8, entry_text, "\n"), level));
        try out.append(gpa, '\n');
        return .{ .offset = text.len, .text = try out.toOwnedSlice(gpa) };
    }
    // The date tree: year, month and day headings, made where missing.
    var dbuf: [16]u8 = undefined;
    const year = try std.fmt.allocPrint(a, "{d}", .{@as(u32, @intCast(today.year))});
    const month = try std.fmt.allocPrint(a, "{d}-{d:0>2} {s}", .{ @as(u32, @intCast(today.year)), today.month, date.month_names[today.month - 1] });
    const day = try std.fmt.allocPrint(a, "{s} {s}", .{ (try std.fmt.bufPrint(&dbuf, "{d}-{d:0>2}-{d:0>2}", .{ @as(u32, @intCast(today.year)), today.month, today.day })), date.day_names[today.weekday()] });
    var cur: []u8 = try a.dupe(u8, text);
    var path: []const u8 = "";
    const parts = [_][]const u8{ t.heading, year, month, day };
    for (parts) |part| {
        if (part.len == 0) continue;
        const next_path = if (path.len == 0) part else try std.fmt.allocPrint(a, "{s}/{s}", .{ path, part });
        if ((try refile.find(a, cur, next_path)) == null) {
            const under = if (path.len == 0) null else try refile.find(a, cur, path);
            const ins = try refile.insertion(a, cur, under, try std.fmt.allocPrint(a, "* {s}\n", .{part}));
            cur = try std.mem.concat(a, u8, &.{ cur[0..ins.offset], ins.text, cur[ins.offset..] });
        }
        path = next_path;
    }
    const under = try refile.find(a, cur, path);
    const ins = try refile.insertion(a, cur, under, entry_text);
    const out = try std.mem.concat(gpa, u8, &.{ cur[0..ins.offset], ins.text, cur[ins.offset..] });
    return .{ .offset = 0, .end = text.len, .text = out };
}

/// Files `title` as a TODO stamped with the inactive timestamp `stamp`
/// ("2026-10-21 Wed 14:03"): the last child of the first heading titled
/// `heading`, which is added at the end of the file if it is missing. An
/// empty `heading` puts a top-level entry at the end of the file.
pub fn entry(gpa: Allocator, text: []const u8, heading: []const u8, title: []const u8, stamp: []const u8) !Insertion {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    if (heading.len > 0) if (findSubtree(text, heading)) |sub| {
        try out.append(gpa, '\n');
        try writeEntry(gpa, &out, sub.level + 1, title, stamp);
        return .{ .offset = sub.end, .text = try out.toOwnedSlice(gpa) };
    };
    // At the end of the file, on lines of its own.
    if (text.len > 0 and text[text.len - 1] != '\n') try out.append(gpa, '\n');
    var level: usize = 1;
    if (heading.len > 0) {
        try out.print(gpa, "* {s}\n", .{heading});
        level = 2;
    }
    try writeEntry(gpa, &out, level, title, stamp);
    try out.append(gpa, '\n');
    return .{ .offset = text.len, .text = try out.toOwnedSlice(gpa) };
}

fn writeEntry(gpa: Allocator, out: *std.ArrayList(u8), level: usize, title: []const u8, stamp: []const u8) !void {
    try out.appendNTimes(gpa, '*', level);
    try out.print(gpa, " TODO {s}\n  [{s}]", .{ title, stamp });
}

/// The level of the first heading titled `heading`, and the end of the last
/// non-blank line of its subtree.
fn findSubtree(text: []const u8, heading: []const u8) ?struct { level: usize, end: usize } {
    var level: ?usize = null;
    var end: usize = 0;
    var start: usize = 0;
    while (true) {
        const nl = std.mem.indexOfScalarPos(u8, text, start, '\n') orelse text.len;
        const line = text[start..nl];
        if (parser.parseHeadingLine(line, .{})) |h| {
            if (level) |l| {
                if (h.level <= l) break;
            } else if (std.mem.eql(u8, h.title, heading)) {
                level = h.level;
            }
        }
        if (level != null and std.mem.trim(u8, line, " \t").len > 0) end = nl;
        if (nl == text.len) break;
        start = nl + 1;
    }
    return .{ .level = level orelse return null, .end = end };
}

test "templates" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const ts = parseTemplates(a,
        \\[{"key": "j", "description": "Journal", "file": "~/org/journal.org", "template": "* %?\n%U", "datetree": true},
        \\ {"key": "n", "file": "~/org/notes.org", "heading": "Notes", "template": "* %? :note:\n  From %a"}]
    );
    try std.testing.expectEqual(@as(usize, 2), ts.len);
    try std.testing.expect(ts[0].datetree);
    const ctx: Context = .{ .input = "Lunch", .now = .{ .date = .{ .year = 2026, .month = 10, .day = 22 }, .time = .{ .hour = 12, .minute = 5 } }, .link = "file:/x.zig::3" };
    const e = try expand(a, ts[1].template, ctx);
    try std.testing.expectEqualStrings("* Lunch :note:\n  From [[file:/x.zig::3]]", e);
    const j = try expand(a, ts[0].template, ctx);
    const ins = try place(a, "* 2026\n** 2026-09 September\n", ts[0], j, ctx.now.date);
    try std.testing.expectEqualStrings(
        \\* 2026
        \\** 2026-09 September
        \\** 2026-10 October
        \\*** 2026-10-22 Thursday
        \\**** Lunch
        \\[2026-10-22 Thu 12:05]
        \\
    , ins.text);
    const n = try place(a, "* Notes\n** Old\n* Other\n", ts[1], e, ctx.now.date);
    const out = try std.mem.concat(a, u8, &.{ "* Notes\n** Old\n* Other\n"[0..n.offset], n.text, "* Notes\n** Old\n* Other\n"[n.offset..] });
    try std.testing.expectEqualStrings("* Notes\n** Old\n** Lunch :note:\n  From [[file:/x.zig::3]]\n* Other\n", out);
}

fn expectCapture(before: []const u8, heading: []const u8, after: []const u8) !void {
    const gpa = std.testing.allocator;
    const ins = try entry(gpa, before, heading, "Call Sam", "2026-10-21 Wed 14:03");
    defer gpa.free(ins.text);
    const result = try std.mem.concat(gpa, u8, &.{ before[0..ins.offset], ins.text, before[ins.offset..] });
    defer gpa.free(result);
    try std.testing.expectEqualStrings(after, result);
}

test "capture under an existing heading" {
    try expectCapture(
        \\* Tasks
        \\
        \\** TODO Create an org schedule
        \\  [2026-09-24 Thu 11:02]
        \\
        \\
    ,
        "Tasks",
        \\* Tasks
        \\
        \\** TODO Create an org schedule
        \\  [2026-09-24 Thu 11:02]
        \\** TODO Call Sam
        \\  [2026-10-21 Wed 14:03]
        \\
        \\
    );
    // The subtree ends at the next heading of the same level; tags don't matter.
    try expectCapture(
        \\* Tasks   :inbox:
        \\** DONE Old
        \\
        \\* Notes
        \\text
    ,
        "Tasks",
        \\* Tasks   :inbox:
        \\** DONE Old
        \\** TODO Call Sam
        \\  [2026-10-21 Wed 14:03]
        \\
        \\* Notes
        \\text
    );
}

test "capture adds a missing heading" {
    try expectCapture("", "Tasks",
        \\* Tasks
        \\** TODO Call Sam
        \\  [2026-10-21 Wed 14:03]
        \\
    );
    try expectCapture("* Notes", "Tasks",
        \\* Notes
        \\* Tasks
        \\** TODO Call Sam
        \\  [2026-10-21 Wed 14:03]
        \\
    );
    try expectCapture("* Notes\n", "",
        \\* Notes
        \\* TODO Call Sam
        \\  [2026-10-21 Wed 14:03]
        \\
    );
}

test "template escapes" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const ctx: Context = .{
        .input = "Report",
        .now = .{ .date = .{ .year = 2026, .month = 11, .day = 5 }, .time = .{ .hour = 9, .minute = 7 } },
        .user = "joey",
        .file_name = "x.org",
    };
    try std.testing.expectEqualStrings("* TODO Report", try expand(a, "* TODO %^{Task}", ctx));
    // Without typed text the first choice stands.
    var empty = ctx;
    empty.input = "";
    try std.testing.expectEqualStrings("size: ebook", try expand(a, "size: %^{Size|ebook|audio}", empty));
    try std.testing.expectEqualStrings("<2026-11-05 Thu 09:07> [2026-11-05 Thu]", try expand(a, "%^T %^u", ctx));
    try std.testing.expectEqualStrings("09:07 Thursday  5 Nov 26 by joey in x.org", try expand(a, "%<%H:%M %A %e %b %y> by %n in %f", ctx));
    try std.testing.expectEqualStrings("100% sure %z", try expand(a, "100%% sure %z", ctx));
    // %^g (tags) leaves nothing behind.
    try std.testing.expectEqualStrings("* A ", try expand(a, "* A %^g", ctx));
}
