//! ~/.config/cs-zig/settings.json: Sublime-style JSON (comments and
//! trailing commas allowed) for editor and theme preferences.

const std = @import("std");
const editor_mod = @import("../editor/editor.zig");
const theme_manager = @import("theme_manager.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Value = std.json.Value;

pub const default_contents =
    \\// cs-zig settings. Changes apply when you save this file.
    \\{
    \\    // A scheme name, or "auto" to switch between the light and dark
    \\    // schemes below by time of day. Built in: Visual Studio Dark, Mariana, Breakers, Monokai,
    \\    // Dayfox, Nightfox, Gruvbox Dark, Gruvbox Light, Solarized Dark,
    \\    // Solarized Light. Any .sublime-color-scheme or .tmTheme from an
    \\    // installed Sublime Text, or dropped into ~/.config/cs-zig/schemes,
    \\    // can be named too (installed ones replace the built-in copies).
    \\    // "UI: Select Color Scheme" lists and previews them all.
    \\    "color_scheme": "Visual Studio Dark",
    \\    "light_color_scheme": "Dayfox",
    \\    "dark_color_scheme": "Visual Studio Dark",
    \\
    \\    // How "auto" decides it's daytime:
    \\    //   "sun"  - sunrise to sunset at "latitude"/"longitude". Without them
    \\    //            the location of your time zone is used (offline, from the
    \\    //            tz database). Falls back to the fixed times below.
    \\    //   "time" - always use "day_start" and "night_start".
    \\    "auto_color_scheme_by": "sun",
    \\    "day_start": "07:00",
    \\    "night_start": "19:00",
    \\    // "latitude": 37.77,
    \\    // "longitude": -122.42,
    \\
    \\    "font_face": "Fira Code",
    \\    "font_size": 13,
    \\    "ui_font_face": "sans-serif",
    \\    "ui_font_size": 13,
    \\    "line_padding_top": 1,
    \\    "line_padding_bottom": 1,
    \\    "draw_minimap": true,
    \\    "line_numbers": true,
    \\    "draw_indent_guides": true,
    \\    "draw_white_space": "selection",
    \\    "highlight_line": false,
    \\    "match_brackets": true,
    \\    "auto_match_enabled": true,
    \\    "caret_blink": true,
    \\    "scroll_past_end": true,
    \\    "vim_mode": false,
    \\
    \\    // The leader keys, as in Doom Emacs: space in vim's normal and visual
    \\    // modes, alt+m anywhere, then e.g. "f s" saves, "w v" splits. After
    \\    // this many milliseconds (0: at once) a popup lists the keys that can
    \\    // follow. keymap.json adds, renames and removes them.
    \\    "which_key_delay_ms": 400,
    \\
    \\    // Soft wrap: "auto" wraps prose (Plain Text, Markdown, Org) up to 16 MB,
    \\    // true/false force it. wrap_width 0 wraps at the window edge. View >
    \\    // Word Wrap toggles it for the current view.
    \\    "word_wrap": "auto",
    \\    "wrap_width": 0,
    \\
    \\    // Git blame of the caret line, dimmed after its end, once the caret
    \\    // rests there ("Git: Toggle Inline Blame"). "Git: Toggle Blame"
    \\    // (ctrl+k ctrl+g) shows a blame column in the gutter.
    \\    "git_blame_inline": true,
    \\
    \\    // Org: show the agenda when cs-zig starts without files, and where
    \\    // "Org: Capture…" (SPC o c or SPC X) files a TODO: under this heading
    \\    // ("" for top level) of this file, both created if missing.
    \\    "org_agenda_on_startup": false,
    \\    "org_capture_file": "~/org/inbox.org",
    \\    "org_capture_heading": "Tasks",
    \\    // Marking an item DONE stamps it "CLOSED: [date time]".
    \\    "org_log_done": true,
    \\
    \\    // More capture targets than the one above, picked from a list:
    \\    // %? is what you type, %U / %u an inactive date with / without the
    \\    // time, %T / %t active ones, %a a link to where you were, %i the
    \\    // selection. "datetree" files under year / month / day headings.
    \\    // "org_capture_templates": [
    \\    //     {"key": "t", "description": "Todo", "file": "~/org/inbox.org", "heading": "Tasks", "template": "* TODO %?\\n  %U"},
    \\    //     {"key": "j", "description": "Journal", "file": "~/org/journal.org", "template": "* %?\\n  %U", "datetree": true}
    \\    // ],
    \\
    \\    // Where the agenda looks (files and folders; default: the project's
    \\    // org files, open org files and ~/org).
    \\    // "org_agenda_files": ["~/org", "~/work/plan.org"],
    \\
    \\    // Agenda views of your own, in the Agenda Dispatcher (SPC o a o):
    \\    // "type" is "tags" (a tag match like "work+urgent-someday"),
    \\    // "todo" (keywords, "NEXT|WAITING") or "search" (words).
    \\    // "org_agenda_custom_commands": [
    \\    //     {"key": "w", "description": "Work", "type": "tags", "match": "work"}
    \\    // ],
    \\
    \\    // Per language server settings, sent when the server asks for its
    \\    // configuration. Dotted keys nest. zls gets build-on-save (type
    \\    // errors) by default, using the build.zig "check" step if there is one.
    \\    // "lsp": {
    \\    //     "zls": { "settings": { "zls.enable_build_on_save": true } },
    \\    //     "pyright": { "settings": { "python.analysis.typeCheckingMode": "strict" } }
    \\    // },
    \\
    \\    // Files bigger than this aren't syntax highlighted: tree-sitter needs
    \\    // tens of bytes of memory per byte of source. 0 = no limit.
    \\    "syntax_max_file_size_mb": 8,
    \\
    \\    // Keep running (buffers and language servers stay warm) after the
    \\    // window closes; `cs-zig` then reconnects instantly. `cs-zig --daemon`
    \\    // always does this.
    \\    "keep_running_in_background": false,
    \\}
    \\
;

pub fn path(buf: []u8) ?[]const u8 {
    if (std.c.getenv("XDG_CONFIG_HOME")) |x| {
        return std.fmt.bufPrint(buf, "{s}/cs-zig/settings.json", .{std.mem.span(x)}) catch null;
    }
    const home = std.c.getenv("HOME") orelse return null;
    return std.fmt.bufPrint(buf, "{s}/.config/cs-zig/settings.json", .{std.mem.span(home)}) catch null;
}

/// The editor was called Zublime Text before it became CS Zig. On the
/// first run under the new name, a ~/.config/zublime folder (settings, key
/// bindings, schemes) is copied to ~/.config/cs-zig; the old one is kept.
pub fn migrateLegacyConfig(gpa: Allocator, io: Io) void {
    var new_buf: [std.fs.max_path_bytes]u8 = undefined;
    var old_buf: [std.fs.max_path_bytes]u8 = undefined;
    const base: []const u8, const sub: []const u8 = if (std.c.getenv("XDG_CONFIG_HOME")) |x| .{ std.mem.span(x), "" } else .{ std.mem.span(std.c.getenv("HOME") orelse return), "/.config" };
    const new = std.fmt.bufPrint(&new_buf, "{s}{s}/cs-zig", .{ base, sub }) catch return;
    const old = std.fmt.bufPrint(&old_buf, "{s}{s}/zublime", .{ base, sub }) catch return;
    const cwd = Io.Dir.cwd();
    if (cwd.access(io, new, .{})) |_| return else |_| {}
    var src = cwd.openDir(io, old, .{ .iterate = true }) catch return;
    defer src.close(io);
    var dst = cwd.createDirPathOpen(io, new, .{}) catch return;
    defer dst.close(io);
    var walker = src.walk(gpa) catch return;
    defer walker.deinit();
    while (walker.next(io) catch null) |entry| switch (entry.kind) {
        .directory => dst.createDirPath(io, entry.path) catch {},
        .file => src.copyFile(entry.path, dst, entry.path, io, .{}) catch {},
        else => {},
    };
    std.log.info("copied settings from {s} to {s}", .{ old, new });
}

/// Creates the settings file with documented defaults if it doesn't exist.
pub fn ensureExists(io: Io) !void {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = path(&buf) orelse return error.NoHome;
    if (Io.Dir.cwd().statFile(io, p, .{})) |_| return else |_| {}
    if (std.fs.path.dirname(p)) |dir| try Io.Dir.cwd().createDirPath(io, dir);
    try Io.Dir.cwd().writeFile(io, .{ .sub_path = p, .data = default_contents });
}

/// Strips // and /* */ comments and trailing commas (outside strings).
pub fn toStrictJson(gpa: Allocator, text: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var i: usize = 0;
    var in_string = false;
    while (i < text.len) : (i += 1) {
        const ch = text[i];
        if (in_string) {
            try out.append(gpa, ch);
            if (ch == '\\' and i + 1 < text.len) {
                i += 1;
                try out.append(gpa, text[i]);
            } else if (ch == '"') in_string = false;
            continue;
        }
        if (ch == '"') {
            in_string = true;
            try out.append(gpa, ch);
        } else if (ch == '/' and i + 1 < text.len and text[i + 1] == '/') {
            while (i < text.len and text[i] != '\n') i += 1;
            try out.append(gpa, '\n');
        } else if (ch == '/' and i + 1 < text.len and text[i + 1] == '*') {
            i += 2;
            while (i + 1 < text.len and !(text[i] == '*' and text[i + 1] == '/')) i += 1;
            i += 1;
        } else if (ch == ',') {
            // Drop the comma if only whitespace separates it from } or ].
            var j = i + 1;
            while (j < text.len and std.ascii.isWhitespace(text[j])) j += 1;
            // Comments after a trailing comma.
            if (j + 1 < text.len and text[j] == '/' and (text[j + 1] == '/' or text[j + 1] == '*')) {
                try out.append(gpa, ch);
                continue;
            }
            if (j < text.len and (text[j] == '}' or text[j] == ']')) continue;
            try out.append(gpa, ch);
        } else try out.append(gpa, ch);
    }
    // A second pass catches commas whose closing bracket followed a comment.
    return removeTrailingCommas(gpa, try out.toOwnedSlice(gpa));
}

fn removeTrailingCommas(gpa: Allocator, text: []u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    defer gpa.free(text);
    var in_string = false;
    var i: usize = 0;
    while (i < text.len) : (i += 1) {
        const ch = text[i];
        if (in_string) {
            try out.append(gpa, ch);
            if (ch == '\\' and i + 1 < text.len) {
                i += 1;
                try out.append(gpa, text[i]);
            } else if (ch == '"') in_string = false;
            continue;
        }
        if (ch == '"') in_string = true;
        if (ch == ',') {
            var j = i + 1;
            while (j < text.len and std.ascii.isWhitespace(text[j])) j += 1;
            if (j < text.len and (text[j] == '}' or text[j] == ']')) continue;
        }
        try out.append(gpa, ch);
    }
    return out.toOwnedSlice(gpa);
}

fn parseClock(s: []const u8) ?f64 {
    const colon = std.mem.indexOfScalar(u8, s, ':') orelse return null;
    const h = std.fmt.parseInt(u32, std.mem.trim(u8, s[0..colon], " "), 10) catch return null;
    const m = std.fmt.parseInt(u32, std.mem.trim(u8, s[colon + 1 ..], " "), 10) catch return null;
    if (h > 24 or m > 59) return null;
    return @floatFromInt(h * 60 + m);
}

pub const Loaded = struct {
    vim_mode: ?bool = null,
    /// The "lsp" object as JSON text (in the string arena), for the LSP client.
    lsp_json: ?[]const u8 = null,
    fonts_changed: bool = false,
};

/// Reads the settings file into `editor_settings` / `theme_settings`.
/// String settings are copied into `string_arena`.
pub fn load(gpa: Allocator, io: Io, string_arena: Allocator, editor_settings: *editor_mod.Settings, theme_settings: *theme_manager.ThemeSettings) !Loaded {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = path(&buf) orelse return .{};
    const raw = Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(1 << 20)) catch |err| switch (err) {
        error.FileNotFound => return .{},
        else => return err,
    };
    defer gpa.free(raw);
    return loadText(gpa, raw, string_arena, editor_settings, theme_settings);
}

