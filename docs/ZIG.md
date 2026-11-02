# Zig

Stage 6 of the language stages (`docs/briefs/lang-6-zig.md`, overview `docs/briefs/languages.md`). User request (2026-11-01): *"Lets break
this up into stages/languages: … 6. Zig"*. The stage ran in parallel with stages 1 and 2 (the user's choice), so what needs their shared pieces
waited; main now has them, and the items still open are listed in [Open items](#open-items-their-pieces-are-on-main-since-stages-1-and-2-merged).

Toolchain used for everything below: **Zig 0.17.0** and **zls 0.17.0** (`tools/fetch-zig.sh`: pinned tarballs from ziglang.org and the zls
GitHub release, SHA-256 checked, into `.scratch/tools/zig/0.17.0/`; the user chose these over Homebrew), CodeLLDB v1.12.3 (LLDB 22.1.8, Python
3.12), Apple's lldb-dap (lldb-2103, Python 3.9), macOS 27 arm64.

| Area | Status |
|---|---|
| Highlighting, language configuration, folding | already there (tree-sitter `zig`; not changed) |
| zls | already the built-in server; **new**: build-on-save on by default, one clear zig/zls version-mismatch message; real-server tests run on this Mac |
| Debugging | **new**: `zig` in CodeLLDB's and lldb-dap's languages, `build.zig` templates and tasks, Zig values by value; matrix below |
| Test Explorer | **new**: `ZigTestProvider` |
| Tasks | **new**: the `$zig` problem matcher; the `zig build` task provider (`zig build`, its steps from `zig build -l`) |
| Snippets | open (stage 1's bundled snippets) |

## Using it

* **Point CsCode at zig** when it is not on PATH: `"zig.path"` (VS Code's Zig extension setting: the executable or its directory) for the
  debuggers, tasks and Test Explorer; `"zls.zig_exe_path"` (zls's own setting, which zls reads through `workspace/configuration`) for zls — the
  debuggers and Test Explorer fall back to it too. zls itself is found as every server is (PATH, then `cscode.lsp.searchDirs`).
* **F5 in a `build.zig` folder** without a launch.json offers *Create launch.json*, for CodeLLDB (`lldb`; lldb-dap when CodeLLDB is not
  registered). It writes the templates and their tasks (below). Breakpoints can be set in `.zig` files.
* **Run Task** lists the tasks.json tasks: `zig: build` is the default build task and reports errors in Problems through `$zig`.
* **Test Explorer** lists the tests of every `build.zig` project in the folder; run or debug them from the explorer, the editor's glyphs or
  the code lenses.

## Debugging

### How it is wired

* `src/CsCode.Dap/Adapters/ZigToolchain.cs`: `ZigToolchainLocator` (zig from `zig.path`, `zls.zig_exe_path`, PATH; `zig env` read once per
  executable, bounded to 15 s — ZON since Zig 0.15, JSON before), `ZigProject` (the literal `.name` of each `addExecutable(.{ … })` in build.zig,
  comments and strings skipped) and `ZigDebugging` (templates, tasks.json, the pretty-printer commands).
* CodeLLDB (`CodeLldbAdapterFactory`) and lldb-dap (`LldbDapAdapterFactory`) list `zig` among their breakpoint languages and use the Zig
  templates for a folder with a `build.zig` (and no Cargo.toml). A configuration is a Zig one when `sourceLanguages` names `zig` or its folder
  has a `build.zig`; CodeLLDB is then started with `sourceLanguages: ["zig"]` (not the Rust default, so it does not look for rustc).
* **Templates** (CodeLLDB; lldb-dap's are the same without `sourceLanguages`/`terminal`):

  | Name | Program | preLaunchTask |
  |---|---|---|
  | `Zig: Debug <exe> (CodeLLDB, zig build)` (one per executable of build.zig, else the folder's name) | `${workspaceFolder}/zig-out/bin/<exe>` | `zig: build` |
  | `Zig: Debug the tests of the current file (CodeLLDB, zig test)` | `${workspaceFolder}/.zig-cache/${fileBasenameNoExtension}-test` | `zig: build test binary of the current file` |
  | `Zig: Attach to <exe> (CodeLLDB)` | `pid: ${command:pickProcess}` | — |

  The tasks are PROCESS tasks with `problemMatcher: "$zig"`: `zig build` (default build task) and `zig test ${file} --test-no-exec
  -femit-bin=${workspaceFolder}/.zig-cache/${fileBasenameNoExtension}-test` (checked for 0.17: `--test-no-exec` and `-femit-bin` exist; the
  binary goes into `.zig-cache` because zig creates that directory itself, while `zig-out/test` would not exist on a first run). The command
  is `zig` when PATH has it, else the configured absolute path. A file whose tests need modules from build.zig (`@import("mymodule")`) does
  not compile with `zig test <file>` alone: debug its tests from the Test Explorer, which compiles them as the build does.
* **Pretty printers.** The binary distribution ships Zig's LLDB printers as `lib/lldb/pretty_printers.py` (found through `zig env`'s
  `lib_dir`; Homebrew's zig keeps the same lib layout). Two problems were found and handled by CsCode's helper `Adapters/Lldb/cscode_zig_lldb.py`
  (copied next to the assembly, loaded by two `initCommands` before the user's own):
  1. The script uses Python 3.10 syntax (`match`): it loads in CodeLLDB (Python 3.12) but fails with a SyntaxError in Apple's LLDB and lldb-dap
     (Python 3.9). The helper checks the version first and prints one line in the Debug Console instead.
  2. In Zig 0.17.0 the printers read optionals as `{some, data}` and error unions as `{tag, value}`, while the LLVM backend's DWARF (what a Debug
     build uses on macOS and Linux) names them `{payload, some}` and `{error, payload}`: **every optional showed as `null`** and every error union
     as `()`. So the helper adds CsCode's own formatters (category `cscode.zig`, enabled in front of Zig's) for slices (`len=N` and the
     elements; `u8` slices as text), optionals, error unions and tagged unions (recognised by shape, LLDB 16+), accepting both layouts and
     leaving any other shape alone. Written for Python 3.6+, so lldb-dap shows the same values; Zig's own printers still format std's
     containers (ArrayList, HashMap, MultiArrayList) where they load.

### Matrix (evidence: `RealZigDebuggingTests`, `RealZigProjectTests`; values as the Variables view shows them)

The sample (`RealZigDebuggingTests.Source`) is built with `zig build-exe` (Debug). Both debuggers on this Mac, Developer Mode on.

| Row | CodeLLDB (`lldb`, default) | lldb-dap (Apple's) |
|---|---|---|
| Breakpoints in `.zig` files, launch, stop, end | works | works |
| **Slices** | `numbers = len=3`, expands to `[0]=2 [1]=3 [2]=4` (CodeLLDB adds its `[raw]` child); `name = "zig"` | same, without `[raw]` |
| **Optionals** | `some = 42`, `none = null` (Zig's printers alone: `null` for both) | same |
| **Error unions** | `good = 12`, `bad = error.InvalidCharacter` (Zig's printers alone: `()`) | same |
| **Tagged unions** | `shape = .rect = {w = 3, h = 4}`, expands to the active field only | same |
| **Generic functions in the call stack** | `main.sum__func_856@main.zig:16 \| main.main@main.zig:52 \| start.callMain…`: Zig names an instance `<fn>__func_<n>`, not `sum(i32)` | same |
| **Breaking on panic** | a panic aborts: the stop is `exception` / `signal SIGABRT`, the stack `abort \| process.abort \| debug.defaultPanic \| main.main@main.zig:61` (the `@panic` line), the message (`panic: boom`) in the Debug Console. No separate "on panic" filter: the abort is the stop | same |
| **Threads** | a breakpoint in a spawned thread stops there (`main.worker \| Thread.callFn__func_908 \| …entryFn \| _pthread_start`); `threads` lists all three | stops there too, but **lldb-dap answers the first `threads` request after the stop with the main thread alone** and lists all three a second later (an lldb-dap behaviour that affects every language: see follow-ups) |
| Watch / hover expressions | `some` → `42`, `numbers` → `len=3` | same |
| Parameters passed by reference | `pair = <variable not available>` (a `Pair(i32)` struct parameter Zig passes by reference without a location) | same |
| End to end on `zig init` (`RealZigProjectTests`) | Create launch.json → Run Task `zig: build` with a type error → Problems: `7:25 Error expected type 'u32', found '*const [4:0]u8'`; the `zig build` template builds through its preLaunchTask and stops in `zig-out/bin/hello`: `window = len=3`, `maybe = 7` | — |
| **Disassembly window** (stage 2's) | at the `inspect` breakpoint: a 128-instruction page around the instruction pointer with `main.inspect` and `main.zig:26` on its instructions; one instruction step moves `…96b8 → …96bc` (the next instruction, same line) | same page and step; lldb-dap names the symbol and file only on an instruction that starts a line |
| **Memory window** (stage 2's) | View Binary Data on `numbers[0]` (memory reference `0x1001606AC`, CsCode's slice formatter keeps the elements' addresses) reads `02 00 00 00 03 00 00 00 04 00 00 00` | same reference and bytes |
| **Registers window** | a `Registers` scope (scopes `Local`, `Static`, `Global`, `Registers`) | `Registers` (hint `registers`) |
| **Data breakpoints** (Break When Value Changes) | on `var n` in `inspect`: `4 bytes at 16FDFD19C (n)`, verified; Continue stops (`data breakpoint`) with `n = 3` | same; the stop's reason is `breakpoint` |

## Test Explorer (`src/CsCode.Testing/Providers/Zig/`)

* **Detection**: each `build.zig` under the folder (not in `zig-out` or the caches) is a project.
* **Discovery runs nothing.** The project's `.zig` files (not build.zig, `zig-out`, `.zig-cache`/`zig-cache`, or the files of a nested
  project) are scanned by `ZigTestScanner`: `test "name" { … }` and decltests `test ident { … }`, at the top level and inside named containers
  (`const Inner = struct { test "x" {} };`). Ids: `<file>::<qualified>` — `src/root.zig::test.basic add functionality`,
  `src/root.zig::decltest.add`, `src/root.zig::Inner.test.nested one`; the tree groups by file, then container. Unnamed `test { … }` blocks
  and tests inside anonymous (generic) containers are left out: neither can be run by name.
  * **Deviation from the brief**, which asks for the bundled tree-sitter grammar: the provider project (`CsCode.Testing`) is UI-free and
    references Core only, and the tree-sitter engine runs native grammar code that the editor confines to its syntax lane behind crash/hang
    isolation (quarantine, flight recorder); scanning from the Test Explorer's threads would bypass that, and a Core seam to the editor's engine
    would mean changing `CsCode.Editor`, which this stage does not own. The scan is lexical instead, and exact for this purpose: `test` is a
    reserved word (an identifier spelled so is `@"test"`), Zig has no block comments, and strings, character literals and `\\` multiline
    strings are skipped (`ZigProviderTests.The_scan_finds_…` covers each case). Moving it onto tree-sitter is a follow-up if wanted.
* **Results — what the tools offer** (checked on 0.17.0):
  * `zig test` (and a test binary) without a terminal on stderr prints one line per test: `3/6 root.test.skipped...SKIP`, `…OK`,
    `…FAIL (TestExpectedEqual)` followed by the error trace, with the test's own output between the dots and the outcome; a panic ends the
    process (`thread N panic: msg` and a trace). `ZigTestOutputParser` reads exactly that: outcome, output, message (`error.TestExpectedEqual:
    expected 3, found 2`), trace and failure location (the innermost frame inside the project).
  * `zig build test` names failed tests only (`error: 'root.test.x' failed:`), not the passing ones, and a second run on unchanged code reports
    the run steps as `cached` with no counts at all. The machine-readable modes are binary and internal: `zig build --listen=-` (the build
    server protocol) and the test runner's `--listen=-` (`std.zig.Server` messages). `zig build --print-configuration` prints the steps but
    not their modules. None of them is used.
* **Runs**: `zig test` with a `--test-filter` per requested test (the runner's full name, e.g. `root.test.basic add functionality`). A file is
  compiled as the build compiles it: the `zig test` command lines of the build's `test` step are learned from `zig build test --verbose
  --summary none` — on a cached build that prints them in ~0.15 s and runs nothing — and kept until build.zig or build.zig.zon changes (bounded
  to 5 minutes; on an unbuilt project that first call also compiles and runs the tests once, its output in the Tests channel). Their
  `--dep`/`-M` module flags are what make `@import("hello")` resolve (`zig test src/main.zig` alone fails on it). A file that is not a root of
  the test step borrows the nearest root's flags with itself as the root module; without a test step, `zig test <file>`. **Run all** of a project
  runs each root of the test step (not `zig build test`, whose output cannot give per-test results — see above).
* **Debug** compiles the same command with `--test-no-exec -femit-bin=<temp>/<file>-test` and launches that binary under CodeLLDB (`lldb`) in
  the project directory; results are read from the session's output; the binary is deleted after the session.
* Saving a `.zig` file rescans its project (a text scan). Setting `cscode.testing.zig.enabled` (default on).
* Evidence (`ZigProviderTests`, real zig on a `zig init` project with a test that uses the `hello` module, a failing, a skipped, a decltest
  and a nested test): detected; 8 tests discovered with their lines; Run all → 6 passed, 1 skipped, 1 failed with `error.TestExpectedEqual:
  expected 3, found 2`, its output and its location; one test of `main.zig` (needing the build's module) run alone, filtered, without
  re-learning.

## Tasks

* `$zig` problem matcher (`src/CsCode.Dap/Tasks/ProblemMatchers.cs`): `file:line:col: error|note: message` (owner and source `zig`); paths
  relative to the task's folder (`${cwd}`) or absolute; notes are Information; test stack-trace lines (`file:line:col: 0x… in f (test)`) do not
  match.
* The Zig **task provider** (`src/CsCode.Dap/Tasks/ZigTaskProvider.cs`, stage 2's `ITaskProvider`, registered beside cargo's): a folder with a
  build.zig gets `zig: build` (the default step, build group) and `zig: build <step>` for every other step `zig build -l` lists (`run`,
  `test` in the test group, `check` in the build group, `uninstall`, the project's own), each with `$zig` and the step's description under it in
  Run Task. `zig build -l` compiles the build script: 0.1 s on a warm cache, 48 s on a cold one (measured, zig 0.17.0, `zig init`). So it runs
  in the background (bounded at 3 minutes, its output at 256 KB and 200 steps); Run Task waits for it at most 4 s and lists `zig: build`,
  `zig: build run` and `zig: build test` until it has answered, or when it fails (a build.zig that does not compile: the build task then shows
  why in Problems). The answer is cached per folder until build.zig or build.zig.zon changes, or another zig is configured. tasks.json entries:
  `{"type": "zig", "step": "run"}` customises a detected task; with `args` (after the step), `env` or `cwd` it runs `zig build [step] [args…]`
  as written. Evidence: `ZigTaskProviderTests` (a real `zig init` project lists install's siblings, runs `zig: build test` and puts a compile
  error of `zig: build` in Problems on its line). The launch templates' tasks.json still writes its own `zig: build` (a tasks.json label wins
  over a detected one of the same label) and the per-file test-binary task, which needs `${file}`.

## zls (`src/CsCode.Lsp/Extensions/Zls/ZlsExtension.cs`)

zls's handling is a server extension on stage 2's per-server pattern (docs/LSP.md §7a), listed in `ServerExtensions.CreateBuiltins()`: it
registers the settings below in `Register`, runs the version check in `OnInitialized`, and takes zls's own version complaint through the
pattern's `HandleShowMessage` hook (added for it: an extension may keep a server's `window/showMessage` to itself).

* **Build-on-save**: zls 0.17 asks for its whole `zls` section through `workspace/configuration`; CsCode answers from its settings, where it
  registers `zls.enable_build_on_save` = **true** (zls's own default turns it on only when build.zig declares a `check` step). zls then runs
  `zig build --watch` in a project with a build.zig — the `check` step when there is one (no code generation: fast), else the install step — and
  publishes the compiler's errors. Turn it off with `"zls.enable_build_on_save": false`. Evidence: `ZlsExtensionTests.Real_zls_reports_the_compilers_
  errors_on_save…` — a `const wrong: u32 = "text";` that zls's own analysis does not flag is reported on its line (`expected type 'u32'…`).
* **Version check**: zls must be built for the Zig it runs. When zls is running, CsCode asks the zig zls uses (`zls.zig_exe_path`, else the
  first zig on the PATH zls gets: its own directory first, then PATH, then the search directories) for `zig version` (bounded) and compares
  major.minor with the version zls reported in `initialize`. A mismatch shows ONE warning: *zls and Zig do not match: zls 0.17.0 (/…/zls) is
  built for Zig 0.17, but the zig it runs is 0.16.0 (/…/zig). Install the zls release for Zig 0.16 (github.com/zigtools/zls/releases;
  tools/fetch-zig.sh pins a matching pair), or set 'zls.zig_exe_path' to a Zig 0.17.* zls's own complaint (*ZLS '…' does not support Zig '…'*)
  is held back while the check runs and shown only when the check could not tell, so one problem is one message. Evidence: real zls 0.17.0
  with a zig that reports 0.16.0 → exactly one version message (`ZlsExtensionTests.Real_zls_with_a_zig_of_another_version…`).
* The real-server test (`RealZlsAndNilServerTests.Zls_…`: initialize, diagnostics, hover, formatting, shutdown) **passes on this Mac** with zig
  and zls on PATH.
* Not checked (SHOULD items): what zls offers for `build.zig.zon` editing and its highlighting, and whether zls's inlay hints display.

## Open items (their pieces are on main since stages 1 and 2 merged)

| Item | Builds on |
|---|---|
| Zig snippets | stage 1's bundled snippets (`src/CsCode.Editor/Snippets/Builtin/zig.json`) |

Done in the merge with main: zls's handling moved onto stage 2's `LanguageServerExtension` pattern (above).

## Limits

* Test names containing `...` are split at the first `...` when the runner's line is read. Verbose command lines are split on spaces with
  quotes respected; a project path does not appear in them (they are relative), but an argument with a space in build.zig's own flags would split.
* A `zig build test` step with a custom test runner (`.test_runner`) is run with the default runner's output expectations.
* Debugging a test from the explorer debugs the first file of the selection.
* The `zig test` template (launch.json) compiles the file alone: files whose tests need build.zig's modules debug from the Test Explorer.

## Running the tests

```
tools/fetch-zig.sh                      # zig 0.17.0 + zls 0.17.0 into .scratch/tools/zig/0.17.0 (tests find them there, or CSCODE_ZIG / CSCODE_ZLS)
tools/fetch-codelldb.sh                 # CodeLLDB for the debugging rows
dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~Zig" --blame-hang-timeout 5m
dotnet test tests/CsCode.Panels.Testing.Tests --filter "FullyQualifiedName~ZigProviderTests"
dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~ZlsExtensionTests"
PATH=$PWD/.scratch/tools/zig/0.17.0/zls:$PWD/.scratch/tools/zig/0.17.0/zig:$PATH dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~RealZlsAndNilServerTests.Zls"
```

Every real-tool test skips with its reason when zig, zls, CodeLLDB or lldb-dap is missing, or when macOS Developer Mode is off.
