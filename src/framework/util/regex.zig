//! A small regular expression engine: parser → instructions → backtracking
//! VM with a visited bitset (RE2's "BitState"), so matching is polynomial and
//! empty loops can't spin. Works on UTF-8 codepoints, leftmost-first.
//!
//! Supported: literals, `.`, `[...]` classes (ranges, negation, escapes),
//! `\d \w \s \D \W \S \b \B \n \t`, anchors `^ $` (line-based), groups with
//! captures, `(?:...)`, alternation, `* + ? {n} {n,} {n,m}` and lazy variants.

const std = @import("std");
const repeat = @import("repeat.zig").repeat;
const Allocator = std.mem.Allocator;

pub const Options = struct {
    case_insensitive: bool = false,
};

const Range = struct { lo: u21, hi: u21 };

const Class = struct {
    ranges: []Range,
    negated: bool,
    /// Bit b set when ASCII char b matches, under the Regex's case folding
    /// (filled in by `compile`): one test where the VM sees ASCII.
    ascii: u128 = 0,

    fn matches(self: Class, cp: u21, ci: bool) bool {
        var hit = inRanges(self.ranges, cp);
        if (!hit and ci) {
            const other = swapCase(cp);
            if (other != cp) hit = inRanges(self.ranges, other);
        }
        return hit != self.negated;
    }
};

fn inRanges(ranges: []const Range, cp: u21) bool {
    for (ranges) |r| if (cp >= r.lo and cp <= r.hi) return true;
    return false;
}

fn swapCase(cp: u21) u21 {
    if (cp >= 'a' and cp <= 'z') return cp - 32;
    if (cp >= 'A' and cp <= 'Z') return cp + 32;
    return cp;
}

fn isWordChar(cp: u21) bool {
    return (cp >= 'a' and cp <= 'z') or (cp >= 'A' and cp <= 'Z') or (cp >= '0' and cp <= '9') or cp == '_' or cp >= 0x80;
}

const Inst = union(enum) {
    char: u21,
    any_but_newline,
    class: u32,
    split: struct { x: u32, y: u32 },
    jmp: u32,
    save: u32,
    bol,
    eol,
    word_boundary,
    not_word_boundary,
    match,
};

const Node = union(enum) {
    empty,
    literal: u21,
    any,
    class: u32,
    bol,
    eol,
    word_boundary,
    not_word_boundary,
    group: struct { node: *Node, capture: ?u32 },
    concat: []*Node,
    alt: []*Node,
    repeat: struct { node: *Node, min: u32, max: ?u32, greedy: bool },
};

pub const Match = struct {
    start: usize,
    end: usize,
    /// groups[i] = (start, end) of capture group i+1, if it participated.
    groups: [9]?[2]usize = @splat(null),
};

const Frame = struct { pc: u32, sp: usize, restore_slot: u32 = std.math.maxInt(u32), restore_val: usize = 0 };

