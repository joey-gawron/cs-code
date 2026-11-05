//! Language injections: code in one language embedded in another, such as the
//! <script> and <style> of HTML, Vue, Svelte and Astro, Markdown's inline
//! markup and fenced code blocks, or a template's host language. A grammar's
//! injections.scm (tree-sitter-highlight and nvim-treesitter conventions)
//! marks the nodes holding such code with @injection.content and names the
//! language with @injection.language or `#set! injection.language`.
//!
//! Each match becomes a layer: a tree parsed with the injected grammar over
//! just those byte ranges (ts_parser_set_included_ranges), whose highlight
//! spans paint over the parent's. Layers nest (Markdown, its inline markup,
//! HTML in that, JavaScript in the HTML) and survive edits: an edit shifts
//! every layer's tree and ranges and marks the layers it touches; once the
//! parent has reparsed, its injection query runs again around what changed
//! (a combined injection's layer keeps its ranges elsewhere) and each match
//! takes over the old layer of its language that it overlaps, so reparsing
//! it is incremental. A layer is parsed only when highlighting
//! first reaches it, so a long Markdown file doesn't parse thousands of
//! paragraphs up front.
//!
//! The UI thread does this within budgets, like the buffer's own parse. A
//! frame's layer parsing gets `layer_budget_ns`: the parse that runs past it
//! finishes on a worker (which also looks at what that tree injects), and the
//! layers after it wait for the next frame. An injections query gets
//! `query_budget_ns`: one that runs past it (a big tree's first look, or after
//! a grammar arrived) starts over on a worker, over all of the tree. Either
//! way the worker has snapshots of the text and the tree, and of the
//! languages loaded; the edits made meanwhile are replayed on what it brings
//! back, and the languages it lacked are loaded on the main thread.

const std = @import("std");
const c = @import("c");
const syntax = @import("syntax.zig");
const languages = @import("languages.zig");
const rope_mod = @import("../text/rope.zig");
const Regex = @import("../../framework/util/regex.zig").Regex;
const Executor = @import("../../framework/util/executor.zig").Executor;

const Allocator = std.mem.Allocator;
const Language = syntax.Language;
const Syntax = syntax.Syntax;
const Rope = rope_mod.Rope;
const log = std.log.scoped(.syntax);

/// Injections nest (Markdown, inline markup, HTML, JavaScript) this deep at most.
const max_depth = 4;
/// Past this many edits between two updates (a replace-all), the layers are
/// rebuilt instead of shifted edit by edit.
const max_edits = 64;

// ---- Queries ------------------------------------------------------------------

/// How one pattern of an injections query injects.
pub const Pattern = struct {
    /// `#set! injection.language "name"`; otherwise @injection.language's text.
    language: ?[]const u8 = null,
    /// The table entry `language` names, resolved once when the query loads.
    config: ?*const languages.LanguageConfig = null,
    /// `#set! injection.self` / `#set! injection.parent`: the language of
    /// the tree being queried, or of the layer it is injected into.
    self_language: bool = false,
    parent_language: bool = false,
    /// `#set! injection.combined`: all matches form one layer (per language).
    combined: bool = false,
    /// `#set! injection.include-children`: the content's named child nodes
    /// are part of the injected text; by default they are cut out.
    include_children: bool = false,
    /// `#offset! @injection.content start_row start_col end_row end_col`.
    offset: ?[4]i32 = null,
    /// `#gsub! @injection.language "lua pattern" "replacement"`.
    gsub: ?Gsub = null,
};

const Gsub = struct { regex: *Regex, replacement: []const u8 };

/// A grammar's compiled injections.scm, with those of the grammars it
/// inherits highlights from (`query_inherits`) first.
pub const Query = struct {
    query: *c.TSQuery,
    patterns: []Pattern,
    predicates: [][]syntax.Predicate,
    content_capture: ?u32 = null,
    language_capture: ?u32 = null,
    /// Patterns dropped because they didn't compile against the grammar.
    dropped_patterns: usize = 0,
    /// Some pattern can inject a language we have. Many queries only name
    /// ones we don't ship (Zig's: `comment`); running those over a big tree
    /// takes a while to find nothing.
    can_inject: bool = false,

    pub fn deinit(self: *Query) void {
        c.ts_query_delete(self.query);
        for (self.predicates) |preds| for (preds) |p| if (p.regex) |r| r.deinit();
        for (self.patterns) |p| if (p.gsub) |g| g.regex.deinit();
    }
};

/// Compiles the injections query for `lang` (in its arena); null when its
/// grammar has none. Safe off the main thread, like the rest of loading.
pub fn load(loader: syntax.Loader, lang: *Language, grammar: []const u8) !?*Query {
    const gpa = loader.gpa;
    var source: std.ArrayList(u8) = .empty;
    defer source.deinit(gpa);
    for (lang.config.query_inherits) |parent| try appendQueryFile(gpa, &source, loader.grammar_dir, parent);
    try appendQueryFile(gpa, &source, loader.grammar_dir, grammar);
    if (source.items.len == 0) return null;

    // Like the highlights query: drop patterns naming nodes the grammar lacks.
    var src: []const u8 = source.items;
    var err_offset: u32 = 0;
    var err_type: c.TSQueryError = c.TSQueryErrorNone;
    var query = c.ts_query_new(lang.ts, src.ptr, @intCast(src.len), &err_offset, &err_type);
    var scratch: std.ArrayList(u8) = .empty;
    defer scratch.deinit(gpa);
    var attempts: usize = 0;
    while (query == null and attempts < 64) : (attempts += 1) {
        log.debug("injection query error {d} at {d} in {s}", .{ err_type, err_offset, lang.config.name });
        if (!try syntax.removePatternAt(gpa, &scratch, src, err_offset)) break;
        src = scratch.items;
        query = c.ts_query_new(lang.ts, src.ptr, @intCast(src.len), &err_offset, &err_type);
    }
    const q = query orelse return error.QueryCompileFailed;
    // nvim's files can be just an `; inherits:` line.
    if (c.ts_query_pattern_count(q) == 0) {
        c.ts_query_delete(q);
        return null;
    }
    errdefer c.ts_query_delete(q);

    const alloc = lang.arena.allocator();
    const iq = try alloc.create(Query);
    iq.* = .{ .query = q, .patterns = &.{}, .predicates = &.{}, .dropped_patterns = attempts };
    for (0..c.ts_query_capture_count(q)) |i| {
        var len: u32 = 0;
        const ptr = c.ts_query_capture_name_for_id(q, @intCast(i), &len);
        const name = ptr[0..len];
        // `@content` and `@language` are the names before tree-sitter 0.20.
        if (std.mem.eql(u8, name, "injection.content") or std.mem.eql(u8, name, "content")) iq.content_capture = @intCast(i);
        if (std.mem.eql(u8, name, "injection.language") or std.mem.eql(u8, name, "language")) iq.language_capture = @intCast(i);
    }
    const pattern_count = c.ts_query_pattern_count(q);
    iq.patterns = try alloc.alloc(Pattern, pattern_count);
    iq.predicates = try alloc.alloc([]syntax.Predicate, pattern_count);
    for (iq.patterns, iq.predicates, 0..) |*pattern, *preds, i| {
        preds.* = try loader.parsePredicates(alloc, q, @intCast(i));
        pattern.* = try parseDirectives(gpa, alloc, iq, @intCast(i));
        iq.can_inject = iq.can_inject or pattern.self_language or pattern.parent_language or pattern.config != null or pattern.language == null;
    }
    return iq;
}

fn appendQueryFile(gpa: Allocator, out: *std.ArrayList(u8), dir: []const u8, grammar: []const u8) !void {
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const path = try std.fmt.bufPrintSentinel(&path_buf, "{s}/{s}/queries/injections.scm", .{ dir, grammar }, 0);
    const file = std.c.fopen(path, "rb") orelse return;
    defer _ = std.c.fclose(file);
    var buf: [16384]u8 = undefined;
    while (true) {
        const n = std.c.fread(&buf, 1, buf.len, file);
        if (n == 0) break;
        try out.appendSlice(gpa, buf[0..n]);
    }
    try out.append(gpa, '\n');
}

