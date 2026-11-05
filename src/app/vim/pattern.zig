//! Vim's regular expression syntax in the engine's: `\(a\|b\)\+` becomes
//! `(a|b)+`, a bare `(` or `+` is a literal, `\<` and `\>` become `\b`, `\{n,m}`
//! counts and `\{-}` lazy ones are translated, and `\c` / `\C` set the case
//! sensitivity. `\v` (very magic) switches the rest to the engine's own syntax.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Case = enum { default, ignore, match };

pub const Translated = struct {
    /// The engine's pattern, owned by the caller.
    pattern: []u8,
    case: Case,
};

pub fn translate(gpa: Allocator, vim_pattern: []const u8) Allocator.Error!Translated {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var case: Case = .default;
    var very_magic = false;
    var i: usize = 0;
    while (i < vim_pattern.len) : (i += 1) {
        const ch = vim_pattern[i];
        if (ch == '\\' and i + 1 < vim_pattern.len) {
            i += 1;
            const nx = vim_pattern[i];
            switch (nx) {
                'c' => case = .ignore,
                'C' => case = .match,
                'v' => very_magic = true,
                'V', 'm', 'M' => very_magic = false,
                '<', '>' => try out.appendSlice(gpa, "\\b"),
                // \zs and \ze (match start and end) are not supported: dropped.
                'z' => if (i + 1 < vim_pattern.len and (vim_pattern[i + 1] == 's' or vim_pattern[i + 1] == 'e')) {
                    i += 1;
                } else try out.appendSlice(gpa, "z"),
                '(', ')', '|', '+', '?', '{', '}' => if (very_magic) {
                    try out.append(gpa, '\\');
                    try out.append(gpa, nx);
                } else if (nx == '{') {
                    i = try braces(gpa, &out, vim_pattern, i);
                } else try out.append(gpa, nx),
                '=' => try out.append(gpa, '?'),
                '/' => try out.append(gpa, '/'),
                else => {
                    try out.append(gpa, '\\');
                    try out.append(gpa, nx);
                },
            }
        } else if (!very_magic and (ch == '(' or ch == ')' or ch == '|' or ch == '+' or ch == '?' or ch == '{' or ch == '}')) {
            try out.append(gpa, '\\');
            try out.append(gpa, ch);
        } else try out.append(gpa, ch);
    }
    return .{ .pattern = try out.toOwnedSlice(gpa), .case = case };
}

/// `\{n,m}` and `\{-n,m}` (lazy), the closing brace optionally escaped; `i`
/// is at the `{`, the return value at the `}` that ends the count.
fn braces(gpa: Allocator, out: *std.ArrayList(u8), pattern: []const u8, start: usize) Allocator.Error!usize {
    var i = start + 1;
    var lazy = false;
    if (i < pattern.len and pattern[i] == '-') {
        lazy = true;
        i += 1;
    }
    const body_start = i;
    while (i < pattern.len and pattern[i] != '}' and !(pattern[i] == '\\' and i + 1 < pattern.len and pattern[i + 1] == '}')) i += 1;
    const body = pattern[body_start..i];
    try out.append(gpa, '{');
    try out.appendSlice(gpa, if (body.len == 0) "0," else body);
    try out.append(gpa, '}');
    if (lazy) try out.append(gpa, '?');
    if (i < pattern.len and pattern[i] == '\\') i += 1;
    return i;
}

/// Whether a search with `ignorecase` and `smartcase` set ignores case for `pattern`.
pub fn ignoresCase(case: Case, ignorecase: bool, smartcase: bool, vim_pattern: []const u8) bool {
    switch (case) {
        .ignore => return true,
        .match => return false,
        .default => {},
    }
    if (!ignorecase) return false;
    if (!smartcase) return true;
    var i: usize = 0;
    while (i < vim_pattern.len) : (i += 1) {
        // A capital that is not the letter after a backslash (\S, \W) makes it case-sensitive.
        if (vim_pattern[i] == '\\') {
            i += 1;
            continue;
        }
        if (std.ascii.isUpper(vim_pattern[i])) return false;
    }
    return true;
}

fn expectTranslated(vim_pattern: []const u8, expected: []const u8) !void {
    const t = try translate(std.testing.allocator, vim_pattern);
    defer std.testing.allocator.free(t.pattern);
    try std.testing.expectEqualStrings(expected, t.pattern);
}

test "magic syntax: groups, alternation and counts" {
    try expectTranslated("\\(a\\|b\\)\\+", "(a|b)+");
    try expectTranslated("a\\?b\\=", "a?b?");
    try expectTranslated("a\\{2,3}", "a{2,3}");
    try expectTranslated("a\\{2,3\\}", "a{2,3}");
    try expectTranslated("a\\{-1,}", "a{1,}?");
    try expectTranslated("a\\{-}", "a{0,}?");
    try expectTranslated("\\<word\\>", "\\bword\\b");
    try expectTranslated("\\d\\+\\s*\\w", "\\d+\\s*\\w");
}

test "magic syntax: bare metacharacters are literal" {
    try expectTranslated("f(x)", "f\\(x\\)");
    try expectTranslated("a+b?c|d", "a\\+b\\?c\\|d");
    try expectTranslated("{a}", "\\{a\\}");
    try expectTranslated("a.*b", "a.*b");
    try expectTranslated("\\.\\*\\[", "\\.\\*\\[");
    try expectTranslated("a\\/b", "a/b");
}

test "very magic passes the engine's syntax through" {
    try expectTranslated("\\v(a|b)+", "(a|b)+");
    try expectTranslated("\\v\\(x\\)", "\\(x\\)");
    try expectTranslated("\\v(a)\\V(b)", "(a)\\(b\\)");
}

test "case flags and smartcase" {
    const t = try translate(std.testing.allocator, "foo\\c");
    defer std.testing.allocator.free(t.pattern);
    try std.testing.expectEqualStrings("foo", t.pattern);
    try std.testing.expectEqual(Case.ignore, t.case);
    try std.testing.expect(!ignoresCase(.default, false, false, "foo"));
    try std.testing.expect(ignoresCase(.default, true, false, "Foo"));
    try std.testing.expect(ignoresCase(.default, true, true, "foo"));
    try std.testing.expect(!ignoresCase(.default, true, true, "Foo"));
    try std.testing.expect(ignoresCase(.default, true, true, "a\\S"));
    try std.testing.expect(ignoresCase(.ignore, false, false, "Foo"));
    try std.testing.expect(!ignoresCase(.match, true, false, "foo"));
}
