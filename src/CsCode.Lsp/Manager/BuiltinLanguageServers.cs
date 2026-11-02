using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;

namespace CsCode.Lsp.Manager;

/// <summary>How to install a server that is not there, per platform. Shown once when the executable is missing; CsCode never installs it.</summary>
public sealed record InstallHints(string Linux, string MacOS, string Windows)
{
    public static InstallHints Everywhere(string text) => new(text, text, text);

    public string ForCurrentPlatform => OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? MacOS : Linux;
}

/// <summary>Which marked directory becomes the server's root when markers nest.</summary>
public enum RootStrategy
{
    /// <summary>The nearest ancestor of the file that contains a marker (one server per project).</summary>
    Nearest,
    /// <summary>The outermost such ancestor inside the workspace folder (one server for a multi-module build).</summary>
    Outermost,
}

/// <summary>
/// A built-in server: its <see cref="LanguageServerDefinition"/> plus what that Core record has no slot for. <paramref name="Formatter"/> is what
/// formats documents of its languages (CsCode has no formatter of its own: formatting is always the language server's).
/// </summary>
public sealed record BuiltinServer(
    LanguageServerDefinition Definition,
    InstallHints Install,
    string Formatter,
    IReadOnlyList<ServerAlternative> Alternatives,
    RootStrategy Roots = RootStrategy.Nearest,
    string? Notes = null)
{
    /// <summary>
    /// The language group of built-ins that are alternatives to one another (<c>"csharp"</c>: csharp-ls and Roslyn): one of them runs, the
    /// group's default or the one <c>cscode.lsp.serverChoice</c> names. Null: not one of several.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>Whether this is its <see cref="Group"/>'s server when <c>cscode.lsp.serverChoice</c> names none.</summary>
    public bool IsGroupDefault { get; init; } = true;
}

/// <summary>
/// The language servers CsCode knows out of the box (data only: nothing here starts a process). Any stdio language server works by adding
/// a <see cref="LanguageServerDefinition"/> through <see cref="ILanguageServerManager.AddDefinition"/> or the <c>cscode.languageServers</c> setting.
/// Executables are looked up on PATH, then in the per-user install directories (<c>cscode.lsp.searchDirs</c>), then in
/// <c>{ToolsDir}/lsp/**</c> (see tools/fetch-lsp-tools.sh); a definition with alternatives (nil / nixd) runs the first one installed.
/// Nothing is ever installed: a missing server leaves the language without one and shows the platform's install hint once.
/// Sources for commands, arguments, root markers and options: each server's own documentation and its VS Code / Neovim client setup.
/// </summary>
public static class BuiltinLanguageServers
{
    /// <summary>
    /// T39: the restart cap of the servers built on the TypeScript engine (typescript-language-server with its tsserver, vue-language-server,
    /// svelteserver). VS Code's limit is <c>typescript.tsserver.maxTsServerMemory</c> = 3072 MB of V8 old space for EACH tsserver; this cap is
    /// compared with the resident memory of the whole process tree (lsp-core-4): the launcher, the syntax and semantic tsservers and the typings
    /// installer, the node runtime's shared pages counted once per process. W6 measured that tree at ~550 MB on a small project, so 4 GiB = VS Code's
    /// 3 GiB heap plus 1 GiB for the rest of the tree: a project VS Code serves stays under it, and only a tree whose semantic server would have hit
    /// VS Code's limit reaches it. The slot restarts a server for its cap at most <see cref="ServerSlot.MaxMemoryRestarts"/> times, then stops
    /// enforcing it and says so. Other servers have no cap: native ones (rust-analyzer, clangd, gopls, HLS) grow with the project and would only
    /// regrow after a restart, and the JVM ones are bounded by their own -Xmx. Override per server with
    /// <c>cscode.languageServers.&lt;id&gt;.restartAboveBytes</c> (null or 0: no cap).
    /// </summary>
    public const long TypeScriptEngineCap = 4L * 1024 * 1024 * 1024;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The C# servers' group in <c>cscode.lsp.serverChoice</c>.</summary>
    public const string CSharpGroup = "csharp";

