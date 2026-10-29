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
* **Server process**: `node <js-debug>/src/dapDebugServer.js <socket path>` started through the `DebugAdapterServerProcess` descriptor
  (`src/CsCode.Dap/Transport/ServerProcessTransport.cs`): `nice -n 10` (below normal; the debuggees it spawns inherit it), stdin closed, stdout/stderr
  drained into bounded tails, the line `Debug server listening at <socket path>` read with a timeout (30 s; a server that exits first fails at
  once with its own stderr), then a connection to the socket (`UnixDomainSocketEndPoint`). The process is tracked
  (`IMemoryMonitor.TrackChildProcess` through the session's `AdapterProcessId`) and owned by the parent session's transport.
* **Private socket, no TCP port** (`src/CsCode.Dap/Transport/PrivateSocket.cs`): the server listens on `dap.sock` in a fresh directory only the
  user can enter — mode 0700, named `cscode-jsdebug-<pid>-<start time>-<random>`, under `$XDG_RUNTIME_DIR` when set (`/run/user/<uid>`, itself
  private), else the temp directory (`/tmp` when the path would be too long for a socket). `dapDebugServer.js` listens on any argument that is not
  a number as a path (its usage line: `[port|socket path=8123] [host=localhost]`). So no other local process — another user on a shared machine
  included — can connect to the adapter and have it launch programs as the user, which the earlier `127.0.0.1:<port>` allowed (code review,
  reproduced). The directory is deleted when the session's transport closes, whatever ended it (stop, the program's exit, a crash of the server,
  the debug service's shutdown, a synchronous container dispose), and when the start fails; the directories of a CsCode that was killed outright
  (creator pid gone, or reused by another process) are removed at the next js-debug start. Windows: a named pipe
  `\\.\pipe\cscode-jsdebug-<random>` through the existing pipe transport (written, not exercised here).
* **Child sessions**: js-debug answers `launch` and then asks for the program's session with `startDebugging` (request `launch`, configuration
  `{type, name, __pendingTargetId}`). `DebugService` starts it as a child session that opens **its own connection to the same socket**
  (`DebugSession.AdapterEndpoint`, a `DebugAdapterNamedPipe`; the factory is not asked again — it would start another server that does not know
  the pending target). Breakpoints, threads, stops and variables live in the child; the parent only orchestrates.
* **Lifetime**: the server and every child connection end with the session — stop (Shift+F5), stop of the child alone, restart, debuggee exit,
  adapter crash, app shutdown (`DebugService.DisposeAsync`), and a container disposed synchronously (`DebugService.Dispose`, e.g. a test host:
  it kills every adapter process tree and `runInTerminal` debuggee **at once**, because the asynchronous shutdown runs on the debug lane, which the
  same container dispose ends right after). Closing the parent's transport kills the server's **process tree** (the debuggees it launched, and
  their children) and deletes its socket directory. Processes that **left** the tree are left alone on a normal close — a program may detach a browser (`xdg-open`) or a daemon on
  purpose, and VS Code leaves those running too. Only when the server **died on its own** (crash, external kill) are its orphans reaped: the
  processes carrying its environment marker (`CSCODE_DAP_ADAPTER=<guid>`, which the server passes on to everything it launches) that are still
  in the server's session (a process that detached into a session of its own, `setsid` / Node's `detached: true`, is spared). Like js-debug's
  own tree kill, Stop does end a detached process whose parent (the program) is still running, since it is still in the tree. Children of an
  ended parent are stopped.
* **Locating js-debug**: setting `cscode.debug.jsDebugPath` → environment variable `CSCODE_JSDEBUG` → the installed copy
  `{ToolsDir}/js-debug/v1.140.0/js-debug`; any of them may name the version directory, the `js-debug` directory, its `src/` or
  `dapDebugServer.js`. Node.js: setting `cscode.debug.nodePath` → `node` on PATH.
* **Installer** (`JsDebugInstaller`): when nothing is found the user is **asked** ("Download" / "Cancel"; nothing is fetched otherwise) and the
  **pinned** `js-debug-dap-v1.140.0.tar.gz` is downloaded from the GitHub release, refused unless its SHA-256 is
  `27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968` (1,249,709 bytes), extracted by the zip-slip-safe tar extractor into a staging
  directory and moved into place atomically. `tools/fetch-js-debug.sh` does exactly the same for tests and development
  (`JSDEBUG_ARCHIVE=<file>` installs from an archive you already have without any download; `JSDEBUG_DEST=<dir>` installs elsewhere).
