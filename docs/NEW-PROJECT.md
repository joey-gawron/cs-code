# New Project page

File ▸ New ▸ Project… opens a page modelled on IntelliJ's New Project dialog and Visual Studio 2022's "Create a new project": choose a language
on the left, a name and a folder on the right, the language's options, and Create. The project it makes is a good start in CsCode: it builds,
its tests run, and **F5** debugs it at once (its `.vscode/launch.json` and `tasks.json` are written for CsCode's own debug adapters). Brief:
[docs/briefs/new-project.md](briefs/new-project.md).

| Piece | Where | References |
|---|---|---|
| Contract: `IProjectGenerator`, `ProjectOption`, `ProjectRequest`, `ProjectResult`, `ProjectToolCheck`, `IProjectGenerationProgress` | `src/CsCode.Core/Projects/ProjectGenerators.cs` (additive) | — |
| Generators, the creation steps (folder checks, Git, clean-up), the tool runner, name rules | `src/CsCode.ProjectTemplates` | Core only |
| The page: view, view model, command, setting | `src/CsCode.Panels.NewProject` | Core, Ui, ProjectTemplates |
| Shell wiring: File ▸ New submenu, Welcome link, composition root, closing a document view, New Window with paths | `src/CsCode.App` | — |

```csharp
services.AddCsCodeNewProject();   // the page, its host (command + setting) and AddCsCodeProjectTemplates() (AppServices does this)
```

Another module adds a generator by registering one more `IProjectGenerator`; the page lists every registered one by `Order`.

## Opening the page

| Where | What |
|---|---|
| File ▸ New ▸ Project… | `cscode.file.newProject` (File ▸ New ▸ File is the old File ▸ New File) |
| Welcome page | "Create a new project", first link of Start |
| Command palette | "File: New Project..." |

It is a document page (a singleton tab), as Settings and Keyboard Shortcuts are: CsCode has no modal form windows, and Visual Studio's own
New Project is a full page. Opening it again brings the open one to the front.

## The page

* **Left:** the generators, each with its icon, name and one line. Each one's tool is checked off the UI thread when the page opens
  (`--version`, 30 s at most). A generator whose tool is missing stays listed but disabled, its second line saying what is missing and how
  to install it on this platform (the style of the language servers' install hints: "Install it with: brew install zig"); its tooltip shows the
  hint too. The selected generator's tool line ("cargo 1.96.1 (…)") shows under the options.
* **Right:** Name, Location with **Browse…** (the folder picker), "Project will be created in …", the generator's options (drop-downs and check
  boxes, each with its description), **Create Git repository**, then Create and Cancel with the status line and a progress bar.
* **Validation:** the name follows the generator's toolchain (below); the target folder must not exist or be empty; Location must be
  absolute (`~` is the home folder). The folder check runs off the UI thread; Create is enabled only when everything holds.
* **Remembered:** the last generator, every generator's options and the Git box (state key `cscode.newProject.last`, application scope),
  saved when Create is pressed.
* **Default location:** `cscode.newProject.defaultLocation` when set (`~` expanded), else the parent of the most recently opened folder, else
  the home folder. The proposed name is `untitled`, `untitled1`, … whichever is free there.
* **Keyboard:** the page opens with the keyboard in Name, its text selected. Tab moves through the page; in the list the arrows choose a generator
  (disabled ones are skipped); access keys: Alt+N and Alt+L move the keyboard to Name and Location, Alt+B browses, Alt+G toggles Create Git
  repository, Alt+C creates (Alt+L and Alt+G are tested); **Enter** creates (unless a button or an open drop-down takes it); **Esc** cancels a
  running creation, or closes the page when nothing runs.

## Generators

Every flag was checked against the tool installed on this Mac on 2026-11-01 (versions in the table); a generator whose tool was not installed
says so.

| Generator (id) | Tool | What runs | Options | Name rule |
|---|---|---|---|---|
| Empty Project (`empty`) | — | nothing | — | a portable folder name |
| Python (`python`) | python3 3.14 | files written by CsCode, then `python -m venv .venv` (`uv venv .venv` when uv is installed), then `.venv/bin/python -m pip install pytest` | Application / Package; Create a virtual environment; pytest with `tests/` | PEP 508 project name whose package (normalised: `-` `_` `.` → `_`, lower case) is an identifier, not a keyword, not `test(s)` |
| Rust (`rust`) | cargo 1.96.1 | `cargo init --bin\|--lib --edition E --vcs V --name N <dir>` (`init`: the page has made the folder) | Binary / Library; edition 2024, 2021, 2018, 2015; version control git, hg, pijul, fossil, none | cargo's: ASCII letters, digits, `-`, `_`, no leading digit, no keyword, not `test`/`std`/`core`/`alloc`/`proc_macro`/`build`/`deps`/… |
| C (`cmake-c`), C++ (`cmake-cpp`) | CMake 4.4.2, Apple clang 21, Ninja 1.13 | files written by CsCode | C11 / C17 / C23 or C++17 / 20 / 23; Executable / Library; tests: CTest (C), GoogleTest / Catch2 / CTest only / none (C++) | letters, digits, `-`, `_`; not a target CMake reserves (`test`, `all`, `clean`, `install`, …) |
| C# (`dotnet`) | .NET SDK 10.0.403 | `dotnet new sln --name N [--format slnx\|sln]`, `dotnet new <template> --name N --output src/N [--framework F]`, `dotnet sln add`, optionally `dotnet new xunit` in `tests/N.Tests` + `dotnet add … reference` + `dotnet sln add`, `dotnet new gitignore` | template (from `dotnet new list --type project --language C#`, console / classlib / webapi / xunit first); target framework (from the template's `--help`); solution format (when the SDK has `--format`); xUnit test project | dot-separated C# identifiers, no keyword |
| Zig (`zig`) | zig 0.17.0 (Homebrew) | `zig init` in the folder (it takes the folder's name; its only flag is `--minimal`) | — | an identifier of 32 bytes at most that is not a keyword, not a primitive (`u8`, `i32`, `usize`, `bool`, `true`, `null`, …), not `_`, `std`, `builtin` or `root`, and not a name zig init's `src/main.zig` declares (`main`, `io`, `Io`, `init`, `args`, `list`, `context`, …) |
| TypeScript (Node.js) (`node-ts`) | node 24, npm 11 | `npm init -y`, package.json completed, `npm install --save-dev typescript @types/node`, `tsc --init --rootDir src --outDir dist --sourceMap --types node` | — | npm's: lower case, digits, `-` `.` `_` `~`, no leading `.`/`_` |
| JavaScript (Node.js) (`node-js`) | node 24, npm 11 | `npm init -y`, package.json completed (ES module, `start` and `test` scripts) | — | as TypeScript |
| Static Website (`static-site`) | — | `index.html`, `styles.css`, `script.js` | — | a portable folder name |

Details:

* **Python:** `pyproject.toml` with a src layout (`src/<package>/__init__.py`, and `__main__.py` for an application, also as a console script),
  hatchling as build backend, `[dependency-groups] dev` (pytest, ruff), **Ruff** configured (`[tool.ruff]`: line length 100, `src`; lint rules
  E F W I B UP; double quotes) and pytest configured with `pythonpath = ["src"]`, so the tests import the package without installing it. pytest comes
  from PyPI: offline, the project is still made and a warning says how to install it later. `requires-python` is the interpreter's version.
* **C / C++:** `CMakeLists.txt` (`cmake_minimum_required(VERSION 3.21)`, the standard required and without extensions, compile_commands.json
  exported), `CMakePresets.json` (version 3: `debug` and `release` configure, build and test presets in `build/debug` and `build/release`; the Ninja
  generator when Ninja was installed at creation), a small library (`src/<name>.c|cpp` with its header in `include/`) that `src/main.c|cpp` uses,
  tests in `tests/`, `.clang-format` (LLVM, 4 spaces, 100 columns) and a `.clangd` naming `build/debug` as the compilation database. GoogleTest
  `v1.17.0` and Catch2 `v3.8.1` come through `FetchContent` (git, shallow) at the first configure, with `gtest_discover_tests` / `catch_discover_tests`.
* **C#:** templates are listed with `dotnet new list --type project --language C#` and parsed by the dash line's column widths; the target
  frameworks come from `dotnet new console --help` (`Type: choice` block); both are read off the UI thread and cached for 5 minutes. A template
  without `--framework` is not given one (its `--help` is checked first). CsCode owns this listing.
* **Zig:** `zig init` never refuses a folder name: it rewrites it (`hello-zig` → `hello_zig`, `fn` → `foo`, `9lives` → `_9lives`, longer than 32
  bytes → cut), so the executable would not be the `zig-out/bin/<name>` that launch.json runs; and with a name it keeps, its `src/main.zig`
  (`const <name> = @import("<name>")`) does not build when the name is a primitive, a built-in module or one of that file's own names. The name
  rule refuses all of these up front (each checked with zig 0.17.0: `zig init` then `zig build`).

## A good start

Every generator writes `.gitignore` (the language's lines and the usual OS/editor files; a tool's own `.gitignore` is completed, not replaced),
`README.md` (how to build, run and test from CsCode and from a shell) and `.editorconfig` (UTF-8, LF, final newline, 4 spaces; 2 for JSON/YAML/TOML;
the language's sections). With **Create Git repository**, `git init` (unless the tool made the repository: cargo's `--vcs git`), `git add -A` and
a first commit "Initial commit"; a missing git, or a commit git refuses (no `user.name`/`user.email`), is a warning, not a failure. For Rust, the
Git box and the *Version control* option stay in step (unchecking the box chooses `none`, choosing `hg` unchecks it).

`.vscode/launch.json` and `tasks.json`, checked against the adapter factories in `src/CsCode.Dap/Adapters`:

| Generator | F5 configuration | Build task (Ctrl+Shift+B) | Test task |
|---|---|---|---|
| Python | `debugpy`, `"module": "<package>"`, `PYTHONPATH=${workspaceFolder}/src`, `"python"`: the venv's interpreter; also pytest and Current File; `.vscode/settings.json` has `python.defaultInterpreterPath` and pytest's keys | — | `pytest` (venv's `python -m pytest`) |
| Rust binary | CodeLLDB `lldb`, `program: target/debug/<name>`, `preLaunchTask: "cargo: build"`, `sourceLanguages: ["rust"]` (CsCode's own Rust template) | `cargo: build` (`$rustc`) | `cargo: test` |
| Rust library | CodeLLDB `lldb` with the `cargo` key (`test --no-run`, filter `kind: lib`), which the factory resolves | `cargo: build` | `cargo: test` |
| C / C++ | CodeLLDB `lldb`, `program: build/debug/<name>` (and the test program), `preLaunchTask: "cmake: build (debug)"`, `sourceLanguages` c / cpp | `cmake: build (debug)` (`$gcc`, after `cmake: configure (debug)`) | `ctest (debug)` |
| C# | netcoredbg `coreclr`, `program: src/<N>/bin/Debug/<tfm>/<N>.dll`, `cwd` the project folder, `preLaunchTask: "build"`, `ASPNETCORE_ENVIRONMENT=Development` for web projects; attach; none for a library or test template | `build` (`dotnet build <sln>`, `$msCompile`) | `test` |
| Zig | CodeLLDB `lldb`, `program: zig-out/bin/<name>`, `preLaunchTask: "zig: build"`, no `sourceLanguages` (CodeLLDB lists no `zig` yet) | `zig: build` (`$gcc`) | `zig: test` |
| TypeScript | js-debug `pwa-node`, `program: src/index.ts`, `preLaunchTask: "tsc: build"`, `outFiles: dist/**/*.js` (CsCode's own TypeScript template) | `tsc: build` (`$tsc`) | `npm: test` |
| JavaScript | js-debug `pwa-node`, `program: src/index.js` | — | `npm: test` (`node --test`) |

## Running a generator

`ProjectCreator.CreateAsync` (on the pool; the page only waits for its result) checks the folder again, creates it, runs the generator, then the
Git step. Tools run through `ToolRunner`: off the UI thread, after the login shell's environment is merged (`LoginShellEnvironment.WaitAsync`),
standard input closed, `DOTNET_NOLOGO`, `NO_COLOR` and `GIT_TERMINAL_PROMPT=0` set, tracked by the memory monitor while they run
(`IMemoryMonitor.TrackChildProcess`, "new project: cargo"). Each step goes to the status line (latest wins) and, with every line of the tools'
output, to the **New Project** Output channel. **Cancel** kills the running tool's whole process tree.

On a failure or a cancel, what this run created is removed, and only when the project folder did not exist before: the folder and any parents
it had to create (a parent only while empty). An empty folder the user chose is left as it is ("nothing was removed" in the output). On a failure
the page shows the error in red and brings the Output window to the front.

## After creation

* The window has **no folder**: the project opens in it, then its main file (`src/main.rs`, `src/main.c`, `Program.cs`, `src/<pkg>/__main__.py`,
  `src/index.ts`, …), and the page's tab closes.
* The window **has a folder**: CsCode asks *Open in This Window*, *Open in New Window* or *Don't Open*. *New Window* is offered where File ▸ New
  Window exists (session processes, docs/SESSIONS.md): it now takes absolute paths as arguments, so the new session opens the folder and the main file.
  A `--foreground` run has one window and offers *Open in This Window* / *Don't Open*. *Don't Open* leaves the page with "Created …".

## Settings, state, commands

| Key | Kind | Default | Meaning |
|---|---|---|---|
| `cscode.newProject.defaultLocation` | setting | `""` | the proposed Location; empty: parent of the most recent folder, else home |
| `cscode.newProject.last` | state (application) | — | last generator, each generator's options, the Git box |
| `cscode.file.newProject` | command | — | opens the page ("File: New Project...") |

## Core and shell changes (additive)

* `src/CsCode.Core/Projects/ProjectGenerators.cs` (new).
* `IViewActivator.CloseViewAsync(viewId)` with a default that closes nothing; `ShellViewActivator` closes the document view's tab.
* `workbench.action.newWindow` takes absolute paths (folder, then files) and passes them to the new session's `LaunchRequest.Targets`.
* File ▸ New is a submenu (Project…, File); the Welcome page's Start list begins with "Create a new project".

## Deferred (owned by stages not on `main` yet)

* **Select the new `.venv`** with the Python stage's interpreter service: not on main. Until then the project writes VS Code's
  `python.defaultInterpreterPath` into `.vscode/settings.json`, which that stage is expected to read.
* **CMake configure** through the C stage's CMake integration (`ICMakeService.ConfigureAsync`, on branch `claude/lang-c-ba48e8`): not on main. The
  first F5 or Ctrl+Shift+B configures through the `cmake: configure (debug)` task.
* **`dotnet restore`**: needs no hook: `dotnet new` restores each project as it creates it (the templates' default), so the first build is offline.
* **Python F5:** `debugpy` arrives with the Python stage; the configuration is written now.
* **uv** is not installed on this Mac: the `uv venv` / `uv pip install` path is untested.

## Limits

* No "sample code" check box (SHOULD): every generator writes a small greeting program and one test.
* TypeScript needs the npm registry at creation (offline it fails, and the folder is removed); Python's pytest and C++'s GoogleTest/Catch2 also come
  from the network, the former at creation, the latter at the first configure.
* Template options other than the target framework (`--aot`, `--use-program-main`, authentication…) are not offered.
* The check "in the real app" was made with the real composition in a headless window (below), not a visible window: the agent session that
  built this had no display access (Avalonia.Native fails to start its render timer, CoreVideo error -6661, before any CsCode code runs) and no
  computer use. A person should still open File ▸ New ▸ Project… once in the windowed app.

## Evidence (this Mac, 2026-11-01)

| Generator | Created with the real tool | Built and tested with its tasks' commands | Opened in the real app with its main file | F5 in the real app |
|---|---|---|---|---|
| Rust (bin, lib) | yes | `cargo build`, `cargo test`; the binary prints Hello, world! | yes | CodeLLDB stops at `main.rs:2` |
| C (C11 exe, C17 lib, C23 exe) | yes | `cmake --preset debug`, `cmake --build --preset debug`, `ctest --preset debug` (100 %) | yes | CodeLLDB stops at `main.c:7` |
| C++ (17 exe, 20 lib, 23 exe; GoogleTest; Catch2) | yes | as C; GoogleTest and Catch2 tests discovered and passing | yes | CodeLLDB stops at `main.cpp:6` |
| C# (console + xUnit, classlib in .sln) | yes | `dotnet build`, `dotnet test`; the program prints Hello, World! | yes | netcoredbg stops at `Program.cs:1` |
| JavaScript | yes | `node --test` (1 pass); `node src/index.js` | yes | js-debug stops at `index.js:3` |
| Python (app with .venv) | yes | `python -m hello_py` with launch.json's PYTHONPATH; pytest only with downloads allowed | yes | not on main (debugpy) |
| Empty, Static Website | yes | — | yes | — |
| TypeScript | needs the npm registry: not run | — | — | — |
| Zig (zig 0.17.0, installed 2026-11-02) | yes | `zig build`, `zig build test` | yes | CodeLLDB stops at `main.zig:8` |

Screenshots of the page (Rust and C# chosen) and of the Welcome link come from `RealApp/NewProjectTests.Screenshots_of_the_page` into
`.scratch/screenshots/new-project-*.png` (not committed).

## Tests

| Project | What | Command |
|---|---|---|
| `tests/CsCode.ProjectTemplates.Tests` | name rules, `dotnet new` parsers, creation and clean-up, Git; **real tools** (`Category=RealTools`): every generator makes a project with the real CLI, which is then built and tested with the commands its tasks use, and the program its launch.json names is run | `dotnet test tests/CsCode.ProjectTemplates.Tests` |
| `tests/CsCode.Panels.NewProject.Tests` | the view model (generators, validation, options and the VCS link, remembering, create/fail/cancel, opening here or in a new window) and the real view headless (keyboard only: Name focused, typing, Enter, arrows, Esc, Alt+L, Alt+G), `LeakAssert` for the view model and the view | `dotnet test tests/CsCode.Panels.NewProject.Tests` |
| `tests/CsCode.App.Tests` (`RealApp/NewProjectTests`) | the real application (every module, `CSCODE_HOME` in a temp dir, headless window): the menu, the palette command and the Welcome link open the page; per installed toolchain a project made on the page opens with its main file and F5 builds it and stops at a breakpoint in it | `dotnet test tests/CsCode.App.Tests --blame-hang-timeout 12m --filter FullyQualifiedName~RealApp.NewProjectTests` |

Results on this Mac: ProjectTemplates 105 passed, 2 skipped (pytest and TypeScript downloads); Panels.NewProject 21 passed; RealApp
NewProjectTests 12 passed, Zig's F5 among them (on 2026-11-01, with the menu, Welcome and session App tests: 54 passed). Real-tool tests skip with their reason when the tool is missing. Downloads only with the user's go-ahead: pytest and TypeScript run with
`CSCODE_TEST_DOWNLOADS=1`; the C++ framework tests use the GoogleTest/Catch2 archives the C++ stage fetched into `.scratch/tools/cpp-test-frameworks`
(`FETCHCONTENT_SOURCE_DIR_*`), and otherwise need the same variable. The F5 cases use CodeLLDB, netcoredbg and js-debug from `.scratch/tools`.
