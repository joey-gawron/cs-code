# Module brief — Clipboard support and right-click menus — queued after the work in flight

**User request (2026-10-29):** "Can you add clipboard support to that list … and right click menu."

Read `docs/briefs/00-common.md` first (rules, threading, memory). Visual Studio 2022 is the reference for behaviour and wording.

## State today (checked 2026-10-29 on main 0cdd026)
* **Right-click menus:** the editor's text area has **none**. The only editor menu is the breakpoint margin's (`src/CsCode.Editor/Features/DebugFeature.cs`,
  `ShowContextMenu`). Explorer, the Debug panels (4 views) and the Search panels (3 views) have some menus; document tabs, Error List, Output and the status
  bar have none or have not been audited.
* **Clipboard:** the editor declares `editor.action.clipboardCopyAction` / `CutAction` / `PasteAction`, `IWindowService.CopyToClipboardAsync` exists,
  and a few panels copy (Call Stack, Debug Console, Exception helper, Find in Files, Explorer). Nobody has checked the system clipboard in both
  directions on the user's desktop: KDE Plasma on **Wayland**, with CsCode running through XWayland. No `xclip`, `xsel` or `wl-copy` is installed.
* **Vim:** the Vim↔editor adapter (separate session, workflow `wf_386b8644-ed6`) backs **both** `+` and `*` with the system CLIPBOARD through
  `IVimHost.ReadClipboard` / `WriteClipboard`, so `"+y`, `"+p`, `"*y` and `"*p` work against CLIPBOARD once it merges. The Linux PRIMARY selection was
  left to this wave, for three reasons:
  * Avalonia 12.1.4 has no public primary-selection API (Avalonia.X11 knows XA_PRIMARY only internally);
  * xclip, xsel and wl-clipboard are not installed;
  * the host members take no register name, so the host cannot tell `*` from `+`.

## A. Clipboard (MUST)
1. Cut / Copy / Paste work everywhere text can be selected or copied:
   * the editor;
   * Locals, Autos and Watch (value, name, expression);
   * Call Stack (one frame or all);
   * the Debug Console and Output;
   * Error List rows;
   * Find in Files results;
   * Settings fields, the quick input / command palette, the Explorer (paths);
   * the terminal, when it exists.
   They work through the standard keys (Ctrl+C / X / V, Ctrl+Insert, Shift+Insert, Shift+Delete), the Edit menu and the right-click menus, with
   each item enabled only when it can act.
2. **System clipboard, both directions:** CsCode ↔ native Wayland apps (Kate, Konsole, Firefox) and ↔ X11 apps. It handles Unicode (emoji,
   combining marks, RTL) and large text (10 MB without freezing the UI: reads and writes are asynchronous, and the UI thread never waits on the
   clipboard owner). On paste, line endings are converted to the document's EOL. The real-desktop check is the integrator's (copy from Kate into
   CsCode and back, then screenshot). Headless tests use Avalonia's clipboard.
3. **Visual Studio behaviours:**
   * Ctrl+C / Ctrl+X with no selection copies / cuts the whole line, and pasting such a line inserts it above the caret's line (setting
     `editor.emptySelectionClipboard`, on by default).
   * Multi-caret copy / paste puts one line per caret when the counts match.
   * Box (column) selection copies as a block and pastes as a block.
   * Paste respects auto-indent, and `editor.formatOnPaste` formats through the language server's range formatting when the server supports it.
   * Undo restores a paste in one step.
4. **Clipboard ring** (Visual Studio's Ctrl+Shift+V): the last 20 copies, in memory only (never persisted: copied secrets must not land on disk),
   each capped at 1 MB. Repeated Ctrl+Shift+V cycles through them in place.
5. **Linux primary selection:** selecting text sets PRIMARY, and middle-click pastes it (setting `editor.selectionClipboard`, on by default on Linux as
   in VS Code, off on other platforms). This needs:
   * a primary-selection service that owns the X11 PRIMARY selection and answers SelectionRequest events through XWayland, or speaks the Wayland
     primary-selection protocol when the app runs natively. It must work without xclip, xsel or wl-clipboard;
   * additive `IVimHost` overloads `ReadClipboard(char register)` and `WriteClipboard(char register, string text, VimRegisterType type)`, whose
     defaults call the current members. This is a small engine change, and the Vim engine's tests must stay green;
   * `*` mapped to PRIMARY in the Vim host (`+` stays CLIPBOARD);
   * middle-click paste in the editor.
6. **Remote editing:** copy and paste always use the local clipboard, never the remote host's.
7. **SHOULD:** *Copy with formatting* (HTML with the theme's syntax colours, for pasting into documents and mail), and *Copy path* / *Copy relative
   path* on tabs and in the Explorer.

## B. Right-click menus (MUST)
1. **Editor text area**, Visual Studio-like. Right-click moves the caret to the click unless it lands inside the selection; Shift+F10 and the Menu key
   open the menu at the caret. Items:
   * Quick Actions and Refactorings (Ctrl+.), Rename;
   * Go To Definition / Declaration / Implementation, Find All References, Peek Definition;
   * Format Document / Selection;
   * Cut / Copy / Paste, Comment / Uncomment, Surround With (snippets);
   * Breakpoint ▸ (Insert, Toggle, Conditions), Run To Cursor, Add Watch, QuickWatch;
   * Command Palette.
   Each item shows its shortcut from the active keymap and is enabled from the language server's capabilities and the debug state. Items that cannot
   apply are hidden, not shown dead.
2. **Everywhere else a menu is expected:**
   * Document tabs: Close, Close All But This, Close All, Close To The Right, Pin Tab, Copy Full Path, Open Containing Folder, Reveal in Explorer,
     New Horizontal / Vertical Document Group.
   * Explorer: audit what exists. New File / Folder, Rename, Delete, Cut / Copy / Paste files, Copy Path / Relative Path, Find in Folder,
     Open in Terminal, Reveal in File Manager.
   * Error List rows: Copy, Go To, Show Error Help.
   * Output: Copy, Clear All, Word Wrap.
   * Tool-window title bars: Float, Dock, Auto Hide, Close.
   * Find Results, the status bar items, and the debug panels (audit).
3. **One menu model:** menus are built from a contribution point (`IContributionRegistry`, with `when` clauses on the existing context keys), so the
   LSP, DAP, Vim and Search modules add their own items instead of the shell hard-coding them. The menus follow the theme (including high contrast),
   show icons and shortcut text, and are keyboard-navigable and screen-reader labelled.
4. **Budgets:** opening any menu takes under 50 ms and does no blocking I/O or language-server round-trip on the UI thread (capabilities are
   already known; slow items such as a Rename availability check resolve asynchronously and enable when ready). 100 open/close cycles retain nothing.

## Tests
Real-composition headless tests: right-click at given positions (text, selection, gutter, tab, panel rows); assert the items, their enabled state and
their effect. Clipboard tests through Avalonia's headless clipboard; the behaviours in A3 to A5 in editor tests. A real-window smoke test on a private
Xvfb (no user display) for a round trip with a second X client. The Wayland ↔ XWayland round trip is checked by hand by the integrator on the real
desktop, with screenshots.

## Order
Queued after the three pieces in flight (TypeScript debugger, theme dropdown, language support). It touches the editor and the shell but not `src/CsCode.Dap`,
so it can run alongside the Rust/GDB debugger round. It overlaps the Vim adapter (keys, the editor controller) and the themes branch (shell menus):
merge main into it first.

## Done when
Every item above works on the real window (screenshots of the editor menu, a tab menu, and a paste from Kate); all tests are green; everything is
committed without attribution trailers.
