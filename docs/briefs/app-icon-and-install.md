# Module brief — App icon and local install (terminal + start menu) — **last in the current queue**

**User request (2026-10-29):** "After you finish these remaining tasks can you change the app icon to the one attached and install the app so
that it can both be called in terminal or start menu." The user attached `ide-icon.svg` and `ide-icon-pack.zip` (a seal mascot on a dark blue rounded
square). Its files are committed in `assets/branding/`: `cscode.svg` (1024×1024 viewBox), `cscode-{16,32,48,64,128,256,512,1024}.png`, and
`cscode.ico` (6 sizes, up to 256).

Runs after everything queued before it (TypeScript then Rust debugger, theme dropdown / languages / tree-sitter, background mode), so the version
that gets installed is the finished one.

## Machine (checked 2026-10-29)
KDE Plasma on **Wayland**. Avalonia runs through XWayland, and the window's class is `cscode` (`WM_CLASS ("cscode" "cscode")`), so KDE matches it to
`cscode.desktop`. `~/.local/bin` is on `PATH` (`~/.profile`, `~/.bashrc`). User desktop entries live in `~/.local/share/applications` (Zed is
installed the same way). The development build only runs inside `tools/nix-dev-env.sh` (Nix dotnet; Skia needs libfontconfig). The installed app must
not depend on that shell.

## MUST
1. **Icon everywhere:**
   * the window icon (`src/CsCode.App/Views/AppIcon.cs` draws one in code today: replace it with the PNGs, embedded as Avalonia resources, all sizes so
     the taskbar and Alt+Tab pick a sharp one);
   * the title-bar logo at the top left of the shell;
   * the Windows executable (`<ApplicationIcon>` = `cscode.ico`);
   * the Linux desktop entry and icon theme;
   * About and Welcome, if they show a logo.
   Check it at 16 px: if the 16/32 px PNGs are unreadable in the title bar, say so and show the user. Do not redraw their artwork without asking.
2. **`tools/install.sh`** (idempotent; re-running it upgrades), installing for the current user only, with no sudo:
   * `dotnet publish src/CsCode.App -c Release -r linux-x64 --self-contained`, so it runs on the system loader and libraries without the Nix shell.
     Verify it starts from a clean environment (`env -i HOME=… DISPLAY=… WAYLAND_DISPLAY=… XDG_RUNTIME_DIR=…`). If a library is missing, fix it in the
     publish, not with `LD_LIBRARY_PATH` into the Nix store.
   * App files go in `~/.local/share/cscode/<version>/`, with a `current` symlink. Keep the previous version for rollback.
   * `~/.local/bin/cscode`: a small launcher that passes all arguments through (`cscode .`, `cscode file:line:col`, folders). When background mode is
     in, this is its launcher (hot-loading a saved-session window). It sets `DOTNET_ROOT` only when unset and a dotnet is found, and adds
     `~/.dotnet/tools` to PATH for the child processes, so `csharp-ls` works when started from the start menu, not just from a terminal.
   * `~/.local/share/applications/cscode.desktop`:
     * `Name=CsCode`, `GenericName=Code Editor`, `Exec=cscode %F`, `Icon=cscode`, `StartupWMClass=cscode`;
     * `Categories=Development;IDE;TextEditor;`;
     * a `MimeType=` list for source files;
     * a "New Window" action.
     Validate it with `desktop-file-validate` if available.
   * Icons go into `~/.local/share/icons/hicolor/{16,32,48,64,128,256,512}x…/apps/cscode.png` and `scalable/apps/cscode.svg`. Then refresh the
     caches that exist: `update-desktop-database ~/.local/share/applications`, `kbuildsycoca6` (KDE), `gtk-update-icon-cache`.
   * Debug adapters already on this machine (js-debug v1.140.0, netcoredbg) are copied into the installed app's adapter location so debugging works
     without the `CSCODE_*` variables. Nothing is downloaded.
   * Never touch the user's other editors (`je`, Zed, Kate) or their settings. The installed app uses the normal config dirs, which is the point of
     installing. Ask the user before the first real run against their real config, since tests always used `CSCODE_HOME` temp dirs.
3. **`tools/uninstall.sh`**: removes exactly what `install.sh` created (the files it lists in a manifest), keeping the user's settings and state.
4. **Verify on the real desktop:**
   * `which cscode` and `cscode --version`;
   * `cscode <folder>` opens a window;
   * the app appears in the KDE launcher under Development, with the seal icon;
   * starting it from the launcher works;
   * the taskbar shows the seal icon, not a generic one (Wayland app-id match);
   * closing it leaves no process behind.
   Screenshot the launcher entry and the running window for the user.

## SHOULD
A `tools/install.ps1` and Windows Start Menu shortcut, and a macOS `.app` bundle with an `.icns` built from the PNGs: written, but untested here (Linux
only). `cscode --install-shell-integration` is out of scope.

## Done when
The real installed app runs from a terminal and from the KDE start menu with the new icon in the launcher, title bar and taskbar; install and uninstall
are idempotent and tested in a temp `HOME`; everything is committed without attribution trailers.
