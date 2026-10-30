#!/usr/bin/env bash
# Installs CsCode for the current user, so it runs from a terminal (`cscode`) and from the desktop's application menu
# (docs/briefs/app-icon-and-install.md; README.md, "Install"). Per user only: no sudo, nothing outside the home directory. Idempotent: a rerun
# installs the current build as a new version beside the installed one, moves `current` to it and keeps the one before for rollback (older
# versions are removed, except one still running); rerunning the same build replaces it in place.
#
#   tools/install.sh [options]
#
#   --from DIR             install this publish (tools/publish-app.sh's output) instead of publishing now
#   --version ID           the version's folder name (default: <app version>-g<commit>, plus -dirty.<time> for uncommitted changes; with
#                          --from: <app version>-<content hash>)
#   --netcoredbg PATH      netcoredbg to bundle: its executable or its folder (default: $CSCODE_NETCOREDBG, else .scratch/tools/netcoredbg)
#   --js-debug PATH        js-debug to bundle: dapDebugServer.js, the js-debug folder or the folder holding it (default: $CSCODE_JSDEBUG, else
#                          .scratch/tools/js-debug/<version>)
#   --codelldb PATH        CodeLLDB to bundle: its adapter, extension/ folder or install folder (default: $CSCODE_CODELLDB, else
#                          .scratch/downloads/codelldb/<version>)
#   --no-adapters          bundle no debug adapter (the app offers the downloads when a debug session needs one)
#   --tree-sitter DIR      the tree-sitter bundle to ship (a folder with bundle.json); default: the one the publish carries, after
#                          tools/treesitter/build-grammars.sh built it offline from the pinned sources
#   --no-tree-sitter       ship no tree-sitter bundle (TextMate colours every language that has a TextMate grammar)
#   --server-bundles DIR   the remote server bundles to ship (tools/publish-server.sh's output); default: built now (linux-x64 + portable)
#   --no-server-bundles    ship none (remote editing then needs remote.ssh.serverBundles)
#   --no-refresh           do not refresh the desktop's caches (update-desktop-database, kbuildsycoca6, gtk-update-icon-cache); for tests
#
# Layout (XDG_DATA_HOME defaults to ~/.local/share):
#   $XDG_DATA_HOME/cscode/<version>/        the app (self-contained: its own .NET runtime), tree-sitter/, shellIntegration/, server-bundles/,
#                                           debug-adapters/, uninstall.sh and .cscode-install/ (what this script wrote there)
#   $XDG_DATA_HOME/cscode/current           symlink to the installed version (roll back: ln -sfn <previous> current)
#   $XDG_DATA_HOME/cscode/install-manifest.tsv   every file and directory this script created, for tools/uninstall.sh
#   ~/.local/bin/cscode                     the launcher (passes every argument to the app)
#   $XDG_DATA_HOME/applications/cscode.desktop and icons/hicolor/{16..512}/apps/cscode.png, scalable/apps/cscode.svg
# The app's own data shares $XDG_DATA_HOME/cscode (sessions, tools, themes...); version folders start with a digit and never collide with it.
#
# Building needs the .NET SDK on PATH; on this machine's Nix SDK run it as `bash .scratch/notes/with-nix-env.sh tools/install.sh` (the library
# path only serves the build: the installed app runs on the system's libraries, and the desktop tools run without it).
set -euo pipefail
umask 022

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/.." && pwd)
CSCODE_SCRIPT=install.sh
# shellcheck source=uninstall.sh
source "$here/uninstall.sh"
say() { cscode_say "$@"; }
warn() { cscode_warn "$@"; }
die() { cscode_die "$@"; }

