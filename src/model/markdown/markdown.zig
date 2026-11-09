//! A CommonMark + GFM Markdown parser (headings, paragraphs, lists with task
//! items, block quotes, fenced and indented code, HTML blocks, tables,
//! thematic breaks, reference definitions, YAML front matter) producing the
//! tree in tree.zig. The block structure follows commonmark.js: each line is
//! matched against the open containers, then tried against the block starts.
//!
//! `parse` never fails on any input; the only error is running out of memory.

const std = @import("std");
const tree = @import("tree.zig");
const inline_mod = @import("inline.zig");
const Allocator = std.mem.Allocator;

pub const Block = tree.Block;
pub const Inline = tree.Inline;
pub const Doc = tree.Doc;
pub const Align = tree.Align;

pub const Options = struct {
    /// GFM autolink literals: `www.x.com` and `https://x.com` become links.
    linkify: bool = true,
    /// A source longer than this is cut there.
    max_bytes: usize = 4_000_000,
};

const code_indent = 4;

const Kind = enum { document, block_quote, list, item, paragraph, heading, thematic_break, code_block, html_block, table };

const Node = struct {
    kind: Kind,
    parent: ?*Node = null,
    first: ?*Node = null,
    last: ?*Node = null,
    prev: ?*Node = null,
    next: ?*Node = null,
    open: bool = true,
    last_line_blank: bool = false,
    start_line: u32 = 0,
    end_line: u32 = 0,
    content: std.ArrayList(u8) = .empty,
    // Lists and items.
    ordered: bool = false,
    delim: u8 = 0,
    list_start: u32 = 0,
    tight: bool = true,
    marker_offset: usize = 0,
    padding: usize = 0,
    // Headings.
    level: u8 = 0,
    // Code blocks.
    fenced: bool = false,
    fence_char: u8 = 0,
    fence_len: usize = 0,
    fence_offset: usize = 0,
    front_matter: bool = false,
    /// A fenced block's info string (language and attributes).
    content_info: []const u8 = "",
    html_type: u8 = 0,
    // Tables: the raw cell texts of the header row and the column alignments.
    header: []const []const u8 = &.{},
    aligns: []const Align = &.{},

    fn unlink(n: *Node) void {
        if (n.prev) |p| p.next = n.next else if (n.parent) |par| par.first = n.next;
        if (n.next) |x| x.prev = n.prev else if (n.parent) |par| par.last = n.prev;
        n.parent = null;
        n.next = null;
        n.prev = null;
    }

    fn append(n: *Node, child: *Node) void {
        child.unlink();
        child.parent = n;
        if (n.last) |l| {
            l.next = child;
            child.prev = l;
            n.last = child;
        } else {
            n.first = child;
            n.last = child;
        }
    }

    fn insertAfter(n: *Node, sib: *Node) void {
        sib.unlink();
        sib.next = n.next;
        if (n.next) |x| x.prev = sib;
        sib.prev = n;
        n.next = sib;
        sib.parent = n.parent;
        if (sib.next == null) if (sib.parent) |p| {
            p.last = sib;
        };
    }

    fn canContain(n: *const Node, child: Kind) bool {
        return switch (n.kind) {
            .document, .block_quote, .item => child != .item,
            .list => child == .item,
            else => false,
        };
    }

    fn acceptsLines(n: *const Node) bool {
        return switch (n.kind) {
            .paragraph, .code_block, .html_block, .table => true,
            else => false,
        };
    }
};

