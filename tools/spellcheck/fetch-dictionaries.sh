#!/bin/sh
# Downloads extra Hunspell dictionaries from LibreOffice's dictionaries repository into .scratch/dictionaries/ (git-ignored), for the
# spell checker's measurements (docs/SPELLCHECK.md §8: the false-positive count with en_GB beside the bundled en_US) and for trying a
# language by hand: copy a .dic/.aff pair into {UserDataDir}/dictionaries and name it in cSpell.language. Nothing downloaded is executed.
#
#   tools/spellcheck/fetch-dictionaries.sh            # en_GB (default)
#   tools/spellcheck/fetch-dictionaries.sh de_DE_frami:de de_AT_frami:de    # name:folder pairs of LibreOffice's repository
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
out="$root/.scratch/dictionaries"
mkdir -p "$out"
base="https://raw.githubusercontent.com/LibreOffice/dictionaries/master"
[ $# -eq 0 ] && set -- en_GB:en
for spec in "$@"; do
  name=${spec%%:*}
  folder=${spec#*:}
  for ext in dic aff; do
    curl -fsSL --max-time 120 -o "$out/$name.$ext.tmp" "$base/$folder/$name.$ext"
    mv "$out/$name.$ext.tmp" "$out/$name.$ext"
  done
  echo "$out/$name.dic + .aff"
done
