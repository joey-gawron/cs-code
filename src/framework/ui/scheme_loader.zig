//! Loads Sublime Text color schemes at runtime and maps them onto
//! `theme.Scheme`: `.sublime-color-scheme` files (JSON with comments and
//! trailing commas) and legacy `.tmTheme` property lists, read from the
//! user's own Sublime Text installation (straight out of its
//! `.sublime-package` zips) and from scheme folders.
//!
//! Nothing from Sublime's packages is bundled with the editor: schemes are
//! only read from the user's disk when the editor runs.

const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const Io = std.Io;
const theme = @import("theme.zig");
const Color = @import("../gpu/scene.zig").Color;

const log = std.log.scoped(.scheme_loader);

/// Scheme files larger than this are not color schemes worth loading.
const max_scheme_size = 4 << 20;

pub const LoadedScheme = struct {
    /// `scheme.name` points into `storage`.
    scheme: theme.Scheme,
    /// Where the colors came from: a file path, or
    /// "<package path>:/<entry>" for a scheme inside a .sublime-package.
    /// Empty for schemes parsed from memory.
    source: []const u8,
    /// Backing memory for `scheme.name` and `source`.
    storage: []u8,

    fn init(gpa: Allocator, scheme: theme.Scheme, name: []const u8, source: []const u8) error{OutOfMemory}!LoadedScheme {
        const storage = try gpa.alloc(u8, name.len + source.len);
        @memcpy(storage[0..name.len], name);
        @memcpy(storage[name.len..], source);
        var s = scheme;
        s.name = storage[0..name.len];
        return .{ .scheme = s, .source = storage[name.len..], .storage = storage };
    }

    pub fn deinit(self: *LoadedScheme, gpa: Allocator) void {
        gpa.free(self.storage);
        self.* = undefined;
    }
};

/// Frees every scheme in `list` and the list itself.
pub fn deinitList(gpa: Allocator, list: *std.ArrayList(LoadedScheme)) void {
    for (list.items) |*s| s.deinit(gpa);
    list.deinit(gpa);
}

/// Case-insensitive lookup by scheme name.
pub fn find(schemes: []const LoadedScheme, name: []const u8) ?*const LoadedScheme {
    return &schemes[findIndex(schemes, name) orelse return null];
}

fn findIndex(schemes: []const LoadedScheme, name: []const u8) ?usize {
    for (schemes, 0..) |s, i| if (std.ascii.eqlIgnoreCase(s.scheme.name, name)) return i;
    return null;
}

pub const ParseError = error{ OutOfMemory, InvalidColorScheme, MissingBackground };

/// Parses a .sublime-color-scheme (JSON; // and /* */ comments and trailing
/// commas allowed). `name` names the scheme (normally the file stem); the
/// file's own "name" field is only used when `name` is empty.
pub fn parseColorScheme(gpa: Allocator, text: []const u8, name: []const u8) ParseError!LoadedScheme {
    var model: Model = .init(gpa);
    defer model.deinit();
    try model.applyJson(text);
    return model.finish(gpa, name, "");
}

/// Parses a legacy .tmTheme (XML property list). Naming as for `parseColorScheme`.
pub fn parseTmTheme(gpa: Allocator, text: []const u8, name: []const u8) ParseError!LoadedScheme {
    var model: Model = .init(gpa);
    defer model.deinit();
    try model.applyPlist(text);
    return model.finish(gpa, name, "");
}

// ---- Discovery ------------------------------------------------------------------

pub const SearchPath = struct {
    path: []const u8,
    kind: Kind,

    pub const Kind = enum {
        /// `*.sublime-package` zips directly inside `path`.
        packages,
        /// Unpacked packages: `path/*/<scheme files>`, with "User" last.
        unpacked,
        /// Scheme files directly inside `path`.
        schemes,
    };
};

/// Where `discover` looks, lowest precedence first. Strings are allocated
/// with `arena`.
pub fn defaultSearchPaths(arena: Allocator) error{OutOfMemory}![]SearchPath {
    var list: std.ArrayList(SearchPath) = .empty;
    try list.append(arena, .{ .path = "/usr/lib/sublime-text/Packages", .kind = .packages });
    try list.append(arena, .{ .path = "/opt/sublime_text/Packages", .kind = .packages });
    if (builtin.os.tag.isDarwin()) {
        try list.append(arena, .{ .path = "/Applications/Sublime Text.app/Contents/MacOS/Packages", .kind = .packages });
        if (std.c.getenv("HOME")) |home| {
            const support = try std.fmt.allocPrint(arena, "{s}/Library/Application Support/Sublime Text", .{std.mem.span(home)});
            try list.append(arena, .{ .path = try join(arena, support, "Installed Packages"), .kind = .packages });
            try list.append(arena, .{ .path = try join(arena, support, "Packages"), .kind = .unpacked });
        }
    }
    const config: []const u8 = if (std.c.getenv("XDG_CONFIG_HOME")) |x|
        try arena.dupe(u8, std.mem.span(x))
    else if (std.c.getenv("HOME")) |home|
        try std.fmt.allocPrint(arena, "{s}/.config", .{std.mem.span(home)})
    else
        return list.items;
    try list.append(arena, .{ .path = try join(arena, config, "sublime-text/Installed Packages"), .kind = .packages });
    try list.append(arena, .{ .path = try join(arena, config, "sublime-text/Packages"), .kind = .unpacked });
    try list.append(arena, .{ .path = try join(arena, config, "cs-zig/schemes"), .kind = .schemes });
    return list.items;
}

/// Finds the schemes the user has: Sublime's bundled packages
/// (/usr/lib/sublime-text/Packages, /opt/sublime_text/Packages), its
/// Installed Packages and unpacked Packages/*/ folders, then
/// ~/.config/cs-zig/schemes/. Both *.sublime-color-scheme and *.tmTheme are
/// read; unreadable or broken files are skipped. The result is sorted by
/// name; free it with `deinitList`.
pub fn discover(gpa: Allocator, io: Io) error{OutOfMemory}!std.ArrayList(LoadedScheme) {
    var arena_state: std.heap.ArenaAllocator = .init(gpa);
    defer arena_state.deinit();
    return discoverIn(gpa, io, try defaultSearchPaths(arena_state.allocator()));
}

/// `discover` over explicit locations, lowest precedence first.
///
/// Like Sublime, a file in a later source with the same package-relative
/// path replaces an earlier one (an unpacked override, or an Installed
/// Package shadowing a bundled one), and .sublime-color-scheme files with the
/// same name are merged in order: later variables and globals win, later
/// rules are appended (and so win ties). That is how packages such as LSP
/// add rules to Mariana without replacing it. A later scheme of another
/// format with the same name replaces the earlier one.
pub fn discoverIn(gpa: Allocator, io: Io, paths: []const SearchPath) error{OutOfMemory}!std.ArrayList(LoadedScheme) {
    var arena_state: std.heap.ArenaAllocator = .init(gpa);
    defer arena_state.deinit();
    var collector: Collector = .{ .arena = arena_state.allocator(), .io = io };
    for (paths) |sp| switch (sp.kind) {
        .packages => try collector.scanPackages(sp.path),
        .unpacked => try collector.scanUnpacked(sp.path),
        .schemes => try collector.scanFlat(sp.path, sp.path),
    };
    return collector.build(gpa);
}

const Format = enum { json, tmtheme };

const SchemeFile = struct {
    stem: []const u8,
    format: Format,
    /// Package-relative identity, for overrides.
    key: []const u8,
    origin: []const u8,
    text: []const u8,
};

fn classify(path: []const u8) ?struct { stem: []const u8, format: Format } {
    const base = std.fs.path.basename(path);
    const suffixes = [_]struct { []const u8, Format }{
        .{ ".sublime-color-scheme", .json },
        .{ ".tmTheme", .tmtheme },
    };
    for (suffixes) |s| {
        if (std.mem.endsWith(u8, base, s[0])) return .{ .stem = base[0 .. base.len - s[0].len], .format = s[1] };
    }
    return null;
}

fn join(arena: Allocator, a: []const u8, b: []const u8) error{OutOfMemory}![]const u8 {
    return std.fmt.allocPrint(arena, "{s}/{s}", .{ a, b });
}

fn lessName(_: void, a: []const u8, b: []const u8) bool {
    return std.mem.lessThan(u8, a, b);
}

