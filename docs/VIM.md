# CsCode.Vim — Vim emulation

`CsCode.Vim` is a Vim engine for the CsCode editor: modal editing, operators x motions x text objects, registers, marks, dot-repeat, a Vim
undo tree, search, Ex commands, mappings and macros. It is verified against real Neovim (v0.12.5 was used) with a differential test suite.
It depends only on `CsCode.Core` (no Avalonia). The Editor/Shell adapter that connects it to the real editor is a later step; this module
ships the engine, the host abstraction and a headless host that every test uses.

* Source: `src/CsCode.Vim` — `Engine/` (the `VimEngine` partial class), `Abstractions/` (`IVimHost`, `IVimBuffer`), `Hosting/` (headless host,
  `LineVimBuffer`, timers), `Config/` (options, session, settings), `Registers/`, `Search/` (Vim regex -> .NET), `Editing/` (marks, undo tree).
* Tests: `tests/CsCode.Vim.Tests` (xUnit v3). Lab tool: `tools/vim/lab.sh`.

## Integration guide for the Editor/Shell adapter

```csharp
services.AddCsCodeVim();                         // needs IConfigurationService and ICommandService; uses IStateStorage/IAppPaths/IOutputService if present
var vim = provider.GetRequiredService<IVimService>();   // resolving it registers vim.* settings and commands, loads the vimrc and saved registers
var engine = vim.CreateEngine(myHost);           // one engine per editor view; the caller disposes it
```

Per key event the adapter calls

```csharp
VimKeyResult r = engine.HandleKey(keyStroke);    // or HandleKey(int) / HandleText(string) for IME / typed text
if (r == VimKeyResult.PassThrough) { /* let the editor handle the key */ }
```

and after anything else that makes the engine work for a while (see Threading) it keeps calling `engine.RunPendingWork(TimeSpan.FromMilliseconds(3))`
from a dispatcher tick while `engine.HasPendingWork` (or when `engine.PendingWorkChanged` fires `true`).

`IVimHost` is deliberately small. Mandatory: `Buffer`, `GetCaret`, `SetCaret`, `Timer`. Everything else has a harmless default:

