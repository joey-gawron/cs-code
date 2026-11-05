//! New Project templates: the generators the New Project page lists, the
//! name rules of their toolchains, and the code that writes a starter
//! project. Files are written directly (no `cargo init` or `dotnet new`), so
//! a project can be made without its toolchain installed; the toolchain is
//! only checked (off the main thread) so the page can show its version, and
//! asked for the optional steps (a Python venv, the first Git commit).

const std = @import("std");
const git = @import("../git/git.zig");
const executor_mod = @import("../../framework/util/executor.zig");
const Executor = executor_mod.Executor;
const Completion = executor_mod.Completion;

const Allocator = std.mem.Allocator;
const Io = std.Io;

pub const max_options = 4;

pub const Option = struct {
    key: []const u8,
    label: []const u8,
    /// A line under the drop-down saying what the choice writes.
    hint: []const u8 = "",
    choices: []const []const u8,
    default: u8 = 0,
};

/// A command that prints the toolchain's version, and what to do without it.
pub const Tool = struct {
    argv: []const []const u8,
    install: []const u8,
};

pub const Generator = struct {
    id: []const u8,
    name: []const u8,
    tagline: []const u8,
    /// Two or three characters drawn in the list's badge.
    badge: []const u8,
    tool: ?Tool = null,
    options: []const Option = &.{},
    name_rule: *const fn (name: []const u8) ?[]const u8,
};

pub const Id = enum { empty, python, rust, c, cpp, csharp, zig };

pub const generators = [_]Generator{
    .{ .id = "empty", .name = "Empty Project", .tagline = "A folder with a README, .editorconfig and .gitignore", .badge = "[ ]", .name_rule = portableName },
    .{
        .id = "python",
        .name = "Python",
        .tagline = "pyproject.toml (src layout, Ruff), .venv and pytest",
        .badge = "py",
        .tool = .{ .argv = &.{ "python3", "--version" }, .install = "brew install python" },
        .options = &.{
            .{ .key = "kind", .label = "Type", .choices = &.{ "Application", "Package" } },
            .{ .key = "venv", .label = "Virtual environment", .hint = "python3 -m venv .venv", .choices = &.{ "Create .venv", "None" } },
            .{ .key = "sample", .label = "Sample code", .choices = &.{ "Add sample code", "Entry point only" } },
        },
        .name_rule = pythonName,
    },
    .{
        .id = "rust",
        .name = "Rust",
        .tagline = "A cargo package: binary or library",
        .badge = "Rs",
        .tool = .{ .argv = &.{ "cargo", "--version" }, .install = "brew install rust" },
        .options = &.{
            .{ .key = "kind", .label = "Type", .hint = "src/main.rs or src/lib.rs", .choices = &.{ "Binary (application)", "Library" } },
            .{ .key = "edition", .label = "Edition", .choices = &.{ "2024", "2021", "2018" } },
        },
        .name_rule = rustName,
    },
    .{
        .id = "c",
        .name = "C",
        .tagline = "CMake project with CTest",
        .badge = "C",
        .tool = .{ .argv = &.{ "cmake", "--version" }, .install = "brew install cmake" },
        .options = &.{
            .{ .key = "kind", .label = "Type", .choices = &.{ "Executable", "Library" } },
            .{ .key = "std", .label = "Standard", .choices = &.{ "C17", "C11", "C23" } },
            .{ .key = "tests", .label = "Tests", .choices = &.{ "CTest", "None" } },
        },
        .name_rule = cmakeName,
    },
    .{
        .id = "cpp",
        .name = "C++",
        .tagline = "CMake project with GoogleTest or Catch2",
        .badge = "C++",
        .tool = .{ .argv = &.{ "cmake", "--version" }, .install = "brew install cmake" },
        .options = &.{
            .{ .key = "kind", .label = "Type", .choices = &.{ "Executable", "Library" } },
            .{ .key = "std", .label = "Standard", .choices = &.{ "C++20", "C++17", "C++23" } },
            .{ .key = "tests", .label = "Tests", .hint = "GoogleTest and Catch2 are fetched at the first configure", .choices = &.{ "CTest only", "GoogleTest", "Catch2", "None" } },
        },
        .name_rule = cmakeName,
    },
    .{
        .id = "csharp",
        .name = "C#",
        .tagline = ".NET solution with a console app or library and xUnit",
        .badge = "C#",
        .tool = .{ .argv = &.{ "dotnet", "--version" }, .install = "brew install --cask dotnet-sdk" },
        .options = &.{
            .{ .key = "kind", .label = "Template", .choices = &.{ "Console App", "Class Library" } },
            .{ .key = "tfm", .label = "Framework", .choices = &.{ "net10.0", "net9.0", "net8.0" } },
            .{ .key = "sln", .label = "Solution format", .choices = &.{ ".slnx", ".sln" } },
            .{ .key = "tests", .label = "Tests", .choices = &.{ "xUnit test project", "None" } },
        },
        .name_rule = csharpName,
    },
    .{
        .id = "zig",
        .name = "Zig",
        .tagline = "An executable and a library module, as `zig init` makes",
        .badge = "Zig",
        .tool = .{ .argv = &.{ "zig", "version" }, .install = "brew install zig" },
        .name_rule = zigName,
    },
};

pub fn indexOf(id: Id) usize {
    return @intFromEnum(id);
}

// ---- Name rules ------------------------------------------------------------

const keywords_zig = [_][]const u8{
    "addrspace", "align",    "allowzero", "and",     "anyframe", "anytype",     "asm",      "async",      "await",     "break",
    "callconv",  "catch",    "comptime",  "const",   "continue", "defer",       "else",     "enum",       "errdefer",  "error",
    "export",    "extern",   "fn",        "for",     "if",       "inline",      "linksection", "noalias", "noinline",  "nosuspend",
    "opaque",    "or",       "orelse",    "packed",  "pub",      "resume",      "return",   "struct",     "suspend",   "switch",
    "test",      "threadlocal", "try",    "union",   "unreachable", "var",      "volatile", "while",
};

const zig_reserved = [_][]const u8{
    "std",  "builtin", "root",   "main",   "Io",   "io",   "init",        "stdout", "stdout_buffer", "stdout_file_writer",
    "bool", "void",    "type",   "true",   "false", "null", "undefined",   "anyerror", "noreturn",    "usize",
    "isize", "c_int",  "c_uint", "c_long", "c_char", "comptime_int", "comptime_float", "anyopaque",
};

const keywords_rust = [_][]const u8{
    "as",     "async", "await",  "break",  "const",  "continue", "crate",  "dyn",   "else",   "enum",   "extern", "false",
    "fn",     "for",   "if",     "impl",   "in",     "let",      "loop",   "match", "mod",    "move",   "mut",    "pub",
    "ref",    "return", "self",  "Self",   "static", "struct",   "super",  "trait", "true",   "type",   "unsafe", "use",
    "where",  "while", "abstract", "become", "box",  "do",       "final",  "macro", "override", "priv",  "try",    "typeof",
    "unsized", "virtual", "yield",
};

const rust_reserved = [_][]const u8{ "test", "std", "core", "alloc", "proc_macro", "build", "deps", "examples", "incremental", "debug", "release" };

const keywords_python = [_][]const u8{
    "False", "None",  "True",   "and",    "as",     "assert", "async", "await",   "break",    "class",  "continue", "def",
    "del",   "elif",  "else",   "except", "finally", "for",    "from",  "global",  "if",       "import", "in",       "is",
    "lambda", "nonlocal", "not", "or",     "pass",   "raise",  "return", "try",    "while",    "with",   "yield",
};

const keywords_csharp = [_][]const u8{
    "abstract", "as",      "base",     "bool",    "break",   "byte",     "case",      "catch",   "char",    "checked", "class",   "const",
    "continue", "decimal", "default",  "delegate", "do",     "double",   "else",      "enum",    "event",   "explicit", "extern", "false",
    "finally",  "fixed",   "float",    "for",     "foreach", "goto",     "if",        "implicit", "in",     "int",     "interface", "internal",
    "is",       "lock",    "long",     "namespace", "new",   "null",     "object",    "operator", "out",    "override", "params", "private",
    "protected", "public", "readonly", "ref",     "return",  "sbyte",    "sealed",    "short",   "sizeof",  "stackalloc", "static", "string",
    "struct",   "switch",  "this",     "throw",   "true",    "try",      "typeof",    "uint",    "ulong",   "unchecked", "unsafe", "ushort",
    "using",    "virtual", "void",     "volatile", "while",
};