from=""; version=""; refresh=1; adapters=1; treesitter=1; servers=1
netcoredbg_src=""; jsdebug_src=""; codelldb_src=""; treesitter_src=""; servers_src=""
need() { [ $# -ge 2 ] && [ -n "$2" ] || die "$1 needs a value"; }
while [ $# -gt 0 ]; do
    case "$1" in
        --from) need "$@"; from=$2; shift 2 ;;
        --version) need "$@"; version=$2; shift 2 ;;
        --netcoredbg) need "$@"; netcoredbg_src=$2; shift 2 ;;
        --js-debug) need "$@"; jsdebug_src=$2; shift 2 ;;
        --codelldb) need "$@"; codelldb_src=$2; shift 2 ;;
        --no-adapters) adapters=0; shift ;;
        --tree-sitter) need "$@"; treesitter_src=$2; shift 2 ;;
        --no-tree-sitter) treesitter=0; shift ;;
        --server-bundles) need "$@"; servers_src=$2; shift 2 ;;
        --no-server-bundles) servers=0; shift ;;
        --no-refresh) refresh=0; shift ;;
        -h|--help) sed -n '2,38p' "$0"; exit 0 ;;
        *) die "unknown argument '$1' (see --help)" ;;
    esac
done

[ "$(id -u)" != 0 ] || die "run it as the user who will use CsCode, not as root: it installs into that user's home only (no sudo)"
cscode_locations
for tool in cp mv ln find sort sha256sum readlink mktemp sed awk grep cmp; do command -v "$tool" >/dev/null || die "$tool is required"; done
case "$(uname -m)" in
    x86_64|amd64) rid=linux-x64 ;;
    aarch64|arm64) rid=linux-arm64 ;;
    *) die "no CsCode build for $(uname -m)" ;;
esac
[ "$(uname -s)" = Linux ] || die "this installer is for Linux (Windows: tools/install.ps1; macOS: tools/macos/make-app-bundle.sh)"
branding="$repo/assets/branding"
template="$repo/packaging/linux/cscode.desktop.in"
[ -f "$template" ] && [ -f "$branding/cscode.svg" ] || die "run it from a CsCode source tree (packaging/linux and assets/branding are missing)"
# The pinned adapter versions are the app's own (the bundled copies must sit where the adapter factories look).
pinned() { sed -n 's/.*public const string Version = "\(v[0-9.]*\)".*/\1/p' "$repo/src/CsCode.Dap/Adapters/$1" | head -n1; }
jsdebug_version=$(pinned JsDebugInstaller.cs)
codelldb_version=$(pinned CodeLldbInstaller.cs)
[ -n "$jsdebug_version" ] && [ -n "$codelldb_version" ] || die "cannot read the pinned js-debug/CodeLLDB versions from src/CsCode.Dap/Adapters"
# Downloads and tools are shared by every worktree of the repository (the main checkout's .scratch).
main=$repo
common=$(git -C "$repo" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)
[ -n "$common" ] && [ -d "$(dirname "$common")/.scratch" ] && main=$(dirname "$common")

# ------------------------------------------------------------------ what was installed before (the manifest)

declare -A prior_files=()
prior_versions=(); prior_dirs=(); prior_current=""; prior_previous=""
if [ -f "$cscode_manifest" ]; then
    while IFS=$'\t' read -r kind a b; do
        case "$kind" in
            version) prior_versions+=("$a") ;;
            current) prior_current=$a ;;
            previous) prior_previous=$a ;;
            file) prior_files["$b"]=$a ;;
            dir) prior_dirs+=("$a") ;;
        esac
    done < "$cscode_manifest"
fi

# ------------------------------------------------------------------ refuse before doing any work: nothing that is not ours is replaced

launcher="$cscode_bin/cscode"
desktop="$cscode_apps/cscode.desktop"
icon_sizes=(16 32 48 64 128 256 512)
if [ -e "$launcher" ] || [ -L "$launcher" ]; then
    if [ -L "$launcher" ] || ! grep -q "$CSCODE_MARKER" "$launcher" 2>/dev/null; then
        die "$launcher exists and was not created by this installer; move it away (or remove it) and run this again"
    fi
