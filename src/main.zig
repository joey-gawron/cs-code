//! cs-zig: one window with an editor surface on a file, the built-in sample, or a synthetic
//! buffer. The Zig spike's counterpart of the Rust spike's `cs` binary.
//!
//! ```text
//! cs [FILE] [--generate N] [--long-line N] [--demo SCRIPT] [--size W,H] [--screenshot PATH.ppm] [--headless]
//! ```
//!
//! `CS_FRAME_STATS=1` logs startup milestones, each frame's time (split into building the scene,
//! issuing the GL draw and presenting) and a summary on exit (Ctrl-C and closing the window
//! included). `CS_DEMO_INTERVAL_MS` changes the 100 ms between demo steps.

const std = @import("std");
const builtin = @import("builtin");
const c = @import("c");
const gl = @import("framework/gpu/gl.zig");
const Atlas = @import("framework/gpu/atlas.zig").Atlas;
const Renderer = @import("framework/gpu/renderer.zig").Renderer;
const Scene = @import("framework/gpu/scene.zig").Scene;
const platform = @import("framework/platform/platform.zig");
const window_mod = @import("framework/platform/window.zig");
const headless_mod = @import("framework/platform/headless.zig");
const text_mod = @import("framework/text/text_system.zig");
const Buffer = @import("model/text/buffer.zig").Buffer;
const Editor = @import("editor/editor.zig").Editor;
const demo = @import("editor/demo.zig");
const sample = @import("editor/sample.zig");
const stats_mod = @import("editor/stats.zig");

const usage = "usage: cs [FILE] [--generate N] [--long-line N] [--demo SCRIPT] [--size W,H] [--screenshot PATH.ppm] [--headless]";

const Source = union(enum) {
    sample,
    file: []const u8,
    generate: usize,
    long_line: usize,
};

var interrupted: bool = false;

/// Either a real window or an offscreen GL context of the same size (`--headless`, for machines
/// whose display is locked or asleep: Cocoa stops drawing to an occluded window).
const Surface = union(enum) {
    window: window_mod.Window,
    headless: struct { h: headless_mod.Headless, w: f32, ht: f32 },

    fn destroy(s: *Surface) void {
        switch (s.*) {
            .window => |w| w.destroy(),
            .headless => |*h| h.h.deinit(),
        }
    }
    fn scale(s: *Surface) f32 {
        return switch (s.*) {
            .window => |w| w.scale(),
            .headless => 1,
        };
    }
    fn logicalSize(s: *Surface) platform.Vec2 {
        return switch (s.*) {
            .window => |w| w.logicalSize(),
            .headless => |h| .{ .x = h.w, .y = h.ht },
        };
    }
    fn bufferSize(s: *Surface) struct { i32, i32 } {
        return switch (s.*) {
            .window => |w| w.bufferSize(),
            .headless => |h| .{ @intFromFloat(h.w), @intFromFloat(h.ht) },
        };
    }
    fn pollEvents(s: *Surface, timeout_ms: i32) !void {
        switch (s.*) {
            .window => |w| try w.pollEvents(timeout_ms, &.{}),
            .headless => _ = c.usleep(@intCast(@max(0, timeout_ms) * 1000)),
        }
    }
    fn events(s: *Surface) []const platform.Event {
        return switch (s.*) {
            .window => |w| w.events(),
            .headless => &.{},
        };
    }
    fn canRender(s: *Surface) bool {
        return switch (s.*) {
            .window => |w| w.canRender(),
            .headless => true,
        };
    }
    fn present(s: *Surface) void {
        switch (s.*) {
            .window => |w| w.present(),
            .headless => gl.p.Finish(),
        }
    }
    fn setTextInputRect(s: *Surface, r: platform.Rect) void {
        if (s.* == .window) s.window.setTextInputRect(r, true);
    }
};

fn onSigint(_: c_int) callconv(.c) void {
    // A second Ctrl-C exits at once, in case the loop is stuck.
    if (interrupted) std.process.exit(130);
    interrupted = true;
}

const log_to_stderr = std.debug.print;

