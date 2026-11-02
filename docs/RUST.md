# Rust in CsCode (stage 2)

What CsCode does for Rust on top of plain LSP and DAP, how each piece was checked, and its limits. The brief is
[briefs/lang-2-rust.md](briefs/lang-2-rust.md); the debugger's evaluation matrices are in [DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md).

Toolchain checked on the Mac (2026-11-01): cargo, rustc and rust-analyzer 1.96.1 (rustup); Apple LLDB lldb-2103; lldb-dap from the
Command Line Tools; CodeLLDB v1.12.3 in `.scratch/tools/codelldb`.

## 1. rust-analyzer's extensions (`src/CsCode.Lsp/Extensions/RustAnalyzer/`)

Built on the per-server extension pattern ([LSP.md §7a](LSP.md)): one class, `RustAnalyzerExtension`, which clangd (stage 3) and Roslyn
(stage 5) mirror.

### Method names, checked against the installed server

The names come from rust-analyzer's `docs/book/src/contributing/lsp-extensions.md`. Each was sent to rust-analyzer 1.96.1 on a small package
(a direct probe, then `RealRustAnalyzerTests`); its `initialize` answer advertises `externalDocs`, `joinLines`, `matchingBrace`,
`openCargoToml`, `parentModule`, `runnables` (`kinds: ["cargo"]`) and `ssr` under `capabilities.experimental`.

| Method | Answer seen from 1.96.1 | CsCode command |
|---|---|---|
| `rust-analyzer/expandMacro` | `{name: "twice!", expansion: "(v.len()) + (v.len())\n"}` | Rust: Expand Macro Recursively at Caret (`rust-analyzer.expandMacro`) |
| `experimental/joinLines` | `TextEdit[]` | Rust: Join Lines |
| `experimental/matchingBrace` | `Position[]` | Rust: Find Matching Brace |
| `experimental/parentModule` | `Location[]` | Rust: Locate Parent Module |
| `experimental/openCargoToml` | `Location` | Rust: Open Cargo.toml |
| `rust-analyzer/viewSyntaxTree` | a JSON tree as a string (the older text request `rust-analyzer/syntaxTree` answers "unknown request") | Rust: Show Syntax Tree |
| `rust-analyzer/viewHir` | text (`fn main() {…}`) | Rust: View Hir |
| `rust-analyzer/viewMir` | text (`fn main() { let _0: (); …}`) | Rust: View Mir |
| `experimental/ssr` | `WorkspaceEdit` | Rust: Structural Search Replace |
| `experimental/externalDocs` | a URL string (`https://doc.rust-lang.org/stable/alloc/macro.vec.html`) | Rust: Open External Docs |
| `rust-analyzer/reloadWorkspace` | `null` | Rust: Reload Workspace |
| `rust-analyzer/rebuildProcMacros` | `null` | Rust: Rebuild Build Dependencies and Proc Macros |
| `experimental/runnables` | `Runnable[]` (`kind: "cargo"`, `args.cargoArgs`, `executableArgs`, `cwd`, `environment`) | Rust: Run... / Rust: Debug... |
| `experimental/serverStatus` (notification) | `{health: "ok", quiescent: false → true, message: null}` | the status-bar item |

### Client capabilities advertised (`capabilities.experimental`)

`hoverActions`, `serverStatusNotification`, `codeActionGroup`, `snippetTextEdit`, and `commands` with the client-side commands CsCode
registers: `rust-analyzer.runSingle`, `rust-analyzer.debugSingle`, `rust-analyzer.showReferences`, `rust-analyzer.gotoLocation`. Not
advertised: `localDocs`, `openServerLogs`, `colorDiagnosticOutput`, `testExplorer` (the Test Explorer has its own cargo provider).

* **`snippetTextEdit`** is honoured as plain text: an assist's edit with `insertTextFormat: 2` has its snippet syntax removed before it is
  applied (`$0` and `$1` vanish, `${1:x}` becomes `x`, `${1|a,b|}` becomes `a`, `\$` `\}` `\\` are unescaped) — `Snippets.cs`, through the
  extension's `RewriteAnswer` hook on `textDocument/codeAction` and `codeAction/resolve`. Gap: the caret is not moved to `$0`.
