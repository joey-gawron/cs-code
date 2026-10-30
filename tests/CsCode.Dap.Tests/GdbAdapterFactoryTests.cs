using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The built-in GDB factory (GDB's native DAP, MUST 1): the command line it builds (Rust pretty-printers, std sources, CsCode's helper, user
/// arguments, environment), what the provider fills in, the cargo templates, and the stdio transport's priority, marker and orphan reaping.
/// No real GDB needed: stand-in scripts.
/// </summary>
public class GdbAdapterFactoryTests
{
    private static GdbAdapterFactory Make(TestConfigurationService? settings = null, Dictionary<string, string?>? env = null, string? helperDir = null,
        FakeWorkspace? ws = null, RecordingWindowService? window = null) =>
        new(new DapPlatform { Configuration = settings, Workspace = ws, Window = window })
        {
            Environment = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
            HelperDirectory = helperDir ?? "/nonexistent",
        };

    private static DebugConfiguration Cfg(object o, Uri? folder = null) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(o), folder);

    private static string Script(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A fake Rust toolchain: <c>rustc</c> that reports a sysroot with the pretty-printers and the rust-src component.</summary>
    private static (string Bin, string Sysroot) FakeRust(TempDirectory dir, string commit = "48a229ceaefd4985c50990b14116b6d856af0985")
    {
        var sysroot = dir.Combine("sysroot");
        Directory.CreateDirectory(Path.Combine(sysroot, "lib", "rustlib", "src", "rust", "library"));
        dir.WriteFile("sysroot/lib/rustlib/etc/gdb_load_rust_pretty_printers.py", "# fake\n");
        var bin = dir.Combine("bin");
        Script(bin, "rustc", $"""
            case "$1" in
              --print) echo "{sysroot}" ;;
              -vV) printf 'rustc 1.98.1 (48a229cea 2026-09-01)\nbinary: rustc\ncommit-hash: {commit}\nhost: x86_64-unknown-linux-gnu\n' ;;
            esac

            """);
        return (bin, sysroot);
    }

    [Fact]
    public async Task It_runs_gdb_in_dap_mode_below_normal_priority_with_the_rust_pretty_printers_and_std_sources()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "POSIX shell stand-ins");
        using var dir = new TempDirectory("gdbfactory-");
        var gdb = Script(dir.Combine("gdbbin"), "gdb", "exit 0\n");
        var (bin, sysroot) = FakeRust(dir);
        dir.WriteFile("helper/cscode_gdb.py", "# helper\n");
        var settings = new TestConfigurationService();
        settings.Set(GdbAdapterFactory.SettingPath, gdb);
        settings.Set(GdbAdapterFactory.ArgsSettingPath, new[] { "-nx", "-ex", "set print pretty on" });
        var f = Make(settings, new() { ["PATH"] = bin }, dir.Combine("helper"));
        foreach (var type in new[] { "gdb", "rust-gdb", "GDB" })
        {
            var d = Assert.IsType<DebugAdapterStdioProcess>(await f.CreateDescriptorAsync(Cfg(new { type, request = "launch", name = "r" }, dir.Uri), Ct));
            Assert.Equal(gdb, d.Command);
            Assert.True(d.BelowNormalPriority);
            Assert.Equal("gdb", d.DisplayName);
            Assert.Equal(dir.Path, d.Cwd);
            Assert.Equal(
            [
                "-nx", "-ex", "set print pretty on", "-q",
                "-iex", $"source {sysroot}/lib/rustlib/etc/gdb_load_rust_pretty_printers.py",
                "-iex", $"set substitute-path /rustc/48a229ceaefd4985c50990b14116b6d856af0985 {sysroot}/lib/rustlib/src/rust",
                "-iex", $"source {dir.Combine("helper", "cscode_gdb.py")}",
                "-i=dap",
            ], d.Args);
        }
        Assert.Null(await f.CreateDescriptorAsync(Cfg(new { type = "pwa-node", request = "launch", name = "n" }), Ct));
    }

    [Fact]
    public async Task Without_rustc_gdb_runs_without_the_rust_extras_and_without_gdb_the_start_fails_with_advice()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "POSIX shell stand-ins");
        using var dir = new TempDirectory("gdbfactory-");
        var gdb = Script(dir.Combine("bin"), "gdb", "exit 0\n");
        var d = Assert.IsType<DebugAdapterStdioProcess>(await Make(env: new() { ["PATH"] = dir.Combine("bin") }).CreateDescriptorAsync(Cfg(new { type = "gdb", request = "launch", name = "r" }), Ct));
        Assert.Equal(gdb, d.Command);
        Assert.Equal(["-q", "-i=dap"], d.Args);

        var missing = await Assert.ThrowsAsync<CsCode.Dap.Session.DebugSessionStartException>(async () =>
            await Make(env: new() { ["PATH"] = dir.Combine("nothing") }).CreateDescriptorAsync(Cfg(new { type = "gdb", request = "launch", name = "r" }), Ct));
        Assert.Contains("GDB was not found", missing.Message, StringComparison.Ordinal);
        Assert.Contains("cscode.debug.gdbPath", missing.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ provider

    [Fact]
    public async Task The_provider_turns_env_into_gdb_environment_commands_and_fills_in_the_folder()
    {
        using var dir = new TempDirectory("gdbfactory-");
        var f = Make();
        var resolved = (await f.ResolveAsync(dir.Uri, Cfg(new
        {
            type = "rust-gdb", request = "launch", name = "r", program = "/p",
            env = new Dictionary<string, object?> { ["RUST_BACKTRACE"] = "1", ["SPACED"] = "a b", ["LANG"] = null, ["bad name"] = "x" },
        }, dir.Uri), Ct))!;
        Assert.Equal("gdb", resolved.Type);
        Assert.Equal("gdb", resolved.Properties["type"].GetString());
        Assert.False(resolved.Properties.ContainsKey("env")); // GDB's own env would REPLACE the whole environment
        Assert.Equal(dir.Path.TrimEnd('/'), resolved.GetString("cwd"));
        Assert.Equal(["set environment RUST_BACKTRACE=1", "set environment SPACED=a b", "unset environment LANG"], GdbAdapterFactory.EnvironmentCommands(resolved));
        var d = Assert.IsType<DebugAdapterStdioProcess>(await Make(env: new() { ["PATH"] = "/usr/bin:/bin" }).CreateDescriptorAsync(resolved, Ct)
            ?? throw new Xunit.Sdk.XunitException("no descriptor"));
        Assert.Contains("set environment RUST_BACKTRACE=1", d.Args);
    }

    [Fact]
    public async Task An_env_value_with_a_line_break_is_refused_with_a_message()
    {
        var window = new RecordingWindowService();
        var r = await Make(window: window).ResolveAsync(null, Cfg(new { type = "gdb", request = "launch", name = "r", env = new { X = "a\nb" } }), Ct);
        Assert.Null(r);
        Assert.Contains(window.Messages, m => m.Message.Contains("'X'", StringComparison.Ordinal) && m.Message.Contains("line break", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pid", "4242")]
    [InlineData("processId", "4242")]
    public async Task Attach_takes_the_pid_as_a_number_also_when_pickProcess_gave_text(string key, string pid)
    {
        var props = new Dictionary<string, object?> { ["type"] = "gdb", ["request"] = "attach", ["name"] = "a", [key] = pid };
        var r = (await Make().ResolveAsync(null, Cfg(props), Ct))!;
        Assert.Equal(JsonValueKind.Number, r.Properties["pid"].ValueKind);
        Assert.Equal(4242, r.Properties["pid"].GetInt32());
        Assert.False(r.Properties.ContainsKey("processId"));

        var window = new RecordingWindowService();
        Assert.Null(await Make(window: window).ResolveAsync(null, Cfg(new { type = "gdb", request = "attach", name = "a", pid = "abc" }), Ct));
        Assert.Contains(window.Messages, m => m.Message.Contains("'abc' is not a process id", StringComparison.Ordinal));
    }

    [Fact]
    public void Cargo_reports_its_executables_in_compiler_artifact_messages()
    {
        var stdout = """
            {"reason":"compiler-artifact","package_id":"x","target":{"kind":["lib"],"crate_types":["lib"],"name":"helper","src_path":"/w/src/lib.rs","test":true},"profile":{"test":false},"executable":null,"fresh":true}
            {"reason":"compiler-artifact","package_id":"x","target":{"kind":["bin"],"crate_types":["bin"],"name":"sample","src_path":"/w/src/main.rs","test":true},"profile":{"test":true},"executable":"/w/target/debug/deps/sample-c77dd8832268fcec","fresh":false}
            {"reason":"compiler-artifact","package_id":"x","target":{"kind":["test"],"crate_types":["bin"],"name":"integration","src_path":"/w/tests/integration.rs","test":true},"profile":{"test":true},"executable":"/w/target/debug/deps/integration-0123","fresh":false}
            not json at all
            {"reason":"build-finished","success":true}
            """;
        var artifacts = CargoArtifacts.Parse(stdout);
        Assert.Equal(
            [("sample", "bin", "/w/target/debug/deps/sample-c77dd8832268fcec", true), ("integration", "test", "/w/target/debug/deps/integration-0123", true)],
            artifacts.Select(a => (a.Name, a.Kinds[0], a.Executable, a.IsTest)));
    }

    // ------------------------------------------------------------------ templates

    [Fact]
    public async Task A_cargo_package_gets_binary_unit_test_and_attach_templates_and_a_cargo_build_process_task()
    {
        using var dir = new TempDirectory("gdbtemplates-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"my-app\" # the package\nversion = \"0.1.0\"\n\n[dependencies]\nname = \"not-this\"\n");
        dir.WriteFile("src/main.rs", "fn main() {}\n");
        dir.WriteFile("src/lib.rs", "pub fn f() {}\n");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        var configs = await f.ProvideInitialAsync(dir.Uri, Ct);
        Assert.Equal(["Rust: Debug my-app (cargo build)", "Rust: Debug unit tests of my-app (cargo test)", "Rust: Attach to my-app"], configs.Select(c => c.Name));
        Assert.All(configs, c => Assert.Equal("gdb", c.Type));
        Assert.Equal("${workspaceFolder}/target/debug/my-app", configs[0].GetString("program"));
        Assert.Equal("cargo: build", configs[0].PreLaunchTask);
        var cargo = configs[1].Properties["cargo"];
        Assert.Equal(["test", "--no-run"], cargo.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("lib", cargo.GetProperty("filter").GetProperty("kind").GetString()); // a package with a library: its unit tests
        Assert.False(configs[1].Properties.ContainsKey("program"));
        Assert.Equal("${command:pickProcess}", configs[2].GetString("pid"));
        Assert.True(configs[2].IsAttach);

        var (path, content) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
        Assert.Equal("tasks.json", path);
        using var tasks = JsonDocument.Parse(content);
        var task = tasks.RootElement.GetProperty("tasks")[0];
        Assert.Equal(("cargo: build", "process", "cargo"), (task.GetProperty("label").GetString(), task.GetProperty("type").GetString(), task.GetProperty("command").GetString()));
        Assert.Equal(["build"], task.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_binary_named_in_cargo_toml_is_the_program_and_a_package_without_a_library_tests_its_binary()
    {
        using var dir = new TempDirectory("gdbtemplates-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"pkg\"\n\n[[bin]]\nname = \"tool\"\npath = \"src/tool.rs\"\n");
        var configs = await Make(ws: new FakeWorkspace(dir.Uri)).ProvideInitialAsync(dir.Uri, Ct);
        Assert.Equal("${workspaceFolder}/target/debug/tool", configs[0].GetString("program"));
        var filter = configs[1].Properties["cargo"].GetProperty("filter");
        Assert.Equal(("tool", "bin"), (filter.GetProperty("name").GetString(), filter.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_folder_without_cargo_toml_gets_plain_gdb_templates_and_no_task()
    {
        using var dir = new TempDirectory("gdbtemplates-");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        Assert.Equal(["GDB: Launch program", "GDB: Attach to process"], (await f.ProvideInitialAsync(dir.Uri, Ct)).Select(c => c.Name));
        Assert.Empty(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
    }

    [Fact]
    public async Task The_module_registers_gdb_and_rust_gdb_with_the_gdb_quirks_and_the_rust_breakpoint_language()
    {
        var services = new ServiceCollection().AddCsCodeDap();
        await using var sp = services.BuildServiceProvider();
        var builtins = sp.GetServices<BuiltinDebugAdapter>().ToList();
        var gdb = builtins.Where(b => b.Type is "gdb" or "rust-gdb").ToList();
        Assert.Equal(["gdb", "rust-gdb"], gdb.Select(b => b.Type));
        Assert.All(gdb, b => Assert.Same(GdbAdapterQuirks.Instance, b.Quirks));
        Assert.All(gdb, b => Assert.Contains("rust", b.Languages));
        Assert.Same(gdb[0].Factory, sp.GetRequiredService<GdbAdapterFactory>());
        Assert.All(builtins.Where(b => b.Type is not ("gdb" or "rust-gdb" or "lldb")), b => Assert.Null(b.Quirks)); // lldb: CodeLLDB's own quirks
    }

    // ------------------------------------------------------------------ stdio transport: priority, marker, orphans

    [Fact]
    public async Task A_stdio_adapter_runs_niced_with_a_marker_that_its_children_inherit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && ProcessTransport.FindNice() is not null, "Linux with nice");
        var descriptor = new DebugAdapterStdioProcess("/bin/sh", ["-c", "sleep 600 & exec cat > /dev/null"]) { DisplayName = "stand-in" };
        await using var transport = ProcessTransport.Start(descriptor, NullLogger.Instance);
        var pid = transport.ProcessId!.Value;
        Assert.NotNull(transport.Marker);
        await SessionHarness.Eventually(() => ProcessTable.Descendants(pid).Count > 0, 5_000, "the stand-in's child");
        var child = ProcessTable.Descendants(pid).Single();
        Assert.Equal(10, RealGdbRig.NiceOf(pid));
        Assert.Equal(10, RealGdbRig.NiceOf(child));
        Assert.Equal([pid, child], ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", transport.Marker!).Order());
        transport.KillNow(); // the adapter's tree, its child included
        await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid) && !ProcessTable.IsAlive(child), 5_000, "the tree is gone");
    }

    /// <summary>An adapter that dies (killed by a signal) orphans the debuggee it launched: the processes carrying its marker are reaped.</summary>
    [Fact]
    public async Task The_processes_a_crashed_stdio_adapter_orphaned_are_reaped_but_not_after_a_normal_exit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux /proc");
        var crashed = new DebugAdapterStdioProcess("/bin/sh", ["-c", "sleep 600 & exec cat > /dev/null"]);
        await using (var transport = ProcessTransport.Start(crashed, NullLogger.Instance))
        {
            var pid = transport.ProcessId!.Value;
            await SessionHarness.Eventually(() => ProcessTable.Descendants(pid).Count > 0, 5_000, "the debuggee");
            var orphan = ProcessTable.Descendants(pid).Single();
            using (var p = Process.GetProcessById(pid)) p.Kill(); // the adapter alone (SIGKILL): its child is re-parented
            await transport.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), 5_000, "the adapter is gone");
            Assert.True(ProcessTable.IsAlive(orphan));
            await transport.CloseAsync(TimeSpan.Zero);
            await SessionHarness.Eventually(() => !ProcessTable.IsAlive(orphan), 5_000, "the orphan is reaped");
        }

        // GDB quits with code 0 after `disconnect`, having killed or detached its debuggee itself: what is left was left on purpose
        var normal = new DebugAdapterStdioProcess("/bin/sh", ["-c", "sleep 600 & exit 0"]);
        await using var quitting = ProcessTransport.Start(normal, NullLogger.Instance);
        var marker = quitting.Marker!;
        await quitting.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var left = ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker);
        try
        {
            await quitting.CloseAsync(TimeSpan.Zero);
            Assert.All(left, p => Assert.True(ProcessTable.IsAlive(p), "not reaped after a normal exit"));
        }
        finally
        {
            foreach (var p in left) try { using var proc = Process.GetProcessById(p); proc.Kill(); } catch (ArgumentException) { }
        }
    }
}
