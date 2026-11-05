//! OpenGL renderer for a `Scene`: one instanced draw per primitive kind per
//! layer (the same batching strategy as GPUI's Blade/Metal renderers).

const std = @import("std");
const gl = @import("gl.zig");
const scene_mod = @import("scene.zig");
const Atlas = @import("atlas.zig").Atlas;

const Scene = scene_mod.Scene;
const Quad = scene_mod.Quad;
const Sprite = scene_mod.Sprite;
const log = std.log.scoped(.renderer);

const quad_vs =
    \\#version 330 core
    \\layout(location = 0) in vec4 a_bounds;
    \\layout(location = 1) in vec4 a_color;
    \\layout(location = 2) in vec4 a_border_color;
    \\layout(location = 3) in vec4 a_radii;
    \\layout(location = 4) in vec4 a_borders;
    \\layout(location = 5) in vec4 a_clip;
    \\uniform vec2 u_viewport;
    \\out vec2 v_pos;
    \\flat out vec4 v_bounds;
    \\flat out vec4 v_color;
    \\flat out vec4 v_border_color;
    \\flat out vec4 v_radii;
    \\flat out vec4 v_borders;
    \\flat out vec4 v_clip;
    \\void main() {
    \\    vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    \\    vec2 pos = a_bounds.xy + corner * a_bounds.zw;
    \\    v_pos = pos;
    \\    v_bounds = a_bounds;
    \\    v_color = a_color;
    \\    v_border_color = a_border_color;
    \\    v_radii = a_radii;
    \\    v_borders = a_borders;
    \\    v_clip = a_clip;
    \\    gl_Position = vec4(pos / u_viewport * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);
    \\}
;

const quad_fs =
    \\#version 330 core
    \\in vec2 v_pos;
    \\flat in vec4 v_bounds;
    \\flat in vec4 v_color;
    \\flat in vec4 v_border_color;
    \\flat in vec4 v_radii;
    \\flat in vec4 v_borders;
    \\flat in vec4 v_clip;
    \\out vec4 frag;
    \\float sd_round_rect(vec2 p, vec2 half_size, float r) {
    \\    vec2 q = abs(p) - half_size + r;
    \\    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
    \\}
    \\float clip_alpha() {
    \\    vec2 lo = v_clip.xy;
    \\    vec2 hi = v_clip.xy + v_clip.zw;
    \\    vec2 inside = step(lo, v_pos) * step(v_pos, hi);
    \\    return inside.x * inside.y;
    \\}
    \\void main() {
    \\    vec2 half_size = v_bounds.zw * 0.5;
    \\    vec2 p = v_pos - (v_bounds.xy + half_size);
    \\    float r = p.x < 0.0 ? (p.y < 0.0 ? v_radii.x : v_radii.w) : (p.y < 0.0 ? v_radii.y : v_radii.z);
    \\    r = min(r, min(half_size.x, half_size.y));
    \\    float outer = sd_round_rect(p, half_size, r);
    \\    float coverage = clamp(0.5 - outer, 0.0, 1.0);
    \\    vec4 bg = vec4(v_color.rgb * v_color.a, v_color.a);
    \\    vec4 color = bg;
    \\    if (any(greaterThan(v_borders, vec4(0.0)))) {
    \\        // v_borders: top, right, bottom, left
    \\        vec2 inner_lo = v_bounds.xy + vec2(v_borders.w, v_borders.x);
    \\        vec2 inner_hi = v_bounds.xy + v_bounds.zw - vec2(v_borders.y, v_borders.z);
    \\        vec2 inner_half = max((inner_hi - inner_lo) * 0.5, vec2(0.0));
    \\        float max_border = max(max(v_borders.x, v_borders.y), max(v_borders.z, v_borders.w));
    \\        float inner_r = max(0.0, r - max_border);
    \\        float inner = sd_round_rect(v_pos - (inner_lo + inner_half), inner_half, inner_r);
    \\        float in_border = clamp(0.5 + inner, 0.0, 1.0);
    \\        vec4 border = vec4(v_border_color.rgb * v_border_color.a, v_border_color.a);
    \\        color = mix(bg, border, in_border);
    \\    }
    \\    frag = color * coverage * clip_alpha();
    \\}
;

