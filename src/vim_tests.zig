//! Scripted vim tests: key sequences fed through the real event path of a
//! headless App (workspace, editor, vim hook) against an in-memory buffer.
//! Keys are written as text: plain characters are typed, `<Esc>`, `<CR>`,
//! `<BS>`, `<Tab>`, the arrows and `<C-x>` are named keys.

const std = @import("std");
const c = @import("c");
const platform = @import("framework/platform/platform.zig");
const app_mod = @import("app.zig");
const Buffer = @import("model/text/buffer.zig").Buffer;
const Editor = @import("app/editor/editor.zig").Editor;
const vim_mod = @import("app/vim/vim.zig");

const App = app_mod.App;
const testing = std.testing;

const Harness = struct {
    app: *App,
    ed: *Editor,

    fn init(initial: []const u8) !Harness {
        // Keep the user's settings, keymap and state out of the run.
        _ = c.setenv("XDG_CONFIG_HOME", "/tmp/cs-zig-vim-tests/config", 1);
        _ = c.setenv("HOME", "/tmp/cs-zig-vim-tests/home", 1);
        const gpa = testing.allocator;
        const app = try App.create(gpa, testing.io, .{ .headless_size = .{ 640, 400 }, .enable_lsp = false });
        errdefer app.destroy();
        const ws = &app.workspace;
        const buffer = try gpa.create(Buffer);
        buffer.* = try Buffer.initText(gpa, ws.next_buffer_id, initial);
        ws.next_buffer_id += 1;
        try ws.buffers.append(gpa, buffer);
        const ed = try Editor.create(gpa, ws.env, buffer);
        _ = try ws.insertTab(.{ .editor = ed });
        ws.perform(.toggle_vim_mode);
        app.render();
        return .{ .app = app, .ed = ed };
    }

    fn deinit(self: *Harness) void {
        self.app.destroy();
    }

    fn vim(self: *Harness) *vim_mod.Vim {
        return &self.app.vim;
    }

    fn press(self: *Harness, ev: platform.KeyEvent) void {
        self.app.handleEvent(.{ .key_down = ev });
        self.app.executor.drain();
        self.app.render();
    }

    fn keys(self: *Harness, script: []const u8) !void {
        var i: usize = 0;
        while (i < script.len) {
            if (script[i] == '<') if (std.mem.indexOfScalarPos(u8, script, i, '>')) |close| {
                const name = script[i + 1 .. close];
                if (name.len > 0) {
                    self.press(try namedKey(name));
                    i = close + 1;
                    continue;
                }
            };
            const len = try std.unicode.utf8ByteSequenceLength(script[i]);
            const cp = try std.unicode.utf8Decode(script[i .. i + len]);
            self.press(.{ .key = .{ .char = cp }, .mods = .{}, .text = script[i .. i + len] });
            i += len;
        }
    }

    fn namedKey(name: []const u8) !platform.KeyEvent {
        const eql = std.mem.eql;
        if (name.len == 3 and name[0] == 'C' and name[1] == '-') return .{ .key = .{ .char = name[2] }, .mods = .{ .ctrl = true } };
        const key: platform.NamedKey = if (eql(u8, name, "Esc")) .escape else if (eql(u8, name, "CR")) .enter else if (eql(u8, name, "BS")) .backspace else if (eql(u8, name, "Tab")) .tab else if (eql(u8, name, "Left")) .left else if (eql(u8, name, "Right")) .right else if (eql(u8, name, "Up")) .up else if (eql(u8, name, "Down")) .down else if (eql(u8, name, "Del")) .delete else return error.UnknownKey;
        return .{ .key = .{ .named = key }, .mods = .{} };
    }

    fn text(self: *Harness) ![]u8 {
        return self.ed.buffer.textRange(testing.allocator, 0, self.ed.buffer.len());
    }

    fn expectText(self: *Harness, expected: []const u8) !void {
        const actual = try self.text();
        defer testing.allocator.free(actual);
        try testing.expectEqualStrings(expected, actual);
    }

    fn expectMode(self: *Harness, mode: vim_mod.Mode) !void {
        try testing.expectEqual(mode, self.vim().mode);
    }

    /// The primary caret as (row, column in bytes).
    fn expectCaret(self: *Harness, row: u32, col: usize) !void {
        const head = self.ed.primarySelection().head;
        const p = self.ed.buffer.offsetToPoint(head);
        try testing.expectEqual(row, p.row);
        try testing.expectEqual(col, p.column);
    }

    /// Runs `script` on `text` and expects `expected`.
    fn expectEdit(initial: []const u8, script: []const u8, expected: []const u8) !void {
        var h = try Harness.init(initial);
        defer h.deinit();
        try h.keys(script);
        try h.expectText(expected);
    }

    fn register(self: *Harness, name: u8) ?[]const u8 {
        const r = self.vim().registers[name] orelse return null;
        return r.text;
    }
};

