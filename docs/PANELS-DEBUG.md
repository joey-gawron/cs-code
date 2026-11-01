# Debug panels (`CsCode.Panels.Debug`)

The Visual-Studio-style debugging windows: **Locals, Autos, Watch, Call Stack, Breakpoints, Threads, Debug Console**, plus the
**Exception Helper** and **Modules** windows. They are `CsCode.Ui.IToolWindow`s over Core interfaces only; the project references
`CsCode.Core` and `CsCode.Ui` (never `CsCode.Dap`) and only the test project references the real DAP module.

```csharp
services.AddCsCodeDebugPanels();   // registers the tool windows + DebugPanelsRuntime
```

## Windows

| Id | Title | Icon | Toggle command (bound by the shell) | Auto-show |
|---|---|---|---|---|
| `debug.callstack` | Call Stack | `debug-stackframe` | `workbench.debug.action.toggleCallStack` | `inDebugMode` |
| `debug.locals` | Locals | `symbol-variable` | `workbench.debug.action.toggleLocals` | `inDebugMode` |
| `debug.autos` | Autos | `symbol-field` | `workbench.debug.action.toggleAutos` | `inDebugMode` |
| `debug.watch` | Watch 1 | `watch` | `workbench.debug.action.toggleWatch` | `inDebugMode` |
| `debug.breakpoints` | Breakpoints | `record` | `workbench.debug.action.toggleBreakpoints` | `inDebugMode` |
| `debug.threads` | Threads | `layers` | `workbench.debug.action.toggleThreads` | `inDebugMode` |
| `debug.console` | Debug Console | `debug-console` | `workbench.debug.action.toggleDebugConsole` | `inDebugMode` |
| `debug.exception` | Exception | `error` | `workbench.debug.action.toggleException` | `cscode.debug.exceptionStopped` |
| `debug.modules` | Modules | `package` | `workbench.debug.action.toggleModules` | manual |

All dock at the bottom. The shell creates a window's view lazily and disposes it when the window is hidden; a view is a thin
`UserControl` whose `DataContext` is a fully disposable view-model, so creating one is cheap and hiding one releases every subscription.