/// The `#set!`, `#offset!` and `#gsub!` directives of one pattern. The
/// filters (#eq?, #lua-match?, ...) are `Query.predicates`.
fn parseDirectives(gpa: Allocator, alloc: Allocator, iq: *const Query, pattern: u32) !Pattern {
    const q = iq.query;
    var out: Pattern = .{};
    var step_count: u32 = 0;
    const steps = c.ts_query_predicates_for_pattern(q, pattern, &step_count);
    var i: usize = 0;
    while (i < step_count) {
        var end = i;
        while (end < step_count and steps[end].type != c.TSQueryPredicateStepTypeDone) end += 1;
        defer i = end + 1;
        if (end == i or steps[i].type != c.TSQueryPredicateStepTypeString) continue;
        const name = syntax.stringValue(q, steps[i].value_id);
        const args = steps[i + 1 .. end];
        const eql = std.mem.eql;
        if (eql(u8, name, "set!")) {
            // `#set! @capture key value` sets a capture's property: not ours.
            if (args.len == 0 or args[0].type != c.TSQueryPredicateStepTypeString) continue;
            const key = syntax.stringValue(q, args[0].value_id);
            const value = if (args.len > 1 and args[1].type == c.TSQueryPredicateStepTypeString) syntax.stringValue(q, args[1].value_id) else null;
            if (eql(u8, key, "injection.language")) {
                out.language = value;
                if (value) |v| out.config = languages.forInjection(v);
            } else if (eql(u8, key, "injection.combined")) {
                out.combined = true;
            } else if (eql(u8, key, "injection.include-children")) {
                out.include_children = true;
            } else if (eql(u8, key, "injection.self")) {
                out.self_language = true;
            } else if (eql(u8, key, "injection.parent")) {
                out.parent_language = true;
            }
        } else if (eql(u8, name, "offset!")) {
            if (args.len != 5 or args[0].type != c.TSQueryPredicateStepTypeCapture) continue;
            if (iq.content_capture == null or args[0].value_id != iq.content_capture.?) continue;
            var offset: [4]i32 = undefined;
            var ok = true;
            for (args[1..], &offset) |a, *o| {
                o.* = std.fmt.parseInt(i32, syntax.stringValue(q, a.value_id), 10) catch blk: {
                    ok = false;
                    break :blk 0;
                };
            }
            if (ok) out.offset = offset;
        } else if (eql(u8, name, "gsub!")) {
            if (args.len != 3 or args[0].type != c.TSQueryPredicateStepTypeCapture) continue;
            if (iq.language_capture == null or args[0].value_id != iq.language_capture.?) continue;
            const pattern_src = syntax.stringValue(q, args[1].value_id);
            const re = try alloc.create(Regex);
            re.* = Regex.compile(gpa, try syntax.luaToRegex(alloc, pattern_src), .{}) catch {
                log.debug("unsupported #gsub! pattern: {s}", .{pattern_src});
                continue;
            };
            out.gsub = .{ .regex = re, .replacement = syntax.stringValue(q, args[2].value_id) };
        }
    }
    return out;
}

/// Lua's string.gsub with `%0`..`%9` in the replacement. Null if the result
/// doesn't fit `out`.
fn gsub(g: Gsub, text: []const u8, out: []u8, scratch: *Regex.Scratch) ?[]const u8 {
    var len: usize = 0;
    var pos: usize = 0;
    const Append = struct {
        fn f(buf: []u8, n: *usize, s: []const u8) bool {
            if (n.* + s.len > buf.len) return false;
            @memcpy(buf[n.*..][0..s.len], s);
            n.* += s.len;
            return true;
        }
    };
    while (pos <= text.len) {
        const m = g.regex.searchIn(scratch, text, pos) orelse break;
        if (!Append.f(out, &len, text[pos..m.start])) return null;
        var i: usize = 0;
        while (i < g.replacement.len) : (i += 1) {
            const ch = g.replacement[i];
            if (ch == '%' and i + 1 < g.replacement.len) {
                i += 1;
                const d = g.replacement[i];
                if (d >= '0' and d <= '9') {
                    const group: ?[2]usize = if (d == '0') .{ m.start, m.end } else m.groups[d - '1'];
                    if (group) |gr| if (!Append.f(out, &len, text[gr[0]..gr[1]])) return null;
                    continue;
                }
            }
            if (!Append.f(out, &len, g.replacement[i .. i + 1])) return null;
        }
        if (m.end > m.start) {
            pos = m.end;
        } else {
            if (m.end < text.len and !Append.f(out, &len, text[m.end .. m.end + 1])) return null;
            pos = m.end + 1;
        }
    }
    if (pos < text.len and !Append.f(out, &len, text[pos..])) return null;
    return out[0..len];
}

test "gsub" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    var re = try Regex.compile(std.testing.allocator, try syntax.luaToRegex(arena.allocator(), "(.+)/(.+)"), .{});
    defer re.deinit();
    var buf: [64]u8 = undefined;
    var scratch: Regex.Scratch = .{};
    defer scratch.deinit(std.testing.allocator);
    const g: Gsub = .{ .regex = &re, .replacement = "%2" };
    try std.testing.expectEqualStrings("javascript", gsub(g, "text/javascript", &buf, &scratch).?);
    try std.testing.expectEqualStrings("module", gsub(g, "module", &buf, &scratch).?);
}

// ---- Layers -------------------------------------------------------------------

/// How long injected layers may parse on the UI thread in one frame (ns),
/// when there is an executor to hand the rest to. The parse that runs past
/// it finishes on a worker; the layers after it wait for the next frame.
pub var layer_budget_ns: u64 = 8 * std.time.ns_per_ms;
/// How long one injections query may run on the UI thread (ns). One that
/// runs past it starts over on a worker, over the whole tree.
pub var query_budget_ns: u64 = 4 * std.time.ns_per_ms;

/// One injected tree.
pub const Layer = struct {
    language: *Language,
    tree: ?*c.TSTree = null,
    /// What the tree covers: sorted, disjoint and never empty.
    ranges: []c.TSRange,
    /// A combined injection's: its ranges come from all over its parent.
    combined: bool = false,
    /// Needs (re)parsing: new, its ranges changed, or an edit touched it.
    dirty: bool = true,
    /// What is injected into it needs a look over its whole tree (it is
    /// new, or a grammar arrived), or else around `changed`.
    full: bool = true,
    changed: ?[2]u32 = null,
    /// Layers injected into this one, by start.
    children: std.ArrayList(*Layer) = .empty,
    /// A worker finishing its parse, or looking at all of its tree.
    job: ?*Job = null,

    fn start(self: *const Layer) u32 {
        return self.ranges[0].start_byte;
    }

    fn end(self: *const Layer) u32 {
        return self.ranges[self.ranges.len - 1].end_byte;
    }

    /// Frees one of its trees (a big one on a thread of its own).
    fn deleteTree(self: *const Layer, tree: *c.TSTree) void {
        syntax.deleteTreeOf(tree, self.end() - self.start());
    }

    fn destroy(self: *Layer, gpa: Allocator) void {
        if (self.job) |job| job.abandon();
        if (self.tree) |t| self.deleteTree(t);
        gpa.free(self.ranges);
        clearList(gpa, &self.children);
        self.children.deinit(gpa);
        gpa.destroy(self);
    }
};

fn clearList(gpa: Allocator, list: *std.ArrayList(*Layer)) void {
    for (list.items) |layer| layer.destroy(gpa);
    list.clearRetainingCapacity();
}

/// A grammar arrived: what is injected into every layer gets a whole look.
fn markFull(list: []const *Layer) void {
    for (list) |layer| {
        layer.full = true;
        markFull(layer.children.items);
    }
}

/// Where `changed` (in the text before edit `e`) is after it; grown by
/// what the edit replaced when that was part of the tree's text (`touched`).
fn moveChanged(changed: ?[2]u32, e: *const c.TSInputEdit, touched: bool) ?[2]u32 {
    var out: ?[2]u32 = if (touched) .{ e.start_byte, e.new_end_byte } else null;
    if (changed) |old| {
        const moved: [2]u32 = .{ syntax.mapByte(old[0], e, e.start_byte), syntax.mapByte(old[1], e, e.new_end_byte) };
        out = if (out) |o| .{ @min(o[0], moved[0]), @max(o[1], moved[1]) } else moved;
    }
    return out;
}

/// Grows `changed` by where `new` differs from `old`, which it was reparsed from.
fn noteTreeChanged(changed: *?[2]u32, old: *const c.TSTree, new: *const c.TSTree) void {
    var count: u32 = 0;
    const ranges = c.ts_tree_get_changed_ranges(old, new, &count) orelse return;
    defer std.c.free(ranges);
    for (ranges[0..count]) |r| {
        const ch = changed.* orelse [2]u32{ r.start_byte, r.end_byte };
        changed.* = .{ @min(ch[0], r.start_byte), @max(ch[1], r.end_byte) };
    }
}

/// `lang`'s injections query, if it can inject anything at `depth`.
fn injectable(lang: *const Language, depth: u32) ?*const Query {
    const iq = lang.injections orelse return null;
    if (!iq.can_inject or depth >= max_depth) return null;
    return iq;
}

