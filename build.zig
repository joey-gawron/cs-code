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

/// Window icons from pixels (wayland-protocols 1.37+), generated only when the
/// wayland-protocols in use has it (`build_options.toplevel_icon`).
const toplevel_icon_protocol: Protocol = .{ .name = "xdg-toplevel-icon-v1", .xml = "staging/xdg-toplevel-icon/xdg-toplevel-icon-v1.xml" };

/// Sizes of the app icon embedded for window icons; src/app/app_icon.zig lists the same ones.
const window_icon_sizes = [_]u32{ 16, 32, 48, 64, 128 };

/// System libraries, resolved through pkg-config (tools/dev-env.sh points
/// PKG_CONFIG_PATH at the ones a Mac lacks; the Nix dev shell provides them on Linux).
const linux_libs = [_][]const u8{ "wayland-client", "wayland-egl", "egl", "xkbcommon" };
const portable_libs = [_][]const u8{ "freetype2", "harfbuzz", "fontconfig", "tree-sitter", "vterm" };
/// Translated separately: Xlib's macros clash with Wayland's names.
const x11_libs = [_][]const u8{ "x11", "x11-xcb", "xcb", "xcursor", "xrandr", "xi", "xkbcommon-x11" };

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const macos = target.result.os.tag.isDarwin();
    // Tree-sitter grammars: -Dgrammars, $CS_ZIG_GRAMMARS, else the folder
    // tools/bundle-grammars.sh fills. They are installed beside the binary.
    const repo_grammars = b.root.joinString(b.allocator, ".grammars") catch @panic("OOM");
    const grammar_dir = b.option([]const u8, "grammars", "Directory of tree-sitter grammars (<lang>/parser)") orelse
        b.graph.environ_map.get("CS_ZIG_GRAMMARS") orelse
        if (std.Io.Dir.cwd().access(b.graph.io, repo_grammars, .{})) |_| repo_grammars else |_| "";
    // fontconfig's pkg-config flags bring freetype's along. On macOS freetype
    // isn't named again: dyld refuses a binary that links a dylib twice.
    const libs: []const []const u8 = if (macos) portable_libs[1..] else &(linux_libs ++ portable_libs);

    const translate_c = b.addTranslateC(.{
        .root_source_file = b.path("src/c.h"),
        .target = target,
        .optimize = optimize,
    });
    for (libs) |lib| translate_c.linkSystemLibrary(lib, .{});

    var editor: Editor = .{ .target = target, .c = undefined, .libs = libs, .grammar_dir = grammar_dir };
    if (macos) {
        editor.sdk = b.graph.environ_map.get("SDKROOT") orelse
            std.mem.trim(u8, b.run(&.{ "xcrun", "--sdk", "macosx", "--show-sdk-path" }), " \n");
        translate_c.addIncludePath(b.path("src/framework/platform"));
    } else {
        const protocols_dir = b.option([]const u8, "wayland-protocols", "wayland-protocols share dir") orelse
            b.graph.environ_map.get("WAYLAND_PROTOCOLS_DIR") orelse
            std.mem.trim(u8, b.run(&.{ "pkg-config", "--variable=pkgdatadir", "wayland-protocols" }), " \n");

        const gen = b.addWriteFiles();
        const protocol_sources = b.allocator.alloc(std.Build.LazyPath, protocols.len) catch @panic("OOM");
        for (protocols, protocol_sources) |p, *src| src.* = addProtocol(b, gen, protocols_dir, p);
        const toplevel_icon_xml = b.pathJoin(&.{ protocols_dir, toplevel_icon_protocol.xml });
        editor.toplevel_icon_source = if (std.Io.Dir.cwd().access(b.graph.io, toplevel_icon_xml, .{})) |_|
            addProtocol(b, gen, protocols_dir, toplevel_icon_protocol)
        else |_|
            null;
        translate_c.addIncludePath(gen.getDirectory());
        if (editor.toplevel_icon_source != null) translate_c.defineCMacro("CS_ZIG_TOPLEVEL_ICON", null);

        const translate_x11 = b.addTranslateC(.{
            .root_source_file = b.path("src/c_x11.h"),
            .target = target,
            .optimize = optimize,
        });
        for (x11_libs) |lib| translate_x11.linkSystemLibrary(lib, .{});
        editor.cx11 = translate_x11.createModule();
        editor.gen = gen;
        editor.protocol_sources = protocol_sources;
    }
    editor.c = translate_c.createModule();

    const exe_mod = editor.module(b, "src/main.zig", optimize);
    const exe = b.addExecutable(.{ .name = "cs", .root_module = exe_mod });
    b.installArtifact(exe);
    // The code font, found by fontconfig next to the binary (fontconfig.zig).
    b.installDirectory(.{ .source_dir = b.path("assets/fonts/fira-code"), .install_dir = .prefix, .install_subdir = "share/cs-zig/fonts" });
    if (grammar_dir.len > 0) b.installDirectory(.{ .source_dir = .{ .cwd_relative = grammar_dir }, .install_dir = .prefix, .install_subdir = "share/cs-zig/grammars" });

    const run_cmd = b.addRunArtifact(exe);
    run_cmd.step.dependOn(b.getInstallStep());
    run_cmd.addPassthruArgs();
    b.step("run", "Run the editor").dependOn(&run_cmd.step);

    // cs-zig-server, the remote side of Remote-SSH editing. No libc and no GUI
    // libraries, so every build is a static binary that runs on any Linux
    // host. `zig build server` cross-builds the architectures the editor
    // uploads to share/cs-zig-server/<arch>/ (it also runs on a Mac's install).
    const server_mod = b.createModule(.{ .root_source_file = b.path("src/server.zig"), .target = target, .optimize = optimize });
    const server_exe = b.addExecutable(.{ .name = "cs-zig-server", .root_module = server_mod });
    // The server is Linux-only (raw syscalls): a Mac install carries the
    // Linux builds the editor deploys to remote hosts instead.
    if (!macos) b.installArtifact(server_exe);
    const server_step = b.step("server", "Build static cs-zig-server binaries for x86_64 and aarch64 Linux");
    const server_optimize = b.option(std.builtin.OptimizeMode, "server-optimize", "Optimize mode of `zig build server` (default small)") orelse .small;
    for ([_]std.Target.Cpu.Arch{ .x86_64, .aarch64 }) |arch| {
        const cross = b.addExecutable(.{ .name = "cs-zig-server", .root_module = b.createModule(.{
            .root_source_file = b.path("src/server.zig"),
            .target = b.resolveTargetQuery(.{ .cpu_arch = arch, .os_tag = .linux }),
            .optimize = server_optimize,
        }) });
        const dest = b.fmt("share/cs-zig-server/{t}", .{arch});
        server_step.dependOn(&b.addInstallArtifact(cross, .{ .dest_dir = .{ .override = .{ .custom = dest } } }).step);
    }
    if (macos) b.getInstallStep().dependOn(server_step);

    // `zig build layers`: imports must point down framework <- model <- app.
    const layers_tool = b.addExecutable(.{ .name = "check_layers", .root_module = b.createModule(.{
        .root_source_file = b.path("tools/check_layers.zig"),
        .target = b.graph.host,
    }) });
    const layers_run = b.addRunArtifact(layers_tool);
    layers_run.addDirectoryArg(b.path("src"));
    b.step("layers", "Check that imports respect framework <- model <- app").dependOn(&layers_run.step);

    const test_step = b.step("test", "Run unit tests");
    const exe_tests = b.addTest(.{ .root_module = exe_mod });
    test_step.dependOn(&b.addRunArtifact(exe_tests).step);
    const core_tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("src/model/text/tests.zig"),
        .target = target,
        .optimize = optimize,
    }) });
    test_step.dependOn(&b.addRunArtifact(core_tests).step);
    test_step.dependOn(&layers_run.step);

    // Remote tests: protocol units plus client <-> cs-zig-server integration
    // (the server runs as a local subprocess instead of over ssh). Linux only, like the server.
    if (!macos) {
        const server_test_options = b.addOptions();
        server_test_options.addOptionPath("server_path", server_exe.getEmittedBin());
        // The password test runs the editor as ssh's SSH_ASKPASS.
        server_test_options.addOptionPath("editor_path", exe.getEmittedBin());
        const server_tests = b.addTest(.{ .root_module = b.createModule(.{
            .root_source_file = b.path("src/server.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "server_test_options", .module = server_test_options.createModule() }},
        }) });
        test_step.dependOn(&b.addRunArtifact(server_tests).step);
    }

    // Sessions end to end: the installed binary with its own HOME and data folder.
    const e2e_options = b.addOptions();
    e2e_options.addOptionPath("exe_path", exe.getEmittedBin());
    const session_e2e = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("src/app/session/e2e_test.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .imports = &.{.{ .name = "e2e_options", .module = e2e_options.createModule() }},
    }) });
    test_step.dependOn(&b.addRunArtifact(session_e2e).step);
}

