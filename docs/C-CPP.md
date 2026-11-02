# C and C++ (stages 3 and 4)

Stage 3 (C, `docs/briefs/lang-3-c.md`) writes sections 1–9 of this page (CMake, CTest, document links, the C matrix). Stage 4 (C++,
`docs/briefs/lang-4-cpp.md`) adds sections 10–16 below. The two stages ran at the same time on separate branches; when they merge, this page
keeps stage 3's sections and appends these.

Stage 4 ran before stages 1 (Python), 2 (Rust) and 3 (C) were merged, so what builds on them is **deferred** (section 15): finding test
executables through the CMake File API and CTest, building a target before a run, the matrix rows that need the Memory and Disassembly
windows, and C++ snippets.

Toolchain on this Mac (2026-11-01): Apple clang / clangd 21.0.0, CMake 3.31.12 (MacPorts, first on PATH) and 4.4.2 (Homebrew), Ninja 1.13.2,
CodeLLDB v1.12.3 (`.scratch/tools/codelldb`), Apple lldb-dap (LLVM 21, `lldb-2103.0.34.103`). GoogleTest 1.17.0, Catch2 3.8.1 and doctest
2.4.12 were fetched with the user's go-ahead by `tools/fetch-cpp-test-frameworks.sh` (pinned GitHub source archives, SHA-256 checked, built
and installed into `.scratch/tools/cpp-test-frameworks/`); nothing else was installed.

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

* The executable with the same arguments under `cscode.testing.cpp.debugger`: empty (default) → CodeLLDB (`lldb`) on macOS, GDB elsewhere;
  `codelldb`, `lldb-dap` or `gdb`. CodeLLDB gets `sourceLanguages: ["cpp"]` (no Rust formatters, its C++ exception filters). `cwd` is the
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
| `cscode.testing.cpp.debugger` | `""` | `codelldb`, `lldb-dap`, `gdb`; empty: CodeLLDB on macOS, GDB elsewhere. |
| `cscode.testing.cpp.breakOnFailure` | `true` | Debugging stops at the failing assertion. |

### Limits

* **No build before a run** and no discovery through CMake yet (section 15): build, then Refresh. Not listing tests twice once CTest lists
  `gtest_discover_tests` / `catch_discover_tests` tests (stage 3's CTest provider) is part of that deferred work: until then a CMake project
  whose test executables match the globs shows them both as CTest tests and as C++ executables.
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
| Memory window on a vector's data, Disassembly of an inlined function | deferred (stage 2's windows) | deferred |

**Fixed:** CodeLLDB's "C++: on throw" / "C++: on catch" filters are LLDB breakpoints on `__cxa_throw` / `__cxa_begin_catch`, and their stops came
as reason `breakpoint` with no description: the editor and the Exception window treated a throw as a breakpoint. `CodeLldbAdapterQuirks` now
lists them with the Rust panic filter among the adapter's own filters whose stops are breakpoint stops (`BreakpointExceptionFilters`), so the
session reports them as the exceptions "C++ throw" / "C++ catch" (Exception window: "C++ exception: A C++ exception was thrown.").
`CppDebuggingQuirksTests` covers it over a fake adapter.

**Found, fixed in stage 3:** Apple's lldb-dap answers the first `threads` request after a stop on a thread it has not listed with the list
from before that thread started (seen here with `std::thread`, by stage 3 with pthreads). Stage 3's branch makes `DebugSession` ask again
(390b376, `StaleThreadsTests`); this branch does not repeat that change, and the matrix's thread row asks again itself meanwhile.

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
| A CMake C++ project with GoogleTest and Catch2 tests builds | built with `cmake --build` (outside the app); building from the app is stage 3's CMake integration (deferred) |
| … lists, runs and debugs both kinds of test, with results and failure locations | ✅ (section 13) |
| Debugs a program, STL containers readable, stops on a thrown exception | ✅ (sections 12, 13) |
| `.cu` and `.mm` files get clangd | ✅ (sections 11, 13) |
| `docs/C-CPP.md` has the C++ matrix | ✅ (section 12) |
| Real-tool suites pass or skip with their reasons | ✅ (section 16) |

## 15. Deferred and not done

| Item | Waits for / why | Note |
|---|---|---|
| Executables from the CMake File API (targets linking `GTest::gtest`, `gtest_main`, Catch2, doctest) | stage 3 merged | `ICMakeService.Projects[].Targets[]` has `LinkLibraries`, `Dependencies`, `Artifacts` (stage 3's note) |
| Not listing `gtest_discover_tests` / `catch_discover_tests` tests twice (CTest and C++) | stage 3 merged | stage 3's `CTestJson.Parse` gives each test's command: skip executables the C++ provider owns, or the reverse |
| Build the target before a run, re-list after `ICMakeService.BuildFinished` | stage 3 merged | today: build, then Refresh |
| Matrix rows: Memory window on a `std::vector`'s data, Disassembly of an inlined function | stage 2's windows | |
| C++ snippets (MUST 5) | stage 1's bundled-snippets mechanism | |
| SHOULD: "Run All Tests" lens above a GoogleTest fixture / Catch2 group | not done | shared Test Explorer code (`TestCodeLensProvider`); the scanner would report the groups |
| SHOULD: C++20 modules with clangd 21 (`--experimental-modules-support`) | not checked | |
| SHOULD: vcpkg / Conan through presets' `toolchainFile` | not checked | neither is installed; needs the user's go-ahead and stage 3's presets |
| STRETCH: New Class, CMake target sources from the UI | not done | |

## 16. Tests (C++)

```
tools/fetch-cpp-test-frameworks.sh        # once: GoogleTest, Catch2, doctest into .scratch/tools/cpp-test-frameworks (asks no questions; network)
dotnet test tests/CsCode.Panels.Testing.Tests --filter "FullyQualifiedName~Cpp"          # parsers on captured output, scanner, provider (scripted), real executables
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
  configurations for CodeLLDB and lldb-dap with break on failure; GoogleTest's console from the session.
* `RealCppTests` (`Category=RealTools`): the three real executables built with CMake (`CppTestSample`), listed, run, filtered, a disabled /
  hidden / skipped test run by itself. Skips without CMake, a C++ compiler or the fetched frameworks.
* `RealClangdDialectTests` (`RealTools`), `RealCppDebuggingTests` (`RealAdapter`; skips without the adapter, clang++ or, on macOS, Developer
  Mode), the two real-app tests above (screenshots in `.scratch/screenshots/cpp-*.png`).
