#!/usr/bin/env bash
# Installs the .NET diagnostics tools the profiling playbook uses (docs/MEMORY.md) — dotnet-counters, dotnet-gcdump, dotnet-trace, dotnet-dump —
# from nuget.org at a PINNED version, each into its own directory under the repository's shared, git-ignored downloads folder:
#   {repo}/.scratch/downloads/<tool>/<version>/          (main checkout's .scratch: shared by every worktree, kept after worktrees go)
# The .nupkg lands in the normal NuGet cache (~/.nuget/packages); its SHA-512 is checked against the digest pinned below, which is nuget.org's
# published packageHash for that version (catalog entry), and its size and SHA-256 are printed. Nothing global is installed (no `-g`),
# no install script is run. Idempotent: a tool already present with a matching package digest is reused.
#
#   tools/profile/fetch-dotnet-tools.sh                 # all four
#   tools/profile/fetch-dotnet-tools.sh --print NAME    # (for the other scripts) fetch NAME if needed, print its executable path
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
worktree_root="$(cd "$here/../.." && pwd)"
# The shared downloads folder lives in the MAIN checkout (git rev-parse --git-common-dir points at it from any worktree).
common="$(git -C "$worktree_root" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || echo "$worktree_root/.git")"
main_root="$(dirname "$common")"
downloads="$main_root/.scratch/downloads"
# shellcheck disable=SC1091
[[ -d /nix/store ]] && source "$worktree_root/tools/nix-dev-env.sh" >/dev/null

VERSION="10.0.750501"   # published 2026-10-07 on nuget.org (all four tools share the diagnostics release number)
declare -A SHA512=(      # nuget.org catalog packageHash (SHA512, base64) of each .nupkg at $VERSION
  [dotnet-counters]="J11umV/f3cgabxfJYbPIl+60T3UXPblkNYbeJeIfcgXa2g4eWlzeYH/l15nkDIJF83HGNA4YoHaGUJSi9I9ATQ=="
  [dotnet-gcdump]="U4OLsZtZakYPU068ImxNSm8Jtg98riK805k52A9J2qlsgjXmdJNzNLiy46jfWAszg4fjMwr8nNsagXk+nH+Ukg=="
  [dotnet-trace]="bHcFw2upDrhkR920Se8V0fgX19kLLA0+Chn/NDWiLfiyCkA4/b6zlPtroBJJPCLjU1GWbyynuwR3qxuFehun2w=="
  [dotnet-dump]="tfl36NHJ3zgTiefn6IzF9ezKnp83dBh9Q3ST8AuzoH0C9aoR3yar7UQzO1R3FxjuVt32vj+ChCDpJfPmgJDdcw=="
)

# dotnet tool install --tool-path restores into the tool's own store; the NuGet cache is the fallback.
nupkg_of() {
  local store="$downloads/$1/$VERSION/.store/$1/$VERSION/$1/$VERSION/$1.$VERSION.nupkg"
  if [[ -f "$store" ]]; then echo "$store"; else echo "$HOME/.nuget/packages/$1/$VERSION/$1.$VERSION.nupkg"; fi
}

verify() {
  local tool="$1" pkg
  pkg="$(nupkg_of "$tool")"
  [[ -f "$pkg" ]] || { echo "fetch-dotnet-tools: $pkg missing after install" >&2; return 1; }
  local got
  got="$(openssl dgst -sha512 -binary "$pkg" | base64 -w0)"
  if [[ "$got" != "${SHA512[$tool]}" ]]; then
    echo "fetch-dotnet-tools: $tool $VERSION digest mismatch: got $got, pinned ${SHA512[$tool]}" >&2
    return 1
  fi
}

fetch() {
  local tool="$1" dir="$downloads/$1/$VERSION"
  if [[ -x "$dir/$tool" ]] && verify "$tool" 2>/dev/null; then return 0; fi
  if [[ -d "$dir" && -n "$(ls -A "$dir" 2>/dev/null)" ]]; then
    echo "fetch-dotnet-tools: $dir exists but does not hold a verified $tool; remove it to fetch again" >&2
    return 1
  fi
  mkdir -p "$dir"
  dotnet tool install "$tool" --version "$VERSION" --tool-path "$dir" --configfile "$worktree_root/nuget.config" >&2
  verify "$tool"
  local pkg
  pkg="$(nupkg_of "$tool")"
  echo "fetched $tool $VERSION: $(stat -c %s "$pkg") bytes, sha256 $(sha256sum "$pkg" | cut -d' ' -f1), into $dir" >&2
}

if [[ "${1:-}" == "--print" ]]; then
  fetch "$2"
  echo "$downloads/$2/$VERSION/$2"
  exit 0
fi
for tool in dotnet-counters dotnet-gcdump dotnet-trace dotnet-dump; do fetch "$tool"; done
