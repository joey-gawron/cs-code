//! Org agenda: a generated, read-only buffer listing scheduled items,
//! deadlines and appointments per day (week / day views) or every open TODO,
//! gathered from the project's .org files, open org buffers and ~/org.

const std = @import("std");
const parser = @import("parser.zig");
const date = @import("date.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const syntax_mod = @import("../syntax/syntax.zig");
const rope_mod = @import("../text/rope.zig");

const Allocator = std.mem.Allocator;
const Date = date.Date;

/// week / day: dated items; todo: open TODOs (`query`: "NEXT|WAITING"
/// limits the keywords); search: headings containing every word of
/// `query`; tags: headings matching the tag expression `query`.
pub const View = enum { week, day, todo, search, tags };

/// Narrows any view: by tags ("+work-home"), by text, by category.
pub const Filter = struct {
    tags: []const u8 = "",
    text: []const u8 = "",
    category: []const u8 = "",

    pub fn active(self: Filter) bool {
        return self.tags.len > 0 or self.text.len > 0 or self.category.len > 0;
    }
};

pub const Source = struct {
    path: []const u8,
    text: []const u8,
};

/// Where an agenda line points.
pub const Target = struct {
    path: []const u8,
    offset: usize,
    /// The item's category (agenda `<` filters by it).
    category: []const u8 = "",
};

const deadline_warning_days = 14;

/// How a docked agenda row colours its second line: overdue things in the
/// error colour, deadlines coming up in the warning colour.
pub const ItemKind = enum { normal, overdue, upcoming };

/// One agenda entry as the docked window shows it.
pub const Item = struct {
    /// The TODO keyword, "" when the heading has none.
    todo: []const u8 = "",
    done: bool = false,
    priority: ?u8 = null,
    /// The title without its timestamps.
    title: []const u8 = "",
    /// The heading's own tags, ":a:b:" or "".
    tags: []const u8 = "",
    category: []const u8 = "",
    /// Why the entry is on its day: "09:30", "Scheduled", "Overdue 2 d.".
    label: []const u8 = "",
    kind: ItemKind = .normal,
    target: Target,
    /// 0-based row of the heading in its file.
    line: u32 = 0,
    /// The ancestors' titles, "Project / Phase", "" at the top level.
    outline: []const u8 = "",
    scheduled: []const u8 = "",
    deadline: []const u8 = "",
    closed: []const u8 = "",
    /// The entry's own text: planning lines and drawers left out.
    notes: []const u8 = "",
};

/// A day (week and day views), a file (the to-do list) or everything (search).
pub const Group = struct {
    header: []const u8,
    day: ?Date = null,
    items: []const Item,
};

const Entry = struct {
    sort_time: i32,
    priority: u8,
    kind_rank: u8,
    text: []const u8,
    target: Target,
    item: Item,
};

/// A parsed source kept for the length of a build.
const DocRef = struct { doc: parser.Document, path: []const u8, text: []const u8 };

pub const Agenda = struct {
    gpa: Allocator,
    arena: std.heap.ArenaAllocator,
    view: View = .week,
    /// First day shown (Monday for the week view).
    start: Date,
    text: std.ArrayList(u8) = .empty,
    /// Target for each line of `text` (null for headers / blank lines).
    targets: std.ArrayList(?Target) = .empty,
    /// The same entries grouped for the docked window; the items live in
    /// `arena` and last until the next build.
    groups: std.ArrayList(Group) = .empty,
    /// The search words, tag expression or TODO keywords (owned).
    query: []u8 = &.{},
    /// Owned strings.
    filter: Filter = .{},
    /// A custom command's name for the header ("" for the built-in views).
    title: []u8 = &.{},

    pub fn init(gpa: Allocator, view: View) Agenda {
        const today = Date.today();
        return .{ .gpa = gpa, .arena = .init(gpa), .view = view, .start = periodStart(view, today) };
    }

    pub fn deinit(self: *Agenda) void {
        self.text.deinit(self.gpa);
        self.targets.deinit(self.gpa);
        self.groups.deinit(self.gpa);
        self.arena.deinit();
        self.gpa.free(self.query);
        self.gpa.free(self.title);
        self.setFilter(.{}) catch {};
    }

    fn replace(gpa: Allocator, field: *[]u8, value: []const u8) !void {
        const copy = try gpa.dupe(u8, value);
        gpa.free(field.*);
        field.* = copy;
    }

    pub fn setQuery(self: *Agenda, view: View, query: []const u8, title: []const u8) !void {
        try replace(self.gpa, &self.query, query);
        try replace(self.gpa, &self.title, title);
        self.setView(view);
    }

    /// Copies `f`'s strings (the old ones are freed).
    pub fn setFilter(self: *Agenda, f: Filter) !void {
        const new: Filter = .{
            .tags = try self.gpa.dupe(u8, f.tags),
            .text = try self.gpa.dupe(u8, f.text),
            .category = try self.gpa.dupe(u8, f.category),
        };
        self.gpa.free(self.filter.tags);
        self.gpa.free(self.filter.text);
        self.gpa.free(self.filter.category);
        self.filter = new;
    }

    fn passes(self: *const Agenda, h: parser.Heading) bool {
        const f = self.filter;
        if (f.tags.len > 0 and !tagMatch(f.tags, h.all_tags)) return false;
        if (f.category.len > 0 and !std.mem.eql(u8, f.category, h.category)) return false;
        if (f.text.len > 0 and !containsWords(h.title, f.text)) return false;
        return true;
    }

    fn periodStart(view: View, d: Date) Date {
        return switch (view) {
            .week => d.addDays(-@as(i64, d.weekday())),
            else => d,
        };
    }

    pub fn setView(self: *Agenda, view: View) void {
        const anchor = if (self.view == .week and view == .day) Date.today() else self.start;
        self.view = view;
        self.start = periodStart(view, anchor);
    }

    pub fn goToday(self: *Agenda) void {
        self.start = periodStart(self.view, Date.today());
    }

    pub fn shift(self: *Agenda, forward: bool) void {
        const n: i64 = switch (self.view) {
            .week => 7,
            .day => 1,
            .todo, .search, .tags => 0,
        };
        self.start = self.start.addDays(if (forward) n else -n);
    }

    pub fn targetAt(self: *const Agenda, row: u32) ?Target {
        if (row >= self.targets.items.len) return null;
        return self.targets.items[row];
    }

    fn line(self: *Agenda, target: ?Target, comptime fmt: []const u8, args: anytype) !void {
        var w = std.Io.Writer.Allocating.fromArrayList(self.gpa, &self.text);
        defer self.text = w.toArrayList();
        try w.writer.print(fmt, args);
        try w.writer.writeByte('\n');
        try self.targets.append(self.gpa, target);
    }

    pub fn build(self: *Agenda, sources: []const Source) !void {
        _ = self.arena.reset(.retain_capacity);
        self.text.clearRetainingCapacity();
        self.targets.clearRetainingCapacity();
        self.groups.clearRetainingCapacity();
        const alloc = self.arena.allocator();

        var docs: std.ArrayList(DocRef) = .empty;
        defer {
            for (docs.items) |*d| d.doc.deinit();
            docs.deinit(self.gpa);
        }
        for (sources) |src| {
            const doc = try parser.parse(self.gpa, src.text, std.fs.path.basename(src.path));
            try docs.append(self.gpa, .{ .doc = doc, .path = try alloc.dupe(u8, src.path), .text = src.text });
        }

        const today = Date.today();
        if (self.filter.active()) try self.line(null, "Filter: {s}{s}{s}{s}{s}   (| clears)", .{
            self.filter.tags,
            if (self.filter.text.len > 0) " \"" else "",
            self.filter.text,
            if (self.filter.text.len > 0) "\" " else "",
            self.filter.category,
        });
        switch (self.view) {
            .search, .tags => {
                if (self.view == .search) {
                    try self.line(null, "Search words: {s}", .{self.query});
                } else try self.line(null, "{s}Headlines with TAGS match: {s}", .{ self.title, self.query });
                var entries: std.ArrayList(Entry) = .empty;
                for (docs.items) |*d| for (d.doc.headings.items, 0..) |h, hi| {
                    const hit = if (self.view == .search) containsWords(h.title, self.query) else tagMatch(self.query, h.all_tags);
                    if (!hit or !self.passes(h)) continue;
                    try entries.append(alloc, .{
                        .sort_time = 0,
                        .priority = h.priority orelse 'B',
                        .kind_rank = 0,
                        .text = try formatEntry(alloc, h.category, "", h),
                        .target = .{ .path = d.path, .offset = h.offset, .category = try alloc.dupe(u8, h.category) },
                        .item = try makeItem(alloc, d, hi, "", .normal),
                    });
                };
                for (entries.items) |e| try self.line(e.target, "{s}", .{e.text});
                if (entries.items.len == 0) try self.line(null, "  (nothing matches)", .{});
                try self.addGroup(alloc, "", null, entries.items, null);
            },
            .todo => {
                if (self.query.len > 0) {
                    try self.line(null, "{s}Global list of TODO items of type: {s}", .{ self.title, self.query });
                } else try self.line(null, "Global list of TODO items of type: ALL", .{});
                var entries: std.ArrayList(Entry) = .empty;
                for (docs.items) |*d| for (d.doc.headings.items, 0..) |h, hi| {
                    const kw = h.todo orelse continue;
                    if (d.doc.keywords.isDone(kw)) continue;
                    if (self.query.len > 0 and !keywordIn(kw, self.query)) continue;
                    if (!self.passes(h)) continue;
                    try entries.append(alloc, .{
                        .sort_time = 0,
                        .priority = h.priority orelse 'B',
                        .kind_rank = 0,
                        .text = try formatEntry(alloc, h.category, "", h),
                        .target = .{ .path = d.path, .offset = h.offset, .category = try alloc.dupe(u8, h.category) },
                        .item = try makeItem(alloc, d, hi, "", .normal),
                    });
                };
                sortEntries(entries.items);
                for (entries.items) |e| try self.line(e.target, "{s}", .{e.text});
                if (entries.items.len == 0) try self.line(null, "  (no open TODO items)", .{});
                // The window lists them file by file.
                for (docs.items) |d| try self.addGroup(alloc, std.fs.path.basename(d.path), null, entries.items, d.path);
            },
            .week, .day => {
                const days: i64 = if (self.view == .week) 7 else 1;
                try self.line(null, "{s}-agenda (W{d:0>2}):", .{ if (self.view == .week) "Week" else "Day", self.start.isoWeek() });
                var i: i64 = 0;
                while (i < days) : (i += 1) {
                    const day = self.start.addDays(i);
                    if (day.weekday() == 0 or i == 0) {
                        try self.line(null, "{s: <10} {d: >2} {s} {d} W{d:0>2}", .{ date.day_names[day.weekday()], day.day, date.month_names[day.month - 1], @as(u32, @intCast(day.year)), day.isoWeek() });
                    } else {
                        try self.line(null, "{s: <10} {d: >2} {s} {d}", .{ date.day_names[day.weekday()], day.day, date.month_names[day.month - 1], @as(u32, @intCast(day.year)) });
                    }
                    var entries: std.ArrayList(Entry) = .empty;
                    for (docs.items) |*d| for (d.doc.headings.items, 0..) |h, hi| {
                        if (!self.passes(h)) continue;
                        try collectDay(alloc, &entries, d, hi, day, today);
                    };
                    sortEntries(entries.items);
                    for (entries.items) |e| try self.line(e.target, "{s}", .{e.text});
                    const header = try std.fmt.allocPrint(alloc, "{s} {d} {s}", .{ date.day_names[day.weekday()], day.day, date.month_names[day.month - 1] });
                    try self.addGroup(alloc, header, day, entries.items, null);
                }
            },
        }
    }

    /// Appends a group of `entries`' items (only those in `path`, when given).
    fn addGroup(self: *Agenda, alloc: Allocator, header: []const u8, day: ?Date, entries: []const Entry, path: ?[]const u8) !void {
        var items: std.ArrayList(Item) = .empty;
        for (entries) |e| {
            if (path) |p| if (!std.mem.eql(u8, p, e.target.path)) continue;
            try items.append(alloc, e.item);
        }
        // A file with nothing to do has no group; a day always has one.
        if (path != null and items.items.len == 0) return;
        try self.groups.append(self.gpa, .{ .header = header, .day = day, .items = items.items });
    }
};

/// Org's tag match: alternatives separated by '|', each a run of +tag
/// (must have) and -tag (must not have); a bare first tag is +tag.
/// "work+urgent-someday|home". `tags` is ":a:b:".
pub fn tagMatch(expr: []const u8, tags: []const u8) bool {
    var alts = std.mem.splitScalar(u8, expr, '|');
    while (alts.next()) |alt| {
        var ok = true;
        var any = false;
        var i: usize = 0;
        while (i < alt.len) {
            var want = true;
            if (alt[i] == '+' or alt[i] == '-') {
                want = alt[i] == '+';
                i += 1;
            }
            const start = i;
            while (i < alt.len and alt[i] != '+' and alt[i] != '-') i += 1;
            const tag = std.mem.trim(u8, alt[start..i], " :");
            if (tag.len == 0) continue;
            any = true;
            if (hasTag(tags, tag) != want) ok = false;
        }
        if (any and ok) return true;
    }
    return false;
}

fn hasTag(tags: []const u8, tag: []const u8) bool {
    var it = std.mem.tokenizeScalar(u8, tags, ':');
    while (it.next()) |t| if (std.ascii.eqlIgnoreCase(t, tag)) return true;
    return false;
}

/// Every space-separated word of `words` occurs in `text` (ignoring case).
fn containsWords(text: []const u8, words: []const u8) bool {
    var it = std.mem.tokenizeScalar(u8, words, ' ');
    while (it.next()) |w| if (std.ascii.findIgnoreCase(text, w) == null) return false;
    return true;
}

/// `kw` is one of "A|B|C".
fn keywordIn(kw: []const u8, list: []const u8) bool {
    var it = std.mem.tokenizeAny(u8, list, "| ");
    while (it.next()) |k| if (std.mem.eql(u8, k, kw)) return true;
    return false;
}

fn sortEntries(entries: []Entry) void {
    std.mem.sort(Entry, entries, {}, struct {
        fn lt(_: void, a: Entry, b: Entry) bool {
            if (a.sort_time != b.sort_time) return a.sort_time < b.sort_time;
            if (a.kind_rank != b.kind_rank) return a.kind_rank < b.kind_rank;
            return a.priority < b.priority;
        }
    }.lt);
}

fn formatEntry(alloc: Allocator, category: []const u8, extra: []const u8, h: parser.Heading) ![]const u8 {
    var w: std.Io.Writer.Allocating = .init(alloc);
    var cat_buf: [16]u8 = undefined;
    const cat = std.fmt.bufPrint(&cat_buf, "{s}:", .{category[0..@min(category.len, 10)]}) catch "";
    try w.writer.print("  {s: <12}{s: <20}", .{ cat, extra });
    if (h.todo) |kw| try w.writer.print("{s} ", .{kw});
    if (h.priority) |p| try w.writer.print("[#{c}] ", .{p});
    // Titles show without their timestamps (the time is in the extra column).
    var i: usize = 0;
    var last_space = true;
    while (i < h.title.len) {
        if ((h.title[i] == '<' or h.title[i] == '[') and date.parseTimestamp(h.title, i) != null) {
            i = date.parseTimestamp(h.title, i).?.end;
            continue;
        }
        if (h.title[i] == ' ' and last_space) {
            i += 1;
            continue;
        }
        last_space = h.title[i] == ' ';
        try w.writer.writeByte(h.title[i]);
        i += 1;
    }
    if (last_space and w.written().len > 0 and w.written()[w.written().len - 1] == ' ') w.shrinkRetainingCapacity(w.written().len - 1);
    if (h.tags.len > 0) try w.writer.print("   {s}", .{h.tags});
    return w.written();
}

fn timeLabel(buf: []u8, ts: date.Timestamp) []const u8 {
    const t = ts.time orelse return "";
    if (ts.end_time) |e| return std.fmt.bufPrint(buf, "{d:0>2}:{d:0>2}-{d:0>2}:{d:0>2}", .{ t.hour, t.minute, e.hour, e.minute }) catch "";
    return std.fmt.bufPrint(buf, "{d:0>2}:{d:0>2}......", .{ t.hour, t.minute }) catch "";
}

fn collectDay(alloc: Allocator, out: *std.ArrayList(Entry), d: *const DocRef, hi: usize, day: Date, today: Date) !void {
    const doc = &d.doc;
    const h = doc.headings.items[hi];
    const done = if (h.todo) |kw| doc.keywords.isDone(kw) else false;
    const is_today = day.eql(today);
    const target: Target = .{ .path = d.path, .offset = h.offset, .category = try alloc.dupe(u8, h.category) };
    const prio = h.priority orelse 'B';
    var buf: [32]u8 = undefined;

    // The order within a day, as cs-code lists it: timed entries by time,
    // then overdue deadlines, deadlines due, scheduled entries not done since
    // an earlier day, scheduled entries, deadlines coming up, appointments.
    if (h.deadline) |dl| {
        const delta = dl.date.toDays() - day.toDays();
        const warn: i64 = dl.warn_days orelse deadline_warning_days;
        if (dl.occursOn(day)) {
            try out.append(alloc, .{ .sort_time = timeKey(dl), .priority = prio, .kind_rank = 1, .text = try formatEntry(alloc, h.category, "Deadline:", h), .target = target, .item = try makeItem(alloc, d, hi, if (dl.time != null) timeLabel(&buf, dl) else "Deadline", .normal) });
        } else if (is_today and !done and delta > 0 and delta <= warn) {
            const label = std.fmt.bufPrint(&buf, "Due in {d} d.", .{@as(u64, @intCast(delta))}) catch "";
            var wbuf: [48]u8 = undefined;
            const when = dl.date.format(&wbuf);
            const long = try std.fmt.allocPrint(alloc, "{s} ({s} {d} {s})", .{ label, when[11..14], dl.date.day, date.month_names[dl.date.month - 1][0..3] });
            try out.append(alloc, .{ .sort_time = untimed, .priority = prio, .kind_rank = 4, .text = try formatEntry(alloc, h.category, label, h), .target = target, .item = try makeItem(alloc, d, hi, long, .upcoming) });
        } else if (is_today and !done and delta < 0 and dl.repeat == null) {
            const label = std.fmt.bufPrint(&buf, "Overdue {d} d.", .{@as(u64, @intCast(-delta))}) catch "";
            try out.append(alloc, .{ .sort_time = untimed, .priority = prio, .kind_rank = 0, .text = try formatEntry(alloc, h.category, label, h), .target = target, .item = try makeItem(alloc, d, hi, label, .overdue) });
        }
    }
    if (h.scheduled) |sc| {
        const overdue = today.toDays() - sc.date.toDays();
        if (sc.occursOn(day)) {
            const label = if (sc.time != null) timeLabel(&buf, sc) else "Scheduled:";
            const short = if (sc.time != null) std.mem.trimEnd(u8, label, ".") else "Scheduled";
            try out.append(alloc, .{ .sort_time = timeKey(sc), .priority = prio, .kind_rank = 3, .text = try formatEntry(alloc, h.category, label, h), .target = target, .item = try makeItem(alloc, d, hi, short, .normal) });
        } else if (is_today and !done and overdue > 0 and sc.repeat == null) {
            const label = std.fmt.bufPrint(&buf, "Scheduled {d} d. ago", .{@as(u64, @intCast(overdue))}) catch "";
            const short = try std.fmt.allocPrint(alloc, "Scheduled {d} d. ago", .{@as(u64, @intCast(overdue))});
            try out.append(alloc, .{ .sort_time = untimed, .priority = prio, .kind_rank = 2, .text = try formatEntry(alloc, h.category, label, h), .target = target, .item = try makeItem(alloc, d, hi, short, .overdue) });
        }
    }
    for (h.timestamps.items) |ts| {
        if (!ts.occursOn(day)) continue;
        const label = timeLabel(&buf, ts);
        try out.append(alloc, .{ .sort_time = timeKey(ts), .priority = prio, .kind_rank = 5, .text = try formatEntry(alloc, h.category, label, h), .target = target, .item = try makeItem(alloc, d, hi, std.mem.trimEnd(u8, label, "."), .normal) });
    }
}

/// The heading's row for the docked window; strings are copied into `alloc`.
fn makeItem(alloc: Allocator, d: *const DocRef, hi: usize, label: []const u8, kind: ItemKind) !Item {
    const h = d.doc.headings.items[hi];
    var tbuf: [96]u8 = undefined;
    return .{
        .todo = if (h.todo) |kw| try alloc.dupe(u8, kw) else "",
        .done = if (h.todo) |kw| d.doc.keywords.isDone(kw) else false,
        .priority = h.priority,
        .title = try cleanTitle(alloc, h.title),
        .tags = try alloc.dupe(u8, h.tags),
        .category = try alloc.dupe(u8, h.category),
        .label = try alloc.dupe(u8, label),
        .kind = kind,
        .target = .{ .path = d.path, .offset = h.offset, .category = try alloc.dupe(u8, h.category) },
        .line = h.line,
        .outline = try outlinePath(alloc, d.doc.headings.items, hi),
        .scheduled = if (h.scheduled) |t| try alloc.dupe(u8, formatTimestamp(&tbuf, t)) else "",
        .deadline = if (h.deadline) |t| try alloc.dupe(u8, formatTimestamp(&tbuf, t)) else "",
        .closed = if (h.closed) |t| try alloc.dupe(u8, formatTimestamp(&tbuf, t)) else "",
        .notes = try notesOf(alloc, d.text, h.offset),
    };
}

/// The title without its timestamps (the time is in the label), in `alloc`.
fn cleanTitle(alloc: Allocator, title: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var i: usize = 0;
    while (i < title.len) {
        if ((title[i] == '<' or title[i] == '[') and date.parseTimestamp(title, i) != null) {
            i = date.parseTimestamp(title, i).?.end;
            continue;
        }
        try out.append(alloc, title[i]);
        i += 1;
    }
    return std.mem.trim(u8, out.items, " ");
}

/// "Project / Phase": the titles of the headings above `hi`, outermost first.
fn outlinePath(alloc: Allocator, headings: []const parser.Heading, hi: usize) ![]const u8 {
    var parts: [8][]const u8 = undefined;
    var n: usize = 0;
    var level = headings[hi].level;
    var j = hi;
    while (j > 0 and level > 1 and n < parts.len) {
        j -= 1;
        if (headings[j].level < level) {
            parts[n] = headings[j].title;
            n += 1;
            level = headings[j].level;
        }
    }
    var out: std.ArrayList(u8) = .empty;
    while (n > 0) {
        n -= 1;
        if (out.items.len > 0) try out.appendSlice(alloc, " / ");
        try out.appendSlice(alloc, parts[n]);
    }
    return out.items;
}

const max_notes = 2000;

fn isHeadingLine(ln: []const u8) bool {
    const stars = std.mem.indexOfNone(u8, ln, "*") orelse return false;
    return stars > 0 and ln[stars] == ' ';
}

/// The text under the heading line at `offset`, up to the next heading:
/// planning lines and drawers (:PROPERTIES:, :LOGBOOK:) are left out.
fn notesOf(alloc: Allocator, text: []const u8, offset: usize) ![]const u8 {
    if (offset >= text.len) return "";
    var out: std.ArrayList(u8) = .empty;
    var pos = std.mem.indexOfScalarPos(u8, text, offset, '\n') orelse return "";
    pos += 1;
    var in_drawer = false;
    while (pos < text.len and out.items.len < max_notes) {
        const end = std.mem.indexOfScalarPos(u8, text, pos, '\n') orelse text.len;
        const ln = std.mem.trimEnd(u8, text[pos..end], "\r");
        pos = end + 1;
        if (isHeadingLine(ln)) break;
        const t = std.mem.trim(u8, ln, " \t");
        if (in_drawer) {
            if (std.ascii.eqlIgnoreCase(t, ":END:")) in_drawer = false;
            continue;
        }
        if (std.ascii.eqlIgnoreCase(t, ":PROPERTIES:") or std.ascii.eqlIgnoreCase(t, ":LOGBOOK:")) {
            in_drawer = true;
            continue;
        }
        if (std.mem.startsWith(u8, t, "SCHEDULED:") or std.mem.startsWith(u8, t, "DEADLINE:") or std.mem.startsWith(u8, t, "CLOSED:")) continue;
        try out.appendSlice(alloc, t);
        try out.append(alloc, '\n');
    }
    return std.mem.trim(u8, out.items, "\n");
}

/// "<2026-11-03 Tue 09:30-10:00 +1w -2d>", as org writes it.
pub fn formatTimestamp(buf: []u8, ts: date.Timestamp) []const u8 {
    var w: std.Io.Writer = .fixed(buf);
    var dbuf: [24]u8 = undefined;
    w.writeByte(if (ts.active) '<' else '[') catch return "";
    w.writeAll(ts.date.format(&dbuf)) catch return "";
    if (ts.time) |t| {
        w.print(" {d:0>2}:{d:0>2}", .{ t.hour, t.minute }) catch return "";
        if (ts.end_time) |e| w.print("-{d:0>2}:{d:0>2}", .{ e.hour, e.minute }) catch return "";
    }
    if (ts.repeat) |r| {
        const prefix = switch (r.kind) {
            .cumulate => "+",
            .catch_up => "++",
            .restart => ".+",
        };
        const unit: u8 = switch (r.unit) {
            .day => 'd',
            .week => 'w',
            .month => 'm',
            .year => 'y',
        };
        w.print(" {s}{d}{c}", .{ prefix, r.n, unit }) catch return "";
    }
    if (ts.warn_days) |n| w.print(" -{d}d", .{n}) catch return "";
    w.writeByte(if (ts.active) '>' else ']') catch return "";
    return w.buffered();
}

/// Timed entries sort by time; untimed (and the rows that are on a day for
/// being overdue or coming up) after them, in kind order.
const untimed: i32 = 5_000;

fn timeKey(ts: date.Timestamp) i32 {
    const t = ts.time orelse return untimed;
    return @as(i32, t.hour) * 60 + t.minute;
}

/// Highlighter for the agenda buffer (registered as a custom language).
pub fn highlight(gpa: Allocator, rope: *const rope_mod.Rope, start: usize, end: usize, s: *const theme_mod.Syntax, out: *std.ArrayList(syntax_mod.HighlightSpan)) void {
    out.clearRetainingCapacity();
    const today = Date.today();
    var today_buf: [48]u8 = undefined;
    const today_prefix = std.fmt.bufPrint(&today_buf, "{s: <10} {d: >2} {s}", .{ date.day_names[today.weekday()], today.day, date.month_names[today.month - 1] }) catch "";
    var line_buf: std.ArrayList(u8) = .empty;
    defer line_buf.deinit(gpa);
    const first = rope.offsetToPoint(start).row;
    const last = rope.offsetToPoint(end).row;
    var row = first;
    while (row <= last) : (row += 1) {
        line_buf.clearRetainingCapacity();
        const base = rope.lineStart(row);
        var it = rope.chunks(base, rope.lineEnd(row));
        while (it.next()) |chunk| line_buf.appendSlice(gpa, chunk) catch return;
        const line = line_buf.items;
        if (line.len == 0) continue;
        const add = struct {
            fn f(o: *std.ArrayList(syntax_mod.HighlightSpan), g: Allocator, a: usize, b: usize, color: @import("../../framework/gpu/scene.zig").Color) void {
                if (b > a) o.append(g, .{ .start = a, .end = b, .color = color }) catch {};
            }
        }.f;
        if (line[0] != ' ') {
            const color = if (std.mem.startsWith(u8, line, today_prefix)) s.number else if (std.mem.endsWith(u8, line, "):") or std.mem.startsWith(u8, line, "Global")) s.function else s.keyword;
            add(out, gpa, base, base + line.len, color);
            continue;
        }
        // "  category:   extra       TODO [#A] title   :tags:"
        const cat_end = @min(line.len, 14);
        const rest_at: usize = 34;
        add(out, gpa, base, base + cat_end, s.comment);
        const extra = line[cat_end..@min(line.len, rest_at)];
        // Overdue and due deadlines in the error colour, coming ones and
        // earlier schedules in the warning colour.
        const extra_color = if (std.mem.startsWith(u8, extra, "Deadline") or std.mem.startsWith(u8, extra, "Overdue"))
            s.constant
        else if (std.mem.startsWith(u8, extra, "Due in") or std.mem.indexOf(u8, extra, "ago") != null)
            s.escape
        else if (std.mem.startsWith(u8, extra, "Scheduled"))
            s.string
        else
            s.number;
        add(out, gpa, base + cat_end, base + @min(line.len, rest_at), extra_color);
        if (line.len > rest_at) {
            const rest = line[rest_at..];
            const word_end = std.mem.indexOfScalar(u8, rest, ' ') orelse rest.len;
            const word = rest[0..word_end];
            const kw: parser.Keywords = .{};
            if (kw.isDone(word)) {
                add(out, gpa, base + rest_at, base + line.len, s.comment);
            } else if (kw.isTodo(word)) {
                add(out, gpa, base + rest_at, base + rest_at + word_end, if (std.mem.eql(u8, word, "TODO")) s.constant else s.label);
            }
            if (std.mem.indexOf(u8, rest, "[#")) |p| add(out, gpa, base + rest_at + p, base + rest_at + p + 4, s.number);
            if (std.mem.lastIndexOf(u8, line, "   :")) |p| add(out, gpa, base + p, base + line.len, s.comment);
        }
    }
}

test "tag match" {
    try std.testing.expect(tagMatch("work", ":work:urgent:"));
    try std.testing.expect(tagMatch("work+urgent", ":work:urgent:"));
    try std.testing.expect(!tagMatch("work-urgent", ":work:urgent:"));
    try std.testing.expect(tagMatch("work-urgent|home", ":home:"));
    try std.testing.expect(!tagMatch("home", ""));
    try std.testing.expect(tagMatch("-someday", ":work:"));
}

test "agenda views and filters" {
    const gpa = std.testing.allocator;
    var agenda = Agenda.init(gpa, .todo);
    defer agenda.deinit();
    const text =
        \\* Work :work:
        \\** TODO Ship the deb :urgent:
        \\** NEXT Write docs
        \\* TODO Buy milk :home:
        \\
    ;
    const src = [_]Source{.{ .path = "/x.org", .text = text }};
    try agenda.setQuery(.tags, "work+urgent", "");
    try agenda.build(&src);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Ship the deb") != null);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Write docs") == null);
    try agenda.setQuery(.search, "milk", "");
    try agenda.build(&src);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Buy milk") != null);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Ship") == null);
    try agenda.setQuery(.todo, "NEXT", "");
    try agenda.build(&src);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Write docs") != null);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Buy milk") == null);
    try agenda.setQuery(.todo, "", "");
    try agenda.setFilter(.{ .tags = "+work" });
    try agenda.build(&src);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Write docs") != null);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Buy milk") == null);
}

