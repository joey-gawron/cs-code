# CsCode.Lsp — native LSP 3.17 client

`CsCode.Lsp` is a complete, memory-lean language-server client. Any stdio (or TCP / named-pipe / unix-socket) language server works by
adding a `LanguageServerDefinition`. There is **no extension system** in CsCode: servers are configured with built-in definitions plus the
`cscode.languageServers` setting, nothing else.

It implements `ILanguageServerManager`, registers one provider per feature interface into `ILanguageFeatureRegistry`, publishes diagnostics
into `IDiagnosticService`, and depends on Core interfaces only (no Avalonia, no UI thread assumptions, no static state), so the same code can later
run inside the headless `CsCode.Server`.

```
 IDocumentManager events ─► LanguageServerManager ──routes by (definition, root)──► ServerSlot ──supervises──► LanguageClient (one session)
                                  │                                                   restart/backoff/Faulted       │ JsonRpc (StreamJsonRpc)
                                  │                                                                                │ FeatureTable → providers → ILanguageFeatureRegistry
   cscode.languageServers ────────┘                                                                                │ DocumentSync / diagnostics / progress / ...
                                                                       LanguageWorkQueue ("Language lane": dedicated BelowNormal threads) ◄┘
```

## 1. Protocol types (`CsCode.Lsp.Protocol`)

Approach: **generated, committed C#** from the official `metaModel.json` (3.17) by `tools/lspgen/lspgen.py` (`python3 tools/lspgen/lspgen.py`;
downloads the model into `.scratch/lspgen/` when missing; `--check` reports staleness). Output: `src/CsCode.Lsp/Protocol/Generated/*.g.cs`
(~11k lines, 324 structures, 37 enums, every non-proposed request/notification). Hand-written support is `OneOf.cs` and `LspJson.cs`.

* **System.Text.Json only**, one `LspJson.Options` (null-omitting, explicit property names). No Newtonsoft.
* Structures are `sealed record`s; `extends`/`mixins` are **flattened** into each record. Optional members are nullable and omitted when null;
  *required-but-nullable* members (`processId`, `rootUri`…) are always written. Missing required members deserialize tolerantly (no `required`
  keyword): real servers are sloppy.
* **Unions** are `OneOf<…>` (2–4 arms). The converter picks the arm by JSON token kind (no parsing needed in the common case) and, when several
  arms share a token kind (`TextEdit | InsertReplaceEdit`, `DocumentSymbol[] | SymbolInformation[]`, `Command | CodeAction`), by each arm's generated
  `MatchesShape` (required members *including nested required members*, string-literal discriminators such as `kind:"create"`).
  Unions of inline literal shapes (`TextDocumentFilter`, `TextDocumentContentChangeEvent`…) are merged into one all-optional record.
