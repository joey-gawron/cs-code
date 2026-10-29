using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Lsp.Manager;

/// <summary>
/// The language servers CsCode knows out of the box (data only: nothing here starts a process). Any stdio language server works by adding
/// a <see cref="LanguageServerDefinition"/> through <see cref="ILanguageServerManager.AddDefinition"/> or the <c>cscode.languageServers</c> setting.
/// Executables are looked up on PATH, then in <c>{ToolsDir}/lsp/**</c> (see tools/fetch-lsp-tools.sh).
/// </summary>
public static class BuiltinLanguageServers
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static LanguageServerDefinition Def(string id, string name, string[] languages, string command, string[] args, string[] markers, string? initOptions = null) =>
        new(id, name, DocumentSelector.ForLanguages(languages), command, args,
            RootMarkers: markers,
            InitializationOptions: initOptions is null ? null : Json(initOptions),
            SettingsKey: LspSettingKeys.LanguageServers + "." + id);

    public static IReadOnlyList<LanguageServerDefinition> All { get; } =
    [
        Def("typescript", "TypeScript / JavaScript", ["typescript", "typescriptreact", "javascript", "javascriptreact"], "typescript-language-server", ["--stdio"],
            ["tsconfig.json", "jsconfig.json", "package.json"]),
        Def("csharp", "C# (csharp-ls)", ["csharp"], "csharp-ls", [], ["*.sln", "*.slnx", "*.csproj"]),
        Def("python", "Python (Pyright)", ["python"], "pyright-langserver", ["--stdio"],
            ["pyrightconfig.json", "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt"]),
        Def("clangd", "C / C++ (clangd)", ["c", "cpp", "objective-c"], "clangd", [], ["compile_commands.json", "compile_flags.txt", ".clangd"]),
        Def("rust-analyzer", "Rust (rust-analyzer)", ["rust"], "rust-analyzer", [], ["Cargo.toml"]),
        Def("gopls", "Go (gopls)", ["go"], "gopls", [], ["go.work", "go.mod"]),
        Def("json", "JSON", ["json", "jsonc"], "vscode-json-language-server", ["--stdio"], [], """{"provideFormatter":true}"""),
        Def("html", "HTML", ["html"], "vscode-html-language-server", ["--stdio"], [], """{"provideFormatter":true,"embeddedLanguages":{"css":true,"javascript":true}}"""),
        Def("css", "CSS / SCSS / Less", ["css", "scss", "less"], "vscode-css-language-server", ["--stdio"], [], """{"provideFormatter":true}"""),
        Def("bash", "Bash", ["shellscript"], "bash-language-server", ["start"], []),
        Def("yaml", "YAML", ["yaml"], "yaml-language-server", ["--stdio"], []),
        Def("lua", "Lua", ["lua"], "lua-language-server", [], [".luarc.json", ".luarc.jsonc", ".stylua.toml"]),
    ];

    private static readonly Dictionary<string, string> Hints = new()
    {
        ["typescript"] = "Install it with: npm install -g typescript-language-server typescript (or run tools/fetch-lsp-tools.sh).",
        ["csharp"] = "Install it with: dotnet tool install --global csharp-ls",
        ["python"] = "Install it with: npm install -g pyright",
        ["clangd"] = "Install clangd from your LLVM / distribution package (e.g. apt install clangd).",
        ["rust-analyzer"] = "Install it with: rustup component add rust-analyzer",
        ["gopls"] = "Install it with: go install golang.org/x/tools/gopls@latest",
        ["json"] = "Install it with: npm install -g vscode-langservers-extracted",
        ["html"] = "Install it with: npm install -g vscode-langservers-extracted",
        ["css"] = "Install it with: npm install -g vscode-langservers-extracted",
        ["bash"] = "Install it with: npm install -g bash-language-server",
        ["yaml"] = "Install it with: npm install -g yaml-language-server",
        ["lua"] = "Install lua-language-server from https://github.com/LuaLS/lua-language-server/releases",
    };

    /// <summary>Human-readable install instructions shown once when the executable cannot be found.</summary>
    public static string InstallHint(string definitionId) =>
        Hints.TryGetValue(definitionId, out var h) ? h : "Install it and make sure it is on your PATH, or set its location in the cscode.languageServers setting.";
}