/// `load` for settings.json's text.
pub fn loadText(gpa: Allocator, raw: []const u8, string_arena: Allocator, editor_settings: *editor_mod.Settings, theme_settings: *theme_manager.ThemeSettings) !Loaded {
    const strict = try toStrictJson(gpa, raw);
    defer gpa.free(strict);
    var parsed = try std.json.parseFromSlice(Value, gpa, strict, .{});
    defer parsed.deinit();
    const root = parsed.value;
    if (root != .object) return error.InvalidSettings;
    const obj = root.object;
    var result: Loaded = .{};

    const str = struct {
        fn f(o: std.json.ObjectMap, key: []const u8) ?[]const u8 {
            const v = o.get(key) orelse return null;
            return if (v == .string) v.string else null;
        }
    }.f;
    const num = struct {
        fn f(o: std.json.ObjectMap, key: []const u8) ?f64 {
            const v = o.get(key) orelse return null;
            return switch (v) {
                .integer => |i| @floatFromInt(i),
                .float => |x| x,
                else => null,
            };
        }
    }.f;
    const boolean = struct {
        fn f(o: std.json.ObjectMap, key: []const u8) ?bool {
            const v = o.get(key) orelse return null;
            return if (v == .bool) v.bool else null;
        }
    }.f;

    if (str(obj, "color_scheme")) |s| theme_settings.color_scheme.set(s);
    if (str(obj, "light_color_scheme")) |s| theme_settings.light_color_scheme.set(s);
    if (str(obj, "dark_color_scheme")) |s| theme_settings.dark_color_scheme.set(s);
    if (str(obj, "auto_color_scheme_by")) |s| theme_settings.auto_by = if (std.mem.eql(u8, s, "time")) .time else .sun;
    if (str(obj, "day_start")) |s| if (parseClock(s)) |m| {
        theme_settings.day_start = m;
    };
    if (str(obj, "night_start")) |s| if (parseClock(s)) |m| {
        theme_settings.night_start = m;
    };
    const lat = num(obj, "latitude");
    const lon = num(obj, "longitude");
    theme_settings.location = if (lat != null and lon != null) .{ .latitude = lat.?, .longitude = lon.? } else null;

    if (str(obj, "font_face")) |s| if (!std.mem.eql(u8, s, editor_settings.font_family)) {
        editor_settings.font_family = try string_arena.dupe(u8, s);
        result.fonts_changed = true;
    };
    if (str(obj, "ui_font_face")) |s| if (!std.mem.eql(u8, s, editor_settings.ui_font_family)) {
        editor_settings.ui_font_family = try string_arena.dupe(u8, s);
        result.fonts_changed = true;
    };
    if (num(obj, "font_size")) |n| if (@as(f32, @floatCast(n)) != editor_settings.font_size) {
        editor_settings.font_size = std.math.clamp(@as(f32, @floatCast(n)), 6, 48);
        result.fonts_changed = true;
    };
    if (num(obj, "ui_font_size")) |n| if (@as(f32, @floatCast(n)) != editor_settings.ui_font_size) {
        editor_settings.ui_font_size = std.math.clamp(@as(f32, @floatCast(n)), 8, 32);
        result.fonts_changed = true;
    };
    if (num(obj, "line_padding_top")) |n| editor_settings.line_padding_top = @floatCast(n);
    if (num(obj, "line_padding_bottom")) |n| editor_settings.line_padding_bottom = @floatCast(n);
    if (boolean(obj, "draw_minimap")) |b| editor_settings.show_minimap = b;
    if (boolean(obj, "line_numbers")) |b| editor_settings.show_line_numbers = b;
    if (boolean(obj, "draw_indent_guides")) |b| editor_settings.draw_indent_guides = b;
    if (boolean(obj, "highlight_line")) |b| editor_settings.highlight_line = b;
    if (boolean(obj, "match_brackets")) |b| editor_settings.match_brackets = b;
    if (boolean(obj, "auto_match_enabled")) |b| editor_settings.auto_match_enabled = b;
    if (boolean(obj, "caret_blink")) |b| editor_settings.caret_blink = b;
    if (boolean(obj, "scroll_past_end")) |b| editor_settings.scroll_past_end = b;
    if (boolean(obj, "keep_running_in_background")) |b| editor_settings.keep_running_in_background = b;
    if (num(obj, "syntax_max_file_size_mb")) |n| editor_settings.syntax_max_file_size_mb = @intFromFloat(std.math.clamp(n, 0, 1 << 20));
    if (boolean(obj, "git_blame_inline")) |b| editor_settings.git_blame_inline = b;
    if (boolean(obj, "org_agenda_on_startup")) |b| editor_settings.org_agenda_on_startup = b;
    if (boolean(obj, "org_log_done")) |b| editor_settings.org_log_done = b;
    inline for (.{ .{ "org_capture_templates", "org_capture_templates_json" }, .{ "org_agenda_custom_commands", "org_agenda_custom_commands_json" }, .{ "org_agenda_files", "org_agenda_files_json" } }) |kv| {
        @field(editor_settings, kv[1]) = if (obj.get(kv[0])) |v| (if (v == .array) std.json.Stringify.valueAlloc(string_arena, v, .{}) catch "" else "") else "";
    }
    if (num(obj, "which_key_delay_ms")) |n| editor_settings.which_key_delay_ms = @intFromFloat(std.math.clamp(n, 0, 10_000));
    if (str(obj, "org_capture_file")) |s| if (s.len > 0) {
        editor_settings.org_capture_file = try string_arena.dupe(u8, s);
    };
    if (str(obj, "org_capture_heading")) |s| {
        editor_settings.org_capture_heading = try string_arena.dupe(u8, s);
    }
    if (obj.get("word_wrap")) |v| switch (v) {
        .bool => |b| editor_settings.word_wrap = if (b) .on else .off,
        .string => |s| editor_settings.word_wrap = if (std.mem.eql(u8, s, "true") or std.mem.eql(u8, s, "on")) .on else if (std.mem.eql(u8, s, "false") or std.mem.eql(u8, s, "off")) .off else .auto,
        else => {},
    };
    if (num(obj, "wrap_width")) |n| editor_settings.wrap_width = @intFromFloat(std.math.clamp(n, 0, 1000));
    if (str(obj, "draw_white_space")) |s| {
        editor_settings.draw_white_space = if (std.mem.eql(u8, s, "all")) .all else if (std.mem.eql(u8, s, "none")) .none else .selection;
    }
    result.vim_mode = boolean(obj, "vim_mode");
    if (obj.get("lsp")) |v| if (v == .object) {
        result.lsp_json = std.json.Stringify.valueAlloc(string_arena, v, .{}) catch null;
    };
    return result;
}

