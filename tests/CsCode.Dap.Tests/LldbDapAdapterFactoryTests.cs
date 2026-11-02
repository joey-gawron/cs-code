using System.Reflection;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Terminal;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The built-in lldb-dap factory (<c>lldb-dap</c>, LLVM's adapter; audit dbg-1): the lookup order (setting, Apple's xcrun, PATH, versioned
/// names, Homebrew's and Debian's LLVM directories), the command line, what the provider fills in, the templates "Add Configuration…" offers and
/// the module registration. Stand-in files and seams only; nothing is debugged (<see cref="RealLldbDapTests"/> runs the real adapter).
/// </summary>
public class LldbDapAdapterFactoryTests
{
    private static LldbDapAdapterFactory Make(TestConfigurationService? settings = null, Dictionary<string, string?>? env = null, RecordingWindowService? window = null,
        FakeWorkspace? ws = null, Func<ITerminalService?>? terminals = null, Func<CancellationToken, Task<string?>>? xcrun = null, IReadOnlyList<string>? wellKnown = null,
        string? llvmRoot = null) =>
        new(new DapPlatform { Configuration = settings, Window = window, Workspace = ws, Terminals = terminals })
        {
            Environment = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
            AskXcrun = xcrun is not null,
            Xcrun = xcrun ?? (_ => Task.FromResult<string?>(null)),
            WellKnownDirectories = wellKnown ?? [],
            VersionedLlvmRoot = llvmRoot,
        };

    private static DebugConfiguration Cfg(object o, Uri? folder = null) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(o), folder);

    private static string Exe(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A fake Rust toolchain: <c>rustc</c> reporting a sysroot with rust-src and the LLDB formatters rust-lldb loads.</summary>
    private static (string Bin, string Sysroot) FakeRust(TempDirectory dir, string commit = "48a229ceaefd4985c50990b14116b6d856af0985")
    {
        var sysroot = dir.Combine("sysroot");
        Directory.CreateDirectory(Path.Combine(sysroot, "lib", "rustlib", "src", "rust", "library"));
        Directory.CreateDirectory(Path.Combine(sysroot, "lib", "rustlib", "etc"));
        File.WriteAllText(Path.Combine(sysroot, "lib", "rustlib", "etc", "lldb_lookup.py"), "# formatters\n");
        File.WriteAllText(Path.Combine(sysroot, "lib", "rustlib", "etc", "lldb_commands"), "# type summary add ...\n");
        var bin = dir.Combine("rustbin");
        var rustc = Exe(bin, "rustc");
        File.WriteAllText(rustc, $"""
            #!/bin/sh
            case "$1" in
              --print) echo "{sysroot}" ;;
              -vV) printf 'rustc 1.98.1 (48a229cea 2026-09-01)\nbinary: rustc\ncommit-hash: {commit}\nhost: aarch64-apple-darwin\n' ;;
            esac

            """);
        return (bin, sysroot);
    }

    // ------------------------------------------------------------------ adapter

    [Fact]
    public async Task It_starts_lldb_dap_on_stdio_below_normal_priority_in_the_folder_and_leaves_other_types_alone()
    {
        using var dir = new TempDirectory("lldbdap-");
        var adapter = Exe(dir.Combine("llvm", "bin"), "lldb-dap");
        var settings = new TestConfigurationService();
        settings.Set(LldbDapAdapterFactory.SettingPath, adapter);
        var f = Make(settings);
        foreach (var type in new[] { "lldb-dap", "LLDB-DAP" })
        {
            var d = Assert.IsType<DebugAdapterStdioProcess>(await f.CreateDescriptorAsync(Cfg(new { type, request = "launch", name = "r" }, dir.Uri), Ct));
            Assert.Equal(adapter, d.Command);
            Assert.Empty(d.Args); // stdio: no --connection
            Assert.True(d.BelowNormalPriority);
            Assert.Equal("lldb-dap", d.DisplayName);
            Assert.Equal(dir.Path.TrimEnd('/'), d.Cwd);
        }
        Assert.Null(await f.CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "codelldb's" }), Ct));
        Assert.Null(await f.CreateDescriptorAsync(Cfg(new { type = "gdb", request = "launch", name = "g" }), Ct));
    }

    [Fact]
    public async Task The_setting_wins_then_xcrun_then_PATH_then_versioned_names_then_Homebrew_then_Debian_s_LLVM_directories()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix names");
        using var dir = new TempDirectory("lldbdap-");
        var settings = new TestConfigurationService();
        var env = new Dictionary<string, string?>();
        string? fromXcode = null;
        var xcrunCalls = 0;
        var homebrew = dir.Combine("opt", "homebrew", "opt", "llvm", "bin");
        var llvmRoot = dir.Combine("usr", "lib");
        var f = Make(settings, env, xcrun: _ => { xcrunCalls++; return Task.FromResult(fromXcode); }, wellKnown: [homebrew], llvmRoot: llvmRoot);

        Assert.Null(await f.LocateAsync(Ct));
        var debian17 = Exe(Path.Combine(llvmRoot, "llvm-17", "bin"), "lldb-vscode");
        var debian19 = Exe(Path.Combine(llvmRoot, "llvm-19", "bin"), "lldb-dap");
        Exe(Path.Combine(llvmRoot, "llvm-9", "bin"), "lldb-vscode"); // llvm-9 < llvm-19: numbers, not strings
        Assert.Equal(debian19, await f.LocateAsync(Ct));
        File.Delete(debian19);
        Assert.Equal(debian17, await f.LocateAsync(Ct)); // the old name in the newest version that has one
        var brew = Exe(homebrew, "lldb-dap");
        Assert.Equal(brew, await f.LocateAsync(Ct));

        var bin = dir.Combine("bin");
        var other = dir.Combine("other-bin");
        env["PATH"] = bin + Path.PathSeparator + other;
        var v18 = Exe(bin, "lldb-dap-18");
        var v20 = Exe(other, "lldb-dap-20");
        Exe(bin, "lldb-server-21"); // not the adapter
        Assert.Equal(v20, await f.LocateAsync(Ct)); // the newest versioned name on PATH
        File.Delete(v20);
        Assert.Equal(v18, await f.LocateAsync(Ct));
        var vscode = Exe(other, "lldb-vscode");
        Assert.Equal(vscode, await f.LocateAsync(Ct)); // a plain name beats a versioned one
        var plain = Exe(other, "lldb-dap");
        Assert.Equal(plain, await f.LocateAsync(Ct)); // lldb-dap before its old name

        fromXcode = Exe(dir.Combine("CommandLineTools", "usr", "bin"), "lldb-dap");
        var g = Make(settings, env, xcrun: _ => { xcrunCalls++; return Task.FromResult<string?>(fromXcode); });
        Assert.Equal(fromXcode, await g.LocateAsync(Ct)); // Apple's tools before PATH
        Assert.Equal(fromXcode, await g.LocateAsync(Ct));

        var llvm = dir.Combine("my-llvm", "bin");
        var mine = Exe(llvm, "lldb-dap");
        settings.Set(LldbDapAdapterFactory.SettingPath, llvm); // a directory holding it
        Assert.Equal(mine, await g.LocateAsync(Ct));
        settings.Set(LldbDapAdapterFactory.SettingPath, "/does/not/exist");
        Assert.Equal(fromXcode, await g.LocateAsync(Ct)); // a dead setting is passed over
        Assert.Equal(2, xcrunCalls); // the first factory found nothing once and never asked again; the second asked once
    }

    [Fact]
    public async Task Xcrun_is_asked_once_per_run_on_a_Mac_without_the_tools()
    {
        var calls = 0;
        var f = Make(xcrun: _ => { calls++; return Task.FromResult<string?>(null); });
        Assert.Null(await f.LocateAsync(Ct));
        Assert.Null(await f.LocateAsync(Ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task On_this_Mac_xcrun_finds_Apple_s_lldb_dap_when_the_developer_tools_are_installed()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Apple's xcrun");
        Assert.SkipUnless(File.Exists("/usr/bin/xcode-select") && Directory.Exists("/Library/Developer/CommandLineTools/usr/bin") || Directory.Exists("/Applications/Xcode.app"),
            "no Xcode or Command Line Tools on this Mac");
        var found = await LldbDapAdapterFactory.XcrunFindAsync(Ct);
        Assert.NotNull(found);
        Assert.True(File.Exists(found), found);
        Assert.Equal("lldb-dap", Path.GetFileName(found));
    }

    [Theory]
    [InlineData("lldb-dap-18", 18)]
    [InlineData("lldb-vscode-14", 14)]
    [InlineData("lldb-dap", null)]
    [InlineData("lldb-server-18", null)]
    [InlineData("lldb-dap-18.1", null)]
    public void Versioned_names_are_recognised(string name, int? version) => Assert.Equal(version, LldbDapAdapterFactory.VersionOf(name));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_lldb_dap_the_start_fails_with_where_to_get_it_on_this_OS(bool macOS)
    {
        var message = LldbDapAdapterFactory.NotFoundMessage(macOS);
        Assert.Contains("cscode.debug.lldbDapPath", message);
        if (macOS) Assert.Contains("xcode-select --install", message);
        else Assert.Contains("lldb-dap-NN", message);
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await Make().CreateDescriptorAsync(Cfg(new { type = "lldb-dap", request = "launch", name = "r" }), Ct));
        Assert.Equal(LldbDapAdapterFactory.NotFoundMessage(OperatingSystem.IsMacOS()), ex.Message);
    }

    // ------------------------------------------------------------------ configuration provider

    [Fact]
    public async Task The_provider_fills_in_cwd_turns_env_into_lldb_dap_s_list_and_never_overrides_the_user()
    {
        using var dir = new TempDirectory("lldbdap-");
        var r = (await Make().ResolveAsync(dir.Uri, Cfg(new
        {
            type = "LLDB-DAP", request = "launch", name = "r", program = "/p",
            env = new Dictionary<string, object?> { ["RUST_BACKTRACE"] = "1", ["LANG"] = null, ["N"] = 3 },
            sourceLanguages = new[] { "cpp" },
        }, dir.Uri), Ct))!;
        Assert.Equal("lldb-dap", r.Type);
        Assert.Equal("lldb-dap", r.Properties["type"].GetString());
        Assert.Equal(dir.Path.TrimEnd('/'), r.GetString("cwd"));
        Assert.Equal(["RUST_BACKTRACE=1", "N=3"], r.Properties["env"].EnumerateArray().Select(e => e.GetString())); // "NAME": null has no equivalent
        Assert.False(r.Properties.ContainsKey("sourceLanguages")); // CodeLLDB's key
        Assert.False(r.Properties.ContainsKey("initCommands")); // not Rust

        var mine = (await Make().ResolveAsync(dir.Uri, Cfg(new { type = "lldb-dap", request = "launch", name = "r", program = "/p", cwd = "/mine", env = new[] { "A=b" } }, dir.Uri), Ct))!;
        Assert.Equal("/mine", mine.GetString("cwd"));
        Assert.Equal("""["A=b"]""", mine.Properties["env"].GetRawText());

        var other = Cfg(new { type = "lldb", request = "launch", name = "codelldb's" });
        Assert.Same(other, await Make().ResolveAsync(dir.Uri, other, Ct));
    }

    [Fact]
    public async Task The_integrated_terminal_becomes_runInTerminal_only_when_there_is_one()
    {
        var without = (await Make().ResolveAsync(null, Cfg(new { type = "lldb-dap", request = "launch", name = "r", program = "/p", console = "integratedTerminal" }), Ct))!;
        Assert.False(without.Properties.ContainsKey("console")); // a newer lldb-dap would ask for a terminal the client cannot give
        Assert.False(without.Properties.ContainsKey("runInTerminal"));
        var terminal = DispatchProxy.Create<ITerminalService, CodeLldbAdapterFactoryTests.UnusedProxy>();
        var with = (await Make(terminals: () => terminal).ResolveAsync(null, Cfg(new { type = "lldb-dap", request = "launch", name = "r", program = "/p", console = "integratedTerminal" }), Ct))!;
        Assert.True(with.Properties["runInTerminal"].GetBoolean());
        Assert.False(with.Properties.ContainsKey("console"));
        var console = (await Make(terminals: () => terminal).ResolveAsync(null, Cfg(new { type = "lldb-dap", request = "launch", name = "r", program = "/p", console = "internalConsole" }), Ct))!;
        Assert.False(console.Properties.ContainsKey("runInTerminal"));
    }

    [Fact]
    public async Task For_Rust_the_toolchain_s_formatters_load_before_the_user_s_init_commands_and_the_standard_library_maps_onto_rust_src()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell stand-ins");
        using var dir = new TempDirectory("lldbdap-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"app\"\nversion = \"0.1.0\"\n");
        var (bin, sysroot) = FakeRust(dir);
        var etc = Path.Combine(sysroot, "lib", "rustlib", "etc");
        var f = Make(env: new() { ["PATH"] = bin });
        var r = (await f.ResolveAsync(dir.Uri, Cfg(new { type = "lldb-dap", request = "launch", name = "r", program = "/p", initCommands = new[] { "settings set target.x 1" } }, dir.Uri), Ct))!;
        Assert.Equal([$"command script import \"{Path.Combine(etc, "lldb_lookup.py")}\"", $"command source -s 0 \"{Path.Combine(etc, "lldb_commands")}\"", "settings set target.x 1"],
            r.Properties["initCommands"].EnumerateArray().Select(c => c.GetString()));
        var pair = Assert.Single(r.Properties["sourceMap"].EnumerateArray());
        Assert.Equal(["/rustc/48a229ceaefd4985c50990b14116b6d856af0985", Path.Combine(sysroot, "lib", "rustlib", "src", "rust")], pair.EnumerateArray().Select(p => p.GetString()));

        // CodeLLDB's object form becomes lldb-dap's pairs; the user's own mapping of the prefix wins
        var mine = (await f.ResolveAsync(dir.Uri, Cfg(new
        {
            type = "lldb-dap", request = "launch", name = "r", program = "/p",
            sourceMap = new Dictionary<string, string> { ["/rustc/48a229ceaefd4985c50990b14116b6d856af0985"] = "/my/src", ["/build"] = "/here" },
        }, dir.Uri), Ct))!;
        Assert.Equal("""[["/rustc/48a229ceaefd4985c50990b14116b6d856af0985","/my/src"],["/build","/here"]]""", mine.Properties["sourceMap"].GetRawText());

        // not a Rust folder and no Rust source language: nothing of the toolchain
        using var c = new TempDirectory("lldbdap-c-");
        var plain = (await f.ResolveAsync(c.Uri, Cfg(new { type = "lldb-dap", request = "launch", name = "c", program = "/a.out" }, c.Uri), Ct))!;
        Assert.False(plain.Properties.ContainsKey("initCommands"));
        Assert.False(plain.Properties.ContainsKey("sourceMap"));
        // ...unless the configuration says it is Rust
        var rust = (await f.ResolveAsync(c.Uri, Cfg(new { type = "lldb-dap", request = "launch", name = "c", program = "/a.out", sourceLanguages = new[] { "rust" } }, c.Uri), Ct))!;
        Assert.Equal(2, rust.Properties["initCommands"].GetArrayLength());
    }

    [Fact]
    public async Task The_cargo_key_builds_with_cargo_and_its_executable_becomes_the_program()
    {
        var cargo = RustToolchainLocator.FindOnPath(System.Environment.GetEnvironmentVariable, OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");
        Assert.SkipWhen(cargo is null, "cargo is not on PATH");
        using var dir = new TempDirectory("lldbdapcargo-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"lldbdap-sample\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        dir.WriteFile("src/main.rs", "fn main() { println!(\"hi\"); }\n");
        var r = await Make(window: new RecordingWindowService()).ResolveAsync(dir.Uri,
            Cfg(new { type = "lldb-dap", request = "launch", name = "r", cargo = new { args = new[] { "build" } } }, dir.Uri), CancellationTokenSource.CreateLinkedTokenSource(Ct).Token);
        Assert.NotNull(r);
        var program = r.GetString("program")!;
        Assert.Equal("lldbdap-sample", Path.GetFileNameWithoutExtension(program));
        Assert.True(File.Exists(program), program);
        Assert.False(r.Properties.ContainsKey("cargo")); // lldb-dap does not know it
    }

    [Theory]
    [InlineData("pid", "4242")]
    [InlineData("processId", "4242")]
    public async Task Attach_takes_the_pid_as_a_number_also_when_pickProcess_gave_text(string key, string pid)
    {
        var props = new Dictionary<string, object?> { ["type"] = "lldb-dap", ["request"] = "attach", ["name"] = "a", [key] = pid };
        var r = (await Make().ResolveAsync(null, Cfg(props), Ct))!;
        Assert.Equal(4242, r.Properties["pid"].GetInt32());
        Assert.False(r.Properties.ContainsKey("processId"));
        Assert.False(r.Properties.ContainsKey("cwd")); // launch only

        var window = new RecordingWindowService();
        Assert.Null(await Make(window: window).ResolveAsync(null, Cfg(new { type = "lldb-dap", request = "attach", name = "a", pid = "abc" }), Ct));
        Assert.Contains(window.Messages, m => m.Message.Contains("'abc' is not a process id", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ templates ("Add Configuration…", a new launch.json)

    [Fact]
    public async Task A_cargo_package_gets_lldb_dap_binary_unit_test_and_attach_templates_and_the_cargo_build_task()
    {
        using var dir = new TempDirectory("lldbdaptemplates-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"my-app\"\nversion = \"0.1.0\"\n");
        dir.WriteFile("src/main.rs", "fn main() {}\n");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        var configs = await f.ProvideInitialAsync(dir.Uri, Ct);
        Assert.Equal(["Rust: Debug my-app (lldb-dap, cargo build)", "Rust: Debug unit tests of my-app (lldb-dap, cargo test)", "Rust: Attach to my-app (lldb-dap)"], configs.Select(c => c.Name));
        Assert.All(configs, c => Assert.Equal("lldb-dap", c.Type));
        Assert.Equal("${workspaceFolder}/target/debug/my-app", configs[0].GetString("program"));
        Assert.Equal("cargo: build", configs[0].PreLaunchTask);
        var cargo = configs[1].Properties["cargo"];
        Assert.Equal(["test", "--no-run"], cargo.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("bin", cargo.GetProperty("filter").GetProperty("kind").GetString()); // no library: its binary's tests
        Assert.Equal("${command:pickProcess}", configs[2].GetString("pid"));
        Assert.True(configs[2].IsAttach);
        var (path, content) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
        Assert.Equal(("tasks.json", CargoPackage.BuildTasksJson), (path, content));
    }

    [Fact]
    public async Task A_folder_without_cargo_toml_gets_plain_c_and_cpp_templates_and_no_task()
    {
        using var dir = new TempDirectory("lldbdaptemplates-");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        Assert.Equal(["lldb-dap: Launch program", "lldb-dap: Attach to process"], (await f.ProvideInitialAsync(dir.Uri, Ct)).Select(c => c.Name));
        Assert.Empty(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
    }

    // ------------------------------------------------------------------ module

    [Fact]
    public async Task The_module_registers_lldb_dap_beside_codelldb_with_its_setting_and_the_rust_c_and_cpp_breakpoint_languages()
    {
        var services = new ServiceCollection().AddCsCodeDap();
        await using var sp = services.BuildServiceProvider();
        var builtins = sp.GetServices<BuiltinDebugAdapter>().ToList();
        var dap = Assert.Single(builtins, b => b.Type == "lldb-dap");
        Assert.Equal("Rust / C / C++ / Zig (lldb-dap)", dap.Label);
        Assert.Equal(["rust", "c", "cpp", "zig"], dap.Languages);
        Assert.Same(sp.GetRequiredService<LldbDapAdapterFactory>(), dap.Factory);
        Assert.Same(dap.Factory, dap.Provider);
        Assert.Single(builtins, b => b.Type == "lldb"); // CodeLLDB stays
        var setting = Assert.Single(DebugService.SettingProperties(), p => p.Key == LldbDapAdapterFactory.SettingPath);
        Assert.Equal(CsCode.Core.Platform.ConfigurationScope.Machine, setting.Scope); // it names a program: user settings only
        Assert.Contains(LldbDapAdapterFactory.SettingPath, DebugService.SettingsRead);
    }

    [Theory]
    [InlineData("lldb-dap", "lldb-dap")]
    [InlineData("LLDB-DAP", "lldb-dap")]
    [InlineData("lldb", "lldb")]
    [InlineData("gdb", "gdb")]
    public void The_rust_debugger_setting_can_choose_lldb_dap_for_a_new_launch_json(string setting, string type) =>
        Assert.Equal(type, DebugService.RustDebugType(setting, macOS: true));
}
