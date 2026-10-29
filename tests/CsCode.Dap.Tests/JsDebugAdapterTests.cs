using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>The built-in js-debug factory: lookup order, the descriptor it produces, the configuration it fills in, the templates it offers.</summary>
public class JsDebugFactoryTests
{
    private static JsDebugAdapterFactory Make(TestConfigurationService? settings = null, TestAppPaths? paths = null, RecordingWindowService? window = null,
        Dictionary<string, string?>? env = null, JsDebugInstaller? installer = null, FakeWorkspace? ws = null) =>
        new(new DapPlatform { Configuration = settings, Paths = paths, Window = window, Workspace = ws }, installer)
        {
            Environment = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
        };

    private static string FakeJsDebug(string root)
    {
        var script = Path.Combine(root, "js-debug", "src", "dapDebugServer.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "// fake\n");
        return script;
    }

    private static string FakeNode(string dir)
    {
        Directory.CreateDirectory(dir);
        var node = Path.Combine(dir, JsDebugAdapterFactory.NodeExecutableName);
        File.WriteAllText(node, "#!/bin/sh\n");
        return node;
    }

    private static DebugConfiguration Cfg(object o, Uri? folder = null) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(o), folder);

    [Fact]
    public async Task It_starts_dapDebugServer_on_node_on_a_private_socket_below_normal_priority()
    {
        using var scratch = new TempDirectory();
        using var runtime = new TempDirectory();
        var script = FakeJsDebug(scratch.Path);
        var node = FakeNode(Path.Combine(scratch.Path, "bin"));
        var f = Make(env: new() { ["CSCODE_JSDEBUG"] = Path.Combine(scratch.Path, "js-debug"), ["PATH"] = Path.GetDirectoryName(node), ["XDG_RUNTIME_DIR"] = runtime.Path });
        foreach (var type in new[] { "pwa-node", "node", "PWA-NODE" })
        {
            var d = Assert.IsType<DebugAdapterServerProcess>(await f.CreateDescriptorAsync(Cfg(new { type, request = "launch", name = "n" }), Ct));
            Assert.Equal(node, d.Command);
            // a socket path, never a TCP port that any local process could connect to (dapDebugServer.js: "[port|socket path=8123] [host=localhost]")
            Assert.Equal([script, DebugAdapterServerProcess.SocketPathArgument], d.Args);
            Assert.True(d.PrivateSocket);
            Assert.Equal(runtime.Path, d.SocketRoot);
            Assert.Equal(JsDebugAdapterFactory.SocketPrefix, d.SocketPrefix);
            Assert.True(d.BelowNormalPriority);
            Assert.Matches(d.ListeningPattern, "Debug server listening at /run/user/1000/cscode-jsdebug-1-2-ab/dap.sock");
            Assert.Matches(d.ListeningPattern, @"Debug server listening at \\.\pipe\cscode-jsdebug-ab");
            Assert.Matches(d.ListeningPattern, "Debug server listening at 127.0.0.1:41235"); // the server's TCP form still reads as such
            var m = System.Text.RegularExpressions.Regex.Match("Debug server listening at 127.0.0.1:41235", d.ListeningPattern);
            Assert.Equal(("127.0.0.1", "41235"), (m.Groups["host"].Value, m.Groups["port"].Value));
        }
        // without XDG_RUNTIME_DIR: the temp directory
        var noRuntime = Make(env: new() { ["CSCODE_JSDEBUG"] = Path.Combine(scratch.Path, "js-debug"), ["PATH"] = Path.GetDirectoryName(node) });
        var plain = Assert.IsType<DebugAdapterServerProcess>(await noRuntime.CreateDescriptorAsync(Cfg(new { type = "pwa-node", request = "launch", name = "n" }), Ct));
        Assert.Equal(Path.GetTempPath(), plain.SocketRoot);
        Assert.Null(await f.CreateDescriptorAsync(Cfg(new { type = "coreclr", request = "launch", name = "n" }), Ct));
    }