* **Enums**: integer enums are C# enums (unknown numbers pass through); string enums are open `readonly record struct`s (unknown values round-trip).
* `LSPAny` = `JsonElement`; URIs stay `string` on the wire and become `System.Uri` in the mappers; `uinteger[]` is `uint[]` (semantic-token data is
  zero-copy into Core's `IReadOnlyList<uint>`).
* `LspRequests.*` / `LspNotifications.*` are typed descriptors (`LspRequest<TParams,TResult>`), `LspMethods.*` are the method names,
  `LspRegistrationOptionTypes.ByMethod` maps methods to registration-option types.

Tests: `ProtocolRoundTripTests` (spec shapes, unions, optional/null handling, unknown enum values), `BoundedStructuresTests` (framing guard, caches, encodings).

## 2. Transport and processes

* StreamJsonRpc `JsonRpc` + `HeaderDelimitedMessageHandler` + `SystemTextJsonFormatter`. **Findings that shaped the design** (measured, see tests):
  StreamJsonRpc sends `$/cancelRequest` on cancellation but completes the local task *only when the server answers*, so every wait is bounded with
  `Task.WaitAsync(token)`; it throws `RemoteMethodNotFoundException` (a sibling of `RemoteInvocationException`) for unknown methods; send order is
  preserved (40,000 notifications, single and multi-threaded under a lock).
* Endpoints (`LanguageServerDefinition.Command`): a program (resolved on `PATH`, then the per-user install directories of `cscode.lsp.searchDirs`
  — `~/.nix-profile/bin`, `~/.cargo/bin`, `~/.local/bin`, `~/go/bin`, `~/.dotnet/tools`, … which a desktop-launched editor's PATH usually lacks —,
  then `{ToolsDir}/lsp/**`, i.e. `node_modules/.bin`, `<tool>/bin`), `tcp://host:port` / `socket://host:port` (retries ~3 s), `pipe://name`
  (named pipe), `unix:///path` (unix domain socket). `ServerLaunch.Resolve` does the lookup (testable without a process); a built-in definition whose
  command and args are unchanged also tries its **alternatives** in order (`nil` → `nixd`, `erlang_ls` → `elp server`, …) and the not-found message
  names every executable it looked for. `${workspaceFolder}` / `${userHome}` expand in `args` and `initializationOptions` (`LaunchVariables`).
  A command with a directory part must be absolute or start with `~/`: a relative one (`./bin/x`, `bin/x`) is **not** found — it used to be resolved
  against the working directory, i.e. the server's root inside the workspace, so the opened repository chose the program (lsp-core-1).
* Child processes (`ProcessConnection`): started **without a shell**, env + cwd from the definition, stderr drained on its own task into a bounded
  64-line ring and the bounded server log, below-normal priority, whole tree killed on dispose. Every child gets an environment marker
  (`CSCODE_LSP_SESSION`); on Linux a kill also finds **re-parented descendants** (server `kill -9`ed, grandchild orphaned) by scanning `/proc/*/environ`
  for that marker and waits (≤ 2 s) until they are really gone. Only processes carrying our own marker are ever killed.
* .NET servers (`DotnetLaunchEnvironment`, applied after the definition's `env`): a framework-dependent .NET program (csharp-ls from
  `dotnet tool install -g`, fsautocomplete, …) finds its runtime through `DOTNET_ROOT_<ARCH>`, then `DOTNET_ROOT`, then the default install
  locations, and a dotnet from Nix, `~/.dotnet` or a tarball is in none of them. (1) The arch-specific variable of CsCode's own architecture is
  folded into `DOTNET_ROOT` (moved when `DOTNET_ROOT` is unset, dropped when both name the same directory, left alone when they differ):
  `dotnet run` and `dotnet test` set it for CsCode's own apphost, and since it outranks `DOTNET_ROOT` it overrode the `DOTNET_ROOT` nixpkgs'
  marksman wrapper sets for the .NET 9 runtime marksman needs (the T2/T11 failure). (2) When `DOTNET_ROOT` is still unset and the server is a
  .NET program (a known command: csharp-ls, marksman, fsautocomplete, omnisharp, Microsoft.CodeAnalysis.LanguageServer, dotnet; an apphost
  with its `.dll` or `.runtimeconfig.json` beside it, also through symlinks; a `dotnet tool` shim beside `.store/`), it gets the root
  (the directory with `host/fxr`) of the dotnet on the server's PATH, then in `cscode.lsp.searchDirs`, then `~/.dotnet`, then the shared
  runtime CsCode runs on, and the server log says so. A definition whose `env` sets either variable is never changed.
  **Only when the host's own lookup would not do** (lsp-core-5): with `DOTNET_ROOT` set, the Linux host looks nowhere else, so a distro or
  global runtime (`/etc/dotnet/install_location[_<arch>]`, else `/usr/share/dotnet`, `/usr/local/share/dotnet` on macOS) that served the server
  is left to the host when it has the frameworks the server's `.runtimeconfig.json` asks for (beside the apphost; for a tool shim, the one beside
  the `.dll` whose path `dotnet tool install` wrote into the shim, i.e. the target framework the shim really runs — when that path cannot be read,
  every runtimeconfig of the newest `.store` version must be satisfied, since a tool packed for net8.0 and net9.0 has one per framework;
  roll-forward policy honoured, `DOTNET_ROLL_FORWARD` too) — or when nothing says what it asks for. Otherwise the first candidate above that has
  those frameworks is chosen (or the first one, said so in the log line, which names the root and the reason).
* Per-message size guard (`FramingGuardStream`, `cscode.lsp.maxMessageBytes`, default 64 MB): a header announcing more drops the connection (the
  session is then restarted by the normal crash path). Parsing rents ~9× a message's size from shared pools, so this bounds a hostile payload.

## 3. Threading and isolation (binding rules, all tested)

*"LSP must never slow down editing or the GUI."* This project has no Avalonia reference and never assumes a UI thread.

