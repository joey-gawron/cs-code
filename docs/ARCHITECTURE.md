# CsCode — architecture

CsCode is a VSCodium-class code editor written in C# on .NET 10, with a native desktop UI modelled on Visual Studio 2022,
first-class LSP and DAP support, and compatibility with VS Code extensions (Open VSX), engineered for a flat memory profile.

## 1. Key decisions (and why)

| Decision | Choice | Rationale / trade-off |
|---|---|---|
| Language/runtime | C# 14 / .NET 10 | Requirement. Workstation concurrent GC, tiered PGO. |
| UI toolkit | **Avalonia 12** | Cross-platform .NET UI with native windows, native menu bars (macOS global menu / DBus on Linux), native file dialogs (`StorageProvider`), OS IME + accessibility. Controls are *rendered* by Skia rather than being OS widgets — that is what lets us reproduce the VS2022 look identically everywhere. The only toolkit that lets us develop and test on this Linux machine. All logic lives in view-models + Core services, so a WPF/WinUI shell could be added later without touching the rest. |
| Editor control | **AvaloniaEdit** (rope-backed `TextDocument`, virtualised `TextView`) + **TextMateSharp** | Mature, MIT, large-file capable. We add: LSP-driven features, diagnostics squiggles, snippets, bracket/indent handling, folding, breakpoint gutter. Known gap: multi-cursor (stretch). |
| Docking | **Dock.Avalonia** | VS-style tool windows, document well, floating/auto-hide. Styled to match VS2022. |
| LSP | Own client on **StreamJsonRpc** + System.Text.Json | Lean, no Newtonsoft; full control of lifecycle/memory. |
| DAP | Own client (System.Text.Json) | Same. Default .NET adapter: Samsung **netcoredbg** (MIT) — NOT vsdbg (licence forbids non-Microsoft products). |
| Extensions | Out-of-process **Node.js extension host** (`host/extension-host`) speaking JSON-RPC to C#, exposing a `vscode` API shim; manifests + declarative contributions parsed in C# | JS extensions need a JS runtime; the host is the only non-C# runtime code. Process isolation = a bloated/crashing extension can't corrupt or bloat the editor; the host is restartable. Marketplace = **Open VSX** (Microsoft's marketplace ToS forbids third-party clients). |
| Config / keybindings / when-clauses | Same file formats and syntax as VS Code (`settings.json`, `keybindings.json`, `launch.json`, `package.json` contributes) | Compatibility. |

### Honest scope statement
Full VS Code API parity is not a one-session goal (the API is ~1,500 members). The extension host implements a tiered subset
(Tier 1 = what `vscode-languageclient`, themes, snippets, grammars, commands, status bar, output channels, configuration,
debug adapter factories and diagnostics need). `docs/EXTENSION-COMPAT.md` (owned by the Extensions module) lists, per API,
status = implemented / stubbed / unsupported, and is the contract with users.

## 2. Module map

```
                         ┌──────────────────────────── CsCode.App (Avalonia shell, composition root) ───────────────────────────┐
                         │  VS2022 chrome · docking · menus · tool windows · palettes · debug UI · extensions view · themes      │
                         └──────┬───────────┬───────────┬────────────┬─────────────┬──────────────┬───────────────┬────────────┘
                                │           │           │            │             │              │               │
                          CsCode.Editor  CsCode.Lsp  CsCode.Dap  CsCode.Extensions  CsCode.Platform  CsCode.Profiling   (all depend only on ↓)
                                └───────────┴───────────┴────────────┴─────────────┴──────────────┴───────────────┘
                                                                    CsCode.Core
                       (UI-free contracts + leak-safe primitives: Emitter/DisposableStore, ITextDocument, ILanguageFeatureRegistry,
                        IDiagnosticService, IContributionRegistry, ICommandService, IConfigurationService, IWindowService,
                        IDebugService/IDebugSession/IBreakpointManager, IMemoryMonitor/IMemoryTrimmable, IColorThemeService …)
```