const Parser = struct {
    tmp: Allocator,
    doc: *Node,
    tip: *Node,
    old_tip: *Node,
    last_matched: *Node,
    all_closed: bool = true,
    line: []const u8 = "",
    line_number: u32 = 0,
    offset: usize = 0,
    column: usize = 0,
    next_nonspace: usize = 0,
    next_nonspace_column: usize = 0,
    indent: usize = 0,
    indented: bool = false,
    blank: bool = false,
    partially_consumed_tab: bool = false,
    refs: inline_mod.RefMap = .empty,

    fn newNode(self: *Parser, kind: Kind, line: u32) !*Node {
        const n = try self.tmp.create(Node);
        n.* = .{ .kind = kind, .start_line = line, .end_line = line };
        return n;
    }

    fn peek(self: *const Parser, i: usize) ?u8 {
        return if (i < self.line.len) self.line[i] else null;
    }

    fn findNextNonspace(self: *Parser) void {
        var i = self.offset;
        var cols = self.column;
        while (i < self.line.len) {
            const c = self.line[i];
            if (c == ' ') {
                i += 1;
                cols += 1;
            } else if (c == '\t') {
                i += 1;
                cols += 4 - (cols % 4);
            } else break;
        }
        self.blank = i >= self.line.len;
        self.next_nonspace = i;
        self.next_nonspace_column = cols;
        self.indent = cols - self.column;
        self.indented = self.indent >= code_indent;
    }

    fn advanceNextNonspace(self: *Parser) void {
        self.offset = self.next_nonspace;
        self.column = self.next_nonspace_column;
        self.partially_consumed_tab = false;
    }

    fn advanceOffset(self: *Parser, count_in: usize, columns: bool) void {
        var count = count_in;
        while (count > 0 and self.offset < self.line.len) {
            const c = self.line[self.offset];
            if (c == '\t') {
                const to_tab = 4 - (self.column % 4);
                if (columns) {
                    self.partially_consumed_tab = to_tab > count;
                    const adv = @min(to_tab, count);
                    self.column += adv;
                    if (!self.partially_consumed_tab) self.offset += 1;
                    count -= adv;
                } else {
                    self.partially_consumed_tab = false;
                    self.column += to_tab;
                    self.offset += 1;
                    count -= 1;
                }
            } else {
                self.partially_consumed_tab = false;
                self.offset += 1;
                self.column += 1;
                count -= 1;
            }
        }
    }

    fn addLine(self: *Parser) !void {
        if (self.partially_consumed_tab) {
            self.offset += 1;
            const to_tab = 4 - (self.column % 4);
            try self.tip.content.appendNTimes(self.tmp, ' ', to_tab);
        }
        try self.tip.content.appendSlice(self.tmp, self.line[@min(self.offset, self.line.len)..]);
        try self.tip.content.append(self.tmp, '\n');
        self.tip.end_line = self.line_number;
    }

    fn addChild(self: *Parser, kind: Kind) !*Node {
        while (!self.tip.canContain(kind)) try self.finalize(self.tip, self.line_number -| 1);
        const n = try self.newNode(kind, self.line_number);
        self.tip.append(n);
        self.tip = n;
        return n;
    }

    fn closeUnmatchedBlocks(self: *Parser) !void {
        if (self.all_closed) return;
        while (self.old_tip != self.last_matched) {
            const parent = self.old_tip.parent.?;
            try self.finalize(self.old_tip, self.line_number -| 1);
            self.old_tip = parent;
        }
        self.all_closed = true;
    }

    fn finalize(self: *Parser, block: *Node, line: u32) !void {
        const above = block.parent;
        block.open = false;
        // End lines follow the lines a block took, not the blank ones after it.
        _ = line;
        switch (block.kind) {
            .paragraph => {
                const consumed = try self.extractReferences(block.content.items);
                if (consumed > 0) {
                    const rest = block.content.items[consumed..];
                    std.mem.copyForwards(u8, block.content.items[0..rest.len], rest);
                    block.content.shrinkRetainingCapacity(rest.len);
                }
                if (std.mem.indexOfNone(u8, block.content.items, " \t\n") == null) block.unlink();
            },
            .code_block => {
                if (block.fenced) {
                    // The first line is the info string.
                    const c = block.content.items;
                    const nl = std.mem.indexOfScalar(u8, c, '\n') orelse c.len;
                    block.content_info = try self.tmp.dupe(u8, std.mem.trim(u8, c[0..nl], " \t"));
                    const rest = if (nl < c.len) c[nl + 1 ..] else c[c.len..];
                    std.mem.copyForwards(u8, c[0..rest.len], rest);
                    block.content.shrinkRetainingCapacity(rest.len);
                } else {
                    // Trailing blank lines are not part of an indented block.
                    const c = block.content.items;
                    var end = c.len;
                    while (end > 0) {
                        const body_end = end - 1; // the line's '\n'
                        const ls = if (std.mem.lastIndexOfScalar(u8, c[0..body_end], '\n')) |i| i + 1 else 0;
                        if (std.mem.indexOfNone(u8, c[ls..body_end], " \t") != null) break;
                        end = ls;
                    }
                    block.content.shrinkRetainingCapacity(end);
                }
            },
            .html_block => {
                while (block.content.items.len > 0 and block.content.items[block.content.items.len - 1] == '\n') _ = block.content.pop();
            },
            .list => {
                block.tight = true;
                var item = block.first;
                while (item) |it| : (item = it.next) {
                    // A blank line between two items, or between two children of an item.
                    if (endsWithBlankLine(it) and it.next != null) {
                        block.tight = false;
                        break;
                    }
                    var sub = it.first;
                    while (sub) |s| : (sub = s.next) {
                        if (endsWithBlankLine(s) and (it.next != null or s.next != null)) {
                            block.tight = false;
                            break;
                        }
                    }
                    if (!block.tight) break;
                }
            },
            else => {},
        }
        if (above) |a| self.tip = a;
    }

    fn endsWithBlankLine(n: *Node) bool {
        var cur: ?*Node = n;
        while (cur) |b| {
            if (b.last_line_blank) return true;
            cur = if (b.kind == .list or b.kind == .item) b.last else null;
        }
        return false;
    }

    /// Reference definitions at the start of a paragraph; returns their length.
    fn extractReferences(self: *Parser, content: []const u8) !usize {
        if (content.len == 0 or content[0] != '[') return 0;
        var ip: inline_mod.Parser = .{ .tmp = self.tmp, .refs = &self.refs };
        return ip.parseReferences(content);
    }

    // ---- One line --------------------------------------------------------------------

    fn continueBlock(self: *Parser, c: *Node) !enum { matched, failed, done } {
        switch (c.kind) {
            .document, .list => return .matched,
            .block_quote => {
                if (!self.indented and self.peek(self.next_nonspace) == '>') {
                    self.advanceNextNonspace();
                    self.advanceOffset(1, false);
                    if (self.peek(self.offset)) |ch| if (ch == ' ' or ch == '\t') self.advanceOffset(1, true);
                    return .matched;
                }
                return .failed;
            },
            .item => {
                if (self.blank) {
                    if (c.first == null) return .failed;
                    self.advanceNextNonspace();
                } else if (self.indent >= c.marker_offset + c.padding) {
                    self.advanceOffset(c.marker_offset + c.padding, true);
                } else return .failed;
                return .matched;
            },
            .heading, .thematic_break => return .failed,
            .code_block => {
                if (c.fenced) {
                    if (self.indent <= 3) if (closingFence(self.line[self.next_nonspace..], c.fence_char)) |n| if (n >= c.fence_len) {
                        c.end_line = self.line_number;
                        try self.finalize(c, self.line_number);
                        return .done;
                    };
                    var i = c.fence_offset;
                    while (i > 0) : (i -= 1) {
                        const ch = self.peek(self.offset) orelse break;
                        if (ch != ' ' and ch != '\t') break;
                        self.advanceOffset(1, true);
                    }
                } else {
                    if (self.indent >= code_indent) {
                        self.advanceOffset(code_indent, true);
                    } else if (self.blank) {
                        self.advanceNextNonspace();
                    } else return .failed;
                }
                return .matched;
            },
            .html_block => return if (self.blank and (c.html_type == 6 or c.html_type == 7)) .failed else .matched,
            .paragraph => return if (self.blank) .failed else .matched,
            .table => {
                if (self.blank) return .failed;
                if (!self.indented) {
                    const rest = self.line[self.next_nonspace..];
                    if (rest[0] == '>' or atxMarker(rest) != null or fenceStart(rest) != null or isThematicBreak(rest)) return .failed;
                }
                return .matched;
            },
        }
    }

    fn incorporateLine(self: *Parser, ln: []const u8) !void {
        var all_matched = true;
        var container = self.doc;
        self.old_tip = self.tip;
        self.offset = 0;
        self.column = 0;
        self.blank = false;
        self.partially_consumed_tab = false;
        self.line = ln;

        while (container.last) |last| {
            if (!last.open) break;
            container = last;
            self.findNextNonspace();
            switch (try self.continueBlock(container)) {
                .matched => {},
                .failed => all_matched = false,
                .done => return,
            }
            if (!all_matched) {
                container = container.parent.?;
                break;
            }
        }

        self.all_closed = container == self.old_tip;
        self.last_matched = container;

        var matched_leaf = container.kind != .paragraph and container.acceptsLines();
        while (!matched_leaf) {
            self.findNextNonspace();
            if (!self.indented and !maybeSpecial(self.line[self.next_nonspace..])) {
                self.advanceNextNonspace();
                break;
            }
            switch (try self.blockStart(container)) {
                .none => {
                    self.advanceNextNonspace();
                    break;
                },
                .container => container = self.tip,
                .leaf => {
                    container = self.tip;
                    matched_leaf = true;
                },
            }
        }

        if (!self.all_closed and !self.blank and self.tip.kind == .paragraph) {
            // Lazy paragraph continuation.
            try self.addLine();
        } else {
            try self.closeUnmatchedBlocks();
            if (self.blank) if (container.last) |l| {
                l.last_line_blank = true;
            };
            const t = container.kind;
            const last_line_blank = self.blank and !(t == .block_quote or (t == .code_block and container.fenced) or
                (t == .item and container.first == null and container.start_line == self.line_number));
            var cont: ?*Node = container;
            while (cont) |c| : (cont = c.parent) c.last_line_blank = last_line_blank;

            if (container.acceptsLines()) {
                try self.addLine();
                if (t == .html_block and container.html_type >= 1 and container.html_type <= 5 and
                    htmlBlockCloses(container.html_type, self.line[@min(self.offset, self.line.len)..]))
                {
                    try self.finalize(container, self.line_number);
                }
            } else if (self.offset < self.line.len and !self.blank) {
                container = try self.addChild(.paragraph);
                self.advanceNextNonspace();
                try self.addLine();
            }
        }
        // Containers extend to the line.
        if (!self.blank) {
            var up: ?*Node = self.tip;
            while (up) |n| : (up = n.parent) n.end_line = @max(n.end_line, self.line_number);
        }
    }

    const Start = enum { none, container, leaf };

    fn blockStart(self: *Parser, container: *Node) !Start {
        const rest = self.line[self.next_nonspace..];
        if (rest.len == 0) return .none;
        // Block quote.
        if (!self.indented and rest[0] == '>') {
            self.advanceNextNonspace();
            self.advanceOffset(1, false);
            if (self.peek(self.offset)) |ch| if (ch == ' ' or ch == '\t') self.advanceOffset(1, true);
            try self.closeUnmatchedBlocks();
            _ = try self.addChild(.block_quote);
            return .container;
        }
        // ATX heading.
        if (!self.indented) if (atxMarker(rest)) |marker_len| {
            self.advanceNextNonspace();
            self.advanceOffset(marker_len, false);
            try self.closeUnmatchedBlocks();
            const h = try self.addChild(.heading);
            h.level = @intCast(std.mem.indexOfNone(u8, rest, "#") orelse rest.len);
            var text = self.line[@min(self.offset, self.line.len)..];
            text = std.mem.trim(u8, text, " \t");
            // A closing sequence of #s.
            var end = text.len;
            while (end > 0 and text[end - 1] == '#') end -= 1;
            if (end == 0) {
                text = "";
            } else if (end < text.len and (text[end - 1] == ' ' or text[end - 1] == '\t')) {
                text = std.mem.trimEnd(u8, text[0..end], " \t");
            }
            try h.content.appendSlice(self.tmp, text);
            self.advanceOffset(self.line.len - self.offset, false);
            return .leaf;
        };
        // Fenced code.
        if (!self.indented) if (fenceStart(rest)) |fl| {
            try self.closeUnmatchedBlocks();
            const b = try self.addChild(.code_block);
            b.fenced = true;
            b.fence_char = rest[0];
            b.fence_len = fl;
            b.fence_offset = self.indent;
            self.advanceNextNonspace();
            self.advanceOffset(fl, false);
            return .leaf;
        };
        // HTML block.
        if (!self.indented and rest[0] == '<') {
            var t: u8 = 1;
            while (t <= 7) : (t += 1) {
                if (!htmlBlockOpens(t, rest)) continue;
                if (t == 7 and (container.kind == .paragraph or (!self.all_closed and !self.blank and self.tip.kind == .paragraph))) continue;
                try self.closeUnmatchedBlocks();
                const b = try self.addChild(.html_block);
                b.html_type = t;
                return .leaf;
            }
        }
        // Setext heading, or a table whose header is the paragraph.
        if (!self.indented and container.kind == .paragraph) {
            if (setextLevel(rest)) |level| {
                try self.closeUnmatchedBlocks();
                const consumed = try self.extractReferences(container.content.items);
                if (consumed > 0) {
                    const rem = container.content.items[consumed..];
                    std.mem.copyForwards(u8, container.content.items[0..rem.len], rem);
                    container.content.shrinkRetainingCapacity(rem.len);
                }
                if (container.content.items.len > 0) {
                    const h = try self.newNode(.heading, container.start_line);
                    h.level = level;
                    h.end_line = self.line_number;
                    h.content = container.content;
                    container.insertAfter(h);
                    container.unlink();
                    self.tip = h;
                    self.advanceOffset(self.line.len - self.offset, false);
                    return .leaf;
                }
            } else if (try self.tableStart(container, rest)) {
                return .leaf;
            }
        }
        // Thematic break.
        if (!self.indented and isThematicBreak(rest)) {
            try self.closeUnmatchedBlocks();
            _ = try self.addChild(.thematic_break);
            self.advanceOffset(self.line.len - self.offset, false);
            return .leaf;
        }
        // List item.
        if (!self.indented or container.kind == .list) {
            if (try self.listItemStart(container)) return .container;
        }
        // Indented code.
        if (self.indented and self.tip.kind != .paragraph and !self.blank) {
            self.advanceOffset(code_indent, true);
            try self.closeUnmatchedBlocks();
            _ = try self.addChild(.code_block);
            return .leaf;
        }
        return .none;
    }

    fn listItemStart(self: *Parser, container: *Node) !bool {
        const rest = self.line[self.next_nonspace..];
        if (self.indent >= 4) return false;
        var ordered = false;
        var delim: u8 = 0;
        var start: u32 = 0;
        var marker_len: usize = 0;
        if (rest[0] == '*' or rest[0] == '+' or rest[0] == '-') {
            delim = rest[0];
            marker_len = 1;
        } else if (std.ascii.isDigit(rest[0])) {
            var i: usize = 0;
            while (i < rest.len and std.ascii.isDigit(rest[i])) i += 1;
            if (i > 9 or i >= rest.len or (rest[i] != '.' and rest[i] != ')')) return false;
            start = std.fmt.parseInt(u32, rest[0..i], 10) catch return false;
            // Only a list starting with 1 can interrupt a paragraph.
            if (container.kind == .paragraph and start != 1) return false;
            ordered = true;
            delim = rest[i];
            marker_len = i + 1;
        } else return false;
        // Spaces (or the end of the line) must follow the marker.
        const after = self.peek(self.next_nonspace + marker_len);
        if (after != null and after.? != ' ' and after.? != '\t') return false;
        // A list item that interrupts a paragraph cannot start empty.
        if (container.kind == .paragraph and std.mem.indexOfNone(u8, rest[marker_len..], " \t") == null) return false;

        const marker_offset = self.indent;
        try self.closeUnmatchedBlocks();
        self.advanceNextNonspace();
        self.advanceOffset(marker_len, true);
        const spaces_start_col = self.column;
        const spaces_start_offset = self.offset;
        while (true) {
            self.advanceOffset(1, true);
            const nc = self.peek(self.offset);
            if (!(self.column - spaces_start_col < 5 and nc != null and (nc.? == ' ' or nc.? == '\t'))) break;
        }
        const blank_item = self.peek(self.offset) == null;
        const spaces_after = self.column - spaces_start_col;
        var padding: usize = undefined;
        if (spaces_after >= 5 or spaces_after < 1 or blank_item) {
            padding = marker_len + 1;
            self.column = spaces_start_col;
            self.offset = spaces_start_offset;
            if (self.peek(self.offset)) |ch| if (ch == ' ' or ch == '\t') self.advanceOffset(1, true);
        } else {
            padding = marker_len + spaces_after;
        }

        // Start a list unless this continues the tip's list.
        const same_list = self.tip.kind == .list and self.tip.ordered == ordered and self.tip.delim == delim;
        if (!same_list) {
            const l = try self.addChild(.list);
            l.ordered = ordered;
            l.delim = delim;
            l.list_start = start;
            l.marker_offset = marker_offset;
            l.padding = padding;
        }
        const item = try self.addChild(.item);
        item.ordered = ordered;
        item.delim = delim;
        item.list_start = start;
        item.marker_offset = marker_offset;
        item.padding = padding;
        return true;
    }

    fn tableStart(self: *Parser, container: *Node, rest: []const u8) !bool {
        // The header row must be the paragraph's only line.
        const header_line = std.mem.trim(u8, container.content.items, " \t\n");
        if (header_line.len == 0 or std.mem.indexOfScalar(u8, header_line, '\n') != null) return false;
        if (std.mem.indexOfScalar(u8, header_line, '|') == null and std.mem.indexOfScalar(u8, rest, '|') == null) return false;
        const aligns = try delimiterRow(self.tmp, rest) orelse return false;
        const header = try splitRow(self.tmp, header_line);
        if (header.len != aligns.len) return false;
        try self.closeUnmatchedBlocks();
        const t = try self.newNode(.table, container.start_line);
        t.end_line = self.line_number;
        t.header = header;
        t.aligns = aligns;
        container.insertAfter(t);
        container.unlink();
        self.tip = t;
        self.advanceOffset(self.line.len - self.offset, false);
        return true;
    }
};

