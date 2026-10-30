#!/usr/bin/env bash
# Downloads every pinned source of CsCode's tree-sitter bundle (tools/treesitter/manifest.json): the tree-sitter runtime, nvim-treesitter (the
# queries) and each grammar repository, as GitHub source archives of the pinned revision. Every archive is checked against the SHA-256 and size
# recorded in the manifest AND against the commit id git wrote into the archive's pax header, so a moved tag or a swapped archive is refused.
#
#   tools/treesitter/fetch-grammars.sh            download what is missing, verify everything (fails on any mismatch)
#   tools/treesitter/fetch-grammars.sh --pin      also records sha256/size/commit/licence for entries that have none yet (maintainers only,
#                                                 when a revision changes: clear the entry's sha256 first, then review the diff)
#   --dir DIR                                     download root (default: CSCODE_DOWNLOADS, else <main checkout>/.scratch/downloads)
#
# Layout: <root>/<name>/<revision>/<name>-<revision>.tar.gz, one new directory per download (shared by all worktrees of the repository).
# Nothing downloaded is executed: build-grammars.sh only compiles the C sources with the host's compiler. Needs curl, jq, gzip, tar, sha256sum
# (or shasum). Network access only to github.com / codeload.github.com.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
manifest="$here/manifest.json"
pin=0
root=${CSCODE_DOWNLOADS:-}

while [ $# -gt 0 ]; do
    case "$1" in
        --pin) pin=1 ;;
        --dir) root=$2; shift ;;
        -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

for tool in curl jq gzip tar; do
    command -v "$tool" >/dev/null || { echo "fetch-grammars: $tool is required" >&2; exit 1; }
done
if command -v sha256sum >/dev/null; then sha() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum >/dev/null; then sha() { shasum -a 256 "$1" | cut -d' ' -f1; }
else echo "fetch-grammars: sha256sum or shasum is required" >&2; exit 1; fi

if [ -z "$root" ]; then
    # worktrees share the main checkout's download cache (git-ignored .scratch/downloads)
    common=$(git -C "$repo" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)
    if [ -n "$common" ] && [ -d "$(dirname "$common")/.scratch" ]; then root="$(dirname "$common")/.scratch/downloads"
    else root="$repo/.scratch/downloads"; fi
fi
mkdir -p "$root"

# Commit id from the pax global header git archive writes ("52 comment=<sha>").
archive_commit() {
    { gzip -dc "$1" 2>/dev/null || true; } | head -c 1024 | tr -d '\0' | grep -ao 'comment=[0-9a-f]\{40\}' | head -n1 | cut -d= -f2 || true
}

# The archive's top-level licence file and a best guess of its SPDX id (the guess is reviewed by hand before it is committed).
license_of() {
    local file=$1 lic name text
    lic=$(tar -tzf "$file" | grep -iE '^[^/]+/(LICEN[CS]E|COPYING)(\.(md|txt|mit))?$' | head -n1 || true)
    [ -n "$lic" ] || { echo " "; return; }
    name=${lic#*/}
    text=$(tar -xzOf "$file" "$lic" | head -c 4000)
    case "$text" in
        *"Apache License"*"Version 2.0"*) echo "$name Apache-2.0" ;;
        *"Permission is hereby granted, free of charge"*) echo "$name MIT" ;;
        *"Permission to use, copy, modify, and/or distribute this software for any"*) echo "$name ISC" ;;
        *"Redistribution and use in source and binary forms"*"Neither the name"*) echo "$name BSD-3-Clause" ;;
        *"Redistribution and use in source and binary forms"*) echo "$name BSD-2-Clause" ;;
        *"GNU GENERAL PUBLIC LICENSE"*) echo "$name GPL" ;;
        *"Mozilla Public License"*) echo "$name MPL-2.0" ;;
        *) echo "$name unknown" ;;
    esac
}

failures=0
updated=$(mktemp)
trap 'rm -f "$updated"' EXIT
cp "$manifest" "$updated"

