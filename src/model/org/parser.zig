//! A light org-mode parser: headings with TODO state, priority, tags,
//! planning (SCHEDULED / DEADLINE / CLOSED), property drawers and body
//! timestamps.

const std = @import("std");
const date = @import("date.zig");
const clock = @import("clock.zig");

const Allocator = std.mem.Allocator;

pub const default_todo = [_][]const u8{ "TODO", "NEXT", "WAITING" };
pub const default_done = [_][]const u8{ "DONE", "CANCELLED" };

/// TODO keyword sequence (from `#+TODO: A B | C D` or the defaults).
pub const Keywords = struct {
    todo: []const []const u8 = &default_todo,
    done: []const []const u8 = &default_done,

    pub fn isTodo(self: Keywords, word: []const u8) bool {
        for (self.todo) |k| if (std.mem.eql(u8, k, word)) return true;
        return false;
    }

    pub fn isDone(self: Keywords, word: []const u8) bool {
        for (self.done) |k| if (std.mem.eql(u8, k, word)) return true;
        return false;
    }

    pub fn isKeyword(self: Keywords, word: []const u8) bool {
        return self.isTodo(word) or self.isDone(word);
    }

    /// The keyword after `current` in the cycle (none → first todo → ... → none).
    pub fn next(self: Keywords, current: ?[]const u8, forward: bool) ?[]const u8 {
        // The cycle is: none, todo..., done...
        var all: [32][]const u8 = undefined;
        var n: usize = 0;
        for (self.todo) |k| if (n < all.len) {
            all[n] = k;
            n += 1;
        };
        for (self.done) |k| if (n < all.len) {
            all[n] = k;
            n += 1;
        };
        const cycle_len = n;
        var idx: isize = -1;
        if (current) |cur| {
            for (all[0..n], 0..) |k, i| if (std.mem.eql(u8, k, cur)) {
                idx = @intCast(i);
            };
        }
        const len: isize = @intCast(cycle_len);
        var nx: isize = if (forward) idx + 1 else idx - 1;
        if (nx >= len) nx = -1;
        if (nx < -1) nx = len - 1;
        return if (nx < 0) null else all[@intCast(nx)];
    }
};

pub const Property = struct { key: []const u8, value: []const u8 };

pub const Heading = struct {
    level: u8,
    todo: ?[]const u8 = null,
    priority: ?u8 = null,
    title: []const u8,
    tags: []const u8 = "",
    line: u32,
    /// Byte offset of the heading line start.
    offset: usize,
    scheduled: ?date.Timestamp = null,
    deadline: ?date.Timestamp = null,
    closed: ?date.Timestamp = null,
    /// Active timestamps in the heading or its body (appointments).
    timestamps: std.ArrayList(date.Timestamp) = .empty,
    /// The :PROPERTIES: drawer, in order.
    properties: std.ArrayList(Property) = .empty,
    /// Its CATEGORY property, else the nearest ancestor's, else the file's.
    category: []const u8 = "",
    /// Its tags and its ancestors', ":a:b:" (org's tag inheritance).
    all_tags: []const u8 = "",
    /// Its CLOCK lines (in :LOGBOOK: or not), newest first as org writes them.
    clocks: std.ArrayList(clock.Clock) = .empty,

    /// Minutes clocked on this heading itself (a running clock counts up to `now`).
    pub fn clocked(self: *const Heading, now: i64) i64 {
        var total: i64 = 0;
        for (self.clocks.items) |c| total += c.minutes(now);
        return total;
    }

    pub fn property(self: *const Heading, key: []const u8) ?[]const u8 {
        for (self.properties.items) |p| if (std.ascii.eqlIgnoreCase(p.key, key)) return p.value;
        return null;
    }
};

pub const Document = struct {
    arena: std.heap.ArenaAllocator,
    headings: std.ArrayList(Heading) = .empty,
    keywords: Keywords = .{},
    category: []const u8 = "",
    title: []const u8 = "",
    /// #+FILETAGS: every heading has them, ":a:b:".
    file_tags: []const u8 = "",

    pub fn deinit(self: *Document) void {
        self.arena.deinit();
    }
};

pub const HeadingLine = struct {
    level: u8,
    todo: ?[]const u8,
    priority: ?u8,
    /// Title text (without keyword, priority and tags).
    title: []const u8,
    tags: []const u8,
    /// Offsets within the line.
    todo_start: usize = 0,
    priority_start: usize = 0,
    title_start: usize = 0,
    tags_start: usize = 0,
};

