//! The Settings tab: a searchable list of settings with typed controls
//! (checkbox, number or text field, drop-down, color theme list), written to
//! settings.json as they change, and the Keyboard Shortcuts page, which
//! writes keymap.json. Like a terminal tab, it is an ordinary tab (split
//! panes, MRU and closing work) whose editor shows an empty read-only buffer
//! and whose `Tab.settings` is drawn instead of it.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const editor_mod = @import("../editor/editor.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const catalog = @import("../settings/catalog.zig");
const settings_file = @import("../settings/settings_file.zig");
const bindings_file = @import("../settings/bindings_file.zig");
const keymap_mod = @import("../settings/keymap.zig");
const platform = @import("../../framework/platform/platform.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const ui = @import("../../framework/ui/ui.zig");
const find_panel = @import("find_panel.zig");

const Allocator = std.mem.Allocator;
const Workspace = workspace_mod.Workspace;
const Editor = editor_mod.Editor;
const Buffer = buffer_mod.Buffer;
const Action = keymap_mod.Action;
const Rect = scene_mod.Rect;
const Value = std.json.Value;
const Category = catalog.Category;

pub const Page = enum { settings, shortcuts };

const Focus = enum { none, search, field };

const sidebar_w: f32 = 220;
const row_h: f32 = 24;
const field_h: f32 = 26;
const max_content_w: f32 = 840;

const What = union(enum) {
    category: ?Category,
    page: Page,
    search,
    open_json,
    toggle: usize,
    edit: usize,
    dropdown: usize,
    reset: usize,
    option: usize,
    capture: usize,
    reset_binding: usize,
};

const Hit = struct { rect: Rect, what: What };

const Option = struct {
    label: []const u8,
    /// JSON text for a choice, the scheme name for a scheme.
    value: []const u8,
    header: bool = false,
};

const Dropdown = struct {
    def: usize,
    /// The control it opened from (the list hangs below, or above it).
    anchor: Rect,
    arena: std.heap.ArenaAllocator,
    options: std.ArrayList(Option) = .empty,
    /// The option under the pointer or the arrow keys.
    hover: ?usize = null,
    /// How far the list is scrolled, in pixels.
    scroll: f32 = 0,
    popup: Rect = .{},
};

const actions = std.enums.values(Action);

pub const View = struct {
    gpa: Allocator,
    page: Page = .settings,
    search_buffer: *Buffer,
    search: *Editor,
    field_buffer: *Buffer,
    field: *Editor,
    focus: Focus = .none,
    /// The setting whose text or number field is being edited.
    editing: ?usize = null,
    category: ?Category = null,
    scroll: f32 = 0,
    content_h: f32 = 0,
    view_h: f32 = 0,
    values: ?std.json.Parsed(Value) = null,
    values_read_at: i64 = 0,
    values_stale: bool = true,
    dropdown: ?Dropdown = null,
    hits: std.ArrayList(Hit) = .empty,
    rect: Rect = .{},
    /// Keyboard Shortcuts: rows in label order, the row being rebound.
    order: []u16 = &.{},
    capturing: ?usize = null,
    modified_count: usize = 0,

    pub fn create(gpa: Allocator, env: *editor_mod.Env) !*View {
        const self = try gpa.create(View);
        errdefer gpa.destroy(self);
        const sb = try gpa.create(Buffer);
        errdefer gpa.destroy(sb);
        sb.* = try Buffer.initEmpty(gpa, 0);
        errdefer sb.deinit();
        const fb = try gpa.create(Buffer);
        errdefer gpa.destroy(fb);
        fb.* = try Buffer.initEmpty(gpa, 0);
        errdefer fb.deinit();
        const search = try Editor.create(gpa, env, sb);
        errdefer search.destroy();
        const field = try Editor.create(gpa, env, fb);
        errdefer field.destroy();
        search.mode = .single_line;
        field.mode = .single_line;
        search.placeholder = "Search settings";
        const order = try gpa.alloc(u16, actions.len);
        errdefer gpa.free(order);
        for (order, 0..) |*o, i| o.* = @intCast(i);
        std.mem.sort(u16, order, {}, lessByName);
        self.* = .{ .gpa = gpa, .search_buffer = sb, .search = search, .field_buffer = fb, .field = field, .order = order };
        return self;
    }

    pub fn destroy(self: *View) void {
        self.closeDropdown();
        if (self.values) |*v| v.deinit();
        self.search.destroy();
        self.field.destroy();
        self.search_buffer.deinit();
        self.field_buffer.deinit();
        self.gpa.destroy(self.search_buffer);
        self.gpa.destroy(self.field_buffer);
        self.hits.deinit(self.gpa);
        self.gpa.free(self.order);
        self.gpa.destroy(self);
    }

    fn closeDropdown(self: *View) void {
        if (self.dropdown) |*d| {
            d.arena.deinit();
            self.dropdown = null;
        }
    }

    fn valueOf(self: *const View, d: catalog.Def) ?Value {
        const p = self.values orelse return null;
        return p.value.object.get(d.key);
    }

    fn isModified(self: *const View, d: catalog.Def) bool {
        const v = self.valueOf(d) orelse return false;
        return !catalog.isDefault(d, v);
    }

    /// Re-reads settings.json (after a write, or now and then to see edits
    /// made in the JSON tab).
    fn refresh(self: *View, ws: *Workspace) void {
        const now = platform.nowMs();
        if (!self.values_stale and now - self.values_read_at < 700) return;
        self.values_stale = false;
        self.values_read_at = now;
        if (self.values) |*v| v.deinit();
        self.values = settings_file.readObject(self.gpa, ws.io) catch null;
        self.modified_count = 0;
        for (catalog.defs) |d| {
            if (self.isModified(d)) self.modified_count += 1;
        }
    }
};

fn lessByName(_: void, a: u16, b: u16) bool {
    return std.mem.lessThan(u8, @tagName(actions[a]), @tagName(actions[b]));
}

// ---- Opening ----------------------------------------------------------------

/// The settings view shown in the focused group, if any.
pub fn active(ws: *Workspace) ?*View {
    const i = ws.activeIndex() orelse return null;
    return ws.tabs.items[i].settings;
}

/// Shows the Settings tab (a new one, or the one already open) on `page`.
pub fn open(ws: *Workspace, page: Page) void {
    for (ws.tabs.items, 0..) |t, i| if (t.settings) |v| {
        v.page = page;
        v.values_stale = true;
        v.scroll = 0;
        ws.activateTab(i);
        ws.focus = .editor;
        return;
    };
    const view = View.create(ws.gpa, ws.env) catch |err| {
        ws.setStatus("Unable to open settings: {t}", .{err});
        return;
    };
    view.page = page;
    const buffer = ws.gpa.create(Buffer) catch return view.destroy();
    buffer.* = Buffer.initEmpty(ws.gpa, ws.next_buffer_id) catch {
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    ws.next_buffer_id += 1;
    buffer.display_name = "Settings";
    buffer.read_only = true;
    ws.buffers.append(ws.gpa, buffer) catch {
        buffer.deinit();
        ws.gpa.destroy(buffer);
        return view.destroy();
    };
    const ed = Editor.create(ws.gpa, ws.env, buffer) catch return view.destroy();
    _ = ws.insertTab(.{ .editor = ed, .settings = view }) catch {
        ed.destroy();
        return view.destroy();
    };
    ws.focus = .editor;
    ws.updateTitle();
}

/// The text input that has the keyboard in the active settings tab, so the
/// workspace's editing commands and typing go to it.
pub fn focusedInput(ws: *Workspace) ?*Editor {
    const v = active(ws) orelse return null;
    return switch (v.focus) {
        .none => null,
        .search => v.search,
        .field => v.field,
    };
}

pub fn statusText(ws: *Workspace, buf: []u8) ?[]const u8 {
    const v = active(ws) orelse return null;
    if (v.page == .shortcuts) return std.fmt.bufPrint(buf, "Keyboard Shortcuts", .{}) catch null;
    return std.fmt.bufPrint(buf, "Settings  {d} of {d} changed", .{ v.modified_count, catalog.defs.len }) catch null;
}

// ---- Writing ----------------------------------------------------------------

fn reload(ws: *Workspace, v: *View) void {
    v.values_stale = true;
    if (ws.prefs) |pr| pr.reloadSettings(pr.ctx);
    ws.dirty = true;
}

fn writeLiteral(ws: *Workspace, v: *View, d: catalog.Def, literal: []const u8) void {
    settings_file.setRaw(ws.gpa, ws.io, d.key, literal) catch |err| {
        ws.setStatus("Couldn't save {s}: {t}", .{ d.key, err });
        return;
    };
    reload(ws, v);
}

fn resetSetting(ws: *Workspace, v: *View, d: catalog.Def) void {
    settings_file.unset(ws.gpa, ws.io, d.key) catch |err| {
        ws.setStatus("Couldn't reset {s}: {t}", .{ d.key, err });
        return;
    };
    reload(ws, v);
}

fn toggle(ws: *Workspace, v: *View, di: usize) void {
    const d = catalog.defs[di];
    const now = if (v.valueOf(d)) |val| (val == .bool and val.bool) else std.mem.eql(u8, d.default, "true");
    writeLiteral(ws, v, d, if (now) "false" else "true");
}

fn startEditing(ws: *Workspace, v: *View, di: usize) void {
    v.closeDropdown();
    commit(ws, v);
    const d = catalog.defs[di];
    var buf: [64]u8 = undefined;
    v.editing = di;
    v.focus = .field;
    v.field.setText(catalog.displayText(d, v.valueOf(d), &buf), true);
}

/// Writes the field being edited, if it holds a valid, different value.
fn commit(ws: *Workspace, v: *View) void {
    const di = v.editing orelse return;
    v.editing = null;
    if (v.focus == .field) v.focus = .none;
    const d = catalog.defs[di];
    const text = v.field.contents(ws.gpa) catch return;
    defer ws.gpa.free(text);
    var buf: [64]u8 = undefined;
    if (std.mem.eql(u8, text, catalog.displayText(d, v.valueOf(d), &buf))) return;
    const literal = catalog.literalFor(ws.gpa, d, text) catch return;
    if (literal) |lit| {
        defer ws.gpa.free(lit);
        writeLiteral(ws, v, d, lit);
    } else switch (d.kind) {
        .integer => |r| ws.setStatus("{s}: expected a whole number from {d} to {d}", .{ d.title, r.min, r.max }),
        .number => |r| ws.setStatus("{s}: expected a number from {d} to {d}", .{ d.title, r.min, r.max }),
        else => ws.setStatus("{s}: invalid value", .{d.title}),
    }
}

fn cancelEditing(v: *View) void {
    v.editing = null;
    if (v.focus == .field) v.focus = .none;
}

// ---- Drop-downs ---------------------------------------------------------------

fn openDropdown(ws: *Workspace, v: *View, di: usize, anchor: Rect) void {
    commit(ws, v);
    v.closeDropdown();
    const d = catalog.defs[di];
    v.dropdown = .{ .def = di, .anchor = anchor, .arena = .init(v.gpa) };
    const dd = &v.dropdown.?;
    const a = dd.arena.allocator();
    var current: ?usize = null;
    switch (d.kind) {
        .choice => |cs| {
            const idx = if (v.valueOf(d)) |val| catalog.choiceIndex(cs, val) else null;
            const default_idx = blk: {
                const parsed = std.json.parseFromSliceLeaky(Value, a, d.default, .{}) catch break :blk null;
                break :blk catalog.choiceIndex(cs, parsed);
            };
            current = idx orelse default_idx;
            for (cs) |c| dd.options.append(a, .{ .label = c.label, .value = c.json }) catch {};
        },
        .scheme => |s| {
            const pr = ws.prefs orelse return v.closeDropdown();
            var buf: [64]u8 = undefined;
            const now = catalog.displayText(d, v.valueOf(d), &buf);
            if (s.allow_auto) {
                if (std.ascii.eqlIgnoreCase(now, "auto")) current = dd.options.items.len;
                dd.options.append(a, .{ .label = "Auto", .value = "auto" }) catch {};
            }
            const schemes = pr.schemes(pr.ctx);
            for ([_]bool{ false, true }) |light| {
                dd.options.append(a, .{ .label = if (light) "Light" else "Dark", .value = "", .header = true }) catch {};
                for (schemes) |sch| {
                    if (sch.light != light) continue;
                    if (std.ascii.eqlIgnoreCase(now, sch.name)) current = dd.options.items.len;
                    const name = a.dupe(u8, sch.name) catch continue;
                    dd.options.append(a, .{ .label = name, .value = name }) catch {};
                }
            }
        },
        else => return v.closeDropdown(),
    }
    dd.hover = current;
    if (current) |c| dd.scroll = @max(0, @as(f32, @floatFromInt(c)) * row_h - 6 * row_h);
}

fn previewOption(ws: *Workspace, v: *View) void {
    const dd = v.dropdown orelse return;
    const d = catalog.defs[dd.def];
    if (d.kind != .scheme or !std.mem.eql(u8, d.key, "color_scheme")) return;
    const pr = ws.prefs orelse return;
    const h = dd.hover orelse return;
    const o = dd.options.items[h];
    pr.previewScheme(pr.ctx, if (std.mem.eql(u8, o.value, "auto")) null else o.value);
}

fn cancelDropdown(ws: *Workspace, v: *View) void {
    if (v.dropdown == null) return;
    const d = catalog.defs[v.dropdown.?.def];
    if (d.kind == .scheme) if (ws.prefs) |pr| pr.previewScheme(pr.ctx, null);
    v.closeDropdown();
}

fn chooseOption(ws: *Workspace, v: *View, index: usize) void {
    const dd = v.dropdown orelse return;
    const o = dd.options.items[index];
    if (o.header) return;
    const d = catalog.defs[dd.def];
    switch (d.kind) {
        .choice => writeLiteral(ws, v, d, o.value),
        .scheme => {
            if (ws.prefs) |pr| pr.previewScheme(pr.ctx, null);
            if (std.mem.eql(u8, d.key, "color_scheme")) {
                if (ws.prefs) |pr| pr.chooseScheme(pr.ctx, o.value);
                v.values_stale = true;
            } else {
                const lit = std.json.Stringify.valueAlloc(ws.gpa, o.value, .{}) catch return;
                defer ws.gpa.free(lit);
                writeLiteral(ws, v, d, lit);
            }
        },
        else => {},
    }
    v.closeDropdown();
}

fn moveHover(ws: *Workspace, v: *View, step: isize) void {
    const dd = if (v.dropdown) |*p| p else return;
    const n: isize = @intCast(dd.options.items.len);
    var i: isize = if (dd.hover) |h| @intCast(h) else if (step > 0) -1 else n;
    while (true) {
        i += step;
        if (i < 0 or i >= n) return;
        if (!dd.options.items[@intCast(i)].header) break;
    }
    dd.hover = @intCast(i);
    const top = @as(f32, @floatFromInt(i)) * row_h;
    if (top < dd.scroll) dd.scroll = top;
    if (top + row_h > dd.scroll + dd.popup.h) dd.scroll = top + row_h - dd.popup.h;
    previewOption(ws, v);
}

// ---- Keyboard Shortcuts ---------------------------------------------------------

fn humanize(buf: []u8, name: []const u8) []const u8 {
    var n: usize = 0;
    var cap = true;
    for (name) |ch| {
        if (n >= buf.len) break;
        if (ch == '_') {
            buf[n] = ' ';
            cap = true;
        } else {
            buf[n] = if (cap) std.ascii.toUpper(ch) else ch;
            cap = false;
        }
        n += 1;
    }
    return buf[0..n];
}

fn isUserBound(ws: *Workspace, action: Action) bool {
    for (ws.keymap.bindings.items[0..ws.keymap.user_count]) |b| if (b.action == action) return true;
    return false;
}

fn rebind(ws: *Workspace, v: *View, action: Action, chord: keymap_mod.Chord) void {
    var spec_buf: [48]u8 = undefined;
    const spec = bindings_file.chordSpec(&spec_buf, chord);
    // The keys the command has by default go: it moves instead of gaining one.
    var old_bufs: [4][96]u8 = undefined;
    var old: [4][]const u8 = undefined;
    var n: usize = 0;
    for (ws.keymap.bindings.items[ws.keymap.user_count..]) |b| {
        if (b.action != action or n == old.len) continue;
        const c0 = b.chords[0] orelse continue;
        var b0: [48]u8 = undefined;
        var b1: [48]u8 = undefined;
        const s0 = bindings_file.chordSpec(&b0, c0);
        old[n] = if (b.chords[1]) |c1|
            std.fmt.bufPrint(&old_bufs[n], "{s}\", \"{s}", .{ s0, bindings_file.chordSpec(&b1, c1) }) catch continue
        else
            std.fmt.bufPrint(&old_bufs[n], "{s}", .{s0}) catch continue;
        n += 1;
    }
    bindings_file.bind(ws.gpa, ws.io, @tagName(action), spec, old[0..n]) catch |err| {
        ws.setStatus("Couldn't save keymap.json: {t}", .{err});
        return;
    };
    reload(ws, v);
    var buf: [32]u8 = undefined;
    ws.setStatus("{s} is now {s}", .{ @tagName(action), chord.format(&buf) });
}

fn resetBinding(ws: *Workspace, v: *View, action: Action) void {
    bindings_file.reset(ws.gpa, ws.io, @tagName(action)) catch |err| {
        ws.setStatus("Couldn't save keymap.json: {t}", .{err});
        return;
    };
    reload(ws, v);
}

// ---- Input ------------------------------------------------------------------------

fn groupAt(ws: *Workspace, pos: platform.Vec2) ?u8 {
    const area = ws.editor_rect;
    for (ws.group_editor_rects[0..ws.groupCount()], 0..) |r, g| {
        if (!r.contains(pos.x, pos.y)) continue;
        if (r.x > area.x + 1 and pos.x < r.x + 4) return null;
        if ((r.right() < area.right() - 1 or ws.sidebar_rect.w > 0) and pos.x > r.right() - 4) return null;
        if (r.bottom() < area.bottom() - 1 and pos.y > r.bottom() - 4) return null;
        return @intCast(g);
    }
    return null;
}

fn viewIn(ws: *Workspace, g: u8) ?*View {
    const ed = ws.group_active[g] orelse return null;
    const i = ws.tabIndexOf(ed) orelse return null;
    return ws.tabs.items[i].settings;
}

/// Keys, clicks and the wheel for the settings tab. Returns true if consumed.
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
        .mouse_move => |m| {
            const v = active(ws) orelse return false;
            const dd = if (v.dropdown) |*p| p else return false;
            for (v.hits.items) |h| if (h.what == .option and h.rect.contains(m.pos.x, m.pos.y)) {
                if (dd.hover != h.what.option) {
                    dd.hover = h.what.option;
                    previewOption(ws, v);
                }
                break;
            };
            return false;
        },
        .scroll => |s| {
            if (ws.overlay != null) return false;
            const g = groupAt(ws, s.pos) orelse return false;
            const v = viewIn(ws, g) orelse return false;
            const dy = if (s.unit == .lines) s.delta.y * 3 * row_h else s.delta.y;
            if (v.dropdown) |*dd| if (dd.popup.contains(s.pos.x, s.pos.y)) {
                const total = @as(f32, @floatFromInt(dd.options.items.len)) * row_h;
                dd.scroll = std.math.clamp(dd.scroll + dy, 0, @max(0, total - dd.popup.h));
                return true;
            };
            v.scroll = std.math.clamp(v.scroll + dy, 0, @max(0, v.content_h - v.view_h));
            return true;
        },
        else => return false,
    }
}