| Rule | Implementation | Verified by |
|---|---|---|
| Callers pay only an enqueue | every provider method → `LanguageClient.DocRequestAsync` → `LanguageWorkQueue.RunAsync` (allocate + enqueue); version/selector checks are field reads | `ThreadingTests.Every_provider_method_costs_the_caller_under_100_microseconds…` (25 provider methods, throwing `SynchronizationContext`, lane and server saturated); hostile suite asserts p99 < 1 ms per call |
| Typing never waits | `OnChanged` = append under a tiny lock + one enqueue per burst; no text copies, no serialisation, no wait on the lane lock | `Typing_costs_the_editing_thread_almost_nothing…` (< 50 µs overhead per keystroke while the lane is stuck) |
| Dedicated lane | `LanguageWorkQueue`: N threads (`cscode.lsp.workerThreads`, default 2), `BelowNormal` + per-thread `nice` on Linux, **not** the thread pool; delegates run on a lane thread until their first real await and re-enter with `ctx.ResumeOnLane()` | `LanguageWorkQueueTests`, `Document_text_is_only_ever_read_on_lane_threads…`, `Diagnostics_are_decoded_and_published_on_lane_threads` |
| Priorities | strict `Interactive` > `Background` > `Idle` (see the table in `LanePriority`) | `Strict_priority…` |
| Latest wins per (document, feature) | a newer item cancels an older one before it is sent, or via `$/cancelRequest` if in flight; didChange bursts coalesce into one message | `Latest_request_wins…`, `A_burst_of_edits_coalesces…` |
| Stale results dropped | document version captured at call time; checked before sending and again after the response | `A_response_for_an_older_document_version_is_dropped` |
| Bounded everything | hard queue cap (shed Idle → Background → oldest Interactive), pending-change cap (→ full resync), bounded mailboxes (diagnostics 512 files, log 2048 lines, progress 1024), capped results (`maxCompletionItems`, `maxReferences`, `maxWorkspaceSymbols`, `maxSemanticTokens`, 2000 diagnostics/file) | `A_flood_of_requests_cannot_grow_the_queue…`, `The_reader_never_blocks_on_a_stuck_consumer…` |
| Backpressure | optional work (`ShouldShed`) is skipped *without enqueueing* when the lane is backed up; the reader loop never blocks (handlers only enqueue) | `Background_features_are_skipped_without_enqueueing…` |
| Timeouts | Interactive 3 s, Background 30 s (`interactiveTimeoutMs`, `backgroundTimeoutMs`) → `null` + non-blocking status message | `A_slow_server_yields_null_at_the_timeout…` |
| Circuit breaker | 5 consecutive timeouts (`breakerThreshold`) → state `Unresponsive` (Core enum member added), requests answered `null` immediately, one probe per interval (active read-only probe loop), automatic recovery | `Consecutive_timeouts_mark_the_server_Unresponsive…` |
| OS-level isolation | servers start at below-normal priority via the `nice` binary (grandchildren inherit it; a post-start `setpriority` would race their creation) / `PriorityClass` on Windows; `cscode.lsp.processPriority`; RSS of the server's whole process tree (tsserver included, lsp-core-4) tracked via `IMemoryMonitor.TrackChildProcess` with restart over `RestartAboveBytes` (typescript, vue, svelte: 4 GiB = VS Code's 3072 MB `maxTsServerMemory` heap of the semantic tsserver + 1 GiB for the rest of the tree, whose RSS counts the node runtime once per process, T39; `restartAboveBytes: null` turns it off). The cap restarts a server at most 3 times (a status-bar message and a log line each time, the crash history kept); the 4th time it is no longer enforced: the server keeps running, one warning offers Restart, and a restart or a new `restartAboveBytes` re-arms it (`RestartCapTests`); **optional CPU cap** `cscode.lsp.cpuQuotaPercent` via a transient `systemd-run --user --scope -p CPUQuota=N%` (probed once; falls back with a log line) | `Cpu_burning_children_run_at_low_priority…`, `The_optional_cpu_quota…` (checks `cpu.max`), `Server_runs_below_normal_priority_including_its_tsserver_child` |
| In-flight work dies with its document | per-document version token cancelled (on the lane) whenever the text changes | `Editing_the_document_cancels_its_in_flight_requests` |

One honest deviation: StreamJsonRpc's reader loop runs on the thread pool; it only frames and `JsonDocument.Parse`s. All typed (de)serialisation, mapping,
coalescing and result handling run on the lane, and the reader never waits for the lane.

### Hostile-server suite (`tests/CsCode.Lsp.Tests/Hostile/`)
A node peer (`hostile-server.js`) runs as a **real child process** next to a healthy fake server and misbehaves: CPU-burning children, answers after 5 s,
100 MB of `logMessage`/`publishDiagnostics` flood, 100 MB of stderr, 50k-item completion + 1M-token semantic result, never answers, crash loop (with orphaned
grandchildren), oversized message. Each asserts: caller-side cost < 1 ms, bounded memory/queue, the healthy server unaffected, breaker/restart behaviour,
and that nothing (process, thread, orphan) is left behind.

## 4. Session lifecycle (`LanguageClient`) and supervision (`ServerSlot`)

`initialize` (honest capabilities, built by `ClientCapabilitiesFactory`; `workspaceFolders`, `initializationOptions`, `trace`) → `initialized` → Running →
`shutdown` + `exit` (bounded by `shutdownTimeoutMs`) → kill of the process tree. A `Starting→Running` transition is atomic, so a connection lost during
start-up can never leave a dead session marked Running.

