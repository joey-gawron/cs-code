# Module brief — Integrated terminal — wave 3 (needs the merged shell, clipboard and sessions)

Read `docs/briefs/00-common.md` first (rules, threading, memory). The reference for behaviour and keys is VS Code's integrated terminal
(`workbench.action.terminal.*`, `terminal.integrated.*` settings); the window chrome follows Visual Studio 2022 (a **Terminal** tool window in the
bottom well, next to Output and the Error List).

## State today (main after the five-builder merge, 2026-10-29)
* The menu bar has **Terminal ▸ New Terminal / Toggle Terminal** (`workbench.action.terminal.new`, `KnownCommands.ToggleTerminal` =
  `workbench.action.terminal.toggleTerminal`); both stay disabled because nothing registers them (`ShellCommands` leaves them to this module).
* The Explorer's right-click menu has **Open in Terminal** (`workbench.action.terminal.newHere`, folder argument), shown only once that command is registered.
* `ContextKeyTracker` already has `FocusArea.Terminal` and sets `terminalFocus` / `panelFocus`; a view joins by being marked with `ShellFocus` as the terminal region.
* Themes already carry `terminal.background`, `terminal.foreground` and the 16 `terminal.ansi*` colours (`WorkbenchColors`).
* The debugger answers DAP `runInTerminal` without a terminal (`src/CsCode.Dap/Session/DebuggeeLauncher.cs` spawns the program and pipes it into the
  Debug Console); js-debug's `console: integratedTerminal` runs in the Debug Console for the same reason (`JsDebugAdapterFactory`).
* Remote editing (R1–R2) is merged; a remote terminal is R3 and needs this module (see `docs/briefs/remote.md`, `docs/REMOTE.md`).
* There is no PTY or terminal-emulator code and no NuGet package for one in the offline cache. **Downloads need the user's approval**: write it.

**Owns:** `src/CsCode.Terminal/**` (Avalonia-free: PTY hosts, VT parser, screen model, scrollback, terminal sessions, `ITerminalService`),
`src/CsCode.Panels.Terminal/**` (Avalonia: the Terminal tool window, the renderer control, input mapping), `tests/CsCode.Terminal.Tests/**`,
`tests/CsCode.Panels.Terminal.Tests/**`, `docs/TERMINAL.md`. Additive Core contract under `src/CsCode.Core/Terminal/`. Integration edits (list each in the
report): `CsCode.slnx`, `src/CsCode.App/AppServices.cs` (one `AddCsCodeTerminal()` line), the Dap `runInTerminal` hook (an additive seam, see C3).

## A. Core and process layer (MUST)
1. **Contract** `ITerminalService` (additive Core): create a terminal (shell/profile, args, cwd, env, name, `IsTransient` for task/debug terminals),
   list, get the active one, show/focus, `SendTextAsync(text, addNewLine)`, kill, events (created, exited with code, title changed, active changed).
   Process-free consumers (Explorer, Dap, tasks later, remote later) use only this.
2. **PTY host** behind an interface (`IPtyHost`: spawn, write, resize, kill, exit code, output stream) so the remote server can host one later (R3):
   * **Unix**: `posix_openpt` / `grantpt` / `unlockpt` / `ptsname`, then `posix_spawn` with `POSIX_SPAWN_SETSID` and file actions that open the
     slave as fd 0 (it becomes the controlling terminal) and dup it to 1 and 2. **Never `fork()` from managed code.** Window size through
     `TIOCSWINSZ` (the kernel sends SIGWINCH). The master fd is non-blocking-safe: a dedicated reader thread per terminal, a bounded write queue
     and a writer thread, so a full PTY buffer (a shell that stopped reading) never blocks the UI or the caller.
   * **Windows**: ConPTY (`CreatePseudoConsole`) behind the same interface; code it, mark it untested in the docs.
   * Child processes: tracked, below-normal priority is **not** applied to the shell (it is interactive), but the terminal's own reader/parser
     threads are not on the UI lane. Killing a terminal kills the process group (SIGHUP, then SIGKILL after a grace period); disposing the service kills
     every terminal; a test proves no shell, child or `sleep` survives.
3. **Shell profiles**: `terminal.integrated.defaultProfile.linux` / `.osx` / `.windows`, `terminal.integrated.profiles.*` (VS Code's shape: `path`,
   `args`, `env`, `icon`), detected defaults (`$SHELL`, else bash, zsh, fish, pwsh, sh), `terminal.integrated.cwd` (default: the workspace folder, else
   home), `terminal.integrated.env.linux` / `.osx` / `.windows` (null removes a variable), `TERM=xterm-256color`, `COLORTERM=truecolor`,
   `TERM_PROGRAM=cscode`. Login-shell behaviour follows VS Code (`-l` only where VS Code uses it).

