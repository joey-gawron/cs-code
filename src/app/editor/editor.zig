//! An editor view over a Buffer: multi-cursor selections, movement and
//! editing commands with Sublime Text semantics, scrolling, and (in
//! editor_view.zig) painting and mouse handling.

const std = @import("std");
const syntax_mod = @import("../../model/syntax/syntax.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const selection_mod = @import("../../model/text/selection.zig");
const movement = @import("../../model/text/movement.zig");
const keymap = @import("../settings/keymap.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const platform = @import("../../framework/platform/platform.zig");
const view = @import("editor_view.zig");
const display_map = @import("../../model/text/display_map.zig");
const Executor = @import("../../framework/util/executor.zig").Executor;
const long_line = @import("long_line.zig");
const blame_mod = @import("../../model/git/blame.zig");
const blame_view = @import("blame_view.zig");
const quick_diff = @import("quick_diff.zig");

const Allocator = std.mem.Allocator;
pub const Buffer = buffer_mod.Buffer;
pub const Edit = buffer_mod.Edit;
pub const Selection = selection_mod.Selection;
pub const Point = buffer_mod.Point;
const Action = keymap.Action;
const Rect = scene_mod.Rect;
const Scanner = movement.Scanner;

pub const Settings = struct {
    font_family: []const u8 = "Fira Code",
    font_size: f32 = 13,
    ui_font_family: []const u8 = "sans-serif",
    ui_font_size: f32 = 13,
    line_padding_top: f32 = 1,
    line_padding_bottom: f32 = 1,
    show_minimap: bool = true,
    show_line_numbers: bool = true,
    draw_indent_guides: bool = true,
    draw_white_space: enum { none, selection, all } = .selection,
    auto_match_enabled: bool = true,
    scroll_past_end: bool = true,
    word_wrap: enum { auto, on, off } = .auto,
    /// Wrap column; 0 = the window width.
    wrap_width: u32 = 0,
    highlight_line: bool = false,
    caret_blink: bool = true,
    match_brackets: bool = true,
    rulers: []const u32 = &.{},
    /// Keep the process (buffers, language servers) alive when the window closes.
    keep_running_in_background: bool = false,
    /// Files bigger than this aren't syntax highlighted (0 = no limit).
    syntax_max_file_size_mb: u32 = 8,
    /// Dim "author, age • summary" after the caret line (git repositories).
    git_blame_inline: bool = true,
    /// Show the org agenda when cs-zig starts without files.
    org_agenda_on_startup: bool = false,
    /// Opening a folder with a README.md shows its preview.
    markdown_open_readme: bool = false,
    /// Where org capture files TODOs ("~/" is the home folder), and under
    /// which heading ("" for top level).
    org_capture_file: []const u8 = "~/org/inbox.org",
    org_capture_heading: []const u8 = "Tasks",
    /// Marking an item DONE adds "CLOSED: [stamp]" (Doom Emacs's default).
    org_log_done: bool = true,
    /// Structured org settings as their JSON ("" when unset), read by org_ui:
    /// "org_capture_templates" (an array of capture.Template),
    /// "org_agenda_custom_commands" and "org_agenda_files" (an array of paths).
    org_capture_templates_json: []const u8 = "",
    org_agenda_custom_commands_json: []const u8 = "",
    org_agenda_files_json: []const u8 = "",
    /// How long a leader key sequence waits before which-key lists the
    /// keys that can follow (0: at once).
    which_key_delay_ms: u32 = 400,
};

pub const Clipboard = struct {
    ctx: *anyopaque,
    get: *const fn (ctx: *anyopaque, gpa: Allocator) ?[]u8,
    set: *const fn (ctx: *anyopaque, text: []const u8) void,
};

/// Services shared by every editor (owned by the app).
/// What the gutter shows beside a row of a file being debugged.
pub const DebugMark = enum {
    none,
    breakpoint,
    breakpoint_disabled,
    /// The adapter did not accept it.
    breakpoint_unverified,
    /// The line the program is stopped at (the top frame).
    current,
    current_breakpoint,
    /// The line of another frame of the call stack.
    frame,
    frame_breakpoint,
};

/// The debugger's answers for the editors' gutters (debugger.zig).
pub const DebugHook = struct {
    ctx: *anyopaque,
    mark: *const fn (ctx: *anyopaque, path: []const u8, row: u32) DebugMark,
    toggle: *const fn (ctx: *anyopaque, path: []const u8, row: u32) void,
};

pub const Env = struct {
    gpa: Allocator,
    text: *text_mod.TextSystem,
    font: text_mod.FontId,
    settings: *Settings,
    clipboard: Clipboard,
    /// Whether the last copy was a whole-line copy (Sublime pastes those above the caret).
    clipboard_whole_line: ?[]u8 = null,
    /// Background work (soft-wrapping big buffers); null does it inline.
    executor: ?*Executor = null,
    /// Grammars, for code embedded in custom-highlighted buffers (org
    /// source blocks); null leaves it plain.
    registry: ?*syntax_mod.Registry = null,
    /// Breakpoint glyphs and the current line; null shows none.
    debug: ?DebugHook = null,
};

pub const Motion = enum {
    left,
    right,
    up,
    down,
    word_left,
    word_right,
    subword_left,
    subword_right,
    line_start,
    line_end,
    page_up,
    page_down,
    doc_start,
    doc_end,
};

pub const DragMode = enum { none, chars, words, lines, column, minimap, scrollbar, gutter };

pub const Mode = enum {
    full,
    /// Text inputs (find field, quick panel): no gutter/minimap, one line.
    single_line,
};

/// The background of a buffer row, or null for none.
pub const RowBackground = struct {
    ctx: *anyopaque,
    color: *const fn (ctx: *anyopaque, row: u32, theme: *const theme_mod.Theme) ?scene_mod.Color,
};

pub const Editor = struct {
    gpa: Allocator,
    env: *Env,
    buffer: *Buffer,
    mode: Mode = .full,
    /// No line numbers, minimap, indent guides or fold toggles (output views
    /// such as the Language Servers log).
    plain: bool = false,
    /// Shown dimmed when a single-line input is empty.
    placeholder: []const u8 = "",
    /// Backgrounds of whole rows under the text (the Source Control
    /// view's diff lines).
    row_background: ?RowBackground = null,

    selections: std.ArrayList(Selection) = .empty,
    /// Index of the most recently added selection (the "primary" one).
    primary: usize = 0,

    /// f64: x on a long line (a 50 MB minified file) is beyond f32 precision.
    scroll_x: f64 = 0,
    scroll_y: f32 = 0,
    /// Last laid-out geometry (logical px), set by `layout`.
    bounds: Rect = .{},
    text_rect: Rect = .{},
    gutter_rect: Rect = .{},
    minimap_rect: Rect = .{},
    scrollbar_rect: Rect = .{},

    caret_epoch: i64 = 0,
    focused: bool = true,

    drag: DragMode = .none,
    drag_origin: Selection = .caret(0),
    /// Selections kept while dragging (ctrl+drag adds to them).
    drag_base: std.ArrayList(Selection) = .empty,
    drag_selections_before: usize = 0,
    drag_grab_offset: f64 = 0,
    hover_minimap: bool = false,
    hover_gutter: bool = false,
    hover_scrollbar: bool = false,

    /// ctrl+d started from an empty caret: subsequent matches are whole-word.
    find_whole_word: bool = false,
    /// Offsets of find matches to outline (set by the find panel).
    highlights: std.ArrayList([2]usize) = .empty,
    /// Folds and soft wrap: buffer rows → display rows.
    display: display_map.DisplayMap = .{},
    /// Per-view word wrap toggle (null = follow the "word_wrap" setting).
    word_wrap_override: ?bool = null,
    /// The text at the top of the view while a background wrap lands.
    reflow_anchor: ?struct { offset: usize, dy: f32 } = null,
    /// Row whose gutter fold marker is hovered.
    hover_fold_row: ?u32 = null,

    /// Git blame of the buffer (owned by the workspace, see git_ui.zig).
    blame: ?*blame_mod.Blame = null,
    /// The change markers of the buffer's file (owned by the workspace, see git_ui.zig).
    quick_diff: ?*quick_diff.QuickDiff = null,
    /// This view shows the blame column in its gutter.
    show_blame: bool = false,
    inline_blame: blame_view.InlineState = .{},
    /// Where the caret line's inline blame was drawn (for hovering it).
    inline_blame_rect: ?Rect = null,
    /// Blame column row under the mouse, and a click on it for the workspace.
    blame_hover_row: ?u32 = null,
    blame_clicked: ?u32 = null,

    /// Selection history for soft undo (ctrl+u).
    sel_history: std.ArrayList([]Selection) = .empty,
    sel_redo: std.ArrayList([]Selection) = .empty,

    /// Set while this editor applies an edit (its own selections are explicit).
    applying_edit: bool = false,
    /// Overrides the caret shape (vim normal mode draws a block).
    block_caret: bool = false,
    /// Drawn as an underline when `block_caret` is set (vim's replace and operator-pending modes).
    underline_caret: bool = false,
    /// When non-empty, carets are drawn here instead of at selection heads
    /// (vim visual mode: the cursor sits on the last selected char).
    caret_override: std.ArrayList(usize) = .empty,
    /// Input-method composition shown at the caret (not part of the buffer).
    preedit: std.ArrayList(u8) = .empty,

    // Scratch.
    line_buf: std.ArrayList(u8) = .empty,
    display_buf: std.ArrayList(u8) = .empty,
    display_map: std.ArrayList(u32) = .empty,
    /// Position indexes of long lines (see long_line.zig).
    long_lines: long_line.Cache = .{},

    pub fn create(gpa: Allocator, env: *Env, buffer: *Buffer) !*Editor {
        const self = try gpa.create(Editor);
        self.* = .{ .gpa = gpa, .env = env, .buffer = buffer };
        try self.selections.append(gpa, .caret(0));
        try buffer.addListener(.{ .ctx = self, .onChange = onBufferChange });
        self.caret_epoch = platform.nowMs();
        self.display.executor = env.executor;
        self.display.reflow = .{ .ctx = self, .before = reflowBefore, .after = reflowAfter };
        return self;
    }

    pub fn destroy(self: *Editor) void {
        self.buffer.removeListener(self);
        self.selections.deinit(self.gpa);
        self.drag_base.deinit(self.gpa);
        self.caret_override.deinit(self.gpa);
        self.preedit.deinit(self.gpa);
        self.display.deinit(self.gpa);
        self.highlights.deinit(self.gpa);
        for (self.sel_history.items) |s| self.gpa.free(s);
        for (self.sel_redo.items) |s| self.gpa.free(s);
        self.sel_history.deinit(self.gpa);
        self.sel_redo.deinit(self.gpa);
        self.line_buf.deinit(self.gpa);
        self.display_buf.deinit(self.gpa);
        self.display_map.deinit(self.gpa);
        self.long_lines.deinit(self.gpa);
        self.gpa.destroy(self);
    }

    pub const paint = view.paint;
    pub const layout = view.layout;
    pub const onMouseDown = view.onMouseDown;
    pub const onMouseMove = view.onMouseMove;
    pub const onMouseUp = view.onMouseUp;
    pub const onScroll = view.onScroll;
    pub const cursorShapeAt = view.cursorShapeAt;
    pub const screenX = view.screenX;
    pub const textX = view.textX;

    fn onBufferChange(ctx: *anyopaque, buffer: *Buffer, change: buffer_mod.Change) void {
        const self: *Editor = @ptrCast(@alignCast(ctx));
        self.display.applyChange(self.gpa, change);
        self.long_lines.applyChange(self.gpa, buffer, change);
        if (self.blame) |b| b.onChange(buffer, change);
        if (self.applying_edit) return;
        for (self.selections.items) |*sel| {
            sel.anchor = selection_mod.transformOffset(sel.anchor, change.start, change.old_len, change.new_len, false);
            sel.head = selection_mod.transformOffset(sel.head, change.start, change.old_len, change.new_len, true);
            sel.goal_x = null;
        }
        self.highlights.clearRetainingCapacity();
    }

    // ---- Metrics and coordinates ------------------------------------------

    pub fn metrics(self: *const Editor) text_mod.Metrics {
        return self.env.text.metrics(self.env.font);
    }

    pub fn lineHeight(self: *const Editor) f32 {
        if (self.mode == .single_line) return self.metrics().line_height + 4;
        const s = self.env.settings;
        return self.metrics().line_height + s.line_padding_top + s.line_padding_bottom;
    }

    pub fn charWidth(self: *const Editor) f32 {
        return self.metrics().advance;
    }

    pub fn tabSize(self: *const Editor) u32 {
        return self.buffer.tab_size;
    }

    pub fn indentUnit(self: *const Editor, out: []u8) []const u8 {
        if (!self.buffer.translate_tabs_to_spaces) return "\t";
        const n = @min(out.len, self.buffer.tab_size);
        @memset(out[0..n], ' ');
        return out[0..n];
    }

    pub const DisplayLine = struct {
        text: []const u8,
        /// Display byte index for each buffer column (len = line len + 1).
        map: []const u32,
    };

    /// The line as displayed: tabs expanded to the next tab stop.
    pub fn displayLine(self: *Editor, row: u32) DisplayLine {
        const line = self.buffer.lineText(row, &self.line_buf) catch "";
        self.display_buf.clearRetainingCapacity();
        self.display_map.clearRetainingCapacity();
        const tab: usize = self.tabSize();
        var col: usize = 0; // display column in chars
        var i: usize = 0;
        while (i < line.len) {
            const b = line[i];
            self.display_map.append(self.gpa, @intCast(self.display_buf.items.len)) catch {};
            if (b == '\t') {
                const spaces = tab - (col % tab);
                self.display_buf.appendNTimes(self.gpa, ' ', spaces) catch {};
                col += spaces;
                i += 1;
            } else {
                self.display_buf.append(self.gpa, b) catch {};
                if (b & 0xC0 != 0x80) col += 1;
                i += 1;
            }
        }
        self.display_map.append(self.gpa, @intCast(self.display_buf.items.len)) catch {};
        return .{ .text = self.display_buf.items, .map = self.display_map.items };
    }

    pub fn lineLayout(self: *Editor, row: u32) struct { *const text_mod.LineLayout, DisplayLine } {
        const dl = self.displayLine(row);
        return .{ self.env.text.layoutLine(dl.text, self.env.font), dl };
    }

    /// Positions on `row` when it's a long line: those aren't laid out whole
    /// (see long_line.zig). Null for ordinary lines.
    pub fn longLine(self: *Editor, row: u32) ?long_line.View {
        if (self.buffer.lineLen(row) <= long_line.threshold) return null;
        return self.long_lines.view(self.gpa, self.env.text, self.env.font, self.buffer, row);
    }

    /// Width of line `row` (logical px).
    pub fn lineWidth(self: *Editor, row: u32) f64 {
        if (self.longLine(row)) |ll| return ll.width();
        const l, _ = self.lineLayout(row);
        return l.width;
    }

    /// Display column of `offset` (tabs expanded), as the status bar shows it.
    pub fn displayColumn(self: *Editor, offset: usize) usize {
        const p = self.buffer.offsetToPoint(offset);
        if (self.longLine(p.row)) |ll| return @intCast(ll.posAt(p.column).col);
        _ = self.displayLine(p.row);
        return self.display_map.items[@min(p.column, self.display_map.items.len - 1)];
    }

    pub const Segment = display_map.Segment;

    pub fn segmentAt(self: *Editor, display_row: u32) Segment {
        return self.display.segmentAt(self.gpa, self.buffer, display_row);
    }

    pub fn segmentForOffset(self: *Editor, offset: usize) Segment {
        return self.display.segmentForOffset(self.gpa, self.buffer, offset);
    }

    pub fn displayRowOfOffset(self: *Editor, offset: usize) u32 {
        return self.display.displayRowOfOffset(self.gpa, self.buffer, offset);
    }

    /// Indentation drawn before a wrapped continuation (logical px).
    pub fn segmentIndent(self: *const Editor, seg: Segment) f32 {
        return @as(f32, @floatFromInt(seg.indent_cols)) * self.charWidth();
    }

    /// x of `offset` within the segment `seg` of its line.
    pub fn xInSegment(self: *Editor, seg: Segment, offset: usize) f64 {
        const ls = self.buffer.lineStart(seg.row);
        if (self.longLine(seg.row)) |ll| {
            const x0 = ll.xAt(seg.start - ls);
            return self.segmentIndent(seg) + ll.xAt(@min(offset -| ls, ll.src.len)) - x0;
        }
        const l, const dl = self.lineLayout(seg.row);
        const x0 = l.xForIndex(dl.map[@min(seg.start - ls, dl.map.len - 1)]);
        const col = @min(offset -| ls, dl.map.len - 1);
        return self.segmentIndent(seg) + l.xForIndex(dl.map[col]) - x0;
    }

    /// x (logical, relative to the text origin) of a buffer offset on its display row.
    pub fn xForOffset(self: *Editor, offset: usize) f64 {
        return self.xInSegment(self.segmentForOffset(offset), offset);
    }

    /// Buffer offset on display row `d` nearest to x (relative to the text origin).
    pub fn offsetForDisplayRow(self: *Editor, d: u32, x: f64) usize {
        const seg = self.segmentAt(d);
        const ls = self.buffer.lineStart(seg.row);
        if (self.longLine(seg.row)) |ll| {
            const x0 = ll.xAt(seg.start - ls);
            var off = std.math.clamp(ls + ll.offsetAtX(x - self.segmentIndent(seg) + x0), seg.start, seg.end);
            // Past the end of a wrapped (non-final) segment: stay on this visual line.
            if (off == seg.end and seg.end < ls + ll.src.len and off > seg.start) {
                var s = Scanner.init(&self.buffer.rope);
                off = s.prevBoundary(off);
            }
            return off;
        }
        const l, const dl = self.lineLayout(seg.row);
        const x0 = l.xForIndex(dl.map[@min(seg.start - ls, dl.map.len - 1)]);
        const target: f32 = @floatCast(x - self.segmentIndent(seg) + x0);
        const di = l.indexForX(target);
        // Map display index back to the nearest buffer column.
        var best: usize = 0;
        var best_dist: f32 = std.math.inf(f32);
        // Binary search the monotonic map, then check neighbours.
        var lo: usize = 0;
        var hi: usize = dl.map.len - 1;
        while (lo < hi) {
            const mid = (lo + hi + 1) / 2;
            if (dl.map[mid] <= di) lo = mid else hi = mid - 1;
        }
        for ([_]usize{ lo, @min(lo + 1, dl.map.len - 1) }) |col| {
            const cx = l.xForIndex(dl.map[col]);
            const dist = @abs(cx - target);
            if (dist < best_dist) {
                best_dist = dist;
                best = col;
            }
        }
        var s = Scanner.init(&self.buffer.rope);
        var off = std.math.clamp(ls + best, seg.start, seg.end);
        // Past the end of a wrapped (non-final) segment: stay on this visual line.
        if (off == seg.end and seg.end < self.buffer.lineEnd(seg.row) and off > seg.start) off = s.prevBoundary(off);
        // Snap to a char boundary.
        while (off > ls and off < s.len() and s.byte(off) & 0xC0 == 0x80) off -= 1;
        return off;
    }

    /// Buffer offset on the first display row of `row` nearest to x.
    pub fn offsetForX(self: *Editor, row: u32, x: f64) usize {
        return self.offsetForDisplayRow(self.displayRow(row), x);
    }

    pub fn wrapEnabled(self: *const Editor) bool {
        if (self.mode == .single_line) return false;
        if (self.word_wrap_override) |o| return o;
        return switch (self.env.settings.word_wrap) {
            .on => true,
            .off => false,
            // Sublime's "auto": wrap prose, not code. Huge files (logs, dumps)
            // open unwrapped: they would wrap in the background and reflow
            // after opening. View > Word Wrap still wraps them.
            .auto => blk: {
                if (self.buffer.len() > display_map.auto_wrap_bytes) break :blk false;
                const name = self.buffer.language.name;
                break :blk std.mem.eql(u8, name, "Plain Text") or std.mem.eql(u8, name, "Markdown") or std.mem.eql(u8, name, "Org");
            },
        };
    }

    /// A background wrap is about to change the display rows: note the text
    /// at the top of the view...
    fn reflowBefore(ctx: *anyopaque) void {
        const self: *Editor = @ptrCast(@alignCast(ctx));
        const lh = self.lineHeight();
        if (!(lh > 0)) return;
        const d: u32 = @intFromFloat(@max(0, @floor(self.scroll_y / lh)));
        const seg = self.segmentAt(d);
        self.reflow_anchor = .{ .offset = seg.start, .dy = self.scroll_y - @as(f32, @floatFromInt(d)) * lh };
    }

    /// ...and keep it there.
    fn reflowAfter(ctx: *anyopaque) void {
        const self: *Editor = @ptrCast(@alignCast(ctx));
        const anchor = self.reflow_anchor orelse return;
        self.reflow_anchor = null;
        self.scroll_y = @as(f32, @floatFromInt(self.displayRowOfOffset(anchor.offset))) * self.lineHeight() + anchor.dy;
        self.clampScroll();
    }

    /// Whole buffer contents (caller frees) — used by single-line inputs.
    pub fn contents(self: *Editor, gpa: Allocator) ![]u8 {
        return self.buffer.rope.toOwnedSlice(gpa);
    }

    /// Replaces the whole buffer (undoable), selecting the new text.
    pub fn setText(self: *Editor, new_text: []const u8, select: bool) void {
        var reps = [_]Replacement{.{
            .start = 0,
            .end = self.buffer.len(),
            .text = new_text,
            .sel_anchor = if (select) 0 else new_text.len,
            .sel_head = new_text.len,
        }};
        self.applyReplacements(&reps, .other);
    }

    /// Replaces the contents with `new_text` as one undoable edit that spans
    /// only the changed middle (common prefix and suffix are kept), so carets
    /// outside it stay where they were. Used when reloading from disk.
    pub fn replaceContentsMinimal(self: *Editor, new_text: []const u8) void {
        const old = self.buffer.rope.toOwnedSlice(self.gpa) catch return;
        defer self.gpa.free(old);
        const common = @min(old.len, new_text.len);
        var pre: usize = 0;
        while (pre < common and old[pre] == new_text[pre]) pre += 1;
        var suf: usize = 0;
        while (suf < common - pre and old[old.len - 1 - suf] == new_text[new_text.len - 1 - suf]) suf += 1;
        // Keep the edit on UTF-8 boundaries (both texts agree inside prefix/suffix).
        while (pre > 0 and pre < old.len and old[pre] & 0xC0 == 0x80) pre -= 1;
        while (suf > 0 and old[old.len - suf] & 0xC0 == 0x80) suf -= 1;
        if (pre == old.len and pre == new_text.len) return;
        const start = pre;
        const end = old.len - suf;
        const ins = new_text[pre .. new_text.len - suf];
        const sels = self.gpa.dupe(Selection, self.selections.items) catch return;
        defer self.gpa.free(sels);
        for (sels) |*sel| {
            sel.anchor = selection_mod.transformOffset(sel.anchor, start, end - start, ins.len, false);
            sel.head = selection_mod.transformOffset(sel.head, start, end - start, ins.len, true);
            sel.goal_x = null;
        }
        const scroll = self.scroll_y;
        self.commit(&.{.{ .start = start, .end = end, .text = ins }}, sels, .other);
        self.scroll_y = scroll;
        self.clampScroll();
    }

    // ---- Selections -------------------------------------------------------

    pub fn primarySelection(self: *const Editor) Selection {
        return self.selections.items[@min(self.primary, self.selections.items.len - 1)];
    }

    pub fn setSelections(self: *Editor, sels: []const Selection) void {
        self.selections.clearRetainingCapacity();
        self.selections.appendSlice(self.gpa, sels) catch {};
        if (self.selections.items.len == 0) self.selections.append(self.gpa, .caret(0)) catch {};
        self.primary = self.selections.items.len - 1;
        self.clampSelections();
        self.normalize();
    }

    pub fn setSingleSelection(self: *Editor, sel: Selection) void {
        self.setSelections(&.{sel});
    }

    fn clampSelections(self: *Editor) void {
        const total = self.buffer.len();
        for (self.selections.items) |*sel| {
            sel.anchor = @min(sel.anchor, total);
            sel.head = @min(sel.head, total);
        }
    }

    pub fn normalize(self: *Editor) void {
        const head = self.primarySelection().head;
        self.primary = selection_mod.normalize(&self.selections, head);
    }

    /// Records the selection state for soft undo and resets the caret blink.
    pub fn pushSelectionHistory(self: *Editor) void {
        const snapshot = self.gpa.dupe(Selection, self.selections.items) catch return;
        if (self.sel_history.items.len > 0) {
            const last = self.sel_history.items[self.sel_history.items.len - 1];
            if (selectionsEqual(last, snapshot)) {
                self.gpa.free(snapshot);
                return;
            }
        }
        self.sel_history.append(self.gpa, snapshot) catch self.gpa.free(snapshot);
        if (self.sel_history.items.len > 200) self.gpa.free(self.sel_history.orderedRemove(0));
    }

    pub fn setPreedit(self: *Editor, text: []const u8) void {
        self.preedit.clearRetainingCapacity();
        self.preedit.appendSlice(self.gpa, text) catch {};
        self.touchCaret();
    }

    pub fn touchCaret(self: *Editor) void {
        self.caret_epoch = platform.nowMs();
    }

    // ---- Scrolling --------------------------------------------------------

    pub fn maxScrollY(self: *Editor) f32 {
        if (self.mode == .single_line) return 0;
        const lh = self.lineHeight();
        const lines: f32 = @floatFromInt(self.displayRowCount());
        const content = if (self.env.settings.scroll_past_end) (lines - 1) * lh else lines * lh - self.text_rect.h;
        return @max(0, content);
    }

    pub fn clampScroll(self: *Editor) void {
        self.scroll_y = std.math.clamp(self.scroll_y, 0, self.maxScrollY());
        self.scroll_x = @max(0, self.scroll_x);
    }

    /// Scrolls so the primary caret is visible (centered if `center`, or if far away).
    pub fn ensureCaretVisible(self: *Editor, center: bool) void {
        if (self.text_rect.h <= 0) return;
        const lh = self.lineHeight();
        const head = self.primarySelection().head;
        const y = @as(f32, @floatFromInt(self.displayRowOfOffset(head))) * lh;
        const margin = lh * 2;
        const view_h = self.text_rect.h;
        const far = y < self.scroll_y - view_h or y > self.scroll_y + 2 * view_h;
        if (center or far) {
            self.scroll_y = y - view_h / 2 + lh / 2;
        } else if (y < self.scroll_y + margin) {
            self.scroll_y = y - margin;
        } else if (y + lh > self.scroll_y + view_h - margin) {
            self.scroll_y = y + lh - view_h + margin;
        }
        if (self.wrapEnabled()) {
            self.scroll_x = 0;
            self.clampScroll();
            return;
        }
        const x = self.xForOffset(head);
        const view_w = self.text_rect.w;
        if (x < self.scroll_x) {
            self.scroll_x = @max(0, x - view_w / 3);
        } else if (x + self.charWidth() * 2 > self.scroll_x + view_w) {
            self.scroll_x = x - view_w + self.charWidth() * 8;
        }
        self.clampScroll();
    }

    pub fn visibleRows(self: *const Editor) u32 {
        return @intFromFloat(@max(1, @floor(self.text_rect.h / self.lineHeight())));
    }

    pub fn displayRow(self: *Editor, row: u32) u32 {
        return self.display.displayRow(self.gpa, self.buffer, row);
    }

    pub fn bufferRow(self: *Editor, display_row: u32) u32 {
        return self.segmentAt(display_row).row;
    }

    pub fn displayRowCount(self: *Editor) u32 {
        return self.display.displayRowCount(self.gpa, self.buffer);
    }

    pub fn isRowHidden(self: *Editor, row: u32) bool {
        return self.display.folds.isHidden(self.gpa, self.buffer, row);
    }

    pub fn nextVisibleRow(self: *Editor, row: u32) u32 {
        return self.display.folds.nextVisible(self.gpa, self.buffer, row);
    }

    /// Opens folds that hide any caret.
    pub fn revealCarets(self: *Editor) void {
        if (self.display.folds.isEmpty()) return;
        for (self.selections.items) |sel| {
            if (self.isRowHidden(self.buffer.offsetToPoint(sel.head).row)) _ = self.display.folds.unfoldContaining(sel.head);
        }
    }

    // ---- Movement ---------------------------------------------------------

    fn motionTarget(self: *Editor, sel: Selection, motion: Motion) struct { usize, ?f64 } {
        var s = Scanner.init(&self.buffer.rope);
        const head = sel.head;
        const total = self.buffer.len();
        switch (motion) {
            .left => return .{ s.prevBoundary(head), null },
            .right => return .{ s.nextBoundary(head), null },
            .word_left => return .{ movement.prevWordStart(&s, head), null },
            .word_right => return .{ movement.nextWordEnd(&s, head), null },
            .subword_left => return .{ movement.prevSubwordStart(&s, head), null },
            .subword_right => return .{ movement.nextSubwordEnd(&s, head), null },
            .line_start => {
                const p = self.buffer.offsetToPoint(head);
                const start = self.buffer.lineStart(p.row);
                const first = self.firstNonWhitespace(p.row);
                return .{ if (head == first) start else first, null };
            },
            .line_end => {
                const p = self.buffer.offsetToPoint(head);
                return .{ self.buffer.lineEnd(p.row), null };
            },
            .doc_start => return .{ 0, null },
            .doc_end => return .{ total, null },
            .up, .down, .page_up, .page_down => {
                const goal = sel.goal_x orelse self.xForOffset(head);
                const page: u32 = @max(1, self.visibleRows() -| 1);
                const delta: u32 = if (motion == .up or motion == .down) 1 else page;
                // Vertical motion moves over display rows: folds are skipped and
                // wrapped lines are walked one visual line at a time.
                const d = self.displayRowOfOffset(head);
                const last_d = self.displayRowCount() - 1;
                if (motion == .up or motion == .page_up) {
                    if (d == 0) return .{ 0, goal };
                    return .{ self.offsetForDisplayRow(d -| delta, goal), goal };
                }
                if (d >= last_d) return .{ total, goal };
                return .{ self.offsetForDisplayRow(@min(last_d, d + delta), goal), goal };
            },
        }
    }

    pub fn firstNonWhitespace(self: *Editor, row: u32) usize {
        const start = self.buffer.lineStart(row);
        const end = self.buffer.lineEnd(row);
        var s = Scanner.init(&self.buffer.rope);
        var o = start;
        while (o < end) : (o += 1) {
            const b = s.byte(o);
            if (b != ' ' and b != '\t') break;
        }
        return o;
    }

    pub fn moveSelections(self: *Editor, motion: Motion, extend: bool) void {
        self.pushSelectionHistory();
        for (self.selections.items) |*sel| {
            if (!extend and !sel.isEmpty() and (motion == .left or motion == .right)) {
                sel.* = .caret(if (motion == .left) sel.start() else sel.end());
                continue;
            }
            const target, const goal = self.motionTarget(sel.*, motion);
            sel.head = target;
            if (!extend) sel.anchor = target;
            sel.goal_x = goal;
        }
        if (motion == .page_up or motion == .page_down) {
            const lh = self.lineHeight();
            const rows: f32 = @floatFromInt(@max(1, self.visibleRows() -| 1));
            self.scroll_y += if (motion == .page_up) -rows * lh else rows * lh;
            self.clampScroll();
        }
        self.normalize();
        self.afterCommand(false);
    }

    /// Common epilogue: scroll the caret into view and restart blinking.
    pub fn afterCommand(self: *Editor, center: bool) void {
        self.revealCarets();
        self.buffer.breakUndoGroup();
        self.touchCaret();
        self.ensureCaretVisible(center);
    }

    // ---- Editing primitives -----------------------------------------------

    pub const Replacement = struct {
        start: usize,
        end: usize,
        text: []const u8,
        /// Resulting selection, relative to `start` in the new text.
        sel_anchor: usize,
        sel_head: usize,
    };

    /// Applies one replacement per resulting selection (sorted, non-overlapping).
    pub fn applyReplacements(self: *Editor, reps: []Replacement, kind: buffer_mod.EditKind) void {
        if (reps.len == 0) return;
        std.mem.sort(Replacement, reps, {}, struct {
            fn lt(_: void, a: Replacement, b: Replacement) bool {
                return a.start < b.start;
            }
        }.lt);
        const edits = self.gpa.alloc(Edit, reps.len) catch return;
        defer self.gpa.free(edits);
        const new_sels = self.gpa.alloc(Selection, reps.len) catch return;
        defer self.gpa.free(new_sels);
        var delta: isize = 0;
        var n_edits: usize = 0;
        for (reps, 0..) |r, i| {
            if (r.start != r.end or r.text.len > 0) {
                edits[n_edits] = .{ .start = r.start, .end = r.end, .text = r.text };
                n_edits += 1;
            }
            const new_start: usize = @intCast(@as(isize, @intCast(r.start)) + delta);
            new_sels[i] = .{ .anchor = new_start + r.sel_anchor, .head = new_start + r.sel_head };
            delta += @as(isize, @intCast(r.text.len)) - @as(isize, @intCast(r.end - r.start));
        }
        self.commit(edits[0..n_edits], new_sels, kind);
    }

    /// Low-level: applies `edits` and sets `new_sels` (post-edit coordinates).
    pub fn commit(self: *Editor, edits: []const Edit, new_sels: []const Selection, kind: buffer_mod.EditKind) void {
        if (self.buffer.read_only and edits.len > 0) return;
        self.pushSelectionHistory();
        self.applying_edit = true;
        const before = self.gpa.dupe(Selection, self.selections.items) catch &.{};
        defer self.gpa.free(before);
        if (edits.len > 0) {
            self.buffer.edit(edits, kind, before, new_sels) catch |err| std.log.err("edit failed: {t}", .{err});
        }
        self.applying_edit = false;
        self.setSelections(new_sels);
        self.highlights.clearRetainingCapacity();
        self.revealCarets();
        self.touchCaret();
        self.ensureCaretVisible(false);
    }

    /// Applies edits that don't map 1:1 to selections; selections are moved
    /// through the edits.
    pub fn commitTransform(self: *Editor, edits: []Edit, kind: buffer_mod.EditKind) void {
        std.mem.sort(Edit, edits, {}, struct {
            fn lt(_: void, a: Edit, b: Edit) bool {
                return a.start < b.start;
            }
        }.lt);
        const new_sels = self.gpa.alloc(Selection, self.selections.items.len) catch return;
        defer self.gpa.free(new_sels);
        for (self.selections.items, new_sels) |sel, *out| {
            const a_right = sel.anchor > sel.head;
            out.* = .{
                .anchor = transformThrough(edits, sel.anchor, a_right),
                .head = transformThrough(edits, sel.head, !a_right or sel.isEmpty()),
            };
        }
        self.commit(edits, new_sels, kind);
    }

    /// Replaces every selection with `text` (typing / paste).
    pub fn insertText(self: *Editor, text: []const u8) void {
        if (text.len == 0) return;
        if (self.env.settings.auto_match_enabled and text.len == 1) {
            if (self.tryAutoPair(text[0])) return;
        }
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            r.* = .{ .start = sel.start(), .end = sel.end(), .text = text, .sel_anchor = text.len, .sel_head = text.len };
        }
        self.applyReplacements(reps, .typing);
    }

    fn tryAutoPair(self: *Editor, ch: u8) bool {
        const pairs = [_][2]u8{ .{ '(', ')' }, .{ '[', ']' }, .{ '{', '}' }, .{ '"', '"' }, .{ '\'', '\'' }, .{ '`', '`' } };
        var s = Scanner.init(&self.buffer.rope);
        var close: ?u8 = null;
        for (pairs) |p| if (p[0] == ch) {
            close = p[1];
        };
        const is_closer = ch == ')' or ch == ']' or ch == '}' or ch == '"' or ch == '\'' or ch == '`';

        // Typing a closer right before the same closer just steps over it.
        if (is_closer) {
            var all_over = true;
            for (self.selections.items) |sel| {
                if (!sel.isEmpty() or s.charAfter(sel.head) != ch) {
                    all_over = false;
                    break;
                }
            }
            if (all_over) {
                self.moveSelections(.right, false);
                return true;
            }
        }
        const closer = close orelse return false;
        const is_quote = ch == closer;

        var tmp: [2]u8 = .{ ch, closer };
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return false;
        defer self.gpa.free(reps);
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        for (self.selections.items, reps) |sel, *r| {
            if (!sel.isEmpty()) {
                // Wrap the selection, keeping it selected.
                const inner = self.buffer.textRange(arena.allocator(), sel.start(), sel.end()) catch return false;
                const wrapped = std.mem.concat(arena.allocator(), u8, &.{ &.{ch}, inner, &.{closer} }) catch return false;
                r.* = .{ .start = sel.start(), .end = sel.end(), .text = wrapped, .sel_anchor = 1, .sel_head = 1 + inner.len };
                continue;
            }
            const next = s.charAfter(sel.head);
            const prev = s.charBefore(sel.head);
            var ok = next == null or next.? == ' ' or next.? == '\t' or next.? == '\n' or next.? == ')' or next.? == ']' or next.? == '}' or next.? == ',' or next.? == ';';
            if (is_quote) {
                if (prev) |pc| if (movement.isWordChar(pc) or pc == ch) {
                    ok = false;
                };
            }
            if (!ok) return false;
            r.* = .{ .start = sel.start(), .end = sel.end(), .text = &tmp, .sel_anchor = 1, .sel_head = 1 };
        }
        self.applyReplacements(reps, .typing);
        return true;
    }

    pub fn insertNewline(self: *Editor) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var s = Scanner.init(&self.buffer.rope);
        var unit_buf: [16]u8 = undefined;
        const unit = self.indentUnit(&unit_buf);
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            const p = self.buffer.offsetToPoint(sel.start());
            const line_start = self.buffer.lineStart(p.row);
            // Leading whitespace of the current line, up to the caret.
            var ws_end = line_start;
            while (ws_end < sel.start()) : (ws_end += 1) {
                const b = s.byte(ws_end);
                if (b != ' ' and b != '\t') break;
            }
            const indent = self.buffer.textRange(alloc, line_start, ws_end) catch "";
            const prev = s.charBefore(sel.start());
            const next = s.charAfter(sel.end());
            const opens = if (prev) |pc| movement.isOpenBracket(pc) or
                (pc == ':' and self.buffer.language.indent_after_colon) else false;
            if (opens and next != null and prev != null and movement.isOpenBracket(prev.?) and next.? == movement.closingFor(prev.?)) {
                const text = std.mem.concat(alloc, u8, &.{ "\n", indent, unit, "\n", indent }) catch return;
                const caret = 1 + indent.len + unit.len;
                r.* = .{ .start = sel.start(), .end = sel.end(), .text = text, .sel_anchor = caret, .sel_head = caret };
            } else {
                const text = std.mem.concat(alloc, u8, &.{ "\n", indent, if (opens) unit else "" }) catch return;
                r.* = .{ .start = sel.start(), .end = sel.end(), .text = text, .sel_anchor = text.len, .sel_head = text.len };
            }
        }
        self.applyReplacements(reps, .other);
    }

    pub fn deleteBackward(self: *Editor) void {
        var s = Scanner.init(&self.buffer.rope);
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            if (!sel.isEmpty()) {
                r.* = .{ .start = sel.start(), .end = sel.end(), .text = "", .sel_anchor = 0, .sel_head = 0 };
                continue;
            }
            const head = sel.head;
            var start = s.prevBoundary(head);
            var end = head;
            if (head > 0 and self.env.settings.auto_match_enabled) {
                const prev = s.charAt(start);
                const next = s.charAfter(head);
                const pairs = [_][2]u21{ .{ '(', ')' }, .{ '[', ']' }, .{ '{', '}' }, .{ '"', '"' }, .{ '\'', '\'' }, .{ '`', '`' } };
                for (pairs) |p| if (prev == p[0] and next == p[1]) {
                    end = head + 1;
                };
            }
            // Soft tabs: in leading whitespace, delete back to the previous tab stop.
            if (self.buffer.translate_tabs_to_spaces and end == head and head > 0) {
                const p = self.buffer.offsetToPoint(head);
                const line_start = self.buffer.lineStart(p.row);
                var only_spaces = p.column > 0;
                var o = line_start;
                while (o < head) : (o += 1) if (s.byte(o) != ' ') {
                    only_spaces = false;
                    break;
                };
                if (only_spaces) {
                    const tab = self.tabSize();
                    const remove = ((p.column - 1) % tab) + 1;
                    start = head - remove;
                }
            }
            r.* = .{ .start = start, .end = end, .text = "", .sel_anchor = 0, .sel_head = 0 };
        }
        self.applyReplacements(reps, .deleting);
    }

    pub fn deleteForward(self: *Editor) void {
        var s = Scanner.init(&self.buffer.rope);
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            const end = if (sel.isEmpty()) s.nextBoundary(sel.head) else sel.end();
            r.* = .{ .start = sel.start(), .end = end, .text = "", .sel_anchor = 0, .sel_head = 0 };
        }
        self.applyReplacements(reps, .deleting);
    }

    /// Deletes from each caret to the target of `motion` (or the selection).
    pub fn deleteMotion(self: *Editor, motion: Motion) void {
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            if (!sel.isEmpty()) {
                r.* = .{ .start = sel.start(), .end = sel.end(), .text = "", .sel_anchor = 0, .sel_head = 0 };
                continue;
            }
            var target, _ = self.motionTarget(sel, motion);
            if (motion == .line_start) target = self.buffer.lineStart(self.buffer.offsetToPoint(sel.head).row);
            r.* = .{ .start = @min(target, sel.head), .end = @max(target, sel.head), .text = "", .sel_anchor = 0, .sel_head = 0 };
        }
        self.applyReplacements(reps, .other);
    }

    /// Replaces each selection with the result of `f(text)`, keeping it selected.
    pub fn transformSelections(self: *Editor, f: *const fn (Allocator, []const u8) anyerror![]u8) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        // Empty selections act on the word under the caret, like Sublime.
        var s = Scanner.init(&self.buffer.rope);
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            var start = sel.start();
            var end = sel.end();
            if (sel.isEmpty()) {
                if (movement.wordAt(&s, sel.head)) |w| {
                    start = w[0];
                    end = w[1];
                }
            }
            const old = self.buffer.textRange(alloc, start, end) catch return;
            const new = f(alloc, old) catch return;
            const rev = sel.reversed();
            r.* = .{ .start = start, .end = end, .text = new, .sel_anchor = if (rev) new.len else 0, .sel_head = if (rev) 0 else new.len };
            if (sel.isEmpty()) {
                const off = sel.head - start;
                r.sel_anchor = off;
                r.sel_head = off;
            }
        }
        self.applyReplacements(reps, .other);
    }

    // ---- Line helpers -----------------------------------------------------

    /// Sorted, de-duplicated rows touched by the selections. A selection
    /// ending at column 0 of a line doesn't include that line.
    pub fn selectedRows(self: *Editor, gpa: Allocator) ![]u32 {
        var rows: std.ArrayList(u32) = .empty;
        for (self.selections.items) |sel| {
            const a = self.buffer.offsetToPoint(sel.start());
            var b = self.buffer.offsetToPoint(sel.end());
            if (!sel.isEmpty() and b.column == 0 and b.row > a.row) b.row -= 1;
            var r = a.row;
            while (r <= b.row) : (r += 1) {
                if (rows.items.len == 0 or rows.items[rows.items.len - 1] < r) try rows.append(gpa, r);
            }
        }
        std.mem.sort(u32, rows.items, {}, std.sort.asc(u32));
        var out: usize = 0;
        for (rows.items) |r| {
            if (out == 0 or rows.items[out - 1] != r) {
                rows.items[out] = r;
                out += 1;
            }
        }
        rows.shrinkRetainingCapacity(out);
        return rows.toOwnedSlice(gpa);
    }

    /// Contiguous row blocks [first, last] covered by the selections.
    fn rowBlocks(self: *Editor, gpa: Allocator) ![][2]u32 {
        const rows = try self.selectedRows(gpa);
        defer gpa.free(rows);
        var blocks: std.ArrayList([2]u32) = .empty;
        for (rows) |r| {
            if (blocks.items.len > 0 and blocks.items[blocks.items.len - 1][1] + 1 == r) {
                blocks.items[blocks.items.len - 1][1] = r;
            } else try blocks.append(gpa, .{ r, r });
        }
        return blocks.toOwnedSlice(gpa);
    }

    pub fn lineIndentWidth(self: *Editor, row: u32) ?u32 {
        const start = self.buffer.lineStart(row);
        const end = self.buffer.lineEnd(row);
        var s = Scanner.init(&self.buffer.rope);
        var width: u32 = 0;
        var o = start;
        while (o < end) : (o += 1) {
            switch (s.byte(o)) {
                ' ' => width += 1,
                '\t' => width += self.tabSize() - (width % self.tabSize()),
                else => return width,
            }
        }
        return null; // blank line
    }

    pub fn indentLines(self: *Editor) void {
        const rows = self.selectedRows(self.gpa) catch return;
        defer self.gpa.free(rows);
        var unit_buf: [16]u8 = undefined;
        const unit = self.indentUnit(&unit_buf);
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        for (rows) |r| {
            if (self.buffer.lineLen(r) == 0 and rows.len > 1) continue;
            const o = self.buffer.lineStart(r);
            edits.append(self.gpa, .{ .start = o, .end = o, .text = unit }) catch return;
        }
        self.commitTransform(edits.items, .other);
    }

    pub fn unindentLines(self: *Editor) void {
        const rows = self.selectedRows(self.gpa) catch return;
        defer self.gpa.free(rows);
        var s = Scanner.init(&self.buffer.rope);
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        const tab = self.tabSize();
        for (rows) |r| {
            const start = self.buffer.lineStart(r);
            const end = self.buffer.lineEnd(r);
            var o = start;
            if (o < end and s.byte(o) == '\t') {
                o += 1;
            } else {
                while (o < end and o - start < tab and s.byte(o) == ' ') o += 1;
            }
            if (o > start) edits.append(self.gpa, .{ .start = start, .end = o, .text = "" }) catch return;
        }
        self.commitTransform(edits.items, .other);
    }

    pub fn toggleComment(self: *Editor, block: bool) void {
        const lang = self.buffer.language;
        if (block or lang.line_comment == null) {
            const bc = lang.block_comment orelse return;
            self.toggleBlockComment(bc);
            return;
        }
        const token = lang.line_comment.?;
        const trimmed = std.mem.trimEnd(u8, token, " ");
        const rows = self.selectedRows(self.gpa) catch return;
        defer self.gpa.free(rows);
        var s = Scanner.init(&self.buffer.rope);

        // Comment out unless every non-blank line is already commented.
        var all_commented = true;
        var min_indent: usize = std.math.maxInt(usize);
        for (rows) |r| {
            const start = self.buffer.lineStart(r);
            const first = self.firstNonWhitespace(r);
            if (first == self.buffer.lineEnd(r)) continue;
            min_indent = @min(min_indent, first - start);
            var matches = true;
            for (trimmed, 0..) |ch, i| {
                if (first + i >= self.buffer.len() or s.byte(first + i) != ch) {
                    matches = false;
                    break;
                }
            }
            if (!matches) all_commented = false;
        }
        if (min_indent == std.math.maxInt(usize)) min_indent = 0;
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        for (rows) |r| {
            const start = self.buffer.lineStart(r);
            const first = self.firstNonWhitespace(r);
            const blank = first == self.buffer.lineEnd(r);
            if (all_commented) {
                if (blank) continue;
                var len = trimmed.len;
                if (first + len < self.buffer.len() and s.byte(first + len) == ' ' and token.len > trimmed.len) len += 1;
                edits.append(self.gpa, .{ .start = first, .end = first + len, .text = "" }) catch return;
            } else {
                if (blank and rows.len > 1) continue;
                const at = start + @min(min_indent, first - start);
                edits.append(self.gpa, .{ .start = at, .end = at, .text = token }) catch return;
            }
        }
        self.commitTransform(edits.items, .other);
    }

    fn toggleBlockComment(self: *Editor, bc: [2][]const u8) void {
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const open = std.mem.trimEnd(u8, bc[0], " ");
        const close = std.mem.trimStart(u8, bc[1], " ");
        for (self.selections.items) |sel| {
            var start = sel.start();
            var end = sel.end();
            if (sel.isEmpty()) {
                const row = self.buffer.offsetToPoint(sel.head).row;
                start = self.firstNonWhitespace(row);
                end = self.buffer.lineEnd(row);
            }
            const text = self.buffer.textRange(arena.allocator(), start, end) catch return;
            const t = std.mem.trim(u8, text, " \t");
            if (std.mem.startsWith(u8, t, open) and std.mem.endsWith(u8, t, close) and t.len >= open.len + close.len) {
                const lead = std.mem.indexOf(u8, text, open).?;
                const trail = std.mem.lastIndexOf(u8, text, close).?;
                var o_len = open.len;
                if (lead + o_len < text.len and text[lead + o_len] == ' ') o_len += 1;
                var c_start = trail;
                if (c_start > 0 and text[c_start - 1] == ' ') c_start -= 1;
                edits.append(self.gpa, .{ .start = start + c_start, .end = start + trail + close.len, .text = "" }) catch return;
                edits.append(self.gpa, .{ .start = start + lead, .end = start + lead + o_len, .text = "" }) catch return;
            } else {
                edits.append(self.gpa, .{ .start = start, .end = start, .text = bc[0] }) catch return;
                edits.append(self.gpa, .{ .start = end, .end = end, .text = bc[1] }) catch return;
            }
        }
        self.commitTransform(edits.items, .other);
    }

    pub fn duplicateLines(self: *Editor) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        const new_sels = self.gpa.alloc(Selection, self.selections.items.len) catch return;
        defer self.gpa.free(new_sels);
        var delta: usize = 0;
        for (self.selections.items, new_sels) |sel, *out| {
            if (sel.isEmpty()) {
                const row = self.buffer.offsetToPoint(sel.head).row;
                const start = self.buffer.lineStart(row);
                const end = self.buffer.lineEnd(row);
                const line = self.buffer.textRange(alloc, start, end) catch return;
                const text = std.mem.concat(alloc, u8, &.{ line, "\n" }) catch return;
                edits.append(self.gpa, .{ .start = start, .end = start, .text = text }) catch return;
                delta += text.len;
                out.* = .caret(sel.head + delta);
            } else {
                const text = self.buffer.textRange(alloc, sel.start(), sel.end()) catch return;
                edits.append(self.gpa, .{ .start = sel.end(), .end = sel.end(), .text = text }) catch return;
                out.* = .{ .anchor = sel.anchor + delta + text.len, .head = sel.head + delta + text.len };
                delta += text.len;
            }
        }
        self.commit(edits.items, new_sels, .other);
    }

    pub fn deleteLines(self: *Editor) void {
        const blocks = self.rowBlocks(self.gpa) catch return;
        defer self.gpa.free(blocks);
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        const last_row = self.buffer.lineCount() - 1;
        for (blocks) |b| {
            var start = self.buffer.lineStart(b[0]);
            var end = self.buffer.lineEnd(b[1]);
            if (b[1] < last_row) end += 1 else if (b[0] > 0) start -= 1;
            edits.append(self.gpa, .{ .start = start, .end = end, .text = "" }) catch return;
        }
        self.commitTransform(edits.items, .other);
        // Carets land at the start of the following line.
        for (self.selections.items) |*sel| sel.* = .caret(sel.head);
        self.normalize();
    }

    pub fn swapLines(self: *Editor, up: bool) void {
        const blocks = self.rowBlocks(self.gpa) catch return;
        defer self.gpa.free(blocks);
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        const last_row = self.buffer.lineCount() - 1;
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        for (blocks) |b| {
            if (up and b[0] == 0) return;
            if (!up and b[1] == last_row) return;
            const block_start = self.buffer.lineStart(b[0]);
            const block_end = self.buffer.lineEnd(b[1]);
            const block = self.buffer.textRange(alloc, block_start, block_end) catch return;
            if (up) {
                const other_start = self.buffer.lineStart(b[0] - 1);
                const other = self.buffer.textRange(alloc, other_start, block_start - 1) catch return;
                const text = std.mem.concat(alloc, u8, &.{ block, "\n", other }) catch return;
                edits.append(self.gpa, .{ .start = other_start, .end = block_end, .text = text }) catch return;
            } else {
                const other_end = self.buffer.lineEnd(b[1] + 1);
                const other = self.buffer.textRange(alloc, block_end + 1, other_end) catch return;
                const text = std.mem.concat(alloc, u8, &.{ other, "\n", block }) catch return;
                edits.append(self.gpa, .{ .start = block_start, .end = other_end, .text = text }) catch return;
            }
        }
        // Selections move with their lines (blocks are disjoint, so a single
        // shift per selection is exact).
        const new_sels = self.gpa.alloc(Selection, self.selections.items.len) catch return;
        defer self.gpa.free(new_sels);
        for (self.selections.items, new_sels) |sel, *out| {
            const row = self.buffer.offsetToPoint(sel.start()).row;
            var sh: isize = 0;
            for (blocks) |b| if (row >= b[0] and row <= b[1]) {
                if (up) {
                    sh = -@as(isize, @intCast(self.buffer.lineLen(b[0] - 1) + 1));
                } else {
                    sh = @intCast(self.buffer.lineLen(b[1] + 1) + 1);
                }
            };
            out.* = .{ .anchor = @intCast(@as(isize, @intCast(sel.anchor)) + sh), .head = @intCast(@as(isize, @intCast(sel.head)) + sh) };
        }
        self.commit(edits.items, new_sels, .other);
    }

    pub fn joinLines(self: *Editor) void {
        const blocks = self.rowBlocks(self.gpa) catch return;
        defer self.gpa.free(blocks);
        var s = Scanner.init(&self.buffer.rope);
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        const last_row = self.buffer.lineCount() - 1;
        for (blocks) |b| {
            const last = if (b[0] == b[1]) b[1] + 1 else b[1];
            if (last > last_row) continue;
            var r = b[0];
            while (r < last) : (r += 1) {
                const end = self.buffer.lineEnd(r);
                var trim_start = end;
                while (trim_start > self.buffer.lineStart(r) and (s.byte(trim_start - 1) == ' ' or s.byte(trim_start - 1) == '\t')) trim_start -= 1;
                const next_first = self.firstNonWhitespace(r + 1);
                const sep: []const u8 = if (trim_start == self.buffer.lineStart(r) or next_first == self.buffer.lineEnd(r + 1)) "" else " ";
                edits.append(self.gpa, .{ .start = trim_start, .end = next_first, .text = sep }) catch return;
            }
        }
        self.commitTransform(edits.items, .other);
    }

    pub fn insertLine(self: *Editor, after: bool) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        for (self.selections.items, reps) |sel, *r| {
            const row = self.buffer.offsetToPoint(sel.head).row;
            const start = self.buffer.lineStart(row);
            const indent = self.buffer.textRange(arena.allocator(), start, self.firstNonWhitespace(row)) catch return;
            if (after) {
                const end = self.buffer.lineEnd(row);
                const text = std.mem.concat(arena.allocator(), u8, &.{ "\n", indent }) catch return;
                r.* = .{ .start = end, .end = end, .text = text, .sel_anchor = text.len, .sel_head = text.len };
            } else {
                const text = std.mem.concat(arena.allocator(), u8, &.{ indent, "\n" }) catch return;
                r.* = .{ .start = start, .end = start, .text = text, .sel_anchor = indent.len, .sel_head = indent.len };
            }
        }
        // Several carets on one line would produce overlapping replacements.
        dedupeReplacements(reps);
        self.applyReplacements(reps, .other);
    }

    /// Sorts / reverses / uniques the selected lines (or the whole buffer).
    pub fn permuteLines(self: *Editor, op: enum { sort, sort_case, reverse, unique }) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var first: u32 = 0;
        var last: u32 = self.buffer.lineCount() - 1;
        const single_empty = self.selections.items.len == 1 and self.selections.items[0].isEmpty();
        if (!single_empty) {
            const rows = self.selectedRows(alloc) catch return;
            first = rows[0];
            last = rows[rows.len - 1];
        }
        const start = self.buffer.lineStart(first);
        const end = self.buffer.lineEnd(last);
        const text = self.buffer.textRange(alloc, start, end) catch return;
        var lines: std.ArrayList([]const u8) = .empty;
        var it = std.mem.splitScalar(u8, text, '\n');
        while (it.next()) |l| lines.append(alloc, l) catch return;
        switch (op) {
            .sort => std.mem.sort([]const u8, lines.items, {}, struct {
                fn lt(_: void, a: []const u8, b: []const u8) bool {
                    return std.ascii.lessThanIgnoreCase(a, b);
                }
            }.lt),
            .sort_case => std.mem.sort([]const u8, lines.items, {}, struct {
                fn lt(_: void, a: []const u8, b: []const u8) bool {
                    return std.mem.lessThan(u8, a, b);
                }
            }.lt),
            .reverse => std.mem.reverse([]const u8, lines.items),
            .unique => {
                var out: usize = 0;
                outer: for (lines.items) |l| {
                    for (lines.items[0..out]) |seen| if (std.mem.eql(u8, seen, l)) continue :outer;
                    lines.items[out] = l;
                    out += 1;
                }
                lines.shrinkRetainingCapacity(out);
            },
        }
        const joined = std.mem.join(alloc, "\n", lines.items) catch return;
        var reps = [_]Replacement{.{ .start = start, .end = end, .text = joined, .sel_anchor = 0, .sel_head = joined.len }};
        self.applyReplacements(&reps, .other);
    }

    // ---- Selection commands -----------------------------------------------

    pub fn selectAll(self: *Editor) void {
        self.pushSelectionHistory();
        self.setSingleSelection(.range(0, self.buffer.len()));
        self.touchCaret();
    }

    pub fn expandToLine(self: *Editor) void {
        self.pushSelectionHistory();
        const last_row = self.buffer.lineCount() - 1;
        for (self.selections.items) |*sel| {
            const a = self.buffer.offsetToPoint(sel.start());
            const b = self.buffer.offsetToPoint(sel.end());
            const start = self.buffer.lineStart(a.row);
            // Already whole lines: grow by the next line.
            const whole = !sel.isEmpty() and sel.start() == start and b.column == 0 and b.row > a.row;
            const end_row = if (whole) b.row else b.row + 1;
            const end = if (end_row <= last_row) self.buffer.lineStart(end_row) else self.buffer.len();
            sel.* = .range(start, end);
        }
        self.normalize();
        self.afterCommand(false);
    }

    fn selectionText(self: *Editor, gpa: Allocator, sel: Selection) ![]u8 {
        return self.buffer.textRange(gpa, sel.start(), sel.end());
    }

    /// ctrl+d: select the word, then add the next occurrence.
    pub fn findUnderExpand(self: *Editor, skip: bool) void {
        var s = Scanner.init(&self.buffer.rope);
        var all_empty = true;
        for (self.selections.items) |sel| if (!sel.isEmpty()) {
            all_empty = false;
        };
        if (all_empty) {
            self.pushSelectionHistory();
            for (self.selections.items) |*sel| {
                if (movement.wordAt(&s, sel.head)) |w| sel.* = .range(w[0], w[1]);
            }
            self.find_whole_word = true;
            self.normalize();
            self.afterCommand(false);
            return;
        }
        const primary = self.primarySelection();
        const needle = self.selectionText(self.gpa, primary) catch return;
        defer self.gpa.free(needle);
        if (needle.len == 0) return;
        const from = primary.end();
        const found = self.findNextOccurrence(needle, from, self.find_whole_word) orelse return;
        self.pushSelectionHistory();
        if (skip and self.selections.items.len > 1) {
            _ = self.selections.orderedRemove(self.primary);
        } else if (skip) {
            self.selections.clearRetainingCapacity();
        }
        // Already selected (wrapped all the way around)?
        for (self.selections.items) |sel| if (sel.start() == found and sel.end() == found + needle.len) return;
        self.selections.append(self.gpa, .range(found, found + needle.len)) catch return;
        self.normalize();
        // The new selection is primary.
        for (self.selections.items, 0..) |sel, i| if (sel.start() == found) {
            self.primary = i;
        };
        self.afterCommand(false);
    }

    pub fn findAllUnder(self: *Editor) void {
        var s = Scanner.init(&self.buffer.rope);
        var sel = self.primarySelection();
        var whole_word = self.find_whole_word;
        if (sel.isEmpty()) {
            const w = movement.wordAt(&s, sel.head) orelse return;
            sel = .range(w[0], w[1]);
            whole_word = true;
        }
        const needle = self.selectionText(self.gpa, sel) catch return;
        defer self.gpa.free(needle);
        if (needle.len == 0) return;
        var found: std.ArrayList(Selection) = .empty;
        defer found.deinit(self.gpa);
        var from: usize = 0;
        while (self.findForward(needle, from, whole_word)) |pos| {
            found.append(self.gpa, .range(pos, pos + needle.len)) catch return;
            from = pos + needle.len;
        }
        if (found.items.len == 0) return;
        self.pushSelectionHistory();
        self.setSelections(found.items);
        self.afterCommand(false);
    }

    /// Next occurrence at or after `from`, wrapping around.
    pub fn findNextOccurrence(self: *Editor, needle: []const u8, from: usize, whole_word: bool) ?usize {
        return self.findForward(needle, from, whole_word) orelse self.findForward(needle, 0, whole_word);
    }

    pub fn findForward(self: *Editor, needle: []const u8, from: usize, whole_word: bool) ?usize {
        var s = Scanner.init(&self.buffer.rope);
        const total = self.buffer.len();
        if (needle.len == 0 or needle.len > total) return null;
        var o = from;
        while (o + needle.len <= total) : (o += 1) {
            if (s.byte(o) != needle[0]) continue;
            var ok = true;
            for (needle[1..], 1..) |ch, i| if (s.byte(o + i) != ch) {
                ok = false;
                break;
            };
            if (!ok) continue;
            if (whole_word) {
                const before = s.charBefore(o);
                const after = s.charAfter(o + needle.len);
                if ((before != null and movement.isWordChar(before.?)) or (after != null and movement.isWordChar(after.?))) continue;
            }
            return o;
        }
        return null;
    }

    pub fn splitIntoLines(self: *Editor) void {
        var out: std.ArrayList(Selection) = .empty;
        defer out.deinit(self.gpa);
        for (self.selections.items) |sel| {
            if (sel.isEmpty()) {
                out.append(self.gpa, sel) catch return;
                continue;
            }
            const a = self.buffer.offsetToPoint(sel.start());
            const b = self.buffer.offsetToPoint(sel.end());
            var r = a.row;
            while (r <= b.row) : (r += 1) {
                const s = if (r == a.row) sel.start() else self.buffer.lineStart(r);
                const e = if (r == b.row) sel.end() else self.buffer.lineEnd(r);
                if (s == e and r == b.row and r != a.row) continue;
                out.append(self.gpa, .range(s, e)) catch return;
            }
        }
        self.pushSelectionHistory();
        self.setSelections(out.items);
        self.afterCommand(false);
    }

    pub fn addCursorVertical(self: *Editor, below: bool) void {
        self.pushSelectionHistory();
        // Extend from the outermost caret in that direction.
        var edge = self.selections.items[0];
        for (self.selections.items) |sel| {
            if (below and sel.head > edge.head) edge = sel;
            if (!below and sel.head < edge.head) edge = sel;
        }
        const d = self.displayRowOfOffset(edge.head);
        const last_d = self.displayRowCount() - 1;
        if ((below and d >= last_d) or (!below and d == 0)) return;
        const goal = edge.goal_x orelse self.xForOffset(edge.head);
        const off = self.offsetForDisplayRow(if (below) d + 1 else d - 1, goal);
        var new_sel = Selection.caret(off);
        new_sel.goal_x = goal;
        self.selections.append(self.gpa, new_sel) catch return;
        self.normalize();
        for (self.selections.items, 0..) |sel, i| if (sel.head == off) {
            self.primary = i;
        };
        self.afterCommand(false);
    }

    pub fn singleSelection(self: *Editor) void {
        if (self.selections.items.len > 1) {
            const p = self.primarySelection();
            self.pushSelectionHistory();
            self.setSingleSelection(p);
        } else if (!self.selections.items[0].isEmpty()) {
            self.pushSelectionHistory();
            self.setSingleSelection(.caret(self.selections.items[0].head));
        }
        self.touchCaret();
    }

    pub fn expandToBrackets(self: *Editor) void {
        var s = Scanner.init(&self.buffer.rope);
        self.pushSelectionHistory();
        for (self.selections.items) |*sel| {
            const pair = movement.enclosingBrackets(&s, sel.start(), sel.end()) orelse continue;
            const inner: Selection = .range(pair[0] + 1, pair[1]);
            // Second press includes the brackets themselves.
            if (sel.start() == inner.start() and sel.end() == inner.end()) {
                sel.* = .range(pair[0], pair[1] + 1);
            } else {
                sel.* = inner;
            }
        }
        self.normalize();
        self.afterCommand(false);
    }

    pub fn expandToIndentation(self: *Editor) void {
        self.pushSelectionHistory();
        const last_row = self.buffer.lineCount() - 1;
        for (self.selections.items) |*sel| {
            const row = self.buffer.offsetToPoint(sel.head).row;
            const indent = self.lineIndentWidth(row) orelse continue;
            var first = row;
            var last = row;
            while (first > 0) {
                const w = self.lineIndentWidth(first - 1);
                if (w != null and w.? < indent) break;
                first -= 1;
            }
            while (last < last_row) {
                const w = self.lineIndentWidth(last + 1);
                if (w != null and w.? < indent) break;
                last += 1;
            }
            sel.* = .range(self.buffer.lineStart(first), self.buffer.lineEnd(last));
        }
        self.normalize();
        self.afterCommand(false);
    }

    pub fn expandToScope(self: *Editor) void {
        const syntax = &(self.buffer.syntax orelse return);
        self.buffer.updateSyntax();
        self.pushSelectionHistory();
        for (self.selections.items) |*sel| {
            const r = syntax.enclosingNode(sel.start(), sel.end()) orelse continue;
            sel.* = .range(r[0], r[1]);
        }
        self.normalize();
        self.afterCommand(false);
    }

    pub fn jumpToBracket(self: *Editor) void {
        var s = Scanner.init(&self.buffer.rope);
        self.pushSelectionHistory();
        for (self.selections.items) |*sel| {
            const h = sel.head;
            var target: ?usize = null;
            if (h < s.len() and (movement.isOpenBracket(s.charAt(h)) or movement.isCloseBracket(s.charAt(h)))) {
                target = movement.matchingBracket(&s, h);
            } else if (h > 0 and movement.isCloseBracket(s.charAt(h - 1))) {
                target = movement.matchingBracket(&s, h - 1);
            } else if (movement.enclosingBrackets(&s, h, h)) |pair| {
                target = pair[1];
            }
            if (target) |t| sel.* = .caret(t);
        }
        self.normalize();
        self.afterCommand(false);
    }

    pub fn softUndo(self: *Editor) void {
        const prev = self.sel_history.pop() orelse return;
        const current = self.gpa.dupe(Selection, self.selections.items) catch return;
        self.sel_redo.append(self.gpa, current) catch self.gpa.free(current);
        self.setSelections(prev);
        self.gpa.free(prev);
        self.afterCommand(false);
    }

    pub fn softRedo(self: *Editor) void {
        const next = self.sel_redo.pop() orelse return;
        self.pushSelectionHistory();
        self.setSelections(next);
        self.gpa.free(next);
        self.afterCommand(false);
    }

    // ---- Clipboard --------------------------------------------------------

    pub fn copy(self: *Editor, cut_too: bool) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var all_empty = true;
        for (self.selections.items) |sel| if (!sel.isEmpty()) {
            all_empty = false;
        };
        var parts: std.ArrayList([]const u8) = .empty;
        if (all_empty) {
            // Whole-line copy.
            const rows = self.selectedRows(alloc) catch return;
            for (rows) |r| {
                const line = self.buffer.textRange(alloc, self.buffer.lineStart(r), self.buffer.lineEnd(r)) catch return;
                parts.append(alloc, std.mem.concat(alloc, u8, &.{ line, "\n" }) catch return) catch return;
            }
            const text = std.mem.join(alloc, "", parts.items) catch return;
            self.setClipboard(text, true);
            if (cut_too) self.deleteLines();
            return;
        }
        for (self.selections.items) |sel| {
            if (sel.isEmpty()) continue;
            parts.append(alloc, self.buffer.textRange(alloc, sel.start(), sel.end()) catch return) catch return;
        }
        const text = std.mem.join(alloc, "\n", parts.items) catch return;
        self.setClipboard(text, false);
        if (cut_too) {
            const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
            defer self.gpa.free(reps);
            for (self.selections.items, reps) |sel, *r| r.* = .{ .start = sel.start(), .end = sel.end(), .text = "", .sel_anchor = 0, .sel_head = 0 };
            self.applyReplacements(reps, .other);
        }
    }

    fn setClipboard(self: *Editor, text: []const u8, whole_line: bool) void {
        self.env.clipboard.set(self.env.clipboard.ctx, text);
        if (self.env.clipboard_whole_line) |old| self.env.gpa.free(old);
        self.env.clipboard_whole_line = if (whole_line) self.env.gpa.dupe(u8, text) catch null else null;
    }

    pub fn paste(self: *Editor) void {
        const text = self.env.clipboard.get(self.env.clipboard.ctx, self.gpa) orelse return;
        defer self.gpa.free(text);
        self.pasteText(text);
    }

    pub fn pasteText(self: *Editor, raw: []const u8) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        // Normalize line endings.
        const text = std.mem.replaceOwned(u8, alloc, raw, "\r\n", "\n") catch return;
        const whole_line = if (self.env.clipboard_whole_line) |w| std.mem.eql(u8, w, text) else false;

        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        // One line per caret when the counts match.
        var lines: std.ArrayList([]const u8) = .empty;
        var it = std.mem.splitScalar(u8, std.mem.trimEnd(u8, text, "\n"), '\n');
        while (it.next()) |l| lines.append(alloc, l) catch return;
        const distribute = self.selections.items.len > 1 and lines.items.len == self.selections.items.len;

        for (self.selections.items, reps, 0..) |sel, *r, i| {
            const piece = if (distribute) lines.items[i] else text;
            if (whole_line and sel.isEmpty() and !distribute) {
                const ls = self.buffer.lineStart(self.buffer.offsetToPoint(sel.head).row);
                const off = sel.head - ls + piece.len;
                r.* = .{ .start = ls, .end = ls, .text = piece, .sel_anchor = off, .sel_head = off };
            } else {
                r.* = .{ .start = sel.start(), .end = sel.end(), .text = piece, .sel_anchor = piece.len, .sel_head = piece.len };
            }
        }
        dedupeReplacements(reps);
        self.applyReplacements(reps, .other);
    }

    // ---- Undo -------------------------------------------------------------

    pub fn undo(self: *Editor) void {
        self.applying_edit = true;
        const sels = self.buffer.undo() catch null;
        self.applying_edit = false;
        if (sels) |s| self.setSelections(s);
        self.afterCommand(false);
    }

    pub fn redo(self: *Editor) void {
        self.applying_edit = true;
        const sels = self.buffer.redo() catch null;
        self.applying_edit = false;
        if (sels) |s| self.setSelections(s);
        self.afterCommand(false);
    }

    // ---- Folding ------------------------------------------------------------

    /// Last row of the foldable region headed by `row`, if any: an org
    /// subtree for Org files, otherwise the following more-indented lines.
    pub fn foldEndFor(self: *Editor, row: u32) ?u32 {
        const last_row = self.buffer.lineCount() - 1;
        if (std.mem.eql(u8, self.buffer.language.name, "Org")) {
            const level = self.orgHeadingLevel(row) orelse return null;
            var r = row + 1;
            var last: ?u32 = null;
            while (r <= last_row) : (r += 1) {
                if (self.orgHeadingLevel(r)) |l| if (l <= level) break;
                last = r;
            }
            // Trailing blank lines stay visible, like Emacs.
            while (last != null and last.? > row and self.buffer.lineLen(last.?) == 0) last = if (last.? - 1 > row) last.? - 1 else null;
            return last;
        }
        const indent = self.lineIndentWidth(row) orelse return null;
        var r = row + 1;
        var last: ?u32 = null;
        while (r <= last_row) : (r += 1) {
            const w = self.lineIndentWidth(r) orelse continue;
            if (w <= indent) break;
            last = r;
        }
        return last;
    }

    pub fn orgHeadingLevel(self: *Editor, row: u32) ?u32 {
        const start = self.buffer.lineStart(row);
        const end = self.buffer.lineEnd(row);
        var s = Scanner.init(&self.buffer.rope);
        var o = start;
        while (o < end and s.byte(o) == '*') o += 1;
        if (o == start or o >= end or s.byte(o) != ' ') return null;
        return @intCast(o - start);
    }

    /// Toggles the fold headed by `row`. Returns false if nothing is foldable there.
    pub fn toggleFoldAt(self: *Editor, row: u32) bool {
        if (self.display.folds.unfoldHeader(self.buffer, row)) return true;
        const last = self.foldEndFor(row) orelse return false;
        self.display.folds.fold(self.gpa, self.buffer, row, last) catch return false;
        self.moveCaretsOutOfFolds();
        return true;
    }

    /// Carets inside a newly folded region move to its header line.
    pub fn moveCaretsOutOfFolds(self: *Editor) void {
        for (self.selections.items) |*sel| {
            const row = self.buffer.offsetToPoint(sel.head).row;
            if (!self.isRowHidden(row)) continue;
            var r = row;
            while (r > 0 and self.isRowHidden(r)) r -= 1;
            sel.* = .caret(self.buffer.lineStart(r));
        }
        self.normalize();
    }

    pub fn foldSelection(self: *Editor) void {
        for (self.selections.items) |sel| {
            const a = self.buffer.offsetToPoint(sel.start()).row;
            const b = self.buffer.offsetToPoint(sel.end()).row;
            if (b > a) {
                self.display.folds.fold(self.gpa, self.buffer, a, b) catch return;
                continue;
            }
            // The block the caret's line heads, or else the enclosing one.
            var row = a;
            while (true) {
                if (self.foldEndFor(row)) |last| if (last >= a) {
                    self.display.folds.fold(self.gpa, self.buffer, row, last) catch return;
                    break;
                };
                if (row == 0) break;
                row -= 1;
            }
        }
        self.moveCaretsOutOfFolds();
    }

    pub fn unfoldSelection(self: *Editor) void {
        for (self.selections.items) |sel| {
            const a = self.buffer.offsetToPoint(sel.start()).row;
            const b = self.buffer.offsetToPoint(sel.end()).row;
            var r = a;
            while (r <= b) : (r += 1) _ = self.display.folds.unfoldHeader(self.buffer, r);
        }
    }

    /// Folds every block whose indentation level (or org heading level) is `level`.
    pub fn foldLevel(self: *Editor, level: u32) void {
        self.display.folds.unfoldAll();
        const is_org = std.mem.eql(u8, self.buffer.language.name, "Org");
        const unit = self.tabSize();
        var ranges: std.ArrayList([2]u32) = .empty;
        defer ranges.deinit(self.gpa);
        var row: u32 = 0;
        const count = self.buffer.lineCount();
        while (row < count) : (row += 1) {
            const matches = if (is_org)
                (self.orgHeadingLevel(row) orelse 0) == level
            else if (self.lineIndentWidth(row)) |w| w / unit == level - 1 else false;
            if (!matches) continue;
            if (self.foldEndFor(row)) |last| ranges.append(self.gpa, .{ row, last }) catch return;
        }
        self.display.folds.foldMany(self.gpa, self.buffer, ranges.items) catch return;
        self.moveCaretsOutOfFolds();
    }

    // ---- Action dispatch --------------------------------------------------

    /// Returns false if the action isn't an editor action.
    pub fn perform(self: *Editor, action: Action) bool {
        switch (action) {
            .move_left => self.moveSelections(.left, false),
            .move_right => self.moveSelections(.right, false),
            .move_up => self.moveSelections(.up, false),
            .move_down => self.moveSelections(.down, false),
            .move_word_left => self.moveSelections(.word_left, false),
            .move_word_right => self.moveSelections(.word_right, false),
            .move_subword_left => self.moveSelections(.subword_left, false),
            .move_subword_right => self.moveSelections(.subword_right, false),
            .move_line_start => self.moveSelections(.line_start, false),
            .move_line_end => self.moveSelections(.line_end, false),
            .move_page_up => self.moveSelections(.page_up, false),
            .move_page_down => self.moveSelections(.page_down, false),
            .move_doc_start => self.moveSelections(.doc_start, false),
            .move_doc_end => self.moveSelections(.doc_end, false),
            .select_left => self.moveSelections(.left, true),
            .select_right => self.moveSelections(.right, true),
            .select_up => self.moveSelections(.up, true),
            .select_down => self.moveSelections(.down, true),
            .select_word_left => self.moveSelections(.word_left, true),
            .select_word_right => self.moveSelections(.word_right, true),
            .select_subword_left => self.moveSelections(.subword_left, true),
            .select_subword_right => self.moveSelections(.subword_right, true),
            .select_line_start => self.moveSelections(.line_start, true),
            .select_line_end => self.moveSelections(.line_end, true),
            .select_page_up => self.moveSelections(.page_up, true),
            .select_page_down => self.moveSelections(.page_down, true),
            .select_doc_start => self.moveSelections(.doc_start, true),
            .select_doc_end => self.moveSelections(.doc_end, true),
            .scroll_line_up, .scroll_line_down => {
                self.scroll_y += if (action == .scroll_line_up) -self.lineHeight() else self.lineHeight();
                self.clampScroll();
            },

            .select_all => self.selectAll(),
            .expand_selection_line => self.expandToLine(),
            .find_under_expand => self.findUnderExpand(false),
            .find_under_expand_skip => self.findUnderExpand(true),
            .find_all_under => self.findAllUnder(),
            .split_selection_into_lines => self.splitIntoLines(),
            .add_cursor_above => self.addCursorVertical(false),
            .add_cursor_below => self.addCursorVertical(true),
            .single_selection => self.singleSelection(),
            .expand_selection_brackets => self.expandToBrackets(),
            .expand_selection_indentation => self.expandToIndentation(),
            .jump_to_bracket => self.jumpToBracket(),

            .insert_newline => self.insertNewline(),
            .insert_tab => {
                var multi_line = false;
                for (self.selections.items) |sel| {
                    if (self.buffer.offsetToPoint(sel.start()).row != self.buffer.offsetToPoint(sel.end()).row) multi_line = true;
                }
                if (multi_line) self.indentLines() else self.insertSoftTab();
            },
            .backspace => self.deleteBackward(),
            .delete_forward => self.deleteForward(),
            .delete_word_backward => self.deleteMotion(.word_left),
            .delete_word_forward => self.deleteMotion(.word_right),
            .delete_to_line_start => self.deleteMotion(.line_start),
            .delete_to_line_end => self.deleteMotion(.line_end),
            .delete_line => self.deleteLines(),
            .indent => self.indentLines(),
            .unindent => self.unindentLines(),
            .toggle_comment => self.toggleComment(false),
            .toggle_block_comment => self.toggleComment(true),
            .duplicate_line => self.duplicateLines(),
            .swap_line_up => self.swapLines(true),
            .swap_line_down => self.swapLines(false),
            .join_lines => self.joinLines(),
            .insert_line_after => self.insertLine(true),
            .insert_line_before => self.insertLine(false),
            .upper_case => self.transformSelections(caseUpper),
            .lower_case => self.transformSelections(caseLower),
            .title_case => self.transformSelections(caseTitle),
            .swap_case => self.transformSelections(caseSwap),
            .sort_lines => self.permuteLines(.sort),
            .sort_lines_case_sensitive => self.permuteLines(.sort_case),
            .reverse_lines => self.permuteLines(.reverse),
            .unique_lines => self.permuteLines(.unique),
            .trim_trailing_whitespace => self.trimTrailingWhitespace(),
            .convert_indentation_to_spaces => self.convertIndentation(true),
            .convert_indentation_to_tabs => self.convertIndentation(false),
            .undo => self.undo(),
            .redo => self.redo(),
            .soft_undo => self.softUndo(),
            .soft_redo => self.softRedo(),
            .copy => self.copy(false),
            .cut => self.copy(true),
            .paste, .paste_and_indent => self.paste(),
            .fold => self.foldSelection(),
            .unfold => self.unfoldSelection(),
            .unfold_all => self.display.folds.unfoldAll(),
            .toggle_word_wrap => {
                self.word_wrap_override = !self.wrapEnabled();
                // Apply the new wrap width now, so the caret is found in the
                // new layout (and turning wrap off never re-wraps at the old one).
                self.layout(self.bounds);
                self.ensureCaretVisible(false);
            },
            .fold_level_1 => self.foldLevel(1),
            .fold_level_2 => self.foldLevel(2),
            .fold_level_3 => self.foldLevel(3),
            .fold_level_4 => self.foldLevel(4),
            .set_tab_size_2 => self.buffer.tab_size = 2,
            .set_tab_size_4 => self.buffer.tab_size = 4,
            .set_tab_size_8 => self.buffer.tab_size = 8,
            .toggle_translate_tabs => self.buffer.translate_tabs_to_spaces = !self.buffer.translate_tabs_to_spaces,
            else => return false,
        }
        return true;
    }

    fn insertSoftTab(self: *Editor) void {
        if (!self.buffer.translate_tabs_to_spaces) {
            self.insertText("\t");
            return;
        }
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const reps = self.gpa.alloc(Replacement, self.selections.items.len) catch return;
        defer self.gpa.free(reps);
        const tab = self.tabSize();
        for (self.selections.items, reps) |sel, *r| {
            // Display column (tabs expanded) of the caret.
            const col = self.displayColumn(sel.start());
            const n = tab - (col % tab);
            const spaces = arena.allocator().alloc(u8, n) catch return;
            @memset(spaces, ' ');
            r.* = .{ .start = sel.start(), .end = sel.end(), .text = spaces, .sel_anchor = n, .sel_head = n };
        }
        self.applyReplacements(reps, .typing);
    }

    fn trimTrailingWhitespace(self: *Editor) void {
        var s = Scanner.init(&self.buffer.rope);
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        var r: u32 = 0;
        while (r < self.buffer.lineCount()) : (r += 1) {
            const start = self.buffer.lineStart(r);
            const end = self.buffer.lineEnd(r);
            var o = end;
            while (o > start and (s.byte(o - 1) == ' ' or s.byte(o - 1) == '\t')) o -= 1;
            if (o < end) edits.append(self.gpa, .{ .start = o, .end = end, .text = "" }) catch return;
        }
        self.commitTransform(edits.items, .other);
    }

    fn convertIndentation(self: *Editor, to_spaces: bool) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        var edits: std.ArrayList(Edit) = .empty;
        defer edits.deinit(self.gpa);
        const tab = self.tabSize();
        var r: u32 = 0;
        while (r < self.buffer.lineCount()) : (r += 1) {
            const width = self.lineIndentWidth(r) orelse continue;
            const start = self.buffer.lineStart(r);
            const first = self.firstNonWhitespace(r);
            var text: std.ArrayList(u8) = .empty;
            if (to_spaces) {
                text.appendNTimes(arena.allocator(), ' ', width) catch return;
            } else {
                text.appendNTimes(arena.allocator(), '\t', width / tab) catch return;
                text.appendNTimes(arena.allocator(), ' ', width % tab) catch return;
            }
            edits.append(self.gpa, .{ .start = start, .end = first, .text = text.items }) catch return;
        }
        self.buffer.translate_tabs_to_spaces = to_spaces;
        self.commitTransform(edits.items, .other);
    }
};

