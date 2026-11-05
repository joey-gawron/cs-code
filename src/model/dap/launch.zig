//! launch.json: VS Code's debug configurations (JSON with comments), their
//! `${…}` variables, and the adapter a configuration's `type` runs.
//!
//! The file is `.cs-zig/launch.json`, else `.vscode/launch.json`. Without
//! one, a configuration is guessed from what the project has built.

const std = @import("std");
const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const Config = struct {
    name: []const u8,
    /// "lldb", "lldb-dap", "codelldb" or "dap" (with an `adapter` command).
    type: []const u8,
    /// "launch" or "attach".
    request: []const u8,
    /// An `adapter` command the configuration names, replacing the type's.
    adapter: ?[]const []const u8,
    /// The configuration with its variables resolved, as the arguments of
    /// the launch or attach request.
    arguments_json: []const u8,
    /// The `program` it runs, for the toolbar and messages.
    program: ?[]const u8,
};

pub const Set = struct {
    arena: std.heap.ArenaAllocator,
    configs: []const Config,
    /// Where they came from; null when guessed.
    source: ?[]const u8 = null,

    pub fn deinit(self: *Set) void {
        self.arena.deinit();
    }
};

pub const Vars = struct {
    workspace_folder: []const u8,
    /// The active file, for ${file} and its parts.
    file: ?[]const u8 = null,
};

// ---- JSONC ----------------------------------------------------------------------

/// `text` without // and /* */ comments and without trailing commas.
pub fn stripJsonc(gpa: Allocator, text: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var i: usize = 0;
    while (i < text.len) {
        const ch = text[i];
        if (ch == '"') {
            const start = i;
            i += 1;
            while (i < text.len and text[i] != '"') : (i += 1) {
                if (text[i] == '\\' and i + 1 < text.len) i += 1;
            }
            i = @min(i + 1, text.len);
            try out.appendSlice(gpa, text[start..i]);
        } else if (ch == '/' and i + 1 < text.len and text[i + 1] == '/') {
            while (i < text.len and text[i] != '\n') i += 1;
        } else if (ch == '/' and i + 1 < text.len and text[i + 1] == '*') {
            i += 2;
            while (i + 1 < text.len and !(text[i] == '*' and text[i + 1] == '/')) i += 1;
            i = @min(i + 2, text.len);
        } else if (ch == ',') {
            // A comma before a closing bracket is dropped.
            var j = i + 1;
            while (j < text.len) {
                if (std.ascii.isWhitespace(text[j])) {
                    j += 1;
                } else if (text[j] == '/' and j + 1 < text.len and (text[j + 1] == '/' or text[j + 1] == '*')) {
                    if (text[j + 1] == '/') {
                        while (j < text.len and text[j] != '\n') j += 1;
                    } else {
                        j += 2;
                        while (j + 1 < text.len and !(text[j] == '*' and text[j + 1] == '/')) j += 1;
                        j = @min(j + 2, text.len);
                    }
                } else break;
            }
            if (j < text.len and (text[j] == '}' or text[j] == ']')) {
                i += 1;
            } else {
                try out.append(gpa, ch);
                i += 1;
            }
        } else {
            try out.append(gpa, ch);
            i += 1;
        }
    }
    return out.toOwnedSlice(gpa);
}

// ---- Variables ------------------------------------------------------------------

/// `s` with ${workspaceFolder}, ${file} and friends, ${env:NAME} and ${cwd}
/// replaced; unknown variables stay as written.
pub fn resolve(gpa: Allocator, s: []const u8, vars: Vars) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var i: usize = 0;
    while (i < s.len) {
        if (s[i] == '$' and i + 1 < s.len and s[i + 1] == '{') {
            if (std.mem.indexOfScalarPos(u8, s, i + 2, '}')) |end| {
                const name = s[i + 2 .. end];
                if (try expand(gpa, name, vars)) |value| {
                    defer gpa.free(value);
                    try out.appendSlice(gpa, value);
                    i = end + 1;
                    continue;
                }
            }
        }
        try out.append(gpa, s[i]);
        i += 1;
    }
    return out.toOwnedSlice(gpa);
}