const Collector = struct {
    arena: Allocator,
    io: Io,
    files: std.ArrayList(SchemeFile) = .empty,

    fn add(c: *Collector, file: SchemeFile) error{OutOfMemory}!void {
        for (c.files.items) |*f| if (std.mem.eql(u8, f.key, file.key)) {
            f.* = file;
            return;
        };
        try c.files.append(c.arena, file);
    }

    /// Sorted names of the files (or directories) in `path`; empty if it
    /// can't be read.
    fn list(c: *Collector, path: []const u8, want: Io.File.Kind) error{OutOfMemory}![]const []const u8 {
        var dir = Io.Dir.cwd().openDir(c.io, path, .{ .iterate = true }) catch return &.{};
        defer dir.close(c.io);
        var names: std.ArrayList([]const u8) = .empty;
        var it = dir.iterate();
        while (it.next(c.io) catch null) |entry| {
            if (entry.name.len == 0 or entry.name[0] == '.') continue;
            // Symlinks may point either way; opening them sorts it out.
            if (entry.kind != want and entry.kind != .sym_link and entry.kind != .unknown) continue;
            try names.append(c.arena, try c.arena.dupe(u8, entry.name));
        }
        std.mem.sort([]const u8, names.items, {}, lessName);
        return names.items;
    }

    fn scanPackages(c: *Collector, dir_path: []const u8) error{OutOfMemory}!void {
        for (try c.list(dir_path, .file)) |name| {
            const suffix = ".sublime-package";
            if (!std.mem.endsWith(u8, name, suffix)) continue;
            const path = try join(c.arena, dir_path, name);
            c.scanZip(path, name[0 .. name.len - suffix.len]) catch |err| switch (err) {
                error.OutOfMemory => return error.OutOfMemory,
                else => log.debug("skipping {s}: {t}", .{ path, err }),
            };
        }
    }

    fn scanUnpacked(c: *Collector, dir_path: []const u8) error{OutOfMemory}!void {
        const names = try c.list(dir_path, .directory);
        // Packages/User has the final say.
        var user: ?[]const u8 = null;
        for (names) |name| {
            if (std.mem.eql(u8, name, "User")) {
                user = name;
                continue;
            }
            try c.scanFlat(try join(c.arena, dir_path, name), name);
        }
        if (user) |name| try c.scanFlat(try join(c.arena, dir_path, name), name);
    }

    /// Scheme files directly inside `dir_path`; `key_prefix` identifies the
    /// package they override.
    fn scanFlat(c: *Collector, dir_path: []const u8, key_prefix: []const u8) error{OutOfMemory}!void {
        for (try c.list(dir_path, .file)) |name| {
            const kind = classify(name) orelse continue;
            const path = try join(c.arena, dir_path, name);
            const text = Io.Dir.cwd().readFileAlloc(c.io, path, c.arena, .limited(max_scheme_size)) catch |err| switch (err) {
                error.OutOfMemory => return error.OutOfMemory,
                else => {
                    log.debug("skipping {s}: {t}", .{ path, err });
                    continue;
                },
            };
            try c.add(.{
                .stem = kind.stem,
                .format = kind.format,
                .key = try join(c.arena, key_prefix, name),
                .origin = path,
                .text = text,
            });
        }
    }

    /// Reads the scheme entries of a .sublime-package (a zip).
    fn scanZip(c: *Collector, path: []const u8, package: []const u8) !void {
        const file = try Io.Dir.cwd().openFile(c.io, path, .{});
        defer file.close(c.io);
        var buf: [4096]u8 = undefined;
        var reader = file.reader(c.io, &buf);
        var it = try std.zip.Iterator.init(&reader);
        var name_buf: [1024]u8 = undefined;
        while (try it.next()) |entry| {
            if (entry.filename_len == 0 or entry.filename_len > name_buf.len) continue;
            const name = name_buf[0..entry.filename_len];
            try reader.seekTo(entry.header_zip_offset + @sizeOf(std.zip.CentralDirectoryFileHeader));
            try reader.interface.readSliceAll(name);
            const kind = classify(name) orelse continue;
            const text = readZipEntry(c.arena, &reader, entry) catch |err| switch (err) {
                error.OutOfMemory => return error.OutOfMemory,
                else => {
                    log.debug("skipping {s}:/{s}: {t}", .{ path, name, err });
                    continue;
                },
            };
            try c.add(.{
                .stem = try c.arena.dupe(u8, kind.stem),
                .format = kind.format,
                .key = try join(c.arena, package, name),
                .origin = try std.fmt.allocPrint(c.arena, "{s}:/{s}", .{ path, name }),
                .text = text,
            });
        }
    }

    /// Merges same-named files and builds the schemes.
    fn build(c: *Collector, gpa: Allocator) error{OutOfMemory}!std.ArrayList(LoadedScheme) {
        var out: std.ArrayList(LoadedScheme) = .empty;
        errdefer deinitList(gpa, &out);
        // Index of the last file behind each scheme in `out`, for precedence.
        var ranks: std.ArrayList(usize) = .empty;
        const files = c.files.items;
        const used = try c.arena.alloc(bool, files.len);
        @memset(used, false);
        for (files, 0..) |first, i| {
            if (used[i]) continue;
            var model: Model = .init(gpa);
            defer model.deinit();
            var origin = first.origin;
            var last = i;
            for (files[i..], used[i..], i..) |f, *u, j| {
                if (u.* or f.format != first.format or !std.mem.eql(u8, f.stem, first.stem)) continue;
                u.* = true;
                model.set_background = false;
                model.apply(f.format, f.text) catch |err| switch (err) {
                    error.OutOfMemory => return error.OutOfMemory,
                    else => {
                        log.debug("skipping {s}: {t}", .{ f.origin, err });
                        continue;
                    },
                };
                // Report the file whose background is in effect.
                if (model.set_background) origin = f.origin;
                last = j;
            }
            var loaded = model.finish(gpa, first.stem, origin) catch |err| switch (err) {
                error.OutOfMemory => return error.OutOfMemory,
                else => {
                    log.debug("skipping scheme {s}: {t}", .{ first.stem, err });
                    continue;
                },
            };
            // The same name in another format: the later source wins.
            if (findIndex(out.items, loaded.scheme.name)) |k| {
                if (ranks.items[k] > last) {
                    loaded.deinit(gpa);
                } else {
                    out.items[k].deinit(gpa);
                    out.items[k] = loaded;
                    ranks.items[k] = last;
                }
                continue;
            }
            out.append(gpa, loaded) catch |err| {
                loaded.deinit(gpa);
                return err;
            };
            try ranks.append(c.arena, last);
        }
        std.mem.sort(LoadedScheme, out.items, {}, struct {
            fn f(_: void, a: LoadedScheme, b: LoadedScheme) bool {
                return std.ascii.lessThanIgnoreCase(a.scheme.name, b.scheme.name);
            }
        }.f);
        return out;
    }
};

/// Reads and decompresses one zip entry (stored or deflated), checking its CRC.
fn readZipEntry(gpa: Allocator, reader: *Io.File.Reader, entry: std.zip.Iterator.Entry) ![]u8 {
    if (entry.uncompressed_size > max_scheme_size or entry.compressed_size > max_scheme_size)
        return error.SchemeTooLarge;
    const size: usize = @intCast(entry.uncompressed_size);
    try reader.seekTo(entry.file_offset);
    const local = try reader.interface.takeStruct(std.zip.LocalFileHeader, .little);
    if (!std.mem.eql(u8, &local.signature, &std.zip.local_file_header_sig)) return error.ZipBadFileOffset;
    try reader.seekTo(entry.file_offset + @sizeOf(std.zip.LocalFileHeader) + local.filename_len + local.extra_len);
    const data = switch (entry.compression_method) {
        .store => blk: {
            if (entry.compressed_size != entry.uncompressed_size) return error.ZipBadSize;
            break :blk try reader.interface.readAlloc(gpa, size);
        },
        .deflate => blk: {
            const window = try gpa.alloc(u8, std.compress.flate.max_window_len);
            defer gpa.free(window);
            var inflate: std.compress.flate.Decompress = .init(&reader.interface, .raw, window);
            break :blk inflate.reader.readAlloc(gpa, size) catch |err| switch (err) {
                error.ReadFailed => return inflate.err orelse error.ReadFailed,
                else => return err,
            };
        },
        else => return error.UnsupportedCompressionMethod,
    };
    errdefer gpa.free(data);
    if (std.hash.Crc32.hash(data) != entry.crc32) return error.ZipBadCrc32;
    return data;
}

// ---- Scheme model ---------------------------------------------------------------

const VarMap = std.array_hash_map.String([]const u8);

const Rule = struct {
    scope: []const u8,
    foreground: ?[]const u8 = null,
    /// "italic", "bold", "bold italic", "" (normal), ...; null = not set.
    font_style: ?[]const u8 = null,
};

/// A scheme's raw strings, possibly merged from several files.
const Model = struct {
    arena_state: std.heap.ArenaAllocator,
    variables: VarMap = .empty,
    globals: VarMap = .empty,
    rules: std.ArrayList(Rule) = .empty,
    /// The file's own "name" field.
    name: ?[]const u8 = null,
    /// Whether the last `apply` set the background.
    set_background: bool = false,

    fn init(gpa: Allocator) Model {
        return .{ .arena_state = .init(gpa) };
    }

    fn deinit(self: *Model) void {
        self.arena_state.deinit();
    }

    fn apply(self: *Model, format: Format, text: []const u8) ParseError!void {
        return switch (format) {
            .json => self.applyJson(text),
            .tmtheme => self.applyPlist(text),
        };
    }

    fn putGlobal(self: *Model, key: []const u8, value: []const u8) error{OutOfMemory}!void {
        try self.globals.put(self.arena_state.allocator(), key, value);
        if (std.mem.eql(u8, key, "background")) self.set_background = true;
    }

    fn applyJson(self: *Model, text: []const u8) ParseError!void {
        const arena = self.arena_state.allocator();
        const strict = try toStrictJson(arena, text);
        const root = std.json.parseFromSliceLeaky(std.json.Value, arena, strict, .{
            .duplicate_field_behavior = .use_last,
            .allocate = .alloc_always,
        }) catch |err| switch (err) {
            error.OutOfMemory => return error.OutOfMemory,
            else => return error.InvalidColorScheme,
        };
        if (root != .object) return error.InvalidColorScheme;
        const obj = root.object;
        if (obj.get("name")) |v| {
            if (v == .string) self.name = v.string;
        }
        if (obj.get("variables")) |v| {
            if (v == .object) for (v.object.keys(), v.object.values()) |key, val| {
                if (val == .string) try self.variables.put(arena, key, val.string);
            };
        }
        if (obj.get("globals")) |v| {
            if (v == .object) for (v.object.keys(), v.object.values()) |key, val| {
                if (val == .string) try self.putGlobal(key, val.string);
            };
        }
        if (obj.get("rules")) |v| {
            if (v == .array) for (v.array.items) |item| {
                if (item != .object) continue;
                const scope = item.object.get("scope") orelse continue;
                if (scope != .string) continue;
                const fg_text: ?[]const u8 = if (item.object.get("foreground")) |fg| switch (fg) {
                    .string => |s| s,
                    // A gradient ("hashed" foreground): use its first color.
                    .array => |a| if (a.items.len > 0 and a.items[0] == .string) a.items[0].string else null,
                    else => null,
                } else null;
                const style: ?[]const u8 = if (item.object.get("font_style")) |fs| (if (fs == .string) fs.string else null) else null;
                if (fg_text == null and style == null) continue;
                try self.rules.append(arena, .{ .scope = scope.string, .foreground = fg_text, .font_style = style });
            };
        }
    }

    fn applyPlist(self: *Model, text: []const u8) ParseError!void {
        const arena = self.arena_state.allocator();
        const root = try parsePlist(arena, text);
        if (root != .dict) return error.InvalidColorScheme;
        if (root.get("name")) |n| {
            if (n == .string) self.name = n.string;
        }
        const items = root.get("settings") orelse return error.InvalidColorScheme;
        if (items != .array) return error.InvalidColorScheme;
        var have_globals = false;
        for (items.array) |item| {
            const settings = item.get("settings") orelse continue;
            if (settings != .dict) continue;
            const scope = item.get("scope");
            if (scope == null and !have_globals) {
                have_globals = true;
                for (settings.dict) |kv| {
                    if (kv.value != .string) continue;
                    const key = if (std.mem.eql(u8, kv.key, "lineHighlight")) "line_highlight" else kv.key;
                    try self.putGlobal(key, kv.value.string);
                }
            } else if (scope != null and scope.? == .string) {
                const fg: ?[]const u8 = if (settings.get("foreground")) |f| (if (f == .string) f.string else null) else null;
                const style: ?[]const u8 = if (settings.get("fontStyle")) |f| (if (f == .string) f.string else null) else null;
                if (fg == null and style == null) continue;
                try self.rules.append(arena, .{ .scope = scope.?.string, .foreground = fg, .font_style = style });
            }
        }
    }

    fn finish(self: *Model, gpa: Allocator, name: []const u8, source: []const u8) ParseError!LoadedScheme {
        const scheme = try self.build();
        const display = if (name.len > 0) name else self.name orelse "Untitled";
        return LoadedScheme.init(gpa, scheme, display, source);
    }

    fn color(self: *const Model, text: ?[]const u8) ?Color {
        return resolveColor(text orelse return null, &self.variables, 0) catch null;
    }

    fn build(self: *Model) ParseError!theme.Scheme {
        var bg = self.color(self.globals.get("background")) orelse return error.MissingBackground;
        bg.a = 1;
        const light = relativeLuminance(bg) > 0.5;
        const fg = over(self.color(self.globals.get("foreground")) orelse
            (if (light) Color.black else Color.white), bg);

        // Rule colors, resolved once; rules whose color doesn't parse are ignored.
        const rule_colors = try self.arena_state.allocator().alloc(?Color, self.rules.items.len);
        for (self.rules.items, rule_colors) |rule, *out| {
            out.* = if (self.color(rule.foreground)) |c| over(c, bg) else null;
        }

        var syntax: theme.Syntax = undefined;
        inline for (@typeInfo(theme.Syntax).@"struct".field_names) |name| @field(syntax, name) = fg;
        inline for (slot_scopes) |slot| {
            if (slotColor(self.rules.items, rule_colors, slot[1])) |c| @field(syntax, @tagName(slot[0])) = c;
        }
        var styles: theme.SyntaxStyles = .{};
        inline for (style_scopes) |slot| {
            if (slotStyle(self.rules.items, slot[1])) |st| @field(styles, @tagName(slot[0])) = st;
        }
        return .{
            .name = "",
            .light = light,
            .styles = styles,
            .background = bg,
            .foreground = fg,
            .caret = over(self.color(self.globals.get("caret")) orelse fg, bg),
            .selection = self.color(self.globals.get("selection")) orelse Color.mix(bg, fg, 0.2),
            .line_highlight = self.color(self.globals.get("line_highlight")) orelse Color.mix(bg, fg, 0.06),
            .syntax = syntax,
        };
    }
};

