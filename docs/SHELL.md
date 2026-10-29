# Shell (`CsCode.App` + `CsCode.Ui`)

The Visual Studio 2022 style workbench: title bar with menus and Quick Launch, toolbar, Dock-based document well and tool windows, status bar,
command palette, notifications, themes. It is the composition root of CsCode. There is no extension system: customisation is file based
(settings, keybindings, theme JSON), and nothing in the shell hosts or loads third-party code.

* `src/CsCode.Ui`: design system. Theme pipeline, built-in themes, icons, UI dispatcher, shared controls, `IToolWindow`.
* `src/CsCode.App`: shell. Services, view-models, views, Dock integration, startup.
* `tests/CsCode.App.Tests`: unit, headless UI, leak, responsiveness and real-window tests, plus the **dev host** (below).

## Architecture

```
Program -> App.axaml.cs -> AppSession -> AppStartup.BuildServicesAsync (pool thread) -> MainWindow(provider) (UI thread)
                                   |                                |
              AppServices.Build:                                    +-- ShellController.Initialize()  (before first frame, no I/O)
              AddCsCodeCore/Platform/Lsp/Dap/Editor/Profiling       +-- ShellController.StartDeferredAsync()  (after first frame)
              + AddCsCodeShell (shell services, singletons)
              + configure?.Invoke(services)   <- dev host / tests replace anything here
              main window closed -> AppShutdown.RunAsync (bounded) -> lifetime.Shutdown
```

* **`AppServices.Build`** registers the real modules and the shell services. There are no silent fallbacks: if a module that the shell needs
  is not registered, resolution fails and the shell shows the crash window with the log path (it never exits silently).
* **Process lifecycle** (`Startup/`, see below): the container is built and the file-reading Platform services are constructed off the UI thread; the
  exit is an orderly, bounded shutdown owned by `AppSession`.
* **`ShellController`** wires everything that is not pure view: theme registration and `ThemeApplier`, the palette, every shell command,
  keyboard pipeline, context-key tracker, menus, status items, tool-window auto-show, layout persistence, the UI stall watchdog.
* **First paint**: the window shows with the chrome and an empty dock; layout restore, command-line targets, theme from settings and the
  Welcome page run at `Background` priority after the first frame.
* **Models vs views**: all behaviour lives in toolkit-light models (`DocumentWell`, `QuickInputViewModel`, `PaletteSource`, `MenuModel`,
  `ExplorerViewModel`, `ErrorListViewModel`, `OutputViewModel`, `FindResultsViewModel`, `KeyboardPipeline`, `LayoutPersistence`, ...) that are unit tested
  with fakes; the views are thin and code-built (compiled bindings are off in these two projects; most views use no bindings at all).

### Services implemented (Core contracts)

| Contract | Implementation | Notes |
|---|---|---|
| `IWindowService` | `WindowService` | toasts with action buttons + notification centre, quick pick / input box overlays, native dialogs through `IShellHost`, progress notification + status item, clipboard, `OpenExternalAsync` |
| `IStatusBarService` | `StatusBarService` | thread safe, `Changed` coalesced to one per UI turn |
| `IOutputService` | `OutputService` | 2 MB ring per channel, appends batched (one flush per UI turn, never a post per line) |
| `INavigationService` | `NavigationService` | open/preview/reveal, `ApplyWorkspaceEditAsync` (open docs: one undo group each; closed files: read, edit, write via `IFileSystem` off the UI thread; create/rename/delete with the file-operation participants around them; confirmation for multi-file edits, setting `cscode.shell.confirmMultiFileEdits`) |
| `IToolWindow` host | `ToolWindowManager` | views created on first show, **disposed when hidden** |
| `IColorThemeService` consumer | `ThemeApplier` | the three VS2022 themes are registered into the module's `IColorThemeService` |

### Process lifecycle (`src/CsCode.App/Startup`)

* **Start-up.** `Program.Main` sets `GCSettings.LatencyMode = SustainedLowLatency` (no blocking gen-2 collections while typing; the editor's figures
  were measured in this mode) and installs the crash handler. `App` hands the desktop lifetime to an `AppSession`, which builds the container on a pool
  thread while Avalonia finishes starting (`AppStartup.BuildServicesAsync`) and constructs there the services whose constructors read files synchronously
  (`ConfigurationService`, `StateStorage`, `KeybindingService`, plus the file logger and the workspace service; docs/PLATFORM.md). The main window is then
  created and shown on the UI thread. A failure anywhere in this path, `Window.Show` included (a windowing or GL error), writes a fatal crash log and
  shows the crash window instead (a main window that failed while opening is closed without prompts); the run ends with exit code 1 when it is closed.
  If not even the crash window can be shown, the session prints the summary and the log path to stderr, shuts the services down and ends with exit
  code 1: with the exit taken over (below), a lost exception here would otherwise leave the process running without a window.