/// Where the uncommented top-level `"key": value` of `raw` sits: the start
/// of its line, the span of its value, and the end of its line (after the
/// newline). Keys inside comments or nested objects (not first on their line)
/// don't count.
const KeySpan = struct { line_start: usize, value_start: usize, value_end: usize, line_end: usize };

fn findKey(raw: []const u8, key: []const u8) ?KeySpan {
    var needle_buf: [96]u8 = undefined;
    const needle = std.fmt.bufPrint(&needle_buf, "\"{s}\"", .{key}) catch return null;
    var search: usize = 0;
    while (std.mem.indexOfPos(u8, raw, search, needle)) |pos| : (search = pos + needle.len) {
        const line_start = if (std.mem.lastIndexOfScalar(u8, raw[0..pos], '\n')) |n| n + 1 else 0;
        if (std.mem.trim(u8, raw[line_start..pos], " \t").len != 0) continue;
        var i = pos + needle.len;
        while (i < raw.len and (raw[i] == ' ' or raw[i] == '\t')) i += 1;
        if (i >= raw.len or raw[i] != ':') continue;
        i += 1;
        while (i < raw.len and (raw[i] == ' ' or raw[i] == '\t')) i += 1;
        const value_start = i;
        var depth: usize = 0;
        var in_string = false;
        while (i < raw.len) : (i += 1) {
            const ch = raw[i];
            if (in_string) {
                if (ch == '\\') i += 1 else if (ch == '"') {
                    in_string = false;
                    if (depth == 0) {
                        i += 1;
                        break;
                    }
                }
                continue;
            }
            switch (ch) {
                '"' => in_string = true,
                '{', '[' => depth += 1,
                '}', ']' => if (depth == 0) break else {
                    depth -= 1;
                    if (depth == 0) {
                        i += 1;
                        break;
                    }
                },
                ',', '\n' => if (depth == 0) break,
                '/' => if (depth == 0 and i + 1 < raw.len and raw[i + 1] == '/') break,
                else => {},
            }
        }
        var value_end = i;
        while (value_end > value_start and (raw[value_end - 1] == ' ' or raw[value_end - 1] == '\t')) value_end -= 1;
        const line_end = if (std.mem.indexOfScalarPos(u8, raw, value_end, '\n')) |n| n + 1 else raw.len;
        return .{ .line_start = line_start, .value_start = value_start, .value_end = value_end, .line_end = line_end };
    }
    return null;
}