const sprite_vs =
    \\#version 330 core
    \\layout(location = 0) in vec4 a_bounds;
    \\layout(location = 1) in vec4 a_uv;
    \\layout(location = 2) in vec4 a_color;
    \\layout(location = 3) in vec4 a_clip;
    \\uniform vec2 u_viewport;
    \\uniform vec2 u_atlas_size;
    \\out vec2 v_pos;
    \\out vec2 v_uv;
    \\flat out vec4 v_color;
    \\flat out vec4 v_clip;
    \\void main() {
    \\    vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    \\    vec2 pos = a_bounds.xy + corner * a_bounds.zw;
    \\    v_pos = pos;
    \\    v_uv = (a_uv.xy + corner * a_uv.zw) / u_atlas_size;
    \\    v_color = a_color;
    \\    v_clip = a_clip;
    \\    gl_Position = vec4(pos / u_viewport * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);
    \\}
;

const sprite_fs =
    \\#version 330 core
    \\in vec2 v_pos;
    \\in vec2 v_uv;
    \\flat in vec4 v_color;
    \\flat in vec4 v_clip;
    \\uniform sampler2D u_atlas;
    \\uniform float u_contrast;
    \\out vec4 frag;
    \\void main() {
    \\    vec2 inside = step(v_clip.xy, v_pos) * step(v_pos, v_clip.xy + v_clip.zw);
    \\    float a = texture(u_atlas, v_uv).r;
    \\    // Boost coverage a little: gamma-space blending makes light-on-dark text look thin.
    \\    a = a * (u_contrast + 1.0) / (a * u_contrast + 1.0);
    \\    a *= v_color.a * inside.x * inside.y;
    \\    frag = vec4(v_color.rgb * a, a);
    \\}
;

const image_vs =
    \\#version 330 core
    \\uniform vec2 u_viewport;
    \\uniform vec4 u_bounds;
    \\out vec2 v_pos;
    \\out vec2 v_uv;
    \\void main() {
    \\    vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    \\    vec2 pos = u_bounds.xy + corner * u_bounds.zw;
    \\    v_pos = pos;
    \\    v_uv = corner;
    \\    gl_Position = vec4(pos / u_viewport * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);
    \\}
;

const image_fs =
    \\#version 330 core
    \\in vec2 v_pos;
    \\in vec2 v_uv;
    \\uniform vec4 u_clip;
    \\uniform sampler2D u_tex;
    \\out vec4 frag;
    \\void main() {
    \\    vec2 inside = step(u_clip.xy, v_pos) * step(v_pos, u_clip.xy + u_clip.zw);
    \\    vec4 c = texture(u_tex, v_uv);
    \\    frag = vec4(c.rgb * c.a, c.a) * inside.x * inside.y;
    \\}
;

const Pipeline = struct {
    program: gl.GLuint,
    vao: gl.GLuint,
    vbo: gl.GLuint,
    capacity: usize = 0,
    u_viewport: gl.GLint,

    fn init(vs: [:0]const u8, fs: [:0]const u8, comptime Instance: type) !Pipeline {
        const program = try linkProgram(vs, fs);
        var vao: gl.GLuint = 0;
        var vbo: gl.GLuint = 0;
        gl.p.GenVertexArrays(1, @ptrCast(&vao));
        gl.p.GenBuffers(1, @ptrCast(&vbo));
        gl.p.BindVertexArray(vao);
        gl.p.BindBuffer(gl.ARRAY_BUFFER, vbo);
        const field_names = @typeInfo(Instance).@"struct".field_names;
        inline for (field_names, 0..) |field_name, i| {
            gl.p.EnableVertexAttribArray(i);
            gl.p.VertexAttribPointer(i, 4, gl.FLOAT, gl.FALSE, @sizeOf(Instance), @ptrFromInt(@offsetOf(Instance, field_name)));
            gl.p.VertexAttribDivisor(i, 1);
        }
        gl.p.BindVertexArray(0);
        return .{
            .program = program,
            .vao = vao,
            .vbo = vbo,
            .u_viewport = gl.p.GetUniformLocation(program, "u_viewport"),
        };
    }

    fn deinit(self: *Pipeline) void {
        gl.p.DeleteProgram(self.program);
        gl.p.DeleteBuffers(1, @ptrCast(&self.vbo));
        gl.p.DeleteVertexArrays(1, @ptrCast(&self.vao));
    }

    fn draw(self: *Pipeline, comptime Instance: type, instances: []const Instance) void {
        if (instances.len == 0) return;
        gl.p.BindVertexArray(self.vao);
        gl.p.BindBuffer(gl.ARRAY_BUFFER, self.vbo);
        const bytes: isize = @intCast(instances.len * @sizeOf(Instance));
        if (instances.len > self.capacity) {
            self.capacity = std.math.ceilPowerOfTwoAssert(usize, instances.len);
            gl.p.BufferData(gl.ARRAY_BUFFER, @intCast(self.capacity * @sizeOf(Instance)), null, gl.STREAM_DRAW);
        }
        gl.p.BufferSubData(gl.ARRAY_BUFFER, 0, bytes, instances.ptr);
        gl.p.DrawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, @intCast(instances.len));
    }
};