const cmake_reserved = [_][]const u8{ "test", "all", "clean", "install", "help", "package", "package_source", "edit_cache", "rebuild_cache", "depend", "doc", "docs" };

fn inList(list: []const []const u8, s: []const u8) bool {
    for (list) |k| if (std.mem.eql(u8, k, s)) return true;
    return false;
}

fn inListIgnoreCase(list: []const []const u8, s: []const u8) bool {
    for (list) |k| if (std.ascii.eqlIgnoreCase(k, s)) return true;
    return false;
}

fn isAlnum(ch: u8) bool {
    return std.ascii.isAlphanumeric(ch);
}

/// What any folder name must satisfy.
fn portableName(name: []const u8) ?[]const u8 {
    if (name.len == 0) return "Enter a name.";
    if (name.len > 100) return "The name is too long.";
    if (std.mem.eql(u8, name, ".") or std.mem.eql(u8, name, "..")) return "That is not a folder name.";
    if (name[0] == ' ' or name[name.len - 1] == ' ') return "The name must not start or end with a space.";
    for (name) |ch| {
        if (ch == '/' or ch == '\\' or ch == ':' or ch == 0 or ch < 32) return "The name must not contain / \\ : or control characters.";
    }
    return null;
}

fn zigName(name: []const u8) ?[]const u8 {
    if (portableName(name)) |e| return e;
    if (name.len > 32) return "A Zig package name is at most 32 bytes.";
    if (std.ascii.isDigit(name[0])) return "A Zig package name is an identifier: it cannot start with a digit.";
    for (name) |ch| if (!(isAlnum(ch) or ch == '_')) return "A Zig package name is an identifier: letters, digits and _ only.";
    if (std.mem.eql(u8, name, "_")) return "_ is not a package name.";
    if (inList(&keywords_zig, name)) return "That is a Zig keyword.";
    if (inList(&zig_reserved, name)) return "The generated src/main.zig declares that name.";
    if (isIntegerPrimitive(name)) return "That is a Zig primitive type.";
    return null;
}

fn isIntegerPrimitive(name: []const u8) bool {
    if (name.len < 2) return false;
    if (name[0] != 'u' and name[0] != 'i' and name[0] != 'f') return false;
    for (name[1..]) |ch| if (!std.ascii.isDigit(ch)) return false;
    return true;
}

fn cmakeName(name: []const u8) ?[]const u8 {
    if (portableName(name)) |e| return e;
    if (std.ascii.isDigit(name[0])) return "The name must not start with a digit.";
    for (name) |ch| if (!(isAlnum(ch) or ch == '-' or ch == '_')) return "Use letters, digits, - and _.";
    if (inListIgnoreCase(&cmake_reserved, name)) return "CMake reserves that target name.";
    return null;
}

fn rustName(name: []const u8) ?[]const u8 {
    if (portableName(name)) |e| return e;
    if (std.ascii.isDigit(name[0])) return "A cargo package name must not start with a digit.";
    for (name) |ch| if (!(std.ascii.isAscii(ch) and (isAlnum(ch) or ch == '-' or ch == '_'))) return "Use ASCII letters, digits, - and _.";
    var buf: [128]u8 = undefined;
    const crate = packageName(&buf, name);
    if (inList(&keywords_rust, crate)) return "That is a Rust keyword.";
    if (inList(&rust_reserved, crate)) return "Cargo reserves that name.";
    return null;
}

fn pythonName(name: []const u8) ?[]const u8 {
    if (portableName(name)) |e| return e;
    if (!isAlnum(name[0]) or !isAlnum(name[name.len - 1])) return "A Python project name starts and ends with a letter or digit.";
    for (name) |ch| if (!(isAlnum(ch) or ch == '-' or ch == '_' or ch == '.')) return "Use letters, digits, - _ and .";
    var buf: [128]u8 = undefined;
    const pkg = packageName(&buf, name);
    if (std.ascii.isDigit(pkg[0])) return "The package name must not start with a digit.";
    if (inList(&keywords_python, pkg)) return "That is a Python keyword.";
    if (std.mem.eql(u8, pkg, "test") or std.mem.eql(u8, pkg, "tests")) return "test and tests are taken by the test folder.";
    return null;
}

fn csharpName(name: []const u8) ?[]const u8 {
    if (portableName(name)) |e| return e;
    var parts = std.mem.splitScalar(u8, name, '.');
    while (parts.next()) |part| {
        if (part.len == 0) return "Empty part between dots.";
        if (std.ascii.isDigit(part[0])) return "Each part must start with a letter or _.";
        for (part) |ch| if (!(isAlnum(ch) or ch == '_')) return "Use letters, digits, _ and . only.";
        if (inList(&keywords_csharp, part)) return "That is a C# keyword.";
    }
    return null;
}

/// The identifier a name makes: `-`, `.` and spaces become `_`, lower case for
/// Python and C (the caller decides); here only the separators change.
pub fn packageName(buf: []u8, name: []const u8) []const u8 {
    const n = @min(buf.len, name.len);
    for (name[0..n], buf[0..n]) |ch, *out| out.* = if (ch == '-' or ch == '.' or ch == ' ') '_' else std.ascii.toLower(ch);
    return buf[0..n];
}

pub fn validateName(gen: usize, name: []const u8) ?[]const u8 {
    return generators[gen].name_rule(name);
}

// ---- Location --------------------------------------------------------------

pub const FolderState = enum { free, empty, not_empty, not_a_folder };

/// Whether `dir` is free to make a project in.
pub fn folderState(io: Io, dir: []const u8) FolderState {
    const st = Io.Dir.cwd().statFile(io, dir, .{}) catch return .free;
    if (st.kind != .directory) return .not_a_folder;
    var d = Io.Dir.cwd().openDir(io, dir, .{ .iterate = true }) catch return .not_empty;
    defer d.close(io);
    var it = d.iterate();
    const first = it.next(io) catch return .not_empty;
    return if (first == null) .empty else .not_empty;
}

/// `~` and `~/…` expanded; null for a location that is not absolute.
pub fn expandLocation(gpa: Allocator, location: []const u8) !?[]u8 {
    if (location.len > 0 and location[0] == '~' and (location.len == 1 or location[1] == '/')) {
        const home = std.c.getenv("HOME") orelse return null;
        return try std.fmt.allocPrint(gpa, "{s}{s}", .{ std.mem.span(home), location[1..] });
    }
    if (location.len == 0 or location[0] != '/') return null;
    return try gpa.dupe(u8, location);
}

/// `untitled`, `untitled1`, … whichever is free in `location`.
pub fn freeName(gpa: Allocator, io: Io, location: []const u8) ![]u8 {
    var i: usize = 0;
    while (i < 1000) : (i += 1) {
        const name = if (i == 0) try gpa.dupe(u8, "untitled") else try std.fmt.allocPrint(gpa, "untitled{d}", .{i});
        errdefer gpa.free(name);
        const path = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ std.mem.trimEnd(u8, location, "/"), name });
        defer gpa.free(path);
        if (folderState(io, path) == .free) return name;
        gpa.free(name);
    }
    return gpa.dupe(u8, "untitled");
}

// ---- Toolchain checks ------------------------------------------------------

pub const ToolStatus = enum(u8) { unknown, checking, found, missing };

const ToolSlot = struct {
    status: std.atomic.Value(u8) = .init(0),
    version: [96]u8 = undefined,
    version_len: std.atomic.Value(u32) = .init(0),
};

var tool_slots: [generators.len]ToolSlot = @splat(.{});
var tools_started: std.atomic.Value(bool) = .init(false);

pub fn toolStatus(gen: usize) ToolStatus {
    return @enumFromInt(tool_slots[gen].status.load(.acquire));
}

/// The first line of the toolchain's version output, once found.
pub fn toolVersion(gen: usize) []const u8 {
    if (toolStatus(gen) != .found) return "";
    return tool_slots[gen].version[0..tool_slots[gen].version_len.load(.acquire)];
}

/// Starts checking every toolchain on a worker thread (once per process).
/// `wake` runs on the main thread when the results are in.
pub fn startToolChecks(gpa: Allocator, io: Io, executor: *Executor, wake: ?Completion) void {
    if (tools_started.swap(true, .acq_rel)) return;
    for (&tool_slots, 0..) |*slot, i| slot.status.store(if (generators[i].tool != null) @intFromEnum(ToolStatus.checking) else @intFromEnum(ToolStatus.found), .release);
    executor.spawn(checkTools, .{ gpa, io, executor, wake }) catch {
        for (&tool_slots) |*slot| slot.status.store(@intFromEnum(ToolStatus.missing), .release);
    };
}

