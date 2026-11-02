# Test Explorer (`CsCode.Testing`, `CsCode.Panels.Testing`)

Discovering, running and debugging the workspace's tests, built in (there is no extension system): .NET test projects, cargo packages,
JavaScript/TypeScript packages that use Jest or Vitest, and C++ test executables built with GoogleTest, Catch2 or doctest. Two projects:

| Project | References | Holds |
|---|---|---|
| `CsCode.Testing` | Core only (no Avalonia) | the providers (`Providers/DotNet`, `Providers/Rust`, `Providers/JavaScript`, `Providers/CTest`, `Providers/Cpp`), their output parsers and source scanners, the process runner, the test tree and the run controller (`Engine/`) |
| `CsCode.Panels.Testing` | Core, Ui, Testing | the Test Explorer tool window and its view-model, `TestingHost` (commands, settings, Error List / Output glue, context keys), the editor's glyph provider |

The editor side is `CsCode.Editor/Features/TestGutterFeature.cs`, over the new Core contract `ITestGutterProvider`
(`src/CsCode.Core/Languages/TestGutter.cs`, additive).

```csharp
services.AddCsCodeTestExplorer();   // TestingHost + the "testing" tool window (AppServices does this)
```

## Running and debugging tests (how to use it)

| What | Visual Studio keymap (default) | VS Code keymap | Command | Menu |
|---|---|---|---|---|
| Show the Test Explorer | Ctrl+E, T (⌘E, T) | — | `workbench.view.testing.focus` | Test > Test Explorer, View > Tool Windows |
| Run all tests | Ctrl+R, A (⌘R, A) | Ctrl+; A (⌘; A) | `testing.runAll` | Test > Run All Tests |
| Debug all tests | Ctrl+R, Ctrl+A (⌘R, ⌘A) | Ctrl+; Ctrl+A | `testing.debugAll` | Test > Debug All Tests |
| Run the test at the caret | Ctrl+R, T (⌘R, T) | Ctrl+; C | `testing.runAtCursor` | Test > Run Tests at Cursor |
| Debug the test at the caret | Ctrl+R, Ctrl+T (⌘R, ⌘T) | Ctrl+; Ctrl+C | `testing.debugAtCursor` | Test > Debug Tests at Cursor |
| Run the tests of the current file | — | Ctrl+; F | `testing.runCurrentFile` | Test > Run Tests in Current File |
| Debug the tests of the current file | — | Ctrl+; Ctrl+F | `testing.debugCurrentFile` | — |
| Rerun the failed tests | Ctrl+R, F (⌘R, F) | Ctrl+; E | `testing.reRunFailTests` | Test > Run Failed Tests |
| Debug the failed tests | Ctrl+R, Ctrl+F | Ctrl+; Ctrl+E | `testing.debugFailTests` | — |
| Repeat the last run | Ctrl+R, L (⌘R, L) | Ctrl+; L | `testing.reRunLastRun` | Test > Repeat Last Run |
| Debug the last run | Ctrl+R, Ctrl+L | Ctrl+; Ctrl+L | `testing.debugLastRun` | — |
| Cancel the run | — | — | `testing.cancelRun` | Test > Cancel Test Run |
| Discover again | — | — | `testing.refreshTests` | the explorer's Refresh button |

