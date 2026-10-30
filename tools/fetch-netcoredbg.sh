#!/usr/bin/env bash
# Fetches Samsung's netcoredbg (MIT) into .scratch/tools/netcoredbg/ for the DAP integration tests and local development.
#
#   tools/fetch-netcoredbg.sh            # the pinned release; skipped when that release is already there
#   tools/fetch-netcoredbg.sh --force    # re-download
#   NETCOREDBG_ARCHIVE=/path/netcoredbg-linux-amd64.tar.gz tools/fetch-netcoredbg.sh   # a local copy of the pinned asset (no download)
#   NETCOREDBG_TAG=<tag> NETCOREDBG_SHA256=<hex> tools/fetch-netcoredbg.sh   # another release: its asset's SHA-256 must be given with it
#
# Pinned: release 3.2.0-1092 (published 2026-06-25) and each platform asset's size and SHA-256 (the digests GitHub publishes for the release
# assets; the linux-amd64 one was also checked against a download, 2026-10-30); on an Intel Mac, release 3.1.3-1062, the last with an
# osx-amd64 build. Runs on macOS's bash 3.2 and BSD tools. The archive must match the pin before anything is extracted;
# extraction refuses absolute paths, '..' components, links and special files. NOT vsdbg (its licence forbids use outside Microsoft products).
# The app itself does the same job at runtime through CsCode.Dap's NetCoreDbgInstaller (after asking the user), with the same pins.
set -euo pipefail

repo="Samsung/netcoredbg"
pinned_tag="3.2.0-1092"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${root}/.scratch/tools/netcoredbg"
force=0
[[ "${1:-}" == "--force" ]] && force=1

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)              asset="netcoredbg-linux-amd64.tar.gz"; size=3510717; pinned_sha256="080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa" ;;
  Linux-aarch64|Linux-arm64) asset="netcoredbg-linux-arm64.tar.gz"; size=3424271; pinned_sha256="065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb" ;;
  Darwin-arm64)              asset="netcoredbg-osx-arm64.zip";      size=3389504; pinned_sha256="f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea" ;;
  # Intel Macs: 3.2.0-1092 has no osx-amd64 build; its last one is 3.1.3-1062 (checked against a download, 2026-10-30; as NetCoreDbgInstaller pins).
  Darwin-x86_64)             asset="netcoredbg-osx-amd64.tar.gz";   size=3479468; pinned_sha256="49459b066836b6a452f418501d7ecab57bcd7e60d8464faac21ff70b496b8634"
                             pinned_tag="3.1.3-1062"
                             echo "fetch-netcoredbg: Samsung no longer builds netcoredbg for Intel Macs; using its last Intel build, ${pinned_tag}" >&2 ;;
  MINGW*|MSYS*|CYGWIN*)      asset="netcoredbg-win64.zip";          size=3524161; pinned_sha256="3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a" ;;
  *) echo "fetch-netcoredbg: Samsung publishes no netcoredbg build for $(uname -s)/$(uname -m); build it from source (github.com/${repo}) and set cscode.debug.netcoredbgPath" >&2; exit 2 ;;
esac

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }  # not ${x,,}: macOS ships bash 3.2

tag="${NETCOREDBG_TAG:-$pinned_tag}"
sha256="$pinned_sha256"
if [[ "$tag" != "$pinned_tag" ]]; then
  if [[ -z "${NETCOREDBG_SHA256:-}" ]]; then
    echo "fetch-netcoredbg: ${tag} is not the pinned release (${pinned_tag}); give its ${asset}'s SHA-256 as NETCOREDBG_SHA256 too" >&2
    exit 2
  fi
  sha256="$NETCOREDBG_SHA256"
  size=""
fi
sha256="$(lower "$sha256")"

exe="${dest}/netcoredbg"
[[ "$asset" == *win64* ]] && exe="${dest}/netcoredbg.exe"

if [[ -x "$exe" && $force -eq 0 && "$(cat "$dest/VERSION" 2>/dev/null || true)" == "$tag" ]]; then
  echo "netcoredbg ${tag} already present: $exe"
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [[ -n "${NETCOREDBG_ARCHIVE:-}" ]]; then
  [[ -f "$NETCOREDBG_ARCHIVE" ]] || { echo "fetch-netcoredbg: NETCOREDBG_ARCHIVE '${NETCOREDBG_ARCHIVE}' does not exist" >&2; exit 2; }
  echo "Using the local archive ${NETCOREDBG_ARCHIVE} (no download)"
  cp "$NETCOREDBG_ARCHIVE" "$tmp/$asset"
else
  command -v curl >/dev/null || { echo "fetch-netcoredbg: curl is required (or set NETCOREDBG_ARCHIVE)" >&2; exit 2; }
  echo "Downloading ${asset} (${tag}) ..."
  curl -fsSL -A "cscode-fetch" "https://github.com/${repo}/releases/download/${tag}/${asset}" -o "$tmp/$asset"
fi

actual="$(lower "$( (sha256sum "$tmp/$asset" 2>/dev/null || shasum -a 256 "$tmp/$asset") | awk '{print $1}')")"
if [[ "$actual" != "$sha256" ]]; then
  echo "fetch-netcoredbg: SHA-256 mismatch (got ${actual}, expected ${sha256}) - refusing to extract" >&2
  exit 1
fi
got_size="$(wc -c < "$tmp/$asset" | tr -d ' ')"
if [[ -n "$size" && "$got_size" != "$size" ]]; then
  echo "fetch-netcoredbg: size mismatch (got ${got_size} bytes, expected ${size})" >&2
  exit 1
fi
echo "SHA-256 verified: ${actual} (${got_size} bytes)"

# Safe extraction: list first and reject absolute paths, '..' components (tar/zip-slip) and any link or special file before unpacking anything.
if [[ "$asset" == *.tar.gz ]]; then
  if tar -tzf "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-netcoredbg: archive contains unsafe paths" >&2; exit 1; fi
  if tar -tzvf "$tmp/$asset" | grep -Eq '^[lhcbp]'; then echo "fetch-netcoredbg: archive contains links or special files" >&2; exit 1; fi
  mkdir -p "$tmp/x"; tar -xzf "$tmp/$asset" -C "$tmp/x" --no-same-owner --no-same-permissions
else
  if unzip -Z1 "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-netcoredbg: archive contains unsafe paths" >&2; exit 1; fi
  if unzip -Z "$tmp/$asset" | grep -Eq '^l'; then echo "fetch-netcoredbg: archive contains links" >&2; exit 1; fi
  mkdir -p "$tmp/x"; unzip -q "$tmp/$asset" -d "$tmp/x"
fi

src="$tmp/x"; [[ -d "$tmp/x/netcoredbg" ]] && src="$tmp/x/netcoredbg"
[[ -e "$src/$(basename "$exe")" ]] || { echo "fetch-netcoredbg: archive does not contain $(basename "$exe")" >&2; exit 1; }

rm -rf "$dest"
mkdir -p "$(dirname "$dest")"
mv "$src" "$dest"
chmod +x "$exe" 2>/dev/null || true
echo "$tag" > "$dest/VERSION"
echo "Installed netcoredbg ${tag} -> ${exe}"