pub const Regex = struct {
    arena: std.heap.ArenaAllocator,
    prog: []Inst,
    classes: []Class,
    num_groups: u32,
    opts: Options,
    first: ?*const FirstBytes,
    scratch: Scratch = .{},
    gpa: Allocator,

    /// What a match attempt writes as it goes. A Regex has its own, for
    /// `search` and `matchAt`; threads sharing one bring theirs to
    /// `searchIn` and `matchAtIn`. It grows with the Regex's allocator.
    pub const Scratch = struct {
        visited: std.ArrayList(u64) = .empty,
        stack: std.ArrayList(Frame) = .empty,

        pub fn deinit(self: *Scratch, gpa: Allocator) void {
            self.visited.deinit(gpa);
            self.stack.deinit(gpa);
        }

        /// Makes `visited` cover word `word`, zeroing a cache line at a
        /// time: an attempt usually needs just one, and a call to memset
        /// for it costs more than the attempt.
        inline fn growVisited(self: *Scratch, gpa: Allocator, word: usize) !void {
            const len = self.visited.items.len;
            if (word < len + 8 and len + 8 <= self.visited.capacity) {
                self.visited.items.len = len + 8;
                self.visited.items[len..][0..8].* = @splat(0);
            } else try self.growVisitedSlow(gpa, word);
        }

        fn growVisitedSlow(self: *Scratch, gpa: Allocator, word: usize) !void {
            const len = self.visited.items.len;
            try self.visited.ensureTotalCapacity(gpa, (word / 8 + 1) * 8);
            self.visited.items.len = (word / 8 + 1) * 8;
            @memset(self.visited.items[len..], 0);
        }

        inline fn push(self: *Scratch, gpa: Allocator, frame: Frame) !void {
            if (self.stack.items.len == self.stack.capacity) try self.stack.ensureUnusedCapacity(gpa, 1);
            self.stack.appendAssumeCapacity(frame);
        }
    };

    pub fn compile(gpa: Allocator, pattern: []const u8, opts: Options) !Regex {
        var arena = std.heap.ArenaAllocator.init(gpa);
        errdefer arena.deinit();
        var parser: Parser = .{ .alloc = arena.allocator(), .src = pattern };
        const root = try parser.parseAlt();
        if (parser.pos != pattern.len) return error.UnbalancedParenthesis;

        var gen: Codegen = .{ .alloc = arena.allocator() };
        try gen.emit(.{ .save = 0 });
        try gen.gen(root);
        try gen.emit(.{ .save = 1 });
        try gen.emit(.match);
        for (parser.classes.items) |*class| for (0..0x80) |b| {
            if (class.matches(@intCast(b), opts.case_insensitive)) class.ascii |= @as(u128, 1) << @intCast(b);
        };
        const first = try FirstBytes.init(arena.allocator(), gen.prog.items, parser.classes.items, opts.case_insensitive);
        return .{
            .arena = arena,
            .prog = gen.prog.items,
            .classes = parser.classes.items,
            .num_groups = parser.group_count,
            .opts = opts,
            .first = first,
            .gpa = gpa,
        };
    }

    pub fn deinit(self: *Regex) void {
        self.scratch.deinit(self.gpa);
        self.arena.deinit();
    }

    /// First match at or after `start` in `text`. `^`/`$` match at line boundaries.
    pub fn search(self: *Regex, text: []const u8, start: usize) ?Match {
        return self.searchIn(&self.scratch, text, start);
    }

    /// `search` with the caller's scratch: safe while other threads use the Regex.
    pub fn searchIn(self: *const Regex, scratch: *Scratch, text: []const u8, start: usize) ?Match {
        var pos = start;
        while (pos <= text.len) {
            if (self.first) |first| {
                // Skip the positions whose bytes can't start a match. None
                // can match at the end: that would be an empty match.
                pos = first.next(text, pos);
                if (pos >= text.len) break;
            }
            if (self.matchAtIn(scratch, text, pos)) |m| return m;
            if (pos == text.len) break;
            pos += std.unicode.utf8ByteSequenceLength(text[pos]) catch 1;
        }
        return null;
    }

    /// Last match starting before `end` (for find-previous).
    pub fn searchBackward(self: *Regex, text: []const u8, end: usize) ?Match {
        var last: ?Match = null;
        var pos: usize = 0;
        while (self.search(text, pos)) |m| {
            if (m.start >= end) break;
            last = m;
            pos = if (m.end > m.start) m.end else m.start + 1;
            if (pos > text.len) break;
        }
        return last;
    }

    /// Anchored match starting exactly at `pos`.
    pub fn matchAt(self: *Regex, text: []const u8, pos: usize) ?Match {
        return self.matchAtIn(&self.scratch, text, pos);
    }

    /// `matchAt` with the caller's scratch.
    pub fn matchAtIn(self: *const Regex, scratch: *Scratch, text: []const u8, pos: usize) ?Match {
        const ci = self.opts.case_insensitive;
        // States are numbered position-major ((sp - pos) * prog.len + pc), so
        // an attempt clears and grows only the bits up to the furthest
        // position it reached, not one row per instruction across the rest of
        // the text (quadratic on long lines).
        const visited = &scratch.visited;
        const stack = &scratch.stack;
        // Not clearRetainingCapacity: in safe builds that memsets what was
        // there, which costs more than a short attempt.
        visited.items.len = 0;
        stack.items.len = 0;
        var slots: [20]usize = @splat(std.math.maxInt(usize));

        // The first thread runs without a frame, past `save 0` (pc 0, which
        // nothing jumps back to).
        slots[0] = pos;
        var pc: u32 = 1;
        var sp = pos;
        while (true) {
            while (true) {
                const state = (sp - pos) * self.prog.len + pc;
                if (state / 64 >= visited.items.len) scratch.growVisited(self.gpa, state / 64) catch return null;
                const bit = @as(u64, 1) << @intCast(state % 64);
                if (visited.items[state / 64] & bit != 0) break;
                visited.items[state / 64] |= bit;

                switch (self.prog[pc]) {
                    .match => {
                        var m: Match = .{ .start = slots[0], .end = slots[1] };
                        var g: u32 = 0;
                        while (g < @min(self.num_groups, 9)) : (g += 1) {
                            const s = slots[2 * (g + 1)];
                            const e = slots[2 * (g + 1) + 1];
                            if (s != std.math.maxInt(usize) and e != std.math.maxInt(usize)) m.groups[g] = .{ s, e };
                        }
                        return m;
                    },
                    .char => |want| {
                        const cp, const n = decode(text, sp) orelse break;
                        if (cp != want and !(ci and swapCase(cp) == want)) break;
                        sp += n;
                        pc += 1;
                    },
                    .any_but_newline => {
                        const cp, const n = decode(text, sp) orelse break;
                        if (cp == '\n') break;
                        sp += n;
                        pc += 1;
                    },
                    .class => |idx| {
                        const cp, const n = decode(text, sp) orelse break;
                        const class = &self.classes[idx];
                        if (!(if (cp < 0x80) class.ascii >> @intCast(cp) & 1 != 0 else class.matches(cp, ci))) break;
                        sp += n;
                        pc += 1;
                    },
                    .split => |s| {
                        scratch.push(self.gpa, .{ .pc = s.y, .sp = sp }) catch return null;
                        pc = s.x;
                    },
                    .jmp => |target| pc = target,
                    .save => |slot| {
                        if (slot < slots.len) {
                            // A restore frame matters only if a thread below
                            // it can run after it pops, and none can once
                            // `.match` is reached.
                            if (stack.items.len > 0 and self.prog[pc + 1] != .match)
                                scratch.push(self.gpa, .{ .pc = 0, .sp = 0, .restore_slot = slot, .restore_val = slots[slot] }) catch return null;
                            slots[slot] = sp;
                        }
                        pc += 1;
                    },
                    .bol => {
                        if (sp != 0 and text[sp - 1] != '\n') break;
                        pc += 1;
                    },
                    .eol => {
                        if (sp != text.len and text[sp] != '\n') break;
                        pc += 1;
                    },
                    .word_boundary, .not_word_boundary => {
                        const before = sp > 0 and isWordChar(prevChar(text, sp));
                        const after = if (decode(text, sp)) |d| isWordChar(d[0]) else false;
                        const at_boundary = before != after;
                        if (at_boundary != (self.prog[pc] == .word_boundary)) break;
                        pc += 1;
                    },
                }
            }
            const frame = while (stack.pop()) |frame| {
                if (frame.restore_slot == std.math.maxInt(u32)) break frame;
                slots[frame.restore_slot] = frame.restore_val;
            } else return null;
            pc = frame.pc;
            sp = frame.sp;
        }
    }
};

