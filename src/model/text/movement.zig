//! Text scanning helpers over a Rope: character classes, word / subword
//! boundaries (Sublime's `word_separators`), bracket matching.

const std = @import("std");
const Rope = @import("rope.zig").Rope;

pub const word_separators = "./\\()\"'-:,.;<>~!@#$%^&*|+=[]{}`~?";

pub const CharClass = enum { whitespace, newline, punctuation, word };

pub fn classify(cp: u21) CharClass {
    if (cp == '\n') return .newline;
    if (cp == ' ' or cp == '\t' or cp == '\r' or cp == 0x0b or cp == 0x0c) return .whitespace;
    if (cp < 128 and std.mem.indexOfScalar(u8, word_separators, @intCast(cp)) != null) return .punctuation;
    return .word;
}

pub fn isWordChar(cp: u21) bool {
    return classify(cp) == .word;
}

/// Random-access byte reader that copies a window of the rope, so scanning
/// back and forth costs O(1) amortized per byte.
pub const Scanner = struct {
    rope: *const Rope,
    window: [512]u8 = undefined,
    window_start: usize = 0,
    window_len: usize = 0,

    pub fn init(rope: *const Rope) Scanner {
        return .{ .rope = rope };
    }

    pub fn len(self: *const Scanner) usize {
        return self.rope.len();
    }

    pub fn byte(self: *Scanner, offset: usize) u8 {
        if (offset < self.window_start or offset >= self.window_start + self.window_len) self.refill(offset);
        return self.window[offset - self.window_start];
    }

    fn refill(self: *Scanner, offset: usize) void {
        const total = self.rope.len();
        const start = offset -| self.window.len / 2;
        const end = @min(total, start + self.window.len);
        var it = self.rope.chunks(start, end);
        var n: usize = 0;
        while (it.next()) |chunk| {
            @memcpy(self.window[n..][0..chunk.len], chunk);
            n += chunk.len;
        }
        self.window_start = start;
        self.window_len = n;
    }

    /// Codepoint starting at `offset` (offset must be a char boundary < len).
    pub fn charAt(self: *Scanner, offset: usize) u21 {
        const b0 = self.byte(offset);
        if (b0 < 0x80) return b0;
        const n = std.unicode.utf8ByteSequenceLength(b0) catch return 0xFFFD;
        if (offset + n > self.len()) return 0xFFFD;
        var tmp: [4]u8 = undefined;
        for (0..n) |i| tmp[i] = self.byte(offset + i);
        return std.unicode.utf8Decode(tmp[0..n]) catch 0xFFFD;
    }

    pub fn nextBoundary(self: *Scanner, offset: usize) usize {
        if (offset >= self.len()) return self.len();
        const n = std.unicode.utf8ByteSequenceLength(self.byte(offset)) catch 1;
        return @min(self.len(), offset + n);
    }

    pub fn prevBoundary(self: *Scanner, offset: usize) usize {
        if (offset == 0) return 0;
        var o = offset - 1;
        while (o > 0 and self.byte(o) & 0xC0 == 0x80) o -= 1;
        return o;
    }

    /// Char before `offset`, or null at the start.
    pub fn charBefore(self: *Scanner, offset: usize) ?u21 {
        if (offset == 0) return null;
        return self.charAt(self.prevBoundary(offset));
    }

    pub fn charAfter(self: *Scanner, offset: usize) ?u21 {
        if (offset >= self.len()) return null;
        return self.charAt(offset);
    }
};

/// ctrl+right: end of the next word or punctuation run, stopping at line ends.
pub fn nextWordEnd(s: *Scanner, offset: usize) usize {
    var o = offset;
    const total = s.len();
    if (o >= total) return total;
    if (s.charAt(o) == '\n') return o + 1;
    while (o < total) {
        const cp = s.charAt(o);
        if (classify(cp) != .whitespace) break;
        o = s.nextBoundary(o);
    }
    if (o >= total) return total;
    const cls = classify(s.charAt(o));
    if (cls == .newline) return o;
    while (o < total and classify(s.charAt(o)) == cls) o = s.nextBoundary(o);
    return o;
}

/// ctrl+left: start of the previous word or punctuation run.
pub fn prevWordStart(s: *Scanner, offset: usize) usize {
    var o = offset;
    if (o == 0) return 0;
    if (s.charBefore(o) == '\n') return o - 1;
    while (o > 0) {
        const cp = s.charBefore(o).?;
        if (classify(cp) != .whitespace) break;
        o = s.prevBoundary(o);
    }
    if (o == 0) return 0;
    const cls = classify(s.charBefore(o).?);
    if (cls == .newline) return o;
    while (o > 0 and classify(s.charBefore(o).?) == cls) o = s.prevBoundary(o);
    return o;
}

fn subwordKind(cp: u21) u8 {
    if (cp >= 'a' and cp <= 'z') return 'l';
    if (cp >= 'A' and cp <= 'Z') return 'u';
    if (cp >= '0' and cp <= '9') return 'd';
    if (cp == '_') return '_';
    return switch (classify(cp)) {
        .word => 'l',
        .punctuation => 'p',
        .whitespace => 'w',
        .newline => 'n',
    };
}

