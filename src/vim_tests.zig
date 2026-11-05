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
                if (namedKey(name)) |ev| {
                    self.press(ev);
                    i = close + 1;
                    continue;
                } else |_| {}
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

    var failures: usize = 0;

    fn escaped(buf: []u8, s: []const u8) []const u8 {
        var n: usize = 0;
        for (s) |ch| {
            if (n + 2 > buf.len) break;
            if (ch == '\n') {
                buf[n] = '\\';
                buf[n + 1] = 'n';
                n += 2;
            } else {
                buf[n] = ch;
                n += 1;
            }
        }
        return buf[0..n];
    }

    /// Runs `script` on `initial` and expects `expected`. A mismatch is
    /// printed and counted, so one run lists every case; `finish` fails the test.
    fn expectEdit(initial: []const u8, script: []const u8, expected: []const u8) !void {
        var h = try Harness.init(initial);
        defer h.deinit();
        var a: [512]u8 = undefined;
        var b: [512]u8 = undefined;
        var c2: [512]u8 = undefined;
        var d: [512]u8 = undefined;
        h.keys(script) catch |err| {
            std.debug.print("FAIL keys {s}: {t}\n", .{ script, err });
            failures += 1;
            return;
        };
        const actual = try h.text();
        defer testing.allocator.free(actual);
        if (!std.mem.eql(u8, expected, actual)) {
            std.debug.print("FAIL {s} on [{s}]: expected [{s}] found [{s}]\n", .{ escaped(&a, script), escaped(&b, initial), escaped(&c2, expected), escaped(&d, actual) });
            failures += 1;
        }
    }

    fn finish() !void {
        const n = failures;
        failures = 0;
        try testing.expectEqual(@as(usize, 0), n);
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
    try Harness.finish();
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
    try Harness.finish();
}

test "dot repeat" {
    try Harness.expectEdit("a b c d\n", "dw.", "c d\n");
    try Harness.expectEdit("a b c d\n", "dw2.", "d\n");
    try Harness.expectEdit("a a a\n", "x.", "a a\n");
    try Harness.expectEdit("one\ntwo\nthree\n", "A!<Esc>j.j.", "one!\ntwo!\nthree!\n");
    try Harness.expectEdit("x x x x\n", "cwy<Esc>w.w.", "y y y x\n");
    try Harness.expectEdit("a\nb\nc\n", "ddj.", "b\n");
    try Harness.finish();
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
    try Harness.finish();
}

test "undo and redo" {
    try Harness.expectEdit("abc\n", "xxu", "bc\n");
    try Harness.expectEdit("abc\n", "xxuu", "abc\n");
    try Harness.expectEdit("abc\n", "xxuu<C-r>", "bc\n");
    try Harness.expectEdit("abc\n", "ixy<Esc>u", "abc\n");
    try Harness.expectEdit("a b c\n", "dwdwu", "b c\n");
    try Harness.finish();
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
    try Harness.finish();
}

test "deleting the last line" {
    try Harness.expectEdit("a\nb\nc\n", "jjdd", "a\nb\n");
    try Harness.expectEdit("a\nb\nc", "jjdd", "a\nb");
    try Harness.expectEdit("a\nb\nc\n", "jdd", "a\nc\n");
    try Harness.expectEdit("a\nb\nc\n", "jjdk", "a\n");
    try Harness.finish();
}

test "visual mode operators" {
    try Harness.expectEdit("abcdef\n", "vlld", "def\n");
    try Harness.expectEdit("abcdef\n", "vlly$p", "abcdefabc\n");
    try Harness.expectEdit("a\nb\nc\n", "Vjd", "c\n");
    try Harness.expectEdit("abc\ndef\nghi\n", "l<C-v>jjd", "ac\ndf\ngi\n");
    try Harness.expectEdit("abc\ndef\nghi\n", "l<C-v>jjIX<Esc>", "aXbc\ndXef\ngXhi\n");
    try Harness.expectEdit("abc\ndef\nghi\n", "l<C-v>jjAX<Esc>", "abXc\ndeXf\nghXi\n");
    try Harness.expectEdit("abc def\n", "viwU", "ABC def\n");
    try Harness.expectEdit("abc def\n", "vee~", "ABC DEF\n");
    try Harness.expectEdit("abc def\n", "veyP", "abcabc def\n");
    try Harness.expectEdit("abc def\n", "vlrx", "xxc def\n");
    try Harness.expectEdit("a\nb\nc\n", "VjJ", "a b\nc\n");
    try Harness.expectEdit("a\nb\n", "Vj>", "    a\n    b\n");
    try Harness.expectEdit("abc def\n", "wviwo<Esc>x", "abc ef\n");
    try Harness.expectEdit("one two\n", "wviwcX<Esc>", "one X\n");
    var h = try Harness.init("abc def ghi\n");
    defer h.deinit();
    try h.keys("vee<Esc>");
    try h.expectMode(.normal);
    try h.keys("gv");
    try h.expectMode(.visual);
    try Harness.finish();
}