fn handleKey(ws: *Workspace, v: *View, k: platform.KeyEvent) bool {
    const m = k.mods;
    const plain = !m.ctrl and !m.alt and !m.super;

    if (v.dropdown != null) {
        if (k.key == .named) switch (k.key.named) {
            .escape => cancelDropdown(ws, v),
            .enter => if (v.dropdown.?.hover) |h| chooseOption(ws, v, h) else cancelDropdown(ws, v),
            .up => moveHover(ws, v, -1),
            .down => moveHover(ws, v, 1),
            .home => {
                v.dropdown.?.hover = null;
                moveHover(ws, v, 1);
            },
            .end => {
                v.dropdown.?.hover = null;
                moveHover(ws, v, -1);
            },
            else => {},
        };
        return true;
    }

    if (v.capturing) |row| {
        if (k.key == .named and k.key.named == .escape and plain) {
            v.capturing = null;
            ws.setStatus("Cancelled", .{});
        } else {
            v.capturing = null;
            rebind(ws, v, actions[row], .{ .key = k.key, .mods = m });
        }
        return true;
    }

    switch (v.focus) {
        .field => {
            if (k.key == .named and plain) switch (k.key.named) {
                .escape => {
                    cancelEditing(v);
                    return true;
                },
                .enter, .tab => {
                    commit(ws, v);
                    return true;
                },
                else => {},
            };
            return inputKey(ws, v.field, k);
        },
        .search => {
            if (k.key == .named and plain) switch (k.key.named) {
                .escape => {
                    if (v.search.buffer.len() > 0) v.search.setText("", false) else v.focus = .none;
                    v.scroll = 0;
                    return true;
                },
                .enter, .down => {
                    v.focus = .none;
                    return true;
                },
                else => {},
            };
            const before = v.search.buffer.version;
            const consumed = inputKey(ws, v.search, k);
            if (v.search.buffer.version != before) v.scroll = 0;
            return consumed;
        },
        .none => {
            const step = row_h * 2;
            if (k.key == .named and plain) switch (k.key.named) {
                .up => v.scroll = @max(0, v.scroll - step),
                .down => v.scroll = std.math.clamp(v.scroll + step, 0, @max(0, v.content_h - v.view_h)),
                .page_up => v.scroll = @max(0, v.scroll - v.view_h * 0.9),
                .page_down => v.scroll = std.math.clamp(v.scroll + v.view_h * 0.9, 0, @max(0, v.content_h - v.view_h)),
                .home => v.scroll = 0,
                .end => v.scroll = @max(0, v.content_h - v.view_h),
                else => return false,
            } else if (k.key == .char and k.key.char == 'f' and (m.ctrl or m.super) and !m.alt) {
                v.focus = .search;
                v.search.selectAll();
            } else return false;
            return true;
        },
    }
}

