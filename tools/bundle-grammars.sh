#!/usr/bin/env bash
# One command to get tree-sitter highlighting for every language cs-zig knows.
#
#   tools/bundle-grammars.sh [-o DIR] [--source nix|fetch]
#
# Writes the grammar farm the editor loads at runtime (DIR/<lang>/parser,
# DIR/<lang>/queries/*.scm) as plain files, with nothing pointing into the
# Nix store, so the folder can be copied into an app bundle or another Mac.
#
#   nix    (default when `nix` exists) builds tools/grammars/flake.nix: nixpkgs'
#          tree-sitter grammars and nvim-treesitter's queries, about 170
#          languages, from the binary cache in seconds. The same set jiffy-edit
#          highlights with.
#   fetch  falls back to tools/fetch-grammars.sh, which builds the grammars it
#          has pinned from source with the system C compiler (about 20).
#
# DIR defaults to .grammars in the repository. `zig build` installs it to
# share/cs-zig/grammars next to the binary, where the editor finds it without
# CS_ZIG_GRAMMARS (which still overrides). Running this again replaces DIR.

set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
out=$root/.grammars
source_kind=

die() { echo "bundle-grammars: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case $1 in
        -o | --out) out=${2:?}; shift ;;
        --source) source_kind=${2:?}; shift ;;
        -h | --help) sed -n '2,/^$/s/^# \{0,1\}//p' "$0"; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
    shift
done

if [ -z "$source_kind" ]; then
    if command -v nix >/dev/null; then source_kind=nix; else source_kind=fetch; fi
fi

tmp=$out.tmp.$$
rm -rf "$tmp"

case $source_kind in
    nix)
        command -v nix >/dev/null || die "nix is not installed (use --source fetch)"
        store=$(nix build "path:$root/tools/grammars" --no-link --print-out-paths) ||
            die "nix build failed"
        mkdir -p "$tmp"
        # -L follows the farm's symlinks into the store; -X (BSD) / --no-preserve
        # keeps the store's read-only modes out of the copy.
        if cp --version >/dev/null 2>&1; then
            cp -rL --no-preserve=mode "$store"/. "$tmp"/
        else
            cp -RL "$store"/. "$tmp"/
            chmod -R u+w "$tmp"
        fi
        ;;
    fetch)
        "$root/tools/fetch-grammars.sh" -o "$tmp"
        ;;
    *) die "--source is nix or fetch" ;;
esac

# Where an upstream highlights.scm classifies a token differently from
# nvim-treesitter's (the queries cs-code's colours follow), adjust the copy:
#   rust   integer and float literals are @number, not @constant.builtin
#   c      primitive types (int, char) are @type.builtin, which VS colours as keywords
#   cpp    the same
#   html   quoted attribute values are strings with their quotes
sedi() { sed -i.bak "$@" && rm -f "${@: -1}.bak"; }
if [ -f "$tmp/rust/queries/highlights.scm" ]; then
    sedi -e 's/^(integer_literal) @constant\.builtin/(integer_literal) @number/' \
        -e 's/^(float_literal) @constant\.builtin/(float_literal) @number/' "$tmp/rust/queries/highlights.scm"
fi
for lang in c cpp; do
    [ -f "$tmp/$lang/queries/highlights.scm" ] &&
        sedi 's/^(primitive_type) @type$/(primitive_type) @type.builtin/' "$tmp/$lang/queries/highlights.scm"
done
if [ -f "$tmp/html/queries/highlights.scm" ] && ! grep -q quoted_attribute_value "$tmp/html/queries/highlights.scm"; then
    printf '\n(quoted_attribute_value) @string\n' >>"$tmp/html/queries/highlights.scm"
fi

# Every parser must be a library of this platform's architecture and must not
# name a Nix store path (it would not load once the store is gone).
bad=0
for d in "$tmp"/*/; do
    p=$d/parser
    [ -f "$p" ] || continue
    if command -v otool >/dev/null; then
        if otool -L "$p" 2>/dev/null | grep -q /nix/store; then
            echo "bundle-grammars: $(basename "$d") links into the Nix store" >&2
            bad=1
        fi
    fi
done
[ "$bad" -eq 0 ] || die "refusing to install parsers that depend on the Nix store"

rm -rf "$out"
mv "$tmp" "$out"
count=$(find "$out" -maxdepth 2 -name parser | wc -l | tr -d ' ')
size=$(du -sh "$out" | cut -f1)
echo "bundled $count grammars ($size) in $out"
echo "  zig build           installs them to share/cs-zig/grammars"
echo "  CS_ZIG_GRAMMARS=... overrides the lookup at run time"