test "harness: typing in insert mode and leaving it" {
    var h = try Harness.init("");
    defer h.deinit();
    try h.expectMode(.normal);
    try h.keys("ihello<Esc>");
    try h.expectMode(.normal);
    try h.expectText("hello");
    try h.expectCaret(0, 4);
}

const sample =
    \\alpha beta gamma delta
    \\one two three four
    \\foo bar foo baz foo
    \\the quick brown fox
;

test "visual block: j extends the block over rows" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("<C-v>jjll");
    try h.expectMode(.visual_block);
    try testing.expectEqual(@as(usize, 3), h.ed.selections.items.len);
}

test "visual block: the block covers every row and column it spans" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("<C-v>jjll");
    try h.expectMode(.visual_block);
    try testing.expectEqual(@as(usize, 3), h.ed.selections.items.len);
    for (h.ed.selections.items) |sel| try testing.expectEqual(@as(usize, 3), sel.end() - sel.start());
}

test "visual: motions start at the cursor" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("vll");
    try testing.expectEqual(@as(usize, 2), h.vim().visual_cursors.items[0]);
    try h.keys("<Esc>");
    try h.expectMode(.normal);
    try h.keys("vwwb");
    try testing.expectEqual(@as(usize, 6), h.vim().visual_cursors.items[0]);
}

test "motions and counts" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("w");
    try h.expectCaret(0, 6);
    try h.keys("2w");
    try h.expectCaret(0, 17);
    try h.keys("$");
    try h.expectCaret(0, 21);
    try h.keys("0");
    try h.expectCaret(0, 0);
    try h.keys("2j");
    try h.expectCaret(2, 0);
    try h.keys("G");
    try h.expectCaret(3, 0);
    try h.keys("gg");
    try h.expectCaret(0, 0);
    try h.keys("fg");
    try h.expectCaret(0, 11);
    try h.keys("tl;");
    try h.expectCaret(0, 18);
    try h.keys("e");
    try h.expectCaret(0, 21);
    try h.keys("3b");
    try h.expectCaret(0, 6);
    try h.keys("3G");
    try h.expectCaret(2, 0);
    try h.keys("^");
    try h.expectCaret(2, 0);
}

test "operators with motions" {
    try Harness.expectEdit("one two three\n", "dw", "two three\n");
    try Harness.expectEdit("one two three\n", "2dw", "three\n");
    try Harness.expectEdit("one two three\n", "wD", "one \n");
    try Harness.expectEdit("one two three\n", "cwuno<Esc>", "uno two three\n");
    try Harness.expectEdit("one two three\n", "wdb", "two three\n");
    try Harness.expectEdit("one two three\n", "d$", "\n");
    try Harness.expectEdit("a\nb\nc\n", "dd", "b\nc\n");
    try Harness.expectEdit("a\nb\nc\n", "2dd", "c\n");
    try Harness.expectEdit("a\nb\nc\n", "dj", "c\n");
    try Harness.expectEdit("a\nb\nc\n", "jdk", "c\n");
    try Harness.expectEdit("a\nb\nc\n", "dG", "");
    try Harness.expectEdit("abc\n", "x", "bc\n");
    try Harness.expectEdit("abc\n", "3x", "\n");
    try Harness.expectEdit("abc\n", "$X", "ac\n");
    try Harness.expectEdit("abc\n", "rz", "zbc\n");
    try Harness.expectEdit("abc def\n", "~~~", "ABC def\n");
    try Harness.expectEdit("abc def\n", "g~w", "ABC def\n");
    try Harness.expectEdit("abc def\n", "gUiw", "ABC def\n");
    try Harness.expectEdit("ABC def\n", "guiw", "abc def\n");
    try Harness.expectEdit("a\nb\n", "J", "a b\n");
    try Harness.expectEdit("a\nb\n", ">>", "    a\nb\n");
    try Harness.expectEdit("a b\n", "sX<Esc>", "X b\n");
    try Harness.expectEdit("a b\n", "SX<Esc>", "X\n");
    try Harness.expectEdit("a b\n", "Cx<Esc>", "x\n");
}

