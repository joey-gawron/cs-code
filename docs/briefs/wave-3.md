# Wave 3 — everything left before the end pass and the install

**User decisions (2026-10-29 ~21:00), verbatim:** "1. Let's do the hybrid approach 2. Get all the downloads you need 3. You can run the tests
4. Please install it. Extra: Get rid of the color drop down on the main page and only have it in the settings menu. Scope the rest now."

What that settles:
1. **Tree-sitter: hybrid, staged** (docs/TREE-SITTER-EVALUATION.md §1): TextMate stays the default engine; tree-sitter colours the languages TextMate
   cannot (the 17), is opt-in for the others, and C# stays on TextMate. Grammars ship pre-built from pinned, licence-checked sources.
2. **Downloads are approved** for what the work needs: NuGet packages through `dotnet restore` (runtime packs for linux-x64/linux-arm64/osx-x64/
   osx-arm64/win-x64, crossgen2/ReadyToRun, ILCompiler), CodeLLDB from github.com/vadimcn/codelldb releases, tree-sitter grammar sources at the
   revisions nvim-treesitter's lockfile pins, and container images for a Docker transport test. Rules: official sources only; pin the version; record
   size and SHA-256 and check a published digest when there is one; each download goes into its own new, empty directory under the repo's git-ignored
   `.scratch/downloads/<name>/<version>/`; never run a downloaded install script, never `curl | sh`, never sudo/apt/nix-env; a `tools/fetch-*.sh` script
   reproduces every download with its pinned digest. The app itself still asks the user before it downloads anything (like `NetCoreDbgInstaller`).
3. **Real-desktop checks** (clipboard with KDE/Wayland apps, Ctrl+F focus on a real window) are approved, but the integrator runs them. Builders still
   never open windows on the user's display.
4. **Install** (docs/briefs/app-icon-and-install.md) is approved and stays last, after the end pass.
5. **Accepted defaults** (the list put to the user): the palette lists every command; the warm spare is OFF by default; Rust panics stop by default,
   the GDB Debug Console evaluates expressions, Locals shows Return after Step Out; macros are refused while Vim is on (command-only macros play);
   macro keys Ctrl+M Ctrl+R / Ctrl+M Ctrl+Enter; one Undo reverses a whole macro playback; nothing is recorded while the terminal has focus; terminal
   shell integration is injected (setting to turn it off); Ctrl+F and Ctrl+Up/Down in a focused terminal stay VS Code's; terminals are not restored
   after a restart; remote keeps its own JSON-RPC peer, gets a coloured status-bar badge and server-side Find in Files.
6. **"Scope the rest now":** every item left out earlier is in scope now (below). The js-debug one-console.log-per-line flood fix stays dropped (the
   user said "Drop flood fix").

The end-of-work fix list is `.scratch/notes/end-pass-triage.md` (items referred to as T1…T25). Items not given to a builder here stay for the end pass.

## Builders (each in its own worktree off main; the integrator merges them one by one)