// ---- Line classifiers ---------------------------------------------------------------------

fn maybeSpecial(s: []const u8) bool {
    if (s.len == 0) return false;
    return switch (s[0]) {
        '#', '`', '~', '*', '+', '_', '=', '<', '>', '-', '|', ':' => true,
        '0'...'9' => true,
        else => false,
    };
}

/// Length of an ATX marker (`#`..`######` plus its space), or null.
fn atxMarker(s: []const u8) ?usize {
    var n: usize = 0;
    while (n < s.len and s[n] == '#') n += 1;
    if (n == 0 or n > 6) return null;
    if (n == s.len) return n;
    if (s[n] == ' ' or s[n] == '\t') {
        var m = n;
        while (m < s.len and (s[m] == ' ' or s[m] == '\t')) m += 1;
        return m;
    }
    return null;
}

/// The length of an opening code fence, or null.
fn fenceStart(s: []const u8) ?usize {
    if (s.len < 3) return null;
    const c = s[0];
    if (c != '`' and c != '~') return null;
    var n: usize = 0;
    while (n < s.len and s[n] == c) n += 1;
    if (n < 3) return null;
    // A backtick fence's info string cannot contain a backtick.
    if (c == '`' and std.mem.indexOfScalar(u8, s[n..], '`') != null) return null;
    return n;
}