test "text objects" {
    try Harness.expectEdit("one two three\n", "wdiw", "one  three\n");
    try Harness.expectEdit("one two three\n", "wdaw", "one three\n");
    try Harness.expectEdit("f(a, b) g\n", "fadi(", "f() g\n");
    try Harness.expectEdit("f(a, b) g\n", "fada(", "f g\n");
    try Harness.expectEdit("say \"hi there\" now\n", "fhdi\"", "say \"\" now\n");
    try Harness.expectEdit("say \"hi there\" now\n", "fhda\"", "say now\n");
    try Harness.expectEdit("x = {a: [1, 2]}\n", "f1di[", "x = {a: []}\n");
    try Harness.expectEdit("x = {a: [1, 2]}\n", "f1di{", "x = {}\n");
    try Harness.expectEdit("a\n\nb\nc\n\nd\n", "jjdip", "a\n\n\nd\n");
    try Harness.expectEdit("one two three\n", "wciwTWO<Esc>", "one TWO three\n");
    try Harness.expectEdit("one two three\n", "wyiwP", "one twotwo three\n");
}

test "dot repeat" {
    try Harness.expectEdit("a b c d\n", "dw.", "c d\n");
    try Harness.expectEdit("a b c d\n", "dw2.", "d\n");
    try Harness.expectEdit("a a a\n", "x.", "a a\n");
    try Harness.expectEdit("one\ntwo\nthree\n", "A!<Esc>j.j.", "one!\ntwo!\nthree!\n");
    try Harness.expectEdit("x x x x\n", "cwy<Esc>w.w.", "y y y x\n");
    try Harness.expectEdit("a\nb\nc\n", "ddj.", "b\n");
}

test "registers, yank and put" {
    try Harness.expectEdit("one two\n", "yiwwP", "one onetwo\n");
    try Harness.expectEdit("one two\n", "yywp", "one two\none two\n");
    try Harness.expectEdit("one two\n", "yyP", "one two\none two\n");
    try Harness.expectEdit("one two\n", "\"ayiww\"ap", "one tonewo\n");
    try Harness.expectEdit("a b\n", "dwP", "a b\n");
    try Harness.expectEdit("a b\n", "\"_dwP", "b\n");
    try Harness.expectEdit("ab\n", "xp", "ba\n");
    try Harness.expectEdit("a\nb\n", "ddp", "b\na\n");
    try Harness.expectEdit("a\nb\n", "yyjp2p", "a\nb\na\na\na\n");
    var h = try Harness.init("one two\n");
    defer h.deinit();
    try h.keys("\"qyw");
    try testing.expectEqualStrings("one ", h.register('q').?);
    try h.keys("\"Qyw");
    try testing.expectEqualStrings("one one ", h.register('q').?);
}

test "undo and redo" {
    try Harness.expectEdit("abc\n", "xxu", "bc\n");
    try Harness.expectEdit("abc\n", "xxuu", "abc\n");
    try Harness.expectEdit("abc\n", "xxuu<C-r>", "bc\n");
    try Harness.expectEdit("abc\n", "ixy<Esc>u", "abc\n");
    try Harness.expectEdit("a b c\n", "dwdwu", "b c\n");
}

test "insert and replace modes" {
    try Harness.expectEdit("abc\n", "ix<Esc>", "xabc\n");
    try Harness.expectEdit("abc\n", "ax<Esc>", "axbc\n");
    try Harness.expectEdit("abc\n", "Ax<Esc>", "abcx\n");
    try Harness.expectEdit("  abc\n", "Ix<Esc>", "  xabc\n");
    try Harness.expectEdit("abc\n", "ox<Esc>", "abc\nx\n");
    try Harness.expectEdit("abc\n", "Ox<Esc>", "x\nabc\n");
    try Harness.expectEdit("abc\n", "Rxy<Esc>", "xyc\n");
    try Harness.expectEdit("abc\n", "3ix<Esc>", "xxxabc\n");
    try Harness.expectEdit("abc\n", "ix<BS><BS>y<Esc>", "yabc\n");
    try Harness.expectEdit("abc\n", "a<CR>b<Esc>", "a\nbbc\n");
}

test "deleting the last line" {
    try Harness.expectEdit("a\nb\nc\n", "jjdd", "a\nb\n");
    try Harness.expectEdit("a\nb\nc", "jjdd", "a\nb");
    try Harness.expectEdit("a\nb\nc\n", "jdd", "a\nc\n");
    try Harness.expectEdit("a\nb\nc\n", "jjdk", "a\n");
}
