# Tree-sitter in CsCode

CsCode colours code with two engines: **TextMate** (VS Code's grammars, the default wherever it has one) and **tree-sitter** (grammars CsCode
bundles). This is stage 1 of the hybrid plan the user chose (docs/TREE-SITTER-EVALUATION.md §1, decision of 2026-10-29): TextMate stays the
default; tree-sitter colours the languages TextMate cannot and is an option for the others; C# stays on TextMate. The same syntax trees give
Expand/Shrink Selection, folding, an outline when no language server does, and sticky scroll by syntax.

Contents: [1 The setting](#1-the-setting) · [2 Languages](#2-languages) · [3 Distribution](#3-distribution) · [4 Isolation](#4-isolation) ·
[5 Structure](#5-structure) · [6 Threading](#6-threading) · [7 Tests](#7-tests) · [8 Limits and what is not done](#8-limits-and-what-is-not-done)

## 1. The setting

`editor.syntaxEngine` = `auto` (default) | `textmate` | `treesitter`, globally or per language:

```jsonc
"editor.syntaxEngine": "auto",
"[rust]":   { "editor.syntaxEngine": "treesitter" },   // Rust by tree-sitter
"[toml]":   { "editor.syntaxEngine": "textmate" }      // TOML plain (TextMate has no TOML grammar)
```

* **auto**: tree-sitter for a language exactly when TextMate has no grammar for it (no built-in one and no user `GrammarContribution`): a user's
  TextMate grammar for TOML takes TOML back from tree-sitter (tested). Every TextMate language — C# included — stays on TextMate.
* **textmate**: TextMate only (languages without a TextMate grammar stay plain). **treesitter**: tree-sitter wherever CsCode maps the language
  to a grammar (§2), TextMate elsewhere.
* Precedence: the language section's `editor.syntaxEngine`, then the section's old `editor.experimental.treeSitter`, then the global value; the
  old global boolean is read only while the new global setting is at its default. Alias: `true` = `treesitter`, `false` = `auto` (it never
  meant "leave TOML plain"). An unknown value is ignored (the level below decides). Sections are read both flat (`"editor.syntaxEngine"`) and
  nested (`{"editor": {"syntaxEngine": …}}`, which is how the settings service hands them back).
* Changes apply live, both ways, to open editors (a plain view starts colouring when a grammar becomes available for it).
  `CodeEditorControl.SyntaxEngine` says which engine colours a view.
* Every failure falls back to TextMate, logged once per grammar: no runtime, no grammar, a digest or ABI mismatch, a query that does not compile,
  a quarantined grammar (§4), a document that trips a guard (§4).

## 2. Languages

`src/CsCode.Editor/TreeSitter/TreeSitterLanguages.cs`; docs/LANGUAGES.md (generated) shows the result per language.

| | Languages → grammar |
|---|---|
| **tree-sitter by default** (no TextMate grammar) | TOML `toml`, CMake `cmake`, Kotlin `kotlin`, Scala `scala`, Zig `zig`, Protocol Buffers `proto`, GraphQL `graphql`, Haskell `haskell`, Elixir `elixir`, Erlang `erlang`, OCaml `ocaml` (`.mli`: `ocaml_interface`), Vue `vue`, Svelte `svelte`, Terraform + Terraform Variables `terraform`, Nix `nix`, reStructuredText `rst`, Assembly (NASM/MASM) `nasm`, GNU Assembler `asm`, CSV `csv`, JSON Lines `json`, Julia Markdown `markdown` |
| **tree-sitter as an option** | C# `c_sharp`, TypeScript `typescript`, TSX `tsx`, JavaScript/JSX `javascript`, Rust `rust`, Python `python`, Markdown `markdown`, C `c`, C++ `cpp`, HTML `html`, CSS `css`, SCSS `scss`, shell `bash`, YAML `yaml`, JSON/JSONC `json`, Lua `lua`, Go `go`, Java `java` |
| **injections only** | `markdown_inline`, `regex`, `jsdoc`, `hcl` (and every grammar above inside Markdown fences, HTML/Vue/Svelte script and style, Nix strings…) |

C# stays on TextMate because tree-sitter-c-sharp v0.23.5 (the newest pinned) cannot parse valid C# 11 list patterns and its error recovery then
explodes (evaluation §1, B1c); the give-up guard (§4) protects users who opt in anyway.

## 3. Distribution

**Pinned manifest** — `tools/treesitter/manifest.json`: the runtime (tree-sitter v0.26.11), nvim-treesitter at commit `1a4f47b0` (its
`lua/nvim-treesitter/parsers.lua` pins every grammar revision; its `runtime/queries` are the queries) and 38 grammar repositories at exactly
those revisions, and nvim-treesitter-textobjects at commit `5c7b0263` (the head of its `main` branch, which follows nvim-treesitter's `main`,
before the nvim-treesitter pin: its `queries/<lang>/textobjects.scm` are Vim's syntax text objects, §5); for each: URL (github.com only),
revision, the commit id, the SHA-256 and size of the GitHub source archive (GitHub publishes no digest: the SHA-256 is recorded at the first
download and the pinned commit is checked against the archive's pax header), the licence (SPDX) and licence file; then the 42 grammars built
from them (name, source, sub-directory, role: auto / opt-in / injection). Licences: MIT, except tree-sitter-elixir, tree-sitter-erlang,
tree-sitter-hcl, nvim-treesitter and nvim-treesitter-textobjects (Apache-2.0). No GPL/EUPL/MPL source is used.

**Fetch** — `tools/treesitter/fetch-grammars.sh` downloads each archive (`<url>/archive/<revision>.tar.gz`) into its own new directory
`<main checkout>/.scratch/downloads/<name>/<revision>/` (git-ignored, shared by the worktrees; `--dir` or `CSCODE_DOWNLOADS` to change), and
checks the size, the SHA-256 and the commit id git wrote into the archive's pax header (so a moved tag or a swapped archive is refused).
`--offline` verifies without downloading, `--only NAME` checks one source, `--pin` records the digests of entries that have none (maintainers,
when a revision changes: clear the entry's `sha256`, run, review the licence guess and the diff). 41 archives, 20.1 MB in all
(nvim-treesitter-textobjects: 52,907 bytes, SHA-256 `703e09b80b1c6d1ac0d08e9c3caece95079fdaf2ba6a21be625ed23ea47b30d8`).

**Build** — `tools/treesitter/build-grammars.sh [--target RID] [--out DIR] [--jobs N]` verifies the downloads (fetching what is missing),
unpacks each into its own directory, refuses a licence outside MIT/Apache-2.0/ISC/BSD/0BSD/CC0 or a `tree-sitter.json` that declares another
licence than the manifest, compiles the runtime (`lib/src/lib.c` with the flags of tree-sitter's Makefile) and each grammar
(`parser.c` + `scanner.c`, `-O2 -std=c11 -fPIC`, stripped), copies nvim-treesitter's queries (plus `tags.scm` from the grammar projects that
have one, as their `tree-sitter.json` lists them, and nvim-treesitter-textobjects' `textobjects.scm` for the 35 names that have one) and
writes `bundle.json`, `NOTICE` and `licenses/<source>.txt`. Nothing downloaded is executed;
the output appears atomically (`<out>.tmp` renamed). Default output: `artifacts/tree-sitter/<rid>/` (git-ignored). linux-x64 here: 12 s
with 6 jobs, 47 MB (grammars 46.8 MB — Kotlin, C++, C# 5.3–5.8 MB each; OCaml, Scala, Haskell 3.9–4.8 MB; the runtime 224 KB). Other targets:
`linux-arm64` (`CC=aarch64-linux-gnu-gcc`), `win-x64` (`CC=x86_64-w64-mingw32-gcc`, `.dll`), `osx-x64`/`osx-arm64` (`CC="clang -target … -isysroot
<SDK>"`, `.dylib`) — written, **not built or tested** (no cross compilers here).

**Shipping** — `src/CsCode.Editor.TreeSitter/CsCode.Editor.TreeSitter.csproj` includes `artifacts/tree-sitter/<RuntimeIdentifier or SDK rid>/`
(when it has a `bundle.json`) as content `tree-sitter/**` with CopyToOutputDirectory/CopyToPublishDirectory, so every project that references it
— the app, its tests, `dotnet publish -r <rid>` — gets `tree-sitter/` next to its assemblies. No App build edit was needed. Without a bundle
(the script not run) nothing is copied.

**Discovery order** (`TreeSitterBundle`, `TsRuntime`, `GrammarLocator.CreateDefault`): the app's bundle (`<app>/tree-sitter`) first, then
the user's `{UserDataDir}/tree-sitter` (`lib/`, `grammars/<name>.so` + `grammars/queries/<name>/`), then the environment
(`CSCODE_TREESITTER_LIB`, `CSCODE_TREESITTER_GRAMMARS`), then the system library; the **Nix store only for development**: when the app has no
bundle (`CSCODE_TREESITTER_NIX=1`/`0` forces it on/off). A grammar's queries are taken from the root its library came from first, so a user's
newer grammar never gets the bundle's queries and vice versa. CsCode's own queries (§5) are embedded in the assembly and used when no query
directory has a file of that name.

**Version gate** (`TreeSitterBundle.TryOpen`, `GrammarRegistry`): the bundle is used only when `bundle.json` has a known format, was built for
this OS/architecture and for the ABI range this engine's runtime accepts (13–15). The runtime library is checked against its recorded SHA-256
before it is loaded; each grammar library against its recorded size and SHA-256 (hashed once per process, ≈ 5 ms for the largest) and, once
loaded, its ABI against the recorded one. Any grammar from anywhere is refused when its ABI is outside 13–15 (`TsLanguage.Load`). A damaged or
foreign file is refused with the reason, never mapped into the process (tested with a copy of the bundle whose TOML grammar has one byte
changed: TOML refused, JSON still loads).

**Updating a grammar**: change its `revision` in the manifest to the one nvim-treesitter's `parsers.lua` pins (and the `queries` commit to
that nvim-treesitter commit), clear `sha256`/`size`/`commit`, run `fetch-grammars.sh --pin`, review, `build-grammars.sh`, run the tests
(every bundled grammar must load and every query compile).

## 4. Isolation

**Decision**: keep parsing in process, behind guards, on a thread of its own, with crash/hang quarantine; **out-of-process parsing is not
worth it now** for a pinned, licence-checked, bundled set. Numbers (B6, `TreeSitterBenchmarks.B6_what_out_of_process_parsing_would_cost`,
`.scratch/bench/treesitter-w1-b6.md`, load average 18.5–19.5 during the run, so the times are upper bounds):

| | |
|---|---:|
| a native helper process (C, runtime + C# grammar loaded, one tree parsed): resident memory | 3.5 MB |
| the same: spawn → ready (what a restart after a crash costs, before re-parsing the open documents) | 6.0 ms |
| a .NET helper (the headless `cscode-server`, idle): resident memory | 52.6 MB |
| one keystroke's IPC round trip (edit in, one 60-line C# viewport of token rows out: 23 KB), p50 / p99 | 0.042 / 0.193 ms |
| first transfer of a 1 MB document (2 MB as UTF-16, there and back) | 5.1 ms |
| the same keystroke in process: incremental parse + 60-line highlight of 290 K characters of C#, p50 / p99 | 6.3 / 11.0 ms |

The IPC itself is cheap (≈ 3 % of a keystroke's work). What out-of-process costs is the rest: either the highlighter (predicates,
injections, scope mapping) and every structural query ported to C for a 3.5 MB native helper, or a .NET helper at ≈ 50 MB per process
(several CsCode processes run in background mode); every structural feature becomes an IPC request; documents are re-sent and re-parsed after
a helper crash. It buys protection only against a crash inside a grammar's C code, which the measures below make a one-time event instead of a
crash loop. Revisit if user-supplied grammars are ever loaded (unvetted native code): then a native helper, or hosting the engine in the
`cscode-server` child that `cscode.languageHost = process` already runs.

**What is implemented** (`src/CsCode.Editor.TreeSitter/Isolation`, `TreeSitterService`):

* **Own syntax lane.** Every tree-sitter object lives on one dedicated thread (`cscode-syntax-lane`), not the editor worker: a grammar that hangs
  stops tree-sitter only; TextMate tokenisation, folding, brackets, find keep running.
* **Hang watchdog.** Before each native call into grammar code (a parse slice, an injection parse, a query) the lane records the grammar in
  the flight recorder; a timer checks it every second. A call that has not returned after `HangThreshold` (10 s; a parse slice is 6 ms, a
  query is capped at 1 s) is a hang: the grammar build is quarantined, a fresh lane takes over, every tree-sitter document restarts (engines of
  the old lane are dropped, never touched again: its thread may still be inside them), the quarantined grammar's documents go to TextMate, and
  the user is told (the stuck thread keeps one core busy until CsCode restarts: native code cannot be stopped). Tested with a test hook that
  stands in for a grammar that never returns.
* **Flight recorder + quarantine (crash).** The recorder is a 256-byte memory-mapped file per process (`{UserDataDir}/tree-sitter/flight/<pid>.flight`):
  entering grammar code is a few plain stores, no system call. If the process dies inside a grammar, the kernel keeps the page, so the next
  CsCode process finds a file of a dead process that says "inside grammar X" (`FlightRecorder.CollectCrashes`) and counts a crash of that
  grammar build in `{UserDataDir}/tree-sitter/quarantine.json`. **Two crashes** (one may be the user killing a busy process) or **one hang**
  quarantine the build: it is never loaded again, its languages fall back to TextMate, the user is told why. Entries are per build (bundle
  digest, or path/size/time): a new grammar build starts clean. The command *Clear Quarantined Tree-sitter Grammars*
  (`editor.treeSitter.clearQuarantine`) forgives everything, also before the process's first tree-sitter document (it clears the file
  itself then). A clean exit deletes the recorder file.
* **Several processes** (background mode) share the folder. A recorder file belongs to a live process for as long as its owner holds a
  shared `flock` on it (the kernel drops it when the process ends, crash or not); another process collects a file only when it gets the
  exclusive lock (comparing the pid's start time does not work: .NET computes it from a per-process boot time, so a live process looked
  dead to another one). The quarantine file carries a clear generation: a process that finds a newer one forgives what it held from before
  the clear instead of merging it back, and `Reason` re-reads the file when it changed, so a clear in one process holds in all of them.
  The file work happens on the lane outside the lock the UI thread takes to schedule documents.
* **Guards.** Parse give-up: a document that parses for 5 s of lane time without a tree goes to TextMate (unchanged from the prototype). Query
  guard (new): every query runs with `ts_query_cursor_exec_with_options` and a progress callback that stops it after `MaxQueryTime` (1 s); a
  document whose highlight passes run into it three times in a row goes to TextMate. Match limit 2048 per cursor. Injection depth 4, 400
  injection sites per highlight call. **Injection parses** run under the same 1 s limit of the highlight they belong to (and stop on an
  edit's cancel), never under the 10 s hang watchdog alone: a block that runs out of time stays in the host's colours, the pass counts as
  partial, and a region of that language at least as big is not tried again in that document; regions over 1 M characters are not parsed.
* **Injection languages from the document.** A fence's info string, an rst `code-block` argument or a `<script type>` value is rewritten by
  the query's `#gsub!`/`#trim!` (Lua patterns with their captures; so Nix's `/* bash */ ''…''` and HTML's `type="text/javascript"` inject
  bash and JavaScript), then used only as the name of an INSTALLED grammar: `[a-z0-9_]{1,64}` and listed under a search root (or, for
  development, in the Nix store). Anything else is refused before any path is built or any library is loaded, and nothing is cached for it,
  so a document can neither name a library to `dlopen` nor grow the registry's caches.
* **Gate.** §3: digests and ABI before anything is loaded.

## 5. Structure

`SyntaxDocument.Structure.cs` (engine) and `TreeSitterLanguageFeatures` (editor), registered once in the shared `ILanguageFeatureRegistry`
with `DocumentSelector.All`: score 5, below any language server's providers (score 10), so **a server that offers a feature wins**; the
tree's selection and symbol providers are also **silent while another provider of the same feature exists** (Go to Symbol merges all
providers: nothing appears twice; folding answers anyway, below) and every provider is silent for a document tree-sitter does not colour
(answered at once, on the caller's thread). Answers are computed on the syntax lane
from the tree of the document's current version, waiting up to 2 s for a parse in progress.

* **Expand/Shrink Selection** (`editor.action.smartSelect.expand/shrink`, `ISelectionRangeProvider`): the named nodes that contain the
  selection, smallest first, each strictly larger (a caret at the end of a word takes that word). Order: server → tree → the word/bracket
  heuristic.
* **Folding** (`IFoldingRangeProvider`): the grammar's `folds.scm` (nvim-treesitter; `@fold`, a quantified capture such as `(use_declaration)+`
  is one fold), VS Code style: the first line stays, trailing blank lines are left out, a last line that is only a closer (`}`, `)`, `end…`)
  stays visible; one fold per start line; comment and import kinds. Grammars without `folds.scm` (rst, graphql, asm, nasm, csv) keep the
  indentation folding. The editor merges every folding provider as VS Code does (T32: ranked, one range per start line, the server's first)
  with the language's region markers, so the tree's folds fill in what a server leaves out and fold the document while the server answers
  nothing. A request that gives up waiting for the tree (a first parse over 2 s) leaves a mark on the document's session; when the next tree
  lands the manager raises `SyntaxTreeArrived` and folding (and the breadcrumbs' cache) ask again, without an edit.
* **Vim text objects and motions** (`af if ac ic aa ia ]m [m ]M [M`, docs/VIM.md): `SyntaxDocument.TextObjectRanges` runs the bundled
  `textobjects.scm` and returns the ranges of one capture (`function.outer`, `class.inner`, `parameter.outer`…) as nvim-treesitter-textobjects
  reads them — every node one match captures under that name is one range, from the first to the last, after `#offset!` (so `aa` takes the
  comma). `EditorModalSurface.RequestSyntaxObjects` (null at once without a tree) computes them on the syntax lane for the document's version
  at the call and completes off the UI thread (null when the text changed meanwhile); the Vim host hands them to the engine as an engine call
  of its own.
* **Outline / breadcrumbs** (`IDocumentSymbolProvider`): from the first query the grammar has of CsCode's own `symbols.scm` (embedded,
  `src/CsCode.Editor.TreeSitter/OwnQueries/<grammar>/symbols.scm`: Markdown headings by section, rst titles, TOML tables and keys, YAML and
  JSON keys, GraphQL types/fields/operations, HCL/Terraform blocks with their labels, CMake functions/macros, Erlang module/functions/records,
  Haskell functions/types/classes, Zig functions/containers/tests, NASM/GAS labels, shell functions, CSS/SCSS rules/keyframes/media/mixins), the
  grammar's own `tags.scm` (C, C++, C#, Elixir, Go, Java, JavaScript, Kotlin, Lua, Nix, OCaml, Protobuf, Python, Rust, Scala, TypeScript/TSX),
  or nvim-treesitter's `locals.scm` (definitions of functions, methods, types, classes, namespaces, modules, macros…; the symbol is the name's
  parent node). Symbols nest by containment; names are one line, at most 120 characters.
* **Sticky scroll** (`editor.stickyScroll.enabled`, **off by default** in CsCode; `Features/StickyScrollFeature.cs`): while the first visible
  line is inside foldable regions whose first lines have scrolled away, those lines stay pinned above the text (outermost first, at most 5;
  a region whose body ends under the pinned rows is dropped; a header that is only `{` shows the declaration above it); clicking one goes
  there. The regions are folding's, so on a tree-sitter document they are the syntax tree's folds (TOML tables, functions, Markdown
  sections), elsewhere a server's ranges or indentation. Pinned lines are drawn in the editor font and foreground colour, without syntax
  colours. UI cost: the folding manager's segment-tree lookup around one line per scroll event (tested: every step of a 60-step scroll
  through 1,300 lines under 16 ms).

## 6. Threading

Unchanged rules (ARCHITECTURE §6), one change: the lane is the TreeSitterService's own thread instead of the editor worker. The UI thread only
records edits (a list append) and flips the native cancel flag; parsing is sliced (6 ms, progress callback) and cancellable; stale parses are
abandoned; publications carry the document version and the engine's generation (a retired engine's rows are refused). Disposal of native
objects is posted to the lane that owns them.

## 7. Tests

`tests/CsCode.Editor.Tests/TreeSitter/` (skip with the reason when there is no runtime/grammar), `tests/CsCode.App.Tests/TreeSitterCompositionTests.cs`:

* distribution: every source pinned, licence-checked, from GitHub; every grammar CsCode uses is in the manifest and every manifest grammar is
  used; every detected language without a TextMate grammar has a tree-sitter one; the NOTICE names every source; `fetch-grammars.sh --offline`
  verifies all 40 downloads and refuses a tampered archive; the bundle is found first, every file matches its digest, every bundled grammar
  loads and its highlight query compiles; a bundle for another platform/format/ABI range is refused; a damaged grammar file is refused while the
  others load; bundle before user folder, each grammar with its own queries; the Nix store only without a bundle;
* setting: auto/textmate/treesitter, language sections both ways and nested, the alias, a user TextMate grammar taking a language back, live
  switching, `.mli` → `ocaml_interface`;
* isolation: flight recorder states and nesting, crash collection, the quarantine rules and persistence, the registry refusing a quarantined
  grammar, the whole crash path through the editor (two crash records → TextMate + message → clear command → tree-sitter), the hang path
  (watchdog → quarantine → new lane → other languages keep tree-sitter), the query guard, the recorder file removed on a clean shutdown;
* structure: enclosing nodes, folds (quantified captures, closer lines, Markdown sections), the outline of 18 languages, the locals fallback,
  sticky scroll (pinned headers by indentation and by the tree, click, off by default, scroll cost),
  every own/tags query compiles, Expand/Shrink Selection through the real command, folding through the real FoldingFeature (and the C#/TextMate
  view untouched), breadcrumbs, deferring to a server's symbols;
* the real app: the hybrid default (TOML on the bundled grammar, Rust on TextMate), the setting through settings.json and the alias, the
  outline provider in the shared registry, the command's title, the flight recorder under the user data directory.

Run: `dotnet test tests/CsCode.Editor.Tests --filter "FullyQualifiedName~TreeSitter|FullyQualifiedName~TextMateLivelock"` and
`dotnet test tests/CsCode.App.Tests --filter "TreeSitterCompositionTests|LanguageMatrixTests"`. Benchmarks (off by default):
`tools/bench-treesitter.sh` (B1–B6; `--filter B6`).

## 8. Limits and what is not done

* Sticky scroll is off by default (VS Code turns it on) and its pinned lines are not syntax-coloured.
* Structural features use only the host tree: inside an injection (a Markdown code fence, a Vue `<script>`) selection and outline see the host's
  node for the whole block.
* Folds from the tree replace indentation folding for that document (region markers are merged in, as are a language server's folds, the
  server's first on a shared start line); a first parse slower than the 2 s wait folds by indentation until the tree lands, then by the tree.
* Vim's syntax text objects use nvim-treesitter-textobjects' queries as they are: 35 of the bundle's names have one (not asm, csv, erlang,
  graphql, jsdoc, markdown_inline, ocaml_interface, proto, regex); the objects each query defines (e.g. TOML has only parameters) are that
  project's.
* Out-of-process parsing: not done, by decision (§4). A crash inside a grammar still takes the process down once (twice before quarantine).
* Only linux-x64 bundles were built; the other targets' compiler flags are written but untried. Windows/macOS library names are coded, untested.
* First colours of a big file still wait for its first parse (evaluation §12).
