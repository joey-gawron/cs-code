//! Offline sunrise / sunset (NOAA's solar equations) and a location guess
//! from the system time zone, so day/night themes need no network.

const std = @import("std");
const c = @import("c");

pub const Location = struct {
    latitude: f64,
    longitude: f64,
};

/// Local minutes since midnight.
pub const SunTimes = union(enum) {
    normal: struct { sunrise: f64, sunset: f64 },
    polar_day,
    polar_night,
};

/// Sunrise and sunset for a day of the year at a location, in local minutes
/// (UTC offset in minutes, east positive).
pub fn sunTimes(day_of_year: u32, loc: Location, utc_offset_min: f64) SunTimes {
    const deg = std.math.pi / 180.0;
    // Fractional year (radians) at local noon.
    const g = 2.0 * std.math.pi / 365.0 * (@as(f64, @floatFromInt(day_of_year)) - 1.0);
    const eqtime = 229.18 * (0.000075 + 0.001868 * @cos(g) - 0.032077 * @sin(g) -
        0.014615 * @cos(2 * g) - 0.040849 * @sin(2 * g));
    const decl = 0.006918 - 0.399912 * @cos(g) + 0.070257 * @sin(g) - 0.006758 * @cos(2 * g) +
        0.000907 * @sin(2 * g) - 0.002697 * @cos(3 * g) + 0.00148 * @sin(3 * g);
    const lat = loc.latitude * deg;
    // 90.833° accounts for refraction and the sun's radius.
    const cos_ha = @cos(90.833 * deg) / (@cos(lat) * @cos(decl)) - @tan(lat) * @tan(decl);
    if (cos_ha > 1) return .polar_night;
    if (cos_ha < -1) return .polar_day;
    const ha = std.math.acos(cos_ha) / deg;
    const sunrise_utc = 720.0 - 4.0 * (loc.longitude + ha) - eqtime;
    const sunset_utc = 720.0 - 4.0 * (loc.longitude - ha) - eqtime;
    return .{ .normal = .{
        .sunrise = @mod(sunrise_utc + utc_offset_min, 1440.0),
        .sunset = @mod(sunset_utc + utc_offset_min, 1440.0),
    } };
}

pub const LocalNow = struct {
    day_of_year: u32,
    /// Minutes since local midnight.
    minutes: f64,
    utc_offset_min: f64,
    /// Unix seconds.
    epoch: i64,
};

pub fn localNow() LocalNow {
    var t: c.time_t = c.time(null);
    var tm: c.struct_tm = undefined;
    _ = c.localtime_r(&t, &tm);
    return .{
        .day_of_year = @intCast(tm.tm_yday + 1),
        .minutes = @as(f64, @floatFromInt(tm.tm_hour * 60 + tm.tm_min)) + @as(f64, @floatFromInt(tm.tm_sec)) / 60.0,
        .utc_offset_min = @as(f64, @floatFromInt(tm.tm_gmtoff)) / 60.0,
        .epoch = @intCast(t),
    };
}

/// The system time zone name ("America/Los_Angeles"), from $TZ,
/// /etc/timezone or the /etc/localtime symlink.
pub fn timeZoneName(buf: []u8) ?[]const u8 {
    if (std.c.getenv("TZ")) |tz| {
        const s = std.mem.trimStart(u8, std.mem.span(tz), ":");
        if (std.mem.indexOf(u8, s, "zoneinfo/")) |i| return copy(buf, s[i + 9 ..]);
        if (s.len > 0 and std.mem.indexOfScalar(u8, s, '/') != null) return copy(buf, s);
    }
    if (readSmallFile("/etc/timezone", buf)) |s| {
        const t = std.mem.trim(u8, s, " \n");
        if (t.len > 0) return t;
    }
    var link: [512]u8 = undefined;
    const n = std.c.readlink("/etc/localtime", &link, link.len);
    if (n > 0) {
        const target = link[0..@intCast(n)];
        if (std.mem.indexOf(u8, target, "zoneinfo/")) |i| return copy(buf, target[i + 9 ..]);
    }
    return null;
}