* **Exit.** The session takes the exit over (`ShutdownMode.OnExplicitShutdown`): when the main window has closed (after its own Save prompts and layout
  save), `AppShutdown.RunAsync` runs on the UI thread before the dispatcher stops, bounded by `App.ShutdownDeadline` (default **5 s**):
  1. flush `IStateStorage` (what the user did up to now is on disk before anything can hang);
  2. dispose, in parallel and off the UI thread, the services that own sessions and child processes: `ILanguageServerManager` (shutdown/exit to every
     server, then their process trees are killed), `IDebugService` (sessions terminated), `IBreakpointManager` (persists on dispose),
     `IDebugConfigurationService`;
  3. flush state again (`StateStorage.DisposeAsync`: what step 2 persisted), with at least 0.75 s even if step 2 used up the deadline;
  4. **kill tracked child processes that are still running**: modules report their language servers and debug adapters through
     `IMemoryMonitor.TrackChildProcess`; the shell wraps the registered monitor (`ShellMemoryMonitor`, a stand-in that tracks children and lists
     trimmables but samples nothing while CsCode.Profiling registers none) and `ChildProcessTracker` kills the survivors' process trees. Only reported
     pids are killed (a browser started through `xdg-open` is a child too), and only while the pid still has the reported start time (pid reuse);
  5. dispose the container synchronously on the UI thread (UI singletons must be disposed on their thread; an async container dispose would continue on
     a pool thread). The module singletons of step 2 are no-ops by then, so nothing here waits for a server.

  If the lifetime is ended some other way (`Shutdown` called directly), `AppShutdown.RunAbrupt` kills the tracked children and flushes state
  synchronously. Not covered: processes started by `ITaskService` are not reported through `TrackChildProcess` (Dap module), so a task that is still
  running at the exit is not killed by the shell.
* **Crash logs** go to `IAppPaths.LogsDir` (`$XDG_STATE_HOME/cscode/logs`, `%LOCALAPPDATA%\CsCode\Logs`, `~/Library/Logs/CsCode`, `$CSCODE_HOME/logs`),
  next to the rolling `cscode.log`; `CrashHandler.DefaultLogDirectory` asks Platform's pure `AppPaths` because the handler starts before the container.
  The handler is also registered in the container, so the main window reports deferred-startup and closing failures through it.

## Design system (`CsCode.Ui`)

**Theme pipeline.** `ThemeApplier` listens to `IColorThemeService.Changed` and publishes one `SolidColorBrush` per workbench colour id under
`vsc:<colorId>` (`{DynamicResource vsc:editor.background}`), plus the `ui:*` tokens and the Dock.Avalonia brush keys. Brushes are created once and
their `Color` is mutated in place, so a theme switch repaints every window without recreating anything. Resolution of missing ids uses the shell
fallbacks (`WorkbenchColors.ShellFallbacks`) and then `IColorThemeService.GetColor` (the Core chain), off the UI thread; the UI thread applies a small
immutable array. A theme switch costs 0.1-7 ms of UI work (measured, see Threading) because structural resource entries are published once: every
assignment into the application resource dictionary makes all controls re-resolve dynamic resources, so only brush colours and the Fluent palette change afterwards.

Code-built views use `ThemeBrushes.Get("statusBar.background")` (the same live brush instance).

**Tokens** (application resources): `ui:FontFamily` (Segoe UI, Inter, Noto Sans), `ui:FontSize` 12, `ui:FontSizeSmall` 11, `ui:FontSizeLarge` 14, `ui:FontSizeTitle` 20,
`ui:CornerRadius` 2, `ui:CornerRadiusNone` 0, `ui:BorderThickness`, `ui:ControlPadding`, `ui:MenuItemPadding`, `ui:ToolbarHeight` 30, `ui:TitleBarHeight` 30,
`ui:StatusBarHeight` 22, `ui:ItemHeight` 22, `ui:IconSize` 16, `ui:CodeFontFamily` (from `editor.fontFamily`).

