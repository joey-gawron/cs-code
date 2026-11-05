//! The settings the Settings page lists: key (as in settings.json), category,
//! title, description, kind of control and default. Only keys that
//! settings_file.load reads belong here.

const std = @import("std");

const Value = std.json.Value;

pub const Choice = struct {
    label: []const u8,
    /// What goes into settings.json for this choice, as JSON text.
    json: []const u8,
};

pub const Kind = union(enum) {
    boolean,
    integer: struct { min: i64, max: i64 },
    number: struct { min: f64, max: f64 },
    text,
    choice: []const Choice,
    /// A color scheme: the list of schemes, grouped dark and light.
    scheme: struct { allow_auto: bool },
};

pub const Category = enum {
    editor,
    workbench,
    application,
    git,
    org,

    pub fn title(c: Category) []const u8 {
        return switch (c) {
            .editor => "Editor",
            .workbench => "Workbench",
            .application => "Application",
            .git => "Git",
            .org => "Org",
        };
    }
};

pub const Def = struct {
    key: []const u8,
    category: Category,
    title: []const u8,
    description: []const u8,
    kind: Kind,
    /// The value when settings.json doesn't have the key, as JSON text.
    default: []const u8,
};

const white_space_choices = [_]Choice{
    .{ .label = "none", .json = "\"none\"" },
    .{ .label = "selection", .json = "\"selection\"" },
    .{ .label = "all", .json = "\"all\"" },
};

const word_wrap_choices = [_]Choice{
    .{ .label = "auto", .json = "\"auto\"" },
    .{ .label = "on", .json = "true" },
    .{ .label = "off", .json = "false" },
};

const auto_by_choices = [_]Choice{
    .{ .label = "sun", .json = "\"sun\"" },
    .{ .label = "time", .json = "\"time\"" },
};