* **Configuration provider**: fills in `__workspaceFolder` (see the quirks below), leaves child configurations alone, never overrides the
  user — except `console: "integratedTerminal"`/`"externalTerminal"` on a launch: CsCode has no terminal for debuggees, so such a launch runs in
  the Debug Console with js-debug capturing the program's streams (`console: "internalConsole"`, `outputCapture: "std"` unless the user set a
  capture): every line once, `process.stdout.write` included, as a terminal would show it. Templates (*Add Configuration* / *Create
  launch.json*): **TypeScript: Launch (tsc build)** (`program: ${workspaceFolder}/src/<entry>.ts`, `preLaunchTask: "tsc: build"`, `outFiles` from
  tsconfig's `outDir` as tsconfig means it — `../build` in a monorepo package, `.out`, an absolute path; an `outDir` outside the folder is also
  added to `resolveSourceMapLocations`; no `outDir` → `${workspaceFolder}/**/*.js` next to the sources — `sourceMaps: true`,
  `skipFiles: ["<node_internals>/**"]`) with the `tsc: build` task written to `tasks.json` as a **process** task (`tsc -p <tsconfig>`: no shell
  re-reads a folder path containing `$`, quotes or spaces; Windows keeps a shell task, tsc is a `.cmd` there); **Node.js: Launch Program**
  (`package.json` `main` as written, or `index.js`); **Node.js: Attach (--inspect)** (port 9229). The TypeScript template was run end to end: its
  preLaunchTask compiles with tsc and the `.ts` program stops at a breakpoint, also with `"outDir": "../build"`.

### Toolchain notes

* **TypeScript compiler**: `/usr/local/bin/tsc` reports `Version 7.0.2` (the native TypeScript 7 compiler). `tsc -p` with `sourceMap: true`,
  `outDir: out`, `rootDir: src` writes `out/main.js` + `out/main.js.map` with `"sources": ["../src/main.ts"], "sourceRoot": ""`; js-debug resolves
  these maps, binds `.ts` breakpoints and reports `.ts` frames. The generated JavaScript has different line numbers than the TypeScript (class
  fields, split try/catch), so the tests prove the mapping. Offline there is no `@types/node`; the sample declares the little of `process` it uses.
* **Node.js** v24.21.0 (`/usr/bin/node`) runs both js-debug and the debuggee. The machine's system update replaced v20.19.2 with it on
  2026-10-29 (11:04–11:14); the earlier rounds' evidence was taken on v20.19.2, and **every real js-debug test was run again on v24.21.0**
  (three runs in a row; the numbers below are from those runs unless a row says otherwise).

### Node 20 vs Node 24 (what changed for the debugger)

* **TDZ locals are hidden.** V8 in Node 24 no longer lists a `let`/`const` before its declaration ran (Node 20 listed it as `undefined`): in
  `add`, Locals show `a`, `b`, `this` before `const sum = a + b` and `sum` only after it. The variable names of a frame now change between two
  stops of the same function, which switched change highlighting off for the whole frame (the frame was recognised by its names): `sum` was no
  longer red after the step. **Fixed** — the frame is recognised by its function (row "Change highlighting after a step").
* **Node internals frames are named `<anonymous>`.** The skipped `<node_internals>` frames that were `Module._compile`, `Module.load`, … are
  `<anonymous>` (plus `wrapModuleLoad`, `executeUserEntryPoint`) in Node 24's stacks; they fold into `[External Code]` the same way.
* **Floods are faster.** The 1,000,000-line program finishes sooner (batched `process.stdout.write`: ~1.0 s instead of ~1.3–1.4 s; one
  `console.log` per line: ~28–33 s instead of ~39–42 s), so the Debug Console receives the same output in less time.

### The matrix

