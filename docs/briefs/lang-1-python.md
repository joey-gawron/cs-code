# Stage brief — 1. Python: full feature set

**User request (2026-11-01):** "Lets break this up into stages/languages: 1. Python full feature set …" (overview: `docs/briefs/languages.md`).

**Owns:** `src/CsCode.Lsp/Manager/**` (Ruff and basedpyright definitions), `src/CsCode.Dap/**` (debugpy adapter factory, installer and
quirks; `envFile` in `Configuration/`), new `src/CsCode.Testing/Providers/Python/**`, the editor's formatting and save features
(`src/CsCode.Editor/Features/FormattingFeature.cs`, a new code-actions-on-save participant), bundled snippets (`src/CsCode.Editor/Snippets/**`
plus embedded resources), a Python environment service (additive Core contract + implementation), shell wiring in `src/CsCode.App/**`
(status bar item, commands, menus), tests next to each, `tools/fetch-debugpy.sh`, `tools/fetch-lsp-tools.sh` (Ruff, basedpyright),
`docs/PYTHON.md`, the regenerated matrix in `docs/LANGUAGES.md`, `THIRD-PARTY-NOTICES.md`.

## What is already there (verify, don't rebuild)
* Pyright (`pyright-langserver --stdio`) is the built-in Python server (`BuiltinLanguageServers.cs`; root markers `pyrightconfig.json`,
  `pyproject.toml`, `setup.py`, `setup.cfg`, `requirements.txt`). It does not format, so Python has no formatter today.
* TextMate highlighting (tree-sitter as an option), off-side folding, VS Code's Python language configuration. No built-in snippets.
* **Several servers per document already work:** `LanguageServerManager.Route` attaches every enabled definition whose selector matches
  (`src/CsCode.Lsp/Manager/LanguageServerManager.cs:242`); completion and folding merge providers. Formatting does not choose:
  `FormattingFeature.FirstResultAsync` (`FormattingFeature.cs:82`) takes the first provider that answers.
* Hooks to build on: `IWillSaveParticipant` (`src/CsCode.Core/Languages/AdditionalLanguageProviders.cs:59`) for edits before save;
  `IDebugConfigurationProvider` (`src/CsCode.Core/Debugging/IDebugService.cs:53`) to resolve launch configurations; the Test Explorer's
  providers in `src/CsCode.Testing/Providers/{DotNet,Rust,JavaScript}` as templates; the status bar in `src/CsCode.Core/Platform/WindowServices.cs`.
* Watcher exclusions already know virtual environments (`pyvenv.cfg`) and Ruff / pytest / mypy caches (`WatchExclusion.cs`).
* Nothing handles `envFile` in launch configurations (no adapter, no resolver).

## Toolchain on this Mac (checked 2026-11-01)
Python 3.14.7 (Homebrew, `/opt/homebrew/bin/python3`, externally managed), pip 26.2.1, Node 24.22.2 / npm 11.19.1. **Not installed:**
pyright, basedpyright, ruff, debugpy, pytest, uv. Ask the user before fetching any of them (languages.md, "Downloads").

## MUST
1. **Formatter choice and code actions on save (later stages reuse these).**
   * `editor.defaultFormatter`, global and per language (`"[python]": { "editor.defaultFormatter": "ruff" }`); the value is a language
     server id (the `cscode.languageServers` key). Format Document / Selection / on save / on paste / on type use only that server. When it
     is unset, keep today's behaviour. **Format Document With…** opens a quick pick of the servers that can format the document.
   * `editor.codeActionsOnSave`: the object form (`{"source.organizeImports": "explicit", "source.fixAll": "always"}`), the legacy
     booleans and the array form. It runs as an `IWillSaveParticipant` before format on save. Kinds match hierarchically
     (`source.fixAll` matches `source.fixAll.ruff`). Each save has a deadline (`editor.codeActionsOnSaveTimeout`, default 750 ms). Actions
     are resolved before they are applied, as one undo unit. `explicit` runs on a save the user asked for, not on auto-save, as in VS Code.
2. **Python environments.**
   * **Discovery**, on the IO lane, bounded (each probe ≤ 2 s, then killed) and cached per workspace: workspace `.venv` / `venv` / `env`
     (`pyvenv.cfg`), `VIRTUAL_ENV`, `CONDA_PREFIX` and conda envs, Poetry (`poetry env info -p`, only when `poetry` exists), pyenv
     versions, Homebrew / system `python3`, and `py -0p` on Windows.
   * **Python: Select Interpreter** quick pick (version, kind, path, "Enter interpreter path…"). The choice is persisted per workspace
     folder, with `python.defaultInterpreterPath` as the fallback. A status bar item shows it; clicking it opens the picker.
   * **The choice feeds** Pyright / basedpyright (`python.pythonPath` through `workspace/configuration`, then
     `workspace/didChangeConfiguration`), debugpy (`python`), the pytest provider, and new terminals (the venv's `bin` prepended to PATH
     and `VIRTUAL_ENV` set; setting `python.terminal.activateEnvironment`, default on).
