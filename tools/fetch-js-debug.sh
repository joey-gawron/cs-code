#!/usr/bin/env bash
# Fetches Microsoft's standalone JavaScript debug adapter (js-debug-dap, MIT, github.com/microsoft/vscode-js-debug) into
# .scratch/tools/js-debug/<version>/ for the Node.js/TypeScript debugging tests and local development:
#
#   tools/fetch-js-debug.sh                     # pinned version, skipped when already present
#   tools/fetch-js-debug.sh --force             # extract again
#   JSDEBUG_ARCHIVE=/path/js-debug-dap-v1.140.0.tar.gz tools/fetch-js-debug.sh   # use an archive you already have: NO download
#   JSDEBUG_DEST=/some/dir tools/fetch-js-debug.sh                                # install under /some/dir/<version>/ instead
#
# The version and the archive's SHA-256 are PINNED (the same values as CsCode.Dap's JsDebugInstaller, which the app uses after asking the
# user): the archive is refused unless its digest matches, and its entries are listed and checked (no absolute paths, no '..', no links)
# before anything is extracted. Result: <dest>/<version>/js-debug-dap-<version>.tar.gz and <dest>/<version>/js-debug/src/dapDebugServer.js.
# Run the adapter only as `node <dest>/<version>/js-debug/src/dapDebugServer.js <port> 127.0.0.1`.
set -euo pipefail

version="v1.140.0"
sha256="27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968"
size=1249709
asset="js-debug-dap-${version}.tar.gz"
url="https://github.com/microsoft/vscode-js-debug/releases/download/${version}/${asset}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${JSDEBUG_DEST:-${root}/.scratch/tools/js-debug}/${version}"
force=0
[[ "${1:-}" == "--force" ]] && force=1

server="${dest}/js-debug/src/dapDebugServer.js"
if [[ -f "$server" && $force -eq 0 ]]; then
  echo "js-debug ${version} already present: ${dest}/js-debug"
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [[ -n "${JSDEBUG_ARCHIVE:-}" ]]; then
  [[ -f "$JSDEBUG_ARCHIVE" ]] || { echo "fetch-js-debug: JSDEBUG_ARCHIVE '${JSDEBUG_ARCHIVE}' does not exist" >&2; exit 2; }
  echo "Using the local archive ${JSDEBUG_ARCHIVE} (no download)"
  cp "$JSDEBUG_ARCHIVE" "$tmp/$asset"
else
  command -v curl >/dev/null || { echo "fetch-js-debug: curl is required (or set JSDEBUG_ARCHIVE)" >&2; exit 2; }
  echo "Downloading ${asset} ..."
  curl -fsSL -A "cscode-fetch" "$url" -o "$tmp/$asset"
fi

actual="$( (sha256sum "$tmp/$asset" 2>/dev/null || shasum -a 256 "$tmp/$asset") | awk '{print $1}')"
if [[ "${actual,,}" != "${sha256}" ]]; then
  echo "fetch-js-debug: SHA-256 mismatch (got ${actual}, expected ${sha256}) - refusing to extract" >&2
  exit 1
fi
echo "SHA-256 verified: ${actual} ($(wc -c < "$tmp/$asset" | tr -d ' ') bytes, expected ${size})"

# Safe extraction: list first and reject absolute paths, '..' components (tar-slip) and any link entry before unpacking anything.
if tar -tzf "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-js-debug: archive contains unsafe paths" >&2; exit 1; fi
if tar -tzvf "$tmp/$asset" | grep -Eq '^[lhcbp]'; then echo "fetch-js-debug: archive contains links or special files" >&2; exit 1; fi
mkdir -p "$tmp/x"
tar -xzf "$tmp/$asset" -C "$tmp/x" --no-same-owner --no-same-permissions
[[ -f "$tmp/x/js-debug/src/dapDebugServer.js" ]] || { echo "fetch-js-debug: archive does not contain js-debug/src/dapDebugServer.js" >&2; exit 1; }

rm -rf "$dest"
mkdir -p "$dest"
mv "$tmp/x/js-debug" "$dest/js-debug"
mv "$tmp/$asset" "$dest/$asset"
echo "$version" > "$dest/VERSION"
echo "Installed js-debug ${version} -> ${dest}/js-debug (server: ${server})"