/// What every build of the editor's code shares: the generated Wayland
/// protocol code (Linux) or Apple's SDK (macOS), the translated C headers
/// and the build options' values.
const Editor = struct {
    target: std.Build.ResolvedTarget,
    c: *std.Build.Module,
    libs: []const []const u8,
    grammar_dir: []const u8,
    // Linux.
    cx11: ?*std.Build.Module = null,
    gen: ?*std.Build.Step.WriteFile = null,
    protocol_sources: []const std.Build.LazyPath = &.{},
    toplevel_icon_source: ?std.Build.LazyPath = null,
    // macOS.
    sdk: ?[]const u8 = null,

    fn module(e: Editor, b: *std.Build, root: []const u8, optimize: std.builtin.OptimizeMode) *std.Build.Module {
        const options = b.addOptions();
        options.addOption([]const u8, "grammar_dir", e.grammar_dir);
        options.addOption(bool, "toplevel_icon", e.toplevel_icon_source != null);
        options.addOption(bool, "perf", false);

        const mod = b.createModule(.{
            .root_source_file = b.path(root),
            .target = e.target,
            .optimize = optimize,
            .link_libc = true,
            .imports = &.{
                .{ .name = "c", .module = e.c },
                .{ .name = "build_options", .module = options.createModule() },
            },
        });
        if (e.cx11) |m| mod.addImport("cx11", m);
        if (e.gen) |gen| mod.addIncludePath(gen.getDirectory());
        for (e.protocol_sources) |src| mod.addCSourceFile(.{ .file = src, .flags = &.{"-std=c11"} });
        if (e.toplevel_icon_source) |src| mod.addCSourceFile(.{ .file = src, .flags = &.{"-std=c11"} });
        // libvterm's cells and string fragments are bitfield structs translate-c can't express.
        mod.addCSourceFile(.{ .file = b.path("src/model/terminal/vterm_shim.c"), .flags = &.{"-std=c11"} });
        for (e.libs) |lib| mod.linkSystemLibrary(lib, .{});
        if (e.sdk) |sdk| {
            mod.addSystemFrameworkPath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "System/Library/Frameworks" }) });
            mod.addSystemIncludePath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "usr/include" }) });
            mod.addLibraryPath(.{ .cwd_relative = b.pathJoin(&.{ sdk, "usr/lib" }) });
            mod.addCSourceFile(.{ .file = b.path("src/framework/platform/cocoa.m"), .flags = &.{ "-fobjc-arc", "-DGL_SILENCE_DEPRECATION" } });
            for ([_][]const u8{ "Cocoa", "OpenGL", "QuartzCore", "CoreServices" }) |f| mod.linkFramework(f, .{});
        } else {
            for (x11_libs) |lib| mod.linkSystemLibrary(lib, .{});
        }
        // @embedFile can't reach outside src/, so the PNGs come in as modules.
        for (window_icon_sizes) |n| {
            const png = b.fmt("assets/icons/hicolor/{d}x{d}/apps/jiffy-edit.png", .{ n, n });
            mod.addAnonymousImport(b.fmt("icon_{d}", .{n}), .{ .root_source_file = b.path(png) });
        }
        return mod;
    }
};

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
