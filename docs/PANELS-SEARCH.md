# Search and Settings panels (`CsCode.Panels.Search`)

Find in Files (with replace), the Settings editor and the Keyboard Shortcuts view. Everything is built against Core interfaces plus the
`CsCode.Ui` design system; the project references `CsCode.Core` and `CsCode.Ui` only.

| View | Id | `IToolWindow.DefaultLocation` | Opened by |
|---|---|---|---|
| Find in Files | `search` | `Left` (lays out fine in a bottom well too) | `workbench.action.findInFiles` (Ctrl+Shift+F), `workbench.action.replaceInFiles` (Ctrl+Shift+H) |
| Settings | `settings` | `Document` | `workbench.action.openSettings` |
| Keyboard Shortcuts | `keybindings` | `Document` | `workbench.action.openGlobalKeybindings` |

## Integration notes (for the shell / integrator)

* **DI:** `services.AddCsCodeSearchPanels()`. Registers three `IToolWindow`s and `SearchPanelsHost`. **The host registers the commands and the `search.*` settings
  in its constructor, and the tool windows depend on it**, so the shell enumerating `IEnumerable<IToolWindow>` at startup is what registers them.
  Required service: `IWorkspaceService`. Optional (the panel degrades without them): `IDocumentManager`, `INavigationService`, `IWindowService`,
  `IConfigurationService`, `IStateStorage`, `ICommandService`, `IKeybindingService`, `IContextKeyService`, `IActiveSelectionProvider`,
  `CsCode.Ui.Threading.IUiDispatcher` (falls back to `AvaloniaUiDispatcher.Instance`).
* **Additive Core change:** `src/CsCode.Core/Platform/ViewActivation.cs` adds `IViewActivator.ShowViewAsync(string viewId)`. The commands call it to
  bring a view to the front ("search", "settings", "keybindings"). **The shell must implement it** (map to `ToolWindowManager.Show` /
  `DocumentWell.OpenView`). Without it the commands still work, but nothing is revealed. It is the only Core change.
* **Document-location tool windows.** `Settings` and `Keyboard Shortcuts` use `ToolWindowLocation.Document`. `ToolWindowManager.DockFor` in this base maps
  every location other than Left/Bottom to the right tool well, so the shell has to route `Document` to `DocumentWell.OpenView(id, title, factory)`.
* **Views are disposable.** `FindInFilesView`, `SettingsView`, `KeyboardShortcutsView` implement `IDisposable`; the shell's `ToolTab.DisposeView`
  already disposes views and their DataContext. Disposing cancels a running search, flushes pending settings edits and drops every subscription.
* **Resource keys.** XAML must write `{DynamicResource ResourceKey='vsc:editor.background'}` (quoted, named argument). The shorthand
  `{DynamicResource vsc:editor.background}` compiles but the `vsc:` is read as an XML prefix and the lookup silently fails (found with a headless screenshot).
* Commands registered (only if not already registered by someone else): `workbench.action.findInFiles`, `workbench.action.replaceInFiles`,
  `search.action.focusNextSearchResult` (F4), `search.action.focusPreviousSearchResult` (Shift+F4), `search.action.clearSearchResults`,
  `search.action.collapseSearchResults`, `search.action.findInFolder` (argument: folder URI), `workbench.action.openSettings` (optional query argument),
  `workbench.action.openSettingsJson`, `workbench.action.openGlobalKeybindings`, `workbench.action.openGlobalKeybindingsFile`.
  `findInFiles`/`replaceInFiles` take VS Code's argument object (`query`, `replace`, `filesToInclude`, `filesToExclude`, `isRegex`, `isCaseSensitive`,
  `matchWholeWord`, `preserveCase`, `useExcludeSettingsAndIgnoreFiles`, `triggerSearch`) or a plain string, and seed the query from
  `IActiveSelectionProvider.SelectedText` (single line) or the word at the caret.
* Context key set: `searchViewletVisible`. Settings registered: see below. Default key bindings are the Platform keymap's business.

## Search engine (`Engine/`, UI-free)

