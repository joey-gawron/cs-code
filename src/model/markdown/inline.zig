//! Inline parsing for CommonMark + GFM: code spans, emphasis, strikethrough,
//! links and images (inline and reference), autolinks, entities, escapes and
//! hard breaks. A port of the algorithm in commonmark.js: a flat list of nodes
//! with a delimiter stack for emphasis and a bracket stack for links.
//!
//! Everything lives in a temporary arena owned by the caller; `convert` copies
//! the finished tree into the output arena.

const std = @import("std");
const tree = @import("tree.zig");
const Allocator = std.mem.Allocator;
const Inline = tree.Inline;

pub const Ref = struct { url: []const u8, title: []const u8 };
pub const RefMap = std.StringHashMapUnmanaged(Ref);

const Kind = enum { root, text, code, softbreak, linebreak, em, strong, strike, link, image };

const Node = struct {
    kind: Kind,
    text: []const u8 = "",
    url: []const u8 = "",
    title: []const u8 = "",
    parent: ?*Node = null,
    first: ?*Node = null,
    last: ?*Node = null,
    prev: ?*Node = null,
    next: ?*Node = null,

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
};

const Delim = struct {
    cc: u8,
    num: usize,
    orig: usize,
    node: *Node,
    prev: ?*Delim,
    next: ?*Delim = null,
    can_open: bool,
    can_close: bool,
};

const Bracket = struct {
    node: *Node,
    prev: ?*Bracket,
    prev_delim: ?*Delim,
    index: usize,
    image: bool,
    active: bool = true,
    bracket_after: bool = false,
};

// ---- Character classes -------------------------------------------------------

fn isAsciiPunct(c: u8) bool {
    return (c >= 33 and c <= 47) or (c >= 58 and c <= 64) or (c >= 91 and c <= 96) or (c >= 123 and c <= 126);
}

fn isUnicodeWhitespace(cp: u21) bool {
    return switch (cp) {
        ' ', '\t', '\n', 0x0B, 0x0C, '\r', 0xA0, 0x1680, 0x2000...0x200A, 0x202F, 0x205F, 0x3000 => true,
        else => false,
    };
}

fn isUnicodePunct(cp: u21) bool {
    if (cp < 128) return isAsciiPunct(@intCast(cp));
    return switch (cp) {
        0xA1, 0xA7, 0xAB, 0xB6, 0xB7, 0xBB, 0xBF => true,
        0x2010...0x2027, 0x2030...0x205E => true,
        0x3001...0x3003, 0x3008...0x3011, 0x3014...0x301F => true,
        0xFF01...0xFF0F, 0xFF1A...0xFF20, 0xFF3B...0xFF40, 0xFF5B...0xFF65 => true,
        0x20A0...0x20C0 => true, // currency symbols
        else => false,
    };
}

fn isSpaceOrTab(c: u8) bool {
    return c == ' ' or c == '\t';
}

fn isAsciiAlnum(c: u8) bool {
    return std.ascii.isAlphanumeric(c);
}

// ---- Entities --------------------------------------------------------------------

const entities = [_]struct { []const u8, []const u8 }{
    .{ "amp", "&" },        .{ "lt", "<" },         .{ "gt", ">" },         .{ "quot", "\"" },
    .{ "apos", "'" },       .{ "nbsp", "\u{a0}" },  .{ "copy", "©" },       .{ "reg", "®" },
    .{ "trade", "™" },      .{ "mdash", "—" },      .{ "ndash", "–" },      .{ "hellip", "…" },
    .{ "laquo", "«" },      .{ "raquo", "»" },      .{ "lsquo", "‘" },      .{ "rsquo", "’" },
    .{ "ldquo", "“" },      .{ "rdquo", "”" },      .{ "bull", "•" },       .{ "middot", "·" },
    .{ "times", "×" },      .{ "divide", "÷" },     .{ "deg", "°" },        .{ "plusmn", "±" },
    .{ "euro", "€" },       .{ "pound", "£" },      .{ "yen", "¥" },        .{ "cent", "¢" },
    .{ "sect", "§" },       .{ "para", "¶" },       .{ "larr", "←" },       .{ "rarr", "→" },
    .{ "uarr", "↑" },       .{ "darr", "↓" },       .{ "harr", "↔" },       .{ "hearts", "♥" },
    .{ "Auml", "Ä" },       .{ "auml", "ä" },       .{ "Ouml", "Ö" },       .{ "ouml", "ö" },
    .{ "Uuml", "Ü" },       .{ "uuml", "ü" },       .{ "szlig", "ß" },      .{ "eacute", "é" },
    .{ "egrave", "è" },     .{ "agrave", "à" },     .{ "ccedil", "ç" },     .{ "ntilde", "ñ" },
    .{ "ne", "≠" },         .{ "le", "≤" },         .{ "ge", "≥" },         .{ "infin", "∞" },
    .{ "ZeroWidthSpace", "\u{200b}" }, .{ "ThickSpace", "\u{205f}\u{200a}" },
    .{ "ngE", "≧\u{338}" }, .{ "Dcaron", "Ď" },     .{ "HilbertSpace", "ℋ" }, .{ "DifferentialD", "ⅆ" },
    .{ "ClockwiseContourIntegral", "∲" },           .{ "ouml", "ö" },
};