Every command is in the palette under "Test:" (Ctrl+Shift+P). In the editor, the glyph left of a test (right of the breakpoint margin) runs it
on a click and offers Run Test / Debug Test / Reveal in Test Explorer on right-click, and the "Run Test | Debug Test" code lens above it
(`TestCodeLensProvider`, the same tests as the glyphs, through the editor's code lens: docs/EDITOR.md §7e; refreshed only when the
tests of a file or their lines change, `TestGutterProvider.TestsChanged`, not on every result during a run) runs or debugs it
(`testing.codeLens.run` / `testing.codeLens.debug` with the test's id). In the explorer: the toolbar's Run All, Run (the
selected node, else everything), Debug, Rerun Failed and Cancel; double-click or Enter opens a test's source; the context menu has Run, Debug,
Go to Test, Go to Failure, Copy Name, Copy Message.

**Debugging a test** sets breakpoints as usual (F9) and starts the debugger the language's own way: a .NET test under **netcoredbg**
(`coreclr`), a Rust test under the platform's Rust debugger (`cscode.debug.rustDebugger`: CodeLLDB on macOS, GDB on Linux), a Jest/Vitest test
under **js-debug** (`pwa-node`). No launch.json is needed: the provider builds the configuration. Then F5 / F10 / F11 / Shift+F5 work as in any
debug session; the test's result comes back to the explorer when the session ends. The debuggers themselves are set up as in
[DAP.md](DAP.md) and [DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md) (netcoredbg, CodeLLDB and js-debug are bundled with the Mac app and the
Linux install; GDB comes from the system).

## Providers

### .NET (`DotNetTestProvider`)

* **Detection.** Every `*.csproj` under a folder (skipping `bin`, `obj`, `node_modules`, `.git`, …) whose text, or a `Directory.Build.props/
  .targets/Directory.Packages.props` between it and the folder, mentions a test framework (`Microsoft.NET.Test.Sdk`, xunit, NUnit, MSTest,
  TUnit, `IsTestProject`) is **evaluated** with `dotnet msbuild <proj> -getProperty:IsTestProject -getProperty:TargetPath
  -getProperty:TargetFramework -getProperty:TargetFrameworks -getItem:PackageReference` (no build; about 0.2 s each with the build server
  warm, four at a time). `IsTestProject=true` makes it a test project; an `xunit.v3` (or `xunit.v3.core*`) reference makes it **xUnit v3**,
  anything else **VSTest**. A project with `<TargetFrameworks>` (several, or a single entry) uses its first target framework: it is built
  and run with `-f <tfm>`, and since MSBuild's outer (cross-targeting) evaluation has an empty `TargetPath`, an xUnit v3 project is
  evaluated once more with `-p:TargetFramework=<tfm> -getProperty:TargetPath` for the assembly to run or debug (`bin/Debug/<tfm>/X.dll`).
  A project still without a `TargetPath` reports that instead of starting a runner with no assembly.
* **Discovery** is a scan of the project's C# files (`CSharpTestScanner`; nested projects and `bin`/`obj` excluded): methods with an xUnit
  (`[Fact]`, `[Theory]`, every `*Fact`/`*Theory` such as `[AvaloniaFact]`), NUnit (`[Test]`, `[TestCase]`, `[TestCaseSource]`) or MSTest
  (`[TestMethod]`, `[DataTestMethod]`) attribute, with their namespace (block or file-scoped), class (nested classes joined with `+`, as the
  runners name them), line and `Skip = "…"` / `[Ignore]`. Comments, strings, verbatim/raw/interpolated strings and char literals are blanked first.
  Instant, no build, every test has a file and line. *Why not `dotnet test --list-tests`*: it builds, prints display names only (no class or
  location), and takes seconds per project. What a scan cannot see (tests inherited from a base class in another file, generated tests) is
  added when a run reports it.
* **Run, xUnit v3.** `dotnet build <proj> -v:q` (output to the Tests channel), then the test assembly is run directly — `dotnet <TargetPath>
  -reporter json -noColor -noLogo -method Ns.Class.Method …` in its output directory — and every JSON progress message is read as it comes
  (`XunitJsonReporter`): `test-case-starting` (with `SourceFilePath`/`SourceLineNumber`), `test-passed|failed|skipped|not-run` with
  `ExecutionTime`, messages, exception types and stack traces, `test-output` (ITestOutputHelper). A theory's data rows (display name other than
  `Ns.Class.Method`) become **cases** under the test, which shows the worst of them. More than 300 requested tests, or a filter longer than
  24,000 characters (`MaxFilterChars`), drop the filter (the whole project runs): Windows' CreateProcess takes at most 32,767 characters of
  command line, and the count alone does not bound it (300 VSTest ids of 80 characters, each named twice, make a 62,000-character filter).
  Test: `A_filter_too_long_for_a_Windows_command_line_is_dropped_…`.