fn closingFence(s: []const u8, ch: u8) ?usize {
    if (s.len == 0 or s[0] != ch) return null;
    var n: usize = 0;
    while (n < s.len and s[n] == ch) n += 1;
    if (n < 3) return null;
    if (std.mem.indexOfNone(u8, s[n..], " \t") != null) return null;
    return n;
}

fn isThematicBreak(s: []const u8) bool {
    if (s.len < 3) return false;
    const c = s[0];
    if (c != '*' and c != '-' and c != '_') return false;
    var n: usize = 0;
    for (s) |ch| {
        if (ch == c) n += 1 else if (ch != ' ' and ch != '\t') return false;
    }
    return n >= 3;
}

fn setextLevel(s: []const u8) ?u8 {
    if (s.len == 0 or (s[0] != '=' and s[0] != '-')) return null;
    const c = s[0];
    var i: usize = 0;
    while (i < s.len and s[i] == c) i += 1;
    if (std.mem.indexOfNone(u8, s[i..], " \t") != null) return null;
    return if (c == '=') 1 else 2;
}

const html_block_names = [_][]const u8{
    "address",  "article",  "aside",    "base",     "basefont", "blockquote", "body",    "caption",
    "center",   "col",      "colgroup", "dd",       "details",  "dialog",     "dir",     "div",
    "dl",       "dt",       "fieldset", "figcaption", "figure", "footer",     "form",    "frame",
    "frameset", "h1",       "h2",       "h3",       "h4",       "h5",         "h6",      "head",
    "header",   "hr",       "html",     "iframe",   "legend",   "li",         "link",    "main",
    "menu",     "menuitem", "nav",      "noframes", "ol",       "optgroup",   "option",  "p",
    "param",    "section",  "source",   "summary",  "table",    "tbody",      "td",      "tfoot",
    "th",       "thead",    "title",    "tr",       "track",    "ul",
};