/// Decodes the entity at `s[0]` (which is '&'); returns its length and text.
pub fn decodeEntity(buf: *[8]u8, s: []const u8) ?struct { len: usize, text: []const u8 } {
    if (s.len < 3 or s[0] != '&') return null;
    const semi = std.mem.indexOfScalar(u8, s[0..@min(s.len, 34)], ';') orelse return null;
    const body = s[1..semi];
    if (body.len == 0) return null;
    if (body[0] == '#') {
        const hex = body.len > 1 and (body[1] == 'x' or body[1] == 'X');
        const digits = if (hex) body[2..] else body[1..];
        if (digits.len == 0 or digits.len > (if (hex) @as(usize, 6) else 7)) return null;
        const cp = std.fmt.parseInt(u32, digits, if (hex) 16 else 10) catch return null;
        const valid: u21 = if (cp == 0 or cp > 0x10FFFF or (cp >= 0xD800 and cp <= 0xDFFF)) 0xFFFD else @intCast(cp);
        const n = std.unicode.utf8Encode(valid, buf) catch return null;
        return .{ .len = semi + 1, .text = buf[0..n] };
    }
    for (entities) |e| if (std.mem.eql(u8, e[0], body)) return .{ .len = semi + 1, .text = e[1] };
    return null;
}

/// Backslash escapes and entities resolved.
pub fn unescape(a: Allocator, s: []const u8) ![]const u8 {
    if (std.mem.indexOfAny(u8, s, "\\&") == null) return s;
    var out: std.ArrayList(u8) = .empty;
    var i: usize = 0;
    while (i < s.len) {
        const c = s[i];
        if (c == '\\' and i + 1 < s.len and isAsciiPunct(s[i + 1])) {
            try out.append(a, s[i + 1]);
            i += 2;
        } else if (c == '&') {
            var buf: [8]u8 = undefined;
            if (decodeEntity(&buf, s[i..])) |e| {
                try out.appendSlice(a, e.text);
                i += e.len;
            } else {
                try out.append(a, c);
                i += 1;
            }
        } else {
            try out.append(a, c);
            i += 1;
        }
    }
    return out.items;
}

/// A reference label as keyed in the map: trimmed, whitespace collapsed, lowercased.
pub fn normalizeLabel(a: Allocator, label: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var pending_space = false;
    for (label) |c| {
        if (std.ascii.isWhitespace(c)) {
            pending_space = out.items.len > 0;
            continue;
        }
        if (pending_space) try out.append(a, ' ');
        pending_space = false;
        try out.append(a, std.ascii.toLower(c));
    }
    return out.items;
}

// ---- The parser ---------------------------------------------------------------

