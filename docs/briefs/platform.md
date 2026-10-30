# Module brief — Platform (`CsCode.Platform`)

**Owns:** `src/CsCode.Platform/**`, `tests/CsCode.Platform.Tests/**`, `docs/PLATFORM.md`, the *Platform* section of `Directory.Packages.props`.
**Implements (Core):** `ICommandService`, `IContextKeyService`, `IConfigurationService`, `IKeybindingService`, `IWorkspaceService`,
`IFileSystem`, `IFileWatcher`, `IStateStorage`, `IAppPaths`. Also a file-logging `ILoggerProvider`.
**Consumes:** `IContributionRegistry` (keybinding/configuration contributions), `IDocumentManager` (optional).
**Does not touch:** Avalonia. No UI.

This is the foundation every other module's runtime leans on, so correctness and test depth matter more than breadth.

## MUST
1. **`AppPaths : IAppPaths`** — per-OS dirs (Linux XDG: `~/.config/cscode`, `~/.local/share/cscode` (user themes/grammars/snippets live in `themes/`, `grammars/`, `snippets/` below it), cache, logs; Windows `%APPDATA%\CsCode`;
   macOS `~/Library/Application Support/CsCode`), `CSCODE_HOME` override (everything under it; used by tests/portable), `InstallDir = AppContext.BaseDirectory`,
   workspace storage dir = stable hash of the folder URI.
2. **`CommandService`** — thread-safe; stack semantics (later registration shadows; disposing a registration restores the previous handler);
   `ExecuteAsync` of unknown id → try `AddUnknownCommandResolver` resolvers in order, retry once, else `CommandNotFoundException`; `Registered` event; metadata table
   (`CommandMetadata`) for palette/menus; handler exceptions propagate to the caller with the command id attached; no re-entrancy deadlocks.
3. **`ContextKeyService` + when-clause engine** — full VS Code grammar: `!`, `&&`, `||` (precedence `!` > `&&` > `||`), parentheses, `==`/`===`, `!=`/`!==`,
   `<` `<=` `>` `>=`, `=~ /regex/flags`, `in`, `not in`, literals (`true`/`false`/quoted strings/numbers), bare key truthiness, values of any JSON-ish type,
   `config.<setting>` keys resolved live from `IConfigurationService` (inject optionally), child scopes via `CreateScope()` (overlay without mutating parent),
   change events with the set of changed keys, parse cache (bounded) so evaluating the same clause on every keystroke allocates nothing. Parser errors → clause is false + logged, never throws at evaluation.