`ServerSlot` = "server X for root Y": owns the documents that belong to it (a restarted session re-opens them), the restart policy (**at most
`maxRestarts` (5) crashes per `restartWindowSeconds` (180 s)** with exponential backoff `restartBackoffMs`, then `Faulted` + `LastError` and **one**
`IWindowService` message with a "Restart" action), the "binary not found" state (`Stopped` + one friendly message with an install hint, never retried
automatically) and the user-stop rule (a stopped server is not auto-started by new documents until `StartAsync`).

## 5. Document synchronisation

`didOpen/didChange/didSave/willSave/willSaveWaitUntil/didClose` from `IDocumentManager` events, only for documents matching the definition's selector
and ≤ `maxDocumentBytes` (10 MB). Honours `None/Full/Incremental` (and the detailed `TextDocumentSyncOptions`, static or dynamic).

* Incremental changes are built straight from `TextDocumentChangedEventArgs` (spans of the edit, never the document); runs of typing merge into one change.
* Versions are `ITextDocument.Version`, monotonic. A request flushes the document's pending changes **under the same per-document lock that initiates
  the send**, so the server never sees a request before the text it refers to.
* Safety nets: a full-text resync (with duplicate suppression: edits already inside the sent text are not sent again) happens after a pending-change
  overflow, when sync was `None`/disabled and is turned on dynamically, for non-UTF-16 servers, or if text could not be read consistently.
* Language-id change = close + open (or just close if the selector no longer matches). Edits racing `didOpen` are folded into the opened text.
* **Contract for the editor:** every text change must fire `ITextDocument.Changed` (a reload without an event is invisible to the server; re-register
  the document instead).
* `willSave*`: the shell calls `IWillSaveParticipant.OnWillSaveAsync` (resolve `IEnumerable<IWillSaveParticipant>` from DI) before saving; a hung server can
  delay a save by at most 1.5 s.