fn htmlBlockOpens(t: u8, s: []const u8) bool {
    switch (t) {
        1 => {
            for ([_][]const u8{ "script", "pre", "textarea", "style" }) |name| {
                if (s.len > name.len and std.ascii.eqlIgnoreCase(s[1 .. 1 + name.len], name)) {
                    if (s.len == 1 + name.len) return true;
                    const c = s[1 + name.len];
                    if (c == ' ' or c == '\t' or c == '>') return true;
                }
            }
            return false;
        },
        2 => return std.mem.startsWith(u8, s, "<!--"),
        3 => return std.mem.startsWith(u8, s, "<?"),
        4 => return s.len > 2 and s[1] == '!' and std.ascii.isAlphabetic(s[2]),
        5 => return std.mem.startsWith(u8, s, "<![CDATA["),
        6 => {
            var i: usize = 1;
            if (i < s.len and s[i] == '/') i += 1;
            const start = i;
            while (i < s.len and std.ascii.isAlphanumeric(s[i])) i += 1;
            const name = s[start..i];
            for (html_block_names) |n| if (std.ascii.eqlIgnoreCase(n, name)) {
                if (i >= s.len) return true;
                const c = s[i];
                return c == ' ' or c == '\t' or c == '>' or (c == '/' and i + 1 < s.len and s[i + 1] == '>');
            };
            return false;
        },
        7 => {
            const end = inline_mod.Parser.scanTag(s, 0) orelse return false;
            if (s.len > 1 and s[1] != '/' and !std.ascii.isAlphabetic(s[1])) return false;
            return std.mem.indexOfNone(u8, s[end..], " \t") == null;
        },
        else => return false,
    }
}