**Built-in themes** (`VsThemes`): *Visual Studio Dark* (default), *Visual Studio Blue*, *Visual Studio Light*, each a `ColorTheme` that defines **every**
published id without relying on fallbacks (enforced by a test), with VS-style token colours (keywords `#569CD6`, types `#4EC9B0`, strings `#D69D85`,
comments `#57A64A`, numbers `#B5CEA8`, functions `#DCDCAA` in Dark; the Visual Studio Light equivalents in Blue/Light). Dark values are tested against the palette in the spec
(`#1E1E1E` editor, `#252526` tool windows, `#2D2D30` chrome, `#3F3F46` border, `#007ACC` accent, status bar `#007ACC` / `#CA5100` debugging / `#68217A` no folder).

**Controls: what is and is not custom.** Avalonia's Fluent theme is used as the *structural base* of the stock controls and is **retargeted** at the workbench palette
(`FluentBridge`: the per-variant `ColorPaletteResources` ramp plus about 100 control-specific keys for menus, scroll bars, tooltips, buttons, inputs, lists, tabs), with
VS density and shapes layered on in `Styles/VsTheme.axaml` (12 px text, 0/2 px radii, flat buttons, thin Fluent scroll bars with pill thumbs that widen on hover). This is deliberately not a
from-scratch re-template of every control; the result is the VS look (see screenshots), but a few rarely used controls (Slider, ToggleSwitch, Expander, ProgressBar,
DataGrid) still have Fluent's shape with VS colours. Hand-built VS controls: `StatusBarView`, `ToolbarView`, `TitleBarView`, `ShellMenuBar`, `FilterBox`,
`HighlightedTextBlock` (fuzzy highlight), `QuickInputView`, `DialogHostView`, `NotificationsView`, `Icon`.
`QuickPickView` and the status/tool bars live in `CsCode.App` rather than `CsCode.Ui` because they bind App view-models.

**Icons.** 246 Codicons as vector geometry (`IconCatalog`, `Icon` control, `IconKey` constants): crisp at any DPI, recoloured by foreground, no binary committed.
(A deliberate deviation from "embed the codicon font"; attribution in `THIRD-PARTY-NOTICES.md`; regeneration steps in `src/CsCode.Ui/Icons/README.md`.)

**Dock theme.** Dock.Avalonia's Fluent theme is retargeted through its `Dock*Brush` keys (see `ThemeApplier.DockBridge`). VS specifics: the active document tab carries a **top accent bar**
(style in `MainWindow`), preview tabs are italic (`EditorTab.PreviewFontStyle`), Dock's drag grip and bottom indicator are suppressed, an empty tool well takes no space,
tool windows have caption bars with the Dock dropdown (Dock / Float / Auto Hide / Hide), pin and close.

### `vsc:` colour keys panel authors may rely on

Every id below exists as `{DynamicResource vsc:<id>}` in every built-in theme and (through fallbacks) in every user theme.
The list is generated from `WorkbenchColors.All`; a test fails if this document and the catalogue drift apart.

<!-- VSC-KEYS-BEGIN -->
229 ids.

**Base** (16)

`foreground`, `descriptionForeground`, `disabledForeground`, `errorForeground`, `focusBorder`, `contrastBorder`, `contrastActiveBorder`, `icon.foreground`, `widget.shadow`, `selection.background`, `textLink.foreground`, `textLink.activeForeground`, `textCodeBlock.background`, `textSeparator.foreground`, `sash.hoverBorder`, `progressBar.background`

**Scrollbars** (4)

`scrollbar.shadow`, `scrollbarSlider.background`, `scrollbarSlider.hoverBackground`, `scrollbarSlider.activeBackground`

**Buttons and inputs** (32)

`button.background`, `button.foreground`, `button.hoverBackground`, `button.border`, `button.secondaryBackground`, `button.secondaryForeground`, `button.secondaryHoverBackground`, `checkbox.background`, `checkbox.foreground`, `checkbox.border`, `dropdown.background`, `dropdown.foreground`, `dropdown.border`, `dropdown.listBackground`, `input.background`, `input.foreground`, `input.border`, `input.placeholderForeground`, `inputOption.activeBorder`, `inputOption.activeBackground`, `inputOption.activeForeground`, `inputValidation.errorBackground`, `inputValidation.errorBorder`, `inputValidation.errorForeground`, `inputValidation.warningBackground`, `inputValidation.warningBorder`, `inputValidation.infoBackground`, `inputValidation.infoBorder`, `badge.background`, `badge.foreground`, `toolbar.hoverBackground`, `toolbar.activeBackground`

**Lists and trees** (15)

`list.activeSelectionBackground`, `list.activeSelectionForeground`, `list.inactiveSelectionBackground`, `list.inactiveSelectionForeground`, `list.hoverBackground`, `list.hoverForeground`, `list.focusBackground`, `list.focusOutline`, `list.highlightForeground`, `list.errorForeground`, `list.warningForeground`, `list.dropBackground`, `list.invalidItemForeground`, `list.filterMatchBackground`, `tree.indentGuidesStroke`

