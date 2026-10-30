using CsCode.Core;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>Definitions, root detection, routing, settings overrides, DI, tool resolution.</summary>
public class ManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static P.InitializeParams InitOf(FakeLspServer s) => s.Of(P.LspMethods.Initialize).Single().P<P.InitializeParams>();

    [Fact]
    public async Task Root_detection_walks_up_from_the_file_to_the_nearest_marker()
    {
        await using var env = new LspTestEnv();
        env.Dir.WriteFile("proj/fake.root", "");
        env.AddFake(rootMarkers: ["fake.root"]);
        env.Open("proj/src/deep/er/file.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        Assert.Equal(new Uri(env.Dir.Combine("proj") + "/").AbsoluteUri, InitOf(server).RootUri);
        Assert.Equal(env.Dir.Combine("proj") + "/", InitOf(server).RootPath);
    }

    [Fact]
    public async Task Nested_projects_get_separate_servers_each_seeing_only_its_documents()
    {
        await using var env = new LspTestEnv();
        env.Dir.WriteFile("one/fake.root", "");
        env.Dir.WriteFile("two/fake.root", "");
        env.AddFake(rootMarkers: ["fake.root"]);
        var a = env.Open("one/a.fake", "A");
        var b = env.Open("two/b.fake", "B");
        var s0 = await env.Connections.WaitForServerAsync(0);
        var s1 = await env.Connections.WaitForServerAsync(1);
        await s0.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        await s1.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        Assert.Equal(2, env.Manager.Servers.Count);
        var roots = new[] { InitOf(s0).RootUri, InitOf(s1).RootUri }.OrderBy(x => x).ToArray();
        Assert.EndsWith("/one/", roots[0]);
        Assert.EndsWith("/two/", roots[1]);
        // each server only ever saw its own document
        Assert.All(new[] { s0, s1 }, s => Assert.Single(s.Of(P.LspMethods.TextDocumentDidOpen)));
        Assert.NotEqual(s0.Of(P.LspMethods.TextDocumentDidOpen).Single().P<P.DidOpenTextDocumentParams>().TextDocument.Uri, s1.Of(P.LspMethods.TextDocumentDidOpen).Single().P<P.DidOpenTextDocumentParams>().TextDocument.Uri);
    }

    [Fact]
    public async Task Two_documents_under_one_root_share_one_server()
    {
        await using var env = new LspTestEnv();
        env.Dir.WriteFile("fake.root", "");
        env.AddFake(rootMarkers: ["fake.root"]);
        env.Open("x/a.fake", "A");
        env.Open("y/b.fake", "B");
        var server = await env.Connections.WaitForServerAsync();
        await TestHelpers.EventuallyAsync(() => server.CountOf(P.LspMethods.TextDocumentDidOpen) == 2, "both documents opened on the same server");
        await Task.Delay(150, Ct);
        Assert.Equal(1, env.Connections.ConnectCount);
    }

    [Fact]
    public async Task Without_a_marker_the_workspace_folder_is_the_root()
    {
        await using var env = new LspTestEnv();
        env.AddFake(rootMarkers: ["does-not-exist.marker"]);
        env.Open("sub/dir/file.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        Assert.Equal(env.Dir.Uri.AbsoluteUri, InitOf(server).RootUri);
    }

    [Fact]
    public async Task Glob_markers_like_star_sln_are_supported()
    {
        await using var env = new LspTestEnv();
        env.Dir.WriteFile("app/My.App.sln", "");
        env.AddFake(rootMarkers: ["*.sln"]);
        env.Open("app/src/code.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        Assert.EndsWith("/app/", InitOf(server).RootUri);
    }

    [Fact]
    public async Task Definitions_without_root_markers_serve_all_workspace_folders_with_one_server()
    {
        await using var env = new LspTestEnv();
        using var second = new TempDirectoryScope();
        env.Workspace.AddFolder(second.Path);
        env.AddFake();
        env.Open("a.fake", "x");
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        Assert.Equal(2, InitOf(server).WorkspaceFolders!.Length);
        Assert.Equal(env.Dir.Uri.AbsoluteUri, InitOf(server).RootUri); // primary folder
    }

    [Fact]
    public async Task Documents_outside_every_workspace_folder_use_their_own_directory()
    {
        await using var env = new LspTestEnv();
        using var elsewhere = new TempDirectoryScope();
        env.AddFake(rootMarkers: ["nope.marker"]);
        var path = Path.Combine(elsewhere.Path, "loose.fake");
        File.WriteAllText(path, "x");
        var doc = new InMemoryTextDocument(new Uri(path), "fake", "x");
        using var reg = env.Documents.Register(doc);
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        Assert.Equal(new Uri(elsewhere.Path + "/").AbsoluteUri, InitOf(server).RootUri);
    }

    [Fact]
    public async Task Two_definitions_for_the_same_language_both_start()
    {
        await using var env = new LspTestEnv();
        env.Manager.AddDefinition(LspTestEnv.FakeDef("one", "fake"));
        env.Manager.AddDefinition(LspTestEnv.FakeDef("two", "fake"));
        env.Open("a.fake", "x");
        await env.Connections.WaitForServerAsync(1);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Select(s => s.Id).OrderBy(x => x).SequenceEqual(["one", "two"]), "both servers listed");
    }

    [Fact]
    public async Task AutoStart_false_waits_for_an_explicit_start()
    {
        await using var env = new LspTestEnv();
        env.AddFake(autoStart: false);
        env.Open("a.fake", "x");
        await Task.Delay(250, Ct);
        Assert.Equal(0, env.Connections.ConnectCount);
        Assert.Equal(LanguageServerState.Stopped, env.Manager.Servers.Single().State);
        await env.Manager.StartAsync("fake", Ct);
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen); // the already-open document is synced on start
    }

    [Fact]
    public async Task StartAsync_for_a_definition_without_documents_starts_a_server_at_the_workspace_root()
    {
        await using var env = new LspTestEnv();
        env.AddFake();
        await env.Manager.StartAsync("fake", Ct);
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        Assert.Equal(env.Dir.Uri.AbsoluteUri, InitOf(server).RootUri);
        await env.WaitForStateAsync("fake", LanguageServerState.Running);
    }

    [Fact]
    public async Task ServersChanged_reports_the_state_progression()
    {
        await using var env = new LspTestEnv();
        var seen = new List<LanguageServerState>();
        using var sub = env.Manager.ServersChanged.Subscribe(list => { lock (seen) seen.AddRange(list.Select(i => i.State)); });
        await env.StartAsync();
        await env.Manager.StopAsync("fake", Ct);
        LanguageServerState[] states;
        lock (seen) states = seen.ToArray();
        Assert.Contains(LanguageServerState.Starting, states);
        Assert.Contains(LanguageServerState.Running, states);
        Assert.Contains(LanguageServerState.Stopping, states);
        Assert.Equal(LanguageServerState.Stopped, states[^1]);
        Assert.True(Array.IndexOf(states, LanguageServerState.Starting) < Array.IndexOf(states, LanguageServerState.Running));
    }

    [Fact]
    public async Task Closing_a_workspace_folder_stops_the_servers_rooted_in_it()
    {
        await using var env = new LspTestEnv();
        using var other = new TempDirectoryScope();
        File.WriteAllText(Path.Combine(other.Path, "fake.root"), "");
        env.Workspace.AddFolder(other.Path);
        env.AddFake(rootMarkers: ["fake.root"]);
        var path = Path.Combine(other.Path, "f.fake");
        File.WriteAllText(path, "x");
        using var reg = env.Documents.Register(new InMemoryTextDocument(new Uri(path), "fake", "x"));
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        env.Workspace.RemoveFolder(env.Workspace.Folders.Single(f => f.LocalPath.TrimEnd('/') == other.Path));
        await TestHelpers.EventuallyAsync(() => server.ExitReceived, "server for the closed folder stopped");
        Assert.Empty(env.Manager.Servers);
    }

    [Fact]
    public async Task Changing_the_launch_command_in_settings_restarts_a_running_server_but_selector_changes_do_not()
    {
        await using var env = new LspTestEnv();
        var (first, _) = await env.StartAsync();
        env.Config.Set(LspSettingKeys.LanguageServers, new { fake = new { selector = new[] { "fake", "fake3" } } });
        await Task.Delay(300, Ct);
        Assert.Equal(1, env.Connections.ConnectCount); // same launch => no restart

        env.Config.Set(LspSettingKeys.LanguageServers, new { fake = new { command = "different-binary", selector = new[] { "fake" } } });
        var second = await env.Connections.WaitForServerAsync(1);
        await second.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        Assert.True(first.ExitReceived);
        Assert.Equal("different-binary", env.Connections.Requests[^1].Definition.Command);
    }

    [Fact]
    public async Task Disabling_a_definition_in_settings_stops_its_servers()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        env.Config.Set(LspSettingKeys.LanguageServers, new { fake = new { enabled = false } });
        await TestHelpers.EventuallyAsync(() => server.ExitReceived, "stopped after disabling");
        Assert.Empty(env.Manager.Servers);
    }

    [Fact]
    public async Task StopAll_stops_every_server()
    {
        await using var env = new LspTestEnv();
        env.Manager.AddDefinition(LspTestEnv.FakeDef("one", "fake"));
        env.Manager.AddDefinition(LspTestEnv.FakeDef("two", "fake"));
        env.Open("a.fake", "x");
        var s0 = await env.Connections.WaitForServerAsync(0);
        var s1 = await env.Connections.WaitForServerAsync(1);
        await s0.WaitForAsync(P.LspMethods.Initialized);
        await s1.WaitForAsync(P.LspMethods.Initialized);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.All(s => s.State == LanguageServerState.Running), "both running");
        await env.Manager.StopAllAsync(Ct);
        Assert.True(s0.ExitReceived && s1.ExitReceived);
        Assert.All(env.Manager.Servers, s => Assert.Equal(LanguageServerState.Stopped, s.State));
    }

    /// <summary>T6: a container disposed synchronously still stops the servers; after the shell's asynchronous disposal it is a no-op.</summary>
    [Fact]
    public async Task Synchronous_Dispose_stops_the_servers_and_after_DisposeAsync_returns_at_once()
    {
        await using var env = new LspTestEnv();
        env.Manager.AddDefinition(LspTestEnv.FakeDef("one", "fake"));
        env.Open("a.fake", "x");
        var server = await env.Connections.WaitForServerAsync(0);
        await server.WaitForAsync(P.LspMethods.Initialized);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.All(s => s.State == LanguageServerState.Running), "running");

        await Task.Run(env.Manager.Dispose, Ct); // as a container's synchronous Dispose would (bounded; not on a UI thread here)
        Assert.True(server.ExitReceived, "the synchronous Dispose shut the server down");

        await env.Manager.DisposeAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        env.Manager.Dispose();
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Dispose after disposal took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Builtin_definitions_cover_the_required_servers_with_markers_and_install_hints()
    {
        var all = BuiltinLanguageServers.All.ToDictionary(d => d.Id);
        string[] ids = ["typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua"];
        Assert.All(ids, id => Assert.Contains(id, all.Keys)); // the original twelve; the servers added later are covered by BuiltinServerTests

        Assert.Equal("typescript-language-server", all["typescript"].Command);
        Assert.Equal(["--stdio"], all["typescript"].Args);
        Assert.Equal("pyright-langserver", all["python"].Command);
        Assert.Equal(["--stdio"], all["python"].Args);
        Assert.Equal("bash-language-server", all["bash"].Command);
        Assert.Equal(["start"], all["bash"].Args);
        Assert.Equal("csharp-ls", all["csharp"].Command);
        Assert.Equal("clangd", all["clangd"].Command);
        Assert.Equal("rust-analyzer", all["rust-analyzer"].Command);
        Assert.Equal("gopls", all["gopls"].Command);
        Assert.Equal("lua-language-server", all["lua"].Command);
        Assert.Equal("yaml-language-server", all["yaml"].Command);
        foreach (var id in new[] { "json", "html", "css" }) Assert.EndsWith("-language-server", all[id].Command);

        Assert.Contains("tsconfig.json", all["typescript"].RootMarkers!);
        Assert.Contains("*.sln", all["csharp"].RootMarkers!);
        Assert.Contains("Cargo.toml", all["rust-analyzer"].RootMarkers!);
        Assert.Contains("go.mod", all["gopls"].RootMarkers!);
        Assert.Contains("compile_commands.json", all["clangd"].RootMarkers!);
        Assert.Contains("pyproject.toml", all["python"].RootMarkers!);

        Assert.True(all["typescript"].Selector.Score(new Uri("file:///a.tsx"), "typescriptreact") > 0);
        Assert.True(all["typescript"].Selector.Score(new Uri("file:///a.js"), "javascript") > 0);
        Assert.True(all["clangd"].Selector.Score(new Uri("file:///a.m"), "objective-c") > 0);
        Assert.True(all["css"].Selector.Score(new Uri("file:///a.less"), "less") > 0);
        Assert.True(all["json"].Selector.Score(new Uri("file:///a.jsonc"), "jsonc") > 0);
        Assert.True(all["bash"].Selector.Score(new Uri("file:///a.sh"), "shellscript") > 0);
        foreach (var id in ids) Assert.False(string.IsNullOrWhiteSpace(BuiltinLanguageServers.InstallHint(id)), id + " has an install hint");
        Assert.Contains("npm", BuiltinLanguageServers.InstallHint("typescript"));
        Assert.Contains("dotnet tool", BuiltinLanguageServers.InstallHint("csharp"));
    }

    [Fact]
    public async Task AddCsCodeLsp_registers_the_manager_and_participants_in_DI_and_disposes_cleanly()
    {
        var services = new ServiceCollection();
        services.AddCsCodeCore();
        services.AddSingleton<ILspConnectionFactory>(new FakeConnectionFactory());
        services.AddCsCodeLsp();
        var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<ILanguageServerManager>();
        Assert.Same(manager, provider.GetRequiredService<ILanguageServerManager>());
        Assert.Same(manager, provider.GetRequiredService<IWillSaveParticipant>());
        Assert.Same(manager, provider.GetRequiredService<IFileOperationParticipant>());
        Assert.Equal(BuiltinLanguageServers.All.Count, manager.Definitions.Count); // built-ins registered without any consumer doing anything
        Assert.True(manager.Definitions.Count >= 36, $"only {manager.Definitions.Count} built-in definitions");

        // it observes documents resolved from the same container
        var docs = provider.GetRequiredService<IDocumentManager>();
        var factory = (FakeConnectionFactory)provider.GetRequiredService<ILspConnectionFactory>();
        using var reg = docs.Register(new InMemoryTextDocument(new Uri("file:///tmp/x/main.ts"), "typescript", "let a = 1;"));
        var server = await factory.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.TextDocumentDidOpen);
        await provider.DisposeAsync();
        Assert.True(server.ExitReceived);
    }

    [Fact]
    public async Task Settings_are_registered_with_defaults_for_the_settings_ui()
    {
        await using var env = new LspTestEnv();
        var keys = env.Config.Properties.Select(p => p.Key).ToHashSet();
        foreach (var k in new[]
        {
            LspSettingKeys.MaxDocumentBytes, LspSettingKeys.Trace, LspSettingKeys.WorkerThreads, LspSettingKeys.ProcessPriority,
            LspSettingKeys.MaxCompletionItems, LspSettingKeys.MaxReferences, LspSettingKeys.LanguageServers,
        })
            Assert.Contains(k, keys);
        Assert.Equal(10 * 1024 * 1024, env.Config.Properties.Single(p => p.Key == LspSettingKeys.MaxDocumentBytes).Default!.Value.GetInt64());
        Assert.Equal(5000, env.Config.Properties.Single(p => p.Key == LspSettingKeys.MaxCompletionItems).Default!.Value.GetInt32());
        Assert.Equal("belowNormal", env.Config.Properties.Single(p => p.Key == LspSettingKeys.ProcessPriority).Default!.Value.GetString());
    }

    // ------------------------------------------------------------------------------------------------------------
    // tool resolution
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToolResolver_finds_commands_on_PATH_in_the_tools_directory_and_by_path()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("unix permission bits"); return; }
        using var dir = new TempDirectory("cscode-tools-");
        var exe = dir.WriteFile("bin1/my-server", "#!/bin/sh\n");
        var plain = dir.WriteFile("bin1/not-executable", "x");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var env = new Dictionary<string, string> { ["PATH"] = Path.Combine(dir.Path, "bin1") };
        Assert.Equal(exe, ToolResolver.Resolve("my-server", null, null, env));
        Assert.Null(ToolResolver.Resolve("not-executable", null, null, env)); // exists but is not executable
        Assert.Null(ToolResolver.Resolve("missing-tool-xyz", null, null, new Dictionary<string, string> { ["PATH"] = "/nonexistent" }));
        Assert.Equal(exe, ToolResolver.Resolve(exe, null, null, null));         // explicit path
        Assert.Equal(exe, ToolResolver.Resolve("bin1/my-server", null, dir.Path, null)); // relative to the working directory

        // {ToolsDir}/lsp/node_modules/.bin and {ToolsDir}/lsp/<tool>/bin
        var npm = dir.WriteFile("tools/lsp/node_modules/.bin/ts-ls", "#!/bin/sh\n");
        var tool = dir.WriteFile("tools/lsp/clangd-18/bin/clangd", "#!/bin/sh\n");
        File.SetUnixFileMode(npm, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var empty = new Dictionary<string, string> { ["PATH"] = "/nonexistent" };
        Assert.Equal(npm, ToolResolver.Resolve("ts-ls", Path.Combine(dir.Path, "tools"), null, empty));
        Assert.Equal(tool, ToolResolver.Resolve("clangd", Path.Combine(dir.Path, "tools"), null, empty));
        Assert.Null(ToolResolver.Resolve("clangd", null, null, empty));
    }

    [Fact]
    public void RootDetector_caches_but_stays_bounded_and_handles_missing_directories()
    {
        // The detector is internal; exercise it through its public effect: a non-existent file path must not throw.
        var detector = new RootDetector();
        var def = LspTestEnv.FakeDef(rootMarkers: ["fake.root"]);
        var root = detector.Resolve(def, new Uri("file:///definitely/not/here/f.fake"), []);
        Assert.NotNull(root.Key);
        for (var i = 0; i < 2000; i++) detector.Resolve(def, new Uri($"file:///definitely/not/here/{i}/f.fake"), []); // many distinct directories
    }
}
