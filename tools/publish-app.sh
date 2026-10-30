#!/usr/bin/env bash
# Publishes CsCode for installing (docs/SESSIONS.md, "Publishing"): self-contained, ReadyToRun composite, official runtime packs
# (build/Publish.props, build/NuGetRuntimePacks.props). Installs nothing; tools/install.sh calls this and copies the output.
#
#   tools/publish-app.sh [--rid RID] [--out DIR] [--artifacts DIR]
#
#   --rid RID        runtime identifier (default: linux-x64). Its runtime packs come from nuget.org through restore (tools/fetch-publish-packs.sh
#                    pins the linux-x64 ones).
#   --out DIR        output directory (default: .scratch/publish/<rid>). Replaced when it holds an earlier publish; refused when it holds
#                    anything else.
#   --artifacts DIR  intermediate build output (default: .scratch/publish-artifacts/<rid>), apart from the development bin/ and obj/
#
# The output runs on the system's loader and libraries, without a dotnet and without the Nix shell (tests/CsCode.App.Tests,
# PublishedAppTests, starts it with env -i). On a Nix-provided dotnet, run the script itself in the development environment, for the SDK only:
#   bash .scratch/notes/with-nix-env.sh tools/publish-app.sh
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
rid=linux-x64
out=""
artifacts=""
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) rid=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    --artifacts) artifacts=$2; shift 2 ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *) echo "publish-app.sh: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] || out="$repo/.scratch/publish/$rid"
[ -n "$artifacts" ] || artifacts="$repo/.scratch/publish-artifacts/$rid"
mkdir -p "$(dirname "$out")"
out=$(cd "$(dirname "$out")" && pwd)/$(basename "$out")

case "$rid" in win-*) app=cscode.exe ;; *) app=cscode ;; esac
if [ -e "$out" ]; then
  if [ -n "$(ls -A "$out")" ] && [ ! -f "$out/cscode.dll" ]; then
    echo "publish-app.sh: $out is not empty and holds no earlier publish; refusing to replace it" >&2
    exit 1
  fi
  rm -rf "$out"
fi

echo "Publishing CsCode ($rid, self-contained, ReadyToRun composite) to $out"
# The self-contained switch must be global (the App references the cscode-server executable: NETSDK1150); the rest is in build/Publish.props.
# No build servers: an installer run leaves no MSBuild/compiler daemons behind.
dotnet publish "$repo/src/CsCode.App/CsCode.App.csproj" --nologo -v:q -c Release -r "$rid" --self-contained true \
  --disable-build-servers --artifacts-path "$artifacts" -o "$out"
[ -x "$out/$app" ] || { echo "publish-app.sh: $out/$app is missing" >&2; exit 1; }

# A file that points into /nix/store would run only on this machine (and on Nix's glibc): never ship one.
if command -v readelf >/dev/null 2>&1; then
  bad=0
  while IFS= read -r -d '' f; do
    [ "$(head -c 4 "$f" | od -An -c | tr -d ' ')" = '177ELF' ] || continue
    if readelf -l -d "$f" 2>/dev/null | grep -q '/nix/store/'; then echo "publish-app.sh: $f refers to /nix/store" >&2; bad=1; fi
  done < <(find "$out" -type f -print0)
  [ "$bad" = 0 ] || exit 1
fi
echo "Done: $(du -sh "$out" | cut -f1) in $out ($("$out/$app" --version 2>/dev/null || echo '?'))"
