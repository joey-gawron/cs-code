#!/usr/bin/env bash
# Installs the REAL language servers used by the CsCode.Lsp integration tests into .scratch/tools/lsp/ (git-ignored).
# Nothing is installed globally. Idempotent: re-running only does work that is missing.
#
#   tools/fetch-lsp-tools.sh                  # typescript-language-server + typescript (npm)
#   tools/fetch-lsp-tools.sh --with-csharp    # additionally csharp-ls (dotnet tool, needs the .NET SDK + network)
#
# The tests look for the binaries in {repo}/.scratch/tools/lsp/node_modules/.bin (npm) and {repo}/.scratch/tools/lsp (dotnet tool)
# and SKIP with an explicit reason when they are missing, so running this script is what turns the skips into real runs.
set -euo pipefail

TS_LS_VERSION="${TS_LS_VERSION:-4.3.3}"
TYPESCRIPT_VERSION="${TYPESCRIPT_VERSION:-5.6.3}"
CSHARP_LS_VERSION="${CSHARP_LS_VERSION:-}"   # empty = latest

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$root/.scratch/tools/lsp"
mkdir -p "$dest"

with_csharp=0
for arg in "$@"; do
  case "$arg" in
    --with-csharp) with_csharp=1 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

if ! command -v npm >/dev/null 2>&1; then
  echo "fetch-lsp-tools: npm not found; cannot install typescript-language-server (the TypeScript integration tests will skip)" >&2
else
  bin="$dest/node_modules/.bin/typescript-language-server"
  tsc="$dest/node_modules/typescript/lib/tsserver.js"
  installed_ls=""
  [[ -f "$dest/node_modules/typescript-language-server/package.json" ]] && installed_ls="$(node -p "require('$dest/node_modules/typescript-language-server/package.json').version" 2>/dev/null || true)"
  installed_ts=""
  [[ -f "$dest/node_modules/typescript/package.json" ]] && installed_ts="$(node -p "require('$dest/node_modules/typescript/package.json').version" 2>/dev/null || true)"
  if [[ -x "$bin" && -f "$tsc" && "$installed_ls" == "$TS_LS_VERSION" && "$installed_ts" == "$TYPESCRIPT_VERSION" ]]; then
    echo "typescript-language-server $installed_ls + typescript $installed_ts already installed"
  else
    echo "installing typescript-language-server@$TS_LS_VERSION + typescript@$TYPESCRIPT_VERSION into $dest"
    [[ -f "$dest/package.json" ]] || echo '{"name":"cscode-lsp-tools","private":true,"version":"0.0.0"}' > "$dest/package.json"
    npm install --prefix "$dest" --no-audit --no-fund --loglevel=error "typescript-language-server@$TS_LS_VERSION" "typescript@$TYPESCRIPT_VERSION"
  fi
fi

if [[ "$with_csharp" == 1 ]]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "fetch-lsp-tools: dotnet not found; cannot install csharp-ls" >&2
  elif [[ -x "$dest/csharp-ls" ]]; then
    echo "csharp-ls already installed"
  else
    echo "installing csharp-ls into $dest"
    if [[ -n "$CSHARP_LS_VERSION" ]]; then
      dotnet tool install csharp-ls --tool-path "$dest" --version "$CSHARP_LS_VERSION"
    else
      dotnet tool install csharp-ls --tool-path "$dest"
    fi
  fi
fi

echo "done. tools in: $dest"
