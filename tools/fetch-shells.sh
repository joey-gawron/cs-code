#!/usr/bin/env bash
# Fetches the shells the terminal's shell-integration tests run besides bash (tests/CsCode.Terminal.Tests/ShellIntegrationTests.cs):
# fish and PowerShell (pwsh), from their official GitHub releases. zsh publishes no Linux release build (its tests are skipped without a zsh).
#
#   tools/fetch-shells.sh                  # both, pinned versions, skipped when already present with the right digest
#   tools/fetch-shells.sh fish             # one of them
#   CSCODE_DOWNLOADS=/dir tools/fetch-shells.sh
#
# Versions, sizes and SHA-256 digests are PINNED (the digests are the ones GitHub publishes for the release assets; PowerShell also lists them
# in the release's hashes.sha256): an archive is refused unless its digest matches. Each version goes into its own directory,
# <downloads>/<name>/<version>/: the archive there, its contents in extracted/ (entries are listed and checked first: no absolute paths, no
# '..', links only relative and inside). Nothing is installed: the tests point at the extracted binaries (CSCODE_FISH / CSCODE_PWSH, else
# these default places), and no downloaded script is ever run.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
downloads="${CSCODE_DOWNLOADS:-${root}/.scratch/downloads}"

fetch() {
  local name=$1 version=$2 asset=$3 url=$4 sha256=$5 size=$6 binary=$7
  local dest="${downloads}/${name}/${version}"
  if [[ -f "${dest}/${asset}" ]] && [[ "$(sha256sum "${dest}/${asset}" | awk '{print $1}')" == "${sha256}" ]] && [[ -x "${dest}/extracted/${binary}" ]]; then
    echo "${name} ${version} already present: ${dest}/extracted/${binary}"
    return 0
  fi
  if [[ -e "$dest" ]] && [[ -n "$(ls -A "$dest" 2>/dev/null)" ]]; then
    echo "fetch-shells: ${dest} exists and is not a verified copy; remove it to fetch again" >&2
    return 1
  fi
  mkdir -p "$dest"
  echo "Downloading ${url} ..."
  curl -fsSL -A "cscode-fetch" "$url" -o "${dest}/${asset}.part"
  local actual
  actual="$(sha256sum "${dest}/${asset}.part" | awk '{print $1}')"
  if [[ "${actual}" != "${sha256}" ]]; then
    echo "fetch-shells: SHA-256 mismatch for ${asset} (got ${actual}, expected ${sha256}) - refusing it" >&2
    rm -f "${dest}/${asset}.part"
    return 1
  fi
  mv "${dest}/${asset}.part" "${dest}/${asset}"
  echo "SHA-256 verified: ${actual} ($(wc -c < "${dest}/${asset}" | tr -d ' ') bytes, expected ${size})"

  local list
  list="$(tar -tvf "${dest}/${asset}")"
  if tar -tf "${dest}/${asset}" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-shells: ${asset} contains unsafe paths" >&2; return 1; fi
  if grep -Eq '^[hcbp]' <<<"$list"; then echo "fetch-shells: ${asset} contains hard links or special files" >&2; return 1; fi
  if grep -E '^l' <<<"$list" | grep -Eq -- '-> (/|.*(^|/)\.\.(/|$))'; then echo "fetch-shells: ${asset} contains links leaving the archive" >&2; return 1; fi
  mkdir -p "${dest}/extracted"
  tar -xf "${dest}/${asset}" -C "${dest}/extracted" --no-same-owner
  [[ -f "${dest}/extracted/${binary}" ]] || { echo "fetch-shells: ${asset} has no ${binary}" >&2; return 1; }
  chmod u+x "${dest}/extracted/${binary}"
  echo "${name} ${version}: ${dest}/extracted/${binary}"
}

want="${1:-all}"
if [[ "$want" == all || "$want" == fish ]]; then
  # fish 4.9.3 (2026-09-08), static Linux build
  fetch fish 4.9.3 fish-4.9.3-linux-x86_64.tar.xz \
    https://github.com/fish-shell/fish-shell/releases/download/4.9.3/fish-4.9.3-linux-x86_64.tar.xz \
    8f643d10ad1abb0ef7072764c01ceb5cf61b2d373fda400cf60f0bad69b7e095 3217572 fish
fi
if [[ "$want" == all || "$want" == pwsh ]]; then
  # PowerShell 7.6.7 (2026-10-13), linux-x64
  fetch pwsh 7.6.7 powershell-7.6.7-linux-x64.tar.gz \
    https://github.com/PowerShell/PowerShell/releases/download/v7.6.7/powershell-7.6.7-linux-x64.tar.gz \
    6896f9e3a6b0a354bf470835a96da76d2f801cd50dcf047ad7c4563bf93bf431 75913191 pwsh
fi
