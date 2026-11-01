# Follow-ups reported by the fix wave (merged to main at 09b05f8), not done yet

Triaged against main on 2026-10-30: entries found already fixed were removed (capability-gated breakpoint menu, TaskService tracking, stop-line re-centring, floating tool windows, Auto Hide in layouts, the Dark+ chrome icons); netcoredbg stays at normal priority by the owner's decision (lowering it slows the debuggee). The small entries below are being fixed on the fu/* branches; the rest wait for later waves.

Debugger (most handled by the TypeScript/js-debug wave; check its docs before redoing):
* DebugFocusTracker waits out its 400 ms PendingGrace after a variables/threads-only invalidation (Locals/Watch show "Loading…" briefly).
* IDebugSessionModules has no change event (Modules window refreshes at stops and on Refresh only).

Shell:
* AvaloniaEdit's static RoutedCommand._inputElement keeps the last focused editor view after the last document closes (bounded to one view); Editor could clear it in DisposeView.
* LanguageServerManager.Dispose() (sync) can block up to 15 s unless DisposeAsync ran first.
* Platform WorkspaceService swaps the StateStorage workspace scope before FoldersChanged; a watch edit in that window could save into the new folder.

Platform / Search:
* FileWatcher: a file created in a new directory before the OS watch arms can be reported Changed instead of Created (coalescer Changed+Created -> Changed).
* Each watched root costs one inotify instance on Linux.
* ReplaceService.cs:312 keeps the unexpanded replacement on RegexMatchTimeoutException (silently wrong preview under CPU pressure).
* Editor's fallback FswFileWatcher has neither missing-root waiting nor polling-after-losses; DebugConfigurationService.cs:211 comment partly stale.

Deferred to the performance/profiling wave (user decision 2026-10-29):
* (done on feat/perf-console: Debug Console 1M-line flood and REPL placement after a flood, see docs/PANELS-DEBUG.md "Debug Console" and docs/DEBUGGING-RUST-TS.md) Left from it: with scrolling locked during a flood, trimming the oldest lines moves the rows on screen (no first-row anchoring); the headless flood tests see gen2 GCs induced by the headless framebuffer (Avalonia's headless window allocates a new WriteableBitmap, with GC.AddMemoryPressure, for every rendered frame: "induced_noforce" in the reported GC pauses, max ~23 ms on a loaded Mac), which the real app does not have.
