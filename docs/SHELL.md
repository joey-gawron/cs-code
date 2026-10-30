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
              AddCsCodeCore/Platform/Lsp/Dap/Editor/Vim/Profiling   +-- ShellController.StartDeferredAsync()  (after first frame)
              + AddCsCodeShell (shell services, singletons)
              + configure?.Invoke(services)   <- dev host / tests replace anything here
              main window closed -> AppShutdown.RunAsync (bounded) -> lifetime.Shutdown
```

* **`AppServices.Build`** registers the real modules and the shell services. There are no silent fallbacks: if a module that the shell needs
  is not registered, resolution fails and the shell shows the crash window with the log path (it never exits silently).
* **Process lifecycle** (`Startup/`, see below): the container is built and the file-reading Platform services are constructed off the UI thread; the
  exit is an orderly, bounded shutdown owned by `AppSession`.
* **`ShellController`** wires everything that is not pure view: theme registration, completion and `ThemeApplier`, the palette, every shell command,
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
| `IToolWindow` host | `ToolWindowManager` | views created on first show, **disposed when hidden**; `ToolWindowLocation.Document` windows are not docked (see Layout) |
| `IViewActivator` | `ShellViewActivator` | panels' "show view X" (Find in Files, Settings, Keyboard Shortcuts, Watch): a tool window is shown in its well, a document-location window opens or re-activates its singleton document tab; any thread |
| `IClipboardService` (Core, new) | `ClipboardService` | the system clipboard of the main window (Avalonia; X11 CLIPBOARD, also under XWayland) through `IPlatformClipboard`, CsCode's shape metadata (whole lines, block, one piece per caret), the in-memory clipboard ring, and the Linux PRIMARY selection (`X11PrimarySelection`); see *Clipboard* below. `IWindowService.CopyToClipboardAsync` goes through it |
| `IActiveSelectionProvider` | `ActiveSelectionProvider` | primary selection of the active editor (`${selectedText}` for Dap, the Find in Files seed); the UI thread publishes a (document, range) snapshot, readers on any thread take the text on demand |
| `IColorThemeService` consumer | `ThemeApplier`, `ThemePicker` | the three VS2022 themes are registered into the module's `IColorThemeService`, and the service is given the shell's completion (`ThemeCompletion`) so that it hands out complete themes; `IColorThemePreview` (Core, optional) is used for the Settings page dropdown's live preview |

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
their `Color` is mutated in place, so a theme switch repaints every window without recreating anything. Every theme is first **completed**
(`ThemeCompletion`, below; the editor's service hands out completed themes, any other service's theme is completed by the applier, cached per theme
instance), so no published id is ever unthemed; the shell fallbacks (`WorkbenchColors.ShellFallbacks`) and `IColorThemeService.GetColor` (the Core
chain) remain as a safety net. Preparation runs off the UI thread; the UI thread applies a small immutable array. Structural resource entries are
published once (every assignment into the application resource dictionary makes all controls re-resolve dynamic resources), so a runtime switch is
brush colours in one dispatcher callback, then Avalonia Fluent's palette, light/dark variant and text-selection colour in one callback each (each makes
every control re-resolve its Fluent resources: the palette step is 4-8 ms on a full window, the others < 1 ms). A newer switch supersedes the queued
steps of an older one, and a **burst** of switches (two within `ThemeApplier.BurstWindow`, 200 ms: arrowing through a theme list) repaints the
brushes each time but runs the Fluent steps once, when the burst settles. The startup apply runs everything in one go before the first frame.

Code-built views use `ThemeBrushes.Get("statusBar.background")` (the same live brush instance).

**Tokens** (application resources): `ui:FontFamily` (Segoe UI, Inter, Noto Sans), `ui:FontSize` 12, `ui:FontSizeSmall` 11, `ui:FontSizeLarge` 14, `ui:FontSizeTitle` 20,
`ui:CornerRadius` 2, `ui:CornerRadiusNone` 0, `ui:BorderThickness`, `ui:ControlPadding`, `ui:MenuItemPadding`, `ui:ToolbarHeight` 30, `ui:TitleBarHeight` 30,
`ui:StatusBarHeight` 22, `ui:ItemHeight` 22, `ui:IconSize` 16, `ui:CodeFontFamily` (from `editor.fontFamily`).

**Built-in themes** (`VsThemes`): *Visual Studio Dark* (default), *Visual Studio Blue*, *Visual Studio Light*, each a `ColorTheme` that defines **every**
published id without relying on fallbacks (enforced by a test), with VS-style token colours (keywords `#569CD6`, types `#4EC9B0`, strings `#D69D85`,
comments `#57A64A`, numbers `#B5CEA8`, functions `#DCDCAA` in Dark; the Visual Studio Light equivalents in Blue/Light). Dark values are tested against the palette in the spec
(`#1E1E1E` editor, `#252526` tool windows, `#2D2D30` chrome, `#3F3F46` border, `#007ACC` accent, status bar `#007ACC` / `#C95000` debugging / `#68217A` no folder).
The three pass every chrome contrast check without repair; four values were nudged for that (WCAG AA, 4.5:1, below): the debugging orange `#CA5100` gives
white text 4.48:1 and is `#C95000` (4.54:1, indistinguishable), list/quick-pick selection `#3399FF` under white text (3.0:1) is the accent `#007ACC`,
Dark's secondary text `#999999` (4.42:1 on menus and inputs) is `#A0A0A0`, Light's secondary and inactive-tab text `#717171` is `#6A6A6A`.
Two more palettes, *High Contrast* (black) and *High Contrast Light* (white), are not offered as themes: they are the bases `ThemeCompletion` fills
high-contrast themes from (`VsThemes.BaseFor(kind)`).