## B. Emulator (MUST)
1. **Parser**: a VT500-style state machine (Paul Williams' DEC ANSI parser), UTF-8 with replacement for bad bytes, C0/C1 controls, ESC, CSI, OSC,
   DCS (consumed and ignored unless listed), with bounded parameter and string lengths (a hostile program cannot make it allocate without limit).
2. **Screen model**: a grid of cells (code point or grapheme cluster, width 1 or 2 with East Asian width and emoji, attributes), a bounded
   scrollback ring (`terminal.integrated.scrollback`, default 1000 lines, plus a hard memory cap), primary and alternate screens, cursor and saved
   cursor (DECSC/DECRC), scroll regions (DECSTBM), tab stops, origin mode, auto-wrap (DECAWM) with the pending-wrap state, insert/replace mode,
   DEC Special Graphics line drawing, reverse/dim/bold/italic/underline (single, double, curly, coloured)/strikethrough/invisible/blink, 16, 256 and
   24-bit colours.
3. **Sequences** (xterm's set as xterm.js supports it): cursor movement (CUU/CUD/CUF/CUB/CNL/CPL/CHA/CUP/HVP/VPA/HPA), erase (ED/EL/ECH incl. 3 J),
   insert/delete (ICH/DCH/IL/DL), scroll (SU/SD, IND/RI/NEL), SGR, DECSET/DECRST (1 cursor keys, 6 origin, 7 wrap, 12/25 cursor, 47/1047/1048/1049 alt screen,
   1000/1002/1003/1006 mouse, 1004 focus, 2004 bracketed paste, 2026 synchronized output), DECSCUSR cursor shapes, DSR/CPR, DA1/DA2 answers, window
   title OSC 0/2, OSC 7 cwd, OSC 8 hyperlinks, OSC 10/11/12 colour queries (answer from the theme), OSC 52 clipboard **write** only (setting
   `terminal.integrated.allowOsc52`, off by default), RIS/DECSTR resets, bell.
4. **Resize**: the grid resizes with the view and the PTY gets the new size; lines re-flow on width changes (`terminal.integrated.reflow`
   — SHOULD; when not done, truncate as xterm does and say so).
5. **Differential tests (real beats mock)**: feed identical byte streams to this emulator and to a reference and compare the screen (characters,
   attributes, cursor, alternate screen):
   * **tmux** (`/usr/bin/tmux`): always with a private socket (`-S <temp>/sock`), `-f /dev/null`, a fixed size, `capture-pane -p -e -N`, and kill
     only that server (by its socket / PID). **Never touch the user's tmux server or its default socket.**
   * **libvterm** (Neovim's, in the Nix store `*-libvterm-neovim-*`): optional P/Invoke oracle for cell-exact attributes; skip with a reason when missing.
   Corpus: hand-written sequences per feature, captured real sessions (bash prompt, `ls --color`, `git log` through `less`, `nvim -u NONE -i NONE -n`
   editing a file, `top -b`/`top` for one refresh, Python REPL, a progress bar with `\r`), and a fuzz pass (random bytes must never throw or exceed the
   memory cap; both emulators must survive).

## C. Panel and integration (MUST)
1. **Terminal tool window** (bottom well, id `terminal`, VS name "Terminal"): a list of terminals (dropdown and/or tabs), New (`+`), Split (SHOULD), Kill,
   Clear, Rename, Maximize panel; title from OSC 0/2 or the process name; an exited terminal keeps its buffer and shows
   "[The terminal process exited with code N.]"; `terminal.integrated.confirmOnKill` / `confirmOnExit`.
2. **Renderer**: a custom control that draws only the visible rows from an immutable screen snapshot, one snapshot per frame at most (coalesced; a
   flood of output never queues more than one pending frame), row-level dirty tracking, glyph runs per attribute run, the editor's font by default
   (`terminal.integrated.fontFamily`, `fontSize`, `lineHeight`, `letterSpacing`), bold-as-bright option, minimum contrast (SHOULD), cursor styles
   and blink (`terminal.integrated.cursorStyle`, `cursorBlinking`), theme colours live on theme switch, IME composition at the cursor, accessible
   text for screen readers (SHOULD).
3. **Input**: keys to xterm sequences (cursor keys in normal/application mode, modifiers, function keys, Alt as Meta, Ctrl+letter), bracketed paste,
   mouse reporting when the program asks for it (Shift bypasses it for selection), focus reports. **Keybinding precedence** follows VS Code: while the
   terminal has focus, keys go to the shell except commands in `terminal.integrated.commandsToSkipShell` (default list as VS Code's: palette, quick
   open, toggle terminal, focus editor, debug F-keys...) and `terminal.integrated.sendKeybindingsToShell` (false). Vim emulation never applies inside the
   terminal.
4. **Selection and clipboard** through the merged `IClipboardService`: drag, double-click word (`terminal.integrated.wordSeparators`), triple-click line,
   Alt+drag column (SHOULD), Ctrl+Shift+C / Ctrl+Shift+V (and Ctrl+Insert / Shift+Insert), copy on selection (`terminal.integrated.copyOnSelection`,
   off), right-click menu (Copy, Paste, Select All, Clear, Kill Terminal, Split, Rename) in the clipboard branch's menu style, PRIMARY on Linux
   (selection sets it, middle-click pastes), multi-line paste warning (`terminal.integrated.enableMultiLinePasteWarning`).
5. **Scrolling**: wheel, Shift+PgUp/PgDn, Ctrl+Home/End, scrollbar, stays at the bottom while following output, keeps the position while the user
   reads the scrollback; Find in terminal (Ctrl+F while focused; SHOULD).
6. **Links**: URLs (open with the OS opener after `IWindowService` confirmation if not http/https), OSC 8 hyperlinks, and file paths with optional
   `:line[:col]` resolved against the terminal's cwd (OSC 7) or the workspace → open in the editor through `INavigationService`. Ctrl+click.
7. **Commands and keys** (VS Code ids and default keys): `workbench.action.terminal.new` (Ctrl+Shift+\`), `.toggleTerminal` (Ctrl+\`), `.newHere`,
   `.kill`, `.clear`, `.focus`, `.focusNext` / `.focusPrevious`, `.split` (SHOULD), `.rename`, `.selectAll`, `.copySelection`, `.paste`,
   `.scrollUp` / `.scrollDown` / `.scrollToTop` / `.scrollToBottom`, `.runSelectedText` (sends the editor's selection), `.runActiveFile`,
   `.sendSequence` (args `{ "text": ... }`). Status: the terminal count in the tool window title is enough.
8. **Debugger**: DAP `runInTerminal` with `kind: integrated` runs the program in a new terminal (named after the launch configuration, reused for the next
   run when idle, as VS Code does) through an additive seam the Dap module calls when a terminal service is registered (keep today's
   `DebuggeeLauncher` as the fallback; `kind: external` keeps its current behaviour). js-debug's `console: integratedTerminal` then really uses the
   terminal; update its comment and docs. The debuggee's process id comes back from the shell-less spawn (no shell in between) as VS Code does.
9. **Sessions / background mode**: terminals are not restored after a restart (say so in the docs); in a hidden session, terminals keep running with
   rendering stopped and no periodic UI work (≈0 % CPU idle — follow the sessions module's `SetHidden` pattern).

## D. SHOULD / STRETCH
* SHOULD: split terminals, Find, reflow, shell integration (OSC 633 / 133: command decorations, exit-code markers, `Ctrl+Up/Down` to jump between
  commands, cwd tracking), tasks running in terminals (Dap's task runner gains a terminal presentation), `terminal.integrated.persistentSessionReviveProcess`
  (not reviving: say so).
* STRETCH: the remote terminal (R3) through the server using `IPtyHost` over the remote protocol; image protocols (sixel/kitty): no.

## Threading, memory, budgets (binding)
* PTY read on a dedicated thread per terminal; parsing and screen mutation on that terminal's own worker (never the UI thread, never the thread pool's
  hot path for a flood); the UI receives at most one immutable snapshot per frame (rows reused when unchanged).
* **Flood**: `cat` of a 100 MB file / `yes` for 10 s with the terminal on screen: dispatcher p99 < 16 ms, max < 50 ms (use the Profiling
  `ResponsivenessProbe` like the Debug Console tests), typing in an editor at the same time stays within the editor's typing budget, memory stays
  under the scrollback cap (+ a small constant), and Ctrl+C reaches the program within 100 ms.
* 50 terminals created and killed: no process, thread, fd (`/proc/self/fd`) or view retained (`LeakAssert`).
* Keystroke to PTY write < 1 ms on the UI thread; echo-to-screen p99 < one frame plus the shell's own latency.

## Tests (all headless; never open a window on the user's display)
Run shells as `bash --noprofile --norc` (and `sh`) with `HOME`, `XDG_CONFIG_HOME` and `ZDOTDIR` in temp dirs: tests never run the user's rc files.
Unit (parser states, each sequence, wide chars, scroll regions, alt screen, bounded strings, fuzz), differential (tmux and libvterm as above), PTY (spawn,
echo, resize → `stty size`, SIGINT on Ctrl+C, exit codes, process-group kill, a shell that stops reading does not block writes), panel (headless Avalonia:
render rows, selection + copy, paste with bracketed paste, link activation opens the editor, keybinding precedence, theme switch), DAP (`runInTerminal`
with a real adapter: netcoredbg or js-debug with `console: integratedTerminal`), flood and leak budgets above, and a real-app test through the menu
and Ctrl+\` in the real composition. Screenshots of the terminal running `ls --color`, `nvim -u NONE -i NONE -n` and `git log` for the user (headless,
git-ignored).

## Done when
MUST items work and are tested, `docs/TERMINAL.md` describes design, settings, commands, limits and how to run the differential tests, the build is clean,
and the report lists every file outside the owned directories.
