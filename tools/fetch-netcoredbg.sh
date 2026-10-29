#!/usr/bin/env bash
# Fetches Samsung's netcoredbg (MIT) into .scratch/tools/netcoredbg/ for the DAP integration tests and local development.
#
#   tools/fetch-netcoredbg.sh            # latest release, skipped if already present
#   tools/fetch-netcoredbg.sh --force    # re-download
#   NETCOREDBG_TAG=3.2.0-1092 tools/fetch-netcoredbg.sh   # pin a release tag
#
# The archive's SHA-256 is verified against the digest GitHub publishes for the release asset (API field "digest") before it is
# extracted; extraction refuses entries that would land outside the target directory. NOT vsdbg (its licence forbids use outside
# Microsoft products). The app itself does the same job at runtime through CsCode.Dap's NetCoreDbgInstaller (after asking the user).
set -euo pipefail

repo="Samsung/netcoredbg"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${root}/.scratch/tools/netcoredbg"
force=0
[[ "${1:-}" == "--force" ]] && force=1

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)         asset="netcoredbg-linux-amd64.tar.gz" ;;
  Linux-aarch64|Linux-arm64) asset="netcoredbg-linux-arm64.tar.gz" ;;
  Darwin-arm64)         asset="netcoredbg-osx-arm64.zip" ;;
  MINGW*|MSYS*|CYGWIN*) asset="netcoredbg-win64.zip" ;;
  *) echo "fetch-netcoredbg: no netcoredbg release build for $(uname -s)/$(uname -m)" >&2; exit 2 ;;
esac

exe="${dest}/netcoredbg"
[[ "$asset" == *.zip && "$asset" == *win64* ]] && exe="${dest}/netcoredbg.exe"

if [[ -x "$exe" && $force -eq 0 && -z "${NETCOREDBG_TAG:-}" ]]; then
  echo "netcoredbg already present: $exe"
  exit 0
fi

command -v curl >/dev/null || { echo "fetch-netcoredbg: curl is required" >&2; exit 2; }
api="https://api.github.com/repos/${repo}/releases/latest"
[[ -n "${NETCOREDBG_TAG:-}" ]] && api="https://api.github.com/repos/${repo}/releases/tags/${NETCOREDBG_TAG}"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Looking up ${asset} ..."
curl -fsSL -H "Accept: application/vnd.github+json" -A "cscode-fetch" "$api" -o "$tmp/release.json"

# Pull tag + the asset's digest out of the JSON without needing jq. GitHub answers with compact OR pretty-printed JSON depending on
# headers, so split into one field per line first; each asset object lists "name" before its "digest".
tr ',' '\n' < "$tmp/release.json" > "$tmp/release.lines"
tag="$(sed -n 's/^ *{\? *"tag_name": *"\([^"]*\)".*/\1/p' "$tmp/release.lines" | head -1)"
digest="$(grep -A40 "\"name\": *\"${asset}\"" "$tmp/release.lines" | grep -m1 '"digest": *"sha256:' | sed 's/.*"sha256:\([0-9a-fA-F]*\)".*/\1/' || true)"
url="https://github.com/${repo}/releases/download/${tag}/${asset}"
[[ -n "$tag" ]] || { echo "fetch-netcoredbg: could not read the release tag" >&2; exit 1; }

echo "Downloading ${asset} (${tag}) ..."
curl -fsSL -A "cscode-fetch" "$url" -o "$tmp/$asset"

if [[ -n "$digest" ]]; then
  actual="$( (sha256sum "$tmp/$asset" 2>/dev/null || shasum -a 256 "$tmp/$asset") | awk '{print $1}')"
  if [[ "${actual,,}" != "${digest,,}" ]]; then
    echo "fetch-netcoredbg: SHA-256 mismatch (got ${actual}, expected ${digest}) - refusing to extract" >&2
    exit 1
  fi
  echo "SHA-256 verified: ${actual}"
else
  echo "fetch-netcoredbg: warning - GitHub published no digest for ${asset}; relying on archive integrity only" >&2
fi

# Safe extraction: list first and reject absolute paths / '..' components (zip-slip) before unpacking anything.
if [[ "$asset" == *.tar.gz ]]; then
  if tar -tzf "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-netcoredbg: archive contains unsafe paths" >&2; exit 1; fi
  mkdir -p "$tmp/x"; tar -xzf "$tmp/$asset" -C "$tmp/x" --no-same-owner
else
  if unzip -Z1 "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-netcoredbg: archive contains unsafe paths" >&2; exit 1; fi
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