test "search" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("/foo<CR>");
    try h.expectCaret(2, 0);
    try h.keys("n");
    try h.expectCaret(2, 8);
    try h.keys("n");
    try h.expectCaret(2, 16);
    try h.keys("N");
    try h.expectCaret(2, 8);
    try h.keys("?alpha<CR>");
    try h.expectCaret(0, 0);
    try h.keys("jj*");
    try h.expectCaret(2, 8);
    try h.keys("#");
    try h.expectCaret(2, 0);
}

test "search with an operator" {
    try Harness.expectEdit("one two three\n", "d/three<CR>", "three\n");
    try Harness.expectEdit("a b a c\n", "c/a<CR>x<Esc>", "xa c\n");
    try Harness.finish();
}

test "substitute" {
    try Harness.expectEdit("foo foo\nfoo\n", ":s/foo/bar/<CR>", "bar foo\nfoo\n");
    try Harness.expectEdit("foo foo\nfoo\n", ":s/foo/bar/g<CR>", "bar bar\nfoo\n");
    try Harness.expectEdit("foo foo\nfoo\n", ":%s/foo/bar/g<CR>", "bar bar\nbar\n");
    try Harness.expectEdit("foo foo\nfoo\nfoo\n", ":2,3s/foo/x/<CR>", "foo foo\nx\nx\n");
    try Harness.expectEdit("a1 b22\n", ":s/\\d\\+/N/g<CR>", "aN bN\n");
    try Harness.expectEdit("john smith\n", ":s/\\(\\w\\+\\) \\(\\w\\+\\)/\\2 \\1/<CR>", "smith john\n");
    try Harness.expectEdit("a/b\n", ":s#/#-#<CR>", "a-b\n");
    try Harness.expectEdit("foo\nfoo\n", "Vj:s/o/0/g<CR>", "f00\nf00\n");
    try Harness.expectEdit("foo foo\nfoo\n", ":s/foo/bar/<CR>j&", "bar foo\nbar\n");
    try Harness.finish();
}

test "ex commands" {
    try Harness.expectEdit("a\nb\nc\nd\n", ":2d<CR>", "a\nc\nd\n");
    try Harness.expectEdit("a\nb\nc\nd\n", ":2,3d<CR>", "a\nd\n");
    try Harness.expectEdit("a\nb\nc\nd\n", ":$d<CR>", "a\nb\nc\n");
    try Harness.expectEdit("a\nb\nc\nd\n", ":2y<CR>:$put<CR>", "a\nb\nc\nd\nb\n");
    try Harness.expectEdit("c\nb\na\n", ":sort<CR>", "a\nb\nc\n");
    try Harness.expectEdit("a\nb\nc\n", ":1,2j<CR>", "a b\nc\n");
    try Harness.expectEdit("a\nb\n", ":%><CR>", "    a\n    b\n");
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys(":3<CR>");
    try h.expectCaret(2, 0);
    try h.keys(":$<CR>");
    try h.expectCaret(3, 0);
    try h.keys(":1<CR>");
    try h.expectCaret(0, 0);
    try h.keys(":noh<CR>");
    try h.expectMode(.normal);
    try Harness.finish();
}

test "marks and the jump list" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("wmajjmb");
    try h.expectCaret(2, 6);
    try h.keys("'a");
    try h.expectCaret(0, 0);
    try h.keys("`a");
    try h.expectCaret(0, 6);
    try h.keys("`b");
    try h.expectCaret(2, 6);
    try h.keys("gg");
    try h.expectCaret(0, 0);
    try h.keys("G");
    try h.expectCaret(3, 0);
    try h.keys("<C-o>");
    try h.expectCaret(0, 0);
    try h.keys("<C-i>");
    try h.expectCaret(3, 0);
}

