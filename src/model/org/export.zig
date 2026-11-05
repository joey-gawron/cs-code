//! Org to HTML and Markdown: headings (with TODO keywords and tags),
//! paragraphs, lists and checkboxes, tables, source / example / quote
//! blocks, emphasis (*bold* /italic/ _underline_ =verbatim= ~code~
//! +strike+) and links. Drawers, planning lines, comments and #+ settings
//! are left out, as org's exporter does by default.

const std = @import("std");
const parser = @import("parser.zig");
const table = @import("table.zig");
const highlight = @import("highlight.zig");

const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;

pub const Format = enum { html, markdown };

pub fn toHtml(gpa: Allocator, text: []const u8, file_name: []const u8) ![]u8 {
    return exportAs(gpa, text, file_name, .html);
}

pub fn toMarkdown(gpa: Allocator, text: []const u8, file_name: []const u8) ![]u8 {
    return exportAs(gpa, text, file_name, .markdown);
}

const List = struct { indent: usize, ordered: bool };

const State = struct {
    fmt: Format,
    w: *Writer,
    gpa: Allocator,
    keywords: parser.Keywords,
    in_paragraph: bool = false,
    lists: std.ArrayList(List) = .empty,
    table_lines: std.ArrayList([]const u8) = .empty,

    fn closeParagraph(s: *State) !void {
        if (!s.in_paragraph) return;
        s.in_paragraph = false;
        try s.w.writeAll(if (s.fmt == .html) "</p>\n" else "\n\n");
    }

    fn closeLists(s: *State, to_indent: ?usize) !void {
        const had = s.lists.items.len > 0;
        while (s.lists.items.len > 0) {
            const top = s.lists.items[s.lists.items.len - 1];
            if (to_indent) |i| if (top.indent <= i) break;
            _ = s.lists.pop();
            if (s.fmt == .html) try s.w.writeAll(if (top.ordered) "</li></ol>\n" else "</li></ul>\n");
        }
        // Markdown items end at the next item or here.
        if (s.fmt == .markdown and had and s.lists.items.len == 0 and to_indent == null) try s.w.writeAll("\n\n");
    }

    fn flushTable(s: *State) !void {
        if (s.table_lines.items.len == 0) return;
        defer s.table_lines.clearRetainingCapacity();
        var t = try table.parse(s.gpa, s.table_lines.items);
        defer t.deinit();
        const ncols = t.columns();
        // Rows before the first separator are the header.
        var header_rows: usize = 0;
        for (t.rows.items, 0..) |r, i| if (r == .hline) {
            header_rows = i;
            break;
        };
        if (s.fmt == .html) {
            try s.w.writeAll("<table>\n");
            for (t.rows.items, 0..) |r, i| {
                if (r == .hline) continue;
                const tag = if (i < header_rows) "th" else "td";
                try s.w.writeAll("<tr>");
                for (0..ncols) |c| {
                    try s.w.print("<{s}>", .{tag});
                    try inline_(s.w, t.cell(i, c), .html);
                    try s.w.print("</{s}>", .{tag});
                }
                try s.w.writeAll("</tr>\n");
            }
            try s.w.writeAll("</table>\n");
            return;
        }
        // Markdown needs a header row: the first row.
        var first = true;
        for (t.rows.items, 0..) |r, i| {
            if (r == .hline) continue;
            try s.w.writeAll("|");
            for (0..ncols) |c| {
                try s.w.writeAll(" ");
                try inline_(s.w, t.cell(i, c), .markdown);
                try s.w.writeAll(" |");
            }
            try s.w.writeAll("\n");
            if (first) {
                try s.w.writeAll("|");
                for (0..ncols) |_| try s.w.writeAll(" --- |");
                try s.w.writeAll("\n");
                first = false;
            }
        }
        try s.w.writeAll("\n");
    }

    fn closeAll(s: *State) !void {
        try s.closeParagraph();
        try s.flushTable();
        if (s.lists.items.len > 0) try s.closeLists(null);
    }
};