* **`codeActionGroup`**: rust-analyzer then sends a `group` on related assists; CsCode lists them as separate actions (no submenu).

### The commands

All are in the command palette under "Rust", enabled in a Rust editor (`editorLangId == rust`); Reload Workspace and Rebuild Proc Macros
work from anywhere. They ask the server that serves the active editor (`ServerExtensionHost.ActiveSession`). The editor commands use the
caret: CsCode's navigation service exposes the caret, not the selection, so **Join Lines joins the caret's line with the next** (VS Code's
behaviour for an empty selection) and Structural Search Replace runs over the whole workspace.

* Results that are text open as **read-only documents** (`ReadOnlyDocuments`: Core virtual documents,
  `cscode-virtual://results/<kind>/rust-analyzer/<name>`, the same way as library and generated code, docs/CSHARP.md §2): the macro
  expansion as `<macro>!.rs [expanded]` (Rust colouring), the syntax tree as `<file>.rast [syntax tree]` (rendered from the JSON tree as
  indented `KIND@start..end` lines, capped at 200,000 lines), HIR as `<file>.hir [HIR]`, MIR as `<file>.mir [MIR]`.
* Matching Brace moves the caret; Parent Module and Open Cargo.toml open the file (several parents: a pick); Open External Docs opens the
  browser; SSR asks for `pattern ==>> replacement` and applies the edit as one undoable step per file.

### Server status

`experimental/serverStatus` drives a status-bar item (left): `⟳ rust-analyzer` while not quiescent (loading the workspace, indexing,
building proc macros), `✓ rust-analyzer` when quiescent and healthy, `⚠` / `✖` for a warning or error health with the message as the tooltip
(and in the server's Output channel). With several servers (one per Cargo workspace root) the latest report shows; the item goes when the last
server stops. A click offers Reload Workspace, Rebuild Proc Macros and Restart Server.

### Hover actions

With `hoverActions`, rust-analyzer adds `actions` (groups of command links) to hovers: ▶︎ Run / ⚙︎ Debug on `fn main` and tests, "Go to N
implementations" (`rust-analyzer.showReferences` → the references list), "Go to Type" (`rust-analyzer.gotoLocation`). The extension turns
them into one more Markdown block under the hover, marked **trusted** (`MarkupContent.IsTrusted`, additive Core), so the editor runs its
`command:` links; links in a server's own text are never run (docs/EDITOR.md).

### Run and debug from runnables

* **Lenses.** rust-analyzer's ▶︎ Run | ⚙︎ Debug lenses show on `fn main` and on examples; the extension drops them on tests, benches and
  doctests (`AdjustCodeLenses`), where the Test Explorer's "Run Test | Debug Test" lenses are (docs/TESTING.md).
* **Run** (`rust-analyzer.runSingle`, the lens, the hover link, Rust: Run...): `cargo <cargoArgs> [-- <executableArgs>]` in a task terminal
  named after the runnable, in the runnable's `cwd` with its `environment` (rust-analyzer's VS Code extension runs it as a cargo task too).
* **Debug** (`rust-analyzer.debugSingle`, Rust: Debug...): a launch of the Rust debugger (`cscode.debug.rustDebugger`: CodeLLDB `lldb` on
  macOS, GDB on Linux) with the `cargo` key: `cargo run …` becomes `cargo build …` (with a `filter` on the `--bin` / `--example`), `cargo
  test …` gets `--no-run`; the executable cargo reports is launched with the runnable's arguments, `cwd` and `env`.
* **Rust: Run... / Debug...** ask `experimental/runnables` for the active file at the caret and offer them in a quick pick (label, cargo
  command line).

### Seen in the real application

