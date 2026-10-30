# Background mode and saved sessions

CsCode can stay running in the background so a window with a saved session comes back at once ("hot load"). Brief:
`docs/briefs/background-sessions.md`. Code: `src/CsCode.Sessions/` (Avalonia-free: model, store, hot-exit backups, IPC, locks, launcher
decision logic), `src/CsCode.App/Background/` (the window side), `src/CsCode.App/Program.cs` (the three roles of the executable),
`tools/autostart/`. Tests: `tests/CsCode.Sessions.Tests/` (unit, in-process IPC) and `tests/CsCode.App.Tests/Background/` (real processes).

## Architecture: one process per session, plus an optional warm spare

```
 cscode (CLI / desktop entry / tray) ──local IPC──► session process "work"  (window shown or hidden)
                                     ──local IPC──► session process "oss"   (hidden, trimmed)
                                     ──local IPC──► warm spare              (opt-in; no session; claimed on demand, then replaced)
```

Core services are process-wide singletons bound to one workspace, so a session is a process. The `cscode` executable has three roles,
chosen in `Main` before Avalonia is touched:

| Role | How it starts | What it does |
|---|---|---|
| **Launcher** (default) | `cscode [options] [paths]` | Talks to the running CsCode processes over IPC (show / claim / start / quit / list) and exits. No Avalonia, no services. |
| **CsCode process** | `cscode --host --session NAME` or `--host --spare` (started by the launcher) | Takes the session's lock, opens its IPC endpoint at once, builds the services and the window, restores the session. |
| **In-process window** | `cscode --foreground [paths]`, or automatically when background mode is unavailable (no private runtime directory) | The classic single-process run: the window opens in this process and closing it exits. |

A process started by the launcher detaches from the launcher's terminal (`setsid`) and has no stdio; it logs to `{LogsDir}` as usual.

## Command line (stable interface)

The install wave makes `~/.local/bin/cscode` this launcher; scripts, the desktop entry and `git config core.editor "cscode --wait"` depend on it.

```
cscode [paths...]                 folder → workspace, file → editor; file:line[:col] jumps to a position
cscode -g|--goto file:line[:col]
cscode -s|--session NAME [paths]  the named session (shown at once when running; created when it does not exist)
cscode -n|--new-window [paths]    a new unnamed session (window-1, window-2, ...)
cscode -w|--wait FILE             returns when FILE's tab is closed (for $EDITOR / git)
cscode --list-sessions            saved and running sessions (state, pid, memory, last used, folder)
cscode --background               start the warm spare (if on) and, with restoreOnLogin, the resident sessions, hidden; no window
cscode --quit [NAME]              quit a session's process (default: the last active one)
cscode --quit --all               quit every CsCode process of this user
cscode --foreground [paths]       run the window in this process, without background mode
cscode -h|--help, -v|--version
```

Without `--session`: files open in the **last active** session; a **folder** opens the saved session whose primary folder it is (else a new
unnamed session with it); no arguments follow `cscode.sessions.default` (`last` | `new` | a name). Exit codes: 0 success; 1 the request
failed (message on stderr). Unknown switches are reported and ignored.

Internal switches (how the launcher starts processes; not part of the interface): `--host`, `--spare`, `--hidden`, `--unnamed`,
`--seed-from NAME`, `--headless-test` (Avalonia headless platform + test-only IPC verbs; integration tests only).

## Launcher decision table (`LaunchPlanner`, pure, unit-tested row by row)

| Situation | Action |
|---|---|
| A responsive process owns the session | **Show** it (open the paths there); reply comes after the window painted |
| Not running, a warm spare answers | **Claim** the spare (it restores the session hidden, then the show makes it visible); start a new spare when `warmSpare` is on |
| Not running, no spare (or the claim fails) | **Start** a session process, wait for its hello, then show; with `warmSpare` on, start a spare if none exists or is starting |
| The session's lock is held but no process answers (wedged) | Never wait: **Start** a new unnamed session seeded from it, with a warning |
| A spare is still starting | Not claimed, and no second spare is started |
| More than `maxResidentSessions` would be resident | After the show, quit the least recently shown **hidden** sessions (never a shown window, a debugging session, the spare or the target) |
| Two launches at once | Serialised by the launcher lock (`<runtime>/launcher.lock`); the lock is released as soon as the target owns the session (before `--wait` blocks) |
| Stale endpoint (dead pid, or a pid reused by another program) | Removed during discovery |
| Runtime directory unusable | Exit code 2 internally → the window runs in-process |

