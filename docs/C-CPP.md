# C and C++ (stages 3 and 4)

Stage 3 (C, `docs/briefs/lang-3-c.md`) adds the CMake integration, the CTest provider, `textDocument/documentLink`, the C launch templates and
the C debugging matrix. Stage 4 (C++, `docs/briefs/lang-4-cpp.md`) adds the GoogleTest / Catch2 / doctest
provider, clangd for Objective-C++ and CUDA and the C++ debugging matrix in sections 10–16. The two stages ran at the same time on separate
branches and were merged in that order.

Stage 3 ran in parallel with stages 1 (Python) and 2 (Rust) instead of after them, so the pieces that build on theirs are **deferred**
(section 8): CMake tasks through Rust's `ITaskProvider`, the clangd extension (Switch Header/Source, inactive regions, View AST, file status,
the review of clangd's default arguments) on Rust's per-server extension pattern, the matrix rows that need the Memory / Disassembly windows
and data breakpoints, and C snippets on Python's bundled-snippets mechanism.

Toolchain on this Mac (2026-11-01): Apple clang / clangd 21.0.0 (`/usr/bin`), CMake 3.31.12 (MacPorts, first on PATH; Homebrew's 4.4.2 is
also installed), Ninja 1.13.2 (MacPorts), Apple lldb-dap (LLVM 21, `xcrun -f lldb-dap`), CodeLLDB v1.12.3 (`.scratch/tools/codelldb`). No
GDB, no cmake-language-server, no bear: nothing was installed for this stage. For stage 4, GoogleTest 1.17.0, Catch2 3.8.1 and doctest 2.4.12
were fetched with the user's go-ahead by `tools/fetch-cpp-test-frameworks.sh` (pinned GitHub source archives, SHA-256 checked, built and
installed into `.scratch/tools/cpp-test-frameworks/`).

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
with `cc -g -O0 -pthread` (evidence: `CSCODE_DAP_TEST_METRICS=<file>`). 20/20 pass (CodeLLDB v1.12.3, Apple lldb-dap / LLVM 21, macOS 27 arm64,
Developer Mode on). Line numbers below are the sample's (`RealCDebugMatrixTests.Program`).