4. **`ConfigurationService`** — layered: registered property defaults < user `settings.json` < workspace `.cscode/settings.json` (also read `.vscode/settings.json` as a lower-priority workspace source) < folder.
   JSONC (comments + trailing commas) with dotted keys and nested objects equivalent (`"editor.fontSize"` ≡ `{"editor":{"fontSize":…}}`); `Get<T>` with `CoreJson.Options`; section reads return merged objects;
   live reload via `IFileWatcher` (debounced), change events listing the exact affected keys (diff old/new merged values — don't fire for no-op rewrites);
   **`UpdateAsync` edits the settings file in place preserving comments, ordering and formatting** (implement a small JSONC-aware minimal-edit writer: find the key's value span and replace it, or insert a new
   member before the closing brace with matching indentation; create the file if absent; handle nested dotted keys; atomic write; ignore our own write echo when reloading). Register built-in defaults for every key in `SettingKeys`
   (VS-like defaults: font "Cascadia Code, Consolas, 'Courier New', monospace" / size 14 / tabSize 4 / insertSpaces true / wordWrap off / minimap true / formatOnSave false / autoSave off / memory budget 0 / trimOnIdle true …).
   Malformed settings file → keep last good values, log, surface a single warning event (property `LastError`) — never crash.
5. **`KeybindingService`** — implements `IKeybindingService` exactly as documented in `Core/Platform/Keybindings.cs`: chord state machine (prefix → `Pending`, timeout/escape/unknown → reset), `when` filtering through `IContextKeyService`,
   precedence **user > default**, later entry in the same source wins, negative bindings (`"-command"`) remove matching lower entries, platform-specific key overrides (`mac`/`linux`/`win` in contributions),
   user `keybindings.json` (JSONC array `{key, command, when, args}`) live-reloaded, `GetLabel` formatting per OS (`Ctrl+K, Ctrl+C`; macOS `⌘S`), `GetBindingsFor`.
   Ship two **embedded-resource default presets**: `visualstudio` (default — F5, Ctrl+F5, Shift+F5, Ctrl+Shift+F5, F9, F10, F11, Shift+F11, F12, Shift+F12, F2, Ctrl+K Ctrl+D, Ctrl+K Ctrl+C/U, Ctrl+. , Ctrl+Space, Ctrl+Shift+Space,
   Ctrl+Q quick launch, Ctrl+T/Ctrl+, go to all, Ctrl+Shift+P palette, Ctrl+P quick open, Ctrl+Shift+F find in files, Ctrl+F/H, Ctrl+G, Ctrl+S, Ctrl+Shift+S, Ctrl+Tab, Ctrl+F4 close (Ctrl+W starts Vim's window chords: docs/PLATFORM.md), Ctrl+Alt+L explorer,
   Ctrl+Alt+O output, Ctrl+\\ Ctrl+E error list, Ctrl+` terminal, Alt+Up/Down move line, Ctrl+D duplicate? (VS: Ctrl+D), Ctrl+L delete line, Ctrl+/ comment toggle …) and `vscode` (the VS Code defaults). Use the command ids in `KnownCommands`
   plus the standard VS Code ids (`editor.action.*`, `workbench.action.*`). Preset persisted via `IStateStorage`/setting `cscode.keymap`.
6. **`LocalFileSystem : IFileSystem`** (`file:` scheme; others → `NotSupportedException`): stat (follow symlinks, report `SymbolicLink` flag), readdir (no recursion), read, **atomic write** (temp file + fsync + rename; preserve permissions;
   handle read-only), mkdir -p, delete (with **trash**: Linux freedesktop Trash spec incl. `.trashinfo`; macOS `~/.Trash`; Windows `SHFileOperationW`/recycle bin P/Invoke), rename/copy (recursive, overwrite flag), path ↔ `Uri` correctness
   (spaces, `#`, `%`, Windows drive letters, UNC), all async + cancellable, no blocking the thread pool on large reads (use `FileStream` async with 64 KB buffers).
7. **`FileWatcher : IFileWatcher`** — recursive watch honouring exclude globs (`Glob` from Core; defaults from `files.watcherExclude`: `**/.git/objects/**`, `**/node_modules/**`, `**/bin/**`, `**/obj/**`…), **debounce + coalesce**
   (Created+Changed → Created, Created+Deleted → nothing, Renamed → Deleted+Created), robust to inotify watch-limit exhaustion (degrade to a coarse polling watcher for that root and log once), no handle leaks (disposing the token disposes every `FileSystemWatcher`/timer),
   events delivered on a background thread in batches.
8. **`WorkspaceService`** — open/close folder(s), `FoldersChanged`, recents (persisted, capped at 30, dedup), name, `FindFilesAsync` (streaming `IAsyncEnumerable<Uri>`, parallel directory walk with a bounded degree, cancellation, honours `files.exclude` **and `.gitignore`**
   including nested ignore files and negations, skips binary-looking dirs like `.git`, symlink-loop safe, memory O(depth) not O(files)). Plus `FuzzyMatcher` (public, `CsCode.Platform.Search`) — scoring à la VS Code quick open (consecutive bonus, word-start/camelCase bonus, path-separator bonus, case match bonus) returning score + matched indices; used by the palette and quick open (benchmark: 100k items < 50 ms).
9. **`StateStorage`** — JSON key/value for `Application` and `Workspace` scopes, atomic debounced flush (also on `FlushAsync`/dispose), corruption-tolerant (keep `.bak`), typed `Get<T>/Set<T>` via System.Text.Json.
10. **Logging** — `ILoggerProvider` writing rolling files under `LogsDir` (size + file-count caps: max 5 × 5 MB), thread-safe, bounded async queue (drop oldest when full, count drops), includes timestamps/levels/categories; plus `AddCsCodePlatform` wiring everything in DI as singletons with sensible disposal.
11. `ServiceCollectionExtensions.AddCsCodePlatform(this IServiceCollection)` registers all of the above (and `AddCsCodeLogging`).

## SHOULD
* **`.editorconfig` support**: additive Core interface `IEditorConfigService` (`Core/Platform/EditorConfig.cs`) + implementation: resolve `indent_style`, `indent_size`, `tab_width`, `end_of_line`, `charset`, `trim_trailing_whitespace`, `insert_final_newline` for a file URI per the editorconfig spec (section globs incl. `{a,b}`, `[*.{cs,vb}]`, `root = true`), cached per directory with invalidation on change. (The Editor module will consume it if present.)
* **Language-scoped settings** (`"[csharp]": {"editor.tabSize": 2}`): additive `IConfigurationService` extension method/interface for `Get<T>(key, languageId)`.
* Settings JSON-schema metadata endpoint: `IConfigurationService.Properties` returns what `RegisterProperties` registered, incl. enum/description for a future settings UI.

## Threading & isolation (binding)
* File watching, workspace walking/search and all file I/O run on a dedicated **IO lane** (own threads, bounded parallelism) — never on the UI thread and never starving the shared thread pool; async APIs return to the caller immediately. `FindFilesAsync` yields in batches and honours cancellation within ~10 ms.
* `CommandService.ExecuteAsync` invokes handlers as registered (handlers choose their own lane) but never holds a lock while awaiting; no sync-over-async anywhere.
* Configuration/keybinding/context-key reads are called on every keystroke from the UI thread: lock-free or very-short-lock, **zero allocation** in `Resolve`/`Evaluate`/`Get` fast paths. Test: 1M `Evaluate` calls, assert no allocations after warm-up.

## Tests (xUnit v3; `tests/CsCode.Platform.Tests`)
* When-clause: **≥ 80 cases** (precedence, short-circuit, type coercion, regex flags, `in`, malformed input, unicode, scopes, change events).
* Configuration: layering, dotted vs nested, JSONC edge cases, comment-preserving updates (golden before/after files incl. nested keys, missing file, trailing commas, CRLF), reload on external edit, no-op rewrite fires nothing, malformed file recovery.
* Keybindings: parse/format round trips, chords with timeout/reset, `when`, precedence & negatives, platform overrides, preset switch, user file reload, contribution add/remove.
* FileSystem/Watcher: temp-dir based (use `TempDirectory`); atomic-write crash safety (simulate failure before rename); trash on Linux (use a temp `XDG_DATA_HOME`); watcher coalescing, exclude globs, dispose releases handles (count `/proc/self/fd` on Linux).
* WorkspaceService: gitignore semantics golden tests (negation, dir-only patterns, anchored patterns, nested), cancellation, symlink loop.
* FuzzyMatcher: ranking golden cases + the 100k-item benchmark assertion (generous bound; mark as perf test).
* **Leak tests** (`LeakAssert`): command/keybinding/config subscriptions released on dispose; watcher disposal; context-key scopes.
* Concurrency smoke tests (parallel register/execute/set/get) — no deadlocks/exceptions.

## Done when
All MUST items implemented and green, `docs/PLATFORM.md` written (settings keys, file locations, keymap presets, limits), everything committed.
