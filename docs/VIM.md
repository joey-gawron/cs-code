# CsCode.Vim — Vim emulation

`CsCode.Vim` is a Vim engine for the CsCode editor: modal editing, operators x motions x text objects, registers, marks, dot-repeat, a Vim
undo tree, search, Ex commands, mappings and macros. It is verified against real Neovim (v0.12.5 was used) with a differential test suite.
It depends only on `CsCode.Core` (no Avalonia). The shell's Vim adapter (`src/CsCode.App/Vim`, [Integration](#integration-the-adapter-in-the-shell))
connects it to every editor view of the real application, where it is **on by default**.

* Source: `src/CsCode.Vim` — `Engine/` (the `VimEngine` partial class), `Abstractions/` (`IVimHost`, `IVimBuffer`), `Hosting/` (headless host,
  `LineVimBuffer`, timers), `Config/` (options, session, settings), `Registers/`, `Search/` (Vim regex -> .NET), `Editing/` (marks, undo tree).
* Adapter: `src/CsCode.App/Vim` (`VimIntegration`, `EditorVimHost`, `EditorVimBuffer`, `VimKeyPolicy`, `VimShellActions`, `VimClipboard`) over the
  editor's `EditorModalSurface` (`src/CsCode.Editor/View`).
* Tests: `tests/CsCode.Vim.Tests` (xUnit v3) for the engine; `tests/CsCode.App.Tests/RealApp/Vim` and `VimKeyPolicyTests` for the adapter in the
  real application. Lab tool: `tools/vim/lab.sh`.

## Using Vim in CsCode

Vim keys work in every editor as soon as CsCode starts. The status bar shows the mode on the left (`-- NORMAL --`, `-- INSERT --`,
`-- VISUAL BLOCK --`, `recording @q`), the keys of a command being typed (`2d`), the `:` `/` `?` command line with its cursor, and Vim's messages
and errors (until the next key; several lines, like `:reg`, go to the **Output › Vim** channel). The caret is a block in Normal and Visual mode, a
bar in Insert mode and an underline in Replace and Operator-pending mode.

* **Turning it off**: the command **Toggle Vim Mode** (`vim.toggle`, also the *Vim* item at the right of the status bar) switches it for the
  session; the setting `"vim.enable": false` switches it off for good (`vim.enabled` is the older spelling and works too). Off means the plain
  editor at once: its caret, its keys and no Vim status items; on again creates engines for the open editors.
