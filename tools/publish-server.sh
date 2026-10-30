#!/usr/bin/env bash
# Builds the cscode-server bundles that the client uploads to remote hosts (docs/REMOTE.md, "Server bootstrap"):
#
#   cscode-server-<rid>.tar.gz       self-contained single-file build per runtime id (default: linux-x64)
#   cscode-server-portable.tar.gz    framework-dependent build for any host with a .NET 10 runtime (launched through `dotnet`)
#   manifest.json                    flavour, rid, file, sha256 and install hash (first 16 hex digits of the sha256) of each bundle
#
# Usage: tools/publish-server.sh [--out DIR] [--rid RID]... [--no-portable]
#   --out DIR      output directory (default: .scratch/server-bundles; the client also looks there when it runs from a source tree)
#   --rid RID      add a self-contained runtime id (repeatable). Only RIDs whose runtime pack the SDK already has build offline;
#                  others need a NuGet download (this script never downloads anything itself).
#   --no-portable  skip the framework-dependent bundle
#
# Not trimmed: the server (de)serialises Core's records with reflection-based System.Text.Json and composes its services through
# Microsoft.Extensions.DependencyInjection, neither of which is trim-safe without source-generated contexts for every model.
# On a Nix-provided dotnet, run it as `bash .scratch/notes/with-nix-env.sh tools/publish-server.sh` (or source tools/nix-dev-env.sh first).
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
out="$repo/.scratch/server-bundles"
rids=()
portable=1
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$2; shift 2 ;;
    --rid) rids+=("$2"); shift 2 ;;
    --no-portable) portable=0; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "publish-server.sh: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[ ${#rids[@]} -gt 0 ] || rids=(linux-x64)

project="$repo/src/CsCode.Server/CsCode.Server.csproj"
work=$(mktemp -d "${TMPDIR:-/tmp}/cscode-publish.XXXXXX")
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"
version=$(git -C "$repo" describe --always --dirty 2>/dev/null || echo dev)
manifest_entries=()

# bundle <flavour> <rid> <dir> <file> <runtime>
bundle() {
  local flavour=$1 rid=$2 dir=$3 file=$4 runtime=$5
  [ -f "$dir/cscode-server" ] || { echo "publish-server.sh: $dir has no cscode-server" >&2; exit 1; }
  chmod 755 "$dir/cscode-server"
  tar -C "$dir" -czf "$out/$file.tmp" --owner=0 --group=0 --numeric-owner --sort=name .
  mv "$out/$file.tmp" "$out/$file"
  local sum
  sum=$(sha256sum "$out/$file" | cut -d' ' -f1)
  local runtime_json=""
  [ -n "$runtime" ] && runtime_json=", \"runtime\": \"$runtime\""
  manifest_entries+=("    { \"flavour\": \"$flavour\", \"rid\": \"$rid\", \"file\": \"$file\", \"sha256\": \"$sum\", \"hash\": \"${sum:0:16}\"$runtime_json }")
  echo "  $file  $(du -h "$out/$file" | cut -f1)  hash ${sum:0:16}"
}

for rid in "${rids[@]}"; do
  echo "Publishing self-contained $rid…"
  dotnet publish "$project" --nologo -v:q -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:PublishTrimmed=false \
    -o "$work/$rid"
  find "$work/$rid" -name '*.pdb' -delete
  bundle self-contained "$rid" "$work/$rid" "cscode-server-$rid.tar.gz" ""
done

if [ "$portable" = 1 ]; then
  echo "Publishing framework-dependent (portable)…"
  dotnet publish "$project" --nologo -v:q -c Release --no-self-contained -p:UseAppHost=false -p:DebugType=none -o "$work/portable"
  find "$work/portable" -name '*.pdb' -delete
  cat > "$work/portable/cscode-server" <<'LAUNCHER'
#!/bin/sh
# cscode-server (portable): runs on the host's .NET 10 runtime.
exec dotnet "$(dirname "$0")/cscode-server.dll" "$@"
LAUNCHER
  bundle framework-dependent any "$work/portable" "cscode-server-portable.tar.gz" "10.0"
fi

{
  echo "{"
  echo "  \"version\": \"$version\","
  echo "  \"bundles\": ["
  for i in "${!manifest_entries[@]}"; do
    sep=","; [ "$i" -eq $((${#manifest_entries[@]} - 1)) ] && sep=""
    echo "${manifest_entries[$i]}$sep"
  done
  echo "  ]"
  echo "}"
} > "$out/manifest.json.tmp"
mv "$out/manifest.json.tmp" "$out/manifest.json"
echo "Wrote $out/manifest.json"
