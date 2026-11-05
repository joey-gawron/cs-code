//! Vim motions and text objects over a buffer. Positions are byte offsets
//! of the character the cursor sits on.

const std = @import("std");
const movement = @import("../../model/text/movement.zig");
const Buffer = @import("../../model/text/buffer.zig").Buffer;

const Scanner = movement.Scanner;

pub const Kind = enum { exclusive, inclusive, linewise };

/// The last line of the text: the empty row after a final newline is not a line.
pub fn lastRow(buffer: *const Buffer) u32 {
    const n = buffer.lineCount();
    if (n > 1 and buffer.lineStart(n - 1) == buffer.len()) return n - 2;
    return n - 1;
}

pub const Target = struct {
    offset: usize,
    kind: Kind,
    /// Keep the remembered column (j/k).
    keep_goal: bool = false,
};

pub const Range = struct {
    start: usize,
    end: usize,
    linewise: bool = false,
};

/// 0 = blank, 1 = punctuation, 2 = keyword. WORD motions merge 1 and 2.
fn class(cp: u21, big: bool) u8 {
    if (cp == ' ' or cp == '\t' or cp == '\n' or cp == '\r') return 0;
    if (big) return 2;
    if ((cp >= 'a' and cp <= 'z') or (cp >= 'A' and cp <= 'Z') or (cp >= '0' and cp <= '9') or cp == '_' or cp >= 0x80) return 2;
    return 1;
}