```
Start(query) --enqueue--> coordinator thread ("cscode-search-coordinator", single-thread TaskScheduler)
                           compile pattern, read settings, enumerate files (async), push into a bounded queue (512)
                                   |
        N dedicated worker threads ("cscode-search-<session>-<n>", BelowNormal) <-- N = min(processors/2, 8)
        decode 64 KB chunks, split lines, match, build previews
                                   |  FileMatches
        flusher (coordinator thread) every 16 ms --> SearchBatch / SearchProgress events --> subscriber marshals to its own thread
```

* **Threading.** `SearchEngine.Start`/`Cancel` cost the caller one enqueue / one token flip (asserted < 5 ms worst case, typically microseconds).
  Nothing runs on the UI thread or the shared pool; a test counts thread-pool work items during a 200k-file search (471 in this run, all the test's own awaits).
  Events fire on engine threads; the view-model enqueues and applies them on the UI thread in slices of at most 4 ms
  (`IUiDispatcher.Post` at background priority, one ordered queue), then sorts the files by path on a worker and swaps the order in once.
* **Files.** `ISearchFileSource`: when "use exclude settings" and "use ignore files" are both on, `IWorkspaceService.FindFilesAsync` (the platform's
  `files.exclude` + `.gitignore` semantics). Otherwise the built-in `WalkingFileSource` (depth-first in name order, nested `.gitignore` with negation/anchoring/`**`,
  `files.exclude` + `search.exclude`, never follows directory symlinks, always skips `.git`/`.svn`/`.hg`). On top of either: per-query include/exclude globs
  (`GlobFilter` over Core `Glob`; bare names mean "that file or folder at any depth", `./x` anchors at the workspace folder), `search.exclude`, roots.
* **Matching.** Literal (vectorised ordinal / ordinal-ignore-case, VS Code whole-word rule: a boundary is only required on an edge that is a word character),
  regex (.NET `NonBacktracking` whenever the pattern allows it, so `(a+)+$` is linear; otherwise the backtracking engine with a per-match timeout
  `search.regexTimeoutMs`, a file whose match times out is abandoned and after 8 such files the search stops with an explanation), multi-line regex or a literal containing a
  line break (whole file, line breaks normalised to `\n`, bounded by the max file size). Empty matches are never results. Columns are UTF-16 code units.
* **Content.** BOM decides UTF-8 / UTF-16 LE / BE; a NUL in the first 8 KB means binary (skipped); no BOM means strict UTF-8 and, if the bytes are not valid, the
  whole file is rescanned as Latin-1; lines end at `\n`, `\r\n` or `\r` (a `\r\n` split across a read boundary counts once). Files above `search.maxFileSizeBytes` are skipped
  by length without being read. A line longer than 2,000,000 chars is processed in pieces (a match straddling a split is missed; columns stay absolute).
  Previews: leading whitespace trimmed, control characters shown as spaces, clipped to 300 chars with `…` and at most 25 chars of lead-in, so the match is visible in a narrow pane.
* **Open documents.** Dirty documents (and untitled buffers) are searched from memory (`IDocumentManager`), never from the stale disk copy; the searched version is
  recorded. "Open editors only" searches just the open documents. Selection scope (`SelectionScope`) searches one range of one document.
* **Limits.** `search.maxResults` (default 20,000: the search stops, the summary says "truncated"; the cap is exact across workers), `search.maxFileSizeBytes` (5 MiB),
  `search.regexTimeoutMs` (1000). Memory is O(results) (each match: a range and two short strings); the queue is bounded; file contents are never held except for multi-line mode.
* **Cancellation.** A token checked per line and per chunk; `Result` completes at once (about 0.1 ms measured), `WorkersStopped` when the last worker has exited.
  A backtracking regex cannot be interrupted mid-match, so its worker stops within the regex timeout; `Result` still completes immediately.
* **Replace (`ReplaceService`).** Results only remember positions, so replace **re-searches each file right before editing** (open document from memory, closed file from
  disk) and replaces exactly the matches that are still there; a requested match that is gone is reported as skipped. Regex replacement expands `$1`, `${name}`, `$&`
  and the escapes `\n`, `\t`, `\\`; "preserve case" follows the matched text; newlines follow the file's line ending; CRLF and the BOM survive. Replace is supported for
  UTF-8 and UTF-16 files only (the shell's closed-file editor refuses anything else, so the panel skips such files with a reason). Files are applied **one at a time** through
  `INavigationService.ApplyWorkspaceEditAsync` (an open document is therefore one undo step, and the shell never shows its own multi-file prompt on top of the panel's single
  confirmation). A document that changed after planning is skipped.

## Find in Files (view-model `FindInFilesViewModel`)

Query box with Match Case / Whole Word / Regex toggles (Alt+C / Alt+W / Alt+R), replace row (Ctrl+Shift+H, preserve case Alt+P, Replace All Ctrl+Alt+Enter), files to include /
exclude (collapsed behind `…` by default, like VS Code), "use exclude settings", "use ignore files", "search only in open editors", history (Up/Down in the query box,
50 entries, persisted through `IStateStorage` key `search.history`). Results are a flat, virtualised list (file rows and match rows): file icon, name, dimmed folder, count
badge; match rows show the 1-based line number and the preview with the match highlighted (`MatchPreviewText`, find-match colour); with a replacement typed the match is struck
through and the new text follows in green. Keys in the list: Up/Down, Left/Right (collapse/expand), Enter (open at the match), Del (dismiss), F4 / Shift+F4 (next/previous, wraps,
expands a collapsed file), Ctrl+C (copy path). Click opens a preview tab, double-click a permanent one. Context menu: Open, Copy Path, Reveal in Explorer (only when the
command `revealInExplorer` exists), Expand/Collapse, Exclude This Folder, Replace, Dismiss. Summary line: "12 results in 4 files — 38 ms", "No results found for 'x' …",
"truncated at 20,000, refine the search", "Search cancelled", or the regex error. After replacing, the replaced matches leave the tree; files that still have matches
(positions moved) are re-searched.

## Settings editor (`SettingsViewModel`)

Mapping from `ConfigurationProperty` (default + JSON-schema fragment) to controls:

| Schema / default | Control | Notes |
|---|---|---|
| `boolean` | checkbox | writes at once |
| `integer` / `number` | text box | validated against `minimum`/`maximum` (and "whole number"); an invalid value shows an inline error and is **not written**; valid edits are debounced (400 ms) |
| `string` | text box | `maxLength` / `pattern` validated; debounced |
| `enum` (+ `enumDescriptions`) | combo box with the description of the selected choice | writes at once |
| `array`, `object`, unknown | read-only summary ("3 entries: …") + **Edit in settings.json** | the link writes the key with its current value if absent, then opens the file with the key selected |
| (no schema) | decided by the default's JSON kind | |

* **Categories** come from key prefixes: `editor.fontSize` is Editor, `editor.minimap.enabled` is Editor › Minimap, a leading `cscode.` namespace is ignored
  (`cscode.memory.budgetMb` is Memory). Titles are humanised ("Editor › Minimap: Enabled").
* **Search** is a fuzzy filter (`FuzzyMatcher`): each word must be a substring of title/key/description or a fuzzy match of title+key; searching ignores the selected category.
  `@modified` (alone or with words) shows only changed settings.
* **Scope tabs** User / Workspace (`ConfigurationTarget`); the Workspace tab is disabled without an open folder. Values shown for a scope come from that scope's file
  (`settings.json` flattened, nested and dotted keys equivalent, `.cscode/settings.json` for the workspace), falling back to the effective value. *Modified* = set in
  this scope (or, if no file mentions the key, different from the default); "(also modified in Workspace)" is shown for the other scope.
* **Writes** always go through `IConfigurationService.UpdateAsync(key, value, target)` (the platform's comment-preserving JSONC writer; verified against the real
  `ConfigurationService` in `RealPlatformTests`). **Reset Setting** = `UpdateAsync(key, null)` (removes the key). A refresh never overwrites a box with an edit waiting;
  closing the tab flushes waiting edits. `IConfigurationService.Changed` (raised on IO threads) is marshalled and coalesced.
* **Keyboard Shortcuts**: table of `IKeybindingService.All` (command title or id, key label such as `Ctrl+K, Ctrl+C`, `when`, source User/Default, removals shown as `-command`), filter over all
  columns, "keybindings.json" button (`workbench.action.openGlobalKeybindingsFile`; creates the file if missing). Rebinding in the UI is a STRETCH and not implemented.

Settings registered by this module: `search.maxResults`, `search.maxFileSizeBytes`, `search.regexTimeoutMs`, `search.exclude`, `search.useIgnoreFiles`, `search.collapseResults`
(`auto` | `alwaysCollapse` | `alwaysExpand`).

## Tests (`tests/CsCode.Panels.Search.Tests`, xUnit v3 + Avalonia.Headless)

`dotnet test tests/CsCode.Panels.Search.Tests` (on Nix-provided dotnet run it after `source tools/nix-dev-env.sh`, the headless tests need Skia's libraries). 335 tests:
engine (matching, files/encodings, filters and `.gitignore`, open documents, limits/lifecycle, replace), view-models (find, replace flows, history, settings, host/commands,
shortcuts), headless UI + screenshots, real-platform integration, hostile/responsiveness and leak tests.
* Screenshots are written to `.scratch/screenshots/` (git-ignored): `search-*.png`, `settings-*.png`, `keybindings.png`.
* The hostile suite builds a 200,000-file tree (about 800 MB of small files in the temp dir, removed afterwards) plus a 10 MB line, a million-match file, a 150 MB text file and a
  400 MB sparse binary, and asserts, with a heartbeat probe on a simulated UI thread (whose own self-test proves it sees a 60 ms stall): dispatcher latency p99 < 16 ms and max < 50 ms
  during the search and while results flood in, cancel < 50 ms, the result cap exact, heap growth bounded (14 MB over 200k files, 2 MB over the 150 MB file), no worker or thread left
  after 100 start/cancel cycles. Measured on a 22-core Linux box (tmpfs): 200k files in about 0.8 s, heartbeat p99 0.03 ms.
* Leak tests (`LeakAssert`): 100 searches/cancels then dispose, finished and cancelled sessions, settings and shortcuts view-models, the host, and 100 open/close cycles of the real
  Avalonia views.

## Opening the panels in a real window (`tests/CsCode.Panels.Search.Tests/Preview`)

The real `cscode` app in this base is still a scaffold window (the shell has not composed any panels yet), so the panels cannot be seen there. `Preview/` is a small, opt-in
Avalonia app (its own csproj, not in `CsCode.slnx`, excluded from the test assembly) that hosts the three views in tabs, implements `IViewActivator` over the tabs, and is wired to
the real `LocalFileSystem`, `ConfigurationService`, `CommandService`, `KeybindingService` (with the Visual Studio keymap) and the real engine:

```
dotnet run --project tests/CsCode.Panels.Search.Tests/Preview                                  # throw-away sample workspace
dotnet run --project tests/CsCode.Panels.Search.Tests/Preview -- --folder <dir>                # your folder; Replace is refused, nothing is written
dotnet run --project tests/CsCode.Panels.Search.Tests/Preview -- --query UserService --replace AccountService   # drive findInFiles/replaceInFiles through the command bus
dotnet run --project tests/CsCode.Panels.Search.Tests/Preview -- --tab settings|keys
```

It was launched on the X display and captured with ImageMagick while writing this: Find in Files with a replace preview (7 results in 5 files; the `.gitignore`d folder excluded) and the Keyboard
Shortcuts table from the real keymap. On Nix-provided dotnet source `tools/nix-dev-env.sh` first.

## Known gaps

* Not implemented: "open results in an editor" (SHOULD), include/exclude presets (SHOULD), a command/UI to *start* a search-in-selection (the engine supports `SelectionScope`,
  but Core exposes no selection range, only `IActiveSelectionProvider.SelectedText`), rebinding UI (STRETCH).
* The replace preview in the tree sees only the (clipped) matched text, so for patterns that depend on context (lookarounds, anchors) it is an approximation; the replacement that is
  actually written is always computed from the file.
* A named pipe (FIFO) inside the workspace would block a worker on open (not guarded). The real `WorkspaceService.FindFilesAsync` was not available in this base; the
  engine is tested against a fake of it plus the real `LocalFileSystem` (walker) and must be re-checked when Platform lands it.
* Search-as-you-type and re-run on workspace change are not implemented (a folder change marks the results stale instead).