fn dedupeReplacements(reps: []Editor.Replacement) void {
    // Identical insertion points (several carets on one line) collapse into one.
    for (reps, 0..) |*r, i| {
        for (reps[0..i]) |prev| if (prev.start == r.start and prev.end == r.end and r.start == r.end) {
            r.text = "";
        };
    }
}

fn transformThrough(edits: []const Edit, offset: usize, bias_right: bool) usize {
    var delta: isize = 0;
    for (edits) |e| {
        if (e.end < offset or (e.end == offset and e.start < offset)) {
            delta += @as(isize, @intCast(e.text.len)) - @as(isize, @intCast(e.end - e.start));
        } else if (e.start <= offset and offset <= e.end) {
            const base: isize = @intCast(e.start);
            if (e.start == e.end) {
                // Insertion at the offset.
                if (bias_right) delta += @intCast(e.text.len);
                return @intCast(@as(isize, @intCast(offset)) + delta);
            }
            return @intCast(base + delta + (if (bias_right) @as(isize, @intCast(e.text.len)) else 0));
        } else break;
    }
    return @intCast(@as(isize, @intCast(offset)) + delta);
}

fn selectionsEqual(a: []const Selection, b: []const Selection) bool {
    if (a.len != b.len) return false;
    for (a, b) |x, y| if (x.anchor != y.anchor or x.head != y.head) return false;
    return true;
}

fn caseUpper(gpa: Allocator, text: []const u8) ![]u8 {
    return std.ascii.allocUpperString(gpa, text);
}

fn caseLower(gpa: Allocator, text: []const u8) ![]u8 {
    return std.ascii.allocLowerString(gpa, text);
}

fn caseSwap(gpa: Allocator, text: []const u8) ![]u8 {
    const out = try gpa.dupe(u8, text);
    for (out) |*ch| {
        if (std.ascii.isUpper(ch.*)) ch.* = std.ascii.toLower(ch.*) else if (std.ascii.isLower(ch.*)) ch.* = std.ascii.toUpper(ch.*);
    }
    return out;
}

fn caseTitle(gpa: Allocator, text: []const u8) ![]u8 {
    const out = try gpa.dupe(u8, text);
    var at_start = true;
    for (out) |*ch| {
        if (std.ascii.isAlphabetic(ch.*)) {
            ch.* = if (at_start) std.ascii.toUpper(ch.*) else std.ascii.toLower(ch.*);
            at_start = false;
        } else at_start = !std.ascii.isDigit(ch.*);
    }
    return out;
}
