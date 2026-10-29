# CsCode

A VSCodium-class code editor written in C# (.NET 10) with a native Avalonia UI modelled on Visual Studio 2022,
LSP + DAP support, VS Code extension compatibility (Open VSX) and a memory-stable design.

* Architecture and module boundaries: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
* Build: `dotnet build CsCode.slnx` · Test: `dotnet test CsCode.slnx` · Run: `dotnet run --project src/CsCode.App`
* On a Nix-provided `dotnet` (non-NixOS host): `source tools/nix-dev-env.sh` first.
