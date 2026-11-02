#!/usr/bin/env bash
# Fetches the Zig toolchain (ziglang.org, MIT) and the matching zls language server (github.com/zigtools/zls, MIT) into
# .scratch/tools/zig/<version>/ for the Zig tests and local development:
#
#   tools/fetch-zig.sh                    # pinned version, this machine's platform, skipped when already present
#   tools/fetch-zig.sh --force            # download and extract again
#   ZIG_DEST=/some/dir tools/fetch-zig.sh # install under /some/dir/<version>/ instead
#
# zls must match zig's version (zls refuses to work with, or misreads, another zig's standard library), so both are pinned to the same
# release. The SHA-256 of every archive is PINNED (zig's from ziglang.org/download/index.json, zls's from the "digest" GitHub publishes for
# the release assets, both checked 2026-11-01): an archive is refused unless its digest matches, and its entries are listed and checked
# (no absolute paths, no '..') before anything is extracted. Nothing inside the archives is run by this script.
# Result: <dest>/<version>/zig/zig (with its lib/ beside it) and <dest>/<version>/zls/zls. CsCode finds them through
# cscode.lsp.searchDirs (zls) and zig.path (zig); the tests find them in this directory on their own.
set -euo pipefail

version="0.17.0"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${ZIG_DEST:-${root}/.scratch/tools/zig}/${version}"
force=0
[[ "${1:-}" == "--force" ]] && force=1

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)              zig_platform="x86_64-linux"  ;;
  Linux-aarch64|Linux-arm64) zig_platform="aarch64-linux" ;;
  Darwin-x86_64)             zig_platform="x86_64-macos"  ;;
  Darwin-arm64)              zig_platform="aarch64-macos" ;;
  *) echo "fetch-zig: no pinned Zig ${version} package for $(uname -s)/$(uname -m)" >&2; exit 2 ;;
esac

# Pinned: size and SHA-256 of each platform's archives of ${version}.
case "$zig_platform" in
  x86_64-linux)  zig_size=57332648; zig_sha="1cbe9df9f27e6b78d14ccbca43b6703a404ef79ef1c463de901d7f088d4e2026"
                 zls_size=4070752;  zls_sha="dfa4514e1868803fdf2ae56832c88b9d400094d70ea6bb136b57bcc91af10258" ;;
  aarch64-linux) zig_size=52877280; zig_sha="9e8d11661d4ae3bd57702a3832781e23ad151dde5798e16a5ccd503f65234ff8"
                 zls_size=3832280;  zls_sha="0d314f3cab30da770328e1b8c84ab89bb308e56dbc26a85ac82429ff02b44873" ;;
  x86_64-macos)  zig_size=59317572; zig_sha="4f9a1c5269aa17ebda5e6d3c2b89d6cbf36f7d2b22a0306e9ab98f25f95529c6"
                 zls_size=1214848;  zls_sha="0801481ebf0027620db6729968c069401c7b80f376d35f9f91f480d72c393523" ;;
  aarch64-macos) zig_size=53985220; zig_sha="b607e9b9234790a008116ae5bdb71c6243b84b9fb42a53a9e70fde41c06c536a"
                 zls_size=1045676;  zls_sha="2e451358b4f7e2d4b1fccb65db177dbb321c5a98e95f67e09aa72d6ecfb39d20" ;;
esac
zig_asset="zig-${zig_platform}-${version}.tar.xz"
zig_url="https://ziglang.org/download/${version}/${zig_asset}"
zls_asset="zls-${zig_platform}.tar.xz"
zls_url="https://github.com/zigtools/zls/releases/download/${version}/${zls_asset}"

if [[ -x "$dest/zig/zig" && -x "$dest/zls/zls" && $force -eq 0 ]]; then
  echo "Zig ${version} and zls ${version} already present: ${dest}"
  exit 0
fi

command -v curl >/dev/null || { echo "fetch-zig: curl is required" >&2; exit 2; }
command -v tar >/dev/null || { echo "fetch-zig: tar is required" >&2; exit 2; }
mkdir -p "$(dirname "$dest")"
tmp="$(mktemp -d "$(dirname "$dest")/.fetch-zig.XXXXXX")" # beside the destination: the final move is a rename, not a copy
trap 'rm -rf "$tmp"' EXIT

# fetch <url> <file> <size> <sha256>: downloads and refuses anything whose digest does not match.
fetch() {
  echo "Downloading $(basename "$2") (${version}, $3 bytes) ..."
  curl -fsSL -A "cscode-fetch" --max-filesize $(($3 * 2)) "$1" -o "$2"
  local actual
  # Lower-cased with tr, not ${actual,,}: macOS ships bash 3.2.
  actual="$( (sha256sum "$2" 2>/dev/null || shasum -a 256 "$2") | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
  if [[ "$actual" != "$4" ]]; then
    echo "fetch-zig: SHA-256 mismatch for $(basename "$2") (got ${actual}, expected $4) - refusing to extract" >&2
    exit 1
  fi
  echo "SHA-256 verified: ${actual}"
}

# extract <archive> <dir>: lists first and rejects absolute paths and '..' components before unpacking anything.
extract() {
  if tar -tJf "$1" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-zig: $(basename "$1") contains unsafe paths" >&2; exit 1; fi
  mkdir -p "$2"
  tar -xJf "$1" -C "$2"
}

fetch "$zig_url" "$tmp/$zig_asset" "$zig_size" "$zig_sha"
fetch "$zls_url" "$tmp/$zls_asset" "$zls_size" "$zls_sha"

extract "$tmp/$zig_asset" "$tmp/zigx"
extract "$tmp/$zls_asset" "$tmp/zlsx"
# zig's archive holds one top-level directory (zig-<platform>-<version>/) with the zig binary and its lib/; zls's holds the binary.
zig_dir="$tmp/zigx/zig-${zig_platform}-${version}"
[[ -f "$zig_dir/zig" ]] || { echo "fetch-zig: ${zig_asset} does not contain zig-${zig_platform}-${version}/zig" >&2; exit 1; }
[[ -f "$tmp/zlsx/zls" ]] || { echo "fetch-zig: ${zls_asset} does not contain zls" >&2; exit 1; }
chmod u+x "$zig_dir/zig" "$tmp/zlsx/zls"

rm -rf "$dest"
mkdir -p "$dest"
mv "$zig_dir" "$dest/zig"
mv "$tmp/zlsx" "$dest/zls"
echo "$version" > "$dest/VERSION"
echo "Installed Zig ${version} -> ${dest}/zig/zig and zls ${version} -> ${dest}/zls/zls"
