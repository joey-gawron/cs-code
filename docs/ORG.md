# Org mode (`CsCode.Panels.Org`)

Emacs's org mode, built in: `.org` files are a language of their own (colours, folding by heading, an outline), the org commands work on the
entry at the caret with org's keys, and the **Org Agenda** window, docked on the right next to Source Control, shows what is scheduled and due
this week (or today, or every entry still to do) across your org files, and lets you tick entries off, schedule them, set deadlines,
priorities and tags, clock in and out, refile and archive, capture new entries with your own templates, and read an entry's notes without
leaving the window. Habits show org-habit's consistency graph; files export to HTML and Markdown; source blocks run (org-babel). Everything
is plain org text in your files: what CsCode writes is what Emacs writes, and the other way round.

| Piece | Where | What |
|---|---|---|
| Engine (UI-free) | `src/CsCode.Panels.Org/Engine/` | `OrgTimestamp` (`<2026-11-05 Thu 10:00 +1w -2d>`, ranges, repeaters, `.+2d/4d`), `OrgDocument` (headings, planning, drawers, logbook: clock lines and state log, in-buffer settings), `OrgAgenda` (day/week agenda, TODO list), `OrgEdits` (every command as text edits), `OrgDateInput` (org's date prompt), `OrgClock` (clock lines, totals, clock table), `OrgHabit` (consistency graph), `OrgRefile` (refile, archive), `OrgCapture` (templates), `OrgExport` (HTML, Markdown), `OrgBabel` (source blocks, results, running them), `OrgStore` (the agenda's files), `OrgText` |
| Language | `src/CsCode.Panels.Org/Language/` | `org.tmLanguage.json`, `org.language-configuration.json` (shipped in `Org/` next to the app), `OrgLanguage` (folding ranges, document symbols, semantic tokens for a file's own TODO states) |
| Host, window | `src/CsCode.Panels.Org/` | `OrgHost` (commands, keys, settings, edits to open editors or files on disk, status bar, `orgHeadingAtCursor`) and `OrgHost.Features.cs` (clock, refile/archive, export, code blocks, capture templates), `OrgAgendaViewModel`, `OrgAgendaView`, `OrgSettings` |
| Tests | `tests/CsCode.Panels.Org.Tests/` | timestamps, parser, date prompt, agenda, edits, clock, habits, refile/archive, capture templates, export, code blocks (running `sh`), keyword tokens, the store on a temp folder, the host's commands, the grammar through TextMateSharp, the window headless; `tests/CsCode.App.Tests/RealApp/OrgModeRealAppTests.cs`: the real app, keys included |

DI: `services.AddCsCodeOrg()` (in `AppServices.Build`) registers `OrgHost` and the `OrgAgendaToolWindow` (id `org.agenda`). The only Core
change is additive: `INavigationService.ActiveCaretChanged` (default: never fires), which the shell's navigation service already raised.

## Files

* **`org.directory`** (default `~/org`) is where your org files live. The agenda reads the `.org` files directly in it (not in its subfolders, as
  org reads a folder), unless **`org.agendaFiles`** names files and folders of its own (`~` is your home folder). *Org: Add File to Agenda*
  (org's `C-c [`) adds the active file to that list, *Org: Remove File from Agenda* (`C-c ]`) takes it out.
* **`org.defaultNotesFile`** (default `notes.org`, relative to `org.directory`) is where *Capture* puts new entries. It and its folder are
  created the first time something is captured (or *Open Notes File* is run).
* A file open in an editor is read from the editor, unsaved text included; other files from disk. The agenda follows changes: typing in an
  org file, saving it, a file changed by another program (the folder is watched), the settings. Lock and autosave files (`.#x.org`,
  `#x.org#`) and files over 8 MB are left out.

## The Org Agenda window

*View ▸ Tool Windows ▸ Org Agenda*, the status bar's `$(checklist) N` (entries to do today, overdue included: shown when there are any), or
*Org: Agenda* (`Ctrl+C A` on a Mac, `Ctrl+Alt+O A` elsewhere).

* **Day / Week / To do**: one day, a week (from Monday: `org.agenda.startOnWeekday`; `org.agenda.span` picks the view it opens in), or every
  entry with a to-do state, file by file. ◀ **Today** ▶ moves through the days or weeks.
* A day lists, as org's agenda does: entries with a time first (by time), then deadlines that have passed (`Overdue 2 d.`), deadlines due that
  day, scheduled entries not done since an earlier day (`Scheduled 3 d. ago`), scheduled entries, deadlines coming within
  `org.deadlineWarningDays` (14; a deadline's own `-3d` wins: `Due in 3 d.`), and appointments (an active timestamp in a heading or its text,
  each day of a range `(2/3)`, each repetition of `+1w`); within each, `[#A]` before `[#B]` (no priority counts as B). Overdue things are on
  today only; done entries stay on their day, struck through (`org.agenda.showDone`).
* A row: a check mark (click: done, or back to to do), the TODO state, priority and title; under it the time or why it is there, the file's
  category and the tags, overdue and due in the error colour, coming deadlines in the warning colour.
* The **filter** keeps rows whose title, state or category contains every word; `:tag` (or `+tag`) keeps entries with that tag (inherited
  tags and `#+FILETAGS` count).
* Below the list, the selected entry: its outline path, scheduled / deadline / closed dates, tags and **its notes** (the entry's own text).
  *Open* shows it in the editor; **Write Notes** opens it on a new line after its notes, ready to type.
* `+` captures (on the selected day), the note button opens the notes file, `…` has Capture Task / Note / Event, Go to Date, Add Active File to
  Agenda, Org Settings and Refresh.

Keys in the list (org's agenda keys where org has one) and the context menu:

| Key | Does |
|---|---|
| Enter, double-click / `o` | Open the entry (`o` keeps the keyboard in the agenda) |
| Space | Mark done / back to to do (a repeating entry moves to its next date and stays to do) |
| `t` / Shift+`T` | Next TODO state / pick one |
| `s` or `>` / Shift+`D` | Schedule / deadline (org's date prompt, see below) |
| Shift+Right / Shift+Left | The row's date (scheduled, or deadline for a deadline row) one day later / earlier |
| `,` `+` `-` | Pick a priority / raise / lower it |
| `:` | Tags |
| `n` | Write notes |
| Shift+`I` / Shift+`O` | Clock in on the entry / clock out |
| Shift+`W` / `$` | Refile / archive the entry |
| `d` `w` `a` | Day / week / to-do view |
| `f` `b` `.` `j` | Next / previous / today / go to a date |
| `c` or `k`, `r` or `g` | Capture, refresh |

The context menu adds *Reschedule* (Today, Tomorrow, Next Monday, a day later or earlier), *Remove Schedule / Deadline*, *Clock In / Out*,
*Refile*, *Archive Subtree* and *Copy Title*; on a day: *Capture … on This Day* for each template, *Show This Day*. The entry the clock runs
on shows the time so far; a habit's row shows its consistency graph (below); the details add the time clocked (in all, and today).

Edits made from the agenda go to the file: in its editor when it is open (one undo step; saved afterwards when it had no unsaved changes,
`org.agenda.saveAfterEdit`), else straight to disk (its line endings and byte order mark kept; a file changed meanwhile is read again first).
The entry is found again by its line and title, so an agenda a moment out of date edits the right heading.

## Capture

*Org: Capture* (`Ctrl+C C` / `Ctrl+Alt+O C`, the agenda's `+`) picks a template, asks what it asks for and files the entry. Without
`org.capture.templates` there are four: **[t] Task** (`* TODO %^{Task}` and a creation stamp), **[n] Note** (`* %^{Title}`, a stamp, then the
file opens with the caret under it to write), **[e] Event** (`* %^{What}` with a date and time asked for) and **[j] Journal** (`* %<%H:%M>
%^{Title}` in `journal.org`, in a date tree, opened to write). Captured from a day of the agenda, a to-do entry without a date of its own is
scheduled on that day.

Your own templates (org's `org-capture-templates`, as JSON):

```json
"org.capture.templates": [
  { "key": "t", "description": "Task", "template": "* TODO %^{Task}\n%U" },
  { "key": "m", "description": "Meeting", "template": "* %^{Meeting} :meeting:\n%^T\n%?", "file": "work.org", "headline": "Meetings" },
  { "key": "j", "description": "Journal", "template": "* %<%H:%M> %^{Title}\n%?", "file": "journal.org", "datetree": true },
  { "key": "b", "description": "Book", "template": "* TODO Read %^{Title} %^g\n:PROPERTIES:\n:SIZE: %^{Size|paperback|ebook|audio}\n:END:" }
]
```

* **Where it goes**: `file` (relative to `org.directory`; the notes file when left out; created when missing), under `headline` (the
  first heading with that title; created at the end when missing), or with `datetree: true` under `* 2026` / `** 2026-11 November` /
  `*** 2026-11-03 Tuesday` (the agenda's day, else today; missing parts created in date order); else at the end, at the top level. The
  template's headings take the level they get there.
* **Escapes**: `%^{Prompt}` asks (`%^{Prompt|default|other}` offers choices), `%^t` `%^T` ask for a date / date and time (`%^u` `%^U`
  inactive), `%t` `%T` the day (the agenda's day, else today), `%u` `%U` now (inactive: a creation stamp), `%^g` asks for tags, `%a` a link to
  where you were (`[[file:path::line]]`), `%f` / `%F` that file's name / path, `%<%Y-%m-%d %H:%M>` the time in a C format, `%n` your user
  name, `%%` a `%`, and `%?` where the caret goes (the file opens there; without `%?` the entry is filed and nothing opens).

## Clocking

* *Org: Clock In* (`Ctrl+C Ctrl+X Ctrl+I`; agenda Shift+`I`) starts a clock on the entry: `CLOCK: [2026-11-03 Tue 14:05]` at the top of its
  `:LOGBOOK:` (created after its planning line and properties). One clock runs at a time: clocking in elsewhere clocks out first.
* *Clock Out* (`Ctrl+C Ctrl+X Ctrl+O`, from anywhere; agenda Shift+`O`) closes it: `CLOCK: [14:05 stamp]--[15:10 stamp] =>  1:05`. *Cancel
  Clock* (`Ctrl+C Ctrl+X Ctrl+Q`) removes the running line; *Go to Clocked Entry* (`Ctrl+C Ctrl+X Ctrl+J`) opens it.
* The status bar shows the running clock (`$(watch) 0:25 Write the report`, updated every 30 s); a click offers Clock Out, Go to, Cancel. A
  clock left running in an agenda file (CsCode restarted, Emacs clocked in) is picked up as the running clock.
* *Insert or Update Clock Table* (`Ctrl+C Ctrl+X Ctrl+R`) inserts org's dynamic block (`#+BEGIN: clocktable :scope file :maxlevel 2` ...
  `#+END:`): total time, then each heading down to `:maxlevel` with its subheadings' time included. `Ctrl+C Ctrl+C` in it updates it.

## Habits

An entry with `:STYLE: habit` in its properties and a scheduled timestamp that repeats is a habit (org-habit): `SCHEDULED: <2026-11-04 Wed
.+2d/4d>` means every 2 days, at most 4 apart. Marking it done moves the date on and logs `- State "DONE" from "TODO" [time]` in its
logbook (every repeating entry logs that), which is the habit's history. The agenda lists habits on today only, with the consistency graph
under the row: a cell per day from three weeks back to a week ahead, **blue** before it was due, **green** while it may be done, **yellow** on
the last day it may be done, **red** after; a dot on the days it was done; today outlined; days to come faded.

## Refile and archive

* *Org: Refile* (`Ctrl+C Ctrl+W`; agenda Shift+`W`) asks where the entry goes: any heading down to `org.refile.maxLevel` (3) of the agenda's
  files and the entry's own, or a file's top level. The subtree becomes the target's last child, its levels adjusted. To another file:
  inserted there first, then removed here.
* *Org: Archive Subtree* (`Ctrl+C Ctrl+X Ctrl+S`; agenda `$`) moves the subtree to `org.archiveLocation` (org's `%s_archive::`: the file
  `notes.org_archive` next to it, created with org's header; `archive.org::* Archived Tasks` puts it under that heading of another file;
  `::* Archive` keeps it in the file) with `ARCHIVE_TIME`, `ARCHIVE_FILE`, `ARCHIVE_OLPATH`, `ARCHIVE_CATEGORY` and `ARCHIVE_TODO`.
  `.org_archive` files open as org.
* *Toggle ARCHIVE Tag* (`Ctrl+C Ctrl+X A`) archives in place: like org, the agenda and the TODO list leave out trees tagged `:ARCHIVE:`.

## Export

*Org: Export...* (`Ctrl+C Ctrl+E`) writes the active file as **HTML** (a standalone page with light and dark styles; opened in the browser if
you choose) or **Markdown** (GitHub's; opened in the editor if you choose) next to it (`plan.org` → `plan.html` / `plan.md`). *Export to
HTML* / *Export to Markdown* skip the question. Exported: the title (`#+TITLE`), headings with their TODO state and priority (HTML: tags too),
paragraphs, emphasis, links (`file:x.org` links become `x.html` in HTML), timestamps, lists (nested, check boxes, `term :: text`), tables,
source blocks with their language, example / quote / verse blocks, fixed-width lines and rules. Left out, as org's defaults do: planning
lines, drawers, `#+KEYWORD` lines, comments, source blocks' results, `COMMENT` headings and subtrees tagged `:noexport:` or `:ARCHIVE:`.

## Code blocks (org-babel)

`Ctrl+C Ctrl+C` in a `#+BEGIN_SRC lang ... #+END_SRC` block (or on its `#+NAME:` / `#+HEADER:` lines or its results), or *Run Code Block*
(`Ctrl+C Ctrl+V E`), runs it; *Run All Code Blocks in File* (`Ctrl+C Ctrl+V B`) runs each in turn.

* **It asks first** ("Run this sh code block of notes.org on this computer?": Run / Always Run / Cancel) unless `org.babel.confirmEvaluate`
  is off (Always Run turns it off), as org's `org-confirm-babel-evaluate`.
* The code goes to a temporary file run by the language's program: `sh`, `bash`, `zsh`, `fish`, `python` (`python3`), `js` / `node`, `ts`
  (`npx tsx`), `ruby`, `perl`, `lua`, `php`, `r` (`Rscript`), `powershell` (`pwsh`); `org.babel.languages` adds or replaces them
  (`"python": "/opt/homebrew/bin/python3.12"`). On a Mac it runs with your login shell's environment (PATH included).
* Header arguments: `:dir` (the working folder; else the org file's), `:var name=value` (set before the code in the language's syntax,
  numbers as numbers), `:results silent` (output only in the Output window). A `#+HEADER:` line above the block adds arguments.
* The output becomes the block's results, replacing those it had: `#+RESULTS:` then `: line` per line, or an example block past 10 lines, as
  org writes them. Errors (standard error, the exit code) go to the **Org Babel** output channel, and a failure says so with *Show Output*.
  A block is stopped after `org.babel.timeout` (60 s), or from its progress notification.
* Results are the block's output (org's `:results output`), for every language: a Python block prints what it wants in its results.

## Editing org files

Org files get org's colours (heading levels in turn, TODO and DONE states, priorities, tags, timestamps, planning keywords, `*bold*`
`/italic/` `_underline_` `=verbatim=` `~code~` `+strike+`, links, lists and check boxes, tables, drawers, `#+KEYWORDS`, and source blocks
coloured in their own language), fold by heading (a heading folds its subtree), drawer and block, and list their headings as symbols
(breadcrumbs, *Go to Symbol in Editor*, sticky scroll). The commands act on the entry the caret is in:

| Command (palette: "Org: …") | Mac | Linux / Windows |
|---|---|---|
| Agenda / Capture... | Ctrl+C A / Ctrl+C C | Ctrl+Alt+O A / C |
| Cycle TODO State (`org.todo`) | Ctrl+C Ctrl+T | Ctrl+Alt+O Ctrl+T |
| Schedule... / Deadline... | Ctrl+C Ctrl+S / Ctrl+C Ctrl+D | Ctrl+Alt+O Ctrl+S / Ctrl+D |
| Set Priority... | Ctrl+C , | Ctrl+Alt+O , |
| Set Tags... | Ctrl+C Ctrl+Q | Ctrl+Alt+O Ctrl+Q |
| Run Code Block / Update Clock Table / Toggle Checkbox (org's C-c C-c) | Ctrl+C Ctrl+C | Ctrl+Alt+O Ctrl+C |
| Insert Timestamp... | Ctrl+C . | Ctrl+Alt+O . |
| Insert Heading / Insert TODO Heading | Option+Enter / Option+Shift+Enter | Alt+Enter / Alt+Shift+Enter |
| Promote / Demote Heading (caret on a heading) | Option+Left / Right | Alt+Left / Right |
| Promote / Demote Subtree (caret on a heading) | Option+Shift+Left / Right | Alt+Shift+Left / Right |
| Move Subtree Up / Down (caret on a heading) | Option+Up / Down | Alt+Up / Down |
| Toggle Fold of Heading (caret on a heading) | Tab | Tab |
| Clock In / Clock Out / Cancel Clock / Go to Clocked Entry | Ctrl+C Ctrl+X Ctrl+I / O / Q / J | Ctrl+Alt+O Ctrl+X Ctrl+I / O / Q / J |
| Insert or Update Clock Table | Ctrl+C Ctrl+X Ctrl+R | Ctrl+Alt+O Ctrl+X Ctrl+R |
| Refile... | Ctrl+C Ctrl+W | Ctrl+Alt+O Ctrl+W |
| Archive Subtree / Toggle ARCHIVE Tag | Ctrl+C Ctrl+X Ctrl+S / Ctrl+C Ctrl+X A | Ctrl+Alt+O Ctrl+X Ctrl+S / Ctrl+X A |
| Export... | Ctrl+C Ctrl+E | Ctrl+Alt+O Ctrl+E |
| Run Code Block / Run All Code Blocks in File | Ctrl+C Ctrl+V E / B | Ctrl+Alt+O Ctrl+V E / B |
| Set TODO State..., Insert Inactive Timestamp..., Open Notes File, Add / Remove File from Agenda, Refresh Agenda, Export to HTML / Markdown | palette | palette |

On a Mac the keys are Emacs's own (Control is free there: Cmd copies). Elsewhere Ctrl+C copies, so `Ctrl+Alt+O` stands in for org's `C-c`
prefix. The keys apply in org editors only (`editorLangId == 'org'`); the structure keys only with the caret on a heading line (context key
`orgHeadingAtCursor`), so elsewhere Alt+Up/Down still move lines, Option+Left/Right still move by word and Tab still indents.
`keybindings.json` can rebind any of them.

* **Cycle TODO State** goes none → each to-do state → each done state → none (`org.todoKeywords`, or the file's `#+TODO: A B | C` lines).
  Becoming done adds `CLOSED: [date time]` under the heading (`org.logDone`: `time` or `none`); leaving a done state removes it. An entry whose
  scheduled or deadline timestamp repeats (`+1w`, `++1m`, `.+2d`) is not closed: the timestamp moves to its next date (`+` one interval,
  `++` past today, `.+` from today), the entry stays to do, gets `:LAST_REPEAT:` and a `- State "DONE" from "TODO" [time]` line in its
  `:LOGBOOK:`, as in org.
* **Schedule / Deadline** set, change or (an emptied prompt) remove the planning line's `SCHEDULED:` / `DEADLINE:`; a new date keeps the old
  repeater and warning. **Priority**: A, B, C or none. **Tags**: words separated by blanks or colons, aligned to column 77 as org aligns them.
* **Insert Heading** adds a sibling after the current subtree (on a list item: a new item below, `- [ ]` for a check box, the next number for
  `1.`); **Move Subtree** swaps with the sibling above or below; **Promote / Demote** change the stars (a heading never goes above level 1).
* **C-c C-c** runs the code block the caret is in, updates the clock table it is in, or flips a check box `- [ ]` / `- [X]` and updates the
  heading's statistics cookie (`[1/3]` or `[33%]`).
* **Colours**: the grammar colours the usual states (TODO, NEXT, WAITING, DONE, CANCELLED, ...); the states a file defines itself
  (`#+TODO: OPEN WAIT | CLOSED`, or `org.todoKeywords`) are coloured too, through semantic tokens: to-do states as TODO, done states as DONE.

### The date prompt

Schedule, Deadline, Insert Timestamp, Go to Date and an event's date read dates as org's date prompt does, relative to today:
`today` (or `.`), `tomorrow`, `+3d` `+2w` `+1m` `-1d`, a weekday (`fri`: the next one, today included; `+2fri`: the second), `2026-11-20`,
`11/20`, `20` (the 20th, this month or next), `nov 20`, `20 nov 2027`; a time anywhere (`14:00`, `2pm`, `10:30am`, `9-11am`, `10:00-11:30`);
and after a date a repeater (`fri 10:00 +1w`, `++1m`, `.+2d`) or a deadline warning (`-3d`). An empty prompt means today, or removes the date
when the entry had one (its current value is filled in).

## Settings

| Setting | Default | |
|---|---|---|
| `org.directory` | `~/org` | Your org files' folder: captures go there, the agenda reads it unless `org.agendaFiles` says otherwise |
| `org.agendaFiles` | `[]` | Files and folders the agenda reads (a folder: its own `.org` files) |
| `org.defaultNotesFile` | `notes.org` | Where Capture adds entries (relative to `org.directory`) |
| `org.todoKeywords` | `["TODO","NEXT","WAITING","\|","DONE","CANCELLED"]` | The TODO states; a file's `#+TODO:` wins |
| `org.deadlineWarningDays` | 14 | Days before a deadline the agenda shows it on today |
| `org.logDone` | `time` | `time` adds `CLOSED: [...]` when an entry is done; `none` does not |
| `org.agenda.span` | `week` | The view the agenda opens in: `day` or `week` |
| `org.agenda.startOnWeekday` | 1 | The week view's first day (0 Sunday ... 6 Saturday; -1 today) |
| `org.agenda.showDone` | true | List entries already done on their day |
| `org.agenda.saveAfterEdit` | true | Save an open file after an agenda edit when it had no unsaved changes |
| `org.statusBar.enabled` | true | The status bar's count of what is to do today, and the running clock |
| `org.capture.templates` | `[]` | Capture templates (see Capture); empty: Task, Note, Event, Journal |
| `org.refile.maxLevel` | 3 | The deepest headings Refile offers |
| `org.archiveLocation` | `%s_archive::` | Where Archive Subtree moves entries (`file::heading`) |
| `org.babel.confirmEvaluate` | true | Ask before running a code block |
| `org.babel.languages` | `{}` | Programs that run code blocks, by language (added to the built-in ones) |
| `org.babel.timeout` | 60 | Seconds after which a running code block is stopped |

## What is not there (yet)

The agenda's time grid and log mode, clock reports across files (`:scope agenda`), effort estimates and column view, custom agenda views,
`org-attach`, tables' spreadsheet formulas and alignment, export to LaTeX / PDF / ODT and a table of contents, Babel's `:results value`,
sessions and `:noweb`, links of type `id:` / `file:` followed on click, and remote (`ssh:`) workspaces (the agenda reads local folders only).