**Side bar and panel** (15)

`sideBar.background`, `sideBar.foreground`, `sideBar.border`, `sideBarTitle.foreground`, `sideBarSectionHeader.background`, `sideBarSectionHeader.foreground`, `sideBarSectionHeader.border`, `panel.background`, `panel.border`, `panelTitle.activeForeground`, `panelTitle.inactiveForeground`, `panelTitle.activeBorder`, `breadcrumb.foreground`, `breadcrumb.background`, `breadcrumb.focusForeground`

**Editor groups and tabs** (21)

`editorGroup.border`, `editorGroup.dropBackground`, `editorGroupHeader.tabsBackground`, `editorGroupHeader.tabsBorder`, `editorGroupHeader.noTabsBackground`, `tab.activeBackground`, `tab.activeForeground`, `tab.inactiveBackground`, `tab.inactiveForeground`, `tab.border`, `tab.activeBorder`, `tab.activeBorderTop`, `tab.hoverBackground`, `tab.hoverForeground`, `tab.unfocusedActiveBackground`, `tab.unfocusedActiveForeground`, `tab.unfocusedInactiveBackground`, `tab.unfocusedInactiveForeground`, `tab.lastPinnedBorder`, `tab.dirtyForeground`, `tab.previewForeground`

**Status bar, title bar, menus** (27)

`statusBar.background`, `statusBar.foreground`, `statusBar.border`, `statusBar.debuggingBackground`, `statusBar.debuggingForeground`, `statusBar.noFolderBackground`, `statusBar.noFolderForeground`, `statusBar.noFolderBorder`, `statusBarItem.hoverBackground`, `statusBarItem.prominentBackground`, `titleBar.activeBackground`, `titleBar.activeForeground`, `titleBar.inactiveBackground`, `titleBar.inactiveForeground`, `titleBar.border`, `menu.background`, `menu.foreground`, `menu.selectionBackground`, `menu.selectionForeground`, `menu.separatorBackground`, `menu.border`, `menubar.selectionBackground`, `menubar.selectionForeground`, `menubar.selectionBorder`, `commandCenter.background`, `commandCenter.foreground`, `commandCenter.border`

**Quick input and notifications** (18)

`quickInput.background`, `quickInput.foreground`, `quickInputList.focusBackground`, `quickInputList.focusForeground`, `quickInputTitle.background`, `pickerGroup.foreground`, `pickerGroup.border`, `notifications.background`, `notifications.foreground`, `notifications.border`, `notificationCenter.border`, `notificationCenterHeader.background`, `notificationCenterHeader.foreground`, `notificationToast.border`, `notificationLink.foreground`, `notificationsErrorIcon.foreground`, `notificationsWarningIcon.foreground`, `notificationsInfoIcon.foreground`

**Editor** (35)

`editor.background`, `editor.foreground`, `editor.selectionBackground`, `editor.inactiveSelectionBackground`, `editor.lineHighlightBackground`, `editor.lineHighlightBorder`, `editor.findMatchBackground`, `editor.findMatchHighlightBackground`, `editor.wordHighlightBackground`, `editor.stackFrameHighlightBackground`, `editor.focusedStackFrameHighlightBackground`, `editorCursor.foreground`, `editorLineNumber.foreground`, `editorLineNumber.activeForeground`, `editorIndentGuide.background`, `editorWhitespace.foreground`, `editorBracketMatch.background`, `editorBracketMatch.border`, `editorGutter.background`, `editorError.foreground`, `editorWarning.foreground`, `editorInfo.foreground`, `editorHint.foreground`, `editorWidget.background`, `editorWidget.foreground`, `editorWidget.border`, `editorSuggestWidget.background`, `editorSuggestWidget.border`, `editorSuggestWidget.foreground`, `editorSuggestWidget.selectedBackground`, `editorSuggestWidget.highlightForeground`, `editorHoverWidget.background`, `editorHoverWidget.foreground`, `editorHoverWidget.border`, `editorOverviewRuler.border`

**Debug, problems, git** (20)

`debugToolBar.background`, `debugToolBar.border`, `debugIcon.breakpointForeground`, `debugIcon.startForeground`, `debugIcon.stopForeground`, `debugIcon.pauseForeground`, `debugIcon.continueForeground`, `debugIcon.stepOverForeground`, `debugIcon.stepIntoForeground`, `debugIcon.stepOutForeground`, `debugIcon.restartForeground`, `problemsErrorIcon.foreground`, `problemsWarningIcon.foreground`, `problemsInfoIcon.foreground`, `gitDecoration.modifiedResourceForeground`, `gitDecoration.addedResourceForeground`, `gitDecoration.deletedResourceForeground`, `gitDecoration.untrackedResourceForeground`, `gitDecoration.ignoredResourceForeground`, `gitDecoration.conflictingResourceForeground`

