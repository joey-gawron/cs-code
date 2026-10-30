#!/usr/bin/env bash
# Packages CsCode for macOS, on a Mac: publishes the app (tools/publish-app.sh), builds the remote server bundles (tools/publish-server.sh),
# assembles CsCode.app with them (tools/macos/make-app-bundle.sh: Info.plist with the document types, an .icns from assets/branding made with
# sips and iconutil, an ad-hoc signature), checks it with plutil -lint and codesign --verify, and with --install puts the app in
# ~/Applications and a `cscode` command on PATH.
#
#   tools/package-macos.sh [--rid osx-arm64|osx-x64]... [--from DIR] [--out DIR] [--server-bundles DIR | --no-server-bundles] [--no-sign]
#                          [--install [--app-dir DIR] [--bin-dir DIR] [--force]]
#
#   --rid               a macOS runtime to package (repeatable; default: this Mac's, osx-arm64 on Apple silicon). Each goes to <out>/<rid>/CsCode.app.
#   --from DIR          bundle this existing osx publish instead of publishing (one --rid only)
#   --out DIR           output directory (default: .scratch/macos)
#   --server-bundles DIR  ship these remote server bundles (tools/publish-server.sh's output); default: built now (linux-x64 + portable)
#   --no-server-bundles ship none (remote editing then needs remote.ssh.serverBundles)
#   --no-sign           leave the bundle unsigned (Apple silicon will not start it)
#   --install           copy the bundle for this Mac's runtime to the app directory and install the `cscode` command
#   --app-dir DIR       where --install puts CsCode.app (default: ~/Applications; /Applications needs an administrator)
#   --bin-dir DIR       where --install puts `cscode` (default: the first writable of /opt/homebrew/bin, /usr/local/bin, ~/.local/bin, ~/bin that
#                       is on PATH; else ~/.local/bin, with the line to add to ~/.zprofile)
#   --force             let --install replace a `cscode` in the bin directory that it did not write
#
# The osx runtime packs are not pinned (only linux-x64's are: tools/fetch-publish-packs.sh), so the publish takes the ones restore picks from
# nuget.org (publish-app.sh --unpinned). The signature is ad hoc: the app runs on the Mac that built it; a copy for other Macs needs a
# Developer ID signature and notarization (docs/handoff/macos-audit.md, pkg-2). Runs on macOS's bash 3.2.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
rids=""; from=""; out="$repo/.scratch/macos"; servers=""; no_servers=0; sign=1
install=0; app_dir="$HOME/Applications"; bin_dir=""; force=0
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rids="$rids $2"; shift 2 ;;
        --from) from=$2; shift 2 ;;
        --out) out=$2; shift 2 ;;
        --server-bundles) servers=$2; shift 2 ;;
        --no-server-bundles) no_servers=1; shift ;;
        --no-sign) sign=0; shift ;;
        --install) install=1; shift ;;
        --app-dir) app_dir=$2; shift 2 ;;
        --bin-dir) bin_dir=$2; shift 2 ;;
        --force) force=1; shift ;;
        -h|--help) sed -n '2,/^[^#]/p' "$0" | sed -e '/^[^#]/d'; exit 0 ;; # the header comment
        *) echo "package-macos.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

die() { echo "package-macos.sh: $*" >&2; exit 1; }
[ "$(uname -s)" = Darwin ] || die "run this on a Mac (it needs sips, iconutil, codesign and plutil); tools/macos/make-app-bundle.sh builds an unsigned bundle elsewhere"
for tool in sips iconutil plutil ditto; do command -v "$tool" >/dev/null || die "$tool is required"; done
[ "$sign" = 0 ] || command -v codesign >/dev/null || die "codesign is required (install the Xcode command line tools: xcode-select --install), or pass --no-sign"

if [ "$(uname -m)" = x86_64 ]; then here_rid=osx-x64; else here_rid=osx-arm64; fi
[ -n "$rids" ] || rids=$here_rid
for rid in $rids; do case "$rid" in osx-x64|osx-arm64) ;; *) die "--rid must be osx-x64 or osx-arm64" ;; esac; done
if [ -n "$from" ] && [ "$(echo $rids | wc -w | tr -d ' ')" != 1 ]; then die "--from bundles one publish: give one --rid with it"; fi
mkdir -p "$out"
out=$(cd "$out" && pwd)

