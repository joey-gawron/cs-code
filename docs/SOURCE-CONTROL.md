# Source control and the diff editor (`CsCode.Panels.Git`, `CsCode.Editor/Diff`)

Git integration built in (no extension): the Source Control window, Explorer decorations, a status bar branch item, gutter change markers,
inline blame, and a diff editor that the rest of the app (Compare with Saved / Clipboard / Selected) uses too. Git is driven through the
**git CLI** (no libgit2): every call is one `git` process on the module's own lane.

| Piece | Where | What |
|---|---|---|
| Line diff | `src/CsCode.Core/Text/LineDiff.cs` | Myers O(ND), linear space; character diff; used by everything below |
| Contracts | `src/CsCode.Core/Scm/SourceControlContracts.cs` | `IFileDecorationProvider`, `IQuickDiffProvider`, `ILineBlameProvider`, `IDiffEditorService`, `DiffEditorRequest`, context keys |
| Engine | `src/CsCode.Panels.Git/Engine/` (UI-free) | `GitRunner` (process lane), `PorcelainV2` (status parser), `GitRepository` (status model, operations), `GitService` (repositories, providers), `BlameParser`, `TextSplice` (hunk staging) |
| Window, commands, status bar | `src/CsCode.Panels.Git/` | `GitHost`, `SourceControlViewModel`, `SourceControlView`, `GitSettings` |
| Diff editor | `src/CsCode.Editor/Diff/` | `DiffModel`, `UnifiedDiff`, `DiffEditorControl`, renderers |
| Gutter markers, inline blame | `src/CsCode.Editor/Features/QuickDiffFeature.cs`, `InlineBlameFeature.cs` | editor features over the Core providers |
| Shell glue | `src/CsCode.App/Services/DiffEditorService.cs`, Explorer (`ExplorerNode.Decoration`) | diff tabs, `textCompareEditorVisible`, Next/Previous Change, decorations on rows |

DI: `services.AddCsCodeGit()` (in `AppServices.Build`) registers `GitService` (also as the three providers), `GitHost` and the
`SourceControlToolWindow`. The shell registers `DiffEditorService` as `IDiffEditorService` and starts it from `ShellController` (commands and
the context key). Everything is optional: without the module the Explorer shows no decorations and the editor no markers.

## Finding git and repositories

* **Executable** (`GitRunner.ResolveAsync`): `git.path` if set (`~` expanded), else the PATH, else the usual install directories
  (`/opt/homebrew/bin`, `/usr/local/bin`, MacPorts, Nix, `/usr/bin`; Linux: `/usr/bin`, `/usr/local/bin`, Nix). On macOS it first waits (bounded)
  for the login shell's environment (`LoginShellEnvironment.WaitAsync`), so an app started from the Dock finds Homebrew's git. A candidate must
  answer `git --version`; macOS's `/usr/bin/git` shim without the Command Line Tools does not, and the window then says how to fix it
  ("Install the Xcode Command Line Tools (xcode-select --install) ... or set git.path"). A failed lookup is retried after 30 s.
* **Repositories** (`GitService`): for each local workspace folder, `git rev-parse --show-prefix --absolute-git-dir` there (the repository that
  contains the folder, also when a subfolder is opened), plus repositories **directly inside** it (a folder of checkouts: a child with a `.git`;
  at most 64 children are looked at). The root is spelled from the workspace path, not git's realpath: macOS's temp and home paths
  (`/var` → `/private/var`) would otherwise never match the Explorer's URIs. A linked worktree's git directory (outside the root) is watched too.
* **Remote folders** (`ssh:`, containers): no repository, and the window shows *"Source control is not available for remote folders yet: git runs
  on this machine only."* Running git on the remote host needs a `git/exec`-like request in the remote server protocol (see What is left).

## Threading

* Every git process is started and read on the **git lane**: three dedicated `BelowNormal` threads (`cscode-git-1..3`), never the UI thread or a
  shared-pool worker that blocks. Three, so a status refresh or a blame is not stuck behind a long pull. Standard error is collected by the process
  class; stdin (commit message, blame `--contents`, `hash-object --stdin`) is written before stdout is read (each of these commands reads all
  its input first).
