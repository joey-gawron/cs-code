# Python

Language stage 1 (brief: `docs/briefs/lang-1-python.md`). What CsCode does for Python, how, what was checked against the real tools, and the
limits. Tool versions used for every check below (2026-11-01, macOS 27 arm64): Pyright 1.1.414, basedpyright 1.40.2, Ruff 0.17.3, debugpy
1.8.22, pytest 9.2.0; interpreters python.org 3.13.5, Homebrew 3.14.7.

Two pieces are language-neutral and reused by later stages: **formatter choice / code actions on save** (§2) and **bundled snippets** (§6).
`envFile` (§4) applies to every debugger.

## 1. Interpreters

`IPythonEnvironmentService` (Core contract, `src/CsCode.Platform/Python/`).

* **Discovery** (on the IO lane, each probe `python -I -c …` killed after 2 s, at most four at a time, cached per folder): the folder's
  `.venv` / `venv` / `env` (marked by `pyvenv.cfg`), `VIRTUAL_ENV`, `CONDA_PREFIX`, Poetry (`poetry env info -p`, only when `poetry` is on PATH and
  pyproject.toml has `[tool.poetry…]`), conda's `~/.conda/environments.txt` and `CONDA_EXE`'s base, pyenv's versions, Homebrew's `python3*`,
  python.org's framework builds, `python3` / `python` on PATH, `py -0p` on Windows. Two paths to one interpreter (same `sys.prefix`) are listed
  once. It waits for the login shell's environment first (a Dock-started app has launchd's PATH). macOS's `/usr/bin/python3` is used only when
  the Command Line Tools' (or Xcode's) Python exists: otherwise running it opens the "install developer tools" dialog.
* **Choice**, per workspace folder: the one picked in **Python: Select Interpreter** (persisted in workspace state), else
  `python.defaultInterpreterPath` (user settings only: it names a program CsCode runs), else the best found: the folder's own venv, then
  `VIRTUAL_ENV`, `CONDA_PREFIX`, Poetry's, then the first `python3` on PATH.
* **Picker**: version, kind and path of each, "Enter interpreter path…", "Refresh". **Status bar** (right side, while a Python file is active):
  `3.14.7 ('.venv': venv)`; a click opens the picker. **Python: Clear Workspace Interpreter Setting** forgets the pick.
  `${command:python.interpreterPath}` works in launch configurations and tasks.
* **Fed to**: Pyright / basedpyright as `python.pythonPath` in their `workspace/configuration` answers (an additive
  `ILanguageServerSettingsContributor`; a change pushes `workspace/didChangeConfiguration` and the server asks again), debugpy (`python`), the
  test provider, and **new terminals** (`python.terminal.activateEnvironment`, default on): the environment's `bin` first on PATH and
  `VIRTUAL_ENV` (or `CONDA_PREFIX`) set, through an additive `ITerminalEnvironmentContributor`. A macOS login shell's `path_helper` (and Nix's
  profile scripts here) rebuild PATH and drop or demote that directory, so the shell integration (zsh, bash, fish) puts `CSCODE_PATH_PREPEND`
  first again after the user's startup files (`TerminalServiceTests.Contributed_PATH_directories_come_first_even_in_a_login_shell`).
* Workspace trust: a folder's own `.venv/bin/python` is probed (with `-I`) and given to Pyright, as VS Code does in a trusted workspace. CsCode
  has no trust prompt; rust-analyzer already runs a folder's build scripts the same way.

## 2. Formatter choice and code actions on save

* **`editor.defaultFormatter`** (global or `"[python]": { … }`): a language server id (the `cscode.languageServers` key: `ruff`, `python`
  for Pyright); a VS Code extension id ending in it (`charliermarsh.ruff`) names it too. Format Document / Selection / on save / on paste / on
  type use only that server. Unset: the first formatter that answers, as before. A chosen server that is not running formats nothing and says
  so in the status bar. Providers say which server they come from through the additive `ILanguageProviderSource`; a remote workspace's bridged
  providers cannot yet, and keep formatting as before. **Format Document With…** (Edit menu, editor context menu,
  `editor.action.formatDocument.multiple`) picks one for a single run, the default marked.
