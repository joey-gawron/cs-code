# Macros (general, non-Vim)

Record what you do in the editor, replay it once, N times, until the end of the file, until a search fails or on every selected line, save it
under a name, bind it to a key. This is the editor-level macro feature of a VS-style editor (Visual Studio 2022 dropped its own); Vim's `q`/`@`
macros are separate (docs/VIM.md).

* `src/CsCode.Macros`: the module, Avalonia-free: step model, macros.json format, recorder, sliced player, store, `macro.*` commands.
* `src/CsCode.App/Macros`: the shell glue: keyboard-pipeline tap, editor text taps, the playback host over the active editor, the search step,
  the Vim gate, default keys. Tools > Macros is in `Menus/ShellMenus.cs`.
* Tests: `tests/CsCode.Macros.Tests` (unit, real file system and state storage in temp dirs), `tests/CsCode.App.Tests/RealApp/Macros` (the real
  composition, headless, real key events; `MacroUndoRealAppTests`, and `MacroTerminalRealAppTests` with a real sandboxed bash),
  `tests/CsCode.Editor.Tests/MacroHooksViewTests.cs` and `HeldUndoGroupTests.cs`, `CommandServiceTests` (Platform).

## Using it

| Action | Where | Default key |
|---|---|---|
| Record / stop recording | Tools > Macros > Record Macro, `macro.record` (toggles), status bar item | **Ctrl+M, Ctrl+R** |
| Stop recording or playback | Tools > Macros > Stop Recording, `macro.stop`, a click on the status bar item; **Esc** stops playback | |
| Play the last macro | Tools > Macros > Play Last Macro, `macro.playLast` (argument: a count) | **Ctrl+M, Ctrl+Enter** |
| Play N times / until end of file / until search fails / on selected lines | Tools > Macros, `macro.play` (asks, or takes an argument) | |
| Play a saved macro | Tools > Macros > Play Saved Macro, `macro.playNamed` (argument: the name), or its own command `macro.run.<name>` | `"key"` in macros.json or keybindings.json |
| Save the last macro | Tools > Macros > Save Last Macro As, `macro.save` (argument: the name) | |
| Edit saved macros | Tools > Macros > Manage Macros, `macro.edit` (opens macros.json, created with a short guide) | |

While recording, the status bar shows **"Recording macro…"** on the left (click to stop; **"Recording macro… (paused in the terminal)"** while the
terminal has the keyboard, where nothing is recorded); while a macro plays, **"Playing macro… (Esc to stop)"**. **One Undo reverses a whole
playback** (see *Undo*).
Context keys for menus and `when` clauses: `cscode.macroRecording`, `cscode.macroPlaying`, `cscode.macroHasLast`.

`macro.play` argument: `"times"`, `"untilEndOfFile"`, `"untilSearchFails"`, `"onSelectedLines"`, `"once"`, a number (times), or an object
`{"name": "x", "times": 3, "until": "endOfFile" | "searchFails", "onSelectedLines": true}` (`"name": ""` is the last macro). What is missing
is asked (which macro, when there are saved ones; how often). Example key binding:

```jsonc
{ "key": "ctrl+alt+e", "command": "macro.play", "args": { "name": "add-semicolon", "until": "endOfFile" } }
```

**Repeat modes.** *N times*. *Until end of file*: plays again while each run brings the caret closer to the end of the file (a run that deletes
lines counts); the first run that does not (typically the run on the last line) is the last one, so every line from the caret's to the last is
processed once (Notepad++'s "run until end of file"). *Until search fails*: plays until a recorded search finds nothing more (the macro needs a
search step); a run that changes nothing ends it too. *On selected lines*: once per line of the selection, the caret at the start of each line
first; a selection ending at the start of a line does not include that line; lines a run adds or removes are followed.

## What is recorded (the step model)