Dependency rules (enforced by project references, checked in review):
* `CsCode.Core` references nothing but `Microsoft.Extensions.*.Abstractions`.
* Feature modules (`Platform`, `Lsp`, `Dap`, `Extensions`, `Profiling`) reference **Core only**. They never reference each other and never reference Avalonia.
* `CsCode.Editor` references Core + Avalonia/AvaloniaEdit/TextMate only.
* `CsCode.App` references everything and is the only composition root (`AddCsCodeCore/Platform/Lsp/Dap/Extensions/Editor/Profiling`).
* Modules talk to each other **only** through Core interfaces resolved from DI. That is what makes the parallel work mergeable.

### Who implements which Core contract

| Core contract | Implemented by |
|---|---|
| `IDocumentManager`, `ILanguageFeatureRegistry`, `IDiagnosticService`, `IContributionRegistry`, `InMemoryTextDocument`, `Glob` | Core (done) |
| `ICommandService`, `IContextKeyService`, `IConfigurationService`, `IWorkspaceService`, `IFileSystem`, `IFileWatcher`, `IStateStorage`, `IAppPaths`, keybinding service, logging | **Platform** |
| `ILanguageServerManager` (+ native LSP providers into `ILanguageFeatureRegistry`, diagnostics into `IDiagnosticService`) | **Lsp** |
| `IDebugService`, `IDebugSession`, `IBreakpointManager`, `IDebugConfigurationService`, DAP client, netcoredbg adapter factory | **Dap** |
| `IExtensionService`, manifest loader, VSIX/Open VSX install, Node host + `vscode` shim, contributions → `IContributionRegistry`, ext-registered providers → `ILanguageFeatureRegistry` | **Extensions** |
| `IEditorViewFactory`, `IEditorViewController`, `IEditorDocument` impl, `ILanguageService`, `IColorThemeService` (+ VS Code theme JSON loader), TextMate wiring, snippets, folding, squiggles, completion/hover/signature UI | **Editor** |
| `IWindowService`, `IStatusBarService`, `IOutputService`, `INavigationService`, all views/view-models, VS2022 themes, menus, keymap, debug UI | **App** |
| `IMemoryMonitor`, `IMemoryTrimmable` policy, GC configuration, soak/leak harness, benchmarks | **Profiling** (+ `tests/CsCode.MemoryTests`) |

## 3. Language features: one registry, two producers

```
 native LSP servers ──► CsCode.Lsp ───────────┐
                                               ├──► ILanguageFeatureRegistry ──► Editor (completion, hover, squiggles…) / App (Error List, symbols, rename…)
 JS extensions ──► ext host ──► CsCode.Extensions ┘                      ▲
                                                IDiagnosticService ◄── both
```
Consumers ask the registry for providers for a document; multiple providers (e.g. a native server + an extension) are merged or raced
per feature by the consumer. Providers are registered per **feature interface** (`Register<IHoverProvider>(selector, p)`); disposing
the registration token removes it.

## 4. Extension host (Extensions module)

* One Node process per window (`node host/extension-host/main.js`), started lazily on the first activation event; Node discovered
  from `cscode.extensionHost.nodePath` → `PATH`; clear error UX if missing. No npm runtime dependencies in the host.
* Transport: JSON-RPC 2.0, `Content-Length` framed, over the child's stdio (stderr = log). C# side uses StreamJsonRpc.
* The host injects a `vscode` module (hooked into `Module._load`) implementing the API subset in §1. Extensions run unmodified
  (`require('vscode')`, bundled `vscode-languageclient`, etc.).
* Document sync: C# pushes `IDocumentManager` events (open/change/close/save) to the host; the host keeps `TextDocument` mirrors.
* Providers registered in JS (`languages.registerCompletionItemProvider`…) become C# provider objects that proxy over RPC and map
  results to Core models. LSP-shaped JSON on the wire to keep mapping mechanical.
