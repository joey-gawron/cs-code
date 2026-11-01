# CsCode — architecture

CsCode is a VSCodium-class code editor written in C# on .NET 10, with a native desktop UI modelled on Visual Studio 2022,
first-class LSP and DAP support, Vim emulation and macros, remote (SSH) editing, and an engineering focus on a flat memory profile
and a GUI/editing path that is isolated from language-server load.

## 1. Key decisions (and why)

| Decision | Choice | Rationale / trade-off |
|---|---|---|
| Language/runtime | C# 14 / .NET 10 | Requirement. Workstation concurrent GC, tiered PGO. |
| UI toolkit | **Avalonia 12** | Cross-platform .NET UI with native windows, native menu bars (macOS global menu / DBus on Linux), native file dialogs (`StorageProvider`), OS IME + accessibility. Controls are *rendered* by Skia rather than being OS widgets — that is what lets us reproduce the VS2022 look identically everywhere, and it is the only toolkit we can develop and test on this Linux machine. All logic lives in view-models + Core services, so a WPF/WinUI shell could be added later without touching the rest. |
| Editor control | **AvaloniaEdit** (rope-backed `TextDocument`, virtualised `TextView`) + **TextMateSharp** | Mature, MIT, large-file capable. We add: language-service UI, diagnostics squiggles, snippets, bracket/indent handling, folding, breakpoint gutter, multi-cursor (a caret layer over AvaloniaEdit's single caret, docs/EDITOR.md §7b). |
| Docking | **Dock.Avalonia** | VS-style tool windows, document well, floating/auto-hide. Styled to match VS2022. |
| LSP | Own client on **StreamJsonRpc** + System.Text.Json | Lean, no Newtonsoft; full control of lifecycle, threading and memory. |
| DAP | Own client (System.Text.Json) | Same. Default .NET adapter: Samsung **netcoredbg** (MIT) — NOT vsdbg (licence forbids non-Microsoft products). |
| **No extension system** | *Decision 2026-10-28: the VS Code extension host / marketplace compatibility was dropped.* | Removes a Node.js runtime dependency, a large API-compat surface and a security/memory risk. Customisation is native and file-based: language servers (`cscode.languageServers`), debug adapters (`cscode.debuggers`), VS Code–format **theme JSON**, **TextMate grammars**, **snippet** JSON, `keybindings.json`, `settings.json`, Vim rc + macros — all loaded from built-ins or `{UserDataDir}` by the module that owns each file kind. |
| Config formats | Same file formats/syntax as VS Code where it exists (`settings.json`, `keybindings.json`, `launch.json`, `tasks.json`, theme JSON, `language-configuration.json`, snippet JSON) | Users' existing files keep working. |
| Vim | Own engine (`CsCode.Vim`), differentially tested against real Neovim | Fidelity over "vim-ish"; includes macros (`q`/`@`). |
| Remote | Headless `CsCode.Server` + `CsCode.Remote` client over the **system `ssh`** | VS Code Remote-SSH model: workspace-bound services run next to the code. Same server doubles as a local out-of-process language host. |

## 2. Module map

```
 CsCode.App (Avalonia shell, composition root) ── CsCode.Ui (VS2022 design system, IToolWindow)
   │ hosts: CsCode.Editor (AvaloniaEdit, IntelliSense UI) · [wave 2: panels, Vim adapter, macros UI, remote UI glue]
   │
   ├── CsCode.Lsp ─┐
   ├── CsCode.Dap ─┤   workspace-bound services — hostable in-process OR in the headless CsCode.Server (remote / local process isolation)
   ├── CsCode.Platform (commands, when-clauses, config, keybindings, FS, watcher, workspace, state, logging)
   ├── CsCode.Vim (engine over IVimHost; no UI)            ├── CsCode.Remote / CsCode.Server  [wave 2]
   ├── CsCode.Testing (Test Explorer engine: .NET/cargo/Jest-Vitest providers, run controller; no UI) ── CsCode.Panels.Testing (tool window; docs/TESTING.md)
   ├── CsCode.Profiling (memory monitor, soak/leak/responsiveness harness)
   └── all depend only on ↓
                          CsCode.Core
 (UI-free contracts + leak-safe primitives: Emitter/DisposableStore, ITextDocument, ILanguageFeatureRegistry, IDiagnosticService,
  IContributionRegistry, ICommandService, IConfigurationService, IWindowService, IDebugService/IDebugSession/IBreakpointManager,
  IMemoryMonitor/IMemoryTrimmable, IColorThemeService, IKeybindingService …)
```

Dependency rules (enforced by project references, checked in review):
* `CsCode.Core` references nothing but `Microsoft.Extensions.*.Abstractions`.
* Feature modules (`Platform`, `Lsp`, `Dap`, `Vim`, `Profiling`, `Remote`, `Server`) reference **Core only** (Server references the modules it hosts). They never reference Avalonia.
* `CsCode.Editor` references Core + Avalonia/AvaloniaEdit/TextMate only. `CsCode.Ui` references Core + Avalonia.
* `CsCode.App` references everything and is the only GUI composition root (`AddCsCodeCore/Platform/Lsp/Dap/Editor/Profiling/…`).
* Modules talk to each other **only** through Core interfaces resolved from DI. That is what makes the parallel work mergeable and what lets the same Lsp/Dap code run in another process.

### Who implements which Core contract

| Core contract | Implemented by |
|---|---|
| `IDocumentManager`, `ILanguageFeatureRegistry`, `IDiagnosticService`, `IContributionRegistry`, `InMemoryTextDocument`, `Glob` | Core (done) |
| `ICommandService`, `IContextKeyService`, `IConfigurationService`, `IKeybindingService`, `IWorkspaceService`, `IFileSystem`, `IFileWatcher`, `IStateStorage`, `IAppPaths`, logging | **Platform** |
| `ILanguageServerManager` (+ native LSP providers into `ILanguageFeatureRegistry`, diagnostics into `IDiagnosticService`) | **Lsp** |
| `IDebugService`, `IDebugSession`, `IBreakpointManager`, `IDebugConfigurationService`, `ITaskService`, DAP client, netcoredbg factory | **Dap** |
| `IEditorViewFactory`, `IEditorViewController`, `IEditorDocument` impl, `ILanguageService`, `IColorThemeService` (+ VS Code theme JSON loader), TextMate wiring, user themes/grammars/snippets folders, snippets, folding, squiggles, completion/hover/signature UI, Markdown preview (`MarkdownPreviewFactory`, docs/MARKDOWN.md) | **Editor** |
| `VimEngine` over `IVimHost` (+ Vim macros) | **Vim** (adapter to the editor in wave 2) |
| `IWindowService`, `IStatusBarService`, `IOutputService`, `INavigationService`, views/view-models, VS2022 themes, menus, keymap | **Shell (App + Ui)** |
| `IMemoryMonitor`, `IMemoryTrimmable` policy, GC configuration, soak/leak/responsiveness harness | **Profiling** (+ `tests/CsCode.MemoryTests`) |
| `IRemoteConnectionManager`, `ssh:` file system provider, remote bridges | **Remote** + **Server** (wave 2) |
| `LineDiff` (Core), `IFileDecorationProvider`, `IQuickDiffProvider`, `ILineBlameProvider` | **Panels.Git** (git CLI engine + Source Control window, `docs/SOURCE-CONTROL.md`) |
| `IDiffEditorService` (diff tabs) | **Shell**, over the editor's `DiffEditorControl` (`IEditorViewFactory.CreateDiffView`) |

## 3. Language features: one registry, bridged when remote

```
 native LSP servers ──► CsCode.Lsp ──► ILanguageFeatureRegistry ──► Editor (completion, hover, squiggles…) / App (Error List, symbols, rename…)
                                  └──► IDiagnosticService ───────► Editor squiggles / Error List
 (remote workspace) CsCode.Server hosts the same Lsp ⇒ CsCode.Remote registers bridge providers in the local registry
```
Consumers ask the registry for providers for a document; multiple providers are merged or raced per feature by the consumer.
Providers are registered per **feature interface** (`Register<IHoverProvider>(selector, p)`); disposing the registration token removes it.

## 4. Customisation without extensions

* **Language servers**: built-in definitions + `cscode.languageServers` setting. **Debug adapters**: built-in netcoredbg + `cscode.debuggers`.
* **Themes**: VS Code theme JSON files in `{UserDataDir}/themes/` (and built-in VS2022 Dark/Blue/Light). **Grammars**: TextMate `.tmLanguage.json` in `{UserDataDir}/grammars/` + `languages.json`. **Snippets**: `{UserDataDir}/snippets/*.json`.
* **Keys/behaviour**: `keybindings.json`, `settings.json`, Vim rc, macros. The `IContributionRegistry` is the internal bus the owning modules publish these into (`ContributionSources.Builtin|User`).

## 5. LSP / DAP (see `docs/briefs/lsp.md`, `docs/briefs/dap.md`)

LSP: full 3.17 client — lifecycle with restart/backoff, incremental sync, dynamic registration, push+pull diagnostics, all common features,
`window/*`/`workspace/*` server requests. DAP: own client, correct handshake order, multi-session, breakpoints (source/function/exception/conditional/logpoints),
variables paging with handle invalidation, `launch.json` + variable substitution, minimal `tasks.json` runner, netcoredbg for .NET.

## 6. Threading model (binding)

The GUI and editing must never feel language-server (or debug-adapter, or network) load.

| Lane | Thread(s) | Owns |
|---|---|---|
| **UI** | Avalonia dispatcher | input, layout, render coordination, AvaloniaEdit buffer mutations (the control is not thread-safe). **Budget ≤ 4 ms per callback.** Never blocks. |
| **Editor worker** | dedicated | all *derived* per-document work from immutable snapshots: tokenisation, folding, bracket pairs, highlights, find-all, snippet parsing |
| **Language** | dedicated, `BelowNormal` | LSP client loops, (de)serialisation, mapping, change coalescing. Priority classes Interactive > Background > Idle; latest-wins per (document, feature); bounded; circuit breaker |
| **IO** | dedicated | file system, watchers, workspace search |
| **Debug** | dedicated | DAP client loops |
| **Remote** | dedicated | SSH transport, remote calls |
| **Out of process** | child processes | language servers, debug adapters (below-normal OS priority); optionally the whole language client via the local `CsCode.Server` (`cscode.languageHost=process`) for separate heap/GC |

Rules: no sync-over-async on the UI thread (banned-API analyzer); the typing path is *keystroke → buffer mutation → return*; producers → UI traffic is one-way,
coalesced (latest wins), version-tagged (stale results dropped), bounded and in small immutable batches; every slow thing is async + cancellable + timed out.
**Verification**: `ResponsivenessProbe` + hostile-server suite (CPU burner, slow, flooder, giant payloads, hanger, crash looper) with budgets
dispatcher latency p99 ≤ 16 ms / max ≤ 50 ms, typing latency p99 ≤ 20 ms, memory/queues bounded.

## 7. UI spec — Visual Studio 2022 look and feel (Shell module)

* **Chrome**: custom title bar — left: app icon + menu bar (**File Edit View Go Debug Test Terminal Tools Window Help**), centre: Quick Launch box (Ctrl+Q), right: window controls. Native menu bar on macOS/DBus global menu where available.
* **Standard toolbar**: back/forward, new/open/save/save-all, undo/redo, launch-configuration dropdown, green ▶ *Start* (F5), *Start Without Debugging* (Ctrl+F5), step buttons while debugging, find-in-files.
* **Layout (Dock.Avalonia)**: Explorer docked right (VS default), Output / Error List / Find Results / Terminal / Debug Console in the bottom tool well, Locals / Autos / Watch / Call Stack / Breakpoints / Threads shown while debugging. VS-style document tabs (preview tab italic, pin, dirty dot, split). Tool windows dock, float, auto-hide, tab-group. Layout persisted per workspace.
* **Status bar**: VS blue (`#007ACC`) normally, **orange (`#CA5100`) while debugging**, purple (`#68217A`) when no folder is open; left: ready/progress/remote indicator, right: Ln/Col, indent, encoding, EOL, language, diagnostics counts, Vim mode, memory.
* **Themes** (built-in `ColorTheme`s keyed by VS Code workbench colour ids): *Visual Studio Dark* (editor `#1E1E1E`, tool windows `#252526`, chrome `#2D2D30`/`#333337`, border `#3F3F46`, accent `#007ACC`, selection `#264F78`), *Blue*, *Light*; user theme JSON plugs in through the same pipeline.
* **Default keymap = Visual Studio** (F5, Shift+F5, Ctrl+Shift+F5, F9, F10, F11, Shift+F11, F12, Shift+F12, F2, Ctrl+K Ctrl+D, Ctrl+K Ctrl+C/U, Ctrl+., Ctrl+Space, Ctrl+Shift+Space, Ctrl+Q, Ctrl+T, Ctrl+Shift+P, Ctrl+Shift+F, Ctrl+Tab, Ctrl+Alt+L, Ctrl+`) with a "VS Code" preset; chords supported; **Vim emulation** is an optional layer on top.
* **Icons**: open-licensed Codicons (CC-BY-4.0 attribution in `THIRD-PARTY-NOTICES.md`); no proprietary VS image assets or trademarked logos.
* Everything bound to `ICommandService` ids so keybindings/menus/palette/macros/Vim share one command table.

## 8. Memory stability — engineering rules (apply to every module)

1. **Every subscription is disposable.** Cross-service notifications use `IEventSource<T>`/`Emitter<T>`; every `Subscribe`/`Register` result lives in a `DisposableStore` owned by the subscriber. No C# `event` on long-lived singletons subscribed by short-lived objects. No `static` mutable collections without a bound.
2. **Own your lifetime.** Anything per document/tab/session/server is disposed when its owner goes away; proven by a `LeakAssert` test per module.
3. **Bound every cache** (LRU/size cap) and register big ones as `IMemoryTrimmable` (tokenizer state of non-visible docs, glyph/measure caches, LSP result caches, icon bitmaps, output channels ≤ 2 MB, remote directory/content caches).
4. **No unbounded growth** in logs/output/consoles/undo stacks/registers/jumplists: ring buffers and limits.
5. **Virtualise and stream**: trees, Error List, Output, variable grids; large-file mode above a threshold.
6. **Avoid allocation in hot paths** (typing, scrolling, tokenisation, key resolution, Vim commands).
7. **Child processes are accounted for** (`IMemoryMonitor.TrackChildProcess`) and restartable.
8. **Measure, don't guess.** Budgets live in `docs/MEMORY.md` with measured numbers; `tests/CsCode.MemoryTests` holds the soak/leak/responsiveness suites; GC settings are justified by experiment.

## 9. Testing

* Unit tests per module (`tests/CsCode.<Module>.Tests`), xUnit v3. Headless UI via `Avalonia.Headless.XUnit` + Skia (screenshots via `CaptureRenderedFrame`). Shared fakes in `tests/CsCode.TestDoubles`.
* Integration against **real** tools fetched by scripts into `.scratch/tools/` (git-ignored; never commit binaries): `typescript-language-server` (npm), `netcoredbg` (GitHub release), `nvim` (Vim differential tests), `ssh`/`sshd` (remote). A test needing an unavailable tool **skips with a stated reason**.
* On Nix-provided dotnet run `source tools/nix-dev-env.sh` first.
* `dotnet build CsCode.slnx` must be **warning-free** (`TreatWarningsAsErrors`); `dotnet test CsCode.slnx` must be green.

## 10. Repository rules

* No `Co-Authored-By` trailers or AI attribution lines in commits. Short imperative subject, body optional. Commit locally; never push.
* Package versions only in `Directory.Packages.props`, inside your module's section.
* Do not commit binaries, downloaded tools, `node_modules`, screenshots from local runs, or BenchmarkDotNet output.
* Each module owns specific directories (see its brief). Don't edit another module's files; Core changes are additive only and called out in the report.
* Never read or use `~/.ssh/*` keys, agents or the real known_hosts (tests generate throwaway material in temp dirs).

## 11. Background mode & saved sessions (wave 3, see `docs/briefs/background-sessions.md`)

CsCode can stay resident: **one process per session**, coordinated by the `cscode` launcher over per-user local IPC (Unix sockets in
`$XDG_RUNTIME_DIR/cscode`, named pipes on Windows). A pre-started *warm spare* process is opt-in (`cscode.background.warmSpare`, **off** by
default: it costs an idle process of ≈100-130 MB, and resident sessions are shown just as fast without it). Closing a window hides it;
`cscode --session NAME` re-shows it in < 150 ms with documents, undo, layout, view state and language servers intact. Sessions (folders,
editors, view state, layout, hot-exit backups of unsaved buffers) persist to `{UserDataDir}/sessions`, and each session's lock (one writer)
lives there too. Hidden sessions trim memory, stop idle language servers and idle at ~0 % CPU; the number of resident sessions is capped (LRU
eviction of sessions that can quit without asking). Chosen over multi-window-per-process because Core services are workspace-scoped
singletons and per-process isolation matches §6. Details: `docs/SESSIONS.md`.

## 12. Roadmap

Wave 1 (building): Platform, LSP, DAP, Editor, Shell+Ui, Vim engine (Profiling parked — merged later). Wave 2a (building): Debug panels, Search/Settings panels. Then: integration (real composition, first real-window run). Wave 3: **background mode + saved sessions**, **debugger with Rust and TypeScript** (evaluate + fix, `docs/briefs/debugger-rust-typescript.md`), Vim↔editor adapter, general macros, remote editing (R1–R2, then R3–R4), terminal. Wave 4 (after the Rust debugger): **theme dropdown, more languages, tree-sitter evaluation** (`docs/briefs/themes-languages-treesitter.md`). Then: **clipboard support + right-click menus** (`docs/briefs/clipboard-and-context-menus.md`; can run alongside the Rust round). Then: **app icon + local install** (terminal command and start-menu entry, `docs/briefs/app-icon-and-install.md`). Later: memory profiling/soak (parked module), hostile-load responsiveness suite on the integrated app, adversarial review, polish, packaging.
