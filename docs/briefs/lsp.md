# Module brief — LSP (`CsCode.Lsp`)

**Owns:** `src/CsCode.Lsp/**`, `tests/CsCode.Lsp.Tests/**`, `tools/fetch-lsp-tools.sh` (+ any generator under `tools/lspgen/`), `docs/LSP.md`, the *LSP* section of `Directory.Packages.props`.
**Implements (Core):** `ILanguageServerManager`; produces providers for `ILanguageFeatureRegistry` (`Core/Languages/LanguageProviders.cs`) and diagnostics for `IDiagnosticService`.
**Consumes (Core, via DI):** `IDocumentManager`, `ILanguageFeatureRegistry`, `IDiagnosticService`, `IWorkspaceService`, `IConfigurationService`, `IWindowService`, `IOutputService`, `INavigationService`
(for `workspace/applyEdit`), `IFileWatcher`, `IFileSystem`, `ILanguageService` (optional), optional `IMemoryMonitor`, `ILoggerFactory`. In tests use the doubles in `CsCode.TestDoubles`.
**Does not touch:** Avalonia/UI, other modules.

## Goal
A complete, robust, memory-lean native LSP 3.17 client. Any stdio language server (typescript-language-server, clangd, pyright, rust-analyzer, gopls, csharp-ls, …) works by adding a `LanguageServerDefinition`.

