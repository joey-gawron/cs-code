//! The leader keys, as in Doom Emacs: after the leader (space in vim's
//! normal and visual modes, or the `leader` command, alt+m, anywhere) a
//! sequence of keys picks a command from a tree of groups ("+buffer",
//! "+file", …) that the which-key popup lists (workspace/leader_ui.zig).
//! The defaults are a comptime table; keymap.json adds, relabels and unbinds
//! entries (`"keys": ["leader", "b", "b"]`, see Keymap.loadUserFile).

const std = @import("std");
const keymap = @import("keymap.zig");
const platform = @import("../../framework/platform/platform.zig");

const Allocator = std.mem.Allocator;
const Action = keymap.Action;
const Chord = keymap.Chord;

/// The most keys a sequence takes after the leader.
pub const max_depth = 6;

/// A key after the leader: what a printable key types, so case and shifted
/// symbols count on any layout ('S', '<', ':'), else the chord (TAB, C-w).
pub const Key = union(enum) {
    text: u21,
    chord: Chord,

    pub fn fromEvent(k: platform.KeyEvent) Key {
        // Keys type text only without ctrl, alt and super.
        if (k.text.len > 0) {
            const n = std.unicode.utf8ByteSequenceLength(k.text[0]) catch 1;
            if (std.unicode.utf8Decode(k.text[0..@min(n, k.text.len)])) |cp| return .{ .text = cp } else |_| {}
        }
        return .{ .chord = .{ .key = k.key, .mods = k.mods } };
    }

    /// "b", "B", "<", "SPC", "TAB", "RET", or a chord such as "ctrl+w".
    pub fn parse(spec: []const u8) !Key {
        if (std.ascii.eqlIgnoreCase(spec, "spc") or std.ascii.eqlIgnoreCase(spec, "space")) return .{ .text = ' ' };
        if (std.ascii.eqlIgnoreCase(spec, "tab")) return .{ .chord = .{ .key = .{ .named = .tab }, .mods = .{} } };
        if (std.ascii.eqlIgnoreCase(spec, "ret")) return .{ .chord = .{ .key = .{ .named = .enter }, .mods = .{} } };
        const view = std.unicode.Utf8View.init(spec) catch return error.BadKey;
        var it = view.iterator();
        if (it.nextCodepoint()) |cp| if (it.nextCodepoint() == null) return .{ .text = cp };
        return .{ .chord = try Chord.parse(spec) };
    }

    pub fn eql(a: Key, b: Key) bool {
        return switch (a) {
            .text => |cp| b == .text and b.text == cp,
            .chord => |c| b == .chord and b.chord.eql(c),
        };
    }

    /// As which-key shows it: "b", "SPC", "TAB", "C-w".
    pub fn format(self: Key, buf: []u8) []const u8 {
        var w: std.Io.Writer = .fixed(buf);
        switch (self) {
            .text => |cp| writeChar(&w, cp),
            .chord => |c| {
                if (c.mods.ctrl) w.writeAll("C-") catch {};
                if (c.mods.alt) w.writeAll("M-") catch {};
                if (c.mods.super) w.writeAll("s-") catch {};
                if (c.mods.shift) w.writeAll("S-") catch {};
                switch (c.key) {
                    .char => |cp| writeChar(&w, cp),
                    .named => |n| w.writeAll(switch (n) {
                        .tab => "TAB",
                        .enter => "RET",
                        .escape => "ESC",
                        .backspace => "DEL",
                        inline else => |tag| "<" ++ @tagName(tag) ++ ">",
                    }) catch {},
                }
            },
        }
        return w.buffered();
    }

    /// which-key's order: by letter, lowercase first (b B c), then named
    /// keys and chords.
    fn order(self: Key) u64 {
        return switch (self) {
            .text => |cp| blk: {
                const ascii = cp < 128;
                const lower: u64 = if (ascii) std.ascii.toLower(@intCast(cp)) else cp;
                break :blk (lower << 1) | @intFromBool(ascii and std.ascii.isUpper(@intCast(cp)));
            },
            .chord => |c| blk: {
                const k: u64 = switch (c.key) {
                    .char => |cp| cp,
                    .named => |n| 0x110000 + @as(u64, @intFromEnum(n)),
                };
                break :blk (1 << 40) | (k << 8) | @as(u8, @bitCast(c.mods));
            },
        };
    }
};