    [Fact]
    public void The_setting_wins_over_CSCODE_JSDEBUG_which_wins_over_the_installed_copy()
    {
        using var scratch = new TempDirectory();
        using var paths = new TestAppPaths();
        var viaSetting = FakeJsDebug(Path.Combine(scratch.Path, "setting"));
        var viaEnv = FakeJsDebug(Path.Combine(scratch.Path, "env"));
        var installed = JsDebugInstaller.ServerScript(paths.ToolsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "// installed\n");
        var settings = new TestConfigurationService();
        var env = new Dictionary<string, string?>();
        var f = Make(settings, paths, env: env);

        Assert.Equal(installed, f.Locate());
        env["CSCODE_JSDEBUG"] = viaEnv; // the script itself
        Assert.Equal(viaEnv, f.Locate());
        settings.Set(JsDebugAdapterFactory.SettingPath, Path.Combine(scratch.Path, "setting")); // the install directory (contains js-debug/)
        Assert.Equal(viaSetting, f.Locate());
        settings.Set(JsDebugAdapterFactory.SettingPath, "/does/not/exist");
        Assert.Equal(viaEnv, f.Locate()); // a dead setting falls through
        File.Delete(installed);
        env.Clear();
        Assert.Null(f.Locate());
    }

    [Theory]
    [InlineData("")]                 // the install directory (<version>/) containing js-debug/
    [InlineData("js-debug")]         // the js-debug directory
    [InlineData("js-debug/src")]     // its src/
    [InlineData("js-debug/src/dapDebugServer.js")]
    public void Any_of_the_usual_paths_resolves_to_the_server_script(string relative)
    {
        using var scratch = new TempDirectory();
        var script = FakeJsDebug(scratch.Path);
        Assert.Equal(script, JsDebugAdapterFactory.ResolveServerScript(Path.Combine(scratch.Path, relative)));
        Assert.Null(JsDebugAdapterFactory.ResolveServerScript(Path.Combine(scratch.Path, "nothing-here")));
    }