/// Inline for the VM's sake: most text is ASCII.
inline fn decode(text: []const u8, sp: usize) ?struct { u21, usize } {
    if (sp >= text.len) return null;
    if (text[sp] < 0x80) return .{ text[sp], 1 };
    return decodeWide(text, sp);
}

fn decodeWide(text: []const u8, sp: usize) struct { u21, usize } {
    const n = std.unicode.utf8ByteSequenceLength(text[sp]) catch return .{ 0xFFFD, 1 };
    if (sp + n > text.len) return .{ 0xFFFD, 1 };
    const cp = std.unicode.utf8Decode(text[sp .. sp + n]) catch return .{ 0xFFFD, 1 };
    return .{ cp, n };
}

fn prevChar(text: []const u8, sp: usize) u21 {
    var i = sp - 1;
    while (i > 0 and text[i] & 0xC0 == 0x80) i -= 1;
    return (decode(text, i) orelse return 0)[0];
}

/// Byte sets for the start of a match, so `search` runs the VM only where one
/// could begin. `sets[k]` holds the bytes that can sit k bytes into a match:
/// the first bytes of what the consuming instructions reachable after k
/// others accept, walking from pc 0 past saves, jumps, splits and the
/// zero-width assertions. Offsets line up only while each char is one byte,
/// so the sets stop after an instruction that accepts non-ASCII chars.
const FirstBytes = struct {
    sets: [max_len][256]bool,
    /// How many sets apply; every match is at least this many bytes long.
    len: usize,
    /// The ASCII bytes of `sets[0]` when there are at most four (0x80 fills
    /// free slots): runs of other ASCII bytes are skipped a vector at a time.
    few: ?[4]u8,

    const max_len = 4;

    /// Null when the pattern can match without consuming, or when every byte
    /// could start a match.
    fn init(alloc: Allocator, prog: []const Inst, classes: []const Class, ci: bool) !?*FirstBytes {
        var sets: [max_len][256]bool = @splat(@splat(false));
        var len: usize = max_len;
        // States are (k, pc): at pc after k consuming instructions.
        const seen = try alloc.alloc(bool, max_len * prog.len);
        @memset(seen, false);
        const State = struct { k: usize, pc: u32 };
        var todo: std.ArrayList(State) = .empty;
        try todo.append(alloc, .{ .k = 0, .pc = 0 });
        while (todo.pop()) |state| {
            const k = state.k;
            var pc = state.pc;
            while (k < len and !seen[k * prog.len + pc]) {
                seen[k * prog.len + pc] = true;
                switch (prog[pc]) {
                    .save, .bol, .eol, .word_boundary, .not_word_boundary => pc += 1,
                    .jmp => |target| pc = target,
                    .split => |s| {
                        try todo.append(alloc, .{ .k = k, .pc = s.y });
                        pc = s.x;
                    },
                    .match => len = k,
                    .char, .any_but_newline, .class => {
                        if (addFirstBytes(&sets[k], prog[pc], classes, ci)) len = k + 1;
                        try todo.append(alloc, .{ .k = k + 1, .pc = pc + 1 });
                        break;
                    },
                }
            }
        }
        if (len == 0 or std.mem.indexOfScalar(bool, &sets[0], false) == null) return null;

        var few: [4]u8 = @splat(0x80);
        var n: usize = 0;
        for (sets[0][0..0x80], 0..) |in, b| if (in) {
            if (n < few.len) few[n] = @intCast(b);
            n += 1;
        };
        const self = try alloc.create(FirstBytes);
        self.* = .{ .sets = sets, .len = len, .few = if (n <= few.len) few else null };
        return self;
    }

    /// Adds the bytes that can begin a char `inst` accepts. Returns whether it
    /// accepts non-ASCII chars, which take more than one byte.
    fn addFirstBytes(set: *[256]bool, inst: Inst, classes: []const Class, ci: bool) bool {
        switch (inst) {
            .char => |want| {
                var utf8: [4]u8 = undefined;
                if (want < 0x80) {
                    set[want] = true;
                    if (ci) set[swapCase(want)] = true;
                    return false;
                } else if (want == 0xFFFD) {
                    // Invalid UTF-8 decodes as U+FFFD, whatever its first byte.
                    @memset(set[0x80..], true);
                } else if (std.unicode.utf8Encode(want, &utf8)) |_| {
                    set[utf8[0]] = true;
                } else |_| @memset(set[0x80..], true);
                return true;
            },
            .any_but_newline => {
                for (set, 0..) |*in, b| in.* = in.* or b != '\n';
                return true;
            },
            .class => |idx| {
                const class = classes[idx];
                for (set[0..0x80], 0..) |*in, b| in.* = in.* or class.matches(@intCast(b), ci);
                // Non-ASCII text decodes to codepoints >= 0x80.
                var wide = class.negated;
                for (class.ranges) |r| wide = wide or r.hi >= 0x80;
                if (wide) @memset(set[0x80..], true);
                return wide;
            },
            else => unreachable,
        }
    }

    /// The first position from `pos` on that `search`'s stepping reaches and
    /// where a match could start (the bytes there are in their sets), or one
    /// at or past the end. Other positions are passed with the step `search`
    /// would take.
    fn next(self: *const FirstBytes, text: []const u8, from: usize) usize {
        const lanes = comptime std.simd.suggestVectorLength(u8) orelse 16;
        const V = @Vector(lanes, u8);
        const Mask = @Int(.unsigned, lanes);
        var pos = from;
        while (pos < text.len) {
            if (self.few) |few| if (text.len >= lanes) {
                // The last block moves back to end at the end of the text;
                // its bytes before `pos` were passed already.
                const block = @min(pos, text.len - lanes);
                const v: V = text[block..][0..lanes].*;
                var stops: Mask = @bitCast(v >= @as(V, @splat(0x80)));
                for (few) |f| stops |= @as(Mask, @bitCast(v == @as(V, @splat(f))));
                stops &= ~@as(Mask, 0) << @intCast(pos - block);
                if (stops == 0) {
                    pos = block + lanes;
                    continue;
                }
                pos = block + @ctz(stops);
            };
            if (self.startsAt(text, pos)) return pos;
            pos += std.unicode.utf8ByteSequenceLength(text[pos]) catch 1;
        }
        return pos;
    }

    fn startsAt(self: *const FirstBytes, text: []const u8, pos: usize) bool {
        if (text.len - pos < self.len) return false;
        for (self.sets[0..self.len], text[pos..][0..self.len]) |*set, b| {
            if (!set[b]) return false;
        }
        return true;
    }
};