### Position encoding
Default **UTF-16** (identity: ~2 ns, never touches line text). `cscode.lsp.positionEncoding = "auto"` offers `utf-8, utf-16, utf-32`; a server that
picks another encoding gets converted positions for open documents and full-text `didChange` (incremental ranges can't be converted after the fact).
Measured (Release, 80-char line, ns per conversion): UTF-16 2; UTF-8/UTF-32 on ASCII ~5 after the vectorised ASCII fast path (190 before), on mixed
text 85–145 — plus a line-text fetch from the document. Decision: UTF-16 by default, `auto` is opt-in. Limit: ranges in *closed* files keep the
server's columns.

## 6. Capability matrix

Providers are registered **per feature interface**, only while the server advertises the capability (statically at `initialize` or dynamically).

| Feature | Status | Notes |
|---|---|---|
| completion (+resolve, trigger chars, `isIncomplete`, snippets, `textEdit`/`insertReplaceEdit` (insert range), additionalTextEdits, commit chars, label details, item defaults) | implemented | capped at `maxCompletionItems` (then `IsIncomplete`); resolve echoes the original item (weak side table, no explicit cache) |
| hover, signatureHelp (+retrigger, label offsets) | implemented | |
| definition / declaration / typeDefinition / implementation | implemented | `Location`, `Location[]`, `LocationLink[]` |
| references, documentHighlight | implemented | references capped at `maxReferences` |
| documentSymbol (hierarchical + flat fallback), workspaceSymbol | implemented / partial | `workspaceSymbol/resolve` is not called: range-less results map to an empty range |
| codeAction (+resolve, `isPreferred`, kinds, disabled) | implemented | diagnostics round-trip with their server-private `data` |
| rename (+prepareRename) | implemented / partial | `defaultBehavior:true` → empty range (the editor picks the word) |
| formatting / rangeFormatting / onTypeFormatting | implemented | |
| foldingRange, selectionRange | implemented | |
| semanticTokens full + **delta** + **range** | implemented | `ISemanticTokensProvider` (+ additive `ISemanticTokensRangeProvider`); previous result cached (bounded, `IMemoryTrimmable`); capped at `maxSemanticTokens`; coloured over the grammar by the editor (docs/EDITOR.md §7e; range requests for the viewport of documents over 5000 lines) |
| inlayHint | implemented | label parts (tooltip, location, command) mapped; `inlayHint/resolve` through Core's `IInlayHintsProvider.ResolveInlayHintAsync` (lang-2): the client offers `resolveSupport` for `tooltip`, `label.tooltip`, `label.location`, `label.command` (not `textEdits`: hints are not inserted), and a server with `resolveProvider` (rust-analyzer) sends them on resolve, which echoes the server's own hint (kept weakly beside the Core one, `data` included) |
| codeLens (+resolve) | implemented | drawn above their lines by the editor, resolved near the viewport, commands through `workspace/executeCommand` or the editor's mapping of VS Code's reference commands (docs/EDITOR.md §7e) |
| documentColor | implemented / partial | `colorPresentation` not implemented |
| linkedEditingRange, callHierarchy, typeHierarchy | implemented | additive Core interfaces; UI: linked editing in the editor (docs/EDITOR.md §7d), the Hierarchy window (docs/NAVIGATION.md) |
| `workspace/executeCommand` bridge | implemented | `executeCommandProvider.commands` registered into `ICommandService` (optional dependency) |
| diagnostics: `publishDiagnostics` (version-checked), pull (`textDocument/diagnostic`, `workspace/diagnostic`) | implemented | cleared on close (push-only servers) and on stop/crash |
| `willRenameFiles` / `didRenameFiles` / `didCreateFiles` / `didDeleteFiles` | implemented | `IFileOperationParticipant`, filters honoured |
| documentLink (+resolve) | implemented | additive Core `IDocumentLinkProvider` (stage 3, C); each link keeps the server's link in `Data` for `documentLink/resolve`; at most 5000 per document; the editor underlines a link while Ctrl/⌘ is held over it and opens it on Ctrl/⌘+Click (docs/C-CPP.md §3) |
| moniker, inlineValue, `willCreateFiles`/`willDeleteFiles`, notebook documents, proposed features (inline completion…) | not implemented | out of scope |
| partial results / client-initiated `workDoneToken` | not implemented | the client never sends partial-result tokens |

Server → client: `window/logMessage`, `$/logTrace`, `window/showMessage` (rate-limited, 5 per 10 s), `window/showMessageRequest`, `window/showDocument`,
`window/workDoneProgress/create` + `$/progress` (coalesced → `IWindowService.WithProgressAsync`, cancel → `window/workDoneProgress/cancel`),
`workspace/configuration` (definition `Settings` first, then `IConfigurationService`), `workspace/didChangeConfiguration` push, `workspace/workspaceFolders`
(+ change notifications for definitions without root markers), `workspace/applyEdit`, `client/registerCapability`/`unregisterCapability` (every feature above
plus `didChange`/`didSave`/`didOpen`/`didClose`/`willSave*`, watched files, executeCommand, diagnostics, file operations), `workspace/*/refresh`
(a refresh re-registers the provider so consumers re-query), `telemetry/event` (ignored). Unknown requests answer `MethodNotFound`.

## 7. Manager

* **Definitions**: built-ins (`BuiltinLanguageServers.Catalog`, 36 servers: typescript-language-server, csharp-ls, pyright-langserver, clangd,
  rust-analyzer, gopls, vscode-json/html/css-language-server, bash-language-server, yaml-language-server, lua-language-server, jdtls,
  kotlin-language-server, Metals, clojure-lsp, zls, SourceKit-LSP, HLS, ElixirLS / Lexical, erlang_ls / ELP, ocamllsp, nil / nixd, Taplo, terraform-ls,
  docker-langserver, cmake-language-server, sqls, buf / protols, graphql-lsp, Marksman, ruby-lsp, Phpactor, Dart, vue-language-server, svelteserver —
  each with `RootMarkers`, per-platform `InstallHints`, its formatter and alternatives; the full table, generated by a test, is in `docs/LANGUAGES.md`),
  `AddDefinition` (returns a removal token), and the **`cscode.languageServers`** override, a JSON object keyed by id:
  `command, args, env, selector, initializationOptions, settings, rootMarkers, autoStart, workingDirectory, restartAboveBytes, enabled` (an unknown id with
  `command` + `selector` defines a new server). Changing launch properties restarts a running server; selector-only changes don't.
  An entry's `settings` and `initializationOptions` objects are read like a settings.json of their own: their **top-level** dotted keys are paths
  (`"rust-analyzer.check.command": "clippy"` answers `workspace/configuration` for `rust-analyzer` with `{"check":{"command":"clippy"}}`, merged
  with a nested spelling of the same object), and every member name below the top level is kept as written, so URLs and globs survive
  (`"yaml.schemas": {"https://…/x.json": "*.yml"}`). Before lsp-core-2 every member name was split at every depth, which broke those
  (`DefinitionOverrides.ExpandTopLevelKeys`; `Top_level_dotted_keys_of_a_user_s_server_settings…`, `A_server_settings_object_splits_only…`).
  **`command`, `args`, `env` and `workingDirectory` come from user settings only** (machine-scoped members, docs/PLATFORM.md "Setting scopes"), as does
  `cscode.lsp.searchDirs`: a workspace's `.vscode`/`.cscode` settings.json may tune a server (`selector`, `settings`, `initializationOptions`,
  `rootMarkers`, `enabled`, `autoStart`, `restartAboveBytes`) but cannot define what is started; its value is ignored with a notice.
