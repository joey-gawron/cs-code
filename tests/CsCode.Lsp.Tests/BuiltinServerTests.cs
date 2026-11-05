using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>
/// B3 (themes-languages-treesitter brief): the built-in server definitions added for Java, Kotlin, Scala, Zig, TOML, Markdown, Nix, Terraform,
/// Ruby, PHP, Haskell, Elixir, Erlang, OCaml, Swift, Dart, Dockerfile, CMake, SQL, Protobuf, GraphQL, Vue, Svelte and Clojure. Configuration
/// tests only (resolution, alternatives, install hints, root detection, options): servers installed on this machine are smoke-tested for real
/// in <see cref="RealZlsAndNilServerTests"/>, servers the Nix store holds in <see cref="RealStoreServerTests"/>.
/// </summary>
public class BuiltinServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BuiltinServer Get(string id) => BuiltinLanguageServers.Find(id) ?? throw new Xunit.Sdk.XunitException($"no built-in server '{id}'");

    // ---- the brief's list --------------------------------------------------------------------------------------------------------------

    /// <summary>(language id served, executables the brief names: the definition's command or one of its alternatives must be each of them).</summary>
    public static TheoryData<string, string[]> BriefServers => new()
    {
        { "java", ["jdtls"] },
        { "kotlin", ["kotlin-language-server"] },
        { "scala", ["metals"] },
        { "zig", ["zls"] },
        { "toml", ["taplo"] },
        { "markdown", ["marksman"] },
        { "nix", ["nil", "nixd"] },
        { "terraform", ["terraform-ls"] },
        { "ruby", ["ruby-lsp"] },
        { "php", ["phpactor"] },
        { "haskell", ["haskell-language-server-wrapper"] },
        { "elixir", ["elixir-ls", "lexical"] },
        { "erlang", ["erlang_ls"] },
        { "ocaml", ["ocamllsp"] },
        { "swift", ["sourcekit-lsp"] },
        { "dart", ["dart"] },
        { "dockerfile", ["docker-langserver"] },
        { "cmake", ["cmake-language-server"] },
        { "sql", ["sqls"] },
        { "protobuf", ["buf"] },
        { "graphql", ["graphql-lsp"] },
        { "vue", ["vue-language-server"] },
        { "svelte", ["svelteserver"] },
        { "clojure", ["clojure-lsp"] },
    };

    [Theory]
    [MemberData(nameof(BriefServers))]
    public void Every_server_the_brief_names_is_defined_for_its_language(string language, string[] executables)
    {
        var servers = BuiltinLanguageServers.Catalog.Where(s => s.Definition.Selector.Score(new Uri("file:///x/f"), language) > 0).ToArray();
        var server = Assert.Single(servers); // exactly one built-in per language: two would both start and fight over the same documents
        var commands = new[] { server.Definition.Command }.Concat(server.Alternatives.Select(a => a.Command)).ToArray();
        foreach (var exe in executables) Assert.Contains(exe, commands);
        Assert.True(server.Definition.AutoStart);
        Assert.Equal(LspSettingKeys.LanguageServers + "." + server.Definition.Id, server.Definition.SettingsKey);
    }

    /// <summary>Exact launch lines (command + args): these are what the servers' own documentation prescribes for stdio.</summary>
    public static TheoryData<string, string, string[]> LaunchLines => new()
    {
        { "jdtls", "jdtls", [] },
        { "kotlin-language-server", "kotlin-language-server", [] },
        { "metals", "metals", [] },
        { "zls", "zls", [] },
        { "taplo", "taplo", ["lsp", "stdio"] },
        { "marksman", "marksman", ["server"] },
        { "nix", "nil", [] },
        { "terraform-ls", "terraform-ls", ["serve"] },
        { "ruby-lsp", "ruby-lsp", [] },
        { "phpactor", "phpactor", ["language-server"] },
        { "haskell-language-server", "haskell-language-server-wrapper", ["--lsp"] },
        { "elixir", "elixir-ls", [] },
        { "erlang", "erlang_ls", [] },
        { "ocaml-lsp", "ocamllsp", [] },
        { "sourcekit-lsp", "sourcekit-lsp", [] },
        { "dart", "dart", ["language-server", "--protocol=lsp"] },
        { "dockerfile", "docker-langserver", ["--stdio"] },
        { "cmake", "cmake-language-server", [] },
        { "sqls", "sqls", [] },
        { "protobuf", "buf", ["lsp", "serve"] },
        { "graphql", "graphql-lsp", ["server", "-m", "stream"] },
        { "vue", "vue-language-server", ["--stdio"] },
        { "svelte", "svelteserver", ["--stdio"] },
        { "clojure-lsp", "clojure-lsp", [] },
    };

    [Theory]
    [MemberData(nameof(LaunchLines))]
    public void Each_new_server_launches_with_its_documented_command_line(string id, string command, string[] args)
    {
        var d = Get(id).Definition;
        Assert.Equal(command, d.Command);
        Assert.Equal(args, d.Args);
    }

    [Fact]
    public void Alternatives_are_the_documented_second_servers_with_their_own_arguments()
    {
        Assert.Equal([new ServerAlternative("nixd", [])], Get("nix").Alternatives, AlternativeComparer.Instance);
        Assert.Equal(["language_server.sh", "lexical"], Get("elixir").Alternatives.Select(a => a.Command));
        var elp = Assert.Single(Get("erlang").Alternatives);
        Assert.Equal("elp", elp.Command);
        Assert.Equal(["server"], elp.Args);
        Assert.Equal("protols", Assert.Single(Get("protobuf").Alternatives).Command);
        Assert.Equal(["--lsp"], Assert.Single(Get("haskell-language-server").Alternatives).Args);
    }

    private sealed class AlternativeComparer : IEqualityComparer<ServerAlternative>
    {
        public static readonly AlternativeComparer Instance = new();
        public bool Equals(ServerAlternative? x, ServerAlternative? y) => x?.Command == y?.Command && (x?.Args ?? []).SequenceEqual(y?.Args ?? []);
        public int GetHashCode(ServerAlternative obj) => obj.Command.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void Every_builtin_has_a_unique_id_languages_an_install_hint_per_platform_and_a_formatter_entry()
    {
        var installer = new Regex(@"https://|npm install|pipx? install|gem install|go install|cargo install|dotnet tool|brew |nix profile install|opam install|ghcup install|cs install|winget install|choco install|scoop install|rustup|xcode-select|apt(-get)? install|WSL|make install|rebar3",
            RegexOptions.CultureInvariant);
        Assert.Equal(BuiltinLanguageServers.Catalog.Count, BuiltinLanguageServers.Catalog.Select(s => s.Definition.Id).Distinct().Count());
        foreach (var s in BuiltinLanguageServers.Catalog)
        {
            var id = s.Definition.Id;
            Assert.Contains(s.Definition.Selector.Filters, f => f.Language is { Length: > 0 });
            foreach (var (platform, hint) in new[] { ("linux", s.Install.Linux), ("macOS", s.Install.MacOS), ("windows", s.Install.Windows) })
                Assert.True(installer.IsMatch(hint), $"{id} has no concrete install command or download URL for {platform}: '{hint}'");
            Assert.False(string.IsNullOrWhiteSpace(s.Formatter), id + " formatter entry");
            Assert.Equal(s.Install.ForCurrentPlatform, BuiltinLanguageServers.InstallHint(id));
        }
        Assert.Equal(BuiltinLanguageServers.All.Count, BuiltinLanguageServers.Catalog.Count);
        Assert.Contains("dotnet tool", BuiltinLanguageServers.InstallHint("csharp"));
        Assert.Contains("install", BuiltinLanguageServers.InstallHint("no-such-server"), StringComparison.OrdinalIgnoreCase); // the generic fallback
    }

    [Fact]
    public void Install_hints_differ_by_platform_where_the_projects_document_different_routes()
    {
        Assert.Contains("brew install zls", Get("zls").Install.MacOS);
        Assert.Contains("github.com/zigtools/zls/releases", Get("zls").Install.Windows);
        Assert.Contains("nix profile install nixpkgs#nil", Get("nix").Install.Linux);
        Assert.Contains("WSL", Get("nix").Install.Windows);
        Assert.Contains("winget install --id Swift.Toolchain", Get("sourcekit-lsp").Install.Windows);
        Assert.Contains("xcode-select --install", Get("sourcekit-lsp").Install.MacOS);
        Assert.Contains("choco install dart-sdk", Get("dart").Install.Windows);
        Assert.Contains("ghcup install hls", Get("haskell-language-server").Install.Linux);
        Assert.Contains("opam install ocaml-lsp-server", Get("ocaml-lsp").Install.Windows);
    }

    // ---- executable resolution -------------------------------------------------------------------------------------------------------

    private static string Exe(TempDirectory dir, string relative)
    {
        var path = dir.WriteFile(relative, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A launch request whose PATH is <paramref name="pathDir"/> only (never the machine's real PATH) and no tools directory.</summary>
    private static LspLaunchRequest Request(LanguageServerDefinition effective, string pathDir, IReadOnlyList<string>? searchDirs = null) =>
        new(effective with { Env = new Dictionary<string, string> { ["PATH"] = pathDir } }, null, null, SearchDirs: searchDirs,
            Alternatives: BuiltinLanguageServers.AlternativesFor(effective));

    [Fact]
    public void A_missing_command_falls_back_to_the_first_installed_alternative_with_its_arguments()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("unix permission bits"); return; }
        using var dir = new TempDirectory("cscode-alt-");
        var bin = dir.Combine("bin");
        Directory.CreateDirectory(bin);
        Assert.Null(ServerLaunch.Resolve(Request(Get("nix").Definition, bin)));
        Assert.Equal("nil / nixd", ServerLaunch.Describe(Request(Get("nix").Definition, bin)));

        var nixd = Exe(dir, "bin/nixd");
        var r = ServerLaunch.Resolve(Request(Get("nix").Definition, bin))!;
        Assert.True(r.UsedAlternative);
        Assert.Equal(nixd, r.Executable);
        Assert.Equal("nixd", r.Definition.Command);

        var elp = Exe(dir, "bin/elp");
        var erl = ServerLaunch.Resolve(Request(Get("erlang").Definition, bin))!;
        Assert.Equal((elp, "elp"), (erl.Executable, erl.Definition.Command));
        Assert.Equal(["server"], erl.Definition.Args); // the alternative's own arguments, not erlang_ls'

        var nil = Exe(dir, "bin/nil");
        var own = ServerLaunch.Resolve(Request(Get("nix").Definition, bin))!; // the definition's own command wins when both are installed
        Assert.False(own.UsedAlternative);
        Assert.Equal(nil, own.Executable);
        Assert.Equal("nil", own.Definition.Command);
    }

    [Fact]
    public void A_user_supplied_command_is_never_silently_replaced_by_an_alternative()
    {
        var nix = Get("nix").Definition;
        Assert.Single(BuiltinLanguageServers.AlternativesFor(nix));
        Assert.Empty(BuiltinLanguageServers.AlternativesFor(nix with { Command = "/opt/nil/bin/nil" }));
        Assert.Empty(BuiltinLanguageServers.AlternativesFor(Get("erlang").Definition with { Args = ["--transport", "stdio"] }));
        Assert.Empty(BuiltinLanguageServers.AlternativesFor(LspTestEnv.FakeDef()));
    }

    [Fact]
    public void Servers_installed_in_the_usual_per_user_directories_are_found_without_PATH()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("unix permission bits"); return; }
        using var home = new TempDirectory("cscode-home-");
        var empty = home.Combine("empty-path");
        Directory.CreateDirectory(empty);
        var taplo = Exe(home, ".cargo/bin/taplo");
        var zls = Exe(home, ".nix-profile/bin/zls");
        string[] dirs = [home.Combine(".nix-profile/bin"), home.Combine(".cargo/bin"), home.Combine("go/bin")];

        Assert.Null(ServerLaunch.Resolve(Request(Get("taplo").Definition, empty)));            // PATH only: not found
        Assert.Equal(taplo, ServerLaunch.Resolve(Request(Get("taplo").Definition, empty, dirs))!.Executable);
        Assert.Equal(zls, ServerLaunch.Resolve(Request(Get("zls").Definition, empty, dirs))!.Executable);
        var onPath = Exe(home, "path/zls");
        Assert.Equal(onPath, ServerLaunch.Resolve(Request(Get("zls").Definition, home.Combine("path"), dirs))!.Executable); // PATH comes first
    }

    [Fact]
    public void The_search_directories_default_to_the_usual_install_locations_and_follow_the_setting()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaults = new LspSettings().SearchDirs;
        if (OperatingSystem.IsLinux())
        {
            foreach (var d in new[] { ".nix-profile/bin", ".cargo/bin", ".local/bin", "go/bin", ".dotnet/tools" })
                Assert.Contains(Path.Combine(home, d), defaults);
        }
        Assert.All(defaults, d => Assert.DoesNotContain("~", d, StringComparison.Ordinal));

        var config = new TestConfigurationService();
        config.Set(LspSettingKeys.SearchDirs, new[] { "~/my-servers", "/opt/lsp/bin", "", "%CSCODE_SURELY_UNSET_VARIABLE%/bin" });
        Assert.Equal([Path.Combine(home, "my-servers"), "/opt/lsp/bin"], LspSettings.Read(config).SearchDirs);
        config.Set(LspSettingKeys.SearchDirs, Array.Empty<string>());
        Assert.Empty(LspSettings.Read(config).SearchDirs);

        Assert.Equal(home, ToolResolver.ExpandDirectory("~"));
        Assert.Null(ToolResolver.ExpandDirectory("  "));
        Assert.Contains(LspSettings.Properties(), p => p.Key == LspSettingKeys.SearchDirs && p.Default is { ValueKind: JsonValueKind.Array });
    }

    // ---- a missing server through the real manager and the real process factory ----------------------------------------------------------

    [Fact]
    public async Task A_missing_server_stays_stopped_and_shows_its_install_hint_once()
    {
        using var empty = new TempDirectory("cscode-nopath-");
        var noPath = new Dictionary<string, string> { ["PATH"] = empty.Path };
        await using var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.SearchDirs, Array.Empty<string>());
            c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["marksman"] = new { env = noPath }, ["nix"] = new { env = noPath } });
        }, builtins: true, connectionsOverride: new DefaultLspConnectionFactory());

        env.Open("README.md", "# hi\n", "markdown");
        env.Open("docs/other.md", "# other\n", "markdown");
        env.Open("default.nix", "{ }\n", "nix");
        var marksman = await env.WaitForAsync(() => env.Manager.Servers.FirstOrDefault(s => s.Id == "marksman" && s.LastError is not null), "marksman not-found state");
        var nix = await env.WaitForAsync(() => env.Manager.Servers.FirstOrDefault(s => s.Id == "nix" && s.LastError is not null), "nix not-found state");

        Assert.Equal(LanguageServerState.Stopped, marksman.State);
        Assert.Contains("'marksman' was not found", marksman.LastError);
        Assert.Contains(Get("marksman").Install.ForCurrentPlatform, marksman.LastError);
        Assert.Contains("'nil / nixd' was not found", nix.LastError); // both executables were looked for
        Assert.Contains(Get("nix").Install.ForCurrentPlatform, nix.LastError);

        await Task.Delay(300, Ct); // a second document of the same root must not repeat the message, and nothing is retried
        var messages = env.Window.Messages.ToArray().Where(m => m.Message.Contains("marksman", StringComparison.Ordinal)).ToArray();
        var message = Assert.Single(messages);
        Assert.Equal(MessageSeverityInfo, message.Severity);
        Assert.Contains(Get("marksman").Install.ForCurrentPlatform, message.Message);
        Assert.All(env.Manager.Servers, s => Assert.Null(s.ProcessId));
    }

    private const CsCode.Core.Platform.MessageSeverity MessageSeverityInfo = CsCode.Core.Platform.MessageSeverity.Info;

    // ---- root detection ----------------------------------------------------------------------------------------------------------------

    /// <summary>(server id, files to create, the document, the expected root relative to the workspace folder).</summary>
    public static TheoryData<string, string[], string, string> Roots => new()
    {
        // multi-module builds: the outermost marked directory, one server for the whole build
        { "jdtls", ["pom.xml", "core/pom.xml"], "core/src/main/java/A.java", "" },
        { "jdtls", ["app/settings.gradle", "app/lib/build.gradle"], "app/lib/src/B.java", "app" },
        { "kotlin-language-server", ["settings.gradle.kts", "server/build.gradle.kts"], "server/src/main/kotlin/A.kt", "" },
        { "metals", ["build.sbt", "core/src/main/scala/.keep"], "core/src/main/scala/A.scala", "" },
        { "elixir", ["umbrella/mix.exs", "umbrella/apps/web/mix.exs"], "umbrella/apps/web/lib/web.ex", "umbrella" },
        { "haskell-language-server", ["cabal.project", "pkg/pkg.cabal"], "pkg/src/Lib.hs", "" },
        { "ocaml-lsp", ["dune-project", "lib/dune-project"], "lib/a.ml", "" },
        { "nix", ["flake.nix", "pkgs/tool/default.nix"], "pkgs/tool/default.nix", "" },
        { "protobuf", ["buf.yaml", "proto/buf.yaml"], "proto/a/v1/a.proto", "" },
        // single projects: the nearest marked directory
        { "zls", ["build.zig", "vendor/lib/build.zig"], "vendor/lib/src/main.zig", "vendor/lib" },
        { "ruby-lsp", ["svc/Gemfile"], "svc/lib/a.rb", "svc" },
        { "phpactor", ["site/composer.json"], "site/src/A.php", "site" },
        { "dart", ["app/pubspec.yaml"], "app/lib/main.dart", "app" },
        { "sourcekit-lsp", ["pkg/Package.swift"], "pkg/Sources/App/main.swift", "pkg" },
        { "clojure-lsp", ["svc/deps.edn"], "svc/src/core.clj", "svc" },
        { "graphql", ["api/.graphqlrc.yml"], "api/schema.graphql", "api" },
        { "marksman", ["notes/.marksman.toml"], "notes/a/b.md", "notes" },
        { "marksman", ["wiki/.git/HEAD"], "wiki/docs/a.md", "wiki" },   // a git repository (a directory marker)
        { "svelte", ["web/svelte.config.js"], "web/src/App.svelte", "web" },
        { "vue", ["web/package.json"], "web/src/App.vue", "web" },
        { "erlang", ["rebar.config", "apps/a/rebar.config"], "apps/a/src/a.erl", "" },
        { "cmake", ["CMakePresets.json"], "src/CMakeLists.txt", "" },
        // no marker anywhere: the workspace folder
        { "taplo", [], "conf/a.toml", "" },
        { "marksman", [], "docs/a.md", "" },
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void Root_detection_follows_each_servers_project_layout(string id, string[] files, string document, string expectedRoot)
    {
        using var ws = new TempDirectory("cscode-roots-");
        foreach (var f in files) ws.WriteFile(f, "");
        var doc = ws.WriteFile(document, "");
        var root = new RootDetector().Resolve(Get(id).Definition, new Uri(doc), [new Uri(ws.Path + "/")]);
        var expected = expectedRoot.Length == 0 ? ws.Path : ws.Combine(expectedRoot);
        Assert.Equal(new Uri(Path.TrimEndingDirectorySeparator(expected) + "/"), root.Uri);
    }

    [Fact]
    public void Servers_without_markers_serve_every_workspace_folder_with_one_instance()
    {
        foreach (var id in new[] { "terraform-ls", "dockerfile", "sqls" })
        {
            var root = new RootDetector().Resolve(Get(id).Definition, new Uri("file:///w/a/main.tf"), [new Uri("file:///w/")]);
            Assert.True(root.FollowsWorkspaceFolders, id);
        }
    }

    [Fact]
    public void Outermost_never_leaves_the_workspace_folder_and_a_user_marker_override_means_nearest()
    {
        using var outside = new TempDirectory("cscode-outer-");
        outside.WriteFile("pom.xml", "");               // above the workspace folder: must be ignored
        outside.WriteFile("ws/pom.xml", "");
        outside.WriteFile("ws/mod/pom.xml", "");
        var doc = outside.WriteFile("ws/mod/src/A.java", "");
        var ws = new Uri(outside.Combine("ws") + "/");
        var jdtls = Get("jdtls").Definition;
        Assert.Equal(ws, new RootDetector().Resolve(jdtls, new Uri(doc), [ws]).Uri);

        Assert.Equal(RootStrategy.Outermost, BuiltinLanguageServers.RootStrategyFor(jdtls));
        var overridden = jdtls with { RootMarkers = ["pom.xml"] };
        Assert.Equal(RootStrategy.Nearest, BuiltinLanguageServers.RootStrategyFor(overridden));
        Assert.Equal(new Uri(outside.Combine("ws/mod") + "/"), new RootDetector().Resolve(overridden, new Uri(doc), [ws]).Uri);

        // a file outside every workspace folder: nearest (outermost would climb to the file-system root)
        Assert.Equal(new Uri(outside.Combine("ws/mod") + "/"), new RootDetector().Resolve(jdtls, new Uri(doc), []).Uri);
    }

    [Fact]
    public void One_rust_analyzer_serves_every_crate_and_workspace_folder_as_in_vs_code()
    {
        var ra = Get("rust-analyzer").Definition;
        var ws = new Uri("file:///w/");
        var detector = new RootDetector();
        var a = detector.Resolve(ra, new Uri("file:///w/libball/src/lib.rs"), [ws]);
        var b = detector.Resolve(ra, new Uri("file:///w/assetman/src/lib.rs"), [ws]);
        Assert.True(a.FollowsWorkspaceFolders);
        Assert.Equal(a.Key, b.Key); // the server finds the Cargo.toml files itself: no server per crate
    }

    // ---- options that reach the server -------------------------------------------------------------------------------------------------

    private static async Task<(LspTestEnv Env, FakeLspServer Server)> StartBuiltinAsync(string path, string language)
    {
        var env = new LspTestEnv(builtins: true);
        env.Open(path, "x", language);
        var server = await env.Connections.WaitForServerAsync();
        await server.WaitForAsync(P.LspMethods.Initialized);
        await TestHelpers.EventuallyAsync(() => env.Manager.Servers.Any(s => s.State == LanguageServerState.Running), "server Running");
        return (env, server);
    }

    private static P.InitializeParams InitOf(FakeLspServer s) => s.Of(P.LspMethods.Initialize).Single().P<P.InitializeParams>();

    [Fact]
    public async Task Vue_gets_the_projects_typescript_sdk_through_the_workspaceFolder_variable()
    {
        var (env, server) = await StartBuiltinAsync("web/src/App.vue", "vue");
        await using var _ = env;
        var options = InitOf(server).InitializationOptions!.Value;
        Assert.Equal(env.Dir.Path + "/node_modules/typescript/lib", options.GetProperty("typescript").GetProperty("tsdk").GetString());
        Assert.False(options.GetProperty("vue").GetProperty("hybridMode").GetBoolean());
        var request = Assert.Single(env.Connections.Requests);
        Assert.Equal("vue-language-server", request.Definition.Command);
        Assert.Equal(new LspSettings().SearchDirs, request.SearchDirs); // the install directories reach the process factory
    }

    [Theory]
    [InlineData("lib/main.dart", "dart", "onlyAnalyzeProjectsWithOpenFiles", "true")]
    [InlineData("src/A.scala", "scala", "statusBarProvider", "\"log-message\"")]
    [InlineData("src/CMakeLists.txt", "cmake", "buildDirectory", "\"build\"")]
    public async Task Initialization_options_reach_the_server(string path, string language, string property, string expectedJson)
    {
        var (env, server) = await StartBuiltinAsync(path, language);
        await using var _ = env;
        Assert.Equal(expectedJson, InitOf(server).InitializationOptions!.Value.GetProperty(property).GetRawText());
    }

    [Fact]
    public async Task Nix_servers_are_told_to_format_with_nixfmt_and_get_the_alternative_list()
    {
        var (env, server) = await StartBuiltinAsync("default.nix", "nix");
        await using var _ = env;
        var answer = await server.RequestAsync(P.LspMethods.WorkspaceConfiguration, new P.ConfigurationParams
        {
            Items = [new P.ConfigurationItem { Section = "nil" }, new P.ConfigurationItem { Section = "nixd.formatting" }],
        });
        Assert.Equal("nixfmt", answer[0].GetProperty("formatting").GetProperty("command")[0].GetString());
        Assert.Equal("nixfmt", answer[1].GetProperty("command")[0].GetString());
        var request = Assert.Single(env.Connections.Requests);
        Assert.Equal("nixd", Assert.Single(request.Alternatives!).Command);
    }

    [Fact]
    public void Launch_variables_expand_in_args_and_nested_options_and_leave_everything_else_alone()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IReadOnlyList<string> plain = ["--stdio"];
        Assert.Same(plain, LaunchVariables.Expand(plain, "/w"));
        Assert.Equal(["--root=/w", home + "/x", "${unknown}"], LaunchVariables.Expand(["--root=${workspaceFolder}", "${userHome}/x", "${unknown}"], "/w/"));

        var options = JsonDocument.Parse("""{"a":["${workspaceFolder}/x",1,{"b":"${workspaceFolder}"}],"c":true,"d":null}""").RootElement;
        var expanded = LaunchVariables.Expand(options, "/proj")!.Value;
        Assert.Equal("""{"a":["/proj/x",1,{"b":"/proj"}],"c":true,"d":null}""", expanded.GetRawText());
        var untouched = JsonDocument.Parse("""{"x":1}""").RootElement;
        Assert.Equal(untouched.GetRawText(), LaunchVariables.Expand(untouched, "/proj")!.Value.GetRawText());
        Assert.Null(LaunchVariables.Expand((JsonElement?)null, "/proj"));
    }
}