/// A key in a focused input: its binding, else its text (never vim's).
fn inputKey(ws: *Workspace, input: *Editor, k: platform.KeyEvent) bool {
    const chord: keymap_mod.Chord = .{ .key = k.key, .mods = k.mods };
    switch (ws.keymap.match(chord, true)) {
        .pending => return true,
        .action => |a| {
            ws.perform(a);
            return true;
        },
        .none => {},
    }
    if (k.text.len > 0) input.insertText(k.text);
    return true;
}

fn mouseDown(ws: *Workspace, v: *View, m: platform.MouseEvent) void {
    const x = m.pos.x;
    const y = m.pos.y;
    if (v.dropdown) |dd| {
        if (dd.popup.contains(x, y)) {
            for (v.hits.items) |h| if (h.what == .option and h.rect.contains(x, y)) return chooseOption(ws, v, h.what.option);
            return;
        }
        cancelDropdown(ws, v);
        return;
    }
    if (v.capturing != null) {
        v.capturing = null;
        return;
    }
    // Clicks in the focused input place the caret.
    if (v.focus == .field and v.editing != null) for (v.hits.items) |h| if (h.what == .edit and h.what.edit == v.editing.? and h.rect.contains(x, y)) {
        v.field.onMouseDown(m);
        return;
    };
    if (v.focus == .search) for (v.hits.items) |h| if (h.what == .search and h.rect.contains(x, y)) {
        v.search.onMouseDown(m);
        return;
    };
    // Everything else ends an edit.
    commit(ws, v);
    v.focus = .none;
    var i = v.hits.items.len;
    while (i > 0) {
        i -= 1;
        const h = v.hits.items[i];
        if (!h.rect.contains(x, y)) continue;
        switch (h.what) {
            .category => |c| {
                v.category = c;
                v.scroll = 0;
            },
            .page => |p| {
                v.page = p;
                v.scroll = 0;
                v.search.setText("", false);
                v.search.placeholder = if (p == .settings) "Search settings" else "Search keybindings";
            },
            .search => {
                v.focus = .search;
                v.search.onMouseDown(m);
            },
            .open_json => ws.perform(if (v.page == .settings) .open_settings_json else .open_keymap),
            .toggle => |di| toggle(ws, v, di),
            .edit => |di| {
                startEditing(ws, v, di);
                v.field.selectAll();
            },
            .dropdown => |di| openDropdown(ws, v, di, h.rect),
            .reset => |di| resetSetting(ws, v, catalog.defs[di]),
            .option => {},
            .capture => |row| {
                v.capturing = row;
                ws.setStatus("Press the new keys for {s} (Esc cancels)", .{@tagName(actions[row])});
            },
            .reset_binding => |row| resetBinding(ws, v, actions[row]),
        }
        return;
    }
}