/// The injected layers of one buffer's Syntax.
pub const Layers = struct {
    /// Layers injected into the buffer's own tree, by start.
    children: std.ArrayList(*Layer) = .empty,
    /// Parses the layers: its language and ranges are set per layer. A
    /// layer whose parse moves to a worker takes it along.
    parser: ?*c.TSParser = null,
    /// Edits since the layers were last brought up to date.
    edits: u32 = 0,
    /// Where the text changed since then (in current offsets), from the
    /// edits and from where the reparsed tree differs: only there can what
    /// the buffer's own tree injects have changed.
    changed: ?[2]u32 = null,
    /// The next refresh looks at the whole buffer, not just `changed`.
    full: bool = true,
    /// Runs injection queries on the main thread.
    finder: Finder = .{},
    /// A worker looking at all of the buffer's tree.
    job: ?*Job = null,
    /// Layers parse on the UI thread until `deadline`, set as the frame's
    /// first parse starts.
    budgeted: bool = false,
    deadline: ?u64 = null,
    /// A parse ran past `deadline` this frame: the layers after it wait.
    overrun: bool = false,
    /// Layers were left for the next frame, which is on its way.
    deferred: bool = false,
    // Scratch for `reconcile`, kept so typing doesn't allocate.
    made: std.ArrayList(Made) = .empty,
    taken: std.ArrayList(bool) = .empty,
    next: std.ArrayList(*Layer) = .empty,

    const Made = struct { layer: *Layer, new: bool };

    pub fn deinit(self: *Layers, gpa: Allocator) void {
        if (self.job) |job| job.abandon();
        clearList(gpa, &self.children);
        self.children.deinit(gpa);
        if (self.parser) |p| c.ts_parser_delete(p);
        self.finder.deinit(gpa);
        self.made.deinit(gpa);
        self.taken.deinit(gpa);
        self.next.deinit(gpa);
    }

    /// A frame starts: layers may parse on the UI thread for
    /// `layer_budget_ns` when `budgeted` (there is a worker to hand over to).
    pub fn beginFrame(self: *Layers, budgeted: bool) void {
        self.budgeted = budgeted;
        self.deadline = null;
        self.overrun = false;
        self.deferred = false;
    }

    /// Whether workers are still busy with the layers, or what one found in
    /// the buffer's tree is yet to be taken in, or layers were left for the
    /// next frame.
    pub fn busy(self: *const Layers) bool {
        if (self.deferred or self.job != null) return true;
        return busyList(self.children.items);
    }

    fn busyList(list: []const *Layer) bool {
        for (list) |layer| {
            if (layer.job) |job| if (!job.done.load(.acquire)) return true;
            if (busyList(layer.children.items)) return true;
        }
        return false;
    }

    /// Shifts the layers for an edit (already applied to the buffer's tree).
    pub fn edit(self: *Layers, gpa: Allocator, e: *const c.TSInputEdit) void {
        self.changed = moveChanged(self.changed, e, true);
        if (self.job) |job| job.note(e);
        if (self.children.items.len == 0) return;
        self.edits += 1;
        // The buffer's tree reparses before any layer is used, and rebuilds
        // these then.
        if (self.edits > max_edits) {
            clearList(gpa, &self.children);
            self.full = true;
            return;
        }
        editList(self.children.items, e);
    }

    fn editList(list: []const *Layer, input: *const c.TSInputEdit) void {
        for (list) |layer| {
            // Ranges (and so children) before the edit stay put.
            if (layer.end() < input.start_byte) continue;
            if (layer.job) |job| job.note(input);
            if (layer.tree) |t| c.ts_tree_edit(t, input);
            var touched = false;
            for (layer.ranges) |*r| {
                if (r.start_byte <= input.old_end_byte and input.start_byte <= r.end_byte) touched = true;
                shift(&r.start_byte, &r.start_point, input);
                shift(&r.end_byte, &r.end_point, input);
            }
            if (touched) layer.dirty = true;
            layer.changed = moveChanged(layer.changed, input, touched);
            editList(layer.children.items, input);
        }
    }

    fn shift(byte: *u32, point: *c.TSPoint, e: *const c.TSInputEdit) void {
        if (byte.* < e.start_byte) return;
        if (byte.* < e.old_end_byte) {
            // Inside the replaced text: the layer is dirty and gets new ranges.
            byte.* = e.new_end_byte;
            point.* = e.new_end_point;
            return;
        }
        byte.* = byte.* - e.old_end_byte + e.new_end_byte;
        if (point.row == e.old_end_point.row) point.column = point.column - e.old_end_point.column + e.new_end_point.column;
        point.row = point.row - e.old_end_point.row + e.new_end_point.row;
    }

    /// The buffer's tree reparsed from `old` to `new`: what they differ in
    /// may inject differently.
    pub fn treeChanged(self: *Layers, old: *const c.TSTree, new: *const c.TSTree) void {
        if (self.full) return;
        noteTreeChanged(&self.changed, old, new);
    }

    /// After the buffer's tree (re)parsed: finds what is injected into it,
    /// where it changed.
    pub fn refresh(self: *Layers, syn: *Syntax, rope: *const Rope) void {
        // A worker is looking at all of the tree; what changes meanwhile is
        // looked at once it is done.
        if (self.job != null) return;
        defer {
            self.edits = 0;
            self.changed = null;
            self.full = false;
        }
        const tree = syn.tree orelse return;
        const region = if (self.full) null else self.changed orelse return;
        self.inject(syn, rope, syn.language, null, tree, &.{}, &self.children, 0, region, null);
    }

    /// Takes in what a worker found injected all over the buffer's tree,
    /// if it is done. Returns whether it was.
    pub fn poll(self: *Layers, syn: *Syntax) bool {
        const job = self.job orelse return false;
        if (!job.done.load(.acquire)) return false;
        self.job = null;
        defer job.destroy();
        job.wait();
        if (job.stale or !job.looked or self.adopt(syn.gpa, job, syn.language, &self.children)) self.full = true;
        return true;
    }

    /// An injected grammar finished loading: re-injects if some layer was
    /// waiting for one. Returns whether the buffer needs a repaint.
    pub fn languageLoaded(self: *Layers) bool {
        if (!self.finder.waiting) return false;
        self.finder.waiting = false;
        self.full = true;
        markFull(self.children.items);
        return true;
    }

    /// Appends the spans of the layers intersecting [start, end), each after
    /// the spans of the layer it is injected into, so they paint over them.
    pub fn highlight(self: *Layers, syn: *Syntax, rope: *const Rope, start: usize, end: usize, out: *std.ArrayList(syntax.HighlightSpan)) void {
        self.highlightList(syn, rope, syn.language, self.children.items, start, end, out, 0);
    }

    fn highlightList(self: *Layers, syn: *Syntax, rope: *const Rope, parent: *Language, list: []const *Layer, start: usize, end: usize, out: *std.ArrayList(syntax.HighlightSpan), depth: u32) void {
        for (list) |layer| {
            if (layer.start() >= end) break;
            if (layer.end() <= start) continue;
            self.ensureParsed(syn, rope, layer, parent, depth);
            const tree = layer.tree orelse continue;
            syn.highlightTree(layer.language, tree, layer.ranges, rope, start, end, out);
            self.highlightList(syn, rope, layer.language, layer.children.items, start, end, out, depth + 1);
        }
    }

    /// Brings a layer about to be highlighted up to date, as far as the
    /// frame allows: until then its old tree, edited along with the text,
    /// highlights it (a new layer shows nothing).
    fn ensureParsed(self: *Layers, syn: *Syntax, rope: *const Rope, layer: *Layer, parent: *Language, depth: u32) void {
        if (layer.job) |job| {
            if (job.done.load(.acquire)) {
                self.finishJob(syn, layer);
            } else if (job.parser != null) return; // The worker has its parser.
        }
        if (layer.dirty and !self.parse(syn, rope, layer, parent, depth)) return;
        self.refreshLayer(syn, rope, layer, parent, depth);
    }

    /// Parses a dirty layer within the frame's budget: past it, the parse
    /// finishes on a worker, and layers after it wait for the next frame.
    /// Returns whether the layer's tree is up to date.
    fn parse(self: *Layers, syn: *Syntax, rope: *const Rope, layer: *Layer, parent: *Language, depth: u32) bool {
        if (self.overrun) {
            self.putOff(syn);
            return false;
        }
        const parser = self.parser orelse (c.ts_parser_new() orelse return false);
        self.parser = parser;
        layer.dirty = false;
        if (!c.ts_parser_set_language(parser, layer.language.ts)) return false;
        if (!c.ts_parser_set_included_ranges(parser, layer.ranges.ptr, @intCast(layer.ranges.len))) {
            log.debug("bad injection ranges for {s}", .{layer.language.config.name});
            return false;
        }
        if (self.budgeted and self.deadline == null) self.deadline = syntax.nowNs() +| layer_budget_ns;
        var clock: syntax.Budget = .{ .deadline = self.deadline orelse std.math.maxInt(u64) };
        const options: c.TSParseOptions = .{ .payload = &clock, .progress_callback = if (self.budgeted) &syntax.Budget.check else null };
        var new_tree = c.ts_parser_parse_with_options(parser, layer.tree, Syntax.ropeInput(rope), options);
        if (new_tree == null and clock.expired) {
            self.overrun = true;
            if (self.startParse(syn, rope, layer, parent, depth)) return false;
            // Picks up where the budget stopped it.
            new_tree = c.ts_parser_parse(parser, layer.tree, Syntax.ropeInput(rope));
        }
        const new = new_tree orelse return false;
        if (layer.tree) |old| {
            if (!layer.full) noteTreeChanged(&layer.changed, old, new);
            layer.deleteTree(old);
        }
        layer.tree = new;
        return true;
    }

    /// Leaves a layer for the next frame, and asks for one.
    fn putOff(self: *Layers, syn: *Syntax) void {
        if (self.deferred) return;
        self.deferred = true;
        const executor = syn.executor orelse return;
        executor.post(.{ .ctx = executor, .run = noop });
    }

    /// Hands the parse in progress (`parser` keeps its state) to a worker,
    /// which then looks at what the tree injects. False if it can't.
    fn startParse(self: *Layers, syn: *Syntax, rope: *const Rope, layer: *Layer, parent: *Language, depth: u32) bool {
        const job = Job.start(syn, rope, .{
            .parser = self.parser,
            .tree = layer.tree,
            .lang = layer.language,
            .parent = parent,
            .ranges = layer.ranges,
            .iq = injectable(layer.language, depth + 1),
            .whole = layer.full,
            .region = layer.changed,
        }) orelse return false;
        self.parser = null;
        layer.job = job;
        layer.full = false;
        layer.changed = null;
        return true;
    }

    /// Takes in what a layer's worker did: its tree, with the edits made
    /// since replayed on it, and what is injected into it.
    fn finishJob(self: *Layers, syn: *Syntax, layer: *Layer) void {
        const job = layer.job.?;
        layer.job = null;
        defer job.destroy();
        job.wait();
        if (job.stale) {
            // Too much changed meanwhile: start over from the old tree,
            // which was edited all along.
            if (job.parser != null) layer.dirty = true;
            layer.full = true;
            return;
        }
        if (job.parser) |parser| {
            const new = job.tree orelse {
                layer.dirty = true;
                layer.full = true;
                return;
            };
            job.tree = null;
            for (job.edits.items) |*e| c.ts_tree_edit(new, e);
            if (layer.tree) |old| layer.deleteTree(old);
            layer.tree = new;
            // Done with: the next layer's.
            if (self.parser == null) {
                self.parser = parser;
                job.parser = null;
            }
        }
        if (job.looked) {
            if (self.adopt(syn.gpa, job, layer.language, &layer.children)) layer.full = true;
        } else if (job.whole or job.region != null) layer.full = true;
    }

    /// Brings what is injected into `layer` up to date with its tree.
    fn refreshLayer(self: *Layers, syn: *Syntax, rope: *const Rope, layer: *Layer, parent: *Language, depth: u32) void {
        // A worker is looking at all of it; what changes meanwhile is
        // looked at once it is done.
        if (layer.job != null) return;
        if (!layer.full and layer.changed == null) return;
        const tree = layer.tree orelse return;
        const region = if (layer.full) null else layer.changed;
        layer.full = false;
        layer.changed = null;
        self.inject(syn, rope, layer.language, parent, tree, layer.ranges, &layer.children, depth + 1, region, layer);
    }

    /// Recomputes the layers injected into `tree` (of `lang`, covering
    /// `ranges`, itself injected into `parent`), reusing `children`. With a
    /// `region`, only injections near it can have changed. A query too slow
    /// for the UI thread moves to a worker, as `owner`'s job (that of the
    /// buffer's tree when null).
    fn inject(self: *Layers, syn: *Syntax, rope: *const Rope, lang: *Language, parent: ?*Language, tree: *c.TSTree, ranges: []const c.TSRange, children: *std.ArrayList(*Layer), depth: u32, region: ?[2]u32, owner: ?*Layer) void {
        const gpa = syn.gpa;
        const iq = injectable(lang, depth) orelse return clearList(gpa, children);
        self.finder.deadline = if (syn.executor != null) syntax.nowNs() +| query_budget_ns else std.math.maxInt(u64);
        const window = self.finder.find(gpa, rope, iq, lang, parent, tree, ranges, region) catch |err| {
            // Out of memory: keep the old layers.
            if (err != error.OverBudget) return;
            // Too slow for a frame: a worker looks at all of the tree.
            if (Job.start(syn, rope, .{ .tree = tree, .lang = lang, .parent = parent, .ranges = ranges, .iq = iq, .whole = true })) |job| {
                if (owner) |layer| layer.job = job else self.job = job;
                return;
            }
            self.finder.deadline = std.math.maxInt(u64);
            const whole = self.finder.find(gpa, rope, iq, lang, parent, tree, ranges, null) catch return;
            self.reconcile(gpa, &self.finder, children, whole) catch {};
            return;
        };
        self.reconcile(gpa, &self.finder, children, window) catch {};
    }

    /// Takes in what `job` found, in the text as it was when it started:
    /// moved by the edits made since, it replaces `children` where it
    /// looked. Returns whether to look again: a language it lacked has
    /// loaded since.
    fn adopt(self: *Layers, gpa: Allocator, job: *Job, lang: *Language, children: *std.ArrayList(*Layer)) bool {
        var again = false;
        for (job.finder.missing.items) |config| {
            if (self.finder.resolve(gpa, lang, config) != null) again = true;
        }
        var window = job.window;
        job.finder.shift(job.edits.items, &window);
        self.reconcile(gpa, &job.finder, children, window) catch {};
        return again;
    }

    /// Turns what `finder` found into `children`. Each takes over the first
    /// old layer of its language it overlaps, which then reparses
    /// incrementally (or not at all, when nothing about it changed). Old
    /// layers outside `window` stay as they are, and so do the ranges of
    /// combined ones outside it; those in it that nothing took over are freed.
    fn reconcile(self: *Layers, gpa: Allocator, finder: *Finder, children: *std.ArrayList(*Layer), window: ?[2]u32) !void {
        const old = children.items;
        if (window) |w| {
            for (old) |layer| if (layer.combined) try finder.keepCombined(gpa, layer, w);
            finder.sortFound();
        }
        const Affected = struct {
            w: ?[2]u32,
            fn in(a: @This(), layer: *const Layer) bool {
                const w = a.w orelse return true;
                return layer.combined or (layer.start() <= w[1] and layer.end() >= w[0]);
            }
        };
        const affected: Affected = .{ .w = window };
        try self.taken.resize(gpa, old.len);
        @memset(self.taken.items, false);
        const taken = self.taken.items;
        self.made.clearRetainingCapacity();
        errdefer for (self.made.items) |m| if (m.new) m.layer.destroy(gpa);

        var first: usize = 0;
        for (finder.found.items) |f| {
            const ranges = finder.found_ranges.items[f.first..][0..f.len];
            const start = ranges[0].start_byte;
            const end = ranges[ranges.len - 1].end_byte;
            while (first < old.len and (taken[first] or !affected.in(old[first]) or old[first].end() <= start)) first += 1;
            var reuse: ?usize = null;
            var k = first;
            while (k < old.len and old[k].start() < end) : (k += 1) {
                const o = old[k];
                if (taken[k] or !affected.in(o) or o.language != f.language or o.combined != f.combined or o.end() <= start) continue;
                reuse = k;
                break;
            }
            try self.made.ensureUnusedCapacity(gpa, 1);
            if (reuse) |i| {
                const layer = old[i];
                if (!rangesEql(layer.ranges, ranges)) {
                    layer.ranges = try gpa.realloc(layer.ranges, ranges.len);
                    @memcpy(layer.ranges, ranges);
                    layer.dirty = true;
                }
                taken[i] = true;
                self.made.appendAssumeCapacity(.{ .layer = layer, .new = false });
            } else {
                const layer = try gpa.create(Layer);
                errdefer gpa.destroy(layer);
                layer.* = .{ .language = f.language, .combined = f.combined, .ranges = try gpa.dupe(c.TSRange, ranges) };
                self.made.appendAssumeCapacity(.{ .layer = layer, .new = true });
            }
        }

        // The old layers left outside the window and the found ones, by start.
        self.next.clearRetainingCapacity();
        try self.next.ensureTotalCapacity(gpa, old.len + self.made.items.len);
        var i: usize = 0;
        var j: usize = 0;
        while (true) {
            while (i < old.len and affected.in(old[i])) i += 1;
            if (i == old.len) {
                for (self.made.items[j..]) |m| self.next.appendAssumeCapacity(m.layer);
                break;
            }
            if (j < self.made.items.len and self.made.items[j].layer.start() < old[i].start()) {
                self.next.appendAssumeCapacity(self.made.items[j].layer);
                j += 1;
            } else {
                self.next.appendAssumeCapacity(old[i]);
                i += 1;
            }
        }
        for (old, taken) |layer, t| if (!t and affected.in(layer)) layer.destroy(gpa);
        std.mem.swap(std.ArrayList(*Layer), children, &self.next);
        self.next.clearRetainingCapacity();
    }
};

