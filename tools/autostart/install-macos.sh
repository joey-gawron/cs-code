#!/usr/bin/env bash
# macOS: a LaunchAgent that runs `cscode --background` at login (opt-in). Untested on this Linux machine; docs/SESSIONS.md.
#   tools/autostart/install-macos.sh [--cscode PATH] [--uninstall]
set -euo pipefail
cscode="$(command -v cscode || true)"
uninstall=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --cscode) cscode="$2"; shift ;;
    --uninstall) uninstall=1 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
plist="$HOME/Library/LaunchAgents/org.cscode.background.plist"
if [[ $uninstall -eq 1 ]]; then
  launchctl unload "$plist" 2>/dev/null || true
  rm -f "$plist"
  exit 0
fi
[[ -x "$cscode" ]] || { echo "cscode executable not found; pass --cscode PATH" >&2; exit 1; }
mkdir -p "$(dirname "$plist")"
cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>org.cscode.background</string>
  <key>ProgramArguments</key><array><string>$cscode</string><string>--background</string></array>
  <key>RunAtLoad</key><true/>
</dict>
</plist>
EOF
launchctl load "$plist"
echo "CsCode starts in the background at login."