* **One server per (definition, workspace root)**: root = nearest ancestor containing a marker (names or globs like `*.sln`), never leaving the workspace
  folder that contains the file; fallback = that folder, then the file's directory. Built-ins whose build systems nest markers (jdtls, Kotlin, Metals,
  HLS, Elixir, Erlang, ocamllsp, Nix, buf) take the **outermost** marked directory inside the workspace folder instead (`RootStrategy.Outermost`; a
  user `rootMarkers` override means nearest again). Definitions *without* markers get one server serving all workspace folders
  (multi-root aware, `didChangeWorkspaceFolders`). Root probing is file-system I/O, so routing runs on the lane, never on the thread that fired the document event.
* `Start/Stop/Restart/StopAll`, `ServersChanged`; `Dispose`/`DisposeAsync` stops everything (including stops already in flight) and kills the process trees.
  The lifecycle calls hop onto the Language lane before doing anything blocking (process creation/teardown), so a shell calling them from the UI thread
  pays one enqueue (`Manager_start_stop_and_restart_never_run_blocking_work_on_the_calling_thread`).
* Lazy start on the first matching opened document when `AutoStart`.
* The in-process manager serves `file:` and `untitled:` documents only: documents of other schemes (`ssh:` remote workspaces) are served by the language
  host next to their files and bridged by CsCode.Remote (docs/REMOTE.md), so a remote file never starts a local server. With `cscode.languageHost =
  process` this manager is not constructed at all: the same code runs in a local `cscode-server` child.

## 7a. Server extensions (one class per server: rust-analyzer now, clangd and Roslyn next)

What a server's own VS Code extension adds on top of plain LSP — experimental capabilities, its commands, its custom notifications, members it
puts in standard answers — CsCode builds in as one `LanguageServerExtension` subclass per server (`src/CsCode.Lsp/Extensions/`), listed in
`ServerExtensions.CreateBuiltins()`. There is no extension *system*: these are compiled in, one class each.

**Lifetimes.** One extension instance per manager. `Register(host)` runs once when the manager is built: commands and other app-lifetime items go
in `host.Disposables` (released with the manager), so a command exists before any server runs and, when invoked, asks the server for the active
editor (`host.ActiveSession(this)`, `host.SessionFor(this, uri)`, `host.Sessions(this)`). One `ServerExtensionSession` per running server (per
workspace root), created by `LanguageClient` when the server starts:
* `Attach(session)` runs before the connection listens — the only time `session.OnNotification(method, handler, latestWins)` can add a
  handler (StreamJsonRpc refuses new methods afterwards). A handler gets a copy of the params (`default`, kind `Undefined`, when there are none)
  on the Language lane, in the order the server sent them, one at a time (a per-session queue drained by one lane item: the lane has several
  threads; at most 4,096 wait, the oldest dropped beyond); `latestWins` drops a queued one when a newer one of the same method arrives
  (status-like notifications). `session.OnRequest(method, (params, ct) => Task<object?>)` answers a
  server-to-client request the same way (on the lane; its result is the response, a throw an error response; null once the server stopped).
* Client-side commands a server names in code actions, lenses or hovers (rust-analyzer's `runSingle`, Roslyn's `roslyn.client.*`) are
  ordinary commands the extension registers in `Register(host)`; the editor runs them through `ICommandService` with the server's arguments.
* `AddExperimentalCapabilities(experimental)` fills `initialize`'s `capabilities.experimental`.
* `OnInitialized(session)` after the handshake (lane); `OnStopped(session)` when the server stops (crash, restart, shutdown), after which the
  session's `Disposables` are released, its `State` slot cleared and its notification sinks detached; the client drops the session, so a stopped
  server keeps nothing of the extension alive (`A_stopped_server_releases_its_session…`).
