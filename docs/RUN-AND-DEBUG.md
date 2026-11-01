# Running and debugging

How to run and debug programs **with** CsCode (part 1), and how to run, debug, test and package **CsCode itself** (part 2). Internals are
in [DAP.md](DAP.md) (the debugger), [PANELS-DEBUG.md](PANELS-DEBUG.md) (the debug windows), [DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md)
(Rust and TypeScript in depth) and [SESSIONS.md](SESSIONS.md) (the command line and background mode).

Every key below is read from the keymap presets (`src/CsCode.Platform/Resources/Keymaps/visualstudio.json`, the default, and
`vscode.json`; the setting `cscode.keymap`, `"visualstudio"` or `"vscode"`, chooses) and the modules' own built-in keys; `tests/CsCode.Docs.Tests` fails when a table here
and the keymaps disagree. Your `keybindings.json` can change any of them.

## Part 1: running and debugging your program

### Start Debugging and Start Without Debugging

| | Start Debugging | Start Without Debugging |
|---|---|---|
| Command | `workbench.action.debug.start` | `workbench.action.debug.run` |
| Key | F5 | Ctrl+F5 (⌃F5 on a Mac: Control, not Cmd) |
| What runs | the selected launch configuration under its debugger: breakpoints stop, the debug windows open | the same configuration with `noDebug`: the program runs through the same adapter and its output reaches the Debug Console, but no breakpoint stops it |
| Before it | its `preLaunchTask` | its `preLaunchTask` |

