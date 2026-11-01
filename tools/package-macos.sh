#!/usr/bin/env bash
# Packages CsCode for macOS, on a Mac: publishes the app (tools/publish-app.sh), builds the remote server bundles (tools/publish-server.sh),
# gathers the debug adapters, assembles CsCode.app with them (tools/macos/make-app-bundle.sh: Info.plist with the document types, an .icns
# from assets/branding made with sips and iconutil, an ad-hoc signature of the adapters' executables and of the bundle), checks it with
# plutil -lint and codesign --verify, and with --install puts the app in ~/Applications and a `cscode` command on PATH.
#
#   tools/package-macos.sh [--rid osx-arm64|osx-x64]... [--from DIR] [--out DIR] [--server-bundles DIR | --no-server-bundles]
#                          [--netcoredbg PATH] [--js-debug PATH] [--codelldb PATH] [--no-adapters]
#                          [--no-sign | --sign-identity ID [--notarize --notary-profile NAME]]
#                          [--install [--app-dir DIR] [--bin-dir DIR] [--force]]
#
#   --rid               a macOS runtime to package (repeatable; default: this Mac's, osx-arm64 on Apple silicon). Each goes to <out>/<rid>/CsCode.app.
#   --from DIR          bundle this existing osx publish instead of publishing (one --rid only)
#   --out DIR           output directory (default: .scratch/macos)
#   --server-bundles DIR  ship these remote server bundles (tools/publish-server.sh's output); default: built now (linux-x64/arm64, osx-arm64/x64 + portable)
#   --no-server-bundles ship none (remote editing then needs remote.ssh.serverBundles)
#   --netcoredbg PATH   netcoredbg to bundle: its executable or its folder (default: $CSCODE_NETCOREDBG, else the shared cache's
#                       .scratch/tools/netcoredbg, fetched there now by tools/fetch-netcoredbg.sh when missing)
#   --js-debug PATH     js-debug to bundle: dapDebugServer.js, the js-debug folder or the folder holding it (default: $CSCODE_JSDEBUG, else
#                       .scratch/tools/js-debug/<version>, fetched by tools/fetch-js-debug.sh when missing)
#   --codelldb PATH     CodeLLDB to bundle: its adapter, extension/ folder or install folder (default: $CSCODE_CODELLDB, else
#                       .scratch/tools/codelldb/<version>, fetched by tools/fetch-codelldb.sh when missing)
#   --no-adapters       bundle no debug adapter (the app offers the downloads when a debug session needs one)
#   --no-sign           leave the bundle unsigned (Apple silicon will not start it)
#   --sign-identity ID  sign for other Macs: a "Developer ID Application: Name (TEAMID)" identity in your keychain (security find-identity -v
#                       -p codesigning lists them). Hardened runtime, secure timestamp, packaging/macos/*.entitlements, inside out
#                       (tools/macos/make-app-bundle.sh). Without it the signature is ad hoc, which only the Mac that built it accepts
#   --notarize          then notarize: zip the app, `xcrun notarytool submit --keychain-profile NAME --wait`, staple the ticket
#                       (`xcrun stapler staple`, `stapler validate`), check it as Gatekeeper does (`spctl --assess --type execute`) and write
#                       <out>/<rid>/CsCode-<version>-<rid>.zip, the stapled app to hand out. Needs --sign-identity and --notary-profile
#   --notary-profile NAME  the notarytool keychain profile, stored once with: xcrun notarytool store-credentials NAME --apple-id <id>
#                       --team-id <TEAMID> (it asks for an app-specific password; or --key/--key-id/--issuer for an App Store Connect API key)
#   XCRUN=PATH, SPCTL=PATH, CODESIGN=PATH  the tools to run (default xcrun, spctl, codesign; tests put recording stand-ins here)
#   --install           copy the bundle for this Mac's runtime to the app directory and install the `cscode` command
#   --app-dir DIR       where --install puts CsCode.app (default: ~/Applications; /Applications needs an administrator)
#   --bin-dir DIR       where --install puts `cscode` (default: the first writable of /opt/homebrew/bin, /usr/local/bin, ~/.local/bin, ~/bin that
#                       is on PATH; else ~/.local/bin, with the line to add to ~/.zprofile)
#   --force             let --install replace a `cscode` in the bin directory that it did not write
#
# The osx runtime packs are not pinned (only linux-x64's are: tools/fetch-publish-packs.sh), so the publish takes the ones restore picks from
# nuget.org (publish-app.sh --unpinned). By default the signature is ad hoc: the app runs on the Mac that built it; a copy for other Macs
# needs --sign-identity and --notarize (README, "Signing for other Macs"). Runs on macOS's bash 3.2.
#
# Debug adapters are bundled as tools/install.sh bundles them on Linux, in Contents/Resources/debug-adapters laid out as the app reads them
# (src/CsCode.Dap/Adapters/BundledAdapters.cs), so the app debugs .NET, Node.js/TypeScript and Rust/C/C++ (CodeLLDB, the Mac's default) without
# a download. A missing one is fetched into the shared cache (the main checkout's .scratch/tools) by the fetch scripts, which refuse anything
# but the pinned versions and digests (the app's own); one that cannot be had, or is built for another architecture than the --rid, is left
# out with a warning, and the app then offers its download when a session needs it. Only this Mac's architecture is fetched.
set -euo pipefail