/// `raw` with `key` set to the JSON text `literal` (comments and the other
/// lines are kept; a key not in the file is added after the opening brace),
/// or, when `literal` is null, with the key's line removed.
pub fn editText(gpa: Allocator, raw: []const u8, key: []const u8, literal: ?[]const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    if (findKey(raw, key)) |span| {
        if (literal) |lit| {
            try out.appendSlice(gpa, raw[0..span.value_start]);
            try out.appendSlice(gpa, lit);
            try out.appendSlice(gpa, raw[span.value_end..]);
        } else {
            try out.appendSlice(gpa, raw[0..span.line_start]);
            try out.appendSlice(gpa, raw[span.line_end..]);
        }
        return out.toOwnedSlice(gpa);
    }
    const lit = literal orelse return gpa.dupe(u8, raw);
    const brace = std.mem.indexOfScalar(u8, raw, '{') orelse return error.InvalidSettings;
    try out.appendSlice(gpa, raw[0 .. brace + 1]);
    try out.print(gpa, "\n    \"{s}\": {s},", .{ key, lit });
    try out.appendSlice(gpa, raw[brace + 1 ..]);
    return out.toOwnedSlice(gpa);
}

fn writeEdited(gpa: Allocator, io: Io, key: []const u8, literal: ?[]const u8) !void {
    try ensureExists(io);
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = path(&buf) orelse return error.NoHome;
    const raw = try Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(1 << 20));
    defer gpa.free(raw);
    const edited = try editText(gpa, raw, key, literal);
    defer gpa.free(edited);
    try Io.Dir.cwd().writeFile(io, .{ .sub_path = p, .data = edited });
}