## IPC

* **Runtime directory** (`RuntimeDirectory.Resolve`): Linux `$XDG_RUNTIME_DIR/cscode` (else `/tmp/cscode-<uid>`), macOS `$TMPDIR/cscode-<uid>`,
  Windows `%LOCALAPPDATA%\CsCode\run` for lock files with named pipes `cscode-<hash>-<pid>`. With **`CSCODE_HOME`** set (portable installs and
  every test) it is `$CSCODE_HOME/run` — or, when that path is too long for a Unix socket (~108 bytes), `<per-user base>-h<hash of CSCODE_HOME>` —
  so a test never talks to, locks against or signals the user's running CsCode. Created with mode 0700; a directory with looser permissions
  that cannot be tightened is refused (background mode then falls back to an in-process window).
* **One endpoint per process**: `<runtime>/<pid>.sock` (mode 0600). Every connection's peer credentials are checked on both sides (Linux
  `SO_PEERCRED`, macOS `LOCAL_PEERCRED`): a peer running as another user is disconnected before anything is read. Windows uses a
  current-user-only pipe ACL (`PipeOptions.CurrentUserOnly`; compiled, not tested here). No network listener, ever.
* **Framing**: 4-byte little-endian length + UTF-8 JSON (≤ 4 MB), hand-written codec (`IpcCodec`, so a fresh launcher process does not build
  serializer metadata). One request → one reply; a connection may carry several.
* **Verbs**: `hello` (pid, session, state `starting|spare|shown|hidden|quitting`, last shown, debugging, working set — answered without the UI
  thread), `show {paths, wait}`, `hide`, `claim {session, hidden, unnamed}`, `listSessions`, `quit {force}`, `focusFile {paths}`. Test-only
  (served only with `--headless-test`): `inspect`, `testEdit`, `testCaret`, `testFold`, `testCloseTab`, `testCloseWindow`.
* **Timeouts**: connect + handshake 300 ms (past it a process counts as wedged and is never waited for), show reply 30 s (a cold restore),
  started-process hello 30 s, quit wait 20 s.
* **Locks**: `<runtime>/locks/<session>.lock`, an exclusive `flock` held by the owning process (released by the kernel on kill -9). A second
  process for the same session exits with code 3 and the launcher shows the owner instead.
* The launcher is synchronous (blocking sockets on its one thread: no async socket engine or thread pool to start); CsCode processes accept on a
  dedicated thread and serve connections asynchronously, so a long `show --wait` never blocks a `hello`.

## What a session holds

`{UserDataDir}/sessions/<name>.json` (schema v2, `SessionDocument`): name, unnamed flag, last used, workspace folders (local or `ssh://`), editors
in tab order (uri, group, order, pinned, preview, language of untitled buffers, **view state**: caret, selection, scroll offsets, folded lines,
and the id of the hot-exit backup), the active editor, the shell's dock layout snapshot (tool windows, splitters, document groups), window
bounds and state (normal/maximized/fullscreen), and `wasResident`. Breakpoints and watches (Dap), search/palette history and Vim registers are
**not** copied: their modules already persist them per workspace/application, so reopening the session's folder brings them back.

* **Atomic writes** (`AtomicFile`): temp file + flush + rename; the previous version is kept as `.bak`. A torn or corrupt file falls back to
  `.bak` (the user is told); both unreadable → the session starts empty and the damaged file is kept as `.corrupt`. A file written by a **newer** CsCode is never
  loaded or overwritten (the window says so and does not autosave).
