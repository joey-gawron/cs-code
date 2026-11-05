//! Edits keymap.json for the Keyboard Shortcuts page. Every entry written
//! here is one line ending in a `// cs-zig:<command>` marker, so changing or
//! resetting a command's keys removes exactly the lines this file put there
//! and leaves anything typed by hand alone.

const std = @import("std");
const keymap_mod = @import("keymap.zig");
const platform = @import("../../framework/platform/platform.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Chord = keymap_mod.Chord;

const marker = "// cs-zig:";

/// The text keymap.json takes for `chord` ("ctrl+shift+p", "pageup", ...).
pub fn chordSpec(buf: []u8, chord: Chord) []const u8 {
    var w: Io.Writer = .fixed(buf);
    // The Mac matches Command as Ctrl (see Keymap.match), so a recorded
    // Command shortcut is written as ctrl.
    const ctrl = chord.mods.ctrl or (@import("builtin").os.tag.isDarwin() and chord.mods.super);
    if (ctrl) w.writeAll("ctrl+") catch {};
    if (chord.mods.alt) w.writeAll("alt+") catch {};
    if (chord.mods.shift) w.writeAll("shift+") catch {};
    if (chord.mods.super and !@import("builtin").os.tag.isDarwin()) w.writeAll("super+") catch {};
    switch (chord.key) {
        .char => |cp| switch (cp) {
            ' ' => w.writeAll("space") catch {},
            '+' => w.writeAll("plus") catch {},
            '-' => w.writeAll("minus") catch {},
            else => {
                var tmp: [4]u8 = undefined;
                const n = std.unicode.utf8Encode(if (cp < 128) std.ascii.toLower(@intCast(cp)) else cp, &tmp) catch 0;
                w.writeAll(tmp[0..n]) catch {};
            },
        },
        .named => |n| w.writeAll(switch (n) {
            .page_up => "pageup",
            .page_down => "pagedown",
            .context_menu => "menu",
            inline else => |tag| @tagName(tag),
        }) catch {},
    }
    return w.buffered();
}

fn isMarked(line: []const u8, command: []const u8) bool {
    const at = std.mem.lastIndexOf(u8, line, marker) orelse return false;
    return std.mem.eql(u8, std.mem.trim(u8, line[at + marker.len ..], " \t\r"), command);
}

/// `raw` without the lines written for `command`.
pub fn removeCommand(gpa: Allocator, raw: []const u8, command: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var it = std.mem.splitScalar(u8, raw, '\n');
    var first = true;
    while (it.next()) |line| {
        if (isMarked(line, command)) continue;
        if (!first) try out.append(gpa, '\n');
        first = false;
        try out.appendSlice(gpa, line);
    }
    return out.toOwnedSlice(gpa);
}

/// `raw` with `entries` (one line each) added at the end of its array.
fn appendEntries(gpa: Allocator, raw: []const u8, entries: []const []const u8) ![]u8 {
    var at: ?usize = null;
    var pos: usize = 0;
    while (pos < raw.len) {
        const end = std.mem.indexOfScalarPos(u8, raw, pos, '\n') orelse raw.len;
        if (std.mem.eql(u8, std.mem.trim(u8, raw[pos..end], " \t\r"), "]")) {
            at = pos;
            break;
        }
        pos = end + 1;
    }
    const insert_at = at orelse return error.UnsupportedKeymapLayout;
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    try out.appendSlice(gpa, raw[0..insert_at]);
    for (entries) |e| {
        try out.appendSlice(gpa, e);
        try out.append(gpa, '\n');
    }
    try out.appendSlice(gpa, raw[insert_at..]);
    return out.toOwnedSlice(gpa);
}

/// `raw` with `command` bound to `chord` instead of what it had: the lines
/// written for it earlier go, `unbind` (the chords it has by default) are
/// unbound, and `chord` is bound to it.
pub fn bindCommand(gpa: Allocator, raw: []const u8, command: []const u8, chord: []const u8, unbind: []const []const u8) ![]u8 {
    const cleared = try removeCommand(gpa, raw, command);
    defer gpa.free(cleared);
    var lines: std.ArrayList([]u8) = .empty;
    defer {
        for (lines.items) |l| gpa.free(l);
        lines.deinit(gpa);
    }
    try lines.append(gpa, try std.fmt.allocPrint(gpa, "    {{ \"keys\": [\"{s}\"], \"command\": \"{s}\" }}, {s}{s}", .{ chord, command, marker, command }));
    for (unbind) |old| {
        if (std.mem.eql(u8, old, chord)) continue;
        try lines.append(gpa, try std.fmt.allocPrint(gpa, "    {{ \"keys\": [\"{s}\"], \"command\": null }}, {s}{s}", .{ old, marker, command }));
    }
    return appendEntries(gpa, cleared, lines.items);
}

fn readOrTemplate(gpa: Allocator, io: Io) ![]u8 {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = keymap_mod.Keymap.userPath(&buf) orelse return error.NoHome;
    return Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(1 << 20)) catch |err| switch (err) {
        error.FileNotFound => keymap_mod.Keymap.template(gpa),
        else => err,
    };
}