# The SDK installer (dotnet-install.sh) puts dotnet in ~/.dotnet without adding it to PATH; use it from there when PATH has none.
if ! command -v dotnet >/dev/null 2>&1 && [[ -x "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" ]]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$PATH"
fi

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
rids=""; from=""; out="$repo/.scratch/macos"; servers=""; no_servers=0; sign=1
adapters=1; netcoredbg_src=""; jsdebug_src=""; codelldb_src=""
install=0; app_dir="$HOME/Applications"; bin_dir=""; force=0
identity=""; notarize=0; notary_profile=""
xcrun=${XCRUN:-xcrun}; spctl=${SPCTL:-spctl}
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rids="$rids $2"; shift 2 ;;
        --from) from=$2; shift 2 ;;
        --out) out=$2; shift 2 ;;
        --server-bundles) servers=$2; shift 2 ;;
        --no-server-bundles) no_servers=1; shift ;;
        --netcoredbg) netcoredbg_src=$2; shift 2 ;;
        --js-debug) jsdebug_src=$2; shift 2 ;;
        --codelldb) codelldb_src=$2; shift 2 ;;
        --no-adapters) adapters=0; shift ;;
        --no-sign) sign=0; shift ;;
        --sign-identity) identity=$2; shift 2 ;;
        --notarize) notarize=1; shift ;;
        --notary-profile) notary_profile=$2; shift 2 ;;
        --install) install=1; shift ;;
        --app-dir) app_dir=$2; shift 2 ;;
        --bin-dir) bin_dir=$2; shift 2 ;;
        --force) force=1; shift ;;
        -h|--help) sed -n '2,/^[^#]/p' "$0" | sed -e '/^[^#]/d'; exit 0 ;; # the header comment
        *) echo "package-macos.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

die() { echo "package-macos.sh: $*" >&2; exit 1; }
warn() { echo "package-macos.sh: warning: $*" >&2; }
[ "$(uname -s)" = Darwin ] || die "run this on a Mac (it needs sips, iconutil, codesign and plutil); tools/macos/make-app-bundle.sh builds an unsigned bundle elsewhere"
for tool in sips iconutil plutil ditto; do command -v "$tool" >/dev/null || die "$tool is required"; done
[ "$sign" = 0 ] || command -v "${CODESIGN:-codesign}" >/dev/null || die "codesign is required (install the Xcode command line tools: xcode-select --install), or pass --no-sign"
if [ -n "$identity" ] && [ "$sign" = 0 ]; then echo "package-macos.sh: --sign-identity and --no-sign contradict each other" >&2; exit 2; fi
if [ "$notarize" = 1 ]; then
    [ -n "$identity" ] || { echo "package-macos.sh: --notarize needs --sign-identity \"Developer ID Application: …\" (Apple notarizes only Developer ID signatures)" >&2; exit 2; }
    [ -n "$notary_profile" ] || { echo "package-macos.sh: --notarize needs --notary-profile NAME (store one with: xcrun notarytool store-credentials NAME)" >&2; exit 2; }
    command -v "$xcrun" >/dev/null || die "--notarize needs xcrun (Xcode's command line tools)"