const Parser = struct {
    alloc: Allocator,
    src: []const u8,
    pos: usize = 0,
    group_count: u32 = 0,
    classes: std.ArrayList(Class) = .empty,

    fn peek(p: *Parser) ?u8 {
        return if (p.pos < p.src.len) p.src[p.pos] else null;
    }

    fn node(p: *Parser, n: Node) !*Node {
        const ptr = try p.alloc.create(Node);
        ptr.* = n;
        return ptr;
    }

    fn parseAlt(p: *Parser) anyerror!*Node {
        var branches: std.ArrayList(*Node) = .empty;
        try branches.append(p.alloc, try p.parseConcat());
        while (p.peek() == '|') {
            p.pos += 1;
            try branches.append(p.alloc, try p.parseConcat());
        }
        if (branches.items.len == 1) return branches.items[0];
        return p.node(.{ .alt = branches.items });
    }

    fn parseConcat(p: *Parser) !*Node {
        var items: std.ArrayList(*Node) = .empty;
        while (p.peek()) |ch| {
            if (ch == '|' or ch == ')') break;
            try items.append(p.alloc, try p.parseRepeat());
        }
        if (items.items.len == 0) return p.node(.empty);
        if (items.items.len == 1) return items.items[0];
        return p.node(.{ .concat = items.items });
    }

    fn parseRepeat(p: *Parser) !*Node {
        var atom = try p.parseAtom();
        while (p.peek()) |ch| {
            var min: u32 = 0;
            var max: ?u32 = null;
            switch (ch) {
                '*' => p.pos += 1,
                '+' => {
                    p.pos += 1;
                    min = 1;
                },
                '?' => {
                    p.pos += 1;
                    max = 1;
                },
                '{' => {
                    const save = p.pos;
                    p.pos += 1;
                    const lo = p.parseNumber() orelse {
                        p.pos = save;
                        return atom;
                    };
                    min = lo;
                    max = lo;
                    if (p.peek() == ',') {
                        p.pos += 1;
                        max = p.parseNumber();
                    }
                    if (p.peek() != '}') {
                        p.pos = save;
                        return atom;
                    }
                    p.pos += 1;
                },
                else => return atom,
            }
            var greedy = true;
            if (p.peek() == '?') {
                p.pos += 1;
                greedy = false;
            }
            atom = try p.node(.{ .repeat = .{ .node = atom, .min = min, .max = max, .greedy = greedy } });
        }
        return atom;
    }

    fn parseNumber(p: *Parser) ?u32 {
        const start = p.pos;
        while (p.peek()) |ch| {
            if (!std.ascii.isDigit(ch)) break;
            p.pos += 1;
        }
        if (p.pos == start) return null;
        return std.fmt.parseInt(u32, p.src[start..p.pos], 10) catch null;
    }

    fn nextCodepoint(p: *Parser) !u21 {
        const cp, const n = decode(p.src, p.pos) orelse return error.UnexpectedEnd;
        p.pos += n;
        return cp;
    }

    fn parseAtom(p: *Parser) !*Node {
        const ch = p.peek() orelse return error.UnexpectedEnd;
        switch (ch) {
            '(' => {
                p.pos += 1;
                var capture: ?u32 = null;
                if (std.mem.startsWith(u8, p.src[p.pos..], "?:")) {
                    p.pos += 2;
                } else {
                    p.group_count += 1;
                    capture = p.group_count;
                }
                const inner = try p.parseAlt();
                if (p.peek() != ')') return error.UnbalancedParenthesis;
                p.pos += 1;
                return p.node(.{ .group = .{ .node = inner, .capture = capture } });
            },
            '[' => return p.parseClass(),
            '.' => {
                p.pos += 1;
                return p.node(.any);
            },
            '^' => {
                p.pos += 1;
                return p.node(.bol);
            },
            '$' => {
                p.pos += 1;
                return p.node(.eol);
            },
            '\\' => {
                p.pos += 1;
                const esc = p.peek() orelse return error.UnexpectedEnd;
                switch (esc) {
                    'b' => {
                        p.pos += 1;
                        return p.node(.word_boundary);
                    },
                    'B' => {
                        p.pos += 1;
                        return p.node(.not_word_boundary);
                    },
                    'd', 'D', 'w', 'W', 's', 'S' => {
                        p.pos += 1;
                        var ranges: std.ArrayList(Range) = .empty;
                        try appendPerl(p.alloc, &ranges, std.ascii.toLower(esc));
                        try p.classes.append(p.alloc, .{ .ranges = ranges.items, .negated = std.ascii.isUpper(esc) });
                        return p.node(.{ .class = @intCast(p.classes.items.len - 1) });
                    },
                    else => return p.node(.{ .literal = try p.parseEscapeLiteral() }),
                }
            },
            else => return p.node(.{ .literal = try p.nextCodepoint() }),
        }
    }

    fn parseEscapeLiteral(p: *Parser) !u21 {
        const esc = try p.nextCodepoint();
        return switch (esc) {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            'f' => 0x0c,
            'v' => 0x0b,
            '0' => 0,
            else => esc,
        };
    }

    fn parseClass(p: *Parser) !*Node {
        p.pos += 1; // '['
        var negated = false;
        if (p.peek() == '^') {
            negated = true;
            p.pos += 1;
        }
        var ranges: std.ArrayList(Range) = .empty;
        var first = true;
        while (true) {
            const ch = p.peek() orelse return error.UnterminatedClass;
            if (ch == ']' and !first) {
                p.pos += 1;
                break;
            }
            first = false;
            var lo: u21 = undefined;
            if (ch == '\\') {
                p.pos += 1;
                const esc = p.peek() orelse return error.UnexpectedEnd;
                if (std.mem.indexOfScalar(u8, "dDwWsS", esc) != null) {
                    p.pos += 1;
                    if (std.ascii.isUpper(esc)) return error.UnsupportedNegatedClassEscape;
                    try appendPerl(p.alloc, &ranges, esc);
                    continue;
                }
                lo = try p.parseEscapeLiteral();
            } else {
                lo = try p.nextCodepoint();
            }
            var hi = lo;
            if (p.peek() == '-' and p.pos + 1 < p.src.len and p.src[p.pos + 1] != ']') {
                p.pos += 1;
                if (p.peek() == '\\') {
                    p.pos += 1;
                    hi = try p.parseEscapeLiteral();
                } else {
                    hi = try p.nextCodepoint();
                }
            }
            try ranges.append(p.alloc, .{ .lo = @min(lo, hi), .hi = @max(lo, hi) });
        }
        try p.classes.append(p.alloc, .{ .ranges = ranges.items, .negated = negated });
        return p.node(.{ .class = @intCast(p.classes.items.len - 1) });
    }
};