fn writeChar(w: *std.Io.Writer, cp: u21) void {
    if (cp == ' ') return w.writeAll("SPC") catch {};
    var tmp: [4]u8 = undefined;
    const n = std.unicode.utf8Encode(cp, &tmp) catch return;
    w.writeAll(tmp[0..n]) catch {};
}

/// Keys typed after the leader.
pub const Seq = struct {
    keys: [max_depth]Key = undefined,
    len: u8 = 0,

    /// Space-separated keys: "w v", "m d s".
    pub fn parse(spec: []const u8) !Seq {
        var seq: Seq = .{};
        var it = std.mem.tokenizeScalar(u8, spec, ' ');
        while (it.next()) |part| if (!seq.push(try Key.parse(part))) return error.TooManyKeys;
        return seq;
    }

    pub fn slice(self: *const Seq) []const Key {
        return self.keys[0..self.len];
    }

    /// Adds a key; false when the sequence is full.
    pub fn push(self: *Seq, key: Key) bool {
        if (self.len == max_depth) return false;
        self.keys[self.len] = key;
        self.len += 1;
        return true;
    }

    pub fn eql(a: Seq, b: Seq) bool {
        return a.len == b.len and a.startsWith(b);
    }

    fn startsWith(self: Seq, prefix: Seq) bool {
        if (prefix.len > self.len) return false;
        for (prefix.slice(), self.keys[0..prefix.len]) |a, b| if (!a.eql(b)) return false;
        return true;
    }

    /// "SPC b b": the leader, then the keys.
    pub fn format(self: Seq, buf: []u8) []const u8 {
        var w: std.Io.Writer = .fixed(buf);
        w.writeAll("SPC") catch {};
        for (self.slice()) |k| {
            var tmp: [24]u8 = undefined;
            w.print(" {s}", .{k.format(&tmp)}) catch {};
        }
        return w.buffered();
    }
};

pub const Target = union(enum) {
    action: Action,
    /// A window command: vim's ctrl-w + this key (workspace/window_cmd.zig).
    window: u8,
    /// A prefix that more keys continue; its label names it ("+buffer").
    group,
    /// keymap.json's `"command": null`: hides a key, or a whole group.
    unbound,

    fn isCommand(self: Target) bool {
        return self == .action or self == .window;
    }
};

/// Where an entry applies (keymap.json's "context").
pub const When = enum { global, org };

/// Where the leader was started: decides which entries apply.
pub const Ctx = struct { org: bool = false };

pub const Entry = struct {
    seq: Seq,
    target: Target,
    label: []const u8 = "",
    when: When = .global,

    fn live(self: *const Entry, ctx: Ctx) bool {
        return self.when == .global or ctx.org;
    }
};

// ---- Defaults ---------------------------------------------------------------

const Row = struct { []const u8, Target, []const u8 };

fn cmd(a: Action) Target {
    return .{ .action = a };
}

fn win(ch: u8) Target {
    return .{ .window = ch };
}