fn exportAs(gpa: Allocator, text: []const u8, file_name: []const u8, fmt: Format) ![]u8 {
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const keywords = try parser.fileKeywords(arena.allocator(), text);
    var title: []const u8 = std.fs.path.stem(file_name);
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |line| if (std.ascii.startsWithIgnoreCase(line, "#+TITLE:")) {
        title = std.mem.trim(u8, line["#+TITLE:".len..], " \t\r");
    };

    var out: Writer.Allocating = .init(gpa);
    errdefer out.deinit();
    const w = &out.writer;
    var s: State = .{ .fmt = fmt, .w = w, .gpa = arena.allocator(), .keywords = keywords };
    if (fmt == .html) {
        try w.writeAll("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>");
        try escape(w, title);
        try w.writeAll("</title>\n<style>body{font-family:sans-serif;max-width:46em;margin:2em auto;padding:0 1em;line-height:1.5}" ++
            "pre{background:#f4f4f4;padding:.8em;overflow:auto}code{background:#f4f4f4}table{border-collapse:collapse}" ++
            "td,th{border:1px solid #ccc;padding:.2em .6em}.todo{color:#c00;font-weight:bold}.done{color:#080;font-weight:bold}" ++
            ".tag{font-size:.7em;color:#666;margin-left:.5em}</style>\n</head>\n<body>\n<h1 class=\"title\">");
        try escape(w, title);
        try w.writeAll("</h1>\n");
    } else try w.print("# {s}\n\n", .{title});

    var block: ?struct { kind: enum { src, example, quote }, lang: []const u8, indent: usize = 0 } = null;
    var in_drawer = false;
    lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |raw| {
        const line = std.mem.trimEnd(u8, raw, "\r");
        const trimmed = std.mem.trimStart(u8, line, " \t");
        // Blocks.
        if (block) |b| {
            if (std.ascii.startsWithIgnoreCase(trimmed, "#+END_")) {
                block = null;
                try w.writeAll(switch (b.kind) {
                    .src, .example => if (fmt == .html) "</code></pre>\n" else "```\n\n",
                    .quote => if (fmt == .html) "</blockquote>\n" else "\n",
                });
                continue;
            }
            switch (b.kind) {
                .src, .example => {
                    // Without the block's own indentation.
                    var strip: usize = 0;
                    while (strip < b.indent and strip < line.len and (line[strip] == ' ' or line[strip] == '\t')) strip += 1;
                    const code = line[strip..];
                    if (fmt == .html) try escape(w, code) else try w.writeAll(code);
                    try w.writeByte('\n');
                },
                .quote => {
                    if (fmt == .html) {
                        try inline_(w, trimmed, .html);
                        try w.writeByte('\n');
                    } else {
                        try w.writeAll("> ");
                        try inline_(w, trimmed, .markdown);
                        try w.writeByte('\n');
                    }
                },
            }
            continue;
        }
        if (std.ascii.startsWithIgnoreCase(trimmed, "#+BEGIN_")) {
            try s.closeAll();
            const rest = trimmed["#+BEGIN_".len..];
            var words = std.mem.tokenizeAny(u8, rest, " \t");
            const kind_word = words.next() orelse "";
            const lang = words.next() orelse "";
            const block_indent = line.len - trimmed.len;
            if (std.ascii.eqlIgnoreCase(kind_word, "SRC")) {
                block = .{ .kind = .src, .lang = lang, .indent = block_indent };
                if (fmt == .html) {
                    try w.writeAll("<pre><code");
                    if (lang.len > 0) {
                        try w.writeAll(" class=\"language-");
                        try escape(w, lang);
                        try w.writeAll("\"");
                    }
                    try w.writeAll(">");
                } else try w.print("```{s}\n", .{lang});
            } else if (std.ascii.eqlIgnoreCase(kind_word, "QUOTE")) {
                block = .{ .kind = .quote, .lang = "" };
                if (fmt == .html) try w.writeAll("<blockquote>\n");
            } else {
                block = .{ .kind = .example, .lang = "", .indent = block_indent };
                try w.writeAll(if (fmt == .html) "<pre><code>" else "```\n");
            }
            continue;
        }
        // Drawers, settings, comments and planning lines don't export.
        if (in_drawer) {
            if (std.ascii.eqlIgnoreCase(std.mem.trim(u8, trimmed, " \t"), ":END:")) in_drawer = false;
            continue;
        }
        const t = std.mem.trim(u8, trimmed, " \t");
        if (t.len >= 3 and t[0] == ':' and t[t.len - 1] == ':' and std.mem.indexOfScalar(u8, t[1 .. t.len - 1], ' ') == null and std.mem.indexOfScalar(u8, t[1 .. t.len - 1], ':') == null) {
            in_drawer = true;
            continue;
        }
        if (std.mem.startsWith(u8, trimmed, "#+") or std.mem.startsWith(u8, trimmed, "# ") or std.mem.eql(u8, trimmed, "#")) continue;
        if (std.mem.startsWith(u8, trimmed, "SCHEDULED:") or std.mem.startsWith(u8, trimmed, "DEADLINE:") or std.mem.startsWith(u8, trimmed, "CLOSED:")) continue;

        if (parser.parseHeadingLine(line, s.keywords)) |h| {
            try s.closeAll();
            try heading(&s, h);
            continue;
        }
        if (t.len == 0) {
            try s.closeAll();
            continue;
        }
        if (table.isTableLine(line)) {
            try s.closeParagraph();
            if (s.lists.items.len > 0) try s.closeLists(null);
            try s.table_lines.append(s.gpa, try s.gpa.dupe(u8, line));
            continue;
        }
        try s.flushTable();
        const indent = line.len - trimmed.len;
        if (highlight.listMarker(trimmed)) |m| {
            try s.closeParagraph();
            try listItem(&s, indent, trimmed, m);
            continue;
        }
        // Paragraph text (or the continuation of a list item).
        if (s.lists.items.len > 0 and indent > s.lists.items[s.lists.items.len - 1].indent) {
            try w.writeByte(' ');
            try inline_(w, t, fmt);
            continue;
        }
        if (s.lists.items.len > 0) try s.closeLists(null);
        if (!s.in_paragraph) {
            if (fmt == .html) try w.writeAll("<p>");
            s.in_paragraph = true;
        } else try w.writeByte(if (fmt == .html) '\n' else '\n');
        try inline_(w, t, fmt);
    }
    try s.closeAll();
    if (fmt == .html) try w.writeAll("</body>\n</html>\n");
    return out.toOwnedSlice();
}

