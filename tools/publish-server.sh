#!/usr/bin/env bash
# Builds the cscode-server bundles that the client uploads to remote hosts (docs/REMOTE.md, "Server bootstrap"):
#
#   cscode-server-<rid>.tar.gz       self-contained single-file build per runtime id (default: the four POSIX platforms the client bootstraps)
#   cscode-server-portable.tar.gz    framework-dependent build for any host with a .NET 10 runtime (launched through `dotnet`)
#   manifest.json                    flavour, rid, file, sha256 and install hash (first 16 hex digits of the sha256) of each bundle
#
# Usage: tools/publish-server.sh [--out DIR] [--rid RID]... [--all] [--no-portable] [--no-trim]
#   --out DIR      output directory (default: .scratch/server-bundles; the client also looks there when it runs from a source tree)
#   --rid RID      build this self-contained runtime id instead of the default set (repeatable). Default: linux-x64 linux-arm64 osx-arm64
#                  osx-x64, every platform the client can bootstrap, so an arm64 VM, a Raspberry Pi or a Mac remote finds its bundle
#                  (with linux-x64 alone they were 'unsupported' unless the host had a .NET 10 runtime on its PATH). A RID whose runtime pack is not in the NuGet cache is
#                  restored from nuget.org by `dotnet publish` (tools/fetch-server-runtime-packs.sh pins and verifies those packs).
#   --all          the five supported RIDs: linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64
#   --no-portable  skip the framework-dependent bundle
#   --no-trim      do not trim the self-contained bundles. By default they are trimmed in partial mode (framework assemblies only; ours and
#                  the NuGet libraries stay whole): linux-x64 33 MB -> 12 MB, and the whole remote test suite passes against the trimmed
#                  server (docs/REMOTE.md, "Server bundles").
#
# win-x64 holds cscode-server.exe; the client does not bootstrap Windows remotes yet (POSIX start/install scripts), so it is built for the
# record and for a future PowerShell installer. macOS apphosts are signed ad hoc by the SDK on this Linux host (no Apple tooling needed).
# Runs on Linux and on macOS (its bash 3.2, bsdtar and shasum): the archives list the same sorted entries, owned by 0:0, either way.
# On a Nix-provided dotnet, run it as `bash .scratch/notes/with-nix-env.sh tools/publish-server.sh` (or source tools/nix-dev-env.sh first).
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
out="$repo/.scratch/server-bundles"
rids=()
portable=1
trim=1
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$2; shift 2 ;;
    --rid) rids+=("$2"); shift 2 ;;
    --all) rids+=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64); shift ;;
    --no-portable) portable=0; shift ;;
    --no-trim) trim=0; shift ;;
    --trim) trim=1; shift ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "publish-server.sh: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[ ${#rids[@]} -gt 0 ] || rids=(linux-x64 linux-arm64 osx-arm64 osx-x64)

project="$repo/src/CsCode.Server/CsCode.Server.csproj"
work=$(mktemp -d "${TMPDIR:-/tmp}/cscode-publish.XXXXXX")
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"
version=$(git -C "$repo" describe --always --dirty 2>/dev/null || echo dev)
manifest_entries=()

# sha256 <file>: lower-case hex. sha256sum on Linux, shasum on macOS (which has no sha256sum), as the remote install script does.
sha256() { (sha256sum "$1" 2>/dev/null || shasum -a 256 "$1") | cut -d' ' -f1; }

# tarball <dir> <archive>: the same archive on GNU tar and on macOS's bsdtar: entries sorted by name, owned by 0:0, no extended attributes.
tarball() {
  if tar --version 2>/dev/null | grep -q 'GNU tar'; then
    tar -C "$1" -czf "$2" --owner=0 --group=0 --numeric-owner --sort=name .
  else
    # bsdtar has no --sort: the names are listed sorted (the directories too, as GNU tar's '.' adds them) and not recursed into. No
    # AppleDouble ._ files or quarantine/provenance attributes (COPYFILE_DISABLE, --no-mac-metadata), which a Linux remote would unpack.
    (cd "$1" && find . | LC_ALL=C sort) > "$work/tar-list"
    COPYFILE_DISABLE=1 tar -C "$1" -czf "$2" --uid 0 --gid 0 --numeric-owner --no-mac-metadata --no-xattrs -n -T "$work/tar-list"
  fi
}

# bundle <flavour> <rid> <dir> <file> <runtime>
bundle() {
  local flavour=$1 rid=$2 dir=$3 file=$4 runtime=$5 exe=cscode-server
  case "$rid" in win-*) exe=cscode-server.exe ;; esac
  [ -f "$dir/$exe" ] || { echo "publish-server.sh: $dir has no $exe" >&2; exit 1; }
  chmod 755 "$dir/$exe"
  tarball "$dir" "$out/$file.tmp"
  mv "$out/$file.tmp" "$out/$file"
  local sum
  sum=$(sha256 "$out/$file")
  local runtime_json=""
  [ -n "$runtime" ] && runtime_json=", \"runtime\": \"$runtime\""
  manifest_entries+=("    { \"flavour\": \"$flavour\", \"rid\": \"$rid\", \"file\": \"$file\", \"sha256\": \"$sum\", \"hash\": \"${sum:0:16}\"$runtime_json }")
  echo "  $file  $(du -h "$out/$file" | cut -f1)  hash ${sum:0:16}"
}

trim_args=(-p:PublishTrimmed=false)
if [ "$trim" = 1 ]; then
  # Partial: only assemblies marked trimmable (the framework's) are trimmed; System.Text.Json keeps its reflection serializer (Core's records),
  # and the trimmer's warnings (reflection the analysis cannot follow) are reported, not fatal: the tests run against the result instead.
  trim_args=(-p:PublishTrimmed=true -p:TrimMode=partial -p:JsonSerializerIsReflectionEnabledByDefault=true -p:TreatWarningsAsErrors=false -p:SuppressTrimAnalysisWarnings=true)
fi
# The runtime, apphost and targeting packs come from NuGet (Microsoft's builds, verified by tools/fetch-server-runtime-packs.sh), never from the
# SDK's own packs folder: a distribution's SDK may patch those for itself (Nix sets the apphost's ELF interpreter to its /nix/store glibc, which
# no remote has). Pointing the pack root at an empty directory makes the SDK resolve every pack from the NuGet cache.
mkdir -p "$work/no-local-packs"
for rid in "${rids[@]}"; do
  echo "Publishing self-contained ${rid}…"
  dotnet publish "$project" --nologo -v:q -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none "${trim_args[@]}" \
    -p:NetCoreTargetingPackRoot="$work/no-local-packs" -p:AllowMissingPrunePackageData=true \
    -o "$work/$rid"
  find "$work/$rid" -name '*.pdb' -delete
  case "$rid" in
    linux-*)
      # The loader a Linux remote must have: the standard one, not a build machine's.
      if LC_ALL=C head -c 8192 "$work/$rid/cscode-server" | grep -a -q '/nix/store/'; then
        echo "publish-server.sh: $rid's cscode-server uses a /nix/store loader; it would not start on a remote" >&2; exit 1
      fi ;;
  esac
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
