# Stage brief — 2. Rust

**User request (2026-11-01):** "Lets break this up into stages/languages: … 2. Rust …" (overview: `docs/briefs/languages.md`). Starts after
stage 1 (Python) is merged.

**Owns:** `src/CsCode.Panels.Debug/**` (new Disassembly, Memory and Registers windows; data breakpoints in Locals / Watch / Breakpoints),
`src/CsCode.Dap/**` (instruction stepping, instruction and data breakpoints, `writeMemory`, task auto-detection in `Tasks/`), `src/CsCode.Lsp/**`
(rust-analyzer extensions, experimental client capabilities, `inlayHint/resolve`), the editor's debug and inlay-hint features, additive Core
contracts (`ITaskProvider`, the debug memory and disassembly surface, an inlay-hint resolve hook), `src/CsCode.Testing/Providers/Rust/**`,
shell wiring in `src/CsCode.App/**`, tests next to each, `docs/RUST.md`, additions to `docs/DEBUGGING-RUST-TS.md`, `docs/PANELS-DEBUG.md`,
`docs/DAP.md`, and the regenerated `docs/LANGUAGES.md`.

## What is already there (verify, don't rebuild)
* rust-analyzer (rustfmt), CodeLLDB (`lldb`, the default on macOS), lldb-dap, GDB (`gdb`, `rust-gdb`); `CargoArtifacts.cs` turns cargo's
  build output into the program to launch; `RustToolchain.cs`; the `$rustc` problem matcher.
* The Rust debug evaluation matrix is already in `docs/DEBUGGING-RUST-TS.md`; don't redo it, extend it with the new windows.
* The Rust Test Explorer provider (`cargo test --list`, libtest output). rust-analyzer's own Run/Debug lenses are hidden in favour of the
  Test Explorer's (docs/TESTING.md).
* **Protocol plumbing with no UI:** `DebugSession.ReadMemoryAsync` / `DisassembleAsync` (`src/CsCode.Dap/Session/DebugSession.Api.cs:491`),
  data-breakpoint mapping (`DapMapping.cs`), and Core's capability flags `SupportsDataBreakpoints`, `SupportsReadMemory` and
  `SupportsDisassemble` (`src/CsCode.Core/Debugging/DebugModels.cs:79`). No window or menu uses any of them.
* No auto-detected tasks (docs/DAP.md lists task providers as out of scope; this stage changes that).
* `inlayHint/resolve` is not exposed (docs/LSP.md §12).

## Toolchain on this Mac (checked 2026-11-01)
cargo, rustc and rust-analyzer 1.96.1 (rustup, `~/.cargo/bin`); Apple LLDB lldb-2103 (`/usr/bin/lldb`); lldb-dap from the Command Line
Tools (`xcrun -f lldb-dap`); CodeLLDB in `.scratch/tools/codelldb`. No GDB (arm64 macOS). Nothing to download.

## MUST
1. **Native debugging windows (C, C++ and Zig reuse these).** Each is gated on the adapter's capabilities and checked against CodeLLDB and
   lldb-dap on this Mac.
   * **Disassembly**, opened from the current frame's `instructionPointerReference` or from any address.
     * Source lines are interleaved when the adapter returns locations.
     * Breakpoints can be set on instructions (`setInstructionBreakpoints`).
     * While the Disassembly window has focus, stepping goes by instruction (`stepGranularity: "instruction"`), as in VS Code.
     * The view is virtualised: pages are fetched around the visible range with `instructionOffset`, and the cache is bounded.
   * **Memory**, a hex + ASCII view opened from a variable's `memoryReference` ("View Binary Data") or an address expression.
     * Reads are paged `readMemory` requests; edits use `writeMemory` when the adapter supports it.
     * It refreshes at each stop, and the cache is bounded.
   * **Registers**: the adapters' register scope shown as its own window. Check how CodeLLDB and lldb-dap expose it.
   * **Data breakpoints** ("Break When Value Changes" on a Locals / Watch row, through `dataBreakpointInfo` / `setDataBreakpoints`), listed
     in the Breakpoints window.
2. **rust-analyzer extensions.** Take the method names from rust-analyzer's `docs/book/src/contributing/lsp-extensions.md` and check each one
   against the installed version.
   * **Client capabilities:** advertise the experimental ones CsCode can honour (`snippetTextEdit`, `codeActionGroup`, `hoverActions`,
     `serverStatusNotification`, `commands`).
   * **Commands:** Expand Macro Recursively (the result opens as a read-only Rust document), Join Lines, Matching Brace, Parent Module,
     Open Cargo.toml, View Syntax Tree / HIR / MIR, Structural Search Replace, Open External Docs (in the browser), Reload Workspace and
     Rebuild Proc Macros.
   * **Server status** in the status bar (quiescent, health, message).
   * **Hover actions** (Run, Debug, Go to Implementations) shown in hovers.
   * **Build it as a small reusable pattern:** one "server extension" class per server, which registers its commands and handles its custom
     notifications. clangd (stage 3) and Roslyn (stage 5) reuse it.
3. **Run and debug binaries and examples** from rust-analyzer's runnables.
   * Run | Debug lenses on `fn main` and examples; tests keep the Test Explorer's lenses.
   * A "Rust: Run… / Debug…" quick pick of runnables.
   * Both launch through CodeLLDB with a cargo build first.
4. **Auto-detected tasks (C, C# and Zig reuse the contract).**
   * An additive Core `ITaskProvider` whose tasks show in Run Task and can be named in `preLaunchTask` (`"cargo: build"`).
   * Cargo's provider: `build`, `check`, `clippy`, `test`, `run` and `doc` per package, with `$rustc`.
   * Update docs/DAP.md's "out of scope" line.
5. **Rust snippets** in the bundled-snippets mechanism from stage 1.

## SHOULD
* `inlayHint/resolve` (an additive Core resolve hook). rust-analyzer resolves hint tooltips and clickable label parts lazily; make label
  parts clickable (go to the type).
* Test Explorer gaps from docs/TESTING.md: list doc-tests (`cargo test --doc`), and debug runs that span several targets.
* `codeActionsOnSave` with rust-analyzer: check that organize imports works through stage 1's participant.

## STRETCH
Cargo.toml dependency versions from the crates.io sparse index (network: ask first); a `rust-analyzer.cargo.features` quick pick.

## Threading & isolation (binding)
00-common applies. Disassembly and Memory pages are fetched on the Debug lane and delivered as bounded immutable batches. Scrolling
the Disassembly window must not issue one request per row. Each window's cache is bounded and dies with the session; add `LeakAssert`
tests for every new view model.

## Done when
In the real app on this Mac, with a Rust debug session under CodeLLDB and under lldb-dap:
* Step by instruction in the Disassembly window.
* View a `Vec`'s buffer in the Memory window and see the registers.
* A data breakpoint stops when a value changes.

Also in the real app: Expand Macro and the other commands work, a Run | Debug lens shows on `main`, and cargo tasks show in Run Task.
`docs/RUST.md` and the new sections of `docs/PANELS-DEBUG.md` / `docs/DEBUGGING-RUST-TS.md` record what works, with evidence. The real-adapter
suites pass or skip with their reasons. Committed without attribution trailers.