/// Representative Sublime scopes for each syntax slot, in fallback order.
const slot_scopes = [_]struct { std.meta.FieldEnum(theme.Syntax), []const []const u8 }{
    .{ .comment, &.{"comment.line"} },
    .{ .string, &.{"string.quoted.double"} },
    .{ .number, &.{"constant.numeric"} },
    .{ .constant, &.{"constant.language"} },
    .{ .keyword, &.{"keyword.control"} },
    .{ .operator, &.{"keyword.operator"} },
    .{ .function, &.{"entity.name.function"} },
    .{ .type, &.{ "entity.name.type", "storage.type" } },
    .{ .variable_builtin, &.{"variable.language"} },
    .{ .parameter, &.{"variable.parameter"} },
    .{ .property, &.{"variable.other.member"} },
    .{ .punctuation, &.{"punctuation.separator"} },
    .{ .tag, &.{"entity.name.tag"} },
    .{ .attribute, &.{"entity.other.attribute-name"} },
    .{ .escape, &.{"constant.character.escape"} },
    .{ .label, &.{"entity.name.label"} },
};

/// Representative Sublime scopes for each font style slot.
const style_scopes = [_]struct { std.meta.FieldEnum(theme.SyntaxStyles), []const []const u8 }{
    .{ .comment, &.{"comment.line"} },
    .{ .string, &.{"string.quoted.double"} },
    .{ .keyword, &.{"keyword.control"} },
    .{ .storage_type, &.{ "storage.type", "support.type" } },
    .{ .function, &.{"entity.name.function"} },
    .{ .support_function, &.{"support.function"} },
    .{ .type, &.{"entity.name.type"} },
    .{ .parameter, &.{"variable.parameter"} },
    .{ .variable_builtin, &.{"variable.language"} },
    .{ .constant, &.{"constant.language"} },
    .{ .tag, &.{"entity.name.tag"} },
    .{ .attribute, &.{"entity.other.attribute-name"} },
    .{ .heading, &.{"markup.heading"} },
    .{ .emphasis, &.{"markup.italic"} },
    .{ .strong, &.{"markup.bold"} },
};

/// The font style of the best-matching rule that sets one (Sublime resolves
/// each property separately), or null to keep the default.
fn slotStyle(rules: []const Rule, scopes: []const []const u8) ?theme.FontStyle {
    for ([_]bool{ false, true }) |relaxed| {
        for (scopes) |scope| {
            var best: ?u32 = null;
            var result: ?[]const u8 = null;
            for (rules) |rule| {
                const fs = rule.font_style orelse continue;
                const score = matchSelector(rule.scope, scope, relaxed) orelse continue;
                if (best == null or score >= best.?) {
                    best = score;
                    result = fs;
                }
            }
            if (result) |fs| return parseFontStyle(fs);
        }
    }
    return null;
}

/// "bold italic", "italic", "" → a style (underline/glow etc. are ignored).
pub fn parseFontStyle(text: []const u8) theme.FontStyle {
    var italic = false;
    var bold = false;
    var it = std.mem.tokenizeAny(u8, text, " ,");
    while (it.next()) |word| {
        if (std.ascii.eqlIgnoreCase(word, "italic")) italic = true;
        if (std.ascii.eqlIgnoreCase(word, "bold")) bold = true;
    }
    return .init(italic, bold);
}

/// The color of the best rule for the first scope any rule matches. A
/// representative scope has no context, so selectors that need ancestors
/// ("meta.mapping.key string") only count when nothing else matches.
fn slotColor(rules: []const Rule, colors: []const ?Color, scopes: []const []const u8) ?Color {
    for ([_]bool{ false, true }) |relaxed| {
        for (scopes) |scope| if (bestRule(rules, colors, scope, relaxed)) |c| return c;
    }
    return null;
}

fn bestRule(rules: []const Rule, colors: []const ?Color, scope: []const u8, relaxed: bool) ?Color {
    var best: ?u32 = null;
    var result: ?Color = null;
    for (rules, colors) |rule, c| {
        const col = c orelse continue;
        const score = matchSelector(rule.scope, scope, relaxed) orelse continue;
        // Later rules win ties, as in Sublime.
        if (best == null or score >= best.?) {
            best = score;
            result = col;
        }
    }
    return result;
}

// ---- Scope selectors --------------------------------------------------------------

/// Scores a Sublime scope selector against a scope stack (space-separated
/// scopes, outermost first, like "source.python string.quoted.double"), or
/// null if it doesn't match. Supports `,` and `|` (or), `&` (and), `-`
/// (without), parentheses and descendant paths. Higher is more specific:
/// matches deeper in the stack, then more dot-separated segments matched.
pub fn selectorScore(selector: []const u8, scope: []const u8) ?u32 {
    return matchSelector(selector, scope, false);
}

const max_stack = 32;

fn matchSelector(selector: []const u8, scope: []const u8, relaxed: bool) ?u32 {
    var stack: [max_stack][]const u8 = undefined;
    var n: usize = 0;
    var it = std.mem.tokenizeAny(u8, scope, " \t\r\n");
    while (it.next()) |s| {
        if (n == stack.len) break;
        stack[n] = s;
        n += 1;
    }
    var m: SelectorMatcher = .{ .src = selector, .stack = stack[0..n] };
    return m.selector(relaxed);
}

fn isSelectorOperator(c: u8) bool {
    return switch (c) {
        ',', '|', '&', '(', ')' => true,
        else => false,
    };
}

fn maxScore(a: ?u32, b: ?u32) ?u32 {
    if (a == null) return b;
    if (b == null) return a;
    return @max(a.?, b.?);
}

/// Evaluates a selector while parsing it. With `relaxed`, descendant paths
/// only need their last part to match (exclusions stay strict).
const SelectorMatcher = struct {
    src: []const u8,
    pos: usize = 0,
    stack: []const []const u8,

    fn peek(m: *SelectorMatcher) ?u8 {
        while (m.pos < m.src.len and std.ascii.isWhitespace(m.src[m.pos])) m.pos += 1;
        return if (m.pos < m.src.len) m.src[m.pos] else null;
    }

    fn selector(m: *SelectorMatcher, relaxed: bool) ?u32 {
        var best = m.composite(relaxed);
        while (m.peek()) |c| {
            if (c != ',') break;
            m.pos += 1;
            best = maxScore(best, m.composite(relaxed));
        }
        return best;
    }

    fn composite(m: *SelectorMatcher, relaxed: bool) ?u32 {
        var acc = m.expression(relaxed);
        while (m.peek()) |c| {
            switch (c) {
                '|' => {
                    m.pos += 1;
                    acc = maxScore(acc, m.expression(relaxed));
                },
                '&' => {
                    m.pos += 1;
                    const rhs = m.expression(relaxed);
                    acc = if (acc != null and rhs != null) @max(acc.?, rhs.?) else null;
                },
                '-' => {
                    m.pos += 1;
                    if (m.expression(false) != null) acc = null;
                },
                else => break,
            }
        }
        return acc;
    }

    fn expression(m: *SelectorMatcher, relaxed: bool) ?u32 {
        const c = m.peek() orelse return null;
        switch (c) {
            '-' => {
                m.pos += 1;
                return if (m.expression(false) == null) 0 else null;
            },
            '(' => {
                m.pos += 1;
                const inner = m.selector(relaxed);
                if (m.peek()) |d| {
                    if (d == ')') m.pos += 1;
                }
                return inner;
            },
            else => return m.path(relaxed),
        }
    }

    fn path(m: *SelectorMatcher, relaxed: bool) ?u32 {
        var parts: [16][]const u8 = undefined;
        var n: usize = 0;
        while (m.peek()) |c| {
            // A '-' starting a name is the "without" operator.
            if (isSelectorOperator(c) or c == '-') break;
            const start = m.pos;
            while (m.pos < m.src.len and !std.ascii.isWhitespace(m.src[m.pos]) and !isSelectorOperator(m.src[m.pos])) m.pos += 1;
            if (n < parts.len) {
                parts[n] = m.src[start..m.pos];
                n += 1;
            }
        }
        if (n == 0) return null;
        return matchPath(parts[0..n], m.stack, relaxed);
    }
};

/// Matches descendant parts against the stack: the last part against the
/// deepest scope it can, ancestors in order above it.
fn matchPath(parts: []const []const u8, stack: []const []const u8, relaxed: bool) ?u32 {
    const last = parts[parts.len - 1];
    var i = stack.len;
    while (i > 0) {
        i -= 1;
        const segments = segmentMatch(last, stack[i]) orelse continue;
        var ancestors: u32 = 0;
        if (!relaxed) {
            var j = i;
            var k = parts.len - 1;
            while (k > 0) {
                k -= 1;
                while (true) {
                    if (j == 0) return null;
                    j -= 1;
                    if (segmentMatch(parts[k], stack[j]) != null) break;
                }
                ancestors += 1;
            }
        }
        return (@as(u32, @intCast(i)) << 16) | (@as(u32, @min(segments, 255)) << 8) | @min(ancestors, 255);
    }
    return null;
}

