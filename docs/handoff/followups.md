# Follow-ups reported by the fix wave (merged to main at 09b05f8), not done yet

Triaged against main on 2026-10-30: entries found already fixed were removed (capability-gated breakpoint menu, TaskService tracking, stop-line re-centring, floating tool windows, Auto Hide in layouts, the Dark+ chrome icons); netcoredbg stays at normal priority by the owner's decision (lowering it slows the debuggee). The small entries below are being fixed on the fu/* branches; the rest wait for later waves.

Debugger (most handled by the TypeScript/js-debug wave; check its docs before redoing):
* DebugFocusTracker waits out its 400 ms PendingGrace after a variables/threads-only invalidation (Locals/Watch show "Loading…" briefly).
* REPL line placement after a flood: an oversize output batch replaced with `with { Output = tail }` loses identity; needs a sequence number on DebugOutputEventArgs.
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
* Debug Console under a realistic 1M-line flood (one console.log per line, console on screen): dispatcher p99 17-25 ms, max 53/70 ms once each (budget 16/50). The unfinished attempt (one apply per frame, recycled rows in a custom ConsoleLineList, GC-pressure diagnostics) was lost when /tmp was wiped by the 2026-10-29 13:57 reboot.
