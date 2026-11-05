//! Backend-neutral window: Cocoa on macOS; on Linux Wayland when available,
//! X11 otherwise (GPUI likewise picks its Linux platform at startup).

const std = @import("std");
const builtin = @import("builtin");
const platform = @import("platform.zig");
const macos = builtin.os.tag.isDarwin();
const wayland = if (macos) struct {} else @import("wayland.zig");
const x11 = if (macos) struct {} else @import("x11.zig");
const cocoa = if (macos) @import("cocoa.zig") else struct {};

pub const Options = struct {
    title: [:0]const u8 = "cs-zig",
    app_id: [:0]const u8 = "cs-zig",
    width: i32 = 1280,
    height: i32 = 800,
    /// Window icon sizes, for window lists and task bars that don't find the
    /// desktop entry's icon. Only read by `create`.
    icons: []const platform.IconImage = &.{},
};

pub const Backend = if (macos) enum { cocoa } else enum { wayland, x11 };

/// The window of whichever backend is in use.
const Impl = if (macos) union(Backend) {
    cocoa: *cocoa.Window,
} else union(Backend) {
    wayland: *wayland.Window,
    x11: *x11.Window,
};

pub const Window = struct {
    impl: Impl,

    pub fn create(gpa: std.mem.Allocator, opts: Options, prefer: ?Backend) !Window {
        if (macos) {
            return .{ .impl = .{ .cocoa = try cocoa.Window.create(gpa, .{ .title = opts.title, .width = opts.width, .height = opts.height, .icons = opts.icons }) } };
        } else {
            return .{ .impl = try createLinux(gpa, opts, prefer) };
        }
    }

    /// Picks Wayland if $WAYLAND_DISPLAY is set (or `prefer` says so), else X11.
    fn createLinux(gpa: std.mem.Allocator, opts: Options, prefer: ?Backend) !Impl {
        const want: Backend = prefer orelse if (std.c.getenv("WAYLAND_DISPLAY") != null) .wayland else .x11;
        switch (want) {
            .wayland => {
                if (wayland.Window.create(gpa, .{ .title = opts.title, .app_id = opts.app_id, .width = opts.width, .height = opts.height, .icons = opts.icons })) |w| {
                    return .{ .wayland = w };
                } else |err| {
                    if (prefer == .wayland or std.c.getenv("DISPLAY") == null) return err;
                    std.log.warn("Wayland unavailable ({t}), falling back to X11", .{err});
                }
            },
            .x11 => {},
        }
        return .{ .x11 = try x11.Window.create(gpa, .{ .title = opts.title, .app_id = opts.app_id, .width = opts.width, .height = opts.height, .icons = opts.icons }) };
    }

    pub fn destroy(self: Window) void {
        switch (self.impl) {
            inline else => |w| w.destroy(),
        }
    }

    pub fn scale(self: Window) f32 {
        return switch (self.impl) {
            inline else => |w| w.scale(),
        };
    }

    pub fn bufferSize(self: Window) struct { i32, i32 } {
        return switch (self.impl) {
            inline else => |w| w.bufferSize(),
        };
    }

    pub fn logicalSize(self: Window) platform.Vec2 {
        return switch (self.impl) {
            inline else => |w| w.logicalSize(),
        };
    }

    pub fn pollEvents(self: Window, timeout_ms: i32, extra: []platform.PollFd) !void {
        return switch (self.impl) {
            inline else => |w| w.pollEvents(timeout_ms, extra),
        };
    }

    /// Events from the last `pollEvents` call.
    pub fn events(self: Window) []const platform.Event {
        return switch (self.impl) {
            inline else => |w| w.events.items,
        };
    }

    pub fn canRender(self: Window) bool {
        return switch (self.impl) {
            inline else => |w| w.canRender(),
        };
    }

    pub fn present(self: Window) void {
        switch (self.impl) {
            inline else => |w| w.present(),
        }
    }

    pub fn setTitle(self: Window, title: [:0]const u8) void {
        switch (self.impl) {
            inline else => |w| w.setTitle(title),
        }
    }

    pub fn setCursor(self: Window, shape: platform.CursorShape) void {
        switch (self.impl) {
            inline else => |w| w.setCursor(shape),
        }
    }

    pub fn toggleFullscreen(self: Window) void {
        switch (self.impl) {
            inline else => |w| w.toggleFullscreen(),
        }
    }

    pub fn setClipboard(self: Window, text: []const u8) !void {
        return switch (self.impl) {
            inline else => |w| w.setClipboard(text),
        };
    }

    pub fn getClipboard(self: Window, gpa: std.mem.Allocator) !?[]u8 {
        return switch (self.impl) {
            inline else => |w| w.getClipboard(gpa),
        };
    }

    /// The text we put on the clipboard while we still own it, which closing
    /// the window would take away (Linux).
    pub fn ownedClipboard(self: Window) ?[]const u8 {
        return switch (self.impl) {
            inline else => |w| w.ownedClipboard(),
        };
    }

    /// Files the desktop asked the app to open (Finder's Open With, the
    /// Dock), one absolute path per line, or null. Only macOS has them.
    pub fn takeOpenedPaths(self: Window, gpa: std.mem.Allocator) !?[]u8 {
        if (!macos) return null;
        return self.impl.cocoa.takeOpenedPaths(gpa);
    }

    /// Input method: where the caret is (logical px) so candidate windows
    /// appear next to it, and whether text input is wanted at all.
    pub fn setTextInputRect(self: Window, rect: platform.Rect, enabled: bool) void {
        switch (self.impl) {
            inline else => |w| w.setTextInputRect(rect, enabled),
        }
    }
};