* **Run, VSTest** (xUnit v2, NUnit, MSTest): `dotnet test <proj> --logger "trx;LogFileName=<tmp>/results.trx" --results-directory <tmp>
  --filter "FullyQualifiedName=A|FullyQualifiedName~A\(|FullyQualifiedName=B|FullyQualifiedName~B\("` (filter characters escaped); the TRX
  file is read at the end (`TrxReader`: `UnitTestResult` joined with its `UnitTest`'s class and method; Passed, Failed/Error/Timeout/Aborted,
  NotExecuted/Skipped). The `~A\(` half is for NUnit, whose parameterized cases (`[TestCase]`, `[TestCaseSource]`) have the fully qualified
  name `Ns.Class.Method(1,2)`, which the exact condition never matches (running such a test ran nothing); the `(` is the boundary that keeps
  `Ns.Class.Add` from running `Ns.Class.AddMany`. The filter has no "starts with", so a test of another class whose full name merely ends
  with the id followed by `(` runs too (and is reported); a parameterized NUnit fixture (`Ns.Fixture(1).Method`) is not matched by its
  method's id. Test: `Running_an_NUnit_parameterized_test_runs_each_of_its_cases_…` (a scripted `dotnet test` that applies the filter as the
  test host does).
* **Debug.** xUnit v3: the same assembly and arguments launched under netcoredbg (`{"type": "coreclr", "request": "launch", "program":
  <TargetPath>, "args": [...], "cwd": <output dir>, "console": "internalConsole"}`); the JSON reporter's lines are read from the session's
  output events (re-joined into lines). VSTest: `dotnet test` with `VSTEST_HOST_DEBUG=1`; the "Process Id: N" it prints is attached to
  (`"request": "attach", "processId": N`), and the TRX is read when it ends.
* **Failure location**: the first stack frame in a file under the project (frames of the framework's own `/_/src/…` sources skipped).

### Rust (`RustTestProvider`)

* **Detection.** Each `Cargo.toml` with a `[package]` (a pure `[workspace]` root is not a project; its members are).
* **Discovery and every run** start with `cargo test --manifest-path <toml> --no-run --message-format=json-render-diagnostics`: cargo builds
  what changed, its diagnostics go to the Tests channel, and its `compiler-artifact` messages name each test executable with its target
  (lib, bin, test, …) and crate root. Each executable is then asked `--list --format terse`; each test's file and line are found from the
  target's crate root by `RustTestLocator` (inline `mod x { }` stays in the file, `mod x;` is `x.rs` or `x/mod.rs` next to a crate root or a
  `mod.rs` and in the directory named after any other file; `#[path]` is not followed). A test's id is `<target>/<libtest path>`: `lib/tests::adds`,
  `test:integ/integration_works`. Grouping: package > target > modules > test.
