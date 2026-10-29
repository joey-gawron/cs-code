# Module brief — Theme dropdown, more languages, tree-sitter evaluation — **wave 4** (after the Rust debugger)

**User request (2026-10-29):** "After the rust debugger gets finished could you add a drop down for selecting color themes and add a bunch more
support for different programming languages and also explore if it would make sense to switch to tree sitter for code coloring."

Read `docs/briefs/00-common.md` first (rules, threading, memory). **No extension system**: everything below is built in or file-based.
**No downloads** without the user's go-ahead (asked at launch time, one per artifact, like js-debug).

## State today (checked 2026-10-29)
* **Themes:** only the three VS2022 themes (`vs2022-dark`, `vs2022-blue`, `vs2022-light`, `CsCode.Ui/Theming/VsThemes.cs`) are registered, plus
  file-based user themes. They are chosen through the palette command *Color Theme* (quick pick with live preview). `TextMateSharp.Grammars` 2.0.3,
  already a dependency, embeds 21 VS Code themes (MIT) that nothing registers: Dark+, Light+, Dark (VS), Light (VS), Visual Studio Dark/Light,
  Monokai, Dimmed Monokai, Dracula, One Dark, Atom One Dark/Light, Solarized Dark/Light, Abyss, Kimbie Dark, Quiet Light, Red, Tomorrow Night Blue,
  High Contrast Dark/Light.