fn checkTools(gpa: Allocator, io: Io, executor: *Executor, wake: ?Completion) void {
    defer executor.taskDone();
    for (generators, 0..) |g, i| {
        const tool = g.tool orelse continue;
        const slot = &tool_slots[i];
        const out = git.runFull(gpa, io, tool.argv, "/", null, false) catch {
            slot.status.store(@intFromEnum(ToolStatus.missing), .release);
            continue;
        };
        defer gpa.free(out.stdout);
        defer gpa.free(out.stderr);
        const text = if (out.stdout.len > 0) out.stdout else out.stderr;
        if (!out.ok) {
            slot.status.store(@intFromEnum(ToolStatus.missing), .release);
            continue;
        }
        const line = std.mem.trim(u8, text[0 .. std.mem.indexOfScalar(u8, text, '\n') orelse text.len], " \r\t");
        const n = @min(line.len, slot.version.len);
        @memcpy(slot.version[0..n], line[0..n]);
        slot.version_len.store(@intCast(n), .release);
        // A bare version number ("0.17.0", "10.0.403") gets its tool's name.
        slot.status.store(@intFromEnum(ToolStatus.found), .release);
    }
    if (wake) |w| executor.post(w);
}

// ---- Generation ------------------------------------------------------------

pub const Request = struct {
    gen: usize,
    name: []const u8,
    /// The absolute folder the project is made in.
    dir: []const u8,
    choices: [max_options]u8 = @splat(0),
    git: bool = true,
};

pub const Result = struct {
    /// The file to open first, absolute.
    main_file: []u8,
    /// Something that did not work but did not stop the project (git, venv).
    warning: ?[]u8 = null,

    pub fn deinit(self: *Result, gpa: Allocator) void {
        gpa.free(self.main_file);
        if (self.warning) |w| gpa.free(w);
    }
};

const Sub = struct { key: []const u8, value: []const u8 };

const Maker = struct {
    gpa: Allocator,
    io: Io,
    req: Request,
    subs: std.ArrayList(Sub) = .empty,
    /// Absolute paths of the files written, for clean-up.
    written: std.ArrayList([]u8) = .empty,
    warnings: std.ArrayList(u8) = .empty,
    arena: std.heap.ArenaAllocator,

    fn deinit(self: *Maker) void {
        self.subs.deinit(self.gpa);
        for (self.written.items) |p| self.gpa.free(p);
        self.written.deinit(self.gpa);
        self.warnings.deinit(self.gpa);
        self.arena.deinit();
    }

    fn sub(self: *Maker, key: []const u8, value: []const u8) !void {
        try self.subs.append(self.gpa, .{ .key = key, .value = try self.arena.allocator().dupe(u8, value) });
    }

    fn choice(self: *Maker, key: []const u8) []const u8 {
        const g = generators[self.req.gen];
        for (g.options, 0..) |o, i| if (std.mem.eql(u8, o.key, key)) return o.choices[@min(self.req.choices[i], o.choices.len - 1)];
        return "";
    }

    fn choiceIs(self: *Maker, key: []const u8, value: []const u8) bool {
        return std.mem.eql(u8, self.choice(key), value);
    }

    fn warn(self: *Maker, comptime fmt: []const u8, args: anytype) void {
        if (self.warnings.items.len > 0) self.warnings.appendSlice(self.gpa, "; ") catch {};
        self.warnings.print(self.gpa, fmt, args) catch {};
    }

    /// Writes `template` (with `{{key}}` replaced) to `rel` under the project.
    fn put(self: *Maker, rel: []const u8, template: []const u8) !void {
        const gpa = self.gpa;
        const text = try render(gpa, template, self.subs.items);
        defer gpa.free(text);
        const rel_path = try render(gpa, rel, self.subs.items);
        defer gpa.free(rel_path);
        const path = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ self.req.dir, rel_path });
        errdefer gpa.free(path);
        if (std.fs.path.dirname(path)) |parent| try Io.Dir.cwd().createDirPath(self.io, parent);
        try Io.Dir.cwd().writeFile(self.io, .{ .sub_path = path, .data = text });
        try self.written.append(gpa, path);
    }

    fn run(self: *Maker, argv: []const []const u8) bool {
        const out = git.runFull(self.gpa, self.io, argv, self.req.dir, null, true) catch return false;
        defer self.gpa.free(out.stdout);
        defer self.gpa.free(out.stderr);
        return out.ok;
    }
};

/// `{{key}}` → value; unknown keys stay as they are.
pub fn render(gpa: Allocator, template: []const u8, subs: []const Sub) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var i: usize = 0;
    while (i < template.len) {
        if (std.mem.startsWith(u8, template[i..], "{{")) {
            if (std.mem.indexOfPos(u8, template, i + 2, "}}")) |end| {
                const key = template[i + 2 .. end];
                const value = lookup(subs, key) orelse template[i .. end + 2];
                try out.appendSlice(gpa, value);
                i = end + 2;
                continue;
            }
        }
        try out.append(gpa, template[i]);
        i += 1;
    }
    return out.toOwnedSlice(gpa);
}

fn lookup(subs: []const Sub, key: []const u8) ?[]const u8 {
    for (subs) |s| if (std.mem.eql(u8, s.key, key)) return s.value;
    return null;
}

/// Makes the project. On failure what this call made is removed again.
pub fn generate(gpa: Allocator, io: Io, req: Request) !Result {
    const existed = folderState(io, req.dir) != .free;
    var maker: Maker = .{ .gpa = gpa, .io = io, .req = req, .arena = .init(gpa) };
    defer maker.deinit();
    errdefer {
        if (!existed) {
            Io.Dir.cwd().deleteTree(io, req.dir) catch {};
        } else for (maker.written.items) |p| Io.Dir.cwd().deleteFile(io, p) catch {};
    }

    try Io.Dir.cwd().createDirPath(io, req.dir);
    var pkg_buf: [128]u8 = undefined;
    const pkg = packageName(&pkg_buf, req.name);
    try maker.sub("name", req.name);
    try maker.sub("pkg", pkg);
    try maker.sub("tool", generators[req.gen].name);

    const main_rel: []const u8 = switch (@as(Id, @enumFromInt(req.gen))) {
        .empty => try makeEmpty(&maker),
        .zig => try makeZig(&maker),
        .c => try makeCmake(&maker, .c),
        .cpp => try makeCmake(&maker, .cpp),
        .rust => try makeRust(&maker),
        .python => try makePython(&maker),
        .csharp => try makeCsharp(&maker),
    };
    try writeCommon(&maker);

    if (req.git) {
        if (maker.run(&.{ "git", "init", "-q" })) {
            if (maker.run(&.{ "git", "add", "-A" })) {
                if (!maker.run(&.{ "git", "commit", "-q", "-m", "Initial commit" }))
                    maker.warn("The first commit was not made (git needs user.name and user.email)", .{});
            }
        } else maker.warn("git init failed; no repository was made", .{});
    }

    const main_rendered = try render(gpa, main_rel, maker.subs.items);
    defer gpa.free(main_rendered);
    const main_file = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ req.dir, main_rendered });
    errdefer gpa.free(main_file);
    const warning = if (maker.warnings.items.len > 0) try gpa.dupe(u8, maker.warnings.items) else null;
    return .{ .main_file = main_file, .warning = warning };
}

/// A growing text with `writeAll`, for files assembled from parts.
const Doc = struct {
    gpa: Allocator,
    list: std.ArrayList(u8) = .empty,

    fn deinit(self: *Doc) void {
        self.list.deinit(self.gpa);
    }

    fn writeAll(self: *Doc, text: []const u8) !void {
        try self.list.appendSlice(self.gpa, text);
    }
};

// ---- Common files ----------------------------------------------------------

fn writeCommon(m: *Maker) !void {
    const id: Id = @enumFromInt(m.req.gen);
    const lang_ignore: []const u8 = switch (id) {
        .empty => "",
        .zig => ".zig-cache/\nzig-out/\n",
        .c, .cpp => "build/\ncompile_commands.json\n.cache/\n",
        .rust => "target/\n",
        .python => "__pycache__/\n*.py[cod]\n.venv/\n.pytest_cache/\n.ruff_cache/\ndist/\n*.egg-info/\n",
        .csharp => "bin/\nobj/\n*.user\n",
    };
    const gitignore = try std.fmt.allocPrint(m.gpa, "{s}.DS_Store\n.idea/\n*.swp\n", .{lang_ignore});
    defer m.gpa.free(gitignore);
    try m.put(".gitignore", gitignore);

    const lang_editorconfig: []const u8 = switch (id) {
        .csharp => "\n[*.{csproj,slnx,props,targets}]\nindent_size = 2\n",
        .c, .cpp => "\n[CMakeLists.txt]\nindent_size = 2\n",
        else => "",
    };
    const ec = try std.fmt.allocPrint(m.gpa, "{s}{s}", .{ editorconfig, lang_editorconfig });
    defer m.gpa.free(ec);
    try m.put(".editorconfig", ec);
}

