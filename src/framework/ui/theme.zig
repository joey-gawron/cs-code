//! Color schemes and the UI theme. Each scheme defines editor and syntax
//! colors; window chrome is derived from it (like Sublime Text 4's
//! "Adaptive" theme), except Mariana, which keeps hand-tuned chrome modeled
//! on ST4's "Default Dark".

const std = @import("std");
const Color = @import("../gpu/scene.zig").Color;
pub const FontStyle = @import("../text/text_system.zig").FontStyle;

/// Font styles by kind of token: Sublime's per-scope "font_style" (Mariana
/// and Monokai italicize storage types, parameters, `self`/`this`, ...).
pub const SyntaxStyles = struct {
    comment: FontStyle = .normal,
    string: FontStyle = .normal,
    keyword: FontStyle = .normal,
    /// Built-in types (storage.type, support.type).
    storage_type: FontStyle = .normal,
    function: FontStyle = .normal,
    /// Library functions (support.function).
    support_function: FontStyle = .normal,
    type: FontStyle = .normal,
    parameter: FontStyle = .normal,
    variable_builtin: FontStyle = .normal,
    constant: FontStyle = .normal,
    tag: FontStyle = .normal,
    attribute: FontStyle = .normal,
    heading: FontStyle = .bold,
    emphasis: FontStyle = .italic,
    strong: FontStyle = .bold,
};

/// The font style for a tree-sitter capture name.
pub fn styleForCapture(styles: *const SyntaxStyles, name: []const u8) FontStyle {
    const Entry = struct { []const u8, std.meta.FieldEnum(SyntaxStyles) };
    const table = [_]Entry{
        .{ "comment", .comment },
        .{ "string", .string },
        .{ "character", .string },
        .{ "boolean", .constant },
        .{ "constant.builtin", .constant },
        .{ "keyword", .keyword },
        .{ "conditional", .keyword },
        .{ "repeat", .keyword },
        .{ "include", .keyword },
        .{ "exception", .keyword },
        .{ "storageclass", .keyword },
        .{ "type.qualifier", .keyword },
        .{ "type.builtin", .storage_type },
        .{ "function.builtin", .support_function },
        .{ "function", .function },
        .{ "method", .function },
        .{ "constructor", .type },
        .{ "type", .type },
        .{ "namespace", .type },
        .{ "module", .type },
        .{ "variable.builtin", .variable_builtin },
        .{ "variable.parameter", .parameter },
        .{ "parameter", .parameter },
        .{ "tag.attribute", .attribute },
        .{ "attribute", .attribute },
        .{ "tag", .tag },
        .{ "markup.heading", .heading },
        .{ "markup.italic", .emphasis },
        .{ "markup.emphasis", .emphasis },
        .{ "markup.strong", .strong },
        .{ "markup.bold", .strong },
        .{ "text.title", .heading },
        .{ "text.emphasis", .emphasis },
        .{ "text.strong", .strong },
    };
    for (table) |entry| {
        const prefix = entry[0];
        if (std.mem.startsWith(u8, name, prefix) and (name.len == prefix.len or name[prefix.len] == '.')) {
            return switch (entry[1]) {
                inline else => |field| @field(styles, @tagName(field)),
            };
        }
    }
    return .normal;
}

pub const Syntax = struct {
    foreground: Color,
    comment: Color,
    string: Color,
    number: Color,
    constant: Color,
    keyword: Color,
    operator: Color,
    function: Color,
    type: Color,
    variable_builtin: Color,
    parameter: Color,
    property: Color,
    punctuation: Color,
    tag: Color,
    attribute: Color,
    escape: Color,
    label: Color,
};