* **Run.** The executables are run directly (package directory as working directory, `CARGO_MANIFEST_DIR`/`CARGO_PKG_NAME` set,
  `RUST_BACKTRACE=0`) with the requested names and `--exact` (none: all; more than 500: all of that target), and `--include-ignored` when one test
  is asked for by itself, so an `#[ignore]`d test runs when it is chosen. libtest's plain output is read (`LibtestOutputParser`): `test x ... ok |
  FAILED | ignored[, reason]`, and for a failure its `---- x stdout ----` section (what it printed; the panic message and `panicked at file:line`
  for the failure location). Stable libtest reports no per-test time, so Rust tests show none (the JSON format with times needs nightly).
* **Debug.** The test executable under the Rust debugger (`cscode.debug.rustDebugger`, read as the debugger module reads it: `lldb`/`codelldb`
  → CodeLLDB, `gdb` → GDB; unset: CodeLLDB on macOS, GDB elsewhere) with `<names> --exact --nocapture --test-threads=1`, `cwd` and `env` as for a
  run. A debug run over several targets debugs them one after another, one session each (lang-2; before, only the first). The libtest lines
  are read from the session's output.
* **Doc-tests** (lang-2). A package with a library also has its doc-tests listed (`cargo test --doc -- --list --format terse`) under a
  "Doc-tests (<lib>)" target: id `doc/src/lib.rs - add (line 3)`, label `add (line 3)`, located at the example's line. They run with
  `cargo test --doc` — all of the package's whenever any is asked for: with rustdoc's merged doctests (edition 2024, Rust 1.96.1) a name filter
  does not select one (`--exact` filters every one out; a plain filter runs them all) — so running one also reports the others. They cannot be
  debugged (rustdoc compiles each when it runs): a debug run says so in the Tests output and debugs the other selected tests.

### JavaScript / TypeScript (`JavaScriptTestProvider`)

* **Detection.** Each `package.json` (outside node_modules) that declares `vitest` or `jest` (dependencies, devDependencies, a `jest` key, or
  its `test` script); Vitest wins when both appear.
* **Discovery** scans the test files (`*.test.*`, `*.spec.*`, `__tests__/`; js, jsx, ts, tsx, mjs, cjs, mts, cts) with `JsTestScanner`:
  `describe`/`suite`/`context` blocks and `it`/`test`/`specify` calls (with `.only`, `.skip`, `.todo`, `.concurrent`, `fit`, `xit`, `xdescribe`…)
  whose name is a string literal, nested by their callbacks' braces. Template literals with substitutions and `.each` tables are left to the
  run. A test's id is its file (relative, `/`) and names: `src/math.test.js::math > adds`. Grouping: package > file > describe blocks > test.
* **Run.** The package's own runner with node: `node node_modules/jest/bin/jest.js --json --outputFile=<tmp> --testLocationInResults --ci=false
  [--testNamePattern=^(a b|c)$] [files]`, or `node node_modules/vitest/vitest.mjs run --reporter=json --outputFile=<tmp> …` (looked up from the
  package directory upwards; a missing runner says "Jest is not installed in … (run npm install there)"). The JSON report is read at the end
  (`JestJsonReader`, the same shape for both): `assertionResults` with `ancestorTitles`, `title`, `status`, `duration`, `failureMessages`
  (ANSI colours stripped) and `location`; a file that failed to run at all is reported in the output and as the run's error.
* **Debug.** The same command under js-debug (`pwa-node`, `program` = the runner script, `--runInBand` / `--no-file-parallelism`, timeouts off,
  `autoAttachChildProcesses`, node internals and node_modules skipped); the report is read when the session ends.
* Paths node reports are resolved through symbolic links (`/private/var/…` for `/var/…` on macOS): they are put back under the folder as it
  was opened (`PhysicalPaths`), so ids, Error List entries and glyphs match the open documents. .NET frames get the same treatment.

### CTest (`CTestProvider`, stage 3 — docs/C-CPP.md)

* **Detection.** Each CMake project of the CMake integration (`ICMakeService`, `src/CsCode.Build.CMake`) under the folder; the project is the
  source directory, its "file" its `CMakeLists.txt`.
* **Discovery** configures the project when it never was, then reads `ctest --test-dir <build> --show-only=json-v1 [-C <config>]`
  (`CTestJson`): a test's id is its CTest name, its label carries its `LABELS`, it is grouped under the target whose executable it runs, and
  its location is the `add_test` line its backtrace names, so the glyphs sit in CMakeLists.txt (the source scan finds `add_test(NAME …)`). A
  `DISABLED` test is shown skipped. A new configure (a saved CMakeLists.txt, another preset) re-discovers the project.
* **Run.** The targets whose executables the requested tests run are built through the CMake integration (all targets when a test runs
  something else), then `ctest --test-dir <build> --output-on-failure --no-tests=ignore --output-junit <tmp> [-C <config>] [-R '^(a|b)$']`.
  Passes are reported as their console lines arrive; the JUnit file (CTest 3.21+, checked with `ctest --version`) gives the failures' output
  and messages at the end; an older ctest is read from its console output alone (`CTestResults`). ctest's exit code 8 means "a test failed".
* **Debug.** One test: its command from the JSON (program and arguments) under the C debugger (`cscode.cmake.debugger`: CodeLLDB on macOS,
  GDB elsewhere, `lldb-dap` on request) in its `WORKING_DIRECTORY` (default: the build directory of the CMakeLists.txt that added it), with its
  `ENVIRONMENT`; the result is the program's exit code.

### C++ (`CppTestProvider`)

GoogleTest, Catch2 and doctest executables, each a project. Full description in [C-CPP.md](C-CPP.md) section 10; in short:

* **Detection.** Files matching `cscode.testing.cpp.executables` (else C++ TestMate's `testMate.cpp.test.executables`, else test-named files
  under `build/` or `out/`) that are native executables carrying a framework's signature in their bytes (never run to be classified).
* **Discovery.** The framework's listing (GoogleTest's JSON listing, Catch2's and doctest's XML), with file and line; bounded (20 s, 16 MB),
  cached by the executable's size and modification time (listed again only after a rebuild). Ids are `<executable>/<runner name>`.
* **Run.** The executable in its directory with the framework's filter; GoogleTest's console as it comes plus its JSON, Catch2's and doctest's
  XML report; a crash fails the test that was running. A disabled / hidden / skipped test runs when chosen by itself.
* **Debug.** The same command under `cscode.testing.cpp.debugger` (empty: CTest's `cscode.cmake.debugger`; CodeLLDB on macOS, GDB
  elsewhere, or lldb-dap), stopping at a failed assertion (`cscode.testing.cpp.breakOnFailure`); results read when the session ends.
* **Beside CTest.** An executable whose tests CTest lists one by one (`gtest_discover_tests`, `catch_discover_tests`,
  `doctest_discover_tests`, `gtest_add_tests`, read from `ctest --show-only=json-v1`) is left to the CTest provider while it is on, so no test
  is listed twice; one CTest runs whole stays here.
* Building first and discovery through the CMake File API are open (C-CPP.md section 15): build, then Refresh.

## The tree, runs and threading (`TestController`, `TestTree`)

* **Tree.** Project > grouping levels (namespace, target, module, file, class, describe block) > test > case. Groups show the aggregate of their
  children (failed > running > queued > passed; all skipped = skipped; any not run = not run) and the sum of their durations. A rediscovery
  keeps the state of tests that are still there (by id) and drops tests that are gone.
* **Runs.** One at a time (a second request while one runs is refused with a message; Cancel first). Requested tests are queued, then marked
  running per project, then get their results; tests a run did not report go back to "not run" (with the run's error, if any). A request that
  covers a whole project runs it unfiltered. Debug runs debug one project (the first). Cancel kills the tool's process tree, or stops the debug
  session. Rerun Failed takes the tests whose last result is failed; Repeat Last Run repeats the last request (run or debug as asked).
* **Threading.** The tree is owned by the UI thread. Providers run on the thread pool and report through a sink that queues events and posts one
  flush at a time to the UI thread (at background priority); a flush applies every queued event, re-aggregates each affected group once
  (deepest first) and raises one change. A run of 500 tests reports to the UI in a handful of batches (asserted: under 100 change notifications
  for 1000 events). Tool output goes straight to the Tests output channel (thread-safe, bounded at 2 MB by the shell). Child processes inherit the
  login shell's environment on macOS (`LoginShellEnvironment.WaitAsync`), have `DOTNET_ROOT_<ARCH>` folded into `DOTNET_ROOT`
  (`InheritedDotnetRoot`), and are reported to `IMemoryMonitor.TrackChildProcess`.
* **Memory.** A test's output, message and stack are each cut at 64 KB (`TestLimits`); at most 50,000 tests per project are taken; files over
  2 MB are not scanned; the Error List gets at most 500 failures per file. The view-model is created when the window shows and disposed when it
  hides (a leak test checks the view and its model are collected after a run with a selection); the results live in the controller.

## The Test Explorer window

`testing`, "Test Explorer", icon `checklist`, docked left by default (with Find in Files). Toolbar: Run All, Run, Debug, Rerun Failed, Cancel |
Refresh, Collapse All, Expand All, Clear All Results. Filter box: words matched (all, ignoring case) against a test's id and name; `@failed`,
`@passed`, `@skipped`, `@notrun` select by state; groups show while a test under them matches, expanded. Summary line: "12 tests: 9 passed, 2
failed, 1 not run · last run 1.3 s · Building rs_sample…". Rows (virtualised flat list): chevron, state icon (VS Code's `testing.icon*` colours),
kind icon, name (project rows add the framework, or a discovery error; skipped tests their reason), duration. Projects expand by default, and a
project's only child too. The details pane under a splitter shows the selected node: id, state and time, file:line, message, stack trace,
output; for a group, its counts. Keys: Up/Down (the list), Left/Right (collapse/expand; Left on a leaf selects its parent), Enter (open the
source), Ctrl/⌘+C (copy the test's id). The keyboard stays on the rows when they are rebuilt (`ToolWindowFocus.KeepOnRows`) and so does the
selection (a rebuild resets the list, which used to drop it).

## Settings

| Key | Default | Meaning |
|---|---|---|
| `testing.gutterEnabled` | `true` | Run/result glyphs in the editor. |
| `testing.codeLens` | `true` | "Run Test \| Debug Test" code lenses above each test (shown while `editor.codeLens` is on). |
| `testing.defaultGutterClickAction` | `"run"` | `run`, `debug` or `contextMenu`. |
| `testing.openTesting` | `"openOnTestStart"` | `neverOpen`, `openOnTestStart` / `openExplorerOnTestStart` (show the explorer when a run starts), `openOnTestFailure`. Shown without taking the keyboard. |
| `cscode.testing.autoDiscover` | `true` | Discover when an editor shows a file that declares tests (a Rust package is built to list them). Tests are always discovered when the explorer is first shown, a test command runs, or Refresh, and never just because a folder opened. Off: only then. |
| `cscode.testing.dotnet.enabled` / `.rust.enabled` / `.javascript.enabled` / `.cpp.enabled` | `true` | Turn a provider off. |
| `cscode.testing.cpp.executables` | TestMate's default | Globs naming the C++ test executables (C-CPP.md section 10). |
| `cscode.testing.cpp.debugger` | `""` | `codelldb`, `lldb-dap` or `gdb` for C++ tests; empty: `cscode.cmake.debugger`, then CodeLLDB on macOS, GDB elsewhere. |
| `cscode.testing.cpp.breakOnFailure` | `true` | Debugging a C++ test stops at the failing assertion. |

Also read: `cscode.debug.rustDebugger` (the Rust debugger) and `cscode.debug.nodePath` (node for Jest/Vitest runs).

## Integration (what the host does)

* **Error List.** Failed tests are diagnostics (owner `tests`, source "Test Explorer", severity Error) at the failure's line (else the test's):
  "Test failed: Ns.Class.Method — first line of the message". They go when the test passes, the results are cleared, or the host is disposed.
* **Output.** The "Tests" channel: each command line (`> dotnet build …`), the tools' output, and a header and summary per run.
  `testing.showMostRecentOutput` shows the Output window (choose "Tests" in its channel box).
* **Context keys.** `testing.isRunning`, `testing.hasFailedTests` (the Test menu's `when` clauses).
* **Saving** a C# file or a JS/TS test file rescans its project (debounced 500 ms; a text scan, no build). A Rust package is discovered again
  only by Refresh (it needs a build).
* **When discovery starts.** Not when a folder opens (a cargo package would be built, `cargo test --no-run`, for a user who never looks at
  its tests): the first time tests are needed (`TestingHost.EnsureDiscoveredAsync`), i.e. the Test Explorer is shown, a test command runs
  (Run All, Run Test at Cursor, ... wait for that discovery, then run), Refresh, or, with `cscode.testing.autoDiscover` (default), an editor
  shows a file in which the glyph provider's text scan finds tests (its glyphs appear when discovery ends). From then on a change of the
  workspace folders discovers again. Tests: `Opening_a_folder_discovers_nothing_until_the_test_explorer_is_first_shown`,
  `A_test_command_or_refresh_before_any_discovery_discovers_first`, `An_editor_showing_a_file_with_tests_starts_discovery_unless_auto_discovery_is_off`,
  and the real-app Rust test (no discovery before the explorer is shown).
* **Folders.** Only local (`file:`) workspace folders are searched. A remote (ssh) folder has no tests in the explorer yet.
* **Editor glyphs.** `TestGutterProvider` is registered in `ILanguageFeatureRegistry` for every provider language (csharp, rust, javascript,
  typescript, javascriptreact, typescriptreact, cpp, cuda-cpp, objective-cpp). The editor asks it 300 ms after an edit and when it fires `StatesChanged`; it scans the
  document's text (off the UI thread) and matches the tests found to the tree through an immutable index (file → tests and states) that the UI
  thread rebuilds after each change (coalesced). So the glyphs follow edits (lines come from the current text) and appear for tests the explorer
  knows (after discovery).

## Tests

`tests/CsCode.Panels.Testing.Tests` (xUnit v3 + Avalonia.Headless):

* **Parsers on real output** (`Fixtures/`): an xUnit v3 `-reporter json` run and a `dotnet test` TRX captured from a real sample project
  (passed, failed with output, skipped, a theory with a failing row), libtest output from a real cargo run, a Jest report.
* **Scanners**: C# (namespaces, nesting, attributes, comments/strings/raw strings, skip reasons, generic return types, lambdas), Rust (inline
  and out-of-line modules, `#[ignore]`, lifetimes vs char literals; the locator over a temp tree), JS (describe nesting, modifiers, `.each`).