/// Sets `"key": value` (a JSON literal such as `true`, `13` or `"Dayfox"`)
/// in the settings file, keeping its comments.
pub fn setRaw(gpa: Allocator, io: Io, key: []const u8, literal: []const u8) !void {
    return writeEdited(gpa, io, key, literal);
}

/// Removes `key` from the settings file, so the default applies again.
pub fn unset(gpa: Allocator, io: Io, key: []const u8) !void {
    return writeEdited(gpa, io, key, null);
}

/// Sets `"key": "value"` in the settings file, keeping its comments.
pub fn setString(gpa: Allocator, io: Io, key: []const u8, value: []const u8) !void {
    const literal = try std.json.Stringify.valueAlloc(gpa, value, .{});
    defer gpa.free(literal);
    return setRaw(gpa, io, key, literal);
}

/// The settings file as a JSON object, or null when there is none yet.
pub fn readObject(gpa: Allocator, io: Io) !?std.json.Parsed(Value) {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = path(&buf) orelse return null;
    const raw = Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(1 << 20)) catch |err| switch (err) {
        error.FileNotFound => return null,
        else => return err,
    };
    defer gpa.free(raw);
    const strict = try toStrictJson(gpa, raw);
    defer gpa.free(strict);
    var parsed = try std.json.parseFromSlice(Value, gpa, strict, .{});
    errdefer parsed.deinit();
    if (parsed.value != .object) return error.InvalidSettings;
    return parsed;
}

