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

**Commands.** The panels register one command: `debug.addToWatch` (argument: the expression; with no argument it focuses the Watch
window's empty row, or asks with an input box when the window is closed; the Watch window is then shown through the shell's `IViewActivator`, as Visual
Studio does on Add Watch). Modules is manual: it is not in the shell's default layout. The `workbench.action.debug.*` commands belong to the DAP module
and the `workbench.debug.action.toggle*` ids are only *declared* here: **the shell must bind each `IToolWindow.ToggleCommandId` to
`ToolWindowManager.Toggle(id)`** (nothing in the panels can toggle a dock). **Context key** published for auto-show:
`cscode.debug.exceptionStopped`.

**Consumed services** (all from DI, only `IDebugService` is required, the rest degrade gracefully): `IDebugService`, `IDebugFrameFocus`,
`IDebugOutputHistory`, `INavigationService`, `IWindowService`, `ICommandService`, `IContextKeyService`, `IStateStorage`,
`IDocumentManager` (Autos' line text), `IUiDispatcher` (falls back to the Avalonia dispatcher). Colours are `{DynamicResource vsc:…}`
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
* `Infrastructure/UiAsync.OnUi` — never captures a `SynchronizationContext` (so view-models behave the same under a test dispatcher) and a
  cancelled token unblocks the await **even if the adapter ignores it**: a hung adapter cannot keep a closed panel alive.
* `Variables/VariablesTreeViewModel` — the engine behind Locals/Autos/Watch (and reusable for editor hover pins).

### The variables tree

* **Virtualised by flattening**: visible nodes live in one `RangeObservableCollection` (precise Add/Remove notifications, so selection, focus and
  scroll survive expand/collapse and paging; the shell's `BulkObservableCollection` is used for the lists that are replaced wholesale).
  A million-element array shows one page (100 rows) + a pager row.
* **Lazy + paged**: `GetVariablesAsync(ref, start, 100)`; the `… N more` row loads the next page. A short page ends the list whatever the adapter
  announced; at most 10,000 children per node are ever held (then a notice row). An adapter that ignores `start/count` is recognised and cannot
  duplicate rows; a page of up to 10,000 nodes is created in ≤ 4 ms slices (`UiDispatcher.PostBatched`) and inserted in 100-row chunks.
* **Handle lifetime**: on `Continued`/step/session end the generation is bumped, in-flight loads are cancelled, **every node is dropped**
  (nothing holds a stale `variablesReference`), but expansion *paths* and the shown values are remembered. After the next stop the tree is
  re-expanded lazily (one request at a time) and values that differ from the previous stop are red. Baselines are kept **per frame shape**
  (the frame's first root names), so stepping into another function does not paint its locals red and picking another frame in the Call Stack
  does not turn the current stop's values into the baseline.
* **Editing**: `setVariable` (only when the adapter advertises it and the parent handle is live); a failure stays in the inline editor with the
  adapter's message. Display: strings quoted, ≤ 200 chars + `…` (full text in tooltip and Copy Value), newlines shown as `⏎`, integers
  hex/decimal client-side (per tree and per row). Copy Value / Copy Expression (`evaluateName`) / Add Watch.
* **Keyboard**: ↑↓ move, → expand (or into the children), ← collapse (or to the parent), Enter edits (or loads more / opens the empty watch row),
  F2 rename/edit, Delete remove a watch, Ctrl+C copy the value, Alt+↑/↓ reorder watches; Enter/Esc end an inline edit and keep the keyboard in the list.

### Locals, Autos, Watch

* **Locals**: the variables of the focused frame's scopes hinted `locals`/`arguments` (or named like them), else the first non-expensive scope;
  several matching scopes become expandable groups. Expensive scopes are never loaded automatically.
* **Autos — documented approximation.** DAP has no "variables used near this line" query. Autos takes the Locals' variables and, when the
  editor has the stop location's file open (`IDocumentManager`/`INavigationService.OpenDocuments`), keeps the ones whose name occurs as an
  identifier on the **current or the preceding line**; with no document (or no match) it shows all of them minus `this` and static-member groups.
* **Watch**: expressions live in `IDebugService.Watches` (so they survive the window being hidden) and are persisted per workspace in
  `IStateStorage` (`debug.watches`, restored when the service has none). Evaluated with context `watch` in the focused frame on every stop/step/frame
  switch; errors are red with the adapter's message; results with `variablesReference` expand; values stay (dimmed) while running.
  Edit inline, Delete, Alt+↑/↓ or **drag and drop** to reorder (the service has no reorder operation, so the list is rebuilt in the new order).

### Call Stack, Threads

* Flat frame list for one thread, otherwise thread rows with their frames. Frames are paged (50 per request + `… load more frames`). Frames without
  source or hinted `subtle`/`label` are greyed; "Show External Code" off folds runs of them into one `[External Code]` row.
* Yellow arrow = the stopped location; a picked caller frame gets the outlined (green) arrow. Click picks the frame through
  `IDebugFrameFocus.SetFocusedFrameAsync` (which moves the editor's stop marker and re-scopes Locals/Autos/Watch) and opens the source via
  `INavigationService` (keeps keyboard focus in the window); double-click/Enter also moves focus to the editor. Copy Call Stack.
* Threads lists id, name, top frame and state; switching focuses the thread's top frame. **Freeze/thaw is not offered** (no contract for it).

### Breakpoints

Source / function / exception breakpoints with enable checkboxes, condition / hit-count / log-message badges and the adapter's verdict from
`IBreakpointManager.GetStatus` (verified, unverified = hollow, moved-to-line note, failed message in red). Condition, hit count and log message
are edited through `IWindowService.ShowInputBoxAsync`; Delete / Delete All / Enable All / Disable All / New Function Breakpoint; double-click or
Enter navigates (to the adapter-moved line when it moved). Rows update **in place** when only a status or flag changed. **Limit:** the manager
contract cannot update a function breakpoint, so its checkbox is read-only.

### Debug Console

* **Output path** (isolation from a flooding debuggee): the debug lane only calls `ConsoleOutputPump.Enqueue` (O(1), bounded queue,
  drop-oldest); a pool thread, at most once per ≥ 16 ms, keeps the newest 5,000 lines (older text is *counted*, not split) and builds immutable
  lines; the UI applies ≤ 150 lines per ≤ 4 ms callback and trims to 5,000. Late subscribers are seeded from `IDebugOutputHistory` (deduplicated by
  reference), `OutputCleared` is honoured. Categories colour the lines (stdout / stderr red / console dim / important yellow); output events that
  end without a newline become their own line (no cross-event line joining).
* **REPL**: Enter evaluates in the focused frame (context `repl`), Shift+Enter adds a line, ↑/↓ walk a 200-entry history (at the first/last line),
  completions via `CompletionsAsync` (debounced, cancellable; ↑↓ select, Tab/Enter accept, Esc close, Ctrl+Space ask). Clear, text filter
  (off the UI thread), lock-scroll toggle, Ctrl+C copies the selected lines.

### Exception Helper, Modules (SHOULD)

* `debug.exception`: on a stop with reason `exception` it reads `exceptionInfo` through `IDebugSession.CustomRequestAsync` (type, message, stack,
  inner chain, break mode) or falls back to the stop description; **Copy Details**, **Continue**. `ExceptionHelperViewModel` + `ExceptionHelperView`
  are reusable by the editor as a popup. The runtime keeps the context key `cscode.debug.exceptionStopped` true while such a stop is current.
* `debug.modules`: loaded modules read with the DAP `modules` request through `CustomRequestAsync` on every stop and on demand (the Core contracts
  carry no module events); bounded to 2,000 rows. Adapters without the request show a message.

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
| `VariablesTests`, `WatchTests`, `CallStackTests`, `BreakpointsTests`, `ConsoleTests`, `ExceptionAndModulesTests` | view-model behaviour against fake `IDebugService`/`IDebugSession` (lazy expansion, paging, change highlighting, handle invalidation, edits, watch persistence, frame focus, breakpoint states, console bounding/history/completions, exception helper) |
| `InteractionTests` | real views in a headless window: keyboard model, inline editors, drag-reorder, context routing |
| `ScreenshotTests` | renders every window in the VS Dark/Light/Blue themes → `.scratch/screenshots/debug-*.png` (git-ignored) |
| `RealDebuggerEndToEndTests` | **real `CsCode.Dap` + real netcoredbg**: breakpoint, real frames, real locals, frame switch, step with a highlighted change, watch, REPL, console stdout, clean exit, no stray processes |
| `ResponsivenessTests` | dispatcher heartbeat (p99 < 16 ms, max < 50 ms) under the hostile scenarios; `.scratch/responsiveness.log` has the measured numbers |
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

* Autos is an approximation (see above); Threads cannot freeze/thaw; function-breakpoint checkboxes are read-only; Modules/Exception use
  `CustomRequestAsync` because the contracts have no typed API for them; hover pins / DataTips are the editor's job.
* Console output without a trailing newline is not joined with the next event's text.
* Value edits refresh only the edited row (dependents update at the next stop or via a reload).