**Commands.** The panels register one command: `debug.addToWatch` (argument: the expression). With no argument it starts the Watch window's
empty row when that window is **on screen** (its view in the visual tree and visible: the shell creates the views of every auto-shown debug
window but only one tab of a group is shown); otherwise it brings the window forward through the optional Core `IViewActivator` and starts the
row there, else asks with an input box. An expression that came with the command is added and the Watch window is brought forward, as Visual Studio does on Add Watch. Modules is manual: it is not in the shell's default layout. The runtime contributes its **Ctrl+Alt+W** binding (`when: inDebugMode`) through
`IContributionRegistry` (built-in rank: the user's `keybindings.json` can remove it). The `workbench.action.debug.*` commands belong to the DAP module
and the `workbench.debug.action.toggle*` ids are only *declared* here: **the shell must bind each `IToolWindow.ToggleCommandId` to
`ToolWindowManager.Toggle(id)`** (nothing in the panels can toggle a dock). **Context key** published for auto-show:
`cscode.debug.exceptionStopped`.

**Consumed services** (all from DI, only `IDebugService` is required, the rest degrade gracefully): `IDebugService`, `IDebugFrameFocus`,
`IDebugOutputHistory`, `INavigationService`, `IWindowService`, `ICommandService`, `IContextKeyService`, `IStateStorage`,
`IDocumentManager` (Autos' line text), `IWorkspaceService` (folder switches reload the watch list), `IContributionRegistry` (the Ctrl+Alt+W
binding), `IViewActivator` (Add Watch brings the Watch window forward), `IUiDispatcher` (falls back to the Avalonia dispatcher). Per session,
the optional `IDebugSessionModules` feeds the Modules window. Colours are `{DynamicResource vsc:…}`
brushes only (see `Views/DebugPanelStyles.axaml`); icons come from the shell catalog.

## How the pieces fit

```
IDebugService / IDebugSession events (any thread)
   └─ DebugFocusTracker ── coalesces a step's burst of events into ONE callback on the UI thread, tags it with a stop epoch
        └─ view-models (Locals, Autos, Watch, CallStack, Threads, Exception, Modules)
              └─ every service call: await ui.OnUi(valueTask, ct)    // completes off-thread, hops back through IUiDispatcher
```

* `Infrastructure/DebugFocusTracker` — "which session/stop/frame is focused". A stop is only reported with a frame once the debug service
  published *that stop's* location (`StopLocationChanged`), with a 400 ms grace for the case where an identical location suppressed the event.
  An adapter `invalidated` event starts a new epoch (panels reload) and makes the session forget its frame ids while the service may still
  publish the old location: the tracker then accepts the location only after **one** `stackTrace` request (`Infrastructure/FrameRevalidator`,
  shared by every panel) re-registered the ids; when the adapter renumbered its frames, the frame at the same position is focused (and
  `IDebugFrameFocus` is asked to follow), never a dead id. That live frame is kept for its epoch only (dropped at the next stop, resume,
  invalidation or session change), so the runtime's app-lifetime tracker does not keep an ended session reachable. An invalidation of
  `variables` only (the session's `IDebugSessionFrames.FrameGeneration` did not change since its stop or previous invalidation) keeps the
  frame ids: a location that was current stays current for the new epoch (and so does a live frame found for a renumbered one), so the panels
  reload at once instead of showing "Loading…" for a `stackTrace` round-trip.
* `Infrastructure/UiAsync.OnUi` — never captures a `SynchronizationContext` (so view-models behave the same under a test dispatcher) and a
  cancelled token unblocks the await **even if the adapter ignores it**: a hung adapter cannot keep a closed panel alive.
* `Variables/VariablesTreeViewModel` — the engine behind Locals/Autos/Watch (and reusable for editor hover pins).

### The variables tree

* **Virtualised by flattening**: visible nodes live in one `RangeObservableCollection` (precise Add/Remove notifications, so selection, focus and
  scroll survive expand/collapse and paging; the shell's `BulkObservableCollection` is used for the lists that are replaced wholesale).
  A million-element array shows one page (100 rows) + a pager row.
* **Lazy + paged**: `GetVariablesAsync(ref, start, 100)`; the `… N more` row loads the next page. A value with `IndexedVariables` from a session
  that implements the Core `IDebugSessionVariablePaging` (the DAP session does) pages through `filter: "indexed"` and lists its named children
  (`length`, `[[Prototype]]`) once, first — DAP only promises `start`/`count` on a filtered set, and js-debug ignores `start` without one.
  A **lazy** value (`DebugVariable.IsLazy`, DAP `presentationHint.lazy`: js-debug's getters) shows `(…)` and no type (js-debug types the
  placeholder `function`) and runs only when activated (Enter, double-click, →), then shows its value and type in place. An answer other than
  exactly one child (js-debug answers a getter that *throws* with the getter function's own properties) shows "The debugger did not compute a
  value: the getter may have thrown an exception." Change highlighting compares a lazy row by its placeholder, so an evaluated getter is not
  marked changed at the next stop. A short page ends the list whatever the adapter
  announced; at most 10,000 **paged** children per node are ever held (then a notice row "Showing the first 10,000 of N items"); the named
  children listed with them (`length`, `[[Prototype]]`) do not count against it. An adapter that ignores `start/count` is recognised and cannot
  duplicate rows; a page of up to 10,000 nodes is created in ≤ 4 ms slices (`UiDispatcher.PostBatched`) and inserted in 100-row chunks.
  A chunk's position is resolved when it goes in (the end of the parent's block *then*), not before the yield: Watch re-expands every root
  at once after a stop, and the user can collapse in between. A per-node `RowsVersion` (bumped by collapse, dropped handles and newer inserts)
  voids an insert nobody wants any more.
* **Handle lifetime**: on `Continued`/step/session end the generation is bumped, in-flight loads are cancelled, **every node is dropped**
  (nothing holds a stale `variablesReference`), but expansion *paths* and the shown values are remembered. After the next stop the tree is
  re-expanded lazily (one request at a time) and values that differ from the previous stop are red. Baselines are kept **per frame**,
  recognised by its **function**: session, thread, frame name and source, read with one `stackTrace` request per stop location that Locals and
  Autos share (`Infrastructure/FrameIdentityLookup`; keyed by the stop location only when that location is the frame's own, so a
  value-equal location — a loop — asks nothing, and a caller frame never inherits an earlier stop's caller with the same id). So a step within one function is
  compared with the previous stop whatever variables appeared or disappeared (Node.js 24 hides a `const` until it is initialised, js-debug adds
  `Return value` after a step, a `Block` scope comes and goes), a step into another function is not compared (even with a frame id the
  adapter reuses for depth, or shared variable names), and picking another frame in the Call Stack does not turn the current stop's values
  into the baseline. A variable that **appeared** since the previous stop of the same frame is red too (Visual Studio shows new locals red):
  one missing from a list that was shown completely then (not paged, capped or collapsed), or inside a scope/variable that appeared itself;
  frame-level pseudo-variables (names with whitespace, `Return value`) are not marked for appearing. A scope's variables have the same paths
  whether the scope is a group (several scopes) or the roots (one scope), so a block scope coming or going changes nothing for the others.
  When the frame's identity is unknown (the stack cannot be read, or the frame is deeper than 100) the frame's first root names key the
  baseline, as before (a changed name list then means no comparison). Autos keys its baseline by the frame's **full** variable list (and
  remembers all of its values), not by the line-filtered subset it shows, so a step is always compared with the previous stop.
* **Editing**: `setVariable` (only when the adapter advertises it and the parent handle is live); a failure stays in the inline editor with the
  adapter's message. Display: strings quoted (unless the adapter already quoted them, `"…"` or js-debug's `'…'`), ≤ 200 chars + `…` (full text in tooltip and Copy Value), newlines shown as `⏎`, integers
  hex/decimal client-side (per tree and per row). Copy Value / Copy Expression (`evaluateName`) / Add Watch.
* **Keyboard**: ↑↓ move, → expand (or into the children), ← collapse (or to the parent), Enter edits (or loads more / opens the empty watch row),
  F2 rename/edit, Delete remove a watch, Ctrl+C copy the value, Alt+↑/↓ reorder watches; Enter/Esc end an inline edit and keep the keyboard in the list.

### Locals, Autos, Watch

* **Locals**: the variables of the focused frame's scopes hinted `locals`/`arguments` (or named like them), else the first non-expensive scope;
  several matching scopes become expandable groups. Expensive scopes are never loaded automatically.
* **Autos — documented approximation.** DAP has no "variables used near this line" query. Autos takes the Locals' variables and, when the
  editor has the stop location's file open (`IDocumentManager`/`INavigationService.OpenDocuments`), keeps the ones whose name occurs as an
  identifier on the **current or the preceding line**; with no document (or no match) it shows all of them minus `this` and static-member groups.
  The value the function just stepped out of returned comes first, whatever the line mentions (Visual Studio's "… returned"): GDB's
  `returnValue` scope, and a return value an adapter lists among the locals (CodeLLDB's `[return value]`, js-debug's `Return value`).
* **Watch**: expressions live in `IDebugService.Watches` (so they survive the window being hidden) and are persisted per workspace in
  `IStateStorage` (`debug.watches`, restored when the service has none). `Variables/WatchStore` loads the saved list before the first edit,
  wherever it comes from (Locals' Add Watch, the command), and follows `IWorkspaceService.FoldersChanged`: when the primary folder changes the
  service's list is replaced by the new folder's saved one (nothing is saved in between). The storage swaps its workspace scope before that
  event (and other workspace dependents may take a while in between), so the list is saved through `IWorkspaceStateScope.TrySetForFolder`
  for the folder it was loaded from: an edit made in that window is refused rather than written over the new folder's list. Evaluated with context `watch` in the focused frame on every stop/step/frame
  switch; errors are red with the adapter's message; results with `variablesReference` expand; values stay (dimmed) while running.
  Edit inline, Delete, Alt+↑/↓ or **drag and drop** to reorder (the service has no reorder operation, so the list is rebuilt in the new order).

### Call Stack, Threads

* Flat frame list for one thread, otherwise thread rows with their frames. Frames are paged (50 per request + `… load more frames`). Frames without
  source, hinted `subtle`/`label`, or de-emphasised by the adapter (`deemphasize` on the frame or its source: js-debug's skipped Node internals)
  are greyed; "Show External Code" off folds runs of them into one `[External Code]` row — except `label` frames (js-debug's `await` between the
  frames of an async call stack), which stay visible as separators and cannot be picked. Frames the **adapter skips** (`deemphasize`, js-debug's
  `skipFiles` such as `<node_internals>/**`) fold even with "Show External Code" on (the default) when two or more are in a row, as VS Code
  collapses them. Clicking (or Enter on) an `[External Code]` row shows its frames, greyed, until the next stop.
* Yellow arrow = the stopped location; a picked caller frame gets the outlined (green) arrow. Click picks the frame through
  `IDebugFrameFocus.SetFocusedFrameAsync` (which moves the editor's stop marker and re-scopes Locals/Autos/Watch) and opens the source via
  `INavigationService` (keeps keyboard focus in the window); double-click/Enter also moves focus to the editor. Copy Call Stack. The service
  throws for a frame it cannot focus (no longer valid, ended or unfocused session — the `IDebugFrameFocus` contract), so the arrow moves and the
  source opens only when `FocusedFrameId` really became the picked frame; otherwise the status bar names the frame and the reason
  (Locals/Autos/Watch and the REPL follow the service, never just the row). The test fake throws the same way.
* **Where the debugger stopped**: when a stop (breakpoint, step) is in a document that is not the active editor — another file, a file not open
  at all — the runtime opens it at the stop line (preview tab, keyboard focus kept); the editor marks and reveals the line.
* Threads lists id, name, top frame and state; switching focuses the thread's top frame (with the same check). **Freeze/thaw is not offered** (no contract for it).

### Breakpoints

Source / function / exception breakpoints with enable checkboxes, condition / hit-count / log-message badges and the adapter's verdict from
`IBreakpointManager.GetStatus` (verified, unverified = hollow, moved-to-line note, failed message in red). Condition, hit count and log message
are edited through `IWindowService.ShowInputBoxAsync` — a source breakpoint's condition, hit count and log message only when the active
adapter supports them (`DebugCapabilityGate`: the running session's capabilities, else the last ones of the selected configuration's type;
netcoredbg has conditions but no hit counts or logpoints, js-debug all three): otherwise the menu entry is disabled with "(not supported)"
and the reason as tooltip, and the command says why instead of asking (one the breakpoint already has can still be edited away); Delete / Delete All / Enable All / Disable All / New Function Breakpoint; double-click or
Enter navigates (to the adapter-moved line when it moved). Rows update **in place** when only a status or flag changed. **Limit:** the manager
contract cannot update a function breakpoint, so its checkbox is read-only.

### Debug Console

* **Output path** (isolation from a flooding debuggee): the debug lane only calls `ConsoleOutputPump.Enqueue` (O(1), bounded queue,
  drop-oldest); a pool thread, at most once per frame (≥ 16 ms), keeps the newest 5,000 lines (older text is *counted*, not split), parses their
  ANSI escapes and builds immutable lines; the UI applies **everything waiting in one pass per batch**: at most one precise Remove (the oldest
  lines over the 5,000 bound) and one precise Add (a batch of 5,000 or more: one Reset). Lines waiting for the UI are bounded to 5,000 too
  (older ones would be trimmed on arrival: dropped while waiting, with a note). So the list control does its per-change work (realising the
  rows now on screen) once per frame however fast the program writes; the cost per line on the UI thread is a reference copied into a list.
  Late subscribers are seeded from `IDebugOutputHistory` (deduplicated by `DebugOutputEventArgs.Sequence`), `OutputCleared` is honoured.
  Output events that end without a newline become their own line (no cross-event line joining).
* **Rows** are `Views/ConsoleLineView`, a control that draws its `ConsoleLine` as one text layout (no `TextBlock`, no bindings, no style
  classes): the list's virtualisation recycles a few dozen of them for any number of lines, and a recycled row builds one layout. Categories
  colour the lines (stdout in the foreground colour, stderr red, console dim, important yellow, input blue, notes italic); the colours come
  from the theme's `vsc:*` brushes, resolved once by `ConsoleView` into `ConsoleBrushes` and inherited by the rows (re-resolved when the theme
  changes or mutates its brushes).
* **ANSI colours** (`Console/AnsiText.cs`, parsed on the pump thread): SGR sets 16-colour (the theme's `terminal.ansi*`), 256-colour and
  24-bit foreground and background, bold, dim, italic, underline, strikethrough and inverse; every other escape (cursor movement, erase, OSC
  titles and hyperlinks, charset selection) is removed, so `Text` (filter, copy) is what is shown. The style carries from line to line and
  event to event within one category (stdout's colour never leaks into stderr) and is reset by Clear. A line holds its styled runs as
  `ConsoleSpan`s (none for plain lines: plain output allocates nothing extra).
* **REPL**: Enter evaluates in the focused frame (context `repl`), Shift+Enter adds a line, ↑/↓ walk a 200-entry history (at the first/last line),
  completions via `CompletionsAsync` (debounced, cancellable; ↑↓ select, Tab/Enter accept, Esc close, Ctrl+Space ask). Clear, text filter
  (off the UI thread), lock-scroll toggle, Ctrl+C copies the selected lines.
* The shell disposes the console's view-model whenever the window is hidden (also at the end of every session), so the REPL's history and
  transcript live in the app-lifetime `Console/ReplState`: the history is also persisted in `IStateStorage` (`debug.replHistory`, application
  scope), and the transcript (≤ 1,000 lines) is replayed by a new console with each line after the output event it followed. Clear empties it.
  A REPL line records the `Sequence` of the newest output event when it was entered and goes after the last shown event whose sequence is not
  greater (never before the line entered before it). The history's copies keep the sequence: an ended session's events relabelled by
  `ArchiveHistory`, and an oversize batch whose head the history cut (`with { Output = tail }`: matched neither by reference nor by its
  `Output` string before, so the line was replayed at the top). A line whose event left the history goes after the output that came before it.
  Lines entered in this console take the same path (through the pump, placed with the same rule), so the live console and a replay agree.

### Exception Helper, Modules (SHOULD)

* `debug.exception`: on a stop with reason `exception` it reads `exceptionInfo` through `IDebugSession.CustomRequestAsync` (type, message, stack,
  inner chain, break mode) or falls back to the stop description; **Copy Details**, **Continue**. `ExceptionHelperViewModel` + `ExceptionHelperView`
  are reusable by the editor as a popup. The runtime keeps the context key `cscode.debug.exceptionStopped` true while such a stop is current.
* `debug.modules`: loaded modules on every stop and on demand, bounded to 2,000 rows. A session implementing the Core `IDebugSessionModules`
  (the DAP session does) supplies the list it keeps from the adapter's `module` events and sends the `modules` request only when the adapter
  advertises `supportsModulesRequest` (netcoredbg does not implement it). While the window is shown it also re-reads that list (no request)
  on the session's `ModulesChanged`, which the DAP session fires once per burst of `module` events, so modules loaded while the debuggee
  runs appear at once. Other sessions are asked with the raw request through `CustomRequestAsync`; one that refused it shows the message and
  is not asked again at every stop (Refresh retries).

## Threading and memory

Nothing blocks the UI thread (the banned-API analyzer is on). Results are applied in ≤ 4 ms slices; bursts of events are coalesced; every
subscription lives in a `DisposableStore`/the tracker and is released when the view is hidden. Verified by tests (below): flooding debuggee
(millions of lines/s), a million-element array from an adapter that ignores paging, slow and hung adapters, 150 rapid steps, and show/hide of
every window 100× across 20 sessions with `GC`-based retention checks.

## Tests (`tests/CsCode.Panels.Debug.Tests`, xUnit v3 + Avalonia.Headless)

```bash
dotnet test tests/CsCode.Panels.Debug.Tests                                  # everything (the real-adapter test skips if netcoredbg is missing)
dotnet test tests/CsCode.Panels.Debug.Tests --filter "Category=RealAdapter"  # only the end-to-end test against real netcoredbg
tools/fetch-netcoredbg.sh                                                    # fetch the adapter into .scratch/tools/netcoredbg (git-ignored)
```

On Nix-provided dotnet, `source tools/nix-dev-env.sh &&` first (Skia needs the native libraries) — the screenshot, interaction and hostile tests use the
real Skia headless platform.

| Suite | What it proves |
|---|---|
| `VariablesTests`, `WatchTests`, `CallStackTests`, `BreakpointsTests`, `ConsoleTests`, `ExceptionAndModulesTests` | view-model behaviour against fake `IDebugService`/`IDebugSession` (lazy expansion, paging, chunked inserts racing collapse/re-expansion, change highlighting incl. Autos, handle invalidation, edits, watch persistence and folder switches, frame focus incl. a declined focus, breakpoint states, console bounding/history/completions, REPL state across console views and across a session end, exception helper, modules) |
| `InvalidationTests` | adapter `invalidated`: Locals/Watch reload in a live frame, a variables-only one without a stack request or a pending focus, renumbered frames, one shared stack request, no ended session kept by a tracker that outlives it |
| `ChangeHighlightTests` | change highlighting by the frame's function (js-debug on Node.js 24 shapes): names added and removed between stops of one frame, a new local marked, a block scope coming and going, one shared identity request per stop, no identity of an earlier stop's caller frame, the name fallback without an identity |
| `RuntimeTests` | `debug.addToWatch` with the Watch tab hidden / on screen / brought forward, and the Ctrl+Alt+W contribution |
| `InteractionTests` | real views in a headless window: keyboard model, inline editors, drag-reorder, context routing |
| `ScreenshotTests` | renders every window in the VS Dark/Light/Blue themes → `.scratch/screenshots/debug-*.png` (git-ignored) |
| `RealDebuggerEndToEndTests` | **real `CsCode.Dap` + real netcoredbg**: breakpoint, real frames, real locals, frame switch, step with a highlighted change, watch, REPL, console stdout, modules from `module` events, clean exit, no stray processes |
| `RealJsDebugPanelsTests` | **real `CsCode.Dap` + real js-debug** on a TypeScript program compiled by tsc: moved breakpoint in the Breakpoints window, Locals values (classes, `'strings'`, Map/Set, a getter run in place, a 250-element array paged to its end), REPL + completions, set variable from Locals, Autos, Watch errors, the async call stack with Node internals folded by default and shown greyed on a click, an async frame without variables, step highlighting, stderr in red, the Exception window; a 10,000-element array paged to its last element, a getter that throws, a getter's type, an evaluated getter at the next stop |
| `ConsoleFloodTests`, `ConsoleAnsiTests` | the console under a flood: a 3,000-line batch reaches the list in ONE Add and trimming is ONE Remove, 2,000 one-line events within a frame are applied together, rows are recycled `ConsoleLineView`s (never one control per line) following the newest line, ANSI colours drawn in the theme's terminal colours (pixels of the rendered frame), a REPL line keeping its place after an oversize batch the history cut; the ANSI parser (SGR 16/256/24-bit, attributes, removed sequences, carry-over per category, cut lines) |
| `RealJsDebugResponsivenessTests` | 1,000,000 lines through js-debug with the Debug Console on screen, in two shapes (batched `process.stdout.write`, and one `console.log` per line = one DAP event each): dispatcher p99 < 16 ms, max < 50 ms; each attempt also reports the UI thread's CPU time and the GC pauses (`GcPauseRecorder`) (numbers in docs/DEBUGGING-RUST-TS.md) |
| `JsDebugReviewFixTests` | regression tests of the second round (reviews and the integrator's run on a real display): skipped frames folded by default, the 10,000 cap and its notice, a throwing getter, an evaluated getter at the next stop, a lazy value's type |
| `JsDebugPanelFixTests` | regression tests of the js-debug wave: deemphasized/label frames, quoted strings, lazy getters, filtered paging, `Return value` shape, opening the stop file, the throwing frame focus (fake and real), hit counts/logpoints per capability |
| `ResponsivenessTests` | dispatcher heartbeat (p99 < 16 ms, max < 50 ms) under the hostile scenarios, including a sustained one-line-per-event flood from two sessions (~25,000 events/s for 3 s, about js-debug's rate; needs no Node.js: it runs in CI) that must change the console's list at most once per frame; `.scratch/responsiveness.log` has the measured numbers |
| `LeakTests` | show/hide ×100 across 20 sessions: nothing reachable, no subscriptions left on service/sessions/breakpoint manager |

## Trying it by hand

`src/CsCode.Panels.Debug/Demo` is a developer-only project (not in `CsCode.slnx`, excluded from the library's compile) that opens the panels in a
real window and debugs a tiny C# program with the real DAP module and netcoredbg:

```bash
tools/fetch-netcoredbg.sh
dotnet run --project src/CsCode.Panels.Debug/Demo            # press Start (breakpoint is set in Add())
dotnet run --project src/CsCode.Panels.Debug/Demo -- --start # start the session immediately
```

## Known limits

* Autos is an approximation (see above); Threads cannot freeze/thaw; function-breakpoint checkboxes are read-only; the Exception window (and
  the Modules window for sessions without `IDebugSessionModules`) uses `CustomRequestAsync` because the contracts have no typed API for it;
  the Modules window of such a session refreshes at stops, not live while running; hover pins / DataTips are the editor's job.
* Console output without a trailing newline is not joined with the next event's text.
* With scrolling locked during a flood, trimming the oldest lines moves the rows on screen up (the list keeps its offset, not its first row).
* The ANSI style of output the pump dropped unseen (more than 5,000 lines in one frame) is not tracked: the first line kept starts with the
  style the stream had before the drop.
* Value edits refresh only the edited row (dependents update at the next stop or via a reload).