| Area | Members |
|---|---|
| buffer and caret | `Buffer` (`IVimBuffer`: line-oriented reads, `ApplyEdit`/`ApplyEdits` as one change, optional `Changed` event), `GetCaret`/`SetCaret(position, insertLike)`, `SetSelection(VimSelection)` (char/line/**block** selections) |
| feedback | `OnModeChanged(mode, VimModeInfo{CursorShape,Label,SubLabel})`, `OnMessage`, `OnShowCommand`, `OnRecordingChanged`, `OnCommandLineChanged` (the host draws the `:` `/` `?` prompt), `OnSearchHighlight`, `OnYankHighlight` |
| viewport | `GetViewport`, `SetTopLine` (scrolling, `zt zz zb`, `Ctrl-D/U/F/B/E/Y`), `GetDisplayRows`, `WrapWidth`, `WindowWidth` (`gj gk g0 g$ gm`) |
| folds | `TryGetClosedFold`, `FoldCommand` |
| clipboard | `ReadClipboard`, `WriteClipboard` (registers `+` `*`, `vim.useSystemClipboard`) |
| windows/buffers | `PerformAction(VimEditorAction, args)` for `:w :q :e :bn :sp :vsp Ctrl-W...`; return false when unsupported |
| app commands | `ExecuteCommand(id, args)` — fire and forget (`gd` -> `editor.action.revealDefinition`, `gr`, `K` ...) |
| language | `GetCommentTokens` (surround/commentary), `FormatLines` (`=`, `gq`), `RequestCompletion` (`Ctrl-N/P/X`) |
| time | `Timer` (`IVimTimer`): used only for `timeoutlen`; `ManualVimTimer` for tests, `ThreadingVimTimer(post)` for real time |

`InMemoryVimHost` implements all of it over an `InMemoryTextDocument` (or any `IVimBuffer`) and records what the engine told it.
`LineVimBuffer` is a fast `List<string>` buffer. Engines of one `VimSession` share options, registers, mappings, search memory and the
plugin switches; undo history, marks and the jump list are per buffer.

Events: `ModeChanged`, `MessageChanged`, `RecordingChanged`, `SearchChanged`, `PendingWorkChanged`. State: `Mode`, `PendingKeys`, `Cursor`,
`IsRecording`, `LastMessage`, `LastError`. `Reset()` abandons any pending command (and pending work) and returns to Normal mode;
`Detach()` releases host and buffer; `Dispose()` does both.

## Threading and isolation (ARCHITECTURE section 6)

* The engine runs **synchronously on the caller's (UI) thread** inside `HandleKey`. Every callback into the host happens on that thread
  (`ThreadingTests` verifies it from a worker thread). The engine starts no threads, tasks or timers; the only asynchrony is the host's `IVimTimer`
  for `timeoutlen`. No file or network I/O, nothing is awaited. `ExecuteCommand` is fire-and-forget; nothing waits for language servers.
* Typed keys that arrive while work is pending are queued and run, in order, after it.
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
| `vim.enabled` | `false` | master switch; `vim.toggle/enable/disable` change it for the session |
| `vim.useSystemClipboard` | `false` | unnamed register uses the host clipboard |
| `vim.leader` | `"\\"` | `<leader>` in bindings (vimrc: `let mapleader=`) |
| `vim.hlsearch` / `incsearch` / `ignorecase` / `smartcase` / `wrapscan` | `false/true/true/true/true` | VSCodeVim's defaults; plain Vim defaults apply when no settings service is used |
| `vim.startInInsertMode` | `false` | new engines start in Insert mode |
| `vim.timeout` | `1000` | `timeoutlen` in ms (a boolean turns `timeout` on/off) |
| `vim.handleKeys` | `{}` | `{"<C-c>": false}` passes that key to the editor |
| `vim.{normal,insert,visual,operatorPending,commandLine}ModeKeyBindings` and `...NonRecursive` | `[]` | `{"before": ["j","j"], "after": ["<Esc>"], "commands": [{"command": "...", "args": []}]}`; a command starting with `:` is an Ex command |
| `vim.cursorStylePerMode.{normal,insert,replace,visual,visualline,visualblock,operatorpending,commandline}` | block/line/underline/... | `block line underline block-outline line-thin underline-thin` |
| `vim.autoSwitchInputMethod` | `{}` | accepted, ignored |
| `vim.persistRegisters` | `true` | registers `a`-`z` (and so macros) are stored in `IStateStorage` under `vim.registers` |
| `vim.maxRegisterChars` | `8000000` | register size cap |
| `vim.vimrc.enable` / `vim.vimrc.path` | `true` / `""` | default file: `{UserDataDir}/vimrc` |
| `vim.surround` `vim.commentary` `vim.sneak` `vim.easymotion` `vim.camelCaseMotion` `vim.argumentObjects` `vim.replaceWithRegister` `vim.highlightedyank` `vim.targets` | `true true false false false false false true false` | boolean or `{"enable": bool}`. Only surround and commentary are implemented; the others are parsed and stored for later |

Changing a setting touches only what it names (a runtime `:set scrolloff=7` survives an unrelated settings change). Bindings from settings are
replaced as a group; bindings from the vimrc are replaced when it is reloaded. The vimrc supports the Ex subset below (`set`, the `map` family, `let`,
`source`, comments, `\` continuation lines); errors are collected (`IVimService.VimrcErrors`: `line 5: E492: ...`) and never fatal.

Commands: `vim.toggle`, `vim.enable`, `vim.disable`, `vim.showCheatSheet` (writes the "Vim Cheat Sheet" output channel).

## What is supported

| Group | Supported |
|---|---|
| Modes | Normal, Insert, Replace, Visual (char/line/block), Operator-pending, Command-line (`: / ?`), Insert-Normal (`Ctrl-O`); cursor shape per mode |
| Counts, registers | counts before and inside operators; `" 0 1-9 - a-z A-Z _ . : / % + *`, shift rules, linewise/charwise/blockwise types, `:registers` |
| Motions | `h j k l w W b B e E ge gE 0 ^ $ g_ \| gm gM f F t T ; , % ( ) { } [[ ]] [] ][ H M L gg G {n}G {n}% + - _ <CR> <BS> <Space>`, marks, `/ ? n N * # g* g#` with counts and offsets, `gj gk g0 g$`, `gn gN`, `go`, `Ctrl-O/Ctrl-I`, `g; g,`; `o_v o_V o_CTRL-V` forcing and the exclusive/linewise rules |
| Operators | `d c y > < = g~ gu gU g? gq gw zf` (+ doubled forms), `x X D C s S Y p P gp gP ]p [p J gJ ~ r R .`, `u Ctrl-R U`, `Ctrl-A/Ctrl-X` (decimal/hex/octal/binary, visual `g Ctrl-A`), `& g&` |
| Text objects | `iw aw iW aW is as ip ap`, brackets, quotes, `it at`, counts, nesting, Visual extension |
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
* No `IVimHost` adapter exists yet (Editor/Shell step); `vim.enabled` defaults to false until the shell flips it.
