# Module brief — Editor macros (general, non-Vim) — **wave 2** (needs the merged shell + editor)

Vim's `q`/`@` macros live in the Vim module. This brief is for **editor-level macros that work with the normal (non-Vim) keymap**, because "keybindings and macros" is a standard expectation of a VS-style editor (VS2022 itself dropped macros; this restores them).

**Owns (when launched):** `src/CsCode.Macros/**` (Avalonia-free logic), `tests/CsCode.Macros.Tests/**`, UI glue in `CsCode.App` (menu/status/palette entries — coordinate via additive files `src/CsCode.App/Macros/**`), `docs/MACROS.md`.

## Goal
Record, name, edit, save and replay user macros: *Tools ▸ Macros ▸ Record / Stop / Play / Play N times / Play until end of file / Manage…*, status-bar "Recording macro…" indicator, keybinding assignment, JSON storage, shareable files.

## Design
* **Recorded step model** (`MacroStep`): `Key(KeyStroke)` (only when the key was *not* resolved to a command), `Text(string)` (typed/IME text), `Command(id, args)` (a key chord or menu/palette action that resolved to a command is recorded as the command, not the keys — deterministic replay), `Pause(ms)` (optional, off by default). Recording hooks: the shell's keyboard pipeline (pre-resolution tap) + an additive `ICommandService.Executed` event (**additive Core change**: `IEventSource<CommandExecuted> Executed { get; }` with id/args/source, fired after successful execution — implement in Platform's `CommandService` and `TestCommandService`) + text-input tap from the editor view.
* **Replay** drives the *same* pipeline the user uses (keys/text injected into the focused editor view through an additive `IEditorViewController.ReplayText(string)`/key-injection API; commands via `ICommandService`), on the UI thread in **slices** (≤ 4 ms per dispatcher callback, yielding between steps) so a long macro or "play 10,000 times" never freezes the GUI; cancellable (Esc), with a runaway guard (step budget, wall-clock budget) and **abort on error** (a command that throws, or a search/navigation that fails, stops the macro and says why).
* **Control flow (SHOULD)**: `Play until end of file`, `Play until search fails`, `Play on all selected lines` (+ multi-file later), repeat count, conditional stop.
* **Storage**: `{UserDataDir}/macros.json` (JSONC, human-editable: name, description, steps, optional keybinding), live-reloaded; commands `macro.record`, `macro.stop`, `macro.play`, `macro.playLast`, `macro.playNamed`, `macro.save`, `macro.edit`, each named macro auto-registered as command `macro.run.<name>` so it can be bound in `keybindings.json` and appears in the palette.
* **Vim interplay**: when Vim emulation is on, `macro.record` is disabled with a hint (use `q`); both share the register/persistence conventions where sensible; macros can call `vim.*` commands.
* **Threading**: recording taps are allocation-light and run on the UI thread; storage I/O on the IO lane; never blocks. **Memory**: recording buffer capped (default 100k steps), macro list bounded, no retained editor references after Stop.

## Tests
Unit (step model, JSON round trip, abort semantics, runaway guard, slicing never exceeds the UI budget — use the Profiling `ResponsivenessProbe`), headless UI end-to-end: record typing + commands in a real editor view, replay N times, verify buffer; macros survive restart; keybinding to a macro works; leak tests.
