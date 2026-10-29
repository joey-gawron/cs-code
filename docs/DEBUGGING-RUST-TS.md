# Debugging Rust and TypeScript — evaluation and fixes

User request (2026-10-29): *"can you see how well the debugger feature works with rust and typescript"* — the user approved the js-debug download
and asked to start with it. This document records the evaluation matrix of `docs/briefs/debugger-rust-typescript.md` (MUST 3) with evidence,
what was fixed (each with a regression test that fails without the fix), and what is an adapter limitation.

| Language | Adapter | Status |
|---|---|---|
| **TypeScript / JavaScript** | Microsoft **js-debug v1.140.0** (`js-debug-dap`, MIT), built in | **evaluated, defects fixed** — every row below |
| **Rust** | GDB 16.3 (`gdb -i=dap`) | **not evaluated yet** (next round) |

## TypeScript / JavaScript through js-debug

### How it is wired (MUST 2)

* **Adapter**: built-in debug types `pwa-node` and `node` (the legacy alias; the provider rewrites it to `pwa-node`, the only node type the
  standalone server knows), breakpoint languages `typescript`, `javascript`, `typescriptreact`, `javascriptreact`
  (`src/CsCode.Dap/Adapters/JsDebugAdapterFactory.cs`).
* **Server process**: `node <js-debug>/src/dapDebugServer.js 0 127.0.0.1` started through the new `DebugAdapterServerProcess` descriptor
  (`src/CsCode.Dap/Transport/ServerProcessTransport.cs`): `nice -n 10` (below normal; the debuggees it spawns inherit it), stdin closed, stdout/stderr
  drained into bounded tails, the line `Debug server listening at 127.0.0.1:<port>` read with a timeout (30 s; a server that exits first fails at
  once with its own stderr), then a TCP connection. The process is tracked (`IMemoryMonitor.TrackChildProcess` through the session's
  `AdapterProcessId`) and owned by the parent session's transport.
* **Child sessions**: js-debug answers `launch` and then asks for the program's session with `startDebugging` (request `launch`, configuration
  `{type, name, __pendingTargetId}`). `DebugService` starts it as a child session that opens **its own TCP connection to the same server**
  (`DebugSession.AdapterEndpoint`; the factory is not asked again — it would start another server that does not know the pending target).
  Breakpoints, threads, stops and variables live in the child; the parent only orchestrates.
* **Lifetime**: the server and every child connection end with the session — stop (Shift+F5), stop of the child alone, restart, debuggee exit,
  adapter crash, app shutdown (`DebugService.DisposeAsync`). Closing the parent's transport kills the server's process tree; processes that
  left the tree (an orphaned debuggee after the server was killed) are found through an environment marker (`CSCODE_DAP_ADAPTER=<guid>`, which the
  server passes on to the programs it launches) and killed too. Children of an ended parent are stopped.
* **Locating js-debug**: setting `cscode.debug.jsDebugPath` → environment variable `CSCODE_JSDEBUG` → the installed copy
  `{ToolsDir}/js-debug/v1.140.0/js-debug`; any of them may name the version directory, the `js-debug` directory, its `src/` or
  `dapDebugServer.js`. Node.js: setting `cscode.debug.nodePath` → `node` on PATH.
* **Installer** (`JsDebugInstaller`): when nothing is found the user is **asked** ("Download" / "Cancel"; nothing is fetched otherwise) and the
  **pinned** `js-debug-dap-v1.140.0.tar.gz` is downloaded from the GitHub release, refused unless its SHA-256 is
  `27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968` (1,249,709 bytes), extracted by the zip-slip-safe tar extractor into a staging
  directory and moved into place atomically. `tools/fetch-js-debug.sh` does exactly the same for tests and development
  (`JSDEBUG_ARCHIVE=<file>` installs from an archive you already have without any download; `JSDEBUG_DEST=<dir>` installs elsewhere).