/// Number of segments matched if `selector` is a dot-boundary prefix of `scope`.
fn segmentMatch(selector: []const u8, scope: []const u8) ?u32 {
    if (selector.len == 0 or !std.mem.startsWith(u8, scope, selector)) return null;
    if (scope.len > selector.len and scope[selector.len] != '.') return null;
    var n: u32 = 1;
    for (selector) |c| {
        if (c == '.') n += 1;
    }
    return n;
}

// ---- Colors -------------------------------------------------------------------------

pub const ColorError = error{InvalidColor};

/// Parses a color without variables: #rgb, #rgba, #rrggbb, #rrggbbaa,
/// rgb()/rgba(), hsl()/hsla(), CSS color names, and Sublime's
/// color(<color> <adjusters>) with alpha()/a(), blend()/blenda(),
/// lightness()/l(), saturation()/s() (min-contrast() is ignored).
pub fn parseColor(text: []const u8) ColorError!Color {
    const no_vars: VarMap = .empty;
    return resolveColor(text, &no_vars, 0);
}

const max_var_depth = 32;

fn resolveColor(text: []const u8, vars: *const VarMap, depth: u8) ColorError!Color {
    if (depth > max_var_depth) return error.InvalidColor;
    var p: ColorParser = .{ .src = text, .vars = vars, .depth = depth };
    const c = try p.expr();
    if (p.peek() != null) return error.InvalidColor;
    return .{
        .r = std.math.clamp(c.r, 0, 1),
        .g = std.math.clamp(c.g, 0, 1),
        .b = std.math.clamp(c.b, 0, 1),
        .a = std.math.clamp(c.a, 0, 1),
    };
}

const ColorParser = struct {
    src: []const u8,
    pos: usize = 0,
    vars: *const VarMap,
    depth: u8,

    const Number = struct { value: f32, percent: bool };

    const Adjust = struct {
        op: enum { set, add, sub, mul },
        value: f32,
        percent: bool,

        /// `plain_scale` converts a plain (non-%) number to a fraction.
        fn apply(a: Adjust, current: f32, plain_scale: f32) f32 {
            const amount = if (a.percent) a.value / 100 else a.value * plain_scale;
            return std.math.clamp(switch (a.op) {
                .set => amount,
                .add => current + amount,
                .sub => current - amount,
                .mul => current * (if (a.percent) a.value / 100 else a.value),
            }, 0, 1);
        }
    };

    fn peek(p: *ColorParser) ?u8 {
        while (p.pos < p.src.len and std.ascii.isWhitespace(p.src[p.pos])) p.pos += 1;
        return if (p.pos < p.src.len) p.src[p.pos] else null;
    }

    fn eat(p: *ColorParser, c: u8) bool {
        if (p.peek() != c) return false;
        p.pos += 1;
        return true;
    }

    fn expect(p: *ColorParser, c: u8) ColorError!void {
        if (!p.eat(c)) return error.InvalidColor;
    }

    fn ident(p: *ColorParser) []const u8 {
        _ = p.peek();
        const start = p.pos;
        while (p.pos < p.src.len and (std.ascii.isAlphanumeric(p.src[p.pos]) or p.src[p.pos] == '-' or p.src[p.pos] == '_')) p.pos += 1;
        return p.src[start..p.pos];
    }

    fn is(name: []const u8, comptime options: []const []const u8) bool {
        inline for (options) |o| if (std.ascii.eqlIgnoreCase(name, o)) return true;
        return false;
    }

    fn expr(p: *ColorParser) ColorError!Color {
        const first = p.peek() orelse return error.InvalidColor;
        if (first == '#') {
            p.pos += 1;
            const start = p.pos;
            while (p.pos < p.src.len and std.ascii.isHex(p.src[p.pos])) p.pos += 1;
            return hexColor(p.src[start..p.pos]) orelse error.InvalidColor;
        }
        const name = p.ident();
        if (name.len == 0) return error.InvalidColor;
        if (p.pos < p.src.len and p.src[p.pos] == '(') {
            p.pos += 1;
            if (is(name, &.{"var"})) return p.variable();
            if (is(name, &.{"color"})) return p.colorMod();
            if (is(name, &.{ "rgb", "rgba" })) return p.rgb();
            if (is(name, &.{ "hsl", "hsla" })) return p.hsl();
            return error.InvalidColor;
        }
        return namedColor(name) orelse error.InvalidColor;
    }

    fn variable(p: *ColorParser) ColorError!Color {
        const close = std.mem.findScalarPos(u8, p.src, p.pos, ')') orelse return error.InvalidColor;
        const name = std.mem.trim(u8, p.src[p.pos..close], " \t\r\n");
        p.pos = close + 1;
        const value = p.vars.get(name) orelse return error.InvalidColor;
        return resolveColor(value, p.vars, p.depth + 1);
    }

    fn number(p: *ColorParser) ColorError!Number {
        _ = p.peek();
        const start = p.pos;
        if (p.pos < p.src.len and (p.src[p.pos] == '+' or p.src[p.pos] == '-')) p.pos += 1;
        while (p.pos < p.src.len and (std.ascii.isDigit(p.src[p.pos]) or p.src[p.pos] == '.')) p.pos += 1;
        const value = std.fmt.parseFloat(f32, p.src[start..p.pos]) catch return error.InvalidColor;
        const percent = p.pos < p.src.len and p.src[p.pos] == '%';
        if (percent) p.pos += 1;
        return .{ .value = value, .percent = percent };
    }

    /// Function arguments, separated by commas, spaces or '/'; consumes ')'.
    fn args(p: *ColorParser, out: []Number) ColorError![]Number {
        var n: usize = 0;
        while (true) {
            while (p.pos < p.src.len and (std.ascii.isWhitespace(p.src[p.pos]) or p.src[p.pos] == ',' or p.src[p.pos] == '/')) p.pos += 1;
            if (p.eat(')')) return out[0..n];
            if (n == out.len) return error.InvalidColor;
            out[n] = try p.number();
            // Angle units on hues, directly after the number.
            const unit_start = p.pos;
            while (p.pos < p.src.len and std.ascii.isAlphabetic(p.src[p.pos])) p.pos += 1;
            const unit = p.src[unit_start..p.pos];
            if (unit.len > 0) {
                if (is(unit, &.{"rad"})) {
                    out[n].value *= 180.0 / std.math.pi;
                } else if (is(unit, &.{"turn"})) {
                    out[n].value *= 360;
                } else if (is(unit, &.{"grad"})) {
                    out[n].value *= 0.9;
                } else if (!is(unit, &.{"deg"})) return error.InvalidColor;
            }
            n += 1;
        }
    }

    fn alphaArg(n: ?Number) f32 {
        const a = n orelse return 1;
        return if (a.percent) a.value / 100 else a.value;
    }

    fn rgb(p: *ColorParser) ColorError!Color {
        var buf: [4]Number = undefined;
        const v = try p.args(&buf);
        if (v.len < 3) return error.InvalidColor;
        const channel = struct {
            fn f(n: Number) f32 {
                return if (n.percent) n.value / 100 else n.value / 255;
            }
        }.f;
        return .{ .r = channel(v[0]), .g = channel(v[1]), .b = channel(v[2]), .a = alphaArg(if (v.len == 4) v[3] else null) };
    }

    fn hsl(p: *ColorParser) ColorError!Color {
        var buf: [4]Number = undefined;
        const v = try p.args(&buf);
        if (v.len < 3) return error.InvalidColor;
        // Saturation and lightness are percentages, with or without '%'.
        return fromHsl(v[0].value, v[1].value / 100, v[2].value / 100, alphaArg(if (v.len == 4) v[3] else null));
    }

    fn adjust(p: *ColorParser) ColorError!Adjust {
        const c = p.peek() orelse return error.InvalidColor;
        const op: @FieldType(Adjust, "op") = switch (c) {
            '+' => .add,
            '-' => .sub,
            '*' => .mul,
            else => .set,
        };
        if (op != .set) p.pos += 1;
        const n = try p.number();
        try p.expect(')');
        return .{ .op = op, .value = n.value, .percent = n.percent };
    }

    /// Skips the rest of a parenthesized argument list, including ')'.
    fn skipArgs(p: *ColorParser) ColorError!void {
        var depth: usize = 1;
        while (p.pos < p.src.len) : (p.pos += 1) {
            switch (p.src[p.pos]) {
                '(' => depth += 1,
                ')' => {
                    depth -= 1;
                    if (depth == 0) {
                        p.pos += 1;
                        return;
                    }
                },
                else => {},
            }
        }
        return error.InvalidColor;
    }

    /// color(<base> <adjuster>*), from the CSS color-mod draft as Sublime implements it.
    fn colorMod(p: *ColorParser) ColorError!Color {
        var c = try p.expr();
        while (true) {
            if (p.eat(')')) return c;
            const name = p.ident();
            if (name.len == 0 or !p.eat('(')) return error.InvalidColor;
            if (is(name, &.{ "alpha", "a" })) {
                c.a = (try p.adjust()).apply(c.a, 1);
            } else if (is(name, &.{ "lightness", "l" })) {
                var h = toHsl(c);
                h.l = (try p.adjust()).apply(h.l, 0.01);
                c = fromHsl(h.h, h.s, h.l, c.a);
            } else if (is(name, &.{ "saturation", "s" })) {
                var h = toHsl(c);
                h.s = (try p.adjust()).apply(h.s, 0.01);
                c = fromHsl(h.h, h.s, h.l, c.a);
            } else if (is(name, &.{ "blend", "blenda" })) {
                // The percentage is how much of the base color to keep.
                const other = try p.expr();
                const amount = try p.number();
                const t = std.math.clamp(if (amount.percent or amount.value > 1) amount.value / 100 else amount.value, 0, 1);
                _ = p.ident(); // optional color space; we always mix in RGB
                try p.expect(')');
                const alpha = if (is(name, &.{"blenda"})) c.a * t + other.a * (1 - t) else c.a;
                c = Color.mix(other, c, t);
                c.a = alpha;
            } else {
                // min-contrast() and anything else we don't model.
                try p.skipArgs();
            }
        }
    }
};

fn hexColor(digits: []const u8) ?Color {
    var v: [4]u8 = .{ 0, 0, 0, 255 };
    switch (digits.len) {
        3, 4 => for (digits, 0..) |d, i| {
            v[i] = (std.fmt.charToDigit(d, 16) catch return null) * 17;
        },
        6, 8 => for (0..digits.len / 2) |i| {
            v[i] = std.fmt.parseInt(u8, digits[i * 2 ..][0..2], 16) catch return null;
        },
        else => return null,
    }
    return .{
        .r = @as(f32, @floatFromInt(v[0])) / 255,
        .g = @as(f32, @floatFromInt(v[1])) / 255,
        .b = @as(f32, @floatFromInt(v[2])) / 255,
        .a = @as(f32, @floatFromInt(v[3])) / 255,
    };
}