fn htmlBlockCloses(t: u8, s: []const u8) bool {
    return switch (t) {
        1 => std.ascii.findIgnoreCase(s, "</script>") != null or std.ascii.findIgnoreCase(s, "</pre>") != null or
            std.ascii.findIgnoreCase(s, "</textarea>") != null or std.ascii.findIgnoreCase(s, "</style>") != null,
        2 => std.mem.indexOf(u8, s, "-->") != null,
        3 => std.mem.indexOf(u8, s, "?>") != null,
        4 => std.mem.indexOfScalar(u8, s, '>') != null,
        5 => std.mem.indexOf(u8, s, "]]>") != null,
        else => false,
    };
}

// ---- Tables ---------------------------------------------------------------------------------------

/// The cells of a table row: split on unescaped pipes, trimmed, `\|` unescaped.
fn splitRow(a: Allocator, line_in: []const u8) ![]const []const u8 {
    var line = std.mem.trim(u8, line_in, " \t");
    if (line.len > 0 and line[0] == '|') line = line[1..];
    var cells: std.ArrayList([]const u8) = .empty;
    var cur: std.ArrayList(u8) = .empty;
    var i: usize = 0;
    var in_code: ?usize = null;
    while (i < line.len) : (i += 1) {
        const c = line[i];
        if (c == '\\' and i + 1 < line.len and line[i + 1] == '|') {
            try cur.append(a, '|');
            i += 1;
        } else if (c == '`') {
            var n: usize = 0;
            while (i + n < line.len and line[i + n] == '`') n += 1;
            try cur.appendSlice(a, line[i .. i + n]);
            if (in_code) |open| {
                if (open == n) in_code = null;
            } else in_code = n;
            i += n - 1;
        } else if (c == '|' and in_code == null) {
            try cells.append(a, std.mem.trim(u8, cur.items, " \t"));
            cur = .empty;
        } else {
            try cur.append(a, c);
        }
    }
    const last = std.mem.trim(u8, cur.items, " \t");
    // A trailing pipe leaves an empty last cell that is not a column.
    if (last.len > 0 or line.len == 0 or line[line.len - 1] != '|') try cells.append(a, last);
    return cells.items;
}