pub fn main(init: std.process.Init) !void {
    const started = stats_mod.nowNs();
    const gpa = init.gpa;
    const io = init.io;
    var arena_state: std.heap.ArenaAllocator = .init(gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();
    const args = try init.minimal.args.toSlice(init.arena.allocator());

    var source: Source = .sample;
    var script: ?[]const u8 = null;
    var win_w: i32 = 1000;
    var win_h: i32 = 760;
    var screenshot: ?[]const u8 = null;
    var use_headless = false;
    var i: usize = 1;
    while (i < args.len) : (i += 1) {
        const arg = args[i];
        if (std.mem.eql(u8, arg, "-h") or std.mem.eql(u8, arg, "--help")) {
            log_to_stderr("{s}\n", .{usage});
            return;
        } else if (std.mem.eql(u8, arg, "--headless")) {
            use_headless = true;
        } else if (std.mem.eql(u8, arg, "--generate") or std.mem.eql(u8, arg, "--long-line") or std.mem.eql(u8, arg, "--demo") or
            std.mem.eql(u8, arg, "--size") or std.mem.eql(u8, arg, "--screenshot"))
        {
            i += 1;
            if (i >= args.len) fail("{s} needs a value\n{s}", .{ arg, usage });
            const value = args[i];
            if (std.mem.eql(u8, arg, "--generate")) {
                source = .{ .generate = std.fmt.parseInt(usize, value, 10) catch fail("--generate takes a count", .{}) };
            } else if (std.mem.eql(u8, arg, "--long-line")) {
                source = .{ .long_line = std.fmt.parseInt(usize, value, 10) catch fail("--long-line takes a count", .{}) };
            } else if (std.mem.eql(u8, arg, "--demo")) {
                script = value;
            } else if (std.mem.eql(u8, arg, "--screenshot")) {
                screenshot = value;
            } else {
                const comma = std.mem.indexOfScalar(u8, value, ',') orelse fail("--size takes W,H", .{});
                win_w = std.fmt.parseInt(i32, value[0..comma], 10) catch fail("--size takes W,H", .{});
                win_h = std.fmt.parseInt(i32, value[comma + 1 ..], 10) catch fail("--size takes W,H", .{});
            }
        } else if (std.mem.startsWith(u8, arg, "--")) {
            fail("unknown option {s}\n{s}", .{ arg, usage });
        } else {
            source = .{ .file = arg };
        }
    }
    const frame_stats = if (init.environ_map.get("CS_FRAME_STATS")) |v| std.mem.eql(u8, v, "1") else false;
    const demo_interval_ms: i64 = if (init.environ_map.get("CS_DEMO_INTERVAL_MS")) |v| std.fmt.parseInt(i64, v, 10) catch 100 else 100;

    // The text, then the buffer: "buffer ready" covers generating or reading the text and
    // building the rope, as the Rust side's startup line does.
    var title: []const u8 = "sample";
    const text: []u8 = switch (source) {
        .sample => try sample.sampleText(gpa),
        .file => |path| blk: {
            title = path;
            break :blk try std.Io.Dir.cwd().readFileAlloc(io, path, gpa, .limited(1 << 30));
        },
        .generate => |n| blk: {
            title = try std.fmt.allocPrint(arena, "generated ({d} lines)", .{n});
            break :blk try sample.generate(gpa, n);
        },
        .long_line => |n| blk: {
            title = try std.fmt.allocPrint(arena, "one line of {d} characters", .{n});
            break :blk try sample.longLine(gpa, n);
        },
    };
    var buffer = try Buffer.initText(gpa, 0, text);
    gpa.free(text);
    buffer.tab_size = 4;
    if (frame_stats) log_to_stderr("startup: buffer of {d} lines ready at {d:.1} ms\n", .{ buffer.lineCount(), stats_mod.ms(stats_mod.nowNs() - started) });

    var steps: []demo.Step = &.{};
    if (script) |s| {
        const parsed = try demo.parse(arena, s);
        for (parsed.errors) |e| log_to_stderr("unknown demo step: \"{s}\"\n", .{e});
        steps = parsed.steps;
    }

    if (frame_stats) log_to_stderr("startup: opening the window at {d:.1} ms\n", .{stats_mod.ms(stats_mod.nowNs() - started)});
    const title_z = try std.fmt.allocPrintSentinel(arena, "cs-zig: {s}", .{title}, 0);
    var window: Surface = if (use_headless)
        .{ .headless = .{ .h = try headless_mod.Headless.init(@intCast(win_w), @intCast(win_h)), .w = @floatFromInt(win_w), .ht = @floatFromInt(win_h) } }
    else
        .{ .window = try window_mod.Window.create(gpa, .{ .title = title_z, .width = win_w, .height = win_h }, null) };
    defer window.destroy();

    // GL objects, after the window made its context current.
    var atlas = Atlas.init(1024);
    defer atlas.deinit();
    var renderer = try Renderer.init();
    defer renderer.deinit();
    var text_system = try text_mod.TextSystem.init(gpa, &atlas);
    defer text_system.deinit();
    var scale = window.scale();
    text_system.setScale(scale);
    text_system.resetGlyphs();
    const font = try text_system.font(.{ .family = "monospace", .size = 14 });
    const editor = try Editor.init(gpa, buffer, &text_system, font);
    defer editor.deinit();
    var scene = Scene.init(gpa);
    defer scene.deinit();
    const size0 = window.logicalSize();
    editor.resize(size0.x, size0.y);
    if (frame_stats) log_to_stderr("startup: window open at {d:.1} ms (scale {d:.1}, {d:.0}x{d:.0} points)\n", .{ stats_mod.ms(stats_mod.nowNs() - started), scale, size0.x, size0.y });

    _ = c.signal(c.SIGINT, onSigint);

    var stats: stats_mod.Stats = .{};
    defer stats.deinit(gpa);
    var frame_no: usize = 0;
    var next_step: usize = 0;
    var step_due_ms: i64 = platform.nowMs() + demo_interval_ms;
    var last_step: ?[]const u8 = null;
    var quit = false;
    var describe_buf: [256]u8 = undefined;

    while (!quit and !interrupted) {
        const now = platform.nowMs();
        // Sleep until the next caret blink toggle or the next demo step.
        var timeout: i64 = blinkRemaining(editor, now);
        if (next_step < steps.len) timeout = @min(timeout, @max(0, step_due_ms - now));
        if (editor.dirty) timeout = 0;
        if (interrupted) break;
        try window.pollEvents(@intCast(timeout));
        for (window.events()) |ev| switch (ev) {
            .key_down => |k| editor.onKey(k) catch |err| log_to_stderr("key: {t}\n", .{err}),
            .text_input => |t| editor.onText(t) catch |err| log_to_stderr("text: {t}\n", .{err}),
            .mouse_down => |m| if (m.button == .left) editor.click(m.pos.x, m.pos.y, m.mods.shift),
            .scroll => |s| {
                const unit: f64 = if (s.unit == .lines) editor.lh * 3 else 1;
                editor.scrollBy(-s.delta.x * unit, -s.delta.y * unit);
            },
            .resize => |r| {
                if (r.scale != scale) {
                    scale = r.scale;
                    text_system.setScale(scale);
                    editor.metricsChanged();
                }
                editor.resize(r.width, r.height);
            },
            .close_requested => quit = true,
            else => {},
        };

        // Demo: one step per interval; the previous step's resulting state is logged just before
        // the next one runs, so it includes what the frame drawing it decided.
        const after_events = platform.nowMs();
        if (next_step < steps.len and after_events >= step_due_ms) {
            if (last_step) |label| logDemo(editor, label, &describe_buf);
            const step = steps[next_step];
            next_step += 1;
            var wait_ms: i64 = 0;
            switch (step) {
                .scroll => |dy| editor.scrollBy(0, dy),
                .scrollx => |dx| editor.scrollBy(dx, 0),
                .key => |k| runKey(editor, k),
                .type => |t| runType(editor, t),
                .click => |p| editor.click(p[0], p[1], false),
                .goto => |line| editor.gotoLine(line),
                .wait => |ms| wait_ms = @intCast(ms),
                .quit => quit = true,
            }
            last_step = stepLabel(arena, step) catch "step";
            step_due_ms = platform.nowMs() + demo_interval_ms + wait_ms;
            if (next_step == steps.len and !quit) {
                // Let the last step's frame happen, log its state and finish.
                step_due_ms = platform.nowMs() + demo_interval_ms;
            }
        } else if (next_step == steps.len and steps.len > 0 and after_events >= step_due_ms and !quit) {
            if (last_step) |label| logDemo(editor, label, &describe_buf);
            quit = true;
        }
        if (quit) {
            if (last_step) |label| logDemo(editor, label, &describe_buf);
            last_step = null;
        }

        // Draw when something changed or the caret blinked.
        const blink_phase = blinkPhase(editor, platform.nowMs());
        if (!editor.dirty and blink_phase == last_blink_phase) continue;
        last_blink_phase = blink_phase;
        if (!window.canRender()) continue;
        const f = drawFrame(editor, &scene, &renderer, &atlas, &text_system, &window, scale);
        editor.dirty = false;
        window.setTextInputRect(editor.caretRect());
        stats.record(gpa, f);
        frame_no += 1;
        if (frame_stats) {
            if (frame_no == 1) log_to_stderr("startup: first frame built at {d:.1} ms, presented at {d:.1} ms\n", .{
                stats_mod.ms(first_built_at - started),
                stats_mod.ms(stats_mod.nowNs() - started),
            });
            log_to_stderr("frame {d}: total {d:.3} ms: build {d:.3} ms, draw {d:.3} ms, present {d:.3} ms\n", .{
                frame_no, stats_mod.ms(f.total()), stats_mod.ms(f.build), stats_mod.ms(f.draw), stats_mod.ms(f.present),
            });
        }
        if (screenshot) |path| {
            if (frame_no >= 2) {
                try writeScreenshot(gpa, io, &window, path);
                quit = true;
            }
        }
    }

    if (frame_stats) printSummary(gpa, &stats);
}

var last_blink_phase: i64 = -1;
var first_built_at: u64 = 0;

fn blinkPhase(editor: *const Editor, now_ms: i64) i64 {
    return @divFloor(now_ms - editor.blink_epoch_ms, 500);
}

fn blinkRemaining(editor: *const Editor, now_ms: i64) i64 {
    const elapsed = now_ms - editor.blink_epoch_ms;
    return @max(1, 500 - @mod(elapsed, 500));
}

fn drawFrame(editor: *Editor, scene: *Scene, renderer: *Renderer, atlas: *Atlas, text: *text_mod.TextSystem, window: *Surface, scale: f32) stats_mod.Frame {
    var f: stats_mod.Frame = .{};
    var attempt: usize = 0;
    while (true) : (attempt += 1) {
        const t0 = stats_mod.nowNs();
        const bw, const bh = window.bufferSize();
        scene.begin(@floatFromInt(bw), @floatFromInt(bh), scale) catch return f;
        editor.paint(scene, platform.nowMs());
        const t1 = stats_mod.nowNs();
        if (first_built_at == 0) first_built_at = t1;
        renderer.draw(scene, atlas, .{ .r = 0.118, .g = 0.118, .b = 0.118, .a = 1 });
        text.endFrame();
        const t2 = stats_mod.nowNs();
        // The frame needed more glyphs than fit in the atlas: grow it and draw again.
        if (text.atlas_full and attempt < 2) {
            atlas.reset(true);
            text.atlas_full = false;
            f.build += t1 - t0;
            f.draw += t2 - t1;
            continue;
        }
        window.present();
        const t3 = stats_mod.nowNs();
        f.build += t1 - t0;
        f.draw += t2 - t1;
        f.present += t3 - t2;
        return f;
    }
}

fn writeScreenshot(gpa: std.mem.Allocator, io: std.Io, window: *Surface, path: []const u8) !void {
    const w, const h = window.bufferSize();
    const uw: usize = @intCast(w);
    const uh: usize = @intCast(h);
    const px = try gpa.alloc(u8, uw * uh * 4);
    defer gpa.free(px);
    gl.p.PixelStorei(gl.PACK_ALIGNMENT, 1);
    gl.p.ReadPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px.ptr);
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(gpa);
    try out.print(gpa, "P6\n{d} {d}\n255\n", .{ uw, uh });
    // GL rows run bottom-up; PPM wants top-down RGB.
    var y = uh;
    while (y > 0) {
        y -= 1;
        const row = px[y * uw * 4 ..][0 .. uw * 4];
        var x: usize = 0;
        while (x < uw) : (x += 1) try out.appendSlice(gpa, row[x * 4 ..][0..3]);
    }
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out.items });
    log_to_stderr("screenshot: {d}x{d} written to {s}\n", .{ uw, uh, path });
}

