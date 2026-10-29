# CsCode

A VSCodium-class code editor written in C# (.NET 10) with a native Avalonia UI modelled on Visual Studio 2022,
LSP + DAP support and a memory-stable design. There is deliberately no extension system: customisation is
file-based and uses the VS Code formats where they exist — `settings.json`, `keybindings.json`, `launch.json`/`tasks.json`,
language servers (`cscode.languageServers`) and debug adapters (`cscode.debuggers`) from settings, and theme JSON,
TextMate grammars and snippets dropped into the user data folder.

* Architecture and module boundaries: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
* Build: `dotnet build CsCode.slnx` · Test: `dotnet test CsCode.slnx` · Run: `dotnet run --project src/CsCode.App`
* On a Nix-provided `dotnet` (non-NixOS host): `source tools/nix-dev-env.sh` first.