fn heading(s: *State, h: parser.HeadingLine) !void {
    const w = s.w;
    // Level 1 is the document title's <h1>: headings start at <h2> / "##".
    const level = @min(@as(usize, h.level) + 1, 6);
    if (s.fmt == .html) {
        try w.print("<h{d}>", .{level});
        if (h.todo) |kw| {
            try w.print("<span class=\"{s}\">", .{if (s.keywords.isDone(kw)) "done" else "todo"});
            try escape(w, kw);
            try w.writeAll("</span> ");
        }
        try inline_(w, h.title, .html);
        var it = std.mem.tokenizeScalar(u8, h.tags, ':');
        while (it.next()) |tag| {
            try w.writeAll("<span class=\"tag\">");
            try escape(w, tag);
            try w.writeAll("</span>");
        }
        try w.print("</h{d}>\n", .{level});
    } else {
        try w.splatByteAll('#', level);
        try w.writeByte(' ');
        if (h.todo) |kw| try w.print("{s} ", .{kw});
        try inline_(w, h.title, .markdown);
        try w.writeAll("\n\n");
    }
}

fn listItem(s: *State, indent: usize, trimmed: []const u8, m: highlight.ListMarker) !void {
    const w = s.w;
    const ordered = std.ascii.isDigit(trimmed[0]);
    if (s.fmt == .markdown and s.lists.items.len > 0) try w.writeByte('\n');
    try s.closeLists(indent);
    const top = if (s.lists.items.len > 0) s.lists.items[s.lists.items.len - 1] else null;
    var body = std.mem.trimStart(u8, trimmed[m.bullet_len..], " ");
    var checkbox: ?u8 = null;
    if (m.checkbox) |cb| {
        checkbox = cb;
        body = std.mem.trimStart(u8, body[3..], " ");
    }
    if (s.fmt == .html) {
        if (top == null or top.?.indent < indent) {
            try w.writeAll(if (ordered) "<ol>\n<li>" else "<ul>\n<li>");
            try s.lists.append(s.gpa, .{ .indent = indent, .ordered = ordered });
        } else try w.writeAll("</li>\n<li>");
        if (checkbox) |cb| try w.writeAll(if (cb == ' ') "&#9744; " else if (cb == '-') "&#9745;&#8211; " else "&#9745; ");
        try inline_(w, body, .html);
        return;
    }
    if (top == null or top.?.indent < indent) try s.lists.append(s.gpa, .{ .indent = indent, .ordered = ordered });
    if (s.lists.items.len > 1) try w.splatByteAll(' ', (s.lists.items.len - 1) * 2);
    try w.writeAll(if (ordered) "1. " else "- ");
    if (checkbox) |cb| try w.writeAll(if (cb == ' ') "[ ] " else "[x] ");
    try inline_(w, body, .markdown);
}