fi
if [ -n "$notary_profile" ] && [ "$notarize" = 0 ]; then warn "--notary-profile without --notarize: not notarizing"; fi

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

# ------------------------------------------------------------------ debug adapters
# Their sources, settled once: the option, else the variable, else the shared cache, fetched into it when missing (this Mac's architecture).
# The pinned versions are the app's own (the bundled copies must sit where the adapter factories look).
pinned() { sed -n 's/.*public const string Version = "\(v[0-9.]*\)".*/\1/p' "$repo/src/CsCode.Dap/Adapters/$1" | head -n1; }
jsdebug_version=$(pinned JsDebugInstaller.cs)
codelldb_version=$(pinned CodeLldbInstaller.cs)
[ -n "$jsdebug_version" ] && [ -n "$codelldb_version" ] || die "cannot read the pinned js-debug/CodeLLDB versions from src/CsCode.Dap/Adapters"
# Downloads and tools are shared by every worktree of the repository (the main checkout's .scratch), as tools/install.sh reads them.
main=$repo
common=$(git -C "$repo" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)
[ -n "$common" ] && [ -d "$(dirname "$common")" ] && main=$(dirname "$common")
cache="$main/.scratch/tools"
fetched() { # <name> <path in the cache> <fetch script> <its destination variable>=<dir>: the cached copy, fetched now when missing
    if [ ! -e "$2" ]; then
        echo "Fetching $1 into $cache (pinned version and digest)…" >&2 # stdout is the path
        env "$4" bash "$repo/tools/$3" >&2 || { warn "$1: the fetch failed; not bundled"; return 1; }
    fi
    [ -e "$2" ] && printf '%s' "$2"
}
if [ "$adapters" = 1 ]; then
    [ -n "$netcoredbg_src" ] || netcoredbg_src=${CSCODE_NETCOREDBG:-$(fetched netcoredbg "$cache/netcoredbg/netcoredbg" fetch-netcoredbg.sh "NETCOREDBG_DEST=$cache/netcoredbg" || true)}
    [ -n "$jsdebug_src" ] || jsdebug_src=${CSCODE_JSDEBUG:-$(fetched "js-debug $jsdebug_version" "$cache/js-debug/$jsdebug_version/js-debug/src/dapDebugServer.js" fetch-js-debug.sh "JSDEBUG_DEST=$cache/js-debug" || true)}
    [ -n "$codelldb_src" ] || codelldb_src=${CSCODE_CODELLDB:-$(fetched "CodeLLDB $codelldb_version" "$cache/codelldb/$codelldb_version/extension/adapter/codelldb" fetch-codelldb.sh "CODELLDB_DEST=$cache/codelldb" || true)}
fi