pub const defs = [_]Def{
    .{ .key = "font_face", .category = .editor, .title = "Font Family", .description = "Controls the font family of the editor.", .kind = .text, .default = "\"Fira Code\"" },
    .{ .key = "font_size", .category = .editor, .title = "Font Size", .description = "Controls the font size in pixels.", .kind = .{ .integer = .{ .min = 6, .max = 48 } }, .default = "13" },
    .{ .key = "line_padding_top", .category = .editor, .title = "Line Padding Top", .description = "Extra space above each line, in pixels.", .kind = .{ .number = .{ .min = 0, .max = 20 } }, .default = "1" },
    .{ .key = "line_padding_bottom", .category = .editor, .title = "Line Padding Bottom", .description = "Extra space below each line, in pixels.", .kind = .{ .number = .{ .min = 0, .max = 20 } }, .default = "1" },
    .{ .key = "draw_minimap", .category = .editor, .title = "Minimap: Enabled", .description = "Controls whether the minimap is shown.", .kind = .boolean, .default = "true" },
    .{ .key = "line_numbers", .category = .editor, .title = "Line Numbers", .description = "Controls whether line numbers are shown in the gutter.", .kind = .boolean, .default = "true" },
    .{ .key = "draw_indent_guides", .category = .editor, .title = "Indent Guides", .description = "Controls whether vertical indentation guides are drawn.", .kind = .boolean, .default = "true" },
    .{ .key = "draw_white_space", .category = .editor, .title = "Render Whitespace", .description = "Controls how whitespace characters are drawn.", .kind = .{ .choice = &white_space_choices }, .default = "\"selection\"" },
    .{ .key = "highlight_line", .category = .editor, .title = "Highlight Active Line", .description = "Highlights the line the caret is on.", .kind = .boolean, .default = "false" },
    .{ .key = "match_brackets", .category = .editor, .title = "Match Brackets", .description = "Highlights the bracket matching the one at the caret.", .kind = .boolean, .default = "true" },
    .{ .key = "auto_match_enabled", .category = .editor, .title = "Auto Closing Brackets", .description = "Inserts the closing bracket or quote when the opening one is typed.", .kind = .boolean, .default = "true" },
    .{ .key = "caret_blink", .category = .editor, .title = "Cursor Blinking", .description = "Controls whether the caret blinks.", .kind = .boolean, .default = "true" },
    .{ .key = "scroll_past_end", .category = .editor, .title = "Scroll Beyond Last Line", .description = "Allows scrolling past the last line.", .kind = .boolean, .default = "true" },
    .{ .key = "word_wrap", .category = .editor, .title = "Word Wrap", .description = "Controls how lines wrap: auto wraps prose (Plain Text, Markdown, Org).", .kind = .{ .choice = &word_wrap_choices }, .default = "\"auto\"" },
    .{ .key = "wrap_width", .category = .editor, .title = "Word Wrap Column", .description = "The column lines wrap at; 0 wraps at the window edge.", .kind = .{ .integer = .{ .min = 0, .max = 1000 } }, .default = "0" },

    .{ .key = "color_scheme", .category = .workbench, .title = "Color Theme", .description = "Specifies the color theme used in the workbench. Arrow through the list to preview; Enter applies.", .kind = .{ .scheme = .{ .allow_auto = true } }, .default = "\"Visual Studio Dark\"" },
    .{ .key = "light_color_scheme", .category = .workbench, .title = "Preferred Light Color Theme", .description = "The theme \"auto\" uses by day.", .kind = .{ .scheme = .{ .allow_auto = false } }, .default = "\"Dayfox\"" },
    .{ .key = "dark_color_scheme", .category = .workbench, .title = "Preferred Dark Color Theme", .description = "The theme \"auto\" uses by night.", .kind = .{ .scheme = .{ .allow_auto = false } }, .default = "\"Visual Studio Dark\"" },
    .{ .key = "auto_color_scheme_by", .category = .workbench, .title = "Auto Color Theme By", .description = "How \"auto\" decides it's daytime: sunrise to sunset, or the fixed times below.", .kind = .{ .choice = &auto_by_choices }, .default = "\"sun\"" },
    .{ .key = "day_start", .category = .workbench, .title = "Day Starts At", .description = "When the light theme starts, as HH:MM (used with the time option).", .kind = .text, .default = "\"07:00\"" },
    .{ .key = "night_start", .category = .workbench, .title = "Night Starts At", .description = "When the dark theme starts, as HH:MM (used with the time option).", .kind = .text, .default = "\"19:00\"" },
    .{ .key = "ui_font_face", .category = .workbench, .title = "Font Family", .description = "Controls the font family of menus, tabs and panels.", .kind = .text, .default = "\"sans-serif\"" },
    .{ .key = "ui_font_size", .category = .workbench, .title = "Font Size", .description = "Controls the font size of menus, tabs and panels, in pixels.", .kind = .{ .integer = .{ .min = 8, .max = 32 } }, .default = "13" },

    .{ .key = "vim_mode", .category = .application, .title = "Vim Mode", .description = "Edit with Vim key bindings.", .kind = .boolean, .default = "false" },
    .{ .key = "keep_running_in_background", .category = .application, .title = "Keep Running In Background", .description = "Keep buffers and language servers alive after the window closes, so the next start is instant.", .kind = .boolean, .default = "false" },
    .{ .key = "syntax_max_file_size_mb", .category = .application, .title = "Syntax Highlighting Size Limit (MB)", .description = "Files bigger than this aren't syntax highlighted; 0 means no limit.", .kind = .{ .integer = .{ .min = 0, .max = 1 << 20 } }, .default = "8" },
    .{ .key = "which_key_delay_ms", .category = .application, .title = "Leader Key Popup Delay (ms)", .description = "How long a leader key sequence waits before the popup lists the keys that can follow; 0 shows it at once.", .kind = .{ .integer = .{ .min = 0, .max = 10000 } }, .default = "400" },

    .{ .key = "git_blame_inline", .category = .git, .title = "Blame: Inline", .description = "Shows who last changed the caret line, dimmed after its end.", .kind = .boolean, .default = "true" },

    .{ .key = "org_agenda_on_startup", .category = .org, .title = "Agenda: Show On Startup", .description = "Shows the agenda when cs-zig starts without files.", .kind = .boolean, .default = "false" },
    .{ .key = "org_capture_file", .category = .org, .title = "Capture: File", .description = "The file Org capture files new TODOs in.", .kind = .text, .default = "\"~/org/inbox.org\"" },
    .{ .key = "org_capture_heading", .category = .org, .title = "Capture: Heading", .description = "The heading captured TODOs go under; empty for the top level.", .kind = .text, .default = "\"Tasks\"" },
    .{ .key = "org_log_done", .category = .org, .title = "Log Done", .description = "Marking an item DONE stamps it with CLOSED: [date time].", .kind = .boolean, .default = "true" },
};

