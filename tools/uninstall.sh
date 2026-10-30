#!/usr/bin/env bash
# Removes what tools/install.sh installed for this user, exactly the files its manifest lists, and nothing else: the user's settings
# (~/.config/cscode), state, sessions, logs, caches and anything the app downloaded into its data directory stay. Idempotent: without a manifest
# there is nothing to remove and it says so. A copy ships inside every installed version, so it works after the source tree is gone:
#
#   ~/.local/share/cscode/current/uninstall.sh [--no-refresh]      (or tools/uninstall.sh)
#
#   --no-refresh   do not refresh the desktop's caches afterwards (update-desktop-database, kbuildsycoca6, gtk-update-icon-cache); for tests
#
# It refuses while a CsCode process runs from the installed files (quit it first: `cscode --quit --all`). A launcher or desktop entry that no
# longer carries the installer's marker, or an icon whose content changed, was replaced by someone else and is kept. Directories the installer
# created are removed only when empty. Locations follow tools/install.sh: XDG_DATA_HOME (default ~/.local/share) and ~/.local/bin.
#
# tools/install.sh sources this file for the shared functions below (the main part runs only when the file is executed).
set -euo pipefail

cscode_say() { echo "$*"; }
cscode_warn() { echo "${CSCODE_SCRIPT:-uninstall.sh}: warning: $*" >&2; }
cscode_die() { echo "${CSCODE_SCRIPT:-uninstall.sh}: $*" >&2; exit 1; }

# The installer's marker: present in the launcher (a comment) and the desktop entry (X-CsCode-Installer). A file without it is not ours.
CSCODE_MARKER="cscode-install-marker"

# Sets the locations (the same rules as the app's own data directory, src/CsCode.Platform/AppPaths.cs: XDG_DATA_HOME only when absolute).
cscode_locations() {
    [ -n "${HOME:-}" ] && [ "$HOME" != / ] || cscode_die "HOME is not set"
    local xdg=${XDG_DATA_HOME:-}
    case "$xdg" in /*) cscode_data=$xdg ;; *) cscode_data="$HOME/.local/share" ;; esac
    cscode_root="$cscode_data/cscode"
    cscode_bin="$HOME/.local/bin"
    cscode_apps="$cscode_data/applications"
    cscode_icons="$cscode_data/icons/hicolor"
    cscode_manifest="$cscode_root/install-manifest.tsv"
    case "$cscode_root$cscode_bin" in *$'\t'*|*$'\n'*) cscode_die "the home directory's path contains a tab or a newline: not supported" ;; esac
}

cscode_sha256() { sha256sum "$1" | cut -d' ' -f1; }

# True when a running process's executable lies under $1 (the app, or an adapter it started from the bundle). Other users' processes are not
# readable and do not count.
cscode_in_use() {
    local dir=${1%/} exe target
    for exe in /proc/[0-9]*/exe; do
        target=$(readlink "$exe" 2>/dev/null) || continue
        case "$target" in "$dir"/*) return 0 ;; esac
    done
    return 1
}

# Removes one installed version exactly: the files and directories its .cscode-install/files.tsv lists (written by install.sh), then the version
# directory when nothing else is left in it. Returns 1 (and keeps what is left) when it holds files the installer did not create.
cscode_remove_version() {
    local dir=${1%/} list kind path
    list="$dir/.cscode-install/files.tsv"
    [ -d "$dir" ] && [ ! -L "$dir" ] || return 0
    [ -f "$list" ] || { cscode_warn "$dir has no file list (.cscode-install/files.tsv); left in place"; return 1; }
    local dirs=() files=()
    while IFS=$'\t' read -r kind path; do
        case "$path" in ''|/*|..|../*|*/../*|*/..) continue ;; esac # only paths inside the version directory
        case "$kind" in
            file) files+=("$path") ;;
            dir) dirs+=("$path") ;;
        esac
    done < "$list"
    # Every parent of a listed file is a listed directory: when none of them has become a symlink, no removal can reach outside the version.
    local d f
    for d in "${dirs[@]+"${dirs[@]}"}"; do
        if [ -L "$dir/$d" ]; then cscode_warn "kept $dir: $d is now a symbolic link"; return 1; fi
    done
    for f in "${files[@]+"${files[@]}"}"; do
        if [ -f "$dir/$f" ] || [ -L "$dir/$f" ]; then rm -f -- "$dir/$f"; fi
    done
    rm -f -- "$list"
    # deepest first: a directory is removed once its listed contents are gone, and only when nothing else is in it
    while IFS= read -r d; do
        [ -n "$d" ] && rmdir -- "$dir/$d" 2>/dev/null || true
    done < <(printf '%s\n' "${dirs[@]+"${dirs[@]}"}" | awk '{ print gsub("/", "/") "\t" $0 }' | sort -rn | cut -f2-)
    rmdir -- "$dir/.cscode-install" 2>/dev/null || true
    if rmdir -- "$dir" 2>/dev/null; then return 0; fi
    cscode_warn "kept $dir: it holds files the installer did not create"
    return 1
}

# A file of the manifest is still the installer's when it carries the marker (launcher, desktop entry) or has the recorded content (icons).
cscode_still_ours() {
    local path=$1 sha=$2
    [ -f "$path" ] && [ ! -L "$path" ] || return 1
    if grep -q "$CSCODE_MARKER\|X-CsCode-Installer=" "$path" 2>/dev/null; then return 0; fi
    [ "$(cscode_sha256 "$path")" = "$sha" ]
}

