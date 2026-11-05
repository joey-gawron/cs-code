const std = @import("std");

const Protocol = struct { name: []const u8, xml: []const u8 };

/// Wayland protocol extensions bindings are generated for with wayland-scanner.
const protocols = [_]Protocol{
    .{ .name = "xdg-shell", .xml = "stable/xdg-shell/xdg-shell.xml" },
    .{ .name = "xdg-decoration-unstable-v1", .xml = "unstable/xdg-decoration/xdg-decoration-unstable-v1.xml" },
    .{ .name = "viewporter", .xml = "stable/viewporter/viewporter.xml" },
    .{ .name = "fractional-scale-v1", .xml = "staging/fractional-scale/fractional-scale-v1.xml" },
    .{ .name = "cursor-shape-v1", .xml = "staging/cursor-shape/cursor-shape-v1.xml" },
    // cursor-shape-v1 references zwp_tablet_tool_v2.
    .{ .name = "tablet-v2", .xml = "stable/tablet/tablet-v2.xml" },
    .{ .name = "text-input-unstable-v3", .xml = "unstable/text-input/text-input-unstable-v3.xml" },
    .{ .name = "primary-selection-unstable-v1", .xml = "unstable/primary-selection/primary-selection-unstable-v1.xml" },
};

/// System libraries, resolved through pkg-config. fontconfig's flags bring
/// freetype's along; on macOS freetype is not named again because dyld
/// refuses a binary that links a dylib twice.
const portable_libs = [_][]const u8{ "harfbuzz", "fontconfig" };
const linux_libs = [_][]const u8{ "wayland-client", "wayland-egl", "egl", "xkbcommon", "freetype2" };
/// Translated separately: Xlib's macros clash with Wayland's names.
const x11_libs = [_][]const u8{ "x11", "x11-xcb", "xcb", "xcursor", "xrandr", "xi", "xkbcommon-x11" };

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const macos = target.result.os.tag.isDarwin();
    const libs: []const []const u8 = if (macos) &portable_libs else &(linux_libs ++ portable_libs);

    // C API surface used from Zig, translated once into the `c` module.
    const translate_c = b.addTranslateC(.{
        .root_source_file = b.path("src/c.h"),
        .target = target,
        .optimize = optimize,
    });
    for (libs) |lib| translate_c.linkSystemLibrary(lib, .{});

    const options = b.addOptions();
    // xdg-toplevel-icon is not generated here: windows get no icon from pixels.
    options.addOption(bool, "toplevel_icon", false);

    const mod = b.createModule(.{
        .root_source_file = b.path("src/main.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .imports = &.{.{ .name = "build_options", .module = options.createModule() }},
    });

    if (macos) {
        // Apple's SDK: $SDKROOT, else Xcode's / the Command Line Tools'.
        const sdk = b.graph.environ_map.get("SDKROOT") orelse
            std.mem.trim(u8, b.run(&.{ "xcrun", "--sdk", "macosx", "--show-sdk-path" }), " \n");
        translate_c.addIncludePath(b.path("src/framework/platform"));
        mod.addSystemFrameworkPath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "System/Library/Frameworks" }) });
        mod.addSystemIncludePath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "usr/include" }) });
        mod.addLibraryPath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "usr/lib" }) });
        mod.addCSourceFile(.{ .file = b.path("src/framework/platform/cocoa.m"), .flags = &.{ "-fobjc-arc", "-DGL_SILENCE_DEPRECATION" } });
        for ([_][]const u8{ "Cocoa", "OpenGL", "QuartzCore" }) |f| mod.linkFramework(f, .{});
    } else {
        const protocols_dir = b.option([]const u8, "wayland-protocols", "wayland-protocols share dir") orelse
            b.graph.environ_map.get("WAYLAND_PROTOCOLS_DIR") orelse
            std.mem.trim(u8, b.run(&.{ "pkg-config", "--variable=pkgdatadir", "wayland-protocols" }), " \n");

        // Generated Wayland protocol headers and glue code.
        const gen = b.addWriteFiles();
        for (protocols) |p| mod.addCSourceFile(.{ .file = addProtocol(b, gen, protocols_dir, p), .flags = &.{"-std=c11"} });
        translate_c.addIncludePath(gen.getDirectory());
        mod.addIncludePath(gen.getDirectory());

        const translate_x11 = b.addTranslateC(.{
            .root_source_file = b.path("src/c_x11.h"),
            .target = target,
            .optimize = optimize,
        });
        for (x11_libs) |lib| translate_x11.linkSystemLibrary(lib, .{});
        mod.addImport("cx11", translate_x11.createModule());
        for (x11_libs) |lib| mod.linkSystemLibrary(lib, .{});
    }
    mod.addImport("c", translate_c.createModule());
    for (libs) |lib| mod.linkSystemLibrary(lib, .{});

    const exe = b.addExecutable(.{ .name = "cs", .root_module = mod });
    b.installArtifact(exe);

    const run = b.addRunArtifact(exe);
    run.step.dependOn(b.getInstallStep());
    run.addPassthruArgs();
    b.step("run", "Run the editor").dependOn(&run.step);

    const tests = b.addTest(.{ .root_module = mod });
    b.step("test", "Run unit tests").dependOn(&b.addRunArtifact(tests).step);
}

/// Generates a protocol's client header into `gen` and returns its glue code.
fn addProtocol(b: *std.Build, gen: *std.Build.Step.WriteFile, protocols_dir: []const u8, p: Protocol) std.Build.LazyPath {
    const xml: std.Build.LazyPath = .{ .cwd_relative = b.pathJoin(&.{ protocols_dir, p.xml }) };

    const header = b.addSystemCommand(&.{ "wayland-scanner", "client-header" });
    header.addFileArg(xml);
    const header_name = b.fmt("{s}-client-protocol.h", .{p.name});
    _ = gen.addCopyFile(header.addOutputFileArg(header_name), header_name);

    const code = b.addSystemCommand(&.{ "wayland-scanner", "private-code" });
    code.addFileArg(xml);
    return code.addOutputFileArg(b.fmt("{s}-protocol.c", .{p.name}));
}
