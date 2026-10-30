# Integrated terminal (`CsCode.Terminal` + `CsCode.Panels.Terminal`)

VS Code's integrated terminal (`workbench.action.terminal.*`, `terminal.integrated.*`) in a Visual Studio 2022 **Terminal** tool window in the
bottom well, next to Output and the Error List. A real pseudo-terminal per terminal, an own VT emulator verified against tmux and libvterm, a
custom renderer, and `runInTerminal` for the debugger.

```
 ITerminalService (Core, src/CsCode.Core/Terminal)  ◄── Explorer "Open in Terminal" (command), Dap runInTerminal (seam), tasks/remote later
        │
 CsCode.Terminal (no Avalonia)                          CsCode.Panels.Terminal (Avalonia)
   Pty/   IPtyHost: UnixPtyHost (posix_spawn),            TerminalToolWindow (id "terminal", Bottom)
          ConPtyHost (Windows, untested)                  TerminalPanelRuntime: commands, keys, key precedence, focus key,
   Emulator/ VtParser, VtEmulator, ScreenBuffer,                               clipboard, links, theme palette
          Scrollback, TerminalRow, TerminalSnapshot       TerminalView: dropdown, New/Kill/Clear/Maximize, scrollbar
   Sessions/ TerminalSession (worker), TerminalService,   TerminalControl: renderer, keys/text/IME, mouse, selection
          TerminalLaunchResolver (profiles/env/cwd)       Rendering/ TerminalFont, TerminalPainter
```

Composition: one line in `AppServices.Build` — `services.AddCsCodeTerminal()` (it calls `AddCsCodeTerminalService()` of the core library). The
tool window's toggle command is `workbench.action.terminal.toggleTerminal` (the Terminal menu and Ctrl+\` already bind it).

## Process layer (A2)

* **Unix** (`UnixPtyHost`): `posix_openpt` / `grantpt` / `unlockpt` / `ptsname_r`, then `posix_spawn` with `POSIX_SPAWN_SETSID` and file
  actions that open the slave as fd 0 (a session leader opening a tty without `O_NOCTTY` acquires it as its controlling terminal) and dup it to
  1 and 2, `closefrom(3)`, every signal back to its default and an empty mask (the .NET runtime ignores SIGPIPE; a shell must not inherit that),
  `chdir` as a file action. **No `fork()` from managed code.** Window size through `TIOCSWINSZ` (set before the child starts; on resize the kernel
  sends SIGWINCH).
* **Threads per terminal** (dedicated, background, never the pool or the UI): a *reader* polls the non-blocking master, a pidfd (`pidfd_open`;
  a `waitpid` thread on older libcs) and a wake pipe, and reaps the child itself (no zombie, also after dispose); a *writer* drains the bounded
  write queue (16 MB) into the master and waits for POLLOUT when the program does not read — a full PTY never blocks the UI or the caller; when
  the queue is full a write is refused, except an *urgent* one (Ctrl+C, Ctrl+\, Ctrl+Z jump the queue); the terminal's *worker* parses.
* **Kill**: SIGHUP (+ SIGCONT) to the process group, then after a grace period (2 s; 1 s for a debuggee; 0.5 s on dispose) SIGKILL to every
  process still in the terminal's session (`/proc/*/stat`), so background jobs in their own process groups go too. A program that called
  `setsid` itself (a daemon) is deliberately left alone. Disposing the service kills every terminal.
* **Windows** (`ConPtyHost`): `CreatePseudoConsole` over two pipes, the process in a kill-on-close job object, same thread layout. **Written,
  never run** (development and tests are Linux only). macOS: written for, untested; known problems below.
* The shell is tracked with `IMemoryMonitor.TrackChildProcess` (the shell's exit path kills anything left) and keeps normal priority (it is
  interactive).

### Profiles, environment, working directory (A3)