# The remote server bundles, built once for every runtime's bundle.
if [ "$no_servers" = 1 ]; then servers=""
elif [ -z "$servers" ]; then
    servers="$out/server-bundles"
    echo "Building the remote server bundles…"
    "$repo/tools/publish-server.sh" --out "$servers"
fi

bundle_args=()
[ "$sign" = 1 ] || bundle_args+=(--no-sign)
[ -z "$servers" ] || bundle_args+=(--server-bundles "$servers")
for rid in $rids; do
    if [ -n "$from" ]; then publish=$from
    else
        publish="$repo/.scratch/publish/$rid"
        "$repo/tools/publish-app.sh" --rid "$rid" --unpinned --out "$publish"
    fi
    # ${a[@]+...}: bash 3.2 calls an empty array unbound under set -u.
    "$repo/tools/macos/make-app-bundle.sh" --rid "$rid" --from "$publish" --out "$out/$rid" ${bundle_args[@]+"${bundle_args[@]}"}
    app="$out/$rid/CsCode.app"
    plutil -lint "$app/Contents/Info.plist"
    if [ "$sign" = 1 ]; then codesign --verify --deep --strict --verbose=1 "$app"; fi
done

[ "$install" = 1 ] || { echo "Done. Install with: tools/package-macos.sh --install (or drag $out/$here_rid/CsCode.app to Applications)"; exit 0; }

# ------------------------------------------------------------------ install
case " $rids " in *" $here_rid "*) ;; *) die "--install needs this Mac's runtime ($here_rid) among the packaged ones" ;; esac
src="$out/$here_rid/CsCode.app"
mkdir -p "$app_dir"
app_dir=$(cd "$app_dir" && pwd)
target="$app_dir/CsCode.app"
if [ -e "$target" ] && [ ! -f "$target/Contents/MacOS/cscode.dll" ]; then die "$target exists and is not CsCode; not replacing it"; fi

# The `cscode` command: a script that execs the binary inside the bundle (not `open -a`), so arguments, file:line:col, --wait, the exit code
# and the terminal's working directory and environment reach the app as they do on Linux. Its place is settled (and a foreign `cscode`
# refused) before anything is installed.
on_path() { case ":$PATH:" in *":$1:"*) return 0 ;; *) return 1 ;; esac; }
if [ -z "$bin_dir" ]; then
    for d in /opt/homebrew/bin /usr/local/bin "$HOME/.local/bin" "$HOME/bin"; do
        if [ -d "$d" ] && [ -w "$d" ] && on_path "$d"; then bin_dir=$d; break; fi
    done
    [ -n "$bin_dir" ] || bin_dir="$HOME/.local/bin"
fi
launcher="$bin_dir/cscode"
marker="# cscode launcher (tools/package-macos.sh)"
if { [ -e "$launcher" ] || [ -L "$launcher" ]; } && ! grep -qF "$marker" "$launcher" 2>/dev/null && [ "$force" != 1 ]; then
    die "$launcher exists and was not written by this script; remove it or pass --force"
fi

# Copied next to the old one first, then swapped in, so a failed copy leaves the installed app as it was. ditto keeps the signature intact.
rm -rf "$app_dir/.CsCode.app.new"
ditto "$src" "$app_dir/.CsCode.app.new"
rm -rf "$target"
mv "$app_dir/.CsCode.app.new" "$target"
echo "Installed $target"

mkdir -p "$bin_dir"
rm -f "$launcher"
quoted=$(printf '%s' "$target/Contents/MacOS/cscode" | sed "s/'/'\\\\''/g") # single-quoted for sh: ' becomes '\''
cat > "$launcher.tmp" <<LAUNCHER
#!/bin/sh
$marker
exec '$quoted' "\$@"
LAUNCHER
chmod 755 "$launcher.tmp"
mv "$launcher.tmp" "$launcher"
echo "Installed $launcher -> $target/Contents/MacOS/cscode"
if ! on_path "$bin_dir"; then
    echo
    echo "$bin_dir is not on your PATH. Add it for new terminals with:"
    echo "    echo 'export PATH=\"$bin_dir:\$PATH\"' >> ~/.zprofile"
    echo "then open a new terminal (or run: export PATH=\"$bin_dir:\$PATH\")."
fi
echo "Open CsCode from Launchpad or Spotlight, or run: cscode [folder | file[:line[:column]]]"
