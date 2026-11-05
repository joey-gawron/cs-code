//! The Source Control view's popups, like magit's transients: a key of the
//! view (c, P, F, f, b, z, l, d, m, r, X, A, V, t; ? for all of them) shows
//! a menu at the bottom of the panel listing switches (`-` then a letter
//! toggles one) and commands (a letter runs one); Escape, q or ctrl+g
//! closes it. Also the commands themselves: committing through a
//! COMMIT_EDITMSG tab (ctrl+c ctrl+c commits, ctrl+c ctrl+k aborts, as do
//! saving and closing it, or closing it unsaved), pushing, pulling,
//! fetching, branches, stashes, logs, merges, rebases, resets,
//! cherry-picks, reverts and tags, with pickers and prompts for what they
//! need. git runs without a terminal: it can't ask for passwords
//! (credential helpers and ssh agents work) and takes default messages.

const std = @import("std");
const workspace_mod = @import("workspace.zig");
const buffer_mod = @import("../../model/text/buffer.zig");
const platform = @import("../../framework/platform/platform.zig");
const ui = @import("../../framework/ui/ui.zig");
const git = @import("../../model/git/git.zig");
const status_mod = @import("../../model/git/status.zig");
const git_status_ui = @import("git_status_ui.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Workspace = workspace_mod.Workspace;
const Buffer = buffer_mod.Buffer;
const Panel = git_status_ui.Panel;
const Request = git_status_ui.Request;
const PromptKind = git_status_ui.PromptKind;
const Rect = ui.Rect;

/// A commit message bigger than this isn't shown its diff (too slow to edit).
const max_template_diff = 2 << 20;

// ---- Menus --------------------------------------------------------------------------

const Arg = struct { key: u8, flag: []const u8, desc: []const u8 };

const Cmd = struct {
    key: u8,
    desc: []const u8,
    /// Shown instead of the key ("TAB"); such entries only explain.
    label: ?[]const u8 = null,
    run: ?*const fn (p: *Panel, args: Args, key: u8) void = null,
};

const Group = struct { title: []const u8, cmds: []const Cmd };

pub const Menu = struct {
    title: []const u8,
    args: []const Arg = &.{},
    groups: []const Group,
};

/// An open popup.
pub const State = struct {
    menu: *const Menu,
    /// The switches that are on (bits by index in `menu.args`).
    on: u32 = 0,
    /// `-` was typed: the next letter toggles a switch.
    dash: bool = false,
};

/// The switches a command was run with.
pub const Args = struct {
    menu: *const Menu,
    on: u32,

    pub fn has(self: Args, flag: []const u8) bool {
        for (self.menu.args, 0..) |a, i| if (self.on & (@as(u32, 1) << @intCast(i)) != 0 and std.mem.eql(u8, a.flag, flag)) return true;
        return false;
    }

    pub fn appendTo(self: Args, list: *std.ArrayList([]const u8), alloc: Allocator) !void {
        for (self.menu.args, 0..) |a, i| if (self.on & (@as(u32, 1) << @intCast(i)) != 0) try list.append(alloc, a.flag);
    }
};

const dispatch_menu: Menu = .{ .title = "Git", .groups = &.{
    .{ .title = "Popups", .cmds = &.{
        .{ .key = 'A', .desc = "Cherry-pick", .run = rekey },
        .{ .key = 'b', .desc = "Branch", .run = rekey },
        .{ .key = 'c', .desc = "Commit", .run = rekey },
        .{ .key = 'd', .desc = "Diff", .run = rekey },
        .{ .key = 'f', .desc = "Fetch", .run = rekey },
        .{ .key = 'F', .desc = "Pull", .run = rekey },
        .{ .key = 'l', .desc = "Log", .run = rekey },
    } },
    .{ .title = "", .cmds = &.{
        .{ .key = 'm', .desc = "Merge", .run = rekey },
        .{ .key = 'P', .desc = "Push", .run = rekey },
        .{ .key = 'r', .desc = "Rebase", .run = rekey },
        .{ .key = 't', .desc = "Tag", .run = rekey },
        .{ .key = 'V', .desc = "Revert", .run = rekey },
        .{ .key = 'X', .desc = "Reset", .run = rekey },
        .{ .key = 'z', .desc = "Stash", .run = rekey },
    } },
    .{ .title = "Applying changes", .cmds = &.{
        .{ .key = 's', .desc = "Stage", .run = rekey },
        .{ .key = 'S', .desc = "Stage all tracked", .run = rekey },
        .{ .key = 'u', .desc = "Unstage", .run = rekey },
        .{ .key = 'U', .desc = "Unstage all", .run = rekey },
        .{ .key = 'x', .desc = "Discard", .run = rekey },
        .{ .key = 'v', .desc = "Mark lines (region)", .run = rekey },
    } },
    .{ .title = "Moving and showing", .cmds = &.{
        .{ .key = 0, .label = "TAB", .desc = "Show/hide section" },
        .{ .key = 0, .label = "S-TAB", .desc = "Next level for all" },
        .{ .key = 0, .label = "1-4", .desc = "Level: sections to diffs" },
        .{ .key = 0, .label = "RET", .desc = "Visit file or commit" },
        .{ .key = 0, .label = "n p", .desc = "Next/previous section" },
        .{ .key = 0, .label = "j k ^", .desc = "Line down/up, parent" },
    } },
    .{ .title = "Essential", .cmds = &.{
        .{ .key = 'g', .desc = "Refresh", .run = rekey },
        .{ .key = '$', .desc = "Git's output", .run = rekey },
        .{ .key = '!', .desc = "Run a git command", .run = rekey },
        .{ .key = 'q', .desc = "Hide the panel", .run = rekey },
    } },
} };

const commit_menu: Menu = .{
    .title = "Commit",
    .args = &.{
        .{ .key = 'a', .flag = "--all", .desc = "Stage all modified and deleted files" },
        .{ .key = 'e', .flag = "--allow-empty", .desc = "Allow empty commit" },
        .{ .key = 'n', .flag = "--no-verify", .desc = "Disable hooks" },
        .{ .key = 's', .flag = "--signoff", .desc = "Add Signed-off-by line" },
        .{ .key = 'R', .flag = "--reset-author", .desc = "Claim authorship and reset author date" },
    },
    .groups = &.{
        .{ .title = "Create", .cmds = &.{.{ .key = 'c', .desc = "Commit", .run = commitCreate }} },
        .{ .title = "Edit HEAD", .cmds = &.{
            .{ .key = 'e', .desc = "Extend", .run = commitExtend },
            .{ .key = 'a', .desc = "Amend", .run = commitAmend },
            .{ .key = 'w', .desc = "Reword", .run = commitReword },
        } },
    },
};

const push_menu: Menu = .{
    .title = "Push",
    .args = &.{
        .{ .key = 'f', .flag = "--force-with-lease", .desc = "Force with lease" },
        .{ .key = 'F', .flag = "--force", .desc = "Force" },
        .{ .key = 'n', .flag = "--dry-run", .desc = "Dry run" },
        .{ .key = 'h', .flag = "--no-verify", .desc = "Disable hooks" },
    },
    .groups = &.{
        .{ .title = "Push the branch to", .cmds = &.{
            .{ .key = 'p', .desc = "its upstream, or origin (setting it)", .run = pushDefault },
            .{ .key = 'u', .desc = "its upstream", .run = pushUpstream },
        } },
        .{ .title = "Push", .cmds = &.{.{ .key = 't', .desc = "all tags", .run = pushTags }} },
    },
};

const pull_menu: Menu = .{
    .title = "Pull",
    .args = &.{
        .{ .key = 'r', .flag = "--rebase", .desc = "Rebase local commits" },
        .{ .key = 'f', .flag = "--ff-only", .desc = "Fast-forward only" },
        .{ .key = 'a', .flag = "--autostash", .desc = "Autostash" },
    },
    .groups = &.{.{ .title = "Pull into the branch from", .cmds = &.{
        .{ .key = 'p', .desc = "its upstream", .run = pull },
        .{ .key = 'u', .desc = "its upstream", .run = pull },
    } }},
};

const fetch_menu: Menu = .{
    .title = "Fetch",
    .args = &.{
        .{ .key = 'p', .flag = "--prune", .desc = "Prune deleted branches" },
        .{ .key = 't', .flag = "--tags", .desc = "Fetch all tags" },
    },
    .groups = &.{.{ .title = "Fetch from", .cmds = &.{
        .{ .key = 'p', .desc = "the upstream's remote (or origin)", .run = fetchUpstream },
        .{ .key = 'u', .desc = "the upstream's remote (or origin)", .run = fetchUpstream },
        .{ .key = 'a', .desc = "all remotes", .run = fetchAll },
    } }},
};

const branch_menu: Menu = .{ .title = "Branch", .groups = &.{
    .{ .title = "Checkout", .cmds = &.{
        .{ .key = 'b', .desc = "branch or revision", .run = branchCheckout },
        .{ .key = 'c', .desc = "new branch", .run = branchCreate },
    } },
    .{ .title = "Do", .cmds = &.{
        .{ .key = 'n', .desc = "create (no checkout)", .run = branchNew },
        .{ .key = 'm', .desc = "rename", .run = branchRename },
        .{ .key = 'k', .desc = "delete", .run = branchDelete },
        .{ .key = 'u', .desc = "set upstream", .run = branchUpstream },
    } },
} };

const stash_menu: Menu = .{
    .title = "Stash",
    .args = &.{
        .{ .key = 'u', .flag = "--include-untracked", .desc = "Also save untracked files" },
        .{ .key = 'a', .flag = "--all", .desc = "Also save untracked and ignored files" },
    },
    .groups = &.{
        .{ .title = "Stash", .cmds = &.{
            .{ .key = 'z', .desc = "both", .run = stashPush },
            .{ .key = 'i', .desc = "index", .run = stashPush },
            .{ .key = 'w', .desc = "worktree (keep index)", .run = stashPush },
        } },
        .{ .title = "Use", .cmds = &.{
            .{ .key = 'a', .desc = "apply", .run = stashUse },
            .{ .key = 'p', .desc = "pop", .run = stashUse },
            .{ .key = 'k', .desc = "drop", .run = stashUse },
            .{ .key = 'v', .desc = "show", .run = stashUse },
        } },
    },
};

const log_menu: Menu = .{
    .title = "Log",
    .args = &.{
        .{ .key = 'm', .flag = "--no-merges", .desc = "Omit merges" },
        .{ .key = 'f', .flag = "--first-parent", .desc = "First parent only" },
    },
    .groups = &.{.{ .title = "Log", .cmds = &.{
        .{ .key = 'l', .desc = "current branch", .run = logCurrent },
        .{ .key = 'o', .desc = "other…", .run = logOther },
        .{ .key = 'b', .desc = "all branches", .run = logBranches },
        .{ .key = 'a', .desc = "all references", .run = logAll },
    } }},
};

const diff_menu: Menu = .{
    .title = "Diff",
    .args = &.{.{ .key = 'w', .flag = "--ignore-all-space", .desc = "Ignore whitespace" }},
    .groups = &.{.{ .title = "Diff", .cmds = &.{
        .{ .key = 'd', .desc = "what's at the caret", .run = diffDwim },
        .{ .key = 's', .desc = "staged", .run = diffStaged },
        .{ .key = 'u', .desc = "unstaged", .run = diffUnstaged },
        .{ .key = 'w', .desc = "worktree (against HEAD)", .run = diffWorktree },
        .{ .key = 'c', .desc = "commit at the caret", .run = diffDwim },
    } }},
};

const merge_menu: Menu = .{
    .title = "Merge",
    .args = &.{
        .{ .key = 'f', .flag = "--ff-only", .desc = "Fast-forward only" },
        .{ .key = 'n', .flag = "--no-ff", .desc = "No fast-forward" },
        .{ .key = 's', .flag = "--squash", .desc = "Squash" },
    },
    .groups = &.{
        .{ .title = "Merge", .cmds = &.{.{ .key = 'm', .desc = "merge…", .run = mergePick }} },
        .{ .title = "In progress", .cmds = &.{
            .{ .key = 'c', .desc = "commit the merge", .run = mergeCommit },
            .{ .key = 'a', .desc = "abort", .run = mergeAbort },
        } },
    },
};

const rebase_menu: Menu = .{
    .title = "Rebase",
    .args = &.{.{ .key = 'a', .flag = "--autostash", .desc = "Autostash" }},
    .groups = &.{
        .{ .title = "Rebase the branch onto", .cmds = &.{
            .{ .key = 'u', .desc = "its upstream", .run = rebaseUpstream },
            .{ .key = 'e', .desc = "elsewhere…", .run = rebasePick },
        } },
        .{ .title = "In progress", .cmds = &.{
            .{ .key = 'r', .desc = "continue", .run = sequence },
            .{ .key = 's', .desc = "skip", .run = sequence },
            .{ .key = 'a', .desc = "abort", .run = sequence },
        } },
    },
};

const reset_menu: Menu = .{ .title = "Reset (to the commit at the caret, else pick one)", .groups = &.{.{ .title = "Reset the branch", .cmds = &.{
    .{ .key = 'm', .desc = "mixed (HEAD and index)", .run = reset },
    .{ .key = 's', .desc = "soft (HEAD only)", .run = reset },
    .{ .key = 'h', .desc = "hard (HEAD, index and files)", .run = reset },
} }} };

const cherry_menu: Menu = .{ .title = "Cherry-pick (the commit at the caret, else pick one)", .groups = &.{
    .{ .title = "Apply here", .cmds = &.{
        .{ .key = 'A', .desc = "pick", .run = cherryPick },
        .{ .key = 'a', .desc = "apply (no commit)", .run = cherryPick },
    } },
    .{ .title = "In progress", .cmds = &.{
        .{ .key = 'c', .desc = "continue", .run = sequence },
        .{ .key = 's', .desc = "skip", .run = sequence },
        .{ .key = 'x', .desc = "abort", .run = sequence },
    } },
} };

const revert_menu: Menu = .{ .title = "Revert (the commit at the caret)", .groups = &.{
    .{ .title = "Revert", .cmds = &.{
        .{ .key = 'V', .desc = "commit", .run = revert },
        .{ .key = 'v', .desc = "changes (no commit)", .run = revert },
    } },
    .{ .title = "In progress", .cmds = &.{
        .{ .key = 'c', .desc = "continue", .run = sequence },
        .{ .key = 's', .desc = "skip", .run = sequence },
        .{ .key = 'x', .desc = "abort", .run = sequence },
    } },
} };

const tag_menu: Menu = .{ .title = "Tag", .groups = &.{.{ .title = "Tag", .cmds = &.{
    .{ .key = 't', .desc = "create (at the caret's commit, or HEAD)", .run = tagCreate },
    .{ .key = 'k', .desc = "delete", .run = tagDelete },
} }} };

/// Opens the popup for a key of the view. Returns false if `ch` has none.
pub fn open(p: *Panel, ch: u21) bool {
    const menu: *const Menu = switch (ch) {
        '?', 'h' => &dispatch_menu,
        'c' => &commit_menu,
        'P' => &push_menu,
        'F' => &pull_menu,
        'f' => &fetch_menu,
        'b' => &branch_menu,
        'z', 'Z' => &stash_menu,
        'l' => &log_menu,
        'd' => &diff_menu,
        'm' => &merge_menu,
        'r' => &rebase_menu,
        'X' => &reset_menu,
        'A' => &cherry_menu,
        'V' => &revert_menu,
        't' => &tag_menu,
        else => return false,
    };
    if (p.view.root == null and menu != &dispatch_menu) {
        p.ws.setStatus("No git repository", .{});
        return true;
    }
    p.popup = .{ .menu = menu };
    p.ws.dirty = true;
    return true;
}

fn keyChar(k: platform.KeyEvent) ?u21 {
    return switch (k.key) {
        .char => |c| if (k.text.len == 1 and !k.mods.ctrl) k.text[0] else c,
        .named => null,
    };
}

/// Keys while a popup is open: all of them, but other ctrl chords.
pub fn key(p: *Panel, k: platform.KeyEvent) bool {
    const ws = p.ws;
    const st = if (p.popup) |*open_popup| open_popup else return false;
    ws.dirty = true;
    if (k.mods.ctrl) {
        if (keyChar(k) != 'g') return false;
        p.popup = null;
        return true;
    }
    if (k.key == .named) {
        if (k.key.named == .escape) {
            if (st.dash) st.dash = false else p.popup = null;
        }
        return true;
    }
    const ch = keyChar(k) orelse return true;
    if (st.dash) {
        st.dash = false;
        for (st.menu.args, 0..) |a, i| if (a.key == ch) {
            st.on ^= @as(u32, 1) << @intCast(i);
            return true;
        };
        ws.setStatus("No switch -{u} here", .{ch});
        return true;
    }
    if (ch == '-' and st.menu.args.len > 0) {
        st.dash = true;
        return true;
    }
    for (st.menu.groups) |g| for (g.cmds) |c| {
        if (c.key != ch) continue;
        const run_fn = c.run orelse continue;
        const args: Args = .{ .menu = st.menu, .on = st.on };
        p.popup = null;
        run_fn(p, args, c.key);
        return true;
    };
    if (ch == 'q') {
        p.popup = null;
        return true;
    }
    ws.setStatus("{u} isn't a key of this popup (Escape closes it)", .{ch});
    return true;
}

/// The ? popup's entries run the view's own keys.
fn rekey(p: *Panel, _: Args, k: u8) void {
    _ = git_status_ui.statusKey(p, k);
}

// ---- Painting -----------------------------------------------------------------------

/// The open popup, over the bottom of the Source Control view (`body`).
pub fn paint(ws: *Workspace, ctx: *const ui.Ctx, body: Rect) void {
    const p = ws.panel.source_control orelse return;
    const st = p.popup orelse return;
    const m = st.menu;
    const theme = ctx.theme;
    const font = ctx.fonts.mono;
    const lh = @round(p.editor.lineHeight());
    const cw = ctx.text.measure("m", font);
    const pad: f32 = 8;

    // Groups side by side, wrapping into blocks when they don't fit.
    const max_cols = 8;
    var widths: [max_cols * 2]f32 = undefined;
    for (m.groups, 0..) |g, i| {
        var chars = g.title.len;
        for (g.cmds) |c| chars = @max(chars, (if (c.label) |l| l.len else 1) + 2 + c.desc.len);
        widths[i] = @as(f32, @floatFromInt(chars + 4)) * cw;
    }
    var rows: usize = 1;
    if (m.args.len > 0) rows += m.args.len + 2;
    var blocks: std.ArrayList([2]usize) = .empty;
    defer blocks.deinit(ws.gpa);
    var start: usize = 0;
    while (start < m.groups.len) {
        var w: f32 = 0;
        var end = start;
        var tallest: usize = 0;
        while (end < m.groups.len and (end == start or w + widths[end] <= body.w - 2 * pad)) : (end += 1) {
            w += widths[end];
            tallest = @max(tallest, m.groups[end].cmds.len);
        }
        blocks.append(ws.gpa, .{ start, end }) catch return;
        rows += tallest + 1 + @intFromBool(start > 0);
        start = end;
    }
    const height = @min(body.h, @as(f32, @floatFromInt(rows)) * lh + 2 * pad);
    const box: Rect = .{ .x = body.x, .y = body.bottom() - height, .w = body.w, .h = height };
    const scene = ctx.scene;
    // Above the view's text, which draws after its backgrounds.
    scene.pushLayer() catch return;
    defer scene.popLayer();
    scene.pushClip(box) catch return;
    defer scene.popClip();
    scene.fill(box, theme.sidebar_background);
    scene.fill(.{ .x = box.x, .y = box.y, .w = box.w, .h = 1 }, theme.tab_separator);

    const x0 = box.x + pad + cw;
    var y = box.y + pad;
    const Text = struct {
        ctx: *const ui.Ctx,
        font: ui.FontId,
        lh: f32,
        fn at(self: @This(), x: f32, yy: f32, text: []const u8, color: ui.Color) void {
            _ = self.ctx.label(.{ .x = x, .y = yy, .w = 2000, .h = self.lh }, text, self.font, color);
        }
    };
    const t: Text = .{ .ctx = ctx, .font = font, .lh = lh };
    t.at(x0, y, m.title, theme.syntax.function);
    if (st.dash) t.at(x0 + @as(f32, @floatFromInt(m.title.len + 2)) * cw, y, "-", theme.syntax.keyword);
    y += lh;
    if (m.args.len > 0) {
        y += lh * 0.5;
        t.at(x0, y, "Arguments", theme.syntax.function);
        y += lh;
        for (m.args, 0..) |a, i| {
            const on = st.on & (@as(u32, 1) << @intCast(i)) != 0;
            var kbuf: [2]u8 = .{ '-', a.key };
            t.at(x0 + cw, y, &kbuf, theme.syntax.keyword);
            t.at(x0 + 4 * cw, y, a.desc, theme.foreground);
            var fbuf: [96]u8 = undefined;
            const flag = std.fmt.bufPrint(&fbuf, "({s})", .{a.flag}) catch a.flag;
            t.at(x0 + @as(f32, @floatFromInt(5 + a.desc.len)) * cw, y, flag, if (on) theme.syntax.string else theme.syntax.comment);
            y += lh;
        }
        y += lh * 0.5;
    }
    for (blocks.items, 0..) |b, bi| {
        if (bi > 0) y += lh;
        var x = x0;
        var tallest: usize = 0;
        for (m.groups[b[0]..b[1]], b[0]..) |g, gi| {
            t.at(x, y, g.title, theme.syntax.function);
            for (g.cmds, 0..) |c, ci| {
                const cy = y + @as(f32, @floatFromInt(ci + 1)) * lh;
                var kbuf: [1]u8 = .{c.key};
                const label = c.label orelse &kbuf;
                t.at(x + cw, cy, label, theme.syntax.keyword);
                t.at(x + @as(f32, @floatFromInt(label.len + 2)) * cw, cy, c.desc, theme.foreground);
            }
            tallest = @max(tallest, g.cmds.len);
            x += widths[gi];
        }
        y += @as(f32, @floatFromInt(tallest + 1)) * lh;
    }
}

// ---- Helpers ------------------------------------------------------------------------

fn statusOf(p: *Panel) ?*const status_mod.Status {
    const st = p.status();
    if (st == null) p.ws.setStatus("Git status isn't loaded yet", .{});
    return st;
}

/// The branch checked out, or null (saying so) when HEAD is detached.
fn currentBranch(p: *Panel) ?[]const u8 {
    const st = statusOf(p) orelse return null;
    return st.branch.head orelse {
        p.ws.setStatus("Not on a branch (HEAD is detached)", .{});
        return null;
    };
}

/// The commit at the caret (a commit line or the Head / Merge line).
fn caretCommit(p: *Panel) ?[]const u8 {
    const t = p.view.targetAt(git_status_ui.caretRow(p.editor)) orelse return null;
    const c = p.view.commit(t) orelse return null;
    return c.hash;
}

fn caretStash(p: *Panel) ?[]const u8 {
    const t = p.view.targetAt(git_status_ui.caretRow(p.editor)) orelse return null;
    const s = p.view.stash(t) orelse return null;
    return s.name;
}

/// "origin/main" → "origin" and "main".
fn splitUpstream(up: []const u8) ?struct { remote: []const u8, branch: []const u8 } {
    const slash = std.mem.indexOfScalar(u8, up, '/') orelse return null;
    if (slash == 0 or slash + 1 >= up.len) return null;
    return .{ .remote = up[0..slash], .branch = up[slash + 1 ..] };
}

/// Runs `git <head…> <switches> <tail…>`.
fn runWith(p: *Panel, head: []const []const u8, args: Args, tail: []const []const u8, r: git_status_ui.Run) void {
    var arena = std.heap.ArenaAllocator.init(p.ws.gpa);
    defer arena.deinit();
    const a = arena.allocator();
    var argv: std.ArrayList([]const u8) = .empty;
    argv.appendSlice(a, head) catch return;
    args.appendTo(&argv, a) catch return;
    argv.appendSlice(a, tail) catch return;
    git_status_ui.run(p, argv.items, r);
}

fn noArgs() Args {
    return .{ .menu = &dispatch_menu, .on = 0 };
}

/// Asks, then runs `git argv…`.
fn confirmRun(p: *Panel, question: []const u8, argv: []const []const u8, r: git_status_ui.Run) void {
    const req = Request.create(p.ws.gpa) catch return;
    const a = req.alloc();
    const owned = a.alloc([]const u8, argv.len) catch return req.destroy(p.ws.gpa);
    for (argv, owned) |s, *o| o.* = a.dupe(u8, s) catch return req.destroy(p.ws.gpa);
    req.argv = owned;
    req.run = r;
    req.run.label = a.dupe(u8, r.label) catch "";
    req.run.done = a.dupe(u8, r.done) catch "";
    git_status_ui.confirm(p, question, req);
}

// ---- Commit ---------------------------------------------------------------------------

/// A commit being prepared: its options, then HEAD's message (amend,
/// reword) and the diff shown below the message.
pub const CommitPlan = struct {
    arena: std.heap.ArenaAllocator,
    kind: Kind,
    flags: std.ArrayList([]const u8) = .empty,
    message: []const u8 = "",

    pub const Kind = enum { create, amend, reword };

    pub fn destroy(self: *CommitPlan, gpa: Allocator) void {
        self.arena.deinit();
        gpa.destroy(self);
    }
};

/// What a yes answer continues with (`Request.then`).
pub const Then = enum {
    /// Nothing was staged: commit with --all.
    commit_all,
};

/// The step a git command's result goes to (`After.popup`).
pub const Next = union(enum) {
    commit_message,
    commit_diff,
    committed,
    /// For a picker of branches and tags.
    refs: Pick,
    /// `git branch -d` (not merged: offer -D).
    delete_branch,
};

/// A branch or tag a picker lists.
pub const Choice = struct { name: []const u8, remote: bool = false };

/// What a picked branch, tag or revision is for.
pub const Pick = enum { checkout, delete_branch, merge, rebase, reset_soft, reset_mixed, reset_hard, upstream, log, delete_tag, cherry_pick, cherry_apply };

/// A COMMIT_EDITMSG tab (`Tab.git_commit`).
pub const CommitTab = struct {
    gpa: Allocator,
    arena: std.heap.ArenaAllocator,
    /// The repository.
    root: []const u8,
    path: []const u8,
    /// Options for `git commit`.
    flags: []const []const u8,
    /// The file's modification time as written: a later save means the
    /// message was written, so closing the tab commits.
    written: i128,
    /// ctrl+c was typed: ctrl+c commits, ctrl+k aborts.
    prefix: bool = false,
    /// Committed or aborted: closing the tab does nothing more.
    done: bool = false,

    pub fn destroy(self: *CommitTab) void {
        const gpa = self.gpa;
        self.arena.deinit();
        gpa.destroy(self);
    }
};

fn newPlan(p: *Panel, kind: CommitPlan.Kind, args: Args) ?*CommitPlan {
    const gpa = p.ws.gpa;
    if (p.commit) |old| old.destroy(gpa);
    p.commit = null;
    const plan = gpa.create(CommitPlan) catch return null;
    plan.* = .{ .arena = .init(gpa), .kind = kind };
    const a = plan.arena.allocator();
    args.appendTo(&plan.flags, a) catch {};
    switch (kind) {
        .create => {},
        .amend => plan.flags.append(a, "--amend") catch {},
        .reword => plan.flags.appendSlice(a, &.{ "--amend", "--only" }) catch {},
    }
    p.commit = plan;
    return plan;
}

/// c c: a new commit. Nothing staged asks to commit all tracked changes.
fn commitCreate(p: *Panel, args: Args, _: u8) void {
    const st = statusOf(p) orelse return;
    var staged = false;
    var changed = false;
    for (st.entries) |*e| {
        if (status_mod.inSection(e, .staged)) staged = true;
        if (status_mod.inSection(e, .unstaged)) changed = true;
    }
    _ = newPlan(p, .create, args) orelse return;
    if (!staged and !args.has("--all") and !args.has("--allow-empty")) {
        if (!changed) {
            p.ws.setStatus("Nothing to commit (stage changes with s, or use -e for an empty commit)", .{});
            return;
        }
        const req = Request.create(p.ws.gpa) catch return;
        req.then = .commit_all;
        git_status_ui.confirm(p, "Nothing is staged. Commit all tracked changes (--all)?", req);
        return;
    }
    prepareCommit(p);
}

fn commitAmend(p: *Panel, args: Args, _: u8) void {
    if (!hasHead(p)) return;
    _ = newPlan(p, .amend, args) orelse return;
    prepareCommit(p);
}

fn commitReword(p: *Panel, args: Args, _: u8) void {
    if (!hasHead(p)) return;
    _ = newPlan(p, .reword, args) orelse return;
    prepareCommit(p);
}

/// c e: adds what's staged to HEAD, keeping its message.
fn commitExtend(p: *Panel, args: Args, _: u8) void {
    if (!hasHead(p)) return;
    runWith(p, &.{ "commit", "--amend", "--no-edit" }, args, &.{}, .{ .label = "Extending HEAD", .done = "Extended HEAD", .batch = true });
}

fn hasHead(p: *Panel) bool {
    const st = statusOf(p) orelse return false;
    if (st.branch.initial()) {
        p.ws.setStatus("There's no commit to change yet", .{});
        return false;
    }
    return true;
}

/// A yes answer's step.
pub fn runThen(p: *Panel, then: Then, _: *Request) void {
    switch (then) {
        .commit_all => {
            const plan = p.commit orelse return;
            plan.flags.append(plan.arena.allocator(), "--all") catch return;
            prepareCommit(p);
        },
    }
}

/// Gets HEAD's message (amend, reword), then the diff, then opens the tab.
fn prepareCommit(p: *Panel) void {
    const plan = p.commit orelse return;
    if (plan.kind != .create) {
        git_status_ui.run(p, &.{ "--no-optional-locks", "log", "-1", "--no-color", "--format=%B", "HEAD", "--" }, .{ .after = .{ .popup = .commit_message } });
        return;
    }
    // An aborted or failed commit's message comes back.
    if (p.last_message) |m| plan.message = plan.arena.allocator().dupe(u8, m) catch "";
    commitDiff(p);
}

fn hasFlag(plan: *const CommitPlan, flag: []const u8) bool {
    for (plan.flags.items) |f| if (std.mem.eql(u8, f, flag)) return true;
    return false;
}

/// The diff shown under the message: what the commit will contain.
fn commitDiff(p: *Panel) void {
    const plan = p.commit orelse return;
    const st = statusOf(p) orelse return;
    const all = hasFlag(plan, "--all") and !st.branch.initial();
    const opts = [_][]const u8{ "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/" };
    const show = [_][]const u8{ "--no-optional-locks", "show", "--format=" } ++ opts;
    const diff = [_][]const u8{ "--no-optional-locks", "diff" } ++ opts;
    const run = git_status_ui.run;
    const next: git_status_ui.Run = .{ .after = .{ .popup = .commit_diff } };
    switch (plan.kind) {
        .reword => run(p, &(show ++ [_][]const u8{ "HEAD", "--" }), next),
        .amend => if (all) run(p, &(diff ++ [_][]const u8{ "HEAD~1", "--" }), next) else run(p, &(diff ++ [_][]const u8{ "--cached", "HEAD~1", "--" }), next),
        .create => if (all) run(p, &(diff ++ [_][]const u8{ "HEAD", "--" }), next) else run(p, &(diff ++ [_][]const u8{ "--cached", "--" }), next),
    }
}

fn openCommitTab(p: *Panel, diff_text: []const u8) void {
    const ws = p.ws;
    const gpa = ws.gpa;
    const plan = p.commit orelse return;
    defer {
        plan.destroy(gpa);
        p.commit = null;
    }
    const git_dir = p.git_dir orelse return;
    const root = p.view.root orelse return;
    const st = statusOf(p) orelse return;
    const path = std.fmt.allocPrint(gpa, "{s}/COMMIT_EDITMSG", .{git_dir}) catch return;
    defer gpa.free(path);

    // An older message tab (its message kept) and other views of the file go.
    var i = ws.tabs.items.len;
    while (i > 0) {
        i -= 1;
        const tab = ws.tabs.items[i];
        if (tab.git_commit) |c| if (!c.done) {
            c.done = true;
            const text = tab.editor.buffer.rope.toOwnedSlice(gpa) catch continue;
            defer gpa.free(text);
            remember(p, status_mod.commitMessage(text));
        };
        const same = if (tab.editor.buffer.path) |bp| std.mem.eql(u8, bp, path) else false;
        if (tab.git_commit != null or same) ws.closeTabNow(i);
    }

    var about_buf: [256]u8 = undefined;
    const head = st.head;
    const about = switch (plan.kind) {
        .amend => std.fmt.bufPrint(&about_buf, "Amending {s} {s}", .{ if (head) |h| h.hash else "HEAD", if (head) |h| h.subject else "" }),
        .reword => std.fmt.bufPrint(&about_buf, "Rewording {s} {s}", .{ if (head) |h| h.hash else "HEAD", if (head) |h| h.subject else "" }),
        .create => if (st.branch.head) |b|
            std.fmt.bufPrint(&about_buf, "On branch {s}{s}", .{ b, if (hasFlag(plan, "--all")) " (committing all tracked changes)" else "" })
        else
            std.fmt.bufPrint(&about_buf, "HEAD detached at {s}", .{st.branch.oid[0..@min(st.branch.oid.len, 7)]}),
    } catch "";
    const shown_diff = if (diff_text.len > max_template_diff) "" else diff_text;
    const template = status_mod.commitTemplate(gpa, plan.message, about, shown_diff) catch return;
    defer gpa.free(template);
    Io.Dir.cwd().writeFile(ws.io, .{ .sub_path = path, .data = template }) catch |err| {
        return ws.setStatus("Unable to write {s}: {t}", .{ path, err });
    };

    const c = gpa.create(CommitTab) catch return;
    c.* = .{ .gpa = gpa, .arena = .init(gpa), .root = "", .path = "", .flags = &.{}, .written = mtime(ws.io, path) };
    const a = c.arena.allocator();
    c.root = a.dupe(u8, root) catch "";
    c.path = a.dupe(u8, path) catch "";
    const flags = a.alloc([]const u8, plan.flags.items.len) catch return c.destroy();
    for (plan.flags.items, flags) |f, *o| o.* = a.dupe(u8, f) catch "";
    c.flags = flags;
    const ed = ws.openFile(path, false) catch |err| {
        c.destroy();
        return ws.setStatus("Unable to open {s}: {t}", .{ path, err });
    };
    const index = ws.tabIndexOf(ed) orelse return c.destroy();
    ws.tabs.items[index].git_commit = c;
    ed.setSingleSelection(.caret(0));
    ed.ensureCaretVisible(false);
    ws.focus = .editor;
    ws.setStatus("Write the commit message: ctrl+c ctrl+c commits, ctrl+c ctrl+k aborts", .{});
}

fn mtime(io: Io, path: []const u8) i128 {
    const st = Io.Dir.cwd().statFile(io, path, .{}) catch return 0;
    return st.mtime.nanoseconds;
}

/// Keeps a message that wasn't committed, to offer it again (c c).
fn remember(p: *Panel, message: []const u8) void {
    const gpa = p.ws.gpa;
    const text = status_mod.messageText(gpa, message) catch return;
    if (text.len == 0) return gpa.free(text);
    if (p.last_message) |m| gpa.free(m);
    p.last_message = text;
}

/// Keys in a COMMIT_EDITMSG tab: ctrl+c ctrl+c commits, ctrl+c ctrl+k
/// aborts. A ctrl+c with text selected still copies.
pub fn commitKey(ws: *Workspace, c: *CommitTab, index: usize, k: platform.KeyEvent) bool {
    const ctrl_only = k.mods.ctrl and !k.mods.alt and !k.mods.shift;
    const ch: ?u21 = switch (k.key) {
        .char => |x| std.ascii.toLower(@intCast(@min(x, 127))),
        .named => null,
    };
    if (c.prefix) {
        c.prefix = false;
        if (ctrl_only and ch == 'c') {
            finishCommit(ws, c, index);
            return true;
        }
        if (ctrl_only and ch == 'k') {
            abortCommit(ws, c, index);
            return true;
        }
        return false;
    }
    if (ctrl_only and ch == 'c' and ws.tabs.items[index].editor.primarySelection().isEmpty()) {
        c.prefix = true;
        ws.setStatus("ctrl+c: ctrl+c commits, ctrl+k aborts", .{});
        return true;
    }
    return false;
}

fn finishCommit(ws: *Workspace, c: *CommitTab, index: usize) void {
    const ed = ws.tabs.items[index].editor;
    const text = ed.buffer.rope.toOwnedSlice(ws.gpa) catch return;
    defer ws.gpa.free(text);
    const message = status_mod.commitMessage(text);
    if (status_mod.messageEmpty(message)) return ws.setStatus("The message is empty (ctrl+c ctrl+k aborts the commit)", .{});
    // The file holds what was committed, as with git's own editor.
    ed.buffer.save(ws.io) catch {};
    if (!commitWith(ws, c, message)) return;
    c.done = true;
    ws.closeTabNow(index);
}

fn abortCommit(ws: *Workspace, c: *CommitTab, index: usize) void {
    const ed = ws.tabs.items[index].editor;
    if (ws.panel.source_control) |p| if (ed.buffer.rope.toOwnedSlice(ws.gpa)) |text| {
        defer ws.gpa.free(text);
        remember(p, status_mod.commitMessage(text));
    } else |_| {};
    c.done = true;
    ws.closeTabNow(index);
    ws.setStatus("Commit aborted (c c brings the message back)", .{});
}

/// The tab was closed some other way: as with git's editor, a message that
/// was saved is committed; otherwise the commit is aborted.
pub fn commitTabClosed(ws: *Workspace, c: *CommitTab, buffer: *Buffer) void {
    defer c.destroy();
    if (c.done) return;
    const text = buffer.rope.toOwnedSlice(ws.gpa) catch return;
    defer ws.gpa.free(text);
    const message = status_mod.commitMessage(text);
    const saved = !buffer.isDirty() and mtime(ws.io, c.path) != c.written;
    if (saved and !status_mod.messageEmpty(message) and commitWith(ws, c, message)) return;
    if (ws.panel.source_control) |p| remember(p, message);
    ws.setStatus("Commit aborted (c c brings the message back)", .{});
}

/// Runs `git commit` with the message on stdin. False if it couldn't start.
fn commitWith(ws: *Workspace, c: *CommitTab, message: []const u8) bool {
    const p = ws.panel.source_control orelse return false;
    const root = p.view.root orelse "";
    if (!std.mem.eql(u8, root, c.root)) {
        ws.setStatus("The Source Control view shows another repository now; open {s} there to commit", .{c.root});
        return false;
    }
    // Kept until git says it worked.
    remember(p, message);
    var arena = std.heap.ArenaAllocator.init(ws.gpa);
    defer arena.deinit();
    const a = arena.allocator();
    var argv: std.ArrayList([]const u8) = .empty;
    argv.appendSlice(a, &.{ "commit", "--cleanup=strip", "-F", "-" }) catch return false;
    argv.appendSlice(a, c.flags) catch return false;
    git_status_ui.run(p, argv.items, .{ .input = message, .label = "Committing", .after = .{ .popup = .committed }, .batch = true });
    return true;
}

// ---- Results of git commands ----------------------------------------------------------

pub fn onResult(p: *Panel, next: Next, job: *git.Job) void {
    const ws = p.ws;
    const out = job.output orelse "";
    switch (next) {
        .commit_message => {
            const plan = p.commit orelse return;
            if (job.failed) {
                git_status_ui.reportFailure(p, "Reading HEAD's message", job);
                plan.destroy(ws.gpa);
                p.commit = null;
                return;
            }
            plan.message = plan.arena.allocator().dupe(u8, out) catch "";
            commitDiff(p);
        },
        // No diff (a root commit's amend) is no reason not to commit.
        .commit_diff => openCommitTab(p, if (job.failed) "" else out),
        .committed => {
            if (job.failed) {
                git_status_ui.reportFailure(p, "Commit", job);
            } else {
                if (p.last_message) |m| ws.gpa.free(m);
                p.last_message = null;
                // "[main 1a2b3c4] Subject"
                const first = std.mem.sliceTo(std.mem.trimStart(u8, out, " \n"), '\n');
                ws.setStatus("{s}", .{if (first.len > 0) first else "Committed"});
            }
            git_status_ui.startStatus(p);
        },
        .refs => |what| {
            if (job.failed) return git_status_ui.reportFailure(p, "Listing branches", job);
            openPicker(p, what, out);
        },
        .delete_branch => {
            const name = job.argv[job.argv.len - 1];
            if (job.failed) {
                const err = job.stderr orelse "";
                if (std.mem.indexOf(u8, err, "not fully merged") != null) {
                    var qbuf: [256]u8 = undefined;
                    const q = std.fmt.bufPrint(&qbuf, "{s} isn't merged. Delete it anyway (its commits may be lost)?", .{name}) catch "Delete it anyway?";
                    return confirmRun(p, q, &.{ "branch", "-D", name }, .{ .label = "Deleting branch", .done = "Deleted the branch" });
                }
                return git_status_ui.reportFailure(p, "Deleting the branch", job);
            }
            ws.setStatus("Deleted branch {s}", .{name});
            git_status_ui.startStatus(p);
        },
    }
}

// ---- Pickers and prompts -------------------------------------------------------------

/// Lists branches (and tags) to pick one for `what`; the switches of the
/// popup it came from are kept for the command.
fn pick(p: *Panel, what: Pick, args: Args) void {
    const gpa = p.ws.gpa;
    if (p.pending) |old| old.destroy(gpa);
    p.pending = null;
    const req = Request.create(gpa) catch return;
    var flags: std.ArrayList([]const u8) = .empty;
    args.appendTo(&flags, req.alloc()) catch {};
    req.argv = flags.items;
    p.pending = req;
    p.pick = what;
    const refs: []const []const u8 = switch (what) {
        .checkout, .merge, .rebase, .log, .reset_soft, .reset_mixed, .reset_hard, .cherry_pick, .cherry_apply => &.{ "refs/heads", "refs/remotes", "refs/tags" },
        .delete_branch => &.{"refs/heads"},
        .upstream => &.{ "refs/remotes", "refs/heads" },
        .delete_tag => &.{"refs/tags"},
    };
    runWith(p, &.{ "--no-optional-locks", "for-each-ref", "--sort=-committerdate", "--format=%(refname)" }, noArgs(), refs, .{ .after = .{ .popup = .{ .refs = what } } });
}

fn openPicker(p: *Panel, what: Pick, out: []const u8) void {
    const ws = p.ws;
    const current = if (p.status()) |st| st.branch.head else null;
    const label = switch (what) {
        .checkout => "Checkout (a branch, or type a revision):",
        .delete_branch => "Delete branch:",
        .merge => "Merge into the current branch:",
        .rebase => "Rebase the current branch onto:",
        .reset_soft, .reset_mixed, .reset_hard => "Reset the current branch to (or type a revision):",
        .upstream => "Set the upstream of the current branch to:",
        .log => "Log of (or type a revision):",
        .delete_tag => "Delete tag:",
        .cherry_pick, .cherry_apply => "Cherry-pick (or type a revision):",
    };
    // The names stay with the pending request: the list's own go when it closes.
    const req = p.pending orelse return;
    const a = req.alloc();
    var choices: std.ArrayList(Choice) = .empty;
    ws.openList(.list, ws.gpa.dupe(u8, label) catch return, @intFromEnum(PromptKind.pick), &.{});
    const o = ws.overlay orelse return;
    o.input.placeholder = "branch, tag or revision";
    defer req.choices = choices.items;
    var it = std.mem.tokenizeScalar(u8, out, '\n');
    while (it.next()) |ref| {
        const kinds = [_]struct { prefix: []const u8, detail: []const u8 }{
            .{ .prefix = "refs/heads/", .detail = "branch" },
            .{ .prefix = "refs/remotes/", .detail = "remote branch" },
            .{ .prefix = "refs/tags/", .detail = "tag" },
        };
        for (kinds) |k| if (std.mem.startsWith(u8, ref, k.prefix)) {
            const name = ref[k.prefix.len..];
            if (std.mem.endsWith(u8, name, "/HEAD")) break;
            // Checking out or deleting the branch you're on does nothing.
            if ((what == .checkout or what == .delete_branch) and k.detail[0] == 'b' and current != null and std.mem.eql(u8, name, current.?)) break;
            choices.append(a, .{ .name = a.dupe(u8, name) catch break, .remote = k.detail[0] == 'r' }) catch break;
            o.addItem(.{ .title = o.arena().dupe(u8, name) catch break, .detail = k.detail, .payload = .{ .value = choices.items.len - 1 } }) catch break;
            break;
        };
    }
}

/// Typed text that names a revision rather than filtering the list.
fn looksLikeRevision(s: []const u8) bool {
    if (std.mem.indexOfAny(u8, s, "~^@:") != null) return true;
    if (s.len < 7) return false;
    for (s) |ch| if (!std.ascii.isHex(ch)) return false;
    return true;
}

/// A picker's choice (`index` of the picked item, or the typed `query`).
pub fn picked(p: *Panel, index: ?usize, query: []const u8) void {
    const gpa = p.ws.gpa;
    const flags_req = p.pending orelse return;
    p.pending = null;
    defer flags_req.destroy(gpa);
    const typed = std.mem.trim(u8, query, " \t");
    const choice: ?Choice = if (index) |i| (if (i < flags_req.choices.len) flags_req.choices[i] else null) else null;
    const listed = choice != null and !(looksLikeRevision(typed) and !std.mem.eql(u8, choice.?.name, typed));
    const name = if (listed) choice.?.name else typed;
    if (name.len == 0) return;
    const args = flags_req.argv;
    var arena = std.heap.ArenaAllocator.init(gpa);
    defer arena.deinit();
    const a = arena.allocator();
    var argv: std.ArrayList([]const u8) = .empty;
    const run = git_status_ui.run;
    switch (p.pick) {
        .checkout => {
            // A remote branch: `git checkout foo` makes the local branch that tracks it.
            var target = name;
            if (listed and choice.?.remote) if (std.mem.indexOfScalar(u8, name, '/')) |slash| {
                target = name[slash + 1 ..];
            };
            const label = std.fmt.allocPrint(a, "Checking out {s}", .{target}) catch "Checking out";
            run(p, &.{ "checkout", target }, .{ .label = label, .done = std.fmt.allocPrint(a, "On {s}", .{target}) catch "" });
        },
        .delete_branch => run(p, &.{ "branch", "-d", name }, .{ .label = "Deleting the branch", .after = .{ .popup = .delete_branch } }),
        .merge, .rebase, .cherry_pick, .cherry_apply => {
            argv.appendSlice(a, switch (p.pick) {
                .merge => &.{ "merge", "--no-edit" },
                .rebase => &.{"rebase"},
                .cherry_pick => &.{"cherry-pick"},
                else => &.{ "cherry-pick", "--no-commit" },
            }) catch return;
            argv.appendSlice(a, args) catch return;
            argv.append(a, name) catch return;
            const verb = switch (p.pick) {
                .merge => "Merging",
                .rebase => "Rebasing onto",
                else => "Cherry-picking",
            };
            run(p, argv.items, .{ .label = std.fmt.allocPrint(a, "{s} {s}", .{ verb, name }) catch verb, .batch = true });
        },
        .reset_soft, .reset_mixed, .reset_hard => resetTo(p, p.pick, name),
        .upstream => run(p, &.{ "branch", std.fmt.allocPrint(a, "--set-upstream-to={s}", .{name}) catch return }, .{ .label = "Setting the upstream", .done = std.fmt.allocPrint(a, "Upstream set to {s}", .{name}) catch "" }),
        .log => logOf(p, args, &.{name}, name),
        .delete_tag => run(p, &.{ "tag", "-d", name }, .{ .label = "Deleting the tag", .done = std.fmt.allocPrint(a, "Deleted tag {s}", .{name}) catch "" }),
    }
}

/// Asks for a name; `argv` has an empty slot (at `slot`) for it.
fn ask(p: *Panel, kind: PromptKind, label: []const u8, initial: []const u8, argv: []const []const u8, run: git_status_ui.Run) void {
    const gpa = p.ws.gpa;
    if (p.pending) |old| old.destroy(gpa);
    const req = Request.create(gpa) catch return;
    const a = req.alloc();
    const owned = a.alloc([]const u8, argv.len) catch return req.destroy(gpa);
    for (argv, owned) |s, *o| o.* = a.dupe(u8, s) catch "";
    req.argv = owned;
    req.run = run;
    req.run.label = a.dupe(u8, run.label) catch "";
    req.run.done = a.dupe(u8, run.done) catch "";
    p.pending = req;
    p.ws.openPrompt(label, @intFromEnum(kind), initial);
    if (p.ws.overlay) |o| o.input.selectAll();
}

/// A prompt's answer: fills the pending command's empty slot and runs it.
pub fn prompted(p: *Panel, kind: PromptKind, input: []const u8) void {
    const gpa = p.ws.gpa;
    const req = p.pending orelse return;
    p.pending = null;
    defer req.destroy(gpa);
    const a = req.alloc();
    var argv: std.ArrayList([]const u8) = .empty;
    if (kind == .stash_message) {
        // An empty message is git's own ("WIP on main: …").
        argv.appendSlice(a, req.argv) catch return;
        if (input.len > 0) argv.appendSlice(a, &.{ "-m", input }) catch return;
    } else {
        if (input.len == 0) return;
        for (req.argv) |s| argv.append(a, if (s.len == 0) input else s) catch return;
    }
    git_status_ui.run(p, argv.items, req.run);
}

// ---- Push, pull, fetch ----------------------------------------------------------------

fn pushDefault(p: *Panel, args: Args, _: u8) void {
    pushTo(p, args, false);
}

fn pushUpstream(p: *Panel, args: Args, _: u8) void {
    pushTo(p, args, true);
}

fn pushTo(p: *Panel, args: Args, need_upstream: bool) void {
    const branch = currentBranch(p) orelse return;
    const st = p.status().?;
    var arena = std.heap.ArenaAllocator.init(p.ws.gpa);
    defer arena.deinit();
    const a = arena.allocator();
    if (st.branch.upstream) |up| if (splitUpstream(up)) |u| {
        const refspec = std.fmt.allocPrint(a, "HEAD:refs/heads/{s}", .{u.branch}) catch return;
        const label = std.fmt.allocPrint(a, "Pushing {s} to {s}", .{ branch, up }) catch "Pushing";
        const done = std.fmt.allocPrint(a, "Pushed {s} to {s}", .{ branch, up }) catch "";
        return runWith(p, &.{ "push", "-v" }, args, &.{ u.remote, refspec }, .{ .label = label, .done = done, .batch = true });
    };
    if (need_upstream) return p.ws.setStatus("{s} has no upstream (P p pushes to origin and sets it)", .{branch});
    const label = std.fmt.allocPrint(a, "Pushing {s} to origin/{s}", .{ branch, branch }) catch "Pushing";
    const done = std.fmt.allocPrint(a, "Pushed {s} to origin/{s} (now its upstream)", .{ branch, branch }) catch "";
    runWith(p, &.{ "push", "-v", "--set-upstream" }, args, &.{ "origin", "HEAD" }, .{ .label = label, .done = done, .batch = true });
}

fn pushTags(p: *Panel, args: Args, _: u8) void {
    runWith(p, &.{ "push", "-v", "--tags" }, args, &.{upstreamRemote(p)}, .{ .label = "Pushing tags", .done = "Pushed tags", .batch = true });
}

/// The upstream's remote, else origin.
fn upstreamRemote(p: *Panel) []const u8 {
    const st = p.status() orelse return "origin";
    const up = st.branch.upstream orelse return "origin";
    return if (splitUpstream(up)) |u| u.remote else "origin";
}

fn pull(p: *Panel, args: Args, _: u8) void {
    const branch = currentBranch(p) orelse return;
    const st = p.status().?;
    const up = st.branch.upstream orelse return p.ws.setStatus("{s} has no upstream to pull from (b u sets one)", .{branch});
    var buf: [256]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Pulling from {s}", .{up}) catch "Pulling";
    runWith(p, &.{ "pull", "-v" }, args, &.{}, .{ .label = label, .done = "Pulled", .batch = true });
}

fn fetchUpstream(p: *Panel, args: Args, _: u8) void {
    const remote = upstreamRemote(p);
    var buf: [256]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Fetching {s}", .{remote}) catch "Fetching";
    runWith(p, &.{ "fetch", "-v" }, args, &.{remote}, .{ .label = label, .done = "Fetched", .batch = true });
}

fn fetchAll(p: *Panel, args: Args, _: u8) void {
    runWith(p, &.{ "fetch", "-v", "--all" }, args, &.{}, .{ .label = "Fetching all remotes", .done = "Fetched all remotes", .batch = true });
}

// ---- Branches and tags ------------------------------------------------------------------

fn branchCheckout(p: *Panel, args: Args, _: u8) void {
    pick(p, .checkout, args);
}

fn branchCreate(p: *Panel, _: Args, _: u8) void {
    const from = caretCommit(p);
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Create and check out a branch (from {s}):", .{from orelse "HEAD"}) catch "New branch:";
    const argv: []const []const u8 = if (from) |c| &.{ "checkout", "-b", "", c } else &.{ "checkout", "-b", "" };
    ask(p, .branch_create, label, "", argv, .{ .label = "Creating the branch", .done = "Created and checked out the branch" });
}

fn branchNew(p: *Panel, _: Args, _: u8) void {
    const from = caretCommit(p);
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Create a branch at {s}:", .{from orelse "HEAD"}) catch "New branch:";
    const argv: []const []const u8 = if (from) |c| &.{ "branch", "", c } else &.{ "branch", "" };
    ask(p, .branch_new, label, "", argv, .{ .label = "Creating the branch", .done = "Created the branch" });
}

fn branchRename(p: *Panel, _: Args, _: u8) void {
    const branch = currentBranch(p) orelse return;
    var buf: [256]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Rename {s} to:", .{branch}) catch "Rename to:";
    ask(p, .branch_rename, label, branch, &.{ "branch", "-m", "" }, .{ .label = "Renaming the branch", .done = "Renamed the branch" });
}

fn branchDelete(p: *Panel, args: Args, _: u8) void {
    pick(p, .delete_branch, args);
}

fn branchUpstream(p: *Panel, args: Args, _: u8) void {
    _ = currentBranch(p) orelse return;
    pick(p, .upstream, args);
}

fn tagCreate(p: *Panel, _: Args, _: u8) void {
    const at = caretCommit(p);
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Tag {s} as:", .{at orelse "HEAD"}) catch "Tag name:";
    const argv: []const []const u8 = if (at) |c| &.{ "tag", "", c } else &.{ "tag", "" };
    ask(p, .tag_name, label, "", argv, .{ .label = "Tagging", .done = "Tagged" });
}

fn tagDelete(p: *Panel, args: Args, _: u8) void {
    pick(p, .delete_tag, args);
}

// ---- Stashes ------------------------------------------------------------------------------

fn stashPush(p: *Panel, args: Args, k: u8) void {
    var arena = std.heap.ArenaAllocator.init(p.ws.gpa);
    defer arena.deinit();
    const a = arena.allocator();
    var argv: std.ArrayList([]const u8) = .empty;
    argv.appendSlice(a, &.{ "stash", "push" }) catch return;
    switch (k) {
        'i' => argv.append(a, "--staged") catch return,
        'w' => argv.append(a, "--keep-index") catch return,
        else => {},
    }
    args.appendTo(&argv, a) catch return;
    ask(p, .stash_message, "Stash message (empty: git's \"WIP on …\"):", "", argv.items, .{ .label = "Stashing", .done = "Stashed" });
}

/// The stash at the caret, else the newest.
fn stashTarget(p: *Panel) ?[]const u8 {
    if (caretStash(p)) |s| return s;
    const st = statusOf(p) orelse return null;
    if (st.stashes.len == 0) {
        p.ws.setStatus("No stashes", .{});
        return null;
    }
    return st.stashes[0].name;
}

fn stashUse(p: *Panel, _: Args, k: u8) void {
    const name = stashTarget(p) orelse return;
    var buf: [128]u8 = undefined;
    switch (k) {
        'a' => git_status_ui.run(p, &.{ "stash", "apply", name }, .{ .label = std.fmt.bufPrint(&buf, "Applying {s}", .{name}) catch "Applying", .done = "Applied the stash" }),
        'p' => git_status_ui.run(p, &.{ "stash", "pop", name }, .{ .label = std.fmt.bufPrint(&buf, "Popping {s}", .{name}) catch "Popping", .done = "Popped the stash" }),
        'k' => dropStash(p, name),
        else => git_status_ui.showStash(p, name),
    }
}

/// z k, x on a stash.
pub fn dropStash(p: *Panel, name: []const u8) void {
    var buf: [128]u8 = undefined;
    const q = std.fmt.bufPrint(&buf, "Drop {s}?", .{name}) catch "Drop the stash?";
    confirmRun(p, q, &.{ "stash", "drop", name }, .{ .label = "Dropping the stash", .done = "Dropped the stash" });
}

// ---- Logs and diffs -----------------------------------------------------------------------

fn logOf(p: *Panel, flags: []const []const u8, revs: []const []const u8, what: []const u8) void {
    var arena = std.heap.ArenaAllocator.init(p.ws.gpa);
    defer arena.deinit();
    const a = arena.allocator();
    var argv: std.ArrayList([]const u8) = .empty;
    argv.appendSlice(a, &status_mod.log_argv) catch return;
    argv.appendSlice(a, flags) catch return;
    argv.appendSlice(a, revs) catch return;
    argv.append(a, "--") catch return;
    const title = std.fmt.allocPrint(a, "Log: {s}", .{what}) catch "Log";
    git_status_ui.run(p, argv.items, .{ .title = title, .after = .log });
}

fn flagList(args: Args, a: Allocator) []const []const u8 {
    var list: std.ArrayList([]const u8) = .empty;
    args.appendTo(&list, a) catch {};
    return list.items;
}

fn logCurrent(p: *Panel, args: Args, _: u8) void {
    const st = statusOf(p) orelse return;
    if (st.branch.initial()) return p.ws.setStatus("No commits yet", .{});
    var buf: [256]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buf);
    logOf(p, flagList(args, fba.allocator()), &.{"HEAD"}, st.branch.head orelse "HEAD");
}

