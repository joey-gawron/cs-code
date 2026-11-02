# Stage brief — New Project page

**User request (2026-11-01):** "a project creation page like intellij that allows me to chose the language direcotry and get me to a good start on a
project" (overview of the language stages it runs beside: `docs/briefs/languages.md`). Runs in parallel with the six language stages.

**Owns:** new `src/CsCode.ProjectTemplates/**` (the generators: no UI, references Core only), new `src/CsCode.Panels.NewProject/**` (the page:
view, view model, document view), the additive Core contract `src/CsCode.Core/Projects/**`, their tests (`tests/CsCode.ProjectTemplates.Tests`,
`tests/CsCode.Panels.NewProject.Tests`), shell wiring in `src/CsCode.App/**` (File ▸ New ▸ Project…, the Welcome page link, the composition root,
opening a folder in a new session window), `docs/NEW-PROJECT.md`. **Owns `dotnet new` template listing** (the C# stage has been told).

## What is already there (verify, don't rebuild)
Checked on `main` at `1f6e1f0`, 2026-11-01.
* **Welcome page** (`src/CsCode.App/Views/StartPages.cs`, `WelcomeView`, opened by `cscode.shell.showWelcome` through
  `DocumentWell.OpenView`): links for Open a folder, Open a file, New file and Command palette, recent folders and files, saved sessions.
* **File menu** (`src/CsCode.App/Menus/ShellMenus.cs`): New File, New Window, Open File/Folder/Remote Folder, Open Recent… There is no
  File ▸ New submenu.
* **Document pages:** a module contributes one as an `IToolWindow` with `DefaultLocation = Document` (Settings, Keyboard Shortcuts in
  `src/CsCode.Panels.Search/ToolWindows.cs`); `ShellViewActivator.Show` opens it as a singleton tab, and the shell registers an "Open …" command
  for its `ToggleCommandId` when the module did not. Modal dialogs (`IModalDialogs`) are message boxes only; there is no modal form window. The
  New Project page is therefore a **document page**, as Visual Studio 2022's "Create a new project" is a full page of its own.
* **Opening a folder:** `IWorkspaceService.OpenFolderAsync` (this window). A new window is `workbench.action.newWindow`
  (`SessionWindowController.Commands.cs`), registered only by session processes (background mode, docs/SESSIONS.md); it takes no folder yet,
  although its `LaunchRequest` carries `Targets`. A `--foreground` run has one window.
* **Install hints:** `InstallHints(Linux, MacOS, Windows)` with `ForCurrentPlatform` in `src/CsCode.Lsp/Manager/BuiltinLanguageServers.cs`
  ("Install it with: brew install …"). Lsp is not referenced from here, so the generators carry their own hints in the same style.
* **Child processes:** `IMemoryMonitor.TrackChildProcess`; `LoginShellEnvironment.WaitAsync` before a start (a Mac app started from the Dock
  gets the login shell's PATH); `InheritedDotnetRoot.Fold`. `CsCode.Testing`'s `ProcessRunner` is the pattern to follow (internal to its
  module).
* **Output channels:** `IOutputService.GetOrCreate(name)` (bounded).
* **Debug adapters** (`src/CsCode.Dap/Adapters`): CodeLLDB `lldb` (languages rust, c, cpp; the `cargo` key; its Rust template uses
  `preLaunchTask: "cargo: build"` with `$rustc`), lldb-dap, netcoredbg `coreclr` (template: `preLaunchTask: "build"`, `dotnet build` with
  `$msCompile`, `program` = `bin/Debug/<tfm>/<name>.dll`), js-debug `pwa-node`. **No `debugpy` on main** (the Python stage brings it), **no
  `zig` in CodeLLDB's languages** (the Zig stage adds it). Problem matchers: `$msCompile`, `$gcc`, `$rustc`, `$tsc`.
* **Missing on main, owned by other stages:** interpreter discovery and selection (Python), `ITaskProvider` (Rust), CMake integration and CTest
  (C). The post-creation hooks that need them are deferred until they merge.

## Toolchain on this Mac (checked 2026-11-01)
python3 3.14 (Homebrew; no uv, no pytest, no ruff installed), cargo/rustc 1.96.1 (`~/.cargo/bin`), .NET SDK 10.0.403 (`~/.dotnet`), CMake 4.4.2,
Ninja 1.13.2, Apple clang 21 (`cc`, `c++`), git 2.54. **Not installed:** zig, uv, clang-format (the `.clang-format` file is still written). Later, with the user's go-ahead: zig 0.17.0 and
uv 0.13.6 (Homebrew, 2026-11-02).

`dotnet new list` prints a fixed-width table (Template Name, Short Name, Language, Tags) whose column widths are given by the dashes line;
`dotnet new sln` takes `--format sln|slnx` (default slnx); `dotnet new console --framework` lists its choices in `--help`; `cargo new` takes
`--bin|--lib`, `--edition 2015|2018|2021|2024`, `--vcs git|hg|pijul|fossil|none`, `--name`.

## MUST
1. **The page**, a document page in the VS 2022 look, opened by File ▸ New ▸ Project…, a "Create a new project" link on the Welcome page and
   the palette command *File: New Project…*.
   * Left: the generators, Empty, Python, Rust, C, C++, C#, Zig. Right: Name, Location with a folder browser, the line "Project will be
     created in …", the generator's own options, "Create Git repository", Create and Cancel.
   * Validation: each toolchain's name rules (Rust crate names, C# identifiers, Python package names); the target folder is missing or empty.
   * A generator whose tool is missing (cargo, dotnet, zig, cmake, a C/C++ compiler, python3) is listed but disabled, with the tool's install
     hint for the platform.
   * Remembers the last generator and options. Setting `cscode.newProject.defaultLocation`; unset: the parent of the most recently opened
     folder, else the home folder. Fully usable from the keyboard.
2. **Generators:** an additive Core `IProjectGenerator` (id, display name, icon, language, options, tool check, `GenerateAsync` with progress and
   cancellation) implemented in `src/CsCode.ProjectTemplates`. Every CLI flag checked against the installed tool.
   * Python: `pyproject.toml` (src layout, Ruff configured), `.venv` by `python -m venv` (uv when it exists), application or package, pytest
     with `tests/`.
   * Rust: `cargo new`, binary or library, edition, vcs.
   * C and C++: CMake projects written by CsCode: `CMakeLists.txt`, `CMakePresets.json` (Debug and Release; Ninja when installed),
     `src/main.c`/`src/main.cpp`, `.clang-format`; the language standard (C11/C17/C23, C++17/20/23); executable or library; CTest for C,
     GoogleTest or Catch2 through `FetchContent` for C++.
   * C#: templates from the installed SDK's `dotnet new list` (parsed off the UI thread), curated first (console, classlib, webapi, xunit);
     a solution (`.slnx` or `.sln`) with the project added; target framework; optional test project.
   * Zig: `zig init`. Empty: just the folder.
3. **A good start, every generator:** `.gitignore`, `README.md`, `.editorconfig`; `git init` and a first commit when chosen;
   `.vscode/launch.json` and `tasks.json` so F5 builds and debugs at once — Python `debugpy`; Rust CodeLLDB `lldb` with cargo; C, C++ and Zig
   `lldb` with a build pre-launch task; C# `coreclr` with `dotnet build` and `$msCompile` — each checked against the adapter factories.
4. **Running:** CLIs as tracked child processes off the UI thread, progress and output in an Output channel ("New Project"); Cancel works; on
   failure the output is shown, and only what this run created is removed, only when the folder did not exist before.
5. **After creation:** open the folder (this window when it has none, else ask: this window or a new window/session), then the main source file.

## SHOULD
* Generators for TypeScript/Node (`npm init` + `tsc --init`), JavaScript, and a static HTML/CSS site.
* A "sample code" checkbox per generator.
* Post-creation hooks, only once their owner is on `main`: select the new `.venv` (Python stage), CMake configure (C stage), `dotnet restore`.
  Deferred ones are named in the report.

## Rules
* Never build another stage's shared piece: interpreter discovery is the Python stage's, CMake integration the C stage's, `ITaskProvider`
  the Rust stage's.
* Real-tool tests (`Trait("Category", "RealTools")`): each generator creates a project in a temp directory with the real CLI, then builds it and
  runs its tests (cargo build/test, dotnet build/test, cmake configure + build + ctest, `zig build test`, venv + pytest); each skips with its
  reason when the tool is missing. Steps that download (pytest from PyPI, GoogleTest/Catch2 through FetchContent) run only after the user agrees.
* Ask the user before installing anything; zig is not installed.
* A `LeakAssert` test for the page's view model.

## Threading & isolation (binding)
00-common applies. Tool checks, `dotnet new list` parsing and every generator step run off the UI thread; output reaches the Output channel
(bounded) and the page's status line coalesced; the page never waits on a process.

## Done when
In the real app on this Mac, File ▸ New ▸ Project… and the Welcome page link open the page; for each language whose toolchain is installed a new
project opens, builds, runs its tests, and F5 runs it (wherever that language's debugger is on `main`).