| Row | CodeLLDB (`lldb`) | lldb-dap | Evidence |
|---|---|---|---|
| Launch, program output, clean exit | ✅ exit code 7, ~0.5–0.9 s to exit | ✅ exit code 7, ~0.4 s | output `total 30 3 3 cscode 42` in the Debug Console |
| Line breakpoint | ✅ | ✅ | stops at `square`, `n = 0` |
| Conditional breakpoint (`i == 3`) | ✅ verified, "Resolved locations: 1" | ✅ verified | stops with `i = 3` |
| Hit count (`2`) | ✅ second hit | ✅ second hit | set while `i = 3`: stops in `square` with `n = 4` |
| Logpoint (`square of {n}`) | ✅ no stop | ✅ no stop | `square of 0` … `square of 4` in the console |
| Function breakpoint (`square`) | ✅ | ✅ | stops at the first statement of `square` (line 11) |
| Step into / out / over | ✅ | ✅ | into `square` (stack `square:11 \| main:36`), out to the loop, over to line 37 |
| Struct | ✅ `{x:3, y:4}`, children x, y | ✅ `point @ 0x…`, children x, y | |
| Array | ✅ `{1, 2, 3, 4}` | ✅ `int[4] @ 0x…`, children 1–4 | |
| Pointer | ✅ value `3`, expands to `*ptr = 3` | ✅ value `0x…`, expands to `*ptr = 3` | `*ptr` evaluates to 3 in both |
| Union | ✅ `{i:42, f:5.88545355E-44}` | ✅ `number @ 0x…`, children i, f | |
| `char *` string | ✅ `"cscode"` | ✅ `0x… "cscode"` | |
| SIGSEGV | ✅ exception stop | ✅ exception stop | reason `exception`, `EXC_BAD_ACCESS (code=1, address=0x0)`, at the faulting line |
| abort() | ✅ exception stop | ✅ exception stop | reason `exception`, `signal SIGABRT`; `main` at the `abort()` line in the stack under `__pthread_kill` / `pthread_kill` / `abort` (CodeLLDB shows system frames without a file) |
| pthreads | ✅ 3 threads listed at once | ✅ after a fix (below) | breakpoint in `worker` stops that thread; `id` is 1 or 2 |
| Attach by pid (`"pid"` as text, as `${command:pickProcess}` gives it) | ✅ first stop ~0.5 s | ✅ first stop ~0.3 s | a running program stops at a breakpoint in `tick` with no password dialog; ending the session detaches and the program runs on |
| stdin through the integrated terminal | see §7 | see §7 | |
| Memory window from a pointer, Disassembly, data breakpoints | deferred (stage 2's windows) | deferred | |

**Fixed:** Apple's lldb-dap answers the first `threads` request after a stop with the list it had while the program ran, so a pthread created
since (the one that stopped) was missing from the Threads window; 200 ms later the answer is right. `DebugSession` now asks again (up to 5
times, 100 ms apart) while the thread that stopped is not in the answer (`StaleThreadsTests`). Also fixed: `RealLldbDapTests`' C test
continued within the breakpoint sync's 50 ms debounce after removing its breakpoint and hit it again; it now waits like a user would.

**Attach by pid on macOS** works with both adapters (row above) with Developer Mode on (`DevToolsSecurity -status`; `sudo DevToolsSecurity
-enable`), which this Mac has: no administrator dialog appears. With it off, macOS asks for an administrator in a dialog before the debugger
may take the process's task port. Only processes that allow it can be attached: a program the user built (`cc -g`) can; Apple-signed and
hardened-runtime binaries without the `com.apple.security.get-task-allow` entitlement cannot (System Integrity Protection). CodeLLDB's and
lldb-dap's generic templates include an "Attach to process" configuration with `"pid": "${command:pickProcess}"`.

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

## 8. Deferred (waited for other stages)

Stages 1 (Python) and 2 (Rust) are merged into this branch, so the first four rows are open work now rather than blocked.

| Item | Builds on | Note |
|---|---|---|
| CMake tasks through `ITaskProvider` (`cmake: build`, … with `$gcc`) | `Core/Tasks/ITaskProvider.cs`; `Dap/Tasks/CargoTaskProvider.cs` is the example | `ICMakeService.BuildAsync` is what the provider will call |
| clangd extension: Switch Header/Source, inactive `#if 0` regions, View AST, file status, default-argument review | `Lsp/Extensions/LanguageServerExtension` (docs/LSP.md §7a) | MUST 2 |
| Matrix rows: Memory window from a pointer, Disassembly, data breakpoints | `Core/Debugging/DebugNative.cs` and the `debug.memory` / `debug.disassembly` windows | breakpoints at the physical path (`TempDirectory.PhysicalPath`) |
| C snippets | `Editor/Snippets/Builtin/<language id>.json` | MUST 7 |
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

## 10. C++ tests in the Test Explorer (`src/CsCode.Testing/Providers/Cpp`)

GoogleTest, Catch2 (v3; v2 without places) and doctest executables, in the Test Explorer with the other providers (docs/TESTING.md): listed with
each test's file and line, run with results and failure locations (Error List, editor glyphs), debugged under CodeLLDB. Provider id `cpp`.

**How to use it.** Build the tests (with CMake, `cmake --build build`, or anything else), then show the Test Explorer (or press Refresh after a
rebuild). Executables named like tests under `build/` or `out/` are found with no setting; elsewhere, name them in
`cscode.testing.cpp.executables` (`.vscode/settings.json`), for example `["build-*/tests/*_test", "bin/unit"]`. A workspace already set up for
VS Code's C++ TestMate works as it is: its `testMate.cpp.test.executables` glob is read when ours is not set.

### Finding the executables

* **Globs** (`cscode.testing.cpp.executables`, a list; else TestMate's `testMate.cpp.test.executables`; else TestMate's default
  `{build,Build,BUILD,out,Out,OUT}/**/*{test,Test,TEST}*`) are relative to the workspace folder (`${workspaceFolder}/` and `./` allowed) or
  absolute, matched with VS Code's glob rules against the whole relative path. Only the leading literal directories are walked (a brace of
  literals such as `{build,out}` names the directories that exist exactly so; a case-insensitive volume does not walk `build` twice), at most
  16 levels and 200,000 entries, never through links, `.git`, `node_modules`, `CMakeFiles`, `_deps`, `vcpkg_installed`, `.cache` or `Testing`.