fi
if [ -e "$desktop" ] || [ -L "$desktop" ]; then
    { [ ! -L "$desktop" ] && grep -q '^X-CsCode-Installer=' "$desktop" 2>/dev/null; } || die "$desktop exists and was not created by this installer"
fi
icon_target() { if [ "$1" = scalable ]; then echo "$cscode_icons/scalable/apps/cscode.svg"; else echo "$cscode_icons/${1}x${1}/apps/cscode.png"; fi; }
icon_source() { if [ "$1" = scalable ]; then echo "$branding/cscode.svg"; else echo "$branding/cscode-$1.png"; fi; }
for size in "${icon_sizes[@]}" scalable; do
    t=$(icon_target "$size")
    if [ -e "$t" ] || [ -L "$t" ]; then
        [ -n "${prior_files[$t]+set}" ] || cmp -s "$t" "$(icon_source "$size")" || die "$t exists and was not created by this installer"
    fi
done
if [ -e "$cscode_root/current" ] && [ ! -L "$cscode_root/current" ]; then die "$cscode_root/current exists and is not the installer's symlink"; fi
if [ -n "$version" ]; then
    [[ "$version" =~ ^[0-9][0-9A-Za-z._+-]*$ ]] || die "--version must start with a digit and use only letters, digits and . _ + -"
fi

# ------------------------------------------------------------------ the build to install

if [ -n "$from" ]; then
    src=$(cd "$from" 2>/dev/null && pwd) || die "--from $from: no such directory"
    srcdesc="publish $src"
else
    command -v dotnet >/dev/null || die "dotnet was not found: run it where the .NET SDK is on PATH (here: bash .scratch/notes/with-nix-env.sh tools/install.sh), or pass --from <publish dir>"
    if [ "$treesitter" = 1 ] && [ -z "$treesitter_src" ]; then
        # Offline: the pinned sources must already be downloaded (tools/treesitter/fetch-grammars.sh); nothing is fetched by an install.
        if "$here/treesitter/fetch-grammars.sh" --offline >/dev/null 2>&1; then
            say "Building the tree-sitter grammars ($rid)…"
            "$here/treesitter/build-grammars.sh" --target "$rid" >/dev/null || warn "tools/treesitter/build-grammars.sh failed: the publish carries the previous bundle, if any"
        else
            warn "the pinned tree-sitter sources are not all downloaded (tools/treesitter/fetch-grammars.sh): no new grammar build"
        fi
    fi
    say "Publishing CsCode ($rid)…"
    "$here/publish-app.sh" --rid "$rid" >/dev/null
    src="$repo/.scratch/publish/$rid"
    srcdesc="tools/publish-app.sh --rid $rid"
fi
[ -x "$src/cscode" ] && [ -f "$src/cscode.dll" ] || die "$src is not a CsCode publish (cscode and cscode.dll missing)"

appver=$(env -u LD_LIBRARY_PATH "$src/cscode" --version 2>/dev/null | head -n1 | tr -d '\r') || true
[[ "$appver" =~ ^[0-9][0-9A-Za-z._+-]*$ ]] || appver=0.0.0
commit=$(git -C "$repo" rev-parse HEAD 2>/dev/null || echo unknown)
if [ -z "$version" ]; then
    if [ -n "$from" ]; then
        # Same files, same version: a rerun with the same publish replaces it in place.
        hash=$(cd "$src" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -c1-12)
        version="$appver-$hash"
    elif [ "$commit" != unknown ]; then
        version="$appver-g${commit:0:10}"
        if [ -n "$(git -C "$repo" status --porcelain 2>/dev/null)" ]; then version="$version-dirty.$(date -u +%Y%m%d%H%M%S)"; fi
    else
        version="$appver-$(date -u +%Y%m%d%H%M%S)"
    fi
fi

# ------------------------------------------------------------------ stage the version folder (same file system: moved into place at once)

