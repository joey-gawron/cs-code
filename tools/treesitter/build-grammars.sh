#!/usr/bin/env bash
# Builds CsCode's tree-sitter bundle from the pinned sources (tools/treesitter/manifest.json, downloaded and verified by fetch-grammars.sh):
# the runtime library, one shared library per grammar, nvim-treesitter's queries, the licences and a NOTICE, plus bundle.json (what was
# built, from which commit, each file's SHA-256 and grammar ABI) that the app checks before it loads anything (docs/TREE-SITTER.md).
#
#   tools/treesitter/build-grammars.sh [--target RID] [--out DIR] [--dir DOWNLOADS] [--jobs N]
#
#   --target   linux-x64 (default on x86-64 Linux) | linux-arm64 | win-x64 | osx-x64 | osx-arm64. Other targets need a cross compiler in CC
#              (e.g. CC=aarch64-linux-gnu-gcc, CC=x86_64-w64-mingw32-gcc, CC="clang -target arm64-apple-macos11 -isysroot <SDK>"); only
#              linux-x64 has been built and tested here.
#   --out      default <repo>/artifacts/tree-sitter/<RID> (git-ignored). The CsCode.Editor.TreeSitter project copies that folder next to
#              the app (bin and publish) as tree-sitter/.
#
# The output appears atomically (built in <out>.tmp, then renamed). Nothing downloaded is executed: the C sources are compiled with $CC.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
manifest="$here/manifest.json"
target=""
out=""
downloads=""
jobs=${JOBS:-6}

while [ $# -gt 0 ]; do
    case "$1" in
        --target) target=$2; shift ;;
        --out) out=$2; shift ;;
        --dir) downloads=$2; shift ;;
        --jobs) jobs=$2; shift ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -z "$target" ]; then
    case "$(uname -s)-$(uname -m)" in
        Linux-x86_64) target=linux-x64 ;;
        Linux-aarch64) target=linux-arm64 ;;
        Darwin-arm64) target=osx-arm64 ;;
        Darwin-x86_64) target=osx-x64 ;;
        *) echo "build-grammars: cannot guess the target, pass --target" >&2; exit 2 ;;
    esac
fi
case "$target" in
    linux-*) ext=so; runtime_file=libtree-sitter.so; shared=(-shared); runtime_link=(-shared -Wl,-soname,libtree-sitter.so) ;;
    osx-*) ext=dylib; runtime_file=libtree-sitter.dylib; shared=(-dynamiclib); runtime_link=(-dynamiclib -install_name @rpath/libtree-sitter.dylib) ;;
    win-*) ext=dll; runtime_file=tree-sitter.dll; shared=(-shared); runtime_link=(-shared) ;;
    *) echo "build-grammars: unknown target $target" >&2; exit 2 ;;
esac
CC=${CC:-cc}
STRIP=${STRIP:-}
if [ -z "$STRIP" ] && [ "$target" = linux-x64 ] && [ "$(uname -m)" = x86_64 ] && command -v strip >/dev/null; then STRIP=strip; fi
out=${out:-$repo/artifacts/tree-sitter/$target}
for tool in jq tar sha256sum; do command -v "$tool" >/dev/null || { echo "build-grammars: $tool is required" >&2; exit 1; }; done
command -v ${CC%% *} >/dev/null || { echo "build-grammars: no C compiler ($CC) for $target" >&2; exit 1; }

# 1. every source present and matching its pin (downloads what is missing)
fetch_args=()
[ -n "$downloads" ] && fetch_args=(--dir "$downloads")
"$here/fetch-grammars.sh" "${fetch_args[@]}" >/dev/null || { echo "build-grammars: fetch-grammars.sh failed (run it to see why)" >&2; exit 1; }
if [ -z "$downloads" ]; then
    common=$(git -C "$repo" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)
    if [ -n "$common" ] && [ -d "$(dirname "$common")/.scratch" ]; then downloads="$(dirname "$common")/.scratch/downloads"
    else downloads="$repo/.scratch/downloads"; fi
fi

tmp="$out.tmp"
rm -rf "$tmp"
mkdir -p "$tmp/lib" "$tmp/grammars/queries" "$tmp/licenses" "$tmp/.work"
work="$tmp/.work"

archive() { echo "$downloads/$1/$2/$1-$2.tar.gz"; }

