//! The Markdown preview: a rendered, live view of a Markdown file in a tab of
//! its own (like the Settings tab, its editor shows an empty read-only buffer
//! and `Tab.markdown` is drawn instead of it). The file is parsed by
//! model/markdown; this module lays the tree out with the GPU text system,
//! paints it, and handles scrolling, text selection, links and images.
//!
//! The preview follows the open buffer of its file: a change re-parses it
//! (a few ms for a README) and lays it out again, keeping the line at the top
//! where it was. Scrolling the editor scrolls the preview to the same source
//! line, and scrolling the preview scrolls the editor.
//!
//! Layout works on a flat list of "runs" (a word, a code line, a bullet) with
//! their boxes, in reading order: painting walks the blocks in view, hit
//! testing and selection index the same runs, and copying joins their text.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");
const workspace_mod = @import("workspace.zig");
const file_cmd = @import("file_cmd.zig");
const groups = @import("groups.zig");
const keymap_mod = @import("../settings/keymap.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const md = @import("../../model/markdown/markdown.zig");
const tree = @import("../../model/markdown/tree.zig");
const syntax_mod = @import("../../model/syntax/syntax.zig");
const languages = @import("../../model/syntax/languages.zig");
const Rope = @import("../../model/text/rope.zig").Rope;
const vfs = @import("../../model/remote/vfs.zig");
const rpath = @import("../../model/remote/path.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const text_mod = @import("../../framework/text/text_system.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const image_decode = @import("../../framework/platform/image_decode.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Scene = scene_mod.Scene;
const FontId = text_mod.FontId;
const ColorSpan = text_mod.ColorSpan;
const TextSystem = text_mod.TextSystem;
const Theme = theme_mod.Theme;
const Inline = tree.Inline;
const Block = tree.Block;

// ---- Measurements (logical px at the body size of 14) ---------------------------

const page_pad_x: f32 = 26;
const page_pad_top: f32 = 18;
const page_pad_bottom: f32 = 40;
const line_height_factor: f32 = 1.6;
const code_size_factor: f32 = 0.93;
const heading_em = [6]f32{ 2, 1.5, 1.25, 1, 0.875, 0.85 };
const list_indent: f32 = 26;
const quote_indent: f32 = 18;
const code_pad: f32 = 12;
const cell_pad_x: f32 = 10;
const cell_pad_y: f32 = 5;
const scrollbar_w: f32 = 12;
/// A source longer than this many bytes is cut (the parser's own limit).
const max_source_bytes = 4_000_000;
/// Code blocks longer than this are not highlighted.
const max_highlight_bytes = 200_000;
/// Decoded images are scaled down to this width (device px).
const max_image_width = 2048;
const max_image_file = 20 << 20;
/// How soon after an edit the preview is parsed again.
const reparse_delay_ms = 120;
const tab_width = 4;

// ---- Laid-out content -----------------------------------------------------------------------

/// What comes between a run and the one before it, for copying.
const Sep = enum(u2) { none, space, line, block };

const Run = struct {
    x: f32,
    y: f32 = 0,
    w: f32,
    h: f32 = 0,
    baseline: f32 = 0,
    /// Left inset of the text inside the box (padding of inline code).
    inset: f32 = 0,
    text: []const u8,
    font: FontId,
    color: Color,
    underline: bool = false,
    strike: bool = false,
    bg: ?Color = null,
    link: i32 = -1,
    spans: []const ColorSpan = &.{},
    sep: Sep = .none,
};

const Fill = struct {
    rect: Rect,
    color: Color = .transparent,
    border: Color = .transparent,
    border_w: f32 = 0,
    radius: f32 = 0,
};

const ImageBox = struct {
    rect: Rect,
    slot: u32,
};

const Range = struct { start: u32 = 0, end: u32 = 0 };

/// One top-level block's extent and what it drew.
const BlockInfo = struct {
    top: f32,
    bottom: f32,
    fills: Range,
    runs: Range,
    glyphs: Range,
    images: Range,
};

/// A source line and where it is on the page (for scroll sync).
const LinePoint = struct { line: u32, y: f32 };

const Anchor = struct { id: []const u8, y: f32 };

const Layout = struct {
    arena: std.heap.ArenaAllocator,
    fills: std.ArrayList(Fill) = .empty,
    runs: std.ArrayList(Run) = .empty,
    /// Bullets, numbers and check marks: drawn, but not text to select.
    glyphs: std.ArrayList(Run) = .empty,
    images: std.ArrayList(ImageBox) = .empty,
    blocks: std.ArrayList(BlockInfo) = .empty,
    points: std.ArrayList(LinePoint) = .empty,
    anchors: std.ArrayList(Anchor) = .empty,
    links: std.ArrayList([]const u8) = .empty,
    height: f32 = 0,
    width: f32 = 0,

    fn deinit(self: *Layout) void {
        self.arena.deinit();
    }

    /// The y of source line `line` (fractional), by interpolating between the
    /// points around it.
    fn yForLine(self: *const Layout, line: f32) f32 {
        const pts = self.points.items;
        if (pts.len == 0) return 0;
        if (line <= @as(f32, @floatFromInt(pts[0].line))) return pts[0].y;
        var lo: usize = 0;
        var hi: usize = pts.len;
        // The last point whose line is <= `line`.
        while (hi - lo > 1) {
            const mid = (lo + hi) / 2;
            if (@as(f32, @floatFromInt(pts[mid].line)) <= line) lo = mid else hi = mid;
        }
        const p0 = pts[lo];
        if (lo + 1 >= pts.len) return p0.y;
        const p1 = pts[lo + 1];
        const span = @as(f32, @floatFromInt(p1.line)) - @as(f32, @floatFromInt(p0.line));
        if (span <= 0) return p0.y;
        const t = (line - @as(f32, @floatFromInt(p0.line))) / span;
        return p0.y + (p1.y - p0.y) * std.math.clamp(t, 0, 1);
    }

    /// The source line (fractional) shown at page y.
    fn lineForY(self: *const Layout, y: f32) f32 {
        const pts = self.points.items;
        if (pts.len == 0) return 0;
        if (y <= pts[0].y) return @floatFromInt(pts[0].line);
        var lo: usize = 0;
        var hi: usize = pts.len;
        while (hi - lo > 1) {
            const mid = (lo + hi) / 2;
            if (pts[mid].y <= y) lo = mid else hi = mid;
        }
        const p0 = pts[lo];
        if (lo + 1 >= pts.len) return @floatFromInt(p0.line);
        const p1 = pts[lo + 1];
        const span = p1.y - p0.y;
        if (span <= 0) return @floatFromInt(p0.line);
        const t = (y - p0.y) / span;
        return @as(f32, @floatFromInt(p0.line)) + (@as(f32, @floatFromInt(p1.line)) - @as(f32, @floatFromInt(p0.line))) * std.math.clamp(t, 0, 1);
    }
};

// ---- Images -------------------------------------------------------------------------------------

const ImageSlot = struct {
    /// Scene image id once decoded; 0 while not.
    id: u32 = 0,
    width: u32 = 0,
    height: u32 = 0,
    state: enum { new, ready, failed, blocked } = .new,
};

// ---- Selection ----------------------------------------------------------------------------------

/// A caret between two characters of a run.
const Pos = struct {
    run: u32,
    off: u32,

    fn order(a: Pos, b: Pos) std.math.Order {
        if (a.run != b.run) return std.math.order(a.run, b.run);
        return std.math.order(a.off, b.off);
    }
};

const Sel = struct {
    anchor: Pos,
    head: Pos,

    fn lo(s: Sel) Pos {
        return if (Pos.order(s.anchor, s.head) == .gt) s.head else s.anchor;
    }

    fn hi(s: Sel) Pos {
        return if (Pos.order(s.anchor, s.head) == .gt) s.anchor else s.head;
    }

    fn empty(s: Sel) bool {
        return Pos.order(s.anchor, s.head) == .eq;
    }
};

const Fonts = struct {
    body: FontId,
    bold: FontId,
    italic: FontId,
    bold_italic: FontId,
    code: FontId,
    code_bold: FontId,
    headings: [6]FontId,
    body_size: f32,
    code_size: f32,

    fn forStyle(f: Fonts, bold: bool, italic: bool) FontId {
        if (bold and italic) return f.bold_italic;
        if (bold) return f.bold;
        if (italic) return f.italic;
        return f.body;
    }
};

const Palette = struct {
    fg: Color,
    muted: Color,
    link: Color,
    code_bg: Color,
    block_bg: Color,
    quote_bar: Color,
    quote_bg: Color,
    rule: Color,
    border: Color,
    header_bg: Color,
    zebra: Color,
    accent: Color,

    fn of(theme: *const Theme) Palette {
        const bg = theme.background;
        const fg = theme.foreground;
        const light = theme.light;
        const link = if (light) Color.hex(0x006ab1) else Color.hex(0x3794ff);
        return .{
            .fg = fg,
            .muted = theme.list_foreground_dim,
            .link = link,
            .code_bg = Color.mix(bg, fg, 0.12),
            .block_bg = if (light) Color.mix(bg, Color.hex(0x000000), 0.04) else bg.darken(0.35),
            .quote_bar = link.withAlpha(0.7),
            .quote_bg = fg.withAlpha(0.06),
            .rule = fg.withAlpha(0.18),
            .border = fg.withAlpha(0.22),
            .header_bg = fg.withAlpha(0.10),
            .zebra = fg.withAlpha(0.045),
            .accent = if (light) Color.hex(0x0066b8) else Color.hex(0x0078d4),
        };
    }
};

/// What a layout depends on besides the document and the width.
const Signature = struct {
    background: Color,
    foreground: Color,
    keyword: Color,
    ui_size: f32,
    code_size: f32,
    scale: f32,
    ui_family_hash: u64,
    code_family_hash: u64,

    fn eql(a: Signature, b: Signature) bool {
        return std.meta.eql(a, b);
    }
};

pub const View = struct {
    gpa: Allocator,
    /// The buffer being previewed (by id: it may close while this tab stays).
    source_id: u32,
    /// Its path, for relative links and images (owned).
    source_path: ?[]u8 = null,
    title_buf: [128]u8 = undefined,

    doc: ?tree.Doc = null,
    /// The buffer version `doc` was parsed from.
    doc_version: u64 = std.math.maxInt(u64),
    /// The source's version has moved on; parse again when the delay is up.
    stale_since_ms: ?i64 = null,
    source_closed: bool = false,
    truncated: bool = false,

    layout: ?Layout = null,
    layout_sig: ?Signature = null,
    rect: Rect = .{},

    scroll: f32 = 0,
    sel: ?Sel = null,
    dragging: bool = false,
    drag_thumb: ?f32 = null,
    /// Where the left button went down, to tell a click on a link from a drag.
    press: ?struct { pos: platform.Vec2, link: i32 } = null,
    hover_link: i32 = -1,

    images: std.StringHashMapUnmanaged(ImageSlot) = .empty,
    /// The scene the images live in (set when painted; it outlives the workspace).
    scene: ?*Scene = null,
    /// Grammars a code block asked for that were still loading.
    waiting: std.ArrayList(*const languages.LanguageConfig) = .empty,

    /// The editor's top source line the last time the two were in step.
    last_editor_top: f32 = -1,
    /// Set after a link or `Show Source` jump, to scroll once laid out.
    scroll_to_anchor: ?[]u8 = null,
    /// Scroll to this source line once laid out (the first layout, a jump).
    scroll_to_line: ?f32 = null,

    pub fn create(gpa: Allocator, source: *Buffer) !*View {
        const v = try gpa.create(View);
        v.* = .{ .gpa = gpa, .source_id = source.id };
        if (source.path) |p| v.source_path = try gpa.dupe(u8, p);
        return v;
    }

    pub fn destroy(self: *View) void {
        if (self.doc) |*d| d.deinit();
        if (self.layout) |*l| l.deinit();
        if (self.source_path) |p| self.gpa.free(p);
        if (self.scroll_to_anchor) |a| self.gpa.free(a);
        self.releaseImages(self.scene);
        self.images.deinit(self.gpa);
        self.waiting.deinit(self.gpa);
        self.gpa.destroy(self);
    }

    fn releaseImages(self: *View, scene: ?*Scene) void {
        var it = self.images.iterator();
        while (it.next()) |e| {
            if (scene) |s| if (e.value_ptr.id != 0) s.removeImage(e.value_ptr.id);
            self.gpa.free(e.key_ptr.*);
        }
        self.images.clearRetainingCapacity();
    }
};

// ---- Commands -------------------------------------------------------------------------------------

pub fn isMarkdownPath(path: []const u8) bool {
    const ext = std.fs.path.extension(path);
    for ([_][]const u8{ ".md", ".markdown", ".mdown", ".mkd", ".mdx" }) |e| if (std.ascii.eqlIgnoreCase(ext, e)) return true;
    return false;
}

/// The Markdown commands. Cmd+Shift+V is Paste and Indent in Sublime's keymap
/// and the preview in VS Code's: here it is the preview in Markdown files and
/// previews (as in cs-code) and Paste and Indent everywhere else.
pub fn perform(ws: *Workspace, action: keymap_mod.Action) bool {
    switch (action) {
        .markdown_open_preview => {
            if (!toggle(ws)) ws.setStatus("Open a Markdown file to preview it", .{});
            return true;
        },
        .markdown_open_preview_side => {
            openToSide(ws);
            return true;
        },
        .markdown_show_source => {
            if (active(ws)) |v| showSource(ws, v);
            return true;
        },
        .paste_and_indent => return ws.focus == .editor and ws.overlay == null and toggle(ws),
        else => return false,
    }
}

fn isMarkdown(ed: *Editor) bool {
    if (std.ascii.eqlIgnoreCase(ed.buffer.language.name, "markdown")) return true;
    if (ed.buffer.path) |p| return isMarkdownPath(p);
    return false;
}

fn viewOf(ws: *Workspace, ed: *Editor) ?*View {
    const i = ws.tabIndexOf(ed) orelse return null;
    return ws.tabs.items[i].markdown;
}

pub fn active(ws: *Workspace) ?*View {
    const i = ws.activeIndex() orelse return null;
    return ws.tabs.items[i].markdown;
}

fn bufferById(ws: *Workspace, id: u32) ?*Buffer {
    for (ws.buffers.items) |b| if (b.id == id) return b;
    return null;
}

/// The tab showing `buffer` as an editor (not as a preview), if any.
fn sourceTab(ws: *Workspace, buffer: *Buffer) ?usize {
    var best: ?usize = null;
    for (ws.tabs.items, 0..) |t, i| {
        if (t.markdown != null or t.editor.buffer != buffer) continue;
        // The one in view wins.
        if (ws.group_active[t.group] == t.editor) return i;
        best = best orelse i;
    }
    return best;
}

/// Markdown: Open Preview. From a Markdown file: its preview, as a tab of the
/// same group. From a preview: back to the source (Show Source).
pub fn toggle(ws: *Workspace) bool {
    if (active(ws)) |v| {
        showSource(ws, v);
        return true;
    }
    const ed = ws.activeEditor() orelse return false;
    if (!isMarkdown(ed)) return false;
    open(ws, ed, false);
    return true;
}

/// Markdown: Open Preview to the Side. The editor keeps the keyboard.
pub fn openToSide(ws: *Workspace) void {
    const ed = if (active(ws)) |v| (sourceEditorOf(ws, v) orelse return) else (ws.activeEditor() orelse return);
    if (!isMarkdown(ed)) {
        ws.setStatus("Open a Markdown file to preview it", .{});
        return;
    }
    open(ws, ed, true);
}

fn sourceEditorOf(ws: *Workspace, v: *View) ?*Editor {
    const b = bufferById(ws, v.source_id) orelse return null;
    const i = sourceTab(ws, b) orelse return null;
    return ws.tabs.items[i].editor;
}

/// Markdown: Show Source: from a preview to its editor.
pub fn showSource(ws: *Workspace, v: *View) void {
    if (bufferById(ws, v.source_id)) |b| if (sourceTab(ws, b)) |i| {
        ws.activateTab(i);
        ws.focus = .editor;
        return;
    };
    if (v.source_path) |p| _ = ws.openFile(p, false) catch {
        ws.setStatus("Unable to open {s}", .{p});
    };
}

fn open(ws: *Workspace, src: *Editor, side: bool) void {
    // One preview per file: show the one there is.
    for (ws.tabs.items, 0..) |t, i| if (t.markdown) |v| if (v.source_id == src.buffer.id) {
        const prev_group = ws.focused_group;
        ws.activateTab(i);
        if (side) ws.focusGroup(prev_group);
        ws.focus = .editor;
        return;
    };
    const home = ws.focused_group;
    const view = View.create(ws.gpa, src.buffer) catch return;
    view.scroll_to_line = editorTopLine(src);
    const buffer = ws.gpa.create(Buffer) catch return view.destroy();
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    ws.next_buffer_id += 1;
    const name = src.buffer.fileName() orelse "Untitled";
    buffer.display_name = std.fmt.bufPrint(&view.title_buf, "Preview {s}", .{name}) catch "Preview";
    buffer.read_only = true;
    ws.buffers.append(ws.gpa, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch return view.destroy();
    if (side) {
        // A group beside this one: the next, or a new column.
        var target: u8 = ws.focused_group;
        if (ws.groupCount() < 2) {
            if (!groups.addPane(ws)) {
                ws.setStatus("No room for a second pane", .{});
                ed.destroy();
                return view.destroy();
            }
            target = ws.focused_group;
        } else {
            target = (ws.focused_group + 1) % ws.groupCount();
        }
        ws.focused_group = target;
    }
    _ = ws.insertTab(.{ .editor = ed, .markdown = view }) catch {
        ed.destroy();
        return view.destroy();
    };
    if (side) ws.focusGroup(home);
    ws.focus = .editor;
    ws.updateTitle();
}

/// The README of a project folder when "markdown_open_readme" is set.
pub fn openReadme(ws: *Workspace) void {
    if (!ws.env.settings.markdown_open_readme) return;
    const root = (ws.project orelse return).root;
    const names = [_][]const u8{ "README.md", "Readme.md", "readme.md", "README.markdown", "README.MD" };
    for (names) |name| {
        const path = std.fs.path.join(ws.gpa, &.{ root, name }) catch return;
        defer ws.gpa.free(path);
        if (!exists(ws, path)) continue;
        const ed = ws.openFile(path, false) catch return;
        open(ws, ed, false);
        return;
    }
}

fn exists(ws: *Workspace, path: []const u8) bool {
    _ = vfs.stat(ws.io, path) catch return false;
    return true;
}

// ---- Source tracking --------------------------------------------------------------------------------

fn editorTopLine(ed: *Editor) f32 {
    const lh = ed.lineHeight();
    if (lh <= 0) return 0;
    const d = @max(0, ed.scroll_y / lh);
    const di: u32 = @intFromFloat(@floor(d));
    const seg = ed.segmentAt(di);
    return @as(f32, @floatFromInt(seg.row)) + (d - @floor(d));
}

fn setEditorTopLine(ed: *Editor, line: f32) void {
    const row: u32 = @intFromFloat(@max(0, @floor(line)));
    const frac = line - @floor(line);
    const d = ed.display.displayRow(ed.gpa, ed.buffer, @min(row, ed.buffer.lineCount() -| 1));
    ed.scroll_y = std.math.clamp((@as(f32, @floatFromInt(d)) + frac) * ed.lineHeight(), 0, ed.maxScrollY());
}

fn nowMs() i64 {
    return platform.nowMs();
}

/// Parses the source again when it changed (after a short delay).
fn refreshDoc(ws: *Workspace, v: *View) void {
    const buffer = bufferById(ws, v.source_id) orelse {
        v.source_closed = true;
        return;
    };
    v.source_closed = false;
    if (v.doc != null and buffer.version == v.doc_version) {
        v.stale_since_ms = null;
        return;
    }
    if (v.doc != null) {
        const now = nowMs();
        const since = v.stale_since_ms orelse blk: {
            v.stale_since_ms = now;
            break :blk now;
        };
        if (now - since < reparse_delay_ms) {
            ws.dirty = true;
            return;
        }
    }
    v.stale_since_ms = null;
    const len = buffer.len();
    const text = buffer.textRange(ws.gpa, 0, @min(len, max_source_bytes)) catch return;
    defer ws.gpa.free(text);
    const doc = md.parse(ws.gpa, text, .{ .max_bytes = max_source_bytes }) catch return;
    if (v.doc) |*d| d.deinit();
    v.doc = doc;
    v.doc_version = buffer.version;
    v.truncated = len > max_source_bytes;
    if (buffer.path) |p| {
        if (v.source_path == null or !std.mem.eql(u8, v.source_path.?, p)) {
            if (v.source_path) |old| v.gpa.free(old);
            v.source_path = v.gpa.dupe(u8, p) catch null;
        }
    }
    // Force a new layout.
    if (v.layout) |*l| l.deinit();
    v.layout = null;
}

// ---- Layout ----------------------------------------------------------------------------------------------------

const Style = struct {
    bold: bool = false,
    italic: bool = false,
    strike: bool = false,
    code: bool = false,
    link: i32 = -1,
    /// The font to start from instead of the body's (headings).
    base: ?FontId = null,
    color: ?Color = null,
    /// Text size in px, for the line height (0: the body's).
    size: f32 = 0,
    line_height: f32 = line_height_factor,
};

const AtomKind = enum { word, space, newline, image };

const Atom = struct {
    kind: AtomKind,
    text: []const u8 = "",
    font: FontId = 0,
    color: Color = .{ .r = 0, .g = 0, .b = 0, .a = 1 },
    underline: bool = false,
    strike: bool = false,
    bg: ?Color = null,
    link: i32 = -1,
    w: f32 = 0,
    asc: f32 = 0,
    desc: f32 = 0,
    lh: f32 = 0,
    pad_l: f32 = 0,
    pad_r: f32 = 0,
    slot: u32 = 0,
    img_h: f32 = 0,
};

const Ctx = struct {
    v: *View,
    ws: *Workspace,
    a: Allocator,
    tmp: Allocator,
    text: *TextSystem,
    theme: *const Theme,
    scene: *Scene,
    fonts: Fonts,
    pal: Palette,
    lay: *Layout,
    atoms: std.ArrayList(Atom) = .empty,
    /// The first run of a block has `.block` separation unless it is the first.
    pending_sep: Sep = .none,
    first_content: bool = true,
    base_dir: ?[]const u8,
    waiting_new: bool = false,
    /// List nesting, for the bullet shapes.
    depth: u32 = 0,
    /// What the first run of a list item's content is separated by.
    first_sep: Sep = .none,

    fn point(self: *Ctx, line: u32, y: f32) void {
        const pts = &self.lay.points;
        if (pts.items.len > 0) {
            const last = pts.items[pts.items.len - 1];
            // Keep both coordinates non-decreasing.
            if (line < last.line) return;
            if (line == last.line and y >= last.y) return;
            if (y < last.y) return;
        }
        pts.append(self.a, .{ .line = line, .y = y }) catch {};
    }

    fn addFill(self: *Ctx, f: Fill) void {
        self.lay.fills.append(self.a, f) catch {};
    }

    fn addGlyph(self: *Ctx, r: Run) void {
        self.lay.glyphs.append(self.a, r) catch {};
    }

    fn addRun(self: *Ctx, r: Run) u32 {
        var run = r;
        if (self.pending_sep != .none) {
            run.sep = self.pending_sep;
            self.pending_sep = .none;
        }
        self.lay.runs.append(self.a, run) catch return @intCast(self.lay.runs.items.len);
        return @intCast(self.lay.runs.items.len - 1);
    }

    fn metrics(self: *Ctx, font: FontId) text_mod.Metrics {
        return self.text.metrics(font);
    }

    fn measure(self: *Ctx, s: []const u8, font: FontId) f32 {
        return self.text.measure(s, font);
    }
};

const Margins = struct { top: f32, bottom: f32 };

fn marginsOf(b: Block, f: Fonts, tight: bool) Margins {
    const em = f.body_size;
    return switch (b.data) {
        .paragraph => .{ .top = 0, .bottom = if (tight) 2 else em },
        .heading => |h| switch (h.level) {
            1, 2 => .{ .top = em * 1.7, .bottom = em * 0.9 },
            else => .{ .top = em * 1.4, .bottom = em * 0.7 },
        },
        .code => .{ .top = 0, .bottom = em },
        .quote => .{ .top = 0, .bottom = em },
        .list => .{ .top = 0, .bottom = if (tight) em * 0.2 else em },
        .table => .{ .top = 0, .bottom = em },
        .rule => .{ .top = em * 0.9, .bottom = em * 0.9 },
        .html => .{ .top = 0, .bottom = em },
    };
}

const Flow = struct {
    ctx: *Ctx,
    x0: f32,
    width: f32,
    y: f32,
    align_: tree.Align = .none,
    /// Where the current line's runs start in the layout's run list.
    line_run_start: usize,
    cur_x: f32 = 0,
    line_asc: f32 = 0,
    line_desc: f32 = 0,
    line_h: f32 = 0,
    line_empty: bool = true,
    min_lh: f32,
    /// Width actually used by the widest line (for table sizing).
    used_w: f32 = 0,
    lines: u32 = 0,
    /// Where the current line's images start in the layout's image list.
    line_image_start: usize,

    fn finishLine(self: *Flow) void {
        const runs = self.ctx.lay.runs.items[self.line_run_start..];
        const h = @max(self.line_h, self.min_lh);
        const content = self.line_asc + self.line_desc;
        const baseline = self.y + (h - content) / 2 + self.line_asc;
        const free = self.width - self.cur_x;
        const shift: f32 = switch (self.align_) {
            .center => @max(0, free / 2),
            .right => @max(0, free),
            else => 0,
        };
        for (runs) |*r| {
            r.y = self.y;
            r.h = h;
            r.baseline = baseline;
            r.x += shift;
        }
        // Images sit on the baseline.
        for (self.ctx.lay.images.items[self.line_image_start..]) |*im| {
            im.rect.y = baseline - im.rect.h;
            im.rect.x += shift;
        }
        self.used_w = @max(self.used_w, self.cur_x);
        self.y += h;
        self.cur_x = 0;
        self.line_asc = 0;
        self.line_desc = 0;
        self.line_h = 0;
        self.line_empty = true;
        self.line_run_start = self.ctx.lay.runs.items.len;
        self.line_image_start = self.ctx.lay.images.items.len;
        self.lines += 1;
    }

    fn noteAtom(self: *Flow, a: Atom) void {
        self.line_asc = @max(self.line_asc, a.asc);
        self.line_desc = @max(self.line_desc, a.desc);
        self.line_h = @max(self.line_h, a.lh);
    }

    fn emit(self: *Flow, a: Atom) void {
        const ctx = self.ctx;
        const x = self.x0 + self.cur_x;
        switch (a.kind) {
            .image => {
                const box: ImageBox = .{ .rect = .{ .x = x, .y = 0, .w = a.w, .h = a.img_h }, .slot = a.slot };
                ctx.lay.images.append(ctx.a, box) catch {};
                // A run of its own, so a linked image can be clicked.
                _ = ctx.addRun(.{ .x = x, .w = a.w, .text = "", .font = a.font, .color = a.color, .link = a.link });
            },
            else => {
                _ = ctx.addRun(.{
                    .x = x,
                    .w = a.w,
                    .inset = a.pad_l,
                    .text = a.text,
                    .font = a.font,
                    .color = a.color,
                    .underline = a.underline,
                    .strike = a.strike,
                    .bg = a.bg,
                    .link = a.link,
                });
            },
        }
        self.cur_x += a.w;
        self.line_empty = false;
        self.noteAtom(a);
    }

    fn breakLine(self: *Flow, sep: Sep) void {
        if (self.line_empty) {
            // An empty line still takes its height.
            self.line_h = @max(self.line_h, self.min_lh);
        }
        self.finishLine();
        self.ctx.pending_sep = sep;
    }
};

/// Flattens inlines into atoms (words, spaces, breaks, images).
fn flatten(ctx: *Ctx, inlines: []const Inline, style: Style) void {
    for (inlines) |n| switch (n) {
        .text => |t| addText(ctx, t, style),
        .code => |t| addCode(ctx, t, style),
        .em => |c_| {
            var s = style;
            s.italic = true;
            flatten(ctx, c_, s);
        },
        .strong => |c_| {
            var s = style;
            s.bold = true;
            flatten(ctx, c_, s);
        },
        .strike => |c_| {
            var s = style;
            s.strike = true;
            flatten(ctx, c_, s);
        },
        .link => |l| {
            var s = style;
            ctx.lay.links.append(ctx.a, ctx.a.dupe(u8, l.url) catch "") catch {};
            s.link = @intCast(ctx.lay.links.items.len - 1);
            flatten(ctx, l.children, s);
        },
        .image => |im| addImage(ctx, im, style),
        .soft_break => ctx.atoms.append(ctx.tmp, spaceAtom(ctx, style)) catch {},
        .hard_break => ctx.atoms.append(ctx.tmp, .{ .kind = .newline }) catch {},
    };
}

fn fontFor(ctx: *Ctx, style: Style) FontId {
    if (style.code) return if (style.bold) ctx.fonts.code_bold else ctx.fonts.code;
    if (style.base) |b| {
        // Headings are bold already; italic variants come from the text system.
        return if (style.italic) ctx.text.styledFont(b, .bold_italic) else b;
    }
    return ctx.fonts.forStyle(style.bold, style.italic);
}

fn atomFor(ctx: *Ctx, style: Style, kind: AtomKind, text: []const u8) Atom {
    const font = fontFor(ctx, style);
    const m = ctx.metrics(font);
    const lh = if (style.code) m.line_height else @max(m.line_height, m.ascent + m.descent) * 1.0;
    var color = style.color orelse ctx.pal.fg;
    if (style.link >= 0) color = ctx.pal.link;
    return .{
        .kind = kind,
        .text = text,
        .font = font,
        .color = color,
        .underline = false,
        .strike = style.strike,
        .bg = if (style.code) ctx.pal.code_bg else null,
        .link = style.link,
        .asc = m.ascent,
        .desc = m.descent,
        .lh = lh,
    };
}

fn spaceAtom(ctx: *Ctx, style: Style) Atom {
    var a = atomFor(ctx, style, .space, " ");
    a.w = ctx.measure(" ", a.font);
    return a;
}

fn addText(ctx: *Ctx, t: []const u8, style: Style) void {
    var i: usize = 0;
    while (i < t.len) {
        if (t[i] == ' ' or t[i] == '\t' or t[i] == '\n') {
            while (i < t.len and (t[i] == ' ' or t[i] == '\t' or t[i] == '\n')) i += 1;
            ctx.atoms.append(ctx.tmp, spaceAtom(ctx, style)) catch return;
            continue;
        }
        const start = i;
        while (i < t.len and t[i] != ' ' and t[i] != '\t' and t[i] != '\n') i += 1;
        var a = atomFor(ctx, style, .word, ctx.a.dupe(u8, t[start..i]) catch return);
        a.w = ctx.measure(a.text, a.font);
        ctx.atoms.append(ctx.tmp, a) catch return;
    }
}

fn addCode(ctx: *Ctx, t: []const u8, style_in: Style) void {
    var style = style_in;
    style.code = true;
    const pad: f32 = 3;
    var words = std.mem.tokenizeAny(u8, t, " \t\n");
    var first = true;
    var count: usize = 0;
    // Word atoms, each with the span's background; the spaces between carry it too.
    const first_atom = ctx.atoms.items.len;
    while (words.next()) |w| {
        if (!first) {
            var sp = spaceAtom(ctx, style);
            sp.link = style.link;
            ctx.atoms.append(ctx.tmp, sp) catch return;
        }
        var a = atomFor(ctx, style, .word, ctx.a.dupe(u8, w) catch return);
        a.w = ctx.measure(a.text, a.font);
        ctx.atoms.append(ctx.tmp, a) catch return;
        first = false;
        count += 1;
    }
    if (count == 0) return;
    const items = ctx.atoms.items;
    items[first_atom].pad_l = pad;
    items[first_atom].w += pad;
    items[items.len - 1].pad_r = pad;
    items[items.len - 1].w += pad;
}

fn addImage(ctx: *Ctx, im: Inline.Image, style: Style) void {
    const slot_index = imageSlot(ctx, im.url);
    const slot = slotAt(ctx, slot_index);
    if (slot.state == .ready) {
        const avail = @max(40, ctx.v.rect.w - 2 * page_pad_x);
        var w: f32 = @floatFromInt(slot.width);
        var h: f32 = @floatFromInt(slot.height);
        if (w > avail) {
            h *= avail / w;
            w = avail;
        }
        const font = fontFor(ctx, style);
        ctx.atoms.append(ctx.tmp, .{
            .kind = .image,
            .font = font,
            .color = ctx.pal.fg,
            .w = w,
            .img_h = h,
            .asc = h,
            .desc = 0,
            .lh = h,
            .slot = slot_index,
            .link = style.link,
        }) catch {};
        return;
    }
    // The alt text stands in.
    var alt: std.ArrayList(u8) = .empty;
    tree.plainText(ctx.tmp, &alt, im.alt) catch return;
    var s = style;
    s.italic = true;
    s.color = ctx.pal.muted;
    const text = if (alt.items.len > 0) alt.items else if (slot.state == .blocked) "(remote image blocked)" else "(image)";
    addText(ctx, text, s);
}

/// Index into `v.images` order for `url` (loads the image on first sight).
fn imageSlot(ctx: *Ctx, url: []const u8) u32 {
    const v = ctx.v;
    if (!v.images.contains(url)) {
        const key = v.gpa.dupe(u8, url) catch return 0;
        v.images.put(v.gpa, key, loadImage(ctx, url)) catch {
            v.gpa.free(key);
            return 0;
        };
    }
    // The slot's position in iteration order is not stable; hash it instead.
    return @truncate(std.hash.Wyhash.hash(0, url));
}

fn slotAt(ctx: *Ctx, hash: u32) ImageSlot {
    var it = ctx.v.images.iterator();
    while (it.next()) |e| if (@as(u32, @truncate(std.hash.Wyhash.hash(0, e.key_ptr.*))) == hash) return e.value_ptr.*;
    return .{ .state = .failed };
}

fn loadImage(ctx: *Ctx, url: []const u8) ImageSlot {
    const gpa = ctx.v.gpa;
    var bytes: ?[]u8 = null;
    defer if (bytes) |b| gpa.free(b);
    if (std.mem.startsWith(u8, url, "data:")) {
        const comma = std.mem.indexOfScalar(u8, url, ',') orelse return .{ .state = .failed };
        const head = url[5..comma];
        if (std.mem.indexOf(u8, head, ";base64") == null) return .{ .state = .failed };
        const body = url[comma + 1 ..];
        const size = std.base64.standard.Decoder.calcSizeForSlice(body) catch return .{ .state = .failed };
        if (size > max_image_file) return .{ .state = .failed };
        const out = gpa.alloc(u8, size) catch return .{ .state = .failed };
        std.base64.standard.Decoder.decode(out, body) catch {
            gpa.free(out);
            return .{ .state = .failed };
        };
        bytes = out;
    } else if (std.ascii.startsWithIgnoreCase(url, "http://") or std.ascii.startsWithIgnoreCase(url, "https://")) {
        // A fetched image tells its server the file was opened.
        return .{ .state = .blocked };
    } else {
        const path = resolveRelative(gpa, ctx.base_dir, stripFragment(url)) catch return .{ .state = .failed };
        defer gpa.free(path);
        bytes = vfs.readFile(gpa, ctx.ws.io, path, max_image_file) catch return .{ .state = .failed };
    }
    const img = image_decode.decode(gpa, bytes.?, max_image_width) catch return .{ .state = .failed };
    defer gpa.free(img.pixels);
    const id = ctx.scene.addImage(img.width, img.height, img.pixels) catch return .{ .state = .failed };
    return .{ .id = id, .width = img.width, .height = img.height, .state = .ready };
}

fn stripFragment(url: []const u8) []const u8 {
    const hash = std.mem.indexOfScalar(u8, url, '#') orelse return url;
    return url[0..hash];
}

/// `target` made absolute against `base_dir` (a `file:` prefix and %20 style
/// escapes are resolved; the caller frees the result).
pub fn resolveRelative(gpa: Allocator, base_dir: ?[]const u8, target_in: []const u8) ![]u8 {
    var target = target_in;
    if (std.ascii.startsWithIgnoreCase(target, "file://")) target = target[7..];
    const decoded = try percentDecode(gpa, target);
    defer gpa.free(decoded);
    if (std.fs.path.isAbsolute(decoded) or rpath.isRemote(decoded)) return gpa.dupe(u8, decoded);
    if (std.mem.startsWith(u8, decoded, "~/")) {
        if (std.c.getenv("HOME")) |h| return std.mem.concat(gpa, u8, &.{ std.mem.span(h), decoded[1..] });
    }
    const base = base_dir orelse return gpa.dupe(u8, decoded);
    // Remote bases keep their ssh://host prefix: join with a slash.
    if (rpath.isRemote(base)) return std.mem.concat(gpa, u8, &.{ base, if (std.mem.endsWith(u8, base, "/")) "" else "/", decoded });
    const joined = try std.fs.path.join(gpa, &.{ base, decoded });
    defer gpa.free(joined);
    return std.fs.path.resolve(gpa, &.{joined});
}

pub fn percentDecode(gpa: Allocator, s: []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    var i: usize = 0;
    while (i < s.len) : (i += 1) {
        if (s[i] == '%' and i + 2 < s.len + 0 and i + 2 <= s.len - 1 and std.ascii.isHex(s[i + 1]) and std.ascii.isHex(s[i + 2])) {
            try out.append(gpa, std.fmt.parseInt(u8, s[i + 1 .. i + 3], 16) catch s[i]);
            i += 2;
        } else try out.append(gpa, s[i]);
    }
    return out.toOwnedSlice(gpa);
}

/// Lays `inlines` out in a box `width` wide starting at (`x0`, `y`); returns
/// the y below the last line. `used` gets the width of the widest line.
fn layoutInlines(ctx: *Ctx, inlines: []const Inline, style: Style, x0: f32, width: f32, y: f32, align_: tree.Align, used: ?*f32) f32 {
    ctx.atoms.clearRetainingCapacity();
    flatten(ctx, inlines, style);
    const px = if (style.size > 0) style.size else ctx.fonts.body_size;
    return layoutAtoms(ctx, x0, width, y, align_, used, px * style.line_height);
}

fn layoutAtoms(ctx: *Ctx, x0: f32, width: f32, y: f32, align_: tree.Align, used: ?*f32, min_lh: f32) f32 {
    var flow: Flow = .{
        .ctx = ctx,
        .x0 = x0,
        .width = width,
        .y = y,
        .align_ = align_,
        .line_run_start = ctx.lay.runs.items.len,
        .line_image_start = ctx.lay.images.items.len,
        .min_lh = min_lh,
    };
    const atoms = ctx.atoms.items;
    var pending_space: ?Atom = null;
    var i: usize = 0;
    while (i < atoms.len) {
        const a = atoms[i];
        switch (a.kind) {
            .newline => {
                flow.breakLine(.line);
                pending_space = null;
                i += 1;
            },
            .space => {
                if (!flow.line_empty) pending_space = a;
                i += 1;
            },
            .word, .image => {
                var j = i;
                var cw: f32 = 0;
                while (j < atoms.len and (atoms[j].kind == .word or atoms[j].kind == .image)) : (j += 1) cw += atoms[j].w;
                const sw: f32 = if (pending_space) |s| s.w else 0;
                if (!flow.line_empty and flow.cur_x + sw + cw > width) {
                    flow.breakLine(.space);
                    pending_space = null;
                }
                if (pending_space) |s| {
                    if (!flow.line_empty) flow.emit(s);
                    pending_space = null;
                }
                var k = i;
                while (k < j) : (k += 1) flow.emit(atoms[k]);
                i = j;
            },
        }
    }
    if (!flow.line_empty) flow.finishLine() else if (flow.lines == 0) {
        flow.line_h = flow.min_lh;
        flow.finishLine();
    }
    if (used) |u| u.* = @max(u.*, flow.used_w);
    return flow.y;
}

// ---- Block layout ------------------------------------------------------------------------------------------

fn layoutBlocks(ctx: *Ctx, blocks: []const Block, x: f32, w: f32, y_in: f32, tight: bool) f32 {
    var y = y_in;
    var prev: f32 = 0;
    for (blocks, 0..) |b, i| {
        const mg = marginsOf(b, ctx.fonts, tight);
        if (i > 0) y += @max(prev, mg.top);
        y = layoutBlock(ctx, b, x, w, y, tight);
        prev = mg.bottom;
    }
    return y;
}

fn layoutBlock(ctx: *Ctx, b: Block, x: f32, w: f32, y_in: f32, tight: bool) f32 {
    var y = y_in;
    ctx.point(b.start_line, y);
    ctx.pending_sep = if (ctx.first_content) ctx.first_sep else .block;
    ctx.first_content = false;
    switch (b.data) {
        .paragraph => |ins| y = layoutInlines(ctx, ins, .{}, x, w, y, .none, null),
        .heading => |h| y = layoutHeading(ctx, h, x, w, y),
        .code => |cb| y = layoutCode(ctx, cb, b, x, w, y),
        .quote => |children| y = layoutQuote(ctx, children, x, w, y),
        .list => |l| y = layoutList(ctx, l, x, w, y),
        .table => |t| y = layoutTable(ctx, t, x, w, y),
        .rule => {
            ctx.addFill(.{ .rect = .{ .x = x, .y = y, .w = w, .h = 1 }, .color = ctx.pal.border });
            y += 1;
        },
        .html => |html| y = layoutHtml(ctx, html, x, w, y),
    }
    _ = tight;
    ctx.point(b.end_line + 1, y);
    return y;
}

fn layoutHeading(ctx: *Ctx, h: Block.Heading, x: f32, w: f32, y_in: f32) f32 {
    const level: usize = @min(@max(h.level, 1), 6) - 1;
    ctx.lay.anchors.append(ctx.a, .{ .id = h.id, .y = y_in }) catch {};
    const style: Style = .{ .base = ctx.fonts.headings[level], .bold = true, .size = ctx.fonts.body_size * heading_em[level], .line_height = 1.3 };
    var y = layoutInlines(ctx, h.inlines, style, x, w, y_in, .none, null);
    if (level < 2) {
        // A rule under the first two levels.
        y += 5;
        ctx.addFill(.{ .rect = .{ .x = x, .y = y, .w = w, .h = 1 }, .color = ctx.pal.border });
        y += 1;
    }
    return y;
}

fn layoutHtml(ctx: *Ctx, html: []const u8, x: f32, w: f32, y: f32) f32 {
    // No HTML engine: the markup shows as muted text.
    var lines = std.mem.splitScalar(u8, html, '\n');
    var cur = y;
    const font = ctx.fonts.code;
    const m = ctx.metrics(font);
    while (lines.next()) |line| {
        const trimmed = std.mem.trimEnd(u8, line, " \t\r");
        const text = ctx.a.dupe(u8, trimmed) catch return cur;
        _ = ctx.addRun(.{
            .x = x,
            .y = cur,
            .w = @min(w, ctx.measure(text, font)),
            .h = m.line_height * 1.2,
            .baseline = cur + m.ascent + (m.line_height * 1.2 - m.ascent - m.descent) / 2,
            .text = text,
            .font = font,
            .color = ctx.pal.muted,
            .sep = if (cur == y) .none else .line,
        });
        cur += m.line_height * 1.2;
    }
    return cur;
}

fn layoutQuote(ctx: *Ctx, children: []const Block, x: f32, w: f32, y: f32) f32 {
    const fills_before = ctx.lay.fills.items.len;
    const pad: f32 = 6;
    const end = layoutBlocks(ctx, children, x + quote_indent, w - quote_indent, y + pad, false);
    // A bar and a tint, behind the children's own fills.
    const end_y = end + pad;
    ctx.lay.fills.insert(ctx.a, fills_before, .{ .rect = .{ .x = x, .y = y, .w = w, .h = end_y - y }, .color = ctx.pal.quote_bg }) catch {};
    ctx.lay.fills.insert(ctx.a, fills_before + 1, .{ .rect = .{ .x = x, .y = y, .w = 4, .h = end_y - y }, .color = ctx.pal.quote_bar }) catch {};
    return end_y;
}

fn layoutList(ctx: *Ctx, l: Block.List, x: f32, w: f32, y_in: f32) f32 {
    var y = y_in;
    const body = ctx.fonts.body;
    const gutter = list_indent;
    const m = ctx.metrics(body);
    for (l.items, 0..) |item, idx| {
        if (idx > 0) y += if (l.tight) ctx.fonts.body_size * 0.2 else ctx.fonts.body_size * 0.6;
        ctx.point(item.start_line, y);
        const item_top = y;
        // Items after the first start a new line when copied.
        const item_sep: Sep = if (idx == 0) (if (ctx.first_content) ctx.first_sep else .block) else .line;
        ctx.first_content = true;
        ctx.first_sep = item_sep;
        ctx.depth += 1;
        const content_start = ctx.lay.runs.items.len;
        y = layoutBlocks(ctx, item.blocks, x + gutter, w - gutter, y, l.tight);
        ctx.depth -= 1;
        ctx.first_sep = .none;
        ctx.first_content = false;
        if (item.blocks.len == 0) y += ctx.fonts.body_size * line_height_factor;
        // The marker goes where the item's first line landed.
        const base = firstBaselineAfter(ctx, content_start, item_top, m);
        if (item.task) |done| {
            const size: f32 = @round(ctx.fonts.body_size * 0.95);
            const box: Rect = .{ .x = x + gutter - size - 8, .y = base - size + 2, .w = size, .h = size };
            ctx.addFill(.{
                .rect = box,
                .color = if (done) ctx.pal.accent else .transparent,
                .border = if (done) ctx.pal.accent else ctx.pal.muted,
                .border_w = 1,
                .radius = 3,
            });
            if (done) {
                const font = ctx.fonts.bold;
                const cm = ctx.metrics(font);
                const cw = ctx.measure("✓", font);
                ctx.addGlyph(.{
                    .x = box.x + (box.w - cw) / 2,
                    .y = box.y,
                    .w = cw,
                    .h = box.h,
                    .baseline = box.y + (box.h - (cm.ascent + cm.descent)) / 2 + cm.ascent,
                    .text = "✓",
                    .font = font,
                    .color = .{ .r = 1, .g = 1, .b = 1, .a = 1 },
                });
            }
        } else {
            const marker = if (l.ordered) (std.fmt.allocPrint(ctx.a, "{d}.", .{l.start + @as(u32, @intCast(idx))}) catch "•") else bulletFor(ctx.depth);
            const mw = ctx.measure(marker, body);
            ctx.addGlyph(.{
                .x = x + gutter - 8 - mw,
                .y = base - m.ascent,
                .w = mw,
                .h = m.line_height,
                .baseline = base,
                .text = marker,
                .font = body,
                .color = if (l.ordered) ctx.pal.muted else ctx.pal.fg,
            });
        }
    }
    return y;
}

fn bulletFor(depth: u32) []const u8 {
    return switch (depth % 3) {
        0 => "•",
        1 => "◦",
        else => "▪",
    };
}

/// The baseline of the first text line laid out after run `from`.
fn firstBaselineAfter(ctx: *Ctx, from: usize, top: f32, m: text_mod.Metrics) f32 {
    for (ctx.lay.runs.items[from..]) |r| {
        if (r.text.len > 0 and r.h > 0) return r.baseline;
    }
    return top + (ctx.fonts.body_size * line_height_factor - (m.ascent + m.descent)) / 2 + m.ascent;
}

fn layoutCode(ctx: *Ctx, cb: Block.Code, b: Block, x: f32, w: f32, y_in: f32) f32 {
    const font = ctx.fonts.code;
    const m = ctx.metrics(font);
    const lh = m.line_height * 1.25;
    // Tabs become spaces so the highlighter's offsets match what is drawn.
    const text = expandTabs(ctx.a, std.mem.trimEnd(u8, cb.text, "\n")) catch cb.text;
    var lines_it = std.mem.splitScalar(u8, text, '\n');
    var line_count: usize = 1;
    for (text) |ch| if (ch == '\n') {
        line_count += 1;
    };
    if (text.len == 0) line_count = 0;
    const height = @as(f32, @floatFromInt(line_count)) * lh + 2 * code_pad;
    const block_y = y_in;
    ctx.addFill(.{ .rect = .{ .x = x, .y = block_y, .w = w, .h = height }, .color = ctx.pal.block_bg, .radius = 4 });
    var per_byte: ?[]u32 = null;
    var hl_spans: []const syntax_mod.HighlightSpan = &.{};
    if (cb.lang.len > 0 and !cb.front_matter and text.len > 0 and text.len <= max_highlight_bytes) {
        switch (highlightCode(ctx, cb.lang, text, &hl_spans)) {
            .spans, .plain => {},
            .loading => ctx.waiting_new = true,
        }
    }
    if (hl_spans.len > 0) {
        const pb = ctx.tmp.alloc(u32, text.len) catch null;
        if (pb) |arr| {
            @memset(arr, 0);
            for (hl_spans, 0..) |s, si| {
                const e = @min(s.end, arr.len);
                if (s.start < e) @memset(arr[s.start..e], @intCast(si + 1));
            }
            per_byte = arr;
        }
    }
    var y = block_y + code_pad;
    var offset: usize = 0;
    var i: usize = 0;
    var first = true;
    while (lines_it.next()) |line| : (i += 1) {
        ctx.point(b.start_line + @as(u32, @intCast(@min(i + 1, 1 << 30))), y);
        const spans: []const ColorSpan = if (per_byte) |pb| lineSpans(ctx.a, pb, hl_spans, offset, line.len, ctx.pal.fg) else &.{};
        const lw = ctx.measure(line, font);
        _ = ctx.addRun(.{
            .x = x + code_pad,
            .y = y,
            .w = lw,
            .h = lh,
            .baseline = y + (lh - (m.ascent + m.descent)) / 2 + m.ascent,
            .text = line,
            .font = font,
            .color = if (cb.front_matter) ctx.pal.muted else ctx.pal.fg,
            .spans = spans,
            .sep = if (first) (if (ctx.pending_sep != .none) ctx.pending_sep else .none) else .line,
        });
        if (first) ctx.pending_sep = .none;
        first = false;
        y += lh;
        offset += line.len + 1;
    }
    ctx.pending_sep = .none;
    return block_y + height;
}

fn expandTabs(a: Allocator, s: []const u8) ![]const u8 {
    if (std.mem.indexOfScalar(u8, s, '\t') == null) return a.dupe(u8, s);
    var out: std.ArrayList(u8) = .empty;
    var col: usize = 0;
    for (s) |ch| {
        if (ch == '\t') {
            const n = tab_width - col % tab_width;
            try out.appendNTimes(a, ' ', n);
            col += n;
        } else {
            try out.append(a, ch);
            col = if (ch == '\n') 0 else col + 1;
        }
    }
    return out.toOwnedSlice(a);
}

/// Run-length colours of one code line from per-byte span indexes.
fn lineSpans(a: Allocator, per_byte: []const u32, spans: []const syntax_mod.HighlightSpan, start: usize, len: usize, default: Color) []const ColorSpan {
    var out: std.ArrayList(ColorSpan) = .empty;
    var i: usize = 0;
    while (i < len) {
        const idx = if (start + i < per_byte.len) per_byte[start + i] else 0;
        var j = i + 1;
        while (j < len and (if (start + j < per_byte.len) per_byte[start + j] else 0) == idx) j += 1;
        const color = if (idx == 0) default else spans[idx - 1].color;
        const style: text_mod.FontStyle = if (idx == 0) .normal else spans[idx - 1].style;
        out.append(a, .{ .end = @intCast(j), .color = color, .style = style }) catch return &.{};
        i = j;
    }
    return out.items;
}

const HighlightResult = enum { spans, loading, plain };

/// Highlights `text` as `lang`: the spans come back in the layout's arena.
fn highlightCode(ctx: *Ctx, lang: []const u8, text: []const u8, out: *[]const syntax_mod.HighlightSpan) HighlightResult {
    const config = languages.forInjection(lang) orelse languages.byName(lang) orelse return .plain;
    if (config.grammar == null) return .plain;
    const language = ctx.ws.registry.request(config) orelse {
        ctx.v.waiting.append(ctx.v.gpa, config) catch {};
        return .loading;
    };
    const gpa = ctx.v.gpa;
    var rope = Rope.fromSlice(gpa, text) catch return .plain;
    defer rope.deinit(gpa);
    var syn = syntax_mod.Syntax.init(gpa, language) catch return .plain;
    defer syn.deinit();
    const parsed = c.ts_parser_parse(syn.parser, null, syntax_mod.Syntax.ropeInput(&rope)) orelse return .plain;
    defer c.ts_tree_delete(parsed);
    const ranges = [_]c.TSRange{.{
        .start_point = .{ .row = 0, .column = 0 },
        .end_point = .{ .row = rope.lineCount(), .column = 0 },
        .start_byte = 0,
        .end_byte = @intCast(text.len),
    }};
    var found: std.ArrayList(syntax_mod.HighlightSpan) = .empty;
    defer found.deinit(gpa);
    syn.highlightTree(language, parsed, &ranges, &rope, 0, text.len, &found);
    out.* = ctx.a.dupe(syntax_mod.HighlightSpan, found.items) catch return .plain;
    return .spans;
}

// ---- Tables -----------------------------------------------------------------------------------------------------------

fn layoutTable(ctx: *Ctx, t: Block.Table, x: f32, w: f32, y_in: f32) f32 {
    const n = t.aligns.len;
    if (n == 0) return y_in;
    const rows_total = 1 + t.rows.len;
    const nat = ctx.tmp.alloc(f32, n) catch return y_in;
    const min = ctx.tmp.alloc(f32, n) catch return y_in;
    @memset(nat, 0);
    @memset(min, 0);
    // Natural and minimum widths of every column.
    for (0..rows_total) |ri| {
        const row: tree.Row = if (ri == 0) t.header else t.rows[ri - 1];
        for (0..n) |ci| {
            const cell = if (ci < row.len) row[ci] else &[_]Inline{};
            const style: Style = .{ .bold = ri == 0 };
            ctx.atoms.clearRetainingCapacity();
            flatten(ctx, cell, style);
            var total: f32 = 0;
            var longest: f32 = 0;
            var run: f32 = 0;
            for (ctx.atoms.items) |a| switch (a.kind) {
                .word, .image => {
                    total += a.w;
                    run += a.w;
                    longest = @max(longest, run);
                },
                .space => {
                    total += a.w;
                    run = 0;
                },
                .newline => run = 0,
            };
            nat[ci] = @max(nat[ci], total + 2 * cell_pad_x);
            min[ci] = @max(min[ci], longest + 2 * cell_pad_x);
        }
    }
    var sum_nat: f32 = 0;
    var sum_min: f32 = 0;
    for (0..n) |i| {
        sum_nat += nat[i];
        sum_min += min[i];
    }
    const widths = ctx.tmp.alloc(f32, n) catch return y_in;
    if (sum_nat <= w) {
        @memcpy(widths, nat);
    } else if (sum_min >= w) {
        @memcpy(widths, min);
    } else {
        const extra = w - sum_min;
        var denom: f32 = 0;
        for (0..n) |i| denom += nat[i] - min[i];
        for (0..n) |i| widths[i] = min[i] + (if (denom > 0) extra * (nat[i] - min[i]) / denom else 0);
    }
    var table_w: f32 = 0;
    for (widths) |cw| table_w += cw;

    var y = y_in;
    const fills_start = ctx.lay.fills.items.len;
    _ = fills_start;
    for (0..rows_total) |ri| {
        const row: tree.Row = if (ri == 0) t.header else t.rows[ri - 1];
        const row_top = y;
        var row_h: f32 = 0;
        // Lay the cells out to learn the row's height.
        const run_before = ctx.lay.runs.items.len;
        var cx = x;
        for (0..n) |ci| {
            const cell = if (ci < row.len) row[ci] else &[_]Inline{};
            const style: Style = .{ .bold = ri == 0 };
            ctx.pending_sep = if (ci == 0 and ri == 0) (if (ctx.pending_sep != .none) ctx.pending_sep else .none) else if (ci == 0) .line else .space;
            const end = layoutInlines(ctx, cell, style, cx + cell_pad_x, @max(10, widths[ci] - 2 * cell_pad_x), row_top + cell_pad_y, t.aligns[ci], null);
            row_h = @max(row_h, end - row_top + cell_pad_y);
            cx += widths[ci];
        }
        _ = run_before;
        // Background, then the cell borders.
        if (ri == 0) {
            ctx.addFill(.{ .rect = .{ .x = x, .y = row_top, .w = table_w, .h = row_h }, .color = ctx.pal.header_bg });
        } else if (ri % 2 == 0) {
            ctx.addFill(.{ .rect = .{ .x = x, .y = row_top, .w = table_w, .h = row_h }, .color = ctx.pal.zebra });
        }
        ctx.addFill(.{ .rect = .{ .x = x, .y = row_top, .w = table_w, .h = 1 }, .color = ctx.pal.border });
        y = row_top + row_h;
    }
    ctx.addFill(.{ .rect = .{ .x = x, .y = y, .w = table_w, .h = 1 }, .color = ctx.pal.border });
    var vx = x;
    for (0..n + 1) |ci| {
        ctx.addFill(.{ .rect = .{ .x = vx, .y = y_in, .w = 1, .h = y - y_in }, .color = ctx.pal.border });
        if (ci < n) vx += widths[ci];
    }
    return y + 1;
}

// ---- Building the layout ------------------------------------------------------------------------------------------------

fn signatureOf(ws: *Workspace) Signature {
    const s = ws.env.settings;
    const t = ws.theme;
    return .{
        .background = t.background,
        .foreground = t.foreground,
        .keyword = t.syntax.keyword,
        .ui_size = s.ui_font_size,
        .code_size = s.font_size,
        .scale = ws.env.text.scale,
        .ui_family_hash = std.hash.Wyhash.hash(0, s.ui_font_family),
        .code_family_hash = std.hash.Wyhash.hash(0, s.font_family),
    };
}

fn makeFonts(ws: *Workspace) Fonts {
    const s = ws.env.settings;
    const text = ws.env.text;
    const body_size = s.ui_font_size + 1;
    const code_size = @round(body_size * code_size_factor);
    const body = text.font(.{ .family = s.ui_font_family, .size = body_size }) catch ws.fonts.ui;
    const code = text.font(.{ .family = s.font_family, .size = code_size }) catch ws.fonts.mono;
    var headings: [6]FontId = undefined;
    for (&headings, 0..) |*h, i| {
        h.* = text.font(.{ .family = s.ui_font_family, .size = @round(body_size * heading_em[i]), .weight = .bold }) catch ws.fonts.ui_bold;
    }
    return .{
        .body = body,
        .bold = text.styledFont(body, .bold),
        .italic = text.styledFont(body, .italic),
        .bold_italic = text.styledFont(body, .bold_italic),
        .code = code,
        .code_bold = text.styledFont(code, .bold),
        .headings = headings,
        .body_size = body_size,
        .code_size = code_size,
    };
}

fn buildLayout(ws: *Workspace, v: *View, scene: *Scene, width: f32) void {
    const doc = v.doc orelse return;
    if (v.layout) |*l| l.deinit();
    v.layout = .{ .arena = std.heap.ArenaAllocator.init(v.gpa) };
    const lay = &v.layout.?;
    lay.width = width;
    var tmp_arena = std.heap.ArenaAllocator.init(v.gpa);
    defer tmp_arena.deinit();
    v.waiting.clearRetainingCapacity();
    const base_dir: ?[]const u8 = if (v.source_path) |p| (if (rpath.isRemote(p)) remoteDir(p) else std.fs.path.dirname(p)) else if (ws.project) |p| p.root else null;
    var ctx: Ctx = .{
        .v = v,
        .ws = ws,
        .a = lay.arena.allocator(),
        .tmp = tmp_arena.allocator(),
        .text = ws.env.text,
        .theme = ws.theme,
        .scene = scene,
        .fonts = makeFonts(ws),
        .pal = Palette.of(ws.theme),
        .lay = lay,
        .base_dir = base_dir,
    };
    const content_w = @max(60, width - 2 * page_pad_x - scrollbar_w);
    var y: f32 = page_pad_top;
    var prev: f32 = 0;
    ctx.point(0, 0);
    for (doc.blocks, 0..) |b, i| {
        const mg = marginsOf(b, ctx.fonts, false);
        if (i > 0) y += @max(prev, mg.top);
        const top = y;
        const fills = lay.fills.items.len;
        const runs = lay.runs.items.len;
        const glyphs = lay.glyphs.items.len;
        const images = lay.images.items.len;
        y = layoutBlock(&ctx, b, page_pad_x, content_w, y, false);
        lay.blocks.append(ctx.a, .{
            .top = top,
            .bottom = y,
            .fills = .{ .start = @intCast(fills), .end = @intCast(lay.fills.items.len) },
            .runs = .{ .start = @intCast(runs), .end = @intCast(lay.runs.items.len) },
            .glyphs = .{ .start = @intCast(glyphs), .end = @intCast(lay.glyphs.items.len) },
            .images = .{ .start = @intCast(images), .end = @intCast(lay.images.items.len) },
        }) catch {};
        prev = mg.bottom;
    }
    lay.height = y + page_pad_bottom;
    v.layout_sig = signatureOf(ws);
}

fn remoteDir(path: []const u8) ?[]const u8 {
    const slash = std.mem.lastIndexOfScalar(u8, path, '/') orelse return null;
    return path[0..slash];
}

// ---- Painting --------------------------------------------------------------------------------------------------------------------

/// Draws the preview in `rect` if `ed` is a preview tab's editor.
pub fn paint(ws: *Workspace, ed: *Editor, rect: Rect, scene: *Scene) bool {
    const v = viewOf(ws, ed) orelse return false;
    v.scene = scene;
    const theme = ws.theme;
    scene.fill(rect, theme.background);
    scene.pushClip(rect) catch return true;
    defer scene.popClip();

    const width_changed = if (v.layout) |l| @abs(l.width - rect.w) > 0.5 else false;
    v.rect = rect;
    refreshDoc(ws, v);
    // A grammar a code block waited for has loaded: lay out again.
    var relayout = v.layout == null or width_changed;
    if (v.layout_sig) |sig| if (!sig.eql(signatureOf(ws))) {
        relayout = true;
    };
    for (v.waiting.items) |cfg| if (ws.registry.request(cfg) != null) {
        relayout = true;
    };
    if (v.doc == null) {
        // Nothing to show yet (or the source is gone).
        const m = ws.env.text.metrics(ws.fonts.ui);
        _ = ws.env.text.drawText(scene, if (v.source_closed) "The file is closed." else "Loading…", ws.fonts.ui, rect.x + page_pad_x, rect.y + page_pad_top + m.ascent, theme.list_foreground_dim);
        return true;
    }
    if (relayout) {
        const keep_line: ?f32 = if (v.layout) |*l| (if (v.scroll_to_line == null) l.lineForY(v.scroll) else null) else null;
        buildLayout(ws, v, scene, rect.w);
        if (keep_line) |ln| v.scroll = v.layout.?.yForLine(ln);
    }
    const lay = &v.layout.?;
    if (v.scroll_to_line) |ln| {
        v.scroll = lay.yForLine(ln);
        v.scroll_to_line = null;
    }
    if (v.scroll_to_anchor) |id| {
        if (anchorY(lay, id)) |y| v.scroll = y - 8;
        v.gpa.free(id);
        v.scroll_to_anchor = null;
    }
    v.scroll = std.math.clamp(v.scroll, 0, @max(0, lay.height - rect.h));
    followEditor(ws, v);

    const ox = rect.x;
    const oy = rect.y - v.scroll;
    const top = v.scroll - 40;
    const bottom = v.scroll + rect.h + 40;
    const ctx_text = ws.env.text;
    const pal = Palette.of(theme);
    const sel_color = if (ed.focused) theme.selection else theme.inactive_selection;

    // The blocks in view.
    var first: usize = 0;
    var last: usize = 0;
    {
        var lo: usize = 0;
        var hi: usize = lay.blocks.items.len;
        while (lo < hi) {
            const mid = (lo + hi) / 2;
            if (lay.blocks.items[mid].bottom < top) lo = mid + 1 else hi = mid;
        }
        first = lo;
        last = lo;
        while (last < lay.blocks.items.len and lay.blocks.items[last].top <= bottom) last += 1;
    }
    for (lay.blocks.items[first..last]) |blk| {
        for (lay.fills.items[blk.fills.start..blk.fills.end]) |f| {
            scene.quad(.{ .x = ox + f.rect.x, .y = oy + f.rect.y, .w = f.rect.w, .h = f.rect.h }, .{
                .color = f.color,
                .border_color = f.border,
                .border_width = f.border_w,
                .radius = f.radius,
            });
        }
        // The selection goes under the text.
        if (v.sel) |sel| if (!sel.empty()) paintSelection(v, lay, scene, sel, blk.runs, ox, oy, sel_color, ctx_text);
        for (lay.runs.items[blk.runs.start..blk.runs.end]) |r| paintRun(v, scene, ctx_text, r, ox, oy, pal);
        for (lay.glyphs.items[blk.glyphs.start..blk.glyphs.end]) |r| paintRun(v, scene, ctx_text, r, ox, oy, pal);
        for (lay.images.items[blk.images.start..blk.images.end]) |im| {
            const slot = slotById(v, im.slot) orelse continue;
            if (slot.state != .ready) continue;
            scene.image(.{ .x = ox + im.rect.x, .y = oy + im.rect.y, .w = im.rect.w, .h = im.rect.h }, slot.id);
        }
    }
    paintScrollbar(v, lay, scene, rect, theme);
    if (v.truncated) {
        const note = "The file is longer than the preview's limit; the rest is not shown.";
        const m = ctx_text.metrics(ws.fonts.ui_small);
        _ = ctx_text.drawText(scene, note, ws.fonts.ui_small, rect.x + page_pad_x, rect.bottom() - 8 - m.descent, theme.list_foreground_dim);
    }
    return true;
}

fn slotById(v: *View, hash: u32) ?ImageSlot {
    var it = v.images.iterator();
    while (it.next()) |e| if (@as(u32, @truncate(std.hash.Wyhash.hash(0, e.key_ptr.*))) == hash) return e.value_ptr.*;
    return null;
}

fn paintRun(v: *View, scene: *Scene, text: *TextSystem, r: Run, ox: f32, oy: f32, pal: Palette) void {
    const x = ox + r.x;
    const hovered = r.link >= 0 and r.link == v.hover_link;
    if (r.bg) |bg| {
        const m = text.metrics(r.font);
        const top = oy + r.baseline - m.ascent - 2;
        scene.quad(.{ .x = x, .y = top, .w = r.w, .h = m.ascent + m.descent + 4 }, .{ .color = bg, .radius = 3 });
    }
    if (r.text.len == 0) return;
    const baseline = oy + r.baseline;
    const color = if (hovered) pal.link.lighten(0.2) else r.color;
    if (r.spans.len > 0) {
        const lay = text.layoutLine(r.text, r.font);
        text.drawLine(scene, lay, x + r.inset, baseline, color, r.spans);
    } else {
        _ = text.drawText(scene, r.text, r.font, x + r.inset, baseline, color);
    }
    const m = text.metrics(r.font);
    if (hovered) scene.fill(.{ .x = x + r.inset, .y = baseline + 2, .w = r.w - r.inset - (if (r.bg != null) @as(f32, 3) else 0), .h = 1 }, color);
    if (r.strike) scene.fill(.{ .x = x + r.inset, .y = baseline - m.ascent * 0.32, .w = r.w - r.inset, .h = 1 }, color);
}

fn paintSelection(v: *View, lay: *const Layout, scene: *Scene, sel: Sel, range: Range, ox: f32, oy: f32, color: Color, text: *TextSystem) void {
    _ = v;
    const lo = sel.lo();
    const hi = sel.hi();
    const start = @max(range.start, lo.run);
    const end = @min(range.end, hi.run + 1);
    if (start >= end) return;
    for (start..end) |ri| {
        const r = lay.runs.items[ri];
        if (r.h <= 0 or (r.text.len == 0 and r.bg == null)) continue;
        const x0 = if (ri == lo.run) runX(text, r, lo.off) else r.x;
        var x1 = if (ri == hi.run) runX(text, r, hi.off) else r.x + r.w;
        // Fill the gap to the next run on the line when the selection goes on.
        if (ri != hi.run and ri + 1 < lay.runs.items.len) {
            const nx = lay.runs.items[ri + 1];
            if (@abs(nx.y - r.y) < 1 and nx.x >= x1 and nx.x - x1 < 40) x1 = nx.x;
        }
        if (x1 <= x0) continue;
        scene.fill(.{ .x = ox + x0, .y = oy + r.y, .w = x1 - x0, .h = r.h }, color);
    }
}

fn runX(text: *TextSystem, r: Run, off: u32) f32 {
    if (off == 0) return r.x;
    if (off >= r.text.len) return r.x + r.w;
    return r.x + r.inset + text.layoutLine(r.text, r.font).xForIndex(off);
}

fn paintScrollbar(v: *View, lay: *const Layout, scene: *Scene, rect: Rect, theme: *const Theme) void {
    if (lay.height <= rect.h) return;
    const track = rect.h;
    const th = @max(28, track * rect.h / lay.height);
    const ty = rect.y + (track - th) * (v.scroll / (lay.height - rect.h));
    const hover = v.drag_thumb != null;
    scene.quad(.{ .x = rect.right() - 9, .y = ty, .w = 7, .h = th }, .{ .color = if (hover) theme.scrollbar_puck_hover else theme.scrollbar_puck, .radius = 3.5 });
}

fn anchorY(lay: *const Layout, id: []const u8) ?f32 {
    for (lay.anchors.items) |a| if (std.ascii.eqlIgnoreCase(a.id, id)) return a.y;
    return null;
}

// ---- Scroll sync ------------------------------------------------------------------------------------------------------------------

/// Moves the preview to the editor's top line when the editor was scrolled.
fn followEditor(ws: *Workspace, v: *View) void {
    const lay = &(v.layout orelse return);
    const ed = sourceEditorOf(ws, v) orelse return;
    const top = editorTopLine(ed);
    if (v.last_editor_top < 0) {
        v.last_editor_top = top;
        return;
    }
    if (@abs(top - v.last_editor_top) < 0.02) return;
    v.last_editor_top = top;
    v.scroll = std.math.clamp(lay.yForLine(top), 0, @max(0, lay.height - v.rect.h));
}

/// After the preview scrolled: the editor goes to the same source line.
fn driveEditor(ws: *Workspace, v: *View) void {
    const lay = &(v.layout orelse return);
    const ed = sourceEditorOf(ws, v) orelse return;
    const line = lay.lineForY(v.scroll);
    setEditorTopLine(ed, line);
    // Remember what the editor settled on, so the next paint does not echo it back.
    v.last_editor_top = editorTopLine(ed);
    ws.dirty = true;
}

// ---- Events ----------------------------------------------------------------------------------------------------------------------------

fn groupAt(ws: *Workspace, pos: platform.Vec2) ?u8 {
    for (ws.group_editor_rects[0..ws.groupCount()], 0..) |r, g| if (r.contains(pos.x, pos.y)) return @intCast(g);
    return null;
}

fn viewIn(ws: *Workspace, g: u8) ?*View {
    const ed = ws.group_active[g] orelse return null;
    return viewOf(ws, ed);
}

fn lineHeightPx(v: *View) f32 {
    _ = v;
    return 24;
}

/// Keys, clicks and the wheel for preview tabs. Returns true if consumed.
pub fn handleEvent(ws: *Workspace, ev: platform.Event) bool {
    switch (ev) {
        .key_down => |k| {
            if (ws.menuIsOpen() or ws.focus != .editor or ws.overlay != null) return false;
            const v = active(ws) orelse return false;
            return handleKey(ws, v, k);
        },
        .mouse_down => |m| {
            if (ws.menuIsOpen() or ws.overlay != null) return false;
            const g = groupAt(ws, m.pos) orelse return false;
            const v = viewIn(ws, g) orelse return false;
            if (g != ws.focused_group) ws.focusGroup(g);
            ws.focus = .editor;
            mouseDown(ws, v, m);
            return true;
        },
        .mouse_up => |m| {
            const v = active(ws) orelse return false;
            if (v.press == null and !v.dragging and v.drag_thumb == null) return false;
            mouseUp(ws, v, m);
            return true;
        },
        .mouse_move => |m| {
            if (ws.overlay != null) return false;
            const g = groupAt(ws, m.pos) orelse return false;
            const v = viewIn(ws, g) orelse return false;
            mouseMove(ws, v, m);
            return v.dragging or v.drag_thumb != null;
        },
        .scroll => |s| {
            if (ws.overlay != null) return false;
            const g = groupAt(ws, s.pos) orelse return false;
            const v = viewIn(ws, g) orelse return false;
            const lay = &(v.layout orelse return true);
            const dy = if (s.unit == .lines) s.delta.y * 3 * lineHeightPx(v) else s.delta.y;
            scrollTo(ws, v, lay, v.scroll + dy);
            return true;
        },
        else => return false,
    }
}

fn scrollTo(ws: *Workspace, v: *View, lay: *const Layout, y: f32) void {
    v.scroll = std.math.clamp(y, 0, @max(0, lay.height - v.rect.h));
    driveEditor(ws, v);
    ws.dirty = true;
}

fn handleKey(ws: *Workspace, v: *View, k: platform.KeyEvent) bool {
    const lay = &(v.layout orelse return false);
    const m = k.mods;
    if (m.command() and !m.alt and !m.shift) {
        switch (k.key) {
            .char => |ch| switch (ch) {
                'c' => {
                    copySelection(ws, v);
                    return true;
                },
                'a' => {
                    selectAll(v);
                    ws.dirty = true;
                    return true;
                },
                else => return false,
            },
            else => return false,
        }
    }
    if (!m.none()) return false;
    const page = v.rect.h * 0.9;
    switch (k.key) {
        .named => |n| switch (n) {
            .up => scrollTo(ws, v, lay, v.scroll - 40),
            .down => scrollTo(ws, v, lay, v.scroll + 40),
            .page_up => scrollTo(ws, v, lay, v.scroll - page),
            .page_down => scrollTo(ws, v, lay, v.scroll + page),
            .home => scrollTo(ws, v, lay, 0),
            .end => scrollTo(ws, v, lay, lay.height),
            else => return false,
        },
        .char => |ch| if (ch == ' ') scrollTo(ws, v, lay, v.scroll + page) else return false,
    }
    return true;
}

fn thumbRect(v: *const View, lay: *const Layout) ?Rect {
    if (lay.height <= v.rect.h) return null;
    const th = @max(28, v.rect.h * v.rect.h / lay.height);
    const ty = v.rect.y + (v.rect.h - th) * (v.scroll / (lay.height - v.rect.h));
    return .{ .x = v.rect.right() - scrollbar_w, .y = ty, .w = scrollbar_w, .h = th };
}

/// The link under `pos` (window coordinates), or -1.
fn linkAt(v: *View, lay: *const Layout, pos: platform.Vec2) i32 {
    const run = runIndexAt(v, lay, pos, true) orelse return -1;
    return lay.runs.items[run].link;
}

/// The run containing `pos`, or (when `exact` is false) the nearest one.
fn runIndexAt(v: *View, lay: *const Layout, pos: platform.Vec2, exact: bool) ?u32 {
    const px = pos.x - v.rect.x;
    const py = pos.y - v.rect.y + v.scroll;
    var best: ?u32 = null;
    var best_d: f32 = std.math.floatMax(f32);
    var lo: usize = 0;
    var hi: usize = lay.blocks.items.len;
    while (lo < hi) {
        const mid = (lo + hi) / 2;
        if (lay.blocks.items[mid].bottom < py - 60) lo = mid + 1 else hi = mid;
    }
    var bi = lo;
    while (bi < lay.blocks.items.len) : (bi += 1) {
        const blk = lay.blocks.items[bi];
        if (blk.top > py + 60) break;
        var ri = blk.runs.start;
        while (ri < blk.runs.end) : (ri += 1) {
            const r = lay.runs.items[ri];
            if (r.h <= 0) continue;
            const dy: f32 = if (py < r.y) r.y - py else if (py > r.y + r.h) py - (r.y + r.h) else 0;
            const dx: f32 = if (px < r.x) r.x - px else if (px > r.x + r.w) px - (r.x + r.w) else 0;
            if (dy == 0 and dx == 0) return ri;
            if (exact) continue;
            const d = dy * 1000 + dx;
            if (d < best_d) {
                best_d = d;
                best = ri;
            }
        }
    }
    return best;
}

fn posAt(ws: *Workspace, v: *View, lay: *const Layout, pos: platform.Vec2) ?Pos {
    const ri = runIndexAt(v, lay, pos, false) orelse return null;
    const r = lay.runs.items[ri];
    const px = pos.x - v.rect.x;
    if (r.text.len == 0) return .{ .run = ri, .off = 0 };
    if (px <= r.x) return .{ .run = ri, .off = 0 };
    if (px >= r.x + r.w) return .{ .run = ri, .off = @intCast(r.text.len) };
    const l = ws.env.text.layoutLine(r.text, r.font);
    return .{ .run = ri, .off = @intCast(l.indexForX(@max(0, px - r.x - r.inset))) };
}

fn mouseDown(ws: *Workspace, v: *View, m: platform.MouseEvent) void {
    const lay = &(v.layout orelse return);
    if (m.button != .left) return;
    if (thumbRect(v, lay)) |tr| if (tr.contains(m.pos.x, m.pos.y)) {
        v.drag_thumb = m.pos.y - tr.y;
        return;
    };
    if (v.rect.w - (m.pos.x - v.rect.x) < scrollbar_w and lay.height > v.rect.h) {
        // The track outside the thumb pages.
        const tr = thumbRect(v, lay).?;
        scrollTo(ws, v, lay, v.scroll + if (m.pos.y < tr.y) -v.rect.h * 0.9 else v.rect.h * 0.9);
        return;
    }
    v.press = .{ .pos = m.pos, .link = linkAt(v, lay, m.pos) };
    const p = posAt(ws, v, lay, m.pos) orelse {
        v.sel = null;
        ws.dirty = true;
        return;
    };
    if (m.click_count >= 3) {
        selectBlockOf(v, lay, p);
    } else if (m.click_count == 2) {
        selectWord(ws, v, lay, p);
    } else {
        v.sel = .{ .anchor = p, .head = p };
        v.dragging = true;
    }
    ws.dirty = true;
}

fn mouseMove(ws: *Workspace, v: *View, m: platform.MouseMoveEvent) void {
    const lay = &(v.layout orelse return);
    if (v.drag_thumb) |off| {
        const track = v.rect.h;
        const th = @max(28, track * track / lay.height);
        const t = std.math.clamp((m.pos.y - off - v.rect.y) / @max(1, track - th), 0, 1);
        scrollTo(ws, v, lay, t * (lay.height - v.rect.h));
        return;
    }
    if (v.dragging and m.pressed == .left) {
        if (posAt(ws, v, lay, m.pos)) |p| {
            if (v.sel) |*s| s.head = p;
            ws.dirty = true;
        }
        // Dragging past an edge scrolls.
        if (m.pos.y < v.rect.y + 8) scrollTo(ws, v, lay, v.scroll - 20) else if (m.pos.y > v.rect.bottom() - 8) scrollTo(ws, v, lay, v.scroll + 20);
        return;
    }
    const hl = linkAt(v, lay, m.pos);
    if (hl != v.hover_link) {
        v.hover_link = hl;
        ws.dirty = true;
    }
}

fn mouseUp(ws: *Workspace, v: *View, m: platform.MouseEvent) void {
    const lay = &(v.layout orelse return);
    v.drag_thumb = null;
    v.dragging = false;
    const pr = v.press;
    v.press = null;
    if (pr) |p| {
        const moved = @abs(m.pos.x - p.pos.x) + @abs(m.pos.y - p.pos.y);
        if (moved < 4 and p.link >= 0 and p.link < @as(i32, @intCast(lay.links.items.len))) {
            v.sel = null;
            activateLink(ws, v, lay.links.items[@intCast(p.link)]);
        }
    }
    ws.dirty = true;
}

fn selectAll(v: *View) void {
    const lay = &(v.layout orelse return);
    if (lay.runs.items.len == 0) return;
    const last = lay.runs.items.len - 1;
    v.sel = .{ .anchor = .{ .run = 0, .off = 0 }, .head = .{ .run = @intCast(last), .off = @intCast(lay.runs.items[last].text.len) } };
}

fn selectWord(ws: *Workspace, v: *View, lay: *const Layout, p: Pos) void {
    _ = ws;
    const r = lay.runs.items[p.run];
    var a: u32 = p.off;
    var b: u32 = p.off;
    const t = r.text;
    while (a > 0 and isWordByte(t[a - 1])) a -= 1;
    while (b < t.len and isWordByte(t[b])) b += 1;
    v.sel = .{ .anchor = .{ .run = p.run, .off = a }, .head = .{ .run = p.run, .off = b } };
}

fn isWordByte(ch: u8) bool {
    return std.ascii.isAlphanumeric(ch) or ch == '_' or ch >= 0x80;
}

/// Triple-click: the whole block the run belongs to.
fn selectBlockOf(v: *View, lay: *const Layout, p: Pos) void {
    for (lay.blocks.items) |blk| if (p.run >= blk.runs.start and p.run < blk.runs.end) {
        if (blk.runs.end == blk.runs.start) return;
        const last = blk.runs.end - 1;
        v.sel = .{
            .anchor = .{ .run = blk.runs.start, .off = 0 },
            .head = .{ .run = last, .off = @intCast(lay.runs.items[last].text.len) },
        };
        return;
    };
}

/// The selected text, runs joined by what separated them (caller frees).
pub fn selectedText(gpa: Allocator, lay: *const Layout, sel: Sel) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(gpa);
    const lo = sel.lo();
    const hi = sel.hi();
    var ri: usize = lo.run;
    while (ri <= hi.run and ri < lay.runs.items.len) : (ri += 1) {
        const r = lay.runs.items[ri];
        if (ri > lo.run) switch (r.sep) {
            .none => {},
            .space => try out.append(gpa, ' '),
            .line => try out.append(gpa, '\n'),
            .block => try out.appendSlice(gpa, "\n\n"),
        };
        const a: usize = if (ri == lo.run) @min(lo.off, r.text.len) else 0;
        const b: usize = if (ri == hi.run) @min(hi.off, r.text.len) else r.text.len;
        if (b > a) try out.appendSlice(gpa, r.text[a..b]);
    }
    return out.toOwnedSlice(gpa);
}

fn copySelection(ws: *Workspace, v: *View) void {
    const lay = &(v.layout orelse return);
    const sel = v.sel orelse return;
    if (sel.empty()) return;
    const text = selectedText(ws.gpa, lay, sel) catch return;
    defer ws.gpa.free(text);
    ws.env.clipboard.set(ws.env.clipboard.ctx, text);
}

// ---- Links ----------------------------------------------------------------------------------------------------------------------------------

/// What a link target means.
pub const LinkKind = union(enum) {
    anchor: []const u8,
    external: []const u8,
    file: struct { path: []const u8, line: ?u32, anchor: ?[]const u8 },
    unknown: []const u8,
};

pub fn classifyLink(url: []const u8) LinkKind {
    if (url.len == 0) return .{ .unknown = url };
    if (url[0] == '#') return .{ .anchor = url[1..] };
    if (std.ascii.startsWithIgnoreCase(url, "http://") or std.ascii.startsWithIgnoreCase(url, "https://") or std.ascii.startsWithIgnoreCase(url, "mailto:"))
        return .{ .external = url };
    // A scheme other than file: asks first (here: is refused with a message).
    if (std.mem.indexOfScalar(u8, url, ':')) |colon| {
        const scheme = url[0..colon];
        var is_scheme = scheme.len > 1;
        for (scheme) |ch| if (!(std.ascii.isAlphanumeric(ch) or ch == '+' or ch == '-' or ch == '.')) {
            is_scheme = false;
        };
        if (is_scheme and !std.ascii.eqlIgnoreCase(scheme, "file")) return .{ .unknown = url };
    }
    var path = url;
    var frag: ?[]const u8 = null;
    if (std.mem.indexOfScalar(u8, url, '#')) |h| {
        path = url[0..h];
        frag = url[h + 1 ..];
    }
    var line: ?u32 = null;
    if (frag) |f| if (f.len > 1 and (f[0] == 'L' or f[0] == 'l')) {
        var end: usize = 1;
        while (end < f.len and std.ascii.isDigit(f[end])) end += 1;
        if (end > 1) {
            line = std.fmt.parseInt(u32, f[1..end], 10) catch null;
            if (line != null) frag = null;
        }
    };
    return .{ .file = .{ .path = path, .line = line, .anchor = frag } };
}

fn activateLink(ws: *Workspace, v: *View, url: []const u8) void {
    switch (classifyLink(url)) {
        .anchor => |id| scrollToAnchor(ws, v, id),
        .external => |u| file_cmd.openExternally(ws, u),
        .unknown => |u| ws.setStatus("Not opening a link to {s}", .{u}),
        .file => |f| {
            // A link to this very file with a heading is an anchor.
            const resolved = resolveRelative(ws.gpa, if (v.source_path) |p| (if (rpath.isRemote(p)) remoteDir(p) else std.fs.path.dirname(p)) else null, f.path) catch return;
            defer ws.gpa.free(resolved);
            if (f.path.len == 0 or (v.source_path != null and std.mem.eql(u8, resolved, v.source_path.?))) {
                if (f.anchor) |a| return scrollToAnchor(ws, v, a);
                if (f.line == null) return;
            }
            if (!exists(ws, resolved)) {
                ws.setStatus("{s} does not exist", .{f.path});
                return;
            }
            const ed = ws.openFile(resolved, false) catch {
                ws.setStatus("Unable to open {s}", .{f.path});
                return;
            };
            if (f.line) |ln| {
                ed.setSingleSelection(.caret(ed.buffer.lineStart(@min(ed.buffer.lineCount() -| 1, ln -| 1))));
                ed.ensureCaretVisible(true);
            }
            ws.focus = .editor;
        },
    }
}

fn scrollToAnchor(ws: *Workspace, v: *View, raw: []const u8) void {
    const lay = &(v.layout orelse return);
    const id = percentDecode(ws.gpa, raw) catch return;
    defer ws.gpa.free(id);
    if (anchorY(lay, id)) |y| {
        scrollTo(ws, v, lay, y - 8);
    } else ws.setStatus("No heading #{s}", .{id});
}

// ---- Tests -------------------------------------------------------------------------------------------------------------------------------------

const testing = std.testing;

test "links are classified" {
    try testing.expectEqualStrings("intro", classifyLink("#intro").anchor);
    try testing.expectEqualStrings("https://x.org/a", classifyLink("https://x.org/a").external);
    try testing.expectEqualStrings("mailto:a@b.c", classifyLink("mailto:a@b.c").external);
    try testing.expectEqualStrings("vscode://thing", classifyLink("vscode://thing").unknown);
    const f = classifyLink("docs/setup.md#L12").file;
    try testing.expectEqualStrings("docs/setup.md", f.path);
    try testing.expectEqual(@as(?u32, 12), f.line);
    try testing.expect(f.anchor == null);
    const g = classifyLink("other.md#getting-started").file;
    try testing.expectEqualStrings("other.md", g.path);
    try testing.expectEqualStrings("getting-started", g.anchor.?);
    // A file: link is a path (resolveRelative strips the scheme).
    try testing.expectEqualStrings("file:///a.txt", classifyLink("file:///a.txt").file.path);
}

test "relative paths resolve against the Markdown file's folder" {
    const gpa = testing.allocator;
    const a = try resolveRelative(gpa, "/home/me/proj/docs", "../img/a%20b.png");
    defer gpa.free(a);
    try testing.expectEqualStrings("/home/me/proj/img/a b.png", a);
    const b = try resolveRelative(gpa, "/home/me/proj", "/abs/x.png");
    defer gpa.free(b);
    try testing.expectEqualStrings("/abs/x.png", b);
    const c1 = try resolveRelative(gpa, "/p", "file:///tmp/y.png");
    defer gpa.free(c1);
    try testing.expectEqualStrings("/tmp/y.png", c1);
    const d = try resolveRelative(gpa, "ssh://host/home/me", "pic.png");
    defer gpa.free(d);
    try testing.expectEqualStrings("ssh://host/home/me/pic.png", d);
}

test "percent escapes decode" {
    const gpa = testing.allocator;
    const s = try percentDecode(gpa, "a%20b%2Fc%zz%4");
    defer gpa.free(s);
    try testing.expectEqualStrings("a b/c%zz%4", s);
}

test "tabs expand to the next stop" {
    const a = try expandTabs(testing.allocator, "a\tb\n\tc");
    defer testing.allocator.free(a);
    try testing.expectEqualStrings("a   b\n    c", a);
}

test "scroll sync maps lines and positions both ways" {
    var lay: Layout = .{ .arena = std.heap.ArenaAllocator.init(testing.allocator) };
    defer lay.deinit();
    const a = lay.arena.allocator();
    try lay.points.append(a, .{ .line = 0, .y = 10 });
    try lay.points.append(a, .{ .line = 4, .y = 110 });
    try lay.points.append(a, .{ .line = 10, .y = 410 });
    try testing.expectApproxEqAbs(@as(f32, 10), lay.yForLine(0), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 60), lay.yForLine(2), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 110), lay.yForLine(4), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 260), lay.yForLine(7), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 410), lay.yForLine(99), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 2), lay.lineForY(60), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 7), lay.lineForY(260), 0.01);
    try testing.expectApproxEqAbs(@as(f32, 0), lay.lineForY(-50), 0.01);
    // Round trip.
    var l: f32 = 0;
    while (l <= 10) : (l += 0.5) try testing.expectApproxEqAbs(l, lay.lineForY(lay.yForLine(l)), 0.01);
}

