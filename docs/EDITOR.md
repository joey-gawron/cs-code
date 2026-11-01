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
`ReplayText`, `ReplayKey`, `OpenUndoGroup`, `StartNewUndoStep` (below); `SetSelections` (multi-cursor, §7b; `Selections` lists the primary
first, then the other carets in document order). The later additions are default interface members, so other implementations keep
compiling. **No change was made to CsCode.Core.**

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

**Scroll hooks and the Markdown preview (docs/MARKDOWN.md).** `IEditorViewController.TopLine` (the 0-based line at the top, fractional: from
AvaloniaEdit's visual lines, so folding and wrapping count), `Scrolled` (raised with the new top line when the view scrolls; hooked to
`TextView.ScrollOffsetChanged` on first subscription) and `ScrollToLine(line)` (sets the scroll offset, leaves the caret) couple an editor with
its preview. The preview itself (`Markdown/Preview`, `MarkdownPreviewFactory` registered by `AddCsCodeEditor`) parses with Markdig on the editor
worker and colours code blocks with `CodeHighlighter.HighlightCode` (same grammars and theme as the editor); the hover/completion `MarkdownParser`
is unchanged. In Markdown files Visual Studio's Cycle Clipboard Ring key (Ctrl+Shift+V / Cmd+Shift+V) is left to Open Preview in the VS Code keymap; the Visual Studio keymap keeps it for the ring there too.

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
  *no* `ICommandService` exists (standalone) does the editor bind F12 / Shift+F12 / Alt+F12 / F2 itself.
* **Find references**: with more than one result the editor runs the command `cscode.references.show` (args: `IReadOnlyList<Location>`, a title string) if the shell registered
  it — the shell's Find Results presents them — and otherwise shows them in `IWindowService.ShowQuickPickAsync`. One result just navigates.