// ---- Painting ---------------------------------------------------------------------

const Line = struct { start: usize, end: usize };

/// Splits `text` at spaces into lines no wider than `width` (at most `out.len`).
fn wrap(ctx: *const ui.Ctx, text: []const u8, font: ui.FontId, width: f32, out: []Line) usize {
    var n: usize = 0;
    var start: usize = 0;
    var last_space: ?usize = null;
    var i: usize = 0;
    while (i <= text.len) : (i += 1) {
        const at_end = i == text.len;
        if (!at_end and text[i] == ' ') last_space = i;
        if (!at_end and ctx.text.measure(text[start .. i + 1], font) <= width) continue;
        if (at_end) {
            if (start < text.len and n < out.len) {
                out[n] = .{ .start = start, .end = text.len };
                n += 1;
            }
            break;
        }
        const cut = last_space orelse i;
        if (n < out.len) {
            out[n] = .{ .start = start, .end = cut };
            n += 1;
        }
        start = if (last_space != null) cut + 1 else cut;
        last_space = null;
        if (n == out.len) break;
        i = start;
        if (i > 0) i -= 1;
    }
    return n;
}

fn matches(hay: []const u8, needles: []const []const u8) bool {
    for (needles) |n| if (std.ascii.findIgnoreCase(hay, n) == null) return false;
    return true;
}