const editorconfig =
    \\root = true
    \\
    \\[*]
    \\charset = utf-8
    \\end_of_line = lf
    \\insert_final_newline = true
    \\trim_trailing_whitespace = true
    \\indent_style = space
    \\indent_size = 4
    \\
    \\[*.{json,yml,yaml,toml}]
    \\indent_size = 2
    \\
    \\[Makefile]
    \\indent_style = tab
    \\
;

fn makeEmpty(m: *Maker) ![]const u8 {
    try m.put("README.md",
        \\# {{name}}
        \\
        \\An empty project.
        \\
    );
    return "README.md";
}

// ---- Zig -------------------------------------------------------------------

fn makeZig(m: *Maker) ![]const u8 {
    const name = m.req.name;
    var id: u32 = 0;
    while (id == 0 or id == 0xffff_ffff) m.io.random(std.mem.asBytes(&id));
    const fingerprint: u64 = (@as(u64, std.hash.Crc32.hash(name)) << 32) | id;
    var fp_buf: [20]u8 = undefined;
    try m.sub("fingerprint", try std.fmt.bufPrint(&fp_buf, "0x{x:0>16}", .{fingerprint}));

    try m.put("build.zig.zon",
        \\.{
        \\    .name = .{{name}},
        \\    .version = "0.0.0",
        \\    .fingerprint = {{fingerprint}},
        \\    .minimum_zig_version = "0.17.0",
        \\    .dependencies = .{},
        \\    .paths = .{
        \\        "build.zig",
        \\        "build.zig.zon",
        \\        "src",
        \\    },
        \\}
        \\
    );
    try m.put("build.zig",
        \\const std = @import("std");
        \\
        \\pub fn build(b: *std.Build) void {
        \\    const target = b.standardTargetOptions(.{});
        \\    const optimize = b.standardOptimizeOption(.{});
        \\
        \\    const mod = b.addModule("{{name}}", .{
        \\        .root_source_file = b.path("src/root.zig"),
        \\        .target = target,
        \\    });
        \\
        \\    const exe = b.addExecutable(.{
        \\        .name = "{{name}}",
        \\        .root_module = b.createModule(.{
        \\            .root_source_file = b.path("src/main.zig"),
        \\            .target = target,
        \\            .optimize = optimize,
        \\            .imports = &.{
        \\                .{ .name = "{{name}}", .module = mod },
        \\            },
        \\        }),
        \\    });
        \\    b.installArtifact(exe);
        \\
        \\    const run_step = b.step("run", "Run the app");
        \\    const run_cmd = b.addRunArtifact(exe);
        \\    run_step.dependOn(&run_cmd.step);
        \\    run_cmd.step.dependOn(b.getInstallStep());
        \\    run_cmd.addPassthruArgs();
        \\
        \\    const mod_tests = b.addTest(.{ .root_module = mod });
        \\    const run_mod_tests = b.addRunArtifact(mod_tests);
        \\    const exe_tests = b.addTest(.{ .root_module = exe.root_module });
        \\    const run_exe_tests = b.addRunArtifact(exe_tests);
        \\
        \\    const test_step = b.step("test", "Run tests");
        \\    test_step.dependOn(&run_mod_tests.step);
        \\    test_step.dependOn(&run_exe_tests.step);
        \\}
        \\
    );
    try m.put("src/main.zig",
        \\const std = @import("std");
        \\const Io = std.Io;
        \\
        \\const {{name}} = @import("{{name}}");
        \\
        \\pub fn main(init: std.process.Init) !void {
        \\    const io = init.io;
        \\
        \\    var stdout_buffer: [1024]u8 = undefined;
        \\    var stdout_file_writer: Io.File.Writer = .init(.stdout(), io, &stdout_buffer);
        \\    const stdout = &stdout_file_writer.interface;
        \\
        \\    try stdout.print("Hello, {s}!\n", .{ {{name}}.who() });
        \\    try stdout.flush();
        \\}
        \\
    );
    try m.put("src/root.zig",
        \\//! The library of {{name}}: the module `main.zig` imports as "{{name}}".
        \\const std = @import("std");
        \\
        \\pub fn who() []const u8 {
        \\    return "world";
        \\}
        \\
        \\pub fn add(a: i32, b: i32) i32 {
        \\    return a + b;
        \\}
        \\
        \\test "add" {
        \\    try std.testing.expect(add(3, 7) == 10);
        \\}
        \\
    );
    try m.put("README.md",
        \\# {{name}}
        \\
        \\A Zig project.
        \\
        \\```sh
        \\zig build          # build into zig-out/
        \\zig build run      # build and run
        \\zig build test     # run the tests
        \\```
        \\
    );
    try m.sub("sourcelangs", "");
    try m.put(".vscode/tasks.json", vscode_tasks_zig);
    try m.put(".vscode/launch.json", launchNative("zig-out/bin/{{name}}", "zig: build"));
    return "src/main.zig";
}

// ---- C and C++ -------------------------------------------------------------

