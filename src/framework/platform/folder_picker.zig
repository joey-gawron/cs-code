//! The system's "choose a folder" panel. macOS only for now: elsewhere there
//! is none and callers keep the folder typed in the page.

const std = @import("std");
const builtin = @import("builtin");

const macos = builtin.os.tag.isDarwin();
const cocoa = if (macos) @import("cocoa.zig") else struct {};

pub const available = macos;

/// Blocks until the panel closes. An absolute path (owned), or null when
/// cancelled or when there is no panel.
pub fn pick(gpa: std.mem.Allocator, start: [:0]const u8, title: [:0]const u8) ?[]u8 {
    if (!macos) return null;
    return cocoa.pickFolder(gpa, start, title);
}
