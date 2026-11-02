using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Zig through the LLDB debuggers (docs/ZIG.md): the templates and tasks of a <c>build.zig</c> project, the pretty printers' init commands, the
/// default debugger of a Zig folder, the <c>$zig</c> problem matcher and the small readers (<c>build.zig</c> executables, <c>zig env</c>). A fake
/// <c>zig</c> script stands in for the compiler; the real one is in <see cref="RealZigDebuggingTests"/>.
/// </summary>
public class ZigDebuggingTests
{
    private const string InitBuildZig = """
        const std = @import("std");

        // Although this function looks imperative, it does not perform the build directly.
        // b.addExecutable(.{ .name = "not-this-one" }) in a comment does not count.
        pub fn build(b: *std.Build) void {
            const target = b.standardTargetOptions(.{});
            const optimize = b.standardOptimizeOption(.{});
            const mod = b.addModule("hello", .{
                .root_source_file = b.path("src/root.zig"),
                .target = target,
            });
            const exe = b.addExecutable(.{
                .name = "hello",
                .root_module = b.createModule(.{
                    .root_source_file = b.path("src/main.zig"),
                    .target = target,
                    .optimize = optimize,
                    .imports = &.{
                        .{ .name = "hello", .module = mod },
                    },
                }),
            });
            b.installArtifact(exe);
        }
        """;