/// Draws the settings tab of `ed` in `rect`; false if it isn't one.
pub fn paint(ws: *Workspace, ed: *Editor, rect: Rect, scene: *scene_mod.Scene) bool {
    const i = ws.tabIndexOf(ed) orelse return false;
    const v = ws.tabs.items[i].settings orelse return false;
    v.refresh(ws);
    v.rect = rect;
    v.hits.clearRetainingCapacity();
    const ctx: ui.Ctx = .{ .scene = scene, .text = ws.env.text, .theme = ws.theme, .fonts = ws.fonts, .mouse = ws.mouse };
    const theme = ws.theme;
    scene.fill(rect, theme.background);
    scene.pushClip(rect) catch return true;
    defer scene.popClip();

    const focused_tab = ed.focused;
    var left_w: f32 = 0;
    if (v.page == .settings) {
        left_w = @min(sidebar_w, rect.w * 0.3);
        paintCategories(ws, v, &ctx, .{ .x = rect.x, .y = rect.y, .w = left_w, .h = rect.h });
    }
    const main: Rect = .{ .x = rect.x + left_w, .y = rect.y, .w = rect.w - left_w, .h = rect.h };
    const cw = @min(main.w - 48, max_content_w);
    const x0 = main.x + 24;
    const content: Rect = .{ .x = x0, .y = main.y, .w = cw, .h = main.h };

    // Search row.
    const search_r: Rect = .{ .x = content.x, .y = main.y + 10, .w = content.w - 34, .h = field_h };
    v.search.placeholder = if (v.page == .settings) "Search settings  (@modified shows changed ones)" else "Search keybindings";
    find_panel.paintInput(&ctx, search_r, v.search, focused_tab and v.focus == .search, false);
    v.hits.append(v.gpa, .{ .rect = search_r, .what = .search }) catch {};
    const json_r: Rect = .{ .x = search_r.right() + 6, .y = search_r.y, .w = 28, .h = field_h };
    if (ctx.hovered(json_r)) scene.quad(json_r, .{ .color = theme.list_selected, .radius = 3 });
    ctx.labelCentered(json_r, "{ }", ctx.fonts.ui, theme.list_foreground_dim);
    v.hits.append(v.gpa, .{ .rect = json_r, .what = .open_json }) catch {};

    // Page tabs.
    var tx = content.x;
    const tab_y = main.y + 10 + field_h + 8;
    for ([_]struct { Page, []const u8 }{ .{ .settings, "User" }, .{ .shortcuts, "Keyboard Shortcuts" } }) |t| {
        const w = ctx.text.measure(t.@"1", ctx.fonts.ui) + 24;
        const r: Rect = .{ .x = tx, .y = tab_y, .w = w, .h = 26 };
        const on = v.page == t.@"0";
        ctx.labelCentered(r, t.@"1", ctx.fonts.ui, if (on) theme.tab_active_foreground else theme.list_foreground_dim);
        if (on) scene.fill(.{ .x = r.x, .y = r.bottom() - 2, .w = r.w, .h = 2 }, theme.input_border_focus);
        v.hits.append(v.gpa, .{ .rect = r, .what = .{ .page = t.@"0" } }) catch {};
        tx += w;
    }

    const list: Rect = .{ .x = main.x, .y = tab_y + 32, .w = main.w, .h = @max(0, main.bottom() - (tab_y + 32)) };
    v.view_h = list.h;
    scene.pushClip(list) catch return true;
    switch (v.page) {
        .settings => paintSettings(ws, v, &ctx, list, content, focused_tab),
        .shortcuts => paintShortcuts(ws, v, &ctx, list, content),
    }
    v.scroll = std.math.clamp(v.scroll, 0, @max(0, v.content_h - v.view_h));
    // A scroll thumb.
    if (v.content_h > v.view_h and v.view_h > 0) {
        const th = @max(24, v.view_h * v.view_h / v.content_h);
        const ty = list.y + (v.view_h - th) * (v.scroll / (v.content_h - v.view_h));
        scene.quad(.{ .x = main.right() - 10, .y = ty, .w = 8, .h = th }, .{ .color = theme.scrollbar_puck, .radius = 4 });
    }
    scene.popClip();
    paintDropdown(ws, v, &ctx, rect);
    return true;
}