**W1 Tree-sitter hybrid** — `src/CsCode.Editor.TreeSitter/**`, the editor's highlighting seam, `tools/treesitter/**`, docs/TREE-SITTER*.md,
docs/LANGUAGES.md.
* Stage 1 of the evaluation's plan as a product, not a prototype: a real setting (per language, e.g. `editor.syntaxEngine` = `auto | textmate |
  treesitter` with `[lang]` overrides; `auto` = tree-sitter only where TextMate has no grammar), replacing `editor.experimental.treeSitter`
  (keep reading it as an alias).
* Grammar distribution: a pinned manifest (grammar, repository, revision, licence, query source) for at least the 17 languages and every grammar the
  `auto` default uses; `tools/treesitter/fetch-grammars.sh` downloads the pinned sources and `build-grammars.sh` compiles them (gcc, linux-x64 here;
  the script takes a target for other platforms) into a git-ignored output that the App build/publish copies next to the app; queries from
  nvim-treesitter (Apache-2.0) with a NOTICE file. Discovery order: the app's bundled grammars first, then the user data dir, then the env vars;
  the Nix store fallback stays for development only.
* Crash and hang isolation: decide from §8 and implement (at least the parse give-up guard and an ABI/version gate; say whether out-of-process
  parsing is worth it, with numbers).
* Structural features from the tree (§7): expand/shrink selection first, then folding, then an outline/breadcrumbs fallback when no language
  server provides symbols; Vim text objects and sticky scroll if the rest is solid.
* T1 (regenerate docs/LANGUAGES.md after your changes), T10 (review the TextMate livelock fix 10c2a72), T16's tree-sitter budget tests (real or
  load?).

**W2 Terminal completion** — `src/CsCode.Terminal/**`, `src/CsCode.Panels.Terminal/**`, their tests, docs/TERMINAL.md; the Dap task runner's
presentation seam (additive).
* Split terminals (VS Code ids and keys, a group per tab, focus moves, kill/close semantics).
* Tasks run in terminals: the Dap module's task runner (preLaunchTask, `cargo build`, `tsc`) presents in a terminal named after the task, problem
  matchers still feed the Error List, reuse/close rules as VS Code's `presentation` options.
* Shell-integration injection, default on (`terminal.integrated.shellIntegration.enabled`): bash, zsh, fish and pwsh scripts written for CsCode
  (OSC 633), injected without touching the user's rc files (VS Code's approach: an init file that sources the user's rc first); tests with the
  sandboxed shells only.
* Find: highlight all matches, regex, whole word, matches across wrapped rows.
* Screen-reader text model (the accessible text of the visible buffer and the last command's output).

**W3 Remote R3/R4** — `src/CsCode.Remote/**`, `src/CsCode.Server/**`, tests, docs/REMOTE.md; additive seams in Terminal (a remote `IPtyHost`), Dap
(remote adapters and tasks), Panels.Search (server-side search) and the status bar (an item background colour).
* R3: debug adapters run on the remote host (DAP bridged over the connection), tasks run remotely, remote terminals through a server-side PTY.
* R4: reconnect after network loss with state kept, persistence across a client restart, chaos tests; a Docker transport if docker is usable without
  sudo (only containers/images this work creates, removed by ID afterwards); WSL is Windows-only (write it behind the transport seam, untested).
* The coloured remote badge (status bar left, theme colours `statusBarItem.remoteBackground/Foreground`); server-side Find in Files wired into the
  Search panel when the workspace is remote.
* Server bundles for linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64 (runtime packs downloaded), trimmed if trimming stays correct (measure).
* T5 (`cscode ssh://…` in background mode drops the target: forward RemoteTargets through the launcher IPC), T12's remote parts (external changes on
  remote documents are offered for reload; tab titles show `%20`).

**W4 Shell** — `src/CsCode.App/**` shell views, menus, docking, palette, Welcome; docs/SHELL.md.
* **The user's Extra:** remove the Color Theme dropdown from the main toolbar (and from View > Toolbars); it stays only in Settings
  (Settings > Color Theme, with its live preview); update the tests and docs/SHELL.md.
* Palette lists every command (T13/T18; check the list virtualises with a few hundred rows; keep the wording test); T12's palette part (a faulted
  file-list task is cached until folders change).
* Floating tool windows and documents (Dock.Avalonia floating host windows), with Float/Dock in the tab and tool-window menus; layout persistence.
* Copy with formatting (HTML with the theme's syntax colours; the clipboard service already writes `text/html`).
* Saved sessions on the Welcome page and a `session:` mode in Quick Launch (docs/SESSIONS.md).
* T14 (editor split throws inside Dock: the `FuncDataTemplate<EditorTab>` matches Dock's close-button presenters; unskip
  `VimShellTests.Split_windows_and_Ctrl_W_move_between_editor_groups`), T7/T22 (MainWindow's focus tracking: one fix so editor and panel focus keys
  are right), T4 (ThemeSwitchingCostTests preview budget), T20 (the find box's focus on `actions.find` in headless).

**W5 Startup and sessions performance** — `src/CsCode.Sessions/**`, `src/CsCode.App/Background/**`, publish props (a new `build/Publish.props`),
README.
* Warm spare OFF by default (`cscode.background.warmSpare` = false); docs and tests follow.
* ReadyToRun (composite where it helps) for the app and its modules; evaluate NativeAOT honestly (Avalonia + DI + reflection) and adopt it only if it
  works end to end; measure cold start, resident show and spare claim in Release on this machine; meet the BackgroundSessionProcessTests budgets
  or explain exactly why not.
* The publish settings `tools/install.sh` will use (self-contained linux-x64, R2R), runnable without the Nix shell (`env -i` test, as the install
  brief says). Do not install anything for the user.
* T8 (README: background mode is the default; `--foreground`).

**W6 Memory profiling** — docs/briefs/profiling.md in full (MUST 1-9 and SHOULD), now against the real merged app: `src/CsCode.Profiling/**`,
`tests/CsCode.MemoryTests/**`, `tools/profile/**`, docs/MEMORY.md, `build/MemoryDefaults.props`; minimal App wiring (AddCsCodeProfiling,
NotifyActivity from input, the Memory output channel).
* The GC/runtime matrix with real numbers (a ReadyToRun row through your own harness builds; W5 owns the app's publish props), a measured memory
  budget table for the real app (idle, 20 documents, 1 language server, a debug session, a terminal, a remote session), a real ≥ 10-minute soak.
* T23: the `ResponsivenessProbe` all modules can use, and guidance for measuring UI budgets in headless tests (input posted as dispatcher jobs, the
  headless rasteriser reported separately, as the terminal's BudgetTests do); do not rewrite other modules' tests, list which ones should move.

**W7 Debugger** — Dap adapters/installers, Panels.Debug, docs/DEBUGGING-RUST-TS.md.
* CodeLLDB: pinned download + digest, `tools/fetch-codelldb.sh`, an installer that asks the user before downloading (like `NetCoreDbgInstaller`),
  the adapter factory; run debugger-rust-typescript.md MUST 3's matrix for CodeLLDB beside GDB and record it; choose the default Rust debugger from
  that evidence (document why; keep the accepted Rust defaults).
* T3 (DebugServiceStopLocationTests: rerun alone; fix if real).

**W8 Macros** — `src/CsCode.Macros/**`, the App's Macros glue, an additive editor undo-group API.
* One Undo reverses a whole playback (an undo group held open across slices; Undo inside a macro must not throw).
* Nothing is recorded while the terminal has focus (context key `terminalFocus`), T24.
* T17/T25 docs: docs/SHELL.md "Keyboard" (pipeline tap, the terminal's key precedence) and docs/EDITOR.md (the macro hooks).

**W9 Fixes** — the owning modules, minimal edits.
* T2/T11: .NET-based language servers (marksman, csharp-ls) get `DOTNET_ROOT` when it is unset and a dotnet is found (also for the installed app
  started from the KDE menu).
* T6: the DI container's synchronous Dispose throws for IAsyncDisposable-only singletons: audit every module's singletons and fix shutdown.
* T9/T15: `IVimHost.ReadClipboard(char register)` / `WriteClipboard(char register, string text, VimRegisterType type)` overloads (defaults call the
  current members; the Vim engine tests stay green); the Vim host routes `+` through `IClipboardService` (ring, shape metadata, EOL) and `*` through
  PRIMARY.

## Common
Every builder: start from main (the terminal merge or later), commit early with plain messages (no attribution lines), never push, stay inside the
owned files and list any other edit with its reason, run its module tests plus tests/CsCode.App.Tests, and report what the user must decide.
Nine builders run at once: latency budgets fail under that load; rerun a budget test alone before calling it a real miss, and never loosen one.
