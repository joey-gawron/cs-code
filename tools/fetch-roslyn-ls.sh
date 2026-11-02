#!/usr/bin/env bash
# Fetches the Roslyn language server (Microsoft.CodeAnalysis.LanguageServer, MIT, github.com/dotnet/roslyn) into
# .scratch/tools/roslyn-ls/<version>/ for the C# tests, the csharp-ls/Roslyn evaluation and local development:
#
#   tools/fetch-roslyn-ls.sh                     # pinned version, this machine's runtime identifier, skipped when already present
#   tools/fetch-roslyn-ls.sh --force             # extract again
#   ROSLYN_LS_ARCHIVE=/path/roslyn-language-server.osx-arm64.<v>.nupkg tools/fetch-roslyn-ls.sh   # a package you already have: NO download
#   ROSLYN_LS_DEST=/some/dir tools/fetch-roslyn-ls.sh                                             # install under /some/dir/<version>/
#   ROSLYN_LS_RID=linux-x64 tools/fetch-roslyn-ls.sh                                              # another runtime identifier's package
#
# Source: nuget.org's `roslyn-language-server` .NET tool, which Microsoft publishes from dotnet/roslyn (commit d82b7f7 for this version)
# as one small pointer package plus one package per runtime identifier (`roslyn-language-server.<rid>`, package type
# DotnetToolRidPackage). The VS Code C# extension downloads the same server from Microsoft's vs-impl Azure DevOps feed instead. This script
# fetches the RID package directly (no `dotnet tool install`, nothing global), checks its size and SHA-512 against the values pinned below
# (nuget.org's published package hash, checked 2026-11-01), lists its entries and rejects absolute paths, '..' and links before extracting
# anything. Nothing inside the package is run. Result: <dest>/<version>/Microsoft.CodeAnalysis.LanguageServer.dll, which CsCode starts with
# `dotnet` (the package's apphost is not used, so one install works for every runtime that can load it).
set -euo pipefail

version="5.13.0-1.26514.10"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${ROSLYN_LS_DEST:-${root}/.scratch/tools/roslyn-ls}/${version}"
force=0
[[ "${1:-}" == "--force" ]] && force=1

rid="${ROSLYN_LS_RID:-}"
if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)               rid="linux-x64" ;;
    Linux-aarch64|Linux-arm64)  rid="linux-arm64" ;;
    Darwin-x86_64)              rid="osx-x64" ;;
    Darwin-arm64)               rid="osx-arm64" ;;
    MINGW*|MSYS*|CYGWIN*)       rid="win-x64" ;;
    *) echo "fetch-roslyn-ls: no Roslyn language server package for $(uname -s)/$(uname -m)" >&2; exit 2 ;;
  esac
fi

# Pinned: size and SHA-512 (hex) of each runtime identifier's package of ${version}.
case "$rid" in
  osx-arm64)      size=63391176; sha512="d896f52bfcc40fffd53d5ad940c93d4dd6312803d5a5815d416c91e6ab819309437ce4df9a6bce254a6fca49d29f9804bce843c54682f09bf0da606197648eb3" ;;
  osx-x64)        size=66880259; sha512="2adb19a6fbeaeedf0a53497ec8565a0e15f1dcc1270028d36a7ec8e1efd0c42e07235eb9b2988054fdd33b5741fc46fbadf6133d0f3dadb0e73a23b4f13032c8" ;;
  linux-x64)      size=66639284; sha512="8621029398f0b4aaa7f11bc296fdf9e6a24bff00a24f9cf50a35b36bff7dc202898f4a19fddae5283f0b55843bcca33780f542e2d1576c0f927d0ecfcd09733f" ;;
  linux-arm64)    size=63250046; sha512="923f3b5e5ebf72d68bef498cfaaed7dc51ee2787e3acad25af51fdbac6b6100c45fdab0cf12c9adc6997888704c4213f8aa84d91c3af60a2615dcea085dbed37" ;;
  linux-musl-x64) size=66631777; sha512="b2da780b95ae87016fb1136813a61cf806001b31cb55158852952e6e2772afa2133efa2c90dd0d52bd366ea043487a34085177642ff6d6e914adf416e7b47303" ;;
  win-x64)        size=66766070; sha512="f9b13d802b6eb27951d1256515d7dcd2b7a6ba906f90d7fccb634a369ec2ac20c47db64c8027ee70433243f739e81c71995cbc9ca0f19d8a4be4a9f388b5a8d5" ;;
  win-arm64)      size=64246274; sha512="cd977e2a563edd34bcc5777d54d599d03466bad0ff2da17210e0e01eb881a3bccb5849e2c9054252a16a0a624fa000f32a480b0f1137acbc9645a1bad0caaa9f" ;;
  *) echo "fetch-roslyn-ls: unknown runtime identifier '${rid}'" >&2; exit 2 ;;