fn printSummary(gpa: std.mem.Allocator, stats: *const stats_mod.Stats) void {
    const all = stats.summary(gpa, 0, .build);
    const steady = stats.summary(gpa, 5, .build);
    log_to_stderr("frame stats (build, start of frame to end of scene recording): frames {d}, p50 {d:.2} ms, p95 {d:.2} ms, max {d:.2} ms\n", .{ all.frames, all.p50_ms, all.p95_ms, all.max_ms });
    log_to_stderr("  excluding the first 5 frames: frames {d}, p50 {d:.2} ms, p95 {d:.2} ms, max {d:.2} ms\n", .{ steady.frames, steady.p50_ms, steady.p95_ms, steady.max_ms });
    inline for (.{ .{ "draw", stats_mod.Stats.summary(stats, gpa, 0, .draw) }, .{ "present", stats_mod.Stats.summary(stats, gpa, 0, .present) }, .{ "total", stats_mod.Stats.summary(stats, gpa, 0, .total) } }) |entry| {
        const s = entry[1];
        log_to_stderr("  {s}: p50 {d:.2} ms, p95 {d:.2} ms, max {d:.2} ms\n", .{ entry[0], s.p50_ms, s.p95_ms, s.max_ms });
    }
    var ru: std.c.rusage = undefined;
    // ru_maxrss is bytes on macOS, kilobytes on Linux.
    const maxrss_unit: f64 = if (builtin.os.tag.isDarwin()) 1024 * 1024 else 1024;
    if (std.c.getrusage(std.c.rusage.SELF, &ru) == 0) log_to_stderr("peak RSS {d:.1} MB\n", .{@as(f64, @floatFromInt(ru.maxrss)) / maxrss_unit});
}