/// Parses "** TODO [#A] Title text   :tag1:tag2:".
pub fn parseHeadingLine(line: []const u8, keywords: Keywords) ?HeadingLine {
    var level: usize = 0;
    while (level < line.len and line[level] == '*') level += 1;
    if (level == 0 or level >= line.len or line[level] != ' ') return null;
    var i = level;
    while (i < line.len and line[i] == ' ') i += 1;
    var h: HeadingLine = .{ .level = @intCast(@min(level, 255)), .todo = null, .priority = null, .title = "", .tags = "" };
    // TODO keyword.
    var word_end = i;
    while (word_end < line.len and line[word_end] != ' ') word_end += 1;
    if (word_end > i and keywords.isKeyword(line[i..word_end])) {
        h.todo = line[i..word_end];
        h.todo_start = i;
        i = word_end;
        while (i < line.len and line[i] == ' ') i += 1;
    }
    // Priority cookie.
    if (i + 3 < line.len + 1 and i + 4 <= line.len and line[i] == '[' and line[i + 1] == '#' and line[i + 3] == ']') {
        h.priority = line[i + 2];
        h.priority_start = i;
        i += 4;
        while (i < line.len and line[i] == ' ') i += 1;
    }
    // Tags at the end.
    var title_end = line.len;
    const trimmed = std.mem.trimEnd(u8, line, " \t");
    if (trimmed.len > i and trimmed[trimmed.len - 1] == ':') {
        if (std.mem.lastIndexOfScalar(u8, trimmed[0 .. trimmed.len - 1], ' ')) |sp| {
            const cand = trimmed[sp + 1 ..];
            if (cand.len >= 3 and cand[0] == ':' and std.mem.indexOfAny(u8, cand, " \t") == null and sp + 1 > i) {
                h.tags = cand;
                h.tags_start = sp + 1;
                title_end = sp;
            }
        }
    }
    h.title_start = i;
    h.title = std.mem.trim(u8, line[@min(i, title_end)..title_end], " \t");
    return h;
}

/// `#+TODO: A B | C D` (or SEQ_TODO, TYP_TODO): the keywords it defines,
/// allocated in `alloc`, or null for any other line.
pub fn parseTodoLine(alloc: Allocator, line: []const u8) !?Keywords {
    if (!(std.ascii.startsWithIgnoreCase(line, "#+TODO:") or std.ascii.startsWithIgnoreCase(line, "#+SEQ_TODO:") or std.ascii.startsWithIgnoreCase(line, "#+TYP_TODO:"))) return null;
    const colon = std.mem.indexOfScalar(u8, line, ':').?;
    var todo: std.ArrayList([]const u8) = .empty;
    var done: std.ArrayList([]const u8) = .empty;
    var after_bar = false;
    var it = std.mem.tokenizeAny(u8, line[colon + 1 ..], " \t\r");
    while (it.next()) |tok| {
        if (std.mem.eql(u8, tok, "|")) {
            after_bar = true;
            continue;
        }
        // Strip fast-access keys: "TODO(t)".
        const word = if (std.mem.indexOfScalar(u8, tok, '(')) |p| tok[0..p] else tok;
        try (if (after_bar) &done else &todo).append(alloc, word);
    }
    if (!after_bar and todo.items.len > 0) {
        // Without a bar, the last keyword is the done state.
        try done.append(alloc, todo.pop().?);
    }
    if (todo.items.len == 0) return null;
    return .{ .todo = todo.items, .done = done.items };
}

/// The TODO keywords `text` declares, or the defaults. The last `#+TODO:`
/// line wins, as for `parse`.
pub fn fileKeywords(alloc: Allocator, text: []const u8) !Keywords {
    var kw: Keywords = .{};
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |line| {
        if (line.len < 2 or line[0] != '#') continue;
        if (try parseTodoLine(alloc, line)) |k| kw = k;
    }
    return kw;
}