/// Doom Emacs's SPC keys that have a command here, with its labels.
const leader_rows = [_]Row{
    .{ "SPC", cmd(.goto_anything), "Find file in project" },
    .{ ".", cmd(.open_file_here), "Find file" },
    .{ ",", cmd(.switch_buffer), "Switch workspace buffer" },
    .{ "<", cmd(.switch_buffer), "Switch buffer" },
    .{ "`", cmd(.next_tab_in_stack), "Switch to last buffer" },
    .{ ":", cmd(.command_palette), "M-x" },
    .{ ";", cmd(.run_command), "Run command" },
    .{ "/", cmd(.show_find_in_files), "Search project" },
    .{ "~", cmd(.toggle_panel), "Toggle panel" },
    .{ "x", cmd(.scratch_buffer), "Pop up scratch buffer" },
    .{ "X", cmd(.org_capture), "Org capture" },
    .{ "m", .group, "+localleader" },

    .{ "b", .group, "+buffer" },
    .{ "b [", cmd(.prev_tab), "Previous buffer" },
    .{ "b ]", cmd(.next_tab), "Next buffer" },
    .{ "b b", cmd(.switch_buffer), "Switch workspace buffer" },
    .{ "b B", cmd(.switch_buffer), "Switch buffer" },
    .{ "b c", cmd(.clone_file), "Clone buffer" },
    .{ "b d", cmd(.close_tab), "Kill buffer" },
    .{ "b k", cmd(.close_tab), "Kill buffer" },
    .{ "b K", cmd(.close_all), "Kill all buffers" },
    .{ "b l", cmd(.next_tab_in_stack), "Switch to last buffer" },
    .{ "b n", cmd(.next_tab), "Next buffer" },
    .{ "b N", cmd(.new_file), "New empty buffer" },
    .{ "b O", cmd(.close_other_tabs), "Kill other buffers" },
    .{ "b p", cmd(.prev_tab), "Previous buffer" },
    .{ "b r", cmd(.revert_file), "Revert buffer" },
    .{ "b s", cmd(.save), "Save buffer" },
    .{ "b S", cmd(.save_all), "Save all buffers" },
    .{ "b x", cmd(.scratch_buffer), "Pop up scratch buffer" },
    .{ "b y", cmd(.yank_buffer), "Yank buffer" },

    .{ "c", .group, "+code" },
    .{ "c c", cmd(.run_command), "Compile" },
    .{ "c C", cmd(.run_last_command), "Recompile" },
    .{ "c d", cmd(.lsp_goto_definition), "Jump to definition" },
    .{ "c D", cmd(.lsp_find_references), "Jump to references" },
    .{ "c f", cmd(.lsp_format), "Format buffer" },
    .{ "c i", cmd(.lsp_goto_implementation), "Find implementations" },
    .{ "c k", cmd(.lsp_hover), "Jump to documentation" },
    .{ "c r", cmd(.lsp_rename), "LSP rename" },
    .{ "c a", cmd(.lsp_code_actions), "LSP code actions" },
    .{ "c t", cmd(.lsp_goto_type_definition), "Find type definition" },
    .{ "c w", cmd(.trim_trailing_whitespace), "Delete trailing whitespace" },
    .{ "c x", cmd(.lsp_list_diagnostics), "List errors" },
    .{ "c l", .group, "+lsp" },
    .{ "c l R", cmd(.lsp_restart), "Restart servers" },
    .{ "c l s", cmd(.lsp_show_status), "Server status" },

    .{ "f", .group, "+file" },
    .{ "f D", cmd(.delete_file), "Delete this file" },
    .{ "f f", cmd(.open_file_here), "Find file" },
    .{ "f F", cmd(.open_file_here), "Find file from here" },
    .{ "f l", cmd(.goto_anything), "Locate file" },
    .{ "f p", cmd(.open_settings), "Open settings" },
    .{ "f P", cmd(.open_keymap), "Open key bindings" },
    .{ "f R", cmd(.rename_file), "Rename/move file" },
    .{ "f s", cmd(.save), "Save file" },
    .{ "f S", cmd(.save_as), "Save file as…" },
    .{ "f y", cmd(.copy_file_path), "Yank file path" },
    .{ "f Y", cmd(.copy_relative_path), "Yank file path from project" },

    .{ "g", .group, "+git" },
    .{ "g g", cmd(.git_status), "Git status" },
    .{ "g G", cmd(.git_status), "Git status" },
    .{ "g B", cmd(.git_toggle_blame), "Toggle blame" },
    .{ "g h", cmd(.git_show_commit), "Show commit for line" },
    .{ "g i", cmd(.git_toggle_inline_blame), "Toggle inline blame" },
    .{ "g y", cmd(.git_copy_commit_hash), "Copy commit hash" },

    .{ "h", .group, "+help" },
    .{ "h K", cmd(.open_keymap), "Key bindings" },
    .{ "h r", .group, "+reload" },
    .{ "h r r", cmd(.reload_config), "Reload settings and keys" },
    .{ "h t", cmd(.select_color_scheme), "Load theme" },
    .{ "h v", cmd(.open_settings), "Settings" },

    .{ "n", .group, "+notes" },
    .{ "n a", cmd(.org_agenda), "Org agenda" },
    .{ "n f", cmd(.org_find_notes), "Find file in notes" },
    .{ "n n", cmd(.org_capture), "Org capture" },
    .{ "n N", cmd(.org_goto_capture_file), "Goto capture" },
    .{ "n s", cmd(.org_search_notes), "Search notes" },
    .{ "n t", cmd(.org_todo_list), "Todo list" },

    .{ "o", .group, "+open" },
    .{ "o -", cmd(.reveal_in_sidebar), "Reveal in side bar" },
    .{ "o a", .group, "+org agenda" },
    .{ "o a a", cmd(.org_agenda), "Agenda" },
    .{ "o a d", cmd(.org_agenda_day), "Agenda for today" },
    .{ "o a t", cmd(.org_todo_list), "Todo list" },
    .{ "o a o", cmd(.org_agenda_dispatch), "Agenda dispatcher" },
    .{ "o a s", cmd(.org_agenda_search), "Search headings" },
    .{ "o a m", cmd(.org_agenda_tags), "Match tags" },
    .{ "o b", cmd(.open_in_browser), "Default browser" },
    .{ "o c", cmd(.org_capture), "Org capture" },
    .{ "o e", cmd(.new_panel_terminal), "New terminal in panel" },
    .{ "o E", cmd(.new_terminal_at_project_root), "Terminal at project root" },
    .{ "o p", cmd(.toggle_sidebar), "Project sidebar" },
    .{ "o P", cmd(.reveal_in_sidebar), "Find file in project sidebar" },
    .{ "o t", cmd(.toggle_panel_terminal), "Toggle terminal popup" },
    .{ "o T", cmd(.new_terminal), "Open terminal here" },

    .{ "p", .group, "+project" },
    .{ "p !", cmd(.run_command), "Run cmd in project root" },
    .{ "p c", cmd(.run_command), "Compile in project" },
    .{ "p C", cmd(.run_last_command), "Repeat last command" },
    .{ "p D", cmd(.reveal_in_sidebar), "Reveal in side bar" },
    .{ "p f", cmd(.goto_anything), "Find file in project" },
    .{ "p i", cmd(.rescan_project), "Invalidate project cache" },
    .{ "p s", cmd(.save_all), "Save project files" },
    .{ "p t", cmd(.project_todos), "List project todos" },

    .{ "q", .group, "+quit" },
    .{ "q f", cmd(.quit), "Close window" },
    .{ "q n", cmd(.session_new), "New session" },
    .{ "q r", cmd(.session_rename), "Rename session" },
    .{ "q s", cmd(.session_switch), "Switch session" },
    .{ "q x", cmd(.session_delete), "Delete session" },
    .{ "q K", cmd(.quit_session), "Quit background session" },
    .{ "q q", cmd(.quit), "Quit" },
    .{ "q Q", cmd(.quit_without_saving), "Quit without saving" },

    .{ "r", .group, "+remote" },
    .{ "r .", cmd(.remote_open_folder), "Open remote folder" },
    .{ "r c", cmd(.remote_connect), "Connect to host" },
    .{ "r k", cmd(.remote_close), "Close connection" },

    .{ "s", .group, "+search" },
    .{ "s b", cmd(.show_find), "Find in buffer" },
    .{ "s d", cmd(.search_directory), "Search current directory" },
    .{ "s i", cmd(.goto_symbol), "Jump to symbol" },
    .{ "s k", cmd(.lsp_hover), "Look up documentation" },
    .{ "s p", cmd(.show_find_in_files), "Search project" },
    .{ "s s", cmd(.show_incremental_find), "Search buffer" },
    .{ "s S", cmd(.find_under), "Search for thing at point" },

    .{ "t", .group, "+toggle" },
    .{ "t b", cmd(.toggle_big_font), "Big mode" },
    .{ "t F", cmd(.toggle_fullscreen), "Frame fullscreen" },
    .{ "t i", cmd(.toggle_indent_guides), "Indent guides" },
    .{ "t I", cmd(.toggle_translate_tabs), "Indent style" },
    .{ "t l", cmd(.toggle_line_numbers), "Line numbers" },
    .{ "t m", cmd(.toggle_minimap), "Minimap" },
    .{ "t r", cmd(.toggle_read_only), "Read-only mode" },
    .{ "t v", cmd(.toggle_whitespace), "Visible whitespace" },
    .{ "t w", cmd(.toggle_word_wrap), "Soft line wrapping" },
    .{ "t z", cmd(.toggle_distraction_free), "Zen mode" },

    .{ "w", .group, "+window" },
    .{ "w s", win('s'), "Split window" },
    .{ "w S", win('S'), "Split window" },
    .{ "w v", win('v'), "Split window vertically" },
    .{ "w n", win('n'), "New window" },
    .{ "w h", win('h'), "Window left" },
    .{ "w j", win('j'), "Window below" },
    .{ "w k", win('k'), "Window above" },
    .{ "w l", win('l'), "Window right" },
    .{ "w w", win('w'), "Next window" },
    .{ "w W", win('W'), "Previous window" },
    .{ "w p", win('p'), "Last window" },
    .{ "w t", win('t'), "Top-left window" },
    .{ "w b", win('b'), "Bottom-right window" },
    .{ "w c", win('c'), "Close window" },
    .{ "w d", win('c'), "Close window" },
    .{ "w q", win('q'), "Quit window" },
    .{ "w o", win('o'), "Only this window" },
    .{ "w T", win('T'), "Window to tab" },
    .{ "w =", win('='), "Balance windows" },
    .{ "w +", win('+'), "Taller" },
    .{ "w -", win('-'), "Shorter" },
    .{ "w >", win('>'), "Wider" },
    .{ "w <", win('<'), "Narrower" },
    .{ "w _", win('_'), "Maximize height" },
    .{ "w |", win('|'), "Maximize width" },
    .{ "w x", win('x'), "Exchange windows" },
    .{ "w r", win('r'), "Rotate windows" },
    .{ "w R", win('R'), "Rotate windows back" },
    .{ "w H", win('H'), "Move window far left" },
    .{ "w J", win('J'), "Move window to bottom" },
    .{ "w K", win('K'), "Move window to top" },
    .{ "w L", win('L'), "Move window far right" },
    .{ "w m", .group, "+maximize" },
    .{ "w m m", cmd(.zoom_group), "Maximize window" },
    .{ "w m s", win('|'), "Maximize horizontally" },
    .{ "w m v", win('_'), "Maximize vertically" },
};