Evidence is what the real adapter sent and what the panels showed, recorded by the tests (`CSCODE_DAP_TEST_METRICS=<file>` collects it).
Line numbers are 1-based, as the editor shows them (the loop breakpoint of the sample is `main.ts:51`; earlier revisions of this page said 50).
Sample: `tests/CsCode.TestDoubles/JsDebugSample.cs` (`src/main.ts` + `src/util.ts`, async calls, a class with a getter, Map, Set, a 250-element
array, exceptions, flood/serve modes), compiled by the real tsc in each test's temp directory.
Tests: **D** = `tests/CsCode.Dap.Tests/RealJsDebugTests.cs` (real `DebugService`), **P** = `tests/CsCode.Panels.Debug.Tests/RealJsDebugPanelsTests.cs`
(the panels' view-models over the real DAP module), **R** = `RealJsDebugResponsivenessTests.cs`, **A** = `tests/CsCode.App.Tests/RealApp/TypeScriptDebugTests.cs`
(the real application).

| Row | Status | Evidence | Test |
|---|---|---|---|
| Launch + clean exit, nothing left running | **works** | Parent `launch` → `startDebugging` → child connection to the same socket; the program prints and exits; both sessions end; the js-debug server pid and every process carrying its marker are gone (checked after every real test). Soak: 10 runs → 20 sessions collected after forced GCs, 11 servers gone, threads 21 → 21/22. | D `Launch_stops_at_a_ts_breakpoint_in_the_child_session_and_exits_cleanly`, `Ten_real_sessions_…` |
| Line breakpoints in `.ts` (source maps) | **works** (after the `__workspaceFolder` quirk is handled) | `setBreakpoints` on `src/main.ts` answers `verified: false, "breakpoint.provisionalBreakpoint"`, then a `breakpoint` event `changed, verified: true, line 51` once the map is loaded; the stop is reported at `src/main.ts:51`. A second file (`util.ts`) binds too. | D, P, A |
| Breakpoint added while stopped | **was broken → fixed** | Stopped at `main.ts:51`, a breakpoint added at `main.ts:58`: both sessions get the file's list again. The orchestrating parent answers every breakpoint `verified: false, "Unbound breakpoint"`; the child answers `verified: true` (line 51 unchanged, so not re-reported, and the new one at 58). Before: the newest verdict won, so the Breakpoints window showed `main.ts : 51` as "Unbound breakpoint" (hollow glyph) although the program still stopped there (integrator, real display). Now a session that verified a breakpoint wins over sessions that did not (VS Code's rule); continuing stops at 51 again with the verdict verified. | D `Adding_a_breakpoint_while_stopped_keeps_the_other_breakpoints_of_the_file_verified`, fix test below |
| Moved line (breakpoint on a comment line) | **works** | Requested line 43 → stopped at 44; verdict moved to line 44; Breakpoints window: `main.ts : 43 Moved note='moved to line 44'`. | D `A_breakpoint_on_a_line_without_code_…`, P |
| Conditional breakpoint | **works** | `i === 3` stops once with `i = 3`, `x = 44`, then runs to the end. | D |
| Hit-count breakpoint | **works** | `"3"` (js-debug: `=3`) stops on the third pass, `i = 2`. js-debug evaluates *either* a condition *or* a hit count (a condition wins when both are set) — adapter behaviour. | D |
| Logpoint | **works** | `logged i={i} x={x}` → five `stdout` output events `logged i=0 x=41` … `logged i=4 x=51` with the source location, no stop. | D |
| Function breakpoints | **adapter limitation** (reporting **fixed**) | js-debug v1.140.0 advertises `supportsFunctionBreakpoints: false`. Before: nothing sent and **no verdict at all** (the breakpoint looked pending forever). Now: reported unverified, "This debug adapter does not support function breakpoints." | D `Function_breakpoints_are_reported_unsupported_by_js_debug`; fix test below |
| Exception breakpoints, caught | **works** | Filters advertised: `all` (Caught Exceptions, default off, supports conditions), `uncaught` (default off). With `all`: stop reason `exception` at `throw new Error("boom")`; `exceptionInfo` → `exceptionId: "Error: boom"`, empty description/details message, `breakMode: all`. Exception window: type `Error: boom`, stack `at fails (…/out/main.js:13:11)` — js-debug's stack text is **not** source-mapped (adapter). | D, P `The_exception_window_shows_a_javascript_exception` |
| Exception breakpoints, uncaught | **works** | With `uncaught` only: the caught throw passes, the uncaught one (in a timer callback) stops at its line, "Paused on exception"; continuing ends the program with its error on stderr. | D |
| Step over / into / out | **works** | From `const r = await work(x)`: into → `main.ts:11` (`await delay(1)`), over the await → `main.ts:12`, into → `util.ts:2` (`add`), over → `util.ts:3`, out → `main.ts:13`; reasons `step`. | D `Step_into_over_and_out_across_ts_files_and_awaits` |
| Pause | **works** | Running timer loop → `pause` → stopped reason `pause` in the timer callback (`main.ts:33`), frames `listOnTimeout@timers…` below it. | D |
| Restart | **was broken → fixed** | Before: Restart (Ctrl+Shift+F5) sent `restart` to the focused child session; js-debug closed that connection ("The debug adapter closed the connection"). Now the command restarts the top session; js-debug relaunches the program as a new child; stopped again at the breakpoint after ~700–750 ms (~370 ms earlier); the old child terminated. | D `Restart_runs_the_program_again_in_a_new_child_session` |
| Stop | **works** | Shift+F5 while stopped: every session gone after ~240–270 ms (this round's runs; ~150 ms on a quieter machine earlier); while the program runs: ~220–280 ms; stopping only the child: both end after ~35 ms; app shutdown (`DisposeAsync`) while stopped: ~230–250 ms; a program that started a second Node.js process (auto-attached as a grandchild session): ~330–390 ms, both programs gone; nothing left running in any case. | D `Stop_while_…`, `Stop_while_the_program_runs_…`, `Stop_ends_a_program_that_started_another_node_process_…` |
| Detached processes | **was broken → fixed** | A program that detaches a process on purpose (`spawn("sleep", ["600"], {detached: true}).unref()`, how `open`/`xdg-open` start a browser) and ends normally: the detached process keeps running. Before: every session end SIGKILLed every process that inherited the adapter's marker, so a browser the program opened died with the session (code review). | D `A_process_the_program_detached_on_purpose_…`, fix tests below |
| Synchronous dispose (test host, container) | **was broken → fixed** | `RealAppHost` disposed while stopped at a breakpoint (no Shift+F5, no AppShutdown): the server and the program are gone ~350–450 ms later (`DebugService.Dispose` with the debug lane blocked: ~300–320 ms). Before: `DebugService.Dispose` only started the asynchronous shutdown, which runs on the debug lane that the same container dispose ends; the server (never exits by itself) survived — two servers from failed test runs were found running an hour later. | A `Disposing_the_host_while_stopped_…`, D `Disposing_the_debug_service_synchronously_…` |
| Adapter crash | **works** | Killing the js-debug server (SIGKILL) while stopped: both sessions end ("The debug adapter closed the connection."), the orphaned `node ./out/main.js` is reaped through the marker; a process the program had detached into its own session keeps running. | D `An_adapter_crash_…` (2) |
| Private socket, no TCP port | **was a gap → fixed** | The server was started on `127.0.0.1:<port>`: any local process could connect and have it launch programs (code review, reproduced). Now `node dapDebugServer.js /run/user/1000/cscode-jsdebug-<pid>-<start>-<random>/dap.sock`: the server holds **no TCP listener** (its `/proc/<pid>/fd` sockets against `/proc/<pid>/net/tcp`/`tcp6`: none), listens on that path (`/proc/<pid>/net/unix`), the directory is mode `700`, owner uid 1000 (the user); the program's session connects to the same path; the directory is gone after every kind of end (the socket check every real test makes: stop, child stop, program exit, restart, crash, service shutdown — at once when `DisposeAsync` returns — synchronous dispose, the real app's Shift+F5 and host dispose); a stale directory of a killed CsCode (dead pid) is removed at the next start. The **program** still has a TCP listener: Node's inspector, which js-debug's bootloader opens in the debuggee (`tcp 0100007F:…`; known gaps). | D `Js_debug_listens_on_a_private_unix_socket_…`, `Shutting_the_debug_service_down_…`, rig check; `PrivateSocketTests`; A (2) |
| Call stack: source-mapped `.ts` frames | **works** | `add@src/util.ts:2`, `work@src/main.ts:12`, `main@src/main.ts:54`, `<anonymous>@src/main.ts:62`. js-debug names sources relative to the workspace, so the Call Stack shows `src/main.ts : 12`. | D, P, A |
| Call stack: async frames | **was partial → fixed** | js-debug inserts a frame `await` (`presentationHint: label`, no source) between `work` and `main`. Before: with *Show External Code* off it was folded into `[External Code]`, hiding where the asynchronous part starts. Now it stays visible (greyed) and is not a frame one can switch to. Selecting the async parent `main` shows js-debug's own message in Locals: "Variables not available in async stacks" (adapter limitation). | P, fix test below |
| Call stack: skipped Node internals | **was partial → fixed** (twice) | `skipFiles: ["<node_internals>/**"]` frames come with `presentationHint: "deemphasize"` on the frame and its source (`<node_internals>/internal/modules/cjs/loader`, a `sourceReference`). First: shown as normal frames, never folded. Then greyed, but folded only with *Show External Code* off — which is not the default, so the real app listed `Module._compile`, `Module.load`, … `executeUserEntryPoint` one by one (integrator, real display). Now a run of two or more frames the adapter skips folds into one `[External Code]` row by default, as VS Code collapses them; clicking the row shows them, greyed, until the next stop. Node.js 20: 6 frames (`Module._compile` …); **Node.js 24**: 7 frames, most named `<anonymous>` (`<anonymous>@<node_internals>/internal/modules/cjs/loader : 1929`, … `wrapModuleLoad`, `executeUserEntryPoint`, `<anonymous>@…/run_main_module : 33`), folded the same way. | P, fix test below |
| Locals: objects, classes, nested | **works** | `p = Point {x: 3, y: 4}`, `nested = {name: 'outer', inner: {…}}`, expandable; scopes `Local: main` (hint `locals`) and `Closure` (hint `arguments`) shown as groups; `Global` is not shown (Locals shows the scopes hinted locals/arguments); inside a `for` body a `Block` scope comes first. | D, P |
| Locals: strings | **was broken → fixed** | js-debug quotes strings itself (`'hello'`); the panels quoted again: `"'hello'"`. Now shown as `'hello'`. | P, fix test below |
| Locals: Map / Set | **works** | `Map(2) {size: 2, a => 1, b => 2}`, entries `0={"a" => 1}`, `1={"b" => 2}`, `size=2`, `[[Prototype]]=Map`; `Set(3) {size: 3, 1, 2, 3}`. | D, P |
| Locals: getters | **was partial → fixed** | js-debug sends getters as `presentationHint.lazy` with the getter's source as value (`ƒ length() { return Math.sqrt(… }`) and type `function`; expanding returns the value. Before: the source text was shown as the value. Now `(…)`, no type (was `function`, the accessor's type — integrator); Enter/double-click runs the getter and shows `5`/`7` and its type `number` in place (`DebugVariable.IsLazy`). A getter that **throws**: js-debug answers with the getter function's own properties (`arguments`, `caller`, `length`, `name='get boom'`…) and no error; was shown as `ƒ ()` (the first of them), now "The debugger did not compute a value: the getter may have thrown an exception." At the next stop an evaluated getter is a placeholder again and is not marked changed (it was compared with its computed value). | D, P `Locals_page_a_ten_thousand_element_array_…`, fix tests below |
| Locals: arrays > 100 items, paging | **was broken → fixed** (twice) | `big`: `(250) [0, 2, 4, …]`, `indexedVariables: 250`, `namedVariables: 1`. js-debug ignores `start` without a `filter` (a request for 100..199 answers 0..99), so the pager stopped after the first 100 elements (the duplicate-guard saw page 0 again). Now pages use `filter: "indexed"` (new `IDebugSessionVariablePaging`): `length`, `[[Prototype]]` and all 250 elements in order (js-debug appends a second `[[Prototype]]` to the last indexed page; names are de-duplicated). `huge` with **10,000** elements: after 100 pages all 10,000 (`9999=29997`) and no notice; was 9,998 elements and "Showing the first 10,000 of 10,000 items" (the named `length`/`[[Prototype]]` counted against the 10,000 cap — evidence review). Longer arrays show their first 10,000 elements and "Showing the first 10,000 of 10,050 items". | D, P `Locals_page_a_ten_thousand_element_array_…`, fix tests below |
| Change highlighting after a step | **was broken → fixed** (twice) | After stepping over `const sum = a + b`, js-debug adds a `Return value` pseudo-variable; the frame "shape" (its variable names) changed, so nothing was compared and `sum` (undefined → 52) was not red; pseudo-variables then no longer counted. **Node.js 24** does not list `sum` before the step at all (TDZ): before `a=100 \| b=1 \| this=undefined`, after `a=100 \| b=1 \| Return value=101 \| sum=101 \| this=undefined` — the names changed again and `sum` was not red (RealJsDebugPanelsTests:261 failed). Now a frame is recognised by its function (session, thread, frame name and source; one `stackTrace` per stop location shared by Locals and Autos), so variables that appear or disappear do not switch the comparison off; `sum` is red as a **new** local (`sum=101*`), `a` is not; a block scope coming and going changes nothing for the others. | P, fix tests below |
| Autos | **works** (documented approximation) | In `add`: `a=100 \| b=1` (Locals minus `this`; Node.js 20 also listed `sum=undefined`, Node.js 24 lists `sum` only once assigned). | P |
| Watch | **works** | `a + b = 101` in `add`; `x` → error. Error text was `'evaluate' failed: Uncaught ReferenceError: x is not defined` → now js-debug's own message. | P, fix test below |
| Set variable | **works** | `setVariable` on the `Local` scope: `x = 100`, the REPL then sees 101, and the program continues with it (`add(100, 1)`); also through the Locals inline editor. | D, P |
| Debug Console: REPL + completions | **works** | `x * 2 → 102`, `[...m.keys()].join() → 'a,b'`; completions for `s.toUpp` → `toUpperCase`, for `Ma` → `Map`, `Math`. Errors show js-debug's message (with its stack, e.g. "Uncaught ReferenceError ReferenceError: nope is not defined at eval (repl:1:1)"). | D, P |
| stdout / stderr in the console | **works** (terminal launch **fixed**) | `loop 0…4` category `stdout` (with source location), `err 52` category `stderr` (red), `/usr/bin/node ./out/main.js` category `console`. Adapter behaviour: with the default `outputCapture: "console"` only `console.*` calls are shown — `process.stdout.write` text is not; `outputCapture: "std"` captures the streams (plus node's own "Debugger attached." on stderr). `console: "integratedTerminal"` used to go through our `runInTerminal` (the program's streams piped into the Debug Console) while js-debug *also* reported every `console.log` itself: `flood done` appeared twice (evidence review). There is no terminal for debuggees, so such a launch now runs in the console with `outputCapture: "std"`: every line once, `process.stdout.write` included. Console output can arrive just after the `stopped` event (js-debug buffers it). | D `Program_stdout_and_stderr_…`, `Raw_stdout_writes_…`, P |
| Attach to `node --inspect` | **works** | `node --inspect=127.0.0.1:0 out/main.js serve` → attach `{port, address}` → child session "Remote Process [0]" (attach) stops at the timer breakpoint, Locals show `ticks`; Stop detaches and leaves the program running (the test kills it). | D `Attach_to_a_node_process_…` |
| 1M lines printed, UI responsive | **works** (history **fixed** twice) | Two shapes. **Batched** — `process.stdout.write` of 10,000-line chunks, 1,000,000 lines, `outputCapture: "std"`: the session takes ~1.3–1.4 s; 18–22 output events reach the service, ~41–54 K lines delivered, ~17 M chars dropped by design (drop-oldest, marker line); heap +1.9–2.2 MB; with the Debug Console on screen (headless window) **dispatcher p99 7.6–10.2 ms, max 13.4–30.4 ms** (budget 16/50) after a warm-up run; debug lane round trip p99 15–18 ms, max 135–144 ms (32-frame batches of ~170 KB events; the lane, not the UI). **One `console.log` per line** (default capture, the realistic shape the evidence review asked for): 1,000,002 DAP output events over the two connections, the session takes ~39–42 s (node and js-debug at full speed); history 5,000 lines, 0 chars dropped, heap +4.5–11 MB, lane round trip p99 0.5–0.7 ms, max 124–161 ms; with the Debug Console on screen **dispatcher p99 13.3–16.2 ms, max 32–44 ms** (budget 16/50; first attempt within budget in 3 of 4 runs, the fourth passed on its second attempt — the test allows three on this shared machine); console ≤ 5,000 lines. History fixes: it kept 1 line after a flood batch (whole batches were dropped); then each later one-line event copied the whole oldest batch to cut one line (O(batch) under the service lock: 1.6 s CPU, 626 MB allocated for one 5,000-line batch + 5,000 events — code review); now the cut is an offset (4,900 events after a 5,000-line batch: 382 KB allocated, slowest add 0.1 ms). | D `A_million_line_flood_…`, `A_million_console_log_lines_…`, R (2) |
| Templates | **works** (paths and task **fixed**) | `Create launch.json` for `pwa-node` in a TypeScript folder writes the three configurations and the `tsc: build` task; the TypeScript one compiles (Tasks channel: "Task 'tsc: build' finished with exit code 0") and stops in `src/main.ts`. A monorepo package with `"outDir": "../build"`: `outFiles: ["${workspaceFolder}/../build/**/*.js"]` plus `resolveSourceMapLocations`, compiled into `../build`, stops in the `.ts` source (was `${workspaceFolder}/build/**/*.js`: `TrimStart('.', '/')` also turned `.out` into `out` and `/abs/dist` into `abs/dist`). A folder whose path contains `$HOME` compiles (process task; the shell task expanded it). | D `The_typescript_template_…` (2), fix tests below |
| Real application, F5 | **works** | Real composition, `CSCODE_HOME` in a temp dir, headless window: open the folder, F9 in `main.ts`, F5 → tsc → js-debug → stopped; Call Stack shows `main` at `src/main.ts : 51` with the arrow, the editor marks line 51; a breakpoint in `util.ts` (not open) opens `util.ts` at the stop line; Shift+F5 → nothing left running. Disposing the host while stopped leaves nothing running either (row "Synchronous dispose"). | A (2) |
| Editor gutter: lightbulb and breakpoint glyph | **was broken → fixed** | The code-action lightbulb was drawn over the breakpoint glyph (and the stop arrow) of the caret line (integrator, real display). Now it moves into the line's indentation when that is wide enough, else into the gutter of a free neighbouring line (VS Code's rule), and back when the glyph goes. | Editor `DebugGateAndRevealTests.The_lightbulb_is_never_drawn_over_a_breakpoint_glyph` |

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

