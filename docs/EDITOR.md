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
`RestoreViewState`, `GetBreadcrumbsAsync`, `IsFocused`; the `EditorViewState` record. **No change was made to CsCode.Core.**

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
  everything is released when the last view goes away.

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
   document highlights (provider, else word occurrences); folding (indentation + language-configuration markers + `IFoldingRangeProvider`, +/- glyphs, collapsed-region hover); bracket matching + setting-gated pair colourisation. Everything is cancellable, debounced, latest-wins, and **never shows a result for older text**.
10. **Debug integration** (all null-safe without a debugger) — breakpoint glyph margin (click toggles; right-click: condition / hit count / logpoint / enable / remove; red filled = verified, hollow grey = disabled or unverified; conditional and logpoint shapes; tooltips with the adapter's message; ghost dot on hover),
    execution arrow + line highlight with auto-reveal, breakpoints that follow edits, hover-evaluate (`EvaluateAsync(expr, frameId, "hover")`).
11. `AddCsCodeEditor`, this document.

SHOULD items done: breadcrumbs (`GetBreadcrumbsAsync`, cached per document version), Ctrl+Click on http(s) URLs (through `IWindowService.OpenExternalAsync`), inlay hints (anchored, capped at 1000, setting-gated), accessibility names on the editor, popups and margin.

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
* `UndoStack`: `IsOriginalFile`/`MarkAsOriginalFile` give dirty tracking; `StartContinuedUndoGroup` lets a typing burst be one step.
* **The undo history pins the text area.** Each edit pushes a `RestoreCaretAndSelectionUndoAction` that keeps its `TextArea` weakly but its `Selection` object strongly, and `Selection` holds the `TextArea`. A document that outlives a view therefore retains the whole destroyed view
  (renderers, visual lines, popups) until the entry scrolls off the undo limit. `UndoStackScrubber` clears those selections for the disposed view's entries (reflection against the 12.0.0 layout; a no-op if the layout differs). Found with `RetentionPath` (below).
* `TextEditor.ScrollToVerticalOffset` does nothing for absolute positions on a fresh view; restoring a scroll offset goes through `((IScrollable)TextArea).Offset`, re-applied on `LayoutUpdated` until the extent exists.
* `AbstractMargin` margins are placed through `TextArea.LeftMargins`; toggling line numbers re-inserts the line-number margin at index 0, so the breakpoint margin re-pins itself leftmost. `TextArea.TextPasted` fires after a paste (the pasted range is taken from the last document change).
* `EnableHyperlinks` makes AvaloniaEdit start the browser itself; the editor turns it off and opens URLs through `IWindowService`.
* **AvaloniaEdit allocates in proportion to the document's line count on every edit** (≈ 90 B per line per keystroke: 19 KB for a 50-line file, 538 KB for 6000 lines, measured on a bare themed `TextEditor`), and keeps ≈ 130 B per line per view for its height tree. Nothing in this module can change that.

TextMateSharp 2.0.3:

* The tokenizer (`Registry`, grammars) is **not thread-safe**; it is confined to the editor worker. The native Oniguruma library loads fine on this machine; the first lines of a grammar pay for lazy regex compilation, so a stopped-early line (`StoppedEarly`) is retried with 10/60/300 ms limits before a truncated result is accepted.
* `Theme.Match` ignores parent scopes, so the module implements vscode-textmate's matching itself (`TokenColorResolver`: specificity, inheritance, parent scopes, comment/string/regex kinds). The built-in Monokai has no `name` and `light_vs` has no `type`: kind is inferred from the background's luminance.
* `IStateStack.Equals` is **not** structural across independent runs, and the concrete `StateStack` is internal — so "stop re-tokenising once the state converges" (what VS Code does) is not available. The module instead tokenises only the viewport after each edit and defers the read-ahead until typing pauses (250 ms).
* `TextMateSharp.Grammars` embeds VS Code's built-in grammars and themes (this is where Dark+/Light+ and the ~40 languages come from).

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

**Settings read live** (`IConfigurationService.Changed`; per-language `"[lang]": {…}` overrides through `GetRaw`; `.editorconfig` indent style/size win over settings as in VS Code):
`editor.fontFamily`, `editor.fontSize`, `editor.lineHeightFactor`, `editor.tabSize`, `editor.insertSpaces`, `editor.wordWrap` (on/off), `editor.renderWhitespace` (`boundary` is treated as `all`), `editor.lineNumbers` (on/off), `editor.rulers`, `editor.mouseWheelZoom`, `editor.guides.indentation`,
`editor.renderLineHighlight`, `editor.bracketPairColorization.enabled`, `editor.matchBrackets`, `editor.occurrencesHighlight`, `editor.folding`, `editor.quickSuggestions`, `editor.quickSuggestionsDelay`, `editor.suggestOnTriggerCharacters`, `editor.autoClosingBrackets/Quotes`, `editor.autoSurround`,
`editor.formatOnSave/OnType/OnPaste`, `editor.hover.delay`, `editor.inlayHints.enabled`, `workbench.colorTheme`, `files.encoding`, `files.eol`, `files.insertFinalNewline`, `files.trimFinalNewlines`, `files.trimTrailingWhitespace`, `files.associations`, and the module's own `cscode.editor.undoLimit` (default 1000 groups),
`cscode.editor.maxFileSizeBytes` (200 MB), `cscode.editor.largeFileLongLineChars` (20,000), `cscode.editor.largeFileThresholdBytes` (5 MB).

**Commands** (VS Code ids): `editor.action.commentLine`, `addCommentLine`, `removeCommentLine`, `blockComment`, `moveLinesUpAction`, `moveLinesDownAction`, `copyLinesUpAction`, `copyLinesDownAction`, `deleteLines`, `indentLines`, `outdentLines`, `joinLines`, `transposeLetters`, `trimTrailingWhitespace`,
`sortLinesAscending/Descending`, `smartSelect.expand/shrink`, `selectAll`, `undo`, `redo`, `actions.find`, `editor.action.startFindReplace`, `workbench.action.gotoLine`, `editor.action.triggerSuggest`, `triggerParameterHints`, `quickFix`, `organizeImports`, `revealDefinition`, `goToDeclaration`, `goToTypeDefinition`,
`goToImplementation`, `goToReferences`, `rename`, `formatDocument`, `formatSelection`, `editor.fold`, `foldRecursively`, `unfold`, `unfoldRecursively`, `toggleFold`, `foldAll`, `unfoldAll`, `editor.action.insertSnippet`, `editor.debug.action.toggleBreakpoint`.

**Large-file mode**: above `cscode.editor.largeFileThresholdBytes` (default 5 MB) or with a line longer than 20,000 characters the document's language is forced to `plaintext`, and its views get no tokenisation, folding, squiggles, bracket matching or any other language feature (just text); files over 200 MB are refused with a clear `FileTooLargeException` message.

## 8. Known gaps (honest list)

Not done, or done only in part — none of this is faked:

* **Multi-cursor** (Alt+Click, Ctrl+Alt+Up/Down, Ctrl+D) is not implemented: AvaloniaEdit has no multi-caret model and the stretch goal was not attempted. Rectangular selection (AvaloniaEdit's own) works.
* **Minimap, sticky scroll, code lens, semantic-token overlay, Alt+F12 peek, overview-ruler markers on the scrollbar** are not implemented (settings for them are read but have no effect).
* `editor.cursorStyle`, `editor.cursorBlinking`, `editor.smoothScrolling` and font ligatures are not applied (AvaloniaEdit offers no equivalent; ligatures stay off). `editor.wordWrap` supports on/off only. Drag-and-drop of text is AvaloniaEdit's own behaviour; bidirectional text and IME composition were not tested.
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