3. **Ruff** as a second built-in Python server.
   * Launched as `ruff server` (check the minimum version that ships the native server), with root markers `pyproject.toml`, `ruff.toml`
     and `.ruff.toml`.
   * It provides diagnostics, Quick Fixes, `source.fixAll.ruff`, `source.organizeImports.ruff` and formatting.
   * Add per-platform install hints (`brew install ruff`, `pipx install ruff`, `uv tool install ruff`).
   * The matrix's Formatter column for Python becomes Ruff.
4. **debugpy** as a built-in debugger, types `debugpy` and legacy `python`.
   * **Adapter:** `<interpreter> -m debugpy.adapter` when the selected interpreter has debugpy; otherwise a pinned bundled debugpy run as
     `<interpreter> <bundled>/debugpy/adapter`, with an installer that asks before downloading, like `NetCoreDbgInstaller`.
   * **Launch:** `program`, `module`, `args`, `cwd`, `env`, `envFile`, `console` (integratedTerminal / internalConsole), `justMyCode`,
     `stopOnEntry`, `subProcess`, plus `django` / `jinja` / `flask` passed through.
   * **Attach:** `connect` host / port, and `processId`. Attaching by pid injects through the OS debugger, so document what macOS needs.
   * **Initial configurations:** Current File, Module and Attach, plus configuration snippets.
   * **Breakpoints:** exception filters (`raised`, `uncaught`, `userUnhandled`), conditional, hit-count and logpoints.
   * **Child processes** (`subProcess`, multiprocessing) are announced by a debugpy custom event that the client answers with an attach
     child session. Find the event's exact name and shape in debugpy's sources and handle it.
   * **Evaluation matrix in `docs/PYTHON.md`,** run against the real adapter as `docs/DEBUGGING-RUST-TS.md` did: launch and clean exit (no
     leftover processes), line / conditional / hit / log / exception breakpoints, steps, pause, call stack (async and generators),
     Locals / Watch (dict, list, set, dataclass, class instance, large collections paged), set variable, REPL evaluate with completions,
     threads, multiprocessing, attach, and a 1M-line output flood. Fix what is fixable, with regression tests.
5. **`envFile` for every debugger,** resolved client-side in `DebugConfigurationService` before the provider chain, as VS Code's language
   extensions do: `KEY=VALUE` lines, comments, quotes and `${env:…}` expansion; `env` wins over the file.
6. **pytest and unittest in the Test Explorer** (`Providers/Python`).
   * **Detection:** pytest when the selected interpreter has it or `pytest.ini`, `[tool.pytest.ini_options]` or `conftest.py` exists;
     otherwise unittest.
   * **Discovery** runs with the selected interpreter (pytest `--collect-only -q` node ids; unittest through a small discovery script), plus
     a source scan for locations as `RustTestScanner` does. Parametrized cases are children of their test.
   * **Runs** use `--junitxml` for results, durations and failure messages; tracebacks become stack locations (`Engine/StackTraces.cs`).
   * **Run / debug** a test, a class or a file. Debugging goes through debugpy (`module: pytest`, `purpose: ["debug-test"]`).
   * "Run Test | Debug Test" code lenses as the other providers have, and the settings `python.testing.pytestArgs`, `unittestArgs` and `cwd`.
7. **Bundled snippets (later stages add their languages' files).**
   * Snippet files embedded as resources and registered at built-in rank, so user snippets still win.
   * Source: a licence-checked set (rafamadriz/friendly-snippets is a candidate; check its licence and record it in `THIRD-PARTY-NOTICES.md`).
   * Python's set first.

## SHOULD
* basedpyright as an alternative to Pyright. It adds semantic highlighting and inlay hints, which Pyright's open-source server lacks (check
  both against current releases). Which one is the default is the user's call: record the comparison in `docs/PYTHON.md` and ask.
* **Run Python File in Terminal**, and **Run Selection/Line in Python REPL** (Shift+Enter, reusing one REPL terminal per interpreter).
* `pyproject.toml`: check that Taplo validates it against its schema; document if not.

## Out of scope (a stage of its own if wanted)
Jupyter notebooks (`.ipynb` editor and kernel protocol), a DataFrame / variable viewer, Pylance-only features, test coverage gutters.

## Threading & isolation (binding)
00-common applies. Interpreter discovery and test discovery run processes on the IO lane, bounded and cancellable, never on the UI thread.
debugpy and Ruff run at below-normal priority like every child process. The hostile-server budgets (dispatcher p99 < 16 ms) apply with
Pyright, Ruff and debugpy all running.

## Done when
In the real app on this Mac:
* A project with a `.venv` gets its interpreter picked up and shown in the status bar.
* Pyright and Ruff diagnostics both show. Ruff formats, and fix-all plus organize imports run on save.
* F5 debugs the current file and a pytest test through debugpy.
* The Test Explorer lists, runs and debugs pytest tests (including parametrized ones) and a unittest project.
* `docs/PYTHON.md` has the debug matrix with evidence. The real-tool suites pass or skip with their reasons. `docs/LANGUAGES.md` is
  regenerated. Committed without attribution trailers.
