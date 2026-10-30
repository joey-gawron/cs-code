# Debugging Rust and TypeScript — evaluation and fixes

User request (2026-10-29): *"can you see how well the debugger feature works with rust and typescript"* — the user approved the js-debug download
and asked to start with it; Rust followed with GDB. Wave 3 (user decisions 2 and 3, "Get all the downloads you need", "You can run the tests")
approved CodeLLDB and running it: it is built in beside GDB and its matrix ran against the real adapter (see [Rust through CodeLLDB](#rust-through-codelldb)).
This document records the evaluation matrix of `docs/briefs/debugger-rust-typescript.md` (MUST 3) with evidence, what was fixed (each with a
regression test that fails without the fix), and what is an adapter limitation.

| Language | Adapter | Status |
|---|---|---|
| **TypeScript / JavaScript** | Microsoft **js-debug v1.140.0** (`js-debug-dap`, MIT), built in | **evaluated, defects fixed** — every row below |
| **Rust** (default) | **GDB 18.1**'s native DAP (`gdb -i=dap`, no download), built in; GDB 16.3, which the brief assumed, was replaced by the system update | **evaluated, defects fixed** — [Rust through GDB](#rust-through-gdb) |
| **Rust** | **CodeLLDB v1.12.3** (MIT, with its own LLDB; a pinned 56 MB download the app offers), built in as `lldb` | **evaluated, defects fixed** — [Rust through CodeLLDB](#rust-through-codelldb); [which one is the default](#which-rust-debugger-is-the-default) (GDB stays) |

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
  `{ToolsDir}/js-debug/v1.140.0/js-debug` → the copy tools/install.sh put next to the app (`{InstallDir}/debug-adapters`); the first three may name the version directory, the `js-debug` directory, its `src/` or
  `dapDebugServer.js`. Node.js: setting `cscode.debug.nodePath` → `node` on PATH.
* **Installer** (`JsDebugInstaller`): when nothing is found the user is **asked** ("Download" / "Cancel"; nothing is fetched otherwise) and the
  **pinned** `js-debug-dap-v1.140.0.tar.gz` is downloaded from the GitHub release, refused unless its SHA-256 is
  `27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968` (1,249,709 bytes), extracted by the zip-slip-safe tar extractor into a staging
  directory and moved into place atomically. `tools/fetch-js-debug.sh` does exactly the same for tests and development
  (`JSDEBUG_ARCHIVE=<file>` installs from an archive you already have without any download; `JSDEBUG_DEST=<dir>` installs elsewhere).
* **Configuration provider**: fills in `__workspaceFolder` (see the quirks below), leaves child configurations alone, never overrides the
  user — except `console: "externalTerminal"` on a launch (CsCode opens no external terminal window), and `"integratedTerminal"` when no
  integrated terminal is composed: such a launch runs in the Debug Console with js-debug capturing the program's streams
  (`console: "internalConsole"`, `outputCapture: "std"` unless the user set a capture): every line once, `process.stdout.write` included, as a
  terminal would show it. With the integrated terminal (the app), `"integratedTerminal"` is kept: js-debug sends `runInTerminal` and the
  program runs in a Terminal tab named after the launch configuration (docs/TERMINAL.md, docs/DAP.md). Templates (*Add Configuration* / *Create
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
  need it set `outputCapture: "std"`, or `console: "integratedTerminal"` (the program then runs in the integrated terminal, its output there).
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

Rust is debugged by default with **GDB's native Debug Adapter Protocol support** (`gdb -i=dap`), which needs nothing but the system's GDB
(CodeLLDB, the alternative, is [below](#rust-through-codelldb)).

### How it is wired (MUST 1)

* **Adapter**: built-in debug types `gdb` and its alias `rust-gdb` (rewritten to `gdb`), label "Rust / C / C++ (GDB)", breakpoint languages
  `rust`, `c`, `cpp` (`src/CsCode.Dap/Adapters/GdbAdapterFactory.cs`). GDB is a **stdio** adapter started from a `DebugAdapterStdioProcess`
  descriptor (`src/CsCode.Dap/Transport/DapTransports.cs`): `nice -n 10 gdb [cscode.debug.gdbArgs] -q -iex … -i=dap` — below normal priority
  (the program GDB launches inherits it: both run at nice 10, checked in `/proc`), tracked (`IMemoryMonitor.TrackChildProcess` through the
  session's `AdapterProcessId`), with the environment marker `CSCODE_DAP_ADAPTER=<guid>` that GDB passes on to the program. The transport kills
  GDB's process tree on close; if GDB **died** (killed by a signal, a non-zero exit — not the normal `quit` after `disconnect`, exit code 0),
  the processes still carrying its marker in its session are reaped, as for js-debug's server.
* **What rust-gdb does, on GDB's command line** (`-iex`, before anything loads), found through `rustc` on PATH (run in the workspace folder,
  so rustup honours a `rust-toolchain` file; cached per folder): `source $(rustc --print sysroot)/lib/rustlib/etc/gdb_load_rust_pretty_printers.py`
  when present (String, Vec, HashMap, Rc… by value) and `set substitute-path /rustc/<commit> <sysroot>/lib/rustlib/src/rust` when the rust-src
  component is installed (`rustc -vV`'s commit hash: the standard library's frames then have their sources). rust-gdb's own way — auto-loading
  the printers from the binary's `.debug_gdb_scripts` section through `add-auto-load-safe-path` and `directory` — does **not** load them with
  this toolchain (`info auto-load python-scripts`: `No`), hence the explicit `source`.
* **CsCode's GDB helper** `src/CsCode.Dap/Adapters/Gdb/cscode_gdb.py` (copied next to the assembly, `-iex source …`): a last-resort Python
  pretty-printer for Rust values that have none — GDB's DAP shows a struct, an enum (Option, Result…) or a tuple as an **empty** value, and a
  Box as a bare address. Its summary is GDB's own Rust rendering of the value with type paths dropped (`Point {x: 3, y: 4}`, `Some(5)`,
  `Err("invalid digit found in string")`), bounded (depth 2, 100 elements, 200 characters), never raising, and only while the frame's language
  is Rust; strings, slices and arrays keep GDB's printers, and the children are the ones GDB's DAP shows anyway.
* **Lookup**: setting `cscode.debug.gdbPath` → `gdb` on PATH; otherwise the start fails with "GDB was not found… (GDB 14 or newer speaks the
  Debug Adapter Protocol)". Extra arguments: `cscode.debug.gdbArgs` (e.g. `["-nx"]`, which every test uses so the user's gdbinit files are
  never read). Nothing is downloaded.
* **Configuration provider**: `env` is moved aside and turned into `set environment` / `unset environment` commands (GDB's own `env` REPLACES
  the program's whole environment — PATH, HOME… gone — where VS Code's means "add these"; a value with a line break is refused with a
  message), which GDB reads with `-ix` from a private 0600 file written afresh for each GDB started (a restart starts a new GDB from the same
  descriptor) and deleted with it, never from its command line; `cwd` defaults to the folder; for an attach a textual `pid` (what `${command:pickProcess}` substitutes; VS Code's C++ debugger
  calls it `processId`) becomes the number GDB wants; CodeLLDB's **`"cargo"` key** is resolved: `{"args": ["test", "--no-run"],
  "filter": {"name": …, "kind": "lib|bin|test|…"}, "cwd": …, "env": …}` runs cargo with `--message-format=json-render-diagnostics` (adding
  `--no-run` to `test`/`bench`), its progress and compiler messages go to the Tasks output channel, and the executable cargo reports (after the
  filter; several → a quick pick) becomes `program` — how a test binary, `target/debug/deps/<crate>-<hash>`, is found. The cargo process is
  tracked and killed with its tree when the start is cancelled.
* **Templates** (*Add Configuration* / *Create launch.json*, from `Cargo.toml`): **Rust: Debug <bin> (cargo build)** (`program:
  ${workspaceFolder}/target/debug/<bin>`, `preLaunchTask: "cargo: build"`), **Rust: Debug unit tests of <package> (cargo test)** (the `cargo` key,
  filter `kind: lib` for a package with a library, else its binary), **Rust: Attach to <bin>** (`pid: ${command:pickProcess}`); and the
  `cargo: build` task in `tasks.json` as a **process** task (`cargo build`, `options.cwd` the folder: no shell re-reads the folder's path).
  Without a Cargo.toml: plain GDB launch/attach templates. All three Rust templates were run end to end.
* **What GDB does differently** is compensated in one place, `GdbAdapterQuirks` (`src/CsCode.Dap/Adapters/GdbAdapterQuirks.cs`), the GDB
  instance of the new `DebugAdapterQuirks` (`src/CsCode.Dap/Session/DebugAdapterQuirks.cs`): a built-in adapter carries its quirks
  (`BuiltinDebugAdapter.Quirks`) and every session of its type applies them (see "GDB quirks the client handles").

### Toolchain notes

* **GDB 18.1** (`/usr/bin/gdb`, Debian 18.1-3; its DAP implementation is Python under `/usr/share/gdb/python/gdb/dap/`, read to explain what
  the matrix found). **Rust 1.98.1** (cargo, rustc from Nix; rust-src installed). The sample (`tests/CsCode.TestDoubles/RustGdbSample.cs`: a
  cargo package with `src/main.rs` + `src/util.rs`, no dependencies, so `cargo build --offline` works) is built by the tests in temp
  directories.
* **ptrace**: attaching to a process GDB did not start needs `kernel.yama.ptrace_scope` 0 (it is 0 here; the attach test skips otherwise).

### GDB 16.3 vs GDB 18.1

The brief was written for GDB 16.3; the machine's system update replaced it with **18.1** before this round, and 16.3 is not available
without a download, so **every row below was run on 18.1 only**. What GDB's own NEWS (`/usr/share/doc/gdb/NEWS.gz`) lists between them for
DAP, and what it means here:

* **GDB 17 added the `completions` request.** On 16.3 the adapter advertises no completions: the Debug Console then offers none (the session
  asks only when `supportsCompletionsRequest` is set). Everything else in the matrix uses requests 16.3 already had.
* GDB 16 added the `Globals` scope, the **`Return` scope** (`returnValue` hint) after a step out, and the deferred `launch`/`attach` (answered
  only after `configurationDone`) — all present on 18.1 and handled. GDB 18 adds constants in scopes, Ada's `unhandled` filter, `adaSourceCharset`
  and `coreFile` for attach.
* What earlier docs (VS Code/CodeLLDB habits) assumed and 18.1 + Rust 1.98 do not do: a breakpoint on **`rust_panic`** never binds through
  DAP — Rust 1.98 mangles the symbol as `__rustc::rust_panic` (its debug info calls it `std::panicking::rust_panic`), and GDB's DAP sets function
  breakpoints as explicit locations that do not wildcard-match Rust paths (the CLI's `break rust_panic` does). `std::panicking::rust_panic` binds.

### The matrix

Evidence is what GDB sent and what the panels showed, recorded by the tests (`CSCODE_DAP_TEST_METRICS=<file>` collects it); line numbers are
1-based. Tests: **D** = `tests/CsCode.Dap.Tests/RealGdbRustTests.cs` (real `DebugService`), **P** = `tests/CsCode.Panels.Debug.Tests/RealGdbPanelsTests.cs`
(the panels' view-models over the real DAP module), **R** = `RealGdbResponsivenessTests.cs`, **A** = `tests/CsCode.App.Tests/RealApp/RustDebugTests.cs`
(the real application). Regression tests of the fixes: `tests/CsCode.Dap.Tests/GdbQuirksTests.cs`, `GdbAdapterFactoryTests.cs`,
`tests/CsCode.Panels.Debug.Tests/GdbPanelFixTests.cs`.

| Row | Status | Evidence | Test |
|---|---|---|---|
| Launch + clean exit, nothing left running | **works** | `nice -n 10 gdb -nx -q -iex source …/gdb_load_rust_pretty_printers.py -iex set substitute-path /rustc/48a229ce… …/lib/rustlib/src/rust -iex source …/cscode_gdb.py -i=dap`: gdb and the program at nice 10, the program a child of gdb carrying its marker; it runs to the end (exit code 0, "done hello world 3 2 Some(5) …"); gdb, the program and every marked process gone (checked after every real test). | D `Launch_stops_at_a_breakpoint_and_exits_cleanly_with_gdb_below_normal_priority`, A |
| Line breakpoints | **works** | GDB loads the program only at `configurationDone`, so `setBreakpoints` answers `verified: false, reason: "pending"`; a `breakpoint` event `changed, verified: true, line 97` follows once the program is loaded, before the first stop. | D, P, A |
| Moved line (breakpoint on a comment line) | **works** | Requested 74 → stopped at 75; Breakpoints window: `main.rs : 74 Moved note='moved to line 75'`. | D `A_breakpoint_on_a_line_without_code_…`, P |
| Breakpoint added while stopped | **works** | Added at `@call` while stopped at the loop: verified, hit after Continue; the other breakpoint stays verified. | D `A_breakpoint_added_while_stopped_…` |
| Conditional breakpoint | **works** | `i == 3` stops once with `i = 3`, `x = 44`, then runs to the end. | D |
| Hit-count breakpoint | **partial** (adapter) — translation **fixed** | GDB takes `hitCondition` as an **ignore count** (`bp.ignore_count = int(parse_and_eval(hitCondition))`): "3" sent as is skipped three hits and stopped at the fourth, and `>5`/`%3` were parse errors (breakpoint "failed"). Now `N`/`=N`/`==N` → N−1, `>N` → N, `>=N` → N−1: "3" first stops on the third hit (`i = 2`). GDB consumes an ignore count once, so it then stops on every later hit (`i = 3`, …) — adapter limitation. `%2`, `<5` are not sent: unverified, "GDB cannot break on the hit count '%2': it can only start breaking after a number of hits ('5', '>5' or '>=5')." | D `A_hit_count_breakpoint_first_stops_on_the_requested_hit_…`, fix tests |
| Logpoint | **was partial → fixed** | `logged i={i} x={x}` → five messages, category `console`, no stop. GDB sends each message WITHOUT a line break, so they ran together in the Debug Console; now `logged i=0 x=41` … `logged i=4 x=47`, one per line. | D `A_logpoint_prints_each_message_on_its_own_line_…` |
| Breakpoint on a `println!` line | **adapter behaviour** | Stops twice per pass, both at line 98 with `i = 0`: rustc emits two statement entries for the line (format, print) and GDB 18 puts a location on each. | D `A_breakpoint_on_a_println_line_stops_twice_per_pass` |
| Function breakpoints | **works** (verdict **fixed**) | `sample::util::add` stops at `util.rs:4`. Set before the program loads, GDB answers "pending" and verifies it later by a `breakpoint` event — which was ignored for function breakpoints: the Breakpoints window showed it unverified forever. A bare `add` stays pending (GDB 18 resolves Rust paths only, see above) — adapter. | D `Function_breakpoints_stop_on_a_rust_path_…`, fix test |
| Rust panics (exception breakpoints) | **was missing → added** | GDB advertises Ada and C++ filters only. **"Rust panics"** (on by default, like CodeLLDB's "Rust: on panic") is listed first in the Breakpoints window and sent as a function breakpoint on `std::panicking::rust_panic`; its stop is reported as reason `exception` ("Paused on exception: Rust panics", text "boom at the bottom") at the **user's frame** `util.rs:15` (`std::panicking::rust_panic` and five more std frames above it are de-emphasised), and `exceptionInfo` is answered by the session from the panic message the program printed (`{"exceptionId":"panic","description":"boom at the bottom","details":{"typeName":"panic","stackTrace":"thread 'main' panicked at src/util.rs:15:9"}}`), waiting (bounded) when the message arrives just after the stop. The panic the sample catches (`catch_unwind`) and the uncaught one (exit code 101) both stop. Exception window: "Exception Thrown / panic: boom at the bottom". Off: the program just ends with 101. | D `Rust_panics_stop_as_exceptions_…`, `With_rust_panics_off_…`, P, A |
| Step over / into / out | **works** | From `util::add(x, 1)` (main.rs:100): into → `util.rs:4` (frames `sample::util::add`, `sample::main`), over → `util.rs:6` (the `}`: rustc gives the tail expression `sum` no line of its own), out → back on `main.rs:100`; reasons `step`. | D `Step_into_over_and_out_…`, P |
| Return value after step out | **was hidden → fixed** | GDB 16+ answers `scopes` after a step out with a `Return` scope (hint `returnValue`) holding `(return) = 52`; Locals showed only `locals`/`arguments` scopes. Now Locals shows it as a group ("Return") and Autos first, whatever the line mentions (Visual Studio's "… returned"). | D, P, fix tests |
| Pause | **works** (stop location **fixed**) | Running `serve` → pause → reason `pause` inside libc: `__internal_syscall_cancel` at `./nptl/./nptl/cancellation.c:44` — not on disk, so GDB hands out a `sourceReference` its `source` request cannot serve (`[Errno 2] No such file…`). The stop showed that frame (an error document); now libc and std frames are de-emphasised and the stop shows `sample::serve` at `main.rs:46`. | D `Pause_stops_a_running_program_in_the_users_code_…` |
| Start Without Debugging (Ctrl+F5) | **was broken → fixed** | GDB's `launch` has no `noDebug`: the program ran under the debugger and stopped at the loop breakpoint and at the caught panic. Now a `noDebug` GDB session gets no breakpoints and no exception filters: the program runs to its end ("done hello world …", exit code 0), no stop. | D `Start_without_debugging_runs_the_program_to_its_end_…`, fix test |
| Restart | **works** (client relaunch) | GDB has no `restart` request: the session ends GDB and the program and runs the handshake again — stopped again at the breakpoint after ~660 ms under a new GDB; the old one gone; no `Terminated` in between. | D `Restart_runs_the_program_again_under_a_new_gdb` |
| Stop | **works** | Shift+F5 while stopped (`terminate` → GDB `kill` → `exited`, `terminated`) and while running: every session gone in a few ms; GDB quits after the courtesy `disconnect`. Host disposed while stopped (synchronous container dispose, `IKillableTransport`): gdb and the program gone after ~640 ms. | D `Pause_…`, `Stop_while_the_program_runs_…`, A (2) |
| Adapter crash | **works** | SIGKILL to gdb while stopped: the session ends ("The debug adapter closed the connection."), the program does not survive it; the marker reaping is the safety net (tested with a stand-in that orphans its child). | D `An_adapter_crash_…`, `GdbAdapterFactoryTests.The_processes_a_crashed_stdio_adapter_orphaned_are_reaped_…` |
| Call stack: generics and closures | **works** | `sample::apply<sample::main::{closure_env#4}>` → step into → `sample::main::{closure#4}` above it; `sample::util::describe<i32>`; threads: `sample::main::{closure#1}::{closure#0}`. GDB stops the stack at `main` (`backtrace past-main` off). | D `Generic_functions_and_closures_…` |
| Call stack: standard library | **was partial → fixed** | std frames (`/rustc/<commit>/library/…`, mapped onto rust-src) and frames without debug information (`__rust_try`) were listed one by one between the user's frames. Now de-emphasised and folded: at the panic `[External Code] \| explode : 15 ⇐ \| explode : 17 \| explode : 17 \| {closure#5} : 105 \| [External Code] \| main : 105`; a click shows `std::panicking::rust_panic` … greyed. | P `A_rust_panic_shows_…_folds_the_standard_library`, fix tests |
| Locals: String, &str, Vec, HashMap, Rc | **works** | Rust pretty-printers: `s = "hello"` (`alloc::string::String`), `text = "world"` (`&str`), `v = Vec(size=3)` → `[0]=1 [1]=2 [2]=3`, `map = HashMap(size=2)` → `key0="a" val0=1 key1="b" val1=2`, `shared = Rc(strong=2, weak=0)` → `value="shared" strong=2 weak=0`, `slice = &[i32](size=2)`. | D `Locals_show_rust_values_…`, P |
| Locals: structs, enums, Option/Result, tuples, Box | **was broken → fixed** | GDB's DAP showed them EMPTY (`p = `, `some = `, `err = `) and a Box as an address. Now `p = Point {x: 3, y: 4}`, `some = Some(5)` (child `Some` → `__0 = 5`), `none = None`, `ok = Ok(12)`, `err = Err("invalid digit found in string")`, `shape = Circle{radius: 1.5}`, `rect = Rect(3, 4)`, `empty = Empty`, `pair = (1, "two")`, `wrapper = Wrapper {point: Point {x: 7, y: 8}, tag: Some("w")}`, `boxed = 0x5555555d3e80 Point {x: 5, y: 6}` (child `value`). GDB lists locals latest-declared first. | D, P |
| Locals: a 250-element Vec, paging | **was broken → fixed** | GDB announces only `indexedVariables: 250` and **ignores `filter`** (honours `start`/`count`). The panels asked for the "named" children too (none announced), GDB answered with every element, and the Vec showed `[100]`…`[249]`, then `[0]`…`[99]`. Now named children are asked for only when announced: `[0]`…`[249]` in order, `[249]=498`. | P, `GdbPanelFixTests.A_vector_gdb_announces_only_indexed_children_…` |
| Autos | **works** (documented approximation) | The variables the stop line and the line before mention, else all; after a step out the returned value first. | P |
| Watch | **works** | `p.x + p.y = 7`, `x * 2 = 102`; `nope` → "No symbol 'nope' in current context" (GDB's message). | D, P |
| Set variable | **works** | Locals inline edit `x = 100`: the REPL and `add` see it, the program prints `err 101`. GDB finds the variable among the children it listed (the Locals window lists them first; "no variable named 'x'" otherwise). | D, P |
| Debug Console: REPL + completions | **was broken → fixed** | GDB runs `evaluate` with context `repl` as a **GDB command** (`x * 2` → "Attempt to take contents of a non-pointer value."; `print x` → `$1 = 44`). Now: an expression (`x * 2` → `102`, `v` → `Vec(size=3)`, expandable); `-exec <command>` or `` `<command> `` runs a GDB command (`-exec info locals`); input that is no expression but starts with a GDB command word is retried as one (`p s` → `$1 = "hello"`, `bt`) — only on GDB's syntax errors (the C parser's "A syntax error in expression…", the Rust parser's "Syntax error near 's'") or an unknown single command word, so `x + 1` with no `x` keeps its own error instead of running GDB's `x` (examine memory). Completions are GDB's command completions: an expression is completed as `print <expression>` and mapped back (`p.` → `p.x`, `p.y`; `-exec info lo` → `-exec info locals`). | D `The_debug_console_evaluates_…`, P, fix tests |
| stdout / stderr in the console | **partial** (adapter) — GDB's messages **fixed** | The program's stdout AND stderr reach GDB through one pipe and arrive as `stdout`: `eprintln!("err 52")` is not red (adapter limitation). GDB's own messages on that pipe ("No source file named …", "Breakpoint 1 (…) pending.", "[Thread … exited]") were shown as program output; now as debugger messages (`console`). | D `Program_output_…`, P |
| Threads (`std::thread`) | **works** | `worker-1`, `worker-2` (the stop thread named `worker-…`), the main thread named after the process (`sample`); a thread just spawned may not have its name yet at the first stop (`3=sample`); Linux keeps 15 characters of a thread name (`util::tests::ad`). Worker frames: `sample::worker`, `sample::main::{closure#1}::{closure#0}`, then std's start-up de-emphasised. | D `Threads_of_std_thread_…` |
| Attach to a running process (pid) | **works** | `sample serve` started outside; attach with `pid` as text → number; GDB stops the process it attached to (reason `attach`); Continue → the `@tick` breakpoint, `ticks > 0`; Stop detaches and the program keeps running. | D `Attach_to_a_running_rust_program_by_pid_…` |
| Unit-test binary (`cargo test --no-run`) | **works** (new) | The `cargo` key → `target/debug/deps/sample-c77dd8832268fcec`; stop in `sample::util::tests::adds_numbers` (`util.rs:26`) on the thread `util::tests::ad`; "test result: ok. 1 passed", exit code 0. | D `The_cargo_key_builds_the_test_binary_…` |
| Templates | **works** | Create launch.json for `gdb` in a cargo folder: the three configurations and the `cargo: build` process task; the binary one builds (Tasks: "Task 'cargo: build' finished with exit code 0") and stops in `main.rs`, the unit-test one builds the tests and stops in `util.rs`. | D `The_cargo_template_…` |
| `env` | **was broken → fixed** | GDB's `env` replaced the program's environment (`inf.clear_env()`): no PATH, no HOME. Now `CSCODE_GDB_TEST=yes it is` added, PATH kept, `LANG: null` removed, the marker kept. | D `Env_adds_to_the_programs_environment_…` |
| 1M lines printed, UI responsive | **works** (last lines **fixed**) | `println!` × 1,000,000: GDB reads its pipe on a thread of its own and sends one DAP event per line; the session takes ~17–29 s, 621–1,212 batches reach the service, history 5,000 lines, heap +3.9 MB. GDB delivers the last ~2,500 lines AFTER `terminated`: they were lost ("flood done" missing — shown failing with the fix reverted); the session now ends once the output went quiet (150 ms, at most 2 s). With the Debug Console on screen: **dispatcher p99 12.2 ms, max 24.1 ms** on the second attempt; the first, at load average 16, missed (p99 41 ms, max 176 ms) — the same one-event-per-line shape as js-debug's `console.log` flood, deferred to the performance wave. | D `A_million_line_flood_…`, R |
| 10-session soak | **works** | 10 sessions (start → stop at the loop → Locals → stop): all collected after forced GCs, 11 gdb processes gone, threads 20 → 20. | D `Ten_real_sessions_…` |
| Real application, F5 | **works** | Real composition, `CSCODE_HOME` in a temp dir, headless window, `cscode.debug.gdbArgs: ["-nx"]`: open the cargo folder, F9 in `main.rs`, F5 → `cargo build` → GDB → stopped; Call Stack shows `sample::main` at `main.rs : 97` with the arrow, the editor marks the line; a breakpoint in `util.rs` (not open) opens it at the stop line; Continue → the panic: stop at `util.rs:15`, Exception window "panic: boom at the bottom"; Shift+F5 → no gdb, no program. | A (2) |

### Fixes (each with a test that fails without it)

| Module | Fix | Regression test |
|---|---|---|
| Dap (new) | GDB adapter: `gdb -i=dap` below normal priority with a marker, pretty-printers, std sources, CsCode's summaries, `gdbPath`/`gdbArgs`, templates, `cargo` key, `env` / `pid` | `GdbAdapterFactoryTests` (13), D |
| Dap | `DebugAdapterStdioProcess`: `nice -n 10` + marker for a stdio adapter; orphans reaped after the adapter died, not after a normal quit | `GdbAdapterFactoryTests.A_stdio_adapter_runs_niced_…`, `The_processes_a_crashed_stdio_adapter_orphaned_are_reaped_…` |
| Dap | Debug Console input as expressions, GDB commands with `-exec`/backtick or when no expression; completions through `print` | `GdbQuirksTests` (REPL and completion tests); D, P |
| Dap | Hit counts as GDB ignore counts; inexpressible ones refused with the reason | `GdbQuirksTests.Hit_counts_…` (3) |
| Dap | "Rust panics" client-side exception filter: function breakpoint, `exception` stop, `exceptionInfo` from the panic message (also when it arrives late) | `GdbQuirksTests.Rust_panics_…`, `A_panic_message_that_arrives_after_the_stop_…`, `The_panic_message_is_read_…` |
| Dap | Start Without Debugging on an adapter that ignores `noDebug` (GDB) sends no breakpoints and no exception filters | `GdbQuirksTests.Start_without_debugging_sends_gdb_no_breakpoints_…`; D |
| Dap | Function breakpoints' adapter ids are tracked, so a later `breakpoint` event updates their verdict (all adapters) | `GdbQuirksTests.A_function_breakpoint_verified_later_…` |
| Dap | GDB's own messages shown as debugger messages; logpoint messages end their line | `GdbQuirksTests.Gdbs_own_messages_…`, `Logpoint_messages_end_their_line_…` |
| Dap | Output that arrives after `terminated` still reaches the console (quiet period after `terminated`; a Stop meanwhile ends at once) | `GdbQuirksTests.Output_that_arrives_after_terminated_…`, `Stop_while_waiting_for_trailing_output_…`; D flood (fails with the fix reverted) |
| Dap | std, libc and symbol-less frames de-emphasised; a stop shows the first frame that is not (VS Code's rule; all adapters that de-emphasise frames) | `GdbQuirksTests.Library_frames_…`, `A_frame_without_debug_information_…`, `The_stop_location_is_the_first_frame_that_is_not_library_code`, `Without_the_quirks_…` |
| Panels | Locals and Autos show the `returnValue` scope (Autos: first) | `GdbPanelFixTests.Locals_show_the_value_…`, `Autos_show_the_returned_value_first_…` |
| Panels | Named children are asked for only when the adapter announced some (GDB ignores `filter`: a Vec came out of order) | `GdbPanelFixTests.A_vector_gdb_announces_only_indexed_children_…` |

### GDB quirks the client handles (GDB 18.1's native DAP)

* `evaluate` with context `repl` runs a GDB command (see the REPL row); `completions` completes command lines and answers whole lines with no
  `start` (GDB 17+).
* `hitCondition` is an ignore count, consumed once.
* No Rust exception filter; `rust_panic` must be named `std::panicking::rust_panic`.
* The program's stdout and stderr, and GDB's own console messages, share one pipe, read line by line on a thread of its own: everything is
  `stdout`, a line without its newline is held back until the newline comes, and the last lines can follow `terminated`.
* Logpoint messages have no line break; logpoints evaluate `{expr}` with `str(gdb.Value)` (pretty-printers apply).
* Breakpoints set before `configurationDone` are "pending" (the program is loaded only then) and verified later by events — function
  breakpoints included.
* `variables` ignores `filter`; a variable that has children but no pretty-printer has the value `""`; `setVariable` finds only children it
  listed. Frame columns are always 0 (GDB has no columns).
* A source GDB cannot find on disk gets a `sourceReference` its `source` request cannot serve.
* `launch` with `env` clears the environment first.
* No `restart` request (the session relaunches), `supportsTerminateRequest` (`kill`), `disconnect` makes GDB quit (exit code 0).
* An attach stops the process (reason `attach`).
* `noDebug` is ignored (the program is debugged anyway).

### Known gaps (not fixed)

* **stderr is not told apart** from stdout (one pipe in GDB): no red `eprintln!` lines.
* **Hit counts are "from the Nth hit on"**: GDB keeps stopping after the ignore count is used up; `%N` cannot be expressed.
* A `println!` line (and any line rustc splits into several statements) stops more than once per pass.
* Debug Console input that is BOTH a valid expression and meant as a GDB command runs as the expression (`-exec` forces the command).
* The panic message is read from the program's output: a custom panic hook that prints nothing (or something else) leaves "Rust panics" as the
  description; output older than 8 KB before the stop is not searched.
* Thread names: Linux keeps 15 characters; a thread may be listed under the process name until it sets its own.
* Locals are listed latest-declared first, as GDB gives them; the `Return` scope turns Locals from a flat list into groups right after a step
  out.
* Struct summaries come from GDB's own Rust rendering: nested values deeper than two levels show as `{...}`, and very long ones end with `…`.
* The 1M-line flood can miss the UI budget under heavy machine load (one DAP event per line, as js-debug's `console.log` flood; deferred to the
  performance wave with it).
* GDB 16.3 itself was not run (replaced by 18.1 on this machine): on 16 the Debug Console has no completions.
* CodeLLDB (the VS Code default for Rust) is built in as `lldb` and evaluated beside GDB: see [Rust through CodeLLDB](#rust-through-codelldb).

### Running the suites

```bash
dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~RealGdbRustTests"            # the matrix (Category=RealAdapter)
dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~Gdb"                          # + GdbQuirksTests, GdbAdapterFactoryTests (no GDB needed)
dotnet test tests/CsCode.Panels.Debug.Tests --filter "FullyQualifiedName~Gdb"                 # RealGdbPanelsTests, RealGdbResponsivenessTests, GdbPanelFixTests
dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~RealAppRustDebugTests"
```

Every real test skips with the reason when gdb (14+, with Python), cargo or rustc is missing, and builds the sample offline. Run them with
`DISPLAY` and `SSH_AUTH_SOCK` unset and `CSCODE_HOME` pointing at a temp directory; set `CSCODE_DAP_TEST_METRICS=<file>` to collect the evidence
lines quoted above.

## Rust through CodeLLDB

**CodeLLDB** (Vadim Chugunov, MIT, github.com/vadimcn/codelldb; VS Code's usual Rust debugger) is built in beside GDB as the debug type
**`lldb`** — the type CodeLLDB's own launch.json files use, so they work unchanged. Wave 3 approved the download (user decision 2).

> **Status: evaluated against the real CodeLLDB v1.12.3** (wave 3, builder W7b, 2026-10-29; the user approved running it: "You can run the
> tests"). Both real suites pass: `RealCodeLldbRustTests` 19/19 (D) and `RealCodeLldbPanelsTests` 3/3 (P). Their first run (integration commit
> dd81e5b) failed 4 of 18 D tests and both P tests; the adapter's actual DAP traffic, recorded by a logging wrapper in front of
> `extension/adapter/codelldb` (stdin and stdout teed to files, then the real binary), showed four CsCode defects — fixed, each with a
> regression test — and three test expectations taken from GDB that do not hold for LLDB — corrected, with the evidence below
> ([Fixes](#fixes-codelldb)). Every row of the matrix is now observed; where CodeLLDB's sources explain a behaviour they are cited
> (`src/codelldb/src/…` at the tag).

### Download (pinned) and installer

| | |
|---|---|
| Release | `v1.12.3` (2026-08-23, the latest stable release on 2026-10-29) |
| linux-x64 package | `https://github.com/vadimcn/codelldb/releases/download/v1.12.3/codelldb-linux-x64.vsix` — 55,571,094 bytes, SHA-256 `1cd7f386598022b51a5b93b9ffa23e812b23f519cfe1833384ec4bef4bfd1be1` (GitHub's published digest; checked) |
| Other platforms | linux-arm64, linux-armhf, darwin-x64, darwin-arm64, win32-x64: sizes and digests pinned in `CodeLldbInstaller.Packages` and `tools/fetch-codelldb.sh` (a test checks that both pin the same values) |
| Installed | 170,481,348 bytes: `extension/adapter/codelldb` (the adapter), `extension/lldb/{bin,lib}` (LLDB's `liblldb.so`, `lldb-server`, its own Python 3.12), `extension/lang_support/rust.py` |

* **`tools/fetch-codelldb.sh`** (tests, development): pinned version and per-platform digest, refuses a mismatch before extracting, lists the
  entries first and refuses absolute paths, `..` and links, extracts only `extension/`, never runs anything from the package. `--force`,
  `CODELLDB_ARCHIVE=<vsix>` (no download), `CODELLDB_DEST=<dir>`, `CODELLDB_PLATFORM=<platform>`. Default destination
  `.scratch/tools/codelldb/v1.12.3/` (the real-adapter tests find it there, or through `CSCODE_CODELLDB`).
* **`CodeLldbInstaller`** (the app): the same pins; downloads into `{ToolsDir}/codelldb/` (refused when it grows past twice the pinned size),
  verifies the SHA-256, extracts `extension/` on the thread pool (170 MB never on the debug lane) into a staging directory — names checked
  (`SafeDestination`), symlink entries refused, setuid/setgid never applied, at most 1 GiB declared, all checked before the first byte is
  written — makes the adapter executable and moves the result to `{ToolsDir}/codelldb/v1.12.3/`. **The user is asked first**: "Debugging with
  CodeLLDB needs CodeLLDB v1.12.3 (MIT licence, with its own LLDB: about 56 MB to download, about 170 MB installed) … Download it from
  github.com/vadimcn/codelldb?" (Download / Cancel); concurrent starts ask once and download once; a failed download says why.

### How it is wired

* **Adapter**: built-in type `lldb`, label "Rust / C / C++ (CodeLLDB)", breakpoint languages `rust`, `c`, `cpp`
  (`src/CsCode.Dap/Adapters/CodeLldbAdapterFactory.cs`). CodeLLDB's standalone adapter speaks DAP **on stdio** when started without `--port` or
  `--connect` (its `lib.rs`: `use_stdio`; one session per process, which exits when the session ends), so it is a `DebugAdapterStdioProcess`
  exactly like GDB: `nice -n 10 codelldb --settings {"sourceLanguages":["rust"],"evaluateForHovers":true,"commandCompletions":true}`, below
  normal priority (`lldb-server` and the program inherit it), tracked through the session's `AdapterProcessId`, the `CSCODE_DAP_ADAPTER` marker
  passed on to the program (LLDB's launch inherits the adapter's environment), orphans reaped if the adapter dies. `PYTHONHOME`, `PYTHONPATH`
  and `CODELLDB_STARTUP` are removed from its environment (it embeds its own Python; VS Code's extension removes the same). No TCP port is
  opened (VS Code's extension instead listens and has the adapter `--connect` back with an auth token).
* **Rust support**: `sourceLanguages` (the configuration's, else `["rust"]`) makes CodeLLDB load the Rust toolchain's LLDB formatters
  (`lang_support/rust.py` runs `rustc --print=sysroot` in the adapter's directory — the folder, so a `rust-toolchain` file counts — and imports
  `lib/rustlib/etc/lldb_lookup.py`; Rust 1.98's registers its formatters on import) and offer its **"Rust: on panic"** filter (on by default).
* **Lookup**: `cscode.debug.codelldbPath` → `CSCODE_CODELLDB` → `{ToolsDir}/codelldb/v1.12.3` → the copy installed with the app (`{InstallDir}/debug-adapters`) → `codelldb` on
  PATH; a path may name the adapter,
  CodeLLDB's `extension/` directory or an install directory. Otherwise the download is offered; without a window the start fails saying what to
  do (and that GDB needs no download).
* **Provider** (what VS Code's CodeLLDB extension does before the adapter sees a configuration; never overriding the user's values): the **`cargo`
  key** (object or argument list) resolved through `CargoArtifacts` as for GDB; `cwd` and `relativePathBase` = the folder; `sourceLanguages`;
  **`_adapterSettings.consoleMode: "evaluate"`** (VS Code's `lldb.consoleMode` setting is honoured when set); **`terminal: "console"`** (the
  program's output in the Debug Console, as with GDB) unless `console: "integratedTerminal"` was asked for and a terminal is composed;
  `sourceMap` `/rustc/<commit>` → `<sysroot>/lib/rustlib/src/rust` when rust-src is installed (std frames get their sources, as GDB's
  substitute-path does); `env` values made strings (a `null`, VS Code's "remove", would fail CodeLLDB's launch); attach `pid`/`processId` text →
  number.
* **Templates** (Create launch.json for `lldb`): **Rust: Debug <bin> (CodeLLDB, cargo build)**, **Rust: Debug unit tests of <package> (CodeLLDB,
  cargo test)** (the `cargo` key), **Rust: Attach to <bin> (CodeLLDB)**, and the same `cargo: build` process task as GDB's; plain LLDB templates
  without a Cargo.toml.
* **Quirks** (`CodeLldbAdapterQuirks`; the first five from CodeLLDB's sources and confirmed by the real runs, the last four found by them):
  * Its "rust_panic" filter is an LLDB breakpoint on `rust_panic$`, so the stop comes as reason **`breakpoint`** (and its `exceptionInfo` would
    answer `Breakpoint`). The session now recognises such a filter's breakpoint ids from the `setExceptionBreakpoints` response (new
    `DebugAdapterQuirks.BreakpointExceptionFilters`) and reports the stop as the exception **"Rust panics"** with the panic's message from the
    output — the same presentation as GDB's client-side filter.
  * **Hit counts**: CodeLLDB reads a bare `N` as `>= N` (`src/codelldb/src/expressions/hit_condition.rs`: the last parser alternative is
    `GE(n)`; its own test parses `" 13 "` as `GE(13)`); CsCode sends `=N` (the Nth hit only, as js-debug and Visual Studio's "is equal to");
    `<`, `<=`, `=`, `==`, `>=`, `>`, `%` are CodeLLDB's own and pass unchanged — so `%2` works here (observed: stops on hits 2 and 4), unlike
    with GDB.
  * **Debug Console**: evaluate mode; a backtick or `/cmd ` runs an LLDB command, and `-exec <command>` (VS Code's C++ habit, which the GDB adapter
    takes) is sent as `/cmd <command>`. A command's evaluate result is EMPTY: CodeLLDB prints its output as `console` output events before the
    response (observed for `bt` and `frame variable x`), which the Debug Console shows under the input line.
  * **Frames**: std frames and CodeLLDB's disassembly frames (code without sources: a `sourceReference`) are de-emphasised, so a pause in libc or a
    panic shows the user's code (the GDB rule).
  * **Start Without Debugging**: CodeLLDB honours `noDebug` but then refuses breakpoint requests with an error; the session sends none (new
    `DebugAdapterQuirks.RefusesBreakpointsWithoutDebugging`), as it already did for GDB.
  * **Paging** (found by the real run): `variables` ignores `start`, `count` and `filter` (its `handle_variables` reads only the reference) and
    never announces `indexedVariables`: asked for `[240, 250)` of a 250-element Vec it answered all 250 elements and `[raw]`. The session kept the
    first `count` of an answer (its guard against adapters that ignore `count`), so every page was the first one again and the Locals window
    stopped at `[99]`. New `DebugAdapterQuirks.IgnoresVariablePaging`: the session asks once per stop for all the children (at most
    `MaxVariablesPerRequest`, 10,000) and cuts the pages itself.
  * **Pause** (found): CodeLLDB pauses by stopping the process (LLDB sends SIGSTOP) and its stop handler maps every signal to reason
    `exception` with no case for a pause it asked for: Break All arrived as `{"reason":"exception","description":"signal SIGSTOP"}` (and "Stop
    reason: signal SIGSTOP" on stderr), so the Exception window would open on every pause. New `DebugAdapterQuirks.IsPauseStop`: while the
    client's own `pause` is outstanding, that stop (only `signal SIGSTOP`; a real signal stays an exception) is reported as reason `pause`.
  * **Frame ids** (found): every `stackTrace` answer numbers the frames anew (the stop location's request got 1001…, the Call Stack window's
    request for the same stack 1021…; the older ids stay valid until the program resumes). The Call Stack window marks the frame whose id is the
    stop location's, so it marked none (both panels tests failed on it), and Locals' frame identity found no frame. New
    `DebugAdapterQuirks.RenumbersFramesPerRequest`: the session reads each thread's frames once per stop, from the top, and serves every later
    request from them, asking only for frames beyond those read (bounded by the frame registry's 10,000).
  * **Completions** (found): in evaluate mode CodeLLDB completes only LLDB commands (`handle_completions`: expression completion is a TODO) and
    counts an answer's `start` inside the command with its prefix stripped (`` `fram `` → `frame` at start 0, length 4, which replaced
    `` `fra `` with `frame`). Now an expression is completed as LLDB's `frame variable <expression>` (locals and their members), `-exec` as
    `/cmd`, only the text before the caret is sent, and each answer is placed back at the caret by its length (`MapCompletions`).

### The matrix — CodeLLDB beside GDB

Every CodeLLDB row below was observed with the real CodeLLDB v1.12.3 (its bundled LLDB), Rust 1.98.1 from Nix and the same cargo sample as GDB's,
on this machine (Debian 14, load average 5–17 from other builds); line numbers are 1-based. GDB's column is the evaluated one
[above](#the-matrix). Tests: **D** = `tests/CsCode.Dap.Tests/RealCodeLldbRustTests.cs` (real `DebugService`), **P** =
`tests/CsCode.Panels.Debug.Tests/RealCodeLldbPanelsTests.cs` (the panels over the real DAP module); unit tests with fakes shaped like the observed
traffic: `CodeLldbQuirksTests`, `CodeLldbAdapterFactoryTests`, `CodeLldbInstallerTests`, `CodeLldbStandInTests`. The evidence quoted is what
the tests record (`[codelldb]` lines with `CSCODE_DAP_TEST_METRICS`) and, for the fixes, the adapter's own DAP traffic.

| Row | GDB 18.1 | CodeLLDB v1.12.3 | Evidence (CodeLLDB, observed) | Test |
|---|---|---|---|---|
| Launch + clean exit, nothing left running | works | **works** | `nice -n 10 codelldb --settings {"sourceLanguages":["rust"],"evaluateForHovers":true,"commandCompletions":true}`: codelldb and the program at nice 10; tree codelldb → `lldb-server gdbserver --native-regs --setsid` → `sample`, the program carrying the marker; "done hello world 3 2 Some(5) None Ok(12)", exit code 0; codelldb, lldb-server and the program gone. Start to the first stop **636–654 ms** (GDB: 273 ms in the same run, load 6.5): liblldb and the Python formatters load first. | D `Launch_stops_…_below_normal_priority` |
| Line breakpoints | works | **works** | CodeLLDB creates the target at `launch`: `setBreakpoints` answers `verified: true, "Resolved locations: 0"`, then `breakpoint` events `changed, line 97, "Resolved locations: 1"` once the program is loaded, before the first stop. | D, P |
| Moved line (comment line) | works | **works** | Requested 74 → stopped at 75; the verdict's line is 75. | D `A_breakpoint_on_a_line_without_code_…` |
| Conditional | works | **works** | `i == 3`: every stop has `i = 3`. | D `Conditional_hit_count_and_log_breakpoints` |
| Hit count | partial (ignore count, `%N` impossible) | **works** | A bare `2` (sent as `=2`) stops once, `i = 1`; `%2` stops on hits 2 and 4 (`i = 1, 3`) and nowhere else. | D (same), `Hit_conditions_take_codelldbs_own_operators`, `CodeLldbQuirksTests` |
| Logpoint | fixed | **works** | `logged x=51`, category `console`, its own line, no stop. | D (same) |
| Breakpoint on a `println!` line | adapter (2 stops per pass) | **adapter** (3 stops per pass) | LLDB resolves the line to 3 locations ("Resolved locations: 3", one per lexical block of the macro's expansion; instruction pointers `…5A63`, `…5A73`, `…5A9F`, all column 9) and stops at each: 3 stops, all on the `i == 3` pass. This was the first run's "hit-count breakpoint stopped 3 times": the `=2` hit count was right; the test expected GDB's 2 locations (test fixed: it now expects as many stops as CodeLLDB resolved locations). | D (same) |
| Function breakpoints | fixed | **works** | `sample::util::add` → `util.rs:4`, "Resolved locations: 1". | D `Function_breakpoints_…` |
| Rust panics | added (client-side filter) | **works** | Its own "Rust: on panic" (`rust_panic`, default on); the stop (reason `breakpoint` on the wire) is reported as `exception`, "Paused on exception: Rust panics", text "boom at the bottom", at the user's frame `util.rs:15` (`__rustc::rust_panic` and five std frames de-emphasised); `exceptionInfo` answered by the session; the uncaught one ends with 101. P: Exception window "Exception Thrown / panic: boom at the bottom", arrow on `util.rs : 15`. | D `Rust_panics_…`, P `A_rust_panic_shows_…` |
| Start Without Debugging | fixed | **works** | `noDebug` honoured: 0 stops, exit code 0 (no breakpoint requests sent: CodeLLDB would refuse them); panics off → 101, no stop. | D `Start_without_debugging_…` |
| Step over / into / out | works | **works** | Into → `util.rs:4` (`sample::util::add`), over → `util.rs:6` (as with GDB: `sum` has no line of its own), out → `main.rs:100`; reasons `step`. | D `Step_into_over_and_out_…` |
| Return value after step out | fixed (Return scope) | **works** (Locals) | `[return value]=52` is the first row of "Local" (`stop_return_value()`); Autos lifts it (shows it first, whatever the line mentions), as GDB's `returnValue` scope (end pass T52, `GdbPanelFixTests.Autos_lift_a_return_value_listed_among_the_locals`). | D (same) |
| Scopes | `Locals`/`Arguments` (hinted) | **works** | "Local", "Static" (vtables), "Global" (empty), "Registers": none hinted, none expensive; the Locals window takes "Local" by name (`ScopeSelector`). | D, P |
| Pause | fixed (stop location) | **was broken → fixed** | On the wire: `{"reason":"exception","description":"signal SIGSTOP"}` after our `pause` (the first run's "expected pause, got exception"). Now reason `pause` at `sample::serve` `main.rs:46` (libc's `__internal_syscall_cancel` comes as disassembly, de-emphasised); P: arrow on `serve`, no Exception window. | D `Pause_stop_while_running_and_restart`, P `Break_all_shows_…`, `CodeLldbQuirksTests` |
| Restart | client relaunch (270–660 ms, new GDB) | **works** (faster) | `supportsRestartRequest`: running again after **37–45 ms** in the same codelldb. | D (same) |
| Stop | works | **works** | While running: every session gone after ~107 ms; codelldb exits. | D (same) |
| Adapter crash | works | **works** | SIGKILL to codelldb: "The debug adapter closed the connection."; the program did not survive it. | D `An_adapter_crash_…` |
| Call stack: generics, closures, std | works / fixed | **works** | `sample::apply::<sample::main::{closure#4}>` → into → `sample::main::{closure#4}`; `sample::util::describe::<i32>`; std frames de-emphasised and folded in P (`[External Code] \| explode : 15 ⇐ \| explode : 17 \| explode : 17 \| {closure#5} : 105 \| [External Code] \| main : 105 \| [External Code]`). | D `Generic_functions_…`, P |
| Call stack: the current frame | works | **was broken → fixed** | CodeLLDB numbers frames anew per `stackTrace` (1001… then 1021… for the same stack): the Call Stack window marked no current frame (both P tests failed on it). Now `sample::main@main.rs : 100 ⇐`. | P, `CodeLldbQuirksTests` |
| Locals: Rust values | fixed (CsCode's summaries) | **works** (the toolchain's formatters) | "Loading Rust formatters from <sysroot>/lib/rustlib/etc" (`lldb_lookup.py`): `s="hello"`, `text="world"`, `v=size=3` → `[0]=1 [1]=2 [2]=3`, `map=size=2` → `[0]=("b", 2) [1]=("a", 1)`, `some=Some(5)` → `0=5`, `none=None`, `ok=Ok(12)`, `err=Err("invalid digit found in string")`, `shape=Circle{radius:1.5}`, `rect=Rect(3, 4)`, `empty=Empty`, `p={x:3, y:4}`, `boxed={x:5, y:6}` (the Box is transparent), `shared=strong=2, weak=0` → `value="shared"`, `pair=(1, "two")`, `wrapper={point:{x:7, y:8}, tag:Some("w")}`; every formatted value has a `[raw]` child. Primitive types carry LLDB's C names (`i32` is `int`, `u32` `unsigned int`, `f64` `double`): LLDB has no Rust type system ("This version of LLDB has no plugin for the language "rust""). The first run's "raw `[0]`, `[1]` typed unsigned int" was this, not missing formatters. | D `Locals_show_rust_values_…`, P |
| Locals: a 250-element Vec, paging | fixed | **was broken → fixed** | CodeLLDB ignores `start`/`count`/`filter`: `[240, 250)` came back as all 250 and `[raw]`, and the session kept the first 10 (`[0]=0 … [9]=18`: the first run's failure); the Locals pager showed `[0]…[99]` and stopped. Now `[240]=480 … [249]=498`; P pages to `[249]=498` and `[raw]`. | D (same), P, `CodeLldbQuirksTests` |
| Watch | works | **works** | `x * 2` = 102 (type `long long`), `p.x + p.y` = 7; `nope` → "Expression evaluation in Rust not supported. Falling back to default language. Ran expression as 'Objective C++'. … use of undeclared identifier 'nope'". The same Rust expressions on both: CodeLLDB `v[1]` = 2, `big[249]` = 498, `*boxed`, `boxed.x`, `wrapper.point.x`, `pair.1`, `some`, `text` work; `v.len()`, `s.len()`, `map.len()`, `x as u8` → "Syntax error". GDB: `v[1]`, `big[249]` → "Cannot subscript non-array type", method calls → "Could not find function", `x as u8` = 51. | D `Watch_set_variable_…`, P |
| Set variable | works | **works** | `x = 100` (`setVariable` answers `100`); the program prints `err 101`. | D (same) |
| Debug Console REPL | fixed | **works** | Evaluate mode (`_adapterSettings.consoleMode: "evaluate"`; "Console is in 'evaluation' mode…"): `x * 2` → 102. `-exec bt`, `` `bt ``, `/cmd bt`: the result is empty and the backtrace arrives as console output (`* thread #1 … frame #0: … sample`sample::main at main.rs:100:23`): the first run's "console output empty where main was expected" read the result only (test fixed; P shows the lines). `` `frame variable x `` → `(int) x = 51`. | D (same), P |
| Debug Console completions | fixed | **was broken → fixed** | CodeLLDB answered `[]` for `p.` (no expression completion) and `` `fram `` → `frame` at start 0, length 4 (inside the stripped command). Now `p.` → `p.x`, `p.y`; `x + p.` → `x + p.x`; `sh` → `shape`, `shared`, `shared2`; `wrapper.po` → `wrapper.point` (P: accepted into the input); `` `fram ``, `/cmd fram`, `-exec fram` → `frame` after the prefix. | D (same), P, `CodeLldbQuirksTests` |
| stdout / stderr | partial (one pipe) | **partial** (adapter) | LLDB runs the program on a pseudo-terminal: `eprintln!("err 52")` arrives as `stdout` (stderr category empty); lines end `\r\n` (the console strips the `\r`). | D (same) |
| Threads | works | **works** | `sample`, `worker-1`, `worker-2`; worker frames `sample::worker`, `sample::main::{closure#1}::{closure#0}`, std de-emphasised. | D `Threads_of_std_thread_…` |
| Attach by pid | works | **works** | `pid` as text → number; CodeLLDB does not stop on attach: the first stop is the `@tick` breakpoint (reason `breakpoint`, `main.rs:45`); Stop detaches, the program keeps running, codelldb exits. | D `Attach_to_a_running_rust_program_…` |
| Unit-test binary (`cargo` key) | works | **works** | Resolved by CsCode as for GDB; stop in `util.rs`'s test; "test result: ok. 1 passed". | D `The_cargo_key_…` |
| Templates | works | **works** | Create launch.json for `lldb`: "Rust: Debug sample (CodeLLDB, cargo build)", the two others and `cargo: build`; the binary one builds and stops in `main.rs`. | D `The_codelldb_template_…`, `CodeLldbAdapterFactoryTests` |
| `env` | fixed (GDB replaced it) | works (unit) | Added on top of the inherited environment (LLDB's `target.inherit-env`); string values, `null` dropped. Not in the real suite. | `CodeLldbAdapterFactoryTests` |
| 1M lines | works (11–29 s) | **partial** (adapter) | **2.3–3.1 s**, 107–131 batches, heap +6–8 MB, the console's memory bounded (drop-oldest). CodeLLDB does **not drain** the program's buffered output when it reports the exit (`notify_process_terminated`: console message, `exited`, `terminated`): in 4 of 11 runs recorded on the wire its stream ended early (`flood line 999942`, `…985`, `…987`, `…994`) without "flood done", and the console's last line was the adapter's last line every time; without the recorder 11 of 15 runs ended early (up to 416 lines lost). Once, under load, the session never ended: see Known gaps. | D `A_million_line_flood_…` |
| 10-session soak | works | **works** | 10 sessions collected, 10 codelldb processes gone. | D `Ten_real_sessions_…` |
| Debuggee pid | from GDB's `process` event | **none** | No DAP `process` event ("Launched process <pid>" on the console only): `DebuggeeProcessId` stays empty (informational in CsCode). | D |

### Fixes (CodeLLDB)

The first real run (integration commit dd81e5b) failed 4 D tests and both P tests. Each cause was read from the adapter's DAP traffic:

| First-run failure | Cause | Kind | Change | Regression test |
|---|---|---|---|---|
| `Conditional_hit_count_and_log_breakpoints`: 3 stops at the `println!` line, expected 1–2 | LLDB resolves the line to 3 locations; the `=2` hit count was right (one stop, `i = 1`), and CodeLLDB does read a bare `N` as `>= N` | test expectation (GDB's 2 locations) | The test expects as many stops as CodeLLDB's verdict reports locations ("Resolved locations: 3"), all on the `i == 3` pass; a new test runs `%2` | D (2 tests) |
| `Locals_show_rust_values_…`: `[0]=0, [1]=2 …` typed `unsigned int`, `498` missing | The formatters were loaded; CodeLLDB ignores paging and the session kept the first `count` children of its answer | **CsCode bug** | `DebugAdapterQuirks.IgnoresVariablePaging`: one request per stop for all the children, pages cut by the session (`VariableHandleCache.SetAllChildren`) | `CodeLldbQuirksTests.Codelldb_ignores_paging_…`, `A_page_of_…`; D now also asserts the formatted values; P pages a 250-element Vec |
| `Pause_stop_while_running_and_restart`: reason `exception`, expected `pause` | CodeLLDB reports the stop of a pause as the signal SIGSTOP (reason `exception`) | **CsCode compensates** (adapter behaviour) | `DebugAdapterQuirks.IsPauseStop` and the session's outstanding-pause flag | `CodeLldbQuirksTests.A_pause_codelldb_reports_…`, `Only_the_sigstop_…`; P `Break_all_shows_…` |
| `Watch_set_variable_…`: console result empty, expected `main` | A command's evaluate result is empty; its output comes as console output events | test expectation | The test reads the console output (`frame #0`, `sample::main at main.rs`), as the Debug Console shows it | D; P asserts the console lines |
| Both P tests: the Call Stack marked no current frame | CodeLLDB renumbers frames per `stackTrace` | **CsCode bug** (assumed stable ids) | `DebugAdapterQuirks.RenumbersFramesPerRequest`: frames read once per stop and thread | `CodeLldbQuirksTests.Codelldb_renumbers_frames_…`, `The_service_stop_location_names_…` |
| (found on the way) Completions: none for expressions; command completions garbled the input | CodeLLDB's positions are relative to the stripped command; no expression completion | **CsCode compensates** | `CodeLldbAdapterQuirks.MapCompletions` (`frame variable`, positions put back at the caret) | `CodeLldbQuirksTests.Completions_go_to_codelldb_…`, `A_completion_reaches_codelldb_…`; D, P |
| (found on the way) 1M-line flood: "flood done" missing in some runs | CodeLLDB does not drain the program's output at exit | test expectation (adapter limitation) | The test checks what CsCode owns: the adapter's stream in order up to its "Process exited" message, ending on a whole line | D |

The three new session hooks are additive (`DebugAdapterQuirks`, off by default): GDB, js-debug and netcoredbg sessions are unchanged.

### Known gaps (observed)

* **The program's last output can be lost at exit** (adapter): CodeLLDB reports the exit without draining LLDB's buffered output; seen with a
  1M-line flood (in most runs; up to 416 lines). Programs that print a few lines and exit showed all of them in every run.
* **A rare hang at the end of a flood**: once in about 15 flood runs without the recorder, under load, the session stayed Running after the
  program had ended (codelldb alive with no child process, no `exited`/`terminated` received, the last output `flood line 999016`); the test's
  10-minute limit ended it. Not reproduced in 23 later runs (8 recorded on the wire, 15 with a monitor ready to dump codelldb's threads), so
  the cause is unknown; Stop (Shift+F5) ends such a session.
* stderr is not told apart from stdout (LLDB's pseudo-terminal), as with GDB.
* Expressions are LLDB's: CodeLLDB's "simple" expressions, then LLDB's C++ evaluator. Indexing a Vec works (`v[1]`); method calls
  (`v.len()`) and casts (`x as u8`) do not; primitive types show C names (`int`, `unsigned int`, `long long`).
* A `println!` line has 3 locations (3 stops per pass).
* No `process` event: `DebuggeeProcessId` stays empty.
* Its scopes carry no presentation hints (Locals relies on the name "Local"); Autos lifts the `[return value]` row by its name (end pass T52).
* The first stop comes ~0.4 s later than GDB's (liblldb and the Python formatters load first).
* The download: 56 MB (170 MB installed), offered on first use.

### Running the suites

```bash
tools/fetch-codelldb.sh            # or CODELLDB_ARCHIVE=<…/codelldb-linux-x64.vsix> tools/fetch-codelldb.sh: no download
CSCODE_CODELLDB=<install dir> CSCODE_DAP_TEST_METRICS=<file> dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~RealCodeLldbRustTests"
CSCODE_CODELLDB=<install dir> CSCODE_DAP_TEST_METRICS=<file> dotnet test tests/CsCode.Panels.Debug.Tests --filter "FullyQualifiedName~RealCodeLldbPanelsTests"
dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~CodeLldb"     # + the unit tests (no CodeLLDB needed)
CSCODE_CODELLDB_ARCHIVE=<vsix> dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~The_real_pinned_package_installs"   # nothing executed
```

`CSCODE_CODELLDB` may name the adapter, CodeLLDB's `extension/` directory or the install directory (here
`.scratch/downloads/codelldb/v1.12.3`). Run with `DISPLAY` and `SSH_AUTH_SOCK` unset and `CSCODE_HOME` in a temp directory. To see the wire,
point `CSCODE_CODELLDB` at a script that tees its stdin and stdout to files and runs the real adapter (`tee in | codelldb "$@" | tee out`);
the adapter-crash test then kills the script rather than codelldb, so run that one without the recorder.

## Which Rust debugger is the default

**GDB stays the default** (`cscode.debug.rustDebugger`: `"gdb"`, the default, or `"lldb"` for CodeLLDB; `"codelldb"` is accepted too). The
setting decides which debugger a Rust folder's launch.json is created for when F5 finds no launch configuration (a `Cargo.toml` at the folder's
root); an existing launch.json names its own type, and both debuggers stay available. Revisited in wave 3 (W7b) from both evaluated matrices:
CodeLLDB is better in some rows and worse in others, not clearly better, so the default the user accepted stands.

| | GDB 18.1 (default) | CodeLLDB v1.12.3 |
|---|---|---|
| Install | none (the system's GDB) | 56 MB download, 170 MB installed, offered on first use |
| Start to the first stop | **273 ms** | 636–654 ms |
| Hit counts | "from the Nth hit on", no `%N` | **exact and every operator** (`=N`, `%N`, …) |
| Restart | client relaunch, 270–660 ms | **in the adapter, 37–45 ms** |
| Values | CsCode's summaries over GDB's Rust printers (`Point {x: 3, y: 4}`, `Vec(size=3)`) | **the toolchain's formatters** (`{x:3, y:4}`, `size=3`, `[raw]` children); C type names (`int`, `unsigned int`) |
| Watch expressions | Rust syntax: casts (`x as u8`) work; no Vec indexing, no method calls | Vec indexing (`v[1]`, `big[249]`) works; no casts, no method calls |
| `println!` line | 2 stops per pass | 3 stops per pass |
| 1M-line flood | 11–29 s, every line delivered (after the trailing-output fix) | **2.3–3.1 s**, but the program's last lines are often lost at exit, and one run never ended |
| stderr | merged into stdout | merged into stdout |
| Debug Console | expressions, `-exec` commands, completions through `print` | expressions, `-exec`/backtick/`/cmd` commands (output as console lines), completions through `frame variable` |
| What CsCode compensates | `GdbAdapterQuirks`: REPL, completions, hit counts, panics, noDebug, output, frames, trailing output | `CodeLldbAdapterQuirks`: panics, hit counts, `-exec`, frames, noDebug; paging, pause, frame ids, completions (found by the real run) |

* For GDB: no download, faster start, Rust expression syntax, and the program's whole output (including its last lines) — the last point
  matters most for a debugger whose console shows the program's output by default.
* For CodeLLDB: exact hit counts, a fast in-adapter restart, the toolchain's own formatters, Vec indexing in Watch and a much faster console
  flood.
* A user who prefers CodeLLDB sets `"cscode.debug.rustDebugger": "lldb"`; CodeLLDB's own launch.json files (type `lldb`) work unchanged.

Switching the default is the one line `RustDebugger()` in `src/CsCode.Dap/Service/DebugService.Commands.cs` (its fallback `GdbAdapterFactory.PrimaryType`)
plus this section; the download offer would then appear on a Rust user's first F5. What would justify it: CodeLLDB draining the program's
output at exit (an upstream fix) and the end-of-flood hang never reappearing.

F5 without a launch configuration also used to make a **.NET** launch.json for every folder (the first debugger in label order): it now picks the
folder's debugger — `Cargo.toml` → the Rust debugger, a .NET project or solution → `coreclr`, `tsconfig.json`/`package.json` → `pwa-node` — and
otherwise asks ("Select debugger") (`DefaultDebuggerTests`).