pub fn parse(gpa: Allocator, text: []const u8, file_name: []const u8) !Document {
    var doc: Document = .{ .arena = .init(gpa) };
    errdefer doc.deinit();
    const alloc = doc.arena.allocator();
    doc.category = std.fs.path.stem(file_name);

    // File settings first (#+TODO affects heading parsing).
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |line| {
        if (try parseTodoLine(alloc, line)) |kw| {
            doc.keywords = kw;
        } else if (std.ascii.startsWithIgnoreCase(line, "#+CATEGORY:")) {
            doc.category = std.mem.trim(u8, line["#+CATEGORY:".len..], " ");
        } else if (std.ascii.startsWithIgnoreCase(line, "#+FILETAGS:")) {
            const v = std.mem.trim(u8, line["#+FILETAGS:".len..], " \t\r");
            if (v.len > 0) doc.file_tags = if (v[0] == ':') v else try std.fmt.allocPrint(alloc, ":{s}:", .{v});
        } else if (std.ascii.startsWithIgnoreCase(line, "#+TITLE:")) {
            doc.title = std.mem.trim(u8, line["#+TITLE:".len..], " ");
        }
    }

    var offset: usize = 0;
    var row: u32 = 0;
    var current: ?usize = null;
    var in_block = false;
    var drawer: enum { none, properties, other } = .none;
    lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |line| : ({
        offset += line.len + 1;
        row += 1;
    }) {
        const trimmed = std.mem.trimStart(u8, line, " \t");
        if (std.ascii.startsWithIgnoreCase(trimmed, "#+BEGIN_")) in_block = true;
        if (std.ascii.startsWithIgnoreCase(trimmed, "#+END_")) {
            in_block = false;
            continue;
        }
        if (in_block) continue;
        // Drawers: :PROPERTIES: is read; :LOGBOOK: and others are skipped
        // (their CLOCK and state-change stamps aren't appointments).
        if (drawer != .none) {
            const t = std.mem.trimEnd(u8, trimmed, " \t\r");
            if (std.ascii.eqlIgnoreCase(t, ":END:")) {
                drawer = .none;
            } else if (drawer == .properties) {
                if (current) |idx| if (parseProperty(t)) |p| try doc.headings.items[idx].properties.append(alloc, p);
            } else if (clock.parseLine(t)) |c| if (current) |idx| try doc.headings.items[idx].clocks.append(alloc, c);
            continue;
        }
        if (current != null and isDrawerStart(trimmed)) {
            drawer = if (std.ascii.startsWithIgnoreCase(trimmed, ":PROPERTIES:")) .properties else .other;
            continue;
        }
        if (parseHeadingLine(line, doc.keywords)) |h| {
            try doc.headings.append(alloc, .{
                .level = h.level,
                .todo = h.todo,
                .priority = h.priority,
                .title = h.title,
                .tags = h.tags,
                .line = row,
                .offset = offset,
            });
            current = doc.headings.items.len - 1;
            try collectTimestamps(alloc, &doc.headings.items[current.?], line, false);
            continue;
        }
        const idx = current orelse continue;
        const heading = &doc.headings.items[idx];
        if (clock.parseLine(trimmed)) |c| {
            try heading.clocks.append(alloc, c);
            continue;
        }
        // Planning line(s) directly under the heading.
        var planning = false;
        inline for (.{ .{ "SCHEDULED:", "scheduled" }, .{ "DEADLINE:", "deadline" }, .{ "CLOSED:", "closed" } }) |kw| {
            if (std.mem.indexOf(u8, line, kw[0])) |p| {
                var j = p + kw[0].len;
                while (j < line.len and line[j] == ' ') j += 1;
                if (date.parseTimestamp(line, j)) |ts| {
                    @field(heading, kw[1]) = ts;
                    planning = true;
                }
            }
        }
        if (!planning) try collectTimestamps(alloc, heading, line, true);
    }
    try inheritCategories(&doc);
    return doc;
}

/// ":NAME:" alone on a line opens a drawer.
fn isDrawerStart(trimmed: []const u8) bool {
    const t = std.mem.trimEnd(u8, trimmed, " \t\r");
    if (t.len < 3 or t[0] != ':' or t[t.len - 1] != ':') return false;
    for (t[1 .. t.len - 1]) |ch| if (!(std.ascii.isAlphanumeric(ch) or ch == '_' or ch == '-')) return false;
    return !std.ascii.eqlIgnoreCase(t, ":END:");
}

/// ":KEY: value" inside a property drawer.
fn parseProperty(t: []const u8) ?Property {
    if (t.len < 3 or t[0] != ':') return null;
    const close = std.mem.indexOfScalarPos(u8, t, 1, ':') orelse return null;
    if (close == 1) return null;
    return .{ .key = t[1..close], .value = std.mem.trim(u8, t[close + 1 ..], " \t") };
}

/// Each heading's category (its CATEGORY property, else the nearest
/// ancestor's, else the file's: #+CATEGORY or the file name) and its tags
/// with its ancestors' (plus #+FILETAGS).
fn inheritCategories(doc: *Document) !void {
    const alloc = doc.arena.allocator();
    var stack: [64][]const u8 = undefined; // category by level, while inside it
    var tag_stack: [64][]const u8 = undefined;
    var levels: [64]u8 = undefined;
    var depth: usize = 0;
    for (doc.headings.items) |*h| {
        while (depth > 0 and levels[depth - 1] >= h.level) depth -= 1;
        const inherited = if (depth > 0) stack[depth - 1] else doc.category;
        h.category = h.property("CATEGORY") orelse inherited;
        const parent_tags = if (depth > 0) tag_stack[depth - 1] else doc.file_tags;
        h.all_tags = if (parent_tags.len == 0) h.tags else if (h.tags.len == 0) parent_tags else try std.mem.concat(alloc, u8, &.{ parent_tags, h.tags[1..] });
        if (depth < stack.len) {
            stack[depth] = h.category;
            tag_stack[depth] = h.all_tags;
            levels[depth] = h.level;
            depth += 1;
        }
    }
}