fn logOther(p: *Panel, args: Args, _: u8) void {
    pick(p, .log, args);
}

fn logBranches(p: *Panel, args: Args, _: u8) void {
    var buf: [256]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buf);
    logOf(p, flagList(args, fba.allocator()), &.{"--branches"}, "all branches");
}

fn logAll(p: *Panel, args: Args, _: u8) void {
    var buf: [256]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buf);
    logOf(p, flagList(args, fba.allocator()), &.{"--all"}, "all references");
}

fn diffDwim(p: *Panel, _: Args, k: u8) void {
    const t = p.view.targetAt(git_status_ui.caretRow(p.editor)) orelse return p.ws.setStatus("No diff here", .{});
    if (k == 'c') {
        const c = p.view.commit(t) orelse return p.ws.setStatus("No commit at the caret", .{});
        return git_status_ui.showCommit(p, c.hash);
    }
    git_status_ui.diffAt(p, t);
}

const diff_opts = [_][]const u8{ "--no-optional-locks", "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/" };

fn diffStaged(p: *Panel, args: Args, _: u8) void {
    runWith(p, &(diff_opts ++ [_][]const u8{"--cached"}), args, &.{}, .{ .title = "Diff: staged", .after = .diff });
}

fn diffUnstaged(p: *Panel, args: Args, _: u8) void {
    runWith(p, &diff_opts, args, &.{}, .{ .title = "Diff: unstaged", .after = .diff });
}

