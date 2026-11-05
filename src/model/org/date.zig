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
