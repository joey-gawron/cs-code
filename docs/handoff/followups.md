# Follow-ups reported by the fix wave (merged to main at 09b05f8), not done yet

Debugger (most handled by the TypeScript/js-debug wave; check its docs before redoing):
* Breakpoint context menu offered hit-count and logpoints without checking capabilities (handled in the TS wave).
* Dap TaskService processes not tracked / no Dispose (handled in the TS wave).
* Editor StopLineHandler re-centred on every SetDebugStopLine (handled in the TS wave).
* DebugFocusTracker waits out its 400 ms PendingGrace after a variables/threads-only invalidation (Locals/Watch show "Loading…" briefly).
* REPL line placement after a flood: an oversize output batch replaced with `with { Output = tail }` loses identity; needs a sequence number on DebugOutputEventArgs.
* IDebugSessionModules has no change event (Modules window refreshes at stops and on Refresh only).
* netcoredbg still starts at normal priority.

Shell:
* Floating tool windows do not work (no host-window locator); a closed floated tool fires visibility=false twice.
* AvaloniaEdit's static RoutedCommand._inputElement keeps the last focused editor view after the last document closes (bounded to one view); Editor could clear it in DisposeView.
* LanguageServerManager.Dispose() (sync) can block up to 15 s unless DisposeAsync ran first.
* Platform WorkspaceService swaps the StateStorage workspace scope before FoldersChanged; a watch edit in that window could save into the new folder.
* Auto Hide state is not saved in layouts (Reset and layout restore dock every tool).

Platform / Search:
* FileWatcher: a file created in a new directory before the OS watch arms can be reported Changed instead of Created (coalescer Changed+Created -> Changed).
* Each watched root costs one inotify instance on Linux.
* ReplaceService.cs:312 keeps the unexpanded replacement on RegexMatchTimeoutException (silently wrong preview under CPU pressure).
* Editor's fallback FswFileWatcher has neither missing-root waiting nor polling-after-losses; DebugConfigurationService.cs:211 comment partly stale.

Deferred to the performance/profiling wave (user decision 2026-10-29):
* Debug Console under a realistic 1M-line flood (one console.log per line, console on screen): dispatcher p99 17-25 ms, max 53/70 ms once each (budget 16/50). The unfinished attempt (one apply per frame, recycled rows in a custom ConsoleLineList, GC-pressure diagnostics) was lost when /tmp was wiped by the 2026-10-29 13:57 reboot.

Check when merging the theme dropdown (seen 2026-10-29 13:52 on main's build, theme "vscode.dark-plus" + keymap "vscode"):
* With the Dark+ theme the coloured chrome icons vanish: the green Start triangle on the toolbar and the error/warning/info icons on the Error List filter buttons are invisible (monochrome icons are fine). The shell's icon colour keys are missing from the VS Code themes; the themes branch must fill them from the VS2022 base. Note: the Editor's ColorThemeService already registers VS Code themes (e.g. "vscode.dark-plus"), not only the three VS2022 ones.