fn diffWorktree(p: *Panel, args: Args, _: u8) void {
    runWith(p, &diff_opts, args, &.{"HEAD"}, .{ .title = "Diff: HEAD..worktree", .after = .diff });
}

// ---- Merge, rebase, reset, cherry-pick, revert ------------------------------------------------

fn mergePick(p: *Panel, args: Args, _: u8) void {
    _ = currentBranch(p) orelse return;
    pick(p, .merge, args);
}

fn mergeCommit(p: *Panel, _: Args, _: u8) void {
    git_status_ui.run(p, &.{ "commit", "--no-edit" }, .{ .label = "Committing the merge", .done = "Merged", .batch = true });
}

fn mergeAbort(p: *Panel, _: Args, _: u8) void {
    confirmRun(p, "Abort the merge (and lose its conflict resolutions)?", &.{ "merge", "--abort" }, .{ .label = "Aborting the merge", .done = "Merge aborted" });
}

fn rebaseUpstream(p: *Panel, args: Args, _: u8) void {
    const branch = currentBranch(p) orelse return;
    const st = p.status().?;
    const up = st.branch.upstream orelse return p.ws.setStatus("{s} has no upstream (r e picks what to rebase onto)", .{branch});
    var buf: [256]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Rebasing {s} onto {s}", .{ branch, up }) catch "Rebasing";
    runWith(p, &.{"rebase"}, args, &.{up}, .{ .label = label, .done = "Rebased", .batch = true });
}