* Declarative contributions (`languages`, `grammars`, `themes`, `snippets`, `keybindings`, `commands`, `menus`, `configuration`,
  `debuggers`, `jsonValidation`) are read in **C#** from `package.json` (with `package.nls.json` localisation) — they work without
  starting Node at all, so themes/grammars/snippets cost zero extension-host memory.
* Memory: host RSS tracked via `IMemoryMonitor.TrackChildProcess`; restart on cap breach; `--max-old-space-size` set.
* Security: extensions are arbitrary code, as in VS Code. Install is explicit; VSIX signature verification is out of scope
  (documented). The host never gets credentials from the C# side.

## 5. LSP (Lsp module)

StreamJsonRpc over child stdio (also TCP/pipe). Full lifecycle (initialize → initialized → … → shutdown/exit, crash restart with backoff),
incremental `didChange`, capability negotiation, dynamic registration, `workDoneProgress`, `window/*` → `IWindowService`,
`workspace/configuration`, `applyEdit`, file watching (`workspace/didChangeWatchedFiles`), pull + push diagnostics, and these features:
completion (+resolve), hover, signatureHelp, definition/declaration/typeDefinition/implementation, references, documentHighlight,
documentSymbol/workspaceSymbol, codeAction (+resolve), rename (+prepare), formatting/range/onType, foldingRange, selectionRange,
semanticTokens (full/delta), inlayHint, codeLens, documentColor. One server instance per (definition, workspace root).

## 6. DAP (Dap module)

Own DAP client: framing, request/response correlation, events, reverse requests (`runInTerminal`, `startDebugging`), capability handling,
launch/attach, breakpoint sync (source/function/exception/conditional/hit-count/logpoints), threads/stack/scopes/variables/evaluate/setVariable/completions,
step/continue/pause/restart/terminate, multi-session (child sessions). `launch.json` (JSONC) with variable substitution and compounds.
Adapters come from (a) built-in factories (netcoredbg for `coreclr`), (b) extension `contributes.debuggers`, (c) extension-registered factories via the host.

## 7. UI spec — Visual Studio 2022 look and feel (App module)

