# CsCode.Editor

The code-editing surface of CsCode: AvaloniaEdit 12.0.0 + TextMateSharp 2.0.3 wired to the Core contracts, with Visual-Studio-style language-feature UX
(IntelliSense, diagnostics, hover, signature help, quick fixes, navigation, folding, breakpoints). `src/CsCode.Editor`, tests in `tests/CsCode.Editor.Tests`.

Contents: [1 Using it](#1-using-it) · [2 Architecture and threading](#2-architecture-and-threading) · [3 What is implemented](#3-what-is-implemented) ·
[4 Threading rules and how they are verified](#4-threading-rules-and-how-they-are-verified) · [5 AvaloniaEdit 12 / TextMateSharp 2.0.3 discoveries](#5-avaloniaedit-12--textmatesharp-203-discoveries) ·
[6 Measured performance and memory](#6-measured-performance-and-memory) · [7 Settings, commands, large files](#7-settings-commands-large-files) ·
[8 Known gaps (honest list)](#8-known-gaps-honest-list) · [9 Tests](#9-tests) · [10 Third-party content](#10-third-party-content)

## 1. Using it

```csharp
services.AddCsCodeEditor();                       // IEditorViewFactory, ILanguageService, IColorThemeService, snippets, TextMate pipeline, editor worker
var factory = sp.GetRequiredService<IEditorViewFactory>();   // resolve once, on the UI thread
var doc  = await factory.OpenDocumentAsync(uri);              // encoding/EOL detection, large-file safeguards, registers with IDocumentManager
var view = factory.CreateView(doc);                           // an Avalonia Control (CodeEditorControl) — host it in a tab
var ctl  = factory.GetController(view);                       // caret, selections, reveal, undo/redo, find, view state, breadcrumbs, stop line
```

Every Core service the editor *consumes* is optional. When Platform has not registered one, a small local fallback is used (`LocalFileSystem` on a
dedicated I/O thread, `FswFileWatcher`, `InMemoryConfiguration`, `EditorConfigResolver`, a `DocumentManager`, a `LanguageFeatureRegistry`, a
`DiagnosticService`), so the module runs and is testable on its own. Nothing ever references another module's concrete types.

Contract additions (additive only — no existing member was renamed or removed): `IEditorViewFactory.DisposeView`; `IEditorViewController.GetViewState`,
`RestoreViewState`, `GetBreadcrumbsAsync`, `IsFocused`; the `EditorViewState` record; the clipboard members (§7a); the macro hooks `TextInput`,
`ReplayText`, `ReplayKey`, `OpenUndoGroup`, `StartNewUndoStep` (below). The later additions are default interface members, so other
implementations keep compiling. **No change was made to CsCode.Core.**

**Modal editing surface (for the shell's Vim adapter).** `CodeEditorControl.Modal` (`EditorModalSurface`, created on first use, disposed with the
view) is a Vim-free hook surface: the typing path (`TypeText` = `TextArea.PerformTextInput`), `CaretShape` (block / underline / outline / thin
shapes drawn on the caret layer while the native caret is transparent), stream / whole-line / rectangular selections, viewport queries and
`ScrollToTopLine`, folds by line (`TryGetClosedFold`, `Fold`), the state of the completion list, parameter hints and snippet session (and moving
or closing them), a search-pattern highlight over the visible lines and a flash highlight, comment tokens, range formatting. Between `Attach` and
`Detach` the editor's Undo/Redo (controller, so menus, toolbar and key bindings) are offered to the layer first, and typing never continues an
undo group the layer closed. `EditorDocument` adds `BeforeChange` (each change's range before the buffer mutates, so the replaced lines can be
read), `BeginUndoGroup`/`EndUndoGroup`/`AddUndoMarker` (one undo step per Vim change, with a marker reporting undo/redo of the group) and
`BeginAtomic`/`EndAtomic` (one change event for a burst of edits); `EditorViewFactory` raises `ViewOpened`/`ViewClosing` (the latter first thing in
`DisposeView`). Key routing is the shell's: the window offers editor keys to Vim before the key bindings (docs/SHELL.md, "Keyboard"), and keys Vim
passes through reach the editor unchanged. While the integrated terminal has the keyboard its own precedence decides first (`commandsToSkipShell`,
chords; docs/SHELL.md "Keyboard", docs/TERMINAL.md), and none of those keys reaches an editor.

**Macro hooks (for the shell's macro recorder and player, docs/MACROS.md).** All UI thread; a destroyed view takes nothing and returns false.

| Member | What it does |
|---|---|
| `IEditorViewController.TextInput` | Text the user types into the view (a key's character or an IME commit), raised from a tunnelling handler on the text area *before* the editor applies it, and only for input no earlier handler took (Vim takes its own in the window's handler). Control characters some platforms send after Enter / Tab / Backspace are not reported (those are key presses); text inserted by code (paste, commands, `ReplayText`) is not reported. Costs one handler that returns at once while nothing subscribes (the recorder subscribes only while recording). |
| `ReplayText(text)` | Types through the editor's typing path (`TextArea.PerformTextInput`) as if typed: auto-closing and over-typing, completion and signature-help triggers, snippets, language-aware Enter for `"\n"`, typed-run undo coalescing. One character at a time reproduces typing. False for a read-only document. |
| `ReplayKey(key, modifiers)` | Raises a routed key-down and key-up on the text area: the window's handlers see it first (the shell's pipeline lets a replayed key through unresolved), then the editor's own key handling (caret movement and selection, Backspace, Delete, Enter, Tab, the completion list, snippet placeholders, AvaloniaEdit's Ctrl+Z / Ctrl+Y). The text area takes the keyboard focus first: AvaloniaEdit's editing commands act on the last *focused* text area, not the event's source. Returns whether a handler took the key. |
| `OpenUndoGroup()` | Holds an undo group of the view's document open until the result is disposed: every change of the document meanwhile (typing, keys, commands, in any view, over any number of dispatcher callbacks) becomes ONE undo step. Handles of one document nest; the group also closes when the document closes; typing right before or after it is a step of its own; disposing twice, or after the view or document is gone, does nothing. Off the UI thread: `EditorDocument.OpenUndoGroup` throws, a handle's `Dispose` is posted. |
| `StartNewUndoStep()` | The next change starts an undo step of its own (a typed run does not continue the last one), inside a held group too. |

While a group is held, **Undo and Redo never throw** (AvaloniaEdit's `UndoStack.Undo` does with a group open, and forgets its open groups): the
controller's `Undo`/`Redo`, `EditorDocument.Undo`/`Redo` and the text area's `ApplicationCommands.Undo`/`Redo` (Ctrl+Z / Ctrl+Y, `TextEditor.Undo`;
each view puts its own bindings in front of AvaloniaEdit's, active only while a group is held) reverse and re-apply the steps made inside the
group, newest first, as edits inside it. The steps are the editor's usual ones (a typed run, one key's change, one command's edit, a modal
layer's group), read from the typing path's and modal layers' groups (`EditorDocument.BeginUndoGroup`; the typing path now opens its groups
through it) and from the buffer's update events; the changed text is AvaloniaEdit's immutable text sources, shared with its own history. Nothing
from before the group is reached, so undoing the closed group restores exactly the text it started from; `CanUndo`/`CanRedo` report the
group's own steps meanwhile. The document turns dirty at the group's first change (AvaloniaEdit recomputes its saved marker only when the
outermost group closes), a save inside the group marks it clean and closing the group settles the marker; a reload from disk clears the history
and keeps the group open from the reloaded text. A group keeps at most `EditorDocument.HeldUndoChangeLimit` (100,000) changes and closes when it
reaches that many (memory bound). Code that calls AvaloniaEdit's `UndoStack.Undo`/`ClearAll` directly with a group held gets AvaloniaEdit's
exception; closing the group afterwards is still safe. Tests: `HeldUndoGroupTests`, `MacroHooksViewTests`.

What the shell must know:

* **Destroy and recreate views of inactive tabs** with `GetViewState()` → `DisposeView(view)` → later `CreateView` + `RestoreViewState(state)`. The view owns nothing
  that outlives it (tests: 500 destroy/recreate cycles, 500 open/close cycles, every disposed view collectable). Move keyboard focus away when you destroy
  the focused view: Avalonia's focus / input-method machinery keeps a *focused* `TextEditor` reachable until focus moves (true of a bare AvaloniaEdit editor too).
* **Commands** are registered with `ICommandService` under VS Code ids (§7) and act on the view that last had focus. Keybindings are the shell's job; only when
  *no* `ICommandService` exists (standalone) does the editor bind F12 / Shift+F12 / F2 itself.
* **Find references**: with more than one result the editor runs the command `cscode.references.show` (args: `IReadOnlyList<Location>`, a title string) if the shell registered
  it — the shell's Find Results presents them — and otherwise shows them in `IWindowService.ShowQuickPickAsync`. One result just navigates.
* **Breakpoints follow edits** by calling `IBreakpointManager.ApplyLineShift(uri, fromLine, delta)`. The editor's contract (the Core interface does not say): every breakpoint at
  a line `>= fromLine` moves by `delta`; breakpoints on lines that no longer exist are `Remove`d by the editor itself *before* it calls `ApplyLineShift`.
  (A newline typed at column 0 shifts from that line; deleting whole lines from column 0 removes exactly those lines' breakpoints.)
* **GC mode**: the figures in §6 were measured with `GCSettings.LatencyMode = SustainedLowLatency` (no blocking gen-2 collections while typing). AvaloniaEdit itself allocates
  hundreds of KB per keystroke in a big document (§5); the shell should run the app in that mode.
* `docs/briefs/editor.md` says to add Codicon attribution to `THIRD-PARTY-NOTICES.md`: the completion kind badges are *generated* (a letter/glyph on a coloured tile) so no icon font is shipped
  and nothing needs attributing for them. See §10 for what else is bundled.

## 2. Architecture and threading

```
UI thread                                   editor worker (1 dedicated thread)              I/O lane (1 dedicated thread)        pool (Task.Run)
─────────────────────────────────────────   ───────────────────────────────────────────     ─────────────────────────────────    ──────────────────────
TextDocument (rope) = the only mutable      TextMate tokenisation (viewport first,          file read/decode, atomic save        language-provider calls
  buffer; input; rendering                    6 ms slices; grammar registry lives here)      (temp + rename), encoding/EOL scan   (completion, hover, signature,
EditorDocument: events, undo, dirty         fold ranges, bracket pairs/depth map,           .editorconfig reads                    code actions, definition, …)
DocumentSnapshot published at every         occurrence highlights, diagnostics → offsets,   FileSystemWatcher fallback              each with a deadline and
  change (immutable, any-thread reads)      completion filter/sort (top-K), Markdown                                                  cancellation; the UI only
view components (renderers, margins,        parsing + code colouring for hovers                                                       awaits via continuations
  popups, anchors) — they only DRAW         ↑ results come back as small, version-tagged,
                                              coalesced UI posts; stale ones are dropped
```

* **`EditorDocument`** wraps AvaloniaEdit's `TextDocument`: one `TextDocumentChangedEventArgs` per atomic operation (outermost BeginUpdate/EndUpdate group), changes with
  sequential semantics (each range against the state before that change), monotonic version, `ApplyEdits` as ONE undo unit (stable order, overlap-rejecting, applied back to front),
  dirty tracking through the undo stack's original-file marker, `DocumentSnapshot` (rope clone + lazy line index) for every non-UI reader.
* **View** = `CodeEditorControl` (AvaloniaEdit `TextEditor` + an overlay canvas that hosts popups inside the editor frame, so they are clipped like VS Code's widgets and appear in screenshots).
  Feature components (`src/CsCode.Editor/Features`): folding, snippets, completion, diagnostics, hover, signature help, code actions (lightbulb + Ctrl+.), navigation (definition, references, rename,
  Ctrl+Click on symbols and URLs), formatting (document/selection/on type/on paste; format-on-save lives in the document's save pipeline), bracket matching + pair colourisation, occurrence
  highlights, breadcrumbs, inlay hints, and — only when an `IDebugService` is registered — the breakpoint margin, execution arrow and breakpoint tracking. In large-file mode a view gets **none** of them.
* **Tokenisation** (`TextMate/`): per document one `TokenizationSession`, driven by attached views. Tokens are `int[]` (startColumn, scope-stack id) pairs per line, scope stacks interned
  (theme-independent ids: a theme switch re-colours without re-tokenising), rule-stack checkpoints every 8 lines (not one per line — that is the classic memory hog), a valid-prefix pointer,
  viewport-first slices of 6 ms, "island" tokenisation of the window when a huge file is opened far down, read-ahead of 3000 lines held back while the user is typing. `IMemoryTrimmable`;
  everything is released when the last view goes away. A slice resumes at the first viewport line still missing tokens and always gets at least one line past the
  checkpoint it restarts from, so a slow grammar on a loaded machine cannot stall (it used to restart from the viewport top every slice). An island keeps its progress
  (line and rule state) across slices and, once it covers the viewport for a version, is done: neither a layout pass nor a slice schedules it again until an edit or a
  scroll (each island publish used to redraw, the redraw reported the viewport, the report scheduled the island again, forever). Rows identical to the stored ones are not
  redrawn. An Aggressive memory trim colours the viewport again at once (both engines).
* **Tree-sitter engine** (`editor.syntaxEngine` = `auto` | `textmate` | `treesitter`, per language with `"[rust]": {…}`; `auto` = tree-sitter only for the
  languages TextMate has no grammar for): a session can be driven by tree-sitter instead of TextMate (`src/CsCode.Editor.TreeSitter`, `TreeSitter/`), with the grammars the
  app bundles. It writes the same token rows into the same `LineTokenStore` (capture names mapped onto TextMate scopes), so colouring, themes, `KindAt` and brackets are
  unchanged; parsing and queries run on the engine's own syntax lane, sliced and cancellable; any failure falls back to TextMate. The tree also gives Expand Selection,
  folding and an outline when no language server does. `CodeEditorControl.SyntaxEngine` tells which engine colours a view. Design, isolation and limits:
  `docs/TREE-SITTER.md` (evaluation: `docs/TREE-SITTER-EVALUATION.md`).

## 3. What is implemented

Numbers refer to the brief's MUST list.

1. **EditorDocument** — exact change mapping, fuzz-tested (10k random edit sequences incl. CRLF, surrogates, undo/redo) against `InMemoryTextDocument`; load/save through `IFileSystem`, atomic save; encoding detection
   (BOMs, UTF-8, BOM-less UTF-16 heuristic, Windows-1252 fallback; `files.encoding`; preserved on save); EOL detection/preservation (majority; `SetEndOfLine`); final-newline / trim-whitespace / trim-final-newlines policies
   from `files.*` and `.editorconfig`; `ReloadAsync` as a minimal diff; external change watching with the "File changed on disk — Reload / Keep" prompt when dirty and a silent reload when clean (own writes ignored);
   read-only files; untitled buffers; `IDocumentManager` registration; `Saved`/`DirtyChanged`; **large-file mode** (§7).
2. **View factory + control** — multiple views per document; controller (caret, selections, reveal, focus, undo/redo, find, format, stop line, events, **view state** incl. scroll, selection and folds), `DisposeView` that unhooks everything.
3. **Settings-driven rendering**, live (`IConfigurationService.Changed`): see §7.
4. **TextMate highlighting** from the contribution registry plus VS Code's built-in grammars (embedded in `TextMateSharp.Grammars`), embedded languages through the grammars' own injections, grammar/theme changes live, memory numbers in §6.
5. **LanguageService** — detection order: `files.associations` → exact file name → name pattern → longest extension → first-line regex; ~40 built-in languages from embedded manifests plus a supplement table; `language-configuration.json` parser (JSONC; brackets,
   auto-closing pairs with `notIn`, surrounding pairs, word pattern as string or `{pattern,flags}`, folding markers, indentation rules, `onEnterRules`, `offSide`), live on contribution changes.
6. **ColorThemeService + VS Code theme loader** — JSON/JSONC, `include` chains, `colors`, `tokenColors` (comma/array scopes), `semanticTokenColors`, light/dark/HC; ~200-key defaults table for dark/light/HC with fallback chains; selection persisted in `workbench.colorTheme`;
   the 21 themes embedded in `TextMateSharp.Grammars` built in (`BuiltinColorThemes`: Dark+, Light+, Monokai, Solarized, Dracula, ... with VS Code's labels); `Register` for shell themes;
   user themes from `{UserDataDir}/themes` watched for changes; `IColorThemePreview` (a picker shows a theme without selecting or persisting it) and `SetCompletion`
   (the shell's VS 2022 chrome completion is applied to every theme the service hands out; docs/SHELL.md).
7. **Editing behaviours** — all the listed commands (§7), auto-indent from the language configuration + `.editorconfig`, auto-close/over-type/surround honouring `notIn` and the TextMate token under the caret, typing bursts coalesced into one undo step, sort/join/transpose/trim, expand/shrink selection
   (`ISelectionRangeProvider` when present, else a word/line/block heuristic), format on save / on type / on paste.
8. **Snippet engine** — full VS Code grammar (tab stops, placeholders, nesting, choices, mirrors, variables incl. transforms), indentation normalisation, live mirrored session with Tab/Shift+Tab/Esc, user snippet files `{UserDataDir}/snippets/<lang>.json` watched for changes, prefix completion; well over the required 60 tests (`SnippetEngineTests`).
9. **IntelliSense UI** — completion (virtualised list, fuzzy filter/sort on the worker, kind badges, documentation panel, commit characters, `isIncomplete` re-query, `resolve` on selection, `textEdit`/`additionalTextEdits`, snippets, quick suggestions, trigger characters, Ctrl+Space, merge of several providers);
   hover (300 ms dwell, Markdown with TextMate-coloured fences, diagnostics + Quick Fix link, debugger value while stopped, hidden text of a collapsed region); signature help (trigger/retrigger characters, bold active parameter, ↑/↓ overloads); squiggles in four severities + faded
   "unnecessary" code; lightbulb + Ctrl+. via `IWindowService` quick pick, edits through `INavigationService.ApplyWorkspaceEditAsync`, commands through `ICommandService`, `source.organizeImports`; go to definition (F12; Ctrl+Click underlines the symbol), references, rename (prepare → input box → apply);
   document highlights (provider, else word occurrences); folding (every `IFoldingRangeProvider` at once, each with its own 2 s deadline, merged as VS Code merges them — ranked, one range per start line, nesting kept — plus the language configuration's `#region` markers; indentation + markers only when no provider has anything; a tree-sitter document whose tree arrives after the first request is folded again when it lands; +/- glyphs, collapsed-region hover); bracket matching + setting-gated pair colourisation. Everything is cancellable, debounced, latest-wins, and **never shows a result for older text**.
10. **Debug integration** (all null-safe without a debugger) — breakpoint glyph margin (click toggles; right-click: condition / hit count / logpoint / enable / remove; red filled = verified, hollow grey = disabled or unverified; conditional and logpoint shapes; tooltips with the adapter's message; ghost dot on hover),
    execution arrow + line highlight with auto-reveal, breakpoints that follow edits, hover-evaluate (`EvaluateAsync(expr, frameId, "hover")`).
11. `AddCsCodeEditor`, this document.

12. **Clipboard and the right-click menu** (clipboard wave, `docs/briefs/clipboard-and-context-menus.md`): see §7a.

SHOULD items done: breadcrumbs (`GetBreadcrumbsAsync`, cached per document version and per set of symbol providers: a language server that starts after the file was opened is asked at the next request, without an edit), Ctrl+Click on http(s) URLs (through `IWindowService.OpenExternalAsync`), inlay hints (anchored, capped at 1000, setting-gated), accessibility names on the editor, popups and margin.

## 4. Threading rules and how they are verified

The binding rule — *editing must never wait for language services* — in concrete form:

| Rule | Enforced by | Verified by |
|---|---|---|
| The UI thread owns the buffer; nothing else touches it | `DocumentSnapshot` for every other reader; `ThreadAffinityUiThread` test double asserts thread use | fuzz tests, `EditorDocumentFileTests`, snapshot tests |
| No synchronous blocking in the UI-facing project | `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` (RS0030 is an error): `Task.Wait`, `.Result`, `GetAwaiter().GetResult()`, `Thread.Sleep`, synchronous `File`/`Directory` I/O | the build itself |
| …and at run time | — | `BlockingDetector`: a `SynchronizationContext` that records every managed blocking wait on the UI thread; `HostileProviderTests` assert **zero** while typing under load (and `BlockingDetectorTests` prove the detector detects) |
| Providers never run on the UI thread | every call goes through `FeatureSupport.CallAsync` (`Task.Run` + deadline + cancellation; exceptions/timeouts → "no result") | `HostileProviderTests`, hung-provider tests per feature |
| Latest wins; stale results are never applied | request versions, anchors, `ReferenceEquals` guards, one-pending-run coalescing | `*_for_older_text_is_dropped` tests for hover, signature help, code actions, rename, format, diagnostics, inlay hints, completion |
| Derived work is sliced and posted back small | worker slices ≤ 6 ms; UI applies in slices of ≤ 500 anchors at Background priority; completion shows ≤ 300 rows | `UiJobs.Observer` records every UI job the editor posts: p99 < 0.8 ms, worst 5–12 ms (a GC pause) under the hostile load |
| A slow/hung/flooding provider costs nothing but a "loading…" line | 300 ms loading affordance; 1.5–5 s deadlines; 5000-diagnostic cap; a single pending coalescing timer | `HostileProviderTests` (below) |

`HostileProviderTests` types 400 characters into a 3000-line C# file while: a completion provider never answers, one answers after 3 s with 50k items, one answers instantly with 50k items, one throws; hover, signature,
code-action, document-highlight and definition providers hang; and two producers publish diagnostics at ~100k/s each (≈ 200k/s in total — measured 97k/s achieved in the runs below). Asserted: keystroke handling p99 < 5 ms
(with GC pauses removed; raw p99 < 25 ms), the editor's own UI jobs p99 < 4 ms and none ≥ 16 ms, dispatcher latency (a job posted from another thread) p99 < 16 ms, no blocking wait on the UI thread, every keystroke landed, and after the flood the
applied diagnostics are exactly the last published set and every squiggle still covers its own text. A `Baseline_…` test runs the same typing with nothing misbehaving, and a variant makes every 50k-item answer a fresh allocation (hundreds of MB).
Measured in those runs (shared 22-core machine, Debug build): keystroke p50 0.4–1.0 ms, p99 2.7–4.1 ms raw / 1.2–3.6 ms without GC pauses; editor UI jobs p50 < 0.04 ms, p99 < 0.8 ms; dispatcher latency p50 0.6–1.4 ms, p99 8–14 ms, max 12–26 ms.
The scenarios retry once on a budget miss (another process stealing cores), and fail if two runs in a row miss.

## 5. AvaloniaEdit 12 / TextMateSharp 2.0.3 discoveries

AvaloniaEdit 12.0.0 (`Avalonia.AvaloniaEdit`):

* A `TextEditor` is **inert without its theme** (`avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml`): no template, no TextView, no handlers. `EditorStyles.Ensure()` adds it to the application's styles on first use. (A "plain editor" benchmark without the theme measures nothing.)
* `TextDocument` is single-thread-owned (`SetOwnerThread`). A document can be *built* on a worker and handed to the UI thread, which is how a 50 MB file opens without freezing the UI. `CreateSnapshot()` is a cheap rope clone; wrapping it in a `RopeTextSource` **without** the version object avoids retaining the whole edit history through the version chain.
* Events: `Changing` runs before the mutation (use it to express ranges against the previous state), `Changed` after, `UpdateFinished` once per outermost update group. `NewLineFinder` recognises only `\r`, `\n`, `\r\n` (same as Core).
* `UndoStack`: `IsOriginalFile`/`MarkAsOriginalFile` give dirty tracking; `StartContinuedUndoGroup` lets a typing burst be one step. **Its saved-marker
  arithmetic assumes the step a continued group joins came after the marker**: continuing the step the file was saved after reported the document
  clean for different text (T50: type, save, type at the same caret, close without a prompt, edit lost). `EditorDocument.BeginUndoGroup` therefore
  never continues across a save (VS Code also ends the undo stop at a save) and returns whether it continued, so a modal layer that labels its steps
  (Vim: an Insert session across a save is two groups of one Vim step, still one `u`) counts the extra group; the clipboard ring's cycle goes
  through it too. Inside a held group (macro playback) a save ends the typed run as well.
* **The folding margin keeps every marker it ever made** (T36): on each visual-line change `FoldingMargin` makes a `FoldingMarginMarker` per
  visible fold and removes the old ones from its visual children, but stays their logical — and so property-inheritance — parent, whose
  inheritance-children list holds them with their visual lines, text lines and glyph runs: scrolling a 20k-line file kept ~3 MB per end-to-end
  pass until the tab closed. `FoldingFeature` replaces the installed margin with `DetachingFoldingMargin`, which detaches the markers it replaces
  (`FoldingMarginLeakTests`; the memory soak `AppSoakTests.Scrolling_a_twenty_thousand_line_file`).
* **The undo history pins the text area.** Each edit pushes a `RestoreCaretAndSelectionUndoAction` that keeps its `TextArea` weakly but its `Selection` object strongly, and `Selection` holds the `TextArea`. A document that outlives a view therefore retains the whole destroyed view
  (renderers, visual lines, popups) until the entry scrolls off the undo limit. `UndoStackScrubber` clears those selections for the disposed view's entries (reflection against the 12.0.0 layout; a no-op if the layout differs). Found with `RetentionPath` (below).
* `TextEditor.ScrollToVerticalOffset` does nothing for absolute positions on a fresh view; restoring a scroll offset goes through `((IScrollable)TextArea).Offset`, re-applied on `LayoutUpdated` until the extent exists.
* `AbstractMargin` margins are placed through `TextArea.LeftMargins`; toggling line numbers re-inserts the line-number margin at index 0, so the breakpoint margin re-pins itself leftmost. `TextArea.TextPasted` fires after a paste (the pasted range is taken from the last document change).
* `EnableHyperlinks` makes AvaloniaEdit start the browser itself; the editor turns it off and opens URLs through `IWindowService`.
* **The stock search panel searched the whole document on the UI thread on every edit while open**, without a result limit, with a regex that
  had no match timeout. `FindSearch` (reflection against the 12.0.0 layout; the stock panel stays when it differs) gives it VS Code's find-widget
  behaviour: at most 19,999 matches held and highlighted (the panel says when there are more), a 1 s regex match timeout (the panel says the
  pattern was stopped; such a pattern is not re-run after edits), and after an edit the matches move with it while the re-search waits for
  100 ms of quiet and runs on the editor worker from a snapshot, in 64 K-character chunks a slice at a time, stopped by a newer edit. A new
  pattern or option still searches at once on the UI thread, bounded, so Enter right after typing finds the match. The panel uses its held
  matches for highlighting, navigation AND Replace All, so the limit must not reach further: Find Next / Previous (F3, Shift+F3, Enter,
  Shift+Enter) past the last held match search the text from the caret (wrapping over the whole document), a new pattern selects the match
  after the caret wherever it is, and Replace / Replace All search the CURRENT text (every match, one undo step; a timed-out pattern replaces
  nothing) instead of the held matches, which after an edit are only moved until the re-search lands: VS Code does the same (a model search
  past `MATCHES_LIMIT` decorations, `replaceAll` searching the model again). These are command bindings placed before the panel's own, which
  decline whenever the held matches answer the command exactly, and a tunnelling Enter handler on the panel.
* **AvaloniaEdit allocates in proportion to the document's line count on every edit** (≈ 90 B per line per keystroke: 19 KB for a 50-line file, 538 KB for 6000 lines, measured on a bare themed `TextEditor`), and keeps ≈ 130 B per line per view for its height tree. Nothing in this module can change that.

TextMateSharp 2.0.3:

* The tokenizer (`Registry`, grammars) is **not thread-safe**; it is confined to the editor worker. The native Oniguruma library loads fine on this machine; the first lines of a grammar pay for lazy regex compilation, so a stopped-early line (`StoppedEarly`) is retried with 10/60/300 ms limits before a truncated result is accepted.
* `Theme.Match` ignores parent scopes, so the module implements vscode-textmate's matching itself (`TokenColorResolver`: specificity, inheritance, parent scopes, comment/string/regex kinds). The built-in Monokai has no `name` and `light_vs` has no `type`: kind is inferred from the background's luminance.
* `IStateStack.Equals` is **not** structural across independent runs, and the concrete `StateStack` is internal — so "stop re-tokenising once the state converges" (what VS Code does) is not available. The module instead tokenises only the viewport after each edit and defers the read-ahead until typing pauses (250 ms).
* `TextMateSharp.Grammars` embeds VS Code's built-in grammars and themes (this is where Dark+/Light+ and the ~40 languages come from).
* **It keeps every grammar's raw JSON tree after compiling it** (the registry's tree of each grammar in the include closure, plus a deep copy per
  compiled grammar of its own and of each included one), though the rule graph is built whole on the first line and never reads them again.
  `RawGrammarRelease` compiles each grammar when it is loaded and drops the trees (a grammar compiled later re-reads its closure from the sources):
  C#, Markdown, JSON and shell retain 18.9 MB instead of 43.8 MB (T37; reflection against 2.0.3, a no-op if the layout differs).

## 6. Measured performance and memory

Debug build, shared 22-core Linux machine; ranges are the spread of repeated runs.

| What | Result |
|---|---|
| Open 1 MB C# file | 9–13 ms; view + first layout 47–54 ms; ≈ 8 MB managed (a fixed ≈ 7 MB of tables included) |
| Open 10 MB (large-file mode) | 74–81 ms; UI thread blocked ≤ 0.3 ms during the open; view + layout 138 ms; 15 MB managed after open (1.5 × file size), 64 MB with a view |
| Open 50 MB (large-file mode) | 604–688 ms; UI latency during open max 1.1–13 ms; view + layout 575 ms; 101–114 MB after open (2.0–2.3 ×), 342–355 MB with a view (AvaloniaEdit's per-line height tree) |
| Typing in a 12 MB file (large-file mode) | p50 3.7 ms, p99 6.2–6.9 ms per keystroke (AvaloniaEdit's own per-line cost) |
| Tokeniser memory | 2 MB of C# (66,026 lines): token store 4.1 MB per MB of source (estimate); whole process delta incl. view, colour tables and document 22.4 MB per MB; after the last view is disposed the token arrays are freed (2.0 MB of per-line slots remain) |
| Steady-state typing allocation, UI thread | 50-line document: 158 KB/keystroke (bare AvaloniaEdit: 19 KB); 6000 lines: 678 KB (bare: 538 KB). This module's overhead ≈ 140 KB, **independent of document size**; most of it is the TextMate colorizer's `ChangeLinePart` calls on the edited line (it grows with the number of tokens on that line) |
| Memory flatness | 500 documents + views opened/closed: every document, view, editor and buffer collected; 500 view recreations from saved state with an edit in each: none retained (`PerfMemoryTests`) |

## 7. Settings, commands, large files

**Settings read live** (`IConfigurationService.Changed`; per-language `"[lang]": {…}` overrides for every key below, read in each shape a section
comes in — dotted keys as written, `{"editor.tabSize": 2}`, the nested form the Platform settings service hands back, `{"editor": {"tabSize": 2}}`,
or a mix (T30: only the flat form was read, so in the real app every per-language editor setting was ignored); a wrongly typed override falls
back to the global value; `files.*` and `editor.formatOnSave` per language at save; the size limits are global; `.editorconfig` indent style/size
win over settings as in VS Code, and the status bar's indent item shows that same result through `EditorSettings.IndentationFor` (it read the
settings alone, so a project's `.editorconfig` could make it say "Spaces: 4" while the editor indented with tabs or by 2)):
`editor.fontFamily`, `editor.fontSize`, `editor.fontLigatures` (on by default; §8), `editor.lineHeightFactor`, `editor.tabSize`, `editor.insertSpaces`, `editor.wordWrap` (on/off), `editor.renderWhitespace` (`boundary` is treated as `all`), `editor.lineNumbers` (on/off), `editor.rulers`, `editor.mouseWheelZoom`, `editor.guides.indentation`,
`editor.renderLineHighlight`, `editor.bracketPairColorization.enabled`, `editor.matchBrackets`, `editor.occurrencesHighlight`, `editor.folding`, `editor.quickSuggestions`, `editor.quickSuggestionsDelay`, `editor.suggestOnTriggerCharacters`, `editor.autoClosingBrackets/Quotes`, `editor.autoSurround`,
`editor.formatOnSave/OnType/OnPaste`, `editor.emptySelectionClipboard`, `editor.selectionClipboard`, `editor.autoIndentOnPaste`, `editor.autoIndent`, `editor.hover.delay`, `editor.inlayHints.enabled`, `workbench.colorTheme`, `files.encoding`, `files.eol`, `files.insertFinalNewline`, `files.trimFinalNewlines`, `files.trimTrailingWhitespace`, `files.associations`, and the module's own `cscode.editor.undoLimit` (default 1000 groups),
`cscode.editor.maxFileSizeBytes` (200 MB), `cscode.editor.largeFileLongLineChars` (20,000), `cscode.editor.largeFileThresholdBytes` (5 MB), and
`editor.syntaxEngine` (`auto`; the old `editor.experimental.treeSitter` is read as an alias; see docs/TREE-SITTER.md).

**Commands** (VS Code ids): `editor.action.commentLine`, `addCommentLine`, `removeCommentLine`, `blockComment`, `moveLinesUpAction`, `moveLinesDownAction`, `copyLinesUpAction`, `copyLinesDownAction`, `deleteLines`, `indentLines`, `outdentLines`, `joinLines`, `transposeLetters`, `trimTrailingWhitespace`,
`sortLinesAscending/Descending`, `smartSelect.expand/shrink`, `selectAll`, `undo`, `redo`, `actions.find`, `editor.action.showHover` (at the caret),
`editor.action.marker.next`/`marker.prev` (next / previous problem, wrapping; the caret goes there and the hover shows it), `editor.action.startFindReplace` (`actions.find` and it put the keyboard in the find box at once, also the first time the panel opens and headless: T20), `workbench.action.gotoLine`, `editor.action.triggerSuggest`, `triggerParameterHints`, `quickFix`, `organizeImports`, `revealDefinition`, `goToDeclaration`, `goToTypeDefinition`,
`goToImplementation`, `goToReferences`, `rename`, `formatDocument`, `formatSelection`, `editor.fold`, `foldRecursively`, `unfold`, `unfoldRecursively`, `toggleFold`, `foldAll`, `unfoldAll`, `editor.action.insertSnippet`, `editor.debug.action.toggleBreakpoint`.

### 7a. Clipboard and the right-click menu

`View/EditorClipboard.cs` (one per view, also in large-file mode) replaces AvaloniaEdit's Cut/Copy/Paste with Visual Studio's behaviours, computed by
the pure `Editing/ClipboardOperations.cs` and applied as ONE undo unit:

* **Whole lines**: Ctrl+C / Ctrl+X on a caret copy / cut the line with its line ending (`editor.emptySelectionClipboard`, default on); pasting such a
  line inserts it above the caret's line, the caret staying on its own line. The clipboard text is plain; the "whole line" shape travels as CsCode
  metadata (`ClipboardText.Shape`), which the shell's clipboard service hands back only while the clipboard still holds that exact text.
* **Several carets** are the rows of a box selection (Alt+drag; a zero-width box is a column of carets, which is how this editor, without a
  multi-caret model, expresses Visual Studio's multi-caret). Copy takes one piece per row (or each caret's whole line); a paste puts one line or
  piece per row when the counts match, a single line into every row, else the whole text at each. The caret ends after the last row's paste.
* **Box (column) selection** copies as a block (`Shape = Block`, one piece per row) and a block pastes as a block: row *i* at the caret's visual
  column on line caret+*i* (tabs expanded), short lines padded with spaces, missing lines appended.
* **Line endings** are converted to the document's EOL on paste (CRLF, CR, LF). Unicode is untouched (emoji, combining marks, RTL).
* **Auto-indent on paste** (`editor.autoIndentOnPaste`, default on; off when `editor.autoIndent` is `none`; skipped above 512 KB): the lines
  after the first keep their indentation relative to the copied line (`ClipboardText.SourceIndent`, else inferred from the text) and are
  re-based where the paste lands (the caret's column when only whitespace precedes it, else the line's indentation); whole lines pasted above a
  line get the indentation Enter would give there (the language rules). Then `editor.formatOnPaste` asks the language server for range
  formatting of the pasted range, as its own undo step (Ctrl+Z first undoes the formatting, then the paste, as in Visual Studio).
* **Clipboard ring**: Ctrl+Shift+V pastes the newest ring entry; pressing it again right away (caret still after it, nothing else edited)
  replaces it in place with the next older one, cycling; the whole cycle is one undo step (a continued undo group). The ring itself is the shell's
  (`IClipboardService.Ring`, 20 entries, in memory only).
* **PRIMARY** (Linux, `editor.selectionClipboard`, default on there and off elsewhere): a non-empty selection is offered as PRIMARY once per UI
  turn, lazily (the text is produced from the immutable snapshot only when someone pastes it); a middle-click moves the caret to the click and
  inserts PRIMARY there (line endings converted, one undo step).
* **Right-click** moves the caret to the click unless it lands inside the selection, and focuses the view.
* **No waiting**: the UI thread starts a read and continues; a paste lands when the clipboard owner answered (the shell's service bounds the wait).
  A 10 MB paste goes in whole (one `ApplyEdits`), one Ctrl+Z removes it. Copies and pastes of remote documents use the local clipboard.
* Without the shell's `IClipboardService` (standalone), `Fallbacks/LocalClipboardService` uses the window's clipboard and an in-memory ring.
* **Copy with Formatting** (`editor.action.clipboardCopyWithSyntaxHighlightingAction`, VS Code's id; right-click menu, the shell's Edit menu;
  `IEditorViewController.CopyWithFormattingAsync`, additive): the plain copy plus `ClipboardText.Html`, an HTML fragment
  (`Editing/HtmlClipboard.cs`) in VS Code's shape: an outer `div` with the theme's `editor.background` / `editor.foreground`, the editor font (`editor.fontFamily` as
  written, so Fira Code by name, not the `fonts:CsCode#` collection the view draws it from) and size, `white-space: pre`, one `div` per line and one `span` per run of equal style (colour, background, bold, italic, underline). The colours
  are the highlighter's published token rows (TextMate or tree-sitter: both publish into the session's token store) resolved against the
  current theme, so the paste looks like the editor; a line not highlighted yet is plain. The UI thread only gathers the lines and token rows;
  styles are resolved and the HTML written on a worker. Above 64K characters (`HtmlClipboard.MaxChars`, VS Code's limit) only the text is
  copied. Plain Copy stays text only (VS Code's `editor.copyWithSyntaxHighlighting` is not read).

**Right-click menu** (`View/EditorMenus.cs`): the editor contributes its menu to the shell through `IContributionRegistry` (`editor/context`), in
Visual Studio 2022's wording and order: Quick Actions and Refactorings... (Ctrl+.), Rename... | Peek Definition, Go To Definition, Go To Declaration,
Go To Implementation, Find All References | Breakpoint > (Insert Breakpoint / Delete Breakpoint, Disable / Enable Breakpoint, Conditions...), Run To
Cursor (Ctrl+F10), Add Watch, QuickWatch... (Shift+F9) | Snippet > (Insert Snippet... Ctrl+K Ctrl+X, Surround With... Ctrl+K Ctrl+S) | Cut, Copy,
Paste | Format Document, Format Selection, Comment Selection, Uncomment Selection | Command Palette.... Items that cannot apply are hidden: the
language items follow VS Code's provider keys the editor keeps for the focused document (`editorHasCodeActionsProvider`, `editorHasRenameProvider`,
`editorHasDefinitionProvider` and its declaration / type-definition / implementation siblings: one provider interface serves all four, so they
come and go together, `editorHasReferenceProvider`, `editorHasDocumentFormattingProvider`, `editorHasDocumentSelectionFormattingProvider`); the
debug items follow `inDebugMode` / `debugState` and the caret-line keys `cscode.editor.breakpointsAvailable`, `lineHasBreakpoint`,
`lineBreakpointEnabled`, which the view sets when the menu is requested (`PrepareContextKeys`, O(1)); Cut/Copy follow `cscode.editor.canCutCopy`;
Peek Definition stays hidden (not implemented). Rename enables when the server's prepareRename answers; Paste when the clipboard says it holds
text (`EnablementCheck`, never awaited on the UI thread). Shift+F10, the Menu key and `editor.action.showContextMenu` open it at the caret
(`IEditorViewController.GetCaretBounds`).

Commands added: `editor.action.clipboardCutAction`, `clipboardCopyAction`, `clipboardPasteAction` (the shell's focus-following registration
shadows these in the app; docs/SHELL.md), `cscode.editor.cycleClipboardRing`, `editor.action.showContextMenu`, `cscode.editor.insertSnippetPicker`,
`editor.action.surroundWithSnippet`, `editor.debug.action.runToCursor` (a temporary breakpoint at the caret's line, then Continue, or Start when
not debugging; removed at the next stop or when the session ends), `editor.debug.action.selectionToWatch` (Add Watch with the selection or the
word at the caret, through the Debug panels' `debug.addToWatch`), `cscode.debug.quickWatch` (evaluates in the stopped frame, shows the value with
an Add Watch action: a lighter QuickWatch, no tree), `debug.enableOrDisableBreakpoint` (Ctrl+F9), `editor.debug.action.conditionalBreakpoint`.
Keys contributed (built-in, below the user's keybindings.json): Ctrl+X / Shift+Delete, Ctrl+C / Ctrl+Insert, Ctrl+V / Shift+Insert, Ctrl+Shift+V,
Ctrl+F10, Shift+F9, Ctrl+F9, Ctrl+K Ctrl+S, Ctrl+K Ctrl+X. Contract additions (default members, so other implementations keep compiling):
`IEditorViewController.CanCut/CanCopy/CanPaste`, `CutAsync/CopyAsync/PasteAsync`, `GetCaretBounds`, `PrepareContextMenu`.

Tests: `ClipboardOperationsTests` (pure), `EditorClipboardViewTests` (keys, undo, ring, box, PRIMARY and middle-click, right-click, format on paste,
read-only, a slow clipboard owner, 10 MB, contributions, context keys, Run To Cursor, retention), `RemoteClipboardTests`.

**Large-file mode**: above `cscode.editor.largeFileThresholdBytes` (default 5 MB) or with a line longer than 20,000 characters the document's language is forced to `plaintext`, and its views get no tokenisation, folding, squiggles, bracket matching or any other language feature (just text); files over 200 MB are refused with a clear `FileTooLargeException` message.

## 8. Known gaps (honest list)

Not done, or done only in part — none of this is faked:

* **Multi-cursor** (Alt+Click, Ctrl+Alt+Up/Down, Ctrl+D) is not implemented: AvaloniaEdit has no multi-caret model and the stretch goal was not attempted. Rectangular selection (AvaloniaEdit's own) works, and its rows act as carets for the clipboard (§7a); after a paste into several rows a single caret remains.
* **Minimap, code lens, semantic-token overlay, Alt+F12 peek, overview-ruler markers on the scrollbar** are not implemented (settings for them are read but have no effect). Sticky scroll is (`editor.stickyScroll.enabled`, off by default; docs/TREE-SITTER.md §5).
* `editor.cursorStyle`, `editor.cursorBlinking` and `editor.smoothScrolling` are not applied (AvaloniaEdit offers no equivalent). Font ligatures are on by default (`editor.fontLigatures`; VS Code defaults to off, but the bundled Fira Code is chosen for them, and they keep one glyph per character): `false` turns them off. AvaloniaEdit 12 has no font-feature setting, so `EditorFontFeatures`, a line transformer, then gives each element's run properties `-liga, -calt` (reaching the private `VisualLineElement.TextRunProperties` setter by reflection; `CodeFontTests` fails if an upgrade removes it). `true` (the default) leaves the font's own ligatures (Fira Code's keep one glyph per character, so carets stay on columns) and a string is VS Code's CSS feature list (`"'ss01', 'zero'"`). The code panels follow the same setting through `ui:CodeFontFeatures`. `editor.wordWrap` supports on/off only. Drag-and-drop of text is AvaloniaEdit's own behaviour; bidirectional text and IME composition were not tested.
* **Typing allocation**: the TextMate colorizer allocates per token on the edited line (§6). A single pre-merged element list would cut it; not done.
* The `previousLineText` guard of `onEnterRules` (which Core's `OnEnterRule` record cannot carry) is handled editor-side through `ExtendedLanguageConfiguration`/`EnterRuleEx`; the Core record is unchanged.
* `UndoStackScrubber` is reflection against AvaloniaEdit 12.0.0's private layout; `PerfMemoryTests` will fail loudly if an upgrade changes it (use `RetentionPath` to find the new culprit).
* Debug margin: the right-click menu is an Avalonia `ContextMenu` (its items are exercised through the same methods the tests call; the popup itself is not clicked in tests). Capability checks (`SupportsConditionalBreakpoints` …) are not consulted before offering the menu items.
* Platform features that Platform may ship later (`IEditorConfigSource`, per-language configuration, `IMemoryMonitor`) are consumed when registered; the fallbacks are what the tests exercise.

## 9. Tests

`dotnet test tests/CsCode.Editor.Tests` (headless Avalonia; real temp files, real `FileSystemWatcher`, real TextMate grammars and the native Oniguruma). ≈ 650 tests, ≈ 1.5 minutes. Highlights:

* `EditorDocumentFuzzTests` (oracle comparison), `EditorDocumentFileTests` (encodings, EOL, atomic save, watcher, conflicts, large files), `LanguageServiceTests`, `ThemeTests` (real Dark+/Monokai/TypeScript/Python/C# JSON), `TextMateTests` (golden scopes), `SnippetEngineTests`, `EditingOperationsTests`, `EditorConfigTests`.
* View tests: `EditorViewTests`, `CompletionViewTests`, `DiagnosticsHoverTests`, `SignatureActionsNavigationTests`, `BracketsHighlightsDebugTests`, `ShouldHaveFeatureTests`.
* Hostile/perf/leak: `HostileProviderTests`, `BlockingDetectorTests`, `KeystrokeAllocationTests`, `PerfMemoryTests` (opens 1/10/50 MB files, tokeniser memory, 500× open/close, 500× recreate, popups open). Leak failures print the chain of strong references (`RetentionPath`).
* `ScreenshotTests` write PNGs of the dark and light themes (code with squiggles, breakpoints, the execution line, a folded region, bracket/occurrence highlights; the completion, hover and signature popups) to `.scratch/screenshots/` (git-ignored) and assert the content really is on screen. Visual defects found by looking at them and fixed: invisible match highlight on the selected completion row in the light theme (the "focus highlight" colour was missing).

## 10. Third-party content

AvaloniaEdit (MIT), TextMateSharp and TextMateSharp.Grammars (MIT; the bundled grammars and 15 of its 21 themes are Microsoft's VS Code built-ins, MIT; the other six are TextMateSharp's own, see THIRD-PARTY-NOTICES.md), the Oniguruma regex engine through `TextMateSharp.Onigwrap` (BSD), Avalonia (MIT). No icon font, Codicon or other artwork is embedded.