fn appendPerl(alloc: Allocator, ranges: *std.ArrayList(Range), kind: u8) !void {
    switch (std.ascii.toLower(kind)) {
        'd' => try ranges.append(alloc, .{ .lo = '0', .hi = '9' }),
        'w' => try ranges.appendSlice(alloc, &.{
            .{ .lo = 'a', .hi = 'z' }, .{ .lo = 'A', .hi = 'Z' }, .{ .lo = '0', .hi = '9' }, .{ .lo = '_', .hi = '_' },
        }),
        's' => try ranges.appendSlice(alloc, &.{ .{ .lo = ' ', .hi = ' ' }, .{ .lo = '\t', .hi = '\r' } }),
        else => unreachable,
    }
}

const Codegen = struct {
    alloc: Allocator,
    prog: std.ArrayList(Inst) = .empty,

    fn emit(g: *Codegen, inst: Inst) !void {
        try g.prog.append(g.alloc, inst);
    }

    fn here(g: *Codegen) u32 {
        return @intCast(g.prog.items.len);
    }

    fn gen(g: *Codegen, n: *const Node) anyerror!void {
        switch (n.*) {
            .empty => {},
            .literal => |cp| try g.emit(.{ .char = cp }),
            .any => try g.emit(.any_but_newline),
            .class => |idx| try g.emit(.{ .class = idx }),
            .bol => try g.emit(.bol),
            .eol => try g.emit(.eol),
            .word_boundary => try g.emit(.word_boundary),
            .not_word_boundary => try g.emit(.not_word_boundary),
            .group => |grp| {
                if (grp.capture) |cap| try g.emit(.{ .save = cap * 2 });
                try g.gen(grp.node);
                if (grp.capture) |cap| try g.emit(.{ .save = cap * 2 + 1 });
            },
            .concat => |items| for (items) |item| try g.gen(item),
            .alt => |branches| {
                var jumps: std.ArrayList(u32) = .empty;
                for (branches, 0..) |branch, i| {
                    if (i + 1 < branches.len) {
                        const split_pc = g.here();
                        try g.emit(.{ .split = .{ .x = split_pc + 1, .y = 0 } });
                        try g.gen(branch);
                        try jumps.append(g.alloc, g.here());
                        try g.emit(.{ .jmp = 0 });
                        g.prog.items[split_pc].split.y = g.here();
                    } else {
                        try g.gen(branch);
                    }
                }
                for (jumps.items) |j| g.prog.items[j] = .{ .jmp = g.here() };
            },
            .repeat => |r| {
                var i: u32 = 0;
                while (i < r.min) : (i += 1) try g.gen(r.node);
                if (r.max) |max| {
                    // (e(e(e)?)?)? for the optional tail.
                    var holes: std.ArrayList(u32) = .empty;
                    while (i < max) : (i += 1) {
                        try holes.append(g.alloc, g.here());
                        try g.emit(.{ .split = .{ .x = 0, .y = 0 } });
                        try g.gen(r.node);
                    }
                    const end = g.here();
                    for (holes.items) |h| g.prog.items[h] = g.split(h + 1, end, r.greedy);
                } else {
                    const loop = g.here();
                    try g.emit(.{ .split = .{ .x = 0, .y = 0 } });
                    try g.gen(r.node);
                    try g.emit(.{ .jmp = loop });
                    g.prog.items[loop] = g.split(loop + 1, g.here(), r.greedy);
                }
            },
        }
    }

    fn split(_: *Codegen, body: u32, out: u32, greedy: bool) Inst {
        return if (greedy) .{ .split = .{ .x = body, .y = out } } else .{ .split = .{ .x = out, .y = body } };
    }
};

