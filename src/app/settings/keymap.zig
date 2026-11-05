//! Actions and the Sublime Text (Linux) default key bindings, including
//! two-chord sequences such as ctrl+k ctrl+b, plus the leader keys' tree
//! (leader.zig) that keymap.json also changes.

const std = @import("std");
const builtin = @import("builtin");
const settings_file = @import("settings_file.zig");
const leader = @import("leader.zig");
const platform = @import("../../framework/platform/platform.zig");

pub const Action = enum {
    // Movement (and their selection-extending twins).
    move_left,
    move_right,
    move_up,
    move_down,
    move_word_left,
    move_word_right,
    move_subword_left,
    move_subword_right,
    move_line_start,
    move_line_end,
    move_page_up,
    move_page_down,
    move_doc_start,
    move_doc_end,
    select_left,
    select_right,
    select_up,
    select_down,
    select_word_left,
    select_word_right,
    select_subword_left,
    select_subword_right,
    select_line_start,
    select_line_end,
    select_page_up,
    select_page_down,
    select_doc_start,
    select_doc_end,
    scroll_line_up,
    scroll_line_down,

    // Selection.
    select_all,
    expand_selection_line,
    find_under_expand,
    find_under_expand_skip,
    find_all_under,
    find_under,
    split_selection_into_lines,
    add_cursor_above,
    add_cursor_below,
    single_selection,
    expand_selection_brackets,
    expand_selection_indentation,
    jump_to_bracket,

    // Editing.
    insert_newline,
    insert_tab,
    backspace,
    delete_forward,
    delete_word_backward,
    delete_word_forward,
    delete_to_line_start,
    delete_to_line_end,
    delete_line,
    indent,
    unindent,
    toggle_comment,
    toggle_block_comment,
    duplicate_line,
    swap_line_up,
    swap_line_down,
    join_lines,
    insert_line_after,
    insert_line_before,
    upper_case,
    lower_case,
    title_case,
    swap_case,
    sort_lines,
    sort_lines_case_sensitive,
    reverse_lines,
    unique_lines,
    trim_trailing_whitespace,
    convert_indentation_to_spaces,
    convert_indentation_to_tabs,
    undo,
    redo,
    soft_undo,
    soft_redo,
    copy,
    cut,
    paste,
    paste_and_indent,

    // Find.
    show_find,
    show_replace,
    show_incremental_find,
    find_next,
    find_prev,
    replace_next,
    replace_all,
    find_all,
    toggle_find_regex,
    toggle_find_case,
    toggle_find_whole_word,
    toggle_find_preserve_case,
    use_selection_for_find,

    // Overlays.
    goto_anything,
    goto_line,
    goto_symbol,
    goto_word,
    command_palette,

    // Files and tabs.
    new_file,
    open_file,
    open_file_here,
    open_folder,
    new_project,
    show_welcome,
    open_recent,
    close_folder,
    add_folder_to_workspace,
    clone_repository,
    save,
    save_as,
    save_all,
    revert_file,
    close_tab,
    close_all,
    close_other_tabs,
    reopen_closed,
    switch_buffer,
    scratch_buffer,
    yank_buffer,
    copy_file_path,
    copy_relative_path,
    rename_file,
    delete_file,
    open_in_browser,
    install_shell_command,
    next_tab,
    prev_tab,
    next_tab_in_stack,
    prev_tab_in_stack,
    tab_1,
    tab_2,
    tab_3,
    tab_4,
    tab_5,
    tab_6,
    tab_7,
    tab_8,
    tab_9,
    reveal_in_sidebar,
    clone_file,

    // Split panes (Sublime's View > Layout / Groups).
    layout_single,
    layout_columns_2,
    layout_columns_3,
    layout_columns_4,
    layout_rows_2,
    layout_rows_3,
    layout_grid_4,
    focus_group_1,
    focus_group_2,
    focus_group_3,
    focus_group_4,
    move_to_group_1,
    move_to_group_2,
    move_to_group_3,
    move_to_group_4,
    new_pane,
    close_pane,
    next_group,
    prev_group,
    focus_group_left,
    focus_group_right,
    focus_group_up,
    focus_group_down,
    move_to_next_group,
    move_to_prev_group,
    zoom_group,

    // Find in files, and the project.
    show_find_in_files,
    show_replace_in_files,
    next_result,
    prev_result,
    search_directory,
    project_todos,
    rescan_project,

    // View.
    toggle_sidebar,
    toggle_minimap,
    toggle_status_bar,
    toggle_menu,
    toggle_tabs,
    toggle_word_wrap,
    toggle_line_numbers,
    toggle_indent_guides,
    toggle_whitespace,
    increase_font,
    decrease_font,
    reset_font,
    toggle_big_font,
    toggle_read_only,
    toggle_fullscreen,
    toggle_distraction_free,
    set_tab_size_2,
    set_tab_size_4,
    set_tab_size_8,
    toggle_translate_tabs,

    // Folding.
    fold,
    unfold,
    unfold_all,
    fold_level_1,
    fold_level_2,
    fold_level_3,
    fold_level_4,

    // Language servers.
    lsp_goto_definition,
    lsp_goto_implementation,
    lsp_goto_type_definition,
    lsp_find_references,
    lsp_rename,
    lsp_code_actions,
    lsp_format,
    lsp_hover,
    lsp_signature_help,
    lsp_next_diagnostic,
    lsp_prev_diagnostic,
    lsp_next_error,
    lsp_prev_error,
    lsp_list_diagnostics,
    lsp_complete,
    lsp_restart,
    lsp_show_status,
    lsp_clear_log,

    // Git.
    git_toggle_blame,
    git_toggle_inline_blame,
    git_show_commit,
    git_copy_commit_hash,
    git_status,
    scm_show,
    scm_graph,
    scm_timeline,

    // Org mode.
    org_agenda,
    org_agenda_day,
    org_todo_list,
    org_capture,
    org_cycle_todo,
    org_schedule,
    org_deadline,
    org_timestamp,
    org_timestamp_inactive,
    org_toggle_checkbox,
    org_global_cycle,
    org_open_link,
    org_insert_heading,
    org_priority_up,
    org_priority_down,
    org_promote,
    org_demote,
    org_promote_subtree,
    org_demote_subtree,
    org_move_subtree_up,
    org_move_subtree_down,
    org_goto_capture_file,
    org_find_notes,
    org_search_notes,
    org_set_tags,
    org_refile,
    org_archive,
    org_clock_in,
    org_clock_out,
    org_clock_goto,
    org_clock_report,
    org_edit_src,
    org_export_html,
    org_export_markdown,
    org_table_align,
    org_table_insert_row,
    org_table_delete_row,
    org_table_insert_column,
    org_table_delete_column,
    org_table_insert_hline,
    org_agenda_dispatch,
    org_agenda_search,
    org_agenda_tags,

    // Debugging (debug_ui.zig).
    debug_start,
    debug_stop,
    debug_restart,
    debug_step_over,
    debug_step_into,
    debug_step_out,
    debug_pause,
    debug_toggle_breakpoint,
    debug_remove_all_breakpoints,
    debug_add_watch,
    debug_evaluate,
    debug_show_panel,

    // The panel under the editor (Terminal, Language Servers).
    toggle_panel,
    toggle_panel_terminal,
    new_panel_terminal,
    run_command,
    run_last_command,

    // Terminal.
    new_terminal,
    new_terminal_at_project_root,
    toggle_terminal,
    rename_terminal,

    // Preferences.
    select_color_scheme,
    open_settings,
    open_settings_json,
    open_keyboard_shortcuts,
    open_keymap,
    reload_config,

    // Session.
    quit_session,
    session_switch,
    session_new,
    session_rename,
    session_delete,

    // Remote-SSH.
    remote_connect,
    remote_open_folder,
    remote_close,

    // Macros and modes.
    toggle_record_macro,
    run_macro,
    toggle_vim_mode,

    // The leader keys (leader.zig): alt+m, and space in vim.
    leader,
    show_leader_keys,

    // Other.
    cancel,
    quit,
    quit_without_saving,
};

