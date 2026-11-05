//! Terminal colors from the active color scheme: the scheme's foreground and
//! background as the default colors, ANSI colors 0–15 picked from the
//! scheme's syntax colors by hue (so `ls --color` looks like it belongs to
//! the scheme), and xterm's standard 256-color cube and gray ramp.

const std = @import("std");
const scene_mod = @import("../../framework/gpu/scene.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const vterm = @import("../../model/terminal/vterm.zig");

const Color = scene_mod.Color;
const Theme = theme_mod.Theme;

pub const Palette = struct {
    fg: Color,
    bg: Color,
    ansi: [16]Color,

    pub fn fromTheme(theme: *const Theme) Palette {
        const bg = theme.background.withAlpha(1);
        const fg = opaqueOver(theme.foreground, bg);
        const black = Color.black;
        const white = Color.white;
        var ansi: [16]Color = undefined;
        if (theme.light) {
            ansi[0] = fg.mix(black, 0.2);
            ansi[7] = bg.mix(fg, 0.3);
            ansi[15] = bg.mix(fg, 0.15);
        } else {
            ansi[0] = bg.mix(black, 0.3);
            ansi[7] = fg.mix(bg, 0.15);
            ansi[15] = fg.mix(white, 0.5);
        }
        ansi[8] = opaqueOver(theme.syntax.comment, bg);

        // Chromatic slots: the closest scheme color by hue, each used once.
        const s = theme.syntax;
        const candidates = [_]Color{ s.string, s.number, s.constant, s.keyword, s.operator, s.function, s.type, s.variable_builtin, s.parameter, s.property, s.tag, s.attribute, s.escape, s.label, theme.caret };
        const slots = [_]struct { index: u8, hue: f32 }{
            .{ .index = 1, .hue = 0 },
            .{ .index = 2, .hue = 115 },
            .{ .index = 3, .hue = 48 },
            .{ .index = 4, .hue = 215 },
            .{ .index = 5, .hue = 300 },
            .{ .index = 6, .hue = 180 },
        };
        const Pair = struct { slot: u8, cand: u8, dist: f32 };
        var pairs: [slots.len * candidates.len]Pair = undefined;
        var n: usize = 0;
        var sat_sum: f32 = 0;
        var sat_n: f32 = 0;
        for (candidates, 0..) |cand, ci| {
            const col = opaqueOver(cand, bg);
            const h = Hsl.of(col);
            // Grays and near-whites (HSL saturation is high for pale tints) don't count.
            const chroma = @max(col.r, @max(col.g, col.b)) - @min(col.r, @min(col.g, col.b));
            if (chroma < 0.15 or h.l < 0.15 or h.l > 0.9) continue;
            sat_sum += h.s;
            sat_n += 1;
            for (slots, 0..) |slot, si| {
                const d = hueDistance(h.h, slot.hue);
                if (d <= 32) {
                    pairs[n] = .{ .slot = @intCast(si), .cand = @intCast(ci), .dist = d };
                    n += 1;
                }
            }
        }
        std.mem.sort(Pair, pairs[0..n], {}, struct {
            fn lt(_: void, a: Pair, b: Pair) bool {
                return a.dist < b.dist;
            }
        }.lt);
        var slot_done: [slots.len]bool = @splat(false);
        var used: [slots.len]?Color = @splat(null);
        for (pairs[0..n]) |p| {
            if (slot_done[p.slot]) continue;
            const col = opaqueOver(candidates[p.cand], bg);
            // The same color can't stand for two ANSI colors.
            const taken = for (used) |u| {
                if (u) |uc| if (sameColor(uc, col)) break true;
            } else false;
            if (taken) continue;
            slot_done[p.slot] = true;
            used[p.slot] = col;
        }
        const sat = if (sat_n > 0) std.math.clamp(sat_sum / sat_n, 0.35, 0.8) else 0.6;
        for (slots, 0..) |slot, si| {
            ansi[slot.index] = used[si] orelse (Hsl{ .h = slot.hue, .s = sat, .l = if (theme.light) 0.42 else 0.62 }).color();
            ansi[slot.index + 8] = if (theme.light) ansi[slot.index].mix(black, 0.15) else ansi[slot.index].mix(white, 0.2);
        }
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

fn hueDistance(a: f32, b: f32) f32 {
    const d = @abs(a - b);
    return @min(d, 360 - d);
}

const Hsl = struct {
    h: f32,
    s: f32,
    l: f32,

    fn of(col: Color) Hsl {
        const max = @max(col.r, @max(col.g, col.b));
        const min = @min(col.r, @min(col.g, col.b));
        const l = (max + min) / 2;
        const d = max - min;
        if (d < 1e-5) return .{ .h = 0, .s = 0, .l = l };
        const s = d / (1 - @abs(2 * l - 1));
        var h: f32 = if (max == col.r)
            60 * @mod((col.g - col.b) / d, 6)
        else if (max == col.g)
            60 * ((col.b - col.r) / d + 2)
        else
            60 * ((col.r - col.g) / d + 4);
        if (h < 0) h += 360;
        return .{ .h = h, .s = s, .l = l };
    }

    fn color(self: Hsl) Color {
        const c = (1 - @abs(2 * self.l - 1)) * self.s;
        const hp = self.h / 60;
        const x = c * (1 - @abs(@mod(hp, 2) - 1));
        const rgb: [3]f32 = if (hp < 1) .{ c, x, 0 } else if (hp < 2) .{ x, c, 0 } else if (hp < 3) .{ 0, c, x } else if (hp < 4) .{ 0, x, c } else if (hp < 5) .{ x, 0, c } else .{ c, 0, x };
        const m = self.l - c / 2;
        return .{ .r = rgb[0] + m, .g = rgb[1] + m, .b = rgb[2] + m };
    }
};

test "ANSI colors come from the scheme by hue" {
    const p = Palette.fromTheme(&theme_mod.mariana);
    // Mariana's green string color and red constants.
    try std.testing.expect(sameColor(p.ansi[2], theme_mod.mariana.syntax.string));
    const red = Hsl.of(p.ansi[1]);
    try std.testing.expect(hueDistance(red.h, 0) < 20);
    const blue = Hsl.of(p.ansi[4]);
    try std.testing.expect(hueDistance(blue.h, 215) < 32);
    // Not the pale blue-gray foreground.
    try std.testing.expect(blue.s > 0.35 and blue.l < 0.8);
    // Six distinct chromatic colors.
    for (1..7) |i| for (i + 1..7) |j| try std.testing.expect(!sameColor(p.ansi[i], p.ansi[j]));
    try std.testing.expect(sameColor(p.resolve(.default, true), theme_mod.mariana.foreground));
    try std.testing.expect(sameColor(p.indexed(196), rgbColor(255, 0, 0)));
    try std.testing.expect(sameColor(p.indexed(244), rgbColor(128, 128, 128)));
}