/// The alignments of a delimiter row (`| --- | :-: |`), or null if it is not one.
fn delimiterRow(a: Allocator, s_in: []const u8) !?[]const Align {
    const s = std.mem.trim(u8, s_in, " \t");
    if (s.len == 0) return null;
    for (s) |c| if (c != '|' and c != '-' and c != ':' and c != ' ' and c != '\t') return null;
    const cells = try splitRow(a, s);
    if (cells.len == 0) return null;
    if (cells.len == 1 and std.mem.indexOfScalar(u8, s, '|') == null) return null;
    var aligns = try a.alloc(Align, cells.len);
    for (cells, 0..) |cell, i| {
        if (cell.len == 0) return null;
        const left = cell[0] == ':';
        const right = cell[cell.len - 1] == ':';
        const dashes = cell[@intFromBool(left) .. cell.len - @intFromBool(right)];
        if (dashes.len == 0 or std.mem.indexOfNone(u8, dashes, "-") != null) return null;
        aligns[i] = if (left and right) .center else if (left) .left else if (right) .right else .none;
    }
    return aligns;
}

// ---- Output ---------------------------------------------------------------------------------------

const Converter = struct {
    out: Allocator,
    tmp: Allocator,
    refs: *inline_mod.RefMap,
    options: Options,
    slugs: std.StringHashMapUnmanaged(u32) = .empty,

    fn inlines(self: *Converter, text: []const u8) ![]const Inline {
        return inline_mod.parseInlines(self.out, self.tmp, self.refs, std.mem.trim(u8, text, " \t\n"), self.options.linkify);
    }

    fn blocks(self: *Converter, parent: *Node) Allocator.Error![]const Block {
        var list: std.ArrayList(Block) = .empty;
        var cur = parent.first;
        while (cur) |n| : (cur = n.next) {
            if (try self.block(n)) |b| try list.append(self.out, b);
        }
        return list.items;
    }

    fn block(self: *Converter, n: *Node) Allocator.Error!?Block {
        const data: Block.Data = switch (n.kind) {
            .paragraph => .{ .paragraph = try self.inlines(n.content.items) },
            .heading => blk: {
                const ins = try self.inlines(n.content.items);
                break :blk .{ .heading = .{ .level = n.level, .inlines = ins, .id = try self.slug(ins) } };
            },
            .code_block => .{ .code = .{
                .lang = try self.out.dupe(u8, firstWord(try inline_mod.unescape(self.tmp, n.content_info))),
                .text = try self.out.dupe(u8, n.content.items),
                .fenced = n.fenced,
                .front_matter = n.front_matter,
            } },
            .html_block => .{ .html = try self.out.dupe(u8, n.content.items) },
            .thematic_break => .rule,
            .block_quote => .{ .quote = try self.blocks(n) },
            .list => blk: {
                var items: std.ArrayList(tree.Item) = .empty;
                var cur = n.first;
                while (cur) |it| : (cur = it.next) try items.append(self.out, try self.item(it));
                break :blk .{ .list = .{ .ordered = n.ordered, .start = n.list_start, .tight = n.tight, .delim = n.delim, .items = items.items } };
            },
            .table => blk: {
                var header = try self.out.alloc(tree.Cell, n.header.len);
                for (n.header, 0..) |cell, i| header[i] = try self.inlines(cell);
                var rows: std.ArrayList(tree.Row) = .empty;
                var it = std.mem.splitScalar(u8, n.content.items, '\n');
                while (it.next()) |line| {
                    if (std.mem.trim(u8, line, " \t").len == 0) continue;
                    const raw = try splitRow(self.tmp, line);
                    const row = try self.out.alloc(tree.Cell, n.header.len);
                    for (row, 0..) |*cell, i| cell.* = if (i < raw.len) try self.inlines(raw[i]) else &.{};
                    try rows.append(self.out, row);
                }
                break :blk .{ .table = .{ .aligns = try self.out.dupe(Align, n.aligns), .header = header, .rows = rows.items } };
            },
            .document, .item => return null,
        };
        return .{ .start_line = n.start_line, .end_line = @max(n.start_line, n.end_line), .data = data };
    }

    fn item(self: *Converter, n: *Node) !tree.Item {
        var task: ?bool = null;
        // A `[ ]` or `[x]` at the start of the first paragraph is a task box.
        if (n.first) |f| if (f.kind == .paragraph) {
            const c = f.content.items;
            if (c.len >= 3 and c[0] == '[' and c[2] == ']' and (c.len == 3 or c[3] == ' ' or c[3] == '\n' or c[3] == '\t') and
                (c[1] == ' ' or c[1] == 'x' or c[1] == 'X'))
            {
                task = c[1] != ' ';
                const rest = std.mem.trimStart(u8, c[3..], " \t");
                std.mem.copyForwards(u8, f.content.items[0..rest.len], rest);
                f.content.shrinkRetainingCapacity(rest.len);
            }
        };
        return .{ .blocks = try self.blocks(n), .task = task, .start_line = n.start_line };
    }

    /// GitHub's heading id: lowercase, punctuation dropped, spaces to hyphens,
    /// repeats numbered.
    fn slug(self: *Converter, ins: []const Inline) ![]const u8 {
        var text: std.ArrayList(u8) = .empty;
        try tree.plainText(self.tmp, &text, ins);
        var s: std.ArrayList(u8) = .empty;
        var it = std.unicode.Utf8View.initUnchecked(text.items).iterator();
        while (it.nextCodepoint()) |cp| {
            if (cp == ' ') {
                try s.append(self.tmp, '-');
            } else if (cp < 0x80) {
                const c: u8 = @intCast(cp);
                if (std.ascii.isAlphanumeric(c) or c == '-' or c == '_') try s.append(self.tmp, std.ascii.toLower(c));
            } else {
                var buf: [4]u8 = undefined;
                const n = std.unicode.utf8Encode(lowerCodepoint(cp), &buf) catch continue;
                try s.appendSlice(self.tmp, buf[0..n]);
            }
        }
        const gop = try self.slugs.getOrPut(self.tmp, try self.tmp.dupe(u8, s.items));
        if (!gop.found_existing) {
            gop.value_ptr.* = 0;
            return self.out.dupe(u8, s.items);
        }
        gop.value_ptr.* += 1;
        return std.fmt.allocPrint(self.out, "{s}-{d}", .{ s.items, gop.value_ptr.* });
    }
};

