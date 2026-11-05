//! Platform-independent window/input types. The editor only talks to these;
//! `wayland.zig` (and `headless.zig` for screenshots/tests) produce them.

const std = @import("std");
const builtin = @import("builtin");

pub const Modifiers = packed struct(u8) {
    shift: bool = false,
    ctrl: bool = false,
    alt: bool = false,
    super: bool = false,
    _pad: u4 = 0,

    pub fn eql(a: Modifiers, b: Modifiers) bool {
        return @as(u8, @bitCast(a)) == @as(u8, @bitCast(b));
    }

    pub fn none(m: Modifiers) bool {
        return @as(u8, @bitCast(m)) == 0;
    }

    /// Whether the platform's command modifier is held: Cmd on macOS, Ctrl
    /// elsewhere. Clicking with it adds a cursor, as in Sublime Text.
    pub fn command(m: Modifiers) bool {
        return if (builtin.os.tag.isDarwin()) m.super else m.ctrl;
    }
};

pub const NamedKey = enum {
    enter,
    tab,
    backspace,
    delete,
    escape,
    left,
    right,
    up,
    down,
    home,
    end,
    page_up,
    page_down,
    insert,
    f1,
    f2,
    f3,
    f4,
    f5,
    f6,
    f7,
    f8,
    f9,
    f10,
    f11,
    f12,
    context_menu,
};

/// The logical key, taken from the unshifted (level 0) keysym so bindings like
/// "ctrl+shift+p" match regardless of what shift produces.
pub const Key = union(enum) {
    char: u21,
    named: NamedKey,

    pub fn eql(a: Key, b: Key) bool {
        return switch (a) {
            .char => |ca| b == .char and b.char == ca,
            .named => |na| b == .named and b.named == na,
        };
    }
};

pub const KeyEvent = struct {
    key: Key,
    mods: Modifiers,
    /// UTF-8 text this key press produces (empty for commands / non-printables).
    text: []const u8 = "",
    repeat: bool = false,
};

pub const MouseButton = enum { left, right, middle, back, forward };

pub const Vec2 = struct {
    x: f32 = 0,
    y: f32 = 0,
};

pub const MouseEvent = struct {
    pos: Vec2,
    button: MouseButton,
    mods: Modifiers,
    click_count: u8 = 1,
};

pub const MouseMoveEvent = struct {
    pos: Vec2,
    mods: Modifiers,
    /// Button held while moving (drag), if any.
    pressed: ?MouseButton = null,
};

pub const ScrollEvent = struct {
    pos: Vec2,
    /// Positive y scrolls content up (moves the viewport down).
    delta: Vec2,
    /// Wheel notches are reported in lines, touchpads in logical pixels.
    unit: enum { lines, pixels },
    mods: Modifiers,
};

pub const ResizeEvent = struct {
    /// Logical (scaled) size.
    width: f32,
    height: f32,
    scale: f32,
};

pub const Rect = struct {
    x: f32 = 0,
    y: f32 = 0,
    w: f32 = 0,
    h: f32 = 0,
};

/// Input-method composition in progress (shown at the caret, not yet inserted).
pub const Preedit = struct {
    text: []const u8,
    /// Byte range of the IME's cursor within `text`, if any.
    cursor_begin: ?usize = null,
    cursor_end: ?usize = null,
};

pub const Event = union(enum) {
    key_down: KeyEvent,
    key_up: KeyEvent,
    /// Text committed by an input method.
    text_input: []const u8,
    /// Composition update from an input method (empty text clears it).
    preedit: Preedit,
    mouse_down: MouseEvent,
    mouse_up: MouseEvent,
    mouse_move: MouseMoveEvent,
    mouse_exit,
    scroll: ScrollEvent,
    resize: ResizeEvent,
    focus: bool,
    close_requested,
    /// An item of the system menu bar (macOS) was chosen: the name of the
    /// editor action to run (a keymap.Action tag).
    command: []const u8,
    /// A background task finished and posted results; the app should drain its queue.
    wake,
};

/// One size of a window icon: `size`×`size` RGBA8 pixels (`rgba.len` is
/// size·size·4), straight alpha, rows top to bottom.
pub const IconImage = struct {
    size: u32,
    rgba: []const u8,
};

pub const CursorShape = enum {
    default,
    text,
    pointer,
    ew_resize,
    ns_resize,
    grab,
    grabbing,
};

/// Same layout as C's `struct pollfd`: extra fds the event loop waits on
/// (wake-ups, sockets, inotify) alongside the window system connection.
pub const PollFd = extern struct {
    fd: i32,
    events: i16 = 1, // POLLIN
    revents: i16 = 0,

    pub fn readable(self: PollFd) bool {
        return self.revents & 1 != 0;
    }
};

/// Monotonic milliseconds.
pub fn nowMs() i64 {
    var ts: std.c.timespec = undefined;
    _ = std.c.clock_gettime(.MONOTONIC, &ts);
    return @as(i64, ts.sec) * 1000 + @divTrunc(@as(i64, ts.nsec), 1_000_000);
}