/// Colour images: one textured quad per draw, textures made from the scene's
/// image pixels when first needed and dropped when the scene forgets them.
const ImagePipeline = struct {
    program: gl.GLuint,
    vao: gl.GLuint,
    u_viewport: gl.GLint,
    u_bounds: gl.GLint,
    u_clip: gl.GLint,
    u_tex: gl.GLint,
    textures: std.AutoHashMapUnmanaged(u32, gl.GLuint) = .empty,

    fn init() !ImagePipeline {
        const program = try linkProgram(image_vs, image_fs);
        var vao: gl.GLuint = 0;
        gl.p.GenVertexArrays(1, @ptrCast(&vao));
        return .{
            .program = program,
            .vao = vao,
            .u_viewport = gl.p.GetUniformLocation(program, "u_viewport"),
            .u_bounds = gl.p.GetUniformLocation(program, "u_bounds"),
            .u_clip = gl.p.GetUniformLocation(program, "u_clip"),
            .u_tex = gl.p.GetUniformLocation(program, "u_tex"),
        };
    }

    fn deinit(self: *ImagePipeline) void {
        var it = self.textures.valueIterator();
        while (it.next()) |t| gl.p.DeleteTextures(1, @ptrCast(t));
        self.textures.deinit(std.heap.page_allocator);
        gl.p.DeleteProgram(self.program);
        gl.p.DeleteVertexArrays(1, @ptrCast(&self.vao));
    }

    /// Textures for the scene's images; those it dropped are deleted.
    fn sync(self: *ImagePipeline, scene: *const Scene) void {
        var gone: [16]u32 = undefined;
        var n: usize = 0;
        var it = self.textures.keyIterator();
        while (it.next()) |id| {
            if (!scene.images.contains(id.*) and n < gone.len) {
                gone[n] = id.*;
                n += 1;
            }
        }
        for (gone[0..n]) |id| {
            if (self.textures.fetchRemove(id)) |kv| gl.p.DeleteTextures(1, @ptrCast(&kv.value));
        }
        var images = scene.images.iterator();
        while (images.next()) |e| {
            if (self.textures.contains(e.key_ptr.*)) continue;
            var tex: gl.GLuint = 0;
            gl.p.GenTextures(1, @ptrCast(&tex));
            gl.p.BindTexture(gl.TEXTURE_2D, tex);
            gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.p.TexParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            gl.p.PixelStorei(gl.UNPACK_ALIGNMENT, 1);
            const img = e.value_ptr.*;
            gl.p.TexImage2D(gl.TEXTURE_2D, 0, @intCast(gl.RGBA8), @intCast(img.width), @intCast(img.height), 0, gl.RGBA, gl.UNSIGNED_BYTE, img.pixels.ptr);
            self.textures.put(std.heap.page_allocator, e.key_ptr.*, tex) catch gl.p.DeleteTextures(1, @ptrCast(&tex));
        }
    }

    fn draw(self: *ImagePipeline, draws: []const scene_mod.ImageDraw, w: f32, h: f32) void {
        if (draws.len == 0) return;
        gl.p.UseProgram(self.program);
        gl.p.Uniform2f(self.u_viewport, w, h);
        gl.p.Uniform1i(self.u_tex, 0);
        gl.p.ActiveTexture(gl.TEXTURE0);
        gl.p.BindVertexArray(self.vao);
        for (draws) |d| {
            const tex = self.textures.get(d.id) orelse continue;
            gl.p.BindTexture(gl.TEXTURE_2D, tex);
            gl.p.Uniform4f(self.u_bounds, d.bounds[0], d.bounds[1], d.bounds[2], d.bounds[3]);
            gl.p.Uniform4f(self.u_clip, d.clip[0], d.clip[1], d.clip[2], d.clip[3]);
            gl.p.DrawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, 1);
        }
    }
};

