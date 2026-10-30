# CsCode.Platform

The foundation every other module's runtime leans on: commands, context keys and `when` clauses, layered settings, keybindings, the
file system, file watching, workspace/folders/search, durable state and logging. No UI, no Avalonia, no assumption that a UI thread exists.

Owner: `src/CsCode.Platform/**`, `tests/CsCode.Platform.Tests/**`, this file. Additive Core files: `Core/Platform/EditorConfig.cs`,
`Core/Platform/ConfigurationLanguageExtensions.cs`.

* [Registration](#registration) · [Contracts and classes](#contracts-and-classes) · [Threading and isolation](#threading-and-isolation)
* [File locations](#file-locations) · [Settings](#settings) · [Keybindings](#keybindings) · [Commands and when-clauses](#commands-context-keys-and-when-clauses)
* [File system](#file-system) · [File watching](#file-watching) · [Workspace, search, fuzzy matching](#workspace-search-and-fuzzy-matching)
* [State and logging](#state-and-logging) · [EditorConfig](#editorconfig) · [Limits and known gaps](#limits-and-known-gaps) · [Tests](#tests)

## Registration

```csharp
services.AddCsCodeCore();       // Core registries (IContributionRegistry is optional for Platform, used when present)
services.AddCsCodePlatform();   // everything below + AddCsCodeLogging(); safe to call twice
services.AddCsCodePlatform(o => o.WatchDebounce = TimeSpan.FromMilliseconds(100));   // tunables: PlatformOptions
```

All services are singletons. Registrations use `TryAdd`, so a module registered earlier (e.g. a routing `IFileSystem` for remote editing) wins.
Container disposal flushes state and logs, releases every OS watch and stops the IO lane last. `IConfigurationService` etc. resolve to the
same instance as the concrete type (`ConfigurationService`, `KeybindingService`, `WorkspaceService`, `StateStorage`, `FileWatcher` …).

Start-up notes for the integrator:
* `ConfigurationService`, `StateStorage` and `KeybindingService` read their (small) files **synchronously in the constructor**, so settings, state and keymaps are complete before the first
  consumer runs. Resolve them from a background thread during start-up rather than on the first UI frame.
* Raise `IWorkspaceService.OpenFolderAsync` to bind workspace settings/state/editorconfig invalidation; nothing else is needed (see `IWorkspaceBound`).
* Events (`Changed`, `FoldersChanged`, …) are raised on **background threads** (IO lane / calling thread). UI subscribers must marshal.

## Contracts and classes

| Core contract | Class (`CsCode.Platform.*`) | Notes |
|---|---|---|
| `IAppPaths` | `AppPaths` | per-OS dirs, `CSCODE_HOME`, stable workspace hash; also `ConfigDir` |
| `ICommandService` | `Commands.CommandService` | stack semantics, `AddUnknownCommandResolver`, `GetFailedCommandId` |
| `IContextKeyService` | `Context.ContextKeyService` | full when grammar, scopes, `SetMany`, `Reset`; `Context.WhenClause.Validate/GetKeys` |
| `IConfigurationService` | `Configuration.ConfigurationService` | layered JSONC, comment-preserving `UpdateAsync`, `LastError`, `ErrorChanged`, `ReloadAsync` |
| `IKeybindingService` | `Keybindings.KeybindingService` | chords, presets, user file, contributions; `KeyBindingFormatter`, `LoadWarnings`, `ReloadAsync` |
| `IFileSystem` | `FileSystem.FileSystemRouter` → `FileSystem.LocalFileSystem` | routes by URI scheme (added for remote editing, docs/REMOTE.md): `file:` → `LocalFileSystem` (atomic writes; trash), other schemes → the registered `IFileSystemProvider` (Core); providers resolved lazily on the first non-`file:` call |
| `IFileWatcher` | `Watching.FileWatcherRouter` → `Watching.FileWatcher` | routes by scheme the same way (`IFileWatcherProvider`); an unserved scheme gets an inert watch. `FileWatcher.Watch` returns a `FileWatchHandle` (`Ready` task) |
| `IWorkspaceService` | `Workspace.WorkspaceService` | + `AddFolderAsync`, `RemoveFolderAsync`, `NoteRecent`, `ClearRecents`, `PrimaryFolder` |
| `IStateStorage` | `State.StateStorage` | `FlushAsync`, `DisposeAsync` |
| `ILoggerProvider` | `Logging.FileLoggerProvider` | rolling file logger |
| `IEditorConfigService` (new, additive) | `EditorConfig.EditorConfigService` | SHOULD item |
| language-scoped settings (additive extension methods) | `Core/Platform/ConfigurationLanguageExtensions` | SHOULD item |
| — | `Search.FuzzyMatcher` | public static; quick-open scoring/ranking |
| — | `Io.IoLane`, `Io.FileUri` | the dedicated IO lane; `file:` URI ⇄ path |

`IWorkspaceBound` (public) is the seam by which `WorkspaceService` tells dependents (`ConfigurationService`, `StateStorage`,
`EditorConfigService`) about folder changes *before* `FoldersChanged` fires, so subscribers already see the new workspace's settings. It exists to avoid a
dependency cycle (workspace search needs configuration; configuration needs the folder).

## Threading and isolation

The requirement: editing and the GUI stay on their own threads, isolated from language-server load. Platform is the layer that must never make a
caller wait.

| Work | Where it runs | Cost to the caller |
|---|---|---|
| every file-system call, directory walks, watcher arming/flush/disposal, settings/keybinding/state/editorconfig file I/O | **IO lane**: `IoLane`, ≤ 4 dedicated threads (`CsCode.IO-n`), not the shared pool | enqueue only (tested: 100 enqueues on a saturated lane ≪ 250 ms; each API has its own "does not block the caller" test) |
| log writing | one dedicated below-normal thread (`CsCode.Log`) | enqueue into a bounded ring |
| `ICommandService.ExecuteAsync` | the handler's own choice; **no lock is held while a handler runs or awaits** | lookup is lock-free |
| `Evaluate` (when-clause), `Resolve` (keys), `Get<primitive>` (settings), `FuzzyMatcher.Score` | the caller's thread, **allocation-free** (1M-call tests assert ≤ 4 KB total) | nanoseconds |
| raw OS file events | cheap enqueue on the OS callback thread; coalescing/delivery on the IO lane | none |

Mechanics worth knowing:
* `IoLane` work items are short, synchronous leaf operations that check cancellation between 64 KB chunks and **never wait for another lane item**;
  the queue is bounded (a full queue delays the returned task, never the caller). Long jobs are sliced: the workspace walk is a set of ≤ 3 ms
  "pump" items that re-enqueue themselves and **park** (hold no thread, queue nothing) when the consumer is slow.
  An item drops its delegate once it has run or was cancelled: an idle lane thread may still hold the last item it ran (Debug builds keep the loop's locals alive), and that must not keep
  the caller's closure — a disposed watch's callback — reachable (it made `LeakAssert` tests flaky).
* We deliberately do **not** use `FileStream`'s async API: on Unix .NET implements it by queueing blocking calls on the shared thread pool, which is the
  starvation the lane exists to prevent. Blocking on our own lane threads is safe because they are ours.
* `System.Threading.Timer` is used only to *schedule*; its pool callback does nothing but post a work item to the lane.
* Banned in this module (and grep-checked): `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep`, `Task.Run`, `Parallel.*` in `src/`.
  The only blocking waits are on lane/log threads themselves and one zero-timeout try-acquire.
* Hot-path structure: the context service keeps values in a `ConcurrentDictionary` and parses each `when` clause once into an AST in a bounded
  cache; keybinding resolution works on an immutable index with pre-allocated results and a fixed chord buffer; configuration reads come from an
  immutable snapshot with per-key caches that are replaced wholesale on change.

## File locations

`AppPaths` is pure: computing a path never touches the disk.

| | Linux (XDG) | Windows | macOS |
|---|---|---|---|
| config (`settings.json`, `keybindings.json`) — `AppPaths.ConfigDir` | `$XDG_CONFIG_HOME/cscode` (`~/.config/cscode`) | `%APPDATA%\CsCode` | `~/Library/Application Support/CsCode` |
| `UserDataDir` (`themes/`, `grammars/`, `snippets/`, `tools/`, storage) | `$XDG_DATA_HOME/cscode` (`~/.local/share/cscode`) | same as config | same as config |
| `CacheDir` | `$XDG_CACHE_HOME/cscode` | `%LOCALAPPDATA%\CsCode\Cache` | `~/Library/Caches/CsCode` |
| `LogsDir` | `$XDG_STATE_HOME/cscode/logs` (`~/.local/state/cscode/logs`) | `%LOCALAPPDATA%\CsCode\Logs` | `~/Library/Logs/CsCode` |
| `GlobalStorageDir` / `ToolsDir` | `{UserDataDir}/globalStorage`, `{UserDataDir}/tools` | | |
| application state | `{GlobalStorageDir}/state.json` (+ `.bak`, `.corrupt`) | | |
| per-workspace state | `{UserDataDir}/workspaceStorage/<hash>/state.json`; `<hash>` = first 32 hex of SHA-256 of the folder URI (no trailing slash; case-folded on Windows and, for local folders, on case-insensitive macOS volumes) | | |
| trash | `$XDG_DATA_HOME/Trash/{files,info}` | recycle bin (`SHFileOperationW`) | Finder's Trash (`NSFileManager trashItemAtURL:`; a same-volume rename into `~/.Trash` only as fallback) |

`CSCODE_HOME` overrides everything (portable installs and tests): `$CSCODE_HOME/{config,data,cache,logs}`. Relative `XDG_*` values are ignored per the spec.
An absolute `CSCODE_TRASH_DIR` replaces the trash on every OS with a freedesktop-style one there; the test harnesses set it so no test reaches the user's real Trash.
Core's contribution comment says `settings.json`/`keybindings.json` live in `UserDataDir`; they live in `ConfigDir` (identical on Windows/macOS, different on Linux) —
only Platform reads them, and it exposes `IConfigurationService.UserSettingsPath` and `KeybindingService.UserKeybindingsPath`.

## Settings

Layers, lowest to highest: **registered defaults → user `settings.json` → workspace `.vscode/settings.json` → workspace `.cscode/settings.json` → additional workspace folders' `.cscode/settings.json`** (in folder order).
"Workspace" = the primary (first) folder; `UpdateAsync` with `Workspace` or `WorkspaceFolder` writes the primary folder's `.cscode/settings.json` (there is no per-resource scoping API).

* **Format**: JSON with comments and trailing commas; a BOM and CRLF are accepted. `"editor.fontSize"` and `{"editor":{"fontSize":…}}` are equivalent and may be mixed;
  duplicate keys: last wins. As in VS Code, only the **top-level keys** of a file (and the keys directly inside a `"[lang]"` block) are split on dots: member names
  inside a value are kept as written, registered or not, so globs (`files.associations`, `files.exclude`), URLs (`yaml.schemas`) and server ids with dots survive
  (lsp-core-2; they used to be split at every depth unless the setting was registered as an object). Objects **merge** across layers, arrays/scalars replace.
* **Setting scopes** (VS Code's `ConfigurationScope`, lsp-core-1 / remote-5): a property registered with `Scope = Application` or `Machine`
  (`ConfigurationProperty.IsUserSettingOnly`), and every `MachineScopedMembers` path of an object setting (`*` = any member name), takes its value from the
  registered defaults and the **user** file only. A workspace or folder file's value there — in any spelling: dotted, nested, inside a `"[lang]"` block, or a
  parent replaced by a scalar — is dropped, listed in `ConfigurationService.IgnoredSettings` (+ `IgnoredSettingsChanged`), logged as a warning and shown once per
  file and key as a warning notice (through `IWindowService`, resolved lazily). `UpdateAsync(key, value, Workspace)` for such a key throws
  `InvalidOperationException` (VS Code refuses the same write); removing one (`null`) is allowed. Every setting that names a program CsCode runs, its arguments,
  its environment or its search path is machine-scoped, so a cloned repository's `.vscode/settings.json` cannot start anything through settings:
  `cscode.languageServers.*.command|args|env|workingDirectory`, `cscode.lsp.searchDirs`, `cscode.debuggers`, `cscode.debug.{netcoredbg,jsDebug,node,gdb,codelldb}Path`,
  `cscode.debug.gdbArgs`, `terminal.integrated.{profiles,defaultProfile,env}.<os>` (+ `inheritEnv`, application), every `remote.ssh.*`, `remote.docker.path`,
  `remote.wsl.path`, `vim.vimrc.path` and the ten `vim.*ModeKeyBindings*` settings (a binding's `commands` run any command on a key press)
  (`cscode.languageHost` is application-scoped). The audit list is `SettingScopeTests.ProgramSettings` (Remote.Tests); the remote server
  registers the terminal and debugger scopes itself (it has no TerminalService/DebugService). **Remote windows**
  (`SetRemoteMachineSettingsAsync(path)`, called by the remote server for ssh/docker/wsl clients): VS Code's remote user settings. The host's machine settings
  file is layered between the (client's, mirrored) user file and the workspace; machine-scoped keys and members come from it and the defaults only (the
  user file's values name the client's programs and are ignored, logged once), application-scoped ones from the user file only; `MachineSettingsPath`
  says which file (docs/REMOTE.md "Remote machine settings"). A module must register an executable setting machine-scoped
  **before** it first reads it; an unregistered key is window-scoped. Not covered by scopes (Workspace Trust territory, not implemented): `tasks.json` and
  `launch.json` run commands by design, and language servers themselves execute project content (rust-analyzer build scripts and proc macros, a project's
  TypeScript for Vue, server-specific `settings` such as rust-analyzer's `check.overrideCommand`).
* **Reads**: `Get<T>(key, default)` / `GetRaw(key)` / section reads (`GetRaw("editor")` returns the merged object). A registered default beats the caller's fallback; a value of the
  wrong type yields the fallback (never throws). Primitive reads allocate nothing after the first read of a key.
* **Live reload**: the settings directories are watched — the primary folder's `.cscode/` and `.vscode/` and every additional folder's `.cscode/` (lsp-core-7), also when they are created after the folder was opened; a 50 ms debounce coalesces bursts; `Changed` lists exactly the keys whose merged value changed (leaf paths; a registered object
  setting reports itself). Rewriting a file with the same values (different comments/order) fires nothing, nor does the echo of our own write.
* **`UpdateAsync` edits the file in place** with a small JSONC-aware minimal-edit writer: only the value span is replaced; a new key is inserted before the closing brace using the file's
  indentation (tab/spaces), EOL (LF/CRLF), comma style (a trailing-comma file stays one) and single-line/multi-line shape; flat dotted keys, nested objects and mixes are found; `null` removes the key
  (and its line/comma); a missing file/directory is created; the write is atomic; a BOM is preserved. A malformed existing file is **refused** (`ConfigurationFileException`) rather than rewritten.
  The file is re-read inside the update, so an external edit not yet seen by the watcher is not clobbered.
* **Errors**: a malformed file keeps the last good values; `LastError` holds the problem and `ErrorChanged` fires **once** (and with `null` when fixed).
* **Language-scoped**: `"[csharp]": { "editor.tabSize": 2 }`; read with `config.Get<int>("editor.tabSize", "csharp", 4)`, `HasLanguageOverride`, and use
  `e.Affects("editor.tabSize", "csharp")` on change events (`[csharp].editor.tabSize` is the affected key). `GetRaw("[csharp]")` hands the
  section back with its dotted keys expanded (`{"editor": {"tabSize": 2}}`): a consumer that reads the section itself must read that shape
  (the flat one too, for other configuration services); the editor does since T30 (docs/EDITOR.md §7).
* `Properties` returns everything registered (incl. enum/description schema) for a future settings UI.

Built-in defaults (all in `BuiltInSettings.All`; every key in `SettingKeys` has one):

| key | default |
|---|---|
| `editor.fontFamily` | `Cascadia Code, Consolas, 'Courier New', monospace` |
| `editor.fontSize` / `editor.tabSize` | `14` / `4` |
| `editor.insertSpaces` / `editor.wordWrap` | `true` / `"off"` |
| `editor.minimap.enabled` / `editor.formatOnSave` | `true` / `false` |
| `editor.bracketPairColorization.enabled` / `editor.renderWhitespace` / `editor.suggestOnTriggerCharacters` | `true` / `"selection"` / `true` |
| `editor.quickSuggestions` | `{"other":"on","comments":"off","strings":"off"}` |
| `cscode.editor.largeFileThresholdBytes` | `5242880` (5 MB) |
| `files.autoSave` | `"off"` |
| `files.exclude` | `**/.git`, `**/.svn`, `**/.hg`, `**/CVS`, `**/.DS_Store`, `**/Thumbs.db` → `true` |
| `files.watcherExclude` | `**/.git/objects/**`, `**/.git/subtree-cache/**`, `**/.hg/store/**`, `**/node_modules/**`, `**/bin/**`, `**/obj/**` → `true` |
| `workbench.colorTheme` / `workbench.startupEditor` | `"Visual Studio Dark"` / `"welcomePage"` |
| `cscode.memory.budgetMb` / `cscode.memory.trimOnIdle` | `0` / `true` |
| `cscode.keymap` | `"visualstudio"` (or `"vscode"`) |
| `search.useIgnoreFiles` / `search.followSymlinks` | `true` / `true` |

## Keybindings

Sources, lowest → highest precedence: embedded preset → `KeybindingContribution`s in the contribution registry (built-in, then `ContributionSources.User`) → user `keybindings.json`.
Later entries win; `{"command":"-foo"}` removes **earlier** entries of that command with the same key (a key-less negative removes all of them; when-clauses must match when both sides have one);
`mac`/`linux`/`win` replace `key` on that platform (in presets, user file and contributions).

* **Chords**: a prefix yields `Pending`; the next stroke completes it, or an unknown stroke / Escape / timeout (default 5 s, checked lazily on the next stroke, no timer) resets it and returns `None`.
  The first matching binding by precedence whose `when` holds decides — a user single-stroke binding on `Ctrl+K` therefore disables the `Ctrl+K …` chords.
* `Resolve` is allocation-free (tested over 1,000,000 strokes); `PendingChord` allocates (status bar only).
* `GetLabel`: `Ctrl+K, Ctrl+C` on Windows/Linux (`Win+`/`Super+` for Meta), `⌃⌥⇧⌘` glyph form with `⌘K ⌘C` on macOS.
* **User file** `{ConfigDir}/keybindings.json`: JSONC array of `{key, command, when, args, mac, linux, win}`; live-reloaded (debounced); bad entries are skipped and listed in `LoadWarnings`
  (an invalid `when` is kept but can never fire); a file that is not valid JSON at all keeps the previous bindings (a typo mid-edit does not wipe the keymap).
* **Presets** (embedded `Resources/Keymaps/*.json`): `visualstudio` (default) and `vscode`. `SetPresetAsync` switches live, persists to the `cscode.keymap` setting (the state store when no
  configuration service exists) and raises `Changed`; editing `cscode.keymap` in `settings.json` switches live too.
  Visual Studio preset: F5 start / continue (when `inDebugMode`), Ctrl+F5 run, Shift+F5 stop, Ctrl+Shift+F5 restart, F9 breakpoint, F10/F11/Shift+F11 step, F12 definition, Shift+F12 references,
  Ctrl+F12 implementation, F2 and Ctrl+R Ctrl+R rename, Ctrl+K Ctrl+D format document, Ctrl+K Ctrl+C/U comment/uncomment, Ctrl+/ toggle comment, Ctrl+. quick fix, Ctrl+Space / Ctrl+Shift+Space suggest / parameter hints,
  Ctrl+Q and Ctrl+Shift+P palette (Quick Launch opens the palette), Ctrl+T and Ctrl+, go to all, Ctrl+P quick open, Ctrl+Shift+F find in files, Ctrl+F / Ctrl+H / Ctrl+G, Ctrl+S / Ctrl+Shift+S (save all),
  Ctrl+Tab MRU, Ctrl+W / Ctrl+F4 close, Ctrl+Alt+L explorer, Ctrl+Alt+O output, Ctrl+\ Ctrl+E error list, Ctrl+\` terminal, Alt+Up/Down move lines, Ctrl+D duplicate, Ctrl+L delete line, folding chords.
  VS Code preset: the VS Code defaults for the same feature set (e.g. Shift+Alt+F format — Ctrl+Shift+I on Linux, Ctrl+Shift+K delete line, Ctrl+Shift+E/U/M views, Ctrl+K S save all).
  Command ids are `KnownCommands` or standard VS Code ids; **a key only works if the owning module registers the command** (some ids the presets use, e.g. `editor.action.addCommentLine`,
  `workbench.action.showAllSymbols`, `workbench.action.gotoLine`, are not in `KnownCommands`; the shell's keyboard pipeline should check `ICommandService.Has` before swallowing a key).

## Commands, context keys and when-clauses

`CommandService`: registrations are a **stack** per id (latest shadows; disposing restores). `ExecuteAsync` of an unknown id consults `AddUnknownCommandResolver` resolvers in order until one returns true,
retries the lookup once, else throws `CommandNotFoundException`. Handler exceptions propagate unchanged with the id in `Exception.Data` (`CommandService.GetFailedCommandId`; innermost wins; cancellation is not tagged).
Metadata (`All`) reports each id's effective registration; a shadowing registration without metadata inherits the nearest lower one. `Registered` fires per registration.

`ContextKeyService` (root + `CreateScope()` children that overlay without mutating the parent; parents reach children only through weak references, so dropping a scope needs no `Dispose`):
`Set` ignores no-op writes; enums/chars are stored as strings; `SetMany` raises one event listing exactly the changed keys; `config.<setting>` reads live from configuration and surfaces as `config.*` in `Changed`.

When-clause grammar (VS Code): `!` · `&&` · `||` (precedence `!` > `&&` > `||`) · `( )` · `==` `===` `!=` `!==` · `<` `<=` `>` `>=` · `=~ /re/flags` (`i m s`; linear-time engine, with a timeout-guarded fallback
for lookaround/back-references) · `in` / `not in` (arrays, lists, sets, dictionaries, JSON arrays/objects, substring of a string) · `true`/`false` · quoted strings and numbers · bare keys with JavaScript truthiness.
`==` is loose (a number matches a numeric literal; strings compare ordinally; `key == true` is truthiness; an unset key equals nothing), `===` also requires the same type. A syntax error makes the clause false,
logs one warning and never throws at evaluation; nesting is capped (64); the parse cache is bounded (2048). `WhenClause.Validate` / `GetKeys` are public.

## File system

`LocalFileSystem` (`file:` only; other schemes → `NotSupportedException`):
* `StatAsync` follows symlinks (size/time of the target; flags `SymbolicLink`; a dangling link reports `SymbolicLink` alone); `ReadDirectoryAsync` is one level, sorted, includes hidden entries.
* `WriteFileAsync` is **atomic** (sibling temp file → fsync → rename), writes **through** symlinks, refuses read-only files (`UnauthorizedAccessException`), creates parents on request.
  A failure before the rename (tested by fault injection) leaves the original untouched and no temp file. Readers see the complete old or complete new file (stress-tested).
  Not done: fsync of the containing directory (not expressible with .NET's APIs).
* **A save keeps the file** (lsp-core-8, VS Code / Vim `backupcopy=auto` behaviour; `AtomicFile`, `Io/UnixFileIdentity.cs`): the temp file gets the
  target's owner and group (`chown`, when they differ from a new file's), its mode (set-id bits too) and its extended attributes (ACLs, SELinux
  label, `user.*`). When a rename would lose something — the file has **other hard links**, its owner/group cannot be given to a new file, an xattr
  cannot be copied, or no temp file can be created next to it (a writable file in a read-only directory) — the content is written **in place**
  (same inode: write, truncate, fsync); if that fails part way (disk full, I/O error, cancellation) the old content (kept in memory up to 256 MB)
  is written back. In-place writes are not power-cut safe, as VS Code's are not. Linux only for links/owner/xattrs (`statx`); macOS and Windows
  keep only the mode, as before. `IFileSystem.PreservesFileOnWrite(uri)` (Core, additive, default false) says so for `file:` (and the remote file
  system, whose server writes the same way), and the editor's save then calls `WriteFileAsync(target)` instead of writing a temp file and renaming
  it over the target — that rename dropped the executable bit on every save in the app and broke hard links.
* `DeleteAsync`: recursive flag for non-empty directories; `useTrash` moves to the OS trash and **fails loudly** (`IOException`, original untouched) if it cannot — it never silently deletes permanently.
  Linux follows the freedesktop Trash spec (home trash; unique names `a.2.txt`; `.trashinfo` with percent-encoded `Path=` and local `DeletionDate=`, claimed atomically with `CreateNew`; cross-device = copy then delete).
  macOS uses Finder's Trash through Foundation (`trashItemAtURL:`, P/Invoke through `objc_msgSend`): items on other volumes go to that volume's trash and keep "Put Back"; when Foundation fails, only a same-volume rename into `~/.Trash` is tried, never a copy.
  Symlinks are trashed/deleted as links; recursive delete never follows directory links (cycles are harmless).
* `RenameAsync`/`CopyAsync`: `overwrite` flag; refuse moving/copying a directory into itself; case-only rename works (files and folders); whether names differing only in case are one entry is asked per volume (`pathconf(_PC_CASE_SENSITIVE)` on macOS; Linux sensitive, Windows insensitive) and two entries with different (device, inode) are never treated as one; copies recreate symlinks instead of following them; files above 4 MB copy chunked and cancellable (via temp file) — on macOS through `copyfile(COPYFILE_ALL | COPYFILE_CLONE)` instead (an APFS clone where possible; keeps ACL/xattrs/quarantine, as `File.Copy` does for smaller files), whose progress callback stops a data copy mid-file on cancellation — and copied folders get their metadata with `copyfile(COPYFILE_METADATA)` on macOS. Path comparisons (watcher roots, the workspace-state hash of local folders) follow the volume's case sensitivity on macOS too.
* `FileUri` maps `file:` URIs ⇄ paths correctly for spaces, `#`, `?`, `%`, unicode, Windows drive letters and UNC (OS-specific parts take a flag so they are tested on Linux).

## File watching

`FileWatcher`: one `FileSystemWatcher` per (root, recursive), **shared** by every subscription on that root (each OS watcher costs an inotify instance — the default limit is 128 per user) and disposed with the last one.
`Watch()` and disposal return immediately (the tree walk that arms inotify happens on the lane; `FileWatchHandle.Ready` completes when armed — events before that are not reported).
* **Roots that do not exist yet** (`.cscode/` / `.vscode/` of a folder opened without them — the settings service and DAP's `launch.json` watch both watch them) are not an error:
  the watch waits through a non-recursive watch on the nearest existing ancestor (shared like any other, so usually the folder's own — no extra inotify instance), arms when the directory appears
  and reports what is already inside as created; `Ready` completes once the wait is armed. Before this, the watch failed (`Cannot watch '…': it is not a directory`) and stayed in the shared
  table dead, so every later subscriber of that directory — the settings service re-watching when `.cscode` appeared — joined a watch that never fired: settings created after opening a folder
  were ignored until a restart.
* **Roots that go away**: an OS watch dies silently with its directory (no error on Linux; one on a moved directory keeps reporting it under the old path). A root is re-checked when a watched parent
  reports it deleted, renamed or (re-)created, after deletions below it, and when another subscriber joins it: a deleted root goes back to waiting, a re-created one is re-armed on the new directory
  (announcing its content). Not detected: a root replaced — or an empty one removed — while nothing watches its parent and nobody re-watches it.
* **Roots that keep going away**: on Linux a `FileSystemWatcher` whose directory was deleted can never be released — its reader thread blocks on an inotify instance with no watch left, and
  `Dispose` only removes watches (measured on .NET 10: +1 inotify fd and +1 thread per watcher disposed after its directory was deleted; none when disposed before, or after a move).
  Re-arming a fresh one after every deletion would cost one instance per `rm -rf` of a build output or `git checkout` that removes `.vscode/` — the per-user limit is often 128, shared
  with every other program. So a path that has lost `FileWatcher.MaxOsWatchLosses` (2) OS watches with its directory — counted per path, also when a module drops its watch after the
  directory went and watches the path again — is **polled** from then on (every 2 s, logged once at Information). Polling follows the path: a missing root is an empty snapshot, so
  deletions and re-creations are reported like any other change at no OS cost. Two, not one, because the same creation reported late by a second watched parent looks like a
  replacement. Identity checks cannot replace the counting: on ext4 a directory re-created at once reuses the deleted one's inode number and usually its 4 ms birth-time tick.
* Excludes (`files.watcherExclude` unless an explicit list is passed; `[]` = none) are matched on root-relative paths and drop events before they cost memory. *The OS still watches excluded directories — `FileSystemWatcher` cannot skip them.*
* Debounce 75 ms after the last event, never later than 500 ms after the burst began; batches ≤ 1000 changes, delivered on lane threads, never concurrently per subscription; a throwing callback keeps receiving.
* Coalescing: Created+Changed → Created · Created+Deleted → nothing · Changed+Deleted → Deleted · Deleted+Created → Changed · Renamed → Deleted+Created; events below a deleted directory are dropped;
  more than 100,000 distinct paths in one window collapse to a single `Changed(root)` "rescan" event.
* When a **directory appears**, the watcher lists it and announces what is already inside (`FileSystemWatcher` misses children created before its own watch is armed — `mkdir -p a/b/c` reports only `a`).
* **OS watch limit exhausted** (at arming or later): that root degrades to a polling watcher (every 2 s, ≤ 50,000 entries, default excludes pruned) and logs one warning per root.
  A failure is matched to the OS watch that reported it: a late report from a watch a re-check already replaced is ignored, and one that arrives while the watch is still being armed
  degrades it once it is in place. The polling watcher takes its baseline when it is armed, so a change right after `Ready` is reported.
* Queue overflow reports a `Changed(root)` so consumers rescan.

## Workspace, search and fuzzy matching

* `OpenFolderAsync` validates (missing → `DirectoryNotFoundException`, a file → `IOException`), normalises the URI (trailing slash), updates dependents, then raises `FoldersChanged`; changes are serialised.
  `RecentFolders`: persisted, most recent first, de-duplicated, capped at 30. `Name` joins folder names with `, `.
* `FindFilesAsync(includeGlob, maxResults, ct)` streams `Uri`s from a parallel walk (≤ 3 workers) honouring `files.exclude` and `.gitignore` semantics (below); never enters `.git`/`.hg`/`.svn`; order is unspecified; `maxResults ≤ 0` = unlimited.
  Memory is **O(depth × fan-out)** (depth-first with a LIFO stack of lazy enumerators; a 10×10×10 tree with 20,000 files peaks at under 150 pending directories — asserted), results flow through a bounded channel with back-pressure (a parked walk holds no thread),
  cancellation/abandoning stops it within one directory entry, symlink cycles are detected by comparing real paths with the ancestor chain, dangling links are skipped.
  Only `file:` folders are walked here; folders of other schemes are searched by the `IFileSearchProvider` registered for the scheme (remote: on the
  server), with the same include glob, `files.exclude`, ignore-file and symlink settings; `maxResults` spans all folders.
* **gitignore** (`GitIgnoreFile`, golden-tested): comments/blank lines · trailing unescaped spaces · `!` negation (last match wins; nearest file wins across nested files) · trailing `/` = directory only · a `/` at the start or middle anchors to the file's directory ·
  `*` `?` `[a-z]` `[!x]` never cross `/` · `**/x`, `a/**/b`, `a/**` · backslash escapes. Files in `.gitignore` apply even outside a Git repo; `.git/info/exclude` is read at the root; the global excludes file is not.
  A file inside an ignored directory cannot be re-included (the directory is never entered), as in Git. Ignore files > 1 MB are skipped. `search.useIgnoreFiles` turns it off.
* `FuzzyMatcher` (VS Code quick-open style): subsequence match, case-insensitive; the score rewards consecutive runs (a run keeps the start bonus it began with), word starts (after space `_ - .`), camelCase humps and digit runs,
  characters after a path separator (`/` or `\`), the first character, and exact case; gaps are penalised (3 to open, 1 per extra char). `Score` is the allocation-free ranking path (vectorised prefilter, DP over match positions only);
  `Match`/`MatchPath` also return the matched indices; `ScorePath` adds a file-name bonus; `Rank` keeps the best N with a heap (ties: shorter text first). The DP is fuzz-tested against a brute-force oracle
  (never exceeds it; equals it in ≥ 97% of random cases — it keeps one best path per cell like fzf) and `Score` always equals `Match`'s score.
  **Benchmark** (Release, 100,000 generated paths, top 50, best of 8 runs, on a machine shared with other builds): typically 10–13 ms for `mainwin`/`ctxsvc`/`txtdoc` and 24–36 ms for the worst case `cs` (it matches nearly every path);
  under heavy machine load the same runs measured up to ~46 ms, i.e. still inside the 50 ms budget but with little headroom — **a Release perf assertion can flake on a saturated machine** (it is the only timing test in a non-parallel
  collection for that reason). Debug builds assert a relaxed 250 ms regression guard instead.

## State and logging

`StateStorage`: key/value JSON in `Application` and `Workspace` scopes; reads/writes are in-memory; flush is debounced (500 ms, never later than 5 s after the first unsaved change), on `FlushAsync` and on dispose.
Atomic writes; the last good file is copied to `.bak` before replacement (never from a file not known good); a corrupt file is recovered from the `.bak` (kept as `.corrupt`), else state starts empty — never throws.
`Set` serialises immediately (so later mutation of the object cannot change what is saved); null removes the key; an identical value does not dirty the state. Without an open folder the workspace scope is in-memory only.
`Dispose()` flushes **synchronously on the calling thread** (shutdown path); prefer `DisposeAsync`.

`FileLoggerProvider`: `{LogsDir}/cscode.log`, rotated by size to `cscode.1.log …` — at most **5 files × 5 MB** (`FileLoggerOptions`); line format `2026-10-28T12:34:56.789Z [INF] Category: message` (exceptions and continuation lines indented);
default level Information. Callers only enqueue into a bounded ring (8192 entries); when it overflows the **oldest** entries are dropped, counted (`DroppedCount`) and reported by a warning line in the log. The writer thread swallows
any failure (an unwritable log location never reaches the caller), rotation is checked per line, dispose drains the queue.
Several CsCode processes share the file: each batch is written under an exclusive lock on `.cscode.lock` beside it (readers of the logs folder
read `*.log` only). A batch waits for another process's batch as long as that takes, never writing without the lock (a holder stalled mid-batch
would overwrite it); while stopping the wait ends 1 s after `Dispose` began and what is still queued is dropped (counted). A lock that cannot be
taken at all (read-only or full disk, I/O error) is not waited for: the batch is written without it.

## EditorConfig

`IEditorConfigService` (additive Core contract) / `EditorConfigService`: `indent_style`, `indent_size`, `tab_width`, `end_of_line`, `charset`, `trim_trailing_whitespace`, `insert_final_newline`.
Per the spec: files are read from the file's directory upward until `root = true`; farther files apply first, nearer ones override, later sections override earlier; `unset` removes; section globs support `*`, `**`, `?`, `[seq]`, `[!seq]`,
`{a,b}` (nested), `{n..m}` ranges, escapes, and a `/` anchors the glob to the file's directory; `indent_size = tab` uses `tab_width`, `tab_width` defaults to `indent_size`. Invalid values are ignored.
Parsed files are cached per directory, results per file (both bounded, 1024 / 4096); invalidated by the shared recursive watch on workspace folders, and — for files outside the workspace — by timestamp/size re-checks after 5 s.
`TryGetCached` never touches the disk. `.editorconfig` files > 1 MB are ignored; parsing stops at 20,000 lines. Not supported: the deprecated inline-comment form, `max_line_length`, other properties.

## Limits and known gaps

Stated plainly — what is **not** done or **not verified**:
* **Windows and macOS code paths were not run** (this repository's runs are Linux-only): the recycle-bin P/Invoke (`SHFileOperationW`, both 32/64-bit struct layouts), macOS `~/.Trash` moves, Windows path/UNC handling of `FileSystemWatcher`
  and `AtomicFile`'s rename semantics. The Windows/macOS *path layouts* and URI mapping are unit-tested through injected flags; the OS calls themselves are not.
* Excluded directories are still watched at OS level by `FileSystemWatcher` (events are filtered); a raw inotify implementation that skips them was judged out of scope. On a huge `node_modules` tree the OS limit can still be hit; the polling fallback then takes over.
* Polling fallback is coarse (2 s) and capped at 50,000 entries per root.
* Cycle detection for directory symlinks compares real paths built from the walk root; if the root itself lives below a symlinked directory (e.g. macOS `/tmp`) a link back to that ancestor may be followed once more before being detected.
* `ConfigurationTarget.Workspace` and `WorkspaceFolder` both write the primary folder; there is no per-resource folder scoping, so additional folders' settings are overlaid in order rather than applying to their own files only.
* Settings, state and keybinding files are read synchronously **once, in their constructors** (see Registration); every later read, reload and write is on the IO lane.
* `FileSystem.StatAsync` of a symlink chain resolves the final target only; `ReadDirectoryAsync` marks entry types but not link targets.
* The `.gitignore` engine does not read the global `core.excludesFile`, is case-sensitive (no `core.ignorecase`), and ignores POSIX `[:alpha:]` classes.
* Events carry no ordering guarantee between different services.
* The Windows recycle bin does not write "put back" metadata (macOS's Foundation trash does; its `~/.Trash` fallback does not).

There is no extension system (ARCHITECTURE §1). Its last remnants (`IAppPaths.ExtensionsDir`, the extension command ids, the `ExtensionId` members of Core records, extension-host wording)
were removed at integration; `NoExtensionSystemTests` keeps them from coming back.

## Tests

```
dotnet test tests/CsCode.Platform.Tests          # Debug: all tests
dotnet test tests/CsCode.Platform.Tests -c Release   # also asserts the strict 50 ms fuzzy-ranking budget
```
(On a Nix-provided `dotnet` for the Avalonia projects, `source tools/nix-dev-env.sh` first; the Platform tests need none of its library paths.)
Suites: when-clause (~200 cases) and context keys · JSONC parser/editor golden files + a random-edit model test · configuration (layering, reload, update, echo suppression, malformed recovery) ·
keybindings (chords, timeouts via a manual clock, precedence/negatives/platforms, presets, user file, contributions) · local file system (atomicity stress, fault injection, symlinks, trash, cancellation via chunk hooks) ·
file watcher (real `FileSystemWatcher` + inotify fd accounting, coalescing, excludes, polling fallback by fault injection, roots created/deleted/replaced/moved after watching, ten
delete/re-create cycles costing ≤ 2 inotify instances, stale and early failure reports, a root back before its dead watch is replaced) · workspace/gitignore/walker · fuzzy matcher (oracle + benchmark) · state, logging, editorconfig, language scopes · DI container (incl. workspace settings
created after opening a folder while another module watches the missing directories). The same scenarios through the real app composition (settings + DAP `launch.json`; `.vscode/` deleted and
re-created five times, ≤ 2 inotify instances lost) are in `tests/CsCode.App.Tests/WorkspaceSettingsWatchTests.cs`.
Conventions: a test needing an unavailable feature **skips with a reason** (`Assert.SkipWhen/SkipUnless`: Windows-only, root, non-Linux fd counting); no test touches `~/.cscode`, `~/.config/cscode`, `~/.vscode*` or `~/.ssh`, mutates the process environment,
or kills processes by name — every test uses a unique temp directory and `AppPaths(getEnv: …)`. The watcher and perf tests run in non-parallel collections because they count process-wide handles / measure time.
Every lifecycle-bearing type has a `LeakAssert.NotRetained` test; every API that does background work has a "does not block the caller" test against a saturated `IoLane`; the hot paths have 1M-call allocation tests.