* **Ctrl keys.** In Normal, Visual and Operator-pending mode Vim owns `Ctrl-D U F B E Y R O I V W A X N P [` and `Esc` (as VSCodeVim does), so
  Visual Studio's bindings of those keys (Ctrl+D duplicate line, Ctrl+F find, Ctrl+O open, Ctrl+W close, Ctrl+N new, Ctrl+P quick open...) do
  not run there. In Insert mode Vim takes `Esc Ctrl-[ Ctrl-W U H T D R O N` and leaves `Ctrl+V` (paste), `Ctrl+A`, `Ctrl+X`, `Ctrl+C`, `Ctrl+F`
  and **`Ctrl+P` (Quick Open)** to the editor; with the completion list open `Ctrl-N`/`Ctrl-P` move through it. The command line takes every
  Ctrl+letter except `Ctrl+S`/`Ctrl+Q`. Everywhere the application keeps `Ctrl+Shift+…` chords (`Ctrl+Shift+P` the palette), `Alt+…` (menus),
  F-keys (debugging, F12...), `Ctrl+S`, `Ctrl+Z` (which is Vim's `u`, see below), `Ctrl+K`/`Ctrl+M` chords, `Ctrl+Space`, `Ctrl+Tab`, and the
  clipboard keys `Ctrl+C`/`Ctrl+Insert` (copy), `Shift+Delete` (cut) and `Shift+Insert` (paste; not on Vim's command line). `Ctrl+X` and `Ctrl+V`
  are Vim's (decrement, visual block) outside Insert mode; in Insert mode they cut and paste. `Ctrl+Shift+V` (paste from the clipboard ring)
  is the application's.
  Decision on Ctrl+P: it is Vim's (`k`) in Normal/Visual mode as the brief's list says, and Quick Open in Insert mode (outside the completion
  list), where Vim's own `Ctrl-P` is the least used; `Ctrl+Q` (the palette, plain text = go to file) works everywhere.
* **`vim.handleKeys`** (VSCodeVim format) gives any key to the editor or to Vim: `"vim.handleKeys": { "<C-d>": false, "<C-p>": false, "<C-c>": true }`.
  `false` = the application/editor handles it, `true` = Vim does (also for keys Vim does not take by default, like `<C-c>` or `<A-j>`).
* **Insert mode** types through the editor, so IntelliSense, auto-closing brackets, snippets, signature help and language-aware Enter work as
  without Vim. While the completion list is open `Tab`, `Enter`, the arrows and `PageUp/Down` belong to it and the first `Esc` closes it (the second
  leaves Insert mode); in a snippet `Tab`/`Shift+Tab` move between placeholders and `Esc` ends the snippet and Insert mode together.
* **Undo**: `u`/`Ctrl-R` and the editor's Undo/Redo (Ctrl+Z, Ctrl+Y, menu, toolbar) are the same thing: one Vim change is one undo step everywhere,
  and undoing back to the saved text clears the dirty marker. A change made before Vim was switched on is undone by the editor itself.
* **Your vimrc**: `~/.vimrc` is **not** read (it may use plugins and commands this engine does not have). CsCode reads its own
  `{UserDataDir}/vimrc` if it exists; `"vim.vimrc.path": "~/.vimrc"` (VSCodeVim's setting) opts into another file, `"vim.vimrc.enable": false`
  turns vimrc loading off. Errors are listed, never fatal.
* **Language keys**: `gd` definition, `gD` declaration, `gi` implementation (nvim-lspconfig's convention, replacing Vim's own `gi`), `gr` references,
  `K`/`gh` hover, `]d`/`[d` next/previous problem, `g.` quick fix; a vimrc or settings binding of the same keys wins.
* **Mouse**: a selection made with the mouse becomes Visual mode; a click in Visual mode ends it.

## Integration: the adapter in the shell

**Placement.** ARCHITECTURE §2 lets feature modules reference Core only, the editor reference Core + Avalonia/AvaloniaEdit only, and makes
`CsCode.App` the one project that references everything. The adapter needs both the engine and the editor, so it lives in `CsCode.App/Vim`; the
editor offers a Vim-agnostic hook surface (`EditorModalSurface`, `CodeEditorControl.Modal`) and the engine its host abstraction (`IVimHost`,
`IVimBuffer`). `AppServices.Build` calls `AddCsCodeVim()`; `ShellController.Initialize` creates `VimIntegration` (only when the composition has
the Vim module and the real editor) right after the keyboard pipeline.

**Lifecycle.** One `VimEngine` per editor view, created when the view is created (`EditorViewFactory.ViewOpened`) and disposed with it
(`ViewClosing`, raised first thing in `DisposeView`, while the view is intact; a view destroyed by something its own engine started is torn down
when the engine call returns). One `EditorVimBuffer` per open document, shared by its views, so the undo tree, marks and jump list survive the
shell destroying and recreating inactive tabs' views; it is released when the document closes. The `VimSession` (registers, search, mappings,
macros, options) is the module's singleton; named registers persist in `IStateStorage` across restarts (`vim.persistRegisters`).

**Keys.** `MainWindow`'s tunnelling `KeyDown` and `TextInput` handlers offer every event to `IShellKeyInterceptor` (the adapter) before the
keyboard pipeline. Only events whose source is a Vim view's text area are considered, never while the shell is in the middle of a chord.
Characters (and IME commits) arrive as **text input** and go to `HandleText`, so keyboard layouts and input methods are respected; key presses
(special keys, Ctrl/Alt chords) go to `HandleKey` when `VimKeyPolicy` gives them to Vim. A key the engine passes through continues to the key
bindings and the editor. When Vim takes a key press the editor never sees it, and a control character some platforms send as text after
Enter/Tab/Backspace is swallowed with it.

**Insert mode through the editor.** `IVimHost.InsertTypedText` types each character the user typed through `TextArea.PerformTextInput` (auto-close,
completion/signature triggers, snippets, Enter with the language's indentation); the buffer reports what the editor changed, with the lines it
replaced, and the engine adopts it into the insert (undo, `.` register, change list). Replays (dot-repeat, macros, mapping expansions, counts,
`Ctrl-R`) insert the recorded text exactly, as in Vim — a dot-repeat therefore does not auto-close a bracket the original typing auto-closed.
Keys left to the editor in Insert mode (accepting a completion, a paste) change the document inside the insert's undo group.

**Undo.** `EditorVimBuffer` opens one editor undo group per Vim change (lazily at the first change of a key, continuing the previous group while
the change continues the same Vim step, e.g. an Insert session) and puts a marker in it that reports the step when the editor undoes or redoes the
group. `u`/`Ctrl-R` ask `CanUndoRedoStep`: when the editor's history holds exactly that step on top, the editor undoes it (history, caret and dirty
marker stay in step); otherwise (a foreign change in between, a step that resumed after one, undo-tree branches like `g-`) the engine applies the
step as edits. The marker identity check and the engine's text check after a host undo (`SpliceOverlay.Matches`) make a wrong picture of the
history cost only that fallback. The editor's Undo/Redo commands run `u`/`Ctrl-R` (`<C-o>u` in Insert mode). Changes made outside any key (a
formatter, a workspace edit) become undo steps of their own (or join an Insert session). One key's (or one slice's) edits are one atomic
document change, so the editor's features react once, not once per line.