test "lenient json" {
    const gpa = std.testing.allocator;
    const strict = try toStrictJson(gpa,
        \\{
        \\  // comment
        \\  "a": "x // not a comment", /* block */
        \\  "b": [1, 2,],
        \\}
    );
    defer gpa.free(strict);
    var parsed = try std.json.parseFromSlice(Value, gpa, strict, .{});
    defer parsed.deinit();
    try std.testing.expectEqualStrings("x // not a comment", parsed.value.object.get("a").?.string);
    try std.testing.expectEqual(@as(usize, 2), parsed.value.object.get("b").?.array.items.len);
}

test "default settings parse" {
    const gpa = std.testing.allocator;
    const strict = try toStrictJson(gpa, default_contents);
    defer gpa.free(strict);
    var parsed = try std.json.parseFromSlice(Value, gpa, strict, .{});
    defer parsed.deinit();
    try std.testing.expectEqualStrings("Visual Studio Dark", parsed.value.object.get("color_scheme").?.string);
}

fn expectEdit(raw: []const u8, key: []const u8, literal: ?[]const u8, want: []const u8) !void {
    const got = try editText(std.testing.allocator, raw, key, literal);
    defer std.testing.allocator.free(got);
    try std.testing.expectEqualStrings(want, got);
}

test "editing a value keeps comments and neighbours" {
    const raw =
        \\{
        \\    // the size
        \\    "font_size": 13, // pixels
        \\    "font_face": "Fira Code",
        \\    // "line_numbers": false,
        \\    "lsp": { "zls": { "a": [1, 2] }, "b": 1 },
        \\    "vim_mode": false
        \\}
    ;
    try expectEdit(raw, "font_size", "15",
        \\{
        \\    // the size
        \\    "font_size": 15, // pixels
        \\    "font_face": "Fira Code",
        \\    // "line_numbers": false,
        \\    "lsp": { "zls": { "a": [1, 2] }, "b": 1 },
        \\    "vim_mode": false
        \\}
    );
    try expectEdit(raw, "font_face", "\"Menlo, \\\"x\\\"\"",
        \\{
        \\    // the size
        \\    "font_size": 13, // pixels
        \\    "font_face": "Menlo, \"x\"",
        \\    // "line_numbers": false,
        \\    "lsp": { "zls": { "a": [1, 2] }, "b": 1 },
        \\    "vim_mode": false
        \\}
    );
    try expectEdit(raw, "vim_mode", "true",
        \\{
        \\    // the size
        \\    "font_size": 13, // pixels
        \\    "font_face": "Fira Code",
        \\    // "line_numbers": false,
        \\    "lsp": { "zls": { "a": [1, 2] }, "b": 1 },
        \\    "vim_mode": true
        \\}
    );
    // Whole nested values are replaced, commented-out keys are not touched.
    try expectEdit(raw, "lsp", "null",
        \\{
        \\    // the size
        \\    "font_size": 13, // pixels
        \\    "font_face": "Fira Code",
        \\    // "line_numbers": false,
        \\    "lsp": null,
        \\    "vim_mode": false
        \\}
    );
}