`RealAppRustStageTests.Rust_analyzer_status_Expand_Macro_the_main_lens_and_the_cargo_tasks_show_in_the_app` (real app, headless window, real
rust-analyzer 1.96.1, 2026-11-01): the status bar shows `✓ rust-analyzer` once it is quiescent; the lens providers give `▶︎ Run` and `⚙︎ Debug`
on `fn main` (line 60 of the sample) and nothing on its tests; Expand Macro at `vec!` opens `lsp-readonly:/rust-analyzer/expand-macro/vec!.rs` (since the C# merge: the tab `vec!.rs [expanded]`, a Core
virtual document),
read-only, language `rust` (`alloc::boxed::box_assume_init_into_vec_unsafe(…)`); Run Task lists `cargo: build`, `cargo: check`,
`cargo: clippy`, `cargo: test`, `cargo: run`, `cargo: doc` (no tasks.json). Frames: `.scratch/screenshots/rust-analyzer-status.png`,
`rust-main-lens.png`, `rust-expand-macro.png`, `rust-run-task.png`.

### Tests

```
dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~RustAnalyzerExtensionTests"   # no server: snippets, syntax tree, runnables, lens/hover rewriting
dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~RealRustAnalyzerTests"        # the real rust-analyzer (RealTools; skips without rust-analyzer/cargo)
```

`RealRustAnalyzerTests` (12 tests, 1.96.1 on the Mac, inlay hint resolve included): status reaches the status bar; Expand Macro opens the read-only expansion; syntax
tree, HIR and MIR; Matching Brace, Parent Module, Open Cargo.toml; Join Lines and SSR through the workspace-edit path; Open External Docs; hover
actions on `main` are trusted links; Run/Debug lenses on `main` and none on tests; the Run lens starts `cargo run --package demo --bin demo` in a
task terminal and Debug launches `lldb` with `cargo build …` and a filter; Rust: Run... lists the file's runnables; Reload Workspace and Rebuild
Proc Macros settle the server again.

## 2. Detected cargo tasks