Second round — what two adversarial reviews (evidence, code) and the integrator driving the real app on a real display found. Regression
tests: `tests/CsCode.Dap.Tests/JsDebugReviewFixTests.cs` (with `AdapterServerProcessLifetimeTests`), `tests/CsCode.Panels.Debug.Tests/JsDebugReviewFixTests.cs`,
plus the real-adapter tests named in the matrix. Each was shown to fail with its fix reverted (temporarily, in the source).

| Module | Fix | Regression test |
|---|---|---|
| Dap | Breakpoint verdicts of several sessions: one that verified the breakpoint wins over newer unverified ones (js-debug's parent said "Unbound breakpoint" after a breakpoint was added while stopped) | `A_session_that_verified_a_breakpoint_wins_over_a_newer_unverified_verdict_of_another_session`; D `Adding_a_breakpoint_while_stopped_…` |
| Dap | A normal session end kills the server's process tree only; orphans are reaped only when the server died on its own, and only those still in its session (a detached browser/daemon survives) | `AdapterServerProcessLifetimeTests` (2); D `A_process_the_program_detached_on_purpose_…`, `An_adapter_crash_reaps_the_orphaned_debuggee_but_not_…`; `JsDebugAdapterTests.It_connects_…_kills_its_tree_on_close_but_not_what_left_it` |
| Dap | `DebugService.Dispose` (synchronous container dispose) kills every adapter process tree and `runInTerminal` debuggee at once (`IKillableTransport`) | `AdapterServerProcessLifetimeTests.Disposing_the_debug_service_synchronously_…`; D `Disposing_the_debug_service_synchronously_while_the_lane_is_busy_…`; App `Disposing_the_host_while_stopped_…` |
| Dap | Console history cuts the oldest event by an offset (O(line) per event, no copy) instead of copying it per event | `A_flood_batch_then_thousands_of_one_line_events_cut_the_batch_in_place_…`, `The_character_budget_cuts_the_head_of_a_single_huge_line` |
| Dap | A child session goes to its parent's adapter server only when it carries js-debug's `__pendingTargetId` or is of the parent's type; a child of another type gets its own type's adapter | `A_child_of_another_debug_type_is_created_by_that_types_factory_…` |
| Dap | Last-known capabilities are recorded under the configuration's own type too (`node` → `pwa-node`), so the breakpoint editors are gated after the session; the fallback is now tested | `Known_capabilities_answer_for_the_type_the_configuration_asked_for_…` |
| Dap | Templates keep tsconfig's `outDir`/`rootDir` and package.json's `main` as written (`../build`, `.out`, absolute); an outside `outDir` is allowed for source maps; no `outDir` → JS next to the sources | `The_typescript_template_keeps_the_outDir_as_tsconfig_means_it` (4), `Without_an_outDir_…`; D `…_whose_outDir_is_outside_the_folder_…` |
| Dap | `tsc: build` is a process task (a `$` in the folder path is not expanded by a shell) | `The_tsc_build_task_is_a_process_task_so_a_folder_whose_path_has_a_dollar_sign_still_compiles` |
| Dap | A terminal launch (`integratedTerminal`/`externalTerminal`) runs in the Debug Console with `outputCapture: "std"`: every line once | `A_terminal_launch_runs_in_the_debug_console_…`; D `Raw_stdout_writes_…` |
| Dap (installer) | The tar extractor refuses symlink targets with `..` segments and never writes through a symlink (a `y -> .`, `x -> y/..` chain wrote outside the destination) | `A_chain_of_symlinks_cannot_make_the_tar_extractor_write_outside_the_destination`, `The_tar_extractor_never_writes_through_a_symlink_…` |
| Panels | Call Stack: runs of frames the adapter skips (`deemphasize`) fold by default; clicking an `[External Code]` row shows its frames | `Node_internals_skipped_through_skipFiles_fold_by_default_and_show_when_their_row_is_clicked` |
| Panels | Variables: the 10,000-element cap counts paged elements only (named children no longer cost the last elements; the notice is true) | `An_array_of_ten_thousand_elements_pages_to_its_last_element_…`, `An_array_longer_than_the_cap_…` |
| Panels | A getter that throws says so (was the first property of the getter function, `ƒ ()`) | `A_getter_that_throws_says_so_…` |
| Panels | An evaluated getter is not marked changed at the next stop | `A_getter_that_was_evaluated_is_not_marked_changed_…` |
| Panels | A lazy value shows no type until evaluated (was `function`) | `A_lazy_getter_shows_no_type_until_it_is_evaluated_…` |
| Editor | The lightbulb moves out of the way of the breakpoint glyph / stop arrow | `DebugGateAndRevealTests.The_lightbulb_is_never_drawn_over_a_breakpoint_glyph` |

Third round — what the final review of the second round left, and the move to Node.js 24. Regression tests:
`tests/CsCode.Panels.Debug.Tests/ChangeHighlightTests.cs`, `tests/CsCode.Dap.Tests/PrivateSocketTests.cs`, plus the real-adapter tests named.
Each was shown to fail with its fix reverted (temporarily, in the source).

| Module | Fix | Regression test |
|---|---|---|
| Panels | Change highlighting recognises a frame by its **function** (session, thread, frame name and source, from one `stackTrace` request per stop location that Locals and Autos share), not by its variable names: variables that appear or disappear between two stops keep the others compared, a variable that appeared is marked changed (Visual Studio shows new locals red), a block scope coming and going changes nothing for the others. Node.js 24 hides `const sum` until it is assigned, so the names changed and `sum` was not red after the step. Without the frame's identity (stack unreadable) the names key it as before. | `ChangeHighlightTests`: `Variables_that_appear_or_disappear_…`, `A_block_scope_that_comes_and_goes_…`, `Locals_and_autos_share_one_stack_request_…` (fail without it), `Without_the_frames_identity_…`, `A_caller_frame_is_not_given_the_identity_of_an_earlier_stops_caller_…` (the location keys the shared request only for its own frame); `VariablesTests.Stepping_into_another_function_…` now steps into a real other function with a reused frame id; P `The_panels_show_a_typescript_session_…` |
| Dap | js-debug listens on a **private Unix socket** (`dap.sock` in a fresh 0700 directory under `$XDG_RUNTIME_DIR`, else the temp directory), never on a loopback TCP port; child sessions connect to the same path; the directory is deleted at every session end and after a failed start; directories of a killed CsCode are removed at the next start; a named pipe on Windows | `PrivateSocketTests` (the deletion at close, after a crash, by `KillNow` and after a failed start, and the stale cleanup: each fails without its part); `JsDebugFactoryTests.It_starts_dapDebugServer_on_node_on_a_private_socket_…`; D `Js_debug_listens_on_a_private_unix_socket_…` and the socket-directory check of every real test (crash, stop, shutdown, synchronous dispose, restart…); App `F5_builds_with_tsc_…`, `Disposing_the_host_…` |
| Dap | The debug service's shutdown (`DisposeAsync`) waits (bounded, 10 s) for the sessions its Stop ended to release their adapters — the server killed, its socket directory deleted — instead of returning while that ran in the background (an app exiting right after could leave a socket directory; the full Dap run's rig found one); the transport deletes the directory right after killing the server, not after its exit | `PrivateSocketTests.Shutting_the_debug_service_down_returns_only_after_ended_sessions_released_their_adapters` (a lingering stdio adapter: fails without the wait); D `Shutting_the_debug_service_down_while_stopped_…` (the directory gone when `DisposeAsync` returns) |

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
* The orchestrating parent session answers `setBreakpoints` too, always unverified ("Unbound breakpoint" once the program runs); the child's
  verdict is the real one (verdicts are merged, a verified one wins).