test "selected text joins runs by their separators" {
    var lay: Layout = .{ .arena = std.heap.ArenaAllocator.init(testing.allocator) };
    defer lay.deinit();
    const a = lay.arena.allocator();
    const rs = [_]Run{
        .{ .x = 0, .w = 10, .text = "Hello", .font = 0, .color = .transparent },
        .{ .x = 0, .w = 10, .text = "world", .font = 0, .color = .transparent, .sep = .space },
        .{ .x = 0, .w = 10, .text = "next", .font = 0, .color = .transparent, .sep = .line },
        .{ .x = 0, .w = 10, .text = "para", .font = 0, .color = .transparent, .sep = .block },
    };
    try lay.runs.appendSlice(a, &rs);
    const all = try selectedText(testing.allocator, &lay, .{ .anchor = .{ .run = 0, .off = 0 }, .head = .{ .run = 3, .off = 4 } });
    defer testing.allocator.free(all);
    try testing.expectEqualStrings("Hello world\nnext\n\npara", all);
    const part = try selectedText(testing.allocator, &lay, .{ .anchor = .{ .run = 2, .off = 2 }, .head = .{ .run = 0, .off = 3 } });
    defer testing.allocator.free(part);
    try testing.expectEqualStrings("lo world\nne", part);
}

test "line colours are run-length encoded" {
    const hl = [_]syntax_mod.HighlightSpan{
        .{ .start = 0, .end = 3, .color = .{ .r = 1, .g = 0, .b = 0, .a = 1 } },
        .{ .start = 6, .end = 9, .color = .{ .r = 0, .g = 1, .b = 0, .a = 1 } },
    };
    var per_byte: [12]u32 = @splat(0);
    @memset(per_byte[0..3], 1);
    @memset(per_byte[6..9], 2);
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const def: Color = .{ .r = 1, .g = 1, .b = 1, .a = 1 };
    const spans = lineSpans(arena.allocator(), &per_byte, &hl, 0, 12, def);
    try testing.expectEqual(@as(usize, 4), spans.len);
    try testing.expectEqual(@as(u32, 3), spans[0].end);
    try testing.expectEqual(@as(f32, 1), spans[0].color.r);
    try testing.expectEqual(@as(u32, 6), spans[1].end);
    try testing.expectEqual(@as(f32, 1), spans[1].color.b);
    try testing.expectEqual(@as(u32, 9), spans[2].end);
    try testing.expectEqual(@as(u32, 12), spans[3].end);
    // A line in the middle of the block.
    const mid = lineSpans(arena.allocator(), &per_byte, &hl, 2, 6, def);
    try testing.expectEqual(@as(usize, 3), mid.len);
    try testing.expectEqual(@as(u32, 1), mid[0].end);
}

test "markdown files are recognised by name" {
    try testing.expect(isMarkdownPath("README.md"));
    try testing.expect(isMarkdownPath("/a/b/notes.MARKDOWN"));
    try testing.expect(!isMarkdownPath("main.zig"));
    try testing.expect(!isMarkdownPath("md"));
}