pub const Chord = struct {
    key: platform.Key,
    mods: platform.Modifiers,

    pub fn eql(a: Chord, b: Chord) bool {
        return a.key.eql(b.key) and a.mods.eql(b.mods);
    }

    pub fn parse(spec: []const u8) !Chord {
        var mods: platform.Modifiers = .{};
        var it = std.mem.splitScalar(u8, spec, '+');
        var last: []const u8 = "";
        while (it.next()) |part| {
            // "ctrl++" style: an empty part after a '+' means the plus key.
            const p = if (part.len == 0) "+" else part;
            if (it.peek() == null) {
                last = p;
                break;
            }
            if (std.mem.eql(u8, p, "ctrl")) mods.ctrl = true else if (std.mem.eql(u8, p, "shift")) mods.shift = true else if (std.mem.eql(u8, p, "alt")) mods.alt = true else if (std.mem.eql(u8, p, "super")) mods.super = true else return error.UnknownModifier;
        }
        return .{ .key = try parseKey(last), .mods = mods };
    }

    pub fn format(self: Chord, buf: []u8) []const u8 {
        var w: std.Io.Writer = .fixed(buf);
        if (builtin.os.tag.isDarwin()) {
            // Command runs the ctrl bindings there, so menus name it.
            if (self.mods.ctrl or self.mods.super) w.writeAll("Cmd+") catch {};
            if (self.mods.alt) w.writeAll("Option+") catch {};
            if (self.mods.shift) w.writeAll("Shift+") catch {};
        } else {
            if (self.mods.ctrl) w.writeAll("Ctrl+") catch {};
            if (self.mods.alt) w.writeAll("Alt+") catch {};
            if (self.mods.shift) w.writeAll("Shift+") catch {};
            if (self.mods.super) w.writeAll("Super+") catch {};
        }
        switch (self.key) {
            .char => |ch| {
                if (ch == ' ') w.writeAll("Space") catch {} else {
                    var tmp: [4]u8 = undefined;
                    const n = std.unicode.utf8Encode(if (ch < 128) std.ascii.toUpper(@intCast(ch)) else ch, &tmp) catch 0;
                    w.writeAll(tmp[0..n]) catch {};
                }
            },
            .named => |n| {
                const name = switch (n) {
                    .enter => "Enter",
                    .tab => "Tab",
                    .backspace => "Backspace",
                    .delete => "Delete",
                    .escape => "Esc",
                    .left => "Left",
                    .right => "Right",
                    .up => "Up",
                    .down => "Down",
                    .home => "Home",
                    .end => "End",
                    .page_up => "PgUp",
                    .page_down => "PgDn",
                    .insert => "Insert",
                    .context_menu => "Menu",
                    inline else => |tag| blk: {
                        const s = @tagName(tag);
                        break :blk if (s[0] == 'f') "F" ++ s[1..] else s;
                    },
                };
                w.writeAll(name) catch {};
            },
        }
        return w.buffered();
    }
};