test "macros" {
    try Harness.expectEdit("a\nb\nc\n", "qqA!<Esc>jq@q@@", "a!\nb!\nc!\n");
    try Harness.expectEdit("1\n2\n3\n4\n", "qqI-<Esc>jq3@q", "-1\n-2\n-3\n-4\n");
    var h = try Harness.init("abc\n");
    defer h.deinit();
    try h.keys("qa");
    var buf: [64]u8 = undefined;
    const hook = h.app.workspace.key_hook.?;
    try testing.expect(std.mem.indexOf(u8, hook.statusText(hook.ctx, &buf), "recording @a") != null);
    try h.keys("xq");
    try testing.expect(std.mem.indexOf(u8, hook.statusText(hook.ctx, &buf), "recording") == null);
    try Harness.finish();
}

test "mode indicator, pending keys and caret shape" {
    var h = try Harness.init(sample);
    defer h.deinit();
    var buf: [128]u8 = undefined;
    const hook = h.app.workspace.key_hook.?;
    try testing.expectEqualStrings("NORMAL", hook.statusText(hook.ctx, &buf));
    try testing.expect(h.ed.block_caret);
    try h.keys("2d");
    try testing.expectEqualStrings("NORMAL  2d", hook.statusText(hook.ctx, &buf));
    try h.keys("<Esc>i");
    try testing.expectEqualStrings("-- INSERT --", hook.statusText(hook.ctx, &buf));
    try testing.expect(!h.ed.block_caret);
    try h.keys("<Esc>v");
    try testing.expectEqualStrings("-- VISUAL --", hook.statusText(hook.ctx, &buf));
    try h.keys("V");
    try testing.expectEqualStrings("-- VISUAL LINE --", hook.statusText(hook.ctx, &buf));
    try h.keys("<C-v>");
    try testing.expectEqualStrings("-- VISUAL BLOCK --", hook.statusText(hook.ctx, &buf));
    try h.keys("<Esc>R");
    try testing.expectEqualStrings("-- REPLACE --", hook.statusText(hook.ctx, &buf));
}

test "window commands are the split groups" {
    var h = try Harness.init(sample);
    defer h.deinit();
    const ws = &h.app.workspace;
    try testing.expectEqual(@as(usize, 1), ws.groupCount());
    try h.keys("<C-w>v");
    try testing.expectEqual(@as(usize, 2), ws.groupCount());
    try h.keys("<C-w>s");
    try testing.expectEqual(@as(usize, 3), ws.groupCount());
    try h.keys("<C-w>c");
    try testing.expectEqual(@as(usize, 2), ws.groupCount());
    try h.keys("<C-w>o");
    try testing.expectEqual(@as(usize, 1), ws.groupCount());
    try h.keys(":vsp<CR>");
    try testing.expectEqual(@as(usize, 2), ws.groupCount());
    try h.keys(":only<CR>");
    try testing.expectEqual(@as(usize, 1), ws.groupCount());
}

test "folds" {
    var h = try Harness.init("a\n  b\n  c\nd\n");
    defer h.deinit();
    const rows = h.ed.displayRowCount();
    try h.keys("zfj");
    try testing.expect(h.ed.displayRowCount() < rows);
    try h.keys("zo");
    try testing.expectEqual(rows, h.ed.displayRowCount());
    try h.keys("zc");
    try testing.expect(h.ed.displayRowCount() < rows);
    try h.keys("zR");
    try testing.expectEqual(rows, h.ed.displayRowCount());
    try h.keys("zE");
    try testing.expectEqual(rows, h.ed.displayRowCount());
}

test "vim mode toggles off and on, off by default" {
    var h = try Harness.init("abc\n");
    defer h.deinit();
    const ws = &h.app.workspace;
    try testing.expect(ws.key_hook.?.enabled);
    ws.perform(.toggle_vim_mode);
    try testing.expect(!ws.key_hook.?.enabled);
    try h.keys("x");
    try h.expectText("abcx\n"[0..0] ++ "xabc\n");
    ws.perform(.toggle_vim_mode);
    try h.expectMode(.normal);
}