/// SPC m in org files: Doom's localleader there.
const org_rows = [_]Row{
    .{ "m d", .group, "+date/deadline" },
    .{ "m d d", cmd(.org_deadline), "Deadline" },
    .{ "m d s", cmd(.org_schedule), "Schedule" },
    .{ "m d t", cmd(.org_timestamp), "Timestamp" },
    .{ "m d T", cmd(.org_timestamp_inactive), "Inactive timestamp" },
    .{ "m h", cmd(.org_insert_heading), "Insert heading" },
    .{ "m o", cmd(.org_open_link), "Open link" },
    .{ "m p", cmd(.org_priority_up), "Priority up" },
    .{ "m P", cmd(.org_priority_down), "Priority down" },
    .{ "m s", .group, "+tree/subtree" },
    .{ "m s h", cmd(.org_promote), "Promote heading" },
    .{ "m s l", cmd(.org_demote), "Demote heading" },
    .{ "m s H", cmd(.org_promote_subtree), "Promote subtree" },
    .{ "m s L", cmd(.org_demote_subtree), "Demote subtree" },
    .{ "m s j", cmd(.org_move_subtree_down), "Move subtree down" },
    .{ "m s k", cmd(.org_move_subtree_up), "Move subtree up" },
    .{ "m t", cmd(.org_cycle_todo), "Todo" },
    .{ "m T", cmd(.org_todo_list), "Todo list" },
    .{ "m x", cmd(.org_toggle_checkbox), "Toggle checkbox" },
    .{ "m TAB", cmd(.org_global_cycle), "Global visibility cycle" },
    .{ "m b", .group, "+table" },
    .{ "m c", .group, "+clock" },
    .{ "m e", .group, "+export" },
    .{ "m q", cmd(.org_set_tags), "Set tags" },
    .{ "m r", cmd(.org_refile), "Refile" },
    .{ "m A", cmd(.org_archive), "Archive subtree" },
    .{ "m c i", cmd(.org_clock_in), "Clock in" },
    .{ "m c o", cmd(.org_clock_out), "Clock out" },
    .{ "m c g", cmd(.org_clock_goto), "Go to clocked item" },
    .{ "m c r", cmd(.org_clock_report), "Insert clock report" },
    .{ "m '", cmd(.org_edit_src), "Edit source block" },
    .{ "m e h", cmd(.org_export_html), "Export to HTML" },
    .{ "m e m", cmd(.org_export_markdown), "Export to Markdown" },
    .{ "m b a", cmd(.org_table_align), "Align table" },
    .{ "m b r", cmd(.org_table_insert_row), "Insert row" },
    .{ "m b d", cmd(.org_table_delete_row), "Delete row" },
    .{ "m b c", cmd(.org_table_insert_column), "Insert column" },
    .{ "m b D", cmd(.org_table_delete_column), "Delete column" },
    .{ "m b -", cmd(.org_table_insert_hline), "Insert separator" },
};