fn rebasePick(p: *Panel, args: Args, _: u8) void {
    _ = currentBranch(p) orelse return;
    pick(p, .rebase, args);
}

/// continue / skip / abort of a rebase, cherry-pick or revert that stopped.
fn sequence(p: *Panel, _: Args, k: u8) void {
    const what = p.view.in_progress orelse return p.ws.setStatus("Nothing is in progress", .{});
    const cmd: []const u8 = if (std.mem.eql(u8, what, "Rebasing"))
        "rebase"
    else if (std.mem.eql(u8, what, "Cherry-picking"))
        "cherry-pick"
    else if (std.mem.eql(u8, what, "Reverting"))
        "revert"
    else
        return p.ws.setStatus("{s} is in progress; it has no continue, skip or abort here", .{what});
    const step: []const u8 = switch (k) {
        'r', 'c' => "--continue",
        's' => "--skip",
        else => "--abort",
    };
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "git {s} {s}", .{ cmd, step }) catch cmd;
    if (std.mem.eql(u8, step, "--abort")) {
        var qbuf: [128]u8 = undefined;
        const q = std.fmt.bufPrint(&qbuf, "Abort the {s}?", .{cmd}) catch "Abort?";
        return confirmRun(p, q, &.{ cmd, step }, .{ .label = label, .batch = true });
    }
    git_status_ui.run(p, &.{ cmd, step }, .{ .label = label, .batch = true });
}

