//! Terminal colors: the scheme's foreground and background as the default
//! colors, VS Code's fixed ANSI colors 0-15 (the dark or the light set, as in
//! cs-code's terminal.ansi* defaults), and xterm's standard 256-color cube
//! and gray ramp.

const std = @import("std");
const scene_mod = @import("../../framework/gpu/scene.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const vterm = @import("../../model/terminal/vterm.zig");

const Color = scene_mod.Color;
const Theme = theme_mod.Theme;

const ansi_dark = [16]u24{
    0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
    0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xE5E5E5,
};
const ansi_light = [16]u24{
    0x000000, 0xCD3131, 0x00BC00, 0x949800, 0x0451A5, 0xBC05BC, 0x0598BC, 0x555555,
    0x666666, 0xCD3131, 0x14CE14, 0xB5BA00, 0x0451A5, 0xBC05BC, 0x0598BC, 0xA5A5A5,
};

pub const Palette = struct {
    fg: Color,
    bg: Color,
    ansi: [16]Color,

    pub fn fromTheme(theme: *const Theme) Palette {
        const bg = theme.background.withAlpha(1);
        const fg = opaqueOver(theme.foreground, bg);
        const set = if (theme.light) &ansi_light else &ansi_dark;
        var ansi: [16]Color = undefined;
        for (set, 0..) |rgb, i| ansi[i] = rgbColor(@truncate(rgb >> 16), @truncate(rgb >> 8), @truncate(rgb));
        return .{ .fg = fg, .bg = bg, .ansi = ansi };
    }

    /// The color of a cell's foreground (`is_fg`) or background.
    pub fn resolve(self: *const Palette, col: vterm.Color, is_fg: bool) Color {
        return switch (col) {
            .default => if (is_fg) self.fg else self.bg,
            .indexed => |i| self.indexed(i),
            .rgb => |rgb| rgbColor(rgb[0], rgb[1], rgb[2]),
        };
    }

    pub fn indexed(self: *const Palette, i: u8) Color {
        if (i < 16) return self.ansi[i];
        if (i >= 232) {
            const v: u8 = 8 + 10 * (i - 232);
            return rgbColor(v, v, v);
        }
        const levels = [6]u8{ 0, 95, 135, 175, 215, 255 };
        const k = i - 16;
        return rgbColor(levels[k / 36], levels[(k / 6) % 6], levels[k % 6]);
    }
};

fn rgbColor(r: u8, g: u8, b: u8) Color {
    return .{ .r = @as(f32, @floatFromInt(r)) / 255, .g = @as(f32, @floatFromInt(g)) / 255, .b = @as(f32, @floatFromInt(b)) / 255 };
}

/// A translucent scheme color as it looks over `bg`.
fn opaqueOver(col: Color, bg: Color) Color {
    return bg.mix(col.withAlpha(1), col.a).withAlpha(1);
}

fn sameColor(a: Color, b: Color) bool {
    return @abs(a.r - b.r) < 0.01 and @abs(a.g - b.g) < 0.01 and @abs(a.b - b.b) < 0.01;
}

test "ANSI colors are VS Code's, dark or light by scheme" {
    const dark = Palette.fromTheme(&theme_mod.mariana);
    try std.testing.expect(sameColor(dark.ansi[1], rgbColor(0xCD, 0x31, 0x31)));
    try std.testing.expect(sameColor(dark.ansi[4], rgbColor(0x24, 0x72, 0xC8)));
    try std.testing.expect(sameColor(dark.ansi[0], rgbColor(0, 0, 0)));
    for (1..7) |i| for (i + 1..7) |j| try std.testing.expect(!sameColor(dark.ansi[i], dark.ansi[j]));
    try std.testing.expect(sameColor(dark.resolve(.default, true), theme_mod.mariana.foreground));
    try std.testing.expect(sameColor(dark.indexed(196), rgbColor(255, 0, 0)));
    try std.testing.expect(sameColor(dark.indexed(244), rgbColor(128, 128, 128)));
    var light = theme_mod.mariana;
    light.light = true;
    try std.testing.expect(sameColor(Palette.fromTheme(&light).ansi[2], rgbColor(0x00, 0xBC, 0x00)));
}