    private static DebugConfiguration Cfg(object o, Uri? folder = null) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(o), folder);

    /// <summary>A fake zig whose <c>zig env</c> prints ZON with <c>lib_dir</c> relative to its own directory, and that lib with the printers.</summary>
    private static string FakeZig(TempDirectory dir, bool withPrinters = true)
    {
        var bin = dir.Combine("zig-dist");
        Directory.CreateDirectory(bin);
        var zig = Path.Combine(bin, "zig");
        File.WriteAllText(zig, """
            #!/bin/sh
            case "$1" in
              env) printf '.{\n    .zig_exe = "%s",\n    .lib_dir = "lib",\n    .version = "0.17.0",\n}\n' "$0" ;;
              version) echo 0.17.0 ;;
              *) exit 1 ;;
            esac
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(zig, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (withPrinters)
        {
            Directory.CreateDirectory(Path.Combine(bin, "lib", "lldb"));
            File.WriteAllText(Path.Combine(bin, "lib", "lldb", "pretty_printers.py"), "# stand-in\n");
        }
        return zig;
    }

    private static CodeLldbAdapterFactory CodeLldb(TempDirectory dir, TestConfigurationService? settings = null, string? path = null) =>
        new(new DapPlatform { Workspace = new FakeWorkspace(dir.Uri), Configuration = settings }) { Environment = name => name == "PATH" ? path ?? dir.Combine("nothing") : null };

    private static LldbDapAdapterFactory LldbDap(TempDirectory dir, TestConfigurationService? settings = null, string? path = null) =>
        new(new DapPlatform { Workspace = new FakeWorkspace(dir.Uri), Configuration = settings }) { Environment = name => name == "PATH" ? path ?? dir.Combine("nothing") : null, AskXcrun = false };

    // ------------------------------------------------------------------ readers

    [Fact]
    public void The_executables_of_build_zig_are_the_literal_names_of_its_addExecutable_calls_not_a_nested_modules_or_a_comments()
    {
        Assert.Equal(["hello"], ZigProject.ExecutableNames(InitBuildZig));
        var two = """
            const tool = b.addExecutable(.{ .root_module = b.createModule(.{ .root_source_file = b.path("x.zig") }), .name = "tool" });
            const gen = b.addExecutable(.{ .name = name_from_options, .root_module = m });
            const srv = b.addExecutable(.{
                .name = "server", // the "name" in a comment: "ignored"
            });
            const lib = b.addLibrary(.{ .name = "notexe" });
            """;
        Assert.Equal(["tool", "server"], ZigProject.ExecutableNames(two));
        Assert.Empty(ZigProject.ExecutableNames("pub fn build(b: *std.Build) void { _ = b; }"));
    }

    [Theory]
    [InlineData(".{\n    .zig_exe = \"/z/zig\",\n    .lib_dir = \"lib\",\n    .std_dir = \"lib/std\",\n    .version = \"0.17.0\",\n}\n", "0.17.0", "lib")]
    [InlineData("{\n \"zig_exe\": \"/z/zig\",\n \"lib_dir\": \"/z/lib\",\n \"std_dir\": \"/z/lib/std\",\n \"global_cache_dir\": \"/c\",\n \"version\": \"0.13.0\",\n \"target\": \"x86_64-linux.6.1...6.1-gnu.2.36\"\n}\n", "0.13.0", "/z/lib")]
    [InlineData(".{\n    .lib_dir = \"C:\\\\zig\\\\lib\",\n    .version = \"0.18.0-dev.12+abc\",\n}\n", "0.18.0-dev.12+abc", "C:\\zig\\lib")]
    public void Zig_env_is_read_in_both_its_zon_and_its_older_json_form(string text, string version, string libDir)
    {
        Assert.Equal((version, libDir), ZigToolchainLocator.ParseEnv(text));
    }

    // ------------------------------------------------------------------ templates

    [Fact]
    public async Task A_build_zig_project_gets_codelldb_zig_build_test_binary_and_attach_templates_and_their_zig_tasks()
    {
        using var dir = new TempDirectory("zigtemplates-");
        dir.WriteFile("build.zig", InitBuildZig);
        var f = CodeLldb(dir);
        var configs = await f.ProvideInitialAsync(dir.Uri, Ct);
        Assert.Equal(["Zig: Debug hello (CodeLLDB, zig build)", "Zig: Debug the tests of the current file (CodeLLDB, zig test)", "Zig: Attach to hello (CodeLLDB)"],
            configs.Select(c => c.Name));
        Assert.All(configs, c => Assert.Equal("lldb", c.Type));
        Assert.Equal("${workspaceFolder}/zig-out/bin/hello", configs[0].GetString("program"));
        Assert.Equal(ZigDebugging.BuildTask, configs[0].PreLaunchTask);
        Assert.Equal("${workspaceFolder}/.zig-cache/${fileBasenameNoExtension}-test", configs[1].GetString("program"));
        Assert.Equal(ZigDebugging.TestBinaryTask, configs[1].PreLaunchTask);
        Assert.All(configs.Take(2), c => Assert.Equal(["zig"], c.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString())));
        Assert.All(configs.Take(2), c => Assert.Equal("console", c.GetString("terminal")));
        Assert.True(configs[2].IsAttach);
        Assert.Equal("${command:pickProcess}", configs[2].GetString("pid"));

        var (path, content) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
        Assert.Equal("tasks.json", path);
        var tasks = JsonNode.Parse(content)!["tasks"]!.AsArray();
        Assert.Equal([ZigDebugging.BuildTask, ZigDebugging.TestBinaryTask], tasks.Select(t => t!["label"]!.GetValue<string>()));
        Assert.All(tasks, t => Assert.Equal(("process", "zig", "$zig"), (t!["type"]!.GetValue<string>(), t["command"]!.GetValue<string>(), t["problemMatcher"]!.GetValue<string>())));
        Assert.Equal(["build"], tasks[0]!["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(tasks[0]!["group"]!["isDefault"]!.GetValue<bool>());
        Assert.Equal(["test", "${file}", "--test-no-exec", "-femit-bin=${workspaceFolder}/.zig-cache/${fileBasenameNoExtension}-test"],
            tasks[1]!["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public async Task The_zig_tasks_name_the_configured_zig_when_path_has_none_and_plain_zig_when_it_does()
    {
        using var dir = new TempDirectory("zigtasks-");
        dir.WriteFile("build.zig", InitBuildZig);
        var zig = FakeZig(dir);
        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.SettingPath, zig);
        string Command(string json) => JsonNode.Parse(json)!["tasks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal(zig, Command(Assert.Single(await CodeLldb(dir, settings).ProvideInitialFilesAsync(dir.Uri, Ct)).Content));
        Assert.Equal("zig", Command(Assert.Single(await CodeLldb(dir, settings, path: Path.GetDirectoryName(zig)).ProvideInitialFilesAsync(dir.Uri, Ct)).Content));
        Assert.Equal("zig", Command(Assert.Single(await LldbDap(dir).ProvideInitialFilesAsync(dir.Uri, Ct)).Content)); // none at all: the portable name
    }

    [Fact]
    public async Task A_build_zig_project_gets_lldb_dap_templates_without_codelldbs_keys()
    {
        using var dir = new TempDirectory("zigtemplates-");
        dir.WriteFile("build.zig", InitBuildZig);
        var configs = await LldbDap(dir).ProvideInitialAsync(dir.Uri, Ct);
        Assert.Equal(["Zig: Debug hello (lldb-dap, zig build)", "Zig: Debug the tests of the current file (lldb-dap, zig test)", "Zig: Attach to hello (lldb-dap)"],
            configs.Select(c => c.Name));
        Assert.All(configs, c => Assert.Equal("lldb-dap", c.Type));
        Assert.All(configs, c => Assert.False(c.Properties.ContainsKey("sourceLanguages") || c.Properties.ContainsKey("terminal")));
        Assert.Contains(ZigDebugging.BuildTask, Assert.Single(await LldbDap(dir).ProvideInitialFilesAsync(dir.Uri, Ct)).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_with_both_cargo_toml_and_build_zig_keeps_the_rust_templates()
    {
        using var dir = new TempDirectory("zigtemplates-");
        dir.WriteFile("build.zig", InitBuildZig);
        dir.WriteFile("Cargo.toml", "[package]\nname = \"app\"\nversion = \"0.1.0\"\n");
        dir.WriteFile("src/main.rs", "fn main() {}\n");
        Assert.All(await CodeLldb(dir).ProvideInitialAsync(dir.Uri, Ct), c => Assert.StartsWith("Rust: ", c.Name, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ pretty printers

    [Fact]
    public async Task A_zig_configuration_under_codelldb_loads_zigs_pretty_printers_before_the_users_init_commands()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake zig is a shell script");
        using var dir = new TempDirectory("zigprinters-");
        dir.WriteFile("build.zig", InitBuildZig);
        var zig = FakeZig(dir);
        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.SettingPath, zig);
        var r = (await CodeLldb(dir, settings).ResolveAsync(dir.Uri, Cfg(new { type = "lldb", request = "launch", name = "z", program = "/p", initCommands = new[] { "settings set a b" } }, dir.Uri), Ct))!;
        var init = r.Properties["initCommands"].EnumerateArray().Select(c => c.GetString()!).ToList();
        var helper = Path.Combine(ZigDebugging.DefaultHelperDirectory, ZigDebugging.HelperScript);
        var printers = Path.Combine(Path.GetDirectoryName(zig)!, "lib", "lldb", "pretty_printers.py");
        Assert.Equal([$"command script import \"{helper}\"", $"script cscode_zig_lldb.load(lldb.debugger, {JsonSerializer.Serialize(printers)})", "settings set a b"], init);
        Assert.True(File.Exists(helper), "the helper is copied next to the assembly");
        Assert.Equal(["zig"], r.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString())); // so CodeLLDB does not look for rustc
    }

    [Fact]
    public async Task Without_zig_or_its_printers_nothing_is_added_and_a_folder_that_is_not_zig_is_left_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake zig is a shell script");
        using var dir = new TempDirectory("zigprinters-");
        dir.WriteFile("build.zig", InitBuildZig);
        var noZig = (await CodeLldb(dir).ResolveAsync(dir.Uri, Cfg(new { type = "lldb", request = "launch", name = "z", program = "/p" }, dir.Uri), Ct))!;
        Assert.False(noZig.Properties.ContainsKey("initCommands"));
        Assert.Equal(["zig"], noZig.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString()));

        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.SettingPath, FakeZig(dir, withPrinters: false));
        Assert.False((await CodeLldb(dir, settings).ResolveAsync(dir.Uri, Cfg(new { type = "lldb", request = "launch", name = "z", program = "/p" }, dir.Uri), Ct))!.Properties.ContainsKey("initCommands"));

        using var other = new TempDirectory("notzig-");
        settings.Set(ZigToolchainLocator.SettingPath, FakeZig(other));
        var plain = (await CodeLldb(other, settings).ResolveAsync(other.Uri, Cfg(new { type = "lldb", request = "launch", name = "c", program = "/p" }, other.Uri), Ct))!;
        Assert.False(plain.Properties.ContainsKey("initCommands"));
        Assert.Equal(["rust"], plain.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString())); // unchanged default
        // sourceLanguages: ["zig"] marks a Zig configuration outside a build.zig project (a lone file built with zig build-exe)
        var marked = (await CodeLldb(other, settings).ResolveAsync(other.Uri, Cfg(new { type = "lldb", request = "launch", name = "c", program = "/p", sourceLanguages = new[] { "zig" } }, other.Uri), Ct))!;
        Assert.Equal(2, marked.Properties["initCommands"].GetArrayLength());
    }

    [Fact]
    public async Task A_zig_configuration_under_lldb_dap_loads_the_printers_too_and_drops_source_languages()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake zig is a shell script");
        using var dir = new TempDirectory("zigprinters-");
        dir.WriteFile("build.zig", InitBuildZig);
        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.ZlsSettingPath, FakeZig(dir)); // the zig zls is told to use counts too
        var r = (await LldbDap(dir, settings).ResolveAsync(dir.Uri, Cfg(new { type = "lldb-dap", request = "launch", name = "z", program = "/p", sourceLanguages = new[] { "zig" } }, dir.Uri), Ct))!;
        var init = r.Properties["initCommands"].EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(2, init.Count);
        Assert.StartsWith("command script import ", init[0], StringComparison.Ordinal);
        Assert.StartsWith("script cscode_zig_lldb.load(lldb.debugger, ", init[1], StringComparison.Ordinal);
        Assert.False(r.Properties.ContainsKey("sourceLanguages"));
    }

    [Fact]
    public void Lldb_arguments_are_quoted_with_backslashes_and_quotes_escaped()
    {
        Assert.Equal("\"/a b/c.py\"", ZigDebugging.LldbQuote("/a b/c.py"));
        Assert.Equal("\"C:\\\\z\\\\\\\"q\\\".py\"", ZigDebugging.LldbQuote("C:\\z\\\"q\".py"));
    }

    // ------------------------------------------------------------------ problem matcher

    [Fact]
    public void The_zig_matcher_reads_zig_build_errors_and_notes_relative_to_the_task_folder_and_ignores_test_stack_traces()
    {
        var diagnostics = new DiagnosticService();
        var collector = new ProblemCollector(ProblemMatchers.Parse(JsonNode.Parse("\"$zig\"")), diagnostics, "/work/ws", "/work/ws/app");
        collector.Start();
        collector.Append("""
            install
            +- install hello
               +- compile exe hello debug native 1 errors
            src/main.zig:30:25: error: use of undeclared identifier 'undefinedThing'
                const unused: u32 = undefinedThing;
                                    ^~~~~~~~~~~~~~
            src/main.zig:4:1: note: struct declared here
            /opt/zig/lib/std/testing.zig:93:17: 0x1043eaecb in expectEqualInner__func_1000 (test)
            /opt/zig/lib/std/fmt.zig:12:3: error: invalid format string 'x'
            error: 1 compilation errors

            """);
        collector.Complete();
        var all = diagnostics.GetAll().OrderBy(d => d.Uri.LocalPath).ThenBy(d => d.Diagnostic.Range.Start.Line).ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal("/opt/zig/lib/std/fmt.zig", all[0].Uri.LocalPath);
        Assert.Equal("/work/ws/app/src/main.zig", all[1].Uri.LocalPath);
        Assert.Equal((DiagnosticSeverity.Information, 3, 0, "struct declared here"), (all[1].Diagnostic.Severity, all[1].Diagnostic.Range.Start.Line, all[1].Diagnostic.Range.Start.Character, all[1].Diagnostic.Message));
        Assert.Equal((DiagnosticSeverity.Error, 29, 24, "use of undeclared identifier 'undefinedThing'"), (all[2].Diagnostic.Severity, all[2].Diagnostic.Range.Start.Line, all[2].Diagnostic.Range.Start.Character, all[2].Diagnostic.Message));
        Assert.Equal("zig", all[2].Diagnostic.Source);
        Assert.Equal((2, 0), (collector.Errors, collector.Warnings));
    }

    // ------------------------------------------------------------------ default debugger

    [Fact]
    public async Task F5_in_a_build_zig_folder_creates_a_codelldb_launch_json_with_the_zig_tasks()
    {
        await using var h = new ServiceHarness(moreBuiltins: p =>
        [
            .. GdbAdapterFactory.Builtins(new GdbAdapterFactory(p)),
            .. CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p)),
            .. LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p)),
        ]);
        h.Dir.WriteFile("build.zig", InitBuildZig);
        h.Window.MessageActionResponses.Enqueue("Create launch.json");
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        var launch = Path.Combine(h.Dir.Path, ".cscode", "launch.json");
        await SessionHarness.Eventually(() => File.Exists(launch), 5000, "launch.json created");
        using var doc = JsonDocument.Parse(File.ReadAllText(launch), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(["lldb", "lldb", "lldb"], doc.RootElement.GetProperty("configurations").EnumerateArray().Select(c => c.GetProperty("type").GetString()));
        var tasks = File.ReadAllText(Path.Combine(h.Dir.Path, ".cscode", "tasks.json"));
        Assert.Contains(ZigDebugging.BuildTask, tasks, StringComparison.Ordinal);
        Assert.Contains("$zig", tasks, StringComparison.Ordinal);
    }
}