**What `IVimHost` maps to:**

| `IVimHost` | Real editor / shell |
|---|---|
| `Buffer`, `GetCaret`/`SetCaret`, `SetSelection` | `EditorVimBuffer` over `EditorDocument`; the editor caret; stream, whole-line and rectangular (`RectangleSelection`) selections; the caret goes into view |
| `OnModeChanged` | caret shape (`EditorModalSurface.CaretShape`: block / bar / underline / outline drawn on the caret layer) and the `vim.mode` status item |
| `OnShowCommand`, `OnRecordingChanged`, `OnMessage`, `OnCommandLineChanged` | status items `vim.showcmd`, `vim.mode` ("recording @q"), `vim.cmdline` (command line with its cursor, VSCodeVim-style, or the last message; `:s///c` prompts there); multi-line messages also go to Output › Vim |
| `OnSearchHighlight`, `OnYankHighlight` | match highlight in every view's visible lines (regex with a timeout, capped); a 250 ms flash |
| `GetViewport`, `SetTopLine`, `GetDisplayRows`, `WrapWidth`, `WindowWidth` | the text view's scroll position, line height and wrapping |
| `TryGetClosedFold`, `FoldCommand` | the editor's folding regions (`zo zc za zO zC zR zM`; `zf zd zE zj zk` are not supported: the editor computes its regions) |
| `ReadClipboard(char)`, `ReadClipboardType(char)`, `WriteClipboard(char, …)` | `VimClipboard` over the shell's `IClipboardService`. `+` is the clipboard, written as the editor's Copy writes it: into the clipboard ring, with CsCode's shape metadata (linewise = whole lines, blockwise = a block, one piece per row) and the document's line ends; read back with `\n` line ends and the type when known (CsCode's own yank exactly, the editor's whole-line and box copies; other applications' text: a final newline means linewise, as in Vim). `*` is the PRIMARY selection where the platform has one (Linux X11, also under XWayland; offered lazily) and the clipboard elsewhere, as in Vim. `vim.useSystemClipboard` and `clipboard=unnamedplus` mirror the unnamed register to `+`, `clipboard=unnamed` to `*`. Reads come from a cache: both registers on window activation, `+` on editor focus, and the named one after `"+`/`"*` (the following keys wait — at most 500 ms — for the read). The older `ReadClipboard()`/`WriteClipboard(text, type)` members, which hosts without the register name implement, are the defaults of the new ones |
| `PerformAction` | the document well, after the key (`VimShellActions`, table below) |
| `ExecuteCommand` | `ICommandService`, after the key, fire and forget; after a navigation the cursor goes to the start of the target in Normal mode |
| `RequestSyntaxRanges` | the document's tree-sitter tree: `EditorModalSurface.RequestSyntaxObjects` runs nvim-treesitter-textobjects' query on the syntax lane and the answer comes back as an engine call of its own (`VimIntegration.PostToEngine`); false at once for a TextMate document |
| `GetCommentTokens`, `FormatLines`, `RequestCompletion` | the language configuration's comment tokens; the language server's range formatter (asynchronous: the result is a change of its own; without one the engine re-indents); the editor's IntelliSense (`Ctrl-N/P`) |
| `Timer` | `ThreadingVimTimer` posting to the UI dispatcher (`timeoutlen`) |
| `RunPendingWork` | Background-priority dispatcher callbacks of 3 ms engine time while `HasPendingWork` |

**Windows and buffers** (a Vim window is an editor group, a buffer an editor tab): `:w` saves through the shell (will-save participants, format
on save), `:w {file}` writes a copy, `:wa`, `:wq`/`:x`; `:q` closes the editor tab — with the shell's Save / Don't Save / Cancel prompt when it is
dirty — and **never the application, also on the last editor** (CsCode is a workbench; File > Exit closes it); `:q!` closes without asking,
`:qa[!]` closes every editor; `:e {file}` (relative to the current file's folder), `:e!` reloads, `:enew`, `:sav {file}`; `:bn :bp :b N :b name
:bd[!] :b#`; `:ls` is a quick pick of the buffers (picking switches); `:sp :vsp [file] :new :vnew` split into a new group below / to the right,
`:only` closes the other groups' editors, `:close`; `Ctrl-W h j k l` move to the group in that direction, `w W p t b` cycle, `s v n` split, `c q`
close, `o` only. Resizing and moving windows (`Ctrl-W = _ | + - < > H J K L x r`) are not supported (groups are arranged by dragging).

**Engine API used by hosts** (all optional, see `IVimHost`/`IVimBuffer`): `InsertTypedText`, `TryTakeExternalChanges` (`VimExternalChange`),
`CanUndoRedoStep`/`UndoRedoStep`; queries `UndoSequence`, `NextChangeUndoSequence`, `IsUndoStepOpen`, `IsApplyingUndo`, `CanUndo`, `CanRedo`,
`UndoHistoryGeneration`; `SelectVisual` (a mouse selection). For any other host the minimal contract still holds:

```csharp
services.AddCsCodeVim();                         // needs IConfigurationService and ICommandService; uses IStateStorage/IAppPaths/IOutputService if present
var vim = provider.GetRequiredService<IVimService>();   // resolving it registers vim.* settings and commands, loads the vimrc and saved registers
var engine = vim.CreateEngine(myHost);           // one engine per editor view; the caller disposes it
VimKeyResult r = engine.HandleKey(keyStroke);    // or HandleKey(int) / HandleText(string) for IME / typed text; PassThrough = the editor's key
```

`IVimHost` mandatory members: `Buffer`, `GetCaret`, `SetCaret`, `Timer`; everything else has a harmless default. `InMemoryVimHost` implements all
of it over an `InMemoryTextDocument` (or any `IVimBuffer`) and records what the engine told it. `LineVimBuffer` is a fast `List<string>` buffer.
Engines of one `VimSession` share options, registers, mappings, search memory and the plugin switches; undo history, marks and the jump list are
per buffer. Events: `ModeChanged`, `MessageChanged`, `RecordingChanged`, `SearchChanged`, `PendingWorkChanged`. State: `Mode`, `PendingKeys`,
`Cursor`, `IsRecording`, `LastMessage`, `LastError`. `Reset()` abandons any pending command and returns to Normal mode; `Detach()` releases host
and buffer; `Dispose()` does both.

**Adapter tests** (`tests/CsCode.App.Tests/RealApp/Vim`, the real composition, real key events into the real window): editing matrix (motions
with counts, operators and text objects, Visual char/line/block with block insert/append, `.`, `u`/`Ctrl-R` against the editor's Undo/Redo and the
dirty marker, registers and `+`/`*`, `/ n N` with highlights, `:s///c`, macros incl. recursive, mode labels/showcmd/caret shapes, IME text, scrolling
and folds), shell (`:w :e :b :ls :q :q!`, language keys through a fake provider, on/off live, Ctrl-key ownership and app chords, `vim.handleKeys`),
IntelliSense and snippets in Insert mode, mouse selection, a cross-check of 68 table cases (up to four per group of eighteen) against the headless engine
and the Neovim-blessed expectations, a 300-editor open/edit/close retention test, view eviction with history kept, registers across restarts and
`vim.vimrc.path`, typing latency on vs off and `:g` over 20k lines in slices.

## Threading and isolation (ARCHITECTURE section 6)

* The engine runs **synchronously on the caller's (UI) thread** inside `HandleKey`. Every callback into the host happens on that thread
  (`ThreadingTests` verifies it from a worker thread). The engine starts no threads, tasks or timers; the only asynchrony is the host's `IVimTimer`
  for `timeoutlen`. No file or network I/O, nothing is awaited. `ExecuteCommand` is fire-and-forget; nothing waits for language servers.
* Typed keys that arrive while work is pending are queued and run, in order, after it.
* **Syntax text objects and motions** (`af if ac ic aa ia ]m [m ]M [M`) need the document's syntax tree, which lives on the editor's syntax lane:
  `IVimHost.RequestSyntaxRanges` is asynchronous. The engine suspends the command as sliced work that waits for the host's answer instead of for
  time (nothing blocks; `HasPendingWork` stays false meanwhile, so nothing polls), queues the keys typed meanwhile and runs them after it, in
  order; the answer (on the UI thread) resumes the command. No answer within `VimEngine.SyntaxRequestTimeout` (3 s), or a text that changed
  meanwhile, ends it with a message; `Ctrl-C` abandons it. The object is the count-th smallest enclosing the cursor (Visual mode: enclosing more
  than the selection, so repeating grows it); when none encloses the cursor, the next one after it (nvim-treesitter-textobjects'
  `lookahead`). The ranges are nvim-treesitter-textobjects' `@function`, `@class` and `@parameter` captures (`.outer` for `a`, `.inner` for `i`;
  the queries are bundled with the grammars, docs/TREE-SITTER.md), so what an object covers per language is that project's choice (Kotlin's
  `]m` stops at a primary constructor; its `aa` on a declared parameter takes no comma, on a call argument it does). As in Vim, a delete from
  the indentation to a line end over several lines takes the whole lines. On a TextMate document they fail with one message (a macro stops
  there). Inside `:normal` and `:g` nothing can wait (they run their keys to completion within one command): the real editor's answer is
  always later, so there they fail with a message (macros, mappings and `.` are fine).
