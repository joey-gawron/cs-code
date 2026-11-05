//! Renders a parsed document as HTML in the format of the CommonMark
//! reference implementation. Only the tests use it: they check the parser
//! against the spec's examples, which are written as HTML.

const std = @import("std");
const tree = @import("tree.zig");
const Allocator = std.mem.Allocator;
const Block = tree.Block;
const Inline = tree.Inline;

pub const Renderer = struct {
    a: Allocator,
    out: std.ArrayList(u8) = .empty,

    fn lit(self: *Renderer, s: []const u8) !void {
        try self.out.appendSlice(self.a, s);
    }

    fn cr(self: *Renderer) !void {
        if (self.out.items.len > 0 and self.out.items[self.out.items.len - 1] != '\n') try self.out.append(self.a, '\n');
    }

    fn esc(self: *Renderer, s: []const u8) !void {
        for (s) |c| switch (c) {
            '&' => try self.lit("&amp;"),
            '<' => try self.lit("&lt;"),
            '>' => try self.lit("&gt;"),
            '"' => try self.lit("&quot;"),
            else => try self.out.append(self.a, c),
        };
    }

    /// mdurl-style percent encoding of a link destination.
    fn url(self: *Renderer, s: []const u8) !void {
        var i: usize = 0;
        while (i < s.len) : (i += 1) {
            const c = s[i];
            if (std.ascii.isAlphanumeric(c) or std.mem.indexOfScalar(u8, ";/?:@&=+$,-_.!~*'()#", c) != null) {
                if (c == '&') try self.lit("&amp;") else if (c == '\'') try self.lit("&#x27;") else try self.out.append(self.a, c);
            } else if (c == '%' and i + 2 < s.len and std.ascii.isHex(s[i + 1]) and std.ascii.isHex(s[i + 2])) {
                try self.out.append(self.a, c);
            } else {
                var b: [3]u8 = undefined;
                _ = std.fmt.bufPrint(&b, "%{X:0>2}", .{c}) catch unreachable;
                try self.lit(&b);
            }
        }
    }

    fn inlines(self: *Renderer, list: []const Inline) !void {
        for (list) |n| switch (n) {
            .text => |t| try self.esc(t),
            .code => |t| {
                try self.lit("<code>");
                try self.esc(t);
                try self.lit("</code>");
            },
            .em => |c| {
                try self.lit("<em>");
                try self.inlines(c);
                try self.lit("</em>");
            },
            .strong => |c| {
                try self.lit("<strong>");
                try self.inlines(c);
                try self.lit("</strong>");
            },
            .strike => |c| {
                try self.lit("<del>");
                try self.inlines(c);
                try self.lit("</del>");
            },
            .link => |l| {
                try self.lit("<a href=\"");
                try self.url(l.url);
                try self.lit("\"");
                if (l.title.len > 0) {
                    try self.lit(" title=\"");
                    try self.esc(l.title);
                    try self.lit("\"");
                }
                try self.lit(">");
                try self.inlines(l.children);
                try self.lit("</a>");
            },
            .image => |im| {
                try self.lit("<img src=\"");
                try self.url(im.url);
                try self.lit("\" alt=\"");
                var alt: std.ArrayList(u8) = .empty;
                defer alt.deinit(self.a);
                try tree.plainText(self.a, &alt, im.alt);
                try self.esc(alt.items);
                try self.lit("\"");
                if (im.title.len > 0) {
                    try self.lit(" title=\"");
                    try self.esc(im.title);
                    try self.lit("\"");
                }
                try self.lit(" />");
            },
            .soft_break => try self.lit("\n"),
            .hard_break => try self.lit("<br />\n"),
        };
    }

    pub fn blocks(self: *Renderer, list: []const Block, tight: bool) Allocator.Error!void {
        for (list) |b| try self.block(b, tight);
    }

    fn block(self: *Renderer, b: Block, tight: bool) Allocator.Error!void {
        switch (b.data) {
            .paragraph => |ins| {
                if (tight) {
                    try self.inlines(ins);
                    return;
                }
                try self.cr();
                try self.lit("<p>");
                try self.inlines(ins);
                try self.lit("</p>");
                try self.cr();
            },
            .heading => |h| {
                try self.cr();
                try self.out.print(self.a, "<h{d}>", .{h.level});
                try self.inlines(h.inlines);
                try self.out.print(self.a, "</h{d}>", .{h.level});
                try self.cr();
            },
            .code => |c| {
                try self.cr();
                try self.lit("<pre><code");
                if (c.lang.len > 0) {
                    try self.lit(" class=\"language-");
                    try self.esc(c.lang);
                    try self.lit("\"");
                }
                try self.lit(">");
                try self.esc(c.text);
                try self.lit("</code></pre>");
                try self.cr();
            },
            .quote => |children| {
                try self.cr();
                try self.lit("<blockquote>");
                try self.cr();
                try self.blocks(children, false);
                try self.cr();
                try self.lit("</blockquote>");
                try self.cr();
            },
            .list => |l| {
                try self.cr();
                if (l.ordered) {
                    if (l.start != 1) try self.out.print(self.a, "<ol start=\"{d}\">", .{l.start}) else try self.lit("<ol>");
                } else try self.lit("<ul>");
                try self.cr();
                for (l.items) |item| {
                    try self.lit("<li>");
                    if (item.task) |done| try self.lit(if (done) "<input type=\"checkbox\" disabled=\"\" checked=\"\" /> " else "<input type=\"checkbox\" disabled=\"\" /> ");
                    try self.blocks(item.blocks, l.tight);
                    try self.lit("</li>");
                    try self.cr();
                }
                try self.cr();
                try self.lit(if (l.ordered) "</ol>" else "</ul>");
                try self.cr();
            },
            .table => |t| {
                try self.cr();
                try self.lit("<table>\n<thead>\n<tr>\n");
                for (t.header, 0..) |cell, i| try self.tableCell("th", t.aligns[i], cell);
                try self.lit("</tr>\n</thead>\n");
                if (t.rows.len > 0) {
                    try self.lit("<tbody>\n");
                    for (t.rows) |row| {
                        try self.lit("<tr>\n");
                        for (row, 0..) |cell, i| try self.tableCell("td", t.aligns[i], cell);
                        try self.lit("</tr>\n");
                    }
                    try self.lit("</tbody>\n");
                }
                try self.lit("</table>");
                try self.cr();
            },
            .rule => {
                try self.cr();
                try self.lit("<hr />");
                try self.cr();
            },
            .html => |h| {
                try self.cr();
                try self.lit(h);
                try self.cr();
            },
        }
    }

    fn tableCell(self: *Renderer, tag: []const u8, al: tree.Align, ins: []const Inline) !void {
        try self.out.print(self.a, "<{s}", .{tag});
        switch (al) {
            .none => {},
            .left => try self.lit(" align=\"left\""),
            .center => try self.lit(" align=\"center\""),
            .right => try self.lit(" align=\"right\""),
        }
        try self.lit(">");
        try self.inlines(ins);
        try self.out.print(self.a, "</{s}>\n", .{tag});
    }
};

/// The document as HTML (caller frees).
pub fn render(a: Allocator, doc: *const tree.Doc) ![]u8 {
    var r: Renderer = .{ .a = a };
    errdefer r.out.deinit(a);
    try r.blocks(doc.blocks, false);
    return r.out.toOwnedSlice(a);
}