pub const Theme = struct {
    name: []const u8 = "Mariana",
    light: bool = false,
    syntax_styles: SyntaxStyles = .{},

    // Editor (color scheme)
    background: Color,
    foreground: Color,
    caret: Color,
    selection: Color,
    selection_border: Color,
    inactive_selection: Color,
    line_highlight: Color,
    gutter_foreground: Color,
    gutter_foreground_active: Color,
    indent_guide: Color,
    indent_guide_active: Color,
    find_highlight: Color,
    find_highlight_border: Color,
    bracket_match: Color,
    whitespace: Color,
    rulers: Color,
    syntax: Syntax,

    // Chrome (UI theme)
    window_background: Color,
    sidebar_background: Color,
    sidebar_foreground: Color,
    sidebar_foreground_dim: Color,
    sidebar_selected: Color,
    sidebar_hover: Color,
    sidebar_heading: Color,
    folder_icon: Color,
    tabbar_background: Color,
    tab_inactive: Color,
    tab_inactive_foreground: Color,
    tab_hover: Color,
    tab_active_foreground: Color,
    tab_dirty: Color,
    tab_separator: Color,
    statusbar_background: Color,
    statusbar_foreground: Color,
    panel_background: Color,
    panel_border: Color,
    overlay_background: Color,
    overlay_shadow: Color,
    input_background: Color,
    input_border: Color,
    input_border_focus: Color,
    list_selected: Color,
    list_foreground: Color,
    list_foreground_dim: Color,
    list_match: Color,
    button_background: Color,
    button_active: Color,
    minimap_viewport: Color,
    minimap_viewport_hover: Color,
    scrollbar_puck: Color,
    scrollbar_puck_hover: Color,
    menubar_background: Color,
    menubar_foreground: Color,
    menu_background: Color,
    menu_selected: Color,
    menu_separator: Color,
    menu_shortcut: Color,
};

// Mariana palette.
const blue = Color.hex(0x303841);
const blue2 = Color.hexa(0x4e5a65, 0.7);
const blue3 = Color.hex(0x343d46);
const blue4 = Color.hex(0x65737e);
const blue5 = Color.hex(0x5fb4b4);
const blue6 = Color.hex(0xa6acb9);
const green = Color.hex(0x99c794);
const orange = Color.hex(0xf9ae58);
const orange2 = Color.hex(0xee932b);
const orange3 = Color.hex(0xfac761);
const pink = Color.hex(0xc695c6);
const red = Color.hex(0xec5f66);
const red2 = Color.hex(0xf97b58);
const white = Color.hex(0xffffff);
const white3 = Color.hex(0xd8dee9);

pub const mariana: Theme = .{
    .background = blue,
    .foreground = white3,
    .caret = orange,
    .selection = blue2,
    .selection_border = Color.hexa(0x4e5a65, 0.0),
    .inactive_selection = Color.hexa(0x4e5a65, 0.45),
    .line_highlight = blue3,
    .gutter_foreground = Color.hexa(0xd8dee9, 0.35),
    .gutter_foreground_active = Color.hexa(0xd8dee9, 0.8),
    .indent_guide = Color.hexa(0xd8dee9, 0.08),
    .indent_guide_active = Color.hexa(0xd8dee9, 0.22),
    .find_highlight = Color.hexa(0xfac761, 0.25),
    .find_highlight_border = orange3,
    .bracket_match = blue5,
    .whitespace = Color.hexa(0xd8dee9, 0.25),
    .rulers = Color.hexa(0xd8dee9, 0.1),
    .syntax = .{
        .foreground = white3,
        .comment = blue6,
        .string = green,
        .number = orange,
        .constant = red,
        .keyword = pink,
        .operator = red2,
        .function = blue5,
        .type = orange,
        .variable_builtin = red,
        .parameter = orange,
        .property = white3,
        .punctuation = blue5,
        .tag = red,
        .attribute = pink,
        .escape = pink,
        .label = orange2,
    },

    .window_background = Color.hex(0x1d2228),
    .sidebar_background = Color.hex(0x252b32),
    .sidebar_foreground = Color.hex(0xc5ccd6),
    .sidebar_foreground_dim = Color.hex(0x8a94a1),
    .sidebar_selected = Color.hex(0x37414c),
    .sidebar_hover = Color.hex(0x2d343c),
    .sidebar_heading = Color.hex(0x7d8794),
    .folder_icon = Color.hex(0x8d98a5),
    .tabbar_background = Color.hex(0x1f2429),
    .tab_inactive = Color.hex(0x262c33),
    .tab_inactive_foreground = Color.hex(0x8f99a6),
    .tab_hover = Color.hex(0x2b323a),
    .tab_active_foreground = Color.hex(0xe6ebf2),
    .tab_dirty = orange,
    .tab_separator = Color.hex(0x161a1e),
    .statusbar_background = Color.hex(0x1f2429),
    .statusbar_foreground = Color.hex(0x9aa4b1),
    .panel_background = Color.hex(0x262c33),
    .panel_border = Color.hex(0x161a1e),
    .overlay_background = Color.hex(0x2a3139),
    .overlay_shadow = Color.hexa(0x000000, 0.45),
    .input_background = Color.hex(0x1f2429),
    .input_border = Color.hex(0x3b4550),
    .input_border_focus = Color.hex(0x5fb4b4),
    .list_selected = Color.hex(0x3a4550),
    .list_foreground = Color.hex(0xd8dee9),
    .list_foreground_dim = Color.hex(0x8a94a1),
    .list_match = Color.hex(0x5fb4b4),
    .button_background = Color.hex(0x303841),
    .button_active = Color.hex(0x4a5764),
    .minimap_viewport = Color.hexa(0xffffff, 0.06),
    .minimap_viewport_hover = Color.hexa(0xffffff, 0.12),
    .scrollbar_puck = Color.hexa(0xffffff, 0.16),
    .scrollbar_puck_hover = Color.hexa(0xffffff, 0.3),
    .menubar_background = Color.hex(0x1d2228),
    .menubar_foreground = Color.hex(0xc5ccd6),
    .menu_background = Color.hex(0x2a3139),
    .menu_selected = Color.hex(0x3d4855),
    .menu_separator = Color.hex(0x3b4550),
    .menu_shortcut = Color.hex(0x8a94a1),
};