/// Runs injections queries: what a thread looking for injections needs.
const Finder = struct {
    /// What the last look found, by start.
    found: std.ArrayList(Found) = .empty,
    found_ranges: std.ArrayList(c.TSRange) = .empty,
    pieces: std.ArrayList(Piece) = .empty,
    cursor: ?*c.TSQueryCursor = null,
    options: c.TSQueryCursorOptions = undefined,
    /// Node text, for predicates and language names.
    text: std.ArrayList(u8) = .empty,
    regex: Regex.Scratch = .{},
    /// On a worker: the languages loaded when it started; the configs of
    /// others it finds go to `missing`. On the main thread (null) the
    /// registry is asked, which starts loading them.
    loaded: ?std.StringHashMapUnmanaged(?*Language) = null,
    missing: std.ArrayList(*const languages.LanguageConfig) = .empty,
    /// Main thread: an injected grammar was still loading in the background.
    waiting: bool = false,
    /// A query stops once past `deadline`, or once `cancel` is set (a
    /// worker's); `halted` says it did.
    deadline: u64 = std.math.maxInt(u64),
    cancel: ?*const std.atomic.Value(bool) = null,
    halted: bool = false,

    /// One injection found in a tree, before it becomes a Layer. Its ranges
    /// are found_ranges[first..][0..len].
    const Found = struct {
        language: *Language,
        first: u32 = 0,
        len: u32 = 0,
        combined: bool,
    };

    /// A range of a combined injection, found_ranges[group]'s.
    const Piece = struct { group: u32, range: c.TSRange };

    fn deinit(self: *Finder, gpa: Allocator) void {
        self.found.deinit(gpa);
        self.found_ranges.deinit(gpa);
        self.pieces.deinit(gpa);
        if (self.cursor) |cur| c.ts_query_cursor_delete(cur);
        self.text.deinit(gpa);
        self.regex.deinit(gpa);
        if (self.loaded) |*m| m.deinit(gpa);
        self.missing.deinit(gpa);
    }

    /// Finds what `tree` (of `lang`, covering `ranges`, injected into
    /// `parent`) injects around `region`, or all over it when that is null.
    /// Returns the window that covers (null: the whole tree). A match near
    /// the region can reach beyond it (a predicate on a start tag, an
    /// injection spanning lines): then the look widens to all it covers,
    /// which takes in the old layers it overlaps; failing that, to the
    /// whole tree.
    fn find(self: *Finder, gpa: Allocator, rope: *const Rope, iq: *const Query, lang: *Language, parent: ?*Language, tree: *c.TSTree, ranges: []const c.TSRange, region: ?[2]u32) !?[2]u32 {
        if (region) |r| {
            var q: [2]u32 = .{ r[0] -| 1, r[1] +| 1 };
            for (0..2) |_| {
                try self.collect(gpa, rope, iq, lang, parent, tree, ranges, q);
                var w = r;
                for (self.found.items) |f| {
                    const fr = self.found_ranges.items[f.first..][0..f.len];
                    w = .{ @min(w[0], fr[0].start_byte), @max(w[1], fr[fr.len - 1].end_byte) };
                }
                if (w[0] >= q[0] and w[1] <= q[1]) return w;
                q = .{ w[0] -| 1, w[1] +| 1 };
            }
        }
        try self.collect(gpa, rope, iq, lang, parent, tree, ranges, null);
        return null;
    }

    /// Runs `iq` over `tree` (only around `range` when given) into `found`.
    fn collect(self: *Finder, gpa: Allocator, rope: *const Rope, iq: *const Query, lang: *Language, parent: ?*Language, tree: *c.TSTree, ranges: []const c.TSRange, range: ?[2]u32) !void {
        self.found.clearRetainingCapacity();
        self.found_ranges.clearRetainingCapacity();
        self.pieces.clearRetainingCapacity();
        const content_capture = iq.content_capture orelse return;
        const cursor = self.cursor orelse (c.ts_query_cursor_new() orelse return error.OutOfMemory);
        self.cursor = cursor;
        self.halted = false;
        const stoppable = self.cancel != null or self.deadline != std.math.maxInt(u64);
        self.options = .{ .payload = self, .progress_callback = if (stoppable) &progress else null };
        c.ts_query_cursor_exec_with_options(cursor, iq.query, c.ts_tree_root_node(tree), &self.options);
        const r = range orelse .{ 0, std.math.maxInt(u32) };
        _ = c.ts_query_cursor_set_byte_range(cursor, r[0], r[1]);
        var match: c.TSQueryMatch = undefined;
        while (c.ts_query_cursor_next_match(cursor, &match)) {
            const pattern = &iq.patterns[match.pattern_index];
            const captures = match.captures[0..match.capture_count];
            if (!syntax.checkPredicates(gpa, iq.predicates[match.pattern_index], rope, &match, &self.text, &self.regex)) continue;
            const target: *Language = if (pattern.self_language)
                lang
            else if (pattern.parent_language)
                parent orelse continue
            else if (pattern.config) |config|
                self.resolve(gpa, lang, config) orelse continue
            else if (pattern.language != null)
                continue // A language we don't have (`comment`, `regex`, ...).
            else blk: {
                const lang_capture = iq.language_capture orelse continue;
                var name_buf: [64]u8 = undefined;
                var name: []const u8 = "";
                for (captures) |cap| if (cap.index == lang_capture) {
                    const text = syntax.readNodeText(gpa, &self.text, rope, cap.node);
                    if (text.len > name_buf.len) break;
                    @memcpy(name_buf[0..text.len], text);
                    name = name_buf[0..text.len];
                    break;
                };
                var gsub_buf: [64]u8 = undefined;
                if (pattern.gsub) |g| name = gsub(g, name, &gsub_buf, &self.regex) orelse continue;
                const config = languages.forInjection(name) orelse continue;
                break :blk self.resolve(gpa, lang, config) orelse continue;
            };

            if (pattern.combined) {
                // Combined layers are few: find this one's.
                const group: u32 = for (self.found.items, 0..) |f, i| {
                    if (f.combined and f.language == target) break @intCast(i);
                } else new: {
                    try self.found.append(gpa, .{ .language = target, .combined = true });
                    break :new @intCast(self.found.items.len - 1);
                };
                const before = self.found_ranges.items.len;
                for (captures) |cap| if (cap.index == content_capture) {
                    try appendContent(gpa, &self.found_ranges, cap.node, pattern, rope, ranges);
                };
                for (self.found_ranges.items[before..]) |piece| try self.pieces.append(gpa, .{ .group = group, .range = piece });
                self.found_ranges.items.len = before;
            } else {
                const first: u32 = @intCast(self.found_ranges.items.len);
                for (captures) |cap| if (cap.index == content_capture) {
                    try appendContent(gpa, &self.found_ranges, cap.node, pattern, rope, ranges);
                };
                const len: u32 = @intCast(self.found_ranges.items.len - first);
                if (len > 0) try self.found.append(gpa, .{ .language = target, .first = first, .len = len, .combined = false });
            }
        }
        if (self.halted) return if (self.cancel != null) error.Cancelled else error.OverBudget;

        // Each combined layer's ranges, from all over the tree, go together.
        std.mem.sort(Piece, self.pieces.items, {}, struct {
            fn lessThan(_: void, a: Piece, b: Piece) bool {
                return a.group < b.group or (a.group == b.group and a.range.start_byte < b.range.start_byte);
            }
        }.lessThan);
        for (self.pieces.items) |piece| {
            const f = &self.found.items[piece.group];
            if (f.len == 0) f.first = @intCast(self.found_ranges.items.len);
            try self.found_ranges.append(gpa, piece.range);
            f.len += 1;
        }

        // Included ranges must be sorted and disjoint; layers go by start.
        var kept: usize = 0;
        for (self.found.items) |f| {
            var len: usize = f.len;
            normalize(self.found_ranges.items[f.first..][0..f.len], &len);
            if (len == 0) continue;
            self.found.items[kept] = f;
            self.found.items[kept].len = @intCast(len);
            kept += 1;
        }
        self.found.items.len = kept;
        self.sortFound();
    }

    fn progress(state: [*c]c.TSQueryCursorState) callconv(.c) bool {
        const self: *Finder = @ptrCast(@alignCast(state.*.payload));
        const stop = if (self.cancel) |flag| flag.load(.monotonic) else syntax.nowNs() >= self.deadline;
        if (stop) self.halted = true;
        return stop;
    }

    fn sortFound(self: *Finder) void {
        std.mem.sort(Found, self.found.items, self.found_ranges.items, struct {
            fn lessThan(all: []const c.TSRange, a: Found, b: Found) bool {
                return all[a.first].start_byte < all[b.first].start_byte;
            }
        }.lessThan);
    }

    /// The language `config` names, if it has loaded.
    fn resolve(self: *Finder, gpa: Allocator, lang: *Language, config: *const languages.LanguageConfig) ?*Language {
        const grammar = config.grammar orelse return null;
        if (self.loaded) |loaded| {
            // Null: it failed to load.
            if (loaded.get(grammar)) |l| return l;
            for (self.missing.items) |m| if (m == config) return null;
            self.missing.append(gpa, config) catch {};
            return null;
        }
        const registry = lang.registry orelse return null;
        if (registry.request(config)) |l| return l;
        // Loading in the background (unless it failed): look again when it has.
        if (!registry.loaded.contains(grammar)) self.waiting = true;
        return null;
    }

    /// Moves what was found by `edits`, made since, along with `window`;
    /// drops the ranges they emptied.
    fn shift(self: *Finder, edits: []const c.TSInputEdit, window: *?[2]u32) void {
        if (edits.len == 0) return;
        for (edits) |*e| {
            for (self.found_ranges.items) |*r| {
                if (r.end_byte < e.start_byte) continue;
                Layers.shift(&r.start_byte, &r.start_point, e);
                Layers.shift(&r.end_byte, &r.end_point, e);
            }
            if (window.*) |w| window.* = .{ syntax.mapByte(w[0], e, e.start_byte), syntax.mapByte(w[1], e, e.new_end_byte) };
        }
        var kept: usize = 0;
        for (self.found.items) |f| {
            var len: u32 = 0;
            for (self.found_ranges.items[f.first..][0..f.len]) |r| if (r.start_byte < r.end_byte) {
                self.found_ranges.items[f.first + len] = r;
                len += 1;
            };
            if (len == 0) continue;
            self.found.items[kept] = f;
            self.found.items[kept].len = len;
            kept += 1;
        }
        self.found.items.len = kept;
    }

    /// For a look around window `w`, which saw only the part of combined
    /// `layer` in it: adds its ranges outside `w` to what was found for its
    /// language.
    fn keepCombined(self: *Finder, gpa: Allocator, layer: *const Layer, w: [2]u32) !void {
        const group: ?usize = for (self.found.items, 0..) |f, i| {
            if (f.combined and f.language == layer.language) break i;
        } else null;
        var in_first: usize = 0;
        var in_len: usize = 0;
        if (group) |g| {
            in_first = self.found.items[g].first;
            in_len = self.found.items[g].len;
        }
        try self.found_ranges.ensureUnusedCapacity(gpa, layer.ranges.len + in_len);
        const first = self.found_ranges.items.len;
        var inside: usize = 0;
        for (layer.ranges) |r| {
            // In the window: found again, or gone.
            if (r.start_byte <= w[1] and r.end_byte >= w[0]) continue;
            while (inside < in_len and self.found_ranges.items[in_first + inside].start_byte < r.start_byte) : (inside += 1) {
                self.found_ranges.appendAssumeCapacity(self.found_ranges.items[in_first + inside]);
            }
            self.found_ranges.appendAssumeCapacity(r);
        }
        while (inside < in_len) : (inside += 1) self.found_ranges.appendAssumeCapacity(self.found_ranges.items[in_first + inside]);
        const len: u32 = @intCast(self.found_ranges.items.len - first);
        if (len == 0) return;
        if (group) |g| {
            self.found.items[g].first = @intCast(first);
            self.found.items[g].len = len;
        } else try self.found.append(gpa, .{ .language = layer.language, .first = @intCast(first), .len = len, .combined = true });
    }
};

