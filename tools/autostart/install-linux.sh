#!/usr/bin/env bash
# Starts CsCode in the background at login (opt-in; nothing calls this silently). docs/SESSIONS.md, "Autostart".
#
#   tools/autostart/install-linux.sh [--systemd | --xdg] [--cscode PATH] [--uninstall] [--dry-run]
#
#   --systemd   a systemd *user* unit, cscode-background.service, running `cscode --background` (default when `systemctl --user` works)
#   --xdg       an XDG autostart entry, ~/.config/autostart/cscode-background.desktop (any desktop that follows the XDG autostart spec)
#   --cscode    the cscode executable (default: `command -v cscode`, else ~/.local/bin/cscode)
#   --uninstall remove whichever of the two is installed
#   --dry-run   print what would be written/run, change nothing
#
# `cscode --background` starts the warm spare when "cscode.background.warmSpare" is true (it is off by default) and, with
# "cscode.background.restoreOnLogin": true, the sessions that were running before, hidden. With both off it starts nothing. It never opens a window.
set -euo pipefail

mode=""
cscode=""
uninstall=0
dry=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --systemd) mode=systemd ;;
    --xdg) mode=xdg ;;
    --cscode) cscode="$2"; shift ;;
    --uninstall) uninstall=1 ;;
    --dry-run) dry=1 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
unit_dir="$config_home/systemd/user"
unit="$unit_dir/cscode-background.service"
desktop_dir="$config_home/autostart"
desktop="$desktop_dir/cscode-background.desktop"

run() { if [[ $dry -eq 1 ]]; then echo "would run: $*"; else "$@"; fi; }
write() { # write <path> (content on stdin)
  if [[ $dry -eq 1 ]]; then echo "would write $1:"; cat; else mkdir -p "$(dirname "$1")"; cat > "$1"; echo "wrote $1"; fi
}
have_systemd() { command -v systemctl >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; }

if [[ $uninstall -eq 1 ]]; then
  if [[ -f "$unit" ]]; then
    have_systemd && run systemctl --user disable --now cscode-background.service || true
    run rm -f "$unit"
    have_systemd && run systemctl --user daemon-reload || true
  fi
  [[ -f "$desktop" ]] && run rm -f "$desktop"
  echo "CsCode no longer starts at login."
  exit 0
fi

if [[ -z "$cscode" ]]; then
  cscode="$(command -v cscode || true)"
  [[ -z "$cscode" && -x "$HOME/.local/bin/cscode" ]] && cscode="$HOME/.local/bin/cscode"
fi
if [[ -z "$cscode" || ! -x "$cscode" ]]; then
  echo "cscode executable not found; pass --cscode PATH" >&2
  exit 1
fi
[[ -z "$mode" ]] && { have_systemd && mode=systemd || mode=xdg; }

case "$mode" in
  systemd)
    write "$unit" <<EOF
[Unit]
Description=CsCode in the background (warm spare and resident sessions)
After=graphical-session.target
PartOf=graphical-session.target

[Service]
# The launcher starts the CsCode processes and exits; they stay in this unit's cgroup. RemainAfterExit keeps the unit active after that
# (without it the unit would stop at once and take them along). Stopping the unit (logout through PartOf=graphical-session.target, or
# systemctl --user stop) sends SIGTERM to every process in the cgroup (the default kill mode, control-group): a session answers it by saving
# its unsaved changes (hot exit) and quitting, within 15 s (its own watchdog); SIGKILL only after TimeoutStopSec.
Type=oneshot
RemainAfterExit=yes
TimeoutStopSec=30
ExecStart=$cscode --background

[Install]
WantedBy=graphical-session.target
EOF
    run systemctl --user daemon-reload
    run systemctl --user enable cscode-background.service
    ;;
  xdg)
    write "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=CsCode (background)
Comment=Starts CsCode in the background so windows open instantly
Exec=$cscode --background
NoDisplay=true
X-GNOME-Autostart-enabled=true
EOF
    ;;
esac
echo "CsCode starts in the background at login ($mode)."
