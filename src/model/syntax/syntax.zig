//! Tree-sitter integration: grammars loaded at runtime from
//! $CS_ZIG_GRAMMARS/<lang>/parser (like Zed's grammar extensions),
//! incremental reparsing driven by buffer changes, and highlight queries
//! (with #eq?/#match?/#any-of? predicates) resolved to theme colors.

const std = @import("std");
const c = @import("c");
const build_options = @import("build_options");
const languages = @import("languages.zig");
const injection = @import("injection.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const Color = @import("../../framework/gpu/scene.zig").Color;
const Rope = @import("../text/rope.zig").Rope;
const Change = @import("../text/buffer.zig").Change;
const Regex = @import("../../framework/util/regex.zig").Regex;
const executor_mod = @import("../../framework/util/executor.zig");
const Executor = executor_mod.Executor;

const Allocator = std.mem.Allocator;
const log = std.log.scoped(.syntax);

pub const LanguageConfig = languages.LanguageConfig;

pub const Predicate = struct {
    kind: enum { eq, not_eq, match, not_match, any_of, not_any_of, has_ancestor, not_has_ancestor, has_parent, not_has_parent },
    capture: u32,
    /// For eq against another capture.
    other_capture: ?u32 = null,
    strings: []const []const u8 = &.{},
    regex: ?*Regex = null,
};

pub const Language = struct {
    config: *const LanguageConfig,
    ts: *const c.TSLanguage,
    query: ?*c.TSQuery,
    capture_colors: []?Color,
    capture_styles: []theme_mod.FontStyle,
    /// Predicates per pattern index.
    predicates: [][]Predicate,
    /// When patterns match the same node, the later one wins (Neovim-style
    /// queries that start with a catch-all like `(identifier) @variable`);
    /// otherwise the first one does (tree-sitter-highlight style).
    last_wins: bool = false,
    /// Query patterns dropped because they didn't compile against the grammar.
    dropped_patterns: usize = 0,
    /// Its injections.scm: which nodes hold code in another language.
    injections: ?*injection.Query = null,
    /// The registry that loaded it, which loads the languages it injects
    /// (set on the main thread).
    registry: ?*Registry = null,
    lib: std.DynLib,
    arena: std.heap.ArenaAllocator,
};

pub const Registry = struct {
    gpa: Allocator,
    grammar_dir: []const u8,
    loaded: std.StringHashMapUnmanaged(?*Language) = .empty,
    theme: *const theme_mod.Theme,
    /// When set, `request` loads grammars on background threads: compiling a
    /// big highlights query takes up to a second.
    executor: ?*Executor = null,
    /// Main thread: a grammar `request` returned null for has loaded.
    listener: ?Listener = null,
    /// Grammars loading in the background.
    loading: std.StringHashMapUnmanaged(void) = .empty,
    /// Background loads that finished, not yet taken in by `collect`.
    finished: ?*LoadJob = null,
    finished_lock: executor_mod.SpinLock = .{},
    /// Background loads still running (they point at the registry).
    running: std.atomic.Value(u32) = .init(0),

    pub const Listener = struct {
        ctx: *anyopaque,
        onLoaded: *const fn (ctx: *anyopaque, lang: *Language) void,
    };

    const LoadJob = struct {
        registry: *Registry,
        config: *const LanguageConfig,
        grammar: []const u8,
        /// Written by the worker before it hands the job over.
        result: ?*Language = null,
        next: ?*LoadJob = null,
    };

    pub fn init(gpa: Allocator, theme: *const theme_mod.Theme) Registry {
        const env = std.c.getenv("CS_ZIG_GRAMMARS");
        const dir = if (env) |e| std.mem.span(e) else build_options.grammar_dir;
        return .{ .gpa = gpa, .grammar_dir = dir, .theme = theme };
    }

    pub fn deinit(self: *Registry) void {
        // Background loads point at the registry: let them finish, then take
        // in what they loaded so it is freed below.
        while (self.running.load(.acquire) > 0) std.Thread.yield() catch {};
        if (self.executor) |e| e.cancel(.{ .ctx = self, .run = collectPosted });
        self.listener = null;
        self.collect();
        self.loading.deinit(self.gpa);
        var it = self.loaded.valueIterator();
        while (it.next()) |maybe| {
            const lang = maybe.* orelse continue;
            if (lang.query) |q| c.ts_query_delete(q);
            for (lang.predicates) |preds| for (preds) |p| if (p.regex) |r| r.deinit();
            if (lang.injections) |iq| iq.deinit();
            lang.arena.deinit();
            lang.lib.close();
            self.gpa.destroy(lang);
        }
        self.loaded.deinit(self.gpa);
    }

    /// Recomputes capture colors after the theme changed.
    pub fn refreshColors(self: *Registry) void {
        var it = self.loaded.valueIterator();
        while (it.next()) |maybe| if (maybe.*) |lang| self.applyTheme(lang);
    }

    /// Capture colors for the current theme (main thread: the theme changes there).
    fn applyTheme(self: *Registry, lang: *Language) void {
        const q = lang.query orelse return;
        for (lang.capture_colors, lang.capture_styles, 0..) |*color, *style, i| {
            var len: u32 = 0;
            const name_ptr = c.ts_query_capture_name_for_id(q, @intCast(i), &len);
            color.* = theme_mod.colorForCapture(&self.theme.syntax, name_ptr[0..len]);
            style.* = theme_mod.styleForCapture(&self.theme.syntax_styles, name_ptr[0..len]);
        }
    }

    /// Loads the grammar for `config` (once), blocking. Null if unavailable.
    pub fn get(self: *Registry, config: *const LanguageConfig) ?*Language {
        const grammar = config.grammar orelse return null;
        // A background load of it may be under way: wait for that one.
        while (self.loading.contains(grammar)) : (std.Thread.yield() catch {}) self.collect();
        if (self.loaded.get(grammar)) |l| return l;
        const lang = self.loader().load(config, grammar);
        self.adopt(grammar, lang);
        return lang;
    }

    /// The language for `config` if its grammar has loaded. Otherwise starts
    /// loading it on a background thread (once) and returns null; the
    /// listener hears when it's ready. Without an executor this is `get`.
    pub fn request(self: *Registry, config: *const LanguageConfig) ?*Language {
        const grammar = config.grammar orelse return null;
        if (self.loaded.get(grammar)) |l| return l;
        const executor = self.executor orelse return self.get(config);
        if (self.loading.contains(grammar)) return null;
        self.startLoad(executor, config, grammar) catch |err| {
            log.warn("loading grammar '{s}' in the foreground: {t}", .{ grammar, err });
            return self.get(config);
        };
        return null;
    }

    fn startLoad(self: *Registry, executor: *Executor, config: *const LanguageConfig, grammar: []const u8) !void {
        const job = try self.gpa.create(LoadJob);
        errdefer self.gpa.destroy(job);
        job.* = .{ .registry = self, .config = config, .grammar = grammar };
        try self.loading.put(self.gpa, grammar, {});
        errdefer _ = self.loading.remove(grammar);
        _ = self.running.fetchAdd(1, .monotonic);
        errdefer _ = self.running.fetchSub(1, .monotonic);
        try executor.spawn(loadThread, .{job});
    }

    /// Background thread. The Language it builds is touched by nothing else
    /// until `collect` takes it in on the main thread.
    fn loadThread(job: *LoadJob) void {
        const self = job.registry;
        const executor = self.executor.?;
        job.result = self.loader().load(job.config, job.grammar);
        self.finished_lock.lock();
        job.next = self.finished;
        self.finished = job;
        self.finished_lock.unlock();
        executor.post(.{ .ctx = self, .run = collectPosted });
        executor.taskDone();
        // Last touch of the registry: `deinit` waits for this.
        _ = self.running.fetchSub(1, .release);
    }

    fn collectPosted(ctx: *anyopaque) void {
        const self: *Registry = @ptrCast(@alignCast(ctx));
        self.collect();
    }

    /// Main thread: takes in finished background loads and tells the listener.
    fn collect(self: *Registry) void {
        self.finished_lock.lock();
        var next = self.finished;
        self.finished = null;
        self.finished_lock.unlock();
        while (next) |job| {
            next = job.next;
            const grammar = job.grammar;
            const lang = job.result;
            self.gpa.destroy(job);
            _ = self.loading.remove(grammar);
            self.adopt(grammar, lang);
            if (lang) |l| if (self.listener) |li| li.onLoaded(li.ctx, l);
        }
    }

    fn adopt(self: *Registry, grammar: []const u8, lang: ?*Language) void {
        if (lang) |l| {
            l.registry = self;
            self.applyTheme(l);
        }
        self.loaded.put(self.gpa, grammar, lang) catch {};
    }

    fn loader(self: *const Registry) Loader {
        return .{ .gpa = self.gpa, .grammar_dir = self.grammar_dir };
    }
};

/// Builds Languages. Safe off the main thread: its fields never change.
pub const Loader = struct {
    gpa: Allocator,
    grammar_dir: []const u8,

    fn load(self: Loader, config: *const LanguageConfig, grammar: []const u8) ?*Language {
        return self.tryLoad(config, grammar) catch |err| {
            log.warn("grammar '{s}' unavailable: {t}", .{ grammar, err });
            return null;
        };
    }

    fn tryLoad(self: Loader, config: *const LanguageConfig, grammar: []const u8) !*Language {
        if (self.grammar_dir.len == 0) return error.NoGrammarDir;
        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        const parser_path = try std.fmt.bufPrint(&path_buf, "{s}/{s}/parser", .{ self.grammar_dir, grammar });
        var lib = try std.DynLib.open(parser_path);
        errdefer lib.close();
        var sym_buf: [128]u8 = undefined;
        const sym = try std.fmt.bufPrintSentinel(&sym_buf, "tree_sitter_{s}", .{grammar}, 0);
        const lang_fn = lib.lookup(*const fn () callconv(.c) ?*const c.TSLanguage, sym) orelse return error.MissingSymbol;
        const ts_lang = lang_fn() orelse return error.NullLanguage;

        const lang = try self.gpa.create(Language);
        errdefer self.gpa.destroy(lang);
        lang.* = .{
            .config = config,
            .ts = ts_lang,
            .query = null,
            .capture_colors = &.{},
            .capture_styles = &.{},
            .predicates = &.{},
            .lib = lib,
            .arena = .init(self.gpa),
        };
        errdefer lang.arena.deinit();

        // Assemble the highlight query, inherited grammars first.
        var source: std.ArrayList(u8) = .empty;
        defer source.deinit(self.gpa);
        for (config.query_inherits) |parent| try self.appendQueryFile(&source, parent);
        try self.appendQueryFile(&source, grammar);
        for (config.query_extends) |extra| try self.appendQueryFile(&source, extra);
        if (source.items.len > 0) self.buildQuery(lang, source.items) catch |err| {
            log.warn("highlight query for {s} failed: {t}", .{ grammar, err });
        };
        lang.injections = injection.load(self, lang, grammar) catch |err| blk: {
            log.warn("injection query for {s} failed: {t}", .{ grammar, err });
            break :blk null;
        };
        return lang;
    }

    fn appendQueryFile(self: Loader, out: *std.ArrayList(u8), grammar: []const u8) !void {
        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        const path = try std.fmt.bufPrintSentinel(&path_buf, "{s}/{s}/queries/highlights.scm", .{ self.grammar_dir, grammar }, 0);
        const file = std.c.fopen(path, "rb") orelse return;
        defer _ = std.c.fclose(file);
        var buf: [16384]u8 = undefined;
        while (true) {
            const n = std.c.fread(&buf, 1, buf.len, file);
            if (n == 0) break;
            try out.appendSlice(self.gpa, buf[0..n]);
        }
        try out.append(self.gpa, '\n');
    }

    fn buildQuery(self: Loader, lang: *Language, source: []const u8) !void {
        var src = source;
        var err_offset: u32 = 0;
        var err_type: c.TSQueryError = c.TSQueryErrorNone;
        var query = c.ts_query_new(lang.ts, src.ptr, @intCast(src.len), &err_offset, &err_type);
        // Inherited queries may reference nodes the child grammar lacks;
        // drop the offending pattern and retry.
        var scratch: std.ArrayList(u8) = .empty;
        defer scratch.deinit(self.gpa);
        var attempts: usize = 0;
        while (query == null and attempts < 64) : (attempts += 1) {
            log.debug("query error {d} at {d} in {s}", .{ err_type, err_offset, lang.config.name });
            const removed = try removePatternAt(self.gpa, &scratch, src, err_offset);
            if (!removed) break;
            src = scratch.items;
            query = c.ts_query_new(lang.ts, src.ptr, @intCast(src.len), &err_offset, &err_type);
        }
        const q = query orelse return error.QueryCompileFailed;
        lang.dropped_patterns = attempts;
        lang.query = q;
        const alloc = lang.arena.allocator();
        lang.last_wins = lang.config.query_last_wins orelse catchAllFirst(src);

        // Colored by Registry.applyTheme once the Language reaches the main thread.
        const capture_count = c.ts_query_capture_count(q);
        lang.capture_colors = try alloc.alloc(?Color, capture_count);
        lang.capture_styles = try alloc.alloc(theme_mod.FontStyle, capture_count);
        @memset(lang.capture_colors, null);
        @memset(lang.capture_styles, .normal);

        const pattern_count = c.ts_query_pattern_count(q);
        lang.predicates = try alloc.alloc([]Predicate, pattern_count);
        for (lang.predicates, 0..) |*preds, pattern| {
            preds.* = try self.parsePredicates(alloc, q, @intCast(pattern));
        }
    }

    pub fn parsePredicates(self: Loader, alloc: Allocator, q: *c.TSQuery, pattern: u32) ![]Predicate {
        var step_count: u32 = 0;
        const steps = c.ts_query_predicates_for_pattern(q, pattern, &step_count);
        var out: std.ArrayList(Predicate) = .empty;
        var i: usize = 0;
        while (i < step_count) {
            // One predicate: string name, args..., done.
            var end = i;
            while (end < step_count and steps[end].type != c.TSQueryPredicateStepTypeDone) end += 1;
            defer i = end + 1;
            if (end == i or steps[i].type != c.TSQueryPredicateStepTypeString) continue;
            const name = stringValue(q, steps[i].value_id);
            const args = steps[i + 1 .. end];
            if (args.len < 2 or args[0].type != c.TSQueryPredicateStepTypeCapture) continue;
            const capture = args[0].value_id;
            const eql = std.mem.eql;
            if (eql(u8, name, "eq?") or eql(u8, name, "not-eq?")) {
                const kind: @FieldType(Predicate, "kind") = if (name[0] == 'n') .not_eq else .eq;
                if (args[1].type == c.TSQueryPredicateStepTypeCapture) {
                    try out.append(alloc, .{ .kind = kind, .capture = capture, .other_capture = args[1].value_id });
                } else {
                    const strs = try alloc.alloc([]const u8, 1);
                    strs[0] = stringValue(q, args[1].value_id);
                    try out.append(alloc, .{ .kind = kind, .capture = capture, .strings = strs });
                }
            } else if (eql(u8, name, "match?") or eql(u8, name, "not-match?") or eql(u8, name, "lua-match?") or eql(u8, name, "not-lua-match?")) {
                const pattern_src = stringValue(q, args[1].value_id);
                const translated = if (std.mem.indexOf(u8, name, "lua") != null) try luaToRegex(alloc, pattern_src) else pattern_src;
                const re = try alloc.create(Regex);
                re.* = Regex.compile(self.gpa, translated, .{}) catch {
                    log.debug("unsupported regex in query: {s}", .{pattern_src});
                    continue;
                };
                try out.append(alloc, .{ .kind = if (name[0] == 'n') .not_match else .match, .capture = capture, .regex = re });
            } else if (eql(u8, name, "any-of?") or eql(u8, name, "not-any-of?")) {
                const strs = try alloc.alloc([]const u8, args.len - 1);
                for (args[1..], strs) |a, *s| s.* = stringValue(q, a.value_id);
                try out.append(alloc, .{ .kind = if (name[0] == 'n') .not_any_of else .any_of, .capture = capture, .strings = strs });
            } else if (std.mem.endsWith(u8, name, "has-ancestor?") or std.mem.endsWith(u8, name, "has-parent?")) {
                // nvim-treesitter's: some ancestor (or the parent) is one of these node types.
                const strs = try alloc.alloc([]const u8, args.len - 1);
                for (args[1..], strs) |a, *s| s.* = stringValue(q, a.value_id);
                const negated = name[0] == 'n';
                const kind: @FieldType(Predicate, "kind") = if (std.mem.endsWith(u8, name, "parent?"))
                    (if (negated) .not_has_parent else .has_parent)
                else
                    (if (negated) .not_has_ancestor else .has_ancestor);
                try out.append(alloc, .{ .kind = kind, .capture = capture, .strings = strs });
            }
            // Other predicates (#set!, #is-not? local, ...) are treated as satisfied.
        }
        return out.toOwnedSlice(alloc);
    }
};

pub fn stringValue(q: *c.TSQuery, id: u32) []const u8 {
    var len: u32 = 0;
    const ptr = c.ts_query_string_value_for_id(q, id, &len);
    return ptr[0..len];
}

/// Removes the top-level S-expression containing `offset` from `src`.
pub fn removePatternAt(gpa: Allocator, out: *std.ArrayList(u8), src: []const u8, offset: u32) !bool {
    if (offset >= src.len) return false;
    // Find the start of the enclosing top-level form by scanning depth from the beginning.
    var depth: i32 = 0;
    var form_start: ?usize = null;
    var i: usize = 0;
    var in_string = false;
    var start: usize = 0;
    var end: usize = src.len;
    while (i < src.len) : (i += 1) {
        const ch = src[i];
        if (in_string) {
            if (ch == '\\') i += 1 else if (ch == '"') in_string = false;
            continue;
        }
        switch (ch) {
            '"' => in_string = true,
            ';' => {
                while (i < src.len and src[i] != '\n') i += 1;
            },
            '(', '[' => {
                if (depth == 0) form_start = i;
                depth += 1;
            },
            ')', ']' => {
                depth -= 1;
                if (depth == 0) {
                    if (form_start) |fs| if (fs <= offset and offset <= i) {
                        start = fs;
                        end = i + 1;
                        // Trailing captures / quantifiers belong to the form.
                        while (end < src.len and src[end] != '\n' and src[end] != '(' and src[end] != '[') end += 1;
                        break;
                    };
                }
            },
            else => {},
        }
    } else return false;
    var next: std.ArrayList(u8) = .empty;
    try next.appendSlice(gpa, src[0..start]);
    try next.appendSlice(gpa, src[end..]);
    out.deinit(gpa);
    out.* = next;
    return true;
}

/// Translates the common subset of Lua patterns used in queries to regex syntax.
pub fn luaToRegex(alloc: Allocator, pat: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    // Inside a `[...]` set, classes contribute bare ranges and `-` is a range.
    var in_set = false;
    var i: usize = 0;
    while (i < pat.len) : (i += 1) {
        if (pat[i] == '%' and i + 1 < pat.len) {
            i += 1;
            const class: [2][]const u8 = switch (pat[i]) {
                'd' => .{ "\\d", "0-9" },
                'a' => .{ "[A-Za-z]", "A-Za-z" },
                'l' => .{ "[a-z]", "a-z" },
                'u' => .{ "[A-Z]", "A-Z" },
                'w' => .{ "[A-Za-z0-9]", "A-Za-z0-9" },
                'x' => .{ "[0-9A-Fa-f]", "0-9A-Fa-f" },
                's' => .{ "\\s", "\\s" },
                'p' => .{ "[!-/:-@\\[-`{-~]", "!-/:-@\\[-`{-~" },
                else => .{ &.{ '\\', pat[i] }, &.{ '\\', pat[i] } },
            };
            try out.appendSlice(alloc, class[@intFromBool(in_set)]);
        } else if (in_set) {
            // A `]` right after `[` or `[^` is a literal member.
            if (pat[i] == ']' and out.items[out.items.len - 1] != '[' and out.items[out.items.len - 1] != '^') in_set = false;
            try out.append(alloc, pat[i]);
        } else if (pat[i] == '[') {
            in_set = true;
            try out.append(alloc, '[');
            if (i + 1 < pat.len and pat[i + 1] == '^') {
                i += 1;
                try out.append(alloc, '^');
            }
        } else if (pat[i] == '-' and i > 0) {
            try out.appendSlice(alloc, "*?");
        } else {
            try out.append(alloc, pat[i]);
        }
    }
    return out.items;
}

test "luaToRegex" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const cases = [_][2][]const u8{
        .{ "^[A-Z][A-Z0-9_]*$", "^[A-Z][A-Z0-9_]*$" },
        .{ "^[%u][%u%d_]*$", "^[A-Z][A-Z0-9_]*$" },
        .{ "^:?[%l]", "^:?[a-z]" },
        .{ "^%u", "^[A-Z]" },
        .{ "^[-][-](%s?)@", "^[-][-](\\s?)@" },
        .{ "^[^/%s]*$", "^[^/\\s]*$" },
        .{ "^%.%-%S*", "^\\.\\-\\S*" },
        .{ "a.-b", "a.*?b" },
    };
    for (cases) |case| try std.testing.expectEqualStrings(case[1], try luaToRegex(arena.allocator(), case[0]));
}