fn parseKey(name: []const u8) !platform.Key {
    const named = std.StaticStringMap(platform.NamedKey).initComptime(.{
        .{ "enter", .enter },        .{ "tab", .tab },       .{ "backspace", .backspace },
        .{ "delete", .delete },      .{ "escape", .escape }, .{ "left", .left },
        .{ "right", .right },        .{ "up", .up },         .{ "down", .down },
        .{ "home", .home },          .{ "end", .end },       .{ "pageup", .page_up },
        .{ "pagedown", .page_down }, .{ "insert", .insert }, .{ "f1", .f1 },
        .{ "f2", .f2 },              .{ "f3", .f3 },         .{ "f4", .f4 },
        .{ "f5", .f5 },              .{ "f6", .f6 },         .{ "f7", .f7 },
        .{ "f8", .f8 },              .{ "f9", .f9 },         .{ "f10", .f10 },
        .{ "f11", .f11 },            .{ "f12", .f12 },       .{ "menu", .context_menu },
    });
    if (named.get(name)) |n| return .{ .named = n };
    if (std.mem.eql(u8, name, "space")) return .{ .char = ' ' };
    if (std.mem.eql(u8, name, "plus")) return .{ .char = '+' };
    if (std.mem.eql(u8, name, "minus")) return .{ .char = '-' };
    const view = std.unicode.Utf8View.init(name) catch return error.BadKey;
    var it = view.iterator();
    const cp = it.nextCodepoint() orelse return error.BadKey;
    if (it.nextCodepoint() != null) return error.BadKey;
    return .{ .char = if (cp < 128) std.ascii.toLower(@intCast(cp)) else cp };
}

pub const Context = enum {
    /// Applies everywhere.
    global,
    /// Only while a text editor (or text input) has focus.
    editor,
};

pub const Binding = struct {
    chords: [2]?Chord,
    /// null: the user removed this key (it no longer triggers anything).
    action: ?Action,
    context: Context = .editor,
};

const DefaultBinding = struct { []const u8, Action, Context };