| Step | Recorded when | Replayed as | macros.json |
|---|---|---|---|
| `Command(id, args)` | a key chord, menu item, toolbar or status-bar button, right-click item or palette pick ran a command | `ICommandService.ExecuteAsync(id, args)`: deterministic, whatever the key bindings are later | `{ "command": "id", "args": [ ... ] }` |
| `Key(KeyStroke)` | a key press **no binding resolved** reached the focused editor's text area and is not a character (arrows, Home/End, PageUp, Backspace, Delete, Enter, Tab, Esc, Ctrl+Left...) | the key pressed on the active editor's text area (`IEditorViewController.ReplayKey`) | `{ "key": "shift+end" }` |
| `Text(string)` | characters typed or committed by an IME into an editor (consecutive ones merge) | typed character by character through the editor's typing path (`ReplayText`): auto-closing, over-typing, completion triggers as when typed | `{ "text": "abc" }` |
| `Pause(ms)` | only with `"macros.recordPauses": true`: a gap of 250 ms or more (at most 10 s) | a wait (the GUI keeps running) | `{ "pause": 250 }` |
| Search | Enter / Shift+Enter / F3 / Shift+F3 in the editor's find panel, F3 / Shift+F3 in the editor while the panel is open | `macro.find` (below) | `{ "command": "macro.find", "args": [ { "text": "foo", "regex": false, "matchCase": false, "wholeWord": false, "backwards": false } ] }` |

Not recorded: `macro.*` commands; the palette and Quick Open themselves (`workbench.action.showCommands`, `quickOpen`: the command picked in
them is recorded); opening the find panel (`actions.find`, `editor.action.startFindReplace`: the search is recorded instead); anything a command
ran itself (`ICommandService.Executed` names the callers); commands the editor starts while it handles a recorded key or text (a completion
item's own command); commands that started before recording; keys and text a modal layer (Vim) took; keys aimed anywhere but an editor (the
palette's text box, the find box, tree views); mouse actions (clicks, drags, scrolling) and Ctrl+Tab tab switching; **anything while the
integrated terminal has the keyboard** (context key `terminalFocus`, `MacroCommands.RecordingPausedWhen`): what is typed into the shell never
reaches the editor's taps anyway, and the terminal's commands are left out too, whether run by their keys (paste, select all, scroll, the
`commandsToSkipShell` keys), from a menu or the palette: a command that started while the terminal had the keyboard, or that completes while it
has it (Toggle Terminal pressed in the editor moves the keyboard there), is not recorded. Recording goes on: the status item reads
"Recording macro… (paused in the terminal)" meanwhile, the next step after returning to the editor joins the macro as if nothing happened in
between, and the time spent in the terminal is not recorded as a pause (`macros.recordPauses`). Ctrl+M, Ctrl+R works in the terminal too (a
chord reaches the key bindings, docs/TERMINAL.md), so recording can be started or stopped there. A command whose arguments
have no JSON form is not recorded (the status bar says so). Recording stops at `macros.maxRecordedSteps` steps (default 100,000) with a message.

**Search step (`macro.find`).** Searches the active editor on a snapshot off the UI thread (regex with a 2 s timeout), forward from the end of the
selection (the previous match) or the caret, backward from before the selection's start; selects the match with the caret at its start, as the
find panel does. It **never wraps around**: at the end of the file it fails, which stops the macro (and is the normal end of *until search
fails*). The find panel's own Find Next wraps; a macro's does not.

## Storage

`{UserDataDir}/macros.json` (`$CSCODE_HOME/data/macros.json` in portable/test installs, `~/.local/share/cscode/macros.json` on Linux), JSONC
(comments, trailing commas):

```jsonc
{
  "macros": [
    {
      "name": "add-semicolon",            // letters, digits, '-', '_'; runs as the command macro.run.add-semicolon
      "description": "End the line with ;",
      "key": "ctrl+alt+;",                // optional: a key binding (below keybindings.json, which can bind it too)
      "steps": [ { "key": "end" }, { "text": ";" }, { "key": "down" } ]
    }
  ]
}
```

* **Live reload**: the file is watched; saving it (in CsCode or elsewhere) re-registers `macro.run.<name>` commands and keys within ~150 ms.
  Problems are reported (a toast), never fatal: a macro with a bad step is skipped as a whole (a partial macro would do something else),
  a bad `key` loads the macro without it, a duplicate name keeps the later macro. At most 1,000 macros, 16 MB.
* **Saving** (`macro.save`) rewrites only that macro's object in the file (comments, layout and other macros stay as written; a trailing comment
  stays with its line); a file with JSON errors is never overwritten (fix it first). Reading, parsing, formatting and writing happen off the UI
  thread (I/O through `IFileSystem`, i.e. the IO lane, atomic writes).