/// Built at compile time: no heap and no parsing at startup.
const defaults = blk: {
    @setEvalBranchQuota(200_000);
    var out: [leader_rows.len + org_rows.len]Entry = undefined;
    for (leader_rows ++ org_rows, 0..) |r, i| out[i] = .{
        .seq = Seq.parse(r[0]) catch @compileError("bad leader keys: " ++ r[0]),
        .target = r[1],
        .label = r[2],
        .when = if (i < leader_rows.len) .global else .org,
    };
    break :blk out;
};

// ---- The tree -----------------------------------------------------------------

pub const Tree = struct {
    /// keymap.json's entries, searched before the defaults; their labels
    /// live in `arena`.
    user: std.ArrayList(Entry) = .empty,
    arena: std.heap.ArenaAllocator,

    pub fn init(gpa: Allocator) Tree {
        return .{ .arena = .init(gpa) };
    }

    pub fn deinit(self: *Tree, gpa: Allocator) void {
        self.user.deinit(gpa);
        self.arena.deinit();
    }

    /// Drops keymap.json's entries (before it is read again).
    pub fn clearUser(self: *Tree) void {
        self.user.clearRetainingCapacity();
        _ = self.arena.reset(.retain_capacity);
    }

    pub fn addUser(self: *Tree, gpa: Allocator, entry: Entry) !void {
        var e = entry;
        e.label = try self.arena.allocator().dupe(u8, entry.label);
        try self.user.append(gpa, e);
    }

    pub const Lookup = union(enum) {
        none,
        /// A group: more keys follow.
        prefix,
        command: Target,
    };

    /// What `seq` is: a command, a group with commands under it, or nothing.
    pub fn lookup(self: *const Tree, seq: Seq, ctx: Ctx) Lookup {
        if (!self.reachable(seq, ctx)) return .none;
        if (self.resolve(seq, ctx)) |e| {
            if (e.target.isCommand()) return .{ .command = e.target };
            if (e.target == .unbound) return .none;
        }
        return if (self.hasCommands(seq, ctx)) .prefix else .none;
    }

    pub const Child = struct {
        key: Key,
        /// A command, or a group.
        target: Target,
        /// "" for a keymap.json entry without one.
        label: []const u8,

        fn before(_: void, a: Child, b: Child) bool {
            return a.key.order() < b.key.order();
        }
    };

    /// The keys that can follow the group `prefix`, in which-key's order:
    /// commands, and groups with a command under them. At most `out.len`.
    pub fn children(self: *const Tree, prefix: Seq, ctx: Ctx, out: []Child) []Child {
        var n: usize = 0;
        for (self.lists()) |list| for (list) |*e| {
            if (n == out.len) break;
            if (e.seq.len <= prefix.len or !e.live(ctx) or !e.seq.startsWith(prefix)) continue;
            const key = e.seq.keys[prefix.len];
            const seen = for (out[0..n]) |c| {
                if (c.key.eql(key)) break true;
            } else false;
            if (seen) continue;
            var seq = prefix;
            _ = seq.push(key);
            const r = self.resolve(seq, ctx);
            const target: Target = if (r) |x| x.target else .group;
            switch (target) {
                .unbound => continue,
                .group => if (!self.hasCommands(seq, ctx)) continue,
                .action, .window => {},
            }
            out[n] = .{ .key = key, .target = target, .label = if (r) |x| x.label else "" };
            n += 1;
        };
        std.sort.pdq(Child, out[0..n], {}, Child.before);
        return out[0..n];
    }

    /// keymap.json's entries, then the defaults: the first live entry for a
    /// sequence decides what it is.
    fn lists(self: *const Tree) [2][]const Entry {
        return .{ self.user.items, &defaults };
    }

    fn resolve(self: *const Tree, seq: Seq, ctx: Ctx) ?*const Entry {
        for (self.lists()) |list| for (list) |*e| {
            if (e.live(ctx) and e.seq.eql(seq)) return e;
        };
        return null;
    }

    /// No shorter part of `seq` is a command, or unbound (which hides its group).
    fn reachable(self: *const Tree, seq: Seq, ctx: Ctx) bool {
        var p = seq;
        while (p.len > 1) {
            p.len -= 1;
            if (self.resolve(p, ctx)) |e| if (e.target != .group) return false;
        }
        return true;
    }

    /// A command is bound somewhere under the group `seq`.
    fn hasCommands(self: *const Tree, seq: Seq, ctx: Ctx) bool {
        for (self.lists()) |list| for (list) |*e| {
            if (!e.target.isCommand() or !e.live(ctx) or e.seq.len <= seq.len or !e.seq.startsWith(seq)) continue;
            if (self.resolve(e.seq, ctx) == e and self.reachable(e.seq, ctx)) return true;
        };
        return false;
    }
};