fn logDemo(editor: *Editor, label: []const u8, buf: []u8) void {
    var w: std.Io.Writer = .fixed(buf);
    editor.describe(&w) catch {};
    log_to_stderr("demo: {s} -> {s}\n", .{ label, w.buffered() });
}

fn stepLabel(arena: std.mem.Allocator, step: demo.Step) ![]const u8 {
    return switch (step) {
        .scroll => |v| try std.fmt.allocPrint(arena, "scroll {d}", .{v}),
        .scrollx => |v| try std.fmt.allocPrint(arena, "scrollx {d}", .{v}),
        .key => |k| try std.fmt.allocPrint(arena, "key {s}", .{k}),
        .type => |t| try std.fmt.allocPrint(arena, "type {s}", .{t}),
        .click => |p| try std.fmt.allocPrint(arena, "click {d},{d}", .{ p[0], p[1] }),
        .goto => |l| try std.fmt.allocPrint(arena, "goto {d}", .{l}),
        .wait => |ms| try std.fmt.allocPrint(arena, "wait {d}", .{ms}),
        .quit => "quit",
    };
}

fn runType(editor: *Editor, text: []const u8) void {
    // One keystroke per character, the way real typing arrives.
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepointSlice()) |cp| editor.onText(cp) catch |err| log_to_stderr("type: {t}\n", .{err});
}