    [Fact]
    public async Task Without_node_the_start_fails_with_actionable_instructions()
    {
        using var scratch = new TempDirectory();
        FakeJsDebug(scratch.Path);
        var f = Make(env: new() { ["CSCODE_JSDEBUG"] = Path.Combine(scratch.Path, "js-debug"), ["PATH"] = scratch.Path });
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await f.CreateDescriptorAsync(Cfg(new { type = "pwa-node", request = "launch", name = "n" }), Ct));
        Assert.Contains("Node.js", ex.Message);
        Assert.Contains(JsDebugAdapterFactory.NodeSettingPath, ex.Message);
    }

    [Fact]
    public async Task Without_js_debug_the_user_is_asked_before_anything_is_downloaded_and_a_refusal_installs_nothing()
    {
        using var scratch = new TempDirectory();
        using var paths = new TestAppPaths();
        var node = FakeNode(Path.Combine(scratch.Path, "bin"));
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Cancel");
        var http = new JsDebugInstallerTests.ServeBytes([1, 2, 3]);
        var f = Make(paths: paths, window: window, env: new() { ["PATH"] = Path.GetDirectoryName(node) }, installer: new JsDebugInstaller(http));
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await f.CreateDescriptorAsync(Cfg(new { type = "pwa-node", request = "launch", name = "n" }), Ct));
        Assert.Contains("fetch-js-debug.sh", ex.Message);
        var offer = Assert.Single(window.Messages);
        Assert.Contains("js-debug adapter v1.140.0", offer.Message);
        Assert.Contains("MIT", offer.Message);
        Assert.Empty(http.Requests);
        Assert.False(Directory.Exists(Path.Combine(paths.ToolsDir, "js-debug")));
    }

    [Fact]
    public async Task Accepting_the_offer_installs_the_pinned_archive_and_starts_from_it()
    {
        var archive = JsDebugToolchain.FindArchive();
        Assert.SkipWhen(archive is null, "the pinned js-debug archive is not available locally (run tools/fetch-js-debug.sh); this test never downloads");
        using var scratch = new TempDirectory();
        using var paths = new TestAppPaths();
        var node = FakeNode(Path.Combine(scratch.Path, "bin"));
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var http = new JsDebugInstallerTests.ServeBytes(await File.ReadAllBytesAsync(archive!, Ct));
        var f = Make(paths: paths, window: window, env: new() { ["PATH"] = Path.GetDirectoryName(node) }, installer: new JsDebugInstaller(http));
        var d = Assert.IsType<DebugAdapterServerProcess>(await f.CreateDescriptorAsync(Cfg(new { type = "pwa-node", request = "launch", name = "n" }), Ct));
        Assert.Equal(JsDebugInstaller.ServerScript(paths.ToolsDir), d.Args[0]);
        Assert.Equal([JsDebugInstaller.DownloadUrl.ToString()], http.Requests);
        Assert.Equal(JsDebugInstaller.ServerScript(paths.ToolsDir), f.Locate()); // no second offer next time
    }

    [Fact]
    public async Task The_provider_sets_workspaceFolder_for_source_maps_rewrites_the_node_alias_and_leaves_children_alone()
    {
        using var folder = new TempDirectory();
        var f = Make();
        var resolved = (await f.ResolveAsync(folder.Uri, Cfg(new { type = "node", request = "launch", name = "n", program = "/x/out/main.js" }, folder.Uri), Ct))!;
        Assert.Equal("pwa-node", resolved.Type);
        Assert.Equal("pwa-node", resolved.GetString("type"));
        Assert.Equal(folder.Path.TrimEnd('/'), resolved.GetString("__workspaceFolder"));
        Assert.Equal("/x/out/main.js", resolved.GetString("program"));

        var own = (await f.ResolveAsync(folder.Uri, Cfg(new { type = "pwa-node", request = "launch", name = "n", __workspaceFolder = "/mine" }), Ct))!;
        Assert.Equal("/mine", own.GetString("__workspaceFolder")); // never overrides the user

        var noFolder = (await f.ResolveAsync(null, Cfg(new { type = "pwa-node", request = "launch", name = "n", program = "/p/out/a.js" }), Ct))!;
        Assert.Equal("/p/out", noFolder.GetString("__workspaceFolder"));

        var child = Cfg(new { type = "pwa-node", name = "main.js [1]", __pendingTargetId = "abc" });
        Assert.Same(child, await f.ResolveAsync(folder.Uri, child, Ct));
        var other = Cfg(new { type = "coreclr", request = "launch", name = "n" });
        Assert.Same(other, await f.ResolveAsync(folder.Uri, other, Ct));
    }

    [Fact]
    public async Task A_typescript_folder_gets_a_tsc_build_launch_template_and_the_task_it_needs()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("tsconfig.json", """{ "compilerOptions": { "outDir": "./dist", "rootDir": "./src", "sourceMap": true } }""");
        h.Dir.WriteFile("src/index.ts", "console.log(1);\n");
        var f = new JsDebugAdapterFactory(h.Platform);
        var configs = await f.ProvideInitialAsync(h.Dir.Uri, Ct);
        Assert.Equal(["TypeScript: Launch (tsc build)", "Node.js: Launch Program", "Node.js: Attach (--inspect)"], configs.Select(c => c.Name));
        var ts = configs[0];
        Assert.Equal(("pwa-node", "launch", "tsc: build"), (ts.Type, ts.Request, ts.PreLaunchTask));
        Assert.Equal("${workspaceFolder}/src/index.ts", ts.GetString("program"));
        Assert.Equal("${workspaceFolder}/dist/**/*.js", ts.Properties["outFiles"][0].GetString());
        Assert.True(ts.Properties["sourceMaps"].GetBoolean());
        Assert.Equal("<node_internals>/**", ts.Properties["skipFiles"][0].GetString());
        Assert.Equal(9229, configs[2].Properties["port"].GetInt32());

        var files = await f.ProvideInitialFilesAsync(h.Dir.Uri, Ct);
        var (path, content) = Assert.Single(files);
        Assert.Equal("tasks.json", path);
        using var doc = JsonDocument.Parse(content);
        var task = doc.RootElement.GetProperty("tasks")[0];
        Assert.Equal("tsc: build", task.GetProperty("label").GetString());
        Assert.Equal("tsc", task.GetProperty("command").GetString());
        Assert.Equal(["-p", "${workspaceFolder}/tsconfig.json"], task.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_plain_javascript_folder_gets_the_node_templates_only_using_package_main()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("package.json", """{ "name": "x", "main": "./lib/app.js" }""");
        var f = new JsDebugAdapterFactory(h.Platform);
        var configs = await f.ProvideInitialAsync(h.Dir.Uri, Ct);
        Assert.Equal(["Node.js: Launch Program", "Node.js: Attach (--inspect)"], configs.Select(c => c.Name));
        Assert.Equal("${workspaceFolder}/lib/app.js", configs[0].GetString("program"));
        Assert.Empty(await f.ProvideInitialFilesAsync(h.Dir.Uri, Ct));
    }

    [Fact]
    public async Task Create_launch_json_for_pwa_node_writes_the_templates_and_tasks_json()
    {
        await using var h = new ServiceHarness(moreBuiltins: p => CsCode.Dap.ServiceCollectionExtensions.JsDebugAdapters().Select(m => m(new JsDebugAdapterFactory(p))));
        h.Dir.WriteFile("tsconfig.json", """{ "compilerOptions": { "outDir": "out" } }""");
        h.Dir.WriteFile("src/main.ts", "console.log(1);\n");
        var uri = await h.Configurations.CreateLaunchJsonAsync("pwa-node", h.Dir.Uri, Ct);
        var launch = await File.ReadAllTextAsync(uri.LocalPath, Ct);
        Assert.Contains("\"preLaunchTask\": \"tsc: build\"", launch);
        Assert.Contains("${workspaceFolder}/src/main.ts", launch);
        Assert.True(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "tasks.json")));
        Assert.Contains(h.Service.DebuggerTypes, t => t.Type == "pwa-node" && t.Languages.Contains("typescript") && t.Languages.Contains("javascript"));
    }
}