test "removing and adding keys" {
    const raw =
        \\{
        \\    "font_size": 13, // pixels
        \\    // "line_numbers": false,
        \\    "vim_mode": false
        \\}
    ;
    try expectEdit(raw, "font_size", null,
        \\{
        \\    // "line_numbers": false,
        \\    "vim_mode": false
        \\}
    );
    try expectEdit(raw, "line_numbers", "false",
        \\{
        \\    "line_numbers": false,
        \\    "font_size": 13, // pixels
        \\    // "line_numbers": false,
        \\    "vim_mode": false
        \\}
    );
    try expectEdit(raw, "absent", null, raw);
}

test "every edit leaves valid settings that load back" {
    const gpa = std.testing.allocator;
    var text = try gpa.dupe(u8, default_contents);
    defer gpa.free(text);
    const edits = [_]struct { []const u8, ?[]const u8 }{
        .{ "font_size", "17" },
        .{ "font_face", "\"Menlo\"" },
        .{ "draw_minimap", "false" },
        .{ "word_wrap", "true" },
        .{ "latitude", "37.5" },
        .{ "font_size", null },
        .{ "color_scheme", "\"Solarized Light\"" },
    };
    for (edits) |e| {
        const next = try editText(gpa, text, e[0], e[1]);
        gpa.free(text);
        text = next;
        const strict = try toStrictJson(gpa, text);
        defer gpa.free(strict);
        var parsed = try std.json.parseFromSlice(Value, gpa, strict, .{});
        defer parsed.deinit();
        if (e[1]) |lit| {
            const want = try std.json.parseFromSlice(Value, gpa, lit, .{});
            defer want.deinit();
            try std.testing.expect(parsed.value.object.get(e[0]) != null);
        } else try std.testing.expect(parsed.value.object.get(e[0]) == null);
    }
    const strict_final = try toStrictJson(gpa, text);
    defer gpa.free(strict_final);
    var parsed = try std.json.parseFromSlice(Value, gpa, strict_final, .{});
    defer parsed.deinit();
    try std.testing.expectEqualStrings("Solarized Light", parsed.value.object.get("color_scheme").?.string);
    try std.testing.expectEqualStrings("Menlo", parsed.value.object.get("font_face").?.string);
    try std.testing.expect(parsed.value.object.get("draw_minimap").?.bool == false);
}