* **Breakpoints follow edits** by calling `IBreakpointManager.ApplyLineShift(uri, fromLine, delta)`. The editor's contract (the Core interface does not say): every breakpoint at
  a line `>= fromLine` moves by `delta`; breakpoints on lines that no longer exist are `Remove`d by the editor itself *before* it calls `ApplyLineShift`.
  (A newline typed at column 0 shifts from that line; deleting whole lines from column 0 removes exactly those lines' breakpoints.)
* **GC mode**: the figures in §6 were measured with `GCSettings.LatencyMode = SustainedLowLatency` (no blocking gen-2 collections while typing); the shell should run
  the app in that mode. Typing allocates ≈ 90 KB per keystroke on the UI thread whatever the document's size (§6; it was hundreds of KB to
  tens of MB before the fixes in §5).
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
10. **Debug integration** (all null-safe without a debugger) — breakpoint glyph margin (click toggles; right-click: condition / hit count / logpoint (each disabled with the reason when the active adapter lacks it, `DebugCapabilityGate`) / enable / remove; red filled = verified, hollow grey = disabled or unverified; conditional and logpoint shapes; tooltips with the adapter's message; ghost dot on hover),
    execution arrow + line highlight with auto-reveal, breakpoints that follow edits, hover-evaluate (`EvaluateAsync(expr, frameId, "hover")`).
11. `AddCsCodeEditor`, this document.

12. **Clipboard and the right-click menu** (clipboard wave, `docs/briefs/clipboard-and-context-menus.md`): see §7a.

13. **Multi-cursor** (Add Next Occurrence, Select All Occurrences, Insert Cursor Above/Below, Alt+Click, editing at every caret as one undo step): see §7b.

14. **Warm-up editor** (`EditorWarmUp`): a real view over a read-only document that is not registered with `IDocumentManager` (URI scheme
    `cscode-warmup:`, the same kind of document as a diff side), for the warm spare to render once before it is claimed so the first file a
    session opens does not pay the editor's first construction, layout, grammar load and tokenisation; `Dispose` leaves no view, tokenisation
    session or document behind (docs/SESSIONS.md, "The warm spare"; `EditorWarmUpTests`).

15. **Peek** (Peek Definition / References / Declaration / Type Definition / Implementations: an editor embedded under the line): see §7c.

16. **Linked editing** (`editor.linkedEditing`: tag pairs edited together): see §7d.

17. **Code lens and semantic highlighting** (the LSP results the client had but nothing drew): see §7e.

18. **Overview ruler and minimap** (marks for problems, find matches, occurrences, source-control changes, breakpoints and the carets on the
    scrollbar; a token-coloured minimap with a draggable slider, drawn off the UI thread): see §7f.

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
* **There are no view zones** (space between lines that is not text). A visual line's height is the sum of its text rows' heights, and
  `VisualLine.Height` and the height tree are set inside `BuildVisualLine` with no hook between them. `LineZones` (code lens, §7e) opens a zone
  by making the line's FIRST ROW taller upwards: a zero-width, zero-length element at the line start whose `DrawableTextRun` is as tall as the
  zone plus the text's ascent and sits on the baseline. Everything that follows the baseline (`VisualYPosition.TextTop`, the line numbers, the
  folding markers, hit-testing in the text) is right at once; what uses the row's `LineTop`/`LineBottom` is not: **the caret rectangle spans the
  whole row** (`Caret.CalculateCaretRectangle` = `LineTop`..`LineBottom`), and so do the selection and the current-line highlight. A layer
  inserted above `KnownLayer.Caret` paints each zone with the editor background (then the lens text), which hides all three there, and is
  hit-testable over the zones only. The element needs `HandlesLineBorders` and its own caret stops, or Home stops before it and Left at the text
  start stays on the line; the editor's own gutters (breakpoints, test glyphs, quick diff, inline blame, snippet markers) add `LineZones.HeightOf`
  / `ZoneOffset`. AvaloniaEdit asks no element generator about an empty line, so an empty line cannot carry a zone. At one offset AvaloniaEdit
  goes on through the generators while each element it gets is zero-length and stops at the first that covers text (the next generator is
  asked from the next offset), so the order of `TextView.ElementGenerators` decides what is kept where several start at one offset: the zone
  generator is first, then inlay hints, then AvaloniaEdit's own (folding, whitespace markers and control-character boxes). Column 0 of a lens
  line keeps the zone, an inlay hint and the first indentation's `editor.renderWhitespace` marker; an inlay hint where a whitespace marker
  or a folded region starts (the end of a fold's first line) is drawn before it.
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
* **The text-area theme read the whole document on every edit** (perf-typing): its watermark's `IsVisible` is bound to
  `$templatedParent.Document.Text` with `StringConverters.IsNullOrEmpty`, and the document raises `PropertyChanged("Text")` after each change,
  so every keystroke built the full text as a string — twice the file's size in bytes, on the large object heap (24 MB per key in a 12 MB
  file: a gen-2 collection every few keys, the 35–40 ms large-file typing p99 on macOS; the ≈ 90 B per line per keystroke this section used to
  call AvaloniaEdit's own per-line cost). `TextAreaWatermarkBinding` disposes that binding in every text area as its template is applied
  (template priority: `ClearValue` does not stop it, the expression's `Dispose` does) and keeps the unused watermark hidden; a bare
  themed `TextEditor` now allocates ≈ 14 KB per keystroke at 50 lines and at 6000 (`TextAreaWatermarkBindingTests`, `KeystrokeAllocationTests`).
  AvaloniaEdit still keeps ≈ 130 B per line per view for its height tree.
* **An expanded fold redraws its whole range on every edit inside it**: `FoldingSection.OnSegmentChanged` calls `FoldingManager.Redraw(this)`
  whenever an edit changes the section's length, folded or not, and in code nearly every keystroke is inside a namespace or class region, so
  every keystroke threw away and re-shaped all visible lines (35 per key in `FoldRedrawFilterTests`, ~60 in a full-height editor). An expanded
  section contributes nothing to a visual line, so `FoldRedrawFilter` hides the manager's text views (its internal `TextViews`, all that
  `Redraw(section)` walks) between the document's `Changing` and `Changed` unless a folded section overlaps the edit up to the end of its line
  (reflection against 12.0.0; inert if the member is gone).
* **The folding margin re-creates every marker on every change of the visual lines** (each keystroke): `DetachingFoldingMargin` keeps them when
  the markers it would make (same visual lines, sections and expanded states: AvaloniaEdit's own rule) are the ones it has, and only
  re-arranges and redraws (reflection against the internal `FoldingMarginMarker`; it rebuilds always if that changes).
* **The line-number margin shapes every visible number on every keystroke**: it re-measures and re-renders after each change of the visual
  lines with a new `FormattedText` per number (formatted and shaped again when drawn, once more for the width): about a fifth of the UI
  thread's work per key in a profile on macOS. `CachedLineNumberMargin` replaces it (after `ShowLineNumbers` inserts it) and keeps the laid-out
  number (`TextLayout`) of each line drawn in the last frame; its frame is pixel-identical to the stock margin's (`CachedLineNumberMarginTests`).
* `TextView.Redraw(offset, length)` **also drops the line before the range**: it compares the range start with each line's end *including the
  delimiter* inclusively, so a range starting at a line's first character takes the previous line too. The colorizer starts its row redraw one
  character into the first line (`TokenRedrawTests`).

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

Debug build, shared 22-core Linux machine unless a row names the arm64 Mac (shared with other agents: the load average is given); ranges are the spread of repeated runs.

| What | Result |
|---|---|
| Open 1 MB C# file | 9–13 ms; view + first layout 47–54 ms; ≈ 8 MB managed (a fixed ≈ 7 MB of tables included) |
| Open 10 MB (large-file mode) | 74–81 ms; UI thread blocked ≤ 0.3 ms during the open; view + layout 138 ms; 15 MB managed after open (1.5 × file size), 64 MB with a view |
| Open 50 MB (large-file mode) | 604–688 ms; UI latency during open max 1.1–13 ms; view + layout 575 ms; 101–114 MB after open (2.0–2.3 ×), 342–355 MB with a view (AvaloniaEdit's per-line height tree) |
| Typing in a 12 MB file (large-file mode) | p50 0.02 ms, p99 0.17 ms per keystroke on Linux (was p50 3.7 ms, p99 6.2–6.9 ms); on the arm64 Mac p50 0.14–0.21 ms, p99 0.36–0.81 ms, against p50 2.3–2.8 ms, p99 23–40 ms on main, all under load 9–37 (perf-typing: the whole text was built as a string per keystroke, §5) |
| Typing in a 400 KB C# file, full-height view (1400×1000), keystroke + the frame after it | arm64 Mac, under load 9–37: frame p50 4.5–7.3 ms, p90 9.6–15 ms (main: p50 13–17.6 ms, p90 22–34 ms); 0.4–0.5 MB allocated per key (main: 2.7–3.0 MB); the keystroke itself p50 1.0–1.3 ms (main 0.8–1.1 ms: the edited line is built once in both). Linux: frame p50 3.5 ms, p90 7.4 ms, 0.39 MB per key (perf-typing: the fold redraw, line-number shaping, folding-margin markers and the neighbour redraw, §5) |
| Tokeniser memory | 2 MB of C# (66,026 lines): token store 4.1 MB per MB of source (estimate); whole process delta incl. view, colour tables and document 22.4 MB per MB; after the last view is disposed the token arrays are freed (2.0 MB of per-line slots remain) |
| Steady-state typing allocation, UI thread | 50-line document: 89 KB/keystroke (bare AvaloniaEdit: 14 KB); 6000 lines: 93 KB (bare: 14 KB); before perf-typing 158 KB and 678 KB (bare 19 KB and 538 KB, the watermark binding of §5). This module's overhead ≈ 75–80 KB, **independent of document size**; most of it is the TextMate colorizer's `ChangeLinePart` calls on the edited line (it grows with the number of tokens on that line) |
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
`cscode.editor.maxFileSizeBytes` (200 MB), `cscode.editor.largeFileLongLineChars` (20,000), `cscode.editor.largeFileThresholdBytes` (5 MB),
`editor.syntaxEngine` (`auto`; the old `editor.experimental.treeSitter` is read as an alias; see docs/TREE-SITTER.md), and
`editor.multiCursorLimit` (10,000) / `editor.multiCursorPaste` (`spread`) (§7b; the factory registers both, so the Settings panel lists them),
`editor.linkedEditing` (off, VS Code's default; §7d; registered by the factory too),
`editor.codeLens` (true), `editor.codeLensFontSize` (0 = 90 % of the editor's), `editor.codeLensFontFamily` ("" = the editor's) and
`editor.semanticHighlighting.enabled` (`configuredByTheme`) (§7e; registered by the factory too),
and the minimap's and overview ruler's `editor.minimap.enabled` (true), `.side`, `.renderCharacters` (true, VS Code's default), `.maxColumn`, `.showSlider`, `.scale`,
`editor.overviewRulerLanes`, `editor.overviewRulerBorder`, `editor.hideCursorInOverviewRuler`, `debug.showBreakpointsInOverviewRuler` (§7f).

**Commands** (VS Code ids): `editor.action.commentLine`, `addCommentLine`, `removeCommentLine`, `blockComment`, `moveLinesUpAction`, `moveLinesDownAction`, `copyLinesUpAction`, `copyLinesDownAction`, `deleteLines`, `indentLines`, `outdentLines`, `joinLines`, `transposeLetters`, `trimTrailingWhitespace`,
`sortLinesAscending/Descending`, `smartSelect.expand/shrink`, `selectAll`, `undo`, `redo`, `actions.find`, `editor.action.showHover` (at the caret),
`editor.action.marker.next`/`marker.prev` (next / previous problem, wrapping; the caret goes there and the hover shows it), `editor.action.startFindReplace` (`actions.find` and it put the keyboard in the find box at once, also the first time the panel opens and headless: T20), `workbench.action.gotoLine`, `editor.action.triggerSuggest`, `triggerParameterHints`, `quickFix`, `organizeImports`, `revealDefinition`, `goToDeclaration`, `goToTypeDefinition`,
`goToImplementation`, `goToReferences`, `rename`, `formatDocument`, `formatSelection`, `editor.fold`, `foldRecursively`, `unfold`, `unfoldRecursively`, `toggleFold`, `foldAll`, `unfoldAll`, `editor.action.insertSnippet`, `editor.debug.action.toggleBreakpoint`,
multi-cursor's `editor.action.addSelectionToNextFindMatch`, `moveSelectionToNextFindMatch`, `selectHighlights`, `insertCursorAbove`, `insertCursorBelow`, `cursorUndo`, `removeSecondaryCursors` (§7b),
the peek's `editor.action.peekDefinition`, `peekDeclaration`, `peekTypeDefinition`, `peekImplementation`, `referenceSearch.trigger` (Peek References), `closeReferenceSearch` (§7c)
and `editor.action.linkedEditing` (Start Linked Editing, §7d),
and `editor.action.toggleMinimap` (View: Toggle Minimap, §7f).

### 7a. Clipboard and the right-click menu

`View/EditorClipboard.cs` (one per view, also in large-file mode) replaces AvaloniaEdit's Cut/Copy/Paste with Visual Studio's behaviours, computed by
the pure `Editing/ClipboardOperations.cs` and applied as ONE undo unit:

* **Whole lines**: Ctrl+C / Ctrl+X on a caret copy / cut the line with its line ending (`editor.emptySelectionClipboard`, default on); pasting such a
  line inserts it above the caret's line, the caret staying on its own line. The clipboard text is plain; the "whole line" shape travels as CsCode
  metadata (`ClipboardText.Shape`), which the shell's clipboard service hands back only while the clipboard still holds that exact text.
* **Several carets** are the carets of multi-cursor (§7b) or the rows of a box selection (Alt+drag on Linux and Windows, Ctrl+Option+drag on
  macOS, AvaloniaEdit's own gesture; a zero-width box is a column of carets). Copy takes one piece per caret or row (or each caret's whole line);
  multi-cursor pieces are joined by line breaks as plain text (VS Code), so pasting them at one caret inserts the lines, not a column block. A
  paste puts one line or piece per caret when the counts match (`editor.multiCursorPaste`: `spread`; `full` puts everything at each), else the
  whole text at each. Multi-cursor carets stay, one after each piece; after a paste into a box's rows a single caret remains, after the last row's
  paste. Cut at several carets removes each selection (or each caret's line) and keeps a caret at each.
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
Go To Implementation, Find All References (then View Call Hierarchy and View Type Hierarchy, contributed by the Hierarchy window's module,
docs/NAVIGATION.md) | Breakpoint > (Insert Breakpoint / Delete Breakpoint, Disable / Enable Breakpoint, Conditions...), Run To
Cursor (Ctrl+F10), Add Watch, QuickWatch... (Shift+F9) | Snippet > (Insert Snippet... Ctrl+K Ctrl+X, Surround With... Ctrl+K Ctrl+S) | Cut, Copy,
Paste | Format Document, Format Selection, Comment Selection, Uncomment Selection | Command Palette.... Items that cannot apply are hidden: the
language items follow VS Code's provider keys the editor keeps for the focused document (`editorHasCodeActionsProvider`, `editorHasRenameProvider`,
`editorHasDefinitionProvider` and its declaration / type-definition / implementation siblings: one provider interface serves all four, so they
come and go together, `editorHasReferenceProvider`, `editorHasDocumentFormattingProvider`, `editorHasDocumentSelectionFormattingProvider`); the
debug items follow `inDebugMode` / `debugState` and the caret-line keys `cscode.editor.breakpointsAvailable`, `lineHasBreakpoint`,
`lineBreakpointEnabled`, which the view sets when the menu is requested (`PrepareContextKeys`, O(1)); Cut/Copy follow `cscode.editor.canCutCopy`;
Peek Definition shows with a definition provider (§7c; it was hidden while no module registered its command). Rename enables when the server's prepareRename answers; Paste when the clipboard says it holds
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

### 7b. Multi-cursor

`View/MultiCursorController.cs` (one per view, also in large-file mode) adds carets on top of AvaloniaEdit, which has one. The **primary** caret
and selection stay AvaloniaEdit's own, so every single-caret feature (completion, hover, signature help, find, the status bar's Ln/Col, Vim's
caret shapes) is untouched; the others live in an immutable `Editing/CaretSet` of document offsets (sorted, merged when they overlap or meet,
one of them the primary). The editing itself is pure (`Editing/MultiCaretOperations`, unit tested without a view): each operation turns the
caret set into one batch of edits plus the carets after it; the view applies the batch back to front inside one buffer update, which is ONE
change event (one LSP `didChange`, one snapshot) and ONE undo step.

| What | How |
|---|---|
| **Add Next Occurrence** (`editor.action.addSelectionToNextFindMatch`) | A caret selects its word first; each further press adds the next occurrence after the primary (wrapping) as the new primary, revealed. Started from a word it matches whole words with matching case, from a selection the selected text ignoring case (VS Code's find defaults). An occurrence already selected is skipped; when all are, nothing happens. |
| **Move to Next Occurrence** (`moveSelectionToNextFindMatch`) | The primary selection jumps to the next occurrence instead of adding one. |
| **Select All Occurrences** (`selectHighlights`) | Every occurrence of the selection (or of the word at the caret, whole word and case) becomes a selection, the one at the old primary staying primary. Searches a snapshot in 64K chunks; above 4M characters on a worker (dropped if the text changed meanwhile). |
| **Insert Cursor Above / Below** (`insertCursorAbove` / `insertCursorBelow`) | A caret on the line above / below each caret at the same visual column (tabs expanded; the column is kept across shorter lines); the new outermost one is primary, so repeating extends the column. |
| **Alt+Click** (Option+Click on macOS) | Adds a caret at the click, or removes the caret clicked on. AvaloniaEdit's own Alt+press (the start of its box selection) is let through: a release within 3 px of the press is a click, anything further is a drag and the box selection (Linux) or stream selection (macOS, where AvaloniaEdit's box wants Ctrl+Option) stands. |
| **Cursor Undo** (`cursorUndo`) | Back to the carets before the last caret command (the ones above and Alt+Click), up to 50 states; states from before an edit are dropped. |
| **Escape** / `removeSecondaryCursors` | Back to the primary alone, after the completion list, parameter hints and a snippet session have had their Escape. |
| Typing, Backspace / Delete (Ctrl / Option for a word; Shift+Backspace is Backspace), Enter, Tab / Shift+Tab | At every caret: each selection replaced; Backspace and Delete take a CRLF or a surrogate pair as one character; word deletes that reach over other carets merge; Enter gives each new line the language's indentation (onEnterRules, brackets: `{|}` splits) and drops the blanks after the caret, as the single-caret Enter does; Tab inserts spaces to the next tab stop (or a tab), or indents the lines of a multi-line selection, Shift+Tab outdents. A typed run is one undo step (continued while nothing else happens, as single-caret typing). |
| Arrows, Home / End, Ctrl+Left/Right (Option on macOS), ⌘←/⌘→ on macOS, Shift for selection | Every caret moves; Left/Right collapse a selection to its side; Up/Down keep a visual column, the first line's Up goes to its start and the last line's Down to its end; Home is smart (first non-blank, then column 0). Carets that meet merge. Up/Down move by document lines (not wrapped rows). |
| **Undo / Redo** | An edit's undo step carries two markers (`EditorDocument.AddUndoMarker`): undo puts back the carets from before it, redo the carets after it, applied when the buffer's update ends. Only for edits with at most 1,000 carets (a snapshot per step); above that undo leaves AvaloniaEdit's caret alone. The markers hold the controller weakly, so history never keeps a view alive. |
| **Clipboard** | §7a: copy joins the selections with line breaks, paste spreads one line per caret, cut keeps a caret at each. |
| **Completion** | Typing at several carets triggers completion and signature help at the primary (the typing path's `Typed` event); accepting a plain completion replaces the same prefix and suffix around every caret (VS Code's rule), in one step. A snippet completion, Insert Snippet, Format, the line commands (move / copy / comment lines) act at the primary, and the caret move that follows collapses the others. |
| **Edits from elsewhere** | A language server's edit, another view of the document, undo of an older step: the carets follow all the changes of the buffer's update at once when it ends (`ChangeSequenceMapper`: the same offsets as following each change in turn, but by binary search when the changes run back to front, as a batch and its redo are applied, or front to back, as AvaloniaEdit's undo replays one, so O(carets × log changes); other orders go change by change), then the primary is taken from AvaloniaEdit and carets that met merge. An undo that restores its carets maps nothing. Following each change as it came made undo of a 10,000-caret step take seconds (one change per caret, each moving every caret). |
| **Collapsing** | Anything else that moves the primary (a click, Page Down, Ctrl+Home, Ctrl+A, Go to Definition, Find Next, `SetSelection`) collapses the others to it, as in VS Code. |
| **Vim** | While a modal layer is attached (`EditorModalSurface.Attach`, so whenever Vim mode is on, which is the default) multi-cursor is off: attaching collapses the carets, the commands say "Multiple cursors are not available while Vim mode is on" in the status bar and add nothing, Alt+Click adds nothing. Turn Vim off (`vim.enable: false` or Toggle Vim Mode) to use it. |
| **Macros** | Recorded as what they are: the keys (replayed through `ReplayKey`, which reaches the controller's handler), typed text (`ReplayText`, through `TextEntering`) and the commands. Alt+Click is a mouse gesture and is not recorded. |
| **Read-only documents** | Carets can be added and moved; typing and the editing keys do nothing (and never edit at the primary alone). |
| **Limits** | `editor.multiCursorLimit` (default 10,000, VS Code's id and default): the carets nearest the primary are kept and the status bar says "The number of cursors has been limited to N.". A keystroke costs O(carets × log document) plus each caret's own line; nothing per caret scans the document. Drawing visits only the carets in the visible lines (binary search). Measured: typing a character at 10,000 carets is one change event and one undo step, and its undo and redo move the carets along their 10,000 changes at once (`MultiCursorViewTests`). |

Drawing: two `IBackgroundRenderer`s, the secondary selections on the selection layer (theme `editor.selectionBackground`, AvaloniaEdit's corner
radius) and 2 px carets in `editorCursor.foreground` on the caret layer, blinking every 530 ms while the view has the keyboard. The blink timer
runs only while there are several carets and the view is focused, and stops on collapse and dispose. With one caret every handler returns at
once (no allocation on the typing path). Context key: the shell sets VS Code's `editorHasMultipleSelections` from the controller's
`Selections` (docs/SHELL.md).

Keys (`src/CsCode.Platform/Resources/Keymaps`): the **VS Code** preset has VS Code's Ctrl+D, Ctrl+K Ctrl+D, Ctrl+Shift+L, Ctrl+Alt+Up/Down and
Ctrl+U (⌘D, ⌘K ⌘D, ⇧⌘L, ⌥⌘↑/↓, ⌘U on macOS). The **Visual Studio** preset (the default) keeps Visual Studio's own meaning of those keys,
Ctrl+D Duplicate, Ctrl+K Ctrl+D Format Document and Ctrl+Shift+L Delete Line, and binds Visual Studio 2022's multi-caret keys instead:
Shift+Alt+. (Insert Next Matching Caret = Add Next Occurrence) and Shift+Alt+; (Insert Carets at All Matching = Select All Occurrences), plus
Ctrl+Alt+Up/Down (⌥⌘↑/↓) and Ctrl+U (⌘U); Move to Next Occurrence has no Visual Studio key (Command Palette). Ctrl+U is bound with
`!vim.active` in both (Vim owns it). Escape and Alt+Click are the editor's own.

Contract: `IEditorViewController.Selections` lists the primary first, then the other carets in document order; `SetSelections(list)` sets
several (the first is primary; one only while Vim is on); `SetSelection` collapses them.

Tests: `MultiCaretModelTests` (the caret set, every edit and movement, the occurrence search across chunk seams), `ChangeSequenceMapperTests` (offsets through a batch, its undo and any other order, against change-by-change mapping), `MultiCursorViewTests`
(each command, Alt+Click and Alt+drag, typing and the keys with undo/redo of text and carets, clipboard, completion, macros' replay, edits from
elsewhere, collapsing, Vim, read-only, the limit, 10,000 carets with their undo and redo, drawing and the blink timer), `RealApp/MultiCursorTests` (the preset keys
through the real keyboard pipeline, `editorHasMultipleSelections`, Vim on), `KeybindingServiceTests` (both presets on Linux and macOS).

### 7c. Peek

`Features/PeekFeature.cs` (one per view, not in large-file mode): VS Code's peek view and Visual Studio's Peek Definition. **Alt+F12** (both
presets; `editor.action.peekDefinition`, the right-click menu's Peek Definition), `peekDeclaration`, `peekTypeDefinition`, `peekImplementation`
and **Peek References** (`editor.action.referenceSearch.trigger`, Shift+F12 in the VS Code preset as in VS Code, where Find All References moves
to Shift+Alt+F12; the Visual Studio preset keeps Shift+F12 for Find All References) ask the same providers as Go to Definition / Find All
References (`NavigationFeature.FindDefinitionsAsync`, `FindReferenceLocationsAsync`) and open the peek under the caret's line; no answer says
"No definition found for 'x'" in the status bar.

* **A real zone, not a popup over the code.** The lines below the anchor line move down by the widget's height: a `VisualLineElementGenerator`
  puts a zero-length `InlineObjectElement` at the end of the anchor line whose spacer (1 px wide, line height + widget height tall) has its
  `TextBlock.BaselineOffset` at the line's baseline, so the line's text stays at the top of its (now tall) visual line and the rest hangs below
  it (AvaloniaEdit 12 honours an inline object's baseline offset and allows zero-length elements). The widget lives on the view's
  `WidgetLayer` (a canvas over the editor whose children take the pointer and the keyboard, unlike `Overlay`, which is not hit-testable) and is
  placed in the zone on every scroll and visual-lines change; edits above move it with its line (a `TextAnchor` at the line's end); deleting
  the line closes the peek. With word wrap the spacer sits on the line's last row, so the widget's top is the visual line's bottom less the
  zone (not one row under the line's top). Height: 40 % of the view, between 8 and 18 lines, plus the title bar; the peek is scrolled into
  sight when it opens (the line's rows and the zone; the line's top wins when both do not fit).
* **The embedded editor** is a full `CodeEditorControl`. When the target's document is open in the shell (`EditorViewFactory.TryGetOpenDocument`,
  or the view's own document) it is a second view of THAT document: edits go straight into it, with its undo and its language features. Otherwise
  it is read-only over the file's text (read through `IFileSystem`, decoded like a file, coloured as its language): a snapshot document that is
  never registered with the document manager (no language server sees it), released when the peek closes or shows another file. The target
  range is highlighted (`peekViewEditor.matchHighlightBackground`), the caret goes to its start and the view scrolls so it is the fourth line.
  Colours: VS Code's `peekView.border`, `peekViewTitle.background`, `peekViewTitleLabel.foreground`, `peekViewTitleDescription.foreground`,
  `peekViewResult.background`, `peekViewEditor.background` (the missing defaults were added to `DefaultColors`).
* **Several locations** (references, a symbol defined twice): a list on the right (file:line and the line's text) picks which one the embedded
  editor shows; the title says "2 of 5 references · directory".
* **Keys**: Esc closes it from the view (a tunnelling handler registered after the completion list's, parameter hints', snippets' and
  multi-cursor's, so they take Esc first when they have something to close), from the list and from the embedded editor (whose own popups and
  carets keep their Esc and Enter first); with Vim on, Esc in Normal mode calls `EditorModalSurface.ClosePeek`, which closes a peek of the
  view or of the peek the view is embedded in, while the Esc that leaves Insert, Visual or an operator only changes the mode (VS Code's Vim),
  so `i`, typing, Esc and more edits work in the embedded editor; `CloseInfoPopups` (every Vim Esc) closes hover and parameter hints only. Enter in the list or in a read-only embedded editor, a double-click in either, or a click on the
  title opens the location as Go to Definition does (this view when it is the same file, else a tab through the shell) and closes the peek. The
  ✕ button closes it. While a peek is open the context key `referenceSearchVisible` (VS Code's) is true.
* **Lifetime**: closing the peek disposes the embedded editor (it leaves the factory's views) and releases its snapshot document; disposing the
  view closes its peek. Closing the last tab of the peeked document destroys every view over it, the embedded one too (the factory's
  `ViewClosing`): the peek lets go of it and shows the same location again, read-only over the file on disk. Opening and closing a peek
  leaves nothing reachable (`PeekViewTests`, weak references over four cycles).

Tests: `PeekViewTests` (the zone and the widget's place, the read-only and the in-place editable embedded editor with its undo, the list and
Enter, the same file, Esc from the view / the embedded editor / a modal layer (`ClosePeek` closes, `CloseInfoPopups` does not), the widget
under a word-wrapped anchor line and its reveal, the peeked document's tab closed, no definition, edits above and deletion of the anchor line,
the menu item, lifetime, a screenshot `.scratch/screenshots/editor-peek-definition.png` checked for the border colour), `RealApp/LanguageNavigationTests`
(Alt+F12 and Vim's Esc in the real app; in the embedded editor `ix<Esc>` and `v<Esc>` keep the peek and Normal mode's Esc closes it), `RealAppContextMenuTests` (the item and its key).

### 7d. Linked editing

`Features/LinkedEditingFeature.cs` (one per view, not in large-file mode): VS Code's linked editing. With `editor.linkedEditing` on (off by
default, as in VS Code; per language), once the caret has rested 100 ms the first `ILinkedEditingRangeProvider` (`textDocument/linkedEditingRange`)
is asked for the ranges at the caret (off the UI thread, 2 s deadline; an answer for older text or a caret that moved is dropped; fewer than two,
overlapping, or ranges that do not contain the caret are ignored). **Start Linked Editing** (`editor.action.linkedEditing`, Ctrl+Shift+F2 /
Cmd+Shift+F2 in both presets) asks at once, with the setting on or off, and ends the session when pressed during one ("No linked ranges at the
caret" when there is nothing to link).

* **Mirroring.** The ranges are document offsets that follow every change of the buffer (an insertion at a range's edge grows it, VS Code's
  `AlwaysGrowsWhenTypingAtEdges`). An edit inside one range is mirrored into the others; only the part that differs is replaced (a common prefix
  and suffix stay). The mirror is applied right AFTER the edit's change event (posted at normal priority, so before the next key), never inside
  it: every listener — the language client's document sync above all — sees the edit and then the mirror as two ordered changes. It is applied
  in `EditorDocument.BeginUndoGroup(continueLast: true)`, so it joins the edit's undo step, and `TypingBehaviors.FollowEdit` lets a typed run
  continue across it: a word typed into a tag name is ONE undo step for both tags (a mirror before the caret moves the caret with it).
* **Ending.** An edit outside the ranges or across a boundary ends the session; so does an edited range that no longer matches the word
  pattern as a whole (the server's `wordPattern`, a JavaScript regex compiled with a 50 ms timeout, else the language configuration's): a space
  typed into a tag name is not mirrored and ends it, as in VS Code. The caret leaving the ranges ends it and asks again where it rests (with the
  setting on). Turning the setting off ends a session it started.
* **Several carets** (multi-cursor): carets editing every range the same way change each once and keep the session; edits at several places that
  leave the ranges different are led by the range with the primary caret if it was edited, else end the session.
* **Vim**: Vim's edits are ordinary edits. An Insert session's typing is mirrored, and since the Vim layer holds its undo group open for the
  session, the mirrors are part of that one Vim change (`u` undoes both tags); a Normal-mode change (`x`, `r`) gets its mirror joined to its step.
* Drawn with `editor.linkedEditingBackground` (VS Code's default, translucent red) on the selection layer while a session is on. Costs nothing
  while off: the change and caret handlers return at once.

Tests: `LinkedEditingViewTests` (over a provider that answers like vscode-html-language-server: typing in either tag, one undo step for a typed
word, delete-and-retype, the word pattern (the server's and the language's), edits outside or across a boundary, the setting off and Start Linked
Editing, the caret leaving, a stale answer, the order listeners see the edit and the mirror in, several carets, a modal layer's undo group, the
setting's registration), `RealApp/LanguageNavigationTests` (Vim's `a`…`Esc` mirrored, one `u`), `KeybindingServiceTests` (the keys of both presets).

### 7e. Code lens and semantic highlighting

**Code lens** (`CodeLensFeature`, `CodeLensCommands`, `LineZones`). Every `ICodeLensProvider` of the document (the LSP client's, the Test
Explorer's) is asked 500 ms after the text stops changing, at once when a provider comes or goes (the LSP client re-registers its provider on
`workspace/codeLens/refresh`) or fires `ICodeLensProvider.CodeLensesChanged` (additive Core member; the Test Explorer fires it when discovery
changes which tests a file has or their lines, never for results: a refresh asks every provider, the server's too, and re-resolves its lenses), off the UI thread with a 5 s deadline; an answer for an older version of the text is dropped (the next request is scheduled).
At most 5000 lenses are kept. Each lens is anchored to its range start, so it moves with the text, and the lenses on screen stay until the next
answer replaces them: typing never removes and re-adds zones, so the caret line does not jump. When the set does change, the first visible line
keeps its place on screen (VS Code's `StableEditorScrollState`).

Drawing: the lenses of a line are one line of text in `editorCodeLens.foreground`, at the line's indentation, titles joined by " | ", in a
zone above the line (`LineZones`, §5) as tall as the editor's line height (a set `editor.codeLensFontSize` gives `size × max(1.3, line height /
font size)`, VS Code's rule); the font is 90 % of the editor's and its family unless the settings say otherwise. VS Code's `$(icon)` syntax is
dropped from titles (no Codicons are embedded). A lens without a command holds its zone (so its resolve does not move the text) and is resolved
(`codeLens/resolve`) only when it is within 20 lines of the viewport, once; until then a lens that replaced one at the same place on its line shows
that one's title. A title with a command is a link: under the pointer it takes `editorLink.activeForeground`, underlined, with the hand cursor,
and a click runs it. A click anywhere in a zone never moves the caret. Folded lines show no zone (the fold's first line keeps its own).

What a click runs (`CodeLensCommands`): the reference commands servers borrow from VS Code (`editor.action.showReferences`,
`editor.action.peekLocations`, `editor.action.goToLocations`, `editor.action.findReferences`, `rust-analyzer.showReferences`,
`roslyn.client.peekReferences`) open the references UI with the locations in their arguments (none: a status-bar message; one: opened; several:
`cscode.references.show`, the shell's Find Symbol Results, else a quick pick; a command without locations asks the reference providers at its
position). Everything else goes through `ICommandService` with the lens's arguments: a server's own commands are registered there by the LSP
client (`workspace/executeCommand`), CsCode's own (the Test Explorer's `testing.codeLens.run` / `debug`) by their modules; an unknown command
says so in the status bar. rust-analyzer's `rust-analyzer.runSingle` / `debugSingle` lenses (client-side commands of its VS Code extension) are
hidden: the Test Explorer's "Run Test | Debug Test" lenses (docs/TESTING.md) replace them.

**Semantic highlighting** (`SemanticTokensFeature`, `SemanticTokenStore`, `SemanticTokenStyleResolver`). On per
`editor.semanticHighlighting.enabled`: `true`, `false`, or `configuredByTheme` (the default) = the theme's own `"semanticHighlighting": true`
(read from theme JSON through `include` chains: Dark+, Light+, Monokai and most of VS Code's own themes; set on the Visual Studio 2022 themes, which
colour classified names). The first `ISemanticTokensProvider` is asked (the LSP client does full/delta itself and hands back whole data), 300 ms
after the text stops changing, when a provider comes or goes (`workspace/semanticTokens/refresh` re-registers it) and after a theme or setting
change; a document of more than 5000 lines with an `ISemanticTokensRangeProvider` asks for the visible lines ± 100 instead, again 150 ms after
scrolling, and keeps tokens only within 400 lines of the asked range. An answer for older text is dropped; a failed or timed-out one keeps what is
shown. Between answers the tokens follow every edit (`EditorDocument.BeforeChange`): tokens after an edit on its line shift, lines below move, an
edit inside a token grows or shrinks it (typing an identifier character at its end grows it), and only tokens an edit cuts are dropped until the
next answer: no flicker back to the grammar's colours while typing. At most 200,000 tokens are kept (4 ints each).

Colours, VS Code's rules: a selector `type.modifier…:language` (`*` for any type) matches a token whose type is the selector's or a sub-type of
it (`class`, `struct`, `interface`, `enum`, `typeParameter` → `type`; rust-analyzer's `builtinType`, `typeAlias`, `union` → `type`,
`selfKeyword`/`selfTypeKeyword` → `keyword`), that has all its modifiers, in its language; score 100 − super-type distance, + 100 per modifier,
+ 10 for a language. Two rule sets compete property by property (foreground, bold, italic, underline, strikethrough), the higher score winning,
the theme winning ties: VS Code's defaults, which map each standard type (and `variable.readonly`, `*.defaultLibrary` …) to TextMate scopes looked
up in the theme's `tokenColors` (the first scope a rule matches; no match sets nothing), then the theme's `semanticTokenColors`. A token nothing
styles keeps the grammar's colour (TextMate or tree-sitter). The colorizer runs right after the grammar's (which is now always the first line
transformer, also after a language change), so bracket pair colours still win, as in VS Code; styles are cached per (type, modifiers) of the
legend, so colouring a line allocates nothing beyond AvaloniaEdit's own element splits.

Typing cost: `HostileProviderTests` types with semantic tokens on every line of a 3000-line file and a slowly resolving lens every 25 lines on top
of the hostile providers and the diagnostics flood: keystroke p99 4.5 ms (budget 5), editor UI job max 0.9 ms (budget 16), dispatcher latency p99
5.4 ms (budget 16) on this Mac; `KeystrokeAllocationTests` is unchanged (≈ 90 KB/keystroke of our own, independent of the document's size).

Tests: `CodeLensViewTests` (the zone geometry and the caret keys, the zone hiding selection / current line / caret in pixels, a click running the
command with its arguments and never moving the caret, the reference command mapping, resolve near the viewport once, old lenses kept while typing,
the old title until the new resolve, the first visible line kept on screen, the settings and both refresh paths, folding and edits, the breakpoint
glyph on the text), `SemanticTokenTests` (style resolution with VS Code's scoring and real Dark+, the store following single-line and multi-line
edits and CRLF, range mode memory and the token cap, colours in a view with the setting and the theme flag, no flicker, stale answers, refresh,
range requests for the viewport of a big file), `ThemeTests` (the theme flag), `PerfMemoryTests` (a view with lenses and tokens is collectable),
`ScreenshotTests.Code_lenses_and_semantic_tokens` (Dark+, Light+, Visual Studio Dark), `TestingHostTests` / `TestExplorerCompositionTests` (the
Test Explorer's lenses, and that results during a run do not refresh them). All of these pass on macOS (arm64) and on Linux (Ubuntu arm64,
headless), zone geometry, pixel counts and screenshots included.

### 7f. Overview ruler and minimap

`View/Decorations` (one `ScrollDecorations` per view, also in large-file mode and in split views) adds VS Code's two scroll aids. Both controls
sit in the view's grid over the editor's right edge, below the popup overlay, and their width is reserved with the text area's margin, so no
text (and no sticky-scroll row: its panel keeps the right inset while it lines the rows up with the text) runs under them: text | minimap |
ruler (the ruler over the vertical scrollbar), or minimap | gutter | text | ruler with `editor.minimap.side: left`.

**Overview ruler** (the scrollbar's lanes; `OverviewRuler`, `OverviewRulerModel`):

| Lane | Marks (theme key) |
|---|---|
| left | source-control changes from the gutter's quick diff (`editorOverviewRuler.addedForeground`, `modifiedForeground`, `deletedForeground`: the gutter colours at 60 %), breakpoints with `debug.showBreakpointsInOverviewRuler` (off by default, as in VS Code; `debugIcon.breakpointForeground`) |
| centre | find matches while the find panel is open (`editorOverviewRuler.findMatchForeground`), occurrence highlights (`wordHighlightForeground`, writes `wordHighlightStrongForeground`) |
| right | errors, warnings, information (`editorOverviewRuler.errorForeground`, `warningForeground`, `infoForeground`; hints are not marked, as in VS Code) |
| all | the caret lines, every caret of multi-cursor (`editorCursor.foreground`; `editor.hideCursorInOverviewRuler` hides them) |

A mark is placed where its lines are in the scrollable height over the scrollbar's track (between its arrow buttons), from AvaloniaEdit's height
tree, so folded lines take no room, wrapped lines their rows, and marks line up with the thumb. Laying out does one height-tree lookup per
pixel row (the line at the top of each row) and places each mark by binary search in that table; marks are bucketed per pixel row and lane,
the more severe kind winning, and drawn as one rectangle per run. 19,999 find matches on a 1M-line file are collected and laid out in about
2 ms on the UI thread (it was 18 ms with a lookup per mark). The marks are recollected per source only when that source changed (each feature
calls `CodeEditorControl.NotifyMarkersChanged` with its own source, so new occurrence highlights do not re-read the find matches; the find
panel's searches, opening and closing; the carets), and after an edit once typing has paused for 100 ms, so a keystroke costs a flag and a
timer start; scrolling lays nothing out. A left press on the scrollbar's track (not the
thumb, not the arrows) scrolls that position to the middle of the view, VS Code's default instead of paging. The ruler takes no pointer
input itself, so the scrollbar keeps its hover, thumb and arrows. `editor.overviewRulerLanes: 0` hides it, `editor.overviewRulerBorder` draws
its left border (`editorOverviewRuler.border`), `editorOverviewRuler.background` fills it when a theme sets one.

**Minimap** (`Minimap`, `MinimapRowMap`, `MinimapGeometry`, `MinimapRasterizer`):

* One row per document line, 2 DIPs high and one DIP per character at `editor.minimap.scale` 1 (2 and 3 multiply both). Lines hidden by a
  collapsed fold have no row (a hidden line maps to its header's row); a wrapped line is one row of its first `editor.minimap.maxColumn`
  columns. The width is `maxColumn` columns, at most 30 % of the editor, and the minimap is hidden below 24 DIPs.
* Each non-blank character is a cell in its token's colour (the published token rows of the TextMate or tree-sitter session, resolved against
  the theme with `TokenColorResolver`), blended over `minimap.background`; tabs go to the next tab stop. `editor.minimap.renderCharacters`
  (default true, as in VS Code) shapes the cells by character class (capitals, digits and brackets full height, lower case shorter, punctuation a low, light
  mark), so words read as words; false draws runs of non-blank text as solid blocks. Large-file mode has no tokens: plain foreground.
* The slider covers the rows on screen (`minimapSlider.background`, `hoverBackground`, `activeBackground` while dragging), shown while the
  pointer is over the minimap with `editor.minimap.showSlider: mouseover` (the default) or always with `always`. A press on the slider drags it
  (the editor follows; the slider stays under the pointer), a press elsewhere centres the slider there and goes on dragging, the wheel scrolls
  the editor.
* A file longer than the minimap scrolls it in proportion (VS Code's proportional minimap): the window's first row is the same fraction of
  the rows it cannot show as the editor's top row is of the rows it can scroll, so the slider reaches the bottom at the end of the file. Only
  the window's rows are ever drawn.
* Over the rows: find matches (`minimap.findMatchHighlight`), errors and warnings (`minimap.errorHighlight`, `minimap.warningHighlight`), the
  caret lines (`minimap.selectionHighlight`), and source-control changes in a 2-DIP gutter (`minimapGutter.addedBackground`, ...), from the
  ruler's marks.
* **Threading and memory.** The UI thread captures the window's rows (each line's offset and length, its token row; consecutive lines by
  `NextLine`) and the document's immutable snapshot, a pool thread draws them into a premultiplied BGRA buffer (allocation-free after the
  first render), and the UI thread copies the buffer into a `WriteableBitmap`. One render runs at a time; requests while it runs coalesce into
  one more, so a keystroke only marks the minimap stale and posts one background job. A scroll within the window only moves the slider; a new
  window, an edit, newly coloured lines in the window, a fold, a theme or a setting draw again. Measured (Debug, loaded shared Mac): a
  350-row window of a 1M-line file costs the UI thread 0.2 ms to capture and 0.02 ms to copy at scale 1; drawing 600 rows of 120 columns at
  a 2x screen takes about 4 ms on the pool thread. The view holds the bitmap and one buffer of the minimap's size in device pixels (120 × 1000
  DIPs at 2x: 2 × 1.9 MB); both are released when the minimap is turned off or the view is disposed. Rendering is on the pool, not the editor
  worker, so it never delays a tokenisation slice.
* Settings apply live (`editor.minimap.enabled`, `.side`, `.renderCharacters`, `.maxColumn`, `.showSlider`, `.scale`); **View: Toggle
  Minimap** (`editor.action.toggleMinimap`) flips `editor.minimap.enabled` in the user settings. The diff editor's panes have no minimap
  (`CodeEditorControl.AllowMinimap = false`, as in VS Code) but keep the ruler. The factory registers the settings so the Settings panel
  lists them.

Tests: `ScrollDecorationsModelTests` (rows under folds, the proportional window and slider and its inverse, bucketing and priorities, the
rasteriser's colours, tab stops, `maxColumn`, character shapes, a full window's cost and zero allocation), `ScrollDecorationsViewTests`
(placement and the reserved margin, every setting live, narrow editors, diff panes, the toggle command, token colours and typing in the
minimap, click / drag / wheel, the slider's visibility, folding, each source of marks on the ruler, word wrap, a press on the scrollbar's track,
theme changes, disposal, sticky scroll's insets, a 1M-line file, typing that renders nothing on the keystroke, and screenshots in the dark and
light themes written to `.scratch/screenshots/minimap-*.png`).

**Large-file mode**: above `cscode.editor.largeFileThresholdBytes` (default 5 MB) or with a line longer than 20,000 characters the document's language is forced to `plaintext`, and its views get no tokenisation, folding, squiggles, bracket matching or any other language feature (just text); files over 200 MB are refused with a clear `FileTooLargeException` message.

## 8. Known gaps (honest list)

Not done, or done only in part — none of this is faked:

* **Multi-cursor** (§7b) is off while Vim mode is on (the default). With several carets, auto-closing brackets and quotes, auto-surround and
  over-typing are not applied (plain text is typed at each caret); snippets, the line commands (move / copy / comment / indent lines through
  the commands), Format and code actions act at the primary only and collapse the others; Up/Down move by document lines when words wrap; the
  carets are not kept in the view state (a recreated view has one); `editor.multiCursorModifier` (Ctrl+Click to add carets) is not read;
  Backspace in leading spaces deletes one space, not one indentation unit. Rectangular selection is AvaloniaEdit's own (Alt+drag; Ctrl+Option+drag
  on macOS) and its rows act as carets for the clipboard (§7a).
* **Code lens** (§7e): no keyboard access to a lens (VS Code's "Show CodeLens Commands For Current Line" is not implemented); a lens on an
  empty line is not shown (§5); split views of one document ask (and resolve) separately; lenses
  and semantic tokens are not `IMemoryTrimmable` (both are bounded and die with the view).
* **Semantic highlighting** (§7e): only the first full provider and the first range provider are used (VS Code also picks one); custom token
  types and modifiers declared by a server's VS Code extension (`semanticTokenTypes` / `semanticTokenScopes` contributions) are not known beyond
  the few super-types listed in §7e; `editor.semanticTokenColorCustomizations` is not read.
* **Peek** (§7c): a file that is not open in the shell is peeked read-only (VS Code opens it editable; opening it here would register it with
  the language servers and leave the peek to decide when to close a document the shell may meanwhile have opened). The list has one row per
  location (VS Code groups references by file), there is no "open to the side", the height is fixed while the peek is open (VS Code's sash
  resizes it), and a view has one peek at a time.
* **Linked editing** (§7d): with several carets a typed run is not continued across the mirror (each character is its own undo step, each
  with its mirror); a server's word pattern that .NET's regex engine rejects (rare JavaScript-only syntax) checks nothing.
* **Minimap and overview ruler** (§7f): a wrapped line is one minimap row (VS Code draws each wrapped row); `editor.minimap.size`
  (`fill` / `fit`), `editor.minimap.autohide`, `editor.minimap.sectionHeader*` and the minimap's own hover / selection rendering of the
  text (it marks whole rows) are not read; the ruler has three fixed lanes (`editor.overviewRulerLanes` 1 and 2 are drawn as 3) and does
  not mark selection highlights, bracket matches or the debugger's stop line; `editor.scrollbar.scrollByPage` is not read (a track press
  always jumps). Sticky scroll is (`editor.stickyScroll.enabled`, off by default; docs/TREE-SITTER.md §5).
* `editor.cursorStyle`, `editor.cursorBlinking` and `editor.smoothScrolling` are not applied (AvaloniaEdit offers no equivalent). Font ligatures are on by default (`editor.fontLigatures`; VS Code defaults to off, but the bundled Fira Code is chosen for them, and they keep one glyph per character): `false` turns them off. AvaloniaEdit 12 has no font-feature setting, so `EditorFontFeatures`, a line transformer, then gives each element's run properties `-liga, -calt` (reaching the private `VisualLineElement.TextRunProperties` setter by reflection; `CodeFontTests` fails if an upgrade removes it). `true` (the default) leaves the font's own ligatures (Fira Code's keep one glyph per character, so carets stay on columns) and a string is VS Code's CSS feature list (`"'ss01', 'zero'"`). The code panels follow the same setting through `ui:CodeFontFeatures`. `editor.wordWrap` supports on/off only. Drag-and-drop of text is AvaloniaEdit's own behaviour; bidirectional text and IME composition were not tested.
* **Typing allocation**: the TextMate colorizer allocates per token on the edited line (§6). A single pre-merged element list would cut it; not done.
* The `previousLineText` guard of `onEnterRules` (which Core's `OnEnterRule` record cannot carry) is handled editor-side through `ExtendedLanguageConfiguration`/`EnterRuleEx`; the Core record is unchanged.
* `UndoStackScrubber` is reflection against AvaloniaEdit 12.0.0's private layout; `PerfMemoryTests` will fail loudly if an upgrade changes it (use `RetentionPath` to find the new culprit).
* Debug margin: the right-click menu is an Avalonia `ContextMenu` (its items are exercised through the same methods the tests call; the popup itself is not clicked in tests). Capability checks (`SupportsConditionalBreakpoints` …) are not consulted before offering the menu items.
* Platform features that Platform may ship later (`IEditorConfigSource`, per-language configuration, `IMemoryMonitor`) are consumed when registered; the fallbacks are what the tests exercise.

## 9. Tests

`dotnet test tests/CsCode.Editor.Tests` (headless Avalonia; real temp files, real `FileSystemWatcher`, real TextMate grammars and the native Oniguruma). ≈ 650 tests, ≈ 1.5 minutes. Highlights:

* `EditorDocumentFuzzTests` (oracle comparison), `EditorDocumentFileTests` (encodings, EOL, atomic save, watcher, conflicts, large files), `LanguageServiceTests`, `ThemeTests` (real Dark+/Monokai/TypeScript/Python/C# JSON), `TextMateTests` (golden scopes), `SnippetEngineTests`, `EditingOperationsTests`, `EditorConfigTests`.
* Language rendering: `CodeLensViewTests`, `SemanticTokenTests` (§7e).
* View tests: `EditorViewTests`, `CompletionViewTests`, `DiagnosticsHoverTests`, `SignatureActionsNavigationTests`, `BracketsHighlightsDebugTests`, `ShouldHaveFeatureTests`, `MultiCursorViewTests` (and the pure `MultiCaretModelTests`), `ScrollDecorationsViewTests` (and the pure `ScrollDecorationsModelTests`).
* Hostile/perf/leak: `HostileProviderTests`, `BlockingDetectorTests`, `KeystrokeAllocationTests`, `PerfMemoryTests` (opens 1/10/50 MB files, tokeniser memory, 500× open/close, 500× recreate, popups open). Leak failures print the chain of strong references (`RetentionPath`).
* Typing cost (perf-typing): `TextAreaWatermarkBindingTests` (bytes per keystroke in 0.5–6 MB documents, the watermark unbound), `FoldRedrawFilterTests` (visual lines built per keystroke inside an expanded fold; folding, a folded region edited while collapsed, a multi-change update), `CachedLineNumberMarginTests` (no number laid out while typing, the stock margin's exact pixels), `TokenRedrawTests` (the line above the caret is not rebuilt; a line built before the next layout is redrawn), `FoldingMarginLeakTests.Typing_keeps_the_markers…`.
* `ScreenshotTests` write PNGs of the dark and light themes (code with squiggles, breakpoints, the execution line, a folded region, bracket/occurrence highlights; the completion, hover and signature popups) to `.scratch/screenshots/` (git-ignored) and assert the content really is on screen. Visual defects found by looking at them and fixed: invisible match highlight on the selected completion row in the light theme (the "focus highlight" colour was missing).

## 10. Third-party content

AvaloniaEdit (MIT), TextMateSharp and TextMateSharp.Grammars (MIT; the bundled grammars and 15 of its 21 themes are Microsoft's VS Code built-ins, MIT; the other six are TextMateSharp's own, see THIRD-PARTY-NOTICES.md), the Oniguruma regex engine through `TextMateSharp.Onigwrap` (BSD), Avalonia (MIT). No icon font, Codicon or other artwork is embedded.