const Hsl = struct { h: f32, s: f32, l: f32 };

fn toHsl(c: Color) Hsl {
    const max = @max(c.r, @max(c.g, c.b));
    const min = @min(c.r, @min(c.g, c.b));
    const l = (max + min) / 2;
    const d = max - min;
    const denom = 1 - @abs(2 * l - 1);
    if (d <= 0 or denom <= 0) return .{ .h = 0, .s = 0, .l = l };
    const h: f32 = if (max == c.r)
        @mod((c.g - c.b) / d, 6)
    else if (max == c.g)
        (c.b - c.r) / d + 2
    else
        (c.r - c.g) / d + 4;
    return .{ .h = h * 60, .s = std.math.clamp(d / denom, 0, 1), .l = l };
}

fn fromHsl(hue: f32, saturation: f32, lightness: f32, alpha: f32) Color {
    const s = std.math.clamp(saturation, 0, 1);
    const l = std.math.clamp(lightness, 0, 1);
    const hh = @mod(hue, 360) / 60;
    const c = (1 - @abs(2 * l - 1)) * s;
    const x = c * (1 - @abs(@mod(hh, 2) - 1));
    const m = l - c / 2;
    const rgb: [3]f32 = if (hh < 1)
        .{ c, x, 0 }
    else if (hh < 2)
        .{ x, c, 0 }
    else if (hh < 3)
        .{ 0, c, x }
    else if (hh < 4)
        .{ 0, x, c }
    else if (hh < 5)
        .{ x, 0, c }
    else
        .{ c, 0, x };
    return .{ .r = rgb[0] + m, .g = rgb[1] + m, .b = rgb[2] + m, .a = alpha };
}

/// WCAG relative luminance of an sRGB color (alpha ignored).
pub fn relativeLuminance(c: Color) f32 {
    const linear = struct {
        fn f(v: f32) f32 {
            return if (v <= 0.04045) v / 12.92 else std.math.pow(f32, (v + 0.055) / 1.055, 2.4);
        }
    }.f;
    return 0.2126 * linear(c.r) + 0.7152 * linear(c.g) + 0.0722 * linear(c.b);
}

/// `c` composited over an opaque `bg`, as Sublime draws translucent text.
fn over(c: Color, bg: Color) Color {
    if (c.a >= 1) return c;
    var out = Color.mix(bg, c, c.a);
    out.a = 1;
    return out;
}

fn namedColor(name: []const u8) ?Color {
    if (std.ascii.eqlIgnoreCase(name, "transparent")) return Color.transparent;
    for (css_colors) |entry| {
        if (std.ascii.eqlIgnoreCase(name, entry[0])) return Color.hex(entry[1]);
    }
    return null;
}

/// CSS named colors.
const css_colors = [_]struct { []const u8, u24 }{
    .{ "aliceblue", 0xf0f8ff },         .{ "antiquewhite", 0xfaebd7 },         .{ "aqua", 0x00ffff },
    .{ "aquamarine", 0x7fffd4 },        .{ "azure", 0xf0ffff },                .{ "beige", 0xf5f5dc },
    .{ "bisque", 0xffe4c4 },            .{ "black", 0x000000 },                .{ "blanchedalmond", 0xffebcd },
    .{ "blue", 0x0000ff },              .{ "blueviolet", 0x8a2be2 },           .{ "brown", 0xa52a2a },
    .{ "burlywood", 0xdeb887 },         .{ "cadetblue", 0x5f9ea0 },            .{ "chartreuse", 0x7fff00 },
    .{ "chocolate", 0xd2691e },         .{ "coral", 0xff7f50 },                .{ "cornflowerblue", 0x6495ed },
    .{ "cornsilk", 0xfff8dc },          .{ "crimson", 0xdc143c },              .{ "cyan", 0x00ffff },
    .{ "darkblue", 0x00008b },          .{ "darkcyan", 0x008b8b },             .{ "darkgoldenrod", 0xb8860b },
    .{ "darkgray", 0xa9a9a9 },          .{ "darkgreen", 0x006400 },            .{ "darkgrey", 0xa9a9a9 },
    .{ "darkkhaki", 0xbdb76b },         .{ "darkmagenta", 0x8b008b },          .{ "darkolivegreen", 0x556b2f },
    .{ "darkorange", 0xff8c00 },        .{ "darkorchid", 0x9932cc },           .{ "darkred", 0x8b0000 },
    .{ "darksalmon", 0xe9967a },        .{ "darkseagreen", 0x8fbc8f },         .{ "darkslateblue", 0x483d8b },
    .{ "darkslategray", 0x2f4f4f },     .{ "darkslategrey", 0x2f4f4f },        .{ "darkturquoise", 0x00ced1 },
    .{ "darkviolet", 0x9400d3 },        .{ "deeppink", 0xff1493 },             .{ "deepskyblue", 0x00bfff },
    .{ "dimgray", 0x696969 },           .{ "dimgrey", 0x696969 },              .{ "dodgerblue", 0x1e90ff },
    .{ "firebrick", 0xb22222 },         .{ "floralwhite", 0xfffaf0 },          .{ "forestgreen", 0x228b22 },
    .{ "fuchsia", 0xff00ff },           .{ "gainsboro", 0xdcdcdc },            .{ "ghostwhite", 0xf8f8ff },
    .{ "gold", 0xffd700 },              .{ "goldenrod", 0xdaa520 },            .{ "gray", 0x808080 },
    .{ "green", 0x008000 },             .{ "greenyellow", 0xadff2f },          .{ "grey", 0x808080 },
    .{ "honeydew", 0xf0fff0 },          .{ "hotpink", 0xff69b4 },              .{ "indianred", 0xcd5c5c },
    .{ "indigo", 0x4b0082 },            .{ "ivory", 0xfffff0 },                .{ "khaki", 0xf0e68c },
    .{ "lavender", 0xe6e6fa },          .{ "lavenderblush", 0xfff0f5 },        .{ "lawngreen", 0x7cfc00 },
    .{ "lemonchiffon", 0xfffacd },      .{ "lightblue", 0xadd8e6 },            .{ "lightcoral", 0xf08080 },
    .{ "lightcyan", 0xe0ffff },         .{ "lightgoldenrodyellow", 0xfafad2 }, .{ "lightgray", 0xd3d3d3 },
    .{ "lightgreen", 0x90ee90 },        .{ "lightgrey", 0xd3d3d3 },            .{ "lightpink", 0xffb6c1 },
    .{ "lightsalmon", 0xffa07a },       .{ "lightseagreen", 0x20b2aa },        .{ "lightskyblue", 0x87cefa },
    .{ "lightslategray", 0x778899 },    .{ "lightslategrey", 0x778899 },       .{ "lightsteelblue", 0xb0c4de },
    .{ "lightyellow", 0xffffe0 },       .{ "lime", 0x00ff00 },                 .{ "limegreen", 0x32cd32 },
    .{ "linen", 0xfaf0e6 },             .{ "magenta", 0xff00ff },              .{ "maroon", 0x800000 },
    .{ "mediumaquamarine", 0x66cdaa },  .{ "mediumblue", 0x0000cd },           .{ "mediumorchid", 0xba55d3 },
    .{ "mediumpurple", 0x9370db },      .{ "mediumseagreen", 0x3cb371 },       .{ "mediumslateblue", 0x7b68ee },
    .{ "mediumspringgreen", 0x00fa9a }, .{ "mediumturquoise", 0x48d1cc },      .{ "mediumvioletred", 0xc71585 },
    .{ "midnightblue", 0x191970 },      .{ "mintcream", 0xf5fffa },            .{ "mistyrose", 0xffe4e1 },
    .{ "moccasin", 0xffe4b5 },          .{ "navajowhite", 0xffdead },          .{ "navy", 0x000080 },
    .{ "oldlace", 0xfdf5e6 },           .{ "olive", 0x808000 },                .{ "olivedrab", 0x6b8e23 },
    .{ "orange", 0xffa500 },            .{ "orangered", 0xff4500 },            .{ "orchid", 0xda70d6 },
    .{ "palegoldenrod", 0xeee8aa },     .{ "palegreen", 0x98fb98 },            .{ "paleturquoise", 0xafeeee },
    .{ "palevioletred", 0xdb7093 },     .{ "papayawhip", 0xffefd5 },           .{ "peachpuff", 0xffdab9 },
    .{ "peru", 0xcd853f },              .{ "pink", 0xffc0cb },                 .{ "plum", 0xdda0dd },
    .{ "powderblue", 0xb0e0e6 },        .{ "purple", 0x800080 },               .{ "rebeccapurple", 0x663399 },
    .{ "red", 0xff0000 },               .{ "rosybrown", 0xbc8f8f },            .{ "royalblue", 0x4169e1 },
    .{ "saddlebrown", 0x8b4513 },       .{ "salmon", 0xfa8072 },               .{ "sandybrown", 0xf4a460 },
    .{ "seagreen", 0x2e8b57 },          .{ "seashell", 0xfff5ee },             .{ "sienna", 0xa0522d },
    .{ "silver", 0xc0c0c0 },            .{ "skyblue", 0x87ceeb },              .{ "slateblue", 0x6a5acd },
    .{ "slategray", 0x708090 },         .{ "slategrey", 0x708090 },            .{ "snow", 0xfffafa },
    .{ "springgreen", 0x00ff7f },       .{ "steelblue", 0x4682b4 },            .{ "tan", 0xd2b48c },
    .{ "teal", 0x008080 },              .{ "thistle", 0xd8bfd8 },              .{ "tomato", 0xff6347 },
    .{ "turquoise", 0x40e0d0 },         .{ "violet", 0xee82ee },               .{ "wheat", 0xf5deb3 },
    .{ "white", 0xffffff },             .{ "whitesmoke", 0xf5f5f5 },           .{ "yellow", 0xffff00 },
    .{ "yellowgreen", 0x9acd32 },
};

// ---- Sublime JSON -----------------------------------------------------------------