fn makeCmake(m: *Maker, lang: enum { c, cpp }) ![]const u8 {
    const is_c = lang == .c;
    const exe = m.choiceIs("kind", "Executable");
    const std_label = m.choice("std");
    const std_num = std.mem.trimStart(u8, std_label, "C+");
    try m.sub("std", std_num);
    try m.sub("lang", if (is_c) "C" else "CXX");
    try m.sub("ext", if (is_c) "c" else "cpp");
    try m.sub("hext", if (is_c) "h" else "hpp");
    try m.sub("stdvar", if (is_c) "CMAKE_C_STANDARD" else "CMAKE_CXX_STANDARD");
    const lib_target: []const u8 = if (exe) try std.fmt.allocPrint(m.arena.allocator(), "{s}_lib", .{m.req.name}) else m.req.name;
    try m.sub("lib", lib_target);
    const tests = m.choice("tests");
    const with_tests = !std.mem.eql(u8, tests, "None");

    // Presets: Ninja when it is installed.
    const ninja = m.run(&.{ "ninja", "--version" });
    try m.sub("generator", if (ninja) "    \"generator\": \"Ninja\",\n" else "");
    try m.put("CMakePresets.json",
        \\{
        \\  "version": 3,
        \\  "cmakeMinimumRequired": { "major": 3, "minor": 21, "patch": 0 },
        \\  "configurePresets": [
        \\    {
        \\      "name": "debug",
        \\      "displayName": "Debug",
        \\{{generator}}      "binaryDir": "${sourceDir}/build/debug",
        \\      "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" }
        \\    },
        \\    {
        \\      "name": "release",
        \\      "displayName": "Release",
        \\{{generator}}      "binaryDir": "${sourceDir}/build/release",
        \\      "cacheVariables": { "CMAKE_BUILD_TYPE": "Release" }
        \\    }
        \\  ],
        \\  "buildPresets": [
        \\    { "name": "debug", "configurePreset": "debug" },
        \\    { "name": "release", "configurePreset": "release" }
        \\  ],
        \\  "testPresets": [
        \\    { "name": "debug", "configurePreset": "debug", "output": { "outputOnFailure": true } },
        \\    { "name": "release", "configurePreset": "release", "output": { "outputOnFailure": true } }
        \\  ]
        \\}
        \\
    );

    // CMakeLists.txt, assembled from the parts the options choose.
    var cml: Doc = .{ .gpa = m.gpa };
    defer cml.deinit();
    const w = &cml;
    try w.writeAll(
        \\cmake_minimum_required(VERSION 3.21)
        \\project({{name}} VERSION 0.1.0 LANGUAGES {{lang}})
        \\
        \\set({{stdvar}} {{std}})
        \\set({{stdvar}}_REQUIRED ON)
        \\set(CMAKE_{{lang}}_EXTENSIONS OFF)
        \\set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
        \\
        \\add_library({{lib}} src/{{pkg}}.{{ext}})
        \\target_include_directories({{lib}} PUBLIC include)
        \\
    );
    if (exe) try w.writeAll(
        \\
        \\add_executable({{name}} src/main.{{ext}})
        \\target_link_libraries({{name}} PRIVATE {{lib}})
        \\
    );
    if (with_tests) try w.writeAll(
        \\
        \\include(CTest)
        \\if(BUILD_TESTING)
        \\
    );
    if (with_tests and !is_c and std.mem.eql(u8, tests, "GoogleTest")) try w.writeAll(
        \\  include(FetchContent)
        \\  FetchContent_Declare(googletest
        \\    GIT_REPOSITORY https://github.com/google/googletest.git
        \\    GIT_TAG v1.17.0
        \\    GIT_SHALLOW TRUE)
        \\  FetchContent_MakeAvailable(googletest)
        \\  include(GoogleTest)
        \\  add_executable({{pkg}}_tests tests/test_{{pkg}}.cpp)
        \\  target_link_libraries({{pkg}}_tests PRIVATE {{lib}} GTest::gtest_main)
        \\  gtest_discover_tests({{pkg}}_tests)
        \\
    ) else if (with_tests and !is_c and std.mem.eql(u8, tests, "Catch2")) try w.writeAll(
        \\  include(FetchContent)
        \\  FetchContent_Declare(Catch2
        \\    GIT_REPOSITORY https://github.com/catchorg/Catch2.git
        \\    GIT_TAG v3.8.1
        \\    GIT_SHALLOW TRUE)
        \\  FetchContent_MakeAvailable(Catch2)
        \\  list(APPEND CMAKE_MODULE_PATH ${catch2_SOURCE_DIR}/extras)
        \\  include(Catch)
        \\  add_executable({{pkg}}_tests tests/test_{{pkg}}.cpp)
        \\  target_link_libraries({{pkg}}_tests PRIVATE {{lib}} Catch2::Catch2WithMain)
        \\  catch_discover_tests({{pkg}}_tests)
        \\
    ) else if (with_tests) try w.writeAll(
        \\  add_executable({{pkg}}_tests tests/test_{{pkg}}.{{ext}})
        \\  target_link_libraries({{pkg}}_tests PRIVATE {{lib}})
        \\  add_test(NAME {{pkg}}_tests COMMAND {{pkg}}_tests)
        \\
    );
    if (with_tests) try w.writeAll("endif()\n");
    try m.put("CMakeLists.txt", cml.list.items);

    if (is_c) {
        try m.put("include/{{pkg}}.h",
            \\#ifndef {{pkg}}_H
            \\#define {{pkg}}_H
            \\
            \\/* The greeting the program prints. */
            \\const char *{{pkg}}_greeting(void);
            \\
            \\#endif
            \\
        );
        try m.put("src/{{pkg}}.c",
            \\#include "{{pkg}}.h"
            \\
            \\const char *{{pkg}}_greeting(void) {
            \\    return "Hello, world!";
            \\}
            \\
        );
        if (exe) try m.put("src/main.c",
            \\#include <stdio.h>
            \\
            \\#include "{{pkg}}.h"
            \\
            \\int main(void) {
            \\    puts({{pkg}}_greeting());
            \\    return 0;
            \\}
            \\
        );
        if (with_tests) try m.put("tests/test_{{pkg}}.c",
            \\#include <string.h>
            \\
            \\#include "{{pkg}}.h"
            \\
            \\int main(void) {
            \\    return strcmp({{pkg}}_greeting(), "Hello, world!") == 0 ? 0 : 1;
            \\}
            \\
        );
    } else {
        try m.put("include/{{pkg}}.hpp",
            \\#pragma once
            \\
            \\#include <string>
            \\
            \\namespace {{pkg}} {
            \\
            \\/// The greeting the program prints.
            \\std::string greeting();
            \\
            \\}  // namespace {{pkg}}
            \\
        );
        try m.put("src/{{pkg}}.cpp",
            \\#include "{{pkg}}.hpp"
            \\
            \\namespace {{pkg}} {
            \\
            \\std::string greeting() {
            \\    return "Hello, world!";
            \\}
            \\
            \\}  // namespace {{pkg}}
            \\
        );
        if (exe) try m.put("src/main.cpp",
            \\#include <iostream>
            \\
            \\#include "{{pkg}}.hpp"
            \\
            \\int main() {
            \\    std::cout << {{pkg}}::greeting() << '\n';
            \\    return 0;
            \\}
            \\
        );
        if (std.mem.eql(u8, tests, "GoogleTest")) try m.put("tests/test_{{pkg}}.cpp",
            \\#include <gtest/gtest.h>
            \\
            \\#include "{{pkg}}.hpp"
            \\
            \\TEST(Greeting, SaysHello) {
            \\    EXPECT_EQ({{pkg}}::greeting(), "Hello, world!");
            \\}
            \\
        ) else if (std.mem.eql(u8, tests, "Catch2")) try m.put("tests/test_{{pkg}}.cpp",
            \\#include <catch2/catch_test_macros.hpp>
            \\
            \\#include "{{pkg}}.hpp"
            \\
            \\TEST_CASE("the greeting says hello") {
            \\    REQUIRE({{pkg}}::greeting() == "Hello, world!");
            \\}
            \\
        ) else if (with_tests) try m.put("tests/test_{{pkg}}.cpp",
            \\#include "{{pkg}}.hpp"
            \\
            \\int main() {
            \\    return {{pkg}}::greeting() == "Hello, world!" ? 0 : 1;
            \\}
            \\
        );
    }

    try m.put(".clang-format",
        \\BasedOnStyle: LLVM
        \\IndentWidth: 4
        \\ColumnLimit: 100
        \\
    );
    try m.put(".clangd",
        \\CompileFlags:
        \\  CompilationDatabase: build/debug
        \\
    );
    try m.put("README.md",
        \\# {{name}}
        \\
        \\A {{tool}} project built with CMake.
        \\
        \\```sh
        \\cmake --preset debug             # configure into build/debug
        \\cmake --build --preset debug     # build
        \\ctest --preset debug             # run the tests
        \\```
        \\
    );
    try m.sub("sourcelangs", if (is_c) ",\n      \"sourceLanguages\": [\"c\"]" else ",\n      \"sourceLanguages\": [\"cpp\"]");
    try m.put(".vscode/tasks.json", vscode_tasks_cmake);
    if (exe) try m.put(".vscode/launch.json", launchNative("build/debug/{{name}}", "cmake: build (debug)"));
    const main_rel = try std.fmt.allocPrint(m.arena.allocator(), "src/{s}", .{if (exe) (if (is_c) "main.c" else "main.cpp") else (if (is_c) "{{pkg}}.c" else "{{pkg}}.cpp")});
    return try render(m.arena.allocator(), main_rel, m.subs.items);
}

// ---- Rust ------------------------------------------------------------------

fn makeRust(m: *Maker) ![]const u8 {
    const lib = m.choiceIs("kind", "Library");
    try m.sub("edition", m.choice("edition"));
    try m.put("Cargo.toml",
        \\[package]
        \\name = "{{name}}"
        \\version = "0.1.0"
        \\edition = "{{edition}}"
        \\
        \\[dependencies]
        \\
    );
    if (lib) {
        try m.put("src/lib.rs",
            \\pub fn add(left: u64, right: u64) -> u64 {
            \\    left + right
            \\}
            \\
            \\#[cfg(test)]
            \\mod tests {
            \\    use super::*;
            \\
            \\    #[test]
            \\    fn it_works() {
            \\        assert_eq!(add(2, 2), 4);
            \\    }
            \\}
            \\
        );
    } else {
        try m.put("src/main.rs",
            \\fn main() {
            \\    println!("Hello, world!");
            \\}
            \\
        );
    }
    try m.put("README.md",
        \\# {{name}}
        \\
        \\A Rust package.
        \\
        \\```sh
        \\cargo build
        \\cargo run
        \\cargo test
        \\```
        \\
    );
    try m.sub("sourcelangs", ",\n      \"sourceLanguages\": [\"rust\"]");
    try m.put(".vscode/tasks.json", vscode_tasks_cargo);
    if (!lib) try m.put(".vscode/launch.json", launchNative("target/debug/{{name}}", "cargo: build"));
    return if (lib) "src/lib.rs" else "src/main.rs";
}

// ---- Python ----------------------------------------------------------------