pub const Parser = struct {
    tmp: Allocator,
    refs: *RefMap,
    subject: []const u8 = "",
    pos: usize = 0,
    delimiters: ?*Delim = null,
    brackets: ?*Bracket = null,
    /// GFM autolink literals (www. and http:// text becomes links).
    linkify: bool = true,

    fn newNode(self: *Parser, kind: Kind) !*Node {
        const n = try self.tmp.create(Node);
        n.* = .{ .kind = kind };
        return n;
    }

    fn textNode(self: *Parser, s: []const u8) !*Node {
        const n = try self.newNode(.text);
        n.text = s;
        return n;
    }

    fn peek(self: *const Parser) ?u8 {
        return if (self.pos < self.subject.len) self.subject[self.pos] else null;
    }

    fn peekAt(self: *const Parser, i: usize) ?u8 {
        return if (i < self.subject.len) self.subject[i] else null;
    }

    pub fn parse(self: *Parser, content: []const u8) !*Node {
        self.subject = content;
        self.pos = 0;
        self.delimiters = null;
        self.brackets = null;
        const root = try self.newNode(.root);
        while (try self.parseInline(root)) {}
        self.processEmphasis(null);
        if (self.linkify) try self.linkifyTree(root);
        return root;
    }

    fn parseInline(self: *Parser, block: *Node) !bool {
        const c = self.peek() orelse return false;
        switch (c) {
            '\n' => try self.parseNewline(block),
            '\\' => try self.parseBackslash(block),
            '`' => try self.parseBackticks(block),
            '*', '_', '~' => if (!try self.handleDelim(c, block)) {
                // Not a delimiter run (three or more tildes): literal text.
                const start = self.pos;
                while (self.peek() == c) self.pos += 1;
                block.append(try self.textNode(self.subject[start..self.pos]));
            },
            '[' => try self.parseOpenBracket(block),
            '!' => try self.parseBang(block),
            ']' => try self.parseCloseBracket(block),
            '<' => if (!(try self.parseAutolink(block) or try self.parseHtmlTag(block))) {
                self.pos += 1;
                block.append(try self.textNode("<"));
            },
            '&' => try self.parseEntity(block),
            else => try self.parseString(block),
        }
        return true;
    }

    fn parseString(self: *Parser, block: *Node) !void {
        const start = self.pos;
        self.pos += 1;
        while (self.pos < self.subject.len) : (self.pos += 1) {
            switch (self.subject[self.pos]) {
                '\n', '\\', '`', '*', '_', '~', '[', ']', '<', '&', '!' => break,
                else => {},
            }
        }
        block.append(try self.textNode(self.subject[start..self.pos]));
    }

    fn parseNewline(self: *Parser, block: *Node) !void {
        self.pos += 1;
        // A hard break if the previous text ended with two or more spaces.
        var hard = false;
        if (block.last) |last| if (last.kind == .text) {
            const t = last.text;
            var end = t.len;
            while (end > 0 and t[end - 1] == ' ') end -= 1;
            hard = t.len - end >= 2;
            last.text = t[0..end];
        };
        block.append(try self.newNode(if (hard) .linebreak else .softbreak));
        while (self.peek()) |c| {
            if (!isSpaceOrTab(c)) break;
            self.pos += 1;
        }
    }

    fn parseBackslash(self: *Parser, block: *Node) !void {
        self.pos += 1;
        if (self.peek()) |c| {
            if (c == '\n') {
                self.pos += 1;
                block.append(try self.newNode(.linebreak));
                while (self.peek()) |s| {
                    if (!isSpaceOrTab(s)) break;
                    self.pos += 1;
                }
                return;
            }
            if (isAsciiPunct(c)) {
                block.append(try self.textNode(self.subject[self.pos .. self.pos + 1]));
                self.pos += 1;
                return;
            }
        }
        block.append(try self.textNode("\\"));
    }

    fn parseBackticks(self: *Parser, block: *Node) !void {
        const start = self.pos;
        while (self.peek() == '`') self.pos += 1;
        const n = self.pos - start;
        const after = self.pos;
        var i = after;
        while (i < self.subject.len) {
            if (self.subject[i] != '`') {
                i += 1;
                continue;
            }
            const run_start = i;
            while (i < self.subject.len and self.subject[i] == '`') i += 1;
            if (i - run_start == n) {
                var content = self.subject[after..run_start];
                // Line endings become spaces; one space is stripped from each
                // end when both are spaces and the content is not all spaces.
                const buf = try self.tmp.dupe(u8, content);
                for (buf) |*ch| if (ch.* == '\n') {
                    ch.* = ' ';
                };
                content = buf;
                if (content.len >= 2 and content[0] == ' ' and content[content.len - 1] == ' ' and
                    std.mem.indexOfNone(u8, content, " ") != null) content = content[1 .. content.len - 1];
                const node = try self.newNode(.code);
                node.text = content;
                block.append(node);
                self.pos = i;
                return;
            }
        }
        block.append(try self.textNode(self.subject[start..after]));
        self.pos = after;
    }

    fn parseEntity(self: *Parser, block: *Node) !void {
        var buf: [8]u8 = undefined;
        if (decodeEntity(&buf, self.subject[self.pos..])) |e| {
            self.pos += e.len;
            block.append(try self.textNode(try self.tmp.dupe(u8, e.text)));
        } else {
            self.pos += 1;
            block.append(try self.textNode("&"));
        }
    }

    // ---- Autolinks and raw HTML ---------------------------------------------------

    fn parseAutolink(self: *Parser, block: *Node) !bool {
        const s = self.subject;
        const start = self.pos + 1;
        const close = std.mem.indexOfScalarPos(u8, s, start, '>') orelse return false;
        const body = s[start..close];
        if (body.len == 0) return false;
        for (body) |c| if (c <= ' ' or c == '<') return false;
        var url: ?[]const u8 = null;
        if (std.ascii.isAlphabetic(body[0])) {
            var i: usize = 1;
            while (i < body.len and (isAsciiAlnum(body[i]) or body[i] == '+' or body[i] == '.' or body[i] == '-')) i += 1;
            if (i >= 2 and i <= 32 and i < body.len and body[i] == ':') url = body;
        }
        if (url == null and isEmail(body)) url = try std.mem.concat(self.tmp, u8, &.{ "mailto:", body });
        const dest = url orelse return false;
        const link = try self.newNode(.link);
        link.url = dest;
        link.append(try self.textNode(body));
        block.append(link);
        self.pos = close + 1;
        return true;
    }

    fn isEmail(s: []const u8) bool {
        const at = std.mem.indexOfScalar(u8, s, '@') orelse return false;
        if (at == 0 or at + 1 >= s.len) return false;
        for (s[0..at]) |c| if (!(isAsciiAlnum(c) or std.mem.indexOfScalar(u8, ".!#$%&'*+/=?^_`{|}~-", c) != null)) return false;
        for (s[at + 1 ..]) |c| if (!(isAsciiAlnum(c) or c == '-' or c == '.')) return false;
        const dom = s[at + 1 ..];
        return dom[0] != '.' and dom[0] != '-' and dom[dom.len - 1] != '.';
    }

    /// Raw inline HTML: `<br>` becomes a hard break, any other well-formed tag
    /// is dropped (the preview has no HTML engine).
    fn parseHtmlTag(self: *Parser, block: *Node) !bool {
        const end = scanTag(self.subject, self.pos) orelse return false;
        const tag = self.subject[self.pos..end];
        self.pos = end;
        if (tag.len >= 3 and tag[1] != '/' and std.ascii.eqlIgnoreCase(tag[1..@min(tag.len, 3)], "br")) {
            const rest = std.mem.trim(u8, tag[3 .. tag.len - 1], " \t\n/");
            if (rest.len == 0) block.append(try self.newNode(.linebreak));
        }
        return true;
    }

    /// The end (past '>') of the HTML tag, comment, PI, declaration or CDATA
    /// starting at `s[pos]`.
    pub fn scanTag(s: []const u8, pos: usize) ?usize {
        if (pos + 2 >= s.len or s[pos] != '<') return null;
        const c = s[pos + 1];
        if (c == '!') {
            if (std.mem.startsWith(u8, s[pos..], "<!--")) {
                const e = std.mem.indexOfPos(u8, s, pos + 4, "-->") orelse return null;
                if (std.mem.startsWith(u8, s[pos..], "<!-->") or std.mem.startsWith(u8, s[pos..], "<!--->")) return pos + (if (s[pos + 4] == '>') @as(usize, 5) else 6);
                return e + 3;
            }
            if (std.mem.startsWith(u8, s[pos..], "<![CDATA[")) {
                const e = std.mem.indexOfPos(u8, s, pos + 9, "]]>") orelse return null;
                return e + 3;
            }
            if (std.ascii.isAlphabetic(s[pos + 2])) {
                const e = std.mem.indexOfScalarPos(u8, s, pos + 2, '>') orelse return null;
                return e + 1;
            }
            return null;
        }
        if (c == '?') {
            const e = std.mem.indexOfPos(u8, s, pos + 2, "?>") orelse return null;
            return e + 2;
        }
        var i = pos + 1;
        const closing = c == '/';
        if (closing) i += 1;
        if (i >= s.len or !std.ascii.isAlphabetic(s[i])) return null;
        while (i < s.len and (isAsciiAlnum(s[i]) or s[i] == '-')) i += 1;
        if (closing) {
            while (i < s.len and std.ascii.isWhitespace(s[i])) i += 1;
            return if (i < s.len and s[i] == '>') i + 1 else null;
        }
        // Attributes.
        while (true) {
            const ws_start = i;
            while (i < s.len and std.ascii.isWhitespace(s[i])) i += 1;
            if (i >= s.len) return null;
            if (s[i] == '>') return i + 1;
            if (s[i] == '/') return if (i + 1 < s.len and s[i + 1] == '>') i + 2 else null;
            if (i == ws_start) return null;
            if (!(std.ascii.isAlphabetic(s[i]) or s[i] == '_' or s[i] == ':')) return null;
            while (i < s.len and (isAsciiAlnum(s[i]) or std.mem.indexOfScalar(u8, "_.:-", s[i]) != null)) i += 1;
            var j = i;
            while (j < s.len and std.ascii.isWhitespace(s[j])) j += 1;
            if (j < s.len and s[j] == '=') {
                j += 1;
                while (j < s.len and std.ascii.isWhitespace(s[j])) j += 1;
                if (j >= s.len) return null;
                if (s[j] == '"' or s[j] == '\'') {
                    const q = s[j];
                    const e = std.mem.indexOfScalarPos(u8, s, j + 1, q) orelse return null;
                    i = e + 1;
                } else {
                    const vs = j;
                    while (j < s.len and std.mem.indexOfScalar(u8, " \t\n\"'=<>`", s[j]) == null) j += 1;
                    if (j == vs) return null;
                    i = j;
                }
            }
        }
    }

    // ---- Emphasis ---------------------------------------------------------------------

    fn charBefore(s: []const u8, pos: usize) u21 {
        if (pos == 0) return '\n';
        var i = pos - 1;
        while (i > 0 and (s[i] & 0xC0) == 0x80) i -= 1;
        return std.unicode.utf8Decode(s[i..pos]) catch 0xFFFD;
    }

    fn charAfter(s: []const u8, pos: usize) u21 {
        if (pos >= s.len) return '\n';
        const n = std.unicode.utf8ByteSequenceLength(s[pos]) catch return 0xFFFD;
        if (pos + n > s.len) return 0xFFFD;
        return std.unicode.utf8Decode(s[pos .. pos + n]) catch 0xFFFD;
    }

    fn handleDelim(self: *Parser, cc: u8, block: *Node) !bool {
        const start = self.pos;
        var end = start;
        while (end < self.subject.len and self.subject[end] == cc) end += 1;
        const num = end - start;
        if (cc == '~' and num > 2) return false;
        const before = charBefore(self.subject, start);
        const after = charAfter(self.subject, end);
        const after_ws = isUnicodeWhitespace(after);
        const after_p = isUnicodePunct(after);
        const before_ws = isUnicodeWhitespace(before);
        const before_p = isUnicodePunct(before);
        const left = !after_ws and (!after_p or before_ws or before_p);
        const right = !before_ws and (!before_p or after_ws or after_p);
        var can_open: bool = undefined;
        var can_close: bool = undefined;
        if (cc == '_') {
            can_open = left and (!right or before_p);
            can_close = right and (!left or after_p);
        } else {
            can_open = left;
            can_close = right;
        }
        self.pos = end;
        const node = try self.textNode(self.subject[start..end]);
        block.append(node);
        if (can_open or can_close) {
            const d = try self.tmp.create(Delim);
            d.* = .{ .cc = cc, .num = num, .orig = num, .node = node, .prev = self.delimiters, .can_open = can_open, .can_close = can_close };
            if (self.delimiters) |p| p.next = d;
            self.delimiters = d;
        }
        return true;
    }

    fn removeDelimiter(self: *Parser, d: *Delim) void {
        if (d.prev) |p| p.next = d.next;
        if (d.next) |n| n.prev = d.prev else self.delimiters = d.prev;
    }

    fn removeDelimitersBetween(self: *Parser, bottom: *Delim, top: *Delim) void {
        if (bottom.next != top) {
            bottom.next = top;
            top.prev = bottom;
        }
        _ = self;
    }

    fn processEmphasis(self: *Parser, stack_bottom: ?*Delim) void {
        var openers_bottom: [18]?*Delim = @splat(stack_bottom);
        var closer = self.delimiters;
        while (closer != null and closer.?.prev != stack_bottom) closer = closer.?.prev;
        while (closer) |cl| {
            if (!cl.can_close) {
                closer = cl.next;
                continue;
            }
            const cc_idx: usize = switch (cl.cc) {
                '*' => 0,
                '_' => 1,
                else => 2,
            };
            const bottom_idx = cc_idx * 6 + (if (cl.can_open) @as(usize, 3) else 0) + cl.orig % 3;
            var opener = cl.prev;
            var found = false;
            while (opener) |op| {
                if (op == stack_bottom or op == openers_bottom[bottom_idx]) break;
                const odd_match = (cl.can_open or op.can_close) and cl.orig % 3 != 0 and (op.orig + cl.orig) % 3 == 0;
                if (op.cc == cl.cc and op.can_open and !odd_match and (cl.cc != '~' or op.num == cl.num)) {
                    found = true;
                    break;
                }
                opener = op.prev;
            }
            const old_closer = cl;
            if (found) {
                const op = opener.?;
                const use: usize = if (cl.num >= 2 and op.num >= 2) 2 else 1;
                const op_node = op.node;
                const cl_node = cl.node;
                op.num -= use;
                cl.num -= use;
                op_node.text = op_node.text[0 .. op_node.text.len - use];
                cl_node.text = cl_node.text[0 .. cl_node.text.len - use];
                const kind: Kind = if (cl.cc == '~') .strike else if (use == 1) .em else .strong;
                const emph = self.newNode(kind) catch return;
                var tmp = op_node.next;
                while (tmp) |t| {
                    if (t == cl_node) break;
                    const nx = t.next;
                    t.unlink();
                    emph.append(t);
                    tmp = nx;
                }
                op_node.insertAfter(emph);
                self.removeDelimitersBetween(op, cl);
                if (op.num == 0) {
                    op_node.unlink();
                    self.removeDelimiter(op);
                }
                if (cl.num == 0) {
                    cl_node.unlink();
                    closer = cl.next;
                    self.removeDelimiter(cl);
                }
            } else {
                closer = cl.next;
                openers_bottom[bottom_idx] = old_closer.prev;
                if (!old_closer.can_open) self.removeDelimiter(old_closer);
            }
        }
        while (self.delimiters != null and self.delimiters != stack_bottom) self.removeDelimiter(self.delimiters.?);
    }

    // ---- Links and images ------------------------------------------------------------------

    fn addBracket(self: *Parser, node: *Node, index: usize, image: bool) !void {
        if (self.brackets) |b| b.bracket_after = true;
        const b = try self.tmp.create(Bracket);
        b.* = .{ .node = node, .prev = self.brackets, .prev_delim = self.delimiters, .index = index, .image = image };
        self.brackets = b;
    }

    fn removeBracket(self: *Parser) void {
        if (self.brackets) |b| self.brackets = b.prev;
    }

    fn parseOpenBracket(self: *Parser, block: *Node) !void {
        const start = self.pos;
        self.pos += 1;
        const node = try self.textNode("[");
        block.append(node);
        try self.addBracket(node, start, false);
    }

    fn parseBang(self: *Parser, block: *Node) !void {
        const start = self.pos;
        self.pos += 1;
        if (self.peek() == '[') {
            self.pos += 1;
            const node = try self.textNode("![");
            block.append(node);
            try self.addBracket(node, start + 1, true);
        } else {
            block.append(try self.textNode("!"));
        }
    }

    fn spnl(self: *Parser) bool {
        while (self.peek()) |c| {
            if (!isSpaceOrTab(c)) break;
            self.pos += 1;
        }
        if (self.peek() == '\n') {
            self.pos += 1;
            while (self.peek()) |c| {
                if (!isSpaceOrTab(c)) break;
                self.pos += 1;
            }
        }
        return true;
    }

    fn parseLinkDestination(self: *Parser) !?[]const u8 {
        const s = self.subject;
        if (self.peek() == '<') {
            var i = self.pos + 1;
            while (i < s.len) : (i += 1) {
                switch (s[i]) {
                    '\\' => i += 1,
                    '\n', '<' => return null,
                    '>' => {
                        const dest = try unescape(self.tmp, s[self.pos + 1 .. i]);
                        self.pos = i + 1;
                        return dest;
                    },
                    else => {},
                }
            }
            return null;
        }
        var i = self.pos;
        var depth: usize = 0;
        while (i < s.len) : (i += 1) {
            const c = s[i];
            if (c == '\\' and i + 1 < s.len and isAsciiPunct(s[i + 1])) {
                i += 1;
            } else if (c == '(') {
                depth += 1;
                if (depth > 32) return null;
            } else if (c == ')') {
                if (depth == 0) break;
                depth -= 1;
            } else if (c <= ' ' or c == 0x7f) {
                break;
            }
        }
        if (i == self.pos and self.peekAt(i) != ')') return null;
        if (depth != 0) return null;
        const dest = try unescape(self.tmp, s[self.pos..i]);
        self.pos = i;
        return dest;
    }

    fn parseLinkTitle(self: *Parser) !?[]const u8 {
        const s = self.subject;
        const open = self.peek() orelse return null;
        const close: u8 = switch (open) {
            '"' => '"',
            '\'' => '\'',
            '(' => ')',
            else => return null,
        };
        var i = self.pos + 1;
        while (i < s.len) : (i += 1) {
            if (s[i] == '\\') {
                i += 1;
            } else if (s[i] == close) {
                const title = try unescape(self.tmp, s[self.pos + 1 .. i]);
                self.pos = i + 1;
                return title;
            } else if (open == '(' and s[i] == '(') {
                return null;
            }
        }
        return null;
    }

    /// Length of the `[label]` at the position (0 if there is none).
    fn parseLinkLabel(self: *Parser) usize {
        const s = self.subject;
        if (self.peek() != '[') return 0;
        var i = self.pos + 1;
        while (i < s.len and i - self.pos <= 1000) : (i += 1) {
            switch (s[i]) {
                '\\' => i += 1,
                '[' => return 0,
                ']' => {
                    const n = i + 1 - self.pos;
                    return if (n > 1000) 0 else n;
                },
                else => {},
            }
        }
        return 0;
    }

    fn parseCloseBracket(self: *Parser, block: *Node) !void {
        self.pos += 1;
        const startpos = self.pos;
        const opener = self.brackets orelse {
            block.append(try self.textNode("]"));
            return;
        };
        if (!opener.active) {
            block.append(try self.textNode("]"));
            self.removeBracket();
            return;
        }
        const is_image = opener.image;
        const savepos = self.pos;
        var matched = false;
        var dest: []const u8 = "";
        var title: []const u8 = "";
        if (self.peek() == '(') {
            self.pos += 1;
            _ = self.spnl();
            if (try self.parseLinkDestination()) |d| {
                dest = d;
                const before_title = self.pos;
                _ = self.spnl();
                if (self.pos != before_title) {
                    if (try self.parseLinkTitle()) |t| title = t;
                }
                _ = self.spnl();
                if (self.peek() == ')') {
                    self.pos += 1;
                    matched = true;
                }
            }
            if (!matched) self.pos = savepos;
        }
        if (!matched) {
            const before_label = self.pos;
            const n = self.parseLinkLabel();
            var reflabel: ?[]const u8 = null;
            if (n > 2) {
                reflabel = self.subject[before_label + 1 .. before_label + n - 1];
            } else if (!opener.bracket_after) {
                // `[text][]` and `[text]`: the text is the label.
                reflabel = self.subject[opener.index + 1 .. startpos - 1];
            }
            if (reflabel) |label| {
                const key = try normalizeLabel(self.tmp, label);
                if (key.len > 0) if (self.refs.get(key)) |r| {
                    dest = r.url;
                    title = r.title;
                    matched = true;
                    self.pos = before_label + n;
                };
            }
            if (!matched) self.pos = savepos;
        }
        if (matched) {
            const node = try self.newNode(if (is_image) .image else .link);
            node.url = dest;
            node.title = title;
            var tmp = opener.node.next;
            while (tmp) |t| {
                const nx = t.next;
                t.unlink();
                node.append(t);
                tmp = nx;
            }
            block.append(node);
            self.processEmphasis(opener.prev_delim);
            self.removeBracket();
            opener.node.unlink();
            if (!is_image) {
                var o = self.brackets;
                while (o) |b| : (o = b.prev) if (!b.image) {
                    b.active = false;
                };
            }
        } else {
            self.removeBracket();
            self.pos = startpos;
            block.append(try self.textNode("]"));
        }
    }

    // ---- Reference definitions -----------------------------------------------------------

    /// Parses link reference definitions at the start of `content`; returns how
    /// many bytes they take.
    pub fn parseReferences(self: *Parser, content: []const u8) !usize {
        self.subject = content;
        self.pos = 0;
        var total: usize = 0;
        while (total < content.len and content[total] == '[') {
            self.pos = total;
            const n = (try self.parseReference()) orelse break;
            total = n;
        }
        return total;
    }

    fn parseReference(self: *Parser) !?usize {
        const s = self.subject;
        const start = self.pos;
        const n = self.parseLinkLabel();
        if (n == 0) return null;
        const raw = s[start + 1 .. start + n - 1];
        self.pos += n;
        if (self.peek() != ':') return null;
        self.pos += 1;
        _ = self.spnl();
        const dest = (try self.parseLinkDestination()) orelse return null;
        if (dest.len == 0 and !(self.pos > 0 and s[self.pos - 1] == '>')) return null;
        const before_title = self.pos;
        _ = self.spnl();
        var title: ?[]const u8 = null;
        if (self.pos != before_title) title = try self.parseLinkTitle();
        if (title == null) self.pos = before_title;
        var at_end = self.restIsBlank();
        if (!at_end) {
            if (title == null) return null;
            title = null;
            self.pos = before_title;
            at_end = self.restIsBlank();
        }
        if (!at_end) return null;
        const key = try normalizeLabel(self.tmp, raw);
        if (key.len == 0) return null;
        // The paragraph's text is shifted once the definitions are cut off it.
        if (!self.refs.contains(key)) try self.refs.put(self.tmp, key, .{ .url = try self.tmp.dupe(u8, dest), .title = try self.tmp.dupe(u8, title orelse "") });
        return self.pos;
    }

    /// Skips spaces and one newline; true if that reaches the end of the line.
    fn restIsBlank(self: *Parser) bool {
        var i = self.pos;
        while (i < self.subject.len and isSpaceOrTab(self.subject[i])) i += 1;
        if (i == self.subject.len) {
            self.pos = i;
            return true;
        }
        if (self.subject[i] == '\n') {
            self.pos = i + 1;
            return true;
        }
        return false;
    }

    // ---- GFM autolink literals ----------------------------------------------------------------

    fn linkifyTree(self: *Parser, parent: *Node) !void {
        var cur = parent.first;
        while (cur) |n| {
            const next = n.next;
            switch (n.kind) {
                .text => try self.linkifyText(n),
                .em, .strong, .strike => try self.linkifyTree(n),
                else => {},
            }
            cur = next;
        }
    }

    fn urlStart(s: []const u8, i: usize) ?usize {
        const rest = s[i..];
        const boundary_ok = i == 0 or !(isAsciiAlnum(s[i - 1]) or s[i - 1] == '/' or s[i - 1] == '.' or s[i - 1] == '@');
        if (!boundary_ok) return null;
        if (std.ascii.startsWithIgnoreCase(rest, "http://") and rest.len > 7) return 7;
        if (std.ascii.startsWithIgnoreCase(rest, "https://") and rest.len > 8) return 8;
        if (std.ascii.startsWithIgnoreCase(rest, "www.") and rest.len > 4) return 4;
        return null;
    }

    fn linkifyText(self: *Parser, node: *Node) !void {
        const s = node.text;
        var i: usize = 0;
        var found: ?struct { start: usize, end: usize } = null;
        while (i < s.len) : (i += 1) {
            const prefix = urlStart(s, i) orelse continue;
            var end = i + prefix;
            while (end < s.len and !std.ascii.isWhitespace(s[end]) and s[end] != '<') end += 1;
            // Trailing punctuation and unbalanced parentheses are not part of it.
            while (end > i + prefix) {
                const c = s[end - 1];
                if (std.mem.indexOfScalar(u8, "?!.,:*_~'\";", c) != null) {
                    end -= 1;
                } else if (c == ')') {
                    const opens = std.mem.count(u8, s[i..end], "(");
                    const closes = std.mem.count(u8, s[i..end], ")");
                    if (closes > opens) end -= 1 else break;
                } else break;
            }
            // A host with at least one dot.
            const host = s[i + prefix .. end];
            const host_end = std.mem.indexOfAny(u8, host, "/?#") orelse host.len;
            if (std.mem.indexOfScalar(u8, host[0..host_end], '.') == null and prefix != 4) continue;
            if (end > i + prefix) {
                found = .{ .start = i, .end = end };
                break;
            }
        }
        const f = found orelse return;
        const before = s[0..f.start];
        const url_text = s[f.start..f.end];
        const after = s[f.end..];
        const link = try self.newNode(.link);
        link.url = if (std.ascii.startsWithIgnoreCase(url_text, "www.")) try std.mem.concat(self.tmp, u8, &.{ "http://", url_text }) else url_text;
        link.append(try self.textNode(url_text));
        var anchor = node;
        if (before.len > 0) {
            node.text = before;
            node.insertAfter(link);
            anchor = link;
        } else {
            // Replace the node itself.
            node.insertAfter(link);
            node.unlink();
            anchor = link;
        }
        if (after.len > 0) {
            const rest = try self.textNode(after);
            anchor.insertAfter(rest);
            try self.linkifyText(rest);
        }
    }
};

