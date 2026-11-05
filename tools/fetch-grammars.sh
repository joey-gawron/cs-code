#!/usr/bin/env bash
# Builds the tree-sitter grammars cs-zig loads at runtime, for installs
# without Nix (the flake's package already ships them).
#
#   tools/fetch-grammars.sh [-o DIR] [--src DIR] [--abi N] [LANG...]
#   tools/fetch-grammars.sh --list
#
# For each grammar it clones the repository at the revision pinned below,
# compiles parser.c (and scanner.c) into a shared library with $CC and copies
# the highlight queries, giving the layout the editor reads:
#
#   DIR/<lang>/parser                  exports tree_sitter_<lang>
#   DIR/<lang>/queries/highlights.scm
#   DIR/<lang>/queries/injections.scm  (if any: code embedded in other code)
#
# Then run the editor with CS_ZIG_GRAMMARS=DIR, or build it with
# `zig build -Dgrammars=DIR` to make DIR the default. Running the script again
# rebuilds only what changed.
#
# Without LANG arguments it builds every grammar src/model/syntax/languages.zig
# uses (`.grammar` and `.query_inherits`), so new languages are picked up from
# there. Each one needs a line in GRAMMARS below: name, repository, tag or
# commit, and the grammar's subdirectory when the repository holds several.
# The revisions match nixpkgs' tree-sitter-grammars, so Nix and non-Nix
# installs highlight the same way.
#
# Options:
#   -o, --out DIR   Output directory (default: ~/.local/share/cs-zig/grammars)
#   --src DIR       Where checkouts are cached (default: ~/.cache/cs-zig/grammar-src)
#   --abi N         Newest tree-sitter ABI the editor's libtree-sitter can load.
#                   Grammars generated for a newer one are regenerated with the
#                   `tree-sitter` CLI. Default: read from pkg-config (14 before
#                   tree-sitter 0.25, 15 from 0.25 on).
#   --all           Build every grammar listed below, not just the used ones
#   --list          Print the grammars languages.zig uses and whether each is pinned
#
# Needs: git, a C compiler ($CC, default cc), and for --abi regeneration the
# tree-sitter CLI. Environment: CC, CXX, CFLAGS (default -O2), JOBS.

set -euo pipefail

# name        repository                                                   revision                                  [subdir]
GRAMMARS='
bash          https://github.com/tree-sitter/tree-sitter-bash               v0.25.1
c             https://github.com/tree-sitter/tree-sitter-c                  v0.24.2
cpp           https://github.com/tree-sitter/tree-sitter-cpp                v0.23.4
css           https://github.com/tree-sitter/tree-sitter-css                v0.25.0
go            https://github.com/tree-sitter/tree-sitter-go                 v0.25.0
html          https://github.com/tree-sitter/tree-sitter-html               v0.23.2
javascript    https://github.com/tree-sitter/tree-sitter-javascript         v0.25.0
json          https://github.com/tree-sitter/tree-sitter-json               v0.24.8
lua           https://github.com/tree-sitter-grammars/tree-sitter-lua       10fe0054734eec83049514ea2e718b2a56acd0c9
make          https://github.com/tree-sitter-grammars/tree-sitter-make      70613f3d812cbabbd7f38d104d60a409c4008b43
markdown      https://github.com/tree-sitter-grammars/tree-sitter-markdown  v0.5.3                                    tree-sitter-markdown
markdown_inline https://github.com/tree-sitter-grammars/tree-sitter-markdown v0.5.3                                 tree-sitter-markdown-inline
nix           https://github.com/nix-community/tree-sitter-nix              eabf96807ea4ab6d6c7f09b671a88cd483542840
org           https://github.com/nvim-orgmode/tree-sitter-org               v2.0.4
python        https://github.com/tree-sitter/tree-sitter-python             v0.25.0
rust          https://github.com/tree-sitter/tree-sitter-rust               v0.24.2
toml          https://github.com/tree-sitter-grammars/tree-sitter-toml      64b56832c2cffe41758f28e05c756a3a98d16f41
tsx           https://github.com/tree-sitter/tree-sitter-typescript         v0.23.2                                   tsx
typescript    https://github.com/tree-sitter/tree-sitter-typescript         v0.23.2                                   typescript
yaml          https://github.com/tree-sitter-grammars/tree-sitter-yaml      v0.7.2
zig           https://github.com/tree-sitter-grammars/tree-sitter-zig       6479aa13f32f701c383083d8b28360ebd682fb7d
'

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
languages_zig=$root/src/model/syntax/languages.zig