# Refreshes the desktop's caches that exist (docs/briefs/app-icon-and-install.md). The Qt/GTK tools run without a development shell's
# LD_LIBRARY_PATH (the Nix shell's libraries would be loaded into KDE's tools otherwise). An icon cache is only ever updated, never created: a
# cache the user does not have would go stale and hide icons other programs install later. None of them inherits the install lock (fd 9).
cscode_refresh_caches() {
    local run=(env -u LD_LIBRARY_PATH)
    if command -v update-desktop-database >/dev/null 2>&1 && [ -d "$cscode_apps" ]; then
        "${run[@]}" update-desktop-database -q "$cscode_apps" 9<&- || cscode_warn "update-desktop-database failed"
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1 && [ -f "$cscode_icons/icon-theme.cache" ]; then
        "${run[@]}" gtk-update-icon-cache -q -t -f "$cscode_icons" 9<&- || cscode_warn "gtk-update-icon-cache failed"
    fi
    if command -v kbuildsycoca6 >/dev/null 2>&1; then
        "${run[@]}" kbuildsycoca6 >/dev/null 2>&1 9<&- || cscode_warn "kbuildsycoca6 failed"
    elif command -v kbuildsycoca5 >/dev/null 2>&1; then
        "${run[@]}" kbuildsycoca5 >/dev/null 2>&1 9<&- || cscode_warn "kbuildsycoca5 failed"
    fi
}

cscode_uninstall_main() {
    local refresh=1
    while [ $# -gt 0 ]; do
        case "$1" in
            --no-refresh) refresh=0; shift ;;
            -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed -e 's/^#//' -e 's/^ //'; exit 0 ;;
            *) cscode_die "unknown argument '$1'" ;;
        esac
    done
    [ "$(id -u)" != 0 ] || cscode_die "run it as the user CsCode was installed for, not as root"
    cscode_locations
    if [ ! -f "$cscode_manifest" ]; then
        cscode_say "CsCode is not installed for $(id -un) (no $cscode_manifest): nothing to remove."
        return 0
    fi
    if command -v flock >/dev/null 2>&1; then exec 9<"$cscode_root"; flock -n 9 || cscode_die "an install or uninstall of CsCode is running"; fi

    local versions=() files=() links=() dirs=() kind a b
    while IFS=$'\t' read -r kind a b; do
        case "$kind" in
            version) versions+=("$a") ;;
            file) files+=("$a"$'\t'"$b") ;;
            link) links+=("$a") ;;
            dir) dirs+=("$a") ;;
        esac
    done < "$cscode_manifest"

    local v
    for v in "${versions[@]+"${versions[@]}"}"; do
        case "$v" in ''|*/*|.|..) continue ;; esac
        if cscode_in_use "$cscode_root/$v"; then
            cscode_die "CsCode $v is running (or a debug adapter it started): quit it first (cscode --quit --all), then run this again"
        fi
    done

    local removed=0 kept=0
    for v in "${versions[@]+"${versions[@]}"}"; do
        case "$v" in ''|*/*|.|..) continue ;; esac
        if cscode_remove_version "$cscode_root/$v"; then removed=$((removed + 1)); else kept=$((kept + 1)); fi
    done
    local link
    for link in "${links[@]+"${links[@]}"}"; do
        # the `current` symlink: removed when it is still a symlink to a version name (never a directory someone put there)
        if [ -L "$link" ]; then rm -f -- "$link"; fi
    done
    local entry sha path
    for entry in "${files[@]+"${files[@]}"}"; do
        sha=${entry%%$'\t'*}
        path=${entry#*$'\t'}
        [ -e "$path" ] || [ -L "$path" ] || continue
        if cscode_still_ours "$path" "$sha"; then rm -f -- "$path"; removed=$((removed + 1))
        else cscode_warn "kept $path: it was replaced or changed after the install"; kept=$((kept + 1)); fi
    done
    rm -f -- "$cscode_manifest"
    [ "$refresh" = 0 ] || cscode_refresh_caches
    # Directories the installer created, deepest first, when empty. update-desktop-database's mimeinfo.cache does not keep an applications
    # directory alive that only exists because of the install.
    local d
    while IFS= read -r d; do
        [ -n "$d" ] && [ -d "$d" ] || continue
        if [ "$d" = "$cscode_apps" ] && [ "$(ls -A "$d")" = mimeinfo.cache ]; then rm -f -- "$d/mimeinfo.cache"; fi
        rmdir -- "$d" 2>/dev/null || true
    done < <(printf '%s\n' "${dirs[@]+"${dirs[@]}"}" | awk '{ print gsub("/", "/") "\t" $0 }' | sort -rn | cut -f2-)

    cscode_say "CsCode uninstalled: ${#versions[@]} version(s), the launcher, the desktop entry and the icons removed."
    [ "$kept" = 0 ] || cscode_say "Kept $kept item(s) that were changed or are not the installer's (see the warnings above)."
    cscode_say "Your settings and data stay: ${XDG_CONFIG_HOME:-$HOME/.config}/cscode, $cscode_root (sessions, tools), ${XDG_STATE_HOME:-$HOME/.local/state}/cscode."
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    CSCODE_SCRIPT=uninstall.sh
    cscode_uninstall_main "$@"
fi