* Big operations are cooperative: `:s` (also with `c`), `:g`, `:v`, `:sort` and their work generators yield every few milliseconds
  (`SliceBudget`, default 3 ms; the first slice runs inside the key that started it). `dG`, `:%s` and `gg=G` compute against the buffer and apply
  ONE batched `ApplyEdits`. Measured on a 100k-line buffer (unoptimised test build, one developer machine): `:%s/a/b/g` longest call 36 ms in 17 calls;
  `dG` 9 ms; `gg=G` 17 ms; `:g/a/d` and `:v/a/s/b/c/` a few ms per slice; `:sort` 39 ms. Per key on a 1 MB buffer: motions about 1 microsecond and no
  allocation, `x`/`dd`/insert typing 2-20 microseconds and 0.3-0.9 KB (a new line string plus the old line for undo), `}` scans the buffer when it has no blank lines.
* Runaway guards: a macro or mapping may execute at most `MacroKeyBudget` (20 M) keys / `MacroTimeBudget` (10 s) per typed key; recursive mappings stop
  at depth 1000 (`E223`); a recording longer than 1 M characters is dropped.
* **Isolation:** an engine and its session are single-threaded objects. Separate sessions/engines on separate threads are independent
  (`Engines_on_different_threads...` test). Sharing one session between threads is not supported.
