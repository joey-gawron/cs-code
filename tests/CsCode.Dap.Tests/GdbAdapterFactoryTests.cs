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
        FakeWorkspace? ws = null, RecordingWindowService? window = null, string? privateRoot = null) =>
        new(new DapPlatform { Configuration = settings, Workspace = ws, Window = window })
        {
            Environment = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
            HelperDirectory = helperDir ?? "/nonexistent",
            PrivateRoot = privateRoot,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_missing_gdb_advice_points_a_mac_at_codelldb(bool macOS)
    {
        var message = GdbAdapterFactory.NotFoundMessage(macOS);
        Assert.Contains("GDB was not found", message, StringComparison.Ordinal);
        Assert.Contains("cscode.debug.gdbPath", message, StringComparison.Ordinal);
        Assert.Equal(macOS, message.Contains("CodeLLDB (type 'lldb'", StringComparison.Ordinal)); // GDB cannot debug native arm64 macOS programs
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
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the environment goes through a private file on Unix only");
        using var root = new TempDirectory("gdbenv-");
        Script(dir.Combine("bin"), "gdb", "exit 0\n"); // a stand-in: no GDB needs to be installed (a Mac has none)
        var factory = Make(env: new() { ["PATH"] = dir.Combine("bin") + ":/usr/bin:/bin" }, privateRoot: root.Path);
        var d = Assert.IsType<DebugAdapterStdioProcess>(await factory.CreateDescriptorAsync(resolved, Ct) ?? throw new Xunit.Sdk.XunitException("no descriptor"));
        // The values (often secrets) are not on GDB's command line (/proc/<pid>/cmdline is world-readable): GDB reads them from a private file,
        // written when a gdb starts (each start: see Each_gdb_started_from_the_descriptor_gets_its_own_private_environment_file...).
        Assert.DoesNotContain(d.Args, a => a.Contains("RUST_BACKTRACE", StringComparison.Ordinal) || a.Contains("a b", StringComparison.Ordinal));
        Assert.Equal(PrivateStartupFile.Placeholder, d.Args[d.Args.ToList().IndexOf("-ix") + 1]);
        Assert.True(d.Args.ToList().IndexOf("-ix") < d.Args.ToList().IndexOf("-i=dap"));
        Assert.Equal(["set environment RUST_BACKTRACE=1", "set environment SPACED=a b", "unset environment LANG"], d.PrivateFile!.Lines);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path)); // nothing written before a gdb starts
        Assert.DoesNotContain("RUST_BACKTRACE", d.ToString(), StringComparison.Ordinal); // a logged descriptor shows no value
    }

    /// <summary>
    /// GDB has no <c>restart</c> request: a restart starts a NEW gdb from the same descriptor, after the first gdb's file was deleted with it
    /// (end-pass review, debug-2). Each start writes its own private file (0600 in a 0700 directory), which goes with that gdb; a start that
    /// fails leaves nothing behind. A stand-in gdb records what its <c>-ix</c> file holds.
    /// </summary>
    [Fact]
    public async Task Each_gdb_started_from_the_descriptor_gets_its_own_private_environment_file_which_goes_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "POSIX shell stand-in, GNU stat");
        using var dir = new TempDirectory("gdbenv-starts-");
        var root = dir.Combine("private");
        Directory.CreateDirectory(root);
        var seen = dir.Combine("seen");
        Directory.CreateDirectory(seen);
        var gdb = Script(dir.Combine("bin"), "gdb", $$"""
            while [ $# -gt 0 ]; do
              if [ "$1" = -ix ]; then
                { cat "$2"; echo "modes $(stat -c %a "$2") $(stat -c %a "$(dirname "$2")") $(dirname "$2")"; } > "{{seen}}/$$.tmp" && mv "{{seen}}/$$.tmp" "{{seen}}/$$"
              fi
              shift
            done
            exec cat > /dev/null

            """);
        var settings = new TestConfigurationService();
        settings.Set(GdbAdapterFactory.SettingPath, gdb);
        var factory = Make(settings, new() { ["PATH"] = "/usr/bin:/bin" }, privateRoot: root);
        var config = Cfg(new { type = "gdb", request = "launch", name = "r", program = "/bin/sh", __cscodeEnvironment = new { CSCODE_SECRET_PROBE = "s3cret value" } });
        var d = Assert.IsType<DebugAdapterStdioProcess>(await factory.CreateDescriptorAsync(config, Ct));

        async Task<string[]> RunOnceAsync()
        {
            var transport = ProcessTransport.Start(d, NullLogger.Instance);
            string[] lines;
            try
            {
                var record = Path.Combine(seen, transport.ProcessId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await SessionHarness.Eventually(() => File.Exists(record), 10_000, "the stand-in gdb read its -ix file");
                lines = File.ReadAllLines(record);
                var cmdline = File.ReadAllText($"/proc/{transport.ProcessId}/cmdline");
                Assert.DoesNotContain("s3cret", cmdline, StringComparison.Ordinal);
            }
            finally { await transport.DisposeAsync(); }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root)); // the directory went with that gdb
            return lines;
        }

        var first = await RunOnceAsync();
        var second = await RunOnceAsync(); // a restart: the same descriptor, a new gdb
        foreach (var lines in new[] { first, second })
        {
            Assert.Equal("set environment CSCODE_SECRET_PROBE=s3cret value", lines[0]);
            Assert.StartsWith("modes 600 700 " + root + "/" + GdbAdapterFactory.EnvironmentFilePrefix + "-", lines[1], StringComparison.Ordinal);
        }
        Assert.NotEqual(first[1], second[1]); // a fresh directory each time

        // a gdb that cannot be started leaves no private directory behind: started directly (the start throws) or through nice (which exits)
        var missing = d with { Command = dir.Combine("bin", "no-such-gdb") };
        Assert.Throws<InvalidOperationException>(() => ProcessTransport.Start(missing with { BelowNormalPriority = false }, NullLogger.Instance));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        var throughNice = ProcessTransport.Start(missing, NullLogger.Instance);
        try
        {
            await throughNice.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root)); // gone when it exited, before it is disposed
        }
        finally { await throughNice.DisposeAsync(); }
    }

    /// <summary>
    /// A real <c>gdb -i=dap</c> started from the descriptor: no argument of the running GDB carries a launch environment value, and the private
    /// file is gone once GDB is. (That GDB applies the commands, also after a restart, is covered by RealGdbRustTests' env tests.)
    /// </summary>
    [Fact]
    public async Task A_real_gdb_gets_the_launch_environment_from_the_private_file_and_the_file_goes_with_gdb()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var gdb = RustToolchainLocator.FindOnPath(System.Environment.GetEnvironmentVariable, "gdb");
        Assert.SkipWhen(gdb is null, "gdb is not on PATH");
        using var root = new TempDirectory("gdbenv-real-");
        var factory = Make(env: new() { ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") }, privateRoot: root.Path);
        var config = Cfg(new { type = "gdb", request = "launch", name = "r", program = "/bin/sh", __cscodeEnvironment = new { CSCODE_SECRET_PROBE = "s3cret-value" } });
        var d = Assert.IsType<DebugAdapterStdioProcess>(await factory.CreateDescriptorAsync(config, Ct));
        var transport = CsCode.Dap.Transport.ProcessTransport.Start(d, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        try
        {
            var cmdline = File.ReadAllText($"/proc/{transport.ProcessId}/cmdline").Replace('\0', ' ');
            Assert.DoesNotContain("s3cret-value", cmdline, StringComparison.Ordinal);
            Assert.Contains("-ix " + root.Path + "/" + GdbAdapterFactory.EnvironmentFilePrefix + "-", cmdline, StringComparison.Ordinal);
            Assert.Single(Directory.EnumerateDirectories(root.Path));
        }
        finally { await transport.DisposeAsync(); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path)); // the private directory did not outlive GDB
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
        // lldb: CodeLLDB's own quirks; debugpy / python: debugpy's (its debugpyAttach child event)
        Assert.All(builtins.Where(b => b.Type is not ("gdb" or "rust-gdb" or "lldb" or "debugpy" or "python")), b => Assert.Null(b.Quirks));
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
