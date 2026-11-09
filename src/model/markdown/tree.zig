//! The tree a parsed Markdown document is turned into: what the preview lays
//! out. Blocks know the source lines they span (for scroll sync).

const std = @import("std");

pub const Align = enum { none, left, center, right };

pub const Inline = union(enum) {
    text: []const u8,
    code: []const u8,
    em: []const Inline,
    strong: []const Inline,
    strike: []const Inline,
    link: Link,
    image: Image,
    soft_break,
    hard_break,

    pub const Link = struct { url: []const u8, title: []const u8, children: []const Inline };
    pub const Image = struct { url: []const u8, title: []const u8, alt: []const Inline };
};

pub const Cell = []const Inline;
pub const Row = []const Cell;

pub const Item = struct {
    blocks: []const Block,
    /// `- [ ]` is false, `- [x]` is true, null for an ordinary item.
    task: ?bool,
    start_line: u32,
};

pub const Block = struct {
    /// 0-based source lines, inclusive.
    start_line: u32,
    end_line: u32,
    data: Data,

    pub const Data = union(enum) {
        paragraph: []const Inline,
        heading: Heading,
        code: Code,
        quote: []const Block,
        list: List,
        table: Table,
        rule,
        html: []const u8,
    };

    pub const Heading = struct { level: u8, inlines: []const Inline, id: []const u8 };
    pub const Code = struct { lang: []const u8, text: []const u8, fenced: bool, front_matter: bool = false };
    pub const List = struct { ordered: bool, start: u32, tight: bool, delim: u8, items: []const Item };
    pub const Table = struct { aligns: []const Align, header: Row, rows: []const Row };
};

pub const Doc = struct {
    arena: std.heap.ArenaAllocator,
    blocks: []const Block,
    /// The source's line count.
    lines: u32,

    pub fn deinit(self: *Doc) void {
        self.arena.deinit();
    }
};

/// The text of inlines without markup (heading ids, alt text, copy).
pub fn plainText(a: std.mem.Allocator, out: *std.ArrayList(u8), inlines: []const Inline) std.mem.Allocator.Error!void {
    for (inlines) |n| switch (n) {
        .text, .code => |t| try out.appendSlice(a, t),
        .em, .strong, .strike => |c| try plainText(a, out, c),
        .link => |l| try plainText(a, out, l.children),
        .image => |i| try plainText(a, out, i.alt),
        .soft_break, .hard_break => try out.append(a, ' '),
    };
}
