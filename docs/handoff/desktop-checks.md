# CsCode: checks that need a real desktop

The installed build (1.0.0-gd35e03d98d) passes every automated test that can run without a screen. These need eyes and a mouse on KDE.
If you'd rather I do them: turn on Computer use in the Claude desktop app (Settings → This computer → Computer use) and say so.

The first launch uses your real settings folders (~/.config/cscode, ~/.local/share/cscode, ~/.local/state/cscode); nothing has written there yet.

## Install and window (5 min)
1. Open the KDE launcher, then Development: CsCode is there with the seal icon. Start it from there.
2. The taskbar and Alt+Tab show the seal, sharp (not a generic or blurry icon).
3. In a terminal: `cscode ~/repos/cs-code` opens that folder in a window. Right-click the launcher entry, then New Window: a second window opens.
4. Close every window, then run `cscode --quit --all`. Afterwards `pgrep -a cscode` prints nothing.

## Clipboard (5 min)
5. Copy text in Kate, then paste in CsCode with Ctrl+V; copy in CsCode, then paste in Kate and Konsole.
6. Select text in CsCode, then middle-click in Kate: the selection is pasted (PRIMARY). The reverse also works.
7. Copy with Formatting (right-click in the editor) and paste into LibreOffice Writer: colours are kept.
8. With Vim on (the default): `"+yy` in CsCode, then paste in Kate; copy in Firefox, then `"+p` in CsCode.

## Windows and keys (5 min)
9. Drag a document tab out to make a floating window; move it, close CsCode, restart: it floats again where it was.
10. Ctrl+F in an editor opens Find; Ctrl+F in the terminal opens the terminal's Find, not the shell's.
11. Set `"terminal.integrated.confirmOnExit": "always"`, open a terminal, and close the window: it asks; Cancel keeps everything working.

## Debugging (5 min, optional)
12. A small Rust project: F5 with GDB (the default on Linux), set a breakpoint, step, check that the Locals show values.
13. Same with `"cscode.debug.rustDebugger": "lldb"` (CodeLLDB is bundled; the default on a Mac, where CsCode.app ships it).

## Not checkable here
- ssh/remote: needs a host you choose (I never touch ~/.ssh). `cscode ssh://host/path` against a real host.
- Orca screen reader: not installed.
- Windows and macOS installers: written, untested (Linux only).