fn expand(gpa: Allocator, name: []const u8, vars: Vars) !?[]u8 {
    const eql = std.mem.eql;
    if (eql(u8, name, "workspaceFolder") or eql(u8, name, "cwd")) return try gpa.dupe(u8, vars.workspace_folder);
    if (eql(u8, name, "workspaceFolderBasename")) return try gpa.dupe(u8, std.fs.path.basename(vars.workspace_folder));
    const file = vars.file orelse "";
    if (eql(u8, name, "file")) return try gpa.dupe(u8, file);
    if (eql(u8, name, "fileBasename")) return try gpa.dupe(u8, std.fs.path.basename(file));
    if (eql(u8, name, "fileDirname")) return try gpa.dupe(u8, std.fs.path.dirname(file) orelse "");
    if (eql(u8, name, "fileBasenameNoExtension")) return try gpa.dupe(u8, std.fs.path.stem(file));
    if (std.mem.startsWith(u8, name, "env:")) {
        const key = try gpa.dupeSentinel(u8, name[4..], 0);
        defer gpa.free(key);
        const v = std.c.getenv(key) orelse return try gpa.dupe(u8, "");
        return try gpa.dupe(u8, std.mem.span(v));
    }
    return null;
}

fn resolveValue(arena: Allocator, v: std.json.Value, vars: Vars) !std.json.Value {
    switch (v) {
        .string => |s| return .{ .string = try resolve(arena, s, vars) },
        .array => |a| {
            var out: std.json.Array = .init(arena);
            for (a.items) |item| try out.append(try resolveValue(arena, item, vars));
            return .{ .array = out };
        },
        .object => |o| {
            var out: std.json.ObjectMap = .empty;
            var it = o.iterator();
            while (it.next()) |e| try out.put(arena, e.key_ptr.*, try resolveValue(arena, e.value_ptr.*, vars));
            return .{ .object = out };
        },
        else => return v,
    }
}

// ---- Parsing --------------------------------------------------------------------

fn str(v: ?std.json.Value) ?[]const u8 {
    const val = v orelse return null;
    return if (val == .string) val.string else null;
}

/// The configurations of a launch.json's text.
pub fn parse(gpa: Allocator, text: []const u8, vars: Vars) !Set {
    var set: Set = .{ .arena = .init(gpa), .configs = &.{} };
    errdefer set.arena.deinit();
    const arena = set.arena.allocator();
    const clean = try stripJsonc(gpa, text);
    defer gpa.free(clean);
    const parsed = std.json.parseFromSlice(std.json.Value, gpa, clean, .{}) catch return error.BadLaunchJson;
    defer parsed.deinit();
    if (parsed.value != .object) return error.BadLaunchJson;
    const list = parsed.value.object.get("configurations") orelse return error.BadLaunchJson;
    if (list != .array) return error.BadLaunchJson;

    var configs: std.ArrayList(Config) = .empty;
    for (list.array.items) |item| {
        if (item != .object) continue;
        const resolved = try resolveValue(arena, item, vars);
        const o = resolved.object;
        const name = str(o.get("name")) orelse continue;
        var adapter: ?[]const []const u8 = null;
        if (o.get("adapter")) |a| if (a == .array) {
            var argv: std.ArrayList([]const u8) = .empty;
            for (a.array.items) |x| if (x == .string) try argv.append(arena, x.string);
            if (argv.items.len > 0) adapter = try argv.toOwnedSlice(arena);
        };
        var args = o;
        _ = args.orderedRemove("adapter");
        try configs.append(arena, .{
            .name = name,
            .type = str(o.get("type")) orelse "lldb",
            .request = str(o.get("request")) orelse "launch",
            .adapter = adapter,
            .arguments_json = try std.json.Stringify.valueAlloc(arena, std.json.Value{ .object = args }, .{}),
            .program = str(o.get("program")),
        });
    }
    set.configs = try configs.toOwnedSlice(arena);
    return set;
}

