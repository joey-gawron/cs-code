//! Minimal OpenGL 3.3 core bindings, loaded at runtime through a
//! `getProcAddress` function (eglGetProcAddress).

const std = @import("std");

pub const GLenum = u32;
pub const GLuint = u32;
pub const GLint = i32;
pub const GLsizei = i32;
pub const GLfloat = f32;
pub const GLboolean = u8;
pub const GLbitfield = u32;
pub const GLchar = u8;
pub const GLsizeiptr = isize;
pub const GLintptr = isize;

pub const FALSE: GLboolean = 0;
pub const TRUE: GLboolean = 1;

pub const COLOR_BUFFER_BIT: GLbitfield = 0x00004000;
pub const BLEND: GLenum = 0x0BE2;
pub const SCISSOR_TEST: GLenum = 0x0C11;
pub const DEPTH_TEST: GLenum = 0x0B71;
pub const CULL_FACE: GLenum = 0x0B44;
pub const FRAMEBUFFER_SRGB: GLenum = 0x8DB9;
pub const ONE: GLenum = 1;
pub const ZERO: GLenum = 0;
pub const SRC_ALPHA: GLenum = 0x0302;
pub const ONE_MINUS_SRC_ALPHA: GLenum = 0x0303;
pub const SRC1_COLOR: GLenum = 0x88F9;
pub const ONE_MINUS_SRC1_COLOR: GLenum = 0x88FA;
pub const TRIANGLE_STRIP: GLenum = 0x0005;
pub const TRIANGLES: GLenum = 0x0004;
pub const FLOAT: GLenum = 0x1406;
pub const UNSIGNED_BYTE: GLenum = 0x1401;
pub const ARRAY_BUFFER: GLenum = 0x8892;
pub const STREAM_DRAW: GLenum = 0x88E0;
pub const STATIC_DRAW: GLenum = 0x88E4;
pub const DYNAMIC_DRAW: GLenum = 0x88E8;
pub const VERTEX_SHADER: GLenum = 0x8B31;
pub const FRAGMENT_SHADER: GLenum = 0x8B30;
pub const COMPILE_STATUS: GLenum = 0x8B81;
pub const LINK_STATUS: GLenum = 0x8B82;
pub const INFO_LOG_LENGTH: GLenum = 0x8B84;
pub const TEXTURE_2D: GLenum = 0x0DE1;
pub const TEXTURE0: GLenum = 0x84C0;
pub const TEXTURE_MIN_FILTER: GLenum = 0x2801;
pub const TEXTURE_MAG_FILTER: GLenum = 0x2800;
pub const TEXTURE_WRAP_S: GLenum = 0x2802;
pub const TEXTURE_WRAP_T: GLenum = 0x2803;
pub const NEAREST: GLint = 0x2600;
pub const LINEAR: GLint = 0x2601;
pub const CLAMP_TO_EDGE: GLint = 0x812F;
pub const RED: GLenum = 0x1903;
pub const R8: GLenum = 0x8229;
pub const RGBA: GLenum = 0x1908;
pub const RGBA8: GLenum = 0x8058;
pub const UNPACK_ALIGNMENT: GLenum = 0x0CF5;
pub const PACK_ALIGNMENT: GLenum = 0x0D05;
pub const UNPACK_ROW_LENGTH: GLenum = 0x0CF2;
pub const FRAMEBUFFER: GLenum = 0x8D40;
pub const READ_FRAMEBUFFER: GLenum = 0x8CA8;
pub const RENDERBUFFER: GLenum = 0x8D41;
pub const COLOR_ATTACHMENT0: GLenum = 0x8CE0;
pub const FRAMEBUFFER_COMPLETE: GLenum = 0x8CD5;
pub const NO_ERROR: GLenum = 0;
pub const VERSION: GLenum = 0x1F02;
pub const RENDERER: GLenum = 0x1F01;