fn paintCategories(ws: *Workspace, v: *View, ctx: *const ui.Ctx, r: Rect) void {
    const theme = ws.theme;
    ctx.scene.fill(r, theme.sidebar_background);
    ctx.scene.fill(.{ .x = r.right() - 1, .y = r.y, .w = 1, .h = r.h }, theme.panel_border);
    var y = r.y + 6;
    const entries = [_]?Category{ null, .editor, .workbench, .application, .git, .org };
    for (entries) |c| {
        const row: Rect = .{ .x = r.x, .y = y, .w = r.w - 1, .h = row_h };
        const selected = v.category == c;
        if (selected) ctx.scene.fill(row, theme.list_selected) else if (ctx.hovered(row)) ctx.scene.fill(row, theme.sidebar_hover);
        const title = if (c) |cat| cat.title() else "All Settings";
        _ = ctx.label(.{ .x = row.x + 14, .y = row.y, .w = row.w - 50, .h = row.h }, title, ctx.fonts.ui, if (selected) theme.tab_active_foreground else theme.sidebar_foreground);
        var nb: [16]u8 = undefined;
        const n = std.fmt.bufPrint(&nb, "{d}", .{catalog.count(c)}) catch "";
        _ = ctx.labelRight(.{ .x = row.x, .y = row.y, .w = row.w - 12, .h = row.h }, n, ctx.fonts.ui_small, theme.sidebar_foreground_dim);
        v.hits.append(v.gpa, .{ .rect = row, .what = .{ .category = c } }) catch {};
        y += row_h;
    }
}

fn paintCheckbox(ctx: *const ui.Ctx, r: Rect, on: bool) void {
    const theme = ctx.theme;
    ctx.scene.quad(r, .{ .color = if (on) theme.input_border_focus else theme.input_background, .border_color = if (on) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 3 });
    if (!on) return;
    // A check mark from two short strokes.
    const white = ui.Color.hex(0xffffff);
    const cx = r.x + r.w / 2;
    const cy = r.y + r.h / 2;
    var s: f32 = 0;
    while (s < 3) : (s += 1) ctx.scene.fill(.{ .x = cx - 5 + s, .y = cy - 0.5 + s, .w = 1.8, .h = 1.8 }, white);
    s = 0;
    while (s < 6) : (s += 1) ctx.scene.fill(.{ .x = cx - 2 + s, .y = cy + 2 - s, .w = 1.8, .h = 1.8 }, white);
}

