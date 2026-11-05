//! Chooses the active color scheme, including "auto" day/night switching by
//! sunrise/sunset (computed offline) or fixed times.

const std = @import("std");
const theme_mod = @import("../../framework/ui/theme.zig");
const scheme_loader = @import("../../framework/ui/scheme_loader.zig");
const sun = @import("sun.zig");

const Allocator = std.mem.Allocator;

pub const AutoBy = enum { sun, time };

/// Scheme names live in fixed buffers so settings reloads can't dangle.
pub const Name = struct {
    buf: [64]u8 = undefined,
    len: usize = 0,

    pub fn init(s: []const u8) Name {
        var n: Name = .{};
        n.set(s);
        return n;
    }

    pub fn set(self: *Name, s: []const u8) void {
        self.len = @min(s.len, self.buf.len);
        @memcpy(self.buf[0..self.len], s[0..self.len]);
    }

    pub fn get(self: *const Name) []const u8 {
        return self.buf[0..self.len];
    }
};

pub const ThemeSettings = struct {
    color_scheme: Name = .init("Mariana"),
    light_color_scheme: Name = .init("Breakers"),
    dark_color_scheme: Name = .init("Mariana"),
    auto_by: AutoBy = .sun,
    /// Minutes after midnight.
    day_start: f64 = 7 * 60,
    night_start: f64 = 19 * 60,
    location: ?sun.Location = null,
};

pub const ThemeManager = struct {
    /// The live theme; everything holds a pointer to this.
    theme: theme_mod.Theme,
    settings: ThemeSettings = .{},
    current: Name = .init("Mariana"),
    /// Location guessed from the time zone (looked up once).
    tz_location: ?sun.Location = null,
    tz_looked_up: bool = false,
    /// A scheme shown temporarily (palette preview).
    preview: ?Name = null,
    last_check: i64 = 0,
    /// Schemes read at runtime from a Sublime Text install and
    /// ~/.config/cs-zig/schemes (`catalog` names point into these).
    discovered: std.ArrayList(scheme_loader.LoadedScheme) = .empty,
    /// Built-in schemes, with installed ones replacing the built-in
    /// approximations of the same name, plus every other installed scheme.
    catalog: std.ArrayList(theme_mod.Scheme) = .empty,

    pub fn init() ThemeManager {
        return .{ .theme = theme_mod.build(&theme_mod.schemes[0]) };
    }

    pub fn deinit(self: *ThemeManager, gpa: Allocator) void {
        self.catalog.deinit(gpa);
        scheme_loader.deinitList(gpa, &self.discovered);
    }

    /// (Re)discovers installed color schemes and rebuilds the theme.
    pub fn loadSchemes(self: *ThemeManager, gpa: Allocator, io: std.Io) void {
        var found = scheme_loader.discover(gpa, io) catch return;
        var catalog: std.ArrayList(theme_mod.Scheme) = .empty;
        catalog.appendSlice(gpa, &theme_mod.schemes) catch {
            scheme_loader.deinitList(gpa, &found);
            return;
        };
        for (found.items) |ls| {
            for (catalog.items) |*existing| {
                if (!std.ascii.eqlIgnoreCase(existing.name, ls.scheme.name)) continue;
                // Hand-tuned built-ins (Mariana) keep their colors and chrome
                // but take the installed scheme's italics.
                if (existing.tuned == null) existing.* = ls.scheme else existing.styles = ls.scheme.styles;
                break;
            } else catalog.append(gpa, ls.scheme) catch {};
        }
        // The live theme's name points into the old lists: switch, rebuild, then free.
        var old_found = self.discovered;
        var old_catalog = self.catalog;
        self.discovered = found;
        self.catalog = catalog;
        _ = self.update(true);
        old_catalog.deinit(gpa);
        scheme_loader.deinitList(gpa, &old_found);
    }

    /// Every scheme that can be chosen.
    pub fn schemes(self: *const ThemeManager) []const theme_mod.Scheme {
        return if (self.catalog.items.len > 0) self.catalog.items else &theme_mod.schemes;
    }

    /// Finds a scheme by name, also accepting Sublime-style values such as
    /// "Packages/Color Scheme - Default/Breakers.sublime-color-scheme".
    pub fn lookup(self: *const ThemeManager, name: []const u8) ?*const theme_mod.Scheme {
        const list = self.schemes();
        for (list) |*sch| if (std.ascii.eqlIgnoreCase(sch.name, name)) return sch;
        var base = std.fs.path.basename(name);
        for ([_][]const u8{ ".sublime-color-scheme", ".hidden-color-scheme", ".tmTheme", ".hidden-tmTheme" }) |ext| {
            if (std.ascii.endsWithIgnoreCase(base, ext)) base = base[0 .. base.len - ext.len];
        }
        for (list) |*sch| if (std.ascii.eqlIgnoreCase(sch.name, base)) return sch;
        return null;
    }

    pub fn isAuto(self: *const ThemeManager) bool {
        return std.ascii.eqlIgnoreCase(self.settings.color_scheme.get(), "auto");
    }

    fn location(self: *ThemeManager) ?sun.Location {
        if (self.settings.location) |l| return l;
        if (!self.tz_looked_up) {
            self.tz_looked_up = true;
            self.tz_location = sun.locationFromTimeZone();
        }
        return self.tz_location;
    }

    pub const DayInfo = struct {
        is_day: bool,
        /// Today's switch times (local minutes) and how they were decided.
        day_start: f64,
        night_start: f64,
        from_sun: bool,
    };

    pub fn dayInfo(self: *ThemeManager) DayInfo {
        const now = sun.localNow();
        if (self.settings.auto_by == .sun) {
            if (self.location()) |loc| switch (sun.sunTimes(now.day_of_year, loc, now.utc_offset_min)) {
                .normal => |t| return .{ .is_day = now.minutes >= t.sunrise and now.minutes < t.sunset, .day_start = t.sunrise, .night_start = t.sunset, .from_sun = true },
                .polar_day => return .{ .is_day = true, .day_start = 0, .night_start = 1440, .from_sun = true },
                .polar_night => return .{ .is_day = false, .day_start = 1440, .night_start = 0, .from_sun = true },
            };
        }
        const a = self.settings.day_start;
        const b = self.settings.night_start;
        const m = now.minutes;
        const day = if (a <= b) m >= a and m < b else m >= a or m < b;
        return .{ .is_day = day, .day_start = a, .night_start = b, .from_sun = false };
    }

    /// The scheme that should be showing right now.
    pub fn desired(self: *ThemeManager) []const u8 {
        if (self.preview) |*p| return p.get();
        if (!self.isAuto()) return self.settings.color_scheme.get();
        return if (self.dayInfo().is_day) self.settings.light_color_scheme.get() else self.settings.dark_color_scheme.get();
    }

    /// Re-evaluates the scheme; returns true if the theme changed.
    pub fn update(self: *ThemeManager, force: bool) bool {
        const want = self.desired();
        if (!force and std.ascii.eqlIgnoreCase(want, self.current.get())) return false;
        const scheme = self.lookup(want) orelse &theme_mod.schemes[0];
        self.theme = theme_mod.build(scheme);
        self.current.set(scheme.name);
        return true;
    }

    pub fn setPreview(self: *ThemeManager, name: ?[]const u8) void {
        self.preview = if (name) |n| Name.init(n) else null;
    }
};