* A getter (`presentationHint.lazy`) is typed `function` until evaluated; a getter that throws is answered with the getter function's own
  properties and no error.
* Frames skipped through `skipFiles` carry `presentationHint: "deemphasize"` on the frame and on its source.

### Known gaps (not fixed)

* `debug:` sources: selecting a skipped `<node_internals>` frame opens a read-only `debug:/…` document with the adapter's source (by design).
* The Exception window's stack trace is js-debug's raw JavaScript stack (`out/main.js:13:11`), not mapped to `.ts`.
* `outputCapture: "console"` (js-debug's default, kept in the templates as in VS Code) does not show `process.stdout.write` output; users who
  need it set `outputCapture: "std"` or `console: "integratedTerminal"` (which now means exactly that, in the Debug Console).
* Debug-lane stalls of ~100–160 ms during output floods (32 frames per yield, whatever their size); the UI budget is unaffected, but other
  sessions on the lane can wait that long. A byte-based yield would bound it.
* **The program's own Node.js inspector listens on a loopback TCP port.** js-debug's server no longer opens a TCP port (it listens on the
  private socket), but to debug the program its bootloader opens Node's inspector **inside the debuggee** on a random `127.0.0.1` port (seen in
  the test: `debuggee pid … (/usr/bin/node ./out/main.js): TCP listeners [tcp 0100007F:…]`), as VS Code's js-debug does. A local process that
  finds that port can read the WebSocket address from `/json/list` and attach to the program while it is debugged. Not changed (js-debug's
  bootloader decides it). js-debug's own IPC sockets (`/tmp/node-cdp.<pid>-….sock`) are Unix sockets created with the user's umask.
* Stop kills the program's whole process tree, including a process it detached that is still its child (js-debug's own tree kill does the
  same); one that already left the tree survives.
* `pwa-chrome` / browser debugging is out of scope; the factory handles `pwa-node`/`node` only.
* **A js-debug server never exits by itself**, unlike a stdio adapter (netcoredbg exits when its stdin closes). Every way a session ends kills
  it, and so do the app's shutdown paths (graceful: the debug service's `DisposeAsync`; synchronous container dispose: `DebugService.Dispose`
  kills it at once — this was the root cause of the two servers that failing real-composition test runs left behind, and the test's
  `finally` workaround is gone; abrupt: `ChildProcessTracker.KillSurvivorsNow`, which knows the server through `IMemoryMonitor.TrackChildProcess`).
  If CsCode itself is killed (SIGKILL, crash) during a session, the server and its debuggee survive; its socket directory stays until the next
  js-debug start removes it (the orphaned server can then no longer be reached).

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