/// Work on one tree off the main thread: the parse of a layer that ran over
/// its budget, then a look at what the tree injects (around `region`, or all
/// over); or just the look, over a tree too big for a frame. Until `done`
/// the worker owns all of it but `edits` and `stale`; the main thread frees
/// it once the worker has let go (`exited`).
const Job = struct {
    gpa: Allocator,
    executor: *Executor,
    /// A layer's parser, part way through.
    parser: ?*c.TSParser,
    /// A copy of the tree the parse started from, or of the tree to look at.
    base: ?*c.TSTree,
    /// The text as it was.
    rope: Rope,
    lang: *Language,
    parent: ?*Language,
    /// What the tree covers (empty: the whole buffer).
    ranges: []c.TSRange,
    /// `lang`'s injections, if anything can be injected.
    iq: ?*const Query,
    /// Look at all of the tree, or around `region` (which grows by where
    /// the parse changed the tree).
    whole: bool,
    region: ?[2]u32,
    finder: Finder,
    /// What the parse made.
    tree: ?*c.TSTree = null,
    /// The finder's results are in, and cover `window` (null: everywhere).
    looked: bool = false,
    window: ?[2]u32 = null,
    /// Main thread: edits since the snapshot, replayed on what it made.
    edits: std.ArrayList(c.TSInputEdit) = .empty,
    /// Too many edits to replay (or one couldn't be kept): what it made is dropped.
    stale: bool = false,
    /// Set by the main thread when what it works for goes away.
    cancel: std.atomic.Value(bool) = .init(false),
    done: std.atomic.Value(bool) = .init(false),
    exited: std.atomic.Value(bool) = .init(false),

    const Spec = struct {
        /// A parse part way through, which started from `tree`.
        parser: ?*c.TSParser = null,
        tree: ?*c.TSTree,
        lang: *Language,
        parent: ?*Language,
        ranges: []const c.TSRange,
        iq: ?*const Query,
        whole: bool,
        region: ?[2]u32 = null,
    };

    /// Starts a worker on `spec`, with snapshots of the text, the tree and
    /// the languages loaded. Null without an executor, or if that failed.
    fn start(syn: *Syntax, rope: *const Rope, spec: Spec) ?*Job {
        const gpa = syn.gpa;
        const executor = syn.executor orelse return null;
        const registry = syn.language.registry orelse return null;
        const job = gpa.create(Job) catch return null;
        const ranges = gpa.dupe(c.TSRange, spec.ranges) catch {
            gpa.destroy(job);
            return null;
        };
        const loaded = registry.loaded.clone(gpa) catch {
            gpa.free(ranges);
            gpa.destroy(job);
            return null;
        };
        job.* = .{
            .gpa = gpa,
            .executor = executor,
            .parser = spec.parser,
            .base = if (spec.tree) |t| c.ts_tree_copy(t) else null,
            .rope = rope.clone(),
            .lang = spec.lang,
            .parent = spec.parent,
            .ranges = ranges,
            .iq = spec.iq,
            .whole = spec.whole,
            .region = spec.region,
            .finder = .{ .loaded = loaded },
        };
        job.finder.cancel = &job.cancel;
        executor.spawn(run, .{job}) catch {
            // The parser stays the caller's.
            if (job.base) |b| c.ts_tree_delete(b);
            job.rope.deinit(gpa);
            job.finder.deinit(gpa);
            gpa.free(ranges);
            gpa.destroy(job);
            return null;
        };
        return job;
    }

    fn run(job: *Job) void {
        job.work();
        if (job.base) |b| c.ts_tree_delete(b);
        job.base = null;
        job.rope.deinit(job.gpa);
        const executor = job.executor;
        job.done.store(true, .release);
        // Wakes the event loop, whose repaint takes the results in.
        executor.post(.{ .ctx = executor, .run = noop });
        executor.taskDone();
        // Last touch: the main thread may free the job from here on.
        job.exited.store(true, .release);
    }

    fn work(job: *Job) void {
        var tree = job.base;
        if (job.parser) |parser| {
            const options: c.TSParseOptions = .{ .payload = job, .progress_callback = cancelled };
            // Resumes the parse where the budget stopped it.
            job.tree = c.ts_parser_parse_with_options(parser, job.base, Syntax.ropeInput(&job.rope), options);
            tree = job.tree;
            if (!job.whole) if (job.base) |base| if (job.tree) |new| noteTreeChanged(&job.region, base, new);
        }
        const t = tree orelse return;
        if (!job.whole and job.region == null) return;
        const iq = job.iq orelse {
            // Nothing can be injected: there is nothing, everywhere.
            job.looked = true;
            return;
        };
        job.window = job.finder.find(job.gpa, &job.rope, iq, job.lang, job.parent, t, job.ranges, if (job.whole) null else job.region) catch return;
        job.looked = true;
    }

    fn cancelled(state: [*c]c.TSParseState) callconv(.c) bool {
        const job: *Job = @ptrCast(@alignCast(state.*.payload));
        return job.cancel.load(.monotonic);
    }

    /// Main thread: the text changed by `e` since the snapshot.
    fn note(job: *Job, e: *const c.TSInputEdit) void {
        if (job.stale) return;
        if (job.edits.items.len >= max_edits) {
            job.stale = true;
            return;
        }
        job.edits.append(job.gpa, e.*) catch {
            job.stale = true;
        };
    }

    /// Main thread: waits until the worker has let go.
    fn wait(job: *Job) void {
        while (!job.exited.load(.acquire)) std.Thread.yield() catch {};
    }

    /// Main thread: stops the worker (at its next check) and frees the job.
    fn abandon(job: *Job) void {
        job.cancel.store(true, .release);
        job.destroy();
    }

    /// Main thread: frees the job (and what of it wasn't taken) once the
    /// worker has let go.
    fn destroy(job: *Job) void {
        job.wait();
        const gpa = job.gpa;
        if (job.tree) |t| syntax.deleteTreeOf(t, if (job.ranges.len == 0) 0 else job.ranges[job.ranges.len - 1].end_byte - job.ranges[0].start_byte);
        if (job.parser) |p| c.ts_parser_delete(p);
        job.finder.deinit(gpa);
        job.edits.deinit(gpa);
        gpa.free(job.ranges);
        gpa.destroy(job);
    }
};