fn write(io: Io, text: []const u8) !void {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = keymap_mod.Keymap.userPath(&buf) orelse return error.NoHome;
    if (std.fs.path.dirname(p)) |dir| try Io.Dir.cwd().createDirPath(io, dir);
    try Io.Dir.cwd().writeFile(io, .{ .sub_path = p, .data = text });
}

/// Binds `command` to `chord` in the user's keymap.json (creating it).
pub fn bind(gpa: Allocator, io: Io, command: []const u8, chord: []const u8, unbind: []const []const u8) !void {
    const raw = try readOrTemplate(gpa, io);
    defer gpa.free(raw);
    const edited = try bindCommand(gpa, raw, command, chord, unbind);
    defer gpa.free(edited);
    try write(io, edited);
}

/// Gives `command` its default keys back.
pub fn reset(gpa: Allocator, io: Io, command: []const u8) !void {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = keymap_mod.Keymap.userPath(&buf) orelse return;
    const raw = Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(1 << 20)) catch |err| switch (err) {
        error.FileNotFound => return,
        else => return err,
    };
    defer gpa.free(raw);
    const edited = try removeCommand(gpa, raw, command);
    defer gpa.free(edited);
    try write(io, edited);
}

test "chords become what keymap.json parses" {
    var buf: [64]u8 = undefined;
    for ([_][]const u8{ "ctrl+shift+p", "alt+left", "pageup", "f5", "ctrl+space", "ctrl+plus", "ctrl+minus", "shift+tab", "ctrl+=" }) |spec| {
        const chord = try Chord.parse(spec);
        try std.testing.expectEqualStrings(spec, chordSpec(&buf, chord));
    }
}

test "binding replaces the lines written before, and reset removes them" {
    const gpa = std.testing.allocator;
    const template = try keymap_mod.Keymap.template(gpa);
    defer gpa.free(template);

    const one = try bindCommand(gpa, template, "unique_lines", "f7", &.{"f9"});
    defer gpa.free(one);
    const two = try bindCommand(gpa, one, "unique_lines", "ctrl+alt+s", &.{"f9"});
    defer gpa.free(two);
    try std.testing.expect(std.mem.indexOf(u8, two, "\"f7\"") == null);
    try std.testing.expect(std.mem.indexOf(u8, two, "\"ctrl+alt+s\"") != null);
    try std.testing.expectEqual(@as(usize, 2), std.mem.count(u8, two, "// cs-zig:unique_lines"));

    // Another command's lines stay.
    const other = try bindCommand(gpa, two, "reverse_lines", "f6", &.{});
    defer gpa.free(other);
    const reset_text = try removeCommand(gpa, other, "unique_lines");
    defer gpa.free(reset_text);
    try std.testing.expect(std.mem.indexOf(u8, reset_text, "\"command\": \"unique_lines\"") == null);
    try std.testing.expect(std.mem.indexOf(u8, reset_text, "\"command\": \"reverse_lines\"") != null);

    // What was written loads back as bindings.
    var km = try keymap_mod.Keymap.initDefault(gpa);
    defer km.deinit(gpa);
    try km.addUserEntries(gpa, other);
    try std.testing.expectEqual(@as(usize, 3), km.user_count);
    var found = false;
    for (km.bindings.items[0..km.user_count]) |b| {
        if (b.action == .unique_lines and b.chords[0].?.eql(try Chord.parse("ctrl+alt+s"))) found = true;
    }
    try std.testing.expect(found);
}

test "a keymap without a closing bracket line is refused" {
    try std.testing.expectError(error.UnsupportedKeymapLayout, bindCommand(std.testing.allocator, "[ ]", "unique_lines", "f5", &.{}));
}