pub const Renderer = struct {
    quads: Pipeline,
    sprites: Pipeline,
    images: ImagePipeline,
    u_atlas_size: gl.GLint,
    u_atlas: gl.GLint,
    u_contrast: gl.GLint,
    contrast: f32 = 0.6,

    pub fn init() !Renderer {
        const quads = try Pipeline.init(quad_vs, quad_fs, Quad);
        const sprites = try Pipeline.init(sprite_vs, sprite_fs, Sprite);
        return .{
            .quads = quads,
            .sprites = sprites,
            .images = try ImagePipeline.init(),
            .u_atlas_size = gl.p.GetUniformLocation(sprites.program, "u_atlas_size"),
            .u_atlas = gl.p.GetUniformLocation(sprites.program, "u_atlas"),
            .u_contrast = gl.p.GetUniformLocation(sprites.program, "u_contrast"),
        };
    }

    pub fn deinit(self: *Renderer) void {
        self.quads.deinit();
        self.sprites.deinit();
        self.images.deinit();
    }

    pub fn draw(self: *Renderer, scene: *const Scene, atlas: *const Atlas, clear: scene_mod.Color) void {
        self.images.sync(scene);
        const w = scene.width;
        const h = scene.height;
        gl.p.Viewport(0, 0, @intFromFloat(w), @intFromFloat(h));
        gl.p.ClearColor(clear.r, clear.g, clear.b, clear.a);
        gl.p.Clear(gl.COLOR_BUFFER_BIT);
        gl.p.Enable(gl.BLEND);
        gl.p.BlendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);

        gl.p.UseProgram(self.quads.program);
        gl.p.Uniform2f(self.quads.u_viewport, w, h);
        gl.p.UseProgram(self.sprites.program);
        gl.p.Uniform2f(self.sprites.u_viewport, w, h);
        const atlas_size: f32 = @floatFromInt(atlas.size);
        gl.p.Uniform2f(self.u_atlas_size, atlas_size, atlas_size);
        gl.p.Uniform1i(self.u_atlas, 0);
        gl.p.Uniform1f(self.u_contrast, self.contrast);
        gl.p.ActiveTexture(gl.TEXTURE0);
        gl.p.BindTexture(gl.TEXTURE_2D, atlas.texture);

        for (scene.activeLayers()) |*layer| {
            if (layer.quads.items.len > 0) {
                gl.p.UseProgram(self.quads.program);
                self.quads.draw(Quad, layer.quads.items);
            }
            if (layer.sprites.items.len > 0) {
                gl.p.UseProgram(self.sprites.program);
                gl.p.ActiveTexture(gl.TEXTURE0);
                gl.p.BindTexture(gl.TEXTURE_2D, atlas.texture);
                self.sprites.draw(Sprite, layer.sprites.items);
            }
            self.images.draw(layer.images.items, w, h);
        }
        gl.p.BindVertexArray(0);
    }
};

fn compileShader(kind: gl.GLenum, source: [:0]const u8) !gl.GLuint {
    const shader = gl.p.CreateShader(kind);
    const sources = [_][*]const u8{source.ptr};
    gl.p.ShaderSource(shader, 1, &sources, null);
    gl.p.CompileShader(shader);
    var ok: gl.GLint = 0;
    gl.p.GetShaderiv(shader, gl.COMPILE_STATUS, &ok);
    if (ok == 0) {
        var buf: [2048]u8 = undefined;
        var len: gl.GLsizei = 0;
        gl.p.GetShaderInfoLog(shader, buf.len, &len, &buf);
        log.err("shader compile failed: {s}", .{buf[0..@intCast(len)]});
        return error.ShaderCompileFailed;
    }
    return shader;
}

fn linkProgram(vs: [:0]const u8, fs: [:0]const u8) !gl.GLuint {
    const v = try compileShader(gl.VERTEX_SHADER, vs);
    defer gl.p.DeleteShader(v);
    const f = try compileShader(gl.FRAGMENT_SHADER, fs);
    defer gl.p.DeleteShader(f);
    const program = gl.p.CreateProgram();
    gl.p.AttachShader(program, v);
    gl.p.AttachShader(program, f);
    gl.p.LinkProgram(program);
    var ok: gl.GLint = 0;
    gl.p.GetProgramiv(program, gl.LINK_STATUS, &ok);
    if (ok == 0) {
        var buf: [2048]u8 = undefined;
        var len: gl.GLsizei = 0;
        gl.p.GetProgramInfoLog(program, buf.len, &len, &buf);
        log.err("program link failed: {s}", .{buf[0..@intCast(len)]});
        return error.ProgramLinkFailed;
    }
    return program;
}