    /// <summary>Roslyn's command line (checked against its <c>--help</c> at 5.13.0-1.26514.10).</summary>
    public static readonly string[] RoslynArgs =
        ["--stdio", "--logLevel", "Information", "--extensionLogDirectory", "${cscodeLogsDir}/roslyn", "--telemetryLevel", "off", "--clientProcessId", "${cscodePid}"];

    private static LanguageServerDefinition Def(string id, string name, string[] languages, string command, string[] args, string[] markers,
        string? initOptions = null, string? settings = null) =>
        new(id, name, DocumentSelector.ForLanguages(languages), command, args,
            RootMarkers: markers,
            InitializationOptions: initOptions is null ? null : Json(initOptions),
            Settings: settings is null ? null : Json(settings),
            SettingsKey: LspSettingKeys.LanguageServers + "." + id);

    private static BuiltinServer S(LanguageServerDefinition def, InstallHints install, string formatter, RootStrategy roots = RootStrategy.Nearest,
        ServerAlternative[]? alternatives = null, string? notes = null) =>
        new(def, install, formatter, alternatives ?? [], roots, notes);

    private static ServerAlternative Alt(string command, params string[] args) => new(command, args);

    private static InstallHints All3(string text) => InstallHints.Everywhere(text);

    /// <summary>Every built-in server with its install hints, formatter, alternatives and root strategy (the support matrix is generated from this).</summary>
    public static IReadOnlyList<BuiltinServer> Catalog { get; } =
    [
        // ---- the original twelve ------------------------------------------------------------------------------------------------------
        S(Def("typescript", "TypeScript / JavaScript", ["typescript", "typescriptreact", "javascript", "javascriptreact"], "typescript-language-server", ["--stdio"],
                ["tsconfig.json", "jsconfig.json", "package.json"]) with { RestartAboveBytes = TypeScriptEngineCap },
            All3("Install it with: npm install -g typescript-language-server typescript (or run tools/fetch-lsp-tools.sh)."),
            "typescript-language-server (tsserver)"),
        // --features metadata-uris (csharp-ls 0.28): go to definition into a referenced assembly answers a csharp: location whose decompiled text
        // comes from csharp/metadata (without it, nothing); opened as a read-only virtual document, docs/CSHARP.md
        S(Def("csharp", "C# (csharp-ls)", ["csharp"], "csharp-ls", ["--features", "metadata-uris"], ["*.sln", "*.slnx", "*.csproj"]),
            All3("Install it with: dotnet tool install --global csharp-ls"),
            "csharp-ls") with { Group = CSharpGroup },
        // Microsoft's Roslyn server (what VS Code's C# extension runs), docs/CSHARP.md. One server per solution: the outermost marked directory
        // (a repository's .slnx, not each project's .csproj); the client then opens the solution (solution/open) or its projects (project/open).
        // Telemetry off; logs into CsCode's logs directory; --clientProcessId ends it if CsCode dies without stopping it.
        S(Def("roslyn", "C# (Roslyn)", ["csharp"], "roslyn-language-server", RoslynArgs, ["*.sln", "*.slnx", "*.csproj"]),
            All3("Install it with: dotnet tool install --global roslyn-language-server --prerelease (CsCode's development checkout: tools/fetch-roslyn-ls.sh)"),
            "Roslyn", RootStrategy.Outermost, [new ServerAlternative("Microsoft.CodeAnalysis.LanguageServer", RoslynArgs)])
            with { Group = CSharpGroup, IsGroupDefault = false },
        S(Def("python", "Python (Pyright)", ["python"], "pyright-langserver", ["--stdio"],
                ["pyrightconfig.json", "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt"]),
            All3("Install it with: npm install -g pyright"),
            "none (Pyright does not format)"),
        S(Def("clangd", "C / C++ (clangd)", ["c", "cpp", "objective-c"], "clangd", [], ["compile_commands.json", "compile_flags.txt", ".clangd"]),
            new("Install clangd from your LLVM / distribution package (e.g. sudo apt install clangd).",
                "Install it with: brew install llvm (clangd is in $(brew --prefix llvm)/bin), or xcode-select --install for Apple's clangd.",
                "Install it with: winget install LLVM.LLVM"),
            "clangd (clang-format rules)"),
        S(Def("rust-analyzer", "Rust (rust-analyzer)", ["rust"], "rust-analyzer", [], ["Cargo.toml"]),
            All3("Install it with: rustup component add rust-analyzer"),
            "rust-analyzer (rustfmt)"),
        S(Def("gopls", "Go (gopls)", ["go"], "gopls", [], ["go.work", "go.mod"]),
            All3("Install it with: go install golang.org/x/tools/gopls@latest"),
            "gopls (gofmt)"),
        S(Def("json", "JSON", ["json", "jsonc"], "vscode-json-language-server", ["--stdio"], [], """{"provideFormatter":true}"""),
            All3("Install it with: npm install -g vscode-langservers-extracted"),
            "vscode-json-language-server"),
        S(Def("html", "HTML", ["html"], "vscode-html-language-server", ["--stdio"], [], """{"provideFormatter":true,"embeddedLanguages":{"css":true,"javascript":true}}"""),
            All3("Install it with: npm install -g vscode-langservers-extracted"),
            "vscode-html-language-server"),
        S(Def("css", "CSS / SCSS / Less", ["css", "scss", "less"], "vscode-css-language-server", ["--stdio"], [], """{"provideFormatter":true}"""),
            All3("Install it with: npm install -g vscode-langservers-extracted"),
            "vscode-css-language-server"),
        S(Def("bash", "Bash", ["shellscript"], "bash-language-server", ["start"], []),
            All3("Install it with: npm install -g bash-language-server"),
            "bash-language-server (shfmt, when installed)"),
        S(Def("yaml", "YAML", ["yaml"], "yaml-language-server", ["--stdio"], []),
            All3("Install it with: npm install -g yaml-language-server"),
            "yaml-language-server (built-in Prettier)"),
        S(Def("lua", "Lua", ["lua"], "lua-language-server", [], [".luarc.json", ".luarc.jsonc", ".stylua.toml"]),
            new("Install lua-language-server from https://github.com/LuaLS/lua-language-server/releases (extract it and put bin/ on PATH).",
                "Install it with: brew install lua-language-server",
                "Install lua-language-server from https://github.com/LuaLS/lua-language-server/releases (extract it and add bin\\ to PATH)."),
            "lua-language-server (EmmyLuaCodeStyle)"),

        // ---- JVM -------------------------------------------------------------------------------------------------------------------------
        // jdtls' launcher (bin/jdtls, Python) picks a per-project -data directory itself; Java 21+ must be on PATH or in JAVA_HOME.
        S(Def("jdtls", "Java (Eclipse JDT LS)", ["java"], "jdtls", [],
                ["settings.gradle", "settings.gradle.kts", "pom.xml", "build.gradle", "build.gradle.kts", "build.xml", ".project"]),
            new("Download the latest jdt-language-server-*.tar.gz from https://download.eclipse.org/jdtls/milestones/, extract it and put its bin/ on PATH "
                    + "(needs Java 21+ and Python 3), or: nix profile install nixpkgs#jdt-language-server",
                "Install it with: brew install jdtls",
                "Download the latest jdt-language-server-*.tar.gz from https://download.eclipse.org/jdtls/milestones/, extract it and add its bin\\ (jdtls.bat) to PATH "
                    + "(needs Java 21+ and Python 3)."),
            "jdtls (Eclipse formatter)", RootStrategy.Outermost),
        S(Def("kotlin-language-server", "Kotlin (kotlin-language-server)", ["kotlin"], "kotlin-language-server", [],
                ["settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts", "pom.xml"]),
            new("Download server.zip from https://github.com/fwcd/kotlin-language-server/releases, extract it and put server/bin on PATH, "
                    + "or: nix profile install nixpkgs#kotlin-language-server",
                "Install it with: brew install kotlin-language-server",
                "Download server.zip from https://github.com/fwcd/kotlin-language-server/releases, extract it and add server\\bin (kotlin-language-server.bat) to PATH."),
            "kotlin-language-server (ktfmt)", RootStrategy.Outermost),
        S(Def("metals", "Scala (Metals)", ["scala"], "metals", [],
                ["build.sbt", "build.sc", "build.mill", ".scala-build", "pom.xml", "build.gradle", "build.gradle.kts"],
                """{"statusBarProvider":"log-message","compilerOptions":{"snippetAutoIndent":false}}"""),
            All3("Install it with Coursier (https://get-coursier.io): cs install metals"),
            "Metals (scalafmt)", RootStrategy.Outermost),
        S(Def("clojure-lsp", "Clojure (clojure-lsp)", ["clojure"], "clojure-lsp", [],
                ["deps.edn", "project.clj", "shadow-cljs.edn", "bb.edn", "build.boot", ".lsp"]),
            new("Install it with: bash < <(curl -s https://raw.githubusercontent.com/clojure-lsp/clojure-lsp/master/install) (or: nix profile install nixpkgs#clojure-lsp)",
                "Install it with: brew install clojure-lsp/brew/clojure-lsp-native",
                "Install it with: scoop bucket add scoop-clojure https://github.com/littleli/scoop-clojure; scoop install clojure-lsp"),
            "clojure-lsp (cljfmt)"),

        // ---- systems languages --------------------------------------------------------------------------------------------------------------
        S(Def("zls", "Zig (zls)", ["zig"], "zls", [], ["build.zig", "zls.json"]),
            new("Download zls for your Zig version from https://github.com/zigtools/zls/releases and put it on PATH, or: nix profile install nixpkgs#zls",
                "Install it with: brew install zls",
                "Download zls.exe for your Zig version from https://github.com/zigtools/zls/releases and put it on PATH."),
            "zls (zig fmt)"),
        S(Def("sourcekit-lsp", "Swift (SourceKit-LSP)", ["swift"], "sourcekit-lsp", [],
                ["Package.swift", "buildServer.json", "compile_commands.json", "*.xcodeproj", "*.xcworkspace"]),
            new("Install the Swift toolchain from https://www.swift.org/install/linux/ (it includes sourcekit-lsp) and put its usr/bin on PATH.",
                "It ships with Xcode and the Command Line Tools: xcode-select --install",
                "Install the Swift toolchain (it includes sourcekit-lsp): winget install --id Swift.Toolchain -e"),
            "sourcekit-lsp (swift-format, Swift 6+)"),

        // ---- functional languages ---------------------------------------------------------------------------------------------------------
        S(Def("haskell-language-server", "Haskell (HLS)", ["haskell"], "haskell-language-server-wrapper", ["--lsp"],
                ["hie.yaml", "cabal.project", "stack.yaml", "*.cabal", "package.yaml"]),
            All3("Install it with GHCup (https://www.haskell.org/ghcup/): ghcup install hls"),
            "haskell-language-server (Ormolu / Fourmolu plugins)", RootStrategy.Outermost,
            [Alt("haskell-language-server", "--lsp")]),
        S(Def("elixir", "Elixir (ElixirLS / Lexical)", ["elixir"], "elixir-ls", [], ["mix.exs"]),
            new("Download elixir-ls-*.zip from https://github.com/elixir-lsp/elixir-ls/releases, extract it and put language_server.sh on PATH, "
                    + "or: nix profile install nixpkgs#elixir-ls (Lexical: nixpkgs#lexical)",
                "Install it with: brew install elixir-ls",
                "Download elixir-ls-*.zip from https://github.com/elixir-lsp/elixir-ls/releases, extract it and set cscode.languageServers.elixir.command to its language_server.bat."),
            "ElixirLS / Lexical (mix format)", RootStrategy.Outermost,
            [Alt("language_server.sh"), Alt("lexical")]),
        S(Def("erlang", "Erlang (erlang_ls / ELP)", ["erlang"], "erlang_ls", [], ["rebar.config", "erlang.mk", "erlang_ls.config", ".elp.toml"]),
            new("Build it: git clone https://github.com/erlang-ls/erlang_ls && cd erlang_ls && make && sudo make install "
                    + "(or install ELP from https://github.com/WhatsApp/erlang-language-platform/releases)",
                "Install it with: brew install erlang_ls",
                "Build it with rebar3: git clone https://github.com/erlang-ls/erlang_ls && cd erlang_ls && rebar3 escriptize, then set "
                    + "cscode.languageServers.erlang.command to _build/default/bin/erlang_ls (run through escript)."),
            "erlang_ls / ELP", RootStrategy.Outermost,
            [Alt("elp", "server")]),
        S(Def("ocaml-lsp", "OCaml (ocamllsp)", ["ocaml"], "ocamllsp", [], ["dune-project", "dune-workspace", "*.opam", "esy.json", "_opam"]),
            All3("Install it with opam: opam install ocaml-lsp-server (formatting also needs: opam install ocamlformat)"),
            "ocamllsp (ocamlformat, when installed)", RootStrategy.Outermost),

        // ---- configuration and data languages -------------------------------------------------------------------------------------------
        // nil formats through an external command; nixfmt is the official Nix formatter (RFC 166). Both servers read their own section.
        S(Def("nix", "Nix (nil / nixd)", ["nix"], "nil", [], ["flake.nix", "default.nix", "shell.nix"],
                settings: """{"nil":{"formatting":{"command":["nixfmt"]}},"nixd":{"formatting":{"command":["nixfmt"]}}}"""),
            new("Install it with: nix profile install nixpkgs#nil (or nixpkgs#nixd); formatting uses nixfmt: nix profile install nixpkgs#nixfmt",
                "Install it with: nix profile install nixpkgs#nil (or nixpkgs#nixd); formatting uses nixfmt: nix profile install nixpkgs#nixfmt",
                "nil and nixd do not run on Windows natively: use them through WSL."),
            "nil / nixd running nixfmt", RootStrategy.Outermost,
            [Alt("nixd")]),
        S(Def("taplo", "TOML (Taplo)", ["toml"], "taplo", ["lsp", "stdio"], [".taplo.toml", "taplo.toml"]),
            new("Install it with: cargo install --locked taplo-cli --features lsp",
                "Install it with: brew install taplo",
                "Install it with: cargo install --locked taplo-cli --features lsp"),
            "taplo"),
        // terraform-ls indexes every module under its root itself: one server for the workspace folder(s), no markers.
        S(Def("terraform-ls", "Terraform (terraform-ls)", ["terraform", "terraform-vars"], "terraform-ls", ["serve"], []),
            new("Download terraform-ls from https://releases.hashicorp.com/terraform-ls/ and put it on PATH (or: sudo apt install terraform-ls from HashiCorp's apt repository)",
                "Install it with: brew install hashicorp/tap/terraform-ls",
                "Download terraform-ls from https://releases.hashicorp.com/terraform-ls/ and put terraform-ls.exe on PATH."),
            "terraform-ls (terraform fmt; needs terraform on PATH)"),
        S(Def("dockerfile", "Dockerfile (docker-langserver)", ["dockerfile"], "docker-langserver", ["--stdio"], []),
            All3("Install it with: npm install -g dockerfile-language-server-nodejs"),
            "docker-langserver"),
        S(Def("cmake", "CMake (cmake-language-server)", ["cmake"], "cmake-language-server", [],
                ["CMakePresets.json", "CMakeUserPresets.json", "CTestConfig.cmake"], """{"buildDirectory":"build"}"""),
            All3("Install it with: pipx install cmake-language-server (or: pip install --user cmake-language-server)"),
            "cmake-language-server (cmake-format, when installed)"),
        S(Def("sqls", "SQL (sqls)", ["sql"], "sqls", [], []),
            All3("Install it with: go install github.com/sqls-server/sqls@latest"),
            "sqls"),
        S(Def("protobuf", "Protocol Buffers (buf / protols)", ["protobuf"], "buf", ["lsp", "serve"], ["buf.yaml", "buf.work.yaml"]),
            new("Install it with: npm install -g @bufbuild/buf (or the alternative server: cargo install protols)",
                "Install it with: brew install bufbuild/buf/buf (or the alternative server: cargo install protols)",
                "Install it with: npm install -g @bufbuild/buf (or the alternative server: cargo install protols)"),
            "buf (buf format) / protols (clang-format)", RootStrategy.Outermost,
            [Alt("protols")], "Older buf releases name the server `buf beta lsp`: set cscode.languageServers.protobuf.args to [\"beta\", \"lsp\"] for those."),
        S(Def("graphql", "GraphQL (graphql-lsp)", ["graphql"], "graphql-lsp", ["server", "-m", "stream"],
                [".graphqlrc*", "graphql.config.*", "package.json"]),
            All3("Install it with: npm install -g graphql-language-service-cli"),
            "none (graphql-lsp does not format)"),

        // ---- documents -----------------------------------------------------------------------------------------------------------------------
        // Marksman indexes a folder only when it holds .marksman.toml or .git ("workspace folder is bogus" otherwise, and it works file by
        // file without link diagnostics), so a git repository is a root too.
        S(Def("marksman", "Markdown (Marksman)", ["markdown"], "marksman", ["server"], [".marksman.toml", ".git"]),
            new("Download marksman-linux-x64 (or -arm64) from https://github.com/artempyanykh/marksman/releases, rename it to marksman, chmod +x it and put it on PATH "
                    + "(or: nix profile install nixpkgs#marksman)",
                "Install it with: brew install marksman",
                "Download marksman.exe from https://github.com/artempyanykh/marksman/releases and put it on PATH."),
            "none (Marksman does not format)"),

        // ---- web / scripting ---------------------------------------------------------------------------------------------------------------
        S(Def("ruby-lsp", "Ruby (ruby-lsp)", ["ruby"], "ruby-lsp", [], ["Gemfile", "gems.rb"]),
            All3("Install it with: gem install ruby-lsp"),
            "ruby-lsp (RuboCop or Syntax Tree, when in the bundle)"),
        S(Def("phpactor", "PHP (Phpactor)", ["php"], "phpactor", ["language-server"], ["composer.json", ".phpactor.json", ".phpactor.yml"]),
            new("Install it with: curl -Lo ~/.local/bin/phpactor https://github.com/phpactor/phpactor/releases/latest/download/phpactor.phar && chmod +x ~/.local/bin/phpactor (needs PHP 8.1+)",
                "Install it with: brew install phpactor",
                "Phpactor does not support Windows: use it through WSL."),
            "none by default (Phpactor's php-cs-fixer integration is opt-in)"),
        S(Def("dart", "Dart (analysis server)", ["dart"], "dart", ["language-server", "--protocol=lsp"], ["pubspec.yaml"],
                """{"onlyAnalyzeProjectsWithOpenFiles":true,"suggestFromUnimportedLibraries":true,"closingLabels":false,"outline":false,"flutterOutline":false}"""),
            new("Install the Dart SDK (it includes the server): add Google's apt repository and run sudo apt-get install dart (https://dart.dev/get-dart), or use the Flutter SDK's dart",
                "Install it with: brew tap dart-lang/dart && brew install dart",
                "Install it with: choco install dart-sdk"),
            "dart language-server (dart format)"),
        // @vue/language-server 2.x in non-hybrid mode does TypeScript itself and needs the project's TypeScript SDK; 3.x expects the client
        // to route TypeScript through a tsserver plugin, which CsCode does not do (only its HTML/CSS features work then).
        S(Def("vue", "Vue (vue-language-server)", ["vue"], "vue-language-server", ["--stdio"], ["package.json"],
                """{"typescript":{"tsdk":"${workspaceFolder}/node_modules/typescript/lib"},"vue":{"hybridMode":false}}""") with { RestartAboveBytes = TypeScriptEngineCap },
            All3("Install it with: npm install -g @vue/language-server@2 (the project needs typescript in its node_modules)"),
            "vue-language-server", notes: "Written for @vue/language-server 2.x (non-hybrid mode); 3.x needs a tsserver plugin route CsCode does not implement."),
        S(Def("svelte", "Svelte (svelteserver)", ["svelte"], "svelteserver", ["--stdio"], ["svelte.config.js", "package.json"]) with { RestartAboveBytes = TypeScriptEngineCap },
            All3("Install it with: npm install -g svelte-language-server"),
            "svelteserver (prettier-plugin-svelte)"),
    ];