fn escape(w: *Writer, text: []const u8) !void {
    for (text) |ch| switch (ch) {
        '<' => try w.writeAll("&lt;"),
        '>' => try w.writeAll("&gt;"),
        '&' => try w.writeAll("&amp;"),
        '"' => try w.writeAll("&quot;"),
        else => try w.writeByte(ch),
    };
}

fn text_(w: *Writer, text: []const u8, fmt: Format) !void {
    if (fmt == .html) try escape(w, text) else try w.writeAll(text);
}

fn isBorder(ch: u8) bool {
    return ch == ' ' or ch == '\t' or ch == '(' or ch == ')' or ch == '{' or ch == '}' or ch == '"' or ch == '\'' or
        ch == ',' or ch == '.' or ch == ';' or ch == ':' or ch == '!' or ch == '?' or ch == '-' or ch == '[' or ch == ']';
}

/// Emphasis and links in a line of text.
fn inline_(w: *Writer, text: []const u8, fmt: Format) Writer.Error!void {
    var i: usize = 0;
    var plain_start: usize = 0;
    while (i < text.len) {
        const ch = text[i];
        // [[target][description]] / [[target]]
        if (ch == '[' and i + 1 < text.len and text[i + 1] == '[') {
            if (std.mem.indexOfPos(u8, text, i, "]]")) |close| {
                try text_(w, text[plain_start..i], fmt);
                const inner = text[i + 2 .. close];
                const sep = std.mem.indexOf(u8, inner, "][");
                const target = if (sep) |p| inner[0..p] else inner;
                const desc = if (sep) |p| inner[p + 2 ..] else inner;
                const href = if (std.mem.startsWith(u8, target, "file:")) target[5..] else target;
                if (fmt == .html) {
                    try w.writeAll("<a href=\"");
                    try escape(w, href);
                    try w.writeAll("\">");
                    try inline_(w, desc, .html);
                    try w.writeAll("</a>");
                } else {
                    try w.writeByte('[');
                    try inline_(w, desc, .markdown);
                    try w.print("]({s})", .{href});
                }
                i = close + 2;
                plain_start = i;
                continue;
            }
        }
        // *bold* /italic/ _underline_ =verbatim= ~code~ +strike+
        if (std.mem.indexOfScalar(u8, "*/_=~+", ch) != null and (i == 0 or isBorder(text[i - 1])) and i + 1 < text.len and text[i + 1] != ' ') {
            var j = i + 1;
            const close: ?usize = while (j < text.len) : (j += 1) {
                if (text[j] == ch and text[j - 1] != ' ' and (j + 1 == text.len or isBorder(text[j + 1]))) break j;
            } else null;
            if (close) |c| if (c > i + 1) {
                try text_(w, text[plain_start..i], fmt);
                const inner = text[i + 1 .. c];
                const verbatim = ch == '=' or ch == '~';
                const tags: [2][]const u8 = switch (fmt) {
                    .html => switch (ch) {
                        '*' => .{ "<b>", "</b>" },
                        '/' => .{ "<i>", "</i>" },
                        '_' => .{ "<u>", "</u>" },
                        '+' => .{ "<del>", "</del>" },
                        else => .{ "<code>", "</code>" },
                    },
                    .markdown => switch (ch) {
                        '*' => .{ "**", "**" },
                        '/' => .{ "*", "*" },
                        '_' => .{ "<u>", "</u>" },
                        '+' => .{ "~~", "~~" },
                        else => .{ "`", "`" },
                    },
                };
                try w.writeAll(tags[0]);
                if (verbatim) try text_(w, inner, fmt) else try inline_(w, inner, fmt);
                try w.writeAll(tags[1]);
                i = c + 1;
                plain_start = i;
                continue;
            };
        }
        i += 1;
    }
    try text_(w, text[plain_start..], fmt);
}

