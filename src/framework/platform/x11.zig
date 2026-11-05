//! Native X11 window through Xlib: ICCCM/EWMH properties, Xft.dpi scaling,
//! XIM keyboard input (dead keys, compose, ibus/fcitx) with detectable
//! auto-repeat, pointer input with click counting, Xcursor cursors and the
//! CLIPBOARD selection.
//!
//! Mirrors `wayland.zig`: events are translated into `platform.Event`s and
//! rendering happens through EGL on the same window.

const std = @import("std");
const c = @import("cx11");
const platform = @import("platform.zig");
const egl = @import("egl.zig");

const Allocator = std.mem.Allocator;
const Event = platform.Event;
const log = std.log.scoped(.x11);

pub const Options = struct {
    title: [:0]const u8 = "jiffy-edit",
    app_id: [:0]const u8 = "jiffy-edit",
    width: i32 = 1280,
    height: i32 = 800,
    icons: []const platform.IconImage = &.{},
};

/// Interned at startup; field names are the atom names.
const Atoms = struct {
    WM_PROTOCOLS: c.Atom = 0,
    WM_DELETE_WINDOW: c.Atom = 0,
    _NET_WM_PING: c.Atom = 0,
    _NET_WM_PID: c.Atom = 0,
    _NET_WM_NAME: c.Atom = 0,
    _NET_WM_ICON_NAME: c.Atom = 0,
    _NET_WM_ICON: c.Atom = 0,
    _NET_WM_STATE: c.Atom = 0,
    _NET_WM_STATE_FULLSCREEN: c.Atom = 0,
    _NET_WM_STATE_HIDDEN: c.Atom = 0,
    _NET_WM_WINDOW_TYPE: c.Atom = 0,
    _NET_WM_WINDOW_TYPE_NORMAL: c.Atom = 0,
    UTF8_STRING: c.Atom = 0,
    CLIPBOARD: c.Atom = 0,
    TARGETS: c.Atom = 0,
    TIMESTAMP: c.Atom = 0,
    TEXT: c.Atom = 0,
    INCR: c.Atom = 0,
    @"text/plain;charset=utf-8": c.Atom = 0,
    /// Property our clipboard requests are delivered into.
    JIFFY_EDIT_SELECTION: c.Atom = 0,
};

const event_mask: c_long = c.KeyPressMask | c.KeyReleaseMask | c.ButtonPressMask | c.ButtonReleaseMask |
    c.PointerMotionMask | c.EnterWindowMask | c.LeaveWindowMask | c.ExposureMask |
    c.StructureNotifyMask | c.FocusChangeMask | c.PropertyChangeMask;

const ime_style: c.XIMStyle = c.XIMPreeditNothing | c.XIMStatusNothing;

const POLLIN = 0x1;
const POLLERR = 0x8;
const POLLHUP = 0x10;