/// alt+right: next camelCase / snake_case boundary.
pub fn nextSubwordEnd(s: *Scanner, offset: usize) usize {
    const total = s.len();
    var o = offset;
    if (o >= total) return total;
    while (o < total and subwordKind(s.charAt(o)) == '_') o = s.nextBoundary(o);
    while (o < total and subwordKind(s.charAt(o)) == 'w') o = s.nextBoundary(o);
    if (o >= total) return total;
    const first = subwordKind(s.charAt(o));
    if (first == 'n') return if (o == offset) o + 1 else o;
    o = s.nextBoundary(o);
    while (o < total) {
        const k = subwordKind(s.charAt(o));
        if (first == 'u' and k == 'l') {
            o = s.nextBoundary(o);
            continue;
        }
        if (k != first) break;
        // "HTTPServer": stop before the 'S' that starts the next word.
        if (first == 'u' and o + 1 < total and subwordKind(s.charAt(s.nextBoundary(o))) == 'l') break;
        o = s.nextBoundary(o);
    }
    return o;
}

pub fn prevSubwordStart(s: *Scanner, offset: usize) usize {
    var o = offset;
    if (o == 0) return 0;
    while (o > 0 and subwordKind(s.charBefore(o).?) == '_') o = s.prevBoundary(o);
    while (o > 0 and subwordKind(s.charBefore(o).?) == 'w') o = s.prevBoundary(o);
    if (o == 0) return 0;
    const last = subwordKind(s.charBefore(o).?);
    if (last == 'n') return if (o == offset) o - 1 else o;
    o = s.prevBoundary(o);
    while (o > 0) {
        const k = subwordKind(s.charBefore(o).?);
        if (last == 'l' and k == 'u') return s.prevBoundary(o);
        if (k != last) break;
        o = s.prevBoundary(o);
    }
    return o;
}

/// The word (run of word chars) touching `offset`, if any.
pub fn wordAt(s: *Scanner, offset: usize) ?[2]usize {
    const total = s.len();
    var start = offset;
    var end = offset;
    const after_is_word = offset < total and isWordChar(s.charAt(offset));
    const before_is_word = offset > 0 and isWordChar(s.charBefore(offset).?);
    if (!after_is_word and !before_is_word) return null;
    while (start > 0 and isWordChar(s.charBefore(start).?)) start = s.prevBoundary(start);
    while (end < total and isWordChar(s.charAt(end))) end = s.nextBoundary(end);
    return .{ start, end };
}

/// Double-click semantics: word, or run of punctuation / whitespace.
pub fn classRunAt(s: *Scanner, offset: usize) [2]usize {
    const total = s.len();
    if (total == 0) return .{ 0, 0 };
    const at = if (offset >= total) s.prevBoundary(total) else offset;
    const cls = classify(s.charAt(at));
    if (cls == .newline) return .{ at, at };
    var start = at;
    var end = s.nextBoundary(at);
    while (start > 0 and classify(s.charBefore(start).?) == cls) start = s.prevBoundary(start);
    while (end < total and classify(s.charAt(end)) == cls) end = s.nextBoundary(end);
    return .{ start, end };
}

const open_brackets = "([{";
const close_brackets = ")]}";

pub fn isOpenBracket(cp: u21) bool {
    return cp < 128 and std.mem.indexOfScalar(u8, open_brackets, @intCast(cp)) != null;
}

pub fn isCloseBracket(cp: u21) bool {
    return cp < 128 and std.mem.indexOfScalar(u8, close_brackets, @intCast(cp)) != null;
}

pub fn closingFor(cp: u21) u8 {
    return close_brackets[std.mem.indexOfScalar(u8, open_brackets, @intCast(cp)).?];
}

/// Given the offset of a bracket char, the offset of its partner.
pub fn matchingBracket(s: *Scanner, offset: usize) ?usize {
    const total = s.len();
    if (offset >= total) return null;
    const ch = s.charAt(offset);
    const limit: usize = 1 << 20;
    if (isOpenBracket(ch)) {
        const close: u21 = closingFor(ch);
        var depth: usize = 0;
        var o = offset;
        var steps: usize = 0;
        while (o < total and steps < limit) : (steps += 1) {
            const cp = s.byte(o);
            if (cp == ch) depth += 1 else if (cp == close) {
                depth -= 1;
                if (depth == 0) return o;
            }
            o += 1;
        }
    } else if (isCloseBracket(ch)) {
        const open: u21 = open_brackets[std.mem.indexOfScalar(u8, close_brackets, @intCast(ch)).?];
        var depth: usize = 0;
        var o = offset + 1;
        var steps: usize = 0;
        while (o > 0 and steps < limit) : (steps += 1) {
            o -= 1;
            const cp = s.byte(o);
            if (cp == ch) depth += 1 else if (cp == open) {
                depth -= 1;
                if (depth == 0) return o;
            }
        }
    }
    return null;
}

/// Innermost bracket pair strictly enclosing [start, end): returns offsets of
/// the open and close bracket.
pub fn enclosingBrackets(s: *Scanner, start: usize, end: usize) ?[2]usize {
    var depth = [_]usize{ 0, 0, 0 };
    var o = start;
    var steps: usize = 0;
    while (o > 0 and steps < (1 << 20)) : (steps += 1) {
        o -= 1;
        const b = s.byte(o);
        if (std.mem.indexOfScalar(u8, close_brackets, b)) |k| depth[k] += 1 else if (std.mem.indexOfScalar(u8, open_brackets, b)) |k| {
            if (depth[k] > 0) {
                depth[k] -= 1;
                continue;
            }
            const close = matchingBracket(s, o) orelse continue;
            if (close >= end) return .{ o, close };
        }
    }
    return null;
}