* **Languages:** about 50 TextMate grammars (VS Code's built-in extensions, embedded by TextMateSharp.Grammars). Another 17 languages are detected but
  have **no highlighting**: TOML, Kotlin, Scala, Zig, Protobuf, GraphQL, Haskell, Elixir, Erlang, OCaml, Vue, Svelte, Terraform, Nix,
  reStructuredText, Assembly, CSV/JSON Lines (`CsCode.Editor/Languages/BuiltinLanguages.cs` supplement table). 12 built-in language-server
  definitions (`CsCode.Lsp/Manager/BuiltinLanguageServers.cs`): C#, TypeScript/JavaScript, Python, C/C++, Rust, Go, JSON, HTML, CSS, Bash, YAML, Lua.
* **Tree-sitter on this machine (offline prototype possible):** `libtree-sitter.so` 0.26.11 in the Nix store
  (`/nix/store/9i9i8k9mfzqpa749j3jdfw2vyxhys0k0-tree-sitter-0.26.11/lib`), about 370 compiled grammar packages
  (`/nix/store/*-tree-sitter-<lang>-*/parser` + `queries/highlights.scm`, from the user's Neovim/Helix setups), the `tree-sitter` CLI 0.27.0
  (`~/.cargo/bin/tree-sitter`) and gcc. Read-only use of these is fine; do not modify the Nix store or the user's editor configs.

## A. Theme dropdown (MUST)
1. A **Color Theme dropdown** (ComboBox) on the main toolbar, after the debug configuration dropdown. It can be hidden like the other toolbar groups
   (View > Toolbars). The same dropdown sits at the top of the Settings panel (Environment / Appearance), where Visual Studio has it
   (Tools > Options > Environment > General). *Placement is the integrator's default; the user may move it.*
2. Lists every installed theme (built-in and user), grouped Dark / Light / High contrast, with a colour swatch per entry. Arrowing through the open list
   previews live; Escape reverts; choosing applies and persists `workbench.colorTheme`. It stays in sync with every other way of changing the theme
   (palette command, `settings.json` edit, settings sync of another window).
3. Register the 21 embedded TextMateSharp themes as full CsCode themes: their token colours as they are, and their `colors` (workbench colours) mapped
   onto the VS2022 chrome keys the shell uses. Fill missing keys from the nearest VS2022 base (dark/light/high contrast), so no chrome element is ever
   unthemed. Screenshot every theme (headless) and check text/background contrast in the chrome (WCAG AA for normal text, flag failures).
4. Switching themes allocates no per-switch garbage that survives (100 switches: managed heap flat) and keeps the dispatcher budgets.

## B. More languages (MUST)
1. `docs/LANGUAGES.md`: one support matrix row per language with detection, highlighting (source), language configuration (comments, brackets,
   auto-closing, indentation, word pattern), snippets, folding, language server (+ install hint), formatter, debugger. It is generated or checked by a
   test, so it cannot drift.
2. **Highlighting for every detected language.** The source depends on part C: tree-sitter grammars if C recommends them, otherwise TextMate grammars
   (third-party grammar files need the user's OK to download, one licence check per file).
3. **Built-in language-server definitions** for widely used servers, each found on `PATH` (or a setting) with an install hint, never installed
   without asking: Java (`jdtls`), Kotlin (`kotlin-language-server`), Scala (`metals`), Zig (`zls`), TOML (`taplo`), Markdown (`marksman`),
   Nix (`nil` / `nixd`), Terraform (`terraform-ls`), Ruby (`ruby-lsp`), PHP (`phpactor`), Haskell (`haskell-language-server-wrapper`),
   Elixir (`elixir-ls` / `lexical`), Erlang (`erlang_ls`), OCaml (`ocamllsp`), Swift (`sourcekit-lsp`), Dart (`dart language-server`),
   Dockerfile (`docker-langserver`), CMake (`cmake-language-server`), SQL (`sqls`), Protobuf (`buf lsp` / `pls`), GraphQL, Vue (`vue-language-server`),
   Svelte (`svelteserver`), Clojure (`clojure-lsp`). Every server that is installed on this machine gets a real smoke test (initialize, hover,
   diagnostics, shutdown, no leftover process); the others get configuration tests. Servers run on the Language lane, below normal priority (the
   threading rule the user asked for).
4. Language configurations (comments, brackets, auto-closing pairs, indentation rules, word pattern) for every language added.

## C. Tree-sitter evaluation (explore; a switch only with the user's OK)
Deliverable: `docs/TREE-SITTER-EVALUATION.md` with measurements and a recommendation, either **switch**, **hybrid** (tree-sitter where a grammar
exists, TextMate as the fallback) or **stay**, plus a prototype behind the setting `editor.experimental.treeSitter` for C#, TypeScript, Rust,
Python and Markdown (with injections).
* **Quality:** side-by-side screenshots against TextMate for C#, TypeScript/TSX, Rust, Python, C++, HTML with embedded JS/CSS, Markdown with fenced
  code; where each is better or worse (semantic tokens from LSP paint over both).
* **Performance and memory** on the editor's own benchmarks: open 1/10/100 MB files, per-keystroke incremental re-highlight latency (p50/p99),
  memory per open document (syntax tree versus `LineTokenStore`), a 300-document soak. The editor's existing budgets (10 MB open, UI p99 < 16 ms)
  are the bar.
* **Threading:** parsing and queries on the editor worker, never the UI thread. Trees are not thread-safe, so the prototype copies them (or
  confines them) per thread. Cancellation of stale parses.
* **Theming:** map capture names (`@keyword`, `@function.method`, …) onto TextMate scopes, so that every existing theme, including part A's, colours
  tree-sitter output without per-theme work.
* **What else it unlocks:** syntax-aware folding, expand/shrink selection, document outline without a language server, indentation, sticky scroll,
  and better Vim text objects. Say which of these are worth doing.
* **Distribution and risk:** grammars are native code per platform (linux-x64/arm64, win-x64, osx-arm64). Compare building them in CI from pinned,
  licence-checked sources with WebAssembly grammars run through tree-sitter's wasm support. ABI-version mismatches. A crash inside a native grammar
  takes the process down: is isolation needed, and what does it cost? Licences of the grammars and queries (most are MIT; list the exceptions).
  .NET binding: a thin own P/Invoke layer over the C API versus existing bindings (maturity, maintenance).

## Order
After the Rust/GDB debugger round. A (themes) and C (evaluation) run in parallel, as does the language-server and language-configuration half of B.
B's highlighting waits for C's recommendation and the user's choice. Background mode + saved sessions (`docs/briefs/background-sessions.md`) touches
other files and can run alongside.

## Done when
The theme dropdown works on the real window (integrator screenshots several themes); every detected language is highlighted or has a documented
reason it is not; `docs/LANGUAGES.md` and `docs/TREE-SITTER-EVALUATION.md` exist with evidence; all tests are green; everything is committed
without attribution trailers.