* Bounded memory: jump list and change list 100 entries each, undo by `undolevels` and a 48 M character cap (oldest steps are dropped), register size
  `vim.maxRegisterChars`, macro recording 1 M characters, persisted registers 64 KB each / 512 KB total. After `Dispose` neither the engine nor its buffer is kept alive
  by the session (`LeakTests`); after `Detach` the engine holds no reference to the buffer.

## Configuration (`vim.*`, VSCodeVim-compatible, live-reloaded)

| Setting | Default | Notes |
|---|---|---|
| `vim.enable` | `true` | master switch (`vim.enabled` is the older spelling: Vim is off when either is false); `vim.toggle/enable/disable` change it for the session |
| `vim.useSystemClipboard` | `false` | unnamed register uses the host clipboard (register `+`, not the `*` selection) |
| `vim.leader` | `"\\"` | `<leader>` in bindings (vimrc: `let mapleader=`) |
| `vim.hlsearch` / `incsearch` / `ignorecase` / `smartcase` / `wrapscan` | `false/true/true/true/true` | VSCodeVim's defaults; plain Vim defaults apply when no settings service is used |
| `vim.startInInsertMode` | `false` | new engines start in Insert mode |
| `vim.timeout` | `1000` | `timeoutlen` in ms (a boolean turns `timeout` on/off) |
| `vim.handleKeys` | `{}` | `{"<C-c>": false}` passes that key to the editor, `true` gives it to Vim (see [Using Vim](#using-vim-in-cscode)) |
| `vim.{normal,insert,visual,operatorPending,commandLine}ModeKeyBindings` and `...NonRecursive` | `[]` | `{"before": ["j","j"], "after": ["<Esc>"], "commands": [{"command": "...", "args": []}]}`; a command starting with `:` is an Ex command |
| `vim.cursorStylePerMode.{normal,insert,replace,visual,visualline,visualblock,operatorpending,commandline}` | block/line/underline/... | `block line underline block-outline line-thin underline-thin` |
| `vim.autoSwitchInputMethod` | `{}` | accepted, ignored |
| `vim.persistRegisters` | `true` | registers `a`-`z` (and so macros) are stored in `IStateStorage` under `vim.registers` |
| `vim.maxRegisterChars` | `8000000` | register size cap |
| `vim.vimrc.enable` / `vim.vimrc.path` | `true` / `""` | default file: `{UserDataDir}/vimrc` (CsCode's own); `~/.vimrc` is read only when named here |
| `vim.surround` `vim.commentary` `vim.sneak` `vim.easymotion` `vim.camelCaseMotion` `vim.argumentObjects` `vim.replaceWithRegister` `vim.highlightedyank` `vim.targets` | `true true false false false false false true false` | boolean or `{"enable": bool}`. Only surround and commentary are implemented; the others are parsed and stored for later |

Changing a setting touches only what it names (a runtime `:set scrolloff=7` survives an unrelated settings change). Bindings from settings are
replaced as a group; bindings from the vimrc are replaced when it is reloaded. The vimrc supports the Ex subset below (`set`, the `map` family, `let`,
`source`, comments, `\` continuation lines); errors are collected (`IVimService.VimrcErrors`: `line 5: E492: ...`) and never fatal.

Commands: `vim.toggle` (*Toggle Vim Mode*), `vim.enable`, `vim.disable`, `vim.showCheatSheet` (writes the "Vim Cheat Sheet" output channel).
The adapter also syncs `tabstop`/`shiftwidth`/`expandtab` from the editor's tab settings when a view takes the keys (a `:set ts` lasts until
then) and turns `autoindent` on before the vimrc is applied.

## What is supported

| Group | Supported |
|---|---|
| Modes | Normal, Insert, Replace, Visual (char/line/block), Operator-pending, Command-line (`: / ?`), Insert-Normal (`Ctrl-O`); cursor shape per mode |
| Counts, registers | counts before and inside operators; `" 0 1-9 - a-z A-Z _ . : / % + *`, shift rules, linewise/charwise/blockwise types, `:registers` |
| Motions | `h j k l w W b B e E ge gE 0 ^ $ g_ \| gm gM f F t T ; , % ( ) { } [[ ]] [] ][ H M L gg G {n}G {n}% + - _ <CR> <BS> <Space>`, marks, `/ ? n N * # g* g#` with counts and offsets, `gj gk g0 g$`, `gn gN`, `go`, `Ctrl-O/Ctrl-I`, `g; g,`; `o_v o_V o_CTRL-V` forcing and the exclusive/linewise rules |
| Operators | `d c y > < = g~ gu gU g? gq gw zf` (+ doubled forms), `x X D C s S Y p P gp gP ]p [p J gJ ~ r R .`, `u Ctrl-R U`, `Ctrl-A/Ctrl-X` (decimal/hex/octal/binary, visual `g Ctrl-A`), `& g&` |
| Text objects | `iw aw iW aW is as ip ap`, brackets, quotes, `it at`, counts, nesting, Visual extension; from the syntax tree (tree-sitter documents, below): `af if` function, `ac ic` class, `aa ia` argument/parameter |
| Syntax motions | `]m [m` next/previous function start, `]M [M` next/previous function end (counts, after operators, in Visual mode; jumps) |
| Visual | `v V Ctrl-V gv o O`, operators, block `I A c $ r J > <` incl. ragged lines, `v_p`, `:` with `'<,'>`, `{count}v` / `{count}V` |
| Insert | `i a I A o O gi gI`, counts, `Ctrl-W U H R{reg} O V K(digraphs) T D [ A @ E Y`, completion hook, autoindent/smartindent basics, Replace-mode backspace, `.` register, undo granularity like Vim (`Ctrl-G u`, cursor keys) |
| Undo | undo tree: `u Ctrl-R U :undo N g- g+ :earlier :later`; the Visual area and marks are restored with undo |
| Search | Vim regex translated to .NET (`\v \V \m \M \< \> \{n,m} \+ \? \= \| \( \) \%( \) \zs \ze \_s \_. [[:alpha:]] \d \w \s \a \l \u \c \C ~ \1-\9 \@= \@! \@<= \@<! \%^ \%$ \n`, multi-line patterns), `ignorecase smartcase wrapscan incsearch hlsearch`, offsets `/pat/e+1;/pat2/`, `cpoptions` `c`, `:noh` |
| Ex | ranges (`. $ % 'a '<,'> /pat/ ?pat? +n -n ; ,`), `:w :q :q! :wq :x :wa :qa :e :enew :bn :bp :bd :ls :b :sp :vsp :new :only :close :sav` (via the host), `:d :y :pu :m :t :co :j :> :< :normal :norm! :s :& :&& :~ :g :v :g! :noh :reg :marks :jumps :changes :undo :redo :earlier :later :k :mark :delm :sort :retab :left :right :center :set/:setlocal :let :echo :execute :source :@ :silent :keepjumps :help` |
| `:s` | flags `& c e g i I n p # l r`, any delimiter except letters/digits/`"`/`\`/`\|`, `~ & \0-\9 \u \l \U \L \e \E \r \n \t`, multi-line matches, interactive `c` (y/n/a/q/l/Esc; the prompt shows the result of every answer), Vim's empty-match rules |
| Mappings | `map nmap imap vmap xmap smap omap cmap` + `noremap` variants, `unmap`, `mapclear`, `<leader> <localleader> <C-x> <A-x> <S-x> <CR> <Esc> <Space> <Tab> <BS> <Del> <Up> ... <F1-12> <Plug> <silent> <nowait> <Nop>`, recursion limit 1000, `timeoutlen` disambiguation of prefixes through `IVimTimer`, a mapping whose rhs starts with its lhs does not recurse |
| Macros | `q{a-z0-9}`, `qA` append, `@{reg}`, `@@`, `{n}@a`, `@:`, `:@a`, `:{range}normal @a`, `Q` (last recorded); registers hold Vim's key encoding (`<80>kb`) so `"ap`, `:let @a=` and `"Ayy` edit macros as text and round-trip exactly; recursive macros, abort on the first error (also of an enclosing count), macros under `:g`/`:normal`, macros record typed keys not mapping expansions; one undo step per `@a` |
| Plugins | vim-surround (`ys{motion}{char}`, `yss`, `ds`, `cs`, visual `S`, `<tag>`/`t`, `(` vs `)` spacing) and vim-commentary (`gc{motion}`, `gcc`, `gC` block, visual `gc`, comment tokens from the host) |

### Not supported (documented)

`:!` and `:r !` (no shell), `:read`, `\=` and `<expr>` (no expression evaluator beyond string/register arithmetic in `:let`/`:echo`), regex atoms `\%V \%# \%'m \%23l \%23c \%23v \& \z( \%C`,
`Ex mode (gQ)`, `Ctrl-X` completion sub-modes (stubbed to the host hook), `q:` command-line window, spell, diff, quickfix, window-local options (options are per session), abbreviations,
the `sneak/easymotion/camelCaseMotion/argumentObjects/replaceWithRegister/targets` emulations, `:s` backwards-range prompt ("Backwards range given, OK to swap").

## Known differences from Neovim

The differential suite (`DifferentialTests`) runs the table cases through `nvim --headless --embed` and compares text, cursor, mode, registers,
marks (relative to the initial state) and error codes. Last run: **3124 cases run through nvim, 247 diverged, 247 explained, 0 unexplained**; the stored
expectations (blessed from nvim) match nvim for every case that is not listed. Every explained divergence is a line in
`tests/CsCode.Vim.Tests/Cases/known-divergences.txt`. By category:

* **`'` (previous-context) mark rules** after Ex commands, `:g`, `:s` and undo (about 100 cases): the engine sets the mark where Vim's `setpcmark()` is
  called for the command; nvim reports it unset in some of those situations (for example after `:3`) while still honouring `<C-o>`. The cursor is not affected.
* **`'< '>` marks** after a range that was deleted or moved, and **`'[ '] '.`** after `:g/pat/d`, `:v`, `:m`, `:t`, `:normal` inside `:g` and their undo (about 110 cases).
* **Registers** `:` (command line run by a mapping), `.` (insert interrupted by a cursor key) and the recorded text after `f,` (nvim_input adds an internal key code: an oracle artifact).
* **Error codes** in a handful of cases (`/~` is `E383` in nvim, `E33` here).
* **Cursor details**: after undoing `:g/pat/m0`, `:%s/\n//` (column of the joined line), keys that stop a recording while they are a prefix of a mapping,
  `Q` in Visual mode, `jVK`/`VJ` mappings that use `gv=gv`, dot-repeat of an insert whose mapping ends in `<Left>`.
* `visual-042` (`'>` after a linewise Visual change) and earlier entries of the same file.

Intentional differences: the `vim.*` defaults for `ignorecase`/`smartcase` follow VSCodeVim (on), `:normal` takes raw keys like Vim, `ysiw` and friends need the
plugin switches, options are session-wide even for `:setlocal`.

## Running the tests

```
dotnet test tests/CsCode.Vim.Tests                      # everything (nvim cases are skipped with a reason when nvim is not installed)
VIMTEST_BLESS=1 dotnet test tests/CsCode.Vim.Tests --filter FullyQualifiedName~Bless   # write nvim's result into cases without '<' lines
tools/vim/lab.sh 'foo ‸bar\nbaz' 'dw'                   # run keys through nvim and the engine and print both (build once: dotnet build tools/vim/VimLab)
```

Neovim is only started as `~/.nix-profile/bin/nvim` (or `CSCODE_NVIM`, or `PATH`) with `-u NONE -i NONE -n` and temp files, never touching a user config.
`.vimtest` format (one case per `=== name [nodiff]` header): `set:`/`ex:` setup, `>` initial buffer (`‸` marks the cursor), `keys:` in Vim notation, `<` expected
buffer, optional `mode:`, `reg a c: text`, `mark a: 1:5`, `err: E486`. Groups: motions, delete/change/yank/replace/join/shift, text objects, visual/block, insert, undo, dot, registers,
marks, **macros** (counts, recursion, abort-on-error, append, `:normal @a` ranges, macros with search/`f`/text objects, special keys in registers), search, substitute, ex, mappings, and the hand-written
`plugins` group (`nodiff`: nvim has no surround/commentary). Other suites: fuzz and properties (`u` restores, `.` equals retyping, counts, 10k sequences x 50 keys never throw or corrupt, every
`ApplyEdits` is validated for surrogates/CRLF/tabs/wide characters/empty and unterminated buffers), mappings with a fake timer, the Ex parser (ranges, flags, delimiters, error codes), settings/vimrc/persistence/DI,
performance and memory budgets, threading and leak tests.

## Known gaps

* A few single-batch operations on 100k+ lines are not sliced and take longer than 50 ms in an unoptimised build: `:%normal ...` (~170 ms), `gU`/`>`/`J`/`gq` over the whole buffer (60-100 ms).
  `:g` with commands that move many lines (`:g/a/m0` on 100k lines) is quadratic in the line moves (it is bounded in memory but slow, seconds).
* `:s///c` shows the result of every answer but the cursor/`.`-mark bookkeeping after `a`/`l` is only verified for the cases in the suite.
* **Split windows**: `:sp :vsp :new Ctrl-W s/v` call the shell's editor split, which currently throws inside Dock.Avalonia (the document well's
  data template for editor tabs also matches the close-button presenters of Dock's tab strip, which then take an inactive tab's view host;
  View > Split Editor Right fails the same way without Vim). `Ctrl-W h/j/k/l/w` work between groups once they exist. The adapter's split test is
  skipped with this reason.
* `:w {file}` writes the copy as UTF-8; a dot-repeat or macro replays typed text without the editor's auto-closing (see Integration).
* A selection made with the keyboard by the editor itself (Shift+arrows when `vim.handleKeys` gives them to the editor) does not start Visual mode.
* Engine slices are 3 ms of engine time; the editor's reaction to the atomic change of a slice comes on top (measured up to ~15 ms on a heavily
  loaded machine for a slice of `:g` over 20k lines).