fn copy(buf: []u8, s: []const u8) ?[]const u8 {
    if (s.len > buf.len) return null;
    @memcpy(buf[0..s.len], s);
    return buf[0..s.len];
}

fn readSmallFile(path: [*:0]const u8, buf: []u8) ?[]const u8 {
    const f = std.c.fopen(path, "rb") orelse return null;
    defer _ = std.c.fclose(f);
    const n = std.c.fread(buf.ptr, 1, buf.len, f);
    if (n == 0) return null;
    return buf[0..n];
}

/// "+4152-08739" or "+415100-0873900" (ISO 6709) → degrees.
fn parseIso6709(s: []const u8) ?Location {
    var split: usize = 1;
    while (split < s.len and s[split] != '+' and s[split] != '-') split += 1;
    if (split >= s.len) return null;
    const lat = parseDegrees(s[0..split], 2) orelse return null;
    const lon = parseDegrees(s[split..], 3) orelse return null;
    return .{ .latitude = lat, .longitude = lon };
}

fn parseDegrees(s: []const u8, deg_digits: usize) ?f64 {
    if (s.len < 1 + deg_digits + 2) return null;
    const sign: f64 = if (s[0] == '-') -1 else 1;
    const digits = s[1..];
    const d = std.fmt.parseFloat(f64, digits[0..deg_digits]) catch return null;
    const m = std.fmt.parseFloat(f64, digits[deg_digits .. deg_digits + 2]) catch return null;
    const sec = if (digits.len >= deg_digits + 4) std.fmt.parseFloat(f64, digits[deg_digits + 2 .. deg_digits + 4]) catch 0 else 0;
    return sign * (d + m / 60.0 + sec / 3600.0);
}

/// Coordinates of the time zone's principal city from the tz database
/// (zone1970.tab / zone.tab), e.g. Los Angeles for America/Los_Angeles.
pub fn locationFromTimeZone() ?Location {
    var name_buf: [128]u8 = undefined;
    const zone = timeZoneName(&name_buf) orelse return null;
    const tables = [_][*:0]const u8{
        "/usr/share/zoneinfo/zone1970.tab",
        "/usr/share/zoneinfo/zone.tab",
        "/etc/zoneinfo/zone1970.tab",
    };
    var buf: [64 * 1024]u8 = undefined;
    for (tables) |path| {
        const text = readSmallFile(path, &buf) orelse continue;
        if (lookupZone(text, zone)) |loc| return loc;
    }
    return null;
}

fn lookupZone(text: []const u8, zone: []const u8) ?Location {
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |line| {
        if (line.len == 0 or line[0] == '#') continue;
        var cols = std.mem.splitScalar(u8, line, '\t');
        _ = cols.next() orelse continue; // country codes
        const coords = cols.next() orelse continue;
        const name = cols.next() orelse continue;
        if (std.mem.eql(u8, name, zone)) return parseIso6709(coords);
    }
    return null;
}

test "sunrise and sunset" {
    // Los Angeles, 21 October (day 294), UTC-7: sunrise ≈ 07:10, sunset ≈ 18:16.
    const t = sunTimes(294, .{ .latitude = 34.05, .longitude = -118.25 }, -420);
    try std.testing.expect(t == .normal);
    try std.testing.expect(@abs(t.normal.sunrise - (7 * 60 + 10)) < 15);
    try std.testing.expect(@abs(t.normal.sunset - (18 * 60 + 16)) < 15);
    // Tromsø in December: polar night.
    try std.testing.expect(sunTimes(355, .{ .latitude = 69.65, .longitude = 18.96 }, 60) == .polar_night);
}

test "tz table parsing" {
    const loc = lookupZone("US\t+340308-1181434\tAmerica/Los_Angeles\tPacific\n", "America/Los_Angeles").?;
    try std.testing.expect(@abs(loc.latitude - 34.052) < 0.01);
    try std.testing.expect(@abs(loc.longitude + 118.243) < 0.01);
}