`TerminalLaunchResolver`, VS Code's rules: detected profiles `bash`, `zsh`, `fish`, `pwsh`, `sh`, `tmux` (on PATH) overlaid with
`terminal.integrated.profiles.<os>` (`path` — a string or a list —, `args`, `env`, `icon`; `null` removes a detected one). Default profile:
`terminal.integrated.defaultProfile.<os>`, else `$SHELL`, else bash, zsh, fish, pwsh, sh. Login shells (`-l`) only on macOS, as VS Code's
default profiles. Environment: CsCode's (`terminal.integrated.inheritEnv`), `TERM=xterm-256color`, `COLORTERM=truecolor`, `TERM_PROGRAM=cscode`,
`TERM_PROGRAM_VERSION`, `LANG=C.UTF-8` when no locale is set, then the profile's `env`, `terminal.integrated.env.<os>`, the caller's; a `null`
value removes a variable; `${env:NAME}`, `${workspaceFolder}`, `${userHome}` are expanded. Working directory: the caller's, else
`terminal.integrated.cwd` (`~` and `${workspaceFolder}` expanded, relative to the workspace folder), else the workspace folder, else home; a file
means its folder ("Open in Terminal" on a file). A program that cannot be found makes a terminal that shows why and counts as exited (code -1).
Under `tools/nix-dev-env.sh` the terminal inherits that script's `LD_LIBRARY_PATH` (Nix libraries) like any other variable.

## Emulator (B1-B4)

* **Parser** (`VtParser`): Paul Williams' VT500 state machine as xterm.js runs it — UTF-8 decoded first (U+FFFD for every maximal invalid
  subpart), decoded C1 code points act as 8-bit controls (the UTF-8 encoded ST, C2 9C, also ends a string), CSI with `:` sub-parameters,
  ESC, OSC (BEL or ST), DCS/SOS/PM/APC consumed. Bounded: 32 parameters (values clamped at 65535), 2 intermediates (a third makes the sequence
  ignored), OSC payloads up to 1 MB and DCS data up to 64 KB (longer strings are consumed and dropped; a big buffer is released afterwards).
  It never throws.
* **Screen** (`VtEmulator` over two `ScreenBuffer`s): cells hold a code point or a grapheme cluster (combining marks, ZWJ emoji sequences; a
  cluster is capped at 32 UTF-16 units), width 0/1/2 (Unicode 15 East Asian Wide/Fullwidth and emoji presentation; marks and format characters
  join the previous cell), rendition (bold, dim, italic, underline single/double/curly/dotted/dashed with colour, blink, inverse, invisible,
  strikethrough, overline; default/16/256/24-bit colours) and an OSC 8 link (row-local table). Primary screen with scrollback, alternate
  screen without; cursor, DECSC/DECRC (position, rendition, charsets, origin mode, pending wrap), DECSTBM, tab stops, origin mode, DECAWM with
  the pending-wrap state, IRM, DEC Special Graphics and UK sets (G0-G3, SI/SO, SS2/SS3, LS2/LS3), erase with the current background (BCE).
* **Sequences**: CUU/CUD/CUF/CUB/CNL/CPL/CHA/HPA/CUP/HVP/VPA/HPR/VPR(e/a), ED 0-3, EL, ECH, ICH, DCH, IL, DL, SU, SD, IND, RI, NEL, HTS, TBC,
  CHT, CBT, REP (bounded to one screen), DECALN, SGR (`;` and `:` forms incl. `38:2::r:g:b`), DECSET/DECRST 1, 5, 6, 7, 9, 12, 25, 47, 66,
  1000, 1002, 1003, 1004, 1005, 1006, 1007, 1015, 1047, 1048, 1049, 2004, 2026; SM/RM 4, 20; DECSCUSR; DSR 5/6, DECXCPR; DA1 (`CSI ?1;2c`),
  DA2 (`CSI >0;276;0c`), XTVERSION, DECRQM (ANSI and DEC), `CSI 18 t`, title stack 22/23 t; OSC 0/1/2 (titles, control characters stripped,
  1024 characters), 4 (palette set/query), 7 (cwd), 8 (hyperlinks), 10/11/12 queries answered from the theme (setting them is ignored),
  52 clipboard **write only** and only with `terminal.integrated.allowOsc52` (off by default), 104/110/111/112, 133/633 shell-integration
  marks (below); RIS, DECSTR; BEL (an event). DCS (DECRQSS, XTGETTCAP, sixel) is consumed without an answer.
* **Shell integration** (SHOULD, partial): when the shell reports OSC 633 (VS Code's) or 133 (FinalTerm's) marks, the prompt row (`A`) is
  marked and keeps its command's exit code (`D;code`, attributed to the nearest prompt above; a shell that reports only `B`/`C` gets it on that
  row), `P;Cwd=` sets the cwd like OSC 7. The view draws a mark left of each prompt (`terminalCommandDecoration.defaultBackground` /
  `successBackground` / `errorBackground`) and Ctrl+Up / Ctrl+Down bring the previous/next prompt to the top of the view. CsCode does **not**
  inject integration scripts into the shell (VS Code does): the marks appear only for a shell configured to print them.