test "settings written like the Settings page does load back" {
    const gpa = std.testing.allocator;
    var arena: std.heap.ArenaAllocator = .init(gpa);
    defer arena.deinit();

    var text = try gpa.dupe(u8, default_contents);
    defer gpa.free(text);
    const writes = [_]struct { []const u8, []const u8 }{
        .{ "font_size", "17" },
        .{ "font_face", "\"Menlo\"" },
        .{ "draw_minimap", "false" },
        .{ "line_numbers", "false" },
        .{ "draw_white_space", "\"all\"" },
        .{ "word_wrap", "true" },
        .{ "wrap_width", "80" },
        .{ "caret_blink", "false" },
        .{ "vim_mode", "true" },
        .{ "which_key_delay_ms", "0" },
        .{ "color_scheme", "\"Solarized Light\"" },
        .{ "auto_color_scheme_by", "\"time\"" },
        .{ "day_start", "\"06:30\"" },
        .{ "org_capture_heading", "\"Inbox\"" },
    };
    for (writes) |w| {
        const next = try editText(gpa, text, w[0], w[1]);
        gpa.free(text);
        text = next;
    }
    var settings: editor_mod.Settings = .{};
    var themes: theme_manager.ThemeSettings = .{};
    const loaded = try loadText(gpa, text, arena.allocator(), &settings, &themes);
    try std.testing.expectEqual(@as(f32, 17), settings.font_size);
    try std.testing.expectEqualStrings("Menlo", settings.font_family);
    try std.testing.expect(loaded.fonts_changed);
    try std.testing.expect(!settings.show_minimap);
    try std.testing.expect(!settings.show_line_numbers);
    try std.testing.expect(settings.draw_white_space == .all);
    try std.testing.expect(settings.word_wrap == .on);
    try std.testing.expectEqual(@as(u32, 80), settings.wrap_width);
    try std.testing.expect(!settings.caret_blink);
    try std.testing.expectEqual(@as(?bool, true), loaded.vim_mode);
    try std.testing.expectEqual(@as(u32, 0), settings.which_key_delay_ms);
    try std.testing.expectEqualStrings("Solarized Light", themes.color_scheme.get());
    try std.testing.expect(themes.auto_by == .time);
    try std.testing.expectEqual(@as(f64, 6 * 60 + 30), themes.day_start);
    try std.testing.expectEqualStrings("Inbox", settings.org_capture_heading);

    // Resetting every one of them gives the defaults back.
    for (writes) |w| {
        const next = try editText(gpa, text, w[0], null);
        gpa.free(text);
        text = next;
    }
    var again: editor_mod.Settings = .{};
    _ = try loadText(gpa, text, arena.allocator(), &again, &themes);
    try std.testing.expectEqual(@as(f32, 13), again.font_size);
    try std.testing.expect(again.show_minimap);
}