* **An executable** is a file executable by its owner whose first bytes are a Mach-O `MH_EXECUTE`, a universal binary, an ELF executable or PIE,
  or a PE image (`.exe` on Windows); shared libraries, objects and scripts are not.
* **Its framework** comes from a signature in its bytes (read in 4 MB blocks, at most 1 GB): `gtest_list_tests` or the mangled
  `testing::InitGoogleTest` (GoogleTest, also when it is linked as a shared library), `Catch2TestRun` (Catch2 v3's XML reporter) or
  `list-test-names-only` (v2), `list-test-cases` (doctest). **Executables are never run to be classified**: only those that carry a signature are
  ever started. The classification is cached by path, size and modification time (4,096 entries).
* Each executable is one **project** in the explorer, named by its path in the folder (`build/math_tests`) with its framework beside it.
* **Beside CTest** (section 2): an executable whose tests CTest already lists one by one is left to the CTest provider, so no test is listed
  twice. For each CMake project of the folder built with CTest (`CTestTestfile.cmake` in its build directory), detection reads
  `ctest --show-only=json-v1` (`CTestJson`) and skips the executables a CTest test runs with a single test's filter (`--gtest_filter=` from
  `gtest_discover_tests` or `gtest_add_tests`, `--test-case=` from `doctest_discover_tests`) or with arguments from a test that has no
  `add_test` line (the discovery modules, `catch_discover_tests` among them, add theirs from a generated file when ctest runs). An executable
  CTest runs whole (`add_test(NAME all COMMAND math_tests)`) stays here, with its tests one by one. Paths are compared resolved (`/tmp` and
  `/private/tmp` are one). When the CTest provider is turned off, every executable is listed here.

### Listing (discovery)

| Framework | Command | Places |
|---|---|---|
| GoogleTest | `--gtest_list_tests --gtest_output=json:<tmp>/list.json` | file and line of every test (GoogleTest 1.10+); older: the printed list, names only |
| Catch2 v3 | `--list-tests --reporter xml "*,[.]"` | `SourceInfo/File`, `/Line` |
| Catch2 v2 | `--list-test-names-only "*,[.]"` (v2 lists as text whatever the reporter) | none until a run |
| doctest | `--list-test-cases --reporters=xml --no-skip=true` | `filename`, `line` |

* Listings run in the executable's directory, off the UI thread, bounded: **20 s** and **16 MB** of output, after which the executable is killed
  and the project shows why. The result is **cached by the executable's size and modification time**: an executable is listed again only after
  it was rebuilt (Refresh, a folder change or a test command re-checks; an unchanged one costs a `stat`). Listing never runs while typing: the
  editor's glyphs come from a text scan (below) and only the first one starts discovery, as for the other providers.
* **Ids**: `<executable name>/<the runner's name>`: `math_tests/MathTest.Adds`, `math_tests/Numbers/Param.IsPositive/1`,
  `catch_tests/adds numbers`, `doc_tests/suite::in suite`; the same test name in two executables stays two tests.
