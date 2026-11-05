//! Native Wayland window: xdg-shell toplevel, server-side decorations,
//! fractional scaling, xkbcommon keyboard handling with key repeat and compose,
//! input methods (text-input-v3), pointer input with click counting, cursor
//! shapes, and the clipboard.
//!
//! Like GPUI's Linux platform, this talks to the compositor directly rather than
//! going through a toolkit; rendering happens through EGL on the same surface.

const std = @import("std");
const c = @import("c");
const platform = @import("platform.zig");
const egl = @import("egl.zig");
const build_options = @import("build_options");

const Allocator = std.mem.Allocator;
const Event = platform.Event;
const log = std.log.scoped(.wayland);

/// xdg_toplevel_icon_manager_v1 when the build has the protocol (build.zig
/// generates it only if wayland-protocols does); otherwise never bound.
const ToplevelIconManager = if (build_options.toplevel_icon) c.struct_xdg_toplevel_icon_manager_v1 else opaque {};

pub const Options = struct {
    title: [:0]const u8 = "jiffy-edit",
    app_id: [:0]const u8 = "jiffy-edit",
    width: i32 = 1280,
    height: i32 = 800,
    icons: []const platform.IconImage = &.{},
};

const text_mimes = [_][:0]const u8{ "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "TEXT", "STRING" };

pub const Window = struct {
    gpa: Allocator,

    display: *c.struct_wl_display,
    registry: *c.struct_wl_registry,
    compositor: ?*c.struct_wl_compositor = null,
    compositor_version: u32 = 0,
    wm_base: ?*c.struct_xdg_wm_base = null,
    seat: ?*c.struct_wl_seat = null,
    decoration_manager: ?*c.struct_zxdg_decoration_manager_v1 = null,
    viewporter: ?*c.struct_wp_viewporter = null,
    fractional_scale_manager: ?*c.struct_wp_fractional_scale_manager_v1 = null,
    cursor_shape_manager: ?*c.struct_wp_cursor_shape_manager_v1 = null,
    data_device_manager: ?*c.struct_wl_data_device_manager = null,
    text_input_manager: ?*c.struct_zwp_text_input_manager_v3 = null,
    toplevel_icon_manager: ?*ToplevelIconManager = null,
    /// Only for the toplevel icon's pixels.
    shm: ?*c.struct_wl_shm = null,

    surface: ?*c.struct_wl_surface = null,
    xdg_surface: ?*c.struct_xdg_surface = null,
    toplevel: ?*c.struct_xdg_toplevel = null,
    decoration: ?*c.struct_zxdg_toplevel_decoration_v1 = null,
    viewport: ?*c.struct_wp_viewport = null,
    fractional_scale: ?*c.struct_wp_fractional_scale_v1 = null,
    egl_window: ?*c.struct_wl_egl_window = null,
    egl: egl.Context = undefined,

    keyboard: ?*c.struct_wl_keyboard = null,
    pointer: ?*c.struct_wl_pointer = null,
    cursor_shape_device: ?*c.struct_wp_cursor_shape_device_v1 = null,
    data_device: ?*c.struct_wl_data_device = null,
    text_input: ?*c.struct_zwp_text_input_v3 = null,

    xkb_context: ?*c.struct_xkb_context = null,
    xkb_keymap: ?*c.struct_xkb_keymap = null,
    xkb_state: ?*c.struct_xkb_state = null,
    compose_state: ?*c.struct_xkb_compose_state = null,
    mods: platform.Modifiers = .{},

    // Geometry. `width`/`height` are logical; the buffer is width*scale.
    width: i32,
    height: i32,
    pending_width: i32 = 0,
    pending_height: i32 = 0,
    scale120: u32 = 120,
    integer_scale: i32 = 1,
    configured: bool = false,
    size_dirty: bool = true,
    fullscreen: bool = false,
    maximized: bool = false,

    frame_pending: bool = false,
    should_close: bool = false,

    // Input state.
    last_serial: u32 = 0,
    pointer_serial: u32 = 0,
    pointer_pos: platform.Vec2 = .{},
    pressed_button: ?platform.MouseButton = null,
    cursor_shape: platform.CursorShape = .default,
    last_click_time: i64 = 0,
    last_click_pos: platform.Vec2 = .{},
    last_click_button: platform.MouseButton = .left,
    click_count: u8 = 0,
    scroll_accum: platform.Vec2 = .{},
    scroll_value120: platform.Vec2 = .{},
    scroll_is_wheel: bool = false,
    scroll_pending: bool = false,

    repeat_rate: i32 = 25,
    repeat_delay: i32 = 400,
    repeat_key: ?u32 = null,
    repeat_deadline: i64 = 0,

    // Clipboard.
    pending_offer: ?*c.struct_wl_data_offer = null,
    pending_offer_mime: ?[:0]const u8 = null,
    selection_offer: ?*c.struct_wl_data_offer = null,
    selection_mime: ?[:0]const u8 = null,
    data_source: ?*c.struct_wl_data_source = null,
    clipboard_text: ?[]u8 = null,

    // Input method (text-input-v3). `ime_wanted`/`ime_rect` are what the app
    // last asked for; `ime_enabled`/`ime_sent_rect` what the compositor has
    // been told. `ime_commits` counts our commit requests: the compositor
    // echoes that count as the serial of each `done` event.
    ime_focused: bool = false,
    ime_wanted: bool = false,
    ime_enabled: bool = false,
    ime_rect: [4]i32 = @splat(0),
    ime_sent_rect: ?[4]i32 = null,
    ime_commits: u32 = 0,
    /// A `done` arrived for a commit older than our latest: hold state
    /// updates until the compositor catches up (or other input moves the caret).
    ime_hold: bool = false,
    // Double-buffered event state, applied on `done`. Kept in `gpa` memory
    // since `done` may arrive in a later `pollEvents` than its events.
    ime_pending_preedit: std.ArrayList(u8) = .empty,
    ime_pending_cursor: ?[2]usize = null,
    ime_pending_commit: std.ArrayList(u8) = .empty,
    /// Preedit currently shown by the app.
    ime_preedit: std.ArrayList(u8) = .empty,
    ime_preedit_cursor: ?[2]usize = null,
    /// The shown preedit was dropped (disable/leave); tell the app.
    ime_clear_preedit: bool = false,

    events: std.ArrayList(Event) = .empty,
    event_arena: std.heap.ArenaAllocator,

    pub fn create(gpa: Allocator, opts: Options) !*Window {
        const display = c.wl_display_connect(null) orelse return error.NoWaylandDisplay;
        errdefer c.wl_display_disconnect(display);
        const registry = c.wl_display_get_registry(display) orelse return error.NoRegistry;

        const self = try gpa.create(Window);
        errdefer gpa.destroy(self);
        self.* = .{
            .gpa = gpa,
            .display = display,
            .registry = registry,
            .width = opts.width,
            .height = opts.height,
            .event_arena = .init(gpa),
        };

        _ = c.wl_registry_add_listener(registry, &registry_listener, self);
        if (c.wl_display_roundtrip(display) < 0) return error.RoundtripFailed;
        if (self.compositor == null or self.wm_base == null) return error.MissingWaylandGlobals;

        self.xkb_context = c.xkb_context_new(c.XKB_CONTEXT_NO_FLAGS);

        const surface = c.wl_compositor_create_surface(self.compositor) orelse return error.SurfaceFailed;
        self.surface = surface;
        _ = c.wl_surface_add_listener(surface, &surface_listener, self);

        self.xdg_surface = c.xdg_wm_base_get_xdg_surface(self.wm_base, surface);
        _ = c.xdg_surface_add_listener(self.xdg_surface, &xdg_surface_listener, self);
        self.toplevel = c.xdg_surface_get_toplevel(self.xdg_surface);
        _ = c.xdg_toplevel_add_listener(self.toplevel, &toplevel_listener, self);
        c.xdg_toplevel_set_title(self.toplevel, opts.title.ptr);
        c.xdg_toplevel_set_app_id(self.toplevel, opts.app_id.ptr);
        if (build_options.toplevel_icon) self.setIcon(opts);
        c.xdg_toplevel_set_min_size(self.toplevel, 320, 200);

        if (self.decoration_manager) |manager| {
            self.decoration = c.zxdg_decoration_manager_v1_get_toplevel_decoration(manager, self.toplevel);
            _ = c.zxdg_toplevel_decoration_v1_add_listener(self.decoration, &decoration_listener, self);
            c.zxdg_toplevel_decoration_v1_set_mode(self.decoration, c.ZXDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE);
        }
        if (self.fractional_scale_manager != null and self.viewporter != null) {
            self.viewport = c.wp_viewporter_get_viewport(self.viewporter, surface);
            self.fractional_scale = c.wp_fractional_scale_manager_v1_get_fractional_scale(self.fractional_scale_manager, surface);
            _ = c.wp_fractional_scale_v1_add_listener(self.fractional_scale, &fractional_scale_listener, self);
        }

        c.wl_surface_commit(surface);
        while (!self.configured) {
            if (c.wl_display_dispatch(display) < 0) return error.DispatchFailed;
        }
        // Give the compositor a chance to send the preferred scale before the first buffer.
        _ = c.wl_display_roundtrip(display);
        self.applyPendingSize();

        self.egl = try egl.Context.initWayland(display);
        const bw, const bh = self.bufferSize();
        self.egl_window = c.wl_egl_window_create(surface, bw, bh) orelse return error.EglWindowFailed;
        try self.egl.createWindowSurface(self.egl_window.?);
        try self.egl.makeCurrent();
        try self.egl.loadGl();
        self.egl.setSwapInterval(0);
        self.applyScaleToSurface();
        return self;
    }

    pub fn destroy(self: *Window) void {
        const gpa = self.gpa;
        self.egl.deinit();
        if (self.egl_window) |w| c.wl_egl_window_destroy(w);
        if (self.data_source) |s| c.wl_data_source_destroy(s);
        if (self.clipboard_text) |t| gpa.free(t);
        if (self.selection_offer) |o| c.wl_data_offer_destroy(o);
        if (self.text_input) |t| c.zwp_text_input_v3_destroy(t);
        if (self.text_input_manager) |m| c.zwp_text_input_manager_v3_destroy(m);
        if (build_options.toplevel_icon) {
            if (self.toplevel_icon_manager) |m| c.xdg_toplevel_icon_manager_v1_destroy(m);
        }
        if (self.shm) |s| c.wl_shm_destroy(s);
        self.ime_pending_preedit.deinit(gpa);
        self.ime_pending_commit.deinit(gpa);
        self.ime_preedit.deinit(gpa);
        if (self.compose_state) |s| c.xkb_compose_state_unref(s);
        if (self.xkb_state) |s| c.xkb_state_unref(s);
        if (self.xkb_keymap) |k| c.xkb_keymap_unref(k);
        if (self.xkb_context) |x| c.xkb_context_unref(x);
        if (self.toplevel) |t| c.xdg_toplevel_destroy(t);
        if (self.xdg_surface) |s| c.xdg_surface_destroy(s);
        if (self.surface) |s| c.wl_surface_destroy(s);
        c.wl_display_disconnect(self.display);
        self.events.deinit(gpa);
        self.event_arena.deinit();
        gpa.destroy(self);
    }

    // ---- Public API -------------------------------------------------------

    pub fn scale(self: *const Window) f32 {
        if (self.fractional_scale != null) return @as(f32, @floatFromInt(self.scale120)) / 120.0;
        return @floatFromInt(self.integer_scale);
    }

    pub fn bufferSize(self: *const Window) struct { i32, i32 } {
        const s = self.scale();
        return .{
            @intFromFloat(@round(@as(f32, @floatFromInt(self.width)) * s)),
            @intFromFloat(@round(@as(f32, @floatFromInt(self.height)) * s)),
        };
    }

    pub fn logicalSize(self: *const Window) platform.Vec2 {
        return .{ .x = @floatFromInt(self.width), .y = @floatFromInt(self.height) };
    }

    /// Blocks until input arrives, one of `extra` becomes ready, key repeat
    /// fires, or `timeout_ms` elapses (-1 = forever). Window events land in
    /// `self.events` (valid until the next call); `extra[i].revents` is set.
    pub fn pollEvents(self: *Window, timeout_ms: i32, extra: []platform.PollFd) !void {
        _ = self.event_arena.reset(.retain_capacity);
        self.events.clearRetainingCapacity();

        while (c.wl_display_prepare_read(self.display) != 0) {
            if (c.wl_display_dispatch_pending(self.display) < 0) return error.WaylandDisconnected;
        }
        _ = c.wl_display_flush(self.display);

        var timeout = timeout_ms;
        if (self.events.items.len > 0 or self.size_dirty or self.ime_clear_preedit) timeout = 0;
        if (self.repeat_key != null) {
            const until: i32 = @intCast(@max(0, self.repeat_deadline - platform.nowMs()));
            timeout = if (timeout < 0) until else @min(timeout, until);
        }

        var fds: [33]platform.PollFd = undefined;
        fds[0] = .{ .fd = c.wl_display_get_fd(self.display) };
        const n_extra = @min(extra.len, fds.len - 1);
        for (extra[0..n_extra], fds[1..][0..n_extra]) |e, *f| f.* = .{ .fd = e.fd, .events = e.events };
        const n = c.poll(@ptrCast(&fds), @intCast(1 + n_extra), timeout);
        if (n > 0 and fds[0].readable()) {
            if (c.wl_display_read_events(self.display) < 0) return error.WaylandDisconnected;
        } else {
            c.wl_display_cancel_read(self.display);
        }
        if (c.wl_display_dispatch_pending(self.display) < 0) return error.WaylandDisconnected;
        for (extra[0..n_extra], fds[1..][0..n_extra]) |*e, f| e.revents = if (n > 0) f.revents else 0;

        if (self.repeat_key) |key| {
            const now = platform.nowMs();
            if (now >= self.repeat_deadline and self.repeat_rate > 0) {
                self.emitKey(key, true, true);
                self.repeat_deadline = now + @divTrunc(1000, self.repeat_rate);
            }
        }

        if (self.ime_clear_preedit) {
            self.ime_clear_preedit = false;
            self.push(.{ .preedit = .{ .text = "" } });
        }
        if (self.size_dirty) self.applyPendingSize();
    }

    /// False while waiting on the compositor's frame callback (or while hidden).
    pub fn canRender(self: *const Window) bool {
        return !self.frame_pending;
    }

    /// Call after drawing: requests the next frame callback and presents.
    pub fn present(self: *Window) void {
        const callback = c.wl_surface_frame(self.surface);
        _ = c.wl_callback_add_listener(callback, &frame_listener, self);
        self.frame_pending = true;
        self.egl.swapBuffers();
    }

    /// Input method: enables text-input-v3 while the app wants text input and
    /// points the candidate window at the caret (`rect` is logical, i.e.
    /// surface-local). Called every frame; only changes reach the compositor.
    pub fn setTextInputRect(self: *Window, rect: platform.Rect, enabled: bool) void {
        const toInt = struct {
            fn f(v: f32) i32 {
                return std.math.lossyCast(i32, v);
            }
        }.f;
        self.ime_wanted = enabled;
        self.ime_rect = .{ toInt(@floor(rect.x)), toInt(@floor(rect.y)), toInt(@ceil(rect.w)), toInt(@ceil(rect.h)) };
        self.syncTextInput();
    }

    pub fn setTitle(self: *Window, title: [:0]const u8) void {
        c.xdg_toplevel_set_title(self.toplevel, title.ptr);
    }

    pub fn setCursor(self: *Window, shape: platform.CursorShape) void {
        if (shape == self.cursor_shape) return;
        self.cursor_shape = shape;
        self.applyCursor();
    }

    pub fn toggleFullscreen(self: *Window) void {
        if (self.fullscreen) c.xdg_toplevel_unset_fullscreen(self.toplevel) else c.xdg_toplevel_set_fullscreen(self.toplevel, null);
    }

    pub fn setClipboard(self: *Window, text: []const u8) !void {
        const manager = self.data_device_manager orelse return;
        const device = self.data_device orelse return;
        const owned = try self.gpa.dupe(u8, text);
        if (self.clipboard_text) |old| self.gpa.free(old);
        self.clipboard_text = owned;
        if (self.data_source) |old| c.wl_data_source_destroy(old);
        const source = c.wl_data_device_manager_create_data_source(manager);
        self.data_source = source;
        _ = c.wl_data_source_add_listener(source, &data_source_listener, self);
        for (text_mimes[0..3]) |mime| c.wl_data_source_offer(source, mime.ptr);
        c.wl_data_device_set_selection(device, source, self.last_serial);
    }

    /// The text we put on the clipboard, while it's still ours (it goes
    /// when this window does).
    pub fn ownedClipboard(self: *Window) ?[]const u8 {
        return if (self.data_source != null) self.clipboard_text else null;
    }

    /// Returns the clipboard contents (caller frees), or null if empty / not text.
    pub fn getClipboard(self: *Window, gpa: Allocator) !?[]u8 {
        if (self.data_source != null) {
            if (self.clipboard_text) |t| return try gpa.dupe(u8, t);
        }
        const offer = self.selection_offer orelse return null;
        const mime = self.selection_mime orelse return null;
        var fds: [2]c_int = undefined;
        if (c.pipe2(&fds, c.O_CLOEXEC) != 0) return error.PipeFailed;
        c.wl_data_offer_receive(offer, mime.ptr, fds[1]);
        _ = c.close(fds[1]);
        _ = c.wl_display_flush(self.display);
        defer _ = c.close(fds[0]);

        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(gpa);
        var buf: [16384]u8 = undefined;
        while (true) {
            var pfd = [_]c.struct_pollfd{.{ .fd = fds[0], .events = c.POLLIN, .revents = 0 }};
            if (c.poll(&pfd, 1, 1000) <= 0) break; // give up on unresponsive sources
            const n = c.read(fds[0], &buf, buf.len);
            if (n <= 0) break;
            try out.appendSlice(gpa, buf[0..@intCast(n)]);
        }
        return try out.toOwnedSlice(gpa);
    }

    // ---- Internals --------------------------------------------------------

    fn push(self: *Window, event: Event) void {
        switch (event) {
            // Input that bypasses the input method moves the caret on the
            // client's own account; that is reported regardless of `ime_hold`.
            .key_down, .mouse_down, .scroll, .resize => self.ime_hold = false,
            else => {},
        }
        self.events.append(self.gpa, event) catch log.err("dropping event: out of memory", .{});
    }

    fn applyPendingSize(self: *Window) void {
        if (self.pending_width > 0) self.width = self.pending_width;
        if (self.pending_height > 0) self.height = self.pending_height;
        self.size_dirty = false;
        if (self.egl_window) |w| {
            const bw, const bh = self.bufferSize();
            c.wl_egl_window_resize(w, bw, bh, 0, 0);
            self.applyScaleToSurface();
        }
        self.push(.{ .resize = .{
            .width = @floatFromInt(self.width),
            .height = @floatFromInt(self.height),
            .scale = self.scale(),
        } });
    }

    fn applyScaleToSurface(self: *Window) void {
        if (self.viewport) |vp| {
            c.wp_viewport_set_destination(vp, self.width, self.height);
        } else if (self.compositor_version >= 3) {
            c.wl_surface_set_buffer_scale(self.surface, self.integer_scale);
        }
    }

    fn applyCursor(self: *Window) void {
        const device = self.cursor_shape_device orelse return;
        const shape: u32 = switch (self.cursor_shape) {
            .default => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_DEFAULT,
            .text => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_TEXT,
            .pointer => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_POINTER,
            .ew_resize => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_EW_RESIZE,
            .ns_resize => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_NS_RESIZE,
            .grab => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_GRAB,
            .grabbing => c.WP_CURSOR_SHAPE_DEVICE_V1_SHAPE_GRABBING,
        };
        c.wp_cursor_shape_device_v1_set_shape(device, self.pointer_serial, shape);
    }

    /// Gives the toplevel its icon (xdg-toplevel-icon-v1): the icon theme's
    /// icon named `app_id` if the compositor finds one, else `icons`. Must
    /// come before the first commit, which applies it. Without the protocol,
    /// or if anything fails, the compositor goes by the desktop entry.
    fn setIcon(self: *Window, opts: Options) void {
        const manager = self.toplevel_icon_manager orelse return;
        const shm = self.shm orelse return;
        if (opts.icons.len == 0) return;
        const pool = iconPool(shm, opts.icons) catch |err| {
            log.warn("no window icon: {t}", .{err});
            return;
        };
        defer c.wl_shm_pool_destroy(pool);
        const buffers = self.gpa.alloc(*c.struct_wl_buffer, opts.icons.len) catch return;
        defer self.gpa.free(buffers);
        var count: usize = 0;
        defer for (buffers[0..count]) |buffer| c.wl_buffer_destroy(buffer);
        var offset: usize = 0;
        for (opts.icons) |image| {
            const side: i32 = @intCast(image.size);
            buffers[count] = c.wl_shm_pool_create_buffer(pool, @intCast(offset), side, side, side * 4, c.WL_SHM_FORMAT_ARGB8888) orelse return;
            count += 1;
            offset += image.rgba.len;
        }

        const icon = c.xdg_toplevel_icon_manager_v1_create_icon(manager) orelse return;
        // Destroyed before its buffers, which the protocol wants alive while
        // it exists. set_icon has the compositor take what it needs (the
        // icon may go right after), so nothing outlives this call.
        defer c.xdg_toplevel_icon_v1_destroy(icon);
        // First, so a compositor that finds the themed icon skips the pixels.
        c.xdg_toplevel_icon_v1_set_name(icon, opts.app_id.ptr);
        for (buffers) |buffer| c.xdg_toplevel_icon_v1_add_buffer(icon, buffer, 1);
        c.xdg_toplevel_icon_manager_v1_set_icon(manager, self.toplevel, icon);
    }

    /// A wl_shm pool holding `icons` back to back as ARGB8888 (premultiplied
    /// alpha, B G R A in memory).
    fn iconPool(shm: *c.struct_wl_shm, icons: []const platform.IconImage) !*c.struct_wl_shm_pool {
        var size: usize = 0;
        for (icons) |image| size += image.rgba.len;
        const fd = try std.posix.memfd_create("jiffy-edit-icon", std.posix.MFD.CLOEXEC);
        defer _ = c.close(fd);
        if (c.ftruncate(fd, @intCast(size)) != 0) return error.IconBufferFailed;
        const map = try std.posix.mmap(null, size, .{ .READ = true, .WRITE = true }, .{ .TYPE = .SHARED }, fd, 0);
        defer std.posix.munmap(map);
        var offset: usize = 0;
        for (icons) |image| {
            for (0..image.rgba.len / 4) |i| {
                const src = image.rgba[i * 4 ..][0..4];
                const a: u16 = src[3];
                map[offset + i * 4 ..][0..4].* = .{ premultiply(src[2], a), premultiply(src[1], a), premultiply(src[0], a), src[3] };
            }
            offset += image.rgba.len;
        }
        // libwayland sends a duplicate of the fd, so ours can be closed.
        return c.wl_shm_create_pool(shm, fd, @intCast(size)) orelse error.IconBufferFailed;
    }

    fn premultiply(channel: u8, alpha: u16) u8 {
        return @intCast((channel * alpha + 127) / 255);
    }

    /// Brings the compositor's text-input state in line with what the app
    /// asked for. Requests are double-buffered and only take effect on
    /// `commit`, so one commit covers everything that changed.
    fn syncTextInput(self: *Window) void {
        const ti = self.text_input orelse return;
        // Before `enter` (and after `leave`) the compositor ignores requests.
        if (!self.ime_focused) return;
        var changed = false;
        if (self.ime_wanted != self.ime_enabled) {
            // Both reset all text-input state, so it's sent again from scratch.
            self.ime_enabled = self.ime_wanted;
            self.ime_sent_rect = null;
            self.ime_hold = false;
            self.resetImeText();
            if (self.ime_enabled) {
                c.zwp_text_input_v3_enable(ti);
                c.zwp_text_input_v3_set_content_type(ti, c.ZWP_TEXT_INPUT_V3_CONTENT_HINT_NONE, c.ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_NORMAL);
            } else {
                c.zwp_text_input_v3_disable(ti);
            }
            changed = true;
        }
        if (self.ime_enabled and !self.ime_hold) {
            const r = self.ime_rect;
            if (self.ime_sent_rect == null or !std.mem.eql(i32, &self.ime_sent_rect.?, &r)) {
                c.zwp_text_input_v3_set_cursor_rectangle(ti, r[0], r[1], r[2], r[3]);
                self.ime_sent_rect = r;
                changed = true;
            }
        }
        if (changed) {
            c.zwp_text_input_v3_commit(ti);
            self.ime_commits +%= 1;
        }
    }

    /// Drops input-method text state. The app's preedit is cleared from
    /// `pollEvents`, since this may run outside event dispatch (from
    /// `setTextInputRect`), where pushed events would be lost.
    fn resetImeText(self: *Window) void {
        self.ime_pending_preedit.clearRetainingCapacity();
        self.ime_pending_cursor = null;
        self.ime_pending_commit.clearRetainingCapacity();
        if (self.ime_preedit.items.len > 0) self.ime_clear_preedit = true;
        self.ime_preedit.clearRetainingCapacity();
        self.ime_preedit_cursor = null;
    }

    fn updateModifiers(self: *Window) void {
        const state = self.xkb_state orelse return;
        const active = struct {
            fn f(s: *c.struct_xkb_state, name: [*:0]const u8) bool {
                return c.xkb_state_mod_name_is_active(s, name, c.XKB_STATE_MODS_EFFECTIVE) > 0;
            }
        }.f;
        self.mods = .{
            .shift = active(state, c.XKB_MOD_NAME_SHIFT),
            .ctrl = active(state, c.XKB_MOD_NAME_CTRL),
            .alt = active(state, c.XKB_MOD_NAME_ALT),
            .super = active(state, c.XKB_MOD_NAME_LOGO),
        };
    }

    fn emitKey(self: *Window, keycode: u32, pressed: bool, repeat: bool) void {
        const state = self.xkb_state orelse return;
        const keymap = self.xkb_keymap orelse return;
        const sym = c.xkb_state_key_get_one_sym(state, keycode);

        // Bindings match on the unshifted keysym of the active layout.
        var base_sym = sym;
        var syms: [*c]const c.xkb_keysym_t = null;
        const layout = c.xkb_state_key_get_layout(state, keycode);
        if (c.xkb_keymap_key_get_syms_by_level(keymap, keycode, layout, 0, &syms) > 0) base_sym = syms[0];
        const key = keyFromSym(base_sym) orelse keyFromSym(sym) orelse return;

        var text: []const u8 = "";
        if (pressed and !self.mods.ctrl and !self.mods.alt and !self.mods.super) {
            var buf: [64]u8 = undefined;
            var len: usize = 0;
            var composed = false;
            if (self.compose_state) |cs| {
                if (!repeat) _ = c.xkb_compose_state_feed(cs, sym);
                switch (c.xkb_compose_state_get_status(cs)) {
                    c.XKB_COMPOSE_COMPOSING => return,
                    c.XKB_COMPOSE_COMPOSED => {
                        len = @intCast(@max(0, c.xkb_compose_state_get_utf8(cs, &buf, buf.len)));
                        c.xkb_compose_state_reset(cs);
                        composed = true;
                    },
                    c.XKB_COMPOSE_CANCELLED => {
                        c.xkb_compose_state_reset(cs);
                        return;
                    },
                    else => {},
                }
            }
            if (!composed) len = @intCast(@max(0, c.xkb_state_key_get_utf8(state, keycode, &buf, buf.len)));
            len = @min(len, buf.len);
            if (len > 0 and buf[0] >= 0x20 and buf[0] != 0x7f) {
                text = self.event_arena.allocator().dupe(u8, buf[0..len]) catch "";
            }
        }

        const ev: platform.KeyEvent = .{ .key = key, .mods = self.mods, .text = text, .repeat = repeat };
        self.push(if (pressed) .{ .key_down = ev } else .{ .key_up = ev });
    }

    fn keyFromSym(sym: c.xkb_keysym_t) ?platform.Key {
        const named: ?platform.NamedKey = switch (sym) {
            c.XKB_KEY_Return, c.XKB_KEY_KP_Enter => .enter,
            c.XKB_KEY_Tab, c.XKB_KEY_ISO_Left_Tab => .tab,
            c.XKB_KEY_BackSpace => .backspace,
            c.XKB_KEY_Delete, c.XKB_KEY_KP_Delete => .delete,
            c.XKB_KEY_Escape => .escape,
            c.XKB_KEY_Left, c.XKB_KEY_KP_Left => .left,
            c.XKB_KEY_Right, c.XKB_KEY_KP_Right => .right,
            c.XKB_KEY_Up, c.XKB_KEY_KP_Up => .up,
            c.XKB_KEY_Down, c.XKB_KEY_KP_Down => .down,
            c.XKB_KEY_Home, c.XKB_KEY_KP_Home => .home,
            c.XKB_KEY_End, c.XKB_KEY_KP_End => .end,
            c.XKB_KEY_Page_Up, c.XKB_KEY_KP_Page_Up => .page_up,
            c.XKB_KEY_Page_Down, c.XKB_KEY_KP_Page_Down => .page_down,
            c.XKB_KEY_Insert, c.XKB_KEY_KP_Insert => .insert,
            c.XKB_KEY_Menu => .context_menu,
            c.XKB_KEY_F1 => .f1,
            c.XKB_KEY_F2 => .f2,
            c.XKB_KEY_F3 => .f3,
            c.XKB_KEY_F4 => .f4,
            c.XKB_KEY_F5 => .f5,
            c.XKB_KEY_F6 => .f6,
            c.XKB_KEY_F7 => .f7,
            c.XKB_KEY_F8 => .f8,
            c.XKB_KEY_F9 => .f9,
            c.XKB_KEY_F10 => .f10,
            c.XKB_KEY_F11 => .f11,
            c.XKB_KEY_F12 => .f12,
            else => null,
        };
        if (named) |n| return .{ .named = n };
        const cp = c.xkb_keysym_to_utf32(sym);
        if (cp == 0 or cp > 0x10FFFF) return null;
        const lower: u21 = if (cp < 128) std.ascii.toLower(@intCast(cp)) else @intCast(cp);
        return .{ .char = lower };
    }

    fn handleButton(self: *Window, button: platform.MouseButton, pressed: bool) void {
        if (pressed) {
            const now = platform.nowMs();
            const dx = self.pointer_pos.x - self.last_click_pos.x;
            const dy = self.pointer_pos.y - self.last_click_pos.y;
            if (button == self.last_click_button and now - self.last_click_time < 400 and dx * dx + dy * dy < 25) {
                self.click_count +%= 1;
            } else {
                self.click_count = 1;
            }
            self.last_click_time = now;
            self.last_click_pos = self.pointer_pos;
            self.last_click_button = button;
            self.pressed_button = button;
            self.push(.{ .mouse_down = .{ .pos = self.pointer_pos, .button = button, .mods = self.mods, .click_count = self.click_count } });
        } else {
            if (self.pressed_button == button) self.pressed_button = null;
            self.push(.{ .mouse_up = .{ .pos = self.pointer_pos, .button = button, .mods = self.mods, .click_count = self.click_count } });
        }
    }

    fn fromData(data: ?*anyopaque) *Window {
        return @ptrCast(@alignCast(data.?));
    }

    // ---- Listeners --------------------------------------------------------

    const registry_listener: c.struct_wl_registry_listener = .{
        .global = registryGlobal,
        .global_remove = registryGlobalRemove,
    };

    fn registryGlobal(data: ?*anyopaque, registry: ?*c.struct_wl_registry, name: u32, interface: [*c]const u8, version: u32) callconv(.c) void {
        const self = fromData(data);
        const iface = std.mem.span(interface);
        const eql = std.mem.eql;
        if (eql(u8, iface, "wl_compositor")) {
            self.compositor_version = @min(version, 6);
            self.compositor = @ptrCast(c.wl_registry_bind(registry, name, &c.wl_compositor_interface, self.compositor_version));
        } else if (eql(u8, iface, "xdg_wm_base")) {
            self.wm_base = @ptrCast(c.wl_registry_bind(registry, name, &c.xdg_wm_base_interface, @min(version, 6)));
            _ = c.xdg_wm_base_add_listener(self.wm_base, &wm_base_listener, self);
        } else if (eql(u8, iface, "wl_seat")) {
            self.seat = @ptrCast(c.wl_registry_bind(registry, name, &c.wl_seat_interface, @min(version, 9)));
            _ = c.wl_seat_add_listener(self.seat, &seat_listener, self);
        } else if (eql(u8, iface, "zxdg_decoration_manager_v1")) {
            self.decoration_manager = @ptrCast(c.wl_registry_bind(registry, name, &c.zxdg_decoration_manager_v1_interface, 1));
        } else if (eql(u8, iface, "wp_viewporter")) {
            self.viewporter = @ptrCast(c.wl_registry_bind(registry, name, &c.wp_viewporter_interface, 1));
        } else if (eql(u8, iface, "wp_fractional_scale_manager_v1")) {
            self.fractional_scale_manager = @ptrCast(c.wl_registry_bind(registry, name, &c.wp_fractional_scale_manager_v1_interface, 1));
        } else if (eql(u8, iface, "wp_cursor_shape_manager_v1")) {
            self.cursor_shape_manager = @ptrCast(c.wl_registry_bind(registry, name, &c.wp_cursor_shape_manager_v1_interface, 1));
        } else if (eql(u8, iface, "wl_data_device_manager")) {
            self.data_device_manager = @ptrCast(c.wl_registry_bind(registry, name, &c.wl_data_device_manager_interface, @min(version, 3)));
        } else if (eql(u8, iface, "zwp_text_input_manager_v3")) {
            // Version 1: v2 only adds actions/hints we don't use, and defers
            // the cursor rectangle to the next surface commit.
            self.text_input_manager = @ptrCast(c.wl_registry_bind(registry, name, &c.zwp_text_input_manager_v3_interface, 1));
        } else if (build_options.toplevel_icon and eql(u8, iface, "xdg_toplevel_icon_manager_v1")) {
            self.toplevel_icon_manager = @ptrCast(c.wl_registry_bind(registry, name, &c.xdg_toplevel_icon_manager_v1_interface, 1));
        } else if (build_options.toplevel_icon and eql(u8, iface, "wl_shm")) {
            self.shm = @ptrCast(c.wl_registry_bind(registry, name, &c.wl_shm_interface, 1));
        }
        self.setupSeatDevices();
    }

    fn registryGlobalRemove(_: ?*anyopaque, _: ?*c.struct_wl_registry, _: u32) callconv(.c) void {}

    /// Data device / cursor shape device / text input need both the seat and
    /// their manager, which may be announced in either order.
    fn setupSeatDevices(self: *Window) void {
        const seat = self.seat orelse return;
        if (self.data_device == null) {
            if (self.data_device_manager) |m| {
                self.data_device = c.wl_data_device_manager_get_data_device(m, seat);
                _ = c.wl_data_device_add_listener(self.data_device, &data_device_listener, self);
            }
        }
        if (self.text_input == null) {
            if (self.text_input_manager) |m| {
                self.text_input = c.zwp_text_input_manager_v3_get_text_input(m, seat);
                _ = c.zwp_text_input_v3_add_listener(self.text_input, &text_input_listener, self);
            }
        }
        if (self.cursor_shape_device == null) {
            if (self.cursor_shape_manager) |m| {
                if (self.pointer) |p| self.cursor_shape_device = c.wp_cursor_shape_manager_v1_get_pointer(m, p);
            }
        }
    }

    const wm_base_listener: c.struct_xdg_wm_base_listener = .{ .ping = wmBasePing };
    fn wmBasePing(_: ?*anyopaque, wm_base: ?*c.struct_xdg_wm_base, serial: u32) callconv(.c) void {
        c.xdg_wm_base_pong(wm_base, serial);
    }

    const surface_listener: c.struct_wl_surface_listener = .{
        .enter = surfaceEnter,
        .leave = surfaceLeave,
        .preferred_buffer_scale = surfacePreferredBufferScale,
        .preferred_buffer_transform = surfacePreferredBufferTransform,
    };
    fn surfaceEnter(_: ?*anyopaque, _: ?*c.struct_wl_surface, _: ?*c.struct_wl_output) callconv(.c) void {}
    fn surfaceLeave(_: ?*anyopaque, _: ?*c.struct_wl_surface, _: ?*c.struct_wl_output) callconv(.c) void {}
    fn surfacePreferredBufferScale(data: ?*anyopaque, _: ?*c.struct_wl_surface, factor: i32) callconv(.c) void {
        const self = fromData(data);
        if (self.integer_scale != factor and factor > 0) {
            self.integer_scale = factor;
            if (self.fractional_scale == null) self.size_dirty = true;
        }
    }
    fn surfacePreferredBufferTransform(_: ?*anyopaque, _: ?*c.struct_wl_surface, _: u32) callconv(.c) void {}

    const fractional_scale_listener: c.struct_wp_fractional_scale_v1_listener = .{ .preferred_scale = preferredScale };
    fn preferredScale(data: ?*anyopaque, _: ?*c.struct_wp_fractional_scale_v1, scale120: u32) callconv(.c) void {
        const self = fromData(data);
        if (self.scale120 != scale120) {
            self.scale120 = scale120;
            self.size_dirty = true;
        }
    }

    const xdg_surface_listener: c.struct_xdg_surface_listener = .{ .configure = xdgSurfaceConfigure };
    fn xdgSurfaceConfigure(data: ?*anyopaque, xdg_surface: ?*c.struct_xdg_surface, serial: u32) callconv(.c) void {
        const self = fromData(data);
        c.xdg_surface_ack_configure(xdg_surface, serial);
        self.configured = true;
    }

    const toplevel_listener: c.struct_xdg_toplevel_listener = .{
        .configure = toplevelConfigure,
        .close = toplevelClose,
        .configure_bounds = toplevelConfigureBounds,
        .wm_capabilities = toplevelWmCapabilities,
    };
    fn toplevelConfigure(data: ?*anyopaque, _: ?*c.struct_xdg_toplevel, width: i32, height: i32, states: [*c]c.struct_wl_array) callconv(.c) void {
        const self = fromData(data);
        if (width > 0 and height > 0 and (width != self.width or height != self.height)) {
            self.pending_width = width;
            self.pending_height = height;
            self.size_dirty = true;
        }
        self.fullscreen = false;
        self.maximized = false;
        if (states != null and states.*.data != null) {
            const items: [*]const u32 = @ptrCast(@alignCast(states.*.data));
            for (items[0 .. states.*.size / 4]) |state| switch (state) {
                c.XDG_TOPLEVEL_STATE_FULLSCREEN => self.fullscreen = true,
                c.XDG_TOPLEVEL_STATE_MAXIMIZED => self.maximized = true,
                else => {},
            };
        }
    }
    fn toplevelClose(data: ?*anyopaque, _: ?*c.struct_xdg_toplevel) callconv(.c) void {
        const self = fromData(data);
        self.should_close = true;
        self.push(.close_requested);
    }
    fn toplevelConfigureBounds(_: ?*anyopaque, _: ?*c.struct_xdg_toplevel, _: i32, _: i32) callconv(.c) void {}
    fn toplevelWmCapabilities(_: ?*anyopaque, _: ?*c.struct_xdg_toplevel, _: [*c]c.struct_wl_array) callconv(.c) void {}

    const decoration_listener: c.struct_zxdg_toplevel_decoration_v1_listener = .{ .configure = decorationConfigure };
    fn decorationConfigure(_: ?*anyopaque, _: ?*c.struct_zxdg_toplevel_decoration_v1, _: u32) callconv(.c) void {}

    const frame_listener: c.struct_wl_callback_listener = .{ .done = frameDone };
    fn frameDone(data: ?*anyopaque, callback: ?*c.struct_wl_callback, _: u32) callconv(.c) void {
        const self = fromData(data);
        c.wl_callback_destroy(callback);
        self.frame_pending = false;
    }

    const seat_listener: c.struct_wl_seat_listener = .{ .capabilities = seatCapabilities, .name = seatName };
    fn seatCapabilities(data: ?*anyopaque, seat: ?*c.struct_wl_seat, caps: u32) callconv(.c) void {
        const self = fromData(data);
        if (caps & c.WL_SEAT_CAPABILITY_KEYBOARD != 0 and self.keyboard == null) {
            self.keyboard = c.wl_seat_get_keyboard(seat);
            _ = c.wl_keyboard_add_listener(self.keyboard, &keyboard_listener, self);
        }
        if (caps & c.WL_SEAT_CAPABILITY_POINTER != 0 and self.pointer == null) {
            self.pointer = c.wl_seat_get_pointer(seat);
            _ = c.wl_pointer_add_listener(self.pointer, &pointer_listener, self);
        }
        self.setupSeatDevices();
    }
    fn seatName(_: ?*anyopaque, _: ?*c.struct_wl_seat, _: [*c]const u8) callconv(.c) void {}

    const keyboard_listener: c.struct_wl_keyboard_listener = .{
        .keymap = keyboardKeymap,
        .enter = keyboardEnter,
        .leave = keyboardLeave,
        .key = keyboardKey,
        .modifiers = keyboardModifiers,
        .repeat_info = keyboardRepeatInfo,
    };
    fn keyboardKeymap(data: ?*anyopaque, _: ?*c.struct_wl_keyboard, format: u32, fd: i32, size: u32) callconv(.c) void {
        const self = fromData(data);
        defer _ = c.close(fd);
        if (format != c.WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1) return;
        const map = std.posix.mmap(null, size, .{ .READ = true }, .{ .TYPE = .PRIVATE }, fd, 0) catch return;
        defer std.posix.munmap(map);
        const keymap = c.xkb_keymap_new_from_string(self.xkb_context, map.ptr, c.XKB_KEYMAP_FORMAT_TEXT_V1, c.XKB_KEYMAP_COMPILE_NO_FLAGS) orelse return;
        if (self.xkb_state) |s| c.xkb_state_unref(s);
        if (self.xkb_keymap) |k| c.xkb_keymap_unref(k);
        self.xkb_keymap = keymap;
        self.xkb_state = c.xkb_state_new(keymap);

        if (self.compose_state == null) {
            const locale = std.c.getenv("LC_ALL") orelse std.c.getenv("LC_CTYPE") orelse std.c.getenv("LANG") orelse "C";
            if (c.xkb_compose_table_new_from_locale(self.xkb_context, locale, c.XKB_COMPOSE_COMPILE_NO_FLAGS)) |table| {
                self.compose_state = c.xkb_compose_state_new(table, c.XKB_COMPOSE_STATE_NO_FLAGS);
                c.xkb_compose_table_unref(table);
            }
        }
    }
    fn keyboardEnter(data: ?*anyopaque, _: ?*c.struct_wl_keyboard, serial: u32, _: ?*c.struct_wl_surface, _: [*c]c.struct_wl_array) callconv(.c) void {
        const self = fromData(data);
        self.last_serial = serial;
        self.push(.{ .focus = true });
    }
    fn keyboardLeave(data: ?*anyopaque, _: ?*c.struct_wl_keyboard, _: u32, _: ?*c.struct_wl_surface) callconv(.c) void {
        const self = fromData(data);
        self.repeat_key = null;
        self.mods = .{};
        self.push(.{ .focus = false });
    }
    fn keyboardKey(data: ?*anyopaque, _: ?*c.struct_wl_keyboard, serial: u32, _: u32, key: u32, state: u32) callconv(.c) void {
        const self = fromData(data);
        self.last_serial = serial;
        const keycode = key + 8;
        const pressed = state == c.WL_KEYBOARD_KEY_STATE_PRESSED;
        self.emitKey(keycode, pressed, false);
        if (pressed) {
            if (self.xkb_keymap != null and c.xkb_keymap_key_repeats(self.xkb_keymap, keycode) != 0) {
                self.repeat_key = keycode;
                self.repeat_deadline = platform.nowMs() + self.repeat_delay;
            }
        } else if (self.repeat_key == keycode) {
            self.repeat_key = null;
        }
    }
    fn keyboardModifiers(data: ?*anyopaque, _: ?*c.struct_wl_keyboard, _: u32, depressed: u32, latched: u32, locked: u32, group: u32) callconv(.c) void {
        const self = fromData(data);
        if (self.xkb_state) |s| _ = c.xkb_state_update_mask(s, depressed, latched, locked, 0, 0, group);
        self.updateModifiers();
    }
    fn keyboardRepeatInfo(data: ?*anyopaque, _: ?*c.struct_wl_keyboard, rate: i32, delay: i32) callconv(.c) void {
        const self = fromData(data);
        self.repeat_rate = rate;
        self.repeat_delay = delay;
    }

    // Keys the input method consumes never arrive on wl_keyboard; its results
    // come here as preedit/commit strings instead. Keys it passes through
    // arrive as ordinary key events and produce text via xkb as usual, so
    // each key press yields text from exactly one of the two paths.
    const text_input_listener: c.struct_zwp_text_input_v3_listener = .{
        .enter = textInputEnter,
        .leave = textInputLeave,
        .preedit_string = textInputPreeditString,
        .commit_string = textInputCommitString,
        .delete_surrounding_text = textInputDeleteSurroundingText,
        .done = textInputDone,
        // Version 2 events; we bind version 1, so these are never sent.
        .action = textInputAction,
        .language = textInputLanguage,
        .preedit_hint = textInputPreeditHint,
    };
    fn textInputEnter(data: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, surface: ?*c.struct_wl_surface) callconv(.c) void {
        const self = fromData(data);
        if (surface == null or surface != self.surface) return;
        // Enter invalidates all text-input state: enable again if wanted.
        self.ime_focused = true;
        self.ime_enabled = false;
        self.ime_hold = false;
        self.resetImeText();
        self.syncTextInput();
    }
    fn textInputLeave(data: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, surface: ?*c.struct_wl_surface) callconv(.c) void {
        const self = fromData(data);
        if (surface != null and surface != self.surface) return;
        // Requests are ignored until the next enter; there's nothing to send.
        self.ime_focused = false;
        self.ime_enabled = false;
        self.ime_hold = false;
        self.resetImeText();
    }
    fn textInputPreeditString(data: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, text: [*c]const u8, cursor_begin: i32, cursor_end: i32) callconv(.c) void {
        const self = fromData(data);
        const s: []const u8 = if (text != null) std.mem.span(text) else "";
        self.ime_pending_preedit.clearRetainingCapacity();
        self.ime_pending_preedit.appendSlice(self.gpa, s) catch log.err("dropping preedit: out of memory", .{});
        // -1/-1 hides the cursor; other out-of-range values are treated alike.
        const len = self.ime_pending_preedit.items.len;
        self.ime_pending_cursor = null;
        if (cursor_begin >= 0 and cursor_end >= 0) {
            const b: usize = @intCast(cursor_begin);
            const e: usize = @intCast(cursor_end);
            if (b <= len and e <= len) self.ime_pending_cursor = .{ b, e };
        }
    }
    fn textInputCommitString(data: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, text: [*c]const u8) callconv(.c) void {
        const self = fromData(data);
        const s: []const u8 = if (text != null) std.mem.span(text) else "";
        self.ime_pending_commit.clearRetainingCapacity();
        self.ime_pending_commit.appendSlice(self.gpa, s) catch log.err("dropping input method text: out of memory", .{});
    }
    fn textInputDeleteSurroundingText(_: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, before: u32, after: u32) callconv(.c) void {
        // We never send surrounding text, so input methods shouldn't ask.
        if (before != 0 or after != 0) log.debug("ignoring delete_surrounding_text({d}, {d})", .{ before, after });
    }
    fn textInputDone(data: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, serial: u32) callconv(.c) void {
        const self = fromData(data);
        const arena = self.event_arena.allocator();
        const valid = std.unicode.utf8ValidateSlice;

        // A preedit arriving after disable/leave would never be cleared.
        const show = self.ime_focused and self.ime_enabled and valid(self.ime_pending_preedit.items);
        const preedit: []const u8 = if (show) self.ime_pending_preedit.items else "";
        const cursor = if (show) self.ime_pending_cursor else null;
        const commit = self.ime_pending_commit.items;
        const preedit_changed = !std.mem.eql(u8, preedit, self.ime_preedit.items) or !std.meta.eql(cursor, self.ime_preedit_cursor);

        // In protocol order: drop the old preedit, insert the commit, show
        // the new preedit (`.text_input` clears the app's preedit itself).
        if (commit.len > 0 or preedit_changed) {
            // Output from the IME means a newer key press went to it, which
            // ends key repeat just like a new wl_keyboard press would.
            self.repeat_key = null;
            if (commit.len > 0) {
                if (!valid(commit)) {
                    log.warn("ignoring invalid UTF-8 from the input method", .{});
                } else if (arena.dupe(u8, commit)) |t| {
                    self.push(.{ .text_input = t });
                } else |_| log.err("dropping input method text: out of memory", .{});
            }
            if (arena.dupe(u8, preedit)) |t| {
                self.push(.{ .preedit = .{
                    .text = t,
                    .cursor_begin = if (cursor) |cur| cur[0] else null,
                    .cursor_end = if (cursor) |cur| cur[1] else null,
                } });
            } else |_| self.push(.{ .preedit = .{ .text = "" } });
            self.ime_clear_preedit = false;
        }
        self.ime_preedit.clearRetainingCapacity();
        self.ime_preedit.appendSlice(self.gpa, preedit) catch {};
        self.ime_preedit_cursor = cursor;
        self.ime_pending_preedit.clearRetainingCapacity();
        self.ime_pending_cursor = null;
        self.ime_pending_commit.clearRetainingCapacity();

        // The serial is the number of our commits the compositor had seen.
        // If it matches, state changes prompted by this text go out as usual;
        // if not, a newer commit is still in flight, so hold them until a
        // `done` that matches (or input bypassing the IME moves the caret).
        if (serial == self.ime_commits) {
            self.ime_hold = false;
            self.syncTextInput();
        } else {
            self.ime_hold = true;
        }
    }
    fn textInputAction(_: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, _: u32, _: u32) callconv(.c) void {}
    fn textInputLanguage(_: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, _: [*c]const u8) callconv(.c) void {}
    fn textInputPreeditHint(_: ?*anyopaque, _: ?*c.struct_zwp_text_input_v3, _: u32, _: u32, _: u32) callconv(.c) void {}

    const pointer_listener: c.struct_wl_pointer_listener = .{
        .enter = pointerEnter,
        .leave = pointerLeave,
        .motion = pointerMotion,
        .button = pointerButton,
        .axis = pointerAxis,
        .frame = pointerFrame,
        .axis_source = pointerAxisSource,
        .axis_stop = pointerAxisStop,
        .axis_discrete = pointerAxisDiscrete,
        .axis_value120 = pointerAxisValue120,
        .axis_relative_direction = pointerAxisRelativeDirection,
    };
    fn fixedToF32(v: c.wl_fixed_t) f32 {
        return @as(f32, @floatFromInt(v)) / 256.0;
    }
    fn pointerEnter(data: ?*anyopaque, _: ?*c.struct_wl_pointer, serial: u32, _: ?*c.struct_wl_surface, x: c.wl_fixed_t, y: c.wl_fixed_t) callconv(.c) void {
        const self = fromData(data);
        self.pointer_serial = serial;
        self.pointer_pos = .{ .x = fixedToF32(x), .y = fixedToF32(y) };
        self.applyCursor();
        self.push(.{ .mouse_move = .{ .pos = self.pointer_pos, .mods = self.mods } });
    }
    fn pointerLeave(data: ?*anyopaque, _: ?*c.struct_wl_pointer, _: u32, _: ?*c.struct_wl_surface) callconv(.c) void {
        const self = fromData(data);
        self.pressed_button = null;
        self.push(.mouse_exit);
    }
    fn pointerMotion(data: ?*anyopaque, _: ?*c.struct_wl_pointer, _: u32, x: c.wl_fixed_t, y: c.wl_fixed_t) callconv(.c) void {
        const self = fromData(data);
        self.pointer_pos = .{ .x = fixedToF32(x), .y = fixedToF32(y) };
        self.push(.{ .mouse_move = .{ .pos = self.pointer_pos, .mods = self.mods, .pressed = self.pressed_button } });
    }
    fn pointerButton(data: ?*anyopaque, _: ?*c.struct_wl_pointer, serial: u32, _: u32, button: u32, state: u32) callconv(.c) void {
        const self = fromData(data);
        self.last_serial = serial;
        const b: platform.MouseButton = switch (button) {
            0x110 => .left,
            0x111 => .right,
            0x112 => .middle,
            0x113 => .back,
            0x114 => .forward,
            else => return,
        };
        self.handleButton(b, state == c.WL_POINTER_BUTTON_STATE_PRESSED);
    }
    fn pointerAxis(data: ?*anyopaque, _: ?*c.struct_wl_pointer, _: u32, axis: u32, value: c.wl_fixed_t) callconv(.c) void {
        const self = fromData(data);
        const v = fixedToF32(value);
        if (axis == c.WL_POINTER_AXIS_VERTICAL_SCROLL) self.scroll_accum.y += v else self.scroll_accum.x += v;
        self.scroll_pending = true;
    }
    fn pointerFrame(data: ?*anyopaque, _: ?*c.struct_wl_pointer) callconv(.c) void {
        const self = fromData(data);
        if (!self.scroll_pending) return;
        if (self.scroll_is_wheel and (self.scroll_value120.x != 0 or self.scroll_value120.y != 0)) {
            self.push(.{ .scroll = .{
                .pos = self.pointer_pos,
                .delta = .{ .x = self.scroll_value120.x / 120.0, .y = self.scroll_value120.y / 120.0 },
                .unit = .lines,
                .mods = self.mods,
            } });
        } else if (self.scroll_is_wheel) {
            // Compositor without value120: a notch is usually 15 units.
            self.push(.{ .scroll = .{
                .pos = self.pointer_pos,
                .delta = .{ .x = self.scroll_accum.x / 15.0, .y = self.scroll_accum.y / 15.0 },
                .unit = .lines,
                .mods = self.mods,
            } });
        } else {
            self.push(.{ .scroll = .{ .pos = self.pointer_pos, .delta = self.scroll_accum, .unit = .pixels, .mods = self.mods } });
        }
        self.scroll_accum = .{};
        self.scroll_value120 = .{};
        self.scroll_is_wheel = false;
        self.scroll_pending = false;
    }
    fn pointerAxisSource(data: ?*anyopaque, _: ?*c.struct_wl_pointer, source: u32) callconv(.c) void {
        const self = fromData(data);
        self.scroll_is_wheel = source == c.WL_POINTER_AXIS_SOURCE_WHEEL or source == c.WL_POINTER_AXIS_SOURCE_WHEEL_TILT;
    }
    fn pointerAxisStop(_: ?*anyopaque, _: ?*c.struct_wl_pointer, _: u32, _: u32) callconv(.c) void {}
    fn pointerAxisDiscrete(_: ?*anyopaque, _: ?*c.struct_wl_pointer, _: u32, _: i32) callconv(.c) void {}
    fn pointerAxisValue120(data: ?*anyopaque, _: ?*c.struct_wl_pointer, axis: u32, value: i32) callconv(.c) void {
        const self = fromData(data);
        const v: f32 = @floatFromInt(value);
        if (axis == c.WL_POINTER_AXIS_VERTICAL_SCROLL) self.scroll_value120.y += v else self.scroll_value120.x += v;
        self.scroll_is_wheel = true;
        self.scroll_pending = true;
    }
    fn pointerAxisRelativeDirection(_: ?*anyopaque, _: ?*c.struct_wl_pointer, _: u32, _: u32) callconv(.c) void {}

    const data_device_listener: c.struct_wl_data_device_listener = .{
        .data_offer = dataDeviceDataOffer,
        .enter = dataDeviceEnter,
        .leave = dataDeviceLeave,
        .motion = dataDeviceMotion,
        .drop = dataDeviceDrop,
        .selection = dataDeviceSelection,
    };
    fn dataDeviceDataOffer(data: ?*anyopaque, _: ?*c.struct_wl_data_device, offer: ?*c.struct_wl_data_offer) callconv(.c) void {
        const self = fromData(data);
        self.pending_offer = offer;
        self.pending_offer_mime = null;
        _ = c.wl_data_offer_add_listener(offer, &data_offer_listener, self);
    }
    fn dataDeviceEnter(_: ?*anyopaque, _: ?*c.struct_wl_data_device, _: u32, _: ?*c.struct_wl_surface, _: c.wl_fixed_t, _: c.wl_fixed_t, offer: ?*c.struct_wl_data_offer) callconv(.c) void {
        // Drag and drop isn't supported yet; drop the offer.
        if (offer) |o| c.wl_data_offer_destroy(o);
    }
    fn dataDeviceLeave(_: ?*anyopaque, _: ?*c.struct_wl_data_device) callconv(.c) void {}
    fn dataDeviceMotion(_: ?*anyopaque, _: ?*c.struct_wl_data_device, _: u32, _: c.wl_fixed_t, _: c.wl_fixed_t) callconv(.c) void {}
    fn dataDeviceDrop(_: ?*anyopaque, _: ?*c.struct_wl_data_device) callconv(.c) void {}
    fn dataDeviceSelection(data: ?*anyopaque, _: ?*c.struct_wl_data_device, offer: ?*c.struct_wl_data_offer) callconv(.c) void {
        const self = fromData(data);
        if (self.selection_offer) |old| {
            if (old != offer) c.wl_data_offer_destroy(old);
        }
        self.selection_offer = offer;
        self.selection_mime = if (offer != null and offer == self.pending_offer) self.pending_offer_mime else null;
    }

    const data_offer_listener: c.struct_wl_data_offer_listener = .{
        .offer = dataOfferOffer,
        .source_actions = dataOfferSourceActions,
        .action = dataOfferAction,
    };
    fn dataOfferOffer(data: ?*anyopaque, offer: ?*c.struct_wl_data_offer, mime_type: [*c]const u8) callconv(.c) void {
        const self = fromData(data);
        if (offer != self.pending_offer) return;
        const mime = std.mem.span(mime_type);
        // Keep the most preferred text mime type seen so far.
        const rank = struct {
            fn f(m: []const u8) usize {
                for (text_mimes, 0..) |t, i| if (std.mem.eql(u8, m, t)) return i;
                return text_mimes.len;
            }
        }.f;
        const new_rank = rank(mime);
        if (new_rank == text_mimes.len) return;
        if (self.pending_offer_mime == null or new_rank < rank(self.pending_offer_mime.?)) {
            self.pending_offer_mime = text_mimes[new_rank];
        }
    }
    fn dataOfferSourceActions(_: ?*anyopaque, _: ?*c.struct_wl_data_offer, _: u32) callconv(.c) void {}
    fn dataOfferAction(_: ?*anyopaque, _: ?*c.struct_wl_data_offer, _: u32) callconv(.c) void {}

    const data_source_listener: c.struct_wl_data_source_listener = .{
        .target = dataSourceTarget,
        .send = dataSourceSend,
        .cancelled = dataSourceCancelled,
        .dnd_drop_performed = dataSourceDndDropPerformed,
        .dnd_finished = dataSourceDndFinished,
        .action = dataSourceAction,
    };
    fn dataSourceTarget(_: ?*anyopaque, _: ?*c.struct_wl_data_source, _: [*c]const u8) callconv(.c) void {}
    fn dataSourceSend(data: ?*anyopaque, _: ?*c.struct_wl_data_source, _: [*c]const u8, fd: i32) callconv(.c) void {
        const self = fromData(data);
        defer _ = c.close(fd);
        const text = self.clipboard_text orelse return;
        var written: usize = 0;
        while (written < text.len) {
            const n = c.write(fd, text[written..].ptr, text.len - written);
            if (n <= 0) break;
            written += @intCast(n);
        }
    }
    fn dataSourceCancelled(data: ?*anyopaque, source: ?*c.struct_wl_data_source) callconv(.c) void {
        const self = fromData(data);
        c.wl_data_source_destroy(source);
        if (self.data_source == source) self.data_source = null;
    }
    fn dataSourceDndDropPerformed(_: ?*anyopaque, _: ?*c.struct_wl_data_source) callconv(.c) void {}
    fn dataSourceDndFinished(_: ?*anyopaque, _: ?*c.struct_wl_data_source) callconv(.c) void {}
    fn dataSourceAction(_: ?*anyopaque, _: ?*c.struct_wl_data_source, _: u32) callconv(.c) void {}
};