    /// <summary>The groups of alternative built-ins (<see cref="BuiltinServer.Group"/>) and their members, in catalog order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BuiltinServer>> Groups { get; } =
        Catalog.Where(s => s.Group is not null).GroupBy(s => s.Group!).ToDictionary(g => g.Key, g => (IReadOnlyList<BuiltinServer>)g.ToArray(), StringComparer.Ordinal);

    /// <summary><c>cscode.lsp.serverChoice</c> as group → definition id (members that are not strings are ignored).</summary>
    public static IReadOnlyDictionary<string, string> ReadChoice(JsonElement? setting)
    {
        var choice = new Dictionary<string, string>(StringComparer.Ordinal);
        if (setting is { ValueKind: JsonValueKind.Object } obj)
            foreach (var p in obj.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } id) choice[p.Name] = id;
        return choice;
    }

    /// <summary>
    /// Whether a definition runs when <c>cscode.languageServers</c> does not say: yes, unless it is a member of a group whose choice (or, with no
    /// valid choice, whose default) is another member.
    /// </summary>
    public static bool EnabledByDefault(string definitionId, IReadOnlyDictionary<string, string> choice)
    {
        if (!ById.TryGetValue(definitionId, out var s) || s.Group is not { } group) return true;
        return choice.TryGetValue(group, out var chosen) && Groups[group].Any(m => m.Definition.Id == chosen)
            ? chosen == definitionId
            : s.IsGroupDefault;
    }

    /// <summary>The built-in definitions (what <see cref="LanguageServerManager"/> registers at start-up).</summary>
    public static IReadOnlyList<LanguageServerDefinition> All { get; } = Catalog.Select(s => s.Definition).ToArray();

    private static readonly Dictionary<string, BuiltinServer> ById = Catalog.ToDictionary(s => s.Definition.Id, StringComparer.Ordinal);

    public static BuiltinServer? Find(string definitionId) => ById.GetValueOrDefault(definitionId);

    /// <summary>Human-readable install instructions for this platform, shown once when the executable cannot be found.</summary>
    public static string InstallHint(string definitionId) =>
        ById.TryGetValue(definitionId, out var s)
            ? s.Install.ForCurrentPlatform
            : "Install it and make sure it is on your PATH, or set its location in the cscode.languageServers setting.";

    /// <summary>
    /// Alternatives for an EFFECTIVE definition: only while it still launches the built-in command and arguments. A user who points
    /// <c>cscode.languageServers.&lt;id&gt;.command</c> somewhere else gets exactly that command, never a silent substitute.
    /// </summary>
    public static IReadOnlyList<ServerAlternative> AlternativesFor(LanguageServerDefinition effective) =>
        ById.TryGetValue(effective.Id, out var s) && s.Alternatives.Count > 0
            && s.Definition.Command == effective.Command && s.Definition.Args.SequenceEqual(effective.Args)
            ? s.Alternatives
            : [];

    /// <summary>The root strategy of an EFFECTIVE definition: the built-in one while its root markers are unchanged, else <see cref="RootStrategy.Nearest"/>.</summary>
    public static RootStrategy RootStrategyFor(LanguageServerDefinition effective) =>
        ById.TryGetValue(effective.Id, out var s) && (s.Definition.RootMarkers ?? []).SequenceEqual(effective.RootMarkers ?? [])
            ? s.Roots
            : RootStrategy.Nearest;
}