/// Strips // and /* */ comments, trailing commas and a BOM (outside strings).
fn toStrictJson(arena: Allocator, text: []const u8) error{OutOfMemory}![]u8 {
    const src = if (std.mem.startsWith(u8, text, "\xEF\xBB\xBF")) text[3..] else text;
    const out = try arena.alloc(u8, src.len);
    var n: usize = 0;
    var i: usize = 0;
    var in_string = false;
    while (i < src.len) {
        const c = src[i];
        if (in_string) {
            out[n] = c;
            n += 1;
            i += 1;
            if (c == '\\' and i < src.len) {
                out[n] = src[i];
                n += 1;
                i += 1;
            } else if (c == '"') in_string = false;
            continue;
        }
        if (c == '/' and i + 1 < src.len and src[i + 1] == '/') {
            while (i < src.len and src[i] != '\n') i += 1;
            continue;
        }
        if (c == '/' and i + 1 < src.len and src[i + 1] == '*') {
            i += 2;
            while (i + 1 < src.len and !(src[i] == '*' and src[i + 1] == '/')) i += 1;
            i = @min(i + 2, src.len);
            out[n] = ' ';
            n += 1;
            continue;
        }
        if (c == '"') in_string = true;
        out[n] = c;
        n += 1;
        i += 1;
    }
    // Drop commas that only whitespace separates from '}' or ']'.
    var m: usize = 0;
    in_string = false;
    i = 0;
    while (i < n) : (i += 1) {
        const c = out[i];
        if (in_string) {
            out[m] = c;
            m += 1;
            if (c == '\\' and i + 1 < n) {
                i += 1;
                out[m] = out[i];
                m += 1;
            } else if (c == '"') in_string = false;
            continue;
        }
        if (c == '"') in_string = true;
        if (c == ',') {
            var j = i + 1;
            while (j < n and std.ascii.isWhitespace(out[j])) j += 1;
            if (j < n and (out[j] == '}' or out[j] == ']')) continue;
        }
        out[m] = c;
        m += 1;
    }
    return out[0..m];
}

// ---- Property lists (.tmTheme) -----------------------------------------------------

const PlistNode = union(enum) {
    string: []const u8,
    dict: []const Entry,
    array: []const PlistNode,
    other,

    const Entry = struct { key: []const u8, value: PlistNode };

    fn get(node: PlistNode, key: []const u8) ?PlistNode {
        if (node != .dict) return null;
        for (node.dict) |e| if (std.mem.eql(u8, e.key, key)) return e.value;
        return null;
    }
};

const PlistError = error{ OutOfMemory, InvalidColorScheme };

fn parsePlist(arena: Allocator, text: []const u8) PlistError!PlistNode {
    var x: Xml = .{ .src = text, .arena = arena };
    while (try x.nextTag()) |tag| {
        if (std.mem.eql(u8, tag.name, "plist") and tag.kind == .open) continue;
        if (tag.kind != .open and tag.kind != .empty) break;
        return x.value(tag);
    }
    return error.InvalidColorScheme;
}

/// Just enough XML for Apple property lists.
const Xml = struct {
    src: []const u8,
    pos: usize = 0,
    arena: Allocator,

    const Tag = struct { name: []const u8, kind: enum { open, close, empty } };

    /// The next tag, skipping text, comments, declarations and processing
    /// instructions; null at the end.
    fn nextTag(x: *Xml) PlistError!?Tag {
        while (true) {
            x.pos = std.mem.findScalarPos(u8, x.src, x.pos, '<') orelse {
                x.pos = x.src.len;
                return null;
            };
            const rest = x.src[x.pos..];
            if (std.mem.startsWith(u8, rest, "<!--")) {
                const end = std.mem.find(u8, rest, "-->") orelse return error.InvalidColorScheme;
                x.pos += end + 3;
                continue;
            }
            const gt = std.mem.findScalarPos(u8, x.src, x.pos, '>') orelse return error.InvalidColorScheme;
            var inner = x.src[x.pos + 1 .. gt];
            x.pos = gt + 1;
            if (inner.len > 0 and (inner[0] == '?' or inner[0] == '!')) continue;
            var tag: Tag = .{ .name = undefined, .kind = .open };
            if (inner.len > 0 and inner[0] == '/') {
                tag.kind = .close;
                inner = inner[1..];
            } else if (inner.len > 0 and inner[inner.len - 1] == '/') {
                tag.kind = .empty;
                inner = inner[0 .. inner.len - 1];
            }
            const name_end = std.mem.findAny(u8, inner, " \t\r\n") orelse inner.len;
            tag.name = inner[0..name_end];
            return tag;
        }
    }

    /// The decoded text up to `</name>`.
    fn text(x: *Xml, name: []const u8) PlistError![]const u8 {
        const start = x.pos;
        const end = std.mem.findScalarPos(u8, x.src, x.pos, '<') orelse return error.InvalidColorScheme;
        const raw = x.src[start..end];
        x.pos = end;
        const close = try x.nextTag() orelse return error.InvalidColorScheme;
        if (close.kind != .close or !std.mem.eql(u8, close.name, name)) return error.InvalidColorScheme;
        return decodeEntities(x.arena, raw);
    }

    fn value(x: *Xml, tag: Tag) PlistError!PlistNode {
        if (tag.kind == .empty) return if (std.mem.eql(u8, tag.name, "string")) .{ .string = "" } else .other;
        if (std.mem.eql(u8, tag.name, "dict")) {
            var entries: std.ArrayList(PlistNode.Entry) = .empty;
            while (true) {
                const key_tag = try x.nextTag() orelse return error.InvalidColorScheme;
                if (key_tag.kind == .close) break;
                if (!std.mem.eql(u8, key_tag.name, "key")) return error.InvalidColorScheme;
                const key = if (key_tag.kind == .empty) "" else try x.text("key");
                const value_tag = try x.nextTag() orelse return error.InvalidColorScheme;
                if (value_tag.kind == .close) return error.InvalidColorScheme;
                try entries.append(x.arena, .{ .key = key, .value = try x.value(value_tag) });
            }
            return .{ .dict = entries.items };
        }
        if (std.mem.eql(u8, tag.name, "array")) {
            var items: std.ArrayList(PlistNode) = .empty;
            while (true) {
                const item_tag = try x.nextTag() orelse return error.InvalidColorScheme;
                if (item_tag.kind == .close) break;
                try items.append(x.arena, try x.value(item_tag));
            }
            return .{ .array = items.items };
        }
        const content = try x.text(tag.name);
        return if (std.mem.eql(u8, tag.name, "string")) .{ .string = content } else .other;
    }
};

fn decodeEntities(arena: Allocator, raw: []const u8) error{OutOfMemory}![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    try out.ensureTotalCapacity(arena, raw.len);
    var i: usize = 0;
    while (i < raw.len) : (i += 1) {
        if (raw[i] == '&') {
            if (std.mem.findScalarPos(u8, raw, i, ';')) |semi| {
                const entity = raw[i + 1 .. semi];
                const named = [_]struct { []const u8, u8 }{
                    .{ "amp", '&' }, .{ "lt", '<' }, .{ "gt", '>' }, .{ "quot", '"' }, .{ "apos", '\'' },
                };
                var decoded: ?u21 = null;
                for (named) |e| {
                    if (std.mem.eql(u8, entity, e[0])) decoded = e[1];
                }
                if (entity.len > 1 and entity[0] == '#') {
                    decoded = if (entity[1] == 'x' or entity[1] == 'X')
                        std.fmt.parseInt(u21, entity[2..], 16) catch null
                    else
                        std.fmt.parseInt(u21, entity[1..], 10) catch null;
                }
                if (decoded) |cp| {
                    var buf: [4]u8 = undefined;
                    const len = std.unicode.utf8Encode(cp, &buf) catch 0;
                    try out.appendSlice(arena, buf[0..len]);
                    i = semi;
                    continue;
                }
            }
        }
        try out.append(arena, raw[i]);
    }
    return out.items;
}

// ---- Tests --------------------------------------------------------------------------
// The schemes below are invented for these tests.

const testing = std.testing;

fn expectColor(expected: Color, actual: Color) !void {
    const tolerance = 0.004;
    errdefer std.debug.print("expected {any}, got {any}\n", .{ expected, actual });
    try testing.expectApproxEqAbs(expected.r, actual.r, tolerance);
    try testing.expectApproxEqAbs(expected.g, actual.g, tolerance);
    try testing.expectApproxEqAbs(expected.b, actual.b, tolerance);
    try testing.expectApproxEqAbs(expected.a, actual.a, tolerance);
}

fn rgba(r: f32, g: f32, b: f32, a: f32) Color {
    return .{ .r = r, .g = g, .b = b, .a = a };
}

test "color syntax" {
    try expectColor(Color.hex(0xaabbcc), try parseColor("#abc"));
    try expectColor(Color.hexa(0xaabbcc, @as(f32, 0xdd) / 255), try parseColor("#abcd"));
    try expectColor(Color.hex(0xa1b2c3), try parseColor("  #A1B2C3 "));
    try expectColor(Color.hexa(0xa1b2c3, @as(f32, 0x80) / 255), try parseColor("#a1b2c380"));
    try expectColor(rgba(1, 0, 128.0 / 255.0, 1), try parseColor("rgb(255, 0, 128)"));
    try expectColor(rgba(1, 0, 0, 0.5), try parseColor("rgba(255,0,0,0.5)"));
    try expectColor(rgba(1, 0, 0, 0.5), try parseColor("rgb(255 0 0 / 50%)"));
    try expectColor(rgba(1, 0.5, 0, 1), try parseColor("rgb(100%, 50%, 0%)"));
    try expectColor(rgba(0, 0.5, 0, 1), try parseColor("hsl(120, 100%, 25%)"));
    try expectColor(rgba(0, 0, 1, 0.25), try parseColor("hsla(240deg 100% 50% / 0.25)"));
    try expectColor(rgba(1, 0, 0, 1), try parseColor("hsl(0, 100, 50)"));
    try expectColor(rgba(0, 1, 1, 1), try parseColor("hsl(0.5turn, 100%, 50%)"));
    try expectColor(Color.transparent, try parseColor("transparent"));
    try expectColor(Color.hex(0x808080), try parseColor("Grey"));
    try expectColor(Color.hex(0xffa500), try parseColor("orange"));
    for ([_][]const u8{ "nonsense", "#12", "#12345", "rgb(1, 2)", "var(missing)", "hsl(10, 20%", "#fff garbage", "" }) |bad| {
        try testing.expectError(error.InvalidColor, parseColor(bad));
    }
}