/// cs-code's default theme, Visual Studio Dark: editor #1E1E1E, tool windows
/// #252526, chrome #2D2D30, borders #3F3F46, accent and status bar #007ACC.
pub const vs_dark: Theme = .{
    .background = Color.hex(0x1e1e1e),
    .foreground = Color.hex(0xdcdcdc),
    .caret = Color.hex(0xaeafad),
    .selection = Color.hex(0x264f78),
    .selection_border = Color.hexa(0x264f78, 0.0),
    .inactive_selection = Color.hex(0x3a3d41),
    .line_highlight = Color.hexa(0xffffff, 0.04),
    .gutter_foreground = Color.hex(0x858585),
    .gutter_foreground_active = Color.hex(0xc6c6c6),
    .indent_guide = Color.hexa(0xffffff, 0.1),
    .indent_guide_active = Color.hexa(0xffffff, 0.3),
    .find_highlight = Color.hexa(0xea5c00, 0.33),
    .find_highlight_border = Color.hex(0xea5c00),
    .bracket_match = Color.hex(0x888888),
    .whitespace = Color.hexa(0xffffff, 0.25),
    .rulers = Color.hexa(0xffffff, 0.1),
    .syntax = .{
        .foreground = Color.hex(0xdcdcdc),
        .comment = Color.hex(0x57a64a),
        .string = Color.hex(0xd69d85),
        .number = Color.hex(0xb5cea8),
        .constant = Color.hex(0x4fc1ff),
        .keyword = Color.hex(0x569cd6),
        .operator = Color.hex(0xb4b4b4),
        .function = Color.hex(0xdcdcaa),
        .type = Color.hex(0x4ec9b0),
        .variable_builtin = Color.hex(0x569cd6),
        .parameter = Color.hex(0x9cdcfe),
        .property = Color.hex(0x9cdcfe),
        .punctuation = Color.hex(0xdcdcdc),
        .tag = Color.hex(0x569cd6),
        .attribute = Color.hex(0x9cdcfe),
        .escape = Color.hex(0xd7ba7d),
        .label = Color.hex(0xc8c8c8),
    },

    .window_background = Color.hex(0x2d2d30),
    .sidebar_background = Color.hex(0x252526),
    .sidebar_foreground = Color.hex(0xf1f1f1),
    .sidebar_foreground_dim = Color.hex(0xa0a0a0),
    .sidebar_selected = Color.hex(0x3f3f46),
    .sidebar_hover = Color.hex(0x333337),
    .sidebar_heading = Color.hex(0xf1f1f1),
    .folder_icon = Color.hex(0xdcb67a),
    .tabbar_background = Color.hex(0x2d2d30),
    .tab_inactive = Color.hex(0x2d2d30),
    .tab_inactive_foreground = Color.hex(0xf1f1f1),
    .tab_hover = Color.hex(0x3f3f46),
    .tab_active_foreground = Color.hex(0xffffff),
    .tab_dirty = Color.hex(0xffffff),
    .tab_separator = Color.hex(0x3f3f46),
    .statusbar_background = Color.hex(0x007acc),
    .statusbar_foreground = Color.hex(0xffffff),
    .panel_background = Color.hex(0x252526),
    .panel_border = Color.hex(0x3f3f46),
    .overlay_background = Color.hex(0x252526),
    .overlay_shadow = Color.hexa(0x000000, 0.5),
    .input_background = Color.hex(0x333337),
    .input_border = Color.hex(0x3f3f46),
    .input_border_focus = Color.hex(0x007acc),
    .list_selected = Color.hex(0x007acc),
    .list_foreground = Color.hex(0xf1f1f1),
    .list_foreground_dim = Color.hex(0xa0a0a0),
    .list_match = Color.hex(0x75beff),
    .button_background = Color.hex(0x3f3f46),
    .button_active = Color.hex(0x1c97ea),
    .minimap_viewport = Color.hexa(0xffffff, 0.06),
    .minimap_viewport_hover = Color.hexa(0xffffff, 0.12),
    .scrollbar_puck = Color.hexa(0x686868, 0.7),
    .scrollbar_puck_hover = Color.hexa(0x9e9e9e, 0.7),
    .menubar_background = Color.hex(0x2d2d30),
    .menubar_foreground = Color.hex(0xf1f1f1),
    .menu_background = Color.hex(0x1b1b1c),
    .menu_selected = Color.hex(0x333337),
    .menu_separator = Color.hex(0x333337),
    .menu_shortcut = Color.hex(0xa0a0a0),
};