fn collectTimestamps(alloc: Allocator, heading: *Heading, line: []const u8, _: bool) !void {
    var i: usize = 0;
    while (std.mem.indexOfScalarPos(u8, line, i, '<')) |p| {
        if (date.parseTimestamp(line, p)) |ts| {
            if (heading.timestamps.items.len < 16) try heading.timestamps.append(alloc, ts);
            i = ts.end;
        } else i = p + 1;
    }
}

test "parse headings" {
    const text =
        \\#+TITLE: Notes
        \\* TODO [#A] Write the editor :zig:work:
        \\  SCHEDULED: <2026-10-21 Wed> DEADLINE: <2026-10-30 Fri>
        \\** DONE Rope
        \\   CLOSED: [2026-10-20 Tue 18:00]
        \\* Meeting <2026-10-22 Thu 10:00-11:00>
        \\#+BEGIN_SRC zig
        \\* not a heading
        \\#+END_SRC
    ;
    var doc = try parse(std.testing.allocator, text, "notes.org");
    defer doc.deinit();
    try std.testing.expectEqual(@as(usize, 3), doc.headings.items.len);
    const h0 = doc.headings.items[0];
    try std.testing.expectEqualStrings("TODO", h0.todo.?);
    try std.testing.expectEqual(@as(u8, 'A'), h0.priority.?);
    try std.testing.expectEqualStrings("Write the editor", h0.title);
    try std.testing.expectEqualStrings(":zig:work:", h0.tags);
    try std.testing.expectEqual(@as(u8, 30), h0.deadline.?.date.day);
    try std.testing.expectEqual(@as(u8, 21), h0.scheduled.?.date.day);
    try std.testing.expectEqualStrings("DONE", doc.headings.items[1].todo.?);
    try std.testing.expect(doc.headings.items[1].closed != null);
    try std.testing.expectEqual(@as(usize, 1), doc.headings.items[2].timestamps.items.len);
    try std.testing.expectEqualStrings("notes", doc.category);
}

test "property drawers and categories" {
    const text =
        \\#+CATEGORY: home
        \\* Project
        \\  :PROPERTIES:
        \\  :CATEGORY: garden
        \\  :ID:       abc-123
        \\  :END:
        \\** TODO Plant <2026-10-22 Thu>
        \\   :LOGBOOK:
        \\   CLOCK: [2026-10-20 Tue 10:00]--[2026-10-20 Tue 11:00] =>  1:00
        \\   - State "DONE" from "TODO" <2026-10-19 Mon>
        \\   :END:
        \\* Other
    ;
    var doc = try parse(std.testing.allocator, text, "x.org");
    defer doc.deinit();
    const hs = doc.headings.items;
    try std.testing.expectEqual(@as(usize, 3), hs.len);
    try std.testing.expectEqualStrings("abc-123", hs[0].property("id").?);
    try std.testing.expectEqualStrings("garden", hs[0].category);
    try std.testing.expectEqualStrings("garden", hs[1].category);
    try std.testing.expectEqualStrings("home", hs[2].category);
    var doc2 = try parse(std.testing.allocator, "#+FILETAGS: :f:\n* A :a:\n** B :b:\n** C\n* D\n", "y.org");
    defer doc2.deinit();
    try std.testing.expectEqualStrings(":f:a:b:", doc2.headings.items[1].all_tags);
    try std.testing.expectEqualStrings(":f:a:", doc2.headings.items[2].all_tags);
    try std.testing.expectEqualStrings(":f:", doc2.headings.items[3].all_tags);
    // The LOGBOOK's stamp isn't an appointment; the heading's is.
    try std.testing.expectEqual(@as(usize, 1), hs[1].timestamps.items.len);
    try std.testing.expectEqual(@as(i64, 60), hs[1].clocked(0));
    try std.testing.expectEqual(@as(u8, 22), hs[1].timestamps.items[0].date.day);
}

test "file keywords" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const kw = try fileKeywords(arena.allocator(), "#+TITLE: x\n#+TODO: TODO(t) DOING | DONE(d) DROP\n* DOING thing\n");
    try std.testing.expect(kw.isTodo("DOING"));
    try std.testing.expect(kw.isDone("DROP"));
    try std.testing.expect(!kw.isTodo("NEXT"));
    const def = try fileKeywords(arena.allocator(), "* TODO x\n");
    try std.testing.expect(def.isTodo("NEXT"));
}

test "todo cycle" {
    const k: Keywords = .{};
    try std.testing.expectEqualStrings("TODO", k.next(null, true).?);
    try std.testing.expectEqualStrings("NEXT", k.next("TODO", true).?);
    try std.testing.expect(k.next("CANCELLED", true) == null);
    try std.testing.expectEqualStrings("CANCELLED", k.next(null, false).?);
}