* **`editor.codeActionsOnSave`**: the object form (`"explicit"`, `"always"`, `"never"`, and the legacy `true`/`false`) and the array form;
  `explicit` runs on a save the user asked for, `always` also on auto-save. Kinds match hierarchically (`source.fixAll` runs
  `source.fixAll.ruff`); a kind listed with a more specific one leaves that one to its own entry. In the object form the fix-all kinds run first.
  Each provider is asked in turn (the next sees the text the last left); deferred edits are resolved (`codeAction/resolve`); command-only actions
  are executed. Everything a save applies is one undo unit. Deadline: `editor.codeActionsOnSaveTimeout` (750 ms); a hung provider costs at most
  that. It is the first `IWillSaveParticipant`, ahead of the servers' `willSaveWaitUntil`, and runs before format on save (VS Code's order).
  CsCode has no auto-save yet, so every save is "explicit".

## 3. Language servers

| Server | Id | Default | What it gives |
|---|---|---|---|
| Pyright `pyright-langserver --stdio` | `python` | on | types, completion, hover, navigation, semantic tokens, inlay hints; gets the selected interpreter |
| Ruff `ruff server` (Ruff ≥ 0.5.3) | `ruff` | on | lint diagnostics, Quick Fixes, `source.fixAll.ruff`, `source.organizeImports.ruff`, formatting; one server per workspace |
| basedpyright `basedpyright-langserver --stdio` | `basedpyright` | **off** | Pyright's features plus its stricter checks; turn it on and Pyright off: `"cscode.languageServers": { "basedpyright": { "enabled": true }, "python": { "enabled": false } }` |

The Python tool servers run the selected environment's own copy when it has one (`.venv/bin/ruff`, as VS Code's Ruff extension does by
default), else PATH, `cscode.lsp.searchDirs`, the tools directory. Install hints per platform are in docs/LANGUAGES.md.

**Pyright or basedpyright as the default.** The brief expected only basedpyright to have semantic highlighting and inlay hints; against the
current releases both have them (`RealPythonServerTests.What_Pyright_and_basedpyright_offer`: semantic tokens and inlay hints `: int`, `a=`, `b=`
from each). basedpyright's remaining differences are its stricter default rule set, its baseline file and a few Pylance-like features. Pyright
stays the default until the user decides otherwise.

`pyproject.toml` schema validation through Taplo is **not verified**: Taplo is not installed on this Mac.

## 4. Debugging: debugpy

Types `debugpy` and the legacy `python` (rewritten to `debugpy`), `src/CsCode.Dap/Adapters/DebugpyAdapterFactory.cs`.

* **Adapter**: `<interpreter> -m debugpy.adapter` when the interpreter has debugpy; otherwise the pinned bundled copy,
  `<interpreter> {ToolsDir}/debugpy/1.8.22/debugpy/adapter` (`DebugpyInstaller`: the pure-Python wheel, SHA-256 pinned, asked before
  downloading; `cscode.debug.debugpyPath` / `CSCODE_DEBUGPY` override). Attach with `connect` (or legacy `host`/`port`) talks to the adapter that
  `python -m debugpy --listen` started; attach with `processId` injects (below).