/// Maps a tree-sitter highlight capture name ("keyword.function", "string",
/// ...) to a color, using the most specific prefix we know.
pub fn colorForCapture(syntax: *const Syntax, name: []const u8) ?Color {
    const Entry = struct { []const u8, std.meta.FieldEnum(Syntax) };
    const table = [_]Entry{
        .{ "comment", .comment },
        .{ "string.escape", .escape },
        .{ "string.special", .escape },
        .{ "string.regexp", .escape },
        .{ "string", .string },
        .{ "character", .string },
        .{ "escape", .escape },
        .{ "number", .number },
        .{ "float", .number },
        .{ "boolean", .constant },
        .{ "constant.builtin", .constant },
        .{ "constant.numeric", .number },
        .{ "constant", .number },
        .{ "keyword.operator", .keyword },
        .{ "keyword.function", .keyword },
        .{ "keyword", .keyword },
        .{ "conditional", .keyword },
        .{ "repeat", .keyword },
        .{ "include", .keyword },
        .{ "exception", .keyword },
        .{ "storageclass", .keyword },
        .{ "type.qualifier", .keyword },
        .{ "operator", .operator },
        .{ "function.builtin", .function },
        .{ "function.macro", .function },
        .{ "function.method", .function },
        .{ "function.call", .function },
        .{ "function", .function },
        .{ "method", .function },
        .{ "constructor", .type },
        .{ "type.builtin", .keyword },
        .{ "type", .type },
        .{ "namespace", .type },
        .{ "module", .type },
        .{ "variable.builtin", .variable_builtin },
        .{ "variable.parameter", .parameter },
        .{ "parameter", .parameter },
        .{ "variable.member", .property },
        .{ "property", .property },
        .{ "field", .property },
        .{ "attribute", .attribute },
        .{ "tag.attribute", .attribute },
        .{ "tag.delimiter", .foreground },
        .{ "tag", .tag },
        .{ "label", .label },
        .{ "punctuation.special", .keyword },
        .{ "punctuation.delimiter", .foreground },
        .{ "punctuation.bracket", .foreground },
        .{ "punctuation", .foreground },
        .{ "markup.heading", .function },
        .{ "markup.link", .string },
        .{ "markup.raw", .string },
        .{ "markup.math", .string },
        .{ "markup.list", .keyword },
        .{ "markup.quote", .comment },
        .{ "markup.strong", .foreground },
        .{ "markup.italic", .foreground },
        .{ "markup.strikethrough", .foreground },
        .{ "markup.underline", .foreground },
        .{ "text.title", .function },
        .{ "text.literal", .string },
        .{ "text.uri", .string },
        .{ "text.reference", .string },
        .{ "text.emphasis", .foreground },
        .{ "text.strong", .foreground },
        .{ "diff.plus", .string },
        .{ "diff.minus", .constant },
        .{ "diff.delta", .number },
        .{ "delimiter", .foreground },
        .{ "preproc", .keyword },
        .{ "special", .escape },
        .{ "embedded", .foreground },
        .{ "variable", .foreground },
        // Resets what an enclosing node's capture painted (an interpolation
        // inside a string, a Markdown code fence's injected code).
        .{ "none", .foreground },
    };
    for (table) |entry| {
        const prefix = entry[0];
        if (std.mem.startsWith(u8, name, prefix) and (name.len == prefix.len or name[prefix.len] == '.')) {
            return switch (entry[1]) {
                inline else => |field| @field(syntax, @tagName(field)),
            };
        }
    }
    return null;
}