/// <summary>The pinned js-debug installer and tools/fetch-js-debug.sh: digest checked against the local archive and a corrupted copy, never downloading.</summary>
public class JsDebugInstallerTests
{
    /// <summary>Serves fixed bytes for any URL and records the requests (no network).</summary>
    internal sealed class ServeBytes(byte[] bytes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static string RequireArchive()
    {
        var archive = JsDebugToolchain.FindArchive();
        Assert.SkipWhen(archive is null, "the pinned js-debug archive is not available locally (run tools/fetch-js-debug.sh); these tests never download");
        return archive!;
    }

    private static string Corrupt(string archive, TempDirectory dir)
    {
        var bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0x5A;
        var path = Path.Combine(dir.Path, Path.GetFileName(archive));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task The_pinned_digest_matches_the_local_archive()
    {
        var archive = RequireArchive();
        Assert.Equal(JsDebugInstaller.Sha256, await JsDebugInstaller.ComputeSha256Async(archive, Ct));
        Assert.Equal(JsDebugInstaller.Size, new FileInfo(archive).Length);
        Assert.Equal(JsDebugInstaller.AssetName, Path.GetFileName(archive));
    }

    [Fact]
    public async Task Installing_from_the_local_archive_extracts_the_server_and_records_the_version()
    {
        var archive = RequireArchive();
        using var tools = new TempDirectory();
        var script = await new JsDebugInstaller().InstallFromArchiveAsync(archive, tools.Path, null, Ct);
        Assert.Equal(JsDebugInstaller.ServerScript(tools.Path), script);
        Assert.True(File.Exists(script));
        Assert.Equal("v1.140.0\n", await File.ReadAllTextAsync(Path.Combine(JsDebugInstaller.InstallDirectory(tools.Path), "VERSION"), Ct));
        Assert.Equal([JsDebugInstaller.Version], Directory.GetDirectories(Path.Combine(tools.Path, "js-debug")).Select(Path.GetFileName)); // no staging residue
    }

    [Fact]
    public async Task A_corrupted_archive_is_refused_before_extraction_and_leaves_nothing()
    {
        var archive = RequireArchive();
        using var tools = new TempDirectory();
        using var scratch = new TempDirectory();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new JsDebugInstaller().InstallFromArchiveAsync(Corrupt(archive, scratch), tools.Path, null, Ct));
        Assert.Contains("tampered", ex.Message);
        Assert.Contains(JsDebugInstaller.Sha256, ex.Message);
        Assert.False(Directory.Exists(JsDebugInstaller.InstallDirectory(tools.Path)));
        Assert.Empty(Directory.Exists(Path.Combine(tools.Path, "js-debug")) ? Directory.GetFileSystemEntries(Path.Combine(tools.Path, "js-debug")) : []);
    }

    [Fact]
    public async Task The_download_goes_to_the_pinned_url_and_a_tampered_download_leaves_no_residue()
    {
        var archive = RequireArchive();
        using var tools = new TempDirectory();
        var good = new ServeBytes(await File.ReadAllBytesAsync(archive, Ct));
        var script = await new JsDebugInstaller(good).InstallAsync(tools.Path, null, Ct);
        Assert.True(File.Exists(script));
        Assert.Equal([JsDebugInstaller.DownloadUrl.ToString()], good.Requests);
        Assert.Equal("https://github.com/microsoft/vscode-js-debug/releases/download/v1.140.0/js-debug-dap-v1.140.0.tar.gz", JsDebugInstaller.DownloadUrl.ToString());

        using var other = new TempDirectory();
        using var scratch = new TempDirectory();
        var bad = new ServeBytes(await File.ReadAllBytesAsync(Corrupt(archive, scratch), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsDebugInstaller(bad).InstallAsync(other.Path, null, Ct));
        Assert.Empty(Directory.GetFiles(other.Path, "*", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------ tools/fetch-js-debug.sh

    private static (int Exit, string Output, bool Downloaded) RunScript(string dest, string archive)
    {
        Assert.SkipUnless(File.Exists("/bin/bash") && !OperatingSystem.IsWindows(), "needs bash");
        var script = FindScript();
        using var bin = new TempDirectory("fakebin-");
        var marker = Path.Combine(bin.Path, "network-was-used");
        // curl/wget stand-ins: any download attempt is recorded and fails.
        foreach (var tool in new[] { "curl", "wget" })
        {
            var path = bin.WriteFile(tool, $"#!/bin/sh\necho \"$0 $*\" >> '{marker}'\nexit 97\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var psi = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        psi.Environment["PATH"] = bin.Path + ":" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["JSDEBUG_DEST"] = dest;
        psi.Environment["JSDEBUG_ARCHIVE"] = archive;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, File.Exists(marker));
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "fetch-js-debug.sh");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("tools/fetch-js-debug.sh");
    }

    [Fact]
    public void The_fetch_script_installs_from_a_local_archive_after_checking_its_digest_without_downloading()
    {
        var archive = RequireArchive();
        using var dest = new TempDirectory("jsdebug-fetch-");
        var (exit, output, downloaded) = RunScript(dest.Path, archive);
        Assert.True(exit == 0, output);
        Assert.False(downloaded, "the script must not download when JSDEBUG_ARCHIVE is set");
        Assert.Contains("SHA-256 verified: " + JsDebugInstaller.Sha256, output);
        Assert.True(File.Exists(Path.Combine(dest.Path, "v1.140.0", "js-debug", "src", "dapDebugServer.js")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "v1.140.0", "js-debug-dap-v1.140.0.tar.gz")));
        // The script and the app agree on where an install lives: the factory finds it from the version directory.
        Assert.NotNull(JsDebugAdapterFactory.ResolveServerScript(Path.Combine(dest.Path, "v1.140.0")));
        // Present already: a second run changes nothing.
        var again = RunScript(dest.Path, archive);
        Assert.Equal(0, again.Exit);
        Assert.Contains("already present", again.Output);
    }

    [Fact]
    public void The_fetch_script_refuses_a_corrupted_archive_and_extracts_nothing()
    {
        var archive = RequireArchive();
        using var dest = new TempDirectory("jsdebug-fetch-");
        using var scratch = new TempDirectory();
        var (exit, output, downloaded) = RunScript(dest.Path, Corrupt(archive, scratch));
        Assert.Equal(1, exit);
        Assert.False(downloaded);
        Assert.Contains("SHA-256 mismatch", output);
        Assert.False(Directory.Exists(Path.Combine(dest.Path, "v1.140.0")));
    }

    [Fact]
    public void The_script_pins_the_same_version_and_digest_as_the_installer()
    {
        var text = File.ReadAllText(FindScript());
        Assert.Contains($"version=\"{JsDebugInstaller.Version}\"", text);
        Assert.Contains($"sha256=\"{JsDebugInstaller.Sha256}\"", text);
        Assert.Contains($"size={JsDebugInstaller.Size.ToString(CultureInfo.InvariantCulture)}", text);
    }
}

/// <summary>The adapter-server transport (js-debug's model) against small shell stand-ins: listening line, failures, priority, cleanup.</summary>
[Collection(SerialCollection.Name)]
public class ServerProcessTransportTests
{
    private static DebugAdapterServerProcess Sh(string script, TimeSpan? timeout = null) =>
        new("/bin/sh", ["-c", script], JsDebugAdapterFactory.ListeningPattern) { StartTimeout = timeout ?? TimeSpan.FromSeconds(10), DisplayName = "fake server" };

    private static void RequireSh() => Assert.SkipUnless(File.Exists("/bin/sh") && OperatingSystem.IsLinux(), "needs /bin/sh and Linux /proc");

    [Fact]
    public async Task It_connects_to_the_port_the_server_prints_runs_it_below_normal_priority_and_kills_its_tree_on_close_but_not_what_left_it()
    {
        RequireSh();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? marker = null;
        try
        {
            // Prints some noise, then the listening line; leaves a child behind and a grandchild re-parented away from its tree.
            var transport = await ServerProcessTransport.StartAsync(Sh($"echo starting; echo 'Debug server listening at 127.0.0.1:{port}'; sleep 300 & (sleep 301 &) ; wait"), NullLogger.Instance, Ct);
            marker = transport.Marker;
            using var accepted = await listener.AcceptSocketAsync(Ct);
            Assert.Equal(new DebugAdapterServer("127.0.0.1", port), transport.Endpoint);
            Assert.Null(transport.SocketDirectory);
            var pid = transport.ProcessId!.Value;
            Assert.Equal(10, Nice(pid)); // nice -n 10: below normal, inherited by everything it spawns
            await SessionHarness.Eventually(() => ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", transport.Marker).Count >= 3, 5_000, "server + children carry the marker");
            var marked = ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", transport.Marker);
            Assert.Contains(marked, p => !ProcessTable.Descendants(pid).Contains(p) && p != pid); // the re-parented grandchild

            var child = ProcessTable.Descendants(pid).Single(p => ProcessTable.CommandLine(p) == "sleep 300");
            var left = marked.Single(p => ProcessTable.CommandLine(p) == "sleep 301");
            await transport.DisposeAsync();
            Assert.False(ProcessTable.IsAlive(pid));
            Assert.False(ProcessTable.IsAlive(child)); // the server's tree dies with it
            // A process that LEFT the tree is not the server's to kill on a normal close (a program may detach a browser or a daemon on
            // purpose); only the orphans of a server that died on its own are reaped (AdapterServerProcessLifetimeTests).
            Assert.True(ProcessTable.IsAlive(left));
        }
        finally
        {
            listener.Stop();
            foreach (var p in marker is null ? [] : ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker)) // this test's own processes
                try { using var proc = Process.GetProcessById(p); proc.Kill(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    [Fact]
    public async Task A_server_that_exits_before_listening_fails_fast_with_its_own_words()
    {
        RequireSh();
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ServerProcessTransport.StartAsync(Sh("echo 'SyntaxError: Unexpected token' >&2; exit 1"), NullLogger.Instance, Ct));
        Assert.Contains("exited before it was listening", ex.Message);
        Assert.Contains("SyntaxError: Unexpected token", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_server_that_never_prints_its_port_times_out_and_is_killed()
    {
        RequireSh();
        var marker = Guid.NewGuid().ToString("N");
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await ServerProcessTransport.StartAsync(
            Sh("echo still booting; exec sleep 300", TimeSpan.FromMilliseconds(800)) with { Env = new Dictionary<string, string> { ["PROBE"] = marker } }, NullLogger.Instance, Ct));
        Assert.Contains("did not report a listening port", ex.Message);
        Assert.Contains("still booting", ex.Message);
        await SessionHarness.Eventually(() => ProcessTable.WithEnvironment("PROBE", marker).Count == 0, 5_000, "the silent server was killed");
    }

    [Fact]
    public async Task A_debug_session_over_a_server_process_reports_the_server_as_its_adapter_process()
    {
        RequireSh();
        await using var h = new ServiceHarness();
        // The fake adapter host is the "server": the script prints its port and waits; the session must connect there and own the script.
        var factory = new ScriptedServerFactory(Sh($"echo 'Debug server listening at 127.0.0.1:{h.Host.Port}'; exec sleep 300"));
        using var reg = h.Service.RegisterAdapterFactory("scripted", factory);
        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("s", "scripted", folder: h.Dir.Uri));
        var pid = session.AdapterProcessId!.Value;
        Assert.True(ProcessTable.IsAlive(pid));
        Assert.Equal(new DebugAdapterServer("127.0.0.1", h.Host.Port), session.AdapterEndpoint);
        await session.StopAsync(Ct);
        await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), 10_000, "the server process ended with its session");
    }

    private sealed class ScriptedServerFactory(DebugAdapterServerProcess descriptor) : IDebugAdapterFactory
    {
        public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct) => ValueTask.FromResult<DebugAdapterDescriptor?>(descriptor);
    }

    private static int Nice(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(fields[16], CultureInfo.InvariantCulture); // field 19 of /proc/pid/stat: nice
    }
}