* **Scrollback**: `terminal.integrated.scrollback` lines (1000) and a hard cap of 32 MB, whichever comes first. Rows have stable absolute
  numbers (selections and the viewport do not move while old rows fall off). Scrolling is allocation-free: the ring keeps the line objects
  themselves and the line that falls off the front is cleared and reused as the new bottom line; immutable `TerminalRow` copies are made only
  for rows somebody reads (a frame's visible rows, a selection). (The first version copied every scrolled line: 2.8 GB of garbage per 10 s of
  `yes` and GC pauses on every thread; now ~0.1 GB, and 4x the throughput.)
* **Resize** (B4): rows as xterm.js (shrinking drops blank rows below the cursor first, then pushes rows into the scrollback; growing pulls them
  back); columns **re-flow** the primary screen and its scrollback (`terminal.integrated.reflow`, on by default): soft-wrapped lines are joined
  and split at the new width without cutting a wide character, the cursor follows its character. The alternate screen (and everything with
  reflow off) is truncated/padded as xterm does.

## Sessions and frames

`TerminalSession` (implements `ITerminal`): the reader copies output into a bounded queue (256 KB; a full queue makes the reader wait, so the
program is throttled by its full PTY), the worker parses and keeps the emulator — **nothing else touches it**; the UI uses `Post`/`InvokeAsync`
(selection text, scrolling, clear). **One snapshot per frame at most**: the view asks for a frame after drawing one (`RequestFrame`); the worker
builds the next `TerminalSnapshot` (visible rows; unchanged rows are the same `TerminalRow` instances, which is the renderer's row-level dirty
tracking) only when it changed and was asked for, and signals `FrameReady`. A flood therefore costs the UI one snapshot per rendered frame; a
terminal nobody shows (hidden tool window, inactive terminal, hidden session window) costs the UI nothing. Synchronized output (DECSET 2026)
holds frames until the program ends the update, for one second at most. Keystrokes go straight to the host's queue (no worker hop); typing
scrolls back to the bottom. On exit the terminal keeps its buffer and shows `[The terminal process exited with code N.]`. The title is the
user's rename, else the creator's name, else the program's (OSC 0/2), else the foreground process name (polled at most every 500 ms while
output flows).

## The Terminal tool window (C1-C7)

* **Window**: dropdown of terminals ("1: bash", "(exited N)"), New (+), Kill (trash), Clear, Maximize Panel Size (the bottom well's proportion,
  and back); the window title shows the count ("Terminal (3)"). Opening the window with no terminal creates one (VS Code). Hide (toggle) and
  maximize act on the Dock dockable that hosts the view — the same operations as its title-bar buttons (no shell API exists for them).
* **Renderer** (`TerminalPainter`): only visible rows; per row, runs of equal rendition become one background rectangle and glyph runs built
  without shaping (each glyph advances exactly one or two cells, as terminals draw; grapheme clusters are shaped on their own); built runs are
  cached per `TerminalRow` instance. Font: `terminal.integrated.fontFamily`, else the editor's, else Cascadia/Consolas/DejaVu/Liberation/Noto
  Mono (the first family the system really has: the font manager answers a missing family with its default face, so the returned face is
  checked), `fontSize` (else `editor.fontSize`), `lineHeight`, `letterSpacing`; system fallback per character (CJK, emoji) with a bounded cache.
  `drawBoldTextInBrightColors`, `minimumContrastRatio` (4.5), cursor block/underline/bar (`cursorStyle`, DECSCUSR wins), blinking
  (`cursorBlinking`, DECSET 12) only while focused in an active window, a hollow block when unfocused. Colours from the theme's `terminal.*` keys,
  read per frame, so a theme switch repaints at once. IME: the candidate window at the cursor, the preedit drawn there. Screen readers get the
  terminal's name (SHOULD: no text model yet).
* **Input** (C3): xterm encodings (`KeyEncoder`): cursor keys normal/application, `CSI 1;m X` modifiers, F1-F12, Alt as Meta, Ctrl+letter and
  Ctrl+punctuation, Backspace = DEL, Shift+Tab = `CSI Z`, application keypad; text and IME commits as UTF-8; bracketed paste (end markers inside
  the text are removed); mouse reports X10/1000/1002/1003 in default/UTF-8/SGR/urxvt encoding (`MouseEncoder`), Shift bypasses them; focus
  reports (1004); on the alternate screen the wheel sends cursor keys (xterm.js), elsewhere it scrolls three lines a notch.