test "leader keys" {
    const expect = std.testing.expect;
    try expect((try Key.parse("SPC")).eql(.{ .text = ' ' }));
    try expect((try Key.parse("S")).eql(.{ .text = 'S' }));
    try expect((try Key.parse("TAB")).eql(.{ .chord = try Chord.parse("tab") }));
    try expect((try Key.parse("ctrl+w")).eql(.{ .chord = try Chord.parse("ctrl+w") }));
    // Printable keys match on what they type (shift+s is "S"), others on the chord.
    try expect(Key.fromEvent(.{ .key = .{ .char = 's' }, .mods = .{ .shift = true }, .text = "S" }).eql(.{ .text = 'S' }));
    try expect(Key.fromEvent(.{ .key = .{ .char = 'w' }, .mods = .{ .ctrl = true } }).eql(.{ .chord = try Chord.parse("ctrl+w") }));
    var buf: [32]u8 = undefined;
    try std.testing.expectEqualStrings("C-w", (try Key.parse("ctrl+w")).format(&buf));
    try std.testing.expectEqualStrings("SPC m TAB", (try Seq.parse("m TAB")).format(&buf));
    try std.testing.expectError(error.TooManyKeys, Seq.parse("a b c d e f g"));
}

test "default leader tree" {
    const gpa = std.testing.allocator;
    var tree: Tree = .init(gpa);
    defer tree.deinit(gpa);
    const expect = std.testing.expect;
    try expect(tree.lookup(try Seq.parse("f s"), .{}).command.action == .save);
    try expect(tree.lookup(try Seq.parse("<"), .{}).command.action == .switch_buffer);
    try expect(tree.lookup(try Seq.parse("w v"), .{}).command.window == 'v');
    try expect(tree.lookup(try Seq.parse("w m v"), .{}).command.window == '_');
    try expect(tree.lookup(try Seq.parse("w m m"), .{}).command.action == .zoom_group);
    try expect(tree.lookup(try Seq.parse("m s H"), .{ .org = true }).command.action == .org_promote_subtree);
    try expect(tree.lookup(try Seq.parse("f"), .{}) == .prefix);
    try expect(tree.lookup(try Seq.parse("f s x"), .{}) == .none);
    try expect(tree.lookup(try Seq.parse("z"), .{}) == .none);
    // The localleader is org's: SPC m is hidden elsewhere.
    try expect(tree.lookup(try Seq.parse("m t"), .{}) == .none);
    try expect(tree.lookup(try Seq.parse("m t"), .{ .org = true }).command.action == .org_cycle_todo);
    var out: [64]Tree.Child = undefined;
    const root = tree.children(.{}, .{}, &out);
    var x_at: ?usize = null;
    var big_x_at: ?usize = null;
    for (root, 0..) |c, i| {
        try expect(!c.key.eql(.{ .text = 'm' }));
        if (c.key.eql(.{ .text = 'x' })) x_at = i;
        if (c.key.eql(.{ .text = 'X' })) big_x_at = i;
    }
    try expect(x_at.? + 1 == big_x_at.?);
    const in_org = tree.children(.{}, .{ .org = true }, &out);
    var has_m = false;
    for (in_org) |c| if (c.key.eql(.{ .text = 'm' })) {
        has_m = c.target == .group;
    };
    try expect(has_m);
}