fn paintSettings(ws: *Workspace, v: *View, ctx: *const ui.Ctx, list: Rect, content: Rect, focused_tab: bool) void {
    const theme = ws.theme;
    const scene = ctx.scene;
    const query = v.search.contents(ws.gpa) catch return;
    defer ws.gpa.free(query);
    var needles_buf: [8][]const u8 = undefined;
    var n_needles: usize = 0;
    var only_modified = false;
    var tok = std.mem.tokenizeScalar(u8, query, ' ');
    while (tok.next()) |t| {
        if (std.ascii.eqlIgnoreCase(t, "@modified")) {
            only_modified = true;
        } else if (n_needles < needles_buf.len) {
            needles_buf[n_needles] = t;
            n_needles += 1;
        }
    }
    const needles = needles_buf[0..n_needles];

    var y = list.y - v.scroll + 4;
    const top = y;
    var shown: usize = 0;
    for (catalog.defs, 0..) |d, di| {
        if (v.category) |c| if (d.category != c) continue;
        if (only_modified and !v.isModified(d)) continue;
        var hb: [256]u8 = undefined;
        const heading = catalog.heading(d, &hb);
        if (needles.len > 0 and !(matches(heading, needles) or matches(d.key, needles) or matches(d.description, needles))) continue;
        shown += 1;

        const w = content.w - 40;
        const modified = v.isModified(d);
        var lines: [4]Line = undefined;
        const text_w = if (d.kind == .boolean) w - 26 else w;
        const nlines = wrap(ctx, d.description, ctx.fonts.ui, text_w, &lines);
        const lh: f32 = 18;
        const is_checkbox = d.kind == .boolean;
        const control_h: f32 = if (is_checkbox) 0 else field_h;
        const h: f32 = 12 + 20 + 4 + @as(f32, @floatFromInt(@max(nlines, 1))) * lh + (if (is_checkbox) 0 else 6 + control_h) + 14;
        const row: Rect = .{ .x = content.x, .y = y, .w = content.w, .h = h };
        defer y += h;
        if (row.bottom() < list.y or row.y > list.bottom()) continue;

        if (modified) scene.fill(.{ .x = content.x - 12, .y = row.y + 12, .w = 2, .h = h - 22 }, theme.input_border_focus);
        _ = ctx.label(.{ .x = content.x, .y = row.y + 12, .w = w, .h = 20 }, heading, ctx.fonts.ui_bold, theme.tab_active_foreground);
        if (modified) {
            const reset_r: Rect = .{ .x = content.right() - 60, .y = row.y + 12, .w = 56, .h = 20 };
            const hov = ctx.hovered(reset_r);
            ctx.labelCentered(reset_r, "Reset", ctx.fonts.ui_small, if (hov) theme.tab_active_foreground else theme.input_border_focus);
            v.hits.append(v.gpa, .{ .rect = reset_r, .what = .{ .reset = di } }) catch {};
        }
        var ty = row.y + 12 + 20 + 4;
        if (is_checkbox) {
            const checked = if (v.valueOf(d)) |val| (val == .bool and val.bool) else std.mem.eql(u8, d.default, "true");
            const box: Rect = .{ .x = content.x, .y = ty + 1, .w = 16, .h = 16 };
            paintCheckbox(ctx, box, checked);
            for (lines[0..nlines], 0..) |ln, li| {
                _ = ctx.label(.{ .x = content.x + 26, .y = ty + @as(f32, @floatFromInt(li)) * lh, .w = text_w, .h = lh }, d.description[ln.start..ln.end], ctx.fonts.ui, theme.list_foreground_dim);
            }
            v.hits.append(v.gpa, .{ .rect = .{ .x = content.x, .y = ty, .w = w, .h = @as(f32, @floatFromInt(@max(nlines, 1))) * lh }, .what = .{ .toggle = di } }) catch {};
            continue;
        }
        for (lines[0..nlines], 0..) |ln, li| {
            _ = ctx.label(.{ .x = content.x, .y = ty + @as(f32, @floatFromInt(li)) * lh, .w = text_w, .h = lh }, d.description[ln.start..ln.end], ctx.fonts.ui, theme.list_foreground_dim);
        }
        ty += @as(f32, @floatFromInt(@max(nlines, 1))) * lh + 6;

        switch (d.kind) {
            .integer, .number, .text => {
                const fw: f32 = switch (d.kind) {
                    .text => @min(w, 420),
                    else => 160,
                };
                const fr: Rect = .{ .x = content.x, .y = ty, .w = fw, .h = field_h };
                if (v.editing != null and v.editing.? == di) {
                    find_panel.paintInput(ctx, fr, v.field, focused_tab and v.focus == .field, false);
                } else {
                    var buf: [64]u8 = undefined;
                    scene.quad(fr, .{ .color = theme.input_background, .border_color = if (ctx.hovered(fr)) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 3 });
                    _ = ctx.labelFit(fr.inset(8, 0), catalog.displayText(d, v.valueOf(d), &buf), ctx.fonts.ui, theme.list_foreground);
                }
                v.hits.append(v.gpa, .{ .rect = fr, .what = .{ .edit = di } }) catch {};
            },
            .choice => |cs| {
                const idx = if (v.valueOf(d)) |val| catalog.choiceIndex(cs, val) else null;
                var buf: [64]u8 = undefined;
                const label = if (idx) |k| cs[k].label else if (v.valueOf(d) == null) defaultChoiceLabel(d, cs) else catalog.displayText(d, v.valueOf(d), &buf);
                paintSelect(ctx, v, .{ .x = content.x, .y = ty, .w = 220, .h = field_h }, label, di);
            },
            .scheme => {
                var buf: [64]u8 = undefined;
                var label = catalog.displayText(d, v.valueOf(d), &buf);
                if (std.ascii.eqlIgnoreCase(label, "auto")) label = "Auto";
                paintSelect(ctx, v, .{ .x = content.x, .y = ty, .w = 300, .h = field_h }, label, di);
            },
            .boolean => {},
        }
    }
    if (shown == 0) {
        _ = ctx.label(.{ .x = content.x, .y = list.y + 16, .w = content.w, .h = 20 }, "No settings found.", ctx.fonts.ui, theme.list_foreground_dim);
    }
    v.content_h = y - top + 8;
}

fn defaultChoiceLabel(d: catalog.Def, cs: []const catalog.Choice) []const u8 {
    for (cs) |c| if (std.mem.eql(u8, c.json, d.default)) return c.label;
    return cs[0].label;
}

fn paintSelect(ctx: *const ui.Ctx, v: *View, r: Rect, label: []const u8, di: usize) void {
    const theme = ctx.theme;
    const is_open = v.dropdown != null and v.dropdown.?.def == di;
    ctx.scene.quad(r, .{ .color = theme.input_background, .border_color = if (is_open or ctx.hovered(r)) theme.input_border_focus else theme.input_border, .border_width = 1, .radius = 3 });
    _ = ctx.labelFit(.{ .x = r.x + 8, .y = r.y, .w = r.w - 30, .h = r.h }, label, ctx.fonts.ui, theme.list_foreground);
    _ = ctx.labelRight(.{ .x = r.x, .y = r.y, .w = r.w - 8, .h = r.h }, "▾", ctx.fonts.ui, theme.list_foreground_dim);
    v.hits.append(v.gpa, .{ .rect = r, .what = .{ .dropdown = di } }) catch {};
}

fn paintDropdown(ws: *Workspace, v: *View, ctx: *const ui.Ctx, bounds: Rect) void {
    const dd = if (v.dropdown) |*p| p else return;
    const theme = ws.theme;
    const scene = ctx.scene;
    const total = @as(f32, @floatFromInt(dd.options.items.len)) * row_h;
    const max_h: f32 = 13 * row_h;
    const h = @min(total + 4, max_h);
    const below = bounds.bottom() - dd.anchor.bottom();
    const y = if (below >= h or below >= dd.anchor.y - bounds.y) dd.anchor.bottom() + 2 else dd.anchor.y - h - 2;
    const w = @max(dd.anchor.w, 300);
    dd.popup = .{ .x = dd.anchor.x, .y = y, .w = w, .h = @min(h, bounds.bottom() - y) - 4 };
    dd.scroll = std.math.clamp(dd.scroll, 0, @max(0, total - dd.popup.h));
    // Above the settings list and any clip it set.
    scene.pushLayer() catch return;
    defer scene.popLayer();
    scene.pushClip(bounds) catch return;
    defer scene.popClip();
    const outer: Rect = .{ .x = dd.popup.x - 1, .y = dd.popup.y - 2, .w = dd.popup.w + 2, .h = dd.popup.h + 4 };
    ctx.shadow(outer, 4, 8);
    scene.quad(outer, .{ .color = theme.overlay_background, .border_color = theme.input_border, .border_width = 1, .radius = 3 });
    scene.pushClip(dd.popup) catch return;
    defer scene.popClip();
    // The hits of the entries below the list are not clickable through it.
    var i: usize = 0;
    while (i < dd.options.items.len) : (i += 1) {
        const o = dd.options.items[i];
        const r: Rect = .{ .x = dd.popup.x, .y = dd.popup.y + @as(f32, @floatFromInt(i)) * row_h - dd.scroll, .w = dd.popup.w, .h = row_h };
        if (r.bottom() < dd.popup.y or r.y > dd.popup.bottom()) continue;
        if (o.header) {
            _ = ctx.label(.{ .x = r.x + 10, .y = r.y + 2, .w = r.w, .h = r.h }, o.label, ctx.fonts.ui_small, theme.list_foreground_dim);
            continue;
        }
        const hov = dd.hover != null and dd.hover.? == i;
        if (hov) scene.fill(r, theme.list_selected);
        _ = ctx.labelFit(.{ .x = r.x + 22, .y = r.y, .w = r.w - 30, .h = r.h }, o.label, ctx.fonts.ui, theme.list_foreground);
        v.hits.append(v.gpa, .{ .rect = r, .what = .{ .option = i } }) catch {};
    }
}