**Shell-specific (not VS Code ids; user themes may omit them, ShellFallbacks applies)** (4)

`toolbar.background`, `toolbar.foreground`, `toolbar.separator`, `statusBarItem.activeBackground`

**Terminal and welcome** (22)

`terminal.background`, `terminal.foreground`, `terminal.ansiBlack`, `terminal.ansiRed`, `terminal.ansiGreen`, `terminal.ansiYellow`, `terminal.ansiBlue`, `terminal.ansiMagenta`, `terminal.ansiCyan`, `terminal.ansiWhite`, `terminal.ansiBrightBlack`, `terminal.ansiBrightRed`, `terminal.ansiBrightGreen`, `terminal.ansiBrightYellow`, `terminal.ansiBrightBlue`, `terminal.ansiBrightMagenta`, `terminal.ansiBrightCyan`, `terminal.ansiBrightWhite`, `welcomePage.background`, `welcomePage.tileBackground`, `welcomePage.tileHoverBackground`, `welcomePage.tileBorder`

<!-- VSC-KEYS-END -->

## Behaviour

**Menus.** File, Edit, View, Go, Debug, Terminal, Tools, Window, Help (`ShellMenus`). Items run command ids; enablement = command registered **and** `when` true; shortcut text from
`IKeybindingService.GetLabel`; check marks for tool windows; Open Recent; Alt mnemonics. Menus regenerate when they open, so state is always current. There are no extension or marketplace entries.
Contributed items (`IContributionRegistry.Of<MenuContribution>()`) feed the **Explorer context menu** (`explorer/context`); `ContributedMenus.Get` also serves `commandPalette`,
`editor/context`, `editor/title` for the Editor module.

**Keyboard.** A tunnelling window handler maps key events to `KeyStroke` (VS Code names) and asks `IKeybindingService.Resolve`: matched binding runs the command asynchronously (never blocks the key),
a chord prefix is swallowed and the status bar shows "(Ctrl+K) was pressed. Waiting for second key of chord...", unbound keys pass through to the focused control (typing is untouched),
Esc or a 5 s timeout abandons a chord. Context keys (`ContextKeyTracker`): `editorTextFocus`, `editorHasSelection`, `editorReadonly`, `editorLangId`, `resourceExtname`, `resourceFilename`,
`resourceScheme`, `explorerViewletFocus`, `listFocus`, `panelFocus`, `inputFocus`, `terminalFocus`, `inDebugMode`, `debugState`, `isMac/isLinux/isWindows`, `workspaceFolderCount`, `hasOpenEditors`,
`activeEditorIsDirty`, `quickInputVisible`. Focus areas come from the `ShellFocus.Area` attached property on view roots.

**Documents.** `DocumentWell` adds to the Dock model (the single source of truth for tabs, order and groups): open / preview (italic, replaced by the next preview, promoted by editing or reopening) /
pin / close / close others / saved / all / to the right / reopen closed (last 20), Save/Don't Save/Cancel prompts (also when the user clicks Dock's close button: Dock's close is cancelled and routed through the prompt),
split right/down, move between groups, MRU order and the **Ctrl+Tab switcher** overlay. Only the active tab of each group plus the last `cscode.shell.liveEditorViews` (default 8) tabs keep a live editor view;
others follow the editor's contract (docs/EDITOR.md): `GetViewState()` (caret, selection, scroll offsets, folds) -> `IEditorViewFactory.DisposeView(view)`, and on the next
presentation `CreateView` + `RestoreViewState`. Closed tabs' views go back through `DisposeView` too. Dock presents a thin `Border` host per tab (see *View lifecycle and Dock* below).

**View lifecycle and Dock.** Two things kept destroyed views reachable, and the shell handles both:
* Dock.Avalonia 12.1 caches the control it presented for every dockable (control recycling on the `DockControl`, keyed by the dockable), and a host
  emptied while out of the window still lists the removed view in its composition visual (that list is synchronised only when the host is rendered).
  So a tab whose host Dock no longer shows drops the host (a fresh one is built on the next presentation) and removes its own entry from Dock's
  recycling cache; a closed tab always removes its entry. This was the "closed tabs stay reachable" retention noted earlier: with it fixed, closed tab
  objects, their documents and views are all released (the live 300-tab test asserts every sample, not just the steady state).
