//! EGL + OpenGL 3.3 core context creation, for Wayland and X11 windows and for
//! surfaceless (headless) rendering.

const std = @import("std");
const c = @import("c");
const gl = @import("../gpu/gl.zig");

const log = std.log.scoped(.egl);

pub const Context = struct {
    display: c.EGLDisplay,
    config: c.EGLConfig,
    context: c.EGLContext,
    surface: c.EGLSurface = c.EGL_NO_SURFACE,

    pub fn initWayland(wl_display: *c.struct_wl_display) !Context {
        const display = c.eglGetPlatformDisplay(c.EGL_PLATFORM_WAYLAND_KHR, wl_display, null);
        return init(display, true);
    }

    /// No window system at all: render into an FBO (used for --screenshot).
    pub fn initSurfaceless() !Context {
        const EGL_PLATFORM_SURFACELESS_MESA = 0x31DD;
        const display = c.eglGetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, c.EGL_DEFAULT_DISPLAY, null);
        return init(display, false);
    }

    fn init(display: c.EGLDisplay, window: bool) !Context {
        if (display == c.EGL_NO_DISPLAY) return error.EglNoDisplay;
        var major: c.EGLint = 0;
        var minor: c.EGLint = 0;
        if (c.eglInitialize(display, &major, &minor) != c.EGL_TRUE) {
            log.err("eglInitialize failed: 0x{x}", .{c.eglGetError()});
            return error.EglInitFailed;
        }
        if (c.eglBindAPI(c.EGL_OPENGL_API) != c.EGL_TRUE) return error.EglBindApiFailed;

        const config_attribs = [_]c.EGLint{
            c.EGL_SURFACE_TYPE,    if (window) c.EGL_WINDOW_BIT else 0,
            c.EGL_RENDERABLE_TYPE, c.EGL_OPENGL_BIT,
            c.EGL_RED_SIZE,        8,
            c.EGL_GREEN_SIZE,      8,
            c.EGL_BLUE_SIZE,       8,
            c.EGL_ALPHA_SIZE,      0,
            c.EGL_NONE,
        };
        var config: c.EGLConfig = null;
        var num_configs: c.EGLint = 0;
        if (c.eglChooseConfig(display, &config_attribs, &config, 1, &num_configs) != c.EGL_TRUE or num_configs == 0) {
            log.err("eglChooseConfig found no config: 0x{x}", .{c.eglGetError()});
            return error.EglNoConfig;
        }

        const context_attribs = [_]c.EGLint{
            c.EGL_CONTEXT_MAJOR_VERSION,       3,
            c.EGL_CONTEXT_MINOR_VERSION,       3,
            c.EGL_CONTEXT_OPENGL_PROFILE_MASK, c.EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
            c.EGL_NONE,
        };
        const context = c.eglCreateContext(display, config, c.EGL_NO_CONTEXT, &context_attribs);
        if (context == c.EGL_NO_CONTEXT) {
            log.err("eglCreateContext failed: 0x{x}", .{c.eglGetError()});
            return error.EglContextFailed;
        }
        log.debug("EGL {d}.{d}", .{ major, minor });
        return .{ .display = display, .config = config, .context = context };
    }

    pub fn createWindowSurface(self: *Context, native: *c.struct_wl_egl_window) !void {
        self.surface = c.eglCreatePlatformWindowSurface(self.display, self.config, native, null);
        if (self.surface == c.EGL_NO_SURFACE) {
            log.err("eglCreatePlatformWindowSurface failed: 0x{x}", .{c.eglGetError()});
            return error.EglSurfaceFailed;
        }
    }

    /// X11: `x_display` is the Xlib `Display*`. The window must be created with
    /// the visual from `nativeVisualId`.
    pub fn initX11(x_display: *anyopaque, screen: c_int) !Context {
        const attribs = [_]c.EGLAttrib{ c.EGL_PLATFORM_X11_SCREEN_KHR, screen, c.EGL_NONE };
        const display = c.eglGetPlatformDisplay(c.EGL_PLATFORM_X11_KHR, x_display, &attribs);
        if (display == c.EGL_NO_DISPLAY) return error.EglNoDisplay;
        var major: c.EGLint = 0;
        var minor: c.EGLint = 0;
        if (c.eglInitialize(display, &major, &minor) != c.EGL_TRUE) {
            log.err("eglInitialize failed: 0x{x}", .{c.eglGetError()});
            return error.EglInitFailed;
        }
        errdefer _ = c.eglTerminate(display);
        if (c.eglBindAPI(c.EGL_OPENGL_API) != c.EGL_TRUE) return error.EglBindApiFailed;

        const config_attribs = [_]c.EGLint{
            c.EGL_SURFACE_TYPE,    c.EGL_WINDOW_BIT,
            c.EGL_RENDERABLE_TYPE, c.EGL_OPENGL_BIT,
            c.EGL_RED_SIZE,        8,
            c.EGL_GREEN_SIZE,      8,
            c.EGL_BLUE_SIZE,       8,
            c.EGL_NONE,
        };
        var configs: [64]c.EGLConfig = undefined;
        var num_configs: c.EGLint = 0;
        if (c.eglChooseConfig(display, &config_attribs, &configs, configs.len, &num_configs) != c.EGL_TRUE or num_configs == 0) {
            log.err("eglChooseConfig found no config: 0x{x}", .{c.eglGetError()});
            return error.EglNoConfig;
        }
        // Prefer plain 8-bit RGB with an X visual: a visual with alpha would make
        // the window translucent under compositing managers.
        var config: c.EGLConfig = null;
        var fallback: c.EGLConfig = null;
        for (configs[0..@intCast(num_configs)]) |cfg| {
            if (configAttrib(display, cfg, c.EGL_NATIVE_VISUAL_ID) == 0) continue;
            if (fallback == null) fallback = cfg;
            if (configAttrib(display, cfg, c.EGL_ALPHA_SIZE) == 0 and configAttrib(display, cfg, c.EGL_RED_SIZE) == 8) {
                config = cfg;
                break;
            }
        }
        config = config orelse fallback orelse return error.EglNoConfig;

        const context_attribs = [_]c.EGLint{
            c.EGL_CONTEXT_MAJOR_VERSION,       3,
            c.EGL_CONTEXT_MINOR_VERSION,       3,
            c.EGL_CONTEXT_OPENGL_PROFILE_MASK, c.EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
            c.EGL_NONE,
        };
        const context = c.eglCreateContext(display, config, c.EGL_NO_CONTEXT, &context_attribs);
        if (context == c.EGL_NO_CONTEXT) {
            log.err("eglCreateContext failed: 0x{x}", .{c.eglGetError()});
            return error.EglContextFailed;
        }
        log.debug("EGL {d}.{d} (X11)", .{ major, minor });
        return .{ .display = display, .config = config, .context = context };
    }

    fn configAttrib(display: c.EGLDisplay, config: c.EGLConfig, attrib: c.EGLint) c.EGLint {
        var value: c.EGLint = 0;
        if (c.eglGetConfigAttrib(display, config, attrib, &value) != c.EGL_TRUE) return 0;
        return value;
    }

    /// X visual ID of the chosen config (X11 only).
    pub fn nativeVisualId(self: *const Context) c_ulong {
        return @intCast(@max(0, configAttrib(self.display, self.config, c.EGL_NATIVE_VISUAL_ID)));
    }

    /// `window` points at the X `Window` id.
    pub fn createX11WindowSurface(self: *Context, window: *c_ulong) !void {
        self.surface = c.eglCreatePlatformWindowSurface(self.display, self.config, window, null);
        if (self.surface == c.EGL_NO_SURFACE) {
            log.err("eglCreatePlatformWindowSurface failed: 0x{x}", .{c.eglGetError()});
            return error.EglSurfaceFailed;
        }
    }

    pub fn makeCurrent(self: *Context) !void {
        if (c.eglMakeCurrent(self.display, self.surface, self.surface, self.context) != c.EGL_TRUE) {
            log.err("eglMakeCurrent failed: 0x{x}", .{c.eglGetError()});
            return error.EglMakeCurrentFailed;
        }
    }

    pub fn loadGl(_: *Context) !void {
        try gl.load(@ptrCast(&c.eglGetProcAddress));
    }

    pub fn setSwapInterval(self: *Context, interval: i32) void {
        _ = c.eglSwapInterval(self.display, interval);
    }

    pub fn swapBuffers(self: *Context) void {
        if (c.eglSwapBuffers(self.display, self.surface) != c.EGL_TRUE)
            log.warn("eglSwapBuffers failed: 0x{x}", .{c.eglGetError()});
    }

    pub fn deinit(self: *Context) void {
        _ = c.eglMakeCurrent(self.display, c.EGL_NO_SURFACE, c.EGL_NO_SURFACE, c.EGL_NO_CONTEXT);
        if (self.surface != c.EGL_NO_SURFACE) _ = c.eglDestroySurface(self.display, self.surface);
        _ = c.eglDestroyContext(self.display, self.context);
        _ = c.eglTerminate(self.display);
    }
};