* **Chrome**: custom-styled title bar with the VS2022 layout — left: app icon + menu bar (**File Edit View Go Debug Terminal Tools Extensions Window Help**), centre: Quick Launch search box (Ctrl+Q), right: window controls. Native menu bar on macOS/DBus global menu where the platform provides it.
* **Standard toolbar**: back/forward, new/open/save/save-all, undo/redo, launch-configuration dropdown, green ▶ *Start* (F5), *Start Without Debugging* (Ctrl+F5), step buttons while debugging, find-in-files.
* **Layout (Dock.Avalonia)**: Solution/Folder Explorer docked right (VS default), Output / Error List / Find Results / Terminal / Debug Console in the bottom tool well, Locals / Autos / Watch / Call Stack / Breakpoints / Threads auto-shown bottom while debugging, Properties optional. Document well in the centre with VS-style tabs (preview tab italic, pin, dirty dot, close-others, split). Tool windows support dock, float, auto-hide, tab-group. Layout persisted per workspace.
* **Status bar**: VS blue (`#007ACC`) normally, **orange (`#CA5100`) while debugging**, purple when no folder is open; left: ready/progress/branch, right: Ln/Col, spaces/tab, encoding, EOL, language mode, diagnostics counts, memory.
* **Themes** (built-in, registered as `ColorTheme`s keyed by VS Code workbench color ids): *Visual Studio Dark* (editor `#1E1E1E`, tool windows `#252526`, chrome `#2D2D30`/`#333337`, border `#3F3F46`, accent `#007ACC`, selection `#264F78`), *Blue* (VS2022 Blue palette), *Light*. VS Code theme extensions plug in through the same pipeline (`IColorThemeService`).
* **Default keymap = Visual Studio** (F5 start, Shift+F5 stop, Ctrl+Shift+F5 restart, F9 breakpoint, F10 step over, F11 step in, Shift+F11 step out, F12 go to definition, Shift+F12 find references, F2 rename, Ctrl+K Ctrl+D format document, Ctrl+K Ctrl+C / Ctrl+K Ctrl+U comment/uncomment, Ctrl+. quick actions, Ctrl+Space complete, Ctrl+Shift+Space parameter info, Ctrl+Q quick launch, Ctrl+T go to all, Ctrl+, go to all, Ctrl+Shift+P command palette, Ctrl+Shift+F find in files, Ctrl+Tab switch documents, Ctrl+Alt+L explorer, Ctrl+` terminal). A "VS Code" keymap preset ships as an alternative. Chords supported.
* **Icons**: open-licensed icon set (Codicons, CC-BY-4.0 attribution in `THIRD-PARTY-NOTICES.md`); no proprietary VS Image Library assets.
* Everything bound to `ICommandService` ids from `KnownCommands` so keybindings/menus/palette/extensions share one command table.

## 8. Memory stability — engineering rules (apply to every module)

1. **Every subscription is disposable.** Cross-service notifications use `IEventSource<T>`/`Emitter<T>`; every `Subscribe`/`Register` result is
   added to a `DisposableStore` owned by the subscriber. No C# `event` on long-lived singletons subscribed by short-lived objects. No `static` mutable collections without a bound.
2. **Own your lifetime.** Anything created per document/tab/session/server is disposed when that document/tab/session/server goes away. Closing a document must
   leave nothing reachable: proven by a `WeakReference` leak test per module (helper in `CsCode.Profiling`).
3. **Bound every cache** (LRU/size cap) and register big ones as `IMemoryTrimmable` so the monitor can shed them under pressure (tokenizer line states for
   non-visible documents, glyph/measure caches, LSP result caches, icon bitmaps, output channel text ≤ 2 MB).
4. **No unbounded growth in logs/output/consoles/undo stacks**: ring buffers, undo size limits, truncated debug-console history.
5. **Prefer streaming/virtualisation**: tree views, Error List, Output, variable grids all virtualise; file reads stream; large files switch to a "large file mode"
   (no tokenisation/folding/LSP) above a threshold.
6. **Avoid allocation in hot paths** (typing, scrolling, tokenisation, diagnostics refresh): spans, pooled buffers, no LINQ in per-keystroke code, no per-frame closures.
7. **Child processes are accounted for** (`IMemoryMonitor.TrackChildProcess`) and restartable.
8. **Measure, don't guess.** Budgets live in `docs/MEMORY.md` with the measured numbers; `tests/CsCode.MemoryTests` has the soak + leak suites; GC settings in
   `runtimeconfig` are justified by experiment.

## 9. Testing

* Unit tests per module (`tests/CsCode.<Module>.Tests`), xUnit v3. Headless UI tests via `Avalonia.Headless.XUnit` + Skia (screenshots via `CaptureRenderedFrame`).
* Integration tests use **real** tools fetched by scripts into `.scratch/tools/` (git-ignored; never commit binaries): `typescript-language-server` (npm), `netcoredbg` (GitHub release), real Open VSX extensions. Tests that need a tool are skipped with a clear reason when it's absent offline.
* On Nix-provided dotnet run `source tools/nix-dev-env.sh` first (see file header).
* `dotnet build CsCode.slnx` must be **warning-free** (`TreatWarningsAsErrors`); `dotnet test CsCode.slnx` must be green.

## 10. Repository rules

* No `Co-Authored-By` trailers or AI attribution lines in commits. Short imperative subject, body optional. Commit locally; never push.
* Package versions only in `Directory.Packages.props`, inside your module's section.
* Do not commit binaries, downloaded tools, `node_modules`, screenshots from local runs, or BenchmarkDotNet output.
* Each module owns specific directories (see its brief). Don't edit another module's files; if you need a Core contract change, make the *additive* change (new members/files only, never rename/remove), keep it minimal, and call it out in your final report.