/// Escapes regex metacharacters (for "find literal" via the regex engine).
pub fn escape(gpa: Allocator, text: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    for (text) |ch| {
        if (std.mem.indexOfScalar(u8, "\\.^$|?*+()[]{}", ch) != null) try out.append(gpa, '\\');
        try out.append(gpa, ch);
    }
    return out.toOwnedSlice(gpa);
}

fn expectMatch(pattern: []const u8, text: []const u8, expected: ?[]const u8) !void {
    var re = try Regex.compile(std.testing.allocator, pattern, .{});
    defer re.deinit();
    const m = re.search(text, 0);
    if (expected) |e| {
        try std.testing.expect(m != null);
        try std.testing.expectEqualStrings(e, text[m.?.start..m.?.end]);
    } else {
        try std.testing.expect(m == null);
    }
}

test "regex basics" {
    try expectMatch("abc", "xxabcxx", "abc");
    try expectMatch("a.c", "abc", "abc");
    try expectMatch("^[A-Z][A-Z\\d_]*$", "MAX_SIZE2", "MAX_SIZE2");
    try expectMatch("^[A-Z][A-Z\\d_]*$", "Max", null);
    try expectMatch("^(append|cap|close)$", "cap", "cap");
    try expectMatch("^(append|cap|close)$", "capx", null);
    try expectMatch("a+", "baaab", "aaa");
    try expectMatch("a+?", "baaab", "a");
    try expectMatch("colou?r", "color", "color");
    try expectMatch("\\bfoo\\b", "a foo b", "foo");
    try expectMatch("\\bfoo\\b", "afoob", null);
    try expectMatch("x{2,3}", "xxxx", "xxx");
    try expectMatch("(a*)*b", "aaab", "aaab");
    try expectMatch("[^a-c]+", "abcdef", "def");
    try expectMatch("é+", "café", "é");
    try expectMatch("(?:ab)+", "ababx", "abab");
    try expectMatch("", "abc", "");
}