* **Provider** fills in `python` (the folder's interpreter), `cwd` (the folder), `console` (`integratedTerminal` when there is a terminal),
  the folder's `.env` when no `envFile` is given (`python.envFile`), a numeric `processId`, and `program` = the active Python file when nothing
  says what to run. `args`, `env`, `justMyCode`, `stopOnEntry`, `subProcess`, `django`, `jinja`, `flask` and the rest pass through.
* **F5 with no launch configuration** on a Python file debugs it ("Python Debugger: Current File", an additive `IDebugCurrentFileProvider`).
  Create launch.json in a Python folder offers Current File, Module, Attach and Attach using Process Id.
* **Child processes**: debugpy announces each subprocess with DAP's `startDebugging` (CsCode advertises it) or, to a client that does not, with
  its `debugpyAttach` event (`debugpy/adapter/clients.py`, `notify_of_subprocess`: an attach configuration with `connect` to the parent adapter's
  listener and `subProcessId`); both start a child session (the event through a new `DebugAdapterQuirks.ChildSessionFromEvent` hook).
* **`envFile` for every debugger**: resolved in `DebugConfigurationService` after `${…}` substitution and before the providers: dotenv lines,
  `export`, comments, quotes, `${env:NAME}` / `${NAME}`; `env` wins over the file; a missing file is skipped.

### Evaluation matrix (real adapter)

`tests/CsCode.Dap.Tests/RealDebugpyTests.cs`, debugpy 1.8.22 through the real `DebugService`, the tool venv (python.org 3.13.5).

| Area | Result | Evidence |
|---|---|---|
| Launch, stop, clean exit | works; adapter and debuggee processes gone | `Launch_stops_at_a_line_breakpoint_…_exits_cleanly` |
| Line breakpoints | works | same |
| Conditional (`i == 3`) | works | `Conditional_hit_count_and_log_breakpoints` |
| Hit count (`4`) | works (the 4th hit) | same |
| Logpoints (`logged total={total}`) | works; never stops | same |
| Exception filters | `raised`, `uncaught` (default), `userUnhandled`; raised and uncaught both stop at the raise with the exception text | `Raised_and_uncaught_exception_filters` |
| Removed breakpoint while stopped | **was broken, fixed**: the `continue` overtook the debounced `setBreakpoints`, so the removed breakpoint stopped again; pending breakpoint changes are now sent before any resume (all adapters) | `A_breakpoint_removed_while_stopped_does_not_stop_again` |
| Step into / out / over | works | `Steps_and_the_call_stacks_of_a_generator_and_a_coroutine` |
| Call stack: generator | `gen < main` | same |
| Call stack: coroutine | `work < main < <module>` (asyncio's frames hidden by justMyCode) | same |
| Pause | works, reason `pause` | `Pause_set_variable_and_the_REPL_with_completions` |
| Locals: dict, list, set, dataclass, instance | values, types and members | `Launch_…` |
| Large collections | debugpy pages them itself: the first 100 items, then `more` (ranges `[100:1100]` … `[4100:5000]`) and `len()`; it announces no `indexedVariables` | `Launch_…` |
| Set variable (`41 + 1`) | `42`, and the program sees it | `Pause_set_variable_…` |
| REPL evaluate, watch, completions | `len(items) + total` → `45`; `ite` completes `items` | same |
| Threads | `MainThread`, `spinner`; a breakpoint in the thread stops it | `Threads_are_listed_…` |
| multiprocessing (`subProcess: true`, spawn) | child session `Subprocess <pid>` (attach, `subProcessId`) hits its breakpoint | `A_multiprocessing_child_becomes_a_child_session_…` |
| Attach with `connect` to `debugpy --listen` | works; no adapter process of CsCode's | `Attach_connects_to_a_program_started_with_debugpy_listen` |
| Attach by process id (macOS) | **does not work here**: lldb attaches (Developer Mode is on) and loads debugpy's `attach.dylib`, but the injected call is interrupted (`Expression execution was interrupted: signal SIGSTOP`), so the adapter never connects; CsCode reports "did not send 'initialized' within 30s" and cleans up | `Attach_by_process_id_…` (skips with the reason); reproduced outside CsCode with `python -m debugpy --pid` |
| Bundled adapter (Homebrew 3.14.7 without debugpy) | works | `The_bundled_adapter_debugs_with_an_interpreter_that_has_no_debugpy` |
| 1M-line output flood | session ends in 2.4 s; 3.8 M characters shown, 3.1 M dropped by the output bound; nothing left running | `A_million_lines_of_output_stay_bounded_…` |

**Attach by process id on macOS needs**: lldb (Command Line Tools or Xcode), Developer Mode (`DevToolsSecurity -enable`), and a target that is
not a hardened-runtime binary. Even with all three, debugpy 1.8.22's injection into python.org's 3.13 failed as above. The dependable way is
`python -m debugpy --listen 5678 --wait-for-client app.py` and the "Python Debugger: Attach" configuration.

## 5. Tests: pytest and unittest

`src/CsCode.Testing/Providers/Python/`.

* **Detection**: a folder with a pytest configuration (`pytest.ini`, `[tool.pytest.ini_options]`, `[tool:pytest]`, `[pytest]` in tox.ini,
  `conftest.py`) or a test file (`test*.py`, `*_test.py`; virtual environments are not searched). pytest when configured or installed in the
  folder's interpreter, otherwise unittest.
* **Discovery**: `python -m pytest -p cscode_pytest --collect-only -q` plus `python.testing.pytestArgs`; a small plugin reports each item's node
  id, real file and line. A parametrized test is one test whose **cases** (its parametrizations) show as children before it runs (additive
  `DiscoveredTest.Cases`). unittest: a discovery script over `python.testing.unittestArgs` (`-s`, `-p`, `-t`). `python.testing.cwd` sets the
  directory. Real paths (`/private/var/…`, symlinked checkouts) are mapped back to the folder's spelling.
* **Runs**: pytest with the node ids and `--junitxml` (results, durations, messages, reports, captured output); the plugin marks each test
  started; the report's last frame under the project becomes the failure location (`StackTraces.FromPython`). unittest: the script's own runner.
* **Debugging** runs the same command through debugpy (`module: pytest` or the script, `purpose: ["debug-test"]`). "Run Test | Debug Test" lenses
  and gutter glyphs come from the Test Explorer as for the other providers. Running one case runs its whole test.
* `.env` files are not loaded for test runs yet.

Real-tool evidence: `tests/CsCode.Panels.Testing.Tests/Providers/PythonProviderTests.cs` (pytest discovery with cases, run with a failing case
located at its assert, skip, single-test run, the debug configuration run for real; unittest discovery and run with Homebrew's 3.14.7, which has
no pytest).

## 6. Bundled snippets

`src/CsCode.Editor/Snippets/Builtin/<language id>.json` are embedded and registered with the built-in snippets; later stages add their
languages' files there. A user snippet with the same prefix (or name) replaces the built-in one. Python's set (48 snippets: control flow,
functions, classes, dataclass, `main`, comprehensions, `match`, context managers, pytest `test` / `param` / `fixture` / `raises`, unittest
`testcase`, typing) is original to CsCode: rafamadriz/friendly-snippets (MIT) was the brief's candidate, but fetching it would have been a
download nobody had approved; it can replace or extend this set later.

## 7. Running code

**Run Python File in Terminal** (`python.execInTerminal`): saves, then runs the file with its folder's interpreter in the "Python" terminal.
**Run Selection/Line in Python REPL** (`python.execSelectionInTerminal`, Shift+Enter in a Python editor): the selection, else the caret's line,
dedented, in one REPL terminal per interpreter.

## 8. Running the suites

```bash
tools/fetch-lsp-tools.sh --with-python   # pyright, basedpyright (npm) and Ruff (venv)
tools/fetch-debugpy.sh                   # bundled debugpy, and debugpy + pytest in .scratch/tools/python/venv
dotnet test tests/CsCode.Lsp.Tests --filter "FullyQualifiedName~Python"
dotnet test tests/CsCode.Dap.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~Debugpy"
dotnet test tests/CsCode.Panels.Testing.Tests --filter "FullyQualifiedName~PythonProviderTests"
dotnet test tests/CsCode.Platform.Tests --filter "FullyQualifiedName~PythonEnvironmentServiceTests"
dotnet test tests/CsCode.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~PythonCompositionTests"
```

Each real-tool test skips with its reason when its tool is missing. `PythonCompositionTests` checks the stage's "Done when" over the real
composition root: a `.venv` folder's interpreter, Pyright and Ruff diagnostics, a save running fix-all, organize imports and Ruff's format,
and debugpy stopping at a breakpoint with that interpreter.

## 9. Limits

* Remote workspaces: interpreters are discovered on this machine only; bridged providers carry no server id for `editor.defaultFormatter`.
* Attach by process id on macOS (§4). No auto-save in CsCode, so `"always"` code actions run only on explicit saves too.
* Changing the interpreter does not restart Ruff to pick a different environment's `ruff` (restart the server).
* Not in this stage: Jupyter notebooks, a DataFrame viewer, Pylance-only features, coverage.
