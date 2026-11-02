# Language stages — overview (wave after the macOS port)

**User request (2026-11-01):** "Lets break this up into stages/languages: 1. Python full feature set 2. Rust 3. C 4. C++ 5. C# 6. Zig" —
following a gap review of CsCode against a top-tier editor for those languages.

Each stage has its own brief. Read `docs/briefs/00-common.md` first (rules, threading, memory, honesty), then this page, then the stage's brief.

| Stage | Brief | Builds on | Introduces for later stages | Downloads that need the user's go-ahead |
|---|---|---|---|---|
| 1 | [lang-1-python.md](lang-1-python.md) | — | `editor.defaultFormatter`, `editor.codeActionsOnSave`, `envFile`, bundled snippets | pyright, basedpyright, ruff, debugpy, pytest |
| 2 | [lang-2-rust.md](lang-2-rust.md) | 1 | Disassembly / Memory / Registers windows, data breakpoints, `ITaskProvider`, the per-server LSP extension pattern, `inlayHint/resolve` | none (all installed) |
| 3 | [lang-3-c.md](lang-3-c.md) | 1, 2 | CMake integration, CTest provider, clangd extensions, `documentLink` | cmake-language-server (optional), bear (optional) |
| 4 | [lang-4-cpp.md](lang-4-cpp.md) | 3 | GoogleTest / Catch2 providers | GoogleTest and Catch2 for test fixtures (CMake `FetchContent` in test temp dirs) |
| 5 | [lang-5-csharp.md](lang-5-csharp.md) | 1 (2 for tasks) | read-only virtual documents for decompiled / source-generated code | Roslyn language server |
| 6 | [lang-6-zig.md](lang-6-zig.md) | 1, 2 | — | zig, zls |
| 7 | [new-project.md](new-project.md) | 1–6 (their tools, debuggers, the Python interpreter service, CMake) | `IProjectGenerator` (Core; another module adds a generator by registering one), the `dotnet new` template listing (`DotnetTemplates`), `IViewActivator.CloseViewAsync`, `workbench.action.newWindow` with paths | zig (installed with Homebrew); typescript and @types/node, pytest (at creation, in the user's new project) |

**Order.** The stages run in this order because each uses what the one before it built: 1 builds the formatter choice and code actions on
save, which Ruff needs first and Rust, C# and Zig reuse. 2 builds the native debugging windows and task auto-detection that C, C++ and Zig
use. 3 builds CMake and CTest, on which C++'s test providers sit. Stage 5 (C#) depends only on stage 1 and could run alongside 2–4 if the
user wants it sooner; stage 6 (Zig) needs stage 2.

## Rules for every stage
* **Branch** `lang/<language>` (`lang/python`, `lang/rust`, `lang/c`, `lang/cpp`, `lang/csharp`, `lang/zig`), cut from `main` after the
  stage it builds on is merged.
* **On this Mac:** the .NET SDK lives in `~/.dotnet`; tool shells must `export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"`.
  Run targeted test projects, not the whole suite; the App, Sessions, Remote, Dap and Panels.Terminal test projects can hang here, so run
  them with `--blame-hang-timeout 5m`. Gate macOS-specific behaviour without changing Linux behaviour.
* **Downloads** only after the user says yes (asked at the start of the stage, listing name, source, version and size). Pinned versions,
  digests checked, into `.scratch/tools/` through a `tools/fetch-*.sh` script, as `tools/fetch-codelldb.sh` does. Homebrew's Python is
  externally managed: Python tools go in a venv under `.scratch/tools/python/`, never into Homebrew's site-packages.
* **Real beats mock:** every new server, adapter and test provider gets a real-tool suite (`Trait("Category", "RealTools")` for servers and
  tools, `"RealAdapter"` for debug adapters) that skips with its reason when the tool is missing.
* **Docs:** each stage writes `docs/<LANGUAGE>.md` (C and C++ share `docs/C-CPP.md`) with what works, the evaluation matrices with
  evidence, and the limits; and regenerates the matrix in `docs/LANGUAGES.md`:
  `CSCODE_UPDATE_LANGUAGES_MD=1 dotnet test tests/CsCode.App.Tests --filter LanguageMatrixTests`.
* **Done means seen in the real app on this Mac**, not only tests: each brief's "Done when" lists what to check in the window.

## Not in these six stages
TypeScript / JavaScript / HTML / CSS (ESLint, Biome/Prettier, Emmet, auto-close tags, Tailwind, browser debugging, npm scripts), Jupyter
notebooks, AI inline completion, user-dropped TextMate grammars and `languages.json` (LANGUAGES.md §4 says nothing reads them yet), and the
multi-cursor gaps in docs/EDITOR.md §8. Each would be a stage of its own.