* **Tree**: GoogleTest groups by test suite (`MathTest`, `Numbers/Param`, `Typed/0`); a parameterised instance shows its parameter
  (`IsPositive/1 (GetParam() = -3)`, `DefaultIsZero (TypeParam = long)`). Catch2 groups by source file and shows each test's tags after its name
  (`fails  [math][slow]`), so the explorer's filter finds tags (`[slow]`). doctest groups by `TEST_SUITE`, else by file.
* **Not run by default**: GoogleTest's `DISABLED_` tests, Catch2's hidden tests (`[.]`, `[.tag]`, `[!hide]`) and doctest's `doctest::skip()`
  tests are listed with that reason; each runs when it is chosen by itself (`--gtest_also_run_disabled_tests`, Catch2's name spec, doctest's
  `--no-skip=true`).

### Running

| Framework | Arguments | Results |
|---|---|---|
| GoogleTest | `--gtest_color=no --gtest_output=json:<tmp>/results.json [--gtest_filter=A.B:C/0.D]` | the console as it comes (`[ RUN ]`, `[ OK ]`, `[ FAILED ]`, `[ SKIPPED ]`, `[ DISABLED ]`; the output and each `file:line: Failure` block of a test), then the JSON for what the console did not report |
| Catch2 | `--reporter xml --out <tmp>/results.xml --durations yes [spec]` | the XML report: `Expression`s, `Exception`s, `FatalErrorCondition`s, `Failure`s (also in `Section`s), `Skip`, `OverallResult` with `StdOut`/`StdErr` |
| doctest | `--reporters=xml --out=<tmp>/results.xml --duration=true --no-version=true --no-breaks=true [--test-case=… --case-sensitive=true]` | the XML report: `Expression`s, `Exception`s, error `Message`s (also in `SubCase`s), `OverallResultsAsserts` |