fn noop(_: *anyopaque) void {}

/// Appends the text of a content node: without its named children unless the
/// pattern includes them (as nvim-treesitter does: anonymous ones are tokens
/// of the text, like Markdown's emphasis delimiters), within the parent's ranges.
fn appendContent(gpa: Allocator, out: *std.ArrayList(c.TSRange), node: c.TSNode, pattern: *const Pattern, rope: *const Rope, parent: []const c.TSRange) !void {
    var whole: c.TSRange = .{
        .start_point = c.ts_node_start_point(node),
        .end_point = c.ts_node_end_point(node),
        .start_byte = c.ts_node_start_byte(node),
        .end_byte = c.ts_node_end_byte(node),
    };
    if (pattern.offset) |o| whole = offsetRange(whole, o, rope) orelse return;
    if (pattern.include_children) return appendClipped(gpa, out, whole, parent);
    var piece = whole;
    const count = c.ts_node_named_child_count(node);
    for (0..count) |i| {
        const child = c.ts_node_named_child(node, @intCast(i));
        const child_start = c.ts_node_start_byte(child);
        if (child_start > piece.start_byte) {
            var before = piece;
            if (child_start < before.end_byte) {
                before.end_byte = child_start;
                before.end_point = c.ts_node_start_point(child);
            }
            try appendClipped(gpa, out, before, parent);
        }
        const child_end = c.ts_node_end_byte(child);
        if (child_end > piece.start_byte) {
            piece.start_byte = child_end;
            piece.start_point = c.ts_node_end_point(child);
        }
        if (piece.start_byte >= piece.end_byte) return;
    }
    try appendClipped(gpa, out, piece, parent);
}

fn appendClipped(gpa: Allocator, out: *std.ArrayList(c.TSRange), r: c.TSRange, parent: []const c.TSRange) !void {
    if (r.start_byte >= r.end_byte) return;
    if (parent.len == 0) return out.append(gpa, r);
    for (parent) |p| {
        if (p.end_byte <= r.start_byte) continue;
        if (p.start_byte >= r.end_byte) break;
        var piece = r;
        if (p.start_byte > piece.start_byte) {
            piece.start_byte = p.start_byte;
            piece.start_point = p.start_point;
        }
        if (p.end_byte < piece.end_byte) {
            piece.end_byte = p.end_byte;
            piece.end_point = p.end_point;
        }
        if (piece.start_byte < piece.end_byte) try out.append(gpa, piece);
    }
}

/// `#offset!`: moves the range's ends by rows and columns.
fn offsetRange(r: c.TSRange, o: [4]i32, rope: *const Rope) ?c.TSRange {
    const Move = struct {
        fn f(p: c.TSPoint, rows: i32, cols: i32, text: *const Rope) ?struct { u32, c.TSPoint } {
            const row = @as(i64, p.row) + rows;
            const col = @as(i64, p.column) + cols;
            if (row < 0 or col < 0) return null;
            const byte = text.pointToOffset(.{ .row = @intCast(row), .column = @intCast(col) });
            const at = text.offsetToPoint(byte);
            return .{ @intCast(byte), .{ .row = at.row, .column = at.column } };
        }
    };
    const s = Move.f(r.start_point, o[0], o[1], rope) orelse return null;
    const e = Move.f(r.end_point, o[2], o[3], rope) orelse return null;
    if (s[0] >= e[0]) return null;
    return .{ .start_byte = s[0], .start_point = s[1], .end_byte = e[0], .end_point = e[1] };
}