* Each process gets `GIT_TERMINAL_PROMPT=0` (never waits for a password nobody can type; credential helpers still work), `GIT_OPTIONAL_LOCKS=0`
  (status never takes the index lock from the user's own git), no pager, `GIT_EDITOR=true`, `-c core.quotepath=false -c color.ui=false`, and
  `GIT_DIR`/`GIT_WORK_TREE`/`GIT_INDEX_FILE` removed (a CsCode started from a git hook must not inherit them). Local commands time out after 60 s;
  cancelling kills the process tree. Child processes are reported to `IMemoryMonitor.TrackChildProcess`.
* **Network operations** (fetch, pull, push: `GitRunner.RunNetworkAsync`, environment built by `GitNetwork`) can neither prompt on a terminal nor
  hang for ever:
  * **Prompts**: when an `IAskpassService` is registered (the Remote module's, the same `AskpassServer` remote connections use: a 0700 directory, a
    one-time token, `cscode --askpass`) and `git.useIntegratedAskPass` is on, each operation gets its own helper: `SSH_ASKPASS` +
    `SSH_ASKPASS_REQUIRE=force` (key passphrases, passwords, host-key confirmations) and `GIT_ASKPASS` (HTTPS user names and passwords), answered in
    CsCode's input box / Yes-No message, deleted when the operation ends. Without one, ssh runs with `-o BatchMode=yes` and fails instead of
    asking. Credential helpers (osxkeychain, libsecret, GCM) and ssh agents work either way. With the setting on, CsCode's helper replaces the
    user's own for these operations: `GIT_ASKPASS` beats `core.askPass`, and `SSH_ASKPASS_REQUIRE=force` makes ssh use CsCode's `SSH_ASKPASS`
    (so e.g. KDE's ksshaskpass is not asked). With `git.useIntegratedAskPass: false` one's own `GIT_ASKPASS` / `core.askPass` answer HTTPS
    prompts, and ssh gets `BatchMode=yes` unless one's ssh command or ssh configuration sets `BatchMode` (e.g. `BatchMode no`, which lets one's
    own `SSH_ASKPASS` answer: ssh has no terminal).
  * **No terminal**: git starts in a session of its own (`setsid` on Linux, util-linux's or busybox's, with no option as busybox's takes none but
    `-c`; else perl's `POSIX::setsid` + exec), so neither ssh nor a credential helper can open the terminal CsCode was started from (`/dev/tty`);
    without either program (Windows, a minimal system) the batch mode / askpass above still keeps ssh off the terminal. Either way git is the same
    process (killing it kills git, its exit code is git's): a child of .NET is never a process-group leader, so `setsid` calls `setsid()` and
    execs rather than forking, and util-linux's `--wait` (2.29+, and refused by busybox) is not needed.
  * **Timeouts**: ssh gets `-o ConnectTimeout=30 -o ServerAliveInterval=15 -o ServerAliveCountMax=4` (a host that does not answer in 30 s, or a link
    silent for a minute, fails), each only when the user's ssh configuration does not set that keyword: options on ssh's command line beat
    `~/.ssh/config`, so ours are left out when `~/.ssh/config`, `/etc/ssh/ssh_config` (on Windows `%ProgramData%\ssh\ssh_config` and Git for Windows' `etc\ssh\ssh_config`) or a file they `Include` (or the user's ssh command's `-F`
    file instead) sets it in any `Host`/`Match` block (`GitNetwork.ConfiguredSshKeywords`, read on the git lane before each operation); a
    `ConnectTimeout 120` there gives 120 s, not 30 (the same goes for `BatchMode`). The scan is per keyword, not per host (`ssh -G` would need the host and costs a process): a keyword set for one host leaves ours out for every host, and such a host's neighbours then rely on `git.networkTimeout` and the OS connect timeout. HTTP transfers get no low-speed limit (as in git and VS Code): curl's
    `GIT_HTTP_LOW_SPEED_LIMIT/TIME` abort a transfer whose average stays under the limit, and a live server counting or compressing objects
    without bitmaps, or running a slow pre-receive hook, sends only progress lines and keep-alives (tens of bytes a second) for minutes; the user's
    own `http.lowSpeedLimit/Time` still apply. The whole operation is stopped after `git.networkTimeout` (1800 s; 0 = no limit, and so is a value
    above the 49.7 days a timer takes) with *"git fetch timed out after 30 min"*; curl's TCP keep-alive ends a dead HTTPS peer sooner. Any
    operation can be cancelled from its progress notification.
  * **The user's own ssh command** is kept. `GIT_SSH_COMMAND` or `core.sshCommand` (the last value of all scopes, as git reads it) running `ssh`
    gets the options appended after the user's own (ssh takes an option's first value, so an explicit `-o BatchMode=no` or `ConnectTimeout` of
    the user wins); any other program (a wrapper, plink) and `GIT_SSH` are left exactly as they are.
* Mutating operations of one repository run one at a time (a semaphore) and each ends with a status refresh. Events (`StatusChanged`,
  `HistoryChanged`, `DecorationsChanged`, `OriginalChanged`) fire on lane/timer threads; view-models post to the UI thread.

## Status model

* `git status --porcelain=v2 -z --branch --untracked-files=all --ignored=matching`, parsed by `PorcelainV2` (headers `branch.oid/head/upstream/ab`,
  records `1`, `2` (rename, with the original path in the next NUL field), `u` (unmerged), `?`, `!`; malformed records are skipped). `--ignored=matching`
  lists an ignored folder once (`bin/`), not its contents, so it stays cheap in a tree with build output.
* **Refresh**: the workspace file watcher on the repository root (recursive, `files.watcherExclude` applies: `.git/objects` is excluded) with a
  300 ms debounce; changes to `*.lock`, `objects/`, `logs/`, `FETCH_HEAD` are ignored; `index`, `HEAD`, `refs/`, `packed-refs`, `MERGE_HEAD` also mark
  **history changed** (the originals of the gutter markers and blame are stale). Plus: on window activation (at most every 2 s; `git.autorefresh`
  turns both off) and after every operation. At most one `git status` runs per repository, and at most one more is queued however many requests come.
* `GitStatusEntry` exposes `IsStaged` / `IsUnstaged` / `IsConflict` and the letters VS Code shows (`IndexLetter`, `WorkTreeLetter`: M, A, D, R, C,
  T, U untracked, ! conflict).

## Explorer decorations

`GitService` builds, after each status change and off the UI thread, a map of full path → `FileDecoration` (badge, theme colour key, tooltip,
strikethrough, faded) for files, a map for folders (the colour of the most important change inside, badge `●`: conflict > deleted > modified >
added > untracked), and the set of ignored paths (a path is ignored when it or one of its folders is listed). The Explorer
(`ExplorerViewModel`, new optional `IFileDecorationProvider` argument) sets `ExplorerNode.Decoration` for every shown row whenever its rows change,
and again (coalesced) on `DecorationsChanged`: dictionary lookups only. The row draws the name in the colour, the letter after it, ignored rows at
60 % opacity, deleted ones struck through. Colours are VS Code's `gitDecoration.*` keys (already in the built-in themes). Off with `git.decorations.enabled`.
Repositories refresh on different lane threads, so two rebuilds can overlap: building and publishing a snapshot happen under one lock, so the
snapshot published last was built from statuses read after every earlier one (an older snapshot published after a newer one left stale
decorations with several repositories; test `With_two_repositories_a_slow_rebuild_never_publishes_its_older_decorations_over_a_newer_one`).

## Source Control window (`scm`, docked on the right next to the Explorer)

Visual Studio's Git Changes / VS Code's Source Control view in one:

* Header: branch button (switch/create), Fetch, Pull, Push, Refresh, `…` (Commit, Commit (Amend), Pull, Push, Fetch, Sync, Checkout to...,
  Create Branch..., Stage/Unstage/Discard All, Show Git Output). A repository picker appears when the workspace has more than one; the window
  follows the active editor's repository. The header's commands act on the repository the window shows (it passes that repository's root as the
  command argument); run from the palette or the status bar they act on the active editor's repository.
* Commit box (placeholder `Message (Ctrl+Enter to commit on 'main')`, `⌘↩` on a Mac: the active keymap's label of `git.commit`, as is the Commit button's tooltip, both following keymap changes), **Commit** (Ctrl/Cmd+Enter in the box, also bound in the keymaps as
  `git.commit` when `scmInputFocus`), **Amend** (fills the box with the last message; an empty message amends with `--no-edit`). With nothing staged
  the button says **Commit All** and asks *"There are no staged changes to commit. Stage all your changes and commit them directly?"* (Yes /
  Always (sets `git.enableSmartCommit`) / Cancel). The message goes through stdin (`commit -F - --cleanup=strip`).
* One virtualised list: **Merge Changes**, **Staged Changes**, **Changes** group rows (count badge; Unstage All, Discard All, Stage All buttons) and
  file rows (name in the git colour, folder dimmed, letter; Open File, Discard, Stage/Unstage buttons). Click opens the change in the diff editor,
  double-click opens the file. Keys: Enter (open changes), Space (stage/unstage), Delete (discard). Context menu: Open Changes, Open File,
  Stage/Unstage, Discard. `ToolWindowFocus.KeepOnRows` keeps the keyboard on the rows across refreshes; View > Tool Windows puts it in the box.
* **Open Changes** compares: a staged change HEAD ↔ index (texts), an unstaged one index ↔ the working file (live), an untracked file empty ↔
  the file, a file deleted on disk index ↔ empty; a conflicted file opens as a file. Revisions are read with `git cat-file --filters`, i.e. as
  checkout would write them (core.autocrlf, `.gitattributes` eol), so they compare line by line with the file.
* **Discard** asks first; for untracked files it says they will be DELETED (`git clean -f`), tracked files are restored from the index (`git checkout`).
  When local history is composed (`ILocalHistoryService`, docs/LOCAL-HISTORY.md), each file is first kept as an "Undo Discard" entry (at most
  5 s), so the Timeline can restore what was discarded, untracked files included.
* Pull, push (a branch without upstream is published to `origin`, else the first remote, with `-u`), fetch (`--prune`) and sync (pull then push,
  confirmed unless `git.confirmSync` is off) run under a cancellable progress notification; the window shows them as busy. Any git failure shows
  *"Git: <git's own message>"* with a Show Output action and is logged in the **Git** output channel. Refusals printed on stdout ("nothing to
  commit") are used when stderr is empty.

## Timeline (Git History)

`GitTimelineProvider` (`Engine/GitTimeline.cs`, registered by `AddCsCodeGit` as a Core `ITimelineProvider`) lists the commits that changed a
file in the Timeline window, merged by time with its local history: `git log --follow`, paged without git's `--skip` (wrong with `--follow` past
a rename), each version read under the path it had in that commit. Details: docs/LOCAL-HISTORY.md, "Git History".

## Status bar

`git.branch`: `$(source-control) main*` (`*` = uncommitted changes; a detached HEAD shows its short commit), click = Checkout to...;
`git.sync`: `$(sync)` / `$(sync) 1↓ 2↑` with the upstream in the tooltip (click = Sync), or `$(arrow-up) Publish` without an upstream. For the
active editor's repository, else the window's, else the first.

## Diff editor

`IDiffEditorService.OpenDiffAsync(DiffEditorRequest)` opens a document tab (id `view:diff:<key>`; the same comparison again activates its tab, or
replaces it when the content changed) whose view is `DiffEditorControl`, created by `IEditorViewFactory.CreateDiffView` (additive member) and
recreated from the request when the shell rebuilds the tab's view.

* A side is a fixed text (`DiffSide.Text`: a revision, the clipboard) or a file (`DiffSide.Uri`: the open document's text when it is open, else
  the disk; followed live: document edits, open/close, and file-watcher changes recompute the diff, debounced). An original with neither means
  "nothing to compare": one read-only document (Git: Blame).
* **Panes** are real editor views (`CodeEditorControl`) over **snapshot documents**: read-only `EditorDocument`s that are not registered with the
  document manager (no language server, no tab, no hot-exit backup), coloured with the language of `LanguageHint`. So find, copy, Vim motions and
  syntax colouring work in both panes. They are released with their tokenisation session when the control is disposed.
* **Side by side** (`diffEditor.renderSideBySide`, default) or **inline** (toggle button): the inline view puts each change's removed lines above
  its inserted lines and draws its own two-column line-number margin (original, modified, +/-).
* Highlighting: changed lines `diffEditor.removedLineBackground` / `insertedLineBackground`, changed characters inside them
  `removedTextBackground` / `insertedTextBackground` (character diff of each change up to 20,000 characters a side, tiny equal gaps merged), and a
  thin `editorGutter.*` marker line where the other side has lines this one lacks.
* **Synchronised scrolling** through `DiffModel.MapLine`: an equal region maps 1:1 (shifted), inside a change the position is interpolated between
  the two blocks, so panes stay aligned through blocks of different heights. A programmatic scroll is applied at the next layout pass and the
  pane reports its old offset meanwhile: the target is remembered (500 ms) so those reports are not synced back (without this the panes fought
  and neither moved). `TextEditor.ScrollToVerticalOffset` is a no-op for absolute positions; the text area's `IScrollable.Offset` is set instead.
* **Navigation**: Previous / Next Change buttons, and the commands `workbench.action.compareEditor.nextChange` / `previousChange` (Alt+F5 /
  Shift+Alt+F5 when `textCompareEditorVisible`), wrapping; the caret of both panes moves to the change.
* Whitespace: `diffEditor.ignoreTrimWhitespace` (default true, VS Code's). The diff runs on the editor worker with a 2 s budget; past it the
  remaining ranges are shown as whole changes and the summary says "diff took too long: shown coarsely".

### The diff algorithm (`LineDiff`)

Lines are interned to integers, the common prefix and suffix stripped, then Myers' middle-snake divide and conquer finds a shortest edit script
in O((N+M)·D) time and O(N+M) space. Pure insertions/deletions are slid down as far as they go and back up to end on a blank line (git's rule in
its simplest form), so a function inserted between two others is reported whole instead of as a shifted closing brace. A time budget makes
pathological inputs safe: unsolved ranges become one change each (a correct, coarser diff). Cancellation is checked in the recursion and every
256 diagonals. `ComputeChars` runs the same engine on characters. No dependency was added.

## Gutter change markers (quick diff)

`QuickDiffFeature` (per editor view of a `file:` document, when an `IQuickDiffProvider` is registered): the original is the file's **index**
version (VS Code's choice; untracked and ignored files have none), cached per path (64 entries) and dropped when history changes. The document is
diffed against it on the editor worker 250 ms after typing pauses, and again when the original moves. A 6 px margin between the line numbers and
the folding chevrons draws `editorGutter.addedBackground` / `modifiedBackground` bars and a `deletedBackground` wedge on the line boundary where
lines were deleted. Clicking a marker opens a peek (an in-editor overlay) with the original lines and ↑ ↓ Revert Stage ✕: **Revert** puts the
original lines back (one undo step), **Stage** calls `IQuickDiffProvider.StageRangeAsync` with that change's lines. `scm.diffDecorations: "none"`
turns the markers off.

**Hunk staging** (`GitRepository.StageRangeAsync`): the index version (with filters) and the editor's text are diffed **with line terminators**
(so a final newline added or CRLF turned to LF is a change of its own), the changes touching the given lines are spliced into the index text
(`TextSplice`), and the result is written straight to the index (`git hash-object -w --stdin --path` + `git update-index --cacheinfo <mode>`),
the way VS Code stages selected ranges. `UnstageRangeAsync` splices HEAD's lines back into the index the same way. The splice works on **bytes**
(each byte read as one Latin-1 character), so lines it does not pick keep their exact bytes in any ASCII-compatible encoding (UTF-8,
Windows-1252, Shift-JIS, ...) and the index keeps its UTF-8 BOM, or its lack of one; the editor's text is encoded in the document's encoding
(`GitRepository.EncodingFor`) first, a file that is not open is taken as its bytes on disk, and binary (NUL) content is not staged.

## Blame

* **Inline** (`InlineBlameFeature`, `git.blame.editorDecoration.enabled`, off by default): after the caret has rested 350 ms on a line,
  `git blame --porcelain -L n,n [--contents -]` (the editor's text goes along when the document is dirty, so unsaved lines read "You, Uncommitted
  changes"); drawn after the end of the line in the CodeLens colour, italic: `Jane Doe, 3 weeks ago • Fix the parser`. Cached per (line, version).
  `git.toggleBlame` flips the setting.
* **Git: Blame** (`git.blame`): the active file annotated (`<sha> <author> <date> │ <line>`) in a read-only document tab.

## Compare commands

| Command | Title | Does |
|---|---|---|
| `workbench.files.action.compareWithSaved` | Compare Active File with Saved | the disk copy ↔ the open document (live) |
| `workbench.files.action.compareWithClipboard` | Compare Active File with Clipboard | the clipboard text ↔ the open document |
| `selectForCompare` (arg: URI) | Select for Compare | remembers the left side; sets `resourceSelectedForCompare` |
| `compareFiles` (arg: URI) | Compare with Selected | the selected file ↔ this one (both live) |

The last two are contributed to the Explorer's `explorer/context` menu (group `3_compare`).

## Commands, keys, settings, context keys

Commands (palette category "Git"): `git.refresh`, `git.commit`, `git.commitAmend`, `git.stageAll`, `git.unstageAll`, `git.cleanAll`,
`git.stage` / `git.unstage` / `git.clean` / `git.openChange` / `git.openFile` (arguments: file URIs or URI strings; none = the active file),
`git.checkout`, `git.branch`, `git.pull`, `git.push`, `git.fetch`, `git.sync`, `git.blame`, `git.toggleBlame`, `git.showOutput`; the tool window
toggle `workbench.view.scm` (registered by the shell from `IToolWindow.ToggleCommandId`); `workbench.action.compareEditor.nextChange` /
`previousChange` (shell, category "Compare").

Keys (both presets): `ctrl+shift+g` → `workbench.view.scm` (mac `ctrl+shift+g`, as VS Code for Mac: ⌘⇧G is Find Previous there);
`ctrl+enter` → `git.commit` when `scmInputFocus` (mac `cmd+enter`); `alt+f5` / `shift+alt+f5` → next / previous change when
`textCompareEditorVisible`. Visual Studio's own Git Changes chord (Ctrl+0, Ctrl+G) is not bound: it would make Ctrl+0 a chord prefix.

Settings (registered by `GitService`, `GitSettings.Properties`): `git.enabled` (true), `git.path` (null, machine scope), `git.autorefresh` (true),
`git.decorations.enabled` (true), `git.blame.editorDecoration.enabled` (false), `git.confirmSync` (true), `git.enableSmartCommit` (false),
`git.useIntegratedAskPass` (true), `git.networkTimeout` (1800 seconds, 0 or above 49.7 days = none),
`scm.diffDecorations` ("all" | "none"), `diffEditor.renderSideBySide` (true), `diffEditor.ignoreTrimWhitespace` (true). The editor reads the last
three as nullable, so it keeps VS Code's defaults when the git module is absent.

Context keys: `textCompareEditorVisible`, `scmInputFocus`, `resourceSelectedForCompare`, `gitOpenRepositoryCount`.

## Memory and lifetime

The view-model, view, diff control, gutter and blame features are disposable and drop every subscription (`DisposableStore`); the diff control
disposes its pane views and releases its snapshot documents and their tokenisation sessions (test: `Closing_a_diff_releases_its_views_and_documents`).
Caches are bounded: quick-diff originals 64 entries, inline blame 256 entries per view, git stderr 64 KB per process. Status snapshots are
immutable and replaced wholesale.

## Tests

* `tests/CsCode.Core.Tests/LineDiffTests.cs`: identical/empty/insert/delete cases, Myers' classic example has edit distance 5, the inserted-function
  case, ignore-whitespace, 300 random edit scripts round-trip and align, 50,000-line inputs under 2 s, a 1-tick budget still gives a correct
  coarse diff, cancellation, line splitting, character changes.
* `tests/CsCode.Panels.Git.Tests/ParserTests.cs`: porcelain v2 headers, every record kind (paths with spaces, renames), malformed records,
  decoration letters/colours, blame porcelain with reused commit headers and `--contents`, relative times, splicing one hunk, line endings.
* `tests/CsCode.Panels.Git.Tests/RepositoryTests.cs` (real repositories in temp dirs): discovery spelled as the workspace spells it (macOS
  `/var`), from a subfolder, nested repositories; status kinds; decorations and folder propagation; stage/unstage/discard/commit/amend; commit all;
  initial repository; branches; push/pull/fetch with ahead/behind against a local bare remote; network errors; quick-diff original and its change
  event; hunk staging and unstaging; blame; the real file watcher refreshing status and history; coalescing; remote folders; missing git; disabling.
* `tests/CsCode.Panels.Git.Tests/NetworkTests.cs`: the network environment (batch mode without askpass, the askpass variables with it, the
  user's ssh command appended to or left alone, no HTTP low-speed limit, ssh options the user's ssh configuration sets (`Include`d files, `-F`)
  left out), `setsid` (busybox's applet and util-linux's, on Linux) giving a new session and the exit code of what it runs, and real git against
  a fake ssh (`core.sshCommand` with `-F /dev/null`, a script that records what it got): batch mode, an askpass answer reaching ssh and the
  helper ended afterwards, the setting turning askpass off, git in a process group (session) of its own, a hanging operation stopped at
  `git.networkTimeout`, a `git.networkTimeout` beyond what a timer takes meaning no limit. `tests/CsCode.Remote.Tests` checks `AskpassService`.
* `tests/CsCode.Panels.Git.Tests/SourceControlWindowTests.cs` (headless): rows and stage/unstage, Ctrl/Cmd+Enter commit, Commit All prompt,
  discard confirmation, the versions each Open Changes compares, Enter/Space on rows, status bar items, command registration and error surfacing,
  branch create/switch, Select for Compare, Compare with Saved, Git: Blame, the remote message, view-model disposal.
* `tests/CsCode.Editor.Tests/SourceControlViewTests.cs` (headless): diff model, unified diff, whitespace, the diff editor's panes, colouring,
  navigation and synchronised scrolling, inline mode following a live document, the single-document mode, release of views; gutter markers
  following edits and the original, peek / revert / stage, feature gating; inline blame.
* `tests/CsCode.App.Tests/RealApp/SourceControlTests.cs`: the real composition on a git folder: status bar, Explorer decorations, the window,
  the providers, Open Changes in a diff tab, `textCompareEditorVisible`, Next Change, staging.

## What is left

* **Remote (ssh/container) folders**: git is disabled there with a message. Doing it properly means a `git/exec` request in the remote server
  (`CsCode.Server`) that runs git next to the files and streams its output, plus a `GitRunner` that targets it; the engine already goes through one
  runner, so the change is contained.
* **Editable diff panes**: both sides are read-only snapshots (the modified side follows edits made in the file's own tab). Editing in the diff
  needs the modified pane to share the open `EditorDocument` and survive that tab closing (the well closes a document when its last tab goes).
* Per-hunk stage/unstage/discard **in the Source Control window** (the gutter peek stages and reverts single changes; the engine has
  `StageRangeAsync`/`UnstageRangeAsync`); a tree view of the change lists; multi-select drag.
* Merge conflict tooling beyond listing (accept current/incoming, a 3-way merge editor); stash; tags; history/graph; `git.autofetch`.
* A dedicated `git-branch` icon (the Codicon set in `CsCode.Ui/Icons` has `source-control` only; adding icons regenerates a shared file).
* View zones (VS Code's blank filler lines) to align the side-by-side panes exactly; the scroll map interpolates instead.
