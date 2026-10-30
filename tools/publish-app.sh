#!/usr/bin/env bash
# Publishes CsCode for installing (docs/SESSIONS.md, "Publishing"): self-contained, ReadyToRun composite, official runtime packs
# (build/Publish.props, build/NuGetRuntimePacks.props). Installs nothing; tools/install.sh calls this and copies the output.
#
#   tools/publish-app.sh [--rid RID] [--out DIR] [--artifacts DIR] [--unpinned]
#
#   --rid RID        runtime identifier (default: linux-x64). Its runtime packs come from nuget.org through restore.
#   --out DIR        output directory (default: .scratch/publish/<rid>). Replaced when it holds an earlier publish; refused when it holds
#                    anything else.
#   --artifacts DIR  intermediate build output (default: .scratch/publish-artifacts/<rid>), apart from the development bin/ and obj/
#   --unpinned       publish a runtime identifier whose packs are not pinned (only linux-x64's are), with the packs restore picks, unchecked
#
# The packs are pinned: before publishing, tools/fetch-publish-packs.sh restores them and checks each one's size, SHA-512 and SHA-256 against
# its pin (no publish when one differs), the publish is told to use exactly that version (build/Publish.props, CsCodePinnedRuntimeVersion), and
# the output must name it.
#
# The output runs on the system's loader and libraries, without a dotnet and without the Nix shell (tests/CsCode.App.Tests,
# PublishedAppTests, starts it with env -i). On a Nix-provided dotnet, run the script itself in the development environment, for the SDK only:
#   bash .scratch/notes/with-nix-env.sh tools/publish-app.sh
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
rid=linux-x64
out=""
artifacts=""
unpinned=0
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) rid=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    --artifacts) artifacts=$2; shift 2 ;;
    --unpinned) unpinned=1; shift ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "publish-app.sh: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

# The pinned packs: restored and checked before anything is built (tools/fetch-publish-packs.sh pins linux-x64's).
pin_args=()
pinned=""
if [ "$rid" = linux-x64 ]; then
  pinned=$(bash "$repo/tools/fetch-publish-packs.sh" --print-version)
  if ! bash "$repo/tools/fetch-publish-packs.sh"; then
    echo "publish-app.sh: the publish packs do not match their pins (tools/fetch-publish-packs.sh above); not publishing" >&2
    exit 1
  fi
  pin_args=("-p:CsCodePinnedRuntimeVersion=$pinned")
elif [ "$unpinned" != 1 ]; then
  echo "publish-app.sh: only linux-x64's packs are pinned; pass --unpinned to publish $rid with the packs restore picks (unchecked)" >&2
  exit 2
else
  pin_args=("-p:CsCodePinnedRuntimeVersion=none")
fi
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
  --disable-build-servers --artifacts-path "$artifacts" -o "$out" "${pin_args[@]}"
[ -x "$out/$app" ] || { echo "publish-app.sh: $out/$app is missing" >&2; exit 1; }
# The output must carry the pinned runtime (a publish that took another version of the packs is not shipped).
if [ -n "$pinned" ] && ! grep -q "\"runtimepack.Microsoft.NETCore.App.Runtime.$rid/$pinned\"" "$out/cscode.deps.json"; then
  echo "publish-app.sh: $out/cscode.deps.json does not name the pinned runtime pack $pinned; refusing this publish" >&2
  exit 1
fi

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