* **Configuration provider**: fills in `__workspaceFolder` (see the quirks below), leaves child configurations alone, never overrides the
  user. Templates (*Add Configuration* / *Create launch.json*): **TypeScript: Launch (tsc build)** (`program: ${workspaceFolder}/src/<entry>.ts`,
  `preLaunchTask: "tsc: build"`, `outFiles` from tsconfig's `outDir`, `sourceMaps: true`, `skipFiles: ["<node_internals>/**"]`) with the
  `tsc: build` task written to `tasks.json`; **Node.js: Launch Program** (`package.json` `main` or `index.js`); **Node.js: Attach (--inspect)**
  (port 9229). The TypeScript template was run end to end: its preLaunchTask compiles with tsc and the `.ts` program stops at a breakpoint.

### Toolchain notes

* **TypeScript compiler**: `/usr/local/bin/tsc` reports `Version 7.0.2` (the native TypeScript 7 compiler). `tsc -p` with `sourceMap: true`,
  `outDir: out`, `rootDir: src` writes `out/main.js` + `out/main.js.map` with `"sources": ["../src/main.ts"], "sourceRoot": ""`; js-debug resolves
  these maps, binds `.ts` breakpoints and reports `.ts` frames. The generated JavaScript has different line numbers than the TypeScript (class
  fields, split try/catch), so the tests prove the mapping. Offline there is no `@types/node`; the sample declares the little of `process` it uses.
* **Node.js** v20.19.2 runs both js-debug and the debuggee.

### The matrix

Evidence is what the real adapter sent and what the panels showed, recorded by the tests (`CSCODE_DAP_TEST_METRICS=<file>` collects it).
Sample: `tests/CsCode.TestDoubles/JsDebugSample.cs` (`src/main.ts` + `src/util.ts`, async calls, a class with a getter, Map, Set, a 250-element
array, exceptions, flood/serve modes), compiled by the real tsc in each test's temp directory.
Tests: **D** = `tests/CsCode.Dap.Tests/RealJsDebugTests.cs` (real `DebugService`), **P** = `tests/CsCode.Panels.Debug.Tests/RealJsDebugPanelsTests.cs`
(the panels' view-models over the real DAP module), **R** = `RealJsDebugResponsivenessTests.cs`, **A** = `tests/CsCode.App.Tests/RealApp/TypeScriptDebugTests.cs`
(the real application).

| Row | Status | Evidence | Test |
|---|---|---|---|
| Launch + clean exit, nothing left running | **works** | Parent `launch` → `startDebugging` → child connection to the same port; the program prints and exits; both sessions end; the js-debug server pid and every process carrying its marker are gone (checked after every real test). Soak: 10 runs → 20 sessions collected after forced GCs, 11 servers gone, threads 21 → 21/22. | D `Launch_stops_at_a_ts_breakpoint_in_the_child_session_and_exits_cleanly`, `Ten_real_sessions_…` |
| Line breakpoints in `.ts` (source maps) | **works** (after the `__workspaceFolder` quirk is handled) | `setBreakpoints` on `src/main.ts` answers `verified: false, "breakpoint.provisionalBreakpoint"`, then a `breakpoint` event `changed, verified: true, line 50` once the map is loaded; the stop is reported at `src/main.ts:50`. A second file (`util.ts`) binds too. | D, P, A |
| Moved line (breakpoint on a comment line) | **works** | Requested line 43 → stopped at 44; verdict `ActualLine` 44; Breakpoints window: `main.ts : 43 Moved note='moved to line 44'`. | D `A_breakpoint_on_a_line_without_code_…`, P |
| Conditional breakpoint | **works** | `i === 3` stops once with `i = 3`, `x = 44`, then runs to the end. | D |
| Hit-count breakpoint | **works** | `"3"` (js-debug: `=3`) stops on the third pass, `i = 2`. js-debug evaluates *either* a condition *or* a hit count (a condition wins when both are set) — adapter behaviour. | D |
| Logpoint | **works** | `logged i={i} x={x}` → five `stdout` output events `logged i=0 x=41` … `logged i=4 x=51` with the source location, no stop. | D |
| Function breakpoints | **adapter limitation** (reporting **fixed**) | js-debug v1.140.0 advertises `supportsFunctionBreakpoints: false`. Before: nothing sent and **no verdict at all** (the breakpoint looked pending forever). Now: reported unverified, "This debug adapter does not support function breakpoints." | D `Function_breakpoints_are_reported_unsupported_by_js_debug`; fix test below |
| Exception breakpoints, caught | **works** | Filters advertised: `all` (Caught Exceptions, default off, supports conditions), `uncaught` (default off). With `all`: stop reason `exception` at `throw new Error("boom")`; `exceptionInfo` → `exceptionId: "Error: boom"`, empty description/details message, `breakMode: all`. Exception window: type `Error: boom`, stack `at fails (…/out/main.js:13:11)` — js-debug's stack text is **not** source-mapped (adapter). | D, P `The_exception_window_shows_a_javascript_exception` |
| Exception breakpoints, uncaught | **works** | With `uncaught` only: the caught throw passes, the uncaught one (in a timer callback) stops at its line, "Paused on exception"; continuing ends the program with its error on stderr. | D |
| Step over / into / out | **works** | From `const r = await work(x)`: into → `main.ts:11` (`await delay(1)`), over the await → `main.ts:12`, into → `util.ts:2` (`add`), over → `util.ts:3`, out → `main.ts:13`; reasons `step`. | D `Step_into_over_and_out_across_ts_files_and_awaits` |
| Pause | **works** | Running timer loop → `pause` → stopped reason `pause` in the timer callback (`main.ts:33`), frames `listOnTimeout@timers…` below it. | D |
| Restart | **was broken → fixed** | Before: Restart (Ctrl+Shift+F5) sent `restart` to the focused child session; js-debug closed that connection ("The debug adapter closed the connection"). Now the command restarts the top session; js-debug relaunches the program as a new child; stopped again at the breakpoint after ~370 ms; the old child terminated. | D `Restart_runs_the_program_again_in_a_new_child_session` |
| Stop | **works** | Shift+F5 while stopped: every session gone after ~150 ms; stopping only the child: both end after ~20 ms; app shutdown while stopped: ~150–200 ms; nothing left running in any case. | D |
| Adapter crash | **works** | Killing the js-debug server (SIGKILL) while stopped: both sessions end ("The debug adapter closed the connection."), the orphaned `node ./out/main.js` is reaped through the marker. | D `An_adapter_crash_…` |
| Call stack: source-mapped `.ts` frames | **works** | `add@src/util.ts:2`, `work@src/main.ts:12`, `main@src/main.ts:54`, `<anonymous>@src/main.ts:62`. js-debug names sources relative to the workspace, so the Call Stack shows `src/main.ts : 12`. | D, P, A |
| Call stack: async frames | **was partial → fixed** | js-debug inserts a frame `await` (`presentationHint: label`, no source) between `work` and `main`. Before: with *Show External Code* off it was folded into `[External Code]`, hiding where the asynchronous part starts. Now it stays visible (greyed) and is not a frame one can switch to. Selecting the async parent `main` shows js-debug's own message in Locals: "Variables not available in async stacks" (adapter limitation). | P, fix test below |
| Call stack: skipped Node internals | **was partial → fixed** | `skipFiles: ["<node_internals>/**"]` frames come with `presentationHint: "deemphasize"` on the frame and its source (`<node_internals>/internal/modules/cjs/loader`, a `sourceReference`). Before: shown as normal frames, never folded. Now greyed and folded into one `[External Code]` row (6 frames). | P, fix test below |
| Locals: objects, classes, nested | **works** | `p = Point {x: 3, y: 4}`, `nested = {name: 'outer', inner: {…}}`, expandable; scopes `Local: main` (hint `locals`) and `Closure` (hint `arguments`) shown as groups; `Global` is not shown (Locals shows the scopes hinted locals/arguments); inside a `for` body a `Block` scope comes first. | D, P |
| Locals: strings | **was broken → fixed** | js-debug quotes strings itself (`'hello'`); the panels quoted again: `"'hello'"`. Now shown as `'hello'`. | P, fix test below |
| Locals: Map / Set | **works** | `Map(2) {size: 2, a => 1, b => 2}`, entries `0={"a" => 1}`, `1={"b" => 2}`, `size=2`, `[[Prototype]]=Map`; `Set(3) {size: 3, 1, 2, 3}`. | D, P |
| Locals: getters | **was partial → fixed** | js-debug sends getters as `presentationHint.lazy` with the getter's source as value (`ƒ length() { return Math.sqrt(… }`); expanding returns the value. Before: the source text was shown as the value. Now `(…)`; Enter/double-click runs the getter and shows `5` in place (`DebugVariable.IsLazy`). | D, P, fix test below |
| Locals: arrays > 100 items, paging | **was broken → fixed** | `big`: `(250) [0, 2, 4, …]`, `indexedVariables: 250`, `namedVariables: 1`. js-debug ignores `start` without a `filter` (a request for 100..199 answers 0..99), so the pager stopped after the first 100 elements (the duplicate-guard saw page 0 again). Now pages use `filter: "indexed"` (new `IDebugSessionVariablePaging`): `length`, `[[Prototype]]` and all 250 elements in order (js-debug appends a second `[[Prototype]]` to the last indexed page; names are de-duplicated). | D, P, fix test below |
| Change highlighting after a step | **was broken → fixed** | After stepping over `const sum = a + b`, js-debug adds a `Return value` pseudo-variable; the frame "shape" changed, so nothing was compared and `sum` (undefined → 52) was not red. Pseudo-variables (names with whitespace) no longer count. | P, fix test below |
| Autos | **works** (documented approximation) | In `add`: `a=100 | b=1 | sum=undefined` (Locals minus `this`). | P |
| Watch | **works** | `a + b = 101` in `add`; `x` → error. Error text was `'evaluate' failed: Uncaught ReferenceError: x is not defined` → now js-debug's own message. | P, fix test below |
| Set variable | **works** | `setVariable` on the `Local` scope: `x = 100`, the REPL then sees 101, and the program continues with it (`add(100, 1)`); also through the Locals inline editor. | D, P |
| Debug Console: REPL + completions | **works** | `x * 2 → 102`, `[...m.keys()].join() → 'a,b'`; completions for `s.toUpp` → `toUpperCase`, for `Ma` → `Map`, `Math`. Errors show js-debug's message (with its stack, e.g. "Uncaught ReferenceError ReferenceError: nope is not defined at eval (repl:1:1)"). | D, P |
| stdout / stderr in the console | **works** | `loop 0…4` category `stdout` (with source location), `err 52` category `stderr` (red), `/usr/bin/node ./out/main.js` category `console`. Adapter behaviour: with the default `outputCapture: "console"` only `console.*` calls are shown — `process.stdout.write` text is not; `outputCapture: "std"` captures the streams (plus node's own "Debugger attached." on stderr); `console: "integratedTerminal"` makes js-debug send `runInTerminal`, and the session pipes the program's streams to the Debug Console (no terminal UI). Console output can arrive just after the `stopped` event (js-debug buffers it). | D `Program_stdout_and_stderr_…`, `Raw_stdout_writes_…`, P |
| Attach to `node --inspect` | **works** | `node --inspect=127.0.0.1:0 out/main.js serve` → attach `{port, address}` → child session "Remote Process [0]" (attach) stops at the timer breakpoint, Locals show `ticks`; Stop detaches and leaves the program running (the test kills it). | D `Attach_to_a_node_process_…` |
| 1M lines printed, UI responsive | **works** (history **fixed**) | `process.stdout.write` of 1,000,000 lines with `outputCapture: "std"`: the session takes ~0.6–0.9 s; 13–15 output events reach the service, ~29 K lines delivered, ~17 M chars dropped by design (drop-oldest, marker line); heap +1.5–1.9 MB. With the Debug Console on screen (headless window): **dispatcher p99 8.6–11.3 ms, max 9.9–14.2 ms** (budget 16/50) after a warm-up run; console ≤ 5,000 lines. Debug lane round trip during the flood: p99 ≈ 100–108 ms (one 32-frame batch of ~170 KB output events; the lane, not the UI). Before the fix the console **history** kept 1 line after the flood (see fixes). | D `A_million_line_flood_…`, R |
| Templates | **works** | `Create launch.json` for `pwa-node` in a TypeScript folder writes the three configurations and the `tsc: build` task; the TypeScript one compiles (Tasks channel: "Task 'tsc: build' finished with exit code 0") and stops in `src/main.ts`. | D `The_typescript_template_…` |
| Real application, F5 | **works** | Real composition, `CSCODE_HOME` in a temp dir, headless window: open the folder, F9 in `main.ts`, F5 → tsc → js-debug → stopped; Call Stack shows `main` at `src/main.ts : 50` with the arrow, the editor marks line 50; a breakpoint in `util.ts` (not open) opens `util.ts` at the stop line; Shift+F5 → nothing left running. | A |

### Fixes (each with a test that fails without it)

| Module | Fix | Regression test |
|---|---|---|
| Dap | Child sessions of a TCP adapter server connect to the parent's server (js-debug's pending target); children end with their parent | `JsDebugWaveFixTests.A_child_session_connects_to_its_parents_adapter_server_…`, `Child_sessions_end_when_their_parent_ends` |
| Dap | Restart restarts the top session of the focused one | `JsDebugWaveFixTests.The_restart_command_restarts_the_top_session_…` |
| Dap | Unsupported function breakpoints get an unverified verdict with the reason | `JsDebugWaveFixTests.Function_breakpoints_an_adapter_does_not_support_…` |
| Dap | Console history keeps the newest 5,000 lines (trims the oldest batch's head instead of dropping whole batches; it fell to 1 line after a flood) | `JsDebugWaveFixTests.After_a_flood_batch_the_console_history_still_holds_the_newest_5000_lines` |
| Dap | A failed request's message is the adapter's own (Watch/REPL/Locals) | `JsDebugWaveFixTests.A_failed_evaluation_carries_just_the_adapters_message_…` |
| Dap | A generated launch.json keeps `<node_internals>/**` readable (was `<node_internals>/**`) | `JsDebugWaveFixTests.A_generated_launch_json_keeps_skipFiles_patterns_readable` |
| Dap (follow-up b) | Task processes are tracked child processes; `TaskService.Dispose` kills a running task tree (a `tsc --watch` preLaunchTask at shutdown) | `TaskProcessLifetimeTests` (2) |
| Panels | Call Stack: `deemphasize` frames are external code; `label` frames stay visible and inert | `JsDebugPanelFixTests.Node_internals_the_adapter_deemphasizes_…` |
| Panels | Strings already in single quotes are not quoted again | `JsDebugPanelFixTests.Strings_the_adapter_already_quoted_…` |
| Panels | Lazy getters show `(…)` and run only on activation | `JsDebugPanelFixTests.A_lazy_getter_…` |
| Panels | Indexed children page through `filter: "indexed"` | `JsDebugPanelFixTests.Indexed_children_are_paged_with_the_indexed_filter_…` |
| Panels | Pseudo-variables (`Return value`) do not change a frame's shape | `JsDebugPanelFixTests.A_pseudo_variable_that_appears_after_a_step_…` |
| Panels | A stop in a file that is not the active editor opens it at the stop line | `JsDebugPanelFixTests.A_stop_in_a_file_that_is_not_the_active_editor_…`, App `F5_builds_with_tsc_…` |
| Panels + Editor (follow-up a) | Hit counts and logpoints offered only when the active adapter supports them (Breakpoints window and glyph-margin menu: disabled, "(not supported)", tooltip with the reason; the edit commands say why) | `JsDebugPanelFixTests.Hit_counts_and_logpoints_are_offered_only_…`, Editor `DebugGateAndRevealTests.Hit_counts_and_logpoints_…` |
| Editor (follow-up c) | The stop line is revealed only when it changes | Editor `DebugGateAndRevealTests.The_stop_line_is_revealed_when_it_changes_…` |
| Panels tests + Core doc (follow-up d) | The panels' fake `SetFocusedFrameAsync` throws like the real service; Call Stack/Threads name the frame and the reason; `IDebugFrameFocus` documents that it throws | `JsDebugPanelFixTests.The_test_fake_refuses_a_frame_…`, `A_frame_the_service_refuses_keeps_the_arrow_…` |

### js-debug quirks the client handles (adapter behaviour, v1.140.0)

* **`__workspaceFolder` is required for source maps.** The standalone server applies `resolveSourceMapLocations: ["${workspaceFolder}/**",
  "!**/node_modules/**"]`; without `__workspaceFolder` (VS Code's extension always sets it) it drops every `${workspaceFolder}` entry, no source
  map is ever resolved, and `.ts` breakpoints stay "provisional" forever (seen in a raw DAP probe). The provider sets it to the workspace folder
  (else `cwd`, else the program's directory).
* `supportsTerminateRequest: false` → stop sends `disconnect{terminateDebuggee}`; the parent's courtesy `disconnect` after `terminated` is never
  answered (1 s timeout, then the transport is closed and the server killed).
* The server never exits by itself: it is killed with its session.
* The child gets `initialized` twice (before and after the `launch` response); thread id `0`; `allThreadsStopped: false`; `continue` answers
  `allThreadsContinued: false` — all handled.
* `exceptionInfo` puts the text in `exceptionId` ("Error: boom") and leaves `description`/`details.message` empty; its stack is the raw JS stack.
* Async parent frames have no variables ("Variables not available in async stacks").
* Without a filter, `variables` ignores `start`.

### Known gaps (not fixed)

* `debug:` sources: selecting a skipped `<node_internals>` frame opens a read-only `debug:/…` document with the adapter's source (by design).
* The Exception window's stack trace is js-debug's raw JavaScript stack (`out/main.js:13:11`), not mapped to `.ts`.
* `outputCapture: "console"` (js-debug's default, kept in the templates as in VS Code) does not show `process.stdout.write` output; users who
  need it set `outputCapture: "std"` or `console: "integratedTerminal"` (output then comes through our `runInTerminal`, still in the Debug Console).
* Debug-lane stall of ~100 ms while a flood of large output events is parsed (32 frames per yield); the UI budget is unaffected.
* `pwa-chrome` / browser debugging is out of scope; the factory handles `pwa-node`/`node` only.

### Running the suites

```bash
tools/fetch-js-debug.sh                     # or JSDEBUG_ARCHIVE=<archive> …, or point CSCODE_JSDEBUG at a js-debug directory
dotnet test tests/CsCode.Dap.Tests --filter "Category=RealAdapter"           # RealJsDebugTests (+ the netcoredbg ones)
dotnet test tests/CsCode.Panels.Debug.Tests --filter "Category=RealAdapter"  # RealJsDebugPanelsTests, RealJsDebugResponsivenessTests
dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~RealAppTypeScriptDebugTests"
```

Every real test skips with the reason when `node`, `tsc` or js-debug is missing. Run them with `DISPLAY` unset and `CSCODE_HOME` pointing at a
temp directory; set `CSCODE_DAP_TEST_METRICS=<file>` to collect the evidence lines quoted above.

## Rust through GDB

**Not evaluated yet.** The Rust/GDB round (MUST 1, the Rust rows of MUST 3) comes next; CodeLLDB is not approved.