# 2. unpack (each source into its own directory) and gate the licence
allowed=" MIT Apache-2.0 ISC BSD-2-Clause BSD-3-Clause 0BSD CC0-1.0 "
unpack() { # <jq path>
    local name rev lic licfile dir declared
    name=$(jq -r "$1.name" "$manifest"); rev=$(jq -r "$1.revision" "$manifest")
    lic=$(jq -r "$1.license" "$manifest"); licfile=$(jq -r "$1.licenseFile" "$manifest")
    dir="$work/$name"
    mkdir -p "$dir"
    tar -xzf "$(archive "$name" "$rev")" -C "$dir" --strip-components=1
    case "$allowed" in *" $lic "*) ;; *) echo "build-grammars: $name is licensed '$lic', which CsCode cannot bundle" >&2; exit 1 ;; esac
    [ -f "$dir/$licfile" ] || { echo "build-grammars: $name has no $licfile" >&2; exit 1; }
    if [ -f "$dir/tree-sitter.json" ]; then
        declared=$(jq -r '.metadata.license // empty' "$dir/tree-sitter.json")
        if [ -n "$declared" ] && [ "$declared" != "$lic" ]; then echo "build-grammars: $name declares '$declared', the manifest says '$lic'" >&2; exit 1; fi
    fi
    { echo "$name $(jq -r "$1.url" "$manifest") (commit $(jq -r "$1.commit" "$manifest")), $lic"; echo; cat "$dir/$licfile"
      if [ -f "$dir/NOTICE" ]; then echo; echo "---- NOTICE ----"; cat "$dir/NOTICE"; fi; } >"$tmp/licenses/$name.txt"
}
unpack .runtime
unpack .queries
count=$(jq '.sources | length' "$manifest")
for ((i = 0; i < count; i++)); do unpack ".sources[$i]"; done

# 3. the runtime (flags as tree-sitter's own Makefile; the API is exported through api.h's visibility pragma)
rt="$work/tree-sitter"
echo "build-grammars: runtime $(jq -r .runtime.revision "$manifest") for $target ($CC)"
# shellcheck disable=SC2086
$CC -O2 -std=c11 -fPIC -fvisibility=hidden -D_POSIX_C_SOURCE=200112L -D_DEFAULT_SOURCE -D_BSD_SOURCE -D_DARWIN_C_SOURCE \
    -I"$rt/lib/src" -I"$rt/lib/src/wasm" -I"$rt/lib/include" "$rt/lib/src/lib.c" "${runtime_link[@]}" -o "$tmp/lib/$runtime_file"
[ -n "$STRIP" ] && $STRIP --strip-unneeded "$tmp/lib/$runtime_file"

# 4. the grammars, in parallel (the big parse tables take tens of seconds each at -O2)
build_one() { # <grammar name> <source> <location>
    local name=$1 src="$work/$2" location=$3 dir files=() abi
    [ "$location" = . ] && location=""
    dir="$src${location:+/$location}/src"
    [ -f "$dir/parser.c" ] || { echo "build-grammars: $name: no $dir/parser.c" >&2; return 1; }
    files=("$dir/parser.c")
    [ -f "$dir/scanner.c" ] && files+=("$dir/scanner.c")
    if [ -f "$dir/scanner.cc" ]; then echo "build-grammars: $name has a C++ scanner, not supported" >&2; return 1; fi
    # shellcheck disable=SC2086
    $CC -O2 -std=c11 -fPIC -I"$dir" "${files[@]}" "${shared[@]}" -o "$tmp/grammars/$name.$ext" 2>"$work/$name.log" || { cat "$work/$name.log" >&2; return 1; }
    [ -n "$STRIP" ] && $STRIP --strip-unneeded "$tmp/grammars/$name.$ext"
    abi=$(grep -m1 -E '^#define LANGUAGE_VERSION' "$dir/parser.c" | awk '{print $3}')
    echo "$name $abi" >"$work/$name.abi"
    echo "  $name (ABI $abi, $(wc -c <"$tmp/grammars/$name.$ext" | tr -d ' ') bytes)"
}
export -f build_one
export work tmp ext CC STRIP
export shared_flags="${shared[*]}"
echo "build-grammars: $(jq '.grammars | length' "$manifest") grammars, $jobs at a time"
# (an empty location is passed as "." so that no line ends in a blank, which xargs -L would join with the next line)
jq -r '.grammars[] | "\(.name) \(.source) \(if .location == "" then "." else .location end)"' "$manifest" |
    xargs -P "$jobs" -L 1 bash -c 'shared=($shared_flags); build_one "$0" "$1" "$2"'

