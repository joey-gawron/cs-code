#!/usr/bin/env bash
# Fetches CodeLLDB (Vadim Chugunov's LLDB-based debug adapter, MIT, github.com/vadimcn/codelldb) with its bundled LLDB into
# .scratch/tools/codelldb/<version>/ for the Rust debugging tests and local development:
#
#   tools/fetch-codelldb.sh                     # pinned version, this machine's platform, skipped when already present
#   tools/fetch-codelldb.sh --force             # extract again
#   CODELLDB_ARCHIVE=/path/codelldb-linux-x64.vsix tools/fetch-codelldb.sh   # use a package you already have: NO download
#   CODELLDB_DEST=/some/dir tools/fetch-codelldb.sh                          # install under /some/dir/<version>/ instead
#   CODELLDB_PLATFORM=linux-arm64 tools/fetch-codelldb.sh                    # another platform's package (darwin-*, win32-x64…)
#
# The version and each platform package's SHA-256 are PINNED (the same values as CsCode.Dap's CodeLldbInstaller, which the app uses after
# asking the user; the digests are the ones GitHub publishes for the v1.12.3 release assets): the package is refused unless its digest
# matches, and its entries are listed and checked (no absolute paths, no '..', no links) before anything is extracted. Nothing inside the
# package is ever run by this script. Result: <dest>/<version>/codelldb-<platform>.vsix and <dest>/<version>/extension/adapter/codelldb.
# The package is a VS Code extension (.vsix, a zip); CsCode runs only its standalone adapter:
#   <dest>/<version>/extension/adapter/codelldb [--port N | --connect N]   (stdio without either; liblldb from extension/lldb/lib)
set -euo pipefail

version="v1.12.3"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${CODELLDB_DEST:-${root}/.scratch/tools/codelldb}/${version}"
force=0
[[ "${1:-}" == "--force" ]] && force=1

platform="${CODELLDB_PLATFORM:-}"
if [[ -z "$platform" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)               platform="linux-x64" ;;
    Linux-aarch64|Linux-arm64)  platform="linux-arm64" ;;
    Linux-armv7l)               platform="linux-armhf" ;;
    Darwin-x86_64)              platform="darwin-x64" ;;
    Darwin-arm64)               platform="darwin-arm64" ;;
    MINGW*|MSYS*|CYGWIN*)       platform="win32-x64" ;;
    *) echo "fetch-codelldb: CodeLLDB has no package for $(uname -s)/$(uname -m)" >&2; exit 2 ;;
  esac
fi

# Pinned: size and SHA-256 of each platform package of ${version} (GitHub's published "digest", checked 2026-10-29).
case "$platform" in
  linux-x64)    size=55571094; sha256="1cd7f386598022b51a5b93b9ffa23e812b23f519cfe1833384ec4bef4bfd1be1" ;;
  linux-arm64)  size=54544514; sha256="0887f67d440554617894266f80706b700907c36b95e6e49d23b95a0e05318101" ;;
  linux-armhf)  size=52535623; sha256="20294f0ea876a73c662bb09b1814551a859dc77156564b62c75acf1088ecaf30" ;;
  darwin-x64)   size=47735596; sha256="e25cc716b94c62c07fec268ff2785d2b797245b160502baef8b9c970a0c4d8e8" ;;
  darwin-arm64) size=45390048; sha256="2f114a990e1b368dd1dbd33c80c0e719767af2d228391ec0df0571c957f9ac91" ;;
  win32-x64)    size=50943475; sha256="a916e509308dac817732f63ca604a8b93ed29cd16f38a2fa9f0b64ed58e8f51a" ;;
  *) echo "fetch-codelldb: unknown platform '${platform}'" >&2; exit 2 ;;
esac
asset="codelldb-${platform}.vsix"
url="https://github.com/vadimcn/codelldb/releases/download/${version}/${asset}"
adapter="${dest}/extension/adapter/codelldb"
[[ "$platform" == win32-* ]] && adapter="${adapter}.exe"

if [[ -f "$adapter" && $force -eq 0 && -f "$dest/PLATFORM" && "$(cat "$dest/PLATFORM")" == "$platform" ]]; then
  echo "CodeLLDB ${version} (${platform}) already present: ${adapter}"
  exit 0
fi

command -v unzip >/dev/null || { echo "fetch-codelldb: unzip is required" >&2; exit 2; }
mkdir -p "$(dirname "$dest")"
tmp="$(mktemp -d "$(dirname "$dest")/.fetch-codelldb.XXXXXX")" # beside the destination: the final move is a rename, not a 170 MB copy
trap 'rm -rf "$tmp"' EXIT

if [[ -n "${CODELLDB_ARCHIVE:-}" ]]; then
  [[ -f "$CODELLDB_ARCHIVE" ]] || { echo "fetch-codelldb: CODELLDB_ARCHIVE '${CODELLDB_ARCHIVE}' does not exist" >&2; exit 2; }
  echo "Using the local package ${CODELLDB_ARCHIVE} (no download)"
  cp "$CODELLDB_ARCHIVE" "$tmp/$asset"
else
  command -v curl >/dev/null || { echo "fetch-codelldb: curl is required (or set CODELLDB_ARCHIVE)" >&2; exit 2; }
  echo "Downloading ${asset} (${version}, ${size} bytes) ..."
  curl -fsSL -A "cscode-fetch" --max-filesize $((size * 2)) "$url" -o "$tmp/$asset"
fi

actual_size="$(wc -c < "$tmp/$asset" | tr -d ' ')"
actual="$( (sha256sum "$tmp/$asset" 2>/dev/null || shasum -a 256 "$tmp/$asset") | awk '{print $1}')"
if [[ "${actual,,}" != "${sha256}" ]]; then
  echo "fetch-codelldb: SHA-256 mismatch (got ${actual}, expected ${sha256}) - refusing to extract" >&2
  exit 1
fi
echo "SHA-256 verified: ${actual} (${actual_size} bytes, expected ${size})"

# Safe extraction: list first and reject absolute paths, '..' components (zip-slip) and links (a zip entry whose Unix mode is a symlink)
# before unpacking anything; only the extension/ tree is extracted (the .vsix manifest files are not needed).
if unzip -Z1 "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$)|\\)'; then echo "fetch-codelldb: package contains unsafe paths" >&2; exit 1; fi
if unzip -Zl "$tmp/$asset" | grep -Eq '^l'; then echo "fetch-codelldb: package contains links" >&2; exit 1; fi
mkdir -p "$tmp/x"
unzip -q "$tmp/$asset" 'extension/*' -d "$tmp/x"
[[ -f "$tmp/x/${adapter#"$dest"/}" ]] || { echo "fetch-codelldb: package does not contain ${adapter#"$dest"/}" >&2; exit 1; }
chmod u+x "$tmp/x/${adapter#"$dest"/}"

rm -rf "$dest"
mkdir -p "$dest"
mv "$tmp/x/extension" "$dest/extension"
mv "$tmp/$asset" "$dest/$asset"
echo "$version" > "$dest/VERSION"
echo "$platform" > "$dest/PLATFORM"
echo "Installed CodeLLDB ${version} (${platform}) -> ${dest} (adapter: ${adapter})"