* `session.RequestAsync(method, params)` / `RequestAsync(method, document, (id, mapper) => params)` (the document's pending edits reach the server
  first; no feature registration gates it; never superseded by a later call) return the raw answer or null (not running, null answer, error,
  timeout — logged); `Notify`, `Mapper` (Core ⇄ server positions), `Log` (the server's Output channel), `Post` (run on the lane).

**Hooks into standard answers** (all on the Language lane, all optional): `WantsRawHover` + `AdjustHover(session, raw, hover, uri)` (members
the protocol has no slot for, e.g. rust-analyzer's hover `actions`); `AdjustCodeLenses(session, lenses, uri)` (drop, keep or rewrite);
`RewritesAnswerOf(method)` + `RewriteAnswer(session, method, raw)` (rewrite a typed request's raw answer before it is deserialised, e.g.
snippet text edits). A hook that throws is logged in the server's Output channel ("<name> <hook> failed: …") and the session goes on as if the
extension had not been there for that call.

**Threading.** Command handlers run on the caller's thread (the UI thread from a key or the palette) and only enqueue; everything that maps or
parses runs on the Language lane. **Adding one** (stage 3's clangd, stage 5's Roslyn): a class deriving from `LanguageServerExtension` whose
`AppliesTo` matches the built-in definition's id, plus one line in `CreateBuiltins()`; tests drive it through
`new LspTestEnv(extensions: [new MyExtension()])` against the fake server (`ServerExtensionTests` is the worked example) and a real-server suite.

**Read-only results** (`ReadOnlyDocuments`, registered by `AddCsCodeLsp`): `Put(producer, kind, fileName, text)` returns
`cscode-virtual://results/<kind>/<producer>/<fileName>` to open with `INavigationService.OpenAsync`. It is the `results` provider of Core's
virtual documents (the scheme library and generated code use, docs/CSHARP.md §2): the editor opens it like a file and refuses edits, the
language comes from the file name, the tab says what it is (`vec!.rs [expanded]`), and session restore skips it; the 32 most recent are kept.
Until the C# merge this was a scheme of its own (`lsp-readonly:`); see docs/CSHARP.md §2 for why the two were folded into Core's. Additive `LspServices` members for extensions: `StatusBar` (`IStatusBarService`), `ReadOnlyDocuments`, lazy `Debug` (`IDebugService`)
and `TaskTerminals` (`ITaskTerminalHost`).

## 8. Settings

| Key | Default | |
|---|---|---|
| `cscode.languageServers` | – | definition overrides (above) |
| `cscode.lsp.maxDocumentBytes` | 10485760 | larger documents are not synced |
| `cscode.lsp.maxMessageBytes` | 67108864 | larger server messages drop the connection |
| `cscode.lsp.trace` | `off` | `off` \| `messages` \| `verbose` (into the server's Output channel `"<DisplayName> (LSP)"`, bounded) |
| `cscode.lsp.workerThreads` | 2 | lane threads (applies to the next manager) |
| `cscode.lsp.processPriority` | `belowNormal` | `belowNormal` \| `normal` \| `idle` |
| `cscode.lsp.cpuQuotaPercent` | 0 | Linux CPU cap per server tree (systemd user scope) |
| `cscode.lsp.maxCompletionItems` / `maxReferences` / `maxWorkspaceSymbols` / `maxSemanticTokens` | 5000 / 10000 / 5000 / 250000 | result caps |
| `cscode.lsp.interactiveTimeoutMs` / `backgroundTimeoutMs` / `initializeTimeoutMs` / `shutdownTimeoutMs` | 3000 / 30000 / 60000 / 3000 | |
| `cscode.lsp.positionEncoding` | `utf-16` | `auto` to negotiate utf-8/utf-32 |
| `cscode.lsp.searchDirs` | per platform (Linux: `~/.nix-profile/bin`, `/nix/var/nix/profiles/default/bin`, `~/.cargo/bin`, `~/.local/bin`, `~/go/bin`, `~/.dotnet/tools`, `~/.local/share/coursier/bin`, `~/.ghcup/bin`) | directories searched for server executables after PATH (`~` and `%VAR%` expand); `[]` = PATH and the tools directory only |
| `cscode.lsp.queueCapacity`, `breakerThreshold`, `breakerProbeMs`, `maxRestarts`, `restartWindowSeconds`, `restartBackoffMs` | 4096, 5, 5000, 5, 180, [500,1000,2000,4000,8000] | |

## 8b. C# (stage 5, docs/CSHARP.md)

* **Roslyn** (`roslyn`, `src/CsCode.Lsp/CSharp/`) is C#'s default server, csharp-ls the alternative: `solution/open` / `project/open` after
  `initialized`, ready on `workspace/projectInitializationComplete`, its client-side commands (`roslyn.client.nestedCodeAction`,
  `fixAllCodeAction`, `completionComplexEdit`), toasts. Built on the per-server extension pattern (§7a): `RoslynExtension`, its per-server
  state `RoslynSession`, its commands `RoslynCommands`.
* **`cscode.lsp.serverChoice`** (generic): which built-in of a group of alternatives runs (`{"csharp": "roslyn"}`; `BuiltinServer.Group`,
  `BuiltinServer.EnabledByDefault` picks the group's default: unset, C# runs Roslyn). An explicit `cscode.languageServers.<id>.enabled` still wins.
* **Pull diagnostics with several identifiers.** A server may register several `textDocument/diagnostic` providers, each with an identifier
  (Roslyn does): every one is pulled, and each publishes under its own owner (`lsp:<id>#<identifier>`). The client kept only the last one before.
* **Library and generated code** (`ExternalDocuments`): a location a server's source classifies (csharp-ls `csharp:`, Roslyn's MetadataAsSource
  files and `roslyn-source-generated:`) becomes a Core virtual document (`cscode-virtual://lsp/…`), synced back to that server under its own URI.
* Launch variables `${cscodeLogsDir}` and `${cscodePid}` in a definition's `args`.

## 9. Core changes made by this module (all additive)
* `Core/Languages/LanguageModels.cs` + `LanguageProviders.cs` (lang-2): `InlayHint.LabelParts` / `InlayHint.CanResolve`, `InlayHintLabelPart`, and
  `IInlayHintsProvider.ResolveInlayHintAsync` (default: the hint as it is).
* Stage 5: `Core/Text/VirtualDocuments.cs` (`VirtualDocumentUri`, `VirtualDocumentKinds`, `IVirtualDocumentProvider`, `IVirtualDocumentService`,
  `VirtualDocumentService`, registered by `AddCsCodeCore` as the `cscode-virtual` scheme's `IFileSystemProvider`).
* `Core/Languages/LanguageModels.cs`: `MarkupContent.IsTrusted` (lang-2): `command:` links of trusted content run their command (CsCode's own
  content only, never a server's text: rust-analyzer's hover actions).
* `Core/Languages/LanguageServers.cs`: `LanguageServerState.Unresponsive` appended to the enum. **A `switch` expression over the enum without a
  discard arm in other modules will need a case.**
* New file `Core/Languages/AdditionalLanguageProviders.cs`: `ISemanticTokensRangeProvider`, `ILinkedEditingRangeProvider` (+`LinkedEditingRanges`),
  `ICallHierarchyProvider`, `ITypeHierarchyProvider` (+`HierarchyItem`, call records), `IWillSaveParticipant` (+`DocumentSaveReason`), `IFileOperationParticipant`.

## 10. Memory discipline
Every `Subscribe`/`Register` lives in a `DisposableStore`/token owned by the session; per-document state is released on close; providers/clients are
collectable after stop (`LeakAndSoakTests`: weak references after dispose, 2,000-document churn flat on threads/fds/memory, 30× start/stop). Caches are
bounded (semantic-token delta base: 32 documents / 32 MB, `IMemoryTrimmable` when an `IMemoryMonitor` exists; completion/code-action/lens originals are
weak side tables). The idle lane thread holds no result between items. Known transient: while a multi-megabyte message is parsed, System.Text.Json /
StreamJsonRpc keep rented `ArrayPool` buffers (~9× the message) that the runtime trims on its own schedule; `maxMessageBytes` caps the size of any one message.

## 11. Tests and how to run them
```
dotnet test tests/CsCode.Lsp.Tests          # everything (parallelisation is disabled on purpose: timing/thread assertions need a quiet process)
tools/fetch-lsp-tools.sh                    # installs typescript-language-server + typescript into .scratch/tools/lsp (idempotent, local, git-ignored)
tools/fetch-lsp-tools.sh --with-csharp      # additionally csharp-ls (dotnet tool)
python3 tools/lspgen/lspgen.py              # regenerate the protocol types
```
Real-server tests (`RealTypeScriptServerTests`, and `RealZlsAndNilServerTests` for the zls and nil binaries found on PATH or in `cscode.lsp.searchDirs`:
initialize, diagnostics, hover, formatting, below-normal priority, shutdown with no process left, all with a private HOME/XDG so the user's configuration
and caches are never touched) and the hostile suite SKIP with an explicit reason when `node`/the tools are missing. `RealStoreServerTests` runs ten more
built-in definitions against servers found in the Nix store (marksman, docker-langserver, sqls, terraform-ls, svelteserver, graphql-lsp, taplo, and the
alternatives nixd, protols and elp), reached only through `cscode.lsp.searchDirs`: initialize, launch line, priority, diagnostics where the server checks
syntax on open, shutdown, no process left; each skips when its package is not in the store. `BuiltinServerTests` cover every built-in
definition without running it (launch line, alternatives, search directories, install hints, the not-found path through the real process factory, root
detection per server layout, initialization options). `DotnetLaunchEnvironmentTests` cover the .NET launch rules on fake installations, through
the real process factory (the server's `/proc/<pid>/environ`), and for real: csharp-ls installed as a dotnet tool started with neither
`DOTNET_ROOT` nor a dotnet on PATH (as from the desktop menu; a control run shows the shim alone fails), and nixpkgs' marksman started by a CsCode
that inherited `DOTNET_ROOT_<ARCH>`. The fake-server tests use an
independent in-proc peer (`Harness/FakeLspServer`: own framing over `System.IO.Pipelines`, document mirror applying real LSP change semantics).

## 12. Known limits
`workspaceSymbol/resolve` is not exposed; non-UTF-16 encodings don't convert ranges in closed files; `colorPresentation`,
notebooks and proposed features are absent; `Dispose()` (sync) blocks up to 15 s — prefer `DisposeAsync` off the UI thread; the manager starts
observing documents when `ILanguageServerManager` is first resolved (resolve it at startup).