## MUST
1. **Protocol types** (`CsCode.Lsp.Protocol`, System.Text.Json, **no Newtonsoft**). Choose the approach that gives fully-typed, correct models with the least hand-maintenance and document it in `docs/LSP.md`.
   *Recommended:* a small generator (`tools/lspgen`, C# or Python) driven by the official `metaModel.json`
   (`https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/metaModel/metaModel.json`) that emits **committed** C# records/enums, with a `OneOf`-style struct + `JsonConverter` for
   union types, string-or-int enums with unknown-value tolerance, `LSPAny` as `JsonElement`. Hand-writing the subset you use is acceptable if generation proves too costly — but then cover every feature in the Feature list below.
2. **Transport & RPC**: StreamJsonRpc (`HeaderDelimitedMessageHandler` + `SystemTextJsonFormatter`) over child-process stdio; also TCP and named-pipe/unix-socket transports (`socket://host:port` style definition field or an additive optional `Transport` on your own options type).
   Child process management: start without a shell, env + cwd from the definition, capture stderr to the Output channel, kill the whole process tree on dispose/exit, never leave zombies, handle `exit`/crash.
3. **`LanguageClient`** — full lifecycle: `initialize` (honest client capabilities for exactly the features you implement; `workspaceFolders`; `initializationOptions`; `trace`), `initialized`, normal operation, `shutdown` → `exit` with timeouts then kill;
   crash detection + **restart with backoff** (max 5 restarts / 3 minutes → `Faulted` state with `LastError`, surfaced through `LanguageServerInfo` and a single non-spammy `IWindowService` message with a "Restart" action);
   request **cancellation** (`$/cancelRequest`, `CancellationToken` plumbing, `ContentModified`/`ServerCancelled`/`RequestCancelled` handling incl. retry-for-ContentModified where the spec says so);
   **dynamic registration** (`client/registerCapability`/`unregisterCapability` incl. document selectors) — providers appear/disappear in the registry accordingly.
4. **Document sync**: `didOpen/didChange/didSave/willSave/willSaveWaitUntil/didClose` driven by `IDocumentManager` events for documents matching the definition's selector; `TextDocumentSyncKind` None/Full/Incremental honoured; incremental changes built directly from
   `TextDocumentChangedEventArgs` (convert UTF-16 positions ↔ the negotiated `positionEncoding` utf-8/utf-16/utf-32); **coalesce** bursts, preserve order, versions monotonic; language-id change → close+open; ignore documents larger than a threshold (setting `cscode.lsp.maxDocumentBytes`, default 10 MB).
5. **Features** — provider classes registered **per feature interface** only when the server advertises the capability (statically or dynamically), mapping LSP ⇄ Core models:
   completion (+`completionItem/resolve`, trigger characters, `isIncomplete` re-query, `InsertTextFormat`, `textEdit`/`insertReplaceEdit`, additionalTextEdits, commit chars, label details, item defaults, snippets), hover, signatureHelp (+ retrigger), definition/declaration/typeDefinition/implementation (Location | Location[] | LocationLink[]),
   references, documentHighlight, documentSymbol (hierarchical + flat `SymbolInformation[]` fallback), workspaceSymbol, codeAction (+resolve, `isPreferred`, kinds, disabled), rename (+prepareRename), documentFormatting / rangeFormatting / onTypeFormatting,
   foldingRange, selectionRange, semanticTokens (full + delta + range; legend exposed via `ISemanticTokensProvider.Legend`), inlayHint (+resolve), codeLens (+resolve), documentColor (+colorPresentation optional), `workspace/executeCommand` bridge (register the server's `executeCommandProvider.commands` into `ICommandService` **if** an `ICommandService` is resolvable — optional dependency).
6. **Diagnostics**: `textDocument/publishDiagnostics` (drop results whose `version` is older than the document's) and **pull diagnostics** (`textDocument/diagnostic`, `workspace/diagnostic` where supported) → `IDiagnosticService.Set(uri, owner: "lsp:<definitionId>", …)`; clear on document close for push-only servers and on server stop; map tags/related information/codeDescription.
7. **Server → client requests/notifications**: `window/showMessage`, `window/showMessageRequest` (actions → `IWindowService.ShowMessageAsync`), `window/showDocument` (→ `INavigationService.OpenAsync`), `window/logMessage` + `$/logTrace` (→ Output channel `"<DisplayName> (LSP)"`, **bounded**),
   `window/workDoneProgress/create` + `$/progress` (→ `IWindowService.WithProgressAsync`/status bar), `workspace/configuration` (answer from `IConfigurationService` by section; plus `Settings` in the definition), `workspace/didChangeConfiguration` push on config change,
   `workspace/workspaceFolders` (+ change notifications from `IWorkspaceService.FoldersChanged`), `workspace/applyEdit` (→ `INavigationService.ApplyWorkspaceEditAsync`, return `applied`), `workspace/didChangeWatchedFiles` (dynamic registrations → `IFileWatcher`), `client/registerCapability`, `telemetry/event` (ignored).
8. **`LanguageServerManager : ILanguageServerManager`** — definitions registry (`AddDefinition` returns a removal token); lazy start on the first matching opened document if `AutoStart`; **one server per (definition, workspace root)** with root detection from `RootMarkers` (walk up from the file; fall back to the workspace folder;
   multi-root aware); `Start/Stop/Restart/StopAll` + `ServersChanged`; shutdown of everything when the host disposes; definitions overridable from the setting `cscode.languageServers` (JSON object keyed by id: `command`, `args`, `env`, `selector`, `initializationOptions`, `settings`, `enabled`).
   Resolve `Command` via `PATH` and `{ToolsDir}/lsp/**` (`IAppPaths.ToolsDir`); a missing binary → state `Stopped` + one friendly message with install hint (not an exception storm).
9. **Built-in definitions** (data only, in `BuiltinLanguageServers`): `typescript-language-server --stdio` (typescript, typescriptreact, javascript, javascriptreact), `csharp-ls` (csharp), `pyright-langserver --stdio` (python), `clangd` (c, cpp, objective-c), `rust-analyzer` (rust),
   `gopls` (go), `vscode-json-language-server --stdio` (json, jsonc), `vscode-html-language-server --stdio` (html), `vscode-css-language-server --stdio` (css, scss, less), `bash-language-server start` (shellscript), `yaml-language-server --stdio` (yaml), `lua-language-server` (lua).
   Each with `RootMarkers` (e.g. `tsconfig.json`, `package.json`, `*.sln`, `*.csproj`, `Cargo.toml`, `go.mod`, `compile_commands.json`, `pyproject.toml`) and an install hint string.
10. **Memory discipline**: result caches (semantic-token previous result, completion item data, code-lens) bounded and registered as `IMemoryTrimmable` if `IMemoryMonitor` is present; no per-keystroke full-text allocations (incremental changes are spans of the edit, not the document);
    in-flight requests for a document are cancelled when it changes; trace logging off by default (`cscode.lsp.trace`: off | messages | verbose); `Dispose` of a client releases pipes, tasks, timers and every registry registration.
11. `AddCsCodeLsp(this IServiceCollection)` registers `ILanguageServerManager` (singleton, hosted by an `IDisposable`/started when `IDocumentManager` exists) and the built-in definitions.

## SHOULD
* `positionEncoding` negotiation (prefer utf-8 when the server supports it and the document is mostly ASCII — measure before deciding), `workspace/willRenameFiles`/`didRenameFiles`, `textDocument/linkedEditingRange`, `callHierarchy`/`typeHierarchy` models (no UI yet) behind a small additive Core interface if cheap, `notebookDocument` NOT required.
* A `docs/LSP.md` capability matrix (implemented / partial / not implemented).

## Tests (`tests/CsCode.Lsp.Tests`, xUnit v3)
1. **In-process fake server** over `System.IO.Pipelines` duplex streams (or `Nerdbank.Streams` if already transitively present — otherwise Pipelines) exercising every feature end-to-end through the registry (request mapping, response mapping, cancellation, dynamic registration, version-stale diagnostics, incremental sync content, crash + restart/backoff, shutdown sequence). ≥ 60 tests.
2. **Real server integration**: `tools/fetch-lsp-tools.sh` installs `typescript-language-server` + `typescript` into `.scratch/tools/lsp/` with npm (network, idempotent). Tests: open a real `.ts` file in a temp project; assert **diagnostics** appear for a type error; **completion** contains an expected member; **hover** text; **definition** across two files; **references**; **rename** `WorkspaceEdit` correct; **formatting**; **document symbols**; edit-then-diagnostics-update; kill -9 the server → restart → features work again; `StopAllAsync` leaves no child process. Skip with reason when node/npm/network is unavailable.
   Optionally also `csharp-ls` via `dotnet tool install --tool-path .scratch/tools/lsp csharp-ls` for a C# smoke test (skip if unavailable).
3. **Leak/soak tests** (`LeakAssert` + counters): open/change/close 2,000 documents against the fake server; start/stop a server 30×; assert no retained clients/providers, thread count and process handle count stable, registry empty afterwards, registered `IDisposable`s all released.
4. Protocol-type round-trip tests against the spec's own examples (unions, optional fields, unknown enum values).

## Done when
MUST all implemented + green incl. the real typescript-language-server integration test actually run (report whether it ran or skipped), `docs/LSP.md` written, committed.