fn makePython(m: *Maker) ![]const u8 {
    const app = m.choiceIs("kind", "Application");
    const sample = m.choiceIs("sample", "Add sample code");
    try m.put("pyproject.toml",
        \\[build-system]
        \\requires = ["hatchling"]
        \\build-backend = "hatchling.build"
        \\
        \\[project]
        \\name = "{{name}}"
        \\version = "0.1.0"
        \\description = ""
        \\readme = "README.md"
        \\requires-python = ">=3.9"
        \\dependencies = []
        \\
        \\[dependency-groups]
        \\dev = ["pytest", "ruff"]
        \\
        \\[tool.hatch.build.targets.wheel]
        \\packages = ["src/{{pkg}}"]
        \\
        \\[tool.ruff]
        \\line-length = 100
        \\src = ["src"]
        \\
        \\[tool.ruff.lint]
        \\select = ["E", "F", "W", "I", "B", "UP"]
        \\
        \\[tool.ruff.format]
        \\quote-style = "double"
        \\
        \\[tool.pytest.ini_options]
        \\pythonpath = ["src"]
        \\testpaths = ["tests"]
        \\
    );
    if (sample) {
        try m.put("src/{{pkg}}/__init__.py",
            \\"""{{name}}."""
            \\
            \\__version__ = "0.1.0"
            \\
            \\
            \\def greeting() -> str:
            \\    return "Hello, world!"
            \\
        );
        try m.put("tests/test_{{pkg}}.py",
            \\from {{pkg}} import greeting
            \\
            \\
            \\def test_greeting() -> None:
            \\    assert greeting() == "Hello, world!"
            \\
        );
    } else {
        try m.put("src/{{pkg}}/__init__.py",
            \\"""{{name}}."""
            \\
            \\__version__ = "0.1.0"
            \\
        );
        try m.put("tests/test_{{pkg}}.py",
            \\from {{pkg}} import __version__
            \\
            \\
            \\def test_version() -> None:
            \\    assert __version__
            \\
        );
    }
    if (app) try m.put("src/{{pkg}}/__main__.py", if (sample)
        \\from . import greeting
        \\
        \\
        \\def main() -> None:
        \\    print(greeting())
        \\
        \\
        \\if __name__ == "__main__":
        \\    main()
        \\
    else
        \\def main() -> None:
        \\    pass
        \\
        \\
        \\if __name__ == "__main__":
        \\    main()
        \\
    );
    try m.put("README.md",
        \\# {{name}}
        \\
        \\A Python project (src layout).
        \\
        \\```sh
        \\python3 -m venv .venv && .venv/bin/pip install pytest ruff
        \\PYTHONPATH=src .venv/bin/python -m {{pkg}}   # run
        \\.venv/bin/python -m pytest                    # test
        \\.venv/bin/ruff check .                        # lint
        \\```
        \\
    );
    try m.put(".vscode/settings.json",
        \\{
        \\  "python.defaultInterpreterPath": "${workspaceFolder}/.venv/bin/python",
        \\  "python.testing.pytestEnabled": true
        \\}
        \\
    );
    if (m.choiceIs("venv", "Create .venv")) {
        if (!m.run(&.{ "python3", "-m", "venv", ".venv" })) m.warn("python3 -m venv failed; no .venv was made", .{});
    }
    return if (app) "src/{{pkg}}/__main__.py" else "src/{{pkg}}/__init__.py";
}

// ---- C# --------------------------------------------------------------------

fn makeCsharp(m: *Maker) ![]const u8 {
    const console = m.choiceIs("kind", "Console App");
    const slnx = m.choiceIs("sln", ".slnx");
    const tests = m.choiceIs("tests", "xUnit test project");
    try m.sub("tfm", m.choice("tfm"));
    try m.sub("output", if (console) "    <OutputType>Exe</OutputType>\n" else "");

    try m.put("src/{{name}}/{{name}}.csproj",
        \\<Project Sdk="Microsoft.NET.Sdk">
        \\
        \\  <PropertyGroup>
        \\{{output}}    <TargetFramework>{{tfm}}</TargetFramework>
        \\    <ImplicitUsings>enable</ImplicitUsings>
        \\    <Nullable>enable</Nullable>
        \\  </PropertyGroup>
        \\
        \\</Project>
        \\
    );
    const main_rel: []const u8 = if (console) "src/{{name}}/Program.cs" else "src/{{name}}/Class1.cs";
    if (console) try m.put(main_rel,
        \\Console.WriteLine("Hello, World!");
        \\
    ) else try m.put(main_rel,
        \\namespace {{name}};
        \\
        \\public class Class1
        \\{
        \\    public static string Greeting() => "Hello, World!";
        \\}
        \\
    );
    if (tests) {
        try m.put("tests/{{name}}.Tests/{{name}}.Tests.csproj",
            \\<Project Sdk="Microsoft.NET.Sdk">
            \\
            \\  <PropertyGroup>
            \\    <TargetFramework>{{tfm}}</TargetFramework>
            \\    <ImplicitUsings>enable</ImplicitUsings>
            \\    <Nullable>enable</Nullable>
            \\    <IsPackable>false</IsPackable>
            \\  </PropertyGroup>
            \\
            \\  <ItemGroup>
            \\    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            \\    <PackageReference Include="xunit" Version="2.9.3" />
            \\    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
            \\  </ItemGroup>
            \\
            \\  <ItemGroup>
            \\    <ProjectReference Include="../../src/{{name}}/{{name}}.csproj" />
            \\  </ItemGroup>
            \\
            \\</Project>
            \\
        );
        try m.put("tests/{{name}}.Tests/UnitTest1.cs",
            \\namespace {{name}}.Tests;
            \\
            \\public class UnitTest1
            \\{
            \\    [Fact]
            \\    public void Test1()
            \\    {
            \\        Assert.True(true);
            \\    }
            \\}
            \\
        );
    }
    if (slnx) {
        try m.put("{{name}}.slnx", if (tests)
            \\<Solution>
            \\  <Folder Name="/src/">
            \\    <Project Path="src/{{name}}/{{name}}.csproj" />
            \\  </Folder>
            \\  <Folder Name="/tests/">
            \\    <Project Path="tests/{{name}}.Tests/{{name}}.Tests.csproj" />
            \\  </Folder>
            \\</Solution>
            \\
        else
            \\<Solution>
            \\  <Project Path="src/{{name}}/{{name}}.csproj" />
            \\</Solution>
            \\
        );
    } else try writeSln(m, tests);
    try m.put("README.md",
        \\# {{name}}
        \\
        \\A .NET solution.
        \\
        \\```sh
        \\dotnet build
        \\dotnet run --project src/{{name}}
        \\dotnet test
        \\```
        \\
    );
    try m.put(".vscode/tasks.json", vscode_tasks_dotnet);
    if (console) try m.put(".vscode/launch.json", vscode_launch_dotnet);
    return try render(m.arena.allocator(), main_rel, m.subs.items);
}

fn guidFor(buf: *[36]u8, name: []const u8, salt: u8) []const u8 {
    var h = std.hash.Wyhash.init(salt);
    h.update(name);
    const a = h.final();
    h.update("cs-zig");
    const b = h.final();
    return std.fmt.bufPrint(buf, "{x:0>8}-{x:0>4}-{x:0>4}-{x:0>4}-{x:0>12}", .{
        @as(u32, @truncate(a)),
        @as(u16, @truncate(a >> 32)),
        @as(u16, @truncate(a >> 48)),
        @as(u16, @truncate(b)),
        @as(u48, @truncate(b >> 16)),
    }) catch unreachable;
}

