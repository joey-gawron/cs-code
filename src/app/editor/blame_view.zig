//! Git blame in an editor: a gutter column (short hash, author and age of
//! each line's commit, once per run of lines from the same commit) and
//! inline blame after the caret line, shown once the caret rests like
//! Zed's. The workspace (git_ui.zig) owns the blame data and the popup.

const std = @import("std");
const editor_mod = @import("editor.zig");
const view = @import("editor_view.zig");
const blame_mod = @import("../../model/git/blame.zig");
const scene_mod = @import("../../framework/gpu/scene.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const platform = @import("../../framework/platform/platform.zig");

const Editor = editor_mod.Editor;
const Scene = scene_mod.Scene;
const Rect = scene_mod.Rect;
const Color = scene_mod.Color;
const Theme = theme_mod.Theme;
const Commit = blame_mod.Commit;

/// How long the caret rests on a line before its inline blame appears.
pub const inline_delay_ms = 500;

/// Column layout in characters: hash, author, age.
const hash_chars = 7;
const author_chars = 14;
const age_chars = 14;
const column_chars = hash_chars + 1 + author_chars + 1 + age_chars;
/// Room for the age bar on the left and a gap before the line numbers.
const pad_left: f32 = 8;
const pad_right: f32 = 6;

/// Inline blame timing: the caret row (and buffer version) it waits on.
pub const InlineState = struct {
    row: u32 = std.math.maxInt(u32),
    version: u64 = 0,
    since: i64 = 0,
    shown: bool = false,
};

fn blameData(ed: *const Editor) ?*blame_mod.Blame {
    const b = ed.blame orelse return null;
    return if (b.hasData()) b else null;
}

/// Width the blame column adds to the gutter (0 when hidden or unavailable).
pub fn columnWidth(ed: *const Editor) f32 {
    if (!ed.show_blame or ed.mode == .single_line or blameData(ed) == null) return 0;
    return ed.charWidth() * column_chars + pad_left + pad_right;
}

/// The column, at the left edge of the gutter.
pub fn columnRect(ed: *const Editor) Rect {
    const g = ed.gutter_rect;
    return .{ .x = g.x, .y = g.y, .w = columnWidth(ed), .h = g.h };
}

/// Row under a window y (for the mouse over the column).
fn rowAtY(ed: *Editor, y: f32) u32 {
    return ed.buffer.offsetToPoint(view.hitTest(ed, .{ .x = ed.text_rect.x, .y = y })).row;
}

/// Writes at most `max` chars of `s` (cut with "…").
fn truncate(buf: []u8, s: []const u8, max: usize) []const u8 {
    var chars: usize = 0;
    var i: usize = 0;
    while (i < s.len) : (chars += 1) {
        if (chars == max) {
            // Too long: back up one char for the ellipsis.
            var cut = i - 1;
            while (cut > 0 and s[cut] & 0xC0 == 0x80) cut -= 1;
            return std.fmt.bufPrint(buf, "{s}…", .{s[0..cut]}) catch s[0..cut];
        }
        i += std.unicode.utf8ByteSequenceLength(s[i]) catch 1;
    }
    return s;
}

/// Newer commits get a stronger age bar (GitLens-style heat).
fn heat(now: i64, time: i64) f32 {
    const days: f32 = @floatFromInt(@max(0, @divTrunc(now - time, 86400)));
    return std.math.clamp(1 - @log(days + 1) / @log(1500.0), 0.12, 1);
}

pub fn paintColumn(ed: *Editor, scene: *Scene, theme: *const Theme, first_d: u32, last_d: u32) void {
    const blame = blameData(ed) orelse return;
    const col = columnRect(ed);
    if (col.w == 0) return;
    scene.pushClip(col) catch return;
    defer scene.popClip();
    const text = ed.env.text;
    const font = ed.env.font;
    const cw = ed.charWidth();
    const lh = ed.lineHeight();
    const baseline_off = ed.env.settings.line_padding_top + ed.metrics().ascent;
    const now = blame_mod.unixNow();
    const caret_row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    const hover_row = ed.blame_hover_row;
    const text_x = col.x + pad_left;

    var prev_row: ?u32 = null;
    var d = first_d;
    while (d <= last_d) : (d += 1) {
        const seg = ed.segmentAt(d);
        const row = seg.row;
        const y = view.rowY(ed, d);
        const commit = blame.commitAt(row) orelse {
            if (seg.index == 0) prev_row = null;
            continue;
        };
        // Rows of the hovered commit, anywhere on screen.
        if (hover_row) |hr| if (blame.sameCommit(hr, row)) {
            scene.fill(.{ .x = col.x, .y = y, .w = col.w, .h = lh }, theme.line_highlight.mix(theme.selection, 0.5));
        };
        const bar_color = if (commit.isUncommitted()) theme.caret.withAlpha(0.8) else theme.syntax.function.withAlpha(heat(now, commit.author_time) * 0.85);
        scene.fill(.{ .x = col.x + 2, .y = y, .w = 3, .h = lh }, bar_color);
        // Text only on the first row of a run of lines from one commit
        // (wrapped continuation rows belong to their line).
        if (seg.index != 0) continue;
        const same_as_prev = if (prev_row) |p| blame.sameCommit(p, row) else false;
        prev_row = row;
        // The top row repeats its commit, also when the row above it is
        // only partly scrolled into view.
        const top = d == first_d or (d == first_d + 1 and view.rowY(ed, first_d) < col.y - lh / 2);
        if (same_as_prev and !top) continue;
        if (!same_as_prev and d != first_d) scene.fill(.{ .x = col.x + pad_left, .y = y, .w = col.w - pad_left - pad_right, .h = 1 }, theme.indent_guide);

        const active = blame.sameCommit(caret_row, row);
        const color = if (active) theme.gutter_foreground_active else theme.gutter_foreground;
        const baseline = y + baseline_off;
        if (commit.isUncommitted()) {
            _ = text.drawText(scene, "Not committed yet", font, text_x, baseline, color);
            continue;
        }
        _ = text.drawText(scene, commit.shortHash(), font, text_x, baseline, color.mix(theme.syntax.comment, 0.5));
        var author_buf: [96]u8 = undefined;
        const author = truncate(&author_buf, commit.author, author_chars);
        _ = text.drawText(scene, author, font, text_x + cw * (hash_chars + 1), baseline, color);
        var age_buf: [48]u8 = undefined;
        const age = blame_mod.formatRelative(&age_buf, now, commit.author_time);
        const aw = text.measure(age, font);
        _ = text.drawText(scene, age, font, col.right() - pad_right - aw, baseline, color);
    }
}

/// Whether the caret has rested on its row long enough (and restarts the
/// wait when the row or the text changed).
fn caretRested(ed: *Editor, row: u32) bool {
    const st = &ed.inline_blame;
    const now = platform.nowMs();
    if (st.row != row or st.version != ed.buffer.version) {
        st.* = .{ .row = row, .version = ed.buffer.version, .since = now };
        return false;
    }
    if (now - st.since < inline_delay_ms) return false;
    st.shown = true;
    return true;
}

/// Ms until a waiting inline blame should appear (0: now), or null.
pub fn inlineDueIn(ed: *const Editor) ?i64 {
    if (!ed.env.settings.git_blame_inline or blameData(ed) == null) return null;
    const st = ed.inline_blame;
    if (st.shown or st.row == std.math.maxInt(u32)) return null;
    return @max(0, st.since + inline_delay_ms - platform.nowMs());
}

/// "Author, 3 days ago • summary" after the end of the caret line, in the
/// text area (drawn over nothing, so text and hit-testing don't move).
pub fn paintInline(ed: *Editor, scene: *Scene, theme: *const Theme, first_d: u32, last_d: u32) void {
    ed.inline_blame_rect = null;
    if (!ed.env.settings.git_blame_inline or ed.mode == .single_line) return;
    if (ed.selections.items.len != 1) return;
    const row = ed.buffer.offsetToPoint(ed.primarySelection().head).row;
    // The wait starts when the caret moves, even before blame data arrives.
    const rested = caretRested(ed, row);
    const blame = blameData(ed) orelse return;
    if (!rested or ed.isRowHidden(row)) return;
    const commit = blame.commitAt(row) orelse return;
    const le = ed.buffer.lineEnd(row);
    const d = ed.displayRowOfOffset(le);
    if (d < first_d or d > last_d) return;
    const cw = ed.charWidth();
    var x = view.screenX(ed, ed.xInSegment(ed.segmentAt(d), le)) + cw * 4;
    // After a fold's "…" placeholder.
    if (ed.display.folds.isFoldHeader(ed.buffer, row)) x += cw * 4;
    var buf: [640]u8 = undefined;
    const label = inlineText(&buf, commit, blame_mod.unixNow());
    const y = view.rowY(ed, d);
    const color = theme.syntax.comment.mix(theme.background, 0.25);
    const width = ed.env.text.drawText(scene, label, ed.env.font, x, y + ed.env.settings.line_padding_top + ed.metrics().ascent, color);
    ed.inline_blame_rect = .{ .x = x, .y = y, .w = width, .h = ed.lineHeight() };
}

pub fn inlineText(buf: []u8, commit: *const Commit, now: i64) []const u8 {
    if (commit.isUncommitted()) return "You • Uncommitted changes";
    var age_buf: [48]u8 = undefined;
    const age = blame_mod.formatRelative(&age_buf, now, commit.author_time);
    var summary_buf: [400]u8 = undefined;
    const summary = truncate(&summary_buf, commit.summary, 80);
    return std.fmt.bufPrint(buf, "{s}, {s} • {s}", .{ commit.author, age, summary }) catch commit.summary;
}

// ---- Mouse --------------------------------------------------------------------------

pub fn onMouseMove(ed: *Editor, pos: platform.Vec2) void {
    const r = columnRect(ed);
    ed.blame_hover_row = if (r.w > 0 and r.contains(pos.x, pos.y)) rowAtY(ed, pos.y) else null;
}

/// A click on the blame column asks for the commit popup (handled by the
/// workspace); returns whether it was one.
pub fn onMouseDown(ed: *Editor, pos: platform.Vec2) bool {
    const r = columnRect(ed);
    if (r.w == 0 or !r.contains(pos.x, pos.y)) return false;
    const row = rowAtY(ed, pos.y);
    if (ed.blame == null or ed.blame.?.commitAt(row) == null) return false;
    ed.blame_clicked = row;
    return true;
}

/// Row of the blame entry under `pos` (column or inline text), if any.
pub fn entryAt(ed: *Editor, pos: platform.Vec2) ?u32 {
    const r = columnRect(ed);
    if (r.w > 0 and r.contains(pos.x, pos.y)) {
        const row = rowAtY(ed, pos.y);
        if (ed.blame != null and ed.blame.?.commitAt(row) != null) return row;
        return null;
    }
    if (ed.inline_blame_rect) |ir| if (ir.contains(pos.x, pos.y)) return ed.inline_blame.row;
    return null;
}
