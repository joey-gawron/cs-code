# C and C++ (stages 3 and 4)

Stage 3 (C, `docs/briefs/lang-3-c.md`) adds the CMake integration, the CTest provider, `textDocument/documentLink`, the C launch templates and
the C debugging matrix. Stage 4 (C++) builds its GoogleTest / Catch2 providers and its matrix on top and adds its sections below.

Stage 3 ran in parallel with stages 1 (Python) and 2 (Rust) instead of after them, so the pieces that build on theirs are **deferred**
(section 8): CMake tasks through Rust's `ITaskProvider`, the clangd extension (Switch Header/Source, inactive regions, View AST, file status,
the review of clangd's default arguments) on Rust's per-server extension pattern, the matrix rows that need the Memory / Disassembly windows
and data breakpoints, and C snippets on Python's bundled-snippets mechanism.

Toolchain on this Mac (2026-11-01): Apple clang / clangd 21.0.0 (`/usr/bin`), CMake 3.31.12 (MacPorts, first on PATH; Homebrew's 4.4.2 is
also installed), Ninja 1.13.2 (MacPorts), Apple lldb-dap (LLVM 21, `xcrun -f lldb-dap`), CodeLLDB v1.12.3 (`.scratch/tools/codelldb`). No
GDB, no cmake-language-server, no bear: nothing was installed for this stage.

## 1. CMake integration (`src/CsCode.Build.CMake`)

A new UI-free project (Core only) because CMake is neither a language server nor a debug adapter: it is a build system that C++ (stage 4)
reads too, through a small Core contract.

**Core contract** (additive, `src/CsCode.Core/Build/CMake.cs`): `ICMakeService` — `Projects` (snapshots of `CMakeProject`: source and build
directory, state, configure / build preset, `Configuration` for `--config` / `ctest -C`, generator, targets, the `ctest` beside the `cmake`
that configured, a `Generation` that grows when the targets change), `ProjectsChanged`, `BuildFinished`, `ProjectFor(path)`,
`ConfigureAsync`, `BuildAsync(project, targets)`. `CMakeTarget`: name, type, artifacts, source directory, dependencies (the project's own
targets), link-line libraries, sources (capped at 2000 per target) and `Executable`.

**Detection.** A workspace folder with `CMakeLists.txt` at its root, or at `cmake.sourceDirectory`. A build directory that already holds a
File API reply is read at once (the project shows as configured without running cmake).

**Configure.** With presets (`CMakePresets.json` / `CMakeUserPresets.json`, `cmake.useCMakePresets` = `auto`): the chosen configure preset,
from a quick pick, remembered per workspace (a single visible preset needs no choice): `cmake -S <src> --preset <name>
-DCMAKE_EXPORT_COMPILE_COMMANDS=ON` (+ `-B <cmake.buildDirectory>` when the preset has no `binaryDir`). The presets are read for the quick
pick and to predict the build directory: `include`, `inherits` (first parent wins), `hidden`, `condition` (all CMake's kinds) and the
`binaryDir` macros (`${sourceDir}`, `${presetName}`, `$env{}`, …). Without presets: `cmake -S <src> -B <cmake.buildDirectory> -G
<cmake.generator, else Ninja when installed> -DCMAKE_BUILD_TYPE=<Debug> -DCMAKE_EXPORT_COMPILE_COMMANDS=ON [-D<cmake.configureSettings>]
[cmake.configureArgs]`; `-G` only for a new build directory (a cache keeps its generator) and `-DCMAKE_MAKE_PROGRAM=<ninja>` when ninja is
not on PATH. Delete Cache and Reconfigure adds `--fresh`.

**Targets** come from the CMake File API: the stateless query `<build>/.cmake/api/v1/query/client-cscode/codemodel-v2` is written before
every configure, and the newest `reply/index-*.json` names the `codemodel-v2` object and its `target-*.json` files. Generator-provided
targets (`ALL_BUILD`, `ZERO_CHECK`) are left out. `CMakeLists.txt` is never parsed.

**Build.** `cmake --build <build> [--config <cfg> for a multi-config generator] [--target …] [--parallel N] [cmake.buildArgs] [-- cmake.buildToolArgs]`,
or `cmake --build --preset <build preset> …`. Clean is `--target clean`. A build of a project that was never configured configures it first.
After a build the reply is read again (cmake re-runs itself when a CMakeLists.txt changed).

**Output and problems.** Everything goes to the **CMake** output channel. Compiler diagnostics (`file:line:col: error|warning|note: …`, what
`$gcc` matches; colour escapes stripped; clang's `[-Wflag]` becomes the code) and cmake's own `CMake Error|Warning at file:line (cmd):`
blocks go to the Error List, per project and per kind (configure / build), each run clearing its last results; relative paths are looked up
in the build directory (Ninja runs compilers there), then the source directory; at most 2000 problems per run.

**clangd** is started with `--compile-commands-dir=<build dir>` of the first configured project that has a `compile_commands.json`: the
integration re-registers clangd's definition with that argument (the language-server manager restarts a running clangd when its launch
changes, and only then; a new directory restarts it once). `cscode.cmake.clangdCompileCommandsDir: false` turns it off. A user override of
`cscode.languageServers.clangd.args` replaces the arguments, so add `--compile-commands-dir` there yourself in that case.

**Configure on open.** `cmake.configureOnOpen` null (the default) asks once per project and session: *Configure* / *Always* / *Never* /
*Not Now*; Always and Never are saved to the user settings. `cmake.configureOnEdit` (default on) configures again when a CMakeLists.txt or
`*.cmake` of a configured project is saved; saving a presets file re-reads the presets.

**Status bar** (left): `$(tools) CMake: [<configure preset | build type>]` (click: choose; a spinner while configuring, a warning after a
failed configure), `$(gear) Build [target]` (click: build; while building `Building …` and a click cancels), `[launch target]` (click:
choose), `$(debug-alt)` (Debug) and `$(play)` (Run in the terminal). The items follow the project of the active editor.

**Threading.** All state lives on the integration's own dedicated lane (`CMakeLane`, a below-normal thread with its own synchronisation
context); every public call only enqueues. cmake and the build run as tracked child processes under `nice -n 10` (which the compilers
inherit), their output batched (every 100 ms or 500 lines) to the channel and, on the lane, to the problem matcher. One operation per project
at a time; later ones wait; Cancel Build kills the process tree.

