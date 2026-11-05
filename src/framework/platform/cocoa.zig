//! macOS window: an NSWindow with an OpenGL view (`cocoa.m`), whose queued
//! events become platform.Events here.

const std = @import("std");
const c = @import("c");
const platform = @import("platform.zig");
const gl = @import("../gpu/gl.zig");

const Allocator = std.mem.Allocator;
const Event = platform.Event;

pub const Options = struct {
    title: [:0]const u8 = "jiffy-edit",
    width: i32 = 1280,
    height: i32 = 800,
    /// The largest one becomes the Dock icon.
    icons: []const platform.IconImage = &.{},
};

pub const Window = struct {
    gpa: Allocator,
    handle: *c.JeWindow,
    width: f32 = 0,
    height: f32 = 0,
    scale_factor: f32 = 1,
    events: std.ArrayList(Event) = .empty,
    /// Copies of event text, valid until the next `pollEvents`.
    event_arena: std.heap.ArenaAllocator,

    pub fn create(gpa: Allocator, opts: Options) !*Window {
        const handle = c.je_window_create(opts.title.ptr, opts.width, opts.height) orelse return error.CocoaWindowFailed;
        errdefer c.je_window_destroy(handle);
        var largest: ?platform.IconImage = null;
        for (opts.icons) |icon| {
            if (icon.rgba.len == icon.size * icon.size * 4 and (largest == null or icon.size > largest.?.size)) largest = icon;
        }
        if (largest) |icon| c.je_set_app_icon(icon.rgba.ptr, @intCast(icon.size));
        try gl.load(@ptrCast(&c.je_gl_proc_address));
        const self = try gpa.create(Window);
        self.* = .{ .gpa = gpa, .handle = handle, .event_arena = .init(gpa) };
        self.readSize();
        return self;
    }

    pub fn destroy(self: *Window) void {
        c.je_window_destroy(self.handle);
        self.events.deinit(self.gpa);
        self.event_arena.deinit();
        self.gpa.destroy(self);
    }

    fn readSize(self: *Window) void {
        var w: f64 = 0;
        var h: f64 = 0;
        var s: f64 = 1;
        c.je_window_size(self.handle, &w, &h, &s);
        self.width = @floatCast(w);
        self.height = @floatCast(h);
        self.scale_factor = @floatCast(s);
    }

    pub fn scale(self: *const Window) f32 {
        return self.scale_factor;
    }

    pub fn bufferSize(self: *const Window) struct { i32, i32 } {
        return .{
            @intFromFloat(@round(self.width * self.scale_factor)),
            @intFromFloat(@round(self.height * self.scale_factor)),
        };
    }

    pub fn logicalSize(self: *const Window) platform.Vec2 {
        return .{ .x = self.width, .y = self.height };
    }

    /// Blocks until input arrives, one of `extra` becomes ready, or
    /// `timeout_ms` elapses (-1 = forever). Window events land in
    /// `self.events` (valid until the next call); `extra[i].revents` is set.
    pub fn pollEvents(self: *Window, timeout_ms: i32, extra: []platform.PollFd) !void {
        _ = self.event_arena.reset(.retain_capacity);
        self.events.clearRetainingCapacity();
        _ = c.je_poll(self.handle, timeout_ms, @ptrCast(extra.ptr), @intCast(extra.len));
        const raw = c.je_events(self.handle)[0..c.je_event_count(self.handle)];
        for (raw) |*ev| self.translate(ev) catch {};
    }

    fn text(self: *Window, ev: *const c.JeEvent) ![]const u8 {
        if (ev.text_len == 0 or ev.text == null) return "";
        return self.event_arena.allocator().dupe(u8, ev.text[0..ev.text_len]);
    }

    fn mods(m: c_uint) platform.Modifiers {
        return .{
            .shift = m & c.JE_MOD_SHIFT != 0,
            .ctrl = m & c.JE_MOD_CTRL != 0,
            .alt = m & c.JE_MOD_ALT != 0,
            .super = m & c.JE_MOD_SUPER != 0,
        };
    }

    fn key(ev: *const c.JeEvent) ?platform.Key {
        if (ev.named != c.JE_KEY_NONE) return .{ .named = @fromBackingInt(@intCast(ev.named - 1)) };
        if (ev.codepoint == 0 or ev.codepoint > std.math.maxInt(u21)) return null;
        return .{ .char = @intCast(ev.codepoint) };
    }

    fn button(b: c_int) platform.MouseButton {
        return switch (b) {
            1 => .right,
            2 => .middle,
            3 => .back,
            4 => .forward,
            else => .left,
        };
    }

    fn translate(self: *Window, ev: *const c.JeEvent) !void {
        const pos: platform.Vec2 = .{ .x = @floatCast(ev.x), .y = @floatCast(ev.y) };
        const out: Event = switch (ev.kind) {
            c.JE_EV_KEY_DOWN, c.JE_EV_KEY_UP => blk: {
                const k: platform.KeyEvent = .{
                    .key = key(ev) orelse return,
                    .mods = mods(ev.mods),
                    .text = try self.text(ev),
                    .repeat = ev.repeat != 0,
                };
                break :blk if (ev.kind == c.JE_EV_KEY_DOWN) .{ .key_down = k } else .{ .key_up = k };
            },
            c.JE_EV_TEXT_INPUT => .{ .text_input = try self.text(ev) },
            c.JE_EV_PREEDIT => .{ .preedit = .{
                .text = try self.text(ev),
                .cursor_begin = if (ev.cursor_begin >= 0) @intCast(ev.cursor_begin) else null,
                .cursor_end = if (ev.cursor_end >= 0) @intCast(ev.cursor_end) else null,
            } },
            c.JE_EV_MOUSE_DOWN, c.JE_EV_MOUSE_UP => blk: {
                const m: platform.MouseEvent = .{
                    .pos = pos,
                    .button = button(ev.button),
                    .mods = mods(ev.mods),
                    .click_count = @intCast(std.math.clamp(ev.click_count, 1, 255)),
                };
                break :blk if (ev.kind == c.JE_EV_MOUSE_DOWN) .{ .mouse_down = m } else .{ .mouse_up = m };
            },
            c.JE_EV_MOUSE_MOVE => .{ .mouse_move = .{
                .pos = pos,
                .mods = mods(ev.mods),
                .pressed = if (ev.pressed >= 0) button(ev.pressed) else null,
            } },
            c.JE_EV_MOUSE_EXIT => .mouse_exit,
            c.JE_EV_SCROLL => .{ .scroll = .{
                .pos = pos,
                .delta = .{ .x = @floatCast(ev.dx), .y = @floatCast(ev.dy) },
                .unit = if (ev.precise != 0) .pixels else .lines,
                .mods = mods(ev.mods),
            } },
            c.JE_EV_RESIZE => blk: {
                self.width = @floatCast(ev.width);
                self.height = @floatCast(ev.height);
                self.scale_factor = @floatCast(ev.scale);
                break :blk .{ .resize = .{ .width = self.width, .height = self.height, .scale = self.scale_factor } };
            },
            c.JE_EV_FOCUS => .{ .focus = ev.focused != 0 },
            c.JE_EV_CLOSE => .close_requested,
            c.JE_EV_COMMAND => .{ .command = try self.text(ev) },
            else => return,
        };
        try self.events.append(self.gpa, out);
    }

    pub fn canRender(self: *const Window) bool {
        return c.je_can_render(self.handle) != 0;
    }

    pub fn present(self: *Window) void {
        c.je_present(self.handle);
    }

    pub fn setTextInputRect(self: *Window, rect: platform.Rect, enabled: bool) void {
        c.je_set_text_input_rect(self.handle, rect.x, rect.y, rect.w, rect.h, @intFromBool(enabled));
    }

    pub fn setTitle(self: *Window, title: [:0]const u8) void {
        c.je_set_title(self.handle, title.ptr);
    }

    pub fn setCursor(self: *Window, shape: platform.CursorShape) void {
        c.je_set_cursor(self.handle, @backingInt(shape));
    }

    pub fn toggleFullscreen(self: *Window) void {
        c.je_toggle_fullscreen(self.handle);
    }

    pub fn setClipboard(_: *Window, data: []const u8) !void {
        c.je_set_clipboard(data.ptr, data.len);
    }

    pub fn getClipboard(_: *Window, gpa: Allocator) !?[]u8 {
        return getSystemClipboard(gpa);
    }

    /// The pasteboard outlives the app: nothing to hand over on close.
    pub fn ownedClipboard(_: *Window) ?[]const u8 {
        return null;
    }

    pub fn takeOpenedPaths(_: *Window, gpa: Allocator) !?[]u8 {
        var len: usize = 0;
        const raw = c.je_take_opened_paths(&len) orelse return null;
        defer c.free(raw);
        return try gpa.dupe(u8, raw[0..len]);
    }
};

/// NSPasteboard needs no window (system_clipboard.zig).
pub fn setSystemClipboard(text: []const u8) void {
    c.je_set_clipboard(text.ptr, text.len);
}

pub fn getSystemClipboard(gpa: Allocator) ?[]u8 {
    var len: usize = 0;
    const raw = c.je_get_clipboard(&len) orelse return null;
    defer c.free(raw);
    return gpa.dupe(u8, raw[0..len]) catch null;
}