/// Sorts ranges and merges overlapping ones, as included ranges must be.
fn normalize(ranges: []c.TSRange, len: *usize) void {
    std.mem.sort(c.TSRange, ranges, {}, struct {
        fn lessThan(_: void, a: c.TSRange, b: c.TSRange) bool {
            return a.start_byte < b.start_byte;
        }
    }.lessThan);
    var n: usize = 0;
    for (ranges) |r| {
        if (n > 0 and r.start_byte < ranges[n - 1].end_byte) {
            if (r.end_byte > ranges[n - 1].end_byte) {
                ranges[n - 1].end_byte = r.end_byte;
                ranges[n - 1].end_point = r.end_point;
            }
            continue;
        }
        ranges[n] = r;
        n += 1;
    }
    len.* = n;
}

fn rangesEql(a: []const c.TSRange, b: []const c.TSRange) bool {
    if (a.len != b.len) return false;
    for (a, b) |x, y| if (x.start_byte != y.start_byte or x.end_byte != y.end_byte) return false;
    return true;
}

// ---- Tests --------------------------------------------------------------------

const theme_mod = @import("../../framework/ui/theme.zig");

/// A buffer's text, parsed and highlighted as `path`'s language.
const TestBuffer = struct {
    registry: syntax.Registry,
    theme: theme_mod.Theme,
    rope: Rope,
    syn: Syntax,
    spans: std.ArrayList(syntax.HighlightSpan) = .empty,

    /// Null without the grammar farm ($CS_ZIG_GRAMMARS, set by `nix develop`).
    fn create(path: []const u8, text: []const u8) !?*TestBuffer {
        const gpa = std.testing.allocator;
        const self = try gpa.create(TestBuffer);
        errdefer gpa.destroy(self);
        self.theme = theme_mod.build(theme_mod.schemeByName("Mariana").?);
        self.registry = .init(gpa, &self.theme);
        var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
        const dir_z = std.fmt.bufPrintSentinel(&dir_buf, "{s}", .{self.registry.grammar_dir}, 0) catch null;
        if (dir_z == null or self.registry.grammar_dir.len == 0 or std.c.access(dir_z.?, std.c.F_OK) != 0) {
            self.registry.deinit();
            gpa.destroy(self);
            return null;
        }
        errdefer self.registry.deinit();
        const lang = self.registry.get(languages.forPath(path)) orelse return error.GrammarMissing;
        self.rope = try .fromSlice(gpa, text);
        errdefer self.rope.deinit(gpa);
        self.syn = try .init(gpa, lang);
        self.spans = .empty;
        return self;
    }

    fn destroy(self: *TestBuffer) void {
        const gpa = std.testing.allocator;
        self.spans.deinit(gpa);
        self.syn.deinit();
        self.rope.deinit(gpa);
        self.registry.deinit();
        gpa.destroy(self);
    }

    fn highlight(self: *TestBuffer) void {
        self.syn.update(&self.rope);
        self.syn.highlight(&self.rope, 0, self.rope.len(), &self.spans);
    }

    /// The paint of the first byte of `needle` (after `after`): the last
    /// span covering it, as the editor paints them in order.
    fn paintAt(self: *TestBuffer, needle: []const u8, after: usize) ?syntax.HighlightSpan {
        var buf: std.ArrayList(u8) = .empty;
        defer buf.deinit(std.testing.allocator);
        var it = self.rope.chunks(0, self.rope.len());
        while (it.next()) |chunk| buf.appendSlice(std.testing.allocator, chunk) catch return null;
        const at = std.mem.indexOfPos(u8, buf.items, after, needle) orelse return null;
        var paint: ?syntax.HighlightSpan = null;
        for (self.spans.items) |span| if (span.start <= at and at < span.end) {
            paint = span;
        };
        return paint;
    }

    fn replace(self: *TestBuffer, start: usize, end: usize, text: []const u8) !void {
        const start_point = self.rope.offsetToPoint(start);
        const old_end_point = self.rope.offsetToPoint(end);
        try self.rope.replace(std.testing.allocator, start, end, text);
        self.syn.applyChange(.{
            .start = start,
            .old_len = end - start,
            .new_len = text.len,
            .start_point = start_point,
            .old_end_point = old_end_point,
            .new_end_point = self.rope.offsetToPoint(start + text.len),
        });
    }

    /// Paints like a Syntax that parsed the text as it is now in one go.
    fn expectSameAsFresh(self: *TestBuffer) !void {
        const gpa = std.testing.allocator;
        var fresh = try Syntax.init(gpa, self.syn.language);
        defer fresh.deinit();
        fresh.executor = null;
        fresh.update(&self.rope);
        var want: std.ArrayList(syntax.HighlightSpan) = .empty;
        defer want.deinit(gpa);
        fresh.highlight(&self.rope, 0, self.rope.len(), &want);
        self.highlight();
        const len = self.rope.len();
        const Paint = struct { color: @FieldType(syntax.HighlightSpan, "color"), style: theme_mod.FontStyle };
        const got_paint = try gpa.alloc(?Paint, len);
        defer gpa.free(got_paint);
        const want_paint = try gpa.alloc(?Paint, len);
        defer gpa.free(want_paint);
        @memset(got_paint, null);
        @memset(want_paint, null);
        for (self.spans.items) |sp| @memset(got_paint[sp.start..sp.end], Paint{ .color = sp.color, .style = sp.style });
        for (want.items) |sp| @memset(want_paint[sp.start..sp.end], Paint{ .color = sp.color, .style = sp.style });
        for (got_paint, want_paint, 0..) |g, w, i| if (!std.meta.eql(g, w)) {
            std.debug.print("byte {d} painted differently\n", .{i});
            return error.TestUnexpectedResult;
        };
    }

    fn expectColor(self: *TestBuffer, needle: []const u8, color: anytype) !void {
        const span = self.paintAt(needle, 0) orelse {
            std.debug.print("\"{s}\" isn't highlighted\n", .{needle});
            return error.TestUnexpectedResult;
        };
        try std.testing.expectEqual(color, span.color);
    }
};

test "Markdown injects inline markup and fenced code" {
    const tb = try TestBuffer.create("README.md",
        \\# Title
        \\
        \\Some *emphasis* and `code` here.
        \\
        \\```rust
        \\fn main() { let x = 1; }
        \\```
        \\
    ) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const s = &tb.theme.syntax;
    try std.testing.expectEqual(theme_mod.FontStyle.italic, tb.paintAt("emphasis", 0).?.style);
    try tb.expectColor("code", s.string);
    try tb.expectColor("fn", s.keyword);
    try tb.expectColor("let", s.keyword);
}

test "Vue injects TypeScript into <script lang=ts> and CSS into <style>" {
    const tb = try TestBuffer.create("App.vue",
        \\<template><p>{{ n }}</p></template>
        \\<script setup lang="ts">
        \\const n: number = 1
        \\</script>
        \\<style scoped>
        \\p { margin: 4px; }
        \\</style>
        \\
    ) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const s = &tb.theme.syntax;
    try tb.expectColor("const", s.keyword);
    try tb.expectColor("number", s.keyword);
    try tb.expectColor("4px", s.number);
}

test "injections nest: JavaScript in HTML in Markdown" {
    const tb = try TestBuffer.create("notes.md",
        \\Intro.
        \\
        \\<div>
        \\<script>
        \\const answer = 42;
        \\</script>
        \\</div>
        \\
    ) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const s = &tb.theme.syntax;
    try tb.expectColor("div", s.tag);
    try tb.expectColor("const", s.keyword);
    try tb.expectColor("42", s.number);
}

test "edits keep layers, shift them and reparse only what they touch" {
    const tb = try TestBuffer.create("index.html",
        \\<p>hi</p>
        \\<script>let a = 1;</script>
        \\<style>b { margin: 2px; }</style>
        \\
    ) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const s = &tb.theme.syntax;
    const layers = &tb.syn.injections.children;
    try std.testing.expectEqual(2, layers.items.len);
    const script = layers.items[0];
    const style = layers.items[1];
    const style_tree = style.tree.?;

    // Typing inside the script reparses it (the same layer) and leaves the style alone.
    const at = std.mem.indexOf(u8, "<p>hi</p>\n<script>let a = 1;", "1;").?;
    try tb.replace(at, at + 1, "1 + 22");
    tb.highlight();
    try std.testing.expectEqual(2, layers.items.len);
    try std.testing.expectEqual(script, layers.items[0]);
    try std.testing.expectEqual(style, layers.items[1]);
    try std.testing.expectEqual(style_tree, style.tree.?);
    try tb.expectColor("22", s.number);
    try tb.expectColor("2px", s.number);

    // Text before both shifts them without reparsing.
    try tb.replace(0, 0, "<h1>Title</h1>\n");
    tb.highlight();
    try std.testing.expectEqual(style_tree, style.tree.?);
    try tb.expectColor("let", s.keyword);
    try tb.expectColor("2px", s.number);
    const style_start = "<h1>Title</h1>\n<p>hi</p>\n<script>let a = 1 + 22;</script>\n<style>".len;
    try std.testing.expectEqual(style_start, style.ranges[0].start_byte);

    // Breaking the script's start tag drops its layer.
    const tag = "<h1>Title</h1>\n<p>hi</p>\n<".len;
    try tb.replace(tag, tag + 1, "x");
    tb.highlight();
    try std.testing.expectEqual(1, layers.items.len);
    try std.testing.expectEqual(style, layers.items[0]);
    try std.testing.expect(tb.paintAt("let", 0) == null or tb.paintAt("let", 0).?.color.r != s.keyword.r);
}