// Needs $CS_ZIG_GRAMMARS (nix develop); skipped without it.
test "Registry.request loads in the background, once per grammar" {
    const gpa = std.testing.allocator;
    const theme = theme_mod.build(theme_mod.schemeByName("Mariana").?);
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    var registry = Registry.init(gpa, &theme);
    defer registry.deinit();
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_z = std.fmt.bufPrintSentinel(&dir_buf, "{s}", .{registry.grammar_dir}, 0) catch return error.SkipZigTest;
    if (registry.grammar_dir.len == 0 or std.c.access(dir_z, std.c.F_OK) != 0) return error.SkipZigTest;
    registry.executor = &executor;
    const Seen = struct {
        langs: [4]*Language = undefined,
        count: usize = 0,
        fn onLoaded(ctx: *anyopaque, lang: *Language) void {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            self.langs[self.count] = lang;
            self.count += 1;
        }
    };
    var seen: Seen = .{};
    registry.listener = .{ .ctx = &seen, .onLoaded = Seen.onLoaded };

    const json = languages.forPath("a.json");
    try std.testing.expect(registry.request(json) == null);
    try std.testing.expect(registry.request(json) == null);
    try std.testing.expectEqual(1, registry.loading.count());
    var waited: usize = 0;
    while (seen.count == 0 and waited < 3000) : (waited += 1) {
        executor.drain();
        var req: std.c.timespec = .{ .sec = 0, .nsec = 10 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
    }
    try std.testing.expectEqual(1, seen.count);
    const lang = seen.langs[0];
    try std.testing.expectEqual(lang, registry.request(json).?);
    try std.testing.expect(lang.query != null);
    // Colored on the main thread, for the registry's theme.
    var colored = false;
    for (lang.capture_colors) |color| colored = colored or color != null;
    try std.testing.expect(colored);

    // A blocking get joins a load in flight instead of starting another.
    const nim = languages.forPath("a.nim");
    try std.testing.expect(registry.request(nim) == null);
    const nim_lang = registry.get(nim).?;
    try std.testing.expectEqual(2, seen.count);
    try std.testing.expectEqual(nim_lang, seen.langs[1]);
    try std.testing.expectEqual(0, registry.loading.count());

    // deinit waits for a load still running and frees what it built.
    try std.testing.expect(registry.request(languages.forPath("a.sv")) == null);
}

const TestPaint = struct { color: Color, style: theme_mod.FontStyle };

/// The paint of every byte, as the editor lays spans over each other.
fn paintOf(gpa: Allocator, syn: *Syntax, rope: *const Rope) ![]?TestPaint {
    var spans: std.ArrayList(HighlightSpan) = .empty;
    defer spans.deinit(gpa);
    syn.highlight(rope, 0, rope.len(), &spans);
    const paint = try gpa.alloc(?TestPaint, rope.len());
    @memset(paint, null);
    for (spans.items) |s| @memset(paint[s.start..s.end], TestPaint{ .color = s.color, .style = s.style });
    return paint;
}

// Needs $CS_ZIG_GRAMMARS (nix develop); skipped without it.
test "parses over budget finish on a worker; edits made meanwhile are replayed on their trees" {
    const gpa = std.testing.allocator;
    const theme = theme_mod.build(theme_mod.schemeByName("Mariana").?);
    var registry = Registry.init(gpa, &theme);
    defer registry.deinit();
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_z = std.fmt.bufPrintSentinel(&dir_buf, "{s}", .{registry.grammar_dir}, 0) catch return error.SkipZigTest;
    if (registry.grammar_dir.len == 0 or std.c.access(dir_z, std.c.F_OK) != 0) return error.SkipZigTest;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    // Every parse overruns.
    const budgets = .{ first_parse_budget_ns, reparse_budget_ns };
    defer {
        first_parse_budget_ns = budgets[0];
        reparse_budget_ns = budgets[1];
    }
    first_parse_budget_ns = 0;
    reparse_budget_ns = 0;
    // Layers parse on this thread here, so that a paint of the whole file
    // is complete (their workers have tests of their own); injection
    // queries may go to workers until the final paint.
    const injection_budgets = .{ injection.layer_budget_ns, injection.query_budget_ns };
    defer {
        injection.layer_budget_ns = injection_budgets[0];
        injection.query_budget_ns = injection_budgets[1];
    }
    injection.layer_budget_ns = std.math.maxInt(u64);
    // What Markdown injects loads up front (blocking: the registry has no executor).
    for ([_][]const u8{ "a.md", "a.rs" }) |p| _ = registry.get(languages.forPath(p)).?;

    // Each edit replaces the first [0] from a third of the way in with [1].
    const cases = [_]struct { path: []const u8, unit: []const u8, edits: []const [2][]const u8 }{
        .{
            .path = "big.zig",
            .unit = "const S = struct {\n    x: u32 = 0x1f,\n    fn f(self: *S) void {\n        self.x += 1; // more\n    }\n};\n",
            .edits = &.{
                .{ "0x1f,", "0x2f, // c" },
                .{ "self.x += 1;", "self.x += \"a\".len;" },
                .{ "const S", "const T = 1;\nconst S" },
                .{ "fn f(", "pub fn f(" },
            },
        },
        .{
            .path = "notes.md",
            .unit = "# Title\n\nSome *emphasis* and `code` here.\n\n```rust\nfn main() { let x = 1; }\n```\n\n",
            // An unclosed fence swallows all that follows.
            .edits = &.{ .{ "Some", "```\nSome" }, .{ "*emphasis*", "**emphasis*" }, .{ "Title", "" } },
        },
    };
    for (cases) |case| {
        var text: std.ArrayList(u8) = .empty;
        defer text.deinit(gpa);
        for (0..4000) |_| try text.appendSlice(gpa, case.unit);
        var rope = try Rope.fromSlice(gpa, text.items);
        defer rope.deinit(gpa);
        const lang = registry.get(languages.forPath(case.path)).?;
        var syn = try Syntax.init(gpa, lang);
        defer syn.deinit();
        syn.executor = &executor;

        // The first parse leaves the tree to a worker: no highlighting yet.
        syn.update(&rope);
        try std.testing.expect(syn.job != null and syn.tree == null);
        const before = try paintOf(gpa, &syn, &rope);
        gpa.free(before);

        // Edit while parses are in flight...
        injection.query_budget_ns = injection_budgets[1];
        var round: usize = 0;
        var waited: usize = 0;
        while (syn.job != null or syn.dirty or syn.parsing()) : (waited += 1) {
            if (round < case.edits.len and syn.job != null) {
                const e = case.edits[round];
                round += 1;
                const len = rope.len();
                const all = try rope.toOwnedSlice(gpa);
                defer gpa.free(all);
                const start = std.mem.indexOfPos(u8, all, len / 3 + round * 997, e[0]).?;
                const end = start + e[0].len;
                const new_text = e[1];
                const start_point = rope.offsetToPoint(start);
                const old_end_point = rope.offsetToPoint(end);
                try rope.replace(gpa, start, end, new_text);
                syn.applyChange(.{
                    .start = start,
                    .old_len = end - start,
                    .new_len = new_text.len,
                    .start_point = start_point,
                    .old_end_point = old_end_point,
                    .new_end_point = rope.offsetToPoint(start + new_text.len),
                });
                // ...and paints with whatever tree there is meanwhile.
                const meanwhile = try paintOf(gpa, &syn, &rope);
                gpa.free(meanwhile);
            }
            try std.testing.expect(waited < 3000);
            var req: std.c.timespec = .{ .sec = 0, .nsec = 2 * std.time.ns_per_ms };
            _ = std.c.nanosleep(&req, null);
            executor.drain();
            syn.update(&rope);
        }
        try std.testing.expectEqual(case.edits.len, round);
        injection.query_budget_ns = std.math.maxInt(u64);

        // The same as parsing the final text in one go.
        var fresh = try Syntax.init(gpa, lang);
        defer fresh.deinit();
        fresh.update(&rope);
        const got = try paintOf(gpa, &syn, &rope);
        defer gpa.free(got);
        const want = try paintOf(gpa, &fresh, &rope);
        defer gpa.free(want);
        var colored: usize = 0;
        for (got, want, 0..) |g, w, i| {
            colored += @intFromBool(w != null);
            if (!std.meta.eql(g, w)) {
                std.debug.print("{s}: byte {d} painted differently\n", .{ case.path, i });
                return error.TestUnexpectedResult;
            }
        }
        try std.testing.expect(colored > rope.len() / 4);
    }
}

test "a buffer closed mid-parse stops its worker" {
    const gpa = std.testing.allocator;
    const theme = theme_mod.build(theme_mod.schemeByName("Mariana").?);
    var registry = Registry.init(gpa, &theme);
    defer registry.deinit();
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_z = std.fmt.bufPrintSentinel(&dir_buf, "{s}", .{registry.grammar_dir}, 0) catch return error.SkipZigTest;
    if (registry.grammar_dir.len == 0 or std.c.access(dir_z, std.c.F_OK) != 0) return error.SkipZigTest;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const budget = first_parse_budget_ns;
    defer first_parse_budget_ns = budget;
    first_parse_budget_ns = 0;
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    for (0..20000) |_| try text.appendSlice(gpa, "fn f() void { return; }\n");
    var rope = try Rope.fromSlice(gpa, text.items);
    defer rope.deinit(gpa);
    var syn = try Syntax.init(gpa, registry.get(languages.forPath("a.zig")).?);
    syn.executor = &executor;
    syn.update(&rope);
    try std.testing.expect(syn.parsing());
    syn.deinit();
    try std.testing.expectEqual(0, executor.active.load(.acquire));
}

/// Replaces [start, end) of `rope` with `new_text`, as a buffer edit does.
fn replaceForTest(gpa: Allocator, syn: *Syntax, rope: *Rope, start: usize, end: usize, new_text: []const u8) !void {
    const start_point = rope.offsetToPoint(start);
    const old_end_point = rope.offsetToPoint(end);
    try rope.replace(gpa, start, end, new_text);
    syn.applyChange(.{
        .start = start,
        .old_len = end - start,
        .new_len = new_text.len,
        .start_point = start_point,
        .old_end_point = old_end_point,
        .new_end_point = rope.offsetToPoint(start + new_text.len),
    });
}

/// Updates `syn` until its workers are done, then expects it to paint
/// `rope` as parsing it in one go does.
fn expectSettledPaint(gpa: Allocator, syn: *Syntax, rope: *const Rope, executor: *Executor) !void {
    var waited: usize = 0;
    while (syn.job != null or syn.dirty or syn.parsing()) : (waited += 1) {
        try std.testing.expect(waited < 3000);
        var req: std.c.timespec = .{ .sec = 0, .nsec = 2 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
        executor.drain();
        syn.update(rope);
    }
    var fresh = try Syntax.init(gpa, syn.language);
    defer fresh.deinit();
    fresh.update(rope);
    const got = try paintOf(gpa, syn, rope);
    defer gpa.free(got);
    const want = try paintOf(gpa, &fresh, rope);
    defer gpa.free(want);
    try std.testing.expectEqualSlices(?TestPaint, want, got);
}

// Needs $CS_ZIG_GRAMMARS (nix develop); skipped without it.
test "edits in a long token reparse on a worker; text a parse reads past its deadline is cut off" {
    const gpa = std.testing.allocator;
    const theme = theme_mod.build(theme_mod.schemeByName("Mariana").?);
    var registry = Registry.init(gpa, &theme);
    defer registry.deinit();
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_z = std.fmt.bufPrintSentinel(&dir_buf, "{s}", .{registry.grammar_dir}, 0) catch return error.SkipZigTest;
    if (registry.grammar_dir.len == 0 or std.c.access(dir_z, std.c.F_OK) != 0) return error.SkipZigTest;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const saved = .{ first_parse_budget_ns, reparse_budget_ns, token_overrun_ns };
    defer {
        first_parse_budget_ns = saved[0];
        reparse_budget_ns = saved[1];
        token_overrun_ns = saved[2];
    }
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    try text.appendSlice(gpa, "const b = 2;\n");
    const b = "const b = ".len;
    for (0..2000) |_| try text.appendSlice(gpa, "fn f() void { return; }\n");
    try text.appendSlice(gpa, "// ");
    const comment = text.items.len;
    try text.appendNTimes(gpa, 'x', 2 * long_token_min);
    try text.appendSlice(gpa, "\n");
    var rope = try Rope.fromSlice(gpa, text.items);
    defer rope.deinit(gpa);
    var syn = try Syntax.init(gpa, registry.get(languages.forPath("a.zig")).?);
    defer syn.deinit();
    syn.executor = &executor;

    // A parse past its deadline on its first read: cut off there, it
    // starts over on a worker. First parse, then a reparse.
    first_parse_budget_ns = 0;
    reparse_budget_ns = 0;
    token_overrun_ns = 0;
    syn.update(&rope);
    try std.testing.expect(syn.job.?.restart);
    try expectSettledPaint(gpa, &syn, &rope, &executor);
    try replaceForTest(gpa, &syn, &rope, b, b + 1, "3");
    try std.testing.expect(!syn.long_token);
    syn.update(&rope);
    try std.testing.expect(syn.job.?.restart);
    try expectSettledPaint(gpa, &syn, &rope, &executor);

    // With all the time in the world, an edit in the long comment still
    // goes to a worker...
    reparse_budget_ns = std.math.maxInt(u64);
    token_overrun_ns = saved[2];
    const mid = comment + long_token_min;
    try replaceForTest(gpa, &syn, &rope, mid, mid, "y");
    try std.testing.expect(syn.long_token);
    syn.update(&rope);
    try std.testing.expect(!syn.job.?.restart);
    // ...and so does one made meanwhile, from the tree it returns.
    try replaceForTest(gpa, &syn, &rope, mid, mid + 1, "");
    var waited: usize = 0;
    while (!syn.job.?.parsed.load(.acquire)) : (waited += 1) {
        try std.testing.expect(waited < 3000);
        var req: std.c.timespec = .{ .sec = 0, .nsec = 2 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
    }
    syn.update(&rope);
    try std.testing.expect(syn.job != null);
    try expectSettledPaint(gpa, &syn, &rope, &executor);
    // Outside it, the reparse finishes here.
    try replaceForTest(gpa, &syn, &rope, b, b + 1, "4");
    try std.testing.expect(!syn.long_token);
    syn.update(&rope);
    try std.testing.expect(syn.job == null and !syn.dirty);
}

/// Whether a highlights query opens with a bare catch-all pattern such as
/// `(identifier) @variable` before its first function capture: then it relies
/// on later patterns overriding earlier ones.
fn catchAllFirst(src: []const u8) bool {
    const first_function = std.mem.indexOf(u8, src, "@function") orelse src.len;
    for ([_][]const u8{ "(identifier) @variable\n", "(identifier) @variable\r" }) |needle| {
        if (std.mem.indexOf(u8, src, needle)) |at| if (at < first_function) return true;
    }
    return false;
}

/// The node's text in `buf`, valid until it changes.
pub fn readNodeText(gpa: Allocator, buf: *std.ArrayList(u8), rope: *const Rope, node: c.TSNode) []const u8 {
    const s = c.ts_node_start_byte(node);
    const e = c.ts_node_end_byte(node);
    buf.clearRetainingCapacity();
    var it = rope.chunks(s, e);
    while (it.next()) |chunk| buf.appendSlice(gpa, chunk) catch break;
    return buf.items;
}

/// `Syntax.predicatesPass` for any thread: node text goes to `text`, and
/// regexes match with `scratch` (null: their own, on the main thread).
pub fn checkPredicates(gpa: Allocator, preds: []const Predicate, rope: *const Rope, match: *const c.TSQueryMatch, text: *std.ArrayList(u8), scratch: ?*Regex.Scratch) bool {
    if (preds.len == 0) return true;
    const captures = match.captures[0..match.capture_count];
    for (preds) |p| {
        var node: ?c.TSNode = null;
        for (captures) |cap| if (cap.index == p.capture) {
            node = cap.node;
            break;
        };
        const n = node orelse continue;
        switch (p.kind) {
            .has_ancestor, .not_has_ancestor, .has_parent, .not_has_parent => {
                const parent_only = p.kind == .has_parent or p.kind == .not_has_parent;
                var found = false;
                var up = c.ts_node_parent(n);
                while (!found and !c.ts_node_is_null(up)) : (up = c.ts_node_parent(up)) {
                    const node_type = std.mem.span(c.ts_node_type(up));
                    for (p.strings) |s| found = found or std.mem.eql(u8, s, node_type);
                    if (parent_only) break;
                }
                if (found != (p.kind == .has_ancestor or p.kind == .has_parent)) return false;
                continue;
            },
            else => {},
        }
        const node_text = readNodeText(gpa, text, rope, n);
        const ok = switch (p.kind) {
            .has_ancestor, .not_has_ancestor, .has_parent, .not_has_parent => unreachable,
            .eq, .not_eq => blk: {
                var equal = false;
                if (p.other_capture) |other| {
                    const owned = gpa.dupe(u8, node_text) catch return true;
                    defer gpa.free(owned);
                    for (captures) |cap| if (cap.index == other) {
                        equal = std.mem.eql(u8, owned, readNodeText(gpa, text, rope, cap.node));
                    };
                } else equal = std.mem.eql(u8, node_text, p.strings[0]);
                break :blk equal == (p.kind == .eq);
            },
            .match, .not_match => blk: {
                const re = p.regex.?;
                const m = if (scratch) |sc| re.searchIn(sc, node_text, 0) else re.search(node_text, 0);
                break :blk (m != null) == (p.kind == .match);
            },
            .any_of, .not_any_of => blk: {
                var found = false;
                for (p.strings) |s| if (std.mem.eql(u8, s, node_text)) {
                    found = true;
                    break;
                };
                break :blk found == (p.kind == .any_of);
            },
        };
        if (!ok) return false;
    }
    return true;
}

pub const HighlightSpan = struct {
    start: usize,
    end: usize,
    color: Color,
    style: theme_mod.FontStyle = .normal,
};

/// How long a parse may run on the UI thread before `update` hands it to a
/// worker (ns). A first parse gets longer so that ordinary files open
/// highlighted; typing gets a slice of a frame.
pub var first_parse_budget_ns: u64 = 25 * std.time.ns_per_ms;
pub var reparse_budget_ns: u64 = 3 * std.time.ns_per_ms;
/// Tree-sitter checks the budget only between tokens, and lexes a token in
/// one go (2 MB of inline <script> text: ~40 ms). A parse still reading
/// text this long past its deadline (ns) finds it ends there, and starts
/// over on a worker.
pub var token_overrun_ns: u64 = 1 * std.time.ns_per_ms;
/// Edits in a token this long (bytes) or longer reparse on a worker right
/// away: lexing it again takes about a reparse budget by itself.
const long_token_min = 128 << 10;

/// Parse state for one buffer.
pub const Syntax = struct {
    gpa: Allocator,
    language: *Language,
    parser: *c.TSParser,
    tree: ?*c.TSTree = null,
    cursor: *c.TSQueryCursor,
    dirty: bool = true,
    text_buf: std.ArrayList(u8) = .empty,
    /// Trees of the code embedded in this one (see injection.zig).
    injections: injection.Layers = .{},
    /// Where parses that run over budget finish. Without one, every parse
    /// runs to the end on the calling thread.
    executor: ?*Executor = null,
    /// A parse finishing on a worker, which has `parser` meanwhile.
    job: ?*ParseJob = null,
    /// Edits since `job` took its snapshot, replayed on the tree it returns.
    pending_edits: std.ArrayList(c.TSInputEdit) = .empty,
    /// An edit couldn't be queued: `job`'s tree is of no use.
    job_stale: bool = false,
    /// An edit since the last parse fell in a token of `long_token_min`
    /// bytes or more, which the next parse lexes again.
    long_token: bool = false,

    pub fn init(gpa: Allocator, language: *Language) !Syntax {
        const parser = c.ts_parser_new() orelse return error.OutOfMemory;
        if (!c.ts_parser_set_language(parser, language.ts)) {
            c.ts_parser_delete(parser);
            return error.IncompatibleGrammar;
        }
        const executor = if (language.registry) |r| r.executor else null;
        return .{ .gpa = gpa, .language = language, .parser = parser, .cursor = c.ts_query_cursor_new().?, .executor = executor };
    }

    pub fn deinit(self: *Syntax) void {
        if (self.job) |job| {
            // It stops at its next progress check.
            job.cancel.store(true, .release);
            if (job.wait()) |t| deleteTree(t);
            job.destroy();
        }
        self.pending_edits.deinit(self.gpa);
        if (self.tree) |t| deleteTree(t);
        c.ts_parser_delete(self.parser);
        c.ts_query_cursor_delete(self.cursor);
        self.text_buf.deinit(self.gpa);
        self.injections.deinit(self.gpa);
    }

    /// A grammar finished loading in the background: re-injects if this
    /// buffer embeds code in it. Returns whether to repaint.
    pub fn injectedLanguageLoaded(self: *Syntax) bool {
        if (!self.injections.languageLoaded()) return false;
        self.dirty = true;
        return true;
    }

    pub fn applyChange(self: *Syntax, change: Change) void {
        self.dirty = true;
        const input: c.TSInputEdit = .{
            .start_byte = @intCast(change.start),
            .old_end_byte = @intCast(change.start + change.old_len),
            .new_end_byte = @intCast(change.start + change.new_len),
            .start_point = .{ .row = change.start_point.row, .column = change.start_point.column },
            .old_end_point = .{ .row = change.old_end_point.row, .column = change.old_end_point.column },
            .new_end_point = .{ .row = change.new_end_point.row, .column = change.new_end_point.column },
        };
        // The text a background parse reads is from before this edit.
        if (self.job != null) self.pending_edits.append(self.gpa, input) catch {
            self.job_stale = true;
        };
        const tree = self.tree orelse return;
        c.ts_tree_edit(tree, &input);
        if (self.executor != null and !self.long_token) self.long_token = inLongToken(tree, &input);
        self.injections.edit(self.gpa, &input);
    }

    pub fn ropeInput(rope: *const Rope) c.TSInput {
        return .{
            .payload = @constCast(rope),
            .read = readRope,
            .encoding = c.TSInputEncodingUTF8,
            .decode = null,
        };
    }

    fn readRope(payload: ?*anyopaque, byte_index: u32, _: c.TSPoint, bytes_read: [*c]u32) callconv(.c) [*c]const u8 {
        const rope: *const Rope = @ptrCast(@alignCast(payload));
        if (byte_index >= rope.len()) {
            bytes_read.* = 0;
            return "";
        }
        var it = rope.chunks(byte_index, rope.len());
        const chunk = it.next() orelse {
            bytes_read.* = 0;
            return "";
        };
        bytes_read.* = @intCast(chunk.len);
        return chunk.ptr;
    }

    /// Re-parses if the buffer changed since the last parse (incremental).
    /// A parse that runs over its budget moves to a worker as it stands and
    /// a later call takes in the tree it returns; until then the old tree,
    /// edited along with the text, keeps highlighting (a first parse shows
    /// none). The worker wakes the event loop, whose repaint calls this.
    pub fn update(self: *Syntax, rope: *const Rope) void {
        self.injections.beginFrame(self.executor != null);
        // What a worker found injected all over the tree: what changed since
        // is looked at now if the tree is up to date, else once it is.
        if (self.injections.poll(self) and self.job == null and !self.dirty) self.injections.refresh(self, rope);
        if (self.job) |job| {
            if (!job.parsed.load(.acquire)) return;
            self.finishJob(job, rope);
        }
        if (!self.dirty) return;
        const budget: ?u64 = if (self.executor == null) null else if (self.tree == null) first_parse_budget_ns else reparse_budget_ns;
        if (budget != null and self.long_token) {
            // No budget would stop it here.
            self.startJob(rope, false) catch |err| log.warn("lexing a long token in the foreground: {t}", .{err});
            if (self.job != null) return;
        }
        var clock: Budget = .{ .deadline = if (budget) |b| nowNs() +| b else 0 };
        const options: c.TSParseOptions = .{ .payload = &clock, .progress_callback = if (budget != null) &Budget.check else null };
        const input = if (budget != null) clock.input(rope) else ropeInput(rope);
        var new_tree = c.ts_parser_parse_with_options(self.parser, self.tree, input, options);
        if (clock.cut) if (new_tree) |t| {
            // Of the text up to the cut.
            deleteTree(t);
            new_tree = null;
        };
        if (new_tree == null and clock.expired) {
            self.startJob(rope, clock.cut) catch |err| {
                log.warn("finishing a parse in the foreground: {t}", .{err});
                if (clock.cut) c.ts_parser_reset(self.parser);
                // Picks up where the budget stopped it, or starts over.
                new_tree = c.ts_parser_parse(self.parser, self.tree, ropeInput(rope));
            };
            if (self.job != null) return;
        }
        if (new_tree) |t| {
            if (self.tree) |old| {
                self.injections.treeChanged(old, t);
                deleteTree(old);
            }
            self.tree = t;
        }
        self.dirty = false;
        self.long_token = false;
        self.injections.refresh(self, rope);
    }

    /// Whether a worker is still parsing this buffer's text, or code
    /// embedded in it, or the UI thread put some of that off to a later frame.
    pub fn parsing(self: *const Syntax) bool {
        if (self.injections.busy()) return true;
        const job = self.job orelse return false;
        return !job.parsed.load(.acquire);
    }

    /// Hands the parse in progress (`parser` keeps its state) to a worker,
    /// with a snapshot of the text it was reading; or a parse that hasn't
    /// started, or must `restart` (its text was cut short).
    fn startJob(self: *Syntax, rope: *const Rope, restart: bool) !void {
        const executor = self.executor.?;
        const job = try self.gpa.create(ParseJob);
        errdefer self.gpa.destroy(job);
        job.* = .{
            .gpa = self.gpa,
            .executor = executor,
            .parser = self.parser,
            .restart = restart,
            .rope = rope.clone(),
            .base = if (self.tree) |t| c.ts_tree_copy(t) else null,
            .want_changed = !self.injections.full,
        };
        errdefer {
            job.rope.deinit(self.gpa);
            if (job.base) |b| c.ts_tree_delete(b);
        }
        try executor.spawn(ParseJob.run, .{job});
        self.job = job;
        self.pending_edits.clearRetainingCapacity();
        self.job_stale = false;
        self.long_token = false;
    }

    /// Takes in the tree a worker parsed, with the edits made since replayed
    /// on it. Without any, it is up to date; otherwise `update` goes on to
    /// reparse from it.
    fn finishJob(self: *Syntax, job: *ParseJob, rope: *const Rope) void {
        self.job = null;
        defer job.destroy();
        const edits = self.pending_edits.items;
        defer self.pending_edits.clearRetainingCapacity();
        const new = job.wait() orelse return;
        if (self.job_stale) return deleteTree(new);
        if (job.changed != null) for (job.changed[0..job.changed_count]) |r| self.noteChanged(r, edits);
        // What the edits fell in, in this tree rather than the old one.
        self.long_token = false;
        for (edits) |*e| {
            c.ts_tree_edit(new, e);
            if (!self.long_token) self.long_token = inLongToken(new, e);
        }
        if (self.tree) |old| deleteTree(old);
        self.tree = new;
        if (edits.len > 0) return;
        self.dirty = false;
        self.injections.refresh(self, rope);
    }

    /// Has the injections look again at `r`, where a worker's parse changed
    /// the tree (as `Layers.treeChanged` does), moved by the `edits` since.
    fn noteChanged(self: *Syntax, r: c.TSRange, edits: []const c.TSInputEdit) void {
        const layers = &self.injections;
        if (layers.full) return;
        var span: [2]u32 = .{ r.start_byte, r.end_byte };
        for (edits) |*e| span = .{ mapByte(span[0], e, e.start_byte), mapByte(span[1], e, e.new_end_byte) };
        const changed = layers.changed orelse span;
        layers.changed = .{ @min(changed[0], span[0]), @max(changed[1], span[1]) };
    }

    /// The node's text, valid until the next call.
    pub fn nodeText(self: *Syntax, rope: *const Rope, node: c.TSNode) []const u8 {
        return readNodeText(self.gpa, &self.text_buf, rope, node);
    }

    /// Whether `match` satisfies its pattern's predicates `preds`.
    pub fn predicatesPass(self: *Syntax, preds: []const Predicate, rope: *const Rope, match: *const c.TSQueryMatch) bool {
        return checkPredicates(self.gpa, preds, rope, match, &self.text_buf, null);
    }

    /// Colored spans intersecting [start, end). Each tree's spans are ordered
    /// by start, inner nodes after the outer nodes that contain them; the
    /// spans of injected code follow those of the code around it. So painting
    /// in order lets the most specific capture win.
    pub fn highlight(self: *Syntax, rope: *const Rope, start: usize, end: usize, out: *std.ArrayList(HighlightSpan)) void {
        out.clearRetainingCapacity();
        const tree = self.tree orelse return;
        self.highlightTree(self.language, tree, &.{}, rope, start, end, out);
        self.injections.highlight(self, rope, start, end, out);
    }

    /// Appends the spans of `tree` (parsed as `lang`) in [start, end), cut to
    /// `ranges` unless that is empty (the whole buffer).
    pub fn highlightTree(self: *Syntax, lang: *const Language, tree: *c.TSTree, ranges: []const c.TSRange, rope: *const Rope, start: usize, end: usize, out: *std.ArrayList(HighlightSpan)) void {
        const query = lang.query orelse return;
        c.ts_query_cursor_exec(self.cursor, query, c.ts_tree_root_node(tree));
        _ = c.ts_query_cursor_set_byte_range(self.cursor, @intCast(start), @intCast(end));
        var match: c.TSQueryMatch = undefined;
        var capture_index: u32 = 0;
        var last_range: [2]usize = .{ std.math.maxInt(usize), 0 };
        // Where the spans of the node in last_range begin.
        var last_spans = out.items.len;
        const last_wins = lang.last_wins;
        while (c.ts_query_cursor_next_capture(self.cursor, &match, &capture_index)) {
            const cap = match.captures[capture_index];
            const color = lang.capture_colors[cap.index] orelse continue;
            const s = c.ts_node_start_byte(cap.node);
            const e = c.ts_node_end_byte(cap.node);
            if (e <= start or s >= end or s == e) continue;
            // Captures of one node arrive in pattern order: keep the first, or
            // let later ones override it, depending on the query's convention.
            const same = s == last_range[0] and e == last_range[1];
            if (same and !last_wins) continue;
            if (!self.predicatesPass(lang.predicates[match.pattern_index], rope, &match)) continue;
            const style = lang.capture_styles[cap.index];
            if (same) {
                for (out.items[last_spans..]) |*span| {
                    span.color = color;
                    span.style = style;
                }
                continue;
            }
            last_range = .{ s, e };
            last_spans = out.items.len;
            if (ranges.len == 0) {
                out.append(self.gpa, .{ .start = s, .end = e, .color = color, .style = style }) catch return;
                continue;
            }
            // A node of an injected tree can span text between its ranges
            // (an emphasis across a quoted line's `> `): color only its ranges.
            var lo: usize = 0;
            var hi: usize = ranges.len;
            while (lo < hi) {
                const mid = (lo + hi) / 2;
                if (ranges[mid].end_byte <= s) lo = mid + 1 else hi = mid;
            }
            for (ranges[lo..]) |r| {
                if (r.start_byte >= e) break;
                const a = @max(s, r.start_byte);
                const b = @min(e, r.end_byte);
                if (b > a) out.append(self.gpa, .{ .start = a, .end = b, .color = color, .style = style }) catch return;
            }
        }
    }

    /// Smallest node strictly larger than [start, end) — expand-selection-to-scope.
    pub fn enclosingNode(self: *Syntax, start: usize, end: usize) ?[2]usize {
        const tree = self.tree orelse return null;
        var node = c.ts_node_descendant_for_byte_range(c.ts_tree_root_node(tree), @intCast(start), @intCast(end));
        while (!c.ts_node_is_null(node)) {
            const s = c.ts_node_start_byte(node);
            const e = c.ts_node_end_byte(node);
            if (s <= start and e >= end and (s != start or e != end)) return .{ s, e };
            node = c.ts_node_parent(node);
        }
        return null;
    }
};

/// A parse that ran over its budget, finishing on a worker. Until `parsed`
/// the worker owns `parser`, `rope`, `base` and `result`; the main thread
/// frees the job once the worker has let go of it (`wait`).
const ParseJob = struct {
    gpa: Allocator,
    executor: *Executor,
    /// The Syntax's parser, part way through (or not yet started).
    parser: *c.TSParser,
    /// What it has parsed so far is of text cut short: drop it.
    restart: bool,
    /// The text it was reading, as it was then.
    rope: Rope,
    /// A copy of the tree the parse started from, to diff the result with.
    base: ?*c.TSTree,
    want_changed: bool,
    result: ?*c.TSTree = null,
    /// Where `result` differs from `base` (allocated by tree-sitter).
    changed: [*c]c.TSRange = null,
    changed_count: u32 = 0,
    /// Set by the main thread when the Syntax goes away.
    cancel: std.atomic.Value(bool) = .init(false),
    parsed: std.atomic.Value(bool) = .init(false),
    exited: std.atomic.Value(bool) = .init(false),

    fn run(job: *ParseJob) void {
        const options: c.TSParseOptions = .{ .payload = job, .progress_callback = cancelled };
        if (job.restart) c.ts_parser_reset(job.parser);
        // Resumes the parse where the budget stopped it, if any.
        job.result = c.ts_parser_parse_with_options(job.parser, job.base, Syntax.ropeInput(&job.rope), options);
        if (job.base) |base| {
            if (job.want_changed) if (job.result) |new| {
                job.changed = c.ts_tree_get_changed_ranges(base, new, &job.changed_count);
            };
            c.ts_tree_delete(base);
            job.base = null;
        }
        job.rope.deinit(job.gpa);
        const executor = job.executor;
        job.parsed.store(true, .release);
        // Wakes the event loop, whose repaint takes the tree in.
        executor.post(.{ .ctx = executor, .run = noop });
        executor.taskDone();
        // Last touch: the main thread may free the job from here on.
        job.exited.store(true, .release);
    }

    fn cancelled(state: [*c]c.TSParseState) callconv(.c) bool {
        const job: *ParseJob = @ptrCast(@alignCast(state.*.payload));
        return job.cancel.load(.monotonic);
    }

    fn noop(_: *anyopaque) void {}

    /// Main thread: waits until the worker has let go; the tree it made.
    fn wait(job: *ParseJob) ?*c.TSTree {
        while (!job.exited.load(.acquire)) std.Thread.yield() catch {};
        return job.result;
    }

    fn destroy(job: *ParseJob) void {
        if (job.changed != null) std.c.free(job.changed);
        job.gpa.destroy(job);
    }
};

/// A deadline for a parse on the main thread (a TSParseOptions progress callback).
pub const Budget = struct {
    deadline: u64,
    expired: bool = false,
    /// The text from `input` ended early: the parser's state is wrong.
    cut: bool = false,
    rope: *const Rope = undefined,

    pub fn check(state: [*c]c.TSParseState) callconv(.c) bool {
        const self: *Budget = @ptrCast(@alignCast(state.*.payload));
        if (nowNs() < self.deadline) return false;
        self.expired = true;
        return true;
    }

    /// `rope`'s text, which ends where the parser is still reading it
    /// `token_overrun_ns` past the deadline: lexing one long token.
    pub fn input(self: *Budget, rope: *const Rope) c.TSInput {
        self.rope = rope;
        return .{ .payload = self, .read = read, .encoding = c.TSInputEncodingUTF8, .decode = null };
    }

    fn read(payload: ?*anyopaque, byte_index: u32, point: c.TSPoint, bytes_read: [*c]u32) callconv(.c) [*c]const u8 {
        const self: *Budget = @ptrCast(@alignCast(payload));
        if (!self.cut and nowNs() >= self.deadline +| token_overrun_ns) {
            self.cut = true;
            self.expired = true;
        }
        if (self.cut) {
            bytes_read.* = 0;
            return "";
        }
        return Syntax.readRope(@constCast(self.rope), byte_index, point, bytes_read);
    }
};

/// Whether `e`, just applied to `tree`, falls in a token of at least
/// `long_token_min` bytes: the one before it, or the one its text ends in.
fn inLongToken(tree: *c.TSTree, e: *const c.TSInputEdit) bool {
    const root = c.ts_tree_root_node(tree);
    for ([_]u32{ e.start_byte -| 1, e.new_end_byte }) |at| {
        const node = c.ts_node_descendant_for_byte_range(root, at, at);
        if (c.ts_node_child_count(node) == 0 and c.ts_node_end_byte(node) - c.ts_node_start_byte(node) >= long_token_min) return true;
    }
    return false;
}

/// Trees of more text than this (bytes) are freed on a thread of their own:
/// a 40 MB file's takes a second, and replacing one after an edit frees
/// thousands of nodes along its root. Freeing runs no grammar code.
const background_free_min = 1 << 20;

fn deleteTree(tree: *c.TSTree) void {
    deleteTreeOf(tree, c.ts_node_end_byte(c.ts_tree_root_node(tree)));
}

/// Frees `tree`, which spans `bytes` of text: on a thread of its own when
/// that is a lot (an injected tree's root starts at the buffer's start).
pub fn deleteTreeOf(tree: *c.TSTree, bytes: usize) void {
    if (bytes >= background_free_min) {
        if (std.Thread.spawn(.{}, freeTree, .{tree})) |thread| return thread.detach() else |_| {}
    }
    c.ts_tree_delete(tree);
}

fn freeTree(tree: *c.TSTree) void {
    c.ts_tree_delete(tree);
}

pub fn nowNs() u64 {
    var ts: std.c.timespec = undefined;
    _ = std.c.clock_gettime(.MONOTONIC, &ts);
    return @as(u64, @intCast(ts.sec)) * std.time.ns_per_s + @as(u64, @intCast(ts.nsec));
}

/// Where `byte` ends up after edit `e`; `inside` if the edit replaced it.
pub fn mapByte(byte: u32, e: *const c.TSInputEdit, inside: u32) u32 {
    if (byte < e.start_byte) return byte;
    if (byte < e.old_end_byte) return inside;
    return byte - e.old_end_byte + e.new_end_byte;
}