/// Parses `down`, `cmd-down`, `shift-alt-left`, `a`... and runs it as a key press.
fn runKey(editor: *Editor, spec: []const u8) void {
    var mods: platform.Modifiers = .{};
    var rest = spec;
    while (std.mem.indexOfScalar(u8, rest, '-')) |dash| {
        if (dash == 0 or dash + 1 >= rest.len) break;
        const m = rest[0..dash];
        if (std.mem.eql(u8, m, "cmd") or std.mem.eql(u8, m, "super")) mods.super = true else if (std.mem.eql(u8, m, "shift")) mods.shift = true else if (std.mem.eql(u8, m, "alt")) mods.alt = true else if (std.mem.eql(u8, m, "ctrl")) mods.ctrl = true else break;
        rest = rest[dash + 1 ..];
    }
    const named = [_]struct { []const u8, platform.NamedKey }{
        .{ "left", .left },      .{ "right", .right },         .{ "up", .up },
        .{ "down", .down },      .{ "home", .home },           .{ "end", .end },
        .{ "pageup", .page_up }, .{ "pagedown", .page_down },  .{ "enter", .enter },
        .{ "tab", .tab },        .{ "backspace", .backspace }, .{ "delete", .delete },
        .{ "escape", .escape },
    };
    var ev: platform.KeyEvent = undefined;
    for (named) |n| if (std.mem.eql(u8, rest, n[0])) {
        ev = .{ .key = .{ .named = n[1] }, .mods = mods };
        editor.onKey(ev) catch |err| log_to_stderr("key: {t}\n", .{err});
        return;
    };
    const cp = std.unicode.utf8Decode(rest) catch {
        log_to_stderr("demo: bad keystroke \"{s}\"\n", .{spec});
        return;
    };
    ev = .{ .key = .{ .char = cp }, .mods = mods, .text = if (mods.super or mods.ctrl) "" else rest };
    editor.onKey(ev) catch |err| log_to_stderr("key: {t}\n", .{err});
}

fn fail(comptime fmt: []const u8, args: anytype) noreturn {
    log_to_stderr(fmt ++ "\n", args);
    std.process.exit(2);
}

test {
    _ = demo;
    _ = sample;
    _ = stats_mod;
    _ = @import("model/text/rope.zig");
    _ = @import("framework/text/line_index.zig");
    _ = @import("model/text/buffer.zig");
}
