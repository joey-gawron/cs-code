#!/usr/bin/env bash
# Publishes the profiling host (Release, framework-dependent, linux-x64) for measurements: docs/MEMORY.md.
#
#   tools/profile/publish-host.sh            # -> .scratch/profile/host-jit
#   tools/profile/publish-host.sh --r2r      # -> .scratch/profile/host-r2r (ReadyToRun; restores the crossgen2 pack from nuget.org once)
#
# Measure with these builds, never with bin/Debug. Run from anywhere; with Nix-provided dotnet, the Nix runtime libraries are set up here.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck disable=SC1091
[[ -d /nix/store ]] && source "$root/tools/nix-dev-env.sh" >/dev/null
r2r=false
out="$root/.scratch/profile/host-jit"
for arg in "$@"; do
  case "$arg" in
    --r2r) r2r=true; out="$root/.scratch/profile/host-r2r" ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done
rm -rf "$out"
dotnet publish "$root/tools/profile/CsCode.ProfileHost/CsCode.ProfileHost.csproj" -c Release -r linux-x64 --self-contained false \
  -p:PublishReadyToRun="$r2r" -o "$out" -v:q -nologo
echo "$out"
