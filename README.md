# CsCode

A VSCodium-class code editor written in C# (.NET 10) with a native Avalonia UI modelled on Visual Studio 2022,
LSP + DAP support and a memory-stable design. There is deliberately no extension system: customisation is
file-based and uses the VS Code formats where they exist — `settings.json`, `keybindings.json`, `launch.json`/`tasks.json`,
language servers (`cscode.languageServers`) and debug adapters (`cscode.debuggers`) from settings, and theme JSON,
TextMate grammars and snippets dropped into the user data folder.

* Architecture and module boundaries: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
* Build: `dotnet build CsCode.slnx` · Test: `dotnet test CsCode.slnx`
* On a Nix-provided `dotnet` (non-NixOS host): `source tools/nix-dev-env.sh` first.

## Running: background mode is the default

`cscode` (and `dotnet run --project src/CsCode.App`) is a **launcher**: it hands the request to a CsCode process that keeps the
session's window — starting one when none runs — and returns at once. Closing a window only hides it, so the next `cscode`
shows it again instantly with its tabs, unsaved edits and language servers intact (*File ▸ Quit Session* ends it). Details:
[docs/SESSIONS.md](docs/SESSIONS.md).

```
cscode [paths...]              files (file:line[:col]) open in the last session; a folder opens its own session
cscode --session NAME [paths]  open (or create) a named session
cscode --list-sessions         saved and running sessions
cscode --quit [NAME | --all]   quit a session's process, or every CsCode process
cscode --foreground [paths]    no background mode: the window runs in this process and closing it exits
```

From a source tree, `--foreground` gives the classic single-process run (what a debugger or `dotnet run` users usually want):

```
dotnet run --project src/CsCode.App -- --foreground [paths...]
```

Without it, `dotnet run` returns as soon as the window is up and the window keeps running in its own process (`cscode --quit --all`,
or `dotnet run --project src/CsCode.App -- --quit --all`, ends it). The pre-started "warm spare" process is off by default
(`cscode.background.warmSpare`).

## Publishing

`tools/publish-app.sh` builds what the installer installs: a self-contained linux-x64 app with a ReadyToRun composite image
(`build/Publish.props`), in `.scratch/publish/linux-x64/`. It runs on the system's libraries, without a `dotnet` and without the
Nix shell; on a Nix SDK it takes the official runtime packs from nuget.org (`tools/fetch-publish-packs.sh` pins them). Start-up
numbers: [docs/SESSIONS.md](docs/SESSIONS.md), "Start-up performance".

## Install (per user, Linux)

`tools/install.sh` installs CsCode for the current user, so it runs from a terminal (`cscode`) and from the desktop's application menu, with
the app icon (`assets/branding`) in the menu, the title bar and the taskbar. No sudo; nothing outside your home directory. On this machine's
Nix SDK run it as `bash .scratch/notes/with-nix-env.sh tools/install.sh` (the library path only serves the build).

* It builds the tree-sitter grammars offline from the pinned sources, publishes the app (`tools/publish-app.sh`), builds the remote server
  bundles (`tools/publish-server.sh`) and copies the debug adapters already on the machine (netcoredbg, js-debug, CodeLLDB:
  `--netcoredbg`/`--js-debug`/`--codelldb` or `CSCODE_NETCOREDBG`/`CSCODE_JSDEBUG`/`CSCODE_CODELLDB`) next to the app, where the app finds
  them without those variables. Nothing is downloaded; `--from DIR` installs an existing publish instead.
* Files: the app in `~/.local/share/cscode/<version>/` with a `current` symlink (the previous version is kept for rollback:
  `ln -sfn <previous> ~/.local/share/cscode/current`), the launcher `~/.local/bin/cscode`, `~/.local/share/applications/cscode.desktop`
  (Development menu; a New Window action) and the icons in `~/.local/share/icons/hicolor`. Every file it creates is listed in
  `~/.local/share/cscode/install-manifest.tsv`.
* Rerunning it upgrades: the new build becomes `current`, the one before stays, older ones go (one still running is kept until a later
  install). Running windows keep their version until they quit (`cscode --quit --all`).
* The installed app uses the normal per-user directories (`~/.config/cscode`, `~/.local/share/cscode`, `~/.local/state/cscode`).
* `~/.local/share/cscode/current/uninstall.sh` (or `tools/uninstall.sh`) removes exactly what the manifest lists and keeps your settings,
  sessions and data. Both scripts refuse to replace or remove a `cscode` launcher or desktop entry they did not create.

Windows: `tools/install.ps1` (Start Menu shortcut, `cscode` on the user's PATH; `-Uninstall`). macOS: `tools/macos/make-app-bundle.sh` builds
`CsCode.app` with an `.icns` made from the PNGs. Both are written but untested (only Linux is available here). Details:
[docs/SESSIONS.md](docs/SESSIONS.md), "Installing".