const defaults = [_]DefaultBinding{
    .{ "left", .move_left, .editor },
    .{ "right", .move_right, .editor },
    .{ "up", .move_up, .editor },
    .{ "down", .move_down, .editor },
    .{ "ctrl+left", .move_word_left, .editor },
    .{ "ctrl+right", .move_word_right, .editor },
    .{ "alt+left", .move_subword_left, .editor },
    .{ "alt+right", .move_subword_right, .editor },
    .{ "home", .move_line_start, .editor },
    .{ "end", .move_line_end, .editor },
    .{ "pageup", .move_page_up, .editor },
    .{ "pagedown", .move_page_down, .editor },
    .{ "ctrl+home", .move_doc_start, .editor },
    .{ "ctrl+end", .move_doc_end, .editor },
    .{ "shift+left", .select_left, .editor },
    .{ "shift+right", .select_right, .editor },
    .{ "shift+up", .select_up, .editor },
    .{ "shift+down", .select_down, .editor },
    .{ "ctrl+shift+left", .select_word_left, .editor },
    .{ "ctrl+shift+right", .select_word_right, .editor },
    .{ "alt+shift+left", .select_subword_left, .editor },
    .{ "alt+shift+right", .select_subword_right, .editor },
    .{ "shift+home", .select_line_start, .editor },
    .{ "shift+end", .select_line_end, .editor },
    .{ "shift+pageup", .select_page_up, .editor },
    .{ "shift+pagedown", .select_page_down, .editor },
    .{ "ctrl+shift+home", .select_doc_start, .editor },
    .{ "ctrl+shift+end", .select_doc_end, .editor },
    .{ "ctrl+up", .scroll_line_up, .editor },
    .{ "ctrl+down", .scroll_line_down, .editor },

    .{ "ctrl+a", .select_all, .editor },
    .{ "ctrl+l", .expand_selection_line, .editor },
    .{ "ctrl+d", .find_under_expand, .editor },
    .{ "ctrl+k ctrl+d", .find_under_expand_skip, .editor },
    .{ "alt+f3", .find_all_under, .editor },
    .{ "ctrl+f3", .find_under, .editor },
    .{ "ctrl+shift+l", .split_selection_into_lines, .editor },
    .{ "alt+shift+up", .add_cursor_above, .editor },
    .{ "alt+shift+down", .add_cursor_below, .editor },
    .{ "ctrl+alt+up", .add_cursor_above, .editor },
    .{ "ctrl+alt+down", .add_cursor_below, .editor },
    .{ "escape", .cancel, .global },
    .{ "ctrl+shift+m", .expand_selection_brackets, .editor },
    .{ "ctrl+shift+j", .expand_selection_indentation, .editor },
    .{ "ctrl+m", .jump_to_bracket, .editor },

    .{ "enter", .insert_newline, .editor },
    .{ "shift+enter", .insert_newline, .editor },
    .{ "tab", .insert_tab, .editor },
    .{ "shift+tab", .unindent, .editor },
    .{ "backspace", .backspace, .editor },
    .{ "shift+backspace", .backspace, .editor },
    .{ "delete", .delete_forward, .editor },
    .{ "ctrl+backspace", .delete_word_backward, .editor },
    .{ "ctrl+delete", .delete_word_forward, .editor },
    .{ "ctrl+shift+backspace", .delete_to_line_start, .editor },
    .{ "ctrl+shift+delete", .delete_to_line_end, .editor },
    .{ "ctrl+k ctrl+k", .delete_to_line_end, .editor },
    .{ "ctrl+k ctrl+backspace", .delete_to_line_start, .editor },
    .{ "ctrl+shift+k", .delete_line, .editor },
    .{ "ctrl+]", .indent, .editor },
    .{ "ctrl+[", .unindent, .editor },
    .{ "ctrl+/", .toggle_comment, .editor },
    .{ "ctrl+shift+/", .toggle_block_comment, .editor },
    .{ "ctrl+shift+d", .duplicate_line, .editor },
    .{ "ctrl+shift+up", .swap_line_up, .editor },
    .{ "ctrl+shift+down", .swap_line_down, .editor },
    .{ "ctrl+j", .join_lines, .editor },
    .{ "ctrl+enter", .insert_line_after, .editor },
    .{ "ctrl+shift+enter", .insert_line_before, .editor },
    .{ "ctrl+k ctrl+u", .upper_case, .editor },
    .{ "ctrl+k ctrl+l", .lower_case, .editor },
    .{ "f9", .debug_toggle_breakpoint, .editor },
    .{ "ctrl+f9", .sort_lines_case_sensitive, .editor },
    .{ "ctrl+z", .undo, .editor },
    .{ "ctrl+shift+z", .redo, .editor },
    .{ "ctrl+y", .redo, .editor },
    .{ "ctrl+u", .soft_undo, .editor },
    .{ "ctrl+shift+u", .soft_redo, .editor },
    .{ "ctrl+c", .copy, .editor },
    .{ "ctrl+insert", .copy, .editor },
    .{ "ctrl+x", .cut, .editor },
    .{ "shift+delete", .cut, .editor },
    .{ "ctrl+v", .paste, .editor },
    .{ "shift+insert", .paste, .editor },
    .{ "ctrl+shift+v", .paste_and_indent, .editor },

    .{ "ctrl+f", .show_find, .global },
    .{ "ctrl+h", .show_replace, .global },
    .{ "ctrl+i", .show_incremental_find, .global },
    .{ "f3", .find_next, .global },
    .{ "shift+f3", .find_prev, .global },
    .{ "ctrl+e", .use_selection_for_find, .editor },
    .{ "ctrl+alt+enter", .replace_all, .global },
    .{ "alt+r", .toggle_find_regex, .global },
    .{ "alt+c", .toggle_find_case, .global },
    .{ "alt+w", .toggle_find_whole_word, .global },
    .{ "alt+p", .toggle_find_preserve_case, .global },

    .{ "ctrl+p", .goto_anything, .global },
    .{ "ctrl+g", .goto_line, .global },
    .{ "ctrl+r", .goto_symbol, .global },
    .{ "ctrl+;", .goto_word, .global },
    .{ "ctrl+shift+p", .command_palette, .global },

    .{ "ctrl+n", .new_file, .global },
    .{ "ctrl+o", .open_file, .global },
    .{ "ctrl+k ctrl+o", .open_folder, .global },
    .{ "ctrl+s", .save, .global },
    .{ "ctrl+shift+s", .save_as, .global },
    .{ "ctrl+w", .close_tab, .global },
    .{ "ctrl+f4", .close_tab, .global },
    .{ "ctrl+shift+t", .reopen_closed, .global },
    .{ "ctrl+pagedown", .next_tab, .global },
    .{ "ctrl+pageup", .prev_tab, .global },
    .{ "ctrl+tab", .next_tab_in_stack, .global },
    .{ "ctrl+shift+tab", .prev_tab_in_stack, .global },
    .{ "alt+1", .tab_1, .global },
    .{ "alt+2", .tab_2, .global },
    .{ "alt+3", .tab_3, .global },
    .{ "alt+4", .tab_4, .global },
    .{ "alt+5", .tab_5, .global },
    .{ "alt+6", .tab_6, .global },
    .{ "alt+7", .tab_7, .global },
    .{ "alt+8", .tab_8, .global },
    .{ "alt+9", .tab_9, .global },
    .{ "alt+shift+1", .layout_single, .global },
    .{ "alt+shift+2", .layout_columns_2, .global },
    .{ "alt+shift+3", .layout_columns_3, .global },
    .{ "alt+shift+4", .layout_columns_4, .global },
    .{ "alt+shift+8", .layout_rows_2, .global },
    .{ "alt+shift+9", .layout_rows_3, .global },
    .{ "alt+shift+5", .layout_grid_4, .global },
    .{ "ctrl+1", .focus_group_1, .global },
    .{ "ctrl+2", .focus_group_2, .global },
    .{ "ctrl+3", .focus_group_3, .global },
    .{ "ctrl+4", .focus_group_4, .global },
    .{ "ctrl+shift+1", .move_to_group_1, .global },
    .{ "ctrl+shift+2", .move_to_group_2, .global },
    .{ "ctrl+shift+3", .move_to_group_3, .global },
    .{ "ctrl+shift+4", .move_to_group_4, .global },
    .{ "ctrl+k ctrl+up", .new_pane, .global },
    .{ "ctrl+k ctrl+down", .close_pane, .global },
    .{ "ctrl+k ctrl+right", .next_group, .global },
    .{ "ctrl+k ctrl+left", .prev_group, .global },
    .{ "ctrl+k ctrl+shift+right", .move_to_next_group, .global },
    .{ "ctrl+k ctrl+shift+left", .move_to_prev_group, .global },
    .{ "ctrl+k left", .focus_group_left, .global },
    .{ "ctrl+k right", .focus_group_right, .global },
    .{ "ctrl+k up", .focus_group_up, .global },
    .{ "ctrl+k down", .focus_group_down, .global },
    .{ "ctrl+shift+f", .show_find_in_files, .global },
    .{ "ctrl+shift+h", .show_replace_in_files, .global },
    .{ "f4", .next_result, .global },
    .{ "shift+f4", .prev_result, .global },

    .{ "ctrl+shift+[", .fold, .editor },
    .{ "ctrl+shift+]", .unfold, .editor },
    .{ "ctrl+k ctrl+j", .unfold_all, .editor },
    .{ "ctrl+k ctrl+0", .unfold_all, .editor },
    .{ "ctrl+k ctrl+1", .fold_level_1, .editor },
    .{ "ctrl+k ctrl+2", .fold_level_2, .editor },
    .{ "ctrl+k ctrl+3", .fold_level_3, .editor },
    .{ "ctrl+k ctrl+4", .fold_level_4, .editor },
    .{ "f12", .lsp_goto_definition, .editor },
    .{ "shift+f12", .lsp_find_references, .editor },
    .{ "f2", .lsp_rename, .editor },
    .{ "ctrl+.", .lsp_code_actions, .editor },
    .{ "alt+shift+f", .lsp_format, .editor },
    .{ "ctrl+k ctrl+i", .lsp_hover, .editor },
    .{ "ctrl+shift+space", .lsp_signature_help, .editor },
    .{ "f8", .lsp_next_diagnostic, .editor },
    .{ "shift+f8", .lsp_prev_diagnostic, .editor },
    .{ "ctrl+space", .lsp_complete, .editor },
    .{ "ctrl+k ctrl+g", .git_toggle_blame, .editor },
    .{ "ctrl+shift+g", .scm_show, .global },
    .{ "ctrl+k ctrl+b", .toggle_sidebar, .global },
    .{ "ctrl+shift+y", .toggle_panel, .global },
    .{ "ctrl+=", .increase_font, .global },
    .{ "ctrl+shift+=", .increase_font, .global },
    .{ "ctrl+-", .decrease_font, .global },
    .{ "ctrl+0", .reset_font, .global },
    .{ "ctrl+alt+f", .toggle_fullscreen, .global },
    .{ "ctrl+alt+shift+f", .toggle_distraction_free, .global },
    .{ "f5", .debug_start, .global },
    .{ "shift+f5", .debug_stop, .global },
    .{ "ctrl+shift+f5", .debug_restart, .global },
    .{ "f10", .debug_step_over, .global },
    .{ "f11", .debug_step_into, .global },
    .{ "shift+f11", .debug_step_out, .global },
    .{ "f6", .debug_pause, .global },
    .{ "ctrl+shift+f9", .debug_remove_all_breakpoints, .global },
    .{ "ctrl+alt+w", .debug_add_watch, .global },
    .{ "ctrl+q", .toggle_record_macro, .global },
    .{ "ctrl+shift+q", .run_macro, .global },
    .{ "ctrl+alt+t", .new_terminal, .global },
    .{ "ctrl+alt+shift+t", .new_terminal_at_project_root, .global },
    // As in VS Code: terminals in the panel under the editors.
    .{ "ctrl+`", .toggle_panel_terminal, .global },
    .{ "ctrl+shift+`", .new_panel_terminal, .global },
    // As Sublime's build keys: run a command line in the panel.
    .{ "ctrl+shift+b", .run_command, .global },
    .{ "ctrl+b", .run_last_command, .global },
    // Doom Emacs's M-SPC (alt+space is often the desktop's) and M-x.
    .{ "alt+m", .leader, .global },
    .{ "alt+x", .command_palette, .global },
};