test "each default key is bound once" {
    for (defaults, 0..) |a, i| for (defaults[i + 1 ..]) |b| {
        try std.testing.expect(!(a.when == b.when and a.seq.eql(b.seq)));
    };
}

test "keymap.json leader entries" {
    const gpa = std.testing.allocator;
    var tree: Tree = .init(gpa);
    defer tree.deinit(gpa);
    const expect = std.testing.expect;
    try tree.addUser(gpa, .{ .seq = try Seq.parse("j"), .target = .group, .label = "+jump" });
    try tree.addUser(gpa, .{ .seq = try Seq.parse("j j"), .target = .{ .action = .goto_anything }, .label = "Jump" });
    try tree.addUser(gpa, .{ .seq = try Seq.parse("f s"), .target = .unbound });
    try tree.addUser(gpa, .{ .seq = try Seq.parse("g"), .target = .unbound });
    try tree.addUser(gpa, .{ .seq = try Seq.parse("b n"), .target = .{ .action = .next_tab }, .label = "Onward" });
    // A group with nothing in it isn't offered.
    try tree.addUser(gpa, .{ .seq = try Seq.parse("z"), .target = .group, .label = "+empty" });

    try expect(tree.lookup(try Seq.parse("j j"), .{}).command.action == .goto_anything);
    try expect(tree.lookup(try Seq.parse("f s"), .{}) == .none);
    try expect(tree.lookup(try Seq.parse("g g"), .{}) == .none);
    try expect(tree.lookup(try Seq.parse("z"), .{}) == .none);
    var out: [64]Tree.Child = undefined;
    for (tree.children(.{}, .{}, &out)) |c| {
        try expect(!c.key.eql(.{ .text = 'g' }) and !c.key.eql(.{ .text = 'z' }));
        if (c.key.eql(.{ .text = 'j' })) try std.testing.expectEqualStrings("+jump", c.label);
    }
    for (tree.children(try Seq.parse("f"), .{}, &out)) |c| try expect(!c.key.eql(.{ .text = 's' }));
    for (tree.children(try Seq.parse("b"), .{}, &out)) |c| if (c.key.eql(.{ .text = 'n' })) {
        try std.testing.expectEqualStrings("Onward", c.label);
    };
    tree.clearUser();
    try expect(tree.lookup(try Seq.parse("f s"), .{}).command.action == .save);
}
