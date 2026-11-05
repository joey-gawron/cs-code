//! Font discovery through fontconfig.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");
const Allocator = std.mem.Allocator;

pub const FontFile = struct {
    path: [:0]u8,
    index: i32,

    pub fn deinit(self: FontFile, gpa: Allocator) void {
        gpa.free(self.path);
    }
};

pub const Weight = enum { regular, bold };

var loaded = false;

/// fontconfig's current config, loaded on first use. A private one (from
/// FcInitLoadConfigAndFonts) isn't what FcDefaultSubstitute uses: it would
/// load and keep a second copy of the whole config (~750 KB, 25k blocks).
fn getConfig() !*c.FcConfig {
    if (!loaded and builtin.os.tag.isDarwin() and std.c.getenv("FONTCONFIG_FILE") == null) try installMacConfig();
    const cfg = c.FcConfigGetCurrent() orelse return error.FontconfigInitFailed;
    loaded = true;
    return cfg;
}

/// Frees the config and fontconfig's caches, at exit (no font is looked up
/// after this, or fontconfig loads it all again).
pub fn deinit() void {
    if (!loaded) return;
    c.FcFini();
    loaded = false;
}

/// macOS has no fontconfig setup of its own: point it at the system's font
/// folders and give the generic families Mac fonts.
const mac_config_format =
    \\<?xml version="1.0"?>
    \\<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
    \\<fontconfig>
    \\  <dir>/System/Library/Fonts</dir>
    \\  <dir>/Library/Fonts</dir>
    \\  <dir>~/Library/Fonts</dir>
    \\  <cachedir>~/Library/Caches/cs-zig/fontconfig</cachedir>
    \\  <dir>{s}/../share/cs-zig/fonts</dir>
    \\  <alias><family>Fira Code</family><default><family>monospace</family></default></alias>
    \\  <alias><family>monospace</family><prefer><family>Menlo</family><family>SF Mono</family><family>Monaco</family></prefer></alias>
    \\  <alias><family>sans-serif</family><prefer><family>SF Pro</family><family>Helvetica Neue</family><family>Helvetica</family></prefer></alias>
    \\  <alias><family>serif</family><prefer><family>New York</family><family>Times</family></prefer></alias>
    \\</fontconfig>
;

/// Makes `mac_config` fontconfig's current config, so it doesn't look for a
/// fonts.conf.
fn installMacConfig() !void {
    const cfg = c.FcConfigCreate() orelse return error.FontconfigInitFailed;
    // FcConfigSetCurrent takes its own reference; FcFini frees it.
    defer c.FcConfigDestroy(cfg);
    // The bundled fonts (Fira Code) are installed beside the binary: <exe dir>/../share/cs-zig/fonts.
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    var exe_len: u32 = exe_buf.len;
    var exe_dir: []const u8 = ".";
    if (std.c._NSGetExecutablePath(&exe_buf, &exe_len) == 0) {
        const exe = std.mem.sliceTo(&exe_buf, 0);
        exe_dir = std.fs.path.dirname(exe) orelse ".";
    }
    var config_buf: [4096]u8 = undefined;
    const config = std.fmt.bufPrintSentinel(&config_buf, mac_config_format, .{exe_dir}, 0) catch return error.FontconfigInitFailed;
    if (c.FcConfigParseAndLoadFromMemory(cfg, config.ptr, c.FcTrue) == c.FcFalse) return error.FontconfigInitFailed;
    if (c.FcConfigBuildFonts(cfg) == c.FcFalse) return error.FontconfigInitFailed;
    if (c.FcConfigSetCurrent(cfg) == c.FcFalse) return error.FontconfigInitFailed;
}

fn matchPattern(gpa: Allocator, pattern: *c.FcPattern) !?FontFile {
    const cfg = try getConfig();
    _ = c.FcConfigSubstitute(cfg, pattern, c.FcMatchPattern);
    c.FcDefaultSubstitute(pattern);
    var result: c.FcResult = undefined;
    const match = c.FcFontMatch(cfg, pattern, &result) orelse return null;
    defer c.FcPatternDestroy(match);
    var file: [*c]c.FcChar8 = null;
    if (c.FcPatternGetString(match, c.FC_FILE, 0, &file) != c.FcResultMatch) return null;
    var index: c_int = 0;
    _ = c.FcPatternGetInteger(match, c.FC_INDEX, 0, &index);
    return .{ .path = try gpa.dupeSentinel(u8, std.mem.span(@as([*:0]const u8, @ptrCast(file))), 0), .index = index };
}

fn basePattern(family: [:0]const u8, weight: Weight, italic: bool) !*c.FcPattern {
    const pattern = c.FcPatternCreate() orelse return error.OutOfMemory;
    _ = c.FcPatternAddString(pattern, c.FC_FAMILY, family.ptr);
    _ = c.FcPatternAddInteger(pattern, c.FC_WEIGHT, if (weight == .bold) c.FC_WEIGHT_BOLD else c.FC_WEIGHT_REGULAR);
    _ = c.FcPatternAddInteger(pattern, c.FC_SLANT, if (italic) c.FC_SLANT_ITALIC else c.FC_SLANT_ROMAN);
    return pattern;
}

/// Resolve a family name ("monospace", "JetBrains Mono", ...) to a font file.
pub fn find(gpa: Allocator, family: [:0]const u8, weight: Weight, italic: bool) !?FontFile {
    const pattern = try basePattern(family, weight, italic);
    defer c.FcPatternDestroy(pattern);
    return matchPattern(gpa, pattern);
}

/// Find a font that covers `codepoint`, preferring ones similar to `family`.
pub fn findFallback(gpa: Allocator, family: [:0]const u8, codepoint: u21) !?FontFile {
    const pattern = try basePattern(family, .regular, false);
    defer c.FcPatternDestroy(pattern);
    const charset = c.FcCharSetCreate() orelse return error.OutOfMemory;
    defer c.FcCharSetDestroy(charset);
    _ = c.FcCharSetAddChar(charset, codepoint);
    _ = c.FcPatternAddCharSet(pattern, c.FC_CHARSET, charset);
    return matchPattern(gpa, pattern);
}