Both start the configuration selected in the toolbar (the first one when none is). While a session runs, F5 means **Continue** and
Shift+F5 stops it. With no configuration at all, F5 offers **Create launch.json** (see [launch.json](#launchjson)). The same commands are on the
**Debug** menu and in the command palette (F1).

### Debugging keys

The presets' keys, per OS (`—`: no key). On a Mac the function keys need **Fn** unless *System Settings ▸ Keyboard ▸ Keyboard Shortcuts ▸
Function Keys ▸ Use F1, F2, etc. keys as standard function keys* is on, and F11 is *Show Desktop* in macOS's own shortcuts until you turn that
off (*Keyboard Shortcuts ▸ Mission Control*).

| Action | Command | Visual Studio preset, Windows/Linux | Visual Studio preset, macOS | VS Code preset, Windows/Linux | VS Code preset, macOS |
|---|---|---|---|---|---|
| Start Debugging | `workbench.action.debug.start` | F5 | F5 | F5 | F5 |
| Continue (while debugging) | `workbench.action.debug.continue` | F5 | F5 | F5 | F5 |
| Start Without Debugging | `workbench.action.debug.run` | Ctrl+F5 | ⌃F5 | Ctrl+F5 | ⌃F5 |
| Stop Debugging | `workbench.action.debug.stop` | Shift+F5 | ⇧F5 | Shift+F5 | ⇧F5 |
| Restart | `workbench.action.debug.restart` | Ctrl+Shift+F5 | ⇧⌘F5 | Ctrl+Shift+F5 | ⇧⌘F5 |
| Pause (Break All) | `workbench.action.debug.pause` | Ctrl+Alt+Break | F6 | F6 | F6 |
| Step Over | `workbench.action.debug.stepOver` | F10 | F10 | F10 | F10 |
| Step Into | `workbench.action.debug.stepInto` | F11 | F11 | F11 | F11 |
| Step Out | `workbench.action.debug.stepOut` | Shift+F11 | ⇧F11 | Shift+F11 | ⇧F11 |
| Toggle Breakpoint | `editor.debug.action.toggleBreakpoint` | F9 | F9 | F9 | F9 |
| Command palette | `workbench.action.showCommands` | Ctrl+Q or Ctrl+Shift+P or F1 | ⇧⌘A or ⇧⌘P or F1 | Ctrl+Shift+P or F1 | ⇧⌘P or F1 |
| Output window | `workbench.action.output.toggleOutput` | Ctrl+Alt+O | ⌥⌘O | Ctrl+Shift+U | ⇧⌘U |
| Error List | `workbench.actions.view.problems` | Ctrl+\\, Ctrl+E | ⌘\\ ⌘E | Ctrl+Shift+M | ⇧⌘M |
| Terminal | `workbench.action.terminal.toggleTerminal` | Ctrl+\` | ⌃\` | Ctrl+\` | ⌃\` |

Keys the editor, the debug windows and the task runner add, the same in both presets:

| Action | Command | Windows/Linux | macOS |
|---|---|---|---|
| Run To Cursor | `editor.debug.action.runToCursor` | Ctrl+F10 | ⌘F10 |
| Enable or disable the line's breakpoint | `debug.enableOrDisableBreakpoint` | Ctrl+F9 | ⌘F9 |
| QuickWatch (while stopped) | `cscode.debug.quickWatch` | Shift+F9 | ⇧F9 |
| Add Watch (while debugging) | `debug.addToWatch` | Ctrl+Alt+W | ⌃⌥W |
| Run Build Task | `workbench.action.tasks.build` | Ctrl+Shift+B | ⌃⇧B |

### The Debug toolbar

The **Debug** toolbar group (*View ▸ Toolbars* hides it) holds the launch-configuration drop-down, the green **Start** button (it shows the
selected configuration's name; the tooltip shows its key), the **Start Without Debugging** button, and while a session runs Continue, Pause,
Step Over, Step Into, Step Out, Restart and Stop. The drop-down lists the folder's launch.json configurations and compounds; the choice is remembered per
workspace and is what F5 and Ctrl+F5 start.

### launch.json

* **Where**: `.cscode/launch.json` in the workspace folder, else `.vscode/launch.json` (VS Code's format, JSON with comments; the file is
  watched, so edits apply at once). Configurations, `compounds`, `inputs`, per-OS `windows`/`linux`/`osx` blocks and VS Code's
  `${…}` variables work as in VS Code ([DAP.md](DAP.md#launchjson-variables-tasks)).
* **Creating it**: F5 with no configuration asks *"There is no launch configuration. Create a launch.json?"*; *Debug ▸ Open launch.json*
  opens it (creating it first); *Debug ▸ Add Configuration...* adds one debugger template to the file in the editor.
* **Which debugger F5 picks** for a new launch.json, from the files at the folder's root:

| Folder has | Debugger (`type`) | Adapter |
|---|---|---|
| `Cargo.toml` | `lldb` on macOS, `gdb` on Linux (setting `cscode.debug.rustDebugger`: `"lldb"` or `"gdb"`) | CodeLLDB / GDB |
| a `.csproj`, `.fsproj`, `.sln` or `.slnx` | `coreclr` | netcoredbg |
| `tsconfig.json` or `package.json` | `pwa-node` | js-debug |
| anything else | a *Select debugger* pick | |

* **What it writes** (and the `tasks.json` its `preLaunchTask` needs, beside it, unless your tasks.json already has that task):

| Debugger | Configurations | Task written to tasks.json |
|---|---|---|
| `coreclr` | **.NET: Launch** (`program: …/bin/Debug/<tfm>/<Project>.dll`, `preLaunchTask: build`; you pick the startup project when there are several), **.NET: Attach** | `build`: `dotnet build <project>`, the default build task |
| `pwa-node` | **TypeScript: Launch (tsc build)** (when there is TypeScript), **Node.js: Launch Program**, **Node.js: Attach (--inspect)** (port 9229) | `tsc: build` |
| `lldb` (CodeLLDB) | **Rust: Debug &lt;bin&gt; (CodeLLDB, cargo build)**, **Rust: Debug unit tests of &lt;package&gt; (CodeLLDB, cargo test)**, **Rust: Attach to &lt;bin&gt; (CodeLLDB)**; without Cargo.toml **LLDB: Launch program**, **LLDB: Attach to process** | `cargo: build` |
| `gdb` | **Rust: Debug &lt;bin&gt; (cargo build)**, **Rust: Debug unit tests of &lt;package&gt; (cargo test)**, **Rust: Attach to &lt;bin&gt;**; without Cargo.toml **GDB: Launch program**, **GDB: Attach to process** | `cargo: build` |

### preLaunchTask and tasks.json

A configuration's `preLaunchTask` names a task in `.cscode/tasks.json` or `.vscode/tasks.json` (VS Code's format: `shell` and `process`
tasks, `dependsOn`, `group`, `problemMatcher`, `options.cwd`/`env`). It runs in a terminal named after it before the debugger starts;
`postDebugTask` runs after the session. If the task fails you are asked whether to **Debug Anyway**; Stop (Shift+F5) during the build cancels
the start and kills the task. Problem matchers (`$msCompile`, `$tsc`, `$rustc`, `$gcc`, …) fill the Error List. Run tasks yourself with
*Terminal ▸ Run Task...* and *Run Build Task...* (Ctrl+Shift+B, the default build task).

### Breakpoints, conditions, hit counts and logpoints

* **F9** toggles a breakpoint on the caret's line; a click in the glyph margin (left of the line numbers) does the same.
* **Right-click the glyph margin**: *Add Conditional Breakpoint…*, *Add Logpoint…*, and on an existing breakpoint *Edit Condition…*,
  *Edit Hit Count…*, *Edit Log Message…*, *Disable/Enable Breakpoint*, *Remove Breakpoint*. The editor's context menu has the same under
  *Breakpoint* (*Conditions...*). A logpoint prints its message (`{expression}` is evaluated) to the Debug Console without stopping.
* Only what the adapter supports is offered; the rest says *(not supported)* with the reason:

| Adapter | Condition | Hit count | Logpoint |
|---|---|---|---|
| netcoredbg (.NET) | yes | no | no |
| js-debug (Node/TypeScript) | yes | yes | yes |
| CodeLLDB (Rust/C/C++) | yes | yes, exact (`=N`, `%N`, `>=N`) | yes |
| GDB (Rust/C/C++) | yes | from the Nth hit on (no `%N`) | yes |

* The **Breakpoints** window lists every breakpoint with its state (filled = verified, hollow = not bound yet, red text = the adapter's
  error), the adapter's **exception filters** (for example js-debug's caught and uncaught exceptions, "Rust panics" for GDB and CodeLLDB),
  *New Function Breakpoint*, *Enable All*, *Disable All*, *Delete All*. Breakpoints are saved per workspace and follow the lines as you edit.

### The debug windows

They open in the bottom well when a session starts (the Exception window when the program stops on an exception, Modules only on request).
Show any of them from *View ▸ Tool Windows* or the palette (*Toggle Locals*, …).

| Window | What it shows | Palette command (id) |
|---|---|---|
| Locals | the current frame's variables; edit a value inline (Enter or F2) | `workbench.debug.action.toggleLocals` |
| Autos | the variables used on the current and the previous line, plus the value the last step-out returned | `workbench.debug.action.toggleAutos` |
| Watch 1 | your expressions, re-evaluated at every stop (Add Watch, drag to reorder, saved per workspace) | `workbench.debug.action.toggleWatch` |
| Call Stack | the stopped thread's frames; click one to see its source and variables; *Show External Code* | `workbench.debug.action.toggleCallStack` |
| Threads | every thread, its top frame and state; pick one to switch | `workbench.debug.action.toggleThreads` |
| Breakpoints | see above | `workbench.debug.action.toggleBreakpoints` |
| Debug Console | the program's output (stdout, stderr in red) and a REPL that evaluates in the current frame (↑/↓ history, completions) | `workbench.debug.action.toggleDebugConsole` |
| Modules | the modules (assemblies, shared libraries) the adapter reports as loaded | `workbench.debug.action.toggleModules` |
| Exception | the exception the program stopped on: type, message, stack, inner exceptions; *Copy Details*, *Continue* | `workbench.debug.action.toggleException` |

While stopped, the editor marks the stop line with the yellow arrow; *Run To Cursor* (Ctrl+F10), *Add Watch* and *QuickWatch...* (Shift+F9)
are in the editor's context menu. In the Debug Console a backtick or `-exec` sends a raw GDB/LLDB command.

### Attach to a process

Use (or add with *Debug ▸ Add Configuration...*) an attach configuration: **.NET: Attach**, **Rust: Attach to &lt;bin&gt;**, **GDB/LLDB: Attach
to process** use `${command:pickProcess}`, which lists the running processes to pick from (CsCode itself is never listed); **Node.js:
Attach (--inspect)** connects to a program started with `node --inspect` on port 9229. Select it in the toolbar and press F5. Stop
(Shift+F5) detaches and leaves an attached program running (a launched one is ended). On Linux, GDB and CodeLLDB can only attach to a process that is not
your own child when `/proc/sys/kernel/yama/ptrace_scope` is 0 (Ubuntu's default is 1).

### Per language

**.NET (C#, F#, VB)** — debugger `coreclr`, adapter **netcoredbg** (Samsung, MIT). F5 builds with the `build` task (`dotnet build`) and runs the
project's `bin/Debug/<tfm>/<Project>.dll`. `"console": "integratedTerminal"` runs the program in a terminal (for programs that read the
keyboard); the default `internalConsole` puts its output in the Debug Console. netcoredbg has no hit counts or logpoints.

**Node.js and TypeScript** — debugger `pwa-node` (`node` works too), adapter **js-debug** (Microsoft's, the one VS Code uses). Needs `node`
on PATH (or `cscode.debug.nodePath`); the TypeScript template compiles with `tsc` first and uses the source maps (`outFiles`), so breakpoints
in `.ts` files bind. Node.js internals are skipped while stepping (`skipFiles`).

**Rust, C and C++** — **CodeLLDB** (`lldb`) is the default on macOS, where GDB cannot debug native arm64 programs; **GDB** (`gdb`, GDB 14 or
newer, `gdb -i=dap`, nothing to download) is the default on Linux. Both are always available: choose with `cscode.debug.rustDebugger` (for new
launch.json files) or the `type` of a configuration. The `cargo` key (`"cargo": {"args": ["test", "--no-run"]}`) builds and finds the
program for you, as VS Code's CodeLLDB does. A "Rust panics" exception filter stops on a panic. Comparison:
[DEBUGGING-RUST-TS.md](DEBUGGING-RUST-TS.md#which-rust-debugger-is-the-default).

**lldb-dap** (LLVM's own adapter, in Xcode's Command Line Tools: `xcrun -f lldb-dap`) is **not built in**; add it as your own debugger with the
`cscode.debuggers` setting, with the path `xcrun -f lldb-dap` prints (it is then offered by *Select debugger* and usable as
`"type": "lldb-dap"`). This setting is checked by a test; a debug session through lldb-dap has not been tried:

```json
"cscode.debuggers": [
  {
    "type": "lldb-dap",
    "label": "LLDB (lldb-dap)",
    "program": "/Library/Developer/CommandLineTools/usr/bin/lldb-dap",
    "languages": ["c", "cpp", "rust"]
  }
]
```

```json
{ "name": "lldb-dap: Launch", "type": "lldb-dap", "request": "launch", "program": "${workspaceFolder}/a.out", "cwd": "${workspaceFolder}" }
```

### Where the debug adapters come from

Each adapter is looked up in this order: its setting (`cscode.debug.netcoredbgPath`, `cscode.debug.jsDebugPath`,
`cscode.debug.codelldbPath`, `cscode.debug.gdbPath`), its environment variable (`CSCODE_JSDEBUG`, `CSCODE_CODELLDB`), a copy you downloaded
from CsCode (`{UserDataDir}/tools`), **the copy installed with the app**, then PATH.

* **CsCode.app** (`tools/package-macos.sh`) ships netcoredbg, js-debug and CodeLLDB in `CsCode.app/Contents/Resources/debug-adapters`.
* **Linux install** (`tools/install.sh`) copies the adapters already on the build machine next to the app
  (`~/.local/share/cscode/current/debug-adapters`); it downloads nothing, so run `tools/fetch-netcoredbg.sh`, `tools/fetch-js-debug.sh` and
  `tools/fetch-codelldb.sh` first (or pass `--netcoredbg`/`--js-debug`/`--codelldb DIR`). GDB comes from the system (`sudo apt install gdb`).
* **Missing adapter**: the first session that needs it asks *Download …?* (netcoredbg from its GitHub release, js-debug v1.140.0 and CodeLLDB
  v1.12.3 pinned, each checked against its SHA-256) and installs it into `{UserDataDir}/tools`. Nothing is downloaded without asking.

### Where the output goes

| What | Where |
|---|---|
| Your program's stdout/stderr | Debug Console (`"console": "internalConsole"`, the templates' default), or a terminal with `"console": "integratedTerminal"` (for CodeLLDB also drop the template's `"terminal": "console"`) |
| `preLaunchTask` and other tasks | a terminal named after the task (Terminal window, Ctrl+\` / ⌃\`) |
| Build errors from problem matchers | Error List |
| The debugger's own messages, adapter errors | Debug Console (grey lines), and CsCode's log (below) |
| CsCode's log | `cscode.log` in the logs folder: `~/Library/Logs/CsCode` (macOS), `~/.local/state/cscode/logs` (Linux), `$CSCODE_HOME/logs` with `CSCODE_HOME` |

### Common problems

* **macOS: native debugging (CodeLLDB, lldb-dap) asks for a password or fails to attach.** macOS lets a debugger control a process only for a
  user in the `_developer` group with developer mode on. Run `DevToolsSecurity -status`; if it says disabled, `sudo DevToolsSecurity -enable`
  (and, if `id -Gn` does not list `_developer`, `sudo dseditgroup -o edit -a "$USER" -t user _developer`, then log out and in). .NET
  (netcoredbg) and Node.js debugging do not need it.
* **"node", "cargo", "dotnet" or "gdb" not found when CsCode was started from Finder, the Dock or Spotlight.** Such an app gets macOS's minimal
  PATH. CsCode asks your login shell (`$SHELL -l -i`) for its environment once at start-up and every debugger, task and terminal inherits it,
  so whatever `~/.zprofile`/`~/.zshrc` add (Homebrew, nvm, rustup, `~/.dotnet`) is found. If your rc files take longer than 10 s or print
  errors, check the log's `login shell environment:` line; `CSCODE_SKIP_SHELL_ENV=1` turns it off (the usual install folders are then added to
  PATH instead). Started from a terminal, CsCode simply inherits that terminal's environment.
* **The adapter is missing**: accept the download offer, or point the setting (`cscode.debug.*Path`) at your own copy. Without a window (a
  test, a script) the start fails and says which setting or script to use.
* **F5 does nothing visible**: there is a launch.json with no configuration (or one that does not parse): F5 opens it; the error is in the
  notification.
* **Breakpoint stays hollow**: the code is not loaded yet, has no debug symbols (build in Debug), or the source maps do not cover it (TypeScript:
  check `outFiles`). The Breakpoints window shows the adapter's reason.
* **GDB on a Mac** cannot debug arm64 programs: use CodeLLDB (the default there).

## Part 2: developing CsCode

Set up: the .NET SDK `global.json` names (10.0.401 or a later 10.0 feature band); on macOS [README.md](../README.md#macos), on Linux with a Nix
SDK `source tools/nix-dev-env.sh` first. Build with `dotnet build CsCode.slnx` (warnings are errors).

### Running from source

```bash
dotnet run --project src/CsCode.App -- --foreground [paths...]    # the window runs in this process; closing it exits
dotnet run --project src/CsCode.App -- [paths...]                 # background mode: returns once the window is up
dotnet run --project src/CsCode.App -- --list-sessions            # saved and running sessions, with their PIDs
dotnet run --project src/CsCode.App -- --quit --all               # quit every background CsCode process (of this CSCODE_HOME)
dotnet run --project src/CsCode.App -- --help
```

Without `--foreground`, `dotnet run` is the **launcher**: it starts (or reuses) a session process that keeps the window and outlives the
command, so a later run shows the same window; quit it with `--quit --all` or *File ▸ Quit Session*. To keep a development build away
from your real settings and sessions, give it its own home: `CSCODE_HOME=/tmp/cscode-dev dotnet run --project src/CsCode.App -- --foreground`
puts config, data, cache, logs and the session sockets under that folder (`config/`, `data/`, `cache/`, `logs/`, `run/`). `--list-sessions`
and `--quit --all` with the same `CSCODE_HOME` see only that home's background processes, and never a `--foreground` window (it has no
session endpoint): close that window, or stop it with Shift+F5 when it runs under a debugger.

### Debugging CsCode itself

**From CsCode or VS Code**: open the repository folder and press F5. The repository's `.vscode/launch.json` (CsCode reads `.cscode/` first,
then `.vscode/`; VS Code needs its C# extension) has:

| Configuration | What it does |
|---|---|
| **CsCode (sandboxed window)** | builds the app (`build app` task), starts `cscode.dll --foreground` under the debugger with `CSCODE_HOME=.scratch/debug-home`, so the debugged window has its own settings, sessions and logs and never meets the CsCode you are working in. Add paths after `--foreground` to open them; remove `env` to use your own settings. |
| **CsCode: attach to a process** | attaches to a running CsCode process picked from a list |

`.vscode/tasks.json` also has `build` (the whole solution, the default build task) and `quit sandboxed background sessions` (`--quit --all`
for `.scratch/debug-home`: background sessions started under that home, for example by running `cscode` in the debugged window's terminal).
Neither it nor `--quit --all` reaches the debugged window itself, a `--foreground` process: stop it with Shift+F5 (Stop Debugging) or close it. In CsCode the adapter is netcoredbg (it stops in CsCode's own code on an arm64 Mac too); in VS Code it is the C#
extension's debugger.

**From Rider or Visual Studio**: run the `CsCode.App` project with program arguments `--foreground` (plus any paths) and, for a sandbox, the
environment variable `CSCODE_HOME=<repo>/.scratch/debug-home`. Without `--foreground` the debugged process is only the launcher, which hands
the window to a background process and exits.

**A background session process**: run `cscode --list-sessions` (or `dotnet run --project src/CsCode.App -- --list-sessions`, with the
same `CSCODE_HOME` as that process) for its PID, then attach (*CsCode: attach to a process* in CsCode/VS Code, *Attach to Process* in Rider).
While you are stopped the process misses the launcher's 300 ms hello: a `cscode` run meanwhile reports the session as not responding and
opens a copy, so continue before you use the launcher again.

**Logs**: `cscode.log` (rotated to `cscode.1.log` …, 5 × 5 MB) and `crash-*.log` in `~/Library/Logs/CsCode` (macOS),
`~/.local/state/cscode/logs` (Linux), `$CSCODE_HOME/logs` when it is set (`.scratch/debug-home/logs` for the sandboxed configuration).

### Running tests

Run a module's project or a filter, not the whole solution (it takes long and timing tests are load-sensitive):

```bash
dotnet test tests/CsCode.Dap.Tests                                                # one project
dotnet test tests/CsCode.Dap.Tests --filter "FullyQualifiedName~DefaultDebuggerTests"   # one class
dotnet test tests/CsCode.Panels.Debug.Tests --filter "Category=RealAdapter"      # the real-adapter suites only
dotnet test tests/CsCode.Docs.Tests                                               # this guide against the keymaps and .vscode/
dotnet test tests/CsCode.Dap.Tests --blame-hang-timeout 5m                        # dump and stop a test that hangs
```

* Real-adapter tests skip until the adapter is there: `tools/fetch-netcoredbg.sh`, `tools/fetch-js-debug.sh`, `tools/fetch-codelldb.sh` fetch
  the pinned versions into `.scratch/tools/` (they need `node`, `tsc`, `cargo` or `gdb` for their samples, and skip without them).
* The test harnesses set their own `CSCODE_HOME` and trash folder, so a test never touches your settings or your running CsCode.
* A timing-budget test that fails in a loaded run should be rerun alone before you conclude anything (known cases:
  [handoff/known-issues.md](handoff/known-issues.md)).

### Packaging and installing

```bash
tools/package-macos.sh              # macOS: .scratch/macos/osx-arm64/CsCode.app, adapters bundled, signed ad hoc
tools/package-macos.sh --install    # ... and copy it to ~/Applications with a `cscode` command on PATH
tools/install.sh                    # Linux: per-user install (~/.local/share/cscode, ~/.local/bin/cscode, desktop entry)
tools/uninstall.sh                  # Linux: removes what install.sh wrote; settings and sessions stay
```

Both bundle the debug adapters found in `.scratch/tools` (package-macos.sh fetches missing ones; install.sh does not). After an install,
quit running windows (`cscode --quit --all`) so the next `cscode` starts the new build. Details: [README.md](../README.md#install-per-user-linux)
and [SESSIONS.md](SESSIONS.md#installing-toolsinstallsh-toolsuninstallsh).