test "agenda week" {
    const gpa = std.testing.allocator;
    var agenda = Agenda.init(gpa, .week);
    defer agenda.deinit();
    const today = Date.today();
    var dbuf: [32]u8 = undefined;
    const text = try std.fmt.allocPrint(gpa,
        \\* TODO Write tests
        \\  SCHEDULED: <{s}>
        \\* DONE Old thing
        \\
    , .{today.format(&dbuf)});
    defer gpa.free(text);
    try agenda.build(&.{.{ .path = "/tmp/x.org", .text = text }});
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Scheduled:          TODO Write tests") != null);
    try std.testing.expect(std.mem.indexOf(u8, agenda.text.items, "Old thing") == null);
    var lines: usize = 0;
    for (agenda.text.items) |ch| if (ch == '\n') {
        lines += 1;
    };
    try std.testing.expectEqual(lines, agenda.targets.items.len);
}

fn indexOfText(agenda: *const Agenda, needle: []const u8) usize {
    return std.mem.indexOf(u8, agenda.text.items, needle) orelse std.math.maxInt(usize);
}

test "agenda day: order, labels and warning periods" {
    const gpa = std.testing.allocator;
    var agenda = Agenda.init(gpa, .day);
    defer agenda.deinit();
    const today = Date.today();
    var b: [9][32]u8 = undefined;
    const text = try std.fmt.allocPrint(gpa,
        \\* TODO Overdue thing
        \\  DEADLINE: <{s}>
        \\* TODO [#A] Due today
        \\  DEADLINE: <{s}>
        \\* TODO Old schedule
        \\  SCHEDULED: <{s}>
        \\* TODO [#B] Plain schedule
        \\  SCHEDULED: <{s}>
        \\* TODO [#A] Urgent schedule
        \\  SCHEDULED: <{s}>
        \\* TODO Soon
        \\  DEADLINE: <{s} -2d>
        \\* TODO Far away
        \\  DEADLINE: <{s} -2d>
        \\* DONE Finished today
        \\  SCHEDULED: <{s}>
        \\* TODO Meeting
        \\  <{s} 09:30>
        \\
    , .{
        today.addDays(-2).format(&b[0]),
        today.format(&b[1]),
        today.addDays(-3).format(&b[2]),
        today.format(&b[3]),
        today.format(&b[4]),
        today.addDays(2).format(&b[5]),
        today.addDays(3).format(&b[6]),
        today.format(&b[7]),
        today.format(&b[8]),
    });
    defer gpa.free(text);
    try agenda.build(&.{.{ .path = "/tmp/x.org", .text = text }});

    // Wording.
    try std.testing.expect(indexOfText(&agenda, "Overdue 2 d.") != std.math.maxInt(usize));
    try std.testing.expect(indexOfText(&agenda, "Scheduled 3 d. ago") != std.math.maxInt(usize));
    try std.testing.expect(indexOfText(&agenda, "Due in 2 d.") != std.math.maxInt(usize));
    // A deadline's own -2d warning: three days away is not shown.
    try std.testing.expect(indexOfText(&agenda, "Far away") == std.math.maxInt(usize));
    // Done entries stay on their day.
    try std.testing.expect(indexOfText(&agenda, "DONE Finished today") != std.math.maxInt(usize));

    // Timed first, then overdue, deadlines, earlier schedules, schedules, coming deadlines.
    const meeting = indexOfText(&agenda, "Meeting");
    const overdue = indexOfText(&agenda, "Overdue thing");
    const due = indexOfText(&agenda, "Due today");
    const old = indexOfText(&agenda, "Old schedule");
    const urgent = indexOfText(&agenda, "Urgent schedule");
    const plain = indexOfText(&agenda, "Plain schedule");
    const soon = indexOfText(&agenda, "Soon");
    try std.testing.expect(meeting < overdue);
    try std.testing.expect(overdue < due);
    try std.testing.expect(due < old);
    try std.testing.expect(old < urgent);
    // [#A] before [#B] within the scheduled rows.
    try std.testing.expect(urgent < plain);
    try std.testing.expect(plain < soon);
}

test "agenda leaves overdue rows to today" {
    const gpa = std.testing.allocator;
    var agenda = Agenda.init(gpa, .day);
    defer agenda.deinit();
    const today = Date.today();
    var b: [32]u8 = undefined;
    const text = try std.fmt.allocPrint(gpa, "* TODO Late\n  DEADLINE: <{s}>\n", .{today.addDays(-1).format(&b)});
    defer gpa.free(text);
    agenda.start = today.addDays(1);
    try agenda.build(&.{.{ .path = "/tmp/x.org", .text = text }});
    try std.testing.expect(indexOfText(&agenda, "Late") == std.math.maxInt(usize));
}

test "agenda groups: a group per day, items with their details" {
    const gpa = std.testing.allocator;
    var agenda = Agenda.init(gpa, .week);
    defer agenda.deinit();
    const today = Date.today();
    var dbuf: [32]u8 = undefined;
    var ybuf: [32]u8 = undefined;
    const yesterday = today.addDays(-2).format(&ybuf);
    const text = try std.fmt.allocPrint(gpa,
        \\* Project :proj:
        \\** TODO [#A] Ship it :release:
        \\   SCHEDULED: <{s}>
        \\   :PROPERTIES:
        \\   :ID: abc
        \\   :END:
        \\   Remember the changelog.
        \\   - [ ] notes
        \\** TODO Pay rent
        \\   DEADLINE: <{s}>
        \\
    , .{ today.format(&dbuf), yesterday });
    defer gpa.free(text);
    try agenda.build(&.{.{ .path = "/tmp/work.org", .text = text }});
    try std.testing.expectEqual(@as(usize, 7), agenda.groups.items.len);
    var found_today: ?Group = null;
    for (agenda.groups.items) |g| if (g.day) |d| if (d.eql(today)) {
        found_today = g;
    };
    const g = found_today.?;
    try std.testing.expectEqual(@as(usize, 2), g.items.len);
    // Overdue rows come first, then the scheduled one.
    try std.testing.expectEqualStrings("Pay rent", g.items[0].title);
    try std.testing.expectEqualStrings("Overdue 2 d.", g.items[0].label);
    try std.testing.expectEqual(ItemKind.overdue, g.items[0].kind);
    const ship = g.items[1];
    try std.testing.expectEqualStrings("Ship it", ship.title);
    try std.testing.expectEqualStrings("TODO", ship.todo);
    try std.testing.expectEqual(@as(?u8, 'A'), ship.priority);
    try std.testing.expectEqualStrings(":release:", ship.tags);
    try std.testing.expectEqualStrings("Scheduled", ship.label);
    try std.testing.expectEqualStrings("Project", ship.outline);
    try std.testing.expectEqualStrings("work", ship.category);
    try std.testing.expectEqual(@as(u32, 1), ship.line);
    try std.testing.expect(std.mem.startsWith(u8, ship.scheduled, "<"));
    try std.testing.expectEqualStrings("Remember the changelog.\n- [ ] notes", ship.notes);
}

test "agenda groups: the to-do list goes file by file" {
    const gpa = std.testing.allocator;
    var agenda = Agenda.init(gpa, .todo);
    defer agenda.deinit();
    try agenda.build(&.{
        .{ .path = "/x/a.org", .text = "* TODO One\n* TODO Two\n" },
        .{ .path = "/x/b.org", .text = "* DONE Finished\n" },
        .{ .path = "/x/c.org", .text = "* NEXT Three\n" },
    });
    try std.testing.expectEqual(@as(usize, 2), agenda.groups.items.len);
    try std.testing.expectEqualStrings("a.org", agenda.groups.items[0].header);
    try std.testing.expectEqual(@as(usize, 2), agenda.groups.items[0].items.len);
    try std.testing.expectEqualStrings("c.org", agenda.groups.items[1].header);
}

test "formatTimestamp writes what org writes" {
    var buf: [64]u8 = undefined;
    const ts = parser_ts("<2026-11-03 Tue 09:30-10:00 +1w -2d>");
    try std.testing.expectEqualStrings("<2026-11-03 Tue 09:30-10:00 +1w -2d>", formatTimestamp(&buf, ts));
}

fn parser_ts(s: []const u8) date.Timestamp {
    return date.parseTimestamp(s, 0).?;
}