* **The last macro** (unsaved) is kept in `IStateStorage` (`macros.last`, up to 256 KB of JSON) and comes back after a restart.
* **Palette**: every macro command has a title: "Macros: Record Macro (Start/Stop)", "Macros: Play Last Macro", "Macros: Run add-semicolon"...

## Playback: threading, slicing, errors

The player (`MacroPlayer`) runs on the UI thread and drives the same paths as the user's input: commands through `ICommandService`, keys and text
into the active editor through two additive `IEditorViewController` members. A key step raises a real key event on the text area, so the
window's handlers see it (the keyboard pipeline's tap lets it through unresolved: it was recorded because no binding took it) and then the
editor's own key handling (caret commands, completion list, snippets). The editor takes the keyboard focus first: AvaloniaEdit's commands act on
the focused text area, not the event's source (without that, a replayed key could edit another editor).

* **Slices**: a dispatcher callback runs steps until the next one (estimated by the most expensive of the last eight) would exceed **1 ms**;
  the rest is posted at Background priority, so input, rendering and Esc are handled in between. Text is typed one character per step unit and
  may be split across slices. A command that does not complete synchronously ends the slice; the next starts on the UI thread when it completes.
  Measured in the real editor (headless, machine shared with other builds, load average 2-3), a typing macro played 10,000 times: slices p50
  0.9-1.0 ms, **p99 3.6-3.8 ms** (max 13-14 ms: one step's own cost, e.g. a collection); dispatcher heartbeat p99 14-17 ms, max 20-30 ms (headless
  layout included). With a 2 ms target slices reached p99 3.8-5.1 ms, with 3 ms 5.6-5.9 ms, hence 1 ms. During a full App test run beside
  other builds the same test measured slices p99 4.3-4.6 ms and failed (wall clock: the UI thread is preempted mid-slice); alone it passes.
* **Esc** (with the keyboard focus in an editor), `macro.stop` or a click on the status item stop playback between two steps; a command that is
  still running is no longer waited for. While a macro plays, other keys and typing aimed at an editor are swallowed (they would interleave
  with the macro's); keys for a prompt a step waits for (palette, input box, dialog) work normally. Mouse input is not blocked.
* **Abort on error**: a command that throws or is not registered, a search that finds nothing, a key or text the editor cannot take (no editor,
  a read-only file, Vim switched on meanwhile) stops playback at that step; a toast says which macro, step and run, and why.
* **Runaway guard**: at most `macros.maxPlaybackSteps` steps (5,000,000) and `macros.maxPlaybackSeconds` (600) per playback, nested macros
  included; macros nest at most 8 deep (`macro.run.x` steps join the running playback: same guards, Esc stops all); a macro that runs itself,
  directly or through others, is stopped. While recording nothing plays (it would record its own playback); while playing nothing records.
* **Targets**: each step resolves the active editor anew (no editor reference is kept); a macro whose command switches the editor continues in
  the new one, one that closes the last editor stops at its next key or text step ("No editor is open").
* **Undo**: one Undo reverses a whole playback (below).

### Undo

**One Undo (Ctrl+Z, Edit > Undo) reverses a whole playback**: all its runs, nested macros included, in every editor it acted on, one undo step
per document (a playback that types into two editors leaves one step in each); Redo (Ctrl+Y) re-applies it. Typing right before or right after
the playback is a step of its own.

* **How**: when a playback starts the player asks the host for an undo scope (`IMacroHost.BeginUndoScope`); before every step
  (`IMacroUndoScope.Enter`) the shell holds an undo group of the active editor's document open (`IEditorViewController.OpenUndoGroup`, the
  first time the playback acts on that document) and marks the step as an undo step of its own inside it (`StartNewUndoStep`). The groups stay
  open across slices and close when the playback ends, however it ends (completed, a step failed, Esc / `macro.stop`, the runaway guard), when
  their document closes (a step closed the editor), and all at once when Vim is switched on (Vim groups its own changes, and the editor's Undo
  then runs Vim's `u`; with Vim on no group is held, so a command-only macro played with Vim on undoes step by step, as before).
* **Undo inside a macro** (a recorded Ctrl+Z / Ctrl+Y, which are AvaloniaEdit's own keys and so are recorded as key steps, or the `undo` /
  `redo` commands): never throws. It reverses the playback's own changes in that editor, newest first, one macro step at a time (the
  characters of one text step are one typed run), and Redo re-applies them; a new change drops what is left to redo. It never reaches the
  history from before the playback: when nothing of the playback's is left in that editor it does nothing, so the one Undo after the
  playback always brings back exactly the text the playback started from. (A recording that began with Ctrl+Z undid something from before
  the recording; replayed, that Ctrl+Z does nothing.)