out=${XDG_DATA_HOME:-$HOME/.local/share}/cs-zig/grammars
src=${XDG_CACHE_HOME:-$HOME/.cache}/cs-zig/grammar-src
abi=
all=false
list=false
want=()

die() { echo "fetch-grammars: $*" >&2; exit 1; }
usage() { sed -n '2,/^$/s/^# \{0,1\}//p' "$0"; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
    case $1 in
        -o | --out) out=${2:?}; shift ;;
        --src) src=${2:?}; shift ;;
        --abi) abi=${2:?}; shift ;;
        --all) all=true ;;
        --list) list=true ;;
        -h | --help) usage ;;
        -*) echo "unknown option: $1" >&2; usage 2 ;;
        *) want+=("$1") ;;
    esac
    shift
done

# The pinned line for a grammar, or nothing.
pinned() { awk -v n="$1" '$1 == n { print; exit }' <<<"$GRAMMARS"; }

# Grammars languages.zig refers to: `.grammar = "x"` and `.query_inherits = &.{ "a", "b" }`.
used_grammars() {
    [ -f "$languages_zig" ] || die "can't find $languages_zig (pass the grammars to build instead)"
    {
        grep -oE '\.grammar = "[A-Za-z0-9_]+"' "$languages_zig" | cut -d'"' -f2
        grep -oE '\.query_inherits = &\.\{[^}]*\}' "$languages_zig" | grep -oE '"[A-Za-z0-9_]+"' | tr -d '"'
    } | sort -u
}

if $list; then
    for g in $(used_grammars); do
        if [ -n "$(pinned "$g")" ]; then echo "$g"; else echo "$g    (not pinned: add it to GRAMMARS in $0)"; fi
    done
    exit 0
fi

