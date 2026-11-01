# CsCode

A VSCodium-class code editor written in C# (.NET 10) with a native Avalonia UI modelled on Visual Studio 2022,
LSP + DAP support and a memory-stable design. There is deliberately no extension system: customisation is
file-based and uses the VS Code formats where they exist — `settings.json`, `keybindings.json`, `launch.json`/`tasks.json`,
language servers (`cscode.languageServers`) and debug adapters (`cscode.debuggers`) from settings, and theme JSON,
TextMate grammars and snippets dropped into the user data folder.

* Architecture and module boundaries: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
* Running and debugging (F5, Start Without Debugging, launch.json, the debug windows, and how to run, debug and test CsCode itself):
  [docs/RUN-AND-DEBUG.md](docs/RUN-AND-DEBUG.md)
* Build: `dotnet build CsCode.slnx` · Test: `dotnet test CsCode.slnx`
* Linux only: on a Nix-provided `dotnet` (non-NixOS host), `source tools/nix-dev-env.sh` first.
* macOS: see [macOS](#macos) below.

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

F5 in CsCode (or VS Code) on this repository debugs CsCode in a sandboxed window (`.vscode/launch.json`;
[docs/RUN-AND-DEBUG.md](docs/RUN-AND-DEBUG.md#debugging-cscode-itself)).

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
* Only linux-x64's runtime packs are pinned: on another machine (aarch64) the install refuses unless you pass `--unpinned` (publish with
  the packs restore picks, unchecked) or `--from DIR`.
* Files: the app in `~/.local/share/cscode/<version>/` with a `current` symlink (the previous version is kept for rollback:
  `ln -sfn <previous> ~/.local/share/cscode/current`), the launcher `~/.local/bin/cscode` (passes every argument and your environment,
  `DOTNET_ROOT` included, as they are; adds `~/.dotnet/tools` to PATH), `~/.local/share/applications/cscode.desktop`
  (Development menu; a New Window action) and the icons in `~/.local/share/icons/hicolor`. Every file it creates is listed in
  `~/.local/share/cscode/install-manifest.tsv`.
* Rerunning it upgrades: the new build becomes `current`, the one before stays, older ones go (one still running is kept until a later
  install). Running windows keep their version until they quit (`cscode --quit --all`).
* The installed app uses the normal per-user directories (`~/.config/cscode`, `~/.local/share/cscode`, `~/.local/state/cscode`).
* `~/.local/share/cscode/current/uninstall.sh` (or `tools/uninstall.sh`) removes exactly what the manifest lists and keeps your settings,
  sessions and data. Both scripts refuse to replace or remove a `cscode` launcher or desktop entry they did not create.

Windows: `tools/install.ps1` (Start Menu shortcut, `cscode` on the user's PATH; `-Uninstall`), written but untested. macOS: see below.
Details: [docs/SESSIONS.md](docs/SESSIONS.md), "Installing".

## macOS

Develop: install the .NET 10 SDK (the installer from dot.net, or `brew install --cask dotnet-sdk`), then

```
dotnet build CsCode.slnx
dotnet run --project src/CsCode.App -- --foreground [paths...]
```

`brew install node` is optional: the TypeScript and JavaScript debugging tests need `node` (and `npm install -g typescript` for `tsc`);
they are skipped without it. The debug adapters come from `tools/fetch-netcoredbg.sh`, `tools/fetch-js-debug.sh` and
`tools/fetch-codelldb.sh`, which run on the stock bash 3.2 and BSD tools (an Intel Mac gets netcoredbg 3.1.3-1062, Samsung's last Intel
build). `tools/nix-dev-env.sh` is not needed and does nothing on a Mac.

Package and install (per user, on the Mac that builds it):

```
tools/package-macos.sh                     # .scratch/macos/osx-arm64/CsCode.app (osx-x64 on an Intel Mac)
tools/package-macos.sh --install           # and put it in ~/Applications, with a `cscode` command on PATH
tools/package-macos.sh --rid osx-arm64 --rid osx-x64   # both runtimes, each in .scratch/macos/<rid>/
```

* It publishes the app (`tools/publish-app.sh --unpinned`: the osx runtime packs are the ones restore picks from nuget.org, unchecked;
  only linux-x64's are pinned), builds the remote server bundles (`tools/publish-server.sh`, shipped in `Contents/MacOS/server-bundles`;
  `--no-server-bundles` or `--server-bundles DIR` instead), bundles the debug adapters as `tools/install.sh` does on Linux (netcoredbg,
  js-debug and CodeLLDB, the Mac's Rust/C/C++ debugger, about 155 MB, in `Contents/Resources/debug-adapters`: from `--netcoredbg`,
  `--js-debug`, `--codelldb` or `CSCODE_NETCOREDBG`/`CSCODE_JSDEBUG`/`CSCODE_CODELLDB`, else the main checkout's `.scratch/tools`, where the
  `tools/fetch-*.sh` scripts fetch the pinned versions, digests checked, when they are missing; `--no-adapters` ships none and the app offers
  each download when a session needs it) and assembles `CsCode.app` (`tools/macos/make-app-bundle.sh`): Info.plist
  (`local.cscode.CsCode`, macOS 12 or later, Retina), an `.icns` built from `assets/branding` with `sips` and `iconutil`, and document types
  so Finder's *Open With* lists CsCode for text, source files and folders (never as their default app). Opening a file that way starts
  CsCode but does not open the file yet (no file-activation handling: docs/handoff/macos-audit.md, pkg-3).
* The bundle is signed ad hoc (`codesign --sign -`; each debug adapter's executables and libraries first, on their own), checked with
  `plutil -lint` and `codesign --verify --deep --strict`. That is enough for
  the Mac that built it; a copy given to another Mac is stopped by Gatekeeper until it is signed with a Developer ID and notarized
  (not done: pkg-2).
* `--install` copies the app to `~/Applications/CsCode.app` (`--app-dir /Applications` for every user, with an administrator account)
  and writes `cscode`, a two-line script that execs `CsCode.app/Contents/MacOS/cscode` with every argument, into the first writable
  directory on your PATH of `/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin` and `~/bin` (`--bin-dir DIR` to choose). When none is on
  PATH it uses `~/.local/bin` and prints the line to add to `~/.zprofile`. It never replaces a `cscode` it did not write (`--force` to).
* Rerunning `--install` upgrades in place. To uninstall: quit CsCode (`cscode --quit --all`), then delete `~/Applications/CsCode.app` and
  the `cscode` script; settings, sessions and data stay in `~/Library/Application Support/CsCode` (caches and logs in
  `~/Library/Caches/CsCode` and `~/Library/Logs/CsCode`).
