//! Workspace side of LSP: results → UI (completion popup, hover popup,
//! definition / references / rename / formatting), plus the related keys.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const movement = @import("../../model/text/movement.zig");
const platform = @import("../../framework/platform/platform.zig");
const lsp_mod = @import("../../model/lsp/lsp.zig");
const protocol = @import("../../model/lsp/protocol.zig");
const fuzzy = @import("../../model/project/fuzzy.zig");
const ui = @import("../../framework/ui/ui.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Rect = ui.Rect;

pub const Completion = struct {
    arena: std.heap.ArenaAllocator,
    editor: *Editor,
    /// Where the word being completed starts.
    word_start: usize,
    items: []lsp_mod.CompletionItem,
    filtered: std.ArrayList(u32) = .empty,
    selected: usize = 0,
    scroll: usize = 0,
    rect: Rect = .{},

    fn deinit(self: *Completion, gpa: Allocator) void {
        self.filtered.deinit(gpa);
        self.arena.deinit();
    }
};

pub const Hover = struct {
    arena: std.heap.ArenaAllocator,
    editor: *Editor,
    offset: usize,
    text: []const u8,
    /// Anchored at the mouse rather than the caret.
    at_mouse: bool,
    mouse: platform.Vec2,
};

pub const State = struct {
    completion: ?Completion = null,
    hover: ?Hover = null,
    matcher: ?fuzzy.Matcher = null,
    /// Mouse rest tracking for hover-on-dwell.
    rest_pos: platform.Vec2 = .{},
    rest_since: ?i64 = null,
    rest_requested: bool = false,
    pending_rename_offset: usize = 0,
    /// vim's gd: if the server finds nothing, the first occurrence of the
    /// word at `offset` in buffer `buffer_id` (the active one, still).
    goto_fallback: ?struct { buffer_id: u32, offset: usize } = null,
    /// The code actions listed in the quick panel, until one is chosen.
    code_actions: ?CodeActions = null,
};

pub const CodeActions = struct {
    arena: std.heap.ArenaAllocator,
    buffer: *Buffer,
    /// Each action's JSON, by the list's item value.
    json: []const []const u8,
};

/// The quick panel tag of the code action list.
pub const code_action_tag: u32 = 0xC0DE;

pub fn deinit(ws: *Workspace) void {
    closeCompletion(ws);
    closeHover(ws);
    clearCodeActions(ws);
    if (ws.lsp_state.matcher) |*m| m.deinit();
}

pub fn closeCompletion(ws: *Workspace) void {
    if (ws.lsp_state.completion) |*c| c.deinit(ws.gpa);
    ws.lsp_state.completion = null;
}

pub fn closeHover(ws: *Workspace) void {
    if (ws.lsp_state.hover) |*h| h.arena.deinit();
    ws.lsp_state.hover = null;
}

/// Sink for LSP results (called on the main thread).
pub fn onResult(ctx: *anyopaque, result: lsp_mod.Result) void {
    const ws: *Workspace = @ptrCast(@alignCast(ctx));
    ws.dirty = true;
    switch (result) {
        .diagnostics_changed, .status_changed => {},
        .message => |m| ws.setStatus("{s}", .{m[0..@min(m.len, 150)]}),
        .hover => |h| {
            const ed = ws.activeEditor() orelse return;
            if (ed.buffer != h.buffer) return;
            closeHover(ws);
            var arena = std.heap.ArenaAllocator.init(ws.gpa);
            const text = arena.allocator().dupe(u8, h.text) catch return;
            const at_mouse = ws.lsp_state.rest_requested;
            ws.lsp_state.hover = .{ .arena = arena, .editor = ed, .offset = h.offset, .text = text, .at_mouse = at_mouse, .mouse = ws.lsp_state.rest_pos };
        },
        .goto => |g| {
            const fallback = ws.lsp_state.goto_fallback;
            ws.lsp_state.goto_fallback = null;
            if (g.locations.len == 0) {
                if (fallback) |f| if (ws.activeEditor()) |ed| if (ed.buffer.id == f.buffer_id and gotoFirstOccurrence(ed, f.offset)) return;
                ws.setStatus("No {s} found", .{switch (g.what) {
                    .definition => "definition",
                    .implementation => "implementation",
                    .type_definition => "type definition",
                }});
                return;
            }
            if (g.locations.len == 1) {
                jumpTo(ws, g.locations[0]);
                return;
            }
            showLocations(ws, switch (g.what) {
                .definition => "Definitions",
                .implementation => "Implementations",
                .type_definition => "Type Definitions",
            }, g.locations);
        },
        .references => |r| {
            if (r.locations.len == 0) {
                ws.setStatus("No references found", .{});
                return;
            }
            showLocations(ws, "References", r.locations);
        },
        .completion => |c| showCompletion(ws, c.buffer, c.offset, c.items),
        .workspace_edit => |we| {
            var count: usize = 0;
            for (we.files) |f| {
                const ed = ws.openFile(f.path, false) catch continue;
                applyTextEdits(ed, f.edits, we.encoding);
                count += f.edits.len;
            }
            ws.setStatus("Applied {d} edits in {d} files", .{ count, we.files.len });
        },
        .code_actions => |ca| showCodeActions(ws, ca.buffer, ca.actions),
        .formatting => |f| {
            const ed = ws.activeEditor() orelse return;
            if (ed.buffer != f.buffer or ed.buffer.version != f.version) return;
            applyTextEdits(ed, f.edits, f.encoding);
        },
    }
}

// ---- Code actions ---------------------------------------------------------------

fn clearCodeActions(ws: *Workspace) void {
    if (ws.lsp_state.code_actions) |*c| c.arena.deinit();
    ws.lsp_state.code_actions = null;
}

/// ctrl+. / SPC c a: asks the server what it can do with the selection
/// (or the caret's line), to pick from a list.
pub fn requestCodeActions(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    const l = ws.lsp orelse return ws.setStatus("No language server for this file", .{});
    const sel = ed.primarySelection();
    var start = sel.start();
    var end = sel.end();
    if (sel.isEmpty()) {
        // The caret's line: the diagnostics on it get their fixes.
        const row = ed.buffer.offsetToPoint(sel.head).row;
        start = ed.buffer.lineStart(row);
        end = ed.buffer.lineEnd(row);
    }
    if (!l.codeActions(ed.buffer, start, end)) ws.setStatus("The language server has no code actions for this file", .{});
}

fn showCodeActions(ws: *Workspace, buffer: *Buffer, actions: []const lsp_mod.CodeAction) void {
    const ed = ws.activeEditor() orelse return;
    if (ed.buffer != buffer) return;
    var enabled: usize = 0;
    for (actions) |a| {
        if (a.disabled == null) enabled += 1;
    }
    if (enabled == 0) return ws.setStatus("No code actions here", .{});
    clearCodeActions(ws);
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    const alloc = arena.allocator();
    const json = alloc.alloc([]const u8, actions.len) catch return arena.deinit();
    for (actions, json) |a, *j| j.* = alloc.dupe(u8, a.json) catch "";
    ws.lsp_state.code_actions = .{ .arena = arena, .buffer = buffer, .json = json };

    var titles: std.ArrayList([]const u8) = .empty;
    defer titles.deinit(ws.gpa);
    var values: std.ArrayList(usize) = .empty;
    defer values.deinit(ws.gpa);
    // Preferred fixes first, then in the server's order; disabled ones are left out.
    for ([_]bool{ true, false }) |preferred| for (actions, 0..) |a, i| {
        if (a.disabled != null or a.preferred != preferred) continue;
        titles.append(ws.gpa, a.title) catch return;
        values.append(ws.gpa, i) catch return;
    };
    const label = std.fmt.allocPrint(ws.gpa, "Code Actions ({d})", .{titles.items.len}) catch "Save changes?";
    ws.openList(.list, label, code_action_tag, titles.items);
    // The list's items carry their position; point them at the actions.
    if (ws.overlay) |o| for (o.items.items, values.items) |*item, v| {
        item.payload = .{ .value = v };
        if (actions[v].kind.len > 0) item.detail = o.arena().dupe(u8, actions[v].kind) catch "";
    };
}

/// The chosen code action runs. Returns true for the code action list.
pub fn confirmList(ws: *Workspace, tag: u32, value: ?usize) bool {
    if (tag != code_action_tag) return false;
    defer clearCodeActions(ws);
    const ca = ws.lsp_state.code_actions orelse return true;
    const v = value orelse return true;
    if (v >= ca.json.len) return true;
    const l = ws.lsp orelse return true;
    if (!l.runCodeAction(ca.buffer, ca.json[v])) ws.setStatus("The language server is gone", .{});
    return true;
}

fn applyTextEdits(ed: *Editor, edits: []const lsp_mod.TextEdit, enc: protocol.Encoding) void {
    var scratch: std.ArrayList(u8) = .empty;
    defer scratch.deinit(ed.gpa);
    const out = ed.gpa.alloc(editor_mod.Edit, edits.len) catch return;
    defer ed.gpa.free(out);
    for (edits, out) |e, *o| {
        const start = protocol.positionToOffset(ed.buffer, e.range.start, enc, &scratch);
        const end = protocol.positionToOffset(ed.buffer, e.range.end, enc, &scratch);
        o.* = .{ .start = start, .end = @max(start, end), .text = e.text };
    }
    ed.commitTransform(out, .other);
}

fn jumpTo(ws: *Workspace, loc: lsp_mod.Location) void {
    const ed = ws.openFile(loc.path, false) catch |err| {
        ws.setStatus("Unable to open {s}: {t}", .{ loc.path, err });
        return;
    };
    var scratch: std.ArrayList(u8) = .empty;
    defer scratch.deinit(ws.gpa);
    const off = protocol.positionToOffset(ed.buffer, loc.range.start, loc.encoding, &scratch);
    ed.pushSelectionHistory();
    ed.setSingleSelection(.caret(off));
    ed.afterCommand(true);
}

/// Lists locations in the quick panel ("path:line  code").
fn showLocations(ws: *Workspace, label: []const u8, locs: []const lsp_mod.Location) void {
    const o = ws.openLocationPanel(label) orelse return;
    var line_buf: std.ArrayList(u8) = .empty;
    defer line_buf.deinit(ws.gpa);
    for (locs) |loc| {
        const rel = if (ws.project) |p| p.relativePath(loc.path) orelse loc.path else loc.path;
        const title = std.fmt.allocPrint(o.arena(), "{s}:{d}", .{ rel, loc.range.start.line + 1 }) catch continue;
        // A preview of the line, from an open buffer when possible.
        var preview: []const u8 = "";
        for (ws.buffers.items) |b| if (b.path) |p| if (std.mem.eql(u8, p, loc.path) and loc.range.start.line < b.lineCount()) {
            const t = b.lineText(loc.range.start.line, &line_buf) catch "";
            preview = o.arena().dupe(u8, std.mem.trim(u8, t, " \t")) catch "";
        };
        const path = o.arena().dupe(u8, loc.path) catch continue;
        o.addItem(.{ .title = title, .detail = preview, .payload = .{ .location = .{ .path = path, .line = loc.range.start.line, .character = loc.range.start.character, .utf8 = loc.encoding == .utf8 } } }) catch break;
    }
}

pub fn confirmLocation(ws: *Workspace, path: []const u8, line: u32, character: u32, utf8: bool) void {
    jumpTo(ws, .{ .path = path, .range = .{ .start = .{ .line = line, .character = character }, .end = .{ .line = line, .character = character } }, .encoding = if (utf8) .utf8 else .utf16 });
}

/// Asks the language server where the symbol at `ed`'s caret is defined
/// (implemented, its type defined); false without a server. With
/// `fallback` (vim's gd) an empty answer goes to the word's first
/// occurrence in the file instead.
pub fn goto(ws: *Workspace, ed: *Editor, what: lsp_mod.Goto, fallback: bool) bool {
    const l = ws.lsp orelse return false;
    const head = ed.primarySelection().head;
    if (!l.goto(ed.buffer, head, what)) return false;
    ws.lsp_state.goto_fallback = if (fallback) .{ .buffer_id = ed.buffer.id, .offset = head } else null;
    return true;
}

/// The first occurrence in the file of the word at `offset` (vim's gd
/// without a language server); false when there is no word there.
pub fn gotoFirstOccurrence(ed: *Editor, offset: usize) bool {
    var s = movement.Scanner.init(&ed.buffer.rope);
    const w = movement.wordAt(&s, @min(offset, ed.buffer.len())) orelse return false;
    const word = ed.buffer.textRange(ed.gpa, w[0], w[1]) catch return false;
    defer ed.gpa.free(word);
    const at = ed.findForward(word, 0, true) orelse return false;
    ed.pushSelectionHistory();
    ed.setSingleSelection(.caret(at));
    ed.ensureCaretVisible(true);
    return true;
}

// ---- Completion ---------------------------------------------------------------------

fn wordStart(ed: *Editor, offset: usize) usize {
    var s = movement.Scanner.init(&ed.buffer.rope);
    var o = offset;
    while (o > 0) {
        const cp = s.charBefore(o) orelse break;
        if (!movement.isWordChar(cp)) break;
        o = s.prevBoundary(o);
    }
    return o;
}

fn showCompletion(ws: *Workspace, buffer: *Buffer, offset: usize, items: []const lsp_mod.CompletionItem) void {
    const ed = ws.activeEditor() orelse return;
    if (ed.buffer != buffer or ws.focus != .editor) return;
    const head = ed.primarySelection().head;
    const ws_start = wordStart(ed, head);
    // Stale if the caret left the word the request was made for.
    if (head < offset or wordStart(ed, offset) != ws_start) return;
    if (items.len == 0) return;
    closeCompletion(ws);
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    const alloc = arena.allocator();
    const copy = alloc.alloc(lsp_mod.CompletionItem, items.len) catch return;
    for (items, copy) |it, *out| {
        out.* = it;
        out.label = alloc.dupe(u8, it.label) catch "";
        out.detail = alloc.dupe(u8, it.detail) catch "";
        out.insert_text = alloc.dupe(u8, it.insert_text) catch "";
        out.filter_text = alloc.dupe(u8, it.filter_text) catch "";
        out.sort_text = alloc.dupe(u8, it.sort_text) catch "";
    }
    ws.lsp_state.completion = .{ .arena = arena, .editor = ed, .word_start = ws_start, .items = copy };
    refilter(ws);
}

/// Re-filters the open completion list against the typed prefix.
pub fn refilter(ws: *Workspace) void {
    const c = &(ws.lsp_state.completion orelse return);
    const ed = c.editor;
    const head = ed.primarySelection().head;
    if (head < c.word_start or wordStart(ed, head) != c.word_start or ed.selections.items.len == 0) {
        closeCompletion(ws);
        return;
    }
    const prefix = ed.buffer.textRange(ws.gpa, c.word_start, head) catch return;
    defer ws.gpa.free(prefix);
    if (ws.lsp_state.matcher == null) ws.lsp_state.matcher = fuzzy.Matcher.init(ws.gpa);
    const m = &ws.lsp_state.matcher.?;
    const Scored = struct { idx: u32, score: i32 };
    var scored: std.ArrayList(Scored) = .empty;
    defer scored.deinit(ws.gpa);
    for (c.items, 0..) |it, i| {
        const r = m.match(prefix, it.filter_text, false) orelse continue;
        scored.append(ws.gpa, .{ .idx = @intCast(i), .score = r.score }) catch break;
    }
    std.mem.sort(Scored, scored.items, c.items, struct {
        fn lt(items: []lsp_mod.CompletionItem, a: Scored, b: Scored) bool {
            if (a.score != b.score) return a.score > b.score;
            return std.mem.lessThan(u8, items[a.idx].sort_text, items[b.idx].sort_text);
        }
    }.lt);
    c.filtered.clearRetainingCapacity();
    for (scored.items) |s| c.filtered.append(ws.gpa, s.idx) catch break;
    c.selected = 0;
    c.scroll = 0;
    if (c.filtered.items.len == 0) closeCompletion(ws);
}

fn acceptCompletion(ws: *Workspace) void {
    const c = &(ws.lsp_state.completion orelse return);
    if (c.filtered.items.len == 0) return closeCompletion(ws);
    const item = c.items[c.filtered.items[c.selected]];
    const ed = c.editor;
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    // The item's text lives in the popup's arena, which closing it frees.
    const text = arena.allocator().dupe(u8, item.insert_text) catch return;
    const reps = arena.allocator().alloc(Editor.Replacement, ed.selections.items.len) catch return;
    const primary_head = ed.primarySelection().head;
    for (ed.selections.items, reps) |sel, *r| {
        // The primary cursor honours the server's edit range; others replace their word.
        var start = wordStart(ed, sel.head);
        var end = sel.head;
        if (sel.head == primary_head) if (item.edit_start) |es| {
            start = @min(es, sel.head);
            end = @max(item.edit_end orelse sel.head, sel.head);
        };
        r.* = .{ .start = start, .end = end, .text = text, .sel_anchor = text.len, .sel_head = text.len };
    }
    closeCompletion(ws);
    ed.applyReplacements(reps, .other);
}

/// Keys while the completion popup is open. Returns true if consumed.
pub fn completionKey(ws: *Workspace, k: platform.KeyEvent) bool {
    const c = &(ws.lsp_state.completion orelse return false);
    if (k.key != .named) return false;
    const n = c.filtered.items.len;
    const visible: usize = 10;
    switch (k.key.named) {
        .escape => closeCompletion(ws),
        .enter, .tab => acceptCompletion(ws),
        .down => c.selected = (c.selected + 1) % n,
        .up => c.selected = (c.selected + n - 1) % n,
        .page_down => c.selected = @min(n - 1, c.selected + visible),
        .page_up => c.selected -|= visible,
        else => return false,
    }
    if (ws.lsp_state.completion) |*cc| {
        if (cc.selected < cc.scroll) cc.scroll = cc.selected;
        if (cc.selected >= cc.scroll + visible) cc.scroll = cc.selected + 1 - visible;
    }
    return true;
}

/// After text was typed in the editor: refilter or (re)request completions.
pub fn afterTyping(ws: *Workspace, ed: *Editor, text: []const u8) void {
    const lsp = ws.lsp orelse return;
    if (text.len != 1) return closeCompletion(ws);
    const ch = text[0];
    const head = ed.primarySelection().head;
    if (lsp.isTriggerChar(ed.buffer, ch)) {
        closeCompletion(ws);
        _ = lsp.completion(ed.buffer, head, ch);
        return;
    }
    if (!movement.isWordChar(ch)) return closeCompletion(ws);
    if (ws.lsp_state.completion != null) {
        refilter(ws);
        if (ws.lsp_state.completion != null) return;
    }
    // Ask once the word has two characters.
    if (head - wordStart(ed, head) >= 2) _ = lsp.completion(ed.buffer, head, null);
}

// ---- Painting ---------------------------------------------------------------------

fn kindLetter(kind: u8) u8 {
    // LSP CompletionItemKind → a one-letter badge.
    return switch (kind) {
        2, 3, 4 => 'f',
        5, 10 => 'p',
        6, 21 => 'v',
        7, 8, 22 => 't',
        9 => 'm',
        13, 20 => 'e',
        14 => 'k',
        15 => 's',
        17 => 'F',
        else => ' ',
    };
}

pub fn paint(ws: *Workspace, ctx: *const ui.Ctx) void {
    paintHover(ws, ctx);
    paintCompletion(ws, ctx);
}

fn caretAnchor(ed: *Editor, offset: usize) ?platform.Vec2 {
    const row = ed.buffer.offsetToPoint(offset).row;
    if (ed.isRowHidden(row)) return null;
    const lh = ed.lineHeight();
    const x = ed.screenX(ed.xForOffset(offset));
    const y = ed.text_rect.y + @as(f32, @floatFromInt(ed.displayRowOfOffset(offset))) * lh - ed.scroll_y;
    if (y < ed.text_rect.y - lh or y > ed.text_rect.bottom()) return null;
    return .{ .x = x, .y = y };
}

fn paintCompletion(ws: *Workspace, ctx: *const ui.Ctx) void {
    const c = &(ws.lsp_state.completion orelse return);
    if (ws.activeEditor() != c.editor) return closeCompletion(ws);
    const ed = c.editor;
    const anchor = caretAnchor(ed, c.word_start) orelse return;
    const theme = ctx.theme;
    const row_h: f32 = 24;
    const shown = @min(c.filtered.items.len -| c.scroll, 10);
    var width: f32 = 280;
    for (c.filtered.items[c.scroll .. c.scroll + shown]) |idx| {
        const it = c.items[idx];
        width = @max(width, ctx.text.measure(it.label, ctx.fonts.mono) + ctx.text.measure(it.detail[0..@min(it.detail.len, 40)], ctx.fonts.ui_small) + 70);
    }
    width = @min(width, 640);
    const lh = ed.lineHeight();
    var y = anchor.y + lh + 2;
    const h = @as(f32, @floatFromInt(shown)) * row_h + 8;
    if (y + h > ws.size.y) y = anchor.y - h - 2;
    const x = @min(anchor.x - 30, ws.size.x - width - 4);
    const box: Rect = .{ .x = @max(4, x), .y = y, .w = width, .h = h };
    c.rect = box;
    ctx.shadow(box, 5, 10);
    ctx.scene.quad(box, .{ .color = theme.overlay_background, .border_color = theme.panel_border, .border_width = 1, .radius = 5 });
    ctx.scene.pushClip(box) catch return;
    defer ctx.scene.popClip();
    for (0..shown) |i| {
        const fi = c.scroll + i;
        const it = c.items[c.filtered.items[fi]];
        const r: Rect = .{ .x = box.x + 4, .y = box.y + 4 + @as(f32, @floatFromInt(i)) * row_h, .w = box.w - 8, .h = row_h };
        if (fi == c.selected) ctx.scene.quad(r, .{ .color = theme.list_selected, .radius = 3 });
        var letter = [_]u8{kindLetter(it.kind)};
        const badge: Rect = .{ .x = r.x + 4, .y = r.y + 4, .w = 16, .h = row_h - 8 };
        ctx.scene.quad(badge, .{ .color = theme.syntax.function.withAlpha(0.18), .radius = 3 });
        ctx.labelCentered(badge, &letter, ctx.fonts.ui_small, theme.syntax.function);
        const lw = ctx.label(.{ .x = r.x + 26, .y = r.y, .w = r.w, .h = r.h }, it.label, ctx.fonts.mono, theme.list_foreground);
        if (it.detail.len > 0) {
            const avail = r.right() - (r.x + 26 + lw + 16) - 6;
            if (avail > 40) _ = ctx.labelFit(.{ .x = r.right() - avail - 6, .y = r.y, .w = avail, .h = r.h }, it.detail, ctx.fonts.ui_small, theme.list_foreground_dim);
        }
    }
}

fn paintHover(ws: *Workspace, ctx: *const ui.Ctx) void {
    const h = &(ws.lsp_state.hover orelse return);
    if (ws.activeEditor() != h.editor) return closeHover(ws);
    const ed = h.editor;
    const anchor = if (h.at_mouse) h.mouse else caretAnchor(ed, h.offset) orelse return;
    const theme = ctx.theme;
    const m = ctx.text.metrics(ctx.fonts.mono);
    const line_h = m.line_height + 2;
    // Wrap long lines by characters.
    var lines: std.ArrayList([]const u8) = .empty;
    defer lines.deinit(ws.gpa);
    const max_chars: usize = 90;
    var it = std.mem.splitScalar(u8, h.text, '\n');
    while (it.next()) |line| {
        var rest = line;
        while (rest.len > max_chars) {
            var cut = max_chars;
            while (cut > 0 and rest[cut] & 0xC0 == 0x80) cut -= 1;
            lines.append(ws.gpa, rest[0..cut]) catch return;
            rest = rest[cut..];
        }
        lines.append(ws.gpa, rest) catch return;
        if (lines.items.len >= 24) break;
    }
    var width: f32 = 120;
    for (lines.items) |l| width = @max(width, ctx.text.measure(l, ctx.fonts.mono));
    width = @min(width + 20, ws.size.x - 20);
    const h_px = @as(f32, @floatFromInt(lines.items.len)) * line_h + 14;
    var y = anchor.y - h_px - 4;
    if (y < 4) y = anchor.y + ed.lineHeight() + 4;
    const box: Rect = .{ .x = @max(4, @min(anchor.x, ws.size.x - width - 4)), .y = y, .w = width, .h = h_px };
    ctx.shadow(box, 5, 10);
    ctx.scene.quad(box, .{ .color = theme.overlay_background, .border_color = theme.panel_border, .border_width = 1, .radius = 5 });
    ctx.scene.pushClip(box) catch return;
    defer ctx.scene.popClip();
    for (lines.items, 0..) |l, i| {
        _ = ctx.label(.{ .x = box.x + 10, .y = box.y + 7 + @as(f32, @floatFromInt(i)) * line_h, .w = box.w, .h = line_h }, l, ctx.fonts.mono, theme.foreground);
    }
}

// ---- Commands -----------------------------------------------------------------

pub fn diagnosticAt(ed: *Editor, offset: usize) ?*const buffer_mod.Diagnostic {
    var best: ?*const buffer_mod.Diagnostic = null;
    for (ed.buffer.diagnostics.items) |*d| {
        if (offset >= d.start and offset <= @max(d.end, d.start + 1)) {
            if (best == null or @intFromEnum(d.severity) < @intFromEnum(best.?.severity)) best = d;
        }
    }
    return best;
}

/// The next (previous) diagnostic after (before) the caret, wrapping
/// around; `errors_only` passes over warnings, infos and hints.
pub fn gotoDiagnostic(ws: *Workspace, forward: bool, errors_only: bool) void {
    const ed = ws.activeEditor() orelse return;
    const diags = ed.buffer.diagnostics.items;
    const head = ed.primarySelection().head;
    // The first one in the direction of travel, for wrapping around.
    var first: ?*const buffer_mod.Diagnostic = null;
    const target = for (0..diags.len) |i| {
        const d = &diags[if (forward) i else diags.len - 1 - i];
        if (errors_only and d.severity != .err) continue;
        if (first == null) first = d;
        if (if (forward) d.start > head else d.start < head) break d;
    } else first orelse {
        ws.setStatus("No {s}", .{if (errors_only) "errors" else "diagnostics"});
        return;
    };
    ed.pushSelectionHistory();
    ed.setSingleSelection(.caret(target.start));
    ed.afterCommand(true);
    ws.setStatus("{s}", .{target.message[0..@min(target.message.len, 150)]});
}

/// The active file's diagnostics in a list ("12:5  message"); picking one
/// goes there.
pub fn listDiagnostics(ws: *Workspace) void {
    const ed = ws.activeEditor() orelse return;
    const diags = ed.buffer.diagnostics.items;
    if (diags.len == 0) {
        ws.setStatus("No diagnostics", .{});
        return;
    }
    // Language servers only see files.
    const path = ed.buffer.path orelse return;
    const o = ws.openLocationPanel("Diagnostics") orelse return;
    const owned_path = o.arena().dupe(u8, path) catch return;
    for (diags) |d| {
        const p = ed.buffer.offsetToPoint(d.start);
        const line = d.message[0 .. std.mem.indexOfScalar(u8, d.message, '\n') orelse d.message.len];
        const title = std.fmt.allocPrint(o.arena(), "{d}:{d}  {s}", .{ p.row + 1, p.column + 1, line }) catch continue;
        o.addItem(.{ .title = title, .detail = severityName(d.severity), .payload = .{ .location = .{ .path = owned_path, .line = p.row, .character = p.column, .utf8 = true } } }) catch break;
    }
}

fn severityName(s: buffer_mod.Severity) []const u8 {
    return switch (s) {
        .err => "error",
        .warning => "warning",
        .info => "info",
        .hint => "hint",
    };
}

/// Mouse dwell: request hover (or show the diagnostic) after 600 ms at rest.
pub fn mouseMoved(ws: *Workspace, pos: platform.Vec2) void {
    const st = &ws.lsp_state;
    const dx = pos.x - st.rest_pos.x;
    const dy = pos.y - st.rest_pos.y;
    if (dx * dx + dy * dy > 9) {
        st.rest_pos = pos;
        st.rest_since = platform.nowMs();
        st.rest_requested = false;
        if (st.hover) |h| if (h.at_mouse) closeHover(ws);
    }
}

pub fn tick(ws: *Workspace) void {
    const st = &ws.lsp_state;
    const since = st.rest_since orelse return;
    if (st.rest_requested or platform.nowMs() - since < 600) return;
    st.rest_requested = true;
    st.rest_since = null;
    if (ws.overlay != null or ws.menuIsOpen()) return;
    if (!ws.editor_rect.contains(st.rest_pos.x, st.rest_pos.y)) return;
    const ed = ws.activeEditor() orelse return;
    if (!ed.text_rect.contains(st.rest_pos.x, st.rest_pos.y)) return;
    const offset = @import("../editor/editor_view.zig").hitTest(ed, st.rest_pos);
    if (diagnosticAt(ed, offset)) |d| {
        closeHover(ws);
        var arena = std.heap.ArenaAllocator.init(ws.gpa);
        const text = std.fmt.allocPrint(arena.allocator(), "{s}{s}{s}: {s}", .{ d.source, if (d.source.len > 0) " " else "", severityName(d.severity), d.message }) catch return;
        ws.lsp_state.hover = .{ .arena = arena, .editor = ed, .offset = offset, .text = text, .at_mouse = true, .mouse = st.rest_pos };
        ws.dirty = true;
        return;
    }
    if (ws.lsp) |lsp| _ = lsp.hover(ed.buffer, offset);
}

pub fn nextTimer(ws: *Workspace) ?i64 {
    const since = ws.lsp_state.rest_since orelse return null;
    if (ws.lsp_state.rest_requested) return null;
    return @max(1, 600 - (platform.nowMs() - since));
}
