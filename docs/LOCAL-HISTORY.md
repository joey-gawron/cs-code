# Local history and the Timeline (`CsCode.Panels.Timeline`)

VS Code's Local History and Timeline view, built in: every save of a file stores the saved file, bounded per file, and the **Timeline** tool
window lists a file's history newest first, merged by time with the git commits that changed it. Any entry can be compared with the file or
with the version before it, and put back.

| Piece | Where | What |
|---|---|---|
| Contracts (additive) | `src/CsCode.Core/History/LocalHistory.cs`, `src/CsCode.Core/Timeline/Timeline.cs` | `ILocalHistoryService`, `LocalHistoryEntry`, `LocalHistorySources`; `ITimelineProvider`, `TimelineItem`, `TimelinePage`, `TimelineContent` |
| Engine (UI-free) | `src/CsCode.Panels.Timeline/LocalHistory/` | `LocalHistoryService` (save hook, storage, renames), `HistoryFormat` (VS Code's layout), `LocalHistorySettings`, `LocalHistoryTimelineProvider` |
| Window, commands | `src/CsCode.Panels.Timeline/` | `TimelineHost` (commands, actions), `TimelineViewModel` (merge, paging), `TimelineView`, `TimelineToolWindow` |
| Git History | `src/CsCode.Panels.Git/Engine/GitTimeline.cs` | `GitTimelineProvider` (`git log --follow`), `GitLogParser` |
| Discard hook | `src/CsCode.Panels.Git/GitHost.cs` (`KeepInLocalHistoryAsync`) | Discard Changes keeps each file first |

```csharp
services.AddCsCodeTimeline();   // after AddCsCodeRemote(); AppServices does this
```

It registers `LocalHistoryService` (also as `ILocalHistoryService`, as an `IFileOperationParticipant` so entries follow renames, and with the
memory monitor), its `ITimelineProvider`, `TimelineHost` and the `timeline` tool window. The tool window resolves the host and the host the
service, so the shell enumerating the tool windows at startup is what arms the save hook and registers the commands; nothing reads the disk
until a save or the window asks. It must come after `AddCsCodeRemote()`: that call redirects the file-operation participants registered before
it to the out-of-process language host when `cscode.languageHost` is `process`. `AddCsCodeGit()` registers the git provider. Every other
service the host uses is optional.

## Storage: VS Code's layout

`{UserDataDir}/History` (`~/Library/Application Support/CsCode/History`, `~/.local/share/cscode/History`, `%APPDATA%\CsCode\History`,
`$CSCODE_HOME/data/History`), one folder per file:

```
History/
  4849c989/                 VS Code's string hash of the file's URI, in hex (negative ones start with '-': -12ac7d2b)
    entries.json            {"version":1,"resource":"file:///Users/me/project/a.cs","entries":[{"id":"k3x9.cs","timestamp":1793835329000}, ...]}
    k3x9.cs                 the file as saved: four random characters and the file's extension
    q0a2.cs
```

* `entries.json` is VS Code's: `id`, `timestamp` (ms since 1970), `source` (left out for a save, VS Code's `moved.source` / `renamed.source` /
  `undoRedo.source` ids, CsCode's own labels such as `File Restored`, or a name the user gave the entry) and `sourceDescription`. It is written
  after every change (atomically: temp file and rename), so nothing is lost when the app ends; VS Code flushes it at shutdown.
* The hash is VS Code's `hash()` over the URI string (h = h·31 + c, seeded with 149417). CsCode spells a `file:` URI from its path
  (`new Uri(path).AbsoluteUri`), so a path with characters the two escape differently (VS Code escapes `:` and more) hashes to another
  folder than VS Code's: the layout is the same, sharing a History folder with VS Code is not a goal.
* The hash is 32 bits: when a folder's `entries.json` names another file, the next of `<hash>_1`, `<hash>_2`... is used.
* A folder whose `entries.json` is missing or damaged is rebuilt from its copies (time = the copy's modification time). Entry names read from
  disk that would leave the folder (`..`, separators) are dropped.
* Entry names use lower-case letters and digits only, so two never differ by case alone (macOS volumes ignore case).

## What is stored, and when

* **Saves.** Every save of a file-backed document (`IDocumentManager.Saved`: Save, Save All, the Save of a close prompt, format-on-save, and
  `files.autoSave`, which goes through the same save path) adds an entry with the file **as written to disk** (encoding, BOM and line endings
  included), read after the save. Schemes: `file`, and remote `ssh`, `docker`, `wsl` (below). Untitled buffers have none.
* **Merge window** (`workbench.localHistory.mergeWindow`, 10 s): a new entry within that many seconds of the newest entry **of the same
  source** replaces it (same entry name, new time and content), as in VS Code. Auto save every second therefore keeps one entry per pause of
  ten seconds, and a burst of saves never pushes older history out.
* **Unchanged saves**: a save whose content equals the newest save's adds nothing (Save All of clean files; the old file of a Save As). Other
  sources (a rename, a discard, a name) are always added.
* **Limits**: `workbench.localHistory.maxFileEntries` (50) per file, the oldest deleted first with their copies; files over
  `workbench.localHistory.maxFileSize` (256 KB) are not stored (the size is checked before the file is read); `workbench.localHistory.exclude`
  (an object of glob → `true`, VS Code's `files.exclude` shape) leaves files out: a pattern is matched against the path relative to the
  workspace folder that holds the file (any folder of a multi-root workspace), an absolute pattern against the full path. Files inside the
  History folder itself are never stored. Changing a setting never touches entries already stored.
* **Renames and moves inside CsCode** (Explorer rename, drag and drop or cut/paste moves, rename operations of workspace edits: everything that
  calls the file-operation participants): the entries move with the file, and every file under a renamed folder takes its own. The file then
  gets a `File Renamed` (same folder) or `File Moved` entry, as in VS Code. Moving onto a file that has history merges both by time. Renames
  outside CsCode (a terminal `mv`, git) are not followed: the history stays under the old name, which Find Entry to Restore still lists.
* **Before CsCode overwrites a file**: Source Control's Discard Changes (`git.clean`, `git.cleanAll`, a row's Discard) stores each file as it
  is just before git restores or deletes it, as an `Undo Discard` entry (bounded: at most 5 s, then the discard goes ahead without the rest);
  restoring it undoes the discard, also for an untracked file `git clean` deleted. Restore Contents of a file that is not open stores the file
  first as `Before Restore`.
* Deleting a file keeps its history: Find Entry to Restore brings it back.

## Threading

* The save hook runs on the thread that reported the save and only **enqueues** (O(1); bounded at 4096 pending operations, beyond which the
  entry is dropped, counted in `DroppedCount` and logged). A save never waits for history (test `Saves_never_wait_for_history_IO`: 200 saves
  against a file system that never answers return in well under 200 ms and nothing reaches the disk until it answers).
* Every operation (add, read the list, rename, delete, move) is a work item on one queue with one consumer, so one file's entries are never
  changed by two operations at once. All file I/O goes through `IFileSystem`, whose local implementation runs on the I/O lane; the consumer only
  awaits it (and builds a small JSON document or a SHA-256 of ≤ 256 KB in between). The public API's synchronous part is an enqueue.
* `Changed` fires on the consumer's thread; the Timeline posts a coalesced reload to the UI thread.

## The Timeline window

`timeline` (View ▸ Timeline, View ▸ Tool Windows ▸ Timeline, `timeline.focus`), docked on the right with the Explorer and Source Control (VS
Code's Timeline sits under the Explorer): the file's name, a toolbar (Filter, pin, Refresh, Create Entry, More: Find Entry to Restore, Delete
All Entries of This File, Delete All Local History), and one virtualised list newest first. A row: icon (`history` for an entry,
`source-control` for a commit), label (the entry's source or name, the commit's subject), dimmed description (the commit's author), and how long
ago in VS Code's short form ("now", "5 mins", "2 hrs", "3 days", "1 wk", "4 mos", "1 yr"; refreshed every minute). The tooltip has the full time.

* **Which file**: the active editor's, as in VS Code. When the active tab is not a file editor (a diff tab opened from the Timeline) the file
  stays; the pin button (`timeline.toggleFollowActiveEditor`) keeps it on its file whatever the active editor. Reopened, the window shows the
  file it showed last.
* **Merging and paging**: every provider that handles the file and is not filtered out is asked for its first page (50) at once, and the rows
  are all their items sorted by time. A source with more pages contributes only down to its oldest item loaded (an older item of another source
  could interleave with its next page), and a **Load more** row asks each such source for its next page. A file change, a save, a commit or a
  checkout reloads (coalesced).
* **Filter** (VS Code's Filter Timeline): one checked item per source ("Local History", "Git History"); remembered in application state
  (`timeline.excludedSources`).
* **Click or Enter**: a local entry opens Compare with File, a commit its change (Compare with Previous). **Right-click**: Compare with File,
  Compare with Previous, Show Contents, then for an entry Restore Contents..., Rename... (F2), Delete (Delete); for a commit Copy Commit ID,
  Copy Commit Message.
* **Compare with File**: the diff editor (`IDiffEditorService`) with the entry's copy on the left and the file on the right (the open
  editor's live text, else the disk), titled `a.cs (File Saved • 04/11/2026 10:04:31) ↔ a.cs`. **Compare with Previous**: the entry before it
  ↔ the entry, or a commit's parent ↔ the commit (with renames: the parent's version under its old name); with nothing before it (the oldest
  entry, a root commit) the item alone. **Show Contents**: the item alone, read-only.
* **Restore Contents** asks first (Restore / Cancel). A file **open in an editor** gets the entry's text as **one edit** covering only what
  differs (Edit ▸ Undo takes it back; caret and folds outside the change stay), is saved, and the save is recorded as `File Restored`. A file
  that is **not open** (also one deleted since) is first stored as `Before Restore`, then written, recorded as `File Restored`, and opened.
  The text is decoded in the document's encoding (a BOM decides first).
* **Rename** gives the entry a name (its source), **Delete** deletes it after asking, **Delete All Entries of This File** the file's, **Delete
  All** (VS Code's `workbench.action.localHistory.deleteAll`) every file's local history, each after asking ("This cannot be undone").

## Commands

| Command | Title (category) | Does |
|---|---|---|
| `timeline.focus` | Focus on Timeline View (View) | shows the window (View ▸ Timeline) |
| `timeline.refresh` | Refresh (Timeline) | reads every source again |
| `timeline.toggleFollowActiveEditor` | Toggle Active Editor Following (Timeline) | pins / unpins the window's file |
| `workbench.action.localHistory.create` | Create Entry (Local History) | stores the active editor's file (as on disk) under a name typed; enabled while `workbench.localHistory.enabled` |
| `workbench.action.localHistory.restoreViaPicker` | Find Entry to Restore (Local History) | picks a file with history (most recently changed first, deleted files too), then an entry; compares it with the file, then Restore Contents |
| `workbench.action.localHistory.deleteAll` | Delete All (Local History) | every entry of every file, after asking |
| `cscode.localHistory.deleteAllOfFile` | Delete All Entries of the File (Local History) | argument: the file's URI, else the Timeline's file |
| `workbench.action.localHistory.compareWithFile` | Compare with File | |
| `workbench.action.localHistory.compareWithPrevious` | Compare with Previous | |
| `workbench.action.localHistory.open` | Show Contents | |
| `workbench.action.localHistory.restore` | Restore Contents | |
| `workbench.action.localHistory.rename` | Rename | |
| `workbench.action.localHistory.delete` | Delete | |

The per-entry commands take a `TimelineItem` or a `LocalHistoryEntry` as argument; from the palette (no argument) they act on the Timeline's
selected row. No keys are bound (VS Code binds none either). Not implemented: VS Code's Select for Compare / Compare with Selected on entries,
and `restoreViaEditor` (the Restore button in an entry's editor: CsCode's diff tabs have no title actions; Find Entry to Restore asks instead).

## Settings

| Key | Default | |
|---|---|---|
| `workbench.localHistory.enabled` | `true` | off: nothing is stored (entries already stored stay and are shown) |
| `workbench.localHistory.maxFileSize` | `256` | KB; larger files are not stored |
| `workbench.localHistory.maxFileEntries` | `50` | per file, oldest deleted first; `0` keeps none |
| `workbench.localHistory.exclude` | `{}` | glob → `true`; relative to the workspace folder, absolute patterns against the full path |
| `workbench.localHistory.mergeWindow` | `10` | seconds; same source only |

Registered by `LocalHistoryService` (`LocalHistorySettings.Properties`, with descriptions and schemas), so the Settings page lists them. All are window-scoped (a workspace
may set them); none names a program.

## Git History (SHOULD item)

`GitTimelineProvider` ("Git History", id `git-history`) answers for files inside a repository the git module found (`GitService.RepositoryFor`):
`git log --follow --format=... --name-status -z -n<k> -- <path>` on the git lane, newest first, the commit time (`%ct`, VS Code's default
`git.timeline.date`), subject, author and e-mail. `--name-status` gives each commit's path, so a version before a rename is read under its old
name (`git cat-file --filters <sha>:<path>`, as checkout writes it). **Paging**: git's `--skip` is wrong together with `--follow` once the skipped
commits pass a rename (`--skip=2` on a file renamed in its second commit returns nothing), so a page asks for the commits up to its end
(`-n skip+limit+1`) and drops the ones already shown; pages stop at 5,000 commits. A page is cancellable (the process is killed) and is cancelled
when the window moves to another file. The Timeline reloads when a repository's history moves (commit, checkout, stage: `HistoryChanged`). Not
done: VS Code's "Uncommitted Changes" item, merge commits' second parents (Compare with Previous uses the first), blame-style details in the
tooltip beyond author, time, subject and id.

## Remote files (ssh, containers, WSL): kept on this machine

VS Code keeps a remote window's local history on the remote host (`~/.vscode-server/data/User/History`). CsCode keeps it **locally**, in the same
`History` folder, keyed by the remote URI (`ssh://host/home/u/main.py`):

* CsCode's remote server has no history store and no protocol for one; keeping it local needs neither, and the history of a container's files
  outlives the container.
* The cost: after each save of a remote file, its size is asked and the file read once over the connection (only files under `maxFileSize`),
  on the history's queue, never holding the save. Restore writes through the remote file system.
* Renames inside CsCode follow (the Explorer calls the participants for remote folders too); exclusion patterns apply relative to the remote
  workspace folder. Git History is not available for remote files (the git module runs git locally only, docs/SOURCE-CONTROL.md).
* Test: `A_remote_files_history_is_kept_on_this_machine_and_follows_its_renames` (an `ssh:` file system over a local folder). Not run against a
  real ssh host.

## Memory

Per file, at most `maxFileEntries` small records are cached (256 files, least recently used dropped); the index of every file with history
(built the first time Find Entry to Restore or a folder rename needs it, then kept up to date) holds one small record per file. Both are an
`IMemoryTrimmable` ("Local history": Moderate and Aggressive trims drop them; they are read again from disk). The window's view-model exists only
while the window is shown and drops every subscription and its timer when hidden (test `A_hidden_window_keeps_neither_its_view_nor_its_view_model`,
`LeakAssert.NotRetainedAsync`). On disk the history is bounded per file (`maxFileEntries × maxFileSize`), not in total: as in VS Code, there is no
limit on the number of files, and files deleted or renamed outside CsCode keep theirs until deleted (Delete All).

## Limits and known gaps

* **Save As**: the editor reports the save under the document's old URI (it changes its identity after the write), so the new file gets no
  entry until its next save. The old file, unchanged on disk, gets none when its newest entry is a save of that content (the unchanged-save
  rule), else one entry of its current content.
* Writes CsCode makes to files that are not open (language-server rename edits of closed files through workspace edits) add no entry: the
  shell writes those files itself, without a save.
* A save during which the file changes again on disk records what is on disk when the queue reaches it (the later content).
* Pending operations are not finished at exit: the consumer stops when the service is disposed (a save in the last moment may not be recorded).
* Folder names differ from VS Code's for URIs the two spell differently (see Storage); entries.json written by VS Code is read (same format).

## Tests

* `tests/CsCode.Panels.Timeline.Tests/LocalHistoryServiceTests.cs` (real `LocalFileSystem` on its own I/O lane, temp folders, a manual clock):
  VS Code's hash values, the layout and `entries.json`, merge window (also by setting and across sources), unchanged saves (also after a
  restart), pruning at `maxFileEntries` (copies deleted, `0` keeps none), exclusions (relative, absolute, `false`), `maxFileSize`, disabled
  history, untitled and History-folder files, **saves never wait for history I/O**, persistence across a restart (names, ids, times, merge with
  the restored newest entry), a damaged `entries.json`, a hash collision, renames and moves of files and folders (and after a restart), a move
  onto a file with history, rename / delete / delete all, the next save's source, the resource list order, unsafe ids, a remote (`ssh:`) file,
  trimming.
* `tests/CsCode.Panels.Timeline.Tests/TimelineTests.cs` (headless window): rows newest first with relative times, following saves, the active
  editor, no editor and the pin; merging two sources by time with paging and Load more, the filter and its state; Compare with File / Previous /
  Show Contents (with an argument and on the selected row); Restore of an open document (asks, one edit, saved, `File Restored`) and of a file
  not open (`Before Restore`); Rename, Delete, Create Entry, Delete All of a file and of all files; Find Entry to Restore (a deleted file);
  command ids and menus; Enter; `LeakAssert` of view and view-model; relative times; the minimal restore edit and decoding.
* `tests/CsCode.Panels.Git.Tests/GitTimelineTests.cs`: the log parser (renames, merges), a real repository's history of a renamed file in pages
  of two (the case `--skip` gets wrong), each version by its old name, previous versions, untracked files, cancellation, `Changed` after a
  commit, and Discard Changes keeping each file (modified and untracked) before it is overwritten or deleted.
* `tests/CsCode.App.Tests/RealApp/LocalHistoryTests.cs`: the real composition: a save of a real editor document is stored, the Timeline window
  lists it, Restore Contents is one undo step in the real editor, an Explorer rename takes the entries along and the Timeline follows.

```
dotnet test tests/CsCode.Panels.Timeline.Tests
dotnet test tests/CsCode.Panels.Git.Tests --filter "FullyQualifiedName~GitTimelineTests"
dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~RealApp.LocalHistoryTests" --blame-hang-timeout 5m
```