pub fn count(category: ?Category) usize {
    var n: usize = 0;
    for (defs) |d| {
        if (category == null or d.category == category.?) n += 1;
    }
    return n;
}

/// "Editor: Font Family" — the heading of a setting.
pub fn heading(d: Def, buf: []u8) []const u8 {
    return std.fmt.bufPrint(buf, "{s}: {s}", .{ d.category.title(), d.title }) catch d.title;
}

fn asF64(v: Value) ?f64 {
    return switch (v) {
        .integer => |i| @floatFromInt(i),
        .float => |f| f,
        else => null,
    };
}

/// Whether `value` (from settings.json) is the same setting as the default.
pub fn isDefault(d: Def, value: Value) bool {
    var arena_buf: [512]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&arena_buf);
    const default = std.json.parseFromSliceLeaky(Value, fba.allocator(), d.default, .{}) catch return false;
    switch (default) {
        .bool => |b| return value == .bool and value.bool == b,
        .integer, .float => {
            const a = asF64(value) orelse return false;
            return a == asF64(default).?;
        },
        .string => |s| return value == .string and std.mem.eql(u8, value.string, s),
        else => return false,
    }
}

/// The choice `value` stands for (word_wrap takes true, false or a string),
/// or null if it matches none.
pub fn choiceIndex(choices: []const Choice, value: Value) ?usize {
    for (choices, 0..) |c, i| {
        var arena_buf: [1024]u8 = undefined;
        var fba = std.heap.FixedBufferAllocator.init(&arena_buf);
        const lit = std.json.parseFromSliceLeaky(Value, fba.allocator(), c.json, .{}) catch continue;
        const same = switch (lit) {
            .bool => |b| value == .bool and value.bool == b,
            .string => |s| value == .string and std.mem.eql(u8, value.string, s),
            else => false,
        };
        if (same) return i;
    }
    return null;
}

/// Text for a number the way the field shows it: 13, not 13.0.
pub fn formatNumber(buf: []u8, n: f64) []const u8 {
    if (n == @floor(n) and @abs(n) < 1e15) return std.fmt.bufPrint(buf, "{d}", .{@as(i64, @intFromFloat(n))}) catch "";
    return std.fmt.bufPrint(buf, "{d}", .{n}) catch "";
}

/// What a text field shows for `value`, or for the default when null.
pub fn displayText(d: Def, value: ?Value, buf: []u8) []const u8 {
    if (value) |v| switch (v) {
        .string => |s| return s,
        .integer, .float => return formatNumber(buf, asF64(v).?),
        else => {},
    };
    var arena_buf: [512]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&arena_buf);
    const default = std.json.parseFromSliceLeaky(Value, fba.allocator(), d.default, .{}) catch return "";
    return switch (default) {
        .string => |s| std.fmt.bufPrint(buf, "{s}", .{s}) catch "",
        .integer, .float => formatNumber(buf, asF64(default).?),
        else => "",
    };
}