pub const Window = struct {
    gpa: Allocator,

    display: *c.Display,
    screen: c_int,
    root: c.Window,
    window: c.Window = 0,
    colormap: c.Colormap = 0,
    atoms: Atoms = .{},
    egl: egl.Context = undefined,
    has_egl: bool = false,
    /// Set by the IO error exit handler: the connection is gone.
    io_error: bool = false,

    // Input method.
    im: c.XIM = null,
    ic: c.XIC = null,
    /// The IM server went away; watch for it to come back.
    im_lost: bool = false,
    im_watch: bool = false,
    ime_enabled: bool = true,
    ime_spot: ?c.XPoint = null,
    ime_spot_supported: bool = true,

    // Keyboard.
    detectable_repeat: bool = false,
    keys_down: [256]bool = @splat(false),
    alt_mask: c_uint = c.Mod1Mask,
    super_mask: c_uint = c.Mod4Mask,
    mods: platform.Modifiers = .{},
    /// Server time of the latest input event (ICCCM wants real timestamps).
    last_time: c.Time = 0,

    // Geometry. X works in device pixels; logical = device / scale.
    device_width: i32,
    device_height: i32,
    dpi_scale: f32 = 1,
    size_dirty: bool = true,
    expose_pending: bool = false,
    mapped: bool = false,
    hidden: bool = false,
    fullscreen: bool = false,
    focused: bool = false,

    // Pointer.
    pointer_pos: platform.Vec2 = .{},
    pressed_button: ?platform.MouseButton = null,
    pointer_outside: bool = false,
    cursor_shape: platform.CursorShape = .default,
    cursors: [@typeInfo(platform.CursorShape).@"enum".field_names.len]c.Cursor = @splat(0),
    last_click_time: i64 = 0,
    last_click_pos: platform.Vec2 = .{},
    last_click_button: platform.MouseButton = .left,
    click_count: u8 = 0,

    // Clipboard (we own CLIPBOARD while this is set).
    clipboard_text: ?[]u8 = null,
    selection_time: c.Time = 0,

    events: std.ArrayList(Event) = .empty,
    event_arena: std.heap.ArenaAllocator,

    pub fn create(gpa: Allocator, opts: Options) !*Window {
        // Mesa may talk to the same connection from its own threads.
        _ = c.XInitThreads();
        const display = c.XOpenDisplay(null) orelse return error.NoX11Display;
        const self = gpa.create(Window) catch |err| {
            _ = c.XCloseDisplay(display);
            return err;
        };
        const screen = c.XDefaultScreen(display);
        self.* = .{
            .gpa = gpa,
            .display = display,
            .screen = screen,
            .root = c.XRootWindow(display, screen),
            .device_width = opts.width,
            .device_height = opts.height,
            .event_arena = .init(gpa),
        };
        errdefer self.destroy();

        // Xlib's default handlers exit the process; protocol errors (e.g. a
        // clipboard requestor that vanished) are harmless and a lost
        // connection is reported from pollEvents instead.
        _ = c.XSetErrorHandler(onXError);
        _ = c.XSetIOErrorHandler(onIoError);
        c.XSetIOErrorExitHandler(display, onIoErrorExit, self);
        c.XrmInitialize();
        self.atoms = internAtoms(display);
        self.dpi_scale = self.readScale();
        self.device_width = self.toDevice(opts.width);
        self.device_height = self.toDevice(opts.height);

        self.egl = try egl.Context.initX11(display, screen);
        self.has_egl = true;

        // The window's visual must match the EGL config.
        var template: c.XVisualInfo = .{ .visualid = self.egl.nativeVisualId() };
        var count: c_int = 0;
        const info = c.XGetVisualInfo(display, c.VisualIDMask, &template, &count);
        if (info == null or count == 0) return error.NoMatchingVisual;
        defer _ = c.XFree(info);
        self.colormap = c.XCreateColormap(display, self.root, info.*.visual, c.AllocNone);
        var attrs: c.XSetWindowAttributes = .{
            // No background: the server doesn't clear exposed areas before we redraw.
            .background_pixmap = c.None,
            .border_pixel = 0,
            .colormap = self.colormap,
            .event_mask = event_mask,
        };
        self.window = c.XCreateWindow(
            display,
            self.root,
            0,
            0,
            @intCast(@max(1, self.device_width)),
            @intCast(@max(1, self.device_height)),
            0,
            info.*.depth,
            c.InputOutput,
            info.*.visual,
            c.CWBackPixmap | c.CWBorderPixel | c.CWColormap | c.CWEventMask,
            &attrs,
        );
        if (self.window == 0) return error.CreateWindowFailed;
        self.setProperties(opts);
        // RESOURCE_MANAGER on the root window carries Xft.dpi.
        _ = c.XSelectInput(display, self.root, c.PropertyChangeMask);

        var supported: c_int = 0;
        _ = c.XkbSetDetectableAutoRepeat(display, 1, &supported);
        self.detectable_repeat = supported != 0;
        self.updateModifierMasks();
        self.initInputMethod();
        self.applyCursor();
        _ = c.XMapWindow(display, self.window);
        _ = c.XFlush(display);

        try self.egl.createX11WindowSurface(&self.window);
        try self.egl.makeCurrent();
        try self.egl.loadGl();
        self.egl.setSwapInterval(0);
        return self;
    }

    pub fn destroy(self: *Window) void {
        const gpa = self.gpa;
        const d = self.display;
        if (self.has_egl) self.egl.deinit();
        // After an IO error requests can't be sent anyway (Xlib only complains).
        if (!self.io_error) {
            if (self.ic) |ic| c.XDestroyIC(ic);
            if (self.im) |im| _ = c.XCloseIM(im);
            if (self.im_watch) _ = c.XUnregisterIMInstantiateCallback(d, null, null, null, imInstantiated, @ptrCast(self));
            for (self.cursors) |cursor| if (cursor != 0) {
                _ = c.XFreeCursor(d, cursor);
            };
            if (self.window != 0) _ = c.XDestroyWindow(d, self.window);
            if (self.colormap != 0) _ = c.XFreeColormap(d, self.colormap);
        }
        _ = c.XCloseDisplay(d);
        if (self.clipboard_text) |t| gpa.free(t);
        self.events.deinit(gpa);
        self.event_arena.deinit();
        gpa.destroy(self);
    }

    // ---- Public API -------------------------------------------------------

    pub fn scale(self: *const Window) f32 {
        return self.dpi_scale;
    }

    pub fn bufferSize(self: *const Window) struct { i32, i32 } {
        return .{ self.device_width, self.device_height };
    }

    pub fn logicalSize(self: *const Window) platform.Vec2 {
        return .{
            .x = @as(f32, @floatFromInt(self.device_width)) / self.dpi_scale,
            .y = @as(f32, @floatFromInt(self.device_height)) / self.dpi_scale,
        };
    }

    /// Blocks until X events arrive, one of `extra` becomes ready, or
    /// `timeout_ms` elapses (-1 = forever). Window events land in
    /// `self.events` (valid until the next call); `extra[i].revents` is set.
    pub fn pollEvents(self: *Window, timeout_ms: i32, extra: []platform.PollFd) !void {
        _ = self.event_arena.reset(.retain_capacity);
        self.events.clearRetainingCapacity();
        if (self.io_error) return error.X11Disconnected;

        // Xlib may already hold events it read while waiting for a reply (the
        // socket can be empty then), so drain its queue before sleeping.
        self.drainEvents();

        var timeout = timeout_ms;
        if (self.events.items.len > 0 or self.size_dirty or self.expose_pending) timeout = 0;

        var fds: [33]platform.PollFd = undefined;
        fds[0] = .{ .fd = c.XConnectionNumber(self.display) };
        const n_extra = @min(extra.len, fds.len - 1);
        for (extra[0..n_extra], fds[1..][0..n_extra]) |e, *f| f.* = .{ .fd = e.fd, .events = e.events };
        _ = c.XFlush(self.display);
        const n = std.c.poll(@ptrCast(&fds), @intCast(1 + n_extra), timeout);
        if (n > 0 and fds[0].revents != 0) {
            if (fds[0].revents & POLLIN == 0 and fds[0].revents & (POLLERR | POLLHUP) != 0) return error.X11Disconnected;
            self.drainEvents();
        }
        for (extra[0..n_extra], fds[1..][0..n_extra]) |*e, f| e.revents = if (n > 0) f.revents else 0;
        if (self.io_error) return error.X11Disconnected;
        if (self.im_lost) {
            self.im_lost = false;
            self.watchInputMethod();
        }

        // Coalesce configure storms into one resize; a bare expose still needs
        // an event so the app repaints.
        if (self.size_dirty or (self.expose_pending and self.events.items.len == 0)) {
            const size = self.logicalSize();
            self.push(.{ .resize = .{ .width = size.x, .height = size.y, .scale = self.dpi_scale } });
        }
        self.size_dirty = false;
        self.expose_pending = false;
    }

    /// False while the window is unmapped or minimized.
    pub fn canRender(self: *const Window) bool {
        return self.mapped and !self.hidden;
    }

    pub fn present(self: *Window) void {
        self.egl.swapBuffers();
    }

    /// Tells the input method where the caret is (XNSpotLocation, device px)
    /// so servers like ibus/fcitx put their candidate window there.
    pub fn setTextInputRect(self: *Window, rect: platform.Rect, enabled: bool) void {
        const ic = self.ic orelse return;
        if (enabled != self.ime_enabled) {
            self.ime_enabled = enabled;
            if (self.focused) {
                if (enabled) c.XSetICFocus(ic) else c.XUnsetICFocus(ic);
            }
        }
        if (!enabled or !self.ime_spot_supported) return;
        const s = self.dpi_scale;
        var spot: c.XPoint = .{
            .x = @intFromFloat(std.math.clamp(rect.x * s, 0, 32767)),
            .y = @intFromFloat(std.math.clamp((rect.y + rect.h) * s, 0, 32767)),
        };
        if (self.ime_spot) |old| if (old.x == spot.x and old.y == spot.y) return;
        self.ime_spot = spot;
        const attrs = c.XVaCreateNestedList(0, @as([*:0]const u8, c.XNSpotLocation), &spot, @as(?*anyopaque, null)) orelse return;
        defer _ = c.XFree(attrs);
        if (c.XSetICValues(ic, @as([*:0]const u8, c.XNPreeditAttributes), attrs, @as(?*anyopaque, null)) != null) {
            self.ime_spot_supported = false;
        }
    }

    pub fn setTitle(self: *Window, title: [:0]const u8) void {
        // WM_NAME / WM_ICON_NAME for old window managers, _NET_WM_NAME (UTF-8) for the rest.
        c.Xutf8SetWMProperties(self.display, self.window, title.ptr, title.ptr, null, 0, null, null, null);
        for ([_]c.Atom{ self.atoms._NET_WM_NAME, self.atoms._NET_WM_ICON_NAME }) |prop| {
            _ = c.XChangeProperty(self.display, self.window, prop, self.atoms.UTF8_STRING, 8, c.PropModeReplace, title.ptr, @intCast(title.len));
        }
        _ = c.XFlush(self.display);
    }

    pub fn setCursor(self: *Window, shape: platform.CursorShape) void {
        if (shape == self.cursor_shape) return;
        self.cursor_shape = shape;
        self.applyCursor();
    }

    /// Asks the window manager (EWMH) to toggle fullscreen.
    pub fn toggleFullscreen(self: *Window) void {
        const net_wm_state_toggle = 2;
        var ev = std.mem.zeroes(c.XEvent);
        ev.xclient = .{
            .type = c.ClientMessage,
            .window = self.window,
            .message_type = self.atoms._NET_WM_STATE,
            .format = 32,
        };
        ev.xclient.data.l = .{ net_wm_state_toggle, @bitCast(self.atoms._NET_WM_STATE_FULLSCREEN), 0, 1, 0 };
        _ = c.XSendEvent(self.display, self.root, 0, c.SubstructureNotifyMask | c.SubstructureRedirectMask, &ev);
        _ = c.XFlush(self.display);
    }

    pub fn setClipboard(self: *Window, text: []const u8) !void {
        const owned = try self.gpa.dupe(u8, text);
        if (self.clipboard_text) |old| self.gpa.free(old);
        self.clipboard_text = owned;
        self.selection_time = self.last_time;
        _ = c.XSetSelectionOwner(self.display, self.atoms.CLIPBOARD, self.window, self.last_time);
        if (c.XGetSelectionOwner(self.display, self.atoms.CLIPBOARD) != self.window) {
            log.warn("could not take ownership of the clipboard", .{});
        }
    }

    /// Returns the clipboard contents (caller frees), or null if empty / not text.
    /// Waits up to ~1 s for the owner; other events stay queued for the next
    /// `pollEvents` (the caller may be iterating `events`).
    /// The text we put on the clipboard, while we still own the selection
    /// (it goes when this window does).
    pub fn ownedClipboard(self: *Window) ?[]const u8 {
        return self.clipboard_text;
    }

    pub fn getClipboard(self: *Window, gpa: Allocator) !?[]u8 {
        const a = self.atoms;
        const owner = c.XGetSelectionOwner(self.display, a.CLIPBOARD);
        if (owner == self.window) {
            if (self.clipboard_text) |t| return try gpa.dupe(u8, t);
            return null;
        }
        if (owner == c.None) return null;

        for ([_]c.Atom{ a.UTF8_STRING, c.XA_STRING }) |target| {
            _ = c.XDeleteProperty(self.display, self.window, a.JIFFY_EDIT_SELECTION);
            _ = c.XConvertSelection(self.display, a.CLIPBOARD, target, a.JIFFY_EDIT_SELECTION, self.window, self.last_time);
            var ev: c.XEvent = undefined;
            if (!self.waitForEvent(&ev, .{ .type = c.SelectionNotify, .window = self.window, .atom = a.CLIPBOARD, .target = target }, 1000)) {
                log.warn("clipboard owner did not respond", .{});
                return null;
            }
            // The owner can't convert to this target; try the next one.
            if (ev.xselection.property == c.None) continue;
            const data = try self.readSelection(gpa) orelse continue;
            if (target == c.XA_STRING) {
                defer gpa.free(data);
                return try latin1ToUtf8(gpa, data);
            }
            return data;
        }
        return null;
    }

    // ---- Internals --------------------------------------------------------

    fn push(self: *Window, event: Event) void {
        self.events.append(self.gpa, event) catch log.err("dropping event: out of memory", .{});
    }

    fn toDevice(self: *const Window, logical: i32) i32 {
        return @intFromFloat(@round(@as(f32, @floatFromInt(logical)) * self.dpi_scale));
    }

    fn toLogical(self: *const Window, x: c_int, y: c_int) platform.Vec2 {
        return .{
            .x = @as(f32, @floatFromInt(x)) / self.dpi_scale,
            .y = @as(f32, @floatFromInt(y)) / self.dpi_scale,
        };
    }

    fn internAtoms(display: *c.Display) Atoms {
        const field_names = @typeInfo(Atoms).@"struct".field_names;
        var names: [field_names.len][*c]u8 = undefined;
        inline for (field_names, 0..) |name, i| names[i] = @constCast(name.ptr);
        var values: [field_names.len]c.Atom = @splat(0);
        _ = c.XInternAtoms(display, &names, field_names.len, 0, &values);
        var atoms: Atoms = .{};
        inline for (field_names, 0..) |name, i| @field(atoms, name) = values[i];
        return atoms;
    }

    fn setProperties(self: *Window, opts: Options) void {
        const d = self.display;
        const a = self.atoms;
        var protocols = [_]c.Atom{ a.WM_DELETE_WINDOW, a._NET_WM_PING };
        _ = c.XSetWMProtocols(d, self.window, &protocols, protocols.len);

        // WM_NORMAL_HINTS, WM_HINTS, WM_CLASS, WM_CLIENT_MACHINE (names below).
        var size_hints: c.XSizeHints = .{ .flags = c.PMinSize, .min_width = self.toDevice(320), .min_height = self.toDevice(200) };
        var wm_hints: c.XWMHints = .{ .flags = c.InputHint | c.StateHint, .input = 1, .initial_state = c.NormalState };
        var class_hint: c.XClassHint = .{ .res_name = @constCast(opts.app_id.ptr), .res_class = @constCast(opts.app_id.ptr) };
        c.Xutf8SetWMProperties(d, self.window, null, null, null, 0, &size_hints, &wm_hints, &class_hint);
        self.setTitle(opts.title);

        // Format-32 properties are arrays of C longs.
        const pid: c_long = std.c.getpid();
        _ = c.XChangeProperty(d, self.window, a._NET_WM_PID, c.XA_CARDINAL, 32, c.PropModeReplace, @ptrCast(&pid), 1);
        const window_type: c.Atom = a._NET_WM_WINDOW_TYPE_NORMAL;
        _ = c.XChangeProperty(d, self.window, a._NET_WM_WINDOW_TYPE, c.XA_ATOM, 32, c.PropModeReplace, @ptrCast(&window_type), 1);
        self.setIcon(opts.icons);
    }

    /// _NET_WM_ICON: per size its width, height and ARGB pixels (straight
    /// alpha), one C long each. Written a size at a time, so only the
    /// largest needs a buffer.
    fn setIcon(self: *Window, icons: []const platform.IconImage) void {
        var most: usize = 0;
        for (icons) |icon| most = @max(most, icon.size * icon.size);
        if (most == 0) return;
        const data = self.gpa.alloc(c_ulong, 2 + most) catch return;
        defer self.gpa.free(data);
        for (icons, 0..) |icon, i| {
            const pixels = data[2..][0 .. icon.size * icon.size];
            data[0] = icon.size;
            data[1] = icon.size;
            for (pixels, 0..) |*px, j| {
                const rgba = icon.rgba[j * 4 ..][0..4];
                px.* = @as(c_ulong, rgba[3]) << 24 | @as(c_ulong, rgba[0]) << 16 | @as(c_ulong, rgba[1]) << 8 | rgba[2];
            }
            const mode = if (i == 0) c.PropModeReplace else c.PropModeAppend;
            _ = c.XChangeProperty(self.display, self.window, self.atoms._NET_WM_ICON, c.XA_CARDINAL, 32, mode, @ptrCast(data.ptr), @intCast(2 + pixels.len));
        }
    }

    /// Xft.dpi from the RESOURCE_MANAGER property (what desktop environments
    /// set for HiDPI), relative to 96 dpi.
    fn readScale(self: *Window) f32 {
        var kind: c.Atom = 0;
        var format: c_int = 0;
        var count: c_ulong = 0;
        var after: c_ulong = 0;
        var data: [*c]u8 = null;
        if (c.XGetWindowProperty(self.display, self.root, c.XA_RESOURCE_MANAGER, 0, 0x1fffffff, 0, c.XA_STRING, &kind, &format, &count, &after, &data) != c.Success) return 1;
        if (data == null) return 1;
        defer _ = c.XFree(data);
        const db = c.XrmGetStringDatabase(data) orelse return 1;
        defer c.XrmDestroyDatabase(db);
        var value_type: [*c]u8 = null;
        var value: c.XrmValue = .{};
        if (c.XrmGetResource(db, "Xft.dpi", "Xft.Dpi", &value_type, &value) == 0 or value.addr == null) return 1;
        const text = std.mem.trim(u8, std.mem.span(value.addr), " \t\r\n");
        const dpi = std.fmt.parseFloat(f32, text) catch return 1;
        if (!(dpi > 0)) return 1;
        return std.math.clamp(dpi / 96.0, 0.5, 4.0);
    }

    fn readWmState(self: *Window) void {
        var kind: c.Atom = 0;
        var format: c_int = 0;
        var count: c_ulong = 0;
        var after: c_ulong = 0;
        var data: [*c]u8 = null;
        if (c.XGetWindowProperty(self.display, self.window, self.atoms._NET_WM_STATE, 0, 1024, 0, c.XA_ATOM, &kind, &format, &count, &after, &data) != c.Success) return;
        defer if (data != null) {
            _ = c.XFree(data);
        };
        self.fullscreen = false;
        self.hidden = false;
        if (data == null or format != 32) return;
        const states: [*]const c.Atom = @ptrCast(@alignCast(data));
        for (states[0..count]) |state| {
            if (state == self.atoms._NET_WM_STATE_FULLSCREEN) self.fullscreen = true;
            if (state == self.atoms._NET_WM_STATE_HIDDEN) self.hidden = true;
        }
    }

    fn applyCursor(self: *Window) void {
        const index = @backingInt(self.cursor_shape);
        if (self.cursors[index] == 0) self.cursors[index] = self.loadCursor(self.cursor_shape);
        _ = c.XDefineCursor(self.display, self.window, self.cursors[index]);
        _ = c.XFlush(self.display);
    }

    /// Theme cursors (CSS names first, then the legacy X names), falling back
    /// to the core cursor font.
    fn loadCursor(self: *Window, shape: platform.CursorShape) c.Cursor {
        const names: []const [:0]const u8, const font_shape: c_uint = switch (shape) {
            .default => .{ &.{ "default", "left_ptr" }, 68 }, // XC_left_ptr
            .text => .{ &.{ "text", "xterm" }, 152 }, // XC_xterm
            .pointer => .{ &.{ "pointer", "hand2", "hand1" }, 60 }, // XC_hand2
            .ew_resize => .{ &.{ "ew-resize", "sb_h_double_arrow", "col-resize" }, 108 }, // XC_sb_h_double_arrow
            .ns_resize => .{ &.{ "ns-resize", "sb_v_double_arrow", "row-resize" }, 116 }, // XC_sb_v_double_arrow
            .grab => .{ &.{ "grab", "openhand", "hand1" }, 58 }, // XC_hand1
            .grabbing => .{ &.{ "grabbing", "closedhand", "fleur" }, 52 }, // XC_fleur
        };
        for (names) |name| {
            const cursor = c.XcursorLibraryLoadCursor(self.display, name.ptr);
            if (cursor != 0) return cursor;
        }
        return c.XCreateFontCursor(self.display, font_shape);
    }

    /// Alt and Super live on whichever ModN the server maps them to.
    fn updateModifierMasks(self: *Window) void {
        self.alt_mask = c.Mod1Mask;
        self.super_mask = c.Mod4Mask;
        const map = c.XGetModifierMapping(self.display);
        if (map == null) return;
        defer _ = c.XFreeModifiermap(map);
        const per: usize = @intCast(map.*.max_keypermod);
        var found_alt = false;
        var found_super = false;
        // Indices 3..7 are Mod1..Mod5.
        for (3..8) |mod_index| {
            const mask = @as(c_uint, 1) << @intCast(mod_index);
            for (map.*.modifiermap[mod_index * per ..][0..per]) |keycode| {
                if (keycode == 0) continue;
                switch (c.XkbKeycodeToKeysym(self.display, keycode, 0, 0)) {
                    c.XKB_KEY_Alt_L, c.XKB_KEY_Alt_R, c.XKB_KEY_Meta_L, c.XKB_KEY_Meta_R => if (!found_alt) {
                        self.alt_mask = mask;
                        found_alt = true;
                    },
                    c.XKB_KEY_Super_L, c.XKB_KEY_Super_R => if (!found_super) {
                        self.super_mask = mask;
                        found_super = true;
                    },
                    else => {},
                }
            }
        }
    }

    fn modsFromState(self: *const Window, state: c_uint) platform.Modifiers {
        return .{
            .shift = state & c.ShiftMask != 0,
            .ctrl = state & c.ControlMask != 0,
            .alt = state & self.alt_mask != 0,
            .super = state & self.super_mask != 0,
        };
    }

    // ---- Input method -------------------------------------------------------

    fn initInputMethod(self: *Window) void {
        // XIM follows the C locale, and an untouched "C" locale breaks
        // non-ASCII input; adopt the environment's (as GLFW does).
        const current = c.setlocale(c.LC_CTYPE, null);
        if (current == null or std.mem.eql(u8, std.mem.span(current), "C")) _ = c.setlocale(c.LC_CTYPE, "");
        if (c.XSupportsLocale() == 0) {
            log.warn("locale not supported by Xlib; falling back to C", .{});
            _ = c.setlocale(c.LC_CTYPE, "C");
        }
        // Picks up XMODIFIERS (e.g. @im=ibus).
        _ = c.XSetLocaleModifiers("");
        self.openInputMethod();
        if (self.im == null) {
            // The configured server isn't running: Xlib's built-in method
            // still handles dead keys and compose sequences.
            log.debug("input method server unavailable; using local compose", .{});
            _ = c.XSetLocaleModifiers("@im=none");
            self.openInputMethod();
        }
        if (self.im == null) log.warn("no X input method; dead keys and compose won't work", .{});
    }

    fn openInputMethod(self: *Window) void {
        const im = c.XOpenIM(self.display, null, null, null) orelse return;
        var styles: [*c]c.XIMStyles = null;
        if (c.XGetIMValues(im, @as([*:0]const u8, c.XNQueryInputStyle), &styles, @as(?*anyopaque, null)) != null or styles == null) {
            _ = c.XCloseIM(im);
            return;
        }
        const supported = for (styles.*.supported_styles[0..styles.*.count_styles]) |s| {
            if (s == ime_style) break true;
        } else false;
        _ = c.XFree(styles);
        if (!supported) {
            _ = c.XCloseIM(im);
            return;
        }
        // Xlib copies the callback record.
        var destroy_cb: c.XIMCallback = .{ .client_data = @ptrCast(self), .callback = imDestroyed };
        _ = c.XSetIMValues(im, @as([*:0]const u8, c.XNDestroyCallback), &destroy_cb, @as(?*anyopaque, null));
        const ic = c.XCreateIC(
            im,
            @as([*:0]const u8, c.XNInputStyle),
            ime_style,
            @as([*:0]const u8, c.XNClientWindow),
            self.window,
            @as([*:0]const u8, c.XNFocusWindow),
            self.window,
            @as(?*anyopaque, null),
        ) orelse {
            _ = c.XCloseIM(im);
            return;
        };
        self.im = im;
        self.ic = ic;
        self.ime_spot = null;
        self.ime_spot_supported = true;
        // Some input methods need extra events (e.g. key releases) to filter.
        var filter: c_long = 0;
        if (c.XGetICValues(ic, @as([*:0]const u8, c.XNFilterEvents), &filter, @as(?*anyopaque, null)) == null) {
            _ = c.XSelectInput(self.display, self.window, event_mask | filter);
        }
        if (self.focused and self.ime_enabled) c.XSetICFocus(ic);
    }

    /// The input method server went away; Xlib frees the IM and its ICs.
    /// Runs inside event filtering, so reconnecting is left to pollEvents.
    fn imDestroyed(_: c.XIM, client_data: c.XPointer, _: c.XPointer) callconv(.c) void {
        const self: *Window = @ptrCast(@alignCast(client_data));
        self.im = null;
        self.ic = null;
        self.im_lost = true;
    }

    /// Reopens the input method once its server is back (it may answer right away).
    fn watchInputMethod(self: *Window) void {
        if (self.im_watch or self.im != null) return;
        self.im_watch = true;
        if (c.XRegisterIMInstantiateCallback(self.display, null, null, null, imInstantiated, @ptrCast(self)) == 0) self.im_watch = false;
    }

    fn imInstantiated(display: ?*c.Display, client_data: c.XPointer, _: c.XPointer) callconv(.c) void {
        const self: *Window = @ptrCast(@alignCast(client_data));
        if (self.im == null) self.openInputMethod();
        if (self.im != null and self.im_watch) {
            _ = c.XUnregisterIMInstantiateCallback(display, null, null, null, imInstantiated, client_data);
            self.im_watch = false;
        }
    }

    // ---- Events -------------------------------------------------------------

    fn drainEvents(self: *Window) void {
        while (!self.io_error and c.XPending(self.display) > 0) {
            var ev: c.XEvent = undefined;
            _ = c.XNextEvent(self.display, &ev);
            self.handleEvent(&ev);
        }
    }

    fn handleEvent(self: *Window, ev: *c.XEvent) void {
        // Input methods see every event first: they swallow dead keys and
        // composing keystrokes, and use client messages for their protocol.
        if (c.XFilterEvent(ev, c.None) != 0) return;
        const a = self.atoms;
        switch (ev.type) {
            c.KeyPress => self.handleKeyPress(&ev.xkey),
            c.KeyRelease => self.handleKeyRelease(&ev.xkey),
            c.ButtonPress, c.ButtonRelease => {
                const b = &ev.xbutton;
                const pressed = ev.type == c.ButtonPress;
                self.last_time = b.time;
                self.mods = self.modsFromState(b.state);
                self.pointer_pos = self.toLogical(b.x, b.y);
                const button: platform.MouseButton = switch (b.button) {
                    1 => .left,
                    2 => .middle,
                    3 => .right,
                    8 => .back,
                    9 => .forward,
                    // Wheel notches arrive as buttons 4-7 (up, down, left, right).
                    4...7 => {
                        if (!pressed) return;
                        const delta: platform.Vec2 = switch (b.button) {
                            4 => .{ .y = -1 },
                            5 => .{ .y = 1 },
                            6 => .{ .x = -1 },
                            else => .{ .x = 1 },
                        };
                        self.push(.{ .scroll = .{ .pos = self.pointer_pos, .delta = delta, .unit = .lines, .mods = self.mods } });
                        return;
                    },
                    else => return,
                };
                self.handleButton(button, pressed);
            },
            c.MotionNotify => {
                const m = &ev.xmotion;
                self.last_time = m.time;
                self.mods = self.modsFromState(m.state);
                self.pointer_pos = self.toLogical(m.x, m.y);
                self.push(.{ .mouse_move = .{ .pos = self.pointer_pos, .mods = self.mods, .pressed = self.pressed_button } });
            },
            c.EnterNotify => {
                const e = &ev.xcrossing;
                self.last_time = e.time;
                self.pointer_outside = false;
                self.pointer_pos = self.toLogical(e.x, e.y);
                self.push(.{ .mouse_move = .{ .pos = self.pointer_pos, .mods = self.modsFromState(e.state), .pressed = self.pressed_button } });
            },
            c.LeaveNotify => {
                self.last_time = ev.xcrossing.time;
                // While a button is held X keeps reporting motion (implicit
                // grab), like Wayland; report the exit once it's released.
                if (self.pressed_button != null) {
                    self.pointer_outside = true;
                } else {
                    self.push(.mouse_exit);
                }
            },
            c.FocusIn, c.FocusOut => {
                const f = &ev.xfocus;
                // Grab-related focus changes come from WM key chords and window
                // dragging; the window keeps the keyboard.
                if (f.mode == c.NotifyGrab or f.mode == c.NotifyUngrab or f.detail == c.NotifyPointer) return;
                const focused = ev.type == c.FocusIn;
                if (focused == self.focused) return;
                self.focused = focused;
                if (self.ic) |ic| {
                    if (focused and self.ime_enabled) c.XSetICFocus(ic) else c.XUnsetICFocus(ic);
                }
                if (!focused) {
                    self.keys_down = @splat(false);
                    self.mods = .{};
                }
                self.push(.{ .focus = focused });
            },
            c.Expose => if (ev.xexpose.count == 0) {
                self.expose_pending = true;
            },
            c.ConfigureNotify => {
                const cfg = &ev.xconfigure;
                if (cfg.window != self.window) return;
                if (cfg.width != self.device_width or cfg.height != self.device_height) {
                    self.device_width = cfg.width;
                    self.device_height = cfg.height;
                    self.size_dirty = true;
                }
            },
            c.MapNotify => if (ev.xmap.window == self.window) {
                self.mapped = true;
            },
            c.UnmapNotify => if (ev.xunmap.window == self.window) {
                self.mapped = false;
            },
            c.PropertyNotify => {
                const p = &ev.xproperty;
                if (p.window == self.root and p.atom == c.XA_RESOURCE_MANAGER) {
                    const s = self.readScale();
                    if (s != self.dpi_scale) {
                        self.dpi_scale = s;
                        self.size_dirty = true;
                    }
                } else if (p.window == self.window) {
                    self.last_time = p.time;
                    if (p.atom == a._NET_WM_STATE) self.readWmState();
                }
            },
            c.ClientMessage => {
                const msg = &ev.xclient;
                if (msg.message_type != a.WM_PROTOCOLS) return;
                const protocol: c.Atom = @bitCast(msg.data.l[0]);
                if (protocol == a.WM_DELETE_WINDOW) {
                    self.push(.close_requested);
                } else if (protocol == a._NET_WM_PING) {
                    var reply = ev.*;
                    reply.xclient.window = self.root;
                    _ = c.XSendEvent(self.display, self.root, 0, c.SubstructureNotifyMask | c.SubstructureRedirectMask, &reply);
                }
            },
            c.SelectionRequest => self.handleSelectionRequest(&ev.xselectionrequest),
            c.SelectionClear => if (ev.xselectionclear.selection == a.CLIPBOARD) {
                if (self.clipboard_text) |t| self.gpa.free(t);
                self.clipboard_text = null;
            },
            c.MappingNotify => {
                _ = c.XRefreshKeyboardMapping(&ev.xmapping);
                self.updateModifierMasks();
            },
            else => {},
        }
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
            if (self.pointer_outside and self.pressed_button == null) {
                self.pointer_outside = false;
                self.push(.mouse_exit);
            }
        }
    }

    // ---- Keyboard -------------------------------------------------------------

    fn handleKeyPress(self: *Window, ev: *c.XKeyEvent) void {
        self.last_time = ev.time;
        const mods = self.modsFromState(ev.state);
        self.mods = mods;

        var buf: [256]u8 = undefined;
        var sym: c.KeySym = 0;
        const typed = self.lookupText(ev, &buf, &sym);
        // Keycode 0: text committed by the input method (compose result, IME).
        if (ev.keycode == 0) {
            if (typed.len > 0) self.push(.{ .text_input = self.event_arena.allocator().dupe(u8, typed) catch return });
            return;
        }

        const keycode: u8 = @truncate(ev.keycode);
        const repeat = self.keys_down[keycode];
        self.keys_down[keycode] = true;
        const key = keyFromSym(self.baseKeysym(keycode, ev.state)) orelse keyFromSym(sym) orelse {
            if (typed.len > 0 and !mods.ctrl and !mods.alt and !mods.super and typed[0] >= 0x20 and typed[0] != 0x7f) {
                self.push(.{ .text_input = self.event_arena.allocator().dupe(u8, typed) catch return });
            }
            return;
        };

        var text: []const u8 = "";
        if (!mods.ctrl and !mods.alt and !mods.super and typed.len > 0 and typed[0] >= 0x20 and typed[0] != 0x7f) {
            text = self.event_arena.allocator().dupe(u8, typed) catch "";
        }
        self.push(.{ .key_down = .{ .key = key, .mods = mods, .text = text, .repeat = repeat } });
    }

    fn handleKeyRelease(self: *Window, ev: *c.XKeyEvent) void {
        self.last_time = ev.time;
        if (!self.detectable_repeat and self.isAutoRepeatRelease(ev)) return;
        const keycode: u8 = @truncate(ev.keycode);
        if (keycode == 0) return;
        self.keys_down[keycode] = false;
        const key = keyFromSym(self.baseKeysym(keycode, ev.state)) orelse return;
        self.push(.{ .key_up = .{ .key = key, .mods = self.modsFromState(ev.state) } });
    }

    /// Without detectable auto-repeat, the server fakes each repeat with a
    /// release immediately followed by a press carrying the same timestamp.
    fn isAutoRepeatRelease(self: *Window, ev: *const c.XKeyEvent) bool {
        if (c.XEventsQueued(self.display, c.QueuedAfterReading) == 0) return false;
        var next: c.XEvent = undefined;
        _ = c.XPeekEvent(self.display, &next);
        return next.type == c.KeyPress and next.xkey.keycode == ev.keycode and next.xkey.time == ev.time;
    }

    /// Bindings match on the unshifted (level 0) keysym of the active layout.
    fn baseKeysym(self: *Window, keycode: u8, state: c_uint) c.KeySym {
        const group: c_int = @intCast((state >> 13) & 3);
        const sym = c.XkbKeycodeToKeysym(self.display, keycode, group, 0);
        if (sym != c.NoSymbol or group == 0) return sym;
        return c.XkbKeycodeToKeysym(self.display, keycode, 0, 0);
    }

    /// Typed text for a key press as UTF-8 (through the input method when
    /// there is one). Sets `sym` to the keysym with modifiers applied.
    fn lookupText(self: *Window, ev: *c.XKeyEvent, buf: *[256]u8, sym: *c.KeySym) []const u8 {
        if (self.ic) |ic| {
            var status: c_int = 0;
            const n = c.Xutf8LookupString(ic, ev, buf, buf.len, sym, &status);
            if (status != c.XLookupKeySym and status != c.XLookupBoth) sym.* = 0;
            if (status == c.XBufferOverflow) {
                // A long input method commit.
                const big = self.event_arena.allocator().alloc(u8, @intCast(n)) catch return "";
                const m = c.Xutf8LookupString(ic, ev, big.ptr, n, sym, &status);
                if (status != c.XLookupChars and status != c.XLookupBoth) return "";
                return big[0..@intCast(@max(0, m))];
            }
            if (status != c.XLookupChars and status != c.XLookupBoth) return "";
            return buf[0..@intCast(@max(0, n))];
        }
        // XLookupString yields Latin-1; convert through the keysym instead.
        _ = c.XLookupString(ev, buf, buf.len, sym, null);
        const n = c.xkb_keysym_to_utf8(@truncate(sym.*), buf, buf.len);
        return if (n > 1) buf[0..@intCast(n - 1)] else "";
    }

    fn keyFromSym(keysym: c.KeySym) ?platform.Key {
        const sym: u32 = @truncate(keysym);
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

    // ---- Clipboard ------------------------------------------------------------

    fn handleSelectionRequest(self: *Window, req: *const c.XSelectionRequestEvent) void {
        var reply = std.mem.zeroes(c.XEvent);
        reply.xselection = .{
            .type = c.SelectionNotify,
            .display = self.display,
            .requestor = req.requestor,
            .selection = req.selection,
            .target = req.target,
            .property = c.None,
            .time = req.time,
        };
        // Obsolete requestors pass no property; ICCCM says to use the target.
        const property = if (req.property == c.None) req.target else req.property;
        if (req.selection == self.atoms.CLIPBOARD) if (self.clipboard_text) |text| {
            if (self.convertSelection(req.requestor, property, req.target, text)) reply.xselection.property = property;
        };
        _ = c.XSendEvent(self.display, req.requestor, 0, 0, &reply);
        _ = c.XFlush(self.display);
    }

    /// Writes our clipboard in the requested format onto the requestor's property.
    fn convertSelection(self: *Window, requestor: c.Window, property: c.Atom, target: c.Atom, text: []const u8) bool {
        const a = self.atoms;
        const d = self.display;
        if (target == a.TARGETS) {
            const targets = [_]c.Atom{ a.TARGETS, a.TIMESTAMP, a.UTF8_STRING, a.@"text/plain;charset=utf-8", a.TEXT, c.XA_STRING };
            _ = c.XChangeProperty(d, requestor, property, c.XA_ATOM, 32, c.PropModeReplace, @ptrCast(&targets), targets.len);
            return true;
        }
        if (target == a.TIMESTAMP) {
            const time: c_long = @bitCast(self.selection_time);
            _ = c.XChangeProperty(d, requestor, property, c.XA_INTEGER, 32, c.PropModeReplace, @ptrCast(&time), 1);
            return true;
        }
        var data = text;
        const kind: c.Atom = if (target == a.UTF8_STRING or target == a.TEXT)
            a.UTF8_STRING
        else if (target == a.@"text/plain;charset=utf-8")
            target
        else if (target == c.XA_STRING) blk: {
            data = utf8ToLatin1(self.event_arena.allocator(), text) catch return false;
            break :blk c.XA_STRING;
        } else return false;

        // Without INCR, a property must fit in a single request.
        var max_request = c.XExtendedMaxRequestSize(d);
        if (max_request == 0) max_request = c.XMaxRequestSize(d);
        const limit: usize = @intCast(@max(0, max_request * 4 - 1024));
        if (data.len > limit) {
            log.warn("clipboard too large to transfer ({d} bytes)", .{data.len});
            return false;
        }
        _ = c.XChangeProperty(d, requestor, property, kind, 8, c.PropModeReplace, data.ptr, @intCast(data.len));
        return true;
    }

    const Match = struct {
        type: c_int,
        window: c.Window,
        atom: c.Atom,
        target: c.Atom = 0,
    };

    fn matchEvent(_: ?*c.Display, ev: [*c]c.XEvent, arg: c.XPointer) callconv(.c) c_int {
        const m: *const Match = @ptrCast(@alignCast(arg));
        const e = &ev[0];
        if (e.type != m.type) return 0;
        const hit = switch (m.type) {
            c.SelectionNotify => e.xselection.requestor == m.window and e.xselection.selection == m.atom and e.xselection.target == m.target,
            c.PropertyNotify => e.xproperty.window == m.window and e.xproperty.atom == m.atom and e.xproperty.state == c.PropertyNewValue,
            else => false,
        };
        return @intFromBool(hit);
    }

    /// Waits for one specific event, leaving everything else queued.
    fn waitForEvent(self: *Window, out: *c.XEvent, match: Match, timeout_ms: i64) bool {
        var m = match;
        const deadline = platform.nowMs() + timeout_ms;
        while (!self.io_error) {
            if (c.XCheckIfEvent(self.display, out, matchEvent, @ptrCast(&m)) != 0) return true;
            const left = deadline - platform.nowMs();
            if (left <= 0) return false;
            var pfd = [1]platform.PollFd{.{ .fd = c.XConnectionNumber(self.display) }};
            _ = std.c.poll(@ptrCast(&pfd), 1, @intCast(left));
        }
        return false;
    }

    /// Reads (and deletes) the converted selection from our property,
    /// following INCR transfers.
    fn readSelection(self: *Window, gpa: Allocator) !?[]u8 {
        const prop = self.atoms.JIFFY_EDIT_SELECTION;
        var kind: c.Atom = 0;
        const first = self.takeProperty(prop, &kind) orelse return null;
        defer _ = c.XFree(first.ptr);
        if (kind != self.atoms.INCR) return try gpa.dupe(u8, first);

        // INCR: deleting the property (done above) asks the owner for the next
        // chunk; a zero-length chunk ends the transfer.
        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(gpa);
        while (true) {
            var ev: c.XEvent = undefined;
            if (!self.waitForEvent(&ev, .{ .type = c.PropertyNotify, .window = self.window, .atom = prop }, 1000)) {
                log.warn("clipboard transfer timed out", .{});
                out.deinit(gpa);
                return null;
            }
            const chunk = self.takeProperty(prop, &kind) orelse continue;
            defer _ = c.XFree(chunk.ptr);
            // A stale notification for a property that's already gone.
            if (kind == c.None) continue;
            if (chunk.len == 0) break;
            try out.appendSlice(gpa, chunk);
        }
        return try out.toOwnedSlice(gpa);
    }

    /// Reads and deletes a property on our window. The returned bytes are
    /// owned by Xlib (XFree them).
    fn takeProperty(self: *Window, prop: c.Atom, kind: *c.Atom) ?[]u8 {
        var format: c_int = 0;
        var count: c_ulong = 0;
        var after: c_ulong = 0;
        var data: [*c]u8 = null;
        if (c.XGetWindowProperty(self.display, self.window, prop, 0, 0x1fffffff, 1, c.AnyPropertyType, kind, &format, &count, &after, &data) != c.Success) return null;
        if (data == null) return null;
        const unit: usize = switch (format) {
            16 => @sizeOf(c_short),
            32 => @sizeOf(c_long),
            else => 1,
        };
        return data[0 .. count * unit];
    }

    fn latin1ToUtf8(gpa: Allocator, bytes: []const u8) ![]u8 {
        var out: std.ArrayList(u8) = try .initCapacity(gpa, bytes.len);
        errdefer out.deinit(gpa);
        for (bytes) |b| {
            if (b < 0x80) {
                try out.append(gpa, b);
            } else {
                try out.appendSlice(gpa, &.{ 0xC0 | (b >> 6), 0x80 | (b & 0x3F) });
            }
        }
        return try out.toOwnedSlice(gpa);
    }

    /// STRING is Latin-1; characters outside it become '?'.
    fn utf8ToLatin1(arena: Allocator, text: []const u8) ![]u8 {
        const out = try arena.alloc(u8, text.len);
        var n: usize = 0;
        var i: usize = 0;
        while (i < text.len) {
            const len = std.unicode.utf8ByteSequenceLength(text[i]) catch 1;
            const cp: u21 = if (i + len <= text.len) std.unicode.utf8Decode(text[i..][0..len]) catch '?' else '?';
            out[n] = if (cp <= 0xFF) @intCast(cp) else '?';
            n += 1;
            i += if (i + len <= text.len) len else 1;
        }
        return out[0..n];
    }

    // ---- Error handlers -------------------------------------------------------

    fn onXError(display: ?*c.Display, err: [*c]c.XErrorEvent) callconv(.c) c_int {
        var buf: [256]u8 = undefined;
        _ = c.XGetErrorText(display, err.*.error_code, &buf, buf.len);
        log.debug("X error: {s} (request {d}.{d})", .{ std.mem.sliceTo(&buf, 0), err.*.request_code, err.*.minor_code });
        return 0;
    }

    /// Replaces Xlib's default IO error handler, which exits the process.
    fn onIoError(_: ?*c.Display) callconv(.c) c_int {
        return 0;
    }

    /// Called instead of exit() when the connection breaks; Xlib marks the
    /// display dead (further calls are no-ops) and pollEvents reports it.
    fn onIoErrorExit(_: ?*c.Display, data: ?*anyopaque) callconv(.c) void {
        const self: *Window = @ptrCast(@alignCast(data.?));
        if (!self.io_error) log.err("lost the connection to the X server", .{});
        self.io_error = true;
    }
};
