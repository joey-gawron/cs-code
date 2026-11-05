//! Fonts, shaping and glyph rasterization (GPUI's `TextSystem`).
//!
//! Lines are shaped with HarfBuzz (with per-character font fallback via
//! fontconfig), cached across frames like GPUI's `LineLayoutCache`, and drawn
//! as atlas sprites rasterized by FreeType at 4 horizontal subpixel offsets.
//! Layout positions are in logical pixels; rasterization is in device pixels.

const std = @import("std");
const c = @import("c");
const fontconfig = @import("fontconfig.zig");
const line_index = @import("line_index.zig");
const atlas_mod = @import("../gpu/atlas.zig");
const scene_mod = @import("../gpu/scene.zig");

const Allocator = std.mem.Allocator;
const Atlas = atlas_mod.Atlas;
const Color = scene_mod.Color;
const Scene = scene_mod.Scene;
const log = std.log.scoped(.text);

pub const FontId = u16;
pub const Weight = fontconfig.Weight;

/// Sublime's per-scope "font_style".
pub const FontStyle = enum(u2) {
    normal,
    italic,
    bold,
    bold_italic,

    pub fn init(italic: bool, bold: bool) FontStyle {
        return @fromBackingInt(@intCast(@as(u2, @intFromBool(italic)) | @as(u2, @intFromBool(bold)) << 1));
    }

    pub fn isItalic(s: FontStyle) bool {
        return s == .italic or s == .bold_italic;
    }

    pub fn isBold(s: FontStyle) bool {
        return s == .bold or s == .bold_italic;
    }
};

pub const FontDesc = struct {
    family: []const u8,
    /// Logical pixels.
    size: f32,
    weight: Weight = .regular,
    italic: bool = false,
};

/// All values in logical pixels.
pub const Metrics = struct {
    ascent: f32,
    descent: f32,
    line_height: f32,
    /// Advance of '0' (the cell width for monospace fonts).
    advance: f32,
    underline_position: f32,
    underline_thickness: f32,
};

const load_target_light: i32 = 1 << 16; // FT_LOAD_TARGET_LIGHT
const subpixel_variants = 4;

const Face = struct {
    ft: c.FT_Face,
    hb: *c.hb_font_t,
    desc: FontDesc,
    family_z: [:0]u8,
    metrics: Metrics = undefined,
    /// Fonts without scalable outlines (e.g. bitmap emoji) can't be sized freely.
    scalable: bool,
    /// The family has no real italic / bold face: slant or embolden outlines.
    synthetic_italic: bool = false,
    synthetic_bold: bool = false,
};

const GlyphKey = struct { font: FontId, glyph: u32, subpixel: u8 };
const GlyphEntry = struct {
    region: atlas_mod.Region,
    left: i32,
    top: i32,
};

pub const ShapedGlyph = struct {
    font: FontId,
    id: u32,
    /// Logical x of the glyph origin relative to the line start.
    x: f32,
    /// Byte index (cluster start) in the line text.
    index: u32,
};

pub const LineLayout = struct {
    glyphs: []ShapedGlyph,
    /// x for every byte index 0..len (inclusive); continuation bytes repeat
    /// their char's x. Lets the editor map columns to pixels in O(1).
    xs: []f32,
    width: f32,
    font: FontId,
    /// The shaped text (for re-mapping glyphs to styled faces).
    text: []const u8 = "",

    pub fn xForIndex(self: *const LineLayout, index: usize) f32 {
        return self.xs[@min(index, self.xs.len - 1)];
    }

    /// Byte index of the char boundary closest to `x`.
    pub fn indexForX(self: *const LineLayout, x: f32) usize {
        if (x <= 0) return 0;
        const len = self.xs.len - 1;
        if (x >= self.width) return len;
        var lo: usize = 0;
        var hi: usize = len;
        while (lo < hi) {
            const mid = (lo + hi + 1) / 2;
            if (self.xs[mid] <= x) lo = mid else hi = mid - 1;
        }
        // lo is the last boundary at or before x; choose the nearer side of that char.
        var next = lo + 1;
        while (next < len and self.xs[next] == self.xs[lo]) next += 1;
        if (next <= len and (x - self.xs[lo]) > (self.xs[next] - x)) return next;
        return lo;
    }

    /// Byte index of the char containing `x` (for hit-testing glyph boxes).
    pub fn indexContainingX(self: *const LineLayout, x: f32) usize {
        const len = self.xs.len - 1;
        if (x <= 0) return 0;
        if (x >= self.width) return len;
        var lo: usize = 0;
        var hi: usize = len;
        while (lo < hi) {
            const mid = (lo + hi + 1) / 2;
            if (self.xs[mid] <= x) lo = mid else hi = mid - 1;
        }
        return lo;
    }

    fn deinit(self: *LineLayout, gpa: Allocator) void {
        gpa.free(self.glyphs);
        gpa.free(self.xs);
        gpa.free(self.text);
    }
};