test "global, copy, move, put and normal" {
    try Harness.expectEdit("a1\nb\na2\nc\n", ":g/a/d<CR>", "b\nc\n");
    try Harness.expectEdit("a1\nb\na2\nc\n", ":v/a/d<CR>", "a1\na2\n");
    try Harness.expectEdit("a1\nb\na2\n", ":g/a/s/\\d/N/<CR>", "aN\nb\naN\n");
    try Harness.expectEdit("a\nb\nc\n", ":g/^/m0<CR>", "c\nb\na\n");
    try Harness.expectEdit("a\nb\n", ":g/./t.<CR>", "a\na\nb\nb\n");
    try Harness.expectEdit("a\nb\nc\n", ":2t0<CR>", "b\na\nb\nc\n");
    try Harness.expectEdit("a\nb\nc\n", ":1t$<CR>", "a\nb\nc\na\n");
    try Harness.expectEdit("a\nb\nc\n", ":1m$<CR>", "b\nc\na\n");
    try Harness.expectEdit("a\nb\nc\n", ":3m0<CR>", "c\na\nb\n");
    try Harness.expectEdit("a\nb\nc\n", ":1m2<CR>", "b\na\nc\n");
    try Harness.expectEdit("a\nb\nc\n", ":2y<CR>:$put<CR>", "a\nb\nc\nb\n");
    try Harness.expectEdit("a\nb\nc\n", "yy:3put!<CR>", "a\nb\na\nc\n");
    try Harness.expectEdit("a\nb\nc\n", ":%normal Ax<CR>", "ax\nbx\ncx\n");
    try Harness.expectEdit("a\nb\nc\n", ":2,3normal Ix<CR>", "a\nxb\nxc\n");
    try Harness.expectEdit("a b\nc d\n", ":%normal dw<CR>", "b\nd\n");
    try Harness.expectEdit("a1\na2\n", ":g/a/normal Ax<CR>u", "a1\na2\n");
    try Harness.finish();
}

test "search syntax: vim's magic patterns and case options" {
    try Harness.expectEdit("aXb\n", ":s/x/-/<CR>", "aXb\n");
    try Harness.expectEdit("aXb\n", ":set ic<CR>:s/x/-/<CR>", "a-b\n");
    try Harness.expectEdit("aXb\n", ":s/x\\c/-/<CR>", "a-b\n");
    try Harness.expectEdit("aXb\n", ":set ic scs<CR>:s/X/-/<CR>", "a-b\n");
    try Harness.expectEdit("aXb\n", ":set ic scs<CR>:s/x/-/<CR>", "a-b\n");
    try Harness.expectEdit("aXxb\n", ":set ic scs<CR>:s/X/-/<CR>", "a-xb\n");
    try Harness.expectEdit("f(x) f\n", ":s/f(x)/G/<CR>", "G f\n");
    try Harness.expectEdit("foo bar\n", ":s/\\<bar\\>/X/<CR>", "foo X\n");
    try Harness.expectEdit("aab\n", ":s/a\\+/X/<CR>", "Xb\n");
    try Harness.expectEdit("cat dog\n", ":s/\\(cat\\|dog\\)/X/g<CR>", "X X\n");
    try Harness.expectEdit("cat dog\n", ":s/\\v(cat|dog)/X/g<CR>", "X X\n");
    try Harness.expectEdit("aaa\n", ":s/a\\{2}/X/<CR>", "Xa\n");
    try Harness.expectEdit("a.b\n", ":s/\\./-/<CR>", "a-b\n");
    try Harness.expectEdit("a b\n", ":s/ /\\r/<CR>", "a\nb\n");
    try Harness.expectEdit("one two\n", ":s/\\w\\+/<&>/g<CR>", "<one> <two>\n");
    try Harness.finish();
}

test "search wraps and reports a missing pattern" {
    var h = try Harness.init(sample);
    defer h.deinit();
    try h.keys("G/alpha<CR>");
    try h.expectCaret(0, 0);
    try h.keys("/nothing<CR>");
    try h.expectCaret(0, 0);
    var buf: [128]u8 = undefined;
    const hook = h.app.workspace.key_hook.?;
    try testing.expect(std.mem.indexOf(u8, hook.statusText(hook.ctx, &buf), "not found") != null);
    try h.keys("2/foo<CR>");
    try h.expectCaret(2, 8);
}