* **Keybinding precedence** (VS Code's): while a terminal has focus a key goes to the shell, except a key that starts a chord (`allowChords`,
  default true) or runs a command in `terminal.integrated.commandsToSkipShell` (VS Code's default list for the commands CsCode has, every terminal
  command, plus the user's entries; `-id` removes one), unless `sendKeybindingsToShell` (false). Ctrl+Tab stays with the editor switcher. It is
  a tunnelling class handler on every top-level window, which runs before the main window's own keybinding handler — so the shell needs no
  change; the soft lookup reads `IKeybindingService.All` (precedence, `when` clauses, removals) without touching the chord state. Vim emulation
  is an editor layer and never sees terminal keys (to be rechecked once the Vim adapter is merged: it must not add a window-level key hook that
  runs first).
* **Focus key**: the main window classifies tool windows as `panelFocus`; the control sets `terminalFocus` itself on focus (again right after
  the window's own focus handler, which runs later and resets it) and clears it when it loses focus or leaves the window, which also gives
  keyboard focus up (Avalonia's focus machinery would otherwise keep a hidden view reachable). `terminalAltBufferActive` follows the screen.
* **Selection and clipboard** (C4): drag, double-click word (`wordSeparators`), triple-click line (a soft-wrapped line is one line), Alt+drag
  block, Select All; copy joins soft-wrapped rows without a line break and trims trailing blanks; Ctrl+Shift+C / Ctrl+Insert copy,
  Ctrl+Shift+V / Shift+Insert paste, `copyOnSelection` (off), PRIMARY on Linux (a finished selection owns it; the text is read once on the
  worker, so the provider only returns a string; middle-click pastes PRIMARY), the multi-line paste warning (`enableMultiLinePasteWarning`:
  `auto` warns only without bracketed paste; Paste / Paste as one line / Cancel), all through `IClipboardService`. The view is an
  `IClipboardTarget`, so Edit > Copy/Paste act on it. Right-click menu (the shared menu builder): Copy, Paste, Select All, Clear Terminal,
  Rename..., Kill Terminal (no Split: not implemented).
* **Scrolling** (C5): wheel, Shift+PgUp/PgDn, Ctrl+Shift+Up/Down (line), Ctrl+Home/End (all only on the primary screen), the scrollbar; the
  view follows the output at the bottom and keeps its place while the user reads the scrollback; typing returns to the bottom.
* **Links** (C6, Ctrl+click; underlined with a hand cursor while Ctrl is held): OSC 8 targets, URLs (any `scheme://`, `mailto:`, `file:`;
  trailing punctuation trimmed) — http/https open with the OS opener, other schemes after an `IWindowService` confirmation — and file paths
  (`path`, `path:line`, `path:line:col`, MSBuild's `path(line,col)`; absolute, `~/`, `./`, relative, or a bare `name.ext:line`) resolved off the
  UI thread against the terminal's cwd (OSC 7) and the workspace folders, opened in the editor through `INavigationService` at the position.

### Commands and keys (C7)

| Command | Default key | |
|---|---|---|
| `workbench.action.terminal.new` | Ctrl+Shift+\` | Create New Terminal (default profile) |
| `workbench.action.terminal.toggleTerminal` | Ctrl+\` (keymap presets) | hide when shown, else show, focus and create one if there is none |
| `workbench.action.terminal.newHere` | | Explorer's Open in Terminal (folder or file URI argument) |
| `workbench.action.terminal.focus` | | show and focus (creates one if needed) |
| `workbench.action.terminal.kill` | | kill the active terminal (`confirmOnKill`) |
| `workbench.action.terminal.clear` | | scrollback and screen go, the prompt line stays (xterm.js `clear()`) |
| `workbench.action.terminal.focusNext` / `focusPrevious` | Ctrl+PgDn / Ctrl+PgUp (terminal focus) | |
| `workbench.action.terminal.rename` | | input box, or a name argument; empty = automatic |
| `workbench.action.terminal.selectAll` | Ctrl+Shift+A (terminal focus) | |
| `workbench.action.terminal.copySelection` | Ctrl+Shift+C, Ctrl+Insert (terminal focus) | |
| `workbench.action.terminal.paste` | Ctrl+Shift+V, Shift+Insert (terminal focus) | |
| `workbench.action.terminal.scrollUp` / `scrollDown` | Ctrl+Shift+Up / Down | `when terminalFocus && !terminalAltBufferActive` |
| `workbench.action.terminal.scrollUpPage` / `scrollDownPage` | Shift+PgUp / PgDn | same |
| `workbench.action.terminal.scrollToTop` / `scrollToBottom` | Ctrl+Home / Ctrl+End | same |
| `workbench.action.terminal.scrollToPreviousCommand` / `scrollToNextCommand` | Ctrl+Up / Ctrl+Down | same; shell-integration prompts |
| `workbench.action.terminal.runSelectedText` | | the editor's selection, else its current line (or a text argument) |
| `workbench.action.terminal.runActiveFile` | | saves and sends the active file's quoted path |
| `workbench.action.terminal.sendSequence` | | `{ "text": "..." }` sent as is |

Differences from VS Code's Linux defaults (decisions for the user): Select All has no default key in VS Code on Linux — CsCode binds
Ctrl+Shift+A (terminal focus only); Ctrl+PgDn / Ctrl+PgUp for the next/previous terminal and Ctrl+Insert for copy are CsCode's additions (the
brief asks for Ctrl+Insert/Shift+Insert). Everything else matches VS Code. All of them are contributions, so `keybindings.json` can remove them.

### Settings

| Key | Default | |
|---|---|---|
| `terminal.integrated.defaultProfile.linux` / `.osx` / `.windows` | (detected) | |
| `terminal.integrated.profiles.linux` / `.osx` / `.windows` | (detected) | `path`, `args`, `env`, `icon`; `null` removes one |
| `terminal.integrated.env.linux` / `.osx` / `.windows` | | `null` removes a variable |
| `terminal.integrated.cwd` | "" | |
| `terminal.integrated.inheritEnv` | true | |
| `terminal.integrated.scrollback` | 1000 | plus the 32 MB cap |
| `terminal.integrated.reflow` | true | CsCode's |
| `terminal.integrated.allowOsc52` | false | CsCode's: OSC 52 writes only |
| `terminal.integrated.fontFamily` / `fontSize` / `lineHeight` / `letterSpacing` | "" / 0 / 1 / 0 | "" and 0: the editor's |
| `terminal.integrated.drawBoldTextInBrightColors` | true | |
| `terminal.integrated.minimumContrastRatio` | 4.5 | |
| `terminal.integrated.cursorStyle` / `cursorBlinking` | block / false | |
| `terminal.integrated.commandsToSkipShell` | [] | added to the defaults; `-id` removes |
| `terminal.integrated.sendKeybindingsToShell` | false | |
| `terminal.integrated.allowChords` | true | |
| `terminal.integrated.wordSeparators` | ` ()[]{}',"`─‘’“”\|` | |
| `terminal.integrated.copyOnSelection` | false | |
| `terminal.integrated.enableMultiLinePasteWarning` | "auto" | auto / always / never |
| `terminal.integrated.confirmOnKill` | "editor" | never/editor (panel terminals: no prompt)/panel/always |
| `terminal.integrated.confirmOnExit` | "never" | always / hasChildProcesses: closing the window asks first (after the window's own save prompts; a background-mode hide never asks) |
| `terminal.integrated.rightClickBehavior` | "default" | default (context menu) / copyPaste / paste / selectWord / nothing |
| `terminal.integrated.persistentSessionReviveProcess` | "never" | terminals are never restored |

## Debugger: `runInTerminal` (C8)

An additive seam in Dap: `DapPlatform.Terminals` (a lazy `ITerminalService` lookup) → `DebugSessionOptions.Terminals` → `DebuggeeLauncher`. With a
terminal composed, `kind: integrated` runs the debuggee in a terminal named after the request's `title` (else the session's name), **without a
shell in between** (the `processId` returned is the debuggee's), `IsTransient`, reused for the next run once its program exited (VS Code
reuses its debug terminal), shown without taking the keyboard from the editor, and killed (the terminal stays) when the session ends. Without a
terminal, and for `kind: external`, the previous launcher runs unchanged (streams piped into the Debug Console). js-debug's
`console: "integratedTerminal"` is no longer rewritten to the Debug Console when a terminal exists (`JsDebugAdapterFactory`), so it really runs in
the terminal. Tested with the real js-debug (`RunInTerminalTests`).

## Sessions / background mode (C9)

Terminals are **not restored** after a restart (`persistentSessionReviveProcess` is not supported). In a hidden session window terminals keep
running; nothing renders and nothing asks for frames, the blink timer runs only while focused in an active window, and no other timer or
polling exists (the process-name poll happens only while output arrives) — measured 0.4 % CPU for the whole idle process with a hidden window
and a shell (`BudgetTests.A_hidden_window_does_no_ui_work_for_its_terminals`).

## Budgets (measured on this machine, shared with other agents; `tests/CsCode.Panels.Terminal.Tests/BudgetTests.cs`)

Headless real composition, a real bash, the Terminal on screen, frames at 60 Hz, a heartbeat posting to the dispatcher every millisecond,
input injected as dispatcher jobs, the app's GC mode (SustainedLowLatency). Frames are *recorded* the way a desktop UI thread does it (the
control's `Render` into a drawing group); on a desktop, Avalonia rasterises on its render thread, while the headless compositor rasterises with
software Skia **on the UI thread** — that is reported separately and is not a terminal cost.

| Scenario | Budget | Measured |
|---|---|---|
| `yes` for 10 s, an editor being typed into | dispatcher p99 < 16 ms, max < 50 ms; typing p99 < 20 ms | p99 14.3-15.6 ms, max 18-50 ms; typing p99 3.8-4.7 ms; 1.6-1.9 M lines/s parsed; heap +3-6 MB |
| same, headless rasterisation on the UI thread | (reported) | p99 26-28 ms, ticks median ~10 ms |
| Ctrl+C during the `yes` flood → `yes` gone | < 100 ms | 1-25 ms |
| `cat` of a 100 MB file | p99 < 16 ms, max < 50 ms; memory under the cap | 2.1 s (48 MB/s), p99 13.8 ms, max 21.9 ms, heap +7 MB |
| keystroke → PTY write (UI thread), `cat` echoing | < 1 ms | median 0.07 ms, p99 0.13-0.8 ms (KeyDown with the precedence handler, then the text) |
| keystroke → echo on screen, `cat` echoing | p99 < one frame + the PTY's own echo p99 | p99 16.8-17.0 ms against 17.0-17.5 ms; median 8.5-10.4 ms; key → snapshot median 0.23-0.33 ms (bare PTY echo median 0.05-0.19 ms) |
| render of a frame (control) | | 0.12-0.23 ms average, longest 2-12 ms |
| 50 terminals created and killed (views toggled) | nothing retained | no session, view or control; no process; descriptors and threads back to baseline |
| hidden window, idle | ≈ 0 % | 0.4 % for the whole process |
| baseline (frames, no output) | | p99 0.4 ms |

The echo is counted from the moment the UI thread has the key to the first 60 Hz frame after the session published the snapshot with it,
plus the terminal's drawing of that frame; the PTY's own latency is a bare `UnixPtyHost` running `cat`, measured in the same attempt. Because
a key lands at a random point of the frame, the budget leaves the terminal well under a millisecond at p99. The headless compositor holds up
frames and keys whatever invalidates the window (an idle terminal: 0 of 60 frames held up > 3 ms; the terminal merely invalidated at the typing
rate, no input or output: 15-21 of 60), so the echo *as drawn here* (p99 ~25 ms) is reported, not budgeted.

Known noise: while the terminal view is visible and changing, the composed app shows ~15-25 *induced* (non-forced) gen2 collections per second
on the headless platform; a bare `TerminalService` under the same workload shows none, and they stop when the view is hidden, so they belong to
the headless compositor's rasterisation (framebuffer memory pressure), not to the terminal. The flood and echo scenarios get three attempts.

## Tests

```
dotnet test tests/CsCode.Terminal.Tests          # PTY host, parser, emulator, sessions/service, differential (tmux, libvterm), real sessions, fuzz
dotnet test tests/CsCode.Panels.Terminal.Tests   # encodings, selection/links, panel (fake PTYs), real app, runInTerminal (js-debug), budgets, screenshots
```

Shells in tests run sandboxed: `bash --noprofile --norc` / `sh` with HOME, XDG_CONFIG_HOME, XDG_DATA_HOME and ZDOTDIR in temp directories (the
real-app host sets this through a `sandboxed` default profile), nvim only as `nvim -u NONE -i NONE -n`, `CSCODE_HOME` in a temp directory,
never a window on a display (Avalonia headless).

### Differential tests (B5)

Identical byte streams go to CsCode's emulator and to reference terminals; screens are compared cell by cell (text, the attributes each
reference reports, cursor, alternate screen):

* **tmux** (`/usr/bin/tmux`, or `CSCODE_TMUX`): one PRIVATE server per run — `-S <temp>/sock`, `-f /dev/null`, `SHELL=/bin/sh`, HOME/XDG in the
  temp directory, a fixed size; the pane puts its tty in raw mode (`stty raw -echo -opost`), cats the input and signals a `wait-for` channel;
  the screen comes from `capture-pane -p -e -N` (parsed as an SGR stream; tmux marks line drawing with SO/SI), the cursor and alternate screen
  from `display-message`. The server is killed by its socket, then by its PID if needed. The user's tmux server and default socket are never
  touched.
* **libvterm** (Neovim's 0.3 build, `*-libvterm-neovim-*` in the Nix store, or `CSCODE_LIBVTERM`): in-process through function pointers; the
  40-byte cell layout was checked with a C program. Missing → the tests skip with the reason.
* Corpus: ~57 hand-written cases (one feature each), **recorded real sessions** (bash with a coloured prompt and `ls --color`, `git log` in
  less, nvim editing a file, `top -b` and `top`, the Python REPL, a `\r` progress bar — recorded on the real PTY, terminal queries answered by
  an emulator, replayed into every emulator: all agree with both references), and a fuzz pass.
* `CSCODE_DIFF_REPORT=<file>` writes every disagreement. Deliberate divergences are listed with reasons in `Corpus.KnownDivergences` (xterm/
  xterm.js behaviour where tmux or libvterm differ: erasing half a wide character, BS at a pending wrap, DECSTR, RIS and the alternate screen,
  DEC graphics `y`/`z`, 38:2 with a colour-space id, invalid UTF-8 replacement granularity; tmux's capture writes TAB characters, so its tab
  stops cannot be compared).
* **Findings**: libvterm 0.3 crashes (segfault) on a CSI with more than 16 parameters and on REP of an astral-plane character with a large
  count — the fuzz pass runs libvterm in a child process (a small C harness compiled with gcc) and names the crashing token; CsCode's emulator
  survives the same input (8 MB of noise and 50,000-token grammar fuzz, within its memory caps).

Screenshots (headless, git-ignored): `.scratch/screenshots/terminal/terminal-ls-color.png`, `terminal-nvim.png`, `terminal-git-log.png`,
`terminal-light-theme.png`.

## Known gaps and limits

* **Not done (SHOULD/STRETCH)**: split terminals; Find in terminal (Ctrl+F goes to the shell); shell-integration script injection, command
  hover/re-run menus and sticky scroll (the marks, exit-code decorations and Ctrl+Up/Down are done); tasks in terminals; the remote terminal (R3,
  `IPtyHost` is ready for it); screen-reader text; image protocols (no).
* Windows ConPTY: written, never run. macOS: `posix_spawn` cannot make the slave the controlling terminal there (BSD needs `TIOCSCTTY`), so job
  control and Ctrl+C would not work; the variadic `ioctl`/`fcntl` declarations are not valid on Apple arm64. Linux (glibc ≥ 2.29; 2.34+ for
  `closefrom`, 2.36+ for pidfd) is the supported platform.
* `confirmOnExit` hooks the window that shows the Terminal view (`Window.Closing`, after the main window's handler): terminals created with
  `Show = false` in a window that never showed the view are not counted. `confirmOnKill` asks only for `panel`/`always` (panel terminals with
  the default `editor` never ask).
* OSC 10/11/12 *set* is ignored (queries answered); DECRQSS/XTGETTCAP/sixel are consumed without answers; DECSLRM (left/right margins),
  DECDWL/DECDHL and 132-column mode are not implemented (as in xterm.js).
* Rendering: no font shaping or ligatures for ordinary text (one glyph per cell, as terminals draw); box-drawing characters come from the font
  (small gaps possible with some fonts); bidirectional text is not reordered.
* VS16 does not widen a narrow emoji (as xterm.js, tmux and libvterm).
* The key-precedence hook and the hide/maximize through the hosting Dock dockable are integration points outside the shell's API (see above);
  if the shell later offers "focused view handles keys first" or hide/maximize for tool windows, switch to those.
* Typing into an exited terminal does nothing (VS Code offers "press any key to close"; not implemented).