### Commands (VS Code CMake Tools' ids)

| Command | Id |
|---|---|
| CMake: Configure / Delete Cache and Reconfigure | `cmake.configure` / `cmake.cleanConfigure` |
| CMake: Build / Build All / Build Target… / Set Build Target | `cmake.build` / `cmake.buildAll` / `cmake.buildWithTarget` / `cmake.setDefaultTarget` |
| CMake: Clean / Clean Rebuild / Cancel Build | `cmake.clean` / `cmake.cleanRebuild` / `cmake.stop` |
| CMake: Select Configure Preset / Select Build Preset / Select Build Type | `cmake.selectConfigurePreset` / `cmake.selectBuildPreset` / `cmake.setVariant` |
| CMake: Select Launch Target / Run Without Debugging / Debug | `cmake.selectLaunchTarget` / `cmake.launchTarget` / `cmake.debugTarget` |
| For `${command:…}` in launch.json | `cmake.launchTargetPath` (builds the target first when `cmake.buildBeforeRun`), `cmake.launchTargetDirectory`, `cmake.launchTargetFilename`, `cmake.buildDirectory`, `cmake.buildType` |
| CMake: Show Output | `cmake.showOutput` |

Debug Target starts the launch target under `cscode.cmake.debugger` (null: CodeLLDB `lldb` on macOS, GDB elsewhere; `lldb-dap` on request)
in its artifact's directory, with CodeLLDB told the source language (`c`, or `cpp` when the target has C++ sources) and
`cmake.debugConfig` merged on top (`args`, `env`, `stopOnEntry`, `terminal`…). Run Target starts it in a reused integrated terminal.

### Settings

`cmake.sourceDirectory`, `cmake.buildDirectory` (default `${workspaceFolder}/build`; `${workspaceFolder}`, `${workspaceFolderBasename}`,
`${buildType}`, `${generator}`, `${userHome}`, `${env:X}`), `cmake.generator`, `cmake.configureArgs`, `cmake.configureSettings`,
`cmake.buildArgs`, `cmake.buildToolArgs`, `cmake.parallelJobs`, `cmake.configureOnOpen`, `cmake.configureOnEdit`, `cmake.buildBeforeRun`,
`cmake.useCMakePresets`, `cmake.debugConfig`, `cmake.cmakePath` and `cmake.ctestPath` (machine-scoped: a repository cannot choose them;
looked up on PATH, then Homebrew, MacPorts and CMake.app), `cscode.cmake.clangdCompileCommandsDir`, `cscode.cmake.debugger`.

## 2. CTest in the Test Explorer (`src/CsCode.Testing/Providers/CTest`)