test "an edit that changes the structure beyond itself re-injects there" {
    const tb = try TestBuffer.create("notes.md",
        \\Para *one*.
        \\
        \\Para *two*.
        \\
        \\Para *three*.
        \\
    ) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const layers = &tb.syn.injections.children;
    try std.testing.expectEqual(3, layers.items.len);
    // An unclosed fence swallows the paragraphs after it.
    try tb.replace(0, 0, "```\n");
    tb.highlight();
    try std.testing.expectEqual(0, layers.items.len);
    try tb.replace(0, 4, "");
    tb.highlight();
    try std.testing.expectEqual(3, layers.items.len);
    try std.testing.expectEqual(theme_mod.FontStyle.italic, tb.paintAt("three", 0).?.style);
}

test "editing a predicate's node elsewhere in the match re-injects the content" {
    const tb = try TestBuffer.create("App.vue",
        \\<script lang="ts">
        \\let n: number = 1
        \\</script>
        \\
    ) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const layers = &tb.syn.injections.children;
    try std.testing.expectEqual(1, layers.items.len);
    try std.testing.expectEqualStrings("TypeScript", layers.items[0].language.config.name);
    const at = std.mem.indexOf(u8, "<script lang=\"ts\">", "ts").?;
    try tb.replace(at, at + 2, "js");
    tb.highlight();
    try std.testing.expectEqual(1, layers.items.len);
    try std.testing.expectEqualStrings("JavaScript", layers.items[0].language.config.name);
}

test "combined injections are looked at again only around an edit" {
    const gpa = std.testing.allocator;
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    try text.appendSlice(gpa, "const a = css`margin: 1px;`;\n");
    for (0..60) |i| try text.print(gpa, "function f{d}(x) {{ return x + {d}; }}\n", .{ i, i });
    try text.appendSlice(gpa, "const b = css`padding: 2px;`;\n");
    const tb = try TestBuffer.create("app.js", text.items) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.highlight();
    const s = &tb.theme.syntax;
    const layers = &tb.syn.injections.children;
    try std.testing.expectEqual(1, layers.items.len);
    const css = layers.items[0];
    try std.testing.expect(css.combined);
    try std.testing.expectEqual(2, css.ranges.len);

    // Far from both templates: the layer and its ranges stay.
    const mid = std.mem.indexOf(u8, text.items, "return x + 30").?;
    try tb.replace(mid, mid + "return x".len, "return y");
    tb.highlight();
    try std.testing.expectEqual(css, layers.items[0]);
    try std.testing.expectEqual(2, css.ranges.len);
    try tb.expectColor("2px", s.number);

    // A new template between them joins the layer; the others stay in it.
    try tb.replace(mid, mid, "const c = css`border: 3px;`; ");
    tb.highlight();
    try std.testing.expectEqual(1, layers.items.len);
    try std.testing.expectEqual(css, layers.items[0]);
    try std.testing.expectEqual(3, css.ranges.len);
    try tb.expectColor("1px", s.number);
    try tb.expectColor("3px", s.number);
    try tb.expectColor("2px", s.number);

    // Removing one keeps the rest.
    const first = std.mem.indexOf(u8, text.items, "css`margin: 1px;`").?;
    try tb.replace(first, first + "css`margin: 1px;`".len, "1");
    tb.highlight();
    try std.testing.expectEqual(css, layers.items[0]);
    try std.testing.expectEqual(2, css.ranges.len);
    try tb.expectSameAsFresh();
}

/// Paints the whole buffer, as the editor does each frame, until no worker
/// has anything left for it, making one of `edits` (the first `[0]` becomes
/// `[1]`) each round something is on a worker.
fn settle(tb: *TestBuffer, executor: *Executor, edits: []const [2][]const u8) !struct { edits: usize, layer_jobs: usize, buffer_jobs: usize } {
    var made: usize = 0;
    var layer_jobs: usize = 0;
    var buffer_jobs: usize = 0;
    var rounds: usize = 0;
    while (true) : (rounds += 1) {
        try std.testing.expect(rounds < 5000);
        executor.drain();
        tb.highlight();
        if (tb.syn.injections.job != null) buffer_jobs += 1;
        for (tb.syn.injections.children.items) |layer| {
            if (layer.job != null) layer_jobs += 1;
        }
        // A layer's finished job is taken in when it is next painted.
        const settled = !tb.syn.parsing() and tb.syn.job == null and !tb.syn.dirty and !anyJob(tb.syn.injections.children.items);
        if (settled and made == edits.len) break;
        if (made < edits.len) {
            const e = edits[made];
            made += 1;
            const all = try tb.rope.toOwnedSlice(std.testing.allocator);
            defer std.testing.allocator.free(all);
            const at = std.mem.indexOf(u8, all, e[0]).?;
            try tb.replace(at, at + e[0].len, e[1]);
            continue;
        }
        var req: std.c.timespec = .{ .sec = 0, .nsec = 2 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
    }
    return .{ .edits = made, .layer_jobs = layer_jobs, .buffer_jobs = buffer_jobs };
}

fn anyJob(list: []const *Layer) bool {
    for (list) |layer| if (layer.job != null or anyJob(layer.children.items)) return true;
    return false;
}

/// Sets the UI-thread budgets (ns) until the returned value is restored.
fn setBudgets(layer: u64, query: u64) [2]u64 {
    defer {
        layer_budget_ns = layer;
        query_budget_ns = query;
    }
    return .{ layer_budget_ns, query_budget_ns };
}

fn restoreBudgets(saved: [2]u64) void {
    layer_budget_ns = saved[0];
    query_budget_ns = saved[1];
}

test "layers too big for a frame parse on workers; edits meanwhile are replayed" {
    const gpa = std.testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    try text.appendSlice(gpa, "<p>intro</p>\n<script>\n");
    for (0..300) |i| try text.print(gpa, "function f{d}(a) {{ const s = css`margin: {d}px;`; return a + 0x{x}; }}\n", .{ i, i, i });
    try text.appendSlice(gpa, "</script>\n<style>b { margin: 2px; }</style>\n");
    for (0..200) |i| try text.print(gpa, "<div class=\"c{d}\"><b>{d}</b></div>\n", .{ i, i });
    const tb = try TestBuffer.create("big.html", text.items) orelse return error.SkipZigTest;
    defer tb.destroy();
    for ([_][]const u8{ "a.js", "a.css" }) |p| _ = tb.registry.get(languages.forPath(p)).?;
    tb.syn.executor = &executor;
    // Every layer's parse and every query runs over.
    const saved = setBudgets(0, 0);
    defer restoreBudgets(saved);

    const edits = [_][2][]const u8{
        .{ "intro", "intro text" },
        .{ "f5(a) { const s", "f5(a) { let s" },
        .{ "margin: 7px", "padding: 7px" },
        .{ "return a + 0x9;", "return a + 0x9 + `t`;" },
        .{ "<style>", "<style media=\"x\">" },
    };
    const done = try settle(tb, &executor, &edits);
    try std.testing.expectEqual(edits.len, done.edits);
    try std.testing.expect(done.layer_jobs > 0 and done.buffer_jobs > 0);
    _ = setBudgets(std.math.maxInt(u64), std.math.maxInt(u64));
    const s = &tb.theme.syntax;
    try tb.expectColor("function f299", s.keyword);
    try tb.expectColor("299px", s.number);
    try tb.expectSameAsFresh();
}

test "a look on a worker that finds grammars not loaded yet loads them and looks again" {
    const gpa = std.testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    for (0..200) |i| try text.print(gpa, "Para {d} with *emphasis*.\n\n", .{i});
    try text.appendSlice(gpa, "```rust\nfn main() { let x = 1; }\n```\n");
    const tb = try TestBuffer.create("notes.md", text.items) orelse return error.SkipZigTest;
    defer tb.destroy();
    tb.syn.executor = &executor;
    const saved = setBudgets(layer_budget_ns, 0);
    defer restoreBudgets(saved);
    const done = try settle(tb, &executor, &.{});
    try std.testing.expect(done.buffer_jobs > 0);
    const s = &tb.theme.syntax;
    try tb.expectColor("fn", s.keyword);
    try std.testing.expectEqual(theme_mod.FontStyle.italic, tb.paintAt("emphasis", 0).?.style);
}

test "a buffer closed while its layers are on workers stops them" {
    const gpa = std.testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(gpa);
    try text.appendSlice(gpa, "<script>\n");
    for (0..3000) |i| try text.print(gpa, "function f{d}(a) {{ return a + {d}; }}\n", .{ i, i });
    try text.appendSlice(gpa, "</script>\n");
    const tb = try TestBuffer.create("big.html", text.items) orelse return error.SkipZigTest;
    _ = tb.registry.get(languages.forPath("a.js")).?;
    tb.syn.executor = &executor;
    const saved = setBudgets(0, std.math.maxInt(u64));
    defer restoreBudgets(saved);
    // The buffer's own parse may take a worker too.
    var rounds: usize = 0;
    while (tb.syn.injections.children.items.len == 0) : (rounds += 1) {
        try std.testing.expect(rounds < 3000);
        var req: std.c.timespec = .{ .sec = 0, .nsec = 2 * std.time.ns_per_ms };
        _ = std.c.nanosleep(&req, null);
        executor.drain();
        tb.highlight();
    }
    try std.testing.expect(tb.syn.injections.children.items[0].job != null);
    tb.destroy();
    try std.testing.expectEqual(0, executor.active.load(.acquire));
}
