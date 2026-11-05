const std = @import("std");

/// System libraries, resolved through pkg-config. fontconfig's flags bring
/// freetype's along; on macOS freetype is not named again because dyld
/// refuses a binary that links a dylib twice.
const mac_libs = [_][]const u8{ "harfbuzz", "fontconfig" };

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    if (!target.result.os.tag.isDarwin()) {
        // The Linux backends (wayland.zig, x11.zig, egl.zig) are copied from
        // jiffy-edit but this spike never built them: they need generated
        // Wayland protocol headers, see docs/ZIG-EVALUATION.md.
        @panic("only macOS is wired up in this spike's build.zig");
    }

    // Apple's SDK: $SDKROOT, else Xcode's / the Command Line Tools'.
    const sdk = b.graph.environ_map.get("SDKROOT") orelse
        std.mem.trim(u8, b.run(&.{ "xcrun", "--sdk", "macosx", "--show-sdk-path" }), " \n");

    // C API surface used from Zig, translated once into the `c` module.
    const translate_c = b.addTranslateC(.{
        .root_source_file = b.path("src/c.h"),
        .target = target,
        .optimize = optimize,
    });
    translate_c.addIncludePath(b.path("src/framework/platform"));
    for (mac_libs) |lib| translate_c.linkSystemLibrary(lib, .{});

    const mod = b.createModule(.{
        .root_source_file = b.path("src/main.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .imports = &.{.{ .name = "c", .module = translate_c.createModule() }},
    });
    mod.addSystemFrameworkPath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "System/Library/Frameworks" }) });
    mod.addSystemIncludePath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "usr/include" }) });
    mod.addLibraryPath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "usr/lib" }) });
    mod.addCSourceFile(.{ .file = b.path("src/framework/platform/cocoa.m"), .flags = &.{ "-fobjc-arc", "-DGL_SILENCE_DEPRECATION" } });
    for ([_][]const u8{ "Cocoa", "OpenGL", "QuartzCore" }) |f| mod.linkFramework(f, .{});
    for (mac_libs) |lib| mod.linkSystemLibrary(lib, .{});

    const exe = b.addExecutable(.{ .name = "cs", .root_module = mod });
    b.installArtifact(exe);

    const run = b.addRunArtifact(exe);
    run.step.dependOn(b.getInstallStep());
    run.addPassthruArgs();
    b.step("run", "Run the editor").dependOn(&run.step);

    const tests = b.addTest(.{ .root_module = mod });
    b.step("test", "Run unit tests").dependOn(&b.addRunArtifact(tests).step);
}