// ---- Scheme catalogue ---------------------------------------------------------

pub const Scheme = struct {
    name: []const u8,
    light: bool,
    background: Color,
    foreground: Color,
    caret: Color,
    selection: Color,
    line_highlight: Color,
    syntax: Syntax,
    /// Complete hand-tuned theme (chrome included) instead of derived chrome.
    tuned: ?*const Theme = null,
    styles: SyntaxStyles = .{},
};

/// Mariana's italics (as in Sublime Text 4).
const mariana_styles: SyntaxStyles = .{ .storage_type = .italic, .parameter = .italic, .variable_builtin = .italic, .support_function = .italic };
/// Monokai's italics.
const monokai_styles: SyntaxStyles = .{ .storage_type = .italic, .parameter = .italic, .variable_builtin = .italic };
/// The Neovim themes (fox, gruvbox) italicize comments.
const nvim_styles: SyntaxStyles = .{ .comment = .italic };

fn hx(rgb: u24) Color {
    return Color.hex(rgb);
}

pub const schemes = [_]Scheme{
    .{
        .name = "Visual Studio Dark",
        .light = false,
        .background = vs_dark.background,
        .foreground = vs_dark.foreground,
        .caret = vs_dark.caret,
        .selection = vs_dark.selection,
        .line_highlight = vs_dark.line_highlight,
        .syntax = vs_dark.syntax,
        .tuned = &vs_dark,
    },
    .{
        .name = "Mariana",
        .styles = mariana_styles,
        .light = false,
        .background = mariana.background,
        .foreground = mariana.foreground,
        .caret = mariana.caret,
        .selection = mariana.selection,
        .line_highlight = mariana.line_highlight,
        .syntax = mariana.syntax,
        .tuned = &mariana,
    },
    // Sublime Text 4's default light scheme (an approximation).
    .{
        .name = "Breakers",
        .styles = mariana_styles,
        .light = true,
        .background = hx(0xf8f9fa),
        .foreground = hx(0x37414b),
        .caret = hx(0x3b8fb4),
        .selection = Color.hexa(0xb6cde0, 0.7),
        .line_highlight = hx(0xeef1f4),
        .syntax = .{
            .foreground = hx(0x37414b),
            .comment = hx(0x8d98a5),
            .string = hx(0x55925b),
            .number = hx(0xd47d2a),
            .constant = hx(0xcc3e4a),
            .keyword = hx(0x9b52a8),
            .operator = hx(0xcc5c3c),
            .function = hx(0x2e8a9c),
            .type = hx(0xbb7a1c),
            .variable_builtin = hx(0xcc3e4a),
            .parameter = hx(0xbb7a1c),
            .property = hx(0x37414b),
            .punctuation = hx(0x2e8a9c),
            .tag = hx(0xcc3e4a),
            .attribute = hx(0x9b52a8),
            .escape = hx(0x9b52a8),
            .label = hx(0xbb7a1c),
        },
    },
    .{
        .name = "Monokai",
        .styles = monokai_styles,
        .light = false,
        .background = hx(0x272822),
        .foreground = hx(0xf8f8f2),
        .caret = hx(0xf8f8f0),
        .selection = hx(0x49483e),
        .line_highlight = hx(0x3e3d32),
        .syntax = .{
            .foreground = hx(0xf8f8f2),
            .comment = hx(0x75715e),
            .string = hx(0xe6db74),
            .number = hx(0xae81ff),
            .constant = hx(0xae81ff),
            .keyword = hx(0xf92672),
            .operator = hx(0xf92672),
            .function = hx(0xa6e22e),
            .type = hx(0x66d9ef),
            .variable_builtin = hx(0xfd971f),
            .parameter = hx(0xfd971f),
            .property = hx(0xf8f8f2),
            .punctuation = hx(0xf8f8f2),
            .tag = hx(0xf92672),
            .attribute = hx(0xa6e22e),
            .escape = hx(0xae81ff),
            .label = hx(0xe6db74),
        },
    },
    // Dayfox / Nightfox from nightfox.nvim (EdenEast).
    .{
        .name = "Dayfox",
        .styles = nvim_styles,
        .light = true,
        .background = hx(0xf6f2ee),
        .foreground = hx(0x3d2b5a),
        .caret = hx(0x3d2b5a),
        .selection = hx(0xe7d2be),
        .line_highlight = hx(0xebe5df),
        .syntax = .{
            .foreground = hx(0x3d2b5a),
            .comment = hx(0x837a72),
            .string = hx(0x396847),
            .number = hx(0x955f61),
            .constant = hx(0xa57a7c),
            .keyword = hx(0x6e33ce),
            .operator = hx(0x643f61),
            .function = hx(0x4863b6),
            .type = hx(0xac5402),
            .variable_builtin = hx(0xa5222f),
            .parameter = hx(0x287980),
            .property = hx(0x2848a9),
            .punctuation = hx(0x643f61),
            .tag = hx(0xa5222f),
            .attribute = hx(0xa440b5),
            .escape = hx(0xb86e28),
            .label = hx(0x287980),
        },
    },
    .{
        .name = "Nightfox",
        .styles = nvim_styles,
        .light = false,
        .background = hx(0x192330),
        .foreground = hx(0xcdcecf),
        .caret = hx(0xcdcecf),
        .selection = hx(0x2b3b51),
        .line_highlight = hx(0x29394f),
        .syntax = .{
            .foreground = hx(0xcdcecf),
            .comment = hx(0x738091),
            .string = hx(0x81b29a),
            .number = hx(0xf4a261),
            .constant = hx(0xf6b079),
            .keyword = hx(0x9d79d6),
            .operator = hx(0xaeafb0),
            .function = hx(0x86abdc),
            .type = hx(0xdbc074),
            .variable_builtin = hx(0xc94f6d),
            .parameter = hx(0x63cdcf),
            .property = hx(0x719cd6),
            .punctuation = hx(0xaeafb0),
            .tag = hx(0xc94f6d),
            .attribute = hx(0xd67ad2),
            .escape = hx(0xe0c989),
            .label = hx(0x63cdcf),
        },
    },
    // Gruvbox (morhetz).
    .{
        .name = "Gruvbox Dark",
        .styles = nvim_styles,
        .light = false,
        .background = hx(0x282828),
        .foreground = hx(0xebdbb2),
        .caret = hx(0xebdbb2),
        .selection = hx(0x504945),
        .line_highlight = hx(0x3c3836),
        .syntax = .{
            .foreground = hx(0xebdbb2),
            .comment = hx(0x928374),
            .string = hx(0xb8bb26),
            .number = hx(0xd3869b),
            .constant = hx(0xd3869b),
            .keyword = hx(0xfb4934),
            .operator = hx(0xfe8019),
            .function = hx(0x8ec07c),
            .type = hx(0xfabd2f),
            .variable_builtin = hx(0xfe8019),
            .parameter = hx(0x83a598),
            .property = hx(0x83a598),
            .punctuation = hx(0xa89984),
            .tag = hx(0xfb4934),
            .attribute = hx(0x8ec07c),
            .escape = hx(0xfe8019),
            .label = hx(0x8ec07c),
        },
    },
    .{
        .name = "Gruvbox Light",
        .styles = nvim_styles,
        .light = true,
        .background = hx(0xfbf1c7),
        .foreground = hx(0x3c3836),
        .caret = hx(0x3c3836),
        .selection = hx(0xd5c4a1),
        .line_highlight = hx(0xf2e5bc),
        .syntax = .{
            .foreground = hx(0x3c3836),
            .comment = hx(0x928374),
            .string = hx(0x79740e),
            .number = hx(0x8f3f71),
            .constant = hx(0x8f3f71),
            .keyword = hx(0x9d0006),
            .operator = hx(0xaf3a03),
            .function = hx(0x427b58),
            .type = hx(0xb57614),
            .variable_builtin = hx(0xaf3a03),
            .parameter = hx(0x076678),
            .property = hx(0x076678),
            .punctuation = hx(0x7c6f64),
            .tag = hx(0x9d0006),
            .attribute = hx(0x427b58),
            .escape = hx(0xaf3a03),
            .label = hx(0x427b58),
        },
    },
    // Solarized (Ethan Schoonover).
    .{
        .name = "Solarized Dark",
        .light = false,
        .background = hx(0x002b36),
        .foreground = hx(0x839496),
        .caret = hx(0x93a1a1),
        .selection = hx(0x274642),
        .line_highlight = hx(0x073642),
        .syntax = solarizedSyntax(hx(0x839496), hx(0x586e75)),
    },
    .{
        .name = "Solarized Light",
        .light = true,
        .background = hx(0xfdf6e3),
        .foreground = hx(0x657b83),
        .caret = hx(0x586e75),
        .selection = hx(0xeee8d5),
        .line_highlight = hx(0xeee8d5),
        .syntax = solarizedSyntax(hx(0x657b83), hx(0x93a1a1)),
    },
};