esac
package="roslyn-language-server.${rid}"
asset="${package}.${version}.nupkg"
url="https://api.nuget.org/v3-flatcontainer/${package}/${version}/${asset}"
server="${dest}/Microsoft.CodeAnalysis.LanguageServer.dll"

if [[ -f "$server" && $force -eq 0 && -f "$dest/RID" && "$(cat "$dest/RID")" == "$rid" ]]; then
  echo "Roslyn language server ${version} (${rid}) already present: ${server}"
  exit 0
fi

command -v unzip >/dev/null || { echo "fetch-roslyn-ls: unzip is required" >&2; exit 2; }
mkdir -p "$(dirname "$dest")"
tmp="$(mktemp -d "$(dirname "$dest")/.fetch-roslyn-ls.XXXXXX")" # beside the destination: the final move is a rename
trap 'rm -rf "$tmp"' EXIT

if [[ -n "${ROSLYN_LS_ARCHIVE:-}" ]]; then
  [[ -f "$ROSLYN_LS_ARCHIVE" ]] || { echo "fetch-roslyn-ls: ROSLYN_LS_ARCHIVE '${ROSLYN_LS_ARCHIVE}' does not exist" >&2; exit 2; }
  echo "Using the local package ${ROSLYN_LS_ARCHIVE} (no download)"
  cp "$ROSLYN_LS_ARCHIVE" "$tmp/$asset"
else
  command -v curl >/dev/null || { echo "fetch-roslyn-ls: curl is required (or set ROSLYN_LS_ARCHIVE)" >&2; exit 2; }
  echo "Downloading ${asset} (${size} bytes) ..."
  curl -fsSL -A "cscode-fetch" --max-filesize $((size * 2)) "$url" -o "$tmp/$asset"
fi

actual_size="$(wc -c < "$tmp/$asset" | tr -d ' ')"
actual="$( (sha512sum "$tmp/$asset" 2>/dev/null || shasum -a 512 "$tmp/$asset") | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
if [[ "$actual" != "${sha512}" ]]; then
  echo "fetch-roslyn-ls: SHA-512 mismatch (got ${actual}, expected ${sha512}) - refusing to extract" >&2
  exit 1
fi
echo "SHA-512 verified (${actual_size} bytes, expected ${size})"

# Safe extraction: reject absolute paths, '..' components (zip-slip), backslashes and links before unpacking anything. A .NET tool keeps
# its files under tools/<tfm>/<rid>/; only that tree is kept.
if unzip -Z1 "$tmp/$asset" | grep -Eq '(^/|(^|/)\.\.(/|$)|\\)'; then echo "fetch-roslyn-ls: package contains unsafe paths" >&2; exit 1; fi
if unzip -Zl "$tmp/$asset" | grep -Eq '^l'; then echo "fetch-roslyn-ls: package contains links" >&2; exit 1; fi
mkdir -p "$tmp/x"
unzip -q "$tmp/$asset" 'tools/*' -d "$tmp/x"
dll="$(cd "$tmp/x" && find tools -name Microsoft.CodeAnalysis.LanguageServer.dll -type f | head -n 1)"
[[ -n "$dll" ]] || { echo "fetch-roslyn-ls: package does not contain Microsoft.CodeAnalysis.LanguageServer.dll" >&2; exit 1; }

rm -rf "$dest"
mkdir -p "$(dirname "$dest")"
mv "$tmp/x/$(dirname "$dll")" "$dest"
echo "$version" > "$dest/VERSION"
echo "$rid" > "$dest/RID"
echo "Installed the Roslyn language server ${version} (${rid}) -> ${server}"
