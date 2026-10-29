# Background mode and saved sessions

CsCode can stay running in the background so a window with a saved session comes back at once ("hot load"). Brief:
`docs/briefs/background-sessions.md`. Code: `src/CsCode.Sessions/` (Avalonia-free: model, store, hot-exit backups, IPC, locks, launcher
decision logic), `src/CsCode.App/Background/` (the window side), `src/CsCode.App/Program.cs` (the three roles of the executable),
`tools/autostart/`. Tests: `tests/CsCode.Sessions.Tests/` (unit, in-process IPC) and `tests/CsCode.App.Tests/Background/` (real processes).

## Architecture: one process per session, plus a warm spare

```
 cscode (CLI / desktop entry / tray) ──local IPC──► session process "work"  (window shown or hidden)
                                     ──local IPC──► session process "oss"   (hidden, trimmed)
                                     ──local IPC──► warm spare              (no session; claimed on demand, then replaced)
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
cscode --background               start the warm spare (+ resident sessions, hidden, with restoreOnLogin); no window
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
| Not running, a warm spare answers | **Claim** the spare (it restores the session hidden, then the show makes it visible); start a new spare |
| Not running, no spare (or the claim fails) | **Start** a session process, wait for its hello, then show; start a spare if none exists or is starting |
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

## The warm spare (`cscode.background.warmSpare`, default true)

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
| `cscode.background.warmSpare` | `true` | Keep a pre-started process |
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

## Latency (invocation → painted)

Measured 2026-10-29 on this machine (22 cores) while several other agents were building (load average 6–24), from `Process.Start` of the
launcher to its exit (the launcher exits when the window reported a painted frame: a captured frame on the headless platform, two animation
frames on a real one):

| Path | Release, headless, load ≈ 7 | Release, real X11 (private Xvfb), load ≈ 23 | Debug (the test suite), load 12–24 | Budget |
|---|---|---|---|---|
| `cscode --version` (runtime start-up floor) | 45–90 ms | — | 115–125 ms | — |
| Resident session, hidden → shown | **148 ms** (213–310 ms at load 12–21) | 417–568 ms | 250 ms – 1.4 s | < 150 ms |
| Via the warm spare (claim + restore + first frame) | ~1.0 s | 1.3 s | 1.4–5 s | < 500 ms |
| Cold start (no spare) | 1.6–2.3 s | 2.4 s (spare claimed while still warming) | 3–8 s | report |

Where the time goes (`CSCODE_SESSION_TRACE`, Release, load ≈ 7): launcher process start → `Main` ≈ 50 ms; paths and settings ≈ 60 ms;
discovery (one `hello` per process) ≈ 40–80 ms; the show round trip of a shown window ≈ 5–10 ms, of a hidden one 20–60 ms on X11 (100–400 ms on
the headless platform, whose captured frame is a full software render). For the spare: claim + restore ≈ 50–80 ms, first `Show` 110–330 ms,
**first composition frame 0.5–1.4 s** (JIT of the compositor and the first render of the whole window). So the resident budget holds only for a
Release build on a quiet machine, and the warm-spare budget is missed (see "Known gaps"). A real-window run on a private Xvfb (`:137`) showed the
restored window (file at the saved position, title `A — ws`, the session in the status bar) after hide/show cycles through the CLI.

Set `CSCODE_SESSION_TRACE=/path/file` to get a per-phase timeline from the launcher and every CsCode process.

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
  `--wait`; 30 s CPU of a hidden trimmed session; 200 hide/show cycles with flat memory. Each test kills by PID whatever is left of its own home.

```
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.Sessions.Tests
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~Background"
```

## Known gaps

* Warm-spare open misses its 500 ms budget (≈1 s, Release): the compositor's first frame of the real window is not warmed (it would need the
  window shown once, which a spare at login must not do on a real desktop). The resident budget (150 ms) holds only for a Release build on a
  quiet machine; the launcher is a fresh .NET process (≈50 ms runtime start before `Main`), and ReadyToRun/Native AOT would need packages that are
  not in the offline cache.
* The automated tests use the headless platform. The real-X11 path (show/hide, animation-frame paint wait, bounds) was exercised by hand on a
  private Xvfb with screenshots, not by a test; the tray icon was not exercised at all (no StatusNotifier host on Xvfb).
* Only the primary workspace folder is restored (`IWorkspaceService` has no multi-root add in Core).
* The Welcome page does not list saved sessions yet (Shell's `StartPages.cs`), and Quick Launch has no `session:` mode; use *Switch Session…*
  (Ctrl+Alt+S, also in the command palette), *Open Session…* or `cscode --list-sessions`.
* Hot-exit restore reopens the file and replaces its text with one edit (no `IEditorViewFactory.OpenDocumentFromBackupAsync` was added): the
  restored text is dirty as it should be, and one Undo goes back to the disk version.
* Idle language-server stop/restart is wired to `ILanguageServerManager.StopAsync/StartAsync` but no test ran it with a real server.
* `--background` with `restoreOnLogin` and the launcher's `--quit NAME` are tested against in-process fake processes only.
* `--quit` of a spare that is still building its window waits until the window exists (seconds under load) before it exits.
* Windows (named pipes, Run key) and macOS (LOCAL_PEERCRED, LaunchAgent) code paths compile but are untested.
* Untitled buffers come back at the end of their group's tabs (their original position is not kept).
