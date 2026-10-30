# Handoff: work parked on 2026-10-30 (Linux desktop → MacBook)

Work was parked mid-stream so it can continue on another machine. Delete this folder (`docs/handoff/`) once it has been picked up.

**Commit rule:** no `Co-Authored-By` lines and no AI attribution of any kind in commit messages (the owner's rule).

## `main` (this commit)

`main` holds today's desktop fixes, each one reviewed by an independent reviewer (up to three rounds) and merged:

* **Code-action light bulb** (`CodeActionFeature.PositionBulb`): placed from what is on screen, so it stays right of the breakpoint margin when
  a long line is scrolled sideways, never covers a collapsed fold's header or sits outside the view, and shows on the last row.
* **View > Tool Windows**: each item shows, activates and puts the keyboard in its window's content: the terminal, Find in Files' query, the
  Debug Console's input, a row of the debug lists and the Explorer. With only toolbar chrome left, the window itself gets the keyboard. Each
  item is labelled with the tool's shortcut again. The Explorer keeps the keyboard on its rows through expand and collapse
  (`ToolWindowFocus.KeepOnRows`), but not when another app window (a floated tool) activates.
* **Launcher** (`LaunchPlanner`): the last active session is passed over only once its lock is free. `--quit` without a name falls back only
  to a shown window and prints which one. `--quit NAME` rejects an invalid name.
* **Memory monitor**: the 3-minute steady-growth grace restarts on session restore (warm spare claim) and folder changes. No clock overflow.
* **Shared log file lock** (`FileLoggerProvider`): on contention, a batch waits instead of writing unlocked. Other lock errors give up at
  once. Dispose is bounded (1 s). The Remote test harness reads only `*.log`.
* **Title bar**: the children and the Tab and screen-reader order run logo, menu, centre, window controls. The controls are not Tab stops,
  are named (Minimize, Maximize/Restore Down, Close) and keep 78 px clear for macOS traffic lights outside full screen. **The 78 px is an
  estimate: check it on the Mac.**
* **Test**: the task terminal completion test is deterministic.

The targeted tests passed on Linux after the merge (build: 0 warnings):

| Tests | Result |
| --- | --- |
| Sessions | 133 |
| FileLogger | 23 |
| Editor bulb/folding | 58 |
| Memory monitor | 37 |
| TaskTerminalHost | 11 |
| Panels.Search | 377 |
| Panels.Debug | 188 (3 skipped) |
| App menus/Explorer/title bar/tool windows | 133 |

**The full suite has not run on this `main`.** The last full run (at 2177841) failed only the known load-sensitive tests
(`known-issues.md`).

## Branch `watcher-inotify`: unfinished, not on `main`

The branch replaces FileSystemWatcher on Linux with a direct inotify backend (`src/CsCode.Platform/Watching/InotifyBackend.cs`). One real
window held 75,892 of the machine's 90,112 inotify watches, 56k of them in Cargo `target/`. The backend watches one directory at a time and
skips directories every subscription excludes. Cache directories (`CACHEDIR.TAG`) are pruned; a uv `.venv` is exempt. A subscription can ask
for excluded paths it needs (`FileWatchWants`: Explorer-expanded folders, language-server RelativePattern bases), and wants travel to
cscode-server.

It is based on `main`'s previous tip (5684650) and merges cleanly with this `main`. Its commits:

* e8a28bc: the backend.
* d8cff00: fixes for 16 confirmed review findings.
* b3f5021: fixes for the second review's 20 confirmed findings (about 14 distinct), listed in `docs/handoff/watcher-review-round3.txt` on
  that branch.

The agent was stopped while it was checking that each of b3f5021's new tests fails without its fix. On Linux, at b3f5021:

| Check | Result |
| --- | --- |
| Build | 0 warnings |
| Platform tests | 1103 |
| Remote watch/loopback | 19 |
| Lsp DynamicRegistration | 15 |
| App Explorer/WorkspaceSettingsWatch | 27 |

To finish it:
1. Review b3f5021 against `watcher-review-round3.txt` (each finding really fixed, nothing new broken). The worst one: after an inotify
   overflow, the resync could loop forever while holding the backend lock.
2. Do the revert checks: each behaviour test must fail without its fix.
3. Run the full suite **on Linux**. The inotify backend and its tests are Linux-only, and macOS uses `FswBackend`.
4. Merge into `main` and delete `watcher-review-round3.txt`.

## Not started (a follow-up workflow was stopped before it committed anything)

* Error List and Find Results lose the keyboard to nothing whenever their rows are replaced: a diagnostics update swaps the Error List's
  ItemsSource, and Find Results uses ReplaceAll/Clear. Apply `ToolWindowFocus.KeepOnRows` or equivalent, each with a failing-first test.
  Check the other row lists too; the debug views use `ListFocusKeeper`.
* View > Tool Windows > Explorer with no folder open puts the keyboard in the filter box. It should go to the Open Folder button
  (`ExplorerView.FocusPreferred`).
* Earlier follow-ups:
  * `_NET_WM_ICON` is set at one size only.
  * A new window opens exactly on top of an existing one.
  * The warm spare misses its latency budget (T46).
  * See also `followups.md`.

## Then, on the Linux desktop

* Reinstall: `bash .scratch/notes/with-nix-env.sh tools/install.sh`, or on another machine see the README. Restart running windows with
  `cscode --quit --all`, since they keep the old version until they exit.
* Desktop checks that need a person: `desktop-checks.md`.
* Open decisions (each already built one way): `decisions.md`.

## On the MacBook

* Build with a normal .NET 10 SDK: `dotnet build CsCode.slnx`. `.scratch/` is git-ignored, so the Nix wrapper `with-nix-env.sh` is Linux-only
  and not in the repo anyway.
* Tree-sitter grammars live under `artifacts/` (ignored) and are per platform: `tools/treesitter/build-grammars.sh`.
* `macos-audit.md` lists what is known to be broken or degraded on macOS. It was a read-only audit and nothing has run on a Mac yet. The
  biggest items:
  * no Rust/C debugger on Apple Silicon (needs lldb-dap);
  * no login-shell PATH for apps started from Finder or the Dock;
  * traffic lights (partly addressed by the 78 px inset above);
  * Ctrl-based keymaps.
