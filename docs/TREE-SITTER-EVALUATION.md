# Tree-sitter evaluation (brief `docs/briefs/themes-languages-treesitter.md`, part C)

**Question (user, 2026-10-29):** *"explore if it would make sense to switch to tree sitter for code coloring."*

**Answer: hybrid, staged — not a switch, and not yet the default.** Keep TextMate as the default engine. Ship tree-sitter as an
opt-in engine now (the prototype in this branch, setting `editor.experimental.treeSitter`), use it to give the 17 detected-but-uncoloured
languages highlighting, and make it the default per language only after the distribution decision below (§8) and a re-run of the benchmarks
on a quiet machine. The bigger payoff is not colours but structure: folding, expand/shrink selection, outline and Vim text objects without a
language server (§7). Nothing switches without the user's OK; the default stays TextMate.

**Status (wave 3, 2026-10-30): decided and built — see docs/TREE-SITTER.md.** The user chose the hybrid (§10.1), CI-style native grammars
built from pinned, licence-checked sources (§10.2: `tools/treesitter/manifest.json`, `fetch-grammars.sh`, `build-grammars.sh`, shipped as
`tree-sitter/` next to the app), nvim-treesitter's queries with a NOTICE (§10.3), and "the 17 first" (§10.4: `editor.syntaxEngine` = `auto`
colours exactly the languages TextMate cannot; the prototype's `editor.experimental.treeSitter` is now an alias). Crash isolation (§10.5) is in
process with a syntax lane of its own, a hang watchdog, a crash quarantine and a query guard; out-of-process parsing was measured and not
adopted (docs/TREE-SITTER.md §4, benchmark B6). Structural features (§10.6): Expand/Shrink Selection, folding and an outline/breadcrumbs are
built, and sticky scroll (off by default); Vim text objects are not. The text below is the evaluation as it was written (its "prototype" is today's engine).

Everything below was measured on this machine with what is installed (libtree-sitter 0.26.11, the grammar packages and nvim-treesitter's
queries in the Nix store, read-only). **All performance numbers are PROVISIONAL**: they were taken while five other agents loaded the 22-core
machine (1-minute load average 5–34, recorded next to every number in the raw results and in the tables). `tools/bench-treesitter.sh` repeats the whole set in
one command; the integrator should re-run it on a quiet machine and replace the tables in §4.

Contents: [1 Recommendation](#1-recommendation) · [2 The prototype](#2-the-prototype) · [3 Quality](#3-quality) ·
[4 Performance and memory](#4-performance-and-memory) · [5 Threading](#5-threading) · [6 Theming](#6-theming-capture--textmate-scope) ·
[7 What else it unlocks](#7-what-else-it-unlocks) · [8 Distribution, risk, licences](#8-distribution-risk-licences) ·
[9 .NET binding](#9-net-binding) · [10 Decisions for the user](#10-decisions-for-the-user) · [11 Reproducing](#11-reproducing-it) ·
[12 Prototype limits](#12-prototype-limits-honest-list)

## 1. Recommendation

| Option | Verdict | Why |
|---|---|---|
| **Switch** (tree-sitter for everything, drop TextMate) | **No** | A native grammar per language per platform must be built, licence-checked and shipped for every language (§8); first colours of a big file wait for a whole-document parse (§4 B2: 0.7 s for 1 MB of C#, 5 s for 10 MB, where TextMate colours the viewport in 0.2–0.5 s); C#'s grammar is the slowest one measured, C# is the editor's main language, and **one valid C# 11 construct the grammar does not know (`['f', .. var n]`, used in this repository) turns the rest of the file into an error region: on 116 K characters the parse becomes 46× slower and a keystroke's reparse 1,000× slower (5 ms → 5 s)** (§4 B1c). |
| **Hybrid** (tree-sitter where a grammar is installed and enabled, TextMate otherwise) | **Yes — staged** | Same token rows, same themes, same features: the prototype proves the engines are interchangeable per document with zero per-theme work (§6). Tree-sitter colours structure TextMate's regexes cannot see (types, properties, namespaces, calls — closer to what VS Code shows with semantic tokens), handles injections well (Markdown fences, HTML script/style, §3), parses and highlights a whole 1 MB file 3–17× faster than TextMate tokenises it (§4 B1b), and grammars + queries for **all 17 languages CsCode cannot colour today** are on this machine and compile with the prototype (test `Grammars_for_the_languages_TextMate_cannot_highlight_load_and_compile`). |
| **Stay** (TextMate only) | No | Leaves the 17 languages uncoloured unless third-party TextMate grammars are downloaded (one licence check per file), and leaves the structural features in §7 to language servers. |

Staging proposal (each step needs the user's go-ahead):
1. **Now (this branch):** the engine behind `editor.experimental.treeSitter` (default `false`, per-language `"[rust]": {…}` works), using the runtime
   and grammars found on the machine. No downloads, nothing shipped.
2. **Decide distribution** (§8, §10): build the grammars in CI from pinned, licence-checked sources (recommended) or wasm grammars.
3. **Hybrid default for languages without TextMate colouring** (the 17), then for Rust, TypeScript/TSX, Python, Markdown and HTML where
   tree-sitter's colouring is richer; keep C# on TextMate (+ Roslyn semantic tokens from the language server) until its grammar is faster.
4. **Structural features** (§7) on top of the same trees: folding, expand/shrink selection, outline, Vim text objects, sticky scroll.

**The finding that matters most for C#:** tree-sitter's robustness depends on the grammar keeping up with the language. The C# grammar
(v0.23.5, the newest on this machine) cannot parse one file of this repository (a C# 11 list pattern with a slice designation,
`['f', .. var n] when …`, in `KeyboardShortcutsViewModel.cs`). Everything after it becomes one ERROR region (colours there degrade), and
error recovery explodes (B1c, through this engine): with that 10-line construct at the top of 116 K characters of otherwise trivial C#
the first parse takes 7.6 s instead of 0.16 s and a reparse after one keystroke 5.1 s instead of 5 ms (nothing after the error is reusable);
in front of 1.2 MB the first parse took **9.3 minutes** (an uncapped run; capped runs stop at 120 s) instead of 1.9 s. Plain C against the
same library shows the same (1.1 MB: 9 s instead of 1.3 s, keystroke reparse 7.8 s instead of 23 ms), so it is the grammar/library, not the
binding. TextMate degrades locally (one
wrong colour) on syntax it does not know. The prototype therefore hands a document to TextMate when it has parsed for 5 s of worker time
without producing a tree (`TreeSitterHighlighter.MaxParseTimeWithoutTree`, tested), but for a fast-moving language this is the strongest
argument to keep TextMate (plus the language server's semantic tokens) as C#'s engine.

## 2. The prototype

* **Setting:** `editor.experimental.treeSitter` (boolean, default `false`; registered with `IConfigurationService` so the Settings panel lists it;
  works per language: `"[rust]": { "editor.experimental.treeSitter": true }`). Switching is live in both directions. Languages:
  C#, TypeScript, TSX, JavaScript(React), Rust, Python, Markdown (with injections), C, C++, HTML (with JS/CSS injections), CSS. Every other
  language, and every failure (no runtime, grammar missing, ABI mismatch, query that does not compile), stays on / falls back to TextMate; the
  reason is logged once per grammar. `CodeEditorControl.SyntaxEngine` says which engine colours a view.
* **Where:** `src/CsCode.Editor.TreeSitter/` (new, UI-free, no packages): `Native/TsApi.cs` (the P/Invoke layer: 48 of the runtime's 145 exported
  functions, blittable structs only), `Native/TsRuntime.cs` (finds and checks `libtree-sitter`, optional counting allocator), `TsLanguage.cs`
  (grammar loading + ABI check), `Queries/` (query compilation, predicates, Lua patterns), `Grammars/` (discovery, Neovim `; inherits:`,
  bundles, injection-language aliases), `Highlighting/` (`SyntaxDocument`: sliced incremental parser + highlighter, `CaptureScopes`,
  `PaintBuffer`). `src/CsCode.Editor/TreeSitter/` (integration): `TreeSitterService` (setting, registry, scope-stack adapter),
  `TreeSitterHighlighter` (per document, plugs into `TokenizationSession`), `DirtyLines`, `SnapshotTextInput`. Hooks in
  `TextMate/TokenizationSession.cs` and `TokenizationManager.cs` (the engine produces the same `(startColumn, scopeStackId)` rows into the same
  `LineTokenStore`, so the colorizer, `KindAt` (auto-closing), bracket matching and themes are unchanged).
* **Grammar and runtime discovery (read-only):** `CSCODE_TREESITTER_LIB` / `{UserDataDir}/tree-sitter/` / the system library path / the newest
  `/nix/store/*-tree-sitter-0.2x*/lib`; grammars from `{UserDataDir}/tree-sitter/grammars/<name>/parser` (+ `queries/`),
  `CSCODE_TREESITTER_GRAMMARS`, then the Nix store (the nvim-treesitter-pinned builds `tree-sitter-<name>-0.0.0+rev=…` first, which match
  nvim-treesitter's queries; queries from the nvim-treesitter plugin, then the grammar package's own `queries/`).
* **Tests** (`tests/CsCode.Editor.Tests/TreeSitter/`, real runtime and grammars, skip with the reason when missing; `tests/CsCode.App.Tests/TreeSitterCompositionTests.cs`):
  engine (runtime API check, ABI refusal of a fake ABI-12/ABI-99 grammar built with gcc, every prototype grammar loads and every capture maps to
  a scope, Neovim `inherits` order, Lua-pattern translation, scopes for C#/Rust/Python/TS/TSX, Markdown and HTML injections, incremental reparse
  = fresh parse, sliced parse = whole parse, cancellation from another thread, native memory returned on dispose, the 17 extra grammars);
  view (setting off = TextMate, on = tree-sitter **on the editor worker**, `KindAt` on tree-sitter tokens, incremental typing republishes only
  nearby lines, live switch both ways, per-language section, fallback without a grammar, theme switch re-colours without re-parsing, typing
  during the first parse of a 1.2 MB file abandons the stale parse and costs the UI thread nothing more than TextMate); quality (screenshots +
  colour agreement, §3); lifetime (40 documents opened/edited/closed: views, sessions, engines collectable and **native bytes back to the
  baseline**, counted by the allocation-tracking allocator; destroying the last view frees the tree); the give-up guard (a document the grammar
  cannot parse in time is handed to TextMate, a normal one is not); the real app (`AppServices` + Platform
  settings + document well: the setting is registered and switches an open editor through `settings.json`).

## 3. Quality

Side-by-side PNGs (headless, Dark+, same document, the setting switched live on the same view; rainbow brackets off so only the engine differs)
are written by `TreeSitterQualityTests` to `.scratch/screenshots/treesitter/<language>.png` (git-ignored; regenerate with
`dotnet test tests/CsCode.Editor.Tests --filter TreeSitterQualityTests`). The same test compares the foreground colour of every non-blank
character between the engines (Dark+) and writes the most frequent differences to `.scratch/treesitter/quality-<language>.md`.

| Language | Same colour as TextMate | Where tree-sitter is better | Where it is worse / different |
|---|---:|---|---|
| C# | 92.1 % | generic type arguments, attribute names, constructor/method names at call sites are consistent | XML doc comments are one comment colour (TextMate colours `<summary>` tags); `using`/`namespace` keyword categories differ slightly. Roslyn semantic tokens paint over both anyway |
| TypeScript | 86.0 % | enum members, properties, type parameters, built-in values (`undefined` as a constant) | `const` variables are not distinguished from `let` (TextMate: `variable.other.constant`); `async` is purple like `await` (one Neovim capture for both) |
| TSX | 89.2 % | JSX tags/attributes, embedded expressions inside JSX | `{…}` braces in JSX are plain brackets (TextMate: embedded-section punctuation) |
| Rust | 87.0 % | field/property access, `Self`, macros, enum variants, lifetimes are categorised | lifetime tick `'` coloured as modifier; `derive` names coloured as macros |
| Python | 61.2 % | the most visible change: variables, parameters, properties, module names and types are coloured (TextMate leaves them default) — close to VS Code + Pylance semantic highlighting | f-string `{}` punctuation and `self` use different scopes; whether "more colour" is better is a taste call |
| Markdown | 91.0 % | fenced code blocks are coloured by the real grammar of their language (Rust, Python… via injections), table headers, task-list boxes | fence info string is a label, not TextMate's language scope |
| C++ | 77.0 % | templates, namespaces, members and calls; `[[attributes]]` | `static_cast`/`noexcept` categories differ; macro constants coloured as constants |
| HTML (+JS/CSS) | 84.0 % | `<script>`/`<style>` are real JavaScript/CSS trees (injections), `document`/`console` built-ins | CSS font names are plain; `<title>` text is coloured as a heading (Neovim's choice) |

Semantic tokens from a language server (C#/Roslyn, TypeScript, Rust-analyzer, Pylance-like servers) paint over both engines, so for languages
with a good server the visible difference shrinks further. Where the engines disagree it is mostly *more* categories on the tree-sitter side,
which comes from the queries (nvim-treesitter's), not the engine: per-language overrides in `CaptureScopes` already align the common cases with
each language's VS Code TextMate grammar and can be tuned further.

## 4. Performance and memory

**PROVISIONAL — loaded machine (22 cores, load average 5–34 from five other agents; the value at each measurement is in the raw files).**
Release build, `tools/bench-treesitter.sh`, raw results: `.scratch/bench/treesitter-<run>.md`. TextMate here is TextMateSharp 2.0.3 with
Oniguruma, exactly as the editor runs it; tree-sitter is libtree-sitter 0.26.11 with the nvim-treesitter-pinned grammars and queries.

Raw files of the runs used here (all `.scratch/bench/`, git-ignored): `treesitter-20261029-155236.md` (B1b, B4 timing),
`-161844` (B1c), `-162128` (B1), `-164459` (B2/B3), `-164127` (B4 memory), `-164321` (B5 memory), `-163922` (B5 timing, tree-sitter).

**B1 — whole document, real C# (this repository's sources, files the grammar cannot parse left out), outside the editor.**

| | 1 MB | 10 MB | 100 MB |
|---|---:|---:|---:|
| tree-sitter full parse | 0.59 s | 14.2 s | 134 s |
| tree-sitter highlight one 60-line viewport (at 5 % / 50 % / 95 %) | 1.6 / 1.5 / 2.2 ms | 5.6 / 30 / 4.3 ms | 3.5 / 1.8 / 2.5 ms |
| tree-sitter incremental reparse after one keystroke (median of 3) | 4.7 ms | 98 ms | 0.9 s |
| tree-sitter tree, C-heap delta (≈ bytes per character) | 39 MB (39) | 356 MB (36) | 3.5 GB (35) |
| TextMate tokenise everything | 12.4 s | 88 s | ≈ 25 min (extrapolated from 4 % in 60 s) |
| TextMate `LineTokenStore` for everything (≈ bytes per character) | 2.7 MB (2.7) | 25 MB (2.5) | ≈ 1.7 GB |
| load average during the row | 11–13 | 12–23 | 26–34 |

**B1b — 1 MB of each evaluated language (its sample repeated): tree-sitter parse + highlight everything vs TextMate tokenise everything.**

| language | tree-sitter parse | tree-sitter highlight all (incl. injections) | TextMate tokenise all | TextMate / tree-sitter |
|---|---:|---:|---:|---:|
| C# | 1.41 s | 1.24 s | 29.4 s | 11× |
| TypeScript | 0.43 s | 0.89 s | 16.9 s | 13× |
| TSX | 0.80 s | 2.88 s | 25.7 s | 7× |
| Rust | 0.48 s | 0.96 s | 8.8 s | 6× |
| Python | 0.52 s | 0.98 s | 19.3 s | 13× |
| Markdown (+ inline, fences) | 1.59 s | 3.02 s | 12.5 s | 2.7× |
| C++ | 0.82 s | 0.86 s | 27.8 s | 17× |
| HTML (+ JS/CSS) | 0.16 s | 1.81 s | 11.5 s | 6× |

(load average 13–27 during these rows.)

**B1c — one construct the C# grammar does not parse** (a C# 11 list pattern `['f', .. var n]` at the top of trivial C#; each parse capped at 120 s).

| | 115 K characters | 1.2 M characters |
|---|---:|---:|
| full parse, without the construct | 0.16 s | 1.9 s |
| full parse, WITH it | 7.6 s (46×) | > 120 s (capped; an uncapped run took 557 s) |
| reparse after one keystroke, without | 5 ms | 83 ms |
| reparse after one keystroke, WITH it | 5.1 s (1,000×) | — |

**B2/B3 — in the editor** (real views, headless; a warm-up document first; for 10 MB the large-file and long-line limits and the give-up
guard are lifted, which the product does not do).

| | 1 MB TextMate | 1 MB tree-sitter | 10 MB TextMate | 10 MB tree-sitter |
|---|---:|---:|---:|---:|
| open → view laid out | 161 ms | 212 ms | 526 ms | 683 ms |
| open → **first coloured viewport** | **161 ms** | **733 ms** | **527 ms** | **5.1 s** |
| keystroke → viewport re-coloured on the worker, p50 / p99 | 19 / 75 ms | **8.7 / 12.6 ms** | 144 / 185 ms | 162 / 194 ms |
| keystroke handling on the UI thread, p50 / p99 (AvaloniaEdit's own cost dominates) | 1.2 / 4.4 ms | 1.8 / 8.4 ms | 5.9 / 33 ms | 6.5 / 35 ms |
| editor-worker jobs over 10 ms during the scenario / longest job | 43 / 98 ms | 14 / 38 ms | 82 / 50 ms | 132 / 76 ms |
| `LineTokenStore` (viewport + read-ahead) | 0.70 MB | 0.91 MB | 6.4 MB | 6.6 MB |
| syntax tree (native, B1 C-heap figure) | — | ≈ 39 MB | — | ≈ 356 MB |

(load average 5.4–6.4 during this run — the quietest of the session.)

**B4 — memory per open document** (20 documents × 99 K characters of C#, one view each, every line tokenised by both engines).

| per document | TextMate | tree-sitter |
|---|---:|---:|
| managed heap (incl. the AvaloniaEdit view and, for TextMate, its rule-stack checkpoints) | 7.4 MB | 3.2 MB |
| tree-sitter native (counted by the allocator) | — | 3.5 MB |
| `LineTokenStore` | 317 KB | 241 KB |
| total | ≈ 7.4 MB | ≈ 6.7 MB |

**B5 — soak:** 300 documents (the 8 samples, 6–20 K characters), each opened with a view, 5 keystrokes, the view destroyed, all closed at the
end: tree-sitter native memory peaked at 0.6 MB (trees are released with their last view) and ended 0.4 MB above the start (the per-language
injection parsers and query cursors the registry keeps); every session was closed (asserted). Managed deltas are dominated by which engine
ran first in the process (grammar caches, JIT): not comparable between engines here. 300 documents took 23 s (tree-sitter) and 37 s
(TextMate) with allocation tracking on.

**Reading the numbers**

* **Whole-document work:** tree-sitter parses and highlights a whole file 3–17× faster than TextMate tokenises it (B1b; TextMateSharp with
  Oniguruma manages only ~0.05–0.1 M characters/s here). But TextMate never does whole-document work up front: it colours the viewport
  first and reads ahead in the background, so its **first paint is immediate**, while tree-sitter must parse the whole document before the
  first colour: 0.7 s for 1 MB of C#, 5 s for 10 MB (B2). Below a few hundred KB (almost every real file) the difference is invisible; above,
  a production engine should keep TextMate's colours (or the previous tokens) on screen until the first parse completes.
* **Typing:** the UI thread does the same for both engines (record the edit, return); its cost is AvaloniaEdit's. Keystroke → re-coloured
  viewport on the worker is the incremental parse + one viewport query: **faster than TextMate at 1 MB** (p50 8.7 ms vs 19 ms, p99 12.6 vs
  75 ms) and equal at 10 MB (~160–190 ms for both). Both stay off the UI thread; neither touches the dispatcher budget.
* **Memory:** a syntax tree costs native memory for the whole document, ≈ 35–39 bytes per source character (B1), where TextMate's token store
  is ≈ 2.5 bytes per character but only for what has been tokenised. Per open 100 K-character document the totals are similar (B4: TextMate
  7.4 MB managed — its rule-stack checkpoints are big; tree-sitter 3.2 MB managed + 3.5 MB native). Trees are released with the last view
  (tested; B5 peaked at 0.6 MB of native memory over 300 documents) and rebuilt by a full parse when the document is shown again.
* **Large files:** both engines are off in large-file mode (> 5 MB, or a line over 20,000 characters) today. With the limits lifted, 10 MB
  of C# takes 5 s (quiet machine) to 14 s (loaded) before tree-sitter shows a colour and the tree holds ~350 MB; 100 MB takes over two
  minutes and 3.5 GB. Tree-sitter does not make the large-file mode unnecessary.
* **Robustness:** B1c — syntax the grammar does not know can make parsing pathological; the prototype bounds it (5 s give-up guard).

## 5. Threading

The binding rules (ARCHITECTURE §6) hold, and are tested:

* All tree-sitter work runs on the **editor worker** (`TreeSitterHighlighter.LastSliceThreadId` is asserted to be the worker's thread and never the
  UI thread). The UI thread only records the edit (`TreeSitterEditQueue`, a list append) and flips the cancellation flag.
* **Confinement, not copying:** tree-sitter parsers, trees and query cursors are not thread-safe; every one of them belongs to the worker (created,
  used and freed there; disposal from the UI thread is posted to the worker). Compiled queries and languages are immutable and shared. If another
  lane ever needs a tree (outline, folding on another thread), `ts_tree_copy` is O(1) and gives it its own copy.
* **Sliced parsing:** `ts_parser_parse_with_options` with a progress callback stops at the 6 ms slice deadline and resumes on the next slice,
  so the first parse of a large file never occupies the worker for more than a slice (the worker keeps serving other documents).
* **Cancellation of stale parses:** an edit sets the native cancel flag (the running slice stops at its next progress check) and a halted parse
  of an older snapshot is abandoned (`ts_parser_reset`) instead of finished — tested by typing into a 1.2 MB file during its first parse
  (`AbandonedParses > 0`).
* **Stale results are refused** exactly like TextMate's: every publication carries the document version.

## 6. Theming: capture → TextMate scope

Tree-sitter captures (`@keyword.conditional`, `@function.method.call`, `@variable.member`, `@markup.heading.1`, …) are mapped onto the
TextMate scopes VS Code's own grammars use for the same construct (`keyword.control.conditional`, `entity.name.function.member`,
`variable.other.property`, `markup.heading.1`, …) plus the language suffix, by longest dotted prefix (`CaptureScopes`), with per-grammar
overrides where a language's TextMate grammar uses a different scope (C#'s `int` is `keyword.type`, Python decorators are
`entity.name.function.decorator`, TypeScript's `${` is `punctuation.definition.template-expression`). Nested captures become nested scopes
(`string.quoted` > `constant.character.escape`), injected code gets its language's root scope inside the host's (`text.html.markdown` >
`markup.fenced_code.block` > `source.rust` > …). The result is interned into the **same scope-stack ids** as TextMate output, so:

* every theme — the VS 2022 themes, the 21 bundled VS Code themes, user theme JSON — colours tree-sitter output with **no per-theme work**
  (asserted for Dark+ and Light+; the colour-agreement numbers in §3 are computed through the real theme resolver);
* a theme switch re-colours without re-parsing (tested: same rows, same parse count);
* `KindAt` (string/comment detection for auto-closing) and bracket matching work on tree-sitter tokens (tested).

Every capture used by the prototype languages' queries maps to a scope (tested: none left unstyled).

## 7. What else it unlocks

With a syntax tree per open document on the worker, these become small features instead of language-server dependencies:

| Feature | Worth doing? | Notes |
|---|---|---|
| **Expand/shrink selection** (`editor.action.smartSelect.*`) | **Yes, first** | Today: `ISelectionRangeProvider` when a server offers it, else a word/line/block heuristic. From a tree it is "parent node" — exact, instant, every language with a grammar. Cheapest high-value feature. |
| **Folding** | **Yes** | Today: indentation + language-configuration markers + `IFoldingRangeProvider`. nvim-treesitter ships `folds.scm` for all these languages; folds become syntax-exact (multi-line arguments, initialisers, Markdown sections) without a server. |
| **Vim text objects** (`af`/`if`, `ac`/`ic`, `aa`/`ia` — function, class, argument) | **Yes** | nvim-treesitter-textobjects-style queries; the Vim engine (`CsCode.Vim`) needs a small host API (`IVimHost.SyntaxObjectAt`). Big win for Vim users; no server needed. |
| **Document outline / breadcrumbs without a server** | Yes | `tags.scm`/`locals.scm` exist for most grammars. Breadcrumbs today need `IDocumentSymbolProvider`; a tree-based fallback covers TOML, Markdown, YAML, shell… |
| **Sticky scroll** | Yes, after folding | The enclosing scopes of the first visible line are ancestor nodes — trivial with a tree; currently not implemented at all (docs/EDITOR.md §8). |
| **Indentation** | Later | `indents.scm` exists but Neovim's indent semantics are complex and the language-configuration rules work; low value per effort. |
| **Bracket pair colourisation / matching** | Optional | Already done by a fast scanner; a tree would make it exact inside strings/templates, minor gain. |

## 8. Distribution, risk, licences

**Native per platform.** A grammar is C code compiled per platform: linux-x64, linux-arm64, win-x64, osx-arm64 → 4 builds per grammar.
Sizes here: C# 5.3 MB, C++ 5.6 MB, TSX 1.5 MB, Rust 1.1 MB per platform (the parse tables). Shipping ~30 grammars ≈ 60–80 MB per platform;
on-demand download per language is the alternative (needs the user's per-artifact consent, like js-debug).

| Option | For | Against |
|---|---|---|
| **Build in CI from pinned, licence-checked sources** (recommended) | Native speed; reproducible (`tree-sitter build` / a C compiler per platform, grammar revision pinned next to its queries, as nvim-treesitter's lockfile does); licence checked once per pinned revision | A C toolchain per platform in CI; artifacts per platform; a crash in a grammar is a crash of the process (below) |
| **WebAssembly grammars** (tree-sitter's wasm store, grammars as `.wasm`) | One artifact for all platforms; a grammar cannot corrupt the process (sandboxed) | Needs a runtime built with wasm support (wasmtime linked in: **this machine's libtree-sitter 0.26.11 has no `ts_wasm_store_new`**, it was built without it), ~2–3× slower parsing (published figures, not measured here), larger runtime |

**ABI versions.** Grammars carry an ABI version (13–15 accepted by the 0.25/0.26 runtime). Mismatches are refused before use
(`TsLanguage.Load`; tested with fake ABI-12 and ABI-99 grammars). On this machine the prototype grammars are ABI 14 (TypeScript, TSX, HTML, C++
versioned build) and 15 (the rest). Pin the runtime and the grammar generator (`tree-sitter generate --abi`) together in CI.

**Crashes.** A bug in a grammar's C code (most have an external scanner in C) can take the whole editor down — no managed exception, no
recovery. Mitigations, cheapest first: (1) pin and fuzz the shipped grammars (`tree-sitter fuzz` exists in the CLI); (2) never parse untrusted
huge input without the large-file guard; (3) **isolation:** parse in a child process (the `CsCode.Server` model) — costs an extra process
(~30–50 MB), serialising edits in and token rows out per keystroke (~0.1–1 ms of IPC per viewport), and the complexity of a second tree owner.
Recommendation: no isolation for the shipped, pinned, fuzzed set; isolation (or wasm) only if user-supplied grammars are ever allowed.

**Licences** (`tree-sitter.json` metadata of the 214 grammar packages on this machine that carry one): MIT 177, Apache-2.0 15, ISC 5,
MPL-2.0 3 (inko, nim, uiua), **GPL-3.0 1 (caddy)**, **EUPL-1.2 1 (spade)**, CC0 2, BSD-2-Clause 1, 0BSD 1, dual/other 3, undeclared 5
(beancount, dart, gosum, janet_simple, requirements); ~150 older packages declare nothing and need a manual check. **All prototype
grammars are MIT** (C#, TypeScript/TSX, JavaScript, Rust, Python, Markdown, C, C++, HTML, CSS, JSDoc). The **runtime** is MIT. The
**queries** used here are nvim-treesitter's, **Apache-2.0** (NOTICE/attribution required when shipped; Helix's queries are MPL-2.0; each
grammar's own `queries/` are under the grammar's licence but are much less complete — TypeScript/TSX ship none in the Nix packages).
Exceptions to watch: GPL-3.0 and EUPL-1.2 grammars cannot be bundled into a non-GPL product; MPL-2.0 is fine file-by-file.

## 9. .NET binding

**Own thin P/Invoke layer (this prototype) — recommended.** 48 functions (`Native/TsApi.cs`, 250 lines), blittable structs only,
`LibraryImport` source-generated stubs, a DllImport resolver that loads and API-checks the runtime, `UnmanagedCallersOnly` callbacks for input
reading and progress. The whole engine (binding, discovery, queries/predicates, injections, highlighter) is ~2,500 lines with comments, the
editor integration ~700. It follows the C API directly (0.25 removed the cancellation flag and the timeout in favour of progress callbacks and
renamed `ts_language_version` → `ts_language_abi_version`; a binding has to follow such changes), adds no package or transitive native
assets, and keeps memory/threading under the editor's own rules (confinement, counting allocator for tests).

**Existing bindings** (from knowledge, NOT verified on this offline machine — check the current state before relying on it): community
NuGet packages exist that wrap the C API and bundle prebuilt native grammars for several platforms (e.g. *TreeSitter.DotNet*). Pros: native
binaries for Windows/macOS/Linux already built. Cons seen in this class of package: lag behind the runtime's ABI (0.25 broke the parse API),
a fixed set of bundled grammars and queries of unknown pinning/licence provenance, managed wrappers that allocate per node, and a
third-party native supply chain. If one is adopted later, use it only for its native artifacts and keep this layer's API.

## 10. Decisions for the user

1. **Approve the direction**: hybrid, staged (§1), default still TextMate.
2. **Grammar distribution**: CI-built native grammars from pinned sources (recommended) vs wasm grammars (needs a wasm-enabled runtime) vs
   on-demand download per language (consent per artifact).
3. **Query source**: nvim-treesitter's queries (Apache-2.0, complete, used here) vs Helix's (MPL-2.0) vs maintaining our own.
4. **Which languages first** go tree-sitter by default (proposal: the 17 uncoloured languages, then Rust/TS/TSX/Python/Markdown/HTML; C# stays TextMate).
5. **Crash isolation** stance (§8): none for a pinned, fuzzed, shipped set (recommended) vs a child process.
6. Which structural features to build on the trees (§7 proposal: expand/shrink selection, folding, Vim text objects first).

## 11. Reproducing it

* Tests: `dotnet test tests/CsCode.Editor.Tests --filter "FullyQualifiedName~TreeSitter"` (skip with reasons without the runtime/grammars) and
  `dotnet test tests/CsCode.App.Tests --filter TreeSitterCompositionTests`. Screenshots/agreement: `--filter TreeSitterQualityTests`.
* Benchmarks: `tools/bench-treesitter.sh` (Release; `--debug`, `--filter B1|B1b|B2_B3|B4|B5`, `--pass timing|memory`). Two passes (timings with the
  counting allocator off, memory with it on); output `.scratch/bench/treesitter-<run>.md`, every row with the load average.
* Try it in the app: `"editor.experimental.treeSitter": true` in settings.json (or per language). Needs `libtree-sitter` ≥ 0.25 and compiled
  grammars (`CSCODE_TREESITTER_LIB`, `CSCODE_TREESITTER_GRAMMARS`, `{UserDataDir}/tree-sitter/`, or the Nix store on this machine).

## 12. Prototype limits (honest list)

* **First colours wait for the whole first parse** (§4). No TextMate hand-off while the first parse runs; the only fallback is the 5 s
  give-up guard (a document that parses that long without a tree is handed to TextMate for good, until the setting or language changes).
* **Grammar coverage:** syntax a grammar does not know can make parsing pathological (§1, B1c); the guard bounds the cost, it does not fix
  the colours. Memory trimming (`IMemoryTrimmable` aggressive level) does not drop trees of documents that still have a view.
* **Combined injections** (`injection.combined`, e.g. several `<html_block>`s in Markdown parsed as one document) are parsed per visible range,
  not across the whole document; injected trees are re-parsed (not incrementally) after each edit, only for injection sites in the range
  being highlighted. A very large `<script>` re-parses on each keystroke inside it.
* **Neovim semantics only:** later patterns win at equal priority, `#set! priority` is honoured; queries written for tree-sitter-highlight's
  "first pattern wins" (the grammars' own `queries/`) would need the other rule. Predicates: `eq/match/lua-match/vim-match/any-of/contains/
  has-ancestor/has-parent/kind-eq` (+ `not-`/`any-`), directives `set!`/`offset!`; `gsub!`/`trim!` are ignored (they only matter for
  injection-language text); Lua `%b()`/`%f[]` are not translated (reported, never matched; none in the prototype queries).
* **Points vs lines:** tree-sitter counts rows by `\n` only; a file with bare-CR line endings gets correct colours (offsets are used) but
  injection `#offset!` row arithmetic may be off by the CR lines.
* **Viewport query cost grows with document size** (the query cursor walks siblings before the range): fine to ~1 MB, measurable at 10 MB (§4).
* **Native memory accounting** in production is an estimate (visible node count × 140 B, calibrated in B4); exact numbers come from the counting allocator in tests/benchmarks.
* **Not done:** the structural features of §7, a status-bar indicator, user-supplied grammars UI, Windows/macOS library names are coded but untested.