pub const Keymap = struct {
    /// User bindings first (they win), then the defaults.
    bindings: std.ArrayList(Binding) = .empty,
    user_count: usize = 0,
    /// First chord of an in-progress sequence (e.g. after ctrl+k).
    pending: ?Chord = null,
    /// Problems found in keymap.json (shown in the status bar).
    warnings: std.ArrayList(u8) = .empty,
    /// The keys after the leader, with keymap.json's changes.
    leader: leader.Tree,

    pub fn initDefault(gpa: std.mem.Allocator) !Keymap {
        var self: Keymap = .{ .leader = .init(gpa) };
        try self.bindings.ensureTotalCapacity(gpa, defaults.len);
        for (defaults) |d| {
            var chords: [2]?Chord = .{ null, null };
            var it = std.mem.tokenizeScalar(u8, d[0], ' ');
            var i: usize = 0;
            while (it.next()) |part| : (i += 1) chords[i] = try Chord.parse(part);
            self.bindings.appendAssumeCapacity(.{ .chords = chords, .action = d[1], .context = d[2] });
        }
        return self;
    }

    pub fn deinit(self: *Keymap, gpa: std.mem.Allocator) void {
        self.bindings.deinit(gpa);
        self.warnings.deinit(gpa);
        self.leader.deinit(gpa);
    }

    pub fn userPath(buf: []u8) ?[]const u8 {
        if (std.c.getenv("XDG_CONFIG_HOME")) |x| {
            return std.fmt.bufPrint(buf, "{s}/cs-zig/keymap.json", .{std.mem.span(x)}) catch null;
        }
        const home = std.c.getenv("HOME") orelse return null;
        return std.fmt.bufPrint(buf, "{s}/.config/cs-zig/keymap.json", .{std.mem.span(home)}) catch null;
    }

    /// The context an action is bound in by default (user bindings inherit it).
    fn defaultContext(action: Action) Context {
        for (defaults) |d| if (d[1] == action) return d[2];
        return .global;
    }

    /// (Re)loads keymap.json: [{"keys": ["ctrl+k", "ctrl+t"], "command": "toggle_sidebar"}, ...]
    pub fn loadUserFile(self: *Keymap, gpa: std.mem.Allocator, io: std.Io) !void {
        // Drop the previous user bindings.
        self.bindings.replaceRangeAssumeCapacity(0, self.user_count, &.{});
        self.user_count = 0;
        self.leader.clearUser();
        self.warnings.clearRetainingCapacity();
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const path = userPath(&buf) orelse return;
        const raw = std.Io.Dir.cwd().readFileAlloc(io, path, gpa, .limited(1 << 20)) catch |err| switch (err) {
            error.FileNotFound => return,
            else => return err,
        };
        defer gpa.free(raw);
        try self.addUserEntries(gpa, raw);
    }

    /// keymap.json's entries, ahead of the defaults.
    pub fn addUserEntries(self: *Keymap, gpa: std.mem.Allocator, raw: []const u8) !void {
        const strict = try settings_file.toStrictJson(gpa, raw);
        defer gpa.free(strict);
        var parsed = try std.json.parseFromSlice(std.json.Value, gpa, strict, .{});
        defer parsed.deinit();
        if (parsed.value != .array) return error.KeymapNotAnArray;

        var user: std.ArrayList(Binding) = .empty;
        defer user.deinit(gpa);
        for (parsed.value.array.items, 0..) |entry, i| {
            if (entry != .object) continue;
            const keys = entry.object.get("keys") orelse {
                self.warn(gpa, "entry {d}: missing \"keys\"", .{i + 1});
                continue;
            };
            if (keys == .array and keys.array.items.len > 0 and keys.array.items[0] == .string) {
                const first = keys.array.items[0].string;
                if (std.mem.eql(u8, first, "leader") or std.mem.eql(u8, first, "localleader")) {
                    try self.addLeaderEntry(gpa, i + 1, entry.object, keys.array.items);
                    continue;
                }
            }
            if (keys != .array or keys.array.items.len == 0 or keys.array.items.len > 2) {
                self.warn(gpa, "entry {d}: \"keys\" must list one or two chords", .{i + 1});
                continue;
            }
            var chords: [2]?Chord = .{ null, null };
            var ok = true;
            for (keys.array.items, 0..) |k, j| {
                if (k != .string) {
                    ok = false;
                    break;
                }
                chords[j] = Chord.parse(k.string) catch {
                    self.warn(gpa, "entry {d}: can't parse key \"{s}\"", .{ i + 1, k.string });
                    ok = false;
                    break;
                };
            }
            if (!ok) continue;
            var action: ?Action = null;
            if (entry.object.get("command")) |cmd| switch (cmd) {
                .null => {},
                .string => |name| {
                    action = std.meta.stringToEnum(Action, name) orelse {
                        self.warn(gpa, "entry {d}: unknown command \"{s}\"", .{ i + 1, name });
                        continue;
                    };
                },
                else => continue,
            };
            var context: Context = if (action) |a| defaultContext(a) else .global;
            if (entry.object.get("context")) |ctx| if (ctx == .string) {
                context = std.meta.stringToEnum(Context, ctx.string) orelse context;
            };
            try user.append(gpa, .{ .chords = chords, .action = action, .context = context });
        }
        try self.bindings.insertSlice(gpa, 0, user.items);
        self.user_count = user.items.len;
    }

    /// An entry of the leader tree: `"keys": ["leader", "b", "b"]` (keys
    /// after "localleader" follow `leader m`, in org files) with a "command"
    /// (null unbinds; "window" with `"args": {"key": "v"}` is ctrl-w v), or a
    /// "group" naming a prefix; optional "label" and "context" ("global",
    /// "org"). `n` numbers the entry in warnings.
    fn addLeaderEntry(self: *Keymap, gpa: std.mem.Allocator, n: usize, obj: std.json.ObjectMap, keys: []const std.json.Value) !void {
        const local = std.mem.eql(u8, keys[0].string, "localleader");
        var e: leader.Entry = .{ .seq = .{}, .target = .group, .when = if (local) .org else .global };
        if (local) _ = e.seq.push(.{ .text = 'm' });
        for (keys[1..]) |k| {
            if (k != .string) return self.warn(gpa, "entry {d}: keys must be strings", .{n});
            const key = leader.Key.parse(k.string) catch return self.warn(gpa, "entry {d}: can't parse key \"{s}\"", .{ n, k.string });
            if (!e.seq.push(key)) return self.warn(gpa, "entry {d}: more than {d} keys after the leader", .{ n, leader.max_depth });
        }
        if (keys.len == 1) return self.warn(gpa, "entry {d}: no keys after \"{s}\"", .{ n, keys[0].string });
        if (obj.get("group")) |g| {
            if (g == .string) e.label = g.string;
        } else if (obj.get("command")) |cmd| {
            e.target = switch (cmd) {
                .null => .unbound,
                .string => |name| if (std.mem.eql(u8, name, "window")) .{
                    .window = windowKey(obj) orelse return self.warn(gpa, "entry {d}: \"window\" needs \"args\": {{\"key\": \"v\"}}", .{n}),
                } else .{
                    .action = std.meta.stringToEnum(Action, name) orelse return self.warn(gpa, "entry {d}: unknown command \"{s}\"", .{ n, name }),
                },
                else => return self.warn(gpa, "entry {d}: \"command\" must be a name or null", .{n}),
            };
        } else return self.warn(gpa, "entry {d}: needs a \"command\" or a \"group\"", .{n});
        if (obj.get("label")) |l| if (l == .string) {
            e.label = l.string;
        };
        if (obj.get("context")) |c| {
            const when = if (c == .string) std.meta.stringToEnum(leader.When, c.string) else null;
            e.when = when orelse return self.warn(gpa, "entry {d}: a leader key's \"context\" is \"global\" or \"org\"", .{n});
        }
        try self.leader.addUser(gpa, e);
    }

    /// The ctrl-w key in `"args": {"key": "v"}`.
    fn windowKey(obj: std.json.ObjectMap) ?u8 {
        const args = obj.get("args") orelse return null;
        if (args != .object) return null;
        const key = args.object.get("key") orelse return null;
        return if (key == .string and key.string.len == 1) key.string[0] else null;
    }

    fn warn(self: *Keymap, gpa: std.mem.Allocator, comptime fmt: []const u8, args: anytype) void {
        if (self.warnings.items.len > 0) self.warnings.appendSlice(gpa, "; ") catch {};
        self.warnings.print(gpa, fmt, args) catch {};
    }

    pub const Result = union(enum) {
        none,
        /// The chord started a sequence; swallow it and wait for the next.
        pending,
        action: Action,
    };

    pub fn match(self: *Keymap, chord: Chord, in_editor: bool) Result {
        // macOS: Command plays Ctrl's part in Sublime's Mac bindings, so an
        // unbound super+X runs ctrl+X. Ctrl itself stays Ctrl (vim, terminals).
        if (builtin.os.tag.isDarwin() and chord.mods.super and !chord.mods.ctrl) {
            const pending = self.pending;
            const exact = self.matchExact(chord, in_editor);
            if (exact != .none) return exact;
            self.pending = if (pending) |p| commandAsCtrl(p) else null;
            return self.matchExact(commandAsCtrl(chord), in_editor);
        }
        return self.matchExact(chord, in_editor);
    }

    fn commandAsCtrl(chord: Chord) Chord {
        var out = chord;
        if (out.mods.super and !out.mods.ctrl) {
            out.mods.super = false;
            out.mods.ctrl = true;
        }
        return out;
    }

    fn matchExact(self: *Keymap, chord: Chord, in_editor: bool) Result {
        if (self.pending) |first| {
            self.pending = null;
            for (self.bindings.items) |b| {
                const c0 = b.chords[0] orelse continue;
                const c1 = b.chords[1] orelse continue;
                if (b.context == .editor and !in_editor) continue;
                if (c0.eql(first) and c1.eql(chord)) return if (b.action) |a| .{ .action = a } else .none;
            }
            return .none;
        }
        const result = self.peek(chord, in_editor);
        if (result == .pending) self.pending = chord;
        return result;
    }

    /// What `chord` does as a first key, leaving `pending` alone (`.pending`:
    /// it starts a sequence).
    pub fn peek(self: *const Keymap, chord: Chord, in_editor: bool) Result {
        var starts_sequence = false;
        for (self.bindings.items) |b| {
            const c0 = b.chords[0] orelse continue;
            if (b.context == .editor and !in_editor) continue;
            if (!c0.eql(chord)) continue;
            if (b.chords[1] == null) {
                // An unbound single key also cancels a sequence it would start.
                if (starts_sequence) break;
                return if (b.action) |a| .{ .action = a } else .none;
            }
            if (b.action != null) starts_sequence = true;
        }
        return if (starts_sequence) .pending else .none;
    }

    /// True if an earlier binding (a user override) takes over `b`'s keys.
    fn shadowed(self: *const Keymap, index: usize) bool {
        const b = self.bindings.items[index];
        for (self.bindings.items[0..index]) |other| {
            const a0 = other.chords[0] orelse continue;
            const b0 = b.chords[0] orelse continue;
            if (!a0.eql(b0)) continue;
            const same_second = if (other.chords[1]) |a1| (if (b.chords[1]) |b1| a1.eql(b1) else false) else b.chords[1] == null;
            if (same_second) return true;
        }
        return false;
    }

    /// Display string of the first live binding for `action` (for menus / palette).
    pub fn shortcutFor(self: *const Keymap, action: Action, buf: []u8) ?[]const u8 {
        for (self.bindings.items, 0..) |b, i| {
            if (b.action != action) continue;
            if (self.shadowed(i)) continue;
            const c0 = b.chords[0] orelse continue;
            var w: std.Io.Writer = .fixed(buf);
            var tmp: [32]u8 = undefined;
            w.writeAll(c0.format(&tmp)) catch {};
            if (b.chords[1]) |c1| {
                w.writeAll(", ") catch {};
                w.writeAll(c1.format(&tmp)) catch {};
            }
            return w.buffered();
        }
        return null;
    }

    /// A commented template listing every command, for "Preferences: Key Bindings".
    pub fn template(gpa: std.mem.Allocator) ![]u8 {
        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(gpa);
        try out.appendSlice(gpa,
            \\// cs-zig key bindings. Entries here win over the defaults; save to apply.
            \\//
            \\//   { "keys": ["ctrl+k", "ctrl+t"], "command": "toggle_sidebar" }
            \\//   { "keys": ["ctrl+q"], "command": null }        // remove a default
            \\//   { "keys": ["f5"], "command": "sort_lines", "context": "editor" }
            \\//   { "keys": ["alt+l"], "command": "leader" }     // another leader key
            \\//
            \\// "context" is "editor" (only while text has focus) or "global"; it
            \\// defaults to the command's usual context. Commands are listed below.
            \\//
            \\// The leader keys (Doom Emacs's SPC menus: space in vim, alt+m) take
            \\// longer sequences after "leader", or "localleader" (leader m, in org files):
            \\//
            \\//   { "keys": ["leader", "j"], "group": "+jump" }
            \\//   { "keys": ["leader", "j", "j"], "command": "goto_anything", "label": "Find file" }
            \\//   { "keys": ["leader", "w", "V"], "command": "window", "args": { "key": "v" } }
            \\//   { "keys": ["leader", "x"], "command": null }   // remove a key, or a group
            \\//   { "keys": ["localleader", "p"], "command": "org_cycle_todo" }
            \\//
            \\// Keys are what they type ("b", "B", "<"), "SPC", "TAB", "RET" or chords
            \\// ("ctrl+w"). "label" is what the which-key popup shows; "window" runs
            \\// a vim window command (ctrl-w + "key"); "context": "org" keeps a key
            \\// to org files.
            \\[
            \\]
            \\
            \\// Commands:
            \\
        );
        var col: usize = 0;
        inline for (@typeInfo(Action).@"enum".field_names) |name| {
            if (col == 0) try out.appendSlice(gpa, "//  ");
            try out.print(gpa, " {s}", .{name});
            col += name.len + 1;
            if (col > 70) {
                try out.append(gpa, '\n');
                col = 0;
            }
        }
        try out.append(gpa, '\n');
        return out.toOwnedSlice(gpa);
    }
};

