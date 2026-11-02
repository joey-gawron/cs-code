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

* Results that are text open as **read-only documents** (`lsp-readonly:/rust-analyzer/<kind>/<name>`, `ReadOnlyDocuments`): the macro
  expansion as `<macro>!.rs` (Rust colouring), the syntax tree as `<file>.rast` (rendered from the JSON tree as indented `KIND@start..end`
  lines, capped at 200,000 lines), HIR as `<file>.hir`, MIR as `<file>.mir`.
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

### Tests

```
dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~RustAnalyzerExtensionTests"   # no server: snippets, syntax tree, runnables, lens/hover rewriting
dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~RealRustAnalyzerTests"        # the real rust-analyzer (RealTools; skips without rust-analyzer/cargo)
```

`RealRustAnalyzerTests` (11 tests, 1.96.1 on the Mac): status reaches the status bar; Expand Macro opens the read-only expansion; syntax
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

## 3. Deferred to after stage 1 (Python) merges

* **Rust snippets** (MUST 5): they go in stage 1's bundled-snippets mechanism, which is not on this branch.
* **`codeActionsOnSave` with rust-analyzer** (SHOULD): checking organize imports needs stage 1's participant.