test "color() adjusters" {
    try expectColor(rgba(1, 0, 0, 0.5), try parseColor("color(red alpha(50%))"));
    try expectColor(rgba(1, 0, 0, 0.25), try parseColor("color(red a(0.25))"));
    try expectColor(rgba(1, 0, 0, 0.4), try parseColor("color(color(red a(0.5)) a(- 10%))"));
    try expectColor(rgba(1, 0, 0, 0.5), try parseColor("color(red a(* 50%))"));
    try expectColor(rgba(1, 0, 0, 0.75), try parseColor("color(#ff000080 a(+0.25))"));
    // blend() keeps the given percentage of the base color.
    try expectColor(rgba(0.25, 0.25, 0.25, 1), try parseColor("color(white blend(black 25%))"));
    try expectColor(rgba(0.5, 0, 0.5, @as(f32, 0x80) / 255), try parseColor("color(#ff000080 blend(#0000ff 50%))"));
    try expectColor(rgba(0.5, 0, 0.5, (@as(f32, 0x80) / 255 + 1) / 2), try parseColor("color(#ff000080 blenda(#0000ff 50% hsl))"));
    try expectColor(rgba(0.5, 0, 0, 1), try parseColor("color(hsl(0, 100%, 50%) l(25%))"));
    try expectColor(rgba(0.8, 0, 0, 1), try parseColor("color(hsl(0, 100%, 50%) lightness(- 10%))"));
    try expectColor(rgba(0.75, 0.25, 0.25, 1), try parseColor("color(red saturation(* 50%))"));
    try expectColor(rgba(0.5, 0.5, 0.5, 1), try parseColor("color(red s(0%))"));
    try expectColor(rgba(1, 0, 0, 0.5), try parseColor("color(red min-contrast(var(bg) 2.5) a(0.5))"));
    try testing.expectError(error.InvalidColor, parseColor("color(red bogus)"));
    try testing.expectError(error.InvalidColor, parseColor("color(red a(0.5)"));
}

test "selector matching" {
    const s = selectorScore;
    try testing.expect(s("string", "string.quoted.double") != null);
    try testing.expect(s("string.quoted", "string.quoted.double").? > s("string", "string.quoted.double").?);
    try testing.expect(s("strings", "string.quoted") == null);
    try testing.expect(s("string.quoted.double.json", "string.quoted.double") == null);
    try testing.expect(s("comment, string", "string.quoted") != null);
    try testing.expect(s("comment | string", "string.quoted") != null);
    try testing.expect(s("keyword - keyword.operator", "keyword.operator.assignment") == null);
    try testing.expect(s("keyword -keyword.operator", "keyword.control") != null);
    try testing.expect(s("(comment | string) - string.quoted.single", "string.quoted.double") != null);
    try testing.expect(s("(comment | string) - string.quoted.single", "string.quoted.single") == null);
    try testing.expect(s("comment.line.double-slash", "comment.line.double-slash") != null);
    // Descendant paths need their ancestors in the stack.
    try testing.expect(s("source string", "source.python string.quoted") != null);
    try testing.expect(s("text string", "source.python string.quoted") == null);
    try testing.expect(s("meta.mapping.key string", "string.quoted") == null);
    try testing.expect(s("source & string", "source.python string.quoted") != null);
    try testing.expect(s("text & string", "source.python string.quoted") == null);
    // Deeper matches beat shallower ones.
    try testing.expect(s("string", "source.python string.quoted").? > s("source.python", "source.python string.quoted").?);
    try testing.expect(s("", "string") == null);
}

const sample_scheme =
    \\// An invented scheme, only for these tests.
    \\{
    \\    "name": "Fallback Name",
    \\    "author": "cs-zig // tests /* not a comment */",
    \\    "variables": {
    \\        "night": "hsl(210, 20%, 12%)",
    \\        "chalk": "#e0e0e0",
    \\        "flame": "var(ember)",
    \\        "ember": "var( spark )",
    \\        "spark": "rgb(255, 128, 0)",
    \\        "mist": "color(var(chalk) alpha(0.5))",
    \\        "loop": "var(loop)",
    \\    },
    \\    "globals": {
    \\        "background": "var(night)",
    \\        "foreground": "var(chalk)",
    \\        "caret": "#f0a",
    \\        "selection": "#33669980",
    \\        "line_highlight": "#fff1", /* #rgba */
    \\        "shadow": "var(loop)",
    \\    },
    \\    "rules": [
    \\        { "scope": "comment", "foreground": "hsla(120, 50%, 50%, 1)" },
    \\        { "scope": "comment.line", "foreground": "rgb(10%, 20%, 30%)" },
    \\        { "scope": "string", "foreground": "var(flame)" },
    \\        { "scope": "meta.mapping.key string", "foreground": "red" },
    \\        { "scope": "constant", "foreground": "orange" },
    \\        { "scope": "constant.numeric", "foreground": "#123" },
    \\        { "scope": "constant.numeric", "foreground": "#456" },
    \\        { "scope": "keyword - keyword.operator, keyword.operator.word", "foreground": "color(#808080 l(+ 10%))" },
    \\        { "scope": "keyword.operator", "foreground": "color(hsl(0, 100%, 50%) s(* 50%))" },
    \\        { "scope": "entity.name - (entity.name.tag | entity.name.label | entity.name.type)", "foreground": "purple" },
    \\        { "scope": "storage.type", "foreground": "yellow", "font_style": "italic" },
    \\        { "scope": "meta.function variable.language", "foreground": "#abcdef" },
    \\        { "scope": "variable.parameter", "background": "#000000" },
    \\        { "scope": "variable.other.member", "foreground": "var(loop)" },
    \\        { "scope": "punctuation.separator | punctuation.terminator", "foreground": ["#010203", "#040506"] },
    \\        { "scope": "entity.name.tag", "foreground": "color(var(mist) a(1))" },
    \\        { "scope": "entity.other", "foreground": "color(var(flame) blend(white 50%))" },
    \\        { "scope": "entity.other.attribute-name", "foreground": "bogus(1)" },
    \\        { "scope": "constant.character.escape", "foreground": "color(var(chalk) a(0.5))" },
    \\    ],
    \\}
;

test "parse a color scheme" {
    var loaded = try parseColorScheme(testing.allocator, sample_scheme, "Dusk");
    defer loaded.deinit(testing.allocator);
    const s = loaded.scheme;
    try testing.expectEqualStrings("Dusk", s.name);
    try testing.expectEqualStrings("", loaded.source);
    try testing.expect(!s.light);
    const bg = rgba(0.096, 0.12, 0.144, 1);
    const chalk = Color.hex(0xe0e0e0);
    try expectColor(bg, s.background);
    try expectColor(chalk, s.foreground);
    try expectColor(rgba(1, 0, @as(f32, 0xaa) / 255, 1), s.caret);
    try expectColor(Color.hexa(0x336699, @as(f32, 0x80) / 255), s.selection);
    try expectColor(rgba(1, 1, 1, @as(f32, 0x11) / 255), s.line_highlight);

    const flame = rgba(1, 128.0 / 255.0, 0, 1);
    const syn = s.syntax;
    try expectColor(chalk, syn.foreground);
    try expectColor(rgba(0.1, 0.2, 0.3, 1), syn.comment); // more specific wins
    try expectColor(flame, syn.string); // var() chain; context-only rule ignored
    try expectColor(Color.hex(0x445566), syn.number); // later rule wins the tie
    try expectColor(Color.hex(0xffa500), syn.constant);
    const gray: f32 = @as(f32, 0x80) / 255 + 0.1;
    try expectColor(rgba(gray, gray, gray, 1), syn.keyword);
    try expectColor(rgba(0.75, 0.25, 0.25, 1), syn.operator); // excluded from "keyword - ..."
    try expectColor(Color.hex(0x800080), syn.function);
    try expectColor(Color.hex(0xffff00), syn.type); // excluded from entity.name, falls back to storage.type
    try expectColor(Color.hex(0xabcdef), syn.variable_builtin); // only a context rule matches
    try expectColor(chalk, syn.parameter); // that rule sets no foreground
    try expectColor(chalk, syn.property); // its only rule's color is a var() cycle
    try expectColor(Color.hex(0x010203), syn.punctuation); // first color of a gradient
    try expectColor(chalk, syn.tag); // color(var(mist) a(1))
    try expectColor(rgba(1, 0.751, 0.5, 1), syn.attribute); // unparsable rule skipped
    try expectColor(Color.mix(bg, chalk, 0.5), syn.escape); // translucent text composited
    try expectColor(chalk, syn.label);

    // theme.build accepts it.
    const built = theme.build(&s);
    try testing.expectEqualStrings("Dusk", built.name);

    var unnamed = try parseColorScheme(testing.allocator, sample_scheme, "");
    defer unnamed.deinit(testing.allocator);
    try testing.expectEqualStrings("Fallback Name", unnamed.scheme.name);
}

test "light and dark, defaults and errors" {
    const gpa = testing.allocator;
    var paper = try parseColorScheme(gpa, "{ \"globals\": { \"background\": \"#fafafa\" } }", "Paper");
    defer paper.deinit(gpa);
    try testing.expect(paper.scheme.light);
    try expectColor(Color.black, paper.scheme.foreground);
    try expectColor(Color.black, paper.scheme.caret);
    try expectColor(Color.black, paper.scheme.syntax.keyword);
    try expectColor(Color.mix(Color.hex(0xfafafa), Color.black, 0.06), paper.scheme.line_highlight);

    var ink = try parseColorScheme(gpa, "{ \"globals\": { \"background\": \"hsl(0, 0%, 45%)\" }, \"rules\": [] }", "Ink");
    defer ink.deinit(gpa);
    try testing.expect(!ink.scheme.light); // luminance ~0.18
    try expectColor(Color.white, ink.scheme.syntax.string);

    try testing.expectError(error.MissingBackground, parseColorScheme(gpa, "{ \"globals\": {} }", "x"));
    try testing.expectError(error.MissingBackground, parseColorScheme(gpa,
        \\{ "variables": { "a": "var(b)", "b": "var(a)" }, "globals": { "background": "var(a)" } }
    , "x"));
    try testing.expectError(error.InvalidColorScheme, parseColorScheme(gpa, "{ \"globals\": ", "x"));
    try testing.expectError(error.InvalidColorScheme, parseColorScheme(gpa, "[1, 2]", "x"));
}

const sample_tmtheme =
    \\<?xml version="1.0" encoding="UTF-8"?>
    \\<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    \\<plist version="1.0">
    \\<dict>
    \\  <key>name</key>
    \\  <string>Invented &amp; Plain</string>
    \\  <key>settings</key>
    \\  <array>
    \\    <dict>
    \\      <key>settings</key>
    \\      <dict>
    \\        <key>background</key><string>#F4F1E8</string>
    \\        <key>foreground</key><string>#222222</string>
    \\        <key>caret</key><string>#AA0000</string>
    \\        <key>lineHighlight</key><string>#00000010</string>
    \\        <key>selection</key><string>#C0D0E0</string>
    \\      </dict>
    \\    </dict>
    \\    <!-- a comment with a <dict> in it -->
    \\    <dict>
    \\      <key>name</key><string>Comment</string>
    \\      <key>scope</key><string>comment</string>
    \\      <key>settings</key>
    \\      <dict><key>fontStyle</key><string>italic</string><key>foreground</key><string>#7A7A7A</string></dict>
    \\    </dict>
    \\    <dict>
    \\      <key>scope</key><string>string, constant.character.escape</string>
    \\      <key>settings</key><dict><key>foreground</key><string>#336633</string></dict>
    \\    </dict>
    \\    <dict>
    \\      <key>scope</key><string>keyword</string>
    \\      <key>settings</key><dict><key>fontStyle</key><string/></dict>
    \\    </dict>
    \\    <dict>
    \\      <key>scope</key><string>constant.numeric</string>
    \\      <key>settings</key><dict><key>foreground</key><string>#AA5500</string><key>bold</key><true/></dict>
    \\    </dict>
    \\  </array>
    \\  <key>uuid</key><string>00000000-0000-0000-0000-000000000000</string>
    \\</dict>
    \\</plist>