# Whether a Mach-O file runs on the runtime's architecture (a universal binary names each of its own).
built_for() { case "$2" in osx-arm64) file -b "$1" | grep -q 'arm64' ;; osx-x64) file -b "$1" | grep -q 'x86_64' ;; esac; }
# Each copies one adapter into the staging folder <stage>, laid out as BundledAdapters reads it (and as tools/install.sh writes it).
stage_netcoredbg() { # <source> <stage> <rid>
    local s=$1 dir
    if [ -f "$s" ]; then dir=$(dirname "$s"); else dir=$s; fi
    [ -f "$dir/netcoredbg" ] || { [ -f "$dir/netcoredbg/netcoredbg" ] && dir="$dir/netcoredbg"; }
    [ -x "$dir/netcoredbg" ] || { warn "netcoredbg: $s holds no netcoredbg executable; not bundled"; return 1; }
    # Its folder is copied whole (it needs its libraries), so it must BE netcoredbg's folder, not e.g. /usr/local/bin.
    [ -f "$dir/libdbgshim.dylib" ] || [ -f "$dir/ManagedPart.dll" ] || { warn "netcoredbg: $dir does not look like netcoredbg's own folder; not bundled"; return 1; }
    built_for "$dir/netcoredbg" "$3" || { warn "netcoredbg: $dir/netcoredbg is not built for $3; not bundled in its app"; return 1; }
    mkdir -p "$2/netcoredbg" && ditto "$dir" "$2/netcoredbg" || die "netcoredbg: copying $dir failed"
}
stage_jsdebug() { # <source> <stage>
    local s=$1 dir=""
    if [ -f "$s" ] && [ "$(basename "$s")" = dapDebugServer.js ]; then dir=$(dirname "$(dirname "$s")")
    elif [ -f "$s/src/dapDebugServer.js" ]; then dir=$s
    elif [ -f "$s/js-debug/src/dapDebugServer.js" ]; then dir="$s/js-debug"
    fi
    [ -n "$dir" ] || { warn "js-debug: no src/dapDebugServer.js under $s; not bundled"; return 1; }
    case "$(cd "$dir/.." && pwd)" in */v[0-9]*) [ "$(basename "$(cd "$dir/.." && pwd)")" = "$jsdebug_version" ] || { warn "js-debug: $dir is not $jsdebug_version (the version the app pins); not bundled"; return 1; } ;; esac
    mkdir -p "$2/js-debug/$jsdebug_version/js-debug" && ditto "$dir" "$2/js-debug/$jsdebug_version/js-debug" || die "js-debug: copying $dir failed"
}
stage_codelldb() { # <source> <stage> <rid>
    local s=$1 inst="" have="" f
    if [ -f "$s" ] && [ "$(basename "$(dirname "$s")")" = adapter ]; then inst=$(cd "$(dirname "$s")/../.." && pwd)
    elif [ -f "$s/adapter/codelldb" ]; then inst=$(cd "$s/.." && pwd)
    elif [ -f "$s/extension/adapter/codelldb" ]; then inst=$s
    fi
    [ -n "$inst" ] && [ -x "$inst/extension/adapter/codelldb" ] || { warn "CodeLLDB: no extension/adapter/codelldb under $s; not bundled"; return 1; }
    if [ -f "$inst/VERSION" ]; then have=$(tr -d ' \n\r' < "$inst/VERSION")
    else have=v$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)".*/\1/p' "$inst/extension/package.json" 2>/dev/null | head -n1); fi
    [ "$have" = "$codelldb_version" ] || { warn "CodeLLDB: $inst is ${have:-of unknown version}, the app pins $codelldb_version; not bundled"; return 1; }
    built_for "$inst/extension/adapter/codelldb" "$3" || { warn "CodeLLDB: $inst is not built for $3; not bundled in its app"; return 1; }
    mkdir -p "$2/codelldb/$codelldb_version/extension" && ditto "$inst/extension" "$2/codelldb/$codelldb_version/extension" || die "CodeLLDB: copying $inst failed"
    for f in VERSION PLATFORM; do [ ! -f "$inst/$f" ] || cp -p "$inst/$f" "$2/codelldb/$codelldb_version/" || die "CodeLLDB: copying $f failed"; done
}