# 5. queries: nvim-treesitter's, for every grammar and the query-only names they inherit
q="$work/nvim-treesitter/runtime/queries"
for name in $(jq -r '.grammars[].name, .queryOnly[]' "$manifest"); do
    [ -d "$q/$name" ] || { echo "build-grammars: nvim-treesitter has no queries for $name" >&2; exit 1; }
    mkdir -p "$tmp/grammars/queries/$name"
    cp "$q/$name"/*.scm "$tmp/grammars/queries/$name/"
done
for name in $(jq -r '.grammars[].name' "$manifest"); do
    [ -f "$tmp/grammars/queries/$name/highlights.scm" ] || { echo "build-grammars: no highlights.scm for $name" >&2; exit 1; }
done

# 5b. the grammar's own tags.scm (definitions for the outline when no language server runs), as its tree-sitter.json lists them; a path into
#     node_modules/tree-sitter-X/ is that grammar's source here (TypeScript's tags = its own + JavaScript's). nvim-treesitter ships no tags.
jq -r '.grammars[] | "\(.name) \(.source)"' "$manifest" | while read -r name source; do
    src="$work/$source"
    [ -f "$src/tree-sitter.json" ] || continue
    paths=$(jq -r --arg n "$name" '.grammars[] | select((.name | gsub("-"; "_")) == $n) | .tags // empty | if type == "array" then .[] else . end' "$src/tree-sitter.json")
    [ -n "$paths" ] || continue
    out_tags="$tmp/grammars/queries/$name/tags.scm"
    : >"$out_tags"
    for p in $paths; do
        case "$p" in
            node_modules/tree-sitter-*/*) dep=${p#node_modules/}; file="$work/${dep%%/*}/${dep#*/}" ;;
            *) file="$src/$p" ;;
        esac
        [ -f "$file" ] || { echo "build-grammars: $name: tags file $p not found" >&2; exit 1; }
        { echo "; ---- $(basename "$(dirname "$(dirname "$file")")")/${file##*/} ($source)"; cat "$file"; echo; } >>"$out_tags"
    done
done

# 6. bundle.json (what the app checks: target, ABI range, each file's digest) and the NOTICE
grammars_json=$(for name in $(jq -r '.grammars[].name' "$manifest"); do
    read -r _ abi <"$work/$name.abi"
    jq -n --arg n "$name" --arg s "$(jq -r --arg n "$name" '.grammars[] | select(.name == $n) | .source' "$manifest")" \
        --arg f "grammars/$name.$ext" --arg h "$(sha256sum "$tmp/grammars/$name.$ext" | cut -d' ' -f1)" --argjson a "$abi" \
        --argjson z "$(wc -c <"$tmp/grammars/$name.$ext" | tr -d ' ')" '{name: $n, source: $s, file: $f, abi: $a, size: $z, sha256: $h}'
done | jq -s .)
sources_json=$(jq '[.runtime, .queries] + .sources | map({name, url, revision, commit, license})' "$manifest")
jq -n --arg t "$target" --arg rf "lib/$runtime_file" --arg rh "$(sha256sum "$tmp/lib/$runtime_file" | cut -d' ' -f1)" \
    --arg rv "$(jq -r .runtime.revision "$manifest")" --arg rc "$(jq -r .runtime.commit "$manifest")" --arg qc "$(jq -r .queries.commit "$manifest")" \
    --arg cc "$($CC --version 2>/dev/null | head -n1)" --argjson g "$grammars_json" --argjson s "$sources_json" \
    '{format: 1, target: $t, compiler: $cc, abi: {min: 13, max: 15},
      runtime: {version: $rv, commit: $rc, file: $rf, sha256: $rh}, queries: {source: "nvim-treesitter", commit: $qc},
      grammars: $g, sources: $s}' >"$tmp/bundle.json"
cp "$here/NOTICE" "$tmp/NOTICE"

rm -rf "$work"
rm -rf "$out"
mv "$tmp" "$out"
echo "build-grammars: $out ($(du -sh "$out" | cut -f1))"