test "regex captures and case" {
    var re = try Regex.compile(std.testing.allocator, "(\\w+)@(\\w+)", .{});
    defer re.deinit();
    const text = "mail joe@example now";
    const m = re.search(text, 0).?;
    try std.testing.expectEqualStrings("joe", text[m.groups[0].?[0]..m.groups[0].?[1]]);
    try std.testing.expectEqualStrings("example", text[m.groups[1].?[0]..m.groups[1].?[1]]);

    var ci = try Regex.compile(std.testing.allocator, "hello", .{ .case_insensitive = true });
    defer ci.deinit();
    try std.testing.expect(ci.search("Say HeLLo", 0) != null);
}

test "regex pathological" {
    var re = try Regex.compile(std.testing.allocator, "(a|aa)*c", .{});
    defer re.deinit();
    try std.testing.expect(re.search(repeat("a", 200), 0) == null);
}

test "threads share a regex, each with its own scratch" {
    var re = try Regex.compile(std.testing.allocator, "(\\w+)/(\\w+)", .{});
    defer re.deinit();
    const Worker = struct {
        fn run(r: *const Regex, ok: *bool) void {
            var scratch: Regex.Scratch = .{};
            defer scratch.deinit(std.testing.allocator);
            ok.* = true;
            for (0..2000) |_| {
                const m = r.searchIn(&scratch, "type text/javascript here", 0) orelse {
                    ok.* = false;
                    return;
                };
                ok.* = ok.* and m.start == 5 and m.groups[1].?[0] == 10;
            }
        }
    };
    var ok: [4]bool = undefined;
    var threads: [4]std.Thread = undefined;
    for (&threads, &ok) |*t, *o| t.* = try std.Thread.spawn(.{}, Worker.run, .{ &re, o });
    for (threads) |t| t.join();
    for (ok) |o| try std.testing.expect(o);
}

test "regex on a long line" {
    // Each attempt touches only the bits up to where it got, not the whole
    // rest of the line: this was quadratic (gigabytes of clearing) before.
    const text = try std.testing.allocator.alloc(u8, 200_000);
    defer std.testing.allocator.free(text);
    @memset(text, 'x');
    @memcpy(text[text.len - 7 ..], "needle!");
    var re = try Regex.compile(std.testing.allocator, "ne+dle|q", .{});
    defer re.deinit();
    const m = re.search(text, 0).?;
    try std.testing.expectEqual(text.len - 7, m.start);
    try std.testing.expectEqual(text.len - 1, m.end);
    try std.testing.expect(re.search(text[0 .. text.len - 7], 0) == null);
}

test "regex first bytes" {
    const gpa = std.testing.allocator;
    for ([_][]const u8{ "", "a*", "\\b", "(a|)", "^$", "x*\\b", "(?:)" }) |p| {
        var re = try Regex.compile(gpa, p, .{});
        defer re.deinit();
        try std.testing.expect(re.first == null);
    }
    var dot = try Regex.compile(gpa, "\\n|.", .{});
    defer dot.deinit();
    try std.testing.expect(dot.first == null);

    var zq = try Regex.compile(gpa, "zq", .{ .case_insensitive = true });
    defer zq.deinit();
    try std.testing.expectEqual([4]u8{ 'Z', 'z', 0x80, 0x80 }, zq.first.?.few.?);
    try std.testing.expectEqual(@as(usize, 2), zq.first.?.len);
    try std.testing.expect(zq.first.?.sets[1]['Q'] and zq.first.?.sets[1]['q'] and !zq.first.?.sets[1]['z']);
    var word = try Regex.compile(gpa, "^\\b(?:self|const)(\\.)", .{});
    defer word.deinit();
    try std.testing.expectEqual([4]u8{ 'c', 's', 0x80, 0x80 }, word.first.?.few.?);
    try std.testing.expectEqual(@as(usize, 4), word.first.?.len);
    try std.testing.expect(word.first.?.sets[3]['f'] and word.first.?.sets[3]['s'] and !word.first.?.sets[3]['.']);
    // A non-ASCII char ends the sets: what follows it is at another offset.
    var alt = try Regex.compile(gpa, "^(?:\\bm\\w*|éa)", .{});
    defer alt.deinit();
    try std.testing.expectEqual(@as(usize, 1), alt.first.?.len);
    try std.testing.expect(alt.first.?.sets[0][0xC3] and !alt.first.?.sets[0][0xC4]);
    var not_nl = try Regex.compile(gpa, "[^\\n]", .{});
    defer not_nl.deinit();
    try std.testing.expect(not_nl.first.?.few == null and !not_nl.first.?.sets[0]['\n']);
}