See docs/TESTING.md "CTest": discovery from `ctest --show-only=json-v1` (names, labels, the `add_test` line), runs with `-R '^(a|b)$'`,
`--output-on-failure` and `--output-junit` (CTest 3.21+; older ctest is read from its console output), a build of the tests' targets first,
and debugging a test's command under the C debugger in its `WORKING_DIRECTORY` with its `ENVIRONMENT`. The JSON parser (`CTestJson`) is
shared with stage 4's providers, which skip executables whose tests `gtest_discover_tests` already registered.

## 3. Document links (`textDocument/documentLink`)

Additive Core `IDocumentLinkProvider` + `DocumentLink`; the LSP client advertises `documentLink` (dynamic registration, tooltips) and maps
links and `documentLink/resolve` (`src/CsCode.Lsp/Features/DocumentLinkProvider.cs`). In the editor (`NavigationFeature`), the links of a
document are fetched once per version, when Ctrl (⌘ on macOS) is first held over it; a link under the pointer is underlined with the hand
cursor and opens on Ctrl/⌘+Click (a file in the editor, with VS Code's `#L12` / `#12,5` line fragment; http(s) and mailto in the browser),
resolved first when the provider left the target out. A link wins over a definition on the same text. clangd 21 links every `#include` to the
header it resolves (`RealClangdDocumentLinkTests`).

## 4. Projects without CMake

Not done in this stage (SHOULD): the "Create compile_flags.txt" command, Build and Run Active File, and docs for bear / compiledb. clangd
still works with a hand-written `compile_flags.txt` or `compile_commands.json`; the single-file launch template (section 5) builds the
active file.

## 5. C launch templates

CodeLLDB, lldb-dap and GDB offer, in a folder with a `CMakeLists.txt` and/or `.c` files at its top level, before their generic templates:

* **C: Debug CMake launch target** — `"program": "${command:cmake.launchTargetPath}"`, so VS Code CMake Tools' launch.json files work too.
* **C: Debug active file** — `${fileDirname}/${fileBasenameNoExtension}` with `"preLaunchTask": "C: build active file"`; Create launch.json
  writes that task to tasks.json: a process task running `clang -g -O0 -Wall ${file} -o …` (`cc` on Linux) with `$gcc`.

CodeLLDB's templates carry `"sourceLanguages": ["c"]`.

## 6. C debugging matrix on this Mac

`tests/CsCode.Dap.Tests/RealCDebugMatrixTests.cs` runs every row against both adapters through the real `DebugService` on one C program built
with `cc -g -O0 -pthread` (evidence: `CSCODE_DAP_TEST_METRICS=<file>`). 18/18 pass (CodeLLDB v1.12.3, Apple lldb-dap / LLVM 21, macOS 27 arm64,
Developer Mode on).

| Row | CodeLLDB (`lldb`) | lldb-dap | Evidence |
|---|---|---|---|
| Launch, program output, clean exit | ✅ exit code 7, ~0.5–0.9 s to exit | ✅ exit code 7, ~0.4 s | output `total 30 3 3 cscode 42` in the Debug Console |
| Line breakpoint | ✅ | ✅ | stops at `square`, `n = 0` |
| Conditional breakpoint (`i == 3`) | ✅ verified, "Resolved locations: 1" | ✅ verified | stops with `i = 3` |
| Hit count (`2`) | ✅ second hit | ✅ second hit | set while `i = 3`: stops in `square` with `n = 4` |
| Logpoint (`square of {n}`) | ✅ no stop | ✅ no stop | `square of 0` … `square of 4` in the console |
| Function breakpoint (`square`) | ✅ | ✅ | stops at the first statement of `square` (line 10) |
| Step into / out / over | ✅ | ✅ | into `square` (stack `square:10 \| main:29`), out to the loop, over to line 30 |
| Struct | ✅ `{x:3, y:4}`, children x, y | ✅ `point @ 0x…`, children x, y | |
| Array | ✅ `{1, 2, 3, 4}` | ✅ `int[4] @ 0x…`, children 1–4 | |
| Pointer | ✅ value `3`, expands to `*ptr = 3` | ✅ value `0x…`, expands to `*ptr = 3` | `*ptr` evaluates to 3 in both |
| Union | ✅ `{i:42, f:5.88545355E-44}` | ✅ `number @ 0x…`, children i, f | |
| `char *` string | ✅ `"cscode"` | ✅ `0x… "cscode"` | |
| SIGSEGV | ✅ exception stop | ✅ exception stop | reason `exception`, `EXC_BAD_ACCESS (code=1, address=0x0)`, at the faulting line |
| abort() | ✅ exception stop | ✅ exception stop | reason `exception`, `signal SIGABRT`; `main` at the `abort()` line in the stack under `__pthread_kill` / `pthread_kill` / `abort` (CodeLLDB shows system frames without a file) |
| pthreads | ✅ 3 threads listed at once | ✅ after a fix (below) | breakpoint in `worker` stops that thread; `id` is 1 or 2 |
| stdin through the integrated terminal | see §7 | see §7 | |
| Memory window from a pointer, Disassembly, data breakpoints | deferred (stage 2's windows) | deferred | |

**Fixed:** Apple's lldb-dap answers the first `threads` request after a stop with the list it had while the program ran, so a pthread created
since (the one that stopped) was missing from the Threads window; 200 ms later the answer is right. `DebugSession` now asks again (up to 5
times, 100 ms apart) while the thread that stopped is not in the answer (`StaleThreadsTests`). Also fixed: `RealLldbDapTests`' C test
continued within the breakpoint sync's 50 ms debounce after removing its breakpoint and hit it again; it now waits like a user would.

**Attach by pid on macOS.** Attaching needs the debugger to be allowed to take another process's task port: Developer Mode on
(`DevToolsSecurity -status`; `sudo DevToolsSecurity -enable`, and the user in the `_developer` group), and the target not hardened: a program
built by the user (`cc -g`) attaches; Apple-signed and hardened-runtime binaries without the `com.apple.security.get-task-allow` entitlement
do not (SIP). The templates offer `"pid": "${command:pickProcess}"` attach configurations for CodeLLDB and lldb-dap; with Developer Mode off,
macOS asks for an administrator in a dialog first.

## 7. Seen in the real app

**Not yet.** On 2026-11-01 the Mac's screen was locked (`ioreg -n Root -d1` reports `CGSSessionScreenIsLocked=Yes`), and CsCode cannot start
without an active display: Avalonia.Native fails to create its render timer (`CVDisplayLink`, CoreVideo error -6661) and the app exits with a
crash report. Everything above is proven by the tests in section 9 against the real cmake, ninja, ctest, clangd, CodeLLDB and lldb-dap; the
window checks below remain to be done:

1. Open a CMake C project with one visible configure preset (with `cmake.configureOnOpen: true`, or answer *Configure*): the status bar shows
   `CMake: [debug]`, `build/<preset>/compile_commands.json` appears, and clangd resolves `#include "mathlib.h"` (no diagnostic; hover works).
2. Click `Build` in the status bar: the CMake output channel shows the build; a deliberate error lands in the Error List at its line.
3. Choose the launch target, click the bug button: the debugger stops at a breakpoint in `main.c` and the Locals show values.
4. Test Explorer: the CTest tests appear (with `subtracts [fast, math]`), Run All shows `fails` failed with its output, Debug on a test stops
   at a breakpoint in `tests/math_tests.c`.
5. ⌘+hover over `#include "mathlib.h"` underlines it; ⌘+Click opens `include/mathlib.h`.
6. With `"terminal": "integrated"` in `cmake.debugConfig`, a program reading stdin gets the keys typed in the integrated terminal.

## 8. Deferred (waits for other stages)

| Item | Waits for | Note |
|---|---|---|
| CMake tasks through `ITaskProvider` (`cmake: build`, … with `$gcc`) | stage 2 (Rust) | `ICMakeService.BuildAsync` is what the provider will call |
| clangd extension: Switch Header/Source, inactive `#if 0` regions, View AST, file status, default-argument review | stage 2's per-server extension pattern | MUST 2 |
| Matrix rows: Memory window from a pointer, Disassembly, data breakpoints | stage 2's windows | |
| C snippets | stage 1 (Python)'s bundled snippets | MUST 7 |
| cmake-language-server, bear | the user's go-ahead | not installed; nothing asked yet |

## 9. Tests

```
dotnet test tests/CsCode.Build.CMake.Tests                                   # presets, File API, output parser, service, host; real cmake + ninja
dotnet test tests/CsCode.Panels.Testing.Tests --filter CTestProviderTests     # CTest JSON / JUnit / debug config; real ctest
dotnet test tests/CsCode.Lsp.Tests --filter RealClangdDocumentLinkTests       # documentLink against the real clangd
dotnet test tests/CsCode.Editor.Tests --filter DocumentLinkTests              # Ctrl/⌘ hover and click
dotnet test tests/CsCode.Dap.Tests --filter "CLaunchTemplateTests|StaleThreadsTests|RealCDebugMatrixTests"
```

The real-tool tests (`Category=RealTools`, `RealAdapter`) skip with their reason when cmake, ninja, a C compiler, clangd, CodeLLDB
(`tools/fetch-codelldb.sh`), lldb-dap or macOS Developer Mode is missing. The shared C sample is `tests/CsCode.TestDoubles/CMakeCSample.cs`.