**Theme catalogue** (`CsCode.Editor/Theming/BuiltinColorThemes`, registered by the editor's `ColorThemeService`): besides the three VS 2022 themes, the
21 VS Code-format themes embedded in `TextMateSharp.Grammars` 2.0.3, with their `include` chains resolved (Dark+ includes Dark (Visual Studio),
Light+ and Light High Contrast include Light (Visual Studio)): Dark+, Light+, Dark (Visual Studio), Light (Visual Studio), Dark High Contrast,
Light High Contrast, Abyss, Kimbie Dark, Monokai, Monokai Dimmed, Quiet Light, Red, Solarized Dark, Solarized Light, Tomorrow Night Blue (VS Code's
built-in theme extensions), Atom One Dark, Atom One Light, One Dark, Dracula, and the package's own "Visual Studio Dark/Light", listed as
*Visual Studio Dark/Light (TextMateSharp)* so they cannot be confused with the shell's. Labels are VS Code's (`workbench.colorTheme` stores the label,
as VS Code does; ids such as `vscode.monokai` are accepted too); kinds come from VS Code's `uiTheme` (several files carry no `type`). Licences and
origins: `THIRD-PARTY-NOTICES.md`. User theme files (`{UserDataDir}/themes/*.json`) join the same list.

**Theme completion** (`ThemeCompletion`). A VS Code theme defines the VS Code workbench, not the VS 2022 shell (Dark+ defines 12 of the 231 published
ids). The shell completes every theme, built-in or user, in three steps: (1) every key the theme defines is kept, and all token colours untouched;
(2) a missing chrome key is **borrowed** from the related key the theme does define, the way the VS 2022 layout maps onto VS Code's (toolbar = title
bar band, Quick Launch box = input box, tool windows = side bar, menus = dropdowns, ...; a text colour is borrowed only together with its background,
so text and surface stay a matched pair; the editor colours come from the theme's global token colours when `colors` lacks them, as in VS Code);
(3) anything still missing comes from the nearest VS 2022 base (Dark, Light, High Contrast, High Contrast Light by the theme's kind). So no chrome
element is ever unthemed: in particular the coloured icons (green Start triangle, Error List severity icons, debug step icons, notification icons),
which no VS Code theme colours and which were transparent (invisible) with Dark+ before the completion. Then **contrast**: 40 chrome text pairs
(`ThemeCompletion.ChromeText`: title and menu bar, Quick Launch, toolbar, dropdowns, menus and shortcuts, document tabs, tool windows and panels,
headers, the Settings page, links, status bar in its three states, list and quick-pick selections, inputs, buttons, badges, toggle chips,
notifications, hovers, completion list, editor widgets) are composited as drawn and checked against WCAG AA for normal text (4.5:1); a failing text
colour is moved towards white or black (same hue, 1 % steps) just far enough to pass, or, when the text is already white (black) on its side, its
background is moved instead (white on a mid orange gets a slightly darker orange). Disabled and inactive-window text is exempt, as in WCAG.
`ThemeCompletion.Analyze` returns the evidence (source of every colour, every check). The table below is generated by
`ThemeCatalogTests.Shell_docs_carry_the_current_contrast_table`, which fails when it is stale (the current one is written to
`.scratch/screenshots/theme-contrast.md`). Colours = defined by the theme / borrowed / from the VS 2022 base.

<!-- THEME-CONTRAST-BEGIN -->
40 chrome text pairs checked per theme (WCAG AA, 4.5:1). Colours: theme / borrowed / VS 2022 base, of 231 published ids.

| Theme | Kind | Origin | Colours | Below AA before repair: element (foreground key) ratio, substitute, ratio after |
|---|---|---|---|---|
| Visual Studio Dark | dark | CsCode (Visual Studio 2022 palette) | 231 / 0 / 0 | none |
| Visual Studio Blue | light | CsCode (Visual Studio 2022 palette) | 231 / 0 / 0 | none |
| Visual Studio Light | light | CsCode (Visual Studio 2022 palette) | 231 / 0 / 0 | none |
| Dark+ (default dark) | dark | VS Code built-in | 12 / 25 / 194 | none |
| Light+ (default light) | light | VS Code built-in | 11 / 31 / 189 | none |
| Dark (Visual Studio) | dark | VS Code built-in | 12 / 25 / 194 | none |
| Light (Visual Studio) | light | VS Code built-in | 11 / 31 / 189 | none |
| Dark High Contrast | HC dark | VS Code built-in | 7 / 22 / 202 | none |
| Light High Contrast | HC light | VS Code built-in | 11 / 31 / 189 | none |
| Abyss | dark | VS Code built-in | 71 / 90 / 70 | Quick Launch box (`descriptionForeground`, borrowed) 2.74, #7187AE 4.53; Menu shortcut (`descriptionForeground`, borrowed) 2.74, fixed with an earlier pair 4.53; Menu, highlighted item (`menu.selectionForeground`, borrowed) 3.90, #7594D1 4.53; Document tab, inactive (`tab.inactiveForeground`, borrowed) 3.11, #6C82AE 4.54; Tool window, secondary text (`descriptionForeground`, borrowed) 3.31, fixed with an earlier pair 5.47; Bottom panel, secondary text (`descriptionForeground`, borrowed) 3.31, fixed with an earlier pair 5.47; Settings page, secondary text (`descriptionForeground`, borrowed) 3.28, fixed with an earlier pair 5.42; Settings scope tab, inactive (`panelTitle.inactiveForeground`, borrowed) 3.28, #637AA6 4.56; Link (`textLink.foreground`, borrowed) 3.12, #2E7FB5 4.52; List selection (`list.activeSelectionForeground`, borrowed) 3.90, #7594D1 4.53; Quick pick (`quickInput.foreground`, borrowed) 4.15, #6F8FCF 4.53; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 3.90, #7594D1 4.53; Input placeholder (`input.placeholderForeground`, borrowed) 2.74, #7187AE 4.53; Toggle chip, on (Error List) (`foreground`, borrowed) 3.66, #7B99D3 4.51; Notification (`notifications.foreground`, borrowed) 4.15, #6F8FCF 4.53; Completion list (`editorSuggestWidget.foreground`, borrowed) 4.15, #6F8FCF 4.53; Editor widget (find, peek) (`editorWidget.foreground`, borrowed) 4.15, #6F8FCF 4.53 |
| Kimbie Dark | dark | VS Code built-in | 39 / 77 / 115 | Quick Launch box (`descriptionForeground`, borrowed) 2.72, #C0AE99 4.55; Menu shortcut (`descriptionForeground`, borrowed) 4.00, fixed with an earlier pair 6.70; Menu, highlighted item (`menu.selectionForeground`, borrowed) 4.32, #D1D1D1 4.54; Tool window, secondary text (`descriptionForeground`, borrowed) 4.00, fixed with an earlier pair 6.70; Bottom panel, secondary text (`descriptionForeground`, borrowed) 4.00, fixed with an earlier pair 6.70; List selection (`list.activeSelectionForeground`, borrowed) 3.39, #E3CDB3 4.51; List selection, unfocused (`list.inactiveSelectionForeground`, borrowed) 3.59, #E0C7AA 4.52; Input placeholder (`input.placeholderForeground`, borrowed) 2.72, #C0AE99 4.55; Toggle chip, on (Error List) (`foreground`, borrowed) 4.13, #D8B893 4.52 |
| Monokai | dark | VS Code built-in | 77 / 80 / 74 | Menu, highlighted item (`menu.selectionForeground`, borrowed) 3.06, #F5F5F5 4.50; Settings scope tab, inactive (`panelTitle.inactiveForeground`, theme) 3.03, #928F80 4.57 |
| Monokai Dimmed | dark | VS Code built-in | 49 / 80 / 102 | Quick Launch box (`descriptionForeground`, borrowed) 2.59, #C4C5C4 4.51; Menu, highlighted item (`menu.selectionForeground`, borrowed) 3.08, #F4F4F4 4.50; List selection (`list.activeSelectionForeground`, borrowed) 2.94, #F4F5F4 4.53; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 2.94, #F4F5F4 4.53; Input placeholder (`input.placeholderForeground`, borrowed) 2.59, #C4C5C4 4.51 |
| Quiet Light | light | VS Code built-in | 43 / 81 / 107 | Menu, highlighted item (`menu.selectionForeground`, borrowed) 3.48, #5A5A5A 4.57; Document tab, inactive (`tab.inactiveForeground`, borrowed) 3.52, #4D4B52 4.54; Error message (`errorForeground`, theme) 2.23, #A45D56 4.50; List selection (`list.activeSelectionForeground`, theme) 3.48, #5A5A5A 4.57; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 3.67, #5E5E5E 4.53; Badge (`badge.foreground`, base) 3.10, background #7D6F92 4.60 |
| Red | dark | VS Code built-in | 42 / 85 / 104 | none |
| Solarized Dark | dark | VS Code built-in | 72 / 90 / 69 | Quick Launch box (`descriptionForeground`, borrowed) 2.55, #8D9EA1 4.55; Menu shortcut (`descriptionForeground`, borrowed) 3.38, fixed with an earlier pair 6.02; Menu, highlighted item (`menu.selectionForeground`, borrowed) 2.47, #BFC7C8 4.54; Document tab, inactive (`tab.inactiveForeground`, theme) 4.23, #99A7A7 4.55; Tool window, secondary text (`descriptionForeground`, borrowed) 3.38, fixed with an earlier pair 6.02; Bottom panel, secondary text (`descriptionForeground`, borrowed) 3.38, fixed with an earlier pair 6.02; Column and section headers (`sideBarSectionHeader.foreground`, borrowed) 4.23, #99A7A7 4.55; Group headers (`foreground`, borrowed) 3.58, #98A6A8 4.50; Settings page, secondary text (`descriptionForeground`, borrowed) 3.02, fixed with an earlier pair 5.39; Settings scope tab, inactive (`panelTitle.inactiveForeground`, borrowed) 3.02, #7E9195 4.55; List selection (`list.activeSelectionForeground`, borrowed) 2.47, #BFC7C8 4.54; List selection, unfocused (`list.inactiveSelectionForeground`, borrowed) 4.24, #89999B 4.53; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 2.47, #BFC7C8 4.54; Input placeholder (`input.placeholderForeground`, theme) 2.92, #899EA2 4.51; Toggle chip, on (Error List) (`foreground`, borrowed) 3.62, fixed with an earlier pair 4.56; Hover and tooltip (`editorHoverWidget.foreground`, borrowed) 3.58, #98A6A8 4.50 |
| Solarized Light | light | VS Code built-in | 64 / 89 / 78 | Title bar and menu bar (`titleBar.activeForeground`, borrowed) 3.64, #586B72 4.56; Quick Launch box (`descriptionForeground`, borrowed) 1.86, #575E5E 4.57; Workspace name (title bar) (`commandCenter.foreground`, borrowed) 3.71, #4D6066 4.55; Toolbar (`toolbar.foreground`, borrowed) 3.64, #586B72 4.56; Dropdown (toolbar, Settings) (`dropdown.foreground`, borrowed) 4.39, #566C73 4.52; Menu (`menu.foreground`, borrowed) 3.64, #586B72 4.56; Menu shortcut (`descriptionForeground`, borrowed) 2.20, fixed with an earlier pair 5.41; Menu, highlighted item (`menu.selectionForeground`, borrowed) 3.24, #565656 4.52; Document tab, active (`tab.activeForeground`, borrowed) 4.13, #5F747B 4.56; Document tab, inactive (`tab.inactiveForeground`, theme) 3.33, #47595F 4.54; Tool window text (`foreground`, borrowed) 3.64, #586B72 5.60; Tool window, side bar text (`sideBar.foreground`, borrowed) 3.64, #586B72 4.56; Tool window, secondary text (`descriptionForeground`, borrowed) 2.20, fixed with an earlier pair 5.41; Bottom panel text (`foreground`, borrowed) 3.64, fixed with an earlier pair 5.60; Bottom panel, secondary text (`descriptionForeground`, borrowed) 2.20, fixed with an earlier pair 5.41; Column and section headers (`sideBarSectionHeader.foreground`, borrowed) 3.57, #4B5E63 4.53; Group headers (`foreground`, borrowed) 2.96, #4D5D63 4.56; Settings page text (`foreground`, borrowed) 4.13, fixed with an earlier pair 6.36; Settings page, secondary text (`descriptionForeground`, borrowed) 2.50, fixed with an earlier pair 6.15; Settings scope tab, active (`panelTitle.activeForeground`, borrowed) 4.13, #5F747B 4.56; Settings scope tab, inactive (`panelTitle.inactiveForeground`, borrowed) 2.50, #6A7373 4.51; Error message (`errorForeground`, base) 4.39, #E01400 4.56; Status bar (`statusBar.foreground`, theme) 4.39, #566C73 4.52; Status bar, debugging (`statusBar.debuggingForeground`, borrowed) 4.39, #566C73 4.52; Status bar, no folder (`statusBar.noFolderForeground`, borrowed) 4.39, #566C73 4.52; List selection (`list.activeSelectionForeground`, theme) 3.24, #565656 4.52; List selection, unfocused (`list.inactiveSelectionForeground`, borrowed) 2.75, #49595E 4.50; Quick pick (`quickInput.foreground`, borrowed) 3.64, #586B72 4.56; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 3.84, #616161 4.53; Input box (`input.foreground`, theme) 3.71, #4D6066 4.55; Input placeholder (`input.placeholderForeground`, theme) 2.25, #565E5C 4.59; Button (`button.foreground`, base) 2.72, background #817641 4.56; Secondary button (`button.secondaryForeground`, borrowed) 4.39, #566C73 4.52; Badge (`badge.foreground`, base) 2.28, background #887330 4.62; Toggle chip, on (Error List) (`foreground`, borrowed) 2.98, fixed with an earlier pair 4.59; Notification (`notifications.foreground`, borrowed) 3.64, #586B72 4.56; Hover and tooltip (`editorHoverWidget.foreground`, borrowed) 2.56, #455459 4.54; Completion list (`editorSuggestWidget.foreground`, borrowed) 3.64, #586B72 4.56; Editor widget (find, peek) (`editorWidget.foreground`, borrowed) 3.64, #586B72 4.56 |
| Tomorrow Night Blue | dark | VS Code built-in | 50 / 86 / 95 | Error message (`errorForeground`, theme) 2.18, #C9738C 4.61 |
| Atom One Dark | dark | TextMateSharp | 55 / 77 / 99 | Quick Launch box (`descriptionForeground`, borrowed) 3.01, #A6ABB3 4.52; Menu shortcut (`descriptionForeground`, borrowed) 3.01, fixed with an earlier pair 4.52; Document tab, inactive (`tab.inactiveForeground`, theme) 2.55, #868C95 4.54; Tool window, secondary text (`descriptionForeground`, borrowed) 4.44, fixed with an earlier pair 6.67; Bottom panel, secondary text (`descriptionForeground`, borrowed) 4.44, fixed with an earlier pair 6.67; Settings page, secondary text (`descriptionForeground`, borrowed) 4.03, fixed with an earlier pair 6.06; Settings scope tab, inactive (`panelTitle.inactiveForeground`, borrowed) 4.03, #8E939D 4.54; Input placeholder (`input.placeholderForeground`, borrowed) 3.01, #A6ABB3 4.52; Badge (`badge.foreground`, borrowed) 3.23, background #4473D4 4.52 |
| Atom One Light | light | TextMateSharp | 55 / 77 / 99 | Document tab, inactive (`tab.inactiveForeground`, theme) 2.29, #6D6D6D 4.54; Tool window, secondary text (`descriptionForeground`, borrowed) 4.29, #6D6F74 4.61; Bottom panel, secondary text (`descriptionForeground`, borrowed) 4.29, fixed with an earlier pair 4.61; Settings page, secondary text (`descriptionForeground`, borrowed) 4.48, fixed with an earlier pair 4.82; Settings scope tab, inactive (`panelTitle.inactiveForeground`, borrowed) 4.48, #717378 4.55; Badge (`badge.foreground`, borrowed) 4.05, background #3C71E3 4.50 |
| One Dark | dark | TextMateSharp | 55 / 77 / 99 | Quick Launch box (`descriptionForeground`, borrowed) 3.01, #A6ABB3 4.52; Menu shortcut (`descriptionForeground`, borrowed) 3.01, fixed with an earlier pair 4.52; Document tab, inactive (`tab.inactiveForeground`, theme) 2.55, #868C95 4.54; Tool window, secondary text (`descriptionForeground`, borrowed) 4.44, fixed with an earlier pair 6.67; Bottom panel, secondary text (`descriptionForeground`, borrowed) 4.44, fixed with an earlier pair 6.67; Settings page, secondary text (`descriptionForeground`, borrowed) 4.03, fixed with an earlier pair 6.06; Settings scope tab, inactive (`panelTitle.inactiveForeground`, borrowed) 4.03, #8E939D 4.54; Input placeholder (`input.placeholderForeground`, borrowed) 3.01, #A6ABB3 4.52; Badge (`badge.foreground`, borrowed) 3.23, background #4473D4 4.52 |
| Dracula | dark | TextMateSharp | 55 / 77 / 99 | Document tab, inactive (`tab.inactiveForeground`, theme) 3.03, #8390B7 4.50 |
| Visual Studio Dark (TextMateSharp) | dark | TextMateSharp | 55 / 77 / 99 | Menu, highlighted item (`menu.selectionForeground`, borrowed) 2.94, background #2877C7 4.61; List selection (`list.activeSelectionForeground`, theme) 2.94, background #2877C7 4.61; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 2.94, background #2877C7 4.61 |
| Visual Studio Light (TextMateSharp) | light | TextMateSharp | 55 / 77 / 99 | Menu, highlighted item (`menu.selectionForeground`, borrowed) 2.94, background #2877C7 4.61; Document tab, inactive (`tab.inactiveForeground`, theme) 4.17, #707070 4.54; List selection (`list.activeSelectionForeground`, theme) 2.94, background #2877C7 4.61; Quick pick, focused row (`quickInputList.focusForeground`, borrowed) 2.94, background #2877C7 4.61 |
<!-- THEME-CONTRAST-END -->

**Color Theme dropdown** (`ThemePicker`, a `ComboBox`): only at the top of the Settings page (*Environment › Appearance*, where Visual Studio has it
under Tools > Options > Environment > General; Tools > Options opens it). The main toolbar had one too until the user asked for it to go (wave 3,
2026-10-29: "only have it in the settings menu"): the toolbar, View > Toolbars and the setting `cscode.toolbars.colorTheme` no longer have it (an old
`settings.json` entry for that key is ignored); `ThemeDropdownTests` asserts the main window has no theme dropdown. Every installed theme, grouped Dark / Light / High contrast (sorted by label), each with a swatch: the
editor background with three token colours (keyword, string, function). Arrowing through the open list previews live through `IColorThemePreview`
(nothing is persisted; at most one preview per `ThemePreviewer.MinInterval`, 250 ms, latest wins, so holding an arrow key does not repaint the window
30 times a second); Escape or clicking away reverts; Enter or a click applies and writes `workbench.colorTheme` (the label). Arrow keys on the closed,
focused box apply directly, as Visual Studio's combos do; group headers are neither focusable nor selectable. The dropdown follows every other way of
changing the theme (the palette's Color Theme command, a `settings.json` edit, another window) through `IColorThemeService.Changed`.
The list is rebuilt only when the set of themes changes, never per switch. The palette command *Color Theme* (`workbench.action.selectTheme`) uses
the same grouping, the same throttled preview and the same commit (`ThemeSelection`).

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
tool windows have caption bars with the Dock dropdown (Dock / Float / Auto Hide / Hide), pin and close. Closing a tool window (its X or its tab's Close) **hides** it, like
Visual Studio (`ShellDockFactory.HideToolsOnClose`): its view is disposed and its command, key, View menu entry or Reset Window Layout shows it again where it was. A tool that
Dock removes from the layout some other way gets the same treatment (`ToolWindowManager.Restore` docks it in its last, else its default, well). Documents are still removed on close.

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

**Editor** (37)

`editor.background`, `editor.foreground`, `editor.selectionBackground`, `editor.inactiveSelectionBackground`, `editor.lineHighlightBackground`, `editor.lineHighlightBorder`, `editor.findMatchBackground`, `editor.findMatchHighlightBackground`, `editor.wordHighlightBackground`, `editor.stackFrameHighlightBackground`, `editor.focusedStackFrameHighlightBackground`, `editorCursor.foreground`, `editorLineNumber.foreground`, `editorLineNumber.activeForeground`, `editorIndentGuide.background`, `editorWhitespace.foreground`, `editorBracketMatch.background`, `editorBracketMatch.border`, `editorGutter.background`, `editorError.foreground`, `editorWarning.foreground`, `editorInfo.foreground`, `editorHint.foreground`, `editorWidget.background`, `editorWidget.foreground`, `editorWidget.border`, `editorSuggestWidget.background`, `editorSuggestWidget.border`, `editorSuggestWidget.foreground`, `editorSuggestWidget.selectedBackground`, `editorSuggestWidget.highlightForeground`, `editorHoverWidget.background`, `editorHoverWidget.foreground`, `editorHoverWidget.border`, `editorOverviewRuler.border`, `diffEditor.insertedTextBackground`, `diffEditor.removedTextBackground`

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
Contributed items (`IContributionRegistry.Of<MenuContribution>()`) feed the right-click menus (see *Right-click menus* below: `editor/context`,
`editor/title/context`, `explorer/context`, ...); `ContributedMenus.Get` still serves `commandPalette` and `editor/title`.

**Keyboard.** A tunnelling window handler maps key events to `KeyStroke` (VS Code names) and asks `IKeybindingService.Resolve`: matched binding runs the command asynchronously (never blocks the key),
a chord prefix is swallowed and the status bar shows "(Ctrl+K) was pressed. Waiting for second key of chord...", unbound keys pass through to the focused control (typing is untouched),
Esc or a 5 s timeout abandons a chord. **Vim comes first in editors**: the window's tunnelling `KeyDown` and `TextInput` handlers offer every event
to the `IShellKeyInterceptor` (the Vim adapter, `src/CsCode.App/Vim`, docs/VIM.md "Integration") before the pipeline; it takes only events of a
focused editor with Vim on, never the second key of a pending chord, and decides key presses with `VimKeyPolicy` (Vim owns its Ctrl keys and Esc in
Normal/Visual mode; `Ctrl+Shift+…`, `Alt+…`, F-keys, `Ctrl+S` and the other application keys keep working; `vim.handleKeys` overrides per key).
What it passes through reaches the pipeline and the editor as before. A binding whose command **no module registered** (`ICommandService.Has`) counts as absent: the presets bind ids of modules that may not be composed
(F3 `editor.action.nextMatchFindAction`, Ctrl+] `jumpToBracket`, Alt+F12 `peekDefinition`, Ctrl+\` terminal...), so a registered lower-precedence binding of the same keys runs instead, else the key
reaches the focused control (F3 gets to the editor's own find); a chord prefix whose every continuation is unregistered is not a chord. Context keys (`ContextKeyTracker`): `editorTextFocus`, `editorHasSelection`, `editorReadonly`, `editorLangId`, `resourceExtname`, `resourceFilename`,
`resourceScheme`, `explorerViewletFocus`, `listFocus`, `panelFocus`, `inputFocus`, `terminalFocus`, `inDebugMode`, `debugState`, `isMac/isLinux/isWindows`, `workspaceFolderCount`, `hasOpenEditors`,
`activeEditorIsDirty`, `quickInputVisible`. Focus areas come from the `ShellFocus.Area` attached property on the tabs' view hosts (a focused
`TerminalControl` is the terminal area; its window's find box and buttons are the panel). The main window follows `GotFocus` with handled events
too (AvaloniaEdit marks its text area's GotFocus handled, so the editor taking focus used to go unseen and the area stayed stale, T7) and writes
every focus key on each focus change, after the control's own handler: it agrees with what the terminal sets for itself (T22), so the keys are
right synchronously, without a dispatcher turn (`FocusContextKeyTests`). Floating windows get the same handlers (see *Floating windows*).

**Documents.** `DocumentWell` adds to the Dock model (the single source of truth for tabs, order and groups): open / preview (italic, replaced by the next preview, promoted by editing or reopening) /
pin / close / close others / saved / all / to the right / reopen closed (last 20), Save/Don't Save/Cancel prompts (also when the user clicks Dock's close button: Dock's close is cancelled and routed through the prompt),
split right/down, move between groups, MRU order and the **Ctrl+Tab switcher** overlay. Only the active tab of each group plus the last `cscode.shell.liveEditorViews` (default 8) tabs keep a live editor view;
others follow the editor's contract (docs/EDITOR.md): `GetViewState()` (caret, selection, scroll offsets, folds) -> `IEditorViewFactory.DisposeView(view)`, and on the next
presentation `CreateView` + `RestoreViewState`. Closed tabs' views go back through `DisposeView` too. Dock presents a thin `Border` host per tab (see *View lifecycle and Dock* below).

**View lifecycle and Dock.** Two things kept destroyed views reachable, and the shell handles both:
* Dock.Avalonia 12.1 caches the control it presented for every dockable (control recycling on the `DockControl`, keyed by the dockable), and a host
  emptied while out of the window still lists the removed view in its composition visual (that list is synchronised only when the host is rendered).
  The shell's tabs (`ShellDocument`, `ToolTab`) are their own content templates (`IRecyclingDataTemplate`), which Dock's content presenter asks
  before its cache, so the cache never holds a shell tab (`RealAppDockCacheTests`); a tab whose host Dock no longer shows still drops the host (a
  fresh one is built on the next presentation) and removes any cache entry it finds. This was the "closed tabs stay reachable" retention noted
  earlier: closed tab objects, their documents and views are all released (the live 300-tab test asserts every sample, not just the steady state).
* **No data template for the shell's tabs** (`ShellDockContent`, T14): the shell used to register `FuncDataTemplate<EditorTab>` (and for documents
  and tools) on the DockControl. The tab strip's close-button presenters (`PART_ClosePresenter`, `PART_CompactClosePresenter`), whose content is
  the dockable too, fall back to the tree's data templates while Dock re-templates a moved tab, took an inactive tab's shared host, and Window >
  Split Editor Right / Down threw "The control Border already has a visual parent". Now the DockControl's first data template turns the shell's tab
  types into an empty placeholder for any presenter that falls back to the tree; only Dock's content presenter gets a tab's host
  (`SplitEditorTests`, and Vim's `:vsp` / Ctrl-W test is no longer skipped).
* A focused editor stays reachable from Avalonia's focus machinery and from AvaloniaEdit's static "last focused element" (`RoutedCommand`) until another
  element takes focus; detaching it is not enough. Before Dock removes a tab whose view holds keyboard focus, the well clears focus (the window is
  not focusable), and once the document that takes its place is presented its editor gets the keyboard, as in Visual Studio. When the last document
  closes nothing takes focus, so AvaloniaEdit's static slot keeps that one view until focus next moves anywhere (bounded: one view).

**Floating windows** (wave 3). Float in a document tab's or a tool window's menu (and Window > Float Editor, `workbench.action.moveEditorToNewWindow`)
moves it into a native window of its own: Dock.Avalonia's `HostWindow`, owned by the main window (it stays above it and out of the taskbar), with a
layout root of its own (`IRootDock.Windows`). The factory creates the host windows (`ShellDockFactory.HostWindowCreated`), and the main window gives
each one the shell's Dock styles and the same window-level handlers it has itself: Vim and the keyboard pipeline (every key binding works in a
floating window), the focus context keys, the right-click menus and the Edit menu's clipboard routing (which follows the window that last took
focus). A floating document is still one of the well's tabs (`DocumentWell.Tabs` covers the floating groups: Save, Ctrl+Tab, the live-view budget,
close prompts, the session all see it). Dock (tool) / Dock as Tabbed Document (document; `workbench.action.restoreEditorsToMainWindow` docks them
all) returns it to the well or group it came from (else its default well, or the primary group). A floating window closes once it is empty (its
last tab closed, docked or hidden). Its own close button (Alt+F4) empties it the shell's way: tools are hidden, documents closed with their Save
prompts (Cancel keeps the window). A tool window hidden while it floats goes back to its well hidden, and its window's place is remembered: showing
it again (its command, View menu, Reset excepted) floats it there again, as in Visual Studio. The floating windows hide and show with the main
window (background mode hides the main window instead of closing it), and when the main window really closes, everything floating is docked back
after the layout was saved. Reset Window Layout docks everything. Not done: dragging a tab out of the window to float it relies on Dock's own drag
(not tested), and a floating window holds either tools or documents (Dock gives a floating root one kind of dock), never both.

**Layout.** `LayoutPersistence`: tool placement, visibility, splitter proportions, open files with pin/preview, active file, floating windows (their
place and the tools and documents in them, and where hidden floating tools float again: `LayoutSnapshot.Floating` / `FloatingHidden`, still
version 1; a background session restoring into a hidden window floats them when the window is shown); versioned JSON, workspace scope first then global default,
tolerant of corrupt/future data; saved (debounced) on change and on exit; `View > Appearance > Reset Window Layout`. There is no left tool well unless a tool window asks for one.
The default (and reset) layout shows exactly `ShellController.DefaultLayoutTools`: Explorer (right), Output and Error List (bottom). Every other tool window, whichever module contributes it
(Find Results, Find in Files, Modules...), starts hidden unless its `AutoShowWhen` holds, and a tool that a saved layout does not mention (its module was added later) gets that default too.
`ToolWindowLocation.Document` windows (Settings, Keyboard Shortcuts) never enter a tool well: they open on demand as singleton document tabs (through `IViewActivator`) and are not restored at startup.

**Palette / Quick Launch** (`PaletteSource`; the title bar box reads "Search (Ctrl+Q)", with the palette's real shortcut, and the workspace name stands beside it as Visual Studio's solution name does): plain = go to file (fuzzy over a trimmable, bounded workspace index, ranked on a worker, top 100; a failed index build is not cached, the next query builds again), `>` **every** command, as VS Code (title, category, keybinding badge, recent first, `Enablement` honoured; no row cap: the 100-row cap hid 'Preferences:' and 'Search:' commands behind the macro ones; the list is a virtualised `ListBox`, 17 of 176 rows realised in the real app; ranked on the UI thread up to `PaletteSource.SyncCommandLimit` = 1,000 commands, on a worker beyond),
`:` go to line[:column], `@` document symbols, `#` workspace symbols, `session:` saved sessions (background mode, docs/SESSIONS.md: the other saved
sessions, most recently used first, fuzzy over the name and the folder's name, read off the UI thread; Enter opens one through File > Sessions >
Open Session with its name; a `--foreground` window says sessions need background mode), `?` help. The **Welcome page** lists the same saved
sessions under *Sessions* (eight, then *More sessions...*, the Open Session picker; `SessionCatalog`). Cheap modes answer synchronously (first paint needs no dispatcher turn); stale async results are dropped. Also drives `ShowQuickPick`/`ShowInputBox`.
Theme picker (`workbench.action.selectTheme`) lists the themes grouped Dark / Light / High contrast, previews live while arrowing (throttled, nothing persisted) and reverts on Esc.

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
Other modules (Vim mode, remote indicator) register their own through `IStatusBarService`; text may use `$(icon-name)`. The Vim adapter's items:
`vim.mode` (`-- INSERT --`, `recording @q`), `vim.cmdline` (the `:` `/` `?` command line with its cursor, or Vim's last message), `vim.showcmd`
(pending keys), all leftmost, and `vim.toggle` on the right (*Vim* / *Vim: Off*, runs Toggle Vim Mode); all but the toggle disappear when Vim is off.

### Commands registered by the shell

`workbench.action.showCommands`, `quickOpen`, `gotoLine`, `gotoSymbol`, `showAllSymbols`, `files.openFolder`, `files.openFile`, `files.newUntitledFile`, `files.save`, `files.saveAs`, `files.saveAll`,
`closeActiveEditor`, `closeAllEditors`, `closeOtherEditors`, `reopenClosedEditor`, `nextEditor`, `previousEditor`, `pinEditor`, `splitEditorRight`, `splitEditorDown`, `moveEditorToNextGroup`, `moveEditorToNewWindow` (float), `restoreEditorsToMainWindow` (dock floating documents), `closeFolder`, `quit`, `reloadWindow`
(refreshes menus only), `toggleFullScreen`, `toggleZenMode`, `selectTheme`, `workbench.view.explorer`, `workbench.action.output.toggleOutput`, `workbench.actions.view.problems`,
`workbench.action.findResults.toggle`, the toggle of every tool window whose module did not register its `ToggleCommandId` (debug windows: `workbench.debug.action.toggle*`), `cscode.references.show`,
`cscode.shell.{resetLayout,openRecent,about,showWelcome,showKeyboardReference,toggleNotifications,changeLanguageMode,showErrorList,toggleToolbar}`
(`toggleToolbar` takes a toolbar group id: `standard`, `debug`; View > Toolbars). `gotoLine` also takes an optional `"line[:column]"` argument.
`openSettings` / `openSettingsJson` are only fallbacks (they open settings.json) for a composition without the Settings panel.
Clipboard and menus (`ShellContextMenus`, `ClipboardRouter`): `editor.action.clipboardCutAction` / `clipboardCopyAction` / `clipboardPasteAction`
(focus routing, see *Command ownership*), and VS Code's file commands, which take a URI (else the active file): `copyFilePath`, `copyRelativeFilePath`,
`revealFileInOS` (Open Containing Folder / Reveal in File Manager), `revealInExplorer`.
Everything else in the menus (`editor.action.*`, `debug.*`, terminal, tasks) is owned by other modules and stays disabled until they register it.

### Command ownership

The command table is a stack per id (the newest registration runs), so ownership is decided by registration order, and `ShellController.Initialize` fixes that order: the editor
factory (resolved with the document well) registers first, then Dap (`IDebugService`), then the panels (their hosts are built while the tool windows are enumerated), and the shell last,
skipping ids a panel already owns. Ids registered by more than one module, and who wins (asserted over the real composition by `RealAppCommandOwnershipTests`):

| Id | Registered by | Effective owner | Why |
|---|---|---|---|
| `workbench.action.gotoLine` | Editor (input box, standalone fallback), shell | shell | VS Code behaviour (palette in `:` mode); the shell accepts the editor's `"line[:column]"` argument |
| `editor.debug.action.toggleBreakpoint` | Editor (standalone fallback), Dap | Dap | DAP owns the debug commands; both toggle at the active editor's caret |
| `workbench.action.openSettings`, `openSettingsJson` | Settings panel (shell only when no panel is composed) | Settings panel | Settings editor (optional query argument) and settings.json (created when missing) |
| `editor.action.clipboardCutAction`, `clipboardCopyAction`, `clipboardPasteAction` | Editor (standalone), shell (`ClipboardRouter`) | shell | Edit > Cut/Copy/Paste and the keys act on what has keyboard focus: the editor (its Visual Studio behaviours, through `IEditorViewController`), a text box, or a view's own selection (`IClipboardTarget`) |

Every other id has exactly one registration; the test fails when a new duplicate appears, so it needs a decision here.

### Clipboard (`Services/ClipboardService.cs`, `Services/X11/`, `Input/ClipboardRouter.cs`)

* **One service** (`IClipboardService`, Core): every copy in CsCode (editor, panels, Explorer paths, status items) goes through it. Reads and writes
  start on the UI thread and are awaited off it; a read is bounded by `ClipboardService.ReadTimeout` (2 s) and an owner that never answers costs
  an empty paste, never a frozen window. CsCode remembers the shape of its last copy (whole lines, block, one piece per caret, the source line's
  indentation) and hands it back only while the clipboard still holds exactly that text: text another application put there reads as plain
  text. Copies always go to the LOCAL clipboard, also for documents of a remote workspace (the service is process-local; nothing goes through
  `IFileSystem`). HTML (copy with formatting) is offered as `text/html` next to the text on Linux when a caller provides it:
  the editor's **Copy with Formatting** (Edit menu, the editor's right-click menu, `editor.action.clipboardCopyWithSyntaxHighlightingAction`,
  enabled through `cscode.edit.editorCanCopy` while an editor that can copy has focus) writes the selection with the theme's syntax colours
  (docs/EDITOR.md §7a); the ring keeps only the text.
* **Clipboard ring** (Visual Studio's Ctrl+Shift+V, Edit > Cycle Clipboard Ring): the last 20 copies, newest first, each at most 1 MB of
  managed memory (512 K characters; a bigger copy still goes to the system clipboard, it is only left out of the ring), a repeated copy moves
  to the top; in memory only, never persisted (the service has no state storage).
* **PRIMARY** (Linux): `X11PrimarySelection` over `X11SelectionClient`, an XCB client of its own (no xclip, xsel or wl-clipboard): its own
  connection, an unmapped input-only window, one event thread that sleeps in `poll` and answers `SelectionRequest`s (TARGETS, TIMESTAMP,
  UTF8_STRING, STRING, TEXT, text/plain; **INCR** above 256 KB), handles `SelectionClear`, and reads other owners' text (INCR included). XCB,
  not Xlib: it is thread safe and reports protocol errors as events, where Xlib has one process-wide error handler (Avalonia's) whose default
  exits the process. Callers only enqueue work and wake the thread through a pipe, so the UI thread never waits on the X server. The editor
  offers its selection lazily (a provider over an immutable document snapshot, run on the X thread only when someone pastes). Under KDE Plasma
  on Wayland CsCode runs through XWayland and KWin bridges X11 PRIMARY to the Wayland primary selection; a native Wayland backend (Avalonia 12
  has none) would need `zwp_primary_selection`. Without `$DISPLAY` there is no PRIMARY (`IsPrimarySelectionSupported` false).
* **Edit menu routing** (`ClipboardRouter`): Cut / Copy / Paste act on what has keyboard focus, as in Visual Studio: the editor, a text box (its
  own Cut/Copy/Paste), or a view that copies its selection (`CsCode.Ui.Menus.IClipboardTarget`: Error List, Output, Find Results, Explorer files,
  Call Stack, Locals/Autos/Watch, Debug Console, Find in Files). Focus moving into a menu or popup does not count, so Edit > Copy and a context
  menu's Copy act on what had focus before. The Edit menu items are gated on `cscode.edit.canCut/canCopy/canPaste`, computed right before the
  menus refresh (`MenuModel.Refreshing`). The router follows focus with `handledEventsToo`: AvaloniaEdit marks the text area's GotFocus handled.
* **Keys**: the editor contributes Ctrl+X / Shift+Delete, Ctrl+C / Ctrl+Insert, Ctrl+V / Shift+Insert, Ctrl+Shift+V (`editorTextFocus`)
  as built-in keybindings (so menus show them and keybindings.json can rebind them); text boxes use Avalonia's own (the same keys); list views
  handle Ctrl+C and Ctrl+Insert themselves (`ClipboardKeys`).

### Right-click menus (`CsCode.Ui/Menus`, `Menus/ShellContextMenus.cs`)

* **One model**: `MenuEntry` lists, built when a menu opens and dropped when it closes, rendered by `ContextMenus` the same way everywhere:
  the theme's menu colours (Fluent retargeted at the workbench palette, high contrast included), Codicon icons, a right-aligned shortcut column
  from `IKeybindingService.GetLabel`, access keys, screen-reader names and accelerator keys (`AutomationProperties`), keyboard navigation
  (Avalonia's). Other modules add items through `IContributionRegistry` (`MenuContribution` with `when`, `group@order`, VS wording in `Title`,
  `Icon`, `Submenu` + `SubmenuContribution`, `Args`, `EnablementCheck` for asynchronous enablement; `MenuContributions.Resolve`): a `when`
  that fails or an unregistered command hides an item, the command's enablement disables it. Menu ids (`MenuIds`): `editor/context`,
  `editor/title/context`, `explorer/context`, `problems/context`, `output/context`, `search/context`, `view/title/context`, `statusBar/context`.
* **Editor text area**: built entirely from `editor/context` contributions (the editor module contributes Visual Studio 2022's items; the shell
  hard-codes none), at the pointer or, from the keyboard (the Menu key through Avalonia's context request; Shift+F10, which Avalonia does not map, through the editor; or `editor.action.showContextMenu`),
  below the caret. A window-level tunnelling handler owns the request unless a control on the way (the find box) has a menu of its own.
* **Document tabs** (replacing Dock's): Save *file* (when dirty), Close, Close All But This, Close All, Close To The Right, Pin / Unpin Tab,
  Copy Full Path, Open Containing Folder, Reveal in Explorer (files in an open folder), Float (or Dock as Tabbed Document when it floats), New
  Horizontal / Vertical Document Group, then `editor/title/context` items.
* **Tool windows** (tool tabs, auto-hide strip tabs, title bars; replacing Dock's): Float, Dock (when floating or auto-hidden), Auto Hide, Close,
  then `view/title/context`. See *Floating windows*.
* **Explorer**: New File / New Folder (folders), Reveal in File Manager, Open in Terminal (only when a terminal registers
  `workbench.action.terminal.newHere`), Find in Folder (folders), Cut / Copy / Paste (files, like Solution Explorer), Copy Path / Copy Relative
  Path, Rename (F2), Duplicate, Delete (Del), then `explorer/context`. Keys: Ctrl+C/X/V, Ctrl+Insert, Shift+Insert, Shift+Delete on files.
* **Error List** rows: Copy (severity, code, description, file, line; tab separated, as Visual Studio copies), Go To Location (Enter), Show Error
  Help (F1; the diagnostic's `codeDescription` link, disabled without one), then `problems/context`. **Output**: Copy (selected lines; the list
  takes Shift/Ctrl+click selections), Clear All, Word Wrap (check). **Find Results**: Go To Location, Copy (`path(line,column): text`), Copy Full
  Path, Clear All. **Status bar** items: the item's command, Copy (its text without icons), then `statusBar/context`.
* Debug panels (audited): Locals/Autos/Watch have Copy Value, Copy Name (new), Copy Expression; Call Stack has Copy (the selected frame, new) and
  Copy Call Stack; the Debug Console output has Copy / Copy All / Clear All (new); Breakpoints and Threads keep their menus; Modules has none.
  Search panels: Find in Files rows have Copy (new), Copy Path, Copy All (new); Settings' gear menu has Copy Setting ID. Text boxes keep
  Fluent's Cut / Copy / Paste flyout.
* **Budgets**: building a menu (resolving contributions, creating the items) takes 2-5 ms here; Avalonia's own `ContextMenu.Open` dominates
  (headless Skia on this shared machine, load average 17-24: 40-100 ms for any 16-item menu, the same for a plain Avalonia menu without CsCode's
  items). `RealAppContextMenuTests.Menus_open...` asserts the best of three runs of 100 openings stays under 50 ms and that 100 open/close cycles
  retain nothing; it passed alone and failed once under full load (see *Known gaps*). No item does I/O or a language-server round trip while
  the menu opens: Rename's prepareRename and Paste's clipboard query are `EnablementCheck`s (the item opens disabled and enables when they answer).

### Settings

`cscode.shell.liveEditorViews` (8), `cscode.shell.confirmMultiFileEdits` (true), `cscode.diagnostics.uiStallLogMs` (0; Debug builds default to 100),
`cscode.toolbars.standard`, `cscode.toolbars.debug` (true: the toolbar groups View > Toolbars shows; the setting is the
state, so settings.json and other windows agree), plus the ones it reads:
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

**Theme previews** (`ThemeSwitchingCostTests`, the real composition with an editor and the Settings page's dropdown open; preview requests at key-repeat rate,
about 30 a second, through all 24 themes): p99 14.7 ms, max 18.1 ms, longest UI callback 8.2 ms (the Fluent palette step; brushes < 0.1 ms), 11
theme applies in 2.5 s (throttled); 100 switches through the closed dropdown leave the managed heap flat (+52 KB) and never rebuild its list. What
these numbers do **not** include: frame rendering. The headless platform renders only when a test forces a frame, and then on the test's own thread
with software Skia (a full-window repaint after a theme change takes ~100 ms that way under load); on a desktop the rasterisation runs on Avalonia's
render thread, but recording the re-resolved controls' draw operations is UI-thread work that no test here measures (there is no display for
tests). Nor the open list's own arrow handling: a plain 27-item Avalonia `ComboBox` takes 3-25 ms per arrow press headless, rendering included.

Fixes that this suite drove: theme application 30-50 ms to < 8 ms (idempotent resource publication), status bar rebuild made incremental, diagnostics counting moved off the UI thread,
Output attach limited to the recent tail, `PostCoalesced` + version race in `ThemeApplier` (a stale preparation could overwrite the newest one).
Other checks: service calls from the UI thread return without waiting for the work; shell async flows run under a `SynchronizationContext` whose `Send` throws (no sync-over-async dispatch).

## Memory

Bounded: Output 2 MB per channel, 20k lines in the view; notification history 100; closed-tab history 20; palette index capped at 300k files and an `IMemoryTrimmable`; icon geometry cache trimmable; Error List at most one immutable array.
Lifetimes: every view and model owns a `DisposableStore`; tool-window views and their view-models are disposed when hidden; tab views by the LRU policy and on close. Tests: 100x show/hide of every tool window balances creation and disposal and retains nothing;
500 palette opens retain no entries; 500 dialogs leave the heap flat; 200 theme switches leave the resource table and brush set unchanged; 100 switches
through the theme dropdown on the real composition leave the managed heap flat (< 512 KB, measured +52 KB) and keep the dropdown's list; a closed
Settings page releases its dropdown; 300 tabs opened/closed in the live Dock host release every sampled tab, document and view
(managed heap flat between tab 100 and 300); a focused real editor's view is released when its tab closes, and an evicted view is released while its tab stays open; output flood and status updates use a bounded number of dispatcher posts.

## Running it

```bash
dotnet build CsCode.slnx
dotnet test tests/CsCode.App.Tests            # on a Nix dotnet: source tools/nix-dev-env.sh first (SkiaSharp needs the libs)
```

* **Dev host** (`tests/CsCode.App.Tests/DevHost`): composes the **real** shell (`AppServices.Build` + `MainWindow`) over in-memory fakes for the modules that are written concurrently
  (command service, context keys with a real `when` evaluator, configuration, keybindings with chord state, workspace + file system + watcher, state storage, colour themes with a fallback chain, editor factory, debug service).
  `DevScene.BuildAsync` seeds a workspace, tabs, diagnostics, output and a debug state. `host.Capture("name")` writes `.scratch/screenshots/name.png` (git-ignored).
* **Clipboard and menus**: `ClipboardServiceTests` (headless platform clipboard, a silent owner, the ring), `RealApp/ContextMenuTests` (every
  menu over the real composition, the Edit menu's routing, budgets, screenshots `context-menu-editor.png`, `context-menu-tab.png`),
  `X11SelectionTests` (PRIMARY between two X clients on a **private Xvfb** `:100`-`:199` started by the test, `-nolisten tcp`, stopped by its
  PID; binary from `CSCODE_XVFB`, else the Nix store path, else PATH; skipped without one) and `RealWindowClipboardTests` (the real app in a
  child process on the private Xvfb with Avalonia's X11 backend; this process is the second X client: CLIPBOARD and PRIMARY both ways, Unicode,
  CRLF, 2 MB through INCR; `clipboard-real-window.png`). No xclip/xsel is installed here, so the second client is `X11SelectionClient` on its own
  connection (Avalonia's X11 clipboard is the independent implementation on the other side).
* **Theme screenshots**: `RealApp/ThemeScreenshotTests` photographs the real main window (a folder and a C# file open, TextMate highlighting) in every
  installed theme (`.scratch/screenshots/theme-<id>.png`, 24 of them), the open dropdown in Dark and Solarized Light and the Settings page in Dark,
  Monokai and Light; `RealApp/ThemeIconTests` writes `theme-icons-dark-plus.png` and checks the coloured icons are drawn.
* **Screenshots**: `ScreenshotTests` renders Dark, Blue and Light, the palette, an open File menu, the debugging (orange) state, toasts and the modal dialog; `RealWindowSmokeTests` runs the
  real app on the X server (`DISPLAY`) in a child process, photographs the live window and checks for clean exit and no crash log.
* A production start (`dotnet run --project src/CsCode.App`) needs the other modules' registrations; until they are merged it ends in the crash window that names the missing service (by design).

What the screenshots were checked for: custom title bar with the nine menus and a centred Quick Launch box, standard toolbar (back/forward, new/open/save/save-all, undo/redo, launch-config combo, Start, find),
document tabs (active top accent bar, italic preview tab, dirty marker), Explorer docked right with VS file icons and chevrons, Error List with severity chips and counts, bottom tab strip, status bar blue / orange (debugging, with step buttons in the toolbar) / purple (no folder),
File menu with icon, shortcut and separator columns, palette with fuzzy list and keybinding badges, toasts with severity icons and action buttons, modal Save/Don't Save/Cancel dialog; identical structure in all three themes.

## Known gaps and issues (honest list)

Not done: single-instance forwarding (SHOULD); native menu bar (`NativeMenu` on macOS / DBus global menu); Explorer drag-and-drop **UI** (the model's `MoveAsync` is tested; the view has no drag source/drop target) and dropping files from the OS onto the window;
dirty marker is Dock's `*` rather than a dot that turns into a close glyph on hover; pinned tabs have model/commands but no pin glyph; layout restore reopens tabs but does not recreate split document groups (floating windows it does recreate);
auto-hide/peek animations, taskbar jump list, `--diff`, toolbar overflow (all SHOULD; high contrast comes from VS Code's two high-contrast themes, completed from the shell's high-contrast bases); the Windows/macOS title-bar path (`ExtendClientAreaToDecorationsHint`) is written but untested here (Linux only).
`reloadWindow` only refreshes menus. Find in Files is the Search panel's tool window (`CsCode.Panels.Search`); Find Results hosts the references delivered by `cscode.references.show`. Explorer filter searches loaded nodes only.

Clipboard and menus, not done: a native Wayland primary selection
(Avalonia 12 runs through XWayland here); Peek Definition and QuickWatch's tree (Peek is not implemented by the editor, so the item stays hidden;
QuickWatch shows the value in a notification with Add Watch). Not verified headless: the Wayland <-> XWayland round trip with native KDE
applications (Kate, Konsole, Firefox) and screenshots on the real desktop are the integrator's check. The menu-open budget is met in quiet runs
only (above). (The stale focus area found while testing, the main window not seeing the editor take focus, is fixed: *Keyboard* above.)

Known issues: (1) **Combined hostile storm p99 is ~19 ms, not < 16 ms** (each source alone meets it; max is within 50 ms). (2) Dock.Avalonia 12.1.0.13 keeps every presented dockable in its control-recycling
cache (`ControlRecycling`, keyed by the dockable, never evicted); the shell's tabs stay out of it by being their own content templates (see *View lifecycle and Dock*), which depends on
Dock's `ControlRecyclingDataTemplate` asking an `IRecyclingDataTemplate` dockable first (true of 12.1.0.13; `RealAppDockCacheTests` fails if an upgrade changes it). The 300-tab live-host test asserts the steady state (heap flat, closed tabs unreachable).
(3) AvaloniaEdit's static `RoutedCommand` keeps the last element that took focus: after the last document closes, that one view stays reachable until focus moves anywhere (bounded).
(4) A theme switch's Fluent palette step is one dispatcher callback of 4-8 ms on a full window (over the 4 ms slice rule): assigning the palette is a
single Avalonia resource change that every control re-resolves, and it cannot be split; bursts run it once. (5) The contrast repair changes a theme's
own colours where they fail AA (listed per theme above), e.g. Solarized Light's chrome text is darkened; a user who wants the theme's exact colours
cannot switch the repair off yet.

## Integration notes

* **Needs registered** (resolved with `GetRequiredService`): `ICommandService`, `IContextKeyService`, `IConfigurationService`, `IKeybindingService`, `IWorkspaceService`, `IStateStorage`, `IColorThemeService`, `IEditorViewFactory`,
  and from Core `IDocumentManager`, `IDiagnosticService`, `IContributionRegistry`, `ILanguageFeatureRegistry`. **Optional**: `IDebugService` (toolbar combo, debug context keys), `ILanguageService`, `IMemoryMonitor`, `ILanguageServerManager`.
* Editor views must be a `Control`; the shell wraps them in a host, sets the focus-area marker on the host, and hands the view back through `IEditorViewFactory.DisposeView` when the tab
  closes or loses its live-view slot (after `GetViewState`, restored with `RestoreViewState` on re-creation).
* `IToolWindow`s registered by other modules (`AddSingleton<IToolWindow, ...>`) are placed by `DefaultLocation`; `AutoShowWhen` windows follow their context key; all of them start hidden (only
  `DefaultLayoutTools` are in the default layout) and are brought up by their command through `IViewActivator.ShowViewAsync(id)`; a Left-located tool creates the left well; `Document`-located
  windows open as document tabs.
* `tests/CsCode.App.Tests/RealApp/*` run the REAL composition (`AppServices.Build` with every module, `CSCODE_HOME` in a temp directory, all built-in language servers disabled;
  `Infrastructure/RealAppHost.cs`): view activation (also after a tool window was closed with its X, clicked), document windows, default layout, debug auto-show, command
  ownership (which module registered each duplicated id, in order), selection, Ln/Col, keys, wording, the Settings page's Color Theme dropdown and its absence from the main window (`ThemeDropdownTests`), theme
  icons (`ThemeIconTests`), switching cost (`ThemeSwitchingCostTests`) and screenshots (`ThemeScreenshotTests`).
* **Themes**: `ShellController.PrepareThemes` gives the editor's `ColorThemeService` the shell's completion (`AppStartup` calls it on the start-up pool
  thread, so parsing the 21 embedded themes and completing the current one never runs on the UI thread before the first frame; the other themes are
  completed on a pool thread right after). The Settings page gets `IColorThemeService` through `SearchServices.Themes` (optional; without it the
  Appearance section is hidden).
* The brief's `IExtensionService.ActivateByEventAsync("onStartupFinished")` is **not** used: the extension system was removed (commit "Drop the extension system").
* The removed system's remnants elsewhere (extension command ids, `ExtensionsDir`, the `ExtensionId` members, Core wording, the README tagline) were deleted at integration.
