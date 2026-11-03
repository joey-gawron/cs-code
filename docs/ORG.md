# Org mode (`CsCode.Panels.Org`)

Emacs's org mode, built in: `.org` files are a language of their own (colours, folding by heading, an outline), the org commands work on the
entry at the caret with org's keys, and the **Org Agenda** window, docked on the right next to Source Control, shows what is scheduled and due
this week (or today, or every entry still to do) across your org files, and lets you tick entries off, schedule them, set deadlines,
priorities and tags, capture new tasks and notes, and read an entry's notes without leaving the window. Everything is plain org text in your
files: what CsCode writes is what Emacs writes, and the other way round.

| Piece | Where | What |
|---|---|---|
| Engine (UI-free) | `src/CsCode.Panels.Org/Engine/` | `OrgTimestamp` (`<2026-11-05 Thu 10:00 +1w -2d>`, ranges, repeaters), `OrgDocument` (headings, planning, drawers, in-buffer settings), `OrgAgenda` (day/week agenda, TODO list), `OrgEdits` (every command as text edits), `OrgDateInput` (org's date prompt), `OrgStore` (the agenda's files), `OrgText` |
| Language | `src/CsCode.Panels.Org/Language/` | `org.tmLanguage.json`, `org.language-configuration.json` (shipped in `Org/` next to the app), `OrgLanguage` (folding ranges, document symbols) |
| Host, window | `src/CsCode.Panels.Org/` | `OrgHost` (commands, keys, settings, edits, capture, status bar, `orgHeadingAtCursor`), `OrgAgendaViewModel`, `OrgAgendaView`, `OrgSettings` |
| Tests | `tests/CsCode.Panels.Org.Tests/` | timestamps, parser, date prompt, agenda, edits, the store on a temp folder, the host's commands, the grammar through TextMateSharp, the window headless |

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
| `d` `w` `a` | Day / week / to-do view |
| `f` `b` `.` `j` | Next / previous / today / go to a date |
| `c` or `k`, `r` or `g` | Capture, refresh |

The context menu adds *Reschedule* (Today, Tomorrow, Next Monday, a day later or earlier), *Remove Schedule / Deadline* and *Copy Title*;
on a day: *New Task / New Event on This Day*, *Show This Day*.

Edits made from the agenda go to the file: in its editor when it is open (one undo step; saved afterwards when it had no unsaved changes,
`org.agenda.saveAfterEdit`), else straight to disk (its line endings and byte order mark kept; a file changed meanwhile is read again first).
The entry is found again by its line and title, so an agenda a moment out of date edits the right heading.

## Capture

*Org: Capture* (`Ctrl+C C` / `Ctrl+Alt+O C`, the agenda's `+`): **Task** (`* TODO title`, scheduled on the agenda's selected day when
captured from it), **Note** (`* title`, then the file opens with the caret under it to write) or **Event** (an appointment: asks for the date
and time). Each gets an inactive creation stamp, `[2026-11-03 Tue 14:05]`, as org's default templates do, and goes to the end of the notes
file.

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
| Toggle Checkbox / Update Statistics | Ctrl+C Ctrl+C | Ctrl+Alt+O Ctrl+C |
| Insert Timestamp... | Ctrl+C . | Ctrl+Alt+O . |
| Insert Heading / Insert TODO Heading | Option+Enter / Option+Shift+Enter | Alt+Enter / Alt+Shift+Enter |
| Promote / Demote Heading (caret on a heading) | Option+Left / Right | Alt+Left / Right |
| Promote / Demote Subtree (caret on a heading) | Option+Shift+Left / Right | Alt+Shift+Left / Right |
| Move Subtree Up / Down (caret on a heading) | Option+Up / Down | Alt+Up / Down |
| Toggle Fold of Heading (caret on a heading) | Tab | Tab |
| Set TODO State..., Insert Inactive Timestamp..., Open Notes File, Add / Remove File from Agenda, Refresh Agenda | palette | palette |

On a Mac the keys are Emacs's own (Control is free there: Cmd copies). Elsewhere Ctrl+C copies, so `Ctrl+Alt+O` stands in for org's `C-c`
prefix. The keys apply in org editors only (`editorLangId == 'org'`); the structure keys only with the caret on a heading line (context key
`orgHeadingAtCursor`), so elsewhere Alt+Up/Down still move lines, Option+Left/Right still move by word and Tab still indents.
`keybindings.json` can rebind any of them.

* **Cycle TODO State** goes none → each to-do state → each done state → none (`org.todoKeywords`, or the file's `#+TODO: A B | C` lines).
  Becoming done adds `CLOSED: [date time]` under the heading (`org.logDone`: `time` or `none`); leaving a done state removes it. An entry whose
  scheduled or deadline timestamp repeats (`+1w`, `++1m`, `.+2d`) is not closed: the timestamp moves to its next date (`+` one interval,
  `++` past today, `.+` from today), the entry stays to do and gets `:LAST_REPEAT:`, as in org.
* **Schedule / Deadline** set, change or (an emptied prompt) remove the planning line's `SCHEDULED:` / `DEADLINE:`; a new date keeps the old
  repeater and warning. **Priority**: A, B, C or none. **Tags**: words separated by blanks or colons, aligned to column 77 as org aligns them.
* **Insert Heading** adds a sibling after the current subtree (on a list item: a new item below, `- [ ]` for a check box, the next number for
  `1.`); **Move Subtree** swaps with the sibling above or below; **Promote / Demote** change the stars (a heading never goes above level 1).
* **Toggle Checkbox** flips `- [ ]` / `- [X]` and updates the heading's statistics cookie (`[1/3]` or `[33%]`).

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
| `org.statusBar.enabled` | true | The status bar's count of what is to do today |

## What is not there (yet)

Clocking (`CLOCK:` lines, clock reports), custom capture templates, refiling, archiving, habits, the agenda's time grid, tables'
spreadsheet formulas and alignment, export (HTML, LaTeX, Markdown), Babel (running source blocks), links of type `id:` / `file:` followed on
click, and custom TODO keyword colours (the grammar colours the usual states; a file's own `#+TODO:` states are recognised by the commands
and the agenda but not coloured). Remote (`ssh:`) workspaces: the agenda reads local folders only.