# fetch <jq path of the entry>
fetch() {
    local path=$1 name url rev want_sha want_size want_commit dir file got_sha got_size got_commit
    name=$(jq -r "$path.name" "$updated")
    url=$(jq -r "$path.url" "$updated")
    rev=$(jq -r "$path.revision" "$updated")
    want_sha=$(jq -r "$path.sha256" "$updated")
    want_size=$(jq -r "$path.size" "$updated")
    want_commit=$(jq -r "$path.commit" "$updated")
    case "$url" in https://github.com/*) ;; *) echo "  $name: only github.com sources are allowed ($url)" >&2; failures=$((failures + 1)); return ;; esac
    if [ -z "$want_sha" ] && [ "$pin" = 0 ]; then
        echo "  $name: not pinned (no sha256 in the manifest); run with --pin to record it" >&2
        failures=$((failures + 1))
        return
    fi
    dir="$root/$name/$rev"
    file="$dir/$name-$rev.tar.gz"
    if [ ! -f "$file" ]; then
        mkdir -p "$dir"
        rm -f "$file.part"
        if [ -n "$(ls -A "$dir")" ]; then
            echo "  $name: $dir is not empty and holds no $name-$rev.tar.gz; not touching it" >&2
            failures=$((failures + 1))
            return
        fi
        echo "  $name $rev: downloading $url/archive/$rev.tar.gz"
        if ! curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --max-time 600 -o "$file.part" "$url/archive/$rev.tar.gz"; then
            echo "  $name: download failed" >&2
            rm -f "$file.part"
            failures=$((failures + 1))
            return
        fi
        mv "$file.part" "$file"
    fi
    got_sha=$(sha "$file")
    got_size=$(wc -c <"$file" | tr -d ' ')
    got_commit=$(archive_commit "$file")
    if [ -z "$got_commit" ]; then
        echo "  $name: the archive carries no commit id (not a git archive?)" >&2
        failures=$((failures + 1))
        return
    fi
    case "$rev" in
        [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]*)
            if [ "${#rev}" = 40 ] && [ "$rev" != "$got_commit" ]; then
                echo "  $name: archive is commit $got_commit, the manifest pins $rev" >&2
                failures=$((failures + 1))
                return
            fi ;;
    esac
    if [ -z "$want_sha" ]; then
        local lic licfile spdx
        lic=$(license_of "$file")
        licfile=${lic% *}
        spdx=${lic##* }
        jq --arg s "$got_sha" --argjson z "$got_size" --arg c "$got_commit" --arg lf "$licfile" --arg l "$spdx" \
            "$path.sha256 = \$s | $path.size = \$z | $path.commit = \$c | (if $path.licenseFile == \"\" then $path.licenseFile = \$lf else . end) | (if $path.license == \"\" then $path.license = \$l else . end)" \
            "$updated" >"$updated.tmp" && mv "$updated.tmp" "$updated"
        echo "  $name $rev: pinned sha256 $got_sha, $got_size bytes, commit $got_commit, licence $spdx ($licfile)"
        return
    fi
    if [ "$got_sha" != "$want_sha" ] || [ "$got_size" != "$want_size" ]; then
        echo "  $name: $file has sha256 $got_sha ($got_size bytes), the manifest pins $want_sha ($want_size bytes)" >&2
        failures=$((failures + 1))
        return
    fi
    if [ -n "$want_commit" ] && [ "$got_commit" != "$want_commit" ]; then
        echo "  $name: archive is commit $got_commit, the manifest pins $want_commit" >&2
        failures=$((failures + 1))
        return
    fi
    echo "  $name $rev: ok ($got_size bytes)"
}

echo "fetch-grammars: downloads in $root"
fetch .runtime
fetch .queries
count=$(jq '.sources | length' "$updated")
for ((i = 0; i < count; i++)); do fetch ".sources[$i]"; done

if [ "$pin" = 1 ] && ! cmp -s "$manifest" "$updated"; then
    cp "$updated" "$manifest"
    echo "fetch-grammars: manifest updated with the new pins: review the licences and the diff before committing"
fi
if [ "$failures" -gt 0 ]; then
    echo "fetch-grammars: $failures problem(s)" >&2
    exit 1
fi
echo "fetch-grammars: all $((count + 2)) sources verified"
