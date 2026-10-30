#!/usr/bin/env bash
# Shared setup of the profiling scripts (source it): repo root, Nix runtime libraries, no display, no ssh agent, and the published host.
# The profiling host never opens a window (Avalonia's headless platform) and keeps its CSCODE_HOME in a temp directory.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck disable=SC1091
[[ -d /nix/store ]] && source "$root/tools/nix-dev-env.sh" >/dev/null
unset DISPLAY WAYLAND_DISPLAY SSH_AUTH_SOCK
scratch="$root/.scratch/profile"
mkdir -p "$scratch"

# host_dll [jit|r2r]: path of the published host, publishing it first when missing.
host_dll() {
  local flavour="${1:-jit}"
  local dll="$scratch/host-$flavour/cscode-profile.dll"
  if [[ ! -f "$dll" ]]; then
    if [[ "$flavour" == r2r ]]; then "$root/tools/profile/publish-host.sh" --r2r >&2; else "$root/tools/profile/publish-host.sh" >&2; fi
  fi
  echo "$dll"
}

# dotnet_tool NAME: path of a diagnostics tool (dotnet-counters, dotnet-gcdump, dotnet-trace, dotnet-dump), fetched when missing.
dotnet_tool() {
  local name="$1"
  local path
  path="$("$root/tools/profile/fetch-dotnet-tools.sh" --print "$name")"
  echo "$path"
}
