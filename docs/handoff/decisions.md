# CsCode: decisions for you (draft, 2026-10-30)

Everything below is already built one way (my pick first). Say a number and "switch" to change one; silence keeps the pick.

## Your call (real trade-offs)

1. **Rust debugger default**: GDB (pick; works today, CodeLLDB loses a crashing program's last output lines, an upstream bug) or CodeLLDB (better Rust value formatting).
   **Decided 2026-10-30**: CodeLLDB on macOS (GDB cannot debug native arm64 macOS programs), GDB on Linux. Done: `cscode.debug.rustDebugger`
   defaults to `lldb` on a Mac and `gdb` on Linux (the setting still chooses either); a missing CodeLLDB on a Mac says it is the Mac's debugger
   and where the app ships it, not "use GDB" (docs/DEBUGGING-RUST-TS.md, "Which Rust debugger is the default").
2. **Background warm spare**: keep it OFF (pick). With it on, a new window opens in 0.5-1.3 s instead of the 500 ms target, because the first frame and first editor can't be pre-warmed without side effects. The alternative is more work on it.
3. **Install size**: one installed version is 437 MB (app 248, CodeLLDB 166, grammars 47, remote server bundles 14), and the previous version is kept for rollback (up to 2x).
   - (a) bundle CodeLLDB (pick) vs leave it out (-166 MB; Rust uses GDB, CodeLLDB is offered as a download on first use);
   - (b) keep one previous version (pick) vs none.
4. **Startup build**: ReadyToRun composite, 198 MB, 0.87 s cold start (pick), or per-assembly, 157 MB, 1.27 s. A tiny NativeAOT launcher would save another ~25 ms per `cscode` call: not done unless you want it.
5. **Copy**: Ctrl+C copies plain text and "Copy with Formatting" copies HTML (pick), or every Ctrl+C also carries HTML like VS Code (`editor.copyWithSyntaxHighlighting`).
6. **Sticky scroll**: off by default (pick; VS Code has it on). It is implemented and costs < 16 ms per scroll step.
7. **Icon at 16 px**: your artwork as is (pick); the navy tile almost merges with the dark title bar at 16 px. A higher-contrast 16 px variant would need a new drawing from you.
8. **Desktop entry**: opens CsCode for 37 source-file types, not plain .txt or folders (pick). Say if .txt files or folders should offer CsCode.

## Done the VS Code way (FYI; say if you object)

- Undo: a save ends the typed undo step ("a", save, "b": one Undo returns to "a"). One Undo reverses a whole macro playback (265 ms for 100k changes).
- Folding: tree-sitter folds are merged with the language server's (the server wins a shared line).
- Find past 19,999 matches: F3/Enter reach every match and wrap; Replace All is one undo step.
- Settings that start programs (language servers, debuggers, shells, ssh/docker paths) are read from user settings only, never a project's `.vscode/settings.json`. In a remote window they come from the remote host's own machine settings file.
- Tasks: a second run of a running task asks Terminate / Restart; a background task without problem patterns just runs on (no error after 5 s).
- Terminal: confirm-on-exit default "never"; split-pane keys only while split; Ctrl+Shift+B = default build task.
- Vim clipboard: with `clipboard=unnamed(plus)` or `vim.useSystemClipboard`, `p` reads the system clipboard first; a selection in a CsCode editor is not `"*`.
- Background quit: if the terminal asks "really close?" during a hot-exit quit, asking again keeps the hot-exit backups; with hot exit off, "Don't Save" really discards.
- TypeScript/Vue/Svelte servers restart above 4 GiB (VS Code's tsserver limit plus 1 GiB), at most 3 times, then wait for you.
- Remote: server bundles trimmed; the remote server keeps running 3 h after a disconnect.
- Grammar bundle: all 42 grammars shipped (47 MB).

## Test method changes to approve (budgets unchanged)

- The hidden-window idle CPU test waits up to 20 s for .NET's background JIT to finish before measuring (< 2 % kept).
- The scroll memory soak pre-fills the big file's syntax-colour cache before measuring; it still fails on the real leak.

## Follow-ups (not done)

- "Open Remote Settings" command / a Remote tab in Settings (the remote machine settings file is edited by path for now).
- A close-veto API so the terminal can say "asking / declined / proceeding" instead of the 3 s settle the background quit uses.
- The status bar's indentation ignores `.editorconfig`.
- Windows installer and macOS bundle scripts are written but untested (Linux only here).
