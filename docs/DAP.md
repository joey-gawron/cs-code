# CsCode.Dap — Debug Adapter Protocol client and debugging services

`src/CsCode.Dap` is a complete DAP client plus everything a Visual-Studio-style debugger UI binds to, with a working .NET story through
Samsung **netcoredbg** (MIT), Node.js/TypeScript through Microsoft's **js-debug** and Rust (and C/C++) through **GDB's native DAP** (the
default on Linux) or **CodeLLDB** (the default on macOS). No Avalonia, no Newtonsoft, no extension host: the protocol layer is hand-written on `System.Text.Json`,
and customisation is native and file based (`launch.json`, `tasks.json`, the `cscode.debuggers` setting).

> **Status in one paragraph.** Everything in the brief's MUST list is implemented and covered by **587 passing tests against scripted
> in-process fake adapters, real child processes (`/bin/sh`, `python3`, `node`) and real TCP/unix sockets**, plus the SHOULD items listed below.
> The **real-adapter suites (`Category=RealAdapter`, 70 tests) pass**: netcoredbg 3.2.0-1092 (8 tests including the 10-session soak),
> **js-debug v1.140.0 for Node.js/TypeScript** on Node.js 24.21.0 (34 tests: the whole TypeScript evaluation matrix of
> [DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md), the private socket, a 10-session soak) and **Rust through GDB 18.1** (`gdb -i=dap`, no
> download; 28 tests: the Rust evaluation matrix of [DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md#rust-through-gdb), a 1M-line flood, a
> 10-session soak), on Linux x64, three runs in a row (2026-10-29, the GDB round), no adapter, debuggee, task process or socket directory left
> behind. Run them with
> `tools/fetch-netcoredbg.sh && tools/fetch-js-debug.sh && dotnet test tests/CsCode.Dap.Tests --filter Category=RealAdapter`.
> See [Real-adapter tests](#real-adapter-tests-netcoredbg).
>
> **CodeLLDB (wave 3)**: built in as `lldb` with a pinned installer that asks first, and **evaluated against the real CodeLLDB v1.12.3**: its
> real-adapter suites pass (`RealCodeLldbRustTests` 19/19, `RealCodeLldbPanelsTests` 3/3; they skip until CodeLLDB is present,
> `tools/fetch-codelldb.sh` or `CSCODE_CODELLDB`). Their first run found four session defects with this adapter, fixed through three new
> quirk hooks and a completion mapping; GDB stays the Rust default on Linux, CodeLLDB is the default on macOS. See [DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md#rust-through-codelldb).

## Layout

| Path | What |
|---|---|
| `Protocol/DapModels.cs` | Hand-written DAP models (`Dap*` records, init-only, nullable, string unions) + `DapJson` options. Unknown fields/enum values never break parsing. |
| `Transport/` | `DapFrameReader/Writer` (Content-Length framing, size-capped), `ProcessTransport` (stdio), `TcpTransport`, `PipeTransport` (named pipe / unix socket), `ServerProcessTransport` (an adapter that runs as a server process: start it, read its listening line, connect to its private socket — or a TCP port it printed — own the process tree and the socket), `PrivateSocket` (a Unix domain socket in a fresh 0700 directory under `$XDG_RUNTIME_DIR` or the temp directory, a named pipe on Windows; stale directories of a killed CsCode removed). |
| `Client/DebugLane.cs`, `Client/DebugAdapterClient.cs` | The Debug lane and the DAP client (own `seq`, request/response correlation, typed event emitters, reverse requests, timeouts, cancellation). |
| `Session/` | `DebugSession : IDebugSession` (+ `BreakpointSync`, `OutputBatcher`, `VariableHandleCache`, `DebuggeeLauncher`). |
| `Breakpoints/BreakpointManager.cs` | `IBreakpointManager` (source of truth, persisted per workspace). |
| `Service/` | `DebugService : IDebugService` (+ `IDebugFrameFocus`, `IDebugOutputHistory`, `IDebugSourceContentProvider`), commands, `cscode.debuggers` contributions. |
| `Configuration/` | `DebugConfigurationService`, `LaunchJson`, `VariableResolver`, `DapPlatform` (optional Core services bundle). |
| `Tasks/TaskService.cs` | Minimal `tasks.json` runner (`ITaskService`). |
| `Adapters/` | `NetCoreDbgAdapterFactory`, `NetCoreDbgInstaller`, `JsDebugAdapterFactory`, `JsDebugInstaller` (Node.js/TypeScript), `GdbAdapterFactory` + `GdbAdapterQuirks` + `CargoArtifacts` (Rust/C/C++ through GDB's native DAP; `Gdb/cscode_gdb.py`, copied to the output, summarises Rust values), `CodeLldbAdapterFactory` + `CodeLldbAdapterQuirks` + `CodeLldbInstaller` (Rust/C/C++ through CodeLLDB), `RustToolchain` (the folder's sysroot and rustc commit, shared). |
| `Session/DebugAdapterQuirks.cs` | What a built-in adapter does differently, compensated in one place (REPL input, completions, hit conditions, client-side and breakpoint-backed exception filters, output categories, frames, trailing output, `noDebug`, variable paging done by the session, a pause reported as a signal, frame ids renumbered per request); `BuiltinDebugAdapter.Quirks`, applied to every session of the type. |
| `tools/fetch-netcoredbg.sh` | Fetches netcoredbg into `.scratch/tools/netcoredbg/` (SHA-256 verified, safe extraction). |
| `tools/fetch-js-debug.sh` | Fetches the pinned js-debug v1.140.0 into `.scratch/tools/js-debug/v1.140.0/` (pinned SHA-256; `JSDEBUG_ARCHIVE=` installs from a local archive without downloading). |
| `tools/fetch-codelldb.sh` | Fetches the pinned CodeLLDB v1.12.3 package of this platform into `.scratch/tools/codelldb/v1.12.3/` (pinned per-platform size and SHA-256, only `extension/` extracted, entries checked first; `CODELLDB_ARCHIVE=` installs from a local package without downloading). |

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
  at most one queued flush. The Debug Console history (`Service/OutputHistory`) keeps the newest ≤ 5,000 lines (≤ 4 MB): whole old events go
  while they fit in the excess, otherwise the oldest event's head is cut by an **offset** (no copy), so a big batch followed by thousands of
  one-line events costs O(line) per event under the service lock.
* **Hostile payloads.** Frames larger than `MaxFrameBytes` (16 MB per session; 32 MB default for the raw client) are *skipped without
  buffering* (a short prefix is kept to fail the right request); variable pages are capped at 10,000 items even if the adapter ignores `count`;
  stack pages at 1,000 frames; module/loaded-source lists at 2,000.
* **Hung adapters.** Every request has a timeout (default 10 s; `launch`/`attach`/`configurationDone` 60 s; setting
  `cscode.debug.requestTimeoutSeconds`). A timeout fails that request with `DapRequestTimeoutException`, sets `DebugSession.IsUnresponsive`
  (+ `UnresponsiveChanged`), and any later answer/event from the adapter clears it. Nothing ever blocks a caller.
* **Child processes** are killed as a *tree* on close/timeout/dispose and are tracked (`IMemoryMonitor.TrackChildProcess` when available). Stdio
  adapters from a `DebugAdapterExecutable` (netcoredbg) start at normal priority; adapter **servers** (`DebugAdapterServerProcess`, js-debug)
  and stdio adapters from a `DebugAdapterStdioProcess` (GDB) start under `nice -n 10` (PriorityClass BelowNormal on Windows), which the
  debuggees they launch inherit, with the environment marker `CSCODE_DAP_ADAPTER`; a stdio adapter that died (signal, non-zero exit — not GDB's
  normal `quit`) has the processes still carrying its marker in its session reaped. Task processes (`tasks.json`, a preLaunchTask's `tsc`) are tracked
  too, and `TaskService.Dispose` (app shutdown) kills any task still running. A server transport kills the server's tree on close; processes
  that left the tree survive a normal close (a program's detached browser or daemon) and are reaped only after the server died on its own (its
  environment marker, same session only). `DebugService.Dispose` — a container disposed synchronously — kills every adapter tree and
  `runInTerminal` debuggee at once (`IKillableTransport`): its asynchronous shutdown runs on the debug lane, which the same dispose ends.
* **Adapter servers listen privately.** A `DebugAdapterServerProcess` with `PrivateSocket` (js-debug) gets a fresh directory only this user can
  enter (mode 0700, `<prefix>-<pid>-<start time>-<random>` under `$XDG_RUNTIME_DIR`, else the temp directory; `/tmp` when that path would be too
  long for `sun_path`) and the socket path `…/dap.sock` in place of the argument `${cscode:socketPath}`; the session connects with
  `UnixDomainSocketEndPoint`, child sessions to the same path. No TCP port is opened, so no other local process (another user included) can
  reach the adapter and have it launch programs. The directory is deleted whenever the transport closes (stop, program exit, server crash,
  shutdown, `KillNow` of a synchronous dispose) and when the start fails; directories whose creator is gone (pid not running, or running with
  another start time) are removed at the next start. Windows: a named pipe `\\.\pipe\<prefix>-<random>` through `PipeTransport` (written, not
  exercised here).

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
* Reverse requests: `runInTerminal` — kind `integrated` with the integrated terminal composed (`ITerminalService`, `DapPlatform.Terminals`, resolved
  lazily): the debuggee runs in a Terminal tab named after the request's `title` (else the session's name), started without a shell in between
  (the `processId` returned is the debuggee's), reused for the next run once its program exited (as VS Code reuses its debug terminal), and
  killed (the tab stays, with its output and exit code) when the session ends; otherwise (no terminal, or kind `external`, for which CsCode opens
  no window) the debuggee is spawned by the session with stdout/stderr wired to the Debug Console, stdin closed, `env` entries with `null`
  removing variables, returns `processId` — and
  `startDebugging` (asks `DebugService` for a child session, `Parent` set, type inherited). A child of a session connected to an adapter **server**
  (`DebugAdapterServerProcess` on its private socket, `debugServer` over TCP, a named pipe) opens its own connection to that same server
  (`DebugSession.AdapterEndpoint`, a `DebugAdapterNamedPipe` or `DebugAdapterServer` descriptor) instead of asking
  the factory again — js-debug's pending target exists only there — when it carries `__pendingTargetId` or is of the parent's type; a child of
  another type is created by its own type's factory. Children are stopped when their parent ends; the Restart command restarts the
  top session of the focused one (js-debug closes a child's connection on `restart`).
* A failed request (`success: false`) throws `DapRequestException` whose `Message` is the adapter's own text (what Watch/REPL/Locals show); the
  request is `Command`.
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
  (references, pages, scopes), so the frames of the stop and the user's focused frame survive; `threads` alone drops nothing. Then, after
  every `Invalidated` subscriber has run, `DebugService` checks the published stop location. If its frame is still valid (`variables` or
  `threads` only), the location is unchanged and **no `StopLocationChanged` fires**: that event means "the stop moved" and the editor
  reveals (re-centers on) the stop line for it, so re-announcing an identical location would scroll the user's view back. Data views
  re-read through their own `IDebugSession.Invalidated` subscription (the panels' focus tracker starts a new epoch on it and accepts the
  unchanged location after its grace period). Otherwise (`stacks`/`all`) it clears the location, fetches the stack again (re-registering
  the frames) and publishes a location on a live frame, like a new stop (VS Code re-runs the stop for these areas too, revealing the line).
  A dead `FrameId` is never left published.
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
**per breakpoint per session**, so one session ending never erases another live session's verdict. `GetStatus` shows the newest verdict of a
session that **verified** the breakpoint, else the newest verdict (VS Code's rule): js-debug's orchestrating parent session answers every
`setBreakpoints` with "Unbound breakpoint", which must not hide the child's verdict.

Each live session mirrors the manager (`BreakpointSync`): per-file replace-all `setBreakpoints` (an empty list clears a file), debounced 50 ms,
only kinds the adapter supports (conditions/hit conditions/logpoints the adapter lacks are **not sent** — they would stop where the user said
they must not — and are reported unverified with the reason; so are function breakpoints when the adapter has none, e.g. js-debug), exception filters taken from the adapter's `exceptionBreakpointFilters`
(defaults applied once, user choices kept; conditions via `filterOptions`), content-based de-duplication (so adapter status updates cannot
re-trigger sends), per-session adapter-id → breakpoint-id map for `breakpoint` events. An adapter that **errors** on a source (unknown file) does
not abort the session: those breakpoints show unverified with the adapter's message.

## launch.json, variables, tasks

* `.cscode/launch.json` first, else `.vscode/launch.json` (JSONC); `configurations[]`, `compounds[]` (surfaced as configurations with
  `type: "compound"`, started concurrently, `stopAll`, `preLaunchTask`), `inputs[]`. Platform blocks (`windows`/`linux`/`osx`) deep-merge.
  File changes are watched; a broken file reports one error message and yields no configurations. `Selected` is persisted per workspace and
  refreshed when the file is edited. `CreateLaunchJsonAsync` writes the VS Code template layout (never overwrites an existing file) plus
  provider-supplied companion files (the coreclr template also writes a `tasks.json` with a default `build` task). A companion never shadows
  the user's own file: when the folder keeps its `tasks.json` in `.vscode/`, the new `launch.json` goes to `.vscode/` and the template's tasks
  whose labels are missing are appended to that file's `tasks` (text edit, comments kept; a template's default build task is added as a
  plain build task when the file has a default BUILD task — a default test task does not count); a file that does not parse is left alone
  (`JsoncText.cs`, `DebugConfigurationService.AddTasks`).
* **Open launch.json** (`workbench.action.debug.configure`) opens the folder's launch.json, creating it first for the folder's debugger (or a
  picked one). **Add Configuration...** (`debug.addConfiguration`) picks a debugger, then one of its templates, and appends it to
  `configurations` in the editor (one undoable edit, unsaved, the rest of the file as written) and reveals it; without a launch.json it is
  Open launch.json. CsCode detects no tasks (VS Code has `tsc: build` or cargo tasks without a tasks.json), so the task the added
  configuration's `preLaunchTask`/`postDebugTask` names comes with it when the folder lacks it: appended to the tasks.json in use (in the
  editor when it is open there, else on disk; comments kept, no second default build task), or the template's tasks.json is written next to
  the launch.json. F5 opens the launch.json it created, and with a launch.json that holds no configuration (or does not parse) opens it
  instead of offering Create (VS Code's behaviour).
* **Starting**: from `StartAsync` until the session exists (variables, providers, `preLaunchTask`, the adapter download offer) the debugger is
  `initializing` (`inDebugMode`: F5 means Continue, as in VS Code), a second start of the same configuration is ignored (no second build, no
  second debuggee), and Stop cancels the start and kills its `preLaunchTask`. A stop returns once the session let go of its adapter (after
  `terminated` the session sends `disconnect` and lets the adapter exit; output it sends until then still reaches the Debug Console).
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
  success, cancellation kills the process tree, cycle detection. Problem matchers (`$msCompile`, `$tsc`, `$gcc`, `$rustc`, `$eslint-*`,
  custom ones) feed the Error List (`ProblemMatchers.cs`, `DapPlatform.Diagnostics`). Shell tasks run via `/bin/sh -c` (`cmd /d /c` on
  Windows) with POSIX quoting of args. With the integrated terminal composed (`DapPlatform.TaskTerminals`, the Core seam `ITaskTerminalHost`)
  a task runs in a terminal named after it following its `presentation` (`TaskService.Terminal.cs`; details in docs/TERMINAL.md, "Tasks in
  terminals"). `preLaunchTask`/`postDebugTask` use it; a failed `preLaunchTask` offers "Debug Anyway".

## netcoredbg and other adapters

* Built-in `coreclr` (alias `netcoredbg`) → `netcoredbg --interpreter=vscode`. Binary lookup: setting `cscode.debug.netcoredbgPath` →
  `{ToolsDir}/netcoredbg/netcoredbg` → the copy installed with the app (`{InstallDir}/debug-adapters`, `BundledAdapters`) → `PATH`. If none is found the user is **offered** a download (never silent) through
  `IWindowService.ShowMessageAsync` + `WithProgressAsync`. `NetCoreDbgInstaller` resolves the latest release from the GitHub API, downloads
  the platform asset (linux-amd64/arm64, osx-arm64, win64), **verifies its SHA-256 against the digest GitHub publishes for the asset** (refuses on
  mismatch; warns and relies on archive integrity if GitHub publishes none), extracts through a zip-slip-safe extractor (rejects `..`, absolute
  paths, drive letters, symlinks/hardlinks leaving the destination, symlink targets with a `..` segment — a `y -> .`, `x -> y/..` chain is
  lexically inside yet resolves outside — and any entry that would be written through a symlink; never creates devices/fifos), `chmod +x`,
  atomically replaces the old install.
  Concurrent starts trigger one offer and one download.
* The provider fills in `cwd` when missing, converts a textual `processId` (from `${command:pickProcess}`) to the number netcoredbg expects, and
  produces the initial **launch** (`program: ${workspaceFolder}/bin/Debug/<tfm>/<Project>.dll`, `preLaunchTask: build`, `stopAtEntry`, `console`,
  `cwd`) and **attach** (`processId: ${command:pickProcess}`) configurations. The project: the folder's own `.csproj`/`.fsproj`/`.vbproj`,
  else the projects its `.sln`/`.slnx` lists, else project files up to two levels down (`dotnet new sln` layout); executable ones (OutputType
  Exe/WinExe, the Web/Worker SDKs, not test projects) first, the user picks the startup project when there are several; `program`, `cwd` and the
  build task follow that project (its directory, `AssemblyName`, first target framework).
* Built-in `pwa-node` (and the legacy alias `node`, rewritten to `pwa-node`) → Microsoft's **js-debug** standalone server
  (`node dapDebugServer.js <private socket path>`, a `DebugAdapterServerProcess` with `PrivateSocket`; see "Adapter servers listen privately"):
  the parent session orchestrates and the program runs in the child session
  js-debug asks for with `startDebugging`. Lookup: `cscode.debug.jsDebugPath` → `CSCODE_JSDEBUG` → `{ToolsDir}/js-debug/v1.140.0/js-debug` →
  the copy installed with the app (`{InstallDir}/debug-adapters`), else the user is **offered** the pinned download (`JsDebugInstaller`, SHA-256 checked). Node.js: `cscode.debug.nodePath` → PATH. The provider sets
  `__workspaceFolder` (without it js-debug resolves no source maps) and offers TypeScript (tsc build task), Node and attach templates. Breakpoint
  languages typescript, javascript, typescriptreact, javascriptreact. Evaluation and quirks: [docs/DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md).
* Built-in `gdb` (and the alias `rust-gdb`) → **GDB's native DAP**: `nice -n 10 gdb [cscode.debug.gdbArgs] -q -iex … -i=dap` (a
  `DebugAdapterStdioProcess`, GDB 14+, nothing to download). For Rust the factory does what rust-gdb does on GDB's command line: the Rust
  pretty-printers from `$(rustc --print sysroot)` and `set substitute-path /rustc/<commit> <sysroot>/lib/rustlib/src/rust`; plus CsCode's
  `cscode_gdb.py` (one-line summaries of Rust structs/enums, which GDB's DAP shows empty). Lookup `cscode.debug.gdbPath` → PATH. The provider moves
  `env` to `set environment` commands (GDB's `env` replaces the whole environment), which GDB reads with `-ix` from a 0600 file in a private
  0700 directory (`PrivateStartupFile`: written afresh for each GDB the descriptor starts — a restart starts a new GDB — and deleted with that
  GDB) — never on its command line, which every local user can read in `/proc/<pid>/cmdline` — turns a textual
  `pid`/`processId` into a number and resolves
  CodeLLDB's `"cargo": {"args": […], "filter": {…}}` key (cargo's reported executable becomes `program`: the test-binary template uses it).
  Templates for a cargo package: binary (`cargo: build` process preLaunchTask), unit tests (`cargo` key), attach. `GdbAdapterQuirks`: the Debug
  Console evaluates expressions (`-exec`/backtick for GDB commands), completions through `print`, hit counts as ignore counts, a client-side
  **"Rust panics"** exception filter (`std::panicking::rust_panic`, reported as an exception with the panic's message), GDB's messages as
  `console` output, logpoint line breaks, output after `terminated` kept, std/libc/symbol-less frames de-emphasised. Breakpoint languages rust,
  c, cpp. Evaluation, quirks and limits: [docs/DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md#rust-through-gdb).
* Built-in `lldb` → **CodeLLDB v1.12.3** (MIT): `nice -n 10 codelldb --settings {"sourceLanguages":[…],…}` on **stdio** (a
  `DebugAdapterStdioProcess` like GDB's; CodeLLDB speaks stdio without `--port`/`--connect`, one session per process; `PYTHONHOME`/`PYTHONPATH`
  removed from its environment). Lookup `cscode.debug.codelldbPath` → `CSCODE_CODELLDB` → `{ToolsDir}/codelldb/v1.12.3` → the copy installed with the app
  (`{InstallDir}/debug-adapters`) → PATH, else the user is
  **offered** the pinned download (`CodeLldbInstaller`: per-platform SHA-256 and size pinned, only `extension/` extracted, links and setuid bits
  refused, at most 1 GiB, extraction off the debug lane). The provider does what VS Code's CodeLLDB extension does: the `cargo` key (shared
  `CargoArtifacts`), `cwd`/`relativePathBase`, `sourceLanguages` (Rust by default: the toolchain's LLDB formatters and the "Rust: on panic"
  filter), `_adapterSettings.consoleMode: "evaluate"` (or VS Code's `lldb.consoleMode`), `terminal: "console"` unless an integrated terminal
  was asked for and exists, `sourceMap` `/rustc/<commit>` → rust-src, string `env` values, numeric attach `pid`. `CodeLldbAdapterQuirks`: its
  panic filter's breakpoint stops reported as the exception "Rust panics" (new `DebugAdapterQuirks.BreakpointExceptionFilters`: ids taken from
  the `setExceptionBreakpoints` response), a bare hit count sent as `=N`, `-exec` → `/cmd`, std and disassembly frames de-emphasised, no
  breakpoints sent to a `noDebug` launch (new `RefusesBreakpointsWithoutDebugging`); and, found by the real run: `variables` pages cut by the
  session (`IgnoresVariablePaging`: CodeLLDB ignores `start`/`count`/`filter`), the SIGSTOP "exception" of a pause reported as a pause
  (`IsPauseStop`, only while the client's `pause` is outstanding), each thread's frames read once per stop so a frame keeps one id
  (`RenumbersFramesPerRequest`: CodeLLDB numbers frames anew per `stackTrace`), and Debug Console completions through LLDB's `frame variable`
  with their positions put back at the caret. Templates like GDB's. Evaluation:
  [docs/DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md#rust-through-codelldb).
* **The Rust default**: F5 with no launch configuration offers "Create launch.json" for the folder's debugger — `Cargo.toml` → the Rust debugger
  (`cscode.debug.rustDebugger`: `gdb` or `lldb`; the default is `lldb`, CodeLLDB, on macOS, where GDB cannot debug native arm64 programs,
  and `gdb` on Linux), a .NET project or solution → `coreclr`, `tsconfig.json`/`package.json` → `pwa-node`, else a "Select debugger" pick (it
  used to take the first debugger in label order, .NET, for every folder). Why:
  [docs/DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md#which-rust-debugger-is-the-default).
* `${command:pickProcess}` is a built-in command (`pickProcess`) listing processes through `IWindowService.ShowQuickPickAsync` (enumeration runs
  off the caller thread; never lists the editor itself).
* **User adapters, no extensions:** the `cscode.debuggers` setting (array of `{type, label, program, runtime, args, languages, windows/linux/osx,
  initialConfigurations, configurationSnippets, …}`) is published into `IContributionRegistry` as `DebuggerContribution`s
  (`ContributionSources.User`; only this module's own items are replaced on change) and their languages as `BreakpointLanguageContribution`s.
  A configuration may also carry `"debugServer": 4711` or `"host:port"` to connect to an already running adapter. `IDebugService.RegisterAdapterFactory`
  / `RegisterConfigurationProvider` remain for in-process registrations (latest registration wins).

## Commands, context keys, settings

* Commands registered (VS Code ids from `KnownCommands`; **the shell must not register handlers for these again**):
  `workbench.action.debug.start|run|stop|restart|continue|pause|stepOver|stepInto|stepOut`, `editor.debug.action.toggleBreakpoint`,
  `workbench.action.debug.configure` (Open launch.json), `debug.addConfiguration` (Add Configuration...), plus `pickProcess`.
* Context keys (only if `IContextKeyService` resolves): `debugState` (`inactive|initializing|running|stopped`), `inDebugMode`, `debugType`,
  `debuggerAvailable`, `focusedSessionIsAttach`, `breakpointsExist`.
* Settings registered with `IConfigurationService`: `cscode.debuggers`, `cscode.debug.netcoredbgPath`, `cscode.debug.jsDebugPath`,
  `cscode.debug.nodePath`, `cscode.debug.gdbPath`, `cscode.debug.gdbArgs` (extra GDB arguments, e.g. `["-nx"]`), `cscode.debug.codelldbPath`,
  `cscode.debug.rustDebugger` (`gdb` | `lldb` | `codelldb`; default `lldb` on macOS, `gdb` elsewhere), `lldb.consoleMode` (VS Code's CodeLLDB setting: `commands` | `evaluate` |
  `split`), `cscode.debug.requestTimeoutSeconds`. `DebugService.SettingsRead` lists them; a test checks every key the module's sources name is
  registered.
* Environment: CsCode's own `DOTNET_ROOT_<ARCH>` (set by `dotnet run`/`dotnet test`) is folded into `DOTNET_ROOT` for the programs the
  debugger starts itself: debug adapters (and so the programs they launch), debuggees run without a terminal and task processes piped into
  the "Tasks" channel, as for language servers (`DotnetRootFold.cs`, `InheritedDotnetRoot`). A `runInTerminal` debuggee or a task shown in a
  terminal gets only its own `env` as the terminal's caller variables, local or remote: a local terminal's launch resolver folds what it
  inherits before `terminal.integrated.env` and the caller's variables apply (so the user's `DOTNET_ROOT` there wins and
  `terminal.integrated.inheritEnv: false` keeps CsCode's out, as in any terminal; docs/TERMINAL.md), and a remote workspace's terminal starts
  from the remote host's environment (cross-branch review C3; `TerminalDotnetRootTests` runs both through the real terminal service).
* Variables paging: an adapter that answers a later page with more than `count` children starting at the container's first child (it
  ignores `start`/`count`, as CodeLLDB does) is detected per container; that answer is kept as the container's whole list and its pages are cut
  by the session, as with the `IgnoresVariablePaging` quirk (no quirk needed: user adapters too). More than `count` alone is not taken as
  ignoring paging (js-debug adds a row to an honoured page).

## Integration notes

* `services.AddCsCodeDap()` registers a shared `DebugLane`, `BreakpointManager`/`IBreakpointManager`, `ITaskService`, `IDebugConfigurationService`,
  `IDebugService` (and the same instance as `IDebugFrameFocus`, `IDebugOutputHistory`, `IDebugSourceContentProvider`, `IDebugKnownCapabilities`)
  and the built-in adapters (netcoredbg for `coreclr`; js-debug for `pwa-node` and `node`; GDB for `gdb` and `rust-gdb`, with `GdbAdapterQuirks`; CodeLLDB for `lldb`, with `CodeLldbAdapterQuirks`). Every Core platform service it can use is **optional** (`DapPlatform.From(sp)` uses `GetService`).
  **Resolve `IDebugService` once at startup**: its constructor registers the commands, `${command:pickProcess}` and the context keys.
* Additive Core files: `Core/Tasks/ITaskService.cs`; `Core/Debugging/DebugExtras.cs` (`IDebugFrameFocus`, `IDebugOutputHistory`,
  `IDebugSourceContentProvider`, `IDebugSessionHealth`, `IActiveSelectionProvider`, `DebugSourceUri`); `Core/Debugging/DebugKnownCapabilities.cs`
  (`IDebugKnownCapabilities` — DebugService remembers each type's last capabilities — and `DebugCapabilityGate`, which the breakpoint editors use
  to offer hit counts/logpoints only when supported); `Core/Debugging/DebugSessionPaging.cs` (`IDebugSessionVariablePaging`, filtered paging);
  `DebugVariable.IsLazy` (DAP `presentationHint.lazy`). They are *new interfaces/members* rather than changed signatures, so existing implementers
  and fakes are not broken.
* Editor ↔ debugger: the editor calls `IBreakpointManager.ApplyLineShift` on edits and reads `IDebugService.StopLocation`; frame selection goes
  through `IDebugFrameFocus.SetFocusedFrameAsync`; `debug:/<session>/<ref>/<name>` documents are read-only virtual sources
  (`IDebugSourceContentProvider`). Ended sessions are replaced by a lightweight `EndedSession` in the console history so 5,000 retained lines
  can never pin a dead session.

## Limits and known gaps

* **netcoredbg 3.2.0-1092 has no hit-count breakpoints and no logpoints**: it advertises neither, and a `hitCondition` sent anyway is ignored
  (it stops on the first hit — checked with a direct DAP probe). Such breakpoints are therefore not sent; they show unverified with
  "This debug adapter does not support …" and never stop the program. Conditional breakpoints work.
* `serverReadyAction` (open a URL when an output pattern appears) is not implemented; the connect-retry loop of `TcpTransport` covers "wait for the port".
* `${selectedText}` needs the shell's `IActiveSelectionProvider`; `INavigationService` exposes only the caret.
* `runInTerminal` `kind: external` (and `integrated` when no terminal is composed) has no terminal: debuggee stdin is closed (EOF) and output is
  plain text in the Debug Console. A `console: "externalTerminal"` window is not opened. js-debug's `console: "integratedTerminal"` runs the program
  in the integrated terminal (docs/TERMINAL.md); `externalTerminal`, or `integratedTerminal` without a terminal, runs in the console with
  `outputCapture: "std"` (else every `console.log` would show twice).
* js-debug's server listens on a private Unix socket, but the program's own Node.js inspector, which js-debug's bootloader opens inside the
  debuggee, listens on a random loopback TCP port while it is debugged (as in VS Code; see docs/DEBUGGING-RUST-TS.md, known gaps).
* A socket directory survives only a CsCode that was killed outright (`kill -9`, a crash): it is removed the next time a js-debug session
  starts, not at app start.
* Shell tasks quote arguments POSIX-style; VS Code's `quoting` options and `isBackground` task readiness are not implemented. Auto-detected
  task providers (npm, tsc…) are out of scope (no extensions).
* A flood beyond the 128 K-chars/16 ms window drops the *oldest* text by design; the console shows a dropped-characters marker. The history
  for late subscribers keeps the newest 5,000 lines (the oldest batch is cut, not dropped whole).
* js-debug: function breakpoints are unsupported by the adapter; async parent frames have no variables; see docs/DEBUGGING-RUST-TS.md.
* GDB: the program's stderr arrives as stdout (one pipe); hit counts mean "from the Nth hit on" (GDB ignore counts); a `println!` line stops
  twice per pass; see docs/DEBUGGING-RUST-TS.md (Rust through GDB, known gaps).
* CodeLLDB: the program's stderr arrives as stdout (LLDB's pseudo-terminal); it does not drain the program's output at exit (the last lines of
  a flood can be lost); expressions are LLDB's (no Rust casts or method calls); a `println!` line stops three times per pass; no `process`
  event; its `[return value]` row is a variable in its "Local" scope, which Autos lifts (shows first) as it does GDB's `returnValue` scope; see
  docs/DEBUGGING-RUST-TS.md (Rust through CodeLLDB, known gaps).
* A stop is shown at the first frame the adapter does not de-emphasise (VS Code's rule), so a pause in a library or a panic inside std shows
  the user's code; the Call Stack still lists (folded) the frames above it.
* Single-thread-only resumes fire `Continued` once locally and may fire a second (duplicate) event if the adapter also sends `continued`.
* Windows named pipes (`DebugAdapterNamedPipe`, js-debug's private endpoint on Windows) and the Windows/macOS netcoredbg builds are implemented
  but only exercised on Linux here.

## Tests (`tests/CsCode.Dap.Tests`, xUnit v3)

```
dotnet test tests/CsCode.Dap.Tests --filter "Category!=RealAdapter"     # 587 tests: fakes, real processes/sockets, threading, leaks
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
→ step into (lands on the callee's opening brace: a Debug build has a sequence point there, as in Visual Studio) → focus the caller's frame
(`SetFocusedFrameAsync`: stop location on the call line, evaluation in that frame) → over, over → out (lands back on the calling statement) →
continue to exit with the program's exit code 7 → no `netcoredbg`/debuggee PIDs left; conditional breakpoints; hit-count breakpoints and logpoints
(stop on the requested hit / print without stopping when advertised, otherwise reported unverified and never stopping — the netcoredbg case);
exception filter + `exceptionInfo`; and 10 start/stop cycles with PID, thread and `LeakAssert` checks. They skip with an explicit reason when
`.scratch/tools/netcoredbg/` (or `CSCODE_NETCOREDBG`) or `dotnet` is missing. Observed netcoredbg sequencing, all handled: a `capabilities` event
before the `initialize` response, `initialized` before the `launch` response, frame ids starting at 0, `continued` before the response to a step,
`exited` then `terminated`.
`RealAdapterSampleTests` (trait `RealTools`) verifies the sample builds and its line markers resolve, without starting netcoredbg.

### Real-adapter tests (js-debug)

`RealJsDebugTests` (trait `Category=RealAdapter`, serial) compile a two-file TypeScript sample with the real tsc and run the whole evaluation matrix
of docs/DEBUGGING-RUST-TS.md through `DebugService`: launch/exit, `.ts` breakpoints and moved lines, conditional/hit-count/logpoints, function
breakpoints (unsupported), caught/uncaught exceptions, steps across files and awaits, pause, restart, stop (all, child only), adapter crash,
shutdown, async/source-mapped/skipped frames, values (classes, getters, Map/Set, paged arrays), set variable, REPL and completions, output
categories and capture modes, attach to `node --inspect`, a 1M-line flood, the TypeScript template end to end, the private socket (no TCP
listener in the server, the 0700 directory, a stale directory of a killed CsCode removed), and a 10-session soak (sessions collected, no
server/debuggee left, threads back to baseline). Each test checks that no js-debug server, no process carrying its marker and no socket
directory is left. They skip with an explicit reason when `node`, `tsc` or js-debug (`tools/fetch-js-debug.sh` / `CSCODE_JSDEBUG`) is missing.
`JsDebugFactoryTests`, `JsDebugInstallerTests` (the pinned digest against the local archive and a corrupted copy, the fetch script with fake
`curl`/`wget` that fail if called), `ServerProcessTransportTests` and `AdapterServerProcessLifetimeTests` (shell stand-ins: listening line, early
exit, timeout, `nice` 10, tree kill on close, detached/orphaned processes on a normal close and after a crash, synchronous dispose with a busy lane)
and `PrivateSocketTests` (a Node.js stand-in on the socket: the 0700 directory, the path handed to the server, no TCP listener, the directory
gone after close, crash, `KillNow` and a failed start; stale directories removed but not a live CsCode's, links or look-alikes; the
`sun_path` fallback; `$XDG_RUNTIME_DIR`) need no download. `JsDebugWaveFixTests` and `JsDebugReviewFixTests` hold the regression tests of the fixes.

### Real-adapter tests (GDB, Rust)

`RealGdbRustTests` (trait `Category=RealAdapter`, serial) build a Rust cargo package offline with the real cargo
(`tests/CsCode.TestDoubles/RustGdbSample.cs`, shared by the Dap, Panels and App tests) and run the Rust evaluation matrix through `DebugService`
against GDB's native DAP (`-nx`: never the user's gdbinit): launch/exit at nice 10 with the marker, breakpoints (moved, added while stopped,
conditional, hit counts, logpoints, `println!` lines, functions), Rust panics caught and uncaught, steps and the return value, pause, stop,
restart, crash, generic and closure frames, Rust values, watch, set variable, the REPL and completions, output categories, threads, attach by
pid, the `cargo` key's test binary, the templates, `env`, a 1M-line flood and a 10-session soak; each checks that no gdb, debuggee or marked
process is left. They skip with the reason when gdb (14+ with Python), cargo or rustc is missing. `GdbQuirksTests` (fake adapters shaped like
GDB 18) and `GdbAdapterFactoryTests` (stand-in scripts for gdb/rustc, the stdio transport's nice/marker/reaping) need no GDB.

### Real-adapter tests (CodeLLDB, Rust)

`RealCodeLldbRustTests` (trait `Category=RealAdapter`, serial; 19 tests) run the same matrix on the same rig (`RealGdbRig` with type `lldb`)
against the real CodeLLDB, recording what it shows (`[codelldb]` lines with `CSCODE_DAP_TEST_METRICS`) and asserting what a user relies on (stops,
lines, values read, completions applied, processes left); `RealCodeLldbPanelsTests` (3) do the same through the Debug panels (Call Stack arrow,
Locals paging, the console's backtrace and completions, the Exception window at a panic, Break All without one). They skip with the reason
when CodeLLDB (`tools/fetch-codelldb.sh` → `.scratch/tools/codelldb/v1.12.3/`, or `CSCODE_CODELLDB`), cargo or rustc is missing. Both pass
against v1.12.3 (wave 3). Without CodeLLDB:
`CodeLldbInstallerTests` (fabricated packages: pins equal to the fetch script's, download/verify/extract, digest and size refusals, zip-slip,
symlink, size-limit and setuid refusals, cancellation; `The_real_pinned_package_installs` extracts the real v1.12.3 package from
`CSCODE_CODELLDB_ARCHIVE`, trait `RealTools`, nothing executed), `CodeLldbAdapterFactoryTests` (lookup order, the stdio command line, the
download offer accepted/declined/failed/concurrent, the provider, templates, DI), `CodeLldbQuirksTests` (fake adapters shaped like CodeLLDB's
sources and its observed traffic: the panic filter's breakpoint stop as an exception, filter options, hit counts, `-exec`, frames, `noDebug`,
paging, the SIGSTOP pause, renumbered frames, completions) and `DefaultDebuggerTests`
(the launch.json F5 creates per folder, the `cscode.debug.rustDebugger` setting and its per-OS default).