fn solarizedSyntax(fg: Color, comment: Color) Syntax {
    return .{
        .foreground = fg,
        .comment = comment,
        .string = hx(0x2aa198),
        .number = hx(0xd33682),
        .constant = hx(0xcb4b16),
        .keyword = hx(0x859900),
        .operator = hx(0x859900),
        .function = hx(0x268bd2),
        .type = hx(0xb58900),
        .variable_builtin = hx(0xcb4b16),
        .parameter = hx(0x6c71c4),
        .property = fg,
        .punctuation = fg,
        .tag = hx(0x268bd2),
        .attribute = hx(0xb58900),
        .escape = hx(0xdc322f),
        .label = hx(0xcb4b16),
    };
}

pub fn schemeByName(name: []const u8) ?*const Scheme {
    for (&schemes) |*s| if (std.ascii.eqlIgnoreCase(s.name, name)) return s;
    // Accept Sublime-style file names ("Mariana.sublime-color-scheme").
    const base = if (std.mem.indexOfScalar(u8, name, '.')) |dot| name[0..dot] else name;
    for (&schemes) |*s| if (std.ascii.eqlIgnoreCase(s.name, base)) return s;
    return null;
}

/// Full theme for a scheme: its colors plus derived chrome.
pub fn build(s: *const Scheme) Theme {
    if (s.tuned) |t| {
        var copy = t.*;
        copy.name = s.name;
        copy.syntax_styles = s.styles;
        return copy;
    }
    const bg = s.background;
    const fg = s.foreground;
    const accent = s.syntax.function;
    const light = s.light;
    // Chrome sits darker than the editor for dark schemes, slightly darker for light ones.
    const shade = struct {
        fn f(c: Color, is_light: bool, dark_amount: f32, light_amount: f32) Color {
            return if (is_light) c.darken(light_amount) else c.darken(dark_amount);
        }
    }.f;
    const raised = if (light) bg.darken(0.12) else bg.lighten(0.08);
    return .{
        .name = s.name,
        .light = light,
        .background = bg,
        .foreground = fg,
        .caret = s.caret,
        .selection = s.selection,
        .selection_border = Color.transparent,
        .inactive_selection = s.selection.withAlpha(s.selection.a * 0.6),
        .line_highlight = s.line_highlight,
        .gutter_foreground = fg.withAlpha(if (light) 0.45 else 0.35),
        .gutter_foreground_active = fg.withAlpha(0.85),
        .indent_guide = fg.withAlpha(if (light) 0.12 else 0.08),
        .indent_guide_active = fg.withAlpha(0.25),
        .find_highlight = s.syntax.number.withAlpha(0.22),
        .find_highlight_border = s.syntax.number,
        .bracket_match = accent,
        .whitespace = fg.withAlpha(0.3),
        .rulers = fg.withAlpha(0.1),
        .syntax = s.syntax,
        .syntax_styles = s.styles,

        .window_background = shade(bg, light, 0.38, 0.10),
        .sidebar_background = shade(bg, light, 0.22, 0.05),
        .sidebar_foreground = Color.mix(fg, bg, 0.1),
        .sidebar_foreground_dim = Color.mix(fg, bg, 0.42),
        .sidebar_selected = raised,
        .sidebar_hover = shade(bg, light, 0.1, 0.08),
        .sidebar_heading = Color.mix(fg, bg, 0.5),
        .folder_icon = Color.mix(fg, bg, 0.4),
        .tabbar_background = shade(bg, light, 0.33, 0.12),
        .tab_inactive = shade(bg, light, 0.22, 0.07),
        .tab_inactive_foreground = Color.mix(fg, bg, 0.38),
        .tab_hover = shade(bg, light, 0.12, 0.04),
        .tab_active_foreground = fg,
        .tab_dirty = s.syntax.number,
        .tab_separator = shade(bg, light, 0.5, 0.2),
        .statusbar_background = shade(bg, light, 0.33, 0.1),
        .statusbar_foreground = Color.mix(fg, bg, 0.25),
        .panel_background = shade(bg, light, 0.22, 0.05),
        .panel_border = shade(bg, light, 0.5, 0.2),
        .overlay_background = if (light) bg.lighten(0.4) else bg.lighten(0.04),
        .overlay_shadow = Color.hexa(0x000000, if (light) 0.22 else 0.45),
        .input_background = if (light) bg.lighten(0.6) else bg.darken(0.33),
        .input_border = if (light) bg.darken(0.2) else bg.lighten(0.15),
        .input_border_focus = accent,
        .list_selected = raised,
        .list_foreground = fg,
        .list_foreground_dim = Color.mix(fg, bg, 0.4),
        .list_match = accent,
        .button_background = if (light) bg.darken(0.03) else bg,
        .button_active = if (light) bg.darken(0.18) else bg.lighten(0.2),
        .minimap_viewport = fg.withAlpha(0.06),
        .minimap_viewport_hover = fg.withAlpha(0.12),
        .scrollbar_puck = fg.withAlpha(0.18),
        .scrollbar_puck_hover = fg.withAlpha(0.32),
        .menubar_background = shade(bg, light, 0.38, 0.10),
        .menubar_foreground = Color.mix(fg, bg, 0.1),
        .menu_background = if (light) bg.lighten(0.4) else bg.lighten(0.04),
        .menu_selected = raised,
        .menu_separator = if (light) bg.darken(0.2) else bg.lighten(0.15),
        .menu_shortcut = Color.mix(fg, bg, 0.4),
    };
}

test "every scheme builds" {
    for (&schemes) |*s| {
        const t = build(s);
        try std.testing.expectEqualStrings(s.name, t.name);
    }
    try std.testing.expect(schemeByName("gruvbox dark") != null);
    try std.testing.expect(schemeByName("Mariana.sublime-color-scheme") != null);
}