;

test "parse a tmTheme" {
    var loaded = try parseTmTheme(testing.allocator, sample_tmtheme, "");
    defer loaded.deinit(testing.allocator);
    const s = loaded.scheme;
    try testing.expectEqualStrings("Invented & Plain", s.name);
    try testing.expect(s.light);
    try expectColor(Color.hex(0xf4f1e8), s.background);
    try expectColor(Color.hex(0x222222), s.foreground);
    try expectColor(Color.hex(0xaa0000), s.caret);
    try expectColor(Color.hexa(0x000000, @as(f32, 0x10) / 255), s.line_highlight);
    try expectColor(Color.hex(0x7a7a7a), s.syntax.comment);
    try expectColor(Color.hex(0x336633), s.syntax.string);
    try expectColor(Color.hex(0x336633), s.syntax.escape);
    try expectColor(Color.hex(0x222222), s.syntax.keyword);
    try expectColor(Color.hex(0xaa5500), s.syntax.number);
    try testing.expectError(error.InvalidColorScheme, parseTmTheme(testing.allocator, "<plist><dict><key>x</key>", ""));
}

const TestZipEntry = struct { name: []const u8, data: []const u8, deflate: bool = false };

fn deflateRaw(gpa: Allocator, data: []const u8) ![]u8 {
    const flate = std.compress.flate;
    var out: Io.Writer.Allocating = try .initCapacity(gpa, 64);
    defer out.deinit();
    const window = try gpa.alloc(u8, flate.max_window_len);
    defer gpa.free(window);
    const compress = try gpa.create(flate.Compress);
    defer gpa.destroy(compress);
    compress.* = try .init(&out.writer, window, .raw, .default);
    try compress.writer.writeAll(data);
    try compress.finish();
    return out.toOwnedSlice();
}

fn buildTestZip(gpa: Allocator, entries: []const TestZipEntry) ![]u8 {
    var out: Io.Writer.Allocating = .init(gpa);
    defer out.deinit();
    var central: Io.Writer.Allocating = .init(gpa);
    defer central.deinit();
    for (entries) |e| {
        const offset: u32 = @intCast(out.written().len);
        const payload = if (e.deflate) try deflateRaw(gpa, e.data) else try gpa.dupe(u8, e.data);
        defer gpa.free(payload);
        const method: std.zip.CompressionMethod = if (e.deflate) .deflate else .store;
        const crc = std.hash.Crc32.hash(e.data);
        try out.writer.writeStruct(std.zip.LocalFileHeader{
            .signature = std.zip.local_file_header_sig,
            .version_needed_to_extract = 20,
            .flags = @bitCast(@as(u16, 0)),
            .compression_method = method,
            .last_modification_time = 0,
            .last_modification_date = 0,
            .crc32 = crc,
            .compressed_size = @intCast(payload.len),
            .uncompressed_size = @intCast(e.data.len),
            .filename_len = @intCast(e.name.len),
            .extra_len = 0,
        }, .little);
        try out.writer.writeAll(e.name);
        try out.writer.writeAll(payload);
        try central.writer.writeStruct(std.zip.CentralDirectoryFileHeader{
            .signature = std.zip.central_file_header_sig,
            .version_made_by = 20,
            .version_needed_to_extract = 20,
            .flags = @bitCast(@as(u16, 0)),
            .compression_method = method,
            .last_modification_time = 0,
            .last_modification_date = 0,
            .crc32 = crc,
            .compressed_size = @intCast(payload.len),
            .uncompressed_size = @intCast(e.data.len),
            .filename_len = @intCast(e.name.len),
            .extra_len = 0,
            .comment_len = 0,
            .disk_number = 0,
            .internal_file_attributes = 0,
            .external_file_attributes = 0,
            .local_file_header_offset = offset,
        }, .little);
        try central.writer.writeAll(e.name);
    }
    const cd_offset: u32 = @intCast(out.written().len);
    try out.writer.writeAll(central.written());
    try out.writer.writeStruct(std.zip.EndRecord{
        .signature = std.zip.end_record_sig,
        .disk_number = 0,
        .central_directory_disk_number = 0,
        .record_count_disk = @intCast(entries.len),
        .record_count_total = @intCast(entries.len),
        .central_directory_size = @intCast(central.written().len),
        .central_directory_offset = cd_offset,
        .comment_len = 0,
    }, .little);
    return out.toOwnedSlice();
}

test "discover schemes in packages and folders" {
    const gpa = testing.allocator;
    const io = testing.io;
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "pkgs");
    try tmp.dir.createDirPath(io, "unpacked/Base/nested");
    try tmp.dir.createDirPath(io, "unpacked/User");
    try tmp.dir.createDirPath(io, "mine");

    // A package with a deflated and a stored scheme, a broken one and a non-scheme file.
    const base_zip = try buildTestZip(gpa, &.{
        .{ .name = "README.md", .data = "not a scheme" },
        .{ .name = "Dusk.sublime-color-scheme", .data = sample_scheme, .deflate = true },
        .{ .name = "schemes/Paper.sublime-color-scheme", .data = "{ \"globals\": { \"background\": \"#fafafa\", \"foreground\": \"#101010\" } }" },
        .{ .name = "Broken.sublime-color-scheme", .data = "{ \"globals\": { \"background\": " },
        .{ .name = "Classic.tmTheme", .data = sample_tmtheme, .deflate = true },
    });
    defer gpa.free(base_zip);
    try tmp.dir.writeFile(io, .{ .sub_path = "pkgs/Base.sublime-package", .data = base_zip });
    // Another package adds rules to Dusk (merged, not replaced).
    const addon_zip = try buildTestZip(gpa, &.{
        .{ .name = "Extras/Dusk.sublime-color-scheme", .data = "{ \"rules\": [ { \"scope\": \"keyword.control\", \"foreground\": \"#0000ff\" } ] }" },
    });
    defer gpa.free(addon_zip);
    try tmp.dir.writeFile(io, .{ .sub_path = "pkgs/Zeta Addon.sublime-package", .data = addon_zip });
    try tmp.dir.writeFile(io, .{ .sub_path = "pkgs/Corrupt.sublime-package", .data = "PK\x03\x04 definitely not a zip" });
    // An unpacked override of Base's Classic.tmTheme replaces it; User comes last.
    const dark_classic = try std.mem.replaceOwned(u8, gpa, sample_tmtheme, "#F4F1E8", "#101010");
    defer gpa.free(dark_classic);
    try tmp.dir.writeFile(io, .{ .sub_path = "unpacked/Base/Classic.tmTheme", .data = dark_classic });
    // The user's own folders, merged over the package's Paper.
    try tmp.dir.writeFile(io, .{ .sub_path = "unpacked/User/Paper.sublime-color-scheme", .data = "{ \"globals\": { \"caret\": \"#ff0000\" } }" });
    try tmp.dir.writeFile(io, .{ .sub_path = "mine/Paper.sublime-color-scheme", .data = "{ \"globals\": { \"background\": \"#202020\" } }" });
    try tmp.dir.writeFile(io, .{ .sub_path = "mine/notes.txt", .data = "ignored" });

    const root = try std.fmt.allocPrint(gpa, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    defer gpa.free(root);
    var arena_state: std.heap.ArenaAllocator = .init(gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();
    const paths = [_]SearchPath{
        .{ .path = "/nonexistent/cs-zig-test", .kind = .packages },
        .{ .path = try join(arena, root, "pkgs"), .kind = .packages },
        .{ .path = try join(arena, root, "unpacked"), .kind = .unpacked },
        .{ .path = try join(arena, root, "mine"), .kind = .schemes },
    };
    var list = try discoverIn(gpa, io, &paths);
    defer deinitList(gpa, &list);

    try testing.expectEqual(@as(usize, 3), list.items.len);
    try testing.expectEqualStrings("Classic", list.items[0].scheme.name);
    try testing.expectEqualStrings("Dusk", list.items[1].scheme.name);
    try testing.expectEqualStrings("Paper", list.items[2].scheme.name);

    const dusk = find(list.items, "dusk").?;
    try testing.expect(std.mem.endsWith(u8, dusk.source, "pkgs/Base.sublime-package:/Dusk.sublime-color-scheme"));
    try expectColor(Color.hex(0x0000ff), dusk.scheme.syntax.keyword); // from the add-on
    try expectColor(rgba(1, 128.0 / 255.0, 0, 1), dusk.scheme.syntax.string); // from the base

    const classic = find(list.items, "Classic").?;
    try testing.expect(!classic.scheme.light); // the unpacked override won
    try testing.expect(std.mem.endsWith(u8, classic.source, "unpacked/Base/Classic.tmTheme"));

    const paper = find(list.items, "Paper").?;
    try expectColor(Color.hex(0x202020), paper.scheme.background);
    try expectColor(Color.hex(0x101010), paper.scheme.foreground);
    try expectColor(Color.hex(0xff0000), paper.scheme.caret);
    try testing.expect(!paper.scheme.light);
    try testing.expect(std.mem.endsWith(u8, paper.source, "mine/Paper.sublime-color-scheme"));
    try testing.expect(find(list.items, "Broken") == null);
}

test "discover the installed Sublime Text schemes" {
    const io = testing.io;
    Io.Dir.cwd().access(io, "/opt/sublime_text/Packages/Color Scheme - Default.sublime-package", .{}) catch
        return error.SkipZigTest;
    var list = try discover(testing.allocator, io);
    defer deinitList(testing.allocator, &list);
    const breakers = find(list.items, "Breakers") orelse return error.TestExpectedBreakers;
    const mariana = find(list.items, "Mariana") orelse return error.TestExpectedMariana;
    try testing.expect(breakers.scheme.light);
    try testing.expect(!mariana.scheme.light);
    try testing.expect(std.mem.endsWith(u8, breakers.source, "Breakers.sublime-color-scheme"));
    for ([_]*const LoadedScheme{ breakers, mariana }) |l| {
        const s = &l.scheme;
        // Real schemes color strings and comments differently from plain text.
        try testing.expect(!std.meta.eql(s.syntax.string, s.foreground));
        try testing.expect(!std.meta.eql(s.syntax.comment, s.foreground));
        try testing.expect(!std.meta.eql(s.syntax.string, s.syntax.comment));
        try testing.expectEqual(@as(f32, 1), s.background.a);
    }
}
