# CsCode.Dap — Debug Adapter Protocol client and debugging services

`src/CsCode.Dap` is a complete DAP client plus everything a Visual-Studio-style debugger UI binds to, with a working .NET story through
Samsung **netcoredbg** (MIT). No Avalonia, no Newtonsoft, no extension host: the protocol layer is hand-written on `System.Text.Json`,
and customisation is native and file based (`launch.json`, `tasks.json`, the `cscode.debuggers` setting).

> **Status in one paragraph.** Everything in the brief's MUST list is implemented and covered by **440 passing tests against scripted
> in-process fake adapters, real child processes (`/bin/sh`, `python3`) and real TCP/unix sockets**, plus the SHOULD items listed below.
> The **real netcoredbg end-to-end tests are written, build, and are included in the suite as `Category=RealAdapter`, but were NOT executed in
> the session that produced this module**: the netcoredbg release was downloaded and its SHA-256 verified, but this environment's permission
> system refused to *execute* the downloaded binary, and no workaround was attempted. Run them yourself with
> `tools/fetch-netcoredbg.sh && dotnet test tests/CsCode.Dap.Tests --filter Category=RealAdapter` and treat netcoredbg behaviour as unverified until then.
> See [Real-adapter tests](#real-adapter-tests-netcoredbg).

## Layout

| Path | What |
|---|---|
| `Protocol/DapModels.cs` | Hand-written DAP models (`Dap*` records, init-only, nullable, string unions) + `DapJson` options. Unknown fields/enum values never break parsing. |
| `Transport/` | `DapFrameReader/Writer` (Content-Length framing, size-capped), `ProcessTransport` (stdio), `TcpTransport`, `PipeTransport` (named pipe / unix socket). |
| `Client/DebugLane.cs`, `Client/DebugAdapterClient.cs` | The Debug lane and the DAP client (own `seq`, request/response correlation, typed event emitters, reverse requests, timeouts, cancellation). |
| `Session/` | `DebugSession : IDebugSession` (+ `BreakpointSync`, `OutputBatcher`, `VariableHandleCache`, `DebuggeeLauncher`). |
| `Breakpoints/BreakpointManager.cs` | `IBreakpointManager` (source of truth, persisted per workspace). |
| `Service/` | `DebugService : IDebugService` (+ `IDebugFrameFocus`, `IDebugOutputHistory`, `IDebugSourceContentProvider`), commands, `cscode.debuggers` contributions. |
| `Configuration/` | `DebugConfigurationService`, `LaunchJson`, `VariableResolver`, `DapPlatform` (optional Core services bundle). |
| `Tasks/TaskService.cs` | Minimal `tasks.json` runner (`ITaskService`). |
| `Adapters/` | `NetCoreDbgAdapterFactory`, `NetCoreDbgInstaller`. |
| `tools/fetch-netcoredbg.sh` | Fetches netcoredbg into `.scratch/tools/netcoredbg/` (SHA-256 verified, safe extraction). |

## Threading and isolation (binding rules, verified by tests)

* **One Debug lane** (`DebugLane`): a single dedicated, named (`CsCode.Debug`) background thread — not the thread pool — with a
  `SynchronizationContext`. All client work runs on it: frame reading, JSON, response correlation, event dispatch, variable/stack mapping and
  every session state machine. Async code on the lane resumes on the lane (deliberately no `ConfigureAwait(false)` there), so session state needs
  **no locks**. Debuggee/adapter I/O completions still come from the OS/thread pool, but the continuation hops straight back to the lane.
* **Callers only enqueue.** Every public `IDebugSession` / `IDebugService` / `IDebugConfigurationService` / `ITaskService` method is
  `lane.RunAsync(...)`: the synchronous cost on the caller thread is an allocation and a channel write, and completions are delivered
  *asynchronously* (`RunContinuationsAsynchronously`) so the caller never resumes on the lane. Property getters (`State`, `Sessions`,
  `StopLocation`, `Breakpoints.*`, …) read volatile/copy-on-write snapshots and never block.
* **Events fire on the lane.** Subscribers must be cheap and marshal to the UI themselves (the shell's `UiDispatcher`). Handler exceptions are
  isolated by `Emitter`.
* **Floods.** `output` is coalesced by `OutputBatcher` into ≤ one event per category per 16 ms window with a **bounded, drop-oldest** pending
  buffer (128 K chars, with a "[debug output flooded: N characters dropped]" marker). The reader yields to the rest of the lane every
  32 frames, so one flooding adapter cannot starve other sessions. Debuggee output spawned by `runInTerminal` goes through a bounded pump with
  at most one queued flush. The Debug Console history is a ring of ≤ 5,000 lines (≤ 4 MB).
* **Hostile payloads.** Frames larger than `MaxFrameBytes` (16 MB per session; 32 MB default for the raw client) are *skipped without
  buffering* (a short prefix is kept to fail the right request); variable pages are capped at 10,000 items even if the adapter ignores `count`;
  stack pages at 1,000 frames; module/loaded-source lists at 2,000.
* **Hung adapters.** Every request has a timeout (default 10 s; `launch`/`attach`/`configurationDone` 60 s; setting
  `cscode.debug.requestTimeoutSeconds`). A timeout fails that request with `DapRequestTimeoutException`, sets `DebugSession.IsUnresponsive`
  (+ `UnresponsiveChanged`), and any later answer/event from the adapter clears it. Nothing ever blocks a caller.
* **Child processes** start at normal priority, are killed as a *tree* on close/timeout/dispose, and are tracked (`IMemoryMonitor.TrackChildProcess`
  when available).

### Measured (this machine, Debug build, `dotnet test`; reproduce with `CSCODE_DAP_TEST_METRICS=<file>`)

| Scenario | Asserted budget | Observed |
|---|---|---|
| Caller-side sync cost of 10 session APIs while the adapter never answers | median < 1 ms, p95 < 10 ms | median ≈ 0.000 ms, p95 ≤ 0.002 ms |
| Round trip of a healthy session while another session on the same lane receives a nonstop output flood | p99 < 250 ms | 12,820 round trips in 3 s, median 0.15 ms, p99 0.9 ms |
| 1,000,000 output lines pushed as fast as the pipe takes them | < 1,000 UI batches, < 200 events/s, heap growth < 64 MB | 7 UI batches, ~306 K chars delivered, remainder dropped (drop-oldest), heap +4.7 MB |
| 60 MB "1M-element variables" response | caller sync < 1 ms, refused in place, < 30 MB allocated, connection stays usable | 0.19 ms; 15 MB (incl. the test fake's pooled pipe) |
| 50 fake sessions (start → stop at breakpoint → inspect → step → stop) | no retained session, threads ≤ +6, fds ≤ +8, heap < +24 MB | threads 20 → 21, fds 129 → 129, heap 1.5 → 2.2 MB, manager subscribers 1 → 1 |

The tests that read process-wide counters or latency budgets run in a non-parallel xUnit collection (`Serial`).

## Protocol and client

* Handshake order per spec: `initialize` → `launch`/`attach` (**not awaited**: many adapters answer only after `configurationDone`) → wait for
  `initialized` (either order relative to the `initialize` response is handled; a spec-violating adapter that never sends it gets a 2 s grace
  after a successful launch) → all breakpoints (`setBreakpoints` per file, `setFunctionBreakpoints`, `setExceptionBreakpoints`) →
  `configurationDone` → Running.
* `initialize` honestly advertises: `linesStartAt1`/`columnsStartAt1` (configurable per session; **both settings are tested**),
  `pathFormat: path`, `supportsVariableType`, `supportsVariablePaging`, `supportsRunInTerminalRequest`, `supportsMemoryReferences`,
  `supportsProgressReporting`, `supportsInvalidatedEvent`, and `supportsStartDebuggingRequest` **only when a child-session starter is wired**.
  It does not claim `supportsMemoryEvent` or `supportsArgsCanBeInterpretedByShell`.
* The whole resolved launch object goes to the adapter untouched (plus `noDebug`; `__restart` on adapter-requested restarts).
* Reverse requests: `runInTerminal` (the debuggee is spawned by the session — kinds `integrated` and `external` alike, there is no terminal UI yet —
  with stdout/stderr wired to the Debug Console, stdin closed, `env` entries with `null` removing variables, returns `processId`) and
  `startDebugging` (asks `DebugService` for a child session, `Parent` set, type inherited).
* Events handled: `stopped` (all-threads, `hitBreakpointIds`, duplicate suppression, `exceptionInfo` fetch), `continued`, `thread`, `output`
  (categories; `telemetry` dropped; source/line mapped to 0-based), `breakpoint`, `terminated` (incl. `restart`), `exited`, `invalidated`,
  `capabilities` (merged), `loadedSource`, `module`, `process`, `progressStart/Update/End`, `memory` (parsed, ignored).
* State machine `Initializing → Running ⇄ Stopped → Terminated`. Resuming is **optimistic** (state flips before the response) so a `stopped`
  that follows the response in the same read batch can never be overwritten; a failed resume rolls back only if no newer stop arrived.
* **Handles.** `variablesReference`s and frame ids are valid only for the current stop: they are tracked per stop, results are cached in a
  bounded LRU, and *everything is dropped on resume/step/`invalidated`/new stop*. Using a stale handle throws `DebugHandleInvalidatedException`
  **without contacting the adapter** (and a response that arrives after a resume is discarded the same way). The frames a session hands out
  during a stop (any thread, any page; ≤ 10,000) are remembered, so `IDebugFrameFocus.SetFocusedFrameAsync` focuses a frame however deep the
  Call Stack window paged without asking the adapter again; it **throws** (`DebugHandleInvalidatedException` for a dead frame or a running
  session, `InvalidOperationException` for an ended or no-longer-focused session) instead of returning as if focus had moved.
* **`invalidated` while stopped.** Areas `stacks`/`all` (or none) drop everything including frame ids; `variables` drops only variable data
  (references, pages, scopes), so the frames of the stop and the user's focused frame survive; `threads` alone drops nothing. Then
  `DebugService` publishes the stop location again — after every `Invalidated` subscriber has run, so the panels' trackers see the new epoch
  first: the same location (event fired even though it is equal) when its frame is still valid, otherwise it clears the location, fetches
  the stack again (re-registering the frames) and publishes a location on a live frame. A dead `FrameId` is never left published.
* Termination: graceful (`terminate` if supported and the session is a launch, else `disconnect{terminateDebuggee}` / detach for attach),
  waits `StopTimeout` (5 s), then hard kill of the adapter process tree. After `terminated` the client sends a courtesy `disconnect`.
  `RestartAsync` uses the adapter's `restart` request when advertised, otherwise stops silently and re-runs the whole handshake with the same
  configuration (no `Terminated` event in between).

### Public surface beyond the Core interfaces (all additive; nothing in Core was changed)

`DebugSession`: `IsAttach`, `IsUnresponsive`/`UnresponsiveChanged`, `Progress`, `ExceptionInfoChanged`/`LastException`, `LoadedSourceChanged`/
`LoadedSources`, `Modules`, `ExitCode`, `DebuggeeProcessId/Name`, `AdapterProcessId`, `ResolveSourceUri`, `GetSourceContentAsync`,
`GetExceptionInfoAsync`, `GetLoadedSourcesAsync`, `GetModulesAsync`, `ReadMemoryAsync`, `DisassembleAsync`, `DataBreakpointInfoAsync`,
`SetDataBreakpointsAsync`, `SetInstructionBreakpointsAsync`, `SetExpressionAsync`, `StepAsync(kind, thread, {granularity, singleThread})`
incl. `stepBack`/`reverseContinue`, `GetVariablesAsync(ref, filter, start, count)` (indexed/named paging), `Disposed`.
`DebugService`: `StartSelectedAsync(noDebug)`, `GetOutputHistory/ClearOutputHistory`, `FocusedFrameId`, `LoadUserDebuggers()`.

## Breakpoints

`BreakpointManager` is the source of truth (copy-on-write snapshots, thread-safe, debounced persistence under `debug.breakpoints` in
`StorageScope.Workspace`, flushed on dispose, reloaded when the workspace folder changes). `Toggle`/`Add` keep one breakpoint per line;
`ApplyLineShift(uri, fromLine, delta)` follows the editor's contract (docs/EDITOR.md): **every breakpoint at a line `>= fromLine` moves by `delta`**,
where `fromLine` is the first line (before the edit) whose text moves — the line after the edit position for an insertion (the edit's own line for
whole lines inserted at column 0), the first surviving line after the deleted range for a deletion. For `delta < 0` the `|delta|` lines just above it,
`[fromLine + delta, fromLine)`, vanished: their breakpoints are removed (the editor removes them itself first; the manager does it too, so a moved
breakpoint never lands on top of another). `BreakpointEditCompositionTests` (tests/CsCode.App.Tests) drives real edits in a real editor view into the
real manager. Adapter verdicts (`ReportStatus`) are kept
**per breakpoint per session**, so one session ending never erases another live session's verdict.

Each live session mirrors the manager (`BreakpointSync`): per-file replace-all `setBreakpoints` (an empty list clears a file), debounced 50 ms,
only kinds the adapter supports (conditions/hit conditions/logpoints the adapter lacks are **not sent** — they would stop where the user said
they must not — and are reported unverified with the reason), exception filters taken from the adapter's `exceptionBreakpointFilters`
(defaults applied once, user choices kept; conditions via `filterOptions`), content-based de-duplication (so adapter status updates cannot
re-trigger sends), per-session adapter-id → breakpoint-id map for `breakpoint` events. An adapter that **errors** on a source (unknown file) does
not abort the session: those breakpoints show unverified with the adapter's message.

## launch.json, variables, tasks

* `.cscode/launch.json` first, else `.vscode/launch.json` (JSONC); `configurations[]`, `compounds[]` (surfaced as configurations with
  `type: "compound"`, started concurrently, `stopAll`, `preLaunchTask`), `inputs[]`. Platform blocks (`windows`/`linux`/`osx`) deep-merge.
  File changes are watched; a broken file reports one error message and yields no configurations. `Selected` is persisted per workspace and
  refreshed when the file is edited. `CreateLaunchJsonAsync` writes the VS Code template layout (never overwrites an existing file) plus
  provider-supplied companion files (the coreclr template also writes a `tasks.json` with a default `build` task).
* Variables, applied recursively to every string value: `${workspaceFolder}`, `${workspaceFolder:name}`, `${workspaceFolderBasename}`, `${file}`,
  `${fileBasename}`, `${fileBasenameNoExtension}`, `${fileDirname}`, `${fileDirnameBasename}`, `${fileExtname}`, `${fileWorkspaceFolder}`,
  `${relativeFile}`, `${relativeFileDirname}`, `${cwd}`, `${lineNumber}` (1-based), `${selectedText}`, `${env:X}`, `${config:key}`,
  `${command:id}`, `${input:id}` (`promptString`/`pickString`/`command`), `${pathSeparator}`/`${/}`, `${userHome}`, `${defaultBuildTask}`.
  **Unknown-variable policy** (documented because VS Code is vague): a *known* variable that cannot be resolved (no editor open for `${file}`,
  undefined `${config:x}`, unknown `${input:x}`/`${command:x}`) is an **error** that aborts the start with a message; an *unknown* name such as
  `${foo}` is left verbatim (adapters have their own `${…}`); `${env:UNSET}` is the empty string; a cancelled input/command cancels the start
  quietly. `${config:…}` values may themselves contain variables (depth-limited). Each `${input:id}` is asked once per resolution.
  `${selectedText}` needs the shell to implement the optional `IActiveSelectionProvider`; without it it resolves to an empty string.
* `ITaskService`/`TaskService`: `shell` and `process` tasks, `command`, `args`, `options.cwd/env/shell`, `dependsOn` (+ `dependsOrder`),
  `group` (string or `{kind,isDefault}`), platform blocks, the same substitution, output to the `Tasks` channel (bounded, batched), exit-code
  success, cancellation kills the process tree, cycle detection. Problem matchers are accepted in any shape and ignored. Shell tasks run
  via `/bin/sh -c` (`cmd /d /c` on Windows) with POSIX quoting of args. `preLaunchTask`/`postDebugTask` use it; a failed `preLaunchTask` offers
  "Debug Anyway".

## netcoredbg and other adapters

* Built-in `coreclr` (alias `netcoredbg`) → `netcoredbg --interpreter=vscode`. Binary lookup: setting `cscode.debug.netcoredbgPath` →
  `{ToolsDir}/netcoredbg/netcoredbg` → `PATH`. If none is found the user is **offered** a download (never silent) through
  `IWindowService.ShowMessageAsync` + `WithProgressAsync`. `NetCoreDbgInstaller` resolves the latest release from the GitHub API, downloads
  the platform asset (linux-amd64/arm64, osx-arm64, win64), **verifies its SHA-256 against the digest GitHub publishes for the asset** (refuses on
  mismatch; warns and relies on archive integrity if GitHub publishes none), extracts through a zip-slip-safe extractor (rejects `..`, absolute
  paths, drive letters, symlinks/hardlinks leaving the destination; never creates devices/fifos), `chmod +x`, atomically replaces the old install.
  Concurrent starts trigger one offer and one download.
* The provider fills in `cwd` when missing, converts a textual `processId` (from `${command:pickProcess}`) to the number netcoredbg expects, and
  produces the initial **launch** (`program: ${workspaceFolder}/bin/Debug/<tfm>/<Project>.dll`, `preLaunchTask: build`, `stopAtEntry`, `console`,
  `cwd`) and **attach** (`processId: ${command:pickProcess}`) configurations; project name and target framework come from the folder's `.csproj`.
* `${command:pickProcess}` is a built-in command (`pickProcess`) listing processes through `IWindowService.ShowQuickPickAsync` (enumeration runs
  off the caller thread; never lists the editor itself).
* **User adapters, no extensions:** the `cscode.debuggers` setting (array of `{type, label, program, runtime, args, languages, windows/linux/osx,
  initialConfigurations, configurationSnippets, …}`) is published into `IContributionRegistry` as `DebuggerContribution`s
  (`ContributionSources.User`; only this module's own items are replaced on change) and their languages as `BreakpointLanguageContribution`s.
  A configuration may also carry `"debugServer": 4711` or `"host:port"` to connect to an already running adapter. `IDebugService.RegisterAdapterFactory`
  / `RegisterConfigurationProvider` remain for in-process registrations (latest registration wins).

## Commands, context keys, settings

* Commands registered (VS Code ids from `KnownCommands`; **the shell must not register handlers for these again**):
  `workbench.action.debug.start|run|stop|restart|continue|pause|stepOver|stepInto|stepOut`, `editor.debug.action.toggleBreakpoint`, plus `pickProcess`.
* Context keys (only if `IContextKeyService` resolves): `debugState` (`inactive|initializing|running|stopped`), `inDebugMode`, `debugType`,
  `debuggerAvailable`, `focusedSessionIsAttach`, `breakpointsExist`.
* Settings registered with `IConfigurationService`: `cscode.debuggers`, `cscode.debug.netcoredbgPath`, `cscode.debug.requestTimeoutSeconds`.

## Integration notes

* `services.AddCsCodeDap()` registers a shared `DebugLane`, `BreakpointManager`/`IBreakpointManager`, `ITaskService`, `IDebugConfigurationService`,
  `IDebugService` (and the same instance as `IDebugFrameFocus`, `IDebugOutputHistory`, `IDebugSourceContentProvider`) and the built-in
  netcoredbg adapter. Every Core platform service it can use is **optional** (`DapPlatform.From(sp)` uses `GetService`).
  **Resolve `IDebugService` once at startup**: its constructor registers the commands, `${command:pickProcess}` and the context keys.
* Additive Core files: `Core/Tasks/ITaskService.cs`; `Core/Debugging/DebugExtras.cs` (`IDebugFrameFocus`, `IDebugOutputHistory`,
  `IDebugSourceContentProvider`, `IDebugSessionHealth`, `IActiveSelectionProvider`, `DebugSourceUri`). They are *new interfaces* rather than new
  members of `IDebugService`, so existing implementers and fakes are not broken.
* Editor ↔ debugger: the editor calls `IBreakpointManager.ApplyLineShift` on edits and reads `IDebugService.StopLocation`; frame selection goes
  through `IDebugFrameFocus.SetFocusedFrameAsync`; `debug:/<session>/<ref>/<name>` documents are read-only virtual sources
  (`IDebugSourceContentProvider`). Ended sessions are replaced by a lightweight `EndedSession` in the console history so 5,000 retained lines
  can never pin a dead session.

## Limits and known gaps

* **netcoredbg has not been executed by this module's author** (see the status paragraph): its handshake order, logpoint support and exact
  `stopped`/`exited` sequencing are handled generically (both `initialized` orders, capability-gated logpoints, `exited`+`terminated`), and the
  real-adapter tests are written to be run by you, but nothing here proves they pass against 3.2.0-1092.
* `serverReadyAction` (open a URL when an output pattern appears) is not implemented; the connect-retry loop of `TcpTransport` covers "wait for the port".
* `${selectedText}` needs the shell's `IActiveSelectionProvider`; `INavigationService` exposes only the caret.
* `runInTerminal` has no terminal UI: debuggee stdin is closed (EOF) and output is plain text in the Debug Console. A `console: "externalTerminal"` window is not opened.
* Shell tasks quote arguments POSIX-style; VS Code's `quoting` options and `isBackground` task readiness are not implemented. Auto-detected
  task providers (npm, tsc…) are out of scope (no extensions).
* A flood beyond the 128 K-chars/16 ms window drops the *oldest* text by design; the console shows a dropped-characters marker.
* Single-thread-only resumes fire `Continued` once locally and may fire a second (duplicate) event if the adapter also sends `continued`.
* Windows named pipes (`DebugAdapterNamedPipe`) and the Windows/macOS netcoredbg builds are implemented but only exercised on Linux here.

## Tests (`tests/CsCode.Dap.Tests`, xUnit v3)

```
dotnet test tests/CsCode.Dap.Tests --filter "Category!=RealAdapter"     # 440 tests: fakes, real processes/sockets, threading, leaks
dotnet test tests/CsCode.Dap.Tests --filter Category=RealTools          # SDK build of the sample project only (never starts netcoredbg)
tools/fetch-netcoredbg.sh && dotnet test tests/CsCode.Dap.Tests --filter Category=RealAdapter   # the real netcoredbg end-to-end suite
```

* **Fakes** (`Fakes/FakeDebugAdapter.cs`): a scriptable in-process adapter over bounded in-memory pipes (handshake order, stall/delay/crash,
  events, reverse requests, oversized frames); `TcpFakeAdapterHost` serves one per connection over **real loopback TCP** for service tests.
* **Real processes/sockets:** `ProcessTransportTests`, `SocketTransportTests`, and `ProcessAdapterTests` run the real `DebugSession` over real stdio against a small
  Python DAP adapter (skips with a reason if `python3` is missing): graceful stop, hard kill after timeout, crash, startup failure (adapter stderr
  surfaced), no leftover PIDs after 20 start/stop cycles.
* **Threading/isolation** (`ThreadingTests`), **leaks/soak** (`LeakSoakTests`, `LeakAssert` on 50 sessions; spawned debuggees never outlive their
  session), **golden** tests for launch.json/variables, tasks via real `/bin/sh`, archive safety (zip-slip/tar-slip/symlink), installer with a fake
  GitHub (tampered archive rejected, no residue on failure).
* `Assert.Skip*` is used with explicit reasons (`python3`/`/bin/sh`/netcoredbg/`dotnet` missing, non-POSIX).

### Real-adapter tests (netcoredbg)

`RealNetCoreDbgTests` (trait `Category=RealAdapter`) writes a tiny C# console project to a temp dir, builds it with `dotnet build`, then drives
`IDebugService` end-to-end: breakpoint → `Stopped(breakpoint)` at the right file/line → top frame `Main` → locals `x == 51`, `s` → `EvaluateAsync("x + 1")`
→ step into/over/out → continue to exit with the program's exit code 7 → no `netcoredbg`/debuggee PIDs left; conditional and hit-count breakpoints;
logpoint output (skips if the adapter does not advertise logpoints); exception filter + `exceptionInfo`; and 10 start/stop cycles with PID, thread and
`LeakAssert` checks. They skip with an explicit reason when `.scratch/tools/netcoredbg/` (or `CSCODE_NETCOREDBG`) or `dotnet` is missing.
`RealAdapterSampleTests` (trait `RealTools`) verifies the sample builds and its line markers resolve, without starting netcoredbg.