pub const Ctx = struct {
    buffer: *Buffer,
    s: Scanner,

    pub fn init(buffer: *Buffer) Ctx {
        return .{ .buffer = buffer, .s = Scanner.init(&buffer.rope) };
    }

    pub fn len(self: *const Ctx) usize {
        return self.buffer.len();
    }

    fn char(self: *Ctx, o: usize) u21 {
        if (o >= self.len()) return '\n';
        return self.s.charAt(o);
    }

    fn next(self: *Ctx, o: usize) usize {
        return self.s.nextBoundary(o);
    }

    fn prev(self: *Ctx, o: usize) usize {
        return self.s.prevBoundary(o);
    }

    pub fn row(self: *Ctx, o: usize) u32 {
        return self.buffer.offsetToPoint(o).row;
    }

    pub fn lineStart(self: *Ctx, o: usize) usize {
        return self.buffer.lineStart(self.row(o));
    }

    pub fn lineEnd(self: *Ctx, o: usize) usize {
        return self.buffer.lineEnd(self.row(o));
    }

    /// Offset of the last char on the line (or line start if empty).
    pub fn lastChar(self: *Ctx, r: u32) usize {
        const s = self.buffer.lineStart(r);
        const e = self.buffer.lineEnd(r);
        if (e == s) return s;
        return self.prev(e);
    }

    pub fn firstNonBlank(self: *Ctx, r: u32) usize {
        var o = self.buffer.lineStart(r);
        const e = self.buffer.lineEnd(r);
        while (o < e) : (o = self.next(o)) {
            const c = self.char(o);
            if (c != ' ' and c != '\t') break;
        }
        return o;
    }

    /// Normal-mode cursors can't sit on the newline of a non-empty line.
    pub fn clampNormal(self: *Ctx, o: usize) usize {
        const r = self.row(o);
        const e = self.buffer.lineEnd(r);
        const s = self.buffer.lineStart(r);
        if (o >= e and e > s) return self.prev(e);
        return @min(o, self.len());
    }

    fn isEmptyLine(self: *Ctx, o: usize) bool {
        return self.char(o) == '\n' and (o == 0 or self.char(self.prev(o)) == '\n');
    }

    // ---- Word motions -------------------------------------------------

    pub fn wordForward(self: *Ctx, start: usize, big: bool, for_change: bool) usize {
        const total = self.len();
        var o = start;
        if (o >= total) return total;
        const c0 = class(self.char(o), big);
        if (for_change and c0 != 0) {
            // cw acts like ce.
            return self.next(self.wordEnd(o, big, true));
        }
        if (c0 != 0) {
            while (o < total and class(self.char(o), big) == c0) o = self.next(o);
        }
        while (o < total and class(self.char(o), big) == 0) {
            if (self.char(o) == '\n') {
                o = self.next(o);
                if (o < total and self.char(o) == '\n') return o; // empty line is a word
                continue;
            }
            o = self.next(o);
        }
        return o;
    }

    pub fn wordEnd(self: *Ctx, start: usize, big: bool, stay: bool) usize {
        const total = self.len();
        if (total == 0) return 0;
        var o = start;
        if (!stay) o = self.next(o);
        if (stay and o < total and class(self.char(o), big) != 0) {
            const c = class(self.char(o), big);
            while (self.next(o) < total and class(self.char(self.next(o)), big) == c) o = self.next(o);
            return o;
        }
        while (o < total and class(self.char(o), big) == 0) o = self.next(o);
        if (o >= total) return self.prev(total);
        const c = class(self.char(o), big);
        while (self.next(o) < total and class(self.char(self.next(o)), big) == c) o = self.next(o);
        return o;
    }

    pub fn wordBackward(self: *Ctx, start: usize, big: bool) usize {
        var o = start;
        if (o == 0) return 0;
        o = self.prev(o);
        while (o > 0 and class(self.char(o), big) == 0) {
            if (self.isEmptyLine(o)) return o;
            o = self.prev(o);
        }
        const c = class(self.char(o), big);
        while (o > 0 and class(self.char(self.prev(o)), big) == c) o = self.prev(o);
        return o;
    }

    pub fn wordEndBackward(self: *Ctx, start: usize, big: bool) usize {
        var o = start;
        if (o == 0) return 0;
        const c0 = class(self.char(o), big);
        // Leave the current word.
        while (o > 0 and c0 != 0 and class(self.char(o), big) == c0) o = self.prev(o);
        while (o > 0 and class(self.char(o), big) == 0) o = self.prev(o);
        return o;
    }

    // ---- Character / line motions ------------------------------------

    pub fn findInLine(self: *Ctx, start: usize, target: u21, forward: bool, till: bool, count: usize) ?usize {
        const ls = self.lineStart(start);
        const le = self.lineEnd(start);
        var o = start;
        var found: usize = 0;
        if (forward) {
            var probe = self.next(o);
            if (till and probe < le and self.char(probe) == target) probe = self.next(probe);
            o = probe;
            while (o < le) : (o = self.next(o)) {
                if (self.char(o) == target) {
                    found += 1;
                    if (found == count) return if (till) self.prev(o) else o;
                }
            }
        } else {
            if (o == ls) return null;
            var probe = self.prev(o);
            if (till and probe > ls and self.char(probe) == target) probe = self.prev(probe);
            o = probe;
            while (true) {
                if (self.char(o) == target) {
                    found += 1;
                    if (found == count) return if (till) self.next(o) else o;
                }
                if (o == ls) break;
                o = self.prev(o);
            }
        }
        return null;
    }

    pub fn paragraphForward(self: *Ctx, start: usize, count: usize) usize {
        var r = self.row(start);
        const last = lastRow(self.buffer);
        var n: usize = 0;
        while (n < count) : (n += 1) {
            // Skip blank lines, then non-blank lines.
            while (r < last and self.buffer.lineLen(r) == 0) r += 1;
            while (r < last and self.buffer.lineLen(r) != 0) r += 1;
        }
        if (r == last and self.buffer.lineLen(r) != 0) return self.buffer.lineEnd(r);
        return self.buffer.lineStart(r);
    }

    pub fn paragraphBackward(self: *Ctx, start: usize, count: usize) usize {
        var r = self.row(start);
        var n: usize = 0;
        while (n < count) : (n += 1) {
            while (r > 0 and self.buffer.lineLen(r) == 0) r -= 1;
            while (r > 0 and self.buffer.lineLen(r) != 0) r -= 1;
        }
        return self.buffer.lineStart(r);
    }

    pub fn matchingBracket(self: *Ctx, start: usize) ?usize {
        const le = self.lineEnd(start);
        var o = start;
        while (o < le) : (o = self.next(o)) {
            const c = self.char(o);
            if (movement.isOpenBracket(c) or movement.isCloseBracket(c)) return movement.matchingBracket(&self.s, o);
        }
        return null;
    }

    // ---- Text objects --------------------------------------------------

    pub fn wordObject(self: *Ctx, at: usize, big: bool, around: bool, count: usize) ?Range {
        const total = self.len();
        if (total == 0) return null;
        var start = at;
        const c = class(self.char(at), big);
        // Start of the run under the cursor.
        while (start > self.lineStart(at) and class(self.char(self.prev(start)), big) == c and self.char(self.prev(start)) != '\n') start = self.prev(start);
        var end = at;
        var n: usize = 0;
        while (n < count) : (n += 1) {
            const cc = class(self.char(end), big);
            while (end < total and class(self.char(end), big) == cc and self.char(end) != '\n') end = self.next(end);
            if (around and n + 1 == count) {
                // Trailing whitespace, or leading if there is none.
                const before = end;
                while (end < total and (self.char(end) == ' ' or self.char(end) == '\t')) end = self.next(end);
                if (end == before) {
                    while (start > self.lineStart(at) and (self.char(self.prev(start)) == ' ' or self.char(self.prev(start)) == '\t')) start = self.prev(start);
                }
            }
        }
        return .{ .start = start, .end = end };
    }

    pub fn quoteObject(self: *Ctx, at: usize, quote: u21, around: bool) ?Range {
        const ls = self.lineStart(at);
        const le = self.lineEnd(at);
        // Collect quote positions on the line (ignoring escaped ones).
        var positions: [256]usize = undefined;
        var n: usize = 0;
        var o = ls;
        while (o < le and n < positions.len) : (o = self.next(o)) {
            if (self.char(o) == quote and (o == ls or self.char(self.prev(o)) != '\\')) {
                positions[n] = o;
                n += 1;
            }
        }
        var i: usize = 0;
        while (i + 1 < n) : (i += 2) {
            const open = positions[i];
            const close = positions[i + 1];
            if (at <= close and (at >= open or i == 0)) {
                if (around) {
                    var end = close + 1;
                    while (end < le and (self.char(end) == ' ' or self.char(end) == '\t')) end += 1;
                    return .{ .start = open, .end = end };
                }
                return .{ .start = open + 1, .end = close };
            }
        }
        return null;
    }

    pub fn bracketObject(self: *Ctx, at: usize, open: u8, close: u8, around: bool) ?Range {
        // Search backwards for an unmatched `open`, counting nesting.
        var depth: usize = 0;
        var o = at + 1;
        var open_at: ?usize = null;
        // The cursor may sit on the open bracket itself.
        if (at < self.len() and self.s.byte(at) == open) open_at = at;
        while (open_at == null and o > 0) {
            o -= 1;
            const b = self.s.byte(o);
            if (b == close and o != at) depth += 1 else if (b == open) {
                if (depth == 0) {
                    open_at = o;
                    break;
                }
                depth -= 1;
            }
        }
        const start = open_at orelse return null;
        const end = movement.matchingBracket(&self.s, start) orelse return null;
        if (around) return .{ .start = start, .end = end + 1 };
        // Inner object of a multi-line block excludes the bracket lines' newlines.
        var inner_start = start + 1;
        var inner_end = end;
        if (inner_start < self.len() and self.s.byte(inner_start) == '\n' and self.row(start) != self.row(end)) {
            inner_start += 1;
            inner_end = self.buffer.lineStart(self.row(end));
            return .{ .start = inner_start, .end = inner_end, .linewise = true };
        }
        return .{ .start = inner_start, .end = inner_end };
    }

    pub fn paragraphObject(self: *Ctx, at: usize, around: bool) Range {
        const r0 = self.row(at);
        const last = lastRow(self.buffer);
        const blank = self.buffer.lineLen(r0) == 0;
        var first = r0;
        var lst = r0;
        while (first > 0 and (self.buffer.lineLen(first - 1) == 0) == blank) first -= 1;
        while (lst < last and (self.buffer.lineLen(lst + 1) == 0) == blank) lst += 1;
        if (around) {
            while (lst < last and self.buffer.lineLen(lst + 1) == 0) lst += 1;
        }
        const end = if (lst < last) self.buffer.lineStart(lst + 1) else self.len();
        return .{ .start = self.buffer.lineStart(first), .end = end, .linewise = true };
    }
};