* The document is dirty from the playback's first change (a close prompt or a save step sees it); a save inside the playback is honoured, and
  the saved text, being inside the playback's one undo step, is not reached again by Undo / Redo (the tab then stays dirty, as it should).
* **Bounded**: one playback's group keeps at most 100,000 changes per document (`EditorDocument.HeldUndoChangeLimit`; AvaloniaEdit keeps every
  operation of a group in memory); a playback that changes one document more often becomes one undo step for its first 100,000 changes and
  undoes step by step after that. Undo inside the playback reaches back at most the editor's undo limit (`cscode.editor.undoLimit` steps).
* Edits a command makes in documents other than the active editor's (a rename across files, a workspace edit) are their own undo steps there.

## Vim interplay (decision)

Vim emulation is **on by default**, so by default users get Vim's `q`/`@` macros, and general macros are limited while it is on:

* **Recording is refused** with a hint toast: "Vim emulation is on: record a macro with q followed by a register (a-z), stop with q, play it
  with @ and the register. To record a general macro instead, turn Vim off (Toggle Vim Mode)." with a **Turn Off Vim** button (`vim.disable`,
  for the session).
* **Switching Vim on while recording stops the recording** (keeping what was recorded, with a message). Keys Vim takes never reach the
  keyboard pipeline and its text never reaches the editor's text tap, so nothing Vim consumed can be recorded.