/// The folder's launch.json, or a guessed configuration when it has none.
/// `error.BadLaunchJson` when the file is there but unusable.
pub fn load(gpa: Allocator, io: Io, root: []const u8, vars: Vars) !Set {
    const candidates = [_][]const u8{ ".cs-zig/launch.json", ".vscode/launch.json" };
    for (candidates) |rel| {
        const path = try std.fs.path.join(gpa, &.{ root, rel });
        defer gpa.free(path);
        const text = std.Io.Dir.cwd().readFileAlloc(io, path, gpa, .limited(1 << 20)) catch continue;
        defer gpa.free(text);
        var set = try parse(gpa, text, vars);
        set.source = try set.arena.allocator().dupe(u8, path);
        return set;
    }
    return guess(gpa, io, root);
}

/// A configuration for the program the project builds, if one is there.
pub fn guess(gpa: Allocator, io: Io, root: []const u8) !Set {
    var set: Set = .{ .arena = .init(gpa), .configs = &.{} };
    errdefer set.arena.deinit();
    const arena = set.arena.allocator();
    const name = std.fs.path.basename(root);
    const candidates = [_][]const u8{ "zig-out/bin/{s}", "target/debug/{s}", "build/{s}", "{s}", "a.out" };
    for (candidates) |pattern| {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const rel_path = if (std.mem.indexOf(u8, pattern, "{s}")) |at|
            try std.fmt.bufPrint(&buf, "{s}{s}{s}", .{ pattern[0..at], name, pattern[at + 3 ..] })
        else
            pattern;
        const full = try std.fs.path.join(arena, &.{ root, rel_path });
        const stat = std.Io.Dir.cwd().statFile(io, full, .{}) catch continue;
        if (stat.kind != .file) continue;
        const json = try std.json.Stringify.valueAlloc(arena, .{
            .type = "lldb",
            .request = "launch",
            .name = try std.fmt.allocPrint(arena, "Debug {s}", .{std.fs.path.basename(full)}),
            .program = full,
            .cwd = root,
            .args = [0][]const u8{},
        }, .{});
        const configs = try arena.alloc(Config, 1);
        configs[0] = .{
            .name = try std.fmt.allocPrint(arena, "Debug {s}", .{std.fs.path.basename(full)}),
            .type = "lldb",
            .request = "launch",
            .adapter = null,
            .arguments_json = json,
            .program = full,
        };
        set.configs = configs;
        return set;
    }
    return set;
}

// ---- Adapters -------------------------------------------------------------------

const lldb_dap_candidates = [_][]const u8{
    "/Library/Developer/CommandLineTools/usr/bin/lldb-dap",
    "/Applications/Xcode.app/Contents/Developer/usr/bin/lldb-dap",
    "/opt/homebrew/opt/llvm/bin/lldb-dap",
    "/usr/local/opt/llvm/bin/lldb-dap",
    "/usr/bin/lldb-dap",
    "/usr/local/bin/lldb-dap",
    "/opt/homebrew/bin/lldb-dap",
};

fn executable(io: Io, path: []const u8) bool {
    std.Io.Dir.cwd().access(io, path, .{}) catch return false;
    return true;
}

/// lldb-dap: on $PATH, else where Xcode, the Command Line Tools or
/// Homebrew's llvm put it. Owned by the caller.
pub fn findLldbDap(gpa: Allocator, io: Io) !?[]u8 {
    if (std.c.getenv("PATH")) |p| {
        var it = std.mem.tokenizeScalar(u8, std.mem.span(p), ':');
        while (it.next()) |dir| {
            const full = try std.fs.path.join(gpa, &.{ dir, "lldb-dap" });
            if (executable(io, full)) return full;
            gpa.free(full);
        }
    }
    for (lldb_dap_candidates) |c| if (executable(io, c)) return try gpa.dupe(u8, c);
    return null;
}