test "parse chords" {
    const c1 = try Chord.parse("ctrl+shift+p");
    try std.testing.expect(c1.mods.ctrl and c1.mods.shift and !c1.mods.alt);
    try std.testing.expect(c1.key.eql(.{ .char = 'p' }));
    const c2 = try Chord.parse("ctrl+=");
    try std.testing.expect(c2.key.eql(.{ .char = '=' }));
    const c3 = try Chord.parse("alt+shift+up");
    try std.testing.expect(c3.key.eql(.{ .named = .up }));
}

test "user bindings override and unbind" {
    const gpa = std.testing.allocator;
    var km = try Keymap.initDefault(gpa);
    defer km.deinit(gpa);
    try km.bindings.insert(gpa, 0, .{ .chords = .{ try Chord.parse("ctrl+q"), null }, .action = null, .context = .global });
    try km.bindings.insert(gpa, 0, .{ .chords = .{ try Chord.parse("f5"), null }, .action = .sort_lines, .context = .editor });
    km.user_count = 2;
    try std.testing.expect(km.match(try Chord.parse("ctrl+q"), true) == .none);
    try std.testing.expectEqual(Action.sort_lines, km.match(try Chord.parse("f5"), true).action);
    var buf: [64]u8 = undefined;
    try std.testing.expect(km.shortcutFor(.toggle_record_macro, &buf) == null);
    const t = try Keymap.template(gpa);
    defer gpa.free(t);
    try std.testing.expect(std.mem.indexOf(u8, t, "toggle_sidebar") != null);
}

