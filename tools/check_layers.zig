//! `zig build layers`: checks that imports only point down the layers,
//!   src/framework  ←  src/model  ←  src/app  ←  src/main.zig, src/app.zig
//! so each layer can be understood (and extended) without the ones above it.
//! src/server (jiffy-edit-server, plus its root src/server.zig) sits beside
//! app: it may use framework and model, and nothing else may import it.
//! Prints every violation as "file: imports path (layer → layer)".

const std = @import("std");

const Layer = enum(u8) {
    framework,
    model,
    /// The remote server: no app (GUI) code, so it builds without GUI libraries.
    server,
    app,
    /// src/main.zig and src/app.zig wire everything together.
    root,

    fn of(rel: []const u8) Layer {
        if (std.mem.startsWith(u8, rel, "framework/")) return .framework;
        if (std.mem.startsWith(u8, rel, "model/")) return .model;
        if (std.mem.startsWith(u8, rel, "server/") or std.mem.eql(u8, rel, "server.zig")) return .server;
        if (std.mem.startsWith(u8, rel, "app/")) return .app;
        return .root;
    }
};

pub fn main(init: std.process.Init) !u8 {
    const gpa = init.gpa;
    const io = init.io;
    var args = try init.minimal.args.iterateAllocator(gpa);
    defer args.deinit();
    _ = args.skip();
    const src_root = args.next() orelse "src";

    var dir = try std.Io.Dir.cwd().openDir(io, src_root, .{ .iterate = true });
    defer dir.close(io);
    var walker = try dir.walk(gpa);
    defer walker.deinit();

    var violations: usize = 0;
    var files: usize = 0;
    while (try walker.next(io)) |entry| {
        if (entry.kind != .file or !std.mem.endsWith(u8, entry.path, ".zig")) continue;
        files += 1;
        const text = try dir.readFileAlloc(io, entry.path, gpa, .limited(16 << 20));
        defer gpa.free(text);
        const from = Layer.of(entry.path);
        const here = std.fs.path.dirname(entry.path) orelse "";
        var pos: usize = 0;
        while (std.mem.indexOfPos(u8, text, pos, "@import(\"")) |at| {
            const start = at + "@import(\"".len;
            const end = std.mem.indexOfScalarPos(u8, text, start, '"') orelse break;
            pos = end;
            const target = text[start..end];
            if (!std.mem.endsWith(u8, target, ".zig")) continue; // std, c, build_options
            const resolved = try std.fs.path.resolvePosix(gpa, &.{ here, target });
            defer gpa.free(resolved);
            const to = Layer.of(resolved);
            if (@intFromEnum(to) > @intFromEnum(from) or (to == .server and from != .server)) {
                violations += 1;
                std.debug.print("{s}: imports {s} ({t} → {t})\n", .{ entry.path, target, from, to });
            }
        }
    }
    if (violations > 0) {
        std.debug.print("{d} layering violation(s): framework ← model ← app imports must point downward\n", .{violations});
        return 1;
    }
    std.debug.print("layers ok ({d} files)\n", .{files});
    return 0;
}