/// The command that runs the adapter for `config` (argv owned by the caller).
pub fn adapterArgv(gpa: Allocator, io: Io, config: Config) ![]const []const u8 {
    if (config.adapter) |a| {
        const argv = try gpa.alloc([]const u8, a.len);
        errdefer gpa.free(argv);
        for (a, 0..) |s, i| argv[i] = try gpa.dupe(u8, s);
        return argv;
    }
    const known = [_][]const u8{ "lldb", "lldb-dap", "codelldb" };
    for (known) |t| if (std.mem.eql(u8, config.type, t)) {
        const path = (try findLldbDap(gpa, io)) orelse return error.AdapterNotFound;
        const argv = try gpa.alloc([]const u8, 1);
        argv[0] = path;
        return argv;
    };
    return error.AdapterNotFound;
}

pub fn freeArgv(gpa: Allocator, argv: []const []const u8) void {
    for (argv) |s| gpa.free(s);
    gpa.free(argv);
}

const testing = std.testing;

test "comments and trailing commas are stripped, strings are left alone" {
    const gpa = testing.allocator;
    const out = try stripJsonc(gpa,
        \\{ // a comment
        \\  "url": "http://x//y", /* block */
        \\  "list": [1, 2, ],
        \\}
    );
    defer gpa.free(out);
    const parsed = try std.json.parseFromSlice(std.json.Value, gpa, out, .{});
    defer parsed.deinit();
    try testing.expectEqualStrings("http://x//y", parsed.value.object.get("url").?.string);
    try testing.expectEqual(@as(usize, 2), parsed.value.object.get("list").?.array.items.len);
}

test "variables resolve and unknown ones stay" {
    const gpa = testing.allocator;
    const vars: Vars = .{ .workspace_folder = "/w/proj", .file = "/w/proj/src/main.c" };
    const out = try resolve(gpa, "${workspaceFolder}/bin/${fileBasenameNoExtension} ${nope} ${fileDirname}", vars);
    defer gpa.free(out);
    try testing.expectEqualStrings("/w/proj/bin/main ${nope} /w/proj/src", out);
}

test "configurations parse with variables resolved and the adapter key removed" {
    const gpa = testing.allocator;
    var set = try parse(gpa,
        \\{
        \\  "version": "0.2.0",
        \\  "configurations": [
        \\    { "name": "Run it", "type": "lldb", "request": "launch",
        \\      "program": "${workspaceFolder}/a.out", "args": ["-v"], },
        \\    { "name": "Custom", "type": "dap", "request": "launch", "adapter": ["my-dap", "--stdio"] },
        \\  ]
        \\}
    , .{ .workspace_folder = "/w/proj" });
    defer set.deinit();
    try testing.expectEqual(@as(usize, 2), set.configs.len);
    try testing.expectEqualStrings("Run it", set.configs[0].name);
    try testing.expectEqualStrings("/w/proj/a.out", set.configs[0].program.?);
    try testing.expect(std.mem.indexOf(u8, set.configs[0].arguments_json, "/w/proj/a.out") != null);
    try testing.expectEqualStrings("my-dap", set.configs[1].adapter.?[0]);
    try testing.expect(std.mem.indexOf(u8, set.configs[1].arguments_json, "adapter") == null);
}

test "a file that is not a launch.json is an error" {
    try testing.expectError(error.BadLaunchJson, parse(testing.allocator, "[1]", .{ .workspace_folder = "/" }));
    try testing.expectError(error.BadLaunchJson, parse(testing.allocator, "{ \"configurations\": 3 }", .{ .workspace_folder = "/" }));
}