* A focused editor stays reachable from Avalonia's focus machinery and from AvaloniaEdit's static "last focused element" (`RoutedCommand`) until another
  element takes focus; detaching it is not enough. Before Dock removes a tab whose view holds keyboard focus, the well clears focus (the window is
  not focusable), and once the document that takes its place is presented its editor gets the keyboard, as in Visual Studio. When the last document
  closes nothing takes focus, so AvaloniaEdit's static slot keeps that one view until focus next moves anywhere (bounded: one view).

**Layout.** `LayoutPersistence`: tool placement, visibility, splitter proportions, open files with pin/preview, active file; versioned JSON, workspace scope first then global default,
tolerant of corrupt/future data; saved (debounced) on change and on exit; `View > Appearance > Reset Window Layout`. There is no left tool well unless a tool window asks for one.

**Palette / Quick Launch** (`PaletteSource`): plain = go to file (fuzzy over a trimmable, bounded workspace index, ranked on a worker, top 100), `>` commands (title, category, keybinding badge, recent first, `Enablement` honoured),
`:` go to line[:column], `@` document symbols, `#` workspace symbols, `?` help. Cheap modes answer synchronously (first paint needs no dispatcher turn); stale async results are dropped. Also drives `ShowQuickPick`/`ShowInputBox`.
Theme picker (`workbench.action.selectTheme`) previews live while arrowing and reverts on Esc.

**Saving and file operations with language servers** (`FileParticipants`, docs/LSP.md). Every save of the shell (Save, Save As, Save All, the Save
answer of a close prompt) goes through `DocumentWell.SaveAsync`: the `IWillSaveParticipant`s (the LSP manager: `willSave` / `willSaveWaitUntil`) are
asked first and their edits applied as one undo unit each; together they may delay a save by at most **1.5 s**, a failing or late participant is
skipped (logged) and edits computed for a version the user has since changed are dropped. Explorer create/rename/move/delete/duplicate/paste and the
resource operations of workspace edits run the `IFileOperationParticipant`s around the file-system call: `WillRenameFilesAsync` first (bounded to 3 s;
its edit is applied before the move, so references and imports follow the file; in a workspace edit without a second confirmation), then
`DidRename/DidCreate/DidDeleteFilesAsync`. Participants are resolved on first use (the language client itself depends on the navigation service).

**Explorer** (virtualised flat rows, 20k siblings expand in well under a second): lazy folders, off-thread read/sort, VS file icons, persisted expansion, create/rename (F2 inline)/delete (trash, confirmed)/duplicate/cut/copy/paste/move (model; with the file-operation participants),
reveal active file, `files.exclude`, watcher batches applied by diffing only the affected loaded directories, type-ahead, filter box (loaded nodes), context menu incl. contributed items, empty state, multi-root.
**Error List** (counts and chips, search, group by file, sort, double-click navigates, aggregation and sorting on a worker at <= 10 Hz, 50k rows apply as one property change).
**Output** (channel dropdown, clear, shows the most recent 5,000 lines of a channel; the ring keeps 2 MB). **Find Results** (target of `cscode.references.show`; preview lines read lazily).

**Status bar items owned by the shell:** Ln/Col, indent, encoding, EOL, language mode (picker), diagnostics counts (computed off-thread), memory (with UI stall counters in its tooltip), debug state, language-server status, notification bell.
Other modules (Vim mode, remote indicator) register their own through `IStatusBarService`; text may use `$(icon-name)`.

### Commands registered by the shell

`workbench.action.showCommands`, `quickOpen`, `gotoLine`, `gotoSymbol`, `showAllSymbols`, `files.openFolder`, `files.openFile`, `files.newUntitledFile`, `files.save`, `files.saveAs`, `files.saveAll`,
`closeActiveEditor`, `closeAllEditors`, `closeOtherEditors`, `reopenClosedEditor`, `nextEditor`, `previousEditor`, `pinEditor`, `splitEditorRight`, `splitEditorDown`, `moveEditorToNextGroup`, `closeFolder`, `quit`, `reloadWindow`
(refreshes menus only), `toggleFullScreen`, `toggleZenMode`, `selectTheme`, `openSettings`, `openSettingsJson`, `workbench.view.explorer`, `workbench.action.output.toggleOutput`, `workbench.actions.view.problems`,
`workbench.action.findResults.toggle`, `cscode.references.show`, `cscode.shell.{resetLayout,openRecent,about,showWelcome,showKeyboardReference,toggleNotifications,changeLanguageMode,showErrorList}`.
Everything else in the menus (`editor.action.*`, `debug.*`, terminal, tasks) is owned by other modules and stays disabled until they register it.

