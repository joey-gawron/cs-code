# Stage brief — 3. C

**User request (2026-11-01):** "Lets break this up into stages/languages: … 3. C …" (overview: `docs/briefs/languages.md`). Starts after
stages 1 and 2 are merged; stage 4 (C++) builds on what this stage adds.

**Owns:** a new CMake integration project (additive Core contract plus its own `src/CsCode.Build.CMake/**` or a name the implementer
justifies), `src/CsCode.Lsp/**` (clangd extension, `textDocument/documentLink`), the editor's document links and inactive-region
rendering, new `src/CsCode.Testing/Providers/CTest/**`, `src/CsCode.Dap/**` (C launch templates), shell wiring in `src/CsCode.App/**`, tests
next to each, `docs/C-CPP.md` (shared with stage 4), and the regenerated `docs/LANGUAGES.md`.

## What is already there (verify, don't rebuild)
* clangd (root markers `compile_commands.json`, `compile_flags.txt` and `.clangd`; formats with clang-format rules), the `$gcc` problem matcher,
  and tree-sitter `c` as an option.
* CodeLLDB, lldb-dap and GDB all list `c` among their languages (`CodeLldbAdapterFactory.Languages`). No C launch templates or matrix exist.
* cmake-language-server for CMake files (init `buildDirectory: build`), and CMake coloured by tree-sitter.
* `${command:id}` already resolves in launch.json (`src/CsCode.Dap/Configuration/VariableResolver.cs:142`), so a registered
  `cmake.launchTargetPath` command makes existing VS Code CMake Tools launch.json files work.
* From stage 2: the Disassembly, Memory and Registers windows, data breakpoints, `ITaskProvider`, and the per-server extension pattern.
* `textDocument/documentLink` is not implemented (docs/LSP.md §6).

## Toolchain on this Mac (checked 2026-11-01)
Apple clang 21.0.0 and Apple clangd 21.0.0 (`/usr/bin`), CMake 4.4.2 (Homebrew), Ninja 1.13.2 (`/opt/local/bin`, MacPorts), GNU Make 3.81,
Apple LLDB, lldb-dap (`xcrun -f lldb-dap`), CodeLLDB in `.scratch/tools`. No standalone clang-format or clang-tidy (clangd has both built in),
no GDB, no cmake-language-server, no bear. Ask before installing any of those.

## MUST
1. **CMake integration (C++ reuses it).**
   * **Detection:** `CMakeLists.txt` at a workspace folder's root, or at `cmake.sourceDirectory`.
   * **Configure:** with the configure presets in `CMakePresets.json` / `CMakeUserPresets.json` when present (chosen from a quick pick and
     persisted per workspace). Otherwise `cmake -S . -B <cmake.buildDirectory> -G <cmake.generator, Ninja when installed>
     -DCMAKE_BUILD_TYPE=<Debug>` plus `cmake.configureArgs`. Always add `-DCMAKE_EXPORT_COMPILE_COMMANDS=ON`.
   * **clangd** is pointed at the build directory (`--compile-commands-dir`) and restarted when it changes.
   * **Targets** come from the CMake File API (a `codemodel-v2` query under `.cmake/api/v1/query/client-cscode/`; the reply lists the
     executables, libraries and their artifacts). Never parse `CMakeLists.txt`.
   * **Commands:** Configure, Build (all or one target), Clean, Select Configure/Build Preset, Select Build Type, Select Launch Target,
     Run Target and Debug Target (CodeLLDB on macOS, lldb-dap as the alternative, GDB on Linux).
   * **`cmake.launchTargetPath`** registered as a command, for `${command:…}` in launch.json.
   * **Tasks** through `ITaskProvider`, with `$gcc`.
   * **Status bar:** preset / build type, launch target, and a build button.
   * **Configure on open:** asked the first time, then remembered (`cmake.configureOnOpen`).
   * **Output:** build output goes to an Output channel and its errors to the Problems list.
2. **clangd extension** (stage 2's pattern). Check each method against the clangd docs and the installed clangd 21.
   * **Switch Between Header/Source** (`textDocument/switchSourceHeader`), bound to the key VS Code's clangd extension uses.
   * **Inactive preprocessor regions** greyed out (clangd's `textDocument/inactiveRegions` notification, sent when the client advertises
     the capability).
   * **View AST** (`textDocument/ast`).
   * **clangd's file status** in the status bar.
   * **Review the default clangd arguments** (`--background-index`, `--clang-tidy`, `--header-insertion`, `--completion-style`) and record
     the choices in `docs/C-CPP.md`.
3. **`textDocument/documentLink` + `documentLink/resolve`** in the LSP client and the editor: the link is underlined on hover and opens on
   Ctrl/⌘+Click. clangd links `#include` targets; later web stages get HTML/CSS links for free.
4. **CTest in the Test Explorer (C++ reuses it).**
   * **Discovery:** `ctest --show-only=json-v1`, which gives names, labels and the backtrace to each `add_test`.
   * **Runs:** `--output-junit` for results (check the minimum CTest version), and `-R '^name$'` to run one test or several.
   * **Debugging** launches the test's command from the JSON under CodeLLDB, with its `WORKING_DIRECTORY`.
5. **C debugging matrix on this Mac** (CodeLLDB, then lldb-dap), recorded in `docs/C-CPP.md` with evidence; fix what is fixable.
   * Launch and clean exit; line, conditional, hit, log and function breakpoints; data breakpoints (watchpoints); steps.
   * Signals: SIGSEGV and abort shown as an exception stop.
   * Values: structs, arrays, pointers, unions and `char *` strings; the Memory window opened from a pointer; Disassembly.
   * pthreads; stdin through the integrated terminal.
   * Attach by pid: document what macOS requires (Developer Mode / debugger entitlement).
6. **C launch templates:** a CMake target, and a single file (a pre-launch task running `clang -g -O0 ${file} -o …`).
7. **C snippets** in the bundled-snippets mechanism.

## SHOULD
* Projects without CMake: document generating `compile_commands.json` with bear or compiledb, and add a "Create compile_flags.txt" command.
* A Build and Run Active File command for a single-file C program.
* Check that clangd honours `.clang-format` and `.clang-tidy` files; document it.

## STRETCH
Makefile targets as tasks; Meson.

## Threading & isolation (binding)
00-common applies. `cmake` configure and build run as tracked child processes at below-normal priority and their output is batched.
File API replies are parsed on the IO lane. Configuring a large project must not block the editor; it shows progress and can be cancelled.

## Done when
In the real app on this Mac, a CMake C project:
* configures from a preset, and clangd finds its headers through the exported compile commands;
* builds through the status bar, with errors in Problems;
* debugs a target (Disassembly and Memory included);
* lists, runs and debugs its CTest tests in the Test Explorer.

Also: Switch Header/Source and Ctrl+Click on `#include` work, and `#if 0` blocks are greyed. `docs/C-CPP.md` has the C matrix. The real-tool
suites pass or skip with their reasons. Committed without attribution trailers.