/// `search` as it was before the first-byte skip: the VM at every position.
fn searchEveryPosition(re: *Regex, text: []const u8, start: usize) ?Match {
    var pos = start;
    while (pos <= text.len) {
        if (re.matchAt(text, pos)) |m| return m;
        if (pos == text.len) break;
        pos += std.unicode.utf8ByteSequenceLength(text[pos]) catch 1;
    }
    return null;
}

test "regex search finds what the VM finds at every position" {
    const patterns = [_][]const u8{
        "zq",     "zq+",         "magna\\w*",   "a",       "Z",          "M+a?",
        "[a-c]x", "[aZ]q",       "[A-Z][a-z]+", "_[0-9]",  "\\.",        "\\(",
        "[^a-c]", "[^\\w]+",     "[^z]",        "[^\\n]",  ".",          ".q",
        "x.",     "^z",          "q$",          "^m\\w*$", "(^|_)z",     "\\bm",
        "\\Bq",   "\\bz\\w*\\b", "(zq|m)",      "z|q|_",   "(?:fo|ba)o", "(z)(q)?",
        "x*z",    "(x*)q",       "a{0}z",       "m{2,3}",  "q\\nz",      "\\n",
        "[\\n]",  "\\w",         "[\\w]+",      "\\d+",    "\\s+",       "\\S",
        "\\W",    "\\D",         "",            "a*",      "q?",         "(a|)",
        "(?:)",   "a{0}",        "^",           "$",       "^$",         "\\b",
        "\\B",    "(z|\\b)",     "[^\\s]*",     "magna",   "^ma(g)",     "\\bmA\\B",
        "zQ\\n",  "(?:zq)z",
    } ++ [_][]const u8{ "(?:zq|ma)\\w", "m[a-c]g|q\\.", "\\b(?:z|ma|_)+[^m]", "é", "é+", "ü|z", "日本?", "[é-ü]", "[à-ÿ]+", "\u{FFFD}", "\xFF", "[\u{FFFD}]", "(?:ma|é)g", "m(?:a|é)g" };
    // Invalid UTF-8 included: `search` steps over a lead byte's whole length
    // even when what follows isn't a continuation.
    const fixed = [_][]const u8{
        "",
        "zq ZQ zQ Zq zqq",
        "magna Magna MAGNA magnam mmmM",
        "foo bar\nbaz fo_z\n_zq\n",
        "x(a).q_9 \t\r\n9z",
        "café naïve 日本語 üZ é",
        "\xE2zq \xFFz \x80q \xC3zq zq",
        "a\xC3",
        "q\xF0",
        "\xE2",
        "\u{FFFD}\xFFz\xEF\xBF",
        "\xC0\xAFzq\xED\xA0\x80q",
    };
    const pieces = [_][]const u8{ "a", "b", "c", "m", "M", "q", "Q", "z", "Z", "x", "_", "0", "9", " ", "\n", ".", "(", "é", "ü", "日", "\u{FFFD}", "\xFF", "\x80", "\xE2", "\xC3", "magna", "zq" };

    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();
    var prng = std.Random.DefaultPrng.init(0x5eed);
    const rand = prng.random();
    // Short texts are searched from every start; long ones (several vector
    // blocks, with runs of one piece) from each match's end.
    var short: std.ArrayList([]const u8) = .empty;
    var long: std.ArrayList([]const u8) = .empty;
    try short.appendSlice(alloc, &fixed);
    for (0..40) |i| {
        const len = if (i < 24) rand.uintLessThan(usize, 40) else 100 + rand.uintLessThan(usize, 300);
        var text: std.ArrayList(u8) = .empty;
        while (text.items.len < len) {
            const piece = pieces[rand.uintLessThan(usize, pieces.len)];
            const times = if (rand.uintLessThan(u8, 5) == 0) 1 + rand.uintLessThan(usize, 40) else 1;
            for (0..times) |_| try text.appendSlice(alloc, piece);
        }
        if (i < 24) try short.append(alloc, text.items) else try long.append(alloc, text.items);
    }

    for (patterns) |pattern| for ([_]bool{ false, true }) |ci| {
        var re = try Regex.compile(std.testing.allocator, pattern, .{ .case_insensitive = ci });
        defer re.deinit();
        for (short.items) |text| for (0..text.len + 2) |start| {
            errdefer std.debug.print("pattern \"{s}\" ci={} start={d} text {any}\n", .{ pattern, ci, start, text });
            try std.testing.expectEqualDeep(searchEveryPosition(&re, text, start), re.search(text, start));
        };
        for (long.items) |text| {
            var start: usize = 0;
            while (start <= text.len) {
                errdefer std.debug.print("pattern \"{s}\" ci={} start={d} text {any}\n", .{ pattern, ci, start, text });
                const want = searchEveryPosition(&re, text, start);
                try std.testing.expectEqualDeep(want, re.search(text, start));
                const m = want orelse break;
                start = if (m.end > m.start) m.end else m.end + 1;
            }
        }
    };
}