/// Simple lowercase for Latin, Greek and Cyrillic capitals (heading ids).
fn lowerCodepoint(cp: u21) u21 {
    return switch (cp) {
        0xC0...0xD6, 0xD8...0xDE => cp + 0x20,
        0x100...0x137, 0x14A...0x177 => if (cp % 2 == 0) cp + 1 else cp,
        0x139...0x148, 0x179...0x17E => if (cp % 2 == 1) cp + 1 else cp,
        0x391...0x3A1, 0x3A3...0x3A9 => cp + 0x20,
        0x410...0x42F => cp + 0x20,
        0x400...0x40F => cp + 0x50,
        else => cp,
    };
}

fn firstWord(s: []const u8) []const u8 {
    const end = std.mem.indexOfAny(u8, s, " \t") orelse s.len;
    return s[0..end];
}

/// Parses `source`. The result owns its memory (`Doc.deinit`).
pub fn parse(gpa: Allocator, source_in: []const u8, options: Options) Allocator.Error!Doc {
    var out_arena = std.heap.ArenaAllocator.init(gpa);
    errdefer out_arena.deinit();
    var tmp_arena = std.heap.ArenaAllocator.init(gpa);
    defer tmp_arena.deinit();
    const tmp = tmp_arena.allocator();

    var source = source_in[0..@min(source_in.len, options.max_bytes)];
    // Cut at a character boundary.
    while (source.len > 0 and source.len < source_in.len and (source_in[source.len] & 0xC0) == 0x80) source = source[0 .. source.len - 1];

    var p: Parser = undefined;
    const doc = try tmp.create(Node);
    doc.* = .{ .kind = .document };
    p = .{ .tmp = tmp, .doc = doc, .tip = doc, .old_tip = doc, .last_matched = doc };

    // YAML front matter: `---` on the first line to the next `---` or `...`.
    var line_no: u32 = 0;
    var front: ?struct { text: []const u8, lines: u32 } = null;
    if (std.mem.startsWith(u8, source, "---\n") or std.mem.startsWith(u8, source, "---\r\n")) {
        var pos: usize = std.mem.indexOfScalar(u8, source, '\n').? + 1;
        var n: u32 = 1;
        while (pos < source.len) {
            const end = std.mem.indexOfScalarPos(u8, source, pos, '\n') orelse source.len;
            const l = std.mem.trimEnd(u8, source[pos..end], "\r");
            n += 1;
            if (std.mem.eql(u8, l, "---") or std.mem.eql(u8, l, "...")) {
                const body_start = std.mem.indexOfScalar(u8, source, '\n').? + 1;
                // `---` straight after `---` is two rules.
                if (pos == body_start) break;
                front = .{ .text = source[body_start..pos], .lines = n };
                source = source[@min(end + 1, source.len)..];
                break;
            }
            pos = end + 1;
        }
    }

    var lines = std.mem.splitScalar(u8, source, '\n');
    var counted: u32 = 0;
    line_no = if (front) |f| f.lines else 0;
    while (lines.next()) |raw| {
        // A final empty piece after a trailing newline is not a line.
        if (lines.peek() == null and raw.len == 0) break;
        var l = raw;
        if (l.len > 0 and l[l.len - 1] == '\r') l = l[0 .. l.len - 1];
        p.line_number = line_no;
        try p.incorporateLine(l);
        line_no += 1;
        counted += 1;
    }
    while (p.tip != doc) try p.finalize(p.tip, if (line_no > 0) line_no - 1 else 0);

    var conv: Converter = .{ .out = out_arena.allocator(), .tmp = tmp, .refs = &p.refs, .options = options };
    var list: std.ArrayList(Block) = .empty;
    if (front) |f| try list.append(conv.out, .{ .start_line = 0, .end_line = f.lines - 1, .data = .{ .code = .{
        .lang = "yaml",
        .text = try conv.out.dupe(u8, f.text),
        .fenced = true,
        .front_matter = true,
    } } });
    try list.appendSlice(conv.out, try conv.blocks(doc));
    return .{ .arena = out_arena, .blocks = list.items, .lines = line_no };
}