fn reset(p: *Panel, _: Args, k: u8) void {
    _ = currentBranch(p) orelse return;
    const what: Pick = switch (k) {
        's' => .reset_soft,
        'h' => .reset_hard,
        else => .reset_mixed,
    };
    if (caretCommit(p)) |c| return resetTo(p, what, c);
    pick(p, what, noArgs());
}

fn resetTo(p: *Panel, what: Pick, rev: []const u8) void {
    const branch = if (p.status()) |st| st.branch.head orelse "HEAD" else "HEAD";
    var buf: [256]u8 = undefined;
    switch (what) {
        .reset_hard => {
            const q = std.fmt.bufPrint(&buf, "Reset {s} hard to {s}? Uncommitted changes are lost.", .{ branch, rev }) catch "Reset hard?";
            confirmRun(p, q, &.{ "reset", "--hard", rev }, .{ .label = "Resetting", .done = "Reset hard" });
        },
        .reset_soft => git_status_ui.run(p, &.{ "reset", "--soft", rev }, .{ .label = std.fmt.bufPrint(&buf, "Resetting {s} to {s}", .{ branch, rev }) catch "Resetting", .done = "Reset (soft)" }),
        else => git_status_ui.run(p, &.{ "reset", "--mixed", "-q", rev }, .{ .label = std.fmt.bufPrint(&buf, "Resetting {s} to {s}", .{ branch, rev }) catch "Resetting", .done = "Reset" }),
    }
}

fn cherryPick(p: *Panel, args: Args, k: u8) void {
    const what: Pick = if (k == 'a') .cherry_apply else .cherry_pick;
    const c = caretCommit(p) orelse return pick(p, what, args);
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Cherry-picking {s}", .{c}) catch "Cherry-picking";
    if (what == .cherry_apply) {
        git_status_ui.run(p, &.{ "cherry-pick", "--no-commit", c }, .{ .label = label, .batch = true });
    } else git_status_ui.run(p, &.{ "cherry-pick", c }, .{ .label = label, .batch = true });
}

fn revert(p: *Panel, _: Args, k: u8) void {
    const c = caretCommit(p) orelse return p.ws.setStatus("Put the caret on a commit to revert it", .{});
    var buf: [128]u8 = undefined;
    const label = std.fmt.bufPrint(&buf, "Reverting {s}", .{c}) catch "Reverting";
    if (k == 'v') {
        git_status_ui.run(p, &.{ "revert", "--no-commit", c }, .{ .label = label, .batch = true });
    } else git_status_ui.run(p, &.{ "revert", "--no-edit", c }, .{ .label = label, .batch = true });
}