* **Command lines and detection** (scripted processes, temp folders): xUnit `-method`, the VSTest filter and its escaping, msbuild evaluation,
  cargo artifacts, `--exact`/`--include-ignored`, Jest/Vitest arguments, package.json detection, workspace roots.
* **Real tools on sample projects in temp folders**: an xUnit v3 project (msbuild detection, scan, build, JSON run, a single-test filter), a
  cargo package (build, list, locations through `mod parser;`, run with an integration test, an ignored test run by itself), and a Jest package
  whose runner is a stand-in script run by the real node (arguments, report, symlinked paths). A missing tool skips with the reason.
* **Debug runs** with a scripted debug session (output replayed in odd chunks): the launch/attach configurations of all three providers and the
  results read back; cancelling stops the session.
* **Controller**: grouping, aggregation, filters per selection, rerun failed/last, cases, cancel, rediscovery, results for unknown tests, debug
  without a debugger, batching.
* **Host and window** (headless): commands/settings/provider registration, Error List entries and context keys, `testing.openTesting`, the
  glyph provider (states, lines following edits), the code lens provider (lenses per test, their commands, refresh when the tests change but not on results, the setting),
  run at cursor / current file, disposal; the view's rows, toolbar, filter, details, context menu,
  keys, reveal, an empty workspace, a leak test, and a screenshot (Visual Studio Dark) saved to `.scratch/screenshots/test-explorer.png`.

