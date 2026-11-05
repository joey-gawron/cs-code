# Spell checking (`CsCode.SpellCheck`)

CsCode checks spelling itself: nothing to install, and it reads the settings of VS Code's *Code Spell Checker* (cspell) and a project's
`cspell.json`, so a project set up for cspell keeps working. Misspelt words are diagnostics like a language server's: underlined, listed in
the Error List, marked on the overview ruler, fixed from the lightbulb. In code only comments and strings are read, and the editor's own
syntax tokens say which text that is.

Contents: [1 Using it](#1-using-it) · [2 Design](#2-design) · [3 Dictionaries and licences](#3-dictionaries-and-licences) ·
[4 Settings](#4-settings) · [5 Commands](#5-commands) · [6 Vim](#6-vim) · [7 Threading and memory](#7-threading-and-memory) ·
[8 False positives on this repository](#8-false-positives-on-this-repository) · [9 Tests](#9-tests) · [10 Limits](#10-limits)

## 1. Using it

* **What is read**: comments and strings in code; all text in prose (Markdown, plain text, commit messages, org, AsciiDoc); not code,
  not a Markdown code span or link target, not the XML of a C# documentation comment. Identifiers too with `cscode.spellCheck.checkIdentifiers`.
* **The squiggle**: an *Information* diagnostic (`cSpell.diagnosticLevel`) with cspell's message, `"recieve": Unknown word.` (a word in
  `cSpell.flagWords`: `"hte": Forbidden word.`), owner and source `cSpell`. The Error List lists it; the ruler marks it in its right lane.
* **Quick fixes** (lightbulb, Ctrl+., right-click > Quick Actions and Refactorings...): up to 5 replacements in the word's own case
  (`Recieve` → `Receive`, `RECIEVE` → `RECEIVE`; the first is preferred), **Add "word" to user dictionary** (`cSpell.words` in the user
  settings.json), **Add "word" to workspace dictionary** (with a folder open: the project's `cspell.json` when it has a JSON one, else
  `cSpell.words` in `.cscode/settings.json`), **Ignore "word" in this file** (a `cspell:ignore word` comment on the first line — after a `#!` line,
  an XML declaration or Markdown front matter — or the word appended to the file's existing `cspell:ignore`; offered only where the language has comments, never in JSON). Adding a word checks every
  open document again at once, before the settings write lands.
* **Status bar** (right, beside the language): `$(check) Spell` when the active file is checked and clean, `$(book) Spell: 3` with three
  unknown words (`3+` when the file hit `cSpell.maxNumberOfProblems`), `$(circle-slash) Spell` when it is not checked (spell checking off, a
  language not enabled, an ignored path, a large file); the tooltip says which, and lists what could not be used (a YAML config, a bad pattern).
  A click runs **Toggle Spell Checking**.
* **The Source Control commit message box** is checked too (as prose, language id `scminput`, cspell's for that box): misspelt words are
  underlined 300 ms after typing stops, and right-clicking one offers its replacements, Add to User Dictionary, Cut, Copy and Paste
  (docs/SOURCE-CONTROL.md). A commit message opened in an editor (`git-commit`) is a document like any other.
* **In-document directives** (cspell's, in any comment): `cspell:ignore w1 w2` and `cspell:words w1 w2` accept words in this file,
  `cspell:flagWords w` forbids one, `cspell:disable` ... `cspell:enable` stop checking for the lines between, `cspell:disable-next-line`
  and `cspell:disable-line` for one line. The prefixes `cSpell:`, `spell-checker:` and `spellchecker:` work too.

## 2. Design

```
 CsCode.Core          ISyntaxClassificationService (new, additive): per line, runs of Code / Comment / String / Text / Ignored
                      ITextSpellChecker (new, additive): free text (the commit message box) through the same checker
 CsCode.Editor        SyntaxClassifier: that service over the token store (TextMate rows, and tree-sitter rows mapped onto TextMate scopes)
 CsCode.SpellCheck    (Core + WeCantSpell.Hunspell only, no Avalonia)
   SpellCheckService  documents → debounced, versioned checks on its own lane → IDiagnosticService (owner "cSpell"); IMemoryTrimmable
   CheckRun           one check of one text: which runs to read, WordSplitter, settings + directives + dictionaries; sliced
   SpellDictionarySet the Hunspell word lists cSpell.language chose + the software terms; verdict cache
   SpellSettingsLoader cSpell.* from every settings file + the project's cspell.json → immutable SpellSettings
   SpellCheckHost     ICodeActionProvider (quick fixes), the cSpell.* commands, the status bar item
 CsCode.Vim           ]s [s z= zg zw → the checker's commands (gated on 'spell')
 CsCode.Panels.Git    CommitMessageSpelling: underlines and a spelling menu over the commit message box (ITextSpellChecker)
 CsCode.App           AddCsCodeSpellCheck(); ShellController starts SpellCheckHost
```

**Which text is a comment, a string or prose** comes from the editor's token rows, not from a lexer of the checker's: each interned scope
stack is reduced once to a kind (`SyntaxScopeRules`), innermost scope first, as the editor already decides comment/string for auto-closing:

| Scope (innermost that says something) | Kind |
|---|---|
| `constant.character.escape`, `constant.other.placeholder`, `constant.character.format`, `string.regexp`, `markup.underline.link`, `markup.inline.raw`, `markup.raw`, `markup.fenced_code`, `meta.tag`, `entity.name.tag`, `entity.other.attribute-name`, `meta.preprocessor.include`, JSDoc tags and names, Markdown front matter | Ignored |
| `comment.*` | Comment |
| `string.*` — but Ignored when a `comment` scope is outside it (an XML doc attribute value: `cref="..."`) or an import (`meta.import`, `meta.export`, an include) | String |
| `meta.embedded`, `meta.interpolation`, `meta.template.expression` (a Markdown fence's code, `{x}` in an interpolated string) | Code |
| the root `source.*` / `text.*` | Code / Text |

So the Markdown grammar's root `text.html.markdown` makes paragraphs prose while a fence's comments and strings are read like any code's,
and the git-commit grammar's root makes a commit message prose. Tree-sitter rows carry the same scopes (its captures are mapped onto
TextMate's, docs/TREE-SITTER.md), so the same rules serve both engines. Neovim's `@spell` / `@nospell` captures stay dropped
(`CaptureScopes.Skip`): the painter replaces a node's scope when a second capture names the same node, so mapping them to a scope would
uncolour comments; the captures they accompany (`@comment`, `@string`, `@markup.raw`, `@markup.link.url`) already map to scopes the rules
above read. A language with no grammar at all is read whole if it is a prose language (`cscode.spellCheck.proseLanguages`) and not at all
otherwise.

**Words** (`WordSplitter`): text splits into chunks at spaces, quotes, brackets and most punctuation; a chunk that is a URL, an e-mail
address, a path (`src/a.cs`, `./x`, `~/x`, `C:\x`) or a hex number (`0xFF`, `#a0b1c2`) is skipped; the rest splits at everything that is not
a letter (digits, `_`, `-`, `.`, `::`) and at case changes: `XMLHttpRequest` → XML Http Request, `parseUtf8Bytes` → parse Utf Bytes,
`snake_case` → snake case. `URLs` / `APIs` stay one word and are checked without the `s`; a possessive (`parser's`) without its `'s`.
Words shorter than `cSpell.minWordLength` (4) are not checked, so the parts that digits leave (`utf`, `x86`'s `x`) are not either. A
word with a letter of a script other than Latin, Greek or Cyrillic is skipped (CJK has no spaces to split at).

**Why identifiers are off by default**: they are abbreviated on purpose (`ctx`, `cfg`, `impl`, `Utf8`), they come from libraries and
generated code the user cannot rename, and a misspelt identifier is usually misspelt at every use, so one choice would bring dozens of
squiggles to code the compiler already accepts. Comments, strings and prose are where a misspelling reaches a reader. cspell itself
checks identifiers; `cscode.spellCheck.checkIdentifiers: true` does too (split as above).

**Large files**: a document over `cscode.editor.largeFileThresholdBytes` (the editor's large-file mode, 5 MB) is not checked, nor one the
editor opened in large-file mode for a long line; only the first `cSpell.checkLimit` KB (500) of any file are read, and only the first
10,000 characters of a line.

## 3. Dictionaries and licences

| What | Where | Licence |
|---|---|---|
| **Hunspell engine**: WeCantSpell.Hunspell 7.0.1 (aarondandy), a managed C# port of Hunspell, NuGet, used unmodified | `Directory.Packages.props`, SPELL CHECK section | MPL-1.1 / GPL-2.0 / LGPL-2.1 tri-licence, as Hunspell; CsCode uses it under **MPL-1.1** (file-level copyleft: the package's own files stay under the MPL and their source is public at <https://github.com/aarondandy/WeCantSpell.Hunspell>; CsCode's files are not affected). Not permissive in the MIT sense; chosen because it is the managed Hunspell the brief names, has no native code, and is used as a binary dependency only. Recorded in THIRD-PARTY-NOTICES.md |
| **en_US dictionary** (bundled, embedded in `CsCode.SpellCheck.dll`): LibreOffice's `en/en_US.dic` + `.aff`, "en_US Hunspell Dictionary Version 2020.12.07" (SCOWL size 60, 49,568 entries), fetched from <https://github.com/LibreOffice/dictionaries> | `src/CsCode.SpellCheck/Dictionaries/` with its `README_en_US.txt` verbatim | SCOWL's permissive licence (Kevin Atkinson and the listed authors: use, copy, modify, distribute and sell with the notice kept), the affix file under Geoff Kuenning's BSD licence (Ispell), plus public-domain sources; the README's notices travel with the files and in THIRD-PARTY-NOTICES.md |
| **Software terms** (bundled): CsCode's own list of about 1,150 programming words and language keywords (`async`, `config`, `json`, `nullable`, `enum`, `stdout`, `regex`, `constexpr`, `kwargs`, POSIX calls and signals, well-known tools) | `Dictionaries/software-terms.txt` | CsCode's own |
| More languages | `{UserDataDir}/dictionaries/*.dic` + `.aff` (watched: a pair dropped in is used at once) | whatever the dictionary's own |

`cSpell.language` picks the dictionaries, comma separated: `"en"` is the bundled en_US (cspell's meaning), `"en-GB"` / `"en_GB"` a file
named `en_GB.dic`, `"de"` the first `de_*` dictionary (`de_DE` before `de_AT`); a user `en_US.dic` replaces the bundled one. An entry no
dictionary matches is named in the status bar tooltip and otherwise ignored. `tools/spellcheck/fetch-dictionaries.sh [name:folder...]`
downloads LibreOffice dictionaries (default `en_GB:en`) into `.scratch/dictionaries/` (git-ignored) to copy from.

## 4. Settings

Code Spell Checker's settings, read from user, workspace (`.vscode/settings.json`, `.cscode/settings.json`) and folder settings and live
(every change re-checks the open documents):

| Setting | Default | Notes |
|---|---|---|
| `cSpell.enabled` | `true` | |
| `cSpell.language` | `"en"` | see §3 |
| `cSpell.words`, `cSpell.userWords` | `[]` | accepted, any case. **Merged from every settings file**, as cspell merges them (not the top layer only), plus the project config's `words` |
| `cSpell.ignoreWords` | `[]` | never reported (merged likewise) |
| `cSpell.flagWords` | `[]` | always reported as forbidden; `"hte->the"` (or `"hte:the"`) offers the replacement first (merged likewise) |
| `cSpell.enabledLanguageIds` | cspell's list plus CsCode's languages (68 ids) | the languages checked |
| `cSpell.enabledFileTypes` / `cSpell.enableFiletypes` | `{}` | both shapes, both names: `{ "*": true, "json": false }` or `["*", "!json"]`; `*` = every language |
| `cSpell.ignorePaths` | cspell's (`node_modules`, `package-lock.json`, `.git/objects`, ...) | gitignore-like globs relative to the workspace folder (a pattern without `/` matches any path segment) |
| `cSpell.ignoreRegExpList` | `[]` | `"/pattern/flags"` (JavaScript form; `i m s x` honoured), a bare expression, or a pattern name: `Urls`, `Email`, `HexValues`, `HexDigits`, `UUID`, `Base64`, `CommitHash`, `SpellCheckerDisable`. Each match has a 250 ms timeout; a pattern .NET rejects is reported in the tooltip |
| `cSpell.minWordLength` | `4` | |
| `cSpell.diagnosticLevel` | `"Information"` | `Error`, `Warning`, `Information`, `Hint` |
| `cSpell.maxNumberOfProblems` | `100` | per file |
| `cSpell.maxDuplicateProblems` | `20` | per word per file |
| `cSpell.checkLimit` | `500` | KB of a file that are read |
| `cSpell.numSuggestions` | `5` | cspell's default is 8; the brief asks for 5 |
| `cscode.spellCheck.checkIdentifiers` | `false` | §2 |
| `cscode.spellCheck.proseLanguages` | `plaintext, text, markdown, git-commit, org, asciidoc, scminput` | read whole when the editor has no grammar for them |
| `cscode.spellCheck.delay` | `400` | ms of quiet after an edit before the file is checked again |

**A project's cspell config**: the first of `.cspell.json`, `cspell.json`, `.cSpell.json`, `cSpell.json`, `cspell.config.json`,
`.vscode/cspell.json`, `.vscode/cSpell.json`, `.vscode/.cspell.json` in a workspace folder (cspell's order), JSON with comments. Read:
`words`, `ignoreWords`, `flagWords`, `ignorePaths`, `ignoreRegExpList` (added to the settings'), word-list files named by
`dictionaryDefinitions` that `dictionaries` lists (or that say `addWords`; `.txt`/`.dic`, one word per line), and `language`, `enabled`,
`minWordLength`, which apply where the settings keep their defaults. Watched: an edit applies at once. **YAML configs** (`cspell.yaml`,
`cspell.config.yml`, ...) are **not read** — CsCode has no YAML reader as a dependency; one found without a JSON config is named in the
status bar tooltip. Not read either: `import`, `overrides`, `languageSettings`, `patterns`, `dictionaries` that are not word lists (cspell's
own bundled dictionaries such as `typescript` or `companies` do not exist here).

## 5. Commands

| Command | Title (palette category *Spell*) |
|---|---|
| `cSpell.toggleEnableSpellChecker` | Toggle Spell Checking (writes `cSpell.enabled` to the workspace settings when a folder is open, else the user's) |
| `cSpell.enableForWorkspace` / `cSpell.disableForWorkspace` | Enable / Disable Spell Checking for Workspace |
| `cSpell.enableForGlobal` / `cSpell.disableForGlobal` | Enable / Disable Spell Checking (User) |
| `cSpell.addWordToUserDictionary` | Add Word to User Dictionary (argument: the word; none: the misspelt word at the caret, else the word there) |
| `cSpell.addWordToWorkspaceDictionary` | Add Word to Workspace Dictionary (same) |
| `cSpell.goToNextSpellingIssue` / `cSpell.goToPreviousSpellingIssue` | Go to Next / Previous Spelling Issue (argument: a count; wraps around; selects the word) |
| `cSpell.suggestSpellingCorrections` | Spelling Suggestions... (a quick pick of the replacements and the two Add actions for the word at the caret) |
| `cscode.spellCheck.flagWord` | Mark Word as Misspelled (Flag Word): adds to the user's `cSpell.flagWords` and removes it from the user's `cSpell.words` (cspell has no such command; Vim's `zw`) |

No default key bindings (cspell has none either); bind them in `keybindings.json`.

## 6. Vim

In the Vim engine (docs/VIM.md), Normal mode: `]s` / `[s` (with a count; `]S` / `[S` the same, CsCode reports no "rare" words) run
Go to Next / Previous Spelling Issue; `z=` runs Spelling Suggestions...; `zg` adds the word at the cursor to the user dictionary; `zw`
flags it. `]s` and `[s` need Vim's `spell` option (`E756: Spell checking is not enabled` without it); `z=`, `zg` and `zw` work either way,
as in Vim. `spell` follows the setting **`vim.spell`** (default `true`, because the checker is on by default; plain Vim's default, off, is
the engine's own when no settings service is used) and `:set spell` / `:set nospell` (session-wide like every option, docs/VIM.md).
`zG` and `zW` act as `zg` and `zw` (there is no session-only word list); `zug`, `zuw`, `zuG`, `zuW` and `spelllang` are not supported.
Not differential-tested against Neovim: these keys drive CsCode's checker, not Neovim's.

## 7. Threading and memory

* **Nothing on the UI thread but an enqueue.** A document change, a token change (`ISyntaxClassificationService.Changed`), a settings
  change: each bumps the document's generation and restarts its timer (`cscode.spellCheck.delay`); the timer posts the check to the spell
  lane (`cscode-spell`, one dedicated below-normal thread, not the pool). There the check takes the document's text and a capture of the
  token rows (a copy of row references under one lock), drops itself if the version moved, and runs in slices of at most 4 ms, re-posted
  between slices so a newer generation (or another document) takes over: **latest wins**. A result is published only if the document still
  has the version it was computed for; otherwise the newer check publishes. Suggestions (quick fixes, `z=`) run on the same lane.
* **Dictionaries load on the first check**, on the lane; the first check of a session waits for them, nothing else does.
* **Per document** the service keeps a timer and the last result (at most `cSpell.maxNumberOfProblems` issues); both go when the document
  closes (its diagnostics are cleared). The service is `IMemoryTrimmable` ("Spell checker dictionaries"): Moderate drops the verdict cache,
  Aggressive the word lists (they load again on the next check).

Measured (Release build, arm64 Mac, load average 14–24 from other agents' builds; `CsCode.SpellCheck` called as the lane calls it, in a fresh
process, three runs):

| What | Result |
|---|---|
| First load of the bundled en_US + software terms (embedded resources, JIT included) | 79–81 ms |
| Managed memory it keeps (GC heap after full collections) | 11.3 MB (working set +14.6 MB); `EstimatedBytes` reports 10.6 MB |
| A check of 5,000 comment lines, 45,000 words, 10,000 issues | 24–41 ms on the lane in 6–8 slices; 1 MB allocated (the issues) |
| The comments of this repository's `src/` (878 files, 185 K words; §8) | 0.2 s of checking, i.e. about 1 µs per word |
| Suggestions for one word (`recieve`) | 40 ms the first time |
| The caller's share of a keystroke, while the lane is held inside a check (`Typing_does_not_wait_for_the_checker`) | a lock and a timer change: p99 < 0.5 ms asserted over 2,000 calls; 200 real edits of a 700 KB document meanwhile, median < 10 ms asserted (the document's own copy of its text dominates) |

## 8. False positives on this repository

`SpellCheckSourceTreeTests` tokenises every C# file under `src/` with the real C# grammar and checks their **comments** (strings and code
not counted) with the bundled en_US and the software terms, every word reported (no per-file caps). Report:
`.scratch/spellcheck/src-comments-<dictionaries>.txt` (every flagged word, its count and first place).

| Dictionaries | Comment words checked | Flagged | Distinct | Per 1,000 words |
|---|---:|---:|---:|---:|
| en_US + software terms, before the terms were extended from this measurement | 184,643 | 3,076 | 817 | 16.7 |
| **en_US + software terms (shipped)** | 185,028 | 1,899 | 492 | 10.3 |
| en_US + LibreOffice en_GB + software terms | 185,028 | 1,160 | 330 | 6.3 |

(Last run: 881 files, 195,599 lines, 27,560 of them with a comment; tokenising them took 29 s, checking their comments 0.2 s. The first row
was measured on the same tree before this module's own files were added, 878 files.)

What remains: this repository is written in **British English** (`colour`, `behaviour`, `tokenised`, `honour`, `centre`, `grey`: the
difference between the two rows); **names of this project and its tools** (`cscode`, `Avalonia`, `netcoredbg`, `debugpy`, `basedpyright`,
`codelldb`, `askpass`, font names), which belong in a project's own `cspell.json` (`words`), not in a list every project gets; **Vim option
names and terminal escape names** quoted in comments (`hlsearch`, `unnamedplus`, `DECSET`); code quoted in comments (`DEscending`,
`-Madds`). Read through by hand, the list held **no real misspelling** in `src/` comments (the occurrences of `recieve`/`wierd` are this
module's own examples). The terms added from the first run are software words any project has (`parser`, `glyph`, `minimap`, `debounce`,
`dockable`, `rescan`, POSIX calls and signals, `pytest`, `venv`, `systemd`...), never a misspelling: `Common_misspellings_are_not_hidden_by_the_software_terms`
checks ~100 common misspellings, many of programming words (`seperator`, `paramter`, `retreive`, `intialize`, `lenght`), are still reported.
The test fails above 15 per 1,000 words with en_US alone.

## 9. Tests

```
dotnet test tests/CsCode.SpellCheck.Tests                                                  # engine, service, settings over the real Platform
dotnet test tests/CsCode.Editor.Tests --filter "FullyQualifiedName~SpellCheck"             # the editor's real TextMate tokens; §8's measurement
dotnet test tests/CsCode.Vim.Tests --filter "FullyQualifiedName~SpellKeyTests"            # ]s [s z= zg zw and 'spell'
dotnet test tests/CsCode.Panels.Git.Tests --filter "FullyQualifiedName~CommitMessageSpellingTests"   # the commit message box
dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~SpellCheckRealAppTests" --blame-hang-timeout 5m   # the real app
```

Last runs (arm64 Mac): CsCode.SpellCheck.Tests 93 passed; the editor's `SpellCheck` filter 5 passed (the source-tree measurement ~30 s of
it); `SpellKeyTests` 4 passed (the whole CsCode.Vim.Tests: 3,493 passed, 11 skipped); `CommitMessageSpellingTests` 3 passed (the whole
CsCode.Panels.Git.Tests: 64 passed, 1 skipped); `SpellCheckRealAppTests` 3 passed.

* `WordSplitterTests`: identifier splitting (camel, Pascal, snake, kebab, digits, acronyms, `URLs`), URLs, e-mail, paths, hex, scripts,
  case-matched suggestions. `DictionaryTests`: en_US, possessives, plural acronyms, the software terms, the typo guard, suggestions in the
  word's case, `cSpell.language` choices, user dictionary files. `CheckRunTests`: only comments and strings in code, identifiers with the
  setting, Ignored runs, prose with and without tokens, minimum length and digits, words / ignoreWords / flagWords in any case,
  `ignoreRegExpList`, in-document directives, the per-word and per-file caps, the check limit, slicing.
* `SpellCheckServiceTests` (Core's real registries, a fake classifier): diagnostics (owner, source, severity, message, range, Error List),
  settings live, re-check after edits, disabled language / large file / spell checking off, no tokens until the editor has some,
  ignore-path matching, **a check of older text is dropped** (a classifier that holds the lane mid-check while the text changes: nothing
  computed for the old text reaches the diagnostics), **typing does not wait for the checker** (200 edits while the lane is blocked; the
  check runs on `cscode-spell`), **`LeakAssert.NotRetained`** for a closed document with its state and result and for a disposed
  checker, lazy dictionaries and the aggressive trim, quick fixes (case, preferred, apply), add to user dictionary (clears every open
  document), ignore in this file (shebang, joining an existing directive, Markdown, none for plain text), next/previous with counts and
  wrapping, `z=`, flag word, toggle and the status bar.
* `PlatformSettingsTests` (the real configuration service, workspace, file system and watcher over a temp CSCODE_HOME and project): words
  merged from user, `.vscode`, `.cscode` and `cspell.json` (with a `dictionaryDefinitions` word list), the config's ignore paths / patterns /
  flag words / `minWordLength`, a YAML config reported, add to user dictionary edits settings.json in place and keeps the workspace's list
  apart, add to workspace dictionary writes `cspell.json` (formatting kept) or `.cscode/settings.json`, an edited `cspell.json` and a
  dictionary dropped into the user folder apply live.
* `SpellCheckTokenTests` (the editor's real tokens): C# (not the namespace, the doc-comment attribute values, an identifier, an interpolated
  `nameof`, a path; yes a regex-looking string argument), Python (docstrings, comments, f-strings without their braces), Markdown (prose, not
  code spans, fences' code or links), and a re-check when tokens arrive. `SpellCheckSourceTreeTests`: §8.
* `SpellKeyTests` (Vim): the keys, counts, E756 and `:set spell`, the `vim.spell` default. `CommitMessageSpellingTests` (headless): the
  underlines (two lines, gone while the text differs from the checked one), the menu's replacement and Add to User Dictionary, nothing
  checked off-window. `SpellCheckServiceTests.Free_text_is_checked_whole_with_offsets_into_it`: the `ITextSpellChecker` behind the box.
  `SpellCheckRealAppTests` (the real app, Vim on): squiggles from real tokens, the status bar, `]s ]s [s`, `z=` replacing the word, `zg`
  writing settings.json, Ctrl+. listing and applying a spelling fix, the Source Control window's message box getting the spelling layer.

## 10. Limits

* **Only lines the editor has tokens for are checked in code** (and in Markdown): the token store holds the lines shown plus the 3,000 below
  the furthest point scrolled to, while a view is open. A long file's far end is checked when it is scrolled to, and a code file no view has
  shown is not checked at all (its status says so). Next / previous issue therefore move among the issues found so far. Plain text and
  other prose without a grammar is checked whole, view or not.
* The tokens of a line edited a moment ago may be the previous version's until the editor tokenises it again (a few milliseconds); the
  re-check that the new tokens trigger corrects it.
* YAML cspell configs, `import`, `overrides`, `languageSettings`, cspell's own bundled dictionaries (`typescript`, `companies`, ...) and
  `cSpell.customDictionaries` are not read; `cSpell.userWords` is read but nothing writes it (Add to User Dictionary writes `cSpell.words`,
  as the brief asks).
* Compound words are not accepted as a whole: a user word `WeCantSpell` is matched part by part (`We`, `Cant`, `Spell`), like any identifier.
* Words of scripts other than Latin, Greek and Cyrillic are skipped.
* Neovim's `@spell` / `@nospell` captures are not used (§2). reStructuredText under tree-sitter has no root scope, so only its comments
  and strings would be read (it is not a prose language by default).
* The commit message box (§1) is a plain text box: its underlines are drawn by a layer over it, so they follow the box's layout and scrolling
  but not its selection highlight, and its spelling menu replaces the box's own menu only over a misspelt word. Its words have no quick-fix
  lightbulb, no Error List entries and no Ignore in This File (there is no file).
* The repository has no `cspell.json` of its own; the project words of §8 would go there.
