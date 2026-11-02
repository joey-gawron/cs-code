using System.Reflection;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Terminal;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The built-in CodeLLDB factory (<c>lldb</c>): the adapter's lookup and command line, the download offer, what the provider fills in, the
/// cargo templates and the module registration. No real CodeLLDB: stand-in files and fabricated packages; nothing is run.
/// </summary>
public class CodeLldbAdapterFactoryTests
{
    private static CodeLldbAdapterFactory Make(TestConfigurationService? settings = null, Dictionary<string, string?>? env = null, TestAppPaths? paths = null,
        RecordingWindowService? window = null, FakeWorkspace? ws = null, CodeLldbInstaller? installer = null, Func<ITerminalService?>? terminals = null) =>
        new(new DapPlatform { Configuration = settings, Paths = paths, Window = window, Workspace = ws, Terminals = terminals }, installer)
        {
            Environment = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
        };

    private static DebugConfiguration Cfg(object o, Uri? folder = null) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(o), folder);

    private static string FakeAdapter(string dir)
    {
        var path = Path.Combine(dir, "extension", "adapter", CodeLldbInstaller.AdapterExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        return path;
    }

    private static string Script(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A fake Rust toolchain: <c>rustc</c> reporting a sysroot that has the rust-src component.</summary>
    private static (string Bin, string Sysroot) FakeRust(TempDirectory dir, string commit = "48a229ceaefd4985c50990b14116b6d856af0985")
    {
        var sysroot = dir.Combine("sysroot");
        Directory.CreateDirectory(Path.Combine(sysroot, "lib", "rustlib", "src", "rust", "library"));
        var bin = dir.Combine("rustbin");
        Script(bin, "rustc", $"""
            case "$1" in
              --print) echo "{sysroot}" ;;
              -vV) printf 'rustc 1.98.1 (48a229cea 2026-09-01)\nbinary: rustc\ncommit-hash: {commit}\nhost: x86_64-unknown-linux-gnu\n' ;;
            esac

            """);
        return (bin, sysroot);
    }

    // ------------------------------------------------------------------ adapter

    [Fact]
    public async Task It_starts_codelldb_on_stdio_below_normal_priority_with_rust_as_the_source_language()
    {
        using var dir = new TempDirectory("codelldb-");
        var adapter = FakeAdapter(dir.Combine("install"));
        var settings = new TestConfigurationService();
        settings.Set(CodeLldbAdapterFactory.SettingPath, adapter);
        var f = Make(settings);
        foreach (var type in new[] { "lldb", "LLDB" })
        {
            var d = Assert.IsType<DebugAdapterStdioProcess>(await f.CreateDescriptorAsync(Cfg(new { type, request = "launch", name = "r" }, dir.Uri), Ct));
            Assert.Equal(adapter, d.Command);
            Assert.True(d.BelowNormalPriority);
            Assert.Equal("codelldb", d.DisplayName);
            Assert.Equal(dir.Path.TrimEnd('/'), d.Cwd); // rustc for the Rust formatters runs in the folder (rust-toolchain files)
            Assert.Equal(["PYTHONHOME", "PYTHONPATH", "CODELLDB_STARTUP"], d.RemoveEnv);
            Assert.Equal("--settings", d.Args[0]);
            using var json = JsonDocument.Parse(d.Args[1]);
            Assert.Equal(["rust"], json.RootElement.GetProperty("sourceLanguages").EnumerateArray().Select(l => l.GetString()));
            Assert.True(json.RootElement.GetProperty("evaluateForHovers").GetBoolean());
            Assert.True(json.RootElement.GetProperty("commandCompletions").GetBoolean());
            Assert.Equal(2, d.Args.Count); // stdio: no --port, no --connect
        }
        var cpp = Assert.IsType<DebugAdapterStdioProcess>(await f.CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "c", sourceLanguages = new[] { "cpp" } }), Ct));
        using (var json = JsonDocument.Parse(cpp.Args[1]))
            Assert.Equal(["cpp"], json.RootElement.GetProperty("sourceLanguages").EnumerateArray().Select(l => l.GetString()));
        Assert.Null(await f.CreateDescriptorAsync(Cfg(new { type = "gdb", request = "launch", name = "g" }), Ct));
    }

    [Fact]
    public void The_setting_wins_over_the_environment_variable_which_wins_over_the_installed_copy_which_wins_over_PATH()
    {
        using var dir = new TempDirectory("codelldb-");
        using var paths = new TestAppPaths();
        var viaSetting = FakeAdapter(dir.Combine("setting"));
        var viaEnv = FakeAdapter(dir.Combine("env"));
        var installed = FakeAdapter(CodeLldbInstaller.InstallDirectory(paths.ToolsDir));
        var pathDir = dir.Combine("bin");
        Directory.CreateDirectory(pathDir);
        var onPath = Path.Combine(pathDir, CodeLldbInstaller.AdapterExecutableName);
        File.WriteAllText(onPath, "#!/bin/sh\n");
        var settings = new TestConfigurationService();
        var env = new Dictionary<string, string?> { ["PATH"] = pathDir };
        var f = Make(settings, env, paths);

        Assert.Equal(installed, f.Locate());
        env[CodeLldbAdapterFactory.EnvironmentVariable] = dir.Combine("env"); // the install directory: resolved to its adapter
        Assert.Equal(viaEnv, f.Locate());
        settings.Set(CodeLldbAdapterFactory.SettingPath, dir.Combine("setting", "extension")); // CodeLLDB's extension directory
        Assert.Equal(viaSetting, f.Locate());

        settings.Set(CodeLldbAdapterFactory.SettingPath, "/does/not/exist");
        env[CodeLldbAdapterFactory.EnvironmentVariable] = "/nor/this";
        File.Delete(installed);
        Assert.Equal(onPath, f.Locate()); // dead setting and variable, no install: PATH
        File.Delete(onPath);
        Assert.Null(f.Locate());
    }

    [Fact]
    public async Task Without_codelldb_and_without_a_window_the_start_fails_with_this_oss_advice()
    {
        using var paths = new TestAppPaths();
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await Make(paths: paths).CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "r" }), Ct));
        Assert.Equal(CodeLldbAdapterFactory.NotFoundMessage(OperatingSystem.IsMacOS()), ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_missing_codelldb_advice_names_gdb_on_linux_and_the_app_bundle_on_macos(bool macOS)
    {
        var message = CodeLldbAdapterFactory.NotFoundMessage(macOS);
        Assert.Contains("fetch-codelldb.sh", message, StringComparison.Ordinal);
        Assert.Contains(CodeLldbAdapterFactory.SettingPath, message, StringComparison.Ordinal);
        Assert.Contains(CodeLldbAdapterFactory.EnvironmentVariable, message, StringComparison.Ordinal);
        Assert.Contains("download offer", message, StringComparison.Ordinal);
        if (macOS)
        {
            // GDB is no alternative there (before, the Mac was told "GDB (type 'gdb') debugs Rust without a download")
            Assert.DoesNotContain("type 'gdb'", message, StringComparison.Ordinal);
            Assert.Contains("GDB cannot debug native arm64 macOS programs", message, StringComparison.Ordinal);
            Assert.Contains("CsCode.app", message, StringComparison.Ordinal);
        }
        else Assert.Contains("GDB (type 'gdb') debugs Rust without a download", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ download offer

    [Fact]
    public async Task The_download_is_offered_first_and_declining_it_downloads_nothing()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Cancel");
        var archive = CodeLldbInstallerTests.GoodVsix();
        var release = new CodeLldbInstallerTests.FakeRelease(archive);
        var installer = new CodeLldbInstaller(release, package: CodeLldbInstallerTests.PackageFor(archive));
        await Assert.ThrowsAsync<DebugSessionStartException>(async () =>
            await Make(paths: paths, window: window, installer: installer).CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "r" }), Ct));
        var (_, message) = Assert.Single(window.Messages);
        Assert.Contains("CodeLLDB v1.12.3", message, StringComparison.Ordinal);
        Assert.Contains("MIT", message, StringComparison.Ordinal);
        Assert.Contains("MB", message, StringComparison.Ordinal);
        Assert.Contains("github.com/vadimcn/codelldb", message, StringComparison.Ordinal);
        Assert.Equal(0, release.Downloads);
        Assert.False(Directory.Exists(Path.Combine(paths.ToolsDir, "codelldb")));
    }

    [Fact]
    public async Task Accepting_the_offer_installs_the_pinned_package_and_starts_the_installed_adapter()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var archive = CodeLldbInstallerTests.GoodVsix();
        var installer = new CodeLldbInstaller(new CodeLldbInstallerTests.FakeRelease(archive), package: CodeLldbInstallerTests.PackageFor(archive));
        var f = Make(paths: paths, window: window, installer: installer);
        var d = Assert.IsType<DebugAdapterStdioProcess>(await f.CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "r" }), Ct));
        Assert.Equal(CodeLldbInstaller.AdapterPath(paths.ToolsDir), d.Command);
        Assert.Equal(d.Command, f.Locate()); // the next start finds it without asking
        Assert.Single(window.Messages);
    }

    [Fact]
    public async Task A_failed_download_is_reported_and_the_start_fails()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var archive = CodeLldbInstallerTests.GoodVsix();
        var tampered = CodeLldbInstallerTests.PackageFor(archive) with { Sha256 = new string('1', 64) };
        var installer = new CodeLldbInstaller(new CodeLldbInstallerTests.FakeRelease(archive), package: tampered);
        await Assert.ThrowsAsync<DebugSessionStartException>(async () =>
            await Make(paths: paths, window: window, installer: installer).CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "r" }), Ct));
        Assert.Contains(window.Messages, m => m.Severity == CsCode.Core.Platform.MessageSeverity.Error && m.Message.Contains("Could not install CodeLLDB", StringComparison.Ordinal)
            && m.Message.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Null(Make(paths: paths).Locate());
    }

    [Fact]
    public async Task Concurrent_starts_ask_once_and_download_once()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var archive = CodeLldbInstallerTests.GoodVsix();
        var release = new CodeLldbInstallerTests.FakeRelease(archive, delay: TimeSpan.FromMilliseconds(50));
        var f = Make(paths: paths, window: window, installer: new CodeLldbInstaller(release, package: CodeLldbInstallerTests.PackageFor(archive)));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.CreateDescriptorAsync(Cfg(new { type = "lldb", request = "launch", name = "r" }), Ct).AsTask()));
        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(1, release.Downloads);
        Assert.Single(window.Messages);
    }

    // ------------------------------------------------------------------ provider

    [Fact]
    public async Task The_provider_fills_in_what_vs_codes_codelldb_extension_would()
    {
        using var dir = new TempDirectory("codelldb-");
        var r = (await Make().ResolveAsync(dir.Uri, Cfg(new { type = "LLDB", request = "launch", name = "r", program = "/p" }, dir.Uri), Ct))!;
        var folder = dir.Path.TrimEnd('/');
        Assert.Equal("lldb", r.Type);
        Assert.Equal("lldb", r.Properties["type"].GetString());
        Assert.Equal(folder, r.GetString("cwd"));
        Assert.Equal(folder, r.GetString("relativePathBase"));
        Assert.Equal("console", r.GetString("terminal")); // the program's output in the Debug Console, as with GDB
        Assert.Equal(["rust"], r.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("evaluate", r.Properties["_adapterSettings"].GetProperty("consoleMode").GetString()); // the accepted Rust default

        // the user's own values are never overridden
        var mine = (await Make().ResolveAsync(dir.Uri, Cfg(new
        {
            type = "lldb", request = "launch", name = "r", program = "/p", cwd = "/mine", terminal = "integrated", relativePathBase = "/base",
            sourceLanguages = new[] { "cpp" }, _adapterSettings = new { consoleMode = "commands", displayFormat = "hex" },
        }, dir.Uri), Ct))!;
        Assert.Equal(("/mine", "integrated", "/base"), (mine.GetString("cwd"), mine.GetString("terminal"), mine.GetString("relativePathBase")));
        Assert.Equal(["cpp"], mine.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(("commands", "hex"), (mine.Properties["_adapterSettings"].GetProperty("consoleMode").GetString(), mine.Properties["_adapterSettings"].GetProperty("displayFormat").GetString()));

        var other = Cfg(new { type = "gdb", request = "launch", name = "g" });
        Assert.Same(other, await Make().ResolveAsync(dir.Uri, other, Ct));
    }

    [Fact]
    public async Task Vs_codes_lldb_consoleMode_setting_is_honoured()
    {
        var settings = new TestConfigurationService();
        settings.Set(CodeLldbAdapterFactory.ConsoleModeSetting, "commands");
        var r = (await Make(settings).ResolveAsync(null, Cfg(new { type = "lldb", request = "launch", name = "r", program = "/p" }), Ct))!;
        Assert.Equal("commands", r.Properties["_adapterSettings"].GetProperty("consoleMode").GetString());
    }

    [Fact]
    public async Task An_integrated_terminal_is_kept_only_when_there_is_one()
    {
        var withoutTerminal = (await Make().ResolveAsync(null, Cfg(new { type = "lldb", request = "launch", name = "r", program = "/p", console = "integratedTerminal" }), Ct))!;
        Assert.Equal("console", withoutTerminal.GetString("terminal"));
        var terminal = DispatchProxy.Create<ITerminalService, UnusedProxy>();
        var withTerminal = (await Make(terminals: () => terminal).ResolveAsync(null, Cfg(new { type = "lldb", request = "launch", name = "r", program = "/p", console = "integratedTerminal" }), Ct))!;
        Assert.False(withTerminal.Properties.ContainsKey("terminal")); // CodeLLDB runs it in the integrated terminal (runInTerminal)
    }

    /// <summary>A stand-in that is never called: only its presence matters.</summary>
    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    [Fact]
    public async Task Env_values_are_strings_for_codelldb_and_nulls_are_dropped()
    {
        var r = (await Make().ResolveAsync(null, Cfg(new
        {
            type = "lldb", request = "launch", name = "r", program = "/p",
            env = new Dictionary<string, object?> { ["RUST_BACKTRACE"] = "1", ["LANG"] = null, ["N"] = 3 },
        }), Ct))!;
        var env = r.Properties["env"];
        Assert.Equal(["RUST_BACKTRACE=1", "N=3"], env.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}"));
        var untouched = Cfg(new { type = "lldb", request = "launch", name = "r", program = "/p", env = new { A = "b" } });
        Assert.Equal("""{"A":"b"}""", (await Make().ResolveAsync(null, untouched, Ct))!.Properties["env"].GetRawText());
    }

    [Fact]
    public async Task The_standard_librarys_frames_are_mapped_onto_rust_src()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "POSIX shell stand-ins");
        using var dir = new TempDirectory("codelldb-");
        var (bin, sysroot) = FakeRust(dir);
        var f = Make(env: new() { ["PATH"] = bin });
        var r = (await f.ResolveAsync(dir.Uri, Cfg(new { type = "lldb", request = "launch", name = "r", program = "/p" }, dir.Uri), Ct))!;
        Assert.Equal(Path.Combine(sysroot, "lib", "rustlib", "src", "rust"), r.Properties["sourceMap"].GetProperty("/rustc/48a229ceaefd4985c50990b14116b6d856af0985").GetString());

        // the user's own mapping of that prefix wins; other entries are kept
        var mine = (await f.ResolveAsync(dir.Uri, Cfg(new
        {
            type = "lldb", request = "launch", name = "r", program = "/p",
            sourceMap = new Dictionary<string, string> { ["/rustc/48a229ceaefd4985c50990b14116b6d856af0985"] = "/my/src", ["/build"] = "/here" },
        }, dir.Uri), Ct))!;
        Assert.Equal("""{"/rustc/48a229ceaefd4985c50990b14116b6d856af0985":"/my/src","/build":"/here"}""", mine.Properties["sourceMap"].GetRawText());

        // without rustc: no mapping
        var none = (await Make(env: new() { ["PATH"] = dir.Combine("nothing") }).ResolveAsync(dir.Uri, Cfg(new { type = "lldb", request = "launch", name = "r", program = "/p" }, dir.Uri), Ct))!;
        Assert.False(none.Properties.ContainsKey("sourceMap"));
    }

    [Theory]
    [InlineData("pid", "4242")]
    [InlineData("processId", "4242")]
    public async Task Attach_takes_the_pid_as_a_number_also_when_pickProcess_gave_text(string key, string pid)
    {
        var props = new Dictionary<string, object?> { ["type"] = "lldb", ["request"] = "attach", ["name"] = "a", [key] = pid };
        var r = (await Make().ResolveAsync(null, Cfg(props), Ct))!;
        Assert.Equal(4242, r.Properties["pid"].GetInt32());
        Assert.False(r.Properties.ContainsKey("processId"));
        Assert.False(r.Properties.ContainsKey("terminal")); // launch only
        Assert.False(r.Properties.ContainsKey("cwd"));

        var window = new RecordingWindowService();
        Assert.Null(await Make(window: window).ResolveAsync(null, Cfg(new { type = "lldb", request = "attach", name = "a", pid = "abc" }), Ct));
        Assert.Contains(window.Messages, m => m.Message.Contains("'abc' is not a process id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cargo_key_that_is_neither_an_object_nor_an_argument_list_is_refused_with_a_message()
    {
        var window = new RecordingWindowService();
        Assert.Null(await Make(window: window).ResolveAsync(null, Cfg(new { type = "lldb", request = "launch", name = "c", cargo = "test" }), Ct));
        Assert.Contains(window.Messages, m => m.Message.Contains("\"cargo\"", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ templates

    [Fact]
    public async Task A_cargo_package_gets_codelldb_binary_unit_test_and_attach_templates_and_the_cargo_build_task()
    {
        using var dir = new TempDirectory("codelldbtemplates-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"my-app\"\nversion = \"0.1.0\"\n");
        dir.WriteFile("src/main.rs", "fn main() {}\n");
        dir.WriteFile("src/lib.rs", "pub fn f() {}\n");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        var configs = await f.ProvideInitialAsync(dir.Uri, Ct);
        Assert.Equal(["Rust: Debug my-app (CodeLLDB, cargo build)", "Rust: Debug unit tests of my-app (CodeLLDB, cargo test)", "Rust: Attach to my-app (CodeLLDB)"], configs.Select(c => c.Name));
        Assert.All(configs, c => Assert.Equal("lldb", c.Type));
        Assert.All(configs, c => Assert.Equal(["rust"], c.Properties["sourceLanguages"].EnumerateArray().Select(l => l.GetString())));
        Assert.Equal("${workspaceFolder}/target/debug/my-app", configs[0].GetString("program"));
        Assert.Equal("cargo: build", configs[0].PreLaunchTask);
        Assert.Equal("console", configs[0].GetString("terminal"));
        var cargo = configs[1].Properties["cargo"];
        Assert.Equal(["test", "--no-run"], cargo.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("lib", cargo.GetProperty("filter").GetProperty("kind").GetString());
        Assert.False(configs[1].Properties.ContainsKey("program"));
        Assert.Equal("${command:pickProcess}", configs[2].GetString("pid"));
        Assert.True(configs[2].IsAttach);

        var (path, content) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
        Assert.Equal("tasks.json", path);
        Assert.Equal(CargoPackage.BuildTasksJson, content); // the same cargo: build task as the GDB templates
        using var tasks = JsonDocument.Parse(content);
        Assert.Equal("cargo: build", tasks.RootElement.GetProperty("tasks")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_folder_without_cargo_toml_gets_plain_lldb_templates_and_no_task()
    {
        using var dir = new TempDirectory("codelldbtemplates-");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        Assert.Equal(["LLDB: Launch program", "LLDB: Attach to process"], (await f.ProvideInitialAsync(dir.Uri, Ct)).Select(c => c.Name));
        Assert.Empty(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
    }

    // ------------------------------------------------------------------ module

    [Fact]
    public async Task The_module_registers_lldb_with_the_codelldb_quirks_and_the_rust_breakpoint_language()
    {
        var services = new ServiceCollection().AddCsCodeDap();
        await using var sp = services.BuildServiceProvider();
        var lldb = Assert.Single(sp.GetServices<BuiltinDebugAdapter>(), b => b.Type == "lldb");
        Assert.Equal("Rust / C / C++ / Zig (CodeLLDB)", lldb.Label);
        Assert.Same(CodeLldbAdapterQuirks.Instance, lldb.Quirks);
        Assert.Equal(["rust", "c", "cpp", "zig"], lldb.Languages);
        Assert.Same(sp.GetRequiredService<CodeLldbAdapterFactory>(), lldb.Factory);
        Assert.Same(lldb.Factory, lldb.Provider);
    }
}