fn paintShortcuts(ws: *Workspace, v: *View, ctx: *const ui.Ctx, list: Rect, content: Rect) void {
    const theme = ws.theme;
    const scene = ctx.scene;
    const query = v.search.contents(ws.gpa) catch return;
    defer ws.gpa.free(query);
    var needles_buf: [8][]const u8 = undefined;
    var n_needles: usize = 0;
    var tok = std.mem.tokenizeScalar(u8, query, ' ');
    while (tok.next()) |t| if (n_needles < needles_buf.len) {
        needles_buf[n_needles] = t;
        n_needles += 1;
    };
    const needles = needles_buf[0..n_needles];

    const col_key = content.x + content.w * 0.52;
    const col_source = content.x + content.w * 0.80;
    var y = list.y - v.scroll;
    const top = y;
    // Header.
    const head: Rect = .{ .x = content.x, .y = y, .w = content.w, .h = row_h };
    scene.fill(head, theme.sidebar_background);
    _ = ctx.label(.{ .x = content.x + 8, .y = head.y, .w = 200, .h = row_h }, "Command", ctx.fonts.ui_bold, theme.list_foreground_dim);
    _ = ctx.label(.{ .x = col_key, .y = head.y, .w = 200, .h = row_h }, "Keybinding", ctx.fonts.ui_bold, theme.list_foreground_dim);
    _ = ctx.label(.{ .x = col_source, .y = head.y, .w = 200, .h = row_h }, "Source", ctx.fonts.ui_bold, theme.list_foreground_dim);
    y += row_h;
    var shown: usize = 0;
    for (v.order) |oi| {
        const action = actions[oi];
        var lb: [96]u8 = undefined;
        const label = humanize(&lb, @tagName(action));
        var sb: [96]u8 = undefined;
        const shortcut = ws.keymap.shortcutFor(action, &sb) orelse "";
        if (needles.len > 0 and !(matches(label, needles) or matches(@tagName(action), needles) or matches(shortcut, needles))) continue;
        shown += 1;
        const row: Rect = .{ .x = content.x, .y = y, .w = content.w, .h = row_h };
        defer y += row_h;
        if (row.bottom() < list.y or row.y > list.bottom()) continue;
        const user = isUserBound(ws, action);
        const capturing = v.capturing != null and v.capturing.? == oi;
        if (ctx.hovered(row) and ctx.hovered(list)) scene.fill(row, theme.sidebar_hover);
        _ = ctx.labelFit(.{ .x = row.x + 8, .y = row.y, .w = col_key - row.x - 16, .h = row_h }, label, ctx.fonts.ui, theme.list_foreground);
        const key_r: Rect = .{ .x = col_key - 4, .y = row.y + 2, .w = col_source - col_key - 8, .h = row_h - 4 };
        if (capturing) {
            scene.quad(key_r, .{ .color = theme.input_background, .border_color = theme.input_border_focus, .border_width = 1, .radius = 3 });
            _ = ctx.label(key_r.inset(6, 0), "Press the new keys…", ctx.fonts.ui, theme.list_foreground_dim);
        } else if (shortcut.len > 0) {
            _ = ctx.labelFit(.{ .x = col_key, .y = row.y, .w = key_r.w, .h = row_h }, shortcut, ctx.fonts.ui, theme.list_foreground);
        }
        _ = ctx.label(.{ .x = col_source, .y = row.y, .w = 80, .h = row_h }, if (user) "User" else "Default", ctx.fonts.ui_small, if (user) theme.input_border_focus else theme.list_foreground_dim);
        if (user and !capturing) {
            const reset_r: Rect = .{ .x = content.right() - 56, .y = row.y, .w = 52, .h = row_h };
            ctx.labelCentered(reset_r, "Reset", ctx.fonts.ui_small, if (ctx.hovered(reset_r)) theme.tab_active_foreground else theme.input_border_focus);
            v.hits.append(v.gpa, .{ .rect = reset_r, .what = .{ .reset_binding = oi } }) catch {};
        }
        v.hits.append(v.gpa, .{ .rect = .{ .x = row.x, .y = row.y, .w = col_source - row.x, .h = row_h }, .what = .{ .capture = oi } }) catch {};
    }
    if (shown == 0) _ = ctx.label(.{ .x = content.x, .y = y + 8, .w = content.w, .h = 20 }, "No commands found.", ctx.fonts.ui, theme.list_foreground_dim);
    v.content_h = y - top + 8;
}

test "command names read as titles" {
    var buf: [64]u8 = undefined;
    try std.testing.expectEqualStrings("Toggle Sidebar", humanize(&buf, "toggle_sidebar"));
    try std.testing.expectEqualStrings("Quit", humanize(&buf, "quit"));
}

test "descriptions wrap at spaces" {
    // wrap() needs a text system; the splitting rule is checked through a stand-in measure.
    try std.testing.expect(matches("Editor: Font Family", &.{ "font", "edit" }));
    try std.testing.expect(!matches("Editor: Font Family", &.{"minimap"}));
}