test "leader entries in keymap.json" {
    const gpa = std.testing.allocator;
    var km = try Keymap.initDefault(gpa);
    defer km.deinit(gpa);
    try km.addUserEntries(gpa,
        \\[
        \\  { "keys": ["leader", "j"], "group": "+jump" },
        \\  { "keys": ["leader", "j", "j"], "command": "goto_anything", "label": "Find file" },
        \\  { "keys": ["leader", "w", "V"], "command": "window", "args": { "key": "v" } },
        \\  { "keys": ["leader", "f", "s"], "command": null },
        \\  { "keys": ["localleader", "p"], "command": "org_cycle_todo" },
        \\  { "keys": ["leader", "q", "x"], "command": "no_such_command" },
        \\  { "keys": ["alt+l"], "command": "leader" },
        \\]
    );
    const tree = &km.leader;
    const expect = std.testing.expect;
    try expect(tree.lookup(try leader.Seq.parse("j j"), .{}).command.action == .goto_anything);
    try expect(tree.lookup(try leader.Seq.parse("w V"), .{}).command.window == 'v');
    try expect(tree.lookup(try leader.Seq.parse("f s"), .{}) == .none);
    try expect(tree.lookup(try leader.Seq.parse("m p"), .{}) == .none);
    try expect(tree.lookup(try leader.Seq.parse("m p"), .{ .org = true }).command.action == .org_cycle_todo);
    try expect(std.mem.indexOf(u8, km.warnings.items, "entry 6: unknown command") != null);
    try std.testing.expectEqual(Action.leader, km.peek(try Chord.parse("alt+l"), false).action);
    try std.testing.expectEqual(Action.leader, km.peek(try Chord.parse("alt+m"), false).action);
}