// ---- Converting to the output tree --------------------------------------------------------

/// Copies the children of `parent` into `out`, merging adjacent text.
pub fn convert(out: Allocator, parent: *Node) Allocator.Error![]const Inline {
    var list: std.ArrayList(Inline) = .empty;
    var text_buf: std.ArrayList(u8) = .empty;
    defer text_buf.deinit(out);
    var cur = parent.first;
    while (cur) |n| : (cur = n.next) {
        if (n.kind == .text) {
            try text_buf.appendSlice(out, n.text);
            continue;
        }
        try flushText(out, &list, &text_buf);
        switch (n.kind) {
            .code => try list.append(out, .{ .code = try out.dupe(u8, n.text) }),
            .softbreak => try list.append(out, .soft_break),
            .linebreak => try list.append(out, .hard_break),
            .em => try list.append(out, .{ .em = try convert(out, n) }),
            .strong => try list.append(out, .{ .strong = try convert(out, n) }),
            .strike => try list.append(out, .{ .strike = try convert(out, n) }),
            .link => try list.append(out, .{ .link = .{ .url = try out.dupe(u8, n.url), .title = try out.dupe(u8, n.title), .children = try convert(out, n) } }),
            .image => try list.append(out, .{ .image = .{ .url = try out.dupe(u8, n.url), .title = try out.dupe(u8, n.title), .alt = try convert(out, n) } }),
            .text, .root => unreachable,
        }
    }
    try flushText(out, &list, &text_buf);
    return list.items;
}

fn flushText(out: Allocator, list: *std.ArrayList(Inline), buf: *std.ArrayList(u8)) !void {
    if (buf.items.len == 0) return;
    try list.append(out, .{ .text = try out.dupe(u8, buf.items) });
    buf.clearRetainingCapacity();
}

/// Parses `content` into inlines (copied into `out`); `tmp` holds scratch.
pub fn parseInlines(out: Allocator, tmp: Allocator, refs: *RefMap, content: []const u8, linkify: bool) ![]const Inline {
    var p: Parser = .{ .tmp = tmp, .refs = refs, .linkify = linkify };
    const root = try p.parse(content);
    return convert(out, root);
}