* **Filters**: GoogleTest's names joined by `:`; Catch2's names with `\ , [ ] * " ~` escaped, joined by commas in one argument (separate
  arguments would mean AND; `--input-file` fails with exit code 5 in 3.8.1); doctest's names with commas escaped (its `*`/`?` wildcards cannot be
  escaped, so a name containing them may run more tests, each reported as itself). More than 300 tests, or a filter over 24,000 characters,
  run the whole executable (Windows' command-line limit).
* **Results**: passed, failed (message, the failure's place as `file:line` — the Error List entry and the details pane; the test's output),
  skipped (`GTEST_SKIP()`, Catch2's `SKIP()`), durations. The message names the file only; its full path is the failure location.
* **A crash** (a signal, `abort()`): the tests finished before it keep their results, and the test that was running fails with "The test
  executable stopped while this test ran (exit code 139, SIGSEGV)." plus what it printed and its failures so far (GoogleTest: the open `[ RUN ]`;
  Catch2 / doctest: the `TestCase` the report left open, read tolerantly). An executable that reports nothing at all and exits non-zero fails
  the run with its exit code.
* doctest reports the tests its filter left out as skipped: those reports are ignored, so a test that was not run keeps its last result.

### Debugging

* The executable with the same arguments under `cscode.testing.cpp.debugger`, or when it is empty (default) under the CMake integration's
  `cscode.cmake.debugger` (the same choice as CTest's tests and CMake: Debug, through `CTestProvider.DebugType`); both empty → CodeLLDB
  (`lldb`) on macOS, GDB elsewhere; `codelldb`, `lldb-dap` or `gdb`. CodeLLDB gets `sourceLanguages: ["cpp"]` (no Rust formatters, its C++ exception filters). `cwd` is the
  executable's directory. No launch.json is needed.
* **Break on failure** (`cscode.testing.cpp.breakOnFailure`, default on, as TestMate's): a failed assertion stops in the debugger at the
  assertion (`--gtest_break_on_failure`, Catch2's `--break`, doctest's own break unless `--no-breaks`).
* Results come back when the session ends: GoogleTest's console from the session's output plus its JSON, Catch2's and doctest's XML report.
* Verified in the real app on this Mac (section 13): a GoogleTest test and a Catch2 test each debugged to a breakpoint, then continued to their
  result.

### Editor glyphs and lenses

`CppTestScanner` reads a C++ file's text (comments and preprocessor lines blanked; strings kept for names but never searched for macros):
`TEST`, `TEST_F`, `TEST_P`, `TYPED_TEST`, `TYPED_TEST_P` (as `Suite.Name`), and `TEST_CASE`, `SCENARIO` (`Scenario: name`),
`TEST_CASE_METHOD`, `TEST_CASE_FIXTURE`, `TEMPLATE_TEST_CASE*`, `TEMPLATE_LIST_TEST_CASE`, `TEST_CASE_TEMPLATE` with their `CATCH_` /
`DOCTEST_` forms. A listed test matches a declaration in the same file by name: `Inst/Suite.Name/2` and `Suite/0.Name` are instances of
`Suite.Name`; `name - int` and `name<int>` of a template test case; `suite::name` of `name`; doctest's `  Scenario: x` of `Scenario: x`.
Registered for `cpp`, `cuda-cpp` and `objective-cpp` documents.

### Settings

| Key | Default | Meaning |
|---|---|---|
| `cscode.testing.cpp.enabled` | `true` | Find C++ test executables. |
| `cscode.testing.cpp.executables` | TestMate's default (above) | Globs naming the test executables. Left at its default, TestMate's `testMate.cpp.test.executables` is used when set. |
| `cscode.testing.cpp.debugger` | `""` | `codelldb`, `lldb-dap`, `gdb`; empty: `cscode.cmake.debugger`, then CodeLLDB on macOS, GDB elsewhere. |
| `cscode.testing.cpp.breakOnFailure` | `true` | Debugging stops at the failing assertion. |

### Limits

* **No build before a run** and no discovery through the CMake File API yet (section 15): build (CMake: Build), then Refresh. An executable
  that a rebuild newly registers with CTest's discovery modules leaves the C++ provider at the next Refresh.
* A parameterised GoogleTest declaration (`TEST_P`, `TYPED_TEST`) has one glyph and one lens, for its first instance: its state and its Run
  are that instance's (the explorer has each instance).
* Catch2 v2: names only until a run (its listing has no places); its sections and v3's are not separate nodes (a failing section is named in
  the message). doctest subcases likewise.
* Remote (ssh) folders are not searched, as for the other providers.

## 11. clangd for Objective-C++ and CUDA

`objective-cpp` (`.mm`) and `cuda-cpp` (`.cu`, `.cuh`) are in clangd's selector with `c`, `cpp` and `objective-c`: one clangd serves all five
(`BuiltinLanguageServers`, docs/LANGUAGES.md regenerated). clangd infers the dialect from the file name or the compile command, not the
document's language id. Checked with Apple's clangd 21 (`RealClangdDialectTests`, and in the real app, section 13):

* **`.mm`** is parsed as Objective-C++ (`-x objective-c++`): classes, `std::vector` inside methods and Foundation all work; the only diagnostic
  is a real error in the file.
* **`.cu` without a CUDA SDK** (this Mac has none, and NVIDIA ships none for macOS): clangd parses it as CUDA for an `nvptx64` device and
  reports "cannot find CUDA installation; provide its path via '--cuda-path', or pass '-nocudainc'…" and "cannot find libdevice for sm_52…"
  on line 1, then `unknown type name '__global__'` and undeclared `blockIdx` / `blockDim` / `threadIdx` (the CUDA headers define them).
  A `.clangd` next to the sources quiets all but the `<<<…>>>` launch:

  ```yaml
  CompileFlags:
    Add: [-nocudainc, -nocudalib, --cuda-host-only, "-D__global__=__attribute__((global))", "-D__device__=__attribute__((device))",
          "-D__host__=__attribute__((host))", -include, __clang_cuda_builtin_vars.h]
  ```

  (checked: 7 errors → 1, `use of undeclared identifier cudaConfigureCall` on the launch line). With a CUDA SDK and a `compile_commands.json`
  from CMake (`project(x CUDA)`), clangd needs none of this.
* Debugging Objective-C++ and CUDA host code is not offered from a `.mm` / `.cu` file (the debuggers list `c`, `cpp` and `rust`); a launch.json
  of type `lldb` debugs an Objective-C++ program as any other.

## 12. C++ debugging matrix on this Mac

`tests/CsCode.Dap.Tests/RealCppDebuggingTests.cs` runs each row against both adapters through the real `DebugService` on one C++20 program
built with `clang++ -g -O0 -std=c++20` (libc++); evidence with `CSCODE_DAP_TEST_METRICS=<file>`. 8/8 pass (CodeLLDB v1.12.3, Apple lldb-dap /
LLVM 21, macOS 27 arm64, Developer Mode on). Values are what the Locals window shows (value, type; children after `->`).

| Row | CodeLLDB (`lldb`) | lldb-dap |
|---|---|---|
| `std::string` | ✅ `"hello world"` | ✅ `"hello world"` (one noise child, the representation's anonymous struct) |
| `std::vector<int>` | ✅ `size=3` → `[0]=1, [1]=2, [2]=3, [raw]` | ✅ `size=3` → `[0]=1, [1]=2, [2]=3` |
| `std::map<std::string, int>` | ✅ `size=2` → `[0]={first:"ann", second:31}, [1]={first:"bob", second:42}` | ✅ `size=2`; each element `std::pair<…> @ 0x…` until expanded |
| `std::unordered_map<int, std::string>` | ✅ `size=1` → `[0]={first:1, second:"one"}` | ✅ `size=1`; element as a pair to expand |
| `std::optional<int>` | ✅ `Has Value=true` → `Value=5`; empty: `Has Value=false` | ✅ the same |
| `std::variant<int, std::string>` | ✅ `Active Type = std::string` → `Value="alt"` | ✅ the same |
| `std::unique_ptr<int>` | ✅ `7` → `pointer=7` | ✅ `7` → `pointer=0x…` |
| `std::shared_ptr<std::string>` | ✅ `"shared" strong=1 weak=0` → `pointer="shared"` | ✅ the same → `pointer=0x… "shared"` |
| Dynamic type of a `Shape*` pointing at a `Circle` | ✅ type `Circle *`, `{radius:2}`; `*raw` evaluates as `Circle`; a `unique_ptr<Shape>` shows `pointer={radius:2}` | ⚠️ Locals: type `Circle *` → `radius=2`; but the Watch / Debug Console `*raw` evaluates to the static `Shape` (lldb-dap's `evaluate` takes no dynamic type) |
| Template in the call stack | ✅ `int twice<int>(int)` | ✅ the same |
| Lambda in the call stack | ✅ `main::$_1::operator()(int) const` | ✅ the same |
| C++ exception filters | ✅ `cpp_throw` "C++: on throw" (**on by default**, as in VS Code), `cpp_catch` "C++: on catch" | ✅ `cpp_throw` "C++ Throw", `cpp_catch` "C++ Catch" (off by default), plus `objc_*` and `swift_*` |
| Stop on throw | ✅ after a fix (below): `exception`, "Paused on exception: C++ throw"; the stack is `__cxa_throw` (no source, de-emphasised) → `thrower(int)` at the `throw` line, where the editor shows the stop | ✅ `exception`, "C++ Throw"; `__cxa_throw` → `thrower(int)` |
| Stop on catch | ✅ after the fix: "Paused on exception: C++ catch", `__cxa_begin_catch` → `main` | ✅ "C++ Catch" |
| `std::thread` | ✅ the body's breakpoint stops the worker (`main::$_2::operator()() const` over libc++'s `__thread_execute`), 2 threads listed | ✅ the same, once the threads answer is right (below) |
| `std::jthread` | ✅ the same on a third thread | ✅ the same |
| Memory window on a vector's data, Disassembly of an inlined function | open (section 15) | open |

**Fixed:** CodeLLDB's "C++: on throw" / "C++: on catch" filters are LLDB breakpoints on `__cxa_throw` / `__cxa_begin_catch`, and their stops came
as reason `breakpoint` with no description: the editor and the Exception window treated a throw as a breakpoint. `CodeLldbAdapterQuirks` now
lists them with the Rust panic filter among the adapter's own filters whose stops are breakpoint stops (`BreakpointExceptionFilters`), so the
session reports them as the exceptions "C++ throw" / "C++ catch" (Exception window: "C++ exception: A C++ exception was thrown.").
`CppDebuggingQuirksTests` covers it over a fake adapter.

**Found, fixed in stage 3:** Apple's lldb-dap answers the first `threads` request after a stop on a thread it has not listed with the list
from before that thread started (seen here with `std::thread`, by stage 3 with pthreads). `DebugSession` asks again (390b376,
`StaleThreadsTests`); the matrix's thread row checks that the first answer the session gives has the stopped thread.

**Known, not fixable here:** lldb-dap's `evaluate` returns the static type of a polymorphic object (its Locals use the dynamic one); lldb-dap
summarises map / unordered_map elements only when expanded. CodeLLDB stops at every C++ throw by default (its own default, as in VS Code):
turn "C++: on throw" off in the Breakpoints window's exception settings for programs that throw as a matter of course.

## 13. Seen in the real app

This session had no way to drive the native window (no computer-use tools; `screencapture` returns a black frame without Screen Recording
permission). The checks below ran in the **real application composition** (`RealAppHost`: every real module, a temp `CSCODE_HOME`, the
headless Avalonia window), and its rendered frames were looked at (`.scratch/screenshots/cpp-*.png`). A look in the packaged app by the user
is still worth doing.

* `CppTestExplorerRealAppTests`: a CMake C++ project (`CppTestSample`: GoogleTest, Catch2 and doctest executables, built with CMake
  before the folder opens) — showing the Test Explorer lists the three executables with their tests and places; Run All gives 6 passed, 5
  failed, 2 skipped, with each failure in the Error List at its assertion (`gtest_tests.cpp:14`, `catch_tests.cpp:8`, …); the test file shows
  pass/fail glyphs and "Run Test | Debug Test" lenses (`cpp-test-explorer.png`); a GoogleTest and a Catch2 test are each debugged to a
  breakpoint in them under CodeLLDB, continued, and come back passed.
* `CppDebugRealAppTests`: a launch.json of type `lldb` (`sourceLanguages: ["cpp"]`), F9 and F5: the Locals window shows `text`
  `"hello world"`, `numbers` `size=3` with its elements, `ages` `size=2` with `{first:"bob", second:42}`, `raw` typed `Circle *`
  (`cpp-debug-locals.png`); Continue stops at the `throw` as an exception, the Exception window says "C++ exception: A C++ exception was
  thrown." (`cpp-debug-throw.png`); then `view.mm` and `kernel.cu` get clangd's diagnostics, the status bar says "CUDA C++" and the Error List
  shows the CUDA-installation errors (`cpp-clangd-cu.png`).

## 14. Done when (brief) — status

| Check | Status |
|---|---|
| A CMake C++ project with GoogleTest and Catch2 tests builds | built with `cmake --build` in the real-tool tests; CMake: Build (section 1) builds it in the app; the C++ provider does not build before a run yet (section 15) |
| … lists, runs and debugs both kinds of test, with results and failure locations | ✅ (section 13) |
| Debugs a program, STL containers readable, stops on a thrown exception | ✅ (sections 12, 13) |
| `.cu` and `.mm` files get clangd | ✅ (sections 11, 13) |
| `docs/C-CPP.md` has the C++ matrix | ✅ (section 12) |
| Real-tool suites pass or skip with their reasons | ✅ (section 16) |

## 15. Open and not done

Stages 1–3 are merged, so the first four rows no longer wait on anything: they are open work.

| Item | Builds on | Note |
|---|---|---|
| Executables from the CMake File API (targets linking `GTest::gtest`, `gtest_main`, Catch2, doctest) | `ICMakeService.Projects[].Targets[]` (`LinkLibraries`, `Dependencies`, `Artifacts`) | today: the globs (section 10) |
| Build the target before a run, re-list after `ICMakeService.BuildFinished` | `ICMakeService.BuildAsync` | today: build, then Refresh |
| Matrix rows: Memory window on a `std::vector`'s data, Disassembly of an inlined function | `Core/Debugging/DebugNative.cs`, the `debug.memory` / `debug.disassembly` windows | breakpoints at the physical path |
| C++ snippets (MUST 5) | `Editor/Snippets/Builtin/<language id>.json` | |
| SHOULD: "Run All Tests" lens above a GoogleTest fixture / Catch2 group | not done | shared Test Explorer code (`TestCodeLensProvider`); the scanner would report the groups |
| SHOULD: C++20 modules with clangd 21 (`--experimental-modules-support`) | not checked | |
| SHOULD: vcpkg / Conan through presets' `toolchainFile` | not checked | neither is installed; needs the user's go-ahead |
| STRETCH: New Class, CMake target sources from the UI | not done | |

## 16. Tests (C++)

```
tools/fetch-cpp-test-frameworks.sh        # once: GoogleTest, Catch2, doctest into .scratch/tools/cpp-test-frameworks (asks no questions; network)
dotnet test tests/CsCode.Panels.Testing.Tests --filter "FullyQualifiedName~Cpp"          # parsers on captured output, scanner, provider (scripted), real executables, beside CTest
dotnet test tests/CsCode.Lsp.Tests --filter "RealClangdDialectTests|ManagerTests"       # clangd on .mm and .cu
dotnet test tests/CsCode.Dap.Tests --filter "RealCppDebuggingTests|CppDebuggingQuirksTests" --blame-hang-timeout 5m
dotnet test tests/CsCode.App.Tests --filter "CppTestExplorerRealAppTests|CppDebugRealAppTests" --blame-hang-timeout 6m
```

* `CppParserTests` / `CppScannerTests`: GoogleTest's JSON and printed listings, console and JSON results (a parameterised failure, an exception,
  a skip, a disabled test, a crash), Catch2's listing and report (sections, an exception, a skip, captured output, a report cut short), doctest's
  listing and report (suites, subcases, skips), on output captured from real runs (`Fixtures/cpp`); the scanner's macros and name matching.
* `CppProviderTests`: executables found by the default globs and their signatures (not test-named, no signature, CMake's scratch, `_deps`,
  outside `build/`, a library, a text file left out); settings order (ours, TestMate's, default; absolute and `${workspaceFolder}` globs);
  listing once per build; live results; a crash; a hanging listing stopped; Catch2 escaping and doctest filters; long filters dropped; debug
  configurations for CodeLLDB and lldb-dap with break on failure; GoogleTest's console from the session; executables CTest lists one by one
  (`--gtest_filter=`, `--test-case=`, no `add_test` line) left out, and kept when CTest is off; the debugger falling back to `cscode.cmake.debugger`.
* `RealCppTests` (`Category=RealTools`): the three real executables built with CMake (`CppTestSample`), listed, run, filtered, a disabled /
  hidden / skipped test run by itself. Skips without CMake, a C++ compiler or the fetched frameworks. `RealCppCTestTests`: the sample with
  `gtest_discover_tests` and `catch_discover_tests` under the real CMake integration and ctest: CTest lists those tests, the C++ provider
  keeps only the doctest executable CTest runs whole.
* `RealClangdDialectTests` (`RealTools`), `RealCppDebuggingTests` (`RealAdapter`; skips without the adapter, clang++ or, on macOS, Developer
  Mode), the two real-app tests above (screenshots in `.scratch/screenshots/cpp-*.png`).