/// A color (and font style) for the bytes before `end`.
pub const ColorSpan = struct { end: u32, color: Color, style: FontStyle = .normal };

pub const TextSystem = struct {
    gpa: Allocator,
    ft_lib: c.FT_Library,
    faces: std.ArrayList(Face) = .empty,
    scale: f32 = 1,
    atlas: *Atlas,
    atlas_generation: u32 = 0,
    /// Set when the atlas ran out of room this frame; the app resets it and redraws.
    atlas_full: bool = false,
    glyphs: std.AutoHashMapUnmanaged(GlyphKey, ?GlyphEntry) = .empty,
    fallbacks: std.AutoHashMapUnmanaged(struct { FontId, u21 }, FontId) = .empty,
    hb_buffer: *c.hb_buffer_t,
    raster_buf: std.ArrayList(u8) = .empty,

    // Two-generation line cache: layouts used this frame survive into the next.
    lines_current: std.AutoHashMapUnmanaged(u64, *LineLayout) = .empty,
    lines_previous: std.AutoHashMapUnmanaged(u64, *LineLayout) = .empty,
    /// Advances of single chars (1/64 device px) for long-line indexes.
    advances: std.AutoHashMapUnmanaged(struct { FontId, u21 }, i32) = .empty,

    pub fn init(gpa: Allocator, atlas: *Atlas) !TextSystem {
        var lib: c.FT_Library = null;
        if (c.FT_Init_FreeType(&lib) != 0) return error.FreeTypeInitFailed;
        const buffer = c.hb_buffer_create() orelse return error.OutOfMemory;
        return .{ .gpa = gpa, .ft_lib = lib, .atlas = atlas, .hb_buffer = buffer };
    }

    pub fn deinit(self: *TextSystem) void {
        self.clearLineCache();
        self.lines_current.deinit(self.gpa);
        self.lines_previous.deinit(self.gpa);
        for (self.faces.items) |*face| {
            c.hb_font_destroy(face.hb);
            _ = c.FT_Done_Face(face.ft);
            self.gpa.free(face.family_z);
            self.gpa.free(face.desc.family);
        }
        self.faces.deinit(self.gpa);
        self.glyphs.deinit(self.gpa);
        self.fallbacks.deinit(self.gpa);
        self.advances.deinit(self.gpa);
        self.raster_buf.deinit(self.gpa);
        c.hb_buffer_destroy(self.hb_buffer);
        _ = c.FT_Done_FreeType(self.ft_lib);
        // The app's one TextSystem is fontconfig's only user.
        fontconfig.deinit();
    }

    /// Returns a font matching `desc`, loading it on first use.
    pub fn font(self: *TextSystem, desc: FontDesc) !FontId {
        for (self.faces.items, 0..) |face, i| {
            if (face.desc.size == desc.size and face.desc.weight == desc.weight and face.desc.italic == desc.italic and
                std.mem.eql(u8, face.desc.family, desc.family)) return @intCast(i);
        }
        const family_z = try self.gpa.dupeSentinel(u8, desc.family, 0);
        defer self.gpa.free(family_z);
        const file = (try fontconfig.find(self.gpa, family_z, desc.weight, desc.italic)) orelse return error.FontNotFound;
        defer file.deinit(self.gpa);
        return self.loadFace(file, desc);
    }

    /// The italic / bold variant of `base` (same family and size), loaded on
    /// first use; families without one get synthesized slant / emboldening.
    pub fn styledFont(self: *TextSystem, base: FontId, style: FontStyle) FontId {
        if (style == .normal) return base;
        const desc = self.faces.items[base].desc;
        const want: FontDesc = .{ .family = desc.family, .size = desc.size, .weight = if (style.isBold()) .bold else desc.weight, .italic = style.isItalic() or desc.italic };
        const id = self.font(want) catch return base;
        return id;
    }

    fn loadFace(self: *TextSystem, file: fontconfig.FontFile, desc: FontDesc) !FontId {
        var ft: c.FT_Face = null;
        if (c.FT_New_Face(self.ft_lib, file.path.ptr, file.index, &ft) != 0) {
            log.err("failed to load font {s}", .{file.path});
            return error.FontLoadFailed;
        }
        errdefer _ = c.FT_Done_Face(ft);
        const scalable = (ft.*.face_flags & c.FT_FACE_FLAG_SCALABLE) != 0;
        const hb = c.hb_ft_font_create_referenced(ft) orelse return error.OutOfMemory;
        var face: Face = .{
            .ft = ft,
            .hb = hb,
            .desc = .{ .family = try self.gpa.dupe(u8, desc.family), .size = desc.size, .weight = desc.weight, .italic = desc.italic },
            .family_z = try self.gpa.dupeSentinel(u8, desc.family, 0),
            .scalable = scalable,
            // fontconfig falls back to the upright/regular face when a family
            // has no italic/bold; synthesize those like Sublime does.
            .synthetic_italic = desc.italic and scalable and (ft.*.style_flags & c.FT_STYLE_FLAG_ITALIC) == 0,
            .synthetic_bold = desc.weight == .bold and scalable and (ft.*.style_flags & c.FT_STYLE_FLAG_BOLD) == 0,
        };
        self.applySize(&face);
        try self.faces.append(self.gpa, face);
        log.debug("loaded {s} ({s}) at {d}px", .{ desc.family, file.path, desc.size });
        return @intCast(self.faces.items.len - 1);
    }

    fn applySize(self: *TextSystem, face: *Face) void {
        const ft = face.ft;
        const px = face.desc.size * self.scale;
        if (face.scalable) {
            _ = c.FT_Set_Char_Size(ft, 0, @intFromFloat(@round(px * 64)), 72, 72);
        } else if (ft.*.num_fixed_sizes > 0) {
            _ = c.FT_Select_Size(ft, 0);
        }
        c.hb_ft_font_changed(face.hb);
        c.hb_ft_font_set_load_flags(face.hb, load_target_light);

        const m = ft.*.size.*.metrics;
        const s = self.scale;
        const ascent = @round(@as(f32, @floatFromInt(m.ascender)) / 64.0);
        const descent = @round(@as(f32, @floatFromInt(-m.descender)) / 64.0);
        var advance: f32 = px * 0.6;
        if (c.FT_Load_Char(ft, '0', load_target_light) == 0) advance = @as(f32, @floatFromInt(ft.*.glyph.*.advance.x)) / 64.0;
        const y_scale = m.y_scale;
        const ul_pos = @as(f32, @floatFromInt(c.FT_MulFix(ft.*.underline_position, y_scale))) / 64.0;
        const ul_thick = @as(f32, @floatFromInt(c.FT_MulFix(ft.*.underline_thickness, y_scale))) / 64.0;
        face.metrics = .{
            .ascent = ascent / s,
            .descent = descent / s,
            .line_height = @max(@ceil(@as(f32, @floatFromInt(m.height)) / 64.0), ascent + descent) / s,
            .advance = advance / s,
            .underline_position = -ul_pos / s,
            .underline_thickness = @max(1, @round(ul_thick)) / s,
        };
    }

    /// Forget rasterized glyphs (the atlas texture was recreated).
    pub fn resetGlyphs(self: *TextSystem) void {
        self.glyphs.clearRetainingCapacity();
        self.atlas_full = false;
        self.atlas_generation = self.atlas.generation;
    }

    pub fn setScale(self: *TextSystem, scale: f32) void {
        if (scale == self.scale) return;
        self.scale = scale;
        for (self.faces.items) |*face| self.applySize(face);
        self.glyphs.clearRetainingCapacity();
        self.advances.clearRetainingCapacity();
        self.clearLineCache();
    }

    pub fn metrics(self: *const TextSystem, id: FontId) Metrics {
        return self.faces.items[id].metrics;
    }

    fn clearLineCache(self: *TextSystem) void {
        var it = self.lines_current.valueIterator();
        while (it.next()) |l| {
            l.*.deinit(self.gpa);
            self.gpa.destroy(l.*);
        }
        it = self.lines_previous.valueIterator();
        while (it.next()) |l| {
            l.*.deinit(self.gpa);
            self.gpa.destroy(l.*);
        }
        self.lines_current.clearRetainingCapacity();
        self.lines_previous.clearRetainingCapacity();
    }

    /// Call once per frame after drawing: drops layouts not used this frame.
    pub fn endFrame(self: *TextSystem) void {
        var it = self.lines_previous.valueIterator();
        while (it.next()) |l| {
            l.*.deinit(self.gpa);
            self.gpa.destroy(l.*);
        }
        self.lines_previous.clearRetainingCapacity();
        std.mem.swap(@TypeOf(self.lines_current), &self.lines_current, &self.lines_previous);
    }

    /// Shaped layout for a single line of text (no newlines). The pointer is
    /// valid until the end of the next frame.
    pub fn layoutLine(self: *TextSystem, text: []const u8, font_id: FontId) *const LineLayout {
        var hasher = std.hash.Wyhash.init(font_id);
        hasher.update(text);
        const key = hasher.final();
        if (self.lines_current.get(key)) |l| return l;
        if (self.lines_previous.fetchRemove(key)) |kv| {
            self.lines_current.put(self.gpa, key, kv.value) catch {};
            return kv.value;
        }
        const layout = self.shape(text, font_id) catch |err| {
            log.err("shaping failed: {t}", .{err});
            return &empty_layout;
        };
        self.lines_current.put(self.gpa, key, layout) catch {};
        return layout;
    }

    pub fn measure(self: *TextSystem, text: []const u8, font_id: FontId) f32 {
        return self.layoutLine(text, font_id).width;
    }

    /// Per-char advances of `font_id` for a `line_index.LineIndex`, matching
    /// what shaping a line gives each char (fallback fonts included).
    pub fn lineAdvances(self: *TextSystem, font_id: FontId, tab_size: u32) line_index.Advances {
        var a: line_index.Advances = .{ .ascii = undefined, .other = otherAdvance, .ctx = self, .font = font_id, .tab_size = tab_size, .units_per_px = 64 * self.scale };
        for (&a.ascii, 0..) |*w, ch| w.* = if (ch < ' ' or ch == 0x7F) -1 else self.charAdvance(font_id, @intCast(ch));
        return a;
    }

    fn otherAdvance(ctx: ?*anyopaque, font_id: u32, cp: u21) i32 {
        const self: *TextSystem = @ptrCast(@alignCast(ctx.?));
        return self.charAdvance(@intCast(font_id), cp);
    }

    /// How far HarfBuzz moves the pen over `cp` in a line of `font_id`, in
    /// 1/64 device px; 0 for combining marks (they sit on the previous char).
    pub fn charAdvance(self: *TextSystem, font_id: FontId, cp: u21) i32 {
        if (self.advances.get(.{ font_id, cp })) |a| return a;
        const a = self.measureAdvance(font_id, cp);
        self.advances.put(self.gpa, .{ font_id, cp }, a) catch {};
        return a;
    }

    fn measureAdvance(self: *TextSystem, primary: FontId, cp: u21) i32 {
        const gc = c.hb_unicode_general_category(c.hb_unicode_funcs_get_default(), cp);
        if (gc == c.HB_UNICODE_GENERAL_CATEGORY_NON_SPACING_MARK or gc == c.HB_UNICODE_GENERAL_CATEGORY_ENCLOSING_MARK) return 0;
        // The same font choice as `shape`.
        const font_id = if (cp == ' ' or self.hasGlyph(primary, cp)) primary else self.fallbackFor(primary, cp);
        var utf8: [4]u8 = undefined;
        const n = std.unicode.utf8Encode(cp, &utf8) catch return 0;
        const buf = self.hb_buffer;
        c.hb_buffer_reset(buf);
        c.hb_buffer_add_utf8(buf, &utf8, @intCast(n), 0, @intCast(n));
        c.hb_buffer_guess_segment_properties(buf);
        c.hb_buffer_set_direction(buf, c.HB_DIRECTION_LTR);
        c.hb_shape(self.faces.items[font_id].hb, buf, null, 0);
        var count: c_uint = 0;
        const positions = c.hb_buffer_get_glyph_positions(buf, &count);
        if (count == 0) return 0;
        var sum: i32 = 0;
        for (positions[0..count]) |p| sum += p.x_advance;
        return sum;
    }

    fn hasGlyph(self: *TextSystem, font_id: FontId, cp: u21) bool {
        return c.FT_Get_Char_Index(self.faces.items[font_id].ft, cp) != 0;
    }

    fn fallbackFor(self: *TextSystem, primary: FontId, cp: u21) FontId {
        if (self.fallbacks.get(.{ primary, cp })) |f| return f;
        const result: FontId = blk: {
            const face = &self.faces.items[primary];
            const file = (fontconfig.findFallback(self.gpa, face.family_z, cp) catch null) orelse break :blk primary;
            defer file.deinit(self.gpa);
            // Reuse an already-loaded face for the same file and size.
            // (Fallback faces use their file path as the family.)
            for (self.faces.items, 0..) |other, i| {
                if (other.desc.size == face.desc.size and std.mem.eql(u8, other.desc.family, file.path)) break :blk @intCast(i);
            }
            const desc: FontDesc = .{ .family = file.path, .size = face.desc.size };
            const id = self.loadFace(file, desc) catch break :blk primary;
            if (!self.faces.items[id].scalable or !self.hasGlyph(id, cp)) break :blk primary;
            break :blk id;
        };
        self.fallbacks.put(self.gpa, .{ primary, cp }, result) catch {};
        return result;
    }

    fn shape(self: *TextSystem, text: []const u8, primary: FontId) !*LineLayout {
        var glyphs: std.ArrayList(ShapedGlyph) = .empty;
        errdefer glyphs.deinit(self.gpa);
        const xs = try self.gpa.alloc(f32, text.len + 1);
        errdefer self.gpa.free(xs);
        @memset(xs, 0);

        var pen: f32 = 0;
        var run_start: usize = 0;
        var run_font: FontId = primary;
        var i: usize = 0;
        while (i <= text.len) {
            var cp_font = run_font;
            var cp_len: usize = 1;
            if (i < text.len) {
                cp_len = std.unicode.utf8ByteSequenceLength(text[i]) catch 1;
                if (i + cp_len > text.len) cp_len = 1;
                const cp: u21 = std.unicode.utf8Decode(text[i .. i + cp_len]) catch 0xFFFD;
                cp_font = if (cp == ' ' or self.hasGlyph(primary, cp)) primary else self.fallbackFor(primary, cp);
            }
            if (i == text.len or (cp_font != run_font and i > run_start)) {
                pen = try self.shapeRun(text, run_start, i, run_font, pen, &glyphs, xs);
                run_start = i;
            }
            if (i == text.len) break;
            run_font = cp_font;
            i += cp_len;
        }
        xs[text.len] = pen;

        const owned_text = try self.gpa.dupe(u8, text);
        errdefer self.gpa.free(owned_text);
        const layout = try self.gpa.create(LineLayout);
        layout.* = .{ .glyphs = try glyphs.toOwnedSlice(self.gpa), .xs = xs, .width = pen, .font = primary, .text = owned_text };
        return layout;
    }

    fn shapeRun(self: *TextSystem, text: []const u8, start: usize, end: usize, font_id: FontId, pen_start: f32, glyphs: *std.ArrayList(ShapedGlyph), xs: []f32) !f32 {
        if (end <= start) return pen_start;
        const face = &self.faces.items[font_id];
        const buf = self.hb_buffer;
        // HarfBuzz keeps its buffer at peak size (~40 bytes per char): after a
        // huge run (a minified file's line) start over with a small one.
        defer if (end - start > 1 << 16) {
            if (c.hb_buffer_create()) |fresh| {
                c.hb_buffer_destroy(self.hb_buffer);
                self.hb_buffer = fresh;
            }
        };
        c.hb_buffer_reset(buf);
        c.hb_buffer_add_utf8(buf, text.ptr, @intCast(text.len), @intCast(start), @intCast(end - start));
        c.hb_buffer_guess_segment_properties(buf);
        // Code reads left-to-right even when it contains RTL strings.
        c.hb_buffer_set_direction(buf, c.HB_DIRECTION_LTR);
        c.hb_shape(face.hb, buf, null, 0);

        var count: c_uint = 0;
        const infos = c.hb_buffer_get_glyph_infos(buf, &count);
        const positions = c.hb_buffer_get_glyph_positions(buf, &count);
        const inv = 1.0 / (64.0 * self.scale);

        var pen = pen_start;
        var g: usize = 0;
        while (g < count) {
            const cluster = infos[g].cluster;
            const cluster_x = pen;
            // All glyphs sharing this cluster.
            var g_end = g;
            while (g_end < count and infos[g_end].cluster == cluster) : (g_end += 1) {
                try glyphs.append(self.gpa, .{
                    .font = font_id,
                    .id = infos[g_end].codepoint,
                    .x = pen + @as(f32, @floatFromInt(positions[g_end].x_offset)) * inv,
                    .index = cluster,
                });
                pen += @as(f32, @floatFromInt(positions[g_end].x_advance)) * inv;
            }
            const next_cluster: usize = if (g_end < count) infos[g_end].cluster else end;
            // Spread the cluster's advance over its chars (ligatures cover
            // several). A cluster's first byte starts a char even when it is a
            // stray continuation byte (invalid UTF-8, shaped as U+FFFD).
            var chars: usize = 0;
            var b: usize = cluster;
            while (b < next_cluster) : (b += 1) {
                if (b == cluster or text[b] & 0xC0 != 0x80) chars += 1;
            }
            var char_i: usize = 0;
            b = cluster;
            while (b < next_cluster) : (b += 1) {
                if (b == cluster or text[b] & 0xC0 != 0x80) {
                    xs[b] = cluster_x + (pen - cluster_x) * @as(f32, @floatFromInt(char_i)) / @as(f32, @floatFromInt(@max(chars, 1)));
                    char_i += 1;
                } else {
                    xs[b] = xs[b - 1];
                }
            }
            g = g_end;
        }
        return pen;
    }

    fn glyphEntry(self: *TextSystem, font_id: FontId, glyph: u32, subpixel: u8) ?GlyphEntry {
        if (self.atlas_generation != self.atlas.generation) {
            self.glyphs.clearRetainingCapacity();
            self.atlas_generation = self.atlas.generation;
        }
        const key: GlyphKey = .{ .font = font_id, .glyph = glyph, .subpixel = subpixel };
        if (self.glyphs.get(key)) |entry| return entry;
        if (self.atlas_full) return null;
        const entry = self.rasterize(font_id, glyph, subpixel) catch |err| switch (err) {
            error.AtlasFull => {
                self.atlas_full = true;
                return null;
            },
            else => null,
        };
        self.glyphs.put(self.gpa, key, entry) catch {};
        return entry;
    }

    fn rasterize(self: *TextSystem, font_id: FontId, glyph: u32, subpixel: u8) !?GlyphEntry {
        const face = &self.faces.items[font_id];
        const ft = face.ft;
        var delta: c.FT_Vector = .{ .x = @as(c.FT_Pos, subpixel) * (64 / subpixel_variants), .y = 0 };
        // Synthetic oblique: shear x by y (about 12°, like FreeType's FT_GlyphSlot_Oblique).
        var shear: c.FT_Matrix = .{ .xx = 0x10000, .xy = 0x0366A, .yx = 0, .yy = 0x10000 };
        c.FT_Set_Transform(ft, if (face.synthetic_italic) &shear else null, &delta);
        defer c.FT_Set_Transform(ft, null, null);
        if (c.FT_Load_Glyph(ft, glyph, load_target_light | c.FT_LOAD_COLOR) != 0) return null;
        const slot = ft.*.glyph;
        if (face.synthetic_bold and slot.*.format == c.FT_GLYPH_FORMAT_OUTLINE) {
            // Embolden by ~1/24 em, like FT_GlyphSlot_Embolden.
            const strength: c.FT_Pos = @divTrunc(c.FT_MulFix(@intCast(ft.*.units_per_EM), ft.*.size.*.metrics.y_scale), 24);
            _ = c.FT_Outline_Embolden(&slot.*.outline, strength);
        }
        if (slot.*.format != c.FT_GLYPH_FORMAT_BITMAP) {
            if (c.FT_Render_Glyph(slot, c.FT_RENDER_MODE_LIGHT) != 0) return null;
        }
        const bitmap = slot.*.bitmap;
        const w: u32 = bitmap.width;
        const h: u32 = bitmap.rows;
        if (w == 0 or h == 0) return null;

        self.raster_buf.clearRetainingCapacity();
        try self.raster_buf.resize(self.gpa, w * h);
        const out = self.raster_buf.items;
        const pitch: usize = @abs(bitmap.pitch);
        const src: [*]const u8 = bitmap.buffer;
        switch (bitmap.pixel_mode) {
            c.FT_PIXEL_MODE_GRAY => for (0..h) |y| @memcpy(out[y * w ..][0..w], src[y * pitch ..][0..w]),
            c.FT_PIXEL_MODE_MONO => for (0..h) |y| for (0..w) |x| {
                const bit = (src[y * pitch + x / 8] >> @intCast(7 - x % 8)) & 1;
                out[y * w + x] = if (bit != 0) 255 else 0;
            },
            // Color bitmaps (emoji) become a coverage mask from their alpha channel for now.
            c.FT_PIXEL_MODE_BGRA => for (0..h) |y| for (0..w) |x| {
                out[y * w + x] = src[y * pitch + x * 4 + 3];
            },
            else => return null,
        }

        const region = self.atlas.reserve(w, h) orelse return error.AtlasFull;
        self.atlas.upload(region, out, w);
        return .{ .region = region, .left = slot.*.bitmap_left, .top = slot.*.bitmap_top };
    }

    /// Draws `layout` with its origin at logical (x, baseline). `spans` colors
    /// byte ranges; bytes past the last span use `default_color`.
    pub fn drawLine(self: *TextSystem, scene: *Scene, layout: *const LineLayout, x: f32, baseline: f32, default_color: Color, spans: []const ColorSpan) void {
        self.drawLineRange(scene, layout, x, baseline, default_color, spans, 0, std.math.maxInt(u32));
    }

    /// Draws only the glyphs whose source index is in [start, end) (one
    /// soft-wrapped piece of a line). `x` is the origin of the whole line.
    pub fn drawLineRange(self: *TextSystem, scene: *Scene, layout: *const LineLayout, x: f32, baseline: f32, default_color: Color, spans: []const ColorSpan, start: u32, end: u32) void {
        const s = self.scale;
        const clip = scene.currentClip();
        const baseline_px = @round(baseline * s);
        var span_i: usize = 0;
        for (layout.glyphs, 0..) |g, gi| {
            if (g.index < start) continue;
            if (g.index >= end) continue;
            const gx = (x + g.x) * s;
            if (gx > clip.right()) break;
            if (gx + 4 * s * self.faces.items[g.font].desc.size < clip.x) continue;
            while (span_i < spans.len and spans[span_i].end <= g.index) span_i += 1;
            const color = if (span_i < spans.len) spans[span_i].color else default_color;
            const style: FontStyle = if (span_i < spans.len) spans[span_i].style else .normal;
            const floor_x = @floor(gx);
            const subpixel: u8 = @intFromFloat(@floor((gx - floor_x) * subpixel_variants));
            var font_id = g.font;
            var glyph_id = g.id;
            if (style != .normal and g.font == layout.font) {
                if (self.styledGlyph(layout, gi, style)) |sg| {
                    font_id = sg[0];
                    glyph_id = sg[1];
                }
            }
            const entry = self.glyphEntry(font_id, glyph_id, subpixel) orelse continue;
            scene.sprite(.{
                .x = floor_x + @as(f32, @floatFromInt(entry.left)),
                .y = baseline_px - @as(f32, @floatFromInt(entry.top)),
                .w = @floatFromInt(entry.region.w),
                .h = @floatFromInt(entry.region.h),
            }, .{
                .x = @floatFromInt(entry.region.x),
                .y = @floatFromInt(entry.region.y),
                .w = @floatFromInt(entry.region.w),
                .h = @floatFromInt(entry.region.h),
            }, color);
        }
    }

    /// The same character from the styled variant of the line's font, for a
    /// glyph that maps to exactly one character (ligatures stay upright).
    fn styledGlyph(self: *TextSystem, layout: *const LineLayout, gi: usize, style: FontStyle) ?struct { FontId, u32 } {
        const text = layout.text;
        const g = layout.glyphs[gi];
        if (g.index >= text.len) return null;
        const len = std.unicode.utf8ByteSequenceLength(text[g.index]) catch return null;
        const next = g.index + len;
        if (next > text.len) return null;
        // One glyph for several characters (a ligature), or several glyphs
        // for one character: keep the upright shaping.
        const cluster_end: usize = if (gi + 1 < layout.glyphs.len) layout.glyphs[gi + 1].index else text.len;
        if (cluster_end != next) return null;
        if (gi > 0 and layout.glyphs[gi - 1].index == g.index) return null;
        const cp = std.unicode.utf8Decode(text[g.index..next]) catch return null;
        const styled = self.styledFont(layout.font, style);
        if (styled == layout.font) return null;
        const id = c.FT_Get_Char_Index(self.faces.items[styled].ft, cp);
        if (id == 0) return null;
        return .{ styled, id };
    }

    /// Convenience: shape and draw single-color text, returns its width.
    pub fn drawText(self: *TextSystem, scene: *Scene, text: []const u8, font_id: FontId, x: f32, baseline: f32, color: Color) f32 {
        const layout = self.layoutLine(text, font_id);
        self.drawLine(scene, layout, x, baseline, color, &.{});
        return layout.width;
    }
};

var empty_xs = [_]f32{0};
var empty_layout: LineLayout = .{ .glyphs = &.{}, .xs = &empty_xs, .width = 0, .font = 0 };

test "UI text starting with a stray continuation byte shapes" {
    // A file name or label that isn't valid UTF-8: HarfBuzz makes the stray
    // byte a cluster of its own, with no char before it to share an x with.
    const gpa = std.testing.allocator;
    var atlas: Atlas = .{ .size = 64 };
    var ts = try TextSystem.init(gpa, &atlas);
    defer ts.deinit();
    const font_id = ts.font(.{ .family = "monospace", .size = 13 }) catch return error.SkipZigTest;
    for ([_][]const u8{ "\x80name.txt", "\xbf", "a\x80\xbfb", "\xaa\xaa label" }) |text| {
        const layout = ts.layoutLine(text, font_id);
        try std.testing.expectEqual(text.len + 1, layout.xs.len);
        try std.testing.expect(layout.width > 0);
        for (layout.xs[0 .. layout.xs.len - 1], layout.xs[1..]) |x, next| try std.testing.expect(x <= next);
    }
}