pub const Procs = struct {
    Viewport: *const fn (GLint, GLint, GLsizei, GLsizei) callconv(.c) void,
    ClearColor: *const fn (GLfloat, GLfloat, GLfloat, GLfloat) callconv(.c) void,
    Clear: *const fn (GLbitfield) callconv(.c) void,
    Enable: *const fn (GLenum) callconv(.c) void,
    Disable: *const fn (GLenum) callconv(.c) void,
    BlendFunc: *const fn (GLenum, GLenum) callconv(.c) void,
    BlendFuncSeparate: *const fn (GLenum, GLenum, GLenum, GLenum) callconv(.c) void,
    Scissor: *const fn (GLint, GLint, GLsizei, GLsizei) callconv(.c) void,
    GetError: *const fn () callconv(.c) GLenum,
    GetString: *const fn (GLenum) callconv(.c) ?[*:0]const u8,
    PixelStorei: *const fn (GLenum, GLint) callconv(.c) void,
    ReadPixels: *const fn (GLint, GLint, GLsizei, GLsizei, GLenum, GLenum, ?*anyopaque) callconv(.c) void,
    Finish: *const fn () callconv(.c) void,

    GenVertexArrays: *const fn (GLsizei, [*]GLuint) callconv(.c) void,
    DeleteVertexArrays: *const fn (GLsizei, [*]const GLuint) callconv(.c) void,
    BindVertexArray: *const fn (GLuint) callconv(.c) void,
    GenBuffers: *const fn (GLsizei, [*]GLuint) callconv(.c) void,
    DeleteBuffers: *const fn (GLsizei, [*]const GLuint) callconv(.c) void,
    BindBuffer: *const fn (GLenum, GLuint) callconv(.c) void,
    BufferData: *const fn (GLenum, GLsizeiptr, ?*const anyopaque, GLenum) callconv(.c) void,
    BufferSubData: *const fn (GLenum, GLintptr, GLsizeiptr, ?*const anyopaque) callconv(.c) void,
    VertexAttribPointer: *const fn (GLuint, GLint, GLenum, GLboolean, GLsizei, ?*const anyopaque) callconv(.c) void,
    VertexAttribDivisor: *const fn (GLuint, GLuint) callconv(.c) void,
    EnableVertexAttribArray: *const fn (GLuint) callconv(.c) void,
    DrawArraysInstanced: *const fn (GLenum, GLint, GLsizei, GLsizei) callconv(.c) void,

    CreateShader: *const fn (GLenum) callconv(.c) GLuint,
    DeleteShader: *const fn (GLuint) callconv(.c) void,
    ShaderSource: *const fn (GLuint, GLsizei, [*]const [*]const GLchar, ?[*]const GLint) callconv(.c) void,
    CompileShader: *const fn (GLuint) callconv(.c) void,
    GetShaderiv: *const fn (GLuint, GLenum, *GLint) callconv(.c) void,
    GetShaderInfoLog: *const fn (GLuint, GLsizei, ?*GLsizei, [*]GLchar) callconv(.c) void,
    CreateProgram: *const fn () callconv(.c) GLuint,
    DeleteProgram: *const fn (GLuint) callconv(.c) void,
    AttachShader: *const fn (GLuint, GLuint) callconv(.c) void,
    LinkProgram: *const fn (GLuint) callconv(.c) void,
    GetProgramiv: *const fn (GLuint, GLenum, *GLint) callconv(.c) void,
    GetProgramInfoLog: *const fn (GLuint, GLsizei, ?*GLsizei, [*]GLchar) callconv(.c) void,
    UseProgram: *const fn (GLuint) callconv(.c) void,
    GetUniformLocation: *const fn (GLuint, [*:0]const GLchar) callconv(.c) GLint,
    Uniform1i: *const fn (GLint, GLint) callconv(.c) void,
    Uniform1f: *const fn (GLint, GLfloat) callconv(.c) void,
    Uniform2f: *const fn (GLint, GLfloat, GLfloat) callconv(.c) void,
    Uniform4f: *const fn (GLint, GLfloat, GLfloat, GLfloat, GLfloat) callconv(.c) void,

    GenTextures: *const fn (GLsizei, [*]GLuint) callconv(.c) void,
    DeleteTextures: *const fn (GLsizei, [*]const GLuint) callconv(.c) void,
    BindTexture: *const fn (GLenum, GLuint) callconv(.c) void,
    ActiveTexture: *const fn (GLenum) callconv(.c) void,
    TexImage2D: *const fn (GLenum, GLint, GLint, GLsizei, GLsizei, GLint, GLenum, GLenum, ?*const anyopaque) callconv(.c) void,
    TexSubImage2D: *const fn (GLenum, GLint, GLint, GLint, GLsizei, GLsizei, GLenum, GLenum, ?*const anyopaque) callconv(.c) void,
    TexParameteri: *const fn (GLenum, GLenum, GLint) callconv(.c) void,

    GenFramebuffers: *const fn (GLsizei, [*]GLuint) callconv(.c) void,
    DeleteFramebuffers: *const fn (GLsizei, [*]const GLuint) callconv(.c) void,
    BindFramebuffer: *const fn (GLenum, GLuint) callconv(.c) void,
    FramebufferTexture2D: *const fn (GLenum, GLenum, GLenum, GLuint, GLint) callconv(.c) void,
    CheckFramebufferStatus: *const fn (GLenum) callconv(.c) GLenum,
};

pub var p: Procs = undefined;

pub const GetProcAddress = *const fn ([*:0]const u8) callconv(.c) ?*const anyopaque;

pub fn load(getProcAddress: GetProcAddress) !void {
    inline for (@typeInfo(Procs).@"struct".field_names) |field_name| {
        const name = "gl" ++ field_name;
        const ptr = getProcAddress(name) orelse {
            std.log.err("missing OpenGL function {s}", .{name});
            return error.MissingGlFunction;
        };
        @field(p, field_name) = @ptrCast(@alignCast(ptr));
    }
}

pub fn checkError(where: []const u8) void {
    const err = p.GetError();
    if (err != NO_ERROR) std.log.err("GL error 0x{x} at {s}", .{ err, where });
}
