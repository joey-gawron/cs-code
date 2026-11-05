//! Offscreen rendering target (no window system) for screenshots and
//! scripted UI tests.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");
const gl = @import("../gpu/gl.zig");

const macos = builtin.os.tag.isDarwin();
const egl = if (macos) struct {} else @import("egl.zig");

/// A context with no window: surfaceless EGL on Linux, CGL on macOS.
const Context = if (macos) struct {
    cgl: *anyopaque,

    fn init() !@This() {
        const cgl = c.je_headless_context_create() orelse return error.CglContextFailed;
        errdefer c.je_headless_context_destroy(cgl);
        try gl.load(@ptrCast(&c.je_gl_proc_address));
        return .{ .cgl = cgl };
    }

    fn deinit(self: *@This()) void {
        c.je_headless_context_destroy(self.cgl);
    }
} else struct {
    egl: egl.Context,

    fn init() !@This() {
        var ctx = try egl.Context.initSurfaceless();
        try ctx.makeCurrent();
        try ctx.loadGl();
        return .{ .egl = ctx };
    }

    fn deinit(self: *@This()) void {
        self.egl.deinit();
    }
};

pub const Headless = struct {
    ctx: Context,
    fbo: gl.GLuint = 0,
    texture: gl.GLuint = 0,
    width: u32,
    height: u32,

    pub fn init(width: u32, height: u32) !Headless {
        var self: Headless = .{ .ctx = try Context.init(), .width = width, .height = height };
        gl.p.GenTextures(1, @ptrCast(&self.texture));
        gl.p.BindTexture(gl.TEXTURE_2D, self.texture);
        gl.p.TexImage2D(gl.TEXTURE_2D, 0, @intCast(gl.RGBA8), @intCast(width), @intCast(height), 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.p.GenFramebuffers(1, @ptrCast(&self.fbo));
        gl.p.BindFramebuffer(gl.FRAMEBUFFER, self.fbo);
        gl.p.FramebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, self.texture, 0);
        if (gl.p.CheckFramebufferStatus(gl.FRAMEBUFFER) != gl.FRAMEBUFFER_COMPLETE) return error.FramebufferIncomplete;
        return self;
    }

    pub fn deinit(self: *Headless) void {
        gl.p.DeleteFramebuffers(1, @ptrCast(&self.fbo));
        gl.p.DeleteTextures(1, @ptrCast(&self.texture));
        self.ctx.deinit();
    }

    /// RGBA8 rows, bottom-up (caller frees).
    pub fn readPixels(self: *Headless, gpa: std.mem.Allocator) ![]u8 {
        const pixels = try gpa.alloc(u8, self.width * self.height * 4);
        gl.p.Finish();
        gl.p.BindFramebuffer(gl.READ_FRAMEBUFFER, self.fbo);
        gl.p.PixelStorei(gl.PACK_ALIGNMENT, 1);
        gl.p.ReadPixels(0, 0, @intCast(self.width), @intCast(self.height), gl.RGBA, gl.UNSIGNED_BYTE, pixels.ptr);
        return pixels;
    }
};