const sample =
    \\#+TITLE: Plan
    \\#+TODO: TODO | DONE
    \\* TODO Ship *1.0* :work:
    \\  SCHEDULED: <2026-10-30 Fri>
    \\  :PROPERTIES:
    \\  :ID: x
    \\  :END:
    \\Some /text/ with =code= and [[https://x.dev][a link]].
    \\- [X] macOS
    \\- [ ] right click
    \\  menus
    \\| a | b |
    \\|---+---|
    \\| 1 | <2> |
    \\#+BEGIN_SRC zig
    \\const x = a < b;
    \\#+END_SRC
;

test "export html" {
    const gpa = std.testing.allocator;
    const html = try toHtml(gpa, sample, "plan.org");
    defer gpa.free(html);
    const has = struct {
        fn f(h: []const u8, s: []const u8) !void {
            if (std.mem.indexOf(u8, h, s) == null) {
                std.debug.print("missing: {s}\n--- in:\n{s}\n", .{ s, h });
                return error.Missing;
            }
        }
    }.f;
    try has(html, "<title>Plan</title>");
    try has(html, "<h2><span class=\"todo\">TODO</span> Ship <b>1.0</b><span class=\"tag\">work</span></h2>");
    try has(html, "<p>Some <i>text</i> with <code>code</code> and <a href=\"https://x.dev\">a link</a>.</p>");
    try has(html, "<ul>\n<li>&#9745; macOS</li>\n<li>&#9744; right click menus</li></ul>");
    try has(html, "<tr><th>a</th><th>b</th></tr>");
    try has(html, "<tr><td>1</td><td>&lt;2&gt;</td></tr>");
    try has(html, "<pre><code class=\"language-zig\">const x = a &lt; b;\n</code></pre>");
    try std.testing.expect(std.mem.indexOf(u8, html, "SCHEDULED") == null);
    try std.testing.expect(std.mem.indexOf(u8, html, ":ID:") == null);
}

test "export markdown" {
    const gpa = std.testing.allocator;
    const md = try toMarkdown(gpa, sample, "plan.org");
    defer gpa.free(md);
    try std.testing.expectEqualStrings(
        \\# Plan
        \\
        \\## TODO Ship **1.0**
        \\
        \\Some *text* with `code` and [a link](https://x.dev).
        \\
        \\- [x] macOS
        \\- [ ] right click menus
        \\
        \\| a | b |
        \\| --- | --- |
        \\| 1 | <2> |
        \\
        \\```zig
        \\const x = a < b;
        \\```
        \\
        \\
    , md);
}