See [DAP.md](DAP.md), "Detected tasks (ITaskProvider, lang-2)". `Tasks/CargoTaskProvider.cs` lists `cargo: build`, `check`, `clippy`, `test`,
`run` and `doc` for the folder's workspace, per package when there are several (`cargo: build - core` = `cargo build -p core`), and `cargo: run
- <bin>` per binary when there are several; all use `$rustc`. Packages come from `cargo metadata --no-deps --format-version 1` (37 ms on a
fresh package here), cached until a manifest changes. A tasks.json written for VS Code's rust-analyzer (`{"type": "cargo", "command": "build",
"args": […]}`) customises them or runs its own cargo command. `preLaunchTask: "cargo: build"` (the debugger's own Rust templates) now works
without a tasks.json.

Tests: `CargoTaskProviderTests` (metadata parsing; a real two-package workspace: per-package and per-binary tasks, caching until a manifest
changes, `cargo: build` and a customised release build run through the task runner) and `TaskProviderTests` (the contract).

## 3. Native debugging (Disassembly, Memory, Registers, data breakpoints)

The windows and the DAP surface are documented in docs/PANELS-DEBUG.md and docs/DAP.md ("Native debugging"); the per-adapter capability table
is in docs/DEBUGGING-RUST-TS.md. C, C++ and Zig get the same windows: nothing in them is Rust-specific.

### Native debugging, seen (2026-11-01, this Mac, Developer Mode on)

**In the real application** (`RealAppRustStageTests`: `RealAppHost` composes every real module in a headless window, with the real cargo,
rust-analyzer, CodeLLDB v1.12.3 and Apple's lldb-dap; frames saved as `.scratch/screenshots/rust-*.png`). For each adapter, on the cargo sample
with a launch.json naming only `"preLaunchTask": "cargo: build"` (no tasks.json: the detected task builds):

| Check | CodeLLDB (`lldb`) | lldb-dap |
|---|---|---|
| F9 at `@loop`, F5 | built by the detected `cargo: build`, stopped at main.rs:97 | same |
| Disassembly window, current instruction | `0x1000081A0 ldr w8, [sp, #0x28c]`, 162 rows of which 33 source lines | same address, 152 rows, 24 source lines |
| Keyboard in the window (View > Tool Windows) | `disassemblyViewFocus` = true | true |
| Step Over (F10) | **one instruction**: `0x1000081A0 → 0x1000081A4`, still main.rs:97 | **same** |
| A `Vec`'s buffer in the Memory window | View Binary Data on `v[0]`: `0x…c780  01 00 00 00 02 00 00 00  03 00 00 00 …` | address box `v.buf.inner.ptr.pointer.pointer` (no Rust formatters: see below): same bytes |
| Registers window | General Purpose Registers: `x0=1, x1=0, x2=4295394984, …`, `pc` | same |
| Break When Value Changes on `x` (Locals) | listed in Breakpoints, verified; Continue stops (reason `data breakpoint`) with `x` 41 → 42 | verified; stops (reason `breakpoint`) with `x` 41 → 42 |
| Shift+F5 | the session-bound data breakpoint goes with the session | same |

**Through the DAP layer** (`RealNativeDebuggingTests`, both adapters, 6/6): the instruction pointer disassembles with `main.rs` lines (and
the window's first page, 64 each side, holds it at index 64), one instruction step, `vec![1, 2, 3]`'s buffer, the register scope's groups
(General Purpose, Floating Point, Exception State, Scalable Vector/Matrix Extension), and the data breakpoint stop.

### Found and fixed on the way

* **lldb-dap hung on Rust locals on a Mac.** CsCode loaded the toolchain's LLDB formatters into lldb-dap (`initCommands`, as `rust-lldb`
  does). With rust 1.96.1 and Apple's LLDB (lldb-2103), `lldb_providers.py` takes the `Ok(12)` of a `Result<i32, String>` for a `String`,
  reads memory at `0xc`, fails, and LLDB then never answers: the Locals (and every later request) of the sample's `main` hung for 120 s+.
  Now the formatters are not loaded into Apple's lldb-dap (`LldbDapAdapterFactory.IsAppleLldbDap`); Rust values show as their raw structs
  there. LLVM's lldb-dap (Homebrew, Linux) still gets them.
* **Symlinked paths on a Mac.** A breakpoint set on `/var/folders/…/main.rs` never binds in LLDB, whose debug info has `/private/var/…` (the
  same cause as netcoredbg's dbg-16 in docs/handoff/macos-audit.md); the tests use the physical path. A workspace opened through a symlink
  hits it too: not fixed here.

### Known gaps (native)

* lldb-dap answers `evaluate` with "can't evaluate expressions when the process is running" for about half a second after an instruction
  step, though it already reported the stop (seen once per run; a second Enter in the address box works).
* lldb-dap on a Mac has no Rust formatters (above): a `Vec` has no element rows, so View Binary Data shows the `Vec` struct; type
  `v.buf.inner.ptr.pointer.pointer` in the Memory window's address box for its buffer.
* The Disassembly window opens in the bottom tool well (VS Code opens it as an editor tab).
* Memory edits are a row at a time (an input box with up to 16 hex bytes), not typing over the hex grid.

## 4. Test Explorer (SHOULD)

* **Doc-tests** are listed (`cargo test --doc -- --list`) under "Doc-tests (<lib>)", at their example's line, and run with `cargo test --doc`
  — all of the package's: with rustdoc's merged doctests (edition 2024, Rust 1.96.1) `--exact` filters every one out and a plain filter
  runs them all. They cannot be debugged. Real test: `Cargo_doc_tests_are_listed_at_their_examples_and_run`.
* **A debug run over several targets** debugs them one after another (one session each); before, only the first.

## 5. inlayHint/resolve (SHOULD)

rust-analyzer 1.96.1 advertises `resolveProvider` once the client offers `resolveSupport` (tooltip, label tooltips, locations, commands):
its type hints then come without locations, and resolving `: Vec<i32>` gives the `Vec` part a location in `alloc/src/vec/mod.rs`
(`RealRustAnalyzerTests.Inlay_hints_resolve_their_type_parts_to_the_types_definition`). In the editor a hint resolves when the pointer rests on
it (its tooltip then shows) and Ctrl/⌘+click on a part goes to its location (docs/EDITOR.md).

## 6. Snippets (MUST 5)

`src/CsCode.Editor/Snippets/Builtin/rust.json`, in stage 1's bundled-snippets mechanism ([PYTHON.md](PYTHON.md) §6): 56 snippets, written for
CsCode, standard library only.

| Kind | Prefixes |
|---|---|
| Items | `fn`, `fnr` (with a return type), `pfn`, `afn`, `main`, `mainr` (returns `Result`), `new`, `struct`, `tstruct`, `enum`, `impl`, `implt`, `trait`, `mod`, `macro`, `const`, `static` |
| Trait implementations | `display`, `default`, `from`, `iterator`, `drop`, `errortype` (an error enum with `Display` and `std::error::Error`) |
| Attributes | `derive`, `allow`, `cfg` |
| Tests | `test`, `tests` (a `#[cfg(test)]` module), `testpanic` (`#[should_panic]`), `docex` (a doc-comment example, which is a doc-test) |
| Control flow | `if`, `ifelse`, `iflet`, `letelse`, `match`, `matcho` (`Option`), `matchr` (`Result`), `while`, `whilelet`, `loop`, `for`, `forr` (a range), `fore` (`enumerate`) |
| Bindings and blocks | `let`, `letm`, `closure`, `unsafe` (with its `// SAFETY:` comment) |
| Macros | `println`, `printd` (`{:?}` of a value, its name mirrored), `eprintln`, `format`, `vec`, `hashmap`, `assert`, `asserteq`, `spawn` |

Bodies indent with tabs, which the editor turns into the file's indentation (four spaces for Rust), so an expanded snippet is already as
rustfmt lays it out. rust-analyzer adds its own snippet and postfix completions; both show in the suggest list. A user snippet with the same
prefix (or name) replaces CsCode's. Tests: `BundledSnippetTests` (every body parses, one snippet per prefix) and `RustSnippetTests` (the set
is Rust's; `tests` and `printd` expand to rustfmt's layout).

## 7. Organize imports on save (SHOULD)

Checked through stage 1's `editor.codeActionsOnSave` participant with rust-analyzer 1.96.1 (2026-11-02):

* **rust-analyzer has no organize-imports action.** Its `codeActionKinds` are `""`, `quickfix`, `refactor`, `refactor.extract`,
  `refactor.inline` and `refactor.rewrite`. Asked for `only: ["source.organizeImports"]`, it answers with its unused-import quick fixes anyway
  ("remove the whole `use` item", kind `quickfix`).
* **The participant keeps only actions of the kind it asked for**, as VS Code does. So `"source.organizeImports": "explicit"` changes
  nothing in a Rust file, and no import is removed behind the user's back.
* **Sorting comes from rustfmt.** With `"editor.formatOnSave": true`, rust-analyzer's formatting runs rustfmt, whose `reorder_imports` (on by
  default) sorts each group of `use` lines. Unused imports stay. To remove them, use the quick fix "Remove all unused imports" on a `use` line
  (Ctrl+. / ⌘.); "Merge imports" is there too.

So for Rust, `"[rust]": { "editor.formatOnSave": true }` is what organizes imports on save.

Seen in the real app: `RealAppRustStageTests.Organize_imports_on_save_is_rustfmts_import_order_and_applies_none_of_rust_analyzers_quick_fixes`
saves a `lib.rs` with three unsorted imports, two of them unused, under `codeActionsOnSave` `source.organizeImports` and `formatOnSave`. A
recording provider shows the save asked for `source.organizeImports`. rust-analyzer offered its two quick fixes, and the saved file is
rustfmt's: the imports sorted, both unused ones kept, the function reflowed. Frame: `.scratch/screenshots/rust-organize-imports-on-save.png`.
If rust-analyzer gains `source.organizeImports`, the test fails at its first assertion, and this section needs updating.

## 8. Not done

The brief's STRETCH items: Cargo.toml dependency versions from the crates.io sparse index (that needs network approval) and a
`rust-analyzer.cargo.features` quick pick.