### Settings

`cscode.shell.liveEditorViews` (8), `cscode.shell.confirmMultiFileEdits` (true), `cscode.diagnostics.uiStallLogMs` (0; Debug builds default to 100), plus the ones it reads:
`workbench.colorTheme`, `workbench.startupEditor`, `files.exclude`, `editor.fontFamily`, `editor.tabSize`, `editor.insertSpaces`. They are declared through `IConfigurationService.RegisterProperties`.

## Threading and isolation (binding rules, and how they are enforced)

* **Static**: `Microsoft.CodeAnalysis.BannedApiAnalyzers` in both projects (`BannedSymbols.txt`): `Task.Wait/WaitAll/WaitAny`, `Task<T>.Result`, `GetAwaiter().GetResult()`, `Thread.Sleep`, sync `File.*` / `Directory.*` scans. The build fails on use
  (this caught two of my own mistakes). The single exception is the crash-log write, under a commented `#pragma`.
* **`IUiDispatcher`**: every service accepts calls from any thread and marshals through it. `PostCoalesced(key, action)` (latest wins), `PostBatched(items, apply, maxSliceMs = 4)`,
  `AssertUiThread/AssertNotUiThread` (debug), stats (`ui.stalls`, `ui.longestDispatchMs`, name of the longest callback).