/// The JSON literal for text typed into a field of kind `d.kind`, or null if
/// it isn't valid (not a number, or outside the allowed range).
pub fn literalFor(gpa: std.mem.Allocator, d: Def, text: []const u8) !?[]u8 {
    const trimmed = std.mem.trim(u8, text, " \t");
    switch (d.kind) {
        .integer => |r| {
            const n = std.fmt.parseInt(i64, trimmed, 10) catch return null;
            if (n < r.min or n > r.max) return null;
            return try std.fmt.allocPrint(gpa, "{d}", .{n});
        },
        .number => |r| {
            const n = std.fmt.parseFloat(f64, trimmed) catch return null;
            if (!std.math.isFinite(n) or n < r.min or n > r.max) return null;
            var buf: [64]u8 = undefined;
            return try gpa.dupe(u8, formatNumber(&buf, n));
        },
        .text => return try std.json.Stringify.valueAlloc(gpa, text, .{}),
        else => return null,
    }
}

test "every setting has a default of its kind" {
    for (defs) |d| {
        var arena_buf: [512]u8 = undefined;
        var fba = std.heap.FixedBufferAllocator.init(&arena_buf);
        const v = try std.json.parseFromSliceLeaky(Value, fba.allocator(), d.default, .{});
        try std.testing.expect(isDefault(d, v));
        switch (d.kind) {
            .boolean => try std.testing.expect(v == .bool),
            .integer, .number => try std.testing.expect(asF64(v) != null),
            .text, .scheme => try std.testing.expect(v == .string),
            .choice => |cs| try std.testing.expect(choiceIndex(cs, v) != null),
        }
    }
}

test "keys are unique and read by the settings loader" {
    const source = @embedFile("settings_file.zig");
    const loader = source[std.mem.indexOf(u8, source, "pub fn load(").?..std.mem.indexOf(u8, source, "pub fn setRaw").?];
    for (defs, 0..) |d, i| {
        for (defs[0..i]) |e| try std.testing.expect(!std.mem.eql(u8, d.key, e.key));
        var needle: [100]u8 = undefined;
        const quoted = try std.fmt.bufPrint(&needle, "\"{s}\"", .{d.key});
        try std.testing.expect(std.mem.indexOf(u8, loader, quoted) != null);
    }
}

test "field text becomes a literal, or is refused" {
    const gpa = std.testing.allocator;
    const size = for (defs) |d| {
        if (std.mem.eql(u8, d.key, "font_size")) break d;
    } else unreachable;
    const lit = (try literalFor(gpa, size, " 15 ")).?;
    defer gpa.free(lit);
    try std.testing.expectEqualStrings("15", lit);
    try std.testing.expect(try literalFor(gpa, size, "3") == null);
    try std.testing.expect(try literalFor(gpa, size, "abc") == null);
    const face = for (defs) |d| {
        if (std.mem.eql(u8, d.key, "font_face")) break d;
    } else unreachable;
    const quoted = (try literalFor(gpa, face, "Menlo \"Bold\"")).?;
    defer gpa.free(quoted);
    try std.testing.expectEqualStrings("\"Menlo \\\"Bold\\\"\"", quoted);
}

test "the documented default file agrees with the catalog's defaults" {
    const gpa = std.testing.allocator;
    const settings_file = @import("settings_file.zig");
    const strict = try settings_file.toStrictJson(gpa, settings_file.default_contents);
    defer gpa.free(strict);
    var parsed = try std.json.parseFromSlice(Value, gpa, strict, .{});
    defer parsed.deinit();
    for (defs) |d| {
        const v = parsed.value.object.get(d.key) orelse continue;
        if (!isDefault(d, v)) std.debug.print("default of {s} differs from settings.json's\n", .{d.key});
        try std.testing.expect(isDefault(d, v));
    }
}