* **Migration**: `SessionSerializer.Migrate` upgrades step by step; v1 (development builds: folders, layout, a flat `openFiles` list) → v2.
* **Autosave**: every change (tabs, active tab, edits of dirty documents, folders, tool windows) marks the session; it is written at most 1.5 s
  later even while typing (the timer is armed once per save, never pushed out), and on hide, quit and OS shutdown. Capture runs on the UI thread
  and takes only snapshots; JSON and files are written on the session lane (a dedicated thread). A fatal crash writes the last captured
  snapshot synchronously.
* **Session names**: letters, digits, space and `- _ . ( ) +`, 1–64 characters (they are file names).

## Hot exit (`files.hotExit`, default `onExitAndWindowClose`)

Unsaved text of every dirty document goes to `{UserDataDir}/backups/<session>/<id>.txt` + `<id>.json` (uri, encoding, EOL, language, document
version, and the file's size/time/SHA-256 **at backup time**). Only documents whose version changed since their last backup are captured
again. Restoring reopens the file and puts the unsaved text back (dirty, no prompt); when the file **changed on disk** meanwhile the user is
asked (*Keep My Changes* / *Load Disk Version*); a **deleted** file's text comes back in an untitled buffer; untitled buffers come back as
untitled buffers. The total per session is bounded by `cscode.sessions.maxBackupMb` (200): beyond it the user is warned once, those
documents are not backed up, a normal quit prompts for them, and a forced quit (`--quit`) is refused for that session rather than lose them.
Hot exit never writes the user's files. With `off` (or `onExit` for a window close) quitting prompts as before.

## Window lifecycle

* **Closing the window hides it** when `cscode.background.keepRunning` is true (default on every OS): the process, documents, undo stacks,
  language servers, watchers and debug sessions stay alive. With it false, closing quits the session (with hot exit).
* *File ▸ Close Window (Keep Running)*: hide. *File ▸ Quit Session*: persist (with backups, `wasResident` false) and end this process.
  *File ▸ Exit* / *Exit All*: every other CsCode process quits (hot exit), then this one.
* *File ▸ Sessions ▸* Open Session…, Switch Session… (`workbench.action.switchSession`, **Ctrl+Alt+S**; shows the other session and hides
  this one), Save Session As… (the old session stays as saved), Rename Session…, Delete Session… (only sessions that are not running),
  Start in the Background at Login….
* The window title is `file - session — folder - CsCode` for named sessions; the status bar shows the session (click: Switch Session).

## Hidden sessions: resource policy (`HiddenSessionPolicy`)

* After `cscode.background.trimAfterSeconds` (30): every editor view is disposed (view state kept, so it comes back exactly as it was),
  the registered trimmables run (`IMemoryMonitor.TrimNowAsync(Aggressive)`), then a compacting full GC including the large object heap and
  `malloc_trim` (`ProcessMemory.TrimNow`). **The Profiling module is still parked**, so this is the minimal trim path the brief allows; when it
  is merged, its monitor's aggressive trim slots in through the same `IMemoryMonitor` call.
* After `cscode.background.idleServerTimeoutMinutes` (15; 0 = never) the running language servers are stopped and restarted when the window is
  shown again; a running debug session keeps them (and keeps the process from being evicted).
* Before showing again, the active tab of each group gets its view back, so the first frame shows the editors.
* No periodic work while hidden: two one-shot timers, nothing else. The UI stall watchdog (Debug builds, or `cscode.diagnostics.uiStallLogMs`),
  which polls the dispatcher every 20 ms, is stopped while hidden and in a waiting spare (`ShellController.SetHidden`). Measured: < 1 % CPU over
  30 s for a hidden, trimmed session (the test asserts it; what remains is the runtime's tiered JIT finishing).
* At most `cscode.background.maxResidentSessions` (3) sessions stay resident: beyond it the least recently shown hidden session persists and
  exits (checked by the launcher after each open and by a session when it is hidden).

## The warm spare (`cscode.background.warmSpare`, default **false**)

Off by default (the user's decision, docs/briefs/wave-3.md): a spare is a whole idle CsCode process (≈100-130 MB) kept for every user just to
make the first open of a session that is not running faster, while a resident session is shown just as fast without it. With it off, the
launcher never starts a spare (a spare left running from when it was on is still claimed, and not replaced), and `cscode --background` starts
nothing unless `restoreOnLogin` is on (it prints why). *Start in the Background at Login…* says in its confirmation what would start with the
current settings. Turned on, the spare works as below.

A process with services and the main window built but no session. After start-up it lays the hidden window out and renders it once into an
off-screen bitmap (templates, styles, layout, Skia and their JIT), then trims. A claim takes the session's lock, restores the session into the
still-hidden window and the launcher's show makes it visible; the launcher then starts the next spare. A spare that is still starting is not
counted as missing, and if two spares come up anyway the one with the higher pid quits. Not warmed: the compositor's first frame of the real
window (it needs a shown window), which is most of the remaining claim latency.

## Tray icon (`cscode.background.showTrayIcon`)

Shown by one process at a time (the holder of `<runtime>/tray.lock`; a process re-checks when it is shown or hidden). Click: open the last
session. Menu (rebuilt off the UI thread when it opens): running sessions with state and memory, *Open Session ▸* saved ones, *New Window*,
*Quit All* — each runs the launcher. Not shown on the headless test platform; without a StatusNotifier host the CLI and desktop entry still
work. The menu model (`TrayMenuModel`) is unit-tested; the icon itself was not exercised on a real tray here.

## Settings

| Key | Default | |
|---|---|---|
| `cscode.background.keepRunning` | `true` | Closing a window hides it |
| `cscode.background.showTrayIcon` | `true` | Tray icon |
| `cscode.background.trimAfterSeconds` | `30` | Hidden → views disposed + memory trimmed |
| `cscode.background.idleServerTimeoutMinutes` | `15` | Hidden → language servers stopped (0 = never) |
| `cscode.background.maxResidentSessions` | `3` | LRU eviction of hidden sessions |
| `cscode.background.warmSpare` | `false` | Keep a pre-started process (≈100-130 MB idle) |
| `cscode.background.restoreOnLogin` | `false` | `--background` also restarts the sessions that were resident (hidden) |
| `cscode.sessions.default` | `"last"` | `last`, `new` or a session name |
| `cscode.sessions.maxBackupMb` | `200` | Hot-exit backup bound per session |
| `files.hotExit` | `"onExitAndWindowClose"` | `off`, `onExit`, `onExitAndWindowClose` |

The launcher reads the four launcher settings straight from `settings.json` (`LaunchSettings.Read`: flat dotted keys or nested objects).

## Autostart (opt-in, never installed silently)

* Linux: `tools/autostart/install-linux.sh [--systemd | --xdg] [--cscode PATH] [--uninstall] [--dry-run]` — a systemd **user** unit
  `cscode-background.service` or an XDG autostart entry, both running `cscode --background`. In the app: *File ▸ Sessions ▸ Start in the
  Background at Login…* writes/removes the XDG entry after a confirmation that names the file and the command (`AutostartEntry`).
* macOS: `tools/autostart/install-macos.sh` (LaunchAgent). Windows: `tools/autostart/install-windows.ps1` (per-user Run key). Both untested here.

## Threading

IPC accepting runs on a dedicated thread; requests are answered without the UI thread (`hello`, `listSessions`) or hop to it for the window
work only. Session loads, autosave, backups, locks and the store run on the session lane (`SerialLane`, a dedicated BelowNormal thread). The
UI thread captures immutable snapshots (document text only for documents whose version changed since their backup) and applies a restored
session (documents load asynchronously; editor views are created lazily — the active tab's first). A wedged or crashed process never blocks
the launcher (300 ms handshake).

## Start-up performance

Measured 2026-10-29 on this machine (22 cores, Linux 7.2) by `StartupBenchmarkTests` (opt-in: `CSCODE_STARTUP_BENCH=<results file>`, and
`CSCODE_TEST_EXECUTABLE=<cscode>` for another build), from `Process.Start` of the launcher to its exit; the launcher exits when the window reported
a painted frame (a captured frame on the headless platform, two animation frames on a real one), after the file given on the command line was
opened and painted too. Medians; nine builders were running at the same time, so each row gives the load average it ran at.

| Build | load | `--version` (runtime floor) | Cold start (no spare) | Resident show | Warm-spare claim |
|---|---|---|---|---|---|
| Debug, JIT (the build the test suite runs) | 3.5–7 | 27 ms | 2.25 s | 97 ms | 0.91 s (latency test alone, load 3.5: 1.55 s) |
| Release, JIT, framework-dependent | 3–4 | 25 ms | 1.97 s | 88 ms | 0.90 s (latency test alone, load 4.1: 1.16 s) |
| Release, ReadyToRun per assembly, self-contained | 5–8 | 27 ms | 1.27 s | 87 ms | 0.61 s (load 8) |
| **Release, ReadyToRun composite, self-contained (the publish)** | 4.6–5 | 37 ms | **0.87 s** | **88 ms** | **0.61 s** (0.40–0.96); latency test alone, load 4.5: 0.53 s |
| the same at load 8–15 | 8–15 | 28–40 ms | 0.93–1.08 s | 93–130 ms | 0.52–0.63 s |
| Debug / Release JIT at load 12–20 (for scale) | 12–20 | 33–110 ms | 3.3–4.0 s | 126–172 ms | 1.35–1.75 s |
| Budget | | — | report | **< 150 ms** | **< 500 ms** |

* **ReadyToRun composite** (`build/Publish.props`, what the installer ships) is the default of the publish: against the JIT it more than halves
  the cold start (1.97 → 0.87 s) and takes a third off the spare claim; per-assembly ReadyToRun stops at 1.27 s. The composite (the app and the
  framework compiled together into `cscode.r2r.dll`, 115 MB) starts the runtime ≈ 10 ms slower (`--version` 37 against 27 ms: the bigger image),
  which the launcher path does not show (resident show 88 against 87 ms); it costs 41 MB more on disk (198 MB in all) and 27 MB more of clean,
  shared file pages per process, while private memory is a little lower than with the JIT (a session 8 s after opening one file: JIT anonymous 79 MB + 42 MB of JIT double-mapping,
  composite 70 MB + 22 MB; resident set 213 MB vs 231 MB, the difference being the mapped image).
* The **resident show** (150 ms) is met by every build on a quiet machine, the Debug one included (the latency test alone passes that part at
  load 3.5): the launcher is ≈ 25-35 ms of runtime start, ≈ 25 ms of its own work (paths, settings, runtime directory) and one `hello` per running
  process, then the show round trip of an already-built window. Above a load of about 10 the JIT builds miss it (126-195 ms); the publish holds.
* The **warm-spare claim** (500 ms) is **not met reliably by any build**: the composite publish claims in 0.34-0.96 s (one traced claim at load 10:
  341 ms; median 0.61 s at load 5; the latency test alone at load 4.5: 528 ms), the JIT builds in 0.9-1.5 s. BackgroundSessionProcessTests'
  latency test (which runs the Debug build next to the tests; `CSCODE_TEST_EXECUTABLE` runs it against the publish) therefore fails on its
  spare assertion; it is not loosened. The spare is off by default, so no default path has this budget. Where the Debug claim goes (trace):
  launcher 90 ms, claim + restore 25 ms, `Show` 55 ms, **the compositor's first frame of the shown window ≈ 390 ms** and **opening the file and painting it ≈ 400 ms** (editor view,
  TextMate grammar, first editor render), all JIT; with the composite: launcher 68 ms, claim + restore 7 ms, `Show` 9 ms, first paint 106 ms,
  the file 150 ms, and the paint phases stretch most under load (software rendering on the headless platform). The spare's off-screen warm-up
  renders the window into a bitmap, which does not exercise the compositor; Avalonia's `Compositor.CreateCompositionVisualSnapshot` would, but refuses the visual of a window that was never shown
  (InvalidOperationException, tried). Warming an editor would need a document, which the editor module only creates registered (an Untitled
  number, language-server opens): not done in a spare, which must stay side-effect free.
* **Cold start**, traced (ReadyToRun composite, load 10, 723 ms): launcher to its plan 57 ms; spawn and the host's runtime start until it answers
  `hello` ≈ 95 ms; host `Main` → Avalonia initialised (styles, Skia, fonts) ≈ 230 ms at load 45, services built off the UI thread ≈ 130 ms,
  `MainWindow` constructed ≈ 390 ms (load 45; the shell's construction); restore 4 ms; `Show` 60 ms; first paint 110 ms; opening the file and
  painting it ≈ 140 ms. The services could be built while Avalonia initialises (they are built after it today) for ≈ 60-130 ms, but every
  module's service constructors would then run before Avalonia's UI dispatcher exists, and one that touched it would bind the dispatcher to the
  wrong thread: not done without an audit of all of them.

Set `CSCODE_SESSION_TRACE=/path/file` to get a per-phase timeline from the launcher and every CsCode process (start-up phases included).

The first measurements (before ReadyToRun; Release and Debug JIT builds, load 6-24), for comparison:

| Path | Release, headless, load ≈ 7 | Release, real X11 (private Xvfb), load ≈ 23 | Debug (the test suite), load 12–24 | Budget |
|---|---|---|---|---|
| `cscode --version` (runtime start-up floor) | 45–90 ms | — | 115–125 ms | — |
| Resident session, hidden → shown | **148 ms** (213–310 ms at load 12–21) | 417–568 ms | 250 ms – 1.4 s | < 150 ms |
| Via the warm spare (claim + restore + first frame) | ~1.0 s | 1.3 s | 1.4–5 s | < 500 ms |
| Cold start (no spare) | 1.6–2.3 s | 2.4 s (spare claimed while still warming) | 3–8 s | report |

### NativeAOT (evaluated, not adopted)

`dotnet publish src/CsCode.App -c Release -r linux-x64 --self-contained -p:PublishAot=true` (with the official ILCompiler packs) **builds**: a
75 MB native `cscode` (+194 MB of symbols) in 2 min 40 s, with 585 trimming/AOT warnings (283 IL2026, 281 IL3050, and IL2075/2070/2071/3000/2104):
App 168, Dap 124, Remote 80, Lsp 56, Macros 44, Platform 36, Editor 28, Core 10, Panels.Search 8, Panels.Debug 8, Sessions 6, Server 5, Vim 4,
Terminal 4; packages Newtonsoft.Json, MessagePack.Annotations (both via StreamJsonRpc) and Dock.Avalonia. **It does not run**:

1. The first session process dies while building the services: `CsCode.Platform.Configuration.BuiltInSettings` serialises its defaults with
   reflection-based System.Text.Json, which NativeAOT disables ("Reflection-based serialization has been disabled for this application").
2. With reflection-based JSON forced back on (`JsonSerializerIsReflectionEnabledByDefault=true`), the next JSON use dies:
   `DictionaryOfTKeyTValueConverter<Dictionary<string,bool>, string, bool>` "is missing native code or metadata" (generic converters over value
   types cannot be made at run time).

What adopting it would take: System.Text.Json source-generated contexts for every serialised type in 12 modules (≈ 280 call sites:
`SerializeToElement`, `Deserialize<T>`, `JsonArray.Add<T>`...), the 136 `Binding` paths created in code turned into compiled bindings, the
LSP client off StreamJsonRpc's reflection-based dispatch (or proven AOT-safe), the Remote/DAP JSON converters, and the local language host:
an AOT publish compiles everything into one executable, so `cscode-server.dll` is not there and `cscode.languageHost = process` cannot start.
What it would buy: ≈ 25 ms off every launcher run (`--version`: AOT 21 ms against ReadyToRun 31-37 ms; `--list-sessions` in an empty home,
the launcher's whole path: 26 ms against 51-52 ms, load 15), on the resident show, which the composite already meets, and nothing on the spare
claim, whose time is the window's first composition frame and the first editor. The launcher path itself ran fine under NativeAOT in this
evaluation (`--version`, `--list-sessions`, planning an open and a quit): a NativeAOT *launcher* alone (a second, small executable without Avalonia) is the cheaper way to
that gain if it is ever wanted.

Also found by the AOT run and fixed: a session process whose start-up failed kept answering `hello` but never `show` or `quit` (both waited for a
window that was never attached), so the launcher waited 30 s and `--quit --all` could not end it. It now answers both at once (the reason
with the crash log's path; quit ends it) and releases its session's lock.

## Publishing (`build/Publish.props`, `tools/publish-app.sh`)

`tools/publish-app.sh [--rid RID] [--out DIR] [--artifacts DIR]` runs `dotnet publish src/CsCode.App -c Release -r linux-x64 --self-contained`
(no build servers, intermediate files in `.scratch/publish-artifacts/`, output in `.scratch/publish/linux-x64/`) and refuses an output in which
any native file refers to `/nix/store`. The install wave's `tools/install.sh` calls it and copies the output; nothing is installed by it.

* `build/Publish.props` (imported by the App; only for a build with a runtime identifier): self-contained, ReadyToRun, composite, English
  satellite resources only. The self-contained switch must also be on the command line: the App references the `cscode-server` executable, and
  a self-contained executable may only reference self-contained ones (NETSDK1150).
* `build/NuGetRuntimePacks.props` (imported by Publish.props and by `CsCode.Server.csproj`): on a Nix-provided SDK the bundled packs are patched
  for the Nix store (the apphost's ELF interpreter is `/nix/store/…-glibc/lib/ld-linux-x86-64.so.2`, the runtime's RUNPATH points into the store),
  so a self-contained app built from them runs only where that store path exists, on Nix's glibc, which does not see the system's
  libfontconfig/libX11. It points the SDK's pack root at a directory that does not exist, so restore downloads the official packs from nuget.org
  (runtime, apphost, targeting, crossgen2; ILCompiler for AOT) into the NuGet cache. `tools/fetch-publish-packs.sh` reproduces that download and
  checks each package's size, SHA-512 (as nuget.org's catalog publishes it) and SHA-256 against its pin.
* The publish runs with nothing but `env -i HOME=… XDG_RUNTIME_DIR=… CSCODE_HOME=…` (PublishedAppTests: no `dotnet`, no `PATH`, no
  `LD_LIBRARY_PATH`, no `DISPLAY`; headless session open, list, quit): every library comes from the system (glibc, libstdc++, libfontconfig,
  libICU, OpenSSL; libX11 and friends for a real window) or from the publish itself (libSkiaSharp, libHarfBuzzSharp, libonigwrap).

## Tests

* `tests/CsCode.Sessions.Tests` — schema round trip and migration, store atomicity and corruption recovery, hot-exit backups (changed / deleted
  file on disk, size bound, damaged metadata), runtime directory derivation (CSCODE_HOME isolation, socket path length, 0700), IPC (round trip,
  peer-credential rejection with a real `SO_PEERCRED`, test verbs refused, garbage frames, a slow request not blocking another connection,
  wedged-process timeout, stale sockets left by a dead process), file locks (released on kill -9 of the holder), the launcher decision table
  row by row, the launcher against in-process fake processes (concurrent launches, wedged owner, failed start, eviction, list, quit, background),
  autosave debouncing and the hidden-session policy with a manual clock, settings parsing, the serial lane, leak checks.
* `tests/CsCode.App.Tests/Background` — REAL processes of the built `cscode` with `--headless-test` in a private `CSCODE_HOME` under /tmp:
  spare claim → edit, fold, caret → close (hides) → trim → show (same tabs, caret, folds, unsaved edit) → kill -9 → relaunch restores from
  autosave + backup → `--quit --all` leaves no process or socket; latency budgets; cold start; max-resident eviction; concurrent launches;
  `--wait`; 30 s CPU of a hidden trimmed session; 200 hide/show cycles with flat memory; no process at all with the default settings and
  `--background`. Each test kills by PID whatever is left of its own home. `CSCODE_TEST_EXECUTABLE=<cscode>` runs them against another build.
* `PublishedAppTests` — publishes with `tools/publish-app.sh` (once per run, into `.scratch/publish-test/`; or takes `CSCODE_PUBLISHED_APP`),
  checks that no native file refers to `/nix/store` and that the composite image and the runtime are there, then runs the publish with `env -i`
  (HOME, XDG_RUNTIME_DIR, CSCODE_HOME only): `--version`, a headless session with a file at a position, `--list-sessions`, `--quit --all`, no
  crash log. A real-X11 variant (a private Xvfb, `DISPLAY` only) checks that libX11 comes from the system; it skips without an Xvfb.
* `SessionStartupFailureTests` — a session process (and a spare) whose start-up failed: show and claim fail at once with the reason, quit ends
  it, the session's lock is released.
* `StartupBenchmarkTests` — the start-up measurements above (opt-in, `CSCODE_STARTUP_BENCH`).

```
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.Sessions.Tests
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~Background"
# the budgets against the shipped build (tools/publish-app.sh first; it runs on the system libraries, so without the Nix library path):
env -u LD_LIBRARY_PATH CSCODE_TEST_EXECUTABLE=$PWD/.scratch/publish/linux-x64/cscode dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~Latency"
```

## Known gaps

* The warm-spare budget (500 ms) is met only by the ReadyToRun composite publish; the JIT builds (Debug, which the test suite runs, and Release)
  take ≈ 0.9 s: the compositor's first frame of the shown window and the first editor are not warmed ("Start-up performance"). The spare is off
  by default anyway.
* The real-X11 run of the publish (`PublishedAppTests`) skips on this machine today: the Xvfb the tests used is no longer in the Nix store, and no
  other is installed. The integrator's real-desktop check covers it.
* In a self-contained publish, `cscode.languageHost = process` (off by default) starts `dotnet cscode-server.dll`, and there is no `dotnet` next
  to the app (Remote's `LocalServer.FindDotnetHost` falls back to `dotnet` on PATH): it should run the `cscode-server` apphost next to the app.
* The automated tests use the headless platform. The real-X11 path (show/hide, animation-frame paint wait, bounds) was exercised by hand on a
  private Xvfb with screenshots, not by a test; the tray icon was not exercised at all (no StatusNotifier host on Xvfb).
* Only the primary workspace folder is restored (`IWorkspaceService` has no multi-root add in Core).
* The Welcome page lists the other saved sessions (most recently used first, eight of them, then *More sessions…*) and Quick Launch has a
  `session:` mode (fuzzy over the name and the folder's name); both open a session through *Open Session…*, which takes the session's name as
  an argument (`cscode.sessions.open "work"`; no argument shows the picker). docs/SHELL.md, *Palette / Quick Launch*. A window run with
  `--foreground` has no background mode to open a session in: the Welcome page shows no Sessions list there and `session:` says why. The
  name argument was tested through the shell with a stand-in command, not in a real session process.
* Hot-exit restore reopens the file and replaces its text with one edit (no `IEditorViewFactory.OpenDocumentFromBackupAsync` was added): the
  restored text is dirty as it should be, and one Undo goes back to the disk version.
* Idle language-server stop/restart is wired to `ILanguageServerManager.StopAsync/StartAsync` but no test ran it with a real server.
* `--background` with `restoreOnLogin` and the launcher's `--quit NAME` are tested against in-process fake processes only.
* `--quit` of a spare that is still building its window waits until the window exists (seconds under load) before it exits.
* Windows (named pipes, Run key) and macOS (LOCAL_PEERCRED, LaunchAgent) code paths compile but are untested.
* Untitled buffers come back at the end of their group's tabs (their original position is not kept).