* **Replay of a macro that types (key or text steps) is refused** while Vim is on ("... Vim would read the macro's keys as Vim commands. Use
  Vim's own macros (q and @), or turn Vim off"); Vim switched on during playback stops it at the next typing step. Replaying through Vim's
  interception was rejected: a macro recorded without Vim replays as Normal-mode commands (`dd`, `x`...) and destroys text.
* **Command-only macros play** with Vim on (`vim.*` commands included), so saved macros of commands stay usable.

## Hooks added to other modules (additive)

* Core `ICommandService.Executed` (`IEventSource<CommandExecuted>`, default member: never fires), `CommandExecuted(Id, Args, Callers,
  StartedTimestamp)`, `EventSource.Never<T>()`. Platform `CommandService` keeps an async-local chain of running commands (the callers) and fires
  after a handler completed; `TestCommandService` does the same. *Deviation from the brief*: the brief's "source" is the callers chain plus the
  start time; no command caller can say "keybinding" or "menu", and the recorder needs only "did the user start it" (no callers, or the palette
  only) and "did it start while the editor handled a recorded key".
* Editor `IEditorViewController.TextInput` (text-input tap, raised from a tunnelling handler on the text area for unhandled text input; control
  characters ignored), `ReplayText(string)`, `ReplayKey(Key, KeyModifiers)` (default members keep other implementations compiling).
* Editor, for one undo step per playback (wave 3): `IEditorViewController.OpenUndoGroup()` and `StartNewUndoStep()` (default members: no
  group, nothing), over `EditorDocument.OpenUndoGroup()` / `HasOpenUndoGroup` / `StartNewUndoStep()` (`Documents/EditorDocument.UndoGroups.cs`):
  an AvaloniaEdit undo group held open across dispatcher callbacks, plus the document's own record of the steps made inside it, which Undo /
  Redo use while it is open (AvaloniaEdit's `UndoStack.Undo` throws with a group open and forgets its open groups). To see where a step starts
  inside the group, the typing path's undo groups now go through `EditorDocument.BeginUndoGroup` / `EndUndoGroup` (same AvaloniaEdit calls as
  before), and each view puts its own `ApplicationCommands.Undo` / `Redo` bindings in front of AvaloniaEdit's (they act only while a group is
  held, so Ctrl+Z / Ctrl+Y on the text area and `TextEditor.Undo` reach the group's steps). A save and a reload from disk know about a held
  group (dirty marker; the history clear of a reload keeps the group open). docs/EDITOR.md describes the contract.
* Module: `IMacroHost.BeginUndoScope()` (default: null) and `IMacroUndoScope.Enter()`; the shell's `MacroIntegration` implements them.
* Shell `KeyboardPipeline.Tap` (`IKeyboardTap`: `BeforeResolve` may consume a key or pass it through unresolved; `AfterResolve` sees the
  decision). *Deviation*: the brief's "pre-resolution tap" is both: a key step is only recorded when no binding resolved it, which is known after
  resolution; playback needs the pre-resolution part (Esc, replayed keys, swallowing).
* `AppServices.Build` calls `AddCsCodeMacrosShell()`; `ShellController.Initialize` creates `MacroIntegration` after the Vim adapter;
  `ShellMenus` has Tools > Macros; `CsCode.App.csproj` references `CsCode.Macros`.

## Settings

| Key | Default | |
|---|---|---|
| `macros.maxRecordedSteps` | 100000 | recording stops when full (also the most steps a macro in macros.json may have) |
| `macros.maxPlaybackSteps` | 5000000 | runaway guard |
| `macros.maxPlaybackSeconds` | 600 | runaway guard |
| `macros.recordPauses` | false | record pauses of 250 ms+ (at most 10 s each) |

## Known limits

* IntelliSense is asynchronous: a recorded Enter/Tab that accepted a completion may, on replay, run before the list has opened again and insert
  a newline instead. Type the whole word while recording (or record with `editor.quickSuggestions` off).
* Commands that ask for input (Rename, Go to Line, the palette) ask again on replay; what the user picked in them is not recorded (Go to Line's
  palette sets the caret directly, without a command).
* Mouse actions, Ctrl+Tab, clicks on tabs and files opened from Quick Open (no command runs) are not recorded. The find panel's Replace / Replace All buttons are not recorded (use a search step
  and typing). A key step is replayed unresolved: a hand-written `{ "key": "ctrl+s" }` presses Ctrl+S on the editor, it does not run Save
  (use `{ "command": "workbench.action.files.save" }`).
* One more watched directory (UserDataDir, not recursive) per CsCode process.
* Undo inside a playback does not reach history from before it, and a playback with more than 100,000 changes in one document is not one
  undo step past that point (see *Undo*).
* No multi-file playback ("on every open file"), no conditional stop besides the search, no macro editor UI besides macros.json.

## Running the tests

```bash
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.Macros.Tests
env -u DISPLAY -u WAYLAND_DISPLAY bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~RealApp.Macros"
```

The UI-budget tests (`MacroPlayerTests.Playing_ten_thousand_times...`, `MacroRealAppTests.Playing_ten_thousand_times_in_the_real_editor...`) are
wall-clock measurements: they take the best of three attempts and print every attempt; under heavy machine load they can fail like the shell's
`ResponsivenessTests`. `Slices_end_before_the_budget_with_virtual_step_costs` checks the slicing deterministically.