# ------------------------------------------------------------------ notarization
# The signed app goes to Apple's notary service as a zip (ditto keeps the signature and the bundle's symlinks), the ticket is stapled to the
# app so Gatekeeper accepts it offline, Gatekeeper's own assessment must say "accepted" (source=Notarized Developer ID), and the stapled app is
# zipped again for distribution. A rejection prints notarytool's log, which names every file that failed and why.
notarize_app() { # <app> <rid>
    local app=$1 rid=$2 dir zip submission status id version
    dir=$(dirname "$app")
    zip="$dir/.CsCode-notarize.zip"
    rm -f "$zip"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$zip" || die "zipping $app for the notary service failed"
    echo "Submitting $app to Apple's notary service (profile $notary_profile); this usually takes a few minutes…"
    submission=$("$xcrun" notarytool submit "$zip" --keychain-profile "$notary_profile" --wait --output-format plist) \
        || { rm -f "$zip"; die "notarytool submit failed (is the keychain profile '$notary_profile' stored? xcrun notarytool store-credentials $notary_profile)"; }
    rm -f "$zip"
    status=$(printf '%s' "$submission" | plutil -extract status raw -o - - 2>/dev/null || true)
    id=$(printf '%s' "$submission" | plutil -extract id raw -o - - 2>/dev/null || true)
    if [ "$status" != Accepted ]; then
        echo "package-macos.sh: notarization of $app: ${status:-no status} (submission ${id:-unknown}). notarytool's log:" >&2
        [ -z "$id" ] || "$xcrun" notarytool log "$id" --keychain-profile "$notary_profile" >&2 || true
        exit 1
    fi
    echo "Notarized (submission $id); stapling the ticket…"
    "$xcrun" stapler staple "$app" || die "stapling the notarization ticket to $app failed"
    "$xcrun" stapler validate "$app" || die "the stapled ticket of $app does not validate"
    "$spctl" --assess --type execute --verbose=4 "$app" || die "Gatekeeper (spctl --assess) rejects $app"
    version=$(plutil -extract CFBundleShortVersionString raw -o - "$app/Contents/Info.plist" 2>/dev/null || echo 0.0.0)
    rm -f "$dir/CsCode-$version-$rid.zip"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$dir/CsCode-$version-$rid.zip" || die "zipping the stapled app failed"
    echo "Ready to hand out: $dir/CsCode-$version-$rid.zip (signed, notarized, stapled)"
}

bundle_args=()
[ "$sign" = 1 ] || bundle_args+=(--no-sign)
[ -z "$identity" ] || bundle_args+=(--sign-identity "$identity")
[ -z "$servers" ] || bundle_args+=(--server-bundles "$servers")
for rid in $rids; do
    rid_args=()
    stage="$out/.debug-adapters-$rid"
    rm -rf "$stage"
    if [ "$adapters" = 1 ]; then
        bundled=""
        if [ -n "$netcoredbg_src" ]; then stage_netcoredbg "$netcoredbg_src" "$stage" "$rid" && bundled="$bundled netcoredbg" || true
        else warn "netcoredbg: none found (pass --netcoredbg or set CSCODE_NETCOREDBG); not bundled"; fi
        if [ -n "$jsdebug_src" ]; then stage_jsdebug "$jsdebug_src" "$stage" && bundled="$bundled js-debug-$jsdebug_version" || true
        else warn "js-debug: none found (pass --js-debug or set CSCODE_JSDEBUG); not bundled"; fi
        if [ -n "$codelldb_src" ]; then stage_codelldb "$codelldb_src" "$stage" "$rid" && bundled="$bundled CodeLLDB-$codelldb_version" || true
        else warn "CodeLLDB: none found (pass --codelldb or set CSCODE_CODELLDB); not bundled"; fi
        echo "Debug adapters for $rid:${bundled:- none}"
        [ ! -d "$stage" ] || rid_args+=(--debug-adapters "$stage")
    fi
    if [ -n "$from" ]; then publish=$from
    else
        publish="$repo/.scratch/publish/$rid"
        "$repo/tools/publish-app.sh" --rid "$rid" --unpinned --out "$publish"
    fi
    # ${a[@]+...}: bash 3.2 calls an empty array unbound under set -u.
    "$repo/tools/macos/make-app-bundle.sh" --rid "$rid" --from "$publish" --out "$out/$rid" ${bundle_args[@]+"${bundle_args[@]}"} ${rid_args[@]+"${rid_args[@]}"}
    rm -rf "$stage"
    app="$out/$rid/CsCode.app"
    plutil -lint "$app/Contents/Info.plist"
    if [ "$sign" = 1 ]; then "${CODESIGN:-codesign}" --verify --deep --strict --verbose=1 "$app"; fi
    [ "$notarize" = 0 ] || notarize_app "$app" "$rid"
    if [ -n "$identity" ] && [ "$notarize" = 0 ]; then
        echo "Signed with \"$identity\" but not notarized: Gatekeeper on other Macs refuses it until it is (--notarize --notary-profile NAME)."
    fi
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
