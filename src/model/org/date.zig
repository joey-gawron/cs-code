//! Calendar dates and org timestamps (<2026-10-21 Wed 10:00-11:00 +1w>).

const std = @import("std");
const c = @import("c");

pub const day_names = [_][]const u8{ "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
pub const month_names = [_][]const u8{ "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

pub const Date = struct {
    year: i32,
    month: u8,
    day: u8,

    /// Days since 1970-01-01 (Howard Hinnant's days_from_civil).
    pub fn toDays(self: Date) i64 {
        const y: i64 = @as(i64, self.year) - @as(i64, if (self.month <= 2) 1 else 0);
        const era = @divFloor(y, 400);
        const yoe = y - era * 400;
        const m: i64 = self.month;
        const doy = @divFloor(153 * (if (m > 2) m - 3 else m + 9) + 2, 5) + self.day - 1;
        const doe = yoe * 365 + @divFloor(yoe, 4) - @divFloor(yoe, 100) + doy;
        return era * 146097 + doe - 719468;
    }

    pub fn fromDays(days: i64) Date {
        const z = days + 719468;
        const era = @divFloor(z, 146097);
        const doe = z - era * 146097;
        const yoe = @divFloor(doe - @divFloor(doe, 1460) + @divFloor(doe, 36524) - @divFloor(doe, 146096), 365);
        const doy = doe - (365 * yoe + @divFloor(yoe, 4) - @divFloor(yoe, 100));
        const mp = @divFloor(5 * doy + 2, 153);
        const d = doy - @divFloor(153 * mp + 2, 5) + 1;
        const m = if (mp < 10) mp + 3 else mp - 9;
        const y = yoe + era * 400 + @as(i64, if (m <= 2) 1 else 0);
        return .{ .year = @intCast(y), .month = @intCast(m), .day = @intCast(d) };
    }

    pub fn addDays(self: Date, n: i64) Date {
        return fromDays(self.toDays() + n);
    }

    pub fn addMonths(self: Date, n: i64) Date {
        const total = @as(i64, self.year) * 12 + (self.month - 1) + n;
        const y: i32 = @intCast(@divFloor(total, 12));
        const m: u8 = @intCast(@mod(total, 12) + 1);
        return .{ .year = y, .month = m, .day = @min(self.day, daysInMonth(y, m)) };
    }

    /// 0 = Monday ... 6 = Sunday.
    pub fn weekday(self: Date) u8 {
        // 1970-01-01 was a Thursday (3).
        return @intCast(@mod(self.toDays() + 3, 7));
    }

    pub fn eql(a: Date, b: Date) bool {
        return a.year == b.year and a.month == b.month and a.day == b.day;
    }

    pub fn isoWeek(self: Date) u8 {
        // The week containing this date's Thursday.
        const thursday = self.addDays(3 - @as(i64, self.weekday()));
        const jan1: Date = .{ .year = thursday.year, .month = 1, .day = 1 };
        return @intCast(@divFloor(thursday.toDays() - jan1.toDays(), 7) + 1);
    }

    pub fn today() Date {
        return now().date;
    }

    /// "2026-10-21 Wed"
    pub fn format(self: Date, buf: []u8) []const u8 {
        return std.fmt.bufPrint(buf, "{d:0>4}-{d:0>2}-{d:0>2} {s}", .{ @as(u32, @intCast(self.year)), self.month, self.day, day_names[self.weekday()][0..3] }) catch "";
    }

    pub fn parseIso(s: []const u8) ?Date {
        if (s.len < 10 or s[4] != '-' or s[7] != '-') return null;
        const y = std.fmt.parseInt(i32, s[0..4], 10) catch return null;
        const m = std.fmt.parseInt(u8, s[5..7], 10) catch return null;
        const d = std.fmt.parseInt(u8, s[8..10], 10) catch return null;
        if (m < 1 or m > 12 or d < 1 or d > daysInMonth(y, m)) return null;
        return .{ .year = y, .month = m, .day = d };
    }

    /// Org's date prompt: "2026-10-24", "+3d", "-1w", "+2m", "today", "tomorrow", "fri", "24".
    pub fn parseHuman(input: []const u8, base: Date) ?Date {
        const s = std.mem.trim(u8, input, " ");
        if (s.len == 0 or std.mem.eql(u8, s, ".") or std.ascii.eqlIgnoreCase(s, "today")) return base;
        if (std.ascii.eqlIgnoreCase(s, "tomorrow")) return base.addDays(1);
        if (std.ascii.eqlIgnoreCase(s, "yesterday")) return base.addDays(-1);
        if (parseIso(s)) |d| return d;
        if (s[0] == '+' or s[0] == '-') {
            var i: usize = 1;
            while (i < s.len and std.ascii.isDigit(s[i])) i += 1;
            const n = if (i > 1) std.fmt.parseInt(i64, s[1..i], 10) catch return null else 1;
            const sign: i64 = if (s[0] == '-') -1 else 1;
            const unit = if (i < s.len) s[i] else 'd';
            return switch (unit) {
                'd' => base.addDays(sign * n),
                'w' => base.addDays(sign * n * 7),
                'm' => base.addMonths(sign * n),
                'y' => base.addMonths(sign * n * 12),
                else => null,
            };
        }
        // A weekday name: the next such day (today counts).
        for (day_names, 0..) |name, wd| {
            if (s.len >= 3 and std.ascii.startsWithIgnoreCase(name, s)) {
                const delta = @mod(@as(i64, @intCast(wd)) - base.weekday(), 7);
                return base.addDays(delta);
            }
        }
        // A bare day of month: this month, or next if already past.
        if (std.fmt.parseInt(u8, s, 10)) |d| {
            if (d >= 1 and d <= 31) {
                var candidate: Date = .{ .year = base.year, .month = base.month, .day = @min(d, daysInMonth(base.year, base.month)) };
                if (candidate.toDays() < base.toDays()) candidate = (Date{ .year = base.year, .month = base.month, .day = 1 }).addMonths(1);
                candidate.day = @min(d, daysInMonth(candidate.year, candidate.month));
                return candidate;
            }
        } else |_| {}
        return null;
    }
};

pub fn isLeap(y: i32) bool {
    return (@mod(y, 4) == 0 and @mod(y, 100) != 0) or @mod(y, 400) == 0;
}

pub fn daysInMonth(y: i32, m: u8) u8 {
    return switch (m) {
        1, 3, 5, 7, 8, 10, 12 => 31,
        4, 6, 9, 11 => 30,
        else => if (isLeap(y)) 29 else 28,
    };
}

pub const Time = struct { hour: u8, minute: u8 };

/// The local date and time of day.
pub fn now() struct { date: Date, time: Time } {
    var t: c.time_t = c.time(null);
    var tm: c.struct_tm = undefined;
    _ = c.localtime_r(&t, &tm);
    return .{
        .date = .{ .year = tm.tm_year + 1900, .month = @intCast(tm.tm_mon + 1), .day = @intCast(tm.tm_mday) },
        .time = .{ .hour = @intCast(tm.tm_hour), .minute = @intCast(tm.tm_min) },
    };
}

pub const Repeat = struct {
    n: u16,
    unit: enum { day, week, month, year },
    /// How marking the item done moves the date: `+1w` by one interval,
    /// `++1w` by intervals until it is in the future, `.+1w` to one interval
    /// after today.
    kind: enum { cumulate, catch_up, restart } = .cumulate,

    /// The date `from` moves to when its item is marked done on `today`.
    pub fn next(self: Repeat, from: Date, today: Date) Date {
        const base = if (self.kind == .restart) today else from;
        var d = self.step(base);
        if (self.kind == .catch_up) {
            var guard: usize = 0;
            while (d.toDays() <= today.toDays() and guard < 100_000) : (guard += 1) d = self.step(d);
        }
        return d;
    }

    fn step(self: Repeat, d: Date) Date {
        return switch (self.unit) {
            .day => d.addDays(self.n),
            .week => d.addDays(@as(i64, self.n) * 7),
            .month => d.addMonths(self.n),
            .year => d.addMonths(@as(i64, self.n) * 12),
        };
    }
};

pub const Timestamp = struct {
    date: Date,
    time: ?Time = null,
    end_time: ?Time = null,
    active: bool,
    repeat: ?Repeat = null,
    /// The last day of a range, `<2026-10-21 Wed>--<2026-10-23 Fri>`.
    range_end: ?Date = null,
    /// A deadline's own warning period in days (`-3d`), which wins over the
    /// agenda's default.
    warn_days: ?u16 = null,
    /// Byte range of the whole timestamp (both ends of a range) in the
    /// parsed text.
    start: usize = 0,
    end: usize = 0,

    /// Does this (possibly repeating) timestamp fall on `day`?
    pub fn occursOn(self: Timestamp, day: Date) bool {
        const d0 = self.date.toDays();
        const d1 = day.toDays();
        if (d1 == d0) return true;
        if (self.range_end) |last| return d1 > d0 and d1 <= last.toDays();
        const rep = self.repeat orelse return false;
        if (d1 < d0 or rep.n == 0) return false;
        return switch (rep.unit) {
            .day => @mod(d1 - d0, rep.n) == 0,
            .week => @mod(d1 - d0, @as(i64, rep.n) * 7) == 0,
            .month => self.date.day == day.day and @mod((day.year - self.date.year) * 12 + @as(i32, day.month) - self.date.month, rep.n) == 0,
            .year => self.date.day == day.day and self.date.month == day.month and @mod(day.year - self.date.year, rep.n) == 0,
        };
    }
};

fn parseTime(s: []const u8) ?Time {
    const colon = std.mem.indexOfScalar(u8, s, ':') orelse return null;
    const h = std.fmt.parseInt(u8, s[0..colon], 10) catch return null;
    const m = std.fmt.parseInt(u8, s[colon + 1 ..], 10) catch return null;
    if (h > 24 or m > 59) return null;
    return .{ .hour = h, .minute = m };
}

/// What the date prompt reads: a date, optionally a time or a time range and a
/// repeater and warning period after it ("fri 10:00-11:30 +1w -3d").
pub const Input = struct {
    date: Date,
    time: ?Time = null,
    end_time: ?Time = null,
    suffix_buf: [32]u8 = undefined,
    suffix_len: u8 = 0,

    pub fn suffix(self: *const Input) []const u8 {
        return self.suffix_buf[0..self.suffix_len];
    }

    /// The inside of a timestamp: "2026-11-20 Fri 10:00-11:30 +1w -3d".
    pub fn format(self: *const Input, buf: []u8) []const u8 {
        var w: std.Io.Writer = .fixed(buf);
        var dbuf: [32]u8 = undefined;
        w.writeAll(self.date.format(&dbuf)) catch return "";
        if (self.time) |t| {
            w.print(" {d:0>2}:{d:0>2}", .{ t.hour, t.minute }) catch return "";
            if (self.end_time) |e| w.print("-{d:0>2}:{d:0>2}", .{ e.hour, e.minute }) catch return "";
        }
        if (self.suffix_len > 0) w.print(" {s}", .{self.suffix()}) catch return "";
        return w.buffered();
    }
};

const Clock = struct { time: Time, pm: ?bool };

/// One clock time: "14:00", "10:30am", "2pm" (a bare number is not a time).
/// `inherit` gives the am/pm of the other end of a range.
fn parseClock(tok: []const u8, inherit: ?bool) ?Clock {
    var body = tok;
    var pm: ?bool = inherit;
    if (std.ascii.endsWithIgnoreCase(body, "am")) {
        pm = false;
        body = body[0 .. body.len - 2];
    } else if (std.ascii.endsWithIgnoreCase(body, "pm")) {
        pm = true;
        body = body[0 .. body.len - 2];
    } else if (std.mem.indexOfScalar(u8, body, ':') == null) return null;
    if (body.len == 0) return null;
    var hour: u8 = undefined;
    var minute: u8 = 0;
    if (std.mem.indexOfScalar(u8, body, ':')) |colon| {
        hour = std.fmt.parseInt(u8, body[0..colon], 10) catch return null;
        minute = std.fmt.parseInt(u8, body[colon + 1 ..], 10) catch return null;
    } else hour = std.fmt.parseInt(u8, body, 10) catch return null;
    if (minute > 59 or hour > 24) return null;
    if (pm) |is_pm| {
        if (hour == 0 or hour > 12) return null;
        hour = if (is_pm) (if (hour == 12) 12 else hour + 12) else (if (hour == 12) 0 else hour);
    }
    return .{ .time = .{ .hour = hour, .minute = minute }, .pm = pm };
}

/// A time or a range of two ("10:00-11:30", "9-11am").
fn parseClockToken(tok: []const u8) ?struct { start: Time, end: ?Time } {
    if (tok.len == 0 or !std.ascii.isDigit(tok[0])) return null;
    if (std.mem.indexOfScalar(u8, tok, '-')) |dash| {
        const right = parseClock(tok[dash + 1 ..], null) orelse return null;
        const left_text = tok[0..dash];
        // "9-11am": a bare left end takes the right end's am/pm.
        const bare = std.mem.indexOfScalar(u8, left_text, ':') == null and
            !std.ascii.endsWithIgnoreCase(left_text, "am") and !std.ascii.endsWithIgnoreCase(left_text, "pm");
        const left = if (bare) parseClockPlain(left_text, right.pm) else parseClock(left_text, null);
        return .{ .start = (left orelse return null).time, .end = right.time };
    }
    const one = parseClock(tok, null) orelse return null;
    return .{ .start = one.time, .end = null };
}

fn parseClockPlain(digits: []const u8, pm: ?bool) ?Clock {
    const hour = std.fmt.parseInt(u8, digits, 10) catch return null;
    if (hour == 0 or hour > 12) return null;
    const is_pm = pm orelse return null;
    return .{ .time = .{ .hour = if (is_pm) (if (hour == 12) 12 else hour + 12) else (if (hour == 12) 0 else hour), .minute = 0 }, .pm = pm };
}

fn monthIndex(tok: []const u8) ?u8 {
    if (tok.len < 3) return null;
    for (month_names, 0..) |name, i| if (std.ascii.startsWithIgnoreCase(name, tok)) return @intCast(i + 1);
    return null;
}

/// A repeater (+1w, ++1m, .+2d) or a warning period (-3d).
pub fn isSuffixToken(tok: []const u8) bool {
    var rest = tok;
    if (std.mem.startsWith(u8, rest, "++") or std.mem.startsWith(u8, rest, ".+")) {
        rest = rest[2..];
    } else if (rest.len > 0 and (rest[0] == '+' or rest[0] == '-')) rest = rest[1..] else return false;
    if (rest.len < 2) return false;
    for (rest[0 .. rest.len - 1]) |ch| if (!std.ascii.isDigit(ch)) return false;
    return std.mem.indexOfScalar(u8, "dwmy", rest[rest.len - 1]) != null;
}

/// "+2fri": the second such weekday from `base`.
fn parseNthWeekday(tok: []const u8, base: Date) ?Date {
    if (tok.len < 5 or tok[0] != '+') return null;
    var i: usize = 1;
    while (i < tok.len and std.ascii.isDigit(tok[i])) i += 1;
    if (i == 1 or i >= tok.len) return null;
    const n = std.fmt.parseInt(i64, tok[1..i], 10) catch return null;
    if (n < 1) return null;
    const first = Date.parseHuman(tok[i..], base) orelse return null;
    if (first.toDays() == base.toDays() and tok[i..].len < 3) return null;
    // Only weekday names count here.
    for (day_names) |name| if (tok.len - i >= 3 and std.ascii.startsWithIgnoreCase(name, tok[i..])) return first.addDays((n - 1) * 7);
    return null;
}

/// Org's date prompt: a date as `Date.parseHuman` reads it, plus `11/20`,
/// `nov 20`, `20 nov 2027`, `+2fri`, a time anywhere (`14:00`, `2pm`,
/// `9-11am`, `10:00-11:30`) and, after the date, a repeater and a warning
/// period. An empty input is `base`.
pub fn parseInput(input: []const u8, base: Date) ?Input {
    var result: Input = .{ .date = base };
    var date_toks: [4][]const u8 = undefined;
    var n_date: usize = 0;
    var it = std.mem.tokenizeScalar(u8, input, ' ');
    var suffix_w: std.Io.Writer = .fixed(&result.suffix_buf);
    var seen_time = false;
    while (it.next()) |tok| {
        if (parseClockToken(tok)) |clock| {
            if (seen_time) return null;
            seen_time = true;
            result.time = clock.start;
            result.end_time = clock.end;
        } else if (n_date > 0 and isSuffixToken(tok)) {
            if (result.suffix_len > 0) suffix_w.writeByte(' ') catch return null;
            suffix_w.writeAll(tok) catch return null;
            result.suffix_len = @intCast(suffix_w.buffered().len);
        } else {
            if (n_date == date_toks.len) return null;
            date_toks[n_date] = tok;
            n_date += 1;
        }
    }
    if (n_date == 0) return result;
    const toks = date_toks[0..n_date];

    // A month name with a day and maybe a year, in either order.
    var month: ?u8 = null;
    var day: ?u8 = null;
    var year: ?i32 = null;
    for (toks) |tok| {
        if (monthIndex(tok)) |m| {
            if (month != null) return null;
            month = m;
        } else if (std.fmt.parseInt(u16, tok, 10)) |num| {
            if (num >= 1000) year = num else if (num >= 1 and num <= 31 and day == null) day = @intCast(num) else return null;
        } else |_| {
            month = null;
            day = null;
            year = null;
            break;
        }
    }
    if (month) |m| {
        const d = day orelse return null;
        var candidate: Date = .{ .year = year orelse base.year, .month = m, .day = d };
        if (d > daysInMonth(candidate.year, m)) return null;
        if (year == null and candidate.toDays() < base.toDays()) candidate.year += 1;
        result.date = candidate;
        return result;
    }
    if (toks.len != 1) return null;
    const tok = toks[0];
    // 11/20 (this year, or next when past).
    if (std.mem.indexOfScalar(u8, tok, '/')) |slash| {
        const m = std.fmt.parseInt(u8, tok[0..slash], 10) catch return null;
        const d = std.fmt.parseInt(u8, tok[slash + 1 ..], 10) catch return null;
        if (m < 1 or m > 12 or d < 1 or d > daysInMonth(base.year, m)) return null;
        var candidate: Date = .{ .year = base.year, .month = m, .day = d };
        if (candidate.toDays() < base.toDays()) candidate.year += 1;
        result.date = candidate;
        return result;
    }
    result.date = parseNthWeekday(tok, base) orelse Date.parseHuman(tok, base) orelse return null;
    return result;
}

/// Parses a timestamp starting at text[at] ('<' or '['), with the second
/// half of a range (`--<...>`) if one follows.
pub fn parseTimestamp(text: []const u8, at: usize) ?Timestamp {
    var ts = parseSingle(text, at) orelse return null;
    if (std.mem.startsWith(u8, text[ts.end..], "--")) {
        if (parseSingle(text, ts.end + 2)) |last| if (last.active == ts.active and last.date.toDays() > ts.date.toDays()) {
            ts.range_end = last.date;
            ts.end = last.end;
        };
    }
    return ts;
}

fn parseSingle(text: []const u8, at: usize) ?Timestamp {
    if (at >= text.len) return null;
    const open = text[at];
    const close: u8 = switch (open) {
        '<' => '>',
        '[' => ']',
        else => return null,
    };
    const end = std.mem.indexOfScalarPos(u8, text, at + 1, close) orelse return null;
    const inner = text[at + 1 .. end];
    if (std.mem.indexOfScalar(u8, inner, '\n') != null) return null;
    const date = Date.parseIso(inner) orelse return null;
    var ts: Timestamp = .{ .date = date, .active = open == '<', .start = at, .end = end + 1 };
    var it = std.mem.tokenizeScalar(u8, inner[10..], ' ');
    while (it.next()) |tok| {
        if (tok[0] == '+' or tok[0] == '.') {
            // Repeaters: +1w, ++1w, .+1w
            const digits_start = std.mem.indexOfAny(u8, tok, "0123456789") orelse continue;
            var i = digits_start;
            while (i < tok.len and std.ascii.isDigit(tok[i])) i += 1;
            if (i >= tok.len) continue;
            const n = std.fmt.parseInt(u16, tok[digits_start..i], 10) catch continue;
            ts.repeat = .{
                .n = n,
                .unit = switch (tok[i]) {
                    'd' => .day,
                    'w' => .week,
                    'm' => .month,
                    'y' => .year,
                    else => continue,
                },
                .kind = if (std.mem.startsWith(u8, tok, "++")) .catch_up else if (std.mem.startsWith(u8, tok, ".+")) .restart else .cumulate,
            };
        } else if (tok[0] == '-' and tok.len >= 3) {
            // Warning period: -3d, -2w, -1m, -1y (in days).
            const unit = tok[tok.len - 1];
            const n = std.fmt.parseInt(u16, tok[1 .. tok.len - 1], 10) catch continue;
            const per_unit: u16 = switch (unit) {
                'd' => 1,
                'w' => 7,
                'm' => 30,
                'y' => 365,
                else => continue,
            };
            ts.warn_days = n *| per_unit;
        } else if (std.ascii.isDigit(tok[0])) {
            if (std.mem.indexOfScalar(u8, tok, '-')) |dash| {
                ts.time = parseTime(tok[0..dash]);
                ts.end_time = parseTime(tok[dash + 1 ..]);
            } else ts.time = parseTime(tok);
        }
    }
    return ts;
}

test "dates" {
    const d: Date = .{ .year = 2026, .month = 10, .day = 21 };
    try std.testing.expectEqual(@as(u8, 2), d.weekday()); // Wednesday
    try std.testing.expect(Date.fromDays(d.toDays()).eql(d));
    try std.testing.expect(d.addDays(11).eql(.{ .year = 2026, .month = 11, .day = 1 }));
    try std.testing.expectEqual(@as(u8, 43), d.isoWeek());
    try std.testing.expect(Date.parseHuman("+3d", d).?.eql(.{ .year = 2026, .month = 10, .day = 24 }));
    try std.testing.expect(Date.parseHuman("fri", d).?.eql(.{ .year = 2026, .month = 10, .day = 23 }));
    try std.testing.expect((Date{ .year = 2024, .month = 1, .day = 31 }).addMonths(1).eql(.{ .year = 2024, .month = 2, .day = 29 }));
}

test "timestamps" {
    const t = parseTimestamp("SCHEDULED: <2026-10-21 Wed 10:00-11:30 +1w>", 11).?;
    try std.testing.expect(t.active);
    try std.testing.expectEqual(@as(u8, 10), t.time.?.hour);
    try std.testing.expectEqual(@as(u8, 30), t.end_time.?.minute);
    try std.testing.expect(t.occursOn(.{ .year = 2026, .month = 10, .day = 28 }));
    try std.testing.expect(!t.occursOn(.{ .year = 2026, .month = 10, .day = 29 }));
    try std.testing.expect(parseTimestamp("[2026-13-01 Thu]", 0) == null);
}

test "repeaters" {
    const today: Date = .{ .year = 2026, .month = 10, .day = 22 };
    const from: Date = .{ .year = 2026, .month = 10, .day = 1 };
    const plus = parseTimestamp("<2026-10-01 Thu +1w>", 0).?.repeat.?;
    try std.testing.expect(plus.kind == .cumulate);
    try std.testing.expect(plus.next(from, today).eql(.{ .year = 2026, .month = 10, .day = 8 }));
    const catch_up = parseTimestamp("<2026-10-01 Thu ++1w>", 0).?.repeat.?;
    try std.testing.expect(catch_up.kind == .catch_up);
    try std.testing.expect(catch_up.next(from, today).eql(.{ .year = 2026, .month = 10, .day = 29 }));
    const restart = parseTimestamp("<2026-10-01 Thu .+2d>", 0).?.repeat.?;
    try std.testing.expect(restart.kind == .restart);
    try std.testing.expect(restart.next(from, today).eql(.{ .year = 2026, .month = 10, .day = 24 }));
}

test "ranges" {
    const t = parseTimestamp("<2026-10-21 Wed>--<2026-10-23 Fri> after", 0).?;
    try std.testing.expect(t.range_end.?.eql(.{ .year = 2026, .month = 10, .day = 23 }));
    try std.testing.expectEqual(@as(usize, 34), t.end);
    try std.testing.expect(t.occursOn(.{ .year = 2026, .month = 10, .day = 22 }));
    try std.testing.expect(t.occursOn(.{ .year = 2026, .month = 10, .day = 23 }));
    try std.testing.expect(!t.occursOn(.{ .year = 2026, .month = 10, .day = 24 }));
    try std.testing.expect(!t.occursOn(.{ .year = 2026, .month = 10, .day = 20 }));
}

test "warning periods" {
    const t = parseTimestamp("<2026-10-21 Wed 10:00 -3d>", 0).?;
    try std.testing.expectEqual(@as(?u16, 3), t.warn_days);
    try std.testing.expectEqual(@as(u8, 10), t.time.?.hour);
    try std.testing.expectEqual(@as(?u16, 14), parseTimestamp("<2026-10-21 Wed +1w -2w>", 0).?.warn_days);
    try std.testing.expect(parseTimestamp("<2026-10-21 Wed>", 0).?.warn_days == null);
}

test "the date prompt" {
    const base: Date = .{ .year = 2026, .month = 11, .day = 5 }; // a Thursday
    const eq = struct {
        fn f(want: []const u8, input: []const u8) !void {
            var buf: [64]u8 = undefined;
            const got = parseInput(input, .{ .year = 2026, .month = 11, .day = 5 }) orelse return error.Unparsed;
            try std.testing.expectEqualStrings(want, got.format(&buf));
        }
    }.f;
    try eq("2026-11-05 Thu", "");
    try eq("2026-11-05 Thu", ".");
    try eq("2026-11-06 Fri", "tomorrow");
    try eq("2026-11-08 Sun", "+3d");
    try eq("2026-11-20 Fri", "2026-11-20");
    try eq("2026-11-20 Fri", "11/20");
    try eq("2027-01-04 Mon", "1/4");
    try eq("2026-11-20 Fri", "nov 20");
    try eq("2026-11-20 Fri", "20 nov");
    try eq("2027-11-20 Sat", "20 nov 2027");
    try eq("2026-11-20 Fri", "20");
    try eq("2026-11-13 Fri", "+2fri");
    try eq("2026-11-06 Fri 14:00", "fri 14:00");
    try eq("2026-11-06 Fri 14:00", "14:00 fri");
    try eq("2026-11-05 Thu 14:00", "2pm");
    try eq("2026-11-05 Thu 10:30", "10:30am");
    try eq("2026-11-05 Thu 09:00-11:00", "9-11am");
    try eq("2026-11-05 Thu 10:00-11:30", "10:00-11:30");
    try eq("2026-11-06 Fri 10:00 +1w", "fri 10:00 +1w");
    try eq("2026-11-06 Fri ++1m", "fri ++1m");
    try eq("2026-11-06 Fri .+2d -3d", "fri .+2d -3d");
    // A leading +N is a relative date; after a date it is a repeater.
    try eq("2026-11-12 Thu", "+1w");
    try std.testing.expect(parseInput("blah", base) == null);
    try std.testing.expect(parseInput("nov 40", base) == null);
    try std.testing.expect(parseInput("14:00 15:00", base) == null);
}