`tests/CsCode.Editor.Tests/TestGutterFeatureTests.cs`: glyphs on the test lines, following edits, new states, click (run / debug as set),
the context menu, no width without tests, the setting. `tests/CsCode.App.Tests/TestExplorerCompositionTests.cs`: the real composition (tool
window, commands, the Ctrl+R, A chord, the glyph provider for C# and Rust, the Test menu). `tests/CsCode.App.Tests/RealApp/TestExplorerRealAppTests.cs`:
the real app discovers a cargo package, runs it (a failure in the Error List at the panic's line) and debugs one test to a breakpoint in it,
then Continue and the result comes back from the session — under CodeLLDB on macOS (passes here, about 3 s) and GDB on Linux (skips when GDB
14+ is missing, as in the shared Linux VM).

## Known gaps / next steps

* **Code lens.** "Run Test | Debug Test" only: no "Run All Tests" lens above a test class and no result in the lens text (the glyph shows the
  result). rust-analyzer's own Run/Debug lenses show only on binaries and examples (its server extension drops them on tests in favour of these: docs/RUST.md).
* **Microsoft.Testing.Platform projects** (MSTest runner, NUnit runner, TUnit) and **F# / VB** test projects are handled since stage 5
  (C#): listing with `--list-tests json`, runs with `--filter-uid` and `--report-trx`, debugging by launching the test executable (no TRX: it
  moves the tests into a child host), F# discovery through the runner's listing. Details and what was checked: [CSHARP.md](CSHARP.md) §5.
* **Theories with pre-enumerated data** show their cases after a run, not at discovery.
* **Rust**: no per-test durations (stable libtest); `#[path]` modules and macros that generate tests are found by `--list` but get no location;
  running one doc-test runs all of the package's (no name filter with merged doctests), and doc-tests cannot be debugged.
* **Jest/Vitest**: custom `testMatch`/`include` patterns are not read (the default patterns are); a watch mode is not offered; Vitest's browser
  mode is not handled. Debugging Vitest in a worker pool may need `--pool=forks`.
* **Remote folders** (ssh) are not searched: the providers run tools locally. Running them through `cscode-server` is the remote workstream's.
* **Output channel selection.** `testing.showMostRecentOutput` shows the Output window but cannot pick the "Tests" channel (the Output view has
  no command for it).
* **Not verified end to end:** a Jest/Vitest test under js-debug (debugging a .NET test under netcoredbg is: `RealTestingPlatformTests`
  stops at a breakpoint in an MSTest and an NUnit test, docs/CSHARP.md); their configurations are unit-tested with a scripted session.
  Rust under CodeLLDB is verified by the real-app test on macOS; GDB on Linux is not (the shared VM has no GDB).
* **Breakpoints and symlinked folders (macOS `/var` → `/private/var`)**: rustc records the directory as the OS resolves it, and the debugger
  matches breakpoints by that path, so a breakpoint set through a symbolic link of the folder does not bind. This is the debugger's matching
  (launch.json debugging behaves the same), seen with folders under the temp directory; the real-app test sets its breakpoint on the resolved path.