created_dirs=()
ensure_dir() { # mkdir -p that records every directory it creates (tools/uninstall.sh removes them when empty)
    local d=$1 missing=() i
    while [ ! -d "$d" ]; do
        [ ! -e "$d" ] && [ ! -L "$d" ] || die "$d exists and is not a directory"
        missing+=("$d")
        d=$(dirname "$d")
    done
    for ((i = ${#missing[@]} - 1; i >= 0; i--)); do mkdir -m 755 -- "${missing[$i]}"; created_dirs+=("${missing[$i]}"); done
}

ensure_dir "$cscode_root"
stage=$(mktemp -d "$cscode_root/.install-XXXXXX")
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cscode-install.XXXXXX")
cleanup() { [ -z "${stage:-}" ] || rm -rf -- "$stage"; rm -rf -- "$tmp"; }
trap cleanup EXIT
chmod 755 "$stage"
say "Installing CsCode $version into $cscode_root/$version"
cp -a -- "$src/." "$stage/"
summary=()

# The tree-sitter bundle (src/CsCode.Editor.TreeSitter: <app>/tree-sitter, checked by its bundle.json before anything is loaded).
if [ "$treesitter" = 0 ]; then
    rm -rf -- "$stage/tree-sitter"
    summary+=("tree-sitter: not shipped (--no-tree-sitter)")
else
    if [ -n "$treesitter_src" ]; then
        [ -f "$treesitter_src/bundle.json" ] || die "--tree-sitter $treesitter_src: no bundle.json there"
        rm -rf -- "$stage/tree-sitter"; mkdir -p "$stage/tree-sitter"; cp -a -- "$treesitter_src/." "$stage/tree-sitter/"
    elif [ ! -f "$stage/tree-sitter/bundle.json" ] && [ -f "$repo/artifacts/tree-sitter/$rid/bundle.json" ]; then
        mkdir -p "$stage/tree-sitter"; cp -a -- "$repo/artifacts/tree-sitter/$rid/." "$stage/tree-sitter/"
    fi
    rm -rf -- "$stage/tree-sitter/.work"
    if [ -f "$stage/tree-sitter/bundle.json" ]; then summary+=("tree-sitter: $(find "$stage/tree-sitter/grammars" -maxdepth 1 -name '*.so' | wc -l) grammars")
    else summary+=("tree-sitter: none (build it with tools/treesitter/build-grammars.sh); TextMate colours what it can"); fi
fi

# The terminal's shell-integration scripts (src/CsCode.Terminal: <app>/shellIntegration). A publish carries them; an older one gets the sources.
if [ ! -f "$stage/shellIntegration/cscode.bash" ]; then
    if [ -f "$repo/src/CsCode.Terminal/ShellIntegration/cscode.bash" ]; then
        mkdir -p "$stage/shellIntegration"; cp -a -- "$repo/src/CsCode.Terminal/ShellIntegration/." "$stage/shellIntegration/"
    else
        warn "no shell-integration scripts: terminals run without command decorations"
    fi
fi

# The remote server bundles (src/CsCode.Remote: <app>/server-bundles/manifest.json), uploaded to a remote host on first connect.
if [ "$servers" = 0 ]; then
    rm -rf -- "$stage/server-bundles"
    summary+=("remote server bundles: not shipped (--no-server-bundles)")
else
    if [ -n "$servers_src" ]; then
        [ -f "$servers_src/manifest.json" ] || die "--server-bundles $servers_src: no manifest.json there"
        rm -rf -- "$stage/server-bundles"; mkdir -p "$stage/server-bundles"; cp -a -- "$servers_src/." "$stage/server-bundles/"
    elif [ ! -f "$stage/server-bundles/manifest.json" ]; then
        if command -v dotnet >/dev/null; then
            say "Building the remote server bundles…"
            "$here/publish-server.sh" --out "$stage/server-bundles" >/dev/null || { warn "tools/publish-server.sh failed: no remote server bundles"; rm -rf -- "$stage/server-bundles"; }
        else
            warn "dotnet was not found: no remote server bundles (pass --server-bundles DIR)"
        fi
    fi
    if [ -f "$stage/server-bundles/manifest.json" ]; then summary+=("remote server bundles: $(find "$stage/server-bundles" -maxdepth 1 -name '*.tar.gz' | wc -l)")
    else summary+=("remote server bundles: none"); fi
fi

# Debug adapters already on this machine, laid out as the adapter factories expect (src/CsCode.Dap/Adapters/BundledAdapters.cs). Nothing is
# downloaded: a missing one is reported, and the app offers its download when a session needs it.
first_existing() { local p; for p in "$@"; do if [ -n "$p" ] && [ -e "$p" ]; then printf '%s' "$p"; return 0; fi; done; return 1; }
bundle_netcoredbg() {
    local s=$1 dir
    if [ -f "$s" ]; then dir=$(dirname "$s"); else dir=$s; fi
    [ -f "$dir/netcoredbg" ] || { [ -f "$dir/netcoredbg/netcoredbg" ] && dir="$dir/netcoredbg"; }
    [ -x "$dir/netcoredbg" ] || { warn "netcoredbg: $s holds no netcoredbg executable; not bundled"; return 1; }
    # Its folder is copied whole (it needs its libraries), so it must BE netcoredbg's folder, not e.g. /usr/bin.
    [ -f "$dir/libdbgshim.so" ] || [ -f "$dir/ManagedPart.dll" ] || { warn "netcoredbg: $dir does not look like netcoredbg's own folder; not bundled"; return 1; }
    mkdir -p "$stage/debug-adapters/netcoredbg" && cp -a -- "$dir/." "$stage/debug-adapters/netcoredbg/" || die "netcoredbg: copying $dir failed"
}
bundle_jsdebug() {
    local s=$1 dir=""
    if [ -f "$s" ] && [ "$(basename "$s")" = dapDebugServer.js ]; then dir=$(dirname "$(dirname "$s")")
    elif [ -f "$s/src/dapDebugServer.js" ]; then dir=$s
    elif [ -f "$s/js-debug/src/dapDebugServer.js" ]; then dir="$s/js-debug"
    fi
    [ -n "$dir" ] || { warn "js-debug: no src/dapDebugServer.js under $s; not bundled"; return 1; }
    case "$(cd "$dir/.." && pwd)" in */v[0-9]*) [ "$(basename "$(cd "$dir/.." && pwd)")" = "$jsdebug_version" ] || { warn "js-debug: $dir is not $jsdebug_version (the version the app pins); not bundled"; return 1; } ;; esac
    mkdir -p "$stage/debug-adapters/js-debug/$jsdebug_version/js-debug" && cp -a -- "$dir/." "$stage/debug-adapters/js-debug/$jsdebug_version/js-debug/" \
        || die "js-debug: copying $dir failed"
}
bundle_codelldb() {
    local s=$1 inst="" have=""
    if [ -f "$s" ] && [ "$(basename "$(dirname "$s")")" = adapter ]; then inst=$(cd "$(dirname "$s")/../.." && pwd)
    elif [ -f "$s/adapter/codelldb" ]; then inst=$(cd "$s/.." && pwd)
    elif [ -f "$s/extension/adapter/codelldb" ]; then inst=$s
    fi
    [ -n "$inst" ] && [ -x "$inst/extension/adapter/codelldb" ] || { warn "CodeLLDB: no extension/adapter/codelldb under $s; not bundled"; return 1; }
    if [ -f "$inst/VERSION" ]; then have=$(tr -d ' \n\r' < "$inst/VERSION")
    else have=v$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)".*/\1/p' "$inst/extension/package.json" 2>/dev/null | head -n1); fi
    [ "$have" = "$codelldb_version" ] || { warn "CodeLLDB: $inst is ${have:-of unknown version}, the app pins $codelldb_version; not bundled"; return 1; }
    mkdir -p "$stage/debug-adapters/codelldb/$codelldb_version" && cp -a -- "$inst/extension" "$stage/debug-adapters/codelldb/$codelldb_version/" \
        || die "CodeLLDB: copying $inst failed"
    for f in VERSION PLATFORM; do [ ! -f "$inst/$f" ] || cp -a -- "$inst/$f" "$stage/debug-adapters/codelldb/$codelldb_version/" || die "CodeLLDB: copying $f failed"; done
}
rm -rf -- "$stage/debug-adapters"
if [ "$adapters" = 1 ]; then
    bundled=()
    s=${netcoredbg_src:-$(first_existing "${CSCODE_NETCOREDBG:-}" "$main/.scratch/tools/netcoredbg" "$repo/.scratch/tools/netcoredbg" || true)}
    if [ -n "$s" ]; then bundle_netcoredbg "$s" && bundled+=(netcoredbg) || true; else warn "netcoredbg: none found (pass --netcoredbg or set CSCODE_NETCOREDBG); not bundled"; fi
    s=${jsdebug_src:-$(first_existing "${CSCODE_JSDEBUG:-}" "$main/.scratch/tools/js-debug/$jsdebug_version" "$repo/.scratch/tools/js-debug/$jsdebug_version" || true)}
    if [ -n "$s" ]; then bundle_jsdebug "$s" && bundled+=("js-debug $jsdebug_version") || true; else warn "js-debug: none found (pass --js-debug or set CSCODE_JSDEBUG); not bundled"; fi
    s=${codelldb_src:-$(first_existing "${CSCODE_CODELLDB:-}" "$main/.scratch/downloads/codelldb/$codelldb_version" "$main/.scratch/tools/codelldb/$codelldb_version" || true)}
    if [ -n "$s" ]; then bundle_codelldb "$s" && bundled+=("CodeLLDB $codelldb_version") || true; else warn "CodeLLDB: none found (pass --codelldb or set CSCODE_CODELLDB); not bundled"; fi
    summary+=("debug adapters: ${bundled[*]:-none}")
else
    summary+=("debug adapters: not shipped (--no-adapters)")
fi

# What this script wrote into the version folder, for an exact removal later (tools/uninstall.sh, and the pruning of old versions below).
cp -- "$here/uninstall.sh" "$stage/uninstall.sh"
chmod 755 "$stage/uninstall.sh"
mkdir -p "$stage/.cscode-install"
{
    printf 'version\t%s\n' "$version"
    printf 'app-version\t%s\n' "$appver"
    printf 'commit\t%s\n' "$commit"
    printf 'source\t%s\n' "$srcdesc"
    printf 'installed\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "$stage/.cscode-install/info"
[ -z "$(cd "$stage" && find . -name $'*\t*' -o -name $'*\n*')" ] || die "a file name in the build contains a tab or a newline"
(cd "$stage" && find . -mindepth 1 \( -type d -printf 'dir\t%P\n' \) -o \( -printf 'file\t%P\n' \)) | LC_ALL=C sort -t $'\t' -k2 > "$tmp/files.tsv"
printf 'file\t.cscode-install/files.tsv\n' >> "$tmp/files.tsv"
mv -- "$tmp/files.tsv" "$stage/.cscode-install/files.tsv"

# ------------------------------------------------------------------ the launcher and the desktop entry, prepared (and validated) first

sq() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }
cat > "$tmp/cscode" <<LAUNCHER
#!/bin/sh
# CsCode launcher ($CSCODE_MARKER). Written by CsCode's tools/install.sh, removed by its uninstall.sh; a reinstall replaces it.
# Every argument goes to the installed app: in background mode (the default) that is the launcher role, which shows or starts the session's
# window and returns (docs/SESSIONS.md, "Command line").
app=$(sq "$cscode_root/current/cscode")
if [ -z "\${DOTNET_ROOT:-}" ]; then
    # .NET tools the app starts (csharp-ls, marksman) need DOTNET_ROOT when dotnet is not where they look by default (Nix, ~/.dotnet),
    # also when CsCode is started from the desktop's menu rather than a shell. Only a dotnet with a runtime next to it counts.
    for candidate in "\$(command -v dotnet 2>/dev/null)" "\${HOME:+\$HOME/.dotnet/dotnet}" "\${HOME:+\$HOME/.nix-profile/bin/dotnet}" \\
        /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /usr/lib64/dotnet/dotnet /usr/local/share/dotnet/dotnet /opt/dotnet/dotnet; do
        [ -n "\$candidate" ] && [ -x "\$candidate" ] || continue
        real=\$(readlink -f "\$candidate" 2>/dev/null) || continue
        if [ -d "\${real%/*}/shared/Microsoft.NETCore.App" ]; then
            DOTNET_ROOT=\${real%/*}
            export DOTNET_ROOT
            break
        fi
    done
fi
# Global .NET tools (csharp-ls) for the processes the app starts; a desktop session's PATH often lacks them.
if [ -n "\${HOME:-}" ]; then
    case ":\${PATH:-}:" in
        *":\$HOME/.dotnet/tools:"*) ;;
        *) PATH="\${PATH:-/usr/local/bin:/usr/bin:/bin}:\$HOME/.dotnet/tools" ;;
    esac
    export PATH
fi
exec "\$app" "\$@"
LAUNCHER
chmod 755 "$tmp/cscode"

# The desktop entry: Exec and TryExec name the launcher by its absolute path, so the menu works even when the desktop session's PATH lacks
# ~/.local/bin. Quoted as the Desktop Entry Specification asks when the path has reserved characters.
exec_arg() {
    local s=${1//%/%%}
    case "$s" in
        *[!A-Za-z0-9/._+-]*) s=$(printf '%s' "$s" | sed -e 's/[\\"`$]/\\&/g'); s="\"$s\""; s=${s//\\/\\\\} ;;
    esac
    printf '%s' "$s"
}
sed_value() { printf '%s' "$1" | sed 's/[&|\\]/\\&/g'; }
sed -e "s|@EXEC@|$(sed_value "$(exec_arg "$launcher")")|g" -e "s|@TRYEXEC@|$(sed_value "${launcher//\\/\\\\}")|g" \
    -e "s|@MARKER@|$CSCODE_MARKER|g" "$template" > "$tmp/cscode.desktop"
chmod 644 "$tmp/cscode.desktop"
if command -v desktop-file-validate >/dev/null 2>&1; then
    out=$(env -u LD_LIBRARY_PATH desktop-file-validate "$tmp/cscode.desktop" 2>&1) || die "the desktop entry does not validate:"$'\n'"$out"
    # Hints are suggestions (e.g. "TextEditor can be extended with Utility"); the categories are the ones the brief asks for.
    out=$(printf '%s\n' "$out" | grep -v ': hint: ' || true)
    [ -z "$out" ] || warn "desktop-file-validate: $out"
fi

# ------------------------------------------------------------------ the manifest (written as soon as anything is in place, then again at the end)

kept=()
write_manifest() {
    local v t d
    {
        printf '# CsCode install manifest: what tools/install.sh created. tools/uninstall.sh removes exactly this. Do not edit.\n'
        printf 'format\t1\n'
        printf 'current\t%s\n' "$version"
        printf 'previous\t%s\n' "$previous"
        for v in "${kept[@]}"; do printf 'version\t%s\n' "$v"; done
        printf 'link\t%s\n' "$cscode_root/current"
        # only what exists: before the launcher, the entry and the icons are written, nothing of ours is at those paths
        for t in "$launcher" "$desktop" "${icon_paths[@]}"; do
            [ ! -f "$t" ] || printf 'file\t%s\t%s\n' "$(cscode_sha256 "$t")" "$t"
        done
        for d in "${prior_dirs[@]+"${prior_dirs[@]}"}" "${created_dirs[@]+"${created_dirs[@]}"}"; do [ ! -d "$d" ] || printf 'dir\t%s\n' "$d"; done | sort -u
    } > "$tmp/manifest"
    mv -f -- "$tmp/manifest" "$cscode_manifest"
}
icon_paths=()
for size in "${icon_sizes[@]}" scalable; do icon_paths+=("$(icon_target "$size")"); done

# ------------------------------------------------------------------ move the version into place; `current` follows

target="$cscode_root/$version"
if [ -e "$target" ] || [ -L "$target" ]; then
    [ ! -L "$target" ] && [ -f "$target/.cscode-install/info" ] || die "$target exists and was not installed by this script"
    if cscode_in_use "$target"; then warn "CsCode $version is running: it goes on running from the replaced files until it exits"; fi
    replaced="$cscode_root/.replaced-$$"
    mv -T -- "$target" "$replaced"
    mv -T -- "$stage" "$target"; stage=""
    cscode_remove_version "$replaced" || warn "left $replaced"
else
    mv -T -- "$stage" "$target"; stage=""
fi
before=""
[ ! -L "$cscode_root/current" ] || before=$(readlink "$cscode_root/current")
previous=""
if [ -n "$before" ] && [ "$before" != "$version" ] && [ -f "$cscode_root/$before/.cscode-install/info" ]; then previous=$before
elif [ -n "$prior_previous" ] && [ "$prior_previous" != "$version" ] && [ -d "$cscode_root/$prior_previous" ]; then previous=$prior_previous
fi
kept=("$version")
for v in "${prior_versions[@]+"${prior_versions[@]}"}" "$previous"; do
    [ -n "$v" ] || continue
    case " ${kept[*]} " in *" $v "*) ;; *) kept+=("$v") ;; esac
done
write_manifest
ln -sfn -- "$version" "$cscode_root/.current-$$"
mv -T -f -- "$cscode_root/.current-$$" "$cscode_root/current"

# ------------------------------------------------------------------ the launcher, the desktop entry and the icons

ensure_dir "$cscode_bin"
mv -f -- "$tmp/cscode" "$launcher"
ensure_dir "$cscode_apps"
mv -f -- "$tmp/cscode.desktop" "$desktop"
for size in "${icon_sizes[@]}" scalable; do
    t=$(icon_target "$size")
    ensure_dir "$(dirname "$t")"
    cp -f -- "$(icon_source "$size")" "$t.new-$$"
    chmod 644 "$t.new-$$"
    mv -f -- "$t.new-$$" "$t"
done
write_manifest

# ------------------------------------------------------------------ keep the installed and the previous version; remove older ones

remaining=("$version")
[ -z "$previous" ] || remaining+=("$previous")
pruned=()
for v in "${kept[@]}"; do
    case " ${remaining[*]} " in *" $v "*) continue ;; esac
    case "$v" in ''|*/*|.|..) continue ;; esac
    if [ ! -d "$cscode_root/$v" ]; then continue
    elif cscode_in_use "$cscode_root/$v"; then
        warn "CsCode $v is still running: kept (a later install removes it)"
        remaining+=("$v")
    elif cscode_remove_version "$cscode_root/$v"; then
        pruned+=("$v")
    fi
done
kept=("${remaining[@]}")
write_manifest

[ "$refresh" = 0 ] || cscode_refresh_caches

say "Installed CsCode $version (app $appver)."
for line in "${summary[@]}"; do say "  $line"; done
say "  launcher: $launcher"
say "  desktop entry: $desktop"
[ -z "$previous" ] || say "  previous version kept for rollback: $previous (ln -sfn $previous $cscode_root/current)"
[ ${#pruned[@]} = 0 ] || say "  removed older version(s): ${pruned[*]}"
case ":${PATH:-}:" in *":$cscode_bin:"*) ;; *) say "Note: $cscode_bin is not on PATH in this shell; add it to run \`cscode\` from a terminal." ;; esac
if [ -n "$before" ] && [ "$before" != "$version" ]; then
    say "CsCode windows that are already running keep the old version until they exit: \`cscode --quit --all\` restarts them on this one."
fi
say "Uninstall: $cscode_root/current/uninstall.sh (keeps your settings and data)."