if [ ${#want[@]} -eq 0 ]; then
    if $all; then
        mapfile -t want < <(awk 'NF { print $1 }' <<<"$GRAMMARS")
    else
        mapfile -t want < <(used_grammars)
    fi
fi

missing=()
for g in "${want[@]}"; do [ -n "$(pinned "$g")" ] || missing+=("$g"); done
if [ ${#missing[@]} -gt 0 ]; then
    echo "fetch-grammars: no pinned revision for: ${missing[*]}" >&2
    echo "  add a line for each to GRAMMARS in $0 (or skip them by naming the grammars to build)" >&2
    exit 1
fi

command -v git >/dev/null || die "git is required"
CC=${CC:-cc}
CXX=${CXX:-c++}
CFLAGS=${CFLAGS:--O2}
command -v "$CC" >/dev/null || die "no C compiler ($CC); install gcc or clang, or set CC"

if [ -z "$abi" ]; then
    abi=15
    if ts_version=$(pkg-config --modversion tree-sitter 2>/dev/null); then
        IFS=. read -r major minor _ <<<"$ts_version"
        if [ "$major" -eq 0 ] && [ "$minor" -lt 25 ]; then abi=14; fi
    fi
fi

mkdir -p "$out" "$src"

# Checkout of repo at rev (shared by grammars from the same repository).
checkout_dir() { echo "$src/$(basename "$1")@$2"; }

fetch() {
    local repo=$1 rev=$2 dir
    dir=$(checkout_dir "$repo" "$rev")
    [ -d "$dir" ] && return
    echo "fetch  $(basename "$repo") $rev"
    local tmp=$dir.tmp.$$
    rm -rf "$tmp"
    git init -q "$tmp"
    git -C "$tmp" fetch -q --depth 1 "$repo" "$rev"
    git -C "$tmp" -c advice.detachedHead=false checkout -q FETCH_HEAD
    mv "$tmp" "$dir"
}

build() {
    local name=$1 repo=$2 rev=$3 subdir=${4:-.}
    local base grammar_src dest stamp work=
    base=$(checkout_dir "$repo" "$rev")
    grammar_src=$base/$subdir/src
    dest=$out/$name
    stamp="$repo $rev $subdir abi<=$abi $CC $CFLAGS"
    [ -f "$grammar_src/parser.c" ] || die "$name: no parser.c in $grammar_src"

    if [ -f "$dest/parser" ] && [ "$(cat "$dest/.source" 2>/dev/null)" = "$stamp" ]; then
        echo "ok     $name"
        return
    fi

    # Grammars generated for a newer ABI than libtree-sitter supports fail to
    # load ("IncompatibleGrammar"); regenerate parser.c for the older ABI.
    local version
    version=$(sed -n 's/^#define LANGUAGE_VERSION \([0-9]*\).*/\1/p' "$grammar_src/parser.c")
    if [ -n "$version" ] && [ "$version" -gt "$abi" ]; then
        command -v tree-sitter >/dev/null ||
            die "$name: parser.c is ABI $version but libtree-sitter loads at most ABI $abi; install the tree-sitter CLI (cargo install tree-sitter-cli) to regenerate it, or build against tree-sitter >= 0.25"
        echo "gen    $name (ABI $version -> $abi)"
        work=$src/work-$name.$$
        rm -rf "$work"
        cp -r "$base/$subdir" "$work"
        local log
        if ! log=$(cd "$work" && tree-sitter generate --abi "$abi" src/grammar.json 2>&1); then
            echo "$log" >&2
            die "$name: tree-sitter generate failed"
        fi
        grammar_src=$work/src
    fi

    echo "build  $name"
    local tmp=$dest.tmp.$$
    rm -rf "$tmp"
    mkdir -p "$tmp/queries"
    local objs=()
    "$CC" $CFLAGS -std=gnu11 -fPIC -I "$grammar_src" -c "$grammar_src/parser.c" -o "$tmp/parser.o"
    objs+=("$tmp/parser.o")
    local link=$CC
    if [ -f "$grammar_src/scanner.c" ]; then
        "$CC" $CFLAGS -std=gnu11 -fPIC -I "$grammar_src" -c "$grammar_src/scanner.c" -o "$tmp/scanner.o"
        objs+=("$tmp/scanner.o")
    elif [ -f "$grammar_src/scanner.cc" ]; then
        "$CXX" $CFLAGS -fPIC -I "$grammar_src" -c "$grammar_src/scanner.cc" -o "$tmp/scanner.o"
        objs+=("$tmp/scanner.o")
        link=$CXX
    fi
    "$link" -shared "${objs[@]}" -o "$tmp/parser"
    rm -f "${objs[@]}"

    # Queries live next to the grammar, or at the repository root when it holds
    # several grammars (tree-sitter-typescript).
    local queries=$base/$subdir/queries
    [ -d "$queries" ] || queries=$base/queries
    if [ -d "$queries" ]; then cp "$queries"/*.scm "$tmp/queries/" 2>/dev/null || true; fi
    [ -f "$tmp/queries/highlights.scm" ] || echo "note   $name has no highlights.scm (parses, but nothing to color)"

    echo "$stamp" >"$tmp/.source"
    rm -rf "$dest"
    mv "$tmp" "$dest"
    [ -z "$work" ] || rm -rf "$work"
}

# Fetch one at a time (grammars can share a repository), then compile in parallel.
for g in "${want[@]}"; do
    read -r _ repo rev _ <<<"$(pinned "$g")"
    fetch "$repo" "$rev"
done

jobs_max=${JOBS:-$(nproc 2>/dev/null || echo 4)}
failed=0
running=0
for g in "${want[@]}"; do
    read -r name repo rev subdir <<<"$(pinned "$g")"
    build "$name" "$repo" "$rev" "$subdir" &
    running=$((running + 1))
    if [ "$running" -ge "$jobs_max" ]; then
        wait -n || failed=1
        running=$((running - 1))
    fi
done
while [ "$running" -gt 0 ]; do
    wait -n || failed=1
    running=$((running - 1))
done
[ "$failed" -eq 0 ] || die "some grammars failed to build (see above)"

echo
echo "Grammars are in $out. Point the editor at them with"
echo "  export CS_ZIG_GRAMMARS=$out"
echo "or build it with: zig build -Doptimize=ReleaseSafe -Dgrammars=$out"