* **`UiStallWatchdog`**: reports when the UI thread has not pumped for > threshold and names the dispatcher callback that was in flight (a managed thread cannot capture another thread's stack in-process).
* **Heavy work off the UI thread**: palette ranking, Error List aggregation/sort/group, Explorer directory read/sort/diff, theme preparation, diagnostics counting, large text splitting. Only small immutable results are applied.
* **Producers to UI are one-way and bounded**: coalesced per key, versioned/stale results dropped, lists capped.

### Measured (headless Skia, this machine, shared with other build agents; `ResponsivenessTests`)

A heartbeat posts a tick to the real dispatcher every 10 ms and records how late each tick runs while a hostile source works (best of up to 3 attempts, 2.5 s each; budgets p99 < 16 ms, max < 50 ms):

| Hostile source (alone) | p99 | max | longest UI callback |
|---|---|---|---|
| 200k-file palette search on every keystroke | 11.4 ms | 20.3 ms | 0.2 ms |
| 100k diagnostics/s into the Error List | 10.5 ms | 15.3 ms | 2.6 ms |
| 1M-line Output flood | 5.8 ms | 15.0 ms | 3.6 ms |
| tool-window show/hide storm (30 ms period) | 15.7 ms | 16.5 ms | 0.3 ms |
| theme switching | 10.6 ms | 12.8 ms | 5.1 ms |
| **all at once** | **18.7 ms (budget 16 not met)** | 19.8-28.8 ms | 9-16 ms |

Fixes that this suite drove: theme application 30-50 ms to < 8 ms (idempotent resource publication), status bar rebuild made incremental, diagnostics counting moved off the UI thread,
Output attach limited to the recent tail, `PostCoalesced` + version race in `ThemeApplier` (a stale preparation could overwrite the newest one).
Other checks: service calls from the UI thread return without waiting for the work; shell async flows run under a `SynchronizationContext` whose `Send` throws (no sync-over-async dispatch).

## Memory

Bounded: Output 2 MB per channel, 20k lines in the view; notification history 100; closed-tab history 20; palette index capped at 300k files and an `IMemoryTrimmable`; icon geometry cache trimmable; Error List at most one immutable array.
Lifetimes: every view and model owns a `DisposableStore`; tool-window views and their view-models are disposed when hidden; tab views by the LRU policy and on close. Tests: 100x show/hide of every tool window balances creation and disposal and retains nothing;
500 palette opens retain no entries; 500 dialogs leave the heap flat; 200 theme switches leave the resource table and brush set unchanged; 300 tabs opened/closed in the live Dock host release every sampled tab, document and view
(managed heap flat between tab 100 and 300); a focused real editor's view is released when its tab closes, and an evicted view is released while its tab stays open; output flood and status updates use a bounded number of dispatcher posts.

## Running it

```bash
dotnet build CsCode.slnx
dotnet test tests/CsCode.App.Tests            # on a Nix dotnet: source tools/nix-dev-env.sh first (SkiaSharp needs the libs)
```

* **Dev host** (`tests/CsCode.App.Tests/DevHost`): composes the **real** shell (`AppServices.Build` + `MainWindow`) over in-memory fakes for the modules that are written concurrently
  (command service, context keys with a real `when` evaluator, configuration, keybindings with chord state, workspace + file system + watcher, state storage, colour themes with a fallback chain, editor factory, debug service).
  `DevScene.BuildAsync` seeds a workspace, tabs, diagnostics, output and a debug state. `host.Capture("name")` writes `.scratch/screenshots/name.png` (git-ignored).
* **Screenshots**: `ScreenshotTests` renders Dark, Blue and Light, the palette, an open File menu, the debugging (orange) state, toasts and the modal dialog; `RealWindowSmokeTests` runs the
  real app on the X server (`DISPLAY`) in a child process, photographs the live window and checks for clean exit and no crash log.
* A production start (`dotnet run --project src/CsCode.App`) needs the other modules' registrations; until they are merged it ends in the crash window that names the missing service (by design).

What the screenshots were checked for: custom title bar with the nine menus and a centred Quick Launch box, standard toolbar (back/forward, new/open/save/save-all, undo/redo, launch-config combo, Start, find),
document tabs (active top accent bar, italic preview tab, dirty marker), Explorer docked right with VS file icons and chevrons, Error List with severity chips and counts, bottom tab strip, status bar blue / orange (debugging, with step buttons in the toolbar) / purple (no folder),
File menu with icon, shortcut and separator columns, palette with fuzzy list and keybinding badges, toasts with severity icons and action buttons, modal Save/Don't Save/Cancel dialog; identical structure in all three themes.

## Known gaps and issues (honest list)

Not done: single-instance forwarding (SHOULD); native menu bar (`NativeMenu` on macOS / DBus global menu); Explorer drag-and-drop **UI** (the model's `MoveAsync` is tested; the view has no drag source/drop target) and dropping files from the OS onto the window;
dirty marker is Dock's `*` rather than a dot that turns into a close glyph on hover; pinned tabs have model/commands but no pin glyph; layout restore reopens tabs but does not recreate split document groups or floating windows;
auto-hide/peek animations, taskbar jump list, high-contrast theme, `--diff`, toolbar overflow (all SHOULD); the Windows/macOS title-bar path (`ExtendClientAreaToDecorationsHint`) is written but untested here (Linux only).
`reloadWindow` only refreshes menus. `Find in Files` has no panel of its own (Find Results hosts results delivered by `cscode.references.show`). Explorer filter searches loaded nodes only.

Known issues: (1) **Combined hostile storm p99 is ~19 ms, not < 16 ms** (each source alone meets it; max is within 50 ms). (2) Dock.Avalonia 12.1.0.13 keeps every presented dockable in its control-recycling
cache; the shell removes a tab's entry itself (see *View lifecycle and Dock*), which depends on the cache being reachable through `ControlRecyclingDataTemplate.ControlRecycling` on an ancestor of the host.
(3) AvaloniaEdit's static `RoutedCommand` keeps the last element that took focus: after the last document closes, that one view stays reachable until focus moves anywhere (bounded).

## Integration notes

* **Needs registered** (resolved with `GetRequiredService`): `ICommandService`, `IContextKeyService`, `IConfigurationService`, `IKeybindingService`, `IWorkspaceService`, `IStateStorage`, `IColorThemeService`, `IEditorViewFactory`,
  and from Core `IDocumentManager`, `IDiagnosticService`, `IContributionRegistry`, `ILanguageFeatureRegistry`. **Optional**: `IDebugService` (toolbar combo, debug context keys), `ILanguageService`, `IMemoryMonitor`, `ILanguageServerManager`.
* Editor views must be a `Control`; the shell wraps them in a host, sets the focus-area marker on the host, and hands the view back through `IEditorViewFactory.DisposeView` when the tab
  closes or loses its live-view slot (after `GetViewState`, restored with `RestoreViewState` on re-creation).
* `IToolWindow`s registered by other modules (`AddSingleton<IToolWindow, ...>`) are placed by `DefaultLocation`; `AutoShowWhen` windows start hidden and follow their context key; a Left-located tool creates the left well.
* The brief's `IExtensionService.ActivateByEventAsync("onStartupFinished")` is **not** used: the extension system was removed (commit "Drop the extension system").
* Remaining mentions of the removed system outside this module (not edited, per ownership): `KnownCommands.InstallExtension/ShowExtensions`, `IAppPaths.ExtensionsDir`, `ConfigurationProperty.ExtensionId`, `ColorTheme.ExtensionId`, `LanguageInfo.ExtensionId`,
  `DebuggerType.ExtensionId`, doc comments in Core, the README tagline, and the empty "EXTENSIONS" section of `Directory.Packages.props`. None is used by the shell; the integrator may delete them.