fn writeSln(m: *Maker, tests: bool) !void {
    var g_app: [36]u8 = undefined;
    var g_test: [36]u8 = undefined;
    try m.sub("g_app", guidFor(&g_app, m.req.name, 1));
    try m.sub("g_test", guidFor(&g_test, m.req.name, 2));
    try m.sub("t", "\t");
    var s: Doc = .{ .gpa = m.gpa };
    defer s.deinit();
    const w = &s;
    try w.writeAll(
        \\Microsoft Visual Studio Solution File, Format Version 12.00
        \\# Visual Studio Version 17
        \\Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{{name}}", "src\{{name}}\{{name}}.csproj", "{{{g_app}}}"
        \\EndProject
        \\
    );
    if (tests) try w.writeAll(
        \\Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{{name}}.Tests", "tests\{{name}}.Tests\{{name}}.Tests.csproj", "{{{g_test}}}"
        \\EndProject
        \\
    );
    try w.writeAll(
        \\Global
        \\{{t}}GlobalSection(SolutionConfigurationPlatforms) = preSolution
        \\{{t}}{{t}}Debug|Any CPU = Debug|Any CPU
        \\{{t}}{{t}}Release|Any CPU = Release|Any CPU
        \\{{t}}EndGlobalSection
        \\{{t}}GlobalSection(ProjectConfigurationPlatforms) = postSolution
        \\{{t}}{{t}}{{{g_app}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        \\{{t}}{{t}}{{{g_app}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
        \\{{t}}{{t}}{{{g_app}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
        \\{{t}}{{t}}{{{g_app}}}.Release|Any CPU.Build.0 = Release|Any CPU
        \\
    );
    if (tests) try w.writeAll(
        \\{{t}}{{t}}{{{g_test}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        \\{{t}}{{t}}{{{g_test}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
        \\{{t}}{{t}}{{{g_test}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
        \\{{t}}{{t}}{{{g_test}}}.Release|Any CPU.Build.0 = Release|Any CPU
        \\
    );
    try w.writeAll(
        \\{{t}}EndGlobalSection
        \\EndGlobal
        \\
    );
    try m.put("{{name}}.sln", s.list.items);
}

// ---- .vscode ---------------------------------------------------------------

/// A CodeLLDB launch.json for a native program.
fn launchNative(comptime program: []const u8, comptime task: []const u8) []const u8 {
    return "{\n" ++
        "  \"version\": \"0.2.0\",\n" ++
        "  \"configurations\": [\n" ++
        "    {\n" ++
        "      \"name\": \"Debug {{name}}\",\n" ++
        "      \"type\": \"lldb\",\n" ++
        "      \"request\": \"launch\",\n" ++
        "      \"program\": \"${workspaceFolder}/" ++ program ++ "\",\n" ++
        "      \"args\": [],\n" ++
        "      \"cwd\": \"${workspaceFolder}\",\n" ++
        "      \"preLaunchTask\": \"" ++ task ++ "\"{{sourcelangs}}\n" ++
        "    }\n" ++
        "  ]\n" ++
        "}\n";
}

const vscode_launch_dotnet =
    \\{
    \\  "version": "0.2.0",
    \\  "configurations": [
    \\    {
    \\      "name": "Debug {{name}}",
    \\      "type": "coreclr",
    \\      "request": "launch",
    \\      "program": "${workspaceFolder}/src/{{name}}/bin/Debug/{{tfm}}/{{name}}.dll",
    \\      "cwd": "${workspaceFolder}/src/{{name}}",
    \\      "preLaunchTask": "build"
    \\    }
    \\  ]
    \\}
    \\
;

const vscode_tasks_zig =
    \\{
    \\  "version": "2.0.0",
    \\  "tasks": [
    \\    { "label": "zig: build", "type": "shell", "command": "zig build", "group": { "kind": "build", "isDefault": true }, "problemMatcher": "$gcc" },
    \\    { "label": "zig: test", "type": "shell", "command": "zig build test", "group": "test", "problemMatcher": "$gcc" }
    \\  ]
    \\}
    \\
;

const vscode_tasks_cmake =
    \\{
    \\  "version": "2.0.0",
    \\  "tasks": [
    \\    { "label": "cmake: configure (debug)", "type": "shell", "command": "cmake --preset debug" },
    \\    { "label": "cmake: build (debug)", "type": "shell", "command": "cmake --build --preset debug", "dependsOn": "cmake: configure (debug)", "group": { "kind": "build", "isDefault": true }, "problemMatcher": "$gcc" },
    \\    { "label": "ctest (debug)", "type": "shell", "command": "ctest --preset debug", "dependsOn": "cmake: build (debug)", "group": "test" }
    \\  ]
    \\}
    \\
;

const vscode_tasks_cargo =
    \\{
    \\  "version": "2.0.0",
    \\  "tasks": [
    \\    { "label": "cargo: build", "type": "shell", "command": "cargo build", "group": { "kind": "build", "isDefault": true }, "problemMatcher": "$rustc" },
    \\    { "label": "cargo: test", "type": "shell", "command": "cargo test", "group": "test", "problemMatcher": "$rustc" }
    \\  ]
    \\}
    \\
;

const vscode_tasks_dotnet =
    \\{
    \\  "version": "2.0.0",
    \\  "tasks": [
    \\    { "label": "build", "type": "shell", "command": "dotnet build", "group": { "kind": "build", "isDefault": true }, "problemMatcher": "$msCompile" },
    \\    { "label": "test", "type": "shell", "command": "dotnet test", "group": "test", "problemMatcher": "$msCompile" }
    \\  ]
    \\}
    \\
;

// ---- Tests -----------------------------------------------------------------

test "name rules follow each toolchain" {
    const z = indexOf(.zig);
    try std.testing.expect(validateName(z, "hello_zig") == null);
    try std.testing.expect(validateName(z, "hello-zig") != null);
    try std.testing.expect(validateName(z, "fn") != null);
    try std.testing.expect(validateName(z, "u8") != null);
    try std.testing.expect(validateName(z, "std") != null);
    try std.testing.expect(validateName(z, "9lives") != null);
    try std.testing.expect(validateName(z, "aaaaaaaaaabbbbbbbbbbccccccccccdddddddddd") != null);
    const r = indexOf(.rust);
    try std.testing.expect(validateName(r, "my-crate") == null);
    try std.testing.expect(validateName(r, "test") != null);
    try std.testing.expect(validateName(r, "9crate") != null);
    try std.testing.expect(validateName(r, "self") != null);
    const c = indexOf(.c);
    try std.testing.expect(validateName(c, "app") == null);
    try std.testing.expect(validateName(c, "test") != null);
    try std.testing.expect(validateName(c, "a b") != null);
    const p = indexOf(.python);
    try std.testing.expect(validateName(p, "my-app") == null);
    try std.testing.expect(validateName(p, "-app") != null);
    try std.testing.expect(validateName(p, "import") != null);
    try std.testing.expect(validateName(p, "tests") != null);
    const cs = indexOf(.csharp);
    try std.testing.expect(validateName(cs, "Acme.App") == null);
    try std.testing.expect(validateName(cs, "Acme..App") != null);
    try std.testing.expect(validateName(cs, "class") != null);
    try std.testing.expect(validateName(indexOf(.empty), "") != null);
    try std.testing.expect(validateName(indexOf(.empty), "a/b") != null);
}

test "render replaces keys and keeps unknown ones" {
    const gpa = std.testing.allocator;
    const out = try render(gpa, "a {{x}} b {{y}} {z}", &.{.{ .key = "x", .value = "1" }});
    defer gpa.free(out);
    try std.testing.expectEqualStrings("a 1 b {{y}} {z}", out);
}

test "expandLocation wants an absolute path" {
    const gpa = std.testing.allocator;
    try std.testing.expect((try expandLocation(gpa, "relative/dir")) == null);
    const abs = (try expandLocation(gpa, "/tmp/x")).?;
    defer gpa.free(abs);
    try std.testing.expectEqualStrings("/tmp/x", abs);
}

fn tmpRoot(tmp: *std.testing.TmpDir, buf: []u8) ![]const u8 {
    return buf[0..try tmp.dir.realPath(std.testing.io, buf)];
}

/// Generates generator `gen` (with `choices`) into a fresh folder, returns it.
fn makeInTmp(tmp: *std.testing.TmpDir, buf: []u8, gen: usize, name: []const u8, choices: [max_options]u8, with_git: bool) !struct { dir: []u8, result: Result } {
    const gpa = std.testing.allocator;
    const root = try tmpRoot(tmp, buf);
    const dir = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ root, name });
    errdefer gpa.free(dir);
    const result = try generate(gpa, std.testing.io, .{ .gen = gen, .name = name, .dir = dir, .choices = choices, .git = with_git });
    return .{ .dir = dir, .result = result };
}

fn expectFile(dir: []const u8, rel: []const u8) !void {
    const gpa = std.testing.allocator;
    const p = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ dir, rel });
    defer gpa.free(p);
    _ = Io.Dir.cwd().statFile(std.testing.io, p, .{}) catch |e| {
        std.debug.print("missing {s}\n", .{p});
        return e;
    };
}

test "every generator writes its files, and the main file exists" {
    const gpa = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const cases = [_]struct { gen: Id, name: []const u8, files: []const []const u8, choices: [max_options]u8 = @splat(0) }{
        .{ .gen = .empty, .name = "e_empty", .files = &.{ "README.md", ".gitignore", ".editorconfig" } },
        .{ .gen = .zig, .name = "z_app", .files = &.{ "build.zig", "build.zig.zon", "src/main.zig", "src/root.zig", ".gitignore" } },
        .{ .gen = .c, .name = "c-app", .files = &.{ "CMakeLists.txt", "CMakePresets.json", "src/main.c", "src/c_app.c", "include/c_app.h", "tests/test_c_app.c", ".clang-format" } },
        .{ .gen = .cpp, .name = "cpp-app", .files = &.{ "CMakeLists.txt", "src/main.cpp", "include/cpp_app.hpp", "tests/test_cpp_app.cpp" } },
        .{ .gen = .rust, .name = "r-app", .files = &.{ "Cargo.toml", "src/main.rs" } },
        .{ .gen = .rust, .name = "r-lib", .files = &.{ "Cargo.toml", "src/lib.rs" }, .choices = .{ 1, 0, 0, 0 } },
        .{ .gen = .python, .name = "py-app", .files = &.{ "pyproject.toml", "src/py_app/__main__.py", "src/py_app/__init__.py", "tests/test_py_app.py" }, .choices = .{ 0, 1, 0, 0 } },
        .{ .gen = .csharp, .name = "Acme.App", .files = &.{ "Acme.App.slnx", "src/Acme.App/Acme.App.csproj", "src/Acme.App/Program.cs", "tests/Acme.App.Tests/UnitTest1.cs" } },
        .{ .gen = .csharp, .name = "Acme.Lib", .files = &.{ "Acme.Lib.sln", "src/Acme.Lib/Class1.cs" }, .choices = .{ 1, 1, 1, 1 } },
    };
    for (cases) |c| {
        var made = try makeInTmp(&tmp, &buf, indexOf(c.gen), c.name, c.choices, false);
        defer gpa.free(made.dir);
        defer made.result.deinit(gpa);
        for (c.files) |f| try expectFile(made.dir, f);
        _ = Io.Dir.cwd().statFile(std.testing.io, made.result.main_file, .{}) catch |e| {
            std.debug.print("main file missing: {s}\n", .{made.result.main_file});
            return e;
        };
    }
}

test "a failed or refused folder is not left behind" {
    const gpa = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = try tmpRoot(&tmp, &buf);
    const dir = try std.fmt.allocPrint(gpa, "{s}/new", .{root});
    defer gpa.free(dir);
    try std.testing.expectEqual(FolderState.free, folderState(std.testing.io, dir));
    try Io.Dir.cwd().createDirPath(std.testing.io, dir);
    try std.testing.expectEqual(FolderState.empty, folderState(std.testing.io, dir));
    const f = try std.fmt.allocPrint(gpa, "{s}/x", .{dir});
    defer gpa.free(f);
    try Io.Dir.cwd().writeFile(std.testing.io, .{ .sub_path = f, .data = "x" });
    try std.testing.expectEqual(FolderState.not_empty, folderState(std.testing.io, dir));
    try std.testing.expectEqual(FolderState.not_a_folder, folderState(std.testing.io, f));
}

test "freeName skips taken names" {
    const gpa = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = try tmpRoot(&tmp, &buf);
    try tmp.dir.createDir(std.testing.io, "untitled", .default_dir);
    try tmp.dir.createDir(std.testing.io, "untitled1", .default_dir);
    const name = try freeName(gpa, std.testing.io, root);
    defer gpa.free(name);
    try std.testing.expectEqualStrings("untitled2", name);
}

test "a git repository is made with a first commit when asked" {
    const gpa = std.testing.allocator;
    if (!git.available(gpa, std.testing.io)) return error.SkipZigTest;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    var made = try makeInTmp(&tmp, &buf, indexOf(.empty), "with_git", @splat(0), true);
    defer gpa.free(made.dir);
    defer made.result.deinit(gpa);
    try expectFile(made.dir, ".git");
}

// The projects below are built with the real toolchain when it is installed.

fn toolWorks(argv: []const []const u8) bool {
    const gpa = std.testing.allocator;
    const out = git.runFull(gpa, std.testing.io, argv, "/", null, false) catch return false;
    defer gpa.free(out.stdout);
    defer gpa.free(out.stderr);
    return out.ok;
}

fn runIn(dir: []const u8, argv: []const []const u8) !void {
    const gpa = std.testing.allocator;
    const out = try git.runFull(gpa, std.testing.io, argv, dir, null, false);
    defer gpa.free(out.stdout);
    defer gpa.free(out.stderr);
    if (!out.ok) {
        std.debug.print("{s} failed in {s}:\n{s}\n{s}\n", .{ argv[0], dir, out.stdout, out.stderr });
        return error.CommandFailed;
    }
}

test "the Zig template builds and tests" {
    if (!toolWorks(&.{ "zig", "version" })) return error.SkipZigTest;
    const gpa = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    var made = try makeInTmp(&tmp, &buf, indexOf(.zig), "built_zig", @splat(0), false);
    defer gpa.free(made.dir);
    defer made.result.deinit(gpa);
    try runIn(made.dir, &.{ "zig", "build" });
    try runIn(made.dir, &.{ "zig", "build", "test" });
    try expectFile(made.dir, "zig-out/bin/built_zig");
}

test "the C and C++ templates configure, build and test" {
    if (!toolWorks(&.{ "cmake", "--version" })) return error.SkipZigTest;
    const gpa = std.testing.allocator;
    const variants = [_]struct { gen: Id, name: []const u8, choices: [max_options]u8 }{
        .{ .gen = .c, .name = "built-c", .choices = .{ 0, 0, 0, 0 } },
        .{ .gen = .c, .name = "built-c-lib", .choices = .{ 1, 2, 0, 0 } },
        .{ .gen = .cpp, .name = "built-cpp", .choices = .{ 0, 0, 0, 0 } },
        .{ .gen = .cpp, .name = "built-cpp-lib", .choices = .{ 1, 2, 0, 0 } },
    };
    for (variants) |v| {
        var tmp = std.testing.tmpDir(.{});
        defer tmp.cleanup();
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        var made = try makeInTmp(&tmp, &buf, indexOf(v.gen), v.name, v.choices, false);
        defer gpa.free(made.dir);
        defer made.result.deinit(gpa);
        try runIn(made.dir, &.{ "cmake", "--preset", "debug" });
        try runIn(made.dir, &.{ "cmake", "--build", "--preset", "debug" });
        try runIn(made.dir, &.{ "ctest", "--preset", "debug" });
    }
}

test "the Rust template builds and tests" {
    if (!toolWorks(&.{ "cargo", "--version" })) return error.SkipZigTest;
    const gpa = std.testing.allocator;
    for ([_]struct { name: []const u8, kind: u8 }{ .{ .name = "built-rs", .kind = 0 }, .{ .name = "built-rs-lib", .kind = 1 } }) |v| {
        var tmp = std.testing.tmpDir(.{});
        defer tmp.cleanup();
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        var made = try makeInTmp(&tmp, &buf, indexOf(.rust), v.name, .{ v.kind, 0, 0, 0 }, false);
        defer gpa.free(made.dir);
        defer made.result.deinit(gpa);
        try runIn(made.dir, &.{ "cargo", "test", "--offline" });
        try runIn(made.dir, &.{ "cargo", "build", "--offline" });
    }
}

test "the Python template runs" {
    if (!toolWorks(&.{ "python3", "--version" })) return error.SkipZigTest;
    const gpa = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    var made = try makeInTmp(&tmp, &buf, indexOf(.python), "py-run", .{ 0, 1, 0, 0 }, false);
    defer gpa.free(made.dir);
    defer made.result.deinit(gpa);
    try runIn(made.dir, &.{ "python3", "-c", "import sys; sys.path.insert(0, 'src'); import py_run; assert py_run.greeting() == 'Hello, world!'" });
    try runIn(made.dir, &.{ "python3", "-m", "py_compile", "src/py_run/__main__.py", "tests/test_py_run.py" });
}

test "the C# template builds" {
    const home = std.c.getenv("HOME") orelse return error.SkipZigTest;
    var dotnet_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dotnet = std.fmt.bufPrint(&dotnet_buf, "{s}/.dotnet/dotnet", .{std.mem.span(home)}) catch return error.SkipZigTest;
    const exe: []const u8 = if (toolWorks(&.{ "dotnet", "--version" })) "dotnet" else if (toolWorks(&.{ dotnet, "--version" })) dotnet else return error.SkipZigTest;
    const gpa = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    // No test project: restoring xunit needs the network.
    var made = try makeInTmp(&tmp, &buf, indexOf(.csharp), "Built.App", .{ 0, 0, 0, 1 }, false);
    defer gpa.free(made.dir);
    defer made.result.deinit(gpa);
    try runIn(made.dir, &.{ exe, "build", "--nologo", "-v", "q", "Built.App.slnx" });
}
